namespace CloudDrive.Core.Infrastructure;

/// <summary>
/// Interface for tracking application activity.
/// Implemented by the App layer to avoid circular dependencies.
/// </summary>
public interface IActivityTracker
{
    /// <summary>
    /// Records a completed activity entry.
    /// </summary>
    void Record(ActivityCategory category, string message, ActivityStatus status, TimeSpan? duration = null);

    /// <summary>
    /// Begins tracking an ongoing activity.
    /// </summary>
    IActivityScope Begin(string key, ActivityCategory category, string message);
}

/// <summary>
/// Scope object for tracking ongoing activities with RAII pattern.
/// </summary>
public interface IActivityScope : IDisposable
{
    /// <summary>
    /// Completes the activity with success status.
    /// </summary>
    void Complete();

    /// <summary>
    /// Completes the activity with a specific status.
    /// </summary>
    void CompleteWithStatus(ActivityStatus status);
}

/// <summary>
/// Categories for activity entries.
/// </summary>
public enum ActivityCategory
{
    WebDAV,
    Database,
    Sync,
    File,
    System,
    MountLifecycle
}

/// <summary>
/// Status of an activity entry.
/// </summary>
public enum ActivityStatus
{
    InProgress,
    Success,
    Failed,
    Warning
}
