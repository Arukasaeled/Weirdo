using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Weirdo.Core;

if (args.Contains("--child")) { await Task.Delay(Timeout.Infinite); return; }
if (args.Contains("--worker"))
{
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine("READY 中文验证 😀");
    Console.Write("\x1b[31mANSI_RED\x1b[0m\r\n");
    Console.WriteLine("ENV=" + Environment.GetEnvironmentVariable("WEIRDO_TEST_ENV"));
    while (Console.ReadLine() is { } line)
    {
        if (line == "__exit__") return;
        if (line == "__exit7__") { Environment.ExitCode = 7; return; }
        if (line == "__size__") Console.WriteLine($"SIZE={Console.WindowWidth}x{Console.WindowHeight}");
        else if (line == "__child__")
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child") { UseShellExecute = false });
            Console.WriteLine("CHILD=" + child!.Id);
        }
        else if (line == "__cwd__")
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "src");
            Console.Write("\x1b]7;" + new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri + "\x1b\\");
            Console.WriteLine("DIRECTORY_REPORTED");
        }
        else if (line == "__burst__")
        {
            var payload = new string('A', 128) + "中文😀";
            for (var i = 0; i < 10000; i++) Console.WriteLine(payload);
            Console.WriteLine("BURST_DONE");
        }
        else Console.WriteLine("ECHO=" + line);
    }
    return;
}

var checks = new List<object>();
var started = Stopwatch.StartNew();
using var manager = new TerminalSessionManager();
var events = new ConcurrentQueue<TerminalChunk>();
manager.Output += events.Enqueue;
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var executable = Environment.ProcessPath!;
LaunchSpec Launch(Dictionary<string,string>? env = null) => new($"\"{executable}\" --worker", root, env ?? new(), null);
SessionInfo Spawn(string title) => manager.Create(new() { WorkspaceId = "test", AgentId = "test-worker", Title = title, Cwd = root }, Launch(new() { ["WEIRDO_TEST_ENV"] = "代理保留" }));
void Pass(string name, object? detail = null) { checks.Add(new { name, passed = true, detail }); Console.WriteLine("PASS " + name); }
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task<string> WaitText(string id, string needle, int timeoutMs = 12000)
{
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < timeoutMs)
    {
        var output = manager.Replay(id).Data;
        if (output.Contains(needle, StringComparison.Ordinal)) return output;
        await Task.Delay(30);
    }
    throw new TimeoutException($"No '{needle}' from terminal {id}. Tail: " + manager.Replay(id).Data[^Math.Min(500, manager.Replay(id).Data.Length)..]);
}
async Task WaitExit(string id)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.ElapsedMilliseconds < 12000)
    {
        if (!manager.List().Single(session => session.Id == id).Running) return;
        await Task.Delay(30);
    }
    throw new TimeoutException("Terminal did not exit: " + id);
}

var first = Spawn("Unicode test");
var initial = await WaitText(first.Id, "ENV=代理保留");
Assert(initial.Contains("中文验证 😀"), "Unicode output corrupted.");
Assert(initial.Contains("ANSI_RED") && initial.Contains('\x1b'), "ANSI output missing.");
manager.Write(first.Id, "你好，Windows 😀\r");
await WaitText(first.Id, "ECHO=你好，Windows 😀");
Pass("ConPTY Unicode/Chinese/emoji input, ANSI output, inherited overrides", new { first.ProcessId });

manager.Resize(first.Id, 137, 43);
manager.Write(first.Id, "__size__\r");
await WaitText(first.Id, "SIZE=137x43");
Pass("Resize reaches child console", new { cols = 137, rows = 43 });

var sessions = new List<SessionInfo> { first };
for (var i = 1; i < 5; i++) sessions.Add(Spawn("Parallel " + i));
foreach (var item in sessions) await WaitText(item.Id, "READY");
Assert(manager.List().Count(s => s.Running) == 5, "Five terminals are not running.");
Assert(sessions.Select(s => s.ProcessId).Distinct().Count() == 5, "Process IDs not independent.");
for (var i = 0; i < sessions.Count; i++) manager.Write(sessions[i].Id, "parallel-" + i + "\r");
for (var i = 0; i < sessions.Count; i++) await WaitText(sessions[i].Id, "ECHO=parallel-" + i);
Pass("Five independently interactive real terminal processes", sessions.Select(s => s.ProcessId).ToArray());

