# Project Wingman (native, several devices) on synthetic files of the real shape (docs/games/project-wingman.md).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). The fixtures follow the files the
# game wrote on a test PC (2026-10-12): Input.ini with plain ActionMappings/AxisMappings lines and JoystickPlugin keys
# "Joystick_<DirectInput name without spaces>_<place in the game's device list>_Axis<n>/_Button<n>", a pak
# (UE4 v11, built here) holding Config/DefaultInput.ini, and a GVAS savegame.sav with the curve/dead zone floats.
# Fake devices (tests\fixtures\vjoy\devices.tsv): VJ Stick, VJ Throttle, vJoy Device -> the game's list is reversed:
# vJoyDevice_0, VJThrottle_1, VJStick_2.
#  1. pw refresh-db from the pak: flight actions only (no Menu / Mouse rows), axes and buttons, "-" lines applied.
#  2. scan: axes by the device's present axes (VJ Stick X,Y,RZ: Axis2 = JOY_RZ), Scale -1 = inverted, buttons from 0,
#     a moved device number is read with a note, an unknown device is reported; the save's curve / dead zone.
#  3. export (merge-overwrite): keys per device, Invert as Scale -1, a hat is refused with the game's own way, the
#     other lines stay, savegame.sav gets the curve / dead zone floats (new ones before "None"), a second export changes
#     nothing; merge keeps the game's bind; clean drops the exported devices' other joystick lines.
#  4. no Input.ini yet: the export writes the pak's defaults first (plain lines; the Android keys stay: the menu shows
#     one slot per mapping and those are its empty slots).
$pwDir = Join-Path $tempDir "projectwingman"
New-Item -ItemType Directory -Force -Path $pwDir | Out-Null
$pwCatalogDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\ProjectWingman"
$pwCatalogBackup = Join-Path $pwDir "catalog-backup"
$pwHadCatalog = Test-Path $pwCatalogDir
if ($pwHadCatalog) { Copy-Item -Path $pwCatalogDir -Destination $pwCatalogBackup -Recurse -Force }
$pwPrevious = @{ Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION; Save = $env:JOYPRO_PW_SAVEGAME }
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$pwStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$pwThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$pwProfile = Join-Path $pwDir "pw.pr0file"
$pwIni = Join-Path $pwDir "Input.ini"
$pwSave = Join-Path $pwDir "savegame.sav"
$env:JOYPRO_PW_SAVEGAME = $pwSave
$pwFixture = Join-Path $engineDir "tests\fixtures\projectwingman"
Copy-Item (Join-Path $pwFixture "Input.ini") $pwIni

