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
    [switch]$SkipTests,

    [Parameter()]
    [switch]$SkipFetchTags
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

function Test-SemVer {
    param([string]$Value)

    return $Value -match "^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
}

function ConvertTo-SemVerParts {
    param([string]$Value)

    $match = [regex]::Match($Value, "^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)")
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

function Select-UpdateType {
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
            return "minor"
        }

        switch ($choice.Trim()) {
            "1" { return "minor" }
            "2" { return "major" }
            default { Write-Host "Please select 1 for minor or 2 for major." }
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

function Get-GitOutput {
    param([string[]]$Arguments)

    $git = Require-Command "git"
    $output = @(& $git @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed."
    }

    return $output
}

function Invoke-CommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [Parameter()]
        [string[]]$RedactValues = @()
    )

    Write-Host ""
    Write-Host $Description

    $displayArgs = foreach ($arg in $Arguments) {
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

    & $FilePath @Arguments

    if (-not $isPowerShellScript -and $LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Resolve-WorkspacePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Path))
}

function Get-LatestReleaseVersionFromGitTags {
    $versions = @()
    $tags = Get-GitOutput -Arguments @("tag", "--list", "v*")

    foreach ($tag in $tags) {
        $text = $tag.Trim()
        if ($text -match "^v(?<version>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(?:$|[-+])") {
            $parts = ConvertTo-SemVerParts -Value $matches["version"]
            $versions += $parts
        }
    }

    if ($versions.Count -eq 0) {
        return $null
    }

    return $versions |
        Sort-Object `
            @{ Expression = { $_.Major }; Descending = $true },
            @{ Expression = { $_.Minor }; Descending = $true },
            @{ Expression = { $_.Patch }; Descending = $true } |
        Select-Object -First 1
}

function Test-GitTagExists {
    param([string]$TagName)

    $git = Require-Command "git"
    & $git rev-parse -q --verify "refs/tags/$TagName" *> $null
    return $LASTEXITCODE -eq 0
}

function Test-GitRemoteTagExists {
    param([string]$TagName)

    $git = Require-Command "git"
    & $git ls-remote --exit-code --tags origin "refs/tags/$TagName" *> $null
    return $LASTEXITCODE -eq 0
}

function Assert-TagPointsAtHead {
    param([string]$TagName)

    $headCommit = (Get-GitOutput -Arguments @("rev-parse", "HEAD") | Select-Object -First 1).Trim()
    $tagCommit = (Get-GitOutput -Arguments @("rev-list", "-n", "1", $TagName) | Select-Object -First 1).Trim()

    if (-not [string]::Equals($headCommit, $tagCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Tag '$TagName' exists but does not point at HEAD."
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
    param([string]$ExplicitThumbprint)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitThumbprint)) {
        return ($ExplicitThumbprint -replace "\s", "")
    }

    Show-CodeSigningCertificates
    $thumbprint = Read-RequiredValue -Prompt "Enter the code-signing certificate SHA1 thumbprint"
    return ($thumbprint -replace "\s", "")
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

    return $null
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

function Ensure-GitHubToken {
    $token = Resolve-GitHubToken -ExplicitToken $GitHubToken
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
            $env:GH_TOKEN = $GitHubToken
        }

        return $token
    }

    if (-not (Read-YesNo -Question "No GitHub token is set. Paste one for this upload now?" -DefaultYes $true)) {
        throw "GitHub upload requires GH_TOKEN, GITHUB_TOKEN, or -GitHubToken."
    }

    $token = Read-SecretValue -Prompt "GitHub token"
    $env:GH_TOKEN = $token
    return $token
}

function Get-ReleaseVersion {
    param($BaseVersion)

    $updateType = Select-UpdateType -BaseVersion $BaseVersion
    $proposedVersion = New-BumpedVersion -BaseVersion $BaseVersion -UpdateType $updateType

    if (Read-YesNo -Question "Use release version $proposedVersion?" -DefaultYes $true) {
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

function Get-ReleaseScriptArgs {
    param(
        [string]$Version,
        [string]$Thumbprint,
        [string]$TagName,
        [bool]$AllowDirty,
        [bool]$SkipGitChecks,
        [switch]$NoUpload,
        [switch]$SkipBuild,
        [string]$Token
    )

    $scriptArgs = @(
        "-Version", $Version,
        "-Channel", $Channel,
        "-RepositoryUrl", $RepositoryUrl,
        "-OutputDir", $OutputDir,
        "-TagName", $TagName
    )

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $scriptArgs += @("-SignThumbprint", $Thumbprint)
    }

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $scriptArgs += @("-GitHubToken", $Token)
    }

    if ($NoUpload) {
        $scriptArgs += "-NoUpload"
    }

    if ($SkipBuild) {
        $scriptArgs += "-SkipBuild"
    }

    if ($AllowDirty) {
        $scriptArgs += "-AllowDirty"
    }

    if ($SkipGitChecks) {
        $scriptArgs += "-SkipGitChecks"
    }

    return $scriptArgs
}

Push-Location $script:RepoRoot
try {
    Write-Host "CloudDrive local release wizard"
    Write-Host "Repository: $RepositoryUrl"
    Write-Host "Channel:    $Channel"

    Require-Command "git" | Out-Null
    Require-Command "dotnet" | Out-Null
    Require-Command "vpk" | Out-Null

    Get-GitOutput -Arguments @("rev-parse", "--is-inside-work-tree") | Out-Null

    if (-not $SkipFetchTags) {
        if (Read-YesNo -Question "Fetch tags from origin before choosing the next version?" -DefaultYes $true) {
            Invoke-CommandLine `
                -FilePath (Require-Command "git") `
                -Arguments @("fetch", "--tags", "origin") `
                -Description "Fetching release tags"
        }
    }

    $baseVersion = Get-LatestReleaseVersionFromGitTags
    if ($null -eq $baseVersion) {
        Write-Host ""
        Write-Warning "No local release tags like v1.2.3 were found."
        if (Read-YesNo -Question "Use 0.0.0 as the base version?" -DefaultYes $true) {
            $baseVersion = ConvertTo-SemVerParts -Value "0.0.0"
        }
        else {
            $baseText = Read-RequiredValue -Prompt "Enter the current released version"
            $baseVersion = ConvertTo-SemVerParts -Value $baseText
        }
    }

    Write-Host ""
    Write-Host "Latest known release version: $($baseVersion.Text)"

    $version = Get-ReleaseVersion -BaseVersion $baseVersion
    $tagName = "v$version"
    $releaseName = "Selbstlaeufer CloudDrive $version"
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
    $status = @(Get-GitOutput -Arguments @("status", "--short"))
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
                Invoke-CommandLine `
                    -FilePath (Require-Command "dotnet") `
                    -Arguments @("test", "CloudDrive.sln", "-c", "Release") `
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

    if (Test-GitTagExists -TagName $tagName) {
        Assert-TagPointsAtHead -TagName $tagName
        Write-Host "Local tag $tagName already exists and points at HEAD."
    }
    else {
        if (-not (Read-YesNo -Question "Create local release tag $tagName at HEAD?" -DefaultYes $true)) {
            throw "Release tag is required."
        }

        Invoke-CommandLine `
            -FilePath (Require-Command "git") `
            -Arguments @("tag", $tagName) `
            -Description "Creating local release tag"
    }

    if (Test-GitRemoteTagExists -TagName $tagName) {
        Write-Host "Remote tag $tagName already exists on origin."
    }
    else {
        if (Read-YesNo -Question "Push tag $tagName to origin now?" -DefaultYes $true) {
            Invoke-CommandLine `
                -FilePath (Require-Command "git") `
                -Arguments @("push", "origin", $tagName) `
                -Description "Pushing release tag"
        }
        else {
            Write-Warning "The upload can continue, but the GitHub release tag should be pushed before calling the release complete."
        }
    }

    $thumbprint = Resolve-SigningThumbprint -ExplicitThumbprint $SignThumbprint

    $downloadPrevious = Read-YesNo -Question "Download previous release assets before building?" -DefaultYes $true
    $clearOutput = Read-YesNo -Question "Clear the staging output before building?" -DefaultYes $true
    $failOnPreviousDownloadError = $false
    $skipGitChecks = $false

    if (-not $downloadPrevious) {
        Write-Warning "Delta package optimization may be worse without previous release assets."
    }
    else {
        $failOnPreviousDownloadError = Read-YesNo -Question "Fail if previous release assets cannot be downloaded?" -DefaultYes $false
    }

    if (Read-YesNo -Question "Review advanced release script options?" -DefaultYes $false) {
        $skipGitChecks = Read-YesNo -Question "Pass -SkipGitChecks?" -DefaultYes $false
    }

    if (-not (Read-YesNo -Question "Build and sign the staged release now?" -DefaultYes $true)) {
        throw "Release cancelled before build."
    }

    $stageArgs = Get-ReleaseScriptArgs `
        -Version $version `
        -Thumbprint $thumbprint `
        -TagName $tagName `
        -AllowDirty $allowDirty `
        -SkipGitChecks $skipGitChecks `
        -NoUpload

    if (-not $downloadPrevious) {
        $stageArgs += "-SkipPreviousDownload"
    }

    if ($failOnPreviousDownloadError) {
        $stageArgs += "-FailOnPreviousDownloadError"
    }

    if (-not $clearOutput) {
        $stageArgs += "-KeepOutput"
    }

    $publishScript = Join-Path $script:RepoRoot "build\publish-release.ps1"
    Invoke-CommandLine `
        -FilePath $publishScript `
        -Arguments $stageArgs `
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

    Ensure-GitHubToken | Out-Null
    $uploadArgs = Get-ReleaseScriptArgs `
        -Version $version `
        -Thumbprint "" `
        -TagName $tagName `
        -AllowDirty $allowDirty `
        -SkipGitChecks $skipGitChecks `
        -SkipBuild

    $uploadArgs += @("-ReleaseName", $releaseName)

    Invoke-CommandLine `
        -FilePath $publishScript `
        -Arguments $uploadArgs `
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
