# Builds the 3D view packs of a release (docs/device-reference.md "3D view"): one zip per device model from a folder of
# turntables as tools/device_drawings.py turntable makes them (<Source>\<model>\turntable.json + frames\*.png), plus
# manifest.json. The packs are release assets (GitHub release "device-3d-v1", published with --latest=false), never part
# of the repository or of JoyPro's package; this script only writes the output folder and uploads nothing.
#
#   powershell -File tools\build_device_3d_packs.ps1 [-Source <turntables>] [-Out <folder>] [-Model <id>,...] [-Force]
#
# Default source: JOYPRO_DEVICE_3D_DIR; default output: <JOYPRO_RELEASE_DIR>\device-3d. Both must be outside the
# repository. Per pack: turntable.json, frames\*.png, CREDITS.txt (the source of the 3D model and its licence, from
# turntable.json "source" and "licence"). manifest.json lists per pack: model, asset, size, sha256, version, built,
# source, sourceHash. A model whose files did not change keeps its zip and version; a changed one gets the next version
# (JoyPro updates installed packs to a newer version). Every model must be in the catalog.
param(
    [string]$Source = "",
    [string]$Out = "",
    [string[]]$Model = @(),
    [string]$Tag = "device-3d-v1",
    [switch]$Force
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$repo = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$repoRoot = $repo
$commonDir = (& git -C $repo rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($LASTEXITCODE -eq 0 -and $commonDir) { $repoRoot = Split-Path -Parent ([IO.Path]::GetFullPath($commonDir.Trim())) }
if ($Source -eq "") { $Source = $env:JOYPRO_DEVICE_3D_DIR }
if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path $Source)) { throw "Give -Source or set JOYPRO_DEVICE_3D_DIR (the turntables made by tools/device_drawings.py turntable)." }
$Source = (Resolve-Path $Source).Path
if ($Out -eq "") {
    if ([string]::IsNullOrWhiteSpace($env:JOYPRO_RELEASE_DIR)) { throw "Give -Out or set JOYPRO_RELEASE_DIR (the folder that holds the release folders)." }
    $Out = Join-Path $env:JOYPRO_RELEASE_DIR "device-3d"
}
$outFull = [IO.Path]::GetFullPath($Out)
foreach ($p in @($outFull, $Source)) { if ($p.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Must be outside the repository: $p" } }
if ($Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "Not a release tag: $Tag" }
New-Item -ItemType Directory -Force $outFull | Out-Null
$Model = @($Model | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })

# The catalog's model ids.
$ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($f in Get-ChildItem (Join-Path $repo "EngineOnly_Runtime\DB\Devices") -Filter *.json) {
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($f.FullName), '"id"\s*:\s*"([a-z0-9][a-z0-9._-]*)"')) { [void]$ids.Add($m.Groups[1].Value) }
}

function Sha256([string]$file) {
    $s = [IO.File]::OpenRead($file)
    try { return ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)) -replace "-", "").ToLowerInvariant() } finally { $s.Dispose() }
}
function Json-Text([string]$s) { return (ConvertTo-Json -Compress -InputObject $s) }

