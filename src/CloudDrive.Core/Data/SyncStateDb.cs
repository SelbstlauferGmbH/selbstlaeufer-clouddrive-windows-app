using System.Diagnostics;
using System.Globalization;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Data;

public class SyncStateDb : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly ILogger<SyncStateDb>? _logger;
    private readonly object _gate = new();

    public SyncStateDb(string dbPath, ILogger<SyncStateDb>? logger = null)
    {
        _logger = logger;
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        ConfigureConnection();
        Initialize();
    }

    private void ConfigureConnection()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sync_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    local_path TEXT NOT NULL UNIQUE,
                    remote_path TEXT NOT NULL,
                    is_directory INTEGER NOT NULL DEFAULT 0,
                    file_size INTEGER NOT NULL DEFAULT 0,
                    remote_etag TEXT,
                    remote_last_modified TEXT,
                    local_hash TEXT,
                    sync_status INTEGER NOT NULL DEFAULT 0,
                    last_synced TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_sync_items_remote ON sync_items(remote_path);
                CREATE INDEX IF NOT EXISTS idx_sync_items_status ON sync_items(sync_status);
                CREATE TABLE IF NOT EXISTS sync_problems (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    dedupe_key TEXT,
                    problem_type INTEGER NOT NULL,
                    severity INTEGER NOT NULL,
                    status INTEGER NOT NULL DEFAULT 0,
                    title TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    details TEXT,
                    local_path TEXT,
                    remote_path TEXT,
                    conflict_copy_path TEXT,
                    occurrence_count INTEGER NOT NULL DEFAULT 1,
                    first_occurred_at TEXT NOT NULL DEFAULT (datetime('now')),
                    last_occurred_at TEXT NOT NULL DEFAULT (datetime('now')),
                    resolved_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_sync_problems_status ON sync_problems(status, last_occurred_at DESC);
                CREATE INDEX IF NOT EXISTS idx_sync_problems_dedupe ON sync_problems(dedupe_key);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public SyncItem? GetByLocalPath(string localPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_items WHERE local_path = @path";
            cmd.Parameters.AddWithValue("@path", localPath);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    public SyncItem? GetByRemotePath(string remotePath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_items WHERE remote_path = @path";
            cmd.Parameters.AddWithValue("@path", remotePath);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadItem(reader) : null;
        }
    }

    public List<SyncItem> GetChildren(string localDirectoryPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM sync_items
                WHERE local_path LIKE @pattern
                AND local_path NOT LIKE @subPattern
                """;
            var prefix = localDirectoryPath.TrimEnd('\\') + "\\";
            cmd.Parameters.AddWithValue("@pattern", prefix + "%");
            cmd.Parameters.AddWithValue("@subPattern", prefix + "%\\%");

            var items = new List<SyncItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(ReadItem(reader));
            return items;
        }
    }

    public List<SyncItem> GetAll()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_items ORDER BY local_path";

            var items = new List<SyncItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(ReadItem(reader));
            return items;
        }
    }

    public List<SyncItem> GetByStatus(SyncStatus status)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_items WHERE sync_status = @status";
            cmd.Parameters.AddWithValue("@status", (int)status);

            var items = new List<SyncItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(ReadItem(reader));
            return items;
        }
    }

    public void Upsert(SyncItem item)
    {
        lock (_gate)
        {
            var existing = GetByLocalPath(item.LocalPath);
            var oldStatus = existing?.SyncStatus;
            _logger?.LogDebug("DB_UPSERT {Path} {OldStatus} -> {NewStatus}",
                item.LocalPath, oldStatus?.ToString() ?? "NEW", item.SyncStatus);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_items (local_path, remote_path, is_directory, file_size, remote_etag, remote_last_modified, local_hash, sync_status, last_synced, updated_at)
                VALUES (@localPath, @remotePath, @isDir, @size, @etag, @lastMod, @hash, @status, @lastSynced, datetime('now'))
                ON CONFLICT(local_path) DO UPDATE SET
                    remote_path = @remotePath,
                    is_directory = @isDir,
                    file_size = @size,
                    remote_etag = @etag,
                    remote_last_modified = @lastMod,
                    local_hash = @hash,
                    sync_status = @status,
                    last_synced = @lastSynced,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@localPath", item.LocalPath);
            cmd.Parameters.AddWithValue("@remotePath", item.RemotePath);
            cmd.Parameters.AddWithValue("@isDir", item.IsDirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("@size", item.FileSize);
            cmd.Parameters.AddWithValue("@etag", (object?)item.RemoteETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastMod", item.RemoteLastModified?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@hash", (object?)item.LocalHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)item.SyncStatus);
            cmd.Parameters.AddWithValue("@lastSynced", item.LastSynced?.ToString("o") ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateStatus(string localPath, SyncStatus status)
    {
        var stopwatch = Stopwatch.StartNew();

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_items SET sync_status = @status, updated_at = datetime('now')
                WHERE local_path = @path
                """;
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@path", localPath);
            cmd.ExecuteNonQuery();
        }

        if (stopwatch.ElapsedMilliseconds >= 250)
        {
            _logger?.LogWarning(
                "DB_UPDATE_STATUS slow: {Path} Status={Status} DurationMs={DurationMs}",
                localPath,
                status,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public void Delete(string localPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_items WHERE local_path = @path";
            cmd.Parameters.AddWithValue("@path", localPath);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteChildren(string localDirectoryPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_items WHERE local_path LIKE @pattern";
            cmd.Parameters.AddWithValue("@pattern", localDirectoryPath.TrimEnd('\\') + "\\%");
            cmd.ExecuteNonQuery();
        }
    }

    public int EnsureProblemEntriesForTrackedItemStates()
    {
        lock (_gate)
        {
            var legacyItems = GetLegacyProblemItems_NoLock();
            var imported = 0;

            foreach (var item in legacyItems)
            {
                var localizer = AppLocalizer.Instance;
                var fileName = Path.GetFileName(item.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var isConflict = item.SyncStatus == SyncStatus.Conflict;

                UpsertProblem(new SyncProblem
                {
                    DedupeKey = isConflict
                        ? SyncProblemKeys.Conflict(item.LocalPath)
                        : SyncProblemKeys.Upload(item.LocalPath),
                    ProblemType = isConflict ? SyncProblemType.Conflict : SyncProblemType.Upload,
                    Severity = isConflict ? SyncProblemSeverity.Warning : SyncProblemSeverity.Error,
                    Title = isConflict
                        ? localizer.Format("Problem_ImportedConflict_Title", fileName)
                        : localizer.Format("Problem_CouldNotSync_Title", fileName),
                    Summary = isConflict
                        ? localizer.GetString("Problem_ImportedConflict_Summary")
                        : localizer.GetString("Problem_ImportedUpload_Summary"),
                    Details = localizer.GetString("Problem_Imported_Detail"),
                    LocalPath = item.LocalPath,
                    RemotePath = item.RemotePath,
                    FirstOccurredAt = item.UpdatedAt == default ? DateTime.UtcNow : item.UpdatedAt,
                    LastOccurredAt = item.UpdatedAt == default ? DateTime.UtcNow : item.UpdatedAt
                });

                imported++;
            }

            return imported;
        }
    }

    public IReadOnlyList<SyncProblem> GetProblems(bool openOnly = false, int? limit = null)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM sync_problems
                WHERE (@openOnly = 0 OR status = @openStatus)
                ORDER BY last_occurred_at DESC, id DESC
                """ + (limit.HasValue ? "\nLIMIT @limit" : string.Empty);
            cmd.Parameters.AddWithValue("@openOnly", openOnly ? 1 : 0);
            cmd.Parameters.AddWithValue("@openStatus", (int)SyncProblemStatus.Open);
            if (limit.HasValue)
                cmd.Parameters.AddWithValue("@limit", limit.Value);

            var items = new List<SyncProblem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                items.Add(ReadProblem(reader));
            return items;
        }
    }

    public SyncProblem UpsertProblem(SyncProblem problem)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(problem.DedupeKey))
            {
                var existing = GetOpenProblemByDedupeKey_NoLock(problem.DedupeKey);
                if (existing != null)
                {
                    using var update = _connection.CreateCommand();
                    update.CommandText = """
                        UPDATE sync_problems
                        SET
                            problem_type = @problemType,
                            severity = @severity,
                            title = @title,
                            summary = @summary,
                            details = @details,
                            local_path = @localPath,
                            remote_path = @remotePath,
                            conflict_copy_path = @conflictCopyPath,
                            occurrence_count = occurrence_count + 1,
                            last_occurred_at = @lastOccurredAt,
                            resolved_at = NULL
                        WHERE id = @id
                        """;
                    update.Parameters.AddWithValue("@problemType", (int)problem.ProblemType);
                    update.Parameters.AddWithValue("@severity", (int)problem.Severity);
                    update.Parameters.AddWithValue("@title", problem.Title);
                    update.Parameters.AddWithValue("@summary", problem.Summary);
                    update.Parameters.AddWithValue("@details", problem.Details);
                    update.Parameters.AddWithValue("@localPath", (object?)problem.LocalPath ?? DBNull.Value);
                    update.Parameters.AddWithValue("@remotePath", (object?)problem.RemotePath ?? DBNull.Value);
                    update.Parameters.AddWithValue("@conflictCopyPath", (object?)problem.ConflictCopyPath ?? DBNull.Value);
                    update.Parameters.AddWithValue("@lastOccurredAt", now.ToString("o"));
                    update.Parameters.AddWithValue("@id", existing.Id);
                    update.ExecuteNonQuery();

                    return GetProblemById_NoLock(existing.Id)!;
                }
            }

            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sync_problems (
                    dedupe_key,
                    problem_type,
                    severity,
                    status,
                    title,
                    summary,
                    details,
                    local_path,
                    remote_path,
                    conflict_copy_path,
                    occurrence_count,
                    first_occurred_at,
                    last_occurred_at,
                    resolved_at
                )
                VALUES (
                    @dedupeKey,
                    @problemType,
                    @severity,
                    @status,
                    @title,
                    @summary,
                    @details,
                    @localPath,
                    @remotePath,
                    @conflictCopyPath,
                    @occurrenceCount,
                    @firstOccurredAt,
                    @lastOccurredAt,
                    @resolvedAt
                );
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@dedupeKey", (object?)problem.DedupeKey ?? DBNull.Value);
            insert.Parameters.AddWithValue("@problemType", (int)problem.ProblemType);
            insert.Parameters.AddWithValue("@severity", (int)problem.Severity);
            insert.Parameters.AddWithValue("@status", (int)problem.Status);
            insert.Parameters.AddWithValue("@title", problem.Title);
            insert.Parameters.AddWithValue("@summary", problem.Summary);
            insert.Parameters.AddWithValue("@details", problem.Details);
            insert.Parameters.AddWithValue("@localPath", (object?)problem.LocalPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("@remotePath", (object?)problem.RemotePath ?? DBNull.Value);
            insert.Parameters.AddWithValue("@conflictCopyPath", (object?)problem.ConflictCopyPath ?? DBNull.Value);
            insert.Parameters.AddWithValue("@occurrenceCount", Math.Max(1, problem.OccurrenceCount));
            insert.Parameters.AddWithValue("@firstOccurredAt", (problem.FirstOccurredAt == default ? now : problem.FirstOccurredAt).ToString("o"));
            insert.Parameters.AddWithValue("@lastOccurredAt", (problem.LastOccurredAt == default ? now : problem.LastOccurredAt).ToString("o"));
            insert.Parameters.AddWithValue("@resolvedAt", problem.ResolvedAt?.ToString("o") ?? (object)DBNull.Value);

            var id = Convert.ToInt64(insert.ExecuteScalar());
            return GetProblemById_NoLock(id)!;
        }
    }

    public void ResolveProblem(long problemId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_problems
                SET status = @status, resolved_at = @resolvedAt
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@status", (int)SyncProblemStatus.Resolved);
            cmd.Parameters.AddWithValue("@resolvedAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@id", problemId);
            cmd.ExecuteNonQuery();
        }
    }

    public void ResolveProblemsByDedupeKey(string dedupeKey)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_problems
                SET status = @status, resolved_at = @resolvedAt
                WHERE dedupe_key = @dedupeKey
                  AND status = @openStatus
                """;
            cmd.Parameters.AddWithValue("@status", (int)SyncProblemStatus.Resolved);
            cmd.Parameters.AddWithValue("@resolvedAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@dedupeKey", dedupeKey);
            cmd.Parameters.AddWithValue("@openStatus", (int)SyncProblemStatus.Open);
            cmd.ExecuteNonQuery();
        }
    }

    private static SyncItem ReadItem(SqliteDataReader reader)
    {
        return new SyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            LocalPath = reader.GetString(reader.GetOrdinal("local_path")),
            RemotePath = reader.GetString(reader.GetOrdinal("remote_path")),
            IsDirectory = reader.GetInt32(reader.GetOrdinal("is_directory")) == 1,
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            RemoteETag = reader.IsDBNull(reader.GetOrdinal("remote_etag")) ? null : reader.GetString(reader.GetOrdinal("remote_etag")),
            RemoteLastModified = reader.IsDBNull(reader.GetOrdinal("remote_last_modified")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("remote_last_modified"))),
            LocalHash = reader.IsDBNull(reader.GetOrdinal("local_hash")) ? null : reader.GetString(reader.GetOrdinal("local_hash")),
            SyncStatus = (SyncStatus)reader.GetInt32(reader.GetOrdinal("sync_status")),
            LastSynced = reader.IsDBNull(reader.GetOrdinal("last_synced")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("last_synced"))),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private SyncProblem? GetOpenProblemByDedupeKey_NoLock(string dedupeKey)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM sync_problems
            WHERE dedupe_key = @dedupeKey
              AND status = @status
            ORDER BY last_occurred_at DESC, id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@dedupeKey", dedupeKey);
        cmd.Parameters.AddWithValue("@status", (int)SyncProblemStatus.Open);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProblem(reader) : null;
    }

    private List<SyncItem> GetLegacyProblemItems_NoLock()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.*
            FROM sync_items s
            WHERE s.sync_status IN (@errorStatus, @conflictStatus)
              AND NOT EXISTS (
                  SELECT 1
                  FROM sync_problems p
                  WHERE p.status = @openStatus
                    AND p.local_path = s.local_path
              )
            ORDER BY s.updated_at DESC, s.id DESC
            """;
        cmd.Parameters.AddWithValue("@errorStatus", (int)SyncStatus.Error);
        cmd.Parameters.AddWithValue("@conflictStatus", (int)SyncStatus.Conflict);
        cmd.Parameters.AddWithValue("@openStatus", (int)SyncProblemStatus.Open);

        var items = new List<SyncItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(ReadItem(reader));

        return items;
    }

    private SyncProblem? GetProblemById_NoLock(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM sync_problems WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProblem(reader) : null;
    }

    private static SyncProblem ReadProblem(SqliteDataReader reader)
    {
        return new SyncProblem
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            DedupeKey = reader.IsDBNull(reader.GetOrdinal("dedupe_key")) ? null : reader.GetString(reader.GetOrdinal("dedupe_key")),
            ProblemType = (SyncProblemType)reader.GetInt32(reader.GetOrdinal("problem_type")),
            Severity = (SyncProblemSeverity)reader.GetInt32(reader.GetOrdinal("severity")),
            Status = (SyncProblemStatus)reader.GetInt32(reader.GetOrdinal("status")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            Details = reader.IsDBNull(reader.GetOrdinal("details")) ? string.Empty : reader.GetString(reader.GetOrdinal("details")),
            LocalPath = reader.IsDBNull(reader.GetOrdinal("local_path")) ? null : reader.GetString(reader.GetOrdinal("local_path")),
            RemotePath = reader.IsDBNull(reader.GetOrdinal("remote_path")) ? null : reader.GetString(reader.GetOrdinal("remote_path")),
            ConflictCopyPath = reader.IsDBNull(reader.GetOrdinal("conflict_copy_path")) ? null : reader.GetString(reader.GetOrdinal("conflict_copy_path")),
            OccurrenceCount = reader.GetInt32(reader.GetOrdinal("occurrence_count")),
            FirstOccurredAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("first_occurred_at"))),
            LastOccurredAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_occurred_at"))),
            ResolvedAt = reader.IsDBNull(reader.GetOrdinal("resolved_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("resolved_at")))
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    /// <summary>
    /// Gets database statistics for display in the activity panel.
    /// </summary>
    public DatabaseStatistics GetStatistics()
    {
        lock (_gate)
        {
            var fileInfo = new FileInfo(_dbPath);
            
            var stats = new DatabaseStatistics
            {
                DatabasePath = _dbPath,
                DatabaseSize = fileInfo.Exists ? fileInfo.Length : 0L,
                LastModified = fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue
            };

            // Get total count
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sync_items";
            var result = cmd.ExecuteScalar();
            stats.TotalItems = Convert.ToInt64(result);

            cmd.CommandText = "SELECT COALESCE(SUM(file_size), 0) FROM sync_items";
            cmd.Parameters.Clear();
            result = cmd.ExecuteScalar();
            stats.TotalTrackedData = Convert.ToInt64(result);

            // Get count by status
            foreach (SyncStatus status in Enum.GetValues(typeof(SyncStatus)))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sync_items WHERE sync_status = @status";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@status", (int)status);
                result = cmd.ExecuteScalar();
                stats.CountByStatus[status] = Convert.ToInt64(result);
            }

            // Get last synced timestamp
            cmd.CommandText = "SELECT MAX(last_synced) FROM sync_items WHERE last_synced IS NOT NULL";
            cmd.Parameters.Clear();
            result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value && !string.IsNullOrEmpty(result.ToString()))
            {
                stats.LastSynced = DateTime.Parse(result.ToString()!);
            }

            cmd.CommandText = "SELECT COUNT(*) FROM sync_problems WHERE status = @status";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@status", (int)SyncProblemStatus.Open);
            result = cmd.ExecuteScalar();
            stats.OpenProblemCount = Convert.ToInt64(result);

            cmd.CommandText = """
                SELECT COUNT(*) FROM sync_problems
                WHERE status = @status AND problem_type = @problemType
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@status", (int)SyncProblemStatus.Open);
            cmd.Parameters.AddWithValue("@problemType", (int)SyncProblemType.Conflict);
            result = cmd.ExecuteScalar();
            stats.OpenConflictProblemCount = Convert.ToInt64(result);

            return stats;
        }
    }
}

