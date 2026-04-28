# CloudDrive Installer And Auto-Updater

## Documentation Context

This is one document within the CloudDrive documentation set.

Use this page when you need to understand the installer, updater, package identity, release assets, signing behavior, and runtime update flow.

For the step-by-step developer release procedure, use:

- [Local build and deploy guide](./build-and-deploy.md)

For a documentation entry point and reading guide, start with:

- [Documentation home](./README.md)

For system-level runtime and component structure, see:

- [Core architecture](./architecture.md)

---

## Purpose

This document describes the installer and auto-update system used by CloudDrive:

- how the runtime update integration works
- how local packaging works
- how signing works with a local certificate or hardware token
- how GitHub Releases are used as the public update source
- which release assets must be published together
- how to troubleshoot packaging and update problems

Production installer builds are **local-only**. The production certificate is held on a hardware token, so GitHub Actions or any other hosted CI runner must not build, sign, or publish production releases.

---

## Scope

CloudDrive ships as a Windows 11 x64 WPF desktop application on .NET 9.

The installer and updater stack is built on **Velopack** and currently provides:

- a per-user Windows installer (`Setup.exe`)
- background update checks
- silent background download of available updates
- tray notification when an update is ready
- user-triggered install-and-restart
- Authenticode signing for production builds
- GitHub Releases as the active public release source

The current implementation uses the `stable` release channel by default.

---

## Where The Implementation Lives

| Area | File | Responsibility |
|---|---|---|
| App startup integration | `src/CloudDrive.App/Program.cs` | Calls Velopack before any WPF startup code |
| WPF project setup | `src/CloudDrive.App/CloudDrive.App.csproj` | Enables custom startup entry point and adds Velopack package |
| Update background service | `src/CloudDrive.App/Services/UpdateService.cs` | Checks, downloads, and applies updates |
| App wiring | `src/CloudDrive.App/App.xaml.cs` | Registers `UpdateService` and bridges update events into the UI |
| Tray UI | `src/CloudDrive.App/Tray/TrayIconManager.cs` | Shows update menu entry and tray notification |
| Localization | `src/CloudDrive.Core/Localization/AppStrings.resx` and `AppStrings.de.resx` | Update-ready strings |
| Low-level packaging | `build/pack.ps1` | Publishes, signs, validates, and packs installer/update artifacts |
| Local release workflow | `build/publish-release.ps1` | Downloads previous release assets, stages a signed release, and uploads assets from the local PC |

There is intentionally no CI build workflow for releases.

---

## High-Level Design

The installer/updater flow has three layers:

1. **Application startup integration**
   CloudDrive starts through a custom `Program.Main()` entry point. Velopack lifecycle handling runs first, then WPF starts.
2. **Background update runtime**
   Once the app is running as an installed Velopack app, `UpdateService` periodically checks GitHub Releases, downloads updates in the background, and raises a UI event when the update is ready.
3. **Local packaging and release distribution**
   A developer uses the authorized signing PC to build and sign the release. The same PC uploads the complete Velopack asset set, including the final setup executable, to the public GitHub release.

---

## Runtime Behavior

### Application Startup

CloudDrive does not rely on the default WPF-generated `Main()`.

Instead:

- `Program.Main()` is the startup entry point
- `VelopackApp.Build().Run()` is called first
- only after that does the WPF `App` instance start

This matters because Velopack needs to intercept installer and update lifecycle events before the rest of the application boots.

### Installed App Detection

The updater does **not** run in normal developer launches.

`UpdateService` checks:

- `UpdateManager.IsInstalled`

If the app is not running as a Velopack-installed application, the service logs that update checks are skipped and exits early.

This prevents:

- noisy failed update checks during local development
- accidental update behavior when running from `bin\Debug` or `bin\Release`

### Update Check Schedule

Current behavior:

- initial delay: **30 seconds** after app startup
- recurring interval: **every 4 hours**

This is implemented in `UpdateService`.

### Update Source

The active update source is:

```text
https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app
```

