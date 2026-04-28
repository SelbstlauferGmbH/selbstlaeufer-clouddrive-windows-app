using CloudDrive.Core.Data;
using Shouldly;

namespace CloudDrive.Core.Tests.Data;

public class SyncJournalTests
{
    [Fact]
    [Trait("Category", "SyncEngine")]
    public void Upsert_KeepsStableFileIdAcrossRename()
    {
        using var tempDir = new TempDirectory();
        using var db = new SyncStateDb(Path.Combine(tempDir.Path, "syncstate.db"));
        var journal = new SyncJournal(db);

        journal.Upsert(new SyncJournalRecord
        {
            FileId = "stable-file-id",
            LocalPath = Path.Combine(tempDir.Path, "old.txt"),
            RemotePath = "/old.txt",
            ETag = "etag-1",
            Size = 10,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        journal.Upsert(new SyncJournalRecord
        {
            FileId = "stable-file-id",
            LocalPath = Path.Combine(tempDir.Path, "new.txt"),
            RemotePath = "/new.txt",
            ETag = "etag-1",
            Size = 10,
            MTimeUtc = DateTime.UtcNow,
            InSync = true
        });

        journal.GetByLocalPath(Path.Combine(tempDir.Path, "old.txt")).ShouldBeNull();
        var renamed = journal.GetByFileId("stable-file-id");
        renamed.ShouldNotBeNull();
        renamed.LocalPath.ShouldBe(Path.Combine(tempDir.Path, "new.txt"));
        renamed.RemotePath.ShouldBe("/new.txt");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"CloudDrive.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
