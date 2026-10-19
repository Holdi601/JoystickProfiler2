# VTOL VR: the BYOJoystick "Deadzone" per target (byoj model: takeover threshold on stick/throttles, cut on slew and
# Articulate Tilt, unused elsewhere), the native toe brakes' axisRange (vtolnative model) and Settings > Controls in
# gameSettings.cfg (device "*", game VTOLVR). Dot-sourced by engine_smoke_test.ps1. Fixtures are constructed in the
# mod's / game's serializer format (BYOJ master source; the game's GameSettings.SaveGameSettings output order).
$vtFixture = Join-Path $engineDir "tests\fixtures\vtolvr"
$vtStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$vtThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
$vtPedals = "MFG Crosswind V2 {42ce2930-0a3f-11f0-8001-444553540000}"
$previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $vtFixture "devices.tsv"
try {
    # ---- BYOJoystick ------------------------------------------------------------------------------------------------
    $vtSave = Join-Path $tempDir "vtol-tuning\steamapps\common\VTOL VR\SaveData"
    New-Item -Path $vtSave -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $vtFixture "byoj-tuning\BYOJ_AH94Front.xml") -Destination $vtSave -Force
    $vtSeat = Join-Path $vtSave "BYOJ_AH94Front.xml"
    $vtProfile = Join-Path $tempDir "vtol-tuning.pr0file"
    $vtPrefix = ""

    $vtModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $vtModels.Output "(?m)^byoj - VTOL VR \(BYOJoystick mod\)" "binds tuning models should list the byoj model."
    Assert-Match $vtModels.Output "(?m)^vtolnative - VTOL VR \(native rudder and toe brakes\)" "binds tuning models should list the vtolnative model."

    $vtImport = Run-Engine @("--profile", $vtProfile, "vtolvr", "import", $vtSave, "--text")
    Assert-True ($vtImport.ExitCode -eq 0) "vtolvr import (tuning fixture) failed. Output: $($vtImport.Output)"
    $vtPitch = Run-Engine @("--profile", $vtProfile, "binds", "tuning", "show", ($vtPrefix + "Joystick Pitch"), "--game", "VTOLVR", "--text")
    Assert-Match $vtPitch.Output "game:VTOLVR: inv=1;sl=0;dz=0;.*n\.takeover=0\.04" "vtolvr import should keep the stick's Deadzone as the takeover threshold, not as a dead zone."
    $vtSlew = Run-Engine @("--profile", $vtProfile, "binds", "tuning", "show", ($vtPrefix + "SOI Slew X"), "--game", "VTOLVR", "--text")
    Assert-Match $vtSlew.Output "game:VTOLVR: inv=0;sl=0;dz=0;.*n\.cut=0\.15;n\.slewAxis=1" "vtolvr import should keep the slew's Deadzone as a cut."
    $vtCollective = Run-Engine @("--profile", $vtProfile, "binds", "tuning", "show", ($vtPrefix + "Collective"), "--game", "VTOLVR", "--text")
    Assert-Match $vtCollective.Output "n\.takeover=0\.03" "vtolvr import should keep the collective's Deadzone as the takeover threshold."

    $vtRound = Run-Engine @("--profile", $vtProfile, "vtolvr", "export", "--path", $vtSave, "--text")
    Assert-True ($vtRound.ExitCode -eq 0) "vtolvr export after the tuning import failed. Output: $($vtRound.Output)"
    # (The export may reorder the binds JoyPro took over; their values must come back unchanged.)
    $vtRoundText = [System.IO.File]::ReadAllText($vtSeat)
    foreach ($vtExpect in @(@("Joystick Pitch", "0.04"), @("Collective", "0.03"), @("SOI Slew X", "0.15"), @("Radio Volume", "0.2"))) {
        Assert-Match $vtRoundText ("<Target>" + $vtExpect[0] + "</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>" + [regex]::Escape($vtExpect[1]) + "</Deadzone>") ("vtolvr import/export must keep the Deadzone " + $vtExpect[1] + " of " + $vtExpect[0] + " (a knob's unused value too).")
    }
    $vtRoundAgain = Run-Engine @("--profile", $vtProfile, "vtolvr", "export", "--path", $vtSave, "--text")
    Assert-Match $vtRoundAgain.Output "already match" "a second vtolvr export after the import should change nothing."

    # Preview: the slew cuts without rescale; the stick stays linear whatever its takeover threshold.
    $vtSlewPreview = Run-Engine @("--profile", $vtProfile, "binds", "tuning", "preview", ($vtPrefix + "SOI Slew X"), "--game", "VTOLVR", "--points", "21", "--text")
    Assert-Match $vtSlewPreview.Output "\+0\.100 -> \+0\.000000" "the byoj preview should cut the slew below 0.15."
    Assert-Match $vtSlewPreview.Output "\+0\.200 -> \+0\.200000" "the byoj preview must not rescale the slew above the cut."
    $vtPitchPreview = Run-Engine @("--profile", $vtProfile, "binds", "tuning", "preview", ($vtPrefix + "Joystick Pitch"), "--game", "VTOLVR", "--points", "21", "--text")
    Assert-Match $vtPitchPreview.Output "\+0\.100 -> -0\.100000" "the byoj preview of the stick should be linear (inverted) with a takeover threshold."

    Run-Engine @("--profile", $vtProfile, "binds", "tuning", "set", ($vtPrefix + "SOI Slew X"), "--scope", "game:VTOLVR", "--param", "cut=0.25", "--text") | Out-Null
    Run-Engine @("--profile", $vtProfile, "binds", "tuning", "set", ($vtPrefix + "Joystick Pitch"), "--scope", "game:VTOLVR", "--param", "takeover=0.1", "--text") | Out-Null
    $vtSet = Run-Engine @("--profile", $vtProfile, "vtolvr", "export", "--path", $vtSave, "--text")
    Assert-True ($vtSet.ExitCode -eq 0) "vtolvr export with own byoj values failed. Output: $($vtSet.Output)"
    $vtSeatText = [System.IO.File]::ReadAllText($vtSeat)
    Assert-Match $vtSeatText "<Target>SOI Slew X</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>0.25</Deadzone>" "vtolvr export should write the slew's cut as Deadzone."
    Assert-Match $vtSeatText "<Target>Joystick Pitch</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>0.1</Deadzone>" "vtolvr export should write the stick's takeover threshold as Deadzone."
    Assert-Match $vtSeatText "<Target>Radio Volume</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>0.2</Deadzone>" "vtolvr export must keep the Deadzone of a knob the mod ignores."

    # Fit from the base: slew and stick get the base dead zone (as before), a knob 0.
    $vtFitSave = Join-Path $tempDir "vtol-fit\steamapps\common\VTOL VR\SaveData"
    New-Item -Path $vtFitSave -ItemType Directory -Force | Out-Null
    $vtFitProfile = Join-Path $tempDir "vtol-fit.pr0file"
    Run-Engine @("--profile", $vtFitProfile, "relations", "add", "VF Slew Y", "--axis", "--node", "VTOLVR:SOI Slew Y", "--device", $vtStick, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $vtFitProfile, "relations", "add", "VF Volume", "--axis", "--node", "VTOLVR:Radio Volume", "--device", $vtThrottle, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $vtFitProfile, "binds", "set", "VF Slew Y", "--deadzone", "0.12", "--text") | Out-Null
    Run-Engine @("--profile", $vtFitProfile, "binds", "set", "VF Volume", "--deadzone", "0.3", "--text") | Out-Null
    $vtFit = Run-Engine @("--profile", $vtFitProfile, "vtolvr", "export", "--path", $vtFitSave, "--mode", "clean", "--text")
    Assert-True ($vtFit.ExitCode -eq 0) "vtolvr export of fitted base values failed. Output: $($vtFit.Output)"
    $vtFitText = [System.IO.File]::ReadAllText((Join-Path $vtFitSave "BYOJ_AV42C.xml"))
    Assert-Match $vtFitText "<Target>SOI Slew Y</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>0.12</Deadzone>" "vtolvr export should give the slew the base dead zone as its cut."
    Assert-Match $vtFitText "<Target>Radio Volume</Target>(?:(?!</JoystickBinding>)[\s\S])*<Deadzone>0</Deadzone>" "vtolvr export must not write a dead zone the mod ignores (knob)."

    # ---- native: toe brake axisRange, warnings, gameSettings.cfg ------------------------------------------------------
    $vtRoaming = Join-Path $tempDir "vtol-native\Roaming\SaveData"
    New-Item -Path $vtRoaming -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $vtFixture "native\inputbindings.cfg") -Destination $vtRoaming -Force
    Copy-Item -Path (Join-Path $vtFixture "settings\gameSettings.cfg") -Destination $vtRoaming -Force
    $vtBindings = Join-Path $vtRoaming "inputbindings.cfg"
    $vtSettings = Join-Path $vtRoaming "gameSettings.cfg"
    $vtSettingsOriginal = [System.IO.File]::ReadAllText($vtSettings)
    $vtNativeProfile = Join-Path $tempDir "vtol-native.pr0file"
    Run-Engine @("--profile", $vtNativeProfile, "relations", "add", "VN Rudder", "--axis", "--node", "VTOLVRNative:Rudder", "--device", $vtPedals, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $vtNativeProfile, "relations", "add", "VN Brake L", "--axis", "--node", "VTOLVRNative:Wheel Brake L", "--device", $vtPedals, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $vtNativeProfile, "relations", "add", "VN Brake R", "--axis", "--node", "VTOLVRNative:Wheel Brake R", "--device", $vtPedals, "--input", "JOY_Y", "--text") | Out-Null
    $vtRange = Run-Engine @("--profile", $vtNativeProfile, "binds", "tuning", "set", "VN Brake L", "--scope", "game:VTOLVRNative", "--param", "axisRange=1", "--inverted", "true", "--text")
    Assert-True ($vtRange.ExitCode -eq 0) "binds tuning set axisRange failed. Output: $($vtRange.Output)"
    $vtNative = Run-Engine @("--profile", $vtNativeProfile, "vtolvr-native", "export", $vtBindings, "--mode", "clean", "--text")
    Assert-True ($vtNative.ExitCode -eq 0) "vtolvr-native export with axisRange failed. Output: $($vtNative.Output)"
    $vtNativeText = [System.IO.File]::ReadAllText($vtBindings)
    Assert-Match $vtNativeText "<actionId>5</actionId><elementType>0</elementType><elementIdentifierId>0</elementIdentifierId><axisRange>1</axisRange><invert>true</invert>" "vtolvr-native export should write axisRange 1 (Positive) for the left toe brake."
    Assert-Match $vtNativeText "<actionId>6</actionId><elementType>0</elementType><elementIdentifierId>1</elementIdentifierId><axisRange>0</axisRange>" "vtolvr-native export should keep Full for the right brake without the setting."
    Assert-Match $vtNative.Output "VN Brake R' \(Wheel Brake R\): VTOL VR reads a toe brake as 0\.\.1" "vtolvr-native export should warn about the half-travel brake."
    Assert-NotMatch $vtNative.Output "VN Brake L' \(Wheel Brake L\): VTOL VR reads a toe brake" "a brake with axisRange 1 needs no half-travel warning."
    Assert-Match $vtNative.Output "Hardware Control Rudder is on" "without a HARDWARE_RUDDER setting the export should still warn that it is off."

    # With JoyPro's metadata the brake relation comes back by name and tuning; without it from the file alone.
    $vtMetaImportProfile = Join-Path $tempDir "vtol-native-meta-import.pr0file"
    Run-Engine @("--profile", $vtMetaImportProfile, "vtolvr-native", "import", $vtBindings, "--text") | Out-Null
    $vtMetaBrake = Run-Engine @("--profile", $vtMetaImportProfile, "binds", "tuning", "show", "VN Brake L", "--game", "VTOLVRNative", "--text")
    Assert-Match $vtMetaBrake.Output "VTOLVRNative uses .*n\.axisRange=1" "vtolvr-native import with metadata should restore 'VN Brake L' with its axisRange."
    Move-Item -LiteralPath ($vtBindings + ".joypro-meta.json") -Destination ($vtBindings + ".meta-aside") -Force
    $vtNativeImportProfile = Join-Path $tempDir "vtol-native-import.pr0file"
    Run-Engine @("--profile", $vtNativeImportProfile, "vtolvr-native", "import", $vtBindings, "--text") | Out-Null
    Move-Item -LiteralPath ($vtBindings + ".meta-aside") -Destination ($vtBindings + ".joypro-meta.json") -Force
    $vtBrakeShow = Run-Engine @("--profile", $vtNativeImportProfile, "binds", "tuning", "show", "Left wheel brake (toe brake)", "--game", "VTOLVRNative", "--text")
    Assert-Match $vtBrakeShow.Output "game:VTOLVRNative: inv=1;sl=1;.*n\.axisRange=1" "vtolvr-native import should keep the brake's axisRange as tuning."
    $vtBrakePreview = Run-Engine @("--profile", $vtNativeImportProfile, "binds", "tuning", "preview", "Left wheel brake (toe brake)", "--game", "VTOLVRNative", "--points", "3", "--text")
    Assert-Match $vtBrakePreview.Output "-1\.000 -> \+1\.000000[\s\S]*\+0\.000 -> \+0\.000000[\s\S]*\+1\.000 -> -1\.000000" "the vtolnative preview should show the whole travel for axisRange 1 + invert."

    # Only Wheel Brake R: the game never takes the brakes from the hardware.
    $vtOnlyR = Join-Path $tempDir "vtol-only-r.pr0file"
    Run-Engine @("--profile", $vtOnlyR, "relations", "add", "VR Brake R", "--axis", "--node", "VTOLVRNative:Wheel Brake R", "--device", $vtPedals, "--input", "JOY_Y", "--text") | Out-Null
    $vtOnlyRExport = Run-Engine @("--profile", $vtOnlyR, "vtolvr-native", "export", $vtBindings, "--mode", "clean", "--dry-run", "--text")
    Assert-Match $vtOnlyRExport.Output "Only Wheel Brake R is bound" "vtolvr-native export should warn when only the right brake is bound."

    # Settings > Controls (device "*", game VTOLVR) in gameSettings.cfg: bools 1/-1, % values, a missing key added
    # among the root values (before the PLAYAREA node).
    $vtFfbModels = Run-Engine @("ffb", "models", "--text")
    Assert-Match $vtFfbModels.Output "OVERALL_HAPTICS \(Overall Haptics \(%\), 0\.\.200" "ffb models should list VTOL VR's Overall Haptics 0..200 %."
    $vtTooHigh = Run-Engine @("--profile", $vtNativeProfile, "ffb", "set", "--device", "*", "--game", "VTOLVR", "--param", "OVERALL_HAPTICS=250", "--text")
    Assert-True ($vtTooHigh.ExitCode -ne 0) "Overall Haptics above 200 % must be refused."
    $vtWide = Run-Engine @("--profile", $vtNativeProfile, "ffb", "set", "--device", "*", "--game", "VTOLVR", "--param", "HARDWARE_RUDDER=true", "--param", "CONTROL_HAPTICS=60", "--param", "OVERALL_HAPTICS=150", "--param", "TWIST_RUDDER_DEADZONE=5.5", "--param", "THUMB_RUDDER=false", "--text")
    Assert-True ($vtWide.ExitCode -eq 0) "ffb set for VTOL VR failed. Output: $($vtWide.Output)"
    $vtWideExport = Run-Engine @("--profile", $vtNativeProfile, "vtolvr-native", "export", $vtBindings, "--mode", "clean", "--text")
    Assert-True ($vtWideExport.ExitCode -eq 0) "vtolvr-native export with settings failed. Output: $($vtWideExport.Output)"
    Assert-NotMatch $vtWideExport.Output "ignores these axes until Settings > Hardware Control Rudder is on" "with HARDWARE_RUDDER set in the profile the export must not warn that it is off."
    $vtExpected = $vtSettingsOriginal.Replace("`tHARDWARE_RUDDER = -1", "`tHARDWARE_RUDDER = 1").Replace("`tCONTROL_HAPTICS = 100", "`tCONTROL_HAPTICS = 60").Replace("`tTWIST_RUDDER_DEADZONE = 3", "`tTWIST_RUDDER_DEADZONE = 5.5").Replace("`tMSAA = 1`n", "`tMSAA = 1`n`tOVERALL_HAPTICS = 150`n")
    Assert-True ([System.IO.File]::ReadAllText($vtSettings) -eq $vtExpected) "vtolvr-native export should change exactly the set Controls values in gameSettings.cfg (got: $([System.IO.File]::ReadAllText($vtSettings)))."
    Assert-True (Test-Path ($vtSettings + ".jpbak")) "the gameSettings.cfg export should back it up."
    $vtWideAgain = Run-Engine @("--profile", $vtNativeProfile, "vtolvr-native", "export", $vtBindings, "--mode", "clean", "--text")
    Assert-Match $vtWideAgain.Output "gameSettings\.cfg already matches" "a second export should leave gameSettings.cfg alone."

    # Settings only (no native binds in the profile): inputbindings.cfg stays as it is.
    Copy-Item -Path (Join-Path $vtFixture "settings\gameSettings.cfg") -Destination $vtRoaming -Force
    $vtBindingsBefore = [System.IO.File]::ReadAllText($vtBindings)
    $vtSettingsOnly = Join-Path $tempDir "vtol-settings.pr0file"
    Run-Engine @("--profile", $vtSettingsOnly, "ffb", "set", "--device", "*", "--game", "VTOLVR", "--param", "OVERALL_HAPTICS=80", "--text") | Out-Null
    $vtOnly = Run-Engine @("--profile", $vtSettingsOnly, "vtolvr-native", "export", $vtBindings, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($vtOnly.ExitCode -eq 0) "vtolvr-native settings-only export failed. Output: $($vtOnly.Output)"
    Assert-True ([System.IO.File]::ReadAllText($vtBindings) -eq $vtBindingsBefore) "a settings-only vtolvr-native export must not touch inputbindings.cfg."
    Assert-Match ([System.IO.File]::ReadAllText($vtSettings)) "`tOVERALL_HAPTICS = 80`n" "a settings-only vtolvr-native export should write the settings."

    # No gameSettings.cfg yet: warning, nothing created.
    Remove-Item -Path $vtSettings -Force
    $vtNoFile = Run-Engine @("--profile", $vtSettingsOnly, "vtolvr-native", "export", $vtBindings, "--text")
    Assert-Match $vtNoFile.Output "VTOL VR settings not written: .*gameSettings\.cfg does not exist yet" "vtolvr-native export should warn when gameSettings.cfg is missing."
    Assert-True (-not (Test-Path $vtSettings)) "vtolvr-native export must not create gameSettings.cfg."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
}

