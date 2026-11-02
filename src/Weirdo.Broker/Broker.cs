using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Weirdo.Core;
using Weirdo.Core.Services;

namespace Weirdo.BrokerHost;

internal sealed class Broker : IDisposable
{
    private const string ProtectionName = "Weirdo.Windows.SessionRequests.v1";
    private static readonly byte[] SessionEntropy = Encoding.UTF8.GetBytes(ProtectionName);
    private static readonly byte[] LegacySessionEntropy = Encoding.UTF8.GetBytes("Kooky.Windows.SessionRequests.v1");
    private readonly TerminalSessionManager terminals = new();
    private readonly StateStore state = new(AppPaths.Data);
    private readonly AgentCatalog agents = new(AppPaths.Data);
    private readonly WorkspaceService workspace = new();
    private readonly ConcurrentDictionary<Guid, Peer> peers = new();
    private readonly ConcurrentDictionary<string, SavedSession> sessions = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object saveLock = new();
    private readonly object mutationLock = new();
    private readonly string[] migrationWarnings;
    private bool preparingUpdate;

    private Broker(string[] migrationWarnings) => this.migrationWarnings = migrationWarnings;

    public static async Task RunAsync()
    {
        // Closing the owned mutex handle releases singleton lifetime without a
        // thread-affine ReleaseMutex call after an async continuation.
        using var singleton = new Mutex(true, AppPaths.Pipe + "-broker", out var owner);
        if (!owner) return;
        string[] migrationWarnings = [];
        if (Path.TrimEndingDirectorySeparator(AppPaths.Data).Equals(Path.TrimEndingDirectorySeparator(AppPaths.DefaultData), StringComparison.OrdinalIgnoreCase))
        {
            var legacyData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kooky");
            if (Directory.Exists(legacyData))
                migrationWarnings = MigrationService.Migrate(legacyData, AppPaths.Data).Warnings;
        }
        using var broker = new Broker(migrationWarnings);
        broker.Restore();
        broker.terminals.Output += chunk => broker.Broadcast("terminal.output", Node(chunk, BrokerJsonContext.Default.TerminalChunk));
        broker.terminals.Exited += broker.OnTerminalExit;
        _ = broker.DiscoveryLoop();
        try
        {
            while (!broker.stop.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        AppPaths.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
                    await server.WaitForConnectionAsync(broker.stop.Token);
                    _ = broker.Accept(server);
                }
                catch (IOException error)
                {
                    server?.Dispose();
                    AppPaths.Log(error);
                    await Task.Delay(1000, broker.stop.Token);
                }
                catch { server?.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppPaths.Log(error); }
    }

    private static JsonNode? Node<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToNode(value, typeInfo);

    private SessionInfo[] List()
    {
        var live = terminals.List().ToDictionary(session => session.Id);
        return sessions.Values.Select(saved => live.GetValueOrDefault(saved.Info.Id, saved.Info))
            .OrderBy(session => session.StartedAt).ToArray();
    }

    private void OnTerminalExit(SessionInfo info)
    {
        lock (mutationLock)
        {
            if (sessions.TryGetValue(info.Id, out var saved)) sessions[info.Id] = saved with { Info = info };
        }
        Persist();
        Broadcast("terminal.exit", Node(info, BrokerJsonContext.Default.SessionInfo));
        BroadcastSessions();
    }

    private void BroadcastSessions() => Broadcast("sessions.changed", Node(List(), BrokerJsonContext.Default.SessionInfoArray));

    private async Task<JsonNode?> Dispatch(string type, JsonElement payload, Guid peerId)
    {
        string String(string key) => payload.GetProperty(key).GetString() ?? "";
        switch (type)
        {
            case "bootstrap":
                return Node(new BootstrapResult(
                    state.Load("ui"), agents.List(), List(), AppInfo.Version,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.ProcessId,
                    Environment.OSVersion.Version.Build, state.RecoveryWarnings.Concat(migrationWarnings).ToArray()), BrokerJsonContext.Default.BootstrapResult);
            case "state.save":
                state.Save("ui", payload);
                Broadcast("state.changed", new JsonObject { ["state"] = JsonNode.Parse(payload.GetRawText()) }, peerId);
                return null;
            case "session.list":
                return Node(List(), BrokerJsonContext.Default.SessionInfoArray);
            case "session.create":
                lock (mutationLock)
                {
                    var request = payload.Deserialize(BrokerJsonContext.Default.SessionRequest)
                        ?? throw new ArgumentException("A session request is required.");
                    return Node(Create(request), BrokerJsonContext.Default.SessionInfo);
                }
            case "session.resume":
                lock (mutationLock)
                {
                    if (!sessions.TryGetValue(String("sessionId"), out var saved))
                        throw new InvalidOperationException("Session no longer exists.");
                    var running = List().FirstOrDefault(session => session.Id == saved.Info.Id && session.Running);
                    if (running is not null) return Node(running, BrokerJsonContext.Default.SessionInfo);
                    var resumed = Create(saved.Request with
                    {
                        NativeSessionId = saved.Info.NativeSessionId,
                        Title = saved.Info.Title,
                        Cwd = saved.Info.Cwd,
                        Cols = saved.Info.Cols,
                        Rows = saved.Info.Rows
                    });
                    sessions.TryRemove(saved.Info.Id, out _);
                    if (terminals.List().Any(session => session.Id == saved.Info.Id)) terminals.Forget(saved.Info.Id);
                    Persist();
                    BroadcastSessions();
                    return Node(resumed, BrokerJsonContext.Default.SessionInfo);
                }
            case "session.input":
                terminals.Write(String("sessionId"), String("data"));
                return null;
            case "session.resize":
                terminals.Resize(String("sessionId"), payload.GetProperty("cols").GetInt32(), payload.GetProperty("rows").GetInt32());
                return null;
            case "session.replay":
                var replayId = String("sessionId");
                var replay = terminals.List().Any(session => session.Id == replayId)
                    ? terminals.Replay(replayId) : new TerminalReplay(replayId, 0, "", false);
                return Node(replay, BrokerJsonContext.Default.TerminalReplay);
            case "session.close":
                lock (mutationLock)
                {
                    var closeId = String("sessionId");
                    sessions.TryRemove(closeId, out _);
                    if (terminals.List().Any(session => session.Id == closeId)) terminals.Forget(closeId);
                    Persist();
                    BroadcastSessions();
                    return null;
                }
            case "session.move":
                var moveId = String("sessionId");
                var workspaceId = String("workspaceId");
                if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("Workspace ID is required.");
                lock (mutationLock)
                {
                    if (sessions.TryGetValue(moveId, out var moving))
                    {
                        moving.Info.WorkspaceId = workspaceId;
                        sessions[moveId] = moving with { Request = moving.Request with { WorkspaceId = workspaceId } };
                    }
                    if (terminals.List().Any(session => session.Id == moveId)) terminals.Move(moveId, workspaceId);
                    Persist();
                }
                BroadcastSessions();
                return null;
            case "session.rename":
                var renameId = String("sessionId");
                var title = String("title").Trim();
                if (title.Length is < 1 or > 120) throw new ArgumentException("Title must contain 1–120 characters.");
                lock (mutationLock)
                {
                    if (sessions.TryGetValue(renameId, out var renamed))
                    {
                        renamed.Info.Title = title;
                        sessions[renameId] = renamed with { Request = renamed.Request with { Title = title } };
                    }
                    if (terminals.List().Any(session => session.Id == renameId)) terminals.Rename(renameId, title);
                    Persist();
                }
                BroadcastSessions();
                return null;
            case "agents.save":
                agents.Save(payload.GetProperty("agents").Deserialize(BrokerJsonContext.Default.ListAgentProfile)
                    ?? throw new ArgumentException("Agent profiles are required."));
                var updatedAgents = Node(agents.List(), BrokerJsonContext.Default.ListAgentProfile);
                Broadcast("agents.changed", updatedAgents?.DeepClone());
                return updatedAgents;
            case "agents.history":
                var history = await Task.Run(() => agents.History(String("agentId"), String("cwd")));
                return Node(history, BrokerJsonContext.Default.ListNativeSession);
            case "agents.models":
                return Node(agents.Models(String("agentId")), CoreJsonContext.Default.AgentModelChoices);
            case "agents.models.sync":
                return Node(await agents.SyncModelsAsync(String("agentId")), CoreJsonContext.Default.AgentModelSyncResult);
            case "workspace.inspect":
                return Node(await workspace.InspectAsync(String("path")), BrokerJsonContext.Default.WorkspaceInfo);
            case "workspace.resolve":
                return Node(await workspace.ResolvePathAsync(String("path")), CoreJsonContext.Default.WorkspaceLocation);
            case "workspace.files":
                return Node(await workspace.FilesAsync(String("root"), String("path")), BrokerJsonContext.Default.ListFileEntry);
            case "performance":
                var hostPid = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("hostPid", out var host) ? host.GetInt32() : 0;
                return Node(await PerformanceSampler.SampleAsync(Environment.ProcessId, hostPid, List()), BrokerJsonContext.Default.ResourceMeasurements);
            case "updates.configure":
                return UpdateService.Configure(String("feedUrl"));
            case "updates.check":
                return await UpdateService.CheckAsync();
            case "updates.install":
                return await UpdateService.InstallAsync(payload.GetProperty("hostPid").GetInt32(), PrepareUpdate, CancelUpdate, Shutdown);
            case "broker.prepareUpdate":
                await PrepareUpdate();
                return null;
            case "broker.cancelUpdate":
                await CancelUpdate();
                return null;
            case "broker.shutdown":
                await Shutdown();
                return null;
            default:
                throw new ArgumentException($"Unknown service command: {type}");
        }
    }

    private Task PrepareUpdate()
    {
        lock (mutationLock)
        {
            if (preparingUpdate) throw new InvalidOperationException("An update is already being prepared.");
            if (List().Any(session => session.Running)) throw new InvalidOperationException("A terminal is running. Stop all terminals before updating.");
            preparingUpdate = true;
        }
        return Task.CompletedTask;
    }

    private Task CancelUpdate()
    {
        lock (mutationLock) preparingUpdate = false;
        return Task.CompletedTask;
    }

    private Task Shutdown()
    {
        lock (mutationLock) preparingUpdate = true;
        Broadcast("broker.stopping", new JsonObject());
        _ = Task.Run(async () => { await Task.Delay(350); stop.Cancel(); });
        return Task.CompletedTask;
    }

    private SessionInfo Create(SessionRequest request)
    {
        if (preparingUpdate) throw new InvalidOperationException("Weirdo is preparing an update or shutting down. New terminals are temporarily disabled.");
        if (List().Count(session => session.Running) >= 64) throw new InvalidOperationException("Close a terminal before creating more (64 maximum).");
        request = request with { TrackingId = Guid.NewGuid().ToString("N") };
        var launch = agents.Resolve(request);
        var profile = agents.List().FirstOrDefault(agent => agent.Id == request.AgentId);
        request = request with { Title = request.Title ?? profile?.Name ?? "Terminal" };
        var info = terminals.Create(request, launch);
        sessions[info.Id] = new(info, request with { NativeSessionId = launch.NativeSessionId });
        Persist();
        BroadcastSessions();
        return info;
    }

    private async Task DiscoveryLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                var changed = false;
                foreach (var live in List())
                {
                    if (sessions.TryGetValue(live.Id, out var current) &&
                        (current.Info.Cwd != live.Cwd || current.Info.Cols != live.Cols || current.Info.Rows != live.Rows))
                    {
                        sessions[live.Id] = current with { Info = live };
                        changed = true;
                    }
                }
                foreach (var info in List().Where(session => session.Running && session.NativeSessionId is null && session.AgentId != "shell"))
                {
                    try
                    {
                        var nativeId = agents.DiscoverNativeSession(info);
                        if (nativeId is null) continue;
                        if (terminals.List().Any(session => session.Id == info.Id)) terminals.UpdateNativeSessionId(info.Id, nativeId);
                        if (sessions.TryGetValue(info.Id, out var saved))
                        {
                            saved.Info.NativeSessionId = nativeId;
                            changed = true;
                        }
                    }
                    catch (Exception error) { AppPaths.Log(error); }
                }
                if (changed) Persist();
                BroadcastSessions();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Persist()
    {
        lock (saveLock)
        {
            try
            {
                var live = List().ToDictionary(session => session.Id);
                var archive = sessions.Values.Select(saved => saved with { Info = live.GetValueOrDefault(saved.Info.Id, saved.Info) }).ToArray();
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(archive, BrokerJsonContext.Default.SavedSessionArray);
                try
                {
                    var encrypted = Convert.ToBase64String(ProtectedData.Protect(plaintext, SessionEntropy, DataProtectionScope.CurrentUser));
                    state.Save("sessions", new ProtectedSessionState(encrypted, ProtectionName), BrokerJsonContext.Default.ProtectedSessionState);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            catch (Exception error) { AppPaths.Log(error); }
        }
    }

    private void Restore()
    {
        try
        {
            var saved = state.Load("sessions");
            if (saved is null) return;
            var ciphertext = Convert.FromBase64String(saved.Value.GetProperty("encrypted").GetString()!);
            var plaintext = UnprotectSessionArchive(ciphertext);
            try
            {
                foreach (var session in JsonSerializer.Deserialize(plaintext, BrokerJsonContext.Default.SavedSessionArray) ?? [])
                {
                    session.Info.Running = false;
                    session.Info.ProcessId = 0;
                    sessions[session.Info.Id] = session;
                }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception error) { AppPaths.Log(error); }
    }

    private static byte[] UnprotectSessionArchive(byte[] ciphertext)
    {
        try { return ProtectedData.Unprotect(ciphertext, SessionEntropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            try { return ProtectedData.Unprotect(ciphertext, LegacySessionEntropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                // Original Kooky 0.1.x archives used no entropy.
                return ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            }
        }
    }

    private void Broadcast(string type, JsonNode? payload, Guid? except = null)
    {
        var json = new JsonObject { ["type"] = type, ["payload"] = payload }.ToJsonString();
        foreach (var (id, peer) in peers)
            if (id != except && !peer.Queue.Writer.TryWrite(json)) peer.Stop.Cancel();
    }

    private sealed class Peer(NamedPipeServerStream pipe) : IDisposable
    {
        public readonly NamedPipeServerStream Pipe = pipe;
        public readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        public readonly CancellationTokenSource Stop = new();
        public void Dispose()
        {
            Stop.Cancel();
            Queue.Writer.TryComplete();
            Pipe.Dispose();
        }
    }

    private async Task Accept(NamedPipeServerStream pipe)
    {
        var peerId = Guid.NewGuid();
        using var peer = new Peer(pipe);
        peers[peerId] = peer;
        var send = Task.Run(async () =>
        {
            try
            {
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
                await foreach (var line in peer.Queue.Reader.ReadAllAsync(peer.Stop.Token))
                    await writer.WriteLineAsync(line.AsMemory(), peer.Stop.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { peer.Stop.Cancel(); }
        });
        try
        {
            var running = new List<Task>();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, true);
            while (!peer.Stop.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(peer.Stop.Token);
                if (line is null) break;
                if (line.Length > 8_000_000) throw new IOException("IPC request exceeds the limit.");
                running.RemoveAll(task => task.IsCompleted);
                if (running.Count >= 64) await Task.WhenAny(running);
                using var envelope = JsonDocument.Parse(line);
                var kind = envelope.RootElement.GetProperty("type").GetString();
                // Preserve input and mutation arrival order. Disk and process probes
                // run independently so typing is responsive during inspection.
                if (kind is "session.input" or "session.resize" or "session.close" or "session.move" or "session.rename" or "state.save" or "agents.save")
                    await HandleRequest(peer, line, peerId);
                else
                    running.Add(Task.Run(() => HandleRequest(peer, line, peerId)));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { AppPaths.Log(error); }
        finally
        {
            peers.TryRemove(peerId, out _);
            peer.Stop.Cancel();
            await send;
        }
    }

    private async Task HandleRequest(Peer peer, string line, Guid peerId)
    {
        string? requestId = null;
        JsonObject response;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            requestId = request.GetProperty("id").GetString();
            var type = request.GetProperty("type").GetString()!;
            var result = await Dispatch(type, request.TryGetProperty("payload", out var payload) ? payload : default, peerId);
            response = new JsonObject { ["id"] = requestId, ["ok"] = true, ["result"] = result };
        }
        catch (Exception error)
        {
            response = new JsonObject { ["id"] = requestId, ["ok"] = false, ["error"] = error.Message };
        }
        try { await peer.Queue.Writer.WriteAsync(response.ToJsonString(), peer.Stop.Token); }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        Persist();
        terminals.Dispose();
        foreach (var peer in peers.Values) peer.Dispose();
    }
}
