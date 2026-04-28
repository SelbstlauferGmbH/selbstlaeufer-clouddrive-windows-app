using System.Text;
using CloudDrive.Core.WebDav;

namespace CloudDrive.Core.Tests.Fakes;

internal sealed class FakeWebDavService : ISyncCollectionWebDavService
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = Entry.Directory("/")
    };

    public bool SyncCollectionSupported { get; set; } = true;
    public WebDavLockSupport LockSupport { get; set; } =
        WebDavLockSupport.Supported("Fake WebDAV lock support is enabled.");

    public int SyncCollectionReportCount { get; private set; }
    public int LockCount { get; private set; }
    public int UnlockCount { get; private set; }
    public int UploadWithLockTokenCount { get; private set; }
    public string? LastUploadLockToken { get; private set; }

    private readonly Dictionary<string, string> _lockTokens = new(StringComparer.OrdinalIgnoreCase);

    public void AddDirectory(string remotePath)
    {
        remotePath = Normalize(remotePath);
        EnsureParent(remotePath);
        _entries[remotePath] = Entry.Directory(remotePath);
    }

    public void AddFile(string remotePath, string content, string? etag = null)
    {
        AddFile(remotePath, Encoding.UTF8.GetBytes(content), etag);
    }

    public void AddFile(string remotePath, byte[] content, string? etag = null)
    {
        remotePath = Normalize(remotePath);
        EnsureParent(remotePath);
        _entries[remotePath] = Entry.File(remotePath, content, etag ?? NewETag());
    }

    public Task<IReadOnlyList<RemoteItem>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        if (!_entries.TryGetValue(remotePath, out var directory) || !directory.IsDirectory)
            throw new DirectoryNotFoundException(remotePath);

        var items = _entries.Values
            .Where(e => !string.Equals(e.RemotePath, remotePath, StringComparison.OrdinalIgnoreCase))
            .Where(e => string.Equals(GetParent(e.RemotePath), remotePath, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ToRemoteItem())
            .OrderBy(e => e.RemotePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteItem>>(items);
    }

    public Task<SyncCollectionResult> ReportSyncCollectionAsync(
        string remotePath,
        string? syncToken,
        int depth,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SyncCollectionReportCount++;
        if (!SyncCollectionSupported)
            return Task.FromResult(new SyncCollectionResult(false, syncToken, []));

        remotePath = Normalize(remotePath);
        if (!_entries.TryGetValue(remotePath, out var directory) || !directory.IsDirectory)
            throw new DirectoryNotFoundException(remotePath);

        var descendants = _entries.Values
            .Where(e => !string.Equals(e.RemotePath, remotePath, StringComparison.OrdinalIgnoreCase))
            .Where(e => IsWithinDepth(remotePath, e.RemotePath, depth))
            .Select(e => e.ToRemoteItem())
            .OrderBy(e => e.RemotePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(new SyncCollectionResult(true, $"fake-token-{SyncCollectionReportCount}", descendants));
    }

    public Task<Stream> DownloadFileAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        if (!_entries.TryGetValue(remotePath, out var entry) || entry.IsDirectory)
            throw new FileNotFoundException(remotePath);

        return Task.FromResult<Stream>(new MemoryStream(entry.Content ?? []));
    }

    public Task<Stream> DownloadFilePriorityAsync(string remotePath, CancellationToken ct = default) =>
        DownloadFileAsync(remotePath, ct);

    public Task<Stream> DownloadFilePriorityAsync(string remotePath, long offset, long? length = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        if (!_entries.TryGetValue(remotePath, out var entry) || entry.IsDirectory)
            throw new FileNotFoundException(remotePath);

        var content = entry.Content ?? [];
        var start = Math.Min(Math.Max(offset, 0), content.Length);
        var count = length.HasValue
            ? Math.Min(length.Value, content.Length - start)
            : content.Length - start;
        return Task.FromResult<Stream>(new MemoryStream(content.AsSpan((int)start, (int)count).ToArray()));
    }

    public Task<string?> UploadFileAsync(string remotePath, Stream content, CancellationToken ct = default) =>
        UploadFileAsync(remotePath, content, lockToken: null, ct);

    public async Task<string?> UploadFileAsync(string remotePath, Stream content, string? lockToken, CancellationToken ct = default)
    {
        remotePath = Normalize(remotePath);
        EnsureParent(remotePath);
        LastUploadLockToken = lockToken;
        if (!string.IsNullOrWhiteSpace(lockToken))
            UploadWithLockTokenCount++;

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var etag = NewETag();
        _entries[remotePath] = Entry.File(remotePath, buffer.ToArray(), etag);
        return etag;
    }

    public Task DeleteAsync(string remotePath, CancellationToken ct = default) =>
        DeleteAsync(remotePath, lockToken: null, ct);

    public Task DeleteAsync(string remotePath, string? lockToken, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        foreach (var path in _entries.Keys
                     .Where(path => string.Equals(path, remotePath, StringComparison.OrdinalIgnoreCase) ||
                                    path.StartsWith(remotePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (path != "/")
                _entries.Remove(path);
        }

        return Task.CompletedTask;
    }

    public Task MoveAsync(string fromPath, string toPath, CancellationToken ct = default) =>
        MoveAsync(fromPath, toPath, lockToken: null, ct);

    public Task MoveAsync(string fromPath, string toPath, string? lockToken, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        fromPath = Normalize(fromPath);
        toPath = Normalize(toPath);
        if (!_entries.TryGetValue(fromPath, out var entry))
            throw new FileNotFoundException(fromPath);
        if (_entries.ContainsKey(toPath))
            throw new IOException($"Destination already exists: {toPath}");

        EnsureParent(toPath);
        _entries.Remove(fromPath);
        _entries[toPath] = entry with { RemotePath = toPath, LastModified = DateTime.UtcNow };
        return Task.CompletedTask;
    }

    public Task<WebDavLockInfo> LockAsync(string remotePath, WebDavLockRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        var token = $"<opaquelocktoken:{Guid.NewGuid():N}>";
        _lockTokens[remotePath] = token;
        LockCount++;
        return Task.FromResult(new WebDavLockInfo(remotePath, token, DateTimeOffset.UtcNow.Add(request.Timeout)));
    }

    public Task<WebDavLockInfo> RefreshLockAsync(string remotePath, string lockToken, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        _lockTokens[remotePath] = lockToken;
        return Task.FromResult(new WebDavLockInfo(remotePath, lockToken, DateTimeOffset.UtcNow.Add(timeout)));
    }

    public Task UnlockAsync(string remotePath, string lockToken, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        _lockTokens.Remove(remotePath);
        UnlockCount++;
        return Task.CompletedTask;
    }

    public Task<WebDavLockSupport> CheckLockSupportAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LockSupport);
    }

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        AddDirectory(remotePath);
        return Task.CompletedTask;
    }

    public Task<RemoteItem?> GetPropertiesAsync(string remotePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        remotePath = Normalize(remotePath);
        return Task.FromResult(_entries.TryGetValue(remotePath, out var entry) ? entry.ToRemoteItem() : null);
    }

    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<HealthCheckResult> HealthCheckAsync(CancellationToken ct = default) =>
        Task.FromResult(HealthCheckResult.Success(0));

    private void EnsureParent(string remotePath)
    {
        var parent = GetParent(remotePath);
        if (!_entries.ContainsKey(parent))
            AddDirectory(parent);
    }

    private static string Normalize(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath == ".")
            return "/";

        remotePath = remotePath.Replace('\\', '/').Trim();
        if (!remotePath.StartsWith('/'))
            remotePath = "/" + remotePath;
        return remotePath.Length > 1 ? remotePath.TrimEnd('/') : "/";
    }

    private static string GetParent(string remotePath)
    {
        remotePath = Normalize(remotePath);
        if (remotePath == "/")
            return "/";

        var index = remotePath.LastIndexOf('/');
        return index <= 0 ? "/" : remotePath[..index];
    }

    private static string GetName(string remotePath)
    {
        remotePath = Normalize(remotePath);
        if (remotePath == "/")
            return string.Empty;

        var index = remotePath.LastIndexOf('/');
        return remotePath[(index + 1)..];
    }

    private static bool IsWithinDepth(string rootPath, string candidatePath, int depth)
    {
        if (!candidatePath.StartsWith(rootPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) &&
            rootPath != "/")
        {
            return false;
        }

        var rootSegments = rootPath == "/"
            ? 0
            : rootPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var candidateSegments = candidatePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        return candidateSegments - rootSegments <= Math.Max(1, depth);
    }

    private static string NewETag() => $"etag-{Guid.NewGuid():N}";

    private sealed record Entry(
        string RemotePath,
        bool IsDirectory,
        byte[]? Content,
        string? ETag,
        DateTime LastModified)
    {
        public long Size => Content?.LongLength ?? 0;

        public static Entry Directory(string remotePath) =>
            new(remotePath, IsDirectory: true, Content: null, ETag: null, DateTime.UtcNow);

        public static Entry File(string remotePath, byte[] content, string etag) =>
            new(remotePath, IsDirectory: false, content, etag, DateTime.UtcNow);

        public RemoteItem ToRemoteItem() => new()
        {
            Name = GetName(RemotePath),
            RemotePath = RemotePath,
            IsDirectory = IsDirectory,
            Size = Size,
            LastModified = LastModified,
            ETag = ETag
        };
    }
}
