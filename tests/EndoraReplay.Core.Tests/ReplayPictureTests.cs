using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Simulation;
using Xunit;

namespace EndoraReplay.Core.Tests;

public class ReplayPictureTests
{
    private static readonly VideoFormat Fast = new(1920, 1080, 500);

    private static MemoryFrameSequence Seq(int sourceId, long first, int n) =>
        new(sourceId, Enumerable.Range(0, n).Select(i => new EncodedFrame
        {
            SourceId = sourceId, FrameNumber = first + i, TimestampTicks = i * Fast.FrameDurationTicks,
            Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());

    /// <summary>Encodes main, second and zoom into the frame number: main + second×1000 + 1 000 000 when zoomed.</summary>
    private sealed class MarkingEffects : IFrameEffects
    {
        public EncodedFrame Apply(EncodedFrame main, EncodedFrame? second, ViewTransform view) => new()
        {
            SourceId = main.SourceId, TimestampTicks = main.TimestampTicks, Width = main.Width, Height = main.Height, Codec = main.Codec, Data = main.Data,
            FrameNumber = main.FrameNumber + (second?.FrameNumber ?? 0) * 1000 + (view.IsIdentity ? 0 : 1_000_000),
        };
    }

    [Fact]
    public void Zoom_and_pan_stay_inside_the_picture()
    {
        Assert.Equal((0, 0, 1920, 1080), ViewTransform.Identity.CropRect(1920, 1080));
        var v = new ViewTransform(2);
        Assert.Equal((480, 270, 960, 540), v.CropRect(1920, 1080));
        // Panning far to the left stops at the picture's edge.
        Assert.Equal((0, 270, 960, 540), v.PanBy(-5, 0).CropRect(1920, 1080));
        Assert.Equal(ViewTransform.MaxZoom, v.ZoomBy(100).Zoom);
        Assert.True(v.ZoomBy(0.01).IsIdentity);
    }

    [Fact]
    public async Task Split_screen_shows_the_second_angle_at_the_same_moment_and_zoom_applies()
    {
        var o = new RecordingOutput();
        var p = new ReplayPlaybackService(o, Fast, new DiagnosticsService(null, () => 0)) { Effects = new MarkingEffects() };
        var done = false;
        p.PlayoutCompleted += () => done = true;
        p.SetSpeed(1.0);
        p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(1, 0, 300))]);
        p.SetSplit(Seq(2, 100, 300));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (o.Presented.Count < 100 && DateTime.UtcNow < until) await Task.Delay(5);
        p.SetView(new ViewTransform(2));
        while (!done && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(done);

        var frames = o.Presented.ToArray();
        var split = frames.Where(f => f % 1_000_000 >= 1000).ToList();
        Assert.NotEmpty(split);
        // Each split frame pairs main frame i with second-angle frame 100 + i.
        Assert.All(split, f => Assert.Equal(f % 1000 + 100, f % 1_000_000 / 1000));
        Assert.Contains(frames, f => f >= 1_000_000);
        Assert.Equal(299 + 399 * 1000 + 1_000_000, frames[^1]);

        // A new replay starts plain.
        p.Start([new PlayoutSegment(PlayoutSegmentKind.Clip, Seq(1, 0, 5))]);
        Assert.Null(p.Split);
        Assert.True(p.View.IsIdentity);
    }
}
