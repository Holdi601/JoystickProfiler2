# Same id, different meaning per aircraft (engine\EngineIdMeanings.cs, docs/relation-builder.md "Same id, different
# meaning"): fixtures (engine\tests\fixtures\per-aircraft\DB, read through JOYPRO_ACTION_INDEX_DIR) where a2001cdnil is
# "Pitch" in three jets and "Collective" in the UH-1H, the clickable id d3002...cd3 is "Master Arm Switch - ON" in two
# jets, "Canopy Open" in the F-16 and "Rotor Brake - ON" in the helicopter, dnilp95u215 is "Trim: Nose Up" in two jets
# and "Trim: Nose Down" in the F-16, and d3010...cd5 is "Pitch Trim Up" / "Trim pitch up" (near). Checks: the search
# splits the rows per meaning and reports the rest; suggest, resolve, the assistant, Auto Group and a node spec
# without aircraft leave the helicopter out; a node's aircraft selection (relations aircraft [set]) and the export to
# the selected aircraft only; relations check-aircraft finds and repairs a mixed node.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs no real game lists.

$paDir = Join-Path $tempDir "peraircraft"
New-Item -Path $paDir -ItemType Directory -Force | Out-Null
$paPreviousData = $env:JOYPRO_DATA_DIR
$paPreviousIndex = $env:JOYPRO_ACTION_INDEX_DIR
$env:JOYPRO_DATA_DIR = Join-Path $paDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = Join-Path $engineDir "tests\fixtures\per-aircraft\DB"

