[CmdletBinding()]
param(
    [Parameter()]
    [string]$Channel = "stable",

    [Parameter()]
    [string]$RepositoryUrl = "https://github.com/SelbstlauferGmbH/selbstlaeufer-clouddrive-windows-app",

    [Parameter()]
    [string]$OutputDir,

    [Parameter()]
    [string]$SignThumbprint,

    [Parameter()]
    [string]$GitHubToken,

    [Parameter()]
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:PackId = "SelbstlaeuferGmbH.CloudDrive"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Join-Path "build" "releases") $Channel
}

function Read-YesNo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Question,

        [Parameter()]
        [bool]$DefaultYes = $true
    )

    $suffix = if ($DefaultYes) { "[Y/n]" } else { "[y/N]" }

    while ($true) {
        $answer = Read-Host "$Question $suffix"
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $DefaultYes
        }

        switch -Regex ($answer.Trim()) {
            "^(y|yes)$" { return $true }
            "^(n|no)$" { return $false }
            default { Write-Host "Please answer yes or no." }
        }
    }
}

function Read-RequiredValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prompt,

        [Parameter()]
        [string]$DefaultValue
    )

    while ($true) {
        $promptText = if ([string]::IsNullOrWhiteSpace($DefaultValue)) {
            $Prompt
        }
        else {
            "$Prompt [$DefaultValue]"
        }

        $value = Read-Host $promptText
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = $DefaultValue
        }

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }

        Write-Host "A value is required."
    }
}

