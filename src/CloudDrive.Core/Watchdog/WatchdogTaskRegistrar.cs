using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogTaskRegistrar
{
    private readonly WatchdogScheduledTaskXmlBuilder _xmlBuilder;
    private readonly IWatchdogProcessRunner _processRunner;
    private readonly ILogger<WatchdogTaskRegistrar> _logger;
    private readonly TimeProvider _timeProvider;

    public WatchdogTaskRegistrar(
        WatchdogScheduledTaskXmlBuilder xmlBuilder,
        IWatchdogProcessRunner processRunner,
        ILogger<WatchdogTaskRegistrar> logger,
        TimeProvider? timeProvider = null)
    {
        _xmlBuilder = xmlBuilder;
        _processRunner = processRunner;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureRegisteredAsync(string watchdogExecutablePath, CancellationToken ct = default)
    {
        if (!File.Exists(watchdogExecutablePath))
        {
            _logger.LogDebug("Watchdog executable not found at {Path}, skipping scheduled task registration", watchdogExecutablePath);
            return;
        }

        var taskXml = _xmlBuilder.Build(watchdogExecutablePath, _timeProvider.GetLocalNow());
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            $"clouddrive-watchdog-task-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(tempFile, taskXml, Encoding.Unicode);

            var result = await _processRunner.RunAsync(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /TN \"{WatchdogTaskConstants.TaskName}\" /XML \"{tempFile}\" /F",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }, ct);

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("Watchdog scheduled task registered at {TaskName}", WatchdogTaskConstants.TaskName);
                return;
            }

            _logger.LogWarning(
                "Watchdog scheduled task registration failed with exit code {ExitCode}: {Error}",
                result.ExitCode,
                string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register watchdog scheduled task");
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete temporary task file {Path}", tempFile);
            }
        }
    }
}
