# The load correction's "Auto Add DB Items" (docs/relation-builder.md "Load correction"): after a change of the command
# lists only an aircraft that is new in the lists since the profile was corrected is switched on - never one that was
# there and not chosen (2.6.1 switched on every aircraft of a node's id the user had never bound), never a pseudo
# aircraft ("Default"); a profile without the lists' aircraft set gets it recorded and nothing switched on; what the
# pass changes is a history step of its own ("Updated to the new command lists: ..."), a read-only command saves
# nothing; and "profile auto-added" offers exactly the aircraft an earlier pass switched on (a fixture built like a
# tester's history: "THROTTLE - SLIDER -HELO POWER BOTH" 4 -> 21 aircraft) and takes them out as one undo step. "Default"
# is listed but no aircraft: marked and counted apart. A step that was an import or a builder is never offered, by its
# label (old and new, also joined "A + B"), by the checkpoint before it, or by the relations and binds it made.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineBin); needs the development DCS lists.

$aaDir = Join-Path $tempDir "autoadd"
$aaData = Join-Path $aaDir "data"
New-Item -Path $aaData -ItemType Directory -Force | Out-Null
$aaPreviousData = $env:JOYPRO_DATA_DIR
$aaPreviousHistory = $env:JOYPRO_PROFILE_HISTORY
$env:JOYPRO_DATA_DIR = $aaData
$env:JOYPRO_PROFILE_HISTORY = $null
$aaUtf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-AaJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $aaDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--out", $out) + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

# A JSON profile (no DB fingerprint: the load correction runs) with these relations: name -> @(@(id, @(planes)), ...).
function New-AaProfileJson([hashtable]$Relations, [object]$Planes, [string]$Fingerprint) {
    $rels = [ordered]@{}
    foreach ($name in ($Relations.Keys | Sort-Object)) {
        $nodes = @()
        foreach ($node in $Relations[$name]) {
            $aircraft = [ordered]@{}
            foreach ($plane in $node[1]) { $aircraft[$plane] = $true }
            foreach ($plane in @($node[2])) { if ($plane) { $aircraft[$plane] = $false } }
            $nodes += [ordered]@{ ID = $node[0]; Game = "DCS"; AIRCRAFT = $aircraft }
        }
        $rels[$name] = [ordered]@{ NAME = $name; ISAXIS = ($name -notlike "Gear*"); Groups = @(); NODES = $nodes }
    }
    $doc = [ordered]@{ Relations = $rels; Binds = [ordered]@{} }
    if ($null -ne $Planes) { $doc["DatabasePlanes"] = @($Planes) }
    if ($Fingerprint) { $doc["DatabaseFingerprint"] = $Fingerprint }
    return ($doc | ConvertTo-Json -Depth 12)
}

