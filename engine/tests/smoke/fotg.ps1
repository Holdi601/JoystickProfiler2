# Fate of the Galaxy (FreeSpace Open 26 through Knossos): a made-up portable Knossos tree in tests\fixtures\fotg
# (kn_portable\Library\FotG_2\FotG_2-2.0.2 with a small controlconfigdefaults.tbl and *-lcl.tbm, kn_portable\
# HardLightProductions\FreeSpaceOpen with a pilot, the mod's settings and a campaign save built here). Formats from the
# FSO 26.0.1 source (presets.cpp, plr.cpp, csg.cpp, osregistry.cpp); no game data is copied. Devices:
# tests\fixtures\nuclearoption\devices.tsv (vJoy, Smoke Stick X Y Z, Smoke Pedals X Y RZ, Smoke Panel X).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).
$fgDir = Join-Path $tempDir "fotg"
$fgVjoy = "vJoy Device {89d63500-b53f-11f1-8002-444553540000}"
$fgStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$fgPedals = "Smoke Pedals {22222222-3333-4444-5555-666666666666}"
$fgPanel = "Smoke Panel {33333333-4444-5555-6666-777777777777}"

function New-FgCopy([string]$Name) {
    $root = Join-Path $fgDir $Name
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Copy-Item -Recurse -Path (Join-Path $engineDir "tests\fixtures\fotg\kn_portable") -Destination $root
    $players = Join-Path $root "kn_portable\HardLightProductions\FreeSpaceOpen\data\players"
    # A campaign save (CSG 8): "CSG_", version, then sections { ushort id, uint size, data }; 0x0004 = controls preset.
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("CSG_")); $bw.Write([byte]8)
    $bw.Write([uint16]1); $bw.Write([uint32]1); $bw.Write([byte]0)
    $preset = [System.Text.Encoding]::ASCII.GetBytes("default`0")
    $bw.Write([uint16]4); $bw.Write([uint32]$preset.Length); $bw.Write($preset)
    $bw.Write([uint16]0x14); $bw.Write([uint32]4); $bw.Write([uint32]0)
    $bw.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $players "Smoke._SPC_Test.csg"), $ms.ToArray())
    return [PSCustomObject]@{ Root = $root; Pilot = (Join-Path $players "Smoke.json"); Csg = (Join-Path $players "Smoke._SPC_Test.csg")
                              Preset = (Join-Path $players "presets\JoyPro_FotG.json"); Ini = (Join-Path $root "kn_portable\HardLightProductions\FreeSpaceOpen\data\FotG_2_settings.ini") }
}
function Get-FgCsgPreset([string]$Path) {
    $b = [System.IO.File]::ReadAllBytes($Path)
    $p = 5
    while ($p + 6 -le $b.Length) {
        $id = [BitConverter]::ToUInt16($b, $p); $size = [BitConverter]::ToUInt32($b, $p + 2)
        if ($id -eq 4) { return [System.Text.Encoding]::ASCII.GetString($b, $p + 6, $size - 1) }
        $p += 6 + $size
    }
    return $null
}
function Get-FgBind($Preset, [string]$Name) { return @($Preset.actions | Where-Object { $_.bind -eq $Name })[0] }
# The campaign save's sections { id, bytes } in file order; throws when the sizes do not add up to the file.
function Get-FgCsgSections([string]$Path) {
    $b = [System.IO.File]::ReadAllBytes($Path)
    Assert-True ([System.Text.Encoding]::ASCII.GetString($b, 0, 4) -eq "CSG_") "$Path is no campaign save."
    $sections = New-Object System.Collections.Generic.List[object]
    $p = 5
    while ($p -lt $b.Length) {
        Assert-True ($p + 6 -le $b.Length) "$Path ends inside a section header at $p."
        $id = [BitConverter]::ToUInt16($b, $p); $size = [BitConverter]::ToUInt32($b, $p + 2)
        Assert-True ($p + 6 + $size -le $b.Length) "$Path section $id runs past the end."
        $data = New-Object byte[] $size
        [Array]::Copy($b, $p + 6, $data, 0, $size)
        $sections.Add([PSCustomObject]@{ Id = $id; Data = $data })
        $p += 6 + $size
    }
    return $sections
}
# The JSON value types of two parsed files, path by path (FSO reads integers with json_typeof == JSON_INTEGER).
function Compare-FgJsonTypes($A, $B, [string]$Path) {
    if ($null -eq $A -or $null -eq $B) { if (($null -eq $A) -ne ($null -eq $B)) { return "$Path null" }; return $null }
    if ($A -is [System.Management.Automation.PSCustomObject]) {
        if (-not ($B -is [System.Management.Automation.PSCustomObject])) { return "$Path object" }
        foreach ($p in $A.PSObject.Properties) {
            if ($null -eq $B.PSObject.Properties[$p.Name]) { return "$Path/$($p.Name) missing" }
            $r = Compare-FgJsonTypes $p.Value $B.($p.Name) "$Path/$($p.Name)"
            if ($r) { return $r }
        }
        return $null
    }
    if ($A -is [array]) {
        if (-not ($B -is [array]) -or $A.Count -ne $B.Count) { return "$Path array" }
        for ($i = 0; $i -lt $A.Count; $i++) { $r = Compare-FgJsonTypes $A[$i] $B[$i] "$Path[$i]"; if ($r) { return $r } }
        return $null
    }
    $integral = { param($v) $v -is [int] -or $v -is [long] }
    if ((& $integral $A) -ne (& $integral $B) -or ((-not (& $integral $A)) -and $A.GetType() -ne $B.GetType())) { return "$Path $($A.GetType().Name) -> $($B.GetType().Name)" }
    return $null
}
# What FSO loads from a user data folder: every data\players\*.json is a pilot (playermenu.cpp lists them, plr.cpp
# verify reads "signature" as an integer, PLR_) and every players\presets\*.json a preset (PST_).
function Assert-FgFsoFolders([string]$Root) {
    foreach ($players in @(Get-ChildItem -Path $Root -Recurse -Directory | Where-Object { $_.Name -eq "players" })) {
        foreach ($rule in @(@{ Dir = $players.FullName; Signature = 0x5f524c50 }, @{ Dir = (Join-Path $players.FullName "presets"); Signature = 0x5f545350 })) {
            if (-not (Test-Path $rule.Dir)) { continue }
            foreach ($file in @(Get-ChildItem -Path $rule.Dir -File | Where-Object { $_.Name -like "*.json" })) {
                $json = $null
                try { $json = [System.IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json } catch { }
                Assert-True ($null -ne $json) "FSO would load $($file.FullName); it is no valid JSON."
                Assert-True (($json.signature -is [int] -or $json.signature -is [long]) -and $json.signature -eq $rule.Signature) "FSO would load $($file.FullName); its signature is not the integer $($rule.Signature) (FSO stops at start: 'JSON element signature must be an integer')."
            }
        }
    }
}

$fgPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$fgPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$fgPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\nuclearoption\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_DATA_DIR = Join-Path $fgDir "data"
try {
    $fgIntegrations = Run-Engine @("games", "integrations", "--text")
    Assert-Match $fgIntegrations.Output "FateOfTheGalaxy" "games integrations should list Fate of the Galaxy."

    # ---- refresh-db: the mod's menu (texts from the -lcl.tbm, switched-off controls left out) ------------------------
    $fgRefreshCopy = New-FgCopy "refresh"
    $fgRefresh = Run-Engine @("fotg", "refresh-db", (Join-Path $fgRefreshCopy.Root "kn_portable"), "--text")
    Assert-True ($fgRefresh.ExitCode -eq 0) ("fotg refresh-db failed: " + $fgRefresh.Output)
    Assert-Match $fgRefresh.Output "switched off by the mod" "refresh-db should report the controls the mod switched off."
    $fgCatalogFile = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\FateOfTheGalaxy\actions.tsv"
    Assert-True (Test-Path $fgCatalogFile) "fotg refresh-db should write the catalog into the game-data folder."
    $fgCatalog = [System.IO.File]::ReadAllText($fgCatalogFile)
    Assert-Match $fgCatalog "CUSTOM_CONTROL_3\tToggle S-foils\tbutton\tShip\t121" "a control the mod enables gets its English text from the -lcl.tbm and its tab."
    Assert-Match $fgCatalog "TOGGLE_GLIDING\tToggle Active Pause\tbutton\tShip" "a mod's XSTR id renames the control."
    Assert-Match $fgCatalog "JOY_ABS_THROTTLE_AXIS\tAbsolute Throttle Axis\taxis\tShip\t127" "the axes are axis actions."
    Assert-NotMatch $fgCatalog "(?m)^AFTERBURNER\t|(?m)^LAUNCH_COUNTERMEASURE\t|(?m)^SHOW_GOALS\t" "switched-off controls stay out of the catalog."

    # ---- scan: the pilot's preset "default" = FSO's defaults with the mod's override --------------------------------
    $fgScanCopy = New-FgCopy "scan"
    $fgScan = Run-Engine @("fotg", "scan", $fgScanCopy.Pilot, "--include-keyboard", "--text")
    Assert-True ($fgScan.ExitCode -eq 0) "fotg scan failed. Output: $($fgScan.Output)"
    foreach ($fgExpect in @("FIRE_PRIMARY <- Smoke Stick \{[^}]+\} JOY_BTN1", "TARGET_NEXT_CLOSEST_HOSTILE <- Smoke Stick \{[^}]+\} JOY_BTN4",
                            "JOY_HEADING_AXIS <- Smoke Stick \{[^}]+\} JOY_X", "JOY_PITCH_AXIS <- Smoke Stick \{[^}]+\} JOY_Y",
                            "PADLOCK_UP <- Smoke Stick \{[^}]+\} JOY_BTN_POV1_D", "PADLOCK_RIGHT <- Smoke Stick \{[^}]+\} JOY_BTN_POV1_U",
                            "TOGGLE_GLIDING <- Keyboard P", "VIEW_SLEW <- Keyboard L \+")) {
        Assert-Match $fgScan.Output $fgExpect ("fotg scan should read '" + $fgExpect + "'. Output: " + $fgScan.Output)
    }
    Assert-NotMatch $fgScan.Output "FIRE_SECONDARY <- Smoke Stick" "the mod's override clears the default Joy-0 button 1 of the secondary weapon."
    Assert-Match $fgScan.Output "the mod's default controls of pilot Smoke" "scan should name the preset it read."

    # ---- clean export: a preset file, the pilot and its campaign save switched to it --------------------------------
    $fgProfile = Join-Path $fgDir "export.pr0file"
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Pitch", "--axis", "--node", "FateOfTheGalaxy:JOY_PITCH_AXIS", "--device", $fgStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Yaw", "--axis", "--node", "FateOfTheGalaxy:JOY_HEADING_AXIS", "--device", $fgPedals, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Throttle", "--axis", "--node", "FateOfTheGalaxy:JOY_ABS_THROTTLE_AXIS", "--device", $fgVjoy, "--input", "JOY_SLIDER1", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Fire", "--button", "--node", "FateOfTheGalaxy:FIRE_PRIMARY", "--device", $fgStick, "--input", "JOY_BTN2", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Missile", "--button", "--node", "FateOfTheGalaxy:FIRE_SECONDARY", "--device", $fgStick, "--input", "JOY_BTN_POV1_U", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Diagonal", "--button", "--node", "FateOfTheGalaxy:TARGET_NEXT", "--device", $fgVjoy, "--input", "JOY_BTN_POV1_UR", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Shields", "--button", "--node", "FateOfTheGalaxy:SHIELD_EQUALIZE", "--device", $fgPanel, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $fgProfile, "relations", "add", "FG Boost", "--button", "--node", "FateOfTheGalaxy:AFTERBURNER", "--device", $fgStick, "--input", "JOY_BTN3", "--text") | Out-Null
    $fgCopy = New-FgCopy "clean"
    $fgOriginalPilot = [System.IO.File]::ReadAllText($fgCopy.Pilot)
    $fgDry = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgCopy.Pilot, "--mode", "clean", "--dry-run", "--text")
    Assert-True ($fgDry.ExitCode -eq 0) "fotg dry run failed. Output: $($fgDry.Output)"
    Assert-True (-not (Test-Path $fgCopy.Preset)) "a fotg dry run must not write the preset."
    Assert-True ([System.IO.File]::ReadAllText($fgCopy.Pilot) -eq $fgOriginalPilot) "a fotg dry run must not touch the pilot."
    $fgClean = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgCopy.Pilot, "--mode", "clean", "--text")
    Assert-True ($fgClean.ExitCode -eq 0) "fotg clean export failed. Output: $($fgClean.Output)"
    Assert-Match $fgClean.Output "four directions only" "a hat diagonal is skipped with a reason."
    Assert-Match $fgClean.Output "Device 4 \(Joy-3\) = Smoke Panel" "a device without a slot takes the free Device 4."
    Assert-True (Test-Path $fgCopy.Preset) "fotg export should write presets\JoyPro_FotG.json."
    Assert-True (Test-Path ($fgCopy.Pilot + ".jpbak")) "fotg export should back up the pilot first."
    $fgPreset = [System.IO.File]::ReadAllText($fgCopy.Preset) | ConvertFrom-Json
    Assert-True ($fgPreset.signature -eq 1599361872 -and $fgPreset.version -eq 0 -and @($fgPreset.actions).Count -eq 140) "the preset has FSO's header and all 140 controls."
    $fgB = Get-FgBind $fgPreset "FIRE_PRIMARY"
    Assert-True ($fgB.primary.cid -eq "CID_JOY0" -and $fgB.primary.input -eq "1" -and $fgB.secondary.cid -eq "CID_MOUSE") "Fire on Joy-0 button index 1, the mouse button stays. $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "JOY_HEADING_AXIS"
    Assert-True ($fgB.primary.cid -eq "CID_JOY1" -and $fgB.primary.input -eq "Z_AXIS" -and $fgB.primary.flags -eq "AXIS") "yaw on the pedals' third present axis (RZ = SDL axis 2 = Z_AXIS). $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "JOY_ABS_THROTTLE_AXIS"
    # FSO's absolute throttle: the low end of the raw axis is full throttle, as in DCS, so no INVERTED (2.7.3 wrote it and
    # the throttle ran backwards in the game).
    Assert-True ($fgB.primary.cid -eq "CID_JOY2" -and $fgB.primary.input -eq "U_AXIS" -and $fgB.primary.flags -eq "AXIS") "the throttle on vJoy Slider 1 (SDL axis 6), not inverted. $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "FIRE_SECONDARY"
    Assert-True ($fgB.primary.input -eq "KEY_TAB" -and $fgB.secondary.cid -eq "CID_JOY0" -and $fgB.secondary.input -eq "129") "hat up = button 129, the key stays. $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "SHIELD_EQUALIZE"
    Assert-True ($fgB.secondary.cid -eq "CID_JOY3" -and $fgB.secondary.input -eq "2") "the panel's button on Joy-3. $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "TARGET_NEXT_CLOSEST_HOSTILE"
    Assert-True ($fgB.primary.input -eq "KEY_H" -and $fgB.secondary.cid -eq "CID_NONE") "clean releases the stick's other binds, keys stay. $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgPreset "AFTERBURNER"
    Assert-True ($fgB.primary.cid -eq "CID_NONE" -and $fgB.secondary.cid -eq "CID_NONE") "a control the mod switched off gets no bind."
    $fgB = Get-FgBind $fgPreset "PADLOCK_UP"
    Assert-True ($fgB.secondary.cid -eq "CID_NONE") "clean releases the default hat views of the written stick."
    Assert-Match ([System.IO.File]::ReadAllText($fgCopy.Pilot)) '"preset": "JoyPro_FotG"' "the pilot now names the JoyPro preset."
    Assert-True ((Get-FgCsgPreset $fgCopy.Csg) -eq "JoyPro_FotG") "the campaign save names the JoyPro preset too."
    Assert-Match ([System.IO.File]::ReadAllText($fgCopy.Ini)) 'Joystick3=\{"guid":"0300[0-9A-F]{4}CDAB00000300000000000000","id":-1\}' "the free slot gets the panel's SDL GUID."
    $fgAgain = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgCopy.Pilot, "--mode", "clean", "--text")
    Assert-Match $fgAgain.Output "already match" "a second clean export must change nothing."
    $fgBack = Run-Engine @("fotg", "scan", $fgCopy.Pilot, "--text")
    Assert-Match $fgBack.Output "JOY_HEADING_AXIS <- Smoke Pedals \{[^}]+\} JOY_RZ" "the exported yaw axis should read back on the pedals' RZ."
    Assert-Match $fgBack.Output "JOY_ABS_THROTTLE_AXIS <- vJoy Device \{[^}]+\} JOY_SLIDER1" "the exported throttle should read back."
    Assert-Match $fgBack.Output "preset 'JoyPro_FotG'" "the scan reads the pilot's JoyPro preset."
    # JoyPro's re-import data is never a *.json in players: FSO lists each as a pilot and stops at start on one without an
    # integer "signature" (2.7.3 wrote <pilot>.json.joypro-meta.json).
    Assert-True (Test-Path ($fgCopy.Pilot + ".joypro-meta")) "the export keeps its re-import data as <pilot>.json.joypro-meta."
    Assert-FgFsoFolders $fgCopy.Root

    # ---- import of the game's binds into a new profile ------------------------------------------------------------
    $fgImportProfile = Join-Path $fgDir "import.pr0file"
    $fgImport = Run-Engine @("--profile", $fgImportProfile, "fotg", "import", $fgCopy.Pilot, "--text")
    Assert-True ($fgImport.ExitCode -eq 0) "fotg import failed. Output: $($fgImport.Output)"
    Assert-Match $fgImport.Output "(?m)^Relations Imported:\s+[1-9]" "fotg import should create relations."

    # ---- game-wide settings: dead zone into the pilot and the mod's settings -------------------------------------
    Run-Engine @("--profile", $fgProfile, "ffb", "set", "--device", "*", "--game", "FateOfTheGalaxy", "--param", "deadZone=5", "--text") | Out-Null
    $fgSettings = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgCopy.Pilot, "--mode", "clean", "--text")
    Assert-True ($fgSettings.ExitCode -eq 0) "fotg export with a dead zone failed. Output: $($fgSettings.Output)"
    Assert-Match ([System.IO.File]::ReadAllText($fgCopy.Pilot)) '"joy_dead_zone_size": 5' "the pilot gets the dead zone."
    Assert-Match ([System.IO.File]::ReadAllText($fgCopy.Ini)) '(?m)^JoystickDeadZone=5' "the mod's settings get the dead zone."

    # ---- the user's own invert of the throttle is written as INVERTED ----------------------------------------------
    $fgInvSet = Run-Engine @("--profile", $fgProfile, "binds", "tuning", "set", "FG Throttle", "--scope", "base", "--inverted", "true", "--text")
    Assert-True ($fgInvSet.ExitCode -eq 0) "binds tuning set --inverted failed. Output: $($fgInvSet.Output)"
    $fgInv = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgCopy.Pilot, "--mode", "clean", "--text")
    Assert-True ($fgInv.ExitCode -eq 0) "fotg export with an inverted throttle failed. Output: $($fgInv.Output)"
    $fgB = Get-FgBind ([System.IO.File]::ReadAllText($fgCopy.Preset) | ConvertFrom-Json) "JOY_ABS_THROTTLE_AXIS"
    Assert-True ($fgB.primary.flags -eq "INVERTED, AXIS") "an inverted throttle relation is written INVERTED. $($fgB | ConvertTo-Json -Compress)"
    Run-Engine @("--profile", $fgProfile, "binds", "tuning", "set", "FG Throttle", "--scope", "base", "--inverted", "false", "--text") | Out-Null

    # ---- no pilot yet: the preset alone, with the hint to pick it -------------------------------------------------
    $fgEmpty = New-FgCopy "nopilot"
    Remove-Item -Force $fgEmpty.Pilot, $fgEmpty.Csg
    $fgNoPilot = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgEmpty.Preset, "--mode", "clean", "--text")
    Assert-True ($fgNoPilot.ExitCode -eq 0) "fotg export without a pilot failed. Output: $($fgNoPilot.Output)"
    Assert-Match $fgNoPilot.Output "No pilot yet" "without a pilot the export says how to pick the preset."
    Assert-True (Test-Path $fgEmpty.Preset) "without a pilot the preset is written anyway."
    Assert-True (Test-Path ($fgEmpty.Preset + ".joypro-meta")) "without a pilot the re-import data goes next to the preset, not as a *.json."
    Assert-FgFsoFolders $fgEmpty.Root

    # ---- a tester's real files (FSO 26.0.1 SDL3, Fate of the Galaxy, 2026-11-23; tests\fixtures\fotg\tester): pilot
    # JPTEST with its preset "mine", a campaign save and the mod's settings with four devices. 2.7.3 wrote
    # players\JPTEST.json.joypro-meta.json, which FSO read as a pilot and stopped at start, and the throttle INVERTED. ----
    $env:JOYPRO_FAKE_DEVICES = Join-Path $fgDir "tester-devices.tsv"
    [System.IO.File]::WriteAllText($env:JOYPRO_FAKE_DEVICES, ("# JoyPro device`tVID`tPID`tHID product string`taxes`tbuttons`thats`n" +
        "MOZA AB9 FFB Base {534f0e90-8d0c-11f1-8001-444553540000}`t346E`t1000`tMOZA AB9 FFB Base`tX,Y`t64`t1`n" +
        "VPC MongoosT-50CM3 {072c2740-0d8d-11f1-8021-444553540000}`t3344`t0194`tVPC MongoosT-50CM3 Throttle`tX,Y,Z,RX,RY,RZ`t64`t1`n" +
        "MFG Crosswind V2_3 {071d3320-0d8d-11f1-8001-444553540000}`t16D0`t0A38`tMFG Crosswind V2/3`tX,Y,RZ`t0`t0`n"))
    $fgTester = New-FgCopy "tester"
    Remove-Item -Force $fgTester.Pilot, $fgTester.Csg
    $fgTesterData = Join-Path $fgTester.Root "kn_portable\HardLightProductions\FreeSpaceOpen\data"
    Copy-Item -Recurse -Force -Path (Join-Path $engineDir "tests\fixtures\fotg\tester\data\*") -Destination $fgTesterData
    $fgTPilot = Join-Path $fgTesterData "players\JPTEST.json"
    $fgTCsg = Join-Path $fgTesterData "players\JPTEST._SPC_R_Exc.csg"
    $fgTMine = Join-Path $fgTesterData "players\presets\mine.json"
    $fgTPreset = Join-Path $fgTesterData "players\presets\JoyPro_FotG.json"
    $fgTIni = Join-Path $fgTesterData "FotG_2_settings.ini"
    $fgTPilotBefore = [System.IO.File]::ReadAllText($fgTPilot)
    $fgTCsgBefore = Get-FgCsgSections $fgTCsg
    $fgTMineBefore = [System.IO.File]::ReadAllBytes($fgTMine)
    $fgTIniBefore = [System.IO.File]::ReadAllBytes($fgTIni)
    # The file 2.7.3 left: the next export takes its entries and removes it.
    [System.IO.File]::WriteAllText($fgTPilot + ".joypro-meta.json", '{"Game":"FateOfTheGalaxy","UpdatedUtc":"2026-11-23T11:25:57.7760539Z","Binds":{}}')
    $fgTProfile = Join-Path $fgDir "tester.pr0file"
    Run-Engine @("--profile", $fgTProfile, "relations", "add", "THROTTLE FOTG", "--axis", "--node", "FateOfTheGalaxy:JOY_ABS_THROTTLE_AXIS", "--device", "VPC MongoosT-50CM3 {072c2740-0d8d-11f1-8021-444553540000}", "--input", "JOY_RY", "--text") | Out-Null
    Run-Engine @("--profile", $fgTProfile, "relations", "add", "STICK PITCH", "--axis", "--node", "FateOfTheGalaxy:JOY_PITCH_AXIS", "--device", "MOZA AB9 FFB Base {534f0e90-8d0c-11f1-8001-444553540000}", "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $fgTProfile, "relations", "add", "RUDDER", "--axis", "--node", "FateOfTheGalaxy:JOY_HEADING_AXIS", "--device", "MFG Crosswind V2_3 {071d3320-0d8d-11f1-8001-444553540000}", "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $fgTProfile, "relations", "add", "SHIELDS", "--button", "--node", "FateOfTheGalaxy:SHIELD_EQUALIZE", "--device", "VPC MongoosT-50CM3 {072c2740-0d8d-11f1-8021-444553540000}", "--input", "JOY_BTN20", "--text") | Out-Null
    $fgT = Run-Engine @("--profile", $fgTProfile, "fotg", "export", $fgTPilot, "--mode", "clean", "--text")
    Assert-True ($fgT.ExitCode -eq 0) "fotg export onto the tester's files failed. Output: $($fgT.Output)"
    Assert-Match $fgT.Output "now uses the preset 'JoyPro_FotG' \(was 'mine'\)" "the tester's pilot switches from 'mine' to the JoyPro preset. Output: $($fgT.Output)"
    # The pilot: byte for byte as the game wrote it, only the preset's name changed; every integer still an integer.
    $fgTPilotAfter = [System.IO.File]::ReadAllText($fgTPilot)
    Assert-True ($fgTPilotAfter -ceq $fgTPilotBefore.Replace('"preset": "mine"', '"preset": "JoyPro_FotG"')) "the export may change only the pilot's preset name."
    $fgTTypes = Compare-FgJsonTypes ($fgTPilotBefore | ConvertFrom-Json) ($fgTPilotAfter | ConvertFrom-Json) ""
    Assert-True ($null -eq $fgTTypes) "a value of the pilot changed its JSON type: $fgTTypes"
    Assert-True ((($fgTPilotAfter | ConvertFrom-Json).signature) -eq 1599229008) "the pilot's signature stays PLR_."
    # The campaign save: every section as it was but the controls preset's name.
    $fgTCsgAfter = Get-FgCsgSections $fgTCsg
    Assert-True ($fgTCsgAfter.Count -eq $fgTCsgBefore.Count) "the campaign save keeps its sections."
    for ($fgI = 0; $fgI -lt $fgTCsgAfter.Count; $fgI++) {
        Assert-True ($fgTCsgAfter[$fgI].Id -eq $fgTCsgBefore[$fgI].Id) "the campaign save keeps its section order."
        if ($fgTCsgAfter[$fgI].Id -eq 4) { continue }
        Assert-True ([Convert]::ToBase64String($fgTCsgAfter[$fgI].Data) -eq [Convert]::ToBase64String($fgTCsgBefore[$fgI].Data)) "campaign save section $($fgTCsgAfter[$fgI].Id) must stay byte for byte."
    }
    Assert-True ((Get-FgCsgPreset $fgTCsg) -eq "JoyPro_FotG") "the campaign save names the JoyPro preset."
    # His own preset and the mod's settings (all four slots set, nothing new) stay untouched.
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($fgTMine)) -eq [Convert]::ToBase64String($fgTMineBefore)) "the tester's own preset 'mine' must stay untouched."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($fgTIni)) -eq [Convert]::ToBase64String($fgTIniBefore)) "the mod's settings must stay untouched."
    # The new preset: his slots (Joy-1 = the VPC throttle, RY = SDL axis 4), the throttle not inverted.
    $fgTJson = [System.IO.File]::ReadAllText($fgTPreset) | ConvertFrom-Json
    $fgB = Get-FgBind $fgTJson "JOY_ABS_THROTTLE_AXIS"
    Assert-True ($fgB.primary.cid -eq "CID_JOY1" -and $fgB.primary.input -eq "RY_AXIS" -and $fgB.primary.flags -eq "AXIS") "the tester's throttle as in his own preset (Joy-1 RY, not inverted). $($fgB | ConvertTo-Json -Compress)"
    $fgB = Get-FgBind $fgTJson "JOY_HEADING_AXIS"
    Assert-True ($fgB.primary.cid -eq "CID_JOY2" -and $fgB.primary.input -eq "Z_AXIS" -and $fgB.primary.flags -eq "AXIS") "the rudder on Joy-2 (RZ = SDL axis 2). $($fgB | ConvertTo-Json -Compress)"
    # Nothing FSO would load as a pilot or preset is broken, JoyPro's data is no *.json, 2.7.3's file is gone.
    Assert-FgFsoFolders $fgTester.Root
    Assert-True (Test-Path ($fgTPilot + ".joypro-meta")) "the export keeps its re-import data as JPTEST.json.joypro-meta."
    Assert-True (-not (Test-Path ($fgTPilot + ".joypro-meta.json"))) "the export removes 2.7.3's JPTEST.json.joypro-meta.json."
    Assert-True (@(Get-ChildItem -Path (Split-Path $fgTPilot) -Recurse -File | Where-Object { $_.Name -like "*.joypro-meta.json" }).Count -eq 0) "no JoyPro file may end in .json in players."

    # ---- 2.7.3's state (throttle INVERTED, its metadata under the old name) re-imported, then exported again: the
    # relation keeps its own direction and the throttle comes out right. ----------------------------------------------
    Move-Item -Force ($fgTPilot + ".joypro-meta") ($fgTPilot + ".joypro-meta.json")
    $fgTText = [System.IO.File]::ReadAllText($fgTPreset)
    $fgTOld = [regex]::Replace($fgTText, '("bind": "JOY_ABS_THROTTLE_AXIS",\s*"primary": \{\s*"cid": "CID_JOY1",\s*"flags": )"AXIS"', '$1"INVERTED, AXIS"')
    Assert-True ($fgTOld -ne $fgTText) "could not set up 2.7.3's inverted throttle."
    [System.IO.File]::WriteAllText($fgTPreset, $fgTOld)
    $fgTImportProfile = Join-Path $fgDir "tester-import.pr0file"
    $fgTImport = Run-Engine @("--profile", $fgTImportProfile, "fotg", "import", $fgTPilot, "--text")
    Assert-True ($fgTImport.ExitCode -eq 0) "fotg import of 2.7.3's state failed. Output: $($fgTImport.Output)"
    Assert-True (Test-Path ($fgTPilot + ".joypro-meta.json")) "an import must not change the game's folder."
    $fgTAgain = Run-Engine @("--profile", $fgTImportProfile, "fotg", "export", $fgTPilot, "--mode", "clean", "--text")
    Assert-True ($fgTAgain.ExitCode -eq 0) "fotg export after the import failed. Output: $($fgTAgain.Output)"
    $fgB = Get-FgBind ([System.IO.File]::ReadAllText($fgTPreset) | ConvertFrom-Json) "JOY_ABS_THROTTLE_AXIS"
    Assert-True ($fgB.primary.flags -eq "AXIS") "2.7.3's inverted throttle comes out right after import + export (the relation was not inverted). $($fgB | ConvertTo-Json -Compress)"
    Assert-True (-not (Test-Path ($fgTPilot + ".joypro-meta.json")) -and (Test-Path ($fgTPilot + ".joypro-meta"))) "the export moves 2.7.3's metadata to JPTEST.json.joypro-meta."
    Assert-FgFsoFolders $fgTester.Root
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $fgPreviousFakeDevices
    $env:JOYPRO_INSTALL_DETECTION = $fgPreviousDetection
    $env:JOYPRO_DATA_DIR = $fgPreviousData
}
