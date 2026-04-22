using System.Diagnostics;

namespace CloudDrive.Core.Watchdog;

public interface IWatchdogProcessRunner
{
    Task<WatchdogProcessRunnerResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct = default);
}
