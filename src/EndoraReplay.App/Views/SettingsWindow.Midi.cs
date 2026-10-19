using System.Windows;
using System.Windows.Controls;
using EndoraReplay.Core.Input;
using EndoraReplay.Hardware.Midi;

namespace EndoraReplay.App.Views;

/// <summary>MIDI controller page: learn buttons one by one or all in a row, the jog wheel and the T-bar.</summary>
public partial class SettingsWindow
{
    private enum MidiLearnTarget { None, Button, Jog, TBar }

    private MidiLearnTarget _midiLearn;
    private string? _midiLearnAction;
    private Queue<string>? _midiLearnAll;
    private readonly List<int> _jogSamples = [];
    private MidiControl? _jogCandidate;
    private MidiMessage? _lastMidi;
    private int? _tbarRaw;
    private bool _midiHooked;

    private static IEnumerable<string> AllActions => ShortcutGroups.SelectMany(g => g.Actions);

    private void HookMidi()
    {
        if (_midiHooked) return;
        _midiHooked = true;
        _host.MidiReceived += OnMidiSeen;
        Closed += (_, _) =>
        {
            _host.MidiReceived -= OnMidiSeen;
            _host.MidiLearn = null;
        };
    }

    private void OnMidiSeen(MidiMessage m) => Dispatcher.BeginInvoke(() =>
    {
        _lastMidi = m;
        if (_edit.Midi.TBar?.Matches(m) == true) _tbarRaw = m.Value;
    });

    private void StopMidiLearn()
    {
        _midiLearn = MidiLearnTarget.None;
        _midiLearnAction = null;
        _midiLearnAll = null;
        _host.MidiLearn = null;
    }

    private void StartMidiLearn(MidiLearnTarget target, string? action = null)
    {
        _midiLearn = target;
        _midiLearnAction = action;
        _jogSamples.Clear();
        _jogCandidate = null;
        _host.MidiLearn = m => Dispatcher.BeginInvoke(() => OnMidiLearn(m));
        Rebuild();
    }

    private void LearnNextOfAll()
    {
        if (_midiLearnAll is { Count: > 0 } q) StartMidiLearn(MidiLearnTarget.Button, q.Dequeue());
        else
        {
            StopMidiLearn();
            Rebuild();
        }
    }

    private void OnMidiLearn(MidiMessage m)
    {
        var s = _edit.Midi;
        switch (_midiLearn)
        {
            case MidiLearnTarget.Button when _midiLearnAction is { } action:
                // Learn on the press, not the release.
                if (m.Kind == MidiControlKind.PitchBend || m.Value == 0) return;
                var control = MidiControl.From(m);
                s.Bindings.RemoveAll(b => b.Action == action || b.Control.Matches(m));
                s.Bindings.Add(new MidiBinding { Action = action, Control = control });
                if (s.Jog?.Matches(m) == true) s.Jog = null;
                if (s.TBar?.Matches(m) == true) s.TBar = null;
                Changed();
                if (_midiLearnAll is not null) LearnNextOfAll();
                else
                {
                    StopMidiLearn();
                    Rebuild();
                }
                return;

            case MidiLearnTarget.Jog:
                if (m.Kind == MidiControlKind.Note) return;
                if (_jogCandidate is null || !_jogCandidate.Matches(m))
                {
                    _jogCandidate = MidiControl.From(m);
                    _jogSamples.Clear();
                }
                _jogSamples.Add(m.Value);
                if (_jogSamples.Count < 6) return;
                var enc = JogDecoder.Detect(_jogSamples);
                s.Jog = _jogCandidate;
                s.JogEncoding = enc;
                // The operator turned the wheel to the right, so a negative sum means this wheel counts the other way.
                s.JogInvert = JogDecoder.Sum(enc, _jogSamples) < 0;
                s.Bindings.RemoveAll(b => b.Control.Matches(m));
                if (s.TBar?.Matches(m) == true) s.TBar = null;
                Changed();
                StopMidiLearn();
                Rebuild();
                return;

            case MidiLearnTarget.TBar:
                if (m.Kind == MidiControlKind.Note) return;
                s.TBar = MidiControl.From(m);
                s.TBarZero = 0;
                s.TBarFull = m.MaxValue;
                _tbarRaw = m.Value;
                s.Bindings.RemoveAll(b => b.Control.Matches(m));
                if (s.Jog?.Matches(m) == true) s.Jog = null;
                Changed();
                StopMidiLearn();
                Rebuild();
                return;
        }
    }

