using System.Text;

namespace YubiEnroll.Core;

// Small monochrome VT screen for CLI prompts. It never handles OSC links/clipboard commands.
// The buffer is session-only and is explicitly cleared on dismissal.
public sealed class TerminalScreen(int columns = 100, int rows = 28)
{
    private readonly char[][] _cells = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();
    private readonly List<string> _history = [];
    private readonly StringBuilder _escape = new();
    private int _state, _x, _y, _savedX, _savedY;
    public int Columns => columns;
    public int Rows => rows;
    public event Action<string>? Reply;
    public string Text => string.Join("\n", _history.Concat(_cells.Select(row => new string(row).TrimEnd()))).TrimEnd();
    public void Clear()
    {
        foreach (var row in _cells) Array.Fill(row, ' ');
        _history.Clear(); _escape.Clear(); _state = _x = _y = 0;
    }
    public void Feed(string text)
    {
        foreach (var c in text)
        {
            if (_state == 3) { if (c == '\a') _state = 0; else if (c == '\x1b') _state = 4; continue; }
            if (_state == 4) { _state = c == '\\' ? 0 : 3; continue; }
            if (_state == 1)
            {
                _state = 0;
                if (c == '[') { _state = 2; _escape.Clear(); }
                else if (c == ']' || c == 'P') _state = 3;
                else if (c == '7') { _savedX = _x; _savedY = _y; }
                else if (c == '8') { _x = _savedX; _y = _savedY; }
                continue;
            }
            if (_state == 2)
            {
                if (c >= '@' && c <= '~') { Execute(c, _escape.ToString()); _state = 0; }
                else if (_escape.Length < 128) _escape.Append(c);
                else { _escape.Clear(); _state = 0; }
                continue;
            }
            switch (c)
            {
                case '\x1b': _state = 1; break;
                case '\r': _x = 0; break;
                case '\n': NewLine(); break;
                case '\b': _x = Math.Max(0, _x - 1); break;
                case '\t': _x = Math.Min(columns - 1, (_x / 8 + 1) * 8); break;
                default:
                    if (char.IsControl(c)) break;
                    if (_x >= columns) { _x = 0; NewLine(); }
                    _cells[_y][_x++] = c;
                    break;
            }
        }
    }
    private void NewLine()
    {
        if (++_y < rows) return;
        _history.Add(new string(_cells[0]).TrimEnd());
        if (_history.Count > 500) _history.RemoveAt(0);
        for (var i = 0; i < rows - 1; i++) Array.Copy(_cells[i + 1], _cells[i], columns);
        Array.Fill(_cells[rows - 1], ' '); _y = rows - 1;
    }
    private void Execute(char command, string parameters)
    {
        if (parameters.StartsWith('?')) return; // Private display modes do not alter stored text.
        var values = parameters.Split(';').Select(x => int.TryParse(x, out var n) ? Math.Clamp(n, 0, 10000) : 0).ToArray();
        int P(int i, int fallback = 1) => i < values.Length && values[i] > 0 ? values[i] : fallback;
        switch (command)
        {
            case 'H': case 'f': _y = Math.Clamp(P(0) - 1, 0, rows - 1); _x = Math.Clamp(P(1) - 1, 0, columns - 1); break;
            case 'A': _y = Math.Max(0, _y - P(0)); break;
            case 'B': _y = Math.Min(rows - 1, _y + P(0)); break;
            case 'C': _x = Math.Min(columns - 1, _x + P(0)); break;
            case 'D': _x = Math.Max(0, _x - P(0)); break;
            case 'G': _x = Math.Clamp(P(0) - 1, 0, columns - 1); break;
            case 'd': _y = Math.Clamp(P(0) - 1, 0, rows - 1); break;
            case 'K':
                var start = values[0] is 1 or 2 ? 0 : Math.Min(_x, columns - 1);
                var end = values[0] == 1 ? Math.Min(_x + 1, columns) : columns;
                Array.Fill(_cells[_y], ' ', start, end - start); break;
            case 'J':
                if (values[0] is 2 or 3) { foreach (var row in _cells) Array.Fill(row, ' '); if (values[0] == 3) _history.Clear(); }
                else if (values[0] == 0) { Array.Fill(_cells[_y], ' ', Math.Min(_x, columns), columns - Math.Min(_x, columns)); for (var i = _y + 1; i < rows; i++) Array.Fill(_cells[i], ' '); }
                break;
            case 's': _savedX = _x; _savedY = _y; break;
            case 'u': _x = _savedX; _y = _savedY; break;
            case 'n': if (values[0] == 6) Reply?.Invoke($"\x1b[{_y + 1};{Math.Min(_x + 1, columns)}R"); break;
            case 'c': Reply?.Invoke("\x1b[?1;0c"); break;
            // SGR colour and cursor visibility are intentionally monochrome.
        }
    }
}

public static class WindowsCommandLine
{
    // CRT argv quoting; no shell is involved. Also handles embedded quotes and trailing backslashes.
    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Command arguments cannot contain a null character.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
}
