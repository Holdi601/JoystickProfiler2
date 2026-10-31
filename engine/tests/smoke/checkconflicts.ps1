# The check's errors in detail and the profile-load cleanup (docs/relation-builder.md "The check's errors", "Dismissed
# suggestions"). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).
#   1. relations conflicts names an input bound in two relations that both have an action of the same aircraft on
#      (with the validate line it stands for), never for an aircraft unticked in one of them, and a relation with two
#      actions of one aircraft on; --keep switches the aircraft off in the other relation, --keep-both makes the check
#      stop naming it (validate then has no error), --remove-actions takes an action out.
#   2. profile load: an action with no aircraft ticked leaves its relation (one history step of its own, nothing on the
#      next load), goes onto the relation's dismissed suggestions (relations suggest lists it, Restore takes it off),
#      and a relation without a bind whose actions have aircraft ticked (the user's marker) stays as it is.
$ccDir = Join-Path $tempDir "check-conflicts"
$ccData = Join-Path $ccDir "data"
New-Item -ItemType Directory -Force -Path $ccData | Out-Null
$ccUtf8 = New-Object System.Text.UTF8Encoding($false)
$ccPrevious = @{ Data = $env:JOYPRO_DATA_DIR; History = $env:JOYPRO_PROFILE_HISTORY }
$env:JOYPRO_DATA_DIR = $ccData
$env:JOYPRO_PROFILE_HISTORY = $null

function Get-CcJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $ccDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--load-corrections", "false", "--remap-missing-devices", "false", "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return ([System.IO.File]::ReadAllText($out) | ConvertFrom-Json)
}

