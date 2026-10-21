using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Config;

/// <summary>Which program makes the replays.</summary>
public enum ReplayEngine
{
    /// <summary>Endora records the cameras and plays the replays itself (the full operator screen).</summary>
    Endora,

    /// <summary>
    /// vMix makes the replays. Endora only automates: it plays the vMix stinger and switches the ATEM, like a
    /// Companion setup but in one place. No cameras are recorded by Endora in this mode.
    /// </summary>
    Vmix,
}

/// <summary>How the vMix replay goes on and off air through the ATEM.</summary>
public enum VmixStingerMode
{
    /// <summary>
    /// Full stinger both ways. The vMix output (black when idle) is luma-keyed on ATEM DSK 1 over the live picture,
    /// so the whole stinger is seen over live; the ATEM cuts underneath at the stinger's transition point.
    /// Needs a black input in vMix and a stinger with a transparent (black) background.
    /// </summary>
    Keyed,

    /// <summary>The ATEM cuts to (and from) vMix at the stinger's transition point. Viewers see the stinger from the moment it covers the picture.</summary>
    CoverCut,

    /// <summary>No vMix stinger: vMix cuts to the replay and the ATEM goes to vMix with AUTO or CUT.</summary>
    AtemOnly,
}

public sealed class VmixSettings
{
    /// <summary>vMix PC address. 127.0.0.1 when vMix runs on this PC.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>vMix TCP API port (Settings → Web Controller in vMix; the TCP API is always on 8099).</summary>
    public int Port { get; set; } = 8099;

    /// <summary>vMix input that plays the replay: its number or its title. Empty = the first Replay input in vMix.</summary>
    public string ReplayInput { get; set; } = "";

    /// <summary>
    /// vMix input shown when no replay is on air (a black Colour input). Needed for <see cref="VmixStingerMode.Keyed"/>;
    /// in the other modes it is where vMix goes after the replay (empty = back to what vMix showed before).
    /// </summary>
    public string BlackInput { get; set; } = "";

    public VmixStingerMode Mode { get; set; } = VmixStingerMode.Keyed;

    /// <summary>vMix transition used into the replay, e.g. Stinger1, Stinger2, Fade, Merge.</summary>
    public string TransitionIn { get; set; } = "Stinger1";

    /// <summary>vMix transition used back to live.</summary>
    public string TransitionOut { get; set; } = "Stinger1";

    /// <summary>
    /// Time from the start of the vMix stinger to the ATEM cut. Set it to the stinger's transition point in vMix
    /// (the moment the stinger fully covers the picture). Fine-tune it here for the HDMI delay.
    /// </summary>
    public int CutPointMs { get; set; } = 500;

    /// <summary>Full length of the stinger. DSK 1 stays on until it has finished on the way back to live.</summary>
    public int StingerLengthMs { get; set; } = 1000;

    /// <summary>Wait after putting black on vMix before the key goes on air, so nothing flashes over live.</summary>
    public int BlackLeadMs { get; set; } = 120;

    /// <summary>
    /// Start the automation by itself when the vMix replay starts playing (play pressed in vMix): Endora plays the
    /// stinger into the replay and switches the ATEM, with no button in Endora.
    /// </summary>
    public bool StartWhenReplayPlays { get; set; } = true;

    /// <summary>Go back to live by itself when the vMix replay stops playing.</summary>
    public bool AutoReturnWhenReplayStops { get; set; } = true;

    /// <summary>How long the replay must stay stopped before Endora goes back to live (avoids pauses while jogging).</summary>
    public int StopGraceMs { get; set; } = 600;

    /// <summary>When vMix itself takes the replay on or off Program (an operator in vMix), switch the ATEM to follow.</summary>
    public bool FollowVmixProgram { get; set; } = true;

    /// <summary>
    /// Extra vMix functions sent when REPLAY is pressed, before the stinger, one per line: the function name, then
    /// optionally a space and its query string, e.g. "ReplayPlay" or "SetRate Input=3&amp;Value=0.5".
    /// </summary>
    public List<string> FunctionsOnReplay { get; set; } = [];

    /// <summary>Extra vMix functions sent after the replay has gone off air.</summary>
    public List<string> FunctionsOnLive { get; set; } = [];

    /// <summary><see cref="VmixStingerMode.AtemOnly"/>: ATEM transition style, or CUT when <see cref="AtemOnlyCut"/>.</summary>
    public AtemTransitionStyle AtemOnlyStyle { get; set; } = AtemTransitionStyle.Mix;

    public bool AtemOnlyCut { get; set; }

    public int ReconnectSeconds { get; set; } = 3;

    /// <summary>
    /// Optional Bitfocus Companion address (e.g. http://127.0.0.1:8000). When set, Endora writes its state into the
    /// Companion custom variables endora_state, endora_atem and endora_vmix so Stream Deck buttons can show it.
    /// </summary>
    public string CompanionUrl { get; set; } = "";

    /// <summary>Returns problems for the settings window. Empty means usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) p.Add("vMix address is empty.");
        if (Port is < 1 or > 65535) p.Add("vMix port must be 1-65535.");
        if (Mode == VmixStingerMode.Keyed && string.IsNullOrWhiteSpace(BlackInput))
            p.Add("Keyed stinger needs a black input in vMix (add a Colour input set to black and enter its number or title).");
        if (Mode != VmixStingerMode.AtemOnly)
        {
            if (string.IsNullOrWhiteSpace(TransitionIn) || string.IsNullOrWhiteSpace(TransitionOut)) p.Add("vMix transition names cannot be empty.");
            if (CutPointMs is < 0 or > 10_000) p.Add("Cut point must be 0-10000 ms.");
            if (StingerLengthMs < CutPointMs) p.Add("Stinger length must be at least the cut point.");
        }
        if (BlackLeadMs is < 0 or > 2000) p.Add("Black lead must be 0-2000 ms.");
        if (StopGraceMs is < 0 or > 10_000) p.Add("Stop wait must be 0-10000 ms.");
        if (!string.IsNullOrWhiteSpace(CompanionUrl) && !Uri.TryCreate(CompanionUrl, UriKind.Absolute, out _))
            p.Add("Companion address must look like http://127.0.0.1:8000.");
        return p;
    }
}