# A UE4 pak (version 11, stored entries, full directory index) with the given files ("Dir/Name" -> text).
function New-PwPak([string]$Path, [hashtable]$Files) {
    $ascii = [Text.Encoding]::ASCII
    function FStr([string]$s) { $b = $ascii.GetBytes($s); return [byte[]](@([BitConverter]::GetBytes($b.Length + 1)) + $b + @(0)) }
    $data = New-Object IO.MemoryStream
    $encoded = New-Object IO.MemoryStream
    $dirs = @{}
    foreach ($name in ($Files.Keys | Sort-Object)) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Files[$name])
        $offset = $data.Position
        # The entry's own header: offset, size, uncompressed size, method 0, hash 20, flags 1, block size 4.
        $header = [byte[]](@([BitConverter]::GetBytes([long]0)) + @([BitConverter]::GetBytes([long]$bytes.Length)) + @([BitConverter]::GetBytes([long]$bytes.Length)) + @([BitConverter]::GetBytes([int]0)) + (New-Object byte[] 20) + @(0) + @([BitConverter]::GetBytes([int]0)))
        $data.Write($header, 0, $header.Length); $data.Write($bytes, 0, $bytes.Length)
        $entryAt = $encoded.Position
        # Bits: offset, uncompressed and size fit 32 bits; method 0; no blocks.
        $bits = [BitConverter]::GetBytes([int]0xE0000000)
        $encoded.Write($bits, 0, 4); $encoded.Write([BitConverter]::GetBytes([uint32]$offset), 0, 4); $encoded.Write([BitConverter]::GetBytes([uint32]$bytes.Length), 0, 4)
        $dir = $name.Substring(0, $name.LastIndexOf('/') + 1); $file = $name.Substring($name.LastIndexOf('/') + 1)
        if (-not $dirs.ContainsKey($dir)) { $dirs[$dir] = New-Object System.Collections.ArrayList }
        [void]$dirs[$dir].Add(@($file, $entryAt))
    }
    $fdi = New-Object IO.MemoryStream
    $fdi.Write([BitConverter]::GetBytes([int]$dirs.Count), 0, 4)
    foreach ($dir in $dirs.Keys) {
        $b = FStr $dir; $fdi.Write($b, 0, $b.Length); $fdi.Write([BitConverter]::GetBytes([int]$dirs[$dir].Count), 0, 4)
        foreach ($f in $dirs[$dir]) { $b = FStr $f[0]; $fdi.Write($b, 0, $b.Length); $fdi.Write([BitConverter]::GetBytes([int]$f[1]), 0, 4) }
    }
    $fdiAt = $data.Position
    $fdiBytes = $fdi.ToArray(); $data.Write($fdiBytes, 0, $fdiBytes.Length)
    $index = New-Object IO.MemoryStream
    $b = FStr "../../../"; $index.Write($b, 0, $b.Length)
    $index.Write([BitConverter]::GetBytes([int]$Files.Count), 0, 4)
    $index.Write([BitConverter]::GetBytes([long]0), 0, 8)
    $index.Write([BitConverter]::GetBytes([int]0), 0, 4)
    $index.Write([BitConverter]::GetBytes([int]1), 0, 4)
    $index.Write([BitConverter]::GetBytes([long]$fdiAt), 0, 8); $index.Write([BitConverter]::GetBytes([long]$fdiBytes.Length), 0, 8); $index.Write((New-Object byte[] 20), 0, 20)
    $enc = $encoded.ToArray(); $index.Write([BitConverter]::GetBytes([int]$enc.Length), 0, 4); $index.Write($enc, 0, $enc.Length)
    $index.Write([BitConverter]::GetBytes([int]0), 0, 4)
    $indexAt = $data.Position
    $ib = $index.ToArray(); $data.Write($ib, 0, $ib.Length)
    $footer = [byte[]]((New-Object byte[] 16) + @(0) + @([BitConverter]::GetBytes([uint32]0x5A6F12E1)) + @([BitConverter]::GetBytes([int]11)) + @([BitConverter]::GetBytes([long]$indexAt)) + @([BitConverter]::GetBytes([long]$ib.Length)) + (New-Object byte[] 20) + (New-Object byte[] 160))
    $data.Write($footer, 0, $footer.Length)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllBytes($Path, $data.ToArray())
}

# A WingmanSaveGame-shaped GVAS file: header, flat properties (FloatProperty / IntProperty / BoolProperty), "None".
function New-PwSave([string]$Path, [hashtable]$Floats) {
    $ascii = [Text.Encoding]::ASCII
    function FStr([string]$s) { $b = $ascii.GetBytes($s); return [byte[]](@([BitConverter]::GetBytes($b.Length + 1)) + $b + @(0)) }
    $m = New-Object IO.MemoryStream
    $w = { param([byte[]]$b) $m.Write($b, 0, $b.Length) }
    & $w ($ascii.GetBytes("GVAS")); & $w ([BitConverter]::GetBytes([int]2)); & $w ([BitConverter]::GetBytes([int]522))
    & $w ([BitConverter]::GetBytes([int16]4)); & $w ([BitConverter]::GetBytes([int16]27)); & $w ([BitConverter]::GetBytes([int16]2)); & $w ([BitConverter]::GetBytes([int]0))
    & $w (FStr "++UE4+Release-4.27"); & $w ([BitConverter]::GetBytes([int]3)); & $w ([BitConverter]::GetBytes([int]0))
    & $w (FStr "/Game/ProjectWingman/Blueprints/Instance/WingmanSaveGame.WingmanSaveGame_C")
    & $w (FStr "MasterAudio"); & $w (FStr "FloatProperty"); & $w ([BitConverter]::GetBytes([long]4)); $m.WriteByte(0); & $w ([BitConverter]::GetBytes([single]0.5))
    foreach ($k in ($Floats.Keys | Sort-Object)) { & $w (FStr $k); & $w (FStr "FloatProperty"); & $w ([BitConverter]::GetBytes([long]4)); $m.WriteByte(0); & $w ([BitConverter]::GetBytes([single]$Floats[$k])) }
    & $w (FStr "CameraID"); & $w (FStr "IntProperty"); & $w ([BitConverter]::GetBytes([long]4)); $m.WriteByte(0); & $w ([BitConverter]::GetBytes([int]2))
    & $w (FStr "PovWeapon1"); & $w (FStr "BoolProperty"); & $w ([BitConverter]::GetBytes([long]0)); $m.WriteByte(1); $m.WriteByte(0)
    & $w (FStr "None"); & $w ([BitConverter]::GetBytes([int]0))
    [IO.File]::WriteAllBytes($Path, $m.ToArray())
}

