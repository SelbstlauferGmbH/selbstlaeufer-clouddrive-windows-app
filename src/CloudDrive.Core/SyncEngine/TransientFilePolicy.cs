namespace CloudDrive.Core.SyncEngine;

public static class TransientFilePolicy
{
    private static readonly string[] OfficeTempPrefixes =
    [
        "~WRD",
        "~WRF",
        "~WRL",
        "~WRS",
        "~DF"
    ];

    public static bool ShouldIgnoreLocalPath(string path, bool isDirectory = false)
    {
        if (isDirectory || string.IsNullOrWhiteSpace(path))
            return false;

        return ShouldIgnoreName(Path.GetFileName(path));
    }

    public static bool ShouldIgnoreRemotePath(string remotePath, bool isDirectory = false)
    {
        if (isDirectory || string.IsNullOrWhiteSpace(remotePath))
            return false;

        var normalized = remotePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return ShouldIgnoreName(name);
    }

    public static bool IsProviderInternalLocalPath(string path, bool isDirectory = false)
    {
        if (isDirectory || string.IsNullOrWhiteSpace(path))
            return false;

        return IsProviderInternalName(Path.GetFileName(path));
    }

    public static bool IsProviderInternalName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.StartsWith(".clouddrive-", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldIgnoreName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (IsProviderInternalName(name))
            return true;

        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(name);
        if (!string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase))
            return false;

        return OfficeTempPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               || name.StartsWith("~", StringComparison.OrdinalIgnoreCase);
    }
}
