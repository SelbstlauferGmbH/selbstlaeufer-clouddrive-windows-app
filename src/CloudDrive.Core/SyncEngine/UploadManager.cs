using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Localization;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class UploadManager
{
    private readonly IWebDavService _webDav;
    private readonly ISyncItemStateService _stateService;
    private readonly PathMapper _pathMapper;
    private readonly ISyncProjectionService _projectionService;
    private readonly ConflictResolver? _conflictResolver;
    private readonly ISyncProblemService? _problemService;
    private readonly SyncJournal? _journal;
    private readonly ILogger<UploadManager> _logger;
    private readonly SemaphoreSlim _semaphore;

    public UploadManager(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        PathMapper pathMapper,
        ISyncProjectionService projectionService,
        int maxConcurrentTransfers,
        ILogger<UploadManager> logger,
        ConflictResolver? conflictResolver = null,
        ISyncProblemService? problemService = null,
        SyncJournal? journal = null)
    {
        _webDav = webDav;
        _stateService = stateService;
        _pathMapper = pathMapper;
        _projectionService = projectionService;
        _conflictResolver = conflictResolver;
        _problemService = problemService;
        _journal = journal;
        _logger = logger;
        _semaphore = new SemaphoreSlim(maxConcurrentTransfers);
    }

    public async Task ProcessChangeAsync(FileChangeEvent change, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            switch (change.ChangeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Changed:
                    await HandleCreateOrChangeAsync(change.FullPath, ct);
                    break;

                case FileChangeType.Deleted:
                    await HandleDeleteAsync(change.FullPath, ct);
                    break;

                case FileChangeType.Renamed:
                    await HandleRenameAsync(change.FullPath, change.OldFullPath!, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process change: {Type} {Path}", change.ChangeType, change.FullPath);
            _stateService.UpdateStatus(change.FullPath, SyncStatus.Error);
            ReportUploadProblem(change.FullPath, change.ChangeType, ex);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task HandleCreateOrChangeAsync(string localPath, CancellationToken ct)
    {
        var remotePath = _pathMapper.ToRemotePath(localPath);

        if (Directory.Exists(localPath))
        {
            var existingDirectory = _stateService.GetByLocalPath(localPath);
            if (existingDirectory?.IsDirectory == true)
            {
                _logger.LogDebug("Skipping directory change for existing sync item: {Path}", localPath);
                return;
            }

            _logger.LogInformation("Creating remote directory: {Path}", remotePath);
            await _webDav.CreateDirectoryAsync(remotePath, ct);

            _stateService.Upsert(new SyncItem
            {
                LocalPath = localPath,
                RemotePath = remotePath,
                IsDirectory = true,
                SyncStatus = SyncStatus.Synced,
                LastSynced = DateTime.UtcNow
            });
            UpsertJournal(localPath, remotePath, isDirectory: true, size: 0, etag: null, checksum: null);
            _projectionService.ScheduleMarkInSync(localPath);
            return;
        }

        if (!File.Exists(localPath))
            return;

        var existingItem = _stateService.GetByLocalPath(localPath);
        if (_conflictResolver != null &&
            await _conflictResolver.TryCaptureUploadConflictAsync(existingItem, localPath, remotePath, ct))
        {
            _problemService?.ResolveByDedupeKey(SyncProblemKeys.Upload(localPath));
            return;
        }

        _logger.LogInformation("UPLOAD_FILE uploading: {Path}", remotePath);
        _stateService.UpdateStatus(localPath, SyncStatus.Syncing);

        // Ensure parent directories exist
        var parentRemote = remotePath[..remotePath.LastIndexOf('/')];
        if (!string.IsNullOrEmpty(parentRemote))
            await _webDav.CreateDirectoryAsync(parentRemote, ct);

        using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var etag = await _webDav.UploadFileAsync(remotePath, fileStream, ct);

        var fileInfo = new FileInfo(localPath);
        var hash = await FileHasher.ComputeSha256Async(localPath, ct);

        _stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = false,
            FileSize = fileInfo.Length,
            RemoteETag = etag,
            LocalHash = hash,
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });

        _projectionService.ScheduleMarkInSync(localPath);
        UpsertJournal(localPath, remotePath, isDirectory: false, size: fileInfo.Length, etag, hash, fileInfo.LastWriteTimeUtc);
        _problemService?.ResolveByDedupeKey(SyncProblemKeys.Upload(localPath));

        _logger.LogInformation("UPLOAD_FILE complete: {Path} Synced", remotePath);
    }

    private async Task HandleDeleteAsync(string localPath, CancellationToken ct)
    {
        var item = _stateService.GetByLocalPath(localPath);
        if (item == null) return;

        if (item.SyncStatus == SyncStatus.RemoteDeletePendingLocalCleanup)
        {
            _logger.LogInformation("Skipping remote delete for provider-owned cleanup: {Path}", localPath);
            _stateService.CompleteRemoteDeletion(localPath, item.IsDirectory);
            return;
        }

        _logger.LogInformation("DELETE remote: {Path}", item.RemotePath);
        await _webDav.DeleteAsync(item.RemotePath, ct);
        _problemService?.ResolveByDedupeKey(SyncProblemKeys.Upload(localPath));

        if (item.IsDirectory)
            _stateService.DeleteChildren(localPath);
        _stateService.Delete(localPath);
        DeleteJournalForItem(item);
    }

    private async Task HandleRenameAsync(string newPath, string oldPath, CancellationToken ct)
    {
        var newRemotePath = _pathMapper.ToRemotePath(newPath);
        var item = _stateService.GetByLocalPath(oldPath);
        if (item == null)
        {
            var oldRemotePath = _pathMapper.ToRemotePath(oldPath);
            _logger.LogWarning(
                "Rename state missing for {OldPath}; attempting stateless remote move {OldRemotePath} -> {NewRemotePath}",
                oldPath,
                oldRemotePath,
                newRemotePath);

            var remoteItem = await _webDav.GetPropertiesAsync(oldRemotePath, ct);
            if (remoteItem != null)
            {
                await _webDav.MoveAsync(oldRemotePath, newRemotePath, ct);

                _stateService.Upsert(new SyncItem
                {
                    LocalPath = newPath,
                    RemotePath = newRemotePath,
                    IsDirectory = remoteItem.IsDirectory,
                    FileSize = remoteItem.Size,
                    RemoteETag = remoteItem.ETag,
                    RemoteLastModified = remoteItem.LastModified,
                    SyncStatus = SyncStatus.Synced,
                    LastSynced = DateTime.UtcNow
                });
                UpsertJournal(
                    newPath,
                    newRemotePath,
                    remoteItem.IsDirectory,
                    remoteItem.Size,
                    remoteItem.ETag,
                    checksum: null,
                    mtimeUtc: remoteItem.LastModified);
                RefreshPlaceholderIdentity(newPath, newRemotePath, remoteItem.IsDirectory, remoteItem.Size, remoteItem.LastModified);
                _projectionService.ScheduleMarkInSync(newPath);
                return;
            }

            // Treat as new file if the old remote path is unknown too.
            await HandleCreateOrChangeAsync(newPath, ct);
            return;
        }

        _logger.LogInformation("Moving remote: {Old} -> {New}", item.RemotePath, newRemotePath);

        await _webDav.MoveAsync(item.RemotePath, newRemotePath, ct);

        _stateService.Delete(oldPath);
        item.LocalPath = newPath;
        item.RemotePath = newRemotePath;
        item.SyncStatus = SyncStatus.Synced;
        item.LastSynced = DateTime.UtcNow;
        _stateService.Upsert(item);
        RenameJournal(oldPath, newPath, item);
        RefreshPlaceholderIdentity(newPath, newRemotePath, item.IsDirectory, item.FileSize, item.RemoteLastModified);
        _projectionService.ScheduleMarkInSync(newPath);
        _problemService?.ResolveByDedupeKey(SyncProblemKeys.Upload(newPath));
    }

    private void UpsertJournal(
        string localPath,
        string remotePath,
        bool isDirectory,
        long size,
        string? etag,
        string? checksum,
        DateTime? mtimeUtc = null)
    {
        if (_journal == null)
            return;

        var existing = _journal.GetByLocalPath(localPath) ?? _journal.GetByRemotePath(remotePath);
        _journal.Upsert(new SyncJournalRecord
        {
            FileId = existing?.FileId ?? SyncIdentity.NewLocalId(),
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = isDirectory,
            Size = size,
            ETag = etag,
            BaseETag = etag,
            Checksum = checksum,
            MTimeUtc = mtimeUtc?.ToUniversalTime() ?? DateTime.UtcNow,
            InSync = true
        });
    }

    private void RenameJournal(string oldPath, string newPath, SyncItem item)
    {
        if (_journal == null)
            return;

        var existing = _journal.GetByLocalPath(oldPath)
            ?? _journal.GetByRemotePath(item.RemotePath)
            ?? _journal.GetByLocalPath(newPath);

        _journal.Upsert(new SyncJournalRecord
        {
            FileId = existing?.FileId ?? SyncIdentity.NewLocalId(),
            LocalPath = newPath,
            RemotePath = item.RemotePath,
            IsDirectory = item.IsDirectory,
            Size = item.FileSize,
            ETag = item.RemoteETag,
            BaseETag = item.RemoteETag,
            Checksum = item.LocalHash,
            MTimeUtc = item.RemoteLastModified?.ToUniversalTime() ?? DateTime.UtcNow,
            InSync = true
        });
    }

    private void DeleteJournalForItem(SyncItem item)
    {
        if (_journal == null)
            return;

        var record = _journal.GetByLocalPath(item.LocalPath) ?? _journal.GetByRemotePath(item.RemotePath);
        if (record != null)
            _journal.Delete(record.FileId);
    }

    private void RefreshPlaceholderIdentity(
        string localPath,
        string remotePath,
        bool isDirectory,
        long fileSize,
        DateTime? remoteLastModified)
    {
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
            return;

        var lastModified = remoteLastModified ?? (isDirectory
            ? Directory.GetLastWriteTimeUtc(localPath)
            : File.GetLastWriteTimeUtc(localPath));

        _projectionService.UpdatePlaceholderMetadata(
            localPath,
            isDirectory ? 0 : fileSize,
            lastModified,
            remotePath);
    }

    private void ReportUploadProblem(string localPath, FileChangeType changeType, Exception ex)
    {
        if (_problemService == null)
            return;

        var localizer = AppLocalizer.Instance;
        var fileName = Path.GetFileName(localPath);
        var summary = changeType switch
        {
            FileChangeType.Deleted => localizer.Format("Problem_Upload_DeleteSummary", fileName),
            FileChangeType.Renamed => localizer.Format("Problem_Upload_RenameSummary", fileName),
            _ => localizer.Format("Problem_Upload_DefaultSummary", fileName)
        };

        _problemService.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Upload(localPath),
            ProblemType = SyncProblemType.Upload,
            Severity = SyncProblemSeverity.Error,
            Title = localizer.Format("Problem_CouldNotSync_Title", fileName),
            Summary = localizer.Format("Problem_Upload_SummarySuffix", summary),
            Details = ex.Message,
            LocalPath = localPath,
            FirstOccurredAt = DateTime.UtcNow,
            LastOccurredAt = DateTime.UtcNow
        });
    }
}
