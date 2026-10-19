using System.Collections.Concurrent;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Storage;

namespace EndoraReplay.Core.Services;

/// <summary>Maintains one pre-allocated disk ring buffer per source.</summary>
public sealed class RollingBufferService : IDisposable
{
    private readonly ConcurrentDictionary<int, DiskRingBuffer> _buffers = new();
    private readonly DiagnosticsService _diag;
    private readonly string _folder;

    public RollingBufferService(string folder, DiagnosticsService diagnostics)
    {
        _folder = folder;
        _diag = diagnostics;
        Directory.CreateDirectory(folder);
    }

    /// <summary>Bytes needed for <paramref name="seconds"/> of <paramref name="format"/> at the estimated frame size, plus 15% headroom.</summary>
    public static long CapacityFor(int seconds, VideoFormat format, int estimatedFrameBytes) =>
        Math.Max(16L << 20, (long)(seconds * format.FramesPerSecond * estimatedFrameBytes * 1.15));

    public DiskRingBuffer Open(int sourceId, long capacityBytes)
    {
        Close(sourceId);
        var path = Path.Combine(_folder, $"buffer_src{sourceId}.erbuf");
        var buffer = new DiskRingBuffer(sourceId, path, capacityBytes);
        _buffers[sourceId] = buffer;
        _diag.Info(LogCategory.Buffer, $"Source {sourceId}: buffer {capacityBytes / (1 << 20)} MB at {path}");
        return buffer;
    }

    public void Close(int sourceId)
    {
        if (_buffers.TryRemove(sourceId, out var old)) old.Dispose();
    }

    public DiskRingBuffer? Get(int sourceId) => _buffers.TryGetValue(sourceId, out var b) ? b : null;

    public IReadOnlyCollection<int> SourceIds => _buffers.Keys.ToList();

    public void Write(EncodedFrame frame)
    {
        if (!_buffers.TryGetValue(frame.SourceId, out var b))
            throw new InvalidOperationException($"No buffer is open for source {frame.SourceId}.");
        b.Write(frame);
    }

    /// <summary>Seconds of material currently held for a source.</summary>
    public double SecondsHeld(int sourceId)
    {
        var b = Get(sourceId);
        if (b?.Oldest is not { } o || b.Newest is not { } n) return 0;
        return TimeSpan.FromTicks(n.TimestampTicks - o.TimestampTicks).TotalSeconds;
    }

    /// <summary>Frames of a source between two session timestamps, for playback or clip export.</summary>
    public IFrameSequence GetSequence(int sourceId, long fromTicks, long toTicks)
    {
        var b = Get(sourceId) ?? throw new FrameUnavailableException($"Source {sourceId} has no buffer.");
        var entries = b.GetRange(fromTicks, toTicks);
        if (entries.Count == 0)
            throw new FrameUnavailableException($"Source {sourceId} holds no frames for the requested range (it may have rolled out of the buffer).");
        return new BufferFrameSequence(sourceId, b, entries);
    }

    public void Dispose()
    {
        foreach (var id in _buffers.Keys) Close(id);
    }
}

/// <summary>Playback view over a fixed set of entries in a ring buffer.</summary>
public sealed class BufferFrameSequence(int sourceId, DiskRingBuffer buffer, IReadOnlyList<FrameIndexEntry> entries) : IFrameSequence
{
    public int SourceId { get; } = sourceId;
    public int Count => entries.Count;
    public long StartTicks => entries[0].TimestampTicks;
    public long EndTicks => entries[^1].TimestampTicks;
    public long TicksAt(int index) => entries[index].TimestampTicks;

    public EncodedFrame Read(int index)
    {
        var e = entries[index];
        var data = buffer.TryRead(e) ?? throw new FrameUnavailableException(
            $"Source {SourceId} frame {e.FrameNumber} was overwritten by the rolling buffer. Save the clip to keep it.");
        return new EncodedFrame
        {
            SourceId = SourceId, FrameNumber = e.FrameNumber, TimestampTicks = e.TimestampTicks,
            Width = e.Width, Height = e.Height, Codec = e.Codec, Data = data,
        };
    }
}