try {
    $ccStick = "CC Stick {00000000-0000-0000-0000-0000000000d1}"
    function New-CcBind([string]$Button) {
        return @{ Joystick = $ccStick; JAxis = ""; JButton = $Button; AllReformers = @(); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
    }
    $ccDoc = @{
        Relations = @{
            "CC Gear" = @{ NAME = "CC Gear"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "cc_gear"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true; "A-10C" = $true } }) }
            # Same button: the F-16 has an action on here too; the A-10C is unticked here (no conflict for it).
            "CC Lights" = @{ NAME = "CC Lights"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "cc_lights"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true; "A-10C" = $false } }) }
            "CC Pitch" = @{ NAME = "CC Pitch"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "cc_pitch_a"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } },
                @{ ID = "cc_pitch_b"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            # The user's marker: no bind, an aircraft ticked.
            "CC Marker" = @{ NAME = "CC Marker"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "cc_marker"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            # A dismissed suggestion of 2.4 (no aircraft ticked) next to an action that stays.
            "CC Flaps" = @{ NAME = "CC Flaps"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "cc_flaps"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } },
                @{ ID = "cc_flaps_never"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $false; "A-10C" = $false } }) }
        }
        Binds = @{
            "CC Gear" = (New-CcBind "JOY_BTN1")
            "CC Lights" = (New-CcBind "JOY_BTN1")
            "CC Pitch" = (New-CcBind "JOY_BTN2")
            "CC Flaps" = (New-CcBind "JOY_BTN3")
        }
    }
    $ccProfile = Join-Path $ccDir "conflicts.pr0file"
    [System.IO.File]::WriteAllText($ccProfile, ($ccDoc | ConvertTo-Json -Depth 10), $ccUtf8)

    # ---- 1. relations conflicts
    $ccConf = Get-CcJson $ccProfile @("relations", "conflicts")
    $ccInputs = @($ccConf.InputConflicts)
    Assert-True ($ccInputs.Count -eq 1) ("one input conflict expected (the F-16 only), got " + $ccInputs.Count + ": " + (($ccInputs | ForEach-Object { $_.Key }) -join " / "))
    Assert-True ([string]$ccInputs[0].Plane -eq "F-16C_50") ("an aircraft unticked in one relation must never cause a conflict, got " + $ccInputs[0].Plane)
    Assert-True (@($ccInputs[0].Sides).Count -eq 2) "the input conflict names both relations."
    Assert-Match ([string]$ccInputs[0].Message) "^Button Item key found in multiple Relation: DCS:F-16C_50" "the input conflict carries the validate line."
    $ccActions = @($ccConf.ActionConflicts)
    Assert-True ($ccActions.Count -eq 1 -and [string]$ccActions[0].Relation -eq "CC Pitch") "the relation with two actions of one aircraft is named."
    Assert-True (@($ccActions[0].Actions).Count -eq 2) "both actions of the relation are listed."
    $ccValidate = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "--text", "profile", "validate", "--fail-on-warnings", "false")
    Assert-Match $ccValidate.Output ([regex]::Escape([string]$ccInputs[0].Message)) "validate prints the same line as relations conflicts."

    $ccKeep = Get-CcJson $ccProfile @("relations", "conflicts", "--keep", "CC Gear", "--keys", [string]$ccInputs[0].Key)
    Assert-True (@($ccKeep.InputConflicts).Count -eq 0) "--keep left the input conflict."
    $ccLights = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "show", "CC Lights", "--text")
    Assert-NotMatch $ccLights.Output "aircraft=[^\r\n]*F-16C_50" "--keep did not switch the F-16 off in the other relation."
    $ccBoth = Get-CcJson $ccProfile @("relations", "conflicts", "--keep-both", "--keys", [string]$ccActions[0].Key)
    Assert-True (@($ccBoth.ActionConflicts).Count -eq 0 -and $ccBoth.KeptCount -eq 1) "--keep-both did not keep the relation's two actions."
    $ccValidate2 = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "--text", "profile", "validate", "--fail-on-warnings", "false")
    Assert-True ($ccValidate2.ExitCode -eq 0) ("validate still has errors after the fixes: " + $ccValidate2.Output)
    $ccKept = Get-CcJson $ccProfile @("relations", "conflicts", "--include-kept")
    Assert-True (@($ccKept.ActionConflicts | Where-Object { $_.Kept }).Count -eq 1) "--include-kept lists the kept conflict."
    $ccRemove = Get-CcJson $ccProfile @("relations", "conflicts", "--remove-actions", "--keys", "DCS|cc_pitch_b|F-16C_50")
    Assert-True ($ccRemove.Changed -eq 1) "--remove-actions took nothing out."
    $ccPitch = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "show", "CC Pitch", "--text")
    Assert-NotMatch $ccPitch.Output "cc_pitch_b" "--remove-actions left the action in its relation."

    # ---- 2. profile load
    $ccLoad = Run-Engine @("--load-corrections", "false", "--remap-missing-devices", "false", "profile", "load", $ccProfile)
    Assert-True ($ccLoad.ExitCode -eq 0) ("profile load failed: " + $ccLoad.Output)
    $ccHistory = Get-CcJson $ccProfile @("profile", "history", "list")
    $ccSteps = @($ccHistory.Versions | Where-Object { [string]$_.Label -like "Removed * with no aircraft ticked*" })
    Assert-True ($ccSteps.Count -eq 1) ("taking out the unticked actions must be one history step of its own: " + ((@($ccHistory.Versions) | ForEach-Object { $_.Label }) -join " / "))
    Assert-Match ([string]$ccSteps[0].Label) "Removed 2 actions with no aircraft ticked from 2 relations" "the history step says what was taken out (the flaps dismissed one and the lights action --keep switched off)."
    $ccFlaps = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "show", "CC Flaps", "--text")
    Assert-NotMatch $ccFlaps.Output "cc_flaps_never" "the action with no aircraft ticked is still in its relation."
    Assert-Match $ccFlaps.Output "id=cc_flaps," "an action with an aircraft ticked must stay."
    $ccMarker = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "show", "CC Marker", "--text")
    Assert-Match $ccMarker.Output "id=cc_marker, aircraft=F-16C_50" "the unbound relation with a ticked aircraft (the user's marker) must stay as it is."
    $ccSuggest = Get-CcJson $ccProfile @("relations", "suggest", "CC Flaps")
    $ccDismissed = @($ccSuggest.Dismissed | Where-Object { [string]$_.Id -eq "cc_flaps_never" })
    Assert-True ($ccDismissed.Count -eq 1 -and @($ccDismissed[0].Planes).Count -eq 2) "the taken-out action must stay a dismissed suggestion of its relation."
    $ccVersions = @($ccHistory.Versions).Count
    Run-Engine @("--load-corrections", "false", "--remap-missing-devices", "false", "profile", "load", $ccProfile) | Out-Null
    $ccHistory2 = Get-CcJson $ccProfile @("profile", "history", "list")
    Assert-True (@($ccHistory2.Versions).Count -eq $ccVersions) "opening the cleaned profile again must not add a history step."
    $ccUndo = Run-Engine @("--profile", $ccProfile, "profile", "history", "undo")
    Assert-True ($ccUndo.ExitCode -eq 0) ("undo of the cleanup failed: " + $ccUndo.Output)
    $ccFlapsBack = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "show", "CC Flaps", "--text")
    Assert-Match $ccFlapsBack.Output "cc_flaps_never" "one undo must bring the taken-out action back."
    Run-Engine @("--load-corrections", "false", "--remap-missing-devices", "false", "profile", "load", $ccProfile) | Out-Null
    $ccRestore = Run-Engine @("--profile", $ccProfile, "--load-corrections", "false", "relations", "suggest", "apply", "CC Flaps", "--remove", "DCS:cc_flaps_never:F-16C_50|A-10C", "--text")
    Assert-True ($ccRestore.ExitCode -eq 0) ("restoring a dismissed suggestion from the list failed: " + $ccRestore.Output)
    $ccSuggest2 = Get-CcJson $ccProfile @("relations", "suggest", "CC Flaps")
    Assert-True (@($ccSuggest2.Dismissed | Where-Object { [string]$_.Id -eq "cc_flaps_never" }).Count -eq 0) "Restore did not take the dismissed suggestion off the list."
}
finally {
    $env:JOYPRO_DATA_DIR = $ccPrevious.Data
    $env:JOYPRO_PROFILE_HISTORY = $ccPrevious.History
}
