namespace CloudDrive.Core.Watchdog;

public enum WatchdogDisconnectedReason
{
    None,
    AppNotRunning,
    ConfigurationMissing,
    CredentialsMissing,
    SyncRootMissing,
    ServerUnreachable,
    HighLatency,
    ListingFailed,
    UnexpectedError
}
