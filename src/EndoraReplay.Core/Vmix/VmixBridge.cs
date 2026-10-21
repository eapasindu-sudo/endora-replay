using System.Diagnostics;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;

namespace EndoraReplay.Core.Vmix;

public enum BridgeState
{
    /// <summary>Live cameras on air; waiting for a replay.</summary>
    Idle,

    /// <summary>Stinger into the replay is running.</summary>
    GoingToReplay,

    /// <summary>The vMix replay is on air through the ATEM.</summary>
    OnAir,

    /// <summary>Stinger back to live is running.</summary>
    Returning,

    /// <summary>Emergency override: Endora sends nothing to the ATEM or vMix.</summary>
    Override,
}

public sealed record BridgeStatus(
    BridgeState State,
    bool VmixConnected,
    bool AtemConnected,
    int? ReplayInput,
    int? BlackInput,
    IReadOnlySet<int> VmixProgram,
    int? AtemProgram,
    int? ReturnCamera,
    bool ReplayPlaying,
    bool AutoReturnHeld,
    string? LastAlert)
{
    public string StateText => State switch
    {
        BridgeState.Idle => "READY",
        BridgeState.GoingToReplay => "STINGER IN",
        BridgeState.OnAir => "REPLAY ON AIR",
        BridgeState.Returning => "STINGER OUT",
        BridgeState.Override => "OVERRIDE",
        _ => State.ToString(),
    };
}

/// <summary>
/// vMix replay automation. When the vMix replay starts playing (or REPLAY is pressed in Endora, on a Stream Deck,
/// a phone or a vMix shortcut), Endora plays the vMix stinger into the replay and cuts the ATEM to the vMix input at
/// the stinger's transition point. When the replay stops (or LIVE is pressed) it plays the stinger back and cuts the
/// ATEM to the camera that was on air before. With <see cref="VmixStingerMode.Keyed"/> the vMix output is luma-keyed
/// on DSK 1 so the whole stinger is seen over the live picture.
/// </summary>
public sealed class VmixBridge : IAsyncDisposable
{
    /// <summary>Keyed mode: after the cut to vMix, wait this long before DSK 1 goes off, so the cut has happened first.</summary>
    public const int KeyOffAfterCutMs = 80;

    /// <summary>Keyed mode back to live: DSK 1 goes on this long before the cut, so the key is on air first.</summary>
    public const int KeyOnBeforeCutMs = 40;

    private const int MonitorIntervalMs = 40;
    private const int PlayingPollMs = 250;

    private readonly IVmix _vmix;
    private readonly AtemSwitcherService _atem;
    private readonly Func<AppSettings> _settings;
    private readonly DiagnosticsService _diag;
    private readonly SemaphoreSlim _sequence = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();
    private Task? _monitor;
    private CancellationTokenSource? _sequenceCts;

    private BridgeState _state = BridgeState.Idle;
    private int? _returnCamera;
    private int? _vmixBefore;
    private bool _held;
    private bool _seenReplayOnProgram;
    private bool _seenPlaying;
    private long _stoppedSinceMs = -1;
    private bool _awaitReplayOff;
    private bool _lastPlaying;
    private bool? _polledRunning;
    private long _polledAtMs = -10_000;
    private string? _lastAlert;
    private bool _scoreboardHidden;

    public VmixBridge(IVmix vmix, AtemSwitcherService atem, Func<AppSettings> settings, DiagnosticsService diagnostics)
    {
        _vmix = vmix;
        _atem = atem;
        _settings = settings;
        _diag = diagnostics;
    }

    /// <summary>Hides the scoreboard for the replay. Returns true when it was on air and has been taken off.</summary>
    public Func<bool>? HideScoreboard { get; set; }

    /// <summary>Puts the scoreboard back after the replay.</summary>
    public Action? ShowScoreboard { get; set; }

    /// <summary>Operator scoreboard toggle. Returns an error, or null.</summary>
    public Func<string?>? ToggleScoreboard { get; set; }

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<string>? Alerted;

    public BridgeState State { get { lock (_gate) return _state; } }

    private VmixSettings V => _settings().Vmix;
    private int VmixAtemInput => _settings().Atem.ReplayInput;

    public int? ReplayInputNumber => VmixProtocol.ResolveInput(V.ReplayInput, _vmix.Snapshot.Inputs, "Replay");
    public int? BlackInputNumber => VmixProtocol.ResolveInput(V.BlackInput, _vmix.Snapshot.Inputs);

