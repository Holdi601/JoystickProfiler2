# Tiny Combat Arena: Rewired joystick maps in Unity PlayerPrefs (HKCU\Software\TinyCombat\Arena), split over the Control
# Mapper's map categories. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin).
# The registry is the integration's fake-registry .json hook (value name -> base64 of the value bytes), built here in the
# shape the game wrote on a test PC (game 0.17.2.3, 2026-10-11): player "Player", one JoystickMap value plus its
# KnownActionIds per device and map category (0 System, 3 Flight, 8 Weapons, 11 Camera, 4 View Select, 9 Map UI),
# buttons with axisRange 1, a hat direction as element 160, a keyboard map, a calibration map and a DWORD setting. The
# binds are made up; no game data is copied. Devices: tests\fixtures\nuclearoption\devices.tsv (vJoy, Smoke Stick,
# Smoke Pedals, Smoke Panel).
$tcaDir = Join-Path $tempDir "tinycombatarena"
New-Item -Path $tcaDir -ItemType Directory -Force | Out-Null
$tcaVjoy = "vJoy Device {89d63500-b53f-11f1-8002-444553540000}"
$tcaStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$tcaZero = "00000000-0000-0000-0000-000000000000"
$tcaKnownIds = "0,5,1,2,3,4,6,7,9,10,11,12,13,14,90,15,99,16,17,52,53,54,56,58,59,61,121,122,123,64,65,68,71,119,118,72,81,93,94,95,97,96,82,84,89,100,101,103,106,107,108,109,110,111,112,127,128,113,114,116,124,125,126,129,130"

function Get-TcaValueName([string]$Key) {
    [uint64]$h = 5381
    foreach ($b in [System.Text.Encoding]::UTF8.GetBytes($Key)) { $h = ((($h * 33) -band [uint64]4294967295) -bxor [uint64]$b) }
    return $Key + "_h" + $h
}
function Get-TcaMapKey([string]$Identifier, [int]$Category, [switch]$Known) {
    return "RewiredSaveData|playerName=Player|dataType=ControllerMap" + $(if ($Known) { "_KnownActionIds" } else { "" }) + "|kv=2|controllerMapType=JoystickMap|categoryId=" + $Category +
           "|layoutId=0|hardwareGuid=" + $tcaZero + "|hardwareIdentifier=" + $Identifier + "|duplicate=0"
}
function Get-TcaElement([int]$Action, [int]$Type, [int]$Element, [int]$Range = 0, [string]$Invert = "false", [int]$Pole = 0) {
    return "<ActionElementMap><actionCategoryId>0</actionCategoryId><actionId>$Action</actionId><elementType>$Type</elementType><elementIdentifierId>$Element</elementIdentifierId><axisRange>$Range</axisRange><invert>$Invert</invert><axisContribution>$Pole</axisContribution><keyboardKeyCode>0</keyboardKeyCode><modifierKey1>0</modifierKey1><modifierKey2>0</modifierKey2><modifierKey3>0</modifierKey3><enabled>true</enabled></ActionElementMap>"
}
function Get-TcaMap([string]$Name, [int]$Category, [string]$Buttons, [string]$Axes) {
    $b = if ($Buttons) { "<buttonMaps>" + $Buttons + "</buttonMaps>" } else { "<buttonMaps />" }
    $a = if ($Axes) { "<axisMaps>" + $Axes + "</axisMaps>" } else { "<axisMaps />" }
    return '<?xml version="1.0" encoding="utf-16"?><JoystickMap dataVersion="2" hardwareGuid="' + $tcaZero + '" hardwareName="' + $Name +
           '" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:schemaLocation="http://guavaman.com/rewired http://guavaman.com/schemas/rewired/1.1/JoystickMap.xsd" xmlns="http://guavaman.com/rewired"><sourceMapId>-1</sourceMapId><categoryId>' +
           $Category + '</categoryId><layoutId>0</layoutId><name></name><hardwareGuid>' + $tcaZero + '</hardwareGuid><enabled>true</enabled>' + $b + $a + '</JoystickMap>'
}
function ConvertTo-TcaBase64([string]$Text) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Text) + [byte[]]@(0)) }

