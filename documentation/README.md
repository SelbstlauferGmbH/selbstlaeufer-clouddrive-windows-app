# CloudDrive Documentation

## Purpose

This folder contains the project documentation for CloudDrive.

The goal of this entry page is to give a new reader:

- a short understanding of what CloudDrive is
- orientation through the documentation set
- a recommended reading order
- direct links to the detailed documents

---

## What CloudDrive Is

CloudDrive is a Windows 11 desktop application that exposes a WebDAV-backed storage area as a native Windows sync folder.

At a high level, the system combines:

- a WPF desktop application for UI, tray behavior, onboarding, and settings
- a sync engine built on the Windows Cloud Files API
- a local data layer for settings, sync state, and logs
- a watchdog component for runtime supervision
- an installer and auto-update system built on Velopack

The end-user experience is intended to feel similar to a OneDrive-style sync client:

- files appear in Explorer as cloud-backed items
- content can be hydrated on demand
- local changes are uploaded back to the server
- the app can be installed and updated through a normal Windows setup flow

---

## Documentation Map

The current documentation set is intentionally small and split by concern.

| Document | Use It For | Best Audience |
|---|---|---|
| [Core architecture](./architecture.md) | System structure, component responsibilities, sync flow, persistence, dependencies | Developers, reviewers, contributors |
| [Installer and auto-updater](./installer-and-updater.md) | Packaging, release flow, signing, GitHub Releases, runtime update behavior, troubleshooting | Developers, release engineers, maintainers |
| [Local build and deploy guide](./build-and-deploy.md) | Step-by-step local signed build, installer test, and GitHub release upload workflow | Developers, release engineers, maintainers |
| [Activity stream UI specification](./activity-stream-ui-spec.md) | Tray flyout behavior, activity stream layout, UI states, settings surface expectations | Designers, frontend/UI implementers, contributors |

---

## Recommended Reading Order

### If You Are New To The Project

1. Read this page.
2. Read [Core architecture](./architecture.md).
3. Read [Activity stream UI specification](./activity-stream-ui-spec.md) if you are working on tray, activity, or settings UI.
4. Read [Installer and auto-updater](./installer-and-updater.md) if you need to build, package, sign, or release the app.
5. Read [Local build and deploy guide](./build-and-deploy.md) before publishing a production installer or update.

### If You Need To Understand Runtime Behavior

Start with:

- [Core architecture](./architecture.md)

Then continue with:

- [Installer and auto-updater](./installer-and-updater.md)

This second document matters if the question is about:

- startup behavior
- installed-app vs dev-mode behavior
- how updates are discovered and applied

### If You Need To Ship A Release

Start with:

- [Local build and deploy guide](./build-and-deploy.md)

Then use:

- [Installer and auto-updater](./installer-and-updater.md)

Focus especially on:

- hardware-token signing prerequisites
- `build/publish-release.ps1`
- staged installer verification
- complete GitHub release asset upload
- updater verification and troubleshooting

---

## System At A Glance

The repository currently revolves around these main parts:

| Part | Role |
|---|---|
| `src/CloudDrive.App` | Desktop application, tray UI, windows, hosted services |
| `src/CloudDrive.Core` | Sync engine, WebDAV integration, persistence, domain logic |
| `src/CloudDrive.Watchdog` | Runtime watchdog and lifecycle supervision |
| `tests/CloudDrive.Core.Tests` | Unit and end-to-end tests |
| `build/` | Build and packaging scripts |

Production releases are not built in CI. They are built, signed, tested, and uploaded from the authorized local signing PC.

If you need implementation-level detail, the linked documents above drill into these areas.

---

## What Each Document Does Not Try To Cover

This entry page is intentionally short.

It does not attempt to repeat:

- the full sync engine architecture
- the complete release procedure
- every operational detail of packaging and updates

Those details belong in the topic-specific documents.

---

## Documentation Principles

This documentation set should stay:

- implementation-aligned
- concise at the top level
- detailed in the topic documents
- practical for contributors and maintainers

When behavior changes in code, update the relevant detailed document and keep this index aligned with the available pages.

---

## Quick Links

- [Core architecture](./architecture.md)
- [Installer and auto-updater](./installer-and-updater.md)
- [Local build and deploy guide](./build-and-deploy.md)
- [Activity stream UI specification](./activity-stream-ui-spec.md)

