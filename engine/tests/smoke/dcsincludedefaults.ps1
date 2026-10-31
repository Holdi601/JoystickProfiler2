# DCS import with "include defaults" (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir). Two made-up
# aircraft with clean profiles in the game-data folder (generic joystick defaults, defaults of the "Smoke Model Stick"
# model, keyboard defaults) and a synthetic Saved Games folder. Checks:
#   - the model's defaults, DCS's generic joystick defaults (default.lua, here on a button box) and the keyboard
#     defaults are added, with their modifiers (LCtrl+F and F stay apart) (2.5.0: every default active in the game);
#   - an imported device gets the defaults of an imported aircraft it has no file in (SmokeDefB: stick and box);
#   - a default the user's file removes, an action the user bound himself on that device (keyboard: on any device), a
#     default on an input the user uses for another action, the second action of an input DCS gives two actions, and a
#     keyboard default of an action a joystick default has are all left out;
#   - the same default function of two aircraft is one relation; aircraft without a file of the user get nothing;
#   - the check finds no error; the only action in several relations is DCS's generic Fire of SmokeDefB on both devices.
$idUtf8 = New-Object System.Text.UTF8Encoding($false)
$idRoot = Join-Path $tempDir "dcs-include-defaults"
$idSaved = Join-Path $idRoot "saved"
$idStick = "Smoke Model Stick {33333333-0000-0000-0000-000000000001}"
$idBox = "Smoke Box {33333333-0000-0000-0000-000000000002}"
function New-IdEntry([string]$Id, [string]$Name, [string]$Section, [string[]]$Keys, [string]$Reformer = "") {
    $items = ""
    $i = 1
    foreach ($key in $Keys) {
        $ref = if ($Reformer) { " [`"reformers`"] = { [1] = `"" + $Reformer + "`", }," } else { "" }
        $items += "        [" + $i + "] = { [`"key`"] = `"" + $key + "`"," + $ref + " },`n"
        $i++
    }
    return "    [`"" + $Id + "`"] = {`n      [`"name`"] = `"" + $Name + "`",`n      [`"" + $Section + "`"] = {`n" + $items + "      },`n    },`n"
}
function Write-IdFile([string]$Path, [string[]]$Entries, [string[]]$Removed = @()) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllText($Path, "local diff = {`n  [`"keyDiffs`"] = {`n" + (($Entries + $Removed) -join "") + "  },`n}`nreturn diff`n", $idUtf8)
}

$idClean = Join-Path $env:JOYPRO_DATA_DIR "GameData\CleanProfile\DCS"
$idKbClean = Join-Path $env:JOYPRO_DATA_DIR "GameData\KeyboardCleanProfile\DCS"
$idCleanExisted = Test-Path $idClean
$idKbCleanExisted = Test-Path $idKbClean
$idMade = @((Join-Path $idClean "SmokeDefA.cf"), (Join-Path $idClean "SmokeDefA"), (Join-Path $idClean "SmokeDefB.cf"), (Join-Path $idClean "SmokeDefC.cf"),
    (Join-Path $idKbClean "SmokeDefA.cf"), (Join-Path $idKbClean "SmokeDefB.cf"), (Join-Path $idKbClean "SmokeDefC.cf"))
try {
    # Clean profiles: every default of DCS as a "removed" entry.
    Write-IdFile (Join-Path $idClean "SmokeDefA.cf") @((New-IdEntry "d_fire" "Fire" "removed" @("JOY_BTN1")), (New-IdEntry "d_gear" "Gear" "removed" @("JOY_BTN2")))
    Write-IdFile (Join-Path $idClean "SmokeDefA\Smoke Model Stick.cf") @((New-IdEntry "d_fire" "Fire" "removed" @("JOY_BTN1")), (New-IdEntry "d_trim" "Trim" "removed" @("JOY_BTN3")),
        (New-IdEntry "d_cms" "Countermeasures" "removed" @("JOY_BTN4")), (New-IdEntry "d_view" "View" "removed" @("JOY_BTN5")))
    Write-IdFile (Join-Path $idKbClean "SmokeDefA.cf") @((New-IdEntry "d_gear" "Gear" "removed" @("G")), (New-IdEntry "d_flaps" "Flaps" "removed" @("F") "LCtrl"),
        (New-IdEntry "d_brake" "Brake" "removed" @("F")), (New-IdEntry "d_trim" "Trim" "removed" @("T")), (New-IdEntry "d_fire" "Fire" "removed" @("Space")),
        (New-IdEntry "d_lights" "Lights" "removed" @("L")), (New-IdEntry "d_lights2" "Lights Other" "removed" @("L")), (New-IdEntry "d_own" "Own Key Default" "removed" @("K")))
    Write-IdFile (Join-Path $idClean "SmokeDefB.cf") @((New-IdEntry "d_b_fire" "Fire" "removed" @("JOY_BTN1")))
    Write-IdFile (Join-Path $idKbClean "SmokeDefB.cf") @((New-IdEntry "d_b_gear" "Gear" "removed" @("G")))
    Write-IdFile (Join-Path $idClean "SmokeDefC.cf") @((New-IdEntry "d_c_fire" "Fire" "removed" @("JOY_BTN1")))
    Write-IdFile (Join-Path $idKbClean "SmokeDefC.cf") @((New-IdEntry "d_c_gear" "Gear" "removed" @("G")))

    # The user's files (SmokeDefC has none).
    $idInput = Join-Path $idSaved "Config\Input"
    Write-IdFile (Join-Path $idInput ("SmokeDefA\joystick\" + $idStick + ".diff.lua")) @((New-IdEntry "d_fire" "Fire" "added" @("JOY_BTN10"))) @((New-IdEntry "d_view" "View" "removed" @("JOY_BTN5")))
    Write-IdFile (Join-Path $idInput ("SmokeDefA\joystick\" + $idBox + ".diff.lua")) @((New-IdEntry "d_box" "Box Thing" "added" @("JOY_BTN1")))
    Write-IdFile (Join-Path $idInput "SmokeDefA\keyboard\Keyboard.diff.lua") @((New-IdEntry "d_kb_own" "My Key" "added" @("K")))
    Write-IdFile (Join-Path $idInput "SmokeDefB\keyboard\Keyboard.diff.lua") @((New-IdEntry "d_b_x" "B Thing" "added" @("X")))

    $idProfile = Join-Path $idRoot "defaults.pr0file"
    # No connected device (else the PC's own joysticks would get defaults too).
    $idPreviousFake = $env:JOYPRO_FAKE_DEVICES
    $idFake = Join-Path $idRoot "no-devices.tsv"
    [System.IO.File]::WriteAllText($idFake, "", $idUtf8)
    $env:JOYPRO_FAKE_DEVICES = $idFake
    $idImport = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "dcs", "import", $idSaved, "--include-defaults", "--include-keyboard", "true", "--confirm", "false", "--text")
    Assert-True ($idImport.ExitCode -eq 0) ("dcs import with defaults failed: " + $idImport.Output)
    Assert-Match $idImport.Output "Default Entries Parsed:\s+9\b" "include defaults must add the model's (2), generic (3) and keyboard (4) defaults."
    Assert-Match $idImport.Output "Defaults Removed In DCS \(skipped\):\s+1\b" "a default the user's file removes must not be imported."
    Assert-Match $idImport.Output "Defaults Replaced By Own Binds \(skipped\):\s+2\b" "the defaults of an action the user bound (on that device; keyboard: any device) must not be imported."
    Assert-Match $idImport.Output "Defaults On Inputs With Own Binds \(skipped\):\s+2\b" "a default on an input the user uses must not be imported."
    Assert-Match $idImport.Output "Keyboard Defaults Of An Action A Joystick Default Has \(skipped\):\s+2\b" "a keyboard default of an action a joystick default has must not be imported."
    Assert-Match $idImport.Output "Defaults On An Input DCS Gives Two Actions \(skipped\):\s+1\b" "the second action of a shared default input must not be imported."
    Assert-Match $idImport.Output "Generic Joystick Defaults \(default.lua, imported\):\s+3\b" "DCS's generic joystick defaults are active in the game: imported."
    Assert-Match $idImport.Output "Connected Devices Without A File \(defaults imported\):\s+2\b" "an imported device gets the defaults of an imported aircraft it has no file in."
    # 4 own relations + stick Trim/Countermeasures, box Gear, keyboard Flaps/Brake/Lights of SmokeDefA, keyboard Gear of
    # SmokeDefB, Fire of SmokeDefB on stick and box.
    Assert-Match $idImport.Output "Imported Relations:\s+13\b" "include defaults must give one relation per default function and input."

    $idList = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "relations", "list")
    Assert-True ($idList.ExitCode -eq 0) ("relations list failed: " + $idList.Output)
    $idRelations = ($idList.Output | ConvertFrom-Json).Relations
    $idGear = @($idRelations | Where-Object { $_.Name -like "Gear__Keyboard__G__default__button" })
    Assert-True ($idGear.Count -eq 1) "the keyboard Gear default must be one relation."
    $idGearPlanes = @($idGear[0].Nodes | ForEach-Object { $_.Aircraft.PSObject.Properties | Where-Object { $_.Value } | ForEach-Object { $_.Name } })
    Assert-SetEquivalent $idGearPlanes @("SmokeDefB") "the keyboard Gear default of SmokeDefA is left out (the box's generic Gear default has the action), SmokeDefC has no file."
    $idFlaps = @($idRelations | Where-Object { $_.Name -like "Flaps__Keyboard__LCtrl_F__default__button" })
    Assert-True ($idFlaps.Count -eq 1 -and @($idFlaps[0].Bind.AllReformers).Count -eq 1) "a keyboard default keeps its modifier (LCtrl+F)."
    Assert-True (@($idRelations | Where-Object { $_.Name -like "Brake__Keyboard__F__default__button" }).Count -eq 1) "F and LCtrl+F are two inputs."
    Assert-True (@($idRelations | Where-Object { $_.Bind.Joystick -like "Smoke Box*" }).Count -eq 3) "the button box has the user's own bind and DCS's generic Gear (SmokeDefA) and Fire (SmokeDefB); the generic Fire on its own input is left out."
    Assert-True (@($idRelations | Where-Object { $_.Name -like "View*" -or $_.Name -like "Own_Key_Default*" -or $_.Name -like "Lights_Other*" }).Count -eq 0) "removed, own-input and shared-input defaults must not be imported."

    $idValidate = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "profile", "validate", "--text")
    Assert-True ($idValidate.ExitCode -eq 0) ("the defaults must not give check errors: " + $idValidate.Output)
    Assert-Match $idValidate.Output "Errors:\s+0\b" "the defaults must not give check errors."
    Assert-Match $idValidate.Output "Warnings:\s+1\b" "the only check warning is DCS's generic Fire of SmokeDefB on stick and box (as DCS has it)."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $idPreviousFake
    foreach ($made in $idMade) {
        Remove-Item -LiteralPath $made -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $idCleanExisted) { Remove-Item -LiteralPath $idClean -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $idKbCleanExisted) { Remove-Item -LiteralPath $idKbClean -Recurse -Force -ErrorAction SilentlyContinue }
}