`UpdateService` uses `GithubSource(...)`, so GitHub Releases is the authoritative release feed.

Important naming note:

- Windows-visible product names use `Selbstlaeufer` or the localized brand spelling where encoding is safe
- ASCII-only identifiers owned by this project use `Selbstlaeufer`
- immutable external identifiers keep their real upstream spelling, which currently includes the GitHub org slug `SelbstlauferGmbH`

### Download And Install Flow

When a new release is available:

1. `UpdateService` checks the release feed.
2. If a newer version exists, it downloads the update in the background.
3. After download completes, it stores the pending update and raises `UpdateReady`.
4. `App.xaml.cs` handles that event on the UI thread.
5. The tray icon shows an informational notification and the `Update and restart now` menu item.
6. When the user clicks that menu item, `ApplyUpdateAndRestart()` is called.
7. Velopack applies the update and restarts the application.

Important current behavior:

- update download is automatic
- update installation is **user-triggered**
- the app is **not** force-restarted automatically when a download finishes

### Persistent Data Boundaries

Application state and credentials remain outside the install directory:

- settings: `%LOCALAPPDATA%\CloudDrive\settings.json`
- logs: `%LOCALAPPDATA%\CloudDrive\logs\`
- sync database: `%LOCALAPPDATA%\CloudDrive\syncstate.db`

Updates do not replace user settings or runtime data.

---

## Package Identity And Naming

The current Velopack package configuration uses:

- package id: `SelbstlaeuferGmbH.CloudDrive`
- product title: `Selbstlaeufer CloudDrive`
- authors: `Selbstlaeufer GmbH`
- channel: `stable` by default

### Why The Package ID Is Not `CloudDrive`

CloudDrive already stores runtime data under `%LOCALAPPDATA%\CloudDrive`.

Using `CloudDrive` as the Velopack package id would make installer-managed application files and application data too easy to confuse and potentially collide, depending on installer path conventions and operational tooling.

The package id is therefore intentionally namespaced:

```text
SelbstlaeuferGmbH.CloudDrive
```

---

## Local Packaging

The low-level packaging entry point is:

```powershell
.\build\pack.ps1 -Version 1.2.3 -SignThumbprint <certificate-thumbprint>
```

`build/pack.ps1` performs these steps:

1. Validates the version and channel values.
2. Resolves repository-local paths safely.
3. Validates the signing certificate when `-SignThumbprint` is provided.
4. Runs `dotnet publish` for `CloudDrive.App`.
5. Signs DLL and EXE files before the final integrity manifest is written.
6. Writes and validates the final integrity manifest.
7. Runs `vpk pack`.
8. Validates packed payload signatures and the setup executable signature when signing was requested.
9. Prints the generated release assets.

The developer-facing release wrapper is:

```powershell
.\build\publish-release.ps1 -Version 1.2.3 -SignThumbprint <certificate-thumbprint>
```

For production, prefer the staged flow in [Local build and deploy guide](./build-and-deploy.md), because it lets the exact setup executable be tested before upload.

---

## Signing

Production releases must be signed locally with the hardware-token-backed certificate.

The signing certificate must be visible in one of these stores on the release PC:

- `Cert:\CurrentUser\My`
- `Cert:\LocalMachine\My`

`build/pack.ps1` signs using:

- `signtool.exe`
- the SHA1 certificate thumbprint passed through `-SignThumbprint`
- timestamp server: `http://timestamp.sectigo.com`
- file digest: `sha256`
- timestamp digest: `sha256`

Unsigned packaging is still possible with `build/pack.ps1` for local development experiments, but it is not a production release path.

---

## Generated Artifacts

A typical `stable` packaging run produces files like:

```text
assets.stable.json
releases.stable.json
RELEASES-stable
SelbstlaeuferGmbH.CloudDrive-1.2.3-stable-full.nupkg
SelbstlaeuferGmbH.CloudDrive-stable-Setup.exe
```

Notes:

