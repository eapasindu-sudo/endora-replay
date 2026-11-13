using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using EndoraReplay.Core.Services;

namespace EndoraReplay.Core.Vmix;

/// <summary>What Endora knows about vMix right now.</summary>
public sealed record VmixSnapshot(
    bool Connected,
    string? Version,
    IReadOnlySet<int> Program,
    IReadOnlySet<int> Preview,
    IReadOnlySet<int> Playing,
    IReadOnlyList<VmixInput> Inputs)
{
    public static readonly VmixSnapshot Disconnected = new(false, null, new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), []);

    public bool OnProgram(int? input) => input is { } n && Program.Contains(n);

    public bool IsPlaying(int? input) => input is { } n && Playing.Contains(n);

    public string InputLabel(int? input) =>
        input is not { } n ? "—" : Inputs.FirstOrDefault(i => i.Number == n) is { } i ? $"{n} {i.Label}" : n.ToString();
}

/// <summary>The vMix connection used by the automation. Implemented by <see cref="VmixClient"/> and by tests.</summary>
public interface IVmix : IAsyncDisposable
{
    VmixSnapshot Snapshot { get; }

    /// <summary>Raised on a background thread whenever the snapshot changes.</summary>
    event Action<VmixSnapshot>? Changed;

    /// <summary>Runs a vMix function. Returns an error message, or null when vMix answered OK.</summary>
    Task<string?> FunctionAsync(string function, string? query, CancellationToken cancellationToken = default);

    /// <summary>Asks vMix whether an input is running right now (its state in the XML). Null when vMix did not answer.</summary>
    Task<bool?> IsInputRunningAsync(int input, CancellationToken cancellationToken = default);
}

