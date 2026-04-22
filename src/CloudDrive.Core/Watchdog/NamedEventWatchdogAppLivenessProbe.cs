using System.Security.Principal;

namespace CloudDrive.Core.Watchdog;

public sealed class NamedEventWatchdogAppLivenessProbe : IWatchdogAppLivenessProbe
{
    public bool IsAppRunning()
    {
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var eventName = WatchdogAppPresence.GetEventName(userSid);

        if (!EventWaitHandle.TryOpenExisting(eventName, out var handle))
            return false;

        handle.Dispose();
        return true;
    }
}
