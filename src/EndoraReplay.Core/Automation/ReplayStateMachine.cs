using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Automation;

public enum ReplayState
{
    DISCONNECTED,
    LIVE_READY,
    RECORDING,
    REPLAY_ARMED,
    WAITING_FOR_PROGRAM,
    PLAYING,
    REPLAY_COMPLETE,
    ERROR,
}

// ---------------- inputs ----------------

public abstract record AutomationEvent;

/// <summary>Latest switcher state. Program edges are detected by comparing with the previous event.</summary>
public sealed record AtemStateEvent(bool Connected, int? ProgramInput, int? PreviewInput) : AutomationEvent;

public sealed record RecordingStateEvent(bool Recording) : AutomationEvent;
public sealed record OutputReadinessEvent(bool Ready, string Message) : AutomationEvent;
public sealed record ArmRequested(ClipRef Clip) : AutomationEvent;
public sealed record DisarmRequested : AutomationEvent;
public sealed record ReplayRequested(string Origin) : AutomationEvent;
public sealed record LiveRequested(string Origin) : AutomationEvent;
public sealed record StopRequested(string Origin) : AutomationEvent;
public sealed record CancelAutoRequested(string Origin) : AutomationEvent;

/// <summary>Engage: Endora sends no switcher commands at all until released. Release does not resume automation by itself.</summary>
public sealed record EmergencyOverrideRequested(bool Engage, string Origin) : AutomationEvent;

public sealed record ClearErrorRequested(string Origin) : AutomationEvent;
public sealed record StingerCutPoint(bool IsReturn) : AutomationEvent;

/// <summary>The Endora stinger finished playing on the replay output.</summary>
public sealed record StingerFinished(bool IsReturn) : AutomationEvent;

/// <summary>The operator switched the running replay to another camera angle.</summary>
public sealed record PlayingAngleChanged(ClipRef Clip) : AutomationEvent;
public sealed record ClipPlaybackStarted : AutomationEvent;
public sealed record ClipPlaybackCompleted : AutomationEvent;
public sealed record PlayoutFinished : AutomationEvent;
public sealed record PlaybackFailed(string Reason) : AutomationEvent;
public sealed record SwitcherCommandFailed(string Reason) : AutomationEvent;
public sealed record Tick : AutomationEvent;

// ---------------- outputs ----------------

public abstract record AutomationAction;

public sealed record SetPreviewAction(int Input) : AutomationAction;

/// <summary>AUTO (with style) or CUT on the ATEM.</summary>
public sealed record TransitionAction(TransitionMode Mode, AtemTransitionStyle Style) : AutomationAction;

public sealed record CutAction : AutomationAction;

/// <param name="WithStingerIn">Play the Endora stinger before the clip.</param>
/// <param name="Gated">Hold before the clip until <see cref="ReleaseClipAction"/> (Program confirmation).</param>
public sealed record StartPlayoutAction(ClipRef Clip, bool WithStingerIn, bool Gated, int DelayMs) : AutomationAction;

public sealed record ReleaseClipAction(int DelayMs) : AutomationAction;
public sealed record PlayStingerOutAction : AutomationAction;
public sealed record StopPlayoutAction : AutomationAction;

/// <summary>Self-keyed luma key of the replay input on a downstream keyer, so the stinger shows over any source.</summary>
public sealed record DskConfigureAction(int Keyer, int Input, double ClipPercent, double GainPercent) : AutomationAction;

public sealed record DskOnAirAction(int Keyer, bool OnAir) : AutomationAction;
public sealed record ShowHoldingAction : AutomationAction;

public enum AlertLevel
{
    Info,
    Warning,
    Critical,
}

public sealed record AlertAction(AlertLevel Level, string Message) : AutomationAction;

