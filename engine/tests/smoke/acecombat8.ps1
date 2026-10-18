# ACE COMBAT 8 as a virtual-only (Xbox pad) game (docs/games/ace-combat.md). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). Never touches the installed game: a synthetic System.sav
# (tests\fixtures\acecombat8, built like the game's GVAS with the gamepad keys and a checksum; missile and target
# swapped), fake vJoy/ViGEm/HidHide, a temp JoyPro data folder.
#  1. ac8 refresh-db: the fixed sticks from JoyPro's table, the mappable keys from the save (the swapped ones too).
#  2. vjoy mode AceCombat8 xbox: pitch/roll on the left stick, the throttle relation on both triggers (centre split),
#     the missile button on the key the player set.
#  3. export writes nothing (checksummed settings) and says so; the save stays byte-identical.
$acDir = Join-Path $tempDir "acecombat8"
$acFake = Join-Path $acDir "fake"
New-Item -ItemType Directory -Force -Path $acFake | Out-Null
# refresh-db writes into the game-data folder (<data>\GameData), never into the program folder.
$acCatalogDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\AceCombat8"
$acCatalogBackup = Join-Path $acDir "catalog-backup"
$acHadCatalog = Test-Path $acCatalogDir
if ($acHadCatalog) {
    Copy-Item -Path $acCatalogDir -Destination $acCatalogBackup -Recurse -Force
}
[System.IO.File]::WriteAllText((Join-Path $acFake "vjoy.txt"), "installed=2.1.9.1`r`nvigem=1.22.0`r`n")
[System.IO.File]::WriteAllText((Join-Path $acFake "hidhide-state.txt"), "--cloak-off`r`n--inv-on`r`n")
$acPrevious = @{ Fake = $env:JOYPRO_VJOY_FAKE; Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION }
$env:JOYPRO_VJOY_FAKE = $acFake
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$acStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$acThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$acProfile = Join-Path $acDir "ac8.pr0file"
$acSave = Join-Path $acDir "System.sav"
Copy-Item (Join-Path $engineDir "tests\fixtures\acecombat8\System.sav") $acSave
try {
    # ---- 1. catalog
    $acRefresh = Run-Engine @("ac8", "refresh-db", $acSave, "--text")
    Assert-True ($acRefresh.ExitCode -eq 0) "ac8 refresh-db failed. Output: $($acRefresh.Output)"
    Assert-Match $acRefresh.Output "Read 9 gamepad key\(s\) from .*control type Expert" "The gamepad keys and the control type must be read from System.sav."
    $acCatalog = [System.IO.File]::ReadAllText((Join-Path $acCatalogDir "actions.tsv"))
    Assert-Match $acCatalog "(?m)^Pad_Pitch\t[^\t]+\taxis\tGamepad\tGamepad_LeftY" "Pitch sits on the left stick Y."
    Assert-Match $acCatalog "(?m)^Pad_FireMissile\t[^\t]+\tbutton\tGamepad\tGamepad_FaceButton_Top" "The missile key the player set (Y) must be read."
    Assert-Match $acCatalog "(?m)^Pad_ThrottleUp\t[^\t]+\taxis\tGamepad\tGamepad_RightTriggerAxis" "Throttle up sits on the right trigger."

    # ---- 2. the virtual Xbox pad follows the game's keys
    Run-Engine @("--profile", $acProfile, "relations", "add", "Throttle", "--axis", "--node", "AceCombat8:Pad_ThrottleUp", "--device", $acThrottle, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $acProfile, "relations", "item", "add", "Throttle", "--node", "AceCombat8:Pad_ThrottleDown", "--text") | Out-Null
    Run-Engine @("--profile", $acProfile, "relations", "add", "Pitch", "--axis", "--node", "AceCombat8:Pad_Pitch", "--device", $acStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $acProfile, "relations", "add", "Roll", "--axis", "--node", "AceCombat8:Pad_Roll", "--device", $acStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $acProfile, "relations", "add", "Missile", "--button", "--node", "AceCombat8:Pad_FireMissile", "--device", $acStick, "--input", "JOY_BTN2", "--text") | Out-Null
    Run-Engine @("--profile", $acProfile, "relations", "add", "Gun", "--button", "--node", "AceCombat8:Pad_FireMachineGun", "--device", $acStick, "--input", "JOY_BTN1", "--text") | Out-Null
    $acMode = Run-Engine @("--profile", $acProfile, "vjoy", "mode", "AceCombat8", "xbox", "--text")
    Assert-True ($acMode.ExitCode -eq 0) "vjoy mode AceCombat8 xbox failed. Output: $($acMode.Output)"
    Assert-Match $acMode.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_Z -> Xbox XB_TRIGGERS" "The throttle on up + down must become the centre-split triggers."
    Assert-Match $acMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Y -> Xbox XB_LY \(Left stick Y\)" "Pitch on the left stick Y."
    Assert-Match $acMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_X -> Xbox XB_LX \(Left stick X\)" "Roll on the left stick X."
    Assert-Match $acMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_BTN2 -> Xbox XB_Y \(Y\)" "The missile on the key the player set (Y)."
    Assert-Match $acMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_BTN1 -> Xbox XB_A \(A\)" "The gun on A."

    # ---- 3. nothing is written
    $acOriginal = [System.IO.File]::ReadAllBytes($acSave)
    $acExport = Run-Engine @("--profile", $acProfile, "ac8", "export", $acSave, "--text")
    Assert-True ($acExport.ExitCode -eq 0) "ac8 export failed. Output: $($acExport.Output)"
    Assert-Match $acExport.Output "nothing is written \(the game's settings are checksummed" "The export must say that nothing is written."
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$acOriginal, [byte[]][System.IO.File]::ReadAllBytes($acSave))) "System.sav must stay byte-identical."
    $acStatus = Run-Engine @("--profile", $acProfile, "vjoy", "mode", "AceCombat8", "native", "--text")
    Assert-Match $acStatus.Output "one-device game: .*Suggested: engine vjoy mode AceCombat8 xbox" "Native mode must suggest the Xbox pad."
}
finally {
    $env:JOYPRO_VJOY_FAKE = $acPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $acPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $acPrevious.Detection
    if (Test-Path $acCatalogDir) {
        Remove-Item -Path $acCatalogDir -Recurse -Force
    }
    if ($acHadCatalog) {
        Copy-Item -Path $acCatalogBackup -Destination $acCatalogDir -Recurse -Force
    }
}
