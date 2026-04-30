# What happens when a new file appears on the WebDAV server

This document describes the exact sequence of events that occurs when the
CloudDrive app is fully started up and a *new* file is added on the remote
WebDAV server (i.e. by another client). The steps are listed in the order
they execute, with the file, class, method, and database calls involved.

---

## 1. Detecting that something changed on the server

The app does **not** receive a push notification from the WebDAV server.
Instead, it polls. Two pollers are running in the background:

1. **Full remote scan** (every ~30 s, configurable):
   - `src/CloudDrive.Core/SyncEngine/SyncCoordinator.cs` →
     `SyncCoordinator.PollRemoteChangesAsync()` ticks on the polling
     interval.
   - It calls `SyncCoordinator.ExecuteRemoteSyncPassAsync()`, which
     acquires a semaphore so only one scan runs at a time.
   - That in turn calls
     `SyncCoordinator.EnqueueDiscoveryActionsAsync()` with the sync
     root and `depth = int.MaxValue` (full tree).

2. **Focused-folder poll** (every ~10 s):
   - `src/CloudDrive.Core/SyncEngine/FocusedFolderPoller.cs` →
     `FocusedFolderPoller.RunAsync()` watches the folder the user
     currently has open in Explorer.
   - It calls `FocusedFolderPoller.PollOnceAsync()` →
     `DiscoveryWalker.WalkAsync(focusedPath, depth: 1)`.
   - This makes a newly-added remote file appear quickly *if the user
     is looking at its folder*; otherwise the 30 s scan picks it up.

---

## 2. Listing the remote folder

The discovery walker lists the remote directory:

3. `src/CloudDrive.Core/SyncEngine/DiscoveryWalker.cs` →
   `DiscoveryWalker.WalkAsync()` is the entry point.
4. It calls `DiscoveryWalker.ListRemoteItemsAsync()`, which prefers a
   WebDAV `REPORT` (sync-collection) and falls back to `PROPFIND`.
5. `src/CloudDrive.Core/WebDav/WebDavService.cs` →
   `WebDavService.ListDirectoryAsync()` is invoked.
6. Before the HTTP call, the rate limiter
   (`RateLimiter.WaitAsync()`, ~45 requests / 30 s) gates the request.
7. A `PROPFIND` HTTP request is sent with `Depth: 1`, asking for
   `resourcetype`, `getcontentlength`, `getlastmodified`, `getetag`,
   `getcontenttype`, and `displayname`.
8. The XML multistatus response is parsed by
   `WebDavService.ParsePropfindResponse()`, producing a list of
   `RemoteItem` objects (path, size, etag, last-modified, isDirectory).

---

## 3. Comparing remote with local + journal

The walker now compares what the server has with what the local disk
and the local SQLite database have:

9. `DiscoveryWalker.WalkAsync()` enumerates the local placeholders via
   `src/CloudDrive.Core/Vfs/CfApiVfs.cs` →
   `CfApiVfs.EnumerateChildrenAsync()` (Windows Cloud Files API).
10. It loads the journal rows for that directory via
    `src/CloudDrive.Core/Data/SyncJournal.cs` →
    `SyncJournal.GetJournalChildren()`.
11. That method calls
    `src/CloudDrive.Core/Data/SyncStateDb.cs` →
    `SyncStateDb.GetAllJournalRecords()`, which executes:
    ```sql
    SELECT * FROM sync_journal ORDER BY local_path
    ```
12. `DiscoveryWalker.ClassifyExistingLocal()` (and the classification
    logic inside `WalkAsync`) decide for each remote item whether it
    is new, changed, in conflict, or unchanged.
13. For a brand-new remote file there is **no local file** and **no
    journal entry**. `ConflictDetector.HasRemoteChanged()`
    (`src/CloudDrive.Core/SyncEngine/ConflictDetector.cs`) confirms
    "this is new".
14. A `ReconcileAction` of type `ReconcileActionType.DownloadNew` is
    produced, with:
    - `LocalPath`  – computed from the remote path via `PathMapper.ToLocalPath()`
    - `RemotePath` – from the `RemoteItem`
    - `FileId`    – `SyncIdentity.RemotePathFallbackId(remotePath)`
    - `Remote`    – the `RemoteItem`
    - `Local`     – `null`
    - `Journal`   – `null`

---

