using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
namespace Kooky.App;
internal static class AppPaths
{
    public static string Data { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kooky");
    public static string Pipe { get; private set; } = "";
    public static bool Debug { get; private set; }
    public static void Initialize(string[] args)
    {
        var i = Array.IndexOf(args, "--data-dir");
        if (i >= 0 && i + 1 < args.Length) Data = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(Data);
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        Pipe = "kooky-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + Data.ToUpperInvariant())))[..24];
        Debug = args.Contains("--devtools");
    }
    public static void Log(Exception ex) { try { File.AppendAllText(Path.Combine(Data,"errors.log"), $"{DateTimeOffset.Now:O} {ex}\n"); } catch { } }
}
