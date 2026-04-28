<#
.SYNOPSIS
    Runs CloudDrive E2E tests against a local WebDAV server in Docker.

.DESCRIPTION
    1. Builds and starts the WebDAV test servers via Docker Compose
    2. Waits for the server health checks to pass
    3. Runs xUnit E2E tests (filter: Category=E2E by default)
    4. Collects structured JSON server logs from Docker
    5. Stops Docker Compose (unless -KeepAlive)
    6. Exits with the dotnet test exit code

    Test results and logs land in: <repo-root>/TestResults/

.PARAMETER Filter
    xUnit --filter expression. Default: "Category=E2E"

.PARAMETER TimeoutSeconds
    Overrides CLOUDDRIVE_TEST_TIMEOUT_SECONDS (individual test timeout). Default: 60.

.PARAMETER KeepAlive
    Keep Docker containers running after tests (useful for manual inspection).

.PARAMETER NoBuild
    Skip dotnet build before running tests.

.EXAMPLE
    tests/run-e2e-local.ps1
    tests/run-e2e-local.ps1 -Filter "Category=E2E&ClassName=FullLifecycleTest"
    tests/run-e2e-local.ps1 -KeepAlive -TimeoutSeconds 120
#>
param(
    [string] $Filter          = "Category=E2E",
    [int]    $TimeoutSeconds  = 60,
    [switch] $KeepAlive,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot  = Split-Path $PSScriptRoot -Parent
$resultsDir = Join-Path $repoRoot "TestResults"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "──── $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg)  { Write-Host "  ✔  $msg" -ForegroundColor Green }
function Write-Fail([string]$msg){ Write-Host "  ✘  $msg" -ForegroundColor Red   }
function Write-Note([string]$msg){ Write-Host "     $msg" -ForegroundColor Gray  }

function Wait-DockerHealth([string]$ContainerName, [string]$DisplayUrl) {
    Write-Note "Waiting for $ContainerName health check..."
    $maxWait = 30
    $waited  = 0
    while ($waited -lt $maxWait) {
        $inspectResult = Invoke-NativeCapture -FilePath docker -Arguments @("inspect", $ContainerName, "--format", "{{.State.Health.Status}}")
        $status = if ($inspectResult.ExitCode -eq 0) { $inspectResult.Output | Select-Object -First 1 } else { "" }
        if ($status -eq "healthy") { break }
        Start-Sleep -Seconds 1
        $waited++
    }
    if ($waited -ge $maxWait) {
        Write-Fail "$ContainerName did not become healthy within ${maxWait}s"
        [void] (Invoke-NativeNote -FilePath docker -Arguments @("compose", "--profile", "lock-e2e", "logs"))
        if (-not $KeepAlive) { [void] (Invoke-NativeQuiet -FilePath docker -Arguments @("compose", "down", "-v")) }
        exit 1
    }
    Write-Ok "$ContainerName healthy at $DisplayUrl"
}

. (Join-Path $PSScriptRoot "native-command.ps1")

# ── 0. Prerequisites ──────────────────────────────────────────────────────────
Write-Step "Checking prerequisites"

$dockerInfoExitCode = Invoke-NativeQuiet -FilePath docker -Arguments @("info")
if ($dockerInfoExitCode -ne 0) {
    Write-Fail "Docker is not running — please start Docker Desktop."
    exit 1
}
Write-Ok "Docker is running"

# ── 1. Build solution (optional) ──────────────────────────────────────────────
if (-not $NoBuild) {
    Write-Step "Building solution"
    dotnet build (Join-Path $repoRoot "CloudDrive.sln") -c Debug --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Fail "Build failed"; exit 1 }
    Write-Ok "Build succeeded"
}

# ── 2. Start Docker Compose ───────────────────────────────────────────────────
Write-Step "Starting local WebDAV test servers (Docker Compose)"
$sessionStart = [System.DateTime]::UtcNow

Set-Location $repoRoot
$dockerExitCode = Invoke-NativeNote -FilePath docker -Arguments @("compose", "--profile", "lock-e2e", "up", "-d", "--build")
if ($dockerExitCode -ne 0) { Write-Fail "docker compose up failed"; exit 1 }

# ── 3. Wait for healthcheck ───────────────────────────────────────────────────
Wait-DockerHealth "clouddrive-webdav-test" "http://localhost:8080/"
Wait-DockerHealth "clouddrive-webdav-nolock-test" "http://localhost:8081/"

# ── 4. Set test environment variables ────────────────────────────────────────
$env:CLOUDDRIVE_TEST_WEBDAV_URL        = "http://localhost:8080/"
$env:CLOUDDRIVE_TEST_WEBDAV_NOLOCK_URL = "http://localhost:8081/"
$env:CLOUDDRIVE_TEST_USERNAME          = "testuser"
$env:CLOUDDRIVE_TEST_PASSWORD          = "testpass"
$env:CLOUDDRIVE_TEST_TIMEOUT_SECONDS   = "$TimeoutSeconds"

