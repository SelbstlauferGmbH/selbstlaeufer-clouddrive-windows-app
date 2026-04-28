using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public interface ISyncProblemService
{
    event Action<SyncProblem>? ProblemReported;
    event Action<string>? ProblemResolved;

    SyncProblem Report(SyncProblem problem);
    void Resolve(long problemId);
    void ResolveByDedupeKey(string dedupeKey);
}

public static class SyncProblemKeys
{
    public static string Connection(string identifier) => $"connection:{identifier}";

    public static string Conflict(string localPath) => $"conflict:{localPath}";

    public static string Lock(string remotePath) => $"lock:{remotePath}";

    public static string RemoteListing(string remotePath) => $"remote-listing:{remotePath}";

    public static string RemoteSync() => "remote-sync";

    public static string RemoteDeleteConfirmation(string localPath) => $"remote-delete-confirmation:{localPath}";

    public static string Upload(string localPath) => $"upload:{localPath}";

    public static string Download(string localPath) => $"download:{localPath}";
}
