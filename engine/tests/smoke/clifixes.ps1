# The behaviour fixes of the CLI review of 2026-11-01 (help and code compared, docs/releases/v2.6.0.md): clean errors
# instead of crash reports, exit codes of the checks, imports that would drop relations, the DCS install commands
# (strip-defaults / device-luas flags, defaults cut with --dry-run and the running-DCS refusal), relations item add's
# check, relations aircraft --node with one aircraft, usage restore / pin, export-marks aliases, devices replace
# --merge, warnings in --text, Battlefield refresh-db --path <folder>, G-Rebels refresh-db without the game and
# --ignore-devices with commas.
# Dot-sourced by engine_smoke_test.ps1 ($engineDir, $tempDir, Run-Engine, Assert-*, Skip-WithoutGameData). Temp folders
# only: a synthetic DCS folder, a fake DCS_updater.exe (a copy of ping.exe) for the running check, no real game folder.
# In a scope of its own: the dot-sourced parts share one scope, and PowerShell 5.1 allows 4096 variables in it.
& {
$cfDir = Join-Path $tempDir "clifixes"
New-Item -Path $cfDir -ItemType Directory -Force | Out-Null
$cfPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Ignore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES; Installed = $env:JOYPRO_TEST_ASSUME_INSTALLED; Devices = $env:JOYPRO_FAKE_DEVICES }
$env:JOYPRO_DATA_DIR = Join-Path $cfDir "data"
function Assert-CfClean([object]$Run, [string]$What) {
    Assert-True ($Run.ExitCode -ne 0) ($What + " must fail. Output: " + $Run.Output)
    Assert-NotMatch $Run.Output "Crash report|   at JoyPro\." ($What + " must end with a clean error, not a crash report: " + $Run.Output)
}

