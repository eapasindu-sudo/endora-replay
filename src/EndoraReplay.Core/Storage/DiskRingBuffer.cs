using EndoraReplay.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace EndoraReplay.Core.Storage;

/// <summary>Location of one frame inside a <see cref="DiskRingBuffer"/>.</summary>
public readonly record struct FrameIndexEntry(
    long Sequence,
    long FrameNumber,
    long TimestampTicks,
    long Offset,
    int Length,
    int Width,
    int Height,
    FrameCodec Codec);

/// <summary>
/// Fixed-size, pre-allocated circular file holding the most recent frames of one source.
/// One writer (the source's buffer worker) and any number of readers. Frames are evicted strictly
/// oldest-first; a reader holding an evicted entry gets null and must report the frame as missing.
/// </summary>
public sealed class DiskRingBuffer : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<FrameIndexEntry> _entries = new(8192);
    private int _head; // index of oldest live entry in _entries
    private long _writePos;
    private long _nextSequence;
    private long _bytesLive;
    private bool _disposed;

    public DiskRingBuffer(int sourceId, string path, long capacityBytes)
    {
        if (capacityBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(capacityBytes), "Buffer must be at least 1 MB.");
        SourceId = sourceId;
        FilePath = path;
        CapacityBytes = capacityBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.RandomAccess);
        RandomAccess.SetLength(_handle, capacityBytes);
    }

    public int SourceId { get; }
    public string FilePath { get; }
    public long CapacityBytes { get; }

    public int Count
    {
        get { _lock.EnterReadLock(); try { return _entries.Count - _head; } finally { _lock.ExitReadLock(); } }
    }

    public long BytesLive
    {
        get { _lock.EnterReadLock(); try { return _bytesLive; } finally { _lock.ExitReadLock(); } }
    }

    public FrameIndexEntry? Oldest
    {
        get { _lock.EnterReadLock(); try { return _entries.Count > _head ? _entries[_head] : null; } finally { _lock.ExitReadLock(); } }
    }

    public FrameIndexEntry? Newest
    {
        get { _lock.EnterReadLock(); try { return _entries.Count > _head ? _entries[^1] : null; } finally { _lock.ExitReadLock(); } }
    }

    /// <summary>Appends a frame, evicting the oldest frames whose bytes it overwrites.</summary>
    public FrameIndexEntry Write(EncodedFrame frame)
    {
        var len = frame.Data.Length;
        if (len == 0) throw new ArgumentException("Empty frame.", nameof(frame));
        if (len > CapacityBytes / 4) throw new ArgumentException($"Frame of {len} bytes is too large for a {CapacityBytes} byte buffer.", nameof(frame));

        _lock.EnterWriteLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count > _head && frame.TimestampTicks < _entries[^1].TimestampTicks)
                throw new InvalidOperationException("Frames must be written in timestamp order.");

            if (_writePos + len > CapacityBytes)
            {
                // Wrap: everything left in the tail gap is older than anything at the start.
                var gapStart = _writePos;
                while (_entries.Count > _head && _entries[_head].Offset >= gapStart) EvictHead();
                _writePos = 0;
            }

            var start = _writePos;
            var end = start + len;
            while (_entries.Count > _head)
            {
                var h = _entries[_head];
                var overlaps = h.Offset < end && h.Offset + h.Length > start;
                if (!overlaps) break;
                EvictHead();
            }

            RandomAccess.Write(_handle, frame.Data, start);
            var entry = new FrameIndexEntry(_nextSequence++, frame.FrameNumber, frame.TimestampTicks, start, len, frame.Width, frame.Height, frame.Codec);
            _entries.Add(entry);
            _bytesLive += len;
            _writePos = end;

            if (_head > 4096 && _head > _entries.Count / 2)
            {
                _entries.RemoveRange(0, _head);
                _head = 0;
            }

            return entry;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void EvictHead()
    {
        _bytesLive -= _entries[_head].Length;
        _head++;
    }

    /// <summary>Reads a frame's bytes, or null if it has been overwritten since the entry was obtained.</summary>
    public byte[]? TryRead(in FrameIndexEntry entry)
    {
        _lock.EnterReadLock();
        try
        {
            if (_disposed || _entries.Count <= _head || entry.Sequence < _entries[_head].Sequence || entry.Sequence > _entries[^1].Sequence)
                return null;
            var buffer = new byte[entry.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = RandomAccess.Read(_handle, buffer.AsSpan(read), entry.Offset + read);
                if (n == 0) return null;
                read += n;
            }
            return buffer;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>The live frame at or immediately before <paramref name="ticks"/> (or the oldest if earlier).</summary>
    public FrameIndexEntry? FindAtOrBefore(long ticks)
    {
        _lock.EnterReadLock();
        try
        {
            var n = _entries.Count - _head;
            if (n == 0) return null;
            int lo = _head, hi = _entries.Count - 1, best = _head;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_entries[mid].TimestampTicks <= ticks) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return _entries[best];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Snapshot of live index entries with timestamps in [fromTicks, toTicks].</summary>
    public IReadOnlyList<FrameIndexEntry> GetRange(long fromTicks, long toTicks)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<FrameIndexEntry>();
            int lo = _head, hi = _entries.Count - 1, first = _entries.Count;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (_entries[mid].TimestampTicks >= fromTicks) { first = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            for (var i = first; i < _entries.Count && _entries[i].TimestampTicks <= toTicks; i++)
                result.Add(_entries[i]);
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
