using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class RemoteChangeDetectorTests
{
    [Fact]
    public async Task ScanAsync_WhenDirectoryListingIsStale_DoesNotDeleteRevalidatedItem()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "test 5176.docx");
        db.Upsert(new SyncItem
        {
            LocalPath = Path.Combine(tempDir.Path, "Win-CASA"),
            RemotePath = "/Win-CASA",
            IsDirectory = true,
            SyncStatus = SyncStatus.Synced
        });
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/test 5176.docx",
            IsDirectory = false,
            FileSize = 13540,
            RemoteETag = "etag-before",
            SyncStatus = SyncStatus.Synced
        });

        webDav
            .Setup(x => x.ListDirectoryAsync("/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "Win-CASA",
                    RemotePath = "/Win-CASA",
                    IsDirectory = true,
                    Size = 0,
                    LastModified = DateTime.UtcNow
                }
            ]);
        webDav
            .Setup(x => x.ListDirectoryAsync("/Win-CASA", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        webDav
            .Setup(x => x.GetPropertiesAsync("/Win-CASA/test 5176.docx", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteItem
            {
                Name = "test 5176.docx",
                RemotePath = "/Win-CASA/test 5176.docx",
                IsDirectory = false,
                Size = 13540,
                LastModified = new DateTime(2026, 3, 27, 12, 14, 58, DateTimeKind.Utc),
                ETag = "etag-after"
            });

        var detector = new RemoteChangeDetector(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<RemoteChangeDetector>.Instance);

        await detector.ScanAsync(CancellationToken.None);

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.SyncStatus.ShouldBe(SyncStatus.Synced);
        item.RemoteETag.ShouldBe("etag-after");

        projectionService.Verify(
            x => x.ScheduleRemoteDeletion(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Never);
        webDav.VerifyAll();
    }

    [Fact]
    public async Task ScanAsync_WhenItemIsMissingAfterRecheck_SchedulesDeletion()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "gone.docx");
        db.Upsert(new SyncItem
        {
            LocalPath = Path.Combine(tempDir.Path, "Win-CASA"),
            RemotePath = "/Win-CASA",
            IsDirectory = true,
            SyncStatus = SyncStatus.Synced
        });
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/gone.docx",
            IsDirectory = false,
            FileSize = 42,
            SyncStatus = SyncStatus.Synced
        });

        webDav
            .Setup(x => x.ListDirectoryAsync("/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "Win-CASA",
                    RemotePath = "/Win-CASA",
                    IsDirectory = true,
                    Size = 0,
                    LastModified = DateTime.UtcNow
                }
            ]);
        webDav
            .Setup(x => x.ListDirectoryAsync("/Win-CASA", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        webDav
            .Setup(x => x.GetPropertiesAsync("/Win-CASA/gone.docx", It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteItem?)null);

        projectionService
            .Setup(x => x.ScheduleRemoteDeletion(localPath, false, Path.Combine(tempDir.Path, "Win-CASA")));

        var detector = new RemoteChangeDetector(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<RemoteChangeDetector>.Instance);

        await detector.ScanAsync(CancellationToken.None);

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.SyncStatus.ShouldBe(SyncStatus.RemoteDeletePendingLocalCleanup);

        projectionService.VerifyAll();
        webDav.VerifyAll();
    }

    [Fact]
    public async Task ScanAsync_WhenRemotePathDiffersOnlyByCase_PersistsCanonicalRemotePath()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "classic-car66.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = Path.Combine(tempDir.Path, "Win-CASA"),
            RemotePath = "/Win-CASA",
            IsDirectory = true,
            SyncStatus = SyncStatus.Synced
        });
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/classic-car66.heic",
            IsDirectory = false,
            FileSize = 1960764,
            RemoteETag = "etag-stable",
            SyncStatus = SyncStatus.Synced
        });

        webDav
            .Setup(x => x.ListDirectoryAsync("/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "Win-CASA",
                    RemotePath = "/Win-CASA",
                    IsDirectory = true,
                    Size = 0,
                    LastModified = DateTime.UtcNow
                }
            ]);
        webDav
            .Setup(x => x.ListDirectoryAsync("/Win-CASA", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "classic-car66.HEIC",
                    RemotePath = "/Win-CASA/classic-car66.HEIC",
                    IsDirectory = false,
                    Size = 1960764,
                    LastModified = new DateTime(2026, 2, 23, 14, 44, 24, DateTimeKind.Utc),
                    ETag = "etag-stable"
                }
            ]);

        var detector = new RemoteChangeDetector(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<RemoteChangeDetector>.Instance);

        await detector.ScanAsync(CancellationToken.None);

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.RemotePath.ShouldBe("/Win-CASA/classic-car66.HEIC");
        item.SyncStatus.ShouldBe(SyncStatus.Synced);

        projectionService.Verify(
            x => x.RefreshDirectory(It.IsAny<string>()),
            Times.Never);
        projectionService.VerifyNoOtherCalls();
        webDav.VerifyAll();
    }

    [Fact]
    public async Task ScanAsync_WhenStoredETagIsQuoted_DoesNotTreatSameRemoteVersionAsChanged()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);

        var localPath = Path.Combine(tempDir.Path, "upload.txt");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/upload.txt",
            IsDirectory = false,
            FileSize = 8,
            RemoteETag = "\"etag-after-upload\"",
            SyncStatus = SyncStatus.Synced
        });

        webDav
            .Setup(x => x.ListDirectoryAsync("/", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "upload.txt",
                    RemotePath = "/upload.txt",
                    IsDirectory = false,
                    Size = 8,
                    LastModified = new DateTime(2026, 4, 24, 4, 12, 0, DateTimeKind.Utc),
                    ETag = "etag-after-upload"
                }
            ]);

        var detector = new RemoteChangeDetector(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<RemoteChangeDetector>.Instance);

        await detector.ScanAsync(CancellationToken.None);

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.SyncStatus.ShouldBe(SyncStatus.Synced);

        projectionService.Verify(
            x => x.UpdatePlaceholderMetadata(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<string?>()),
            Times.Never);
        projectionService.Verify(
            x => x.RefreshDirectory(It.IsAny<string>()),
            Times.Never);
        projectionService.VerifyNoOtherCalls();
        webDav.VerifyAll();
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
