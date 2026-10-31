# Keyboard key and modifier names (docs/testing.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir). JoyPro 1 spells keys as DirectInput does (LeftAlt, LeftShift, Oem102), DCS its own way (LAlt, LShift,
# OEM102); both are one key:
#   1. A profile with "KeyboardLeftAlt" (Keyboard, LeftAlt) and "LAlt" (Keyboard, LAlt): reading it merges them into one
#      modifier with the user's name (KeyboardLeftAlt), saved as one history step of its own; every bind keeps its
#      modifier (DCS relations with DCS's key spelling LAlt, an IL-2 relation keeps LeftAlt); a second run changes
#      nothing more.
#   2. A DCS import whose files use JoyPro 1's spellings (a keyboard key LeftShift, modifiers.lua "KeyboardLeftAlt" on
#      LeftAlt next to "LAlt" on LAlt) stores DCS's spellings (LShift, LAlt) and one modifier.
$knDir = Join-Path $tempDir "keyboard-names"
$knData = Join-Path $knDir "data"
New-Item -ItemType Directory -Force -Path $knData | Out-Null
$knUtf8 = New-Object System.Text.UTF8Encoding($false)
$knS = [string][char]0xA7
$knPrevious = @{ Data = $env:JOYPRO_DATA_DIR; History = $env:JOYPRO_PROFILE_HISTORY }
$env:JOYPRO_DATA_DIR = $knData
$env:JOYPRO_PROFILE_HISTORY = $null

function Get-KnJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $knDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--remap-missing-devices", "false", "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return ([System.IO.File]::ReadAllText($out) | ConvertFrom-Json)
}

