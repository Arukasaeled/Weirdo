using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weirdo.Core.Services;

internal static class AgentEnvironmentProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Weirdo.Windows.AgentEnvironment.v1");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("Kooky.Windows.AgentEnvironment.v1");

    public static Dictionary<string, string> Read(string? encrypted)
    {
        if (encrypted is null) return new(StringComparer.OrdinalIgnoreCase);
        var cipher = Convert.FromBase64String(encrypted);
        byte[] bytes;
        try { bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { bytes = ProtectedData.Unprotect(cipher, LegacyEntropy, DataProtectionScope.CurrentUser); }
        try
        {
            return new(JsonSerializer.Deserialize(bytes, CoreJsonContext.Default.DictionaryStringString) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string? Write(Dictionary<string, string> environment)
    {
        if (environment.Count == 0) return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(environment, CoreJsonContext.Default.DictionaryStringString);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
