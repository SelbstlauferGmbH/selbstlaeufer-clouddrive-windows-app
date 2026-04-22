using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncRoot;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogCycleRunner
{
    private const int MaxImportantMessages = 5;

    private readonly IWatchdogAppLivenessProbe _appLivenessProbe;
    private readonly IWatchdogPendingCleanupProcessor _pendingCleanupProcessor;
    private readonly IWatchdogHealthProbe _healthProbe;
    private readonly IExplorerConnectionStateWriter _explorerStateWriter;
    private readonly IWatchdogStatusStore _statusStore;
    private readonly ILogger<WatchdogCycleRunner> _logger;
    private readonly TimeProvider _timeProvider;

    public WatchdogCycleRunner(
        IWatchdogAppLivenessProbe appLivenessProbe,
        IWatchdogPendingCleanupProcessor pendingCleanupProcessor,
        IWatchdogHealthProbe healthProbe,
        IExplorerConnectionStateWriter explorerStateWriter,
        IWatchdogStatusStore statusStore,
        ILogger<WatchdogCycleRunner> logger,
        TimeProvider? timeProvider = null)
    {
        _appLivenessProbe = appLivenessProbe;
        _pendingCleanupProcessor = pendingCleanupProcessor;
        _healthProbe = healthProbe;
        _explorerStateWriter = explorerStateWriter;
        _statusStore = statusStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WatchdogStatusSnapshot> RunAsync(AppSettings settings, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var previous = _statusStore.Load() ?? new WatchdogStatusSnapshot();
        var snapshot = new WatchdogStatusSnapshot
        {
            LastCheckUtc = now,
            SyncRootPath = settings.SyncRootPath,
            ImportantMessages =
            [
                .. previous.ImportantMessages
                    .OrderByDescending(message => message.TimestampUtc)
                    .Take(MaxImportantMessages)
            ]
        };

        AppendImportantMessage(snapshot, _pendingCleanupProcessor.Process(), now);

        if (string.IsNullOrWhiteSpace(settings.SyncRootPath) || !Directory.Exists(settings.SyncRootPath))
        {
            snapshot.ConnectionStatus = WatchdogConnectionStatus.Disconnected;
            snapshot.DisconnectedReason = WatchdogDisconnectedReason.SyncRootMissing;
            snapshot.ReasonDetail = AppLocalizer.Instance.GetString("Watchdog_Status_SyncRootMissing");
            AppendStateMessageIfNeeded(previous, snapshot, now);
            _statusStore.Save(snapshot);
            return snapshot;
        }

        snapshot.AppRunning = _appLivenessProbe.IsAppRunning();

        if (!snapshot.AppRunning)
        {
            snapshot.ConnectionStatus = WatchdogConnectionStatus.Disconnected;
            snapshot.DisconnectedReason = WatchdogDisconnectedReason.AppNotRunning;
            snapshot.ReasonDetail = AppLocalizer.Instance.GetString("Watchdog_Status_AppNotRunning");
            await WriteExplorerStateAsync(ExplorerVisualState.Disconnected, settings.SyncRootPath, snapshot, now);
            AppendStateMessageIfNeeded(previous, snapshot, now);
            _statusStore.Save(snapshot);
            return snapshot;
        }

        WatchdogHealthProbeResult healthResult;
        try
        {
            healthResult = await _healthProbe.CheckAsync(settings, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watchdog health probe failed unexpectedly");
            healthResult = WatchdogHealthProbeResult.Disconnected(
                WatchdogDisconnectedReason.UnexpectedError,
                ex.Message);
        }

        snapshot.ConnectionStatus = healthResult.IsConnected
            ? WatchdogConnectionStatus.Connected
            : WatchdogConnectionStatus.Disconnected;
        snapshot.DisconnectedReason = healthResult.Reason;
        snapshot.ReasonDetail = healthResult.Detail;
        snapshot.LatencyMs = healthResult.LatencyMs;

        var explorerState = healthResult.IsConnected
            ? ExplorerVisualState.Connected
            : ExplorerVisualState.Disconnected;
        await WriteExplorerStateAsync(explorerState, settings.SyncRootPath, snapshot, now);

        AppendStateMessageIfNeeded(previous, snapshot, now);
        _statusStore.Save(snapshot);
        return snapshot;
    }

    private async Task WriteExplorerStateAsync(
        ExplorerVisualState state,
        string syncRootPath,
        WatchdogStatusSnapshot snapshot,
        DateTimeOffset timestamp)
    {
        try
        {
            await _explorerStateWriter.SetStateAsync(state, syncRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update Explorer status to {State}", state);
            AppendImportantMessage(
                snapshot,
                new WatchdogImportantMessage(
                    timestamp,
                    AppLocalizer.Instance.GetString("Watchdog_Message_ExplorerUpdateFailed")));
        }
    }

    private static void AppendImportantMessage(
        WatchdogStatusSnapshot snapshot,
        WatchdogPendingCleanupResult result,
        DateTimeOffset timestamp)
    {
        if (result.Outcome == WatchdogPendingCleanupOutcome.None || string.IsNullOrWhiteSpace(result.Message))
            return;

        AppendImportantMessage(snapshot, new WatchdogImportantMessage(timestamp, result.Message));
    }

    private static void AppendImportantMessage(
        WatchdogStatusSnapshot snapshot,
        WatchdogImportantMessage message)
    {
        snapshot.ImportantMessages.Insert(0, message);
        while (snapshot.ImportantMessages.Count > MaxImportantMessages)
        {
            snapshot.ImportantMessages.RemoveAt(snapshot.ImportantMessages.Count - 1);
        }
    }

    private static void AppendStateMessageIfNeeded(
        WatchdogStatusSnapshot previous,
        WatchdogStatusSnapshot current,
        DateTimeOffset timestamp)
    {
        var shouldAppend = previous.LastCheckUtc == null
            || previous.ConnectionStatus != current.ConnectionStatus
            || previous.DisconnectedReason != current.DisconnectedReason
            || !string.Equals(previous.ReasonDetail, current.ReasonDetail, StringComparison.Ordinal);

        if (!shouldAppend)
            return;

        var message = BuildStateMessage(current);
        if (string.IsNullOrWhiteSpace(message))
            return;

        AppendImportantMessage(current, new WatchdogImportantMessage(timestamp, message));
    }

    private static string BuildStateMessage(WatchdogStatusSnapshot snapshot)
    {
        if (snapshot.ConnectionStatus == WatchdogConnectionStatus.Connected)
        {
            return snapshot.LatencyMs.HasValue
                ? AppLocalizer.Instance.Format("Watchdog_Message_ConnectedWithLatency", snapshot.LatencyMs.Value)
                : AppLocalizer.Instance.GetString("Watchdog_Message_Connected");
        }

        return snapshot.DisconnectedReason switch
        {
            WatchdogDisconnectedReason.AppNotRunning => AppLocalizer.Instance.GetString("Watchdog_Message_AppNotRunning"),
            WatchdogDisconnectedReason.ConfigurationMissing => AppLocalizer.Instance.GetString("Watchdog_Message_ConfigurationMissing"),
            WatchdogDisconnectedReason.CredentialsMissing => AppLocalizer.Instance.GetString("Watchdog_Message_CredentialsMissing"),
            WatchdogDisconnectedReason.SyncRootMissing => AppLocalizer.Instance.GetString("Watchdog_Message_SyncRootMissing"),
            WatchdogDisconnectedReason.ServerUnreachable => AppLocalizer.Instance.GetString("Watchdog_Message_ServerUnreachable"),
            WatchdogDisconnectedReason.HighLatency => snapshot.LatencyMs.HasValue
                ? AppLocalizer.Instance.Format("Watchdog_Message_HighLatency", snapshot.LatencyMs.Value)
                : AppLocalizer.Instance.GetString("Watchdog_Message_HighLatencyUnknown"),
            WatchdogDisconnectedReason.ListingFailed => AppLocalizer.Instance.GetString("Watchdog_Message_ListingFailed"),
            WatchdogDisconnectedReason.UnexpectedError => string.IsNullOrWhiteSpace(snapshot.ReasonDetail)
                ? AppLocalizer.Instance.GetString("Watchdog_Message_UnexpectedError")
                : AppLocalizer.Instance.Format("Watchdog_Message_UnexpectedErrorWithDetail", snapshot.ReasonDetail),
            _ => string.Empty
        };
    }
}
