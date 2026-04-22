# CloudDrive - Core Architecture

## Documentation Context

This is the architecture reference for CloudDrive.

Use this page when you need to understand:

- the main projects and their responsibilities
- the sync engine structure
- component interactions
- persistence and dependency boundaries

For a documentation entry point and reading guide, start with:

- [Documentation home](./README.md)

For packaging, installer, signing, and update distribution details, see:

- [Installer and auto-updater](./installer-and-updater.md)

## Overview

CloudDrive is a Windows desktop application that synchronizes a local folder with a remote WebDAV server using the **Windows Cloud Files API (cfapi)**. Files appear as lightweight placeholders in Windows Explorer and are downloaded on demand (hydrated) when accessed. Local changes are automatically uploaded back to the server.

The application consists of three deployable units and one shared library:

| Project | Type | Purpose |
|---|---|---|
| **CloudDrive.App** | WPF Desktop App | UI, tray icon, settings, activity dashboard |
| **CloudDrive.Core** | Class Library | Sync engine, WebDAV client, data layer |
| **CloudDrive.Watchdog** | Windows Service | Monitors and restarts the main app |
| **CloudDrive.Core.Tests** | xUnit Test Project | Unit and E2E tests |

---

## High-Level Architecture Diagram

```
+===================================================================+
|                        Windows Explorer                           |
|              (User browses / opens / saves files)                 |
+============================+======================================+
                             |
                    cfapi callbacks
                    (kernel-mode ↔ user-mode)
                             |
+============================v======================================+
|                                                                   |
|  CloudDrive.App  (WPF / .NET 9)                                  |
|  ┌─────────────────────────────────────────────────────────────┐  |
|  │  App.xaml.cs                                                │  |
|  │  ├── DI Container (Microsoft.Extensions.Hosting)            │  |
|  │  ├── TrayIconManager (Hardcodet.NotifyIcon.Wpf)             │  |
|  │  ├── SettingsWindow / SettingsViewModel (MVVM)              │  |
|  │  └── ActivityPanel (real-time sync status)                  │  |
|  └──────────────────────────┬──────────────────────────────────┘  |
|                             │ IHostedService                      |
|  ┌──────────────────────────v──────────────────────────────────┐  |
|  │  SyncEngineHostedService (BackgroundService)                │  |
|  │  ├── Creates WebDavService + SyncCoordinator                │  |
|  │  ├── Manages MountStateMachine                              │  |
|  │  └── Wires events → UI notifications                        │  |
|  └──────────────────────────┬──────────────────────────────────┘  |
|                             │                                     |
+=============================│=====================================+
                              │ references
+=============================v=====================================+
|                                                                   |
|  CloudDrive.Core  (Class Library / .NET 9)                        |
|                                                                   |
|  ┌─────────────────────────────────────────────────────────────┐  |
|  │                  SyncCoordinator                            │  |
|  │             (Central Orchestrator)                          │  |
|  │                                                             │  |
|  │  Initializes all components, wires cfapi callbacks,         │  |
|  │  manages sync lifecycle, coordinates data flow              │  |
|  └───┬──────┬──────┬──────┬──────┬──────┬──────────────────────┘  |
|      │      │      │      │      │      │                         |
|      v      v      v      v      v      v                         |
|  ┌──────┐┌──────┐┌──────┐┌──────┐┌──────┐┌────────────────────┐  |
|  │Place-││Hydra-││Dehy- ││Upload││Remote││LocalChange-        │  |
|  │holder││tion  ││drat- ││Mana- ││Change││Watcher             │  |
|  │Mana- ││Hand- ││ion   ││ger   ││Detec-││(FileSystemWatcher) │  |
|  │ger   ││ler   ││Hand- ││      ││tor   ││                    │  |
|  │      ││      ││ler   ││      ││      ││                    │  |
|  └──┬───┘└──┬───┘└──────┘└──┬───┘└──┬───┘└────────────────────┘  |
|     │       │               │       │                             |
|     v       v               v       v                             |
|  ┌─────────────────────────────────────────────────────────────┐  |
|  │  SyncRootConnector          (Vanara.PInvoke.CldApi)        │  |
|  │  ├── CfConnectSyncRoot      → callback registration        │  |
|  │  ├── FETCH_PLACEHOLDERS     → PlaceholderManager            │  |
|  │  ├── FETCH_DATA             → HydrationHandler              │  |
|  │  ├── CANCEL_FETCH_DATA      → cancel hydration              │  |
|  │  ├── NOTIFY_DEHYDRATE       → DehydrationHandler            │  |
|  │  ├── NOTIFY_DELETE          → local deletion handling        │  |
|  │  └── NOTIFY_RENAME          → local rename handling         │  |
|  ├─────────────────────────────────────────────────────────────┤  |
|  │  SyncRootRegistrar          (WinRT StorageProvider API)     │  |
|  │  ├── Register/Unregister sync root                          │  |
|  │  ├── Set HydrationPolicy, PopulationPolicy, InSyncPolicy   │  |
|  │  └── Detect + clean stale registrations                     │  |
|  └─────────────────────────────────────────────────────────────┘  |
|                                                                   |
|  ┌─────────────────────────────────────────────────────────────┐  |
|  │  WebDAV Layer                                               │  |
|  │  ├── WebDavService (IWebDavService)  ── WebDav.Client 2.9  │  |
|  │  ├── WebDavAuthHandler (Basic / NTLM / Negotiate)          │  |
|  │  └── RateLimiter (45 requests / 30 seconds)                │  |
|  └──────────────────────────┬──────────────────────────────────┘  |
|                             │ HTTP                                |
|  ┌──────────────────────────v──────────────────────────────────┐  |
|  │  Data Layer                                                 │  |
|  │  ├── SyncStateDb (Microsoft.Data.Sqlite)                    │  |
|  │  │   ├── sync_items    (file metadata, ETag, status)        │  |
|  │  │   └── sync_problems (conflicts, errors)                  │  |
|  │  ├── SyncItemStateService (CRUD operations)                 │  |
|  │  ├── CredentialManager (Windows Credential Store)           │  |
|  │  └── AppSettings (JSON config)                              │  |
|  └─────────────────────────────────────────────────────────────┘  |
|                                                                   |
+===================================================================+
                              │
                     HTTP (PUT/GET/DELETE/
                     PROPFIND/MKCOL/MOVE)
                              │
                              v
                 +========================+
                 │                        │
                 │    WebDAV Server       │
                 │    (External)          │
                 │                        │
                 +========================+
```

