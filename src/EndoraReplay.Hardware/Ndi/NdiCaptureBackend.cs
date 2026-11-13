using System.Runtime.InteropServices;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using OpenCvSharp;

namespace EndoraReplay.Hardware.Ndi;

/// <summary>
/// Camera or Program input from an NDI® source on the network (vMix, OBS, NDI cameras, NDI Tools Screen Capture...).
/// Frames are timestamped on arrival with the shared session clock, like USB inputs. NDI adds network latency
/// (typically 1–3 frames), so angles from NDI and USB may be offset by that much.
/// UNTESTED ON HARDWARE.
/// </summary>
public sealed class NdiCaptureBackend : ICaptureBackend
{
    public CaptureBackendKind Kind => CaptureBackendKind.Ndi;
    public bool IsSimulated => false;
    public string? UnavailableReason => NdiInterop.UnavailableReason;

    public Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<CaptureDeviceInfo>>(() =>
        NdiInterop.FindSources(2500).Select(n => new CaptureDeviceInfo(n, n, CaptureBackendKind.Ndi, [])).ToList(), cancellationToken);

    public Task<ModeValidationResult> ValidateAsync(CaptureDeviceInfo device, VideoFormat format, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (UnavailableReason is { } why) return ModeValidationResult.Fail(why);
        using var rx = NdiReceiver.Connect(device.DeviceId);
        var until = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < until && !cancellationToken.IsCancellationRequested)
        {
            if (!rx.TryCapture(500, out var f)) continue;
            var fps = f.FrameRateD == 0 ? 0 : (double)f.FrameRateN / f.FrameRateD;
            rx.Free(ref f);
            if (fps > 0 && !format.MatchesRate(fps, 0.6))
                return ModeValidationResult.Fail($"{device.Name}: NDI source runs at {fps:0.##} fps, project is {format.FramesPerSecond:0.##} fps. Set the sender's NDI output to {format.FramesPerSecond:0.##} fps.");
            // A different picture size is scaled to the project size (aspect kept, black bars), so any NDI output works.
            var scaled = f.Width != format.Width || f.Height != format.Height ? $", scaled to {format.Width}x{format.Height}" : "";
            return ModeValidationResult.Ok($"{device.Name}: {f.Width}x{f.Height} @ {fps:0.##} fps (NDI{scaled})");
        }
        return ModeValidationResult.Fail($"{device.Name}: no video from this NDI source within 5 s.");
    }, cancellationToken);

    public ICaptureSource Create(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) =>
        new NdiCaptureSource(sourceId, device, format, options);
}

/// <summary>One NDI receiver connection.</summary>
internal sealed class NdiReceiver : IDisposable
{
    private IntPtr _recv;
    private IntPtr _name;

    private NdiReceiver(IntPtr recv, IntPtr name)
    {
        _recv = recv;
        _name = name;
    }

    public readonly record struct Frame(int Width, int Height, int Stride, int FourCC, int FrameRateN, int FrameRateD, IntPtr Data, NdiInterop.VideoFrameV2 Raw);

    public static NdiReceiver Connect(string sourceName)
    {
        if (!NdiInterop.EnsureInitialized()) throw new IOException(NdiInterop.UnavailableReason);
        var name = Marshal.StringToCoTaskMemUTF8(sourceName);
        var recvName = Marshal.StringToCoTaskMemUTF8("Endora Replay");
        try
        {
            var settings = new NdiInterop.RecvCreateV3
            {
                source_to_connect_to = new NdiInterop.Source { p_ndi_name = name },
                color_format = NdiInterop.RecvColorFormat.BGRX_BGRA,
                bandwidth = NdiInterop.RecvBandwidth.Highest,
                allow_video_fields = false,
                p_ndi_recv_name = recvName,
            };
            var recv = NdiInterop.NDIlib_recv_create_v3(ref settings);
            if (recv == IntPtr.Zero) throw new IOException($"Could not create an NDI receiver for '{sourceName}'.");
            return new NdiReceiver(recv, name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(recvName);
        }
    }

    public bool TryCapture(uint timeoutMs, out Frame frame)
    {
        var v = new NdiInterop.VideoFrameV2();
        var t = NdiInterop.NDIlib_recv_capture_v2(_recv, ref v, IntPtr.Zero, IntPtr.Zero, timeoutMs);
        if (t != NdiInterop.FrameType.Video)
        {
            frame = default;
            return false;
        }
        frame = new Frame(v.xres, v.yres, v.line_stride_in_bytes, v.FourCC, v.frame_rate_N, v.frame_rate_D, v.p_data, v);
        return true;
    }

    public void Free(ref Frame frame)
    {
        var raw = frame.Raw;
        NdiInterop.NDIlib_recv_free_video_v2(_recv, ref raw);
    }

    public void Dispose()
    {
        if (_recv != IntPtr.Zero) NdiInterop.NDIlib_recv_destroy(_recv);
        _recv = IntPtr.Zero;
        if (_name != IntPtr.Zero) Marshal.FreeCoTaskMem(_name);
        _name = IntPtr.Zero;
    }
}

internal sealed class NdiCaptureSource(int sourceId, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options) : ICaptureSource
{
    private Thread? _thread;
    private volatile bool _stop;

