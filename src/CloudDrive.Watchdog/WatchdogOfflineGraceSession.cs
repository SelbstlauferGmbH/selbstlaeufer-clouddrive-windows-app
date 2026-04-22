using CloudDrive.Core.Configuration;
using CloudDrive.Core.SyncRoot;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Watchdog;

public sealed class WatchdogOfflineGraceSession
{
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly NTStatus SuccessStatus = new(0);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatchdogOfflineGraceSession> _logger;
    private readonly IWatchdogAppLivenessProbe _appLivenessProbe;

    public WatchdogOfflineGraceSession(
        ILoggerFactory loggerFactory,
        ILogger<WatchdogOfflineGraceSession> logger,
        IWatchdogAppLivenessProbe appLivenessProbe)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _appLivenessProbe = appLivenessProbe;
    }

    public async Task RunAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.SyncRootPath) || !Directory.Exists(settings.SyncRootPath))
        {
            _logger.LogDebug("Skipping offline watchdog grace session because sync root is unavailable");
            return;
        }

        if (_appLivenessProbe.IsAppRunning())
        {
            _logger.LogDebug("Skipping offline watchdog grace session because the app is already running again");
            return;
        }

        SyncRootAccountIdResolver.TryResolve(settings, out var accountId);

        using var connector = new SyncRootConnector(_loggerFactory.CreateLogger<SyncRootConnector>());
        var explorerStatusManager = new ExplorerStatusManager(
            _loggerFactory.CreateLogger<ExplorerStatusManager>(),
            accountId == null ? null : new SyncRootRegistrar(_loggerFactory.CreateLogger<SyncRootRegistrar>()),
            accountId);

        connector.FetchDataRequested += HandleFetchDataAsync;
        connector.FetchPlaceholdersRequested += HandleFetchPlaceholdersAsync;
        connector.NotifyDeleteRequested += HandleNotifyDeleteAsync;
        connector.NotifyRenameRequested += HandleNotifyRenameAsync;

        try
        {
            connector.Connect(settings.SyncRootPath);
            await explorerStatusManager.SetStateAsync(
                ExplorerVisualState.Disconnected,
                settings.SyncRootPath,
                connector);

            _logger.LogInformation(
                "Offline watchdog connected for up to {GraceSeconds}s to keep Explorer responsive",
                GraceWindow.TotalSeconds);

            var deadline = DateTime.UtcNow + GraceWindow;
            while (!ct.IsCancellationRequested &&
                   !_appLivenessProbe.IsAppRunning() &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline watchdog grace session failed");
        }
        finally
        {
            connector.Disconnect();
        }
    }

    private Task HandleFetchDataAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        var offset = Math.Max(callbackParameters.FetchData.RequiredFileOffset, 0);
        var length = callbackParameters.FetchData.RequiredLength;
        if (length <= 0)
            length = callbackParameters.FetchData.OptionalLength;
        if (length <= 0)
            length = Math.Max(callbackInfo.FileSize - offset, 1);

        _logger.LogInformation("Offline watchdog rejecting FETCH_DATA for {Path}", callbackInfo.NormalizedPath);
        SyncRootConnector.ReportFetchDataError(
            callbackInfo,
            SyncRootConnector.NetworkUnavailableStatus,
            offset,
            length,
            _logger);
        return Task.CompletedTask;
    }

    private Task HandleFetchPlaceholdersAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        _logger.LogInformation("Offline watchdog rejecting FETCH_PLACEHOLDERS for {Path}", callbackInfo.NormalizedPath);
        SyncRootConnector.ReportFetchPlaceholdersError(
            callbackInfo,
            SyncRootConnector.NetworkUnavailableStatus,
            _logger);
        return Task.CompletedTask;
    }

    private Task HandleNotifyDeleteAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        SyncRootConnector.AcknowledgeDelete(callbackInfo, SuccessStatus, _logger);
        return Task.CompletedTask;
    }

    private Task HandleNotifyRenameAsync(CF_CALLBACK_INFO callbackInfo, CF_CALLBACK_PARAMETERS callbackParameters)
    {
        SyncRootConnector.AcknowledgeRename(callbackInfo, SuccessStatus, _logger);
        return Task.CompletedTask;
    }
}
