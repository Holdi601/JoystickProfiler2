# DCS export modes and import of the active defaults (2.5.0; dot-sourced by engine_smoke_test.ps1: Run-Engine, Assert-*,
# $tempDir). A made-up aircraft "SmokeModes" with a generic clean profile (Fire BTN1, Gear BTN2, Flaps BTN3, Pitch JOY_Y)
# and a second one "SmokeModesOther" that is never exported. Devices: Stick and Box (files, connected), Extra
# (connected, no file), Gone (a file, not connected). Checks:
#   - import with include defaults takes every default DCS has active (generic ones, a connected device without a file);
#   - clean + nuke: the diff holds only real differences (no default removed and added again), the other aircraft stay
#     byte for byte, a device without differences gets no file, a not connected device is not nuked; a re-import gives
#     the same binds (round trip); the written files load in DCS's luae.exe when DCS is installed;
#   - a default with other axis tuning is written as DCS's "changed";
#   - add never deletes: a profile bind on a button the game uses for another function is not written, the export
#     lists it and the profile marks it (export-marks); a later export that writes it clears the mark;
#   - merge removes the game bind on that button but keeps the moved action's old button; merge-overwrite also takes
#     the profile's actions off their other buttons (also on another device);
#   - profile game-state before and after the import; undo export restores every file byte for byte.
$emUtf8 = New-Object System.Text.UTF8Encoding($false)
$emRoot = Join-Path $tempDir "dcs-export-modes"
$emStick = "Smoke Mode Stick {44444444-0000-0000-0000-000000000001}"
$emBox = "Smoke Mode Box {44444444-0000-0000-0000-000000000002}"
$emExtra = "Smoke Mode Extra {44444444-0000-0000-0000-000000000003}"
$emGone = "Smoke Mode Gone {44444444-0000-0000-0000-000000000004}"