function Get-PwSaveFloat([string]$Path, [string]$Name) {
    $d = [IO.File]::ReadAllBytes($Path)
    $tag = [BitConverter]::ToString([byte[]](@([BitConverter]::GetBytes($Name.Length + 1)) + [Text.Encoding]::ASCII.GetBytes($Name) + @(0)))
    $at = ([BitConverter]::ToString($d)).IndexOf($tag)
    if ($at -lt 0) { return $null }
    return [BitConverter]::ToSingle($d, $at / 3 + $Name.Length + 5 + 18 + 9)
}

$pwInstall = Join-Path $pwDir "install"
New-PwPak (Join-Path $pwInstall "ProjectWingman\Content\Paks\pakchunk0-WindowsNoEditor.pak") @{
    "ProjectWingman/Config/DefaultInput.ini" = [IO.File]::ReadAllText((Join-Path $pwFixture "DefaultInput.ini"))
    "ProjectWingman/Config/DefaultGame.ini" = "[/Script/EngineSettings.GeneralProjectSettings]`r`nProjectName=Synthetic`r`n"
}
New-PwSave $pwSave @{ PitchCurve = 0.71; PitchDeadzone = 0.39 }
try {
    Run-Engine @("games", "install", "set", "ProjectWingman", $pwInstall, "--text") | Out-Null

    # ---- 1. catalog from the pak
    $pwRefresh = Run-Engine @("pw", "refresh-db", (Join-Path $pwInstall "ProjectWingman\Content\Paks\pakchunk0-WindowsNoEditor.pak"), "--text")
    Assert-True ($pwRefresh.ExitCode -eq 0) "pw refresh-db failed. Output: $($pwRefresh.Output)"
    $pwCatalog = [System.IO.File]::ReadAllText((Join-Path $pwCatalogDir "actions.tsv"))
    Assert-Match $pwCatalog "(?m)^Pitch Axis\tPitch\taxis\tFlight axes" "Pitch must be an axis."
    Assert-Match $pwCatalog "(?m)^Gun Fire\tFire gun\tbutton\tWeapons" "Gun Fire must be a button."
    Assert-Match $pwCatalog "(?m)^View Padlock X Axis\t[^\t]+\taxis\tView" "The padlock axis must be a view axis."
    Assert-NotMatch $pwCatalog "Menu Up|Menu Select|Mouse X Axis" "Menu and mouse rows are not flight actions."

    # ---- 2. scan
    $pwScan = Run-Engine @("--profile", $pwProfile, "pw", "scan", $pwIni, "--text")
    Assert-True ($pwScan.ExitCode -eq 0) "pw scan failed. Output: $($pwScan.Output)"
    Assert-Match $pwScan.Output "Pitch Axis <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Y" "Joystick_VJStick_2_Axis1 must read as the stick's Y."
    Assert-Match $pwScan.Output "Roll Axis <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_RZ" "Axis2 of a stick with X,Y,RZ must read as RZ."
    Assert-Match $pwScan.Output "Missile Launch <- VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN5" "Button4 must read as JOY_BTN5 (the game counts from 0)."
    Assert-Match $pwScan.Output "Weapon 2 <- VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN1" "A unique device with a moved number must still be read."
    Assert-Match $pwScan.Output "moved in the device list \(#5 -> #1\)" "A moved device number must be reported."
    Assert-Match $pwScan.Output "no connected device is called 'Foo'" "A key of an unknown device must be reported."
    $pwImport = Run-Engine @("--profile", (Join-Path $pwDir "import.pr0file"), "pw", "import", $pwIni, "--text")
    Assert-True ($pwImport.ExitCode -eq 0) "pw import failed. Output: $($pwImport.Output)"
    $pwShow = Run-Engine @("--profile", (Join-Path $pwDir "import.pr0file"), "binds", "tuning", "show", "Roll", "--game", "ProjectWingman", "--text")
    Assert-Match $pwShow.Output "inv=1" "Scale -1 must import as inverted."
    $pwShowPitch = Run-Engine @("--profile", (Join-Path $pwDir "import.pr0file"), "binds", "tuning", "show", "Pitch", "--game", "ProjectWingman", "--text")
    Assert-Match $pwShowPitch.Output "n\.curve=1\.42" "The save's PitchCurve 0.71 must import as the menu's curve 1.42."
    Assert-Match $pwShowPitch.Output "n\.deadZone=0\.39" "The save's PitchDeadzone must import."

    # ---- 3. export
    Run-Engine @("--profile", $pwProfile, "relations", "add", "Pitch", "--axis", "--node", "ProjectWingman:Pitch Axis", "--device", $pwThrottle, "--input", "JOY_SLIDER1", "--text") | Out-Null
    Run-Engine @("--profile", $pwProfile, "binds", "tuning", "set", "Pitch", "--scope", "game:ProjectWingman", "--inverted", "true", "--param", "curve=1.37", "--param", "deadZone=0.33", "--text") | Out-Null
    Run-Engine @("--profile", $pwProfile, "relations", "add", "Gun", "--button", "--node", "ProjectWingman:Gun Fire", "--device", $pwStick, "--input", "JOY_BTN10", "--text") | Out-Null
    Run-Engine @("--profile", $pwProfile, "relations", "add", "Weapon hat", "--button", "--node", "ProjectWingman:Weapon 2", "--device", $pwStick, "--input", "JOY_BTN_POV1_U", "--text") | Out-Null
    Run-Engine @("--profile", $pwProfile, "relations", "add", "Yaw", "--axis", "--node", "ProjectWingman:Yaw Axis", "--device", $pwStick, "--input", "JOY_RZ", "--text") | Out-Null
    $pwExport = Run-Engine @("--profile", $pwProfile, "pw", "export", $pwIni, "--text")
    Assert-True ($pwExport.ExitCode -eq 0) "pw export failed. Output: $($pwExport.Output)"
    $pwText = [System.IO.File]::ReadAllText($pwIni)
    Assert-Match $pwText "AxisMappings=\(AxisName=""Pitch Axis"",Scale=-1\.000000,Key=Joystick_VJThrottle_1_Axis6\)" "Inverted Slider1 of the throttle (8 axes) must be Axis6 with Scale -1."
    Assert-Match $pwText "AxisMappings=\(AxisName=""Pitch Axis"",Scale=1\.000000,Key=Joystick_VJStick_2_Axis1\)" "Pitch on another device must stay."
    Assert-Match $pwText "ActionMappings=\(ActionName=""Gun Fire"",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Joystick_VJStick_2_Button9\)" "JOY_BTN10 must be Button9."
    Assert-Match $pwText "AxisMappings=\(AxisName=""Yaw Axis"",Scale=1\.000000,Key=Joystick_VJStick_2_Axis2\)" "Yaw must be the stick's Axis2."
    Assert-Match $pwText "Key=Joystick_Foo_7_Axis0" "A mapping of another device must stay (merge-overwrite)."
    Assert-Match $pwText "Key=Joystick_VJThrottle_5_Button0" "Weapon 2 is not exported (a hat): its line stays."
    Assert-Match $pwText "(?s)\[Internationalization\]\s+Culture=en-US" "Other sections must stay."
    Assert-Match $pwExport.Output "Weapon 2 <- .*JOY_BTN_POV1_U: the game takes hat switches only through" "A hat must be refused with the game's own way."
    Assert-True ([Math]::Abs((Get-PwSaveFloat $pwSave "PitchCurve") - 0.685) -lt 0.0005) "PitchCurve must be curve / 2 (in place)."
    Assert-True ([Math]::Abs((Get-PwSaveFloat $pwSave "PitchDeadzone") - 0.33) -lt 0.0005) "PitchDeadzone must be written in place."
    Assert-True ($null -ne (Get-PwSaveFloat $pwSave "YawCurve")) "YawCurve must be added before None."
    Assert-True ([Math]::Abs((Get-PwSaveFloat $pwSave "MasterAudio") - 0.5) -lt 0.0005) "Other save properties must stay."
    Assert-True (([IO.File]::ReadAllBytes($pwSave) | Select-Object -Last 4 | Measure-Object -Sum).Sum -eq 0) "The save must still end with None and four zero bytes."
    $pwAgain = Run-Engine @("--profile", $pwProfile, "pw", "export", $pwIni, "--text")
    Assert-Match $pwAgain.Output "Input.ini is already up to date" "A second export must change nothing."
    Assert-Match $pwAgain.Output "curves and dead zones are already up to date" "A second export must not touch the save."

    # merge keeps the game's own joystick bind of an action on that device
    Copy-Item (Join-Path $pwFixture "Input.ini") $pwIni -Force
    Run-Engine @("--profile", $pwProfile, "relations", "add", "Roll", "--axis", "--node", "ProjectWingman:Roll Axis", "--device", $pwStick, "--input", "JOY_X", "--text") | Out-Null
    $pwMerge = Run-Engine @("--profile", $pwProfile, "pw", "export", $pwIni, "--mode", "merge", "--text")
    Assert-Match $pwMerge.Output "the game already has Roll Axis on Joystick_VJStick_2_Axis2 \(merge keeps it\)" "Merge must keep the game's bind."
    Assert-NotMatch ([IO.File]::ReadAllText($pwIni)) "Joystick_VJStick_2_Axis0" "Merge must not add a second stick axis to Roll."

    # clean: the other joystick lines of the exported devices go, other devices stay
    Copy-Item (Join-Path $pwFixture "Input.ini") $pwIni -Force
    $pwClean = Run-Engine @("--profile", $pwProfile, "pw", "export", $pwIni, "--mode", "clean", "--text")
    Assert-True ($pwClean.ExitCode -eq 0) "pw export --mode clean failed. Output: $($pwClean.Output)"
    $pwText = [IO.File]::ReadAllText($pwIni)
    Assert-NotMatch $pwText "Joystick_VJStick_2_Axis1\)" "Clean must drop the stick's old Pitch line."
    Assert-NotMatch $pwText "Joystick_VJThrottle_1_Button4" "Clean must drop the throttle's old Missile line."
    Assert-Match $pwText "Joystick_Foo_7_Axis0" "Clean must keep a device JoyPro does not export."
    Assert-Match $pwText "Key=SpaceBar" "Clean must keep keyboard keys."

    # ---- 4. no Input.ini yet: the pak's defaults come first
    $pwNew = Join-Path $pwDir "new\Input.ini"
    $pwFresh = Run-Engine @("--profile", $pwProfile, "pw", "export", $pwNew, "--text")
    Assert-True ($pwFresh.ExitCode -eq 0) "pw export into a missing Input.ini failed. Output: $($pwFresh.Output)"
    $pwText = [IO.File]::ReadAllText($pwNew)
    Assert-Match $pwFresh.Output "the game's defaults from its pak were written first" "The defaults step must be reported."
    Assert-Match $pwText "(?m)^\[/Script/Engine\.InputSettings\]" "The section must be written."
    Assert-Match $pwText "(?m)^ActionMappings=\(ActionName=""Change Camera"",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=C\)" "The pak's keyboard binds must be written as plain lines."
    Assert-Match $pwText "(?m)^AxisMappings=\(AxisName=""Pitch Axis"",Scale=-1\.000000,Key=S\)" "The pak's axis binds must be written."
    Assert-NotMatch $pwText "Gamepad_FaceButton_Right\)" "Lines the pak removes must not be written."
    Assert-Match $pwText "(?m)^ActionMappings=\(ActionName=""Change Camera"",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=Android_Menu\)" "The Android keys stay (the menu's empty slots)."
    Assert-Match $pwText "Key=Joystick_VJThrottle_1_Axis6\)" "JoyPro's binds must follow."
}
finally {
    Run-Engine @("games", "install", "clear", "ProjectWingman", "--text") | Out-Null
    $env:JOYPRO_FAKE_DEVICES = $pwPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $pwPrevious.Detection
    $env:JOYPRO_PW_SAVEGAME = $pwPrevious.Save
    if (Test-Path $pwCatalogDir) { Remove-Item -Path $pwCatalogDir -Recurse -Force }
    if ($pwHadCatalog) { Copy-Item -Path $pwCatalogBackup -Destination $pwCatalogDir -Recurse -Force }
}