- the setup executable name is channel-based, not version-based
- the `.nupkg` contains the application payload for installation and updates
- `releases.stable.json` and `assets.stable.json` are Velopack manifests
- `RELEASES-stable` is emitted by the current tooling
- portable artifacts are intentionally disabled because the pack script passes `--noPortable`

End users should run:

```text
SelbstlaeuferGmbH.CloudDrive-stable-Setup.exe
```

For GitHub-based updates, the release must contain the complete generated artifact set for the channel. Do not upload only the setup executable.

---

## Delta Updates And Previous Releases

Delta generation depends on previous release information being available in the output directory before `vpk pack` runs.

`build/publish-release.ps1` handles this by calling:

```powershell
vpk download github
```

before it builds the new package.

For the first release on a channel:

- there are no previous release assets
- the download step may warn or fail
- packaging still works
- only a full package path exists

For later releases:

- the previous channel assets should be downloaded first
- this gives Velopack the information it needs to build optimal update metadata
- the complete current artifact set must be uploaded after the build

---

## GitHub Release Distribution

The public release source is GitHub Releases in:

```text
https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app
```

The local release wrapper uploads with:

```powershell
vpk upload github --publish --merge
```

This keeps release assets, manifests, and channel metadata aligned with Velopack's expectations.

GitHub upload requires write access on the release PC. The scripts first try an authenticated GitHub CLI session:

```powershell
gh auth login
```

If GitHub CLI is not available, use either:

- `GH_TOKEN`
- `GITHUB_TOKEN`
- `-GitHubToken`

For a public repository, use a fine-grained token with contents read/write access to this repository, or a classic token with the appropriate public repository permissions.

---

## Channels

The current runtime configuration is centered on:

```text
stable
```

`build/pack.ps1` and `build/publish-release.ps1` support other channel names, but the application currently hardcodes a GitHub source with `prerelease: false`.

If additional channels are introduced later, review these together:

- runtime update source logic
- release tagging convention
- GitHub release visibility
- package output channel
- developer release guide

---

## Troubleshooting

### `vpk` Not Found

Install the pinned Velopack CLI:

```powershell
dotnet tool install -g vpk --version 0.0.1298
```

Then open a new shell and retry.

### PowerShell Blocks The Script

Use:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\publish-release.ps1 -Version 1.2.3 -SignThumbprint <thumbprint> -NoUpload
```

### Signing Fails

Check:

- the hardware token is connected
- the token middleware is running
- the certificate is visible in `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My`
- `signtool.exe` is installed through the Windows SDK
- the thumbprint is the SHA1 thumbprint without spaces or hidden characters
- any token PIN prompt was completed

### Upload Fails

Check:

- GitHub CLI is authenticated with `gh auth login`, or `GH_TOKEN` / `GITHUB_TOKEN` is set in the current shell
- the token can write release contents for the public repository
- the local tag exists and matches the version
- the tag has been pushed to `origin`
- the staged output directory still contains the full Velopack asset set

### App Never Checks For Updates

Check:

- is this really an installed Velopack app, or a dev build?
- is the release published to the correct GitHub repository?
- are all Velopack artifacts present in the GitHub release?
- is the published version higher than the installed version?

---

## Maintenance Checklist

When changing the installer/updater system, review all of these together:

1. `src/CloudDrive.App/Program.cs`
2. `src/CloudDrive.App/CloudDrive.App.csproj`
3. `src/CloudDrive.App/Services/UpdateService.cs`
4. `src/CloudDrive.App/App.xaml.cs`
5. `src/CloudDrive.App/Tray/TrayIconManager.cs`
6. `build/pack.ps1`
7. `build/publish-release.ps1`
8. `documentation/build-and-deploy.md`

Keep these details aligned:

- Velopack NuGet version
- `vpk` CLI version
- runtime and framework metadata passed to `vpk pack`
- channel naming
- GitHub release source URL
- signing behavior
- release asset upload behavior

---

## Related Documentation

- [Documentation home](./README.md)
- [Core architecture](./architecture.md)
- [Local build and deploy guide](./build-and-deploy.md)