try {
    # ---- 1. relations / binds export and import: missing folders, --text files, other files, partial files
    $cfProfile = Join-Path $cfDir "import.pr0file"
    Run-Engine @("--profile", $cfProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--device", "CF Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $cfProfile, "relations", "add", "Gear", "--button", "--node", "DCS:d3001pnilu3001cd1vd1vpnilvu0:F-16C_50", "--device", "CF Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_BTN1", "--text") | Out-Null
    $cfAll = Join-Path $cfDir "new\folder\relations.json"
    $cfExport = Run-Engine @("--profile", $cfProfile, "relations", "export", $cfAll)
    Assert-True ($cfExport.ExitCode -eq 0 -and (Test-Path $cfAll)) ("relations export must create a missing folder. Output: " + $cfExport.Output)
    $cfGear = Join-Path $cfDir "gear.json"
    Run-Engine @("--profile", $cfProfile, "relations", "export", $cfGear, "Gear") | Out-Null
    $cfBindsText = Join-Path $cfDir "binds.txt"
    Run-Engine @("--profile", $cfProfile, "binds", "export", $cfBindsText, "--text") | Out-Null
    $cfBindsGear = Join-Path $cfDir "binds-gear.json"
    Run-Engine @("--profile", $cfProfile, "binds", "export", $cfBindsGear, "Gear") | Out-Null

    $cfTextImport = Run-Engine @("--profile", $cfProfile, "binds", "import", $cfBindsText)
    Assert-CfClean $cfTextImport "binds import of a --text export"
    Assert-Match $cfTextImport.Output "is not a JSON file\. A binds export with --text is for reading only" ("binds import must say the --text export is for reading: " + $cfTextImport.Output)
    $cfWrongKind = Run-Engine @("--profile", $cfProfile, "relations", "import", $cfBindsGear)
    Assert-CfClean $cfWrongKind "relations import of a binds file"
    Assert-Match $cfWrongKind.Output "is not a relations file" ("relations import must name a file that is no relations document: " + $cfWrongKind.Output)
    $cfBadArg = Run-Engine @("--profile", $cfProfile, "relations", "import", $cfAll, "--ignore-device", "x")
    Assert-CfClean $cfBadArg "relations import with an option it does not take"

    $cfPartial = Run-Engine @("--profile", $cfProfile, "relations", "import", $cfGear)
    Assert-CfClean $cfPartial "relations import of a partial file without --replace"
    Assert-Match $cfPartial.Output "lacks 1 of them \(Pitch\).*--replace" ("the refusal must name the relation it would drop and --replace: " + $cfPartial.Output)
    Assert-Match (Run-Engine @("--profile", $cfProfile, "relations", "list", "--text")).Output "Pitch" "a refused relations import must not change the profile."
    $cfFull = Run-Engine @("--profile", $cfProfile, "relations", "import", $cfAll)
    Assert-True ($cfFull.ExitCode -eq 0) ("relations import of a full export needs no --replace. Output: " + $cfFull.Output)
    $cfBindsPartial = Run-Engine @("--profile", $cfProfile, "binds", "import", $cfBindsGear)
    Assert-CfClean $cfBindsPartial "binds import of a partial file without --replace"
    Assert-Match $cfBindsPartial.Output "lacks the binds of 1 relation\(s\) \(Pitch\)" ("the binds refusal must name the relation: " + $cfBindsPartial.Output)
    $cfBindsReplace = Run-Engine @("--profile", $cfProfile, "binds", "import", $cfBindsGear, "--replace")
    Assert-True ($cfBindsReplace.ExitCode -eq 0) ("binds import --replace failed. Output: " + $cfBindsReplace.Output)
    Assert-Match $cfBindsReplace.Output "Unbound \(not in the file\): 1 relation\(s\): Pitch" ("binds import --replace must say what lost its bind: " + $cfBindsReplace.Output)
    $cfReplace = Run-Engine @("--profile", $cfProfile, "relations", "import", $cfGear, "--replace")
    Assert-True ($cfReplace.ExitCode -eq 0) ("relations import --replace failed. Output: " + $cfReplace.Output)
    Assert-Match $cfReplace.Output "Removed \(not in the file\): 1 relation\(s\): Pitch" ("relations import --replace must say what it removed: " + $cfReplace.Output)
    Assert-NotMatch (Run-Engine @("--profile", $cfProfile, "relations", "list", "--text")).Output "Pitch" "relations import --replace must remove the relations the file lacks."

    # ---- 2. a word where a number belongs: a clean error
    Assert-CfClean (Run-Engine @("setup", "check", "--modifiers", "abc")) "setup check --modifiers abc"
    $cfWidth = Run-Engine @("setup", "render", "--width", "wide")
    Assert-CfClean $cfWidth "setup render --width wide"
    Assert-Match $cfWidth.Output "Invalid value for option --width: 'wide'" ("setup render must name the bad --width: " + $cfWidth.Output)

    # ---- 3. relations item add: the action must be of the relation's kind (and in its game's list); --force adds
    $cfItems = Join-Path $cfDir "items.pr0file"
    Run-Engine @("--profile", $cfItems, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50|FA-18C_hornet", "--text") | Out-Null
    $cfButton = Run-Engine @("--profile", $cfItems, "relations", "item", "add", "Pitch", "--node", "DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C II", "--text")
    Assert-CfClean $cfButton "relations item add of a button into an axis relation"
    Assert-Match $cfButton.Output "is a button; 'Pitch' is an axis relation|is not an action of DCS's command list" ("item add must say why: " + $cfButton.Output)
    $cfForced = Run-Engine @("--profile", $cfItems, "relations", "item", "add", "Pitch", "--node", "DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C II", "--force", "--text")
    Assert-True ($cfForced.ExitCode -eq 0) ("relations item add --force failed. Output: " + $cfForced.Output)
    Assert-Match $cfForced.Output "d1297pnilu1297cdnilvdnilvpnilvunil" "relations item add --force must add the action."
    if (-not (Skip-WithoutGameData "smoke\clifixes.ps1 (relations item add of an unknown DCS id)")) {
        $cfUnknown = Run-Engine @("--profile", $cfItems, "relations", "item", "add", "Pitch", "--node", "DCS:aNoSuchCommand:F-16C_50", "--text")
        Assert-CfClean $cfUnknown "relations item add of an id no DCS list has"
        Assert-Match $cfUnknown.Output "is not an action of DCS's command list" ("item add must name the unknown id: " + $cfUnknown.Output)
        $cfRoll = Run-Engine @("--profile", $cfItems, "relations", "item", "add", "Pitch", "--node", "DCS:a2002cdnil:F-16C_50", "--text")
        Assert-True ($cfRoll.ExitCode -eq 0) ("relations item add of a real DCS axis must work. Output: " + $cfRoll.Output)
    }

    # ---- 4. relations aircraft --node game:id:<one aircraft>
    $cfAircraft = Run-Engine @("--profile", $cfItems, "relations", "aircraft", "Pitch", "--node", "DCS:a2001cdnil:F-16C_50")
    Assert-True ($cfAircraft.ExitCode -eq 0) ("relations aircraft --node with one aircraft failed. Output: " + $cfAircraft.Output)
    $cfAircraftJson = $cfAircraft.Output | ConvertFrom-Json
    Assert-True (@($cfAircraftJson.Nodes).Count -eq 1 -and $cfAircraftJson.Nodes[0].Id -eq "a2001cdnil") ("relations aircraft --node game:id:F-16C_50 must find the action: " + $cfAircraft.Output)

    # ---- 5. exit codes of the checks: telemetry recover / prune with a broken session, a layout sheet with problems
    $cfTelemetry = Join-Path $cfDir "telemetry"
    New-Item -Path $cfTelemetry -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $cfTelemetry "broken.jptel"), "not a session")
    foreach ($cfAction in @("recover", "prune")) {
        $cfRun = Run-Engine @("telemetry", $cfAction, "--folder", $cfTelemetry, "--text")
        Assert-True ($cfRun.ExitCode -eq 1) ("telemetry $cfAction must end with exit code 1 when a session is broken. Output: " + $cfRun.Output)
        Assert-Match $cfRun.Output "problem: broken\.jptel" ("telemetry $cfAction --text must list the problem: " + $cfRun.Output)
    }
    $cfSheet = Join-Path $cfDir "warthog.callouts.json"
    $cfSaved = Run-Engine @("layout", "callouts", "--model", "thrustmaster-warthog-stick", "--save", $cfSheet, "--text")
    Assert-True ($cfSaved.ExitCode -eq 0) ("layout callouts of the Warthog stick (no problems) must end with 0. Output: " + $cfSaved.Output)
    $cfSheetJson = Get-Content -Path $cfSheet -Raw | ConvertFrom-Json
    $cfFirst = $cfSheetJson.callouts.tables[0]
    $cfRaw = [System.IO.File]::ReadAllText($cfSheet)
    $cfSecondId = [regex]::Escape($cfSheetJson.callouts.tables[1].id)
    $cfRaw = [regex]::Replace($cfRaw, '("id":\s*"' + $cfSecondId + '"[\s\S]*?"x":\s*)[-\d.]+(,\s*"y":\s*)[-\d.]+', '${1}' + $cfFirst.x.ToString([Globalization.CultureInfo]::InvariantCulture) + '${2}' + $cfFirst.y.ToString([Globalization.CultureInfo]::InvariantCulture))
    $cfOverlap = Join-Path $cfDir "overlap.callouts.json"
    [System.IO.File]::WriteAllText($cfOverlap, $cfRaw)
    $cfBadSheet = Run-Engine @("layout", "callouts", "--layout", $cfOverlap, "--text")
    Assert-True ($cfBadSheet.ExitCode -eq 1) ("layout callouts must end with exit code 1 when the sheet check finds problems. Output: " + $cfBadSheet.Output)
    Assert-Match $cfBadSheet.Output "(?m)^Result: problems" ("the overlapping sheet must report problems: " + $cfBadSheet.Output)

    # ---- 6. usage restore <day> takes only a listed day; usage pin a known game and unit
    $cfRestore = Run-Engine @("usage", "restore", "..")
    Assert-CfClean $cfRestore "usage restore .."
    Assert-Match $cfRestore.Output "No usage backup of \.\." ("usage restore .. must be refused: " + $cfRestore.Output)
    Assert-CfClean (Run-Engine @("usage", "restore", "..\..\Usage")) "usage restore with a path"
    $cfPinGame = Run-Engine @("usage", "pin", "NoSuchGame")
    Assert-CfClean $cfPinGame "usage pin of an unknown game"
    Assert-Match $cfPinGame.Output "Unknown game: NoSuchGame" ("usage pin must name the unknown game: " + $cfPinGame.Output)
    $cfPinUnit = Run-Engine @("usage", "pin", "DCS", "No-Such-Aircraft")
    Assert-CfClean $cfPinUnit "usage pin of an aircraft without usage"
    Assert-Match $cfPinUnit.Output "No usage of 'No-Such-Aircraft' in DCS" ("usage pin must name the unknown aircraft: " + $cfPinUnit.Output)

    # ---- 7. export-marks dismiss: aliases name the game; nothing matched is said
    $cfMarks = Run-Engine @("export-marks", "dismiss", "--game", "il2", "--relation", "No Such Relation", "--text")
    Assert-True ($cfMarks.ExitCode -eq 0) ("export-marks dismiss with an alias failed. Output: " + $cfMarks.Output)
    Assert-Match $cfMarks.Output "Warning: No open mark of 'No Such Relation' in IL2Game" ("export-marks dismiss must resolve il2 and say nothing matched: " + $cfMarks.Output)
    Assert-CfClean (Run-Engine @("export-marks", "dismiss", "--game", "NoSuchGame", "--relation", "X")) "export-marks dismiss of an unknown game"

    # ---- 8. devices replace --merge in any case; overlay map --text shows the warnings
    $cfMerge = Run-Engine @("--profile", $cfItems, "devices", "replace", "Old Stick", "New Stick", "--merge", "ADD", "--dry-run", "--text")
    Assert-NotMatch $cfMerge.Output "--merge must be" ("devices replace --merge ADD must be taken like add: " + $cfMerge.Output)
    $cfOverlay = Run-Engine @("--profile", $cfItems, "overlay", "map", "--game", "NoSuchGame", "--text")
    Assert-True ($cfOverlay.ExitCode -eq 0) ("overlay map failed. Output: " + $cfOverlay.Output)
    Assert-Match $cfOverlay.Output "(?m)^Warning: Unknown game: NoSuchGame" ("overlay map --text must show its warnings: " + $cfOverlay.Output)
    $cfMediaJson = Run-Engine @("media", "aircraft")
    $cfMediaText = Run-Engine @("media", "aircraft", "--text")
    Assert-True ($cfMediaJson.ExitCode -eq 0 -and $cfMediaText.ExitCode -eq 0) "media aircraft failed."
    foreach ($cfWarning in @(($cfMediaJson.Output | ConvertFrom-Json).Warnings)) {
        if ($cfWarning) { Assert-True ($cfMediaText.Output.Contains("Warning: " + $cfWarning)) ("media --text must show the warning '" + $cfWarning + "'.") }
    }

    # ---- 9. DCS install commands on a synthetic DCS folder: flags, defaults cut --dry-run and the running-DCS refusal
    $cfDcs = Join-Path $cfDir "DCS World"
    $cfSaved = Join-Path $cfDir "Saved Games\DCS"
    $cfJoy = Join-Path $cfDcs "Mods\aircraft\Test\Input\Test\joystick"
    New-Item -Path $cfJoy -ItemType Directory -Force | Out-Null
    New-Item -Path (Join-Path $cfSaved "Config\Input") -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $cfJoy "default.lua"), "return { keyCommands = { {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN1'}}} }, axisCommands = {} }`n")
    [System.IO.File]::WriteAllText((Join-Path $cfJoy "Test Stick.lua"), "return { keyCommands = { {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN2'}}} }, axisCommands = {} }`n")
    $cfTree = { (Get-ChildItem -Path $cfDcs -Recurse -File | ForEach-Object { $_.FullName + "=" + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "|" }
    $cfBefore = & $cfTree
    $cfInstallArgs = @("--install-path", $cfDcs, "--saved-games", $cfSaved, "--no-load-check")
    $cfStatus = Run-Engine (@("dcs", "strip-defaults", "--status") + $cfInstallArgs + @("--text"))
    Assert-True ($cfStatus.ExitCode -eq 0) ("dcs strip-defaults --status failed. Output: " + $cfStatus.Output)
    Assert-Match $cfStatus.Output "DCS default binds in the install \(status\)" ("dcs strip-defaults --status must show the status, not apply: " + $cfStatus.Output)
    Assert-True ((& $cfTree) -eq $cfBefore) "dcs strip-defaults --status must not change the DCS folder."
    $cfLuasStatus = Run-Engine (@("dcs", "device-luas", "--status") + $cfInstallArgs + @("--text"))
    Assert-True ($cfLuasStatus.ExitCode -eq 0) ("dcs device-luas --status failed. Output: " + $cfLuasStatus.Output)
    Assert-Match $cfLuasStatus.Output "Test Stick\.lua" ("dcs device-luas --status must list the device files: " + $cfLuasStatus.Output)
    $cfStripRemove = Run-Engine (@("dcs", "strip-defaults", "--remove") + $cfInstallArgs)
    Assert-CfClean $cfStripRemove "dcs strip-defaults --remove"
    Assert-Match $cfStripRemove.Output "has no --remove: --restore" ("strip-defaults --remove must point to --restore: " + $cfStripRemove.Output)
    $cfNoDcs = Run-Engine @("dcs", "strip-defaults", "--dry-run", "--install-path", (Join-Path $cfDir "no-dcs"), "--saved-games", $cfSaved, "--no-load-check")
    Assert-True ($cfNoDcs.ExitCode -ne 0) "strip-defaults on a folder without DCS must fail."
    Assert-Match $cfNoDcs.Output "dcs strip-defaults apply failed" ("the failure must name the action: " + $cfNoDcs.Output)
    Assert-NotMatch $cfNoDcs.Output "strip-defaults  failed" "the failure must not have a blank action."
    Assert-True ((& $cfTree) -eq $cfBefore) "the DCS folder must be unchanged after the refused strip-defaults runs."

    $cfBackup = Join-Path $cfSaved "BACKUP_JOYSTICK_DEFAULTS"
    $cfCutDry = Run-Engine @("dcs", "defaults", "cut", $cfSaved, $cfDcs, "--dry-run", "--text")
    Assert-True ($cfCutDry.ExitCode -eq 0) ("dcs defaults cut --dry-run failed. Output: " + $cfCutDry.Output)
    Assert-Match $cfCutDry.Output "(?m)^Dry Run: true" ("dcs defaults cut --dry-run must say so: " + $cfCutDry.Output)
    Assert-Match $cfCutDry.Output "(?m)^Stick-Specific Files Moved: 1" ("dcs defaults cut --dry-run must count the device file: " + $cfCutDry.Output)
    Assert-Match $cfCutDry.Output "(?m)^Default Files Copied: 1" ("dcs defaults cut --dry-run must count default.lua: " + $cfCutDry.Output)
    Assert-True ((& $cfTree) -eq $cfBefore -and -not (Test-Path $cfBackup)) "dcs defaults cut --dry-run must not change anything."

    $cfFakeDir = Join-Path $cfDir "proc"
    New-Item -Path $cfFakeDir -ItemType Directory -Force | Out-Null
    $cfFakeExe = Join-Path $cfFakeDir "DCS_updater.exe"
    Copy-Item -Path (Join-Path $env:SystemRoot "System32\PING.EXE") -Destination $cfFakeExe -Force
    $cfFake = Start-Process -FilePath $cfFakeExe -ArgumentList @("-n", "60", "127.0.0.1") -WindowStyle Hidden -PassThru
    try {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $null
        $cfRunning = Run-Engine @("dcs", "defaults", "cut", $cfSaved, $cfDcs, "--text")
        Assert-True ($cfRunning.ExitCode -ne 0) "dcs defaults cut must refuse while DCS_updater runs."
        Assert-Match $cfRunning.Output "DCS is running \(DCS_updater\)" ("the refusal must name the running process: " + $cfRunning.Output)
        Assert-True ((& $cfTree) -eq $cfBefore -and -not (Test-Path $cfBackup)) "a refused dcs defaults cut must not change anything."
        Assert-True ((Run-Engine @("dcs", "defaults", "cut", $cfSaved, $cfDcs, "--dry-run", "--text")).ExitCode -eq 0) "dcs defaults cut --dry-run must work while DCS runs."
    }
    finally {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $cfPrevious.Ignore
        if (-not $cfFake.HasExited) { Stop-Process -Id $cfFake.Id -Force -ErrorAction SilentlyContinue }
        $cfFake.WaitForExit(5000) | Out-Null
    }
    $cfCut = Run-Engine @("dcs", "defaults", "cut", $cfSaved, $cfDcs, "--text")
    Assert-True ($cfCut.ExitCode -eq 0) ("dcs defaults cut failed. Output: " + $cfCut.Output)
    Assert-True (-not (Test-Path (Join-Path $cfJoy "Test Stick.lua")) -and (Test-Path (Join-Path $cfJoy "default.lua"))) "dcs defaults cut must move the device file and keep default.lua."
    Assert-True (Test-Path (Join-Path $cfBackup "Test\Input\Test\joystick\Test Stick.lua")) "dcs defaults cut must keep the device file in the backup."
    $cfRestoreDcs = Run-Engine @("dcs", "defaults", "restore", $cfSaved, $cfDcs, "--text")
    Assert-True ($cfRestoreDcs.ExitCode -eq 0) ("dcs defaults restore failed. Output: " + $cfRestoreDcs.Output)
    Assert-True ((& $cfTree) -eq $cfBefore) "dcs defaults restore must bring the DCS folder back as it was."

    # ---- 10. Battlefield refresh-db --path <folder>; G-Rebels refresh-db without the game
    $cfBf = Join-Path $cfDir "bf6 settings\steam"
    New-Item -Path $cfBf -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\battlefield\PROFSAVEbf6mp_profile") -Destination (Join-Path $cfBf "PROFSAVEbf6mp_profile")
    $cfBfRefresh = Run-Engine @("bf6", "refresh-db", "--path", (Split-Path -Parent $cfBf), "--dry-run", "--text")
    Assert-True ($cfBfRefresh.ExitCode -eq 0) ("bf6 refresh-db --path <folder> failed. Output: " + $cfBfRefresh.Output)
    Assert-Match $cfBfRefresh.Output ([regex]::Escape((Join-Path $cfBf "PROFSAVEbf6mp_profile"))) ("bf6 refresh-db must read the profile in the given folder: " + $cfBfRefresh.Output)
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $null
    Run-Engine @("games", "install", "disable", "GRebels", "--text") | Out-Null
    $cfGr = Run-Engine @("grebels", "refresh-db", "--dry-run", "--text")
    Assert-True ($cfGr.ExitCode -eq 0) ("grebels refresh-db reads nothing of the game and must work without it. Output: " + $cfGr.Output)
    Assert-Match $cfGr.Output "the game's input assets are not read" ("grebels refresh-db must say it reads nothing of the game: " + $cfGr.Output)
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $cfPrevious.Installed

    # ---- 11. --ignore-devices of the registry games takes commas (needs the WARDOGS list)
    if (-not (Skip-WithoutGameData "smoke\clifixes.ps1 (--ignore-devices with commas)")) {
        $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
        $cfIni = Join-Path $cfDir "GameUserSettings.ini"
        Copy-Item -Path (Join-Path $engineDir "tests\fixtures\wardogs\GameUserSettings.ini") -Destination $cfIni -Force
        $cfIgnore = Run-Engine @("wardogs", "scan", $cfIni, "--ignore-devices", "Smoke Throttle,Smoke Stick", "--text")
        Assert-True ($cfIgnore.ExitCode -eq 0) ("wardogs scan --ignore-devices a,b failed. Output: " + $cfIgnore.Output)
        Assert-NotMatch $cfIgnore.Output "<- Smoke (Throttle|Stick)" ("--ignore-devices with a comma must leave both devices out: " + $cfIgnore.Output)
        Assert-Match $cfIgnore.Output "Ignored devices \(--ignore-device\): Smoke Throttle, Smoke Stick;" ("--ignore-devices must list both devices: " + $cfIgnore.Output)
        $env:JOYPRO_FAKE_DEVICES = $cfPrevious.Devices
    }
}
finally {
    $env:JOYPRO_DATA_DIR = $cfPrevious.Data
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $cfPrevious.Ignore
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $cfPrevious.Installed
    $env:JOYPRO_FAKE_DEVICES = $cfPrevious.Devices
}
}