# ---- refresh-db ---------------------------------------------------------------------------------------------------
# Native: the Rewired InputManager in VTOLVR_Data\level0 (VTOL VR 1.13 layout: 3 int32 per action = behavior,
# userAssignable, category; categories end with id, userAssignable), synthetic: Rudder, the non-assignable Mouse X and a
# new assignable "Smoke Throttle". BYOJoystick: the targets of the user's BYOJ_<seat>.xml files (the full list is
# compiled into the mod). Runs in a temporary JoyPro folder with the install set (no --path, as the GUI calls it).
function Add-VtString([System.IO.BinaryWriter]$Writer, [string]$Text, [long]$Start) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $Writer.Write([int]$bytes.Length)
    $Writer.Write($bytes)
    while ((($Writer.BaseStream.Position - $Start) % 4) -ne 0) { $Writer.Write([byte]0) }
}
$vrDir = Join-Path $tempDir "vtol-refresh"
$vrGame = Join-Path $vrDir "VTOL VR"
$vrRoot = Join-Path $vrDir "joypro"
New-Item -Path (Join-Path $vrGame "VTOLVR_Data"), (Join-Path $vrGame "SaveData"), (Join-Path $vrRoot "DB\VTOLVR"), (Join-Path $vrRoot "DB\VTOLVRNative"), (Join-Path $vrRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\VTOLVR\*") -Destination (Join-Path $vrRoot "DB\VTOLVR") -Force
Copy-Item -Path (Join-Path $engineBin "DB\VTOLVRNative\*") -Destination (Join-Path $vrRoot "DB\VTOLVRNative") -Force
$vrStream = New-Object System.IO.MemoryStream
$vrWriter = New-Object System.IO.BinaryWriter($vrStream)
$vrWriter.Write([byte[]](1, 2, 3, 4, 0, 0, 0, 0))
$vrStart = $vrStream.Position
$vrWriter.Write([int]3)
foreach ($vrAction in @(@(0, "Rudder", "Rudder", 0, 1, 0), @(2, "Mouse X", "Mouse X", 0, 0, 1), @(7, "Smoke Throttle", "Smoke Throttle Axis", 0, 1, 0))) {
    $vrWriter.Write([int]$vrAction[0]); Add-VtString $vrWriter $vrAction[1] $vrStart; $vrWriter.Write([int]0)
    Add-VtString $vrWriter $vrAction[2] $vrStart; Add-VtString $vrWriter ($vrAction[1] + "+") $vrStart; Add-VtString $vrWriter ($vrAction[1] + "-") $vrStart
    foreach ($v in $vrAction[3..5]) { $vrWriter.Write([int]$v) }
}
$vrWriter.Write([int]2)
Add-VtString $vrWriter "Default" $vrStart; Add-VtString $vrWriter "Default" $vrStart; Add-VtString $vrWriter "" $vrStart; $vrWriter.Write([int]0); $vrWriter.Write([int]1)
Add-VtString $vrWriter "Mouse Input" $vrStart; Add-VtString $vrWriter "Replace Unity Mouse Inputs" $vrStart; Add-VtString $vrWriter "" $vrStart; $vrWriter.Write([int]1); $vrWriter.Write([int]0)
$vrWriter.Write([byte[]](9, 9, 9, 9))
$vrWriter.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $vrGame "VTOLVR_Data\level0"), $vrStream.ToArray())

