using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

if (args.Contains("--ordered-worker"))
{
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine("ORDER_WORKER_READY");
    while (Console.ReadLine() is { } line)
        Console.WriteLine("ORDER_HASH=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))));
    return;
}

var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var appArgument=Array.IndexOf(args,"--app-dir");
var source = appArgument>=0 && appArgument+1<args.Length ? Path.GetFullPath(args[appArgument+1]) : Path.Combine(project, "src/Weirdo.Broker/bin/Debug/net10.0-windows/win-x64");
var evidence = Path.Combine(project, "tests/Weirdo.Broker.Tests/artifacts", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
var snapshot = Path.Combine(evidence, "app");
var data = Path.GetFullPath(Path.Combine(evidence, "data"));
Directory.CreateDirectory(data);
foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var target = Path.Combine(snapshot, Path.GetRelativePath(source, file));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(file, target, true);
}
var executable = Path.Combine(snapshot, "Weirdo.Broker.exe");
var identity = WindowsIdentity.GetCurrent().User!.Value;
var pipeName = "weirdo-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + data.ToUpperInvariant())))[..24];
var results = new List<object>();
var failures = 0;
Process StartBroker()
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    info.ArgumentList.Add("--broker"); info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data);
    return Process.Start(info)!;
}
async Task<PipeRpc> Connect()
{
    var rpc = new PipeRpc(pipeName);
    await rpc.Connect();
    return rpc;
}
async Task<string> Diagnostic(params string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.ArgumentList.Add("--data-dir");
    start.ArgumentList.Add(data);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    return await output;
}
void Record(string name, bool passed, object? detail = null)
{
    results.Add(new { name, passed, detail });
    if (!passed) failures++;
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
}
async Task<JsonElement[]> Sessions(PipeRpc rpc) => (await rpc.Call("session.list")).EnumerateArray().ToArray();
async Task WaitExit(PipeRpc rpc, string id)
{
    for (var i = 0; i < 100; i++)
    {
        if (!(await Sessions(rpc)).Single(s => s.GetProperty("id").GetString() == id).GetProperty("running").GetBoolean()) return;
        await Task.Delay(50);
    }
    throw new TimeoutException("Session did not exit.");
}
Process? broker = null;
try
{
    using (var identityReply = JsonDocument.Parse(await Diagnostic("--identity")))
    {
        Record("Headless identity command resolves the exact named pipe and profile",
            identityReply.RootElement.GetProperty("pipe").GetString() == pipeName && identityReply.RootElement.GetProperty("data").GetString() == data);
    }
    var expectedVersion = System.Xml.Linq.XDocument.Load(Path.Combine(project, "src/Weirdo.Broker/Weirdo.Broker.csproj"))
        .Descendants("Version").Single().Value;
    Record("Native sidecar reports product version", (await Diagnostic("--version")).Trim() == expectedVersion);
    broker = StartBroker();
    using var rpc = await Connect();
    using (var second = StartBroker())
    {
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var boot = await rpc.Call("bootstrap");
        Record("Second broker exits; original retains singleton", boot.GetProperty("brokerPid").GetInt32() == broker.Id);
    }
    using (var diagnosticBootstrap = JsonDocument.Parse(await Diagnostic("--rpc", "bootstrap")))
        Record("Console RPC diagnostics attach to the same broker", diagnosticBootstrap.RootElement.GetProperty("ok").GetBoolean()
            && diagnosticBootstrap.RootElement.GetProperty("result").GetProperty("brokerPid").GetInt32() == broker.Id);

    var initialBootstrap = await rpc.Call("bootstrap");
    Record("An isolated profile does not import legacy user state", initialBootstrap.GetProperty("state").ValueKind == JsonValueKind.Null
        && !File.Exists(Path.Combine(data, "migration-previous-v1.json")));
    var customProfile = JsonSerializer.SerializeToElement(new
    {
        id = "aot-fixture", name = "AOT fixture", command = "powershell.exe", kind = "custom",
        env = new Dictionary<string, string> { ["WEIRDO_TEST_ENV"] = "aot-local-fixture" }
    });
    var profiles = initialBootstrap.GetProperty("agents").EnumerateArray().Append(customProfile).ToArray();
    await rpc.Call("agents.save", new { agents = profiles });
    Record("Agent settings use DPAPI with source-generated serialization", !File.ReadAllText(Path.Combine(data, "agents.json")).Contains("aot-local-fixture"));
    var models = await rpc.Call("agents.models", new { agentId = "claude" });
    Record("Agent model choices serialize under the headless host", models.GetProperty("suggestions").ValueKind == JsonValueKind.Array);
    await rpc.Call("state.save", new { workspaces = new[] { new { id = "broker-test", path = project } } });
    var inspection = await rpc.Call("workspace.inspect", new { path = project });
    var location = await rpc.Call("workspace.resolve", new { path = "\"" + project + "\"" });
    var normalizedProject = Path.TrimEndingDirectorySeparator(project);
    Record("Workspace location resolves without the inspection payload", location.GetProperty("path").GetString() == normalizedProject
        && location.GetProperty("name").GetString() == Path.GetFileName(normalizedProject) && !location.TryGetProperty("git", out _));
    var files = await rpc.Call("workspace.files", new { root = project, path = project });
    Record("Workspace inspection and file listing serialize", inspection.GetProperty("path").GetString() == project
        && files.EnumerateArray().Any(file => file.GetProperty("name").GetString() == "Weirdo.slnx"));
    var update = await rpc.Call("updates.check");
    Record("Headless update status serializes", !update.GetProperty("configured").GetBoolean());
    var session = await rpc.Call("session.create", new { workspaceId = "broker-test", cwd = project, agentId = "shell", cols = 100, rows = 30 });
    var id = session.GetProperty("id").GetString()!;
    var pid = session.GetProperty("processId").GetInt32();
    var blockedUpdate=false;
    try{await rpc.Call("broker.prepareUpdate");}catch(InvalidOperationException){blockedUpdate=true;}
    Record("Update preparation preserves running terminal and refuses installation",blockedUpdate&&IsAlive(pid));
    await rpc.Call("session.input", new { sessionId = id, data = "Write-Output 'BROKER_READY'\r" });
    for (var i = 0; i < 80; i++)
    {
        if ((await rpc.Call("session.replay", new { sessionId = id })).GetProperty("data").GetString()!.Contains("BROKER_READY")) break;
        await Task.Delay(50);
    }
    using (var reattached = await Connect())
    {
        var existing = (await Sessions(reattached)).Single(s => s.GetProperty("id").GetString() == id);
        Record("Another UI connection sees the same live terminal PID", existing.GetProperty("processId").GetInt32() == pid && existing.GetProperty("running").GetBoolean(), new { pid });
    }
    await rpc.Call("session.input", new { sessionId = id, data = "exit\r" });
    await WaitExit(rpc, id);
    await rpc.Call("broker.prepareUpdate");
    var launchBlocked=false;
    try{await rpc.Call("session.create",new{workspaceId="update-race",cwd=project,agentId="shell"});}catch(InvalidOperationException){launchBlocked=true;}
    Record("Prepared update atomically blocks new terminal launches",launchBlocked&&!(await Sessions(rpc)).Any(s=>s.GetProperty("running").GetBoolean()));
    await rpc.Call("broker.cancelUpdate");
    using var resumePeer = await Connect();
    async Task<JsonElement?> TryResume(PipeRpc connection)
    {
        try { return await connection.Call("session.resume", new { sessionId = id }); }
        catch (InvalidOperationException) { return null; }
    }
    var resumeResults = await Task.WhenAll(TryResume(rpc), TryResume(resumePeer));
    resumePeer.Dispose();
    var resumed = resumeResults.First(value => value.HasValue)!.Value;
    var resumedId = resumed.GetProperty("id").GetString()!;
    var after = await Sessions(rpc);
    Record("Two clients cannot duplicate the same resumed session", after.Count(s => s.GetProperty("running").GetBoolean()) == 1,
        new { successfulResumeRequests = resumeResults.Count(value => value.HasValue), runningCount = after.Count(s => s.GetProperty("running").GetBoolean()) });
    foreach (var extra in after.Where(s => s.GetProperty("id").GetString() != resumedId))
        await rpc.Call("session.close", new { sessionId = extra.GetProperty("id").GetString() });
    after = await Sessions(rpc);
    Record("Resume replaces its previous broker record", after.Length == 1 && after[0].GetProperty("id").GetString() == resumedId,
        new { oldId = id, resumedId, recordCount = after.Length, oldRecordStillPresent = after.Any(s => s.GetProperty("id").GetString() == id) });

    // Demonstrate that an independent slow RPC serializes unrelated input on this connection.
    var perfRequest = await rpc.Send("performance", new { });
    var sw = Stopwatch.StartNew();
    var inputRequest = await rpc.Send("session.input", new { sessionId = resumedId, data = "Write-Output 'AFTER_PERFORMANCE'\r" });
    await rpc.Receive(perfRequest);
    await rpc.Receive(inputRequest);
    var inputLatency = rpc.ResponseLatencyMilliseconds(inputRequest);
    Record("Input can bypass unrelated in-flight RPC", inputLatency < 500, new { responseLatencyMs = inputLatency });

    var ordered = await rpc.Call("session.create", new { workspaceId = "ordered-test", cwd = project, agentId = "shell", command = "\"" + Environment.ProcessPath + "\" --ordered-worker", cols = 200, rows = 30 });
    var orderedId = ordered.GetProperty("id").GetString()!;
    for (var i = 0; i < 100; i++)
    {
        if ((await rpc.Call("session.replay", new { sessionId = orderedId })).GetProperty("data").GetString()!.Contains("ORDER_WORKER_READY")) break;
        await Task.Delay(30);
    }
    var inputLine = string.Concat(Enumerable.Range(0, 100).Select(i => (char)('A' + i % 26)));
    var inputRequests = new List<string>();
    foreach (var c in inputLine) inputRequests.Add(await rpc.Send("session.input", new { sessionId = orderedId, data = c.ToString() }));
    inputRequests.Add(await rpc.Send("session.input", new { sessionId = orderedId, data = "\r" }));
    foreach (var request in inputRequests) await rpc.Receive(request);
    var expectedHash = "ORDER_HASH=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputLine)));
    var orderedOutput = "";
    for (var i = 0; i < 100; i++)
    {
        orderedOutput = (await rpc.Call("session.replay", new { sessionId = orderedId })).GetProperty("data").GetString()!;
        if (orderedOutput.Contains("ORDER_HASH=")) break;
        await Task.Delay(30);
    }
    Record("Pipelined keyboard fragments retain wire order", orderedOutput.Contains(expectedHash), new { fragments = inputRequests.Count });
    await rpc.Call("session.close", new { sessionId = orderedId });

    await rpc.Call("session.input", new { sessionId = resumedId, data = "Set-Location '" + Path.Combine(project, "src").Replace("'", "''") + "'\r" });
    string? reported = null;
    for (var i = 0; i < 80; i++)
    {
        reported = (await Sessions(rpc)).Single(s => s.GetProperty("id").GetString() == resumedId).GetProperty("cwd").GetString();
        if (Path.TrimEndingDirectorySeparator(reported!) == Path.Combine(project, "src")) break;
        await Task.Delay(50);
    }
    Record("Live cwd tracks explicit shell directory changes", Path.TrimEndingDirectorySeparator(reported!) == Path.Combine(project, "src"), new { reported });
    await rpc.Call("session.input", new { sessionId = resumedId, data = "exit\r" });
    await WaitExit(rpc, resumedId);
    var next = await rpc.Call("session.resume", new { sessionId = resumedId });
    Record("Restarted shell uses its last known cwd", Path.TrimEndingDirectorySeparator(next.GetProperty("cwd").GetString()!) == Path.Combine(project, "src"), new { actual = next.GetProperty("cwd").GetString(), expected = Path.Combine(project, "src") });
    var additionalClients = new List<PipeRpc>();
    try
    {
        for (var i = 0; i < 31; i++) additionalClients.Add(await Connect());
        await Task.Delay(500);
        Record("Connection limit does not kill active terminal service", !broker.HasExited, new { connectionCount = additionalClients.Count + 1, brokerExited = broker.HasExited, terminalSurvives = IsAlive(next.GetProperty("processId").GetInt32()) });
    }
    finally { foreach (var peer in additionalClients) peer.Dispose(); }
    if (!broker.HasExited)
    {
        await rpc.Call("broker.shutdown");
        await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Record("Broker shutdown finishes and terminates owned live terminal", !IsAlive(next.GetProperty("processId").GetInt32()));
    }
    broker.Dispose(); broker = null;
    broker = StartBroker();
    using (var restored = await Connect())
    {
        var bootstrap = await restored.Call("bootstrap");
        var restoredSession = bootstrap.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("id").GetString() == next.GetProperty("id").GetString());
        var restoredAgent = bootstrap.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "aot-fixture");
        Record("Native restart restores encrypted sessions, profiles and UI state",
            !restoredSession.GetProperty("running").GetBoolean()
            && restoredAgent.GetProperty("env").GetProperty("WEIRDO_TEST_ENV").GetString() == "aot-local-fixture"
            && bootstrap.GetProperty("state").GetProperty("workspaces").GetArrayLength() == 1);
        await restored.Call("broker.shutdown");
        await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    broker.Dispose(); broker = null;

    var statePath = Path.Combine(data, "sessions.json");
    using var currentState = JsonDocument.Parse(File.ReadAllText(statePath));
    var ciphertext = Convert.FromBase64String(currentState.RootElement.GetProperty("payload").GetProperty("encrypted").GetString()!);
    var archive = ProtectedData.Unprotect(ciphertext, Encoding.UTF8.GetBytes("Weirdo.Windows.SessionRequests.v1"), DataProtectionScope.CurrentUser);
    try
    {
        foreach (var legacyEntropy in new byte[]?[] { Encoding.UTF8.GetBytes("Kooky.Windows.SessionRequests.v1"), null })
        {
            var legacyCiphertext = Convert.ToBase64String(ProtectedData.Protect(archive, legacyEntropy, DataProtectionScope.CurrentUser));
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new { schemaVersion = 1, payload = new { encrypted = legacyCiphertext } }));
            broker = StartBroker();
            using var legacy = await Connect();
            var legacySessions = await Sessions(legacy);
            Record(legacyEntropy is null ? "Original entropy-free session archives remain readable" : "Legacy Kooky session entropy remains readable",
                legacySessions.Any(s => s.GetProperty("id").GetString() == next.GetProperty("id").GetString()));
            await legacy.Call("broker.shutdown");
            await broker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            broker.Dispose(); broker = null;
        }
    }
    finally { CryptographicOperations.ZeroMemory(archive); }
}
finally
{
    if (broker is not null)
    {
        if (!broker.HasExited) { broker.Kill(entireProcessTree: true); await broker.WaitForExitAsync(); }
        broker.Dispose();
    }
    await File.WriteAllTextAsync(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, source, results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(Path.Combine(evidence, "results.json"));
    if (failures > 0) Environment.ExitCode = 1;
}

static bool IsAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }

