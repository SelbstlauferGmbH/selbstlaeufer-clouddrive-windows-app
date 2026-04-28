using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Provider;

namespace CloudDrive.Core.SyncRoot;

public enum ExplorerItemState
{
    Clear = 0,
    Synced = 1,
    Syncing = 2,
    Conflict = 3,
    Error = 4,
    Pinned = 5,
    Unpinned = 6
}

public sealed class ExplorerItemStateService
{
    public const int SyncedPropertyId = 1;
    public const int SyncingPropertyId = 2;
    public const int ConflictPropertyId = 3;
    public const int ErrorPropertyId = 4;
    public const int PinnedPropertyId = 5;
    public const int UnpinnedPropertyId = 6;

    private readonly ILogger<ExplorerItemStateService> _logger;

    public ExplorerItemStateService(ILogger<ExplorerItemStateService> logger)
    {
        _logger = logger;
    }

    public async Task SetStateAsync(string localPath, ExplorerItemState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var item = await GetStorageItemAsync(localPath);
            if (item == null)
                return;

            var properties = state == ExplorerItemState.Clear
                ? []
                : new[] { ToProperty(state) };

            await StorageProviderItemProperties.SetAsync(item, properties);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to set Explorer item state {State} for {Path}", state, localPath);
        }
    }

    private static async Task<IStorageItem?> GetStorageItemAsync(string localPath)
    {
        if (Directory.Exists(localPath))
            return await StorageFolder.GetFolderFromPathAsync(localPath);

        if (File.Exists(localPath))
            return await StorageFile.GetFileFromPathAsync(localPath);

        return null;
    }

    private static StorageProviderItemProperty ToProperty(ExplorerItemState state)
    {
        var (id, value, icon) = state switch
        {
            ExplorerItemState.Synced => (SyncedPropertyId, "Synced", "%SystemRoot%\\system32\\imageres.dll,-1025"),
            ExplorerItemState.Syncing => (SyncingPropertyId, "Syncing", "%SystemRoot%\\system32\\imageres.dll,-16739"),
            ExplorerItemState.Conflict => (ConflictPropertyId, "Conflict", "%SystemRoot%\\system32\\imageres.dll,-98"),
            ExplorerItemState.Error => (ErrorPropertyId, "Error", "%SystemRoot%\\system32\\imageres.dll,-101"),
            ExplorerItemState.Pinned => (PinnedPropertyId, "Pinned", "%SystemRoot%\\system32\\imageres.dll,-16710"),
            ExplorerItemState.Unpinned => (UnpinnedPropertyId, "Online-only", "%SystemRoot%\\system32\\imageres.dll,-16711"),
            _ => (SyncedPropertyId, "Synced", "%SystemRoot%\\system32\\imageres.dll,-1025")
        };

        return new StorageProviderItemProperty
        {
            Id = id,
            Value = value,
            IconResource = icon
        };
    }
}
