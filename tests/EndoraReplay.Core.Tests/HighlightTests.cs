using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Simulation;
using Xunit;

namespace EndoraReplay.Core.Tests;

public class HighlightTests
{
    private static readonly VideoFormat Fmt = VideoFormat.Hd1080p50;

    private static MemoryFrameSequence Seq(int sourceId, long firstNumber, int count, long startTicks = 0) =>
        new(sourceId, Enumerable.Range(0, count).Select(i => new EncodedFrame
        {
            SourceId = sourceId, FrameNumber = firstNumber + i, TimestampTicks = startTicks + i * Fmt.FrameDurationTicks,
            Width = 1920, Height = 1080, Codec = FrameCodec.Jpeg, Data = [1],
        }).ToList());

    /// <summary>Records which clip frame each stinger frame was blended over.</summary>
    private sealed class UnderCompositor : IFrameCompositor
    {
        public EncodedFrame Composite(EncodedFrame top, EncodedFrame under, double fadeToUnder) => new()
        {
            SourceId = top.SourceId, FrameNumber = top.FrameNumber * 1000 + under.FrameNumber, TimestampTicks = top.TimestampTicks,
            Width = top.Width, Height = top.Height, Codec = top.Codec, Data = top.Data,
        };

        public double Coverage(EncodedFrame top) => 1;
        public EncodedFrame Black(VideoFormat format) => throw new NotSupportedException();
    }

    [Fact]
    public void Clips_play_back_to_back_with_the_stinger_between_and_the_change_under_its_cover_point()
    {
        // Clips recorded at very different times on the session clock still follow each other without a gap.
        var a = Seq(1, 100, 5, startTicks: 1_000_000_000);
        var b = Seq(2, 200, 4, startTicks: 9_000_000_000);
        var stinger = Seq(-1, 7, 4);
        var h = new HighlightSequence([a, b], (stinger, 1), new UnderCompositor(), Fmt, 0.5);

        Assert.Equal(5 + 4 + 4, h.Count);
        var numbers = Enumerable.Range(0, h.Count).Select(i => h.Read(i).FrameNumber).ToList();
        // Up to and including the cover frame the stinger is over A's last frame (104), then over B's first (200).
        Assert.Equal([100, 101, 102, 103, 104, 7104, 8104, 9200, 10200, 200, 201, 202, 203], numbers);
        Assert.True(h.IsStinger(5) && !h.IsStinger(9));

        // Timestamps only go forward, and the stinger frames are spaced for real time at 50% speed.
        var ticks = Enumerable.Range(0, h.Count).Select(h.TicksAt).ToList();
        Assert.Equal(ticks.Order(), ticks);
        Assert.Equal(Fmt.FrameDurationTicks / 2, ticks[6] - ticks[5]);
        Assert.Equal(h.EndTicks, ticks[^1]);
    }

    [Fact]
    public void Without_a_stinger_the_clips_follow_with_a_cut()
    {
        var h = new HighlightSequence([Seq(1, 0, 3), Seq(2, 50, 2)], null, null, Fmt, 1.0);
        Assert.Equal([0, 1, 2, 50, 51], Enumerable.Range(0, h.Count).Select(i => h.Read(i).FrameNumber));
    }

    [Fact]
    public void A_package_reference_covers_every_clip_in_it()
    {
        var r = new ClipRef(3, 1, 0.5, "Highlights", 0, 0, [3, 5, 9]);
        Assert.True(r.IsPackage && r.Uses(5) && !r.Uses(4));
    }
}
