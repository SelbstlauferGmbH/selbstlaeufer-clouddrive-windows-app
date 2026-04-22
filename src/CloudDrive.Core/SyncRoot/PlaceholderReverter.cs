using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static Vanara.PInvoke.CldApi;

namespace CloudDrive.Core.SyncRoot;

/// <summary>
/// Strips Cloud Files (cfapi) reparse points from placeholder files and directories
/// so they can be deleted normally. Used during reset/cleanup.
/// </summary>
public static class PlaceholderReverter
{
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
    private const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
    private const int REPARSE_DATA_BUFFER_HEADER_SIZE = 8; // ReparseTag (4) + ReparseDataLength (2) + Reserved (2)

    /// <summary>
    /// Reverts all placeholder files and directories under <paramref name="syncRootPath"/>
    /// back to normal filesystem entries by removing cfapi reparse points.
    /// Processes files first, then directories deepest-first.
    /// </summary>
    public static (int reverted, int failed) RevertAll(string syncRootPath, ILogger? logger = null)
    {
        if (!Directory.Exists(syncRootPath))
            return (0, 0);

        int reverted = 0;
        int failed = 0;

        // Collect all entries — separate files from directories
        var files = new List<string>();
        var directories = new List<string>();

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(syncRootPath, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
                    directories.Add(entry);
                else
                    files.Add(entry);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlaceholderReverter: Error enumerating {Path}", syncRootPath);
        }

        // Process files first
        foreach (var file in files)
        {
            if (RevertSingle(file, isDirectory: false, logger))
                reverted++;
            else
                failed++;
        }

        // Process directories deepest-first (longest path = deepest)
        directories.Sort((a, b) => b.Length.CompareTo(a.Length));
        foreach (var dir in directories)
        {
            if (RevertSingle(dir, isDirectory: true, logger))
                reverted++;
            else
                failed++;
        }

        // Finally, revert the sync root directory itself
        if (RevertSingle(syncRootPath, isDirectory: true, logger))
            reverted++;
        else
            failed++;

        logger?.LogInformation("PlaceholderReverter: Reverted {Reverted}, failed {Failed}", reverted, failed);
        return (reverted, failed);
    }

    private static bool RevertSingle(string path, bool isDirectory, ILogger? logger)
    {
        try
        {
            // Skip entries that are not reparse points (not placeholders)
            var attrs = File.GetAttributes(path);
            if (!attrs.HasFlag(FileAttributes.ReparsePoint))
                return true; // Not a placeholder — nothing to do, count as success

            var flags = isDirectory
                ? FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS | FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT
                : FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT | FileFlagsAndAttributes.FILE_FLAG_OPEN_NO_RECALL;

            using var handle = Kernel32.CreateFile(
                path,
                Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES | Kernel32.FileAccess.FILE_WRITE_DATA,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete,
                null,
                System.IO.FileMode.Open,
                flags,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                logger?.LogWarning("PlaceholderReverter: Cannot open {Path} (isDir={IsDir})", path, isDirectory);
                return RemoveReparsePointByPath(path, isDirectory, logger);
            }

            // Tier 1: Try CfRevertPlaceholder
            var hr = CfRevertPlaceholder(handle, CF_REVERT_FLAGS.CF_REVERT_FLAG_NONE, IntPtr.Zero);
            if (hr.Succeeded)
            {
                logger?.LogDebug("PlaceholderReverter: Reverted {Path}", path);
                return true;
            }

            logger?.LogDebug("PlaceholderReverter: CfRevertPlaceholder failed for {Path}: {Hr}, trying FSCTL fallback", path, hr);

            // Tier 2: Fallback to FSCTL_DELETE_REPARSE_POINT
            return RemoveReparsePoint(handle, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlaceholderReverter: Error reverting {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Fallback: re-open the file with different access and try FSCTL_DELETE_REPARSE_POINT.
    /// Used when the initial handle open fails.
    /// </summary>
    private static bool RemoveReparsePointByPath(string path, bool isDirectory, ILogger? logger)
    {
        try
        {
            var flags = isDirectory
                ? FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS | FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT
                : FileFlagsAndAttributes.FILE_FLAG_OPEN_REPARSE_POINT | FileFlagsAndAttributes.FILE_FLAG_OPEN_NO_RECALL;

            using var handle = Kernel32.CreateFile(
                path,
                Kernel32.FileAccess.GENERIC_WRITE,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete,
                null,
                System.IO.FileMode.Open,
                flags,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                logger?.LogWarning("PlaceholderReverter: Cannot open {Path} for FSCTL fallback either", path);
                return false;
            }

            return RemoveReparsePoint(handle, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlaceholderReverter: FSCTL fallback failed for {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Removes the reparse point from an open file handle using FSCTL_DELETE_REPARSE_POINT.
    /// Reads the current reparse tag first, then sends a delete with the matching tag.
    /// </summary>
    private static bool RemoveReparsePoint(Kernel32.SafeHFILE handle, ILogger? logger)
    {
        try
        {
            // Read the current reparse data to get the tag
            var buffer = new byte[16384]; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE
            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                bool ok = Kernel32.DeviceIoControl(
                    handle,
                    FSCTL_GET_REPARSE_POINT,
                    IntPtr.Zero, 0,
                    bufferHandle.AddrOfPinnedObject(), (uint)buffer.Length,
                    out uint bytesReturned);

                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    logger?.LogWarning("PlaceholderReverter: FSCTL_GET_REPARSE_POINT failed: error {Error}", err);
                    return false;
                }

                if (bytesReturned < REPARSE_DATA_BUFFER_HEADER_SIZE)
                {
                    logger?.LogWarning("PlaceholderReverter: FSCTL_GET_REPARSE_POINT returned too few bytes");
                    return false;
                }

                // Extract the reparse tag (first 4 bytes)
                uint reparseTag = BitConverter.ToUInt32(buffer, 0);

                // Build a minimal REPARSE_DATA_BUFFER header for deletion:
                // ReparseTag (4 bytes) + ReparseDataLength=0 (2 bytes) + Reserved=0 (2 bytes)
                var deleteBuffer = new byte[REPARSE_DATA_BUFFER_HEADER_SIZE];
                BitConverter.GetBytes(reparseTag).CopyTo(deleteBuffer, 0);
                // ReparseDataLength and Reserved remain 0

                var deleteHandle = GCHandle.Alloc(deleteBuffer, GCHandleType.Pinned);
                try
                {
                    ok = Kernel32.DeviceIoControl(
                        handle,
                        FSCTL_DELETE_REPARSE_POINT,
                        deleteHandle.AddrOfPinnedObject(), (uint)deleteBuffer.Length,
                        IntPtr.Zero, 0,
                        out _);

                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        logger?.LogWarning("PlaceholderReverter: FSCTL_DELETE_REPARSE_POINT failed: error {Error}", err);
                        return false;
                    }

                    logger?.LogDebug("PlaceholderReverter: Removed reparse point (tag=0x{Tag:X8})", reparseTag);
                    return true;
                }
                finally
                {
                    deleteHandle.Free();
                }
            }
            finally
            {
                bufferHandle.Free();
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PlaceholderReverter: RemoveReparsePoint exception");
            return false;
        }
    }
}
