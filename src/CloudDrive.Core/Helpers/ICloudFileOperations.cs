using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Helpers;

public interface ICloudFileOperations
{
    bool TryGetPlaceholderState(string path, out CloudFilePlaceholderState? state);
    bool TryHydratePlaceholder(string path, ILogger? logger = null);
    bool TrySetInSyncState(string path, ILogger? logger = null);
    bool TryConvertToPlaceholder(string path, string? remotePath, ILogger? logger = null);
}

public sealed class CloudFileOperations : ICloudFileOperations
{
    public bool TryGetPlaceholderState(string path, out CloudFilePlaceholderState? state)
        => CloudFilePlaceholderHelper.TryGetPlaceholderState(path, out state);

    public bool TryHydratePlaceholder(string path, ILogger? logger = null)
        => CloudFilePlaceholderHelper.TryHydratePlaceholder(path, logger);

    public bool TrySetInSyncState(string path, ILogger? logger = null)
        => CloudFilePlaceholderHelper.TrySetInSyncState(path, logger);

    public bool TryConvertToPlaceholder(string path, string? remotePath, ILogger? logger = null)
        => CloudFilePlaceholderHelper.TryConvertToPlaceholder(path, remotePath, logger);
}
