using EndoraReplay.Core.Abstractions;
using EndoraReplay.Core.Model;
using EndoraReplay.Core.Storage;

namespace EndoraReplay.Core.Services;

/// <summary>Creates, saves, tags and retrieves replay clips. Saved clips are copied out of the rolling buffer so they survive it.</summary>
public sealed class ClipService
{
    private readonly RollingBufferService _buffers;
    private readonly StorageService _storage;
    private readonly DiagnosticsService _diag;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public ClipService(RollingBufferService buffers, StorageService storage, DiagnosticsService diagnostics, IClock clock)
    {
        _buffers = buffers;
        _storage = storage;
        _diag = diagnostics;
        _clock = clock;
    }

    public long? MarkInTicks { get; private set; }
    public long? MarkOutTicks { get; private set; }

    /// <summary>Raised after a clip is saved, updated or deleted (any thread).</summary>
    public event Action? ClipsChanged;

    public void MarkIn(long ticks)
    {
        lock (_gate)
        {
            MarkInTicks = ticks;
            if (MarkOutTicks is { } o && o <= ticks) MarkOutTicks = null;
        }
        _diag.Info(LogCategory.Clip, $"Mark IN at {TimeSpan.FromTicks(ticks):hh\\:mm\\:ss\\.ff}");
    }

    public void MarkOut(long ticks)
    {
        lock (_gate)
        {
            MarkOutTicks = ticks;
            if (MarkInTicks is { } i && i >= ticks) MarkInTicks = null;
        }
        _diag.Info(LogCategory.Clip, $"Mark OUT at {TimeSpan.FromTicks(ticks):hh\\:mm\\:ss\\.ff}");
    }

    public void ClearMarks()
    {
        lock (_gate) { MarkInTicks = null; MarkOutTicks = null; }
    }

    /// <summary>
    /// Resolves the in/out range: explicit marks, or a default length ending at OUT (or now) when IN is missing.
    /// </summary>
    public (long In, long Out) ResolveRange(TimeSpan defaultLength)
    {
        lock (_gate)
        {
            var outT = MarkOutTicks ?? _clock.NowTicks;
            var inT = MarkInTicks ?? outT - defaultLength.Ticks;
            return (Math.Max(0, inT), outT);
        }
    }

    /// <summary>
    /// Saves a clip for every source that has frames in [in, out]. Runs the copy on a worker thread.
    /// Throws if the disk limit would be crossed or no source holds the range.
    /// </summary>
    public async Task<Clip> SaveClipAsync(long inTicks, long outTicks, int primarySourceId, IEnumerable<int> sourceIds, IEnumerable<string>? tags = null, CancellationToken ct = default)
    {
        if (outTicks <= inTicks) throw new ArgumentException("Clip OUT must be after IN.");
        var ids = sourceIds.Distinct().ToList();
        if (!ids.Contains(primarySourceId)) ids.Insert(0, primarySourceId);

        var seconds = TimeSpan.FromTicks(outTicks - inTicks).TotalSeconds;
        var estimate = (long)(seconds * 50 * 400_000 * ids.Count);
        if (!_storage.HasRoomFor(estimate, out var reason))
        {
            _diag.Error(LogCategory.Storage, reason);
            throw new IOException(reason);
        }

        var clip = new Clip
        {
            InTicks = inTicks,
            OutTicks = outTicks,
            PrimarySourceId = primarySourceId,
            Tags = tags?.ToList() ?? [],
            CreatedUtc = _clock.UtcNow,
        };
        _storage.InsertClip(clip);

        var missing = new List<int>();
        await Task.Run(() =>
        {
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                IFrameSequence seq;
                try { seq = _buffers.GetSequence(id, inTicks, outTicks); }
                catch (FrameUnavailableException) { missing.Add(id); continue; }

                var path = Path.Combine(_storage.ClipsFolder, ClipFile.FileNameFor(clip.Id, id));
                var first = seq.Read(0);
                using var writer = new ClipFile.Writer(path, id, first.Width, first.Height, first.Codec);
                writer.Append(first.TimestampTicks, first.FrameNumber, first.Data);
                for (var i = 1; i < seq.Count; i++)
                {
                    try
                    {
                        var f = seq.Read(i);
                        writer.Append(f.TimestampTicks, f.FrameNumber, f.Data);
                    }
                    catch (FrameUnavailableException e)
                    {
                        // Overwritten mid-copy: keep what we have but say so; never fill the hole.
                        _diag.Warn(LogCategory.Clip, $"Clip #{clip.Id} cam {id}: {e.Message} Saved {writer.Count} of {seq.Count} frames.");
                        break;
                    }
                }
                writer.Complete();
                clip.Files[id] = path;
            }
        }, ct).ConfigureAwait(false);

        if (clip.Files.Count == 0)
        {
            _storage.DeleteClip(clip.Id);
            throw new FrameUnavailableException("None of the selected sources hold frames for that range.");
        }
        if (!clip.Files.ContainsKey(primarySourceId)) clip.PrimarySourceId = clip.Files.Keys.Min();
        _storage.UpdateClip(clip);

        _diag.Info(LogCategory.Clip, $"Saved clip #{clip.Id}: {clip.Duration.TotalSeconds:0.00} s, angles {string.Join(",", clip.Files.Keys.Order())}"
            + (missing.Count > 0 ? $"; no frames for {string.Join(",", missing)}" : ""));
        ClipsChanged?.Invoke();
        return clip;
    }

    public IReadOnlyList<Clip> List(string? tag = null) => _storage.ListClips(tag: tag);

    public Clip? Get(long id) => _storage.GetClip(id);

    public void SetTags(long clipId, IEnumerable<string> tags, string? title = null)
    {
        var c = _storage.GetClip(clipId) ?? throw new KeyNotFoundException($"Clip #{clipId} not found.");
        c.Tags = tags.Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        if (title is not null) c.Title = title;
        _storage.UpdateClip(c);
        ClipsChanged?.Invoke();
    }

    public void ToggleTag(long clipId, string tag)
    {
        var c = _storage.GetClip(clipId) ?? throw new KeyNotFoundException($"Clip #{clipId} not found.");
        var t = tag.Trim().ToUpperInvariant();
        if (!c.Tags.Remove(t)) c.Tags.Add(t);
        _storage.UpdateClip(c);
        ClipsChanged?.Invoke();
    }

    public void Delete(long clipId)
    {
        _storage.DeleteClip(clipId);
        ClipsChanged?.Invoke();
    }

    /// <summary>Opens the saved angle of a clip for playback. Throws if that angle was not captured; never substitutes another.</summary>
    public IFrameSequence OpenAngle(long clipId, int sourceId)
    {
        var c = _storage.GetClip(clipId) ?? throw new FrameUnavailableException($"Clip #{clipId} not found.");
        if (!c.Files.TryGetValue(sourceId, out var path))
            throw new FrameUnavailableException($"Clip #{clipId} has no Cam {sourceId} angle (captured: {string.Join(", ", c.Files.Keys.Order())}).");
        if (!File.Exists(path))
            throw new FrameUnavailableException($"Clip #{clipId} Cam {sourceId} file is missing: {path}");
        return new ClipFile.Reader(path);
    }
}
