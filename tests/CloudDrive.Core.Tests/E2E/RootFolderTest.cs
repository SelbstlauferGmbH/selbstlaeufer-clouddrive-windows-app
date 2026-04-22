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
}
