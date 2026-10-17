# Hell Let Loose: Vietnam as a virtual-only game (docs/games/hll-vietnam.md). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). Never touches the installed game: the catalog comes from
# JoyPro's built-in table (JOYPRO_OODLE_DLL=none, no game folder), the layout from a temp GameUserSettings.ini, the
# keyboard keys go into a temp copy of a synthetic EnhancedInputUserSettings.sav, and vJoy/HidHide/ViGEm are fakes.
#  1. hllv refresh-db: built-in table (keyboard pilot keys + gamepad actions with the default key per layout A/B/C).
#  2. vjoy mode HLLVietnam xbox: inputs pinned to the controls of the selected layout (A, then B); the collective
#     relation on Ascend and Descend becomes one centre-split trigger axis; the relation's Invert reverses an axis.
#  3. hllv export: gamepad binds are only noted (fixed layouts); keyboard keys are written with --include-keyboard.
$hlDir = Join-Path $tempDir "hllv"
$hlData = Join-Path $hlDir "data"
$hlFake = Join-Path $hlDir "fake"
New-Item -ItemType Directory -Force -Path $hlData, $hlFake | Out-Null
$hlUtf8 = New-Object System.Text.UTF8Encoding $false
# refresh-db writes into the game-data folder (<data>\GameData), never into the program folder.
$hlCatalogDir = Join-Path $hlData "GameData\DB\HLLVietnam"
$hlCatalogBackup = Join-Path $hlDir "catalog-backup"
$hlHadCatalog = Test-Path $hlCatalogDir
if ($hlHadCatalog) {
    Copy-Item -Path $hlCatalogDir -Destination $hlCatalogBackup -Recurse -Force
}
$hlIni = Join-Path $hlDir "GameUserSettings.ini"
[System.IO.File]::WriteAllText($hlIni, "[/Script/HLLVietnam.ShooterGameUserSettings]`r`nSelectedGamepadLayout=LayoutA`r`n", $hlUtf8)
[System.IO.File]::WriteAllText((Join-Path $hlFake "vjoy.txt"), "installed=2.1.9.1`r`nvigem=1.22.0`r`n", $hlUtf8)
[System.IO.File]::WriteAllText((Join-Path $hlFake "hidhide-state.txt"), "--cloak-off`r`n--inv-on`r`n", $hlUtf8)
$hlPrevious = @{
    Data = $env:JOYPRO_DATA_DIR; Oodle = $env:JOYPRO_OODLE_DLL; Ini = $env:JOYPRO_HLLV_SETTINGS; Fake = $env:JOYPRO_VJOY_FAKE
    Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION
}
$env:JOYPRO_DATA_DIR = $hlData
$env:JOYPRO_OODLE_DLL = "none"
$env:JOYPRO_HLLV_SETTINGS = $hlIni
$env:JOYPRO_VJOY_FAKE = $hlFake
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$hlStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$hlThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$hlProfile = Join-Path $hlDir "hllv.pr0file"
try {
    # ---- 1. catalog
    $hlRefresh = Run-Engine @("hllv", "refresh-db", "--text")
    Assert-True ($hlRefresh.ExitCode -eq 0) "hllv refresh-db failed. Output: $($hlRefresh.Output)"
    Assert-Match $hlRefresh.Output "built-in table of the default helicopter gamepad layouts" "Without the game files the built-in table must be used and named."
    $hlCatalog = [System.IO.File]::ReadAllText((Join-Path $hlCatalogDir "actions.tsv"))
    Assert-Match $hlCatalog "(?m)^Pad_Helicopter_PitchForward\t[^\t]+\taxis\tHelicopter gamepad\tA:Gamepad_LeftY\|B:Gamepad_RightY\|C:Gamepad_LeftY" "The pitch action must carry its key per layout."
    Assert-Match $hlCatalog "(?m)^Driver_IncreaseCollective\t[^\t]+\tbutton\tHelicopter keyboard" "The keyboard pilot keys must be in the catalog."

    # ---- 2. the virtual Xbox pad follows the selected layout
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Collective", "--axis", "--node", "HLLVietnam:Pad_Helicopter_Ascend", "--device", $hlThrottle, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "item", "add", "Collective", "--node", "HLLVietnam:Pad_Helicopter_Descend", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Pitch", "--axis", "--node", "HLLVietnam:Pad_Helicopter_PitchForward", "--device", $hlStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Roll", "--axis", "--node", "HLLVietnam:Pad_Helicopter_RollRight", "--device", $hlStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Yaw", "--axis", "--node", "HLLVietnam:Pad_Helicopter_YawRight_Gamepad", "--device", $hlStick, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Stability", "--button", "--node", "HLLVietnam:Pad_HelicopterStabilityHold", "--device", $hlStick, "--input", "JOY_BTN2", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Supply", "--button", "--node", "HLLVietnam:Pad_Helicopter_SupplyDrop", "--device", $hlThrottle, "--input", "JOY_BTN4", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Spare", "--button", "--node", "HLLVietnam:Driver_Auto-Level", "--device", $hlThrottle, "--input", "JOY_BTN9", "--text") | Out-Null
    Run-Engine @("--profile", $hlProfile, "binds", "set", "Yaw", "--inverted", "true", "--text") | Out-Null
    $hlMode = Run-Engine @("--profile", $hlProfile, "vjoy", "mode", "HLLVietnam", "xbox", "--text")
    Assert-True ($hlMode.ExitCode -eq 0) "vjoy mode HLLVietnam xbox failed. Output: $($hlMode.Output)"
    Assert-Match $hlMode.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_Z -> Xbox XB_TRIGGERS \(Triggers \(centre split" "The collective on Ascend + Descend must become one centre-split trigger axis."
    Assert-Match $hlMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Y -> Xbox XB_LY \(Left stick Y\)" "Layout A: pitch on the left stick Y."
    Assert-Match $hlMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_X -> Xbox XB_LX \(Left stick X\)" "Layout A: roll on the left stick X."
    Assert-Match $hlMode.Output "JOY_RZ -> Xbox XB_RX~ \(Right stick X \(reversed\)\)" "Layout A: yaw on the right stick X, reversed by the relation's Invert."
    Assert-Match $hlMode.Output "JOY_BTN2 -> Xbox XB_B \(B\)" "Layout A: stability hold on B."
    Assert-Match $hlMode.Output "JOY_BTN4 -> Xbox XB_DPAD_RIGHT \(D-pad right\)" "Layout A: supply drop on D-pad right."
    Assert-Match $hlMode.Output "JOY_BTN9 -> Xbox XB_A \(A\)" "An action without a gamepad key gets the first free button."
    $hlStatus = Run-Engine @("--profile", $hlProfile, "vjoy", "status", "--text")
    Assert-Match $hlStatus.Output "JOY_Y -> Left stick Y \(game layout: Pad_Helicopter_PitchForward\)" "status must show the pinned control and its action."
    Assert-Match $hlStatus.Output "HLLVietnam \| xbox \| " "status must show HLLVietnam in Xbox mode."

    [System.IO.File]::WriteAllText($hlIni, "[/Script/HLLVietnam.ShooterGameUserSettings]`r`nSelectedGamepadLayout=LayoutB`r`n", $hlUtf8)
    $hlB = Run-Engine @("--profile", $hlProfile, "vjoy", "map", "--text")
    Assert-Match $hlB.Output "JOY_Y -> Xbox XB_RY \(Right stick Y\)" "Layout B: pitch moves to the right stick Y."
    Assert-Match $hlB.Output "JOY_RZ -> Xbox XB_LX~ \(Left stick X \(reversed\)\)" "Layout B: yaw moves to the left stick X."
    [System.IO.File]::WriteAllText($hlIni, "[/Script/HLLVietnam.ShooterGameUserSettings]`r`nSelectedGamepadLayout=LayoutA`r`n", $hlUtf8)
    Run-Engine @("--profile", $hlProfile, "vjoy", "map", "--text") | Out-Null
    $hlTable = Join-Path $hlDir "forwarder.json"
    Run-Engine @("--profile", $hlProfile, "vjoy", "forwarder-config", "--out", $hlTable, "--text") | Out-Null
    $hlForwarder = Get-Content $hlTable -Raw | ConvertFrom-Json
    Assert-True ($hlForwarder.xboxGame -eq "HLLVietnam" -and @($hlForwarder.xbox | Where-Object { $_.target -eq "XB_TRIGGERS" }).Count -eq 1) "The forwarder table must hold the HLLV pad with the split collective."

    # ---- 3. export: gamepad binds noted, keyboard keys only with --include-keyboard
    $hlSav = Join-Path $hlDir "EnhancedInputUserSettings.sav"
    Copy-Item (Join-Path $engineDir "tests\fixtures\hllv\EnhancedInputUserSettings.sav") $hlSav
    $hlOriginal = [System.IO.File]::ReadAllBytes($hlSav)
    Run-Engine @("--profile", $hlProfile, "relations", "add", "Kb Collective", "--button", "--node", "HLLVietnam:Driver_IncreaseCollective", "--device", "Keyboard", "--input", "Q", "--text") | Out-Null
    $hlExport = Run-Engine @("--profile", $hlProfile, "hllv", "export", $hlSav, "--text")
    Assert-True ($hlExport.ExitCode -eq 0) "hllv export failed. Output: $($hlExport.Output)"
    Assert-Match $hlExport.Output "gamepad action bind\(s\): the game's gamepad layouts are fixed, so nothing is written for them" "The export must note the gamepad binds instead of warning for each."
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$hlOriginal, [byte[]][System.IO.File]::ReadAllBytes($hlSav))) "Without --include-keyboard the .sav must stay byte-identical."
    $hlKeys = Run-Engine @("--profile", $hlProfile, "hllv", "export", $hlSav, "--include-keyboard", "--text")
    Assert-True ($hlKeys.ExitCode -eq 0) "hllv export --include-keyboard failed. Output: $($hlKeys.Output)"
    $hlScan = Run-Engine @("--profile", $hlProfile, "hllv", "scan", $hlSav, "--text")
    Assert-Match $hlScan.Output "Driver_IncreaseCollective <- Keyboard Q" "The keyboard key must be in the .sav."
    # ---- 4. one-device game: native mode warns and the status suggests the Xbox pad
    $hlNative = Run-Engine @("--profile", $hlProfile, "vjoy", "mode", "HLLVietnam", "native", "--text")
    Assert-Match $hlNative.Output "one-device game: .*Suggested: engine vjoy mode HLLVietnam xbox" "Switching a one-device game to native must suggest its virtual mode."
    $hlNativeStatus = Run-Engine @("--profile", $hlProfile, "vjoy", "status", "--text")
    Assert-Match $hlNativeStatus.Output "HLLVietnam \| native \| .*one-device game: xbox suggested" "status must suggest the Xbox pad for HLLV in native mode."
}
finally {
    $env:JOYPRO_DATA_DIR = $hlPrevious.Data
    $env:JOYPRO_OODLE_DLL = $hlPrevious.Oodle
    $env:JOYPRO_HLLV_SETTINGS = $hlPrevious.Ini
    $env:JOYPRO_VJOY_FAKE = $hlPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $hlPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $hlPrevious.Detection
    if (Test-Path $hlCatalogDir) {
        Remove-Item -Path $hlCatalogDir -Recurse -Force
    }
    if ($hlHadCatalog) {
        Copy-Item -Path $hlCatalogBackup -Destination $hlCatalogDir -Recurse -Force
    }
}

