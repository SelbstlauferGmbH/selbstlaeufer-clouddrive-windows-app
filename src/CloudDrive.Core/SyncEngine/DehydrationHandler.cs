using System.Runtime.InteropServices;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public class DehydrationHandler
{
    private readonly ISyncItemStateService _stateService;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<DehydrationHandler> _logger;
    private readonly ICloudFileOperations _cloudFileOperations;
    private readonly ISyncProjectionService? _projectionService;
    private CF_CONNECTION_KEY _connectionKey;

    private static readonly NTStatus STATUS_SUCCESS = new(0);
    private static readonly NTStatus STATUS_CLOUD_FILE_NOT_IN_SYNC = new(unchecked((int)0xC000CF04));

    public DehydrationHandler(
        ISyncItemStateService stateService,
        PathMapper pathMapper,
        ILogger<DehydrationHandler> logger,
        ICloudFileOperations? cloudFileOperations = null,
        ISyncProjectionService? projectionService = null)
    {
        _stateService = stateService;
        _pathMapper = pathMapper;
        _logger = logger;
        _cloudFileOperations = cloudFileOperations ?? new CloudFileOperations();
        _projectionService = projectionService;
    }

    public void SetConnectionKey(CF_CONNECTION_KEY key) => _connectionKey = key;

    public Task HandleNotifyDehydrateAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var normalizedPath = callbackInfo.NormalizedPath;
        var remotePath = _pathMapper.NormalizedPathToRemotePath(normalizedPath);
        var localPath = _pathMapper.ToLocalPath(remotePath);
        var trackedItem = _stateService.GetByLocalPath(localPath);
        var placeholderStateAvailable = _cloudFileOperations.TryGetPlaceholderState(localPath, out var placeholderState);
        var decision = EvaluateDehydrationDecision(trackedItem, placeholderState, placeholderStateAvailable);

        LogDehydrationRequest(normalizedPath, localPath, remotePath, trackedItem, placeholderStateAvailable, placeholderState, decision);

        if (!decision.Allowed)
        {
            TryScheduleInSyncRepair(localPath, "cfapi notify dehydrate", trackedItem, placeholderState, placeholderStateAvailable);
            AcknowledgeDehydrate(callbackInfo, decision.CompletionStatus, localPath, decision.Reason);
            _logger.LogWarning(
                "DEHYDRATE denied: {Path} Reason={Reason} CompletionStatus={CompletionStatus}",
                localPath,
                decision.Reason,
                decision.CompletionStatus);
            return Task.CompletedTask;
        }

        SuppressWatcherEvents(localPath);

        if (AcknowledgeDehydrate(callbackInfo, STATUS_SUCCESS, localPath, decision.Reason))
        {
            PersistCloudOnlyState(localPath, remotePath, trackedItem, placeholderState);
            _logger.LogInformation("DEHYDRATE allowed: {Path} Reason={Reason}", localPath, decision.Reason);
        }

        return Task.CompletedTask;
    }

    public bool TryDehydrateUnpinnedPlaceholder(string localPath, string trigger)
    {
        var trackedItem = _stateService.GetByLocalPath(localPath);
        var placeholderStateAvailable = _cloudFileOperations.TryGetPlaceholderState(localPath, out var placeholderState);
        var decision = EvaluateDehydrationDecision(trackedItem, placeholderState, placeholderStateAvailable);

        if (!placeholderStateAvailable || placeholderState is null)
        {
            _logger.LogDebug(
                "DEHYDRATE skipped: placeholder state unavailable for {Path} Trigger={Trigger} TrackedStatus={TrackedStatus}",
                localPath,
                trigger,
                trackedItem?.SyncStatus.ToString() ?? "<missing>");
            return false;
        }

        if (!placeholderState.ShouldDehydrateUnpinnedFile)
        {
            if (TryScheduleInSyncRepair(localPath, trigger, trackedItem, placeholderState, placeholderStateAvailable))
                return false;

            _logger.LogDebug(
                "DEHYDRATE skipped: placeholder is not an unpinned dehydration candidate for {Path} Trigger={Trigger} PinState={PinState} InSyncState={InSyncState} OnDiskDataSize={OnDiskDataSize} ModifiedDataSize={ModifiedDataSize}",
                localPath,
                trigger,
                placeholderState.PinState,
                placeholderState.InSyncState,
                placeholderState.OnDiskDataSize,
                placeholderState.ModifiedDataSize);
            return false;
        }

        if (!decision.Allowed)
        {
            _logger.LogWarning(
                "DEHYDRATE skipped: unpinned placeholder is not safe to dehydrate for {Path} Trigger={Trigger} Reason={Reason}",
                localPath,
                trigger,
                decision.Reason);
            return false;
        }

        _logger.LogInformation(
            "DEHYDRATE executing for unpinned placeholder: {Path} Trigger={Trigger} RemotePath={RemotePath}",
            localPath,
            trigger,
            trackedItem?.RemotePath ?? _pathMapper.ToRemotePath(localPath));

        return DehydrateFile(localPath, trigger, trackedItem, placeholderState);
    }

    public bool DehydrateFile(string localPath)
        => DehydrateFile(localPath, "direct request", _stateService.GetByLocalPath(localPath), placeholderState: null);

    private bool DehydrateFile(
        string localPath,
        string trigger,
        SyncItem? trackedItem,
        CloudFilePlaceholderState? placeholderState)
    {
        try
        {
            using var handle = Kernel32.CreateFile(
                localPath,
                Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete,
                null,
                System.IO.FileMode.Open,
                FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                _logger.LogWarning("Cannot open file for dehydration: {Path} Trigger={Trigger}", localPath, trigger);
                return false;
            }

            SuppressWatcherEvents(localPath);
            var hr = CfDehydratePlaceholder(handle, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE);
            if (hr.Failed)
            {
                _logger.LogError("CfDehydratePlaceholder failed: {Path} Trigger={Trigger} Hr={Hr}", localPath, trigger, hr);
                return false;
            }
            else
            {
                var remotePath = trackedItem?.RemotePath ?? _pathMapper.ToRemotePath(localPath);
                PersistCloudOnlyState(localPath, remotePath, trackedItem, placeholderState);
                RefreshParentDirectory(localPath);
                _logger.LogInformation("File dehydrated: {Path} Trigger={Trigger}", localPath, trigger);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dehydrate: {Path} Trigger={Trigger}", localPath, trigger);
            return false;
        }
    }

    internal static DehydrationDecision EvaluateDehydrationDecision(
        SyncItem? trackedItem,
        CloudFilePlaceholderState? placeholderState,
        bool placeholderStateAvailable)
    {
        if (placeholderStateAvailable && placeholderState is not null)
        {
            if (placeholderState.InSyncState != CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
            {
                return new DehydrationDecision(
                    false,
                    STATUS_CLOUD_FILE_NOT_IN_SYNC,
                    $"placeholder is not marked in-sync ({placeholderState.InSyncState})");
            }

            if (placeholderState.HasLocalContentChanges)
            {
                return new DehydrationDecision(
                    false,
                    STATUS_CLOUD_FILE_NOT_IN_SYNC,
                    $"placeholder has local content changes ({placeholderState.ModifiedDataSize} modified bytes)");
            }

            return new DehydrationDecision(
                true,
                STATUS_SUCCESS,
                "placeholder is marked in-sync");
        }

        if (trackedItem?.SyncStatus == SyncStatus.Synced)
        {
            return new DehydrationDecision(
                true,
                STATUS_SUCCESS,
                "tracked item is marked Synced while placeholder state is unavailable");
        }

        if (trackedItem != null)
        {
            return new DehydrationDecision(
                false,
                STATUS_CLOUD_FILE_NOT_IN_SYNC,
                $"tracked item status is {trackedItem.SyncStatus} and placeholder state is unavailable");
        }

        return new DehydrationDecision(
            false,
            STATUS_CLOUD_FILE_NOT_IN_SYNC,
            "tracked item is missing and placeholder state is unavailable");
    }

    internal static bool ShouldRepairInSyncProjection(
        SyncItem? trackedItem,
        CloudFilePlaceholderState? placeholderState,
        bool placeholderStateAvailable)
    {
        if (!placeholderStateAvailable || placeholderState is null)
            return false;

        if (trackedItem?.SyncStatus is not (SyncStatus.Synced or SyncStatus.PendingDownload))
            return false;

        return !placeholderState.IsDirectory
            && !placeholderState.IsInSync
            && placeholderState.HasDataOnDisk
            && !placeholderState.NeedsHydration
            && !placeholderState.HasLocalContentChanges;
    }

    private void LogDehydrationRequest(
        string normalizedPath,
        string localPath,
        string remotePath,
        SyncItem? trackedItem,
        bool placeholderStateAvailable,
        CloudFilePlaceholderState? placeholderState,
        DehydrationDecision decision)
    {
        _logger.LogInformation(
            "DEHYDRATE request: NormalizedPath={NormalizedPath} LocalPath={LocalPath} RemotePath={RemotePath} TrackedStatus={TrackedStatus} PlaceholderStateAvailable={PlaceholderStateAvailable}",
            normalizedPath,
            localPath,
            remotePath,
            trackedItem?.SyncStatus.ToString() ?? "<missing>",
            placeholderStateAvailable);

        if (placeholderStateAvailable && placeholderState is not null)
        {
            _logger.LogInformation(
                "DEHYDRATE placeholder state: {Path} InSyncState={InSyncState} PinState={PinState} FileSize={FileSize} OnDiskDataSize={OnDiskDataSize} ValidatedDataSize={ValidatedDataSize} ModifiedDataSize={ModifiedDataSize}",
                localPath,
                placeholderState.InSyncState,
                placeholderState.PinState,
                placeholderState.FileSize,
                placeholderState.OnDiskDataSize,
                placeholderState.ValidatedDataSize,
                placeholderState.ModifiedDataSize);
        }
        else
        {
            _logger.LogWarning("DEHYDRATE placeholder state unavailable: {Path}", localPath);
        }

        _logger.LogInformation(
            "DEHYDRATE decision: {Path} Allowed={Allowed} Reason={Reason}",
            localPath,
            decision.Allowed,
            decision.Reason);
    }

    private void SuppressWatcherEvents(string localPath)
    {
        if (_projectionService == null)
            return;

        _projectionService.SuppressWatcherEvents(localPath);

        var parentDirectoryPath = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
            _projectionService.SuppressWatcherEvents(parentDirectoryPath);
    }

    private void RefreshParentDirectory(string localPath)
    {
        if (_projectionService == null)
            return;

        var parentDirectoryPath = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(parentDirectoryPath))
            _projectionService.RefreshDirectory(parentDirectoryPath);
    }

    private bool TryScheduleInSyncRepair(
        string localPath,
        string trigger,
        SyncItem? trackedItem,
        CloudFilePlaceholderState? placeholderState,
        bool placeholderStateAvailable)
    {
        if (!ShouldRepairInSyncProjection(trackedItem, placeholderState, placeholderStateAvailable))
            return false;

        if (_projectionService == null)
        {
            _logger.LogWarning(
                "DEHYDRATE cannot repair stale in-sync state because the projection service is unavailable: {Path} Trigger={Trigger}",
                localPath,
                trigger);
            return false;
        }

        _logger.LogInformation(
            "DEHYDRATE deferred while repairing stale in-sync state: {Path} Trigger={Trigger} TrackedStatus={TrackedStatus} PinState={PinState} FileSize={FileSize} OnDiskDataSize={OnDiskDataSize}",
            localPath,
            trigger,
            trackedItem?.SyncStatus.ToString() ?? "<missing>",
            placeholderState!.PinState,
            placeholderState.FileSize,
            placeholderState.OnDiskDataSize);

        _projectionService.ScheduleMarkInSync(localPath);
        return true;
    }

    private void PersistCloudOnlyState(
        string localPath,
        string remotePath,
        SyncItem? trackedItem,
        CloudFilePlaceholderState? placeholderState)
    {
        var item = trackedItem ?? new SyncItem
        {
            LocalPath = localPath
        };

        item.LocalPath = localPath;
        item.RemotePath = remotePath;
        item.IsDirectory = placeholderState?.IsDirectory ?? Directory.Exists(localPath);
        item.FileSize = item.IsDirectory
            ? 0
            : placeholderState?.FileSize ?? trackedItem?.FileSize ?? TryGetCurrentFileSize(localPath);
        item.SyncStatus = SyncStatus.CloudOnly;
        item.LastSynced = DateTime.UtcNow;
        _stateService.Upsert(item);

        _logger.LogInformation(
            "DEHYDRATE tracked state updated: {Path} Status={Status} FileSize={FileSize} PreviouslyTracked={PreviouslyTracked}",
            localPath,
            item.SyncStatus,
            item.FileSize,
            trackedItem is not null);
    }

    private static long TryGetCurrentFileSize(string localPath)
    {
        try
        {
            return File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool AcknowledgeDehydrate(CF_CALLBACK_INFO callbackInfo, NTStatus status, string path, string reason)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DEHYDRATE,
            ConnectionKey = _connectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        var ad = new CF_OPERATION_PARAMETERS.ACKDEHYDRATE
        {
            Flags = CF_OPERATION_ACK_DEHYDRATE_FLAGS.CF_OPERATION_ACK_DEHYDRATE_FLAG_NONE,
            CompletionStatus = status
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(ad);

        var hr = CfExecute(opInfo, ref opParams);
        if (hr.Failed)
        {
            _logger.LogError(
                "ACK_DEHYDRATE failed: {Path} CompletionStatus={CompletionStatus} Reason={Reason} Hr={Hr}",
                path,
                status,
                reason,
                hr);
            return false;
        }

        _logger.LogInformation(
            "ACK_DEHYDRATE complete: {Path} CompletionStatus={CompletionStatus} Reason={Reason}",
            path,
            status,
            reason);
        return true;
    }
}

internal sealed record DehydrationDecision(
    bool Allowed,
    NTStatus CompletionStatus,
    string Reason);
