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
        // The Endora Replay branding (designed on black) centred at 60 % of the picture width.
        var logo = new BitmapImage();
        logo.BeginInit();
        logo.CacheOption = BitmapCacheOption.OnLoad;
        logo.UriSource = new Uri("pack://application:,,,/Assets/endora-branding.png");
        logo.EndInit();
        logo.Freeze();
        var lw = w * 0.6;
        var lh = lw * logo.PixelHeight / logo.PixelWidth;
        dc.DrawImage(logo, new Rect((w - lw) / 2, (h - lh) / 2, lw, lh));
    }
}
