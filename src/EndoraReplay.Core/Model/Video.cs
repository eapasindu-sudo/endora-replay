using System.Globalization;
using System.Text.RegularExpressions;

namespace EndoraReplay.Core.Model;

/// <summary>Frame codec of the bytes stored in the rolling buffer and clip files.</summary>
public enum FrameCodec : byte
{
    Jpeg = 1,
    Bgra32 = 2,
}

/// <summary>A progressive video format, e.g. 1080p50.</summary>
public sealed record VideoFormat(int Width, int Height, int FrameRateNumerator, int FrameRateDenominator = 1)
{
    public static readonly VideoFormat Hd1080p50 = new(1920, 1080, 50);
    public static readonly VideoFormat Hd1080p25 = new(1920, 1080, 25);
    public static readonly VideoFormat Hd1080p5994 = new(1920, 1080, 60000, 1001);
    public static readonly VideoFormat Hd1080p60 = new(1920, 1080, 60);
    public static readonly VideoFormat Hd1080p30 = new(1920, 1080, 30);
    public static readonly VideoFormat Hd720p50 = new(1280, 720, 50);

    public double FramesPerSecond => (double)FrameRateNumerator / FrameRateDenominator;

    /// <summary>Frame duration in <see cref="TimeSpan"/> ticks (100 ns).</summary>
    public long FrameDurationTicks => (long)Math.Round(TimeSpan.TicksPerSecond * (double)FrameRateDenominator / FrameRateNumerator);

    public string Name
    {
        get
        {
            var fps = FramesPerSecond;
            var fpsText = Math.Abs(fps - Math.Round(fps)) < 0.001
                ? ((int)Math.Round(fps)).ToString(CultureInfo.InvariantCulture)
                : fps.ToString("0.##", CultureInfo.InvariantCulture);
            return $"{Height}p{fpsText}";
        }
    }

    public override string ToString() => $"{Name} ({Width}x{Height})";

    private static readonly Regex NamePattern = new(@"^(?<h>\d{3,4})p(?<fps>\d+(\.\d+)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses names such as "1080p50", "720p59.94".</summary>
    public static VideoFormat Parse(string name)
    {
        var m = NamePattern.Match(name.Trim());
        if (!m.Success) throw new FormatException($"Unknown video format '{name}'. Use e.g. 1080p50.");
        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var w = h switch { 1080 => 1920, 720 => 1280, 2160 => 3840, 576 => 1024, _ => h * 16 / 9 };
        var fps = double.Parse(m.Groups["fps"].Value, CultureInfo.InvariantCulture);
        return fps switch
        {
            59.94 => new VideoFormat(w, h, 60000, 1001),
            29.97 => new VideoFormat(w, h, 30000, 1001),
            23.98 or 23.976 => new VideoFormat(w, h, 24000, 1001),
            _ => new VideoFormat(w, h, (int)Math.Round(fps)),
        };
    }

    /// <summary>True when the measured frame rate is within tolerance of this format.</summary>
    public bool MatchesRate(double measuredFps, double tolerance = 0.5) => Math.Abs(measuredFps - FramesPerSecond) <= tolerance;
}

/// <summary>One encoded video frame with its capture timestamp on the shared session clock.</summary>
public sealed class EncodedFrame
{
    public required int SourceId { get; init; }

    /// <summary>Monotonic per-source frame counter assigned by the capture worker.</summary>
    public required long FrameNumber { get; init; }

    /// <summary>Capture time on the shared session clock (<see cref="TimeSpan"/> ticks).</summary>
    public required long TimestampTicks { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required FrameCodec Codec { get; init; }
    public required byte[] Data { get; init; }

    /// <summary>Optional hardware timestamp/frame counter reported by the device (0 when unknown).</summary>
    public long DeviceFrameCounter { get; init; }
}

/// <summary>Result of validating a capture or output mode.</summary>
public sealed record ModeValidationResult(bool IsValid, string Message)
{
    public static ModeValidationResult Ok(string message = "OK") => new(true, message);
    public static ModeValidationResult Fail(string message) => new(false, message);
}
