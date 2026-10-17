# Adds reference photos of a device model to the photo database (EngineOnly_Runtime\DB\DevicePhotos, docs/device-reference.md
# "Photo references"). Only the photo's web address is stored, never the photo. The address must be on one of the
# vendor's official hosts (the engine refuses others).
#
#   One photo:      tools\add_device_photos.ps1 -Model vkb-gunfighter-mcg-pro -Url https://vkbsimcontrollers.com/cdn/shop/files/x.webp -View side
#   A shop gallery: tools\add_device_photos.ps1 -Model vkb-gunfighter-mcg-pro -ProductUrl https://vkbsimcontrollers.com/products/<handle>
#                   (Shopify shops: every gallery photo, tagged gallery-1, gallery-2 ... until someone tags the views)
#
# Then look at the photos and set their views (front, side, back, top, base, three-quarter, sheet), and place curated
# marker positions with the marker assistant ("Share layout..." writes a joypro-device-layout file for DB\DeviceLayouts).
param(
    [Parameter(Mandatory = $true)] [string]$Model,
    [string]$Url = "",
    [string]$ProductUrl = "",
    [string]$View = "front",
    [string]$Page = "",
    [string]$Confidence = "medium",
    [string]$Shows = "",
    [string]$Sha256 = "",
    [string]$Engine = "",
    [switch]$IncludeOtherLanguages
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ($Engine -eq "") {
    $Engine = @(
        (Join-Path $repo "engine\bin\x64\Debug\engine.exe"),
        (Join-Path $repo "engine\bin\x64\Release\engine.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Engine -or -not (Test-Path $Engine)) { throw "engine.exe not found; build the engine or pass -Engine." }
# The engine writes next to the catalog it reads: point it at the repository's catalog.
$env:JOYPRO_DEVICE_KB = Join-Path $repo "EngineOnly_Runtime\DB\Devices"

function Add-Photo([string]$photoUrl, [string]$photoView, [string]$photoPage) {
    $arguments = @("devices", "reference", "photo-add", "--model", $Model, "--url", $photoUrl, "--view", $photoView, "--confidence", $Confidence)
    if ($photoPage -ne "") { $arguments += @("--page", $photoPage) }
    if ($Shows -ne "") { $arguments += @("--shows", $Shows) }
    if ($Sha256 -ne "") { $arguments += @("--sha256", $Sha256) }
    & $Engine @arguments
    if ($LASTEXITCODE -ne 0) { Write-Warning "Not added: $photoUrl" }
}

if ($Url -ne "") {
    Add-Photo $Url $View $(if ($Page -ne "") { $Page } else { $ProductUrl })
    return
}
if ($ProductUrl -eq "") { throw "Give -Url (one photo) or -ProductUrl (a shop's product page)." }

$uri = [Uri]$ProductUrl
if ($ProductUrl -notmatch "/products/") { throw "Only Shopify product pages (.../products/<handle>) are read automatically; add other photos one by one with -Url." }
$json = Invoke-RestMethod -Uri ($ProductUrl.TrimEnd('/') + ".json") -UserAgent "JoyPro2 maintainer tool"
$n = 0
foreach ($image in $json.product.images) {
    $src = [string]$image.src
    $file = ([Uri]$src).Segments[-1]
    if (-not $IncludeOtherLanguages -and $file -match "-(RU|DE|FR|CN|JP)_") { continue }
    # The shop's own address of the same file (cdn.shopify.com is the shop's CDN, the shop host is the official one).
    $official = $uri.Scheme + "://" + $uri.Host + "/cdn/shop/files/" + $file
    $n++
    Add-Photo $official ("gallery-" + $n) $ProductUrl
}
Write-Host "$n photo reference(s) for $Model. Tag their views (front, side, back, top, base, three-quarter) after looking at them."
