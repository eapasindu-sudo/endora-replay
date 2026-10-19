using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Simulation;
using EndoraReplay.Hardware.Atem;
using EndoraReplay.Hardware.Capture;

namespace EndoraReplay.Hardware;

/// <summary>Creates the hardware (and clearly-labelled simulated) implementations used by the app.</summary>
public static class HardwareFactory
{
    public const string DeckLinkUnavailable =
        "DeckLink capture/playback needs the Blackmagic Desktop Video SDK interop, which is not part of this build. See docs/COMPATIBILITY.md.";

    public static IReadOnlyList<ICaptureBackend> CaptureBackends() =>
    [
        new OpenCvCaptureBackend(CaptureBackendKind.MediaFoundation),
        new OpenCvCaptureBackend(CaptureBackendKind.DirectShow),
        new UnavailableCaptureBackend(CaptureBackendKind.DeckLink, DeckLinkUnavailable),
        new SimulatedCaptureBackend(),
    ];

    public static IAtemSwitcher CreateSwitcher(AtemBackendKind kind) => kind switch
    {
        AtemBackendKind.BlackmagicSdk => new BlackmagicSdkSwitcher(),
        AtemBackendKind.NetworkProtocol => new LibAtemSwitcher(),
        _ => new SimulatedAtemSwitcher(),
    };

    public static string? SwitcherUnavailableReason(AtemBackendKind kind) =>
        kind == AtemBackendKind.BlackmagicSdk && !BlackmagicSdkSwitcher.IsAvailable ? BlackmagicSdkSwitcher.UnavailableReason : null;
}
