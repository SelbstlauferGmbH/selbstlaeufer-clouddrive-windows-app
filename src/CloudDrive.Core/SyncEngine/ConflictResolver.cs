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

    public ConflictResolver(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        ISyncProjectionService projectionService,
        ISyncProblemService problemService,
        ILogger<ConflictResolver> logger)
    {
        _webDav = webDav;
        _stateService = stateService;
        _projectionService = projectionService;
        _problemService = problemService;
        _logger = logger;
    }

    public async Task<bool> TryCaptureUploadConflictAsync(
        SyncItem? existingItem,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        if (!File.Exists(localPath))
            return false;

        var remoteItem = await _webDav.GetPropertiesAsync(remotePath, ct);
        if (remoteItem == null || remoteItem.IsDirectory)
            return false;

        if (existingItem != null && !HasRemoteVersionChanged(existingItem, remoteItem))
            return false;

        var conflictCopyPath = GenerateUniqueConflictPath(localPath);
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

    private static bool HasRemoteVersionChanged(SyncItem existingItem, RemoteItem remoteItem)
    {
        if (!string.IsNullOrWhiteSpace(existingItem.RemoteETag) &&
            !string.IsNullOrWhiteSpace(remoteItem.ETag))
        {
            return !string.Equals(existingItem.RemoteETag, remoteItem.ETag, StringComparison.Ordinal);
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

    private static string GenerateUniqueConflictPath(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath)!;
        var baseName = Path.GetFileNameWithoutExtension(localPath);
        var extension = Path.GetExtension(localPath);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var machineName = Environment.MachineName;

        var candidate = Path.Combine(directory, $"{baseName} (conflict {timestamp} {machineName}){extension}");
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var suffix = 2;
        while (true)
        {
            var numbered = Path.Combine(directory, $"{baseName} (conflict {timestamp} {machineName} {suffix}){extension}");
            if (!File.Exists(numbered) && !Directory.Exists(numbered))
                return numbered;

            suffix++;
        }
    }
}
