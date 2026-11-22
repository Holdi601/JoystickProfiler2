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
    Assert-True ($fgB.primary.cid -eq "CID_JOY2" -and $fgB.primary.input -eq "U_AXIS" -and $fgB.primary.flags -eq "INVERTED, AXIS") "the throttle on vJoy Slider 1 (SDL axis 6), inverted for a HOTAS lever. $($fgB | ConvertTo-Json -Compress)"
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

    # ---- no pilot yet: the preset alone, with the hint to pick it -------------------------------------------------
    $fgEmpty = New-FgCopy "nopilot"
    Remove-Item -Force $fgEmpty.Pilot, $fgEmpty.Csg
    $fgNoPilot = Run-Engine @("--profile", $fgProfile, "fotg", "export", $fgEmpty.Preset, "--mode", "clean", "--text")
    Assert-True ($fgNoPilot.ExitCode -eq 0) "fotg export without a pilot failed. Output: $($fgNoPilot.Output)"
    Assert-Match $fgNoPilot.Output "No pilot yet" "without a pilot the export says how to pick the preset."
    Assert-True (Test-Path $fgEmpty.Preset) "without a pilot the preset is written anyway."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $fgPreviousFakeDevices
    $env:JOYPRO_INSTALL_DETECTION = $fgPreviousDetection
    $env:JOYPRO_DATA_DIR = $fgPreviousData
}
