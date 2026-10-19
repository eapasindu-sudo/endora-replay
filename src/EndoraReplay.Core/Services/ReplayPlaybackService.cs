using System.Diagnostics;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

public enum PlayoutSegmentKind
{
    StingerIn,
    Clip,
    StingerOut,
}

/// <param name="WaitForRelease">Hold the previous frame until <see cref="ReplayPlaybackService.Release"/> is called (used to wait for Program confirmation).</param>
/// <param name="CutPointIndex">Frame index at which <see cref="ReplayPlaybackService.CutPointReached"/> fires (stingers).</param>
public sealed record PlayoutSegment(PlayoutSegmentKind Kind, IFrameSequence Frames, double Speed = 1.0, int? CutPointIndex = null, bool WaitForRelease = false, string Label = "");

public enum PlaybackStatus
{
    Idle,
    WaitingForRelease,
    Playing,
    Paused,
    HoldingLastFrame,
}

/// <summary>
/// Drives the replay output at the project frame rate on a dedicated thread. Slow motion repeats source frames
/// (no frame blending). Frames are selected by source timestamp, so sources at a different rate still play in real time.
/// </summary>
public sealed class ReplayPlaybackService : IAsyncDisposable
{
    public static readonly double[] StandardSpeeds = [1.0, 0.5, 0.25];

    private readonly IReplayOutput _output;
    private readonly DiagnosticsService _diag;
    private readonly object _gate = new();
    private VideoFormat _format;
    private Job? _job;

    public ReplayPlaybackService(IReplayOutput output, VideoFormat format, DiagnosticsService diagnostics)
    {
        _output = output;
        _format = format;
        _diag = diagnostics;
    }

    public IReplayOutput Output => _output;
    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Idle;
    public PlayoutSegmentKind? CurrentKind { get; private set; }

    /// <summary>Speed applied to the current and later <see cref="PlayoutSegmentKind.Clip"/> segments. Stingers always play at 100%.</summary>
    public double ClipSpeed { get; private set; } = 0.5;

    /// <summary>Position of the current frame on the session clock.</summary>
    public long PositionTicks { get; private set; }

    public event Action<PlayoutSegmentKind>? SegmentStarted;
    public event Action<PlayoutSegmentKind>? CutPointReached;
    public event Action<PlayoutSegmentKind>? SegmentCompleted;

    /// <summary>All queued segments finished. The output holds the last frame until <see cref="ShowHolding"/>.</summary>
    public event Action? PlayoutCompleted;

    public event Action<string>? Failed;
    public event Action<PlaybackStatus>? StatusChanged;

    public void SetFormat(VideoFormat format) => _format = format;

    public void SetSpeed(double speed)
    {
        if (speed is <= 0 or > 2) throw new ArgumentOutOfRangeException(nameof(speed));
        ClipSpeed = speed;
        _diag.Info(LogCategory.Playback, $"Speed {speed:P0}");
    }

    /// <summary>Validates output readiness and mode. Returns null when playback may start, otherwise the reason.</summary>
    public string? CheckOutput()
    {
        if (_output.Readiness != OutputReadiness.Ready)
            return $"Replay output not ready: {_output.Readiness} ({_output.ReadinessMessage})";
        var v = _output.ValidateMode(_format);
        return v.IsValid ? null : $"Replay output mode invalid: {v.Message}";
    }

    /// <summary>Replaces anything playing with a new list of segments.</summary>
    public void Start(IReadOnlyList<PlayoutSegment> segments, int startDelayMs = 0)
    {
        if (segments.Count == 0) throw new ArgumentException("Nothing to play.", nameof(segments));
        if (CheckOutput() is { } problem) throw new InvalidOperationException(problem);
        var job = new Job(this, segments, startDelayMs);
        Job? old;
        lock (_gate)
        {
            old = _job;
            _job = job;
        }
        old?.Cancel();
        job.Run();
    }

