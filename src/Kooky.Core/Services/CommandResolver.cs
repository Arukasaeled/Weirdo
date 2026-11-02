using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

namespace Kooky.Core.Services;

public static class CommandResolver
{
    public static Dictionary<string, string> InheritedEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in System.Environment.GetEnvironmentVariables())
            if (item.Key is string key && item.Value is string value) result[key] = value;
        return result;
    }

    public static string? FindExecutable(string executable, IReadOnlyDictionary<string,string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        executable = executable.Trim().Trim('"', '\'');
        var extension = Path.GetExtension(executable);
        string[] extensions = string.IsNullOrEmpty(extension) ? [".exe", ".cmd", ".bat", ".ps1", ""] : [""];
        if (Path.IsPathRooted(executable) || executable.Contains('\\') || executable.Contains('/'))
            return extensions.Select(e => executable + e).FirstOrDefault(File.Exists) is { } absolute ? Path.GetFullPath(absolute) : null;
        var path = environment != null && environment.TryGetValue("PATH", out var configured) ? configured : System.Environment.GetEnvironmentVariable("PATH") ?? "";
        var user = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var directories = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Concat(new[]
        {
            Path.Combine(user, ".local", "bin"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "hermes", "node"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "PowerShell", "7"),
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0")
        });
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var suffix in extensions)
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), executable + suffix);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    public static (string Executable, string Tail) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith("& ", StringComparison.Ordinal)) command = command[2..].TrimStart();
        if (command.Length == 0) return ("", "");
        if (command[0] is '\'' or '"')
        {
            var quote = command[0];
            var end = command.IndexOf(quote, 1);
            if (end < 0) throw new ArgumentException("The command has an unterminated quoted executable path.");
            return (command[1..end], command[(end + 1)..].TrimStart());
        }
        var space = command.IndexOfAny([' ', '\t', '\r', '\n']);
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }

    public static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    public static string QuoteWindows(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static string PowerShellCommand(string script, bool keepOpen = false, IReadOnlyDictionary<string,string>? environment = null)
    {
        var shell = FindExecutable("pwsh.exe", environment) ?? FindExecutable("powershell.exe", environment)
            ?? throw new FileNotFoundException("PowerShell was not found on this Windows installation.");
        return QuoteWindows(shell) + " -NoLogo -NoProfile " + (keepOpen ? "-NoExit " : "") + "-EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    internal static void ValidateEnvironment(IReadOnlyDictionary<string,string> variables)
    {
        if (variables.Count > 256) throw new ArgumentException("An Agent profile supports at most 256 environment variables.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables)
        {
            if (!Regex.IsMatch(name, "^[a-zA-Z_][a-zA-Z0-9_]*$")) throw new ArgumentException($"Invalid environment variable name: {name}");
            if (!names.Add(name)) throw new ArgumentException($"Duplicate Windows environment variable: {name}");
            if (value.Contains('\0')) throw new ArgumentException($"Environment variable {name} contains a NUL character.");
            if (value.Length > 32760) throw new ArgumentException($"Environment variable {name} is too long.");
        }
    }
}
