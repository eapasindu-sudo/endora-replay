using System.Text.Json;
using EndoraReplay.Core.Model;
using Microsoft.Data.Sqlite;

namespace EndoraReplay.Core.Services;

/// <summary>Clip metadata in SQLite plus disk-space policy for the data folder.</summary>
public sealed class StorageService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly DiagnosticsService _diag;

    public StorageService(string dataFolder, int minFreeDiskGb, DiagnosticsService diagnostics)
    {
        DataFolder = dataFolder;
        ClipsFolder = Path.Combine(dataFolder, "clips");
        BufferFolder = Path.Combine(dataFolder, "buffer");
        LogFolder = Path.Combine(dataFolder, "logs");
        MinFreeBytes = (long)minFreeDiskGb << 30;
        _diag = diagnostics;
        Directory.CreateDirectory(ClipsFolder);
        Directory.CreateDirectory(BufferFolder);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolder, "endora.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS clips (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                in_ticks INTEGER NOT NULL,
                out_ticks INTEGER NOT NULL,
                primary_source INTEGER NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '[]',
                files TEXT NOT NULL DEFAULT '{}',
                protected INTEGER NOT NULL DEFAULT 1,
                session TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_clips_created ON clips(created_utc);
            """);
    }

    public string DataFolder { get; }
    public string ClipsFolder { get; }
    public string BufferFolder { get; }
    public string LogFolder { get; }
    public long MinFreeBytes { get; }

    /// <summary>Identifies the capture session; clip in/out ticks are only meaningful within one session.</summary>
    public string SessionId { get; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

    public long FreeBytes
    {
        get
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DataFolder))!).AvailableFreeSpace; }
            catch (Exception) { return long.MaxValue; }
        }
    }

    /// <summary>True when there is room to save a clip of <paramref name="estimatedBytes"/> without crossing the limit.</summary>
    public bool HasRoomFor(long estimatedBytes, out string reason)
    {
        var free = FreeBytes;
        if (free - estimatedBytes < MinFreeBytes)
        {
            reason = $"Only {free / (1L << 30)} GB free; keeping {MinFreeBytes / (1L << 30)} GB reserved. Delete old clips or free disk space.";
            return false;
        }
        reason = "";
        return true;
    }

    public long InsertClip(Clip clip)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clips (created_utc, in_ticks, out_ticks, primary_source, title, tags, files, protected, session)
                VALUES ($c, $i, $o, $p, $t, $tags, $files, $prot, $s);
                SELECT last_insert_rowid();
                """;
            Bind(cmd, clip);
            cmd.Parameters.AddWithValue("$s", SessionId);
            clip.Id = (long)cmd.ExecuteScalar()!;
            return clip.Id;
        }
    }

    public void UpdateClip(Clip clip)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE clips SET created_utc=$c, in_ticks=$i, out_ticks=$o, primary_source=$p, title=$t,
                tags=$tags, files=$files, protected=$prot WHERE id=$id;
                """;
            Bind(cmd, clip);
            cmd.Parameters.AddWithValue("$id", clip.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public Clip? GetClip(long id) => Query("SELECT * FROM clips WHERE id=$id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<Clip> ListClips(int limit = 500, string? tag = null)
    {
        var all = Query("SELECT * FROM clips ORDER BY id DESC LIMIT $l", ("$l", limit));
        return tag is null ? all : all.Where(c => c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Deletes a clip's metadata and files.</summary>
    public void DeleteClip(long id)
    {
        var clip = GetClip(id);
        if (clip is null) return;
        foreach (var f in clip.Files.Values)
        {
            try { File.Delete(f); } catch (IOException e) { _diag.Warn(LogCategory.Storage, $"Could not delete {f}: {e.Message}"); }
        }
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM clips WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        _diag.Info(LogCategory.Storage, $"Deleted clip #{id}");
    }

    public long ClipsBytes() =>
        Directory.Exists(ClipsFolder) ? Directory.EnumerateFiles(ClipsFolder).Sum(f => new FileInfo(f).Length) : 0;

    private static void Bind(SqliteCommand cmd, Clip clip)
    {
        cmd.Parameters.AddWithValue("$c", clip.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$i", clip.InTicks);
        cmd.Parameters.AddWithValue("$o", clip.OutTicks);
        cmd.Parameters.AddWithValue("$p", clip.PrimarySourceId);
        cmd.Parameters.AddWithValue("$t", clip.Title);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(clip.Tags));
        cmd.Parameters.AddWithValue("$files", JsonSerializer.Serialize(clip.Files));
        cmd.Parameters.AddWithValue("$prot", clip.Protected ? 1 : 0);
    }

    private List<Clip> Query(string sql, params (string Name, object Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            using var r = cmd.ExecuteReader();
            var list = new List<Clip>();
            while (r.Read())
            {
                list.Add(new Clip
                {
                    Id = r.GetInt64(r.GetOrdinal("id")),
                    CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    InTicks = r.GetInt64(r.GetOrdinal("in_ticks")),
                    OutTicks = r.GetInt64(r.GetOrdinal("out_ticks")),
                    PrimarySourceId = r.GetInt32(r.GetOrdinal("primary_source")),
                    Title = r.GetString(r.GetOrdinal("title")),
                    Tags = JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("tags"))) ?? [],
                    Files = JsonSerializer.Deserialize<Dictionary<int, string>>(r.GetString(r.GetOrdinal("files"))) ?? [],
                    Protected = r.GetInt32(r.GetOrdinal("protected")) != 0,
                });
            }
            return list;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
