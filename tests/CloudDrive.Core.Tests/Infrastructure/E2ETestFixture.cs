using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.Tests.Infrastructure;

/// <summary>
/// Sets up a real cfapi sync root pointed at a real WebDAV server.
/// Each test class gets an isolated sync root folder, SQLite DB, and log sink.
/// Cleans up everything on Dispose — including remote test files.
/// </summary>
public class E2ETestFixture : IAsyncLifetime
{
    public string SyncRootPath { get; private set; } = "";
    public string TestDataDir { get; private set; } = "";
    public InMemoryLogSink LogSink { get; private set; } = new();
    public SyncCoordinator Coordinator { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public IWebDavService WebDav { get; private set; } = null!;

    /// <summary>Shortcut to the coordinator's internal DB (shared instance).</summary>
    public SyncStateDb Db => Coordinator.Db;

    // Configuration — override via environment variables or CI secrets
    private string WebDavUrl => Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_WEBDAV_URL")
        ?? throw new InvalidOperationException(
            "CLOUDDRIVE_TEST_WEBDAV_URL environment variable is required");
    private string Username => Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_USERNAME")
        ?? throw new InvalidOperationException(
            "CLOUDDRIVE_TEST_USERNAME environment variable is required");
    private string Password => Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_PASSWORD")
        ?? throw new InvalidOperationException(
            "CLOUDDRIVE_TEST_PASSWORD environment variable is required");

    // Configurable timeouts for CI environments
    private static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_TIMEOUT_SECONDS"),
            out var s) ? s : 30);

    public async Task InitializeAsync()
    {
        // 0. Load .env file (repository root) into environment variables
        DotEnvLoader.Load();

        // 1. Create isolated temp directory
        TestDataDir = Path.Combine(Path.GetTempPath(), $"clouddrive-e2e-{Guid.NewGuid():N}");
        SyncRootPath = Path.Combine(TestDataDir, "syncroot");
        Directory.CreateDirectory(SyncRootPath);

        // 2. Configure settings with DataDirectory override so coordinator uses our test DB path
        Settings = new AppSettings
        {
            WebDavUrl = WebDavUrl,
            Username = Username,
            SyncRootPath = SyncRootPath,
            SyncRootAccountIdOverride = $"e2e-{Guid.NewGuid():N}",
            AuthType = AuthType.Basic,
            SyncIntervalSeconds = 15,
            MaxConcurrentTransfers = 2,
            EnableFileLogging = false,  // we use in-memory sink, not file logging
            DataDirectory = TestDataDir // ensures SyncCoordinator creates DB here
        };

        // 3. Create coordinator with in-memory log sink
        LogSink = new InMemoryLogSink();
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddProvider(LogSink);
            b.SetMinimumLevel(LogLevel.Debug);
        });

        // Fix Issue 1: Create HttpClient via WebDavAuthHandler with proper auth
        var handler = WebDavAuthHandler.CreateHandler(Settings, Password);
        var httpClient = new HttpClient(handler);
        WebDav = new WebDavService(httpClient, Settings.WebDavUrl, loggerFactory.CreateLogger<WebDavService>());

        Coordinator = new SyncCoordinator(Settings, WebDav, loggerFactory);

        // Fix Issue 7: Attach state handler BEFORE calling StartAsync
        // so we don't miss the synchronous SyncState.Synced event
        var waitTask = WaitForSyncStateAsync(SyncState.Synced, DefaultTimeout);

        // 4. Start the sync engine (registers sync root, connects cfapi, starts watchers)
        await Coordinator.RegisterAndConnectAsync(CancellationToken.None);
        Coordinator.StartNetworkOperations();

        // 5. Wait for sync engine to signal ready state (event-driven, not Task.Delay)
        await waitTask;
    }

    public async Task DisposeAsync()
    {
        // 1. Stop sync engine (disconnects cfapi) and explicitly unregister the test sync root
        try { await Coordinator.StopAsync(); } catch { /* best effort */ }
        try
        {
            if (!string.IsNullOrWhiteSpace(Coordinator.AccountId))
                Coordinator.Registrar.Unregister(Coordinator.AccountId);
        }
        catch { /* best effort */ }

        // 2. Clean up remote test files (prevent orphaned data on crash/failure)
        try
        {
            // Fix Issue 5: ListAsync → ListDirectoryAsync
            var remoteItems = await WebDav.ListDirectoryAsync("/");
            foreach (var item in remoteItems.Where(i => i.Name.StartsWith("e2e-test-")))
                // Fix Issue 6: item.Path → item.RemotePath
                await WebDav.DeleteAsync(item.RemotePath);
        }
        catch { /* best effort remote cleanup */ }

        // 3. Dispose coordinator (releases DB, file watchers)
        try { Coordinator.Dispose(); } catch { /* best effort */ }

        // 4. Clean up local temp directory
        try { Directory.Delete(TestDataDir, recursive: true); } catch { /* best effort */ }

        LogSink.Dispose();
    }

    /// <summary>Wait for SyncCoordinator to reach a specific state (event-driven).</summary>
    public async Task WaitForSyncStateAsync(SyncState expected, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource();
        var cts = new CancellationTokenSource(timeout);

        // Fix Issue 3: Handler signature is Action<SyncState>, not EventHandler
        void OnStateChanged(SyncState state)
        {
            if (state == expected) tcs.TrySetResult();
        }

        Coordinator.StateChanged += OnStateChanged;
        cts.Token.Register(() => tcs.TrySetException(
            new TimeoutException($"SyncCoordinator did not reach '{expected}' within {timeout.TotalSeconds}s")));

        // Fix Issue 4: Use CurrentState property for immediate check
        if (Coordinator.CurrentState == expected)
            tcs.TrySetResult();

        try { await tcs.Task; }
        finally
        {
            Coordinator.StateChanged -= OnStateChanged;
            cts.Dispose();
        }
    }
}
