using CloudDrive.App.Services;
using CloudDrive.App.ViewModels;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.Infrastructure;
using CloudDrive.Core.Localization;
using CloudDrive.Core.SyncEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.App;

public class SettingsViewModelHealthTests
{
    public SettingsViewModelHealthTests()
    {
        AppLocalizer.Instance.Initialize(AppLanguage.English);
    }

    [Fact]
    public async Task RefreshAsync_IncludesUpdaterHealthCheckWithFailureDetailsAndDropsWatchdogEntry()
    {
        using var harness = new SettingsViewModelHarness(enableFileLogging: false)
        {
            UpdateStatus = new UpdateStatusSnapshot(
                UpdateStatusState.Failed,
                "0.1.0",
                UpdateService.ReleasesPageUrl,
                UpdateService.ReleasesPageUrl,
                new DateTimeOffset(2026, 4, 8, 10, 15, 0, TimeSpan.Zero),
                FailureMessage: "403 Forbidden")
        };

        using var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync();

        viewModel.HealthChecks.Count.ShouldBe(5);
        viewModel.HealthChecks.Select(check => check.ActionId).ShouldNotContain("refresh-watchdog");

        var updaterCheck = viewModel.HealthChecks.Single(check => check.ActionId == "check-updates");
        updaterCheck.Name.ShouldBe(AppLocalizer.Instance.GetString("HealthCheck_Updater_Name"));
        updaterCheck.State.ShouldBe(DashboardHealthState.Error);
        updaterCheck.StatusText.ShouldBe(AppLocalizer.Instance.GetString("HealthCheck_Updater_Status_Failed"));
        updaterCheck.Description.ShouldContain(UpdateService.ReleasesPageUrl);
        updaterCheck.Description.ShouldContain("403 Forbidden");
    }

    [Fact]
    public async Task ExecuteHealthActionAsync_WhenCheckingUpdates_InvokesCallbackAndRefreshesUpdaterState()
    {
        using var harness = new SettingsViewModelHarness(enableFileLogging: false);
        var callbackInvocations = 0;

        using var viewModel = harness.CreateViewModel(
            checkForUpdatesNowAsync: () =>
            {
                callbackInvocations++;
                harness.UpdateStatus = new UpdateStatusSnapshot(
                    UpdateStatusState.Reachable,
                    "0.1.0",
                    UpdateService.ReleasesPageUrl,
                    UpdateService.ReleasesPageUrl,
                    new DateTimeOffset(2026, 4, 8, 11, 45, 0, TimeSpan.Zero));
                return Task.CompletedTask;
            });

        await viewModel.ExecuteHealthActionAsync("check-updates");

        callbackInvocations.ShouldBe(1);

        var updaterCheck = viewModel.HealthChecks.Single(check => check.ActionId == "check-updates");
        updaterCheck.State.ShouldBe(DashboardHealthState.Healthy);
        updaterCheck.StatusText.ShouldBe(AppLocalizer.Instance.GetString("HealthCheck_Updater_Status_Reachable"));
    }

    [Fact]
    public async Task ResetCommand_InvokesLocalResetCallback()
    {
        using var harness = new SettingsViewModelHarness(enableFileLogging: false);
        var callbackInvocations = 0;

        using var viewModel = harness.CreateViewModel(
            resetCallback: () =>
            {
                callbackInvocations++;
                return Task.CompletedTask;
            });

        await viewModel.ResetCommand.ExecuteAsync(null);

        callbackInvocations.ShouldBe(1);
    }

    [Fact]
    public async Task ResetConfigurationCommand_InvokesFullResetCallback()
    {
        using var harness = new SettingsViewModelHarness(enableFileLogging: false);
        var callbackInvocations = 0;

        using var viewModel = harness.CreateViewModel(
            resetConfigurationCallback: () =>
            {
                callbackInvocations++;
                return Task.CompletedTask;
            });

        await viewModel.ResetConfigurationCommand.ExecuteAsync(null);

        callbackInvocations.ShouldBe(1);
    }

    private sealed class SettingsViewModelHarness : IDisposable
    {
        private readonly string _tempDirectory;
        private readonly SyncStateDb _database;

        public SettingsViewModelHarness(bool enableFileLogging)
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"clouddrive-settings-health-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);

            var syncRootPath = Path.Combine(_tempDirectory, "syncroot");
            Directory.CreateDirectory(syncRootPath);

            Settings = new AppSettings
            {
                WebDavUrl = "https://example.com/webdav",
                Username = "tester",
                SyncRootPath = syncRootPath,
                EnableFileLogging = enableFileLogging,
                Language = AppLanguage.English,
                ThemeMode = "Light",
                DataDirectory = _tempDirectory
            };

            _database = new SyncStateDb(
                Path.Combine(_tempDirectory, "syncstate.db"),
                NullLogger<SyncStateDb>.Instance);
        }

        public AppSettings Settings { get; }
        public UpdateStatusSnapshot UpdateStatus { get; set; } =
            UpdateStatusSnapshot.CreateUnknown(UpdateService.ReleasesPageUrl, "0.1.0");

        public SettingsViewModel CreateViewModel(
            Func<Task>? checkForUpdatesNowAsync = null,
            Func<Task>? resetCallback = null,
            Func<Task>? resetConfigurationCallback = null)
        {
            return new SettingsViewModel(
                NullLoggerFactory.Instance,
                new AppDashboardContext(
                    activityTracker: new NoOpActivityTracker(),
                    database: _database,
                    settingsProvider: () => Settings,
                    syncStateProvider: () => SyncState.Synced,
                    isPausedProvider: () => false,
                    webDavProvider: () => null,
                    updateStatusProvider: () => UpdateStatus,
                    openSettings: _ => { },
                    openFolder: () => { },
                    openWebPortal: () => { },
                    syncNowAsync: null,
                    togglePauseResume: () => { },
                    ensureWatchdogScheduledTaskAsync: null,
                    checkForUpdatesNowAsync: checkForUpdatesNowAsync,
                    startedAt: new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Local)),
                resetCallback: resetCallback,
                resetConfigurationCallback: resetConfigurationCallback);
        }

        public void Dispose()
        {
            try
            {
                _database.Dispose();
            }
            catch
            {
            }

            try
            {
                if (Directory.Exists(_tempDirectory))
                    Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class NoOpActivityTracker : IActivityTracker
    {
        public void Record(ActivityCategory category, string message, ActivityStatus status, TimeSpan? duration = null)
        {
        }

        public IActivityScope Begin(string key, ActivityCategory category, string message) => NoOpScope.Instance;
    }

    private sealed class NoOpScope : IActivityScope
    {
        public static NoOpScope Instance { get; } = new();

        public void Complete()
        {
        }

        public void CompleteWithStatus(ActivityStatus status)
        {
        }

        public void Dispose()
        {
        }
    }
}
