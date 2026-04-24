<#
.SYNOPSIS
    Merges multiple JSON Lines log files into a single time-sorted JSONL file.

.DESCRIPTION
    Understands three source formats and normalises them to a common schema:

    1. test-client  (TestSessionLogger / JsonFileLogSink)
       {"ts":"...","record":"log-event","source":"test-client","level":"Information","category":"...","message":"..."}

    2. webdav-server  (WsgiDAV log_middleware.py)
       {"ts":"...","source":"webdav-server","method":"PUT","path":"/...","status":"201","duration_ms":12}

    3. app-log  (Serilog JsonFormatter from CloudDrive.App / Watchdog when CLOUDDRIVE_DEBUG_JSONLOG=1)
       {"Timestamp":"...","Level":"Information","MessageTemplate":"...","Properties":{"SourceContext":"...",...}}

    All events are sorted by UTC timestamp.  The output file starts with a
    "merge-header" record and ends with a "merge-footer" record that summarise
    the session — useful for AI agents scanning the file.

.PARAMETER InputFiles
    Array of JSONL file paths to merge. Non-existent paths are silently skipped.

.PARAMETER OutputFile
    Destination JSONL file path. Parent directory is created if needed.

.PARAMETER Since
    Only include events at or after this UTC DateTime. Default: no filter.

.OUTPUTS
    Returns the count of events written (excluding header/footer).

.EXAMPLE
    # Dot-source to load the function, then call it
    . tests/merge-logs.ps1
    Merge-SessionLogs -InputFiles $files -OutputFile "TestResults/merged.jsonl"

    # With a time filter (only events after test session started)
    Merge-SessionLogs -InputFiles $files -OutputFile "out.jsonl" -Since $sessionStart
#>

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

function Merge-SessionLogs {
    [CmdletBinding()]
    param(
        [string[]] $InputFiles,
        [string]   $OutputFile,
        [System.DateTime] $Since = [System.DateTime]::MinValue
    )

    $events   = [System.Collections.Generic.List[PSCustomObject]]::new()
    $parsed   = 0
    $skipped  = 0
    $sources  = [System.Collections.Generic.HashSet[string]]::new()

    foreach ($file in $InputFiles) {
        if (-not $file -or -not (Test-Path $file)) { continue }

        foreach ($rawLine in (Get-Content $file -Encoding UTF8)) {
            $line = $rawLine.Trim()
            if (-not $line -or $line[0] -ne '{') { continue }

            $obj = $null
            try { $obj = $line | ConvertFrom-Json } catch { $skipped++; continue }

            # ── 1. Extract timestamp ──────────────────────────────────────────
            # Our format uses "ts"; Serilog JsonFormatter uses "Timestamp"
            $ts = Get-JsonPropertyValue $obj "ts"
            $timestamp = Get-JsonPropertyValue $obj "Timestamp"
            $tsRaw = if ($null -ne $ts) { $ts }
                     elseif ($null -ne $timestamp) { $timestamp }
                     else { $null }

            if (-not $tsRaw) { $skipped++; continue }

            $dt = $null
            try { $dt = [System.DateTimeOffset]::Parse($tsRaw).UtcDateTime } catch { $skipped++; continue }
            if ($dt -lt $Since) { continue }

            # ── 2. Normalise source / format ──────────────────────────────────
            $normalised = $null

            if ($null -ne $timestamp -and $null -eq $ts) {
                # Serilog JsonFormatter event — rewrite to our schema
                $cat = $null
                $properties = Get-JsonPropertyValue $obj "Properties"
                $sourceContext = Get-JsonPropertyValue $properties "SourceContext"
                if ($properties -and $sourceContext) {
                    $cat = $sourceContext
                    $dot = $cat.LastIndexOf('.')
                    if ($dot -ge 0) { $cat = $cat.Substring($dot + 1) }
                }

                $renderedMessage = Get-JsonPropertyValue $obj "RenderedMessage"
                $messageTemplate = Get-JsonPropertyValue $obj "MessageTemplate"
                $level = Get-JsonPropertyValue $obj "Level"
                $exception = Get-JsonPropertyValue $obj "Exception"

                $msg = if ($renderedMessage) { $renderedMessage }
                       else { $messageTemplate }

                $entry = [ordered]@{
                    ts       = $dt.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z"
                    source   = "app-log"
                    level    = $level
                    category = $cat
                    message  = $msg
                }
                # Carry structured properties (removes SourceContext to avoid duplication)
                if ($properties) {
                    $props = $properties | Select-Object -Property * -ExcludeProperty SourceContext
                    $propMembers = @($props | Get-Member -MemberType NoteProperty)
                    if ($propMembers.Count -gt 0) {
                        $entry.props = $props
                    }
                }
                if ($exception) { $entry.exception = $exception }
                $line = ($entry | ConvertTo-Json -Compress -Depth 6)
                $null = $sources.Add("app-log")
            } else {
                # Already in our format (test-client or webdav-server)
                $source = Get-JsonPropertyValue $obj "source"
                $src = if ($source) { $source } else { "unknown" }
                $null = $sources.Add($src)
            }

            # Skip internal bookkeeping records (stream-start, session-start, etc.)
            $recordValue = Get-JsonPropertyValue $obj "record"
            $record = if ($recordValue) { $recordValue } else { "" }
            if ($record -match '^(stream-start|stream-end|session-start|session-end|merge-header|merge-footer)$') {
                continue
            }

            $events.Add([PSCustomObject]@{ Dt = $dt; Line = $line })
            $parsed++
        }
    }

    # ── 3. Sort and write ─────────────────────────────────────────────────────
    $sorted = $events | Sort-Object { $_.Dt }

    $dir = Split-Path $OutputFile -Parent
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    $outLines = [System.Collections.Generic.List[string]]::new()

    # Header
    $header = [ordered]@{
        ts           = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z"
        record       = "merge-header"
        source       = "log-merger"
        total_events = $parsed
        skipped      = $skipped
        sources      = @($sources | Sort-Object)
        input_files  = @($InputFiles | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { Split-Path $_ -Leaf })
    }
    $outLines.Add(($header | ConvertTo-Json -Compress))

    foreach ($e in $sorted) { $outLines.Add($e.Line) }

    # Footer
    $footer = [ordered]@{
        ts           = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z"
        record       = "merge-footer"
        source       = "log-merger"
        total_events = $parsed
        sources      = @($sources | Sort-Object)
    }
    $outLines.Add(($footer | ConvertTo-Json -Compress))

    [System.IO.File]::WriteAllLines($OutputFile, $outLines, [System.Text.Encoding]::UTF8)
    return $parsed
}
