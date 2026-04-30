using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CloudDrive.App.Services;
using CloudDrive.App.Tray;
using CloudDrive.App.ViewModels;
using CloudDrive.App.Views;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.Infrastructure;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.WebDav;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CloudDrive.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayIconManager? _trayIcon;
    private SingleInstanceGuard? _instanceGuard;
    private IActivityTracker? _activityTracker;
    private ActivityPanel? _activityPanel;
    private SettingsWindow? _settingsWindow;
    private SyncStateDb? _fallbackDashboardDb;
    private readonly AppLaunchOptions _launchOptions;
    private ShellCommandServer? _shellCommandServer;
    private bool _folderOpenedOnce;
    private bool _serverWaitNotificationShown;
    private bool _disconnectionNotificationShown;
    private Stopwatch? _serverWaitStopwatch;
    private Stopwatch? _disconnectionStopwatch;
    private readonly DateTime _appStartedAt = DateTime.Now;
    private readonly List<SyncProblem> _remoteDeleteDecisionQueue = [];
    private readonly HashSet<string> _remoteDeleteDecisionQueuedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _remoteDeleteDecisionPromptedKeys = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _remoteDeleteDecisionTimer;
    private bool _remoteDeleteDecisionDialogOpen;
    private int _fatalExceptionHandled;

    public App(AppLaunchOptions? launchOptions = null)
    {
        _launchOptions = launchOptions ?? AppLaunchOptions.Default;
        _folderOpenedOnce = _launchOptions.IsAutoStartLaunch;
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        var localizer = AppLocalizer.Instance;
        localizer.Initialize(settings.Language);

        // Integrity check — verify all critical files against build manifest
        // Runs before everything else so it works even when NuGet DLLs are missing
        var integrityResult = IntegrityChecker.Verify();
        if (!integrityResult.IsValid)
        {
            IntegrityChecker.WriteIntegrityLog(integrityResult);
            System.Windows.MessageBox.Show(
                IntegrityChecker.GetUserFriendlyMessage(integrityResult),
                localizer.GetString("App_IntegrityError_Title"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Suppress Windows error dialog so our crash handlers run first
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

        // Flush logs on any process exit (including native termination by cfapi)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exiting");
            Log.CloseAndFlush();
        };

        // Global exception handlers — always write crash logs regardless of logging setting
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            HandleFatalException(ex, "Unhandled AppDomain exception");
        };

        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            if (TryHandleRecoverableUiException(ex))
            {
                args.Handled = true;
                return;
            }

            HandleFatalException(ex, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Single instance check
        _instanceGuard = new SingleInstanceGuard("CloudDrive");
        if (!_instanceGuard.TryAcquire())
        {
            if (_launchOptions.ShellCommand != null &&
                await ShellCommandClient.TrySendAsync(_launchOptions.ShellCommand, TimeSpan.FromSeconds(2)))
            {
                Shutdown();
                return;
            }

            System.Windows.MessageBox.Show(localizer.GetString("App_AlreadyRunning_Message"), localizer.GetString("App_Name"),
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Configure Serilog
        ThemeManager.Initialize(settings.ThemeMode);
        var logConfig = new LoggerConfiguration().MinimumLevel.Debug();

        if (settings.EnableFileLogging)
        {
            var logPath = Path.Combine(AppSettings.GetDataDirectory(), "logs", "clouddrive-.log");
            logConfig = logConfig.WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024);
        }

        // When CLOUDDRIVE_DEBUG_JSONLOG=1, also write a JSON Lines file that
        // stop-local.ps1 (or run-e2e-local.ps1) can merge with WebDAV server logs.
        if (Environment.GetEnvironmentVariable("CLOUDDRIVE_DEBUG_JSONLOG") == "1")
        {
            var jsonLogDir = Path.Combine(AppSettings.GetDataDirectory(), "logs");
            Directory.CreateDirectory(jsonLogDir);
            logConfig = logConfig.WriteTo.File(
                new Serilog.Formatting.Json.JsonFormatter(),
                Path.Combine(jsonLogDir, "debug-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                fileSizeLimitBytes: 50 * 1024 * 1024);
        }

        Log.Logger = logConfig.CreateLogger();

        if (settings.EnableFileLogging)
            Log.Information("CloudDrive starting — file logging enabled");

        if (!EnsureStartupConfiguration())
        {
            Shutdown();
            return;
        }

        settings = AppSettings.Load();
        RegisterExplorerIntegration(settings);
        localizer.Initialize(settings.Language);
        ThemeManager.ApplyTheme(settings.ThemeMode);

        // Build host
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                // Register ActivityTracker as a singleton
                services.AddSingleton<IActivityTracker, ActivityTracker>();
                services.AddHostedService<SyncEngineHostedService>();
                services.AddSingleton<UpdateService>();
                services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UpdateService>());
            })
            .Build();

        // Setup tray icon
        _trayIcon = new TrayIconManager(settings);

        // Get the activity tracker from the service container
        _activityTracker = _host.Services.GetRequiredService<IActivityTracker>();
        var updateService = _host.Services.GetRequiredService<UpdateService>();
        updateService.UpdateReady += () => Dispatcher.Invoke(() => OnUpdateReady(updateService));

        await EnsureWatchdogScheduledTaskAsync();

        // Wire up tray events
        _trayIcon.SettingsRequested += () => Dispatcher.Invoke(() =>
        {
            Log.Information("UI[Tray] Settings requested");
            OpenSettingsWindow(SettingsSection.General);
        });
        _trayIcon.PauseResumeRequested += () =>
        {
            Log.Information("UI[Tray] Pause/resume requested");
            TogglePauseResume();
        };
        _trayIcon.QuitRequested += () =>
        {
            Log.Information("UI[Tray] Quit requested");
            Shutdown();
        };
        _trayIcon.ActivityPanelRequested += () => Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Log.Information("UI[Tray] Activity flyout toggle requested");
                await ToggleActivityFlyoutAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "UI[Tray] Activity flyout toggle failed");
            }
        });

        // Wire sync state and connection events to tray
        var hostedServices = _host.Services.GetServices<IHostedService>();
        foreach (var svc in hostedServices.OfType<SyncEngineHostedService>())
        {
            svc.SyncStateChanged += state =>
            {
                Dispatcher.Invoke(() => _trayIcon.UpdateState(state));

                if (state == SyncState.Synced && !_folderOpenedOnce)
                {
                    _folderOpenedOnce = true;

                    // We need to run the health check in the background so we don't block the sync engine thread
                    Task.Run(async () =>
                    {
                        var healthCheckResult = await svc.WebDav!.HealthCheckAsync();

                        Dispatcher.Invoke(() =>
                        {
                            if (healthCheckResult.IsHealthy)
                            {
                                Log.Information("Startup health check passed. Latency: {LatencyMs}ms", healthCheckResult.LatencyMs);
                                var path = settings.SyncRootPath;
                                if (Directory.Exists(path))
                                    Process.Start("explorer.exe", path);
                            }
                            else
                            {
                                Log.Warning("Startup health check failed. Reason: {FailureReason}, Latency: {LatencyMs}ms. Error: {ErrorMessage}",
                                    healthCheckResult.FailureReason, healthCheckResult.LatencyMs, healthCheckResult.ErrorMessage);

                                string message = healthCheckResult.FailureReason switch
                                {
                                    HealthCheckFailure.ServerUnreachable => localizer.Format("Tray_Balloon_Startup_ServerUnreachable", settings.WebDavUrl),
                                    HealthCheckFailure.HighLatency => localizer.Format("Tray_Balloon_Startup_HighLatency", healthCheckResult.LatencyMs),
                                    HealthCheckFailure.ListingFailed => localizer.GetString("Tray_Balloon_Startup_ListingFailed"),
                                    _ => localizer.GetString("Tray_Balloon_Startup_Default")
                                };

                                _trayIcon.ShowBalloon(localizer.GetString("App_Name"), message, System.Windows.Forms.ToolTipIcon.Warning);
                            }
                        });
                    });
                }
            };
            svc.ConnectionFailed += url =>
            {
                Dispatcher.Invoke(() => _trayIcon.ShowBalloon(
                    localizer.GetString("App_Name"),
                    localizer.Format("Tray_Balloon_CannotReachServer", url),
                    System.Windows.Forms.ToolTipIcon.Warning));
            };
            svc.ProblemReported += problem =>
            {
                Dispatcher.Invoke(() => QueueRemoteDeleteDecision(problem));
            };

            // Mount lifecycle phase notifications
            WireMountPhaseNotifications(svc);
        }

        // Start host
        await _host.StartAsync();

        StartShellCommandServer();

        if (_launchOptions.ShellCommand != null)
            await HandleShellCommandAsync(_launchOptions.ShellCommand);
    }

    private bool EnsureStartupConfiguration()
    {
        if (AppSettings.Load().HasCompleteAccountConfiguration(CredentialManager.HasPassword()))
            return true;

        using var loggerFactory = LoggerFactory.Create(_ => { });
        var wizard = new ConfigurationWizardWindow(new ConfigurationWizardViewModel(
            loggerFactory,
            remoteTargetResetCallback: CleanupLocalStateForRemoteTargetChangeBeforeStartupAsync));
        return wizard.ShowDialog() == true &&
               AppSettings.Load().HasCompleteAccountConfiguration(CredentialManager.HasPassword());
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        _activityPanel?.ForceClose();
        _settingsWindow?.Close();
        _fallbackDashboardDb?.Dispose();

        // Only dispose ActivityTracker if the host is still alive.
        // During reset, _host is already disposed (which disposes DI singletons
        // including ActivityTracker), so re-disposing would throw ObjectDisposedException.
        if (_host != null)
            (_activityTracker as IDisposable)?.Dispose();

        _trayIcon?.Dispose();
        _shellCommandServer?.Dispose();
        _shellCommandServer = null;

        if (_host != null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                TryLaunchWatchdogProcess("shutdown");
                _host.Dispose();
                _host = null;
            }
        }

        _instanceGuard?.Dispose();
        Log.CloseAndFlush();

        base.OnExit(e);
    }

    private SyncEngineHostedService? GetSyncService()
    {
        return _host?.Services.GetServices<IHostedService>()
            .OfType<SyncEngineHostedService>()
            .FirstOrDefault();
    }

    private void RegisterExplorerIntegration(AppSettings settings)
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
                return;

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: false));
            var registrar = new ExplorerContextMenuRegistrar(loggerFactory.CreateLogger<ExplorerContextMenuRegistrar>());
            registrar.EnsureRegistered(executablePath, settings.SyncRootPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Explorer context menu registration failed");
        }
    }

    private void StartShellCommandServer()
    {
        if (_shellCommandServer != null)
            return;

        var loggerFactory = _host!.Services.GetRequiredService<ILoggerFactory>();
        _shellCommandServer = new ShellCommandServer(loggerFactory.CreateLogger<ShellCommandServer>());
        _shellCommandServer.CommandReceived += command =>
            Dispatcher.InvokeAsync(() => HandleShellCommandAsync(command)).Task.Unwrap();
        _shellCommandServer.Start();
    }

    private async Task HandleShellCommandAsync(ShellCommand command)
    {
        Log.Information("Shell command received: {Command} Path={Path}", command.Command, command.Path);
        switch (command.Command.ToLowerInvariant())
        {
            case "retry":
                await TriggerManualSyncAsync();
                await ShowProblemsAsync();
                break;
            case "resolve-conflict":
            case "open-problems":
                await ShowProblemsAsync();
                break;
            case "dismiss-error":
                DismissProblemsForPath(command.Path);
                await ShowProblemsAsync();
                break;
        }
    }

    private async Task ShowProblemsAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            _activityPanel?.HideFlyout();
            OpenSettingsWindow(SettingsSection.Problems);
        });
    }

    private void DismissProblemsForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var db = GetSyncService()?.Coordinator?.Db;
        if (db == null)
            return;

        db.ResolveProblemsByDedupeKey(SyncProblemKeys.Conflict(path));
        db.ResolveProblemsByDedupeKey(SyncProblemKeys.Upload(path));
        db.ResolveProblemsByDedupeKey(SyncProblemKeys.Download(path));
    }

    private AppDashboardContext CreateDashboardContext()
    {
        var syncService = GetSyncService();
        var coordinator = syncService?.Coordinator;
        var loggerFactory = _host!.Services.GetRequiredService<ILoggerFactory>();
        var updateService = _host.Services.GetRequiredService<UpdateService>();

        var db = coordinator?.Db;
        if (db == null)
        {
            var currentSettings = AppSettings.Load();
            var dataDir = currentSettings.DataDirectory ?? AppSettings.GetDataDirectory();
            var dbPath = Path.Combine(dataDir, "syncstate.db");
            _fallbackDashboardDb ??= new SyncStateDb(dbPath, loggerFactory.CreateLogger<SyncStateDb>());
            db = _fallbackDashboardDb;
        }

        return new AppDashboardContext(
            _activityTracker!,
            db,
            AppSettings.Load,
            () => GetSyncService()?.Coordinator?.CurrentState ?? SyncState.Disconnected,
            () => GetSyncService()?.Coordinator?.IsPaused ?? false,
            () => GetSyncService()?.WebDav,
            updateService.GetStatusSnapshot,
            OpenSettingsWindow,
            OpenFolder,
            OpenWebPortal,
            TriggerManualSyncAsync,
            TogglePauseResume,
            EnsureWatchdogScheduledTaskAsync,
            () => updateService.CheckForUpdatesNowAsync(),
            _appStartedAt,
            ConfirmRemoteDeleteAsync,
            KeepRemoteCopyAsync,
            ReuploadRemoteDeletedLocalChangeAsync,
            DeleteLocalRemoteDeletedLocalChangeAsync,
            updateService.ApplyUpdateAndRestart);
    }

    private async Task ConfirmRemoteDeleteAsync(long problemId, string localPath, string remotePath)
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator == null)
            return;

        await coordinator.ConfirmRemoteDeleteAsync(problemId, localPath, remotePath);
    }

    private async Task KeepRemoteCopyAsync(long problemId, string localPath, string remotePath)
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator == null)
            return;

        await coordinator.KeepRemoteCopyAsync(problemId, localPath, remotePath);
    }

    private async Task ReuploadRemoteDeletedLocalChangeAsync(long problemId, string localPath, string remotePath)
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator == null)
            return;

        await coordinator.ReuploadRemoteDeletedLocalChangeAsync(problemId, localPath, remotePath);
    }

    private async Task DeleteLocalRemoteDeletedLocalChangeAsync(long problemId, string localPath, string remotePath)
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator == null)
            return;

        await coordinator.DeleteLocalRemoteDeletedLocalChangeAsync(problemId, localPath, remotePath);
    }

    private void QueueRemoteDeleteDecision(SyncProblem problem)
    {
        if (problem.ProblemType != SyncProblemType.RemoteDeletedLocalChanged ||
            string.IsNullOrWhiteSpace(problem.LocalPath) ||
            string.IsNullOrWhiteSpace(problem.RemotePath))
        {
            return;
        }

        var key = GetRemoteDeleteDecisionKey(problem);
        if (string.IsNullOrWhiteSpace(key) ||
            _remoteDeleteDecisionPromptedKeys.Contains(key) ||
            !_remoteDeleteDecisionQueuedKeys.Add(key))
        {
            return;
        }

        _remoteDeleteDecisionQueue.Add(problem);
        _remoteDeleteDecisionTimer ??= CreateRemoteDeleteDecisionTimer();
        _remoteDeleteDecisionTimer.Stop();
        _remoteDeleteDecisionTimer.Start();
    }

    private DispatcherTimer CreateRemoteDeleteDecisionTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += RemoteDeleteDecisionTimer_Tick;
        return timer;
    }

    private async void RemoteDeleteDecisionTimer_Tick(object? sender, EventArgs e)
    {
        _remoteDeleteDecisionTimer?.Stop();
        await ShowRemoteDeleteDecisionPromptAsync();
    }

    private async Task ShowRemoteDeleteDecisionPromptAsync()
    {
        if (_remoteDeleteDecisionDialogOpen)
            return;

        var problems = _remoteDeleteDecisionQueue.ToList();
        _remoteDeleteDecisionQueue.Clear();
        _remoteDeleteDecisionQueuedKeys.Clear();

        var items = BuildRemoteDeleteDecisionItems(problems);
        if (items.Count == 0)
            return;

        foreach (var problem in problems)
        {
            var key = GetRemoteDeleteDecisionKey(problem);
            if (!string.IsNullOrWhiteSpace(key))
                _remoteDeleteDecisionPromptedKeys.Add(key);
        }

        _remoteDeleteDecisionDialogOpen = true;
        try
        {
            var owner = GetModalOwner();
            var dialog = new RemoteDeleteDecisionWindow(items);
            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.Topmost = true;
                dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            }

            if (dialog.ShowDialog() == true)
            {
                if (dialog.Decision == RemoteDeleteDecision.Reupload)
                    await ReuploadRemoteDeletedLocalChangesAsync(items);
                else if (dialog.Decision == RemoteDeleteDecision.DeleteLocal)
                    await DeleteLocalRemoteDeletedLocalChangesAsync(items);
            }
        }
        finally
        {
            _remoteDeleteDecisionDialogOpen = false;
            if (_remoteDeleteDecisionQueue.Count > 0)
            {
                _remoteDeleteDecisionTimer?.Stop();
                _remoteDeleteDecisionTimer?.Start();
            }
        }
    }

    private IReadOnlyList<RemoteDeleteDecisionItem> BuildRemoteDeleteDecisionItems(IReadOnlyList<SyncProblem> problems)
    {
        var settings = AppSettings.Load();
        var localizer = AppLocalizer.Instance;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RemoteDeleteDecisionItem>();

        foreach (var problem in problems)
        {
            if (string.IsNullOrWhiteSpace(problem.LocalPath) || string.IsNullOrWhiteSpace(problem.RemotePath))
                continue;

            var key = GetRemoteDeleteDecisionKey(problem);
            if (string.IsNullOrWhiteSpace(key) ||
                _remoteDeleteDecisionPromptedKeys.Contains(key) ||
                !seen.Add(key))
            {
                continue;
            }

            var displayName = Path.GetFileName(problem.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = problem.LocalPath;

            items.Add(new RemoteDeleteDecisionItem
            {
                ProblemId = problem.Id,
                LocalPath = problem.LocalPath,
                RemotePath = problem.RemotePath,
                DisplayName = displayName,
                Location = GetRemoteDeleteDecisionLocation(settings.SyncRootPath, problem.LocalPath),
                RemotePathDisplay = localizer.Format("RemoteDeleteDecision_RemotePath", problem.RemotePath)
            });
        }

        return items;
    }

    private async Task ReuploadRemoteDeletedLocalChangesAsync(IReadOnlyList<RemoteDeleteDecisionItem> items)
    {
        foreach (var item in items)
            await ReuploadRemoteDeletedLocalChangeAsync(item.ProblemId, item.LocalPath, item.RemotePath);
    }

    private async Task DeleteLocalRemoteDeletedLocalChangesAsync(IReadOnlyList<RemoteDeleteDecisionItem> items)
    {
        foreach (var item in items)
            await DeleteLocalRemoteDeletedLocalChangeAsync(item.ProblemId, item.LocalPath, item.RemotePath);
    }

    private System.Windows.Window? GetModalOwner()
    {
        if (_settingsWindow?.IsVisible == true)
            return _settingsWindow;

        if (_activityPanel?.IsVisible == true)
            return _activityPanel;

        return null;
    }

    private static string? GetRemoteDeleteDecisionKey(SyncProblem problem)
    {
        if (!string.IsNullOrWhiteSpace(problem.DedupeKey))
            return problem.DedupeKey;

        return !string.IsNullOrWhiteSpace(problem.LocalPath)
            ? SyncProblemKeys.RemoteDeletedLocalChanged(problem.LocalPath)
            : null;
    }

    private static string GetRemoteDeleteDecisionLocation(string syncRootPath, string localPath)
    {
        try
        {
            var relative = Path.GetRelativePath(syncRootPath, localPath);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative;
        }
        catch
        {
            // Fall back to the full path when the sync root and item cannot be relativized.
        }

        return localPath;
    }

    private async Task EnsureWatchdogScheduledTaskAsync()
    {
        if (_host == null)
            return;

        try
        {
            var loggerFactory = _host.Services.GetRequiredService<ILoggerFactory>();
            var registrar = new WatchdogTaskRegistrar(
                new WatchdogScheduledTaskXmlBuilder(),
                new WatchdogProcessRunner(),
                loggerFactory.CreateLogger<WatchdogTaskRegistrar>());

            await registrar.EnsureRegisteredAsync(ResolveWatchdogExecutablePath());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Watchdog scheduled task registration failed");
        }
    }

    private static string ResolveWatchdogExecutablePath()
    {
        var appDirectory = AppContext.BaseDirectory;
        var primaryCandidate = Path.Combine(appDirectory, "CloudDrive.Watchdog.exe");
        if (File.Exists(primaryCandidate))
            return primaryCandidate;

        var fallbackCandidate = Path.GetFullPath(Path.Combine(
            appDirectory,
            "..",
            "..",
            "..",
            "..",
            "CloudDrive.Watchdog",
            "bin",
            GetBuildConfigurationName(appDirectory),
            "net9.0-windows10.0.22621.0",
            "CloudDrive.Watchdog.exe"));

        return fallbackCandidate;
    }

    private static void TryLaunchWatchdogProcess(string reason)
    {
        try
        {
            var watchdogExecutablePath = ResolveWatchdogExecutablePath();
            if (!File.Exists(watchdogExecutablePath))
            {
                Log.Debug("Skipping watchdog launch for {Reason} because {Path} does not exist", reason, watchdogExecutablePath);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = watchdogExecutablePath,
                Arguments = $"--{reason}",
                WorkingDirectory = Path.GetDirectoryName(watchdogExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Log.Information("Queued watchdog process for {Reason}", reason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to launch watchdog process for {Reason}", reason);
        }
    }

    private static string GetBuildConfigurationName(string appDirectory)
    {
        var trimmed = appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directory = new DirectoryInfo(trimmed);
        return directory.Parent?.Name ?? "Debug";
    }

    private void OpenSettingsWindow(SettingsSection section)
    {
        Log.Information("UI[Settings] Open requested for section {Section}", section);
        _activityPanel?.HideFlyout();

        if (_settingsWindow != null && !_settingsWindow.IsLoaded)
        {
            _settingsWindow = null;
        }

        if (_settingsWindow == null)
        {
            var loggerFactory = _host!.Services.GetRequiredService<ILoggerFactory>();
            var vm = new SettingsViewModel(
                loggerFactory,
                CreateDashboardContext(),
                resetCallback: ResetCloudDriveLocalDataAsync,
                resetConfigurationCallback: ResetCloudDriveConfigurationAndLogsAsync,
                remoteTargetResetCallback: ResetCloudDriveLocalDataForRemoteTargetChangeAsync);
            _settingsWindow = new SettingsWindow(vm);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.NavigateTo(section);
        _settingsWindow.Show();
        _settingsWindow.WindowState = System.Windows.WindowState.Normal;
        _settingsWindow.Activate();
        Log.Information("UI[Settings] Window activated for section {Section}", section);
    }

    private async Task ToggleActivityFlyoutAsync()
    {
        Log.Information("UI[Flyout] Toggle start. ExistingWindow={HasWindow}", _activityPanel != null);
        if (_activityPanel != null && !_activityPanel.IsLoaded)
        {
            Log.Warning("UI[Flyout] Existing flyout window was not loaded. Recreating.");
            _activityPanel = null;
        }

        if (_activityPanel == null)
        {
            Log.Information("UI[Flyout] Creating new flyout window");
            _activityPanel = new ActivityPanel(CreateDashboardContext());
        }

        if (_activityPanel.IsVisible)
        {
            Log.Information("UI[Flyout] Hiding visible flyout");
            _activityPanel.HideFlyout();
            return;
        }

        Log.Information("UI[Flyout] Showing flyout");
        await _activityPanel.ShowFlyoutAsync();
        Log.Information("UI[Flyout] Show request completed. Visible={IsVisible}", _activityPanel.IsVisible);
    }

    private void OpenFolder()
    {
        var path = AppSettings.Load().SyncRootPath;
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
    }

    private void OpenWebPortal()
    {
        var url = AppSettings.Load().WebDavUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var portalUri = new Uri(uri.GetLeftPart(UriPartial.Authority));
            Process.Start(new ProcessStartInfo
            {
                FileName = portalUri.ToString(),
                UseShellExecute = true
            });
        }
    }

    private async Task TriggerManualSyncAsync()
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator != null)
        {
            await coordinator.RunManualSyncAsync();
        }
    }

    private void TogglePauseResume()
    {
        var coordinator = GetSyncService()?.Coordinator;
        if (coordinator == null)
            return;

        if (coordinator.IsPaused)
            coordinator.Resume();
        else
            coordinator.Pause();
    }

    private void OnUpdateReady(UpdateService updateService)
    {
        Log.Information("Update is ready to install.");

        _trayIcon?.ShowBalloon(
            AppLocalizer.Instance.GetString("App_Name"),
            AppLocalizer.Instance.GetString("Tray_Balloon_UpdateReady"),
            System.Windows.Forms.ToolTipIcon.Info,
            () => _ = Dispatcher.InvokeAsync(async () => await OpenUpdateHealthCheckAsync(updateService)));

        _trayIcon?.ShowUpdateAvailable(() => updateService.ApplyUpdateAndRestart());
    }

    private async Task OpenUpdateHealthCheckAsync(UpdateService updateService)
    {
        try
        {
            Log.Information("Update notification clicked; opening health check and checking for updates.");
            OpenSettingsWindow(SettingsSection.HealthCheck);

            if (_settingsWindow?.DataContext is SettingsViewModel viewModel)
            {
                await viewModel.ExecuteHealthActionAsync("check-updates");
                return;
            }

            await updateService.CheckForUpdatesNowAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update notification action failed.");
        }
    }

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    private Task ResetCloudDriveLocalDataAsync() =>
        ResetCloudDriveWithSharedCleanupAsync(
            AppSettings.Load(),
            clearSavedConfiguration: false,
            clearLogs: false);

    private Task ResetCloudDriveConfigurationAndLogsAsync() =>
        ResetCloudDriveWithSharedCleanupAsync(
            AppSettings.Load(),
            clearSavedConfiguration: true,
            clearLogs: true);

    private Task ResetCloudDriveLocalDataForRemoteTargetChangeAsync(AppSettings previousSettings) =>
        ResetCloudDriveWithSharedCleanupAsync(
            previousSettings,
            clearSavedConfiguration: false,
            clearLogs: false,
            restartAfterCleanup: true);

    private async Task CleanupLocalStateForRemoteTargetChangeBeforeStartupAsync(AppSettings previousSettings)
    {
        var cleanupService = new LocalStateCleanupService();
        var cleanupResult = await cleanupService.CleanupAsync(
            previousSettings,
            new LocalStateCleanupOptions(
                ClearSavedConfiguration: false,
                ClearCredentials: false));

        if (PromptForRebootIfRequired(cleanupResult))
            Shutdown();
    }

    private async Task ResetCloudDriveWithSharedCleanupAsync(
        AppSettings settings,
        bool clearSavedConfiguration,
        bool clearLogs,
        bool restartAfterCleanup = false)
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
            _host = null;
        }

        var cleanupService = new LocalStateCleanupService();
        var cleanupResult = await cleanupService.CleanupAsync(
            settings,
            new LocalStateCleanupOptions(
                ClearSavedConfiguration: clearSavedConfiguration,
                ClearCredentials: clearSavedConfiguration));

        var rebootRequested = PromptForRebootIfRequired(cleanupResult);

        if (clearLogs)
        {
            Log.Information("Reset: Closing logger before deleting logs");
            Log.CloseAndFlush();

            try
            {
                LocalStateCleanupService.DeleteLogs();
            }
            catch
            {
                // Best effort after logging is already shut down.
            }

            Shutdown();
            return;
        }

        if (restartAfterCleanup && !rebootRequested)
            RestartApplication();

        Log.CloseAndFlush();
        Shutdown();
    }

    private static bool PromptForRebootIfRequired(LocalStateCleanupResult cleanupResult)
    {
        if (!cleanupResult.RequiresReboot)
            return false;

        Log.Warning("Reset: Sync root folder could not be deleted - reboot may be required");

        var rebootResult = System.Windows.MessageBox.Show(
            AppLocalizer.Instance.GetString("Reset_RestartRequired_Message"),
            AppLocalizer.Instance.GetString("Reset_RestartRequired_Title"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information,
            System.Windows.MessageBoxResult.No);

        if (rebootResult != System.Windows.MessageBoxResult.Yes)
            return false;

        Process.Start("shutdown", "/r /t 5 /c \"CloudDrive reset: restarting to complete cleanup\"");
        return true;
    }

    private async Task ResetCloudDriveAsync(bool clearSavedConfiguration, bool clearLogs)
    {
        var settings = AppSettings.Load();

        // a. Stop the sync engine (triggers SyncCoordinator.StopAsync → disconnect)
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
            _host = null;
        }

        // b. Revert all placeholders to normal files (strips cfapi reparse points)
        if (Directory.Exists(settings.SyncRootPath))
        {
            try
            {
                var (reverted, failed) = PlaceholderReverter.RevertAll(settings.SyncRootPath);
                Log.Information("Reset: Reverted {Reverted} placeholders ({Failed} failed)", reverted, failed);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Reset: Placeholder revert encountered errors");
            }
        }

        // c. Unregister sync root
        if (!string.IsNullOrEmpty(settings.WebDavUrl))
        {
            try
            {
                var accountId = new Uri(settings.WebDavUrl).Host;
                var syncRootId = SyncRootRegistrar.GetSyncRootId(accountId);
                global::Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(syncRootId);
                Log.Information("Reset: Unregistered sync root {SyncRootId}", syncRootId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Reset: Failed to unregister sync root");
            }
        }

        // d. Brief delay to let the minifilter fully detach after unregister
        await Task.Delay(500);

        // e. Clear sync status on the path
        try { SyncRootConnector.ClearSyncStatus(settings.SyncRootPath); }
        catch { /* best effort */ }

        // f. Delete sync root folder (with retry)
        if (Directory.Exists(settings.SyncRootPath))
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(settings.SyncRootPath, recursive: true);
                    Log.Information("Reset: Deleted sync root folder {Path}", settings.SyncRootPath);
                    break;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    Log.Warning(ex, "Reset: Delete attempt {Attempt}/3 failed, retrying...", attempt + 1);
                    await Task.Delay(1000 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Reset: Failed to delete sync root folder after 3 attempts");
                }
            }
        }

        // g. If folder still exists, write cleanup marker and offer reboot
        if (Directory.Exists(settings.SyncRootPath))
        {
            Log.Warning("Reset: Sync root folder could not be deleted — reboot may be required");
            PendingCleanup.Create(settings.SyncRootPath);
            Log.Information("Reset: Pending cleanup marker written for {Path}", settings.SyncRootPath);

            var rebootResult = System.Windows.MessageBox.Show(
                AppLocalizer.Instance.GetString("Reset_RestartRequired_Message"),
                AppLocalizer.Instance.GetString("Reset_RestartRequired_Title"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information,
                System.Windows.MessageBoxResult.No);

            if (rebootResult == System.Windows.MessageBoxResult.Yes)
            {
                Process.Start("shutdown", "/r /t 5 /c \"CloudDrive reset: restarting to complete cleanup\"");
            }
        }

        if (!Directory.Exists(settings.SyncRootPath))
        {
            PendingCleanup.Remove();
        }

        // h. Delete SQLite database
        var dataDir = AppSettings.GetDataDirectory();
        var dbPath = Path.Combine(dataDir, "syncstate.db");
        try
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            // Also delete WAL/SHM files
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
            Log.Information("Reset: Deleted sync state database");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reset: Failed to delete database");
        }

        // i. Delete persisted watchdog/control panel status
        var watchdogStatusPath = WatchdogTaskConstants.GetStatusFilePath();
        try
        {
            if (File.Exists(watchdogStatusPath))
            {
                File.Delete(watchdogStatusPath);
                Log.Information("Reset: Deleted watchdog status snapshot");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reset: Failed to delete watchdog status snapshot");
        }

        // j. Clear or preserve saved configuration depending on reset scope
        if (clearSavedConfiguration)
        {
            try
            {
                CredentialManager.DeletePassword();
                Log.Information("Reset: Deleted stored password");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Reset: Failed to delete stored password");
            }

            new AppSettings().Save();
            Log.Information("Reset: Saved configuration reset to defaults");
        }
        else
        {
            Log.Information("Reset: Saved configuration preserved");
        }

        // k. Delete log files only for the full reset once the logger has released handles
        if (clearLogs)
        {
            var logsDir = Path.Combine(dataDir, "logs");
            Log.Information("Reset: Closing logger before deleting logs");
            Log.CloseAndFlush();

            try
            {
                if (Directory.Exists(logsDir))
                {
                    Directory.Delete(logsDir, recursive: true);
                }
            }
            catch
            {
                // Best effort after logging is already shut down.
            }

            Shutdown();
            return;
        }

        // l. Shut down the app
        Log.CloseAndFlush();
        Shutdown();
    }

    private static string GetCrashLogDir() =>
        Path.Combine(AppSettings.GetDataDirectory(), "logs");

    private bool TryHandleRecoverableUiException(Exception ex)
    {
        if (!IsRecoverableUiException(ex))
            return false;

        WriteCrashLog(ex);
        Log.Error(ex, "Recoverable UI exception");

        try
        {
            _activityPanel?.ForceClose();
            _activityPanel = null;
        }
        catch
        {
        }

        try
        {
            _settingsWindow?.Close();
            _settingsWindow = null;
        }
        catch
        {
        }

        _trayIcon?.ShowBalloon(
            AppLocalizer.Instance.GetString("App_Name"),
            AppLocalizer.Instance.GetString("App_NonFatalUiError_Message"),
            System.Windows.Forms.ToolTipIcon.Warning);
        return true;
    }

    private static bool IsRecoverableUiException(Exception ex)
    {
        var details = ex.ToString();
        return details.Contains("LiveChartsCore.SkiaSharpView.WPF", StringComparison.Ordinal) ||
               details.Contains("CompositionTargetTicker", StringComparison.Ordinal);
    }

    private void HandleFatalException(Exception? ex, string logMessage)
    {
        if (Interlocked.Exchange(ref _fatalExceptionHandled, 1) == 1)
            return;

        WriteCrashLog(ex);
        Log.Fatal(ex, logMessage);
        Log.CloseAndFlush();

        try
        {
            var localizer = AppLocalizer.Instance;
            var message = localizer.Format(
                "App_Error_RestartPrompt",
                ex?.Message ?? localizer.GetString("Common_Unknown"),
                GetCrashLogDir());

            var restart = System.Windows.MessageBox.Show(
                message,
                localizer.GetString("App_Error_Title"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Error,
                System.Windows.MessageBoxResult.Yes);

            if (restart == System.Windows.MessageBoxResult.Yes)
            {
                RestartApplication();
            }
        }
        catch
        {
        }

        if (Dispatcher.CheckAccess())
            Shutdown(1);
        else
            Dispatcher.Invoke(() => Shutdown(1));
    }

    private static void RestartApplication()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping 127.0.0.1 -n 3 > nul && start \"\" \"{executablePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
    }

    private void WireMountPhaseNotifications(SyncEngineHostedService svc)
    {
        // Poll for state machine availability after a short delay
        // (it's created during ExecuteAsync, which runs after StartAsync returns)
        Task.Run(async () =>
        {
            // Wait for state machine to be created
            for (int i = 0; i < 30; i++)
            {
                if (svc.StateMachine != null) break;
                await Task.Delay(500);
            }

            if (svc.StateMachine == null) return;

            svc.StateMachine.OnPhaseChanged += phase =>
            {
                switch (phase)
                {
                    case MountPhase.WaitingForServer:
                        _serverWaitStopwatch ??= Stopwatch.StartNew();
                        // Start monitoring for 2-minute notification
                        if (!_serverWaitNotificationShown)
                        {
                            Task.Run(async () =>
                            {
                                while (svc.StateMachine.CurrentPhase == MountPhase.WaitingForServer ||
                                       svc.StateMachine.CurrentPhase == MountPhase.VerifyingConnection)
                                {
                                    if (_serverWaitStopwatch.Elapsed > TimeSpan.FromMinutes(2) && !_serverWaitNotificationShown)
                                    {
                                        _serverWaitNotificationShown = true;
                                        Dispatcher.Invoke(() => _trayIcon?.ShowBalloon(
                                            AppLocalizer.Instance.GetString("App_Name"),
                                            AppLocalizer.Instance.GetString("Tray_Balloon_ServerStillUnreachable"),
                                            System.Windows.Forms.ToolTipIcon.Warning));
                                        return;
                                    }
                                    await Task.Delay(5000);
                                }
                            });
                        }
                        break;

                    case MountPhase.ConnectionLost:
                        _disconnectionStopwatch = Stopwatch.StartNew();
                        _disconnectionNotificationShown = false;
                        // Start monitoring for 5-minute notification
                        Task.Run(async () =>
                        {
                            while (svc.StateMachine.CurrentPhase == MountPhase.ConnectionLost)
                            {
                                if (_disconnectionStopwatch.Elapsed > TimeSpan.FromMinutes(5) && !_disconnectionNotificationShown)
                                {
                                    _disconnectionNotificationShown = true;
                                    Dispatcher.Invoke(() => _trayIcon?.ShowBalloon(
                                        AppLocalizer.Instance.GetString("App_Name"),
                                        AppLocalizer.Instance.GetString("Tray_Balloon_DisconnectedLong"),
                                        System.Windows.Forms.ToolTipIcon.Warning));
                                    return;
                                }
                                await Task.Delay(5000);
                            }
                        });
                        break;

                    case MountPhase.StaleCleanupFailed:
                        Dispatcher.Invoke(() => _trayIcon?.ShowBalloon(
                            AppLocalizer.Instance.GetString("App_Name"),
                            AppLocalizer.Instance.GetString("Tray_Balloon_StaleCleanupFailed"),
                            System.Windows.Forms.ToolTipIcon.Error));
                        break;

                    case MountPhase.Ready:
                        // Reset notification flags for next occurrence
                        _serverWaitStopwatch = null;
                        _disconnectionStopwatch = null;
                        break;
                }
            };
        });
    }



    private static void WriteCrashLog(Exception? ex)
    {
        try
        {
            var dir = GetCrashLogDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var content = $"""
                CloudDrive Crash Report
                =======================
                Time: {DateTime.Now:O}
                OS: {Environment.OSVersion}
                .NET: {Environment.Version}

                Exception:
                {ex}
                """;
            File.WriteAllText(path, content);
        }
        catch
        {
            // Last resort — can't do much if crash logging itself fails
        }
    }
}
