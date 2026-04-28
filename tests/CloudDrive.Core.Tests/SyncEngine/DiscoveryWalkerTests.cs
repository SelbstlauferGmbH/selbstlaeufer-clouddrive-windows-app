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
    public async Task WalkAsync_WhenTrackedLocalFileWasDeletedAndRemoteExists_DownloadsRemoteAgain()
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

        var download = actions.Single(a => a.Type == ReconcileActionType.DownloadChanged);
        download.LocalPath.ShouldBe(localPath);
        download.RemotePath.ShouldBe("/delete-me.txt");
        actions.ShouldNotContain(a => a.Type == ReconcileActionType.DeleteRemote);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenRemoteDeleteConfirmationIsPending_DoesNotDownloadOrDelete()
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
            InSync = false,
            LocalPendingOp = SyncPendingOperations.RemoteDeleteConfirmation
        });

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        var noOp = actions.Single(a => a.Type == ReconcileActionType.NoOp);
        noOp.LocalPath.ShouldBe(localPath);
        noOp.RemotePath.ShouldBe("/delete-me.txt");
        actions.ShouldNotContain(a => a.Type == ReconcileActionType.DeleteRemote);
        actions.ShouldNotContain(a =>
            a.Type == ReconcileActionType.DownloadNew ||
            a.Type == ReconcileActionType.DownloadChanged);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenOnlyJournalRecordRemains_DoesNotEmitDeleteRemote()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        journal.Upsert(new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = Path.Combine(tempDir.Path, "already-gone.txt"),
            RemotePath = "/already-gone.txt",
            ETag = "etag-1",
            Size = 6,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        actions.ShouldNotContain(a => a.Type == ReconcileActionType.DeleteRemote);
        actions.ShouldNotContain(a => string.Equals(a.RemotePath, "/already-gone.txt", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_IgnoresOfficeTransientFiles()
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");

        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "~$report.docx"), "owner");
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "~WRL0001.tmp"), "scratch");
        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "report.docx"), "content");
        webDav.AddFile("/~$remote.docx", "owner", "etag-owner");
        webDav.AddFile("/~WRD0000.tmp", "scratch", "etag-scratch");

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);

        actions.ShouldContain(a => a.Type == ReconcileActionType.UploadNew && a.RemotePath == "/report.docx");
        actions.ShouldNotContain(a => a.LocalPath.Contains("~$", StringComparison.OrdinalIgnoreCase));
        actions.ShouldNotContain(a => a.LocalPath.Contains("~WR", StringComparison.OrdinalIgnoreCase));
        actions.ShouldNotContain(a => a.RemotePath.Contains("~$", StringComparison.OrdinalIgnoreCase));
        actions.ShouldNotContain(a => a.RemotePath.Contains("~WR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalPlaceholderMatchesRemote_EmitsNoOpAndSeedsJournal()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: "etag-1",
            remoteETag: "etag-1",
            localInSync: true,
            logicalSize: 6,
            remoteSize: 6);

        result.Action.Type.ShouldBe(ReconcileActionType.NoOp);
        result.SeededRecord.ShouldNotBeNull();
        result.SeededRecord.ETag.ShouldBe("etag-1");
        result.SeededRecord.BaseETag.ShouldBe("etag-1");
        result.SeededRecord.Size.ShouldBe(6);
        result.SeededRecord.MTimeUtc.ShouldNotBeNull();
        result.SeededRecord.MTimeUtc.Value.ToUniversalTime().ShouldBe(result.LocalMTimeUtc);
        result.SeededRecord.InSync.ShouldBeTrue();
        result.SeededRecord.LocalPendingOp.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalRemoteChanged_EmitsDownloadChangedAndSeedsPlaceholderBaseline()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: "etag-1",
            remoteETag: "etag-2",
            localInSync: true);

        result.Action.Type.ShouldBe(ReconcileActionType.DownloadChanged);
        result.SeededRecord.ShouldNotBeNull();
        result.SeededRecord.ETag.ShouldBe("etag-1");
        result.SeededRecord.BaseETag.ShouldBe("etag-1");
        result.SeededRecord.InSync.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalLocalChanged_EmitsUploadChangedAndSeedsPlaceholderBaseline()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: "etag-1",
            remoteETag: "etag-1",
            localInSync: false);

        result.Action.Type.ShouldBe(ReconcileActionType.UploadChanged);
        result.SeededRecord.ShouldNotBeNull();
        result.SeededRecord.ETag.ShouldBe("etag-1");
        result.SeededRecord.InSync.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalBothLocalAndRemoteChanged_EmitsConflictWithoutSeeding()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: "etag-1",
            remoteETag: "etag-2",
            localInSync: false);

        result.Action.Type.ShouldBe(ReconcileActionType.Conflict);
        result.SeededRecord.ShouldBeNull();
        result.Action.Journal.ShouldNotBeNull();
        result.Action.Journal.ETag.ShouldBe("etag-1");
        result.Action.Journal.BaseETag.ShouldBe("etag-1");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalLocalPlaceholderHasNoRemote_EmitsUploadNewWithoutSeeding()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: "etag-1",
            remoteETag: null,
            localInSync: true,
            includeRemote: false);

        result.Action.Type.ShouldBe(ReconcileActionType.UploadNew);
        result.SeededRecord.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalDirectoryPlaceholderHasRemoteDirectory_EmitsNoOpAndSeedsJournal()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            name: "docs",
            localETag: "local-dir-etag",
            remoteETag: null,
            localInSync: true,
            localIsDirectory: true,
            remoteIsDirectory: true,
            logicalSize: 0,
            remoteSize: 0);

        result.Action.Type.ShouldBe(ReconcileActionType.NoOp);
        result.SeededRecord.ShouldNotBeNull();
        result.SeededRecord.IsDirectory.ShouldBeTrue();
        result.SeededRecord.ETag.ShouldBe("local-dir-etag");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalFilePlaceholderHasNoETag_EmitsConflictWithoutSeeding()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            localETag: null,
            remoteETag: "etag-remote",
            localInSync: true);

        result.Action.Type.ShouldBe(ReconcileActionType.Conflict);
        result.SeededRecord.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task WalkAsync_WhenEmptyJournalLocalFileMatchesRemoteDirectory_EmitsConflictWithoutSeeding()
    {
        var result = await WalkEmptyJournalPlaceholderAsync(
            name: "mixed",
            localETag: "etag-1",
            remoteETag: null,
            localInSync: true,
            localIsDirectory: false,
            remoteIsDirectory: true);

        result.Action.Type.ShouldBe(ReconcileActionType.Conflict);
        result.SeededRecord.ShouldBeNull();
    }

    private static async Task<EmptyJournalWalkResult> WalkEmptyJournalPlaceholderAsync(
        string name = "report.txt",
        string? localETag = "etag-1",
        string? remoteETag = "etag-1",
        bool localInSync = true,
        bool localIsDirectory = false,
        bool remoteIsDirectory = false,
        bool includeRemote = true,
        long logicalSize = 6,
        long remoteSize = 6)
    {
        using var tempDir = new TempDirectory();
        await using var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var mapper = new PathMapper(tempDir.Path, "/");
        var localPath = Path.Combine(tempDir.Path, name);
        var remotePath = "/" + name.Replace('\\', '/');
        var mtimeUtc = DateTime.UtcNow.AddMinutes(-5);

        var create = await vfs.CreatePlaceholderAsync(
            localPath,
            new VfsMetadata(
                "file-1",
                remotePath,
                localETag,
                logicalSize,
                mtimeUtc,
                localIsDirectory,
                InSync: localInSync),
            CancellationToken.None);
        create.Succeeded.ShouldBeTrue();

        if (includeRemote)
        {
            if (remoteIsDirectory)
            {
                webDav.AddDirectory(remotePath);
            }
            else
            {
                webDav.AddFile(remotePath, new byte[remoteSize], remoteETag);
            }
        }

        var walker = new DiscoveryWalker(vfs, journal, webDav, mapper, NullLogger<DiscoveryWalker>.Instance);
        var actions = await walker.WalkAsync(tempDir.Path, depth: 1, CancellationToken.None);
        var action = actions.Single(a => string.Equals(a.LocalPath, localPath, StringComparison.OrdinalIgnoreCase));
        var seededRecord = journal.GetByLocalPath(localPath);

        return new EmptyJournalWalkResult(action, seededRecord, localPath, remotePath, mtimeUtc.ToUniversalTime());
    }

    private sealed record EmptyJournalWalkResult(
        ReconcileAction Action,
        SyncJournalRecord? SeededRecord,
        string LocalPath,
        string RemotePath,
        DateTime LocalMTimeUtc);

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