$tcaValues = [ordered]@{}
$tcaValues[(Get-TcaValueName "Screenmanager Resolution Width")] = [Convert]::ToBase64String([BitConverter]::GetBytes([int]3840))
$tcaValues[(Get-TcaValueName "RewiredSaveData|playerName=Player|dataType=InputBehavior|id=0")] = ConvertTo-TcaBase64 "<InputBehavior dataVersion=`"1`"><id>0</id></InputBehavior>"
$tcaDevices = @(
    @{ Id = "WindowsRawInputvJoy-VirtualJoystickBEAD1234"; Name = "vJoy - Virtual Joystick"; Maps = @{
        3 = @(((Get-TcaElement 6 1 34 1) + (Get-TcaElement 4 1 160 1)), (Get-TcaElement 1 0 0));   # the inner () first: ',' binds before '+'       # Gear on Button 3, Throttle Down on Hat 0 Up; Roll on X
        8 = @((Get-TcaElement 58 1 32 1), "");                                                         # Fire Gun on Button 1
        11 = @("", (Get-TcaElement 81 0 4)) } },                                                     # Pitch Camera on RY
    @{ Id = "WindowsRawInputSmokePedals0002ABCD"; Name = "Smoke Pedals"; Maps = @{ 3 = @("", (Get-TcaElement 2 0 5)); 8 = @("", "") } }   # Yaw on RZ
)
foreach ($device in $tcaDevices) {
    foreach ($category in @(0, 3, 11, 8, 4, 9)) {
        $parts = if ($device.Maps.ContainsKey($category)) { $device.Maps[$category] } else { @("", "") }
        if ($device.Id -match "Pedals" -and -not $device.Maps.ContainsKey($category)) { continue }
        $tcaValues[(Get-TcaValueName (Get-TcaMapKey $device.Id $category))] = ConvertTo-TcaBase64 (Get-TcaMap $device.Name $category $parts[0] $parts[1])
        $tcaValues[(Get-TcaValueName (Get-TcaMapKey $device.Id $category -Known))] = ConvertTo-TcaBase64 $tcaKnownIds
    }
}
$tcaKeyboard = Get-TcaValueName "RewiredSaveData|playerName=Player|dataType=ControllerMap|kv=2|controllerMapType=KeyboardMap|categoryId=3|layoutId=0|hardwareGuid=ae4830f9-63db-4d4c-90b3-1beb46ecaf49"
$tcaValues[$tcaKeyboard] = ConvertTo-TcaBase64 '<KeyboardMap dataVersion="2"><categoryId>3</categoryId><buttonMaps><ActionElementMap><actionId>0</actionId><keyboardKeyCode>119</keyboardKeyCode></ActionElementMap></buttonMaps></KeyboardMap>'
$tcaValues[(Get-TcaValueName ("RewiredSaveData|dataType=CalibrationMap|controllerType=Joystick|hardwareIdentifier=WindowsRawInputvJoy-VirtualJoystickBEAD1234|hardwareGuid=" + $tcaZero))] = ConvertTo-TcaBase64 '<CalibrationMap dataVersion="4"><axes /></CalibrationMap>'
$tcaFixture = Join-Path $tcaDir "registry.json"
[System.IO.File]::WriteAllText($tcaFixture, ($tcaValues | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

function Read-TcaRegistry([string]$Path) {
    $values = @{}
    $json = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $values[$p.Name] = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p.Value)).TrimEnd([char]0) }
    return $values
}
function New-TcaCopy([string]$Name) {
    $path = Join-Path $tcaDir ($Name + ".json")
    Copy-Item -Path $tcaFixture -Destination $path -Force
    return $path
}
function Get-TcaPart([int]$Action, [int]$Type, [int]$Element) {
    return "<actionId>$Action</actionId><elementType>$Type</elementType><elementIdentifierId>$Element</elementIdentifierId>"
}
$tcaVjoy3 = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputvJoy-VirtualJoystickBEAD1234" 3)
$tcaVjoy8 = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputvJoy-VirtualJoystickBEAD1234" 8)
$tcaVjoy11 = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputvJoy-VirtualJoystickBEAD1234" 11)
$tcaPedals3 = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputSmokePedals0002ABCD" 3)
$tcaStick8 = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputSmokeStick56781234" 8)
$tcaStick8Known = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputSmokeStick56781234" 8 -Known)
$tcaOriginal = Read-TcaRegistry $tcaFixture
foreach ($tcaKey in @($tcaOriginal.Keys | Where-Object { $_ -match 'dataType=ControllerMap\|.*JoystickMap' })) {
    $tcaXml = $tcaOriginal[$tcaKey]
    try { [void][xml]($tcaXml -replace '^<\?xml[^>]*\?>', '') } catch { throw ("The TCA fixture value " + $tcaKey + " is no XML: " + $tcaXml) }
}

$tcaPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\nuclearoption\devices.tsv"
try {
    $tcaIntegrations = Run-Engine @("games", "integrations", "--text")
    Assert-Match $tcaIntegrations.Output "TinyCombatArena" "games integrations should list Tiny Combat Arena."
    $tcaCatalog = Run-Engine @("tca", "catalog", "--text")
    Assert-Match $tcaCatalog.Output "(?m)^\s+- FireGun \| button \| Weapon Control \| Fire Gun" "the shipped Tiny Combat Arena catalog should have the game's actions."
    Assert-Match $tcaCatalog.Output "(?m)^\s+- Pitch\.Negative \| button \| Flight Control \| Down" "the Tiny Combat Arena catalog should have the pole rows of axis actions."
    Assert-NotMatch $tcaCatalog.Output "(?m)^\s+- Afterburner \|" "actions the player cannot assign stay out of the catalog."

    # ---- scan / import ------------------------------------------------------------------------------------------------
    $tcaScanPath = New-TcaCopy "scan"
    $tcaScan = Run-Engine @("tca", "scan", $tcaScanPath, "--text")
    Assert-True ($tcaScan.ExitCode -eq 0) "tca scan failed. Output: $($tcaScan.Output)"
    foreach ($tcaExpect in @("Roll <- vJoy Device \{89d63500-b53f-11f1-8002-444553540000\} JOY_X", "GearToggle <- vJoy Device \{[^}]+\} JOY_BTN3",
                             "ThrottleDown <- vJoy Device \{[^}]+\} JOY_BTN_POV1_U", "FireGun <- vJoy Device \{[^}]+\} JOY_BTN1",
                             "CameraPitch <- vJoy Device \{[^}]+\} JOY_RY", "Yaw <- Smoke Pedals \{[^}]+\} JOY_RZ")) {
        Assert-Match $tcaScan.Output $tcaExpect ("tca scan should read '" + $tcaExpect + "' (every map category). Output: " + $tcaScan.Output)
    }
    $tcaImportProfile = Join-Path $tcaDir "import.pr0file"
    $tcaImport = Run-Engine @("--profile", $tcaImportProfile, "tca", "import", $tcaScanPath, "--text")
    Assert-True ($tcaImport.ExitCode -eq 0) "tca import failed. Output: $($tcaImport.Output)"
    Assert-Match $tcaImport.Output "(?m)^Relations Imported:\s+[1-9]" "tca import should create relations."
    $tcaRound = Run-Engine @("--profile", $tcaImportProfile, "tca", "export", $tcaScanPath, "--mode", "clean", "--text")
    Assert-Match $tcaRound.Output "already match" "a clean export of the imported binds must change nothing (buttons compared without axisRange)."
    Assert-True ([System.IO.File]::ReadAllText($tcaScanPath) -eq [System.IO.File]::ReadAllText($tcaFixture)) "an export that changes nothing must not rewrite the values."

    # ---- clean export: vJoy (saved maps in several map categories) and Smoke Stick (no saved map) ----------------------
    $tcaProfile = Join-Path $tcaDir "export.pr0file"
    Run-Engine @("--profile", $tcaProfile, "relations", "add", "TCA Pitch", "--axis", "--node", "TinyCombatArena:Pitch", "--device", $tcaVjoy, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $tcaProfile, "binds", "set", "TCA Pitch", "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $tcaProfile, "relations", "add", "TCA Flares", "--button", "--node", "TinyCombatArena:Flares", "--device", $tcaVjoy, "--input", "JOY_BTN20", "--text") | Out-Null
    Run-Engine @("--profile", $tcaProfile, "relations", "add", "TCA Throttle Up", "--button", "--node", "TinyCombatArena:ThrottleUp", "--device", $tcaVjoy, "--input", "JOY_BTN_POV2_R", "--text") | Out-Null
    Run-Engine @("--profile", $tcaProfile, "relations", "add", "TCA Fire", "--button", "--node", "TinyCombatArena:FireGun", "--device", $tcaStick, "--input", "JOY_BTN1", "--text") | Out-Null
    Run-Engine @("--profile", $tcaProfile, "relations", "add", "TCA Diagonal", "--button", "--node", "TinyCombatArena:GearToggle", "--device", $tcaStick, "--input", "JOY_BTN_POV1_UR", "--text") | Out-Null
    $tcaCleanPath = New-TcaCopy "clean"
    $tcaDry = Run-Engine @("--profile", $tcaProfile, "tca", "export", $tcaCleanPath, "--mode", "clean", "--dry-run", "--text")
    Assert-True ($tcaDry.ExitCode -eq 0) "tca dry run failed. Output: $($tcaDry.Output)"
    Assert-True ([System.IO.File]::ReadAllText($tcaCleanPath) -eq [System.IO.File]::ReadAllText($tcaFixture)) "a tca dry run must not write."
    $tcaClean = Run-Engine @("--profile", $tcaProfile, "tca", "export", $tcaCleanPath, "--mode", "clean", "--text")
    Assert-True ($tcaClean.ExitCode -eq 0) "tca clean export failed. Output: $($tcaClean.Output)"
    Assert-Match $tcaClean.Output "only the four main hat directions" "a hat diagonal should be skipped with a warning."
    Assert-Match $tcaClean.Output "New joystick map for 'Smoke Stick' \(map category 8\)" "the export should report the new Weapons map."
    Assert-True (Test-Path ($tcaCleanPath + ".jpbak")) "tca export should back up the registry file first."
    $tcaAfter = Read-TcaRegistry $tcaCleanPath
    Assert-Match $tcaAfter[$tcaVjoy3] ((Get-TcaPart 0 0 1) + "<axisRange>0</axisRange><invert>true</invert>") "clean export should bind Pitch to Y, inverted, in the Flight map (category 3)."
    Assert-Match $tcaAfter[$tcaVjoy3] ((Get-TcaPart 3 1 165) + "<axisRange>1</axisRange>") "Throttle Up on hat 2 right = element 160 + 4 + 1, axisRange 1 like the game's mapper."
    Assert-NotMatch $tcaAfter[$tcaVjoy3] "<actionId>1</actionId>|<actionId>6</actionId>|<actionId>4</actionId>" "clean export should release Roll, Gear and Throttle Down from the vJoy Flight map."
    Assert-Match $tcaAfter[$tcaVjoy8] (Get-TcaPart 108 1 51) "clean export should bind Drop Flares to Button 20 (element 51) in the Weapons map (category 8)."
    Assert-NotMatch $tcaAfter[$tcaVjoy8] "<actionId>58</actionId>" "clean export should release Fire Gun from the vJoy Weapons map."
    Assert-NotMatch $tcaAfter[$tcaVjoy11] "<actionId>81</actionId>" "clean export (flight-only game) should release the camera bind of a written device."
    Assert-True ($tcaAfter[$tcaPedals3] -eq $tcaOriginal[$tcaPedals3]) "clean export must not touch a device it does not write (no nuke)."
    Assert-True ($tcaAfter[$tcaKeyboard] -eq $tcaOriginal[$tcaKeyboard]) "tca export must not touch keyboard maps."
    Assert-True ($tcaAfter.Keys.Count -eq ($tcaOriginal.Keys.Count + 2)) "the new device should add exactly one map and its known action ids."
    Assert-Match $tcaAfter[$tcaStick8] "<categoryId>8</categoryId>" "the new map belongs to the Weapons map category."
    Assert-Match $tcaAfter[$tcaStick8] 'hardwareName=" Smoke Stick "' "a new map's hardwareName is the HID product string (untrimmed)."
    Assert-Match $tcaAfter[$tcaStick8] (Get-TcaPart 58 1 32) "the new map should bind Fire Gun to Button 1 (element 32)."
    Assert-Match $tcaAfter[$tcaStick8Known] "^0,1,2,3,4,5,6,7," "a new map needs the known action ids."
    $tcaAgain = Run-Engine @("--profile", $tcaProfile, "tca", "export", $tcaCleanPath, "--mode", "clean", "--text")
    Assert-Match $tcaAgain.Output "already match" "a second clean export must change nothing."
    $tcaBack = Run-Engine @("tca", "scan", $tcaCleanPath, "--text")
    Assert-Match $tcaBack.Output "ThrottleUp <- vJoy Device \{[^}]+\} JOY_BTN_POV2_R" "the exported hat direction should read back."
    Assert-Match $tcaBack.Output "FireGun <- Smoke Stick \{[^}]+\} JOY_BTN1" "the new map should read back."

    # ---- nuke: unused devices lose JoyPro's actions; connected devices without saved maps get empty ones ---------------
    $tcaNukePath = New-TcaCopy "nuke"
    $tcaNuke = Run-Engine @("--profile", $tcaProfile, "tca", "export", $tcaNukePath, "--nuke-untouched-devices", "--text")
    Assert-True ($tcaNuke.ExitCode -eq 0) "tca nuke export failed. Output: $($tcaNuke.Output)"
    $tcaNuked = Read-TcaRegistry $tcaNukePath
    Assert-NotMatch $tcaNuked[$tcaPedals3] "<actionId>2</actionId>" "nuke should release the binds of an unused device."
    foreach ($category in @(0, 3, 11, 8, 4, 9)) {
        $panel = Get-TcaValueName (Get-TcaMapKey "WindowsRawInputSmokePanel0003ABCD" $category)
        Assert-True ($tcaNuked.ContainsKey($panel)) "nuke should give a connected device without saved maps an empty map in map category $category."
    }
    # merge-overwrite (the default): the game's other binds of the written device stay, also in other map categories.
    Assert-Match $tcaNuked[$tcaVjoy3] (Get-TcaPart 1 0 0) "merge-overwrite must keep Roll on X."
    Assert-Match $tcaNuked[$tcaVjoy11] (Get-TcaPart 81 0 4) "merge-overwrite must keep the camera bind."

    # ---- merge vs merge-overwrite: Fire Gun on Button 2 of vJoy (the game has it on Button 1) --------------------------
    $tcaMergeProfile = Join-Path $tcaDir "merge.pr0file"
    Run-Engine @("--profile", $tcaMergeProfile, "relations", "add", "TCA Fire 2", "--button", "--node", "TinyCombatArena:FireGun", "--device", $tcaVjoy, "--input", "JOY_BTN2", "--text") | Out-Null
    $tcaMergePath = New-TcaCopy "merge"
    $tcaMerge = Run-Engine @("--profile", $tcaMergeProfile, "tca", "export", $tcaMergePath, "--mode", "merge", "--text")
    Assert-Match $tcaMerge.Output "kept the game's bind \(merge mode\)" "merge should keep the game's Fire Gun bind."
    Assert-True ([System.IO.File]::ReadAllText($tcaMergePath) -eq [System.IO.File]::ReadAllText($tcaFixture)) "a merge that keeps every game bind must not write."
    $tcaOverwrite = Run-Engine @("--profile", $tcaMergeProfile, "tca", "export", $tcaMergePath, "--mode", "merge-overwrite", "--text")
    Assert-True ($tcaOverwrite.ExitCode -eq 0) "tca merge-overwrite export failed. Output: $($tcaOverwrite.Output)"
    $tcaOver = (Read-TcaRegistry $tcaMergePath)[$tcaVjoy8]
    Assert-Match $tcaOver (Get-TcaPart 58 1 33) "merge-overwrite should bind Fire Gun to Button 2."
    Assert-NotMatch $tcaOver (Get-TcaPart 58 1 32) "merge-overwrite should replace the game's Fire Gun on Button 1."

    $tcaBadPath = Run-Engine @("tca", "scan", "HKCU\Software\Shockfront\NuclearOption", "--text")
    Assert-True ($tcaBadPath.ExitCode -ne 0) "tca must refuse a registry key that is not the game's."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $tcaPreviousFakeDevices
}

# ---- refresh-db: a synthetic Arena_Data\sharedassets1.assets with the InputManager's layout of the game -------------
# (actions: id, name, type, 3 descriptive strings, 6 int32 with userAssignable at 4 and the category at 5; categories:
# 4 strings, int32 id, int32 userAssignable).
function Add-TcaString([System.IO.BinaryWriter]$Writer, [string]$Text, [long]$Start) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $Writer.Write([int]$bytes.Length)
    $Writer.Write($bytes)
    while ((($Writer.BaseStream.Position - $Start) % 4) -ne 0) { $Writer.Write([byte]0) }
}
$tcarDir = Join-Path $tempDir "tinycombatarena-refresh"
$tcarGame = Join-Path $tcarDir "game\Arena_Data"
$tcarRoot = Join-Path $tcarDir "joypro"
New-Item -Path $tcarGame, (Join-Path $tcarRoot "DB\TinyCombatArena"), (Join-Path $tcarRoot "CleanProfile") -ItemType Directory -Force | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $tcarGame "sharedassets0.assets"), [byte[]](1, 2, 3, 4))
$tcarStream = New-Object System.IO.MemoryStream
$tcarWriter = New-Object System.IO.BinaryWriter($tcarStream)
$tcarWriter.Write([byte[]](1, 2, 3, 4, 0, 0, 0, 0))
$tcarStart = $tcarStream.Position
$tcarWriter.Write([int]3)
$tcarWriter.Write([int]0); Add-TcaString $tcarWriter "Pitch" $tcarStart; $tcarWriter.Write([int]0)
Add-TcaString $tcarWriter "Pitch Axis" $tcarStart; Add-TcaString $tcarWriter "Nose Up" $tcarStart; Add-TcaString $tcarWriter "Nose Down" $tcarStart
foreach ($v in @(0, 0, 0, 0, 1, 1)) { $tcarWriter.Write([int]$v) }
$tcarWriter.Write([int]58); Add-TcaString $tcarWriter "FireGun" $tcarStart; $tcarWriter.Write([int]1)
Add-TcaString $tcarWriter "Shoot the gun" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart
foreach ($v in @(0, 0, 0, 0, 1, 7)) { $tcarWriter.Write([int]$v) }
$tcarWriter.Write([int]57); Add-TcaString $tcarWriter "OpenMenu" $tcarStart; $tcarWriter.Write([int]1)
Add-TcaString $tcarWriter "Fixed menu key" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart
foreach ($v in @(0, 0, 0, 0, 0, 11)) { $tcarWriter.Write([int]$v) }
$tcarWriter.Write([int]2)
Add-TcaString $tcarWriter "Flight Control" $tcarStart; Add-TcaString $tcarWriter "A sentence about flying" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart
$tcarWriter.Write([int]1); $tcarWriter.Write([int]1)
Add-TcaString $tcarWriter "Weapon Control" $tcarStart; Add-TcaString $tcarWriter "Guns" $tcarStart; Add-TcaString $tcarWriter "" $tcarStart; Add-TcaString $tcarWriter "Weap" $tcarStart
$tcarWriter.Write([int]7); $tcarWriter.Write([int]1)
$tcarWriter.Write([byte[]](9, 9, 9, 9))
$tcarWriter.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $tcarGame "sharedassets1.assets"), $tcarStream.ToArray())
$tcarPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $tcarDir "data"
Push-Location $tcarRoot
try {
    $tcar = Run-Engine @("tca", "refresh-db", (Join-Path $tcarDir "game"), "--text")
    Assert-True ($tcar.ExitCode -eq 0) ("tca refresh-db failed: " + $tcar.Output)
    Assert-Match $tcar.Output "Read 3 Rewired action\(s\) from .*sharedassets1\.assets \(1 not assignable" "tca refresh-db should find the InputManager in sharedassets1 and leave the fixed action out."
    # Written into the game-data folder (<data>\GameData), never into the program folder ($tcarRoot).
    $tcarCatalog = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\TinyCombatArena\actions.tsv"))
    Assert-True (-not (Test-Path (Join-Path $tcarRoot "DB\TinyCombatArena\actions.tsv"))) "tca refresh-db must not write into the program folder."
    Assert-Match $tcarCatalog "Pitch\tPitch Axis\taxis\tFlight Control\t0" "the refreshed catalog should name the category by its name and keep the Rewired id."
    Assert-Match $tcarCatalog "Pitch\.Positive\tNose Up\tbutton\tFlight Control\t0" "the refreshed catalog should have the poles of an axis action."
    Assert-Match $tcarCatalog "FireGun\tShoot the gun\tbutton\tWeapon Control\t58" "the refreshed catalog should read the category after a fourth string."
    Assert-NotMatch $tcarCatalog "OpenMenu" "an action the player cannot assign stays out."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\TinyCombatArena\actions.tsv")) -match "Drop Flares") "the shipped catalog must stay untouched."
    $tcarAgain = Run-Engine @("tca", "refresh-db", (Join-Path $tcarDir "game"), "--dry-run", "--text")
    Assert-True ($tcarAgain.ExitCode -eq 0) ("tca refresh-db dry run failed: " + $tcarAgain.Output)
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $tcarPreviousData
}
