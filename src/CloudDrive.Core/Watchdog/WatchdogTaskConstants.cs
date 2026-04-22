using CloudDrive.Core.Configuration;

namespace CloudDrive.Core.Watchdog;

public static class WatchdogTaskConstants
{
    public const string TaskName = @"\CloudDrive\Watchdog";

    public static string GetStatusFilePath() =>
        Path.Combine(AppSettings.GetDataDirectory(), "watchdog-status.json");
}
