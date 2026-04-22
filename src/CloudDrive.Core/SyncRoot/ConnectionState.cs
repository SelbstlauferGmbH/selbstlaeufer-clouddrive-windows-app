namespace CloudDrive.Core.SyncRoot;

public record ConnectionState(
    bool IsConnected,
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? DisconnectedSince)
{
    public static ConnectionState Connected() =>
        new(true, 0, DateTimeOffset.UtcNow, null);

    public static ConnectionState Initial() =>
        new(false, 0, null, null);

    public ConnectionState RecordFailure() =>
        this with
        {
            ConsecutiveFailures = ConsecutiveFailures + 1,
            IsConnected = ConsecutiveFailures + 1 < 3,
            DisconnectedSince = ConsecutiveFailures + 1 >= 3 && DisconnectedSince == null
                ? DateTimeOffset.UtcNow : DisconnectedSince
        };

    public ConnectionState RecordSuccess() => Connected();

    public bool IsConnectionLost => ConsecutiveFailures >= 3;

    public TimeSpan? DisconnectedDuration =>
        DisconnectedSince.HasValue
            ? DateTimeOffset.UtcNow - DisconnectedSince.Value : null;
}
