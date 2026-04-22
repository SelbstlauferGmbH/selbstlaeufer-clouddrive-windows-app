namespace CloudDrive.Core.WebDav;

/// <summary>
/// Represents the outcome of a startup health check against the WebDAV server.
/// </summary>
public record HealthCheckResult(
    bool IsHealthy,
    long LatencyMs,
    HealthCheckFailure FailureReason,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful health check result.
    /// </summary>
    public static HealthCheckResult Success(long latencyMs) 
        => new(true, latencyMs, HealthCheckFailure.None);

    /// <summary>
    /// Creates a failed health check result.
    /// </summary>
    public static HealthCheckResult Failure(HealthCheckFailure reason, long latencyMs, string? errorMessage = null) 
        => new(false, latencyMs, reason, errorMessage);
}
