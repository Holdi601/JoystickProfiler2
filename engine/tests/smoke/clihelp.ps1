# What the help says matches what the engine does (the CLI pages of the website quote it), and the small behaviour fixes
# that came out of comparing them: the DCS refresh runs headless unless asked otherwise, the base axis tuning says what it
# does not keep, the export notes of the games that are not flight-only, MSFS 2024-only options on MSFS 2020, every word
# of devices kb list, and --ignore-device on game imports.
# Dot-sourced by engine_smoke_test.ps1 ($engineDir, $tempDir, Run-Engine, Assert-*, Skip-WithoutGameData). Works on temp
# folders only; the parts that read a game's command list run only with the development game lists.
$chDir = Join-Path $tempDir "clihelp"
New-Item -Path $chDir -ItemType Directory -Force | Out-Null

# --- Help: export notes of the games that are not flight-only, MSFS 2024-only options, binds unbind --game
$chHelp = Run-Engine @("--help")
Assert-True ($chHelp.ExitCode -eq 0) "engine --help failed."
$chLine = {
    param([string]$Prefix)
    $m = [regex]::Match($chHelp.Output, "(?m)^  engine " + [regex]::Escape($Prefix) + "\b.*$")
    return $m.Value
}
$chAc8 = & $chLine "ac8 export"
Assert-Match $chAc8 "writes nothing" "ac8 export help must say that the export writes nothing."
Assert-NotMatch $chAc8 "aircraft/helicopter" "ac8 export help must not claim aircraft/helicopter actions."
$chHllv = & $chLine "hllv export"
Assert-Match $chHllv "writes only the keyboard pilot keys, with --include-keyboard" "hllv export help must say that only keyboard pilot keys are written."
Assert-NotMatch $chHllv "aircraft/helicopter" "hllv export help must not claim aircraft/helicopter actions."
foreach ($chShip in @("elite export", "x4 export")) {
    $chShipLine = & $chLine $chShip
    Assert-Match $chShipLine "only ship flight actions" "$chShip help must name the ship flight actions."
    Assert-NotMatch $chShipLine "aircraft/helicopter" "$chShip help must not claim aircraft/helicopter actions."
}
Assert-Match (& $chLine "warthunder export") "only aircraft/helicopter actions" "warthunder export help keeps its aircraft/helicopter note."
Assert-Match (& $chLine "msfs2024 import") "--limit-to-profile-aircraft" "msfs2024 import help must list --limit-to-profile-aircraft."
Assert-NotMatch (& $chLine "msfs2020 import") "--limit-to-profile-aircraft" "msfs2020 import help must not list the MSFS 2024-only --limit-to-profile-aircraft."
Assert-NotMatch (& $chLine "msfs2020 export") "--user-profiles|--import-files" "msfs2020 export help must not list the MSFS 2024-only options."
Assert-Match (& $chLine "wardogs import") "--ignore-device <name>" "game import help must list --ignore-device."
Assert-Match (& $chLine "binds unbind") "--game <game id>" "binds unbind help must take any game id."
Assert-Match (& $chLine "dcs refresh clean") "--confirm true\|false" "dcs refresh clean help must list --confirm."
Assert-Match (& $chLine "dcs refresh id") "--confirm true\|false" "dcs refresh id help must list --confirm."
Assert-Match (& $chLine "profile merge") "--overwrite-mode left\|right\|ask.*--save-profile" "profile merge help must list --overwrite-mode and --save-profile."
Assert-Match (& $chLine "db summary") "^  engine db summary \[filter\]" "db summary help must show the filter word."
Assert-Match (& $chLine "db check") "^  engine db check \[filter\]" "db check help must show the filter word."
Assert-Match (& $chLine "vjoy restore-hidhide") "--force" "vjoy restore-hidhide help must list --force."
Assert-Match (& $chLine "vjoy configure-vjoy") "exit code 2" "vjoy configure-vjoy help must name exit code 2 for a declined prompt."
Assert-NotMatch (& $chLine "vjoy configure-vjoy") "never shrinks" "vjoy configure-vjoy must not claim it never shrinks a device."
foreach ($chCmd in @("relations plan ask", "relations plan apply", "relations ai-matrix", "relations phrase-check")) {
    Assert-True ((& $chLine $chCmd).Length -gt 0) "help must list engine $chCmd."
}
Assert-Match (& $chLine "cockpits") "pictures build \[--game DCS\|XPlane12\|MSFS2020\|IL2Game\|NuclearOption\|WarThunder\|StarCitizen\|all\]" "cockpits help must list every game pictures build takes."
Assert-Match (& $chLine "cockpits") "guide <aircraft>" "cockpits help must list the guide verb."
Assert-Match (& $chLine "setup plan") "--out-plan <plan.json>.*" "setup plan help must list --out-plan on its first line."
$chSetupUnknown = Run-Engine @("setup", "nonsense")
Assert-Match $chSetupUnknown.Output "check" "the unknown setup action message must list check."

