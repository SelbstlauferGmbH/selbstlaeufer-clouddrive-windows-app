using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Localization;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class ConflictResolver
{
    private readonly IWebDavService _webDav;
    private readonly ISyncItemStateService _stateService;
    private readonly ISyncProjectionService _projectionService;
    private readonly ISyncProblemService _problemService;
    private readonly ILogger<ConflictResolver> _logger;
    private readonly SyncJournal? _journal;

    public ConflictResolver(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        ISyncProjectionService projectionService,
        ISyncProblemService problemService,
        ILogger<ConflictResolver> logger,
        SyncJournal? journal = null)
    {
        _webDav = webDav;
        _stateService = stateService;
        _projectionService = projectionService;
        _problemService = problemService;
        _logger = logger;
        _journal = journal;
    }

    public async Task<bool> TryCaptureUploadConflictAsync(
        SyncItem? existingItem,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        if (TransientFilePolicy.ShouldIgnoreLocalPath(localPath) ||
            TransientFilePolicy.ShouldIgnoreRemotePath(remotePath))
        {
            return false;
        }

        if (!File.Exists(localPath))
            return false;

        var remoteItem = await _webDav.GetPropertiesAsync(remotePath, ct);
        if (remoteItem == null || remoteItem.IsDirectory)
            return false;

        if (existingItem != null && !HasRemoteVersionChanged(existingItem, remoteItem))
            return false;

        var conflictCopyPath = ConflictCopyNamer.CreateUniqueLocalPath(localPath);
        var remoteConflictPath = await ConflictCopyNamer.CreateUniqueRemotePathAsync(
            remotePath,
            async (candidate, token) => await _webDav.GetPropertiesAsync(candidate, token) != null,
            ct);
        var tempDownloadPath = Path.Combine(
            Path.GetDirectoryName(localPath)!,
            $".clouddrive-remote-{Guid.NewGuid():N}.tmp");

        _logger.LogWarning(
            "Conflict detected for {LocalPath}. Preserving local copy at {ConflictCopyPath}",
            localPath,
            conflictCopyPath);

        _projectionService.SuppressWatcherEvents(localPath);
        _projectionService.SuppressWatcherEvents(conflictCopyPath);

        File.Copy(localPath, conflictCopyPath, overwrite: false);
        await using (var localCopyStream = new FileStream(
                         conflictCopyPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await _webDav.UploadFileAsync(remoteConflictPath, localCopyStream, ct);
        }

        try
        {
            await using (var remoteStream = await _webDav.DownloadFileAsync(remotePath, ct))
            await using (var tempStream = new FileStream(tempDownloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await remoteStream.CopyToAsync(tempStream, ct);
            }

            File.Copy(tempDownloadPath, localPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempDownloadPath))
                File.Delete(tempDownloadPath);
        }

        var restoredFileInfo = new FileInfo(localPath);
        var trackedItem = existingItem ?? new SyncItem();
        trackedItem.LocalPath = localPath;
        trackedItem.RemotePath = remotePath;
        trackedItem.IsDirectory = false;
        trackedItem.FileSize = restoredFileInfo.Length;
        trackedItem.RemoteETag = remoteItem.ETag;
        trackedItem.RemoteLastModified = remoteItem.LastModified == DateTime.MinValue
            ? null
            : remoteItem.LastModified;
        trackedItem.LocalHash = await FileHasher.ComputeSha256Async(localPath, ct);
        trackedItem.SyncStatus = SyncStatus.Synced;
        trackedItem.LastSynced = DateTime.UtcNow;
        _stateService.Upsert(trackedItem);
        UpsertJournalConflict(existingItem, localPath, remotePath, remoteConflictPath, remoteItem, restoredFileInfo.Length, trackedItem.LocalHash);
        _projectionService.ScheduleMarkInSync(localPath);

        var localizer = AppLocalizer.Instance;
        var conflictCopyName = Path.GetFileName(conflictCopyPath);
        _problemService.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Conflict(localPath),
            ProblemType = SyncProblemType.Conflict,
            Severity = SyncProblemSeverity.Warning,
            Title = localizer.Format("Problem_Conflict_Title", Path.GetFileName(localPath)),
            Summary = localizer.Format("Problem_Conflict_Summary_WithCopy", conflictCopyName),
            Details = localizer.GetString("Problem_Conflict_Detail"),
            LocalPath = localPath,
            RemotePath = remotePath,
            ConflictCopyPath = conflictCopyPath,
            FirstOccurredAt = DateTime.UtcNow,
            LastOccurredAt = DateTime.UtcNow
        });

        _logger.LogInformation(
            "Conflict captured for {LocalPath}. Remote version restored and local edits preserved at {ConflictCopyPath}",
            localPath,
            conflictCopyPath);

        return true;
    }

    private void UpsertJournalConflict(
        SyncItem? existingItem,
        string localPath,
        string remotePath,
        string remoteConflictPath,
        RemoteItem remoteItem,
        long restoredSize,
        string? checksum)
    {
        if (_journal == null)
            return;

        var existing = _journal.GetByLocalPath(localPath) ?? _journal.GetByRemotePath(remotePath);
        var fileId = existing?.FileId ?? SyncIdentity.RemotePathFallbackId(remotePath);

        _journal.Upsert(new SyncJournalRecord
        {
            FileId = fileId,
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = false,
            Size = restoredSize,
            ETag = remoteItem.ETag,
            BaseETag = remoteItem.ETag,
            Checksum = checksum,
            MTimeUtc = remoteItem.LastModified == DateTime.MinValue ? DateTime.UtcNow : remoteItem.LastModified.ToUniversalTime(),
            InSync = true
        });

        _journal.UpsertConflict(new PendingConflictRecord
        {
            FileId = fileId,
            LocalPath = localPath,
            RemotePath = remotePath,
            BaseETag = existingItem?.RemoteETag ?? existing?.ETag,
            RemoteETag = remoteItem.ETag,
            RemoteMTimeUtc = remoteItem.LastModified == DateTime.MinValue ? null : remoteItem.LastModified.ToUniversalTime(),
            RemoteSize = remoteItem.Size,
            LocalSize = restoredSize,
            RemoteTempPath = remoteConflictPath,
            Status = PendingConflictStatus.Pending
        });
    }

    private static bool HasRemoteVersionChanged(SyncItem existingItem, RemoteItem remoteItem)
    {
        if (!string.IsNullOrWhiteSpace(existingItem.RemoteETag) &&
            !string.IsNullOrWhiteSpace(remoteItem.ETag))
        {
            return !WebDavETag.Equals(existingItem.RemoteETag, remoteItem.ETag);
        }

        if (existingItem.RemoteLastModified.HasValue &&
            remoteItem.LastModified != DateTime.MinValue)
        {
            var previous = existingItem.RemoteLastModified.Value.ToUniversalTime();
            var current = remoteItem.LastModified.ToUniversalTime();
            if (current > previous.AddSeconds(1))
                return true;
        }

        if (existingItem.FileSize > 0 && remoteItem.Size > 0 && existingItem.FileSize != remoteItem.Size)
            return true;

        return false;
    }

}
