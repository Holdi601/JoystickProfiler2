# Builds the device photo list of a release (docs/device-reference.md "The photo list in the releases"): the files of
# EngineOnly_Runtime\DB\DevicePhotos (the photo addresses with their SHA-256, _sources.json with the list's "version")
# plus _hosts.json (the makers' hosts of every catalog model, from DB\Devices) as one zip, and manifest.json
# ("joypro-device-photos": version, tag, asset, size, sha256, built, photos). JoyPro downloads it from the release
# "joypro-device-photos" (never marked latest) at most once a day and uses it instead of its shipped list when the
# version is higher. This script only writes the output folder and uploads nothing.
#
#   powershell -File tools\build_device_photo_list.ps1 [-Out <folder>] [-Tag joypro-device-photos]
#
# Before a build raise "version" in DB\DevicePhotos\_sources.json (a list with the version of the shipped one or lower
# is ignored by the app). Default output: <JOYPRO_RELEASE_DIR>\device-photos. The output must be outside the repository.
param(
    [string]$Out = "",
    [string]$Tag = "joypro-device-photos"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
if ($Out -eq "") {
    if (-not $env:JOYPRO_RELEASE_DIR) { throw "Pass -Out or set JOYPRO_RELEASE_DIR." }
    $Out = Join-Path $env:JOYPRO_RELEASE_DIR "device-photos"
}
$outFull = [IO.Path]::GetFullPath($Out)
if ($outFull.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "The output folder must be outside the repository: $outFull" }
$photos = Join-Path $repo "EngineOnly_Runtime\DB\DevicePhotos"
$devices = Join-Path $repo "EngineOnly_Runtime\DB\Devices"
$sources = Get-Content (Join-Path $photos "_sources.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [int]$sources.version
if ($version -le 0) { throw "_sources.json has no list version (""version"")." }

# The makers' hosts per model: the vendor file's officialHosts and the entry's own.
$hosts = [ordered]@{}
foreach ($file in Get-ChildItem $devices -Filter *.json | Sort-Object Name) {
    $data = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $fileHosts = @($data.officialHosts | Where-Object { $_ })
    foreach ($device in @($data.devices)) {
        if (-not $device.id) { continue }
        $all = @($fileHosts + @($device.officialHosts | Where-Object { $_ })) | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } | Select-Object -Unique
        if ($all.Count -gt 0) { $hosts[[string]$device.id] = @($all) }
    }
}

New-Item -ItemType Directory -Force $outFull | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ("joypro-photo-list-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    $count = 0
    foreach ($file in Get-ChildItem $photos -Filter *.json) {
        Copy-Item $file.FullName (Join-Path $stage $file.Name)
        if (-not $file.Name.StartsWith("_")) {
            $count += @((Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json).photos).Count
        }
    }
    $hostsJson = [ordered]@{ format = "joypro-photo-hosts"; models = $hosts } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $stage "_hosts.json"), $hostsJson, (New-Object Text.UTF8Encoding $false))

    $asset = "device-photos-v$version.zip"
    $zip = Join-Path $outFull $asset
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item $zip).Length
    $manifest = [ordered]@{
        format = "joypro-device-photos"; version = $version; tag = $Tag; asset = $asset; size = $size; sha256 = $sha
        built = (Get-Date).ToUniversalTime().ToString("o"); photos = $count
    }
    [IO.File]::WriteAllText((Join-Path $outFull "manifest.json"), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    # Older zips of the folder are not part of the release any more.
    Get-ChildItem $outFull -Filter "device-photos-v*.zip" | Where-Object { $_.Name -ne $asset } | Remove-Item
    Write-Host ("Device photo list v{0}: {1} photos, {2:N0} KB -> {3}" -f $version, $count, ($size / 1KB), $outFull)
    Write-Host ("Upload {0} and manifest.json to the release '{1}' (gh release upload {1} ... --clobber; keep it --latest=false)." -f $asset, $Tag)
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
