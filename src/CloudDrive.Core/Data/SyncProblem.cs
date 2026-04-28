namespace CloudDrive.Core.Data;

public class SyncProblem
{
    public long Id { get; set; }
    public string? DedupeKey { get; set; }
    public SyncProblemType ProblemType { get; set; }
    public SyncProblemSeverity Severity { get; set; }
    public SyncProblemStatus Status { get; set; } = SyncProblemStatus.Open;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? LocalPath { get; set; }
    public string? RemotePath { get; set; }
    public string? ConflictCopyPath { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public enum SyncProblemType
{
    Conflict = 0,
    Connection = 1,
    RemoteListing = 2,
    RemoteSync = 3,
    Upload = 4,
    Download = 5,
    LocalAccess = 6,
    DiskFull = 7,
    PermissionDenied = 8,
    RemoteLock = 9
}

public enum SyncProblemSeverity
{
    Warning = 0,
    Error = 1
}

public enum SyncProblemStatus
{
    Open = 0,
    Resolved = 1
}
