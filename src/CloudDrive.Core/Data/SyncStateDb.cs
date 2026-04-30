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

                CREATE TABLE IF NOT EXISTS sync_journal (
                    file_id TEXT PRIMARY KEY,
                    local_path TEXT NOT NULL UNIQUE,
                    remote_path TEXT NOT NULL UNIQUE,
                    etag TEXT,
                    mtime_utc TEXT,
                    size INTEGER NOT NULL DEFAULT 0,
                    checksum TEXT,
                    pin_state TEXT,
                    is_directory INTEGER NOT NULL DEFAULT 0,
                    in_sync INTEGER NOT NULL DEFAULT 1,
                    base_etag TEXT,
                    local_pending_op TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_sync_journal_local ON sync_journal(local_path);
                CREATE INDEX IF NOT EXISTS idx_sync_journal_remote ON sync_journal(remote_path);
                CREATE INDEX IF NOT EXISTS idx_sync_journal_pending ON sync_journal(local_pending_op);

                CREATE TABLE IF NOT EXISTS pending_conflicts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id TEXT NOT NULL,
                    local_path TEXT NOT NULL,
                    remote_path TEXT NOT NULL,
                    base_etag TEXT,
                    local_checksum TEXT,
                    remote_etag TEXT,
                    local_mtime_utc TEXT,
                    remote_mtime_utc TEXT,
                    local_size INTEGER NOT NULL DEFAULT 0,
                    remote_size INTEGER NOT NULL DEFAULT 0,
                    remote_temp_path TEXT,
                    status INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(file_id, status)
                );
                CREATE INDEX IF NOT EXISTS idx_pending_conflicts_path ON pending_conflicts(local_path, status);

                CREATE TABLE IF NOT EXISTS propagator_jobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    operation_id TEXT NOT NULL UNIQUE,
                    job_type INTEGER NOT NULL,
                    file_id TEXT,
                    local_path TEXT NOT NULL,
                    remote_path TEXT NOT NULL,
                    payload_json TEXT NOT NULL DEFAULT '{}',
                    status INTEGER NOT NULL DEFAULT 0,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    lease_until_utc TEXT,
                    last_error TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_propagator_jobs_status ON propagator_jobs(status, lease_until_utc, id);
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

    public SyncJournalRecord? GetJournalByFileId(string fileId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_journal WHERE file_id = @fileId";
            cmd.Parameters.AddWithValue("@fileId", fileId);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJournalRecord(reader) : null;
        }
    }

    public SyncJournalRecord? GetJournalByLocalPath(string localPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_journal WHERE local_path = @path";
            cmd.Parameters.AddWithValue("@path", localPath);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJournalRecord(reader) : null;
        }
    }

    public SyncJournalRecord? GetJournalByRemotePath(string remotePath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_journal WHERE remote_path = @path";
            cmd.Parameters.AddWithValue("@path", remotePath);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadJournalRecord(reader) : null;
        }
    }

    public List<SyncJournalRecord> GetJournalChildren(string localDirectoryPath)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM sync_journal
                WHERE local_path LIKE @pattern
                AND local_path NOT LIKE @subPattern
                ORDER BY local_path
                """;
            var prefix = localDirectoryPath.TrimEnd('\\') + "\\";
            cmd.Parameters.AddWithValue("@pattern", prefix + "%");
            cmd.Parameters.AddWithValue("@subPattern", prefix + "%\\%");

            var records = new List<SyncJournalRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                records.Add(ReadJournalRecord(reader));
            return records;
        }
    }

    public List<SyncJournalRecord> GetAllJournalRecords()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM sync_journal ORDER BY local_path";

            var records = new List<SyncJournalRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                records.Add(ReadJournalRecord(reader));
            return records;
        }
    }

    public void UpsertJournalRecord(SyncJournalRecord record)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sync_journal (
                    file_id,
                    local_path,
                    remote_path,
                    etag,
                    mtime_utc,
                    size,
                    checksum,
                    pin_state,
                    is_directory,
                    in_sync,
                    base_etag,
                    local_pending_op,
                    updated_at
                )
                VALUES (
                    @fileId,
                    @localPath,
                    @remotePath,
                    @etag,
                    @mtime,
                    @size,
                    @checksum,
                    @pinState,
                    @isDirectory,
                    @inSync,
                    @baseEtag,
                    @localPendingOp,
                    datetime('now')
                )
                ON CONFLICT(file_id) DO UPDATE SET
                    local_path = @localPath,
                    remote_path = @remotePath,
                    etag = @etag,
                    mtime_utc = @mtime,
                    size = @size,
                    checksum = @checksum,
                    pin_state = @pinState,
                    is_directory = @isDirectory,
                    in_sync = @inSync,
                    base_etag = @baseEtag,
                    local_pending_op = @localPendingOp,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@fileId", record.FileId);
            cmd.Parameters.AddWithValue("@localPath", record.LocalPath);
            cmd.Parameters.AddWithValue("@remotePath", record.RemotePath);
            cmd.Parameters.AddWithValue("@etag", (object?)record.ETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@mtime", record.MTimeUtc?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@size", record.Size);
            cmd.Parameters.AddWithValue("@checksum", (object?)record.Checksum ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@pinState", (object?)record.PinState ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isDirectory", record.IsDirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("@inSync", record.InSync ? 1 : 0);
            cmd.Parameters.AddWithValue("@baseEtag", (object?)record.BaseETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@localPendingOp", (object?)record.LocalPendingOp ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteJournalRecord(string fileId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_journal WHERE file_id = @fileId";
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.ExecuteNonQuery();
        }
    }

    public PendingConflictRecord UpsertPendingConflict(PendingConflictRecord record)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pending_conflicts (
                    file_id,
                    local_path,
                    remote_path,
                    base_etag,
                    local_checksum,
                    remote_etag,
                    local_mtime_utc,
                    remote_mtime_utc,
                    local_size,
                    remote_size,
                    remote_temp_path,
                    status,
                    updated_at
                )
                VALUES (
                    @fileId,
                    @localPath,
                    @remotePath,
                    @baseEtag,
                    @localChecksum,
                    @remoteEtag,
                    @localMtime,
                    @remoteMtime,
                    @localSize,
                    @remoteSize,
                    @remoteTempPath,
                    @status,
                    datetime('now')
                )
                ON CONFLICT(file_id, status) DO UPDATE SET
                    local_path = @localPath,
                    remote_path = @remotePath,
                    base_etag = @baseEtag,
                    local_checksum = @localChecksum,
                    remote_etag = @remoteEtag,
                    local_mtime_utc = @localMtime,
                    remote_mtime_utc = @remoteMtime,
                    local_size = @localSize,
                    remote_size = @remoteSize,
                    remote_temp_path = @remoteTempPath,
                    updated_at = datetime('now')
                RETURNING *
                """;
            cmd.Parameters.AddWithValue("@fileId", record.FileId);
            cmd.Parameters.AddWithValue("@localPath", record.LocalPath);
            cmd.Parameters.AddWithValue("@remotePath", record.RemotePath);
            cmd.Parameters.AddWithValue("@baseEtag", (object?)record.BaseETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@localChecksum", (object?)record.LocalChecksum ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remoteEtag", (object?)record.RemoteETag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@localMtime", record.LocalMTimeUtc?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@remoteMtime", record.RemoteMTimeUtc?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@localSize", record.LocalSize);
            cmd.Parameters.AddWithValue("@remoteSize", record.RemoteSize);
            cmd.Parameters.AddWithValue("@remoteTempPath", (object?)record.RemoteTempPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)record.Status);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPendingConflict(reader) : record;
        }
    }

    public PendingConflictRecord? GetPendingConflict(string fileId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM pending_conflicts
                WHERE file_id = @fileId AND status = @status
                ORDER BY updated_at DESC, id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.Parameters.AddWithValue("@status", (int)PendingConflictStatus.Pending);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPendingConflict(reader) : null;
        }
    }

    public PropagatorJobRecord EnqueuePropagatorJob(PropagatorJobRecord job)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO propagator_jobs (
                    operation_id,
                    job_type,
                    file_id,
                    local_path,
                    remote_path,
                    payload_json,
                    status,
                    attempt_count,
                    lease_until_utc,
                    last_error,
                    updated_at
                )
                VALUES (
                    @operationId,
                    @jobType,
                    @fileId,
                    @localPath,
                    @remotePath,
                    @payloadJson,
                    @status,
                    @attemptCount,
                    @leaseUntil,
                    @lastError,
                    datetime('now')
                )
                ON CONFLICT(operation_id) DO UPDATE SET
                    job_type = @jobType,
                    file_id = @fileId,
                    local_path = @localPath,
                    remote_path = @remotePath,
                    payload_json = @payloadJson,
                    status = @status,
                    attempt_count = @attemptCount,
                    lease_until_utc = @leaseUntil,
                    last_error = @lastError,
                    updated_at = datetime('now')
                RETURNING *
                """;
            cmd.Parameters.AddWithValue("@operationId", job.OperationId);
            cmd.Parameters.AddWithValue("@jobType", (int)job.JobType);
            cmd.Parameters.AddWithValue("@fileId", (object?)job.FileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@localPath", job.LocalPath);
            cmd.Parameters.AddWithValue("@remotePath", job.RemotePath);
            cmd.Parameters.AddWithValue("@payloadJson", job.PayloadJson);
            cmd.Parameters.AddWithValue("@status", (int)PropagatorJobStatus.Pending);
            cmd.Parameters.AddWithValue("@attemptCount", job.AttemptCount);
            cmd.Parameters.AddWithValue("@leaseUntil", job.LeaseUntilUtc?.ToString("o") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lastError", (object?)job.LastError ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadPropagatorJob(reader) : job;
        }
    }

    public List<PropagatorJobRecord> LeasePropagatorJobs(int limit, TimeSpan leaseDuration)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var now = DateTime.UtcNow;
            var leaseUntil = now.Add(leaseDuration);
            var ids = new List<long>();

            using (var select = _connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = """
                    SELECT id FROM propagator_jobs
                    WHERE status IN (@pendingStatus, @leasedStatus, @failedStatus)
                      AND (lease_until_utc IS NULL OR lease_until_utc < @now)
                    ORDER BY
                        CASE job_type
                            WHEN 0 THEN 0 -- UploadNew
                            WHEN 1 THEN 0 -- UploadChanged
                            WHEN 4 THEN 1 -- MoveRemote
                            WHEN 6 THEN 1 -- DeleteRemote
                            WHEN 8 THEN 2 -- Conflict
                            ELSE 3
                        END,
                        created_at,
                        id
                    LIMIT @limit
                    """;
                select.Parameters.AddWithValue("@pendingStatus", (int)PropagatorJobStatus.Pending);
                select.Parameters.AddWithValue("@leasedStatus", (int)PropagatorJobStatus.Leased);
                select.Parameters.AddWithValue("@failedStatus", (int)PropagatorJobStatus.Failed);
                select.Parameters.AddWithValue("@now", now.ToString("o"));
                select.Parameters.AddWithValue("@limit", limit);

                using var reader = select.ExecuteReader();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
            }

            if (ids.Count == 0)
            {
                tx.Commit();
                return [];
            }

            using (var update = _connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = $"""
                    UPDATE propagator_jobs
                    SET status = @leasedStatus,
                        lease_until_utc = @leaseUntil,
                        attempt_count = attempt_count + 1,
                        updated_at = datetime('now')
                    WHERE id IN ({string.Join(",", ids)})
                    """;
                update.Parameters.AddWithValue("@leasedStatus", (int)PropagatorJobStatus.Leased);
                update.Parameters.AddWithValue("@leaseUntil", leaseUntil.ToString("o"));
                update.ExecuteNonQuery();
            }

            var jobs = new List<PropagatorJobRecord>();
            using (var fetch = _connection.CreateCommand())
            {
                fetch.Transaction = tx;
                fetch.CommandText = $"SELECT * FROM propagator_jobs WHERE id IN ({string.Join(",", ids)}) ORDER BY id";
                using var reader = fetch.ExecuteReader();
                while (reader.Read())
                    jobs.Add(ReadPropagatorJob(reader));
            }

            tx.Commit();
            return jobs;
        }
    }

    public void CompletePropagatorJob(string operationId)
    {
        UpdatePropagatorJobStatus(operationId, PropagatorJobStatus.Completed, null);
    }

    public void DeferPropagatorJob(string operationId, TimeSpan delay, string error)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE propagator_jobs
                SET status = @status,
                    lease_until_utc = @leaseUntil,
                    last_error = @lastError,
                    updated_at = datetime('now')
                WHERE operation_id = @operationId
                """;
            cmd.Parameters.AddWithValue("@status", (int)PropagatorJobStatus.Failed);
            cmd.Parameters.AddWithValue("@leaseUntil", DateTime.UtcNow.Add(delay).ToString("o"));
            cmd.Parameters.AddWithValue("@lastError", error);
            cmd.Parameters.AddWithValue("@operationId", operationId);
            cmd.ExecuteNonQuery();
        }
    }

    public void FailPropagatorJob(string operationId, string error)
    {
        UpdatePropagatorJobStatus(operationId, PropagatorJobStatus.Failed, error);
    }

    public List<PropagatorJobRecord> GetActivePropagatorJobs(int limit = 100)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT * FROM propagator_jobs
                WHERE status IN (@pendingStatus, @leasedStatus, @failedStatus)
                ORDER BY updated_at DESC, id DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pendingStatus", (int)PropagatorJobStatus.Pending);
            cmd.Parameters.AddWithValue("@leasedStatus", (int)PropagatorJobStatus.Leased);
            cmd.Parameters.AddWithValue("@failedStatus", (int)PropagatorJobStatus.Failed);
            cmd.Parameters.AddWithValue("@limit", limit);

            var jobs = new List<PropagatorJobRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                jobs.Add(ReadPropagatorJob(reader));
            return jobs;
        }
    }

    private void UpdatePropagatorJobStatus(string operationId, PropagatorJobStatus status, string? error)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE propagator_jobs
                SET status = @status,
                    lease_until_utc = NULL,
                    last_error = @lastError,
                    updated_at = datetime('now')
                WHERE operation_id = @operationId
                """;
            cmd.Parameters.AddWithValue("@status", (int)status);
            cmd.Parameters.AddWithValue("@lastError", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@operationId", operationId);
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

    private static SyncJournalRecord ReadJournalRecord(SqliteDataReader reader)
    {
        return new SyncJournalRecord
        {
            FileId = reader.GetString(reader.GetOrdinal("file_id")),
            LocalPath = reader.GetString(reader.GetOrdinal("local_path")),
            RemotePath = reader.GetString(reader.GetOrdinal("remote_path")),
            ETag = reader.IsDBNull(reader.GetOrdinal("etag")) ? null : reader.GetString(reader.GetOrdinal("etag")),
            MTimeUtc = reader.IsDBNull(reader.GetOrdinal("mtime_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("mtime_utc"))),
            Size = reader.GetInt64(reader.GetOrdinal("size")),
            Checksum = reader.IsDBNull(reader.GetOrdinal("checksum")) ? null : reader.GetString(reader.GetOrdinal("checksum")),
            PinState = reader.IsDBNull(reader.GetOrdinal("pin_state")) ? null : reader.GetString(reader.GetOrdinal("pin_state")),
            IsDirectory = reader.GetInt32(reader.GetOrdinal("is_directory")) == 1,
            InSync = reader.GetInt32(reader.GetOrdinal("in_sync")) == 1,
            BaseETag = reader.IsDBNull(reader.GetOrdinal("base_etag")) ? null : reader.GetString(reader.GetOrdinal("base_etag")),
            LocalPendingOp = reader.IsDBNull(reader.GetOrdinal("local_pending_op")) ? null : reader.GetString(reader.GetOrdinal("local_pending_op")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private static PendingConflictRecord ReadPendingConflict(SqliteDataReader reader)
    {
        return new PendingConflictRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            FileId = reader.GetString(reader.GetOrdinal("file_id")),
            LocalPath = reader.GetString(reader.GetOrdinal("local_path")),
            RemotePath = reader.GetString(reader.GetOrdinal("remote_path")),
            BaseETag = reader.IsDBNull(reader.GetOrdinal("base_etag")) ? null : reader.GetString(reader.GetOrdinal("base_etag")),
            LocalChecksum = reader.IsDBNull(reader.GetOrdinal("local_checksum")) ? null : reader.GetString(reader.GetOrdinal("local_checksum")),
            RemoteETag = reader.IsDBNull(reader.GetOrdinal("remote_etag")) ? null : reader.GetString(reader.GetOrdinal("remote_etag")),
            LocalMTimeUtc = reader.IsDBNull(reader.GetOrdinal("local_mtime_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("local_mtime_utc"))),
            RemoteMTimeUtc = reader.IsDBNull(reader.GetOrdinal("remote_mtime_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("remote_mtime_utc"))),
            LocalSize = reader.GetInt64(reader.GetOrdinal("local_size")),
            RemoteSize = reader.GetInt64(reader.GetOrdinal("remote_size")),
            RemoteTempPath = reader.IsDBNull(reader.GetOrdinal("remote_temp_path")) ? null : reader.GetString(reader.GetOrdinal("remote_temp_path")),
            Status = (PendingConflictStatus)reader.GetInt32(reader.GetOrdinal("status")),
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    private static PropagatorJobRecord ReadPropagatorJob(SqliteDataReader reader)
    {
        return new PropagatorJobRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            OperationId = reader.GetString(reader.GetOrdinal("operation_id")),
            JobType = (PropagatorJobType)reader.GetInt32(reader.GetOrdinal("job_type")),
            FileId = reader.IsDBNull(reader.GetOrdinal("file_id")) ? null : reader.GetString(reader.GetOrdinal("file_id")),
            LocalPath = reader.GetString(reader.GetOrdinal("local_path")),
            RemotePath = reader.GetString(reader.GetOrdinal("remote_path")),
            PayloadJson = reader.GetString(reader.GetOrdinal("payload_json")),
            Status = (PropagatorJobStatus)reader.GetInt32(reader.GetOrdinal("status")),
            AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
            LeaseUntilUtc = reader.IsDBNull(reader.GetOrdinal("lease_until_utc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("lease_until_utc"))),
            LastError = reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error")),
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
        GetCount(SyncStatus.Syncing) +
        GetCount(SyncStatus.RemoteDeletePendingUserChoice);
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
