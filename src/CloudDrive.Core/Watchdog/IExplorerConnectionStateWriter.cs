using CloudDrive.Core.SyncRoot;

namespace CloudDrive.Core.Watchdog;

public interface IExplorerConnectionStateWriter
{
    Task SetStateAsync(ExplorerVisualState state, string syncRootPath, CancellationToken ct = default);
}
