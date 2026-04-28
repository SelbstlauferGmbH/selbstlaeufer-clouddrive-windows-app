namespace CloudDrive.Core.SyncEngine;

using CloudDrive.Core.WebDav;

public interface IWebDavLockCoordinator
{
    bool IsEnabled { get; }

    WebDavLockSupport LockSupport { get; }

    void SetLockSupport(WebDavLockSupport lockSupport);

    Task HandleFileOpenAsync(string localPath, CancellationToken ct = default);

    Task HandleFileCloseAsync(string localPath, CancellationToken ct = default);

    Task<WebDavWriteLock> AcquireWriteLockAsync(string localPath, string remotePath, CancellationToken ct = default);

    Task NotifyUploadSucceededAsync(string localPath, string remotePath, CancellationToken ct = default);

    Task NotifyDeleteSucceededAsync(string remotePath, CancellationToken ct = default);

    Task ReleaseAllAsync(CancellationToken ct = default);
}

public sealed class WebDavWriteLock : IAsyncDisposable
{
    private readonly Func<Task>? _releaseAsync;
    private int _disposed;

    public WebDavWriteLock(string? token, Func<Task>? releaseAsync = null)
    {
        Token = token;
        _releaseAsync = releaseAsync;
    }

    public string? Token { get; }

    public static WebDavWriteLock None { get; } = new(null);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _releaseAsync == null)
            return;

        await _releaseAsync();
    }
}
