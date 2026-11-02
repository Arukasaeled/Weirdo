using System.Text.Json;

namespace Weirdo.Core.Services;

internal static class ClaudeModelSuggestions
{
    public static AgentModelChoices Read(AgentProfile profile)
    {
        if (profile.Kind != "claude") return new([], null);
        var values = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && value.IndexOfAny(['\0', '\r', '\n']) < 0 && !values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        var environment = CommandResolver.InheritedEnvironment();
        foreach (var (key, value) in profile.Env) environment[key] = value;
        Add(profile.Model);
        foreach (var (key, value) in environment.Where(item => IsModelVariable(item.Key))) Add(value);
        var inheritedModel = environment.GetValueOrDefault("ANTHROPIC_MODEL");
        var configDirectory = environment.GetValueOrDefault("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (Path.IsPathRooted(configDirectory))
        {
            var settings = Path.Combine(configDirectory, "settings.json");
            try
            {
                if (File.Exists(settings))
                {
                    using var stream = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length <= 16 * 1024 * 1024)
                    {
                        using var document = JsonDocument.Parse(stream);
                        var root = document.RootElement;
                        var configuredModel = String(root, "model");
                        Add(configuredModel);
                        if (root.TryGetProperty("env", out var settingsEnvironment) && settingsEnvironment.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in settingsEnvironment.EnumerateObject())
                                if (IsModelVariable(entry.Name) && entry.Value.ValueKind == JsonValueKind.String) Add(entry.Value.GetString());
                            inheritedModel ??= String(settingsEnvironment, "ANTHROPIC_MODEL");
                        }
                        inheritedModel ??= configuredModel;
                        if (root.TryGetProperty("modelPicker", out var picker) && picker.ValueKind == JsonValueKind.Object
                            && picker.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var option in options.EnumerateArray().Take(512))
                                Add(option.ValueKind == JsonValueKind.String ? option.GetString() : String(option, "value") ?? String(option, "model") ?? String(option, "id"));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        inheritedModel ??= environment.GetValueOrDefault("ANTHROPIC_DEFAULT_MODEL");
        Add(inheritedModel);
        return new(values.ToArray(), string.IsNullOrWhiteSpace(inheritedModel) ? null : inheritedModel);
    }

    private static bool IsModelVariable(string name) => name.Equals("ANTHROPIC_MODEL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ANTHROPIC_DEFAULT_MODEL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ANTHROPIC_CUSTOM_MODEL_OPTION", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ANTHROPIC_SMALL_FAST_MODEL", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ANTHROPIC_DEFAULT_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_MODEL", StringComparison.OrdinalIgnoreCase);

    private static string? String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
