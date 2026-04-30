using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.Watchdog;
using CloudDrive.Watchdog;
using Microsoft.Extensions.Logging;
using Serilog;

AppSettings settings;
try
{
    settings = AppSettings.Load();
}
catch
{
    settings = new AppSettings();
}

AppLocalizer.Instance.Initialize(settings.Language);

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CloudDrive", "logs");

var loggerConfiguration = new LoggerConfiguration().MinimumLevel.Information();

if (settings.EnableFileLogging)
{
    Directory.CreateDirectory(logDir);
    loggerConfiguration = loggerConfiguration.WriteTo.File(
        Path.Combine(logDir, "watchdog-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7);
}

// When CLOUDDRIVE_DEBUG_JSONLOG=1, write a JSON Lines file alongside the app debug log.
if (Environment.GetEnvironmentVariable("CLOUDDRIVE_DEBUG_JSONLOG") == "1")
{
    Directory.CreateDirectory(logDir);
    loggerConfiguration = loggerConfiguration.WriteTo.File(
        new Serilog.Formatting.Json.JsonFormatter(),
        Path.Combine(logDir, "debug-watchdog-.jsonl"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 3);
}

Log.Logger = loggerConfiguration.CreateLogger();

try
{
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.ClearProviders();
        builder.AddSerilog(Log.Logger, dispose: false);
    });

    var appLivenessProbe = new NamedEventWatchdogAppLivenessProbe();

    var runner = new WatchdogCycleRunner(
        appLivenessProbe,
        new WatchdogPendingCleanupProcessor(loggerFactory.CreateLogger<WatchdogPendingCleanupProcessor>()),
        new WebDavWatchdogHealthProbe(
            loggerFactory,
            loggerFactory.CreateLogger<WebDavWatchdogHealthProbe>()),
        new FileWatchdogStatusStore(loggerFactory.CreateLogger<FileWatchdogStatusStore>()),
        loggerFactory.CreateLogger<WatchdogCycleRunner>());

    var snapshot = await runner.RunAsync(settings);

    if (snapshot.ConnectionStatus == WatchdogConnectionStatus.Disconnected &&
        snapshot.DisconnectedReason == WatchdogDisconnectedReason.AppNotRunning)
    {
        var offlineGraceSession = new WatchdogOfflineGraceSession(
            loggerFactory,
            loggerFactory.CreateLogger<WatchdogOfflineGraceSession>(),
            appLivenessProbe);
        await offlineGraceSession.RunAsync(settings);
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Watchdog terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