function Read-SecretValue {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    while ($true) {
        $secure = Read-Host -Prompt $Prompt -AsSecureString
        if ($secure.Length -gt 0) {
            $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            try {
                return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
            }
            finally {
                [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            }
        }

        Write-Host "A value is required."
    }
}

function Test-SemVer {
    param([string]$Value)

    return $Value -match "^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
}

function ConvertTo-SemVerParts {
    param([string]$Value)

    $match = [regex]::Match($Value, "^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)")
    if (-not $match.Success) {
        throw "Version '$Value' is not a valid semantic version."
    }

    return [pscustomobject]@{
        Major = [int]$match.Groups["major"].Value
        Minor = [int]$match.Groups["minor"].Value
        Patch = [int]$match.Groups["patch"].Value
        Text = "$($match.Groups["major"].Value).$($match.Groups["minor"].Value).$($match.Groups["patch"].Value)"
    }
}

function Compare-SemVer {
    param(
        [Parameter(Mandatory = $true)]
        $Left,

        [Parameter(Mandatory = $true)]
        $Right
    )

    foreach ($property in @("Major", "Minor", "Patch")) {
        if ($Left.$property -gt $Right.$property) {
            return 1
        }

        if ($Left.$property -lt $Right.$property) {
            return -1
        }
    }

    return 0
}

function Get-HighestSemVer {
    param([object[]]$Versions)

    if ($null -eq $Versions -or $Versions.Count -eq 0) {
        return $null
    }

    return $Versions |
        Sort-Object `
            @{ Expression = { $_.Major }; Descending = $true },
            @{ Expression = { $_.Minor }; Descending = $true },
            @{ Expression = { $_.Patch }; Descending = $true } |
        Select-Object -First 1
}

function New-BumpedVersion {
    param(
        [Parameter(Mandatory = $true)]
        $BaseVersion,

        [Parameter(Mandatory = $true)]
        [ValidateSet("major", "minor")]
        [string]$UpdateType
    )

    if ($UpdateType -eq "major") {
        return "$($BaseVersion.Major + 1).0.0"
    }

    return "$($BaseVersion.Major).$($BaseVersion.Minor + 1).0"
}

function Select-ReleaseVersion {
    param($BaseVersion)

    $minorVersion = New-BumpedVersion -BaseVersion $BaseVersion -UpdateType "minor"
    $majorVersion = New-BumpedVersion -BaseVersion $BaseVersion -UpdateType "major"

    Write-Host ""
    Write-Host "Choose the update type:"
    Write-Host "  1. Minor update -> $minorVersion"
    Write-Host "  2. Major update -> $majorVersion"

    while ($true) {
        $choice = Read-Host "Select 1 or 2 [1]"
        if ([string]::IsNullOrWhiteSpace($choice)) {
            $choice = "1"
        }

        $proposedVersion = switch ($choice.Trim()) {
            "1" { $minorVersion }
            "2" { $majorVersion }
            default {
                Write-Host "Please select 1 for minor or 2 for major."
                $null
            }
        }

        if ([string]::IsNullOrWhiteSpace($proposedVersion)) {
            continue
        }

        if (Read-YesNo -Question "Use release version ${proposedVersion}?" -DefaultYes $true) {
            return $proposedVersion
        }

        while ($true) {
            $customVersion = Read-RequiredValue -Prompt "Enter release version"
            if (Test-SemVer -Value $customVersion) {
                return $customVersion
            }

            Write-Host "Please enter a valid semantic version, for example 1.2.0."
        }
    }
}

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command '$Name' was not found on PATH."
    }

    return $command.Source
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$CommandArguments = @(),

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter()]
        [string[]]$RedactValues = @()
    )

    Write-Host ""
    Write-Host $Description

    $displayArgs = foreach ($arg in $CommandArguments) {
        if ($RedactValues -contains $arg) {
            "***"
        }
        else {
            $arg
        }
    }

    Write-Host ">> $FilePath $($displayArgs -join ' ')"

    $isPowerShellScript = [System.IO.Path]::GetExtension($FilePath).Equals(
        ".ps1",
        [System.StringComparison]::OrdinalIgnoreCase)

    & $FilePath @CommandArguments

    if (-not $isPowerShellScript -and $LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-GitOutput {
    param([string[]]$CommandArguments)

    $git = Require-Command "git"
    $output = @(& $git @CommandArguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($CommandArguments -join ' ') failed."
    }

    return $output
}

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Path))
}

function Get-GitHubRepositoryInfo {
    param([string]$RepoUrl)

    $trimmed = $RepoUrl.Trim().TrimEnd("/")
    $match = [regex]::Match($trimmed, "github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$")
    if (-not $match.Success) {
        throw "RepositoryUrl must point to a GitHub repository, for example https://github.com/owner/repo."
    }

    return [pscustomobject]@{
        Owner = $match.Groups["owner"].Value
        Repo = $match.Groups["repo"].Value
    }
}

function Resolve-ExistingGitHubToken {
    if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        return $GitHubToken
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

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter()]
        [switch]$AllowNotFound
    )

    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "CloudDriveReleaseWizard"
    }

    $token = Resolve-ExistingGitHubToken
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $headers.Authorization = "Bearer $token"
    }

    try {
        return Invoke-RestMethod -Uri $Uri -Headers $headers -Method Get
    }
    catch {
        $response = $_.Exception.Response
        if ($AllowNotFound -and $null -ne $response -and [int]$response.StatusCode -eq 404) {
            return $null
        }

        throw "$Description failed. $($_.Exception.Message)"
    }
}

function Get-GitHubReleaseVersions {
    param($Repository)

    $uri = "https://api.github.com/repos/$($Repository.Owner)/$($Repository.Repo)/releases?per_page=100"
    $releases = Invoke-GitHubApi -Uri $uri -Description "GitHub release lookup"
    $versions = @()

    foreach ($release in @($releases)) {
        if ($release.draft -or $release.prerelease) {
            continue
        }

        if ($release.tag_name -match "^v?(?<version>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(?:$|[-+])") {
            $parts = ConvertTo-SemVerParts -Value $matches["version"]
            $versions += [pscustomobject]@{
                Major = $parts.Major
                Minor = $parts.Minor
                Patch = $parts.Patch
                Text = $parts.Text
                TagName = $release.tag_name
                Url = $release.html_url
            }
        }
    }

    return $versions
}

function Get-CheckedGitHubReleaseVersions {
    param($Repository)

    try {
        return @(Get-GitHubReleaseVersions -Repository $Repository)
    }
    catch {
        Write-Warning $_.Exception.Message
        Write-Warning "The GitHub Releases check is required so the wizard can reject versions lower than origin."

        if (-not (Read-YesNo -Question "Paste or use a GitHub token now and retry the release check?" -DefaultYes $true)) {
            throw "Release cancelled because GitHub Releases could not be checked."
        }

        Ensure-GitHubToken
        return @(Get-GitHubReleaseVersions -Repository $Repository)
    }
}

function Test-GitHubReleaseVersionExists {
    param(
        [object[]]$Versions,
        [string]$Version
    )

    return @($Versions | Where-Object { $_.Text -eq $Version }).Count -gt 0
}

function Get-ManualBaseVersion {
    if (Read-YesNo -Question "Use 0.0.0 as the base version?" -DefaultYes $true) {
        return ConvertTo-SemVerParts -Value "0.0.0"
    }

    while ($true) {
        $baseText = Read-RequiredValue -Prompt "Enter the current released version"
        if (Test-SemVer -Value $baseText) {
            return ConvertTo-SemVerParts -Value $baseText
        }

        Write-Host "Please enter a valid semantic version, for example 0.2.0."
    }
}

function Test-GitTagExists {
    param([string]$TagName)

    $git = Require-Command "git"
    & $git rev-parse -q --verify "refs/tags/$TagName" *> $null
    return $LASTEXITCODE -eq 0
}

function Get-RemoteTagCommit {
    param([string]$TagName)

    $git = Require-Command "git"
    $lines = @(& $git ls-remote origin "refs/tags/$TagName" "refs/tags/$TagName^{}")
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) {
        return $null
    }

    $peeled = $lines | Where-Object { $_ -match "refs/tags/$([regex]::Escape($TagName))\^\{\}$" } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($peeled)) {
        return ($peeled -split "\s+")[0]
    }

    return (($lines | Select-Object -First 1) -split "\s+")[0]
}

function Assert-LocalTagPointsAtHead {
    param([string]$TagName)

    $headCommit = (Get-GitOutput -CommandArguments @("rev-parse", "HEAD") | Select-Object -First 1).Trim()
    $tagCommit = (Get-GitOutput -CommandArguments @("rev-list", "-n", "1", $TagName) | Select-Object -First 1).Trim()

    if (-not [string]::Equals($headCommit, $tagCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Tag '$TagName' exists locally but does not point at HEAD."
    }
}

function Ensure-ReleaseTag {
    param([string]$TagName)

    $headCommit = (Get-GitOutput -CommandArguments @("rev-parse", "HEAD") | Select-Object -First 1).Trim()
    $remoteTagCommit = Get-RemoteTagCommit -TagName $TagName

    if (-not [string]::IsNullOrWhiteSpace($remoteTagCommit)) {
        if (-not [string]::Equals($remoteTagCommit, $headCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Origin tag '$TagName' already exists but does not point at HEAD."
        }

        if (-not (Test-GitTagExists -TagName $TagName)) {
            Invoke-External `
                -FilePath (Require-Command "git") `
                -CommandArguments ([string[]]@("fetch", "origin", "refs/tags/${TagName}:refs/tags/${TagName}")) `
                -Description "Fetching existing release tag"
        }

        Assert-LocalTagPointsAtHead -TagName $TagName
        Write-Host "Origin tag $TagName already exists and points at HEAD. Continuing with the same release version."
        return
    }

    if (Test-GitTagExists -TagName $TagName) {
        Assert-LocalTagPointsAtHead -TagName $TagName
        Write-Host "Local tag $TagName already exists and points at HEAD."
    }
    else {
        if (-not (Read-YesNo -Question "Create local release tag $TagName at HEAD?" -DefaultYes $true)) {
            throw "Release tag is required."
        }

        Invoke-External `
            -FilePath (Require-Command "git") `
            -CommandArguments ([string[]]@("tag", $TagName)) `
            -Description "Creating local release tag"
    }

    if (Read-YesNo -Question "Push tag $TagName to origin now?" -DefaultYes $true) {
        Invoke-External `
            -FilePath (Require-Command "git") `
            -CommandArguments ([string[]]@("push", "origin", $TagName)) `
            -Description "Pushing release tag"
    }
    else {
        Write-Warning "The GitHub release upload expects the tag to exist on origin."
    }
}

