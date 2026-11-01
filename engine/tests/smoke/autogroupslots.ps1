# Auto Group and the author's "HOTAS slot" profile (2.5.1, docs/auto-group.md), on the real DCS catalog:
#   4. an aircraft switched off in one relation (JoyPro switches same-number aircraft off on load) may still be placed
#      in another relation; a dismissed suggestion keeps it out of that relation only.
#   6. relations that share a button: Auto Group never leaves two of them with the same aircraft on (the check's "one
#      input does two things"); the placement is skipped and listed, the user's relation stays.
#   8. the user's own substitutes count as his style even where DB\Functions\DCS.rejected.tsv rejects the command for
#      the function (the SA342's video power switch on Radar Lock); an aircraft with the user's action in a relation
#      gets no second one there.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir); needs the development game lists.

$asDir = Join-Path $tempDir "autogroupslots"
New-Item -Path $asDir -ItemType Directory -Force | Out-Null
$asUtf8 = New-Object System.Text.UTF8Encoding($false)
$asPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Index = $env:JOYPRO_ACTION_INDEX_DIR; History = $env:JOYPRO_PROFILE_HISTORY }
$env:JOYPRO_DATA_DIR = Join-Path $asDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = $null
$env:JOYPRO_PROFILE_HISTORY = $null

function Get-AsJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $asDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--load-corrections", "false", "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return ([System.IO.File]::ReadAllText($out) | ConvertFrom-Json)
}

