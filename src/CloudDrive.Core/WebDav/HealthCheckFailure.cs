namespace CloudDrive.Core.WebDav;

/// <summary>
/// Represents the reason for a health check failure.
/// </summary>
public enum HealthCheckFailure
{
    /// <summary>
    /// Health check passed.
    /// </summary>
    None,

    /// <summary>
    /// Could not connect to the WebDAV server (timeout, DNS, connection refused).
    /// </summary>
    ServerUnreachable,

    /// <summary>
    /// Server responded but round-trip latency > 1000 ms.
    /// </summary>
    HighLatency,

    /// <summary>
    /// Connectivity OK but root folder listing failed (permission error, server error, etc.).
    /// </summary>
    ListingFailed
}