/// <summary>Options the state machine needs; a snapshot of <see cref="AutomationSettings"/>.</summary>
public sealed record AutomationOptions(
    bool Enabled,
    int ReplayInput,
    bool AutoSwitchIn,
    bool StartOnProgramEdge,
    ReturnToLiveMode ReturnToLive,
    TransitionMode TransitionIn,
    AtemTransitionStyle TransitionInStyle,
    TransitionMode TransitionOut,
    AtemTransitionStyle TransitionOutStyle,
    int PrerollMs,
    int LockoutMs,
    int WaitForProgramTimeoutMs,
    bool StingerKey = false,
    int DskKeyer = 1,
    double DskClipPercent = 8,
    double DskGainPercent = 70,
    int StingerFadeMs = 0,
    int StingerSwitchDelayFrames = 2)
{
    public static AutomationOptions From(AppSettings s) => new(
        s.Automation.Enabled, s.Atem.ReplayInput, s.Automation.AutoSwitchIn, s.Automation.StartOnProgramEdge,
        s.Automation.ReturnToLive, s.Automation.TransitionIn, s.Automation.TransitionInStyle,
        s.Automation.TransitionOut, s.Automation.TransitionOutStyle, s.Automation.PrerollMs,
        s.Automation.LockoutMs, s.Automation.WaitForProgramTimeoutMs,
        s.Automation.StingerKey, s.Automation.StingerKeyer, s.Automation.StingerKeyClip, s.Automation.StingerKeyGain,
        s.Automation.StingerFadeMs, s.Automation.StingerSwitchDelayFrames);
}

/// <summary>
/// Deterministic replay automation. Pure: no I/O, no clocks of its own, no threads. Each event plus the
/// current time yields the actions to perform, so every decision is unit-testable and replayable from logs.
/// <para>Safety rules enforced here:</para>
/// <list type="bullet">
/// <item>Playback starts automatically only with an armed clip, a ready output and a confirmed trigger.</item>
/// <item>Program edges trigger at most once; staying on the replay input never re-triggers.</item>
/// <item>No switcher command is issued while disconnected, in ERROR, under override, or with automation disabled
/// (explicit operator LIVE excepted, which still respects disconnect and override).</item>
/// <item>Endora never switches away from live unless automatic switching is enabled.</item>
/// </list>
/// </summary>
public sealed class ReplayStateMachine
{
    private AutomationOptions _o;
    private bool _connected;
    private int? _program;
    private int? _preview;
    private bool _recording;
    private bool _outputReady;
    private string _outputMessage = "not opened";
    private ClipRef? _armed;
    private ClipRef? _playing;
    private int? _lastLiveInput;
    private bool _override;
    private bool _cycleAutoCancelled;
    private bool _returnIssued;
    private bool _dskOnAir;
    private bool _stingerOutRunning;
    private long _waitDeadline;
    private long _lockoutUntil;
    private ReplayState _state = ReplayState.DISCONNECTED;

    public ReplayStateMachine(AutomationOptions options) => _o = options;

    public ReplayState State => _state;
    public ClipRef? ArmedClip => _armed;
    public ClipRef? PlayingClip => _playing;
    public bool OverrideActive => _override;

    /// <summary>True while Endora has put its stinger key on air.</summary>
    public bool StingerKeyOnAir => _dskOnAir;
    public bool AtemConnected => _connected;
    public int? ProgramInput => _program;
    public int? PreviewInput => _preview;
    public int? LastLiveInput => _lastLiveInput;
    public bool OutputReady => _outputReady;
    public string? ErrorReason { get; private set; }
    public AutomationOptions Options => _o;

    /// <summary>True when Endora may send automatic switcher commands right now.</summary>
    public bool CanAutoSwitch => _o.Enabled && _connected && !_override && _state != ReplayState.ERROR;

    public event Action<ReplayState, ReplayState>? StateChanged;

    public void UpdateOptions(AutomationOptions options) => _o = options;

