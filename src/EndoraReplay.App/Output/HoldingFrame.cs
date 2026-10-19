using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;
using EndoraReplay.Hardware.Video;

namespace EndoraReplay.App.Output;

/// <summary>Renders the idle holding frame (slate, custom image or black) at the project resolution. Call on an STA thread.</summary>
public static class HoldingFrame
{
    public static BgraImage Render(HoldingFrameMode mode, string? imageFile, VideoFormat format)
    {
        var w = format.Width;
        var h = format.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            if (mode == HoldingFrameMode.Slate || mode == HoldingFrameMode.LastFrame)
            {
                if (!string.IsNullOrWhiteSpace(imageFile) && File.Exists(imageFile))
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.UriSource = new Uri(imageFile, UriKind.Absolute);
                    img.EndInit();
                    dc.DrawImage(img, new Rect(0, 0, w, h));
                }
                else
                {
                    DrawSlate(dc, w, h);
                }
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var stride = w * 4;
        var pixels = new byte[stride * h];
        rtb.CopyPixels(pixels, stride, 0);
        return new BgraImage(w, h, stride, pixels);
    }

    private static void DrawSlate(DrawingContext dc, int w, int h)
    {
        var bg = new LinearGradientBrush(Color.FromRgb(0x15, 0x0B, 0x24), Color.FromRgb(0x2A, 0x0F, 0x12), 45);
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        var s = h / 1080.0;
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x1A));
        var cx = w / 2.0;
        var cy = h * 0.435;
        dc.DrawEllipse(null, new Pen(accent, 10 * s), new Point(cx, cy), 110 * s, 110 * s);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(cx - 35 * s, cy - 60 * s), true, true);
            g.LineTo(new Point(cx - 35 * s, cy + 60 * s), true, false);
            g.LineTo(new Point(cx + 70 * s, cy), true, false);
        }
        dc.DrawGeometry(accent, null, tri);
        DrawCentered(dc, "ENDORA REPLAY", cx, h * 0.6, 96 * s, Brushes.White, FontWeights.Bold, 18 * s);
        DrawCentered(dc, "ACTION REPLAY", cx, h * 0.69, 40 * s, new SolidColorBrush(Color.FromRgb(0xAA, 0x8F, 0xBF)), FontWeights.SemiBold, 14 * s);
    }

    private static void DrawCentered(DrawingContext dc, string text, double cx, double y, double size, Brush brush, FontWeight weight, double spacing)
    {
        // Letter-spaced title: draw glyph runs one character at a time.
        var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        var parts = text.Select(c => new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, brush, 1.0)).ToList();
        var total = parts.Sum(p => p.WidthIncludingTrailingWhitespace) + spacing * (parts.Count - 1);
        var x = cx - total / 2;
        foreach (var p in parts)
        {
            dc.DrawText(p, new Point(x, y));
            x += p.WidthIncludingTrailingWhitespace + spacing;
        }
    }
}