/// <summary>
/// vMix TCP API client (port 8099). Keeps one connection open, reconnects on loss, subscribes to TALLY and ACTS
/// (activator) events, and refreshes the input list from the XML state every few seconds.
/// </summary>
public sealed class VmixClient : IVmix
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan XmlTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InputRefresh = TimeSpan.FromSeconds(5);

    private readonly DiagnosticsService _diag;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<VmixMessage>>> _pending = new();
    private VmixSnapshot _snapshot = VmixSnapshot.Disconnected;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private string _host = "127.0.0.1";
    private int _port = VmixProtocol.DefaultPort;
    private int _reconnectSeconds = 3;

    public VmixClient(DiagnosticsService diagnostics) => _diag = diagnostics;

    public VmixSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event Action<VmixSnapshot>? Changed;
    public string Address => $"{_host}:{_port}";

    public async Task StartAsync(string host, int port, int reconnectSeconds)
    {
        await StopAsync().ConfigureAwait(false);
        _host = host.Trim();
        _port = port;
        _reconnectSeconds = Math.Max(1, reconnectSeconds);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct), ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await ConnectAndServeAsync(ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (attempt == 1 || attempt % 10 == 0)
                    _diag.Warn(LogCategory.Vmix, $"vMix at {Address}: {e.Message}");
            }
            Disconnected();
            try { await Task.Delay(TimeSpan.FromSeconds(_reconnectSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndServeAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                try { await tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("no answer (is vMix running, and the address right?)");
                }
            }
            var stream = tcp.GetStream();
            lock (_gate)
            {
                _tcp = tcp;
                _stream = stream;
            }
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reader = Task.Run(() => ReadLoopAsync(stream, session.Token), session.Token);

            await SendRawAsync("SUBSCRIBE TALLY\r\n").ConfigureAwait(false);
            await SendRawAsync("SUBSCRIBE ACTS\r\n").ConfigureAwait(false);
            await SendRawAsync("TALLY\r\n").ConfigureAwait(false);
            await RefreshStateAsync(session.Token).ConfigureAwait(false);
            Update(s => s with { Connected = true });
            _diag.Info(LogCategory.Vmix, $"Connected to vMix {Snapshot.Version} at {Address}, {Snapshot.Inputs.Count} inputs.");

            // Keep the input list fresh while connected; the reader ends the session on loss.
            while (!reader.IsCompleted)
            {
                var done = await Task.WhenAny(reader, Task.Delay(InputRefresh, session.Token)).ConfigureAwait(false);
                if (done == reader) break;
                try { await RefreshStateAsync(session.Token).ConfigureAwait(false); }
                catch (TimeoutException) { _diag.Warn(LogCategory.Vmix, "vMix did not send its state in time."); }
            }
            await session.CancelAsync().ConfigureAwait(false);
            try { await reader.ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
            if (!ct.IsCancellationRequested) _diag.Error(LogCategory.Vmix, "Connection to vMix lost.");
        }
        finally
        {
            lock (_gate)
            {
                _tcp = null;
                _stream = null;
            }
            tcp.Dispose();
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var parser = new VmixStreamParser();
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return;
            foreach (var m in parser.Feed(buffer.AsSpan(0, n))) Handle(m);
        }
    }

    private void Handle(VmixMessage m)
    {
        switch (m)
        {
            case VmixTallyMessage t:
                Update(s => s with { Program = t.Program, Preview = t.Preview });
                break;
            case VmixActivatorMessage { Name: "InputPlaying", Input: { } input } a:
                Update(s =>
                {
                    var playing = new HashSet<int>(s.Playing);
                    if (a.IsOn) playing.Add(input); else playing.Remove(input);
                    return s with { Playing = playing };
                });
                break;
            case VmixReplyMessage r when r.Command is "FUNCTION" or "XMLTEXT":
                Complete(r.Command, m);
                break;
            case VmixDataMessage d when d.Command is "XML" or "XMLTEXT":
                Complete(d.Command, m);
                break;
            case VmixReplyMessage { Command: "XML", Ok: false } r:
                Complete("XML", r);
                break;
        }
    }

    private void Complete(string command, VmixMessage m)
    {
        if (_pending.TryGetValue(command, out var q) && q.TryDequeue(out var tcs)) tcs.TrySetResult(m);
    }

    private async Task<VmixMessage> RequestAsync(string command, string line, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<VmixMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Queue and write under one lock so replies come back in the order they were asked.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = CurrentStream() ?? throw new InvalidOperationException("vMix is not connected.");
            _pending.GetOrAdd(command, _ => new ConcurrentQueue<TaskCompletionSource<VmixMessage>>()).Enqueue(tcs);
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reply may still arrive later; leave the queue as it is so later replies stay matched.
            throw new TimeoutException($"vMix did not answer {command} within {timeout.TotalSeconds:0} s.");
        }
    }

    private async Task SendRawAsync(string line)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stream = CurrentStream() ?? throw new InvalidOperationException("vMix is not connected.");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private NetworkStream? CurrentStream()
    {
        lock (_gate) return _stream;
    }

    /// <summary>Reloads the input list (titles, types, playing state) from vMix's XML.</summary>
    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync("XML", "XML\r\n", XmlTimeout, ct).ConfigureAwait(false);
        if (reply is not VmixDataMessage d) return;
        try
        {
            var (inputs, active, preview, version) = VmixProtocol.ParseState(d.Data);
            var running = inputs.Where(i => i.IsRunning).Select(i => i.Number).ToHashSet();
            Update(s => s with
            {
                Version = version ?? s.Version,
                Inputs = inputs,
                Playing = running,
                // Before the first tally event arrives, take Program/Preview from the XML.
                Program = s.Program.Count == 0 && active is { } a ? new HashSet<int> { a } : s.Program,
                Preview = s.Preview.Count == 0 && preview is { } p ? new HashSet<int> { p } : s.Preview,
            });
        }
        catch (System.Xml.XmlException e)
        {
            _diag.Warn(LogCategory.Vmix, $"vMix XML could not be read: {e.Message}");
        }
    }

    public async Task<string?> FunctionAsync(string function, string? query, CancellationToken cancellationToken = default)
    {
        string line;
        try { line = VmixProtocol.FunctionLine(function, query); }
        catch (ArgumentException e) { return e.Message; }
        if (!Snapshot.Connected) return "vMix is not connected.";
        try
        {
            var reply = await RequestAsync("FUNCTION", line, ReplyTimeout, cancellationToken).ConfigureAwait(false);
            if (reply is VmixReplyMessage { Ok: true })
            {
                _diag.Info(LogCategory.Vmix, line.TrimEnd());
                return null;
            }
            var err = reply is VmixReplyMessage r ? r.Text : "unexpected reply";
            _diag.Error(LogCategory.Vmix, $"{line.TrimEnd()} → {err}");
            return $"vMix refused {function}: {err}";
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException or SocketException)
        {
            _diag.Error(LogCategory.Vmix, $"{line.TrimEnd()} → {e.Message}");
            return e.Message;
        }
    }

    public async Task<bool?> IsInputRunningAsync(int input, CancellationToken cancellationToken = default)
    {
        if (!Snapshot.Connected) return null;
        try
        {
            var reply = await RequestAsync("XMLTEXT", $"XMLTEXT vmix/inputs/input[@number='{input}']/@state\r\n", ReplyTimeout, cancellationToken)
                .ConfigureAwait(false);
            var text = reply switch
            {
                VmixReplyMessage { Ok: true } r => r.Text,
                VmixDataMessage d => d.Data,
                _ => null,
            };
            return text is null ? null : text.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException or SocketException)
        {
            return null;
        }
    }

    private void Disconnected()
    {
        foreach (var q in _pending.Values)
            while (q.TryDequeue(out var tcs)) tcs.TrySetException(new IOException("vMix connection closed."));
        if (Snapshot.Connected || Snapshot.Inputs.Count > 0) Update(_ => VmixSnapshot.Disconnected);
    }

    private void Update(Func<VmixSnapshot, VmixSnapshot> f)
    {
        VmixSnapshot s;
        lock (_gate)
        {
            var next = f(_snapshot);
            if (next == _snapshot) return;
            _snapshot = next;
            s = next;
        }
        Changed?.Invoke(s);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        lock (_gate) _tcp?.Dispose();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (Exception) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
        Disconnected();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
