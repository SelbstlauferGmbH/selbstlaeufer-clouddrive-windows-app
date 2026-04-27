namespace CloudDrive.Core.Data;

public sealed class SyncJournal
{
    private readonly SyncStateDb _db;

    public SyncJournal(SyncStateDb db)
    {
        _db = db;
    }

    public SyncJournalRecord? GetByFileId(string fileId) => _db.GetJournalByFileId(fileId);

    public SyncJournalRecord? GetByLocalPath(string localPath) => _db.GetJournalByLocalPath(localPath);

    public SyncJournalRecord? GetByRemotePath(string remotePath) => _db.GetJournalByRemotePath(remotePath);

    public IReadOnlyList<SyncJournalRecord> GetChildren(string localDirectoryPath) =>
        _db.GetJournalChildren(localDirectoryPath);

    public IReadOnlyList<SyncJournalRecord> GetAll() => _db.GetAllJournalRecords();

    public void Upsert(SyncJournalRecord record) => _db.UpsertJournalRecord(record);

    public void Delete(string fileId) => _db.DeleteJournalRecord(fileId);

    public PendingConflictRecord UpsertConflict(PendingConflictRecord record) =>
        _db.UpsertPendingConflict(record);

    public PendingConflictRecord? GetPendingConflict(string fileId) =>
        _db.GetPendingConflict(fileId);
}
