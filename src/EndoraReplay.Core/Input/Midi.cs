namespace EndoraReplay.Core.Input;

public enum MidiControlKind
{
    Note,
    ControlChange,
    PitchBend,
}

/// <summary>How a jog wheel encodes a turn. Auto works out the encoding from the first few messages.</summary>
public enum MidiJogEncoding
{
    Auto,

    /// <summary>64 = still, 65 = one step right, 63 = one step left (Mackie-style "relative 2").</summary>
    Offset64,

    /// <summary>1 = one step right, 127 = one step left ("relative 1").</summary>
    TwosComplement,

    /// <summary>1 = one step right, 65 = one step left ("relative 3", bit 6 is the sign).</summary>
    SignBit,

    /// <summary>The wheel sends its position 0..127 and the step is the difference from the last value.</summary>
    Absolute,
}

/// <summary>One button, key, knob or fader on a MIDI controller.</summary>
public sealed class MidiControl
{
    public MidiControlKind Kind { get; set; }

    /// <summary>MIDI channel 1..16.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Note or CC number. 0 for pitch bend.</summary>
    public int Number { get; set; }

    public bool Matches(MidiMessage m) => m.Kind == Kind && m.Channel == Channel && (Kind == MidiControlKind.PitchBend || m.Number == Number);

    public static MidiControl From(MidiMessage m) => new() { Kind = m.Kind, Channel = m.Channel, Number = m.Kind == MidiControlKind.PitchBend ? 0 : m.Number };

    public override string ToString() => Kind switch
    {
        MidiControlKind.Note => $"Note {NoteName(Number)} · ch {Channel}",
        MidiControlKind.ControlChange => $"CC {Number} · ch {Channel}",
        _ => $"Pitch bend · ch {Channel}",
    };

    public static string NoteName(int n) => new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[n % 12] + (n / 12 - 1);
}

public sealed class MidiBinding
{
    public string Action { get; set; } = "";
    public MidiControl Control { get; set; } = new();
}

/// <summary>A decoded MIDI short message. Note off arrives as a Note with value 0.</summary>
public readonly record struct MidiMessage(MidiControlKind Kind, int Channel, int Number, int Value)
{
    /// <summary>Highest value for this kind: 127, or 16383 for 14-bit pitch bend.</summary>
    public int MaxValue => Kind == MidiControlKind.PitchBend ? 16383 : 127;

    /// <summary>Parses a Windows MIDI short message (status in the low byte). Returns null for messages Endora does not use.</summary>
    public static MidiMessage? Parse(int packed)
    {
        var status = packed & 0xFF;
        var d1 = (packed >> 8) & 0x7F;
        var d2 = (packed >> 16) & 0x7F;
        var ch = (status & 0x0F) + 1;
        return (status & 0xF0) switch
        {
            0x90 => new MidiMessage(MidiControlKind.Note, ch, d1, d2),
            0x80 => new MidiMessage(MidiControlKind.Note, ch, d1, 0),
            0xB0 => new MidiMessage(MidiControlKind.ControlChange, ch, d1, d2),
            0xE0 => new MidiMessage(MidiControlKind.PitchBend, ch, 0, d1 | (d2 << 7)),
            _ => null,
        };
    }
}

/// <summary>Turns jog wheel messages into signed steps: positive = turned right (forward), negative = left (reverse).</summary>
public sealed class JogDecoder(MidiJogEncoding encoding)
{
    private const int DetectSamples = 3;
    private readonly List<int> _samples = [];
    private int? _last;

    /// <summary>The encoding in use: the configured one, or the detected one once Auto has seen enough messages.</summary>
    public MidiJogEncoding Encoding { get; private set; } = encoding;

    public int Decode(int value)
    {
        if (Encoding == MidiJogEncoding.Auto)
        {
            _samples.Add(value);
            if (_samples.Count >= DetectSamples) Encoding = Detect(_samples);
            var guess = Guess(value);
            _last = value;
            return guess;
        }
        return Decode(Encoding, value, ref _last);
    }

    private static int Decode(MidiJogEncoding e, int v, ref int? last)
    {
        switch (e)
        {
            case MidiJogEncoding.Offset64: return v - 64;
            case MidiJogEncoding.TwosComplement: return v < 64 ? v : v - 128;
            case MidiJogEncoding.SignBit: return (v & 64) != 0 ? -(v & 63) : v;
            default:
                var prev = last;
                last = v;
                if (prev is null) return 0;
                var d = v - prev.Value;
                // A wheel that wraps from 127 to 0 is still turning the same way.
                if (d > 64) d -= 128;
                if (d < -64) d += 128;
                return d;
        }
    }

    private static int Guess(int v) =>
        Math.Abs(v - 64) <= 15 ? v - 64
        : v <= 15 ? v
        : v >= 113 ? v - 128
        : 0;

    /// <summary>Works out the encoding from a few raw values of the wheel being turned.</summary>
    public static MidiJogEncoding Detect(IReadOnlyCollection<int> values)
    {
        if (values.Count == 0) return MidiJogEncoding.Auto;
        if (values.All(v => v != 64 && Math.Abs(v - 64) <= 15)) return MidiJogEncoding.Offset64;
        if (values.All(v => v is >= 1 and <= 15 or >= 113)) return MidiJogEncoding.TwosComplement;
        return MidiJogEncoding.Absolute;
    }

