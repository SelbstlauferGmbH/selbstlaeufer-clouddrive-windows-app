using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Helpers;

public class PathMapper
{
    private readonly string _syncRootPath;
    private readonly string _remoteBasePath;
    private readonly ILogger<PathMapper>? _logger;

    public PathMapper(string syncRootPath, string remoteBasePath, ILogger<PathMapper>? logger = null)
    {
        _syncRootPath = syncRootPath.TrimEnd('\\', '/');
        _remoteBasePath = remoteBasePath.TrimEnd('/');
        _logger = logger;
    }

    public string ToRemotePath(string localPath)
    {
        var relativePath = Path.GetRelativePath(_syncRootPath, localPath);
        var remotePart = relativePath.Replace('\\', '/');
        var result = $"{_remoteBasePath}/{remotePart}";
        _logger?.LogDebug("PathMapper: ToRemotePath({LocalPath}) -> {RemotePath}", localPath, result);
        return result;
    }

    public string ToLocalPath(string remotePath)
    {
        var relative = remotePath;
        if (relative.StartsWith(_remoteBasePath, StringComparison.OrdinalIgnoreCase))
            relative = relative[_remoteBasePath.Length..];

        relative = relative.TrimStart('/').Replace('/', '\\');
        var result = Path.Combine(_syncRootPath, relative);
        _logger?.LogDebug("PathMapper: ToLocalPath({RemotePath}) -> {LocalPath}", remotePath, result);
        return result;
    }

    public string ToRelativePath(string localPath)
    {
        return Path.GetRelativePath(_syncRootPath, localPath);
    }

    /// <summary>
    /// Converts a cfapi NormalizedPath (e.g. \Users\testuser\CloudDriveClient\subfolder)
    /// to a remote WebDAV path (e.g. /subfolder). The NormalizedPath is the full local path
    /// without drive letter.
    /// </summary>
    public string NormalizedPathToRemotePath(string normalizedPath)
    {
        var syncRootWithoutDrive = _syncRootPath[Path.GetPathRoot(_syncRootPath)!.Length..];
        var trimmed = normalizedPath.TrimStart('\\');
        var prefix = syncRootWithoutDrive.TrimStart('\\');

        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[prefix.Length..].TrimStart('\\');

        var relativePath = trimmed.Replace('\\', '/');
        
        // FIX: Return base path without trailing slash for root
        // If relativePath is empty and _remoteBasePath is "/", we want "/" not ""
        var result = string.IsNullOrEmpty(relativePath)
            ? (string.IsNullOrEmpty(_remoteBasePath) ? "/" : _remoteBasePath) 
            : $"{_remoteBasePath}/{relativePath}";
        
        _logger?.LogDebug("PathMapper: NormalizedPathToRemotePath({NormalizedPath}) -> {RemotePath}", 
            normalizedPath, result);
        return result;
    }

    /// <summary>
    /// Converts a remote WebDAV path back to a cfapi NormalizedPath for debugging purposes.
    /// </summary>
    public string RemotePathToNormalizedPath(string remotePath)
    {
        var relative = remotePath;
        if (relative.StartsWith(_remoteBasePath, StringComparison.OrdinalIgnoreCase))
            relative = relative[_remoteBasePath.Length..];

        relative = relative.TrimStart('/').Replace('/', '\\');
        
        var syncRootWithoutDrive = _syncRootPath[Path.GetPathRoot(_syncRootPath)!.Length..];
        var result = $"\\{syncRootWithoutDrive}{(string.IsNullOrEmpty(relative) ? "" : "\\" + relative)}";
        
        _logger?.LogDebug("PathMapper: RemotePathToNormalizedPath({RemotePath}) -> {NormalizedPath}", 
            remotePath, result);
        return result;
    }

    public string GetSyncRootPath() => _syncRootPath;
}
