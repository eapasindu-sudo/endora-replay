using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Abstractions;

/// <summary>Shared monotonic session clock. Every source is timestamped on this clock so angles line up.</summary>
public interface IClock
{
    /// <summary>Monotonic ticks (100 ns) since the session started.</summary>
    long NowTicks { get; }

    DateTime UtcNow { get; }
}

/// <summary>One running capture session for one device.</summary>
public interface ICaptureSource : IAsyncDisposable
{
    int SourceId { get; }
    CaptureDeviceInfo Device { get; }
    VideoFormat Format { get; }
    bool IsSimulated { get; }

    /// <summary>Raised on the capture worker thread. Handlers must not block.</summary>
    event Action<EncodedFrame>? FrameArrived;

    /// <summary>Raised when the device reports signal loss, an error, or recovery.</summary>
    event Action<SourceStatus, string?>? StatusChanged;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public sealed record CaptureOptions(int JpegQuality, IClock Clock);

/// <summary>A capture API (Media Foundation, DeckLink, simulated...).</summary>
public interface ICaptureBackend
{
    CaptureBackendKind Kind { get; }
    bool IsSimulated { get; }

    /// <summary>Non-null when the backend cannot be used on this machine (e.g. SDK not installed).</summary>
    string? UnavailableReason { get; }

    Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken);
    Task<ModeValidationResult> ValidateAsync(CaptureDeviceInfo device, VideoFormat format, CancellationToken cancellationToken);
    ICaptureSource Create(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options);
}

public enum OutputReadiness
{
    NotConfigured,
    Ready,
    ModeMismatch,
    DeviceMissing,
    Error,
}

/// <summary>The physical replay output feeding the ATEM replay input. Independent of the UI preview.</summary>
public interface IReplayOutput : IAsyncDisposable
{
    string Name { get; }
    bool IsSimulated { get; }
    OutputReadiness Readiness { get; }
    string ReadinessMessage { get; }
    event Action<OutputReadiness, string>? ReadinessChanged;

    Task OpenAsync(VideoFormat format, CancellationToken cancellationToken);
    ModeValidationResult ValidateMode(VideoFormat format);

    /// <summary>Last frame presented, or null while the holding frame is shown. Used by the confidence monitor.</summary>
    EncodedFrame? LastFrame { get; }

    /// <summary>Present one frame. Called from the playback thread at output frame rate.</summary>
    void Present(EncodedFrame frame);

    /// <summary>Show the configured holding frame so the output never goes invalid when idle.</summary>
    void ShowHolding();
}

/// <summary>Switcher control. Implementations: official SDK, network protocol, simulated.</summary>
public interface IAtemSwitcher : IAsyncDisposable
{
    AtemBackendKind Kind { get; }
    bool IsSimulated { get; }
    AtemSnapshot Snapshot { get; }

    event Action<AtemSnapshot>? StateChanged;
    event Action<string>? ConnectionLost;

    Task ConnectAsync(string address, int mixEffectBlock, CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task SetPreviewInputAsync(int input);
    Task SetNextTransitionStyleAsync(AtemTransitionStyle style);
    Task PerformAutoTransitionAsync();
    Task PerformCutAsync();
}

/// <summary>An ordered, random-access set of frames to play (from the rolling buffer or a saved clip).</summary>
public interface IFrameSequence
{
    int SourceId { get; }
    int Count { get; }
    long StartTicks { get; }
    long EndTicks { get; }

    /// <summary>Timestamp of frame <paramref name="index"/> on the session clock.</summary>
    long TicksAt(int index);

    /// <summary>Reads a frame. Throws <see cref="FrameUnavailableException"/> if it no longer exists; never substitutes.</summary>
    EncodedFrame Read(int index);
}

public sealed class FrameUnavailableException(string message) : Exception(message);