    public IReadOnlyList<AutomationAction> Handle(AutomationEvent e, long nowTicks)
    {
        var a = new List<AutomationAction>();
        switch (e)
        {
            case AtemStateEvent s: OnAtem(s, nowTicks, a); break;
            case RecordingStateEvent r: _recording = r.Recording; break;
            case OutputReadinessEvent o: OnOutput(o, a); break;
            case ArmRequested arm: OnArm(arm, a); break;
            case DisarmRequested: OnDisarm(a); break;
            case ReplayRequested r: OnReplay(r, nowTicks, a); break;
            case LiveRequested l: OnLive(l, a); break;
            case StopRequested s: OnStop(s, nowTicks, a); break;
            case CancelAutoRequested c: OnCancelAuto(c, a); break;
            case EmergencyOverrideRequested ov: OnOverride(ov, a); break;
            case ClearErrorRequested c: OnClearError(c, a); break;
            case StingerCutPoint cp: OnCutPoint(cp, a); break;
            case StingerFinished sf: OnStingerFinished(sf, nowTicks, a); break;
            case PlayingAngleChanged pa when _playing is not null: _playing = pa.Clip; break;
            case ClipPlaybackCompleted: OnClipCompleted(nowTicks, a); break;
            case PlayoutFinished: break;
            case PlaybackFailed f: Fail($"Playback failed: {f.Reason}", a, stopPlayout: true); break;
            case SwitcherCommandFailed f: Fail($"Switcher command failed: {f.Reason}", a, stopPlayout: false); break;
            case Tick: OnTick(nowTicks, a); break;
        }
        RecomputeIdleState();

        // Whenever the replay output is about to stop or show the holding frame, take Endora's key off air first,
        // so the holding slate is never keyed over the live picture.
        if (_dskOnAir && a.Any(x => x is StopPlayoutAction or ShowHoldingAction))
        {
            a.Insert(0, new DskOnAirAction(_o.DskKeyer, false));
            _dskOnAir = false;
            _stingerOutRunning = false;
        }
        return a;
    }

    // ---------------- handlers ----------------

    private void OnAtem(AtemStateEvent s, long now, List<AutomationAction> a)
    {
        var wasConnected = _connected;
        var prevProgram = _program;
        _connected = s.Connected;
        _preview = s.PreviewInput;

        if (!s.Connected)
        {
            _program = null;
            if (wasConnected)
            {
                a.Add(new AlertAction(AlertLevel.Critical, "ATEM disconnected. Automatic switching stopped; switch manually on the ATEM."));
                if (_state == ReplayState.WAITING_FOR_PROGRAM)
                {
                    // Never wait for a confirmation that cannot arrive.
                    a.Add(new StopPlayoutAction());
                    a.Add(new ShowHoldingAction());
                    _armed = _playing;
                    _playing = null;
                    SetState(ReplayState.REPLAY_ARMED);
                }
            }
            return;
        }

        if (!wasConnected) a.Add(new AlertAction(AlertLevel.Info, "ATEM connected."));
        _program = s.ProgramInput;
        if (_program is { } p && p != _o.ReplayInput) _lastLiveInput = p;

        // Edge detection: only a change of Program counts. A first report after (re)connect is not an edge.
        var edgeOntoReplay = wasConnected && prevProgram != _o.ReplayInput && _program == _o.ReplayInput;
        var edgeOffReplay = wasConnected && prevProgram == _o.ReplayInput && _program != _o.ReplayInput;

        switch (_state)
        {
            case ReplayState.WAITING_FOR_PROGRAM when edgeOntoReplay || (_program == _o.ReplayInput && !wasConnected):
                a.Add(new ReleaseClipAction(_o.PrerollMs));
                SetState(ReplayState.PLAYING);
                break;

            case ReplayState.REPLAY_ARMED when edgeOntoReplay:
                if (!_o.Enabled || !_o.StartOnProgramEdge || _override)
                {
                    a.Add(new AlertAction(AlertLevel.Info, $"Program is on input {_o.ReplayInput}; press REPLAY to play the armed clip."));
                    break;
                }
                if (now < _lockoutUntil)
                {
                    a.Add(new AlertAction(AlertLevel.Info, "Program edge ignored (re-arm lock-out)."));
                    break;
                }
                if (!_outputReady)
                {
                    a.Add(new AlertAction(AlertLevel.Warning, $"Program is on replay input but output is not ready: {_outputMessage}"));
                    break;
                }
                StartPlay(withStinger: false, gated: false, delayMs: _o.PrerollMs, a);
                SetState(ReplayState.PLAYING);
                break;

            case ReplayState.PLAYING when edgeOffReplay:
                // Operator took Program away mid-replay: stop, and don't fight them.
                a.Add(new AlertAction(AlertLevel.Warning, "Program left the replay input during playback. Replay stopped."));
                a.Add(new StopPlayoutAction());
                a.Add(new ShowHoldingAction());
                FinishCycle(now);
                break;

            case ReplayState.REPLAY_COMPLETE when edgeOffReplay && _stingerOutRunning:
                // The stinger OUT keeps playing over the live picture; the cycle ends when it finishes.
                break;

            case ReplayState.REPLAY_COMPLETE when edgeOffReplay:
                a.Add(new StopPlayoutAction());
                a.Add(new ShowHoldingAction());
                FinishCycle(now);
                break;
        }
    }

