using System.Threading.Channels;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;

namespace EndoraReplay.Core.Automation;

/// <summary>Stinger material for <see cref="TransitionMode.EndoraStinger"/>.</summary>
public interface IStingerProvider
{
    /// <summary>Returns the stinger frames and the frame index where the picture is fully covered, or null if none is loaded.</summary>
    (IFrameSequence Frames, int CutFrame)? Get(bool isReturn);
}

/// <summary>
/// Runs <see cref="ReplayStateMachine"/> on a single serialized event loop and performs its actions
/// against playback and the switcher. All inputs (UI, keyboard, phone, ATEM, playback) enter as events,
/// so ordering is deterministic and duplicate triggers are resolved in one place.
/// </summary>
public sealed class ReplayAutomationService : IAsyncDisposable
{
    private readonly ReplayStateMachine _sm;
    private readonly ReplayPlaybackService _playback;
    private readonly AtemSwitcherService _atem;
    private readonly IStingerProvider _stingers;
    private readonly Func<ClipRef, IFrameSequence> _openClip;
    private readonly DiagnosticsService _diag;
    private readonly IClock _clock;
    private readonly Channel<AutomationEvent> _events = Channel.CreateUnbounded<AutomationEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Task _ticker;

    public ReplayAutomationService(
        AutomationOptions options,
        ReplayPlaybackService playback,
        AtemSwitcherService atem,
        IStingerProvider stingers,
        Func<ClipRef, IFrameSequence> openClip,
        DiagnosticsService diagnostics,
        IClock clock)
    {
        _sm = new ReplayStateMachine(options);
        _playback = playback;
        _atem = atem;
        _stingers = stingers;
        _openClip = openClip;
        _diag = diagnostics;
        _clock = clock;

        _sm.StateChanged += (from, to) =>
        {
            _diag.Info(LogCategory.Automation, $"{from} → {to}");
            StateChanged?.Invoke(to);
        };

        _atem.StateChanged += s => Post(new AtemStateEvent(s.Connected, s.ProgramInput, s.PreviewInput));
        _playback.SegmentStarted += k => { if (k == PlayoutSegmentKind.Clip) Post(new ClipPlaybackStarted()); };
        _playback.SegmentCompleted += k => { if (k == PlayoutSegmentKind.Clip) Post(new ClipPlaybackCompleted()); };
        _playback.CutPointReached += k =>
        {
            if (k == PlayoutSegmentKind.StingerIn) Post(new StingerCutPoint(false));
            else if (k == PlayoutSegmentKind.StingerOut) Post(new StingerCutPoint(true));
        };
        _playback.PlayoutCompleted += () => Post(new PlayoutFinished());
        _playback.Failed += r => Post(new PlaybackFailed(r));
        _playback.Output.ReadinessChanged += (r, m) => Post(new OutputReadinessEvent(r == OutputReadiness.Ready, m));

        _loop = Task.Run(LoopAsync);
        _ticker = Task.Run(TickAsync);
        Post(new OutputReadinessEvent(_playback.Output.Readiness == OutputReadiness.Ready, _playback.Output.ReadinessMessage));
    }

    public ReplayState State => _sm.State;
    public ClipRef? ArmedClip => _sm.ArmedClip;
    public ClipRef? PlayingClip => _sm.PlayingClip;
    public bool OverrideActive => _sm.OverrideActive;
    public string? ErrorReason => _sm.ErrorReason;
    public AutomationOptions Options => _sm.Options;

    /// <summary>Raised on the automation thread.</summary>
    public event Action<ReplayState>? StateChanged;

    /// <summary>Operator alerts (info/warning/critical) raised on the automation thread.</summary>
    public event Action<AlertLevel, string>? Alert;

    public void Post(AutomationEvent e) => _events.Writer.TryWrite(e);

    public void UpdateOptions(AutomationOptions o) => Post(new OptionsChanged(o));

