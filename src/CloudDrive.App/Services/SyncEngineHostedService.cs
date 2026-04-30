using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Localization;
using CloudDrive.Core.Services;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.WebDav;
using CloudDrive.Core.Infrastructure;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudDrive.App.Services;

public class SyncEngineHostedService : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SyncEngineHostedService> _logger;
    private readonly IActivityTracker? _activityTracker;
    private SyncCoordinator? _coordinator;
    private IWebDavService? _webDav;
    private MountStateMachine? _stateMachine;
    private ISyncProblemService? _problemService;
    private WindowsNotificationService? _windowsNotificationService;
    private EventWaitHandle? _appRunningEvent;
    private string? _syncRootPath;

    public SyncCoordinator? Coordinator => _coordinator;
    public IWebDavService? WebDav => _webDav;
    public MountStateMachine? StateMachine => _stateMachine;
    public event Action<SyncState>? SyncStateChanged;
    public event Action<string>? ConnectionFailed;
    public event Action<SyncProblem>? ProblemReported;

    public SyncEngineHostedService(ILoggerFactory loggerFactory, IActivityTracker? activityTracker = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SyncEngineHostedService>();
        _activityTracker = activityTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncEngineHostedService starting");

        var settings = AppSettings.Load();
        _syncRootPath = settings.SyncRootPath;
        SignalAppPresence();

        var configurationStatus = settings.GetAccountConfigurationStatus(CredentialManager.HasPassword());
        if (!configurationStatus.IsComplete)
        {
            _logger.LogWarning(
                "Account configuration incomplete. Waiting for configuration. WebDavUrlValid={HasValidWebDavUrl}, UsernameConfigured={HasUsername}, PasswordConfigured={HasPassword}",
                configurationStatus.HasValidWebDavUrl,
                configurationStatus.HasUsername,
                configurationStatus.HasPassword);
            SyncStateChanged?.Invoke(SyncState.Disconnected);
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        var password = CredentialManager.LoadPassword();
        if (password == null)
        {
            _logger.LogWarning("Stored credentials could not be loaded. Waiting for configuration.");
            SyncStateChanged?.Invoke(SyncState.Disconnected);
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        // Create MountStateMachine
        _stateMachine = new MountStateMachine(_loggerFactory.CreateLogger<MountStateMachine>());

        // Create WebDAV client
        var handler = WebDavAuthHandler.CreateHandler(settings, password);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var webDav = new WebDavService(
            httpClient, settings.WebDavUrl,
            _loggerFactory.CreateLogger<WebDavService>(),
            _activityTracker);
        _webDav = webDav;

        // Create coordinator (but don't register/connect yet — wait for readiness gate)
        _coordinator = new SyncCoordinator(settings, webDav, _loggerFactory);
        _problemService = _coordinator.Problems;
        _problemService.ProblemReported += problem => ProblemReported?.Invoke(problem);
        _windowsNotificationService = new WindowsNotificationService(
            _problemService,
            _loggerFactory.CreateLogger<WindowsNotificationService>(),
            appUserModelId: "SelbstlaeuferGmbH.CloudDrive",
            enabled: settings.ShowNotifications);
        _coordinator.SetStateMachine(_stateMachine);

        _coordinator.StateChanged += state => SyncStateChanged?.Invoke(state);

        try
        {
            // === Pre-phase: Pending reset cleanup from previous session ===
            var pending = PendingCleanup.Load();
            if (pending != null)
            {
                _logger.LogInformation("Found pending cleanup marker for {Path}", pending.FolderPath);
                if (Directory.Exists(pending.FolderPath))
                {
                    try
                    {
                        Directory.Delete(pending.FolderPath, recursive: true);
                        _logger.LogInformation("Startup cleanup succeeded: deleted {Path}", pending.FolderPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Startup cleanup failed — Watchdog will retry");
                    }
                }
                PendingCleanup.Remove();
            }

            // Clear any stale watchdog status ("app not running") from previous session
            try { SyncRootConnector.ClearSyncStatus(settings.SyncRootPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "ClearSyncStatus on startup failed (sync root may not exist yet)"); }

            // === Phase 1: Stale Cleanup ===
            await RunStaleCleanupAsync(settings, stoppingToken);

            // === Phase 2: Readiness Gate ===
            await RunReadinessGateAsync(webDav, settings, stoppingToken);

            // === Phase 3: Register and Connect ===
            _stateMachine.TransitionTo(MountPhase.Registering);
            LogActivity(AppLocalizer.Instance.GetString("Activity_FolderRegisteredInExplorer"));

            await _coordinator.RegisterAndConnectAsync(stoppingToken);
            LogActivity(AppLocalizer.Instance.GetString("Activity_CallbacksReady"));

            _stateMachine.TransitionTo(MountPhase.Ready);
            LogActivity(AppLocalizer.Instance.GetString("Activity_CloudDriveReady"));

            // === Phase 4: Start network operations ===
            _coordinator.StartNetworkOperations();

            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — transition handled below
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync engine failed to start");
            SyncStateChanged?.Invoke(SyncState.Error);
        }
        finally
        {
            // === Shutdown ===
            await RunShutdownAsync();
        }
    }

    private async Task RunStaleCleanupAsync(AppSettings settings, CancellationToken ct)
    {
        _stateMachine!.TransitionTo(MountPhase.CheckingStale);
        LogActivity(AppLocalizer.Instance.GetString("Activity_CheckingStaleRegistrations"));

        var allRegistrations = _coordinator!.Registrar.GetStaleRegistrations();

        // Filter out our own registration — it's expected to persist between sessions
        var currentSyncRootId = SyncRootRegistrar.GetSyncRootId(_coordinator.AccountId!);
        var orphanedShellRegistrations = _coordinator.Registrar.GetOrphanedShellNamespaceRegistrations(
            currentSyncRootId,
            settings.SyncRootPath);
        var redundantCurrentShellRegistrations = _coordinator.Registrar.GetRedundantCurrentShellNamespaceRegistrations(
            currentSyncRootId,
            settings.SyncRootPath);
        var shellRegistrationsToCleanup = orphanedShellRegistrations
            .Concat(redundantCurrentShellRegistrations)
            .DistinctBy(registration => registration.Clsid)
            .ToList();
        var staleRegistrations = allRegistrations
            .Where(r => !string.Equals(r.SyncRootId, currentSyncRootId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (staleRegistrations.Count == 0 && shellRegistrationsToCleanup.Count == 0)
        {
            var ownExists = allRegistrations.Any(r =>
                string.Equals(r.SyncRootId, currentSyncRootId, StringComparison.OrdinalIgnoreCase));
            LogActivity(ownExists
                ? AppLocalizer.Instance.GetString("Activity_ExistingSyncRootFound")
                : AppLocalizer.Instance.GetString("Activity_NoStaleRegistrations"));
            return;
        }

        _stateMachine.TransitionTo(MountPhase.CleaningStale);

        foreach (var stale in staleRegistrations)
        {
            LogActivity(AppLocalizer.Instance.GetString("Activity_FoundStaleRegistration"));
            _logger.LogInformation("Stale registration: {Id} at {Path} (status=0x{Status:X8})",
                stale.SyncRootId, stale.SyncRootPath, stale.ProviderStatus);

            var success = await _coordinator.Registrar.UnregisterWithRetry(stale.SyncRootId, ct);

            if (success)
            {
                // Clear any stale error status on the path
                try { SyncRootConnector.ClearSyncStatus(stale.SyncRootPath); }
                catch (Exception ex) { _logger.LogDebug(ex, "ClearSyncStatus failed for stale path"); }

                LogActivity(AppLocalizer.Instance.GetString("Activity_CleanedStaleFolder"));
            }
            else
            {
                _stateMachine.TransitionTo(MountPhase.StaleCleanupFailed);
                LogActivity(AppLocalizer.Instance.GetString("Activity_StaleCleanupFailed"));
                _stateMachine.TransitionTo(MountPhase.Error);
                throw new InvalidOperationException("Stale sync root cleanup failed");
            }
        }

        if (shellRegistrationsToCleanup.Count > 0)
        {
            foreach (var orphaned in shellRegistrationsToCleanup)
            {
                LogActivity(AppLocalizer.Instance.GetString("Activity_FoundStaleRegistration"));
                _logger.LogInformation(
                    "Explorer shell registration scheduled for cleanup: {Identifier} at {Path} (CLSID={Clsid})",
                    orphaned.Identifier,
                    orphaned.TargetFolderPath,
                    orphaned.Clsid);
            }

            var cleanedCount = _coordinator.Registrar.CleanupShellNamespaceRegistrations(
                shellRegistrationsToCleanup,
                ct);

            if (cleanedCount > 0)
                LogActivity(AppLocalizer.Instance.GetString("Activity_CleanedStaleFolder"));
        }
    }

    private async Task RunReadinessGateAsync(IWebDavService webDav, AppSettings settings, CancellationToken ct)
    {
        // === Verify WebDAV connection ===
        _stateMachine!.TransitionTo(MountPhase.VerifyingConnection);
        LogActivity(AppLocalizer.Instance.GetString("Activity_VerifyingServerConnection"));

        var sw = Stopwatch.StartNew();
        if (await webDav.TestConnectionAsync(ct))
        {
            sw.Stop();
            LogActivity(AppLocalizer.Instance.Format("Activity_ServerConnectionVerifiedMilliseconds", sw.ElapsedMilliseconds));
            _problemService?.ResolveByDedupeKey(SyncProblemKeys.Connection(settings.WebDavUrl));
        }
        else
        {
            // Connection failed — enter waiting loop
            _stateMachine.TransitionTo(MountPhase.WaitingForServer);
            LogActivity(AppLocalizer.Instance.GetString("Activity_ServerConnectionWaiting"));
            _problemService?.Report(new SyncProblem
            {
                DedupeKey = SyncProblemKeys.Connection(settings.WebDavUrl),
                ProblemType = SyncProblemType.Connection,
                Severity = SyncProblemSeverity.Error,
                Title = AppLocalizer.Instance.GetString("Problem_CannotReachServer_Title"),
                Summary = AppLocalizer.Instance.GetString("Problem_CannotReachServer_Summary"),
                Details = AppLocalizer.Instance.Format("Problem_CannotReachServer_Detail", settings.WebDavUrl),
                RemotePath = settings.WebDavUrl,
                FirstOccurredAt = DateTime.UtcNow,
                LastOccurredAt = DateTime.UtcNow
            });
            ConnectionFailed?.Invoke(settings.WebDavUrl);

            var waitStart = Stopwatch.StartNew();
            var lastLogTime = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { throw; }

                // Log status update every 10 seconds of the wait
                if (lastLogTime.Elapsed.TotalSeconds >= 10)
                {
                    LogActivity(AppLocalizer.Instance.Format("Activity_WaitingForServerElapsed", waitStart.Elapsed.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.CurrentCulture)));
                    lastLogTime.Restart();
                }

                _stateMachine.TransitionTo(MountPhase.VerifyingConnection);

                if (await webDav.TestConnectionAsync(ct))
                {
                    LogActivity(AppLocalizer.Instance.Format("Activity_ServerConnectionVerifiedElapsed", waitStart.Elapsed.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.CurrentCulture)));
                    _problemService?.ResolveByDedupeKey(SyncProblemKeys.Connection(settings.WebDavUrl));
                    break;
                }

                _stateMachine.TransitionTo(MountPhase.WaitingForServer);
                _logger.LogDebug("WebDAV server still unreachable, retrying in 30s...");
            }

            ct.ThrowIfCancellationRequested();
        }

        // === Verify directory listing ===
        _stateMachine.TransitionTo(MountPhase.VerifyingListing);
        LogActivity(AppLocalizer.Instance.GetString("Activity_VerifyingRemoteListing"));

        try
        {
            var items = await webDav.ListDirectoryAsync("/", ct);
            LogActivity(AppLocalizer.Instance.Format("Activity_RemoteListingVerified", items.Count));
            _problemService?.ResolveByDedupeKey(SyncProblemKeys.RemoteListing("/"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote directory listing failed");
            LogActivity(AppLocalizer.Instance.Format("Activity_RemoteListingFailed", ex.Message));
            _problemService?.Report(new SyncProblem
            {
                DedupeKey = SyncProblemKeys.RemoteListing("/"),
                ProblemType = SyncProblemType.RemoteListing,
                Severity = SyncProblemSeverity.Error,
                Title = AppLocalizer.Instance.GetString("Problem_RemoteFolder_Title"),
                Summary = AppLocalizer.Instance.GetString("Problem_RemoteFolder_Summary"),
                Details = ex.Message,
                RemotePath = "/",
                FirstOccurredAt = DateTime.UtcNow,
                LastOccurredAt = DateTime.UtcNow
            });

            // Fall back to waiting for server
            _stateMachine.TransitionTo(MountPhase.WaitingForServer);
            LogActivity(AppLocalizer.Instance.GetString("Activity_ServerConnectionWaiting"));

            // Retry the full readiness gate
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { throw; }

            await RunReadinessGateAsync(webDav, settings, ct);
        }
    }

    private async Task RunShutdownAsync()
    {
        if (_stateMachine != null &&
            _stateMachine.CurrentPhase != MountPhase.Error &&
            _stateMachine.CurrentPhase != MountPhase.Stopped)
        {
            try
            {
                _stateMachine.TransitionTo(MountPhase.ShuttingDown);
                LogActivity(AppLocalizer.Instance.GetString("Activity_ShuttingDown"));
            }
            catch (InvalidOperationException)
            {
                // Already in a terminal state
            }
        }

        if (_coordinator != null)
        {
            await _coordinator.StopAsync();
            _coordinator.Dispose();
        }
        (_webDav as IDisposable)?.Dispose();
        _windowsNotificationService?.Dispose();
        _windowsNotificationService = null;

        // Clear stale root status from older versions before releasing the presence handle.
        if (_syncRootPath != null)
        {
            try { SyncRootConnector.ClearSyncStatus(_syncRootPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clear sync status on shutdown"); }
        }

        // Dispose app presence handle so watchdog detects app is gone
        _appRunningEvent?.Dispose();
        _appRunningEvent = null;

        if (_stateMachine != null &&
            _stateMachine.CurrentPhase == MountPhase.ShuttingDown)
        {
            try
            {
                _stateMachine.TransitionTo(MountPhase.Stopped);
                LogActivity(AppLocalizer.Instance.GetString("Activity_ShutDownRegistered"));
            }
            catch (InvalidOperationException)
            {
                // Already transitioned
            }
        }
    }

    private void SignalAppPresence()
    {
        if (_appRunningEvent != null)
            return;

        try
        {
            var userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
            {
                _logger.LogDebug("Skipping watchdog presence signal because no user SID is available");
                return;
            }

            _appRunningEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                WatchdogAppPresence.GetEventName(userSid));
            _appRunningEvent.Set();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to signal watchdog app presence");
        }
    }

    private void LogActivity(string message)
    {
        _activityTracker?.Record(ActivityCategory.MountLifecycle, message, ActivityStatus.Success);
    }
}
