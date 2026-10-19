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

    private sealed class TestStingers(IFrameSequence inFrames, int inCut, IFrameSequence outFrames, int outCut) : IStingerProvider
    {
        public (IFrameSequence Frames, int CutFrame)? Get(bool isReturn) => isReturn ? (outFrames, outCut) : (inFrames, inCut);
    }

    private static MemoryFrameSequence Seq(int sourceId, long firstNumber, int count, VideoFormat fmt) =>
        new(sourceId, Enumerable.Range(0, count).Select(i => new EncodedFrame
        {
            SourceId = sourceId, FrameNumber = firstNumber + i, TimestampTicks = i * fmt.FrameDurationTicks,
            Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());

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

    /// <summary>Marks blended frames by adding 100000 to the stinger frame number.</summary>
    private sealed class MarkingCompositor : IFrameCompositor
    {
        public EncodedFrame Composite(EncodedFrame top, EncodedFrame under, double fadeToUnder) => new()
        {
            SourceId = top.SourceId, FrameNumber = top.FrameNumber + 100_000, TimestampTicks = top.TimestampTicks,
            Width = top.Width, Height = top.Height, Codec = top.Codec, Data = top.Data,
        };

        public double Coverage(EncodedFrame top) => 1;

        public EncodedFrame Black(VideoFormat format) => new()
        {
            SourceId = -1, FrameNumber = -1, TimestampTicks = 0, Width = format.Width, Height = format.Height, Codec = FrameCodec.Jpeg, Data = [0],
        };
    }

    [Fact]
    public async Task Keyed_stinger_plays_whole_in_and_out_over_live_and_cuts_at_the_cover_point()
    {
        var clock = new MonotonicClock();
        var fmt = new VideoFormat(1920, 1080, 100);
        await using var diag = new DiagnosticsService(null, () => clock.NowTicks);
        var output = new RecordingOutput();
        await using var playback = new ReplayPlaybackService(output, fmt, diag);
        var sim = new SimulatedAtemSwitcher { AutoDuration = TimeSpan.FromMilliseconds(50) };
        await using var atem = new AtemSwitcherService(_ => sim, diag);
        var clip = Seq(3, 0, 30, fmt);
        var stingers = new TestStingers(Seq(-1, 1000, 20, fmt), 6, Seq(-1, 2000, 20, fmt), 9);

        // What was on the replay output and whether the key was on air at each ATEM change.
        var changes = new List<(int? Program, bool Key, long? Frame)>();
        sim.StateChanged += s =>
        {
            lock (changes)
            {
                var key = s.IsDownstreamKeyOnAir(1);
                if (changes.Count > 0 && changes[^1].Program == s.ProgramInput && changes[^1].Key == key) return;
                changes.Add((s.ProgramInput, key, output.LastFrame?.FrameNumber));
            }
        };

        var settings = new AppSettings();
        settings.Automation.Enabled = true;
        settings.Automation.TransitionIn = TransitionMode.EndoraStinger;
        settings.Automation.TransitionOut = TransitionMode.EndoraStinger;
        settings.Automation.StingerKey = true;
        settings.Automation.StingerSwitchDelayFrames = 2;
        await using var auto = new ReplayAutomationService(AutomationOptions.From(settings), playback, atem, stingers, _ => clip, diag, clock,
            new MarkingCompositor(), fmt);

        await atem.StartAsync(settings.Atem);
        Assert.True(await WaitFor(() => atem.IsConnected));
        auto.Post(new RecordingStateEvent(true));
        auto.Post(new ArmRequested(new ClipRef(1, 3, 1.0, "#1", 0, 0)));
        Assert.True(await WaitFor(() => auto.State == ReplayState.REPLAY_ARMED));

        auto.Post(new ReplayRequested("test"));
        Assert.True(await WaitFor(() => sim.Snapshot.ProgramInput == 8));
        Assert.True(await WaitFor(() => sim.Snapshot.ProgramInput == 1 && auto.State == ReplayState.RECORDING));
        Assert.True(await WaitFor(() => !sim.Snapshot.IsDownstreamKeyOnAir(1)));

        var p = output.Presented.ToArray();
        // Black lead-in, the plain IN stinger up to cover + 2, then the IN stinger blended over the replay.
        Assert.Equal(Enumerable.Repeat(-1L, ReplayAutomationService.KeyLeadFrames), p.Take(ReplayAutomationService.KeyLeadFrames));
        Assert.Equal(Enumerable.Range(1000, 8).Select(i => (long)i), p.Where(f => f is >= 1000 and < 2000));
        Assert.Equal(Enumerable.Range(101_008, 12).Select(i => (long)i), p.Where(f => f is >= 101_000 and < 102_000));
        // The whole clip, then the OUT stinger blended over the replay up to cover + 2, then the plain OUT stinger to its end.
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (long)i), p.Where(f => f is >= 0 and < 1000));
        Assert.Equal(Enumerable.Range(102_000, 11).Select(i => (long)i), p.Where(f => f >= 102_000));
        Assert.Equal(Enumerable.Range(2011, 9).Select(i => (long)i), p.Where(f => f is >= 2000 and < 3000));

        lock (changes)
        {
            // Key goes on air over live (Program 1) while only black is on the replay output.
            var keyOn = changes.First(c => c.Key);
            Assert.Equal(1, keyOn.Program);
            Assert.Equal(-1, keyOn.Frame);
            // The cut onto the replay input happens with the key still on, at or after the IN cover frame.
            var toReplay = changes.First(c => c.Program == 8);
            Assert.True(toReplay.Key);
            Assert.InRange(toReplay.Frame ?? -1, 1006, 1007);
            // Going back: key on first, then the cut to live at the OUT cover frame; the key comes off after the stinger ends.
            var toLive = changes.Last(c => c.Program == 1 && c.Key);
            Assert.InRange(toLive.Frame ?? -1, 102_009, 102_010);
            Assert.False(changes[^1].Key);
            Assert.Equal(1, changes[^1].Program);
        }
        await atem.StopAsync();
    }
}
