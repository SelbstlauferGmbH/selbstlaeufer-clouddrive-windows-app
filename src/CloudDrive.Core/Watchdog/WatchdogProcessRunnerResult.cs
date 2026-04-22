namespace CloudDrive.Core.Watchdog;

public sealed record WatchdogProcessRunnerResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
