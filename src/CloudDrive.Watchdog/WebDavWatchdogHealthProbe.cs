using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.WebDav;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Watchdog;

public sealed class WebDavWatchdogHealthProbe : IWatchdogHealthProbe
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebDavWatchdogHealthProbe> _logger;

    public WebDavWatchdogHealthProbe(
        ILoggerFactory loggerFactory,
        ILogger<WebDavWatchdogHealthProbe> logger)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<WatchdogHealthProbeResult> CheckAsync(AppSettings settings, CancellationToken ct = default)
    {
        var configurationStatus = settings.GetAccountConfigurationStatus(CredentialManager.HasPassword());
        if (!configurationStatus.HasValidWebDavUrl || !configurationStatus.HasUsername)
        {
            return WatchdogHealthProbeResult.Disconnected(
                WatchdogDisconnectedReason.ConfigurationMissing,
                AppLocalizer.Instance.GetString("Watchdog_Status_ConfigurationMissing"));
        }

        if (!configurationStatus.HasPassword)
        {
            return WatchdogHealthProbeResult.Disconnected(
                WatchdogDisconnectedReason.CredentialsMissing,
                AppLocalizer.Instance.GetString("Watchdog_Status_CredentialsMissing"));
        }

        var password = CredentialManager.LoadPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            return WatchdogHealthProbeResult.Disconnected(
                WatchdogDisconnectedReason.CredentialsMissing,
                AppLocalizer.Instance.GetString("Watchdog_Status_CredentialsMissing"));
        }

        try
        {
            using var handler = WebDavAuthHandler.CreateHandler(settings, password);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            using var webDav = new WebDavService(
                httpClient,
                settings.WebDavUrl,
                _loggerFactory.CreateLogger<WebDavService>());

            var result = await webDav.HealthCheckAsync(ct);
            if (result.IsHealthy)
                return WatchdogHealthProbeResult.Connected(result.LatencyMs);

            return result.FailureReason switch
            {
                HealthCheckFailure.ServerUnreachable => WatchdogHealthProbeResult.Disconnected(
                    WatchdogDisconnectedReason.ServerUnreachable,
                    result.ErrorMessage ?? AppLocalizer.Instance.GetString("Watchdog_Status_ServerUnreachable")),
                HealthCheckFailure.HighLatency => WatchdogHealthProbeResult.Disconnected(
                    WatchdogDisconnectedReason.HighLatency,
                    AppLocalizer.Instance.Format("Watchdog_Status_HighLatency", result.LatencyMs),
                    result.LatencyMs),
                HealthCheckFailure.ListingFailed => WatchdogHealthProbeResult.Disconnected(
                    WatchdogDisconnectedReason.ListingFailed,
                    result.ErrorMessage ?? AppLocalizer.Instance.GetString("Watchdog_Status_ListingFailed")),
                _ => WatchdogHealthProbeResult.Disconnected(
                    WatchdogDisconnectedReason.UnexpectedError,
                    result.ErrorMessage ?? AppLocalizer.Instance.GetString("Watchdog_Status_UnexpectedError"))
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebDAV watchdog health probe failed");
            return WatchdogHealthProbeResult.Disconnected(
                WatchdogDisconnectedReason.UnexpectedError,
                ex.Message);
        }
    }
}
