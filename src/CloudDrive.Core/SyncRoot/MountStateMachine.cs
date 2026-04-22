using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncRoot;

public class MountStateMachine
{
    private readonly object _lock = new();
    private readonly ILogger<MountStateMachine> _logger;
    private MountPhase _currentPhase = MountPhase.Idle;

    private static readonly Dictionary<MountPhase, HashSet<MountPhase>> AllowedTransitions = new()
    {
        [MountPhase.Idle] = [MountPhase.CheckingStale],
        [MountPhase.CheckingStale] = [MountPhase.CleaningStale, MountPhase.VerifyingConnection],
        [MountPhase.CleaningStale] = [MountPhase.VerifyingConnection, MountPhase.StaleCleanupFailed],
        [MountPhase.StaleCleanupFailed] = [MountPhase.Error],
        [MountPhase.VerifyingConnection] = [MountPhase.VerifyingListing, MountPhase.WaitingForServer],
        [MountPhase.WaitingForServer] = [MountPhase.VerifyingConnection],
        [MountPhase.VerifyingListing] = [MountPhase.Registering, MountPhase.WaitingForServer],
        [MountPhase.Registering] = [MountPhase.Ready, MountPhase.Error],
        [MountPhase.Ready] = [MountPhase.ConnectionLost],
        [MountPhase.ConnectionLost] = [MountPhase.Ready],
        [MountPhase.ShuttingDown] = [MountPhase.Stopped],
        [MountPhase.Stopped] = [],
        [MountPhase.Error] = [],
    };

    // Terminal phases — no outbound transitions (except ShuttingDown via special path)
    private static readonly HashSet<MountPhase> TerminalPhases = [MountPhase.Stopped, MountPhase.Error];

    public event Action<MountPhase>? OnPhaseChanged;

    public MountStateMachine(ILogger<MountStateMachine> logger)
    {
        _logger = logger;
    }

    public MountPhase CurrentPhase
    {
        get { lock (_lock) { return _currentPhase; } }
    }

    public void TransitionTo(MountPhase newPhase)
    {
        lock (_lock)
        {
            var from = _currentPhase;

            // ShuttingDown is reachable from any non-terminal phase
            if (newPhase == MountPhase.ShuttingDown && !TerminalPhases.Contains(from))
            {
                _currentPhase = newPhase;
                _logger.LogInformation("Mount phase: {From} → {To}", from, newPhase);
            }
            else if (!AllowedTransitions.TryGetValue(from, out var allowed) || !allowed.Contains(newPhase))
            {
                throw new InvalidOperationException(
                    $"Invalid mount phase transition: {from} → {newPhase}");
            }
            else
            {
                _currentPhase = newPhase;
                _logger.LogInformation("Mount phase: {From} → {To}", from, newPhase);
            }
        }

        // Invoke outside lock to prevent deadlocks
        OnPhaseChanged?.Invoke(newPhase);
    }

    /// <summary>
    /// Returns a user-friendly message for the given phase, suitable for activity log display.
    /// </summary>
    public static string GetPhaseMessage(MountPhase phase) => phase switch
    {
        MountPhase.Idle => "Cloud drive starting\u2026",
        MountPhase.CheckingStale => "Checking for stale cloud drive registrations\u2026",
        MountPhase.CleaningStale => "Cleaning up stale cloud drive folder\u2026",
        MountPhase.StaleCleanupFailed => "Stale folder cleanup failed — please restart your computer",
        MountPhase.VerifyingConnection => "Verifying WebDAV server connection\u2026",
        MountPhase.WaitingForServer => "Waiting for server connection\u2026",
        MountPhase.VerifyingListing => "Verifying remote directory listing\u2026",
        MountPhase.Registering => "Registering cloud drive folder in Explorer\u2026",
        MountPhase.Ready => "Cloud drive is ready",
        MountPhase.ConnectionLost => "Connection to server lost — will retry automatically",
        MountPhase.ShuttingDown => "Shutting down cloud drive\u2026",
        MountPhase.Stopped => "Cloud drive shut down",
        MountPhase.Error => "Cloud drive encountered an error",
        _ => phase.ToString()
    };
}
