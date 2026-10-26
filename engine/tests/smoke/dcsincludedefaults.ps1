# DCS import with "include defaults" (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir). Two made-up
# aircraft with clean profiles in the game-data folder (generic joystick defaults, defaults of the "Smoke Model Stick"
# model, keyboard defaults) and a synthetic Saved Games folder. Checks:
#   - the model's defaults and the keyboard defaults are added, with their modifiers (LCtrl+F and F stay apart);
#   - DCS's generic joystick defaults are not added (they would land on every device, here a button box);
#   - a default the user's file removes, an action the user bound himself (on any device), a default on an input the
#     user uses for another action, an action another device already has a default for, and the second action of an
#     input DCS gives two actions are all left out;
#   - the same default function of two aircraft is one relation; aircraft without a file of the user get nothing;
#   - the check finds no error and no action in several relations because of the defaults.
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
    $idImport = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "dcs", "import", $idSaved, "--include-defaults", "--include-keyboard", "true", "--confirm", "false", "--text")
    Assert-True ($idImport.ExitCode -eq 0) ("dcs import with defaults failed: " + $idImport.Output)
    Assert-Match $idImport.Output "Default Entries Parsed:\s+7\b" "include defaults must add the model's and the keyboard's defaults (2 + 4 + 1)."
    Assert-Match $idImport.Output "Defaults Removed In DCS \(skipped\):\s+1\b" "a default the user's file removes must not be imported."
    Assert-Match $idImport.Output "Defaults Replaced By Own Binds \(skipped\):\s+2\b" "the defaults of an action the user bound (any device) must not be imported."
    Assert-Match $idImport.Output "Defaults On Inputs With Own Binds \(skipped\):\s+1\b" "a default on an input the user uses must not be imported."
    Assert-Match $idImport.Output "Defaults Of An Action Another Device Has \(skipped\):\s+1\b" "an action gets defaults of one device only."
    Assert-Match $idImport.Output "Defaults On An Input DCS Gives Two Actions \(skipped\):\s+1\b" "the second action of a shared default input must not be imported."
    Assert-Match $idImport.Output "Generic Joystick Defaults \(skipped\):\s+2\b" "DCS's generic joystick defaults must not be imported."
    # 4 own relations + 2 model defaults + 4 keyboard defaults of SmokeDefA; SmokeDefB's Gear joins SmokeDefA's.
    Assert-Match $idImport.Output "Imported Relations:\s+10\b" "include defaults must give one relation per default function and input."

    $idList = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "relations", "list")
    Assert-True ($idList.ExitCode -eq 0) ("relations list failed: " + $idList.Output)
    $idRelations = ($idList.Output | ConvertFrom-Json).Relations
    $idGear = @($idRelations | Where-Object { $_.Name -like "Gear__Keyboard__G__default__button" })
    Assert-True ($idGear.Count -eq 1) "the keyboard Gear default must be one relation."
    $idGearPlanes = @($idGear[0].Nodes | ForEach-Object { $_.Aircraft.PSObject.Properties | Where-Object { $_.Value } | ForEach-Object { $_.Name } })
    Assert-SetEquivalent $idGearPlanes @("SmokeDefA", "SmokeDefB") "the same default function of two aircraft must be one relation, without aircraft the user has no file for."
    $idFlaps = @($idRelations | Where-Object { $_.Name -like "Flaps__Keyboard__LCtrl_F__default__button" })
    Assert-True ($idFlaps.Count -eq 1 -and @($idFlaps[0].Bind.AllReformers).Count -eq 1) "a keyboard default keeps its modifier (LCtrl+F)."
    Assert-True (@($idRelations | Where-Object { $_.Name -like "Brake__Keyboard__F__default__button" }).Count -eq 1) "F and LCtrl+F are two inputs."
    Assert-True (@($idRelations | Where-Object { $_.Bind.Joystick -like "Smoke Box*" }).Count -eq 1) "the button box keeps only the user's own bind."
    Assert-True (@($idRelations | Where-Object { $_.Name -like "View*" -or $_.Name -like "Own_Key_Default*" -or $_.Name -like "Lights_Other*" }).Count -eq 0) "removed, own-input and shared-input defaults must not be imported."

    $idValidate = Run-Engine @("--profile", $idProfile, "--remap-missing-devices", "false", "profile", "validate", "--fail-on-warnings", "true", "--text")
    Assert-True ($idValidate.ExitCode -eq 0) ("the defaults must not give check errors or actions in several relations: " + $idValidate.Output)
}
finally {
    foreach ($made in $idMade) {
        Remove-Item -LiteralPath $made -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $idCleanExisted) { Remove-Item -LiteralPath $idClean -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $idKbCleanExisted) { Remove-Item -LiteralPath $idKbClean -Recurse -Force -ErrorAction SilentlyContinue }
}
