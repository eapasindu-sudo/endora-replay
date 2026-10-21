using System.IO;
using System.Text.Json;
using EndoraReplay.App.Output;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Services;
using EndoraReplay.Core.Vmix;
using EndoraReplay.Hardware;
using EndoraReplay.Hardware.Midi;
using EndoraReplay.Remote;

namespace EndoraReplay.App.Services;

/// <summary>
/// Composition root for vMix replay: vMix makes the replays, Endora only automates the stinger and the ATEM.
/// No capture, buffers or playout are started in this mode.
/// </summary>
public sealed class VmixHost : IAsyncDisposable, IRemoteHandler
{
    private VmixHost(AppSettings settings, string settingsPath)
    {
        Settings = settings;
        SettingsPath = settingsPath;
    }

    public AppSettings Settings { get; }
    public string SettingsPath { get; }
    public MonotonicClock Clock { get; } = new();
    public DiagnosticsService Diagnostics { get; private set; } = null!;
    public AtemSwitcherService Atem { get; private set; } = null!;
    public VmixClient Vmix { get; private set; } = null!;
    public VmixBridge Bridge { get; private set; } = null!;
    public RemoteServer Remote { get; private set; } = null!;
    public CompanionVariables Companion { get; private set; } = null!;
    public WinMmMidiInput Midi { get; } = new();
    public ScoreboardOutput Scoreboard { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;
    private MidiMapper _midiMap = new(new MidiSettings());
    private readonly CancellationTokenSource _cts = new();

    public static async Task<VmixHost> StartAsync(string settingsPath, IProgress<string>? progress = null)
    {
        progress?.Report("Loading settings…");
        var h = new VmixHost(AppSettings.Load(settingsPath), settingsPath);
        await h.BuildAsync(progress).ConfigureAwait(false);
        return h;
    }

    private async Task BuildAsync(IProgress<string>? progress)
    {
        var s = Settings;
        Directory.CreateDirectory(s.Buffer.DataFolder);
        Diagnostics = new DiagnosticsService(Path.Combine(s.Buffer.DataFolder, "logs"), () => Clock.NowTicks);
        Diagnostics.Info(LogCategory.App, $"Endora Replay {UpdateService.CurrentVersionText} starting in vMix replay mode.");
        foreach (var p in s.Vmix.Validate()) Diagnostics.Warn(LogCategory.App, "Settings: " + p);

        Atem = new AtemSwitcherService(HardwareFactory.CreateSwitcher, Diagnostics);
        Vmix = new VmixClient(Diagnostics);
        Bridge = new VmixBridge(Vmix, Atem, () => Settings, Diagnostics)
        {
            HideScoreboard = HideScoreboardForReplay,
            ShowScoreboard = () => SetScoreboard(true),
            ToggleScoreboard = () => SetScoreboard(!ScoreboardOnAir),
        };
        Remote = new RemoteServer(this, Diagnostics);
        Companion = new CompanionVariables(Diagnostics) { BaseUrl = s.Vmix.CompanionUrl };
        Updates = new UpdateService(s.Updates, Diagnostics);

        progress?.Report("Connecting to the ATEM…");
        await StartAtemAsync().ConfigureAwait(false);
        progress?.Report("Connecting to vMix…");
        await Vmix.StartAsync(s.Vmix.Host, s.Vmix.Port, s.Vmix.ReconnectSeconds).ConfigureAwait(false);
        progress?.Report("Starting the remote…");
        await StartRemoteAsync().ConfigureAwait(false);
        Midi.MessageReceived += OnMidi;
        StartMidi();
        Scoreboard = new ScoreboardOutput(Diagnostics, s.Buffer.DataFolder);
        StartScoreboard();
        Bridge.Start();
        _ = Task.Run(() => CompanionLoopAsync(_cts.Token));
    }

    /// <summary>Runs an operator action from the screen, keyboard, MIDI, phone, Companion or a vMix script.</summary>
    public string? Execute(string action, string origin = "operator") => Bridge.Execute(action, origin);

    // ------------------------------------------------------------------ connections

    private async Task StartAtemAsync()
    {
        if (HardwareFactory.SwitcherUnavailableReason(Settings.Atem.Backend) is { } why)
        {
            Diagnostics.Error(LogCategory.Atem, why);
            return;
        }
        await Atem.StartAsync(Settings.Atem).ConfigureAwait(false);
    }

    private async Task StartRemoteAsync()
    {
        if (!Settings.Remote.Enabled)
        {
            await Remote.StopAsync().ConfigureAwait(false);
            return;
        }
        try { await Remote.StartAsync(Settings.Remote.Port, Settings.Remote.AccessCode, Settings.Remote.AllowTransport).ConfigureAwait(false); }
        catch (Exception e) { Diagnostics.Error(LogCategory.Remote, $"Remote could not start on port {Settings.Remote.Port}: {e.Message}"); }
    }

    public void StartMidi()
    {
        var m = Settings.Midi;
        _midiMap = new MidiMapper(m);
        try
        {
            if (!m.Enabled)
            {
                Midi.Close();
                return;
            }
            foreach (var p in Midi.Open(m.Device)) Diagnostics.Warn(LogCategory.App, "MIDI: " + p);
        }
        catch (Exception e)
        {
            Diagnostics.Error(LogCategory.App, $"MIDI could not start: {e.Message}");
        }
    }

    private void OnMidi(MidiMessage m)
    {
        foreach (var c in _midiMap.Map(m))
        {
            if (c.Action is MidiMapper.JogAction or MidiMapper.SpeedAction) continue;
            if (Execute(c.Action, "midi") is { } err) Diagnostics.Warn(LogCategory.App, $"MIDI {c.Action}: {err}");
        }
    }

    /// <summary>Keeps the Companion custom variables in step with Endora.</summary>
    private async Task CompanionLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Companion.BaseUrl.Length == 0) continue;
                var st = Bridge.Status;
                await Companion.PushAsync(new Dictionary<string, string>
                {
                    ["endora_state"] = st.StateText,
                    ["endora_onair"] = st.State is BridgeState.OnAir or BridgeState.GoingToReplay or BridgeState.Returning ? "1" : "0",
                    ["endora_atem"] = st.AtemConnected ? "OK" : "OFF",
                    ["endora_vmix"] = st.VmixConnected ? "OK" : "OFF",
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ scoreboard (same keying as Endora replay)

    private void StartScoreboard() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try { Scoreboard.Apply(Settings.Scoreboard); }
            catch (Exception e) { Diagnostics.Error(LogCategory.Output, $"Scoreboard could not start: {e.Message}"); }
        });

