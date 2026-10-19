using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using EndoraReplay.App.Output;
using EndoraReplay.App.Services;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Storage;
using EndoraReplay.Hardware.Video;

namespace EndoraReplay.App.Views;

public sealed class ClipRow
{
    public long Id { get; init; }
    public string Title => $"#{Id}";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Detail { get; init; } = "";
    public bool Armed { get; init; }
    public Visibility ArmedVisibility => Armed ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Thumb { get; init; }
}

public sealed record LogRow(string Time, string Category, string Message, Brush Color);

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly KeyBindingService _keys;
    private readonly ObservableCollection<ClipRow> _clips = [];
    private readonly ObservableCollection<LogRow> _log = [];
    private readonly Dictionary<long, ImageSource?> _thumbs = [];
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource _cts = new();
    private readonly Tile[] _tiles = new Tile[8];
    private readonly Dictionary<ReplayState, Border> _flowChips = [];
    private WriteableBitmap? _previewBmp, _programBmp, _outBmp;
    private long? _tagTargetClip;
    private double? _jogLastAngle;
    private double _jogAccum;
    private bool _closing;
    private string? _transientError;
    private DateTime _transientUntil;

    public MainWindow(AppHost host)
    {
        _host = host;
        _keys = new KeyBindingService(host.Settings.KeyBindings);
        InitializeComponent();
        ClipList.ItemsSource = _clips;
        LogList.ItemsSource = _log;
        VersionText.Text = $"INSTANT REPLAY · v{UpdateService.CurrentVersionText}";
        Loaded += OnLoaded;
    }

    private Brush B(string key) => (Brush)FindResource(key);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DecorateActionButtons(this);
        BuildTiles();
        BuildStateFlow();
        BuildTagButtons();
        TagFilter.ItemsSource = new[] { "All tags" }.Concat(StandardTags.Cricket).ToList();
        TagFilter.SelectedIndex = 0;

        foreach (var ev in _host.Diagnostics.Recent(150).Reverse()) AddLog(ev);
        _host.Diagnostics.EventLogged += ev => Dispatcher.BeginInvoke(() => AddLog(ev));
        _host.Clips.ClipsChanged += () => Dispatcher.BeginInvoke(RefreshClips);
        _host.Operator.TagPickerRequested += id => Dispatcher.BeginInvoke(() => ShowTagPicker(id));
        _host.Updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdate);
        _ = _host.Updates.RunBackgroundAsync(_cts.Token);

        RefreshClips();
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
        _ = Task.Run(() => RenderLoopAsync(_cts.Token));
    }

    // ================= actions =================

    private void DecorateActionButtons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase { Tag: string action } b && b.Content is string label && action is not ("Automatic" or "Manual"))
            {
                if (b is Button btn) btn.Click += OnAction;
                b.Content = ButtonContent(label, action);
            }
            DecorateActionButtons(child);
        }
    }

    private object ButtonContent(string label, string action)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Foreground = null });
        if (_host.Settings.KeyBindings.TryGetValue(action, out var g) && !string.IsNullOrWhiteSpace(g))
        {
            sp.Children.Add(new Border
            {
                Style = (Style)FindResource("KeyHint"),
                Child = new TextBlock { Text = KeyBindingService.Pretty(g), Style = (Style)FindResource("KeyHintText") },
            });
        }
        return sp;
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action }) Run(action);
    }

    private void OnActionToggle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string action }) Run(action);
    }

    private void Run(string action, double? value = null)
    {
        var err = _host.Operator.Execute(action, value, origin: "operator");
        if (err is not null)
        {
            _transientError = err;
            _transientUntil = DateTime.UtcNow.AddSeconds(4);
        }
        RefreshUi();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnOpenSettings(this, e);
            e.Handled = true;
            return;
        }
        if (TagPicker.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape) TagPicker.Visibility = Visibility.Collapsed;
            else if (key is >= Key.D1 and <= Key.D8) ApplyTag(StandardTags.Cricket[key - Key.D1]);
            e.Handled = true;
            return;
        }
        var action = _keys.ActionFor(key, Keyboard.Modifiers);
        if (action is null) return;
        if (e.IsRepeat && action is not (OperatorActions.FrameBack or OperatorActions.FrameForward or OperatorActions.ShuttleBack or OperatorActions.ShuttleForward))
        {
            e.Handled = true; // never auto-repeat transport keys
            return;
        }
        Run(action);
        e.Handled = true;
    }

    private void OnReturnMode(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode }) return;
        var s = _host.CloneSettings();
        s.Automation.ReturnToLive = Enum.Parse<ReturnToLiveMode>(mode);
        _ = _host.ApplyAsync(s);
    }

    // ================= jog wheel & timeline =================

    private double JogAngle(MouseEventArgs e)
    {
        var p = e.GetPosition(JogWheel.Parent as IInputElement);
        var center = JogWheel.TranslatePoint(new Point(JogWheel.ActualWidth / 2, JogWheel.ActualHeight / 2), (UIElement)JogWheel.Parent);
        return Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI;
    }

    private void OnJogDown(object sender, MouseButtonEventArgs e)
    {
        _jogLastAngle = JogAngle(e);
        JogWheel.CaptureMouse();
    }

    private void OnJogMove(object sender, MouseEventArgs e)
    {
        if (_jogLastAngle is not { } last) return;
        var a = JogAngle(e);
        var d = a - last;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        _jogLastAngle = a;
        _jogAccum += d;
        JogRotate.Angle += d;
        var frames = (int)(_jogAccum / 10);
        if (frames != 0)
        {
            _jogAccum -= frames * 10;
            Run("Jog", frames);
        }
    }

    private void OnJogUp(object sender, MouseButtonEventArgs e)
    {
        _jogLastAngle = null;
        JogWheel.ReleaseMouseCapture();
    }

    private void OnJogWheel(object sender, MouseWheelEventArgs e)
    {
        var frames = e.Delta / 120 * (Keyboard.Modifiers == ModifierKeys.Shift ? 10 : 1);
        JogRotate.Angle += frames * 10;
        Run("Jog", frames);
    }

    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition(Timeline).X;
        var w = Math.Max(1, Timeline.ActualWidth);
        var seconds = (x / w - 1) * _host.Settings.Buffer.Seconds;
        Run("Seek", seconds);
    }

    // ================= tiles =================

    private sealed class Tile
    {
        public required Border Frame;
        public required Image Image;
        public required TextBlock Health;
        public required TextBlock Rate;
        public required Border NoSignal;
        public required Border PgmBadge;
        public required TextBlock Caption;
        public WriteableBitmap? Bitmap;
        public long LastFrame = -1;
    }

    private void BuildTiles()
    {
        for (var i = 0; i < 8; i++)
        {
            var cam = i + 1;
            var img = new Image { Stretch = Stretch.UniformToFill };
            var noSig = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x19)),
                Child = new TextBlock { Text = "NO SIGNAL", Foreground = B("Mute"), FontWeight = FontWeights.Black, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var health = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold };
            var rate = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            var bottom = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(0xD8, 0, 0, 0), 90) };
            bottom.Children.Add(new Border { Padding = new Thickness(7, 4, 7, 4), Child = health });
            bottom.Children.Add(new Border { Padding = new Thickness(7, 4, 7, 4), Child = rate, HorizontalAlignment = HorizontalAlignment.Right });
            var num = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
                Child = new TextBlock { Text = cam.ToString(), FontWeight = FontWeights.Black, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var pgm = new Border
            {
                Style = (Style)FindResource("Badge"), Background = B("RedSoft"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
                Child = new TextBlock { Text = "PGM", FontSize = 10, FontWeight = FontWeights.Black, Foreground = B("Red") }, Visibility = Visibility.Collapsed,
            };
            var inner = new Grid();
            inner.Children.Add(img);
            inner.Children.Add(noSig);
            inner.Children.Add(bottom);
            inner.Children.Add(num);
            inner.Children.Add(pgm);
            var frame = new Border
            {
                Height = 100, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), BorderBrush = B("Line2"), Background = Brushes.Black,
                ClipToBounds = true, Child = inner, Cursor = Cursors.Hand,
            };
            var caption = new TextBlock { FontSize = 11, Foreground = B("Dim"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new StackPanel { Margin = new Thickness(0, 0, i < 7 ? 10 : 0, 0) };
            stack.Children.Add(frame);
            stack.Children.Add(caption);
            stack.MouseLeftButtonDown += (_, _) => Run(OperatorActions.Camera(cam));
            Tiles.Children.Add(stack);
            _tiles[i] = new Tile { Frame = frame, Image = img, Health = health, Rate = rate, NoSignal = noSig, PgmBadge = pgm, Caption = caption };
        }
    }

    private void BuildStateFlow()
    {
        foreach (var s in Enum.GetValues<ReplayState>())
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 5, 5),
                BorderThickness = new Thickness(1), BorderBrush = B("Line2"), Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x10, 0x14)),
                Child = new TextBlock { Text = s.ToString(), FontSize = 9.5, FontWeight = FontWeights.Black, Foreground = B("Mute") },
            };
            _flowChips[s] = chip;
            StateFlow.Children.Add(chip);
        }
    }

    private void BuildTagButtons()
    {
        var i = 1;
        foreach (var t in StandardTags.Cricket)
        {
            var b = new Button { Margin = new Thickness(0, 0, 8, 8), MinHeight = 48 };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = t, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new Border { Style = (Style)FindResource("KeyHint"), Child = new TextBlock { Text = (i++).ToString(), Style = (Style)FindResource("KeyHintText") } });
            b.Content = sp;
            var tag = t;
            b.Click += (_, _) => ApplyTag(tag);
            TagButtons.Children.Add(b);
        }
    }

    private void ShowTagPicker(long clipId)
    {
        _tagTargetClip = clipId;
        TagPickerTitle.Text = $"TAG CLIP #{clipId}  ·  press 1–8 or click";
        TagPicker.Visibility = Visibility.Visible;
    }

    private void ApplyTag(string tag)
    {
        if (_tagTargetClip is { } id) _host.Operator.Execute("SetTag", text: tag, clipId: id);
        TagPicker.Visibility = Visibility.Collapsed;
    }

    private void OnTagPickerBackground(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == TagPicker) TagPicker.Visibility = Visibility.Collapsed;
    }

    // ================= clips =================

    private void OnTagFilter(object sender, SelectionChangedEventArgs e) => RefreshClips();

    private void RefreshClips()
    {
        var tag = TagFilter.SelectedIndex > 0 ? TagFilter.SelectedItem as string : null;
        var armed = _host.Automation.ArmedClip?.ClipId;
        var list = _host.Clips.List(tag);
        ClipHeader.Text = $"CLIP LIBRARY · {list.Count} CLIPS";
        _clips.Clear();
        foreach (var c in list.Take(200))
        {
            _thumbs.TryGetValue(c.Id, out var thumb);
            if (!_thumbs.ContainsKey(c.Id)) LoadThumb(c);
            _clips.Add(new ClipRow
            {
                Id = c.Id, Tags = c.Tags, Armed = c.Id == armed, Thumb = thumb,
                Detail = $"Cam {c.PrimarySourceId} · {c.Duration.TotalSeconds:0.0} s · {c.Files.Count} angle(s) · {c.CreatedUtc.ToLocalTime():HH:mm:ss}",
            });
        }
    }

    private void LoadThumb(Clip c)
    {
        _thumbs[c.Id] = null;
        if (!c.Files.TryGetValue(c.PrimarySourceId, out var path)) return;
        Task.Run(() =>
        {
            try
            {
                using var r = new ClipFile.Reader(path);
                var img = JpegCodec.Decode(r.Read(r.Count / 2), 8);
                var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Stride);
                bmp.Freeze();
                Dispatcher.BeginInvoke(() => { _thumbs[c.Id] = bmp; RefreshClips(); });
            }
            catch (Exception) { }
        });
    }

    private long? SelectedClipId => (ClipList.SelectedItem as ClipRow)?.Id;
    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e) => OnClipArm(sender, e);
    private void OnClipArm(object sender, RoutedEventArgs e) { if (SelectedClipId is { } id) _host.Operator.Execute("ArmClip", clipId: id); }
    private void OnClipTag(object sender, RoutedEventArgs e) { if (SelectedClipId is { } id) ShowTagPicker(id); }

    private void OnClipQueue(object sender, RoutedEventArgs e)
    {
        if (SelectedClipId is not { } id) return;
        _host.Operator.Execute("ArmClip", clipId: id);
        _host.Operator.Execute(OperatorActions.Queue);
    }

    private void OnClipDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedClipId is not { } id) return;
        if (MessageBox.Show(this, $"Delete clip #{id} and its files?", "Endora Replay", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _host.Clips.Delete(id);
    }

    // ================= log =================

    private void AddLog(LogEvent e)
    {
        if (e.Severity == LogSeverity.Debug) return;
        var color = e.Severity switch
        {
            LogSeverity.Error or LogSeverity.Critical => B("Red"),
            LogSeverity.Warning => B("Amber"),
            _ => e.Category switch
            {
                LogCategory.Atem or LogCategory.Remote => B("Blue"),
                LogCategory.Playback => B("Violet"),
                LogCategory.Automation => B("Amber"),
                LogCategory.Clip => B("Green"),
                _ => B("Dim"),
            },
        };
        _log.Insert(0, new LogRow(e.Utc.ToLocalTime().ToString("HH:mm:ss.fff"), e.Category, e.Message, color));
        while (_log.Count > 300) _log.RemoveAt(_log.Count - 1);
    }

    // ================= periodic UI refresh =================

    private void RefreshUi()
    {
        var s = _host.Settings;
        var atem = _host.Atem.Snapshot;
        var auto = _host.Automation;
        var op = _host.Operator;

        // Header
        AtemDot.Fill = atem.Connected ? B("Green") : B("Red");
        AtemText.Text = atem.Connected
            ? $"{(string.IsNullOrEmpty(atem.ModelName) ? "ATEM" : atem.ModelName)} · {s.Atem.Address}{(_host.Atem.IsSimulated ? " · SIMULATED" : "")}"
            : $"ATEM offline · {s.Atem.Address}";
        PgmText.Text = atem.ProgramInput is { } p ? InputLabel(atem, p) : "—";
        PvwText.Text = atem.PreviewInput is { } v ? InputLabel(atem, v) : "—";

        var state = auto.State;
        StateText.Text = auto.OverrideActive ? "MANUAL OVERRIDE" : state.ToString();
        var (fg, bg) = state switch
        {
            _ when auto.OverrideActive => ("Red", "RedSoft"),
            ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM => ("Red", "RedSoft"),
            ReplayState.REPLAY_ARMED or ReplayState.REPLAY_COMPLETE => ("Amber", "AmberSoft"),
            ReplayState.ERROR or ReplayState.DISCONNECTED => ("Red", "RedSoft"),
            _ => ("Green", "GreenSoft"),
        };
        StateText.Foreground = B(fg);
        StateDot.Fill = B(fg);
        StateChip.Background = B(bg);
        StateChip.BorderBrush = B(fg);

        var health = _host.Diagnostics.SourceHealthSnapshot();
        var cams = s.Sources.Where(x => x.Role == SourceRole.Camera && x.Enabled).ToList();
        var running = _host.Capture.RunningCameraIds.Count;
        RecText.Text = $"REC {running}/{cams.Count} · BUFFER {s.Buffer.Seconds} s";
        RecDot.Fill = running > 0 ? B("Red") : B("Mute");

        var tc = TimeSpan.FromTicks(_host.Clock.NowTicks);
        var fps = s.Format.FramesPerSecond;
        TcText.Text = $"{(int)tc.TotalHours:00}:{tc.Minutes:00}:{tc.Seconds:00}:{(int)(tc.Ticks % TimeSpan.TicksPerSecond / (TimeSpan.TicksPerSecond / fps)):00}";
        var free = _host.Storage.FreeBytes;
        DiskText.Text = free == long.MaxValue ? "—" : $"{free / (1L << 30)} GB free";
        DiskText.Foreground = free < _host.Storage.MinFreeBytes ? B("Red") : B("Text");

        var phones = _host.Remote.Clients.Count;
        RemoteText.Text = _host.Remote.IsRunning ? $"Remote · {phones} phone{(phones == 1 ? "" : "s")}" : "Remote off";
        RemoteDot.Fill = _host.Remote.IsRunning ? B("Blue") : B("Mute");

        // Preview overlays
        var selected = op.SelectedCamera;
        var selName = s.Sources.FirstOrDefault(x => x.SourceId == selected)?.Name ?? $"Cam {selected}";
        var selHealth = health.FirstOrDefault(h => h.SourceId == selected);
        PreviewTagText.Text = $"{(op.Mode == CueMode.Live ? "LIVE" : "REPLAY PREVIEW")} · {selName.ToUpperInvariant()}";
        PreviewSimChip.Visibility = selHealth?.IsSimulated == true ? Visibility.Visible : Visibility.Collapsed;
        PreviewFormat.Text = s.Format.Name;
        PreviewSpeed.Text = op.Speed >= 1 ? "100%" : $"{op.Speed:P0} SLOW";
        PreviewMode.Text = op.Mode == CueMode.Live ? "LIVE" : "JOG";
        PreviewPos.Text = op.Mode == CueMode.Live ? "LIVE" : FormatSeconds(op.PositionSeconds);
        var now = _host.Clock.NowTicks;
        MarkInText.Text = _host.Clips.MarkInTicks is { } mi ? $"IN {FormatSeconds(TimeSpan.FromTicks(mi - now).TotalSeconds)}" : "IN —";
        MarkOutText.Text = _host.Clips.MarkOutTicks is { } mo ? $"OUT {FormatSeconds(TimeSpan.FromTicks(mo - now).TotalSeconds)}" : "OUT —";
        ArmedText.Text = auto.ArmedClip?.Description is { } d ? $"ARMED {d}" : "NOTHING ARMED";
        var signal = selHealth?.Status is SourceStatus.Signal or SourceStatus.Dropping or SourceStatus.ModeMismatch;
        PreviewNoSignal.Visibility = signal || op.Mode == CueMode.Jog ? Visibility.Collapsed : Visibility.Visible;
        PreviewNoSignalText.Text = selHealth is null ? $"CAM {selected} NOT RUNNING" : $"CAM {selected} · {selHealth.Status.ToString().ToUpperInvariant()}\n{selHealth.LastError}";

        // Program & output info
        var pgmSrc = s.Sources.FirstOrDefault(x => x.Role == SourceRole.Program && x.Enabled);
        ProgramNone.Visibility = pgmSrc is null ? Visibility.Visible : Visibility.Collapsed;
        var pgmHealth = pgmSrc is null ? null : health.FirstOrDefault(h => h.SourceId == pgmSrc.SourceId);
        ProgramInfo.Text = pgmSrc is null ? "No program capture" : $"{pgmSrc.DeviceName ?? pgmSrc.Name} · {pgmHealth?.Status} · {pgmHealth?.MeasuredFps:0.00} fps";
        OutTag.Text = $"REPLAY OUT → ATEM IN {s.Atem.ReplayInput}";
        var output = _host.Output;
        OutReady.Text = output.IsSimulated ? "SIMULATED" : output.Readiness == OutputReadiness.Ready ? "OUTPUT READY" : output.Readiness.ToString().ToUpperInvariant();
        OutReady.Foreground = output.IsSimulated ? B("Violet") : output.Readiness == OutputReadiness.Ready ? B("Green") : B("Red");
        OutBadgeHost.Visibility = output.ReplayBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        if (OutBadgeHost.Visibility == Visibility.Visible && OutBadgeHost.Child is null)
        {
            var fmt = _host.Settings.Format;
            OutBadgeHost.Child = new Grid { Width = fmt.Width, Height = fmt.Height, Children = { ReplayBadgeVisual.Create(_host.Settings.Output.ReplayBadgeText, fmt.Height) } };
        }
        OutInfo.Text = (output.LastFrame is null ? "Holding frame" : $"Playing · Cam {output.LastFrame.SourceId}") + " · " + output.ReadinessMessage;

        // Timeline
        TimelineLabel.Text = $"ROLLING BUFFER · {selName.ToUpperInvariant()}";
        var held = _host.Buffers.SecondsHeld(selected);
        TimelineScale.Text = $"-{s.Buffer.Seconds} s · held {held:0} s · LIVE";
        var w = Timeline.ActualWidth;
        double X(double secondsFromLive) => Math.Clamp(w * (1 + secondsFromLive / s.Buffer.Seconds), 0, w);
        TlBuffer.Width = Math.Max(0, w - X(-held));
        Canvas.SetLeft(TlBuffer, X(-held));
        Canvas.SetLeft(TlLive, w - 4);
        Canvas.SetLeft(TlHead, X(op.PositionSeconds) - 1);
        if (_host.Clips.MarkInTicks is { } a || _host.Clips.MarkOutTicks is not null)
        {
            var left = X(TimeSpan.FromTicks((_host.Clips.MarkInTicks ?? now) - now).TotalSeconds);
            var right = X(TimeSpan.FromTicks((_host.Clips.MarkOutTicks ?? now) - now).TotalSeconds);
            TlRange.Visibility = Visibility.Visible;
            Canvas.SetLeft(TlRange, Math.Min(left, right));
            TlRange.Width = Math.Max(2, Math.Abs(right - left));
        }
        else TlRange.Visibility = Visibility.Collapsed;

        // Mode/speed/return buttons
        LiveModeBtn.Style = (Style)FindResource(op.Mode == CueMode.Live ? "BtnBlue" : "BtnBase");
        JogModeBtn.Style = (Style)FindResource(op.Mode == CueMode.Jog ? "BtnBlue" : "BtnBase");
        Speed100Btn.IsChecked = op.Speed >= 1;
        Speed50Btn.IsChecked = Math.Abs(op.Speed - 0.5) < 0.01;
        Speed25Btn.IsChecked = Math.Abs(op.Speed - 0.25) < 0.01;
        AutoReturnBtn.IsChecked = s.Automation.ReturnToLive == ReturnToLiveMode.Automatic;
        ManualReturnBtn.IsChecked = s.Automation.ReturnToLive == ReturnToLiveMode.Manual;

        // Tiles
        for (var i = 0; i < 8; i++)
        {
            var t = _tiles[i];
            var id = i + 1;
            var src = s.Sources.FirstOrDefault(x => x.SourceId == id && x.Role == SourceRole.Camera);
            var h = health.FirstOrDefault(x => x.SourceId == id);
            var on = src?.Enabled == true;
            t.Caption.Text = $"CAM {id} · {(on ? src!.Name : "off")}";
            var ok = h?.Status is SourceStatus.Signal or SourceStatus.Dropping or SourceStatus.ModeMismatch;
            t.NoSignal.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)t.NoSignal.Child).Text = !on ? "DISABLED" : h is null ? "NOT STARTED" : h.Status.ToString().ToUpperInvariant();
            (t.Health.Text, t.Health.Foreground) = h switch
            {
                null => ("● —", B("Mute")),
                { IsSimulated: true } => ($"● SIM", B("Violet")),
                { Status: SourceStatus.Signal } => ($"● {h.MeasuredFps:0.0}", B("Green")),
                { Status: SourceStatus.Dropping or SourceStatus.ModeMismatch } => ($"● {h.MeasuredFps:0.0}", B("Amber")),
                _ => ("● LOST", B("Red")),
            };
            t.Rate.Text = h is null ? "" : $"drop {h.DroppedFrames}";
            t.Rate.Foreground = h?.DroppedFrames > 0 ? B("Amber") : B("Text");
            var isPgm = atem.Connected && atem.ProgramInput == id;
            var isPvw = atem.Connected && atem.PreviewInput == id;
            t.PgmBadge.Visibility = isPgm ? Visibility.Visible : Visibility.Collapsed;
            t.Frame.BorderBrush = isPgm ? B("Red") : isPvw ? B("Green") : id == selected ? B("Blue") : B("Line2");
            t.Frame.BorderThickness = new Thickness(id == selected ? 3 : 2);
        }
        DropText.Text = "dropped " + string.Join(" · ", health.Where(h => h.SourceId > 0).Select(h => h.DroppedFrames));

        // Automation panel
        foreach (var (st, chip) in _flowChips)
        {
            var tb = (TextBlock)chip.Child;
            var active = st == state;
            chip.BorderBrush = active ? B(fg) : B("Line2");
            chip.Background = active ? B(bg) : new SolidColorBrush(Color.FromRgb(0x0D, 0x10, 0x14));
            tb.Foreground = active ? B(fg) : B("Mute");
        }
        var autoOn = s.Automation.Enabled && !auto.OverrideActive;
        AutoBadgeText.Text = auto.OverrideActive ? "OVERRIDE" : !s.Automation.Enabled ? "AUTOMATION OFF"
            : $"AUTO · {Describe(s.Automation.TransitionIn)} IN/{(s.Automation.ReturnToLive == ReturnToLiveMode.Automatic ? Describe(s.Automation.TransitionOut) : "MANUAL")} OUT";
        AutoBadgeText.Foreground = autoOn ? B("Green") : B("Amber");
        AutoBadge.Background = autoOn ? B("GreenSoft") : B("AmberSoft");
        AutoInfo.Text = auto.ErrorReason ?? op.LastAlert ?? "Mark IN/OUT (or press REPLAY for the last 8 seconds).";
        if (op.Queue.Count > 0) AutoInfo.Text += $"\nQueue: {op.Queue.Count} clip(s)";
        OverrideBtn.Style = (Style)FindResource(auto.OverrideActive ? "BtnRed" : "BtnDanger");

        // Alert banner: errors, override, ATEM loss, transient operator errors
        string? alert = null;
        if (state == ReplayState.ERROR) alert = auto.ErrorReason;
        else if (auto.OverrideActive) alert = "EMERGENCY MANUAL OVERRIDE ENGAGED. Endora is not sending any switcher commands. Press OVERRIDE again to release.";
        else if (s.Automation.Enabled && !atem.Connected) alert = "ATEM disconnected: automatic switching is stopped. Switch manually on the ATEM.";
        else if (!output.IsSimulated && output.Readiness != OutputReadiness.Ready) alert = $"Replay output not ready: {output.ReadinessMessage}";
        else if (_transientError is not null && DateTime.UtcNow < _transientUntil) alert = _transientError;
        AlertBanner.Visibility = alert is null ? Visibility.Collapsed : Visibility.Visible;
        AlertText.Text = alert ?? "";
        ClearErrorBtn.Visibility = state == ReplayState.ERROR ? Visibility.Visible : Visibility.Collapsed;

        var armedId = auto.ArmedClip?.ClipId;
        if (_clips.Any(c => c.Armed != (c.Id == armedId))) RefreshClips();
    }

    private static string Describe(TransitionMode m) => m switch
    {
        TransitionMode.EndoraStinger => "STINGER",
        TransitionMode.AtemCut => "CUT",
        _ => "AUTO",
    };

    private string InputLabel(AtemSnapshot a, int input) =>
        input == _host.Settings.Atem.ReplayInput ? $"IN {input} · REPLAY"
        : _host.Settings.Sources.FirstOrDefault(x => x.SourceId == input && x.Role == SourceRole.Camera) is { } src ? src.Name.ToUpperInvariant()
        : a.InputName(input).ToUpperInvariant();

    private static string FormatSeconds(double s)
    {
        var sign = s < 0 ? "-" : "+";
        var t = TimeSpan.FromSeconds(Math.Abs(s));
        return $"{sign}{t:mm\\:ss\\.ff}";
    }

    // ================= video rendering (off the UI thread) =================

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        long lastPreview = -1, lastProgram = -1, lastOut = -2;
        int lastPreviewSrc = -1;
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            tick++;
            try
            {
                var op = _host.Operator;
                var pf = op.PreviewFrame();
                if (pf is not null && (pf.FrameNumber != lastPreview || pf.SourceId != lastPreviewSrc))
                {
                    lastPreview = pf.FrameNumber;
                    lastPreviewSrc = pf.SourceId;
                    var img = JpegCodec.Decode(pf, 2);
                    await Dispatcher.InvokeAsync(() => Blit(ref _previewBmp, PreviewImage, img));
                }

                if (_host.Capture.ProgramSourceId is { } pid && _host.Capture.GetLatest(pid) is { } prog && prog.FrameNumber != lastProgram)
                {
                    lastProgram = prog.FrameNumber;
                    var img = JpegCodec.Decode(prog, 2);
                    await Dispatcher.InvokeAsync(() => Blit(ref _programBmp, ProgramImage, img));
                }

                var of = _host.Output.LastFrame;
                var ofKey = of is null ? -1 : of.FrameNumber * 16 + of.SourceId;
                if (ofKey != lastOut && tick % 2 == 0)
                {
                    lastOut = ofKey;
                    var img = of is not null ? JpegCodec.Decode(of, 4)
                        : (_host.Output as DisplayReplayOutput)?.HoldingImage ?? (_host.Output as SimulatedReplayOutput)?.HoldingImage;
                    if (img is not null) await Dispatcher.InvokeAsync(() => Blit(ref _outBmp, OutImage, img));
                }

                if (tick % 3 == 0)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var f = _host.Capture.GetLatest(i + 1);
                        var t = _tiles[i];
                        if (f is null || f.FrameNumber == t.LastFrame) continue;
                        t.LastFrame = f.FrameNumber;
                        var img = JpegCodec.Decode(f, 8);
                        await Dispatcher.InvokeAsync(() => Blit(ref t.Bitmap, t.Image, img));
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                _host.Diagnostics.Warn(LogCategory.App, $"Preview render: {e.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private static void Blit(ref WriteableBitmap? bmp, Image target, BgraImage img)
    {
        if (bmp is null || bmp.PixelWidth != img.Width || bmp.PixelHeight != img.Height)
        {
            bmp = new WriteableBitmap(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null);
            target.Source = bmp;
        }
        bmp.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.Pixels, img.Stride, 0);
    }

    // ================= windows =================

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_host) { Owner = this };
        w.ShowDialog();
        _keys.Load(_host.Settings.KeyBindings);
        RefreshUi();
    }

    private void OnOpenRemote(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_host, SettingsWindow.Page.Remote) { Owner = this };
        w.ShowDialog();
    }

    private void OnOpenUpdates(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_host, SettingsWindow.Page.Diagnostics) { Owner = this };
        w.ShowDialog();
    }

    private void RefreshUpdate()
    {
        UpdateBtn.Visibility = _host.Updates.Status == UpdateStatus.ReadyToInstall ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.Content = $"Update {_host.Updates.AvailableVersion} ready";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        var st = _host.Automation.State;
        if (st is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM or ReplayState.REPLAY_COMPLETE &&
            MessageBox.Show(this, "A replay is on air or pending. Close Endora anyway?", "Endora Replay", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _closing = true;
        _uiTimer.Stop();
        await _cts.CancelAsync();
        try { await _host.DisposeAsync(); }
        catch (Exception) { }
        Application.Current.Shutdown();
    }
}
