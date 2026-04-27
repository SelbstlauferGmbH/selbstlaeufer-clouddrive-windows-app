using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Tests.Fakes;
using CloudDrive.Core.Vfs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class DiscoveryWalkerTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenLocalPlaceholderWasRenamedWithStableFileId_EmitsMoveRemote()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        webDav.AddFile("/old.txt", "server", "etag-1");
        var oldLocalPath = Path.Combine(tempDir.Path, "old.txt");
        var newLocalPath = Path.Combine(tempDir.Path, "new.txt");

        journal.Upsert(new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = oldLocalPath,
            RemotePath = "/old.txt",
            ETag = "etag-1",
            Size = 6,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        await vfs.CreatePlaceholderAsync(
            newLocalPath,
            new VfsMetadata("file-1", "/new.txt", "etag-1", 6, DateTime.UtcNow, IsDirectory: false),
            CancellationToken.None);

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        var move = actions.Single(a => a.Type == ReconcileActionType.MoveRemote);
        move.LocalPath.ShouldBe(newLocalPath);
        move.RemotePath.ShouldBe("/new.txt");
        move.PreviousLocalPath.ShouldBe(oldLocalPath);
        move.PreviousRemotePath.ShouldBe("/old.txt");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenBothLocalAndRemoteChanged_EmitsConflict()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        var localPath = Path.Combine(tempDir.Path, "report.txt");
        webDav.AddFile("/report.txt", "remote change", "etag-2");
        journal.Upsert(new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = localPath,
            RemotePath = "/report.txt",
            ETag = "etag-1",
            Size = 11,
            MTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            InSync = true
        });

        await File.WriteAllTextAsync(localPath, "local changed");
        await vfs.ConvertToPlaceholderAsync(
            localPath,
            new VfsMetadata("file-1", "/report.txt", "etag-1", 13, DateTime.UtcNow, IsDirectory: false, InSync: false),
            CancellationToken.None);

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        actions.ShouldContain(a => a.Type == ReconcileActionType.Conflict && a.LocalPath == localPath);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenTrackedLocalFileWasDeletedAndRemoteUnchanged_EmitsDeleteRemote()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        webDav.AddFile("/delete-me.txt", "server", "etag-1");
        var localPath = Path.Combine(tempDir.Path, "delete-me.txt");
        journal.Upsert(new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = localPath,
            RemotePath = "/delete-me.txt",
            ETag = "etag-1",
            Size = 6,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        var deleteRemote = actions.Single(a => a.Type == ReconcileActionType.DeleteRemote);
        deleteRemote.LocalPath.ShouldBe(localPath);
        deleteRemote.RemotePath.ShouldBe("/delete-me.txt");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_UsesSyncCollectionWhenServerSupportsIt()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        webDav.AddFile("/remote.txt", "server", "etag-1");

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        webDav.SyncCollectionReportCount.ShouldBe(1);
        actions.ShouldContain(a => a.Type == ReconcileActionType.DownloadNew && a.RemotePath == "/remote.txt");
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
