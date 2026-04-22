using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncEngine;

public class SyncCoordinator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SyncRootRegistrar _registrar;
    private readonly SyncRootConnector _connector;
    private readonly PlaceholderManager _placeholderManager;
    private readonly HydrationHandler _hydrationHandler;
    private readonly DehydrationHandler _dehydrationHandler;
    private readonly NotificationHandler _notificationHandler;
    private readonly UploadManager _uploadManager;
    private readonly RemoteChangeDetector _remoteChangeDetector;
    private readonly ISyncItemStateService _stateService;
    private readonly ISyncProjectionService _projectionService;
    private readonly ConflictResolver _conflictResolver;
    private readonly ISyncProblemService _problemService;
    private LocalChangeWatcher? _changeWatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SyncStateDb _db;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly RetryPolicy _retryPolicy;
    private readonly IWebDavService _webDav;
    private readonly ExplorerWindowMonitor _explorerWindowMonitor;
    private readonly ActiveCloudRequestTracker _activeCloudRequestTracker;

    private CancellationTokenSource? _cts;
    private Task? _uploadTask;
    private Task? _pollingTask;
    private bool _isPaused;
    private string? _accountId;
    private bool? _lastExplorerWindowOpen;
    private int? _lastPollingIntervalSeconds;
    private int? _lastActiveFetchDataCount;

    // Mount resilience: state machine and connection tracking
    private MountStateMachine? _stateMachine;
    private ConnectionState _connectionState = ConnectionState.Initial();
    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;
    private ExplorerStatusManager? _explorerStatusManager;
    private readonly SemaphoreSlim _remoteScanGate = new(1, 1);

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public bool IsPaused => _isPaused;

    /// <summary>Tracks the last emitted sync state for external queries.</summary>
    public SyncState CurrentState { get; private set; } = SyncState.Disconnected;

    /// <summary>Exposes the internal sync state database for test assertions.</summary>
    public SyncStateDb Db => _db;

    /// <summary>Exposes the registrar for stale cleanup operations.</summary>
    public SyncRootRegistrar Registrar => _registrar;

    /// <summary>Exposes the connector for status signaling.</summary>
    public SyncRootConnector Connector => _connector;

    /// <summary>Exposes the problem service for UI and host-level reporting.</summary>
    public ISyncProblemService Problems => _problemService;

    /// <summary>Exposes the WebDAV service for readiness verification.</summary>
    public IWebDavService WebDav => _webDav;

    public string? AccountId => _accountId;

    public event Action<SyncState>? StateChanged;

    public SyncCoordinator(
        AppSettings settings,
        IWebDavService webDav,
        ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _webDav = webDav;
        _logger = loggerFactory.CreateLogger<SyncCoordinator>();

        var dataDir = settings.DataDirectory ?? AppSettings.GetDataDirectory();
        var dbPath = Path.Combine(dataDir, "syncstate.db");
        _db = new SyncStateDb(dbPath, loggerFactory.CreateLogger<SyncStateDb>());
        _stateService = new SyncItemStateService(_db);
        _problemService = new SyncProblemService(_db, loggerFactory.CreateLogger<SyncProblemService>());

        var pathMapper = new PathMapper(settings.SyncRootPath, "/");
        var cloudFileOperations = new CloudFileOperations();
        _retryPolicy = new RetryPolicy(5, _logger);
        _activeCloudRequestTracker = new ActiveCloudRequestTracker();

        _registrar = new SyncRootRegistrar(loggerFactory.CreateLogger<SyncRootRegistrar>());
        _connector = new SyncRootConnector(loggerFactory.CreateLogger<SyncRootConnector>(), _activeCloudRequestTracker);
        _placeholderManager = new PlaceholderManager(webDav, _stateService, pathMapper, loggerFactory.CreateLogger<PlaceholderManager>());
        _projectionService = new SyncProjectionService(_placeholderManager, _stateService, cloudFileOperations, loggerFactory.CreateLogger<SyncProjectionService>());
        _hydrationHandler = new HydrationHandler(webDav, _stateService, pathMapper, _projectionService, loggerFactory.CreateLogger<HydrationHandler>(), _activeCloudRequestTracker);
        _dehydrationHandler = new DehydrationHandler(
            _stateService,
            pathMapper,
            loggerFactory.CreateLogger<DehydrationHandler>(),
            cloudFileOperations,
            _projectionService);
        _notificationHandler = new NotificationHandler(_db, pathMapper, loggerFactory.CreateLogger<NotificationHandler>());
        _conflictResolver = new ConflictResolver(
            webDav,
            _stateService,
            _projectionService,
            _problemService,
            loggerFactory.CreateLogger<ConflictResolver>());
        _uploadManager = new UploadManager(
            webDav,
            _stateService,
            pathMapper,
            _projectionService,
            settings.MaxConcurrentTransfers,
            loggerFactory.CreateLogger<UploadManager>(),
            _conflictResolver,
            _problemService);
        _remoteChangeDetector = new RemoteChangeDetector(
            webDav,
            _stateService,
            pathMapper,
            _projectionService,
            loggerFactory.CreateLogger<RemoteChangeDetector>(),
            _activeCloudRequestTracker,
            settings.EnableFileLogging);
        _explorerWindowMonitor = new ExplorerWindowMonitor(settings.SyncRootPath, loggerFactory.CreateLogger<ExplorerWindowMonitor>());
        _loggerFactory = loggerFactory;

        _accountId = SyncRootAccountIdResolver.Resolve(_settings);

        // Wire up cfapi callbacks
        _connector.FetchPlaceholdersRequested += _placeholderManager.HandleFetchPlaceholdersAsync;
        _connector.FetchDataRequested += _hydrationHandler.HandleFetchDataAsync;
        _connector.CancelFetchDataRequested += _hydrationHandler.HandleCancelFetchDataAsync;
        _connector.NotifyDehydrateRequested += _dehydrationHandler.HandleNotifyDehydrateAsync;
        _connector.NotifyDeleteRequested += _notificationHandler.HandleNotifyDeleteAsync;
        _connector.NotifyRenameRequested += _notificationHandler.HandleNotifyRenameAsync;
    }

    /// <summary>
    /// Sets the mount state machine for lifecycle coordination.
    /// </summary>
    public void SetStateMachine(MountStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    /// <summary>
    /// Sets the ExplorerStatusManager for visual state updates in Windows Explorer.
    /// </summary>
    public void SetExplorerStatusManager(ExplorerStatusManager manager)
    {
        _explorerStatusManager = manager;
    }

    /// <summary>
    /// Register sync root and connect cfapi callbacks.
    /// Called only after readiness gate is fully verified (stale cleanup + connection + listing).
    /// </summary>
    public async Task RegisterAndConnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("Registering and connecting sync root");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Register sync root (idempotent — cleans up any leftover registration first)
        await _registrar.RegisterAsync(_settings.SyncRootPath, _accountId!);

        // Connect cfapi callbacks
        _connector.Connect(_settings.SyncRootPath);

        // Share the connection key
        var key = _connector.ConnectionKey;
        _placeholderManager.SetConnectionKey(key);
        _hydrationHandler.SetConnectionKey(key);
        _dehydrationHandler.SetConnectionKey(key);
        _notificationHandler.SetConnectionKey(key);

        // Initialize connection state and update Explorer visual state
        _connectionState = ConnectionState.Connected();
        _ = _explorerStatusManager?.SetStateAsync(
            ExplorerVisualState.Connected, _settings.SyncRootPath, _connector);

        CurrentState = SyncState.Syncing;
        StateChanged?.Invoke(SyncState.Syncing);
        _logger.LogInformation("Sync root registered and connected");
    }

    /// <summary>
    /// Start network-dependent operations (local watcher, uploads, remote polling).
    /// Call after RegisterAndConnectAsync.
    /// </summary>
    public void StartNetworkOperations()
    {
        _logger.LogInformation("Starting network operations");

        // Mark handlers as ready for network calls
        _hydrationHandler.SetWebDavReady(true);
        _placeholderManager.SetWebDavReady(true);

        // Create and start local change watcher
        _changeWatcher = new LocalChangeWatcher(_settings.SyncRootPath, _loggerFactory.CreateLogger<LocalChangeWatcher>());
        _projectionService.SetLocalChangeWatcher(_changeWatcher);
        _changeWatcher.Start();

        // Start upload processor
        _uploadTask = Task.Run(() => ProcessUploadsAsync(_cts!.Token), _cts!.Token);

        // Start remote change polling
        _pollingTask = Task.Run(() => PollRemoteChangesAsync(_cts!.Token), _cts!.Token);

        SchedulePlaceholderStateResume();

        CurrentState = SyncState.Synced;
        StateChanged?.Invoke(SyncState.Synced);
        _logger.LogInformation("Sync coordinator fully started");
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping sync coordinator");

        // Cancel reconnect loop if running
        _reconnectCts?.Cancel();

        _cts?.Cancel();
        _changeWatcher?.Stop();

        try
        {
            if (_uploadTask != null)
                await _uploadTask;
            if (_pollingTask != null)
                await _pollingTask;
            if (_reconnectTask != null)
                await _reconnectTask;
        }
        catch (OperationCanceledException) { }

        // Signal shutdown to Windows before disconnecting
        _connector.UpdateSyncProviderStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_DISCONNECTED);

        _connector.Disconnect();

        CurrentState = SyncState.Disconnected;
        StateChanged?.Invoke(SyncState.Disconnected);
        _logger.LogInformation("Sync coordinator stopped");
    }

    public void Pause()
    {
        _isPaused = true;
        _changeWatcher?.Stop();
        CurrentState = SyncState.Paused;
        StateChanged?.Invoke(SyncState.Paused);
        _logger.LogInformation("Sync paused");
    }

    public void Resume()
    {
        _isPaused = false;
        _changeWatcher?.Start();
        SchedulePlaceholderStateResume();
        CurrentState = SyncState.Synced;
        StateChanged?.Invoke(SyncState.Synced);
        _logger.LogInformation("Sync resumed");
    }

    public async Task RunManualSyncAsync(CancellationToken ct = default)
    {
        if (_cts == null || _cts.IsCancellationRequested)
            return;

        if (_isPaused)
        {
            Resume();
        }

        await ExecuteRemoteSyncPassAsync(ct);
    }

    /// <summary>
    /// Records a WebDAV operation failure. When 3 consecutive failures occur,
    /// signals connection loss to cfapi and starts the reconnection loop.
    /// </summary>
    public void RecordConnectionFailure()
    {
        _connectionState = _connectionState.RecordFailure();

        if (_connectionState.IsConnectionLost && _stateMachine?.CurrentPhase == MountPhase.Ready)
        {
            var localizer = AppLocalizer.Instance;
            _logger.LogWarning("Connection lost after {Failures} consecutive failures", _connectionState.ConsecutiveFailures);
            _problemService.Report(new SyncProblem
            {
                DedupeKey = SyncProblemKeys.Connection(_settings.WebDavUrl),
                ProblemType = SyncProblemType.Connection,
                Severity = SyncProblemSeverity.Error,
                Title = localizer.GetString("Problem_ConnectionLost_Title"),
                Summary = localizer.GetString("Problem_ConnectionLost_Summary"),
                Details = localizer.Format("Problem_ConnectionLost_Detail", _connectionState.ConsecutiveFailures),
                RemotePath = _settings.WebDavUrl,
                FirstOccurredAt = DateTime.UtcNow,
                LastOccurredAt = DateTime.UtcNow
            });

            // Update Explorer visual state to Disconnected (Layers 1+2+3)
            _ = _explorerStatusManager?.SetStateAsync(
                ExplorerVisualState.Disconnected, _settings.SyncRootPath, _connector);

            _stateMachine.TransitionTo(MountPhase.ConnectionLost);

            // Mark handlers as not ready — they'll return NETWORK_UNAVAILABLE for file operations
            _hydrationHandler.SetWebDavReady(false);
            _placeholderManager.SetWebDavReady(false);

            // Start reconnection loop
            _reconnectCts = new CancellationTokenSource();
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }
    }

    /// <summary>
    /// Records a WebDAV operation success, resetting the failure counter.
    /// </summary>
    public void RecordConnectionSuccess()
    {
        _connectionState = _connectionState.RecordSuccess();
        _problemService.ResolveByDedupeKey(SyncProblemKeys.Connection(_settings.WebDavUrl));
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        int[] delays = [5000, 10000, 20000, 40000, 60000];
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var delay = delays[Math.Min(attempt, delays.Length - 1)];
            var elapsed = _connectionState.DisconnectedDuration;

            _logger.LogInformation("Reconnection attempt {Attempt} — waiting {Delay}s (disconnected {Elapsed})",
                attempt + 1, delay / 1000, elapsed?.ToString(@"m\m\ s\s") ?? "?");

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                if (await _webDav.TestConnectionAsync(ct))
                {
                    _logger.LogInformation("Connection restored — resuming sync");

                    _connectionState = _connectionState.RecordSuccess();

                    // Update Explorer visual state to Connected (Layers 1+2+3)
                    _ = _explorerStatusManager?.SetStateAsync(
                        ExplorerVisualState.Connected, _settings.SyncRootPath, _connector);

                    _hydrationHandler.SetWebDavReady(true);
                    _placeholderManager.SetWebDavReady(true);
                    SchedulePlaceholderStateResume();

                    _stateMachine?.TransitionTo(MountPhase.Ready);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reconnection attempt {Attempt} failed", attempt + 1);
            }

            attempt++;
        }
    }

    private async Task ProcessUploadsAsync(CancellationToken ct)
    {
        await foreach (var change in _changeWatcher!.Changes.ReadAllAsync(ct))
        {
            if (_isPaused) continue;

            if (change.ChangeType == FileChangeType.PinRequested)
            {
                try
                {
                    await _hydrationHandler.TriggerPinnedHydrationAsync(change.FullPath, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pinned placeholder hydration request failed: {Path}", change.FullPath);
                }

                continue;
            }

            if (change.ChangeType == FileChangeType.DehydrateRequested)
            {
                try
                {
                    _dehydrationHandler.TryDehydrateUnpinnedPlaceholder(change.FullPath, "watcher attribute change");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unpinned placeholder dehydration request failed: {Path}", change.FullPath);
                }

                continue;
            }

            // Skip uploads during connection loss
            if (_stateMachine?.CurrentPhase == MountPhase.ConnectionLost)
                continue;

            try
            {
                await _retryPolicy.ExecuteAsync(
                    () => _uploadManager.ProcessChangeAsync(change, ct),
                    $"Upload {change.FullPath}", ct);

                RecordConnectionSuccess();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload failed after retries: {Path}", change.FullPath);
                RecordConnectionFailure();

                if (!_connectionState.IsConnectionLost)
                {
                    CurrentState = SyncState.Error;
                    StateChanged?.Invoke(SyncState.Error);
                }
            }
        }
    }

    private async Task PollRemoteChangesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_isPaused || _stateMachine?.CurrentPhase == MountPhase.ConnectionLost)
                {
                    await Task.Delay(GetPollingDelay(), ct);
                    continue;
                }

                var activeFetchDataCount = _activeCloudRequestTracker.GetActiveCount(ActiveCloudRequestKind.FetchData);
                if (activeFetchDataCount > 0)
                {
                    if (_lastActiveFetchDataCount != activeFetchDataCount)
                    {
                        _logger.LogInformation(
                            "Deferring remote scan while {ActiveFetchDataCount} FETCH_DATA request(s) are active",
                            activeFetchDataCount);
                        _lastActiveFetchDataCount = activeFetchDataCount;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                if (_lastActiveFetchDataCount.HasValue)
                {
                    _logger.LogInformation("Resuming remote scans after FETCH_DATA activity completed");
                    _lastActiveFetchDataCount = null;
                }

                CurrentState = SyncState.Syncing;
                StateChanged?.Invoke(SyncState.Syncing);
                await ExecuteRemoteSyncPassAsync(ct);

                // Delay AFTER scan so the first scan runs immediately on startup
                await Task.Delay(GetPollingDelay(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote polling failed");
                _problemService.Report(new SyncProblem
                {
                    DedupeKey = SyncProblemKeys.RemoteSync(),
                    ProblemType = SyncProblemType.RemoteSync,
                    Severity = SyncProblemSeverity.Error,
                    Title = AppLocalizer.Instance.GetString("Problem_RemoteSync_Title"),
                    Summary = AppLocalizer.Instance.GetString("Problem_RemoteSync_Summary"),
                    Details = ex.Message,
                    RemotePath = "/",
                    FirstOccurredAt = DateTime.UtcNow,
                    LastOccurredAt = DateTime.UtcNow
                });
                RecordConnectionFailure();

                if (!_connectionState.IsConnectionLost)
                {
                    CurrentState = SyncState.Error;
                    StateChanged?.Invoke(SyncState.Error);
                }

                // Still delay after error to avoid tight retry loop
                try { await Task.Delay(GetPollingDelay(), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ExecuteRemoteSyncPassAsync(CancellationToken ct)
    {
        await _remoteScanGate.WaitAsync(ct);
        try
        {
            CurrentState = SyncState.Syncing;
            StateChanged?.Invoke(SyncState.Syncing);

            await _remoteChangeDetector.ScanAsync(ct);

            RecordConnectionSuccess();
            _problemService.ResolveByDedupeKey(SyncProblemKeys.RemoteSync());

            CurrentState = SyncState.Synced;
            StateChanged?.Invoke(SyncState.Synced);
        }
        finally
        {
            _remoteScanGate.Release();
        }
    }

    private TimeSpan GetPollingDelay()
    {
        var configuredSeconds = Math.Max(5, GetCurrentSyncInterval());
        var explorerWindowOpen = _explorerWindowMonitor.HasOpenWindowForSyncRoot();
        var effectiveSeconds = explorerWindowOpen
            ? Math.Min(configuredSeconds, 30)
            : configuredSeconds;

        if (_lastExplorerWindowOpen != explorerWindowOpen || _lastPollingIntervalSeconds != effectiveSeconds)
        {
            _logger.LogInformation(
                "Remote polling interval set to {IntervalSeconds}s (configured={ConfiguredSeconds}s, explorerWindowOpen={ExplorerWindowOpen})",
                effectiveSeconds,
                configuredSeconds,
                explorerWindowOpen);

            _lastExplorerWindowOpen = explorerWindowOpen;
            _lastPollingIntervalSeconds = effectiveSeconds;
        }

        return TimeSpan.FromSeconds(effectiveSeconds);
    }

    private int GetCurrentSyncInterval()
    {
        try { return AppSettings.Load().SyncIntervalSeconds; }
        catch { return _settings.SyncIntervalSeconds; }
    }

    private void SchedulePlaceholderStateResume()
    {
        if (_cts == null)
            return;

        _ = Task.Run(() => ResumePlaceholderStateTransitionsAsync(_cts.Token), _cts.Token);
    }

    private async Task ResumePlaceholderStateTransitionsAsync(CancellationToken ct)
    {
        try
        {
            var candidates = _stateService.GetAll()
                .Where(item => !item.IsDirectory)
                .Select(item => item.LocalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                return;

            var hydrationRequestCount = 0;
            var dehydrationRequestCount = 0;
            foreach (var localPath in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(localPath))
                    continue;

                if (await _hydrationHandler.TriggerPinnedHydrationAsync(localPath, ct))
                {
                    hydrationRequestCount++;
                    continue;
                }

                if (_dehydrationHandler.TryDehydrateUnpinnedPlaceholder(localPath, "startup/reconnect reconciliation"))
                    dehydrationRequestCount++;
            }

            if (hydrationRequestCount > 0 || dehydrationRequestCount > 0)
            {
                _logger.LogInformation(
                    "Reconciled placeholder states after startup/reconnect: HydrationRequests={HydrationRequests} DehydrationRequests={DehydrationRequests}",
                    hydrationRequestCount,
                    dehydrationRequestCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile placeholder states after startup/reconnect");
        }
    }

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _cts?.Cancel();
        _changeWatcher?.Dispose();
        _connector.Dispose();
        _db.Dispose();
        _reconnectCts?.Dispose();
        _cts?.Dispose();
    }
}

public enum SyncState
{
    Disconnected,
    Syncing,
    Synced,
    Paused,
    Error
}
