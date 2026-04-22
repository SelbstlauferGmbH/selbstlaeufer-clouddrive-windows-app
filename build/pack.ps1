[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version = "0.0.1-local",

    [Parameter()]
    [string]$SignThumbprint,

    [Parameter()]
    [string]$Channel = "stable",

    [Parameter()]
    [string]$OutputDir = "build/releases"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$env:DOTNET_CLI_HOME = Join-Path $script:RepoRoot ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null

function Test-SemVer {
    param([string]$Value)

    return $Value -match "^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
}

function Remove-WorkspaceDirectory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    if (-not $resolvedPath.StartsWith($script:RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove directory outside the repository root: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Find-SignTool {
    $fromPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) {
        return $fromPath.Source
    }

    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $windowsKitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
        if (Test-Path -LiteralPath $windowsKitsBin) {
            $kitDirectories = Get-ChildItem -LiteralPath $windowsKitsBin -Directory -ErrorAction SilentlyContinue |
                Sort-Object -Property @{ Expression = { try { [version]$_.Name } catch { [version]"0.0" } }; Descending = $true }

            foreach ($kitDirectory in $kitDirectories) {
                $candidates += Join-Path $kitDirectory.FullName "x64\signtool.exe"
            }
        }

        $candidates += Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\signtool.exe"
    }

    $signToolPath = $candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1

    if ($null -eq $signToolPath) {
        throw "Signing was requested but signtool.exe was not found on PATH or in the Windows SDK."
    }

    return $signToolPath
}

function Get-SignableFiles {
    param([string]$Directory)

    Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object { ".dll", ".exe" -contains $_.Extension.ToLowerInvariant() } |
        Sort-Object FullName
}

function Get-RelativeManifestPath {
    param(
        [string]$BaseDirectory,
        [string]$FilePath
    )

    $pathSeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $basePath = [System.IO.Path]::GetFullPath($BaseDirectory).TrimEnd($pathSeparators)
    $fullPath = [System.IO.Path]::GetFullPath($FilePath)

    if (-not $fullPath.StartsWith($basePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File '$fullPath' is outside '$basePath'."
    }

    return $fullPath.Substring($basePath.Length).
        TrimStart($pathSeparators).
        Replace("\", "/")
}

function Write-IntegrityManifest {
    param([string]$Directory)

    $manifestPath = Join-Path $Directory "integrity-manifest.json"
    $entries = @(
        Get-SignableFiles -Directory $Directory | ForEach-Object {
            [ordered]@{
                path = Get-RelativeManifestPath -BaseDirectory $Directory -FilePath $_.FullName
                hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            }
        }
    )

    if ($entries.Count -eq 0) {
        throw "No DLL/EXE files were found for the integrity manifest in $Directory."
    }

    $manifest = [ordered]@{
        generatedAt = (Get-Date).ToUniversalTime().ToString("O")
        algorithm = "SHA256"
        files = $entries
    }

    $json = $manifest | ConvertTo-Json -Depth 5
    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, $utf8NoBom)

    Write-Host "IntegrityManifest: wrote $($entries.Count) final file hashes to $manifestPath"
    return $manifestPath
}

function Test-IntegrityManifest {
    param(
        [string]$Directory,
        [string]$Context
    )

    $pathSeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $basePath = [System.IO.Path]::GetFullPath($Directory).TrimEnd($pathSeparators)
    $manifestPath = Join-Path $basePath "integrity-manifest.json"

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "$Context is missing integrity-manifest.json: $manifestPath"
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($null -eq $manifest.files -or $manifest.files.Count -eq 0) {
        throw "$Context integrity-manifest.json contains no files."
    }

    $failures = @()
    foreach ($entry in $manifest.files) {
        $relativePath = ([string]$entry.path).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
        $filePath = [System.IO.Path]::GetFullPath((Join-Path $basePath $relativePath))

        if (-not $filePath.StartsWith($basePath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures += "$($entry.path): path escapes package directory"
            continue
        }

        if (-not (Test-Path -LiteralPath $filePath)) {
            $failures += "$($entry.path): missing"
            continue
        }

        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actualHash, [string]$entry.hash, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures += "$($entry.path): hash mismatch"
        }
    }

    if ($failures.Count -gt 0) {
        $preview = ($failures | Select-Object -First 10) -join [Environment]::NewLine
        throw "$Context integrity validation failed for $($failures.Count) file(s):$([Environment]::NewLine)$preview"
    }

    Write-Host "$Context integrity validation passed for $($manifest.files.Count) file(s)."
}

function Assert-ValidAuthenticodeSignatures {
    param(
        [string]$Directory,
        [string]$Context
    )

    $files = @(Get-SignableFiles -Directory $Directory)
    $invalid = @(
        $files | ForEach-Object {
            $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
            if ($signature.Status -ne "Valid") {
                [pscustomobject]@{
                    Path = $_.FullName
                    Status = $signature.Status
                }
            }
        }
    )

    if ($invalid.Count -gt 0) {
        $preview = ($invalid | Select-Object -First 10 | ForEach-Object {
            "  $($_.Status): $($_.Path)"
        }) -join [Environment]::NewLine
        throw "$Context contains $($invalid.Count) unsigned or invalid DLL/EXE file(s):$([Environment]::NewLine)$preview"
    }

    Write-Host "$Context signature validation passed for $($files.Count) DLL/EXE file(s)."
}

function Invoke-CodeSigning {
    param(
        [string]$Directory,
        [string]$SignToolPath,
        [string]$Thumbprint,
        [int]$BatchSize = 100
    )

    $filesToSign = @(
        Get-SignableFiles -Directory $Directory | Where-Object {
            (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne "Valid"
        }
    )

    if ($filesToSign.Count -eq 0) {
        Write-Host "Publish output already contains valid signatures for all DLL/EXE files."
        return
    }

    Write-Host "Signing $($filesToSign.Count) publish DLL/EXE file(s) before writing the final integrity manifest..."
    for ($i = 0; $i -lt $filesToSign.Count; $i += $BatchSize) {
        $batch = @($filesToSign | Select-Object -Skip $i -First $BatchSize)
        $filePaths = @($batch | ForEach-Object { $_.FullName })

        & $SignToolPath sign /sha1 $Thumbprint /tr http://timestamp.sectigo.com /td sha256 /fd sha256 @filePaths
        if ($LASTEXITCODE -ne 0) {
            $preview = ($filePaths | Select-Object -First 10) -join [Environment]::NewLine
            throw "signtool failed while signing a batch of $($filePaths.Count) file(s):$([Environment]::NewLine)$preview"
        }
    }

    Assert-ValidAuthenticodeSignatures -Directory $Directory -Context "Publish output"
}

function Test-PackedRelease {
    param(
        [string]$PackagePath,
        [string]$SetupPath,
        [switch]$RequireSignatures
    )

    if (-not (Test-Path -LiteralPath $PackagePath)) {
        throw "Expected Velopack package was not created: $PackagePath"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $validationDir = Join-Path ([System.IO.Path]::GetTempPath()) "SelbstlaeuferCloudDrivePackValidation-$PID-$([guid]::NewGuid().ToString("N"))"

    try {
        New-Item -ItemType Directory -Path $validationDir -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $validationDir)

        $appDir = Join-Path $validationDir "lib\app"
        if (-not (Test-Path -LiteralPath $appDir)) {
            throw "Velopack package is missing lib/app: $PackagePath"
        }

        Test-IntegrityManifest -Directory $appDir -Context "Packed app payload"

        if ($RequireSignatures) {
            Assert-ValidAuthenticodeSignatures -Directory $appDir -Context "Packed app payload"

            if (-not (Test-Path -LiteralPath $SetupPath)) {
                throw "Expected setup executable was not created: $SetupPath"
            }

            $setupSignature = Get-AuthenticodeSignature -LiteralPath $SetupPath
            if ($setupSignature.Status -ne "Valid") {
                throw "Setup executable signature validation failed: $SetupPath ($($setupSignature.Status))"
            }

            Write-Host "Setup signature validation passed."
        }
    }
    finally {
        if (Test-Path -LiteralPath $validationDir) {
            Remove-Item -LiteralPath $validationDir -Recurse -Force
        }
    }
}

if (-not (Test-SemVer -Value $Version)) {
    throw "Version '$Version' is not a valid semantic version."
}

if ($Channel -notmatch "^[A-Za-z0-9][A-Za-z0-9.-]*$") {
    throw "Channel '$Channel' contains unsupported characters."
}

$projectPath = Join-Path $script:RepoRoot "src\CloudDrive.App\CloudDrive.App.csproj"
$iconPath = Join-Path $script:RepoRoot "assets\app_icons\selbstlaeufer-cloud-app-icon.ico"
$publishDir = Join-Path $script:RepoRoot "build\publish"
$resolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
}
else {
    Join-Path $script:RepoRoot $OutputDir
}

$packId = "SelbstlaeuferGmbH.CloudDrive"

# Keep these literals ASCII-only so Windows PowerShell 5.1 does not misread
# UTF-8 source bytes and turn the a-umlaut into mojibake during local packaging.
$brandAumlaut = [string][char]0x00E4
$packTitle = "Selbstl${brandAumlaut}ufer CloudDrive"
$packAuthors = "Selbstl${brandAumlaut}ufer GmbH"
$mainExe = "CloudDrive.App.exe"
$runtime = "win11.0.22621-x64"
$framework = "net9.0-x64-desktop"
$velopackVersion = "0.0.1298"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Application icon not found: $iconPath"
}

$vpkCommand = Get-Command vpk -ErrorAction SilentlyContinue
if ($null -eq $vpkCommand) {
    throw "The 'vpk' CLI was not found on PATH. Install it with 'dotnet tool install -g vpk --version $velopackVersion'."
}

$normalizedThumbprint = $null
$signToolPath = $null
if ($PSBoundParameters.ContainsKey("SignThumbprint") -and -not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
    $normalizedThumbprint = ($SignThumbprint -replace "\s", "").ToUpperInvariant()
    if ($normalizedThumbprint -notmatch "^[A-F0-9]{40}$") {
        throw "SignThumbprint must be a SHA1 certificate thumbprint."
    }

    $signToolPath = Find-SignTool

    $matchingCertificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq $normalizedThumbprint } |
        Select-Object -First 1

    if ($null -eq $matchingCertificate) {
        throw "Signing certificate '$normalizedThumbprint' was not found in CurrentUser\My or LocalMachine\My."
    }

    $signToolDirectory = Split-Path -Parent $signToolPath
    if (-not (($env:PATH -split ";") -contains $signToolDirectory)) {
        $env:PATH = "$signToolDirectory;$env:PATH"
    }
}

