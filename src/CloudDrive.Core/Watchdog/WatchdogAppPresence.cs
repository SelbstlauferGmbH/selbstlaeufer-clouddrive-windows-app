namespace CloudDrive.Core.Watchdog;

public static class WatchdogAppPresence
{
    public static string GetEventName(string userSid) => $@"Global\CloudDriveAppRunning_{userSid}";
}
