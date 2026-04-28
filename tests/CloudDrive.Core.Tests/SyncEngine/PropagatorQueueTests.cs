using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class PropagatorQueueTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Enqueue_DeduplicatesByOperationId_AndLeaseSurvivesRestart()
    {
        using var tempDir = new TempDirectory();
        var dbPath = Path.Combine(tempDir.Path, "syncstate.db");

        var action = new ReconcileAction(
            ReconcileActionType.UploadChanged,
            Path.Combine(tempDir.Path, "file.txt"),
            "/file.txt",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null);

        using (var db = new SyncStateDb(dbPath))
        {
            var queue = new PropagatorQueue(db);
            queue.Enqueue(action);
            queue.Enqueue(action);

            var leased = queue.Lease(limit: 10, leaseDuration: TimeSpan.FromMinutes(5));
            leased.Count.ShouldBe(1);
            leased[0].AttemptCount.ShouldBe(1);
        }

        using (var db = new SyncStateDb(dbPath))
        {
            var queue = new PropagatorQueue(db);
            queue.Lease(limit: 10, leaseDuration: TimeSpan.FromMinutes(5)).ShouldBeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void FailedLease_CanBeLeasedAgainAfterLeaseExpiry()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        var action = new ReconcileAction(
            ReconcileActionType.DownloadNew,
            Path.Combine(tempDir.Path, "file.txt"),
            "/file.txt",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null);

        var enqueued = queue.Enqueue(action);
        queue.Fail(enqueued.OperationId, "network down");

        var leased = queue.Lease(limit: 1, leaseDuration: TimeSpan.FromMilliseconds(1));

        leased.Count.ShouldBe(1);
        leased[0].AttemptCount.ShouldBe(1);
        leased[0].LastError.ShouldBe("network down");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void DeferredLease_IsNotLeasedUntilDelayExpires()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        var action = new ReconcileAction(
            ReconcileActionType.UploadNew,
            Path.Combine(tempDir.Path, "file.txt"),
            "/file.txt",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null);

        var enqueued = queue.Enqueue(action);
        queue.Defer(enqueued.OperationId, TimeSpan.FromSeconds(30), "file locked");

        queue.Lease(limit: 1, leaseDuration: TimeSpan.FromMinutes(5)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Lease_PrioritizesLocalMutationsOverRemoteDownloads()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        queue.Enqueue(new ReconcileAction(
            ReconcileActionType.DownloadNew,
            Path.Combine(tempDir.Path, "remote.txt"),
            "/remote.txt",
            "remote-file",
            Local: null,
            Journal: null,
            Remote: null));

        queue.Enqueue(new ReconcileAction(
            ReconcileActionType.UploadNew,
            Path.Combine(tempDir.Path, "local.txt"),
            "/local.txt",
            "local-file",
            Local: null,
            Journal: null,
            Remote: null));

        var leased = queue.Lease(limit: 1, leaseDuration: TimeSpan.FromMinutes(5));

        leased.Single().JobType.ShouldBe(PropagatorJobType.UploadNew);
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void CompletedOperation_CanBeQueuedAgainForLaterChanges()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        var action = new ReconcileAction(
            ReconcileActionType.UploadChanged,
            Path.Combine(tempDir.Path, "file.txt"),
            "/file.txt",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null);

        var first = queue.Enqueue(action);
        queue.Complete(first.OperationId);

        queue.Enqueue(action);
        var leased = queue.Lease(limit: 1, leaseDuration: TimeSpan.FromMinutes(5));

        leased.Count.ShouldBe(1);
        leased[0].OperationId.ShouldBe(first.OperationId);
        leased[0].Status.ShouldBe(PropagatorJobStatus.Leased);
        leased[0].AttemptCount.ShouldBe(1);
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
