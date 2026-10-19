using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Simulation;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Video;

/// <summary>Decoded BGRA pixels ready for a WPF WriteableBitmap.</summary>
public sealed record BgraImage(int Width, int Height, int Stride, byte[] Pixels);

public static class JpegCodec
{
    /// <summary>
    /// Decodes a frame to BGRA. <paramref name="reduce"/> 1, 2, 4 or 8 decodes at reduced size directly in libjpeg,
    /// which keeps eight preview tiles cheap.
    /// </summary>
    public static BgraImage Decode(EncodedFrame frame, int reduce = 1)
    {
        if (frame.Codec == FrameCodec.Bgra32)
            return new BgraImage(frame.Width, frame.Height, frame.Width * 4, frame.Data);

        var mode = reduce switch
        {
            >= 8 => ImreadModes.ReducedColor8,
            >= 4 => ImreadModes.ReducedColor4,
            >= 2 => ImreadModes.ReducedColor2,
            _ => ImreadModes.Color,
        };
        using var bgr = Cv2.ImDecode(frame.Data, mode);
        if (bgr.Empty()) throw new FrameUnavailableException($"Source {frame.SourceId} frame {frame.FrameNumber} could not be decoded.");
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var stride = bgra.Width * 4;
        var pixels = new byte[stride * bgra.Height];
        System.Runtime.InteropServices.Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new BgraImage(bgra.Width, bgra.Height, stride, pixels);
    }

    /// <summary>Encodes a BGRA image (e.g. a rendered holding slate) to JPEG.</summary>
    public static byte[] EncodeBgra(BgraImage image, int quality = 92)
    {
        using var bgra = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, image.Pixels, image.Stride);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.ImEncode(".jpg", bgr, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return jpeg;
    }
}

/// <summary>
/// Loads full-frame stinger videos (mp4/mov/avi) for <see cref="TransitionMode.EndoraStinger"/> into memory,
/// scaled to the project format. Hardware-independent but must be checked with the real stinger file.
/// </summary>
public sealed class FileStingerProvider : IStingerProvider
{
    private (IFrameSequence, int)? _in;
    private (IFrameSequence, int)? _out;

    public string? LoadedInFile { get; private set; }
    public string? LoadedOutFile { get; private set; }

    public (IFrameSequence Frames, int CutFrame)? Get(bool isReturn) => isReturn ? _out ?? _in : _in;

    public void Load(string? inFile, string? outFile, int cutFrame, VideoFormat format)
    {
        _in = string.IsNullOrWhiteSpace(inFile) ? null : (LoadFile(inFile, format), cutFrame);
        LoadedInFile = inFile;
        _out = string.IsNullOrWhiteSpace(outFile) ? null : (LoadFile(outFile, format), cutFrame);
        LoadedOutFile = outFile;
    }

    public static IFrameSequence LoadFile(string path, VideoFormat format)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Stinger file not found.", path);
        using var cap = new VideoCapture(path);
        if (!cap.IsOpened()) throw new IOException($"Cannot open stinger video {path}.");
        var frames = new List<EncodedFrame>();
        using var mat = new Mat();
        using var scaled = new Mat();
        var enc = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 92) };
        long n = 0;
        while (cap.Read(mat) && !mat.Empty())
        {
            var src = mat;
            if (mat.Width != format.Width || mat.Height != format.Height)
            {
                Cv2.Resize(mat, scaled, new Size(format.Width, format.Height), 0, 0, InterpolationFlags.Area);
                src = scaled;
            }
            Cv2.ImEncode(".jpg", src, out var jpeg, enc);
            frames.Add(new EncodedFrame
            {
                SourceId = -1, FrameNumber = n, TimestampTicks = n * format.FrameDurationTicks,
                Width = format.Width, Height = format.Height, Codec = FrameCodec.Jpeg, Data = jpeg,
            });
            n++;
            if (n > format.FramesPerSecond * 10) throw new InvalidDataException("Stinger longer than 10 seconds.");
        }
        if (frames.Count == 0) throw new InvalidDataException($"Stinger {path} has no frames.");
        return new MemoryFrameSequence(-1, frames);
    }
}