/// <summary>
/// Contains database statistics for display purposes.
/// </summary>
public class DatabaseStatistics
{
    public string DatabasePath { get; set; } = string.Empty;
    public long DatabaseSize { get; set; }
    public DateTime LastModified { get; set; }
    public long TotalItems { get; set; }
    public long TotalTrackedData { get; set; }
    public Dictionary<SyncStatus, long> CountByStatus { get; set; } = new();
    public DateTime? LastSynced { get; set; }
    public long OpenProblemCount { get; set; }
    public long OpenConflictProblemCount { get; set; }
    public long PendingItems =>
        GetCount(SyncStatus.PendingUpload) +
        GetCount(SyncStatus.PendingDownload) +
        GetCount(SyncStatus.Syncing);
    public long ErrorItems =>
        GetCount(SyncStatus.Error) +
        GetCount(SyncStatus.Conflict);
    public string TotalTrackedDataDisplay => FormatFileSize(TotalTrackedData);

    /// <summary>
    /// Formatted database size for display.
    /// </summary>
    public string DatabaseSizeDisplay => FormatFileSize(DatabaseSize);

    /// <summary>
    /// Formatted last modified time for display.
    /// </summary>
    public string LastModifiedDisplay => LastModified == DateTime.MinValue 
        ? AppLocalizer.Instance.GetString("Database_LastModified_Never")
        : LastModified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>
    /// Formatted last synced time for display.
    /// </summary>
    public string LastSyncedDisplay => LastSynced.HasValue 
        ? LastSynced.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) 
        : AppLocalizer.Instance.GetString("Database_LastSynced_Never");

    private long GetCount(SyncStatus status) => CountByStatus.TryGetValue(status, out var count) ? count : 0L;

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F2} {sizes[order]}";
    }
}
