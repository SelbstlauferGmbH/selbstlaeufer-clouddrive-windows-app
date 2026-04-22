using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace CloudDrive.App.Services;

public enum UpdateStatusState
{
    Unknown,
    Checking,
    Reachable,
    UpdateReady,
    Failed,
    NotInstalled
}

public sealed record UpdateStatusSnapshot(
    UpdateStatusState State,
    string CurrentVersion,
    string SourceUrl,
    string AttemptedLocation,
    DateTimeOffset? LastCheckedUtc = null,
    string? AvailableVersion = null,
    string? FailureMessage = null)
{
    public static UpdateStatusSnapshot CreateUnknown(string sourceUrl, string currentVersion) =>
        new(UpdateStatusState.Unknown, currentVersion, sourceUrl, sourceUrl);
}

public sealed class UpdateService : BackgroundService
{
    private const string ReleasesRepositoryUrl = "https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(4);

    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager _updateManager;
    private readonly object _syncLock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private VelopackAsset? _pendingUpdate;
    private bool _updateReadyRaised;
    private UpdateStatusSnapshot _statusSnapshot;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _updateManager = new UpdateManager(BuildSource());
        _statusSnapshot = CreateInitialSnapshot();
    }

    public event Action? UpdateReady;

    public static string ReleasesPageUrl => $"{ReleasesRepositoryUrl}/releases";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_updateManager.IsInstalled)
        {
            SetStatus(new UpdateStatusSnapshot(
                UpdateStatusState.NotInstalled,
                GetCurrentVersion(),
                ReleasesPageUrl,
                ReleasesPageUrl));
            _logger.LogInformation("Skipping update checks because the app is not running as a Velopack-installed app.");
            return;
        }

        _logger.LogInformation(
            "Update service started. CurrentVersion={CurrentVersion}, Repository={Repository}",
            _updateManager.CurrentVersion?.ToString() ?? "unknown",
            ReleasesRepositoryUrl);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(PeriodicInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void ApplyUpdateAndRestart()
    {
        VelopackAsset? updateToApply;
        lock (_syncLock)
        {
            updateToApply = _pendingUpdate ?? _updateManager.UpdatePendingRestart;
        }

        if (updateToApply == null)
        {
            _logger.LogInformation("Update install requested, but no downloaded update is pending.");
            return;
        }

        _logger.LogInformation("Applying downloaded update {Version} and restarting.", updateToApply.Version);
        Log.CloseAndFlush();
        _updateManager.ApplyUpdatesAndRestart(updateToApply);
    }

    public UpdateStatusSnapshot GetStatusSnapshot()
    {
        lock (_syncLock)
        {
            return _statusSnapshot;
        }
    }

    public Task CheckForUpdatesNowAsync(CancellationToken ct = default) => RunCheckAsync(ct);

    private async Task RunCheckAsync(CancellationToken ct)
    {
        await _checkGate.WaitAsync(ct);
        var attemptedLocation = ReleasesPageUrl;

        try
        {
            SetStatus(new UpdateStatusSnapshot(
                UpdateStatusState.Checking,
                GetCurrentVersion(),
                ReleasesPageUrl,
                attemptedLocation,
                AvailableVersion: GetPendingUpdateVersion()));

            if (!_updateManager.IsInstalled)
            {
                SetStatus(new UpdateStatusSnapshot(
                    UpdateStatusState.NotInstalled,
                    GetCurrentVersion(),
                    ReleasesPageUrl,
                    attemptedLocation,
                    DateTimeOffset.UtcNow));
                return;
            }

            if (TryMarkUpdateReady(_updateManager.UpdatePendingRestart, "A previously-downloaded update {Version} is ready to install."))
            {
                SetStatus(new UpdateStatusSnapshot(
                    UpdateStatusState.UpdateReady,
                    GetCurrentVersion(),
                    ReleasesPageUrl,
                    attemptedLocation,
                    DateTimeOffset.UtcNow,
                    GetPendingUpdateVersion()));
                return;
            }

            lock (_syncLock)
            {
                if (_pendingUpdate != null)
                {
                    _logger.LogDebug("Skipping update check because version {Version} is already ready to install.", _pendingUpdate.Version);
                    SetStatus(new UpdateStatusSnapshot(
                        UpdateStatusState.UpdateReady,
                        GetCurrentVersion(),
                        ReleasesPageUrl,
                        attemptedLocation,
                        DateTimeOffset.UtcNow,
                        _pendingUpdate.Version.ToString()));
                    return;
                }
            }

            _logger.LogInformation("Checking GitHub Releases for updates.");

            var update = await _updateManager.CheckForUpdatesAsync();
            if (update == null)
            {
                _logger.LogDebug("No update is currently available.");
                SetStatus(new UpdateStatusSnapshot(
                    UpdateStatusState.Reachable,
                    GetCurrentVersion(),
                    ReleasesPageUrl,
                    attemptedLocation,
                    DateTimeOffset.UtcNow));
                return;
            }

            _logger.LogInformation(
                "Update available. CurrentVersion={CurrentVersion}, TargetVersion={TargetVersion}",
                _updateManager.CurrentVersion?.ToString() ?? "unknown",
                update.TargetFullRelease.Version);

            attemptedLocation = $"Release asset: {update.TargetFullRelease.FileName}";
            SetStatus(new UpdateStatusSnapshot(
                UpdateStatusState.Checking,
                GetCurrentVersion(),
                ReleasesPageUrl,
                attemptedLocation,
                AvailableVersion: update.TargetFullRelease.Version.ToString()));

            var lastLoggedProgress = -10;
            await _updateManager.DownloadUpdatesAsync(
                update,
                progress =>
                {
                    if (progress < 100 && progress < lastLoggedProgress + 10)
                        return;

                    lastLoggedProgress = progress;
                    _logger.LogInformation("Update download progress: {ProgressPercent}%.", progress);
                },
                ct);

            var pendingUpdate = _updateManager.UpdatePendingRestart ?? update.TargetFullRelease;
            TryMarkUpdateReady(pendingUpdate, "Update {Version} downloaded and ready to install.");
            SetStatus(new UpdateStatusSnapshot(
                UpdateStatusState.UpdateReady,
                GetCurrentVersion(),
                ReleasesPageUrl,
                attemptedLocation,
                DateTimeOffset.UtcNow,
                pendingUpdate.Version.ToString()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            SetStatus(new UpdateStatusSnapshot(
                UpdateStatusState.Failed,
                GetCurrentVersion(),
                ReleasesPageUrl,
                attemptedLocation,
                DateTimeOffset.UtcNow,
                GetPendingUpdateVersion(),
                BuildFailureMessage(ex)));
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private bool TryMarkUpdateReady(VelopackAsset? update, string logMessageTemplate)
    {
        if (update == null)
            return false;

        var shouldRaiseEvent = false;
        lock (_syncLock)
        {
            _pendingUpdate = update;
            if (!_updateReadyRaised)
            {
                _updateReadyRaised = true;
                shouldRaiseEvent = true;
            }
        }

        if (shouldRaiseEvent)
        {
            _logger.LogInformation(logMessageTemplate, update.Version);
            UpdateReady?.Invoke();
        }

        return true;
    }

    private UpdateStatusSnapshot CreateInitialSnapshot()
    {
        var currentVersion = GetCurrentVersion();
        var pendingUpdate = _updateManager.UpdatePendingRestart;
        if (pendingUpdate != null)
        {
            return new UpdateStatusSnapshot(
                UpdateStatusState.UpdateReady,
                currentVersion,
                ReleasesPageUrl,
                ReleasesPageUrl,
                AvailableVersion: pendingUpdate.Version.ToString());
        }

        return _updateManager.IsInstalled
            ? UpdateStatusSnapshot.CreateUnknown(ReleasesPageUrl, currentVersion)
            : new UpdateStatusSnapshot(
                UpdateStatusState.NotInstalled,
                currentVersion,
                ReleasesPageUrl,
                ReleasesPageUrl);
    }

    private void SetStatus(UpdateStatusSnapshot snapshot)
    {
        lock (_syncLock)
        {
            _statusSnapshot = snapshot;
        }
    }

    private string GetCurrentVersion() => _updateManager.CurrentVersion?.ToString() ?? "unknown";

    private string? GetPendingUpdateVersion()
    {
        lock (_syncLock)
        {
            return (_pendingUpdate ?? _updateManager.UpdatePendingRestart)?.Version.ToString();
        }
    }

    private static string BuildFailureMessage(Exception ex) => ex.GetBaseException().Message;

    private static IUpdateSource BuildSource()
    {
        return new GithubSource(ReleasesRepositoryUrl, string.Empty, prerelease: false);
    }
}
