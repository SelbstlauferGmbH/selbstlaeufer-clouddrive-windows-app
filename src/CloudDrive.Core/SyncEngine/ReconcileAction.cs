using CloudDrive.Core.Data;
using CloudDrive.Core.Vfs;
using CloudDrive.Core.WebDav;

namespace CloudDrive.Core.SyncEngine;

public enum ReconcileActionType
{
    NoOp = 0,
    UploadNew = 1,
    UploadChanged = 2,
    DownloadNew = 3,
    DownloadChanged = 4,
    MoveRemote = 5,
    MoveLocal = 6,
    DeleteRemote = 7,
    DeleteLocal = 8,
    Conflict = 9
}

public sealed record ReconcileAction(
    ReconcileActionType Type,
    string LocalPath,
    string RemotePath,
    string? FileId,
    PlaceholderInfo? Local,
    SyncJournalRecord? Journal,
    RemoteItem? Remote,
    string? PreviousLocalPath = null,
    string? PreviousRemotePath = null)
{
    public static ReconcileAction NoOp(
        string localPath,
        string remotePath,
        string? fileId,
        PlaceholderInfo? local,
        SyncJournalRecord? journal,
        RemoteItem? remote) =>
        new(ReconcileActionType.NoOp, localPath, remotePath, fileId, local, journal, remote);
}
