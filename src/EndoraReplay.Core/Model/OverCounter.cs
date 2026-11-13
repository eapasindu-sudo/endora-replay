using System.Globalization;
using System.Text.Json;

namespace EndoraReplay.Core.Model;

/// <summary>
/// Cricket over and ball count, shown as "over.ball" like a scoreboard: "12.4" is the fourth ball of the 13th over
/// (12 overs and 4 balls bowled). The operator adds a ball after each legal delivery; new clips are tagged with it.
/// Wides and no-balls are simply not counted. Saved to a small file so a restart keeps the count.
/// </summary>
public sealed class OverCounter
{
    public const int BallsPerOver = 6;

    private readonly Stack<(int Overs, int Balls)> _undo = new();
    private readonly object _gate = new();
    private readonly string? _path;

    public OverCounter(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            if (JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } s && s.Balls is >= 0 and <= BallsPerOver && s.Overs >= 0)
                (Overs, Balls) = (s.Overs, s.Balls);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private sealed record Saved(int Overs, int Balls);

    /// <summary>Completed overs.</summary>
    public int Overs { get; private set; }

    /// <summary>Balls bowled in the current over (0–6).</summary>
    public int Balls { get; private set; }

    public string Label => $"{Overs}.{Balls}";

    /// <summary>False until the first ball, so clips made before the match are not tagged "0.0".</summary>
    public bool Started => Overs > 0 || Balls > 0;

    public event Action? Changed;

    /// <summary>Counts one legal delivery: 12.6 is followed by 13.1.</summary>
    public void AddBall() => Update(() => Balls >= BallsPerOver ? (Overs + 1, 1) : (Overs, Balls + 1));

    public bool Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return false;
            (Overs, Balls) = _undo.Pop();
        }
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sets the count from text such as "12.4" or "12". Returns false if the text is not a valid count.</summary>
    public bool TrySet(string text)
    {
        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var overs)) return false;
        var balls = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out balls) || balls > BallsPerOver)) return false;
        Update(() => (overs, balls));
        return true;
    }

    private void Update(Func<(int, int)> next)
    {
        lock (_gate)
        {
            _undo.Push((Overs, Balls));
            (Overs, Balls) = next();
        }
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path is null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(new Saved(Overs, Balls))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