## 4. Enqueueing the action

15. `SyncCoordinator.EnqueueDiscoveryActionsAsync()` calls
    `SyncCoordinator.EnqueueAction()`, which calls
    `src/CloudDrive.Core/SyncEngine/PropagatorQueue.cs` →
    `PropagatorQueue.Enqueue()`.
16. `PropagatorQueue.Enqueue()` persists the job by calling
    `SyncStateDb.EnqueuePropagatorJob()`, which executes:
    ```sql
    INSERT INTO propagator_jobs
      (operation_id, job_type, local_path, remote_path,
       payload_json, status, created_at)
    VALUES (...)
    ```
    with `job_type = DownloadNew (2)` and `status = Pending (0)`.

---

## 5. Picking the job up and running it

17. A separate background loop in
    `SyncCoordinator.ProcessPropagatorQueueAsync()` continuously leases
    pending jobs.
18. `PropagatorQueue.Lease()` calls `SyncStateDb.LeaseJobs()`, which
    executes:
    ```sql
    UPDATE propagator_jobs
       SET status = Leased, lease_until_utc = (now + 5 min)
     WHERE status = Pending
       AND (lease_until_utc IS NULL OR lease_until_utc < now)
    ```
19. The leased job is handed to
    `src/CloudDrive.Core/SyncEngine/Propagator.cs` →
    `Propagator.ApplyAsync()`, which dispatches on job type and calls
    `Propagator.DownloadAsync()` for `DownloadNew`.

---

## 6. Creating the placeholder (no content downloaded yet)

The app uses the Windows **Cloud Files API**, so a new remote file
shows up locally as a *placeholder* — the file appears in Explorer
with the right size and timestamp, but bytes are only fetched when
the user actually opens it.

20. `Propagator.DownloadAsync()` builds a `VfsMetadata` value:
    `FileId`, `RemotePath`, `ETag`, `Size`, `LastModified`,
    `InSync = true`.
21. It calls `CfApiVfs.CreatePlaceholderAsync()`.
22. That method creates the parent directory with
    `Directory.CreateDirectory()` if needed, then calls the Windows
    API **`CfCreatePlaceholders`** with:
    - `RelativeFileName`   – the file name
    - `FsMetadata`         – size, attributes, timestamps
    - `FileIdentity`       – packed `(fileId, etag, remotePath)`
    - `Flags`              – `CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC`
23. `Propagator` then calls `CfApiVfs.SetInSyncAsync()` so the
    placeholder is marked in-sync (its metadata matches the server,
    even though no content has been fetched).

---

## 7. Recording the new state in the database

24. `Propagator.UpsertJournal()` calls
    `SyncStateDb.UpsertJournalRecord()`, which executes:
    ```sql
    INSERT INTO sync_journal
      (file_id, local_path, remote_path, etag, mtime_utc, size,
       is_directory, in_sync, base_etag, updated_at)
    VALUES (...)
    ON CONFLICT(file_id) DO UPDATE SET
      local_path = excluded.local_path,
      remote_path = excluded.remote_path,
      etag        = excluded.etag,
      mtime_utc   = excluded.mtime_utc,
      size        = excluded.size,
      is_directory= excluded.is_directory,
      in_sync     = excluded.in_sync,
      base_etag   = excluded.base_etag,
      updated_at  = excluded.updated_at
    ```
25. Optionally, `SyncItemStateService` (legacy UI state) upserts a row
    in the `sync_items` table for tray/UI consumers.
26. `PropagatorQueue.Complete()` calls `SyncStateDb.CompleteJob()`,
    which executes:
    ```sql
    UPDATE propagator_jobs
       SET status = Completed, lease_until_utc = NULL,
           completed_at = now
     WHERE operation_id = ?
    ```

---

## 8. Telling Explorer / the tray to refresh

27. `Propagator.SetExplorerStateAsync()` updates the in-memory
    `ExplorerItemStateService` so any UI bound to it sees the new
    item.
28. `src/CloudDrive.Core/SyncEngine/ExplorerWindowRefresher.cs` →
    `ExplorerWindowRefresher.NotifyShellDirectoryChanged()` calls the
    Win32 shell API:
    ```
    SHChangeNotify(SHCNE_UPDATEDIR,
                   SHCNF_PATHW | SHCNF_FLUSHNOWAIT, ...)
    ```
    which makes any open Explorer window viewing that folder
    re-enumerate its contents and show the new file.