    public BridgeStatus Status
    {
        get
        {
            var s = _vmix.Snapshot;
            var a = _atem.Snapshot;
            var replay = ReplayInputNumber;
            lock (_gate)
            {
                return new BridgeStatus(_state, s.Connected, a.Connected, replay, BlackInputNumber, s.Program, a.ProgramInput,
                    _returnCamera, PlayingNow(s, replay), _held, _lastAlert);
            }
        }
    }

    public void Start() => _monitor ??= Task.Run(() => MonitorAsync(_life.Token));

    // ------------------------------------------------------------------ operator commands

    /// <summary>Runs an operator action (same names as the keyboard actions). Returns an error for the operator, or null.</summary>
    public string? Execute(string action, string origin = "operator")
    {
        switch (action)
        {
            case OperatorActions.Replay: return StartReplay(origin);
            case OperatorActions.Live: return StartReturn(origin, ReturnKind.Stinger);
            case OperatorActions.Stop: return StartReturn(origin, ReturnKind.Cut);
            case OperatorActions.CancelAuto: return ToggleHold();
            case OperatorActions.EmergencyOverride: return ToggleOverride(origin);
            case OperatorActions.Scoreboard: return ToggleScoreboard?.Invoke() ?? "Scoreboard is not set up.";
            case OperatorActions.ClearError:
                lock (_gate) _lastAlert = null;
                Changed();
                return null;
            default: return $"{action} is not used with vMix replay.";
        }
    }

    public string? StartReplay(string origin)
    {
        if (CanStartReplay() is { } why) return why;
        lock (_gate)
        {
            if (_state != BridgeState.Idle) return "A replay is already on air.";
            _state = BridgeState.GoingToReplay;
        }
        Changed();
        _diag.Info(LogCategory.Automation, $"REPLAY ({origin})");
        RunSequence("REPLAY", ReplaySequenceAsync);
        return null;
    }

    private string? CanStartReplay()
    {
        if (State == BridgeState.Override) return "Emergency override is on: Endora sends nothing.";
        if (!_vmix.Snapshot.Connected) return "vMix is not connected.";
        if (!_atem.IsConnected) return "ATEM is not connected.";
        if (ReplayInputNumber is null) return "No replay input found in vMix. Add a Replay input or enter its number in Settings.";
        if (V.Mode == VmixStingerMode.Keyed && BlackInputNumber is null)
            return "Keyed stinger needs the black input. Enter its number or title in Settings (vMix: Add Input → Colour, black).";
        return null;
    }

    private enum ReturnKind
    {
        /// <summary>vMix stinger back to live, then the ATEM cut.</summary>
        Stinger,

        /// <summary>Straight ATEM cut to the camera (emergency LIVE).</summary>
        Cut,

        /// <summary>vMix already left the replay by itself; only the ATEM follows.</summary>
        Follow,
    }

    private string? StartReturn(string origin, ReturnKind kind)
    {
        BridgeState before;
        lock (_gate)
        {
            before = _state;
            if (before == BridgeState.Override) return "Emergency override is on: Endora sends nothing.";
            if (before == BridgeState.Idle && kind != ReturnKind.Cut) return "No replay is on air.";
            if (before == BridgeState.Returning && kind != ReturnKind.Cut) return null;
        }
        if (!_atem.IsConnected) return "ATEM is not connected.";
        if (kind == ReturnKind.Cut)
        {
            // Emergency: stop whatever is running and cut now.
            CancelSequence();
        }
        lock (_gate) _state = BridgeState.Returning;
        Changed();
        _diag.Info(LogCategory.Automation, $"{(kind == ReturnKind.Cut ? "CUT TO LIVE" : "LIVE")} ({origin})");
        RunSequence(kind == ReturnKind.Cut ? "CUT TO LIVE" : "LIVE", ct => ReturnSequenceAsync(kind, ct));
        return null;
    }

    private string? ToggleHold()
    {
        lock (_gate)
        {
            if (_state != BridgeState.OnAir) return "No replay is on air.";
            _held = !_held;
        }
        _diag.Info(LogCategory.Automation, _held ? "Replay held on air: no automatic return to live." : "Automatic return to live is back on.");
        Changed();
        return null;
    }

