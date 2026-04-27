using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using CloudDrive.Core.WebDav;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudDrive.Core.Tests.SyncEngine;

public class SyncProjectionServiceTests
{
    [Fact]
    public async Task ScheduleMarkInSync_WhenUploadedFileIsNotPlaceholder_ConvertsItToInSyncPlaceholder()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var localPath = Path.Combine(tempDir.Path, "upload.txt");
        var remotePath = "/upload.txt";
        await File.WriteAllTextAsync(localPath, "uploaded");

        var stateService = new SyncItemStateService(db);
        stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = false,
            SyncStatus = SyncStatus.Synced
        });

        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var placeholderManager = new PlaceholderManager(
            webDav.Object,
            stateService,
            new PathMapper(tempDir.Path, "/"),
            NullLogger<PlaceholderManager>.Instance);

        var cloudFileOperations = new Mock<ICloudFileOperations>(MockBehavior.Strict);
        CloudFilePlaceholderState? noState = null;
        cloudFileOperations
            .Setup(x => x.TryGetPlaceholderState(localPath, out noState))
            .Returns(false);

        var converted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloudFileOperations
            .Setup(x => x.TryConvertToPlaceholder(localPath, remotePath, It.IsAny<ILogger?>()))
            .Callback(() => converted.TrySetResult())
            .Returns(true);

        var projectionService = new SyncProjectionService(
            placeholderManager,
            stateService,
            cloudFileOperations.Object,
            NullLogger<SyncProjectionService>.Instance);

        projectionService.ScheduleMarkInSync(localPath);

        await converted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cloudFileOperations.Verify(
            x => x.TryConvertToPlaceholder(localPath, remotePath, It.IsAny<ILogger?>()),
            Times.Once);
        cloudFileOperations.Verify(
            x => x.TrySetInSyncState(It.IsAny<string>(), It.IsAny<ILogger?>()),
            Times.Never);
        webDav.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScheduleMarkInSync_WhenUploadedDirectoryIsNotPlaceholder_ConvertsItToInSyncPlaceholder()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var localPath = Path.Combine(tempDir.Path, "New Folder");
        var remotePath = "/New Folder";
        Directory.CreateDirectory(localPath);

        var stateService = new SyncItemStateService(db);
        stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            IsDirectory = true,
            SyncStatus = SyncStatus.Synced
        });

        var webDav = new Mock<IWebDavService>(MockBehavior.Strict);
        var placeholderManager = new PlaceholderManager(
            webDav.Object,
            stateService,
            new PathMapper(tempDir.Path, "/"),
            NullLogger<PlaceholderManager>.Instance);

        var cloudFileOperations = new Mock<ICloudFileOperations>(MockBehavior.Strict);
        CloudFilePlaceholderState? noState = null;
        cloudFileOperations
            .Setup(x => x.TryGetPlaceholderState(localPath, out noState))
            .Returns(false);

        var converted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cloudFileOperations
            .Setup(x => x.TryConvertToPlaceholder(localPath, remotePath, It.IsAny<ILogger?>()))
            .Callback(() => converted.TrySetResult())
            .Returns(true);

        var projectionService = new SyncProjectionService(
            placeholderManager,
            stateService,
            cloudFileOperations.Object,
            NullLogger<SyncProjectionService>.Instance);

        projectionService.ScheduleMarkInSync(localPath);

        await converted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cloudFileOperations.Verify(
            x => x.TryConvertToPlaceholder(localPath, remotePath, It.IsAny<ILogger?>()),
            Times.Once);
        cloudFileOperations.Verify(
            x => x.TrySetInSyncState(It.IsAny<string>(), It.IsAny<ILogger?>()),
            Times.Never);
        webDav.VerifyNoOtherCalls();
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

            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
