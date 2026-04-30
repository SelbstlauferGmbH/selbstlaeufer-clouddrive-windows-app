<#
.SYNOPSIS
    Starts a complete local CloudDrive debug session.

.DESCRIPTION
    Default flow:
      1. Clears TestResults/ unless -SkipClean is used
      2. Builds and starts the local WebDAV Docker container
      3. Waits for the WebDAV health check
      4. Publishes CloudDrive.App to build/publish unless -NoAppPublish is used
      5. Starts CloudDrive.App with CLOUDDRIVE_DEBUG_JSONLOG=1
      6. Blocks until the app exits
      7. Automatically collects/merges logs and shuts Docker down

    Use -ServerOnly when you only want the WebDAV server and will manage the
    app/test lifecycle yourself.

.PARAMETER SkipClean
    Do not clear TestResults/ before starting.

.PARAMETER NoBuild
    Skip rebuilding the Docker image.

.PARAMETER NoAppPublish
    Skip dotnet publish and start the existing build/publish/CloudDrive.App.exe.

.PARAMETER ServerOnly
    Start only the WebDAV server, write .session.json, and print connection info.
    This is the old manual/E2E helper mode.

.PARAMETER OutputFolder
    Where the final merged session logs should be written. Defaults to
    TestResults/session-<start-timestamp>.

.PARAMETER StartApp
    Compatibility switch. The app starts by default; this switch is accepted so
    older commands keep working.

.PARAMETER Seed
    After the WebDAV server is healthy, populate it with a curated tree of test
    files (varied sizes, kinds, and naming edge cases) under /seed/. Off by
    default; see tests/seed-webdav.ps1 for the exact contents.

.EXAMPLE
    tests/start-local.ps1
    tests/start-local.ps1 -NoBuild -NoAppPublish
    tests/start-local.ps1 -ServerOnly
    tests/start-local.ps1 -Seed
#>
param(
    [switch] $SkipClean,
    [switch] $NoBuild,
    [switch] $NoAppPublish,
    [switch] $ServerOnly,
    [string] $OutputFolder = "",
    [switch] $StartApp,
    [switch] $Seed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path $PSScriptRoot -Parent
$resultsDir = Join-Path $repoRoot "TestResults"
$appProject = Join-Path $repoRoot "src\CloudDrive.App\CloudDrive.App.csproj"
$publishDir = Join-Path $repoRoot "build\publish"
$appExePath = Join-Path $publishDir "CloudDrive.App.exe"
$stateFile  = Join-Path $resultsDir ".session.json"
$runApp     = -not $ServerOnly

function Write-Step($msg) { Write-Host ""; Write-Host "---- $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  !!  $msg" -ForegroundColor Red; throw $msg }
function Write-Note($msg) { Write-Host "     $msg" -ForegroundColor Gray }

. (Join-Path $PSScriptRoot "native-command.ps1")
. (Join-Path $PSScriptRoot "seed-webdav.ps1")

function Save-SessionState {
    param(
        [Parameter(Mandatory = $true)]
        $SessionState,

        [Parameter(Mandatory = $true)]
        [string] $StateFile
    )

    $SessionState | ConvertTo-Json | Set-Content $StateFile -Encoding UTF8
}

function Get-CloudDriveAppProcesses {
    @(Get-Process -Name "CloudDrive.App" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try {
            $path = $_.MainModule.FileName
        } catch {
            try { $path = $_.Path } catch { $path = $null }
        }

        [pscustomobject] @{
            Process = $_
            Id      = $_.Id
            Path    = $path
        }
    })
}

function Assert-NoCloudDriveAppAlreadyRunning {
    $existingProcesses = @(Get-CloudDriveAppProcesses)
    if ($existingProcesses.Count -eq 0) {
        return
    }

    $details = ($existingProcesses | ForEach-Object {
        if ($_.Path) {
            "PID $($_.Id) ($($_.Path))"
        } else {
            "PID $($_.Id)"
        }
    }) -join ", "

    Write-Fail "CloudDrive.App is already running: $details. Close it before starting a new local session."
}

