using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace EndoraReplay.Core.Vmix;

/// <summary>One vMix input as listed in the vMix XML state.</summary>
public sealed record VmixInput(int Number, string Key, string Type, string Title, string ShortTitle, string State)
{
    /// <summary>True when vMix reports the input as running (playing).</summary>
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);

    public string Label => string.IsNullOrEmpty(ShortTitle) ? Title : ShortTitle;
}

/// <summary>A message received from the vMix TCP API (port 8099).</summary>
public abstract record VmixMessage;

/// <summary>"TALLY OK 0121": one character per input, 0 off, 1 Program, 2 Preview.</summary>
public sealed record VmixTallyMessage(IReadOnlySet<int> Program, IReadOnlySet<int> Preview) : VmixMessage;

/// <summary>"ACTS OK InputPlaying 3 1": an activator event or reply.</summary>
public sealed record VmixActivatorMessage(string Name, int? Input, string Value) : VmixMessage
{
    public bool IsOn => Value == "1" || (double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0);
}

/// <summary>A single-line reply: "FUNCTION OK PreviewInput", "FUNCTION ER message", "XMLTEXT OK value", "SUBSCRIBE OK TALLY".</summary>
public sealed record VmixReplyMessage(string Command, bool Ok, string Text) : VmixMessage;

/// <summary>A reply with a byte-length header followed by data: "XML 1234" + XML, or a multi-line XMLTEXT.</summary>
public sealed record VmixDataMessage(string Command, string Data) : VmixMessage;

/// <summary>A line that matched nothing known (kept for the log).</summary>
public sealed record VmixUnknownMessage(string Line) : VmixMessage;

/// <summary>Pure parsing and formatting for the vMix TCP API. No I/O.</summary>
public static class VmixProtocol
{
    public const int DefaultPort = 8099;

    /// <summary>Builds "FUNCTION Name Query\r\n". The query uses the Web API syntax (Input=3&amp;Value=...).</summary>
    public static string FunctionLine(string function, string? query)
    {
        function = function.Trim();
        if (function.Length == 0 || function.Any(char.IsWhiteSpace))
            throw new ArgumentException("vMix function name must be one word, e.g. Stinger1.", nameof(function));
        return string.IsNullOrWhiteSpace(query) ? $"FUNCTION {function}\r\n" : $"FUNCTION {function} {query.Trim()}\r\n";
    }

    /// <summary>Query string selecting an input, URL-encoded.</summary>
    public static string InputQuery(int input) => "Input=" + input.ToString(CultureInfo.InvariantCulture);

    /// <summary>Splits a user-entered "Function query" line, e.g. "SetRate Input=3&amp;Value=0.5".</summary>
    public static (string Function, string? Query) SplitFunction(string line)
    {
        var t = line.Trim();
        var space = t.IndexOf(' ');
        return space < 0 ? (t, null) : (t[..space], t[(space + 1)..].Trim());
    }

    /// <summary>True for a header like "XML 1234" or "XMLTEXT 52" that is followed by that many bytes of data.</summary>
    public static bool TryParseLengthHeader(string line, out string command, out int length)
    {
        command = "";
        length = 0;
        var parts = line.Split(' ', 3);
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0) return false;
        command = parts[0];
        return true;
    }

    public static VmixMessage ParseLine(string line)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length < 2) return new VmixUnknownMessage(line);
        var command = parts[0];
        var status = parts[1];
        var rest = parts.Length > 2 ? parts[2] : "";

        if (command == "TALLY" && status == "OK") return ParseTally(rest.Trim());
        if (command == "ACTS" && status == "OK")
        {
            var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 3 && int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var input))
                return new VmixActivatorMessage(t[0], input, t[2]);
            if (t.Length == 2) return new VmixActivatorMessage(t[0], null, t[1]);
            return new VmixUnknownMessage(line);
        }
        if (status is "OK" or "ER") return new VmixReplyMessage(command, status == "OK", rest);
        return new VmixUnknownMessage(line);
    }

    public static VmixTallyMessage ParseTally(string digits)
    {
        var program = new HashSet<int>();
        var preview = new HashSet<int>();
        for (var i = 0; i < digits.Length; i++)
        {
            if (digits[i] == '1') program.Add(i + 1);
            else if (digits[i] == '2') preview.Add(i + 1);
        }
        return new VmixTallyMessage(program, preview);
    }

    /// <summary>Reads the input list, Program (active) and Preview from the vMix XML state.</summary>
    public static (IReadOnlyList<VmixInput> Inputs, int? Active, int? Preview, string? Version) ParseState(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null) return ([], null, null, null);
        var inputs = new List<VmixInput>();
        foreach (var e in root.Element("inputs")?.Elements("input") ?? Enumerable.Empty<XElement>())
        {
            if (!int.TryParse((string?)e.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            inputs.Add(new VmixInput(n,
                (string?)e.Attribute("key") ?? "",
                (string?)e.Attribute("type") ?? "",
                (string?)e.Attribute("title") ?? "",
                (string?)e.Attribute("shortTitle") ?? "",
                (string?)e.Attribute("state") ?? ""));
        }
        static int? Num(XElement? x) => int.TryParse(x?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        return (inputs, Num(root.Element("active")), Num(root.Element("preview")), root.Element("version")?.Value);
    }

    /// <summary>
    /// Finds an input from what the operator typed: a number, a title, a short title or a key. Empty text picks the
    /// first input of <paramref name="defaultType"/> (e.g. "Replay"). Returns null when nothing matches.
    /// </summary>
    public static int? ResolveInput(string? spec, IReadOnlyList<VmixInput> inputs, string? defaultType = null)
    {
        var s = spec?.Trim() ?? "";
        if (s.Length == 0)
            return defaultType is null ? null : inputs.FirstOrDefault(i => i.Type.Equals(defaultType, StringComparison.OrdinalIgnoreCase))?.Number;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        var match = inputs.FirstOrDefault(i => i.Title.Equals(s, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(i => i.ShortTitle.Equals(s, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(i => i.Key.Equals(s, StringComparison.OrdinalIgnoreCase));
        return match?.Number;
    }
}

/// <summary>
/// Turns the raw TCP byte stream into messages. Lines end in CRLF; a length header ("XML 1234") is followed by exactly
/// that many bytes of UTF-8 data. Feed it bytes as they arrive; it keeps partial data between calls.
/// </summary>
public sealed class VmixStreamParser
{
    private byte[] _buf = new byte[64 * 1024];
    private int _len;
    private string? _pendingCommand;
    private int _pendingLength;

    public IReadOnlyList<VmixMessage> Feed(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        var output = new List<VmixMessage>();
        var pos = 0;
        while (true)
        {
            if (_pendingCommand is not null)
            {
                if (_len - pos < _pendingLength) break;
                var text = Encoding.UTF8.GetString(_buf, pos, _pendingLength);
                pos += _pendingLength;
                output.Add(new VmixDataMessage(_pendingCommand, text.TrimEnd('\r', '\n')));
                _pendingCommand = null;
                continue;
            }
            var nl = Array.IndexOf(_buf, (byte)'\n', pos, _len - pos);
            if (nl < 0) break;
            var line = Encoding.UTF8.GetString(_buf, pos, nl - pos).TrimEnd('\r');
            pos = nl + 1;
            if (line.Length == 0) continue;
            if (VmixProtocol.TryParseLengthHeader(line, out var cmd, out var length))
            {
                _pendingCommand = cmd;
                _pendingLength = length;
                continue;
            }
            output.Add(VmixProtocol.ParseLine(line));
        }
        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }
        return output;
    }
}
