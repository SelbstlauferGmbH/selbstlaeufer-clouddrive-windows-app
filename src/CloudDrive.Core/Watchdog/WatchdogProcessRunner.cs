using System.Diagnostics;

namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogProcessRunner : IWatchdogProcessRunner
{
    public async Task<WatchdogProcessRunnerResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");

        var standardOutputTask = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync(ct)
            : Task.FromResult(string.Empty);
        var standardErrorTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync(ct)
            : Task.FromResult(string.Empty);

        await process.WaitForExitAsync(ct);

        return new WatchdogProcessRunnerResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }
}
