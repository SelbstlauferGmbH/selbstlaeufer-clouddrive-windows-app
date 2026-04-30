using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Provider;

namespace CloudDrive.Core.SyncRoot;

public enum ExplorerItemState
{
    Clear = 0,
    Conflict = 3
}

public sealed class ExplorerItemStateService
{
    // Availability badges are owned by Windows Cloud Files placeholder state.
    // This custom item-property layer is only used for states CFAPI does not project.
    public const int ConflictPropertyId = 3;

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
            ExplorerItemState.Conflict => (ConflictPropertyId, "Conflict", "%SystemRoot%\\system32\\imageres.dll,-98"),
            _ => (ConflictPropertyId, "Conflict", "%SystemRoot%\\system32\\imageres.dll,-98")
        };

        return new StorageProviderItemProperty
        {
            Id = id,
            Value = value,
            IconResource = icon
        };
    }
}
