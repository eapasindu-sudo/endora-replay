using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Video;

/// <summary>
/// Split screen and zoom/pan for the replay output. Split screen puts the two angles side by side, each scaled to
/// half the width with its shape kept (black above and below). Zoom crops the result and scales it back up.
/// </summary>
public sealed class FrameEffects : IFrameEffects
{
    private static readonly ImageEncodingParam[] Encode = [new(ImwriteFlags.JpegQuality, 90)];

    public EncodedFrame Apply(EncodedFrame main, EncodedFrame? second, ViewTransform view)
    {
        using var a = ToBgr(main);
        var size = a.Size();
        using var picture = second is null ? a.Clone() : SideBySide(a, second, size);
        using var result = view.IsIdentity ? picture.Clone() : Zoom(picture, view);
        Cv2.ImEncode(".jpg", result, out var jpeg, Encode);
        return new EncodedFrame
        {
            SourceId = main.SourceId, FrameNumber = main.FrameNumber, TimestampTicks = main.TimestampTicks,
            Width = result.Width, Height = result.Height, Codec = FrameCodec.Jpeg, Data = jpeg,
        };
    }

    private static Mat SideBySide(Mat left, EncodedFrame second, Size size)
    {
        using var right = ToBgr(second);
        var outMat = new Mat(size, MatType.CV_8UC3, Scalar.Black);
        var half = size.Width / 2;
        Place(left, outMat, new Rect(0, 0, half, size.Height));
        Place(right, outMat, new Rect(half, 0, size.Width - half, size.Height));
        return outMat;
    }

    /// <summary>Scales <paramref name="src"/> into <paramref name="area"/> of <paramref name="dst"/>, keeping its shape.</summary>
    private static void Place(Mat src, Mat dst, Rect area)
    {
        var scale = Math.Min((double)area.Width / src.Width, (double)area.Height / src.Height);
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var roi = new Mat(dst, new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        Cv2.Resize(src, roi, new Size(w, h), 0, 0, InterpolationFlags.Area);
    }

    private static Mat Zoom(Mat picture, ViewTransform view)
    {
        var (x, y, w, h) = view.CropRect(picture.Width, picture.Height);
        using var crop = new Mat(picture, new Rect(x, y, w, h));
        return crop.Resize(picture.Size(), 0, 0, InterpolationFlags.Cubic);
    }

    private static Mat ToBgr(EncodedFrame f)
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
