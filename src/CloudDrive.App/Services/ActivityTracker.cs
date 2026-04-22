using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using CloudDrive.Core.Infrastructure;

namespace CloudDrive.App.Services;

/// <summary>
/// Tracks application activity and operations for display in the activity panel.
/// Thread-safe implementation that maintains a rolling collection of the last 100 entries.
/// </summary>
public class ActivityTracker : IActivityTracker, IDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<ActivityTracker> _logger;
    private readonly ObservableCollection<ActivityEntry> _entries;
    private readonly Dictionary<string, ActivityEntry> _ongoingActivities;
    private readonly HashSet<string> _ongoingKeys;
    private CancellationTokenSource? _housekeepingCts;
    private Task? _housekeepingTask;

    /// <summary>
    /// Thread-safe collection of activity entries for UI binding.
    /// </summary>
    public IReadOnlyCollection<ActivityEntry> Entries
    {
        get { lock (_lock) { return _entries.ToList().AsReadOnly(); } }
    }

    /// <summary>
    /// Raised when new entries are added or updated.
    /// </summary>
    public event Action? EntriesChanged;

    public ActivityTracker(ILogger<ActivityTracker> logger)
    {
        _logger = logger;
        _entries = new ObservableCollection<ActivityEntry>();
        _ongoingActivities = new Dictionary<string, ActivityEntry>();
        _ongoingKeys = new HashSet<string>();
        
        // Start housekeeping to trim old entries periodically
        _housekeepingCts = new CancellationTokenSource();
        _housekeepingTask = Task.Run(() => HousekeepingLoop(_housekeepingCts.Token));
    }

    /// <summary>
    /// Records a completed activity entry.
    /// </summary>
    void IActivityTracker.Record(ActivityCategory category, string message, ActivityStatus status, TimeSpan? duration)
    {
        var entry = new ActivityEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Message = message,
            Duration = duration,
            Status = status
        };

        lock (_lock)
        {
            AddEntry(entry);
        }
    }

    /// <summary>
    /// Begins tracking an ongoing activity.
    /// </summary>
    IActivityScope IActivityTracker.Begin(string key, ActivityCategory category, string message)
    {
        lock (_lock)
        {
            // Remove any existing activity with the same key
            if (_ongoingActivities.TryGetValue(key, out var existing))
            {
                RemoveEntry(existing);
            }

            var entry = new ActivityEntry
            {
                Timestamp = DateTime.Now,
                Category = category,
                Message = message,
                Status = ActivityStatus.InProgress
            };

            _ongoingActivities[key] = entry;
            _ongoingKeys.Add(key);
            AddEntry(entry);
        }

        return new ActivityScope(this, key);
    }

    /// <summary>
    /// Completes an ongoing activity.
    /// </summary>
    internal void Complete(string key, ActivityStatus status)
    {
        lock (_lock)
        {
            if (!_ongoingActivities.TryGetValue(key, out var entry))
                return;

            // Remove from collection first
            RemoveEntry(entry);

            // Update and re-add as completed
            entry.Status = status;
            entry.Duration = DateTime.Now - entry.Timestamp;
            entry.Timestamp = DateTime.Now;

            _ongoingActivities.Remove(key);
            _ongoingKeys.Remove(key);
            AddEntry(entry);
        }
    }

    private void AddEntry(ActivityEntry entry)
    {
        // Insert at beginning (newest first)
        int insertIndex = 0;
        if (_entries.Count > 0)
        {
            // Find position to maintain sorted order by timestamp
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Timestamp < entry.Timestamp)
                {
                    insertIndex = i;
                    break;
                }
                insertIndex = i + 1;
            }
        }

        _entries.Insert(insertIndex, entry);

        // Trim to max entries
        while (_entries.Count > 100)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        EntriesChanged?.Invoke();
    }

    private void RemoveEntry(ActivityEntry entry)
    {
        _entries.Remove(entry);
        EntriesChanged?.Invoke();
    }

    private async Task HousekeepingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(60000, ct); // Run every minute
                
                lock (_lock)
                {
                    // Trim to max entries
                    while (_entries.Count > 100)
                    {
                        _entries.RemoveAt(_entries.Count - 1);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in activity tracker housekeeping");
            }
        }
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _housekeepingCts, null);
        if (cts == null) return; // Already disposed
        cts.Cancel();
        cts.Dispose();
        _housekeepingTask?.Wait(TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// Scope object for tracking ongoing activities with RAII pattern.
/// </summary>
public class ActivityScope : IActivityScope, IDisposable
{
    private readonly ActivityTracker _tracker;
    private readonly string _key;
    private bool _disposed;

    public ActivityScope(ActivityTracker tracker, string key)
    {
        _tracker = tracker;
        _key = key;
    }

    void IActivityScope.Complete()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.Complete(_key, ActivityStatus.Success);
    }

    void IActivityScope.CompleteWithStatus(ActivityStatus status)
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.Complete(_key, status);
    }

    public void Dispose()
    {
        ((IActivityScope)this).Complete();
    }
}

/// <summary>
/// Represents a single activity log entry.
/// </summary>
public class ActivityEntry : INotifyPropertyChanged
{
    private string _message = string.Empty;
    private TimeSpan? _duration;
    private ActivityStatus _status;

    public DateTime Timestamp { get; set; }
    public ActivityCategory Category { get; set; }
    
    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }
    
    public TimeSpan? Duration
    {
        get => _duration;
        set { _duration = value; OnPropertyChanged(); }
    }
    
    public ActivityStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Formatted display string for duration.
    /// </summary>
    public string DurationDisplay => Duration is TimeSpan d
        ? d.TotalMilliseconds < 1000 
            ? $"{d.TotalMilliseconds:F1}ms" 
            : $"{d.TotalSeconds:F2}s"
        : string.Empty;

    /// <summary>
    /// Formatted timestamp for display.
    /// </summary>
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
