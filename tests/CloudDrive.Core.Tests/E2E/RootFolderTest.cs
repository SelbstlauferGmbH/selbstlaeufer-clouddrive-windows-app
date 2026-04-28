using CloudDrive.Core.Data;
using CloudDrive.Core.Tests.Infrastructure;
using Shouldly;

namespace CloudDrive.Core.Tests.E2E;

[Collection("E2E")]  // prevent parallel execution of E2E tests
[Trait("Category", "E2E")]
public class RootFolderTest : IClassFixture<E2ETestFixture>
{
    private readonly E2ETestFixture _fx;

    public RootFolderTest(E2ETestFixture fx)
    {
        _fx = fx;
    }

    // Configurable timeout (default 30s, override via CLOUDDRIVE_TEST_TIMEOUT_SECONDS)
    private static TimeSpan Timeout => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_TIMEOUT_SECONDS"),
            out var s) ? s : 30);

    [Fact]
    public async Task OpenRootFolder_ShouldReturnAllItems()
    {
        // Arrange
        var syncRoot = _fx.SyncRootPath;

        Console.WriteLine($"[RootFolderTest] Opening root folder: {syncRoot}");

        // Act — trigger placeholder population, then wait for it to complete
        Directory.GetFileSystemEntries(syncRoot);
        await _fx.LogSink.WaitForAsync(
            e => e.Message.Contains("TRANSFER_PLACEHOLDERS"), Timeout);

        // Re-read after placeholders are created
        var entries = Directory.GetFileSystemEntries(syncRoot);

        // Assert
        entries.Length.ShouldBeGreaterThan(0, "Root folder should contain items");
        
        Console.WriteLine($"[RootFolderTest] Root folder contains {entries.Length} entries:");
        foreach (var entry in entries)
        {
            var isDir = Directory.Exists(entry);
            Console.WriteLine($"  {(isDir ? "[DIR]" : "[FILE]")} {Path.GetFileName(entry)}");
        }
    }

    [Fact]
    public async Task Startup_WithPreExistingRemoteTree_ProjectsLocalPlaceholdersAndDoesNotDeleteRemote()
    {
        var syncRoot = _fx.SyncRootPath;

        Console.WriteLine($"[RootFolderTest] Validating pre-existing remote seed tree: {_fx.RemoteSeedPrefix}");

        Directory.GetFileSystemEntries(syncRoot);
        await WaitForProjectedAsync(
            _fx.RemoteSeedItems.Where(item => GetParentRelativePath(item.RelativePath).Length == 0),
            Timeout);

        foreach (var directory in _fx.RemoteSeedItems
                     .Where(item => item.IsDirectory)
                     .OrderBy(item => item.RelativePath.Count(ch => ch == '/')))
        {
            var localDirectory = ToLocalPath(syncRoot, directory.RelativePath);
            Directory.Exists(localDirectory).ShouldBeTrue(
                $"Seeded remote directory '{directory.RemotePath}' should be projected locally");

            Directory.GetFileSystemEntries(localDirectory);
            await WaitForProjectedAsync(
                _fx.RemoteSeedItems.Where(item => GetParentRelativePath(item.RelativePath) == directory.RelativePath),
                Timeout);
        }

        foreach (var item in _fx.RemoteSeedItems)
        {
            var localPath = ToLocalPath(syncRoot, item.RelativePath);
            var dbEntry = _fx.Db.GetByLocalPath(localPath);
            dbEntry.ShouldNotBeNull($"Seeded remote item '{item.RemotePath}' should be tracked locally");
            dbEntry!.RemotePath.ShouldBe(item.RemotePath);

            var remote = await _fx.WebDav.GetPropertiesAsync(item.RemotePath);
            remote.ShouldNotBeNull($"Seeded remote item '{item.RemotePath}' must still exist remotely");
            remote!.IsDirectory.ShouldBe(item.IsDirectory);

            if (item.IsDirectory)
            {
                Directory.Exists(localPath).ShouldBeTrue();
                continue;
            }

            File.Exists(localPath).ShouldBeTrue(
                $"Seeded remote file '{item.RemotePath}' should be projected as a local placeholder");
            var content = await File.ReadAllTextAsync(localPath);
            content.ShouldBe(item.Content);
        }

        var seedDeletes = _fx.LogSink.GetByMessage("DELETE")
            .Where(e => e.Category.Contains("WebDav") &&
                        e.Message.Contains(_fx.RemoteSeedPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        seedDeletes.ShouldBeEmpty("Startup must not issue WebDAV DELETE for pre-existing remote seed data");

        var deleteProblems = _fx.Db.GetProblems(openOnly: true)
            .Where(problem => problem.ProblemType == SyncProblemType.RemoteDeleteConfirmation)
            .Where(problem =>
                (problem.LocalPath?.Contains(_fx.RemoteSeedPrefix, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (problem.RemotePath?.Contains(_fx.RemoteSeedPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        deleteProblems.ShouldBeEmpty("Pre-existing remote seed data must not be treated as a local-delete conflict");
    }

    [Fact]
    public async Task ListRootContents_ShouldMatchDatabase()
    {
        // Arrange
        var syncRoot = _fx.SyncRootPath;
        
        // Act - Open root folder to populate
        var entries = Directory.GetFileSystemEntries(syncRoot);
        
        // Wait for TRANSFER_PLACEHOLDERS to complete
        await _fx.LogSink.WaitForAsync(
            e => e.Message.Contains("TRANSFER_PLACEHOLDERS"), Timeout);
        
        // Get database entries
        var dbEntries = _fx.Db.GetChildren(syncRoot);
        
        // Assert
        entries.Length.ShouldBeGreaterThan(0, "Root folder should contain items");
        dbEntries.Count.ShouldBeGreaterThan(0, "Database should contain entries after opening root");
        
        Console.WriteLine($"[RootFolderTest] Explorer entries: {entries.Length}, DB entries: {dbEntries.Count}");
        
        // Verify all explorer entries are in database
        foreach (var entry in entries)
        {
            var dbEntry = dbEntries.FirstOrDefault(e => e.LocalPath == entry);
            dbEntry.ShouldNotBeNull($"Entry '{entry}' should be in database");
        }
    }

    [Fact]
    public void NavigateIntoSubfolder_ShouldReturnContents()
    {
        // Arrange
        var syncRoot = _fx.SyncRootPath;
        
        // First, get root entries
        var rootEntries = Directory.GetFileSystemEntries(syncRoot);
        var subfolders = rootEntries.Where(e => Directory.Exists(e)).ToList();
        
        if (subfolders.Count == 0)
        {
            Console.WriteLine("[RootFolderTest] No subfolders on remote — skipping navigation test");
            return;
        }
        
        // Act - Navigate into first subfolder
        var firstSubfolder = subfolders[0];
        Console.WriteLine($"[RootFolderTest] Navigating into subfolder: {Path.GetFileName(firstSubfolder)}");
        
        var subfolderEntries = Directory.GetFileSystemEntries(firstSubfolder);
        
        // Assert
        // Subfolder may be empty, so we just verify no exception is thrown
        Console.WriteLine($"[RootFolderTest] Subfolder contains {subfolderEntries.Length} entries");
    }

    [Fact]
    public void OpenRootFolder_ShouldCompleteWithinTimeout()
    {
        // Arrange
        var syncRoot = _fx.SyncRootPath;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Act
        var entries = Directory.GetFileSystemEntries(syncRoot);
        stopwatch.Stop();
        
        // Assert
        stopwatch.Elapsed.TotalSeconds.ShouldBeLessThan(10, 
            $"Root folder should open quickly, but took {stopwatch.Elapsed.TotalSeconds:F2}s");
        
        Console.WriteLine($"[RootFolderTest] Root folder opened in {stopwatch.Elapsed.TotalSeconds:F2}s with {entries.Length} entries");
    }

    private async Task WaitForProjectedAsync(IEnumerable<RemoteSeedItem> expectedItems, TimeSpan timeout)
    {
        var expected = expectedItems.ToList();
        if (expected.Count == 0)
            return;

        var deadline = DateTime.UtcNow + timeout;
        List<string> missing = [];
        while (DateTime.UtcNow < deadline)
        {
            missing = expected
                .Where(item => !IsProjected(item))
                .Select(item => item.RelativePath)
                .ToList();

            if (missing.Count == 0)
                return;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Timed out waiting for remote seed item(s) to project locally: {string.Join(", ", missing)}");
    }

    private bool IsProjected(RemoteSeedItem item)
    {
        var localPath = ToLocalPath(_fx.SyncRootPath, item.RelativePath);
        var exists = item.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath);
        return exists && _fx.Db.GetByLocalPath(localPath) != null;
    }

    private static string ToLocalPath(string root, string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([root, ..segments]);
    }

    private static string GetParentRelativePath(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index <= 0 ? string.Empty : relativePath[..index];
    }
}
