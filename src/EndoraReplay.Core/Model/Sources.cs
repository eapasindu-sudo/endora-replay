namespace EndoraReplay.Core.Model;

public enum CaptureBackendKind
{
    /// <summary>Windows Media Foundation (USB UVC capture devices). Hardware path.</summary>
    MediaFoundation,

    /// <summary>DirectShow (legacy USB capture devices). Hardware path.</summary>
    DirectShow,

    /// <summary>Blackmagic DeckLink / UltraStudio through the Desktop Video SDK. Requires SDK interop (see docs).</summary>
    DeckLink,

    /// <summary>SIMULATED test pattern. Never a production source.</summary>
    Simulated,
}

public enum SourceRole
{
    Camera,

    /// <summary>The final programme feed (e.g. ATEM HDMI out) shown in the PROGRAM monitor.</summary>
    Program,
}

public enum SourceStatus
{
    Stopped,
    Starting,
    Signal,
    NoSignal,
    Dropping,
    ModeMismatch,
    Reconnecting,
    Error,
}

/// <summary>A capture device found by a backend.</summary>
public sealed record CaptureDeviceInfo(
    string DeviceId,
    string Name,
    CaptureBackendKind Backend,
    IReadOnlyList<VideoFormat> SupportedFormats)
{
    public bool IsSimulated => Backend == CaptureBackendKind.Simulated;
    public override string ToString() => IsSimulated ? $"{Name} [SIMULATED]" : $"{Name} ({Backend})";
}

/// <summary>Live health counters for one capture source. Mutated only by the capture service.</summary>
public sealed class SourceHealth
{
    public SourceHealth(int sourceId, string name, bool isSimulated)
    {
        SourceId = sourceId;
        Name = name;
        IsSimulated = isSimulated;
    }

    public int SourceId { get; }
    public string Name { get; }
    public bool IsSimulated { get; }

    public SourceStatus Status { get; set; } = SourceStatus.Stopped;
    public double MeasuredFps { get; set; }
    public long FramesCaptured { get; set; }
    public long DroppedFrames { get; set; }
    public long TimestampGaps { get; set; }
    public long BufferOverflows { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastFrameUtc { get; set; }
    public int ReconnectAttempts { get; set; }

    public SourceHealth Clone() => (SourceHealth)MemberwiseClone();
}
