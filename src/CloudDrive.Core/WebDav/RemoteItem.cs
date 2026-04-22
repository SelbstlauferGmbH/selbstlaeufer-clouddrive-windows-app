namespace CloudDrive.Core.WebDav;

public class RemoteItem
{
    public string Name { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string? ETag { get; set; }
    public string? ContentType { get; set; }
}
