using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Weirdo.Core.Services;

/// <summary>
/// Read-only adapters for official CLI metadata. Gemini .project_root/header handling
/// is adapted from Kooky AgentSessionStores.swift (MIT, copyright 2026 kooky contributors).
/// Windows port: bounded metadata reads, no conversation text extraction, strict cwd matching.
/// </summary>
internal sealed class NativeSessionReader
{
    private sealed record Entry(NativeSession Session, DateTimeOffset? StartedAt, bool Interactive, string? Originator = null);

    public List<NativeSession> History(AgentProfile profile, string cwd) => Read(profile, cwd).Select(x => x.Session).DistinctBy(x => x.Id).OrderByDescending(x => x.ModifiedAt).Take(200).ToList();

    public string? Discover(AgentProfile profile, SessionInfo session)
    {
        // Claude/Gemini receive an exact UUID at launch, so discovery is only needed for Codex.
        if (profile.Kind != "codex" || !Guid.TryParseExact(session.TrackingId, "N", out _)) return null;
        var marker = "/Weirdo:" + session.TrackingId;
        var legacyMarker = "/Kooky:" + session.TrackingId;
        // Onboarding, auth and workspace trust can delay metadata creation arbitrarily.
        // Exact launch identity replaces timing/cwd inference and distinguishes parallel CLIs.
        var candidates = Read(profile, cwd: null)
            .Where(x => x.Interactive && (x.Originator?.EndsWith(marker, StringComparison.Ordinal) == true
                || x.Originator?.EndsWith(legacyMarker, StringComparison.Ordinal) == true))
            .Select(x => x.Session.Id).Distinct().Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private IEnumerable<Entry> Read(AgentProfile profile, string? cwd)
    {
        var user = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string Config(string name, string fallback) => profile.Env.TryGetValue(name, out var custom) ? custom : System.Environment.GetEnvironmentVariable(name) ?? fallback;
        var root = profile.Kind switch
        {
            "codex" => Path.Combine(Config("CODEX_HOME", Path.Combine(user, ".codex")), "sessions"),
            "claude" => Path.Combine(Config("CLAUDE_CONFIG_DIR", Path.Combine(user, ".claude")), "projects"),
            "gemini" => Path.Combine(Config("GEMINI_CLI_HOME", user), ".gemini", "tmp"),
            _ => null
        };
        if (root is null || !Directory.Exists(root)) return [];
        try
        {
            if (profile.Kind == "gemini") return cwd is null ? [] : ReadGemini(profile, root, cwd).ToList();
            var files = SafeFiles(root, "*.jsonl").Where(f => profile.Kind != "claude" || !f.FullName.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(600);
            var result = new List<Entry>();
            foreach (var file in files)
            {
                var entry = profile.Kind == "codex" ? ReadCodex(file, profile.Id) : ReadClaude(file, profile.Id);
                if (entry is not null && (cwd is null || SamePath(entry.Session.Cwd, cwd))) result.Add(entry);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static Entry? ReadCodex(FileInfo file, string agentId)
    {
        try
        {
            var line = HeadLines(file.FullName, maxLines: 1).FirstOrDefault();
            if (line is null) return null;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (String(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload)) return null;
            var id = String(payload, "id") ?? String(payload, "session_id");
            var cwd = String(payload, "cwd");
            if (!Guid.TryParse(id, out _) || string.IsNullOrEmpty(cwd)) return null;
            var started = Date(String(payload, "timestamp") ?? String(root, "timestamp"));
            var source = String(payload, "source");
            // Excludes app, exec and subagent transcripts: only native interactive CLI sessions.
            var interactive = source == "cli";
            return new(new(id!, agentId, cwd, file.LastWriteTimeUtc, null), started, interactive, String(payload, "originator"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static Entry? ReadClaude(FileInfo file, string agentId)
    {
        try
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (!Guid.TryParse(id, out _)) return null;
            foreach (var line in HeadLines(file.FullName, 40))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var cwd = String(root, "cwd");
                var sessionId = String(root, "sessionId");
                if (cwd is null || sessionId != id) continue;
                if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) return null;
                return new(new(id, agentId, cwd, file.LastWriteTimeUtc, null), Date(String(root, "timestamp")), true);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static IEnumerable<Entry> ReadGemini(AgentProfile profile, string root, string cwd)
    {
        foreach (var project in Directory.EnumerateDirectories(root).Take(1000))
        {
            var marker = Path.Combine(project, ".project_root");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 32768) continue;
            string actualCwd;
            try { actualCwd = File.ReadAllText(marker).Trim(); }
            catch (IOException) { continue; }
            if (!SamePath(actualCwd, cwd)) continue;
            foreach (var file in SafeFiles(Path.Combine(project, "chats"), "session-*.json*").OrderByDescending(x => x.LastWriteTimeUtc).Take(200))
            {
                Entry? entry = null;
                try
                {
                    var line = HeadLines(file.FullName, 1).FirstOrDefault();
                    if (line is null) continue;
                    using var document = JsonDocument.Parse(line);
                    var header = document.RootElement;
                    var id = String(header, "sessionId");
                    var kind = String(header, "kind");
                    if (Guid.TryParse(id, out _) && (kind is null or "main"))
                        entry = new(new(id!, profile.Id, actualCwd, file.LastWriteTimeUtc, null), Date(String(header, "startTime")), true);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
                if (entry is not null) yield return entry;
            }
        }
    }

    private static IEnumerable<FileInfo> SafeFiles(string root, string pattern)
    {
        if (!Directory.Exists(root)) return [];
        return new DirectoryInfo(root).EnumerateFiles(pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(20000);
    }

    private static IEnumerable<string> HeadLines(string path, int maxLines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        const int maxCharacters = 2 * 1024 * 1024;
        var remaining = maxCharacters;
        for (var lineIndex = 0; lineIndex < maxLines && remaining > 0; lineIndex++)
        {
            var builder = new StringBuilder();
            int character;
            while (remaining-- > 0 && (character = reader.Read()) >= 0)
            {
                if (character == '\n') break;
                builder.Append((char)character);
            }
            if (builder.Length == 0) yield break;
            if (remaining < 0) yield break;
            yield return builder.ToString();
        }
    }

    private static string? String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static bool SamePath(string a, string b)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
}
