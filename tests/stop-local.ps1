<#
.SYNOPSIS
    Stops the local WebDAV test server and merges all logs into a single file.

.DESCRIPTION
    1. Reads the session state from TestResults/.session.json (written by start-local.ps1)
    2. Stops any CloudDrive.App process started from this workspace
    3. Captures WebDAV server logs since session start
    4. Stops Docker Compose
    5. Finds test-client JSONL files (TestSessionLogger + JsonFileLogSink) in TestResults/
    6. Finds CloudDrive app debug JSONL files in %LOCALAPPDATA%\CloudDrive\logs\
       (only written when CLOUDDRIVE_DEBUG_JSONLOG=1 was set before starting the app)
    7. Merges everything into one time-sorted JSONL file (one JSON object per line)
    8. Writes the merged file to -OutputFolder

    The merged file is the single artifact you hand to Claude Code, Codex, or any
    other AI tool for debugging.  Each line is one JSON object with at least:
      ts        ISO-8601 UTC timestamp
      source    "webdav-server" | "test-client" | "app-log"
      (+ method/path/status for server events, level/category/message for app/test events)

.PARAMETER OutputFolder
    Where to write the merged log. Default: TestResults/session-<start-timestamp>

.PARAMETER KeepDocker
    Do not stop Docker Compose (useful when you want to run more tests).

.PARAMETER KeepApp
    Do not stop CloudDrive.App before collecting logs.

.EXAMPLE
    tests/stop-local.ps1
    tests/stop-local.ps1 -OutputFolder C:\debug\my-session
    tests/stop-local.ps1 -KeepDocker
