using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kooky.Core;
using Kooky.Core.Services;

internal static class NativeLauncherTests
{
    public static async Task RunAsync(Action<bool, string> check, string fixtureRoot)
    {
        var testExecutable = Path.Combine(AppContext.BaseDirectory, "Kooky.Services.Tests.exe");
        if (!File.Exists(testExecutable)) throw new FileNotFoundException("The real native launcher fixture apphost is missing.", testExecutable);
        const string argument = "参数 with spaces & $literal";
        const string environmentValue = "环境 value with spaces & = literal";
        var originalEnvironment = Environment.GetEnvironmentVariable("KOOKY_NATIVE_FIXTURE_ENV");
        var nativeCommand = CommandResolver.QuoteWindows(testExecutable) + " --native-launch-fixture --model fixture/model " + CommandResolver.QuoteWindows(argument);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        const string expression = "[Console]::Write(('explicit-' + 'shell')); exit 23";
        var powershellCommand = CommandResolver.QuoteWindows(powershell) + " -NoLogo -NoProfile -NonInteractive -Command " + CommandResolver.QuoteWindows(expression);
        var catalog = new AgentCatalog(Path.Combine(fixtureRoot, "native-launcher"));
        catalog.Save([
            new AgentProfile { Id = "native-fixture", Name = "Native fixture", Command = nativeCommand, Kind = "claude", Env = new() { ["KOOKY_NATIVE_FIXTURE_ENV"] = environmentValue } },
            new AgentProfile { Id = "powershell-fixture", Name = "Explicit PowerShell", Command = powershellCommand, Kind = "custom" }
        ]);
        var nativeId = Guid.NewGuid().ToString();
        var launch = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = "native-fixture", NativeSessionId = nativeId });
        check(launch.CommandLine == nativeCommand + " --resume " + nativeId,
            "EXE arguments and exact native resume ID produce a direct Windows command line");
        var result = await RunAsync(launch);
        check(result.ExitCode == 17 && result.Error.Length == 0, "direct native EXE preserves its real exit code");
        using (var document = JsonDocument.Parse(result.Output))
        {
            var payload = document.RootElement;
            var actualArguments = payload.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
            check(actualArguments.SequenceEqual(new[] { "--model", "fixture/model", argument, "--resume", nativeId }),
                "native EXE receives model arguments Unicode spaces and shell metacharacters literally");
            check(payload.GetProperty("environment").GetString() == environmentValue && Environment.GetEnvironmentVariable("KOOKY_NATIVE_FIXTURE_ENV") == originalEnvironment,
                "native EXE receives Unicode environment while the parent keeps its existing value");
            check(string.Equals(payload.GetProperty("executable").GetString(), testExecutable, StringComparison.OrdinalIgnoreCase),
                "native launch runs the resolved executable directly");
        }
        var shellLaunch = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = "powershell-fixture" });
        check(shellLaunch.CommandLine == powershellCommand, "explicit PowerShell command keeps its requested arguments");
        var shellResult = await RunAsync(shellLaunch);
        check(shellResult.ExitCode == 23 && shellResult.Output == "explicit-shell" && shellResult.Error.Length == 0,
            "explicit PowerShell Command still evaluates shell expressions");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(LaunchSpec launch)
    {
        var (executable, arguments) = CommandResolver.SplitCommand(launch.CommandLine);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = launch.Cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var (name, value) in launch.Environment) process.StartInfo.Environment[name] = value;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
