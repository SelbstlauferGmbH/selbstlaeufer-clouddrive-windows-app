# CloudDrive Usage Guide

CloudDrive is a native Windows 11 WebDAV sync client. It registers a local folder as a Windows cloud sync root, shows remote files in Explorer as placeholders, downloads file contents on demand, and uploads local changes back to the WebDAV server.

## Prerequisites

- Windows 11, build 22621 or later.
- A reachable WebDAV endpoint and user credentials.
- .NET 9 SDK, only if you build from source.
- Basic authentication support on the WebDAV server for the current UI setup flow.

## Build From Source

From the repository root:

```powershell
dotnet build CloudDrive.sln -c Release -p:Platform=x64
```

Run the app from:

```powershell
.\src\CloudDrive.App\bin\Release\net9.0-windows10.0.22621.0\CloudDrive.App.exe
```

Release builds are intended to be installed through GitHub Releases when available.

## First Launch

If CloudDrive has no complete account configuration, it opens the setup wizard before starting the sync engine.

The wizard has two steps:

| Step | What To Choose |
|---|---|
| General | Language, theme, and whether CloudDrive should launch when Windows starts. |
| Account | WebDAV URL, username, and password. |

On the Account step:

1. Enter the full WebDAV URL, for example `https://server.example.com/dav/`.
2. Enter the WebDAV username.
3. Enter the password.
4. Click **Test connection**.
5. Click **Save and start**.

The password is stored locally as DPAPI-encrypted data for the current Windows user. The default sync root is `%USERPROFILE%\CloudDrive`.

## Startup Behavior

After setup, CloudDrive starts in the system tray and waits until the WebDAV server is ready before it registers and connects the sync root.

When the server is reachable, CloudDrive:

1. Registers the sync root with the Windows Cloud Files API.
2. Connects the Cloud Files callbacks for placeholder and hydration requests.
3. Starts watching local filesystem changes.
4. Runs the first remote scan immediately.
5. Opens Explorer to the sync root only after the startup health check succeeds.

If the server is unreachable, CloudDrive keeps running and waits for the server. The tray icon, activity stream, settings health checks, and watchdog status show the degraded state.

## Browsing Files

Open the sync root in Explorer and browse it like a normal folder.

- Remote items appear as cloud placeholders.
- Folder contents are fetched as you browse.
- Opening a file hydrates it, downloading its contents from WebDAV.
- Hydrated files can be used by normal desktop applications.

Explorer owns the shell status overlays. Depending on state, Windows may show cloud-only, available, syncing, or error indicators.

## Local Changes

When you create, edit, rename, or delete files inside the sync root:

- local changes are detected by a filesystem watcher
- uploads are queued and processed through WebDAV
- renames are sent as WebDAV `MOVE`
- deletions are sent as WebDAV `DELETE`
- sync status and activity history are updated

If the same file changes locally and remotely, CloudDrive preserves both versions. The remote version remains primary and the local version is kept as a conflict copy.

## Remote Changes

CloudDrive polls the WebDAV server on a configurable interval. The default is 30 seconds.

During each remote scan, CloudDrive:

1. Lists remote directories with WebDAV `PROPFIND`.
2. Compares remote metadata with the local sync database.
3. Creates placeholders for new remote items.
4. Updates changed items.
5. Removes local placeholders for deleted remote items.

The polling interval and transfer concurrency can be changed in **Settings > Network**. Changes to core sync settings may require restarting CloudDrive to fully apply to the running sync engine.

## Freeing Disk Space

CloudDrive supports Windows placeholder dehydration behavior. When a hydrated placeholder is unpinned or marked to free local space, CloudDrive can remove the local file content while keeping the remote file available on the server.

Opening the file again downloads the content again.

## Tray And Activity Stream

CloudDrive lives in the Windows system tray.

| Action | Result |
|---|---|
| Left-click tray icon | Toggle the activity stream flyout. |
| Right-click tray icon | Open the tray menu. |

The tray menu includes:

| Menu Item | Action |
|---|---|
| **Open activity stream** | Opens the activity flyout. |
| **Open settings** | Opens the settings/control panel window. |
| **Pause sync** / **Resume sync** | Pauses or resumes sync work. |
| **Install update now** | Appears only when an update is ready. |
| **Quit** | Stops syncing and exits the app. |

The activity stream gives quick access to recent sync events, current health, open folder, manual sync, web portal, and settings shortcuts.

## Settings

Open Settings from the tray menu or activity stream.

The settings window contains:

| Section | Purpose |
|---|---|
| Activity | Recent sync activity with filtering and export. |
| Problems | Open sync problems and dismissible issue records. |
| Statistics | Local sync index, status counts, and storage summary. |
| Health Check | WebDAV, sync root, database, updater, and watchdog status. |
| General | Launch on startup, notifications, theme, and language. |
| Account | WebDAV URL, username, password, and connection testing. |
| Network | Polling interval and transfer concurrency. |
| Advanced | File logging, reset tools, and diagnostics. |

The current UI saves Basic authentication. The underlying settings model includes `AuthType`, but the visible setup and settings flows currently write `Basic`.

## Logs And Diagnostics

File logging is optional and can be enabled in **Settings > Advanced**.

When enabled, diagnostic logs are written to:

```text
%LOCALAPPDATA%\CloudDrive\logs\clouddrive-YYYYMMDD.log
```

CloudDrive also writes crash and integrity-failure logs to the same logs folder when needed.

## Local Data

CloudDrive stores local state under:

```text
%LOCALAPPDATA%\CloudDrive
```

| File Or Folder | Purpose |
|---|---|
| `settings.json` | Saved app settings, account URL, username, sync preferences, UI preferences. |
| `credentials.dat` | DPAPI-encrypted WebDAV password for the current Windows user. |
| `syncstate.db` | Local sync cache and problem/activity metadata. |
| `watchdog-status.json` | Latest watchdog status snapshot. |
| `pending-cleanup.json` | Temporary reset-cleanup marker, only present when cleanup must continue later. |
| `logs\` | Diagnostic, crash, and integrity logs. |

The sync root defaults to:

```text
%USERPROFILE%\CloudDrive
```

## Reset And Cleanup

The Advanced settings section contains reset actions.

- Reset local sync data removes the sync root and local sync metadata but preserves saved account settings.
- Reset configuration and logs removes saved account settings, stored password, app preferences, local metadata, and logs.

If Windows is still holding sync-root files open, CloudDrive may ask for a reboot and write `pending-cleanup.json` so cleanup can continue later.
