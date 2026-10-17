# ACE COMBAT 7 (native, several devices) on a synthetic Input.ini (docs/games/ace-combat.md). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). The game is not installed on the
# development PC; the fixture follows the published samples (one section per device model, Flight_* keys).
#  1. ac7 refresh-db: the Flight_* names from the file, axes and buttons.
#  2. scan: simple values ("Y:R", "Button1", "POV_U1") per device; a combination is reported, not read.
#  3. export: values per device section, Invert as ":R", a hat direction as POV_<dir><n>; the axis is cleared on the
#     other device sections (the game averages), comments and CRLF line ends stay; a second export changes nothing;
#     merge keeps a value the game already has.
$a7Dir = Join-Path $tempDir "acecombat7"
New-Item -ItemType Directory -Force -Path $a7Dir | Out-Null
# refresh-db writes into the game-data folder (<data>\GameData), never into the program folder.
$a7CatalogDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\AceCombat7"
$a7CatalogBackup = Join-Path $a7Dir "catalog-backup"
$a7HadCatalog = Test-Path $a7CatalogDir
if ($a7HadCatalog) {
    Copy-Item -Path $a7CatalogDir -Destination $a7CatalogBackup -Recurse -Force
}
$a7Previous = @{ Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION }
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$a7Stick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$a7Throttle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$a7Profile = Join-Path $a7Dir "ac7.pr0file"
$a7Ini = Join-Path $a7Dir "Input.ini"
Copy-Item (Join-Path $engineDir "tests\fixtures\acecombat7\Input.ini") $a7Ini
try {
    # ---- 1. catalog
    $a7Refresh = Run-Engine @("ac7", "refresh-db", $a7Ini, "--text")
    Assert-True ($a7Refresh.ExitCode -eq 0) "ac7 refresh-db failed. Output: $($a7Refresh.Output)"
    $a7Catalog = [System.IO.File]::ReadAllText((Join-Path $a7CatalogDir "actions.tsv"))
    Assert-Match $a7Catalog "(?m)^Flight_Pitch\tPitch\taxis\tFlight axes" "Pitch must be an axis."
    Assert-Match $a7Catalog "(?m)^Flight_Gun\tFire gun\tbutton\tFlight" "The gun must be a button."
    Assert-NotMatch $a7Catalog "UI_A" "Menu keys are not flight actions."

    # ---- 2. scan
    $a7Scan = Run-Engine @("--profile", $a7Profile, "ac7", "scan", $a7Ini, "--text")
    Assert-Match $a7Scan.Output "Flight_Pitch <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Y" "Y:R must read as JOY_Y."
    Assert-Match $a7Scan.Output "Flight_HatSwitchUp <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_BTN_POV1_U" "POV_U1 must read as hat 1 up."
    Assert-Match $a7Scan.Output "Flight_Yaw=Button4 \+ Button6:R on VJ Throttle .*combination" "A combination must be reported."
    Assert-Match $a7Scan.Output "Joystick - HOTAS Warthog\): no such device is connected" "A section of a missing device must be reported."

    # ---- 3. export
    Run-Engine @("--profile", $a7Profile, "relations", "add", "Pitch", "--axis", "--node", "AceCombat7:Flight_Pitch", "--device", $a7Stick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $a7Profile, "binds", "set", "Pitch", "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $a7Profile, "relations", "add", "Yaw", "--axis", "--node", "AceCombat7:Flight_Yaw", "--device", $a7Throttle, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $a7Profile, "relations", "add", "Missile", "--button", "--node", "AceCombat7:Flight_Missile", "--device", $a7Throttle, "--input", "JOY_BTN5", "--text") | Out-Null
    Run-Engine @("--profile", $a7Profile, "relations", "add", "Flare", "--button", "--node", "AceCombat7:Flight_Flare", "--device", $a7Stick, "--input", "JOY_BTN_POV1_D", "--text") | Out-Null
    $a7Export = Run-Engine @("--profile", $a7Profile, "ac7", "export", $a7Ini, "--text")
    Assert-True ($a7Export.ExitCode -eq 0) "ac7 export failed. Output: $($a7Export.Output)"
    $a7Text = [System.IO.File]::ReadAllText($a7Ini)
    $a7Stick7 = [regex]::Match($a7Text, "(?s)\[Joystick-80CB3344-0000-0000-0000-504944564944\](.*?)(\r\n\[|$)").Groups[1].Value
    $a7Thr7 = [regex]::Match($a7Text, "(?s)\[Joystick-205403EB-0000-0000-0000-504944564944\](.*?)(\r\n\[|$)").Groups[1].Value
    $a7Hog = [regex]::Match($a7Text, "(?s)\[Joystick-0402044F-0000-0000-0000-504944564944\](.*?)(\r\n\[|$)").Groups[1].Value
    Assert-Match $a7Stick7 "\r\nFlight_Pitch=Y:R\r\n" "Inverted pitch must be Y:R."
    Assert-Match $a7Stick7 "\r\nFlight_Flare=POV_D1\r\n" "The flare must be hat 1 down."
    Assert-Match $a7Thr7 "\r\nFlight_Yaw=Rz\r\n" "Yaw must replace the combination with Rz (overwrite)."
    Assert-Match $a7Thr7 "\r\nFlight_Missile=Button5\r\n" "The missile must be Button5."
    Assert-Match $a7Hog "\r\nFlight_Pitch=\r\n" "The pitch axis must be cleared on the other device (the game averages)."
    Assert-Match $a7Hog "\r\nFlight_Roll=X\r\n" "Axes JoyPro does not write stay on the other device."
    Assert-Match $a7Text "^; Synthetic Input.ini" "Comments must stay."
    Assert-Match $a7Text "EnableJoystick=True\r\n" "The JoystickSetting section must stay (CRLF kept)."
    Assert-Match $a7Export.Output "Flight_Pitch cleared on \[Joystick-0402044F" "The cleared axis must be reported."
    $a7Again = Run-Engine @("--profile", $a7Profile, "ac7", "export", $a7Ini, "--text")
    Assert-Match $a7Again.Output "already up to date" "A second export must change nothing."

    Copy-Item (Join-Path $engineDir "tests\fixtures\acecombat7\Input.ini") $a7Ini -Force
    $a7Merge = Run-Engine @("--profile", $a7Profile, "ac7", "export", $a7Ini, "--mode", "merge", "--text")
    Assert-Match $a7Merge.Output "already has Flight_Yaw=Button4 \+ Button6:R on this device \(merge keeps it\)" "Merge must keep the game's own value."
    Assert-Match ([System.IO.File]::ReadAllText($a7Ini)) "(?s)\[Joystick-0402044F[^\[]*Flight_Pitch=Y:R" "Merge must not clear other devices."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $a7Previous.Devices
    $env:JOYPRO_INSTALL_DETECTION = $a7Previous.Detection
    if (Test-Path $a7CatalogDir) {
        Remove-Item -Path $a7CatalogDir -Recurse -Force
    }
    if ($a7HadCatalog) {
        Copy-Item -Path $a7CatalogBackup -Destination $a7CatalogDir -Recurse -Force
    }
}
