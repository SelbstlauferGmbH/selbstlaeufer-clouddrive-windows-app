using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Vfs;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public sealed class DiscoveryWalker
{
    private readonly IVfs _vfs;
    private readonly SyncJournal _journal;
    private readonly IWebDavService _webDav;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<DiscoveryWalker> _logger;

    public DiscoveryWalker(
        IVfs vfs,
        SyncJournal journal,
        IWebDavService webDav,
        PathMapper pathMapper,
        ILogger<DiscoveryWalker> logger)
    {
        _vfs = vfs;
        _journal = journal;
        _webDav = webDav;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReconcileAction>> WalkAsync(string localDirectoryPath, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remoteDirectoryPath = _pathMapper.ToRemotePath(localDirectoryPath);
        var localEntries = (await _vfs.EnumerateChildrenAsync(localDirectoryPath, depth, ct))
            .Where(entry => !TransientFilePolicy.ShouldIgnoreLocalPath(entry.LocalPath, entry.IsDirectory))
            .ToList();
        var remoteItems = (await ListRemoteItemsAsync(remoteDirectoryPath, depth, ct))
            .Where(item => !TransientFilePolicy.ShouldIgnoreRemotePath(item.RemotePath, item.IsDirectory))
            .ToList();
        var journalChildren = GetJournalRecords(localDirectoryPath, depth)
            .Where(record =>
                !TransientFilePolicy.ShouldIgnoreLocalPath(record.LocalPath, record.IsDirectory) &&
                !TransientFilePolicy.ShouldIgnoreRemotePath(record.RemotePath, record.IsDirectory))
            .ToList();

        var actions = new List<ReconcileAction>();
        var localByPath = localEntries.ToDictionary(e => e.LocalPath, StringComparer.OrdinalIgnoreCase);
        var localRemotePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remoteByPath = remoteItems.ToDictionary(i => i.RemotePath, StringComparer.OrdinalIgnoreCase);
        var journalByFileId = journalChildren
            .Where(r => !string.IsNullOrWhiteSpace(r.FileId))
            .ToDictionary(r => r.FileId, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in localEntries)
        {
            ct.ThrowIfCancellationRequested();
            var local = entry.Placeholder ?? await _vfs.GetPlaceholderInfoAsync(entry.LocalPath, ct);
            var fileId = local?.FileId;
            var remotePath = local?.RemotePath;
            if (string.IsNullOrWhiteSpace(remotePath))
                remotePath = _pathMapper.ToRemotePath(entry.LocalPath);

            localRemotePaths.Add(remotePath);
            var journalRecord = ResolveJournalRecord(entry.LocalPath, remotePath, fileId);
            remoteByPath.TryGetValue(remotePath, out var remote);

            if (journalRecord != null &&
                !string.Equals(journalRecord.LocalPath, entry.LocalPath, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(fileId) &&
                string.Equals(journalRecord.FileId, fileId, StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new ReconcileAction(
                    ReconcileActionType.MoveRemote,
                    entry.LocalPath,
                    remotePath,
                    fileId,
                    local,
                    journalRecord,
                    remote,
                    PreviousLocalPath: journalRecord.LocalPath,
                    PreviousRemotePath: journalRecord.RemotePath));
                continue;
            }

            actions.Add(ClassifyExistingLocal(entry.LocalPath, remotePath, fileId, local, journalRecord, remote));
        }

        foreach (var remote in remoteItems)
        {
            ct.ThrowIfCancellationRequested();
            if (localRemotePaths.Contains(remote.RemotePath))
                continue;

            var journalRecord = _journal.GetByRemotePath(remote.RemotePath);
            var localPath = journalRecord?.LocalPath ?? _pathMapper.ToLocalPath(remote.RemotePath);
            if (localByPath.ContainsKey(localPath))
                continue;

            if (journalRecord != null)
            {
                actions.Add(ConflictDetector.HasRemoteChanged(journalRecord, remote)
                    ? new ReconcileAction(
                        ReconcileActionType.Conflict,
                        localPath,
                        remote.RemotePath,
                        journalRecord.FileId,
                        Local: null,
                        Journal: journalRecord,
                        Remote: remote)
                    : new ReconcileAction(
                        ReconcileActionType.DeleteRemote,
                        localPath,
                        remote.RemotePath,
                        journalRecord.FileId,
                        Local: null,
                        Journal: journalRecord,
                        Remote: remote));
                continue;
            }

            actions.Add(new ReconcileAction(
                ReconcileActionType.DownloadNew,
                localPath,
                remote.RemotePath,
                SyncIdentity.RemotePathFallbackId(remote.RemotePath),
                Local: null,
                Journal: null,
                Remote: remote));
        }

        foreach (var journalRecord in journalChildren)
        {
            ct.ThrowIfCancellationRequested();
            if (localByPath.ContainsKey(journalRecord.LocalPath) || remoteByPath.ContainsKey(journalRecord.RemotePath))
                continue;

            actions.Add(new ReconcileAction(
                ReconcileActionType.DeleteRemote,
                journalRecord.LocalPath,
                journalRecord.RemotePath,
                journalRecord.FileId,
                Local: null,
                Journal: journalRecord,
                Remote: null));
        }

        _logger.LogDebug(
            "Discovery completed for {LocalPath}: Local={LocalCount} Remote={RemoteCount} Journal={JournalCount} Actions={ActionCount}",
            localDirectoryPath,
            localEntries.Count,
            remoteItems.Count,
            journalChildren.Count,
            actions.Count);

        return actions;
    }

    private async Task<IReadOnlyList<RemoteItem>> ListRemoteItemsAsync(
        string remoteDirectoryPath,
        int depth,
        CancellationToken ct)
    {
        if (_webDav is ISyncCollectionWebDavService syncCollectionWebDav)
        {
            try
            {
                var result = await syncCollectionWebDav.ReportSyncCollectionAsync(remoteDirectoryPath, null, depth, ct);
                if (result.Supported)
                    return result.Items;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "sync-collection REPORT failed for {RemotePath}; falling back to PROPFIND", remoteDirectoryPath);
            }
        }

        return await ListRemoteItemsByPropfindAsync(remoteDirectoryPath, depth, ct);
    }

    private async Task<IReadOnlyList<RemoteItem>> ListRemoteItemsByPropfindAsync(
        string remoteDirectoryPath,
        int depth,
        CancellationToken ct)
    {
        var items = new List<RemoteItem>();
        var directChildren = await _webDav.ListDirectoryAsync(remoteDirectoryPath, ct);
        items.AddRange(directChildren);

        if (depth <= 1)
            return items;

        foreach (var childDirectory in directChildren.Where(item => item.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            items.AddRange(await ListRemoteItemsByPropfindAsync(childDirectory.RemotePath, depth - 1, ct));
        }

        return items;
    }

    private SyncJournalRecord? ResolveJournalRecord(string localPath, string remotePath, string? fileId)
    {
        if (!string.IsNullOrWhiteSpace(fileId))
        {
            var byFileId = _journal.GetByFileId(fileId);
            if (byFileId != null)
                return byFileId;
        }

        return _journal.GetByLocalPath(localPath) ?? _journal.GetByRemotePath(remotePath);
    }

    private IReadOnlyList<SyncJournalRecord> GetJournalRecords(string localDirectoryPath, int depth)
    {
        if (depth <= 1)
            return _journal.GetChildren(localDirectoryPath);

        var normalizedRoot = localDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _journal.GetAll()
            .Where(record => IsSameOrDescendant(record.LocalPath, normalizedRoot))
            .ToList();
    }

    private static bool IsSameOrDescendant(string candidatePath, string normalizedRoot)
    {
        return string.Equals(candidatePath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private ReconcileAction ClassifyExistingLocal(
        string localPath,
        string remotePath,
        string? fileId,
        PlaceholderInfo? local,
        SyncJournalRecord? journal,
        RemoteItem? remote)
    {
        if (journal == null)
        {
            if (remote == null)
                return new ReconcileAction(ReconcileActionType.UploadNew, localPath, remotePath, fileId, local, null, null);

            if (local == null)
                return new ReconcileAction(ReconcileActionType.Conflict, localPath, remotePath, fileId, null, null, remote);

            var baseline = CreateBaselineFromPlaceholder(localPath, remotePath, local, remote);
            return ClassifyMissingJournalBaseline(localPath, remotePath, local, baseline, remote);
        }

        if (remote == null)
        {
            return local != null && ConflictDetector.HasLocalChanged(local, journal)
                ? new ReconcileAction(ReconcileActionType.Conflict, localPath, remotePath, fileId, local, journal, null)
                : new ReconcileAction(ReconcileActionType.DeleteLocal, localPath, remotePath, fileId, local, journal, null);
        }

        if (ConflictDetector.IsConflict(local, journal, remote))
            return new ReconcileAction(ReconcileActionType.Conflict, localPath, remotePath, fileId, local, journal, remote);

        if (local != null && ConflictDetector.HasLocalChanged(local, journal))
            return new ReconcileAction(ReconcileActionType.UploadChanged, localPath, remotePath, fileId, local, journal, remote);

        if (ConflictDetector.HasRemoteChanged(journal, remote))
            return new ReconcileAction(ReconcileActionType.DownloadChanged, localPath, remotePath, fileId, local, journal, remote);

        return ReconcileAction.NoOp(localPath, remotePath, fileId, local, journal, remote);
    }

    private ReconcileAction ClassifyMissingJournalBaseline(
        string localPath,
        string remotePath,
        PlaceholderInfo local,
        SyncJournalRecord baseline,
        RemoteItem remote)
    {
        if (local.IsDirectory != remote.IsDirectory)
        {
            return new ReconcileAction(
                ReconcileActionType.Conflict,
                localPath,
                remotePath,
                baseline.FileId,
                local,
                baseline,
                remote);
        }

        if (local.IsDirectory)
        {
            SeedJournalBaseline(baseline, localChanged: false);
            return ReconcileAction.NoOp(localPath, remotePath, baseline.FileId, local, baseline, remote);
        }

        if (string.IsNullOrWhiteSpace(local.ETag))
        {
            return new ReconcileAction(
                ReconcileActionType.Conflict,
                localPath,
                remotePath,
                baseline.FileId,
                local,
                baseline,
                remote);
        }

        var localChanged = ConflictDetector.HasLocalChanged(local, baseline);
        var remoteChanged = ConflictDetector.HasRemoteChanged(baseline, remote);

        if (localChanged && remoteChanged)
        {
            return new ReconcileAction(
                ReconcileActionType.Conflict,
                localPath,
                remotePath,
                baseline.FileId,
                local,
                baseline,
                remote);
        }

        SeedJournalBaseline(baseline, localChanged);

        if (localChanged)
        {
            return new ReconcileAction(
                ReconcileActionType.UploadChanged,
                localPath,
                remotePath,
                baseline.FileId,
                local,
                baseline,
                remote);
        }

        if (remoteChanged)
        {
            return new ReconcileAction(
                ReconcileActionType.DownloadChanged,
                localPath,
                remotePath,
                baseline.FileId,
                local,
                baseline,
                remote);
        }

        return ReconcileAction.NoOp(localPath, remotePath, baseline.FileId, local, baseline, remote);
    }

    private static SyncJournalRecord CreateBaselineFromPlaceholder(
        string localPath,
        string remotePath,
        PlaceholderInfo local,
        RemoteItem remote) => new()
    {
        FileId = !string.IsNullOrWhiteSpace(local.FileId)
            ? local.FileId
            : SyncIdentity.RemotePathFallbackId(remote.RemotePath),
        LocalPath = localPath,
        RemotePath = remotePath,
        ETag = local.ETag,
        MTimeUtc = local.MTimeUtc.ToUniversalTime(),
        Size = local.LogicalSize,
        Checksum = null,
        PinState = null,
        IsDirectory = local.IsDirectory,
        BaseETag = local.ETag,
        LocalPendingOp = null
    };

    private void SeedJournalBaseline(SyncJournalRecord baseline, bool localChanged)
    {
        baseline.InSync = !localChanged;
        baseline.LocalPendingOp = null;
        _journal.Upsert(baseline);
    }
}
