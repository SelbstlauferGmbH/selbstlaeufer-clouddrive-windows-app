using System.Threading.Channels;
using CloudDrive.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public enum FileChangeType
{
    Created,
    Changed,
    PinRequested,
    DehydrateRequested,
    Deleted,
    Renamed
}

public record FileChangeEvent(
    FileChangeType ChangeType,
    string FullPath,
    string? OldFullPath = null);

public class LocalChangeWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<FileChangeEvent> _channel;
    private readonly ILogger<LocalChangeWatcher> _logger;
    private readonly Dictionary<string, DateTime> _debounce = new();
    private readonly Dictionary<string, DateTime> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _debounceLock = new();
    private readonly TimeSpan _debounceInterval = TimeSpan.FromSeconds(2);
    private Timer? _debounceTimer;

    public ChannelReader<FileChangeEvent> Changes => _channel.Reader;

    public LocalChangeWatcher(string syncRootPath, ILogger<LocalChangeWatcher> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<FileChangeEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        if (!Directory.Exists(syncRootPath))
            throw new DirectoryNotFoundException(
                $"Sync root directory '{syncRootPath}' does not exist. Register the sync root before starting the watcher.");

        _watcher = new FileSystemWatcher(syncRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.Attributes
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024 // 64 KB
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("Local change watcher started");
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
        _debounceTimer?.Dispose();
        _logger.LogInformation("Local change watcher stopped");
    }

    public void SuppressPath(string path, TimeSpan duration)
    {
        lock (_debounceLock)
        {
            _suppressedPaths[path] = DateTime.UtcNow.Add(duration);
        }

        _logger.LogDebug("Suppressing local watcher events for {Path} until {Until}", path, DateTime.UtcNow.Add(duration));
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (IsSuppressed(e.FullPath))
            return;

        if (TransientFilePolicy.ShouldIgnoreLocalPath(e.FullPath))
        {
            _logger.LogDebug("Ignoring transient local create: {Path}", e.FullPath);
            return;
        }

        // Skip placeholder entries being created by cfapi.
        if (CloudFilePlaceholderHelper.TryGetPlaceholderState(e.FullPath, out _))
            return;

        EnqueueDebounced(new FileChangeEvent(FileChangeType.Created, e.FullPath));
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsSuppressed(e.FullPath))
            return;

        if (TransientFilePolicy.ShouldIgnoreLocalPath(e.FullPath))
        {
            _logger.LogDebug("Ignoring transient local change: {Path}", e.FullPath);
            return;
        }

        if (CloudFilePlaceholderHelper.TryGetPlaceholderState(e.FullPath, out var placeholderState))
        {
            if (placeholderState!.ShouldHydratePinnedFile)
            {
                _logger.LogInformation("Pinned placeholder queued for hydration: {Path}", e.FullPath);
                _channel.Writer.TryWrite(new FileChangeEvent(FileChangeType.PinRequested, e.FullPath));
                return;
            }

            if (placeholderState.ShouldDehydrateUnpinnedFile)
            {
                _logger.LogInformation(
                    "Unpinned placeholder queued for dehydration: {Path} PinState={PinState} InSyncState={InSyncState} OnDiskDataSize={OnDiskDataSize}",
                    e.FullPath,
                    placeholderState.PinState,
                    placeholderState.InSyncState,
                    placeholderState.OnDiskDataSize);
                _channel.Writer.TryWrite(new FileChangeEvent(FileChangeType.DehydrateRequested, e.FullPath));
                return;
            }

            if (placeholderState.IsUnpinned && placeholderState.HasDataOnDisk && !placeholderState.ShouldDehydrateUnpinnedFile)
            {
                _logger.LogWarning(
                    "Unpinned placeholder is not ready for dehydration: {Path} InSyncState={InSyncState} ModifiedDataSize={ModifiedDataSize} OnDiskDataSize={OnDiskDataSize}",
                    e.FullPath,
                    placeholderState.InSyncState,
                    placeholderState.ModifiedDataSize,
                    placeholderState.OnDiskDataSize);
            }

            if (placeholderState.ShouldSuppressWatcherChange)
            {
                _logger.LogDebug("Ignoring provider-owned placeholder change: {Path}", e.FullPath);
                return;
            }
        }

        EnqueueDebounced(new FileChangeEvent(FileChangeType.Changed, e.FullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsSuppressed(e.FullPath))
            return;

        if (TransientFilePolicy.ShouldIgnoreLocalPath(e.FullPath))
        {
            _logger.LogDebug("Ignoring transient local delete: {Path}", e.FullPath);
            return;
        }

        _channel.Writer.TryWrite(new FileChangeEvent(FileChangeType.Deleted, e.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsSuppressed(e.FullPath) || (!string.IsNullOrEmpty(e.OldFullPath) && IsSuppressed(e.OldFullPath)))
            return;

        var oldIsTransient = !string.IsNullOrEmpty(e.OldFullPath) &&
                             TransientFilePolicy.ShouldIgnoreLocalPath(e.OldFullPath);
        var newIsTransient = TransientFilePolicy.ShouldIgnoreLocalPath(e.FullPath);
        var oldIsProviderInternal = !string.IsNullOrEmpty(e.OldFullPath) &&
                                    TransientFilePolicy.IsProviderInternalLocalPath(e.OldFullPath);

        if (newIsTransient)
        {
            _logger.LogDebug("Ignoring transient local rename target: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            return;
        }

        if (oldIsProviderInternal)
        {
            _logger.LogDebug("Ignoring provider-owned local rename: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            return;
        }

        if (oldIsTransient)
        {
            _logger.LogDebug("Transient local file became durable: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            EnqueueDebounced(new FileChangeEvent(FileChangeType.Changed, e.FullPath));
            return;
        }

        _logger.LogDebug("Local watcher rename queued: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
        _channel.Writer.TryWrite(new FileChangeEvent(FileChangeType.Renamed, e.FullPath, e.OldFullPath));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error, restarting");
        _watcher.EnableRaisingEvents = false;
        _watcher.EnableRaisingEvents = true;
    }

    private void EnqueueDebounced(FileChangeEvent evt)
    {
        lock (_debounceLock)
        {
            _debounce[evt.FullPath] = DateTime.UtcNow;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(FlushDebounced, null, _debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void FlushDebounced(object? state)
    {
        lock (_debounceLock)
        {
            PruneSuppressed_NoLock();
            var cutoff = DateTime.UtcNow - _debounceInterval;
            var ready = _debounce.Where(kv => kv.Value <= cutoff).Select(kv => kv.Key).ToList();

            foreach (var path in ready)
            {
                _debounce.Remove(path);
                if (IsSuppressed_NoLock(path))
                    continue;

                var pathStillExists = File.Exists(path) || Directory.Exists(path);
                var changeType = pathStillExists ? FileChangeType.Changed : FileChangeType.Deleted;
                _channel.Writer.TryWrite(new FileChangeEvent(changeType, path));
            }

            // Reschedule if there are pending items
            if (_debounce.Count > 0)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(FlushDebounced, null, _debounceInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private bool IsSuppressed(string path)
    {
        lock (_debounceLock)
        {
            PruneSuppressed_NoLock();
            return IsSuppressed_NoLock(path);
        }
    }

    private bool IsSuppressed_NoLock(string path)
    {
        foreach (var entry in _suppressedPaths)
        {
            if (entry.Value < DateTime.UtcNow)
                continue;

            if (path.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(entry.Key + "\\", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Suppressed local watcher event for {Path}", path);
                return true;
            }
        }

        return false;
    }

    private void PruneSuppressed_NoLock()
    {
        var expired = _suppressedPaths
            .Where(kv => kv.Value < DateTime.UtcNow)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _suppressedPaths.Remove(key);
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        _watcher.Dispose();
        _channel.Writer.Complete();
    }
}
