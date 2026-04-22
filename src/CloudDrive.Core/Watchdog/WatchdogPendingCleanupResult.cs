namespace CloudDrive.Core.Watchdog;

public enum WatchdogPendingCleanupOutcome
{
    None,
    DeletedFolder,
    RemovedMissingTargetMarker,
    ExpiredMarkerRemoved,
    Failed
}

public sealed record WatchdogPendingCleanupResult(
    WatchdogPendingCleanupOutcome Outcome,
    string? Message = null);