function Publish-CloudDriveApp {
    Write-Step "Publishing CloudDrive app"

    $dotnetExitCode = Invoke-NativeQuiet -FilePath dotnet -Arguments @("--info")
    if ($dotnetExitCode -ne 0) {
        Write-Fail ".NET SDK is not available. Install .NET 9 SDK or rerun with -ServerOnly."
    }

    $publishArgs = @(
        "publish",
        $appProject,
        "-c", "Debug",
        "-r", "win-x64",
        "--no-self-contained",
        "-o", $publishDir
    )

    $publishExitCode = Invoke-NativeNote -FilePath dotnet -Arguments $publishArgs
    if ($publishExitCode -ne 0) { Write-Fail "dotnet publish failed" }

    if (-not (Test-Path -LiteralPath $appExePath)) {
        Write-Fail "Published app executable not found: $appExePath"
    }

    Write-Ok "CloudDrive app published -> build\publish\CloudDrive.App.exe"
}

function Start-CloudDriveAppAndWait {
    param(
        [Parameter(Mandatory = $true)]
        $SessionState,

        [Parameter(Mandatory = $true)]
        [string] $StateFile
    )

    if (-not (Test-Path -LiteralPath $appExePath)) {
        Write-Fail "CloudDrive app executable not found at $appExePath. Rerun without -NoAppPublish."
    }

    $resolvedAppExePath = [System.IO.Path]::GetFullPath($appExePath)

    Write-Step "Starting CloudDrive app"

    $hadDebugJsonLog = Test-Path Env:CLOUDDRIVE_DEBUG_JSONLOG
    $previousDebugJsonLog = $env:CLOUDDRIVE_DEBUG_JSONLOG
    try {
        $env:CLOUDDRIVE_DEBUG_JSONLOG = "1"
        $process = Start-Process `
            -FilePath $resolvedAppExePath `
            -WorkingDirectory $repoRoot `
            -PassThru
    } finally {
        if ($hadDebugJsonLog) {
            $env:CLOUDDRIVE_DEBUG_JSONLOG = $previousDebugJsonLog
        } else {
            Remove-Item Env:CLOUDDRIVE_DEBUG_JSONLOG -ErrorAction SilentlyContinue
        }
    }

    Start-Sleep -Milliseconds 750
    if ($process.HasExited) {
        Write-Fail "CloudDrive app exited immediately (exit code $($process.ExitCode))."
    }

    $SessionState["app_process_id"] = $process.Id
    Save-SessionState -SessionState $SessionState -StateFile $StateFile

    Write-Ok "CloudDrive app started with JSON debug logging (PID $($process.Id))"
    Write-Note "Use the app now. Close/exit CloudDrive when you are done testing."
    Write-Note "This terminal will then collect logs, merge them, and stop Docker."

    try {
        $process.WaitForExit()
    } finally {
        $process.Refresh()
        if (-not $process.HasExited) {
            Write-Note "Stopping CloudDrive app because the start script is exiting..."
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            [void] $process.WaitForExit(5000)
        }
    }

    $process.Refresh()
    if ($process.HasExited) {
        Write-Ok "CloudDrive app exited (exit code $($process.ExitCode))"
    }
}

function Invoke-SessionCleanup {
    param(
        [Parameter(Mandatory = $true)]
        [string] $OutputFolder
    )

    $stopScript = Join-Path $PSScriptRoot "stop-local.ps1"
    Write-Step "Collecting logs and stopping local session"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -OutputFolder $OutputFolder
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "stop-local.ps1 failed (exit code $LASTEXITCODE)"
    }
}

if ($StartApp -and $ServerOnly) {
    Write-Fail "-StartApp and -ServerOnly cannot be used together."
}

if ($runApp) {
    Assert-NoCloudDriveAppAlreadyRunning
}

# -- 0. Docker check -----------------------------------------------------------
Write-Step "Checking prerequisites"
$dockerInfoExitCode = Invoke-NativeQuiet -FilePath docker -Arguments @("info")
if ($dockerInfoExitCode -ne 0) { Write-Fail "Docker is not running - please start Docker Desktop." }
Write-Ok "Docker is running"

# -- 1. Clear TestResults ------------------------------------------------------
if (-not $SkipClean) {
    Write-Step "Clearing TestResults/"
    if (Test-Path $resultsDir) {
        Remove-Item $resultsDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    Write-Ok "TestResults/ cleared"
}
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

# -- 2. Start Docker Compose ---------------------------------------------------
Write-Step "Starting local WebDAV test server"
Set-Location $repoRoot

if ($NoBuild) {
    $dockerExitCode = Invoke-NativeNote -FilePath docker -Arguments @("compose", "up", "-d")
} else {
    $dockerExitCode = Invoke-NativeNote -FilePath docker -Arguments @("compose", "up", "-d", "--build")
}
if ($dockerExitCode -ne 0) { Write-Fail "docker compose up failed" }

# -- 3. Wait for healthcheck ---------------------------------------------------
Write-Note "Waiting for WebDAV server health check..."
$maxWait = 30
for ($i = 0; $i -lt $maxWait; $i++) {
    $inspectResult = Invoke-NativeCapture -FilePath docker -Arguments @("inspect", "clouddrive-webdav-test", "--format", "{{.State.Health.Status}}")
    $status = if ($inspectResult.ExitCode -eq 0) { $inspectResult.Output | Select-Object -First 1 } else { "" }
    if ($status -eq "healthy") { break }
    Start-Sleep -Seconds 1
}
if ($i -ge $maxWait) { Write-Fail "WebDAV server did not become healthy within ${maxWait}s" }
Write-Ok "WebDAV server healthy -> http://localhost:8080/"

# -- 4. Write session state ----------------------------------------------------
$sessionStart = [System.DateTime]::UtcNow
$stamp = $sessionStart.ToString("yyyyMMdd-HHmmss")
if (-not $OutputFolder) {
    $OutputFolder = Join-Path $resultsDir "session-$stamp"
}
$OutputFolder = [System.IO.Path]::GetFullPath($OutputFolder)

$sessionState = [ordered]@{
    start_utc                   = $sessionStart.ToString("o")
    webdav_url                  = "http://localhost:8080/"
    webdav_user                 = "testuser"
    docker_container            = "clouddrive-webdav-test"
    app_executable              = if ($runApp) { $appExePath } else { $null }
    app_process_id              = $null
    output_folder               = $OutputFolder
    managed_by_start_local      = $runApp
    start_local_process_id      = $PID
}
Save-SessionState -SessionState $sessionState -StateFile $stateFile
Write-Ok "Session state written -> .session.json"

# -- 5. Seed WebDAV (opt-in) ---------------------------------------------------
if ($Seed) {
    Invoke-WebDavSeed `
        -Url      $sessionState["webdav_url"] `
        -Username $sessionState["webdav_user"] `
        -Password "testpass"
}

function Write-SessionReady {
    Write-Step "Session ready"

    Write-Host ""
    Write-Host "  WebDAV server: http://localhost:8080/ (testuser / testpass)" -ForegroundColor White
    Write-Host "  Logs folder  : $OutputFolder" -ForegroundColor White

    if ($runApp) {
        Write-Host "  CloudDrive   : starting now; close/exit the app to finish the session" -ForegroundColor White
        Write-Host ""
        Write-Host "  Configure CloudDrive with:" -ForegroundColor DarkGray
        Write-Host '  URL      http://localhost:8080/' -ForegroundColor Yellow
        Write-Host '  Username testuser' -ForegroundColor Yellow
        Write-Host '  Password testpass' -ForegroundColor Yellow
    } else {
        Write-Host "  CloudDrive   : not started (-ServerOnly)" -ForegroundColor White
        Write-Host ""
        Write-Host "  To run E2E tests manually:" -ForegroundColor DarkGray
        Write-Host '  $env:CLOUDDRIVE_TEST_WEBDAV_URL      = "http://localhost:8080/"' -ForegroundColor Yellow
        Write-Host '  $env:CLOUDDRIVE_TEST_USERNAME        = "testuser"' -ForegroundColor Yellow
        Write-Host '  $env:CLOUDDRIVE_TEST_PASSWORD        = "testpass"' -ForegroundColor Yellow
        Write-Host '  dotnet test tests/CloudDrive.Core.Tests --filter Category=E2E' -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  When done, collect logs and stop Docker:" -ForegroundColor DarkGray
        Write-Host "  tests/stop-local.ps1 -OutputFolder `"$OutputFolder`"" -ForegroundColor Yellow
    }
    Write-Host ""
}

if (-not $runApp) {
    Write-SessionReady
    return
}

$cleanupNeeded = $true
try {
    Assert-NoCloudDriveAppAlreadyRunning

    if ($NoAppPublish) {
        Write-Step "Checking CloudDrive app publish output"
        if (-not (Test-Path -LiteralPath $appExePath)) {
            Write-Fail "CloudDrive app executable not found at $appExePath. Rerun without -NoAppPublish."
        }
        Write-Ok "Found build\publish\CloudDrive.App.exe"
    } else {
        Publish-CloudDriveApp
    }

    Write-SessionReady
    Start-CloudDriveAppAndWait -SessionState $sessionState -StateFile $stateFile
} finally {
    if ($cleanupNeeded) {
        Invoke-SessionCleanup -OutputFolder $OutputFolder
    }
}
