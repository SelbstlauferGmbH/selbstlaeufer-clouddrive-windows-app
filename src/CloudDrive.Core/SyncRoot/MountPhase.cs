namespace CloudDrive.Core.SyncRoot;

public enum MountPhase
{
    Idle,
    CheckingStale,
    CleaningStale,
    StaleCleanupFailed,
    VerifyingConnection,
    WaitingForServer,
    VerifyingListing,
    Registering,
    Ready,
    ConnectionLost,
    ShuttingDown,
    Stopped,
    Error
}