    /// <summary>Sum of steps for values decoded with <paramref name="e"/>, used by learn to tell left from right.</summary>
    public static int Sum(MidiJogEncoding e, IEnumerable<int> values)
    {
        int? last = null;
        return values.Sum(v => Decode(e, v, ref last));
    }
}

public sealed class MidiSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>MIDI input device name; empty listens to every connected device.</summary>
    public string? Device { get; set; }

    public List<MidiBinding> Bindings { get; set; } = [];

    public MidiControl? Jog { get; set; }
    public MidiJogEncoding JogEncoding { get; set; } = MidiJogEncoding.Auto;

    /// <summary>Frames moved per jog wheel step.</summary>
    public int JogFramesPerStep { get; set; } = 1;

    /// <summary>Swap left and right if the wheel turns the wrong way.</summary>
    public bool JogInvert { get; set; }

    public MidiControl? TBar { get; set; }

    /// <summary>Raw T-bar value at the 0 % end.</summary>
    public int TBarZero { get; set; }

    /// <summary>Raw T-bar value at the 100 % end.</summary>
    public int TBarFull { get; set; } = 127;

    /// <summary>Replay speed (percent) at the 0 % end and the 100 % end of the T-bar.</summary>
    public int TBarMinSpeed { get; set; } = 0;
    public int TBarMaxSpeed { get; set; } = 100;

    public IEnumerable<string> Validate()
    {
        if (JogFramesPerStep is < 1 or > 250) yield return "MIDI jog frames per step must be 1-250.";
        if (TBarMinSpeed is < 0 or > 200 || TBarMaxSpeed is < 0 or > 200) yield return "MIDI T-bar speeds must be 0-200 %.";
        if (TBar is not null && TBarZero == TBarFull) yield return "MIDI T-bar 0 % and 100 % ends are the same value; set them again.";
    }
}

public readonly record struct MidiCommand(string Action, double? Value = null);

/// <summary>Maps MIDI messages to operator actions: buttons, the jog wheel ("Jog", frames) and the T-bar ("SetSpeed", 0..2).</summary>
public sealed class MidiMapper
{
    public const string JogAction = "Jog";
    public const string SpeedAction = "SetSpeed";

    /// <summary>Slowest speed the T-bar sends; the playout cannot run at 0.</summary>
    public const double MinSpeed = 0.01;

    private readonly MidiSettings _s;
    private readonly JogDecoder _jog;
    private readonly Dictionary<(MidiControlKind, int, int), int> _lastValue = [];
    private double? _lastSpeed;

    public MidiMapper(MidiSettings settings)
    {
        _s = settings;
        _jog = new JogDecoder(settings.JogEncoding);
    }

    public MidiJogEncoding JogEncoding => _jog.Encoding;

    /// <summary>T-bar position 0..1 for a raw value, between the learned ends.</summary>
    public static double TBarPosition(MidiSettings s, int raw) =>
        s.TBarFull == s.TBarZero ? 0 : Math.Clamp((raw - s.TBarZero) / (double)(s.TBarFull - s.TBarZero), 0, 1);

    public static double TBarSpeed(MidiSettings s, int raw)
    {
        var pct = s.TBarMinSpeed + TBarPosition(s, raw) * (s.TBarMaxSpeed - s.TBarMinSpeed);
        return Math.Clamp(Math.Round(pct) / 100.0, MinSpeed, 2.0);
    }

    public IReadOnlyList<MidiCommand> Map(MidiMessage m)
    {
        var result = new List<MidiCommand>();
        if (_s.Jog?.Matches(m) == true && m.Kind != MidiControlKind.Note)
        {
            var steps = _jog.Decode(m.Value);
            if (_s.JogInvert) steps = -steps;
            if (steps != 0) result.Add(new MidiCommand(JogAction, steps * Math.Max(1, _s.JogFramesPerStep)));
            return result;
        }
        if (_s.TBar?.Matches(m) == true && m.Kind != MidiControlKind.Note)
        {
            var speed = TBarSpeed(_s, m.Value);
            if (speed != _lastSpeed) result.Add(new MidiCommand(SpeedAction, speed));
            _lastSpeed = speed;
            return result;
        }

        var key = (m.Kind, m.Channel, m.Number);
        var prev = _lastValue.GetValueOrDefault(key);
        _lastValue[key] = m.Value;
        // Press = note on, or a CC rising past the middle. A CC button that only ever sends 127 counts every time.
        var pressed = m.Kind switch
        {
            MidiControlKind.Note => m.Value > 0,
            MidiControlKind.ControlChange => m.Value >= 64 && (prev < 64 || prev == m.Value),
            _ => false,
        };
        if (!pressed) return result;
        foreach (var b in _s.Bindings)
            if (b.Control.Matches(m)) result.Add(new MidiCommand(b.Action));
        return result;
    }
}
