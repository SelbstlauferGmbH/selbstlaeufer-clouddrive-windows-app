using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public interface ISyncItemStateService
{
    SyncItem? GetByLocalPath(string localPath);
    SyncItem? GetByRemotePath(string remotePath);
    IReadOnlyList<SyncItem> GetChildren(string localDirectoryPath);
    IReadOnlyList<SyncItem> GetAll();
    IReadOnlyList<SyncItem> GetByStatus(SyncStatus status);
    void Upsert(SyncItem item);
    void UpdateStatus(string localPath, SyncStatus status);
    void Delete(string localPath);
    void DeleteChildren(string localDirectoryPath);
    void MarkRemoteDeletionPending(SyncItem item);
    bool IsRemoteDeletionPending(string localPath);
    void CompleteRemoteDeletion(string localPath, bool isDirectory);
}