    /// <summary>Appends a segment to the running job, or starts a new one if idle.</summary>
    public void Append(PlayoutSegment segment)
    {
        lock (_gate)
        {
            if (_job is { IsFinished: false } j)
            {
                j.Append(segment);
                return;
            }
        }
        Start([segment]);
    }

    /// <summary>Lets a segment that is waiting for release start, after <paramref name="delayMs"/>.</summary>
    public bool Release(int delayMs = 0)
    {
        lock (_gate) return _job?.Release(delayMs) ?? false;
    }

    /// <summary>
    /// Switches the replay that is playing (or about to play after a stinger) to another camera angle, continuing at the
    /// same moment on the shared clock. Returns false when no replay clip is running.
    /// </summary>
    public bool SwitchAngle(IFrameSequence angle)
    {
        lock (_gate)
        {
            if (_job is not { IsFinished: false } j || !j.HasClip) return false;
            j.SwitchAngle(angle);
            return true;
        }
    }

    public void Pause(bool paused)
    {
        lock (_gate) _job?.SetPaused(paused);
    }

    /// <summary>Stops playback and shows the holding frame.</summary>
    public void Stop()
    {
        Job? j;
        lock (_gate) { j = _job; _job = null; }
        j?.Cancel();
        ShowHolding();
    }

    public void ShowHolding()
    {
        lock (_gate)
        {
            if (_job is { IsFinished: false }) return;
        }
        _output.ShowHolding();
        SetStatus(PlaybackStatus.Idle);
        CurrentKind = null;
    }