$vrJoystick = "    <JoystickBinding>`r`n      <Target>{0}</Target>`r`n      <JoystickId>11111111-2222-3333-4444-555555555555</JoystickId>`r`n      <Input>{1}</Input>`r`n      <RequiresModifier>false</RequiresModifier>`r`n      <Invert>false</Invert>`r`n      <Deadzone>0</Deadzone>`r`n      <POVDirection>None</POVDirection>`r`n    </JoystickBinding>`r`n"
$vrFile = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<Bindings xmlns:xsi=`"http://www.w3.org/2001/XMLSchema-instance`" xmlns:xsd=`"http://www.w3.org/2001/XMLSchema`">`r`n  <KeyboardBindings>`r`n    <KeyboardBinding>`r`n      <Target>Smoke Key</Target>`r`n      <Key>K</Key>`r`n      <ModifierKey>None</ModifierKey>`r`n    </KeyboardBinding>`r`n  </KeyboardBindings>`r`n  <JoystickBindings>`r`n" +
    ($vrJoystick -f "Smoke Target", "Z") + ($vrJoystick -f "Tilt", "S1") + ($vrJoystick -f "Joystick Pitch", "Y") + "  </JoystickBindings>`r`n</Bindings>"
[System.IO.File]::WriteAllText((Join-Path $vrGame "SaveData\BYOJ_FA26B.xml"), $vrFile, (New-Object System.Text.UTF8Encoding $false))
[System.IO.File]::WriteAllText((Join-Path $vrGame "SaveData\BYOJ_Zeppelin.xml"), ($vrFile -replace "Smoke Target", "Zeppelin Target"), (New-Object System.Text.UTF8Encoding $false))

