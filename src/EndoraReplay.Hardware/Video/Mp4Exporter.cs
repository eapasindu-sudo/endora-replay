using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Video;

/// <summary>
/// Writes a clip (or highlights package) to an MP4 file at the project frame rate and the chosen speed, choosing
/// frames by timestamp the same way the replay output does. No audio. Uses H.264 when this PC's OpenCV build can
/// encode it, otherwise MPEG-4 Part 2 (plays in most players and social apps re-encode on upload).
/// </summary>
public static class Mp4Exporter
{
    /// <returns>The codec that was used ("H.264" or "MPEG-4").</returns>
    public static string Export(IFrameSequence seq, string path, VideoFormat format, double speed, IProgress<double>? progress, CancellationToken ct)
    {
        if (seq.Count == 0) throw new FrameUnavailableException("Nothing to export.");
        speed = Math.Clamp(speed, 0.01, 2);
        using var first = Decode(seq.Read(0));
        var size = new Size(first.Width, first.Height);

        VideoWriter? writer = null;
        var codec = "";
        foreach (var (fourcc, name) in new[] { ("avc1", "H.264"), ("mp4v", "MPEG-4") })
        {
            writer = new VideoWriter(path, FourCC.FromString(fourcc), format.FramesPerSecond, size);
            if (writer.IsOpened())
            {
                codec = name;
                break;
            }
            writer.Dispose();
            writer = null;
        }
        if (writer is null) throw new IOException("No MP4 encoder is available on this PC.");

        try
        {
            using (writer)
            {
                var period = format.FrameDurationTicks;
                var total = seq.EndTicks - seq.StartTicks;
                var idx = 0;
                var shown = -1;
                Mat? frame = null;
                try
                {
                    for (double pos = 0; pos <= total + period / 2.0; pos += period * speed)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = seq.StartTicks + (long)pos;
                        while (idx + 1 < seq.Count && seq.TicksAt(idx + 1) <= target) idx++;
                        if (idx != shown)
                        {
                            frame?.Dispose();
                            frame = Decode(seq.Read(idx));
                            if (frame.Width != size.Width || frame.Height != size.Height)
                            {
                                var scaled = frame.Resize(size, 0, 0, InterpolationFlags.Area);
                                frame.Dispose();
                                frame = scaled;
                            }
                            shown = idx;
                        }
                        writer.Write(frame);
                        progress?.Report(total > 0 ? pos / total : 1);
                    }
                }
                finally
                {
                    frame?.Dispose();
                }
            }
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
        progress?.Report(1);
        return codec;
    }

    private static Mat Decode(EncodedFrame f)
    {
        if (f.Codec == FrameCodec.Bgra32)
        {
            using var bgra = Mat.FromPixelData(f.Height, f.Width, MatType.CV_8UC4, f.Data);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        var m = Cv2.ImDecode(f.Data, ImreadModes.Color);
        if (m.Empty()) throw new FrameUnavailableException($"Frame {f.FrameNumber} could not be decoded.");
        return m;
    }
}
