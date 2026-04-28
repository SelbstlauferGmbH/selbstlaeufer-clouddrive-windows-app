using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.Tests.Fakes;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public class WebDavLockCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clouddrive-lock-tests", Guid.NewGuid().ToString("N"));

    public WebDavLockCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task OfficeOpen_AcquiresHeldLockAndUploadUsesSameTokenUntilCloseSyncCompletes()
    {
        var localPath = Path.Combine(_root, "Hallo.docx");
        File.WriteAllText(localPath, "hello");
        var webDav = new FakeWebDavService();
        var coordinator = CreateCoordinator(webDav, enabled: true);

        await coordinator.HandleFileOpenAsync(localPath);
        await using (var writeLock = await coordinator.AcquireWriteLockAsync(localPath, "/Hallo.docx"))
        {
            writeLock.Token.ShouldNotBeNull();
            webDav.LockCount.ShouldBe(1);
        }

        await coordinator.HandleFileCloseAsync(localPath);
        await coordinator.NotifyUploadSucceededAsync(localPath, "/Hallo.docx");

        webDav.LockCount.ShouldBe(1);
        webDav.UnlockCount.ShouldBe(1);
        coordinator.Dispose();
    }

    [Fact]
    public async Task Disabled_DoesNotAcquireLocks()
    {
        var localPath = Path.Combine(_root, "Hallo.docx");
        File.WriteAllText(localPath, "hello");
        var webDav = new FakeWebDavService();
        var coordinator = CreateCoordinator(webDav, enabled: false);

        await coordinator.HandleFileOpenAsync(localPath);
        await using var writeLock = await coordinator.AcquireWriteLockAsync(localPath, "/Hallo.docx");

        writeLock.Token.ShouldBeNull();
        webDav.LockCount.ShouldBe(0);
        coordinator.Dispose();
    }

    private WebDavLockCoordinator CreateCoordinator(FakeWebDavService webDav, bool enabled)
    {
        var coordinator = new WebDavLockCoordinator(
            webDav,
            new PathMapper(_root, "/"),
            new NoOpProblemService(),
            NullLogger<WebDavLockCoordinator>.Instance);
        coordinator.SetLockSupport(enabled
            ? WebDavLockSupport.Supported("Test lock support")
            : WebDavLockSupport.Unsupported("Test lock support disabled"));
        return coordinator;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class NoOpProblemService : ISyncProblemService
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
}
