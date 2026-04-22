using CloudDrive.Core.Configuration;

namespace CloudDrive.Core.SyncRoot;

public static class SyncRootAccountIdResolver
{
    public static string Resolve(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SyncRootAccountIdOverride))
            return settings.SyncRootAccountIdOverride.Trim();

        if (TryResolve(settings, out var accountId))
            return accountId!;

        throw new InvalidOperationException("WebDAV URL is missing or invalid.");
    }

    public static bool TryResolve(AppSettings settings, out string? accountId)
    {
        if (!string.IsNullOrWhiteSpace(settings.SyncRootAccountIdOverride))
        {
            accountId = settings.SyncRootAccountIdOverride.Trim();
            return true;
        }

        if (settings.TryGetWebDavUri(out var uri))
        {
            accountId = uri.Host;
            return true;
        }

        accountId = null;
        return false;
    }
}
