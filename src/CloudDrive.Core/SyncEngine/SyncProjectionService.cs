using System.Collections.Concurrent;
using System.Diagnostics;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class SyncProjectionService : ISyncProjectionService
{
    private static readonly TimeSpan WatcherSuppressionDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromSeconds(2);
    private const int DeleteRetryCount = 10;
    private static readonly TimeSpan InSyncRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int InSyncRetryCount = 10;

    private readonly PlaceholderManager _placeholderManager;
    private readonly ISyncItemStateService _stateService;
    private readonly ICloudFileOperations _cloudFileOperations;
    private readonly ExplorerWindowRefresher _explorerWindowRefresher;
    private readonly ILogger<SyncProjectionService> _logger;
    private readonly ConcurrentDictionary<string, byte> _pendingInSyncOperations = new(StringComparer.OrdinalIgnoreCase);
    private LocalChangeWatcher? _localChangeWatcher;

    public SyncProjectionService(
        PlaceholderManager placeholderManager,
        ISyncItemStateService stateService,
        ICloudFileOperations cloudFileOperations,
        ILogger<SyncProjectionService> logger)
    {
        _placeholderManager = placeholderManager;
        _stateService = stateService;
        _cloudFileOperations = cloudFileOperations;
        _explorerWindowRefresher = new ExplorerWindowRefresher(logger);
        _logger = logger;
    }

    public void SetLocalChangeWatcher(LocalChangeWatcher watcher)
    {
        _localChangeWatcher = watcher;
    }

    public void SuppressWatcherEvents(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        SuppressLocalWatcher(path);
    }

    public bool UpdatePlaceholderMetadata(string localPath, long fileSize, DateTime lastModified, string? remotePath = null)
    {
        SuppressLocalWatcher(localPath);
        return _placeholderManager.UpdatePlaceholderMetadata(localPath, fileSize, lastModified, remotePath);
    }

    public void CreateRemotePlaceholders(string localDirectoryPath, IReadOnlyList<RemoteItem> items)
    {
        SuppressLocalWatcher(localDirectoryPath);
        _placeholderManager.CreatePlaceholdersInDirectory(localDirectoryPath, items);
    }

    public void ScheduleRemoteDeletion(string localPath, bool isDirectory, string parentDirectoryPath)
    {
        SuppressLocalWatcher(localPath);
        SuppressLocalWatcher(parentDirectoryPath);
        _ = DeleteLocalItemAsync(localPath, isDirectory, parentDirectoryPath);
    }

    public void ScheduleMarkInSync(string localPath)
    {
        if (!_pendingInSyncOperations.TryAdd(localPath, 0))
        {
            _logger.LogDebug("In-sync projection already queued: {Path}", localPath);
            return;
        }

        _logger.LogInformation("Queued in-sync projection for {Path}", localPath);
        _ = MarkInSyncAsync(localPath, Path.GetDirectoryName(localPath));
    }

    public void RefreshDirectory(string localDirectoryPath)
    {
        _explorerWindowRefresher.RefreshDirectory(localDirectoryPath);
    }

    private void SuppressLocalWatcher(string path)
    {
        _localChangeWatcher?.SuppressPath(path, WatcherSuppressionDuration);
    }

    private async Task DeleteLocalItemAsync(string localPath, bool isDirectory, string parentDirectoryPath)
    {
        for (int attempt = 1; attempt <= DeleteRetryCount; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    if (!Directory.Exists(localPath))
                    {
                        _stateService.CompleteRemoteDeletion(localPath, isDirectory: true);
                        _explorerWindowRefresher.RefreshDirectory(parentDirectoryPath);
                        return;
                    }

                    Directory.Delete(localPath, true);
                }
                else
                {
                    if (!File.Exists(localPath))
                    {
                        _stateService.CompleteRemoteDeletion(localPath, isDirectory: false);
                        _explorerWindowRefresher.RefreshDirectory(parentDirectoryPath);
                        return;
                    }

                    File.Delete(localPath);
                }

                _logger.LogInformation("Deleted local placeholder for remote deletion: {Path}", localPath);
                _stateService.CompleteRemoteDeletion(localPath, isDirectory);
                _explorerWindowRefresher.RefreshDirectory(parentDirectoryPath);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == DeleteRetryCount)
                {
                    _logger.LogWarning(ex, "Failed to delete local item after retries: {Path}", localPath);
                    return;
                }

                _logger.LogDebug(ex, "Delete retry {Attempt}/{MaxAttempts} for {Path}", attempt, DeleteRetryCount, localPath);
                await Task.Delay(DeleteRetryDelay);
            }
        }
    }

    private async Task MarkInSyncAsync(string localPath, string? parentDirectoryPath)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            for (int attempt = 1; attempt <= InSyncRetryCount; attempt++)
            {
                var exists = File.Exists(localPath) || Directory.Exists(localPath);
                if (!exists)
                {
                    _logger.LogDebug(
                        "In-sync projection attempt {Attempt}/{MaxAttempts} skipped because path does not exist yet: {Path}",
                        attempt,
                        InSyncRetryCount,
                        localPath);
                }
                else
                {
                    if (_cloudFileOperations.TryGetPlaceholderState(localPath, out var placeholderState) &&
                        placeholderState is not null)
                    {
                        _logger.LogDebug(
                            "In-sync projection attempt {Attempt}/{MaxAttempts} for {Path}: InSyncState={InSyncState} PinState={PinState} FileSize={FileSize} OnDiskDataSize={OnDiskDataSize}",
                            attempt,
                            InSyncRetryCount,
                            localPath,
                            placeholderState.InSyncState,
                            placeholderState.PinState,
                            placeholderState.FileSize,
                            placeholderState.OnDiskDataSize);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "In-sync projection attempt {Attempt}/{MaxAttempts} has no placeholder state yet: {Path}",
                            attempt,
                            InSyncRetryCount,
                            localPath);

                        var trackedItem = _stateService.GetByLocalPath(localPath);
                        if (trackedItem is { IsDirectory: false } &&
                            !string.IsNullOrWhiteSpace(trackedItem.RemotePath) &&
                            SuppressAndConvertToPlaceholder(localPath, trackedItem.RemotePath))
                        {
                            _explorerWindowRefresher.NotifyItemChanged(localPath);

                            if (!string.IsNullOrEmpty(parentDirectoryPath))
                            {
                                SuppressLocalWatcher(parentDirectoryPath);
                                _explorerWindowRefresher.NotifyDirectoryChanged(parentDirectoryPath);
                                _explorerWindowRefresher.RefreshDirectory(parentDirectoryPath);
                            }

                            _logger.LogInformation(
                                "In-sync projection converted local file to placeholder for {Path} Attempt={Attempt} TotalDurationMs={DurationMs}",
                                localPath,
                                attempt,
                                stopwatch.ElapsedMilliseconds);
                            return;
                        }
                    }

                    SuppressLocalWatcher(localPath);
                    if (_cloudFileOperations.TrySetInSyncState(localPath, _logger))
                    {
                        _explorerWindowRefresher.NotifyItemChanged(localPath);

                        if (!string.IsNullOrEmpty(parentDirectoryPath))
                        {
                            SuppressLocalWatcher(parentDirectoryPath);
                            _explorerWindowRefresher.NotifyDirectoryChanged(parentDirectoryPath);
                            _explorerWindowRefresher.RefreshDirectory(parentDirectoryPath);
                        }

                        _logger.LogInformation(
                            "In-sync projection complete for {Path} Attempt={Attempt} TotalDurationMs={DurationMs}",
                            localPath,
                            attempt,
                            stopwatch.ElapsedMilliseconds);
                        return;
                    }
                }

                if (attempt < InSyncRetryCount)
                    await Task.Delay(InSyncRetryDelay);
            }

            _logger.LogWarning(
                "In-sync projection exhausted retries for {Path} TotalDurationMs={DurationMs}",
                localPath,
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _pendingInSyncOperations.TryRemove(localPath, out _);
        }
    }

    private bool SuppressAndConvertToPlaceholder(string localPath, string? remotePath)
    {
        SuppressLocalWatcher(localPath);
        return _cloudFileOperations.TryConvertToPlaceholder(localPath, remotePath, _logger);
    }
}
