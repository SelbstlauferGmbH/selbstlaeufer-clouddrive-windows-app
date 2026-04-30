using CloudDrive.Core.Configuration;
using CloudDrive.Core.Localization;
using CloudDrive.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.Watchdog;

public class WatchdogCycleRunnerTests
{
    public WatchdogCycleRunnerTests()
    {
        AppLocalizer.Instance.Initialize(AppLanguage.English);
    }

    [Fact]
    public async Task RunAsync_WhenAppRunningAndHealthy_SavesConnectedSnapshotAndAppendsSuccessMessage()
    {
        var store = new InMemoryWatchdogStatusStore();
        var runner = CreateRunner(
            store,
            appRunning: true,
            cleanupResult: new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.None),
            healthProbeResult: WatchdogHealthProbeResult.Connected(42));

        var syncRootPath = CreateTempSyncRoot();
        try
        {
            var snapshot = await runner.RunAsync(CreateSettings(syncRootPath));

            snapshot.ConnectionStatus.ShouldBe(WatchdogConnectionStatus.Connected);
            snapshot.DisconnectedReason.ShouldBe(WatchdogDisconnectedReason.None);
            snapshot.LatencyMs.ShouldBe(42);
            snapshot.ImportantMessages.ShouldContain(message =>
                message.Message.Contains("connected", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(syncRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenAppIsNotRunning_SetsDisconnectedAndSkipsHealthProbe()
    {
        var store = new InMemoryWatchdogStatusStore();
        var healthProbe = new RecordingHealthProbe(WatchdogHealthProbeResult.Connected(12));
        var runner = CreateRunner(
            store,
            appRunning: false,
            cleanupResult: new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.None),
            healthProbe: healthProbe);

        var syncRootPath = CreateTempSyncRoot();
        try
        {
            var snapshot = await runner.RunAsync(CreateSettings(syncRootPath));

            snapshot.ConnectionStatus.ShouldBe(WatchdogConnectionStatus.Disconnected);
            snapshot.DisconnectedReason.ShouldBe(WatchdogDisconnectedReason.AppNotRunning);
            healthProbe.CallCount.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(syncRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenHealthFailsThenRecovers_PersistsTransitionMessages()
    {
        var store = new InMemoryWatchdogStatusStore();
        var healthProbe = new RecordingHealthProbe(
            WatchdogHealthProbeResult.Disconnected(WatchdogDisconnectedReason.ServerUnreachable),
            WatchdogHealthProbeResult.Connected(18));
        var runner = CreateRunner(
            store,
            appRunning: true,
            cleanupResult: new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.None),
            healthProbe: healthProbe);

        var syncRootPath = CreateTempSyncRoot();
        try
        {
            var settings = CreateSettings(syncRootPath);

            var disconnected = await runner.RunAsync(settings);
            var connected = await runner.RunAsync(settings);

            disconnected.ConnectionStatus.ShouldBe(WatchdogConnectionStatus.Disconnected);
            disconnected.DisconnectedReason.ShouldBe(WatchdogDisconnectedReason.ServerUnreachable);

            connected.ConnectionStatus.ShouldBe(WatchdogConnectionStatus.Connected);
            connected.ImportantMessages.Count.ShouldBe(2);
            connected.ImportantMessages[0].Message.Contains("connected", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            connected.ImportantMessages[1].Message.Contains("server", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(syncRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenCleanupMessagesAccumulate_KeepsOnlyNewestFiveImportantMessages()
    {
        var store = new InMemoryWatchdogStatusStore(new WatchdogStatusSnapshot
        {
            ImportantMessages =
            [
                new(DateTimeOffset.UtcNow.AddMinutes(-6), "msg-1"),
                new(DateTimeOffset.UtcNow.AddMinutes(-5), "msg-2"),
                new(DateTimeOffset.UtcNow.AddMinutes(-4), "msg-3"),
                new(DateTimeOffset.UtcNow.AddMinutes(-3), "msg-4"),
                new(DateTimeOffset.UtcNow.AddMinutes(-2), "msg-5")
            ]
        });
        var runner = CreateRunner(
            store,
            appRunning: true,
            cleanupResult: new WatchdogPendingCleanupResult(
                WatchdogPendingCleanupOutcome.Failed,
                "cleanup-failed"),
            healthProbeResult: WatchdogHealthProbeResult.Connected(15));

        var syncRootPath = CreateTempSyncRoot();
        try
        {
            var snapshot = await runner.RunAsync(CreateSettings(syncRootPath));

            snapshot.ImportantMessages.Count.ShouldBe(5);
            snapshot.ImportantMessages.ShouldContain(message => message.Message == "cleanup-failed");
            snapshot.ImportantMessages.ShouldNotContain(message => message.Message == "msg-1");
        }
        finally
        {
            Directory.Delete(syncRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_PersistsCleanupOutcomeAcrossCycles()
    {
        var store = new InMemoryWatchdogStatusStore();
        var cleanup = new SequencedCleanupProcessor(
            new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.DeletedFolder, "cleanup-success"),
            new WatchdogPendingCleanupResult(WatchdogPendingCleanupOutcome.ExpiredMarkerRemoved, "cleanup-expired"));
        var runner = new WatchdogCycleRunner(
            new StaticAppLivenessProbe(true),
            cleanup,
            new RecordingHealthProbe(WatchdogHealthProbeResult.Connected(12)),
            store,
            NullLogger<WatchdogCycleRunner>.Instance,
            new FakeTimeProvider());

        var syncRootPath = CreateTempSyncRoot();
        try
        {
            var settings = CreateSettings(syncRootPath);
            await runner.RunAsync(settings);
            var second = await runner.RunAsync(settings);

            second.ImportantMessages.ShouldContain(message => message.Message == "cleanup-success");
            second.ImportantMessages.ShouldContain(message => message.Message == "cleanup-expired");
        }
        finally
        {
            Directory.Delete(syncRootPath, recursive: true);
        }
    }

    private static AppSettings CreateSettings(string syncRootPath) => new()
    {
        SyncRootPath = syncRootPath,
        WebDavUrl = "https://example.com/webdav",
        Username = "tester"
    };

    private static string CreateTempSyncRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clouddrive-watchdog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static WatchdogCycleRunner CreateRunner(
        InMemoryWatchdogStatusStore store,
        bool appRunning,
        WatchdogPendingCleanupResult cleanupResult,
        WatchdogHealthProbeResult? healthProbeResult = null,
        RecordingHealthProbe? healthProbe = null)
    {
        return new WatchdogCycleRunner(
            new StaticAppLivenessProbe(appRunning),
            new StaticCleanupProcessor(cleanupResult),
            healthProbe ?? new RecordingHealthProbe(healthProbeResult ?? WatchdogHealthProbeResult.Connected(10)),
            store,
            NullLogger<WatchdogCycleRunner>.Instance,
            new FakeTimeProvider());
    }

    private sealed class InMemoryWatchdogStatusStore : IWatchdogStatusStore
    {
        private WatchdogStatusSnapshot? _snapshot;

        public InMemoryWatchdogStatusStore(WatchdogStatusSnapshot? snapshot = null)
        {
            _snapshot = snapshot;
        }

        public WatchdogStatusSnapshot? Load() => _snapshot;

        public void Save(WatchdogStatusSnapshot snapshot) => _snapshot = snapshot;
    }

    private sealed class StaticAppLivenessProbe : IWatchdogAppLivenessProbe
    {
        private readonly bool _appRunning;

        public StaticAppLivenessProbe(bool appRunning)
        {
            _appRunning = appRunning;
        }

        public bool IsAppRunning() => _appRunning;
    }

    private sealed class RecordingHealthProbe : IWatchdogHealthProbe
    {
        private readonly Queue<WatchdogHealthProbeResult> _results;

        public RecordingHealthProbe(params WatchdogHealthProbeResult[] results)
        {
            _results = new Queue<WatchdogHealthProbeResult>(results);
        }

        public int CallCount { get; private set; }

        public Task<WatchdogHealthProbeResult> CheckAsync(AppSettings settings, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
        }
    }

    private sealed class StaticCleanupProcessor : IWatchdogPendingCleanupProcessor
    {
        private readonly WatchdogPendingCleanupResult _result;

        public StaticCleanupProcessor(WatchdogPendingCleanupResult result)
        {
            _result = result;
        }

        public WatchdogPendingCleanupResult Process() => _result;
    }

    private sealed class SequencedCleanupProcessor : IWatchdogPendingCleanupProcessor
    {
        private readonly Queue<WatchdogPendingCleanupResult> _results;

        public SequencedCleanupProcessor(params WatchdogPendingCleanupResult[] results)
        {
            _results = new Queue<WatchdogPendingCleanupResult>(results);
        }

        public WatchdogPendingCleanupResult Process() =>
            _results.Count > 1 ? _results.Dequeue() : _results.Peek();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _current = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            var value = _current;
            _current = _current.AddMinutes(1);
            return value;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