try {
    $aaLists = @(Get-ChildItem -Path (Join-Path $engineBin "DB\DCS") -Filter "*.html" | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) })
    Assert-True ($aaLists -contains "F-16C_50" -and $aaLists -contains "FA-18C_hornet" -and $aaLists -contains "Default") "the DCS lists lack the aircraft this part uses."
    $aaAll = @($aaLists | ForEach-Object { "DCS`t" + $_ })

    # 1. A profile from before 2.6.2 (no aircraft set): nothing is switched on, the set is recorded.
    $aaOld = Join-Path $aaDir "old.pr0file"
    [IO.File]::WriteAllText($aaOld, (New-AaProfileJson @{ "Pitch" = @(, @("a2001cdnil", @("F-16C_50"))) } $null $null), $aaUtf8)
    $aaOldReport = Join-Path $aaDir "old-report.json"
    $aaOldRun = Run-Engine @("--profile", $aaOld, "--load-corrections", "true", "--auto-add-db-items", "true", "--confirm-corrections", "false", "--correction-report", $aaOldReport,
        "relations", "aircraft", "set", "Pitch", "--node", "DCS:a2001cdnil", "--on", "F-16C_50", "--text")
    Assert-True ($aaOldRun.ExitCode -eq 0) ("a mutating command on an old profile failed: " + $aaOldRun.Output)
    $aaOldShow = Run-Engine @("--profile", $aaOld, "--load-corrections", "false", "relations", "show", "Pitch", "--text")
    Assert-Match $aaOldShow.Output "(?m)id=a2001cdnil, aircraft=F-16C_50\r?$" ("the load correction switched on aircraft the user never bound: " + $aaOldShow.Output)
    $aaOldEntries = @(([IO.File]::ReadAllText($aaOldReport) | ConvertFrom-Json).Entries)
    Assert-True (@($aaOldEntries | Where-Object { $_.Kind -eq "db-plane-set" -and $_.Action -eq "recorded" }).Count -eq 1) "the report does not say that the aircraft set was recorded."
    Assert-True (@($aaOldEntries | Where-Object { $_.Kind -eq "relation-item-auto-add" -and $_.Action -eq "added" }).Count -eq 0) "an old profile got aircraft switched on."
    $aaOldHistory = Invoke-AaJson $aaOld @("profile", "history", "list")
    Assert-True (@($aaOldHistory.Json.Versions | Where-Object { $_.Label -like "Updated to the new command lists*" }).Count -eq 0) "a correction that changed nothing made a history step."

    # 2. One aircraft new in the lists (the profile's set lacks FA-18C_hornet and Default): FA-18C_hornet is switched on,
    #    Default (no aircraft) is not, the others of the id (F-15C, A-10C, ...) were in the set and stay out.
    $aaNewPlanes = @($aaAll | Where-Object { $_ -ne "DCS`tFA-18C_hornet" -and $_ -ne "DCS`tDefault" })
    $aaNewJson = New-AaProfileJson @{ "Pitch" = @(, @("a2001cdnil", @("F-16C_50"))) } $aaNewPlanes $null
    $aaNew = Join-Path $aaDir "new.pr0file"
    [IO.File]::WriteAllText($aaNew, $aaNewJson, $aaUtf8)
    # A read-only command shows the corrected profile but saves nothing.
    $aaBytes = [IO.File]::ReadAllBytes($aaNew)
    $aaSuggest = Run-Engine @("--profile", $aaNew, "--load-corrections", "true", "--auto-add-db-items", "true", "--confirm-corrections", "false", "relations", "suggest", "Pitch", "--min", "high", "--text")
    Assert-True ($aaSuggest.ExitCode -eq 0) ("relations suggest failed: " + $aaSuggest.Output)
    Assert-True ([System.Linq.Enumerable]::SequenceEqual($aaBytes, [IO.File]::ReadAllBytes($aaNew))) "relations suggest (read-only) saved the load correction."
    $aaNewReport = Join-Path $aaDir "new-report.json"
    $aaNewRun = Run-Engine @("--profile", $aaNew, "--load-corrections", "true", "--auto-add-db-items", "true", "--confirm-corrections", "false", "--correction-report", $aaNewReport,
        "relations", "aircraft", "set", "Pitch", "--node", "DCS:a2001cdnil", "--on", "F-16C_50", "--off", "A-10C", "--text")
    Assert-True ($aaNewRun.ExitCode -eq 0) ("a mutating command with a new aircraft failed: " + $aaNewRun.Output)
    $aaNewShow = Run-Engine @("--profile", $aaNew, "--load-corrections", "false", "relations", "show", "Pitch", "--text")
    Assert-Match $aaNewShow.Output "(?m)id=a2001cdnil, aircraft=F-16C_50, FA-18C_hornet\r?$" ("the aircraft new in the lists was not switched on (or others were): " + $aaNewShow.Output)
    $aaNewEntries = @(([IO.File]::ReadAllText($aaNewReport) | ConvertFrom-Json).Entries)
    Assert-True (@($aaNewEntries | Where-Object { $_.Kind -eq "relation-item-auto-add" -and $_.Action -eq "added" -and $_.NewPlane -eq "FA-18C_hornet" }).Count -eq 1) "the report has no 'added' entry for the new aircraft."
    $aaNewHistory = Invoke-AaJson $aaNew @("profile", "history", "list")
    $aaStep = @($aaNewHistory.Json.Versions | Where-Object { $_.Label -eq "Updated to the new command lists: 1 aircraft in 1 relation" })
    Assert-True ($aaStep.Count -eq 1) ("the correction is not a history step of its own: " + (($aaNewHistory.Json.Versions | ForEach-Object { $_.Label }) -join " | "))
    Assert-True ($aaNewHistory.Json.Versions[0].Label -notlike "Updated to the new command lists*") "the command's own change was folded into the correction's step."
    # The next load matches the lists: nothing more is switched on.
    $aaAgain = Run-Engine @("--profile", $aaNew, "--load-corrections", "true", "--auto-add-db-items", "true", "--confirm-corrections", "false", "relations", "aircraft", "set", "Pitch", "--node", "DCS:a2001cdnil", "--on", "F-16C_50", "--text")
    Assert-True ($aaAgain.ExitCode -eq 0) "a second mutating command failed."
    $aaAgainShow = Run-Engine @("--profile", $aaNew, "--load-corrections", "false", "relations", "show", "Pitch", "--text")
    Assert-Match $aaAgainShow.Output "(?m)id=a2001cdnil, aircraft=F-16C_50, FA-18C_hornet\r?$" "a second load switched on more aircraft."

    # 3. The repair: a history built like the tester's (version 2: the command lists changed and the old pass switched
    #    on 17 aircraft in 'THROTTLE - SLIDER -HELO POWER BOTH' and more in two other relations, under the label of the
    #    user's click; version 3: the user switched one of them off himself).
    $aaHelo = "THROTTLE - SLIDER -HELO POWER BOTH"
    $aaHeloOwn = @("AH-64D_BLK_II_CPG", "AH-64D_BLK_II_PLT", "Ka-50 III")
    $aaHeloAdded = @("a-10a", "A-10C II", "A-10C", "AV8BNA", "Default", "F-15C", "F-4E-45MC", "F-5E-3", "FA-18C_hornet", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33")
    $aaBefore = New-AaProfileJson @{
        $aaHelo = @(@("a2004cdnil", $aaHeloOwn), @("a3044cd3", @("MosquitoFBMkVI")));
        "Pitch" = @(, @("a2001cdnil", @("F-16C_50")));
        "Roll" = @(, @("a2002cdnil", @("F-16C_50")));
        "Gear" = @(, @("d3001pnilu3001cd28vd1vpnilvu0", @("F-16C_50"))) } $null "fp-before-refresh"
    $aaAfter = New-AaProfileJson @{
        $aaHelo = @(@("a2004cdnil", ($aaHeloOwn + $aaHeloAdded)), @("a3044cd3", @("MosquitoFBMkVI")));
        "Pitch" = @(, @("a2001cdnil", @("F-16C_50", "FA-18C_hornet", "A-10C")));
        "Roll" = @(, @("a2002cdnil", @("F-16C_50", "FA-18C_hornet")));
        "Gear" = @(, @("d3001pnilu3001cd28vd1vpnilvu0", @("F-16C_50"))) } $null "fp-after-refresh"
    $aaLater = New-AaProfileJson @{
        $aaHelo = @(@("a2004cdnil", ($aaHeloOwn + $aaHeloAdded)), @("a3044cd3", @("MosquitoFBMkVI")));
        "Pitch" = @(, @("a2001cdnil", @("F-16C_50", "FA-18C_hornet", "A-10C")));
        "Roll" = @(, @("a2002cdnil", @("F-16C_50"), @("FA-18C_hornet")));
        "Gear" = @(, @("d3001pnilu3001cd28vd1vpnilvu0", @("F-16C_50"))) } $null "fp-after-refresh"

    # $Steps: @(kind, label, profile content), the last one is the profile file.
    function New-AaRepairFixture([string]$Name, [object[]]$Steps = @(@("open", "Before the history started", $aaBefore), @("change", "Keeping the action in one relation", $aaAfter), @("change", "Setting bind", $aaLater))) {
        $file = Join-Path $aaDir ($Name + ".pr0file")
        [IO.File]::WriteAllText($file, $Steps[-1][2], $aaUtf8)
        $folder = (Invoke-AaJson $file @("profile", "history", "list")).Json.Folder
        $blobs = Join-Path $folder "blobs"
        New-Item -ItemType Directory -Force -Path $blobs | Out-Null
        $versions = @()
        $id = 0
        $sha = [System.Security.Cryptography.SHA256]::Create()
        foreach ($step in $Steps) {
            $id++
            $bytes = $aaUtf8.GetBytes($step[2])
            $hash = (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) -join "").Substring(0, 32)
            $ms = New-Object System.IO.MemoryStream
            $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
            $gz.Write($bytes, 0, $bytes.Length); $gz.Dispose()
            [IO.File]::WriteAllBytes((Join-Path $blobs ($hash + ".gz")), $ms.ToArray())
            $versions += [ordered]@{ id = $id; utc = ("2026-11-0{0}T13:00:00.0000000Z" -f ($id + 1)); kind = $step[0]; label = $step[1]; summary = ""; hash = $hash; size = $bytes.Length; keep = $false; parent = ($id - 1); from = 0 }
        }
        $index = [ordered]@{ format = 1; profilePath = $file; nextId = $id + 1; cursor = $id; redo = @(); lastPruneUtc = "2026-11-03T00:00:00.0000000Z"; versions = $versions }
        [IO.File]::WriteAllText((Join-Path $folder "history.json"), ($index | ConvertTo-Json -Depth 6), $aaUtf8)
        return $file
    }

    $aaRepair = New-AaRepairFixture "repair"
    $aaFound = Invoke-AaJson $aaRepair @("profile", "auto-added")
    Assert-True ($aaFound.ExitCode -eq 0 -and $aaFound.Json.Found) ("profile auto-added found nothing: " + $aaFound.Output)
    $aaHeloOffer = @($aaFound.Json.Entries | Where-Object { $_.Relation -eq $aaHelo })
    Assert-SetEquivalent @($aaHeloOffer | ForEach-Object { $_.Plane }) $aaHeloAdded "profile auto-added does not offer exactly the 17 aircraft the old pass added to the helicopter power relation."
    Assert-SetEquivalent @($aaFound.Json.Entries | Where-Object { $_.Relation -eq "Pitch" } | ForEach-Object { $_.Plane }) @("FA-18C_hornet", "A-10C") "profile auto-added does not offer the pitch aircraft."
    Assert-True (@($aaFound.Json.Entries | Where-Object { $_.Relation -eq "Roll" }).Count -eq 0) "profile auto-added offers an aircraft the user switched off since."
    Assert-True (@($aaFound.Json.Entries | Where-Object { $_.Relation -eq "Gear" }).Count -eq 0) "profile auto-added offers an unchanged relation."
    # "Default" is in the list, marked, and not in the aircraft count.
    Assert-True ($aaFound.Json.Aircraft -eq 18 -and $aaFound.Json.NonAircraft -eq 1 -and $aaFound.Json.RelationCount -eq 2) ("profile auto-added counts wrong: " + $aaFound.Json.Aircraft + " + " + $aaFound.Json.NonAircraft + " in " + $aaFound.Json.RelationCount)
    Assert-SetEquivalent @($aaFound.Json.Entries | Where-Object { $_.NonAircraft } | ForEach-Object { $_.Plane }) @("Default") "profile auto-added does not mark exactly 'Default' as not an aircraft."
    Assert-True ($aaFound.Json.Steps[0].Aircraft -eq 18 -and $aaFound.Json.Steps[0].NonAircraft -eq 1) "the step does not count 'Default' apart."
    Assert-Match (Run-Engine @("--profile", $aaRepair, "profile", "auto-added", "--text")).Output "Default \(not an aircraft\)" "the text output does not mark 'Default'."
    Assert-True (@($aaFound.Json.Steps).Count -eq 1 -and $aaFound.Json.Steps[0].Version -eq 2) "profile auto-added does not name the step that added them."
    $aaHeloKeeps = @($aaFound.Json.Relations | Where-Object { $_.Relation -eq $aaHelo })[0].Keeps
    Assert-SetEquivalent $aaHeloKeeps ($aaHeloOwn + @("MosquitoFBMkVI")) "the preview does not say which aircraft stay."
    $aaSame = Invoke-AaJson $aaRepair @("profile", "auto-added")
    Assert-True ($aaSame.Json.Aircraft -eq 18) "profile auto-added changed something without --apply."

    $aaSkip = @($aaFound.Json.Entries | Where-Object { $_.Relation -eq "Pitch" -and $_.Plane -eq "A-10C" })[0].Key
    $aaApply = Invoke-AaJson $aaRepair @("profile", "auto-added", "--apply", "--skip", $aaSkip)
    Assert-True ($aaApply.ExitCode -eq 0 -and $aaApply.Json.Applied -and $aaApply.Json.Removed -eq 18 -and $aaApply.Json.RemovedNonAircraft -eq 1) ("profile auto-added --apply failed: " + $aaApply.Output)
    $aaHeloShow = Run-Engine @("--profile", $aaRepair, "--load-corrections", "false", "relations", "show", $aaHelo, "--text")
    Assert-Match $aaHeloShow.Output "(?m)id=a2004cdnil, aircraft=AH-64D_BLK_II_CPG, AH-64D_BLK_II_PLT, Ka-50 III\r?$" ("the helicopter power relation is not back to its own aircraft: " + $aaHeloShow.Output)
    Assert-Match $aaHeloShow.Output "(?m)id=a3044cd3, aircraft=MosquitoFBMkVI" "the repair touched the Mosquito's own action."
    $aaPitchShow = Run-Engine @("--profile", $aaRepair, "--load-corrections", "false", "relations", "show", "Pitch", "--text")
    Assert-Match $aaPitchShow.Output "(?m)id=a2001cdnil, aircraft=A-10C, F-16C_50\r?$" ("the unticked aircraft was taken out, or the other one stayed: " + $aaPitchShow.Output)
    $aaRepairHistory = Invoke-AaJson $aaRepair @("profile", "history", "list")
    Assert-True ($aaRepairHistory.Json.Versions[0].Label -eq "Took out aircraft JoyPro added: 17 aircraft in 2 relations") ("the repair is not one labelled step: " + $aaRepairHistory.Json.Versions[0].Label)
    $aaDone = Invoke-AaJson $aaRepair @("profile", "auto-added")
    Assert-True (-not $aaDone.Json.Found) "profile auto-added offers the repair again after it was applied."
    $aaUndo = Run-Engine @("--profile", $aaRepair, "profile", "history", "undo", "--text")
    Assert-Match $aaUndo.Output "^Undone: Took out aircraft JoyPro added" "one undo does not take the repair back."
    $aaUndoShow = Run-Engine @("--profile", $aaRepair, "--load-corrections", "false", "relations", "show", $aaHelo, "--text")
    Assert-Match $aaUndoShow.Output "aircraft=a-10a, A-10C, A-10C II, " "undo did not bring the aircraft back."

    $aaKeep = New-AaRepairFixture "keep"
    $aaKept = Invoke-AaJson $aaKeep @("profile", "auto-added", "--keep")
    Assert-True ($aaKept.ExitCode -eq 0 -and $aaKept.Json.Kept -and $aaKept.Json.Removed -eq 0) ("profile auto-added --keep failed: " + $aaKept.Output)
    Assert-True (-not (Invoke-AaJson $aaKeep @("profile", "auto-added")).Json.Found) "profile auto-added asks again after 'Keep them'."
    $aaKeepShow = Run-Engine @("--profile", $aaKeep, "--load-corrections", "false", "relations", "show", $aaHelo, "--text")
    Assert-Match $aaKeepShow.Output "aircraft=a-10a, A-10C, A-10C II, " "'Keep them' took aircraft out."

    # 3b. Only input layers: still listed, nothing counted as an aircraft, --apply works and says what it took out.
    $aaLayerBefore = New-AaProfileJson @{
        "F1 Cockpit view" = @(, @("d2001pnil", @("F-16C_50")));
        "F2 View" = @(, @("d2002pnil", @("F-16C_50")));
        "F3 View" = @(, @("d2003pnil", @("F-16C_50"))) } $null "fp-before-refresh"
    $aaLayerAfter = New-AaProfileJson @{
        "F1 Cockpit view" = @(, @("d2001pnil", @("F-16C_50", "Default")));
        "F2 View" = @(, @("d2002pnil", @("F-16C_50", "Default")));
        "F3 View" = @(, @("d2003pnil", @("F-16C_50", "CameraRoom"))) } $null "fp-after-refresh"
    $aaLayers = New-AaRepairFixture "layers" @(@("open", "Before the history started", $aaLayerBefore), @("change", "Deleting relation", $aaLayerAfter))
    $aaLayerFound = Invoke-AaJson $aaLayers @("profile", "auto-added")
    Assert-True ($aaLayerFound.Json.Found -and $aaLayerFound.Json.Aircraft -eq 0 -and $aaLayerFound.Json.NonAircraft -eq 3 -and $aaLayerFound.Json.RelationCount -eq 0) ("input layers alone are counted wrong: " + $aaLayerFound.Output)
    Assert-True (@($aaLayerFound.Json.Entries | Where-Object { -not $_.NonAircraft }).Count -eq 0) "an input layer is not marked."
    $aaLayerApply = Invoke-AaJson $aaLayers @("profile", "auto-added", "--apply")
    Assert-True ($aaLayerApply.Json.Removed -eq 3 -and $aaLayerApply.Json.RemovedNonAircraft -eq 3) ("--apply does not work on input layers: " + $aaLayerApply.Output)
    Assert-True ((Invoke-AaJson $aaLayers @("profile", "history", "list")).Json.Versions[0].Label -eq "Took out input layers JoyPro added: 3 in 3 relations") "the repair of input layers is not labelled as such."
    $aaLayerShow = Run-Engine @("--profile", $aaLayers, "--load-corrections", "false", "relations", "show", "F1 Cockpit view", "--text")
    Assert-Match $aaLayerShow.Output "(?m)aircraft=F-16C_50\r?$" ("--apply did not take 'Default' out: " + $aaLayerShow.Output)

    # The UI layer is no aircraft either, but DCS uses its binds in every aircraft: it counts, so the notice comes.
    $aaUiAfter = New-AaProfileJson @{
        "F1 Cockpit view" = @(, @("d2001pnil", @("F-16C_50", "Default")));
        "F2 View" = @(, @("d2002pnil", @("F-16C_50", "Default")));
        "F3 View" = @(, @("d2003pnil", @("F-16C_50", "UiLayer"))) } $null "fp-after-refresh"
    $aaUi = New-AaRepairFixture "uilayer" @(@("open", "Before the history started", $aaLayerBefore), @("change", "Deleting relation", $aaUiAfter))
    $aaUiFound = Invoke-AaJson $aaUi @("profile", "auto-added")
    Assert-True ($aaUiFound.Json.Found -and $aaUiFound.Json.Aircraft -eq 1 -and $aaUiFound.Json.NonAircraft -eq 2) ("the UI layer does not count: " + $aaUiFound.Output)

    # 3c. An import is the user's own binds: nothing from it is offered, though its fingerprint changed too. The labels are
    #     the ones real histories have ("Importing from DCS": a tester's version 57, 274 relation changes; joined labels
    #     from one click). Labels this list does not know (a translated one) are caught by the checkpoint before the
    #     import, or by the relations and binds the step made.
    foreach ($aaLabel in @("Importing from DCS", "Importing from Microsoft Flight Simulator 2024 (experimental)", "Opening the profile + Importing from DCS", "Import from DCS", "Taking over the game's changes", "Merging copied relations", "Game setup: DCS", "Manage with AI")) {
        $aaImport = New-AaRepairFixture ("import-" + [Guid]::NewGuid().ToString("N")) @(@("open", "Before the history started", $aaBefore), @("change", $aaLabel, $aaAfter))
        $aaImportFound = Invoke-AaJson $aaImport @("profile", "auto-added")
        Assert-True ($aaImportFound.ExitCode -eq 0 -and -not $aaImportFound.Json.Found) ("a step labelled '" + $aaLabel + "' is offered for removal: " + $aaImportFound.Output)
    }
    $aaCheckpoint = New-AaRepairFixture "import-checkpoint" @(@("open", "Before the history started", $aaBefore), @("checkpoint", "Before import from DCS", $aaBefore), @("change", "Aus DCS importieren", $aaAfter))
    Assert-True (-not (Invoke-AaJson $aaCheckpoint @("profile", "auto-added")).Json.Found) "a step right after 'Before import from DCS' is offered for removal."
    $aaBuilt = New-AaProfileJson @{
        $aaHelo = @(@("a2004cdnil", ($aaHeloOwn + $aaHeloAdded)), @("a3044cd3", @("MosquitoFBMkVI")));
        "Pitch" = @(, @("a2001cdnil", @("F-16C_50", "FA-18C_hornet", "A-10C")));
        "Roll" = @(, @("a2002cdnil", @("F-16C_50", "FA-18C_hornet")));
        "Gear" = @(, @("d3001pnilu3001cd28vd1vpnilvu0", @("F-16C_50")));
        "New one" = @(, @("a2005cdnil", @("F-16C_50")));
        "New two" = @(, @("a2006cdnil", @("F-16C_50")));
        "New three" = @(, @("a2007cdnil", @("F-16C_50"))) } $null "fp-after-refresh"
    $aaMade = New-AaRepairFixture "import-built" @(@("open", "Before the history started", $aaBefore), @("change", "Etwas Unbekanntes", $aaBuilt))
    Assert-True (-not (Invoke-AaJson $aaMade @("profile", "auto-added")).Json.Found) "a step that made three new relations is offered for removal."
    # The old pass under a click that names no builder is still found, also right after an export's checkpoint.
    $aaAfterExport = New-AaRepairFixture "after-export" @(@("open", "Before the history started", $aaBefore), @("checkpoint", "Before export to DCS", $aaBefore), @("change", "Deleting relation", $aaAfter))
    Assert-True ((Invoke-AaJson $aaAfterExport @("profile", "auto-added")).Json.Found) "the old pass right after an export's checkpoint is no longer found."

    # 4. The meaning check never proposes the user's own aircraft (and a helicopter's throttle is the engine power).
    $aaCheck = Invoke-AaJson $aaKeep @("--load-corrections", "false", "relations", "check-aircraft", "--relation", $aaHelo)
    Assert-True (@($aaCheck.Json.Found | Where-Object { @($_.Drop) -contains "Ka-50 III" }).Count -eq 0) ("check-aircraft proposes to drop the Ka-50 III throttle: " + $aaCheck.Output)
}
finally {
    $env:JOYPRO_DATA_DIR = $aaPreviousData
    $env:JOYPRO_PROFILE_HISTORY = $aaPreviousHistory
}
