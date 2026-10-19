using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EndoraReplay.App.Services;

/// <summary>One reading of the PC's load. GpuPercent is null when Windows has no GPU counters.</summary>
public sealed record SystemLoad(double CpuPercent, double? GpuPercent, double RamPercent, double RamUsedGb, double RamTotalGb, double NetInMbps, double NetOutMbps);

/// <summary>
/// Samples CPU, GPU, RAM and network use once a second on a background thread, the same sources Task Manager uses:
/// system times for CPU, the "GPU Engine" counters (busiest engine) for GPU, memory status for RAM, and the byte
/// counters of the active network adapters.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly Timer _timer;
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private long _lastTicks;
    private Dictionary<string, PerformanceCounter> _gpu = [];
    private DateTime _gpuRefreshed = DateTime.MinValue;
    private bool _gpuUnavailable;
    private int _busy;

    public SystemLoad? Latest { get; private set; }

    public SystemMonitor()
    {
        _timer = new Timer(_ => Sample(), null, 0, 1000);
    }

    private void Sample()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = _lastTicks == 0 ? 1 : (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;

            double cpu = 0;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var di = idle - _idle;
                var total = kernel - _kernel + (user - _user); // kernel time includes idle time
                if (_kernel != 0 && total > 0) cpu = Math.Clamp(100.0 * (total - di) / total, 0, 100);
                (_idle, _kernel, _user) = (idle, kernel, user);
            }

            var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            double ramPct = 0, usedGb = 0, totalGb = 0;
            if (GlobalMemoryStatusEx(ref mem))
            {
                totalGb = mem.ullTotalPhys / 1073741824.0;
                usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                ramPct = mem.dwMemoryLoad;
            }

            long rx = 0, tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var st = n.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                catch (NetworkInformationException) { }
            }
            double inMbps = 0, outMbps = 0;
            if (_rx != 0 && seconds > 0)
            {
                inMbps = Math.Max(0, rx - _rx) * 8 / 1e6 / seconds;
                outMbps = Math.Max(0, tx - _tx) * 8 / 1e6 / seconds;
            }
            (_rx, _tx) = (rx, tx);

            Latest = new SystemLoad(cpu, Gpu(), ramPct, usedGb, totalGb, inMbps, outMbps);
        }
        catch (Exception)
        {
            // A meter must never take the app down.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Busiest GPU engine type (3D, video decode, copy…), summed over processes, like Task Manager.</summary>
    private double? Gpu()
    {
        if (_gpuUnavailable || !OperatingSystem.IsWindows()) return null;
        try
        {
            if (DateTime.UtcNow - _gpuRefreshed > TimeSpan.FromSeconds(10))
            {
                // Processes come and go, so the counter instances do too.
                _gpuRefreshed = DateTime.UtcNow;
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
                var next = new Dictionary<string, PerformanceCounter>();
                foreach (var name in names)
                {
                    if (_gpu.Remove(name, out var existing)) next[name] = existing;
                    else next[name] = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
                }
                foreach (var gone in _gpu.Values) gone.Dispose();
                _gpu = next;
            }
            var byEngine = new Dictionary<string, double>();
            foreach (var (name, counter) in _gpu)
            {
                float v;
                try { v = counter.NextValue(); }
                catch (InvalidOperationException) { continue; }
                var i = name.IndexOf("engtype_", StringComparison.Ordinal);
                var luid = name.IndexOf("luid_", StringComparison.Ordinal);
                var key = (luid >= 0 ? name[luid..Math.Min(name.Length, luid + 27)] : "") + (i >= 0 ? name[i..] : name);
                byEngine[key] = byEngine.GetValueOrDefault(key) + v;
            }
            return byEngine.Count == 0 ? 0 : Math.Clamp(byEngine.Values.Max(), 0, 100);
        }
        catch (Exception)
        {
            _gpuUnavailable = true;
            return null;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var c in _gpu.Values) c.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