    private Button SmallButton(string text, string style, Action click)
    {
        var b = new Button { Content = text, Style = S(style), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private FrameworkElement BuildMidi()
    {
        HookMidi();
        var s = _edit.Midi;
        var root = PageRoot("MIDI controller",
            "Use a MIDI keyboard or controller for any operator action, a jog wheel to move through frames and a T-bar or fader for replay speed. Click Learn, then press or move the control.");

        var dev = Card(root, "Device");
        Toggle(dev, "Use MIDI", s.Enabled, v => { s.Enabled = v; Changed(); }, null);
        IReadOnlyList<string> devices;
        try { devices = WinMmMidiInput.ListDevices(); }
        catch (Exception) { devices = []; }
        var items = new List<(string, string?)> { ("All connected MIDI devices", null) };
        items.AddRange(devices.Select(d => (d, (string?)d)));
        if (!string.IsNullOrWhiteSpace(s.Device) && !devices.Contains(s.Device)) items.Add((s.Device + " (not connected)", s.Device));
        Field(dev, "MIDI input", Combo(items, string.IsNullOrWhiteSpace(s.Device) ? null : s.Device, v => { s.Device = v; Changed(); }),
            devices.Count == 0 ? "No MIDI device found. Plug it in and reopen this page." : "Changes apply when you press Apply.");
        var last = new TextBlock { FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Foreground = B("Dim") };
        Field(dev, "Last message", last, "Move any control to check that Endora hears it.");
        dev.Children.Add(Note("UNTESTED: MIDI has not been tested with a real controller yet.", "Violet"));

        if (_midiLearn != MidiLearnTarget.None)
        {
            var banner = Card(root, "Learning");
            var what = _midiLearn switch
            {
                MidiLearnTarget.Button => $"Press the MIDI button or key for: {ActionLabel(_midiLearnAction!)}",
                MidiLearnTarget.Jog => "Turn the jog wheel slowly to the RIGHT (forward)…",
                _ => "Move the T-bar or fader…",
            };
            banner.Children.Add(new TextBlock { Text = what, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B("Blue"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var buttons = new List<UIElement>();
            if (_midiLearnAll is not null) buttons.Add(SmallButton("Skip", "BtnBase", LearnNextOfAll));
            buttons.Add(SmallButton("Stop learning", "BtnBase", () => { StopMidiLearn(); Rebuild(); }));
            banner.Children.Add(Row(buttons.ToArray()));
        }

        var auto = Card(root, "Program all buttons");
        auto.Children.Add(Note("Endora asks for each action in turn: press the button you want for it. Skip any you don't need.", "Dim"));
        auto.Children.Add(Row(
            SmallButton("Program all", "BtnBlue", () => { _midiLearnAll = new Queue<string>(AllActions); LearnNextOfAll(); }),
            SmallButton("Clear all buttons", "BtnBase", () => { s.Bindings.Clear(); Changed(); Rebuild(); })));

        var jog = Card(root, "Jog wheel");
        Field(jog, "Control", Row(
            new TextBlock { Text = s.Jog?.ToString() ?? "Not set", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 150 },
            SmallButton("Learn", "BtnBlue", () => StartMidiLearn(MidiLearnTarget.Jog)),
            SmallButton("Clear", "BtnBase", () => { s.Jog = null; Changed(); Rebuild(); })),
            "Learn asks you to turn the wheel to the right, then works out the wheel's encoding and direction. Left turns go back, right turns go forward.");
        Field(jog, "Frames per step", IntBox(s.JogFramesPerStep, v => { s.JogFramesPerStep = v; Changed(); }), "How many frames one click of the wheel moves.");
        Field(jog, "Encoding", Combo(new[]
        {
            ("Auto detect", MidiJogEncoding.Auto), ("Relative 64 (63 left / 65 right)", MidiJogEncoding.Offset64),
            ("Relative 127/1 (two's complement)", MidiJogEncoding.TwosComplement), ("Relative sign bit (65 left / 1 right)", MidiJogEncoding.SignBit),
            ("Absolute position 0-127", MidiJogEncoding.Absolute),
        }, s.JogEncoding, v => { s.JogEncoding = v; Changed(); }), null);
        Toggle(jog, "Reverse direction", s.JogInvert, v => { s.JogInvert = v; Changed(); }, "Turn on if the wheel moves the wrong way.");

        var tbar = Card(root, "T-bar / speed fader");
        Field(tbar, "Control", Row(
            new TextBlock { Text = s.TBar?.ToString() ?? "Not set", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 150 },
            SmallButton("Learn", "BtnBlue", () => StartMidiLearn(MidiLearnTarget.TBar)),
            SmallButton("Clear", "BtnBase", () => { s.TBar = null; Changed(); Rebuild(); })), null);
        var tbarNow = new TextBlock { FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 150 };
        var ends = new TextBlock { Foreground = B("Dim"), VerticalAlignment = VerticalAlignment.Center };
        Field(tbar, "Ends", Row(
            tbarNow,
            SmallButton("Set 0 % here", "BtnBase", () => { if (_tbarRaw is { } r) { s.TBarZero = r; Changed(); } }),
            SmallButton("Set 100 % here", "BtnBase", () => { if (_tbarRaw is { } r) { s.TBarFull = r; Changed(); } }),
            ends),
            "Move the T-bar to one end and press Set 0 %, then to the other end and press Set 100 %. Speed changes smoothly between them.");
        Field(tbar, "Speed at 0 % end", IntBox(s.TBarMinSpeed, v => { s.TBarMinSpeed = v; Changed(); }), "Percent. 0 % plays at the slowest speed (1 %).");
        Field(tbar, "Speed at 100 % end", IntBox(s.TBarMaxSpeed, v => { s.TBarMaxSpeed = v; Changed(); }), "Percent, up to 200.");

        var btns = Card(root, "Buttons");
        var wrap = new WrapPanel();
        foreach (var (group, actions) in ShortcutGroups)
        {
            var card = Card(wrap, group);
            ((Border)card.Parent).Width = 420;
            ((Border)card.Parent).Margin = new Thickness(0, 0, 14, 14);
            foreach (var action in actions)
            {
                var bound = s.Bindings.FirstOrDefault(b => b.Action == action);
                var learning = _midiLearn == MidiLearnTarget.Button && _midiLearnAction == action;
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var right = Row(
                    new TextBlock { Text = learning ? "Press…" : bound?.Control.ToString() ?? "—", MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), Foreground = learning ? B("Blue") : B("Text") },
                    SmallButton("Learn", learning ? "BtnBlue" : "BtnBase", () => { _midiLearnAll = null; StartMidiLearn(MidiLearnTarget.Button, action); }),
                    SmallButton("✕", "BtnBase", () => { s.Bindings.RemoveAll(b => b.Action == action); Changed(); Rebuild(); }));
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(new TextBlock { Text = ActionLabel(action), VerticalAlignment = VerticalAlignment.Center });
                card.Children.Add(row);
            }
        }
        btns.Children.Add(wrap);

        _refreshPage = () =>
        {
            last.Text = _lastMidi is { } m ? $"{MidiControl.From(m)} = {m.Value}" : "Nothing received yet";
            tbarNow.Text = s.TBar is null ? "" : _tbarRaw is { } r ? $"Now {r} → {MidiMapper.TBarSpeed(s, r):P0}" : "Move the T-bar";
            ends.Text = s.TBar is null ? "" : $"0 % = {s.TBarZero}, 100 % = {s.TBarFull}";
        };
        return root;
    }
}
