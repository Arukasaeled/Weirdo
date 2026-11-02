using System.Text;

namespace Kooky.Core.Terminal;

/// <summary>Reads explicit OSC 7 directory reports, never prompt/output heuristics.</summary>
internal sealed class WorkingDirectoryTracker
{
    private const int MaximumOscLength = 8192;
    private readonly StringBuilder _osc = new();
    private int _state;

    public string? Feed(string data)
    {
        string? newest = null;
        foreach (var character in data)
        {
            switch (_state)
            {
                case 0:
                    if (character == '\x1b') _state = 1;
                    else if (character == '\u009d') { _osc.Clear(); _state = 2; }
                    break;
                case 1:
                    if (character == ']') { _osc.Clear(); _state = 2; }
                    else _state = character == '\x1b' ? 1 : 0;
                    break;
                case 2:
                    if (character is '\a' or '\u009c') { newest = Parse() ?? newest; _state = 0; }
                    else if (character == '\x1b') _state = 3;
                    else if (_osc.Length < MaximumOscLength) _osc.Append(character);
                    else { _osc.Clear(); _state = 0; }
                    break;
                case 3:
                    if (character == '\\') newest = Parse() ?? newest;
                    _state = character == '\x1b' ? 1 : 0;
                    break;
            }
        }
        return newest;
    }

    private string? Parse()
    {
        if (_osc.Length < 3 || _osc[0] != '7' || _osc[1] != ';') return null;
        if (!Uri.TryCreate(_osc.ToString(2, _osc.Length - 2), UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        try
        {
            var path = Path.GetFullPath(uri.LocalPath);
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
