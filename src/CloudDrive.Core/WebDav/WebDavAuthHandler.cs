using System.Net;
using CloudDrive.Core.Configuration;

namespace CloudDrive.Core.WebDav;

public static class WebDavAuthHandler
{
    public static HttpClientHandler CreateHandler(AppSettings settings, string password)
    {
        var handler = new HttpClientHandler
        {
            PreAuthenticate = true,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var uri = new Uri(settings.WebDavUrl);
        var credentialCache = new CredentialCache();

        switch (settings.AuthType)
        {
            case AuthType.Ntlm:
                credentialCache.Add(uri, "NTLM",
                    new NetworkCredential(settings.Username, password));
                break;

            case AuthType.Negotiate:
                credentialCache.Add(uri, "Negotiate",
                    new NetworkCredential(settings.Username, password));
                break;

            case AuthType.Basic:
            default:
                credentialCache.Add(uri, "Basic",
                    new NetworkCredential(settings.Username, password));
                break;
        }

        handler.Credentials = credentialCache;
        return handler;
    }
}
