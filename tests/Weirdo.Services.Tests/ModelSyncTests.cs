using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weirdo.Core;
using Weirdo.Core.Services;

internal static class ModelSyncTests
{
    private const string Catalog = "{\"data\":[{\"id\":\"codex/gpt-6-astra\",\"display_name\":\"Gateway\\nModel\"},{\"id\":\"deepseek/deepseek-v4-flash\"},{\"id\":\"custom/model[1m]\"},{\"id\":\"codex/gpt-6-astra\"}]}";
    private const string OriginalSettings = "{\"theme\":\"dark\",\"model\":\"keep/current\",\"env\":{\"PRIVATE_TEST_VALUE\":\"keep-private\"},\"permissions\":{\"defaultMode\":\"default\"},\"modelPicker\":{\"options\":[{\"model\":\"stale/alias\"}],\"replaceBuiltInOptions\":false}}";

    public static async Task RunAsync(Action<bool, string> check, string fixtureRoot)
    {
        await using var server = new LocalServer(Catalog);
        var appData = Path.Combine(fixtureRoot, "sync-app-data");
        var ownedConfig = Path.Combine(appData, "agent-configs", "claude");
        Directory.CreateDirectory(ownedConfig);
        var settingsPath = Path.Combine(ownedConfig, "settings.json");
        File.WriteAllText(settingsPath, OriginalSettings);
        var original = File.ReadAllBytes(settingsPath);
        var catalog = new AgentCatalog(appData);
        var profile = Profile(ownedConfig, server.Address);
        catalog.Save([profile]);
        var result = await catalog.SyncModelsAsync(profile.Id);
        check(result.NativePickerUpdated && result.Suggestions.SequenceEqual(new[] { "codex/gpt-6-astra", "deepseek/deepseek-v4-flash", "custom/model[1m]" }),
            "explicit gateway sync retains non-Claude IDs and exact context suffixes");
        var request = server.Requests.Single();
        check(request.Method == "GET" && request.Path == "/v1/models" && request.Authorization == "Bearer fixture-token" && request.ApiKey == "fixture-api-key",
            "model sync uses configured-origin GET with both credential headers");
        using (var settings = JsonDocument.Parse(File.ReadAllBytes(settingsPath)))
        {
            var root = settings.RootElement;
            check(root.GetProperty("theme").GetString() == "dark" && root.GetProperty("model").GetString() == "keep/current"
                && root.GetProperty("env").GetProperty("PRIVATE_TEST_VALUE").GetString() == "keep-private"
                && root.GetProperty("permissions").GetProperty("defaultMode").GetString() == "default", "picker sync preserves unrelated settings and selected model");
            var picker = root.GetProperty("modelPicker");
            check(picker.GetProperty("replaceBuiltInOptions").GetBoolean() && picker.GetProperty("options").GetArrayLength() == 3
                && picker.GetProperty("options")[0].GetProperty("model").GetString() == "codex/gpt-6-astra"
                && picker.GetProperty("options")[0].GetProperty("label").GetString() == "Gateway Model", "native picker writes documented model rows and server labels");
        }
        check(original.SequenceEqual(File.ReadAllBytes(settingsPath + ".bak")) && Directory.GetFiles(ownedConfig, "*.tmp").Length == 0,
            "picker sync keeps exact backup and cleans atomic staging files");
        var stable = File.ReadAllBytes(settingsPath);
        var external = Path.Combine(fixtureRoot, "external-sync-config");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "settings.json"), OriginalSettings);
        catalog.Save([profile with { Env = EnvironmentFor(external, server.Address) }]);
        var externalResult = await catalog.SyncModelsAsync(profile.Id);
        check(!externalResult.NativePickerUpdated && externalResult.Suggestions.Length == 3 && !string.IsNullOrEmpty(externalResult.Note)
            && File.ReadAllText(Path.Combine(external, "settings.json")) == OriginalSettings, "external CLI config receives suggestions without file updates");
        var lookalike = Path.Combine(appData, "agent-configs-other", "claude");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, "settings.json"), OriginalSettings);
        catalog.Save([profile with { Env = EnvironmentFor(lookalike, server.Address) }]);
        check(!(await catalog.SyncModelsAsync(profile.Id)).NativePickerUpdated, "ownership check rejects similarly named directories");
        var junction = Path.Combine(appData, "agent-configs", "linked");
        await CreateJunction(junction, external);
        try
        {
            catalog.Save([profile with { Env = EnvironmentFor(junction, server.Address) }]);
            check(!(await catalog.SyncModelsAsync(profile.Id)).NativePickerUpdated && File.ReadAllText(Path.Combine(external, "settings.json")) == OriginalSettings,
                "ownership check rejects junctions to external configuration");
        }
        finally { Directory.Delete(junction); }

        var routeKeys = new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY" };
        var inherited = routeKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in routeKeys) Environment.SetEnvironmentVariable(key, null);
            var fallback = Path.Combine(fixtureRoot, "fallback-config");
            Directory.CreateDirectory(fallback);
            var config = new JsonObject { ["env"] = new JsonObject { ["ANTHROPIC_BASE_URL"] = server.Address, ["ANTHROPIC_AUTH_TOKEN"] = "settings-token", ["ANTHROPIC_API_KEY"] = "settings-key" } };
            File.WriteAllText(Path.Combine(fallback, "settings.json"), config.ToJsonString());
            catalog.Save([profile with { Env = new() { ["CLAUDE_CONFIG_DIR"] = fallback } }]);
            await catalog.SyncModelsAsync(profile.Id);
            check(server.Requests.Last().Authorization == "Bearer settings-token" && server.Requests.Last().ApiKey == "settings-key", "native settings env supplies configured route and credentials");
        }
        finally { foreach (var (key, value) in inherited) Environment.SetEnvironmentVariable(key, value); }

        catalog.Save([profile]);
        await using (var redirectTarget = new LocalServer(Catalog))
        {
            server.Response = new(302, "", "Location: " + redirectTarget.Address + "/v1/models\r\n");
            await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "重定向", check, "gateway redirect is rejected");
            check(redirectTarget.Requests.IsEmpty, "credentials never reach a redirect destination");
        }
        server.Response = new(200, new string('x', 3 * 1024 * 1024 + 1));
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "3 MB", check, "declared oversized gateway response is rejected");
        server.Response = new(200, new string('x', 3 * 1024 * 1024 + 1), OmitLength: true);
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "3 MB", check, "streamed response is bounded without Content-Length");
        server.Response = new(200, "{\"data\":[]}");
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "空模型", check, "empty model catalog cannot erase native picker");
        server.Response = new(401, "must-not-display-server-body");
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "HTTP 401", check, "authentication error exposes status without server body");
        check(stable.SequenceEqual(File.ReadAllBytes(settingsPath)), "failed gateway requests preserve synchronized settings");
        var requestCount = server.Requests.Count;
        catalog.Save([profile with { Env = EnvironmentFor(ownedConfig, "http://example.invalid") }]);
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "HTTPS", check, "remote plaintext endpoint is rejected before network access");
        check(server.Requests.Count == requestCount, "invalid route validation makes no request");
        server.Response = new(200, Catalog);
        await VerifyEncryptedUpdate(check, server);
        catalog.Save([profile]);
        server.Response = new(200, Catalog, Delay: TimeSpan.FromSeconds(30));
        var elapsed = Stopwatch.StartNew();
        await RejectAsync(() => catalog.SyncModelsAsync(profile.Id), "15 秒", check, "gateway model read has a fifteen-second deadline");
        check(elapsed.Elapsed < TimeSpan.FromSeconds(20), "gateway deadline bounds complete request");
    }

    private static async Task VerifyEncryptedUpdate(Action<bool, string> check, LocalServer server)
    {
        var allowed = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Weirdo-tests"));
        var root = Path.Combine(allowed, "model-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "agent-configs", "claude");
            Directory.CreateDirectory(config);
            var settings = Path.Combine(config, "settings.json");
            File.WriteAllText(settings, "{\"theme\":\"dark\"}");
            var encrypted = (File.GetAttributes(settings) & FileAttributes.Encrypted) != 0;
            var catalog = new AgentCatalog(root);
            catalog.Save([Profile(config, server.Address)]);
            var first = await catalog.SyncModelsAsync("gateway");
            var second = await catalog.SyncModelsAsync("gateway");
            check(first.NativePickerUpdated && second.NativePickerUpdated && File.Exists(settings + ".bak"), "picker supports consecutive atomic LocalApplicationData saves");
            check(!encrypted || (File.GetAttributes(settings) & FileAttributes.Encrypted) != 0 && (File.GetAttributes(settings + ".bak") & FileAttributes.Encrypted) != 0,
                "picker updates retain inherited EFS encryption");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static AgentProfile Profile(string config, string address) => new() { Id = "gateway", Name = "Gateway test", Kind = "claude", Command = "claude", Env = EnvironmentFor(config, address) };
    private static Dictionary<string, string> EnvironmentFor(string config, string address) => new()
    {
        ["CLAUDE_CONFIG_DIR"] = config, ["ANTHROPIC_BASE_URL"] = address, ["ANTHROPIC_AUTH_TOKEN"] = "fixture-token", ["ANTHROPIC_API_KEY"] = "fixture-api-key"
    };
    private static async Task RejectAsync(Func<Task<AgentModelSyncResult>> operation, string message, Action<bool, string> check, string label)
    {
        try { await operation(); }
        catch (InvalidOperationException error) { check(error.Message.Contains(message, StringComparison.Ordinal), label); return; }
        throw new Exception("FAILED " + label);
    }
    private static async Task CreateJunction(string path, string target)
    {
        using var process = new Process { StartInfo = new(Environment.GetEnvironmentVariable("COMSPEC")!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", path, target }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Could not create owned junction fixture.");
    }

    private sealed record Reply(int Status, string Body, string ExtraHeaders = "", bool OmitLength = false, TimeSpan Delay = default);
    private sealed record Request(string Method, string Path, string? Authorization, string? ApiKey);
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task server;
        public string Address { get; }
        public ConcurrentQueue<Request> Requests { get; } = new();
        public Reply Response { get; set; }
        public LocalServer(string body)
        {
            Response = new(200, body);
            listener.Start();
            Address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            server = RunAsync();
        }
        private async Task RunAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var first = (await reader.ReadLineAsync(stop.Token))?.Split(' ') ?? [];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                    }
                    Requests.Enqueue(new(first[0], first[1], headers.GetValueOrDefault("Authorization"), headers.GetValueOrDefault("x-api-key")));
                    var reply = Response;
                    if (reply.Delay > TimeSpan.Zero) await Task.Delay(reply.Delay, stop.Token);
                    var bytes = Encoding.UTF8.GetBytes(reply.Body);
                    var head = $"HTTP/1.1 {reply.Status} Test\r\nContent-Type: application/json\r\nConnection: close\r\n" + (reply.OmitLength ? "" : $"Content-Length: {bytes.Length}\r\n") + reply.ExtraHeaders + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                    await stream.WriteAsync(bytes, stop.Token);
                    await stream.FlushAsync(stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (IOException) { }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await server;
            stop.Dispose();
        }
    }
}
