using System.IO;
using System.Text.Json;
using EndoraReplay.App.Output;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Services;
using EndoraReplay.Hardware;
using EndoraReplay.Hardware.Video;
using EndoraReplay.Remote;

namespace EndoraReplay.App.Services;

/// <summary>Composition root: builds, starts and shuts down every service in a safe order.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(AppSettings settings, string settingsPath)
    {
        Settings = settings;
        SettingsPath = settingsPath;
    }

    public AppSettings Settings { get; private set; }
    public string SettingsPath { get; }
    public MonotonicClock Clock { get; } = new();
    public DiagnosticsService Diagnostics { get; private set; } = null!;
    public StorageService Storage { get; private set; } = null!;
    public RollingBufferService Buffers { get; private set; } = null!;
    public CaptureService Capture { get; private set; } = null!;
    public ClipService Clips { get; private set; } = null!;
    public IReplayOutput Output { get; private set; } = null!;
    public ReplayPlaybackService Playback { get; private set; } = null!;
    public AtemSwitcherService Atem { get; private set; } = null!;
    public FileStingerProvider Stingers { get; } = new();
    public LumaKeyCompositor StingerCompositor { get; } = new(8, 70);
    public ReplayAutomationService Automation { get; private set; } = null!;
    public OperatorController Operator { get; private set; } = null!;
    public RemoteServer Remote { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;

    /// <summary>Settings that only take effect after a restart (format, data folder, output).</summary>
    public bool RestartRequired { get; private set; }

    public static async Task<AppHost> StartAsync(string settingsPath)
    {
        var settings = AppSettings.Load(settingsPath);
        var h = new AppHost(settings, settingsPath);
        await h.BuildAsync().ConfigureAwait(false);
        return h;
    }

    private async Task BuildAsync()
    {
        var s = Settings;
        Directory.CreateDirectory(s.Buffer.DataFolder);
        Diagnostics = new DiagnosticsService(Path.Combine(s.Buffer.DataFolder, "logs"), () => Clock.NowTicks);
        Diagnostics.Info(LogCategory.App, $"Endora Replay {UpdateService.CurrentVersionText} starting. Format {s.VideoFormat}.");
        foreach (var p in s.Validate()) Diagnostics.Warn(LogCategory.App, "Settings: " + p);

        Storage = new StorageService(s.Buffer.DataFolder, s.Buffer.MinFreeDiskGb, Diagnostics);
        Buffers = new RollingBufferService(Storage.BufferFolder, Diagnostics);
        Capture = new CaptureService(HardwareFactory.CaptureBackends(), Buffers, Diagnostics, Clock);
        Clips = new ClipService(Buffers, Storage, Diagnostics, Clock);

        Output = s.Output.Kind switch
        {
            ReplayOutputKind.Display => new DisplayReplayOutput(s.Output, Diagnostics),
            _ => new SimulatedReplayOutput(),
        };
        if (s.Output.Kind == ReplayOutputKind.DeckLink)
            Diagnostics.Error(LogCategory.Output, HardwareFactory.DeckLinkUnavailable + " Using SIMULATED output.");
        try { await Output.OpenAsync(s.Format, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { Diagnostics.Error(LogCategory.Output, $"Output failed to open: {e.Message}"); }

        Playback = new ReplayPlaybackService(Output, s.Format, Diagnostics);
        Playback.SetSpeed(0.5);
        Atem = new AtemSwitcherService(HardwareFactory.CreateSwitcher, Diagnostics);
        LoadStingers();

        OperatorController? op = null;
        Automation = new ReplayAutomationService(AutomationOptions.From(s), Playback, Atem, Stingers,
            r => op!.OpenClip(r), Diagnostics, Clock, StingerCompositor, s.Format);
        op = new OperatorController(s, Clock, Capture, Buffers, Clips, Playback, Automation, Diagnostics, () => Atem.Snapshot);
        Operator = op;
        Capture.RecordingChanged += rec => Automation.Post(new RecordingStateEvent(rec));
        Remote = new RemoteServer(op, Diagnostics);
        Updates = new UpdateService(s.Updates, Diagnostics);

        await StartAtemAsync().ConfigureAwait(false);
        await StartRemoteAsync().ConfigureAwait(false);
        _ = Task.Run(StartCaptureAsync);
    }

    private void LoadStingers()
    {
        var a = Settings.Automation;
        try
        {
            StingerCompositor.Configure(a.StingerKeyClip, a.StingerKeyGain);
            Stingers.Load(a.StingerInFile, a.StingerOutFile, Settings.Format, StingerCompositor);
            foreach (var (name, cov) in new[] { ("IN", Stingers.InCoverage), ("OUT", Stingers.OutCoverage) })
                if (cov is > 0 and < 0.97)
                    Diagnostics.Warn(LogCategory.Automation, $"Stinger {name} covers only {cov:P0} of the picture at its fullest; the ATEM cut may be visible.");
            if (a.StingerInFile is not null) Diagnostics.Info(LogCategory.Automation, $"Stinger loaded: {Path.GetFileName(a.StingerInFile)}");
        }
        catch (Exception e)
        {
            Diagnostics.Error(LogCategory.Automation, $"Stinger not loaded: {e.Message}");
        }
    }

    public async Task StartCaptureAsync()
    {
        try { await Capture.StartAsync(Settings, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { Diagnostics.Error(LogCategory.Capture, $"Capture start failed: {e.Message}"); }
    }

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
        catch (Exception e) { Diagnostics.Error(LogCategory.Remote, $"Phone remote could not start on port {Settings.Remote.Port}: {e.Message}"); }
    }

    /// <summary>Applies edited settings: live where safe, otherwise flags a restart.</summary>
    public async Task ApplyAsync(AppSettings updated)
    {
        var old = Settings;
        RestartRequired |= old.VideoFormat != updated.VideoFormat
            || old.Buffer.DataFolder != updated.Buffer.DataFolder
            || old.Output.Kind != updated.Output.Kind
            || old.Output.DisplayDevice != updated.Output.DisplayDevice
            || old.Output.Holding != updated.Output.Holding
            || old.Output.HoldingImageFile != updated.Output.HoldingImageFile;

        updated.Save(SettingsPath);
        var restartCapture = JsonSerializer.Serialize(old.Sources) != JsonSerializer.Serialize(updated.Sources)
            || old.Buffer.Seconds != updated.Buffer.Seconds || old.Buffer.JpegQuality != updated.Buffer.JpegQuality;
        var restartAtem = JsonSerializer.Serialize(old.Atem) != JsonSerializer.Serialize(updated.Atem);
        var restartRemote = JsonSerializer.Serialize(old.Remote) != JsonSerializer.Serialize(updated.Remote);

        // Copy into the live instance so every service holding it sees the change.
        CopyInto(updated, old);
        Automation.UpdateOptions(AutomationOptions.From(old));
        LoadStingers();
        if (restartAtem) await StartAtemAsync().ConfigureAwait(false);
        if (restartRemote) await StartRemoteAsync().ConfigureAwait(false);
        if (restartCapture) await StartCaptureAsync().ConfigureAwait(false);
        Diagnostics.Info(LogCategory.App, "Settings applied" + (RestartRequired ? " (restart Endora to apply output/format/folder changes)" : ""));
    }

    private static void CopyInto(AppSettings from, AppSettings to)
    {
        var json = JsonSerializer.Serialize(from, AppSettings.JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
        to.Sources = copy.Sources;
        to.Buffer = copy.Buffer;
        to.Atem = copy.Atem;
        to.Automation = copy.Automation;
        to.Output = copy.Output;
        to.Remote = copy.Remote;
        to.Updates = copy.Updates;
        to.KeyBindings = copy.KeyBindings;
        to.VideoFormat = copy.VideoFormat;
    }

    /// <summary>Deep copy for the settings editor.</summary>
    public AppSettings CloneSettings() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings, AppSettings.JsonOptions), AppSettings.JsonOptions)!;

    /// <summary>Stops everything in reverse dependency order: inputs first, output last.</summary>
    public async ValueTask DisposeAsync()
    {
        Diagnostics.Info(LogCategory.App, "Shutting down");
        await Remote.DisposeAsync().ConfigureAwait(false);
        await Automation.DisposeAsync().ConfigureAwait(false);
        await Atem.DisposeAsync().ConfigureAwait(false);
        await Capture.DisposeAsync().ConfigureAwait(false);
        await Playback.DisposeAsync().ConfigureAwait(false);
        await Output.DisposeAsync().ConfigureAwait(false);
        Buffers.Dispose();
        Storage.Dispose();
        await Diagnostics.DisposeAsync().ConfigureAwait(false);
    }
}
