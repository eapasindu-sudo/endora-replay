using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Hardware.Video;

namespace EndoraReplay.App.Output;

/// <summary>
/// Replay output on a GPU display connector (HDMI → ATEM replay input) as a borderless full-screen window.
/// The window lives on its own UI thread so the operator UI can never stall the output.
/// Readiness requires the display to exist, not be the primary display, and match the project resolution and rate.
/// HARDWARE PATH: frame pacing follows the display's vsync via WPF composition; validate on the target GPU.
/// </summary>
public sealed class DisplayReplayOutput : IReplayOutput
{
    private readonly OutputSettings _settings;
    private readonly DiagnosticsService _diag;
    private volatile string? _device;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private Window? _window;
    private WriteableBitmap? _bitmap;
    private FrameworkElement? _badge;
    private volatile bool _badgeVisible;
    private VideoFormat _format = VideoFormat.Hd1080p50;
    private int _pending;
    private OutputReadiness _readiness = OutputReadiness.NotConfigured;
    private string _message = "Not opened";

    public DisplayReplayOutput(OutputSettings settings, DiagnosticsService diagnostics)
    {
        _settings = settings;
        _device = settings.DisplayDevice;
        _diag = diagnostics;
    }

    public string Name => $"Display {_device?.Replace(@"\\.\", "") ?? "(none)"}";
    public bool IsSimulated => false;
    public OutputReadiness Readiness => _readiness;
    public string ReadinessMessage => _message;
    public EncodedFrame? LastFrame { get; private set; }
    public BgraImage? HoldingImage { get; private set; }
    public DisplayInfo? Display { get; private set; }

    public event Action<OutputReadiness, string>? ReadinessChanged;

