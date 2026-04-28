namespace CloudDrive.Core.Data;

public sealed class SyncJournalRecord
{
    public string FileId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public DateTime? MTimeUtc { get; set; }
    public long Size { get; set; }
    public string? Checksum { get; set; }
    public string? PinState { get; set; }
    public bool IsDirectory { get; set; }
    public bool InSync { get; set; } = true;
    public string? BaseETag { get; set; }
    public string? LocalPendingOp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum PendingConflictStatus
{
    Pending = 0,
    Resolving = 1,
    Resolved = 2,
    Dismissed = 3
}

public sealed class PendingConflictRecord
{
    public long Id { get; set; }
    public string FileId { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string? BaseETag { get; set; }
    public string? LocalChecksum { get; set; }
    public string? RemoteETag { get; set; }
    public DateTime? LocalMTimeUtc { get; set; }
    public DateTime? RemoteMTimeUtc { get; set; }
    public long LocalSize { get; set; }
    public long RemoteSize { get; set; }
    public string? RemoteTempPath { get; set; }
    public PendingConflictStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum PropagatorJobType
{
    UploadNew = 0,
    UploadChanged = 1,
    DownloadNew = 2,
    DownloadChanged = 3,
    MoveRemote = 4,
    MoveLocal = 5,
    DeleteRemote = 6,
    DeleteLocal = 7,
    Conflict = 8
}

public enum PropagatorJobStatus
{
    Pending = 0,
    Leased = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public sealed class PropagatorJobRecord
{
    public long Id { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public PropagatorJobType JobType { get; set; }
    public string? FileId { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public PropagatorJobStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