try {
    # ---- 1. profile load
    $knStick = "KN Stick {00000000-0000-0000-0000-0000000000c1}"
    $knJp1Alt = "KeyboardLeftAlt" + $knS + "Keyboard" + $knS + "LeftAlt" + $knS + "False"
    $knDcsAlt = "LAlt" + $knS + "Keyboard" + $knS + "LAlt" + $knS + "False"
    function New-KnRelation([string]$Name, [string]$Game, [string]$Id) {
        return @{ NAME = $Name; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = $Id; Game = $Game; AIRCRAFT = @{ SmokeKeys = $true } }) }
    }
    function New-KnBind([string]$Device, [string]$Input, [string[]]$Reformers) {
        return @{ Joystick = $Device; JAxis = ""; JButton = $Input; AllReformers = @($Reformers); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
    }
    $knDoc = @{
        Relations = @{
            "KB Fire" = (New-KnRelation "KB Fire" "DCS" "kn_fire")
            "KB Gear" = (New-KnRelation "KB Gear" "DCS" "kn_gear")
            "Stick Trim" = (New-KnRelation "Stick Trim" "DCS" "kn_trim")
            "IL2 Flaps" = (New-KnRelation "IL2 Flaps" "IL2Game" "kn_il2_flaps")
        }
        Binds = @{
            "KB Fire" = (New-KnBind "Keyboard" "Space" @($knJp1Alt))
            "KB Gear" = (New-KnBind "Keyboard" "G" @($knDcsAlt))
            "Stick Trim" = (New-KnBind $knStick "JOY_BTN1" @($knDcsAlt))
            "IL2 Flaps" = (New-KnBind "Keyboard" "F" @($knJp1Alt))
        }
    }
    $knProfile = Join-Path $knDir "both-spellings.pr0file"
    [System.IO.File]::WriteAllText($knProfile, ($knDoc | ConvertTo-Json -Depth 10), $knUtf8)

    $knBinds = Get-KnJson $knProfile @("binds", "list")
    $knByName = @{}
    foreach ($knBind in @($knBinds.Binds)) { $knByName[$knBind.RelationName] = $knBind }
    Assert-True ($knByName.Count -eq 4) ("reading a profile with both spellings must keep all 4 binds, got " + $knByName.Count)
    $knMerged = "KeyboardLeftAlt" + $knS + "Keyboard" + $knS + "LAlt" + $knS + "False"
    foreach ($knName in @("KB Fire", "KB Gear", "Stick Trim")) {
        Assert-True ((@($knByName[$knName].AllReformers) -join "|") -eq $knMerged) ("'" + $knName + "' must keep its modifier as KeyboardLeftAlt on LAlt, got: " + (@($knByName[$knName].AllReformers) -join "|"))
    }
    Assert-True ((@($knByName["IL2 Flaps"].AllReformers) -join "|") -eq $knJp1Alt) ("the IL-2 relation keeps its modifier with DirectInput's spelling, got: " + (@($knByName["IL2 Flaps"].AllReformers) -join "|"))
    $knNames = @($knBinds.Binds | ForEach-Object { @($_.AllReformers) } | ForEach-Object { ($_ -split $knS)[0] } | Sort-Object -Unique)
    Assert-True ($knNames.Count -eq 1 -and $knNames[0] -eq "KeyboardLeftAlt") ("one modifier must be left, named as the user named it: " + ($knNames -join ", "))

    $knHistory = Get-KnJson $knProfile @("profile", "history", "list")
    $knMergeSteps = @($knHistory.Versions | Where-Object { [string]$_.Label -like "Merged modifiers of the same key*" })
    Assert-True ($knMergeSteps.Count -eq 1) ("the merge must be one history step of its own: " + ((@($knHistory.Versions) | ForEach-Object { $_.Label }) -join " / "))
    Assert-Match ([string]$knMergeSteps[0].Label) "KeyboardLeftAlt \+ LAlt -> KeyboardLeftAlt" "the history step names the merged modifiers."
    $knVersions = @($knHistory.Versions).Count
    $knAgain = Get-KnJson $knProfile @("relations", "list")
    $knHistory2 = Get-KnJson $knProfile @("profile", "history", "list")
    Assert-True (@($knHistory2.Versions).Count -eq $knVersions) "reading the merged profile again must not add a history step."
    Assert-True (@($knAgain.Relations).Count -eq 4) "the relations stay after the merge."

    # ---- 2. DCS import with JoyPro 1's spellings
    $knSaved = Join-Path $knDir "saved"
    $knPlane = Join-Path $knSaved "Config\Input\SmokeKeys"
    New-Item -ItemType Directory -Force -Path (Join-Path $knPlane "keyboard"), (Join-Path $knPlane "joystick") | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $knPlane "modifiers.lua"),
        "local modifiers = {`n  [`"KeyboardLeftAlt`"] = { [`"device`"] = `"Keyboard`", [`"key`"] = `"LeftAlt`", [`"switch`"] = false, },`n" +
        "  [`"LAlt`"] = { [`"device`"] = `"Keyboard`", [`"key`"] = `"LAlt`", [`"switch`"] = false, },`n}`nreturn modifiers`n", $knUtf8)
    [System.IO.File]::WriteAllText((Join-Path $knPlane "keyboard\Keyboard.diff.lua"),
        "local diff = {`n  [`"keyDiffs`"] = {`n    [`"kn_boost`"] = {`n      [`"name`"] = `"Boost`",`n      [`"added`"] = {`n        [1] = { [`"key`"] = `"LeftShift`", [`"reformers`"] = { [1] = `"KeyboardLeftAlt`", }, },`n      },`n    },`n" +
        "    [`"kn_oem`"] = {`n      [`"name`"] = `"Oem Key`",`n      [`"added`"] = {`n        [1] = { [`"key`"] = `"Oem102`", },`n      },`n    },`n  },`n}`nreturn diff`n", $knUtf8)
    [System.IO.File]::WriteAllText((Join-Path $knPlane ("joystick\" + $knStick + ".diff.lua")),
        "local diff = {`n  [`"keyDiffs`"] = {`n    [`"kn_stick`"] = {`n      [`"name`"] = `"Stick Thing`",`n      [`"added`"] = {`n        [1] = { [`"key`"] = `"JOY_BTN3`", [`"reformers`"] = { [1] = `"LAlt`", }, },`n      },`n    },`n  },`n}`nreturn diff`n", $knUtf8)
    $knImportProfile = Join-Path $knDir "import.pr0file"
    $knImport = Run-Engine @("--profile", $knImportProfile, "--remap-missing-devices", "false", "dcs", "import", $knSaved, "--include-keyboard", "true", "--confirm", "false", "--text")
    Assert-True ($knImport.ExitCode -eq 0) ("dcs import with JoyPro 1 key names failed: " + $knImport.Output)
    $knImported = Get-KnJson $knImportProfile @("binds", "list")
    $knInputs = @($knImported.Binds | Where-Object { $_.Joystick -eq "Keyboard" } | ForEach-Object { $_.JButton } | Sort-Object)
    Assert-True (($knInputs -join ",") -eq "LShift,OEM102") ("the keyboard keys must be stored with DCS's spelling (LShift, OEM102), got: " + ($knInputs -join ","))
    $knImportedMods = @($knImported.Binds | ForEach-Object { @($_.AllReformers) } | Sort-Object -Unique)
    Assert-True ($knImportedMods.Count -eq 1 -and $knImportedMods[0] -eq $knMerged) ("the import must leave one modifier KeyboardLeftAlt on LAlt, got: " + ($knImportedMods -join " | "))
}
finally {
    $env:JOYPRO_DATA_DIR = $knPrevious.Data
    $env:JOYPRO_PROFILE_HISTORY = $knPrevious.History
}