    private void OnOutput(OutputReadinessEvent o, List<AutomationAction> a)
    {
        var was = _outputReady;
        _outputReady = o.Ready;
        _outputMessage = o.Message;
        if (was && !o.Ready)
        {
            if (_state is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM)
                Fail($"Replay output lost: {o.Message}", a, stopPlayout: true);
            else
                a.Add(new AlertAction(AlertLevel.Critical, $"Replay output not ready: {o.Message}"));
        }
    }

    private void OnArm(ArmRequested arm, List<AutomationAction> a)
    {
        if (_state is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM)
        {
            a.Add(new AlertAction(AlertLevel.Warning, "Cannot arm while a replay is running. Stop it first or queue the clip."));
            return;
        }
        if (_state == ReplayState.ERROR)
        {
            a.Add(new AlertAction(AlertLevel.Warning, $"Clear the error first: {ErrorReason}"));
            return;
        }
        if (!_outputReady)
        {
            a.Add(new AlertAction(AlertLevel.Warning, $"Cannot arm: replay output not ready ({_outputMessage})."));
            return;
        }
        _armed = arm.Clip;
        _cycleAutoCancelled = false;
        if (_state == ReplayState.REPLAY_COMPLETE)
        {
            // Arming a new clip while the previous one holds on air: keep the hold, just arm.
            a.Add(new AlertAction(AlertLevel.Info, $"Armed {arm.Clip.Description} (previous replay still on air)."));
            return;
        }
        SetState(ReplayState.REPLAY_ARMED);
        a.Add(new AlertAction(AlertLevel.Info, $"Armed {arm.Clip.Description}"));
    }

    private void OnDisarm(List<AutomationAction> a)
    {
        if (_state != ReplayState.REPLAY_ARMED) return;
        _armed = null;
        a.Add(new AlertAction(AlertLevel.Info, "Disarmed."));
    }

