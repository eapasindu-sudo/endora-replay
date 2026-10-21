using System.Diagnostics;

namespace EndoraReplay.Core.Abstractions;

public sealed class MonotonicClock : IClock
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTime _startUtc = DateTime.UtcNow;

    public long NowTicks => Stopwatch.GetElapsedTime(_startTimestamp).Ticks;

    public DateTime UtcNow => _startUtc + TimeSpan.FromTicks(NowTicks);

    /// <summary>Wall-clock time of day for a session timestamp, used for timecode display.</summary>
    public DateTime ToLocalTime(long ticks) => (_startUtc + TimeSpan.FromTicks(ticks)).ToLocalTime();
}

/// <summary>Manually advanced clock for tests and deterministic replays of event logs.</summary>
public sealed class ManualClock : IClock
{
    public long NowTicks { get; set; }
    public DateTime UtcNow => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.FromTicks(NowTicks);
    public void Advance(TimeSpan by) => NowTicks += by.Ticks;
}
