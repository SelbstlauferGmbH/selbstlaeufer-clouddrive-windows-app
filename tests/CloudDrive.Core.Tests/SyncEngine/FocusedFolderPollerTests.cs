using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Tests.Fakes;
using CloudDrive.Core.Vfs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class FocusedFolderPollerTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task PollOnceAsync_WhenNoFocusedFolder_IsIdle()
    {
        using var tempDir = new TempDirectory();
        var poller = CreatePoller(tempDir.Path, focusedPath: null, out var queue);

        var enqueued = await poller.PollOnceAsync(CancellationToken.None);

        enqueued.ShouldBe(0);
        queue.Lease(10, TimeSpan.FromMinutes(1)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task PollOnceAsync_WalksFocusedFolderDepthOneAndQueuesActions()
    {
        using var tempDir = new TempDirectory();
        var focusedPath = Path.Combine(tempDir.Path, "focused");
        Directory.CreateDirectory(focusedPath);

        var poller = CreatePoller(tempDir.Path, focusedPath, out var queue, webDav =>
        {
            webDav.AddFile("/focused/remote.txt", "remote", "etag-1");
        });

        var enqueued = await poller.PollOnceAsync(CancellationToken.None);

        enqueued.ShouldBe(1);
        var leased = queue.Lease(10, TimeSpan.FromMinutes(1));
        leased.Count.ShouldBe(1);
        leased[0].JobType.ShouldBe(PropagatorJobType.DownloadNew);
        leased[0].RemotePath.ShouldBe("/focused/remote.txt");
    }

    private static FocusedFolderPoller CreatePoller(
        string root,
        string? focusedPath,
        out PropagatorQueue queue,
        Action<FakeWebDavService>? configureWebDav = null)
    {
        var webDav = new FakeWebDavService();
        configureWebDav?.Invoke(webDav);
        var db = new SyncStateDb(Path.Combine(root, $"syncstate-{Guid.NewGuid():N}.db"));
        queue = new PropagatorQueue(db);
        var journal = new SyncJournal(db);
        var vfs = new SuffixVfs(root);
        var mapper = new PathMapper(root, "/");
        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        return new FocusedFolderPoller(
            new FakeFocusedFolderProvider(focusedPath),
            walker,
            queue,
            NullLogger<FocusedFolderPoller>.Instance);
    }

    private sealed class FakeFocusedFolderProvider : IFocusedFolderProvider
    {
        private readonly string? _path;

        public FakeFocusedFolderProvider(string? path)
        {
            _path = path;
        }

        public string? GetCurrentFocusedPath() => _path;
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
