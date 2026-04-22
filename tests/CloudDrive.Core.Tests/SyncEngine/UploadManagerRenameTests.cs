using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class UploadManagerRenameTests
{
    [Fact]
    public async Task ProcessChangeAsync_RenamedTrackedItem_MovesRemoteAndUpdatesState()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/instance_399");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);
        var manager = new UploadManager(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            maxConcurrentTransfers: 1,
            NullLogger<UploadManager>.Instance);

        var oldPath = Path.Combine(tempDir.Path, "Win-CASA", "Ventoy mit Datenpartition.pdf");
        var newPath = Path.Combine(tempDir.Path, "Win-CASA", "Ventoy mit Datenpartition 0002.pdf");
        var oldRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition.pdf";
        var newRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition 0002.pdf";
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        await File.WriteAllTextAsync(newPath, "renamed placeholder");

        db.Upsert(new SyncItem
        {
            LocalPath = oldPath,
            RemotePath = oldRemotePath,
            IsDirectory = false,
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });

        webDav
            .Setup(x => x.MoveAsync(oldRemotePath, newRemotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        projectionService
            .Setup(x => x.UpdatePlaceholderMetadata(
                newPath,
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                newRemotePath))
            .Returns(true);
        projectionService
            .Setup(x => x.ScheduleMarkInSync(newPath));

        await manager.ProcessChangeAsync(
            new FileChangeEvent(FileChangeType.Renamed, newPath, oldPath),
            CancellationToken.None);

        db.GetByLocalPath(oldPath).ShouldBeNull();

        var renamedItem = db.GetByLocalPath(newPath);
        renamedItem.ShouldNotBeNull();
        renamedItem.RemotePath.ShouldBe(newRemotePath);
        renamedItem.SyncStatus.ShouldBe(SyncStatus.Synced);
        renamedItem.LastSynced.ShouldNotBeNull();

        webDav.VerifyAll();
        projectionService.VerifyAll();
    }

    [Fact]
    public async Task ProcessChangeAsync_RenamedUntrackedRemoteItem_MovesUsingMappedPaths()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/instance_399");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);
        var manager = new UploadManager(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            maxConcurrentTransfers: 1,
            NullLogger<UploadManager>.Instance);

        var localDirectory = Path.Combine(tempDir.Path, "Win-CASA");
        Directory.CreateDirectory(localDirectory);

        var oldPath = Path.Combine(localDirectory, "Ventoy mit Datenpartition.pdf");
        var newPath = Path.Combine(localDirectory, "Ventoy mit Datenpartition 0002.pdf");
        await File.WriteAllTextAsync(newPath, "renamed placeholder");
        var expectedOldRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition.pdf";
        var expectedNewRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition 0002.pdf";

        webDav
            .Setup(x => x.GetPropertiesAsync(expectedOldRemotePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteItem
            {
                Name = "Ventoy mit Datenpartition.pdf",
                RemotePath = expectedOldRemotePath,
                IsDirectory = false,
                Size = 252124,
                LastModified = new DateTime(2026, 3, 27, 11, 33, 35, DateTimeKind.Utc),
                ETag = "etag-old"
            });
        webDav
            .Setup(x => x.MoveAsync(expectedOldRemotePath, expectedNewRemotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        projectionService
            .Setup(x => x.UpdatePlaceholderMetadata(
                newPath,
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                expectedNewRemotePath))
            .Returns(true);
        projectionService
            .Setup(x => x.ScheduleMarkInSync(newPath));

        await manager.ProcessChangeAsync(
            new FileChangeEvent(FileChangeType.Renamed, newPath, oldPath),
            CancellationToken.None);

        var movedItem = db.GetByLocalPath(newPath);
        movedItem.ShouldNotBeNull();
        movedItem.RemotePath.ShouldBe(expectedNewRemotePath);
        movedItem.SyncStatus.ShouldBe(SyncStatus.Synced);
        movedItem.RemoteETag.ShouldBe("etag-old");
        movedItem.FileSize.ShouldBe(252124);

        webDav.VerifyAll();
        projectionService.VerifyAll();
    }

    [Fact]
    public async Task ProcessChangeAsync_RenamedUntrackedAndMissingRemote_UploadsNewFile()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/instance_399");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);
        var manager = new UploadManager(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            maxConcurrentTransfers: 1,
            NullLogger<UploadManager>.Instance);

        var localDirectory = Path.Combine(tempDir.Path, "Win-CASA");
        Directory.CreateDirectory(localDirectory);

        var oldPath = Path.Combine(localDirectory, "Ventoy mit Datenpartition.pdf");
        var newPath = Path.Combine(localDirectory, "Ventoy mit Datenpartition 0002.pdf");
        await File.WriteAllTextAsync(newPath, "rename fallback upload test");

        var expectedOldRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition.pdf";
        var expectedRemotePath = "/instance_399/Win-CASA/Ventoy mit Datenpartition 0002.pdf";
        var expectedParentRemotePath = "/instance_399/Win-CASA";

        webDav
            .Setup(x => x.GetPropertiesAsync(expectedOldRemotePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteItem?)null);
        webDav
            .Setup(x => x.CreateDirectoryAsync(expectedParentRemotePath, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        webDav
            .Setup(x => x.UploadFileAsync(expectedRemotePath, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("etag-1");
        projectionService
            .Setup(x => x.ScheduleMarkInSync(newPath));

        await manager.ProcessChangeAsync(
            new FileChangeEvent(FileChangeType.Renamed, newPath, oldPath),
            CancellationToken.None);

        var uploadedItem = db.GetByLocalPath(newPath);
        uploadedItem.ShouldNotBeNull();
        uploadedItem.RemotePath.ShouldBe(expectedRemotePath);
        uploadedItem.SyncStatus.ShouldBe(SyncStatus.Synced);
        uploadedItem.RemoteETag.ShouldBe("etag-1");
        uploadedItem.FileSize.ShouldBe(new FileInfo(newPath).Length);
        uploadedItem.LocalHash.ShouldNotBeNullOrWhiteSpace();

        webDav.VerifyAll();
        projectionService.VerifyAll();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CloudDrive.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
