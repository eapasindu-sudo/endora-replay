using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

public enum StingerDirection
{
    /// <summary>Into the replay: plain stinger until just after the cover point, then the stinger blended over the first replay frame.</summary>
    In,

    /// <summary>Back to live: the stinger blended over the last replay frame until just after the cover point, then the plain stinger.</summary>
    Out,
}

/// <summary>
/// The frames Endora sends for a stinger. Around the cover point the ATEM cuts underneath; before or after it,
/// Endora's output either carries the plain stinger on black (for the ATEM's luma key over the live picture) or the
/// stinger already blended over the replay picture (for the part where the replay input itself is on Program).
/// Blended frames are prepared in the background as soon as the sequence is created.
/// </summary>
public sealed class StingerSequence : IFrameSequence
{
    private readonly IFrameSequence _stinger;
    private readonly StingerDirection _direction;
    private readonly int _lead;
    private readonly int _switchAt;
    private readonly int _fadeFrames;
    private readonly EncodedFrame? _under;
    private readonly IFrameCompositor? _compositor;
    private readonly long _period;
    private readonly EncodedFrame? _black;
    private readonly Lazy<Task<EncodedFrame>>[] _blended;

    /// <param name="coverIndex">Stinger frame where the picture is fully covered.</param>
    /// <param name="switchDelayFrames">Frames after the cover point before the output changes between plain and blended.</param>
    /// <param name="leadFrames">Black frames before the stinger, giving the ATEM key time to come on air.</param>
    public StingerSequence(IFrameSequence stinger, StingerDirection direction, int coverIndex, int switchDelayFrames, int leadFrames,
        int fadeFrames, EncodedFrame? under, IFrameCompositor? compositor, VideoFormat format)
    {
        if (stinger.Count == 0) throw new FrameUnavailableException("Stinger has no frames.");
        _stinger = stinger;
        _direction = direction;
        _compositor = compositor;
        _under = under;
        _lead = compositor is null ? 0 : Math.Max(0, leadFrames);
        _fadeFrames = Math.Clamp(fadeFrames, 0, stinger.Count);
        var cover = Math.Clamp(coverIndex, 0, stinger.Count - 1);
        _switchAt = Math.Min(cover + Math.Max(0, switchDelayFrames), stinger.Count - 1);
        CutPointIndex = _lead + cover;
        _period = format.FrameDurationTicks;
        _black = _lead > 0 ? compositor!.Black(format) : null;
        SourceId = stinger.SourceId;

        _blended = new Lazy<Task<EncodedFrame>>[stinger.Count];
        for (var j = 0; j < stinger.Count; j++)
        {
            var jj = j;
            _blended[j] = new Lazy<Task<EncodedFrame>>(() => Task.Run(() => Blend(jj)));
        }
        if (CanBlend)
        {
            // Start preparing in the order the frames will be needed.
            var order = direction == StingerDirection.Out
                ? Enumerable.Range(0, _switchAt)
                : Enumerable.Range(_switchAt, stinger.Count - _switchAt);
            _ = Task.Run(() =>
            {
                foreach (var j in order) _ = _blended[j].Value;
            });
        }
    }

    private bool CanBlend => _under is not null && _compositor is not null;

    /// <summary>Index (in this sequence) at which the ATEM should cut.</summary>
    public int CutPointIndex { get; }

    public int SourceId { get; }
    public int Count => _lead + _stinger.Count;
    public long StartTicks => 0;
    public long EndTicks => (Count - 1) * _period;
    public long TicksAt(int index) => index * _period;

    public bool IsBlended(int index)
    {
        var j = index - _lead;
        if (j < 0 || !CanBlend) return false;
        return _direction == StingerDirection.In ? j >= _switchAt : j < _switchAt;
    }

    public EncodedFrame Read(int index)
    {
        if (index < _lead) return Retime(_black!, index);
        var j = index - _lead;
        var frame = IsBlended(index) ? _blended[j].Value.GetAwaiter().GetResult() : _stinger.Read(j);
        return Retime(frame, index);
    }

    private EncodedFrame Blend(int j)
    {
        // Crossfade towards the replay over the last frames of IN, and from the replay over the first frames of OUT.
        double fade = 0;
        if (_fadeFrames > 0)
        {
            fade = _direction == StingerDirection.In
                ? (j - (_stinger.Count - _fadeFrames) + 1) / (double)_fadeFrames
                : (_fadeFrames - j) / (double)_fadeFrames;
            fade = Math.Clamp(fade, 0, 1);
        }
        return _compositor!.Composite(_stinger.Read(j), _under!, fade);
    }

    private EncodedFrame Retime(EncodedFrame f, int index) => new()
    {
        SourceId = SourceId, FrameNumber = f.FrameNumber, TimestampTicks = index * _period,
        Width = f.Width, Height = f.Height, Codec = f.Codec, Data = f.Data,
    };
}
