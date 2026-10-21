using System.Buffers.Binary;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Storage;

/// <summary>
/// Endora clip file (.erclip): one angle of a preserved clip.
/// Layout: "ERCLIP01" | int32 sourceId | int32 width | int32 height | byte codec |
/// frames { int64 ticks, int64 frameNumber, int32 length, bytes } ... |
/// index { int64 offset } * count | int32 count | int64 indexOffset | "ERCLIPIX".
/// </summary>
public static class ClipFile
{
    public const string Extension = ".erclip";
    private static readonly byte[] Magic = "ERCLIP01"u8.ToArray();
    private static readonly byte[] FooterMagic = "ERCLIPIX"u8.ToArray();
    private const int HeaderSize = 8 + 4 + 4 + 4 + 1;
    private const int FrameHeaderSize = 8 + 8 + 4;
    private const int FooterSize = 4 + 8 + 8;

    public sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly List<long> _offsets = [];
        private bool _completed;

        public Writer(string path, int sourceId, int width, int height, FrameCodec codec)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            _stream = new FileStream(path + ".partial", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            Span<byte> h = stackalloc byte[HeaderSize];
            Magic.CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], sourceId);
            BinaryPrimitives.WriteInt32LittleEndian(h[12..], width);
            BinaryPrimitives.WriteInt32LittleEndian(h[16..], height);
            h[20] = (byte)codec;
            _stream.Write(h);
        }

        public string Path { get; }
        public int Count => _offsets.Count;

        public void Append(long ticks, long frameNumber, ReadOnlySpan<byte> data)
        {
            _offsets.Add(_stream.Position);
            Span<byte> fh = stackalloc byte[FrameHeaderSize];
            BinaryPrimitives.WriteInt64LittleEndian(fh, ticks);
            BinaryPrimitives.WriteInt64LittleEndian(fh[8..], frameNumber);
            BinaryPrimitives.WriteInt32LittleEndian(fh[16..], data.Length);
            _stream.Write(fh);
            _stream.Write(data);
        }

        /// <summary>Writes the index and atomically renames the file into place.</summary>
        public void Complete()
        {
            var indexOffset = _stream.Position;
            Span<byte> b = stackalloc byte[8];
            foreach (var o in _offsets)
            {
                BinaryPrimitives.WriteInt64LittleEndian(b, o);
                _stream.Write(b);
            }
            Span<byte> f = stackalloc byte[FooterSize];
            BinaryPrimitives.WriteInt32LittleEndian(f, _offsets.Count);
            BinaryPrimitives.WriteInt64LittleEndian(f[4..], indexOffset);
            FooterMagic.CopyTo(f[12..]);
            _stream.Write(f);
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            File.Move(Path + ".partial", Path, overwrite: true);
            _completed = true;
        }

        public void Dispose()
        {
            if (_completed) return;
            _stream.Dispose();
            try { File.Delete(Path + ".partial"); } catch (IOException) { }
        }
    }

    /// <summary>Random-access reader; also the playback sequence for a saved clip angle.</summary>
    public sealed class Reader : IFrameSequence, IDisposable
    {
        private readonly FileStream _stream;
        private readonly long[] _offsets;
        private readonly long[] _ticks;
        private readonly object _gate = new();

        public Reader(string path)
        {
            Path = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            Span<byte> h = stackalloc byte[HeaderSize];
            _stream.ReadExactly(h);
            if (!h[..8].SequenceEqual(Magic)) throw new InvalidDataException($"{path} is not an Endora clip file.");
            SourceId = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            Width = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
            Height = BinaryPrimitives.ReadInt32LittleEndian(h[16..]);
            Codec = (FrameCodec)h[20];

            if (_stream.Length < HeaderSize + FooterSize) throw new InvalidDataException($"{path} is truncated.");
            Span<byte> f = stackalloc byte[FooterSize];
            _stream.Position = _stream.Length - FooterSize;
            _stream.ReadExactly(f);
            if (!f[12..].SequenceEqual(FooterMagic)) throw new InvalidDataException($"{path} has no index (incomplete write?).");
            var count = BinaryPrimitives.ReadInt32LittleEndian(f);
            var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(f[4..]);

            _offsets = new long[count];
            _ticks = new long[count];
            _stream.Position = indexOffset;
            Span<byte> b = stackalloc byte[8];
            for (var i = 0; i < count; i++)
            {
                _stream.ReadExactly(b);
                _offsets[i] = BinaryPrimitives.ReadInt64LittleEndian(b);
            }
            for (var i = 0; i < count; i++)
            {
                _stream.Position = _offsets[i];
                _stream.ReadExactly(b);
                _ticks[i] = BinaryPrimitives.ReadInt64LittleEndian(b);
            }
        }

        public string Path { get; }
        public int SourceId { get; }
        public int Width { get; }
        public int Height { get; }
        public FrameCodec Codec { get; }
        public int Count => _offsets.Length;
        public long StartTicks => Count > 0 ? _ticks[0] : 0;
        public long EndTicks => Count > 0 ? _ticks[^1] : 0;
        public long TicksAt(int index) => _ticks[index];

        public EncodedFrame Read(int index)
        {
            if ((uint)index >= (uint)Count) throw new FrameUnavailableException($"Frame {index} is outside clip {System.IO.Path.GetFileName(Path)}.");
            lock (_gate)
            {
                Span<byte> fh = stackalloc byte[FrameHeaderSize];
                _stream.Position = _offsets[index];
                _stream.ReadExactly(fh);
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(fh);
                var frameNumber = BinaryPrimitives.ReadInt64LittleEndian(fh[8..]);
                var len = BinaryPrimitives.ReadInt32LittleEndian(fh[16..]);
                var data = new byte[len];
                _stream.ReadExactly(data);
                return new EncodedFrame
                {
                    SourceId = SourceId, FrameNumber = frameNumber, TimestampTicks = ticks,
                    Width = Width, Height = Height, Codec = Codec, Data = data,
                };
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    public static string FileNameFor(long clipId, int sourceId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"clip{clipId:D5}_cam{sourceId}{Extension}");
}
