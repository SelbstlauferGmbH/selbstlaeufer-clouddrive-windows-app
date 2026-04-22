# Local Build And Deploy Guide

## Purpose

Use this guide when you need to build, sign, test, and publish a CloudDrive production installer or update.

Production releases are built and uploaded from the authorized local Windows PC. The code-signing certificate is backed by a hardware token, so production installer builds must not run in GitHub Actions or any other hosted CI environment.

GitHub is still the public distribution point. The local PC builds the final installer and uploads the complete Velopack release asset set to GitHub Releases.

---

## Release Rules

- Build production installers only on the authorized signing PC.
- Keep the hardware certificate token local. Do not export it into CI secrets.
- Upload every generated Velopack asset, not only the setup executable.
- Test the exact staged setup executable before publishing when possible.
- Publish updates by creating a higher SemVer version and uploading the new complete asset set.
- Keep the public GitHub release tag and the package version aligned.

---

## Prerequisites

On the release PC:

- Windows 11, build 22621 or later
- .NET 9 SDK
- Windows SDK signing tools, including `signtool.exe`
- Velopack CLI pinned to the project version
- Git
- access to push tags to the public repository
- a GitHub token that can write release contents
- the hardware code-signing token connected and unlocked
- the signing certificate visible in `Cert:\CurrentUser\My` or `Cert:\LocalMachine\My`

Install or update the Velopack CLI:

```powershell
dotnet tool install -g vpk --version 0.0.1298
```

If it is already installed:

```powershell
dotnet tool update -g vpk --version 0.0.1298
```

Open a new PowerShell window after installing global .NET tools.

---

## Find The Signing Certificate

List likely code-signing certificates:

```powershell
Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
    Where-Object { $_.HasPrivateKey } |
    Select-Object Subject, Thumbprint, NotAfter
```

Copy the SHA1 thumbprint for the production certificate. Remove spaces if the certificate UI inserted any.

In examples below:

```powershell
$thumbprint = "<SHA1 certificate thumbprint>"
```

---

## Prepare The GitHub Token

Set a token only in the current shell:

```powershell
$env:GH_TOKEN = "<github token>"
```

For a public repository, use a fine-grained token with contents read/write access to this repository, or a classic token with suitable public repository permissions.

Do not commit tokens to `.env`, scripts, docs, or shell profiles on shared machines.

---

## Step 1: Prepare The Release Commit

From the repository root:

```powershell
git status --short
dotnet test CloudDrive.sln -c Release
```

Make sure all intended changes are committed:

```powershell
git status --short
```

The release script expects a clean tree for production. Use `-AllowDirty` only for non-production experiments.

---

## Interactive Release Wizard

For a guided local release, run:

```powershell
.\build\release-wizard.ps1
```

The wizard asks yes/no questions for the local release flow, offers `major` and `minor` version bump choices based on the latest `v<version>` tag, stages a signed release first, pauses for smoke testing, and then uploads the exact staged assets to GitHub Releases.

---

## Step 2: Choose The Version

Use SemVer:

```powershell
$version = "1.2.3"
```

Use a higher version for every public update. Velopack update discovery depends on version ordering.

---

## Step 3: Create And Push The Tag

Create the release tag on the exact commit that will be built:

```powershell
git tag "v$version"
git push origin "v$version"
```

The local release script checks that `v$version` exists and points at `HEAD`.

---

## Step 4: Stage A Signed Release

Build and sign the release without uploading it yet:

```powershell
.\build\publish-release.ps1 `
    -Version $version `
    -SignThumbprint $thumbprint `
    -NoUpload
