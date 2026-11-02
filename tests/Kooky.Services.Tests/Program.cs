using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kooky.Core;
using Kooky.Core.Services;

if (args.FirstOrDefault() == "--native-launch-fixture")
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.Write(JsonSerializer.Serialize(new { arguments = args.Skip(1).ToArray(), environment = Environment.GetEnvironmentVariable("KOOKY_NATIVE_FIXTURE_ENV"), executable = Environment.ProcessPath }));
    Environment.ExitCode = 17;
    return;
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "run-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); passed++; Console.WriteLine("PASS " + label); }
void Reject<T>(Action action, string label) where T : Exception { try { action(); } catch (T) { Check(true, label); return; } throw new Exception("FAILED: " + label); }
async Task<(int Code, string Output, string Error)> Run(string executable, IEnumerable<string> args, string cwd)
{
    using var process = new Process { StartInfo = new(executable) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
    foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { process.Kill(true); throw; }
    return (process.ExitCode, await output, await error);
}
try
{
    var storePath = Path.Combine(root, "state");
    var store = new StateStore(storePath);
    Check(store.Load("missing") is null, "empty state is absent");
    store.Save("state", new { value = "first" });
    store.Save("state", new { value = "second" });
    Check(store.Load("state")?.GetProperty("value").GetString() == "second", "atomic save round trip");
    File.WriteAllText(Path.Combine(storePath, "state.json"), "{broken");
    Check(store.Load("state")?.GetProperty("value").GetString() == "first", "corrupt primary recovers last-good backup");
    Check(Directory.GetFiles(storePath, "state.json.corrupt-*").Length == 1 && store.RecoveryWarnings.Count > 0, "corruption quarantined with visible warning");
    File.WriteAllText(Path.Combine(storePath, "legacy.json"), "{\"old\":true}");
    Check(store.Load("legacy")?.GetProperty("old").GetBoolean() == true, "legacy state readable");
    store.Save("legacy", new { old = false });
    using (var migrated = JsonDocument.Parse(File.ReadAllText(Path.Combine(storePath, "legacy.json")))) Check(migrated.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "legacy state migrates on save");
    File.WriteAllText(Path.Combine(storePath, "future.json"), "{\"schemaVersion\":999,\"payload\":{}}");
    Reject<NotSupportedException>(() => store.Save("future", new { }), "future schema preserved and rejected");
    Reject<ArgumentException>(() => store.Save("../outside", new { }), "state path traversal rejected");
    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => store.Save("concurrent", new { number = i }))));
    Check(store.Load("concurrent")?.GetProperty("number").TryGetInt32(out _) == true, "concurrent writes leave valid durable state");
    EfsPersistenceTests.Run(Check, root);

    var catalogPath = Path.Combine(root, "catalog");
    var catalog = new AgentCatalog(catalogPath);
    var profiles = catalog.List();
    Check(profiles.Count == 4 && profiles.All(p => p.BuiltIn), "four real built-in Agent profiles");
    Check(profiles.Single(p => p.Id == "shell").Available, "native PowerShell discovered");
    var terminalKeys = new[] { "TERM", "COLORTERM", "TERM_PROGRAM" };
    var originalTerminalEnvironment = terminalKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
    try
    {
        Environment.SetEnvironmentVariable("TERM", "dumb");
        Environment.SetEnvironmentVariable("COLORTERM", "automation-color");
        Environment.SetEnvironmentVariable("TERM_PROGRAM", "Automation Parent");
        var inherited = CommandResolver.InheritedEnvironment();
        var terminalCatalog = new AgentCatalog(Path.Combine(root, "terminal-environment"));
        var defaults = terminalCatalog.Resolve(new() { Cwd = root, AgentId = "shell" });
        Check(defaults.Environment["TERM"] == "xterm-256color" && defaults.Environment["COLORTERM"] == "truecolor" && defaults.Environment["TERM_PROGRAM"] == "Kooky",
            "real terminal defaults replace inherited automation terminal capabilities");
        Check(inherited.Where(item => !terminalKeys.Contains(item.Key, StringComparer.OrdinalIgnoreCase)).All(item => defaults.Environment.TryGetValue(item.Key, out var actual) && actual == item.Value),
            "terminal defaults preserve all unrelated inherited environment values");
        var terminalProfiles = terminalCatalog.List();
        var shellIndex = terminalProfiles.FindIndex(profile => profile.Id == "shell");
        terminalProfiles[shellIndex] = terminalProfiles[shellIndex] with { Env = new() { ["TERM"] = "vt100", ["COLORTERM"] = "profile-color", ["TERM_PROGRAM"] = "Profile Terminal" } };
        terminalCatalog.Save(terminalProfiles);
        var profileOverride = terminalCatalog.Resolve(new() { Cwd = root, AgentId = "shell" });
        Check(profileOverride.Environment["TERM"] == "vt100" && profileOverride.Environment["COLORTERM"] == "profile-color" && profileOverride.Environment["TERM_PROGRAM"] == "Profile Terminal",
            "explicit Agent terminal capabilities override host defaults");
        var requestOverride = terminalCatalog.Resolve(new() { Cwd = root, AgentId = "shell", Env = new() { ["TERM"] = "screen-256color", ["COLORTERM"] = "", ["TERM_PROGRAM"] = "Requested Terminal" } });
        Check(requestOverride.Environment["TERM"] == "screen-256color" && requestOverride.Environment["COLORTERM"] == "" && requestOverride.Environment["TERM_PROGRAM"] == "Requested Terminal",
            "explicit session terminal capabilities override Agent values");
        Check(Environment.GetEnvironmentVariable("TERM") == "dumb" && Environment.GetEnvironmentVariable("COLORTERM") == "automation-color" && Environment.GetEnvironmentVariable("TERM_PROGRAM") == "Automation Parent",
            "terminal capability resolution leaves parent environment intact");
    }
    finally
    {
        foreach (var (key, value) in originalTerminalEnvironment) Environment.SetEnvironmentVariable(key, value);
    }
    var secret = "test-secret-" + Guid.NewGuid();
    var customPath = Path.Combine(root, "工具 space & test.cmd");
    File.WriteAllText(customPath, "@echo off\r\necho %KOOKY_TEST_ENV%\r\nexit /b 7\r\n", new UTF8Encoding(false));
    profiles.Add(new AgentProfile { Id = "fixture", Name = "Fixture", Command = '"' + customPath + '"', Kind = "custom", Env = new() { ["KOOKY_TEST_ENV"] = secret } });
    catalog.Save(profiles);
    Check(!File.ReadAllText(Path.Combine(catalogPath, "agents.json")).Contains(secret), "explicit environment encrypted at rest");
    Check(catalog.List().Single(p => p.Id == "fixture").Env["KOOKY_TEST_ENV"] == secret, "DPAPI environment decrypts for current user");
    var oldValue = Environment.GetEnvironmentVariable("KOOKY_TEST_INHERITED");
    Environment.SetEnvironmentVariable("KOOKY_TEST_INHERITED", "parent-value");
    try
    {
        var launch = catalog.Resolve(new() { Cwd = root, AgentId = "fixture", Env = new() { ["KOOKY_TEST_INHERITED"] = "child-value" } });
        Check(launch.Environment["KOOKY_TEST_INHERITED"] == "child-value" && Environment.GetEnvironmentVariable("KOOKY_TEST_INHERITED") == "parent-value", "per-session overrides preserve parent environment");
        var (exe, tail) = CommandResolver.SplitCommand(launch.CommandLine);
        using var launched = new Process { StartInfo = new(exe, tail) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root } };
        foreach (var (name, value) in launch.Environment) launched.StartInfo.Environment[name] = value;
        launched.Start();
        var stdout = launched.StandardOutput.ReadToEndAsync();
        var stderr = launched.StandardError.ReadToEndAsync();
        await launched.WaitForExitAsync();
        Check(launched.ExitCode == 7 && (await stdout).Contains(secret) && (await stderr).Length == 0, "real cmd launcher supports spaces Unicode ampersand and exit code");
    }
    finally { Environment.SetEnvironmentVariable("KOOKY_TEST_INHERITED", oldValue); }
    await NativeLauncherTests.RunAsync(Check, root);
    var shellLaunch = catalog.Resolve(new() { Cwd = root, AgentId = "shell" });
    Check(Encoding.Unicode.GetString(Convert.FromBase64String(shellLaunch.CommandLine.Split(' ')[^1])).Contains("']7;'"), "shell prompt has session-local OSC7 integration");
    if (profiles.Single(p => p.Id == "claude").Available)
    {
        var claude = catalog.Resolve(new() { Cwd = root, AgentId = "claude" });
        Check(Guid.TryParse(claude.NativeSessionId, out _) && claude.CommandLine.Contains("--session-id"), "Claude receives exact native UUID");
        var resume = catalog.Resolve(new() { Cwd = root, AgentId = "claude", NativeSessionId = claude.NativeSessionId });
        Check(resume.CommandLine.Contains("--resume " + claude.NativeSessionId) && !resume.CommandLine.Contains("--session-id"), "Claude resumes exact UUID");
    }
    Reject<ArgumentException>(() => catalog.Resolve(new() { Cwd = root, AgentId = "codex", NativeSessionId = "x & echo injected" }), "native session injection rejected");

    var codexHome = Path.Combine(root, "native-codex");
    var sessionFolder = Path.Combine(codexHome, "sessions", "2026", "11", "02");
    Directory.CreateDirectory(sessionFolder);
    var start = DateTimeOffset.UtcNow;
    var nativeId = Guid.NewGuid().ToString();
    var secondNativeId = Guid.NewGuid().ToString();
    var nativeProfiles = catalog.List();
    var codexIndex = nativeProfiles.FindIndex(p => p.Id == "codex");
    nativeProfiles[codexIndex] = nativeProfiles[codexIndex] with { Env = new() { ["CODEX_HOME"] = codexHome } };
    catalog.Save(nativeProfiles);
    var firstRequest = new SessionRequest { Cwd = root, AgentId = "codex" };
    var secondRequest = new SessionRequest { Cwd = root, AgentId = "codex" };
    Check(firstRequest.TrackingId != secondRequest.TrackingId && Guid.TryParseExact(firstRequest.TrackingId, "N", out _), "each launch gets an independent tracking UUID");
    var oldOriginator = Environment.GetEnvironmentVariable("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
    string firstOriginator;
    string secondOriginator;
    try
    {
        Environment.SetEnvironmentVariable("CODEX_INTERNAL_ORIGINATOR_OVERRIDE", "Existing Launcher");
        firstOriginator = catalog.Resolve(firstRequest).Environment["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"];
        secondOriginator = catalog.Resolve(secondRequest).Environment["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"];
        Check(firstOriginator == "Existing Launcher/Kooky:" + firstRequest.TrackingId && secondOriginator == "Existing Launcher/Kooky:" + secondRequest.TrackingId, "Codex child originator preserves launcher prefix with distinct markers");
        Check(Environment.GetEnvironmentVariable("CODEX_INTERNAL_ORIGINATOR_OVERRIDE") == "Existing Launcher", "Codex tracking does not mutate inherited process environment");
    }
    finally { Environment.SetEnvironmentVariable("CODEX_INTERNAL_ORIGINATOR_OVERRIDE", oldOriginator); }
    void WriteNative(string id, string cwd, string source, string? originator, DateTimeOffset timestamp) => File.WriteAllText(Path.Combine(sessionFolder, "rollout-" + id + ".jsonl"), JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd, source, originator, timestamp } }));
    WriteNative(nativeId, root, "cli", firstOriginator, start.AddMinutes(12));
    WriteNative(secondNativeId, root, "cli", secondOriginator, start.AddHours(3));
    WriteNative(Guid.NewGuid().ToString(), Path.GetPathRoot(root)!, "cli", "Other Launcher", start);
    var desktopId = Guid.NewGuid().ToString();
    WriteNative(desktopId, root, "vscode", firstOriginator, start);
    Check(catalog.History("codex", root).Count == 3, "session history still filters exact cwd");
    var firstTerminal = new SessionInfo { Id = "terminal-one", TrackingId = firstRequest.TrackingId, AgentId = "codex", Cwd = root, StartedAt = start };
    var secondTerminal = new SessionInfo { Id = "terminal-two", TrackingId = secondRequest.TrackingId, AgentId = "codex", Cwd = root, StartedAt = start };
    Check(catalog.DiscoverNativeSession(firstTerminal) == nativeId && catalog.DiscoverNativeSession(secondTerminal) == secondNativeId, "same-cwd concurrent Codex sessions correlate exactly after delayed onboarding");
    Check(catalog.DiscoverNativeSession(firstTerminal with { Cwd = Path.GetPathRoot(root)! }) == nativeId, "native identity remains exact after terminal cwd changes");
    Check(catalog.DiscoverNativeSession(new() { Id = "legacy", AgentId = "codex", Cwd = root, StartedAt = start }) is null, "legacy sessions without tracking marker are not inferred");
    var missingTracking = Guid.NewGuid().ToString("N");
    WriteNative(Guid.NewGuid().ToString(), root, "cli", "Existing Launcher/Kooky:" + missingTracking + "-other", start);
    Check(catalog.DiscoverNativeSession(new() { Id = "unmatched", TrackingId = missingTracking, AgentId = "codex", Cwd = root, StartedAt = start }) is null, "partial originator marker cannot bind a foreign session");
    WriteNative(Guid.NewGuid().ToString(), root, "cli", firstOriginator, start.AddDays(1));
    Check(catalog.DiscoverNativeSession(firstTerminal) is null, "multiple native IDs with one marker remain ambiguous");
    Check(catalog.DiscoverNativeSession(new() { NativeSessionId = nativeId, AgentId = "codex", Cwd = root }) == nativeId, "explicit native UUID restoration remains supported without marker");

    var repository = Path.Combine(root, "仓库 with spaces");
    Directory.CreateDirectory(repository);
    var git = CommandResolver.FindExecutable("git.exe")!;
    async Task Git(params string[] args) { var value = await Run(git, args, repository); if (value.Code != 0) throw new Exception(value.Error); }
    await Git("init", "-b", "main");
    await Git("config", "user.email", "test@example.invalid");
    await Git("config", "user.name", "Kooky Test");
    await Git("config", "core.autocrlf", "false");
    File.WriteAllText(Path.Combine(repository, "tracked.txt"), "first\nsecond\n", new UTF8Encoding(false));
    await Git("add", ".");
    await Git("commit", "-m", "fixture");
    File.WriteAllText(Path.Combine(repository, "tracked.txt"), "first\nsecond\nstaged\n", new UTF8Encoding(false));
    await Git("add", "tracked.txt");
    File.AppendAllText(Path.Combine(repository, "tracked.txt"), "unstaged\n", new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(repository, "未跟踪.txt"), "one\ntwo", new UTF8Encoding(false));
    File.WriteAllBytes(Path.Combine(repository, "binary.bin"), [0, 1, 2]);
    var workspace = new WorkspaceService();
    var info = await workspace.InspectAsync(repository);
    Console.WriteLine("Git fixture result: " + JsonSerializer.Serialize(info.Git));
    Check(info.Git is { IsRepository: true, Branch: "main", ChangedFiles: 3, Added: 4, Deleted: 0, Error: null }, "real Git staged plus unstaged net totals and Unicode untracked files");
    var files = await workspace.FilesAsync(repository, repository);
    Check(files.Count == 3 && files.Any(x => x.Name == "未跟踪.txt"), "real file tree excludes Git internals");
    await RejectAsync<UnauthorizedAccessException>(() => workspace.FilesAsync(repository, root), "file tree rejects parent traversal");
    Directory.CreateDirectory(Path.Combine(root, "outside"));
    var junction = Path.Combine(repository, "external");
    var junctionResult = await Run(Environment.GetEnvironmentVariable("COMSPEC")!, ["/d", "/c", "mklink", "/J", junction, Path.Combine(root, "outside")], repository);
    if (junctionResult.Code != 0) throw new Exception("Could not create junction test fixture: " + junctionResult.Error);
    await RejectAsync<UnauthorizedAccessException>(() => workspace.FilesAsync(repository, junction), "file tree rejects junction escape");
    Directory.Delete(junction); // Remove the junction itself; never recurse through it.
    await Git("add", ".");
    await Git("commit", "-m", "prepare rename fixture");
    await Git("mv", "tracked.txt", "renamed 文件.txt");
    var renameInfo = await new WorkspaceService().InspectAsync(repository);
    Check(renameInfo.Git is { ChangedFiles: 1, Added: 0, Deleted: 0 }, "pure rename does not inflate added/deleted totals");
    var unborn = Path.Combine(root, "unborn");
    Directory.CreateDirectory(unborn);
    Check((await Run(git, ["init", "-b", "main"], unborn)).Code == 0, "unborn Git fixture initialized");
    File.WriteAllText(Path.Combine(unborn, "new.txt"), "one\ntwo\n", new UTF8Encoding(false));
    Check((await Run(git, ["add", "new.txt"], unborn)).Code == 0, "unborn index fixture staged");
    File.AppendAllText(Path.Combine(unborn, "new.txt"), "three\n", new UTF8Encoding(false));
    var unbornInfo = await workspace.InspectAsync(unborn);
    Check(unbornInfo.Git is { IsRepository: true, ChangedFiles: 1, Added: 3, Deleted: 0 }, "unborn Git net line totals");
    Console.WriteLine($"SERVICES TESTS PASSED: {passed}");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    throw;
}
finally
{
    // Every fixture is under this test's resolved, unique project-local directory.
    var allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures")) + Path.DirectorySeparatorChar;
    if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}

async Task RejectAsync<T>(Func<Task> action, string label) where T : Exception
{
    try { await action(); } catch (T) { Check(true, label); return; }
    throw new Exception("FAILED: " + label);
}
