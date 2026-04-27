namespace CloudDrive.Core.SyncEngine;

public sealed class LocalFileTemporarilyUnavailableException : IOException
{
    public LocalFileTemporarilyUnavailableException(string path, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    public string Path { get; }
}