```

What this does:

1. Checks the git tag and working tree.
2. Clears the default staging folder under `build\releases\stable`.
3. Downloads previous `stable` release assets from GitHub when available.
4. Runs `build\pack.ps1`.
5. Publishes the app with `dotnet publish`.
6. Signs application DLL and EXE files with the local hardware-token certificate.
7. Writes and validates the final integrity manifest.
8. Packs the Velopack installer and update artifacts.
9. Validates signatures in the packed payload and setup executable.
10. Leaves the staged assets on disk for testing.

For the first release on a channel, the previous-release download may warn. That is expected.

---

## Step 5: Verify The Staged Assets

The default staging folder is:

```text
build\releases\stable
```

Expected files include:

```text
assets.stable.json
releases.stable.json
RELEASES-stable
SelbstlaeuferGmbH.CloudDrive-1.2.3-stable-full.nupkg
SelbstlaeuferGmbH.CloudDrive-stable-Setup.exe
```

Check the setup signature:

```powershell
Get-AuthenticodeSignature .\build\releases\stable\SelbstlaeuferGmbH.CloudDrive-stable-Setup.exe
```

Expected status:

```text
Valid
```

Run the setup executable from the staging folder and verify at least:

- setup completes without warnings
- the installed app launches
- the tray icon appears
- settings and logs are still under `%LOCALAPPDATA%\CloudDrive`
- update checks are available in the installed app
- watchdog files are present in the install payload

Use a clean Windows profile or VM for release validation when possible.

---

## Step 6: Upload The Tested Release

After testing, upload the same staged artifacts:

```powershell
.\build\publish-release.ps1 -Version $version -SkipBuild
```

This validates that the staged files still exist, then uploads them to GitHub Releases with Velopack:

```powershell
vpk upload github --publish --merge
```

The upload includes the final installer and every update manifest/package file needed by installed clients.

---

## Step 7: Verify GitHub Releases

Open:

```text
https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app/releases
```

Verify:

- release `v1.2.3` exists
- the release is public
- all staged assets are attached
- `SelbstlaeuferGmbH.CloudDrive-stable-Setup.exe` is present
- `releases.stable.json`, `assets.stable.json`, and `RELEASES-stable` are present
- the `.nupkg` full package is present

Do not manually delete Velopack manifest files from the release.

---

## One-Command Publish

For a trusted run where separate installer testing is not needed:

```powershell
.\build\publish-release.ps1 `
    -Version $version `
    -SignThumbprint $thumbprint
```

This stages, signs, validates, and uploads in one run.

The staged two-step flow is preferred for production because it avoids rebuilding between test and upload.

---

## Publishing Updates

Use the same workflow for updates:

1. Commit the update changes.
2. Choose a higher SemVer version.
3. Create and push `v<version>`.
4. Stage a signed release with `-NoUpload`.
5. Test the exact setup executable.
6. Upload with `-SkipBuild`.
7. Verify the GitHub release assets.

Installed apps on the `stable` channel check GitHub Releases, download the update in the background, and show `Install update now` in the tray menu when ready.

---

## Useful Script Options

| Option | Use |
|---|---|
| `-Version` | Required SemVer package version |
| `-SignThumbprint` | Required for signed production builds |
| `-NoUpload` | Stage, sign, and validate assets without uploading |
| `-SkipBuild` | Upload already staged assets without rebuilding |
| `-Channel` | Override the Velopack channel, default `stable` |
| `-OutputDir` | Override the staging directory |
| `-GitHubToken` | Pass a token explicitly instead of using `GH_TOKEN` or `GITHUB_TOKEN` |
| `-SkipPreviousDownload` | Build without downloading previous release assets |
| `-FailOnPreviousDownloadError` | Fail if previous release assets cannot be downloaded |
| `-KeepOutput` | Do not clear the staging directory before building |
| `-AllowDirty` | Allow a dirty working tree for non-production builds |
| `-SkipGitChecks` | Skip tag and working-tree checks |

---

## Low-Level Local Package Only

For a local package that is not intended for public release:

```powershell
.\build\pack.ps1 -Version 1.2.3
```

For a signed local package without upload:

```powershell
.\build\pack.ps1 -Version 1.2.3 -SignThumbprint $thumbprint
```

Use `build\publish-release.ps1` for production staging and GitHub upload because it also handles previous release asset download, git checks, and upload validation.

---

## Troubleshooting

### The Script Says The Working Tree Is Dirty

Run:

```powershell
git status --short
```

Commit or stash the changes before a production release.

### The Script Says The Tag Is Missing

Create and push the tag:

```powershell
git tag "v$version"
git push origin "v$version"
```

### The Previous Release Download Fails

For the first release on a channel, continue.

For later releases, check:

- the GitHub token
- repository access
- release visibility
- channel name

Use `-FailOnPreviousDownloadError` when you want this to be a hard failure.

### Signing Prompts Repeatedly

The script signs multiple DLL and EXE files. Hardware-token middleware may ask for confirmation or a PIN. Configure token PIN caching on the release PC if the vendor tooling supports it.

### Upload Says A Token Is Required

Set:

```powershell
$env:GH_TOKEN = "<github token>"
```

Then rerun:

```powershell
.\build\publish-release.ps1 -Version $version -SkipBuild
```

### Users Do Not Receive The Update

Check:

- the installed app version is lower than the published version
- the GitHub release is public
- all Velopack assets are attached
- the app was installed through the Velopack setup executable
- the app has been running at least 30 seconds
- the tray menu shows `Install update now` after download

---

## Release Checklist

Use this checklist before calling a release done:

- release commit is clean
- tests passed locally
- tag `v<version>` exists and is pushed
- hardware token was used for signing
- setup executable signature is valid
- staged setup executable was installed and launched
- all Velopack assets were uploaded
- GitHub release is public
- update path was smoke-tested or scheduled for verification