sealed class PipeRpc(string name) : IDisposable
{
    private readonly NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    private StreamReader? reader;
    private StreamWriter? writer;
    private readonly Dictionary<string, JsonElement> replies = new();
    private readonly Dictionary<string, long> sent = new();
    private readonly Dictionary<string, double> latencies = new();
    private bool disposed;
    public async Task Connect()
    {
        await pipe.ConnectAsync(15000);
        reader = new(pipe, Encoding.UTF8, false, 65536, true);
        writer = new(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
    }
    public async Task<string> Send(string type, object? payload)
    {
        var id = Guid.NewGuid().ToString("N");
        sent[id] = Stopwatch.GetTimestamp();
        await writer!.WriteLineAsync(JsonSerializer.Serialize(new { id, type, payload }));
        return id;
    }
    public async Task<JsonElement> Call(string type, object? payload = null) => await Receive(await Send(type, payload));
    public double ResponseLatencyMilliseconds(string id) => latencies[id];
    public async Task<JsonElement> Receive(string id)
    {
        if (replies.Remove(id, out var existing)) return Result(existing);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await reader!.ReadLineAsync(timeout.Token) ?? throw new IOException("Broker disconnected.");
            using var doc = JsonDocument.Parse(line);
            var response = doc.RootElement;
            if (!response.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.String) continue;
            var responseKey = responseId.GetString()!;
            if (sent.TryGetValue(responseKey, out var timestamp)) latencies[responseKey] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            if (responseId.GetString() == id) return Result(response);
            replies[responseId.GetString()!] = response.Clone();
        }
    }
    private static JsonElement Result(JsonElement response)
    {
        if (!response.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(response.GetProperty("error").GetString());
        return response.GetProperty("result").Clone();
    }
    public void Dispose() { if (disposed) return; disposed = true; writer?.Dispose(); reader?.Dispose(); pipe.Dispose(); }
}
