using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Weirdo.Core.Services;

/// <summary>Versioned, durable local JSON with a last-good backup and explicit corruption recovery.</summary>
public sealed class StateStore
{
    private const int CurrentVersion = 1;
    private const int MaxBytes = 16 * 1024 * 1024;
    private readonly string directory;
    private readonly ConcurrentQueue<string> recoveryWarnings = new();
    public IReadOnlyList<string> RecoveryWarnings => recoveryWarnings.ToArray();

    public StateStore(string dataDir)
    {
        directory = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(directory);
    }

    public JsonElement? Load(string key)
    {
        var path = GetPath(key);
        using var lease = Lock(path);
        return LoadCore(path);
    }

    [RequiresUnreferencedCode("Pass JsonTypeInfo, JsonElement or JsonNode when saving from a trimmed or NativeAOT application.")]
    [RequiresDynamicCode("Pass JsonTypeInfo, JsonElement or JsonNode when saving from a NativeAOT application.")]
    public void Save(string key, object value) => Save(key, JsonSerializer.SerializeToElement(value, JsonDefaults.Options));

    public void Save<T>(string key, T value, JsonTypeInfo<T> typeInfo) => Save(key, JsonSerializer.SerializeToElement(value, typeInfo));

    public void Save(string key, JsonNode? value) => Save(key, JsonSerializer.SerializeToElement(value, CoreJsonContext.Default.JsonNode));

    public void Save(string key, JsonElement value)
    {
        var path = GetPath(key);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentVersion);
            writer.WriteString("savedAt", DateTimeOffset.UtcNow);
            writer.WritePropertyName("payload");
            value.WriteTo(writer);
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("State exceeds the 16 MB storage limit.");
        using (JsonDocument.Parse(bytes)) { }
        using var lease = Lock(path);
        var backup = path + ".bak";
        // A file may be encrypted even when its directory no longer inherits EFS.
        // Create every replacement encrypted from the outset in that case.
        var preserveEncryption = IsEncrypted(path) || IsEncrypted(backup);
        // Preserve malformed files before replacing them, and reject newer schemas.
        if (File.Exists(path)) _ = LoadCore(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupStage = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreateStage(temporary, preserveEncryption))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _ = Read(temporary);
            if (File.Exists(path))
            {
                // File.Replace/ReplaceFileW fails with ERROR_UNABLE_TO_REMOVE_REPLACED
                // on some inherited-EFS directories. Stage the backup independently,
                // then use same-directory atomic rename for both publication steps.
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var destination = CreateStage(backupStage, preserveEncryption))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }
                _ = Read(backupStage);
                File.Move(backupStage, backup, overwrite: true);
            }
            File.Move(temporary, path, overwrite: true);
            _ = Read(path);
        }
        finally
        {
            DeleteStage(temporary);
            DeleteStage(backupStage);
        }
    }

    private JsonElement? LoadCore(string path)
    {
        if (File.Exists(path))
        {
            try { return Read(path); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                var quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N")[..6];
                File.Move(path, quarantine);
                recoveryWarnings.Enqueue($"Preserved unreadable {Path.GetFileName(path)} as {Path.GetFileName(quarantine)}: {ex.Message}");
            }
        }
        var backup = path + ".bak";
        if (!File.Exists(backup)) return null;
        try
        {
            var value = Read(backup);
            recoveryWarnings.Enqueue($"Recovered {Path.GetFileName(path)} from its last-good backup.");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            recoveryWarnings.Enqueue($"Backup {Path.GetFileName(backup)} is unreadable; the original is preserved.");
            return null;
        }
    }

    private static JsonElement Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxBytes) throw new InvalidDataException("State file exceeds the 16 MB storage limit.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schemaVersion", out var version))
        {
            if (!version.TryGetInt32(out var number) || number < 1) throw new InvalidDataException("Invalid state schema version.");
            if (number > CurrentVersion) throw new NotSupportedException($"State schema {number} requires a newer Weirdo version. The file was not changed.");
            if (!root.TryGetProperty("payload", out var payload)) throw new InvalidDataException("State payload is missing.");
            return payload.Clone();
        }
        // v0 migration: legacy plain JSON is accepted and wrapped on the next save.
        return root.Clone();
    }

    private static bool IsEncrypted(string path) => File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;

    private static FileStream CreateStage(string path, bool encrypted)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough | (encrypted ? FileOptions.Encrypted : FileOptions.None));
        try
        {
            if (encrypted && !IsEncrypted(path)) throw new IOException("The filesystem could not preserve encryption for the settings replacement.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void DeleteStage(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { } // Do not mask the original failure; published files are never deleted here.
        catch (UnauthorizedAccessException) { }
    }

    private string GetPath(string key)
    {
        if (!Regex.IsMatch(key, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$")) throw new ArgumentException("Invalid state key.", nameof(key));
        return Path.Combine(directory, key + ".json");
    }

    private static IDisposable Lock(string path)
    {
        var name = "Local\\Weirdo.State." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
        var mutex = new Mutex(false, name);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Local settings are busy; retry the operation."); }
            catch (AbandonedMutexException) { }
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
