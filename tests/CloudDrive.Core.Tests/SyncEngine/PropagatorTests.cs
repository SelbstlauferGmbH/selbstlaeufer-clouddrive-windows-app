using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Tests.Fakes;
using CloudDrive.Core.Vfs;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public sealed class PropagatorTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task UploadNew_Directory_ConvertsToInSyncPlaceholder_AndUpdatesState()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "Folder");
        Directory.CreateDirectory(localPath);

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.UploadNew,
            localPath,
            "/Folder",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null), CancellationToken.None);

        var remote = await webDav.GetPropertiesAsync("/Folder");
        remote.ShouldNotBeNull();
        remote.IsDirectory.ShouldBeTrue();

        var placeholder = await vfs.GetPlaceholderInfoAsync(localPath, CancellationToken.None);
        placeholder.ShouldNotBeNull();
        placeholder.IsDirectory.ShouldBeTrue();
        placeholder.InSync.ShouldBeTrue();

        journal.GetByFileId("file-1")?.RemotePath.ShouldBe("/Folder");
        stateService.GetByLocalPath(localPath)?.SyncStatus.ShouldBe(SyncStatus.Synced);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task MoveRemote_WhenOnlyLegacyStateExists_CreatesJournalAndUpdatesState()
    {
        using var tempDir = new TempDirectory();
        var oldLocalPath = Path.Combine(tempDir.Path, "Old");
        var newLocalPath = Path.Combine(tempDir.Path, "New");
        Directory.CreateDirectory(newLocalPath);

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        stateService.Upsert(new SyncItem
        {
            LocalPath = oldLocalPath,
            RemotePath = "/Old",
            IsDirectory = true,
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });

        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddDirectory("/Old");
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.MoveRemote,
            newLocalPath,
            "/New",
            "legacy-file-id",
            Local: null,
            Journal: null,
            Remote: null,
            PreviousLocalPath: oldLocalPath,
            PreviousRemotePath: "/Old"), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/Old")).ShouldBeNull();
        (await webDav.GetPropertiesAsync("/New")).ShouldNotBeNull();

        stateService.GetByLocalPath(oldLocalPath).ShouldBeNull();
        stateService.GetByLocalPath(newLocalPath)?.RemotePath.ShouldBe("/New");
        journal.GetByFileId("legacy-file-id")?.LocalPath.ShouldBe(newLocalPath);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task MoveRemote_WhenRemotePathIsUnchanged_CompletesWithoutCallingWebDavMove()
    {
        using var tempDir = new TempDirectory();
        var oldLocalPath = Path.Combine(tempDir.Path, "Microsoft Excel Worksheet (neu).xlsx");
        var tempLocalPath = Path.Combine(tempDir.Path, "BBE79172.tmp");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        journal.Upsert(new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = oldLocalPath,
            RemotePath = "/Neuer Ordner/Microsoft Excel Worksheet (neu).xlsx",
            ETag = "etag-1",
            Size = 10,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddFile("/Neuer Ordner/Microsoft Excel Worksheet (neu).xlsx", "server", "etag-1");
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.MoveRemote,
            tempLocalPath,
            "/Neuer Ordner/Microsoft Excel Worksheet (neu).xlsx",
            "file-1",
            Local: null,
            Journal: journal.GetByFileId("file-1"),
            Remote: null,
            PreviousLocalPath: oldLocalPath,
            PreviousRemotePath: "/Neuer Ordner/Microsoft Excel Worksheet (neu).xlsx"), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/Neuer Ordner/Microsoft Excel Worksheet (neu).xlsx")).ShouldNotBeNull();
        journal.GetByFileId("file-1")?.LocalPath.ShouldBe(oldLocalPath);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task DownloadNew_File_CreatesDehydratedPlaceholderWithoutReadingContent()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "remote.docx");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddFile("/remote.docx", "remote content", "etag-1");
        var remote = await webDav.GetPropertiesAsync("/remote.docx");
        remote.ShouldNotBeNull();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.DownloadNew,
            localPath,
            "/remote.docx",
            "file-1",
            Local: null,
            Journal: null,
            Remote: remote), CancellationToken.None);

        File.Exists(localPath).ShouldBeFalse();
        File.Exists(localPath + SuffixVfs.PlaceholderSuffix).ShouldBeTrue();

        var placeholder = await vfs.GetPlaceholderInfoAsync(localPath, CancellationToken.None);
        placeholder.ShouldNotBeNull();
        placeholder.HydrationState.ShouldBe(VfsHydrationState.Dehydrated);
        placeholder.InSync.ShouldBeTrue();

        var record = journal.GetByFileId("file-1");
        record.ShouldNotBeNull();
        record.Checksum.ShouldBeNull();
        stateService.GetByLocalPath(localPath)?.SyncStatus.ShouldBe(SyncStatus.Synced);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task UploadNew_File_UploadsOnlyAfterLocalFileIsAccessible()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "report.docx");
        await File.WriteAllTextAsync(localPath, "content");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        using var locked = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Should.ThrowAsync<LocalFileTemporarilyUnavailableException>(() =>
            propagator.ApplyAsync(new ReconcileAction(
                ReconcileActionType.UploadNew,
                localPath,
                "/report.docx",
                "file-1",
                Local: null,
                Journal: null,
                Remote: null), CancellationToken.None));

        (await webDav.GetPropertiesAsync("/report.docx")).ShouldBeNull();
        journal.GetByRemotePath("/report.docx").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task UploadNew_File_UploadsWithoutServerLocking()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "report.docx");
        await File.WriteAllTextAsync(localPath, "content");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.UploadNew,
            localPath,
            "/report.docx",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/report.docx")).ShouldNotBeNull();
        webDav.LastUploadLockToken.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task UploadNew_File_IgnoresOfficeOwnerFile()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "~$report.docx");
        await File.WriteAllTextAsync(localPath, "owner");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.UploadNew,
            localPath,
            "/~$report.docx",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/~$report.docx")).ShouldBeNull();
        journal.GetByRemotePath("/~$report.docx").ShouldBeNull();
        stateService.GetByLocalPath(localPath).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task UploadNew_WhenJournalAlreadyHasRemotePath_ReusesExistingRecord()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "report.docx");
        await File.WriteAllTextAsync(localPath, "content");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        journal.Upsert(new SyncJournalRecord
        {
            FileId = "existing-file-id",
            LocalPath = localPath,
            RemotePath = "/report.docx",
            ETag = "etag-old",
            Size = 3,
            InSync = true
        });

        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.UploadNew,
            localPath,
            "/report.docx",
            "new-file-id",
            Local: null,
            Journal: null,
            Remote: null), CancellationToken.None);

        journal.GetByFileId("new-file-id").ShouldBeNull();
        var record = journal.GetByFileId("existing-file-id");
        record.ShouldNotBeNull();
        record.RemotePath.ShouldBe("/report.docx");
        record.Size.ShouldBe(new FileInfo(localPath).Length);
        stateService.GetByLocalPath(localPath)?.SyncStatus.ShouldBe(SyncStatus.Synced);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task DeleteRemote_WithoutConfirmation_DoesNotDeleteRemote()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "delete-me.txt");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddFile("/delete-me.txt", "content", "etag-1");
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        var record = new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = localPath,
            RemotePath = "/delete-me.txt",
            ETag = "etag-1",
            Size = 7,
            InSync = true
        };

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.DeleteRemote,
            localPath,
            "/delete-me.txt",
            "file-1",
            Local: null,
            Journal: record,
            Remote: await webDav.GetPropertiesAsync("/delete-me.txt")), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/delete-me.txt")).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task DeleteRemote_WithConfirmation_DeletesRemoteAndTrackedState()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "delete-me.txt");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/delete-me.txt",
            IsDirectory = false,
            FileSize = 7,
            RemoteETag = "etag-1",
            SyncStatus = SyncStatus.Synced,
            LastSynced = DateTime.UtcNow
        });

        var record = new SyncJournalRecord
        {
            FileId = "file-1",
            LocalPath = localPath,
            RemotePath = "/delete-me.txt",
            ETag = "etag-1",
            Size = 7,
            InSync = false,
            LocalPendingOp = SyncPendingOperations.RemoteDeleteConfirmation
        };
        journal.Upsert(record);

        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddFile("/delete-me.txt", "content", "etag-1");
        var propagator = CreatePropagator(vfs, journal, webDav, stateService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.DeleteRemote,
            localPath,
            "/delete-me.txt",
            "file-1",
            Local: null,
            Journal: record,
            Remote: await webDav.GetPropertiesAsync("/delete-me.txt")), CancellationToken.None);

        (await webDav.GetPropertiesAsync("/delete-me.txt")).ShouldBeNull();
        journal.GetByFileId("file-1").ShouldBeNull();
        stateService.GetByLocalPath(localPath).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public async Task Conflict_File_CreatesLocalAndRemoteConflictCopies()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "report.docx");
        await File.WriteAllTextAsync(localPath, "local version");

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);
        var stateService = new SyncItemStateService(db);
        var vfs = new SuffixVfs(tempDir.Path);
        var webDav = new FakeWebDavService();
        webDav.AddFile("/report.docx", "remote version", "etag-remote");
        var remote = await webDav.GetPropertiesAsync("/report.docx");
        remote.ShouldNotBeNull();

        var problemService = new NoopProblemService();
        var propagator = CreatePropagator(vfs, journal, webDav, stateService, problemService);

        await propagator.ApplyAsync(new ReconcileAction(
            ReconcileActionType.Conflict,
            localPath,
            "/report.docx",
            "file-1",
            Local: null,
            Journal: new SyncJournalRecord
            {
                FileId = "file-1",
                LocalPath = localPath,
                RemotePath = "/report.docx",
                ETag = "etag-old",
                Size = 12,
                InSync = true
            },
            Remote: remote), CancellationToken.None);

        var problem = problemService.Problems.Single();
        problem.ConflictCopyPath.ShouldNotBeNullOrWhiteSpace();
        File.Exists(problem.ConflictCopyPath!).ShouldBeTrue();
        (await File.ReadAllTextAsync(problem.ConflictCopyPath!)).ShouldBe("local version");
        Path.GetFileName(problem.ConflictCopyPath!).ShouldContain(Environment.MachineName);
        Path.GetFileName(problem.ConflictCopyPath!).ShouldContain("conflict");

        var remoteCopies = await webDav.ListDirectoryAsync("/");
        var remoteConflictCopy = remoteCopies.Single(item =>
            item.RemotePath.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
            item.RemotePath.Contains("conflict", StringComparison.OrdinalIgnoreCase));
        remoteConflictCopy.RemotePath.ShouldNotBe("/report.docx");

        await using var remoteConflictStream = await webDav.DownloadFileAsync(remoteConflictCopy.RemotePath);
        using var reader = new StreamReader(remoteConflictStream);
        (await reader.ReadToEndAsync()).ShouldBe("local version");
        (await webDav.GetPropertiesAsync("/report.docx")).ShouldNotBeNull();
    }

    private static Propagator CreatePropagator(
        IVfs vfs,
        SyncJournal journal,
        FakeWebDavService webDav,
        ISyncItemStateService stateService,
        NoopProblemService? problemService = null)
    {
        return new Propagator(
            vfs,
            journal,
            webDav,
            problemService ?? new NoopProblemService(),
            NullLogger<Propagator>.Instance,
            stateService);
    }

    private sealed class NoopProblemService : ISyncProblemService
    {
        public event Action<SyncProblem>? ProblemReported;
        public event Action<string>? ProblemResolved;
        public List<SyncProblem> Problems { get; } = [];

        public SyncProblem Report(SyncProblem problem)
        {
            Problems.Add(problem);
            ProblemReported?.Invoke(problem);
            return problem;
        }

        public void Resolve(long problemId)
        {
        }

        public void ResolveByDedupeKey(string dedupeKey)
        {
            ProblemResolved?.Invoke(dedupeKey);
        }
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
