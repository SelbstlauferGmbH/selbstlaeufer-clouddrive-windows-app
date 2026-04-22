using CloudDrive.Core.Helpers;
using Shouldly;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.Tests.SyncEngine;

public class CloudFilePlaceholderStateTests
{
    [Fact]
    public void ShouldHydratePinnedFile_WhenPinnedAndNotFullyOnDisk()
    {
        var state = new CloudFilePlaceholderState(
            @"C:\sync\test.pdf",
            IsDirectory: false,
            FileSize: 1024,
            OnDiskDataSize: 128,
            ValidatedDataSize: 128,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_PINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);

        state.NeedsHydration.ShouldBeTrue();
        state.ShouldHydratePinnedFile.ShouldBeTrue();
        state.ShouldSuppressWatcherChange.ShouldBeFalse();
    }

    [Fact]
    public void ShouldSuppressWatcherChange_ForHydratedPlaceholderWithoutLocalChanges()
    {
        var state = new CloudFilePlaceholderState(
            @"C:\sync\test.pdf",
            IsDirectory: false,
            FileSize: 1024,
            OnDiskDataSize: 1024,
            ValidatedDataSize: 1024,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        state.NeedsHydration.ShouldBeFalse();
        state.ShouldHydratePinnedFile.ShouldBeFalse();
        state.ShouldSuppressWatcherChange.ShouldBeTrue();
    }

    [Fact]
    public void ShouldDehydrateUnpinnedFile_WhenUnpinnedHydratedAndInSync()
    {
        var state = new CloudFilePlaceholderState(
            @"C:\sync\test.pdf",
            IsDirectory: false,
            FileSize: 1024,
            OnDiskDataSize: 1024,
            ValidatedDataSize: 1024,
            ModifiedDataSize: 0,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        state.ShouldDehydrateUnpinnedFile.ShouldBeTrue();
        state.ShouldSuppressWatcherChange.ShouldBeFalse();
    }

    [Fact]
    public void ShouldNotDehydrateUnpinnedFile_WhenPlaceholderHasLocalChanges()
    {
        var state = new CloudFilePlaceholderState(
            @"C:\sync\test.pdf",
            IsDirectory: false,
            FileSize: 1024,
            OnDiskDataSize: 1024,
            ValidatedDataSize: 1024,
            ModifiedDataSize: 64,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);

        state.ShouldDehydrateUnpinnedFile.ShouldBeFalse();
        state.ShouldSuppressWatcherChange.ShouldBeFalse();
    }

    [Fact]
    public void ShouldNotSuppressWatcherChange_ForLocallyModifiedPlaceholder()
    {
        var state = new CloudFilePlaceholderState(
            @"C:\sync\test.pdf",
            IsDirectory: false,
            FileSize: 1024,
            OnDiskDataSize: 1024,
            ValidatedDataSize: 512,
            ModifiedDataSize: 256,
            PinState: CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            InSyncState: CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);

        state.HasLocalContentChanges.ShouldBeTrue();
        state.ShouldHydratePinnedFile.ShouldBeFalse();
        state.ShouldSuppressWatcherChange.ShouldBeFalse();
    }
}