    private void OnReplay(ReplayRequested r, long now, List<AutomationAction> a)
    {
        switch (_state)
        {
            case ReplayState.WAITING_FOR_PROGRAM:
            case ReplayState.PLAYING:
                a.Add(new AlertAction(AlertLevel.Info, $"Duplicate REPLAY from {r.Origin} ignored."));
                return;
            case ReplayState.ERROR:
                a.Add(new AlertAction(AlertLevel.Warning, $"REPLAY blocked: {ErrorReason}. Clear the error first."));
                return;
        }
        if (_armed is null)
        {
            a.Add(new AlertAction(AlertLevel.Warning, "No clip armed. Mark IN/OUT and save, or pick a clip."));
            return;
        }
        if (!_outputReady)
        {
            a.Add(new AlertAction(AlertLevel.Warning, $"REPLAY blocked: replay output not ready ({_outputMessage})."));
            return;
        }

        var stingerIn = _o.TransitionIn == TransitionMode.EndoraStinger;
        if (CanAutoSwitch && _o.AutoSwitchIn && _program != _o.ReplayInput)
        {
            // Switch in, then wait for Program to confirm before playing the clip.
            StartPlay(withStinger: stingerIn, gated: true, delayMs: 0, a);
            a.Add(new SetPreviewAction(_o.ReplayInput));
            if (!stingerIn) a.Add(new TransitionAction(_o.TransitionIn, _o.TransitionInStyle));
            else if (_o.StingerKey)
            {
                // The stinger runs keyed over the live picture until its cover point, where the ATEM cuts underneath.
                a.Add(new DskConfigureAction(_o.DskKeyer, _o.ReplayInput, _o.DskClipPercent, _o.DskGainPercent));
                a.Add(new DskOnAirAction(_o.DskKeyer, true));
                _dskOnAir = true;
            }
            // For the Endora stinger the CUT is sent at the stinger's cover frame (StingerCutPoint).
            _waitDeadline = now + TimeSpan.FromMilliseconds(_o.WaitForProgramTimeoutMs + (stingerIn ? 4000 : 0)).Ticks;
            SetState(ReplayState.WAITING_FOR_PROGRAM);
            return;
        }

        if (_connected && _program == _o.ReplayInput)
        {
            // Already on air: the operator explicitly asked to play.
            StartPlay(withStinger: false, gated: false, delayMs: 0, a);
            SetState(ReplayState.PLAYING);
            return;
        }

        if (_connected && _o.Enabled && _o.StartOnProgramEdge && !_override)
        {
            // Operator will cut on the ATEM; play on the Program edge.
            StartPlay(withStinger: false, gated: true, delayMs: 0, a);
            _waitDeadline = now + TimeSpan.FromMilliseconds(Math.Max(_o.WaitForProgramTimeoutMs, 30_000)).Ticks;
            a.Add(new AlertAction(AlertLevel.Info, $"Waiting for Program = input {_o.ReplayInput}. Cut on the ATEM to play."));
            SetState(ReplayState.WAITING_FOR_PROGRAM);
            return;
        }

        // Fully manual (automation off, disconnected or override): play now, no switching.
        StartPlay(withStinger: false, gated: false, delayMs: 0, a);
        a.Add(new AlertAction(AlertLevel.Info, "Manual replay: Endora is not switching the ATEM."));
        SetState(ReplayState.PLAYING);
    }

    private void StartPlay(bool withStinger, bool gated, int delayMs, List<AutomationAction> a)
    {
        _playing = _armed;
        _armed = null;
        _returnIssued = false;
        a.Add(new StartPlayoutAction(_playing!, withStinger, gated, delayMs));
    }

    private void OnLive(LiveRequested l, List<AutomationAction> a)
    {
        var onReplay = _connected && _program == _o.ReplayInput;
        if (onReplay && _connected && !_override)
        {
            if (_lastLiveInput is { } live)
            {
                a.Add(new SetPreviewAction(live));
                if (_o.TransitionOut == TransitionMode.EndoraStinger) StingerOut(a);
                else a.Add(new TransitionAction(_o.TransitionOut == TransitionMode.AtemCut ? TransitionMode.AtemCut : TransitionMode.AtemAuto, _o.TransitionOutStyle));
                _returnIssued = true;
                if (_state == ReplayState.PLAYING)
                {
                    if (_o.TransitionOut != TransitionMode.EndoraStinger) a.Add(new StopPlayoutAction());
                    SetState(ReplayState.REPLAY_COMPLETE);
                }
                else if (_state == ReplayState.WAITING_FOR_PROGRAM) SetState(ReplayState.REPLAY_COMPLETE);
            }
            else
            {
                a.Add(new AlertAction(AlertLevel.Warning, "LIVE: no previous live input is known. Switch on the ATEM."));
            }
            return;
        }

        if (_override)
            a.Add(new AlertAction(AlertLevel.Warning, "Manual override is engaged: switch on the ATEM. Playback stopped."));
        else if (!_connected)
            a.Add(new AlertAction(AlertLevel.Warning, "ATEM disconnected: switch on the ATEM. Playback stopped."));

        if (_state is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM or ReplayState.REPLAY_COMPLETE)
        {
            a.Add(new StopPlayoutAction());
            a.Add(new ShowHoldingAction());
            FinishCycle(0);
        }
    }