$vrPreviousData = $env:JOYPRO_DATA_DIR
$vrPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$vrPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = Join-Path $vrDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_FAKE_DEVICES = Join-Path $vtFixture "devices.tsv"
# The refresh writes into the game-data folder (<data>\GameData), never into the program folder ($vrRoot).
$vrLists = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB"
Push-Location $vrRoot
try {
    foreach ($vrGameId in @("vtolvr", "vtolvr-native")) {
        $vrSet = Run-Engine @("games", "install", "set", $vrGameId, $vrGame, "--text")
        Assert-True ($vrSet.ExitCode -eq 0) ("games install set " + $vrGameId + " failed: " + $vrSet.Output)
    }

    $vrNative = Run-Engine @("vtolvr-native", "refresh-db", "--text")
    Assert-True ($vrNative.ExitCode -eq 0) ("vtolvr-native refresh-db failed: " + $vrNative.Output)
    Assert-Match $vrNative.Output "Read 3 Rewired action\(s\) .*\(2 user-assignable\): 1 new" ("vtolvr-native refresh-db should read the level0 action list: " + $vrNative.Output)
    $vrNativeCatalog = [System.IO.File]::ReadAllText((Join-Path $vrLists "VTOLVRNative\actions.tsv"))
    Assert-Match $vrNativeCatalog "(?m)^Smoke Throttle\tSmoke Throttle Axis\taxis\tHardware Rudder\t7\r?$" "a new assignable Rewired action should come in with its id and the category of its Rewired category."
    Assert-Match $vrNativeCatalog "(?m)^Rudder\tRudder \(hardware rudder pedals / twist\)\taxis\tHardware Rudder\r?$" "known native actions must keep their title."
    Assert-NotMatch $vrNativeCatalog "Mouse X" "non-assignable mouse actions must not become catalog actions."
    # The new action exports with its Rewired id.
    $vrBindings = Join-Path $vrDir "Roaming\inputbindings.cfg"
    New-Item -Path (Split-Path -Parent $vrBindings) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $vtFixture "native\inputbindings.cfg") -Destination $vrBindings -Force
    $vrProfile = Join-Path $vrDir "refresh.pr0file"
    Run-Engine @("--profile", $vrProfile, "relations", "add", "VR Smoke Throttle", "--axis", "--node", "VTOLVRNative:Smoke Throttle", "--device", $vtPedals, "--input", "JOY_Y", "--text") | Out-Null
    $vrExport = Run-Engine @("--profile", $vrProfile, "vtolvr-native", "export", $vrBindings, "--text")
    Assert-True ($vrExport.ExitCode -eq 0) ("vtolvr-native export of a refreshed action failed: " + $vrExport.Output)
    Assert-Match ([System.IO.File]::ReadAllText($vrBindings)) "<actionId>7</actionId><elementType>0</elementType>" "a refreshed native action should be written with its Rewired action id."
    $vrNativeAgain = Run-Engine @("vtolvr-native", "refresh-db", "--text")
    Assert-Match $vrNativeAgain.Output "nothing was written" ("a second vtolvr-native refresh should write nothing: " + $vrNativeAgain.Output)

    $vrByoj = Run-Engine @("vtolvr", "refresh-db", "--text")
    Assert-True ($vrByoj.ExitCode -eq 0) ("vtolvr refresh-db from the BYOJ files failed: " + $vrByoj.Output)
    Assert-Match $vrByoj.Output "Read 1 BYOJ_<seat>\.xml file\(s\) .*: 2 new target\(s\), 3 new seat membership\(s\)" ("vtolvr refresh-db should read the known seat's file: " + $vrByoj.Output)
    Assert-Match $vrByoj.Output "BYOJ_Zeppelin\.xml belongs to a vehicle seat JoyPro does not know" "a file of an unknown seat should be reported."
    $vrCatalog = [System.IO.File]::ReadAllText((Join-Path $vrLists "VTOLVR\actions.tsv"))
    Assert-Match $vrCatalog "(?m)^Smoke Target\tSmoke Target\taxis\tMisc\r?$" "a target bound to an axis should come in as an axis."
    Assert-Match $vrCatalog "(?m)^Smoke Key\tSmoke Key\tbutton\tMisc\r?$" "a keyboard-only target should come in as a button."
    Assert-Match $vrCatalog "(?m)^Tilt\tTilt\taxis\tFlight\r?$" "known targets must keep their title and category."
    Assert-NotMatch $vrCatalog "Zeppelin Target" "targets of unknown seats must not be added."
    $vrSeats = [System.IO.File]::ReadAllText((Join-Path $vrLists "VTOLVR\seats.tsv"))
    Assert-Match $vrSeats "(?m)^Smoke Target\tFA26B\taxis\r?$" "a new target should get the seats it was found in."
    Assert-Match $vrSeats "(?m)^Tilt\tAV42C,FA26B,F45A\taxis\r?$" "a known target should gain the seat it was found in."
    $vrExportSave = Join-Path $vrDir "export\SaveData"
    New-Item -Path $vrExportSave -ItemType Directory -Force | Out-Null
    Run-Engine @("--profile", $vrProfile, "relations", "add", "VR Smoke Target", "--axis", "--node", "VTOLVR:Smoke Target", "--device", $vtStick, "--input", "JOY_Z", "--text") | Out-Null
    $vrByojExport = Run-Engine @("--profile", $vrProfile, "vtolvr", "export", "--path", $vrExportSave, "--text")
    Assert-True ($vrByojExport.ExitCode -eq 0) ("vtolvr export of a refreshed target failed: " + $vrByojExport.Output)
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $vrExportSave "BYOJ_FA26B.xml"))) "<Target>Smoke Target</Target>" "a refreshed target should be exported to the seat it was found in."
    $vrByojAgain = Run-Engine @("vtolvr", "refresh-db", "--text")
    Assert-Match $vrByojAgain.Output "nothing was written" ("a second vtolvr refresh should write nothing: " + $vrByojAgain.Output)
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\VTOLVR\actions.tsv")) -notmatch "Smoke Target") "the shipped VTOL VR catalog must stay untouched."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\VTOLVRNative\actions.tsv")) -notmatch "Smoke Throttle") "the shipped VTOL VR native catalog must stay untouched."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $vrRoot "DB\VTOLVR\seats.tsv")) -notmatch "Smoke Target") "the refresh must not write into the program folder."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $vrPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $vrPreviousDetection
    $env:JOYPRO_FAKE_DEVICES = $vrPreviousFakeDevices
}

