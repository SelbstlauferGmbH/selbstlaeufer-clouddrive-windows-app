using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Localization;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public sealed class WebDavLockCoordinator : IWebDavLockCoordinator, IDisposable
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClosedFileReleaseGrace = TimeSpan.FromMinutes(2);

    private readonly IWebDavService _webDav;
    private readonly PathMapper _pathMapper;
    private readonly ISyncProblemService _problemService;
    private readonly ILogger<WebDavLockCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HeldLock> _locksByRemotePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remotePathByLocalPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _refreshCts = new();
    private readonly Task _refreshTask;
    private WebDavLockSupport _lockSupport;
    private bool _disposed;

    public WebDavLockCoordinator(
        IWebDavService webDav,
        PathMapper pathMapper,
        ISyncProblemService problemService,
        ILogger<WebDavLockCoordinator> logger)
    {
        _lockSupport = WebDavLockSupport.NotChecked();
        _webDav = webDav;
        _pathMapper = pathMapper;
        _problemService = problemService;
        _logger = logger;
        _refreshTask = Task.Run(() => RefreshLoopAsync(_refreshCts.Token));
    }

    public bool IsEnabled => _lockSupport.IsSupported;

    public WebDavLockSupport LockSupport => _lockSupport;

    public void SetLockSupport(WebDavLockSupport lockSupport)
    {
        if (_lockSupport.State == lockSupport.State && _lockSupport.Detail == lockSupport.Detail)
            return;

        _lockSupport = lockSupport;
        _logger.LogInformation(
            "WebDAV server locking support: {State}. {Detail}",
            lockSupport.State,
            lockSupport.Detail);

        if (!lockSupport.IsSupported)
            _ = Task.Run(() => ReleaseAllAsync(CancellationToken.None));
    }

    public async Task HandleFileOpenAsync(string localPath, CancellationToken ct = default)
    {
        if (!ShouldAcquirePersistentLock(localPath))
            return;

        var remotePath = _pathMapper.ToRemotePath(localPath);

        await _gate.WaitAsync(ct);
        try
        {
            if (_locksByRemotePath.TryGetValue(remotePath, out var heldLock))
            {
                heldLock.OpenCount++;
                heldLock.ReleaseAfterUtc = null;
                _remotePathByLocalPath[localPath] = remotePath;
                _logger.LogDebug(
                    "Reusing WebDAV lock for open Office document: {LocalPath} RemotePath={RemotePath} OpenCount={OpenCount}",
                    localPath,
                    remotePath,
                    heldLock.OpenCount);
                return;
            }

            var lockInfo = await _webDav.LockAsync(
                remotePath,
                new WebDavLockRequest(BuildOwner(localPath), LockTimeout),
                ct);

            _locksByRemotePath[remotePath] = new HeldLock(
                localPath,
                remotePath,
                lockInfo.Token,
                lockInfo.ExpiresAtUtc,
                openCount: 1);
            _remotePathByLocalPath[localPath] = remotePath;
            _problemService.ResolveByDedupeKey(SyncProblemKeys.Lock(remotePath));

            _logger.LogInformation(
                "Acquired WebDAV lock for Office document: {LocalPath} RemotePath={RemotePath}",
                localPath,
                remotePath);
        }
        catch (WebDavLockedException ex)
        {
            ReportLockedProblem(localPath, remotePath, ex.Message);
            _logger.LogWarning(ex, "Office document is locked by the WebDAV server: {LocalPath} RemotePath={RemotePath}", localPath, remotePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportLockedProblem(localPath, remotePath, ex.Message);
            _logger.LogWarning(ex, "Failed to acquire WebDAV lock for Office document: {LocalPath} RemotePath={RemotePath}", localPath, remotePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleFileCloseAsync(string localPath, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var remotePath = _remotePathByLocalPath.TryGetValue(localPath, out var trackedRemotePath)
                ? trackedRemotePath
                : _pathMapper.ToRemotePath(localPath);

            if (!_locksByRemotePath.TryGetValue(remotePath, out var heldLock))
                return;

            heldLock.OpenCount = Math.Max(0, heldLock.OpenCount - 1);
            if (heldLock.OpenCount == 0)
            {
                heldLock.ReleaseAfterUtc = DateTimeOffset.UtcNow.Add(ClosedFileReleaseGrace);
                _remotePathByLocalPath.Remove(localPath);
                _logger.LogInformation(
                    "Office document closed; WebDAV lock retained briefly for final upload: {LocalPath} RemotePath={RemotePath}",
                    localPath,
                    remotePath);
                return;
            }

            _logger.LogDebug(
                "Office document handle closed while other handles remain: {LocalPath} RemotePath={RemotePath} OpenCount={OpenCount}",
                localPath,
                remotePath,
                heldLock.OpenCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WebDavWriteLock> AcquireWriteLockAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return WebDavWriteLock.None;

        await _gate.WaitAsync(ct);
        try
        {
            if (_locksByRemotePath.TryGetValue(remotePath, out var heldLock))
            {
                _logger.LogDebug("Using held WebDAV lock for write: {LocalPath} RemotePath={RemotePath}", localPath, remotePath);
                return new WebDavWriteLock(heldLock.Token);
            }

            var lockInfo = await _webDav.LockAsync(
                remotePath,
                new WebDavLockRequest(BuildOwner(localPath), LockTimeout),
                ct);
            _problemService.ResolveByDedupeKey(SyncProblemKeys.Lock(remotePath));

            _logger.LogDebug("Acquired temporary WebDAV lock for write: {LocalPath} RemotePath={RemotePath}", localPath, remotePath);
            return new WebDavWriteLock(
                lockInfo.Token,
                () => UnlockSafelyAsync(remotePath, lockInfo.Token, CancellationToken.None));
        }
        catch (WebDavLockedException ex)
        {
            ReportLockedProblem(localPath, remotePath, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task NotifyUploadSucceededAsync(string localPath, string remotePath, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return;

        HeldLock? lockToRelease = null;
        await _gate.WaitAsync(ct);
        try
        {
            if (_locksByRemotePath.TryGetValue(remotePath, out var heldLock))
            {
                _problemService.ResolveByDedupeKey(SyncProblemKeys.Lock(remotePath));
                if (heldLock.OpenCount == 0 && heldLock.ReleaseAfterUtc.HasValue)
                {
                    lockToRelease = heldLock;
                    RemoveHeldLock(heldLock);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (lockToRelease != null)
            await UnlockSafelyAsync(lockToRelease.RemotePath, lockToRelease.Token, ct);
    }

    public async Task NotifyDeleteSucceededAsync(string remotePath, CancellationToken ct = default)
    {
        HeldLock? lockToRelease = null;
        await _gate.WaitAsync(ct);
        try
        {
            if (_locksByRemotePath.TryGetValue(remotePath, out var heldLock))
            {
                lockToRelease = heldLock;
                RemoveHeldLock(heldLock);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (lockToRelease != null)
            await UnlockSafelyAsync(lockToRelease.RemotePath, lockToRelease.Token, ct);
    }

    public async Task ReleaseAllAsync(CancellationToken ct = default)
    {
        List<HeldLock> locks;
        await _gate.WaitAsync(ct);
        try
        {
            locks = _locksByRemotePath.Values.ToList();
            _locksByRemotePath.Clear();
            _remotePathByLocalPath.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var heldLock in locks)
        {
            ct.ThrowIfCancellationRequested();
            await UnlockSafelyAsync(heldLock.RemotePath, heldLock.Token, ct);
        }
    }

    private bool ShouldAcquirePersistentLock(string localPath)
    {
        if (!IsEnabled ||
            string.IsNullOrWhiteSpace(localPath) ||
            TransientFilePolicy.ShouldIgnoreLocalPath(localPath, Directory.Exists(localPath)) ||
            Directory.Exists(localPath))
        {
            return false;
        }

        return OfficeDocumentPolicy.IsOfficeDocumentPath(localPath);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, ct);
                await RefreshOrReleaseLocksAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebDAV lock refresh loop failed");
            }
        }
    }

    private async Task RefreshOrReleaseLocksAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var refreshCandidates = new List<HeldLock>();
        var releaseCandidates = new List<HeldLock>();

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var heldLock in _locksByRemotePath.Values)
            {
                if (!IsEnabled || (heldLock.OpenCount == 0 && heldLock.ReleaseAfterUtc <= now))
                {
                    releaseCandidates.Add(heldLock);
                    continue;
                }

                if (heldLock.ExpiresAtUtc - now <= RefreshLeadTime)
                    refreshCandidates.Add(heldLock);
            }

            foreach (var heldLock in releaseCandidates)
                RemoveHeldLock(heldLock);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var heldLock in releaseCandidates)
            await UnlockSafelyAsync(heldLock.RemotePath, heldLock.Token, ct);

        foreach (var heldLock in refreshCandidates)
            await RefreshLockSafelyAsync(heldLock, ct);
    }

    private async Task RefreshLockSafelyAsync(HeldLock heldLock, CancellationToken ct)
    {
        try
        {
            var refreshed = await _webDav.RefreshLockAsync(heldLock.RemotePath, heldLock.Token, LockTimeout, ct);
            heldLock.Token = refreshed.Token;
            heldLock.ExpiresAtUtc = refreshed.ExpiresAtUtc;
            _logger.LogDebug("Refreshed WebDAV lock: RemotePath={RemotePath}", heldLock.RemotePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportLockedProblem(heldLock.LocalPath, heldLock.RemotePath, ex.Message);
            _logger.LogWarning(ex, "Failed to refresh WebDAV lock: RemotePath={RemotePath}", heldLock.RemotePath);
        }
    }

    private async Task UnlockSafelyAsync(string remotePath, string token, CancellationToken ct)
    {
        try
        {
            await _webDav.UnlockAsync(remotePath, token, ct);
            _logger.LogInformation("Released WebDAV lock: RemotePath={RemotePath}", remotePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to release WebDAV lock: RemotePath={RemotePath}", remotePath);
        }
    }

    private void RemoveHeldLock(HeldLock heldLock)
    {
        _locksByRemotePath.Remove(heldLock.RemotePath);
        foreach (var localPath in _remotePathByLocalPath
                     .Where(pair => string.Equals(pair.Value, heldLock.RemotePath, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _remotePathByLocalPath.Remove(localPath);
        }
    }

    private void ReportLockedProblem(string localPath, string remotePath, string detail)
    {
        var localizer = AppLocalizer.Instance;
        var fileName = Path.GetFileName(localPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = remotePath;

        _problemService.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Lock(remotePath),
            ProblemType = SyncProblemType.RemoteLock,
            Severity = SyncProblemSeverity.Warning,
            Title = localizer.Format("Problem_RemoteLock_Title", fileName),
            Summary = localizer.GetString("Problem_RemoteLock_Summary"),
            Details = string.IsNullOrWhiteSpace(detail)
                ? localizer.Format("Problem_RemoteLock_Detail", remotePath)
                : detail,
            LocalPath = localPath,
            RemotePath = remotePath,
            FirstOccurredAt = DateTime.UtcNow,
            LastOccurredAt = DateTime.UtcNow
        });
    }

    private static string BuildOwner(string localPath) =>
        $"CloudDrive on {Environment.MachineName} ({Environment.UserName}) - {Path.GetFileName(localPath)}";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshCts.Cancel();
        try { _refreshTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        _refreshCts.Dispose();
        _gate.Dispose();
    }

    private sealed class HeldLock
    {
        public HeldLock(string localPath, string remotePath, string token, DateTimeOffset expiresAtUtc, int openCount)
        {
            LocalPath = localPath;
            RemotePath = remotePath;
            Token = token;
            ExpiresAtUtc = expiresAtUtc;
            OpenCount = openCount;
        }

        public string LocalPath { get; }
        public string RemotePath { get; }
        public string Token { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public int OpenCount { get; set; }
        public DateTimeOffset? ReleaseAfterUtc { get; set; }
    }
}