    private void OnStop(StopRequested s, long now, List<AutomationAction> a)
    {
        if (_state is not (ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM or ReplayState.REPLAY_COMPLETE)) return;
        a.Add(new StopPlayoutAction());
        a.Add(new ShowHoldingAction());
        if (_connected && _program == _o.ReplayInput)
        {
            // Holding frame is on air; the operator decides when to go LIVE.
            _cycleAutoCancelled = true;
            SetState(ReplayState.REPLAY_COMPLETE);
            a.Add(new AlertAction(AlertLevel.Warning, "Stopped. Replay input still on Program (holding frame). Press LIVE."));
        }
        else
        {
            FinishCycle(now);
        }
    }

    private void OnCancelAuto(CancelAutoRequested c, List<AutomationAction> a)
    {
        _cycleAutoCancelled = true;
        if (_state == ReplayState.WAITING_FOR_PROGRAM)
        {
            a.Add(new StopPlayoutAction());
            a.Add(new ShowHoldingAction());
            _armed = _playing;
            _playing = null;
            SetState(ReplayState.REPLAY_ARMED);
            a.Add(new AlertAction(AlertLevel.Info, "Automation cancelled; clip stays armed."));
        }
        else
        {
            a.Add(new AlertAction(AlertLevel.Info, "Automatic return cancelled for this replay."));
        }
    }

    private void OnOverride(EmergencyOverrideRequested ov, List<AutomationAction> a)
    {
        if (ov.Engage)
        {
            _override = true;
            a.Add(new AlertAction(AlertLevel.Critical, $"EMERGENCY MANUAL OVERRIDE ({ov.Origin}): Endora sends no switcher commands. Use the ATEM panel."));
            if (_state == ReplayState.WAITING_FOR_PROGRAM)
            {
                a.Add(new StopPlayoutAction());
                a.Add(new ShowHoldingAction());
                _armed = _playing;
                _playing = null;
                SetState(ReplayState.REPLAY_ARMED);
            }
            if (_state == ReplayState.ERROR)
            {
                ErrorReason = null;
                SetState(ReplayState.LIVE_READY);
            }
        }
        else
        {
            _override = false;
            a.Add(new AlertAction(AlertLevel.Warning, "Manual override released."));
        }
    }

    private void OnClearError(ClearErrorRequested c, List<AutomationAction> a)
    {
        if (_state != ReplayState.ERROR) return;
        a.Add(new AlertAction(AlertLevel.Info, $"Error cleared by {c.Origin}: {ErrorReason}"));
        ErrorReason = null;
        _playing = null;
        SetState(ReplayState.LIVE_READY);
    }

    private void StingerOut(List<AutomationAction> a)
    {
        if (_o.StingerKey) a.Add(new DskConfigureAction(_o.DskKeyer, _o.ReplayInput, _o.DskClipPercent, _o.DskGainPercent));
        a.Add(new PlayStingerOutAction());
        _stingerOutRunning = true;
    }

    private void OnCutPoint(StingerCutPoint cp, List<AutomationAction> a)
    {
        if (!CanAutoSwitch) return;
        if (!cp.IsReturn && _state == ReplayState.WAITING_FOR_PROGRAM && _program != _o.ReplayInput)
            a.Add(new CutAction());
        else if (cp.IsReturn && _state == ReplayState.REPLAY_COMPLETE && _program == _o.ReplayInput)
        {
            if (_o.StingerKey)
            {
                // Key first: keying the replay input over itself changes nothing, so the CUT underneath is invisible.
                a.Add(new DskOnAirAction(_o.DskKeyer, true));
                _dskOnAir = true;
            }
            a.Add(new CutAction());
        }
    }

    private void OnStingerFinished(StingerFinished sf, long now, List<AutomationAction> a)
    {
        if (!sf.IsReturn)
        {
            // Program is now on the replay input, where the key shows the same picture; take it off quietly.
            if (_dskOnAir && _connected)
            {
                a.Add(new DskOnAirAction(_o.DskKeyer, false));
                _dskOnAir = false;
            }
            return;
        }
        if (!_stingerOutRunning) return;
        _stingerOutRunning = false;
        if (_state != ReplayState.REPLAY_COMPLETE) return;
        a.Add(new ShowHoldingAction());
        if (!(_connected && _program == _o.ReplayInput)) FinishCycle(now);
    }

