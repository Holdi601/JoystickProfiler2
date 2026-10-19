# FSX / FSX: Steam Edition / Prepar3D v4-v6 (engine fsx, fsxse, p3d4, p3d5, p3d6): SimControls Standard.xml device maps
# ("<instance name>{<instance GUID>}"), axis sensitivity / null zone, keyboard, refresh-db, scan/import/export modes.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). Fixture: a synthetic
# file in the documented format (docs/games/fsx-p3d.md); none of these sims was available, so this is the only test.
$fsxDir = Join-Path $tempDir "fsx"
New-Item -Path $fsxDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\fsx\*") -Destination $fsxDir -Recurse -Force
$fsxFile = Join-Path $fsxDir "Microsoft\FSX\Controls\Standard.XML"
$fsxOriginal = [System.IO.File]::ReadAllText($fsxFile)
$fsxProfile = Join-Path $tempDir "fsx.pr0file"
$fsxStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$fsxBox = "Smoke Box {0000BEEF-1111-2222-3333-444444444444}"
function Get-FsxText([string]$Path) { return [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n") }
function Get-FsxMap([string]$Text, [string]$Name) {
    $m = [regex]::Match($Text, "(?s)<SimControls\.Map>\s*<Name>" + [regex]::Escape($Name) + "</Name>.*?</SimControls\.Map>")
    return $m.Value
}
# Refresh-db writes the command list into the game-data folder (<data>\GameData): a temp program root (JoyPro's own DB
# folders only) and a temp data folder, so neither the development DB nor the other smoke parts see test entries.
$fsxRoot = Join-Path $tempDir "fsx-root"
New-Item -Path (Join-Path $fsxRoot "DB") -ItemType Directory -Force | Out-Null
foreach ($own in @("Functions", "Devices", "Llm")) {
    Copy-Item -Path (Join-Path $engineBin ("DB\" + $own)) -Destination (Join-Path $fsxRoot "DB") -Recurse -Force
}
$previousFsxFake = $env:JOYPRO_FAKE_DEVICES
$previousFsxData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\fsx\devices.tsv"
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "fsx-data"
$fsxLists = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB"
Push-Location $fsxRoot
try {
    $fsxList = Run-Engine @("games", "integrations", "--text")
    foreach ($fsxId in @("FSX", "FSXSE", "P3D4", "P3D5", "P3D6")) {
        Assert-Match $fsxList.Output ("(?m)\b" + $fsxId + "\b") ("games integrations should list " + $fsxId + ".")
    }

    # Catalog: JoyPro's own event list plus the events of the user's file and the sim's template.
    $fsxRefresh = Run-Engine @("fsx", "refresh-db", "--path", (Join-Path $fsxDir "install"), "--text")
    Assert-True ($fsxRefresh.ExitCode -eq 0) ("fsx refresh-db failed: " + $fsxRefresh.Output)
    $fsxCatalog = Get-FsxText (Join-Path $fsxLists "FSX\actions.tsv")
    Assert-True (-not (Test-Path (Join-Path $fsxRoot "DB\FSX"))) "fsx refresh-db must not write into the program folder."
    Assert-Match $fsxCatalog "(?m)^AXIS_AILERONS_SET\tAilerons \(roll\) axis\taxis\tFlight controls$" "the FSX catalog should have JoyPro's aileron axis."
    Assert-Match $fsxCatalog "(?m)^SMOKE_TEMPLATE_EVENT\t[^\t]+\tbutton\t" "refresh-db should add the template's events."
    Assert-Match $fsxCatalog "(?m)^AXIS_SMOKE_TEMPLATE_SET\t[^\t]+\taxis\t" "an <AxEvent> of the template is an axis event."
    $fsxRefreshUser = Run-Engine @("fsx", "refresh-db", "--path", $fsxFile, "--text")
    Assert-True ($fsxRefreshUser.ExitCode -eq 0) ("fsx refresh-db of the user file failed: " + $fsxRefreshUser.Output)

    # Scan: device maps by instance GUID (else by a unique name), hats, keyboard; slew, mouse and unknown maps skipped.
    $fsxScan = Run-Engine @("fsx", "scan", $fsxFile, "--text")
    Assert-True ($fsxScan.ExitCode -eq 0) ("fsx scan failed: " + $fsxScan.Output)
    Assert-Match $fsxScan.Output ([regex]::Escape("AXIS_AILERONS_SET <- " + $fsxStick + " JOY_X")) "scan should read the stick's aileron axis."
    Assert-Match $fsxScan.Output ([regex]::Escape("PAN_VIEW <- " + $fsxStick + " JOY_BTN_POV1_U")) "scan should read the hat (whole hat, shown on up)."
    Assert-Match $fsxScan.Output "AXIS_RUDDER_SET <- Smoke Pedals \{0AAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\} JOY_RZ" "a map whose GUID changed should match the only device of that name."
    Assert-Match $fsxScan.Output "by name only" "scan should say when a map matched by name only."
    Assert-Match $fsxScan.Output "'Other Yoke\{92657FD0-7C66-11E2-8005-444553540000\}' is not connected" "scan should name a device map that is not connected."
    Assert-Match $fsxScan.Output "PARKING_BRAKES <- Keyboard Period \+ Keyboard LeftControl" "scan should read CTRL+VK_PERIOD."
    Assert-NotMatch $fsxScan.Output "SLEW|VIEW_RIGHT_UP" "scan must skip the slew map and key chords."

    # Import and round trip.
    $fsxImport = Run-Engine @("--profile", $fsxProfile, "fsx", "import", $fsxFile, "--include-keyboard", "--text")
    Assert-True ($fsxImport.ExitCode -eq 0) ("fsx import failed: " + $fsxImport.Output)
    $fsxAileron = Run-Engine @("--profile", $fsxProfile, "binds", "tuning", "show", "Ailerons (roll) axis", "--game", "FSX", "--text")
    Assert-Match $fsxAileron.Output "game:FSX: inv=0;.*n\.nullZone=36;n\.scale=64" ("import should store AxScale/AxNull as the FSX tuning: " + $fsxAileron.Output)
    $fsxThrottle = Run-Engine @("--profile", $fsxProfile, "binds", "tuning", "show", "Throttle axis (all engines)", "--game", "FSX", "--text")
    Assert-Match $fsxThrottle.Output "game:FSX: inv=1;.*n\.nullZone=1;n\.scale=127" ("a negative AxScale should import as an inverted axis: " + $fsxThrottle.Output)
    $fsxRound = Run-Engine @("--profile", $fsxProfile, "fsx", "export", $fsxFile, "--include-keyboard", "--text")
    Assert-True ($fsxRound.ExitCode -eq 0) ("fsx export after import failed: " + $fsxRound.Output)
    Assert-True ([System.IO.File]::ReadAllText($fsxFile) -eq $fsxOriginal) ("fsx import/export must leave Standard.XML as it was: " + $fsxRound.Output)

    # Merge: blocks added to the device's map, a new map for a new device, conflicts kept, hats only as a whole.
    Run-Engine @("--profile", $fsxProfile, "relations", "add", "FSX Prop", "--axis", "--node", "FSX:AXIS_PROPELLER_SET", "--device", $fsxStick, "--input", "JOY_SLIDER1", "--text") | Out-Null
    Run-Engine @("--profile", $fsxProfile, "relations", "add", "FSX Box Gear", "--button", "--node", "FSX:GEAR_DOWN", "--device", $fsxBox, "--input", "JOY_BTN2", "--text") | Out-Null
    Run-Engine @("--profile", $fsxProfile, "relations", "add", "FSX Spoilers", "--button", "--node", "FSX:SPOILERS_TOGGLE", "--device", $fsxStick, "--input", "JOY_BTN8", "--text") | Out-Null
    Run-Engine @("--profile", $fsxProfile, "relations", "add", "FSX Hat Flaps", "--button", "--node", "FSX:FLAPS_UP", "--device", $fsxStick, "--input", "JOY_BTN_POV2_U", "--text") | Out-Null
    $fsxDry = Run-Engine @("--profile", $fsxProfile, "fsx", "export", $fsxFile, "--mode", "merge", "--dry-run", "--text")
    Assert-Match $fsxDry.Output "Dry run: \d+ block change\(s\)" ("fsx dry run should count the changes: " + $fsxDry.Output)
    Assert-True ([System.IO.File]::ReadAllText($fsxFile) -eq $fsxOriginal) "fsx dry run must not write."
    $fsxMerge = Run-Engine @("--profile", $fsxProfile, "fsx", "export", $fsxFile, "--mode", "merge", "--text")
    Assert-True ($fsxMerge.ExitCode -eq 0) ("fsx merge export failed: " + $fsxMerge.Output)
    Assert-Match $fsxMerge.Output "the input already has 'GEAR_TOGGLE'; merge keeps the sim's bind" "merge should keep the sim's bind on button 8."
    Assert-Match $fsxMerge.Output "assign a hat as a whole" "a hat direction for another event should be refused."
    $fsxText = Get-FsxText $fsxFile
    $fsxStickMap = Get-FsxMap $fsxText "Smoke Stick{11111111-2222-3333-4444-555555555555}"
    Assert-Match $fsxStickMap "        <Axis>\n            <AxName>Slider</AxName>\n            <Index>6</Index>\n            <AxEvent>AXIS_PROPELLER_SET</AxEvent>\n            <AxScale>127</AxScale>\n        </Axis>\n    </SimControls.Map>" ("merge should append the propeller axis to the stick's map: " + $fsxStickMap)
    $fsxBoxMap = Get-FsxMap $fsxText "Smoke Box{0000BEEF-1111-2222-3333-444444444444}"
    Assert-Match $fsxBoxMap "<Index>1</Index>\n\s*<Down>GEAR_DOWN</Down>" ("merge should add a map for the new device: " + $fsxText)
    Assert-Match $fsxText "</SimControls\.Map>\n</SimBase\.Document>\n$" "the new map goes before the document's end."
    Assert-Match $fsxText "(?s)Type=""Controls""\n        version=""1,0""" "export must keep the root element's formatting."
    Assert-True (Test-Path ($fsxFile + ".jpbak")) "the FSX export should back up Standard.XML."

    # Merge-overwrite: the profile wins; sensitivity and null zone go into the axis block.
    $fsxTune = Run-Engine @("--profile", $fsxProfile, "binds", "tuning", "set", "Ailerons (roll) axis", "--scope", "game:FSX", "--param", "scale=100", "--param", "nullZone=10", "--text")
    Assert-True ($fsxTune.ExitCode -eq 0) ("binds tuning set for game:FSX failed: " + $fsxTune.Output)
    $fsxOver = Run-Engine @("--profile", $fsxProfile, "fsx", "export", $fsxFile, "--mode", "merge-overwrite", "--text")
    Assert-True ($fsxOver.ExitCode -eq 0) ("fsx merge-overwrite export failed: " + $fsxOver.Output)
    $fsxStickMap = Get-FsxMap (Get-FsxText $fsxFile) "Smoke Stick{11111111-2222-3333-4444-555555555555}"
    Assert-Match $fsxStickMap "<Index>7</Index>\n\s*<Down>SPOILERS_TOGGLE</Down>" "merge-overwrite should put the spoilers on button 8."
    Assert-Match $fsxStickMap "<AxEvent>AXIS_AILERONS_SET</AxEvent>\n\s*<AxScale>100</AxScale>\n\s*<AxNull>10</AxNull>" ("export should write the aileron sensitivity and null zone: " + $fsxStickMap)
    Assert-Match $fsxStickMap "<Down>BRAKES</Down>\n\s*<DownRpt>1</DownRpt>" "untouched blocks keep their <DownRpt>."

    # Clean: the written devices keep only the profile's binds; slew, keyboard and unknown devices stay.
    $fsxDelete = Run-Engine @("--profile", $fsxProfile, "relations", "mass", "delete", "--relation", "Brakes (both)", "--text")
    Assert-True ($fsxDelete.ExitCode -eq 0) ("relations mass delete failed: " + $fsxDelete.Output)
    $fsxClean = Run-Engine @("--profile", $fsxProfile, "fsx", "export", $fsxFile, "--mode", "clean", "--text")
    Assert-True ($fsxClean.ExitCode -eq 0) ("fsx clean export failed: " + $fsxClean.Output)
    $fsxText = Get-FsxText $fsxFile
    Assert-NotMatch (Get-FsxMap $fsxText "Smoke Stick{11111111-2222-3333-4444-555555555555}") "<Down>BRAKES</Down>" "clean should release the stick's brake button."
    Assert-Match (Get-FsxMap $fsxText "Smoke Stick SLEW {11111111-2222-3333-4444-555555555555}") "AXIS_SLEW_SIDEWAYS_SET" "clean must keep the slew map."
    Assert-Match (Get-FsxMap $fsxText "Other Yoke{92657FD0-7C66-11E2-8005-444553540000}") "SPOILERS_TOGGLE" "clean must keep a device that is not connected."
    Assert-Match (Get-FsxMap $fsxText "KEYBOARD_MAIN") "<Key>G</Key>" "clean without keyboard must keep the keyboard map."

    # Nuke: a stick-only profile clears the other connected devices' maps (the maps stay).
    $fsxNukeProfile = Join-Path $tempDir "fsx-nuke.pr0file"
    Run-Engine @("--profile", $fsxNukeProfile, "relations", "add", "FSX Nuke Aileron", "--axis", "--node", "FSX:AXIS_AILERONS_SET", "--device", $fsxStick, "--input", "JOY_X", "--text") | Out-Null
    $fsxNuke = Run-Engine @("--profile", $fsxNukeProfile, "fsx", "export", $fsxFile, "--mode", "merge", "--nuke-untouched-devices", "--text")
    Assert-True ($fsxNuke.ExitCode -eq 0) ("fsx nuke export failed: " + $fsxNuke.Output)
    $fsxText = Get-FsxText $fsxFile
    Assert-NotMatch (Get-FsxMap $fsxText "Smoke Throttle {66666666-7777-8888-9999-000000000000}") "AXIS_THROTTLE_SET|FLAPS_INCR" "nuke should clear the throttle's map."
    Assert-Match $fsxText "<Name>Smoke Throttle \{66666666-7777-8888-9999-000000000000\}</Name>" "nuke keeps the (empty) map."
    Assert-Match (Get-FsxMap $fsxText "Smoke Stick{11111111-2222-3333-4444-555555555555}") "AXIS_AILERONS_SET" "nuke must keep the exported stick bind."

    # Prepar3D reads the same format.
    $p3dFile = Join-Path $fsxDir "p3d5-Standard.xml"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\fsx\Microsoft\FSX\Controls\Standard.XML") -Destination $p3dFile -Force
    $p3dScan = Run-Engine @("p3d5", "scan", $p3dFile, "--text")
    Assert-True ($p3dScan.ExitCode -eq 0) ("p3d5 scan failed: " + $p3dScan.Output)
    Assert-Match $p3dScan.Output ([regex]::Escape("AXIS_ELEVATOR_SET <- " + $fsxStick + " JOY_Y")) "p3d5 scan should read the same file format."
}
finally {
    Pop-Location
    $env:JOYPRO_FAKE_DEVICES = $previousFsxFake
    $env:JOYPRO_DATA_DIR = $previousFsxData
}