    private void SetStatus(PlaybackStatus s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    public ValueTask DisposeAsync()
    {
        Job? j;
        lock (_gate) { j = _job; _job = null; }
        j?.Cancel();
        return ValueTask.CompletedTask;
    }

    private sealed class Job
    {
        private readonly ReplayPlaybackService _svc;
        private readonly Queue<PlayoutSegment> _segments;
        private readonly int _startDelayMs;
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _released = new(false);
        private readonly object _gate = new();
        private volatile int _releaseDelayMs;
        private volatile bool _paused;
        private Thread? _thread;

        public Job(ReplayPlaybackService svc, IReadOnlyList<PlayoutSegment> segments, int startDelayMs)
        {
            _svc = svc;
            _segments = new Queue<PlayoutSegment>(segments);
            _startDelayMs = startDelayMs;
        }

        public bool IsFinished { get; private set; }
        private volatile IFrameSequence? _angleSwap;
        private volatile bool _clipDone;

        /// <summary>True while a clip segment is playing or still queued.</summary>
        public bool HasClip
        {
            get
            {
                if (_svc.CurrentKind == PlayoutSegmentKind.Clip && !_clipDone) return true;
                lock (_gate) return _segments.Any(s => s.Kind == PlayoutSegmentKind.Clip);
            }
        }

        public void SwitchAngle(IFrameSequence angle) => _angleSwap = angle;

        public void Append(PlayoutSegment s)
        {
            lock (_gate) _segments.Enqueue(s);
        }

        public bool Release(int delayMs)
        {
            _releaseDelayMs = delayMs;
            _released.Set();
            return true;
        }

        public void SetPaused(bool p) => _paused = p;

        public void Cancel()
        {
            _cts.Cancel();
            _released.Set();
        }

        public void Run()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Endora playout", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        private void Loop()
        {
            var ct = _cts.Token;
            try
            {
                if (_startDelayMs > 0 && ct.WaitHandle.WaitOne(_startDelayMs)) return;
                while (!ct.IsCancellationRequested)
                {
                    PlayoutSegment? seg;
                    lock (_gate) seg = _segments.Count > 0 ? _segments.Dequeue() : null;
                    if (seg is null) break;

                    if (seg.WaitForRelease)
                    {
                        _svc.SetStatus(PlaybackStatus.WaitingForRelease);
                        _released.Wait(ct);
                        _released.Reset();
                        if (ct.IsCancellationRequested) return;
                        if (_releaseDelayMs > 0 && ct.WaitHandle.WaitOne(_releaseDelayMs)) return;
                    }
                    PlaySegment(seg, ct);
                }
                if (!ct.IsCancellationRequested)
                {
                    IsFinished = true;
                    _svc.SetStatus(PlaybackStatus.HoldingLastFrame);
                    _svc.PlayoutCompleted?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                IsFinished = true;
                _svc._diag.Error(LogCategory.Playback, $"Playback failed: {e.Message}");
                try { _svc._output.ShowHolding(); } catch (Exception) { }
                _svc.SetStatus(PlaybackStatus.Idle);
                _svc.Failed?.Invoke(e.Message);
            }
            finally
            {
                IsFinished = true;
            }
        }

        private void PlaySegment(PlayoutSegment seg, CancellationToken ct)
        {
            var seq = seg.Kind == PlayoutSegmentKind.Clip && Interlocked.Exchange(ref _angleSwap, null) is { } early ? early : seg.Frames;
            if (seq.Count == 0) throw new FrameUnavailableException($"{seg.Kind} has no frames.");
            _svc.CurrentKind = seg.Kind;
            _svc.SetStatus(PlaybackStatus.Playing);
            _svc.SegmentStarted?.Invoke(seg.Kind);
            _svc._diag.Info(LogCategory.Playback, $"{seg.Kind} start {seg.Label} ({seq.Count} frames)");

            var period = _svc._format.FrameDurationTicks;
            var sw = Stopwatch.StartNew();
            long tick = 0;
            double sourcePos = 0;
            var idx = 0;
            var shownIdx = -1;
            EncodedFrame? frame = null;
            var cutFired = seg.CutPointIndex is null;

            while (!ct.IsCancellationRequested)
            {
                if (seg.Kind == PlayoutSegmentKind.Clip && Interlocked.Exchange(ref _angleSwap, null) is { } swap && swap.Count > 0)
                {
                    // Same moment on the new angle: all cameras share one session clock.
                    var now = seq.StartTicks + (long)sourcePos;
                    seq = swap;
                    sourcePos = Math.Max(0, now - seq.StartTicks);
                    idx = 0;
                    shownIdx = -1;
                    _svc._diag.Info(LogCategory.Playback, $"Angle switched to source {seq.SourceId}");
                }
                var target = seq.StartTicks + (long)sourcePos;
                if (target > seq.EndTicks + period / 2) break;
                while (idx + 1 < seq.Count && seq.TicksAt(idx + 1) <= target) idx++;

                if (idx != shownIdx)
                {
                    frame = seq.Read(idx); // throws FrameUnavailableException: never substitute
                    shownIdx = idx;
                    _svc.PositionTicks = frame.TimestampTicks;
                }
                _svc._output.Present(frame!);

                if (!cutFired && idx >= seg.CutPointIndex)
                {
                    cutFired = true;
                    _svc.CutPointReached?.Invoke(seg.Kind);
                }

                var speed = seg.Kind == PlayoutSegmentKind.Clip ? _svc.ClipSpeed : seg.Speed;
                if (!_paused) sourcePos += period * speed;
                _svc.SetStatus(_paused ? PlaybackStatus.Paused : PlaybackStatus.Playing);

                tick++;
                WaitUntil(sw, tick * period, ct);
            }
            if (seg.Kind == PlayoutSegmentKind.Clip) _clipDone = true;
            if (!cutFired && !ct.IsCancellationRequested) _svc.CutPointReached?.Invoke(seg.Kind);
            if (!ct.IsCancellationRequested)
            {
                _svc._diag.Info(LogCategory.Playback, $"{seg.Kind} complete");
                _svc.SegmentCompleted?.Invoke(seg.Kind);
            }
        }

        private static void WaitUntil(Stopwatch sw, long targetTicks, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var remaining = targetTicks - sw.Elapsed.Ticks;
                if (remaining <= 0) return;
                if (remaining > TimeSpan.TicksPerMillisecond * 2) Thread.Sleep(1);
                else Thread.SpinWait(50);
            }
        }
    }
}
