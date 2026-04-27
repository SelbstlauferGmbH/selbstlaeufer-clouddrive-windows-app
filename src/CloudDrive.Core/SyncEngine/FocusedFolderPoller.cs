using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public sealed class FocusedFolderPoller
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly IFocusedFolderProvider _focusedFolderProvider;
    private readonly DiscoveryWalker _discoveryWalker;
    private readonly PropagatorQueue _queue;
    private readonly ILogger<FocusedFolderPoller> _logger;
    private readonly TimeSpan _interval;

    public FocusedFolderPoller(
        IFocusedFolderProvider focusedFolderProvider,
        DiscoveryWalker discoveryWalker,
        PropagatorQueue queue,
        ILogger<FocusedFolderPoller> logger,
        TimeSpan? interval = null)
    {
        _focusedFolderProvider = focusedFolderProvider;
        _discoveryWalker = discoveryWalker;
        _queue = queue;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Focused folder poll failed");
            }

            await Task.Delay(_interval, ct);
        }
    }

    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var focusedPath = _focusedFolderProvider.GetCurrentFocusedPath();
        if (string.IsNullOrWhiteSpace(focusedPath))
            return 0;

        if (!Directory.Exists(focusedPath))
            return 0;

        var actions = await _discoveryWalker.WalkAsync(focusedPath, depth: 1, ct);
        var enqueued = 0;
        foreach (var action in actions.Where(a => a.Type != ReconcileActionType.NoOp))
        {
            _queue.Enqueue(action);
            enqueued++;
        }

        _logger.LogDebug("Focused folder poll completed: {FocusedPath} Enqueued={Enqueued}", focusedPath, enqueued);
        return enqueued;
    }
}