# --- dcs refresh clean / id: headless (DCS's own Lua, DCS not started) unless --headless-lua false is given
$chSaved = Join-Path $chDir "dcs-saved"
$chInstall = Join-Path $chDir "dcs-install"
$chJoystick = Join-Path $chInstall "Mods\aircraft\A-10C\Input\A-10C\joystick"
New-Item -Path $chJoystick -ItemType Directory -Force | Out-Null
New-Item -Path (Join-Path $chSaved "Config\Input") -ItemType Directory -Force | Out-Null
'return {}' | Set-Content -Path (Join-Path $chJoystick "default.lua") -Encoding UTF8
foreach ($chAction in @("clean", "id")) {
    $chDefault = Run-Engine @("dcs", "refresh", $chAction, $chSaved, $chInstall, "--plane", "A-10C", "--dry-run", "--confirm", "false", "--text")
    Assert-True ($chDefault.ExitCode -eq 0) "dcs refresh $chAction --dry-run without --headless-lua failed. Output: $($chDefault.Output)"
    Assert-Match $chDefault.Output "(?m)^Headless Lua:\s+true" "dcs refresh $chAction must run headless when --headless-lua is not given."
    Assert-NotMatch $chDefault.Output "--launch is ignored" "the default --launch must not warn in headless mode (dcs refresh $chAction)."
    $chClick = Run-Engine @("dcs", "refresh", $chAction, $chSaved, $chInstall, "--headless-lua", "false", "--launch", "false", "--dry-run", "--confirm", "false",
        "--window-left", "0", "--window-top", "0", "--window-width", "1920", "--window-height", "1080", "--text")
    Assert-True ($chClick.ExitCode -eq 0) "dcs refresh $chAction --headless-lua false --dry-run failed. Output: $($chClick.Output)"
    Assert-Match $chClick.Output "(?m)^Headless Lua:\s+false" "dcs refresh $chAction --headless-lua false must use the click flow."
}
$chPreview = Run-Engine @("dcs", "refresh", "preview-clicks", $chSaved, $chInstall, "--window-left", "0", "--window-top", "0", "--window-width", "1920", "--window-height", "1080",
    "--preview-image", (Join-Path $chDir "preview.png"), "--text")
Assert-True ($chPreview.ExitCode -eq 0) "dcs refresh preview-clicks must not turn headless by default. Output: $($chPreview.Output)"

# --- binds tuning set --scope base: --param and --curve-points are not stored there, and the result says so
$chProfile = Join-Path $chDir "tuning.pr0file"
$chAxis = "CLI Help Axis"
$chAdd = Run-Engine @("--profile", $chProfile, "relations", "add", $chAxis, "--axis", "--node", "DCS:clihelp_axis_smoke:A-10C_2", "--device", "Help Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_X", "--text")
Assert-True ($chAdd.ExitCode -eq 0) "relations add (tuning fixture) failed. Output: $($chAdd.Output)"
$chBase = Run-Engine @("--profile", $chProfile, "binds", "tuning", "set", $chAxis, "--scope", "base", "--deadzone", "0.1", "--param", "sensitivity=22", "--curve-points", "0:0,0.5:0.3,1:1", "--text")
Assert-True ($chBase.ExitCode -eq 0) "binds tuning set --scope base failed. Output: $($chBase.Output)"
Assert-Match $chBase.Output "(?m)^Set base: .*dz=0\.1" "binds tuning set --scope base must show the stored dead zone."
Assert-NotMatch $chBase.Output "(?m)^Set base: .*(sensitivity|cp=)" "binds tuning set --scope base must not print a parameter or curve points as set."
Assert-Match $chBase.Output "Not set: --param and --curve-points do not apply to the base scope" "binds tuning set --scope base must say that --param and --curve-points were not stored."
$chBasePlain = Run-Engine @("--profile", $chProfile, "binds", "tuning", "set", $chAxis, "--scope", "base", "--deadzone", "0.05", "--text")
Assert-NotMatch $chBasePlain.Output "Not set:" "binds tuning set --scope base without --param must not add a note."
$chGame = Run-Engine @("--profile", $chProfile, "binds", "tuning", "set", $chAxis, "--scope", "game:IL2Korea", "--param", "sensitivity=22", "--text")
Assert-Match $chGame.Output "sensitivity=22" "binds tuning set on a game scope keeps --param."
Assert-NotMatch $chGame.Output "Not set:" "binds tuning set on a game scope must not add the base note."

