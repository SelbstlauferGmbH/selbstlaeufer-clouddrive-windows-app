using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public class HydrationHandler
{
    private const int ChunkSize = 4 * 1024 * 1024; // 4 MB
    private static readonly TimeSpan HydrationTimeout = TimeSpan.FromMinutes(5);

    private readonly IWebDavService _webDav;
    private readonly ISyncItemStateService _stateService;
    private readonly PathMapper _pathMapper;
    private readonly ISyncProjectionService _projectionService;
    private readonly ILogger<HydrationHandler> _logger;
    private readonly ActiveCloudRequestTracker? _requestTracker;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTransfers = new();
    private readonly ConcurrentDictionary<string, DateTime> _explicitHydrationRequests = new(StringComparer.OrdinalIgnoreCase);
    private CF_CONNECTION_KEY _connectionKey;
    private volatile bool _webDavReady;

    private static readonly NTStatus STATUS_SUCCESS = new(0);
    private static readonly NTStatus STATUS_CLOUD_FILE_REQUEST_CANCELED = new(unchecked((int)0xC000CF02));
    private static readonly NTStatus STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE = new(unchecked((int)0xC000CF06));
    private static readonly NTStatus STATUS_CLOUD_FILE_NOT_IN_SYNC = new(unchecked((int)0xC000CF04));
    private static readonly TimeSpan ExplicitHydrationRequestCooldown = TimeSpan.FromSeconds(15);

    public HydrationHandler(
        IWebDavService webDav,
        ISyncItemStateService stateService,
        PathMapper pathMapper,
        ISyncProjectionService projectionService,
        ILogger<HydrationHandler> logger,
        ActiveCloudRequestTracker? requestTracker = null)
    {
        _webDav = webDav;
        _stateService = stateService;
        _pathMapper = pathMapper;
        _projectionService = projectionService;
        _logger = logger;
        _requestTracker = requestTracker;
    }

    public void SetConnectionKey(CF_CONNECTION_KEY key) => _connectionKey = key;
    public void SetWebDavReady(bool ready) => _webDavReady = ready;

    public Task<bool> TriggerPinnedHydrationAsync(string localPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!CloudFilePlaceholderHelper.TryGetPlaceholderState(localPath, out var placeholderState) ||
            placeholderState is null ||
            !placeholderState.ShouldHydratePinnedFile)
        {
            return Task.FromResult(false);
        }

        MarkPendingDownload(localPath, placeholderState);

        if (!_webDavReady)
        {
            _logger.LogInformation("Pinned placeholder hydration deferred until WebDAV is ready: {Path}", localPath);
            return Task.FromResult(false);
        }

        if (_explicitHydrationRequests.TryGetValue(localPath, out var lastRequestUtc) &&
            DateTime.UtcNow - lastRequestUtc < ExplicitHydrationRequestCooldown)
        {
            _logger.LogDebug("Pinned placeholder hydration already requested recently: {Path}", localPath);
            return Task.FromResult(false);
        }

        _explicitHydrationRequests[localPath] = DateTime.UtcNow;

        if (!CloudFilePlaceholderHelper.TryHydratePlaceholder(localPath, _logger))
        {
            _explicitHydrationRequests.TryRemove(localPath, out _);
            _stateService.UpdateStatus(localPath, SyncStatus.Error);
            return Task.FromResult(false);
        }

        _logger.LogInformation("Pinned placeholder hydration requested: {Path}", localPath);
        return Task.FromResult(true);
    }

    public async Task HandleFetchDataAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var fullPath = callbackInfo.NormalizedPath;
        var hydrationStopwatch = Stopwatch.StartNew();
        _logger.LogInformation("FETCH_DATA hydrating: {Path}", fullPath);

        var requiredOffset = callbackParameters.FetchData.RequiredFileOffset;
        var requiredLength = callbackParameters.FetchData.RequiredLength;
        var optionalOffset = callbackParameters.FetchData.OptionalFileOffset;
        var optionalLength = callbackParameters.FetchData.OptionalLength;
        var transferRange = HydrationTransferPlanner.CreateTransferRange(
            callbackInfo.FileSize,
            requiredOffset,
            requiredLength,
            optionalOffset,
            optionalLength);
        var transferOffset = transferRange.Offset;
        var effectiveLength = transferRange.Length;

        if (!_webDavReady)
        {
            _logger.LogWarning("FETCH_DATA rejected (WebDAV not ready): {Path}", fullPath);
            TryReportError(callbackInfo, STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE, transferOffset, effectiveLength);
            return;
        }

        _logger.LogInformation(
            "FETCH_DATA request: {Path} RequiredOffset={RequiredOffset} RequiredLength={RequiredLength} OptionalOffset={OptionalOffset} OptionalLength={OptionalLength} TransferOffset={TransferOffset} FileSize={FileSize} EffectiveLength={EffectiveLength}",
            fullPath,
            requiredOffset,
            requiredLength,
            optionalOffset,
            optionalLength,
            transferOffset,
            callbackInfo.FileSize,
            effectiveLength);

        var mappedRemotePath = _pathMapper.NormalizedPathToRemotePath(fullPath);
        var localPath = _pathMapper.ToLocalPath(mappedRemotePath);
        var trackedItem = _stateService.GetByLocalPath(localPath);
        var identityRemotePath = TryGetRemotePathFromFileIdentity(callbackInfo);
        var remotePathCandidates = BuildRemotePathCandidates(
            trackedItem?.RemotePath,
            mappedRemotePath,
            identityRemotePath);
        var remotePath = remotePathCandidates[0];

        if (!string.IsNullOrWhiteSpace(identityRemotePath) &&
            !string.Equals(identityRemotePath, remotePath, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "FETCH_DATA detected stale placeholder file identity candidate: {Path} PreferredRemotePath={PreferredRemotePath} FileIdentityRemotePath={FileIdentityRemotePath} MappedRemotePath={MappedRemotePath}",
                fullPath,
                remotePath,
                identityRemotePath,
                mappedRemotePath);
        }
        else if (!string.Equals(remotePath, mappedRemotePath, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "FETCH_DATA using tracked remote path: {Path} MappedRemotePath={MappedRemotePath} TrackedRemotePath={TrackedRemotePath}",
                fullPath,
                mappedRemotePath,
                remotePath);
        }

        if (remotePathCandidates.Count > 1)
        {
            _logger.LogInformation(
                "FETCH_DATA remote path candidates: {Path} Candidates={Candidates}",
                fullPath,
                string.Join(" | ", remotePathCandidates));
        }

        if (trackedItem?.SyncStatus == SyncStatus.RemoteDeletePendingLocalCleanup)
        {
            _logger.LogWarning(
                "FETCH_DATA revalidating item marked for remote delete cleanup before rejecting: {Path} Candidates={Candidates}",
                fullPath,
                string.Join(" | ", remotePathCandidates));
        }

        if (effectiveLength <= 0)
        {
            _logger.LogInformation(
                "FETCH_DATA resolved to an empty transfer range: {Path} RequiredOffset={RequiredOffset} RequiredLength={RequiredLength} OptionalOffset={OptionalOffset} OptionalLength={OptionalLength} FileSize={FileSize}",
                fullPath,
                requiredOffset,
                requiredLength,
                optionalOffset,
                optionalLength,
                callbackInfo.FileSize);
            return;
        }

        // Signal Windows immediately that we're working on this request.
        // This prevents the cfapi timeout from expiring during WebDAV connection setup.
        var initialProgressStopwatch = Stopwatch.StartNew();
        ReportProgress(callbackInfo, effectiveLength, 0);
        _logger.LogDebug(
            "FETCH_DATA initial provider progress reported: {Path} DurationMs={DurationMs}",
            fullPath,
            initialProgressStopwatch.ElapsedMilliseconds);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(HydrationTimeout);
        _activeTransfers[fullPath] = cts;

        try
        {
            var statusUpdateStopwatch = Stopwatch.StartNew();
            _stateService.UpdateStatus(localPath, SyncStatus.Syncing);
            _logger.LogDebug(
                "FETCH_DATA marked Syncing: {Path} DurationMs={DurationMs}",
                fullPath,
                statusUpdateStopwatch.ElapsedMilliseconds);

            // Use priority download - bypasses rate limiter since hydration is
            // user-initiated and time-critical (cfapi has a ~60s request timeout).
            var openStreamStopwatch = Stopwatch.StartNew();
            _logger.LogInformation("FETCH_DATA opening priority download stream: {Path} RemotePath={RemotePath}", fullPath, remotePath);
            var download = transferOffset == 0 && effectiveLength == callbackInfo.FileSize
                ? await OpenPriorityDownloadStreamAsync(fullPath, localPath, remotePathCandidates, cts.Token)
                : await OpenPriorityDownloadStreamAsync(fullPath, localPath, remotePathCandidates, transferOffset, effectiveLength, cts.Token);
            remotePath = download.RemotePath;
            using var remoteStream = download.Stream;
            trackedItem = SyncTrackedRemotePath(localPath, trackedItem, remotePath, callbackInfo.FileSize);
            _logger.LogInformation(
                "FETCH_DATA priority download stream opened: {Path} DurationMs={DurationMs}",
                fullPath,
                openStreamStopwatch.ElapsedMilliseconds);

            // Report progress after stream is positioned; connection established and data flowing.
            var positionedProgressStopwatch = Stopwatch.StartNew();
            ReportProgress(callbackInfo, effectiveLength, 0);
            _logger.LogDebug(
                "FETCH_DATA positioned provider progress reported: {Path} DurationMs={DurationMs}",
                fullPath,
                positionedProgressStopwatch.ElapsedMilliseconds);

            long totalTransferred = 0;
            var buffer = new byte[ChunkSize];
            long currentOffset = transferOffset;

            while (totalTransferred < effectiveLength)
            {
                cts.Token.ThrowIfCancellationRequested();

                var targetLength = (int)Math.Min(ChunkSize, effectiveLength - totalTransferred);
                var bytesRead = await HydrationTransferPlanner.ReadAtLeastUntilTargetOrEofAsync(
                    remoteStream,
                    buffer.AsMemory(0, targetLength),
                    cts.Token);

                if (bytesRead != targetLength)
                    throw new IOException($"Premature end of stream at {totalTransferred + bytesRead}/{effectiveLength} bytes");

                if (!TransferData(callbackInfo, buffer, currentOffset, bytesRead))
                {
                    // CfExecute failed - request was likely cancelled by Windows.
                    // Don't try to send an error report; the request is already dead.
                    _logger.LogWarning(
                        "Hydration aborted (CfExecute failed): {Path} at {Offset}/{Total}",
                        fullPath,
                        currentOffset,
                        effectiveLength);
                    _stateService.UpdateStatus(localPath, SyncStatus.Error);
                    return;
                }

                currentOffset += bytesRead;
                totalTransferred += bytesRead;

                ReportProgress(callbackInfo, effectiveLength, totalTransferred);
            }

            var item = SyncTrackedRemotePath(localPath, trackedItem, remotePath, callbackInfo.FileSize);
            if (item != null)
            {
                item.SyncStatus = SyncStatus.Synced;
                item.LastSynced = DateTime.UtcNow;
                _stateService.Upsert(item);
            }

            RefreshPlaceholderIdentity(localPath, remotePath, item, callbackInfo.FileSize);

            if (File.Exists(localPath))
            {
                _projectionService.ScheduleMarkInSync(localPath);
            }
            else
            {
                _logger.LogInformation(
                    "Skipping in-sync projection because hydrated path no longer exists locally: {Path}",
                    localPath);
            }
            _logger.LogInformation(
                "TRANSFER_DATA complete: {Path} ({Bytes} bytes, TotalDurationMs={DurationMs})",
                fullPath,
                totalTransferred,
                hydrationStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Hydration cancelled: {Path} TotalDurationMs={DurationMs}",
                fullPath,
                hydrationStopwatch.ElapsedMilliseconds);
            TryReportError(callbackInfo, STATUS_CLOUD_FILE_REQUEST_CANCELED, transferOffset, effectiveLength);
        }
        catch (RemoteFileListedButUnavailableException ex)
        {
            _logger.LogWarning(
                "Hydration download blocked although the file is still listed remotely: {Path} RemotePath={RemotePath} ContentType={ContentType}",
                ex.LocalPath,
                ex.RemotePath,
                ex.ContentType ?? "<unknown>");
            _stateService.UpdateStatus(localPath, SyncStatus.Error);
            TryReportError(callbackInfo, STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE, transferOffset, effectiveLength);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning("File not found on server during hydration: {Path} - {Message}", fullPath, ex.Message);
            var item = _stateService.GetByLocalPath(localPath);
            if (item != null)
            {
                item.SyncStatus = SyncStatus.RemoteDeletePendingLocalCleanup;
                _stateService.Upsert(item);
            }

            TryReportError(callbackInfo, STATUS_CLOUD_FILE_NOT_IN_SYNC, transferOffset, effectiveLength);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hydration failed: {Path} TotalDurationMs={DurationMs}", fullPath, hydrationStopwatch.ElapsedMilliseconds);
            _stateService.UpdateStatus(localPath, SyncStatus.Error);
            TryReportError(callbackInfo, STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE, transferOffset, effectiveLength);
        }
        finally
        {
            _explicitHydrationRequests.TryRemove(localPath, out _);
            _activeTransfers.TryRemove(fullPath, out _);
            _requestTracker?.CompleteFetchData(fullPath, "handler finished");
            cts.Dispose();
        }
    }

    public Task HandleCancelFetchDataAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var fullPath = callbackInfo.NormalizedPath;
        _logger.LogInformation("Cancel hydration requested: {Path}", fullPath);

        if (_activeTransfers.TryGetValue(fullPath, out var cts))
            cts.Cancel();

        return Task.CompletedTask;
    }

    private bool TransferData(CF_CALLBACK_INFO callbackInfo, byte[] data, long offset, int length)
    {
        var pinnedData = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = callbackInfo.ConnectionKey,
                TransferKey = callbackInfo.TransferKey,
                RequestKey = callbackInfo.RequestKey
            };

            var td = new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                CompletionStatus = STATUS_SUCCESS,
                Buffer = pinnedData.AddrOfPinnedObject(),
                Offset = offset,
                Length = length
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(td);

            var hr = CfExecute(opInfo, ref opParams);
            if (hr.Failed)
            {
                _logger.LogWarning(
                    "CfExecute TRANSFER_DATA failed at offset {Offset} length {Length}: {Hr} - request may have been cancelled by Windows",
                    offset,
                    length,
                    hr);
                return false;
            }

            _logger.LogDebug("TRANSFER_DATA chunk OK: Offset={Offset} Length={Length}", offset, length);
            return true;
        }
        finally
        {
            pinnedData.Free();
        }
    }

    private void ReportProgress(CF_CALLBACK_INFO callbackInfo, long total, long completed)
    {
        try
        {
            var hr = CfReportProviderProgress(callbackInfo.ConnectionKey, callbackInfo.TransferKey, total, completed);
            if (hr.Failed)
                _logger.LogDebug("CfReportProviderProgress failed: {Hr} (total={Total}, completed={Completed})", hr, total, completed);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CfReportProviderProgress exception");
        }
    }

    private void TryReportError(CF_CALLBACK_INFO callbackInfo, NTStatus status, long offset, long length)
    {
        try
        {
            ReportError(callbackInfo, status, offset, length);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryReportError suppressed - request likely already dead");
        }
    }

    private void ReportError(CF_CALLBACK_INFO callbackInfo, NTStatus status, long offset, long length)
    {
        var validLength = length > 0 ? length : Math.Max(callbackInfo.FileSize, 0);

        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey
        };

        var td = new CF_OPERATION_PARAMETERS.TRANSFERDATA
        {
            CompletionStatus = status,
            Buffer = IntPtr.Zero,
            Length = validLength,
            Offset = offset
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(td);
        var hr = CfExecute(opInfo, ref opParams);
        if (hr.Failed)
            _logger.LogWarning("CfExecute ReportError failed: {Hr}", hr);
    }

    internal Task<(Stream Stream, string RemotePath)> OpenPriorityDownloadStreamAsync(
        string fullPath,
        string localPath,
        string remotePath,
        CancellationToken ct)
    {
        return OpenPriorityDownloadStreamAsync(fullPath, localPath, [remotePath], 0, null, ct);
    }

    internal Task<(Stream Stream, string RemotePath)> OpenPriorityDownloadStreamAsync(
        string fullPath,
        string localPath,
        IReadOnlyList<string> remotePathCandidates,
        CancellationToken ct)
    {
        return OpenPriorityDownloadStreamAsync(fullPath, localPath, remotePathCandidates, 0, null, ct);
    }

    internal async Task<(Stream Stream, string RemotePath)> OpenPriorityDownloadStreamAsync(
        string fullPath,
        string localPath,
        string remotePath,
        long offset,
        long? length,
        CancellationToken ct)
    {
        return await OpenPriorityDownloadStreamAsync(fullPath, localPath, [remotePath], offset, length, ct);
    }

    internal async Task<(Stream Stream, string RemotePath)> OpenPriorityDownloadStreamAsync(
        string fullPath,
        string localPath,
        IReadOnlyList<string> remotePathCandidates,
        long offset,
        long? length,
        CancellationToken ct)
    {
        var candidates = remotePathCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            throw new FileNotFoundException($"Remote file not found for {localPath}");

        FileNotFoundException? lastNotFound = null;

        foreach (var candidate in candidates)
        {
            try
            {
                return (await _webDav.DownloadFilePriorityAsync(candidate, offset, length, ct), candidate);
            }
            catch (FileNotFoundException ex) when (!ct.IsCancellationRequested)
            {
                lastNotFound = ex;
                _logger.LogInformation(
                    "FETCH_DATA download candidate not found: {Path} CandidateRemotePath={CandidateRemotePath}",
                    fullPath,
                    candidate);
            }
        }

        var matchedItem = await ResolveCanonicalRemoteItemFromParentListingAsync(localPath, candidates, ct);
        if (matchedItem == null)
        {
            throw lastNotFound ?? new FileNotFoundException($"Remote file not found for {localPath}");
        }

        if (candidates.Any(candidate => string.Equals(candidate, matchedItem.RemotePath, StringComparison.Ordinal)))
            throw new RemoteFileListedButUnavailableException(localPath, matchedItem.RemotePath, matchedItem.ContentType);

        _logger.LogInformation(
            "FETCH_DATA retrying priority download with canonical remote path: {Path} Candidates={Candidates} NewRemotePath={NewRemotePath}",
            fullPath,
            string.Join(" | ", candidates),
            matchedItem.RemotePath);

        return (await _webDav.DownloadFilePriorityAsync(matchedItem.RemotePath, offset, length, ct), matchedItem.RemotePath);
    }

    internal static string? TryGetRemotePathFromFileIdentity(in CF_CALLBACK_INFO callbackInfo)
    {
        if (callbackInfo.FileIdentity == IntPtr.Zero || callbackInfo.FileIdentityLength == 0)
            return null;

        try
        {
            var length = checked((int)callbackInfo.FileIdentityLength);
            var buffer = new byte[length];
            Marshal.Copy(callbackInfo.FileIdentity, buffer, 0, length);
            var remotePath = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(remotePath) ? null : remotePath;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<string?> ResolveCanonicalRemotePathFromParentListingAsync(
        string localPath,
        string attemptedRemotePath,
        CancellationToken ct)
    {
        return (await ResolveCanonicalRemoteItemFromParentListingAsync(localPath, [attemptedRemotePath], ct))?.RemotePath;
    }

    internal async Task<RemoteItem?> ResolveCanonicalRemoteItemFromParentListingAsync(
        string localPath,
        string attemptedRemotePath,
        CancellationToken ct)
    {
        return await ResolveCanonicalRemoteItemFromParentListingAsync(localPath, [attemptedRemotePath], ct);
    }

    internal async Task<RemoteItem?> ResolveCanonicalRemoteItemFromParentListingAsync(
        string localPath,
        IReadOnlyCollection<string> attemptedRemotePaths,
        CancellationToken ct)
    {
        var attemptedPaths = attemptedRemotePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (attemptedPaths.Count == 0)
        {
            return null;
        }

        var namesToMatch = attemptedPaths
            .Select(GetRemoteName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Append(Path.GetFileName(localPath))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (namesToMatch.Count == 0)
            return null;

        var parentRemotePaths = attemptedPaths
            .Select(GetParentRemotePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "FETCH_DATA revalidating remote path from parent listing: {Path} AttemptedRemotePaths={AttemptedRemotePaths} ParentRemotePaths={ParentRemotePaths} MatchNames={MatchNames}",
            localPath,
            string.Join(" | ", attemptedPaths),
            string.Join(" | ", parentRemotePaths),
            string.Join(" | ", namesToMatch));

        foreach (var parentRemotePath in parentRemotePaths)
        {
            var siblings = await _webDav.ListDirectoryAsync(parentRemotePath, ct);
            var match = siblings.FirstOrDefault(item =>
                namesToMatch.Contains(item.Name, StringComparer.OrdinalIgnoreCase) ||
                namesToMatch.Contains(GetRemoteName(item.RemotePath), StringComparer.OrdinalIgnoreCase));

            if (match == null)
                continue;

            var trackedItem = _stateService.GetByLocalPath(localPath);
            if (trackedItem != null)
            {
                trackedItem.RemotePath = match.RemotePath;
                trackedItem.IsDirectory = match.IsDirectory;
                trackedItem.FileSize = match.Size;
                trackedItem.RemoteETag = match.ETag;
                trackedItem.RemoteLastModified = match.LastModified;
                _stateService.Upsert(trackedItem);
            }

            _logger.LogInformation(
                "FETCH_DATA resolved canonical remote item from parent listing: {Path} AttemptedRemotePaths={AttemptedRemotePaths} CanonicalRemotePath={CanonicalRemotePath} IsDirectory={IsDirectory} ContentType={ContentType}",
                localPath,
                string.Join(" | ", attemptedPaths),
                match.RemotePath,
                match.IsDirectory,
                match.ContentType ?? "<unknown>");

            return match;
        }

        _logger.LogWarning(
            "FETCH_DATA parent listing did not contain a matching item: {Path} AttemptedRemotePaths={AttemptedRemotePaths} MatchNames={MatchNames}",
            localPath,
            string.Join(" | ", attemptedPaths),
            string.Join(" | ", namesToMatch));
        return null;
    }

    internal static string GetParentRemotePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            return "/";
        }

        var trimmed = remotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }

    internal static string GetRemoteName(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
        {
            return string.Empty;
        }

        var trimmed = remotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }

    private void MarkPendingDownload(string localPath, CloudFilePlaceholderState placeholderState)
    {
        var item = _stateService.GetByLocalPath(localPath);
        if (item != null)
        {
            if (item.SyncStatus != SyncStatus.PendingDownload)
            {
                item.SyncStatus = SyncStatus.PendingDownload;
                _stateService.Upsert(item);
            }

            return;
        }

        _stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = _pathMapper.ToRemotePath(localPath),
            IsDirectory = placeholderState.IsDirectory,
            FileSize = placeholderState.FileSize,
            SyncStatus = SyncStatus.PendingDownload
        });
    }

    private static List<string> BuildRemotePathCandidates(params string?[] candidates)
    {
        var ordered = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                ordered.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ordered.Add(candidate);
        }

        return ordered;
    }

    private SyncItem? SyncTrackedRemotePath(string localPath, SyncItem? trackedItem, string remotePath, long fileSize)
    {
        var item = trackedItem ?? _stateService.GetByLocalPath(localPath);
        if (item == null)
            return null;

        var changed = false;
        if (!string.Equals(item.RemotePath, remotePath, StringComparison.Ordinal))
        {
            item.RemotePath = remotePath;
            changed = true;
        }

        if (!item.IsDirectory && fileSize > 0 && item.FileSize != fileSize)
        {
            item.FileSize = fileSize;
            changed = true;
        }

        if (changed)
            _stateService.Upsert(item);

        return item;
    }

    private void RefreshPlaceholderIdentity(string localPath, string remotePath, SyncItem? item, long callbackFileSize)
    {
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
            return;

        var fileSize = item?.IsDirectory == true ? 0 : (item?.FileSize > 0 ? item.FileSize : callbackFileSize);
        var lastModified = item?.RemoteLastModified
            ?? (Directory.Exists(localPath)
                ? Directory.GetLastWriteTimeUtc(localPath)
                : File.GetLastWriteTimeUtc(localPath));

        _projectionService.UpdatePlaceholderMetadata(localPath, fileSize, lastModified, remotePath);
    }
}
