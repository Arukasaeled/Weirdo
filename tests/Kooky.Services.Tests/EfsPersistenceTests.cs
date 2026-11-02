using System.Text.Json;
using Kooky.Core.Services;

internal static class EfsPersistenceTests
{
    public static void Run(Action<bool, string> check, string projectFixture)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kooky-tests"));
        var testRoot = Path.GetFullPath(Path.Combine(allowedRoot, "efs-" + Guid.NewGuid().ToString("N")));
        if (!testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("EFS fixture escaped its owned test directory.");
        Directory.CreateDirectory(testRoot);
        try
        {
            try { if (!Encrypted(testRoot)) File.Encrypt(testRoot); }
            catch (Exception ex) when (EfsUnavailable(ex))
            {
                Console.WriteLine($"SKIP EFS persistence: filesystem/user EFS support unavailable (0x{ex.HResult:X8}).");
                return;
            }
            check(Encrypted(testRoot), "real C-volume EFS fixture enabled");
            var store = new StateStore(testRoot);
            var primary = Path.Combine(testRoot, "state.json");
            var backup = primary + ".bak";
            store.Save("state", new { revision = 1 });
            store.Save("state", new { revision = 2 });
            store.Save("state", new { revision = 3 });
            check(Revision(primary) == 3 && Revision(backup) == 2, "three EFS saves publish latest state and previous backup");
            check(Encrypted(primary) && Encrypted(backup), "EFS primary and backup remain encrypted after replacement");
            check(Directory.GetFiles(testRoot, "*.tmp").Length == 0, "successful EFS saves leave no staging files");

            using (var oldReader = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var published = false;
                try { store.Save("state", new { revision = 4 }); published = true; }
                catch (IOException)
                {
                    // EFS/filter-driver implementations can still reject replacement
                    // while a shared-delete reader exists. Failure must preserve both files.
                    check(Revision(primary) == 3 && Revision(backup) == 3,
                        "EFS shared-reader replacement failure preserves original and last-good backup");
                }
                using var oldDocument = JsonDocument.Parse(oldReader);
                check(oldDocument.RootElement.GetProperty("payload").GetProperty("revision").GetInt32() == 3 && Revision(primary) == (published ? 4 : 3),
                    "EFS publication never changes an already-open reader's snapshot");
            }
            if (Revision(primary) != 4) store.Save("state", new { revision = 4 });
            check(Revision(primary) == 4 && Revision(backup) == 3, "EFS publication succeeds after shared reader closes");

            using (var backupLock = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { store.Save("state", new { revision = 5 }); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                check(failed && Revision(primary) == 4 && Revision(backup) == 3,
                    "failed backup publication preserves both previous EFS files");
            }
            using (var primaryLock = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { store.Save("state", new { revision = 5 }); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
                check(failed && Revision(primary) == 4 && Revision(backup) == 4,
                    "failed primary publication preserves original and validated last-good EFS backup");
            }
            check(Encrypted(primary) && Encrypted(backup), "EFS failure paths never decrypt surviving state");
            File.WriteAllText(primary, "{broken");
            check(store.Load("state")?.GetProperty("revision").GetInt32() == 4, "corrupt EFS primary recovers last-good backup");
            var quarantine = Directory.GetFiles(testRoot, "state.json.corrupt-*");
            check(quarantine.Length == 1 && Encrypted(quarantine[0]), "EFS corruption quarantine preserves encryption");
            store.Save("state", new { revision = 6 });
            check(Revision(primary) == 6 && Revision(backup) == 4 && Encrypted(primary), "EFS writes resume safely after corruption recovery");

            // Exercise an individually encrypted file under a directory that does not
            // inherit encryption. New stages must request EFS, rather than rely on inheritance.
            var plainParent = Path.Combine(Path.GetFullPath(projectFixture), "efs-noinherit");
            Directory.CreateDirectory(plainParent);
            if (Encrypted(plainParent))
            {
                Console.WriteLine("SKIP non-inheriting EFS subcase: project test directory also inherits encryption.");
                return;
            }
            var separateStore = new StateStore(plainParent);
            var separatePrimary = Path.Combine(plainParent, "state.json");
            var plainProbe = Path.Combine(plainParent, "plaintext-probe.txt");
            File.WriteAllText(plainProbe, "owned test fixture");
            try
            {
                using var seed = new FileStream(separatePrimary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Encrypted | FileOptions.WriteThrough);
                seed.Write("{\"schemaVersion\":1,\"payload\":{\"revision\":1}}"u8);
                seed.Flush(true);
            }
            catch (Exception ex) when (EfsUnavailable(ex))
            {
                Console.WriteLine($"SKIP non-inheriting EFS subcase: project volume cannot create an individually encrypted file (0x{ex.HResult:X8}).");
                return;
            }
            catch (UnauthorizedAccessException) when (File.Exists(plainProbe))
            {
                Console.WriteLine("SKIP non-inheriting EFS subcase: this volume/user permits plaintext creation but denies explicitly encrypted creation (0x80070005). Inherited C-volume EFS regression passed.");
                return;
            }
            check(Encrypted(separatePrimary) && !Encrypted(plainParent), "owned individually encrypted file starts under an unencrypted directory");
            separateStore.Save("state", new { revision = 2 });
            separateStore.Save("state", new { revision = 3 });
            check(Revision(separatePrimary) == 3 && Revision(separatePrimary + ".bak") == 2
                && Encrypted(separatePrimary) && Encrypted(separatePrimary + ".bak") && !Encrypted(plainParent),
                "individually encrypted state stays encrypted without directory inheritance");
        }
        finally
        {
            // Only this freshly created, unique fixture is removed. No default Kooky
            // state, Agent settings, authentication or EFS parent attributes are touched.
            if (testRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static bool Encrypted(string path) => (File.GetAttributes(path) & FileAttributes.Encrypted) != 0;
    private static int Revision(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("payload").GetProperty("revision").GetInt32();
    }
    private static bool EfsUnavailable(Exception exception)
    {
        if (exception is PlatformNotSupportedException) return true;
        var code = exception.HResult & 0xffff;
        return exception is IOException && code is 50 or 6000 or 6003 or 6004 or 6005 or 6006 or 6010 or 6011 or 6012 or 6014 or 6015 or 6016;
    }
}
