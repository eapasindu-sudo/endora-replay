using System.Text.Json;
using System.Text.Json.Serialization;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Config;

public sealed class AppSettings
{
    public const int MaxCameraSources = 8;

    /// <summary>Project video format, e.g. "1080p50". All sources and the replay output are validated against it.</summary>
    public string VideoFormat { get; set; } = "1080p50";

    public List<SourceSettings> Sources { get; set; } = DefaultSources();
    public BufferSettings Buffer { get; set; } = new();
    public AtemSettings Atem { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public Dictionary<string, string> KeyBindings { get; set; } = new(Input.KeyBindingDefaults.All);

    /// <summary>MIDI keyboard / controller: programmable buttons, jog wheel and T-bar.</summary>
    public Input.MidiSettings Midi { get; set; } = new();

    /// <summary>Operator screen layout.</summary>
    public UiSettings Ui { get; set; } = new();

    /// <summary>Web scoreboard keyed over the ATEM Program (DSK 2 or upstream key 1).</summary>
    public ScoreboardSettings Scoreboard { get; set; } = new();

    /// <summary>Endora's own replay, or vMix replay with Endora automating the stinger and the ATEM.</summary>
    public ReplayEngine Engine { get; set; } = ReplayEngine.Vmix;

    /// <summary>vMix connection and automation, used when <see cref="Engine"/> is <see cref="ReplayEngine.Vmix"/>.</summary>
    public VmixSettings Vmix { get; set; } = new();

    [JsonIgnore]
    public VideoFormat Format => Model.VideoFormat.Parse(VideoFormat);

    public static List<SourceSettings> DefaultSources()
    {
        var list = new List<SourceSettings>();
        for (var i = 1; i <= MaxCameraSources; i++)
            list.Add(new SourceSettings { SourceId = i, Name = $"Cam {i}", Enabled = i <= 4 });
        list.Add(new SourceSettings { SourceId = 0, Name = "Program", Role = SourceRole.Program, Enabled = false });
        return list;
    }

    /// <summary>Cameras in use: enables cameras 1..<paramref name="count"/> and turns the rest off, so only those record and show.</summary>
    public void SetCameraCount(int count)
    {
        count = Math.Clamp(count, 1, MaxCameraSources);
        Ui.CameraCount = count;
        foreach (var s in Sources.Where(s => s.Role == SourceRole.Camera))
            s.Enabled = s.SourceId <= count;
    }

    /// <summary>Returns a list of human-readable configuration problems. Empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        try { _ = Format; } catch (FormatException e) { problems.Add(e.Message); }

        var cams = Sources.Where(s => s.Role == SourceRole.Camera).ToList();
        if (cams.Count > MaxCameraSources) problems.Add($"At most {MaxCameraSources} camera sources are supported.");
        foreach (var dup in Sources.GroupBy(s => s.SourceId).Where(g => g.Count() > 1))
            problems.Add($"Source id {dup.Key} is used more than once.");
        foreach (var dup in Sources.Where(s => s.Enabled && !string.IsNullOrEmpty(s.DeviceId))
                     .GroupBy(s => (s.Backend, s.DeviceId)).Where(g => g.Count() > 1))
            problems.Add($"Device '{dup.Key.DeviceId}' is assigned to more than one source.");
        if (Atem.ReplayInput is < 1 or > 40) problems.Add("ATEM replay input must be between 1 and 40.");
        if (Engine == ReplayEngine.Vmix) problems.AddRange(Vmix.Validate());
        problems.AddRange(Midi.Validate());
        if (Scoreboard.Enabled)
        {
            if (!Uri.TryCreate(Scoreboard.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "file"))
                problems.Add("Scoreboard link must be a full http:// or https:// address.");
            if (Scoreboard.AtemInput is < 1 or > 40) problems.Add("Scoreboard ATEM input must be between 1 and 40.");
            if (Scoreboard.AtemInput == Atem.ReplayInput) problems.Add("Scoreboard and replay cannot use the same ATEM input.");
            if (!string.IsNullOrEmpty(Scoreboard.DisplayDevice) && Scoreboard.DisplayDevice == Output.DisplayDevice)
                problems.Add("Scoreboard and replay cannot use the same display output.");
            if (Scoreboard.ZoomPercent is < 25 or > 400) problems.Add("Scoreboard zoom must be 25-400 %.");
        }
        if (Buffer.Seconds is < 5 or > 3600) problems.Add("Buffer length must be between 5 and 3600 seconds.");
        if (Buffer.JpegQuality is < 30 or > 100) problems.Add("Recording quality must be between 30 and 100.");
        if (Remote.Port is < 1024 or > 65535) problems.Add("Remote port must be between 1024 and 65535.");
        if (Automation.TransitionIn == TransitionMode.EndoraStinger && string.IsNullOrWhiteSpace(Automation.StingerInFile))
            problems.Add("Endora stinger IN is selected but no stinger file is configured.");
        if (Automation.TransitionOut == TransitionMode.EndoraStinger && string.IsNullOrWhiteSpace(Automation.StingerOutFile ?? Automation.StingerInFile))
            problems.Add("Endora stinger OUT is selected but no stinger file is configured.");
        return problems;
    }

