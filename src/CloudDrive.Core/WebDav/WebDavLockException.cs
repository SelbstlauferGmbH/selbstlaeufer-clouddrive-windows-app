using System.Net;

namespace CloudDrive.Core.WebDav;

public sealed class WebDavLockedException : HttpRequestException
{
    public WebDavLockedException(string remotePath, string message)
        : base(message, null, (HttpStatusCode)423)
    {
        RemotePath = remotePath;
    }

    public string RemotePath { get; }
}
