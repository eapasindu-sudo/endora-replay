using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EndoraReplay.App.Output;
using EndoraReplay.App.Services;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Hardware;
using Microsoft.Win32;

namespace EndoraReplay.App.Views;

/// <summary>
/// Settings editor. Edits a copy of the settings; nothing changes until Apply. Pages are built in code so
/// every field binds straight onto the copy.
/// </summary>
public partial class SettingsWindow : Window
{
    public enum Page { Inputs, Atem, Output, Shortcuts, Midi, Remote, Diagnostics }

    private static readonly (Page Page, string Title, string Sub)[] Pages =
    [
        (Page.Inputs, "Capture inputs", "Cameras, Program feed, buffer"),
        (Page.Atem, "ATEM & automation", "Replay input, transitions, stinger"),
        (Page.Output, "Output & storage", "HDMI out to ATEM, holding frame"),
        (Page.Shortcuts, "Keyboard shortcuts", "Rebind every operator key"),
        (Page.Midi, "MIDI controller", "Buttons, jog wheel, T-bar"),
        (Page.Remote, "Phone remote", "QR code, access, connected phones"),
        (Page.Diagnostics, "Diagnostics & updates", "Health, support bundle, version"),
    ];

    private static readonly string[] Formats = ["1080p50", "1080p25", "1080p59.94", "1080p60", "1080p30", "1080p29.97", "720p50", "720p59.94"];

    private readonly AppHost _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppSettings _edit;
    private Page _page;
    private bool _dirty;
    private string? _capturingAction;
    private Action? _refreshPage;
    private List<CaptureDeviceInfo> _devices = [];
    private readonly List<ICaptureBackend> _backends = HardwareFactory.CaptureBackends().ToList();

