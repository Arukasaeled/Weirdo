using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Weirdo.Core.Terminal;

namespace Weirdo.Core;

/// <summary>Owns real ConPTY processes independently of any UI attachment.</summary>
public sealed class TerminalSessionManager : IDisposable
{
    private const int ReplayCharacterLimit = 1_048_576; // ~2 MiB UTF-16, per terminal.
    private const int MaximumArchivedSessions = 32;
    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly Channel<object> _events = Channel.CreateBounded<object>(new BoundedChannelOptions(512)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task _dispatch;
    private int _disposed;
    public event Action<TerminalChunk>? Output;
    public event Action<SessionInfo>? Exited;

    public TerminalSessionManager() => _dispatch = Task.Run(DispatchAsync);

    public SessionInfo Create(SessionRequest request, LaunchSpec launch)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var process = new ConPtyProcess(launch, request.Cols, request.Rows);
        var info = new SessionInfo
        {
            Id = Guid.NewGuid().ToString("N"), TrackingId = request.TrackingId, WorkspaceId = request.WorkspaceId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? request.AgentId : request.Title,
            AgentId = request.AgentId, Cwd = Path.GetFullPath(launch.Cwd), ProcessId = process.ProcessId,
            Running = true, StartedAt = DateTimeOffset.UtcNow, NativeSessionId = launch.NativeSessionId,
            Cols = request.Cols, Rows = request.Rows
        };
        var entry = new Entry(info, process);
        if (!_sessions.TryAdd(info.Id, entry)) { process.Dispose(); throw new InvalidOperationException("Session ID collision."); }
        try
        {
            process.Start((data, bytes) => OnOutput(entry, data, bytes), code => OnExit(entry, code));
            lock (entry.Gate) return info with { };
        }
        catch { _sessions.TryRemove(info.Id, out _); process.Dispose(); throw; }
    }

    public IReadOnlyList<SessionInfo> List() => _sessions.Values.Select(entry =>
    {
        lock (entry.Gate) return entry.Info with { };
    }).OrderBy(info => info.StartedAt).ToArray();

    public void Write(string id, string data) => Find(id).Process.Write(data);

    public void Resize(string id, int cols, int rows)
    {
        var entry = Find(id);
        entry.Process.Resize(cols, rows);
        lock (entry.Gate) { entry.Info.Cols = cols; entry.Info.Rows = rows; }
    }

    public void Close(string id) => Find(id).Process.Terminate();

    /// <summary>Closes an owned terminal and releases its replay after exit.</summary>
    public void Forget(string id)
    {
        var entry = Find(id);
        bool running;
        lock (entry.Gate) { entry.ForgetOnExit = true; running = entry.Info.Running; }
        if (running) entry.Process.Terminate();
        else _sessions.TryRemove(id, out _);
    }

    public void Move(string id, string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) throw new ArgumentException("Workspace ID is required.");
        var entry = Find(id);
        lock (entry.Gate) entry.Info.WorkspaceId = workspaceId;
    }

    public void UpdateWorkingDirectory(string id, string cwd)
    {
        var path = Path.GetFullPath(cwd);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var entry = Find(id);
        lock (entry.Gate) entry.Info.Cwd = path;
    }

    public void Rename(string id, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160) throw new ArgumentException("Tab name must contain 1..160 characters.");
        var entry = Find(id);
        lock (entry.Gate) entry.Info.Title = title.Trim();
    }

    public TerminalReplay Replay(string id)
    {
        var entry = Find(id);
        lock (entry.Gate)
            return new TerminalReplay(id, entry.Sequence, string.Concat(entry.Buffer), entry.Truncated);
    }

    public void UpdateNativeSessionId(string id, string nativeId)
    {
        var entry = Find(id);
        lock (entry.Gate) entry.Info.NativeSessionId = nativeId;
    }

    private Entry Find(string id) => _sessions.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException("Terminal session does not exist.");

    private void OnOutput(Entry entry, string data, int bytes)
    {
        TerminalChunk chunk;
        lock (entry.Gate)
        {
            entry.Info.LastOutputAt = DateTimeOffset.UtcNow;
            entry.Info.BytesRead += bytes;
            if (entry.WorkingDirectory.Feed(data) is { } cwd) entry.Info.Cwd = cwd;
            entry.Buffer.Enqueue(data);
            entry.CharacterCount += data.Length;
            while (entry.CharacterCount > ReplayCharacterLimit && entry.Buffer.Count > 1)
            {
                entry.CharacterCount -= entry.Buffer.Dequeue().Length;
                entry.Truncated = true;
            }
            chunk = new(entry.Info.Id, ++entry.Sequence, data);
        }
        // Bounded delivery creates backpressure instead of losing bytes or growing
        // without limit. Dispatch is independent of the ConPTY read thread.
        _events.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
    }

    private void OnExit(Entry entry, int exitCode)
    {
        SessionInfo snapshot;
        lock (entry.Gate)
        {
            entry.Info.Running = false;
            entry.Info.ExitCode = exitCode;
            snapshot = entry.Info with { };
        }
        _events.Writer.WriteAsync(snapshot).AsTask().GetAwaiter().GetResult();
        if (entry.ForgetOnExit) _sessions.TryRemove(entry.Info.Id, out _);
        foreach (var archived in List().Where(s => !s.Running).OrderByDescending(s => s.StartedAt).Skip(MaximumArchivedSessions))
            _sessions.TryRemove(archived.Id, out _);
    }

    private async Task DispatchAsync()
    {
        await foreach (var message in _events.Reader.ReadAllAsync())
        {
            if (message is TerminalChunk chunk) Notify(Output, chunk);
            else if (message is SessionInfo info) Notify(Exited, info);
        }
    }

    private static void Notify<T>(Action<T>? listeners, T value)
    {
        if (listeners is null) return;
        foreach (Action<T> listener in listeners.GetInvocationList())
            try { listener(value); } catch { /* One disconnected UI must not kill a terminal. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var entry in _sessions.Values) entry.Process.Terminate();
        foreach (var entry in _sessions.Values) entry.Process.Dispose();
        _events.Writer.TryComplete();
        if (Task.CurrentId != _dispatch.Id) _dispatch.GetAwaiter().GetResult();
    }

    private sealed class Entry(SessionInfo info, ConPtyProcess process)
    {
        internal readonly object Gate = new();
        internal SessionInfo Info = info;
        internal ConPtyProcess Process = process;
        internal Queue<string> Buffer = new();
        internal int CharacterCount;
        internal long Sequence;
        internal bool Truncated;
        internal bool ForgetOnExit;
        internal WorkingDirectoryTracker WorkingDirectory = new();
    }
}
