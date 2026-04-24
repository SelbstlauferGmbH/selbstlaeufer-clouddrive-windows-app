# Testing Guide

Three levels of tests exist in this project. Use whichever fits what you're doing.

---

## 1 — Unit Tests

**What:** Tests for a single class in isolation. All dependencies (WebDAV, database, cfapi) are replaced with mocks. Fast — no network, no Docker, no Windows Cloud Files setup needed.

**When to use:** After changing the logic inside a handler, state machine, or service.

**How to run:**

```powershell
dotnet test tests/CloudDrive.Core.Tests --filter "Category!=E2E"
```

That runs everything except the E2E tests. Individual folders:

| Folder | What's tested |
|--------|--------------|
| `SyncEngine/` | Hydration, dehydration, upload, conflict handling |
| `SyncRoot/` | Mount state machine, sync root registrar, Explorer status |
| `WebDav/` | Download, upload, move, health check logic |
| `Watchdog/` | Liveness probing, task scheduler XML, cycle runner |
| `Configuration/` | AppSettings loading, auto-start registration |

**What a passing run looks like:**

```
Passed! - Failed: 0, Passed: 47, Skipped: 0, Total: 47
```

---

## 2 — E2E Tests (automated, with Docker)

**What:** Tests the full sync engine end-to-end against a real WebDAV server running locally in Docker. Uses the actual Windows Cloud Files API (cfapi). Covers: folder browsing, file creation, upload, hydration, deletion.

**When to use:** After changing anything in `SyncCoordinator`, `WebDavService`, `UploadManager`, `HydrationHandler`, or the cfapi connector.

**Requirements:** Windows 11 build 22621 or newer, Docker Desktop running.

**How to run (one command):**

```powershell
tests/run-e2e-local.ps1
```

What it does step by step:
1. Builds the solution
2. Starts the local WebDAV server in Docker (`localhost:8080`)
3. Waits for the server health check to pass
4. Runs `dotnet test --filter Category=E2E`
5. Collects server logs from Docker
6. Merges client logs + server logs into one time-sorted file
7. Stops Docker

**Output files in `TestResults/`:**

| File | Content |
|------|---------|
| `e2e-<stamp>-merged.jsonl` | **Main debug file** — all events from server and client, sorted by time |
| `e2e-<stamp>-server.jsonl` | Raw WebDAV server requests (method, path, status, duration) |
| `e2e-<stamp>-dotnet.txt` | xUnit console output |
| `e2e-<stamp>.trx` | TRX report (openable in Visual Studio) |

**Useful options:**

```powershell
# Keep Docker running after tests (to inspect manually)
tests/run-e2e-local.ps1 -KeepAlive

# Run only one test class
tests/run-e2e-local.ps1 -Filter "Category=E2E&ClassName=FullLifecycleTest"

# Longer timeouts for slow machines
tests/run-e2e-local.ps1 -TimeoutSeconds 120

# Skip build if you already built
tests/run-e2e-local.ps1 -NoBuild
```

---

## 3 — Manual Testing (real app + local WebDAV)

**What:** Run the actual CloudDrive desktop application against a local WebDAV server in Docker. Use this when you want to click through the real UI, reproduce a specific bug, or watch how the sync engine behaves with real files.

**When to use:** When a bug is hard to reproduce in automated tests, or when you want to see the UI behaviour.

**Requirements:** Windows 11 build 22621+, Docker Desktop running, .NET 9 SDK.

---

### Step 1 — Start the session and app

```powershell
tests/start-local.ps1
```

This clears `TestResults/`, starts the Docker WebDAV server, publishes the app to `build/publish/`, and starts `CloudDrive.App.exe` with JSON debug logging enabled. The PowerShell window stays blocked while you test. When you close/exit CloudDrive, the script collects the app and WebDAV logs, merges them, and stops Docker.

If you only want the WebDAV server, run:

```powershell
tests/start-local.ps1 -ServerOnly
```

If the app is already published and you want to skip publishing, run:

```powershell
tests/start-local.ps1 -NoAppPublish
```

---

### Step 2 — Configure the app

When starting the app manually instead of using `tests/start-local.ps1`, run:

```powershell
$env:CLOUDDRIVE_DEBUG_JSONLOG = "1"; Start-Process -FilePath ".\build\publish\CloudDrive.App.exe" -WorkingDirectory "." -Wait
```

The `CLOUDDRIVE_DEBUG_JSONLOG=1` flag makes the app write a machine-readable JSON log file alongside the normal log — this file is what `stop-local.ps1` picks up for merging.

Configure CloudDrive to connect to `http://localhost:8080/` with user `testuser` and password `testpass`.

---

### Step 3 — Do your testing

Use the app normally. Browse folders, create files, wait for sync, trigger edge cases. The WebDAV server logs every request in real time — you can watch them:

```powershell
docker compose logs -f webdav
```

---

### Step 4 — Collect and merge all logs

When you use `tests/start-local.ps1`, this happens automatically after the app exits.

For a `-ServerOnly` session, or if you need to recover logs manually, run:

```powershell
tests/stop-local.ps1
```

Or to a specific folder:

```powershell
tests/stop-local.ps1 -OutputFolder C:\debug\my-session
```

This:
1. Stops the workspace CloudDrive app if it is still running
2. Captures WebDAV server logs and stops Docker
3. Finds the app JSON debug log from `%LOCALAPPDATA%\CloudDrive\logs\`
4. Merges everything into one time-sorted file: `merged-session-<stamp>.jsonl`

---

### What the merged log looks like

Each line is one JSON event. Client (app) and server events are interleaved by timestamp so you can see the cause and effect:

```json
{"ts":"12:00:00.100Z","source":"test-client","level":"Information","category":"WebDavService","message":"WebDav[ListDirectory] RemotePath=/"}
{"ts":"12:00:00.103Z","source":"webdav-server","method":"PROPFIND","path":"/","status":"207","duration_ms":3}
{"ts":"12:00:00.108Z","source":"test-client","level":"Information","category":"WebDavService","message":"WebDav[ListDirectory] Response=207 DurationMs=5"}
{"ts":"12:00:01.500Z","source":"app-log","level":"Information","category":"UploadManager","message":"UPLOAD_FILE complete: /notes.txt Synced"}
{"ts":"12:00:01.502Z","source":"webdav-server","method":"PUT","path":"/notes.txt","status":"201","duration_ms":11}
```

Hand this file to an AI tool (Claude Code, Codex) for debugging — it contains the full causal chain of what happened.

---

## Quick Reference

| Goal | Command |
|------|---------|
| Run unit tests only | `dotnet test --filter "Category!=E2E"` |
| Run E2E tests (automated) | `tests/run-e2e-local.ps1` |
| Start full manual session | `tests/start-local.ps1` |
| Start manual WebDAV session only | `tests/start-local.ps1 -ServerOnly` |
| Recover/collect logs manually | `tests/stop-local.ps1` |
| Watch server logs live | `docker compose logs -f webdav` |
| Start server only (no tests) | `docker compose up -d` |
| Stop server | `docker compose down -v` |
