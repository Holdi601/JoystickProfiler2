# Axis curves for several aircraft at once (docs/axis-curves.md "Several scopes in one run"): binds tuning set with
# --scope repeated stores the same values for each in one run (one history version), --clear-scope removes others in that
# run, binds tuning clear takes several scopes, invalid scopes change nothing, and binds tuning show lists per DCS
# aircraft which scope its curve comes from (the GUI's "Applies to" list).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$asDir = Join-Path $tempDir "axisscopes"
$asData = Join-Path $asDir "data"
New-Item -Path $asData -ItemType Directory -Force | Out-Null
$asPreviousData = $env:JOYPRO_DATA_DIR
$asPreviousHistory = $env:JOYPRO_PROFILE_HISTORY
$env:JOYPRO_DATA_DIR = $asData
$env:JOYPRO_PROFILE_HISTORY = $null

function Get-AsShow([string]$ProfileFile, [string]$Relation) {
    $out = Join-Path $asDir ("show-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine @("--profile", $ProfileFile, "--out", $out, "binds", "tuning", "show", $Relation)
    Assert-True ($run.ExitCode -eq 0) ("binds tuning show failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

function Get-AsVersions([string]$ProfileFile) {
    $out = Join-Path $asDir ("history-" + [Guid]::NewGuid().ToString("N") + ".json")
    Run-Engine @("--profile", $ProfileFile, "--out", $out, "profile", "history", "list") | Out-Null
    return @(([IO.File]::ReadAllText($out) | ConvertFrom-Json).Versions).Count
}

try {
    $asProfile = Join-Path $asDir "helos.pr0file"
    $asRelation = "Collective"
    $asStick = "Smoke Collective {00000000-0000-0000-0000-0000000000A5}"
    Assert-True ((Run-Engine @("profile", "new", $asProfile, "--text")).ExitCode -eq 0) "profile new failed."
    Start-Sleep -Milliseconds 2200
    $asAdd = Run-Engine @("--profile", $asProfile, "relations", "add", $asRelation, "--axis", "--node", "DCS:a2087cdnil:AH-64D_BLK_II_CPG|AH-64D_BLK_II_PLT|Ka-50_3|SA342L|SA342M", "--device", $asStick, "--input", "JOY_Z", "--text")
    Assert-True ($asAdd.ExitCode -eq 0) ("relations add failed: " + $asAdd.Output)
    Start-Sleep -Milliseconds 2200

    # Fresh: every aircraft uses the base.
    $asShow = Get-AsShow $asProfile $asRelation
    Assert-True (@($asShow.Uses).Count -eq 5) ("show should list the five aircraft: " + ($asShow.Uses | ConvertTo-Json -Compress))
    Assert-True (@($asShow.Uses | Where-Object { $_.Source -ne "base" }).Count -eq 0) "a fresh relation's aircraft should all use the base."

    # Three of five in one run: one history version, the same values each.
    $asVersions = Get-AsVersions $asProfile
    $asSet = Run-Engine @("--profile", $asProfile, "binds", "tuning", "set", $asRelation, "--scope", "plane:DCS:AH-64D_BLK_II_CPG", "--scope", "plane:DCS:AH-64D_BLK_II_PLT", "--scope", "plane:DCS:Ka-50_3", "--deadzone", "0.12", "--curvature", "0.2", "--text")
    Assert-True ($asSet.ExitCode -eq 0) ("multi-scope set failed: " + $asSet.Output)
    Assert-True (([regex]::Matches($asSet.Output, "(?m)^Set plane:DCS:")).Count -eq 3) ("set should report each scope: " + $asSet.Output)
    $asAfter = Get-AsVersions $asProfile
    Assert-True ($asAfter -eq $asVersions + 1) ("multi-scope set should be one history version (" + $asVersions + " -> " + $asAfter + ").")
    $asShow = Get-AsShow $asProfile $asRelation
    foreach ($p in @("AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50_3")) {
        Assert-True ($asShow.Scopes.("plane:DCS:" + $p) -eq "inv=0;sl=0;dz=0.12;sx=1;sy=1;cv=0.2") ("plane scope of " + $p + " is " + $asShow.Scopes.("plane:DCS:" + $p))
        Assert-True ((@($asShow.Uses | Where-Object { $_.Plane -eq $p })[0]).Source -eq ("plane:DCS:" + $p)) ("show should say " + $p + " uses its own curve.")
    }
    Assert-True ((@($asShow.Uses | Where-Object { $_.Plane -eq "SA342L" })[0]).Source -eq "base") "SA342L should still use the base."
    $asText = (Run-Engine @("--profile", $asProfile, "binds", "tuning", "show", $asRelation, "--text")).Output
    Assert-Match $asText "(?m)^  DCS / Ka-50_3: own \(plane:DCS:Ka-50_3\)" "show --text should list the aircraft that have their own curve."
    Assert-Match $asText "(?m)^  DCS / SA342M: from base" "show --text should list where the others' curve comes from."

    # One undo takes all three back.
    Start-Sleep -Milliseconds 2200
    $asUndo = Run-Engine @("--profile", $asProfile, "profile", "history", "undo", "--text")
    Assert-Match $asUndo.Output "^Undone: Axis tuning" ("undo should take the multi-scope set back: " + $asUndo.Output)
    $asShow = Get-AsShow $asProfile $asRelation
    Assert-True (@($asShow.Scopes.PSObject.Properties | Where-Object { $_.Name -like "plane:*" }).Count -eq 0) "one undo should remove all three plane curves."
    $asRedo = Run-Engine @("--profile", $asProfile, "profile", "history", "redo", "--text")
    Assert-Match $asRedo.Output "^Redone: Axis tuning" "redo of the multi-scope set failed."
    Start-Sleep -Milliseconds 2200

    # The game's curve with --clear-scope: Ka-50 follows DCS again in the same run.
    $asGame = Run-Engine @("--profile", $asProfile, "binds", "tuning", "set", $asRelation, "--scope", "game:DCS", "--clear-scope", "plane:DCS:Ka-50_3", "--deadzone", "0.07", "--text")
    Assert-True ($asGame.ExitCode -eq 0) ("set with --clear-scope failed: " + $asGame.Output)
    Assert-Match $asGame.Output "(?m)^Cleared plane:DCS:Ka-50_3\." "set should report the scope it cleared."
    $asShow = Get-AsShow $asProfile $asRelation
    Assert-True ($null -eq $asShow.Scopes."plane:DCS:Ka-50_3") "--clear-scope should remove the Ka-50's own curve."
    Assert-True ((@($asShow.Uses | Where-Object { $_.Plane -eq "Ka-50_3" })[0]).Source -eq "game:DCS") "the Ka-50 should use DCS's curve again."
    Assert-True ((@($asShow.Uses | Where-Object { $_.Plane -eq "SA342M" })[0]).Source -eq "game:DCS") "SA342M should use DCS's curve."
    Assert-True ($asShow.Scopes."plane:DCS:AH-64D_BLK_II_CPG" -eq "inv=0;sl=0;dz=0.12;sx=1;sy=1;cv=0.2") "the CPG's own curve should stay."
    Start-Sleep -Milliseconds 2200

    # Invalid input changes nothing.
    $asBad = Run-Engine @("--profile", $asProfile, "binds", "tuning", "set", $asRelation, "--scope", "plane:DCS:SA342L", "--scope", "bogus", "--deadzone", "0.3", "--text")
    Assert-True ($asBad.ExitCode -ne 0) "an invalid scope among several should fail."
    $asBad2 = Run-Engine @("--profile", $asProfile, "binds", "tuning", "set", $asRelation, "--scope", "plane:DCS:SA342L", "--clear-scope", "plane:DCS:SA342L", "--deadzone", "0.3", "--text")
    Assert-True ($asBad2.ExitCode -ne 0) "setting and clearing one scope should fail."
    $asBad3 = Run-Engine @("--profile", $asProfile, "binds", "tuning", "clear", $asRelation, "--scope", "plane:DCS:SA342L", "--scope", "base", "--text")
    Assert-True ($asBad3.ExitCode -ne 0) "clearing the base among others should fail."
    Assert-True ($null -eq (Get-AsShow $asProfile $asRelation).Scopes."plane:DCS:SA342L") "a failed multi-scope run should change nothing."

    # Clear of several in one run (one version); a scope without tuning is named, no error.
    $asBefore = Get-AsVersions $asProfile
    $asClear = Run-Engine @("--profile", $asProfile, "binds", "tuning", "clear", $asRelation, "--scope", "plane:DCS:AH-64D_BLK_II_CPG", "--scope", "plane:DCS:AH-64D_BLK_II_PLT", "--scope", "plane:DCS:SA342L", "--text")
    Assert-True ($asClear.ExitCode -eq 0) ("multi-scope clear failed: " + $asClear.Output)
    Assert-Match $asClear.Output "Cleared plane:DCS:AH-64D_BLK_II_CPG, plane:DCS:AH-64D_BLK_II_PLT\." "clear should name what it cleared."
    Assert-Match $asClear.Output "No tuning for plane:DCS:SA342L\." "clear should name a scope without tuning."
    $asAfterClear = Get-AsVersions $asProfile
    Assert-True ($asAfterClear -eq $asBefore + 1) "multi-scope clear should be one history version."
    Assert-True (@((Get-AsShow $asProfile $asRelation).Uses | Where-Object { $_.Source -ne "game:DCS" }).Count -eq 0) "after the clear every aircraft should use DCS's curve."

    # The single-scope form still works as before.
    $asOne = Run-Engine @("--profile", $asProfile, "binds", "tuning", "set", $asRelation, "--scope", "plane:DCS:SA342L", "--saturation-y", "0.9", "--text")
    Assert-Match $asOne.Output "(?m)^Set plane:DCS:SA342L: inv=0;sl=0;dz=0\.07;sx=1;sy=0\.9;cv=0\r?$" ("single --scope set changed: " + $asOne.Output)
}
finally {
    $env:JOYPRO_DATA_DIR = $asPreviousData
    $env:JOYPRO_PROFILE_HISTORY = $asPreviousHistory
}