var replay = manager.Replay(first.Id);
Assert(replay.Sequence > 0 && replay.Data.Contains("ECHO=parallel-0"), "Replay incomplete.");
manager.Rename(first.Id, "Restored tab");
manager.UpdateNativeSessionId(first.Id, "native-id-test");
var copy = manager.List().Single(s => s.Id == first.Id);
copy.Title = "should not mutate manager";
Assert(manager.List().Single(s => s.Id == first.Id).Title == "Restored tab", "Snapshot mutation leaked.");
manager.Write(first.Id, "unattached-ui-still-runs\r");
await WaitText(first.Id, "ECHO=unattached-ui-still-runs");
Pass("Replay, detached UI survival, immutable snapshots, metadata");

manager.Move(first.Id, "moved-workspace");
Assert(manager.List().Single(s => s.Id == first.Id).WorkspaceId == "moved-workspace", "Move did not update owner.");
manager.Write(first.Id, "__cwd__\r");
await WaitText(first.Id, "DIRECTORY_REPORTED");
Assert(Path.TrimEndingDirectorySeparator(manager.List().Single(s => s.Id == first.Id).Cwd) == Path.Combine(root, "src"), "OSC 7 cwd was not recognized.");
manager.UpdateWorkingDirectory(first.Id, root);
Pass("Move metadata and explicit OSC 7 working-directory protocol");

manager.Write(sessions[1].Id, "__child__\r");
var childOutput = await WaitText(sessions[1].Id, "CHILD=");
var childMatch = System.Text.RegularExpressions.Regex.Match(childOutput, @"CHILD=(\d+)");
Assert(childMatch.Success, "No child PID.");
var childPid = int.Parse(childMatch.Groups[1].Value);
using var childProcess = Process.GetProcessById(childPid);
manager.Close(sessions[1].Id);
await WaitExit(sessions[1].Id);
Assert(childProcess.WaitForExit(5000), "Descendant escaped per-terminal Job.");
Assert(manager.List().Count(s => s.Running) == 4, "Closing one terminal killed another.");
manager.Write(first.Id, "other-session-survives\r");
await WaitText(first.Id, "ECHO=other-session-survives");
Pass("Close kills owned descendant tree, preserves other sessions", new { childPid });

manager.Write(sessions[2].Id, "__exit7__\r");
await WaitExit(sessions[2].Id);
Assert(manager.List().Single(s => s.Id == sessions[2].Id).ExitCode == 7, "Wrong child exit code.");
Pass("Native nonzero exit code is reported accurately");
manager.Forget(sessions[2].Id);
Assert(manager.List().All(s => s.Id != sessions[2].Id), "Forgotten session retains replay.");
Pass("Forget releases archived terminal buffers");

manager.Write(sessions[3].Id, "__burst__\r");
await WaitText(sessions[3].Id, "BURST_DONE", 45000);
var bounded = manager.Replay(sessions[3].Id);
Assert(bounded.Truncated, "Replay was not bounded.");
Assert(bounded.Data.Length <= 1_048_576, "Replay exceeded character budget.");
Assert(!bounded.Data.Contains('\uFFFD'), "UTF8 burst was corrupted.");
Pass("High-volume UTF8 output, bounded replay and backpressure", new { characters = bounded.Data.Length, sequence = bounded.Sequence });

var perSessionSequences = events.ToArray().GroupBy(e => e.SessionId);
foreach (var group in perSessionSequences)
{
    long previous = 0;
    foreach (var item in group) { Assert(item.Sequence == previous + 1, "Event stream lost/reordered a chunk."); previous = item.Sequence; }
}
Pass("Asynchronous events maintain contiguous sequence order");

var interrupted = Spawn("Interrupted burst");
await WaitText(interrupted.Id, "READY");
manager.Write(interrupted.Id, "__burst__\r");
await Task.Delay(20);
manager.Close(interrupted.Id);
await WaitExit(interrupted.Id);
Pass("Close while output is streaming drains without deadlock");

var beforeInvalid = manager.List().Count;
try
{
    manager.Create(new() { Cwd = root }, new("Z:\\weirdo-missing-command.exe", root, new(), null));
    throw new InvalidOperationException("Missing executable accepted.");
}
catch (System.ComponentModel.Win32Exception) { }
Assert(manager.List().Count == beforeInvalid, "Failed launch leaked session.");
try { manager.Resize(first.Id, 0, 12); throw new InvalidOperationException("Invalid resize accepted."); }
catch (ArgumentOutOfRangeException) { }
Pass("Invalid launch and resize fail without a zombie session");

foreach (var item in manager.List().Where(s => s.Running)) manager.Close(item.Id);
foreach (var item in manager.List()) await WaitExit(item.Id);
Pass("All test processes cleaned up");
var evidence = new { suite = "Windows ConPTY behavioral integration", date = DateTimeOffset.UtcNow, durationMs = started.ElapsedMilliseconds, checks };
var evidencePath = Path.Combine(AppContext.BaseDirectory, "conpty-results.json");
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(evidencePath);
