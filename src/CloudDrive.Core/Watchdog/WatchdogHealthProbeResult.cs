namespace CloudDrive.Core.Watchdog;

public sealed record WatchdogHealthProbeResult(
    bool IsConnected,
    WatchdogDisconnectedReason Reason,
    string? Detail = null,
    long? LatencyMs = null)
{
    public static WatchdogHealthProbeResult Connected(long? latencyMs = null) =>
        new(true, WatchdogDisconnectedReason.None, null, latencyMs);

    public static WatchdogHealthProbeResult Disconnected(
        WatchdogDisconnectedReason reason,
        string? detail = null,
        long? latencyMs = null) =>
        new(false, reason, detail, latencyMs);
}