---

## Component Interaction Diagram

```
                    ┌────────────────────────┐
                    │   Windows Explorer     │
                    └──────────┬─────────────┘
                               │
                 cfapi callbacks (kernel ↔ user)
                               │
┌──────────────────────────────v──────────────────────────────────┐
│                     SyncRootConnector                           │
│                  (Vanara.PInvoke.CldApi)                        │
│                                                                 │
│  FETCH_PLACEHOLDERS ──→ PlaceholderManager ──→ WebDavService   │
│                              │                   (PROPFIND)     │
│                              └──→ SyncProjectionService         │
│                                        │                        │
│                                        └──→ SyncItemStateService│
│                                                                 │
│  FETCH_DATA ──────────→ HydrationHandler ──→ WebDavService     │
│                              │                   (GET)          │
│                              └──→ SyncItemStateService          │
│                                                                 │
│  NOTIFY_DEHYDRATE ────→ DehydrationHandler                     │
│                              └──→ CloudFilePlaceholderHelper    │
│                                                                 │
│  NOTIFY_DELETE ───────→ UploadManager ────→ WebDavService      │
│  NOTIFY_RENAME                │                (DELETE/MOVE)    │
│                               └──→ SyncItemStateService         │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                 Local → Remote Sync Path                        │
│                                                                 │
│  FileSystemWatcher ──→ LocalChangeWatcher ──→ Channel<T>       │
│                           (debounce 2s)           │             │
│                                                   v             │
│                                    SyncCoordinator.ProcessUploads│
│                                              │                  │
│                                              v                  │
│                                        UploadManager            │
│                                      (semaphore: 4 max)         │
│                                              │                  │
│                                              v                  │
│                                        WebDavService            │
│                                      (PUT / DELETE / MOVE)      │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                 Remote → Local Sync Path                        │
│                                                                 │
│  Timer (every 30s) ──→ RemoteChangeDetector                    │
│                              │                                  │
│                              ├──→ WebDavService (PROPFIND)      │
│                              ├──→ SyncItemStateService (diff)   │
│                              └──→ CfCreatePlaceholder (new)     │
│                                   CfUpdatePlaceholder (changed) │
│                                   Delete local (removed)        │
└─────────────────────────────────────────────────────────────────┘
```

---

## External APIs and Libraries

### Windows Cloud Files API (cfapi) - via Vanara.PInvoke.CldApi

The core integration point with Windows. cfapi allows the application to register a folder as a "sync root" and respond to file system operations with on-demand content delivery.

