using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.Helpers;

public sealed record CloudFilePlaceholderState(
    string Path,
    bool IsDirectory,
    long FileSize,
    long OnDiskDataSize,
    long ValidatedDataSize,
    long ModifiedDataSize,
    CF_PIN_STATE PinState,
    CF_IN_SYNC_STATE InSyncState)
{
    public bool IsPinned => PinState == CF_PIN_STATE.CF_PIN_STATE_PINNED;
    public bool IsUnpinned => PinState == CF_PIN_STATE.CF_PIN_STATE_UNPINNED;
    public bool IsInSync => InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
    public bool HasDataOnDisk => OnDiskDataSize > 0;
    public bool NeedsHydration => !IsDirectory && FileSize > OnDiskDataSize;
    public bool HasLocalContentChanges => ModifiedDataSize > 0;
    public bool ShouldHydratePinnedFile => IsPinned && NeedsHydration;
    public bool ShouldDehydrateUnpinnedFile => !IsDirectory && IsUnpinned && HasDataOnDisk && IsInSync && !HasLocalContentChanges;
    public bool ShouldSuppressWatcherChange => !HasLocalContentChanges && !ShouldHydratePinnedFile && !ShouldDehydrateUnpinnedFile;
}

public static class CloudFilePlaceholderHelper
{
    public static bool TryGetPlaceholderState(string path, out CloudFilePlaceholderState? state)
    {
        state = null;

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return false;

            var isDirectory = Directory.Exists(path);
            using var handle = OpenHandle(
                path,
                Kernel32.FileAccess.FILE_READ_ATTRIBUTES,
                isDirectory);

            if (handle.IsInvalid)
                return false;

            var info = CfGetPlaceholderInfo<CF_PLACEHOLDER_STANDARD_INFO>(handle);
            var fileSize = isDirectory ? 0L : new FileInfo(path).Length;

            state = new CloudFilePlaceholderState(
                path,
                isDirectory,
                fileSize,
                info.OnDiskDataSize,
                info.ValidatedDataSize,
                info.ModifiedDataSize,
                info.PinState,
                info.InSyncState);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryHydratePlaceholder(string path, ILogger? logger = null)
    {
        try
        {
            using var handle = OpenHandle(
                path,
                Kernel32.FileAccess.FILE_READ_ATTRIBUTES,
                isDirectory: false);

            if (handle.IsInvalid)
            {
                logger?.LogWarning("Cannot open pinned placeholder for hydration: {Path}", path);
                return false;
            }

            var hr = CfHydratePlaceholder(
                handle,
                0,
                -1,
                CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE,
                IntPtr.Zero);

            if (hr.Failed)
            {
                logger?.LogWarning("CfHydratePlaceholder failed for {Path}: {Hr}", path, hr);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to request hydration for pinned placeholder: {Path}", path);
            return false;
        }
    }

    public static bool TrySetInSyncState(string path, ILogger? logger = null)
    {
        try
        {
            if (!TryGetPlaceholderState(path, out var state) || state is null)
            {
                logger?.LogDebug("Cannot mark in-sync because placeholder state is unavailable: {Path}", path);
                return false;
            }

            if (state.InSyncState == CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC)
            {
                logger?.LogDebug("Placeholder already marked in-sync: {Path}", path);
                return true;
            }

            using var handle = OpenHandle(
                path,
                Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES,
                isDirectory: Directory.Exists(path));

            if (handle.IsInvalid)
            {
                logger?.LogWarning("Cannot open placeholder to mark in-sync: {Path}", path);
                return false;
            }

            var hr = CfSetInSyncState(
                handle,
                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                IntPtr.Zero);

            if (hr.Failed)
            {
                logger?.LogWarning("CfSetInSyncState failed for {Path}: {Hr}", path, hr);
                return false;
            }

            logger?.LogInformation("Marked placeholder in-sync: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to mark placeholder in-sync: {Path}", path);
            return false;
        }
    }

    private static Kernel32.SafeHFILE OpenHandle(string path, Kernel32.FileAccess access, bool isDirectory)
    {
        var flags = FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT;
        if (isDirectory)
            flags |= FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS;

        return Kernel32.CreateFile(
            path,
            access,
            FileShare.ReadWrite | FileShare.Delete,
            null,
            FileMode.Open,
            flags,
            IntPtr.Zero);
    }
}