$manifestFile = Join-Path $outFull "manifest.json"
$previous = @{}
if (Test-Path $manifestFile) {
    foreach ($p in @((Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json).packs)) { $previous[$p.model] = $p }
}
$now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$packs = New-Object System.Collections.ArrayList
$built = 0; $reused = 0
foreach ($dir in Get-ChildItem $Source -Directory | Sort-Object Name) {
    $id = $dir.Name
    if ($id.StartsWith(".")) { continue }
    if ($Model.Count -gt 0 -and -not ($Model -contains $id)) { continue }
    $ttFile = Join-Path $dir.FullName "turntable.json"
    if (-not (Test-Path $ttFile)) { Write-Warning "$($id): no turntable.json, left out."; continue }
    if ($id -notmatch '^[a-z0-9][a-z0-9._-]*$' -or -not $ids.Contains($id)) { throw "$($id): not a model of the catalog." }
    $tt = Get-Content $ttFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $frames = @($tt.frames | ForEach-Object { $_.file })
    foreach ($fr in $frames) {
        if ($fr -notmatch '^frames/[A-Za-z0-9][A-Za-z0-9._-]*\.png$') { throw "$($id): frame $fr is not frames/<name>.png" }
        if (-not (Test-Path (Join-Path $dir.FullName $fr))) { throw "$($id): frame $fr is missing" }
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append((Sha256 $ttFile))
    foreach ($fr in ($frames | Sort-Object)) { [void]$sb.Append("|" + $fr + ":" + (Get-Item (Join-Path $dir.FullName $fr)).Length) }
    $hash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))) -replace "-", "").ToLowerInvariant()
    $asset = "$id.zip"
    $zip = Join-Path $outFull $asset
    $prev = $previous[$id]
    if (-not $Force -and $prev -and $prev.sourceHash -eq $hash -and (Test-Path $zip) -and ((Sha256 $zip) -eq $prev.sha256)) {
        $version = [int]$prev.version; $packBuilt = $prev.built; $reused++
    }
    else {
        $version = if ($prev) { [int]$prev.version + 1 } else { 1 }
        $packBuilt = $now
        $source = [string]$tt.source
        $licence = [string]$tt.licence
        $credits = @(
            "3D view of the device model $id, rendered by JoyPro.",
            "",
            "Source: $(if ($source) { $source } else { 'JoyPro' })",
            "$(if ($licence) { "Licence of the 3D model: $licence" } else { 'The 3D model and the names belong to their respective owners.' })",
            "Pack version $version, built $packBuilt.",
            "",
            "JoyPro shows these pictures as a reference for setting up your controls. They are not part of JoyPro's source code",
            "and are not covered by JoyPro's license."
        ) -join "`r`n"
        $tmp = "$zip.tmp"
        if (Test-Path $tmp) { Remove-Item -Force $tmp }
        $archive = [IO.Compression.ZipFile]::Open($tmp, [IO.Compression.ZipArchiveMode]::Create)
        try {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $ttFile, "turntable.json", [IO.Compression.CompressionLevel]::Optimal)
            $entry = $archive.CreateEntry("CREDITS.txt")
            $w = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
            $w.Write($credits + "`r`n"); $w.Dispose()
            foreach ($fr in ($frames | Sort-Object -Unique)) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $dir.FullName $fr), $fr, [IO.Compression.CompressionLevel]::NoCompression)
            }
        }
        finally { $archive.Dispose() }
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Move-Item $tmp $zip
        $built++
    }
    [void]$packs.Add([pscustomobject]@{ model = $id; asset = $asset; size = (Get-Item $zip).Length; sha256 = (Sha256 $zip); version = $version; built = $packBuilt; source = [string]$tt.source; sourceHash = $hash })
}
if ($Model.Count -gt 0) {
    # A partial build keeps the other packs of the previous manifest.
    foreach ($p in $previous.Values) { if (-not ($packs | Where-Object { $_.model -eq $p.model })) { [void]$packs.Add($p) } }
}
$lines = @($packs | Sort-Object model | ForEach-Object {
    "    {`"model`":$(Json-Text $_.model),`"asset`":$(Json-Text $_.asset),`"size`":$($_.size),`"sha256`":`"$($_.sha256)`",`"version`":$($_.version),`"built`":$(Json-Text $_.built),`"source`":$(Json-Text $_.source),`"sourceHash`":`"$($_.sourceHash)`"}"
})
$manifest = "{`r`n  `"format`": `"joypro-device-3d-packs`",`r`n  `"packVersion`": 1,`r`n  `"tag`": $(Json-Text $Tag),`r`n  `"built`": `"$now`",`r`n  `"packs`": [`r`n" + ($lines -join ",`r`n") + "`r`n  ]`r`n}`r`n"
[IO.File]::WriteAllText($manifestFile, $manifest, (New-Object System.Text.UTF8Encoding($false)))
$referenced = @($packs | ForEach-Object { $_.asset })
foreach ($s in @(Get-ChildItem $outFull -Filter *.zip | Where-Object { $referenced -notcontains $_.Name })) { Write-Warning "Not in the manifest (do not upload): $($s.FullName)" }
$total = ($packs | Measure-Object -Property size -Sum).Sum
Write-Host ("{0} packs ({1} built, {2} unchanged), {3:N1} MB in {4}" -f $packs.Count, $built, $reused, ($total / 1MB), $outFull)
Write-Host "Publish (only when asked): gh release create $Tag --latest=false --title `"3D device views`" <zips> manifest.json"
