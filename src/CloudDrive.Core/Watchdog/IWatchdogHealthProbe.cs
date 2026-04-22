using CloudDrive.Core.Configuration;

namespace CloudDrive.Core.Watchdog;

public interface IWatchdogHealthProbe
{
    Task<WatchdogHealthProbeResult> CheckAsync(AppSettings settings, CancellationToken ct = default);
}
