using System.Collections.Concurrent;
using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Automation;
using EndoraReplay.Core.Config;
using EndoraReplay.Core.Input;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Services;
using EndoraReplay.Hardware.Video;
using EndoraReplay.Remote;

namespace EndoraReplay.App.Services;

public enum CueMode
{
    Live,
    Jog,
}

/// <summary>
/// The single path for operator commands from the UI, keyboard/shortcut keyboards and phones.
/// Owns angle selection, live/jog cueing, marks, speed and the replay queue; replay decisions go to automation.
/// </summary>
public sealed class OperatorController : IRemoteHandler
{
    private static readonly TimeSpan InstantReplayLength = TimeSpan.FromSeconds(8);

    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly CaptureService _capture;
    private readonly RollingBufferService _buffers;
    private readonly ClipService _clips;
    private readonly ReplayPlaybackService _playback;
    private readonly ReplayAutomationService _automation;
    private readonly DiagnosticsService _diag;
    private readonly Func<Core.Model.AtemSnapshot> _atem;
    private readonly ConcurrentQueue<ClipRef> _queue = new();
    private readonly object _phoneCacheGate = new();
    private (int Src, long Frame, byte[] Jpeg)? _phoneCache;

    public OperatorController(AppSettings settings, IClock clock, CaptureService capture, RollingBufferService buffers, ClipService clips,
        ReplayPlaybackService playback, ReplayAutomationService automation, DiagnosticsService diagnostics, Func<Core.Model.AtemSnapshot> atem)
    {
        _settings = settings;
        _clock = clock;
        _capture = capture;
        _buffers = buffers;
        _clips = clips;
        _playback = playback;
        _automation = automation;
        _diag = diagnostics;
        _atem = atem;
        SelectedCamera = settings.Sources.FirstOrDefault(s => s.Enabled && s.Role == SourceRole.Camera)?.SourceId ?? 1;
        _automation.Alert += (level, msg) => { LastAlert = msg; LastAlertLevel = level; Changed?.Invoke(); };
        _automation.StateChanged += OnStateChanged;
    }

    public int SelectedCamera { get; private set; }
    public CueMode Mode { get; private set; } = CueMode.Live;
    public long CueTicks { get; private set; }
    public double Speed { get; private set; } = 0.5;
    public long? LastSavedClipId { get; private set; }
    public string? LastAlert { get; private set; }
    public AlertLevel LastAlertLevel { get; private set; }
    public IReadOnlyList<ClipRef> Queue => _queue.ToList();

    /// <summary>Raised (any thread) when operator-visible state changes.</summary>
    public event Action? Changed;

    /// <summary>Puts the scoreboard (ATEM DSK 2) on or off air; set by the app host.</summary>
    public Func<string?>? ScoreboardToggle { get; set; }

    /// <summary>The UI should open the tag picker for this clip.</summary>
    public event Action<long>? TagPickerRequested;

    private VideoFormat Format => _settings.Format;

    // ---------------- command entry ----------------

    public Task<string?> ExecuteAsync(RemoteCommand c, string client) => Task.FromResult(Execute(c.Action, c.Value, c.Text, c.ClipId, client));

    /// <summary>Runs an operator action. Returns an error message for the operator, or null.</summary>
    public string? Execute(string action, double? value = null, string? text = null, long? clipId = null, string origin = "operator")
    {
        try
        {
            var error = Run(action, value, text, clipId, origin);
            Changed?.Invoke();
            return error;
        }
        catch (Exception e)
        {
            _diag.Error(LogCategory.App, $"{action} failed: {e.Message}");
            return e.Message;
        }
    }