29. If needed, the refresher also iterates open Explorer windows via
    `Shell.Application` COM and calls `Refresh()` on the matching
    ones.
30. The tray icon picks up sync state from the same database
    (`sync_items` / `sync_journal`) and updates its overlay/tooltip.

---

## 9. (Later) When the user actually opens the new file

The placeholder has metadata only. The first time the user (or any
process) reads the file's bytes, Windows asks our provider for them:

31. The Cloud Files API fires the **`CF_CALLBACK_TYPE_FETCH_DATA`**
    callback into
    `src/CloudDrive.Core/SyncRoot/SyncRootConnector.cs`.
32. The callback is forwarded to
    `src/CloudDrive.Core/SyncEngine/HydrationHandler.cs` →
    `HydrationHandler.HandleFetchDataAsync()`.
33. `HandleFetchDataAsync()` works out the byte range to fetch and
    calls `src/CloudDrive.Core/WebDav/WebDavService.cs` →
    `WebDavService.DownloadFilePriorityAsync()` (HTTP `GET` with a
    `Range` header, bypassing the normal rate limiter because the
    user is waiting).
34. `CfApiVfs.HydrateAsync()` writes the downloaded stream into the
    placeholder in 4 MB chunks using
    `CfExecute(CF_OPERATION_TYPE_TRANSFER_DATA)`.
35. When the range is satisfied, `HydrationHandler` calls
    `CfExecute(CF_OPERATION_TYPE_ACK_DATA)` so Windows knows the data
    is now present.
36. The journal row is updated again (size/etag/in_sync) via
    `SyncStateDb.UpsertJournalRecord()`.

Note: steps 31–36 happen only on first read. Until then, the new
remote file lives locally as a zero-byte-on-disk placeholder.

---

## Database tables touched in this flow

| Table             | Used in steps   | Purpose                                                |
|-------------------|-----------------|--------------------------------------------------------|
| `sync_journal`    | 11, 24, 36      | `FileId` ↔ local/remote path, etag, size, mtime, in_sync |
| `propagator_jobs` | 16, 18, 26      | Persistent queue of pending download/upload/delete ops |
| `sync_items`      | 25 (optional)   | Legacy UI-facing per-item sync state                   |
| `sync_problems`   | (on errors)     | Sync errors / conflicts shown in the UI                |

---

## One-screen summary

```
WebDAV server gets new file
          │
          ▼
SyncCoordinator.PollRemoteChangesAsync       (every ~30 s)
          │
          ▼
DiscoveryWalker.WalkAsync
          │
          ├──► WebDavService.ListDirectoryAsync   (PROPFIND)
          ├──► CfApiVfs.EnumerateChildrenAsync    (local files)
          └──► SyncStateDb.GetAllJournalRecords   (SELECT sync_journal)
          │
          ▼
ReconcileAction(DownloadNew)
          │
          ▼
PropagatorQueue.Enqueue
   └─► SyncStateDb.EnqueuePropagatorJob       (INSERT propagator_jobs)
          │
          ▼
SyncCoordinator.ProcessPropagatorQueueAsync
   └─► PropagatorQueue.Lease
        └─► SyncStateDb.LeaseJobs             (UPDATE propagator_jobs)
          │
          ▼
Propagator.DownloadAsync
   ├─► CfApiVfs.CreatePlaceholderAsync        (CfCreatePlaceholders)
   ├─► CfApiVfs.SetInSyncAsync
   ├─► SyncStateDb.UpsertJournalRecord        (INSERT/UPSERT sync_journal)
   └─► SyncStateDb.CompleteJob                (UPDATE propagator_jobs)
          │
          ▼
ExplorerWindowRefresher.NotifyShellDirectoryChanged
   └─► SHChangeNotify(SHCNE_UPDATEDIR, …)
          │
          ▼
File is visible in Explorer as a placeholder
          │
          ▼ (only when the user opens it)
CF_CALLBACK_TYPE_FETCH_DATA
   └─► HydrationHandler.HandleFetchDataAsync
        ├─► WebDavService.DownloadFilePriorityAsync   (GET + Range)
        ├─► CfApiVfs.HydrateAsync                     (TRANSFER_DATA)
        └─► SyncStateDb.UpsertJournalRecord
```