    private void OnClipCompleted(long now, List<AutomationAction> a)
    {
        if (_state != ReplayState.PLAYING) return;
        SetState(ReplayState.REPLAY_COMPLETE);

        var onReplay = _connected && _program == _o.ReplayInput;
        if (!onReplay)
        {
            // Not on air (manual replay or operator already cut away): nothing to return from.
            a.Add(new ShowHoldingAction());
            FinishCycle(now);
            return;
        }

        if (_o.ReturnToLive == ReturnToLiveMode.Automatic && CanAutoSwitch && !_cycleAutoCancelled && !_returnIssued)
        {
            if (_lastLiveInput is { } live)
            {
                a.Add(new SetPreviewAction(live));
                if (_o.TransitionOut == TransitionMode.EndoraStinger) StingerOut(a);
                else a.Add(new TransitionAction(_o.TransitionOut, _o.TransitionOutStyle));
                _returnIssued = true;
                _waitDeadline = now + TimeSpan.FromMilliseconds(_o.WaitForProgramTimeoutMs + 4000).Ticks;
            }
            else
            {
                a.Add(new AlertAction(AlertLevel.Warning, "Replay complete; previous live input unknown. Switch on the ATEM."));
            }
        }
        else
        {
            a.Add(new AlertAction(AlertLevel.Info, "Replay complete. Holding on the last frame; press LIVE when ready."));
        }
    }

    private void OnTick(long now, List<AutomationAction> a)
    {
        if (_state == ReplayState.WAITING_FOR_PROGRAM && now > _waitDeadline)
        {
            a.Add(new StopPlayoutAction());
            a.Add(new ShowHoldingAction());
            _armed = _playing;
            _playing = null;
            SetState(ReplayState.REPLAY_ARMED);
            a.Add(new AlertAction(AlertLevel.Warning, $"Program never switched to input {_o.ReplayInput}; replay not started. Clip stays armed."));
        }
        else if (_state == ReplayState.REPLAY_COMPLETE && _returnIssued && now > _waitDeadline && _program == _o.ReplayInput)
        {
            _returnIssued = false;
            a.Add(new AlertAction(AlertLevel.Warning, "Return to live did not happen. Press LIVE or switch on the ATEM."));
        }
    }

    // ---------------- helpers ----------------

    private void Fail(string reason, List<AutomationAction> a, bool stopPlayout)
    {
        ErrorReason = reason;
        if (stopPlayout)
        {
            a.Add(new StopPlayoutAction());
            a.Add(new ShowHoldingAction());
        }
        a.Add(new AlertAction(AlertLevel.Critical, reason + " Automatic switching suspended until the error is cleared."));
        _playing = null;
        SetState(ReplayState.ERROR);
    }

    private void FinishCycle(long now)
    {
        _playing = null;
        _returnIssued = false;
        _stingerOutRunning = false;
        _cycleAutoCancelled = false;
        _lockoutUntil = now + TimeSpan.FromMilliseconds(_o.LockoutMs).Ticks;
        SetState(_armed is not null ? ReplayState.REPLAY_ARMED : IdleState());
    }

    private ReplayState IdleState() => !_connected ? ReplayState.DISCONNECTED : _recording ? ReplayState.RECORDING : ReplayState.LIVE_READY;

    private void RecomputeIdleState()
    {
        if (_state is ReplayState.DISCONNECTED or ReplayState.LIVE_READY or ReplayState.RECORDING)
            SetState(_armed is not null ? ReplayState.REPLAY_ARMED : IdleState());
        else if (_state == ReplayState.REPLAY_ARMED && _armed is null)
            SetState(IdleState());
    }

    private void SetState(ReplayState s)
    {
        if (s == _state) return;
        var old = _state;
        _state = s;
        StateChanged?.Invoke(old, s);
    }
}
