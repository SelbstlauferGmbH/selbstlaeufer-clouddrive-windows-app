using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogPendingCleanupProcessor : IWatchdogPendingCleanupProcessor
{
    private readonly ILogger<WatchdogPendingCleanupProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public WatchdogPendingCleanupProcessor(
        ILogger<WatchdogPendingCleanupProcessor> logger,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WatchdogPendingCleanupResult Process()
    {
        PendingCleanup? pending;
        try
        {
            pending = PendingCleanup.Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pending cleanup marker");
            return new WatchdogPendingCleanupResult(
                WatchdogPendingCleanupOutcome.Failed,
                AppLocalizer.Instance.GetString("Watchdog_Message_CleanupLoadFailed"));
        }

        if (pending == null)
            return new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.None);

        if (!Directory.Exists(pending.FolderPath))
        {
            PendingCleanup.Remove();
            return new WatchdogPendingCleanupResult(
                WatchdogPendingCleanupOutcome.RemovedMissingTargetMarker,
                AppLocalizer.Instance.Format("Watchdog_Message_CleanupMarkerRemoved", pending.FolderPath));
        }

        try
        {
            Directory.Delete(pending.FolderPath, recursive: true);
            PendingCleanup.Remove();
            return new WatchdogPendingCleanupResult(
                WatchdogPendingCleanupOutcome.DeletedFolder,
                AppLocalizer.Instance.Format("Watchdog_Message_CleanupSucceeded", pending.FolderPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pending cleanup failed for {Path}", pending.FolderPath);

            if (pending.CreatedUtc < _timeProvider.GetUtcNow().UtcDateTime.AddDays(-7))
            {
                PendingCleanup.Remove();
                return new WatchdogPendingCleanupResult(
                    WatchdogPendingCleanupOutcome.ExpiredMarkerRemoved,
                    AppLocalizer.Instance.Format("Watchdog_Message_CleanupExpired", pending.FolderPath));
            }

            return new WatchdogPendingCleanupResult(
                WatchdogPendingCleanupOutcome.Failed,
                AppLocalizer.Instance.Format("Watchdog_Message_CleanupFailed", pending.FolderPath));
        }
    }
}
