[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter()]
    [string]$SignThumbprint,

    [Parameter()]
    [string]$Channel = "stable",

    [Parameter()]
    [string]$RepositoryUrl = "https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app",

    [Parameter()]
    [string]$OutputDir,

    [Parameter()]
    [string]$GitHubToken,

    [Parameter()]
    [string]$ReleaseName,

    [Parameter()]
    [string]$TagName,

    [Parameter()]
    [switch]$NoUpload,

    [Parameter()]
    [switch]$SkipBuild,

    [Parameter()]
    [switch]$SkipPreviousDownload,

    [Parameter()]
    [switch]$FailOnPreviousDownloadError,

    [Parameter()]
    [switch]$KeepOutput,

    [Parameter()]
    [switch]$AllowDirty,

    [Parameter()]
    [switch]$SkipGitChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:PackId = "SelbstlaeuferGmbH.CloudDrive"

function Test-SemVer {
    param([string]$Value)

    return $Value -match "^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
}

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Path))
}

function Test-IsInsideDirectory {
    param(
        [string]$Child,
        [string]$Parent
    )

    $pathSeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd($pathSeparators)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd($pathSeparators)

    return $childFull.StartsWith(
        $parentFull + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Clear-ReleaseOutputDirectory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $buildRoot = [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot "build"))

    if (-not (Test-IsInsideDirectory -Child $resolvedPath -Parent $buildRoot)) {
        throw "Refusing to clear release output outside the repository build directory: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Resolve-GitHubToken {
    param([string]$ExplicitToken)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitToken)) {
        return $ExplicitToken
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        return $env:GH_TOKEN
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        return $env:GITHUB_TOKEN
    }

    return Get-GitHubCliToken
}

function Get-GitHubCliToken {
    $ghCommand = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $ghCommand) {
        return $null
    }

    $tokenOutput = @(& $ghCommand.Source auth token --hostname github.com 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tokenOutput.Count -eq 0) {
        return $null
    }

    $token = ($tokenOutput | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        return $null
    }

    return $token
}

function Assert-GitReleaseState {
    param(
        [string]$VersionTag,
        [switch]$AllowDirtyTree,
        [switch]$SkipChecks
    )

    if ($SkipChecks) {
        return
    }

    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        Write-Warning "git was not found on PATH; skipping release tag checks."
        return
    }

    Push-Location $script:RepoRoot
    try {
        & $gitCommand.Source rev-parse --is-inside-work-tree *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Repository git checks could not run; continuing without them."
            return
        }

        $status = @(& $gitCommand.Source status --porcelain)
        if ($status.Count -gt 0 -and -not $AllowDirtyTree) {
            throw "Working tree has uncommitted changes. Commit or stash them before a release, or pass -AllowDirty for a non-production build."
        }

        & $gitCommand.Source rev-parse -q --verify "refs/tags/$VersionTag" *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Release tag '$VersionTag' does not exist locally. Create it with 'git tag $VersionTag' after committing the release changes."
        }

        $headCommit = (& $gitCommand.Source rev-parse HEAD).Trim()
        $tagCommit = (& $gitCommand.Source rev-list -n 1 $VersionTag).Trim()
        if (-not [string]::Equals($headCommit, $tagCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Release tag '$VersionTag' does not point at HEAD. Check out the tagged commit or move the tag before publishing."
        }

        & $gitCommand.Source ls-remote --exit-code --tags origin "refs/tags/$VersionTag" *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Release tag '$VersionTag' was not found on origin. Push it with 'git push origin $VersionTag' before or immediately after upload."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-PreviousReleaseDownload {
    param(
        [string]$RepoUrl,
        [string]$ReleaseChannel,
        [string]$ReleaseOutputDir,
        [string]$Token,
        [switch]$FailOnError
    )

    $vpkCommand = Get-Command vpk -ErrorAction SilentlyContinue
    if ($null -eq $vpkCommand) {
        throw "The 'vpk' CLI was not found on PATH. Install it with 'dotnet tool install -g vpk --version 0.0.1298'."
    }

    $downloadArgs = @(
        "download",
        "github",
        "--repoUrl", $RepoUrl,
        "--channel", $ReleaseChannel,
        "--outputDir", $ReleaseOutputDir
    )

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $downloadArgs += @("--token", $Token)
    }

    Write-Host "Downloading previous '$ReleaseChannel' release assets, if any..."
    & $vpkCommand.Source @downloadArgs
    if ($LASTEXITCODE -ne 0) {
        $message = "Previous release download failed. This is normal for the first release on a channel; later releases may lose delta package optimization."
        if ($FailOnError) {
            throw $message
        }

        Write-Warning $message
    }
}

