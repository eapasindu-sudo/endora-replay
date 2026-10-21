namespace EndoraReplay.Core.Model;

/// <summary>
/// Zoom and pan of the replay picture. <see cref="Zoom"/> 1 shows the whole picture; 2 shows half its width and height.
/// <see cref="CenterX"/>/<see cref="CenterY"/> (0..1) are the point of the picture shown in the middle; they are
/// kept far enough in that the view never leaves the picture.
/// </summary>
public sealed record ViewTransform(double Zoom = 1, double CenterX = 0.5, double CenterY = 0.5)
{
    public const double MaxZoom = 4;
    public static readonly ViewTransform Identity = new();

    public bool IsIdentity => Zoom <= 1.0001;

    /// <summary>Same view with the zoom and centre clamped to valid values.</summary>
    public ViewTransform Clamped()
    {
        var z = Math.Clamp(Zoom, 1, MaxZoom);
        var half = 0.5 / z;
        return new ViewTransform(z, Math.Clamp(CenterX, half, 1 - half), Math.Clamp(CenterY, half, 1 - half));
    }

    public ViewTransform ZoomBy(double factor) => (this with { Zoom = Zoom * factor }).Clamped();

    /// <summary>Moves the view by a fraction of what is currently visible (e.g. 0.1 = a tenth of the view).</summary>
    public ViewTransform PanBy(double dx, double dy) => (this with { CenterX = CenterX + dx / Zoom, CenterY = CenterY + dy / Zoom }).Clamped();

    /// <summary>Area of a <paramref name="width"/>×<paramref name="height"/> picture to show, in pixels (x, y, w, h).</summary>
    public (int X, int Y, int W, int H) CropRect(int width, int height)
    {
        var v = Clamped();
        var w = Math.Max(1, (int)Math.Round(width / v.Zoom));
        var h = Math.Max(1, (int)Math.Round(height / v.Zoom));
        var x = Math.Clamp((int)Math.Round(v.CenterX * width - w / 2.0), 0, width - w);
        var y = Math.Clamp((int)Math.Round(v.CenterY * height - h / 2.0), 0, height - h);
        return (x, y, w, h);
    }
}