# --- MSFS 2020 takes the MSFS 2024-only options without failing and says that they change nothing
$chMsfsFile = Join-Path $chDir "msfs2020-empty.xml"
New-Item -Path $chMsfsFile -ItemType File -Force | Out-Null
$chMsfs = Run-Engine @("msfs2020", "scan", $chMsfsFile, "--user-profiles", "--import-files", "--text")
Assert-NotMatch $chMsfs.Output "only for Microsoft Flight Simulator 2024" "msfs2020 must not refuse --user-profiles / --import-files."
Assert-Match $chMsfs.Output "--user-profiles, --import-files apply to Microsoft Flight Simulator 2024 only; Microsoft Flight Simulator 2020 ignored them\." "msfs2020 must say that the MSFS 2024 options change nothing."
$chMsfsLimit = Run-Engine @("--profile", (Join-Path $chDir "msfs2020.pr0file"), "msfs2020", "import", $chMsfsFile, "--limit-to-profile-aircraft", "--text")
Assert-Match $chMsfsLimit.Output "--limit-to-profile-aircraft applies to Microsoft Flight Simulator 2024 only" "msfs2020 import must say that --limit-to-profile-aircraft changes nothing."

# --- devices kb list: every word counts
$previousChFakeDevices = $env:JOYPRO_FAKE_DEVICES
$previousChDeviceKb = $env:JOYPRO_DEVICE_KB
$env:JOYPRO_DEVICE_KB = Join-Path $engineDir "tests\fixtures\autoassign\kb"
try {
    $chKbOne = Run-Engine @("devices", "kb", "list", "grip", "b", "--text")
    Assert-True ($chKbOne.ExitCode -eq 0) "devices kb list grip b failed. Output: $($chKbOne.Output)"
    Assert-Match $chKbOne.Output "(?m)^  - aa-grip-b \|" "devices kb list grip b must find AA Grip B."
    Assert-NotMatch $chKbOne.Output "(?m)^  - aa-grip-a \|" "devices kb list grip b must use every word (not only 'grip')."
    $chKbQuoted = Run-Engine @("devices", "kb", "list", "test base", "--text")
    Assert-Match $chKbQuoted.Output "(?m)^Device Catalog: 1\s*$" "devices kb list 'test base' (one argument) must find exactly one device."
    Assert-Match $chKbQuoted.Output "(?m)^  - aa-base \|" "devices kb list 'test base' must find AA Test Base."
    $chKbAll = Run-Engine @("devices", "kb", "list", "grip", "--text")
    Assert-Match $chKbAll.Output "(?m)^  - aa-grip-a \|" "devices kb list grip must still find both grips (a)."
    Assert-Match $chKbAll.Output "(?m)^  - aa-grip-b \|" "devices kb list grip must still find both grips (b)."
}
finally {
    $env:JOYPRO_DEVICE_KB = $previousChDeviceKb
}

# --- Game scan / import with --ignore-device: the binds of those devices are left out (needs the WARDOGS list)
if (-not (Skip-WithoutGameData "smoke\clihelp.ps1 (--ignore-device on a game import)")) {
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
    try {
        $chIni = Join-Path $chDir "GameUserSettings.ini"
        Copy-Item -Path (Join-Path $engineDir "tests\fixtures\wardogs\GameUserSettings.ini") -Destination $chIni -Force
        $chScanAll = Run-Engine @("wardogs", "scan", $chIni, "--text")
        Assert-Match $chScanAll.Output "Smoke Throttle \{66666666" "wardogs scan (fixture) should read binds of the throttle."
        $chScan = Run-Engine @("wardogs", "scan", $chIni, "--ignore-device", "Smoke Throttle", "--text")
        Assert-True ($chScan.ExitCode -eq 0) "wardogs scan --ignore-device failed. Output: $($chScan.Output)"
        Assert-NotMatch $chScan.Output "<- Smoke Throttle" "wardogs scan --ignore-device 'Smoke Throttle' must leave every bind of that device out."
        Assert-Match $chScan.Output "<- Smoke Stick" "wardogs scan --ignore-device must keep the other devices."
        Assert-Match $chScan.Output "binds left out: 4" "wardogs scan --ignore-device must say how many binds it left out."
        $chImportProfile = Join-Path $chDir "ignore.pr0file"
        $chImport = Run-Engine @("--profile", $chImportProfile, "wardogs", "import", $chIni, "--ignore-devices", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--text")
        Assert-True ($chImport.ExitCode -eq 0) "wardogs import --ignore-devices failed. Output: $($chImport.Output)"
        $chBinds = Run-Engine @("--profile", $chImportProfile, "binds", "list", "--text")
        Assert-NotMatch $chBinds.Output "Smoke Stick" "wardogs import --ignore-devices must not import binds of the ignored device."
        Assert-Match $chBinds.Output "Smoke Throttle" "wardogs import --ignore-devices must import the other devices."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousChFakeDevices
    }
}
$env:JOYPRO_FAKE_DEVICES = $previousChFakeDevices