# ---- BYOJoystick installed from the Steam Workshop: status, prepare, targets read from the mod's DLL -----------------
# The mod is never shipped: a stand-in DLL is compiled from engine\tests\fixtures\vtolvr\byoj-fake\FakeByojMod.cs (made
# up targets "Fake ...", the shape JoyPro's reader expects). JOYPRO_VTOLVR_MOD_DIR stands for the Workshop item folder.
# A fresh JoyPro folder (no DB\VTOLVR, as a release) builds its list from the mod alone.
$vmDir = Join-Path $tempDir "vtol-mod"
$vmMod = Join-Path $vmDir "workshop\3276352374"
$vmRoot = Join-Path $vmDir "joypro"
$vmGame = Join-Path $vmDir "VTOL VR"
New-Item -Path $vmMod, (Join-Path $vmRoot "DB\Functions"), (Join-Path $vmRoot "CleanProfile"), (Join-Path $vmGame "SaveData") -ItemType Directory -Force | Out-Null
Set-Content -Path (Join-Path $vmGame "VTOLVR.exe") -Value "stand-in" -Encoding ASCII
Copy-Item -Path (Join-Path $vrGame "SaveData\BYOJ_FA26B.xml") -Destination (Join-Path $vmGame "SaveData\BYOJ_FA26B.xml")
$vmCsc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Assert-True (Test-Path $vmCsc) "csc.exe of .NET Framework 4 was not found (needed to build the stand-in mod DLL)."
& $vmCsc /nologo /target:library /optimize+ ("/out:" + (Join-Path $vmMod "BYOJoystick.dll")) (Join-Path $engineDir "tests\fixtures\vtolvr\byoj-fake\FakeByojMod.cs") | Out-Host
Assert-True ($LASTEXITCODE -eq 0) "the stand-in BYOJoystick.dll did not compile."
$vmPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Detection = $env:JOYPRO_INSTALL_DETECTION; Mod = $env:JOYPRO_VTOLVR_MOD_DIR }
$env:JOYPRO_DATA_DIR = Join-Path $vmDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $vmRoot
try {
    Assert-True ((Run-Engine @("games", "install", "set", "vtolvr", $vmGame, "--text")).ExitCode -eq 0) "games install set vtolvr failed."
    # Not installed yet: prepare needs the consent, backs up the BYOJ files and names the Workshop page.
    $env:JOYPRO_VTOLVR_MOD_DIR = Join-Path $vmDir "not-subscribed"
    $vmStatus = (Run-Engine @("vtolvr", "mod", "status")).Output | ConvertFrom-Json
    Assert-True (-not $vmStatus.ModInstalled) "vtolvr mod status should report the mod as missing."
    Assert-Match $vmStatus.License "no license" "the status should say the mod has no license."
    Assert-True ($vmStatus.WorkshopItem -eq "3276352374" -and $vmStatus.ListedSize -gt 0) "the status should name the Workshop item and its size."
    $vmRefused = Run-Engine @("vtolvr", "mod", "prepare")
    Assert-True ($vmRefused.ExitCode -ne 0) "vtolvr mod prepare must refuse without --accept-license."
    $vmPrepare = (Run-Engine @("vtolvr", "mod", "prepare", "--accept-license")).Output | ConvertFrom-Json
    Assert-True (@($vmPrepare.OpenUrls) -contains "steam://url/CommunityFilePage/3276352374") "prepare should name the Workshop page to open."
    Assert-True (@($vmPrepare.BackedUp | Where-Object { $_ -like "*BYOJ_FA26B.xml" }).Count -eq 1) "prepare should back up the user's BYOJ files."
    $vmBackups = Run-Engine @("backups", "list", "--game", "VTOLVR", "--text")
    Assert-Match $vmBackups.Output "BYOJ_FA26B\.xml|1 file" ("the BYOJ backup should be in the backup store: " + $vmBackups.Output)
    $vmNoMod = Run-Engine @("vtolvr", "refresh-db", "--path", (Join-Path $vmDir "empty"), "--text")
    Assert-True ($vmNoMod.ExitCode -ne 0) "without the mod and without BYOJ files the refresh has nothing to read."

    # Subscribed: the mod's targets with seats and input types.
    $env:JOYPRO_VTOLVR_MOD_DIR = $vmMod
    $vmStatus = (Run-Engine @("vtolvr", "mod", "status")).Output | ConvertFrom-Json
    Assert-True ($vmStatus.ModInstalled -and $vmStatus.Targets -eq 8) ("the installed stand-in should offer 8 targets: " + ($vmStatus | ConvertTo-Json -Compress))
    Assert-True ($vmStatus.DllSha256 -match "^[0-9a-f]{64}$") "the status should show the DLL's SHA-256."
    $vmRefresh = Run-Engine @("vtolvr", "refresh-db", "--text")
    Assert-True ($vmRefresh.ExitCode -eq 0) ("vtolvr refresh-db from the installed mod failed: " + $vmRefresh.Output)
    Assert-Match $vmRefresh.Output "Read the installed BYOJoystick mod .*: 8 targets over 2 vehicle seats" ("the refresh should read the mod: " + $vmRefresh.Output)
    $vmList = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\VTOLVR\actions.tsv"))
    Assert-Match $vmList "(?m)^Fake Stick Pitch\tFake Stick Pitch\taxis\tFlight\r?$" "a centred stick axis of the mod should come in as an axis."
    Assert-Match $vmList "(?m)^Fake Nav Lights\tFake Nav Lights\tbutton\tLights\r?$" "a lights button should come in with its category."
    Assert-Match $vmList "(?m)^Modifier\tModifier\tbutton\tModifier\r?$" "the mod's modifier exists in every seat."
    Assert-NotMatch $vmList "not a target" "other strings of the DLL must not become targets."
    $vmSeats = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\VTOLVR\seats.tsv"))
    Assert-Match $vmSeats "(?m)^Fake Stick Pitch\tFA26B,AH94Front\taxis_centered\r?$" "a target of two vehicles should get both seats and the centred input type."
    Assert-Match $vmSeats "(?m)^Fake Recenter\tFA26B,AH94Front\tbutton\r?$" "the base manager's target belongs to every vehicle of the mod."
    Assert-True (-not (Test-Path (Join-Path $vmRoot "DB\VTOLVR"))) "the refresh must not write into the program folder."
    $vmAgain = Run-Engine @("vtolvr", "refresh-db", "--text")
    Assert-Match $vmAgain.Output "nothing was written" ("a second refresh from the mod should write nothing: " + $vmAgain.Output)
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $vmPrevious.Data
    $env:JOYPRO_INSTALL_DETECTION = $vmPrevious.Detection
    $env:JOYPRO_VTOLVR_MOD_DIR = $vmPrevious.Mod
}