    private string? Run(string action, double? value, string? text, long? clipId, string origin)
    {
        if (action.StartsWith("Camera", StringComparison.Ordinal) && int.TryParse(action.AsSpan(6), out var cam))
            return SelectCamera(cam);

        switch (action)
        {
            case OperatorActions.Replay:
                if (_automation.ArmedClip is null && _automation.State is not (ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM))
                {
                    var err = Commit(origin, instantIfNoMarks: true);
                    if (err is not null) return err;
                }
                _automation.Post(new ReplayRequested(origin));
                return null;
            case OperatorActions.Live: _automation.Post(new LiveRequested(origin)); return null;
            case OperatorActions.Stop: _automation.Post(new StopRequested(origin)); return null;
            case OperatorActions.CancelAuto: _automation.Post(new CancelAutoRequested(origin)); return null;
            case OperatorActions.EmergencyOverride: _automation.Post(new EmergencyOverrideRequested(!_automation.OverrideActive, origin)); return null;
            case OperatorActions.ClearError: _automation.Post(new ClearErrorRequested(origin)); return null;
            case OperatorActions.Scoreboard: return ScoreboardToggle?.Invoke() ?? "Scoreboard is not available.";

            case OperatorActions.MarkIn: _clips.MarkIn(CurrentTicks()); return null;
            case OperatorActions.MarkOut:
                _clips.MarkOut(CurrentTicks());
                return _clips.MarkInTicks is null ? null : Commit(origin, instantIfNoMarks: false);
            case OperatorActions.SaveClip: return Commit(origin, instantIfNoMarks: false);

            case OperatorActions.Tag:
                if (LastSavedClipId is { } last) TagPickerRequested?.Invoke(last);
                else return "No saved clip to tag yet.";
                return null;
            case "SetTag":
                var target = clipId ?? LastSavedClipId;
                if (target is null) return "No saved clip to tag yet.";
                if (string.IsNullOrWhiteSpace(text)) return "No tag given.";
                _clips.ToggleTag(target.Value, text);
                return null;
            case OperatorActions.Queue:
                var q = _automation.ArmedClip ?? (LastSavedClipId is { } id ? RefFor(id) : null);
                if (q is null) return "Nothing to queue.";
                _queue.Enqueue(q);
                _diag.Info(LogCategory.Clip, $"Queued {q.Description} ({_queue.Count} in queue)");
                return null;
            case "ArmClip":
                if (clipId is null) return "No clip.";
                var r = RefFor(clipId.Value) ?? throw new KeyNotFoundException($"Clip #{clipId} not found.");
                _automation.Post(new ArmRequested(r));
                return null;

            case OperatorActions.NextAngle: return CycleAngle(+1);
            case OperatorActions.PreviousAngle: return CycleAngle(-1);
            case OperatorActions.LiveMode: Mode = CueMode.Live; return null;
            case OperatorActions.JogMode: EnterJog(); return null;
            case OperatorActions.FrameBack: return Jog(-1);
            case OperatorActions.FrameForward: return Jog(+1);
            case OperatorActions.ShuttleBack: return Jog(-(int)Math.Round(Format.FramesPerSecond));
            case OperatorActions.ShuttleForward: return Jog((int)Math.Round(Format.FramesPerSecond));
            case "Jog": return Jog((int)(value ?? 0));
            case "Seek": return Seek(value ?? 0);

            case OperatorActions.Speed100: return SetSpeed(1.0);
            case OperatorActions.Speed50: return SetSpeed(0.5);
            case OperatorActions.Speed25: return SetSpeed(0.25);
            case "SetSpeed": return SetSpeed(Math.Clamp(value ?? 1.0, 0.01, 2.0));
        }
        return $"Unknown action '{action}'.";
    }

    // ---------------- cueing ----------------

    private long CurrentTicks() => Mode == CueMode.Jog ? CueTicks : _clock.NowTicks;

    private void EnterJog()
    {
        if (Mode == CueMode.Jog) return;
        var newest = _buffers.Get(SelectedCamera)?.Newest?.TimestampTicks ?? _clock.NowTicks;
        CueTicks = _clips.MarkInTicks ?? newest;
        Mode = CueMode.Jog;
    }

    private string? Jog(int frames)
    {
        if (frames == 0) return null;
        EnterJog();
        var b = _buffers.Get(SelectedCamera);
        if (b?.Oldest is not { } oldest || b.Newest is not { } newest) return $"Cam {SelectedCamera} has no recorded frames.";
        var next = CueTicks + frames * Format.FrameDurationTicks;
        CueTicks = Math.Clamp(next, oldest.TimestampTicks, newest.TimestampTicks);
        return null;
    }

    /// <summary>Cues to <paramref name="secondsFromLive"/> (negative) on the selected camera.</summary>
    private string? Seek(double secondsFromLive)
    {
        var b = _buffers.Get(SelectedCamera);
        if (b?.Oldest is not { } oldest || b.Newest is not { } newest) return $"Cam {SelectedCamera} has no recorded frames.";
        Mode = CueMode.Jog;
        CueTicks = Math.Clamp(newest.TimestampTicks + TimeSpan.FromSeconds(secondsFromLive).Ticks, oldest.TimestampTicks, newest.TimestampTicks);
        return null;
    }

