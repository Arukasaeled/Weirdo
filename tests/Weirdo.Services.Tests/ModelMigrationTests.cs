using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weirdo.Core;
using Weirdo.Core.Services;

internal static class ModelMigrationTests
{
    private static readonly byte[] LegacyAgentEntropy = Encoding.UTF8.GetBytes("Kooky.Windows.AgentEnvironment.v1");
    private static readonly byte[] AgentEntropy = Encoding.UTF8.GetBytes("Weirdo.Windows.AgentEnvironment.v1");
    private static readonly byte[] SessionEntropy = Encoding.UTF8.GetBytes("Weirdo.Windows.SessionRequests.v1");

    public static async Task RunAsync(Action<bool, string> check, string fixtureRoot)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Weirdo.Services.Tests.exe");
        var catalog = new AgentCatalog(Path.Combine(fixtureRoot, "model-profiles"));
        const string model = "gateway/模型 with spaces & $literal \"quote\"\\";
        var command = CommandResolver.QuoteWindows(executable) + " --native-launch-fixture --custom \"a b\" --model old/first --model=old/second -- \"--model\" \"prompt literal\"";
        catalog.Save([new AgentProfile { Id = "model-fixture", Name = "Model fixture", Kind = "claude", Command = command, Model = model,
            Env = new() { ["ANTHROPIC_MODEL"] = "env/profile", ["ANTHROPIC_AUTH_TOKEN"] = "test-auth-value", ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:1", ["ANTHROPIC_CUSTOM_MODEL_OPTION"] = "keep/custom", ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "0" } }]);
        var profile = catalog.List().Single(item => item.Id == "model-fixture");
        check(profile.Model == model && !profile.Command.Contains("old/first") && !profile.Command.Contains("old/second") && profile.Command.EndsWith("-- \"--model\" \"prompt literal\"", StringComparison.Ordinal),
            "explicit arbitrary model wins duplicate legacy flags while preserving positional arguments");
        var nativeId = Guid.NewGuid().ToString();
        var launch = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = profile.Id, NativeSessionId = nativeId, Env = new() { ["ANTHROPIC_MODEL"] = "env/request" } });
        check(launch.Environment["ANTHROPIC_MODEL"] == model && launch.Environment["ANTHROPIC_AUTH_TOKEN"] == "test-auth-value"
            && launch.Environment["ANTHROPIC_BASE_URL"] == "http://127.0.0.1:1" && launch.Environment["ANTHROPIC_CUSTOM_MODEL_OPTION"] == "keep/custom"
            && launch.Environment["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] == "0", "explicit profile model overrides environment while retaining configured route and picker variables");
        var arguments = await RunFixture(launch);
        check(arguments.SequenceEqual(new[] { "--custom", "a b", "--model", model, "--resume", nativeId, "--", "--model", "prompt literal" }),
            "real native EXE receives one quoted model option before the argument delimiter");
        catalog.Save([profile with { Model = null }]);
        var inherited = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = profile.Id });
        check(catalog.List().Single(item => item.Id == profile.Id).Model is null && inherited.Environment["ANTHROPIC_MODEL"] == "env/profile", "clearing model restores environment inheritance");
        var plainCommand = CommandResolver.QuoteWindows(executable) + " --native-launch-fixture";
        catalog.Save([profile with { Command = plainCommand, Model = "default" }]);
        var defaultLaunch = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = profile.Id });
        var defaultArguments = await RunFixture(defaultLaunch);
        check(defaultArguments.Take(2).SequenceEqual(new[] { "--model", "default" }) && defaultLaunch.Environment["ANTHROPIC_MODEL"] == "default", "explicit default remains a native Claude model selection");
        catalog.Save([profile with { Command = plainCommand, Model = null }]);
        var manual = catalog.Resolve(new() { Cwd = fixtureRoot, AgentId = profile.Id, Command = plainCommand + " --model manual/request" });
        var manualArguments = await RunFixture(manual);
        check(manual.Environment["ANTHROPIC_MODEL"] == "manual/request" && manualArguments.Count(value => value == "--model") == 1, "manual request model is normalized when the profile inherits");