    public async Task OpenAsync(VideoFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                HoldingImage = HoldingFrame.Render(_settings.Holding, _settings.HoldingImageFile, format);
                CreateWindow();
                started.TrySetResult();
            }
            catch (Exception e)
            {
                started.TrySetException(e);
                return;
            }
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Replay output UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Revalidate();
        // The window was placed before the display was known (SourceInitialized runs first), which left it hidden.
        _dispatcher?.BeginInvoke(PlaceOnDisplay);
        ShowHolding();
    }

    private void CreateWindow()
    {
        _bitmap = new WriteableBitmap(_format.Width, _format.Height, 96, 96, PixelFormats.Bgra32, null);
        var image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var root = new Grid();
        root.Children.Add(image);
        _badge = ReplayBadgeVisual.Create(_settings.ReplayBadgeText, _format.Height);
        _badge.Visibility = Visibility.Collapsed;
        root.Children.Add(_badge);
        _window = new Window
        {
            Title = "Endora Replay Output",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Black,
            Content = root,
            Left = -32000,
            Top = -32000,
            Width = 64,
            Height = 64,
        };
        _window.SourceInitialized += (_, _) => PlaceOnDisplay();
        _window.Show();
    }

    private void PlaceOnDisplay()
    {
        if (_window is null) return;
        var d = Display;
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (d is null || d.IsPrimary)
        {
            _window.Hide();
            return;
        }
        // Physical pixel placement, independent of DPI scaling.
        SetWindowPos(hwnd, new IntPtr(-1), d.X, d.Y, d.Width, d.Height, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
        _window.Show();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Revalidate();
        _dispatcher?.BeginInvoke(PlaceOnDisplay);
    }

    /// <summary>Moves the output to another display without a restart.</summary>
    public void MoveTo(string? device)
    {
        _device = device;
        Revalidate();
        _dispatcher?.BeginInvoke(PlaceOnDisplay);
    }

    /// <summary>Re-reads the display list and updates readiness.</summary>
    public void Revalidate()
    {
        var displays = DisplayInfo.List();
        Display = displays.FirstOrDefault(d => string.Equals(d.DeviceName, _device, StringComparison.OrdinalIgnoreCase));
        var v = ValidateMode(_format);
        var r = Display is null ? OutputReadiness.DeviceMissing : v.IsValid ? OutputReadiness.Ready : OutputReadiness.ModeMismatch;
        if (_device is null) r = OutputReadiness.NotConfigured;
        SetReadiness(r, v.Message);
    }

    public ModeValidationResult ValidateMode(VideoFormat format)
    {
        if (_device is null) return ModeValidationResult.Fail("No output display selected (Settings → Output).");
        var d = Display;
        if (d is null) return ModeValidationResult.Fail($"Display {_device} is not connected.");
        if (d.IsPrimary) return ModeValidationResult.Fail("Replay output cannot be the primary (operator) display.");
        if (d.Width != format.Width || d.Height != format.Height)
            return ModeValidationResult.Fail($"Display is {d.Width}×{d.Height}; set it to {format.Width}×{format.Height} in Windows display settings.");
        var fps = format.FramesPerSecond;
        if (d.RefreshHz > 0 && Math.Abs(d.RefreshHz - fps) > 0.6 && Math.Abs(d.RefreshHz - Math.Round(fps)) > 0.6)
            return ModeValidationResult.Fail($"Display runs at {d.RefreshHz} Hz; set {Math.Round(fps)} Hz to match {format.Name}.");
        return ModeValidationResult.Ok($"{d}");
    }

    private void SetReadiness(OutputReadiness r, string message)
    {
        var changed = r != _readiness || message != _message;
        _readiness = r;
        _message = message;
        if (!changed) return;
        _diag.Log(r == OutputReadiness.Ready ? LogSeverity.Info : LogSeverity.Warning, LogCategory.Output, $"{Name}: {r} – {message}");
        ReadinessChanged?.Invoke(r, message);
    }

    public void Present(EncodedFrame frame)
    {
        LastFrame = frame;
        // Skip if the output thread has not drawn the previous frame yet (never queue up latency).
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        BgraImage img;
        try { img = JpegCodec.Decode(frame); }
        catch
        {
            Interlocked.Exchange(ref _pending, 0);
            throw;
        }
        Draw(img);
    }

    public bool ReplayBadgeVisible => _badgeVisible;

    public void SetReplayBadge(bool visible)
    {
        visible &= _settings.ReplayBadge;
        if (_badgeVisible == visible) return;
        _badgeVisible = visible;
        _dispatcher?.BeginInvoke(() => { if (_badge is not null) _badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; });
    }

    public void ShowHolding()
    {
        LastFrame = null;
        if (HoldingImage is { } h)
        {
            Interlocked.Exchange(ref _pending, 1);
            Draw(h);
        }
    }

    private void Draw(BgraImage img)
    {
        var d = _dispatcher;
        if (d is null)
        {
            Interlocked.Exchange(ref _pending, 0);
            return;
        }
        d.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (_bitmap is not null && img.Width == _bitmap.PixelWidth && img.Height == _bitmap.PixelHeight)
                    _bitmap.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.Pixels, img.Stride, 0);
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _dispatcher?.InvokeShutdown();
        return ValueTask.CompletedTask;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// SIMULATED replay output: frames reach only the on-screen confidence monitor; nothing reaches the ATEM.
/// Shown in purple in the UI. For rehearsal without an output display.
/// </summary>
public sealed class SimulatedReplayOutput : IReplayOutput
{
    public string Name => "Simulated output (no HDMI)";
    public bool IsSimulated => true;
    public OutputReadiness Readiness => OutputReadiness.Ready;
    public string ReadinessMessage => "SIMULATED: no physical output";
    public EncodedFrame? LastFrame { get; private set; }
    public BgraImage? HoldingImage { get; set; }
    public event Action<OutputReadiness, string>? ReadinessChanged { add { } remove { } }
    public Task OpenAsync(VideoFormat format, CancellationToken cancellationToken) => Task.CompletedTask;
    public ModeValidationResult ValidateMode(VideoFormat format) => ModeValidationResult.Ok("SIMULATED");
    public void Present(EncodedFrame frame) => LastFrame = frame;
    public void ShowHolding() => LastFrame = null;
    public bool ReplayBadgeVisible { get; private set; }
    public void SetReplayBadge(bool visible) => ReplayBadgeVisible = visible;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The animated REPLAY badge: a spinning ring and a word that turns over every few seconds, in the title-safe corner.</summary>
public static class ReplayBadgeVisual
{
    public static FrameworkElement Create(string text, int outputHeight)
    {
        var scale = outputHeight / 1080.0;
        var ringSize = 46 * scale;
        var ring = new Grid { Width = ringSize, Height = ringSize, Margin = new Thickness(0, 0, 14 * scale, 0), VerticalAlignment = VerticalAlignment.Center };
        var arc = new Ellipse
        {
            Stroke = new LinearGradientBrush(Color.FromRgb(0xFF, 0x3B, 0x3B), Color.FromRgb(0xFF, 0xB0, 0x20), 0),
            StrokeThickness = 6 * scale,
            StrokeDashArray = [5, 2.2],
            StrokeDashCap = PenLineCap.Round,
        };
        var spin = new RotateTransform();
        arc.RenderTransform = spin;
        arc.RenderTransformOrigin = new Point(0.5, 0.5);
        ring.Children.Add(arc);
        ring.Children.Add(new Path
        {
            Data = Geometry.Parse("M0,0 L0,12 L10,6 Z"), Fill = Brushes.White, Stretch = Stretch.Uniform,
            Width = ringSize * 0.34, Height = ringSize * 0.34, Margin = new Thickness(ringSize * 0.06, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });

        var word = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(text) ? "REPLAY" : text.ToUpperInvariant(),
            Foreground = Brushes.White, FontSize = 40 * scale, FontWeight = FontWeights.Black,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center,
        };
        var turn = new ScaleTransform(1, 1);
        word.RenderTransform = turn;
        word.RenderTransformOrigin = new Point(0.5, 0.5);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(ring);
        panel.Children.Add(word);
        var badge = new Border
        {
            Child = panel,
            Padding = new Thickness(18 * scale, 10 * scale, 26 * scale, 10 * scale),
            CornerRadius = new CornerRadius(40 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x12, 0x16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x3B, 0x3B)),
            BorderThickness = new Thickness(2 * scale),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Inside the 5% title-safe area.
            Margin = new Thickness(0, 0, 96 * scale, 54 * scale),
        };

        spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
        var flip = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(3.5) };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        flip.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flip.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.9))));
        flip.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2)), ease));
        flip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.5)), ease));
        turn.BeginAnimation(ScaleTransform.ScaleXProperty, flip);
        return badge;
    }
}
