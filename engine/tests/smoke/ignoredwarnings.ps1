# The check's ignored warnings (engine EngineIgnoredWarnings, docs/ui/check.md "Ignored warnings"). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).
#   1. relations duplicates --ignore hides "the same command on two buttons" (one undo step); --list-kept lists it; a
#      listing never writes the profile; one more relation with the command shows it again with ShownAgain and
#      NowAlsoIn; fewer relations than ignored stay hidden; ignoring again replaces the old entry.
#   2. stale entries: a fix of the check drops the entry of a command that is on one button only now (it then comes
#      back as a new warning, not as an ignored one).
#   3. relations conflicts --ignore for "one button does two things" and "one button runs two commands", shown again
#      when one more relation (action) joins, --forget-kept --keys for one, relations duplicates --forget-kept for all.
$iwDir = Join-Path $tempDir "ignored-warnings"
$iwData = Join-Path $iwDir "data"
New-Item -ItemType Directory -Force -Path $iwData | Out-Null
$iwUtf8 = New-Object System.Text.UTF8Encoding($false)
$iwPrevious = @{ Data = $env:JOYPRO_DATA_DIR; History = $env:JOYPRO_PROFILE_HISTORY }
$env:JOYPRO_DATA_DIR = $iwData
$env:JOYPRO_PROFILE_HISTORY = "on"

function Get-IwJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $iwDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--load-corrections", "false", "--remap-missing-devices", "false", "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return ([System.IO.File]::ReadAllText($out) | ConvertFrom-Json)
}

function Get-IwHash([string]$File) {
    return (Get-FileHash -Path $File -Algorithm SHA256).Hash
}