        var legacyEnvironment = new Dictionary<string, string> { ["ANTHROPIC_MODEL"] = "legacy/environment", ["ANTHROPIC_AUTH_TOKEN"] = "legacy-test-token" };
        var stored = new List<StoredAgentProfile> { new("claude", "Claude Code", "\"C:\\Program Files\\Claude\\claude.exe\" --flag \"kept value\" --model \"legacy/model id\"", "claude", null, true, Protect(legacyEnvironment, LegacyAgentEntropy)) };
        var legacyDirectory = Path.Combine(fixtureRoot, "legacy-ciphertext");
        var legacyStore = new StateStore(legacyDirectory);
        legacyStore.Save("agents", stored, CoreJsonContext.Default.ListStoredAgentProfile);
        var originalBytes = File.ReadAllBytes(Path.Combine(legacyDirectory, "agents.json"));
        var legacyCatalog = new AgentCatalog(legacyDirectory);
        var readLegacy = legacyCatalog.List().Single(item => item.Id == "claude");
        check(readLegacy.Model == "legacy/model id" && readLegacy.Command == "\"C:\\Program Files\\Claude\\claude.exe\" --flag \"kept value\""
            && readLegacy.Env["ANTHROPIC_AUTH_TOKEN"] == "legacy-test-token", "legacy encrypted profile loads and migrates quoted command model in memory");
        check(originalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(legacyDirectory, "agents.json"))), "reading a legacy profile preserves its source bytes");
        legacyCatalog.Save(legacyCatalog.List());
        var rewritten = legacyStore.Load("agents")!.Value.Deserialize(CoreJsonContext.Default.ListStoredAgentProfile)!.Single(item => item.Id == "claude");
        check(Unprotect(rewritten.ProtectedEnvironment!, AgentEntropy)["ANTHROPIC_AUTH_TOKEN"] == "legacy-test-token" && rewritten.Model == "legacy/model id", "saving legacy profile reencrypts environment with Weirdo entropy");

        var shellCommand = "powershell.exe -NoProfile -Command \"claude --model script/model\"";
        catalog.Save([new AgentProfile { Id = "script", Name = "Script", Kind = "claude", Command = shellCommand, Model = "field/model" }]);
        check(catalog.List().Single(item => item.Id == "script").Command == shellCommand, "model migration preserves explicit shell script text");
        var settingsDirectory = Path.Combine(fixtureRoot, "model-config");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "settings.json"), "{\"model\":\"configured/default\",\"env\":{\"ANTHROPIC_DEFAULT_OPUS_MODEL\":\"configured/opus\",\"ANTHROPIC_AUTH_TOKEN\":\"must-not-leak\"},\"modelPicker\":{\"options\":[{\"value\":\"gateway/choice[1m]\",\"label\":\"Choice\"},{\"value\":\"gateway/other\"}]}}");
        catalog.Save([new AgentProfile { Id = "suggestions", Name = "Suggestions", Kind = "claude", Command = "claude", Env = new() { ["CLAUDE_CONFIG_DIR"] = settingsDirectory, ["ANTHROPIC_MODEL"] = "profile/default" } }]);
        var choices = catalog.Models("suggestions");
        check(choices.InheritedModel == "profile/default" && choices.Suggestions.Contains("gateway/choice[1m]") && choices.Suggestions.Contains("configured/opus")
            && !choices.Suggestions.Contains("must-not-leak"), "local model suggestions retain exact configured IDs and exclude credential values");
        VerifyMigration(check, fixtureRoot);
    }

    private static void VerifyMigration(Action<bool, string> check, string fixtureRoot)
    {
        var source = Path.Combine(fixtureRoot, "old-app-data");
        var destination = Path.Combine(fixtureRoot, "new-app-data");
        var oldWorkspace = Path.Combine(fixtureRoot, "old-workspace");
        var newWorkspace = Path.Combine(fixtureRoot, "new-workspace");
        Directory.CreateDirectory(oldWorkspace);
        Directory.CreateDirectory(newWorkspace);
        var oldConfig = Path.Combine(source, "agent-configs", "claude");
        var projectHistory = Path.Combine(oldConfig, "projects", "legacy-project");
        Directory.CreateDirectory(projectHistory);
        var nativeId = Guid.NewGuid().ToString();
        var historyPath = Path.Combine(projectHistory, nativeId + ".jsonl");
        File.WriteAllText(historyPath, JsonSerializer.Serialize(new { type = "user", cwd = oldWorkspace, sessionId = nativeId, timestamp = DateTimeOffset.UtcNow, message = "private-fixture" }));
        File.WriteAllText(Path.Combine(oldConfig, "settings.json"), "{\"modelPicker\":{\"options\":[{\"value\":\"keep/model\"}]}}");
        var externalConfig = Path.Combine(fixtureRoot, "external-user-config");
        Directory.CreateDirectory(externalConfig);
        File.WriteAllText(Path.Combine(externalConfig, "settings.json"), "{\"external\":true}");
        var sourceStore = new StateStore(source);
        var storedAgents = new List<StoredAgentProfile>
        {
            new("claude", "Claude", "claude --model gateway/legacy", "claude", null, true,
                Protect(new() { ["CLAUDE_CONFIG_DIR"] = oldConfig, ["ANTHROPIC_AUTH_TOKEN"] = "migration-test-token" }, LegacyAgentEntropy)),
            new("external", "External", "claude", "claude", null, false, Protect(new() { ["CLAUDE_CONFIG_DIR"] = externalConfig }, LegacyAgentEntropy))
        };
        sourceStore.Save("agents", storedAgents, CoreJsonContext.Default.ListStoredAgentProfile);
        sourceStore.Save("ui", JsonNode.Parse(JsonSerializer.Serialize(new { workspaces = new[] { new { path = oldWorkspace }, new { path = externalConfig } } }))!);
        var session = new JsonArray
        {
            new JsonObject
            {
                ["info"] = new JsonObject { ["id"] = "saved", ["cwd"] = oldWorkspace, ["nativeSessionId"] = nativeId, ["agentId"] = "claude" },
                ["request"] = new JsonObject { ["cwd"] = oldWorkspace, ["env"] = new JsonObject { ["CLAUDE_CONFIG_DIR"] = oldConfig } }
            }
        };
        var sessionBytes = JsonSerializer.SerializeToUtf8Bytes(session, CoreJsonContext.Default.JsonNode);
        sourceStore.Save("sessions", new JsonObject { ["encrypted"] = Convert.ToBase64String(ProtectedData.Protect(sessionBytes, null, DataProtectionScope.CurrentUser)) });
        var sourceHashes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var result = MigrationService.Migrate(source, destination, oldWorkspace, newWorkspace);
        check(result.Completed && result.CopiedFiles >= 5 && result.Warnings.Length == 0, "one-time migration copies owned state and complete native configuration history");
        check(sourceHashes.All(item => item.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(item.Key)))), "migration preserves every original source file byte");
        var migratedCatalog = new AgentCatalog(destination);
        var migratedClaude = migratedCatalog.List().Single(item => item.Id == "claude");
        var newConfig = Path.Combine(destination, "agent-configs", "claude");
        check(migratedClaude.Model == "gateway/legacy" && migratedClaude.Env["CLAUDE_CONFIG_DIR"] == newConfig && migratedClaude.Env["ANTHROPIC_AUTH_TOKEN"] == "migration-test-token",
            "migration remaps owned configuration and preserves decryptable credentials and model");
        check(migratedCatalog.List().Single(item => item.Id == "external").Env["CLAUDE_CONFIG_DIR"] == externalConfig, "migration retains external user configuration paths");
        check(File.ReadAllBytes(historyPath).SequenceEqual(File.ReadAllBytes(Path.Combine(newConfig, "projects", "legacy-project", nativeId + ".jsonl")))
            && migratedCatalog.History("claude", oldWorkspace).Single().Id == nativeId, "copied native transcript remains byte-identical and discoverable under its original cwd");
        var destinationStore = new StateStore(destination);
        check(destinationStore.Load("ui")!.Value.GetProperty("workspaces")[0].GetProperty("path").GetString() == newWorkspace, "owned UI workspace maps to the existing new project root");
        var cipher = destinationStore.Load("sessions")!.Value.GetProperty("encrypted").GetString()!;
        var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(cipher), SessionEntropy, DataProtectionScope.CurrentUser);
        using (var sessions = JsonDocument.Parse(decrypted))
        {
            var saved = sessions.RootElement[0];
            check(saved.GetProperty("info").GetProperty("cwd").GetString() == oldWorkspace && saved.GetProperty("info").GetProperty("nativeSessionId").GetString() == nativeId
                && saved.GetProperty("request").GetProperty("env").GetProperty("CLAUDE_CONFIG_DIR").GetString() == newConfig,
                "session migration keeps native conversation cwd and ID while remapping owned config");
        }
        CryptographicOperations.ZeroMemory(decrypted);
        File.WriteAllText(Path.Combine(source, "agent-configs", "late-file.txt"), "late-source-change");
        var repeated = MigrationService.Migrate(source, destination, oldWorkspace, newWorkspace);
        check(repeated.Completed && repeated.CopiedFiles == 0 && !File.Exists(Path.Combine(destination, "agent-configs", "late-file.txt")), "completed migration runs once and preserves destination state");
        var existingDestination = Path.Combine(fixtureRoot, "existing-app-data");
        Directory.CreateDirectory(Path.Combine(existingDestination, "agent-configs", "claude"));
        var existingStore = new StateStore(existingDestination);
        existingStore.Save("ui", new JsonObject { ["userChoice"] = "new-user-state" });
        var existingSettings = Path.Combine(existingDestination, "agent-configs", "claude", "settings.json");
        File.WriteAllText(existingSettings, "{\"newConfig\":true}");
        var existingUiBytes = File.ReadAllBytes(Path.Combine(existingDestination, "ui.json"));
        var existingSettingsBytes = File.ReadAllBytes(existingSettings);
        var partial = MigrationService.Migrate(source, existingDestination, oldWorkspace, newWorkspace);
        check(partial.Completed && partial.SkippedFiles >= 2 && existingUiBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(existingDestination, "ui.json")))
            && existingSettingsBytes.SequenceEqual(File.ReadAllBytes(existingSettings)), "retry or partial destination migration never overwrites existing user state or config");
    }

    private static string Protect(Dictionary<string, string> values, byte[] entropy)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values, CoreJsonContext.Default.DictionaryStringString);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static Dictionary<string, string> Unprotect(string value, byte[] entropy)
    {
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize(bytes, CoreJsonContext.Default.DictionaryStringString)!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<string[]> RunFixture(LaunchSpec launch)
    {
        var (executable, arguments) = CommandResolver.SplitCommand(launch.CommandLine);
        using var process = new Process { StartInfo = new(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = launch.Cwd } };
        foreach (var (name, value) in launch.Environment) process.StartInfo.Environment[name] = value;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != 17 || (await error).Length != 0) throw new Exception("Native model argument fixture failed.");
        using var document = JsonDocument.Parse(await output);
        return document.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
