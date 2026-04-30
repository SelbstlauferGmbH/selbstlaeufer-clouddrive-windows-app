<#
.SYNOPSIS
    Seeds the local CloudDrive WebDAV test server with a curated tree of files.

.DESCRIPTION
    Used by tests/start-local.ps1 -Seed to populate the empty webdav-data volume
    with files of varied sizes, types, and naming edge cases so that manual
    sessions can exercise placeholders, hydration, and Explorer overlays without
    uploading files by hand.

    Communicates with the server via raw System.Net.Http.HttpClient because
    Windows PowerShell 5.1's Invoke-WebRequest does not support arbitrary HTTP
    methods (MKCOL).
#>

Set-StrictMode -Version Latest

function Write-Step([string]$msg) { Write-Host ""; Write-Host "---- $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Note([string]$msg) { Write-Host "     $msg" -ForegroundColor Gray }
function Write-Fail([string]$msg) { Write-Host "  !!  $msg" -ForegroundColor Red; throw $msg }

function Get-LoremIpsum {
    param([int] $ApproxSize)
    $para = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum." + [Environment]::NewLine + [Environment]::NewLine
    $sb = [System.Text.StringBuilder]::new()
    while ($sb.Length -lt $ApproxSize) {
        [void] $sb.Append($para)
    }
    if ($sb.Length -gt $ApproxSize) {
        return $sb.ToString().Substring(0, $ApproxSize)
    }
    return $sb.ToString()
}

function Get-SampleYaml {
    return @'
# CloudDrive sample seeded config
service:
  name: clouddrive
  environment: local
  port: 8080
storage:
  provider: webdav
  base_url: http://localhost:8080
features:
  - placeholders
  - hydration
  - icon-overlays
'@
}

function Get-SampleMarkdown {
    return @'
# CloudDrive Seed Readme

This file is part of the seeded test fixture written by `tests/start-local.ps1 -Seed`.

## Folder layout

- `text/`     - small text payloads (txt, yaml, csv, md)
- `docs/`     - office-style binary blobs (pdf, pptx, xlsx)
- `media/`    - image and video blobs (jpg, png, mp4)
- `archives/` - archive blobs (zip)
- `large/`    - larger blob for hydration testing
- `edge/`     - filename edge cases (empty, spaces, non-ASCII)
- `nested/`   - deep folder nesting

## Notes

Binary payloads contain deterministic random bytes; only the extension is real.
Treat these as opaque blobs - opening them in their native app will fail.
'@
}

function Get-SampleCsv {
    param([int] $Rows)
    $sb = [System.Text.StringBuilder]::new()
    [void] $sb.AppendLine("id,name,email,department,joined")
    for ($i = 1; $i -le $Rows; $i++) {
        $dept  = (($i - 1) % 5) + 1
        $month = (($i - 1) % 12) + 1
        [void] $sb.AppendLine("$i,User $i,user$i@example.com,Department $dept,2024-$('{0:D2}' -f $month)-15")
    }
    return $sb.ToString()
}

function Write-SeedPayload {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] $Spec
    )

    switch ($Spec.Kind) {
        "text" {
            $utf8 = [System.Text.UTF8Encoding]::new($false)
            [System.IO.File]::WriteAllText($Path, [string] $Spec.Body, $utf8)
        }
        "random" {
            $rng = [System.Random]::new([int] $Spec.Seed)
            $bytes = New-Object byte[] ([int] $Spec.Size)
            $rng.NextBytes($bytes)
            [System.IO.File]::WriteAllBytes($Path, $bytes)
        }
        "xlsx" {
            Write-MinimalXlsx -Path $Path -Rows ([int] $Spec.Rows)
        }
        default {
            throw "Unknown seed payload kind: $($Spec.Kind)"
        }
    }
}