    public int SourceId { get; } = sourceId;
    public CaptureDeviceInfo Device { get; } = device;
    public VideoFormat Format { get; } = format;
    public bool IsSimulated => false;

    public event Action<EncodedFrame>? FrameArrived;
    public event Action<SourceStatus, string?>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stop = false;
        _thread = new Thread(() => Run(opened)) { IsBackground = true, Name = $"NDI src{SourceId}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return opened.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private void Run(TaskCompletionSource opened)
    {
        NdiReceiver rx;
        try { rx = NdiReceiver.Connect(Device.DeviceId); }
        catch (Exception e)
        {
            opened.TrySetException(e);
            return;
        }
        opened.TrySetResult();
        using var _ = rx;
        var encode = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, options.JpegQuality) };
        long n = 0;
        var signal = false;
        var misses = 0;
        using var bgr = new Mat();
        using var fitted = new Mat();
        while (!_stop)
        {
            if (!rx.TryCapture(200, out var f))
            {
                if (++misses == 5 && signal)
                {
                    signal = false;
                    StatusChanged?.Invoke(SourceStatus.NoSignal, "No video from the NDI source.");
                }
                continue;
            }
            var ticks = options.Clock.NowTicks;
            misses = 0;
            try
            {
                if (!signal)
                {
                    signal = true;
                    StatusChanged?.Invoke(SourceStatus.Signal, null);
                }
                if (f.FourCC != NdiInterop.FourCC_BGRX && f.FourCC != NdiInterop.FourCC_BGRA)
                {
                    StatusChanged?.Invoke(SourceStatus.Error, $"Unexpected NDI pixel format 0x{f.FourCC:X8}.");
                    continue;
                }
                using (var bgra = Mat.FromPixelData(f.Height, f.Width, MatType.CV_8UC4, f.Data, f.Stride))
                    Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            }
            finally
            {
                rx.Free(ref f);
            }
            var outFrame = bgr;
            if (bgr.Width != Format.Width || bgr.Height != Format.Height)
            {
                FitInto(bgr, fitted, Format.Width, Format.Height);
                outFrame = fitted;
            }
            Cv2.ImEncode(".jpg", outFrame, out var jpeg, encode);
            FrameArrived?.Invoke(new EncodedFrame
            {
                SourceId = SourceId, FrameNumber = n++, TimestampTicks = ticks,
                Width = outFrame.Width, Height = outFrame.Height, Codec = FrameCodec.Jpeg, Data = jpeg,
            });
        }
    }

    /// <summary>Scales <paramref name="src"/> into a w×h black frame, keeping its aspect ratio.</summary>
    private static void FitInto(Mat src, Mat dst, int w, int h)
    {
        var scale = Math.Min((double)w / src.Width, (double)h / src.Height);
        var sw = Math.Max(1, (int)Math.Round(src.Width * scale));
        var sh = Math.Max(1, (int)Math.Round(src.Height * scale));
        dst.Create(h, w, MatType.CV_8UC3);
        dst.SetTo(Scalar.Black);
        using var roi = new Mat(dst, new Rect((w - sw) / 2, (h - sh) / 2, sw, sh));
        Cv2.Resize(src, roi, new Size(sw, sh), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
    }

    public Task StopAsync()
    {
        _stop = true;
        var t = _thread;
        return t is null ? Task.CompletedTask : Task.Run(() => t.Join(TimeSpan.FromSeconds(3)));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
