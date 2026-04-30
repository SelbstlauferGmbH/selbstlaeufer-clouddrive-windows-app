using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class LocalSyncStateHealthCheckerTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Inspect_FlagsMoveRemoteSelfMove()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        queue.Enqueue(new ReconcileAction(
            ReconcileActionType.MoveRemote,
            Path.Combine(tempDir.Path, "file.tmp"),
            "/folder/file.xlsx",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null,
            PreviousLocalPath: Path.Combine(tempDir.Path, "file.xlsx"),
            PreviousRemotePath: "/folder/file.xlsx"));

        var result = LocalSyncStateHealthChecker.Inspect(db);

        result.HasSuspiciousState.ShouldBeTrue();
        result.SuspiciousOperationCount.ShouldBe(1);
        result.Reason.ShouldBe("MoveRemoteSelfMove");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Inspect_FlagsRepeatedFailedMoveRemoteNotFound()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        queue.Enqueue(new ReconcileAction(
            ReconcileActionType.MoveRemote,
            Path.Combine(tempDir.Path, "new.xlsx"),
            "/folder/new.xlsx",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null,
            PreviousLocalPath: Path.Combine(tempDir.Path, "old.xlsx"),
            PreviousRemotePath: "/folder/old.xlsx"));

        for (var i = 0; i < 3; i++)
        {
            var leased = queue.Lease(limit: 1, leaseDuration: TimeSpan.FromMinutes(5)).Single();
            queue.Fail(leased.OperationId, "Response status code does not indicate success: 404 (Not Found).");
        }

        var result = LocalSyncStateHealthChecker.Inspect(db);

        result.HasSuspiciousState.ShouldBeTrue();
        result.SuspiciousOperationCount.ShouldBe(1);
        result.Reason.ShouldBe("RepeatedMoveRemoteNotFound");
    }

    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Inspect_IgnoresNormalPendingDownload()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var queue = new PropagatorQueue(db);

        queue.Enqueue(new ReconcileAction(
            ReconcileActionType.DownloadNew,
            Path.Combine(tempDir.Path, "file.txt"),
            "/file.txt",
            "file-1",
            Local: null,
            Journal: null,
            Remote: null));

        var result = LocalSyncStateHealthChecker.Inspect(db);

        result.HasSuspiciousState.ShouldBeFalse();
        result.ActiveOperationCount.ShouldBe(1);
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
