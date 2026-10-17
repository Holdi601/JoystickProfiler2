# Game config watcher (engine sync, docs/sync-watcher.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir, $engineDir, $starCitizenDbCatalogPath; the main script restores the Star Citizen DB catalog afterwards).
# Every game file is a temp copy; the fingerprints go into a temporary JoyPro folder (JOYPRO_DATA_DIR).
# Per game: export -> status unchanged -> change the file as the game would (a bind on an input two relations share,
# an axis tuning, a removed bind, and changes that are only formatting) -> diff (kinds, candidates, nothing for the
# formatting) -> apply (one to an existing relation, one into a new relation, one ignored) -> profile checks -> status
# unchanged and an empty diff.

$syncRoot = Join-Path $tempDir "sync"
New-Item -Path $syncRoot -ItemType Directory -Force | Out-Null
$syncPreviousData = $env:JOYPRO_DATA_DIR
$syncPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = Join-Path $syncRoot "data"
$syncUtf8 = New-Object System.Text.UTF8Encoding($false)

function SyncJson([string]$Text) {
    $line = @($Text -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith("{") }) | Select-Object -First 1
    Assert-True ($null -ne $line) ("no JSON in engine output: " + $Text)
    return ConvertFrom-Json $line
}

function SyncChoices([string]$Path, [object[]]$Choices) {
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Json @{ Choices = $Choices } -Depth 5), $syncUtf8)
}

