namespace CloudDrive.Core.SyncEngine;

public interface IFocusedFolderProvider
{
    string? GetCurrentFocusedPath();
}