| Function | Purpose |
|---|---|
| `CfConnectSyncRoot` | Register callbacks for file operations |
| `CfDisconnectSyncRoot` | Unregister callbacks |
| `CfCreatePlaceholderFromItem` | Create placeholder files (0-byte with metadata) |
| `CfExecute` | Complete a pending cfapi operation |
| `CfGetPlaceholderInfo` | Query placeholder state (pin, in-sync, sizes) |
| `CfHydratePlaceholder` | Trigger file content download |
| `CfUpdateSyncProviderStatus` | Signal connection state to Explorer |
| `CfGetSyncRootInfoByPath` | Query sync root registration info |

### Windows Storage Provider API (WinRT)

Used for sync root registration and policy configuration.

| API | Purpose |
|---|---|
| `StorageProviderSyncRootManager.Register` | Register folder as cloud sync root |
| `StorageProviderSyncRootManager.Unregister` | Remove sync root registration |
| `StorageProviderSyncRootManager.GetCurrentSyncRoots` | List all registered roots |

**Policies configured at registration:**
- **HydrationPolicy**: `Full` - download entire file on access
- **PopulationPolicy**: `Full` - populate directory listings on demand
- **InSyncPolicy**: `FileCreationTime | DirectoryCreationTime`

### WebDAV Server - via WebDav.Client

All remote file operations go through the WebDAV protocol over HTTP.

| Operation | HTTP Method | Usage |
|---|---|---|
| List directory | `PROPFIND` (Depth: 1) | Enumerate remote files |
| Download file | `GET` (with Range header) | Stream content in 4 MB chunks |
| Upload file | `PUT` | Send file to server |
| Delete | `DELETE` | Remove remote file/folder |
| Move/Rename | `MOVE` | Rename or relocate on server |
| Create directory | `MKCOL` | Create remote folder |
| Test connection | `OPTIONS` | Verify server availability |
| Health check | `PROPFIND /` | Measure latency |

**Authentication**: Basic, NTLM, or Negotiate (Kerberos with NTLM fallback).

**Rate Limiting**: 45 requests per 30-second sliding window. User-initiated downloads (hydration) bypass the rate limiter because cfapi imposes a 5-minute timeout.

---

## Mount Lifecycle State Machine

```
                          ┌──────────┐
                          │   Idle   │
                          └────┬─────┘
                               │
                               v
                     ┌─────────────────┐
                     │ CheckingStale   │
                     └────┬────────┬───┘
                          │        │
                stale found│        │ no stale
                          v        │
                  ┌──────────────┐ │
                  │CleaningStale │ │
                  └──────┬───────┘ │
                         │         │
                         v         v
                  ┌──────────────────────┐
           ┌──────│VerifyingConnection   │◄──────────────┐
           │      └──────────┬───────────┘               │
           │                 │                            │
      fail │                 │ OK                    retry│
           │                 v                            │
           │      ┌──────────────────────┐               │
           │      │VerifyingListing      │               │
           │      └────┬────────────┬────┘               │
           │      fail │            │ OK                  │
           │           │            v                     │
           v           │     ┌──────────────┐            │
   ┌────────────────┐  │     │ Registering  │            │
   │WaitingForServer│──┘     └──────┬───────┘            │
   │  (2-min notify)│               │                    │
   └────────────────┘               v                    │
                            ┌─────────────┐              │
                            │    Ready    │──────────────>│
                            │  (syncing)  │  3 failures   │
                            └──────┬──────┘      ┌────────┴──────┐
                                   │             │ConnectionLost │
                                   │             │ (5-min notify)│
                                   │             └───────────────┘
                                   v
                            ┌─────────────┐
                            │ShuttingDown │
                            └──────┬──────┘
                                   v
                            ┌─────────────┐
                            │   Stopped   │
                            └─────────────┘
```

---

## Data Flow: Key Scenarios

### 1. User Opens a Folder in Explorer (Placeholder Population)

```
Explorer                cfapi              App                    WebDAV Server
   │                      │                 │                         │
   │──GetDirectoryEntries→│                 │                         │
   │                      │──FETCH_PLACE──→ │                         │
   │                      │   HOLDERS       │                         │
   │                      │                 │──PROPFIND (Depth:1)───→ │
   │                      │                 │                         │
   │                      │                 │◄──XML response──────── │
   │                      │                 │   (name, size, ETag)    │
   │                      │                 │                         │
   │                      │                 │──reconcile with DB      │
   │                      │                 │                         │
   │                      │◄─CfCreatePlace─ │                         │
   │                      │   holders       │                         │
   │◄─placeholders shown──│                 │                         │
```

### 2. User Opens a File (On-Demand Hydration)

