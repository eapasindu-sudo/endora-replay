using System.Diagnostics;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Capture;

/// <summary>
/// SIMULATED capture: generated test pattern with a moving marker and burnt-in source/timecode labels.
/// For rehearsal and UI testing only. Every frame says SIMULATED so it can never pass as a camera.
/// </summary>
public sealed class SimulatedCaptureBackend : ICaptureBackend
{
    public CaptureBackendKind Kind => CaptureBackendKind.Simulated;
    public bool IsSimulated => true;
    public string? UnavailableReason => null;

    public Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>(
            Enumerable.Range(1, 8).Select(i => new CaptureDeviceInfo($"sim-{i}", $"Test pattern {i}", CaptureBackendKind.Simulated, [VideoFormat.Hd1080p50])).ToList());

    public Task<ModeValidationResult> ValidateAsync(CaptureDeviceInfo device, VideoFormat format, CancellationToken cancellationToken) =>
        Task.FromResult(ModeValidationResult.Ok("SIMULATED source generates any format."));

    public ICaptureSource Create(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) =>
        new SimulatedCaptureSource(sourceId, device, format, options);
}

internal sealed class SimulatedCaptureSource(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) : ICaptureSource
{
    private Thread? _thread;
    private volatile bool _stop;

    public int SourceId { get; } = sourceId;
    public CaptureDeviceInfo Device { get; } = device;
    public VideoFormat Format { get; } = format;
    public bool IsSimulated => true;

    public event Action<EncodedFrame>? FrameArrived;
    public event Action<SourceStatus, string?>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Simulated src{SourceId}" };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void Run()
    {
        var w = Format.Width;
        var h = Format.Height;
        using var bg = new Mat(h, w, MatType.CV_8UC3);
        Scalar[] bars = [new(192, 192, 192), new(0, 192, 192), new(192, 192, 0), new(0, 192, 0), new(192, 0, 192), new(0, 0, 192), new(192, 0, 0)];
        for (var i = 0; i < bars.Length; i++)
            bg[new Rect(i * w / bars.Length, 0, w / bars.Length + 1, h * 2 / 3)].SetTo(bars[i]);
        bg[new Rect(0, h * 2 / 3, w, h - h * 2 / 3)].SetTo(new Scalar(30, 30, 30));

        using var frame = new Mat();
        var encode = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, options.JpegQuality) };
        var period = Format.FrameDurationTicks;
        var sw = Stopwatch.StartNew();
        long n = 0;
        StatusChanged?.Invoke(SourceStatus.Signal, "SIMULATED");

        while (!_stop)
        {
            bg.CopyTo(frame);
            var x = (int)(n * 12 % (w - 120));
            Cv2.Rectangle(frame, new Rect(x, h * 2 / 3 + 40, 120, 120), new Scalar(26, 106, 255), -1);
            Cv2.PutText(frame, $"CAM {SourceId}  SIMULATED", new Point(60, h * 2 / 3 + 260), HersheyFonts.HersheyDuplex, 3.0, Scalar.White, 4);
            var t = TimeSpan.FromTicks(options.Clock.NowTicks);
            Cv2.PutText(frame, $"{t:hh\\:mm\\:ss}:{(int)(t.Ticks % TimeSpan.TicksPerSecond / period):00}  #{n}",
                new Point(60, h - 40), HersheyFonts.HersheyDuplex, 2.0, Scalar.White, 3);

            Cv2.ImEncode(".jpg", frame, out var jpeg, encode);
            FrameArrived?.Invoke(new EncodedFrame
            {
                SourceId = SourceId, FrameNumber = n, TimestampTicks = options.Clock.NowTicks,
                Width = w, Height = h, Codec = FrameCodec.Jpeg, Data = jpeg,
            });
            n++;
            var due = n * period;
            while (!_stop && sw.Elapsed.Ticks < due)
            {
                var rem = due - sw.Elapsed.Ticks;
                if (rem > TimeSpan.TicksPerMillisecond * 2) Thread.Sleep(1); else Thread.SpinWait(50);
            }
        }
    }

    public Task StopAsync()
    {
        _stop = true;
        var t = _thread;
        return t is null ? Task.CompletedTask : Task.Run(() => t.Join(TimeSpan.FromSeconds(2)));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>Placeholder entry for DeckLink so the UI can explain why it is not offered. Not a capture implementation.</summary>
public sealed class UnavailableCaptureBackend(CaptureBackendKind kind, string reason) : ICaptureBackend
{
    public CaptureBackendKind Kind { get; } = kind;
    public bool IsSimulated => false;
    public string? UnavailableReason => reason;
    public Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CaptureDeviceInfo>>([]);
    public Task<ModeValidationResult> ValidateAsync(CaptureDeviceInfo device, VideoFormat format, CancellationToken cancellationToken) => Task.FromResult(ModeValidationResult.Fail(reason));
    public ICaptureSource Create(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) => throw new NotSupportedException(reason);
}
