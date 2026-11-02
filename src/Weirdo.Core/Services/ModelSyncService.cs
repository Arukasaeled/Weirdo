using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weirdo.Core.Services;

internal sealed class ModelSyncService(string dataDirectory)
{
    private const int MaximumResponseBytes = 3 * 1024 * 1024;
    private const int MaximumSettingsBytes = 16 * 1024 * 1024;
    private static readonly string[] RouteVariables = ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY"];
    private sealed record GatewayModel(string Id, string Label, string? Description);

    public async Task<AgentModelSyncResult> SyncAsync(AgentProfile profile)
    {
        if (profile.Kind != "claude") throw new InvalidOperationException("网关模型同步仅适用于 Claude Code 配置。");
        var environment = CommandResolver.InheritedEnvironment();
        foreach (var (name, value) in profile.Env) environment[name] = value;
        var configDirectory = environment.GetValueOrDefault("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (!Path.IsPathRooted(configDirectory)) throw new InvalidOperationException("请为此 Agent 配置绝对路径的 CLAUDE_CONFIG_DIR。");
        configDirectory = Path.GetFullPath(configDirectory);
        var settingsPath = Path.Combine(configDirectory, "settings.json");
        ReadRouteFallback(settingsPath, environment);
        var address = ModelsAddress(environment.GetValueOrDefault("ANTHROPIC_BASE_URL"));
        var models = await ReadModelsAsync(address, environment).ConfigureAwait(false);
        var suggestions = models.Select(model => model.Id).ToArray();
        if (!OwnsSettings(configDirectory, settingsPath))
            return new(suggestions, false, "CLI 配置位于 Weirdo 的 agent-configs 管理目录之外。已读取候选模型，原生菜单需要在该配置中单独设置。");
        try
        {
            UpdatePicker(configDirectory, settingsPath, models);
            return new(suggestions, true, "已更新 Claude 原生模型菜单。新启动的 Claude 会话会读取此列表。");
        }
        catch (JsonException) { return new(suggestions, false, "已读取候选模型。Claude 配置包含无效 JSON，修复后可重试同步。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { return new(suggestions, false, "已读取候选模型。Claude 配置正在使用、已发生变化或无法写入，请稍后重试同步。"); }
    }

    private static void ReadRouteFallback(string settingsPath, Dictionary<string, string> environment)
    {
        if (!File.Exists(settingsPath)) return;
        try
        {
            var bytes = ReadFile(settingsPath);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("env", out var variables) || variables.ValueKind != JsonValueKind.Object) return;
            foreach (var name in RouteVariables)
                if (!environment.ContainsKey(name) && variables.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    environment[name] = value.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (!environment.ContainsKey("ANTHROPIC_BASE_URL")) throw new InvalidOperationException("无法从 Claude 配置读取网关地址，请检查此 Agent 的环境变量或配置文件。");
        }
    }

    private static Uri ModelsAddress(string? configured)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var origin) || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("请配置有效的 ANTHROPIC_BASE_URL 网关地址。");
        if (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback))
            throw new InvalidOperationException("网关模型读取需要 HTTPS，或本机回环地址的 HTTP 服务。");
        var basePath = origin.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(origin) { Path = basePath + (basePath.EndsWith("/v1", StringComparison.Ordinal) ? "/models" : "/v1/models"), Query = "", Fragment = "" };
        var result = builder.Uri;
        if (result.Scheme != origin.Scheme || result.Host != origin.Host || result.Port != origin.Port)
            throw new InvalidOperationException("模型地址必须与配置的网关属于同一来源。");
        return result;
    }