# ── 5. Run tests ──────────────────────────────────────────────────────────────
Write-Step "Running E2E tests  (filter: $Filter)"
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

$stamp      = (Get-Date).ToString("yyyyMMdd-HHmmss")
$trxPath    = Join-Path $resultsDir "e2e-$stamp.trx"
$outputPath = Join-Path $resultsDir "e2e-$stamp-dotnet.txt"

# Build the test command arguments
$testArgs = @(
    "test"
    (Join-Path $repoRoot "tests\CloudDrive.Core.Tests")
    "--filter", $Filter
    "--no-build"
    "--logger", "console;verbosity=normal"
    "--logger", "trx;LogFileName=$trxPath"
    "--"
    "RunConfiguration.TestSessionTimeout=300000"
)

$testExitCode = Invoke-NativeTee -OutputPath $outputPath -FilePath dotnet -Arguments $testArgs

if ($testExitCode -eq 0) {
    Write-Ok "All tests passed"
} else {
    Write-Fail "One or more tests failed (exit code $testExitCode)"
}

# ── 6. Collect server logs ────────────────────────────────────────────────────
Write-Step "Collecting WebDAV server logs"
$serverLogPath = Join-Path $resultsDir "e2e-$stamp-server.jsonl"
$noLockServerLogPath = Join-Path $resultsDir "e2e-$stamp-server-nolock.jsonl"
$sinceIso      = $sessionStart.ToString("yyyy-MM-ddTHH:mm:ssZ")

$logsExitCode = Invoke-NativeOutputFile -OutputPath $serverLogPath -FilePath docker -Arguments @("logs", "clouddrive-webdav-test", "--since", $sinceIso)
if ($logsExitCode -ne 0) { Write-Fail "docker logs failed"; exit 1 }

$lineCount = (Get-Content $serverLogPath | Measure-Object -Line).Lines
Write-Ok "Captured $lineCount server log lines → $(Split-Path $serverLogPath -Leaf)"

$noLockLogsExitCode = Invoke-NativeOutputFile -OutputPath $noLockServerLogPath -FilePath docker -Arguments @("logs", "clouddrive-webdav-nolock-test", "--since", $sinceIso)
if ($noLockLogsExitCode -ne 0) { Write-Fail "docker logs failed for no-lock server"; exit 1 }

$noLockLineCount = (Get-Content $noLockServerLogPath | Measure-Object -Line).Lines
Write-Ok "Captured $noLockLineCount no-lock server log lines → $(Split-Path $noLockServerLogPath -Leaf)"

# ── 7. Stop Docker Compose ────────────────────────────────────────────────────
if (-not $KeepAlive) {
    Write-Step "Stopping test infrastructure"
    [void] (Invoke-NativeQuiet -FilePath docker -Arguments @("compose", "down", "-v"))
    Write-Ok "Containers stopped and volumes removed"
} else {
    Write-Note "Containers kept alive (-KeepAlive). Run 'docker compose down -v' when done."
}

# ── 8. Merge all logs into one time-sorted JSONL ──────────────────────────────
Write-Step "Merging logs (client + server)"
. (Join-Path $PSScriptRoot "merge-logs.ps1")

# Collect client-side logs produced this run (TestSessionLogger + streaming sink)
$clientFiles = @(Get-ChildItem $resultsDir -Filter "session-*.jsonl" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -ge $sessionStart } |
    Select-Object -ExpandProperty FullName)

$allInputFiles = @($serverLogPath, $noLockServerLogPath) + $clientFiles

$mergedPath = Join-Path $resultsDir "e2e-$stamp-merged.jsonl"
$mergedCount = Merge-SessionLogs `
    -InputFiles $allInputFiles `
    -OutputFile $mergedPath `
    -Since $sessionStart

Write-Ok "$mergedCount events → $(Split-Path $mergedPath -Leaf)"

# ── 9. Summary ────────────────────────────────────────────────────────────────
Write-Step "Session summary"
Write-Note "Merged log  : $(Split-Path $mergedPath -Leaf)  ($mergedCount events)"
Write-Note "Server log  : $(Split-Path $serverLogPath -Leaf)"
Write-Note "No-lock log : $(Split-Path $noLockServerLogPath -Leaf)"
Write-Note "Test output : $(Split-Path $outputPath -Leaf)"
Write-Note "TRX report  : $(Split-Path $trxPath -Leaf)"

Write-Host ""
if ($testExitCode -eq 0) {
    Write-Host "SUCCESS" -ForegroundColor Green
} else {
    Write-Host "FAILED  (exit $testExitCode)" -ForegroundColor Red
}

exit $testExitCode
