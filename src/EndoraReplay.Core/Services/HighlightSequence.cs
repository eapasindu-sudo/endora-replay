using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

/// <summary>
/// A highlights package: several clips played back to back as one replay, with the stinger between each pair.
/// Up to the stinger's cover point it is blended over the last frame of the clip before, after it over the first
/// frame of the clip after, so the change of clip happens under the stinger. Without a stinger or compositor the
/// clips follow each other with a cut. Stinger frames are spaced so they run at normal speed even when the package
/// plays in slow motion. Blends are prepared in the background as soon as the package is built.
/// </summary>
public sealed class HighlightSequence : IFrameSequence
{
    private readonly record struct Entry(int Part, int Index, long Ticks);

    private readonly IReadOnlyList<IFrameSequence> _clips;
    private readonly IFrameSequence? _stinger;
    private readonly int _cover;
    private readonly IFrameCompositor? _compositor;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<(int Gap, int Index), Lazy<Task<EncodedFrame>>> _blends = [];

    /// <param name="speed">Speed the package will play at; stinger frames are spaced by it so they run in real time.</param>
    public HighlightSequence(IReadOnlyList<IFrameSequence> clips, (IFrameSequence Frames, int CutFrame)? stinger,
        IFrameCompositor? compositor, VideoFormat format, double speed)
    {
        if (clips.Count == 0 || clips.Any(c => c.Count == 0)) throw new FrameUnavailableException("Highlights need clips with frames.");
        _clips = clips;
        _compositor = compositor;
        if (stinger is { } s && s.Frames.Count > 0 && compositor is not null)
        {
            _stinger = s.Frames;
            _cover = Math.Clamp(s.CutFrame, 0, s.Frames.Count - 1);
        }
        var period = format.FrameDurationTicks;
        var stingerStep = Math.Max(1, (long)Math.Round(period * Math.Clamp(speed, 0.01, 2)));

        long cursor = 0;
        for (var c = 0; c < clips.Count; c++)
        {
            var clip = clips[c];
            for (var i = 0; i < clip.Count; i++) _entries.Add(new Entry(c, i, cursor + clip.TicksAt(i) - clip.StartTicks));
            cursor += clip.EndTicks - clip.StartTicks + period;
            if (_stinger is null || c == clips.Count - 1) continue;
            for (var j = 0; j < _stinger.Count; j++)
            {
                _entries.Add(new Entry(-1 - c, j, cursor));
                cursor += stingerStep;
                var (gap, jj) = (c, j);
                _blends[(gap, jj)] = new Lazy<Task<EncodedFrame>>(() => Task.Run(() => Blend(gap, jj)));
            }
        }
        SourceId = clips[0].SourceId;
        if (_stinger is not null) _ = Task.Run(() => { foreach (var b in _blends.Values) _ = b.Value; });
    }

    public int SourceId { get; }
    public int Count => _entries.Count;
    public long StartTicks => 0;
    public long EndTicks => _entries[^1].Ticks;
    public long TicksAt(int index) => _entries[index].Ticks;

    /// <summary>Number of clips in the package.</summary>
    public int ClipCount => _clips.Count;

    /// <summary>True when <paramref name="index"/> is a stinger frame between two clips.</summary>
    public bool IsStinger(int index) => _entries[index].Part < 0;

    public EncodedFrame Read(int index)
    {
        var e = _entries[index];
        var f = e.Part >= 0 ? _clips[e.Part].Read(e.Index) : _blends[(-1 - e.Part, e.Index)].Value.GetAwaiter().GetResult();
        return new EncodedFrame
        {
            SourceId = SourceId, FrameNumber = f.FrameNumber, TimestampTicks = e.Ticks,
            Width = f.Width, Height = f.Height, Codec = f.Codec, Data = f.Data,
        };
    }

    private EncodedFrame Blend(int gap, int j)
    {
        var under = j <= _cover ? _clips[gap].Read(_clips[gap].Count - 1) : _clips[gap + 1].Read(0);
        return _compositor!.Composite(_stinger!.Read(j), under, 0);
    }
}
