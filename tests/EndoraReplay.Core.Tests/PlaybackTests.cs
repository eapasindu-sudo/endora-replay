using System.Collections.Concurrent;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Simulation;

namespace EndoraReplay.Core.Tests;

/// <summary>Test double that records presented frames. Not a production output.</summary>
internal sealed class RecordingOutput : IReplayOutput
{
    public ConcurrentQueue<long> Presented { get; } = new();
    public int HoldingCount;
    public string Name => "test";
    public bool IsSimulated => true;
    public OutputReadiness Readiness { get; set; } = OutputReadiness.Ready;
    public string ReadinessMessage => "test";
    public EncodedFrame? LastFrame { get; private set; }
    public event Action<OutputReadiness, string>? ReadinessChanged;
    public Task OpenAsync(VideoFormat format, CancellationToken cancellationToken) => Task.CompletedTask;
    public ModeValidationResult ValidateMode(VideoFormat format) => ModeValidationResult.Ok();
    public void Present(EncodedFrame frame) { LastFrame = frame; Presented.Enqueue(frame.FrameNumber); }
    public void ShowHolding() { LastFrame = null; Interlocked.Increment(ref HoldingCount); }
    public void Raise(OutputReadiness r) { Readiness = r; ReadinessChanged?.Invoke(r, "test"); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BrokenSequence(IFrameSequence inner, int failAt) : IFrameSequence
{
    public int SourceId => inner.SourceId;
    public int Count => inner.Count;
    public long StartTicks => inner.StartTicks;
    public long EndTicks => inner.EndTicks;
    public long TicksAt(int index) => inner.TicksAt(index);
    public EncodedFrame Read(int index) => index >= failAt ? throw new FrameUnavailableException("gone") : inner.Read(index);
}

public class PlaybackTests
{
    private static readonly VideoFormat Fast = new(1920, 1080, 500); // 2 ms frames keep tests quick

    private static MemoryFrameSequence Seq(int n) =>
        new(1, Enumerable.Range(0, n).Select(i => new EncodedFrame
        {
            SourceId = 1, FrameNumber = i, TimestampTicks = i * Fast.FrameDurationTicks,
            Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    private static ReplayPlaybackService Service(RecordingOutput o) =>
        new(o, Fast, new DiagnosticsService(null, () => 0));

    [Fact]
    public async Task Full_speed_presents_every_frame_once()
    {
        var o = new RecordingOutput();
        var p = Service(o);
        var done = false;
        p.PlayoutCompleted += () => done = true;
        p.SetSpeed(1.0);
        p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(40))]);
        Assert.True(await WaitFor(() => done));
        var frames = o.Presented.ToArray();
        Assert.Equal(Enumerable.Range(0, 40).Select(i => (long)i), frames);
    }

    [Fact]
    public async Task Switching_angle_continues_at_the_same_moment_on_the_other_camera()
    {
        var o = new RecordingOutput();
        var p = Service(o);
        var done = false;
        p.PlayoutCompleted += () => done = true;
        p.SetSpeed(1.0);
        var camB = new MemoryFrameSequence(2, Enumerable.Range(0, 400).Select(i => new EncodedFrame
        {
            SourceId = 2, FrameNumber = 1000 + i, TimestampTicks = i * Fast.FrameDurationTicks,
            Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());
        p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(400))]);
        Assert.True(await WaitFor(() => o.Presented.Count >= 50));
        Assert.True(p.SwitchAngle(camB));
        Assert.True(await WaitFor(() => done));

        var frames = o.Presented.ToArray();
        var lastA = frames.TakeWhile(f => f < 1000).Last();
        var firstB = frames.First(f => f >= 1000) - 1000;
        // Picks up within a frame of where camera A was, and plays on to the end on camera B.
        Assert.InRange(firstB, lastA, lastA + 2);
        Assert.Equal(1399, frames[^1]);
        Assert.False(p.SwitchAngle(camB));
    }

    [Fact]
    public async Task Half_and_quarter_speed_repeat_frames()
    {
        foreach (var (speed, repeats) in new[] { (0.5, 2), (0.25, 4) })
        {
            var o = new RecordingOutput();
            var p = Service(o);
            var done = false;
            p.PlayoutCompleted += () => done = true;
            p.SetSpeed(speed);
            p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(10))]);
            Assert.True(await WaitFor(() => done));
            var counts = o.Presented.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(10, counts.Count);
            Assert.All(counts.Where(kv => kv.Key < 9), kv => Assert.Equal(repeats, kv.Value));
        }
    }

    [Fact]
    public async Task Gated_segment_waits_for_release_and_fires_cut_point()
    {
        var o = new RecordingOutput();
        var p = Service(o);
        var cut = false;
        var clipStarted = false;
        var done = false;
        p.CutPointReached += k => cut |= k == PlayoutSegmentKind.StingerIn;
        p.SegmentStarted += k => clipStarted |= k == PlayoutSegmentKind.Clip;
        p.PlayoutCompleted += () => done = true;
        p.SetSpeed(1.0);
        p.Start([
            new PlayoutSegment(PlayoutSegmentKind.StingerIn, Seq(10), 1.0, CutPointIndex: 5),
            new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(10), WaitForRelease: true),
        ]);
        Assert.True(await WaitFor(() => cut));
        await Task.Delay(100);
        Assert.False(clipStarted);
        Assert.Equal(PlaybackStatus.WaitingForRelease, p.Status);
        p.Release();
        Assert.True(await WaitFor(() => done));
        Assert.True(clipStarted);
    }

    [Fact]
    public async Task Missing_frame_fails_instead_of_substituting()
    {
        var o = new RecordingOutput();
        var p = Service(o);
        string? failure = null;
        p.Failed += r => failure = r;
        p.SetSpeed(1.0);
        p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, new BrokenSequence(Seq(20), 8))]);
        Assert.True(await WaitFor(() => failure is not null));
        Assert.DoesNotContain(8L, o.Presented);
        Assert.True(o.HoldingCount > 0);
    }

    [Fact]
    public void Refuses_to_start_when_output_not_ready()
    {
        var o = new RecordingOutput { Readiness = OutputReadiness.DeviceMissing };
        var p = Service(o);
        Assert.Throws<InvalidOperationException>(() => p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(5))]));
    }
}
