using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Video;

/// <summary>
/// Luma key for stingers on a black background: dark is transparent, bright is solid. Uses the same clip and gain
/// as the ATEM downstream key so Endora's own blend over the replay matches what the ATEM shows over live.
/// The ATEM's exact key curve is not published; this is a linear approximation, so check the edges on the real switcher.
/// </summary>
public sealed class LumaKeyCompositor : IFrameCompositor
{
    private double _lo;
    private double _hi;

    public LumaKeyCompositor(double clipPercent, double gainPercent) => Configure(clipPercent, gainPercent);

    public void Configure(double clipPercent, double gainPercent)
    {
        _lo = Math.Clamp(clipPercent, 0, 99) / 100.0;
        _hi = _lo + Math.Max(0.01, 1 - Math.Clamp(gainPercent, 0, 100) / 100.0) * (1 - _lo);
    }

    public EncodedFrame Composite(EncodedFrame top, EncodedFrame under, double fadeToUnder)
    {
        using var t = ToBgr(top);
        using var u0 = ToBgr(under);
        using var u = u0.Size() == t.Size() ? u0.Clone() : u0.Resize(t.Size(), 0, 0, InterpolationFlags.Area);

        using var alpha = Alpha(t);
        if (fadeToUnder > 0) Cv2.Multiply(alpha, new Scalar(1 - Math.Clamp(fadeToUnder, 0, 1)), alpha);
        using var alpha3 = new Mat();
        Cv2.Merge([alpha, alpha, alpha], alpha3);
        using var inv3 = new Mat();
        Cv2.Subtract(new Scalar(1, 1, 1), alpha3, inv3);

        using var tf = new Mat();
        using var uf = new Mat();
        t.ConvertTo(tf, MatType.CV_32FC3);
        u.ConvertTo(uf, MatType.CV_32FC3);
        Cv2.Multiply(tf, alpha3, tf);
        Cv2.Multiply(uf, inv3, uf);
        Cv2.Add(tf, uf, tf);
        using var outBgr = new Mat();
        tf.ConvertTo(outBgr, MatType.CV_8UC3);
        Cv2.ImEncode(".jpg", outBgr, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        return new EncodedFrame
        {
            SourceId = top.SourceId, FrameNumber = top.FrameNumber, TimestampTicks = top.TimestampTicks,
            Width = outBgr.Width, Height = outBgr.Height, Codec = FrameCodec.Jpeg, Data = jpeg,
        };
    }

    public double Coverage(EncodedFrame top)
    {
        using var t = ToBgr(top);
        using var small = t.Resize(new Size(Math.Max(1, t.Width / 4), Math.Max(1, t.Height / 4)), 0, 0, InterpolationFlags.Area);
        using var gray = small.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var solid = new Mat();
        Cv2.Threshold(gray, solid, (_lo + 0.98 * (_hi - _lo)) * 255, 255, ThresholdTypes.Binary);
        return Cv2.CountNonZero(solid) / (double)(gray.Width * gray.Height);
    }

    public EncodedFrame Black(VideoFormat format)
    {
        using var black = new Mat(format.Height, format.Width, MatType.CV_8UC3, Scalar.Black);
        Cv2.ImEncode(".jpg", black, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        return new EncodedFrame
        {
            SourceId = -1, FrameNumber = -1, TimestampTicks = 0, Width = format.Width, Height = format.Height, Codec = FrameCodec.Jpeg, Data = jpeg,
        };
    }

    private Mat Alpha(Mat bgr)
    {
        using var gray = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
        var a = new Mat();
        gray.ConvertTo(a, MatType.CV_32F, 1.0 / 255);
        Cv2.Subtract(a, new Scalar(_lo), a);
        Cv2.Multiply(a, new Scalar(1.0 / (_hi - _lo)), a);
        Cv2.Max(a, new Scalar(0), a);
        Cv2.Min(a, new Scalar(1), a);
        return a;
    }

    private static Mat ToBgr(EncodedFrame f)
    {
        if (f.Codec == FrameCodec.Bgra32)
        {
            using var bgra = Mat.FromPixelData(f.Height, f.Width, MatType.CV_8UC4, f.Data);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        var m = Cv2.ImDecode(f.Data, ImreadModes.Color);
        if (m.Empty()) throw new FrameUnavailableException($"Frame {f.FrameNumber} could not be decoded for the stinger blend.");
        return m;
    }
}