Remove-WorkspaceDirectory -Path $publishDir
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

Write-Host "Publishing $packTitle $Version to $publishDir..."

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--no-self-contained",
    "-o", $publishDir,
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$mainExePath = Join-Path $publishDir $mainExe
if (-not (Test-Path -LiteralPath $mainExePath)) {
    throw "Published executable not found: $mainExePath"
}

$integrityManifestPath = Join-Path $publishDir "integrity-manifest.json"
if (-not (Test-Path -LiteralPath $integrityManifestPath)) {
    throw "Publish output is missing integrity-manifest.json: $integrityManifestPath"
}

if ($normalizedThumbprint) {
    Invoke-CodeSigning -Directory $publishDir -SignToolPath $signToolPath -Thumbprint $normalizedThumbprint
}

$integrityManifestPath = Write-IntegrityManifest -Directory $publishDir
Test-IntegrityManifest -Directory $publishDir -Context "Publish output"

$packArgs = @(
    "pack",
    "--packId", $packId,
    "--packTitle", $packTitle,
    "--packAuthors", $packAuthors,
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", $mainExe,
    "--channel", $Channel,
    "--outputDir", $resolvedOutputDir,
    "--runtime", $runtime,
    "--framework", $framework,
    "--icon", $iconPath,
    "--noPortable"
)

if ($normalizedThumbprint) {
    $signParams = "/sha1 $normalizedThumbprint /tr http://timestamp.sectigo.com /td sha256 /fd sha256"
    $packArgs += @("--signParams", $signParams, "--signParallel", "100")
}

Write-Host "Packing Velopack release into $resolvedOutputDir..."
& $vpkCommand.Source @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed."
}

$releasePackagePath = Join-Path $resolvedOutputDir "$packId-$Version-$Channel-full.nupkg"
$setupPath = Join-Path $resolvedOutputDir "$packId-$Channel-Setup.exe"
Test-PackedRelease -PackagePath $releasePackagePath -SetupPath $setupPath -RequireSignatures:([bool]$normalizedThumbprint)

Write-Host ""
Write-Host "Release assets:"
Get-ChildItem -LiteralPath $resolvedOutputDir | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)"
}
