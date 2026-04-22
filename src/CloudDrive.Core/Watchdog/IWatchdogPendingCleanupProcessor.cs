namespace CloudDrive.Core.Watchdog;

public interface IWatchdogPendingCleanupProcessor
{
    WatchdogPendingCleanupResult Process();
}