    // ---------- persistence ----------

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndoraReplay", "settings.json");

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        // Bindings added in newer versions get their defaults.
        foreach (var (action, gesture) in Input.KeyBindingDefaults.All)
            settings.KeyBindings.TryAdd(action, gesture);
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class UiSettings
{
    /// <summary>Number of cameras in use (1-8); the input tiles grow to fill the space.</summary>
    public int CameraCount { get; set; } = 4;

    /// <summary>Ask for the number of cameras each time Endora starts.</summary>
    public bool AskCameraCountOnStart { get; set; } = true;

    /// <summary>Operator buttons minimised to one compact row so the input previews are larger.</summary>
    public bool ControlsMinimized { get; set; }
}

public sealed class SourceSettings
{
    public int SourceId { get; set; }
    public string Name { get; set; } = "";
    public SourceRole Role { get; set; } = SourceRole.Camera;
    public bool Enabled { get; set; }
    public CaptureBackendKind Backend { get; set; } = CaptureBackendKind.MediaFoundation;

    /// <summary>Backend-specific device identifier chosen in Settings → Capture inputs.</summary>
    public string? DeviceId { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>Accept a source whose measured rate differs from the project format. Off by default.</summary>
    public bool AcceptModeMismatch { get; set; }
}

public sealed class BufferSettings
{
    /// <summary>Rolling buffer length per camera.</summary>
    public int Seconds { get; set; } = 120;

    /// <summary>JPEG quality used for the buffer (Draft 70 / Broadcast 90 / Max 97).</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>Folder for buffer files, clips, database and logs.</summary>
    public string DataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EndoraReplay");

    /// <summary>Estimated bytes per 1080p frame used to size buffer files.</summary>
    public int EstimatedFrameBytes { get; set; } = 400_000;

    /// <summary>Free space always kept on the replay drive.</summary>
    public int MinFreeDiskGb { get; set; } = 20;

    /// <summary>When free space falls below <see cref="MinFreeDiskGb"/>, delete the oldest clips (never one that is armed, playing or queued).</summary>
    public bool AutoDeleteOldClips { get; set; } = true;

    /// <summary>Skip clips with tags when deleting automatically.</summary>
    public bool KeepTaggedClips { get; set; }
}

public sealed class AtemSettings
{
    public AtemBackendKind Backend { get; set; } = AtemBackendKind.NetworkProtocol;
    public string Address { get; set; } = "192.168.10.240";
    public int MixEffectBlock { get; set; } = 1;

    /// <summary>ATEM input carrying the replay output. Default HDMI 8.</summary>
    public int ReplayInput { get; set; } = 8;

    public int ReconnectSeconds { get; set; } = 3;
}

public enum ReturnToLiveMode
{
    Automatic,
    Manual,
}

public sealed class AutomationSettings
{
    /// <summary>Master switch. When false Endora never sends switch commands except explicit operator LIVE.</summary>
    public bool Enabled { get; set; }

    /// <summary>REPLAY puts the replay input on Preview and transitions it to Program.</summary>
    public bool AutoSwitchIn { get; set; } = true;

    /// <summary>Start playback when Program changes to the replay input while a clip is armed (edge triggered).</summary>
    public bool StartOnProgramEdge { get; set; } = true;

    public ReturnToLiveMode ReturnToLive { get; set; } = ReturnToLiveMode.Automatic;

    public TransitionMode TransitionIn { get; set; } = TransitionMode.AtemAuto;
    public AtemTransitionStyle TransitionInStyle { get; set; } = AtemTransitionStyle.Mix;
    public TransitionMode TransitionOut { get; set; } = TransitionMode.AtemAuto;
    public AtemTransitionStyle TransitionOutStyle { get; set; } = AtemTransitionStyle.Mix;

    /// <summary>Full-frame stinger video (mp4/mov) for <see cref="TransitionMode.EndoraStinger"/>.</summary>
    public string? StingerInFile { get; set; }

