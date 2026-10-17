# Private backup of every photo the device photo database references (docs/device-reference.md, "Photo references").
# The release ships no photo: JoyPro fetches them on the user's PC. This copy is only for the maintainer, so that a link
# that dies later can be recovered (and the curated marker positions, keyed by the photo's SHA-256, stay valid).
#
#   tools\backup_device_photos.ps1 -Target <a PRIVATE folder outside the repository>
#
# For each photo: the file (<model>\<view>-<sha12>.<ext>) and a line in manifest.json: model, view, URL, source page,
# date, SHA-256, size. The target must not be inside the repository; nothing in the build or release reads it.
param(
    [Parameter(Mandatory = $true)] [string]$Target,
    [string]$Engine = "",
    [switch]$Refresh
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$targetFull = [IO.Path]::GetFullPath($Target)
if ($targetFull.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "The backup folder must be outside the repository: $targetFull" }
if ($Engine -eq "") {
    $Engine = @((Join-Path $repo "engine\bin\x64\Debug\engine.exe"), (Join-Path $repo "engine\bin\x64\Release\engine.exe")) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Engine -or -not (Test-Path $Engine)) { throw "engine.exe not found; build the engine or pass -Engine." }
$env:JOYPRO_DEVICE_KB = Join-Path $repo "EngineOnly_Runtime\DB\Devices"

New-Item -ItemType Directory -Force $targetFull | Out-Null
$manifestFile = Join-Path $targetFull "manifest.json"
$manifest = @{}
if (Test-Path $manifestFile) {
    # Windows PowerShell 5.1 hands a JSON array over as one object: enumerate it explicitly.
    foreach ($entry in @((Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ })) {
        # One entry per model and address: makers reuse one picture for several models (a grip sold alone and in a set).
        if ($entry.url) { $manifest[[string]$entry.model + "|" + [string]$entry.url] = $entry }
    }
}

$models = & $Engine devices reference photos | ConvertFrom-Json
# The same request as JoyPro's own download (no Accept header, so the server sends the same bytes and the hash matches).
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(30)
$http.DefaultRequestHeaders.UserAgent.TryParseAdd("JoyPro2") | Out-Null
$ok = 0; $failed = 0; $skipped = 0
foreach ($model in $models) {
    foreach ($photo in @($model.Photos)) {
        if (-not $photo.Official) { continue }
        $mkey = $model.Id + "|" + $photo.Url
        if (-not $Refresh -and $manifest.ContainsKey($mkey) -and (Test-Path (Join-Path $targetFull $manifest[$mkey].file))) { $skipped++; continue }
        try {
            $response = $http.GetAsync($photo.Url).GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) { throw "HTTP " + [int]$response.StatusCode }
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
            $type = [string]$response.Content.Headers.ContentType.MediaType
            $ext = switch -Wildcard ($type) { "*png" { ".png" } "*webp" { ".webp" } "*gif" { ".gif" } default { ".jpg" } }
            $folder = Join-Path $targetFull $model.Id
            New-Item -ItemType Directory -Force $folder | Out-Null
            $name = $photo.View + "-" + $sha.Substring(0, 12) + $ext
            [IO.File]::WriteAllBytes((Join-Path $folder $name), $bytes)
            $manifest[$mkey] = [PSCustomObject]@{
                model = $model.Id; view = $photo.View; url = $photo.Url; page = $photo.Page
                date = (Get-Date).ToUniversalTime().ToString("o"); sha256 = $sha; bytes = $bytes.Length
                recordedSha256 = $photo.Sha256; file = ($model.Id + "\" + $name)
            }
            if ($photo.Sha256 -and $photo.Sha256 -ne $sha) { Write-Warning ("Changed photo: " + $model.Id + " " + $photo.Url + " (recorded " + $photo.Sha256.Substring(0, 12) + ", now " + $sha.Substring(0, 12) + ")") }
            $ok++
        }
        catch {
            Write-Warning ("Failed: " + $model.Id + " " + $photo.Url + ": " + $_.Exception.Message)
            $failed++
        }
    }
}
$manifest.Values | Sort-Object model, view | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestFile -Encoding UTF8
Write-Host "Device photo backup: $ok downloaded, $skipped already there, $failed failed -> $targetFull"