function Invoke-ReleaseBuild {
    param(
        [string]$ReleaseVersion,
        [string]$ReleaseChannel,
        [string]$ReleaseOutputDir,
        [string]$Thumbprint
    )

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        throw "A signing certificate thumbprint is required for a release build. Pass -SignThumbprint with the hardware-token certificate SHA1 thumbprint."
    }

    $packScript = Join-Path $script:RepoRoot "build\pack.ps1"
    if (-not (Test-Path -LiteralPath $packScript)) {
        throw "Packaging script was not found: $packScript"
    }

    $packArgs = @(
        "-Version", $ReleaseVersion,
        "-Channel", $ReleaseChannel,
        "-OutputDir", $ReleaseOutputDir,
        "-SignThumbprint", $Thumbprint
    )

    Write-Host "Building signed Velopack release $ReleaseVersion..."
    & $packScript @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Release packaging failed."
    }
}

function Assert-ReleaseAssets {
    param(
        [string]$ReleaseVersion,
        [string]$ReleaseChannel,
        [string]$ReleaseOutputDir
    )

    $requiredFiles = @(
        "assets.$ReleaseChannel.json",
        "releases.$ReleaseChannel.json",
        "RELEASES-$ReleaseChannel",
        "$($script:PackId)-$ReleaseChannel-Setup.exe",
        "$($script:PackId)-$ReleaseVersion-$ReleaseChannel-full.nupkg"
    )

    foreach ($fileName in $requiredFiles) {
        $path = Join-Path $ReleaseOutputDir $fileName
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Release output is missing required asset: $path"
        }
    }

    Write-Host ""
    Write-Host "Release assets ready in ${ReleaseOutputDir}:"
    Get-ChildItem -LiteralPath $ReleaseOutputDir -File | Sort-Object Name | ForEach-Object {
        Write-Host "  $($_.Name)"
    }
}

function Invoke-GitHubReleaseUpload {
    param(
        [string]$RepoUrl,
        [string]$ReleaseChannel,
        [string]$ReleaseOutputDir,
        [string]$Token,
        [string]$Name,
        [string]$VersionTag
    )

    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "GitHub upload requires a token. Authenticate with GitHub CLI using 'gh auth login', set GH_TOKEN or GITHUB_TOKEN, or pass -GitHubToken."
    }

    $vpkCommand = Get-Command vpk -ErrorAction SilentlyContinue
    if ($null -eq $vpkCommand) {
        throw "The 'vpk' CLI was not found on PATH. Install it with 'dotnet tool install -g vpk --version 0.0.1298'."
    }

    $uploadArgs = @(
        "upload",
        "github",
        "--repoUrl", $RepoUrl,
        "--channel", $ReleaseChannel,
        "--outputDir", $ReleaseOutputDir,
        "--token", $Token,
        "--publish",
        "--merge",
        "--releaseName", $Name,
        "--tag", $VersionTag
    )

    Write-Host "Uploading release assets to $RepoUrl..."
    & $vpkCommand.Source @uploadArgs
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release upload failed."
    }
}

if (-not (Test-SemVer -Value $Version)) {
    throw "Version '$Version' is not a valid semantic version."
}

if ($Channel -notmatch "^[A-Za-z0-9][A-Za-z0-9.-]*$") {
    throw "Channel '$Channel' contains unsupported characters."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Join-Path "build" "releases") $Channel
}

if ([string]::IsNullOrWhiteSpace($ReleaseName)) {
    $ReleaseName = "Selbstlaeufer CloudDrive $Version"
}

if ([string]::IsNullOrWhiteSpace($TagName)) {
    $TagName = "v$Version"
}

$resolvedOutputDir = Resolve-WorkspacePath -Path $OutputDir
$resolvedToken = Resolve-GitHubToken -ExplicitToken $GitHubToken

Assert-GitReleaseState -VersionTag $TagName -AllowDirtyTree:$AllowDirty -SkipChecks:$SkipGitChecks

if (-not $SkipBuild) {
    if (-not $KeepOutput) {
        Clear-ReleaseOutputDirectory -Path $resolvedOutputDir
    }

    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

    if (-not $SkipPreviousDownload) {
        Invoke-PreviousReleaseDownload `
            -RepoUrl $RepositoryUrl `
            -ReleaseChannel $Channel `
            -ReleaseOutputDir $resolvedOutputDir `
            -Token $resolvedToken `
            -FailOnError:$FailOnPreviousDownloadError
    }

    Invoke-ReleaseBuild `
        -ReleaseVersion $Version `
        -ReleaseChannel $Channel `
        -ReleaseOutputDir $resolvedOutputDir `
        -Thumbprint $SignThumbprint
}

Assert-ReleaseAssets -ReleaseVersion $Version -ReleaseChannel $Channel -ReleaseOutputDir $resolvedOutputDir

if ($NoUpload) {
    Write-Host ""
    Write-Host "Upload skipped. Test the setup executable, then upload these staged assets with:"
    Write-Host "  .\build\publish-release.ps1 -Version $Version -SkipBuild"
    return
}

Invoke-GitHubReleaseUpload `
    -RepoUrl $RepositoryUrl `
    -ReleaseChannel $Channel `
    -ReleaseOutputDir $resolvedOutputDir `
    -Token $resolvedToken `
    -Name $ReleaseName `
    -VersionTag $TagName

Write-Host ""
Write-Host "Release $TagName was uploaded successfully."
