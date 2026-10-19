using System.Management;
using System.Runtime.Versioning;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Capture;

/// <summary>
/// USB/UVC capture through OpenCV's Media Foundation (MSMF) or DirectShow (DSHOW) backends.
/// Device id is the API's device index. HARDWARE PATH: must be validated on the target PC and capture devices
/// (see docs/HARDWARE_VALIDATION.md). Cheap USB HDMI dongles often cannot run several 1080p50 streams on one
/// USB root hub; Endora reports drops rather than hiding them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OpenCvCaptureBackend(CaptureBackendKind kind) : ICaptureBackend
{
    private const int MaxProbeIndex = 10;

    public CaptureBackendKind Kind { get; } = kind is CaptureBackendKind.MediaFoundation or CaptureBackendKind.DirectShow
        ? kind
        : throw new ArgumentException("OpenCV backend supports MediaFoundation or DirectShow.", nameof(kind));

    public bool IsSimulated => false;
    public string? UnavailableReason => OperatingSystem.IsWindows() ? null : "Windows only.";

    internal VideoCaptureAPIs Api => Kind == CaptureBackendKind.MediaFoundation ? VideoCaptureAPIs.MSMF : VideoCaptureAPIs.DSHOW;

    public Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
    {
        var names = FriendlyNames();
        var found = new List<CaptureDeviceInfo>();
        var misses = 0;
        for (var i = 0; i < MaxProbeIndex && misses < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cap = new VideoCapture(i, Api);
            if (!cap.IsOpened())
            {
                misses++;
                continue;
            }
            misses = 0;
            var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
            var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
            var fps = cap.Get(VideoCaptureProperties.Fps);
            // Index order normally follows device enumeration order; names are a hint until confirmed by the preview.
            var name = i < names.Count ? names[i] : $"Video device {i}";
            var fmt = new VideoFormat(w, h, Math.Max(1, (int)Math.Round(fps)));
            found.Add(new CaptureDeviceInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture), name, Kind, [fmt]));
        }
        return found;
    }, cancellationToken);

    public Task<ModeValidationResult> ValidateAsync(CaptureDeviceInfo device, VideoFormat format, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!int.TryParse(device.DeviceId, out var index)) return ModeValidationResult.Fail($"Invalid device id '{device.DeviceId}'.");
        using var cap = new VideoCapture(index, Api);
        if (!cap.IsOpened()) return ModeValidationResult.Fail($"{device.Name}: device could not be opened (in use or unplugged?).");
        OpenCvCaptureSource.Configure(cap, format);
        var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        var fps = cap.Get(VideoCaptureProperties.Fps);
        if (w != format.Width || h != format.Height)
            return ModeValidationResult.Fail($"{device.Name}: device negotiated {w}x{h}, project needs {format.Width}x{format.Height}.");
        // Some drivers report 0 fps; the capture service measures the real rate once running.
        if (fps > 0 && !format.MatchesRate(fps, 0.6))
            return ModeValidationResult.Fail($"{device.Name}: device reports {fps:0.##} fps, project is {format.FramesPerSecond:0.##} fps.");
        return ModeValidationResult.Ok($"{device.Name}: {w}x{h} @ {(fps > 0 ? fps.ToString("0.##") : "?")} fps");
    }, cancellationToken);

    public ICaptureSource Create(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) =>
        new OpenCvCaptureSource(sourceId, device, format, options, Api);

    private static List<string> FriendlyNames()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            return searcher.Get().Cast<ManagementObject>().Select(o => o["Name"]?.ToString() ?? "Video device").ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>One capture session: a dedicated thread reads, timestamps and JPEG-encodes frames.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OpenCvCaptureSource : ICaptureSource
{
    private readonly CaptureOptions _options;
    private readonly VideoCaptureAPIs _api;
    private Thread? _thread;
    private volatile bool _stop;

    public OpenCvCaptureSource(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options, VideoCaptureAPIs api)
    {
        SourceId = sourceId;
        Device = device;
        Format = format;
        _options = options;
        _api = api;
    }

    public int SourceId { get; }
    public CaptureDeviceInfo Device { get; }
    public VideoFormat Format { get; }
    public bool IsSimulated => false;

    public event Action<EncodedFrame>? FrameArrived;
    public event Action<SourceStatus, string?>? StatusChanged;

    internal static void Configure(VideoCapture cap, VideoFormat format)
    {
        // MJPG lets USB 2/3 dongles reach 1080p50; drivers that cannot do it fall back to their default.
        cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        cap.Set(VideoCaptureProperties.FrameWidth, format.Width);
        cap.Set(VideoCaptureProperties.FrameHeight, format.Height);
        cap.Set(VideoCaptureProperties.Fps, format.FramesPerSecond);
        cap.Set(VideoCaptureProperties.BufferSize, 2);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Device.DeviceId, out var index)) throw new ArgumentException($"Invalid device id '{Device.DeviceId}'.");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stop = false;
        _thread = new Thread(() => Run(index, opened)) { IsBackground = true, Name = $"Capture src{SourceId}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return opened.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private void Run(int index, TaskCompletionSource opened)
    {
        using var cap = new VideoCapture(index, _api);
        if (!cap.IsOpened())
        {
            opened.TrySetException(new IOException($"{Device.Name}: could not open device {index}."));
            return;
        }
        Configure(cap, Format);
        opened.TrySetResult();

        using var mat = new Mat();
        var encodeParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, _options.JpegQuality) };
        long frameNumber = 0;
        var consecutiveFailures = 0;
        var signal = false;

        while (!_stop)
        {
            bool ok;
            try { ok = cap.Read(mat) && !mat.Empty(); }
            catch (Exception e)
            {
                StatusChanged?.Invoke(SourceStatus.Error, e.Message);
                return;
            }
            var ticks = _options.Clock.NowTicks;

            if (!ok)
            {
                if (++consecutiveFailures == 10)
                {
                    signal = false;
                    StatusChanged?.Invoke(SourceStatus.NoSignal, "Device returned no frames.");
                }
                if (consecutiveFailures > 200)
                {
                    StatusChanged?.Invoke(SourceStatus.Error, "Device stopped delivering frames.");
                    return;
                }
                Thread.Sleep(5);
                continue;
            }
            consecutiveFailures = 0;
            if (!signal)
            {
                signal = true;
                StatusChanged?.Invoke(SourceStatus.Signal, null);
            }

            Cv2.ImEncode(".jpg", mat, out var jpeg, encodeParams);
            FrameArrived?.Invoke(new EncodedFrame
            {
                SourceId = SourceId,
                FrameNumber = frameNumber++,
                TimestampTicks = ticks,
                Width = mat.Width,
                Height = mat.Height,
                Codec = FrameCodec.Jpeg,
                Data = jpeg,
            });
        }
    }

    public Task StopAsync()
    {
        _stop = true;
        var t = _thread;
        return t is null ? Task.CompletedTask : Task.Run(() => t.Join(TimeSpan.FromSeconds(3)));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