# ---- Oodle with the user's consent (docs/games/hll-vietnam.md "Oodle"): a synthetic Steam library with the game (an
# empty container) and another game shipping a stand-in oo2core_9_win64.dll (not a real DLL: loading it fails, which
# shows whether JoyPro tried). Without consent nothing is loaded; with --oodle-dll it is tried from where it is; the
# fallback is JoyPro's own table either way. The game's own library needs no consent.
$hoDir = Join-Path $tempDir "hllv-oodle"
$hoGame = Join-Path $hoDir "steamapps\common\Hell Let Loose - Vietnam"
$hoOther = Join-Path $hoDir "steamapps\common\Fake Shooter\Engine\Binaries"
New-Item -ItemType Directory -Force -Path (Join-Path $hoGame "HLLVietnam\Content\Paks"), $hoOther | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $hoGame "HLLVietnam\Content\Paks\HLLVietnam-Windows.utoc"), [byte[]](1, 2, 3, 4))
$hoForeign = Join-Path $hoOther "oo2core_9_win64.dll"
[System.IO.File]::WriteAllText($hoForeign, "stand-in, not a DLL")
$hoPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Oodle = $env:JOYPRO_OODLE_DLL; Detection = $env:JOYPRO_INSTALL_DETECTION }
$env:JOYPRO_DATA_DIR = Join-Path $hoDir "data"
$env:JOYPRO_OODLE_DLL = $null
$env:JOYPRO_INSTALL_DETECTION = "off"
try {
    Assert-True ((Run-Engine @("games", "install", "set", "hllv", $hoGame, "--text")).ExitCode -eq 0) "games install set hllv failed."
    $hoStatus = (Run-Engine @("hllv", "oodle", "--path", $hoGame)).Output | ConvertFrom-Json
    Assert-True ($hoStatus.OwnDll -eq "" -and $hoStatus.ForeignDll -eq $hoForeign -and $hoStatus.ForeignGame -eq "Fake Shooter") ("hllv oodle should name the other game's library: " + ($hoStatus | ConvertTo-Json -Compress))
    $hoNo = Run-Engine @("hllv", "refresh-db", "--path", $hoGame, "--text")
    Assert-True ($hoNo.ExitCode -eq 0) ("hllv refresh-db without consent failed: " + $hoNo.Output)
    Assert-NotMatch $hoNo.Output "could not be loaded|with your consent" "without consent the other game's library must not be loaded."
    Assert-Match $hoNo.Output "Fake Shooter has one, oo2core_9_win64\.dll; JoyPro uses it only when you allow it" "the refresh should say which library needs consent."
    Assert-Match $hoNo.Output "built-in table of the default helicopter gamepad layouts" "without consent JoyPro's own list is used."
    $hoYes = Run-Engine @("hllv", "refresh-db", "--path", $hoGame, "--oodle-dll", $hoForeign, "--text")
    Assert-True ($hoYes.ExitCode -eq 0) ("hllv refresh-db with consent failed: " + $hoYes.Output)
    Assert-Match $hoYes.Output "used with your consent from where it is, not copied" "with consent the library is named as used from where it is."
    Assert-Match $hoYes.Output "Oodle could not be loaded from .*oo2core_9_win64\.dll" "with consent JoyPro should try the library (the stand-in fails to load)."
    Assert-Match $hoYes.Output "built-in table of the default helicopter gamepad layouts" "a library that cannot be loaded falls back to JoyPro's own list."
    Assert-True (@(Get-ChildItem -Path $env:JOYPRO_DATA_DIR -Recurse -Filter "oo2core*" -ErrorAction SilentlyContinue).Count -eq 0) "the library must never be copied."
    # The game's own library: used without a question (and no other game is offered).
    New-Item -ItemType Directory -Force -Path (Join-Path $hoGame "HLLVietnam\Binaries") | Out-Null
    Copy-Item $hoForeign (Join-Path $hoGame "HLLVietnam\Binaries\oo2core_9_win64.dll") -Force
    $hoOwn = (Run-Engine @("hllv", "oodle", "--path", $hoGame)).Output | ConvertFrom-Json
    Assert-True ($hoOwn.OwnDll -like "*Hell Let Loose - Vietnam*" -and $hoOwn.ForeignDll -eq "") ("the game's own library should be found first: " + ($hoOwn | ConvertTo-Json -Compress))
}
finally {
    $env:JOYPRO_DATA_DIR = $hoPrevious.Data
    $env:JOYPRO_OODLE_DLL = $hoPrevious.Oodle
    $env:JOYPRO_INSTALL_DETECTION = $hoPrevious.Detection
}
