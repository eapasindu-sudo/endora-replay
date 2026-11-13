using System.Windows.Input;

namespace EndoraReplay.App.Services;

/// <summary>Maps key presses to operator actions using the configurable bindings (WPF gesture syntax, e.g. "Shift+Left").</summary>
public sealed class KeyBindingService
{
    private readonly Dictionary<(Key, ModifierKeys), string> _map = [];

    public KeyBindingService(IReadOnlyDictionary<string, string> bindings) => Load(bindings);

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public void Load(IReadOnlyDictionary<string, string> bindings)
    {
        _map.Clear();
        var errors = new List<string>();
        foreach (var (action, gesture) in bindings)
        {
            if (string.IsNullOrWhiteSpace(gesture)) continue;
            if (TryParse(gesture, out var key, out var mods)) _map[(key, mods)] = action;
            else errors.Add($"{action}: '{gesture}' is not a valid key");
        }
        Errors = errors;
    }

    public string? ActionFor(Key key, ModifierKeys modifiers)
    {
        if (key == Key.System) return null;
        return _map.TryGetValue((key, modifiers), out var a) ? a
            // Number pad digits select cameras like the top-row digits.
            : key is >= Key.NumPad1 and <= Key.NumPad8 && _map.TryGetValue((Key.D1 + (key - Key.NumPad1), modifiers), out a) ? a
            : null;
    }

    public static bool TryParse(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts[..^1])
        {
            if (!Enum.TryParse<ModifierKeys>(p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : p, true, out var m)) return false;
            modifiers |= m;
        }
        return Enum.TryParse(parts[^1], true, out key) && key != Key.None;
    }

    /// <summary>Formats a key press as a gesture string for rebinding.</summary>
    public static string Format(Key key, ModifierKeys modifiers)
    {
        var mods = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        mods.Add(key.ToString());
        return string.Join("+", mods);
    }

    /// <summary>Short label for key hints on buttons.</summary>
    public static string Pretty(string gesture) => gesture
        .Replace("Escape", "ESC").Replace("Back", "BKSP").Replace("Return", "ENTER").Replace("Enter", "ENTER")
        .Replace("Space", "SPACE").Replace("Left", "←").Replace("Right", "→").Replace("Up", "↑").Replace("Down", "↓")
        .Replace("Shift", "SHIFT").Replace("Ctrl", "CTRL").Replace("Tab", "TAB")
        .Replace("D1", "1").Replace("D2", "2").Replace("D3", "3").Replace("D4", "4")
        .Replace("D5", "5").Replace("D6", "6").Replace("D7", "7").Replace("D8", "8");
}