#>
param(
    [string] $OutputFolder = "",
    [switch] $KeepDocker,
    [switch] $KeepApp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path $PSScriptRoot -Parent
$resultsDir = Join-Path $repoRoot "TestResults"

function Write-Step($msg) { Write-Host ""; Write-Host "──── $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  ✔  $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  ✘  $msg" -ForegroundColor Red }
function Write-Note($msg) { Write-Host "     $msg" -ForegroundColor Gray }

function Get-JsonPropertyValue {
    param(
        [AllowNull()]
        $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Use-ExistingServerLogOrSkip($path) {
    if (Test-Path -LiteralPath $path) {
        $lineCount = (Get-Content $path | Measure-Object -Line).Lines
        Write-Note "Using existing WebDAV server log: $lineCount lines → $(Split-Path $path -Leaf)"
        return $path
    }

    return $null
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

function Test-PathUnderDirectory {
    param(
        [AllowNull()]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    if (-not $Path) {
        return $false
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $fullDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

        return $fullPath.StartsWith($fullDirectory, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Stop-SessionCloudDriveApp {
    param(
        [Parameter(Mandatory = $true)]
        $State
    )

    if ($KeepApp) {
        Write-Note "Keeping CloudDrive app alive (-KeepApp)."
        return
    }

    Write-Step "Stopping CloudDrive app"

    $stateAppProcessId = Get-JsonPropertyValue $State "app_process_id"
    $stateAppExecutable = Get-JsonPropertyValue $State "app_executable"
    $workspaceProcesses = @(Get-CloudDriveAppProcesses | Where-Object {
        $isStateProcess = $false
        $isStateExecutable = $false
        if ($stateAppExecutable -and $_.Path) {
            try {
                $isStateExecutable = [string]::Equals(
                    [System.IO.Path]::GetFullPath($_.Path),
                    [System.IO.Path]::GetFullPath($stateAppExecutable),
                    [System.StringComparison]::OrdinalIgnoreCase)
            } catch {
                $isStateExecutable = $false
            }
        }

        $isWorkspaceProcess = Test-PathUnderDirectory -Path $_.Path -Directory $repoRoot
        if ($stateAppProcessId -and ($_.Id -eq [int] $stateAppProcessId)) {
            $isStateProcess = $isStateExecutable -or $isWorkspaceProcess
        }

        $isStateProcess -or $isStateExecutable -or $isWorkspaceProcess
    })

    if ($workspaceProcesses.Count -eq 0) {
        Write-Note "No CloudDrive.App process from this workspace is running."
        return
    }

    foreach ($entry in $workspaceProcesses) {
        $process = $entry.Process
        $pathSuffix = if ($entry.Path) { " ($($entry.Path))" } else { "" }
        Write-Note "Stopping CloudDrive.App PID $($entry.Id)$pathSuffix"

        try {
            $closed = $false
            if ($process.MainWindowHandle -ne 0) {
                $closed = $process.CloseMainWindow()
            }

            if ($closed) {
                [void] $process.WaitForExit(10000)
            }

            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
                [void] $process.WaitForExit(5000)
            }
        } catch {
            Write-Note "Could not stop CloudDrive.App PID $($entry.Id): $($_.Exception.Message)"
        }
    }

    Start-Sleep -Milliseconds 750
    Write-Ok "CloudDrive app stopped"
}

# Load merge function
. (Join-Path $PSScriptRoot "merge-logs.ps1")
. (Join-Path $PSScriptRoot "native-command.ps1")

# ── 1. Read session state ──────────────────────────────────────────────────────
Write-Step "Reading session state"
$stateFile = Join-Path $resultsDir ".session.json"
if (-not (Test-Path $stateFile)) {
    Write-Fail "No session state found at $stateFile — did you run start-local.ps1 first?"
    exit 1
}
$state = Get-Content $stateFile -Raw | ConvertFrom-Json
$sessionStart = [System.DateTime]::Parse($state.start_utc).ToUniversalTime()
$sinceIso     = $sessionStart.ToString("yyyy-MM-ddTHH:mm:ssZ")
$stamp        = $sessionStart.ToString("yyyyMMdd-HHmmss")

Write-Ok "Session started at $sinceIso"

if (-not $OutputFolder) {
    $OutputFolder = Join-Path $resultsDir "session-$stamp"
}
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
Write-Ok "Output folder: $OutputFolder"

# ── 2. Stop app before Docker so it cannot keep polling a dead server ─────────
Stop-SessionCloudDriveApp -State $state

# ── 3. Collect WebDAV server logs ──────────────────────────────────────────────
Write-Step "Collecting WebDAV server logs"
$serverLogPath = Join-Path $OutputFolder "server-webdav.jsonl"

$dockerInfoExitCode = Invoke-NativeQuiet -FilePath docker -Arguments @("info")
if ($dockerInfoExitCode -ne 0) {
    Write-Note "Docker is not running — skipping server log collection."
    $serverLogPath = Use-ExistingServerLogOrSkip $serverLogPath
} else {
    $runningResult = Invoke-NativeCapture -FilePath docker -Arguments @("ps", "--filter", "name=clouddrive-webdav-test", "--format", "{{.Names}}")
    $running = if ($runningResult.ExitCode -eq 0) { $runningResult.Output | Select-Object -First 1 } else { "" }
    if ($running -eq "clouddrive-webdav-test") {
        $logsExitCode = Invoke-NativeOutputFile -OutputPath $serverLogPath -FilePath docker -Arguments @("logs", "clouddrive-webdav-test", "--since", $sinceIso)
        if ($logsExitCode -ne 0) {
            Write-Fail "docker logs failed"
            exit 1
        }

        $lineCount = (Get-Content $serverLogPath | Measure-Object -Line).Lines
        Write-Ok "WebDAV server: $lineCount lines → $(Split-Path $serverLogPath -Leaf)"
    } else {
        Write-Note "Container clouddrive-webdav-test is not running — server logs unavailable."
        $serverLogPath = Use-ExistingServerLogOrSkip $serverLogPath
    }

    # ── 4. Stop Docker ────────────────────────────────────────────────────────
    if (-not $KeepDocker) {
        Write-Step "Stopping Docker Compose"
        Set-Location $repoRoot
        [void] (Invoke-NativeQuiet -FilePath docker -Arguments @("compose", "down", "-v"))
        Write-Ok "Containers stopped and volumes removed"
    } else {
        Write-Note "Keeping containers alive (-KeepDocker)."
    }
}

# ── 5. Collect test-client JSONL files ────────────────────────────────────────
Write-Step "Collecting test-client logs"
$clientFiles = @(Get-ChildItem $resultsDir -Filter "session-*.jsonl" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -ge $sessionStart } |
    Sort-Object LastWriteTime |
    Select-Object -ExpandProperty FullName)

# Also include real-time streaming logs (same session, named session-*-stream.jsonl)
$streamFiles = @(Get-ChildItem $resultsDir -Filter "*-stream.jsonl" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -ge $sessionStart } |
    Sort-Object LastWriteTime |
    Select-Object -ExpandProperty FullName)

$allClientFiles = @($clientFiles + $streamFiles | Select-Object -Unique)
Write-Ok "Found $($allClientFiles.Count) test-client file(s)"
foreach ($f in $allClientFiles) { Write-Note "  $(Split-Path $f -Leaf)" }

# ── 6. Collect app debug JSONL (when CLOUDDRIVE_DEBUG_JSONLOG=1 was set) ───────
Write-Step "Collecting app debug logs"
$appLogDir   = Join-Path $env:LOCALAPPDATA "CloudDrive\logs"
$appLogFiles = @()
if (Test-Path $appLogDir) {
    # Rolling file names: debug-YYYYMMDD.jsonl and debug-watchdog-YYYYMMDD.jsonl
    $appLogFiles = @(Get-ChildItem $appLogDir -Filter "debug*.jsonl" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge ($sessionStart - [TimeSpan]::FromHours(1)) } |
        Sort-Object LastWriteTime |
        Select-Object -ExpandProperty FullName)
}
if ($appLogFiles.Count -gt 0) {
    Write-Ok "Found $($appLogFiles.Count) app debug log file(s)"
    foreach ($f in $appLogFiles) { Write-Note "  $(Split-Path $f -Leaf)" }
} else {
    Write-Note "No app debug logs found (set CLOUDDRIVE_DEBUG_JSONLOG=1 before starting the app)"
}

# ── 7. Merge all logs ──────────────────────────────────────────────────────────
Write-Step "Merging all logs"
$allInputFiles = @()
if ($serverLogPath  -and (Test-Path $serverLogPath))  { $allInputFiles += $serverLogPath  }
foreach ($f in $allClientFiles) { $allInputFiles += $f }
foreach ($f in $appLogFiles)    { $allInputFiles += $f }

if ($allInputFiles.Count -eq 0) {
    Write-Note "No log files found — nothing to merge."
    exit 0
}

$mergedPath = Join-Path $OutputFolder "merged-session-$stamp.jsonl"
$count = Merge-SessionLogs `
    -InputFiles $allInputFiles `
    -OutputFile $mergedPath `
    -Since $sessionStart

Write-Ok "$count events merged → $(Split-Path $mergedPath -Leaf)"

# ── 8. Copy source files to output folder ─────────────────────────────────────
foreach ($f in $allInputFiles) {
    $dest = Join-Path $OutputFolder (Split-Path $f -Leaf)
    if ($f -ne $dest) {
        Copy-Item $f $dest -Force -ErrorAction SilentlyContinue
    }
}

# ── 9. Summary ────────────────────────────────────────────────────────────────
Write-Step "Session complete"
Write-Host ""
Write-Host "  Output folder : $OutputFolder" -ForegroundColor White
Write-Host "  Merged log    : $(Split-Path $mergedPath -Leaf)  ($count events)" -ForegroundColor Green
Write-Host ""
Write-Host "  Tip: Open the merged JSONL in any AI tool for debugging." -ForegroundColor DarkGray
Write-Host "       Each line is one JSON event, sorted by timestamp." -ForegroundColor DarkGray
Write-Host ""
