using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using System.Runtime.InteropServices;
using System.Text;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.Tests.SyncEngine;

public class HydrationHandlerTests
{
    [Fact]
    public async Task ResolveCanonicalRemotePathFromParentListingAsync_WhenSiblingListingContainsCanonicalPath_UpdatesTrackedItem()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Loose);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "classic-car66.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/classic-car66.heic",
            IsDirectory = false,
            FileSize = 1960764,
            RemoteETag = "etag-before",
            SyncStatus = SyncStatus.CloudOnly
        });

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
                    ETag = "etag-after"
                }
            ]);

        var handler = new HydrationHandler(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<HydrationHandler>.Instance);

        var resolvedPath = await handler.ResolveCanonicalRemotePathFromParentListingAsync(
            localPath,
            "/Win-CASA/classic-car66.heic",
            CancellationToken.None);

        resolvedPath.ShouldBe("/Win-CASA/classic-car66.HEIC");

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.RemotePath.ShouldBe("/Win-CASA/classic-car66.HEIC");
        item.RemoteETag.ShouldBe("etag-after");

        webDav.VerifyAll();
    }

    [Fact]
    public async Task ResolveCanonicalRemotePathFromParentListingAsync_WhenFileIdentityNameIsStale_UsesLocalFileNameAsFallback()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Loose);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "greyhounds-looking-for-a-table-not-more.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/greyhounds-looking-for-a-table-not-more.heic",
            IsDirectory = false,
            FileSize = 1721433,
            RemoteETag = "etag-before",
            SyncStatus = SyncStatus.CloudOnly
        });

        webDav
            .Setup(x => x.ListDirectoryAsync("/Win-CASA", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "greyhounds-looking-for-a-table-not-more.heic",
                    RemotePath = "/Win-CASA/greyhounds-looking-for-a-table-not-more.heic",
                    IsDirectory = false,
                    Size = 1721433,
                    LastModified = new DateTime(2026, 3, 30, 10, 31, 34, DateTimeKind.Utc),
                    ETag = "etag-after"
                }
            ]);

        var handler = new HydrationHandler(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<HydrationHandler>.Instance);

        var resolvedItem = await handler.ResolveCanonicalRemoteItemFromParentListingAsync(
            localPath,
            new[] { "/Win-CASA/greyhounds-looking-for-a-table.heic" },
            CancellationToken.None);

        resolvedItem.ShouldNotBeNull();
        resolvedItem.RemotePath.ShouldBe("/Win-CASA/greyhounds-looking-for-a-table-not-more.heic");

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.RemotePath.ShouldBe("/Win-CASA/greyhounds-looking-for-a-table-not-more.heic");
        item.RemoteETag.ShouldBe("etag-after");

        webDav.VerifyAll();
    }

    [Fact]
    public async Task OpenPriorityDownloadStreamAsync_WhenInitialPathReturnsNotFound_RetriesWithCanonicalPath()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Loose);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "classic-car66.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/classic-car66.heic",
            IsDirectory = false,
            FileSize = 1960764,
            SyncStatus = SyncStatus.CloudOnly
        });

        webDav
            .Setup(x => x.DownloadFilePriorityAsync("/Win-CASA/classic-car66.heic", 0, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Remote file not found: /Win-CASA/classic-car66.heic"));
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
                    ETag = "etag-after"
                }
            ]);
        webDav
            .Setup(x => x.DownloadFilePriorityAsync("/Win-CASA/classic-car66.HEIC", 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3, 4]));

        var handler = new HydrationHandler(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<HydrationHandler>.Instance);

        var result = await handler.OpenPriorityDownloadStreamAsync(
            localPath,
            localPath,
            "/Win-CASA/classic-car66.heic",
            CancellationToken.None);

        result.RemotePath.ShouldBe("/Win-CASA/classic-car66.HEIC");
        result.Stream.ShouldNotBeNull();
        await using (result.Stream)
        {
            var bytes = new byte[4];
            var read = await result.Stream.ReadAsync(bytes, CancellationToken.None);
            read.ShouldBe(4);
            bytes.ShouldBe([1, 2, 3, 4]);
        }

        var item = db.GetByLocalPath(localPath);
        item.ShouldNotBeNull();
        item.RemotePath.ShouldBe("/Win-CASA/classic-car66.HEIC");

        webDav.VerifyAll();
    }

    [Fact]
    public async Task OpenPriorityDownloadStreamAsync_WhenStaleIdentityCandidateFails_RetriesMappedCandidate()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Loose);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "greyhounds-looking-for-a-table-not-more.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/greyhounds-looking-for-a-table-not-more.heic",
            IsDirectory = false,
            FileSize = 1721433,
            SyncStatus = SyncStatus.CloudOnly
        });

        webDav
            .Setup(x => x.DownloadFilePriorityAsync("/Win-CASA/greyhounds-looking-for-a-table.heic", 0, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Remote file not found: /Win-CASA/greyhounds-looking-for-a-table.heic"));
        webDav
            .Setup(x => x.DownloadFilePriorityAsync("/Win-CASA/greyhounds-looking-for-a-table-not-more.heic", 0, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([9, 8, 7]));

        var handler = new HydrationHandler(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<HydrationHandler>.Instance);

        var result = await handler.OpenPriorityDownloadStreamAsync(
            localPath,
            localPath,
            new[] { "/Win-CASA/greyhounds-looking-for-a-table.heic", "/Win-CASA/greyhounds-looking-for-a-table-not-more.heic" },
            CancellationToken.None);

        result.RemotePath.ShouldBe("/Win-CASA/greyhounds-looking-for-a-table-not-more.heic");
        using var stream = result.Stream;
        {
            var bytes = new byte[3];
            var read = await stream.ReadAsync(bytes, CancellationToken.None);
            read.ShouldBe(3);
            bytes.ShouldBe([9, 8, 7]);
        }

        webDav.VerifyAll();
    }

    [Fact]
    public async Task OpenPriorityDownloadStreamAsync_WhenListingStillShowsSamePath_ThrowsListedButUnavailable()
    {
        using var tempDir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "Win-CASA"));
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var stateService = new SyncItemStateService(db);
        var pathMapper = new PathMapper(tempDir.Path, "/");
        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Loose);

        var localPath = Path.Combine(tempDir.Path, "Win-CASA", "classic-car66.heic");
        db.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/Win-CASA/classic-car66.heic",
            IsDirectory = false,
            FileSize = 1960764,
            SyncStatus = SyncStatus.CloudOnly
        });

        webDav
            .Setup(x => x.DownloadFilePriorityAsync("/Win-CASA/classic-car66.heic", 0, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Remote file not found: /Win-CASA/classic-car66.heic"));
        webDav
            .Setup(x => x.ListDirectoryAsync("/Win-CASA", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new RemoteItem
                {
                    Name = "classic-car66.heic",
                    RemotePath = "/Win-CASA/classic-car66.heic",
                    IsDirectory = false,
                    Size = 1960764,
                    LastModified = new DateTime(2026, 2, 23, 14, 44, 24, DateTimeKind.Utc),
                    ContentType = "image/heic"
                }
            ]);

        var handler = new HydrationHandler(
            webDav.Object,
            stateService,
            pathMapper,
            projectionService.Object,
            NullLogger<HydrationHandler>.Instance);

        var ex = await Should.ThrowAsync<RemoteFileListedButUnavailableException>(() =>
            handler.OpenPriorityDownloadStreamAsync(
                localPath,
                localPath,
                "/Win-CASA/classic-car66.heic",
                CancellationToken.None));

        ex.RemotePath.ShouldBe("/Win-CASA/classic-car66.heic");
        ex.ContentType.ShouldBe("image/heic");

        webDav.VerifyAll();
    }

    [Fact]
    public void TryGetRemotePathFromFileIdentity_WhenUtf8IdentityPresent_ReturnsRemotePath()
    {
        var bytes = Encoding.UTF8.GetBytes("/Win-CASA/classic-car66.HEIC");
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        try
        {
            var callbackInfo = new CF_CALLBACK_INFO
            {
                FileIdentity = handle.AddrOfPinnedObject(),
                FileIdentityLength = (uint)bytes.Length
            };

            var remotePath = HydrationHandler.TryGetRemotePathFromFileIdentity(callbackInfo);

            remotePath.ShouldBe("/Win-CASA/classic-car66.HEIC");
        }
        finally
        {
            handle.Free();
        }
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
            {
                return;
            }

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
