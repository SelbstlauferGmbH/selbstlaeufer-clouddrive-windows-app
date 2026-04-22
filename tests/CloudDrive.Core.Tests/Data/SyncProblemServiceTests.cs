using CloudDrive.Core.Data;
using CloudDrive.Core.SyncEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CloudDrive.Core.Tests.Data;

public class SyncProblemServiceTests
{
    [Fact]
    public void Report_WithSameDedupeKey_UpdatesExistingOpenProblem()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));
        var service = new SyncProblemService(db, NullLogger<SyncProblemService>.Instance);

        var first = service.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Upload(@"C:\sync\report.docx"),
            ProblemType = SyncProblemType.Upload,
            Severity = SyncProblemSeverity.Error,
            Title = "Could not sync report.docx",
            Summary = "First failure",
            Details = "timeout"
        });

        var second = service.Report(new SyncProblem
        {
            DedupeKey = SyncProblemKeys.Upload(@"C:\sync\report.docx"),
            ProblemType = SyncProblemType.Upload,
            Severity = SyncProblemSeverity.Error,
            Title = "Could not sync report.docx",
            Summary = "Second failure",
            Details = "forbidden"
        });

        var openProblems = db.GetProblems(openOnly: true);
        openProblems.Count.ShouldBe(1);
        openProblems[0].Id.ShouldBe(first.Id);
        openProblems[0].Id.ShouldBe(second.Id);
        openProblems[0].OccurrenceCount.ShouldBe(2);
        openProblems[0].Summary.ShouldBe("Second failure");
        openProblems[0].Details.ShouldBe("forbidden");

        service.ResolveByDedupeKey(SyncProblemKeys.Upload(@"C:\sync\report.docx"));

        db.GetProblems(openOnly: true).ShouldBeEmpty();
        db.GetProblems(openOnly: false).Single().Status.ShouldBe(SyncProblemStatus.Resolved);
    }

    [Fact]
    public void EnsureProblemEntriesForTrackedItemStates_ImportsLegacyErrorAndConflictRows()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "sync-state.db"));

        var conflictPath = Path.Combine(tempDir.Path, "docs", "conflict.docx");
        var errorPath = Path.Combine(tempDir.Path, "docs", "failed.docx");

        db.Upsert(new SyncItem
        {
            LocalPath = conflictPath,
            RemotePath = "/docs/conflict.docx",
            IsDirectory = false,
            SyncStatus = SyncStatus.Conflict,
            UpdatedAt = new DateTime(2026, 3, 29, 8, 0, 0, DateTimeKind.Utc)
        });
        db.Upsert(new SyncItem
        {
            LocalPath = errorPath,
            RemotePath = "/docs/failed.docx",
            IsDirectory = false,
            SyncStatus = SyncStatus.Error,
            UpdatedAt = new DateTime(2026, 3, 29, 8, 1, 0, DateTimeKind.Utc)
        });

        var imported = db.EnsureProblemEntriesForTrackedItemStates();

        imported.ShouldBe(2);

        var problems = db.GetProblems(openOnly: true);
        problems.Count.ShouldBe(2);
        problems.ShouldContain(problem => problem.DedupeKey == SyncProblemKeys.Conflict(conflictPath));
        problems.ShouldContain(problem => problem.DedupeKey == SyncProblemKeys.Upload(errorPath));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CloudDrive.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
                return;

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