function Show-CodeSigningCertificates {
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")
    $certificates = @(
        Get-ChildItem $stores -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending
    )

    if ($certificates.Count -eq 0) {
        Write-Warning "No private-key certificates were found in the current user or local machine personal stores."
        return
    }

    Write-Host ""
    Write-Host "Available private-key certificates:"
    $certificates |
        Select-Object Subject, Thumbprint, NotAfter |
        Format-Table -AutoSize |
        Out-Host
}

function Resolve-SigningThumbprint {
    if (-not [string]::IsNullOrWhiteSpace($SignThumbprint)) {
        return ($SignThumbprint -replace "\s", "")
    }

    Show-CodeSigningCertificates
    $thumbprint = Read-RequiredValue -Prompt "Enter the code-signing certificate SHA1 thumbprint"
    return ($thumbprint -replace "\s", "")
}

function Ensure-GitHubToken {
    $token = Resolve-ExistingGitHubToken
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $env:GH_TOKEN = $token
        return
    }

    if (-not (Read-YesNo -Question "No GitHub token is available. Paste one for this upload now?" -DefaultYes $true)) {
        throw "GitHub upload requires GitHub CLI auth, GH_TOKEN, GITHUB_TOKEN, or -GitHubToken."
    }

    $env:GH_TOKEN = Read-SecretValue -Prompt "GitHub token"
}

