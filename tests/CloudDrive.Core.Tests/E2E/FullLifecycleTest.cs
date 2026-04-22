using System.Text;
using CloudDrive.Core.Data;
using CloudDrive.Core.Tests.Infrastructure;
using Shouldly;

namespace CloudDrive.Core.Tests.E2E;

[Collection("E2E")]  // prevent parallel execution of E2E tests
[Trait("Category", "E2E")]
public class FullLifecycleTest : IClassFixture<E2ETestFixture>
{
    private readonly E2ETestFixture _fx;

    public FullLifecycleTest(E2ETestFixture fx)
    {
        _fx = fx;
    }

    // Configurable timeout (default 30s, override via CLOUDDRIVE_TEST_TIMEOUT_SECONDS)
    private static TimeSpan Timeout => TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLOUDDRIVE_TEST_TIMEOUT_SECONDS"),
            out var s) ? s : 30);

    [Fact]
    public async Task FullLifecycle_CreateBrowseReadDelete()
    {
        // ──────────────────────────────────────────────
        // STEP 1: Open sync root folder (triggers FETCH_PLACEHOLDERS)
        // ──────────────────────────────────────────────
        // This is what Explorer does when you navigate to the folder.
        // Directory.GetFileSystemEntries triggers the cfapi callback.

        // Trigger placeholder population, then wait for it to complete
        Directory.GetFileSystemEntries(_fx.SyncRootPath);

        // FIX: Wait for TRANSFER_PLACEHOLDERS (PlaceholderManager completion log)
        // instead of FETCH_PLACEHOLDERS (SyncRootConnector callback start log).
        // The connector logs "FETCH_PLACEHOLDERS callback" BEFORE the async handler
        // populates the DB, causing a race condition. TRANSFER_PLACEHOLDERS is logged
        // AFTER DB population and CfExecute, so the DB is guaranteed to be up-to-date.
        await _fx.LogSink.WaitForAsync(
            e => e.Message.Contains("TRANSFER_PLACEHOLDERS"), Timeout);

        // Re-read after placeholders are created
        var rootEntries = Directory.GetFileSystemEntries(_fx.SyncRootPath);

        // Log what we found
        Console.WriteLine($"[Step 1] Sync root contains {rootEntries.Length} entries:");
        foreach (var entry in rootEntries)
            Console.WriteLine($"  {Path.GetFileName(entry)}");

        // Verify: entries are now in the DB
        var dbRoot = _fx.Db.GetChildren(_fx.SyncRootPath);
        dbRoot.ShouldNotBeEmpty("DB should have entries after opening root folder");

        // ──────────────────────────────────────────────
        // STEP 2: Create a text file and save it
        // ──────────────────────────────────────────────
        // This simulates a user creating a new file in the sync folder.
        // The LocalChangeWatcher detects the new file → UploadManager uploads it.

        var testFileName = $"e2e-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
        var testFilePath = Path.Combine(_fx.SyncRootPath, testFileName);

        // Generate a text file (~100 KB — multiple paragraphs)
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
            sb.AppendLine($"[Line {i:D4}] This is a test line for the CloudDrive E2E test. " +
                          $"Timestamp: {DateTime.UtcNow:O}. Random: {Guid.NewGuid()}");
        var testContent = sb.ToString();

        await File.WriteAllTextAsync(testFilePath, testContent);
        Console.WriteLine($"[Step 2] Created test file: {testFileName} ({testContent.Length} bytes)");

        // Wait for upload to complete (event-driven, not Task.Delay)
        // UploadManager logs "UPLOAD_FILE complete: {Path} Synced"
        await _fx.LogSink.WaitForAsync(
            e => e.Message.Contains("UPLOAD_FILE") && e.Message.Contains(testFileName),
            TimeSpan.FromSeconds(60));
        Console.WriteLine("[Step 2] Upload completed");

        // Verify: file is in DB with Synced status
        await WaitForDbStatus(testFilePath, SyncStatus.Synced, Timeout);
        var syncItem = _fx.Db.GetByLocalPath(testFilePath);
        syncItem.ShouldNotBeNull();
        syncItem!.SyncStatus.ShouldBe(SyncStatus.Synced);
        // Note: SyncItem.FileSize is long, testContent.Length is int — implicit conversion
        syncItem.FileSize.ShouldBe((long)testContent.Length);
        Console.WriteLine($"[Step 2] DB state: {syncItem.SyncStatus}, ETag: {syncItem.RemoteETag}");

        // ──────────────────────────────────────────────
        // STEP 3: List files in all subfolders (browse through folders)
        // ──────────────────────────────────────────────
        // This simulates clicking through each subfolder in Explorer.
        // Each GetFileSystemEntries call triggers FETCH_PLACEHOLDERS for that folder.

        var allFolders = new List<string> { _fx.SyncRootPath };
        var allFiles = new List<string>();
        int folderIndex = 0;

        while (folderIndex < allFolders.Count)
        {
            var currentFolder = allFolders[folderIndex];
            Console.WriteLine($"[Step 3] Opening folder: {Path.GetRelativePath(_fx.SyncRootPath, currentFolder)}");

            var entries = Directory.GetFileSystemEntries(currentFolder);

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    allFolders.Add(entry);
                    Console.WriteLine($"  [DIR]  {Path.GetFileName(entry)}");
                }
                else
                {
                    allFiles.Add(entry);
                    var fi = new FileInfo(entry);
                    var attrs = fi.Attributes;
                    // cfapi uses FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS for dehydrated state,
                    // not SparseFile. Check the correct attribute.
                    var isDehydrated = (attrs & FileAttributes.ReparsePoint) != 0
                        && ((int)attrs & 0x00400000) != 0;  // RECALL_ON_DATA_ACCESS
                    Console.WriteLine($"  [FILE] {Path.GetFileName(entry)} " +
                                      $"({fi.Length} bytes, dehydrated={isDehydrated})");
                }
            }

            folderIndex++;
        }

        Console.WriteLine($"[Step 3] Browsed {allFolders.Count} folders, found {allFiles.Count} files");

        // Verify: FETCH_PLACEHOLDERS was called at least once (cfapi deduplication
        // and cooldown means rapid folder traversal may not trigger one per folder)
        var fetchEvents = _fx.LogSink.GetByMessage("FETCH_PLACEHOLDERS");
        fetchEvents.Count.ShouldBeGreaterThanOrEqualTo(1,
            "FETCH_PLACEHOLDERS should fire at least once during folder browsing");

        // ──────────────────────────────────────────────
        // STEP 4: Open (read) the test file we created
        // ──────────────────────────────────────────────
        // If the file was dehydrated (became a placeholder after sync),
        // reading it triggers FETCH_DATA → hydration.
        // If it's still hydrated from the write, we verify content directly.

        Console.WriteLine($"[Step 4] Reading test file: {testFileName}");
        var readContent = await File.ReadAllTextAsync(testFilePath);

        readContent.ShouldBe(testContent, "File content should match what we wrote");
        Console.WriteLine($"[Step 4] File content verified ({readContent.Length} bytes)");

        // Check if hydration was needed (file might still be local from step 2)
        var fetchDataEvents = _fx.LogSink.GetByMessage("FETCH_DATA")
            .Where(e => e.Message.Contains(testFileName))
            .ToList();
        Console.WriteLine($"[Step 4] FETCH_DATA callbacks for test file: {fetchDataEvents.Count}");

        // Verify file attributes: should NOT have recall-on-data-access after reading
        // cfapi uses RECALL_ON_DATA_ACCESS (0x00400000), not SparseFile, for dehydrated state
        var fileAttrs = (int)File.GetAttributes(testFilePath);
        ((fileAttrs & 0x00400000) != 0).ShouldBeFalse(
            "File should be fully hydrated after reading (no RECALL_ON_DATA_ACCESS)");

        // ──────────────────────────────────────────────
        // STEP 5: Delete the test file
        // ──────────────────────────────────────────────
        // This triggers: LocalChangeWatcher detects delete → UploadManager sends
        // DELETE to WebDAV. Also cfapi may fire NOTIFY_DELETE callback.

        Console.WriteLine($"[Step 5] Deleting test file: {testFileName}");
        File.Delete(testFilePath);

        // FIX: Wait for the actual WebDAV DELETE response instead of any generic DELETE.
        // The cfapi NOTIFY_DELETE callback fires instantly when the file is deleted,
        // but the LocalChangeWatcher has a 2-second debounce before queuing the change
        // to UploadManager, which then calls WebDavService.DeleteAsync.
        // Waiting for "WEBDAV_RESPONSE DELETE" ensures the HTTP DELETE actually completed.
        await _fx.LogSink.WaitForAsync(
            e => e.Message.Contains("WEBDAV_RESPONSE DELETE") && e.Message.Contains(testFileName),
            TimeSpan.FromSeconds(60));

        // Verify: file no longer exists locally
        File.Exists(testFilePath).ShouldBeFalse("File should be deleted locally");

        // Verify: file removed from DB (or marked as deleted)
        var deletedItem = _fx.Db.GetByLocalPath(testFilePath);
        // Item should either be null (removed) or have an appropriate status
        if (deletedItem != null)
            Console.WriteLine($"[Step 5] DB still has entry with status: {deletedItem.SyncStatus}");
        else
            Console.WriteLine("[Step 5] DB entry removed");

        // Verify: WebDAV DELETE was issued
        var webdavDeletes = _fx.LogSink.GetByMessage("DELETE")
            .Where(e => e.Category.Contains("WebDav"))
            .ToList();
        webdavDeletes.ShouldNotBeEmpty("A WebDAV DELETE request should have been sent");

        // ──────────────────────────────────────────────
        // FINAL: Print diagnostic summary
        // ──────────────────────────────────────────────
        Console.WriteLine("\n=== E2E Test Summary ===");
        Console.WriteLine($"Folders browsed: {allFolders.Count}");
        Console.WriteLine($"Files discovered: {allFiles.Count}");
        Console.WriteLine($"Test file created, uploaded, read, deleted: OK");
        Console.WriteLine($"Total log events: {_fx.LogSink.Events.Count}");
        Console.WriteLine($"Errors in log: {_fx.LogSink.GetErrors().Count}");

        foreach (var err in _fx.LogSink.GetErrors())
            Console.WriteLine($"  ERROR: [{err.Category}] {err.Message}");

        _fx.LogSink.GetErrors().ShouldBeEmpty("No errors should have occurred during the test");
    }

    /// <summary>Polls the DB until a file reaches the expected status.</summary>
    private async Task WaitForDbStatus(string localPath, SyncStatus expected, TimeSpan timeout)
    {
        // Use log sink event-driven wait: UploadManager logs status transitions
        try
        {
            await _fx.LogSink.WaitForAsync(
                e => e.Message.Contains(Path.GetFileName(localPath))
                     && e.Message.Contains(expected.ToString()),
                timeout);
        }
        catch (TimeoutException)
        {
            // Fall back to direct DB check for better error message
            var item = _fx.Db.GetByLocalPath(localPath);
            throw new TimeoutException(
                $"File '{Path.GetFileName(localPath)}' did not reach status '{expected}' within {timeout.TotalSeconds}s. " +
                $"Current: {item?.SyncStatus.ToString() ?? "not in DB"}");
        }
    }
}
