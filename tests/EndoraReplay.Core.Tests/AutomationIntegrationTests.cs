using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Simulation;

namespace EndoraReplay.Core.Tests;

/// <summary>Automation service + playback + SIMULATED switcher, end to end.</summary>
public class AutomationIntegrationTests
{
    private sealed class NoStingers : IStingerProvider
    {
        public (IFrameSequence Frames, int CutFrame)? Get(bool isReturn) => null;
    }

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    [Fact]
    public async Task Replay_cycle_against_simulated_atem()
    {
        var clock = new MonotonicClock();
        var fmt = new VideoFormat(1920, 1080, 100);
        await using var diag = new DiagnosticsService(null, () => clock.NowTicks);
        var output = new RecordingOutput();
        await using var playback = new ReplayPlaybackService(output, fmt, diag);
        var sim = new SimulatedAtemSwitcher { AutoDuration = TimeSpan.FromMilliseconds(50) };
        await using var atem = new AtemSwitcherService(_ => sim, diag);
        var frames = new MemoryFrameSequence(3, Enumerable.Range(0, 30).Select(i => new EncodedFrame
        {
            SourceId = 3, FrameNumber = i, TimestampTicks = i * fmt.FrameDurationTicks, Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());

        var settings = new AppSettings();
        settings.Automation.Enabled = true;
        await using var auto = new ReplayAutomationService(AutomationOptions.From(settings), playback, atem, new NoStingers(), _ => frames, diag, clock);
        var states = new List<ReplayState>();
        auto.StateChanged += s => { lock (states) states.Add(s); };

        await atem.StartAsync(settings.Atem);
        Assert.True(await WaitFor(() => atem.IsConnected));
        auto.Post(new RecordingStateEvent(true));
        auto.Post(new ArmRequested(new ClipRef(1, 3, 1.0, "#1", 0, 0)));
        Assert.True(await WaitFor(() => auto.State == ReplayState.REPLAY_ARMED));

        auto.Post(new ReplayRequested("test"));
        Assert.True(await WaitFor(() => sim.Snapshot.ProgramInput == 8));
        Assert.True(await WaitFor(() => output.Presented.Count >= 30));
        // Automatic return to the previous live input (1).
        Assert.True(await WaitFor(() => sim.Snapshot.ProgramInput == 1));
        Assert.True(await WaitFor(() => auto.State == ReplayState.RECORDING));

        lock (states)
        {
            Assert.Equal(
                [ReplayState.REPLAY_ARMED, ReplayState.WAITING_FOR_PROGRAM, ReplayState.PLAYING, ReplayState.REPLAY_COMPLETE, ReplayState.RECORDING],
                states.SkipWhile(s => s != ReplayState.REPLAY_ARMED).ToArray());
        }
        await atem.StopAsync();
    }
}
