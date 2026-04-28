using CloudDrive.Core.Data;
using CloudDrive.Core.Helpers;
using CloudDrive.Core.Vfs;
using CloudDrive.Core.WebDav;

namespace CloudDrive.Core.SyncEngine;

public static class ConflictDetector
{
    public static bool IsConflict(PlaceholderInfo? local, SyncJournalRecord? journal, RemoteItem? remote)
    {
        if (local == null || journal == null || remote == null || local.IsDirectory || remote.IsDirectory)
            return false;

        return HasLocalChanged(local, journal) && HasRemoteChanged(journal, remote);
    }

    public static bool HasLocalChanged(PlaceholderInfo local, SyncJournalRecord? journal)
    {
        if (journal == null)
            return true;

        if (!local.InSync)
            return true;

        if (local.LogicalSize != journal.Size)
            return true;

        if (journal.MTimeUtc.HasValue &&
            local.MTimeUtc.ToUniversalTime() > journal.MTimeUtc.Value.ToUniversalTime().AddSeconds(1))
        {
            return true;
        }

        return false;
    }

    public static bool HasRemoteChanged(SyncJournalRecord? journal, RemoteItem? remote)
    {
        if (journal == null || remote == null)
            return remote != null;

        if (!string.IsNullOrWhiteSpace(journal.ETag) &&
            !string.IsNullOrWhiteSpace(remote.ETag))
        {
            return !WebDavETag.Equals(journal.ETag, remote.ETag);
        }

        if (journal.MTimeUtc.HasValue && remote.LastModified != DateTime.MinValue &&
            remote.LastModified.ToUniversalTime() > journal.MTimeUtc.Value.ToUniversalTime().AddSeconds(1))
        {
            return true;
        }

        return !remote.IsDirectory && remote.Size != journal.Size;
    }
}
