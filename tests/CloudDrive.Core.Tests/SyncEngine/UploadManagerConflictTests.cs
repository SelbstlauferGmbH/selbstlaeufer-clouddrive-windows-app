using System.Text;
using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class UploadManagerConflictTests
{
    [Fact]
    public async Task ProcessChangeAsync_WhenRemoteChanged_PreservesLocalConflictCopyAndRestoresRemoteVersion()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var problemService = new SyncProblemService(db, NullLogger<SyncProblemService>.Instance);
        var pathMapper = new PathMapper(tempDir.Path, "/instance_399");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);
        var conflictResolver = new ConflictResolver(
            webDav.Object,
            stateService,
            projectionService.Object,
            problemService,
            NullLogger<ConflictResolver>.Instance);
        var manager = new UploadManager(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            maxConcurrentTransfers: 1,
            NullLogger<UploadManager>.Instance,
            conflictResolver,
            problemService);

        var localDirectory = Path.Combine(tempDir.Path, "Win-CASA");
        Directory.CreateDirectory(localDirectory);

        var localPath = Path.Combine(localDirectory, "report.docx");
        var remotePath = "/instance_399/Win-CASA/report.docx";

        await File.WriteAllTextAsync(localPath, "local version");

        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = false,
            FileSize = new FileInfo(localPath).Length,
            RemoteETag = "etag-old",
            RemoteLastModified = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc),
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow.AddMinutes(-10)
        });

        webDav
            .Setup(x => x.GetPropertiesAsync(remotePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteItem
            {
                Name = "report.docx",
                RemotePath = remotePath,
                IsDirectory = false,
                Size = Encoding.UTF8.GetByteCount("remote version"),
                LastModified = new DateTime(2026, 3, 25, 10, 5, 0, DateTimeKind.Utc),
                ETag = "etag-new"
            });
        webDav
            .Setup(x => x.GetPropertiesAsync(
                It.Is<string>(path =>
                    !string.Equals(path, remotePath, StringComparison.OrdinalIgnoreCase) &&
                    path.Contains("conflict", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RemoteItem?)null);
        webDav
            .Setup(x => x.UploadFileAsync(
                It.Is<string>(path =>
                    !string.Equals(path, remotePath, StringComparison.OrdinalIgnoreCase) &&
                    path.Contains("conflict", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("etag-conflict-copy");
        webDav
            .Setup(x => x.DownloadFileAsync(remotePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("remote version")));

        projectionService
            .Setup(x => x.SuppressWatcherEvents(It.IsAny<string>()));
        projectionService
            .Setup(x => x.ScheduleMarkInSync(localPath));

        await manager.ProcessChangeAsync(
            new FileChangeEvent(FileChangeType.Changed, localPath),
            CancellationToken.None);

        var trackedItem = db.GetByLocalPath(localPath);
        trackedItem.ShouldNotBeNull();
        trackedItem.SyncStatus.ShouldBe(SyncStatus.Synced);
        trackedItem.RemoteETag.ShouldBe("etag-new");
        trackedItem.FileSize.ShouldBe(Encoding.UTF8.GetByteCount("remote version"));

        (await File.ReadAllTextAsync(localPath)).ShouldBe("remote version");

        var problem = db.GetProblems(openOnly: true).Single();
        problem.ProblemType.ShouldBe(SyncProblemType.Conflict);
        problem.LocalPath.ShouldBe(localPath);
        problem.ConflictCopyPath.ShouldNotBeNullOrWhiteSpace();
        File.Exists(problem.ConflictCopyPath!).ShouldBeTrue();
        (await File.ReadAllTextAsync(problem.ConflictCopyPath!)).ShouldBe("local version");
        Path.GetFileName(problem.ConflictCopyPath!).ShouldContain("conflict");

        db.GetByLocalPath(problem.ConflictCopyPath!).ShouldBeNull();

        projectionService.Verify(x => x.ScheduleMarkInSync(localPath), Times.Once);
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
                catch
                {
                    return;
                }
            }
        }
    }
}
