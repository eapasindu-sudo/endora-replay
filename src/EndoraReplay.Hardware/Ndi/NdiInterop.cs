using System.Reflection;
using System.Runtime.InteropServices;

namespace EndoraReplay.Hardware.Ndi;

/// <summary>
/// Minimal P/Invoke surface of the NDI® SDK (receive, find, send). The NDI Runtime is NOT shipped with Endora: the user
/// installs it (NDI Tools or the NDI Runtime), which sets NDI_RUNTIME_DIR_V6/V5. NDI® is a registered trademark of Vizrt NDI AB.
/// UNTESTED ON HARDWARE: written against the documented NDI 6 C API; validate with docs/HARDWARE_VALIDATION.md.
/// </summary>
internal static class NdiInterop
{
    private const string Lib = "Processing.NDI.Lib.x64.dll";
    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _loadError;

    static NdiInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4" })
        {
            var dir = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(dir)) continue;
            var file = Path.Combine(dir, Lib);
            if (File.Exists(file) && NativeLibrary.TryLoad(file, out var h)) return h;
        }
        return NativeLibrary.TryLoad(Lib, assembly, path, out var h2) ? h2 : IntPtr.Zero;
    }

    /// <summary>Null when NDI can be used, otherwise why not.</summary>
    public static string? UnavailableReason
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return "NDI needs Windows.";
            EnsureInitialized();
            return _loadError;
        }
    }

    public static bool EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return true;
            if (_loadError is not null) return false;
            try
            {
                if (!NDIlib_initialize())
                {
                    _loadError = "NDI could not start on this CPU (NDIlib_initialize failed).";
                    return false;
                }
                _initialized = true;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _loadError = "NDI Runtime not installed. Install NDI Tools or the NDI 6 Runtime from ndi.video, then restart Endora.";
                return false;
            }
        }
    }

    // ---------------- structs ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr p_ndi_name;
        public IntPtr p_url_address;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindCreate
    {
        [MarshalAs(UnmanagedType.U1)] public bool show_local_sources;
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    public enum RecvColorFormat
    {
        BGRX_BGRA = 0,
    }

    public enum RecvBandwidth
    {
        Highest = 100,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreateV3
    {
        public Source source_to_connect_to;
        public RecvColorFormat color_format;
        public RecvBandwidth bandwidth;
        [MarshalAs(UnmanagedType.U1)] public bool allow_video_fields;
        public IntPtr p_ndi_recv_name;
    }

    public enum FrameType
    {
        None = 0,
        Video = 1,
        Audio = 2,
        Metadata = 3,
        Error = 4,
        StatusChange = 100,
    }

    public const int FourCC_BGRX = 'B' | ('G' << 8) | ('R' << 16) | ('X' << 24);
    public const int FourCC_BGRA = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
    public const int FrameFormatProgressive = 1;
    public const long SendTimecodeSynthesize = long.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres;
        public int yres;
        public int FourCC;
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
        [MarshalAs(UnmanagedType.U1)] public bool clock_video;
        [MarshalAs(UnmanagedType.U1)] public bool clock_audio;
    }

    // ---------------- functions ----------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool NDIlib_initialize();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_create_v2(ref FindCreate settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_find_wait_for_sources(IntPtr finder, uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_get_current_sources(IntPtr finder, out uint count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_find_destroy(IntPtr finder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_recv_create_v3(ref RecvCreateV3 settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern FrameType NDIlib_recv_capture_v2(IntPtr recv, ref VideoFrameV2 video, IntPtr audio, IntPtr metadata, uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_free_video_v2(IntPtr recv, ref VideoFrameV2 video);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_destroy(IntPtr recv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr sender, ref VideoFrameV2 video);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr sender);

    /// <summary>Lists NDI sources visible on the network, waiting up to <paramref name="waitMs"/> for them to appear.</summary>
    public static List<string> FindSources(int waitMs)
    {
        var names = new List<string>();
        if (!EnsureInitialized()) return names;
        var fc = new FindCreate { show_local_sources = true };
        var finder = NDIlib_find_create_v2(ref fc);
        if (finder == IntPtr.Zero) return names;
        try
        {
            var until = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < until)
                NDIlib_find_wait_for_sources(finder, (uint)Math.Max(1, until - Environment.TickCount64));
            var arr = NDIlib_find_get_current_sources(finder, out var count);
            var size = Marshal.SizeOf<Source>();
            for (var i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Source>(arr + i * size);
                if (Utf8(s.p_ndi_name) is { Length: > 0 } n) names.Add(n);
            }
        }
        finally
        {
            NDIlib_find_destroy(finder);
        }
        return names;
    }

    public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
