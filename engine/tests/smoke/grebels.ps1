# G-Rebels: axis roles edited in place in SaveOptions.sav (UE 5.8 GVAS, UE 5.4+ property tags) and button binds in
# EnhancedInputUserSettings.sav. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).
# The SaveOptions.sav here is synthetic, built in the shape the game wrote on the dev PC (2026-10-11): the Blueprint
# class path, AxisOptions = S_Input_Axis_Save { Key (FKey: KeyName), Offset, Multiplier, Role (E_Input_Axis_Roles
# enum name), Inverted (bool in the tag flags), DeadZone } and Joysticks = S_Controller_Save { DeviceID (HID path with
# one NUL too many, as the game writes it), Enabled, MultiID }. The button file is the Hell Let Loose: Vietnam fixture's
# synthetic EnhancedInputUserSettings.sav. Devices: a temp fake device list with HID paths (8th column).
$grDir = Join-Path $tempDir "grebels"
New-Item -ItemType Directory -Force -Path $grDir | Out-Null
$grAscii = [System.Text.Encoding]::GetEncoding(28591)

function New-GrString([string]$Text, [int]$ExtraNul = 0) {
    $bytes = $grAscii.GetBytes($Text)
    return , [byte[]]([BitConverter]::GetBytes([int]($bytes.Length + 1 + $ExtraNul)) + $bytes + [byte[]](@(0) * (1 + $ExtraNul)))
}
# A list of byte arrays (PowerShell would unroll a single array in @()).
function L { return , ([object[]]$args) }
# Type tree node: name, count of inner nodes, the inner nodes.
function New-GrType([string]$Name, [object[]]$Inner = @()) {
    $out = [byte[]]((New-GrString $Name) + [BitConverter]::GetBytes([int]$Inner.Count))
    foreach ($node in $Inner) { $out += [byte[]]$node }
    return , [byte[]]$out
}
function New-GrTag([string]$Name, [byte[]]$Type, [byte[]]$Value, [byte]$Flags = 0) {
    return , [byte[]]((New-GrString $Name) + $Type + [BitConverter]::GetBytes([int]$Value.Length) + [byte[]]@($Flags) + $Value)
}
$grNone = New-GrString "None"
function New-GrAxis([string]$Key, [int]$Role, [bool]$Inverted) {
    $keyValue = [byte[]]((New-GrTag "KeyName" (New-GrType "NameProperty") (New-GrString $Key)) + $grNone)
    $roleType = New-GrType "ByteProperty" (L (New-GrType "E_Input_Axis_Roles" (L (New-GrType "/Game/BASE/VARIABLES/ENUMS/E_Input_Axis_Roles"))))
    $double = New-GrType "DoubleProperty"
    $tags = (New-GrTag "Key_2_1384EE0A45715F227E279F88E5FE5CB3" (New-GrType "StructProperty" (L (New-GrType "Key" (L (New-GrType "/Script/InputCore"))))) $keyValue) +
            (New-GrTag "Offset_9_6E13B6574A65F8AF61137EB9E3BF3927" $double ([BitConverter]::GetBytes([double]0))) +
            (New-GrTag "Multiplier_10_F0E607324C4C9BC3A1CEB2826150FAD1" $double ([BitConverter]::GetBytes([double]1))) +
            (New-GrTag "Role_8_F627096D47FE64705D167592F20AD363" $roleType (New-GrString ("E_Input_Axis_Roles::NewEnumerator" + $Role))) +
            (New-GrTag "Inverted_12_D399D2254AAE65AF29314B81BC27C58B" (New-GrType "BoolProperty") ([byte[]]@()) $(if ($Inverted) { 0x10 } else { 0 })) +
            (New-GrTag "DeadZone_15_6BD3F7894E8E9C7BCEF3D9B12C9D6057" $double ([BitConverter]::GetBytes([double]0.2))) + $grNone
    return , [byte[]]$tags
}
function New-GrDevice([string]$Path, [bool]$Enabled, [int]$Channel) {
    return , [byte[]]((New-GrTag "DeviceID_3_59DBD5F947A6E68E4B9E09BAC95B6DE5" (New-GrType "StrProperty") (New-GrString $Path 1)) +
                    (New-GrTag "Enabled_4_666B8A784D8D57C2CE223DA0EAE5B3A6" (New-GrType "BoolProperty") ([byte[]]@()) $(if ($Enabled) { 0x10 } else { 0 })) +
                    (New-GrTag "MultiID_8_5E259A8940A0E1751B3C24BB2BF07605" (New-GrType "IntProperty") ([BitConverter]::GetBytes([int]$Channel))) + $grNone)
}
function New-GrArray([string]$Name, [string]$Struct, [object[]]$Elements) {
    $value = [byte[]][BitConverter]::GetBytes([int]$Elements.Count)
    foreach ($e in $Elements) { $value += [byte[]]$e }
    $type = New-GrType "ArrayProperty" (L (New-GrType "StructProperty" (L (New-GrType $Struct (L (New-GrType ("/Game/BASE/VARIABLES/STRUCTURES/" + $Struct)))) (New-GrType "c956408c-4595-369d-79e7-c784b08f34f7"))))
    return , (New-GrTag $Name $type $value)
}

