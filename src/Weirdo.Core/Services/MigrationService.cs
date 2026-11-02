using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weirdo.Core.Services;

public static class MigrationService
{
    private const string Marker = "migration-previous-v1.json";
    private static readonly byte[] SessionEntropy = Encoding.UTF8.GetBytes("Weirdo.Windows.SessionRequests.v1");
    private static readonly byte[] LegacySessionEntropy = Encoding.UTF8.GetBytes("Kooky.Windows.SessionRequests.v1");
    private static readonly string[] StateNames = ["ui.json", "ui.json.bak", "agents.json", "agents.json.bak", "sessions.json", "sessions.json.bak", "updates.json", "updates.json.bak"];

    public static DataMigrationResult Migrate(string sourceDataDirectory, string destinationDataDirectory, string? oldWorkspaceRoot = null, string? newWorkspaceRoot = null)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDataDirectory));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDataDirectory));
        if (Within(source, destination) || Within(destination, source)) throw new ArgumentException("Migration source and destination must be separate directories.");
        if (!Directory.Exists(source)) return new(true, 0, 0, []);
        RejectLink(source);
        Directory.CreateDirectory(destination);
        RejectLink(destination);
        var mutexName = "Local\\Weirdo.Migration." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.ToUpperInvariant())))[..24];
        using var mutex = new Mutex(false, mutexName);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("Another Weirdo process is migrating local data."); }
        catch (AbandonedMutexException) { }
        try
        {
            if (File.Exists(Path.Combine(destination, Marker))) return new(true, 0, 0, []);
            var mapping = new PathMapping(source, destination, oldWorkspaceRoot, newWorkspaceRoot);
            var copied = 0;
            var skipped = 0;
            var warnings = new List<string>();
            var sourceConfigs = Path.Combine(source, "agent-configs");
            if (Directory.Exists(sourceConfigs))
            {
                var pending = new Stack<string>();
                pending.Push(sourceConfigs);
                while (pending.Count > 0)
                {
                    var directory = pending.Pop();
                    RejectLink(directory);
                    var targetDirectory = Path.Combine(destination, Path.GetRelativePath(source, directory));
                    Directory.CreateDirectory(targetDirectory);
                    RejectLink(targetDirectory);
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            warnings.Add("A linked path in agent-configs requires manual migration.");
                            continue;
                        }
                        if (entry is DirectoryInfo) pending.Push(entry.FullName);
                        else Copy(entry.FullName, Path.Combine(targetDirectory, entry.Name), null, ref copied, ref skipped);
                    }
                }
            }
            foreach (var name in StateNames)
            {
                var from = Path.Combine(source, name);
                var to = Path.Combine(destination, name);
                if (!File.Exists(from)) continue;
                RejectLink(from);
                if (File.Exists(to)) { skipped++; continue; }
                var data = ReadBytes(from, 16 * 1024 * 1024);
                try { data = TransformState(data, name, mapping); }
                catch (JsonException)
                {
                    // Preserve malformed state bytes so StateStore can use the separately
                    // migrated backup and quarantine this copy through its normal recovery.
                    warnings.Add($"Copied malformed {name}; its backup remains available for recovery.");
                }
                Copy(from, to, data, ref copied, ref skipped);
            }
            var completed = !warnings.Any(warning => warning.StartsWith("A linked path", StringComparison.Ordinal));
            var result = new DataMigrationResult(completed, copied, skipped, warnings.ToArray());
            if (completed)
            {
                var receipt = new JsonObject
                {
                    ["version"] = 1,
                    ["completedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["copiedFiles"] = copied,
                    ["skippedFiles"] = skipped,
                    ["oldWorkspaceRoot"] = mapping.OldWorkspace,
                    ["newWorkspaceRoot"] = mapping.NewWorkspace
                };
                var markerBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, CoreJsonContext.Default.JsonNode);
                WriteNew(Path.Combine(destination, Marker), markerBytes, encrypted: false);
            }
            return result;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static byte[] TransformState(byte[] bytes, string name, PathMapping mapping)
    {
        var root = JsonNode.Parse(bytes) ?? throw new JsonException("State is empty.");
        var payload = root is JsonObject envelope && envelope.ContainsKey("schemaVersion") ? envelope["payload"] ?? throw new JsonException("State payload is absent.") : root;
        if (root is JsonObject versioned && versioned["schemaVersion"] is JsonValue schema && schema.TryGetValue<int>(out var version) && version > 1)
            throw new NotSupportedException("A newer legacy state schema requires a compatible Weirdo version.");
        if (name.StartsWith("agents.", StringComparison.Ordinal))
        {
            var agents = payload.Deserialize(CoreJsonContext.Default.ListStoredAgentProfile) ?? [];
            var migrated = agents.Select(item =>
            {
                var environment = AgentEnvironmentProtection.Read(item.ProtectedEnvironment);
                if (environment.TryGetValue("CLAUDE_CONFIG_DIR", out var configDirectory)) environment["CLAUDE_CONFIG_DIR"] = mapping.ConfigPath(configDirectory);
                var profile = ClaudeModelOptions.Normalize(new AgentProfile { Id = item.Id, Name = item.Name, Command = item.Command, Kind = item.Kind, Model = item.Model, Env = environment });
                return item with { Command = profile.Command, Model = profile.Model, ProtectedEnvironment = AgentEnvironmentProtection.Write(environment) };
            }).ToList();
            var replacement = JsonSerializer.SerializeToNode(migrated, CoreJsonContext.Default.ListStoredAgentProfile);
            if (ReferenceEquals(payload, root)) root = replacement!; else root["payload"] = replacement;
        }
        else if (name.StartsWith("sessions.", StringComparison.Ordinal) && payload["encrypted"] is JsonValue encrypted)
        {
            var cipher = Convert.FromBase64String(encrypted.GetValue<string>());
            var plaintext = UnprotectSessions(cipher);
            try
            {
                var sessions = JsonNode.Parse(plaintext) ?? throw new JsonException("Session state is empty.");
                RewritePaths(sessions, mapping, mapWorkspace: false);
                var rewritten = JsonSerializer.SerializeToUtf8Bytes(sessions, CoreJsonContext.Default.JsonNode);
                try { payload["encrypted"] = Convert.ToBase64String(ProtectedData.Protect(rewritten, SessionEntropy, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(rewritten); }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        else if (name.StartsWith("ui.", StringComparison.Ordinal)) RewritePaths(payload, mapping);
        return JsonSerializer.SerializeToUtf8Bytes(root, CoreJsonContext.Default.JsonNode);
    }

    private static byte[] UnprotectSessions(byte[] encrypted)
    {
        try { return ProtectedData.Unprotect(encrypted, SessionEntropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            try { return ProtectedData.Unprotect(encrypted, LegacySessionEntropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException) { return ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
        }
    }

    private static void RewritePaths(JsonNode node, PathMapping mapping, bool mapWorkspace = true)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj.ToArray())
            {
                if (child is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (mapWorkspace && name is ("cwd" or "path")) obj[name] = mapping.WorkspacePath(text);
                    else if (name.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)) obj[name] = mapping.ConfigPath(text);
                }
                else if (child is not null) RewritePaths(child, mapping, mapWorkspace);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) RewritePaths(child, mapping, mapWorkspace);
    }

    private static void Copy(string source, string destination, byte[]? replacement, ref int copied, ref int skipped)
    {
        if (File.Exists(destination)) { skipped++; return; }
        var encrypted = (File.GetAttributes(source) & FileAttributes.Encrypted) != 0;
        var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.WriteThrough | (encrypted ? FileOptions.Encrypted : FileOptions.None)))
            {
                if (encrypted && (File.GetAttributes(temporary) & FileAttributes.Encrypted) == 0) throw new IOException("Migration could not preserve file encryption.");
                if (replacement is not null) output.Write(replacement);
                else
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var remaining = input.Length;
                    var buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count == 0) throw new IOException("A source file changed during migration. Retry to copy its complete snapshot.");
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                }
                output.Flush(true);
            }
            if (File.Exists(destination)) { skipped++; return; }
            File.Move(temporary, destination);
            copied++;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteNew(string path, byte[] bytes, bool encrypted)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough | (encrypted ? FileOptions.Encrypted : FileOptions.None)))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadBytes(string path, int maximum)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximum) throw new InvalidDataException("Legacy state exceeds the migration size limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Migration will not follow linked data paths.");
    }
    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class PathMapping(string source, string destination, string? oldWorkspace, string? newWorkspace)
    {
        public string? OldWorkspace { get; } = oldWorkspace is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldWorkspace));
        public string? NewWorkspace { get; } = newWorkspace is not null && Directory.Exists(newWorkspace) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(newWorkspace)) : null;
        public string WorkspacePath(string value) => OldWorkspace is not null && NewWorkspace is not null ? Map(value, OldWorkspace, NewWorkspace) : value;
        public string ConfigPath(string value) => Map(value, Path.Combine(source, "agent-configs"), Path.Combine(destination, "agent-configs"));

        private static string Map(string value, string from, string to)
        {
            if (!Path.IsPathRooted(value)) return value;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Within(from, path)) return value;
            var relative = Path.GetRelativePath(from, path);
            var target = relative == "." ? to : Path.Combine(to, relative);
            return Directory.Exists(target) ? target : value;
        }
    }
}