    private string? SelectCamera(int cam)
    {
        var src = _settings.Sources.FirstOrDefault(s => s.SourceId == cam && s.Role == SourceRole.Camera);
        if (src is null || !src.Enabled) return $"Cam {cam} is not enabled.";
        SelectedCamera = cam;

        // During a replay, cut the replay itself to the new angle at the same moment.
        if (_automation.PlayingClip is { } playing && playing.SourceId != cam &&
            _automation.State is ReplayState.PLAYING or ReplayState.WAITING_FOR_PROGRAM)
        {
            var moved = playing with { SourceId = cam, Description = Describe(playing.ClipId, cam, playing.InTicks, playing.OutTicks) };
            IFrameSequence angle;
            try { angle = OpenClip(moved); }
            catch (FrameUnavailableException e) { return e.Message; }
            if (_playback.SwitchAngle(angle))
            {
                _automation.Post(new PlayingAngleChanged(moved));
                _diag.Info(LogCategory.Playback, $"Replay angle → Cam {cam}");
                return null;
            }
        }

        // Angle change re-arms the same moment on the new camera. Never substitutes a missing angle.
        if (_automation.ArmedClip is { } armed && armed.SourceId != cam)
        {
            if (armed.IsSaved)
            {
                var c = _clips.Get(armed.ClipId);
                if (c is null || !c.Files.ContainsKey(cam)) return $"Clip #{armed.ClipId} has no Cam {cam} angle.";
            }
            _automation.Post(new ArmRequested(armed with { SourceId = cam, Description = Describe(armed.ClipId, cam, armed.InTicks, armed.OutTicks) }));
        }
        return null;
    }

    private string? CycleAngle(int dir)
    {
        var cams = _capture.RunningCameraIds;
        if (cams.Count == 0) return "No cameras running.";
        var i = cams.ToList().IndexOf(SelectedCamera);
        var next = cams[((i < 0 ? 0 : i + dir) % cams.Count + cams.Count) % cams.Count];
        return SelectCamera(next);
    }

    private string? SetSpeed(double speed)
    {
        Speed = speed;
        _playback.SetSpeed(speed);
        if (_automation.ArmedClip is { } armed)
            _automation.Post(new ArmRequested(armed with { Speed = speed }));
        return null;
    }

    // ---------------- clips ----------------

    private string Describe(long clipId, int cam, long inT, long outT) =>
        $"{(clipId > 0 ? $"#{clipId}" : "Buffer")} · Cam {cam} · {TimeSpan.FromTicks(outT - inT).TotalSeconds:0.0} s @ {Speed:P0}";

    private ClipRef? RefFor(long clipId)
    {
        var c = _clips.Get(clipId);
        if (c is null) return null;
        var cam = c.Files.ContainsKey(SelectedCamera) ? SelectedCamera : c.PrimarySourceId;
        return new ClipRef(c.Id, cam, Speed, Describe(c.Id, cam, c.InTicks, c.OutTicks), c.InTicks, c.OutTicks);
    }

    /// <summary>Arms the marked range (or the last few seconds) from the buffer at once, then saves all angles in the background.</summary>
    private string? Commit(string origin, bool instantIfNoMarks)
    {
        if (_clips.MarkInTicks is null && _clips.MarkOutTicks is null && !instantIfNoMarks)
            return "Mark IN and OUT first.";
        if (_buffers.Get(SelectedCamera) is null) return $"Cam {SelectedCamera} is not recording.";

        var (inT, outT) = _clips.ResolveRange(InstantReplayLength);
        if (Mode == CueMode.Live && _clips.MarkOutTicks is null) outT = _clock.NowTicks;
        var cam = SelectedCamera;
        var unsaved = new ClipRef(0, cam, Speed, Describe(0, cam, inT, outT), inT, outT);
        _automation.Post(new ArmRequested(unsaved));
        _clips.ClearMarks();

        var cams = _capture.RunningCameraIds;
        _ = Task.Run(async () =>
        {
            try
            {
                var clip = await _clips.SaveClipAsync(inT, outT, cam, cams).ConfigureAwait(false);
                LastSavedClipId = clip.Id;
                if (_automation.ArmedClip is { ClipId: 0 } a && a.InTicks == inT && a.OutTicks == outT)
                    _automation.Post(new ArmRequested(a with { ClipId = clip.Id, Description = Describe(clip.Id, a.SourceId, inT, outT) }));
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                LastAlert = $"Clip not saved: {e.Message}";
                LastAlertLevel = AlertLevel.Warning;
                _diag.Error(LogCategory.Clip, LastAlert);
                Changed?.Invoke();
            }
        });
        return null;
    }

