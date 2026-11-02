using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kooky.Core.Services;

public sealed class AgentCatalog
{
    private readonly StateStore state;
    private readonly object gate = new();
    private readonly NativeSessionReader sessions = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kooky.Windows.AgentEnvironment.v1");
    private sealed record StoredAgent(string Id, string Name, string Command, string Kind, string? ResumeArguments, bool BuiltIn, string? ProtectedEnvironment);

    public AgentCatalog(string dataDir) => state = new StateStore(dataDir);

    public List<AgentProfile> List()
    {
        lock (gate)
        {
            var profiles = Defaults();
            if (state.Load("agents") is { } json)
            {
                var stored = json.Deserialize<List<StoredAgent>>(JsonDefaults.Options) ?? [];
                foreach (var item in stored)
                {
                    var environment = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    if (item.ProtectedEnvironment is not null)
                    {
                        try
                        {
                            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(item.ProtectedEnvironment), Entropy, DataProtectionScope.CurrentUser);
                            try { environment = new(JsonSerializer.Deserialize<Dictionary<string,string>>(bytes, JsonDefaults.Options) ?? [], StringComparer.OrdinalIgnoreCase); }
                            finally { CryptographicOperations.ZeroMemory(bytes); }
                        }
                        catch (Exception ex) when (ex is CryptographicException or FormatException)
                        { throw new InvalidDataException($"The saved environment for {item.Name} cannot be decrypted by this Windows user. The saved file has been preserved.", ex); }
                    }
                    var profile = new AgentProfile { Id = item.Id, Name = item.Name, Command = item.Command, Kind = item.Kind, ResumeArguments = item.ResumeArguments, BuiltIn = Defaults().Any(x => x.Id == item.Id), Env = environment };
                    var index = profiles.FindIndex(p => p.Id == item.Id);
                    if (index >= 0) profiles[index] = profile; else profiles.Add(profile);
                }
            }
            foreach (var profile in profiles)
            {
                var environment = CommandResolver.InheritedEnvironment();
                foreach (var (name, value) in profile.Env) environment[name] = value;
                try { profile.Available = CommandResolver.FindExecutable(CommandResolver.SplitCommand(profile.Command).Executable, environment) is not null; }
                catch (ArgumentException) { profile.Available = false; }
            }
            return profiles;
        }
    }

    public void Save(List<AgentProfile> profiles)
    {
        if (profiles.Count > 128) throw new ArgumentException("At most 128 Agent profiles are supported.");
        lock (gate)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<StoredAgent>();
            foreach (var profile in profiles)
            {
                if (!Regex.IsMatch(profile.Id, "^[a-zA-Z0-9_-]{1,80}$") || !unique.Add(profile.Id)) throw new ArgumentException("Agent profile IDs must be unique and contain letters, digits, hyphens or underscores.");
                if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100) throw new ArgumentException("Each Agent needs a name of 1–100 characters.");
                if (string.IsNullOrWhiteSpace(profile.Command) || profile.Command.Length > 8192 || profile.Command.Contains('\0')) throw new ArgumentException("Each Agent needs a valid command of at most 8192 characters.");
                if (profile.Kind is not ("custom" or "shell" or "claude" or "codex" or "gemini")) throw new ArgumentException("Unsupported Agent integration kind.");
                if (profile.ResumeArguments is { } arguments && (arguments.Length > 2048 || arguments.Contains('\0'))) throw new ArgumentException("Invalid resume arguments.");
                CommandResolver.ValidateEnvironment(profile.Env);
                string? encrypted = null;
                if (profile.Env.Count > 0)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(profile.Env, JsonDefaults.Options);
                    try { encrypted = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                result.Add(new(profile.Id, profile.Name.Trim(), profile.Command.Trim(), profile.Kind, profile.ResumeArguments, Defaults().Any(p => p.Id == profile.Id), encrypted));
            }
            state.Save("agents", result);
        }
    }

    public LaunchSpec Resolve(SessionRequest request)
    {
        var cwd = Path.GetFullPath(request.Cwd);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Workspace folder does not exist: {cwd}");
        var profile = List().Find(p => p.Id == request.AgentId) ?? throw new ArgumentException("The selected Agent profile no longer exists.");
        var environment = CommandResolver.InheritedEnvironment();
        foreach (var (key, value) in profile.Env) environment[key] = value;
        if (request.Env is not null)
        {
            CommandResolver.ValidateEnvironment(request.Env);
            foreach (var (key, value) in request.Env) environment[key] = value;
        }
        environment.TryAdd("TERM", "xterm-256color");
        environment.TryAdd("COLORTERM", "truecolor");
        environment.TryAdd("TERM_PROGRAM", "Kooky");
        if (profile.Kind == "codex")
        {
            if (!Guid.TryParseExact(request.TrackingId, "N", out _)) throw new ArgumentException("The terminal tracking ID must be a 32-character UUID.");
            // Child-scoped only: the official CLI records this originator in session_meta.
            // Keep any existing launcher prefix and append a unique correlation marker.
            var prefix = environment.GetValueOrDefault("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
            environment["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"] = (string.IsNullOrEmpty(prefix) ? "Kooky Windows" : prefix) + "/Kooky:" + request.TrackingId;
        }
        var command = request.Command ?? profile.Command;
        if (string.IsNullOrWhiteSpace(command) || command.Contains('\0') || command.Length > 8192) throw new ArgumentException("Invalid Agent command.");
        var (executable, tail) = CommandResolver.SplitCommand(command);
        var path = CommandResolver.FindExecutable(executable, environment);
        if (path is null) throw new FileNotFoundException($"'{executable}' was not found. Install the official CLI or edit this Agent's command in Settings.");
        var sessionId = request.NativeSessionId;
        if (sessionId is not null && !Guid.TryParse(sessionId, out _)) throw new ArgumentException("The native session ID must be a UUID.");
        string additionalArguments = "";
        if (sessionId is not null)
        {
            additionalArguments = profile.Kind switch
            {
                "claude" => " --resume " + sessionId,
                "codex" => " resume " + sessionId,
                "gemini" => " --resume " + sessionId,
                _ when profile.ResumeArguments is { Length: > 0 } template => " " + template.Replace("{sessionId}", sessionId, StringComparison.Ordinal),
                _ => throw new InvalidOperationException("This Agent has no native session-resume integration.")
            };
        }
        else if (profile.Kind is "claude" or "gemini" && !Regex.IsMatch(tail, @"(?:^|\s)(?:--session-id|--resume|-r|--continue|-c)(?:\s|=|$)"))
        {
            sessionId = Guid.NewGuid().ToString();
            additionalArguments = " --session-id " + sessionId;
        }
        else
        {
            var existing = Regex.Match(tail, "(?:--session-id|--resume)\\s+['\"]?([0-9a-fA-F-]{36})");
            if (existing.Success && Guid.TryParse(existing.Groups[1].Value, out var id)) sessionId = id.ToString();
        }

        string commandLine;
        if (profile.Kind == "shell" && string.IsNullOrWhiteSpace(tail) && Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "powershell" or "pwsh")
        {
            // Session-local prompt integration, never modifies any PowerShell profile or Agent config.
            const string prompt = "$script:KookyOriginalPrompt=$function:prompt; function global:prompt { $p=$executionContext.SessionState.Path.CurrentFileSystemLocation.Path; [Console]::Write([char]27+']7;'+([uri]$p).AbsoluteUri+[char]7); & $script:KookyOriginalPrompt }";
            commandLine = CommandResolver.QuoteWindows(path) + " -NoLogo -NoProfile -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(prompt));
        }
        else if (string.IsNullOrWhiteSpace(tail) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            commandLine = CommandResolver.QuoteWindows(path) + additionalArguments;
        else if ((string.IsNullOrWhiteSpace(tail) || (profile.Id == "codex" && tail == "--no-daemon")) && Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat")
        {
            var cmd = System.Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe");
            commandLine = CommandResolver.QuoteWindows(cmd) + " /d /s /c \"" + CommandResolver.QuoteWindows(path) + " " + tail + additionalArguments + "\"";
        }
        else
        {
            // Custom commands are intentionally PowerShell expressions. Encoding keeps them out of cmd.exe's parser.
            var script = "$global:LASTEXITCODE=0; & " + CommandResolver.QuotePowerShell(path) + " " + tail + additionalArguments + "; $kookySucceeded=$?; if (!$kookySucceeded -and $LASTEXITCODE -eq 0) { exit 1 }; exit $LASTEXITCODE";
            commandLine = CommandResolver.PowerShellCommand(script, environment: environment);
        }
        return new LaunchSpec(commandLine, cwd, environment, sessionId);
    }

    public List<NativeSession> History(string agentId, string cwd)
    {
        var profile = List().Find(p => p.Id == agentId) ?? throw new ArgumentException("Unknown Agent profile.");
        return sessions.History(profile, Path.GetFullPath(cwd));
    }

    public string? DiscoverNativeSession(SessionInfo session)
    {
        if (session.NativeSessionId is not null) return session.NativeSessionId;
        var profile = List().Find(p => p.Id == session.AgentId);
        return profile is null ? null : sessions.Discover(profile, session);
    }

    private static List<AgentProfile> Defaults() =>
    [
        new() { Id = "shell", Name = "PowerShell", Command = CommandResolver.FindExecutable("pwsh.exe") is not null ? "pwsh.exe" : "powershell.exe", Kind = "shell", BuiltIn = true },
        new() { Id = "claude", Name = "Claude Code", Command = "claude", Kind = "claude", BuiltIn = true },
        new() { Id = "codex", Name = "Codex", Command = "codex --no-daemon", Kind = "codex", BuiltIn = true },
        new() { Id = "gemini", Name = "Gemini CLI", Command = "gemini", Kind = "gemini", BuiltIn = true }
    ];
}
