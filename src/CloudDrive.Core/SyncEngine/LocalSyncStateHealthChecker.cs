using System.Text.Json;
using CloudDrive.Core.Configuration;
using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public sealed record LocalSyncStateHealthCheckResult(
    bool HasSuspiciousState,
    int ActiveOperationCount,
    int SuspiciousOperationCount,
    string? ExampleLocalPath,
    string? ExampleRemotePath,
    string? Reason)
{
    public static LocalSyncStateHealthCheckResult Healthy(int activeOperationCount = 0) =>
        new(false, activeOperationCount, 0, null, null, null);
}

public static class LocalSyncStateHealthChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static LocalSyncStateHealthCheckResult Inspect(AppSettings settings)
    {
        var dataDir = settings.DataDirectory ?? AppSettings.GetDataDirectory();
        var dbPath = Path.Combine(dataDir, "syncstate.db");
        if (!File.Exists(dbPath))
            return LocalSyncStateHealthCheckResult.Healthy();

        using var db = new SyncStateDb(dbPath);
        return Inspect(db);
    }

    public static LocalSyncStateHealthCheckResult Inspect(SyncStateDb db)
    {
        var activeJobs = db.GetActivePropagatorJobs();
        if (activeJobs.Count == 0)
            return LocalSyncStateHealthCheckResult.Healthy();

        var suspiciousJobs = activeJobs.Where(IsSuspicious).ToList();
        if (suspiciousJobs.Count == 0)
            return LocalSyncStateHealthCheckResult.Healthy(activeJobs.Count);

        var example = suspiciousJobs[0];
        return new LocalSyncStateHealthCheckResult(
            true,
            activeJobs.Count,
            suspiciousJobs.Count,
            example.LocalPath,
            example.RemotePath,
            GetReason(example));
    }

    private static bool IsSuspicious(PropagatorJobRecord job)
    {
        if (job.JobType != PropagatorJobType.MoveRemote)
            return false;

        if (IsRemoteSelfMove(job))
            return true;

        return job.Status == PropagatorJobStatus.Failed &&
               job.AttemptCount >= 3 &&
               ContainsNotFound(job.LastError);
    }

    private static string GetReason(PropagatorJobRecord job)
    {
        if (IsRemoteSelfMove(job))
            return "MoveRemoteSelfMove";

        if (job.Status == PropagatorJobStatus.Failed &&
            job.AttemptCount >= 3 &&
            ContainsNotFound(job.LastError))
        {
            return "RepeatedMoveRemoteNotFound";
        }

        return "SuspiciousPropagatorJob";
    }

    private static bool IsRemoteSelfMove(PropagatorJobRecord job)
    {
        if (!TryReadAction(job, out var action) ||
            action.Type != ReconcileActionType.MoveRemote ||
            string.IsNullOrWhiteSpace(action.PreviousRemotePath))
        {
            return false;
        }

        return string.Equals(
            NormalizeRemotePath(action.PreviousRemotePath),
            NormalizeRemotePath(action.RemotePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadAction(PropagatorJobRecord job, out ReconcileAction action)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ReconcileAction>(job.PayloadJson, JsonOptions);
            if (parsed != null)
            {
                action = parsed;
                return true;
            }
        }
        catch (Exception)
        {
        }

        action = ReconcileAction.NoOp(job.LocalPath, job.RemotePath, job.FileId, null, null, null);
        return false;
    }

    private static bool ContainsNotFound(string? error)
    {
        return !string.IsNullOrWhiteSpace(error) &&
               (error.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRemotePath(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').Trim();
        if (normalized.Length > 1)
            normalized = normalized.TrimEnd('/');
        return normalized;
    }
}
