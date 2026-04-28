using CloudDrive.Core.Data;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.SyncEngine;

public class SyncProblemService : ISyncProblemService
{
    private readonly SyncStateDb _db;
    private readonly ILogger<SyncProblemService> _logger;

    public SyncProblemService(SyncStateDb db, ILogger<SyncProblemService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public event Action<SyncProblem>? ProblemReported;
    public event Action<string>? ProblemResolved;

    public SyncProblem Report(SyncProblem problem)
    {
        var saved = _db.UpsertProblem(problem);
        _logger.LogWarning(
            "Problem recorded: {ProblemType} {Title} LocalPath={LocalPath} RemotePath={RemotePath} DedupeKey={DedupeKey}",
            saved.ProblemType,
            saved.Title,
            saved.LocalPath,
            saved.RemotePath,
            saved.DedupeKey ?? "<none>");
        ProblemReported?.Invoke(saved);
        return saved;
    }

    public void Resolve(long problemId)
    {
        _db.ResolveProblem(problemId);
        ProblemResolved?.Invoke(problemId.ToString());
    }

    public void ResolveByDedupeKey(string dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
            return;

        _db.ResolveProblemsByDedupeKey(dedupeKey);
        ProblemResolved?.Invoke(dedupeKey);
    }
}
