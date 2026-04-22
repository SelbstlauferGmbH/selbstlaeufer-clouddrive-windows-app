namespace CloudDrive.Core.Data;

public enum SyncStatus
{
    CloudOnly = 0,
    Synced = 1,
    Syncing = 2,
    PendingUpload = 3,
    PendingDownload = 4,
    Error = 5,
    Conflict = 6,
    RemoteDeletePendingLocalCleanup = 7
}
