namespace CloudDrive.Core.WebDav;

public interface IWebDavService
{
    Task<IReadOnlyList<RemoteItem>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task<Stream> DownloadFileAsync(string remotePath, CancellationToken ct = default);

    /// <summary>
    /// Downloads a file without going through the rate limiter.
    /// Used for user-initiated hydration where cfapi has a timeout.
    /// </summary>
    Task<Stream> DownloadFilePriorityAsync(string remotePath, CancellationToken ct = default);
    Task<Stream> DownloadFilePriorityAsync(string remotePath, long offset, long? length = null, CancellationToken ct = default);
    Task<string?> UploadFileAsync(string remotePath, Stream content, CancellationToken ct = default);
    Task DeleteAsync(string remotePath, CancellationToken ct = default);
    Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task<RemoteItem?> GetPropertiesAsync(string remotePath, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
    Task<HealthCheckResult> HealthCheckAsync(CancellationToken ct = default);
}
