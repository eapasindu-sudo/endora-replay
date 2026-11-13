using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EndoraReplay.App.Output;

/// <summary>
/// Shows the scoreboard web page full screen on its own display (an HDMI output wired to an ATEM input) on a black
/// background, so the ATEM luma key (DSK 2 or upstream key 1) keys out everything the page leaves transparent. The page reloads by itself
/// if it fails to load. Runs on the main UI thread. HARDWARE PATH: not yet tested on the target PC and ATEM.
/// </summary>
public sealed class ScoreboardOutput(DiagnosticsService diagnostics, string dataFolder)
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private Window? _window;
    private WebView2? _web;
    private ScoreboardSettings _s = new();
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _loaded;
    private bool _retryHooked;

    /// <summary>What the operator sees about the scoreboard page: showing, loading, or why not.</summary>
    public string Status { get; private set; } = "Off";
    public bool Ready { get; private set; }

    /// <summary>Opens, moves or closes the scoreboard window to match the settings. Call on the UI thread.</summary>
    public void Apply(ScoreboardSettings settings)
    {
        var urlChanged = settings.Url != _s.Url;
        _s = new ScoreboardSettings
        {
            Enabled = settings.Enabled, Url = settings.Url, DisplayDevice = settings.DisplayDevice,
            AtemInput = settings.AtemInput, ZoomPercent = settings.ZoomPercent,
        };
        if (!_s.Enabled || string.IsNullOrWhiteSpace(_s.Url))
        {
            Close();
            Status = _s.Enabled ? "No scoreboard link set." : "Off";
            return;
        }
        if (_window is null) Create();
        else if (urlChanged) Navigate();
        if (_web is not null) _web.ZoomFactor = _s.ZoomPercent / 100.0;
        Place();
    }

    private void Create()
    {
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(dataFolder, "webview2") };
        _web.NavigationCompleted += (_, e) =>
        {
            _loaded = e.IsSuccess;
            Ready = e.IsSuccess;
            Status = e.IsSuccess ? "Showing the scoreboard page." : $"Page did not load ({e.WebErrorStatus}); retrying every 10 s.";
            if (!e.IsSuccess) _retry.Start();
            else _retry.Stop();
            diagnostics.Log(e.IsSuccess ? LogSeverity.Info : LogSeverity.Warning, LogCategory.Output, "Scoreboard: " + Status);
        };
        if (!_retryHooked) _retry.Tick += (_, _) => { if (!_loaded) Navigate(); };
        _retryHooked = true;
        _window = new Window
        {
            Title = "Endora Scoreboard Output",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Black,
            Content = _web,
            Left = -32000, Top = -32000, Width = 64, Height = 64,
        };
        _window.SourceInitialized += (_, _) => Place();
        _window.Show();
        _ = InitAsync();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    private async Task InitAsync()
    {
        try
        {
            await _web!.EnsureCoreWebView2Async();
            // A broadcast overlay must never show browser chrome or prompts.
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _web.CoreWebView2.ScriptDialogOpening += (_, e) => e.GetDeferral().Complete();
            Navigate();
        }
        catch (Exception e)
        {
            Ready = false;
            Status = $"The page viewer (Microsoft Edge WebView2 Runtime) could not start: {e.Message}";
            diagnostics.Error(LogCategory.Output, "Scoreboard: " + Status);
        }
    }

    private void Navigate()
    {
        if (_web?.CoreWebView2 is null || string.IsNullOrWhiteSpace(_s.Url)) return;
        Status = "Loading the scoreboard page…";
        _loaded = false;
        try { _web.CoreWebView2.Navigate(_s.Url.Trim()); }
        catch (ArgumentException) { Status = "The scoreboard link is not a valid address."; }
    }

    /// <summary>Reloads the page, e.g. when the scoring service changed its overlay.</summary>
    public void Reload()
    {
        if (_web?.CoreWebView2 is null) return;
        Status = "Reloading the scoreboard page…";
        _web.CoreWebView2.Reload();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => _window?.Dispatcher.BeginInvoke(Place);

    private void Place()
    {
        if (_window is null) return;
        var d = DisplayInfo.List().FirstOrDefault(x => string.Equals(x.DeviceName, _s.DisplayDevice, StringComparison.OrdinalIgnoreCase));
        if (d is null || d.IsPrimary)
        {
            _window.Hide();
            Ready = false;
            Status = d is null ? "Choose the scoreboard display (Settings → Output)." : "The scoreboard cannot use the primary (operator) display.";
            return;
        }
        var hwnd = new WindowInteropHelper(_window).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), d.X, d.Y, d.Width, d.Height, 0x0010 | 0x0040);
        _window.Show();
        if (_loaded)
        {
            Ready = true;
            Status = "Showing the scoreboard page.";
        }
    }

    public void Close()
    {
        _retry.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _web?.Dispose();
        _web = null;
        _window?.Close();
        _window = null;
        _loaded = false;
        Ready = false;
    }
}
