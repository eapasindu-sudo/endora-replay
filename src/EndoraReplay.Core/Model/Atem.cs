namespace EndoraReplay.Core.Model;

public enum AtemBackendKind
{
    /// <summary>Official Blackmagic ATEM Switchers SDK (COM). Needs the generated interop assembly, see docs/BUILD.md.</summary>
    BlackmagicSdk,

    /// <summary>Open-source LibAtem network protocol implementation (LGPL-3.0). Not the official SDK.</summary>
    NetworkProtocol,

    /// <summary>SIMULATED switcher for rehearsal. Never controls hardware.</summary>
    Simulated,
}

public enum AtemTransitionStyle
{
    Mix,
    Dip,
    Wipe,
    Dve,

    /// <summary>ATEM-side stinger. Requires a switcher with clip media players (not ATEM Mini models).</summary>
    Stinger,
}

/// <summary>How Endora moves Program onto or off the replay input.</summary>
public enum TransitionMode
{
    /// <summary>Send AUTO using the configured ATEM transition style.</summary>
    AtemAuto,

    /// <summary>Send CUT.</summary>
    AtemCut,

    /// <summary>
    /// Endora plays a full-frame stinger video on the replay output and cuts the ATEM at the stinger's
    /// cover frame. Works on switchers without a clip media pool (ATEM Mini Extreme).
    /// </summary>
    EndoraStinger,
}

/// <summary>Point-in-time view of the switcher, as last reported by the SDK.</summary>
public sealed record AtemSnapshot(
    bool Connected,
    string ModelName,
    int? ProgramInput,
    int? PreviewInput,
    bool InTransition,
    IReadOnlySet<int> ProgramTally,
    IReadOnlySet<int> PreviewTally,
    string? VideoMode,
    int? StingerCount = null,
    IReadOnlyDictionary<int, string>? InputNames = null,
    IReadOnlySet<int>? DownstreamKeysOnAir = null,
    IReadOnlySet<int>? UpstreamKeysOnAir = null)
{
    /// <summary>Display name of an input as configured on the switcher, e.g. "HDMI 8" or a custom label.</summary>
    public string InputName(int input) => InputNames is not null && InputNames.TryGetValue(input, out var n) && n.Length > 0 ? n : $"Input {input}";

    /// <summary>False when the switcher reported no stinger (clip) transition, e.g. ATEM Mini models.</summary>
    public bool SupportsAtemStinger => StingerCount is > 0;

    public bool IsDownstreamKeyOnAir(int keyer) => DownstreamKeysOnAir?.Contains(keyer) == true;

    public bool IsUpstreamKeyOnAir(int keyer) => UpstreamKeysOnAir?.Contains(keyer) == true;

    public static readonly AtemSnapshot Disconnected =
        new(false, "", null, null, false, new HashSet<int>(), new HashSet<int>(), null);
}
