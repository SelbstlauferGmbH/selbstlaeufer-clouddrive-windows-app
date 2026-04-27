namespace CloudDrive.Core.Vfs;

public interface IVfs : IAsyncDisposable
{
    Task<VfsResult> RegisterAsync(VfsRegistration registration, CancellationToken ct);
    Task<VfsResult> ConnectAsync(CancellationToken ct);
    Task<VfsResult> DisconnectAsync(CancellationToken ct);
    Task<VfsResult> CreatePlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct);
    Task<VfsResult> ConvertToPlaceholderAsync(string localPath, VfsMetadata metadata, CancellationToken ct);
    Task<VfsResult> HydrateAsync(
        string localPath,
        Stream content,
        long expectedSize,
        IProgress<VfsTransferProgress>? progress,
        CancellationToken ct);
    Task<VfsResult> DehydrateAsync(string localPath, CancellationToken ct);
    Task<VfsResult> UpdateMetadataAsync(string localPath, VfsMetadata metadata, CancellationToken ct);
    Task<VfsResult<PinState>> GetPinStateAsync(string localPath, CancellationToken ct);
    Task<VfsResult> SetPinStateAsync(string localPath, PinState state, PinDescent descent, CancellationToken ct);
    Task<VfsResult> SetInSyncAsync(string localPath, bool inSync, CancellationToken ct);
    Task<PlaceholderInfo?> GetPlaceholderInfoAsync(string localPath, CancellationToken ct);
    Task<IReadOnlyList<VfsEntry>> EnumerateChildrenAsync(string localDirectoryPath, int depth, CancellationToken ct);
    Task<VfsResult> DeleteAsync(string localPath, bool recursive, CancellationToken ct);

    event EventHandler<VfsPinChangedEvent>? PinStateChanged;
}
