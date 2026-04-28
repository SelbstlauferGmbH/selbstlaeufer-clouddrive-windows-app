using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDrive.Core.Data;

namespace CloudDrive.Core.SyncEngine;

public sealed class PropagatorQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SyncStateDb _db;

    public PropagatorQueue(SyncStateDb db)
    {
        _db = db;
    }

    public PropagatorJobRecord Enqueue(ReconcileAction action)
    {
        var job = new PropagatorJobRecord
        {
            OperationId = BuildOperationId(action),
            JobType = Map(action.Type),
            FileId = action.FileId,
            LocalPath = action.LocalPath,
            RemotePath = action.RemotePath,
            PayloadJson = JsonSerializer.Serialize(action, JsonOptions),
            Status = PropagatorJobStatus.Pending
        };

        return _db.EnqueuePropagatorJob(job);
    }

    public IReadOnlyList<PropagatorJobRecord> Lease(int limit, TimeSpan leaseDuration) =>
        _db.LeasePropagatorJobs(limit, leaseDuration);

    public void Complete(string operationId) => _db.CompletePropagatorJob(operationId);

    public void Defer(string operationId, TimeSpan delay, string error) =>
        _db.DeferPropagatorJob(operationId, delay, error);

    public void Fail(string operationId, string error) => _db.FailPropagatorJob(operationId, error);

    public static string BuildOperationId(ReconcileAction action)
    {
        var input = string.Join(
            "|",
            action.Type,
            action.FileId ?? string.Empty,
            action.LocalPath,
            action.RemotePath,
            action.PreviousLocalPath ?? string.Empty,
            action.PreviousRemotePath ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PropagatorJobType Map(ReconcileActionType type) => type switch
    {
        ReconcileActionType.UploadNew => PropagatorJobType.UploadNew,
        ReconcileActionType.UploadChanged => PropagatorJobType.UploadChanged,
        ReconcileActionType.DownloadNew => PropagatorJobType.DownloadNew,
        ReconcileActionType.DownloadChanged => PropagatorJobType.DownloadChanged,
        ReconcileActionType.MoveRemote => PropagatorJobType.MoveRemote,
        ReconcileActionType.MoveLocal => PropagatorJobType.MoveLocal,
        ReconcileActionType.DeleteRemote => PropagatorJobType.DeleteRemote,
        ReconcileActionType.DeleteLocal => PropagatorJobType.DeleteLocal,
        ReconcileActionType.Conflict => PropagatorJobType.Conflict,
        _ => throw new InvalidOperationException($"No queue job type for action {type}.")
    };
}
