namespace CloudDrive.Core.SyncEngine;

public static class ConflictCopyNamer
{
    public static string CreateUniqueLocalPath(string localPath)
    {
        var directory = Path.GetDirectoryName(localPath)!;
        var baseName = Path.GetFileNameWithoutExtension(localPath);
        var extension = Path.GetExtension(localPath);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var machineName = SanitizeToken(Environment.MachineName);

        return CreateUniquePath(
            candidateFactory: suffix => Path.Combine(
                directory,
                suffix == 1
                    ? $"{baseName} ({machineName} conflict {stamp}){extension}"
                    : $"{baseName} ({machineName} conflict {stamp} {suffix}){extension}"),
            exists: path => File.Exists(path) || Directory.Exists(path));
    }

    public static async Task<string> CreateUniqueRemotePathAsync(
        string remotePath,
        Func<string, CancellationToken, Task<bool>> existsAsync,
        CancellationToken ct)
    {
        var directory = GetRemoteDirectory(remotePath);
        var fileName = GetRemoteFileName(remotePath);
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var machineName = SanitizeToken(Environment.MachineName);

        var suffix = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var name = suffix == 1
                ? $"{baseName} ({machineName} conflict {stamp}){extension}"
                : $"{baseName} ({machineName} conflict {stamp} {suffix}){extension}";
            var candidate = CombineRemote(directory, name);
            if (!await existsAsync(candidate, ct))
                return candidate;

            suffix++;
        }
    }

    private static string CreateUniquePath(Func<int, string> candidateFactory, Func<string, bool> exists)
    {
        var suffix = 1;
        while (true)
        {
            var candidate = candidateFactory(suffix);
            if (!exists(candidate))
                return candidate;

            suffix++;
        }
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string GetRemoteFileName(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static string CombineRemote(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == "/")
            return "/" + name.TrimStart('/');

        return directory.TrimEnd('/') + "/" + name.TrimStart('/');
    }

    private static string SanitizeToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "computer" : sanitized;
    }
}
