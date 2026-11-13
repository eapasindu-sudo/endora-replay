using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Tests;

public class ReplayStateMachineTests
{
    private const int Replay = 8;
    private long _now;

    private static AutomationOptions Options(
        bool enabled = true,
        bool autoIn = true,
        bool edge = true,
        ReturnToLiveMode ret = ReturnToLiveMode.Automatic,
        TransitionMode tin = TransitionMode.AtemAuto,
        TransitionMode tout = TransitionMode.AtemAuto) =>
        new(enabled, Replay, autoIn, edge, ret, tin, AtemTransitionStyle.Mix, tout, AtemTransitionStyle.Mix, 0, 1500, 5000);

    private static readonly ClipRef Clip1 = new(1, 3, 0.5, "#1 cam3", 0, 100);
    private static readonly ClipRef Clip2 = new(2, 1, 1.0, "#2 cam1", 0, 100);

    private IReadOnlyList<AutomationAction> Send(ReplayStateMachine sm, AutomationEvent e) => sm.Handle(e, _now);

    private ReplayStateMachine Ready(AutomationOptions? o = null, int program = 2)
    {
        var sm = new ReplayStateMachine(o ?? Options());
        Send(sm, new OutputReadinessEvent(true, "ok"));
        Send(sm, new RecordingStateEvent(true));
        Send(sm, new AtemStateEvent(true, program, 3));
        return sm;
    }

    private static bool AnySwitch(IEnumerable<AutomationAction> a) =>
        a.Any(x => x is SetPreviewAction or TransitionAction or CutAction);

    [Fact]
    public void Starts_disconnected_and_becomes_recording()
    {
        var sm = new ReplayStateMachine(Options());
        Assert.Equal(ReplayState.DISCONNECTED, sm.State);
        Send(sm, new AtemStateEvent(true, 1, 2));
        Assert.Equal(ReplayState.LIVE_READY, sm.State);
        Send(sm, new RecordingStateEvent(true));
        Assert.Equal(ReplayState.RECORDING, sm.State);
    }

    [Fact]
    public void Cannot_arm_when_output_not_ready()
    {
        var sm = new ReplayStateMachine(Options());
        Send(sm, new AtemStateEvent(true, 1, 2));
        var a = Send(sm, new ArmRequested(Clip1));
        Assert.Null(sm.ArmedClip);
        Assert.Contains(a, x => x is AlertAction { Level: AlertLevel.Warning });
    }

    [Fact]
    public void Full_automatic_cycle_switches_in_waits_for_program_plays_and_returns()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Assert.Equal(ReplayState.REPLAY_ARMED, sm.State);

        var a = Send(sm, new ReplayRequested("test"));
        Assert.Equal(ReplayState.WAITING_FOR_PROGRAM, sm.State);
        var start = Assert.Single(a.OfType<StartPlayoutAction>());
        Assert.True(start.Gated);
        Assert.Contains(a, x => x is SetPreviewAction { Input: Replay });
        Assert.Contains(a, x => x is TransitionAction);

        // Preview changes are not Program confirmation.
        a = Send(sm, new AtemStateEvent(true, 2, Replay));
        Assert.Equal(ReplayState.WAITING_FOR_PROGRAM, sm.State);
        Assert.DoesNotContain(a, x => x is ReleaseClipAction);

        a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.Equal(ReplayState.PLAYING, sm.State);
        Assert.Single(a.OfType<ReleaseClipAction>());

        a = Send(sm, new ClipPlaybackCompleted());
        Assert.Equal(ReplayState.REPLAY_COMPLETE, sm.State);
        Assert.Contains(a, x => x is SetPreviewAction { Input: 2 });
        Assert.Contains(a, x => x is TransitionAction);