    private string? ToggleOverride(string origin)
    {
        bool on;
        lock (_gate)
        {
            on = _state != BridgeState.Override;
            _state = on ? BridgeState.Override : BridgeState.Idle;
            if (!on)
            {
                // Do not start again for a replay that is still running in vMix.
                _awaitReplayOff = true;
                _returnCamera = null;
            }
        }
        if (on) CancelSequence();
        _diag.Warn(LogCategory.Automation, on
            ? $"EMERGENCY OVERRIDE ON ({origin}): Endora sends nothing to the ATEM or vMix. Switch by hand."
            : $"Emergency override off ({origin}). Automation is back.");
        Changed();
        return null;
    }

    // ------------------------------------------------------------------ sequences

    private void RunSequence(string name, Func<CancellationToken, Task> body)
    {
        _ = Task.Run(async () =>
        {
            await _sequence.WaitAsync().ConfigureAwait(false);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            lock (_gate) _sequenceCts = cts;
            try
            {
                await body(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _diag.Warn(LogCategory.Automation, $"{name} stopped part-way.");
            }
            catch (Exception e)
            {
                Alert($"{name} failed: {e.Message}");
                await RecoverAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_sequenceCts, cts)) _sequenceCts = null;
                }
                cts.Dispose();
                _sequence.Release();
                Changed();
            }
        });
    }

    private void CancelSequence()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _sequenceCts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ReplaySequenceAsync(CancellationToken ct)
    {
        var v = V;
        var vmixIn = VmixAtemInput;
        var replay = ReplayInputNumber ?? throw new InvalidOperationException("No replay input in vMix.");
        var vs = _vmix.Snapshot;
        var a = _atem.Snapshot;
        lock (_gate)
        {
            _returnCamera = PickReturnCamera(a, vmixIn, _returnCamera);
            _vmixBefore = vs.Program.Where(n => n != replay).Cast<int?>().FirstOrDefault();
        }
        HideScoreboardForReplay();
        await RunFunctionsAsync(v.FunctionsOnReplay, ct).ConfigureAwait(false);

        switch (v.Mode)
        {
            case VmixStingerMode.AtemOnly:
                if (!_vmix.Snapshot.OnProgram(replay)) await FunctionAsync("CutDirect", replay, ct).ConfigureAwait(false);
                await _atem.SetPreviewAsync(vmixIn).ConfigureAwait(false);
                if (v.AtemOnlyCut) await _atem.CutAsync().ConfigureAwait(false);
                else await _atem.AutoAsync(v.AtemOnlyStyle).ConfigureAwait(false);
                break;

            case VmixStingerMode.CoverCut:
            {
                await FunctionAsync(v.TransitionIn, replay, ct).ConfigureAwait(false);
                var t0 = _clock.ElapsedMilliseconds;
                await WaitUntilAsync(t0 + v.CutPointMs, ct).ConfigureAwait(false);
                await _atem.SetPreviewAsync(vmixIn).ConfigureAwait(false);
                await _atem.CutAsync().ConfigureAwait(false);
                break;
            }

            case VmixStingerMode.Keyed:
            {
                var black = BlackInputNumber ?? throw new InvalidOperationException("No black input in vMix.");
                // vMix must show black before the key goes on air, or its picture would flash over live.
                if (!_vmix.Snapshot.OnProgram(black))
                {
                    await FunctionAsync("CutDirect", black, ct).ConfigureAwait(false);
                    await Task.Delay(v.BlackLeadMs, ct).ConfigureAwait(false);
                }
                await _atem.ConfigureDownstreamKeyAsync(AutomationSettings.StingerKeyer, vmixIn, AutomationSettings.StingerKeyClip, AutomationSettings.StingerKeyGain)
                    .ConfigureAwait(false);
                await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, true).ConfigureAwait(false);
                await Task.Delay(v.BlackLeadMs, ct).ConfigureAwait(false);

                await FunctionAsync(v.TransitionIn, replay, ct).ConfigureAwait(false);
                var t0 = _clock.ElapsedMilliseconds;
                await WaitUntilAsync(t0 + v.CutPointMs, ct).ConfigureAwait(false);
                await _atem.SetPreviewAsync(vmixIn).ConfigureAwait(false);
                await _atem.CutAsync().ConfigureAwait(false);
                // vMix is now on Program under the key, so the key shows the same picture; take it off after the cut.
                await Task.Delay(KeyOffAfterCutMs, ct).ConfigureAwait(false);
                await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, false).ConfigureAwait(false);
                break;
            }
        }

        var now = _vmix.Snapshot;
        lock (_gate)
        {
            _seenReplayOnProgram = now.OnProgram(replay);
            _seenPlaying = PlayingNow(now, replay);
            _stoppedSinceMs = -1;
            _held = false;
            // LIVE may already have been pressed during the stinger; then the return runs next.
            if (_state == BridgeState.GoingToReplay) _state = BridgeState.OnAir;
        }
        _diag.Info(LogCategory.Automation, $"Replay on air (ATEM input {vmixIn}); back to camera {_returnCamera} afterwards.");
        Changed();
    }

    private async Task ReturnSequenceAsync(ReturnKind kind, CancellationToken ct)
    {
        var v = V;
        var vmixIn = VmixAtemInput;
        var a = _atem.Snapshot;
        int camera;
        lock (_gate) camera = _returnCamera ?? PickReturnCamera(a, vmixIn, null) ?? 1;
        var black = BlackInputNumber;
        var keyed = v.Mode == VmixStingerMode.Keyed;

        if (kind == ReturnKind.Cut)
        {
            if (a.IsDownstreamKeyOnAir(AutomationSettings.StingerKeyer) && keyed)
                await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, false).ConfigureAwait(false);
            await _atem.SetPreviewAsync(camera).ConfigureAwait(false);
            await _atem.CutAsync().ConfigureAwait(false);
            if (black is { } b && _vmix.Snapshot.Connected) await FunctionAsync("CutDirect", b, CancellationToken.None).ConfigureAwait(false);
        }
        else if (v.Mode == VmixStingerMode.AtemOnly)
        {
            await _atem.SetPreviewAsync(camera).ConfigureAwait(false);
            if (v.AtemOnlyCut || kind == ReturnKind.Follow) await _atem.CutAsync().ConfigureAwait(false);
            else
            {
                await _atem.AutoAsync(v.AtemOnlyStyle).ConfigureAwait(false);
                await WaitForAtemTransitionAsync(ct).ConfigureAwait(false);
            }
            if (kind == ReturnKind.Stinger && black is { } b) await FunctionAsync("CutDirect", b, ct).ConfigureAwait(false);
        }
        else
        {
            var t0 = _clock.ElapsedMilliseconds;
            if (kind == ReturnKind.Stinger)
            {
                // Back to black (or to whatever vMix showed before the replay).
                var target = black ?? (keyed ? null : _vmixBefore);
                await FunctionAsync(v.TransitionOut, target, ct).ConfigureAwait(false);
                t0 = _clock.ElapsedMilliseconds;
                await WaitUntilAsync(t0 + v.CutPointMs, ct).ConfigureAwait(false);
            }
            if (keyed)
            {
                // Key vMix over Program (vMix itself, so nothing changes), then cut the camera in underneath:
                // the rest of the stinger plays over the live picture.
                await _atem.ConfigureDownstreamKeyAsync(AutomationSettings.StingerKeyer, vmixIn, AutomationSettings.StingerKeyClip, AutomationSettings.StingerKeyGain)
                    .ConfigureAwait(false);
                await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, true).ConfigureAwait(false);
                await Task.Delay(KeyOnBeforeCutMs, ct).ConfigureAwait(false);
            }
            await _atem.SetPreviewAsync(camera).ConfigureAwait(false);
            await _atem.CutAsync().ConfigureAwait(false);
            if (keyed)
            {
                var rest = kind == ReturnKind.Stinger ? Math.Max(0, v.StingerLengthMs - v.CutPointMs) + 200 : 200;
                await Task.Delay(rest, ct).ConfigureAwait(false);
                await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, false).ConfigureAwait(false);
            }
        }

        await RunFunctionsAsync(v.FunctionsOnLive, CancellationToken.None).ConfigureAwait(false);
        RestoreScoreboard();
        lock (_gate)
        {
            _state = BridgeState.Idle;
            _awaitReplayOff = true;
            _held = false;
            _returnCamera = null;
        }
        _diag.Info(LogCategory.Automation, $"Back to live on camera {camera}.");
        Changed();
    }

    /// <summary>After a failure: put things in the safest state the switcher allows and say what is on air.</summary>
    private async Task RecoverAsync()
    {
        var a = _atem.Snapshot;
        if (V.Mode == VmixStingerMode.Keyed && a.Connected && a.IsDownstreamKeyOnAir(AutomationSettings.StingerKeyer))
        {
            try { await _atem.SetDownstreamKeyOnAirAsync(AutomationSettings.StingerKeyer, false).ConfigureAwait(false); }
            catch (Exception e) { _diag.Error(LogCategory.Atem, $"Could not take DSK 1 off: {e.Message}"); }
        }
        var onVmix = _atem.Snapshot.ProgramInput == VmixAtemInput;
        lock (_gate)
        {
            if (_state != BridgeState.Override) _state = onVmix ? BridgeState.OnAir : BridgeState.Idle;
            if (!onVmix)
            {
                _awaitReplayOff = true;
                _returnCamera = null;
            }
            _seenReplayOnProgram = false;
            _seenPlaying = false;
        }
        if (!onVmix) RestoreScoreboard();
        Changed();
    }

    // ------------------------------------------------------------------ watching vMix and the ATEM

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(MonitorIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { _diag.Warn(LogCategory.Automation, $"Watcher: {e.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One look at vMix and the ATEM. Public for tests.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var s = _vmix.Snapshot;
        if (!s.Connected) return;
        var v = V;
        var replay = VmixProtocol.ResolveInput(v.ReplayInput, s.Inputs, "Replay");
        if (replay is null) return;
        var state = State;
        if (state is BridgeState.Override or BridgeState.GoingToReplay or BridgeState.Returning) return;

        // vMix's own "state" of the input is the most reliable sign of play; ask a few times a second.
        if (_clock.ElapsedMilliseconds - _polledAtMs >= PlayingPollMs)
        {
            _polledAtMs = _clock.ElapsedMilliseconds;
            var running = await _vmix.IsInputRunningAsync(replay.Value, ct).ConfigureAwait(false);
            lock (_gate) _polledRunning = running;
        }
        bool playing;
        lock (_gate) playing = PlayingNow(s, replay);
        var a = _atem.Snapshot;
        var vmixIn = VmixAtemInput;

        if (state == BridgeState.Idle)
        {
            bool wasPlaying;
            lock (_gate)
            {
                wasPlaying = _lastPlaying;
                _lastPlaying = playing;
                if (_awaitReplayOff)
                {
                    if (!playing && !s.OnProgram(replay)) _awaitReplayOff = false;
                    return;
                }
            }
            if (v.StartWhenReplayPlays && playing && !wasPlaying && !s.OnProgram(replay))
            {
                var err = StartReplay("vMix replay started playing");
                if (err is not null) Alert($"vMix replay started but the automation could not run: {err}");
                return;
            }
            if (v.FollowVmixProgram && s.OnProgram(replay) && a.Connected && a.ProgramInput != vmixIn)
                FollowIntoReplay(replay.Value);
            return;
        }

        // On air.
        lock (_gate)
        {
            _lastPlaying = playing;
            if (s.OnProgram(replay)) _seenReplayOnProgram = true;
            if (playing)
            {
                _seenPlaying = true;
                _stoppedSinceMs = -1;
            }
        }
        if (a.Connected && a.ProgramInput is { } pgm && pgm != vmixIn && !a.InTransition)
        {
            lock (_gate)
            {
                _state = BridgeState.Idle;
                _awaitReplayOff = true;
                _returnCamera = null;
            }
            _diag.Warn(LogCategory.Automation, $"The ATEM was switched to input {pgm} by hand; the replay is off air.");
            RestoreScoreboard();
            Changed();
            return;
        }
        bool seenOnProgram, seenPlaying, held;
        lock (_gate)
        {
            seenOnProgram = _seenReplayOnProgram;
            seenPlaying = _seenPlaying;
            held = _held;
        }
        if (v.FollowVmixProgram && seenOnProgram && !s.OnProgram(replay))
        {
            _diag.Info(LogCategory.Automation, "vMix took the replay off Program; the ATEM follows back to live.");
            StartReturn("vMix", ReturnKind.Follow);
            return;
        }
        if (v.AutoReturnWhenReplayStops && !held && seenPlaying && !playing)
        {
            long since;
            lock (_gate)
            {
                if (_stoppedSinceMs < 0) _stoppedSinceMs = _clock.ElapsedMilliseconds;
                since = _stoppedSinceMs;
            }
            if (_clock.ElapsedMilliseconds - since >= v.StopGraceMs)
            {
                _diag.Info(LogCategory.Automation, "vMix replay stopped; going back to live.");
                StartReturn("replay stopped", ReturnKind.Stinger);
            }
        }
    }

    /// <summary>vMix put the replay on Program by itself: bring the ATEM to vMix at the stinger's transition point.</summary>
    private void FollowIntoReplay(int replay)
    {
        lock (_gate)
        {
            if (_state != BridgeState.Idle) return;
            _state = BridgeState.GoingToReplay;
        }
        _diag.Info(LogCategory.Automation, $"vMix put the replay (input {replay}) on Program; the ATEM follows. For the full stinger, start replays from vMix play or Endora REPLAY.");
        Changed();
        RunSequence("FOLLOW vMix", async ct =>
        {
            var v = V;
            var vmixIn = VmixAtemInput;
            lock (_gate) _returnCamera = PickReturnCamera(_atem.Snapshot, vmixIn, _returnCamera);
            HideScoreboardForReplay();
            if (v.Mode != VmixStingerMode.AtemOnly) await Task.Delay(v.CutPointMs, ct).ConfigureAwait(false);
            await _atem.SetPreviewAsync(vmixIn).ConfigureAwait(false);
            await _atem.CutAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _seenReplayOnProgram = true;
                _seenPlaying = PlayingNow(_vmix.Snapshot, replay);
                _stoppedSinceMs = -1;
                _held = false;
                if (_state == BridgeState.GoingToReplay) _state = BridgeState.OnAir;
            }
            Changed();
        });
    }

    // ------------------------------------------------------------------ helpers

    private bool PlayingNow(VmixSnapshot s, int? replay)
    {
        if (replay is null) return false;
        var pollFresh = _clock.ElapsedMilliseconds - _polledAtMs < PlayingPollMs * 3;
        return pollFresh && _polledRunning is { } polled ? polled : s.IsPlaying(replay);
    }

    /// <summary>The camera to return to: what the ATEM had on Program before the replay (or its Preview when that was vMix).</summary>
    private static int? PickReturnCamera(AtemSnapshot a, int vmixIn, int? current)
    {
        if (a.ProgramInput is { } p && p != vmixIn) return p;
        if (current is { } c) return c;
        if (a.PreviewInput is { } q && q != vmixIn) return q;
        return null;
    }

    private async Task FunctionAsync(string function, int? input, CancellationToken ct)
    {
        var err = await _vmix.FunctionAsync(function, input is { } n ? VmixProtocol.InputQuery(n) : null, ct).ConfigureAwait(false);
        if (err is not null) throw new InvalidOperationException(err);
    }

    private async Task RunFunctionsAsync(IEnumerable<string> lines, CancellationToken ct)
    {
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var (fn, query) = VmixProtocol.SplitFunction(line);
            if (await _vmix.FunctionAsync(fn, query, ct).ConfigureAwait(false) is { } err)
                Alert($"vMix {fn}: {err}");
        }
    }

    private async Task WaitUntilAsync(long targetMs, CancellationToken ct)
    {
        var remaining = targetMs - _clock.ElapsedMilliseconds;
        if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct).ConfigureAwait(false);
    }

    private async Task WaitForAtemTransitionAsync(CancellationToken ct)
    {
        var until = _clock.ElapsedMilliseconds + 5000;
        await Task.Delay(60, ct).ConfigureAwait(false);
        while (_atem.Snapshot.InTransition && _clock.ElapsedMilliseconds < until)
            await Task.Delay(20, ct).ConfigureAwait(false);
    }

    private void HideScoreboardForReplay()
    {
        try
        {
            if (HideScoreboard?.Invoke() == true)
            {
                lock (_gate) _scoreboardHidden = true;
                _diag.Info(LogCategory.Atem, "Scoreboard hidden for the replay.");
            }
        }
        catch (Exception e) { _diag.Warn(LogCategory.Atem, $"Scoreboard: {e.Message}"); }
    }

    private void RestoreScoreboard()
    {
        bool hidden;
        lock (_gate)
        {
            hidden = _scoreboardHidden;
            _scoreboardHidden = false;
        }
        if (!hidden) return;
        try { ShowScoreboard?.Invoke(); }
        catch (Exception e) { _diag.Warn(LogCategory.Atem, $"Scoreboard: {e.Message}"); }
    }

    private void Alert(string message)
    {
        lock (_gate) _lastAlert = message;
        _diag.Error(LogCategory.Automation, message);
        Alerted?.Invoke(message);
        Changed();
    }

    private void Changed()
    {
        try { StatusChanged?.Invoke(Status); }
        catch (Exception e) { _diag.Warn(LogCategory.App, $"Status listener: {e.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        if (_monitor is not null)
        {
            try { await _monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await _sequence.WaitAsync().ConfigureAwait(false);
        _sequence.Release();
        _life.Dispose();
    }
}
