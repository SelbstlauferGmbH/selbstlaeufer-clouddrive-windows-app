using CloudDrive.Core.Localization;

namespace CloudDrive.Core.SyncEngine;

internal sealed class RemoteFileListedButUnavailableException : IOException
{
    public RemoteFileListedButUnavailableException(string localPath, string remotePath, string? contentType = null)
        : base(AppLocalizer.Instance.Format("Exception_RemoteFileNotFound", remotePath))
    {
        LocalPath = localPath;
        RemotePath = remotePath;
        ContentType = contentType;
    }

    public string LocalPath { get; }
    public string RemotePath { get; }
    public string? ContentType { get; }
}
