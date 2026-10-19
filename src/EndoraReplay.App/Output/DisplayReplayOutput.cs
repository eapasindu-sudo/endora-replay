using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private Window? _window;
    private WriteableBitmap? _bitmap;
    private VideoFormat _format = VideoFormat.Hd1080p50;
    private int _pending;
    private OutputReadiness _readiness = OutputReadiness.NotConfigured;
    private string _message = "Not opened";

    public DisplayReplayOutput(OutputSettings settings, DiagnosticsService diagnostics)
    {
        _settings = settings;
        _diag = diagnostics;
    }

    public string Name => $"Display {_settings.DisplayDevice?.Replace(@"\\.\", "") ?? "(none)"}";
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
        ShowHolding();
    }

    private void CreateWindow()
    {
        _bitmap = new WriteableBitmap(_format.Width, _format.Height, 96, 96, PixelFormats.Bgra32, null);
        var image = new Image { Source = _bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _window = new Window
        {
            Title = "Endora Replay Output",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Black,
            Content = image,
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

    /// <summary>Re-reads the display list and updates readiness.</summary>
    public void Revalidate()
    {
        var displays = DisplayInfo.List();
        Display = displays.FirstOrDefault(d => string.Equals(d.DeviceName, _settings.DisplayDevice, StringComparison.OrdinalIgnoreCase));
        var v = ValidateMode(_format);
        var r = Display is null ? OutputReadiness.DeviceMissing : v.IsValid ? OutputReadiness.Ready : OutputReadiness.ModeMismatch;
        if (_settings.DisplayDevice is null) r = OutputReadiness.NotConfigured;
        SetReadiness(r, v.Message);
    }

    public ModeValidationResult ValidateMode(VideoFormat format)
    {
        if (_settings.DisplayDevice is null) return ModeValidationResult.Fail("No output display selected (Settings → Output).");
        var d = Display;
        if (d is null) return ModeValidationResult.Fail($"Display {_settings.DisplayDevice} is not connected.");
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
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
