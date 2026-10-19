using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Storage;

namespace EndoraReplay.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "endora-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal static class Frames
{
    public const long Period = TimeSpan.TicksPerSecond / 50;

    public static EncodedFrame Make(int source, long n, int size = 1000) => new()
    {
        SourceId = source,
        FrameNumber = n,
        TimestampTicks = n * Period,
        Width = 1920,
        Height = 1080,
        Codec = FrameCodec.Jpeg,
        Data = Enumerable.Repeat((byte)(n % 251), size).ToArray(),
    };
}

public class DiskRingBufferTests
{
    [Fact]
    public void Writes_and_reads_back_frames()
    {
        using var dir = new TempDir();
        using var rb = new DiskRingBuffer(1, System.IO.Path.Combine(dir.Path, "b.erbuf"), 1 << 20);
        var e = rb.Write(Frames.Make(1, 0));
        rb.Write(Frames.Make(1, 1));
        Assert.Equal(2, rb.Count);
        var data = rb.TryRead(e);
        Assert.NotNull(data);
        Assert.All(data!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Wraps_and_evicts_oldest_first()
    {
        using var dir = new TempDir();
        const int size = 100_000;
        using var rb = new DiskRingBuffer(1, System.IO.Path.Combine(dir.Path, "b.erbuf"), 1 << 20); // ~10 frames
        var first = rb.Write(Frames.Make(1, 0, size));
        for (var i = 1; i < 50; i++) rb.Write(Frames.Make(1, i, size));

        Assert.Null(rb.TryRead(first)); // overwritten → reported missing, never substituted
        var oldest = rb.Oldest!.Value;
        var newest = rb.Newest!.Value;
        Assert.Equal(49, newest.FrameNumber);
        Assert.True(rb.Count is >= 9 and <= 10);
        Assert.Equal(newest.FrameNumber - rb.Count + 1, oldest.FrameNumber);

        // Every live entry is readable and contains its own data.
        foreach (var e in rb.GetRange(0, long.MaxValue))
        {
            var d = rb.TryRead(e);
            Assert.NotNull(d);
            Assert.Equal((byte)(e.FrameNumber % 251), d![0]);
        }
        Assert.True(rb.BytesLive <= rb.CapacityBytes);
    }

    [Fact]
    public void Finds_frames_by_timestamp()
    {
        using var dir = new TempDir();
        using var rb = new DiskRingBuffer(1, System.IO.Path.Combine(dir.Path, "b.erbuf"), 4 << 20);
        for (var i = 0; i < 100; i++) rb.Write(Frames.Make(1, i));
        Assert.Equal(10, rb.FindAtOrBefore(10 * Frames.Period + 5)!.Value.FrameNumber);
        Assert.Equal(0, rb.FindAtOrBefore(-1)!.Value.FrameNumber);
        var range = rb.GetRange(20 * Frames.Period, 29 * Frames.Period);
        Assert.Equal(10, range.Count);
        Assert.Equal(20, range[0].FrameNumber);
    }

    [Fact]
    public void Rejects_out_of_order_timestamps()
    {
        using var dir = new TempDir();
        using var rb = new DiskRingBuffer(1, System.IO.Path.Combine(dir.Path, "b.erbuf"), 1 << 20);
        rb.Write(Frames.Make(1, 5));
        Assert.Throws<InvalidOperationException>(() => rb.Write(Frames.Make(1, 4)));
    }
}

public class ClipFileTests
{
    [Fact]
    public void Round_trips_frames()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "c" + ClipFile.Extension);
        using (var w = new ClipFile.Writer(path, 3, 1920, 1080, FrameCodec.Jpeg))
        {
            for (var i = 0; i < 20; i++)
            {
                var f = Frames.Make(3, i, 500 + i);
                w.Append(f.TimestampTicks, f.FrameNumber, f.Data);
            }
            w.Complete();
        }
        using var r = new ClipFile.Reader(path);
        Assert.Equal(20, r.Count);
        Assert.Equal(3, r.SourceId);
        var f7 = r.Read(7);
        Assert.Equal(507, f7.Data.Length);
        Assert.Equal(7 * Frames.Period, r.TicksAt(7));
        Assert.Throws<FrameUnavailableException>(() => r.Read(20));
    }

