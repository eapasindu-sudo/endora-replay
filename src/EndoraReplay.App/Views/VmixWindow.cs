using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EndoraReplay.App.Services;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Vmix;
using EndoraReplay.Remote;

namespace EndoraReplay.App.Views;

/// <summary>
/// Operator screen for vMix replay: connection status, REPLAY / LIVE buttons, the automation settings, the Companion
/// and vMix set-up links, and the event log.
/// </summary>
public sealed class VmixWindow : Window
{
    private readonly VmixHost _host;
    private readonly KeyBindingService _keys;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBlock _stateText = new() { FontSize = 26, FontWeight = FontWeights.Black, Foreground = Brushes.White };
    private readonly Border _stateBadge = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _vmixChip = Chip();
    private readonly TextBlock _atemChip = Chip();
    private readonly TextBlock _info = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Border _alert = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _alertText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _holdBtn = new();
    private readonly ListBox _log = new() { MinHeight = 160 };
    private readonly TextBlock _setupText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Image _qr = new() { Width = 132, Height = 132, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _phoneText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _updateBtn = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 0, 0) };

    private readonly Dictionary<string, Func<AppSettings, string?>> _apply = [];
    private readonly Dictionary<string, Action<AppSettings>> _load = [];

    public VmixWindow(VmixHost host)
    {
        _host = host;
        _keys = new KeyBindingService(host.Settings.KeyBindings);
        Title = "Endora Replay · vMix";
        Width = 1240;
        Height = 820;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Res("Bg");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/endora.ico"));

        var root = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(BuildHeader());
        top.Children.Add(BuildButtons());
        top.Children.Add(BuildAlert());
        top.Children.Add(new Border { Style = (Style)FindResource("PanelBorder"), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 0), Child = _info });
        root.Children.Add(top);

        var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var settings = BuildSettings();
        Grid.SetColumn(settings, 0);
        body.Children.Add(settings);
        var right = BuildRight();
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        root.Children.Add(body);
        Content = root;

        LoadSettingsIntoForm(host.Settings);
        RefreshSetupText();
        foreach (var e in host.Diagnostics.Recent(200).Reverse()) AddLog(e);
        host.Diagnostics.EventLogged += e => Dispatcher.BeginInvoke(() => AddLog(e));
        host.Updates.Changed += () => Dispatcher.BeginInvoke(RefreshUpdate);
        _ = host.Updates.RunBackgroundAsync(_cts.Token);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        PreviewKeyDown += OnKey;
        Closed += async (_, _) =>
        {
            _timer.Stop();
            await _cts.CancelAsync();
        };
        Refresh();
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildHeader()
    {
        var row = new DockPanel { LastChildFill = false };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/endora-mark.png")), Height = 40, Margin = new Thickness(0, 0, 12, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        row.Children.Add(logo);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "ENDORA REPLAY", FontSize = 18, FontWeight = FontWeights.Black });
        title.Children.Add(new TextBlock { Text = "vMix replay · ATEM automation with stinger", Foreground = Res("Dim"), FontSize = 12 });
        row.Children.Add(title);
        _stateBadge.Child = _stateText;
        row.Children.Add(_stateBadge);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(chips, Dock.Right);
        chips.Children.Add(Pill(_vmixChip));
        chips.Children.Add(Pill(_atemChip));
        _updateBtn.Click += (_, _) => _host.Updates.ApplyAndRestart();
        chips.Children.Add(_updateBtn);
        row.Children.Add(chips);
        return row;
    }

    private UIElement BuildButtons()
    {
        var g = new UniformGrid { Rows = 1, Margin = new Thickness(0, 16, 0, 0) };
        g.Children.Add(ActionButton("REPLAY", "vMix stinger in + ATEM", OperatorActions.Replay, "BtnRed", 28));
        g.Children.Add(ActionButton("LIVE", "stinger back to camera", OperatorActions.Live, "BtnGreen", 28));
        g.Children.Add(ActionButton("CUT TO LIVE", "no stinger, now", OperatorActions.Stop, "BtnAmber", 18));
        var hold = ActionButton("HOLD", "stay on the replay", OperatorActions.CancelAuto, "BtnBlue", 18);
        g.Children.Add(hold);
        g.Children.Add(ActionButton("SCOREBOARD", "on / off air", OperatorActions.Scoreboard, null, 16));
        g.Children.Add(ActionButton("OVERRIDE", "Endora sends nothing", OperatorActions.EmergencyOverride, "BtnDanger", 16));
        return g;
    }

    private Button ActionButton(string text, string sub, string action, string? style, double size)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.Black, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 11, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center });
        if (_host.Settings.KeyBindings.TryGetValue(action, out var key) && !string.IsNullOrWhiteSpace(key))
        {
            stack.Children.Add(new Border
            {
                Style = (Style)FindResource("KeyHint"),
                Child = new TextBlock { Text = KeyBindingService.Pretty(key), Style = (Style)FindResource("KeyHintText") },
            });
        }
        var b = action == OperatorActions.CancelAuto ? _holdBtn : new Button();
        b.Content = stack;
        b.Height = 104;
        b.Margin = new Thickness(0, 0, 10, 0);
        if (style is not null) b.Style = (Style)FindResource(style);
        b.Click += (_, _) => Run(action, "screen");
        return b;
    }

    private UIElement BuildAlert()
    {
        _alert.Background = Res("RedSoft");
        _alert.BorderBrush = Res("Red");
        _alert.BorderThickness = new Thickness(1);
        var d = new DockPanel();
        var clear = new Button { Content = "Clear", MinHeight = 28, Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += (_, _) => Run(OperatorActions.ClearError, "screen");
        DockPanel.SetDock(clear, Dock.Right);
        d.Children.Add(clear);
        d.Children.Add(_alertText);
        _alert.Child = d;
        return _alert;
    }

    private UIElement BuildRight()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var setup = new StackPanel();
        setup.Children.Add(new TextBlock { Text = "PHONE REMOTE", Style = (Style)FindResource("H2") });
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        var phone = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        phone.Children.Add(_qr);
        phone.Children.Add(_phoneText);
        setup.Children.Add(phone);
        setup.Children.Add(new TextBlock { Text = "CONNECT COMPANION / STREAM DECK / vMix", Style = (Style)FindResource("H2") });
        setup.Children.Add(_setupText);
        var copyRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        copyRow.Children.Add(CopyButton("Copy REPLAY link", () => CommandUrl(OperatorActions.Replay, forVmix: false)));
        copyRow.Children.Add(CopyButton("Copy LIVE link", () => CommandUrl(OperatorActions.Live, forVmix: false)));
        copyRow.Children.Add(CopyButton("Copy vMix script: REPLAY", () => VmixScript(OperatorActions.Replay)));
        copyRow.Children.Add(CopyButton("Copy vMix script: LIVE", () => VmixScript(OperatorActions.Live)));
        setup.Children.Add(copyRow);
        var setupCard = new Border { Style = (Style)FindResource("Card"), Child = setup };
        g.Children.Add(setupCard);

        var logCard = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 12, 0, 0) };
        var logPanel = new DockPanel();
        var logTitle = new TextBlock { Text = "EVENT LOG", Style = (Style)FindResource("H2") };
        DockPanel.SetDock(logTitle, Dock.Top);
        logPanel.Children.Add(logTitle);
        _log.Background = Brushes.Transparent;
        _log.BorderThickness = new Thickness(0);
        _log.Foreground = Res("Text");
        _log.FontFamily = (FontFamily)FindResource("MonoFont");
        _log.FontSize = 11;
        logPanel.Children.Add(_log);
        logCard.Child = logPanel;
        Grid.SetRow(logCard, 1);
        g.Children.Add(logCard);
        return g;
    }

    private Button CopyButton(string text, Func<string> content)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), MinHeight = 30, FontSize = 12 };
        b.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(content());
                _host.Diagnostics.Info(LogCategory.App, $"{text}: copied.");
            }
            catch (Exception e) { _host.Diagnostics.Warn(LogCategory.App, $"Copy failed: {e.Message}"); }
        };
        return b;
    }

    // ------------------------------------------------------------------ settings form

    private UIElement BuildSettings()
    {
        var form = new StackPanel();
        Section(form, "vMix");
        TextRow(form, "vmix.host", "vMix PC address", "127.0.0.1 when vMix runs on this PC", s => s.Vmix.Host, (s, v) => s.Vmix.Host = v);
        IntRow(form, "vmix.port", "vMix TCP port", "8099", s => s.Vmix.Port, (s, v) => s.Vmix.Port = v);
        TextRow(form, "vmix.replay", "Replay input in vMix", "number or title; empty = the first Replay input", s => s.Vmix.ReplayInput, (s, v) => s.Vmix.ReplayInput = v);
        TextRow(form, "vmix.black", "Black input in vMix", "number or title; empty = the first Colour input", s => s.Vmix.BlackInput, (s, v) => s.Vmix.BlackInput = v);

        Section(form, "Stinger");
        ComboRow(form, "vmix.mode", "How the replay goes on air",
            [
                ("Keyed: full stinger over live (ATEM DSK 1)", VmixStingerMode.Keyed),
                ("Cut at the stinger's transition point", VmixStingerMode.CoverCut),
                ("No vMix stinger: ATEM AUTO / CUT", VmixStingerMode.AtemOnly),
            ],
            s => s.Vmix.Mode, (s, v) => s.Vmix.Mode = v);
        TextRow(form, "vmix.tin", "vMix transition in", "Stinger1, Stinger2, Fade…", s => s.Vmix.TransitionIn, (s, v) => s.Vmix.TransitionIn = v);
        TextRow(form, "vmix.tout", "vMix transition out", "Stinger1, Stinger2, Fade…", s => s.Vmix.TransitionOut, (s, v) => s.Vmix.TransitionOut = v);
        IntRow(form, "vmix.cut", "ATEM cut after (ms)", "= the stinger's transition point in vMix", s => s.Vmix.CutPointMs, (s, v) => s.Vmix.CutPointMs = v);
        IntRow(form, "vmix.len", "Stinger length (ms)", "whole stinger video", s => s.Vmix.StingerLengthMs, (s, v) => s.Vmix.StingerLengthMs = v);
        IntRow(form, "vmix.lead", "Black lead (ms)", "black on vMix before the key goes on", s => s.Vmix.BlackLeadMs, (s, v) => s.Vmix.BlackLeadMs = v);
        ComboRow(form, "vmix.atemstyle", "ATEM style (no-stinger mode)",
            [("Mix", AtemTransitionStyle.Mix), ("Dip", AtemTransitionStyle.Dip), ("Wipe", AtemTransitionStyle.Wipe), ("DVE", AtemTransitionStyle.Dve)],
            s => s.Vmix.AtemOnlyStyle, (s, v) => s.Vmix.AtemOnlyStyle = v);
        CheckRow(form, "vmix.atemcut", "Use CUT instead (no-stinger mode)", s => s.Vmix.AtemOnlyCut, (s, v) => s.Vmix.AtemOnlyCut = v);

        Section(form, "Automation");
        CheckRow(form, "vmix.autostart", "Start when the vMix replay starts playing", s => s.Vmix.StartWhenReplayPlays, (s, v) => s.Vmix.StartWhenReplayPlays = v);
        CheckRow(form, "vmix.autoreturn", "Back to live when the vMix replay stops", s => s.Vmix.AutoReturnWhenReplayStops, (s, v) => s.Vmix.AutoReturnWhenReplayStops = v);
        IntRow(form, "vmix.grace", "Stopped for at least (ms)", "so a short pause does not end the replay", s => s.Vmix.StopGraceMs, (s, v) => s.Vmix.StopGraceMs = v);
        CheckRow(form, "vmix.follow", "Follow vMix when its operator switches the replay", s => s.Vmix.FollowVmixProgram, (s, v) => s.Vmix.FollowVmixProgram = v);
        LinesRow(form, "vmix.fnreplay", "Extra vMix functions on REPLAY", "one per line, e.g. ReplayPlay", s => s.Vmix.FunctionsOnReplay, (s, v) => s.Vmix.FunctionsOnReplay = v);
        LinesRow(form, "vmix.fnlive", "Extra vMix functions on LIVE", "one per line", s => s.Vmix.FunctionsOnLive, (s, v) => s.Vmix.FunctionsOnLive = v);

        Section(form, "ATEM");
        ComboRow(form, "atem.backend", "Connection",
            [("Network (ATEM Mini Extreme)", AtemBackendKind.NetworkProtocol), ("Blackmagic SDK", AtemBackendKind.BlackmagicSdk), ("SIMULATED (rehearsal)", AtemBackendKind.Simulated)],
            s => s.Atem.Backend, (s, v) => s.Atem.Backend = v);
        TextRow(form, "atem.addr", "ATEM address", "e.g. 192.168.10.240", s => s.Atem.Address, (s, v) => s.Atem.Address = v);
        IntRow(form, "atem.in", "ATEM input from vMix", "the HDMI input vMix's output is wired to", s => s.Atem.ReplayInput, (s, v) => s.Atem.ReplayInput = v);

        Section(form, "Scoreboard (ATEM key)");
        CheckRow(form, "sb.on", "Scoreboard on", s => s.Scoreboard.Enabled, (s, v) => s.Scoreboard.Enabled = v);
        TextRow(form, "sb.url", "Scoreboard link", "overlay page address", s => s.Scoreboard.Url ?? "", (s, v) => s.Scoreboard.Url = v.Length == 0 ? null : v);
        TextRow(form, "sb.display", "Scoreboard display", @"e.g. \\.\DISPLAY3", s => s.Scoreboard.DisplayDevice ?? "", (s, v) => s.Scoreboard.DisplayDevice = v.Length == 0 ? null : v);
        IntRow(form, "sb.in", "Scoreboard ATEM input", "", s => s.Scoreboard.AtemInput, (s, v) => s.Scoreboard.AtemInput = v);
        ComboRow(form, "sb.keyer", "Scoreboard keyer", [("DSK 2", ScoreboardKeyer.Dsk2), ("Upstream key 1", ScoreboardKeyer.UpstreamKey1)],
            s => s.Scoreboard.Keyer, (s, v) => s.Scoreboard.Keyer = v);
        CheckRow(form, "sb.hide", "Hide the scoreboard during replays", s => s.Scoreboard.HideDuringReplay, (s, v) => s.Scoreboard.HideDuringReplay = v);

        Section(form, "Remote, Companion, MIDI");
        CheckRow(form, "remote.on", "Remote links on (phone, Companion, vMix scripts)", s => s.Remote.Enabled, (s, v) => s.Remote.Enabled = v);
        IntRow(form, "remote.port", "Remote port", "", s => s.Remote.Port, (s, v) => s.Remote.Port = v);
        TextRow(form, "companion", "Companion address (optional)", "e.g. http://127.0.0.1:8000 for live button text", s => s.Vmix.CompanionUrl, (s, v) => s.Vmix.CompanionUrl = v);
        CheckRow(form, "midi.on", "MIDI controller on", s => s.Midi.Enabled, (s, v) => s.Midi.Enabled = v);

        Section(form, "Replay engine");
        ComboRow(form, "engine", "Replays are made by",
            [("vMix (this screen)", ReplayEngine.Vmix), ("Endora (records the cameras itself)", ReplayEngine.Endora)],
            s => s.Engine, (s, v) => s.Engine = v);

        var save = new Button { Content = "Save and apply", Style = (Style)FindResource("BtnBlue"), Margin = new Thickness(0, 16, 0, 4), MinHeight = 40 };
        save.Click += async (_, _) => await SaveAsync();
        form.Children.Add(save);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = form },
        };
    }

    private static void Section(Panel form, string title) =>
        form.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, form.Children.Count == 0 ? 0 : 18, 0, 6), Opacity = 0.7 });

    private Grid Row(Panel form, string label, string hint, UIElement editor)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        l.Children.Add(new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (hint.Length > 0) l.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = Res("Dim"), TextWrapping = TextWrapping.Wrap });
        g.Children.Add(l);
        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        form.Children.Add(g);
        return g;
    }

    private void TextRow(Panel form, string id, string label, string hint, Func<AppSettings, string> get, Action<AppSettings, string> set)
    {
        var box = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        Row(form, label, hint, box);
        _load[id] = s => box.Text = get(s);
        _apply[id] = s =>
        {
            set(s, box.Text.Trim());
            return null;
        };
    }

    private void IntRow(Panel form, string id, string label, string hint, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        var box = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        Row(form, label, hint, box);
        _load[id] = s => box.Text = get(s).ToString(CultureInfo.InvariantCulture);
        _apply[id] = s =>
        {
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return $"{label}: enter a whole number.";
            set(s, v);
            return null;
        };
    }

    private void CheckRow(Panel form, string id, string label, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var box = new CheckBox { Content = label, Foreground = Res("Text"), Margin = new Thickness(0, 6, 0, 6) };
        if (TryFindResource("Switch") is Style sw) box.Style = sw;
        form.Children.Add(box);
        _load[id] = s => box.IsChecked = get(s);
        _apply[id] = s =>
        {
            set(s, box.IsChecked == true);
            return null;
        };
    }

    private void LinesRow(Panel form, string id, string label, string hint, Func<AppSettings, List<string>> get, Action<AppSettings, List<string>> set)
    {
        var box = new TextBox { AcceptsReturn = true, MinHeight = 56, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Row(form, label, hint, box);
        _load[id] = s => box.Text = string.Join(Environment.NewLine, get(s));
        _apply[id] = s =>
        {
            var lines = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            foreach (var l in lines)
            {
                var (fn, _) = VmixProtocol.SplitFunction(l);
                if (fn.Any(c => !char.IsLetterOrDigit(c))) return $"{label}: '{fn}' is not a vMix function name.";
            }
            set(s, lines);
            return null;
        };
    }

    private void ComboRow<T>(Panel form, string id, string label, IReadOnlyList<(string Text, T Value)> items, Func<AppSettings, T> get, Action<AppSettings, T> set)
        where T : struct, Enum
    {
        var combo = new ComboBox { MinHeight = 32 };
        foreach (var (text, _) in items) combo.Items.Add(text);
        Row(form, label, "", combo);
        _load[id] = s =>
        {
            var v = get(s);
            var i = items.ToList().FindIndex(x => EqualityComparer<T>.Default.Equals(x.Value, v));
            combo.SelectedIndex = Math.Max(0, i);
        };
        _apply[id] = s =>
        {
            if (combo.SelectedIndex >= 0) set(s, items[combo.SelectedIndex].Value);
            return null;
        };
    }

    private void LoadSettingsIntoForm(AppSettings s)
    {
        foreach (var load in _load.Values) load(s);
    }

    private async Task SaveAsync()
    {
        var updated = _host.CloneSettings();
        var errors = _apply.Values.Select(a => a(updated)).Where(e => e is not null).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var problems = updated.Vmix.Validate();
        if (problems.Count > 0
            && MessageBox.Show(this, "Saved, but please check:\n\n• " + string.Join("\n• ", problems) + "\n\nSave anyway?", "Endora Replay",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var restart = await Task.Run(() => _host.ApplyAsync(updated));
            _keys.Load(_host.Settings.KeyBindings);
            RefreshSetupText();
            if (restart)
                MessageBox.Show(this, "Restart Endora Replay to change the replay engine.", "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, $"Settings could not be saved: {e.Message}", "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ links for Companion and vMix

    private string CommandUrl(string action, bool forVmix)
    {
        var r = _host.Settings.Remote;
        var vmixOnThisPc = _host.Settings.Vmix.Host is "127.0.0.1" or "localhost";
        var address = forVmix && vmixOnThisPc ? "127.0.0.1" : RemoteServer.LanAddress();
        return $"http://{address}:{r.Port}/api/cmd/{action}?code={Uri.EscapeDataString(r.AccessCode)}";
    }

    /// <summary>VB.NET script for vMix (Settings → Scripting → Add), run from a vMix shortcut with ScriptStart.</summary>
    private string VmixScript(string action) =>
        $"' Endora Replay: {action.ToUpperInvariant()} (paste into vMix → Settings → Scripting → Add, name it Endora{action})\r\n" +
        "Try\r\n" +
        "    Dim web As New System.Net.WebClient()\r\n" +
        $"    web.DownloadString(\"{CommandUrl(action, forVmix: true)}\")\r\n" +
        "Catch ex As Exception\r\n" +
        "    Console.WriteLine(\"Endora: \" & ex.Message)\r\n" +
        "End Try\r\n";

    private void RefreshSetupText()
    {
        RefreshPhone();
        var r = _host.Settings.Remote;
        _setupText.Text = !r.Enabled
            ? "Remote links are off. Turn them on in Settings → Remote to use Companion, a Stream Deck or vMix shortcuts."
            : "Companion (Generic HTTP module): one button per action, request GET\n" +
              $"   {CommandUrl(OperatorActions.Replay, false)}\n" +
              "   Live, Stop (cut to live), CancelAuto (hold), Scoreboard and EmergencyOverride work the same way.\n\n" +
              "vMix shortcut: copy a script below, add it in vMix (Settings → Scripting → Add, name EndoraReplay), " +
              "then Settings → Shortcuts → Add: your key, function ScriptStart, value EndoraReplay.\n\n" +
              "Nothing is needed for normal use: when the vMix replay starts playing, Endora runs the stinger and the ATEM by itself.";
    }

    /// <summary>QR code and link for the phone remote (vMix replay controls + REPLAY / LIVE).</summary>
    private void RefreshPhone()
    {
        var url = _host.Remote.Url;
        if (url is null)
        {
            _qr.Source = null;
            _qr.Visibility = Visibility.Collapsed;
            _phoneText.Text = _host.Settings.Remote.Enabled
                ? "The phone remote is starting… (if it stays like this, the port may be in use: change it in Settings → Remote)."
                : "Phone remote is off. Turn on Remote links in Settings.";
            return;
        }
        try
        {
            var png = _host.Remote.QrPng(6);
            if (png is not null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                _qr.Source = bmp;
                _qr.Visibility = Visibility.Visible;
            }
        }
        catch (Exception e) { _host.Diagnostics.Warn(LogCategory.Remote, $"QR code: {e.Message}"); }
        _phoneText.Text = "Scan with the phone camera (same Wi-Fi as this PC):\n" + url +
            "\n\nPLAY runs the stinger and the ATEM and starts vMix playing at the cut. Mark in/out, last 5-15 s, cameras, " +
            "jog, speed, events, LIVE and STOP are all on the phone.";
    }

    // ------------------------------------------------------------------ live state

    private void Refresh()
    {
        var st = _host.Bridge.Status;
        var vs = _host.Vmix.Snapshot;
        var a = _host.Atem.Snapshot;
        _stateText.Text = st.StateText;
        _stateBadge.Background = st.State switch
        {
            BridgeState.OnAir => Res("Red"),
            BridgeState.GoingToReplay or BridgeState.Returning => Res("Amber"),
            BridgeState.Override => Res("DangerStripes"),
            _ => Res("Panel2"),
        };
        SetChip(_vmixChip, "vMix", st.VmixConnected, st.VmixConnected ? $"{vs.Version} · {_host.Vmix.Address}" : $"not connected · {_host.Vmix.Address}");
        SetChip(_atemChip, "ATEM", st.AtemConnected,
            st.AtemConnected ? $"{a.ModelName}{(_host.Atem.IsSimulated ? " (SIMULATED)" : "")}" : $"not connected · {_host.Settings.Atem.Address}");

        var v = _host.Settings.Vmix;
        _info.Text =
            $"vMix replay input: {vs.InputLabel(st.ReplayInput)}{(st.ReplayPlaying ? "  ▶ playing" : "")}     " +
            $"black input: {vs.InputLabel(st.BlackInput)}     vMix Program: {string.Join(", ", st.VmixProgram.Select(n => vs.InputLabel(n)))}\n" +
            $"ATEM Program: {(a.ProgramInput is { } p ? a.InputName(p) : "—")}     vMix on ATEM input {_host.Settings.Atem.ReplayInput}     " +
            $"back to: {(st.ReturnCamera is { } c ? a.InputName(c) : "camera on air")}     " +
            $"mode: {v.Mode}{(v.StartWhenReplayPlays ? " · starts on vMix play" : "")}{(v.AutoReturnWhenReplayStops ? " · returns when it stops" : "")}" +
            (st.AutoReturnHeld ? "     HELD ON AIR" : "");
        _holdBtn.Opacity = st.AutoReturnHeld ? 1 : 0.85;
        _alert.Visibility = st.LastAlert is null ? Visibility.Collapsed : Visibility.Visible;
        _alertText.Text = st.LastAlert ?? "";
    }

    private void RefreshUpdate()
    {
        _updateBtn.Visibility = _host.Updates.Status == UpdateStatus.ReadyToInstall ? Visibility.Visible : Visibility.Collapsed;
        _updateBtn.Content = $"Update {_host.Updates.AvailableVersion} ready";
    }

    private void SetChip(TextBlock chip, string name, bool ok, string detail)
    {
        chip.Inlines.Clear();
        chip.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = ok ? Res("Green") : Res("Red") });
        chip.Inlines.Add(new System.Windows.Documents.Run(name + "  ") { FontWeight = FontWeights.Bold });
        chip.Inlines.Add(new System.Windows.Documents.Run(detail) { Foreground = Res("Dim") });
    }

    private void AddLog(LogEvent e)
    {
        var item = new ListBoxItem
        {
            Content = $"{e.Utc.ToLocalTime():HH:mm:ss}  {e.Category,-6} {e.Message}",
            Foreground = e.Severity switch
            {
                LogSeverity.Error or LogSeverity.Critical => Res("Red"),
                LogSeverity.Warning => Res("Amber"),
                _ => Res("Text"),
            },
        };
        _log.Items.Insert(0, item);
        while (_log.Items.Count > 300) _log.Items.RemoveAt(_log.Items.Count - 1);
    }

    // ------------------------------------------------------------------ input

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_keys.ActionFor(key, Keyboard.Modifiers) is not { } action) return;
        e.Handled = true;
        Run(action, "keyboard");
    }

    private void Run(string action, string origin)
    {
        if (_host.Execute(action, origin) is { } err)
            _host.Diagnostics.Warn(LogCategory.App, $"{action}: {err}");
        Refresh();
    }

    // ------------------------------------------------------------------ helpers

    private Brush Res(string key) => (Brush)FindResource(key);

    private static TextBlock Chip() => new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private Border Pill(UIElement child) => new() { Style = (Style)FindResource("Pill"), Child = child };
}
