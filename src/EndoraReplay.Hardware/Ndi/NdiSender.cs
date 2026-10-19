using System.Runtime.InteropServices;
using EndoraReplay.Core.Model;
using EndoraReplay.Hardware.Video;

namespace EndoraReplay.Hardware.Ndi;

/// <summary>
/// Sends the replay playout as an NDI source. A background thread decodes the newest frame and sends it at the
/// project frame rate, so a slow network never stalls playback. NDI® is a registered trademark of Vizrt NDI AB.
/// HARDWARE PATH: needs the NDI Runtime installed on the PC and has not been tested on the target network.
/// </summary>
public sealed class NdiSender : IDisposable
{
    private readonly VideoFormat _format;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private EncodedFrame? _frame;
    private BgraImage? _image;
    private IntPtr _sender;

    public string Name { get; }

    /// <summary>Why NDI cannot run on this PC (runtime missing), or null when it can.</summary>
    public static string? UnavailableReason => NdiInterop.UnavailableReason;

    public NdiSender(string name, VideoFormat format)
    {
        if (!NdiInterop.EnsureInitialized()) throw new InvalidOperationException(NdiInterop.UnavailableReason ?? "NDI runtime unavailable.");
        Name = name;
        _format = format;
        var namePtr = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var settings = new NdiInterop.SendCreate { p_ndi_name = namePtr, clock_video = true };
            _sender = NdiInterop.NDIlib_send_create(ref settings);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
        if (_sender == IntPtr.Zero) throw new InvalidOperationException($"NDI sender '{name}' could not be created.");
        _thread = new Thread(Run) { IsBackground = true, Name = "NDI send" };
        _thread.Start();
    }

    /// <summary>Queue a playout frame; only the newest is kept.</summary>
    public void Send(EncodedFrame frame)
    {
        Interlocked.Exchange(ref _image, null);
        Interlocked.Exchange(ref _frame, frame);
        _wake.Set();
    }

    /// <summary>Queue a still picture (the holding frame), repeated until the next frame.</summary>
    public void SendImage(BgraImage image)
    {
        Interlocked.Exchange(ref _frame, null);
        Interlocked.Exchange(ref _image, image);
        _wake.Set();
    }

    private void Run()
    {
        // clock_video makes send_video block to the frame rate, so a still repeats at the right pace.
        BgraImage? current = null;
        while (!_stop)
        {
            _wake.WaitOne(current is null ? 200 : 0);
            if (_stop) break;
            try
            {
                if (Interlocked.Exchange(ref _frame, null) is { } f) current = JpegCodec.Decode(f);
                else if (Interlocked.Exchange(ref _image, null) is { } img) current = img;
                if (current is null) continue;
                SendOne(current);
            }
            catch (Exception)
            {
                // A bad frame is skipped; the next one is sent.
                Thread.Sleep(5);
            }
        }
    }

    private void SendOne(BgraImage img)
    {
        var handle = GCHandle.Alloc(img.Pixels, GCHandleType.Pinned);
        try
        {
            var v = new NdiInterop.VideoFrameV2
            {
                xres = img.Width,
                yres = img.Height,
                FourCC = NdiInterop.FourCC_BGRA,
                frame_rate_N = _format.FrameRateNumerator,
                frame_rate_D = _format.FrameRateDenominator,
                picture_aspect_ratio = (float)img.Width / img.Height,
                frame_format_type = NdiInterop.FrameFormatProgressive,
                timecode = NdiInterop.SendTimecodeSynthesize,
                p_data = handle.AddrOfPinnedObject(),
                line_stride_in_bytes = img.Stride,
            };
            NdiInterop.NDIlib_send_send_video_v2(_sender, ref v);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_sender != IntPtr.Zero) NdiInterop.NDIlib_send_destroy(_sender);
        _sender = IntPtr.Zero;
        _wake.Dispose();
    }
}
