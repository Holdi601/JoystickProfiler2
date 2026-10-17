# What a JoyPro release may contain (THIRD_PARTY_NOTICES.md, section 1.4): JoyPro's own data only. Game data (command lists,
# DCS clean profiles, DCS UI screenshots, layout pictures of other people) is built on the user's PC from the user's
# own game installation and never shipped. Used by tools/publish_release.ps1, tools/build_local.ps1 -ReleaseData and
# tools/export_public.ps1.

# Folders of <engine>\DB that are JoyPro's own work and ship with JoyPro. DevicePhotos holds only the web addresses of
# the makers' photos (JoyPro fetches them on the user's PC), DeviceLayouts only JoyPro's marker positions, DeviceDrawings
# JoyPro's own line drawings of the devices (vector path data), Cockpits JoyPro's cockpit database (drawings as vector
# data, photos only as addresses): no photo.
$script:ShippedDbFolders = @("Functions", "Devices", "Llm", "DevicePhotos", "DeviceLayouts", "DeviceDrawings", "Cockpits", "Detents")

# Pictures never ship in DB (a maker's photo or a user's picture that slipped into the catalog folders).
$script:PictureExtensions = @(".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".img", ".tif", ".tiff")

# TOOLS\Conversions ships: JoyPro's own tables (keyboard conversions, X4/Elite key tables, the measured Battlefield
# joystick numbering <GameId>JoystickMap.tsv). Never put such JoyPro data into a game DB folder: those are removed.

# Top-level folders and files of <engine> that never ship: game data and build leftovers.
$script:ExcludedEngineItems = @("CleanProfile", "KeyboardCleanProfile", "Clicks", "LayoutTemplates", "Requirements")

# Removes (or with -DryRun only lists) everything in <engineDir> that is not JoyPro's own. Returns the removed items
# (relative to the engine folder) and the kept DB folders.
function Remove-JoyProGameData {
    param(
        [Parameter(Mandatory = $true)] [string]$EngineDir,
        [switch]$DryRun
    )
    $removed = New-Object System.Collections.Generic.List[string]
    $doomed = New-Object System.Collections.Generic.List[System.IO.FileSystemInfo]
    foreach ($item in @(Get-ChildItem -Path $EngineDir -Force -ErrorAction SilentlyContinue)) {
        if ($ExcludedEngineItems -contains $item.Name -or $item.Name -like "tmp_*" -or $item.Extension -eq ".pdb") {
            $doomed.Add($item)
        }
    }
    $db = Join-Path $EngineDir "DB"
    foreach ($folder in @(Get-ChildItem -Path $db -Force -ErrorAction SilentlyContinue)) {
        if (-not ($folder.PSIsContainer -and $ShippedDbFolders -contains $folder.Name)) {
            $doomed.Add($folder)
        }
        elseif ($folder.PSIsContainer) {
            foreach ($picture in @(Get-ChildItem -Path $folder.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $PictureExtensions -contains $_.Extension.ToLowerInvariant() })) {
                $doomed.Add($picture)
            }
        }
    }
    foreach ($item in $doomed) {
        $relative = $item.FullName.Substring($EngineDir.TrimEnd('\').Length + 1)
        $count = if ($item.PSIsContainer) { @(Get-ChildItem -Path $item.FullName -Recurse -File -Force -ErrorAction SilentlyContinue).Count } else { 1 }
        $removed.Add($relative + " (" + $count + " file" + $(if ($count -eq 1) { "" } else { "s" }) + ")")
        if (-not $DryRun) {
            Remove-Item -Recurse -Force $item.FullName
        }
    }
    $kept = @(Get-ChildItem -Path $db -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $ShippedDbFolders -contains $_.Name } | ForEach-Object { "DB\" + $_.Name })
    return [PSCustomObject]@{ Removed = $removed.ToArray(); KeptDb = $kept }
}

# Throws when a release folder still holds game data.
function Assert-NoGameData {
    param([Parameter(Mandatory = $true)] [string]$EngineDir)
    $left = @()
    foreach ($name in $ExcludedEngineItems) {
        if (Test-Path (Join-Path $EngineDir $name)) { $left += $name }
    }
    foreach ($folder in @(Get-ChildItem -Path (Join-Path $EngineDir "DB") -Directory -Force -ErrorAction SilentlyContinue)) {
        if ($ShippedDbFolders -notcontains $folder.Name) { $left += "DB\" + $folder.Name }
        else {
            foreach ($picture in @(Get-ChildItem -Path $folder.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $PictureExtensions -contains $_.Extension.ToLowerInvariant() })) {
                $left += "DB\" + $folder.Name + "\" + $picture.Name
            }
        }
    }
    if ($left.Count -gt 0) {
        throw "The release still contains game data: " + ($left -join ", ")
    }
}

# The DB folders and picture extensions above, for tools/export_public.ps1 (the public repository holds the same
# JoyPro-own data as a release).
function Get-JoyProShippedDbFolders { return $ShippedDbFolders }
function Get-JoyProPictureExtensions { return $PictureExtensions }

Export-ModuleMember -Function Remove-JoyProGameData, Assert-NoGameData, Get-JoyProShippedDbFolders, Get-JoyProPictureExtensions
