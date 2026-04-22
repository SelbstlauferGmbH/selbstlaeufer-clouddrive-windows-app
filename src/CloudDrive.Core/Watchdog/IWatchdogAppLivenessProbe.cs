namespace CloudDrive.Core.Watchdog;

public interface IWatchdogAppLivenessProbe
{
    bool IsAppRunning();
}
