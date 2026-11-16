# Auto Group and the user's own relations, on the real game lists (docs/auto-group.md): a relation the user made gets
# only what is its function (no gunner seat's "Gun Up/Down" in the user's "Pitch": only the function catalog reads it as
# pitch), Star Citizen's catalog spelling "map___action" is the profile's "map|action" (no second node), and new
# relations get readable names ("Anti Skid Off", not "AntiSkidOff"; "AI menu down", not "ai_menu_down").
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir); needs the development game lists.

$aoDir = Join-Path $tempDir "autogroupown"
New-Item -Path $aoDir -ItemType Directory -Force | Out-Null
$aoPreviousData = $env:JOYPRO_DATA_DIR
$aoPreviousIndex = $env:JOYPRO_ACTION_INDEX_DIR
$env:JOYPRO_DATA_DIR = Join-Path $aoDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = $null

function Invoke-AoJson([string[]]$Arguments) {
    $out = Join-Path $aoDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    $aoProfile = Join-Path $aoDir "own.pr0file"
    $aoAdd = Run-Engine @("--profile", $aoProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--node", "StarCitizen:spaceship_movement|v_pitch", "--text")
    Assert-True ($aoAdd.ExitCode -eq 0) ("relations add for the own-relation auto-group check failed: " + $aoAdd.Output)
    $aoRun = Invoke-AoJson @("--profile", $aoProfile, "relations", "auto-group", "--planes", "F-16C_50|UH-1H_Gunner|CH-47F_GUN|StarCitizen|MSFS2024")
    Assert-True ($aoRun.KeptOutOfOwnRelations -ge 1) "auto-group reported no axis kept out of the user's own relation."
    $aoPitch = Invoke-AoJson @("--profile", $aoProfile, "relations", "aircraft", "Pitch")
    $aoIds = @($aoPitch.Nodes | ForEach-Object { $_.Game + ":" + $_.Id })
    Assert-True (-not ($aoIds -contains "DCS:a3043cd9") -and -not ($aoIds -contains "DCS:a3082cd19")) ("auto-group put a gunner's 'Gun Up/Down' into the user's Pitch (" + ($aoIds -join ", ") + ").")
    Assert-True ($aoIds -contains "MSFS2024:KEY_AXIS_ELEVATOR_SET") ("auto-group did not add MSFS 2024's elevator axis to the user's Pitch (" + ($aoIds -join ", ") + ").")
    Assert-True (@($aoIds | Where-Object { $_ -like "StarCitizen:*v_pitch" }).Count -eq 1) ("auto-group added Star Citizen's pitch a second time (" + ($aoIds -join ", ") + ").")

    # Readable names for new relations.
    $aoDry = Invoke-AoJson @("--profile", (Join-Path $aoDir "empty.pr0file"), "relations", "auto-group", "--preset", "all", "--dry-run")
    $aoNames = @($aoDry.Relations | ForEach-Object { $_.Name })
    Assert-True ($aoNames.Count -gt 100) ("auto-group --dry-run proposed too few relations (" + $aoNames.Count + ").")
    $aoRaw = @($aoNames | Where-Object { $_ -cmatch '^[a-z0-9]+(_[a-z0-9]+)+$' -or $_ -cmatch '^[A-Za-z]*[a-z][A-Z][A-Za-z0-9]*$' })
    Assert-True ($aoRaw.Count -eq 0) ("auto-group proposed relation names that are ids: " + (($aoRaw | Select-Object -First 8) -join ", "))

    # Head tracking (round 7): the answer lives in the profile; with yes Auto Group makes no look-around relation in the
    # head-tracking sims and the suggestions propose no look command of them (the recenter stays).
    $aoHt = Join-Path $aoDir "headtracking.pr0file"
    $aoHtSet = Run-Engine @("--profile", $aoHt, "profile", "head-tracking", "yes", "--text")
    Assert-True ($aoHtSet.ExitCode -eq 0 -and $aoHtSet.Output -match "Head Tracking: yes") ("profile head-tracking yes failed: " + $aoHtSet.Output)
    $aoHtShow = Run-Engine @("--profile", $aoHt, "profile", "head-tracking", "--text")
    Assert-Match $aoHtShow.Output "Head Tracking: yes" ("The head-tracking answer must be kept in the profile: " + $aoHtShow.Output)
    $aoHtRun = Invoke-AoJson @("--profile", $aoHt, "relations", "auto-group", "--planes", "F-16C_50|IL2Game")
    Assert-True ($aoHtRun.HeadTrackingSkipped -ge 1) "auto-group with head tracking left no look-around function out."
    $aoHtList = Run-Engine @("--profile", $aoHt, "relations", "list", "--text")
    Assert-NotMatch $aoHtList.Output "(?m)^View: look (left|right|forward|back) \(snap\)|^View: look (left / right|up / down) axis" ("auto-group made look-around relations with head tracking: " + $aoHtList.Output)
    Assert-Match $aoHtList.Output "(?m)^View Center " "auto-group must keep the view recenter with head tracking."
    Run-Engine @("--profile", $aoHt, "relations", "add", "My look left", "--button", "--node", "DCS:dnilp28u209cdnilvdnilvpnilvunil:F-16C_50", "--text") | Out-Null
    $aoHtSuggest = Run-Engine @("--profile", $aoHt, "relations", "suggest", "My look left", "--min", "low", "--text")
    Assert-NotMatch $aoHtSuggest.Output "(?m)^\s*- \[\w+\] (DCS|IL2Game|MSFS2020|MSFS2024|XPlane12|StarCitizen|Arma3):" ("suggestions proposed look commands of head-tracking sims: " + $aoHtSuggest.Output)
    Run-Engine @("--profile", $aoHt, "profile", "head-tracking", "no", "--text") | Out-Null
    $aoNoHtSuggest = Run-Engine @("--profile", $aoHt, "relations", "suggest", "My look left", "--min", "low", "--text")
    Assert-Match $aoNoHtSuggest.Output "(?m)^\s*- \[\w+\] (IL2Game|Arma2|Arma3|MSFS2020|MSFS2024):" ("without head tracking the look suggestions of the sims must come back: " + $aoNoHtSuggest.Output)
}
finally {
    $env:JOYPRO_DATA_DIR = $aoPreviousData
    $env:JOYPRO_ACTION_INDEX_DIR = $aoPreviousIndex
}