```
Explorer/App            cfapi              App                    WebDAV Server
   │                      │                 │                         │
   │──open/read file────→ │                 │                         │
   │                      │──FETCH_DATA───→ │                         │
   │                      │                 │──GET (Range)──────────→ │
   │                      │                 │                         │
   │                      │                 │◄──stream 4MB chunks─── │
   │                      │◄─CfExecute─────│                         │
   │                      │  (write chunks) │                         │
   │                      │                 │──update DB (Synced)     │
   │◄─file content────────│                 │                         │
```

### 3. User Saves a File (Upload)

```
User                  FSWatcher           App                    WebDAV Server
  │                      │                 │                         │
  │──save file──────────→│                 │                         │
  │                      │──Changed event─→│                         │
  │                      │  (debounce 2s)  │                         │
  │                      │                 │──PUT──────────────────→ │
  │                      │                 │                         │
  │                      │                 │◄──200 OK + ETag──────  │
  │                      │                 │──update DB (Synced)     │
```

### 4. Remote File Changes (Polling)

```
Timer (30s)                App                           WebDAV Server
   │                        │                                │
   │──tick──────────────── →│                                │
   │                        │──PROPFIND (recursive)────────→ │
   │                        │                                │
   │                        │◄──directory listing──────────  │
   │                        │                                │
   │                        │──diff against SyncStateDb      │
   │                        │                                │
   │                        │──CfCreatePlaceholder (new)     │
   │                        │──CfUpdatePlaceholder (changed) │
   │                        │──delete local (removed)        │
```

---

## Interface Map

```
┌─────────────────────────────┐      ┌────────────────────────────┐
│      CloudDrive.App         │      │      CloudDrive.Core       │
│                             │      │                            │
│  IActivityTracker ◄─────────│──────│─── used by sync handlers   │
│  └─ ActivityTracker (impl)  │      │                            │
│                             │      │  IWebDavService            │
│  IHost                      │      │  └─ WebDavService          │
│  └─ SyncEngineHostedService │      │                            │
│     (uses Core classes)     │      │  ISyncItemStateService     │
│                             │      │  └─ SyncItemStateService   │
│  SettingsViewModel          │      │                            │
│  (CommunityToolkit.Mvvm)    │      │  ISyncProjectionService    │
│                             │      │  └─ SyncProjectionService  │
│                             │      │                            │
│                             │      │  ISyncProblemService       │
│                             │      │  └─ SyncProblemService     │
│                             │      │                            │
│                             │      │  ICloudFileOperations      │
│                             │      │  └─ CloudFileOperations    │
└─────────────────────────────┘      └────────────────────────────┘

       Dependency direction: App ──references──→ Core
       Inversion:           Core ──uses──→ IActivityTracker (defined in Core, implemented in App)
```

---

## Persistence

| Store | Technology | Location | Content |
|---|---|---|---|
| **Sync State** | SQLite | `%LOCALAPPDATA%\CloudDrive\syncstate.db` | File metadata, ETags, sync status, conflict records |
| **Settings** | JSON | `%LOCALAPPDATA%\CloudDrive\settings.json` | WebDAV URL, sync path, intervals, UI preferences |
| **Credentials** | Windows Credential Manager | OS credential store | WebDAV password (DPAPI-protected) |
| **Logs** | Serilog (rolling file) | `%LOCALAPPDATA%\CloudDrive\logs\` | Structured diagnostic logs |

### SQLite Schema (sync_items)

| Column | Type | Purpose |
|---|---|---|
| `LocalPath` | TEXT (PK) | Absolute local file path |
| `RemotePath` | TEXT | WebDAV relative path |
| `FileSize` | INTEGER | File size in bytes |
| `RemoteETag` | TEXT | Server-side content hash |
| `SyncStatus` | INTEGER | Current sync state (enum) |
| `CreatedAt` | TEXT | First seen timestamp |
| `UpdatedAt` | TEXT | Last state change |

### SyncStatus Values

```
CloudOnly ──→ file exists as placeholder only (not downloaded)
Synced ──────→ local content matches remote
Syncing ─────→ transfer in progress
PendingUpload → local changes waiting to upload
PendingDownload → remote changes waiting to download
Error ───────→ sync operation failed
Conflict ────→ local and remote both changed
RemoteDeletePendingLocalCleanup → server-side deletion, local cleanup pending
```

---

## NuGet Dependencies

```
CloudDrive.App
├── CloudDrive.Core (project reference)
├── Hardcodet.NotifyIcon.Wpf          1.1.0    System tray integration
├── CommunityToolkit.Mvvm             8.4.0    MVVM source generators
├── Microsoft.Extensions.Hosting      9.0.3    DI container, hosted services
└── Serilog.*                         9.0.0    Structured logging