    /// <summary>Opens the frames to play for a clip reference. Never substitutes another angle.</summary>
    public IFrameSequence OpenClip(ClipRef r) =>
        r.IsSaved ? _clips.OpenAngle(r.ClipId, r.SourceId) : _buffers.GetSequence(r.SourceId, r.InTicks, r.OutTicks);

    private void OnStateChanged(ReplayState s)
    {
        if (s is ReplayState.RECORDING or ReplayState.LIVE_READY && _queue.TryDequeue(out var next))
            _automation.Post(new ArmRequested(next));
        Changed?.Invoke();
    }

    // ---------------- previews ----------------

    /// <summary>Frame for the PREVIEW monitor: live head of the selected camera, or the cued frame in jog mode.</summary>
    public EncodedFrame? PreviewFrame(int? camera = null)
    {
        var cam = camera ?? SelectedCamera;
        if (Mode == CueMode.Live) return _capture.GetLatest(cam);
        var b = _buffers.Get(cam);
        if (b?.FindAtOrBefore(CueTicks) is not { } e) return null;
        var data = b.TryRead(e);
        return data is null ? null : new EncodedFrame
        {
            SourceId = cam, FrameNumber = e.FrameNumber, TimestampTicks = e.TimestampTicks,
            Width = e.Width, Height = e.Height, Codec = e.Codec, Data = data,
        };
    }

    /// <summary>Seconds relative to the live head (negative = in the past).</summary>
    public double PositionSeconds
    {
        get
        {
            if (Mode == CueMode.Live) return 0;
            var newest = _buffers.Get(SelectedCamera)?.Newest?.TimestampTicks ?? _clock.NowTicks;
            return TimeSpan.FromTicks(CueTicks - newest).TotalSeconds;
        }
    }

    // ---------------- IRemoteHandler ----------------

    public RemoteState GetState()
    {
        var atem = _atem();
        double? Rel(long? t) => t is { } v ? TimeSpan.FromTicks(v - _clock.NowTicks).TotalSeconds : null;
        var cams = _settings.Sources.Where(s => s.Role == SourceRole.Camera && s.Enabled).Select(s =>
        {
            var h = _diag.SourceHealthSnapshot().FirstOrDefault(x => x.SourceId == s.SourceId);
            return new RemoteCamera(s.SourceId, s.Name, h?.Status.ToString() ?? "Stopped", s.Backend == CaptureBackendKind.Simulated);
        }).ToList();
        return new RemoteState(
            _automation.State.ToString(), atem.Connected, atem.ProgramInput, atem.PreviewInput, _settings.Atem.ReplayInput,
            SelectedCamera, Speed, Mode.ToString(), Rel(_clips.MarkInTicks), Rel(_clips.MarkOutTicks), PositionSeconds,
            _settings.Buffer.Seconds, _automation.ArmedClip?.Description, LastAlert, _automation.OverrideActive,
            _settings.Remote.AllowTransport, cams, StandardTags.Cricket);
    }

    public IReadOnlyList<RemoteClip> GetClips()
    {
        var armed = _automation.ArmedClip?.ClipId;
        return _clips.List().Take(60).Select(c => new RemoteClip(c.Id, c.PrimarySourceId, string.Join(",", c.Tags), c.Title, c.Duration.TotalSeconds, c.Id == armed)).ToList();
    }

    public byte[]? GetPreviewJpeg(int? sourceId)
    {
        var f = PreviewFrame(sourceId);
        if (f is null) return null;
        lock (_phoneCacheGate)
        {
            if (_phoneCache is { } c && c.Src == f.SourceId && c.Frame == f.FrameNumber) return c.Jpeg;
        }
        // Downscale for phones: decode at 1/4 size and re-encode.
        var small = JpegCodec.EncodeBgra(JpegCodec.Decode(f, 4), 75);
        lock (_phoneCacheGate) _phoneCache = (f.SourceId, f.FrameNumber, small);
        return small;
    }
}
