# Nuclear Option: Rewired joystick maps in the registry (Unity PlayerPrefs). Dot-sourced by engine_smoke_test.ps1 (Run-Engine,
# Assert-*, $tempDir, $engineDir, $engineBin). The fixture registry.json is the integration's test hook (value name -> base64 of
# the value bytes) and never touches the real registry. Its vJoy map is the real map of the dev PC (game 0.34.2: the default
# generic map with Pitch rebound to RY in the game) plus Pan View (camera) on RZ, Map (gameplay) on Button 19 and
# Throttle.Negative on Button 14; Smoke Pedals has a saved map of its own, Smoke Stick and Smoke Panel have none.
$noFixture = Join-Path $engineDir "tests\fixtures\nuclearoption"
$noDir = Join-Path $tempDir "nuclearoption"
New-Item -Path $noDir -ItemType Directory -Force | Out-Null
$noVjoy = "vJoy Device {89d63500-b53f-11f1-8002-444553540000}"
$noStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$noMapPrefix = "RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=JoystickMap|categoryId=0|layoutId=0|hardwareGuid=00000000-0000-0000-0000-000000000000|hardwareIdentifier="
$noKnownPrefix = "RewiredSaveData|playerName=Player0|dataType=ControllerMap_KnownActionIds|kv=2|controllerMapType=JoystickMap|categoryId=0|layoutId=0|hardwareGuid=00000000-0000-0000-0000-000000000000|hardwareIdentifier="

# Unity's PlayerPrefs hash: djb2 with XOR over the UTF-8 key, unsigned 32 bit.
function Get-NoValueName([string]$Key) {
    [uint64]$h = 5381
    foreach ($b in [System.Text.Encoding]::UTF8.GetBytes($Key)) {
        $h = ((($h * 33) -band [uint64]4294967295) -bxor [uint64]$b)
    }
    return $Key + "_h" + $h
}

function Read-NoRegistry([string]$Path) {
    $values = @{}
    $json = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) {
        $values[$p.Name] = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p.Value)).TrimEnd([char]0)
    }
    return $values
}

function New-NoCopy([string]$Name) {
    $path = Join-Path $noDir ($Name + ".json")
    Copy-Item -Path (Join-Path $noFixture "registry.json") -Destination $path -Force
    return $path
}

function Get-NoElement([int]$Action, [int]$Type, [int]$Element) {
    return "<actionId>$Action</actionId><elementType>$Type</elementType><elementIdentifierId>$Element</elementIdentifierId>"
}

$noVjoyName = Get-NoValueName ($noMapPrefix + "WindowsRawInputvJoy-VirtualJoystickBEAD1234|duplicate=0")
Assert-True ($noVjoyName -eq ($noMapPrefix + "WindowsRawInputvJoy-VirtualJoystickBEAD1234|duplicate=0_h719594119")) "the PlayerPrefs hash of the vJoy map must be 719594119 (value seen in the game's registry)."
$noPedalsName = Get-NoValueName ($noMapPrefix + "WindowsRawInputSmokePedals0002ABCD|duplicate=0")
$noKeyboardName = Get-NoValueName "RewiredSaveData|playerName=Player0|dataType=ControllerMap|kv=2|controllerMapType=KeyboardMap|categoryId=0|layoutId=0|hardwareGuid=ae4830f9-63db-4d4c-90b3-1beb46ecaf49"
# HID product " Smoke Stick " (spaces kept by the fake device list): the identifier drops every space, hardwareName keeps them.
$noStickName = Get-NoValueName ($noMapPrefix + "WindowsRawInputSmokeStick56781234|duplicate=0")
$noStickKnown = Get-NoValueName ($noKnownPrefix + "WindowsRawInputSmokeStick56781234|duplicate=0")
$noPanelName = Get-NoValueName ($noMapPrefix + "WindowsRawInputSmokePanel0003ABCD|duplicate=0")
$noOriginal = Read-NoRegistry (Join-Path $noFixture "registry.json")
Assert-True ($noOriginal.ContainsKey($noVjoyName) -and $noOriginal.ContainsKey($noPedalsName) -and $noOriginal.ContainsKey($noKeyboardName)) "the Nuclear Option fixture must use Unity's value names."

$noPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $noFixture "devices.tsv"
try {
    $noIntegrations = Run-Engine @("games", "integrations", "--text")
    Assert-Match $noIntegrations.Output "NuclearOption" "games integrations should list Nuclear Option."
    $noCatalog = Run-Engine @("nuclearoption", "catalog", "--text")
    Assert-Match $noCatalog.Output "(?m)^\s+- Night Vis \| button \| Flight Controls \| Toggle Night Vision" "the shipped Nuclear Option catalog should have the game's actions."
    Assert-Match $noCatalog.Output "(?m)^\s+- Throttle\.Negative \| button \| Flight Controls \| Decrease Throttle" "the Nuclear Option catalog should have the pole rows of axis actions."

    # ---- scan / import ------------------------------------------------------------------------------------------------
    $noScanPath = New-NoCopy "scan"
    $noScan = Run-Engine @("nuclearoption", "scan", $noScanPath, "--text")
    Assert-True ($noScan.ExitCode -eq 0) "nuclearoption scan failed. Output: $($noScan.Output)"
    foreach ($noExpect in @("Pitch <- vJoy Device \{89d63500-b53f-11f1-8002-444553540000\} JOY_RY", "Roll <- vJoy Device \{[^}]+\} JOY_Y", "Throttle <- vJoy Device \{[^}]+\} JOY_RX",
                            "Fire <- vJoy Device \{[^}]+\} JOY_BTN1", "Eject <- vJoy Device \{[^}]+\} JOY_BTN11", "Pan View <- vJoy Device \{[^}]+\} JOY_RZ",
                            "Map <- vJoy Device \{[^}]+\} JOY_BTN19", "Throttle\.Negative <- vJoy Device \{[^}]+\} JOY_BTN14", "Yaw <- Smoke Pedals \{[^}]+\} JOY_X")) {
        Assert-Match $noScan.Output $noExpect ("nuclearoption scan should read '" + $noExpect + "'. Output: " + $noScan.Output)
    }
    Assert-NotMatch $noScan.Output "Brake <- vJoy" "the vJoy map has no Brake (the game replaced it when Pitch was rebound to RY)."

    $noImportProfile = Join-Path $noDir "import.pr0file"
    $noImport = Run-Engine @("--profile", $noImportProfile, "nuclearoption", "import", $noScanPath, "--text")
    Assert-True ($noImport.ExitCode -eq 0) "nuclearoption import failed. Output: $($noImport.Output)"
    Assert-Match $noImport.Output "(?m)^Relations Imported:\s+[1-9]" "nuclearoption import should create relations."
    $noRound = Run-Engine @("--profile", $noImportProfile, "nuclearoption", "export", $noScanPath, "--text")
    Assert-True ($noRound.ExitCode -eq 0) "nuclearoption export after the import failed. Output: $($noRound.Output)"
    Assert-Match $noRound.Output "already match" "a nuclearoption export right after the import must change nothing."
    $noRoundClean = Run-Engine @("--profile", $noImportProfile, "nuclearoption", "export", $noScanPath, "--mode", "clean", "--text")
    Assert-Match $noRoundClean.Output "already match" "a clean nuclearoption export of the imported binds must change nothing."
    Assert-True ([System.IO.File]::ReadAllText($noScanPath) -eq [System.IO.File]::ReadAllText((Join-Path $noFixture "registry.json"))) "an export that changes nothing must not rewrite the registry values."

    # ---- clean export: vJoy (saved map) and Smoke Stick (no saved map) ------------------------------------------------
    $noProfile = Join-Path $noDir "export.pr0file"
    Run-Engine @("--profile", $noProfile, "relations", "add", "NO Pitch", "--axis", "--node", "NuclearOption:Pitch", "--device", $noVjoy, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $noProfile, "binds", "set", "NO Pitch", "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $noProfile, "relations", "add", "NO Gear", "--button", "--node", "NuclearOption:Gear", "--device", $noVjoy, "--input", "JOY_BTN20", "--text") | Out-Null
    Run-Engine @("--profile", $noProfile, "relations", "add", "NO Fire", "--button", "--node", "NuclearOption:Fire", "--device", $noStick, "--input", "JOY_BTN1", "--text") | Out-Null
    Run-Engine @("--profile", $noProfile, "relations", "add", "NO Throttle Up", "--button", "--node", "NuclearOption:Throttle.Positive", "--device", $noStick, "--input", "JOY_BTN2", "--text") | Out-Null
    Run-Engine @("--profile", $noProfile, "relations", "add", "NO Hat", "--button", "--node", "NuclearOption:Radar", "--device", $noStick, "--input", "JOY_BTN_POV1_U", "--text") | Out-Null
    $noCleanPath = New-NoCopy "clean"
    $noDry = Run-Engine @("--profile", $noProfile, "nuclearoption", "export", $noCleanPath, "--mode", "clean", "--dry-run", "--text")
    Assert-True ($noDry.ExitCode -eq 0) "nuclearoption dry run failed. Output: $($noDry.Output)"
    Assert-True ([System.IO.File]::ReadAllText($noCleanPath) -eq [System.IO.File]::ReadAllText((Join-Path $noFixture "registry.json"))) "a nuclearoption dry run must not write."
    $noClean = Run-Engine @("--profile", $noProfile, "nuclearoption", "export", $noCleanPath, "--mode", "clean", "--text")
    Assert-True ($noClean.ExitCode -eq 0) "nuclearoption clean export failed. Output: $($noClean.Output)"
    Assert-Match $noClean.Output "hats are not written" "a hat bind should be skipped with a warning."
    Assert-Match $noClean.Output "New joystick map for 'Smoke Stick'" "the export should report the new map."
    Assert-True (Test-Path ($noCleanPath + ".jpbak")) "nuclearoption export should back up the registry file first."
    $noAfter = Read-NoRegistry $noCleanPath
    $noVjoyXml = $noAfter[$noVjoyName]
    Assert-Match $noVjoyXml ((Get-NoElement 0 0 1) + "<axisRange>0</axisRange><invert>true</invert>") "clean export should bind Pitch to Y, inverted."
    Assert-Match $noVjoyXml (Get-NoElement 11 1 51) "clean export should bind Gear to Button 20 (element 51)."
    Assert-NotMatch $noVjoyXml "<actionId>5</actionId>" "clean export should release Fire (a flight action) from the vJoy map."
    Assert-NotMatch $noVjoyXml (Get-NoElement 0 0 4) "clean export should release the game's Pitch on RY."
    Assert-NotMatch $noVjoyXml "<actionId>4</actionId>" "clean export should release Throttle and its pole buttons (flight actions)."
    Assert-Match $noVjoyXml (Get-NoElement 23 0 5) "clean export must keep the camera bind (Pan View on RZ)."
    Assert-Match $noVjoyXml (Get-NoElement 21 1 50) "clean export must keep the gameplay bind (Map on Button 19)."
    Assert-Match $noVjoyXml "<sourceMapId>2</sourceMapId>" "clean export must keep the map's sourceMapId."
    Assert-True ($noAfter[$noPedalsName] -eq $noOriginal[$noPedalsName]) "clean export must not touch the map of a device it does not write (no nuke)."
    Assert-True ($noAfter[$noKeyboardName] -eq $noOriginal[$noKeyboardName]) "nuclearoption export must not touch keyboard maps."
    Assert-True ($noAfter.Keys.Count -eq ($noOriginal.Keys.Count + 2)) "the new device should add exactly a map and its known action ids."
    Assert-True ($noAfter.ContainsKey($noStickName)) ("the new device's map must carry Unity's value name " + $noStickName)
    $noStickXml = $noAfter[$noStickName]
    Assert-Match $noStickXml 'hardwareName=" Smoke Stick "' "a new map's hardwareName is the HID product string as Rewired reports it (untrimmed)."
    Assert-Match $noStickXml "<sourceMapId>-1</sourceMapId>" "a new map has no source map."
    Assert-Match $noStickXml ("<buttonMaps>.*" + (Get-NoElement 5 1 32) + ".*</buttonMaps>") "the new map should bind Fire to Button 1 (element 32)."
    Assert-Match $noStickXml ((Get-NoElement 4 1 33) + "<axisRange>0</axisRange><invert>false</invert><axisContribution>0</axisContribution>") "the new map should bind Throttle.Positive to Button 2 (positive pole)."
    Assert-True ($noAfter[$noStickKnown] -eq "0,1,2,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,25,26,27,28,29,30,31,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64") "a new map needs the game's known action ids."
    $noAgain = Run-Engine @("--profile", $noProfile, "nuclearoption", "export", $noCleanPath, "--mode", "clean", "--text")
    Assert-Match $noAgain.Output "already match" "a second clean export must change nothing."
    $noBack = Run-Engine @("nuclearoption", "scan", $noCleanPath, "--text")
    Assert-Match $noBack.Output "Pitch <- vJoy Device \{[^}]+\} JOY_Y" "the exported Pitch should read back."
    Assert-Match $noBack.Output "Throttle\.Positive <- Smoke Stick \{[^}]+\} JOY_BTN2" "the exported pole button should read back."

    # ---- nuke: unused devices lose their flight binds; a connected device without a saved map gets an empty one -----
    $noNukePath = New-NoCopy "nuke"
    $noNuke = Run-Engine @("--profile", $noProfile, "nuclearoption", "export", $noNukePath, "--nuke-untouched-devices", "--text")
    Assert-True ($noNuke.ExitCode -eq 0) "nuclearoption nuke export failed. Output: $($noNuke.Output)"
    $noNuked = Read-NoRegistry $noNukePath
    Assert-NotMatch $noNuked[$noPedalsName] "<actionId>2</actionId>|<actionId>5</actionId>" "nuke should release the flight binds of an unused device."
    Assert-Match $noNuked[$noPedalsName] (Get-NoElement 23 0 1) "nuke must keep the camera bind of an unused device."
    Assert-True ($noNuked.ContainsKey($noPanelName)) "nuke should give a connected device without a saved map an empty map."
    Assert-Match $noNuked[$noPanelName] "<buttonMaps /><axisMaps /></JoystickMap>" "the nuke map of an unused device should be empty."
    # merge-overwrite (the default) on vJoy: the game's other flight binds stay.
    Assert-Match $noNuked[$noVjoyName] (Get-NoElement 5 1 32) "merge-overwrite must keep the game's flight binds JoyPro does not replace."
    Assert-NotMatch $noNuked[$noVjoyName] (Get-NoElement 0 0 4) "merge-overwrite should replace the game's Pitch bind of the device."

    # ---- merge vs merge-overwrite: Fire on Button 5 of vJoy (the game has Fire on Button 1, Next Countermeasure on 5) -
    $noMergeProfile = Join-Path $noDir "merge.pr0file"
    Run-Engine @("--profile", $noMergeProfile, "relations", "add", "NO Fire 5", "--button", "--node", "NuclearOption:Fire", "--device", $noVjoy, "--input", "JOY_BTN5", "--text") | Out-Null
    $noMergePath = New-NoCopy "merge"
    $noMerge = Run-Engine @("--profile", $noMergeProfile, "nuclearoption", "export", $noMergePath, "--mode", "merge", "--text")
    Assert-True ($noMerge.ExitCode -eq 0) "nuclearoption merge export failed. Output: $($noMerge.Output)"
    Assert-Match $noMerge.Output "kept the game's bind \(merge mode\)" "merge should keep the game's Fire bind."
    Assert-True ([System.IO.File]::ReadAllText($noMergePath) -eq [System.IO.File]::ReadAllText((Join-Path $noFixture "registry.json"))) "a merge that keeps every game bind must not write."
    $noOverwrite = Run-Engine @("--profile", $noMergeProfile, "nuclearoption", "export", $noMergePath, "--mode", "merge-overwrite", "--text")
    Assert-True ($noOverwrite.ExitCode -eq 0) "nuclearoption merge-overwrite export failed. Output: $($noOverwrite.Output)"
    $noOver = (Read-NoRegistry $noMergePath)[$noVjoyName]
    Assert-Match $noOver (Get-NoElement 5 1 36) "merge-overwrite should bind Fire to Button 5."
    Assert-NotMatch $noOver (Get-NoElement 5 1 32) "merge-overwrite should replace the game's Fire on Button 1."
    Assert-NotMatch $noOver "<actionId>10</actionId>" "merge-overwrite should release the flight action the game had on Button 5 (Next Countermeasure)."
    Assert-Match $noOver (Get-NoElement 7 1 33) "merge-overwrite must keep the other flight binds of the device."

    # ---- Auto Group: DB\Functions\NuclearOption.tsv --------------------------------------------------------------------
    $noAuto = Run-Engine @("--profile", (Join-Path $noDir "auto.pr0file"), "relations", "auto-group", "--planes", "NuclearOption", "--dry-run", "--text")
    Assert-True ($noAuto.ExitCode -eq 0) "relations auto-group for NuclearOption failed. Output: $($noAuto.Output)"
    Assert-Match $noAuto.Output "(?m)^Relations Created: [1-9]" "auto-group should create Nuclear Option relations."
    Assert-Match $noAuto.Output "Gear TOGGLE \(button, new, \+1 nodes\): NuclearOption" "auto-group should map the gear toggle of Nuclear Option."

    # ---- refusals ----------------------------------------------------------------------------------------------------
    $noBadPath = Run-Engine @("nuclearoption", "scan", "HKCU\Software\Somebody\Else", "--text")
    Assert-True ($noBadPath.ExitCode -ne 0) "nuclearoption must refuse a registry key that is not the game's."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $noPreviousFakeDevices
}

# ---- refresh-db: the Rewired action list from a synthetic resources.assets (count + 2 records) ----------------------
function Add-NoString([System.IO.BinaryWriter]$Writer, [string]$Text, [long]$Start) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $Writer.Write([int]$bytes.Length)
    $Writer.Write($bytes)
    while ((($Writer.BaseStream.Position - $Start) % 4) -ne 0) { $Writer.Write([byte]0) }
}
$norDir = Join-Path $tempDir "nuclearoption-refresh"
$norGame = Join-Path $norDir "game\NuclearOption_Data"
$norRoot = Join-Path $norDir "joypro"
New-Item -Path $norGame, (Join-Path $norRoot "DB\NuclearOption"), (Join-Path $norRoot "CleanProfile") -ItemType Directory -Force | Out-Null
$norStream = New-Object System.IO.MemoryStream
$norWriter = New-Object System.IO.BinaryWriter($norStream)
$norWriter.Write([byte[]](1, 2, 3, 4, 0, 0, 0, 0))
$norStart = $norStream.Position
$norWriter.Write([int]2)
$norWriter.Write([int]0); Add-NoString $norWriter "Pitch" $norStart; $norWriter.Write([int]0)
Add-NoString $norWriter "Pitch Axis" $norStart; Add-NoString $norWriter "Pitch Up" $norStart; Add-NoString $norWriter "Pitch Down" $norStart
foreach ($v in @(0, 0, 0, 4, 1, 1)) { $norWriter.Write([int]$v) }
$norWriter.Write([int]21); Add-NoString $norWriter "Map" $norStart; $norWriter.Write([int]1)
Add-NoString $norWriter "Toggle Map" $norStart; Add-NoString $norWriter "" $norStart; Add-NoString $norWriter "" $norStart
foreach ($v in @(0, 0, 0, 0, 1, 3)) { $norWriter.Write([int]$v) }
$norWriter.Write([byte[]](9, 9, 9, 9))
$norWriter.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $norGame "resources.assets"), $norStream.ToArray())
$norPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $norDir "data"
Push-Location $norRoot
try {
    $nor = Run-Engine @("nuclearoption", "refresh-db", (Join-Path $norDir "game"), "--text")
    Assert-True ($nor.ExitCode -eq 0) ("nuclearoption refresh-db failed: " + $nor.Output)
    Assert-Match $nor.Output "Read 2 Rewired action" "nuclearoption refresh-db should read both actions."
    # Written into the game-data folder (<data>\GameData), never into the program folder ($norRoot).
    $norCatalog = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\NuclearOption\actions.tsv"))
    Assert-True (-not (Test-Path (Join-Path $norRoot "DB\NuclearOption\actions.tsv"))) "nuclearoption refresh-db must not write into the program folder."
    Assert-Match $norCatalog "Pitch\tPitch Axis\taxis\tFlight Controls\t0" "the refreshed catalog should have Pitch with its Rewired id."
    Assert-Match $norCatalog "Pitch\.Negative\tPitch Down\tbutton\tFlight Controls\t0" "the refreshed catalog should have the poles of an axis action."
    Assert-Match $norCatalog "Map\tToggle Map\tbutton\tGameplay\t21" "the refreshed catalog should have the gameplay button with its category."
    Assert-NotMatch $norCatalog "Map\.Positive" "a button action has no poles."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\NuclearOption\actions.tsv")) -match "Toggle Night Vision") "the shipped catalog must stay untouched."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $norPreviousData
}
