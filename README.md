# CloudDrive

**The best WebDAV client for Windows 11.**

CloudDrive is an open source Windows 11 desktop app that turns your WebDAV server into a proper, OneDrive-style Explorer experience. Files show up as native cloud placeholders, download when you open them, upload when you change them, and generally behave like they belong on your machine instead of being held together by hope, mapped drives, and ancient rituals.

If you want WebDAV on Windows 11 to feel modern, native, and just a little bit smug about it, this is the app.

## Why CloudDrive Exists

Because WebDAV deserves better than "technically connected" and "please refresh Explorer again."

CloudDrive uses the Windows Cloud Files API to make your WebDAV storage feel like a real Windows 11 sync client:

- native Explorer integration instead of a sad network-drive cosplay
- cloud-only placeholders that do not eat disk space up front
- on-demand hydration when files are opened
- automatic upload of local edits back to your server
- proper sync state tracking, health checks, and diagnostics
- tray controls, onboarding, and update support built for normal humans

## Cool Stuff We Built Into It

### Native Windows 11 Integration

- Registers as a real sync root in Windows Explorer
- Shows files and folders as cloud-backed placeholders
- Supports cloud-only, synced, syncing, paused, and disconnected states
- Updates Explorer metadata and status as sync state changes

### Real WebDAV Sync, Not Wishful Thinking

- Connects to WebDAV endpoints with `Basic`, `NTLM`, or `Negotiate` auth
- Downloads file contents on demand when Windows requests them
- Uploads create, edit, rename, and delete operations back to the server
- Detects remote changes through polling and reconciles them locally
- Uses ETags and a local sync database to keep state consistent

### Disk Space Friendly By Design

- Starts with placeholder files instead of downloading everything immediately
- Hydrates files only when they are actually opened
- Supports dehydration / "Free up space" workflows to reclaim local storage
- Keeps large transfers inside the Windows file experience instead of inventing a parallel universe

### Built For Daily Use

- Starts from a configuration wizard instead of throwing config files at you
- Lives in the system tray with quick access to settings, activity, pause/resume, and quit
- Supports launch on startup, theme selection, and language selection
- Stores credentials securely using Windows protection mechanisms

### Visibility Instead Of Mystery

- Activity stream for downloads, uploads, sync operations, and startup events
- Health checks for WebDAV connectivity, sync root state, database state, updater reachability, and watchdog status
- Problem tracking for connection errors, upload failures, remote sync problems, and conflicts
- CSV export for activity history when you need receipts

### Extra Reliability Features

- Conflict handling that preserves a local conflict copy instead of eating your work
- Watchdog support that helps keep Explorer state honest when the app or server misbehaves
- Stale sync-root cleanup to recover from old or broken registrations
- Background update flow for installed builds

## What It Feels Like To Use

1. Install CloudDrive or build it from source.
2. Launch the app and walk through the setup wizard.
3. Enter your WebDAV URL, username, password, and authentication type.
4. Pick your sync root folder and preferences.
5. Open the sync root in Explorer and use it like a normal folder.

Behind the scenes, CloudDrive registers the sync root, creates placeholders, hydrates files on demand, uploads local changes, watches for remote updates, and keeps track of what is synced, what is busy, and what needs attention.

The detailed walkthrough lives in [USAGE.md](./USAGE.md).

## Quick Start

### Download A Release

Release builds are intended to be published through GitHub Releases:

- [GitHub Releases](https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app/releases)

Production installers and updates are built, signed, and uploaded from the authorized local signing PC because the code-signing certificate is backed by a hardware token. The step-by-step release process lives in [Local build and deploy guide](./documentation/build-and-deploy.md).

### Build From Source

Prerequisites:

- Windows 11
- .NET 9 SDK
- a reachable WebDAV server

Build:

```powershell
dotnet build CloudDrive.sln -c Release
```

Run:

```powershell
.\src\CloudDrive.App\bin\Release\net9.0-windows10.0.22621.0\CloudDrive.App.exe
```

If you want the full implementation and release details, start here:

- [Usage guide](./USAGE.md)
- [Documentation index](./documentation/README.md)
- [Core architecture](./documentation/architecture.md)
- [Installer and auto-updater](./documentation/installer-and-updater.md)
- [Local build and deploy guide](./documentation/build-and-deploy.md)

## Who We Are

CloudDrive is built by [Selbstläufer GmbH](https://selbstlaeufer.gmbh/), a managed server provider from Hamburg, Germany.

We run our own datacenter infrastructure and host native applications for customers who want the comfort of modern cloud software without giving up control over where their systems live. CloudDrive started with those customers in mind: people who already trust WebDAV, but want it to feel like a current, native Windows experience instead of a compromise.

Our team works fully remote and covers the whole path from system administration and datacenter operations to software tooling and product development. That full-circle view shapes the app: practical infrastructure underneath, a clean daily workflow on top, and fewer reasons for users to think about the machinery.

We are happy to make CloudDrive useful beyond our own customer base. If you want a WebDAV client that feels closer to today's best-practice cloud apps, you are very welcome here.

## Contributing

Contributions are welcome.

If you want to help, the most useful contributions are:

- bug reports with reproduction steps
- WebDAV server compatibility feedback
- UX improvements for the Windows 11 flow
- tests for sync edge cases and regression scenarios
- focused pull requests with clear behavior changes
- documentation improvements when code behavior changes

Please keep changes practical and reviewable. Small, sharp pull requests beat heroic rewrites. If you change sync behavior, Explorer integration, or release/install logic, include enough context for someone else to verify it without telepathy.

Please open a [GitHub Issue](https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app/issues) for bug reports, ideas, compatibility notes, or larger contribution proposals. That keeps the conversation visible and gives us one place to track what needs attention.

For a good starting point, read:

- [Documentation index](./documentation/README.md)
- [Core architecture](./documentation/architecture.md)

## License

CloudDrive is licensed under the GNU General Public License v3.0. See [LICENSE](./LICENSE) for the full license text.

## Feedback

We are happy about feedback.

If CloudDrive works great with your setup, tell us. If it breaks in a creative new way, definitely tell us. Issues, ideas, rough edges, server compatibility notes, and improvement suggestions are all useful and welcome through [GitHub Issues](https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app/issues).

## In One Sentence

CloudDrive makes WebDAV on Windows 11 feel like it finally received adult supervision.
