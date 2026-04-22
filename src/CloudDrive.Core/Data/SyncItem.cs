namespace CloudDrive.Core.Data;

public class SyncItem
{
    public long Id { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public string? RemoteETag { get; set; }
    public DateTime? RemoteLastModified { get; set; }
    public string? LocalHash { get; set; }
    public SyncStatus SyncStatus { get; set; }
    public DateTime? LastSynced { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
