using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public class SyncItemStateService : ISyncItemStateService
{
    private readonly SyncStateDb _db;

    public SyncItemStateService(SyncStateDb db)
    {
        _db = db;
    }

    public SyncItem? GetByLocalPath(string localPath) => _db.GetByLocalPath(localPath);

    public SyncItem? GetByRemotePath(string remotePath) => _db.GetByRemotePath(remotePath);

    public IReadOnlyList<SyncItem> GetChildren(string localDirectoryPath) => _db.GetChildren(localDirectoryPath);

    public IReadOnlyList<SyncItem> GetAll() => _db.GetAll();

    public IReadOnlyList<SyncItem> GetByStatus(SyncStatus status) => _db.GetByStatus(status);

    public void Upsert(SyncItem item) => _db.Upsert(item);

    public void UpdateStatus(string localPath, SyncStatus status) => _db.UpdateStatus(localPath, status);

    public void Delete(string localPath) => _db.Delete(localPath);

    public void DeleteChildren(string localDirectoryPath) => _db.DeleteChildren(localDirectoryPath);

    public void MarkRemoteDeletionPending(SyncItem item)
    {
        item.SyncStatus = SyncStatus.RemoteDeletePendingLocalCleanup;
        item.LastSynced = DateTime.UtcNow;
        _db.Upsert(item);
    }

    public bool IsRemoteDeletionPending(string localPath)
    {
        return _db.GetByLocalPath(localPath)?.SyncStatus == SyncStatus.RemoteDeletePendingLocalCleanup;
    }

    public void CompleteRemoteDeletion(string localPath, bool isDirectory)
    {
        if (isDirectory)
            _db.DeleteChildren(localPath);

        _db.Delete(localPath);
    }
}
