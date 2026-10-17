# Private backup of every photo the cockpit database links (docs/cockpits.md, "Photos"). The release ships no photo:
# the GUI fetches them on the user's PC after the user's yes. This copy is only for the maintainer, so that a link that
# dies later can be recovered.
#
#   tools\backup_cockpit_photos.ps1 [-Target <a PRIVATE folder outside the repository>] [-Refresh] [-Engine <engine.exe>]
#
# Default target: ..\JoyPro2-private\cockpit-photos next to the repository's parent folder (the maintainer's private
# folder). For each photo with an image address: the file (<aircraft>\<view>-<sha12>.<ext>) and a line in
# manifest.json: aircraft, view, variant, URL, source page, credit, license, date, SHA-256, size. Page-only links (the
# fictional craft) are listed in manifest.json without a file. Nothing in the build or release reads the folder.
param(
    [string]$Target = "",
    [string]$Engine = "",
    [switch]$Refresh
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
if ($Target -eq "") {
    # The repository lives in ...\JoyPro\JoystickProfiler2 (or a worktree below it): the private folder is ...\JoyPro\JoyPro2-private.
    $walk = Get-Item $repo
    while ($walk -and -not (Test-Path (Join-Path $walk.FullName "JoyPro2-private"))) { $walk = $walk.Parent }
    if (-not $walk) { throw "No JoyPro2-private folder found above the repository; pass -Target." }
    $Target = Join-Path $walk.FullName "JoyPro2-private\cockpit-photos"
}
$targetFull = [IO.Path]::GetFullPath($Target)
# The main checkout (also when this runs in a linked git worktree inside it).
$repoRoot = $repo
$commonDir = (& git -C $repo rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($LASTEXITCODE -eq 0 -and $commonDir) { $repoRoot = Split-Path -Parent ([IO.Path]::GetFullPath($commonDir.Trim())) }
if ($targetFull.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "The backup folder must be outside the repository: $targetFull" }
if ($Engine -eq "") {
    $Engine = @((Join-Path $repo "engine\bin\x64\Debug\engine.exe"), (Join-Path $repo "engine\bin\x64\Release\engine.exe")) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Engine -or -not (Test-Path $Engine)) { throw "engine.exe not found; build the engine or pass -Engine." }
$env:JOYPRO_COCKPITS_DB = Join-Path $repo "EngineOnly_Runtime\DB\Cockpits"

New-Item -ItemType Directory -Force $targetFull | Out-Null
$manifestFile = Join-Path $targetFull "manifest.json"
$manifest = @{}
if (Test-Path $manifestFile) {
    foreach ($entry in @((Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ })) {
        $key = if ($entry.url) { [string]$entry.url } else { "page:" + [string]$entry.page }
        $manifest[$key] = $entry
    }
}

$photos = @(& $Engine --status false --benchmark false cockpits photos | ConvertFrom-Json | ForEach-Object { $_ })
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(60)
# Wikimedia asks for a descriptive user agent.
$http.DefaultRequestHeaders.UserAgent.TryParseAdd("JoyPro2-maintainer-backup/1.0 (https://github.com/Holdi601/JoystickProfiler2)") | Out-Null
$ok = 0; $failed = 0; $skipped = 0; $pages = 0
foreach ($photo in $photos) {
    if (-not $photo.Url) {
        $manifest["page:" + $photo.Page] = [PSCustomObject]@{
            aircraft = $photo.Aircraft; view = $photo.View; variant = $photo.Variant; url = ""; page = $photo.Page
            credit = $photo.Credit; license = $photo.License; date = (Get-Date).ToUniversalTime().ToString("o"); file = ""
        }
        $pages++
        continue
    }
    if (-not $Refresh -and $manifest.ContainsKey($photo.Url) -and $manifest[$photo.Url].file -and (Test-Path (Join-Path $targetFull $manifest[$photo.Url].file))) { $skipped++; continue }
    try {
        # Wikimedia answers 429 when asked too fast: wait and try again (longer each time).
        $response = $null
        foreach ($wait in @(0, 10, 30, 90)) {
            if ($wait -gt 0) { Start-Sleep -Seconds $wait }
            $response = $http.GetAsync($photo.Url).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne 429) { break }
        }
        if (-not $response.IsSuccessStatusCode) { throw "HTTP " + [int]$response.StatusCode }
        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
        $type = [string]$response.Content.Headers.ContentType.MediaType
        $ext = switch -Wildcard ($type) { "*png" { ".png" } "*webp" { ".webp" } "*gif" { ".gif" } "*svg*" { ".svg" } "*tiff" { ".tif" } default { ".jpg" } }
        $folder = Join-Path $targetFull $photo.Aircraft
        New-Item -ItemType Directory -Force $folder | Out-Null
        $view = if ($photo.View) { $photo.View } else { "photo" }
        $name = $view + "-" + $sha.Substring(0, 12) + $ext
        [IO.File]::WriteAllBytes((Join-Path $folder $name), $bytes)
        $manifest[$photo.Url] = [PSCustomObject]@{
            aircraft = $photo.Aircraft; view = $photo.View; variant = $photo.Variant; url = $photo.Url; page = $photo.Page
            credit = $photo.Credit; license = $photo.License; date = (Get-Date).ToUniversalTime().ToString("o")
            sha256 = $sha; bytes = $bytes.Length; file = ($photo.Aircraft + "\" + $name)
        }
        $ok++
        # Be polite to the image servers.
        Start-Sleep -Milliseconds 1500
    }
    catch {
        Write-Warning ("Failed: " + $photo.Aircraft + " " + $photo.Url + ": " + $_.Exception.Message)
        $failed++
    }
}
$manifest.Values | Sort-Object aircraft, view | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestFile -Encoding UTF8
Write-Host "Cockpit photo backup: $ok downloaded, $skipped already there, $failed failed, $pages page links -> $targetFull"