function Invoke-PaJson([string[]]$Arguments) {
    $out = Join-Path $paDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    $jets = @("F-15C", "F-16C_50", "FA-18C_hornet")

    # The search: one row per meaning, the other meaning listed as "differs" and counted.
    $paPitch = Invoke-PaJson @("relations", "search", "pitch", "--axis")
    $paRow = @($paPitch.Results | Where-Object { $_.Id -eq "a2001cdnil" })
    Assert-True ($paRow.Count -eq 1) ("search 'pitch' should give one a2001cdnil row (the jets), got " + $paRow.Count + ".")
    Assert-SetEquivalent @($paRow[0].Planes) $jets "the pitch row must hold the three jets only."
    Assert-True ($paRow[0].SameIdDifferent -eq 1) "the pitch row did not count the helicopter where a2001cdnil means something else."
    Assert-Match $paRow[0].MeaningNote "same id, different meaning in 1 aircraft" "the pitch row lacks its meaning note."
    $paDiffers = @($paRow[0].Variants | Where-Object { $_.Match -eq "differs" -and -not $_.Included })
    Assert-True ($paDiffers.Count -eq 1 -and $paDiffers[0].Title -eq "Collective" -and @($paDiffers[0].Planes) -contains "UH-1H") "the pitch row's variants must list the UH-1H's 'Collective' as differs."
    $paCollective = Invoke-PaJson @("relations", "search", "collective", "--axis")
    $paHeli = @($paCollective.Results | Where-Object { $_.Id -eq "a2001cdnil" })
    Assert-True ($paHeli.Count -eq 1 -and (@($paHeli[0].Planes) -join "|") -eq "UH-1H" -and $paHeli[0].Title -eq "Collective") "search 'collective' should give the UH-1H's a2001cdnil alone."

    # A niche cockpit id: another switch in every module.
    $paArm = Invoke-PaJson @("relations", "search", "master arm")
    $paArmRow = @($paArm.Results | Where-Object { $_.Id -eq "d3002pnilunilcd3vd1vpnilvunil" })
    Assert-True ($paArmRow.Count -eq 1) "search 'master arm' did not find the cockpit switch."
    Assert-SetEquivalent @($paArmRow[0].Planes) @("F-15C", "FA-18C_hornet") "the master arm row took aircraft where the id is another switch."
    Assert-True ($paArmRow[0].SameIdDifferent -eq 2) "the master arm row did not count the canopy and rotor brake meanings."

    # Near: the same words in another order are grouped and flagged; the opposite direction is its own row.
    $paTrim = Invoke-PaJson @("relations", "search", "trim pitch up")
    $paNear = @($paTrim.Results | Where-Object { $_.Id -eq "d3010pnilunilcd5vd1vpnilvunil" })
    Assert-True ($paNear.Count -eq 1 -and @($paNear[0].Planes).Count -eq 3 -and $paNear[0].Near -ge 1) "'Pitch Trim Up' and 'Trim pitch up' should be one row with a near flag."
    $paNose = Invoke-PaJson @("relations", "search", "trim nose up")
    $paNoseUp = @($paNose.Results | Where-Object { $_.Id -eq "dnilp95u215cdnilvdnilvpnilvunil" -and $_.Title -eq "Trim: Nose Up" })
    Assert-True ($paNoseUp.Count -eq 1) "search 'trim nose up' did not find 'Trim: Nose Up'."
    Assert-True (-not (@($paNoseUp[0].Planes) -contains "F-16C_50")) "the F-16's 'Trim: Nose Down' joined the nose up row of the same id."

    # resolve (the assistant's tool) and a node spec without aircraft leave the helicopter out.
    $paResolve = Invoke-PaJson @("relations", "resolve", "pitch", "--per-game", "0")
    $paResolved = @($paResolve.Games | ForEach-Object { $_.Actions } | Where-Object { $_.Id -eq "a2001cdnil" })
    Assert-True (@($paResolved | Where-Object { @($_.Planes) -contains "UH-1H" }).Count -eq 0) "resolve 'pitch' brought the helicopter's collective."
    $paProfile = Join-Path $paDir "peraircraft.pr0file"
    $paAdd = Run-Engine @("--profile", $paProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil", "--text")
    Assert-True ($paAdd.ExitCode -eq 0) ("relations add without aircraft failed: " + $paAdd.Output)
    $paNode = Invoke-PaJson @("--profile", $paProfile, "relations", "aircraft", "Pitch")
    $paEntry = $paNode.Nodes[0]
    Assert-SetEquivalent @($paEntry.Aircraft | Where-Object { $_.State -eq "on" } | ForEach-Object { $_.Plane }) $jets "a node spec without aircraft must take only the aircraft of the id's main meaning."
    Assert-True ($paEntry.Badge -eq "3 of 4 aircraft") ("the node's badge should read '3 of 4 aircraft' (" + $paEntry.Badge + ").")
    Assert-True (@($paEntry.Aircraft | Where-Object { $_.Plane -eq "UH-1H" -and $_.Match -eq "differs" -and $_.State -eq "absent" }).Count -eq 1) "the node's aircraft list must show the helicopter as differs, not in the node."

    # Suggestions never propose the helicopter for the pitch relation.
    $paSuggest = Invoke-PaJson @("--profile", $paProfile, "relations", "suggest", "Pitch", "--min", "low")
    Assert-True (@($paSuggest.Suggestions | Where-Object { $_.Id -eq "a2001cdnil" -and @($_.Planes) -contains "UH-1H" }).Count -eq 0) "suggest proposed the helicopter's collective for the pitch relation."

    # The assistant (offline brain): "all pitch axes" without the helicopter.
    $paAssist = Run-Engine @("--profile", $paProfile, "relations", "assistant", "Pitch ALL: all pitch axes", "--llm", "off", "--text")
    Assert-True ($paAssist.ExitCode -eq 0) ("the assistant failed: " + $paAssist.Output)
    $paAll = Invoke-PaJson @("--profile", $paProfile, "relations", "aircraft", "Pitch ALL", "--node", "DCS:a2001cdnil")
    Assert-True (@($paAll.Nodes[0].Aircraft | Where-Object { $_.Plane -eq "UH-1H" -and $_.State -eq "on" }).Count -eq 0) "the assistant added the helicopter's collective to a pitch relation."

    # Per-aircraft selection in one step, and the export writes the bind only for the selected aircraft.
    $paSet = Invoke-PaJson @("--profile", $paProfile, "relations", "aircraft", "set", "Pitch", "--node", "DCS:a2001cdnil", "--on", "UH-1H", "--off", "F-16C_50")
    Assert-True ($paSet.Changed -eq 2) ("relations aircraft set changed " + $paSet.Changed + " instead of 2.")
    Assert-True ($paSet.Nodes[0].Mixed -eq $true -and $paSet.Nodes[0].Differs -eq 1) "a node with the helicopter switched on must be flagged as mixed."
    Run-Engine @("--profile", $paProfile, "relations", "aircraft", "set", "Pitch", "--node", "DCS:a2001cdnil", "--off", "UH-1H") | Out-Null
    $paBind = Run-Engine @("--profile", $paProfile, "binds", "set", "Pitch", "--device", "Smoke Stick {00000000-0000-0000-0000-0000000000C2}", "--input", "JOY_Y", "--text")
    Assert-True ($paBind.ExitCode -eq 0) ("binds set for the export failed: " + $paBind.Output)
    $paRoot = Join-Path $paDir "saved-games"
    New-Item -ItemType Directory -Force -Path $paRoot | Out-Null
    # Without the DCS-default removals in the other aircraft, which write removal-only files there.
    $paExport = Run-Engine @("--profile", $paProfile, "dcs", "export", $paRoot, "--clean-all-aircraft", "false", "--text")
    Assert-True ($paExport.ExitCode -eq 0) ("dcs export failed: " + $paExport.Output)
    Assert-True (Test-Path (Join-Path $paRoot "Config\Input\F-15C\joystick")) "the export did not write a selected aircraft."
    Assert-True (-not (Test-Path (Join-Path $paRoot "Config\Input\F-16C_50"))) "the export wrote an aircraft switched off in the node."
    Assert-True (-not (Test-Path (Join-Path $paRoot "Config\Input\UH-1H"))) "the export wrote the helicopter where the id means something else."

    # The repair check: a node with every aircraft switched on (an old profile) is found and repaired in one step.
    $paOld = Join-Path $paDir "old.pr0file"
    Run-Engine @("--profile", $paOld, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-15C|F-16C_50|FA-18C_hornet|UH-1H", "--text") | Out-Null
    $paCheck = Invoke-PaJson @("--profile", $paOld, "relations", "check-aircraft")
    Assert-True ($paCheck.AffectedNodes -eq 1 -and $paCheck.AircraftToDrop -eq 1) ("check-aircraft should find one node with one aircraft to drop (" + $paCheck.AffectedNodes + ", " + $paCheck.AircraftToDrop + ").")
    Assert-True ((@($paCheck.Found[0].Drop) -join "|") -eq "UH-1H") "check-aircraft proposed the wrong aircraft."
    $paFix = Invoke-PaJson @("--profile", $paOld, "relations", "check-aircraft", "--apply")
    Assert-True ($paFix.Dropped -eq 1) "check-aircraft --apply did not switch the aircraft off."
    $paAfter = Invoke-PaJson @("--profile", $paOld, "relations", "check-aircraft")
    Assert-True ($paAfter.AffectedNodes -eq 0) "check-aircraft still finds the repaired node."
    $paFixed = Invoke-PaJson @("--profile", $paOld, "relations", "aircraft", "Pitch")
    Assert-True (@($paFixed.Nodes[0].Aircraft | Where-Object { $_.Plane -eq "UH-1H" -and $_.State -eq "off" }).Count -eq 1) "the repaired aircraft must stay in the node switched off (one tick re-includes it)."

    # Auto Group: the catalog files a2001cdnil under pitch for all four; the helicopter's own title says otherwise.
    $paGroupProfile = Join-Path $paDir "group.pr0file"
    $paGroup = Invoke-PaJson @("--profile", $paGroupProfile, "relations", "auto-group", "--planes", "F-15C|FA-18C_hornet|F-16C_50|UH-1H")
    Assert-True ($paGroup.MeaningExcluded -ge 1) "auto-group did not leave out the helicopter whose a2001cdnil means something else."
    Assert-True (@($paGroup.MeaningNotes | Where-Object { $_ -match "UH-1H a2001cdnil" }).Count -ge 1) "auto-group did not say why it left the helicopter out."
    $paGrouped = Invoke-PaJson @("--profile", $paGroupProfile, "relations", "aircraft", "Pitch", "--node", "DCS:a2001cdnil")
    Assert-True (@($paGrouped.Nodes[0].Aircraft | Where-Object { $_.Plane -eq "UH-1H" -and $_.State -eq "off" }).Count -eq 1) "auto-group must keep the left-out helicopter in the node switched off."
    Assert-True (@($paGrouped.Nodes[0].Aircraft | Where-Object { $_.Plane -eq "F-15C" -and $_.State -eq "on" }).Count -eq 1) "auto-group lost the F-15C's pitch."

    # Per game: only DCS shares ids between aircraft lists.
    $paStats = Invoke-PaJson @("relations", "meanings", "--top", "5")
    $paDcs = @($paStats.Games | Where-Object { $_.Game -eq "DCS" })[0]
    Assert-True ($paDcs.Mixed -ge 3) ("relations meanings counted too few ids with another meaning (" + $paDcs.Mixed + ").")
}
finally {
    $env:JOYPRO_DATA_DIR = $paPreviousData
    $env:JOYPRO_ACTION_INDEX_DIR = $paPreviousIndex
}
