using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using EndoraReplay.Core.Input;

namespace EndoraReplay.Hardware.Midi;

/// <summary>
/// MIDI input through the Windows multimedia API (winmm), which every USB MIDI keyboard and controller supports
/// without extra drivers. Messages are handed on in order on a worker thread, never on the driver callback.
/// HARDWARE PATH: not yet tested with a real MIDI controller.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmMidiInput : IDisposable
{
    private const int CallbackFunction = 0x00030000;
    private const int MimData = 0x3C3;

    private delegate void MidiInProc(IntPtr handle, int msg, IntPtr instance, IntPtr param1, IntPtr param2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] private static extern int midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int midiInGetDevCapsW(IntPtr id, ref MidiInCaps caps, int size);
    [DllImport("winmm.dll")] private static extern int midiInOpen(out IntPtr handle, int id, MidiInProc proc, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int midiInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiInClose(IntPtr handle);

    private readonly MidiInProc _proc;
    private readonly List<(IntPtr Handle, string Name)> _open = [];
    private readonly Channel<MidiMessage> _queue = Channel.CreateUnbounded<MidiMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    /// <summary>Raised on a worker thread, one message at a time in arrival order.</summary>
    public event Action<MidiMessage>? MessageReceived;

    public WinMmMidiInput()
    {
        _proc = OnMidi;
        _pump = Task.Run(PumpAsync);
    }

    public IReadOnlyList<string> OpenDevices => _open.Select(o => o.Name).ToList();

    public static IReadOnlyList<string> ListDevices()
    {
        var names = new List<string>();
        var n = midiInGetNumDevs();
        for (var i = 0; i < n; i++)
        {
            var caps = new MidiInCaps();
            names.Add(midiInGetDevCapsW(i, ref caps, Marshal.SizeOf<MidiInCaps>()) == 0 ? caps.szPname : $"MIDI device {i}");
        }
        return names;
    }

    /// <summary>Opens the named device, or every device when <paramref name="device"/> is empty. Returns problems found.</summary>
    public IReadOnlyList<string> Open(string? device)
    {
        Close();
        var problems = new List<string>();
        var names = ListDevices();
        for (var i = 0; i < names.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(device) && !string.Equals(names[i], device, StringComparison.OrdinalIgnoreCase)) continue;
            var r = midiInOpen(out var h, i, _proc, IntPtr.Zero, CallbackFunction);
            if (r != 0)
            {
                problems.Add($"{names[i]}: could not open (error {r}; another program may be using it).");
                continue;
            }
            midiInStart(h);
            _open.Add((h, names[i]));
        }
        if (!string.IsNullOrWhiteSpace(device) && _open.Count == 0 && problems.Count == 0)
            problems.Add($"MIDI device '{device}' is not connected.");
        return problems;
    }

    private void OnMidi(IntPtr handle, int msg, IntPtr instance, IntPtr p1, IntPtr p2)
    {
        if (msg != MimData) return;
        if (MidiMessage.Parse((int)p1) is { } m) _queue.Writer.TryWrite(m);
    }

    private async Task PumpAsync()
    {
        await foreach (var m in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { MessageReceived?.Invoke(m); }
            catch (Exception) { /* a failing handler must not stop MIDI input */ }
        }
    }

    public void Close()
    {
        foreach (var (h, _) in _open)
        {
            midiInStop(h);
            midiInReset(h);
            midiInClose(h);
        }
        _open.Clear();
    }

    public void Dispose()
    {
        Close();
        _queue.Writer.TryComplete();
        _pump.Wait(TimeSpan.FromSeconds(1));
        GC.KeepAlive(_proc);
    }
}
