using System.Net;

namespace CloudDrive.Core.WebDav;

public class WebDavLockException : HttpRequestException
{
    public WebDavLockException(string remotePath, HttpStatusCode statusCode, string message)
        : base(message, null, statusCode)
    {
        RemotePath = remotePath;
    }

    public string RemotePath { get; }
}

public sealed class WebDavLockedException : WebDavLockException
{
    public WebDavLockedException(string remotePath, string message)
        : base(remotePath, (HttpStatusCode)423, message)
    {
    }
}