        a = Send(sm, new AtemStateEvent(true, 2, Replay));
        Assert.Contains(a, x => x is ShowHoldingAction);
        Assert.Equal(ReplayState.RECORDING, sm.State);
    }

    [Fact]
    public void Staying_on_replay_input_never_retriggers()
    {
        var sm = Ready(Options(ret: ReturnToLiveMode.Manual));
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Send(sm, new ClipPlaybackCompleted());
        Assert.Equal(ReplayState.REPLAY_COMPLETE, sm.State);

        // Arm another clip while the replay input is still on Program, and keep receiving state updates.
        Send(sm, new ArmRequested(Clip2));
        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(1).Ticks;
            var a = Send(sm, new AtemStateEvent(true, Replay, 2));
            Assert.DoesNotContain(a, x => x is StartPlayoutAction);
        }
        Assert.Equal(Clip1, sm.PlayingClip);
        Assert.Equal(Clip2, sm.ArmedClip);
    }

    [Fact]
    public void Manual_return_does_not_switch()
    {
        var sm = Ready(Options(ret: ReturnToLiveMode.Manual));
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        var a = Send(sm, new ClipPlaybackCompleted());
        Assert.False(AnySwitch(a));
        Assert.Equal(ReplayState.REPLAY_COMPLETE, sm.State);

        a = Send(sm, new LiveRequested("operator"));
        Assert.Contains(a, x => x is SetPreviewAction { Input: 2 });
    }

    [Fact]
    public void Duplicate_replay_requests_are_ignored()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("keyboard"));
        var a = Send(sm, new ReplayRequested("phone"));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);
        Assert.False(AnySwitch(a));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        a = Send(sm, new ReplayRequested("keyboard"));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);
    }

    [Fact]
    public void Replay_without_armed_clip_does_nothing()
    {
        var sm = Ready();
        var a = Send(sm, new ReplayRequested("test"));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);
        Assert.False(AnySwitch(a));
    }

    [Fact]
    public void Disabled_automation_never_switches()
    {
        var sm = Ready(Options(enabled: false));
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new ReplayRequested("test"));
        Assert.False(AnySwitch(a));
        Assert.Equal(ReplayState.PLAYING, sm.State);
        a = Send(sm, new ClipPlaybackCompleted());
        Assert.False(AnySwitch(a));
    }

    [Fact]
    public void Program_edge_with_armed_clip_starts_playback_once()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new AtemStateEvent(true, Replay, 2));
        var sp = Assert.Single(a.OfType<StartPlayoutAction>());
        Assert.False(sp.Gated);
        Assert.Equal(ReplayState.PLAYING, sm.State);
        a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);
    }

    [Fact]
    public void First_report_after_connect_is_not_an_edge()
    {
        var sm = new ReplayStateMachine(Options());
        Send(sm, new OutputReadinessEvent(true, "ok"));
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);
    }

    [Fact]
    public void Lockout_blocks_edge_right_after_a_replay()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Send(sm, new ClipPlaybackCompleted());
        Send(sm, new AtemStateEvent(true, 2, Replay));
        Send(sm, new ArmRequested(Clip2));
        _now += TimeSpan.FromMilliseconds(200).Ticks;
        var a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.DoesNotContain(a, x => x is StartPlayoutAction);

        Send(sm, new AtemStateEvent(true, 2, Replay));
        _now += TimeSpan.FromSeconds(2).Ticks;
        a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.Single(a.OfType<StartPlayoutAction>());
    }

    [Fact]
    public void Disconnect_while_waiting_rearms_and_stops_switching()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        var a = Send(sm, new AtemStateEvent(false, null, null));
        Assert.Equal(ReplayState.REPLAY_ARMED, sm.State);
        Assert.Contains(a, x => x is StopPlayoutAction);
        Assert.Contains(a, x => x is AlertAction { Level: AlertLevel.Critical });
        Assert.False(sm.CanAutoSwitch);

        a = Send(sm, new ReplayRequested("test"));
        Assert.False(AnySwitch(a));
        Assert.Single(a.OfType<StartPlayoutAction>());
        Assert.Equal(ReplayState.PLAYING, sm.State);
        a = Send(sm, new ClipPlaybackCompleted());
        Assert.False(AnySwitch(a));
    }

    [Fact]
    public void Waiting_times_out_and_keeps_clip_armed()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        _now += TimeSpan.FromSeconds(6).Ticks;
        var a = Send(sm, new Tick());
        Assert.Equal(ReplayState.REPLAY_ARMED, sm.State);
        Assert.Equal(Clip1, sm.ArmedClip);
        Assert.Contains(a, x => x is StopPlayoutAction);
    }

    [Fact]
    public void Cancel_auto_while_waiting_rearms()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new CancelAutoRequested("test"));
        Assert.Equal(ReplayState.REPLAY_ARMED, sm.State);
        Assert.Equal(Clip1, sm.ArmedClip);
    }

    [Fact]
    public void Cancel_auto_while_playing_suppresses_return()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Send(sm, new CancelAutoRequested("test"));
        var a = Send(sm, new ClipPlaybackCompleted());
        Assert.False(AnySwitch(a));
    }

    [Fact]
    public void Override_blocks_all_switching_including_live()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new EmergencyOverrideRequested(true, "test"));
        var a = Send(sm, new ReplayRequested("test"));
        Assert.False(AnySwitch(a));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        a = Send(sm, new LiveRequested("test"));
        Assert.False(AnySwitch(a));
        Assert.Contains(a, x => x is StopPlayoutAction);
    }

    [Fact]
    public void Playback_failure_enters_error_until_cleared()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        var a = Send(sm, new PlaybackFailed("frame overwritten"));
        Assert.Equal(ReplayState.ERROR, sm.State);
        Assert.Contains(a, x => x is ShowHoldingAction);
        Assert.False(sm.CanAutoSwitch);

        a = Send(sm, new ArmRequested(Clip2));
        Assert.Null(sm.ArmedClip);
        Send(sm, new ClearErrorRequested("test"));
        Assert.NotEqual(ReplayState.ERROR, sm.State);
        Send(sm, new ArmRequested(Clip2));
        Assert.Equal(ReplayState.REPLAY_ARMED, sm.State);
    }

    [Fact]
    public void Output_loss_during_playback_is_an_error()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Send(sm, new OutputReadinessEvent(false, "display unplugged"));
        Assert.Equal(ReplayState.ERROR, sm.State);
    }

    [Fact]
    public void Operator_cutting_away_mid_replay_stops_without_fighting()
    {
        var sm = Ready();
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        var a = Send(sm, new AtemStateEvent(true, 4, Replay));
        Assert.Contains(a, x => x is StopPlayoutAction);
        Assert.False(AnySwitch(a));
        Assert.Equal(ReplayState.RECORDING, sm.State);
    }

    [Fact]
    public void Endora_stinger_cuts_at_cover_frame_both_ways()
    {
        var sm = Ready(Options(tin: TransitionMode.EndoraStinger, tout: TransitionMode.EndoraStinger));
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new ReplayRequested("test"));
        var sp = Assert.Single(a.OfType<StartPlayoutAction>());
        Assert.True(sp.WithStingerIn);
        Assert.Contains(a, x => x is SetPreviewAction { Input: Replay });
        Assert.DoesNotContain(a, x => x is TransitionAction or CutAction);

        a = Send(sm, new StingerCutPoint(false));
        Assert.Single(a.OfType<CutAction>());
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.Equal(ReplayState.PLAYING, sm.State);

        a = Send(sm, new ClipPlaybackCompleted());
        Assert.Contains(a, x => x is PlayStingerOutAction);
        Assert.Contains(a, x => x is SetPreviewAction { Input: 2 });
        a = Send(sm, new StingerCutPoint(true));
        Assert.Single(a.OfType<CutAction>());
    }

    [Fact]
    public void Stop_during_keyed_stinger_takes_the_key_off_before_the_holding_frame()
    {
        var sm = Ready(Options(tin: TransitionMode.EndoraStinger, tout: TransitionMode.EndoraStinger) with { StingerKey = true });
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new ReplayRequested("test"));
        Assert.Contains(a, x => x is DskOnAirAction { OnAir: true });
        Assert.True(sm.StingerKeyOnAir);

        a = Send(sm, new StopRequested("test"));
        Assert.IsType<DskOnAirAction>(a[0]);
        Assert.False(((DskOnAirAction)a[0]).OnAir);
        Assert.Contains(a, x => x is ShowHoldingAction);
        Assert.False(sm.StingerKeyOnAir);
    }

    [Fact]
    public void Keyed_stinger_out_keeps_playing_after_the_cut_and_ends_the_cycle_when_finished()
    {
        var sm = Ready(Options(tin: TransitionMode.EndoraStinger, tout: TransitionMode.EndoraStinger) with { StingerKey = true });
        Send(sm, new ArmRequested(Clip1));
        Send(sm, new ReplayRequested("test"));
        Send(sm, new StingerCutPoint(false));
        Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.Contains(Send(sm, new StingerFinished(false)), x => x is DskOnAirAction { OnAir: false });

        var a = Send(sm, new ClipPlaybackCompleted());
        Assert.Contains(a, x => x is DskConfigureAction { Input: Replay });
        Assert.Contains(a, x => x is PlayStingerOutAction);

        a = Send(sm, new StingerCutPoint(true));
        Assert.IsType<DskOnAirAction>(a[0]);
        Assert.IsType<CutAction>(a[1]);
        a = Send(sm, new AtemStateEvent(true, 2, Replay));
        Assert.DoesNotContain(a, x => x is StopPlayoutAction or ShowHoldingAction);
        Assert.Equal(ReplayState.REPLAY_COMPLETE, sm.State);

        a = Send(sm, new StingerFinished(true));
        Assert.Equal(new DskOnAirAction(1, false), a[0]);
        Assert.Contains(a, x => x is ShowHoldingAction);
        Assert.Equal(ReplayState.RECORDING, sm.State);
    }

    [Fact]
    public void Replay_with_autoswitch_off_waits_for_operator_cut()
    {
        var sm = Ready(Options(autoIn: false));
        Send(sm, new ArmRequested(Clip1));
        var a = Send(sm, new ReplayRequested("test"));
        Assert.False(AnySwitch(a));
        Assert.Equal(ReplayState.WAITING_FOR_PROGRAM, sm.State);
        a = Send(sm, new AtemStateEvent(true, Replay, 2));
        Assert.Single(a.OfType<ReleaseClipAction>());
    }
}
