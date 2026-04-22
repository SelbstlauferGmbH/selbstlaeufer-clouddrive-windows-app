using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.SyncEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.Tests.SyncEngine;

public class DehydrationHandlerTests
{
    [Fact]
    public void EvaluateDehydrationDecision_AllowsWhenPlaceholderIsInSyncEvenIfTrackedStateIsPendingDownload()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\report.pdf",
            RemotePath = "/report.pdf",
            SyncStatus = SyncStatus.PendingDownload
        };

        var placeholderState = new CloudFilePlaceholderState(
            trackedItem.LocalPath,
            IsDirectory: false,
            FileSize: 128,
            OnDiskDataSize: 128,
            ValidatedDataSize: 128,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        var decision = DehydrationHandler.EvaluateDehydrationDecision(
            trackedItem,
            placeholderState,
            placeholderStateAvailable: true);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("placeholder is marked in-sync");
    }

    [Fact]
    public void EvaluateDehydrationDecision_DeniesWhenPlaceholderHasLocalContentChanges()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\draft.docx",
            RemotePath = "/draft.docx",
            SyncStatus = SyncStatus.Synced
        };

        var placeholderState = new CloudFilePlaceholderState(
            trackedItem.LocalPath,
            IsDirectory: false,
            FileSize: 512,
            OnDiskDataSize: 512,
            ValidatedDataSize: 512,
            ModifiedDataSize: 16,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        var decision = DehydrationHandler.EvaluateDehydrationDecision(
            trackedItem,
            placeholderState,
            placeholderStateAvailable: true);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("local content changes");
    }

    [Fact]
    public void EvaluateDehydrationDecision_DeniesWhenPlaceholderIsNotMarkedInSyncEvenIfTrackedStateIsSynced()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\notes.txt",
            RemotePath = "/notes.txt",
            SyncStatus = SyncStatus.Synced
        };

        var placeholderState = new CloudFilePlaceholderState(
            trackedItem.LocalPath,
            IsDirectory: false,
            FileSize: 64,
            OnDiskDataSize: 64,
            ValidatedDataSize: 64,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            InSyncState: (CF_IN_SYNC_STATE)0);

        var decision = DehydrationHandler.EvaluateDehydrationDecision(
            trackedItem,
            placeholderState,
            placeholderStateAvailable: true);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("not marked in-sync");
    }

    [Fact]
    public void EvaluateDehydrationDecision_AllowsTrackedSyncedFallbackWhenPlaceholderStateIsUnavailable()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\photo.jpg",
            RemotePath = "/photo.jpg",
            SyncStatus = SyncStatus.Synced
        };

        var decision = DehydrationHandler.EvaluateDehydrationDecision(
            trackedItem,
            placeholderState: null,
            placeholderStateAvailable: false);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("tracked item is marked Synced");
    }

    [Fact]
    public void ShouldRepairInSyncProjection_ReturnsTrueForHydratedSyncedPlaceholderWithNoLocalChanges()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\report.pdf",
            RemotePath = "/report.pdf",
            SyncStatus = SyncStatus.Synced
        };

        var placeholderState = new CloudFilePlaceholderState(
            trackedItem.LocalPath,
            IsDirectory: false,
            FileSize: 128,
            OnDiskDataSize: 128,
            ValidatedDataSize: 128,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);

        DehydrationHandler.ShouldRepairInSyncProjection(
            trackedItem,
            placeholderState,
            placeholderStateAvailable: true).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRepairInSyncProjection_ReturnsFalseForCloudOnlyPlaceholder()
    {
        var trackedItem = new SyncItem
        {
            LocalPath = @"C:\CloudDrive\report.pdf",
            RemotePath = "/report.pdf",
            SyncStatus = SyncStatus.CloudOnly
        };

        var placeholderState = new CloudFilePlaceholderState(
            trackedItem.LocalPath,
            IsDirectory: false,
            FileSize: 128,
            OnDiskDataSize: 128,
            ValidatedDataSize: 128,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);

        DehydrationHandler.ShouldRepairInSyncProjection(
            trackedItem,
            placeholderState,
            placeholderStateAvailable: true).ShouldBeFalse();
    }

    [Fact]
    public void TryDehydrateUnpinnedPlaceholder_SchedulesInSyncRepairForHydratedSyncedPlaceholder()
    {
        using var tempDir = new TempDirectory();
        var localPath = Path.Combine(tempDir.Path, "report.pdf");
        File.WriteAllBytes(localPath, [1, 2, 3, 4]);

        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));
        var stateService = new SyncItemStateService(db);
        stateService.Upsert(new SyncItem
        {
            LocalPath = localPath,
            RemotePath = "/report.pdf",
            IsDirectory = false,
            FileSize = 4,
            SyncStatus = SyncStatus.Synced
        });

        var pathMapper = new PathMapper(tempDir.Path, "/");
        var projectionService = new Mock<ISyncProjectionService>(MockBehavior.Strict);
        projectionService.Setup(x => x.ScheduleMarkInSync(localPath));

        var placeholderState = new CloudFilePlaceholderState(
            localPath,
            IsDirectory: false,
            FileSize: 4,
            OnDiskDataSize: 4,
            ValidatedDataSize: 4,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);

        var cloudFileOperations = new Mock<ICloudFileOperations>(MockBehavior.Strict);
        CloudFilePlaceholderState? placeholderStateOut = placeholderState;
        cloudFileOperations
            .Setup(x => x.TryGetPlaceholderState(localPath, out placeholderStateOut))
            .Returns(true);

        var handler = new DehydrationHandler(
            stateService,
            pathMapper,
            NullLogger<DehydrationHandler>.Instance,
            cloudFileOperations.Object,
            projectionService.Object);

        var dehydrated = handler.TryDehydrateUnpinnedPlaceholder(localPath, "startup/reconnect reconciliation");

        dehydrated.ShouldBeFalse();
        projectionService.Verify(x => x.ScheduleMarkInSync(localPath), Times.Once);
        cloudFileOperations.VerifyAll();
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
