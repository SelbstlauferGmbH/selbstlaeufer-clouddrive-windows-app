using System.Text.Json;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.Vfs;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public sealed class Propagator
{
    private readonly IVfs _vfs;
    private readonly SyncJournal _journal;
    private readonly IWebDavService _webDav;
    private readonly ISyncProblemService _problemService;
    private readonly ISyncItemStateService? _stateService;
    private readonly ExplorerItemStateService? _explorerItemStateService;
    private readonly ILogger<Propagator> _logger;

    public Propagator(
        IVfs vfs,
        SyncJournal journal,
        IWebDavService webDav,
        ISyncProblemService problemService,
        ILogger<Propagator> logger,
        ISyncItemStateService? stateService = null,
        ExplorerItemStateService? explorerItemStateService = null)
    {
        _vfs = vfs;
        _journal = journal;
        _webDav = webDav;
        _problemService = problemService;
        _logger = logger;
        _stateService = stateService;
        _explorerItemStateService = explorerItemStateService;
    }

    public async Task ApplyAsync(ReconcileAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (action.Type)
        {
            case ReconcileActionType.NoOp:
                return;
            case ReconcileActionType.UploadNew:
            case ReconcileActionType.UploadChanged:
                await UploadAsync(action, ct);
                return;
            case ReconcileActionType.DownloadNew:
            case ReconcileActionType.DownloadChanged:
                await DownloadAsync(action, ct);
                return;
            case ReconcileActionType.MoveRemote:
                await MoveRemoteAsync(action, ct);
                return;
            case ReconcileActionType.DeleteRemote:
                await DeleteRemoteAsync(action, ct);
                return;
            case ReconcileActionType.DeleteLocal:
                await DeleteLocalAsync(action, ct);
                return;
            case ReconcileActionType.Conflict:
                await ParkConflictAsync(action, ct);
                return;
            default:
                throw new InvalidOperationException($"Unsupported reconcile action: {action.Type}");
        }
    }

    private async Task UploadAsync(ReconcileAction action, CancellationToken ct)
    {
        if (Directory.Exists(action.LocalPath))
        {
            await _webDav.CreateDirectoryAsync(action.RemotePath, ct);
            var directoryMetadata = new VfsMetadata(
                action.FileId ?? action.Local?.FileId ?? SyncIdentity.NewLocalId(),
                action.RemotePath,
                action.Remote?.ETag,
                LogicalSize: 0,
                MTimeUtc: Directory.GetLastWriteTimeUtc(action.LocalPath),
                IsDirectory: true,
                action.Local?.PinState ?? PinState.Unspecified,
                InSync: true);
            await EnsurePlaceholderMetadataAsync(action.LocalPath, directoryMetadata, ct);
            await RequireInSyncAsync(action.LocalPath, ct);
            UpsertJournal(action with { FileId = directoryMetadata.FileId }, action.Remote, isDirectory: true, size: 0, checksum: null);
            await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Synced, ct);
            _logger.LogInformation(
                "Propagated local directory upload: {LocalPath} -> {RemotePath}",
                action.LocalPath,
                action.RemotePath);
            return;
        }

        if (!File.Exists(action.LocalPath))
            throw new FileNotFoundException($"Cannot upload missing local file: {action.LocalPath}", action.LocalPath);

        var parentRemote = GetParentRemotePath(action.RemotePath);
        if (!string.IsNullOrWhiteSpace(parentRemote) && parentRemote != "/")
            await _webDav.CreateDirectoryAsync(parentRemote, ct);

        await using var fileStream = new FileStream(action.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var etag = await _webDav.UploadFileAsync(action.RemotePath, fileStream, ct);
        var info = new FileInfo(action.LocalPath);
        var checksum = await FileHasher.ComputeSha256Async(action.LocalPath, ct);

        var metadata = new VfsMetadata(
            action.FileId ?? action.Local?.FileId ?? SyncIdentity.NewLocalId(),
            action.RemotePath,
            etag,
            info.Length,
            info.LastWriteTimeUtc,
            IsDirectory: false,
            action.Local?.PinState ?? PinState.Unspecified,
            InSync: true);

        await EnsurePlaceholderMetadataAsync(action.LocalPath, metadata, ct);
        await RequireInSyncAsync(action.LocalPath, ct);
        UpsertJournal(action with { FileId = metadata.FileId }, null, isDirectory: false, info.Length, checksum, etag, info.LastWriteTimeUtc);
        await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Synced, ct);
        _logger.LogInformation(
            "Propagated local file upload: {LocalPath} -> {RemotePath} Size={Size}",
            action.LocalPath,
            action.RemotePath,
            info.Length);
    }

    private async Task DownloadAsync(ReconcileAction action, CancellationToken ct)
    {
        if (action.Remote == null)
            throw new InvalidOperationException("Download action requires remote state.");

        var fileId = action.FileId ?? action.Journal?.FileId ?? SyncIdentity.RemotePathFallbackId(action.Remote.RemotePath);
        var metadata = new VfsMetadata(
            fileId,
            action.Remote.RemotePath,
            action.Remote.ETag,
            action.Remote.Size,
            action.Remote.LastModified == DateTime.MinValue ? DateTime.UtcNow : action.Remote.LastModified.ToUniversalTime(),
            action.Remote.IsDirectory,
            action.Local?.PinState ?? PinState.Unspecified,
            InSync: true);

        if (action.Remote.IsDirectory)
        {
            await _vfs.CreatePlaceholderAsync(action.LocalPath, metadata, ct);
            UpsertJournal(action with { FileId = fileId }, action.Remote, isDirectory: true, size: 0, checksum: null);
            await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Synced, ct);
            _logger.LogInformation(
                "Propagated remote directory download: {RemotePath} -> {LocalPath}",
                action.Remote.RemotePath,
                action.LocalPath);
            return;
        }

        await using var remoteStream = await _webDav.DownloadFileAsync(action.Remote.RemotePath, ct);
        var hydrate = await _vfs.HydrateAsync(action.LocalPath, remoteStream, action.Remote.Size, progress: null, ct);
        if (hydrate.Failed)
            throw new IOException(hydrate.ErrorMessage);

        await EnsurePlaceholderMetadataAsync(action.LocalPath, metadata, ct);
        await RequireInSyncAsync(action.LocalPath, ct);
        var checksum = File.Exists(action.LocalPath)
            ? await FileHasher.ComputeSha256Async(action.LocalPath, ct)
            : null;
        UpsertJournal(action with { FileId = fileId }, action.Remote, isDirectory: false, action.Remote.Size, checksum);
        await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Synced, ct);
        _logger.LogInformation(
            "Propagated remote file download: {RemotePath} -> {LocalPath} Size={Size}",
            action.Remote.RemotePath,
            action.LocalPath,
            action.Remote.Size);
    }

    private async Task EnsurePlaceholderMetadataAsync(string localPath, VfsMetadata metadata, CancellationToken ct)
    {
        var update = await _vfs.UpdateMetadataAsync(localPath, metadata, ct);
        if (update.Succeeded)
            return;

        var convert = await _vfs.ConvertToPlaceholderAsync(localPath, metadata, ct);
        if (convert.Failed)
            throw new IOException(convert.ErrorMessage ?? $"Failed to convert {localPath} to placeholder.");
    }

    private async Task RequireInSyncAsync(string localPath, CancellationToken ct)
    {
        var result = await _vfs.SetInSyncAsync(localPath, true, ct);
        if (result.Failed)
            throw new IOException(result.ErrorMessage ?? $"Failed to mark {localPath} in sync.");
    }

    private async Task MoveRemoteAsync(ReconcileAction action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.PreviousRemotePath))
            throw new InvalidOperationException("MoveRemote requires previous remote path.");

        var previousLocalPath = action.PreviousLocalPath ?? action.Journal?.LocalPath ?? action.LocalPath;
        var previousSyncItem = _stateService?.GetByLocalPath(previousLocalPath);
        await _webDav.MoveAsync(action.PreviousRemotePath, action.RemotePath, ct);
        var record = action.Journal ?? CreateJournalRecordFromLegacyState(action, previousSyncItem);
        var previousJournalLocalPath = record.LocalPath;
        var previousJournalRemotePath = record.RemotePath;
        record.LocalPath = action.LocalPath;
        record.RemotePath = action.RemotePath;
        record.LocalPendingOp = null;
        record.InSync = true;
        _journal.Upsert(record);
        UpdateJournalDescendantsForMove(previousJournalLocalPath, action.LocalPath, previousJournalRemotePath, action.RemotePath, record.FileId);
        UpdateLegacyStateForMove(previousLocalPath, action.LocalPath, action.PreviousRemotePath, action.RemotePath, record);
        _logger.LogInformation(
            "Propagated remote move: {OldRemotePath} -> {NewRemotePath}",
            action.PreviousRemotePath,
            action.RemotePath);

        if (action.Local != null)
        {
            await _vfs.UpdateMetadataAsync(action.LocalPath, new VfsMetadata(
                record.FileId,
                record.RemotePath,
                record.ETag,
                record.Size,
                record.MTimeUtc ?? DateTime.UtcNow,
                record.IsDirectory,
                action.Local.PinState,
                InSync: true), ct);
        }

        await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Synced, ct);
    }

    private async Task DeleteRemoteAsync(ReconcileAction action, CancellationToken ct)
    {
        var isDirectory = IsDirectoryAction(action);
        await _webDav.DeleteAsync(action.RemotePath, ct);
        DeleteTrackedState(action, isDirectory);
        _logger.LogInformation("Propagated remote delete: {RemotePath}", action.RemotePath);
    }

    private async Task DeleteLocalAsync(ReconcileAction action, CancellationToken ct)
    {
        var isDirectory = IsDirectoryAction(action);
        var result = await _vfs.DeleteAsync(action.LocalPath, recursive: true, ct);
        if (result.Failed)
            throw new IOException(result.ErrorMessage);

        DeleteTrackedState(action, isDirectory);
        _logger.LogInformation("Propagated local delete: {LocalPath}", action.LocalPath);
    }

    private async Task ParkConflictAsync(ReconcileAction action, CancellationToken ct)
    {
        var fileId = action.FileId ?? action.Journal?.FileId ?? action.Local?.FileId ?? SyncIdentity.NewLocalId();
        var localSize = action.Local?.LogicalSize ?? (File.Exists(action.LocalPath) ? new FileInfo(action.LocalPath).Length : 0);
        var localMTime = action.Local?.MTimeUtc ?? (File.Exists(action.LocalPath) ? File.GetLastWriteTimeUtc(action.LocalPath) : null);

        _journal.UpsertConflict(new PendingConflictRecord
        {
            FileId = fileId,
            LocalPath = action.LocalPath,
            RemotePath = action.RemotePath,
            BaseETag = action.Journal?.BaseETag ?? action.Journal?.ETag,
            RemoteETag = action.Remote?.ETag,
            LocalMTimeUtc = localMTime,
            RemoteMTimeUtc = action.Remote?.LastModified,
            LocalSize = localSize,
            RemoteSize = action.Remote?.Size ?? 0,
            Status = PendingConflictStatus.Pending
        });

        await _vfs.SetInSyncAsync(action.LocalPath, false, ct);
        _stateService?.UpdateStatus(action.LocalPath, SyncStatus.Conflict);
        await SetExplorerStateAsync(action.LocalPath, ExplorerItemState.Conflict, ct);

        var fileName = Path.GetFileName(action.LocalPath);
        _problemService.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Conflict(action.LocalPath),
            ProblemType = SyncProblemType.Conflict,
            Severity = SyncProblemSeverity.Warning,
            Title = AppLocalizer.Instance.Format("Problem_Conflict_Title", fileName),
            Summary = AppLocalizer.Instance.GetString("Problem_ImportedConflict_Summary"),
            Details = JsonSerializer.Serialize(new
            {
                action.RemotePath,
                action.Type,
                JournalETag = action.Journal?.ETag,
                RemoteETag = action.Remote?.ETag
            }),
            LocalPath = action.LocalPath,
            RemotePath = action.RemotePath,
            FirstOccurredAt = DateTime.UtcNow,
            LastOccurredAt = DateTime.UtcNow
        });

        _logger.LogWarning("Conflict parked for {LocalPath}", action.LocalPath);
    }

    private void UpsertJournal(
        ReconcileAction action,
        RemoteItem? remote,
        bool isDirectory,
        long size,
        string? checksum,
        string? etagOverride = null,
        DateTime? mtimeOverride = null)
    {
        var record = action.Journal ?? new SyncJournalRecord
        {
            FileId = action.FileId ?? SyncIdentity.NewLocalId()
        };

        record.LocalPath = action.LocalPath;
        record.RemotePath = action.RemotePath;
        record.IsDirectory = isDirectory;
        record.Size = size;
        record.Checksum = checksum;
        record.ETag = etagOverride ?? remote?.ETag ?? record.ETag;
        record.MTimeUtc = mtimeOverride ?? remote?.LastModified.ToUniversalTime() ?? DateTime.UtcNow;
        record.BaseETag = record.ETag;
        record.InSync = true;
        record.LocalPendingOp = null;
        _journal.Upsert(record);
        _stateService?.Upsert(new SyncItem
        {
            LocalPath = record.LocalPath,
            RemotePath = record.RemotePath,
            IsDirectory = record.IsDirectory,
            FileSize = record.Size,
            RemoteETag = record.ETag,
            RemoteLastModified = record.MTimeUtc,
            LocalHash = record.Checksum,
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });
    }

    private static string GetParentRemotePath(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    private Task SetExplorerStateAsync(string localPath, ExplorerItemState state, CancellationToken ct)
    {
        return _explorerItemStateService?.SetStateAsync(localPath, state, ct) ?? Task.CompletedTask;
    }

    private SyncJournalRecord CreateJournalRecordFromLegacyState(ReconcileAction action, SyncItem? syncItem)
    {
        var localPath = action.PreviousLocalPath ?? syncItem?.LocalPath ?? action.LocalPath;
        var remotePath = action.PreviousRemotePath ?? syncItem?.RemotePath ?? action.RemotePath;
        var isDirectory = syncItem?.IsDirectory ?? Directory.Exists(action.LocalPath);
        var fileInfo = !isDirectory && File.Exists(action.LocalPath) ? new FileInfo(action.LocalPath) : null;

        return new SyncJournalRecord
        {
            FileId = action.FileId ?? SyncIdentity.RemotePathFallbackId(remotePath),
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = isDirectory,
            Size = syncItem?.FileSize ?? fileInfo?.Length ?? 0,
            ETag = syncItem?.RemoteETag,
            MTimeUtc = syncItem?.RemoteLastModified ?? fileInfo?.LastWriteTimeUtc ?? DateTime.UtcNow,
            Checksum = syncItem?.LocalHash,
            BaseETag = syncItem?.RemoteETag,
            InSync = true
        };
    }

    private bool IsDirectoryAction(ReconcileAction action)
    {
        if (action.Journal?.IsDirectory == true || action.Remote?.IsDirectory == true)
            return true;

        var syncItem = _stateService?.GetByLocalPath(action.LocalPath);
        return syncItem?.IsDirectory == true || Directory.Exists(action.LocalPath);
    }

    private void DeleteTrackedState(ReconcileAction action, bool isDirectory)
    {
        if (isDirectory)
        {
            _stateService?.DeleteChildren(action.LocalPath);
            foreach (var record in GetJournalDescendants(action.LocalPath))
                _journal.Delete(record.FileId);
        }

        var journalRecord = action.Journal
            ?? (!string.IsNullOrWhiteSpace(action.FileId) ? _journal.GetByFileId(action.FileId) : null)
            ?? _journal.GetByLocalPath(action.LocalPath)
            ?? _journal.GetByRemotePath(action.RemotePath);

        if (journalRecord != null)
            _journal.Delete(journalRecord.FileId);

        _stateService?.Delete(action.LocalPath);
    }

    private void UpdateJournalDescendantsForMove(
        string oldLocalRoot,
        string newLocalRoot,
        string oldRemoteRoot,
        string newRemoteRoot,
        string rootFileId)
    {
        foreach (var record in GetJournalDescendants(oldLocalRoot))
        {
            if (string.Equals(record.FileId, rootFileId, StringComparison.OrdinalIgnoreCase))
                continue;

            record.LocalPath = ReplaceLocalRoot(record.LocalPath, oldLocalRoot, newLocalRoot);
            record.RemotePath = ReplaceRemoteRoot(record.RemotePath, oldRemoteRoot, newRemoteRoot);
            record.InSync = true;
            record.LocalPendingOp = null;
            _journal.Upsert(record);
        }
    }

    private void UpdateLegacyStateForMove(
        string oldLocalRoot,
        string newLocalRoot,
        string oldRemoteRoot,
        string newRemoteRoot,
        SyncJournalRecord rootRecord)
    {
        if (_stateService == null)
            return;

        var descendants = _stateService.GetAll()
            .Where(item => IsLocalDescendant(item.LocalPath, oldLocalRoot))
            .ToList();

        foreach (var item in descendants)
        {
            _stateService.Delete(item.LocalPath);
            item.LocalPath = ReplaceLocalRoot(item.LocalPath, oldLocalRoot, newLocalRoot);
            item.RemotePath = ReplaceRemoteRoot(item.RemotePath, oldRemoteRoot, newRemoteRoot);
            item.SyncStatus = SyncStatus.Synced;
            item.LastSynced = DateTime.UtcNow;
            _stateService.Upsert(item);
        }

        if (!string.Equals(oldLocalRoot, newLocalRoot, StringComparison.OrdinalIgnoreCase))
            _stateService.Delete(oldLocalRoot);

        _stateService.Upsert(new SyncItem
        {
            LocalPath = rootRecord.LocalPath,
            RemotePath = rootRecord.RemotePath,
            IsDirectory = rootRecord.IsDirectory,
            FileSize = rootRecord.Size,
            RemoteETag = rootRecord.ETag,
            RemoteLastModified = rootRecord.MTimeUtc,
            LocalHash = rootRecord.Checksum,
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });
    }

    private IReadOnlyList<SyncJournalRecord> GetJournalDescendants(string localRoot)
    {
        return _journal.GetAll()
            .Where(record => IsLocalDescendant(record.LocalPath, localRoot))
            .ToList();
    }

    private static bool IsLocalDescendant(string candidatePath, string localRoot)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(localRoot))
            return false;

        var normalizedRoot = localRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidatePath.Length > normalizedRoot.Length &&
               candidatePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceLocalRoot(string path, string oldRoot, string newRoot)
    {
        var suffix = path.Length > oldRoot.Length ? path[oldRoot.Length..] : string.Empty;
        return newRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + suffix;
    }

    private static string ReplaceRemoteRoot(string path, string oldRoot, string newRoot)
    {
        var normalizedOld = oldRoot.TrimEnd('/');
        var normalizedNew = newRoot.TrimEnd('/');
        var suffix = path.Length > normalizedOld.Length ? path[normalizedOld.Length..] : string.Empty;
        return string.IsNullOrEmpty(normalizedNew) ? "/" + suffix.TrimStart('/') : normalizedNew + suffix;
    }
}