$grVjoyPath = "\\?\HID#HIDCLASS#1&5m0ke&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"
$grStickPath = "\\?\HID#VID_ABCD&PID_0001#c&5m0ke&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"
$grPedalsPath = "\\?\HID#VID_ABCD&PID_0002#c&5m0ke&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"
$grAxes = L (New-GrAxis "MouseX" 0 $false) (New-GrAxis "Gamepad_LeftY" 1 $false)
foreach ($name in @("X", "Y", "Z", "Rx", "Ry", "Rz", "Slider", "Dial")) {
    $grAxes += , (New-GrAxis ("RawInput_Controller0_Axis" + $name) $(if ($name -eq "Y") { 3 } elseif ($name -eq "Rz") { 0 } else { 6 }) $false)
    $grAxes += , (New-GrAxis ("RawInput_Controller1_Axis" + $name) $(if ($name -eq "X") { 2 } else { 6 }) $false)
}
$grBody = [byte[]]((New-GrArray "AxisOptions" "S_Input_Axis_Save" $grAxes) +
                   (New-GrArray "Joysticks" "S_Controller_Save" (L (New-GrDevice $grVjoyPath $true 0) (New-GrDevice $grStickPath $true 1) (New-GrDevice $grPedalsPath $false 0))) + $grNone + [byte[]]@(0, 0, 0, 0))
$grHeader = [byte[]]($grAscii.GetBytes("GVAS") + [byte[]](@(3, 0, 0, 0) + @(0) * 24) + (New-GrString "++UE5+Release-5.8") + [byte[]](@(0) * 8))
$grFile = [byte[]]($grHeader + (New-GrString "/Game/BASE/UI/STARTMENU/Blueprints/SaveFiles/SaveOptions.SaveOptions_C") + [byte[]]@(0) + $grBody)
$grFixture = Join-Path $grDir "fixture"
New-Item -ItemType Directory -Force -Path $grFixture | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $grFixture "SaveOptions.sav"), $grFile)
Copy-Item (Join-Path $engineDir "tests\fixtures\hllv\EnhancedInputUserSettings.sav") (Join-Path $grFixture "EnhancedInputUserSettings.sav")
$grDevices = Join-Path $grDir "devices.tsv"
[System.IO.File]::WriteAllText($grDevices, ("# JoyPro device`tVID`tPID`tHID product`taxes`tbuttons`thats`tHID path`r`n" +
    "vJoy Device {89d63500-b53f-11f1-8002-444553540000}`t1234`tBEAD`tvJoy - Virtual Joystick`tX,Y,Z,RX,RY,RZ,SLIDER1,SLIDER2`t32`t4`t" + $grVjoyPath.ToLowerInvariant() + "`r`n" +
    "GR Stick {77777777-0000-0000-0000-000000000001}`tABCD`t0001`tGR Stick`tX,Y,RZ`t16`t1`t" + $grStickPath + "`r`n" +
    "GR Pedals {77777777-0000-0000-0000-000000000002}`tABCD`t0002`tGR Pedals`tX,Y,RZ`t0`t0`t" + $grPedalsPath + "`r`n"), (New-Object System.Text.UTF8Encoding $false))