function New-ReleaseScriptArguments {
    param(
        [string]$Version,
        [switch]$ForUpload,
        [string]$Thumbprint,
        [bool]$AllowDirty,
        [bool]$DownloadPrevious,
        [bool]$ClearOutput,
        [bool]$FailOnPreviousDownloadError
    )

    $scriptArgs = @(
        "-Version", $Version,
        "-Channel", $Channel,
        "-RepositoryUrl", $RepositoryUrl,
        "-OutputDir", $OutputDir
    )

    if ($ForUpload) {
        $scriptArgs += "-SkipBuild"
    }
    else {
        $scriptArgs += @("-SignThumbprint", $Thumbprint, "-NoUpload")

        if (-not $DownloadPrevious) {
            $scriptArgs += "-SkipPreviousDownload"
        }

        if (-not $ClearOutput) {
            $scriptArgs += "-KeepOutput"
        }

        if ($FailOnPreviousDownloadError) {
            $scriptArgs += "-FailOnPreviousDownloadError"
        }
    }

    if ($AllowDirty) {
        $scriptArgs += "-AllowDirty"
    }

    return [string[]]$scriptArgs
}

Push-Location $script:RepoRoot
try {
    Write-Host "CloudDrive local release wizard"
    Write-Host "Repository: $RepositoryUrl"
    Write-Host "Channel:    $Channel"

    Require-Command "git" | Out-Null
    Require-Command "dotnet" | Out-Null
    Require-Command "vpk" | Out-Null
    Get-GitOutput -CommandArguments @("rev-parse", "--is-inside-work-tree") | Out-Null

    if (Read-YesNo -Question "Fetch tags from origin before choosing the next version?" -DefaultYes $true) {
        Invoke-External `
            -FilePath (Require-Command "git") `
            -CommandArguments ([string[]]@("fetch", "--tags", "origin")) `
            -Description "Fetching release tags"
    }

    $repository = Get-GitHubRepositoryInfo -RepoUrl $RepositoryUrl
    Write-Host ""
    Write-Host "Checking published GitHub Releases..."
    $originReleaseVersions = @(Get-CheckedGitHubReleaseVersions -Repository $repository)
    $latestOriginRelease = Get-HighestSemVer -Versions $originReleaseVersions

    if ($null -eq $latestOriginRelease) {
        Write-Warning "No published non-prerelease GitHub release with a SemVer tag was found."
        $baseVersion = Get-ManualBaseVersion
    }
    else {
        $baseVersion = $latestOriginRelease
        Write-Host "Latest published GitHub release: $($latestOriginRelease.Text) ($($latestOriginRelease.TagName))"
    }

    Write-Host ""
    Write-Host "Base version for this release: $($baseVersion.Text)"
    $version = Select-ReleaseVersion -BaseVersion $baseVersion
    $versionParts = ConvertTo-SemVerParts -Value $version

    if ($null -ne $latestOriginRelease -and (Compare-SemVer -Left $versionParts -Right $latestOriginRelease) -le 0) {
        throw "Selected version $version is not higher than the latest published GitHub release $($latestOriginRelease.Text)."
    }

    if (Test-GitHubReleaseVersionExists -Versions $originReleaseVersions -Version $version) {
        throw "GitHub Release $version already exists. Choose a higher version."
    }

    $tagName = "v$version"
    $resolvedOutputDir = Resolve-WorkspacePath -Path $OutputDir
    $setupPath = Join-Path $resolvedOutputDir "$script:PackId-$Channel-Setup.exe"

    Write-Host ""
    Write-Host "Release plan:"
    Write-Host "  Version:    $version"
    Write-Host "  Tag:        $tagName"
    Write-Host "  Channel:    $Channel"
    Write-Host "  Output:     $resolvedOutputDir"
    Write-Host "  Repository: $RepositoryUrl"

    if (-not (Read-YesNo -Question "Continue with this release plan?" -DefaultYes $true)) {
        throw "Release cancelled."
    }

    $allowDirty = $false
    $status = @(Get-GitOutput -CommandArguments @("status", "--short"))
    if ($status.Count -gt 0) {
        Write-Host ""
        Write-Warning "The working tree has uncommitted changes:"
        $status | ForEach-Object { Write-Host "  $_" }
        $allowDirty = Read-YesNo -Question "Continue and pass -AllowDirty to the release script?" -DefaultYes $false
        if (-not $allowDirty) {
            throw "Commit or stash the changes before releasing."
        }
    }

    if (-not $SkipTests) {
        if (Read-YesNo -Question "Run the Release test suite now?" -DefaultYes $true) {
            try {
                Invoke-External `
                    -FilePath (Require-Command "dotnet") `
                    -CommandArguments ([string[]]@("test", "CloudDrive.sln", "-c", "Release")) `
                    -Description "Running Release tests"
            }
            catch {
                Write-Warning $_.Exception.Message
                if (-not (Read-YesNo -Question "Tests failed. Continue anyway?" -DefaultYes $false)) {
                    throw
                }
            }
        }
        elseif (-not (Read-YesNo -Question "Continue without a fresh test run?" -DefaultYes $false)) {
            throw "Release cancelled before build."
        }
    }

    Ensure-ReleaseTag -TagName $tagName

    $thumbprint = Resolve-SigningThumbprint
    $downloadPrevious = Read-YesNo -Question "Download previous release assets before building?" -DefaultYes $true
    $clearOutput = Read-YesNo -Question "Clear the staging output before building?" -DefaultYes $true
    $failOnPreviousDownloadError = $false

    if (-not $downloadPrevious) {
        Write-Warning "Delta package optimization may be worse without previous release assets."
    }
    else {
        $failOnPreviousDownloadError = Read-YesNo -Question "Fail if previous release assets cannot be downloaded?" -DefaultYes $false
    }

    if (-not (Read-YesNo -Question "Build and sign the staged release now?" -DefaultYes $true)) {
        throw "Release cancelled before build."
    }

    $publishScript = Join-Path $script:RepoRoot "build\publish-release.ps1"
    $stageArgs = New-ReleaseScriptArguments `
        -Version $version `
        -Thumbprint $thumbprint `
        -AllowDirty $allowDirty `
        -DownloadPrevious $downloadPrevious `
        -ClearOutput $clearOutput `
        -FailOnPreviousDownloadError $failOnPreviousDownloadError

    Invoke-External `
        -FilePath $publishScript `
        -CommandArguments ([string[]]$stageArgs) `
        -Description "Building signed release assets"

    Write-Host ""
    Write-Host "Staged release assets:"
    Get-ChildItem -LiteralPath $resolvedOutputDir -File | Sort-Object Name | ForEach-Object {
        Write-Host "  $($_.Name)"
    }

    if (Read-YesNo -Question "Open the staging folder?" -DefaultYes $true) {
        Invoke-Item -LiteralPath $resolvedOutputDir
    }

    if (Test-Path -LiteralPath $setupPath) {
        if (Read-YesNo -Question "Run the staged setup executable for local smoke testing now?" -DefaultYes $true) {
            Start-Process -FilePath $setupPath -Wait
        }
    }
    else {
        Write-Warning "Expected setup executable was not found: $setupPath"
    }

    if (-not (Read-YesNo -Question "Did the staged installer or update smoke test pass?" -DefaultYes $false)) {
        Write-Host "Release assets are staged at $resolvedOutputDir."
        throw "Upload cancelled because smoke test was not confirmed."
    }

    if (-not (Read-YesNo -Question "Upload the tested staged assets to GitHub Releases now?" -DefaultYes $true)) {
        Write-Host "Release assets are staged at $resolvedOutputDir."
        Write-Host "Upload later with: .\build\publish-release.ps1 -Version $version -SkipBuild"
        return
    }

    Ensure-GitHubToken
    $uploadArgs = New-ReleaseScriptArguments `
        -Version $version `
        -ForUpload `
        -AllowDirty $allowDirty `
        -DownloadPrevious $true `
        -ClearOutput $true `
        -FailOnPreviousDownloadError $false

    Invoke-External `
        -FilePath $publishScript `
        -CommandArguments ([string[]]$uploadArgs) `
        -Description "Uploading tested assets to GitHub Releases"

    Write-Host ""
    Write-Host "Release $tagName uploaded successfully."
    Write-Host "Verify it here: $RepositoryUrl/releases"

    if (Read-YesNo -Question "Open GitHub Releases now?" -DefaultYes $true) {
        Start-Process "$RepositoryUrl/releases"
    }
}
finally {
    Pop-Location
}