try {
    $iwStick = "IW Stick {00000000-0000-0000-0000-0000000000e1}"
    function New-IwBind([string]$Button) {
        return @{ Joystick = $iwStick; JAxis = ""; JButton = $Button; AllReformers = @(); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
    }
    $iwDoc = @{
        Relations = @{
            # The same command of the F-16 on two buttons; a third relation has it with the F-16 unticked.
            "IW Trim A" = @{ NAME = "IW Trim A"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "iw_trim"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "IW Trim B" = @{ NAME = "IW Trim B"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "iw_trim"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "IW Trim C" = @{ NAME = "IW Trim C"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "iw_other"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } },
                @{ ID = "iw_trim"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $false } }) }
            # One button, two relations with an F-16 action; a third one on it with the F-16 unticked.
            "IW Gear" = @{ NAME = "IW Gear"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "iw_gear"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "IW Lights" = @{ NAME = "IW Lights"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "iw_lights"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "IW Hook" = @{ NAME = "IW Hook"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "iw_hook"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $false } }) }
            # One relation, two F-16 actions; a third with the F-16 unticked.
            "IW Pitch" = @{ NAME = "IW Pitch"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "iw_pitch_a"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } },
                @{ ID = "iw_pitch_b"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } },
                @{ ID = "iw_pitch_c"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $false } }) }
        }
        Binds = @{
            "IW Trim A" = (New-IwBind "JOY_BTN1")
            "IW Trim B" = (New-IwBind "JOY_BTN2")
            "IW Trim C" = (New-IwBind "JOY_BTN3")
            "IW Gear" = (New-IwBind "JOY_BTN5")
            "IW Lights" = (New-IwBind "JOY_BTN5")
            "IW Hook" = (New-IwBind "JOY_BTN5")
            "IW Pitch" = (New-IwBind "JOY_BTN6")
        }
    }
    $iwProfile = Join-Path $iwDir "ignored.pr0file"
    [System.IO.File]::WriteAllText($iwProfile, ($iwDoc | ConvertTo-Json -Depth 10), $iwUtf8)
    $iwKey = "DCS|iw_trim|F-16C_50"

    # ---- 1. duplicates: ignore, list, shown again
    $iwDup = Get-IwJson $iwProfile @("relations", "duplicates")
    Assert-True ($iwDup.Count -eq 1 -and [string]$iwDup.Duplicates[0].Key -eq $iwKey) ("one command on two buttons expected: " + ($iwDup | ConvertTo-Json -Depth 4 -Compress))
    $iwIgnore = Get-IwJson $iwProfile @("relations", "duplicates", "--ignore", "--keys", $iwKey)
    Assert-True ($iwIgnore.Count -eq 0 -and $iwIgnore.KeptCount -eq 1) "--ignore did not hide the warning."
    $iwCheck = Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "--text", "profile", "validate", "--fail-on-warnings", "false")
    Assert-NotMatch $iwCheck.Output "IW Trim A" "validate still names the ignored command."
    $iwHash = Get-IwHash $iwProfile
    $iwList = Get-IwJson $iwProfile @("relations", "duplicates", "--list-kept")
    Assert-True (@($iwList.Duplicates).Count -eq 1 -and $iwList.Duplicates[0].Kept -and @($iwList.Groups).Count -eq 1 -and $iwList.Groups[0].Kept) "--list-kept does not list the ignored warning."
    Assert-True (@($iwList.Groups[0].Relations).Count -eq 2) "the ignored warning names its two relations."
    Assert-True ((Get-IwHash $iwProfile) -eq $iwHash) "a listing wrote the profile."
    $iwUndo = Run-Engine @("--profile", $iwProfile, "profile", "history", "undo")
    Assert-True ($iwUndo.ExitCode -eq 0) ("undo of --ignore failed: " + $iwUndo.Output)
    Assert-True ((Get-IwJson $iwProfile @("relations", "duplicates")).Count -eq 1) "one undo did not bring the ignored warning back."
    Get-IwJson $iwProfile @("relations", "duplicates", "--ignore", "--keys", $iwKey) | Out-Null

    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Trim C", "--node", "DCS:iw_trim", "--on", "F-16C_50") | Out-Null
    $iwHash = Get-IwHash $iwProfile
    $iwAgain = Get-IwJson $iwProfile @("relations", "duplicates")
    Assert-True ($iwAgain.Count -eq 1 -and $iwAgain.Duplicates[0].ShownAgain) "one more relation with the ignored command did not show it again."
    Assert-True ((@($iwAgain.Duplicates[0].NowAlsoIn) -join ",") -eq "IW Trim C") ("NowAlsoIn names the new relation: " + (@($iwAgain.Duplicates[0].NowAlsoIn) -join ","))
    Assert-True ($iwAgain.Groups[0].ShownAgain -and (@($iwAgain.Groups[0].NowAlsoIn) -join ",") -eq "IW Trim C") "the card's group is not marked as shown again."
    $iwAgainText = Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "duplicates", "--text")
    Assert-Match $iwAgainText.Output "\[shown again, now also in 'IW Trim C'\]" "the text does not say what joined."
    Assert-True ((Get-IwHash $iwProfile) -eq $iwHash) "listing a warning that shows again wrote the profile."
    $iwIgnore3 = Get-IwJson $iwProfile @("relations", "duplicates", "--ignore", "--keys", $iwKey)
    Assert-True ($iwIgnore3.Count -eq 0 -and $iwIgnore3.KeptCount -eq 1) "ignoring the warning again did not hide it."
    # Fewer relations than ignored: still hidden (the user saw all three).
    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Trim C", "--node", "DCS:iw_trim", "--off", "F-16C_50") | Out-Null
    $iwFewer = Get-IwJson $iwProfile @("relations", "duplicates")
    Assert-True ($iwFewer.Count -eq 0 -and $iwFewer.KeptCount -eq 1) "two of the three ignored relations showed the warning again."

    # ---- 2. stale entries go with the next fix once the command is on one button only
    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Trim B", "--node", "DCS:iw_trim", "--off", "F-16C_50") | Out-Null
    $iwConf = Get-IwJson $iwProfile @("relations", "conflicts")
    $iwInput = @($iwConf.InputConflicts)
    $iwAction = @($iwConf.ActionConflicts)
    Assert-True ($iwInput.Count -eq 1 -and $iwAction.Count -eq 1) ("one input and one action conflict expected: " + ($iwConf | ConvertTo-Json -Depth 5 -Compress))
    # A fix of the check (ignoring the button that runs two commands) drops the duplicate's stale entry.
    $iwActionIgnore = Get-IwJson $iwProfile @("relations", "conflicts", "--ignore", "--keys", [string]$iwAction[0].Key)
    Assert-True (@($iwActionIgnore.ActionConflicts).Count -eq 0 -and $iwActionIgnore.KeptCount -eq 1) "--ignore did not hide the relation's two commands."
    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Trim B", "--node", "DCS:iw_trim", "--on", "F-16C_50") | Out-Null
    $iwFresh = Get-IwJson $iwProfile @("relations", "duplicates")
    Assert-True ($iwFresh.Count -eq 1 -and $iwFresh.KeptCount -eq 0 -and -not $iwFresh.Duplicates[0].ShownAgain) "the stale entry was not dropped: the command came back on two buttons as an ignored or shown-again warning."

    # ---- 3. conflicts: ignore, shown again, forget one, forget all
    $iwInputIgnore = Get-IwJson $iwProfile @("relations", "conflicts", "--ignore", "--keys", [string]$iwInput[0].Key)
    Assert-True (@($iwInputIgnore.InputConflicts).Count -eq 0 -and $iwInputIgnore.KeptCount -eq 2) "--ignore did not hide the button that does two things."
    $iwConfList = Get-IwJson $iwProfile @("relations", "conflicts", "--list-kept")
    Assert-True (@($iwConfList.InputConflicts).Count -eq 1 -and @($iwConfList.ActionConflicts).Count -eq 1) "--list-kept does not list both ignored conflicts."
    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Hook", "--node", "DCS:iw_hook", "--on", "F-16C_50") | Out-Null
    Run-Engine @("--profile", $iwProfile, "--load-corrections", "false", "relations", "aircraft", "set", "IW Pitch", "--node", "DCS:iw_pitch_c", "--on", "F-16C_50") | Out-Null
    $iwConfAgain = Get-IwJson $iwProfile @("relations", "conflicts")
    $iwInputAgain = @($iwConfAgain.InputConflicts)
    $iwActionAgain = @($iwConfAgain.ActionConflicts)
    Assert-True ($iwInputAgain.Count -eq 1 -and $iwInputAgain[0].ShownAgain -and (@($iwInputAgain[0].NowAlsoIn) -join ",") -eq "IW Hook") "a third relation on the ignored button did not show it again with its name."
    Assert-True ($iwActionAgain.Count -eq 1 -and $iwActionAgain[0].ShownAgain -and (@($iwActionAgain[0].NowAlsoIn) -join ",") -eq "iw_pitch_c") "a third command on the ignored relation did not show it again with its id."
    Get-IwJson $iwProfile @("relations", "conflicts", "--ignore", "--keys", ([string]$iwInputAgain[0].Key + ";" + [string]$iwActionAgain[0].Key)) | Out-Null
    Get-IwJson $iwProfile @("relations", "duplicates", "--ignore", "--keys", $iwKey) | Out-Null
    $iwForgetOne = Get-IwJson $iwProfile @("relations", "conflicts", "--forget-kept", "--keys", [string]$iwInputAgain[0].Key)
    Assert-True (@($iwForgetOne.InputConflicts).Count -eq 1 -and -not $iwForgetOne.InputConflicts[0].ShownAgain -and @($iwForgetOne.ActionConflicts).Count -eq 0 -and $iwForgetOne.KeptCount -eq 1) "--forget-kept --keys did not show exactly that conflict again."
    Assert-True ((Get-IwJson $iwProfile @("relations", "duplicates")).KeptCount -eq 1) "forgetting a conflict showed the ignored command again."
    $iwForgetAll = Get-IwJson $iwProfile @("relations", "duplicates", "--forget-kept")
    Assert-True ($iwForgetAll.Count -eq 1 -and $iwForgetAll.KeptCount -eq 0) "--forget-kept did not show the ignored command again."
    $iwAll = Get-IwJson $iwProfile @("relations", "conflicts")
    Assert-True (@($iwAll.ActionConflicts).Count -eq 1 -and $iwAll.KeptCount -eq 0) "relations duplicates --forget-kept did not show the ignored conflicts again."
    $iwHistory = Get-IwJson $iwProfile @("profile", "history", "list")
    Assert-True (@($iwHistory.Versions | Where-Object { [string]$_.Label -like "*Showing ignored warnings again*" }).Count -ge 1) ("show again is a history step with its own label: " + ((@($iwHistory.Versions) | ForEach-Object { $_.Label }) -join " / "))
}
finally {
    $env:JOYPRO_DATA_DIR = $iwPrevious.Data
    $env:JOYPRO_PROFILE_HISTORY = $iwPrevious.History
}