try {
    # ---- DCS (diff.lua) -------------------------------------------------------------------------------------------
    $syncDcsProfile = Join-Path $syncRoot "dcs.pr0file"
    $syncDcsRoot = Join-Path $syncRoot "dcs-saved-games"
    $syncStick = "Sync Stick {11111111-2222-3333-4444-5555555555AA}"
    Run-Engine @("--profile", $syncDcsProfile, "relations", "add", "Shared One", "--button", "--node", "DCS:d3001pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $syncStick, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $syncDcsProfile, "relations", "add", "Shared Two", "--button", "--node", "DCS:d3002pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $syncStick, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $syncDcsProfile, "relations", "add", "Sync Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--device", $syncStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $syncDcsProfile, "binds", "set", "Sync Pitch", "--deadzone", "0.1", "--curvature", "0.2", "--text") | Out-Null

    $syncUntracked = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncUntracked.Games[0].State -eq "missing") ("sync status of an empty folder should say missing: " + $syncUntracked.Games[0].State)

    $syncDcsExport = Run-Engine @("--profile", $syncDcsProfile, "dcs", "export", $syncDcsRoot, "--text")
    Assert-True ($syncDcsExport.ExitCode -eq 0) ("dcs export for the sync test failed: " + $syncDcsExport.Output)
    Assert-True (Test-Path (Join-Path $env:JOYPRO_DATA_DIR "Sync\DCS.json")) "dcs export should record the fingerprint in <data>\Sync\DCS.json."
    $syncStatus = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncStatus.Games[0].State -eq "unchanged") ("sync status right after the export should be unchanged: " + ($syncStatus.Games[0] | ConvertTo-Json -Depth 4))
    Assert-True ($syncStatus.Games[0].Tracked) "sync status should know the exported config."
    Assert-Match (($syncStatus.Games[0].Watch | ForEach-Object { $_.Filters -join "," }) -join ";") "\*\.diff\.lua" "sync status should tell the watcher to watch the diff.lua files."

    $syncDiffFile = @(Get-ChildItem -Path (Join-Path $syncDcsRoot "Config\Input\F-16C_50\joystick") -Filter "*.diff.lua")[0].FullName
    # A newer write time without a change in content is no change.
    (Get-Item $syncDiffFile).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(1)
    $syncTouched = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncTouched.Games[0].State -eq "unchanged") "a file that DCS only touched must not count as changed."

    # DCS rewrites the file: a new bind on the button both Shared relations use, a dead zone, Shared One removed, and
    # a curvature written as a float (0.2 -> 0.20000000298023) plus other whitespace (formatting only).
    $syncText = [System.IO.File]::ReadAllText($syncDiffFile).Replace("`r`n", "`n")
    $syncText = $syncText.Replace('["deadzone"] = 0.1,', '["deadzone"] = 0.15,').Replace('[1] = 0.2,', '[1] = 0.20000000298023,')
    # An axis that equals a default bind of the aircraft is saved by DCS as "changed" (with the filter, without JoyPro's
    # JPK): the import must read its filter from there, not the dead zone of JoyPro's .jp copy.
    $syncText = [regex]::Replace($syncText, '(?s)(\["a2001cdnil"\] = \{.*?)\["added"\]', '$1["changed"]')
    $syncText = [regex]::Replace($syncText, '(?s)(\["a2001cdnil"\] = \{.*?)\n\s*\["JPK"\] = "[^"]*",', '$1')
    Assert-Match $syncText '(?s)\["a2001cdnil"\] = \{\s*(\["name"\][^\n]*\n\s*)?\["changed"\]' "the DCS sync fixture should hold the Pitch axis as a changed entry."
    $syncText = [regex]::Replace($syncText, '(?s)    \["d3001pnilunilcd2vd1vpnilvunil"\] = \{.*?\n    \},\n', '')
    $syncText = $syncText.Replace("  [""keyDiffs""] = {`n", "  [""keyDiffs""] = {`n    [""d3003pnilunilcd2vd1vpnilvunil""] = {`n      [""name""] = ""Game Added"",`n      [""added""] = {`n        [1] = {`n          [""key""] = ""JOY_BTN3"",`n        },`n      },`n    },`n")
    [System.IO.File]::WriteAllText($syncDiffFile, $syncText.Replace("`n", "`r`n").Replace("    ", "`t"), $syncUtf8)
    $syncChanged = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncChanged.Games[0].State -eq "changed") "sync status should see the changed diff.lua."
    Assert-True (@($syncChanged.Games[0].ChangedPlanes) -contains "F-16C_50") "sync status should name the changed aircraft."

    $syncDcsDiffRun = Run-Engine @("--profile", $syncDcsProfile, "sync", "diff", "--game", "DCS", "--path", $syncDcsRoot)
    Assert-True ($syncDcsDiffRun.ExitCode -eq 0) ("sync diff (DCS) failed: " + $syncDcsDiffRun.Output)
    $syncDcsDiff = SyncJson $syncDcsDiffRun.Output
    Assert-True (@($syncDcsDiff.Changes).Count -eq 3) ("sync diff (DCS) should report exactly 3 changes (not the formatting): " + $syncDcsDiffRun.Output)
    $syncAdded = @($syncDcsDiff.Changes | Where-Object { $_.Kind -eq "added" })[0]
    Assert-True ($syncAdded.ActionId -eq "d3003pnilunilcd2vd1vpnilvunil" -and $syncAdded.Plane -eq "F-16C_50" -and $syncAdded.Input -eq "JOY_BTN3") "sync diff (DCS) should report the new bind with plane, action and input."
    # Two relations bind the button in the profile: the new bind asks which one (or a new relation).
    Assert-True ((@($syncAdded.Candidates) -contains "Shared One") -and (@($syncAdded.Candidates) -contains "Shared Two") -and @($syncAdded.Candidates).Count -eq 2) ("a bind on an input two relations share should offer both: " + (@($syncAdded.Candidates) -join ", "))
    Assert-True ($syncAdded.NewRelationName -eq "Game Added") ("the new relation should be named after the action: " + $syncAdded.NewRelationName)
    $syncTuning = @($syncDcsDiff.Changes | Where-Object { $_.Kind -eq "changed-tuning" })[0]
    Assert-True ($null -ne $syncTuning) "sync diff (DCS) should report the dead zone change."
    Assert-Match $syncTuning.OldTuning "dz=0\.1;" "changed-tuning should carry the profile's tuning."
    Assert-Match $syncTuning.NewTuning "dz=0\.15;.*cv=0\.2$" "changed-tuning should carry the game's tuning (curvature 0.2 is unchanged, only the float format differs)."
    Assert-True (@($syncTuning.Candidates) -contains "Sync Pitch") "changed-tuning should name the relation that holds the axis."
    $syncRemoved = @($syncDcsDiff.Changes | Where-Object { $_.Kind -eq "removed" })[0]
    Assert-True ($null -ne $syncRemoved -and @($syncRemoved.Relations) -contains "Shared One") "sync diff (DCS) should report the removed bind with its relation."

    $syncDcsChoices = Join-Path $syncRoot "dcs-choices.json"
    SyncChoices $syncDcsChoices @(
        @{ Id = $syncAdded.Id; Target = "Shared Two" },
        @{ Id = $syncTuning.Id; Target = "new"; Name = "Sync Pitch Game" },
        @{ Id = $syncRemoved.Id; Target = "ignore" })
    $syncDcsApply = Run-Engine @("--profile", $syncDcsProfile, "sync", "apply", "--game", "DCS", "--path", $syncDcsRoot, "--choices", $syncDcsChoices)
    Assert-True ($syncDcsApply.ExitCode -eq 0) ("sync apply (DCS) failed: " + $syncDcsApply.Output)
    $syncDcsApplied = SyncJson $syncDcsApply.Output
    Assert-True ($syncDcsApplied.Applied -eq 2 -and $syncDcsApplied.Ignored -eq 1) ("sync apply (DCS) should apply 2 and ignore 1: " + $syncDcsApply.Output)
    Assert-Match $syncDcsApply.Output "Auto-saved profile" "sync apply should save the profile."
    $syncTwo = Run-Engine @("--profile", $syncDcsProfile, "relations", "show", "Shared Two", "--text")
    Assert-Match $syncTwo.Output "id=d3003pnilunilcd2vd1vpnilvunil, aircraft=F-16C_50" "the new bind should be in the chosen relation."
    Assert-NotMatch (Run-Engine @("--profile", $syncDcsProfile, "relations", "show", "Shared One", "--text")).Output "d3003pnilunilcd2vd1vpnilvunil" "the new bind must not go into the other candidate."
    $syncNew = Run-Engine @("--profile", $syncDcsProfile, "binds", "tuning", "show", "Sync Pitch Game", "--text")
    Assert-Match $syncNew.Output "base: inv=0;sl=0;dz=0\.15;sx=1;sy=1;cv=0\.2(\r|\n|$)" ("the new relation should have the game's tuning without float noise: " + $syncNew.Output)
    $syncOld = Run-Engine @("--profile", $syncDcsProfile, "relations", "show", "Sync Pitch", "--text")
    Assert-NotMatch $syncOld.Output "a2001cdnil" "the axis moved into the new relation must leave the old one."
    $syncOne = Run-Engine @("--profile", $syncDcsProfile, "relations", "show", "Shared One", "--text")
    Assert-Match $syncOne.Output "id=d3001pnilunilcd2vd1vpnilvunil, aircraft=F-16C_50" "an ignored removal must keep the relation as it was."
    $syncAfter = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncAfter.Games[0].State -eq "unchanged") "after sync apply the config counts as known."
    $syncAfterDiff = SyncJson (Run-Engine @("--profile", $syncDcsProfile, "sync", "diff", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True (@($syncAfterDiff.Changes).Count -eq 0) "after sync apply nothing is offered again (the ignored change neither)."
    # JoyPro's own export is no change for the watcher.
    Run-Engine @("--profile", $syncDcsProfile, "dcs", "export", $syncDcsRoot, "--text") | Out-Null
    $syncReExport = SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output
    Assert-True ($syncReExport.Games[0].State -eq "unchanged") "a JoyPro export must leave the sync state unchanged."

    # accept: the current state counts as known without importing anything.
    [System.IO.File]::AppendAllText($syncDiffFile, "`r`n-- touched by the game`r`n")
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output).Games[0].State -eq "changed") "an edited diff.lua is changed."
    $syncAccept = Run-Engine @("sync", "accept", "--game", "DCS", "--path", $syncDcsRoot)
    Assert-True ($syncAccept.ExitCode -eq 0) ("sync accept failed: " + $syncAccept.Output)
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $syncDcsRoot)).Output).Games[0].State -eq "unchanged") "sync accept should mark the config as known."

    # ---- War Thunder (registry game, machine.blk) -----------------------------------------------------------------
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\warthunder\devices.tsv"
    $syncWtDir = Join-Path $syncRoot "warthunder"
    New-Item -Path $syncWtDir -ItemType Directory -Force | Out-Null
    $syncBlk = Join-Path $syncWtDir "machine.blk"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine.blk") -Destination $syncBlk -Force
    $syncWtProfile = Join-Path $syncRoot "warthunder.pr0file"
    $syncWtStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $syncWtImport = Run-Engine @("--profile", $syncWtProfile, "warthunder", "import", $syncBlk, "--text")
    Assert-True ($syncWtImport.ExitCode -eq 0) ("warthunder import for the sync test failed: " + $syncWtImport.Output)
    Run-Engine @("--profile", $syncWtProfile, "relations", "add", "WT Shared Brake", "--button", "--node", "WarThunder:ID_AIR_BRAKE", "--device", $syncWtStick, "--input", "JOY_BTN5", "--text") | Out-Null
    Run-Engine @("--profile", $syncWtProfile, "relations", "add", "WT Shared Bombs", "--button", "--node", "WarThunder:ID_BOMBS", "--device", $syncWtStick, "--input", "JOY_BTN5", "--text") | Out-Null
    # A game-wide setting JoyPro writes (settings{} aileronsMultiplier, % in JoyPro, /100 in the file).
    Run-Engine @("--profile", $syncWtProfile, "ffb", "set", "--device", "*", "--game", "WarThunder", "--param", "aileronsMultiplier=79", "--text") | Out-Null
    $syncWtExport = Run-Engine @("--profile", $syncWtProfile, "warthunder", "export", $syncBlk, "--text")
    Assert-True ($syncWtExport.ExitCode -eq 0) ("warthunder export for the sync test failed: " + $syncWtExport.Output)
    Assert-Match ([System.IO.File]::ReadAllText($syncBlk)) "aileronsMultiplier:r=0\.79" "the War Thunder export should write the game-wide setting."
    # Exported to a custom path: sync status without --path lists it too (the GUI watches every recorded config).
    $syncWtAll = SyncJson (Run-Engine @("sync", "status", "--game", "WarThunder")).Output
    $syncWtCustom = @($syncWtAll.Games | Where-Object { $_.ConfigPath -eq [System.IO.Path]::GetFullPath($syncBlk) })[0]
    Assert-True ($null -ne $syncWtCustom -and $syncWtCustom.State -eq "unchanged" -and $syncWtCustom.Tracked) ("sync status should list the config exported to a custom path: " + ($syncWtAll.Games | ConvertTo-Json -Depth 4))
    $syncWtStatus = SyncJson (Run-Engine @("sync", "status", "--game", "WarThunder", "--path", $syncBlk)).Output
    Assert-True ($syncWtStatus.Games[0].State -eq "unchanged") ("sync status after the War Thunder export should be unchanged: " + $syncWtStatus.Games[0].State)
    Assert-True (@($syncWtStatus.Games[0].ProcessNames) -contains "aces") "sync status should name the game's process (the watcher waits for it to exit)."

    # The game: rockets on the button both WT Shared relations use, a dead zone, the gear button removed; key order
    # and number format changes are no change (War Thunder rewrites machine.blk on exit).
    $syncBlkText = [System.IO.File]::ReadAllText($syncBlk)
    $syncNl = if ($syncBlkText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $syncBlkText = [regex]::Replace($syncBlkText, "hotkeys\{\r?\n", ("hotkeys{" + $syncNl + "      ID_ROCKETS{" + $syncNl + "        joyButton:i=32" + $syncNl + "      }" + $syncNl + $syncNl), 1)
    $syncBlkText = [regex]::Replace($syncBlkText, "(elevator\{[^}]*innerDeadzone:r=)0\.05", '${1}0.15')
    $syncBlkText = [regex]::Replace($syncBlkText, "ID_GEAR\{\s*joyButton:i=50\s*\}", "ID_GEAR{}")
    $syncBlkText = [regex]::Replace($syncBlkText, "(throttle\{\s*)(axisId:i=12)(\s*)(relative:b=no)", '${1}${4}${3}${2}')
    $syncBlkText = $syncBlkText.Replace("nonlinearity:r=0" + $syncNl, "nonlinearity:r=0.0" + $syncNl)
    $syncBlkText = $syncBlkText.Replace("aileronsMultiplier:r=0.79", "aileronsMultiplier:r=0.5")
    [System.IO.File]::WriteAllText($syncBlk, $syncBlkText, $syncUtf8)
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "WarThunder", "--path", $syncBlk)).Output).Games[0].State -eq "changed") "sync status should see the changed machine.blk."
    $syncWtDiffRun = Run-Engine @("--profile", $syncWtProfile, "sync", "diff", "--game", "WarThunder", "--path", $syncBlk)
    Assert-True ($syncWtDiffRun.ExitCode -eq 0) ("sync diff (War Thunder) failed: " + $syncWtDiffRun.Output)
    $syncWtDiff = SyncJson $syncWtDiffRun.Output
    Assert-True (@($syncWtDiff.Changes).Count -eq 4) ("sync diff (War Thunder) should report exactly 4 changes (not the reordered keys or 0.0): " + $syncWtDiffRun.Output)
    $syncWtSetting = @($syncWtDiff.Changes | Where-Object { $_.Kind -eq "changed-setting" })[0]
    Assert-True ($null -ne $syncWtSetting -and $syncWtSetting.SettingKey -eq "aileronsMultiplier" -and $syncWtSetting.OldValue -eq 79 -and $syncWtSetting.NewValue -eq 50) ("sync diff (War Thunder) should report the game-wide setting 79 -> 50: " + ($syncWtSetting | ConvertTo-Json))
    $syncRockets = @($syncWtDiff.Changes | Where-Object { $_.Kind -eq "added" -and $_.ActionId -eq "ID_ROCKETS" })[0]
    Assert-True ($null -ne $syncRockets) "sync diff (War Thunder) should report the rockets bind."
    Assert-True ($syncRockets.ActionTitle -eq "Fire rocket") "the change should carry the action's title from the catalog."
    Assert-True ((@($syncRockets.Candidates) -contains "WT Shared Brake") -and (@($syncRockets.Candidates) -contains "WT Shared Bombs")) ("a bind on an input two relations share should offer both: " + (@($syncRockets.Candidates) -join ", "))
    $syncElevator = @($syncWtDiff.Changes | Where-Object { $_.Kind -eq "changed-tuning" })[0]
    Assert-True ($null -ne $syncElevator -and $syncElevator.ActionId -eq "elevator") "sync diff (War Thunder) should report the elevator dead zone."
    Assert-Match $syncElevator.NewTuning "n\.innerDeadzone=0\.15" "changed-tuning (War Thunder) should carry the game's dead zone."
    $syncGear = @($syncWtDiff.Changes | Where-Object { $_.Kind -eq "removed" })[0]
    Assert-True ($null -ne $syncGear -and $syncGear.ActionId -eq "ID_GEAR") "sync diff (War Thunder) should report the removed gear button."

    $syncWtChoices = Join-Path $syncRoot "wt-choices.json"
    SyncChoices $syncWtChoices @(
        @{ Id = $syncRockets.Id; Target = "WT Shared Brake" },
        @{ Id = $syncElevator.Id; Target = "new"; Name = "WT Elevator Game" },
        @{ Id = $syncGear.Id; Target = "ignore" },
        @{ Id = $syncWtSetting.Id; Target = "apply" })
    $syncWtApply = Run-Engine @("--profile", $syncWtProfile, "sync", "apply", "--game", "WarThunder", "--path", $syncBlk, "--choices", $syncWtChoices)
    Assert-True ($syncWtApply.ExitCode -eq 0) ("sync apply (War Thunder) failed: " + $syncWtApply.Output)
    $syncWtApplied = SyncJson $syncWtApply.Output
    Assert-True ($syncWtApplied.Applied -eq 3 -and $syncWtApplied.Ignored -eq 1) ("sync apply (War Thunder) should apply 3 and ignore 1: " + $syncWtApply.Output)
    Assert-Match (Run-Engine @("--profile", $syncWtProfile, "ffb", "show", "--device", "*", "--game", "WarThunder", "--text")).Output "aileronsMultiplier=50(;|\s|$)" "the game's setting should become the profile's War Thunder setting."
    Assert-Match (Run-Engine @("--profile", $syncWtProfile, "relations", "show", "WT Shared Brake", "--text")).Output "id=ID_ROCKETS" "the rockets bind should join WT Shared Brake."
    Assert-NotMatch (Run-Engine @("--profile", $syncWtProfile, "relations", "show", "WT Shared Bombs", "--text")).Output "id=ID_ROCKETS" "the rockets bind must not join the other candidate."
    Assert-Match (Run-Engine @("--profile", $syncWtProfile, "binds", "tuning", "show", "WT Elevator Game", "--game", "WarThunder", "--text")).Output "game:WarThunder: [^\r\n]*n\.innerDeadzone=0\.15" "the new relation should get the game's tuning as its War Thunder tuning."
    Assert-NotMatch (Run-Engine @("--profile", $syncWtProfile, "relations", "show", "War Thunder Pitch axis", "--text")).Output "id=elevator" "the elevator moved into the new relation must leave the old one."
    Assert-Match (Run-Engine @("--profile", $syncWtProfile, "relations", "show", "War Thunder Toggle Gear", "--text")).Output "id=ID_GEAR" "an ignored removal must keep the gear relation."
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "WarThunder", "--path", $syncBlk)).Output).Games[0].State -eq "unchanged") "after sync apply machine.blk counts as known."
    Assert-True (@((SyncJson (Run-Engine @("--profile", $syncWtProfile, "sync", "diff", "--game", "WarThunder", "--path", $syncBlk)).Output).Changes).Count -eq 0) "after sync apply nothing is offered again."
    # The game drops a controls param set back to its default (seen in War Thunder with Hold throttle for WEP: Yes ->
    # No removed the line): the diff reports the default, not "no change".
    Run-Engine @("--profile", $syncWtProfile, "ffb", "set", "--device", "*", "--game", "WarThunder", "--param", "holdThrottleForWEP=1", "--text") | Out-Null
    $syncWepExport = Run-Engine @("--profile", $syncWtProfile, "warthunder", "export", $syncBlk, "--text")
    Assert-True ($syncWepExport.ExitCode -eq 0) ("warthunder export with holdThrottleForWEP failed: " + $syncWepExport.Output)
    $syncWepText = [System.IO.File]::ReadAllText($syncBlk)
    Assert-Match $syncWepText "holdThrottleForWEP:b=yes" "the export should write holdThrottleForWEP."
    [System.IO.File]::WriteAllText($syncBlk, ($syncWepText -replace '(?m)^[ \t]*holdThrottleForWEP:b=yes\r?\n', ''))
    $syncWepDiff = SyncJson (Run-Engine @("--profile", $syncWtProfile, "sync", "diff", "--game", "WarThunder", "--path", $syncBlk)).Output
    $syncWepChange = @($syncWepDiff.Changes | Where-Object { $_.Kind -eq "changed-setting" -and $_.SettingKey -eq "holdThrottleForWEP" })[0]
    Assert-True ($null -ne $syncWepChange -and $syncWepChange.OldValue -eq 1 -and $syncWepChange.NewValue -eq 0) ("a dropped holdThrottleForWEP line should be reported as 1 -> 0: " + ($syncWepDiff.Changes | ConvertTo-Json -Depth 3))
    # A game the user does not have (engine games install disable) is never watched.
    Run-Engine @("games", "install", "disable", "WarThunder", "--text") | Out-Null
    try {
        $syncWtDisabled = SyncJson (Run-Engine @("sync", "status", "--game", "WarThunder")).Output
        Assert-True (@($syncWtDisabled.Games).Count -eq 1 -and $syncWtDisabled.Games[0].State -eq "disabled" -and $syncWtDisabled.Games[0].Disabled -and @($syncWtDisabled.Games[0].Watch).Count -eq 0) ("a disabled game must not be watched: " + ($syncWtDisabled.Games | ConvertTo-Json -Depth 4))
    }
    finally {
        Run-Engine @("games", "install", "enable", "WarThunder", "--text") | Out-Null
    }
    $env:JOYPRO_FAKE_DEVICES = $syncPreviousFakeDevices

    # ---- Star Citizen (actionmaps.xml) ---------------------------------------------------------------------------
    $syncScFixtures = Join-Path $engineDir "tests\smoke\fixtures\starcitizen"
    Copy-Item -Path (Join-Path $syncScFixtures "catalog.xml") -Destination $starCitizenDbCatalogPath -Force
    $syncScRoot = Join-Path $syncRoot "starcitizen"
    $syncScDir = Join-Path $syncScRoot "LIVE\USER\Client\0\Profiles\default"
    New-Item -Path $syncScDir -ItemType Directory -Force | Out-Null
    $syncScFile = Join-Path $syncScDir "actionmaps.xml"
    [System.IO.File]::WriteAllText($syncScFile, [System.IO.File]::ReadAllText((Join-Path $syncScFixtures "actionmaps.xml")), $syncUtf8)
    Copy-Item -Path (Join-Path $syncScFixtures "attributes.xml") -Destination (Join-Path $syncScDir "attributes.xml") -Force
    $syncScProfile = Join-Path $syncRoot "starcitizen.pr0file"
    $syncScImport = Run-Engine @("--profile", $syncScProfile, "starcitizen", "import", $syncScRoot, "--text")
    Assert-True ($syncScImport.ExitCode -eq 0) ("starcitizen import for the sync test failed: " + $syncScImport.Output)
    $syncScExport = Run-Engine @("--profile", $syncScProfile, "starcitizen", "export", $syncScRoot, "--prefer-local-order", "false", "--text")
    Assert-True ($syncScExport.ExitCode -eq 0) ("starcitizen export for the sync test failed: " + $syncScExport.Output)
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "StarCitizen", "--path", $syncScRoot)).Output).Games[0].State -eq "unchanged") "sync status after the Star Citizen export should be unchanged."

    # The game: yaw exponent 2 -> 3, fire on a new button, the roll rebind removed; saturation 0.9 written as
    # 0.89999998 and an empty element spelled differently (formatting only).
    $syncScText = [System.IO.File]::ReadAllText($syncScFile)
    $syncScText = $syncScText.Replace('<flight_move_yaw invert="1" exponent="2"/>', '<flight_move_yaw invert="1" exponent="3"/>')
    $syncScText = $syncScText.Replace('saturation="0.9"', 'saturation="0.89999998"').Replace('<modifiers />', '<modifiers/>')
    $syncScText = [regex]::Replace($syncScText, '<action name="v_roll">\s*<rebind input="js1_rotz"/>\s*</action>', '<action name="v_roll"/>')
    $syncScText = $syncScText.Replace('<action name="v_yaw">', '<action name="v_fire">' + "`n" + '    <rebind input="js1_button3"/>' + "`n" + '   </action>' + "`n" + '   <action name="v_yaw">')
    [System.IO.File]::WriteAllText($syncScFile, $syncScText, $syncUtf8)
    $syncScDiffRun = Run-Engine @("--profile", $syncScProfile, "sync", "diff", "--game", "StarCitizen", "--path", $syncScRoot)
    Assert-True ($syncScDiffRun.ExitCode -eq 0) ("sync diff (Star Citizen) failed: " + $syncScDiffRun.Output)
    $syncScDiff = SyncJson $syncScDiffRun.Output
    Assert-True (@($syncScDiff.Changes).Count -eq 3) ("sync diff (Star Citizen) should report exactly 3 changes (not 0.89999998): " + $syncScDiffRun.Output)
    $syncYaw = @($syncScDiff.Changes | Where-Object { $_.Kind -eq "changed-tuning" })[0]
    Assert-True ($null -ne $syncYaw -and $syncYaw.ActionId -match "v_yaw") "sync diff (Star Citizen) should report the yaw exponent."
    Assert-Match $syncYaw.NewTuning "n\.exponent=3" "changed-tuning (Star Citizen) should carry the new exponent."
    $syncFire = @($syncScDiff.Changes | Where-Object { $_.Kind -eq "added" })[0]
    Assert-True ($null -ne $syncFire -and $syncFire.ActionId -match "v_fire" -and $syncFire.Input -eq "JOY_BTN3") "sync diff (Star Citizen) should report fire on button 3."
    $syncRoll = @($syncScDiff.Changes | Where-Object { $_.Kind -eq "removed" })[0]
    Assert-True ($null -ne $syncRoll -and $syncRoll.ActionId -match "v_roll") "sync diff (Star Citizen) should report the removed roll bind."

    $syncScChoices = Join-Path $syncRoot "sc-choices.json"
    SyncChoices $syncScChoices @(
        @{ Id = $syncYaw.Id; Target = @($syncYaw.Relations)[0] },
        @{ Id = $syncFire.Id; Target = "new"; Name = "SC Fire Game" },
        @{ Id = $syncRoll.Id; Target = "ignore" })
    $syncScApply = Run-Engine @("--profile", $syncScProfile, "sync", "apply", "--game", "StarCitizen", "--path", $syncScRoot, "--choices", $syncScChoices)
    Assert-True ($syncScApply.ExitCode -eq 0) ("sync apply (Star Citizen) failed: " + $syncScApply.Output)
    $syncScApplied = SyncJson $syncScApply.Output
    Assert-True ($syncScApplied.Applied -eq 2 -and $syncScApplied.Ignored -eq 1) ("sync apply (Star Citizen) should apply 2 and ignore 1: " + $syncScApply.Output)
    Assert-Match (Run-Engine @("--profile", $syncScProfile, "binds", "tuning", "show", @($syncYaw.Relations)[0], "--game", "StarCitizen", "--text")).Output "game:StarCitizen: [^\r\n]*n\.exponent=3" "the yaw relation should get the game's exponent."
    Assert-Match (Run-Engine @("--profile", $syncScProfile, "relations", "show", "SC Fire Game", "--text")).Output "v_fire" "the new relation should hold fire."
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "StarCitizen", "--path", $syncScRoot)).Output).Games[0].State -eq "unchanged") "after sync apply actionmaps.xml counts as known."
    Assert-True (@((SyncJson (Run-Engine @("--profile", $syncScProfile, "sync", "diff", "--game", "StarCitizen", "--path", $syncScRoot)).Output).Changes).Count -eq 0) "after sync apply nothing is offered again (Star Citizen)."

    # ---- Nuclear Option (registry storage: Unity PlayerPrefs, here the integration's fake-registry .json hook) -------
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\nuclearoption\devices.tsv"
    $syncNoFile = Join-Path $syncRoot "nuclearoption-registry.json"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\nuclearoption\registry.json") -Destination $syncNoFile -Force
    $syncNoProfile = Join-Path $syncRoot "nuclearoption.pr0file"
    $syncNoImport = Run-Engine @("--profile", $syncNoProfile, "nuclearoption", "import", $syncNoFile, "--text")
    Assert-True ($syncNoImport.ExitCode -eq 0) ("nuclearoption import for the sync test failed: " + $syncNoImport.Output)
    $syncNoExport = Run-Engine @("--profile", $syncNoProfile, "nuclearoption", "export", $syncNoFile, "--text")
    Assert-True ($syncNoExport.ExitCode -eq 0) ("nuclearoption export for the sync test failed: " + $syncNoExport.Output)
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "NuclearOption", "--path", $syncNoFile)).Output).Games[0].State -eq "unchanged") "sync status after the Nuclear Option export should be unchanged."
    # The game: Fire moved from Button 1 (element 32) to Button 2 (element 33) in the vJoy map, and the values written
    # back with other JSON formatting (no change by itself).
    $syncNoJson = [System.IO.File]::ReadAllText($syncNoFile) | ConvertFrom-Json
    $syncNoFire = "<actionId>5</actionId><elementType>1</elementType><elementIdentifierId>32</elementIdentifierId>"
    $syncNoMoved = 0
    foreach ($syncNoValue in $syncNoJson.PSObject.Properties) {
        if ($syncNoValue.Name -notmatch "vJoy") { continue }
        $syncNoText = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($syncNoValue.Value)).TrimEnd([char]0)
        if (-not $syncNoText.Contains($syncNoFire)) { continue }
        $syncNoBytes = [System.Text.Encoding]::UTF8.GetBytes($syncNoText.Replace($syncNoFire, "<actionId>5</actionId><elementType>1</elementType><elementIdentifierId>33</elementIdentifierId>")) + [byte[]](0)
        $syncNoValue.Value = [Convert]::ToBase64String($syncNoBytes)
        $syncNoMoved++
    }
    Assert-True ($syncNoMoved -eq 1) "the Nuclear Option fixture should have Fire on vJoy Button 1."
    [System.IO.File]::WriteAllText($syncNoFile, ($syncNoJson | ConvertTo-Json -Depth 3), $syncUtf8)
    $syncNoDiffRun = Run-Engine @("--profile", $syncNoProfile, "sync", "diff", "--game", "NuclearOption", "--path", $syncNoFile)
    Assert-True ($syncNoDiffRun.ExitCode -eq 0) ("sync diff (Nuclear Option) failed: " + $syncNoDiffRun.Output)
    $syncNoDiff = SyncJson $syncNoDiffRun.Output
    Assert-True (@($syncNoDiff.Changes).Count -eq 2) ("sync diff (Nuclear Option) should report the moved Fire bind as one added and one removed bind: " + $syncNoDiffRun.Output)
    $syncNoAdded = @($syncNoDiff.Changes | Where-Object { $_.Kind -eq "added" -and $_.ActionId -eq "Fire" -and $_.Input -eq "JOY_BTN2" })[0]
    $syncNoRemoved = @($syncNoDiff.Changes | Where-Object { $_.Kind -eq "removed" -and $_.ActionId -eq "Fire" -and $_.Input -eq "JOY_BTN1" })[0]
    Assert-True ($null -ne $syncNoAdded -and $null -ne $syncNoRemoved) ("sync diff (Nuclear Option) should report Fire on Button 2 and no longer on Button 1: " + $syncNoDiffRun.Output)
    $syncNoFireRelation = @($syncNoRemoved.Relations)[0]
    Assert-True (@($syncNoAdded.Candidates) -contains $syncNoFireRelation) "the relation that holds Fire should be offered for the new button."
    $syncNoChoices = Join-Path $syncRoot "no-choices.json"
    SyncChoices $syncNoChoices @(@{ Id = $syncNoAdded.Id; Target = $syncNoFireRelation }, @{ Id = $syncNoRemoved.Id; Target = "ignore" })
    $syncNoApply = Run-Engine @("--profile", $syncNoProfile, "sync", "apply", "--game", "NuclearOption", "--path", $syncNoFile, "--choices", $syncNoChoices)
    Assert-True ($syncNoApply.ExitCode -eq 0) ("sync apply (Nuclear Option) failed: " + $syncNoApply.Output)
    Assert-Match (Run-Engine @("--profile", $syncNoProfile, "relations", "show", $syncNoFireRelation, "--text")).Output "input=JOY_BTN2" "the Fire relation should be bound to Button 2 now."
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "NuclearOption", "--path", $syncNoFile)).Output).Games[0].State -eq "unchanged") "after sync apply the Nuclear Option values count as known."
    Assert-True (@((SyncJson (Run-Engine @("--profile", $syncNoProfile, "sync", "diff", "--game", "NuclearOption", "--path", $syncNoFile)).Output).Changes).Count -eq 0) "after sync apply nothing is offered again (Nuclear Option)."
    $env:JOYPRO_FAKE_DEVICES = $syncPreviousFakeDevices

    # ---- IL-2 Great Battles (data\input: global.actions, devices.txt, current.devices) ---------------------------
    $syncIl2Root = Join-Path $syncRoot "il2"
    $syncIl2Input = Join-Path $syncIl2Root "data\input"
    New-Item -Path $syncIl2Input -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $syncIl2Input "devices.txt"), "configId,guid,model|`r`n0,%2211111111-2222-3333-0000545345440280%22,Smoke%20Stick|`r`n")
    $syncIl2Actions = Join-Path $syncIl2Input "global.actions"
    [System.IO.File]::WriteAllText($syncIl2Actions, "// Input map preset.`r`n&actions=action,command,invert|`r`ngear, joy0_b0, 0|`r`n")
    $syncIl2Devices = Join-Path $syncIl2Input "current.devices"
    [System.IO.File]::WriteAllText($syncIl2Devices, "ffEnabled=1&ffShaking=1&mouseAcceleration=0&ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0")
    $syncIl2Profile = Join-Path $syncRoot "il2.pr0file"
    $syncIl2Import = Run-Engine @("--profile", $syncIl2Profile, "il2", "import", $syncIl2Root, "--confirm", "false", "--text")
    Assert-True ($syncIl2Import.ExitCode -eq 0) ("il2 import for the sync test failed: " + $syncIl2Import.Output)
    Run-Engine @("--profile", $syncIl2Profile, "ffb", "set", "--device", "*", "--game", "IL2Game", "--param", "force=0.7", "--text") | Out-Null
    $syncIl2Export = Run-Engine @("--profile", $syncIl2Profile, "il2", "export", $syncIl2Root, "--mode", "merge-overwrite", "--text")
    Assert-True ($syncIl2Export.ExitCode -eq 0) ("il2 export for the sync test failed: " + $syncIl2Export.Output)
    Assert-Match ([System.IO.File]::ReadAllText($syncIl2Devices)) "ffForce=0\.7" "the IL-2 export should write the force feedback power."
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "IL2Game", "--path", $syncIl2Root)).Output).Games[0].State -eq "unchanged") "sync status after the IL-2 export should be unchanged."
    # The game: flaps down on button 2, the feedback power lowered in Settings > Input devices, the gear line re-spaced.
    $syncIl2Text = [System.IO.File]::ReadAllText($syncIl2Actions)
    $syncIl2Text = [regex]::Replace($syncIl2Text, "(?m)^gear,\s*joy0_b0,\s*0\|", "gear,      joy0_b0,      0|")
    [System.IO.File]::WriteAllText($syncIl2Actions, $syncIl2Text.TrimEnd() + "`r`nflaps_dn, joy0_b1, 0|`r`n")
    [System.IO.File]::WriteAllText($syncIl2Devices, ([System.IO.File]::ReadAllText($syncIl2Devices)).Replace("ffForce=0.7", "ffForce=0.5"))
    $syncIl2DiffRun = Run-Engine @("--profile", $syncIl2Profile, "sync", "diff", "--game", "IL2Game", "--path", $syncIl2Root)
    Assert-True ($syncIl2DiffRun.ExitCode -eq 0) ("sync diff (IL-2) failed: " + $syncIl2DiffRun.Output)
    $syncIl2Diff = SyncJson $syncIl2DiffRun.Output
    Assert-True (@($syncIl2Diff.Changes).Count -eq 2) ("sync diff (IL-2) should report the new bind and the setting (not the re-spaced line): " + $syncIl2DiffRun.Output)
    $syncIl2Flaps = @($syncIl2Diff.Changes | Where-Object { $_.Kind -eq "added" -and $_.ActionId -eq "flaps_dn" -and $_.Input -eq "JOY_BTN2" })[0]
    $syncIl2Force = @($syncIl2Diff.Changes | Where-Object { $_.Kind -eq "changed-setting" -and $_.SettingKey -eq "force" })[0]
    Assert-True ($null -ne $syncIl2Flaps) ("sync diff (IL-2) should report flaps down on button 2: " + $syncIl2DiffRun.Output)
    Assert-True ($null -ne $syncIl2Force -and $syncIl2Force.OldValue -eq 0.7 -and $syncIl2Force.NewValue -eq 0.5) ("sync diff (IL-2) should report the feedback power 0.7 -> 0.5: " + $syncIl2DiffRun.Output)
    $syncIl2Choices = Join-Path $syncRoot "il2-choices.json"
    SyncChoices $syncIl2Choices @(@{ Id = $syncIl2Flaps.Id; Target = "new"; Name = "IL2 Flaps Game" }, @{ Id = $syncIl2Force.Id; Target = "apply" })
    $syncIl2Apply = Run-Engine @("--profile", $syncIl2Profile, "sync", "apply", "--game", "IL2Game", "--path", $syncIl2Root, "--choices", $syncIl2Choices)
    Assert-True ($syncIl2Apply.ExitCode -eq 0) ("sync apply (IL-2) failed: " + $syncIl2Apply.Output)
    Assert-Match (Run-Engine @("--profile", $syncIl2Profile, "relations", "show", "IL2 Flaps Game", "--text")).Output "id=flaps_dn" "the new relation should hold flaps down."
    Assert-Match (Run-Engine @("--profile", $syncIl2Profile, "ffb", "show", "--device", "*", "--game", "IL2Game", "--text")).Output "force=0\.5(;|\s|$)" "the game's feedback power should become the profile's IL-2 setting."
    Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", "IL2Game", "--path", $syncIl2Root)).Output).Games[0].State -eq "unchanged") "after sync apply the IL-2 files count as known."
    Assert-True (@((SyncJson (Run-Engine @("--profile", $syncIl2Profile, "sync", "diff", "--game", "IL2Game", "--path", $syncIl2Root)).Output).Changes).Count -eq 0) "after sync apply nothing is offered again (IL-2)."

    # ---- Game-wide settings of Arma 3, WARDOGS, X4, Arma Reforger and VTOL VR native (IGameSettingsReader) ------------
    # Per game: engine ffb set --device * -> settings-only export -> status unchanged -> the value changed in the file as
    # the game's menu writes it (X4 config.xml, Reforger's game settings and VTOL VR's gameSettings.cfg are not the
    # config path: the watcher has to see them) -> status changed, diff reports changed-setting old -> new -> apply ->
    # the profile holds the game's value, status unchanged and an empty diff.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
    function SyncSettingRoundTrip([string]$Label, [string]$SyncGame, [string]$FfbGame, [string]$SettingsProfile, [string[]]$Export, [string]$Config,
                                  [string]$File, [string]$Key, [string]$Old, [string]$New, [string]$Written, [string]$GameWrites) {
        $set = Run-Engine @("--profile", $SettingsProfile, "ffb", "set", "--device", "*", "--game", $FfbGame, "--param", ($Key + "=" + $Old), "--text")
        Assert-True ($set.ExitCode -eq 0) ("ffb set (" + $Label + ") failed: " + $set.Output)
        $exported = Run-Engine (@("--profile", $SettingsProfile) + $Export + @("--text"))
        Assert-True ($exported.ExitCode -eq 0) ("settings export (" + $Label + ") failed: " + $exported.Output)
        $text = [System.IO.File]::ReadAllText($File)
        Assert-True ($text.Contains($Written)) ("the " + $Label + " export should write '" + $Written + "': " + $exported.Output)
        $status = SyncJson (Run-Engine @("sync", "status", "--game", $SyncGame, "--path", $Config)).Output
        Assert-True ($status.Games[0].State -eq "unchanged") ("sync status after the " + $Label + " export should be unchanged: " + ($status.Games | ConvertTo-Json -Depth 4))
        Assert-True (@((SyncJson (Run-Engine @("--profile", $SettingsProfile, "sync", "diff", "--game", $SyncGame, "--path", $Config)).Output).Changes).Count -eq 0) ("right after the " + $Label + " export the diff should be empty (exact round trip).")
        [System.IO.File]::WriteAllText($File, $text.Replace($Written, $GameWrites), $syncUtf8)
        Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", $SyncGame, "--path", $Config)).Output).Games[0].State -eq "changed") ("sync status should see the " + $Label + " setting changed in " + [System.IO.Path]::GetFileName($File) + ".")
        $diffRun = Run-Engine @("--profile", $SettingsProfile, "sync", "diff", "--game", $SyncGame, "--path", $Config)
        Assert-True ($diffRun.ExitCode -eq 0) ("sync diff (" + $Label + ") failed: " + $diffRun.Output)
        $changes = @((SyncJson $diffRun.Output).Changes)
        Assert-True ($changes.Count -eq 1 -and $changes[0].Kind -eq "changed-setting" -and $changes[0].SettingKey -eq $Key -and $changes[0].OldValue -eq [double]$Old -and $changes[0].NewValue -eq [double]$New) ("sync diff (" + $Label + ") should report " + $Key + " " + $Old + " -> " + $New + ": " + $diffRun.Output)
        $choices = Join-Path $syncRoot ($SyncGame + "-settings-choices.json")
        SyncChoices $choices @(@{ Id = $changes[0].Id; Target = "apply" })
        $applied = Run-Engine @("--profile", $SettingsProfile, "sync", "apply", "--game", $SyncGame, "--path", $Config, "--choices", $choices)
        Assert-True ($applied.ExitCode -eq 0 -and (SyncJson $applied.Output).Applied -eq 1) ("sync apply (" + $Label + ") failed: " + $applied.Output)
        Assert-Match (Run-Engine @("--profile", $SettingsProfile, "ffb", "show", "--device", "*", "--game", $FfbGame, "--text")).Output ($Key + "=" + [regex]::Escape($New) + "(;|\s|$)") ("the game's " + $Key + " should become the profile's " + $Label + " setting.")
        Assert-True ((SyncJson (Run-Engine @("sync", "status", "--game", $SyncGame, "--path", $Config)).Output).Games[0].State -eq "unchanged") ("after sync apply the " + $Label + " files count as known.")
        Assert-True (@((SyncJson (Run-Engine @("--profile", $SettingsProfile, "sync", "diff", "--game", $SyncGame, "--path", $Config)).Output).Changes).Count -eq 0) ("after sync apply nothing is offered again (" + $Label + ").")
    }

    $syncA3Dir = Join-Path $syncRoot "arma3"
    New-Item -Path $syncA3Dir -ItemType Directory -Force | Out-Null
    $syncA3File = Join-Path $syncA3Dir "Tuning.Arma3Profile"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\arma3\Tuning.Arma3Profile") -Destination $syncA3File -Force
    SyncSettingRoundTrip "Arma 3" "Arma3" "Arma3" (Join-Path $syncRoot "arma3.pr0file") @("arma3", "export", $syncA3File) $syncA3File $syncA3File "RTDEnabled" "1" "0" "RTDEnabled=1;" "RTDEnabled=0;"

    $syncWdDir = Join-Path $syncRoot "wardogs"
    New-Item -Path $syncWdDir -ItemType Directory -Force | Out-Null
    $syncWdIni = Join-Path $syncWdDir "GameUserSettings.ini"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\wardogs\GameUserSettings.ini") -Destination $syncWdIni -Force
    SyncSettingRoundTrip "WARDOGS" "Wardogs" "Wardogs" (Join-Path $syncRoot "wardogs.pr0file") @("wardogs", "export", $syncWdIni) $syncWdIni $syncWdIni "enableHotas" "0" "1" "bUseHOTASHelicopters=False" "bUseHOTASHelicopters=True"

    $syncX4Dir = Join-Path $syncRoot "x4\5678"
    New-Item -Path $syncX4Dir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\x4-settings\5678\*") -Destination $syncX4Dir -Force
    $syncX4Map = Join-Path $syncX4Dir "inputmap.xml"
    $syncX4Profile = Join-Path $syncRoot "x4.pr0file"
    SyncSettingRoundTrip "X4 inputmap.xml" "X4" "X4" $syncX4Profile @("x4", "export", "--path", $syncX4Map) $syncX4Map $syncX4Map "deadzone" "7" "12" '<param name="deadzone" value="7"/>' '<param name="deadzone" value="12"/>'
    # config.xml: rumble 0..1 in the file, % in JoyPro.
    SyncSettingRoundTrip "X4 config.xml" "X4" "X4" $syncX4Profile @("x4", "export", "--path", $syncX4Map) $syncX4Map (Join-Path $syncX4Dir "config.xml") "rumbleIntensity" "40" "75" "<rumbleintensity>0.40</rumbleintensity>" "<rumbleintensity>0.75</rumbleintensity>"

    $syncArSave = Join-Path $syncRoot "reforger\profile\.save"
    $syncArPresets = Join-Path $syncArSave "settings\customInputConfigs"
    $syncArSettings = Join-Path $syncArSave "app1874880_user2\settings"
    New-Item -Path $syncArPresets -ItemType Directory -Force | Out-Null
    New-Item -Path $syncArSettings -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\settings19\*") -Destination $syncArSettings -Force
    $syncArPreset = Join-Path $syncArPresets "JoyPro.conf"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\Smoke.conf") -Destination $syncArPreset -Force
    SyncSettingRoundTrip "Arma Reforger" "ArmaReforger" "ArmaReforger" (Join-Path $syncRoot "reforger.pr0file") @("reforger", "export", $syncArPreset) $syncArPreset (Join-Path $syncArSettings "ReforgerGameSettings.conf") "hotasCollective" "1" "2" "m_eHotasCollective 1" "m_eHotasCollective 2"

    $syncVtDir = Join-Path $syncRoot "vtolvr\SaveData"
    New-Item -Path $syncVtDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\vtolvr\native\inputbindings.cfg") -Destination $syncVtDir -Force
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\vtolvr\settings\gameSettings.cfg") -Destination $syncVtDir -Force
    $syncVtBindings = Join-Path $syncVtDir "inputbindings.cfg"
    # The settings belong to game VTOLVR (ffb set), the watched config is the native integration's (VTOLVRNative).
    SyncSettingRoundTrip "VTOL VR" "VTOLVRNative" "VTOLVR" (Join-Path $syncRoot "vtolvr.pr0file") @("vtolvr-native", "export", $syncVtBindings) $syncVtBindings (Join-Path $syncVtDir "gameSettings.cfg") "TWIST_RUDDER_DEADZONE" "5.5" "4.25" "`tTWIST_RUDDER_DEADZONE = 5.5" "`tTWIST_RUDDER_DEADZONE = 4.25"
    $env:JOYPRO_FAKE_DEVICES = $syncPreviousFakeDevices

    # Usage errors.
    Assert-True ((Run-Engine @("sync", "diff")).ExitCode -ne 0) "sync diff without --game must fail."
    Assert-True ((Run-Engine @("sync", "apply", "--game", "DCS", "--path", $syncDcsRoot)).ExitCode -ne 0) "sync apply without --choices must fail."
}
finally {
    $env:JOYPRO_DATA_DIR = $syncPreviousData
    $env:JOYPRO_FAKE_DEVICES = $syncPreviousFakeDevices
}