    private static async Task<List<GatewayModel>> ReadModelsAsync(Uri address, IReadOnlyDictionary<string, string> environment)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        try
        {
            if (environment.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token) && !string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (environment.TryGetValue("ANTHROPIC_API_KEY", out var key) && !string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-api-key", key);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        catch (FormatException) { throw new InvalidOperationException("配置的网关认证信息格式无效。"); }
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new InvalidOperationException("网关模型接口返回了重定向，请配置能够直接响应的网关地址。");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"网关模型接口返回 HTTP {status}。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException("网关模型列表超过 3 MB 限制。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes) throw new InvalidOperationException("网关模型列表超过 3 MB 限制。");
                buffer.Write(chunk, 0, count);
            }
            return ParseModels(buffer.ToArray());
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("读取网关模型超过 15 秒，请稍后重试。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接当前配置的网关模型接口。"); }
        catch (JsonException) { throw new InvalidOperationException("网关返回了无效的模型列表 JSON。"); }
    }

    private static List<GatewayModel> ParseModels(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("网关模型列表需要包含 data 数组。");
        var models = new List<GatewayModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.EnumerateArray())
        {
            var id = String(entry, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 2048 || id.Any(char.IsControl))
                throw new InvalidOperationException("网关模型列表包含无效的模型 ID。");
            if (!seen.Add(id)) continue;
            var label = SingleLine(String(entry, "display_name") ?? String(entry, "name"), 256) ?? id;
            var description = SingleLine(String(entry, "description"), 512);
            models.Add(new(id, label, description));
        }
        if (models.Count == 0) throw new InvalidOperationException("网关返回了空模型列表。");
        return models;
    }

    private bool OwnsSettings(string configDirectory, string settingsPath)
    {
        var owned = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataDirectory, "agent-configs")));
        var config = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        if (!config.Equals(owned, StringComparison.OrdinalIgnoreCase) && !config.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Directory.Exists(config)) return false;
        var path = Path.GetPathRoot(config)!;
        foreach (var part in config[path.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return !File.Exists(settingsPath) || (File.GetAttributes(settingsPath) & FileAttributes.ReparsePoint) == 0;
    }

    private void UpdatePicker(string configDirectory, string settingsPath, List<GatewayModel> models)
    {
        var mutexName = "Local\\Weirdo.ModelPicker." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsPath.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, mutexName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
        catch (AbandonedMutexException) { }
        try
        {
            if (!OwnsSettings(configDirectory, settingsPath)) throw new IOException("Settings ownership changed.");
            var original = File.Exists(settingsPath) ? ReadFile(settingsPath) : null;
            var root = original is null ? new JsonObject() : JsonNode.Parse(original) as JsonObject ?? throw new JsonException();
            var picker = root["modelPicker"] as JsonObject ?? new JsonObject();
            var options = new JsonArray();
            foreach (var model in models)
            {
                var option = new JsonObject { ["model"] = model.Id, ["label"] = model.Label };
                if (model.Description is not null) option["description"] = model.Description;
                options.Add((JsonNode)option);
            }
            picker["options"] = options;
            picker["replaceBuiltInOptions"] = true;
            if (picker.Parent is null) root["modelPicker"] = picker;
            var replacement = JsonSerializer.SerializeToUtf8Bytes(root, CoreJsonContext.Default.JsonNode);
            if (replacement.Length > MaximumSettingsBytes) throw new IOException("Settings exceed the size limit.");
            PublishSettings(settingsPath, original, replacement, () => OwnsSettings(configDirectory, settingsPath));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void PublishSettings(string path, byte[]? original, byte[] replacement, Func<bool> owned)
    {
        var backup = path + ".bak";
        var encrypted = Encrypted(path) || Encrypted(backup);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupStage = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteStage(temporary, replacement, encrypted);
            if (original is not null) WriteStage(backupStage, original, encrypted);
            if (!owned()) throw new IOException("Settings ownership changed.");
            if (original is null ? File.Exists(path) : !File.Exists(path) || !ReadFile(path).AsSpan().SequenceEqual(original))
                throw new IOException("Settings changed during synchronization.");
            if (original is not null) File.Move(backupStage, backup, overwrite: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            foreach (var stage in new[] { temporary, backupStage })
                try { if (File.Exists(stage)) File.Delete(stage); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteStage(string path, byte[] bytes, bool encrypted)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough | (encrypted ? FileOptions.Encrypted : FileOptions.None));
        if (encrypted && !Encrypted(path)) throw new IOException("Settings encryption could not be preserved.");
        file.Write(bytes);
        file.Flush(true);
        using var document = JsonDocument.Parse(bytes);
    }

    private static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumSettingsBytes) throw new IOException("Settings exceed the size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static bool Encrypted(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;
    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? SingleLine(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = string.Join(" ", new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text[..Math.Min(text.Length, maximum)];
    }
}