    public string? StingerOutFile { get; set; }

    /// <summary>
    /// Show the whole stinger over the live picture with a downstream luma key of the replay input. The stinger
    /// video must be on a black background. The cover point where the ATEM cuts is found automatically.
    /// </summary>
    public bool StingerKey { get; set; } = true;

    /// <summary>The stinger always uses DSK 1. DSK 2 is left for the scoreboard.</summary>
    public const int StingerKeyer = 1;

    /// <summary>
    /// Hard luma key: only the stinger's black background (below 3 %) is transparent, everything else is fully
    /// opaque, so the stinger plays as drawn with no see-through or faded look.
    /// </summary>
    public const double StingerKeyClip = 3;

    public const double StingerKeyGain = 100;

    /// <summary>Frames after the cover point before Endora's output changes from the plain stinger to the blend (allows for the ATEM cut latency).</summary>
    public int StingerSwitchDelayFrames { get; set; } = 2;

    public int PrerollMs { get; set; } = 0;
    public int LockoutMs { get; set; } = 1500;
    public int WaitForProgramTimeoutMs { get; set; } = 5000;
}

public enum ReplayOutputKind
{
    /// <summary>Borderless full-screen window on a GPU display output connected to the ATEM.</summary>
    Display,

    /// <summary>DeckLink playback card. Requires SDK interop.</summary>
    DeckLink,

    /// <summary>SIMULATED output: frames only reach the on-screen confidence monitor.</summary>
    Simulated,
}

public enum HoldingFrameMode
{
    Slate,
    LastFrame,
    Black,
}

public sealed class OutputSettings
{
    public ReplayOutputKind Kind { get; set; } = ReplayOutputKind.Display;

    /// <summary>Windows display device name, e.g. \\.\DISPLAY2.</summary>
    public string? DisplayDevice { get; set; }

    public HoldingFrameMode Holding { get; set; } = HoldingFrameMode.Slate;

    /// <summary>Animated badge in the bottom-right corner of the replay output while a replay plays.</summary>
    public bool ReplayBadge { get; set; } = true;

    public string ReplayBadgeText { get; set; } = "REPLAY";

    /// <summary>Also send the replay playout on the network as an NDI source (needs the NDI Runtime).</summary>
    public bool NdiOutput { get; set; }

    public string NdiOutputName { get; set; } = "Endora Replay";
    public string? HoldingImageFile { get; set; }
}

/// <summary>
/// A scoreboard web page (the scoring service's overlay link) rendered full screen on its own HDMI output, wired to
/// an ATEM input and keyed over Program with DSK 2 or upstream key 1. Transparent parts of the page show as black and are keyed out.
/// </summary>
public enum ScoreboardKeyer
{
    /// <summary>Downstream key 2: on top of everything, including stingers.</summary>
    Dsk2,

    /// <summary>Upstream key 1 on M/E 1: keeps both DSKs free; it stays on air through cuts.</summary>
    UpstreamKey1,
}

public sealed class ScoreboardSettings
{
    public ScoreboardKeyer Keyer { get; set; } = ScoreboardKeyer.Dsk2;

    /// <summary>Take the scoreboard off air while a replay runs and put it back when the replay is over.</summary>
    public bool HideDuringReplay { get; set; } = true;

    /// <summary>Hard luma key: only black (the page's transparent background) is keyed out.</summary>
    public const double KeyClip = 3;

    public const double KeyGain = 100;

    public bool Enabled { get; set; }
    public string? Url { get; set; }

    /// <summary>Windows display (a third HDMI output) that shows the page, e.g. \\.\DISPLAY3.</summary>
    public string? DisplayDevice { get; set; }

    /// <summary>ATEM input that the scoreboard HDMI output is wired to.</summary>
    public int AtemInput { get; set; } = 7;

    /// <summary>Page zoom in percent, for pages designed at another size.</summary>
    public int ZoomPercent { get; set; } = 100;
}

public sealed class RemoteSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8686;
    public bool AllowTransport { get; set; } = true;

    /// <summary>Access code embedded in the QR link. Regenerated on demand.</summary>
    public string AccessCode { get; set; } = Remote.AccessCodeGenerator.Create();
}

public sealed class UpdateSettings
{
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>GitHub repository releases are downloaded from, e.g. https://github.com/owner/endora-replay.</summary>
    public string RepositoryUrl { get; set; } = "https://github.com/eapasindu-sudo/endora-replay";

    public bool IncludePrereleases { get; set; }
}