    public bool ScoreboardOnAir => Settings.Scoreboard.Keyer == ScoreboardKeyer.UpstreamKey1
        ? Atem.Snapshot.IsUpstreamKeyOnAir(1)
        : Atem.Snapshot.IsDownstreamKeyOnAir(2);

    private bool HideScoreboardForReplay()
    {
        var sb = Settings.Scoreboard;
        return sb.Enabled && sb.HideDuringReplay && ScoreboardOnAir && SetScoreboard(false) is null;
    }

    public string? SetScoreboard(bool onAir)
    {
        var sb = Settings.Scoreboard;
        if (!sb.Enabled) return "Scoreboard is not set up.";
        if (Bridge.State == BridgeState.Override) return "Emergency override is on: Endora sends no ATEM commands.";
        if (!Atem.IsConnected) return "ATEM is not connected.";
        _ = Task.Run(async () =>
        {
            try
            {
                if (sb.Keyer == ScoreboardKeyer.UpstreamKey1)
                {
                    if (onAir) await Atem.ConfigureUpstreamKeyAsync(1, sb.AtemInput, ScoreboardSettings.KeyClip, ScoreboardSettings.KeyGain).ConfigureAwait(false);
                    await Atem.SetUpstreamKeyOnAirAsync(1, onAir).ConfigureAwait(false);
                }
                else
                {
                    if (onAir) await Atem.ConfigureDownstreamKeyAsync(2, sb.AtemInput, ScoreboardSettings.KeyClip, ScoreboardSettings.KeyGain).ConfigureAwait(false);
                    await Atem.SetDownstreamKeyOnAirAsync(2, onAir).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Diagnostics.Error(LogCategory.Atem, $"Scoreboard key ({sb.Keyer}): {e.Message}");
            }
        });
        return null;
    }

    // ------------------------------------------------------------------ settings

    /// <summary>Deep copy for editing.</summary>
    public AppSettings CloneSettings() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings, AppSettings.JsonOptions), AppSettings.JsonOptions)!;

    /// <summary>Saves edited settings and reconnects what changed. Returns true when Endora must restart (replay engine changed).</summary>
    public async Task<bool> ApplyAsync(AppSettings updated)
    {
        var old = Settings;
        updated.Save(SettingsPath);
        string J<T>(T v) => JsonSerializer.Serialize(v, AppSettings.JsonOptions);
        var restartAtem = J(old.Atem) != J(updated.Atem);
        var restartVmix = old.Vmix.Host != updated.Vmix.Host || old.Vmix.Port != updated.Vmix.Port || old.Vmix.ReconnectSeconds != updated.Vmix.ReconnectSeconds;
        var restartRemote = J(old.Remote) != J(updated.Remote);
        var restartMidi = J(old.Midi) != J(updated.Midi);
        var engineChanged = old.Engine != updated.Engine;

        var copy = JsonSerializer.Deserialize<AppSettings>(J(updated), AppSettings.JsonOptions)!;
        old.Atem = copy.Atem;
        old.Vmix = copy.Vmix;
        old.Remote = copy.Remote;
        old.Midi = copy.Midi;
        old.Scoreboard = copy.Scoreboard;
        old.KeyBindings = copy.KeyBindings;
        old.Updates = copy.Updates;
        old.Engine = copy.Engine;

        Companion.BaseUrl = old.Vmix.CompanionUrl;
        if (restartAtem) await StartAtemAsync().ConfigureAwait(false);
        if (restartVmix) await Vmix.StartAsync(old.Vmix.Host, old.Vmix.Port, old.Vmix.ReconnectSeconds).ConfigureAwait(false);
        if (restartRemote) await StartRemoteAsync().ConfigureAwait(false);
        if (restartMidi) StartMidi();
        StartScoreboard();
        foreach (var p in old.Vmix.Validate()) Diagnostics.Warn(LogCategory.App, "Settings: " + p);
        Diagnostics.Info(LogCategory.App, "Settings applied" + (engineChanged ? " (restart Endora to change the replay engine)" : ""));
        return engineChanged;
    }

    // ------------------------------------------------------------------ IRemoteHandler (phone, Companion, vMix scripts)

    public RemoteState GetState()
    {
        var st = Bridge.Status;
        var a = Atem.Snapshot;
        return new RemoteState(st.StateText, st.AtemConnected, a.ProgramInput, a.PreviewInput, Settings.Atem.ReplayInput, 0, 1.0, "VMIX",
            null, null, 0, 0, st.ReplayPlaying ? "vMix replay playing" : null, st.LastAlert, st.State == BridgeState.Override,
            Settings.Remote.AllowTransport, [], []);
    }

    public IReadOnlyList<RemoteClip> GetClips() => [];

    public byte[]? GetPreviewJpeg(int? sourceId) => null;

    public Task<string?> ExecuteAsync(RemoteCommand command, string client) => Task.FromResult(Execute(command.Action, client));

    public async ValueTask DisposeAsync()
    {
        Diagnostics.Info(LogCategory.App, "Shutting down");
        await _cts.CancelAsync().ConfigureAwait(false);
        Midi.Dispose();
        await Remote.DisposeAsync().ConfigureAwait(false);
        await Bridge.DisposeAsync().ConfigureAwait(false);
        await Vmix.DisposeAsync().ConfigureAwait(false);
        await Atem.DisposeAsync().ConfigureAwait(false);
        Companion.Dispose();
        await Diagnostics.DisposeAsync().ConfigureAwait(false);
    }
}
