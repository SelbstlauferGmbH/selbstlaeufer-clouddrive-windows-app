namespace CloudDrive.Core.Watchdog;

public sealed record WatchdogImportantMessage(
    DateTimeOffset TimestampUtc,
    string Message);