    public SettingsWindow(AppHost host, Page page = Page.Inputs)
    {
        InitializeComponent();
        _host = host;
        _edit = host.CloneSettings();
        VersionText.Text = "Endora Replay " + UpdateService.CurrentVersionText;
        foreach (var p in Pages)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            sp.Children.Add(new TextBlock { Text = p.Sub, Foreground = B("Dim"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
            Nav.Items.Add(new ListBoxItem { Content = sp, Tag = p.Page });
        }
        _timer.Tick += (_, _) => _refreshPage?.Invoke();
        _timer.Start();
        Nav.SelectedIndex = Array.FindIndex(Pages, x => x.Page == page);
        _ = DiscoverDevicesAsync();
        UpdateFooter();
    }

    // ================= navigation and footer =================

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: Page p }) return;
        if (p != _page)
        {
            // Rebuilding the same page keeps a key or MIDI capture going.
            _capturingAction = null;
            _keyLearnAll = null;
        }
        _page = p;
        _refreshPage = null;
        if (p != Page.Midi) StopMidiLearn();
        PageHost.Content = p switch
        {
            Page.Inputs => BuildInputs(),
            Page.Atem => BuildAtem(),
            Page.Output => BuildOutput(),
            Page.Shortcuts => BuildShortcuts(),
            Page.Midi => BuildMidi(),
            Page.Remote => BuildRemote(),
            _ => BuildDiagnostics(),
        };
        _refreshPage?.Invoke();
    }

    private void Rebuild() => OnNavChanged(Nav, null!);

    private void Changed()
    {
        _dirty = true;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var problems = _edit.Validate().ToList();
        problems.AddRange(KeyBindingDefaults.FindConflicts(_edit.KeyBindings).Select(c => $"Key {c.Gesture} is used by {string.Join(", ", c.Actions)}."));
        if (problems.Count > 0)
        {
            FooterText.Text = "⚠ " + string.Join("  ", problems);
            FooterText.Foreground = B("Amber");
        }
        else if (_host.RestartRequired)
        {
            FooterText.Text = "Restart Endora to apply the output, format or data-folder change.";
            FooterText.Foreground = B("Amber");
        }
        else
        {
            FooterText.Text = _dirty ? "Unsaved changes." : "No changes.";
            FooterText.Foreground = B("Dim");
        }
    }

    private async Task<bool> ApplyAsync()
    {
        var problems = _edit.Validate();
        var conflicts = KeyBindingDefaults.FindConflicts(_edit.KeyBindings);
        if (problems.Count > 0 || conflicts.Count > 0)
        {
            MessageBox.Show(this, "Fix these before applying:\n\n• " + string.Join("\n• ",
                    problems.Concat(conflicts.Select(c => $"Key {c.Gesture} is used by {string.Join(", ", c.Actions)}."))),
                "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_edit.Automation.Enabled && !_host.Settings.Automation.Enabled &&
            MessageBox.Show(this, $"Automation will let Endora switch the ATEM to input {_edit.Atem.ReplayInput} and back to live.\n\nTurn automation on?",
                "Endora Replay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        try
        {
            await _host.ApplyAsync(_edit);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Settings could not be applied: " + e.Message, "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _edit = _host.CloneSettings();
        _dirty = false;
        UpdateFooter();
        Rebuild();
        return true;
    }

    private async void OnApply(object sender, RoutedEventArgs e) => await ApplyAsync();

    private async void OnSaveClose(object sender, RoutedEventArgs e)
    {
        if (!_dirty || await ApplyAsync()) { _dirty = false; Close(); }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _dirty = false;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_dirty && MessageBox.Show(this, "Discard unsaved changes?", "Endora Replay", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _timer.Stop();
    }

    // ================= capture inputs =================

    private async Task DiscoverDevicesAsync()
    {
        var found = new List<CaptureDeviceInfo>();
        foreach (var b in _backends.Where(b => b.UnavailableReason is null))
        {
            try { found.AddRange(await b.DiscoverAsync(CancellationToken.None)); }
            catch (Exception e) { _host.Diagnostics.Warn(Core.Services.LogCategory.Capture, $"{b.Kind} discovery failed: {e.Message}"); }
        }
        _devices = found;
        if (_page == Page.Inputs) Rebuild();
    }

    private FrameworkElement BuildInputs()
    {
        var root = PageRoot("Capture inputs", "Up to 8 USB capture cameras plus the Program (final out) feed shown in its own monitor. Each camera is recorded into its own rolling buffer.");

        var general = Card(root, "Project format and buffer");
        var fmt = Combo(Formats, _edit.VideoFormat, v => { _edit.VideoFormat = v; Changed(); });
        Field(general, "Video format", fmt, "Every input and the replay output must run at this format. Changing it needs a restart.");
        Field(general, "Buffer length (seconds)", IntBox(_edit.Buffer.Seconds, v => { _edit.Buffer.Seconds = v; Changed(); }), "Per camera. Older frames are overwritten.");
        Field(general, "Recording quality", Combo(new[] { ("Draft (70)", 70), ("Broadcast (90)", 90), ("Maximum (97)", 97) }, _edit.Buffer.JpegQuality,
            v => { _edit.Buffer.JpegQuality = v; Changed(); }), "Higher quality uses more disk and CPU.");

        var card = Card(root, "Sources");
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var rescan = new Button { Content = "Rescan devices", Style = S("BtnBase"), Padding = new Thickness(12, 6, 12, 6) };
        rescan.Click += async (_, _) => { rescan.IsEnabled = false; await DiscoverDevicesAsync(); };
        DockPanel.SetDock(rescan, Dock.Right);
        head.Children.Add(rescan);
        head.Children.Add(new TextBlock
        {
            Text = _devices.Count == 0 ? "No capture devices found yet. Connect the USB capture cards and press Rescan."
                : $"{_devices.Count} device(s) found. Devices marked SIMULATED are test patterns, not cameras.",
            Foreground = B("Dim"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        card.Children.Add(head);
        foreach (var b in _backends.Where(b => b.UnavailableReason is not null))
            card.Children.Add(Note($"{b.Kind}: {b.UnavailableReason}", "Amber"));

        var grid = new Grid();
        foreach (var w in new[] { 70.0, 54, 170, double.NaN, 110, 220 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        AddRow(grid, new[] { "SOURCE", "ON", "NAME", "DEVICE", "", "CHECK" }.Select(h => (UIElement)new TextBlock { Text = h, Style = S("Label") }).ToArray());

        foreach (var src in _edit.Sources.OrderBy(s => s.Role).ThenBy(s => s.SourceId))
        {
            var label = new TextBlock
            {
                Text = src.Role == SourceRole.Program ? "PGM" : $"CAM {src.SourceId}",
                FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = src.Role == SourceRole.Program ? B("Red") : B("Text"),
            };
            var on = new CheckBox { Style = S("Switch"), IsChecked = src.Enabled, VerticalAlignment = VerticalAlignment.Center };
            on.Click += (_, _) => { src.Enabled = on.IsChecked == true; Changed(); };
            var name = TextBox(src.Name, v => { src.Name = v; Changed(); });

            var options = new List<object> { "(none)" };
            options.AddRange(_devices);
            var current = _devices.FirstOrDefault(d => d.DeviceId == src.DeviceId && d.Backend == src.Backend);
            var device = new ComboBox { ItemsSource = options, Margin = new Thickness(0, 0, 8, 0) };
            device.SelectedItem = current ?? (object?)(src.DeviceId is null ? "(none)" : null);
            if (current is null && src.DeviceId is not null)
            {
                // Configured device is not connected right now. Keep it, and say so rather than pick another.
                var missing = $"{src.DeviceName ?? src.DeviceId} — NOT FOUND";
                options.Insert(1, missing);
                device.ItemsSource = options;
                device.SelectedItem = missing;
            }
            var check = new TextBlock { Foreground = B("Dim"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            var test = new Button { Content = "Test", Style = S("BtnBase"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
            device.SelectionChanged += (_, _) =>
            {
                if (device.SelectedItem is CaptureDeviceInfo d) { src.DeviceId = d.DeviceId; src.DeviceName = d.Name; src.Backend = d.Backend; }
                else if (device.SelectedItem is "(none)") { src.DeviceId = null; src.DeviceName = null; }
                check.Text = "";
                Changed();
            };
            test.Click += async (_, _) =>
            {
                if (device.SelectedItem is not CaptureDeviceInfo d) { check.Text = "Select a device first."; return; }
                var backend = _backends.FirstOrDefault(b => b.Kind == d.Backend);
                if (backend is null) return;
                check.Text = "Checking…";
                check.Foreground = B("Dim");
                try
                {
                    var r = await backend.ValidateAsync(d, Core.Model.VideoFormat.Parse(_edit.VideoFormat), CancellationToken.None);
                    check.Text = (d.IsSimulated ? "SIMULATED · " : "") + r.Message;
                    check.Foreground = r.IsValid ? B("Green") : B("Amber");
                }
                catch (Exception ex)
                {
                    check.Text = ex.Message;
                    check.Foreground = B("Red");
                }
            };
            var health = _host.Diagnostics.SourceHealthSnapshot().FirstOrDefault(h => h.SourceId == src.SourceId);
            if (health is not null) check.Text = $"Now: {health.Status}{(health.MeasuredFps > 0 ? $" · {health.MeasuredFps:0.0} fps" : "")}";
            AddRow(grid, label, on, name, device, test, check);
        }
        card.Children.Add(grid);
        card.Children.Add(Note("Test briefly opens the device. If the device is already recording, the test may report it busy; that does not interrupt the recording.", "Dim"));
        return root;
    }

    // ================= ATEM & automation =================

    private FrameworkElement BuildAtem()
    {
        var root = PageRoot("ATEM & automation", "Endora controls the ATEM over the network. Video from Endora reaches the ATEM over HDMI (see Output).");
        var a = _edit.Atem;
        var snap = _host.Atem.Snapshot;

        var conn = Card(root, "Switcher connection");
        Field(conn, "Control method", Combo(new[]
        {
            ("Network protocol (LibAtem)", AtemBackendKind.NetworkProtocol),
            ("Blackmagic ATEM SDK", AtemBackendKind.BlackmagicSdk),
            ("SIMULATED switcher (rehearsal only)", AtemBackendKind.Simulated),
        }, a.Backend, v => { a.Backend = v; Changed(); Rebuild(); }),
            HardwareFactory.SwitcherUnavailableReason(a.Backend) ?? (a.Backend == AtemBackendKind.Simulated ? "SIMULATED: no hardware is controlled." : "LibAtem is an open-source implementation, not the official Blackmagic SDK."));
        Field(conn, "ATEM IP address", TextBox(a.Address, v => { a.Address = v.Trim(); Changed(); }), "Same as in ATEM Setup → Network.");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Field(conn, "Status", status, null);

        var replay = Card(root, "Replay input");
        var inputs = Enumerable.Range(1, 8).Select(i => ($"Input {i} · {snap.InputName(i)}", i)).ToList();
        if (a.ReplayInput > 8) inputs.Add(($"Input {a.ReplayInput}", a.ReplayInput));
        Field(replay, "ATEM input carrying Endora", Combo(inputs, a.ReplayInput, v => { a.ReplayInput = v; Changed(); }),
            "Connect Endora's HDMI output to this ATEM input. Automation, tally and the phone remote use this input. Names come from the ATEM when connected.");

        var au = Card(root, "Automation");
        var m = _edit.Automation;
        Toggle(au, "Enable automation", m.Enabled, v => { m.Enabled = v; Changed(); },
            "Off: Endora never switches the ATEM except when you press LIVE. On: REPLAY switches to the replay input and back.");
        Toggle(au, "REPLAY switches the ATEM to the replay input", m.AutoSwitchIn, v => { m.AutoSwitchIn = v; Changed(); }, null);
        Toggle(au, "Start playback when Program cuts to the replay input", m.StartOnProgramEdge, v => { m.StartOnProgramEdge = v; Changed(); },
            "Lets the ATEM operator take the replay from the panel while a clip is armed.");
        Field(au, "When the clip ends", Combo(new[] { ("Return to live automatically", ReturnToLiveMode.Automatic), ("Hold, return manually", ReturnToLiveMode.Manual) },
            m.ReturnToLive, v => { m.ReturnToLive = v; Changed(); }), null);

        var tr = Card(root, "Transitions and stinger");
        var modes = new[] { ("ATEM AUTO (mix, dip, wipe…)", TransitionMode.AtemAuto), ("ATEM CUT", TransitionMode.AtemCut), ("Endora stinger video", TransitionMode.EndoraStinger) };
        var styles = Enum.GetValues<AtemTransitionStyle>().Select(s => (s.ToString(), s)).ToArray();
        Field(tr, "Into replay", Combo(modes, m.TransitionIn, v => { m.TransitionIn = v; Changed(); Rebuild(); }), null);
        if (m.TransitionIn == TransitionMode.AtemAuto)
            Field(tr, "ATEM style (in)", Combo(styles, m.TransitionInStyle, v => { m.TransitionInStyle = v; Changed(); Rebuild(); }), null);
        Field(tr, "Back to live", Combo(modes, m.TransitionOut, v => { m.TransitionOut = v; Changed(); Rebuild(); }), null);
        if (m.TransitionOut == TransitionMode.AtemAuto)
            Field(tr, "ATEM style (out)", Combo(styles, m.TransitionOutStyle, v => { m.TransitionOutStyle = v; Changed(); Rebuild(); }), null);
        if ((m.TransitionIn == TransitionMode.AtemAuto && m.TransitionInStyle == AtemTransitionStyle.Stinger
             || m.TransitionOut == TransitionMode.AtemAuto && m.TransitionOutStyle == AtemTransitionStyle.Stinger) && !snap.SupportsAtemStinger)
            tr.Children.Add(Note("This ATEM did not report a stinger transition. ATEM Mini models have no clip media pool, so use \"Endora stinger video\" instead.", "Amber"));

        if (m.TransitionIn == TransitionMode.EndoraStinger || m.TransitionOut == TransitionMode.EndoraStinger)
        {
            Field(tr, "Stinger video (in)", FilePicker(m.StingerInFile, "Video|*.mp4;*.mov;*.avi|All files|*.*", v => { m.StingerInFile = v; Changed(); }), null);
            Field(tr, "Stinger video (out)", FilePicker(m.StingerOutFile, "Video|*.mp4;*.mov;*.avi|All files|*.*", v => { m.StingerOutFile = v; Changed(); }),
                "Leave empty to use the same stinger both ways.");
            tr.Children.Add(Note("Endora finds the point where the stinger covers the whole picture by itself and cuts the ATEM there, so the whole stinger plays in and out.", "Dim"));
            Toggle(tr, "Show the stinger over live (DSK key)", m.StingerKey, v => { m.StingerKey = v; Changed(); Rebuild(); },
                "Endora puts its replay input on an ATEM downstream key (luma) so the stinger appears over the live camera, then cuts underneath. The stinger video must be on a black background.");
            if (m.StingerKey)
            {
                Field(tr, "Downstream keyer", Combo(new[] { ("DSK 1", 1), ("DSK 2", 2) }, m.StingerKeyer, v => { m.StingerKeyer = v; Changed(); }),
                    "Endora takes over this keyer while a stinger plays. Don't use it for graphics at the same time.");
                Field(tr, "Key clip (%)", IntBox((int)Math.Round(m.StingerKeyClip), v => { m.StingerKeyClip = v; Changed(); }), "Darker than this is transparent. Raise it if the black background shows.");
                Field(tr, "Key gain (%)", IntBox((int)Math.Round(m.StingerKeyGain), v => { m.StingerKeyGain = v; Changed(); }), "Higher gives harder edges.");
            }
            Field(tr, "Fade (ms)", IntBox(m.StingerFadeMs, v => { m.StingerFadeMs = v; Changed(); }),
                "Fades between the stinger and the replay at the stinger's start and end, so the end never goes black. 0 turns it off.");
            foreach (var (isReturn, label, file, cov) in new[]
                     {
                         (false, "In", _host.Stingers.LoadedInFile, _host.Stingers.InCoverage),
                         (true, "Out", _host.Stingers.LoadedOutFile, _host.Stingers.OutCoverage),
                     })
            {
                var st = _host.Stingers.Get(isReturn);
                var fps = SafeFormat()?.FramesPerSecond ?? 50;
                tr.Children.Add(st is null
                    ? Note($"{label}: no stinger loaded. Apply to load the file.", "Amber")
                    : Note($"{label}: {Path.GetFileName(file)} · {st.Value.Frames.Count / fps:0.00} s · covers {cov:P0} at {st.Value.CutFrame / fps:0.00} s"
                           + (cov < 0.97 ? " · never fully covers the picture, so the cut may show" : ""), cov < 0.97 ? "Amber" : "Green"));
            }
        }

        var timing = Card(root, "Timing and safety");
        Field(timing, "Pre-roll (ms)", IntBox(m.PrerollMs, v => { m.PrerollMs = v; Changed(); }), "Delay between switching and starting the clip.");
        Field(timing, "Lockout (ms)", IntBox(m.LockoutMs, v => { m.LockoutMs = v; Changed(); }), "Ignores repeated REPLAY/LIVE presses within this time.");
        Field(timing, "Wait for Program (ms)", IntBox(m.WaitForProgramTimeoutMs, v => { m.WaitForProgramTimeoutMs = v; Changed(); }),
            "If the ATEM has not taken the replay input by then, Endora stops and raises an alert.");

        _refreshPage = () =>
        {
            var s = _host.Atem.Snapshot;
            status.Text = s.Connected
                ? $"Connected · {s.ModelName}{(_host.Atem.IsSimulated ? " · SIMULATED" : "")} · PGM {s.ProgramInput} · PVW {s.PreviewInput} · {s.VideoMode}"
                : "Not connected";
            status.Foreground = s.Connected ? (_host.Atem.IsSimulated ? B("Violet") : B("Green")) : B("Red");
        };
        return root;
    }

    // ================= output & storage =================

    private FrameworkElement BuildOutput()
    {
        var root = PageRoot("Output & storage", "Endora plays replays full-screen on a second GPU output. Connect that HDMI port to the ATEM replay input.");
        var o = _edit.Output;

        var outCard = Card(root, "Replay output");
        Field(outCard, "Output type", Combo(new[]
        {
            ("Display output (GPU HDMI → ATEM)", ReplayOutputKind.Display),
            ("DeckLink card (not available in this version)", ReplayOutputKind.DeckLink),
            ("SIMULATED (on-screen only)", ReplayOutputKind.Simulated),
        }, o.Kind, v => { o.Kind = v; Changed(); Rebuild(); }), null);

        if (o.Kind == ReplayOutputKind.Display)
        {
            var displays = DisplayInfo.List();
            var items = displays.Select(d => ($"{d}", (string?)d.DeviceName)).ToList();
            if (o.DisplayDevice is not null && displays.All(d => d.DeviceName != o.DisplayDevice))
                items.Insert(0, ($"{o.DisplayDevice} — NOT CONNECTED", o.DisplayDevice));
            items.Insert(0, ("(choose a display)", null));
            Field(outCard, "Display", Combo(items, o.DisplayDevice, v => { o.DisplayDevice = v; Changed(); Rebuild(); }),
                "In Windows display settings set this screen to Extend, 1920×1080 and the same refresh rate as the project format (50 Hz for 1080p50).");
            var fmt = SafeFormat();
            if (displays.FirstOrDefault(d => d.DeviceName == o.DisplayDevice) is { } sel && fmt is not null)
            {
                var problems = new List<string>();
                if (sel.IsPrimary) problems.Add("This is the main screen. Choose the second output connected to the ATEM.");
                if (sel.Width != fmt.Width || sel.Height != fmt.Height) problems.Add($"Resolution is {sel.Width}×{sel.Height}, project needs {fmt.Width}×{fmt.Height}.");
                if (!fmt.MatchesRate(sel.RefreshHz, 1)) problems.Add($"Refresh is {sel.RefreshHz} Hz, project needs {fmt.FramesPerSecond:0.##} Hz.");
                outCard.Children.Add(problems.Count == 0 ? Note("Display matches the project format.", "Green") : Note(string.Join(" ", problems), "Red"));
            }
        }
        else if (o.Kind == ReplayOutputKind.DeckLink)
            outCard.Children.Add(Note(HardwareFactory.DeckLinkUnavailable, "Red"));
        else
            outCard.Children.Add(Note("SIMULATED: replays only appear in Endora's OUT monitor. Nothing is sent to the ATEM.", "Violet"));

        var live = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Field(outCard, "Current output", live, null);
        _refreshPage = () =>
        {
            live.Text = $"{_host.Output.Name} · {_host.Output.Readiness}{(string.IsNullOrEmpty(_host.Output.ReadinessMessage) ? "" : " · " + _host.Output.ReadinessMessage)}";
            live.Foreground = _host.Output.Readiness == OutputReadiness.Ready ? B("Green") : B("Amber");
        };

        var badge = Card(root, "REPLAY badge");
        Toggle(badge, "Show the REPLAY badge while a replay plays", o.ReplayBadge, v => { o.ReplayBadge = v; Changed(); },
            "An animated badge in the bottom-right corner of the replay output, inside the title-safe area. Hidden during stingers and the holding frame.");
        Field(badge, "Badge text", TextBox(o.ReplayBadgeText, v => { o.ReplayBadgeText = v; Changed(); }), "Shown in capitals. Badge changes apply after restarting Endora.");

        var ndi = Card(root, "NDI output");
        Toggle(ndi, "Also send the replay playout as an NDI source", o.NdiOutput, v => { o.NdiOutput = v; Changed(); },
            "Sends the same pictures as the replay output on the network, for vMix, OBS or another PC. The REPLAY badge is not included. Applies after restarting Endora.");
        Field(ndi, "NDI source name", TextBox(o.NdiOutputName, v => { o.NdiOutputName = v; Changed(); }), "Shown as \"PC NAME (Endora Replay)\" on the network.");
        if (Hardware.Ndi.NdiSender.UnavailableReason is { } ndiWhy)
            ndi.Children.Add(Note(ndiWhy, "Amber"));
        ndi.Children.Add(Note("UNTESTED: NDI output and NDI inputs have not been tested on real NDI equipment yet. NDI® is a registered trademark of Vizrt NDI AB.", "Violet"));

        var hold = Card(root, "Holding frame (when no replay is playing)");
        Field(hold, "Show", Combo(new[] { ("Endora slate", HoldingFrameMode.Slate), ("Last replay frame", HoldingFrameMode.LastFrame), ("Black", HoldingFrameMode.Black) },
            o.Holding, v => { o.Holding = v; Changed(); }), null);
        Field(hold, "Custom slate image", FilePicker(o.HoldingImageFile, "Images|*.png;*.jpg;*.jpeg|All files|*.*", v => { o.HoldingImageFile = v; Changed(); }),
            "Optional. Used instead of the built-in slate.");

        var st = Card(root, "Storage");
        Field(st, "Data folder", FolderPicker(_edit.Buffer.DataFolder, v => { _edit.Buffer.DataFolder = v; Changed(); }),
            "Buffers, clips, database and logs. Use a fast SSD. Needs a restart.");
        Field(st, "Keep free (GB)", IntBox(_edit.Buffer.MinFreeDiskGb, v => { _edit.Buffer.MinFreeDiskGb = v; Changed(); }),
            "Endora always keeps this much space free on the replay drive.");
        Toggle(st, "Delete the oldest clips when the drive is low", _edit.Buffer.AutoDeleteOldClips, v => { _edit.Buffer.AutoDeleteOldClips = v; Changed(); },
            "Makes room by deleting the oldest saved clips. A clip that is armed, playing or in the queue is never deleted. Off: new clips are refused instead.");
        Toggle(st, "Keep tagged clips", _edit.Buffer.KeepTaggedClips, v => { _edit.Buffer.KeepTaggedClips = v; Changed(); },
            "Clips with a tag (WICKET, SIX…) are never deleted automatically.");
        var free = _host.Storage.FreeBytes;
        st.Children.Add(Note($"Free now: {free / 1e9:0.0} GB.", free < _host.Storage.MinFreeBytes ? "Red" : "Dim"));
        return root;
    }

    // ================= keyboard shortcuts =================

    private static readonly (string Group, string[] Actions)[] ShortcutGroups =
    [
        ("Air", [OperatorActions.Replay, OperatorActions.Live, OperatorActions.Stop, OperatorActions.CancelAuto, OperatorActions.EmergencyOverride, OperatorActions.ClearError]),
        ("Clips", [OperatorActions.MarkIn, OperatorActions.MarkOut, OperatorActions.SaveClip, OperatorActions.Tag, OperatorActions.Queue]),
        ("Cue", [OperatorActions.LiveMode, OperatorActions.JogMode, OperatorActions.FrameBack, OperatorActions.FrameForward, OperatorActions.ShuttleBack, OperatorActions.ShuttleForward]),
        ("Speed", [OperatorActions.Speed100, OperatorActions.Speed50, OperatorActions.Speed25]),
        ("Cameras", [OperatorActions.NextAngle, OperatorActions.PreviousAngle, .. Enumerable.Range(1, 8).Select(OperatorActions.Camera)]),
    ];

    private static string ActionLabel(string action) => action switch
    {
        OperatorActions.CancelAuto => "Cancel auto",
        OperatorActions.EmergencyOverride => "Emergency manual override",
        OperatorActions.ClearError => "Clear error",
        OperatorActions.MarkIn => "Mark IN",
        OperatorActions.MarkOut => "Mark OUT",
        OperatorActions.SaveClip => "Save clip",
        OperatorActions.Tag => "Tag clip",
        OperatorActions.Queue => "Add to queue",
        OperatorActions.NextAngle => "Next camera",
        OperatorActions.PreviousAngle => "Previous camera",
        OperatorActions.LiveMode => "Live mode",
        OperatorActions.JogMode => "Jog mode",
        OperatorActions.FrameBack => "Frame back",
        OperatorActions.FrameForward => "Frame forward",
        OperatorActions.ShuttleBack => "Shuttle back (1 s)",
        OperatorActions.ShuttleForward => "Shuttle forward (1 s)",
        OperatorActions.Speed100 => "Speed 100%",
        OperatorActions.Speed50 => "Speed 50%",
        OperatorActions.Speed25 => "Speed 25%",
        _ when action.StartsWith("Camera", StringComparison.Ordinal) => "Camera " + action[6..],
        _ => action,
    };

    private FrameworkElement BuildShortcuts()
    {
        var root = PageRoot("Keyboard shortcuts", "Click a key, then press the new key. Backspace clears it while capturing… press Esc to cancel. Ctrl+, opens settings.");
        var reset = new Button { Content = "Reset to defaults", Style = S("BtnBase"), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        reset.Click += (_, _) => { _edit.KeyBindings = new Dictionary<string, string>(KeyBindingDefaults.All); Changed(); Rebuild(); };
        var all = new Button { Content = "Program all keys", Style = S("BtnBlue"), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        all.Click += (_, _) =>
        {
            _keyLearnAll = new Queue<string>(ShortcutGroups.SelectMany(g => g.Actions));
            _capturingAction = _keyLearnAll.Dequeue();
            Rebuild();
        };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12), Children = { all, reset } });
        reset.Margin = new Thickness(0);
        if (_capturingAction is not null && _keyLearnAll is not null)
            root.Children.Add(new TextBlock
            {
                Text = $"Press the key for: {ActionLabel(_capturingAction)}   (Esc stops, Backspace skips)",
                FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B("Blue"), Margin = new Thickness(0, 0, 0, 12),
            });

        var conflicts = KeyBindingDefaults.FindConflicts(_edit.KeyBindings).SelectMany(c => c.Actions).ToHashSet();
        var wrap = new WrapPanel();
        foreach (var (group, actions) in ShortcutGroups)
        {
            var card = Card(wrap, group);
            ((Border)card.Parent).Width = 400;
            ((Border)card.Parent).Margin = new Thickness(0, 0, 14, 14);
            foreach (var action in actions)
            {
                _edit.KeyBindings.TryGetValue(action, out var gesture);
                var key = new Button
                {
                    Content = _capturingAction == action ? "Press a key…" : string.IsNullOrWhiteSpace(gesture) ? "—" : KeyBindingService.Pretty(gesture),
                    Style = S(_capturingAction == action ? "BtnBlue" : "BtnBase"),
                    MinWidth = 120, Padding = new Thickness(10, 5, 10, 5), FontFamily = (FontFamily)FindResource("MonoFont"),
                    BorderBrush = conflicts.Contains(action) ? B("Red") : null,
                };
                key.Click += (_, _) => { _capturingAction = action; Rebuild(); };
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
                row.Children.Add(new TextBlock
                {
                    Text = ActionLabel(action), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = conflicts.Contains(action) ? B("Red") : B("Text"),
                });
                card.Children.Add(row);
            }
        }
        root.Children.Add(wrap);
        return root;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingAction is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        e.Handled = true;
        var mods = Keyboard.Modifiers;
        if (_keyLearnAll is not null && mods == ModifierKeys.None && key is Key.Escape or Key.Back)
        {
            // Programming all keys: Esc stops, Backspace skips and keeps the current key.
            if (key == Key.Escape) _keyLearnAll.Clear();
        }
        else if (key == Key.Escape && mods == ModifierKeys.None && _edit.KeyBindings.GetValueOrDefault(_capturingAction) != "Escape")
        {
            // Esc cancels capture, unless the operator is deliberately re-binding Esc to this action: press Esc twice.
            if (_escPending) { _edit.KeyBindings[_capturingAction] = "Escape"; Changed(); }
            else { _escPending = true; return; }
        }
        else if (key == Key.Back && mods == ModifierKeys.None && _capturingAction != OperatorActions.CancelAuto)
        {
            _edit.KeyBindings[_capturingAction] = "";
            Changed();
        }
        else
        {
            var gesture = KeyBindingService.Format(key, mods);
            if (gesture == "Ctrl+OemComma")
            {
                MessageBox.Show(this, "Ctrl+, is reserved for opening settings.", "Endora Replay");
                return;
            }
            _edit.KeyBindings[_capturingAction] = gesture;
            Changed();
        }
        _escPending = false;
        _capturingAction = _keyLearnAll is { Count: > 0 } q ? q.Dequeue() : null;
        if (_capturingAction is null) _keyLearnAll = null;
        Rebuild();
    }

    private bool _escPending;
    private Queue<string>? _keyLearnAll;

    // ================= phone remote =================

    private FrameworkElement BuildRemote()
    {
        var root = PageRoot("Phone remote", "Scan the QR code with an iPhone or Android phone on the same Wi-Fi/LAN as this PC. No app is needed; it opens in the browser.");
        var r = _edit.Remote;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(grid);

        var qrCard = Card(grid, "Scan to connect");
        ((Border)qrCard.Parent).Margin = new Thickness(0, 0, 14, 14);
        var qr = new Image { Width = 260, Height = 260, Margin = new Thickness(0, 6, 0, 10) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        var qrBorder = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center, Child = qr };
        qrCard.Children.Add(qrBorder);
        var url = new TextBox { IsReadOnly = true, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12 };
        qrCard.Children.Add(url);
        var code = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        qrCard.Children.Add(code);
        qrCard.Children.Add(new TextBlock { Text = "ACCESS CODE", Style = S("Label"), HorizontalAlignment = HorizontalAlignment.Center });
        var newCode = new Button { Content = "New code (disconnects all phones)", Style = S("BtnAmber"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0) };
        newCode.Click += async (_, _) =>
        {
            var c = Core.Remote.AccessCodeGenerator.Create();
            _edit.Remote.AccessCode = c;
            _host.Settings.Remote.AccessCode = c;
            _host.Settings.Save(_host.SettingsPath);
            await _host.Remote.ChangeCodeAsync(c);
            _qrUrl = null;
        };
        qrCard.Children.Add(newCode);
        Grid.SetColumn((UIElement)qrCard.Parent, 0);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var cfg = Card(right, "Remote access");
        Toggle(cfg, "Enable phone remote", r.Enabled, v => { r.Enabled = v; Changed(); }, null);
        Toggle(cfg, "Allow REPLAY / LIVE / STOP from phones", r.AllowTransport, v => { r.AllowTransport = v; Changed(); },
            "Off: phones can cue, mark and tag but cannot take anything to air.");
        Field(cfg, "Port", IntBox(r.Port, v => { r.Port = v; Changed(); }), "Windows Firewall must allow Endora on private networks.");

        var clientsCard = Card(right, "Connected phones");
        var clients = new StackPanel();
        clientsCard.Children.Add(clients);

        _qrUrl = null;
        _refreshPage = () =>
        {
            var remote = _host.Remote;
            url.Text = remote.Url ?? "Remote is off";
            code.Text = remote.IsRunning ? _host.Settings.Remote.AccessCode : "—";
            if (remote.Url != _qrUrl)
            {
                _qrUrl = remote.Url;
                qr.Source = remote.QrPng() is { } png ? LoadPng(png) : null;
                qrBorder.Opacity = remote.IsRunning ? 1 : 0.2;
            }
            var list = remote.Clients;
            if (clients.Children.Count == list.Count && clients.Tag as string == string.Join(",", list.Select(c => c.Id))) return;
            clients.Tag = string.Join(",", list.Select(c => c.Id));
            clients.Children.Clear();
            if (list.Count == 0) clients.Children.Add(new TextBlock { Text = "No phones connected.", Foreground = B("Dim") });
            foreach (var c in list)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var kick = new Button { Content = "Disconnect", Style = S("BtnBase"), Padding = new Thickness(10, 4, 10, 4) };
                kick.Click += async (_, _) => await remote.DisconnectAsync(c.Id);
                DockPanel.SetDock(kick, Dock.Right);
                row.Children.Add(kick);
                row.Children.Add(new TextBlock { Text = $"{c.Name} · {c.Address} · since {c.ConnectedUtc.ToLocalTime():HH:mm}", VerticalAlignment = VerticalAlignment.Center });
                clients.Children.Add(row);
            }
        };
        return root;
    }

    private string? _qrUrl;

    private static BitmapImage LoadPng(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ================= diagnostics & updates =================

    private FrameworkElement BuildDiagnostics()
    {
        var root = PageRoot("Diagnostics & updates", "Live health of every input and the output. Export a support bundle when something goes wrong.");

        var hc = Card(root, "Source health");
        var table = new Grid();
        foreach (var w in new[] { 160.0, 120, 80, 100, 90, 90, double.NaN })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        hc.Children.Add(table);

        var sys = Card(root, "System");
        var sysText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, LineHeight = 20 };
        sys.Children.Add(sysText);
        var bundle = new Button { Content = "Export support bundle…", Style = S("BtnBase"), Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        bundle.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Save support bundle to" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_host.Settings, AppSettings.JsonOptions);
                var path = _host.Diagnostics.ExportSupportBundle(dlg.FolderName, json);
                MessageBox.Show(this, "Saved " + path, "Endora Replay");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Endora Replay", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        sys.Children.Add(bundle);

        var up = Card(root, "Updates");
        var u = _edit.Updates;
        var upText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        up.Children.Add(upText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var check = new Button { Content = "Check now", Style = S("BtnBase"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        check.Click += async (_, _) => await _host.Updates.CheckAndDownloadAsync();
        var install = new Button { Content = "Restart & install", Style = S("BtnGreen"), Padding = new Thickness(12, 7, 12, 7) };
        install.Click += (_, _) =>
        {
            if (OnAir) { MessageBox.Show(this, "A replay is on air or pending. Install after it finishes.", "Endora Replay"); return; }
            if (MessageBox.Show(this, "Endora will close, install the update and start again. Recording stops for a moment.", "Endora Replay",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
                _host.Updates.ApplyAndRestart();
        };
        buttons.Children.Add(check);
        buttons.Children.Add(install);
        up.Children.Add(buttons);
        Toggle(up, "Download updates automatically", u.CheckAutomatically, v => { u.CheckAutomatically = v; Changed(); },
            "Updates download in the background and only install when you press Restart & install.");
        Toggle(up, "Include pre-release versions", u.IncludePrereleases, v => { u.IncludePrereleases = v; Changed(); }, null);
        Field(up, "Release source", TextBox(u.RepositoryUrl, v => { u.RepositoryUrl = v.Trim(); Changed(); }), "GitHub repository that publishes Endora releases.");

        _refreshPage = () =>
        {
            table.Children.Clear();
            table.RowDefinitions.Clear();
            AddRow(table, new[] { "SOURCE", "STATUS", "FPS", "FRAMES", "DROPPED", "GAPS", "LAST ERROR" }.Select(h => (UIElement)new TextBlock { Text = h, Style = S("Label") }).ToArray());
            foreach (var h in _host.Diagnostics.SourceHealthSnapshot())
            {
                var color = h.Status switch { SourceStatus.Signal => "Green", SourceStatus.Stopped => "Dim", SourceStatus.Dropping or SourceStatus.Reconnecting => "Amber", _ => "Red" };
                AddRow(table,
                    Cell(h.Name + (h.IsSimulated ? " · SIM" : "")), Cell(h.Status.ToString(), color), Cell($"{h.MeasuredFps:0.0}"),
                    Cell($"{h.FramesCaptured:N0}"), Cell($"{h.DroppedFrames:N0}", h.DroppedFrames > 0 ? "Amber" : null),
                    Cell($"{h.TimestampGaps:N0}", h.TimestampGaps > 0 ? "Amber" : null), Cell(h.LastError ?? "", "Dim"));
            }
            if (table.RowDefinitions.Count == 1) AddRow(table, Cell("No sources running.", "Dim"));

            var s = _host.Atem.Snapshot;
            sysText.Text =
                $"Version        {UpdateService.CurrentVersionText}\n" +
                $"Format         {_host.Settings.VideoFormat}\n" +
                $"ATEM           {(s.Connected ? s.ModelName : "not connected")}{(_host.Atem.IsSimulated ? " (SIMULATED)" : "")} · replay input {_host.Settings.Atem.ReplayInput}\n" +
                $"Automation     {(_host.Settings.Automation.Enabled ? "on" : "off")} · {_host.Automation.State}{(_host.Automation.OverrideActive ? " · MANUAL OVERRIDE" : "")}\n" +
                $"Output         {_host.Output.Name} · {_host.Output.Readiness}\n" +
                $"Disk free      {_host.Storage.FreeBytes / 1e9:0.0} GB\n" +
                $"Data folder    {_host.Settings.Buffer.DataFolder}\n" +
                $"Settings file  {_host.SettingsPath}";

            var st = _host.Updates;
            upText.Text = st.Status switch
            {
                UpdateStatus.NotInstalled => "Running without the installer (developer build). Auto-update works after installing with EndoraReplay-win-Setup.exe.",
                UpdateStatus.UpToDate => $"Up to date ({UpdateService.CurrentVersionText}).",
                UpdateStatus.Checking => "Checking GitHub for updates…",
                UpdateStatus.Downloading => $"Downloading update… {st.Progress}%",
                UpdateStatus.ReadyToInstall => $"Version {st.AvailableVersion} is downloaded and ready to install.",
                _ => "Update check failed: " + st.Error,
            };
            install.IsEnabled = st.Status == UpdateStatus.ReadyToInstall && !OnAir;
            check.IsEnabled = st.IsInstalled && st.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading);
        };
        return root;
    }

    private bool OnAir => _host.Automation.State is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM or ReplayState.REPLAY_COMPLETE;

    // ================= builders =================

    private SolidColorBrush B(string key) => (SolidColorBrush)FindResource(key);
    private Style S(string key) => (Style)FindResource(key);

    private VideoFormat? SafeFormat()
    {
        try { return Core.Model.VideoFormat.Parse(_edit.VideoFormat); }
        catch (FormatException) { return null; }
    }

    private StackPanel PageRoot(string title, string sub)
    {
        var root = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = sub, Foreground = B("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 18) });
        return root;
    }

    private StackPanel Card(Panel parent, string title)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = S("H2"), Margin = new Thickness(0, 0, 0, 12) });
        parent.Children.Add(new Border { Style = S("Card"), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 14), Child = body });
        return body;
    }

    private void Field(Panel card, string label, FrameworkElement control, string? help)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 12, 0), TextWrapping = TextWrapping.Wrap });
        var right = new StackPanel();
        right.Children.Add(control);
        if (help is not null)
            right.Children.Add(new TextBlock { Text = help, Foreground = B("Dim"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        card.Children.Add(g);
    }

    private void Toggle(Panel card, string label, bool value, Action<bool> set, string? help)
    {
        var sw = new CheckBox { Style = S("Switch"), IsChecked = value, HorizontalAlignment = HorizontalAlignment.Left };
        sw.Click += (_, _) => set(sw.IsChecked == true);
        Field(card, label, sw, help);
    }

    private TextBlock Note(string text, string color) => new()
    {
        Text = text, Foreground = B(color), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 8),
    };

    private TextBlock Cell(string text, string? color = null) => new()
    {
        Text = text, Foreground = color is null ? B("Text") : B(color), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 8, 4),
    };

    private static TextBox TextBox(string value, Action<string> set)
    {
        var t = new TextBox { Text = value, Margin = new Thickness(0, 0, 8, 0) };
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    private TextBox IntBox(int value, Action<int> set)
    {
        var t = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        t.TextChanged += (_, _) =>
        {
            var ok = int.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
            t.BorderBrush = ok ? null : B("Red");
            if (ok) set(v);
        };
        return t;
    }

    private static ComboBox Combo(IEnumerable<string> values, string current, Action<string> set) =>
        Combo(values.Select(v => (v, v)), current, set);

    private static ComboBox Combo<T>(IEnumerable<(string Label, T Value)> items, T current, Action<T> set)
    {
        var list = items.ToList();
        var c = new ComboBox { ItemsSource = list.Select(i => i.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        c.SelectedIndex = list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, current));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(list[c.SelectedIndex].Value); };
        return c;
    }

    private DockPanel FilePicker(string? value, string filter, Action<string?> set)
    {
        var d = new DockPanel();
        var t = new TextBox { Text = value ?? "", Margin = new Thickness(0, 0, 8, 0) };
        t.TextChanged += (_, _) => set(string.IsNullOrWhiteSpace(t.Text) ? null : t.Text.Trim());
        var browse = new Button { Content = "Browse…", Style = S("BtnBase"), Padding = new Thickness(12, 5, 12, 5) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog(this) == true) t.Text = dlg.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        d.Children.Add(browse);
        d.Children.Add(t);
        return d;
    }

    private DockPanel FolderPicker(string value, Action<string> set)
    {
        var d = new DockPanel();
        var t = new TextBox { Text = value, Margin = new Thickness(0, 0, 8, 0) };
        t.TextChanged += (_, _) => set(t.Text.Trim());
        var browse = new Button { Content = "Browse…", Style = S("BtnBase"), Padding = new Thickness(12, 5, 12, 5) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(t.Text) ? t.Text : null };
            if (dlg.ShowDialog(this) == true) t.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        d.Children.Add(browse);
        d.Children.Add(t);
        return d;
    }

    private static void AddRow(Grid grid, params UIElement[] cells)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 4, 8, 4);
            Grid.SetRow(cells[i], row);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
    }
}
