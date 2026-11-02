using System.Text;

namespace Weirdo.Core.Services;

internal static class ClaudeModelOptions
{
    private sealed record Argument(int Start, int Length, string Value);

    public static AgentProfile Normalize(AgentProfile profile)
    {
        var model = NormalizeModel(profile.Model);
        if (profile.Kind != "claude" || !IsNativeCommand(profile.Command)) return profile with { Model = model };
        var command = profile.Command.Trim();
        var (_, tail) = CommandResolver.SplitCommand(command);
        var prefix = command[..(command.Length - tail.Length)].TrimEnd();
        var arguments = Parse(tail);
        var removals = new List<(int Start, int End)>();
        string? fromCommand = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.Value == "--") break;
            if (argument.Value.StartsWith("--model=", StringComparison.Ordinal))
            {
                fromCommand = NormalizeModel(argument.Value[8..]) ?? throw new ArgumentException("The --model argument needs a model ID.");
                removals.Add((argument.Start, argument.Start + argument.Length));
            }
            else if (argument.Value == "--model")
            {
                if (++index >= arguments.Count || arguments[index].Value.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("The --model argument needs a model ID.");
                var value = arguments[index];
                fromCommand = NormalizeModel(value.Value) ?? throw new ArgumentException("The --model argument needs a model ID.");
                removals.Add((argument.Start, value.Start + value.Length));
            }
        }
        if (removals.Count == 0) return profile with { Model = model };
        var result = new StringBuilder();
        var position = 0;
        foreach (var (start, end) in removals)
        {
            result.Append(tail[position..start]);
            position = end;
        }
        result.Append(tail[position..]);
        var remaining = result.ToString().Trim();
        return profile with { Command = prefix + (remaining.Length == 0 ? "" : " " + remaining), Model = model ?? fromCommand };
    }

    public static bool IsNativeCommand(string command)
    {
        if (command.TrimStart().StartsWith("& ", StringComparison.Ordinal)) return false;
        var (executable, _) = CommandResolver.SplitCommand(command);
        var extension = Path.GetExtension(executable);
        if (extension.Length != 0 && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetFileNameWithoutExtension(executable).ToLowerInvariant() is not ("powershell" or "pwsh" or "cmd" or "bash" or "sh" or "wsl");
    }

    public static string AddArguments(string tail, string options)
    {
        if (string.IsNullOrWhiteSpace(options)) return tail;
        var separator = Parse(tail).FirstOrDefault(argument => argument.Value == "--");
        return separator is null ? (tail.TrimEnd() + " " + options.Trim()).Trim() : tail.Insert(separator.Start, options.Trim() + " ");
    }

    private static string? NormalizeModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 2048 || value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("A model ID must be at most 2048 characters on one line.");
        return value;
    }

    // Windows CRT argument rules, with raw spans retained so unrelated argument text
    // and quoted executable paths survive migration exactly as entered.
    private static List<Argument> Parse(string text)
    {
        var result = new List<Argument>();
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position == text.Length) break;
            var start = position;
            var value = new StringBuilder();
            var quoted = false;
            while (position < text.Length && (quoted || !char.IsWhiteSpace(text[position])))
            {
                var slashes = 0;
                while (position < text.Length && text[position] == '\\') { slashes++; position++; }
                if (position < text.Length && text[position] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { value.Append('"'); position++; }
                    else if (quoted && position + 1 < text.Length && text[position + 1] == '"') { value.Append('"'); position += 2; }
                    else { quoted = !quoted; position++; }
                }
                else
                {
                    value.Append('\\', slashes);
                    if (position < text.Length && (quoted || !char.IsWhiteSpace(text[position]))) value.Append(text[position++]);
                }
            }
            if (quoted) throw new ArgumentException("The command contains an unterminated quoted argument.");
            result.Add(new(start, position - start, value.ToString()));
        }
        return result;
    }
}
