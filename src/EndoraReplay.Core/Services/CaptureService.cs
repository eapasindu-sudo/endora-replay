using System.Collections.Concurrent;
using System.Threading.Channels;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

/// <summary>
/// Runs one independent capture session per configured source. Each source has its own capture worker
/// (owned by the backend) and its own buffer-writer worker, so a slow disk or a stalled device never
/// blocks another source or playback.
/// </summary>
public sealed class CaptureService : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<CaptureBackendKind, ICaptureBackend> _backends;
    private readonly RollingBufferService _buffers;
    private readonly DiagnosticsService _diag;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitor;

    public CaptureService(IEnumerable<ICaptureBackend> backends, RollingBufferService buffers, DiagnosticsService diagnostics, IClock clock)
    {
        _backends = backends.ToDictionary(b => b.Kind);
        _buffers = buffers;
        _diag = diagnostics;
        _clock = clock;
    }

    /// <summary>Raised when the set of running camera sources changes (any thread).</summary>
    public event Action<bool>? RecordingChanged;

    /// <summary>Raised when a source's status changes (any thread).</summary>
    public event Action<SourceHealth>? HealthChanged;

    public IReadOnlyDictionary<CaptureBackendKind, ICaptureBackend> Backends => _backends;

    public bool IsRecording => _sessions.Values.Any(s => s.Settings.Role == SourceRole.Camera && s.Running);

    public IReadOnlyList<int> RunningCameraIds =>
        _sessions.Values.Where(s => s.Settings.Role == SourceRole.Camera && s.Running).Select(s => s.Settings.SourceId).OrderBy(i => i).ToList();

    public int? ProgramSourceId => _sessions.Values.FirstOrDefault(s => s.Settings.Role == SourceRole.Program && s.Running)?.Settings.SourceId;

    /// <summary>Most recent frame of a source for UI preview; null when none yet. Never a substitute frame.</summary>
    public EncodedFrame? GetLatest(int sourceId) => _sessions.TryGetValue(sourceId, out var s) ? s.Latest : null;

    public async Task<IReadOnlyList<CaptureDeviceInfo>> DiscoverAsync(CancellationToken ct)
    {
        var all = new List<CaptureDeviceInfo>();
        foreach (var b in _backends.Values)
        {
            if (b.UnavailableReason is { } why)
            {
                _diag.Info(LogCategory.Capture, $"{b.Kind} backend unavailable: {why}");
                continue;
            }
            try
            {
                var found = await b.DiscoverAsync(ct).ConfigureAwait(false);
                all.AddRange(found);
                _diag.Info(LogCategory.Capture, $"{b.Kind}: found {found.Count} device(s)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _diag.Error(LogCategory.Capture, $"{b.Kind} discovery failed: {e.Message}");
            }
        }
        return all;
    }

    /// <summary>Starts every enabled source. Sources that fail are reported and retried by the monitor; others continue.</summary>
    public async Task StartAsync(AppSettings settings, CancellationToken ct)
    {
        await StopAsync().ConfigureAwait(false);
        var format = settings.Format;
        var options = new CaptureOptions(settings.Buffer.JpegQuality, _clock);
        var capacity = RollingBufferService.CapacityFor(settings.Buffer.Seconds, format, settings.Buffer.EstimatedFrameBytes);

        foreach (var src in settings.Sources.Where(s => s.Enabled))
        {
            var health = _diag.Health(src.SourceId, src.Name, src.Backend == CaptureBackendKind.Simulated);
            if (!_backends.TryGetValue(src.Backend, out var backend) || backend.UnavailableReason is not null)
            {
                SetStatus(health, SourceStatus.Error, backend?.UnavailableReason ?? $"{src.Backend} backend not available in this build.");
                continue;
            }
            if (string.IsNullOrEmpty(src.DeviceId))
            {
                SetStatus(health, SourceStatus.Error, "No device assigned. Choose one in Settings → Capture inputs.");
                continue;
            }

            var device = new CaptureDeviceInfo(src.DeviceId, src.DeviceName ?? src.DeviceId, src.Backend, [format]);
            var validation = await backend.ValidateAsync(device, format, ct).ConfigureAwait(false);
            if (!validation.IsValid && !src.AcceptModeMismatch)
            {
                SetStatus(health, SourceStatus.ModeMismatch, validation.Message);
                continue;
            }

            if (src.Role == SourceRole.Camera) _buffers.Open(src.SourceId, capacity);
            var session = new Session(this, src, backend, device, format, options, health);
            _sessions[src.SourceId] = session;
            await session.StartAsync(ct).ConfigureAwait(false);
        }

        _monitorCts = new CancellationTokenSource();
        _monitor = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
        RecordingChanged?.Invoke(IsRecording);
    }

    public async Task StopAsync()
    {
        if (_monitorCts is not null)
        {
            await _monitorCts.CancelAsync().ConfigureAwait(false);
            try { if (_monitor is not null) await _monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _monitorCts.Dispose();
            _monitorCts = null;
        }
        foreach (var s in _sessions.Values) await s.DisposeAsync().ConfigureAwait(false);
        foreach (var id in _sessions.Keys) _buffers.Close(id);
        _sessions.Clear();
        RecordingChanged?.Invoke(false);
    }

    private void SetStatus(SourceHealth h, SourceStatus status, string? error = null)
    {
        if (h.Status == status && error is null) return;
        h.Status = status;
        if (error is not null) h.LastError = error;
        var sev = status is SourceStatus.Error or SourceStatus.NoSignal or SourceStatus.ModeMismatch ? LogSeverity.Warning : LogSeverity.Info;
        _diag.Log(sev, LogCategory.Capture, $"{h.Name}: {status}" + (error is null ? "" : $" ({error})"));
        HealthChanged?.Invoke(h.Clone());
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var wasRecording = IsRecording;
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = _clock.NowTicks;
            foreach (var s in _sessions.Values)
                await s.CheckHealthAsync(now, ct).ConfigureAwait(false);
            var rec = IsRecording;
            if (rec != wasRecording) RecordingChanged?.Invoke(rec);
            wasRecording = rec;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed class Session : IAsyncDisposable
    {
        private const int WriterQueueFrames = 32;
        private static readonly TimeSpan ReconnectAfter = TimeSpan.FromSeconds(3);

        private readonly CaptureService _owner;
        private readonly ICaptureBackend _backend;
        private readonly CaptureDeviceInfo _device;
        private readonly VideoFormat _format;
        private readonly CaptureOptions _options;
        private readonly SourceHealth _health;
        private readonly Queue<long> _fpsWindow = new();
        private Channel<EncodedFrame>? _queue;
        private Task? _writer;
        private ICaptureSource? _source;
        private CancellationTokenSource? _cts;
        private long _lastTicks = -1;
        private long _lastDeviceCounter = -1;
        private long _lastFrameAt;
        private long _startedAt;
        private bool _reportedMismatch;

        public Session(CaptureService owner, SourceSettings settings, ICaptureBackend backend, CaptureDeviceInfo device, VideoFormat format, CaptureOptions options, SourceHealth health)
        {
            _owner = owner;
            Settings = settings;
            _backend = backend;
            _device = device;
            _format = format;
            _options = options;
            _health = health;
        }

        public SourceSettings Settings { get; }
        public bool Running => _source is not null && _health.Status is SourceStatus.Signal or SourceStatus.Dropping or SourceStatus.ModeMismatch;
        public EncodedFrame? Latest { get; private set; }

        public async Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _queue = Channel.CreateBounded<EncodedFrame>(new BoundedChannelOptions(WriterQueueFrames)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true,
            });
            if (Settings.Role == SourceRole.Camera) _writer = Task.Run(() => WriterLoopAsync(_cts.Token));

            _owner.SetStatus(_health, SourceStatus.Starting);
            _startedAt = _options.Clock.NowTicks;
            _lastFrameAt = _startedAt;
            try
            {
                _source = _backend.Create(Settings.SourceId, _device, _format, _options);
                _source.FrameArrived += OnFrame;
                _source.StatusChanged += OnSourceStatus;
                await _source.StartAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _owner.SetStatus(_health, SourceStatus.Error, e.Message);
            }
        }

        private void OnSourceStatus(SourceStatus status, string? message) => _owner.SetStatus(_health, status, message);

        // Capture worker thread: must stay cheap and never block.
        private void OnFrame(EncodedFrame f)
        {
            var dur = _format.FrameDurationTicks;
            if (_lastTicks >= 0)
            {
                var delta = f.TimestampTicks - _lastTicks;
                if (delta <= 0)
                {
                    _health.TimestampGaps++;
                    _owner._diag.Warn(LogCategory.Capture, $"{_health.Name}: non-increasing timestamp ({delta} ticks), frame dropped");
                    return;
                }
                if (delta > dur * 3 / 2)
                {
                    var missing = Math.Max(1, (long)Math.Round((double)delta / dur) - 1);
                    _health.TimestampGaps++;
                    _health.DroppedFrames += missing;
                    _owner._diag.Increment($"dropped.src{_health.SourceId}", missing);
                    if (_health.TimestampGaps % 10 == 1)
                        _owner._diag.Warn(LogCategory.Capture, $"{_health.Name}: dropped {missing} frame(s) (ts gap {TimeSpan.FromTicks(delta).TotalMilliseconds:0} ms)");
                }
            }
            if (f.DeviceFrameCounter > 0 && _lastDeviceCounter > 0 && f.DeviceFrameCounter - _lastDeviceCounter > 1)
            {
                _health.DroppedFrames += f.DeviceFrameCounter - _lastDeviceCounter - 1;
            }
            _lastDeviceCounter = f.DeviceFrameCounter;
            _lastTicks = f.TimestampTicks;
            _lastFrameAt = _options.Clock.NowTicks;
            _health.FramesCaptured++;
            _health.LastFrameUtc = DateTime.UtcNow;
            lock (_fpsWindow) _fpsWindow.Enqueue(f.TimestampTicks);

            if ((f.Width != _format.Width || f.Height != _format.Height) && !_reportedMismatch)
            {
                _reportedMismatch = true;
                _owner.SetStatus(_health, SourceStatus.ModeMismatch, $"Device delivers {f.Width}x{f.Height}, project is {_format.Width}x{_format.Height}.");
            }

            Latest = f;
            if (Settings.Role == SourceRole.Camera && !_queue!.Writer.TryWrite(f))
            {
                _health.BufferOverflows++;
                _health.DroppedFrames++;
                _owner._diag.Increment($"overflow.src{_health.SourceId}");
            }
        }

        private async Task WriterLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var f in _queue!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        _owner._buffers.Write(f);
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
                    {
                        _health.DroppedFrames++;
                        _owner._diag.Error(LogCategory.Buffer, $"{_health.Name}: buffer write failed: {e.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task CheckHealthAsync(long now, CancellationToken ct)
        {
            double fps;
            lock (_fpsWindow)
            {
                while (_fpsWindow.Count > 0 && now - _fpsWindow.Peek() > TimeSpan.TicksPerSecond) _fpsWindow.Dequeue();
                fps = _fpsWindow.Count;
            }
            _health.MeasuredFps = fps;

            var silentFor = TimeSpan.FromTicks(now - _lastFrameAt);
            var noSignalAfter = TimeSpan.FromTicks(Math.Max(_format.FrameDurationTicks * 10, TimeSpan.TicksPerMillisecond * 500));

            if (_health.Status is SourceStatus.Signal or SourceStatus.Dropping or SourceStatus.Starting or SourceStatus.ModeMismatch && silentFor > noSignalAfter)
            {
                if (_health.Status != SourceStatus.Starting || silentFor > TimeSpan.FromSeconds(5))
                    _owner.SetStatus(_health, SourceStatus.NoSignal, $"No frames for {silentFor.TotalMilliseconds:0} ms");
            }
            else if (silentFor < noSignalAfter && _health.FramesCaptured > 0 && !_reportedMismatch)
            {
                var ok = _format.MatchesRate(fps, Math.Max(1, _format.FramesPerSecond * 0.04));
                var now1s = TimeSpan.FromTicks(now - _startedAt) > TimeSpan.FromSeconds(1.5);
                if (now1s) _owner.SetStatus(_health, ok ? SourceStatus.Signal : SourceStatus.Dropping);
            }

            if (_health.Status is SourceStatus.Error or SourceStatus.NoSignal && silentFor > ReconnectAfter && !ct.IsCancellationRequested)
                await ReconnectAsync(ct).ConfigureAwait(false);
        }

        private async Task ReconnectAsync(CancellationToken ct)
        {
            _health.ReconnectAttempts++;
            _owner.SetStatus(_health, SourceStatus.Reconnecting, $"attempt {_health.ReconnectAttempts}");
            await DisposeSourceAsync().ConfigureAwait(false);
            _lastFrameAt = _options.Clock.NowTicks;
            _startedAt = _lastFrameAt;
            _lastTicks = -1;
            _lastDeviceCounter = -1;
            try
            {
                _source = _backend.Create(Settings.SourceId, _device, _format, _options);
                _source.FrameArrived += OnFrame;
                _source.StatusChanged += OnSourceStatus;
                await _source.StartAsync(ct).ConfigureAwait(false);
                _owner.SetStatus(_health, SourceStatus.Starting);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _owner.SetStatus(_health, SourceStatus.Error, e.Message);
            }
        }

        private async Task DisposeSourceAsync()
        {
            if (_source is null) return;
            _source.FrameArrived -= OnFrame;
            _source.StatusChanged -= OnSourceStatus;
            try
            {
                await _source.StopAsync().ConfigureAwait(false);
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _owner._diag.Warn(LogCategory.Capture, $"{_health.Name}: error while stopping: {e.Message}");
            }
            _source = null;
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeSourceAsync().ConfigureAwait(false);
            _queue?.Writer.TryComplete();
            if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
            try { if (_writer is not null) await _writer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _cts?.Dispose();
            _owner.SetStatus(_health, SourceStatus.Stopped);
        }
    }
}
