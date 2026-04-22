using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncRoot;

/// <summary>
/// Translates ExplorerVisualState to the appropriate combination of cfapi calls.
/// Used by both the main app (with connector) and the watchdog (without connector).
/// </summary>
public class ExplorerStatusManager : IExplorerConnectionStateWriter
{
    private readonly ILogger<ExplorerStatusManager> _logger;
    private readonly SyncRootRegistrar? _registrar;
    private readonly string? _accountId;
    private readonly ExplorerWindowRefresher _windowRefresher;
    private ExplorerVisualState _currentState;

    public ExplorerStatusManager(
        ILogger<ExplorerStatusManager> logger,
        SyncRootRegistrar? registrar = null,
        string? accountId = null)
    {
        _logger = logger;
        _registrar = registrar;
        _accountId = accountId;
        _windowRefresher = new ExplorerWindowRefresher(logger);
    }

    public ExplorerVisualState CurrentState => _currentState;

    /// <summary>
    /// Orchestrates all cfapi calls for a state transition.
    /// Layer 1: CfUpdateSyncProviderStatus (connection-scoped, requires connector)
    /// Layer 2: CfReportSyncStatus (persistent, works without connector)
    /// Layer 3: Sync-root icon refresh via re-registration + duplicate cleanup
    /// </summary>
    public async Task SetStateAsync(
        ExplorerVisualState state,
        string syncRootPath,
        SyncRootConnector? connector = null)
    {
        var previousState = _currentState;
        var stateChanged = state != _currentState;
        _currentState = state;

        if (stateChanged)
            _logger.LogInformation("Explorer visual state: {Previous} -> {New}", previousState, state);

        if (connector != null)
        {
            var providerStatus = state switch
            {
                ExplorerVisualState.Connected => CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE,
                ExplorerVisualState.Disconnected => CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_CONNECTIVITY_LOST,
                _ => (CF_SYNC_PROVIDER_STATUS?)null
            };

            if (providerStatus.HasValue)
                connector.UpdateSyncProviderStatus(providerStatus.Value);
        }

        var message = ExplorerVisualStateInfo.GetStatusMessage(state);
        if (message != null)
            SyncRootConnector.ReportSyncStatus(syncRootPath, message);
        else
            SyncRootConnector.ClearSyncStatus(syncRootPath);

        try
        {
            _windowRefresher.NotifyItemChanged(syncRootPath);
            _windowRefresher.NotifyDirectoryChanged(syncRootPath);
            _windowRefresher.RefreshDirectory(syncRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh Explorer for {Path}", syncRootPath);
        }

        if (stateChanged)
            await UpdateIconRegistrationAsync(state, syncRootPath);
    }

    Task IExplorerConnectionStateWriter.SetStateAsync(
        ExplorerVisualState state,
        string syncRootPath,
        CancellationToken ct) =>
        SetStateAsync(state, syncRootPath);

    private async Task UpdateIconRegistrationAsync(ExplorerVisualState state, string syncRootPath)
    {
        if (_registrar == null || string.IsNullOrWhiteSpace(_accountId))
            return;

        try
        {
            await _registrar.ReRegisterWithIconAsync(
                syncRootPath,
                _accountId,
                ExplorerVisualStateInfo.GetIconResource(state));

            var redundantRegistrations = _registrar.GetRedundantCurrentShellNamespaceRegistrations(
                SyncRootRegistrar.GetSyncRootId(_accountId),
                syncRootPath);
            if (redundantRegistrations.Count > 0)
                _registrar.CleanupShellNamespaceRegistrations(redundantRegistrations, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update sync-root icon for {Path}", syncRootPath);
        }
    }
}
