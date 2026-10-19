using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Simulation;

/// <summary>
/// SIMULATED switcher for rehearsal and tests. It models Program/Preview, AUTO with a transition duration,
/// CUT and tally. It never touches hardware and is always labelled SIMULATED in the UI.
/// </summary>
public sealed class SimulatedAtemSwitcher : IAtemSwitcher
{
    private readonly object _gate = new();
    private AtemSnapshot _snapshot = AtemSnapshot.Disconnected;
    private AtemTransitionStyle _next = AtemTransitionStyle.Mix;

    public AtemBackendKind Kind => AtemBackendKind.Simulated;
    public bool IsSimulated => true;
    public AtemSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>Duration of a simulated AUTO transition.</summary>
    public TimeSpan AutoDuration { get; set; } = TimeSpan.FromMilliseconds(500);

    public event Action<AtemSnapshot>? StateChanged;
    public event Action<string>? ConnectionLost;

    public Task ConnectAsync(string address, int mixEffectBlock, CancellationToken cancellationToken)
    {
        Update(_ => new AtemSnapshot(true, "Simulated ATEM Mini Extreme", 1, 2, false, new HashSet<int> { 1 }, new HashSet<int> { 2 }, "1080p50"));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Update(_ => AtemSnapshot.Disconnected);
        return Task.CompletedTask;
    }

    /// <summary>Test hook: simulate a network loss.</summary>
    public void SimulateConnectionLoss()
    {
        Update(_ => AtemSnapshot.Disconnected);
        ConnectionLost?.Invoke("simulated network loss");
    }

    /// <summary>Test hook: operator presses a Program button on the panel.</summary>
    public void PanelProgram(int input) => Update(s => s with { ProgramInput = input, ProgramTally = new HashSet<int> { input } });

    public Task SetPreviewInputAsync(int input)
    {
        EnsureConnected();
        Update(s => s with { PreviewInput = input, PreviewTally = new HashSet<int> { input } });
        return Task.CompletedTask;
    }

    public Task SetNextTransitionStyleAsync(AtemTransitionStyle style)
    {
        EnsureConnected();
        _next = style;
        return Task.CompletedTask;
    }

    public async Task PerformAutoTransitionAsync()
    {
        EnsureConnected();
        Update(s => s with { InTransition = true });
        await Task.Delay(AutoDuration).ConfigureAwait(false);
        Swap();
    }

    public Task PerformCutAsync()
    {
        EnsureConnected();
        Swap();
        return Task.CompletedTask;
    }

    /// <summary>Last downstream key configuration: keyer → input.</summary>
    public IReadOnlyDictionary<int, int> DownstreamKeyInputs { get { lock (_gate) return new Dictionary<int, int>(_dskInputs); } }

    private readonly Dictionary<int, int> _dskInputs = [];

    public Task ConfigureDownstreamKeyAsync(int keyer, int input, double clipPercent, double gainPercent)
    {
        EnsureConnected();
        lock (_gate) _dskInputs[keyer] = input;
        return Task.CompletedTask;
    }

    public Task SetDownstreamKeyOnAirAsync(int keyer, bool onAir)
    {
        EnsureConnected();
        Update(s =>
        {
            var on = new HashSet<int>(s.DownstreamKeysOnAir ?? new HashSet<int>());
            if (onAir) on.Add(keyer); else on.Remove(keyer);
            return s with { DownstreamKeysOnAir = on };
        });
        return Task.CompletedTask;
    }

    private void Swap() => Update(s => s with
    {
        ProgramInput = s.PreviewInput,
        PreviewInput = s.ProgramInput,
        InTransition = false,
        ProgramTally = s.PreviewInput is { } p ? new HashSet<int> { p } : new HashSet<int>(),
        PreviewTally = s.ProgramInput is { } q ? new HashSet<int> { q } : new HashSet<int>(),
    });

    private void EnsureConnected()
    {
        if (!Snapshot.Connected) throw new InvalidOperationException("Simulated ATEM not connected.");
    }

    private void Update(Func<AtemSnapshot, AtemSnapshot> f)
    {
        AtemSnapshot s;
        lock (_gate) { _snapshot = f(_snapshot); s = _snapshot; }
        StateChanged?.Invoke(s);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