function New-GrCopy([string]$Name) {
    $dir = Join-Path $grDir $Name
    Copy-Item $grFixture $dir -Recurse -Force
    return (Join-Path $dir "SaveOptions.sav")
}

$grPreviousDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = $grDevices
$grVjoy = "vJoy Device {89d63500-b53f-11f1-8002-444553540000}"
$grStick = "GR Stick {77777777-0000-0000-0000-000000000001}"
try {
    $grCatalog = Run-Engine @("grebels", "catalog", "--text")
    Assert-Match $grCatalog.Output "(?m)^\s+- Axis_Pitch \| axis \| Axis roles \| Pitch" "the G-Rebels catalog should have the axis roles."
    # ---- scan: roles on enabled channels; the disabled pedals and the mouse/gamepad rows are not JoyPro's -------------
    $grScanPath = New-GrCopy "scan"
    $grScan = Run-Engine @("grebels", "scan", $grScanPath, "--text")
    Assert-True ($grScan.ExitCode -eq 0) "grebels scan failed. Output: $($grScan.Output)"
    Assert-Match $grScan.Output "Axis_Pitch <- vJoy Device \{[^}]+\} JOY_Y" "scan should read Pitch on vJoy Y (channel 0)."
    Assert-Match $grScan.Output "Axis_Yaw <- vJoy Device \{[^}]+\} JOY_RZ" "scan should read Yaw on vJoy Rz."
    Assert-Match $grScan.Output "Axis_Roll <- GR Stick \{[^}]+\} JOY_X" "scan should read Roll on the stick (channel 1)."
    Assert-NotMatch $grScan.Output "Axis_Thrust" "the gamepad row (Gamepad_LeftY) is not JoyPro's."

    # ---- export (merge-overwrite): roles, invert and a button; in place: the file keeps its length ---------------------
    $grProfile = Join-Path $grDir "export.pr0file"
    Run-Engine @("--profile", $grProfile, "relations", "add", "GR Pitch", "--axis", "--node", "GRebels:Axis_Pitch", "--device", $grVjoy, "--input", "JOY_RY", "--text") | Out-Null
    Run-Engine @("--profile", $grProfile, "binds", "set", "GR Pitch", "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $grProfile, "relations", "add", "GR Fire", "--button", "--node", "GRebels:PrimaryFire", "--device", $grVjoy, "--input", "JOY_BTN7", "--text") | Out-Null
    Run-Engine @("--profile", $grProfile, "relations", "add", "GR Thrust", "--axis", "--node", "GRebels:Axis_Thrust", "--device", "GR Pedals {77777777-0000-0000-0000-000000000002}", "--input", "JOY_X", "--text") | Out-Null
    $grPath = New-GrCopy "export"
    $grBefore = [System.IO.File]::ReadAllBytes($grPath)
    $grDry = Run-Engine @("--profile", $grProfile, "grebels", "export", $grPath, "--dry-run", "--text")
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($grPath)) -eq [Convert]::ToBase64String($grBefore)) "a grebels dry run must not write."
    $grExport = Run-Engine @("--profile", $grProfile, "grebels", "export", $grPath, "--text")
    Assert-True ($grExport.ExitCode -eq 0) "grebels export failed. Output: $($grExport.Output)"
    Assert-Match $grExport.Output "not switched on in the game" "a device without a channel (the pedals) must be skipped with a note."
    $grAfter = [System.IO.File]::ReadAllBytes($grPath)
    Assert-True ($grAfter.Length -eq $grBefore.Length) "the export edits SaveOptions.sav in place (same length)."
    $grDiff = 0; for ($i = 0; $i -lt $grAfter.Length; $i++) { if ($grAfter[$i] -ne $grBefore[$i]) { $grDiff++ } }
    Assert-True ($grDiff -eq 3) ("the export changes exactly three bytes (Ry role, Ry invert flag, Y role to None); changed: " + $grDiff)
    Assert-True (Test-Path ($grPath + ".jpbak")) "grebels export should back up SaveOptions.sav first."
    $grBack = Run-Engine @("grebels", "scan", $grPath, "--text")
    Assert-Match $grBack.Output "Axis_Pitch <- vJoy Device \{[^}]+\} JOY_RY" "Pitch should read back on Ry."
    Assert-NotMatch $grBack.Output "Axis_Pitch <- vJoy Device \{[^}]+\} JOY_Y" "merge-overwrite gives the old Pitch axis (Y) the role None."
    Assert-Match $grBack.Output "Axis_Yaw <- vJoy Device \{[^}]+\} JOY_RZ" "merge-overwrite keeps the other roles of the channel."
    Assert-Match $grBack.Output "PrimaryFire <- vJoy Device \{[^}]+\} JOY_BTN7" "the button should read back (RawInput_Controller0_Button7)."
    $grImportProfile = Join-Path $grDir "import.pr0file"
    $grImport = Run-Engine @("--profile", $grImportProfile, "grebels", "import", $grPath, "--text")
    Assert-True ($grImport.ExitCode -eq 0) "grebels import failed. Output: $($grImport.Output)"
    $grList = Run-Engine @("--profile", $grImportProfile, "relations", "list", "--text")
    Assert-Match $grList.Output "GR Pitch \|.*inverted|GR Pitch \|[^\n]*JOY_RY" "the import should bring JoyPro's relation back."
    $grAgain = Run-Engine @("--profile", $grProfile, "grebels", "export", $grPath, "--text")
    Assert-Match $grAgain.Output "already match" "a second export must change nothing."

    # ---- clean: every other role of the written channel goes to None; channel 1 (not written) stays -------------------
    $grCleanPath = New-GrCopy "clean"
    $grClean = Run-Engine @("--profile", $grProfile, "grebels", "export", $grCleanPath, "--mode", "clean", "--text")
    Assert-True ($grClean.ExitCode -eq 0) "grebels clean export failed. Output: $($grClean.Output)"
    $grCleanScan = Run-Engine @("grebels", "scan", $grCleanPath, "--text")
    Assert-NotMatch $grCleanScan.Output "Axis_Yaw <- vJoy" "clean releases Yaw on the written vJoy channel."
    Assert-Match $grCleanScan.Output "Axis_Roll <- GR Stick" "clean keeps the stick (not written)."
    $grNukePath = New-GrCopy "nuke"
    Run-Engine @("--profile", $grProfile, "grebels", "export", $grNukePath, "--nuke-untouched-devices", "--text") | Out-Null
    Assert-NotMatch (Run-Engine @("grebels", "scan", $grNukePath, "--text")).Output "Axis_Roll <- GR Stick" "nuke releases the roles of the enabled channel JoyPro does not write."

    # ---- refresh-db on a fresh install (a release ships no DB\GRebels): JoyPro's own table is written to <data>\GameData
    $grFreshRoot = Join-Path $grDir "fresh-joypro"
    New-Item -ItemType Directory -Force -Path (Join-Path $grFreshRoot "DB\Functions"), (Join-Path $grFreshRoot "CleanProfile") | Out-Null
    $grPreviousData = $env:JOYPRO_DATA_DIR
    $env:JOYPRO_DATA_DIR = Join-Path $grDir "fresh-data"
    Push-Location $grFreshRoot
    try {
        $grRefresh = Run-Engine @("grebels", "refresh-db", "--text")
        Assert-True ($grRefresh.ExitCode -eq 0) "grebels refresh-db on a fresh install failed. Output: $($grRefresh.Output)"
        $grFreshList = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\GRebels\actions.tsv"))
        Assert-Match $grFreshList "Axis_Pitch\tPitch\taxis\tAxis roles\trole:3" "the fresh G-Rebels list should hold JoyPro's axis roles."
        Assert-Match $grFreshList "PrimaryFire\tPrimary Fire\tbutton\tButtons\tPRIMARYFIRE" "the fresh G-Rebels list should hold the button action."
        Assert-True (-not (Test-Path (Join-Path $grFreshRoot "DB\GRebels"))) "the refresh must not write into the program folder."
        $grRefreshAgain = Run-Engine @("grebels", "refresh-db", "--text")
        Assert-Match $grRefreshAgain.Output "nothing was written" "a second G-Rebels refresh must write nothing."
    }
    finally {
        Pop-Location
        $env:JOYPRO_DATA_DIR = $grPreviousData
    }
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $grPreviousDevices
}
