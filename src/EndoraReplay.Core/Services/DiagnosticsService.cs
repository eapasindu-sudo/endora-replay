using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using EndoraReplay.Core.Model;

namespace EndoraReplay.Core.Services;

public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical,
}

/// <summary>Categories shown in the operator event log.</summary>
public static class LogCategory
{
    public const string Capture = "CAP";
    public const string Buffer = "BUF";
    public const string Clip = "CLIP";
    public const string Playback = "PLAY";
    public const string Output = "OUT";
    public const string Atem = "ATEM";
    public const string Automation = "AUTO";
    public const string Storage = "DISK";
    public const string Remote = "REMOTE";
    public const string App = "APP";
    public const string Update = "UPDATE";
}

public sealed record LogEvent(DateTime Utc, long SessionTicks, LogSeverity Severity, string Category, string Message, IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// Structured logging (JSON lines on disk), the in-memory operator event log, and per-source counters.
/// Thread-safe; logging never blocks the caller (file writes happen on a background task).
/// </summary>
public sealed class DiagnosticsService : IAsyncDisposable
{
    private readonly Channel<LogEvent> _fileQueue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly ConcurrentQueue<LogEvent> _recent = new();
    private readonly ConcurrentDictionary<int, SourceHealth> _sources = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly Func<long> _sessionTicks;
    private readonly Task? _writer;
    private readonly string? _logFolder;

    public DiagnosticsService(string? logFolder, Func<long> sessionTicks)
    {
        _sessionTicks = sessionTicks;
        _logFolder = logFolder;
        if (logFolder is not null)
        {
            Directory.CreateDirectory(logFolder);
            _writer = Task.Run(WriteLoopAsync);
        }
    }

    public const int RecentCapacity = 2000;

    public string? LogFolder => _logFolder;
    public string? CurrentLogFile { get; private set; }

    /// <summary>Raised for every event (any thread). UI must marshal.</summary>
    public event Action<LogEvent>? EventLogged;

    public void Log(LogSeverity severity, string category, string message, IReadOnlyDictionary<string, object?>? data = null)
    {
        var e = new LogEvent(DateTime.UtcNow, _sessionTicks(), severity, category, message, data);
        _recent.Enqueue(e);
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }
        _fileQueue.Writer.TryWrite(e);
        EventLogged?.Invoke(e);
    }

    public void Info(string category, string message, IReadOnlyDictionary<string, object?>? data = null) => Log(LogSeverity.Info, category, message, data);
    public void Warn(string category, string message, IReadOnlyDictionary<string, object?>? data = null) => Log(LogSeverity.Warning, category, message, data);
    public void Error(string category, string message, IReadOnlyDictionary<string, object?>? data = null) => Log(LogSeverity.Error, category, message, data);

    public IReadOnlyList<LogEvent> Recent(int max = 200) => _recent.Reverse().Take(max).ToList();

    public void Increment(string counter, long by = 1) => _counters.AddOrUpdate(counter, by, (_, v) => v + by);
    public long Counter(string counter) => _counters.TryGetValue(counter, out var v) ? v : 0;
    public IReadOnlyDictionary<string, long> Counters => _counters;

    public SourceHealth Health(int sourceId, string name, bool simulated) => _sources.GetOrAdd(sourceId, id => new SourceHealth(id, name, simulated));
    public void ReplaceHealth(SourceHealth health) => _sources[health.SourceId] = health;
    public IReadOnlyList<SourceHealth> SourceHealthSnapshot() => _sources.Values.Select(h => h.Clone()).OrderBy(h => h.SourceId).ToList();
    public void RemoveHealth(int sourceId) => _sources.TryRemove(sourceId, out _);

    public void ResetCounters()
    {
        _counters.Clear();
        foreach (var h in _sources.Values)
        {
            h.DroppedFrames = 0;
            h.TimestampGaps = 0;
            h.BufferOverflows = 0;
        }
    }

    /// <summary>Writes logs, counters and source health into a zip for support.</summary>
    public string ExportSupportBundle(string destinationFolder, string settingsJson)
    {
        Directory.CreateDirectory(destinationFolder);
        var path = Path.Combine(destinationFolder, $"endora-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        if (_logFolder is not null)
        {
            foreach (var f in Directory.EnumerateFiles(_logFolder, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var entry = zip.CreateEntry("logs/" + Path.GetFileName(f));
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        WriteEntry(zip, "settings.json", settingsJson);
        WriteEntry(zip, "health.json", JsonSerializer.Serialize(new { sources = SourceHealthSnapshot(), counters = _counters }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void WriteEntry(System.IO.Compression.ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var day = DateOnly.MinValue;
        try
        {
            await foreach (var e in _fileQueue.Reader.ReadAllAsync())
            {
                var today = DateOnly.FromDateTime(e.Utc.ToLocalTime());
                if (writer is null || today != day)
                {
                    if (writer is not null) await writer.DisposeAsync();
                    day = today;
                    CurrentLogFile = Path.Combine(_logFolder!, $"endora-{day:yyyyMMdd}.jsonl");
                    writer = new StreamWriter(new FileStream(CurrentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    t = e.Utc.ToString("O"),
                    st = e.SessionTicks,
                    sev = e.Severity.ToString(),
                    cat = e.Category,
                    msg = e.Message,
                    data = e.Data,
                }));
                if (_fileQueue.Reader.Count == 0) await writer.FlushAsync();
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fileQueue.Writer.TryComplete();
        if (_writer is not null) await _writer.ConfigureAwait(false);
    }
}
