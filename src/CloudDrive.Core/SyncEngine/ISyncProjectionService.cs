using CloudDrive.Core.WebDav;

namespace CloudDrive.Core.SyncEngine;

public interface ISyncProjectionService
{
    void SetLocalChangeWatcher(LocalChangeWatcher watcher);
    void SuppressWatcherEvents(string path);
    bool UpdatePlaceholderMetadata(string localPath, long fileSize, DateTime lastModified, string? remotePath = null);
    void CreateRemotePlaceholders(string localDirectoryPath, IReadOnlyList<RemoteItem> items);
    void ScheduleRemoteDeletion(string localPath, bool isDirectory, string parentDirectoryPath);
    void ScheduleMarkInSync(string localPath);
    void RefreshDirectory(string localDirectoryPath);
}
