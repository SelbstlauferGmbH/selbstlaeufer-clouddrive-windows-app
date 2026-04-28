using System.Diagnostics;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class RemoteChangeDetector
{
    private static readonly TimeSpan SlowDirectoryScanThreshold = TimeSpan.FromSeconds(2);

    private readonly IWebDavService _webDav;
    private readonly ISyncItemStateService _stateService;
    private readonly PathMapper _pathMapper;
    private readonly ISyncProjectionService _projectionService;
    private readonly ILogger<RemoteChangeDetector> _logger;
    private readonly ActiveCloudRequestTracker? _requestTracker;
    private readonly bool _verboseDebugLogging;
    private readonly SyncJournal? _journal;

    public RemoteChangeDetector(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        PathMapper pathMapper,
        ISyncProjectionService projectionService,
        ILogger<RemoteChangeDetector> logger,
        ActiveCloudRequestTracker? requestTracker = null,
        bool verboseDebugLogging = false,
        SyncJournal? journal = null)
    {
        _webDav = webDav;
        _stateService = stateService;
        _pathMapper = pathMapper;
        _projectionService = projectionService;
        _logger = logger;
        _requestTracker = requestTracker;
        _verboseDebugLogging = verboseDebugLogging;
        _journal = journal;
    }

    public async Task ScanAsync(CancellationToken ct)
    {
        _logger.LogDebug("Starting remote change scan");

        try
        {
            await ScanDirectoryAsync("/", ct);
            _logger.LogDebug("Remote change scan completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Remote change scan cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote change scan failed");
        }
    }

    private async Task ScanDirectoryAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var directoryStopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Remote scan visiting directory: {RemotePath}", remotePath);

        var remoteItems = (await _webDav.ListDirectoryAsync(remotePath, ct))
            .Where(item => !TransientFilePolicy.ShouldIgnoreRemotePath(item.RemotePath, item.IsDirectory))
            .ToList();
        var localDirPath = _pathMapper.ToLocalPath(remotePath);
        var localChildren = _stateService.GetChildren(localDirPath)
            .Where(item =>
                !TransientFilePolicy.ShouldIgnoreLocalPath(item.LocalPath, item.IsDirectory) &&
                !TransientFilePolicy.ShouldIgnoreRemotePath(item.RemotePath, item.IsDirectory))
            .ToList();
        var shouldRefreshExplorer = false;

        var remoteSet = new HashSet<string>(remoteItems.Select(r => r.RemotePath), StringComparer.OrdinalIgnoreCase);
        var localDict = localChildren.ToDictionary(l => l.RemotePath, StringComparer.OrdinalIgnoreCase);

        // Find new or updated remote items
        foreach (var remoteItem in remoteItems)
        {
            ct.ThrowIfCancellationRequested();

            if (localDict.TryGetValue(remoteItem.RemotePath, out var localItem))
            {
                var remotePathChanged = !string.Equals(localItem.RemotePath, remoteItem.RemotePath, StringComparison.Ordinal);
                if (remotePathChanged)
                {
                    _logger.LogInformation(
                        "Remote path canonicalized: {LocalPath} OldRemotePath={OldRemotePath} NewRemotePath={NewRemotePath}",
                        localItem.LocalPath,
                        localItem.RemotePath,
                        remoteItem.RemotePath);
                    localItem.RemotePath = remoteItem.RemotePath;
                }

                // Check if remote changed (ETag differs)
                if (!string.IsNullOrEmpty(remoteItem.ETag)
                    && !string.IsNullOrEmpty(localItem.RemoteETag)
                    && !WebDavETag.Equals(remoteItem.ETag, localItem.RemoteETag))
                {
                    _logger.LogInformation("Remote file changed: {Path}", remoteItem.RemotePath);
                    localItem.RemoteETag = remoteItem.ETag;
                    localItem.RemoteLastModified = remoteItem.LastModified;
                    localItem.FileSize = remoteItem.Size;
                    localItem.SyncStatus = SyncStatus.CloudOnly;
                    UpsertJournal(localItem, remoteItem);

                    // Update placeholder metadata in Explorer so the user sees correct size/date
                    if (!localItem.IsDirectory)
                    {
                        _projectionService.UpdatePlaceholderMetadata(
                            localItem.LocalPath, remoteItem.Size, remoteItem.LastModified);
                    }

                    // Keep as CloudOnly since the placeholder metadata is updated;
                    // actual content will be fetched on-demand when the user opens the file
                    _stateService.Upsert(localItem);
                    shouldRefreshExplorer = true;
                }
                else if (localItem.SyncStatus == SyncStatus.RemoteDeletePendingLocalCleanup)
                {
                    localItem.RemoteETag = remoteItem.ETag;
                    localItem.RemoteLastModified = remoteItem.LastModified;
                    localItem.FileSize = remoteItem.Size;
                    localItem.SyncStatus = SyncStatus.CloudOnly;
                    _stateService.Upsert(localItem);
                    UpsertJournal(localItem, remoteItem);
                    if (!File.Exists(localItem.LocalPath) && !Directory.Exists(localItem.LocalPath))
                    {
                        _projectionService.CreateRemotePlaceholders(localDirPath, [remoteItem]);
                        shouldRefreshExplorer = true;
                    }
                }
                else if (remotePathChanged)
                {
                    _stateService.Upsert(localItem);
                    UpsertJournal(localItem, remoteItem);
                }
            }
            else
            {
                // New remote item — create placeholder
                _logger.LogInformation("New remote item: {Path}", remoteItem.RemotePath);

                if (Directory.Exists(localDirPath))
                {
                    _projectionService.CreateRemotePlaceholders(localDirPath, [remoteItem]);
                    UpsertJournalForRemote(remoteItem);
                    shouldRefreshExplorer = true;
                }
            }

            // Recurse into subdirectories
            if (remoteItem.IsDirectory)
            {
                var subDirLocal = _pathMapper.ToLocalPath(remoteItem.RemotePath);
                if (Directory.Exists(subDirLocal))
                {
                    await ScanDirectoryAsync(remoteItem.RemotePath, ct);
                }
            }
        }

        // Find deleted remote items
        foreach (var localItem in localChildren)
        {
            if (localItem.SyncStatus == SyncStatus.RemoteDeletePendingLocalCleanup)
                continue;

            if (!remoteSet.Contains(localItem.RemotePath))
            {
                var revalidatedItem = await _webDav.GetPropertiesAsync(localItem.RemotePath, ct);
                if (revalidatedItem != null)
                {
                    _logger.LogDebug(
                        "Remote item missing from directory listing but found on recheck: {Path}",
                        localItem.RemotePath);

                    localItem.IsDirectory = revalidatedItem.IsDirectory;
                    localItem.FileSize = revalidatedItem.Size;
                    localItem.RemoteETag = revalidatedItem.ETag;
                    localItem.RemoteLastModified = revalidatedItem.LastModified;
                    _stateService.Upsert(localItem);
                    UpsertJournal(localItem, revalidatedItem);
                    continue;
                }

                LogActiveRequestsForPath(localItem.LocalPath, localItem.RemotePath);
                _logger.LogInformation("Remote item deleted: {Path}", localItem.RemotePath);
                _stateService.MarkRemoteDeletionPending(localItem);
                MarkJournalRemoteDeletionPending(localItem);

                _projectionService.ScheduleRemoteDeletion(localItem.LocalPath, localItem.IsDirectory, localDirPath);
                shouldRefreshExplorer = true;
            }
        }

        if (shouldRefreshExplorer)
        {
            _projectionService.RefreshDirectory(localDirPath);
        }

        var logLevel = directoryStopwatch.Elapsed >= SlowDirectoryScanThreshold ? LogLevel.Information : LogLevel.Debug;
        _logger.Log(
            logLevel,
            "Remote scan directory completed: {RemotePath} RemoteItems={RemoteItemCount} LocalItems={LocalItemCount} RefreshRequested={RefreshRequested} DurationMs={DurationMs}",
            remotePath,
            remoteItems.Count,
            localChildren.Count,
            shouldRefreshExplorer,
            directoryStopwatch.ElapsedMilliseconds);
    }

    private void UpsertJournal(SyncItem localItem, RemoteItem remoteItem)
    {
        if (_journal == null)
            return;

        var existing = _journal.GetByLocalPath(localItem.LocalPath)
            ?? _journal.GetByRemotePath(remoteItem.RemotePath)
            ?? _journal.GetByRemotePath(localItem.RemotePath);

        _journal.Upsert(new SyncJournalRecord
        {
            FileId = existing?.FileId ?? SyncIdentity.RemotePathFallbackId(remoteItem.RemotePath),
            LocalPath = localItem.LocalPath,
            RemotePath = remoteItem.RemotePath,
            IsDirectory = remoteItem.IsDirectory,
            Size = remoteItem.Size,
            ETag = remoteItem.ETag,
            BaseETag = remoteItem.ETag,
            MTimeUtc = remoteItem.LastModified == DateTime.MinValue ? DateTime.UtcNow : remoteItem.LastModified.ToUniversalTime(),
            InSync = localItem.SyncStatus == SyncStatus.Synced || localItem.SyncStatus == SyncStatus.CloudOnly
        });
    }

    private void UpsertJournalForRemote(RemoteItem remoteItem)
    {
        if (_journal == null)
            return;

        var localPath = _pathMapper.ToLocalPath(remoteItem.RemotePath);
        var existing = _journal.GetByRemotePath(remoteItem.RemotePath) ?? _journal.GetByLocalPath(localPath);
        _journal.Upsert(new SyncJournalRecord
        {
            FileId = existing?.FileId ?? SyncIdentity.RemotePathFallbackId(remoteItem.RemotePath),
            LocalPath = localPath,
            RemotePath = remoteItem.RemotePath,
            IsDirectory = remoteItem.IsDirectory,
            Size = remoteItem.Size,
            ETag = remoteItem.ETag,
            BaseETag = remoteItem.ETag,
            MTimeUtc = remoteItem.LastModified == DateTime.MinValue ? DateTime.UtcNow : remoteItem.LastModified.ToUniversalTime(),
            InSync = true
        });
    }

    private void MarkJournalRemoteDeletionPending(SyncItem item)
    {
        if (_journal == null)
            return;

        var record = _journal.GetByLocalPath(item.LocalPath) ?? _journal.GetByRemotePath(item.RemotePath);
        if (record != null)
        {
            record.LocalPendingOp = SyncPendingOperations.DeleteLocal;
            record.InSync = false;
            _journal.Upsert(record);
        }
    }

    private void LogActiveRequestsForPath(string localPath, string remotePath)
    {
        if (!_verboseDebugLogging || _requestTracker == null)
            return;

        var normalizedPath = _pathMapper.RemotePathToNormalizedPath(remotePath);
        var requests = _requestTracker.GetSnapshot(normalizedPath);

        if (requests.Count == 0)
        {
            _logger.LogInformation("DEBUG active cloud requests for {Path}: none", localPath);
            return;
        }

        foreach (var request in requests)
        {
            _logger.LogInformation(
                "DEBUG active cloud request for {Path}: Kind={Kind} AgeMs={AgeMs} Offset={Offset} Length={Length}",
                localPath,
                request.Kind,
                (long)request.Age.TotalMilliseconds,
                request.RequiredOffset,
                request.RequiredLength);
        }
    }
}
