namespace CloudDrive.Core.Watchdog;

public sealed class WatchdogStatusSnapshot
{
    public WatchdogConnectionStatus ConnectionStatus { get; set; } = WatchdogConnectionStatus.Unknown;
    public WatchdogDisconnectedReason DisconnectedReason { get; set; } = WatchdogDisconnectedReason.None;
    public DateTimeOffset? LastCheckUtc { get; set; }
    public long? LatencyMs { get; set; }
    public bool AppRunning { get; set; }
    public string? SyncRootPath { get; set; }
    public string? ReasonDetail { get; set; }
    public List<WatchdogImportantMessage> ImportantMessages { get; set; } = [];
}
