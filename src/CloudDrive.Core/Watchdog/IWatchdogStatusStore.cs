namespace CloudDrive.Core.Watchdog;

public interface IWatchdogStatusStore
{
    WatchdogStatusSnapshot? Load();
    void Save(WatchdogStatusSnapshot snapshot);
}