function Write-MinimalXlsx {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [int] $Rows = 200
    )

    if (-not ('System.IO.Compression.ZipArchive' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }

    $contentTypes = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
</Types>
'@

    $rootRels = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
'@

    $workbook = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
<sheets><sheet name="Seed" sheetId="1" r:id="rId1"/></sheets>
</workbook>
'@

    $workbookRels = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
</Relationships>
'@

    $sb = [System.Text.StringBuilder]::new()
    [void] $sb.Append('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>')
    [void] $sb.Append('<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>')
    [void] $sb.Append('<row r="1"><c r="A1" t="inlineStr"><is><t>id</t></is></c><c r="B1" t="inlineStr"><is><t>name</t></is></c><c r="C1" t="inlineStr"><is><t>email</t></is></c><c r="D1" t="inlineStr"><is><t>department</t></is></c></row>')
    for ($i = 1; $i -le $Rows; $i++) {
        $r    = $i + 1
        $dept = (($i - 1) % 5) + 1
        [void] $sb.Append("<row r=""$r""><c r=""A$r""><v>$i</v></c><c r=""B$r"" t=""inlineStr""><is><t>User $i</t></is></c><c r=""C$r"" t=""inlineStr""><is><t>user$i@example.com</t></is></c><c r=""D$r"" t=""inlineStr""><is><t>Department $dept</t></is></c></row>")
    }
    [void] $sb.Append('</sheetData></worksheet>')
    $sheet = $sb.ToString()

    $entries = @(
        @{ Name = '[Content_Types].xml';        Body = $contentTypes },
        @{ Name = '_rels/.rels';                Body = $rootRels },
        @{ Name = 'xl/workbook.xml';            Body = $workbook },
        @{ Name = 'xl/_rels/workbook.xml.rels'; Body = $workbookRels },
        @{ Name = 'xl/worksheets/sheet1.xml';   Body = $sheet }
    )

    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $fs   = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new(
            $fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($e in $entries) {
                $entry  = $zip.CreateEntry($e.Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $stream = $entry.Open()
                try {
                    $bytes = $utf8.GetBytes([string] $e.Body)
                    $stream.Write($bytes, 0, $bytes.Length)
                } finally {
                    $stream.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $fs.Dispose()
    }
}

function Join-WebDavUrl {
    param(
        [Parameter(Mandatory = $true)] [string] $BaseUrl,
        [Parameter(Mandatory = $true)] [string] $RelativePath
    )

    $segments = $RelativePath -split '/' | Where-Object { $_ -ne "" }
    $encoded  = $segments | ForEach-Object { [System.Uri]::EscapeDataString($_) }
    return ($BaseUrl.TrimEnd('/')) + "/" + ($encoded -join '/')
}

function New-WebDavCollection {
    param(
        [Parameter(Mandatory = $true)] $Client,
        [Parameter(Mandatory = $true)] [string] $Url
    )

    $req  = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new('MKCOL'), $Url)
    try {
        $resp = $Client.SendAsync($req).GetAwaiter().GetResult()
        try {
            $code = [int] $resp.StatusCode
            # 201 Created = new collection. 405 Method Not Allowed = already a collection. Both are fine.
            if ($code -eq 201 -or $code -eq 405) {
                return
            }
            $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            Write-Fail "MKCOL $Url failed: HTTP $code $($resp.ReasonPhrase) - $body"
        } finally {
            $resp.Dispose()
        }
    } finally {
        $req.Dispose()
    }
}

function Send-WebDavFile {
    param(
        [Parameter(Mandatory = $true)] $Client,
        [Parameter(Mandatory = $true)] [string] $Url,
        [Parameter(Mandatory = $true)] [string] $LocalPath
    )

    $stream = [System.IO.File]::OpenRead($LocalPath)
    try {
        $content = [System.Net.Http.StreamContent]::new($stream)
        $req     = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Put, $Url)
        $req.Content = $content
        try {
            $resp = $Client.SendAsync($req).GetAwaiter().GetResult()
            try {
                $code = [int] $resp.StatusCode
                # 200 OK / 201 Created (new) / 204 No Content (overwrite) - all fine.
                if ($code -ne 200 -and $code -ne 201 -and $code -ne 204) {
                    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    Write-Fail "PUT $Url failed: HTTP $code $($resp.ReasonPhrase) - $body"
                }
            } finally {
                $resp.Dispose()
            }
        } finally {
            $req.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Get-SeedDefinition {
    # Build the umlaut filename without a literal non-ASCII char in source, so
    # the script source stays plain ASCII regardless of how the host loads it.
    $umlautChars = -join @([char] 0x00E4, [char] 0x00F6, [char] 0x00FC)  # ä ö ü
    $umlautRel   = "edge/umlaut-$umlautChars.txt"

    $folders = @(
        "text",
        "docs",
        "media",
        "archives",
        "large",
        "edge",
        "nested",
        "nested/a",
        "nested/a/b",
        "nested/a/b/c"
    )

    $files = @(
        @{ Path = "text/notes.txt";   Kind = "text"; Body = (Get-LoremIpsum 2KB) },
        @{ Path = "text/config.yaml"; Kind = "text"; Body = (Get-SampleYaml) },
        @{ Path = "text/data.csv";    Kind = "text"; Body = (Get-SampleCsv 200) },
        @{ Path = "text/readme.md";   Kind = "text"; Body = (Get-SampleMarkdown) },

        @{ Path = "docs/report.pdf";        Kind = "random"; Size = 1MB;   Seed = 101 },
        @{ Path = "docs/presentation.pptx"; Kind = "random"; Size = 4MB;   Seed = 102 },
        @{ Path = "docs/spreadsheet.xlsx";  Kind = "xlsx";   Rows = 200 },

        @{ Path = "media/photo.jpg";      Kind = "random"; Size = 2MB;   Seed = 201 },
        @{ Path = "media/screenshot.png"; Kind = "random"; Size = 800KB; Seed = 202 },
        @{ Path = "media/clip.mp4";       Kind = "random"; Size = 12MB;  Seed = 203 },

        @{ Path = "archives/backup.zip"; Kind = "random"; Size = 8MB; Seed = 301 },

        @{ Path = "large/dataset.bin"; Kind = "random"; Size = 50MB; Seed = 401 },

        @{ Path = "edge/empty.txt";          Kind = "text"; Body = "" },
        @{ Path = "edge/spaces in name.txt"; Kind = "text"; Body = "Filename with spaces test." + [Environment]::NewLine },
        @{ Path = $umlautRel;                Kind = "text"; Body = "Filename with non-ASCII characters (umlauts)." + [Environment]::NewLine },

        @{ Path = "nested/a/b/c/deep.txt"; Kind = "text"; Body = "Deeply nested file at nested/a/b/c/deep.txt." + [Environment]::NewLine }
    )

    return @{ Folders = $folders; Files = $files }
}

function Invoke-WebDavSeed {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $Url,
        [Parameter(Mandatory = $true)] [string] $Username,
        [Parameter(Mandatory = $true)] [string] $Password,
        [string] $RootName = "seed"
    )

    if (-not ('System.Net.Http.HttpClient' -as [type])) {
        Add-Type -AssemblyName System.Net.Http
    }

    $baseUrl  = $Url.TrimEnd('/') + "/" + [System.Uri]::EscapeDataString($RootName)
    $authBlob = [System.Convert]::ToBase64String(
        [System.Text.Encoding]::ASCII.GetBytes("${Username}:${Password}"))

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [System.TimeSpan]::FromMinutes(5)
    $client.DefaultRequestHeaders.Authorization = `
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Basic', $authBlob)

    $stagingDir = Join-Path $env:TEMP ("clouddrive-seed-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

    Write-Step "Seeding WebDAV server ($RootName/)"

    try {
        $tree = Get-SeedDefinition

        # Stage payloads on disk first so we can stream them via HttpClient.
        foreach ($file in $tree.Files) {
            $localRel  = $file.Path -replace '/', [System.IO.Path]::DirectorySeparatorChar
            $localPath = Join-Path $stagingDir $localRel
            $localDir  = Split-Path $localPath -Parent
            if (-not (Test-Path -LiteralPath $localDir)) {
                New-Item -ItemType Directory -Force -Path $localDir | Out-Null
            }
            Write-SeedPayload -Path $localPath -Spec $file
        }

        # Create the root collection plus all subfolders (parents first).
        New-WebDavCollection -Client $client -Url $baseUrl
        foreach ($folder in $tree.Folders) {
            $folderUrl = Join-WebDavUrl $baseUrl $folder
            New-WebDavCollection -Client $client -Url $folderUrl
        }
        Write-Note "Created $($tree.Folders.Count + 1) collections"

        # Upload files.
        $totalFiles = 0
        $totalBytes = 0L
        foreach ($file in $tree.Files) {
            $localRel  = $file.Path -replace '/', [System.IO.Path]::DirectorySeparatorChar
            $localPath = Join-Path $stagingDir $localRel
            $remoteUrl = Join-WebDavUrl $baseUrl $file.Path
            Send-WebDavFile -Client $client -Url $remoteUrl -LocalPath $localPath
            $totalFiles += 1
            $totalBytes += (Get-Item -LiteralPath $localPath).Length
        }

        $sizeMb = [math]::Round($totalBytes / 1MB, 1)
        Write-Ok "Seeded $totalFiles files (~$sizeMb MB) into $RootName/"
    }
    finally {
        $client.Dispose()
        if (Test-Path -LiteralPath $stagingDir) {
            Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
