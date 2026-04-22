namespace CloudDrive.Core.SyncRoot;

public record ReadinessGate(
    bool StaleCleanupComplete,
    bool ConnectionVerified,
    bool DirectoryListingVerified)
{
    public bool IsReady => StaleCleanupComplete
                        && ConnectionVerified
                        && DirectoryListingVerified;
}
