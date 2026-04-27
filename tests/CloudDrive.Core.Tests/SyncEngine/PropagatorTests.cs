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

    private static Propagator CreatePropagator(
        IVfs vfs,
        SyncJournal journal,
        FakeWebDavService webDav,
        ISyncItemStateService stateService)
    {
        return new Propagator(
            vfs,
            journal,
            webDav,
            new NoopProblemService(),
            NullLogger<Propagator>.Instance,
            stateService);
    }

    private sealed class NoopProblemService : ISyncProblemService
    {
        public event Action<SyncProblem>? ProblemReported;
        public event Action<string>? ProblemResolved;

        public SyncProblem Report(SyncProblem problem)
        {
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
