using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public interface ISyncProblemService
{
    SyncProblem Report(SyncProblem problem);
    void Resolve(long problemId);
    void ResolveByDedupeKey(string dedupeKey);
}

public static class SyncProblemKeys
{
    public static string Connection(string identifier) => $"connection:{identifier}";

    public static string Conflict(string localPath) => $"conflict:{localPath}";

    public static string RemoteListing(string remotePath) => $"remote-listing:{remotePath}";

    public static string RemoteSync() => "remote-sync";

    public static string Upload(string localPath) => $"upload:{localPath}";

    public static string Download(string localPath) => $"download:{localPath}";
}
