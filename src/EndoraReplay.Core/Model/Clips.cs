namespace EndoraReplay.Core.Model;

/// <summary>A replay clip: a time range on the shared session clock, preserved for one or more angles.</summary>
public sealed class Clip
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public long InTicks { get; set; }
    public long OutTicks { get; set; }
    public int PrimarySourceId { get; set; }
    public string Title { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// <summary>Saved clip file per source (angle). Only angles that were captured are present.</summary>
    public Dictionary<int, string> Files { get; set; } = [];

    /// <summary>Operator-protected clips are never removed by disk-space housekeeping.</summary>
    public bool Protected { get; set; } = true;

    public TimeSpan Duration => TimeSpan.FromTicks(Math.Max(0, OutTicks - InTicks));

    public string Describe() =>
        $"#{Id} · Cam {PrimarySourceId} · {Duration.TotalSeconds:0.00} s" + (Tags.Count > 0 ? " · " + string.Join(", ", Tags) : "");
}

/// <summary>
/// Reference to material that automation can arm and play. Immutable. <see cref="ClipId"/> is 0 for
/// material still in the rolling buffer (not yet saved); it then plays from [InTicks, OutTicks] of the buffer.
/// </summary>
public sealed record ClipRef(long ClipId, int SourceId, double Speed, string Description, long InTicks, long OutTicks)
{
    public bool IsSaved => ClipId > 0;
}

public static class StandardTags
{
    public static readonly IReadOnlyList<string> Cricket = ["WICKET", "4", "6", "CATCH", "NOT OUT", "REVIEW", "DROP", "RUN OUT"];
}
