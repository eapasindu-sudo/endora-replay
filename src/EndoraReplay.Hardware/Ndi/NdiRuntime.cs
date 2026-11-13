namespace EndoraReplay.Hardware.Ndi;

/// <summary>Whether the NDI® Runtime can be used on this PC. NDI® is a registered trademark of Vizrt NDI AB.</summary>
public static class NdiRuntime
{
    /// <summary>Null when NDI can be used, otherwise why not.</summary>
    public static string? UnavailableReason => NdiInterop.UnavailableReason;

    /// <summary>Looks for the runtime again, e.g. right after it was installed.</summary>
    public static void Retry() => NdiInterop.Retry();
}
