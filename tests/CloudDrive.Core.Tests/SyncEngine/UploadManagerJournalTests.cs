using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class UploadManagerJournalTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task ProcessChangeAsync_Rename_UpdatesJournalWithoutChangingFileId()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projection = new Mock<ISyncProjectionService>(MockBehavior.Strict);

        var oldPath = Path.Combine(tempDir.Path, "old.txt");
        var newPath = Path.Combine(tempDir.Path, "new.txt");
        await File.WriteAllTextAsync(newPath, "content");

        db.Upsert(new SyncItem
        {
            LocalPath = oldPath,
            RemotePath = "/old.txt",
            IsDirectory = false,
            FileSize = 7,
            RemoteETag = "etag-1",
            SyncStatus = SyncStatus.Synced
        });
        journal.Upsert(new SyncJournalRecord
        {
            FileId = "stable-file-id",
            LocalPath = oldPath,
            RemotePath = "/old.txt",
            ETag = "etag-1",
            Size = 7,
            InSync = true
        });

        webDav.Setup(x => x.MoveAsync("/old.txt", "/new.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        projection.Setup(x => x.UpdatePlaceholderMetadata(newPath, 7, It.IsAny<DateTime>(), "/new.txt"))
            .Returns(true);
        projection.Setup(x => x.ScheduleMarkInSync(newPath));

        var manager = new UploadManager(
            webDav.Object,
            stateService,
            pathMapper,
            projection.Object,
            1,
            NullLogger<UploadManager>.Instance,
            journal: journal);

        await manager.ProcessChangeAsync(new FileChangeEvent(FileChangeType.Renamed, newPath, oldPath), CancellationToken.None);

        journal.GetByLocalPath(oldPath).ShouldBeNull();
        var record = journal.GetByFileId("stable-file-id");
        record.ShouldNotBeNull();
        record.LocalPath.ShouldBe(newPath);
        record.RemotePath.ShouldBe("/new.txt");

        webDav.VerifyAll();
        projection.VerifyAll();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CloudDrive.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