    [Fact]
    public void Incomplete_file_is_rejected()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "c" + ClipFile.Extension);
        using (var w = new ClipFile.Writer(path, 3, 1920, 1080, FrameCodec.Jpeg))
            w.Append(0, 0, new byte[10]); // disposed without Complete
        Assert.False(File.Exists(path));
    }
}

public class ClipServiceTests
{
    [Fact]
    public async Task Saves_multi_angle_clip_that_survives_buffer_rollover()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        await using var diag = new DiagnosticsService(null, () => clock.NowTicks);
        using var storage = new StorageService(dir.Path, 0, diag);
        using var buffers = new RollingBufferService(storage.BufferFolder, diag);
        buffers.Open(1, 2 << 20);
        buffers.Open(2, 2 << 20);
        for (var i = 0; i < 100; i++)
        {
            buffers.Write(Frames.Make(1, i));
            buffers.Write(Frames.Make(2, i));
        }
        var clips = new ClipService(buffers, storage, diag, clock);
        var clip = await clips.SaveClipAsync(40 * Frames.Period, 59 * Frames.Period, 2, [1, 2, 7], ["wicket"]);

        Assert.Equal(2, clip.Files.Count);
        Assert.True(clip.Id > 0);

        // Roll the buffer far past the clip.
        for (var i = 100; i < 3000; i++) buffers.Write(Frames.Make(1, i));
        Assert.Throws<FrameUnavailableException>(() => buffers.GetSequence(1, 40 * Frames.Period, 59 * Frames.Period));

        var seq = clips.OpenAngle(clip.Id, 1);
        Assert.Equal(20, seq.Count);
        Assert.Equal(40, seq.Read(0).FrameNumber);
        Assert.Throws<FrameUnavailableException>(() => clips.OpenAngle(clip.Id, 7));

        clips.ToggleTag(clip.Id, "review");
        var reloaded = clips.Get(clip.Id)!;
        Assert.Contains("REVIEW", reloaded.Tags);
        Assert.Contains("wicket", reloaded.Tags);
        (seq as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Refuses_clip_when_disk_limit_would_be_crossed()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        await using var diag = new DiagnosticsService(null, () => clock.NowTicks);
        using var storage = new StorageService(dir.Path, 1_000_000, diag); // 1 PB reserve
        using var buffers = new RollingBufferService(storage.BufferFolder, diag);
        buffers.Open(1, 2 << 20);
        for (var i = 0; i < 10; i++) buffers.Write(Frames.Make(1, i));
        var clips = new ClipService(buffers, storage, diag, clock);
        await Assert.ThrowsAsync<IOException>(() => clips.SaveClipAsync(0, 9 * Frames.Period, 1, [1]));
    }

    [Fact]
    public async Task Low_disk_deletes_oldest_clips_but_not_ones_in_use()
    {
        using var dir = new TempDir();
        var clock = new ManualClock();
        await using var diag = new DiagnosticsService(null, () => clock.NowTicks);
        using var storage = new StorageService(dir.Path, 0, diag);
        using var buffers = new RollingBufferService(storage.BufferFolder, diag);
        buffers.Open(1, 2 << 20);
        for (var i = 0; i < 30; i++) buffers.Write(Frames.Make(1, i));
        var clips = new ClipService(buffers, storage, diag, clock);
        var a = await clips.SaveClipAsync(0, 5 * Frames.Period, 1, [1]);
        var b = await clips.SaveClipAsync(6 * Frames.Period, 11 * Frames.Period, 1, [1], ["wicket"]);
        var c = await clips.SaveClipAsync(12 * Frames.Period, 17 * Frames.Period, 1, [1]);

        storage.MinFreeBytes = long.MaxValue / 2; // drive "full"
        clips.IsInUse = id => id == c.Id;
        clips.KeepTaggedClips = true;
        Assert.Equal(1, clips.DeleteOldestUntilFree(0));
        Assert.Null(clips.Get(a.Id));
        Assert.NotNull(clips.Get(b.Id)); // tagged
        Assert.NotNull(clips.Get(c.Id)); // armed

        clips.KeepTaggedClips = false;
        Assert.Equal(1, clips.DeleteOldestUntilFree(0));
        Assert.Null(clips.Get(b.Id));
        Assert.NotNull(clips.Get(c.Id));
    }
}