    private sealed record OptionsChanged(AutomationOptions Options) : AutomationEvent;

    private async Task TickAsync()
    {
        using var t = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await t.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false)) Post(new Tick());
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (e is OptionsChanged oc)
                {
                    _sm.UpdateOptions(oc.Options);
                    continue;
                }
                if (e is not Tick and not AtemStateEvent)
                    _diag.Log(LogSeverity.Debug, LogCategory.Automation, $"event {e}");

                IReadOnlyList<AutomationAction> actions;
                try
                {
                    actions = _sm.Handle(e, _clock.NowTicks);
                }
                catch (Exception ex)
                {
                    _diag.Error(LogCategory.Automation, $"State machine fault on {e.GetType().Name}: {ex}");
                    continue;
                }
                foreach (var a in actions) await ExecuteAsync(a).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ExecuteAsync(AutomationAction action)
    {
        try
        {
            switch (action)
            {
                case AlertAction al:
                    var sev = al.Level switch { AlertLevel.Critical => LogSeverity.Error, AlertLevel.Warning => LogSeverity.Warning, _ => LogSeverity.Info };
                    _diag.Log(sev, LogCategory.Automation, al.Message);
                    Alert?.Invoke(al.Level, al.Message);
                    break;

                case StartPlayoutAction sp:
                    StartPlayout(sp);
                    break;

                case ReleaseClipAction r:
                    _playback.Release(r.DelayMs);
                    break;

                case PlayStingerOutAction:
                    if (_stingers.Get(isReturn: true) is { } so)
                    {
                        _playback.Start([new PlayoutSegment(PlayoutSegmentKind.StingerOut, so.Frames, 1.0, so.CutFrame, Label: "stinger out")]);
                    }
                    else
                    {
                        _diag.Warn(LogCategory.Automation, "No stinger OUT loaded; using ATEM AUTO.");
                        _playback.Stop();
                        if (_sm.CanAutoSwitch) await _atem.AutoAsync(AtemTransitionStyle.Mix).ConfigureAwait(false);
                    }
                    break;

                case StopPlayoutAction:
                    _playback.Stop();
                    break;

                case ShowHoldingAction:
                    _playback.ShowHolding();
                    break;

                // Switcher actions: the state machine only emits these when switching is permitted.
                case SetPreviewAction p:
                    await _atem.SetPreviewAsync(p.Input).ConfigureAwait(false);
                    break;

                case CutAction:
                    await _atem.CutAsync().ConfigureAwait(false);
                    break;

                case TransitionAction t when t.Mode == TransitionMode.AtemCut:
                    await _atem.CutAsync().ConfigureAwait(false);
                    break;

                case TransitionAction t:
                    await _atem.AutoAsync(t.Style).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (action is SetPreviewAction or CutAction or TransitionAction or PlayStingerOutAction)
        {
            Post(new SwitcherCommandFailed(e.Message));
        }
        catch (Exception e)
        {
            Post(new PlaybackFailed(e.Message));
        }
    }

    private void StartPlayout(StartPlayoutAction sp)
    {
        var segments = new List<PlayoutSegment>();
        if (sp.WithStingerIn)
        {
            if (_stingers.Get(isReturn: false) is { } si)
                segments.Add(new PlayoutSegment(PlayoutSegmentKind.StingerIn, si.Frames, 1.0, si.CutFrame, Label: "stinger in"));
            else
                throw new InvalidOperationException("Endora stinger IN selected but no stinger is loaded.");
        }

        var clip = _openClip(sp.Clip);
        _playback.SetSpeed(sp.Clip.Speed);
        segments.Add(new PlayoutSegment(PlayoutSegmentKind.Clip, clip, sp.Clip.Speed, WaitForRelease: sp.Gated, Label: sp.Clip.Description));
        if (!sp.WithStingerIn) _playback.ShowHolding();
        _playback.Start(segments, sp.DelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_loop, _ticker).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