CloudDrive.Core
├── Vanara.PInvoke.CldApi             4.0.4    Cloud Files API P/Invoke
├── WebDav.Client                     2.9.0    WebDAV HTTP operations
├── Microsoft.Data.Sqlite             9.0.3    SQLite database
├── System.Security.Cryptography
│   .ProtectedData                    10.0.3   DPAPI credential encryption
└── Serilog.*                         9.0.0    Structured logging

CloudDrive.Watchdog
├── CloudDrive.Core (project reference)
└── Microsoft.Extensions.Hosting
    .WindowsServices                  9.0.3    Windows Service hosting
```

---

## Key Architectural Patterns

| Pattern | Where | Why |
|---|---|---|
| **Dependency Injection** | `App.xaml.cs` via `Microsoft.Extensions.Hosting` | Loose coupling, testability |
| **MVVM** | `SettingsViewModel` via `CommunityToolkit.Mvvm` | Clean UI/logic separation |
| **State Machine** | `MountStateMachine` | Validated mount lifecycle transitions |
| **Event-Driven** | `StateChanged`, `OnPhaseChanged`, `Channel<T>` queues | Async decoupled communication |
| **Dependency Inversion** | `IActivityTracker` defined in Core, implemented in App | Core stays UI-agnostic |
| **Rate Limiting** | `RateLimiter` (token bucket, 45/30s) | Prevent WebDAV server overload |
| **Retry with Backoff** | `RetryPolicy` | Resilient HTTP operations |
| **RAII / Disposable Scope** | `IActivityScope` | Automatic activity lifecycle tracking |

---

## Project File Structure

```
src/
├── CloudDrive.App/
│   ├── App.xaml.cs                          Entry point, DI, lifecycle
│   ├── Services/
│   │   └── SyncEngineHostedService.cs       Bridge between App and Core
│   ├── ViewModels/
│   │   ├── SettingsViewModel.cs             Settings MVVM
│   │   └── DashboardViewSupport.cs          Dashboard context
│   ├── Views/
│   │   ├── SettingsWindow.xaml              Settings UI
│   │   └── ActivityPanel.xaml               Activity flyout
│   └── TrayIcon/
│       └── TrayIconManager.cs               System tray integration
│
├── CloudDrive.Core/
│   ├── SyncEngine/
│   │   ├── SyncCoordinator.cs               Central orchestrator
│   │   ├── PlaceholderManager.cs            FETCH_PLACEHOLDERS handler
│   │   ├── HydrationHandler.cs              FETCH_DATA handler
│   │   ├── DehydrationHandler.cs            NOTIFY_DEHYDRATE handler
│   │   ├── UploadManager.cs                 Local → remote sync
│   │   ├── RemoteChangeDetector.cs          Remote → local polling
│   │   ├── LocalChangeWatcher.cs            FileSystemWatcher wrapper
│   │   ├── SyncProjectionService.cs         Local vs remote reconciliation
│   │   ├── SyncItemStateService.cs          Database CRUD
│   │   └── SyncProblemService.cs            Conflict tracking
│   ├── SyncRoot/
│   │   ├── SyncRootRegistrar.cs             WinRT registration
│   │   ├── SyncRootConnector.cs             cfapi callback dispatcher
│   │   └── MountStateMachine.cs             Connection lifecycle
│   ├── WebDav/
│   │   ├── WebDavService.cs                 HTTP operations
│   │   ├── WebDavAuthHandler.cs             Authentication
│   │   └── RateLimiter.cs                   Request throttling
│   ├── Data/
│   │   ├── SyncStateDb.cs                   SQLite wrapper
│   │   └── SyncItem.cs                      ORM entity
│   ├── Helpers/
│   │   ├── CloudFilePlaceholderHelper.cs     cfapi state queries
│   │   ├── PathMapper.cs                    Local ↔ remote paths
│   │   ├── FileHasher.cs                    Content hashing
│   │   └── ConflictResolver.cs              Rename conflict handling
│   └── Configuration/
│       ├── AppSettings.cs                   Settings model
│       └── CredentialManager.cs             Windows Credential Store
│
├── CloudDrive.Watchdog/
│   └── (Windows Service that monitors CloudDrive.App)
│
tests/
└── CloudDrive.Core.Tests/
    ├── E2E/
    │   └── FullLifecycleTest.cs             Integration tests
    └── Infrastructure/
        └── E2ETestFixture.cs                Test setup
```