function New-EmEntry([string]$Id, [string]$Name, [string]$Section, [string[]]$Keys, [string]$Filter = "") {
    $items = ""
    $i = 1
    foreach ($key in $Keys) {
        $f = if ($Filter) { " [`"filter`"] = { " + $Filter + " }," } else { "" }
        $items += "        [" + $i + "] = { [`"key`"] = `"" + $key + "`"," + $f + " },`n"
        $i++
    }
    return "    [`"" + $Id + "`"] = {`n      [`"name`"] = `"" + $Name + "`",`n      [`"" + $Section + "`"] = {`n" + $items + "      },`n    },`n"
}
function Write-EmFile([string]$Path, [string[]]$Keys, [string[]]$Axes = @()) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, "local diff = {`n  [`"axisDiffs`"] = {`n" + ($Axes -join "") + "  },`n  [`"keyDiffs`"] = {`n" + ($Keys -join "") + "  },`n}`nreturn diff`n", $emUtf8)
}
function New-EmSaved([string]$Name) {
    $saved = Join-Path $emRoot $Name
    $input = Join-Path $saved "Config\Input"
    Write-EmFile (Join-Path $input ("SmokeModes\joystick\" + $emStick + ".diff.lua")) @(
        (New-EmEntry "d_trim" "Trim" "added" @("JOY_BTN5")), (New-EmEntry "d_cms" "CMS" "added" @("JOY_BTN6")), (New-EmEntry "d_flaps" "Flaps" "removed" @("JOY_BTN3")))
    Write-EmFile (Join-Path $input ("SmokeModes\joystick\" + $emBox + ".diff.lua")) @(
        (New-EmEntry "d_light" "Light" "added" @("JOY_BTN1")), (New-EmEntry "d_cms" "CMS" "added" @("JOY_BTN8")))
    Write-EmFile (Join-Path $input ("SmokeModes\joystick\" + $emGone + ".diff.lua")) @((New-EmEntry "d_gone" "Gone Thing" "added" @("JOY_BTN9")))
    Write-EmFile (Join-Path $input ("SmokeModesOther\joystick\" + $emStick + ".diff.lua")) @((New-EmEntry "d_x" "Other Thing" "added" @("JOY_BTN7")))
    return $saved
}
function Get-EmHashes([string]$Saved) {
    $h = @{}
    Get-ChildItem -Path $Saved -Recurse -File | ForEach-Object { $h[$_.FullName.Substring($Saved.Length)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    return $h
}
function Get-EmBinds([string]$Profile) {
    $list = Run-Engine @("--profile", $Profile, "--remap-missing-devices", "false", "relations", "list")
    Assert-True ($list.ExitCode -eq 0) ("relations list failed: " + $list.Output)
    $out = @()
    foreach ($r in @(($list.Output | ConvertFrom-Json).Relations)) {
        foreach ($n in @($r.Nodes)) {
            foreach ($p in @($n.Aircraft.PSObject.Properties | Where-Object { $_.Value })) {
                $key = if ($r.Bind.JButton) { $r.Bind.JButton } else { $r.Bind.JAxis }
                $out += ($p.Name + "|" + $n.Id + "|" + $r.Bind.Joystick + "|" + $key)
            }
        }
    }
    return @($out | Sort-Object -Unique)
}
function ConvertFrom-EmJson([string]$Text) {
    # The JSON without the "Auto-saved profile: ..." line a run that saves the profile prints after it.
    return (($Text -split "`r?`n" | Where-Object { $_ -notmatch "^Auto-saved profile:" }) -join "`n") | ConvertFrom-Json
}
function Find-EmRelation([string]$Profile, [string]$DevicePrefix, [string]$Key) {
    $list = Run-Engine @("--profile", $Profile, "--remap-missing-devices", "false", "relations", "list")
    return @(($list.Output | ConvertFrom-Json).Relations | Where-Object { $_.Bind.Joystick -like ($DevicePrefix + "*") -and ($_.Bind.JButton -eq $Key -or $_.Bind.JAxis -eq $Key) } | ForEach-Object { $_.Name })[0]
}

$emClean = Join-Path $env:JOYPRO_DATA_DIR "GameData\CleanProfile\DCS"
$emCleanExisted = Test-Path $emClean
$emMade = @((Join-Path $emClean "SmokeModes.cf"), (Join-Path $emClean "SmokeModesOther.cf"))
$emPreviousFake = $env:JOYPRO_FAKE_DEVICES
try {
    Write-EmFile (Join-Path $emClean "SmokeModes.cf") @((New-EmEntry "d_fire" "Fire" "removed" @("JOY_BTN1")), (New-EmEntry "d_gear" "Gear" "removed" @("JOY_BTN2")),
        (New-EmEntry "d_flaps" "Flaps" "removed" @("JOY_BTN3"))) @((New-EmEntry "a_pitch" "Pitch" "removed" @("JOY_Y")))
    Write-EmFile (Join-Path $emClean "SmokeModesOther.cf") @((New-EmEntry "d_o" "Other Default" "removed" @("JOY_BTN1")))
    $emFake = Join-Path $emRoot "devices.tsv"
    New-Item -ItemType Directory -Force -Path $emRoot | Out-Null
    [System.IO.File]::WriteAllLines($emFake, @(($emStick + "`t1234`t0001`tSmoke Mode Stick"), ($emBox + "`t1234`t0002`tSmoke Mode Box"), ($emExtra + "`t1234`t0003`tSmoke Mode Extra")), $emUtf8)
    $env:JOYPRO_FAKE_DEVICES = $emFake

    # --- game-state and import -------------------------------------------------------------------------------------
    $emSaved = New-EmSaved "rt"
    $emProfile = Join-Path $emRoot "modes.pr0file"
    $emState = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "profile", "game-state", "--game", "DCS", "--path", $emSaved)
    Assert-True ($emState.ExitCode -eq 0) ("profile game-state failed: " + $emState.Output)
    $emStateJson = ConvertFrom-EmJson $emState.Output
    Assert-True (-not $emStateJson.Imported -and -not $emStateJson.Exported -and $emStateJson.GameHasBinds) "game-state of a new profile: neither imported nor exported, the game has binds of its own."

    $emImport = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "import", $emSaved, "--planes", "SmokeModes", "--include-defaults", "--confirm", "false", "--text")
    Assert-True ($emImport.ExitCode -eq 0) ("dcs import with defaults failed: " + $emImport.Output)
    Assert-Match $emImport.Output "Connected Devices Without A File \(defaults imported\):\s+1\b" "the connected device without a file gets the aircraft's defaults."
    Assert-Match $emImport.Output "Generic Joystick Defaults \(default.lua, imported\):\s+1[0-9]\b" "DCS's generic defaults are imported for every imported device."
    $emState2 = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "profile", "game-state", "--game", "DCS", "--path", $emSaved)
    Assert-True ((ConvertFrom-EmJson $emState2.Output).Imported) "game-state after the import: imported."
    $emBindsBefore = Get-EmBinds $emProfile
    Assert-True ($emBindsBefore -contains ("SmokeModes|d_fire|" + $emStick + "|JOY_BTN1")) "the stick's active Fire default is in the profile."
    Assert-True (-not ($emBindsBefore -contains ("SmokeModes|d_flaps|" + $emStick + "|JOY_BTN3"))) "a default the user's file removes is not in the profile."
    Assert-True (-not ($emBindsBefore -contains ("SmokeModes|d_fire|" + $emBox + "|JOY_BTN1"))) "a default on an input the user uses on that device is left out."
    Assert-True ($emBindsBefore -contains ("SmokeModes|a_pitch|" + $emExtra + "|JOY_Y")) "the device without a file has the aircraft's defaults."

    # --- clean + nuke: minimal diff, other aircraft untouched, round trip, undo ------------------------------------
    $emBefore = Get-EmHashes $emSaved
    $emProfileCopy = Join-Path $emRoot "modes-base.pr0file"
    Copy-Item -LiteralPath $emProfile -Destination $emProfileCopy -Force
    $emExport = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "export", "--path", $emSaved, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($emExport.ExitCode -eq 0) ("dcs export clean+nuke failed: " + $emExport.Output)
    $emAfter = Get-EmHashes $emSaved
    $emOther = "\Config\Input\SmokeModesOther\joystick\" + $emStick + ".diff.lua"
    Assert-True ($emAfter[$emOther] -eq $emBefore[$emOther]) "clean + nuke must not touch an aircraft that is not exported."
    $emGoneFile = "\Config\Input\SmokeModes\joystick\" + $emGone + ".diff.lua"
    Assert-True ($emAfter.ContainsKey($emGoneFile)) "the not connected device keeps its file."
    Assert-True (-not $emAfter.ContainsKey("\Config\Input\SmokeModes\joystick\" + $emExtra + ".diff.lua")) "a device whose binds are all DCS defaults gets no file."
    $emStickText = [System.IO.File]::ReadAllText((Join-Path $emSaved ("Config\Input\SmokeModes\joystick\" + $emStick + ".diff.lua")))
    Assert-True ($emStickText -notmatch "JOY_BTN1" -and $emStickText -notmatch "JOY_BTN2" -and $emStickText -notmatch "JOY_Y") "the stick's diff must not remove and re-add its kept defaults (Fire, Gear, Pitch)."
    Assert-True ($emStickText -match "JOY_BTN5" -and $emStickText -match "JOY_BTN3") "the stick's diff keeps the user's own bind and the removed default."
    foreach ($written in @(Get-ChildItem -Path (Join-Path $emSaved "Config\Input\SmokeModes") -Recurse -File)) {
        $bytes = [System.IO.File]::ReadAllBytes($written.FullName)
        Assert-True (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) ("no BOM in " + $written.Name)
    }
    $emReimport = Join-Path $emRoot "modes-reimport.pr0file"
    $emRe = Run-Engine @("--profile", $emReimport, "--remap-missing-devices", "false", "dcs", "import", $emSaved, "--planes", "SmokeModes", "--include-defaults", "--confirm", "false")
    Assert-True ($emRe.ExitCode -eq 0) ("re-import after clean+nuke failed: " + $emRe.Output)
    Assert-SetEquivalent (Get-EmBinds $emReimport) $emBindsBefore "import -> clean + nuke export -> import must give the same binds (round trip)."
    $emLuae = "G:\Program Files\Eagle Dynamics\DCS World\bin-mt\luae.exe"
    if (Test-Path $emLuae) {
        $emScript = @()
        foreach ($written in @(Get-ChildItem -Path (Join-Path $emSaved "Config\Input\SmokeModes") -Recurse -File | Where-Object { $_.Name -like "*.lua" -or $_.Name -like "*.jp" })) {
            $emScript += "do local f, e = loadfile([[" + $written.FullName + "]]); if not f then print('LOADFAIL ' .. tostring(e)) end end"
        }
        $emScript += "print('LUAE_OK')"
        $emScriptPath = Join-Path $emRoot "load.lua"
        [System.IO.File]::WriteAllLines($emScriptPath, $emScript, $emUtf8)
        $emLuaeOut = & $emLuae $emScriptPath 2>&1 | Out-String
        Assert-True ($emLuaeOut -match "LUAE_OK" -and $emLuaeOut -notmatch "LOADFAIL") ("every written file must load in DCS's luae.exe: " + $emLuaeOut)
    }
    else {
        Write-Host "SKIP (no DCS install): luae load of the written files"
    }
    $emUndo = Run-Engine @("backups", "restore", "latest", "--game", "DCS", "--text")
    Assert-True ($emUndo.ExitCode -eq 0) ("undo export failed: " + $emUndo.Output)
    $emUndone = Get-EmHashes $emSaved
    Assert-SetEquivalent @($emUndone.Keys | ForEach-Object { $_ + "=" + $emUndone[$_] }) @($emBefore.Keys | ForEach-Object { $_ + "=" + $emBefore[$_] }) "undo export after clean + nuke must restore every file byte for byte."

    # --- changed tuning of a default ---------------------------------------------------------------------------------
    $emPitch = Find-EmRelation $emProfile "Smoke Mode Stick" "JOY_Y"
    Assert-True (-not [string]::IsNullOrWhiteSpace($emPitch)) "the stick's Pitch default relation exists."
    $emTune = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "binds", "set", $emPitch, "--deadzone", "0.1")
    Assert-True ($emTune.ExitCode -eq 0) ("binds set --deadzone failed: " + $emTune.Output)
    $emSavedT = New-EmSaved "tuning"
    $emExportT = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "export", "--path", $emSavedT, "--mode", "clean", "--text")
    Assert-True ($emExportT.ExitCode -eq 0) ("export with a tuned default failed: " + $emExportT.Output)
    $emStickT = [System.IO.File]::ReadAllText((Join-Path $emSavedT ("Config\Input\SmokeModes\joystick\" + $emStick + ".diff.lua")))
    Assert-Match $emStickT '\["changed"\]' "a default with another tuning is written as DCS's changed."
    Assert-NotMatch $emStickT '\["removed"\][^}]*"JOY_Y"' "the tuned default must not be removed."
    Copy-Item -LiteralPath $emProfileCopy -Destination $emProfile -Force

    # --- add / merge / merge-overwrite after a move ------------------------------------------------------------------
    # Trim moves from the stick's BTN5 to BTN2 (the Gear default's button); the box's CMS (BTN8) leaves the profile.
    # The Gear default on the stick's BTN2 leaves the profile first (as if the user had not imported it): in the game it
    # is then a bind of another function on the button Trim moves to.
    $emGear = Find-EmRelation $emProfile "Smoke Mode Stick" "JOY_BTN2"
    Assert-True ((Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "relations", "mass", "delete", "--relation", $emGear)).ExitCode -eq 0) "removing the stick's Gear relation failed."
    $emTrim = Find-EmRelation $emProfile "Smoke Mode Stick" "JOY_BTN5"
    Assert-True ((Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "binds", "set", $emTrim, "--input", "JOY_BTN2", "--format", "dcs")).ExitCode -eq 0) "moving Trim failed."
    $emBoxCms = Find-EmRelation $emProfile "Smoke Mode Box" "JOY_BTN8"
    Assert-True ((Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "relations", "mass", "delete", "--relation", $emBoxCms)).ExitCode -eq 0) "removing the box's CMS relation failed."
    Copy-Item -LiteralPath $emProfile -Destination $emProfileCopy -Force

    $emSavedA = New-EmSaved "add"
    $emAddBefore = Get-EmHashes $emSavedA
    $emAdd = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "export", "--path", $emSavedA, "--mode", "add")
    Assert-True ($emAdd.ExitCode -eq 0) ("export add failed: " + $emAdd.Output)
    $emAddJson = ConvertFrom-EmJson $emAdd.Output
    $emSkipped = @($emAddJson.SkippedTakenInputs | Where-Object { $_.ActionId -eq "d_trim" -and $_.Input -eq "JOY_BTN2" })
    Assert-True ($emSkipped.Count -eq 1 -and $emSkipped[0].GameActionId -eq "d_gear" -and $emSkipped[0].Aircraft -eq "SmokeModes") "add must list the bind it did not write and the game's function on that button."
    $emStickA = [System.IO.File]::ReadAllText((Join-Path $emSavedA ("Config\Input\SmokeModes\joystick\" + $emStick + ".diff.lua")))
    Assert-True ($emStickA -match "JOY_BTN5" -and $emStickA -notmatch '"JOY_BTN2"') "add keeps the old Trim button and writes nothing on the Gear button."
    $emBoxA = [System.IO.File]::ReadAllText((Join-Path $emSavedA ("Config\Input\SmokeModes\joystick\" + $emBox + ".diff.lua")))
    Assert-Match $emBoxA "JOY_BTN8" "add never deletes (the box's CMS stays)."
    $emMarks = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "export-marks", "list", "--game", "DCS")
    $emMark = @((ConvertFrom-EmJson $emMarks.Output).Marks | Where-Object { $_.ActionId -eq "d_trim" })
    Assert-True ($emMark.Count -eq 1 -and $emMark[0].GameAction -eq "Gear" -and $emMark[0].Relation -eq $emTrim -and -not $emMark[0].Dismissed) "the profile marks the bind add did not write."
    $emDismiss = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "export-marks", "dismiss", "--game", "DCS", "--relation", $emTrim)
    Assert-True ($emDismiss.ExitCode -eq 0 -and @((ConvertFrom-EmJson $emDismiss.Output).Marks | Where-Object { $_.Dismissed }).Count -eq 1) "export-marks dismiss keeps the mark as 'kept as is'."

    $emMerge = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "export", "--path", $emSavedA, "--mode", "merge", "--text")
    Assert-True ($emMerge.ExitCode -eq 0) ("export merge failed: " + $emMerge.Output)
    $emMarks2 = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "export-marks", "list", "--game", "DCS")
    Assert-True (@((ConvertFrom-EmJson $emMarks2.Output).Marks).Count -eq 0) "the mark clears when a later export writes the bind."
    $emStickM = [System.IO.File]::ReadAllText((Join-Path $emSavedA ("Config\Input\SmokeModes\joystick\" + $emStick + ".diff.lua")))
    Assert-Match $emStickM '"d_gear"[\s\S]*\["removed"\][\s\S]*"JOY_BTN2"' "merge removes the game's Gear default from the button Trim now uses."
    Assert-True ($emStickM -match "JOY_BTN5") "merge keeps the moved action's old button."
    $emBoxM = [System.IO.File]::ReadAllText((Join-Path $emSavedA ("Config\Input\SmokeModes\joystick\" + $emBox + ".diff.lua")))
    Assert-Match $emBoxM "JOY_BTN8" "merge keeps the game's other binds (the box's CMS)."

    Copy-Item -LiteralPath $emProfileCopy -Destination $emProfile -Force
    $emSavedO = New-EmSaved "overwrite"
    $emOver = Run-Engine @("--profile", $emProfile, "--remap-missing-devices", "false", "dcs", "export", "--path", $emSavedO, "--mode", "merge-overwrite", "--text")
    Assert-True ($emOver.ExitCode -eq 0) ("export merge-overwrite failed: " + $emOver.Output)
    $emStickO = [System.IO.File]::ReadAllText((Join-Path $emSavedO ("Config\Input\SmokeModes\joystick\" + $emStick + ".diff.lua")))
    Assert-True ($emStickO -notmatch '"JOY_BTN5"' -and $emStickO -match '"JOY_BTN2"') "merge-overwrite moves Trim: the old button goes."
    $emBoxO = [System.IO.File]::ReadAllText((Join-Path $emSavedO ("Config\Input\SmokeModes\joystick\" + $emBox + ".diff.lua")))
    Assert-True ($emBoxO -notmatch '"JOY_BTN8"') "merge-overwrite takes the profile's CMS off its other button on another device."
    Assert-Match $emOver.Output "Other Buttons Of Profile Actions Removed \(Profile Wins\):\s+[1-9]" "merge-overwrite reports the other buttons it removed."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $emPreviousFake
    foreach ($made in $emMade) {
        Remove-Item -LiteralPath $made -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $emCleanExisted) { Remove-Item -LiteralPath $emClean -Recurse -Force -ErrorAction SilentlyContinue }
}
