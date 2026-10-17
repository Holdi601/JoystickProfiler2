# Profile history and atomic saves (docs/profile-history.md): every save of a profile is a version in
# <JOYPRO_DATA_DIR>\ProfileHistory with what changed; --history-note names it; list / show / diff / undo / redo /
# restore / keep / checkpoint / prune / usage; a change made outside JoyPro is kept as a version before an undo; a
# version is kept before an export; no temp file is left next to the profile; and the cost of the history on a profile of
# the user's size (450 relations).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$phDir = Join-Path $tempDir "history"
$phData = Join-Path $phDir "data"
New-Item -Path $phData -ItemType Directory -Force | Out-Null
$phPreviousData = $env:JOYPRO_DATA_DIR
$phPreviousHistory = $env:JOYPRO_PROFILE_HISTORY
$env:JOYPRO_DATA_DIR = $phData
$env:JOYPRO_PROFILE_HISTORY = $null

function Invoke-PhJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $phDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--out", $out) + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

try {
    $phProfile = Join-Path $phDir "history.pr0file"
    $phStick = "History Stick {00000000-0000-0000-0000-0000000000H1}"
    $phNew = Run-Engine @("profile", "new", $phProfile, "--text")
    Assert-True ($phNew.ExitCode -eq 0) ("profile new failed: " + $phNew.Output)
    Start-Sleep -Milliseconds 2200
    $phAdd = Run-Engine @("--profile", $phProfile, "--history-note", "Adding relation", "relations", "add", "Throttle ALL", "--axis", "--node", "DCS:history_throttle:A-10C_2|F-16C_50|FA-18C_hornet", "--device", $phStick, "--input", "JOY_Z", "--text")
    Assert-True ($phAdd.ExitCode -eq 0) ("relations add failed: " + $phAdd.Output)
    Start-Sleep -Milliseconds 2200
    $phAdd2 = Run-Engine @("--profile", $phProfile, "relations", "add", "Gear", "--button", "--node", "DCS:history_gear:A-10C_2", "--text")
    Assert-True ($phAdd2.ExitCode -eq 0) ("second relations add failed: " + $phAdd2.Output)
    Assert-True (@(Get-ChildItem -Path $phDir -Filter "history.pr0file.tmp-*").Count -eq 0) "an atomic save left a temp file next to the profile."

    $phList = Invoke-PhJson $phProfile @("profile", "history", "list")
    Assert-True ($phList.ExitCode -eq 0) ("profile history list failed: " + $phList.Output)
    $phVersions = @($phList.Json.Versions)
    Assert-True ($phVersions.Count -eq 3) ("expected 3 versions (new, two adds), got " + $phVersions.Count)
    Assert-True ($phVersions[0].Label -eq "Add relation" -and $phVersions[0].Summary -eq "Relation 'Gear' created, 1 action added") ("the newest version is not described: " + $phVersions[0].Label + " / " + $phVersions[0].Summary)
    Assert-True ($phVersions[1].Label -eq "Adding relation") "--history-note did not name the version."
    Assert-Match $phVersions[1].Summary "Relation 'Throttle ALL' created, 3 actions added" "the relation's creation is not in the summary."
    Assert-Match $phVersions[1].Summary "'Throttle ALL' bound to History Stick JOY_Z" "the bind is not in the summary."
    Assert-True ($phVersions[0].Current -and $phList.Json.CanUndo -and -not $phList.Json.CanRedo) "the newest version should be current with undo and no redo."
    Assert-True (Test-Path (Join-Path $phData "ProfileHistory")) "the history is not in <JOYPRO_DATA_DIR>\ProfileHistory."

    $phShow = Run-Engine @("--profile", $phProfile, "profile", "history", "show", [string]$phVersions[1].Id, "--text")
    Assert-Match $phShow.Output "(?m)^  - Relation 'Throttle ALL' created, 3 actions added" "history show does not list what the version changed."
    $phDiff = Invoke-PhJson $phProfile @("profile", "history", "diff", [string]$phVersions[2].Id, "current")
    Assert-True ($phDiff.Json.Available -and @($phDiff.Json.Changes).Count -eq 3) ("history diff from the first version should list 3 changes: " + $phDiff.Output)

    # Undo / redo across engine runs (the GUI's Ctrl+Z / Ctrl+Y).
    $phUndo = Run-Engine @("--profile", $phProfile, "profile", "history", "undo", "--text")
    Assert-Match $phUndo.Output "^Undone: Add relation" "history undo did not undo the newest change."
    $phAfterUndo = Run-Engine @("--profile", $phProfile, "relations", "list", "--text")
    Assert-NotMatch $phAfterUndo.Output "(?m)^Gear \|" "after undo the relation is still there."
    Assert-Match $phAfterUndo.Output "(?m)^Throttle ALL \|" "undo went back too far."
    $phRedo = Run-Engine @("--profile", $phProfile, "profile", "history", "redo", "--text")
    Assert-Match $phRedo.Output "^Redone: Add relation" "history redo failed."
    Assert-Match (Run-Engine @("--profile", $phProfile, "relations", "list", "--text")).Output "(?m)^Gear \|" "after redo the relation is missing."

    # A change outside JoyPro (another copy written over the file) becomes a version before an undo.
    $phOutside = Join-Path $phDir "outside.pr0file"
    Run-Engine @("profile", "new", $phOutside, "--text") | Out-Null
    Copy-Item -Path $phOutside -Destination $phProfile -Force
    Run-Engine @("--profile", $phProfile, "profile", "history", "undo", "--text") | Out-Null
    $phList2 = Invoke-PhJson $phProfile @("profile", "history", "list")
    Assert-True (@($phList2.Json.Versions | Where-Object { $_.Kind -eq "external" }).Count -eq 1) "the outside change was not kept as a version."
    Assert-Match (Run-Engine @("--profile", $phProfile, "relations", "list", "--text")).Output "(?m)^Gear \|" "undo after an outside change did not return to JoyPro's last state."

    # Restore, keep, checkpoint, prune, usage.
    $phFirst = @($phList2.Json.Versions)[-1].Id
    $phRestore = Run-Engine @("--profile", $phProfile, "profile", "history", "restore", [string]$phFirst, "--text")
    Assert-Match $phRestore.Output "^Restored the version of" "history restore failed."
    Assert-Match (Run-Engine @("--profile", $phProfile, "relations", "list", "--text")).Output "(?m)^Relations: 0" "restore of the empty first version left relations."
    Run-Engine @("--profile", $phProfile, "profile", "history", "undo", "--text") | Out-Null
    Assert-Match (Run-Engine @("--profile", $phProfile, "relations", "list", "--text")).Output "(?m)^Gear \|" "undo of a restore did not bring the relations back."
    $phKeep = Run-Engine @("--profile", $phProfile, "profile", "history", "keep", [string]$phFirst, "--text")
    Assert-Match $phKeep.Output "is kept" "history keep failed."
    $phCheckpoint = Run-Engine @("--profile", $phProfile, "profile", "history", "checkpoint", "--label", "Before my experiment", "--text")
    Assert-Match $phCheckpoint.Output "^Kept as version" "history checkpoint failed."
    $phPrune = Invoke-PhJson $phProfile @("profile", "history", "prune", "--all", "--dry-run")
    Assert-True ($phPrune.ExitCode -eq 0 -and $phPrune.Json.Removed -eq 0) ("a fresh history should have nothing to prune: " + $phPrune.Output)
    $phUsage = Invoke-PhJson $phProfile @("profile", "history", "usage", "--all")
    Assert-True ($phUsage.Json.Profiles -ge 2 -and $phUsage.Json.Kept -eq 1 -and $phUsage.Json.Bytes -gt 0) ("history usage is wrong: " + $phUsage.Output)
    $phBad = Run-Engine @("--profile", $phProfile, "profile", "history", "restore", "9999", "--text")
    Assert-True ($phBad.ExitCode -ne 0) "restore of a version that does not exist must fail."

    # A version is kept before an export (needs the DCS lists of a development checkout for the export itself).
    if ($script:HasDevGameData) {
        $phSaved = Join-Path $phDir "saved-games"
        New-Item -ItemType Directory -Force -Path $phSaved | Out-Null
        $phExport = Run-Engine @("--profile", $phProfile, "dcs", "export", $phSaved, "--text")
        Assert-True ($phExport.ExitCode -eq 0) ("dcs export for the history failed: " + $phExport.Output)
        $phList3 = Invoke-PhJson $phProfile @("profile", "history", "list")
        Assert-True (@($phList3.Json.Versions | Where-Object { $_.Kind -eq "checkpoint" -and $_.Label -eq "Before export to DCS" }).Count -eq 1) "no version 'Before export to DCS' was kept."
    }

    # JOYPRO_PROFILE_HISTORY=off: the save works, nothing is recorded.
    $phOff = Join-Path $phDir "off.pr0file"
    $env:JOYPRO_PROFILE_HISTORY = "off"
    Run-Engine @("--profile", $phOff, "relations", "add", "Off", "--button", "--node", "DCS:history_off:A-10C_2", "--text") | Out-Null
    $env:JOYPRO_PROFILE_HISTORY = $null
    Assert-True (Test-Path $phOff) "a save with the history off did not write the profile."
    Assert-True (@((Invoke-PhJson $phOff @("profile", "history", "list")).Json.Versions).Count -eq 0) "the history recorded with JOYPRO_PROFILE_HISTORY=off."

    # The user's size: 450 relations. What a change costs with and without the history.
    $phRelations = New-Object System.Collections.Generic.List[object]
    $phPlanes = @("A-10C_2", "F-16C_50", "FA-18C_hornet", "AV8BNA", "F-14B", "M-2000C", "JF-17", "AH-64D_BLK_II", "Ka-50_3", "Mi-24P", "F-15ESE", "MiG-29A", "Su-27", "UH-1H", "Mi-8MT")
    for ($r = 0; $r -lt 450; $r++) {
        $nodes = New-Object System.Collections.Generic.List[object]
        for ($n = 0; $n -lt 12; $n++) {
            $aircraft = [ordered]@{}
            foreach ($plane in $phPlanes) { $aircraft[$plane] = $true }
            $nodes.Add([ordered]@{ Id = ("d" + (3000 + $r) + "pnilu" + (3000 + $n) + "cd" + $n + "vd1vpnilvu0"); Game = "DCS"; Aircraft = $aircraft })
        }
        $bind = [ordered]@{ RelationName = ("Relation " + $r); IsAxis = $false; Joystick = $phStick; JAxis = ""; JButton = ("JOY_BTN" + ($r % 32 + 1)); Inverted = $false; Slider = $false; Deadzone = 0; Curvature = @(0); SaturationX = 1; SaturationY = 1; AllReformers = @(); AdditionalImportInfo = ""; AliasJoystick = ""; DoubleTap = $false; TuningOverrides = $null }
        $phRelations.Add([ordered]@{ Name = ("Relation " + $r); IsAxis = $false; Groups = @("Group " + ($r % 20)); Nodes = $nodes; Bind = $bind })
    }
    $phJson = Join-Path $phDir "large.json"
    [IO.File]::WriteAllText($phJson, (@{ FormatVersion = "1"; Relations = $phRelations } | ConvertTo-Json -Depth 8 -Compress))
    $phLarge = Join-Path $phDir "large.pr0file"
    $phImport = Run-Engine @("--profile", $phLarge, "relations", "import", $phJson, "--text")
    Assert-True ($phImport.ExitCode -eq 0) ("relations import of 450 relations failed: " + $phImport.Output)
    $phLargeSize = (Get-Item $phLarge).Length
    $phTimes = @{}
    # With the history first: the versions follow the import's version directly.
    foreach ($mode in @("on", "off")) {
        $env:JOYPRO_PROFILE_HISTORY = if ($mode -eq "off") { "off" } else { $null }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        for ($i = 0; $i -lt 3; $i++) {
            $phChange = Run-Engine @("--profile", $phLarge, "relations", "add", ("Timing " + $mode + " " + $i), "--button", "--node", "DCS:history_timing:A-10C_2", "--text")
            Assert-True ($phChange.ExitCode -eq 0) ("relations add on the large profile failed: " + $phChange.Output)
        }
        $phTimes[$mode] = [int]($watch.ElapsedMilliseconds / 3)
    }
    $env:JOYPRO_PROFILE_HISTORY = $null
    $phLargeList = Invoke-PhJson $phLarge @("profile", "history", "list")
    $phLargeBytes = $phLargeList.Json.Bytes
    Write-Host ("Profile history on a 450-relation profile (" + [int]($phLargeSize / 1024) + " KB): change without history " + $phTimes["off"] + " ms, with history " + $phTimes["on"] + " ms; " + @($phLargeList.Json.Versions).Count + " versions use " + [int]($phLargeBytes / 1024) + " KB")
    Assert-True ($phTimes["on"] - $phTimes["off"] -lt 3000) ("the history makes a change on a large profile " + ($phTimes["on"] - $phTimes["off"]) + " ms slower.")
    Assert-Match ($phLargeList.Json.Versions[0].Summary) "created" "the large profile's version is not described."
    Assert-True (@($phLargeList.Json.Versions | Where-Object { $_.Summary -match "450 relations" }).Count -eq 1) "the import's version does not describe the 450 relations."
    Assert-True ($phLargeBytes -lt 4 * $phLargeSize) "the history of a large profile is not compressed."
}
finally {
    $env:JOYPRO_DATA_DIR = $phPreviousData
    $env:JOYPRO_PROFILE_HISTORY = $phPreviousHistory
}
