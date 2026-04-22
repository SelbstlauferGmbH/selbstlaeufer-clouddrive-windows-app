namespace CloudDrive.Core.SyncRoot;

public record StaleRegistrationInfo(
    string SyncRootId,
    string SyncRootPath,
    uint ProviderStatus,
    DateTimeOffset DetectedAt)
{
    public bool IsDisconnected =>
        ProviderStatus == 0x00000000   // CF_PROVIDER_STATUS_DISCONNECTED
     || ProviderStatus == 0xC0000001;  // CF_PROVIDER_STATUS_TERMINATED
}