function New-AsBind([string]$Button, [string]$Axis) {
    return @{ Joystick = "AS Stick {00000000-0000-0000-0000-0000000000d7}"; JAxis = $Axis; JButton = $Button; AllReformers = @(); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
}

function Test-AsOn($Relation, [string]$Id, [string]$Plane) {
    foreach ($node in @($Relation.Nodes)) {
        if ($node.Id -eq $Id -and $node.Aircraft.$Plane -eq $true) { return $true }
    }
    return $false
}

try {
    # ---- 4. switched off in another relation / dismissed in this one
    $asOffDoc = @{
        Relations = @{
            "Old Pitch" = @{ NAME = "Old Pitch"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "a2001cdnil"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $false; "FA-18C_hornet" = $true } }) }
        }
        Binds = @{}
        DismissedSuggestions = @("Roll`tDCS`ta2002cdnil`tF-16C_50")
    }
    $asOffProfile = Join-Path $asDir "switched-off.pr0file"
    [System.IO.File]::WriteAllText($asOffProfile, ($asOffDoc | ConvertTo-Json -Depth 10), $asUtf8)
    $asOffRun = Get-AsJson $asOffProfile @("relations", "auto-group", "--planes", "F-16C_50|FA-18C_hornet")
    $asPitch = (Get-AsJson $asOffProfile @("relations", "show", "Pitch"))
    $asPitch = if ($asPitch.Relation) { $asPitch.Relation } else { $asPitch }
    Assert-True (Test-AsOn $asPitch "a2001cdnil" "F-16C_50") ("an aircraft switched off in another relation must still be placed in its own (Kept: " + (@($asOffRun.Kept) -join " | ") + ").")
    $asRoll = (Get-AsJson $asOffProfile @("relations", "show", "Roll"))
    $asRoll = if ($asRoll.Relation) { $asRoll.Relation } else { $asRoll }
    Assert-True (-not (Test-AsOn $asRoll "a2002cdnil" "F-16C_50")) "a suggestion dismissed for 'Roll' came back into 'Roll'."
    Assert-True (Test-AsOn $asRoll "a2002cdnil" "FA-18C_hornet") "Auto Group did not build 'Roll' for the F/A-18C."
    $asOld = (Get-AsJson $asOffProfile @("relations", "show", "Old Pitch"))
    $asOld = if ($asOld.Relation) { $asOld.Relation } else { $asOld }
    Assert-True (-not (Test-AsOn $asOld "a2001cdnil" "F-16C_50")) "Auto Group switched the aircraft back on in the relation it was off in."

    # ---- 6. two relations on one input ("HOTAS slots")
    $asSlotDoc = @{
        Relations = @{
            "My Pitch Axis" = @{ NAME = "My Pitch Axis"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "a2001cdnil"; Game = "DCS"; AIRCRAFT = @{ "FA-18C_hornet" = $true; "A-10C" = $true } }) }
            "Slot Roll" = @{ NAME = "Slot Roll"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "a2002cdnil"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
        }
        Binds = @{
            "My Pitch Axis" = (New-AsBind "" "JOY_Y")
            "Slot Roll" = (New-AsBind "" "JOY_Y")
        }
    }
    $asSlotProfile = Join-Path $asDir "slots.pr0file"
    [System.IO.File]::WriteAllText($asSlotProfile, ($asSlotDoc | ConvertTo-Json -Depth 10), $asUtf8)
    $asSlotRun = Get-AsJson $asSlotProfile @("relations", "auto-group", "--planes", "F-16C_50|FA-18C_hornet|A-10C")
    Assert-True ($asSlotRun.KeptForButtonConflicts -ge 1) ("Auto Group did not report the placement it left out for the shared button (Kept: " + (@($asSlotRun.Kept) -join " | ") + ").")
    Assert-True (@($asSlotRun.Kept | Where-Object { $_ -match "F-16C_50 a2001cdnil not added to 'My Pitch Axis'.*'Slot Roll'" }).Count -eq 1) ("the skipped placement is not named with the relation that has the button: " + (@($asSlotRun.Kept) -join " | "))
    $asConflicts = Get-AsJson $asSlotProfile @("relations", "conflicts")
    Assert-True (@($asConflicts.InputConflicts).Count -eq 0) ("Auto Group left an input that does two things: " + ((@($asConflicts.InputConflicts) | ForEach-Object { $_.Key }) -join " | "))
    $asMine = (Get-AsJson $asSlotProfile @("relations", "show", "My Pitch Axis"))
    $asMine = if ($asMine.Relation) { $asMine.Relation } else { $asMine }
    Assert-True (-not (Test-AsOn $asMine "a2001cdnil" "F-16C_50")) "Auto Group put the F-16C on the button 'Slot Roll' already uses for it."

    # ---- 8. the user's own substitutes win over the rejected list
    $asStyleDoc = @{
        Relations = @{
            "Radar Lock" = @{ NAME = "Radar Lock"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "d3007pnilunilcd23vd1vpnilvunil"; Game = "DCS"; AIRCRAFT = @{ "AJS37" = $true } },
                @{ ID = "d634pnilu635cdnilvdnilvpnilvunil"; Game = "DCS"; AIRCRAFT = @{ "AV8BNA" = $true } },
                @{ ID = "d3055pnilunilcd46vd1vpnilvunil"; Game = "DCS"; AIRCRAFT = @{ "C-130J" = $true } },
                # The user's substitutes: the video power switch where the aircraft has no radar lock (SA342's is in
                # DCS.rejected.tsv as "not Radar Lock").
                @{ ID = "d3319pnilu3319cd36vd1vpnilvu0"; Game = "DCS"; AIRCRAFT = @{ "SA342" = $true } },
                @{ ID = "d3319pnilu3319cd35vd1vpnilvu0"; Game = "DCS"; AIRCRAFT = @{ "SA342 Copilot" = $true } },
                # F-16C: TDC depress on Radar Lock (rejected for radar_lock; the F-16C has its own radar lock, TMS fwd).
                @{ ID = "d3039pnilu3039cd16vd1vpnilvu0"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
        }
        Binds = @{ "Radar Lock" = (New-AsBind "JOY_BTN1" "") }
    }
    $asStyleProfile = Join-Path $asDir "style.pr0file"
    [System.IO.File]::WriteAllText($asStyleProfile, ($asStyleDoc | ConvertTo-Json -Depth 10), $asUtf8)
    $asStyleRun = Get-AsJson $asStyleProfile @("relations", "auto-group", "--planes", "F-16C_50|SA342|SA342 Copilot", "--dry-run")
    Assert-True (@($asStyleRun.LearnedRules | Where-Object { $_ -match "^Radar Lock <- Radar EO on \(" }).Count -eq 1) ("the user's own substitute (video power on Radar Lock) was not learned: " + (@($asStyleRun.LearnedRules) -join " | "))
    Assert-True (@($asStyleRun.Kept | Where-Object { $_ -match "^F-16C_50 \S+ not added to 'Radar Lock': F-16C_50 already has your action there" }).Count -ge 1) ("Auto Group would add a second radar lock for the F-16C next to the user's TDC depress: " + (@($asStyleRun.Kept) -join " | "))
}
finally {
    $env:JOYPRO_DATA_DIR = $asPrevious.Data
    $env:JOYPRO_ACTION_INDEX_DIR = $asPrevious.Index
    $env:JOYPRO_PROFILE_HISTORY = $asPrevious.History
}
