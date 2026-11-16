# The auto setup (docs/auto-setup-eval.md, docs/setup-wizard.md#start-page):
# - setup eval on the popular setups and a few random ones against the main games and DCS families, with thresholds:
#   a planner change that loses operations or breaks the hand rules fails here (the full run: engine setup eval);
# - Auto Group and Fill all on the setup's profile (no wrong-kind matches) and the novice case (IL-2 + URSA MINOR);
# - setup games: a game with binds of its own, one without, read-only;
# - setup preview: the games and aircraft of a plan, one sheet per device; the profile without binds has none;
# - a gamepad alone flies (main stick, gear on its buttons); --only-aircraft keeps the plan to the owned modules.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs the development game lists.

$asDir = Join-Path $tempDir "autosetup"
$asData = Join-Path $asDir "data"
New-Item -Path $asData -ItemType Directory -Force | Out-Null
$asPreviousData = $env:JOYPRO_DATA_DIR
$asPreviousDevices = $env:JOYPRO_FAKE_DEVICES
$asPreviousAssume = $env:JOYPRO_TEST_ASSUME_INSTALLED
$asPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = $asData
$asUtf8 = New-Object System.Text.UTF8Encoding($false)

try {
    # --- the evaluation with thresholds (measured 2026-11-14: see docs/auto-setup-eval.md) ---------------------------
    # (round 6, 2026-11-15: thresholds at the measured value minus 2 - popular 92.9 %, typical DCS
    # hangar 89.0 %, ACE COMBAT 8 alone 80.0 %; 0 destructive; all rows 80 %: the six fresh random setups swing the
    # all-rows rate between about 83 and 91 % from seed to seed (measured 90.9 and 87.5 %) - plus the per-gate non-regression against the stored
    # baseline of the popular setups' rows (engine/tests/fixtures/setup-eval-gate-baseline.json): a drop in look, PTT
    # or flaps fails even when the averages hold. Regenerate the baseline with --write-gate-baseline after a deliberate
    # change, popular setups only, the same games and families. All rows 75 % since round 7: seed 683404034 draws a VKB
    # Gladiator MCG Pro whose trigger stages have no published numbers, a stick without any throttle and three
    # dual-stick / small sets - 17 % of its random rows pass (the round-6 engine: 13 %), 78.7 % of all rows; its two
    # core-gate setups are in the regression set. Round 7 rewrote it after review 5: flaps gate +10, release
    # +2, gear +5, countermeasures +1 rows; flaps -4, PTT -3, look -2, target -2 essential rows - JoyPro shifts are now
    # reachable while flying (L22: no stick-base shift while the left hand holds the throttle, a left stick's grip may hold
    # it: airbrake -2, PTT -1, look +1) and one thumb stick is used
    # in one mode only (L19).)
    $asEvalDir = Join-Path $asDir "eval"
    # Random setups with a fresh seed every run (the eval draws one and names it), plus the tracked regression set (a copy:
    # the smoke never writes the tracked file). A failing fresh seed is named in the output with its setups' devices: add
    # it to engine/tests/fixtures/setup-eval-regression.tsv (engine setup eval --seed N --regression-file <that file>).
    $asRegression = Join-Path $asDir "setup-eval-regression.tsv"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\setup-eval-regression.tsv") -Destination $asRegression
    $asRegressionCount = @(Get-Content -Path $asRegression | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith("#") }).Count
    $asEval = Run-Engine @("setup", "eval", "--random", "6", "--regression-file", $asRegression,
        "--games", "StarCitizen,EliteDangerous,WarThunder,MSFS2024,IL2Game,XPlane12,FalconBMS",
        "--families", "F-16C_50,FA-18C_hornet,A-10C II,AH-64D_BLK_II_PLT,UH-1H,P-51D", "--no-stress", "--no-pipeline", "--no-persona",
        "--out-dir", $asEvalDir,
        "--min-coverage", "86", "--min-popular-coverage", "90", "--min-ergonomics", "80", "--min-flight", "99",
        "--min-hard-pass", "75", "--min-popular-hard-pass", "90", "--max-destructive", "0",
        "--popular-zero-gates", "fire,wheel_brakes,throttle,destructive", "--min-typical-hard-pass", "87", "--min-pad-hard-pass", "78",
        "--gate-baseline", (Join-Path $engineDir "tests\fixtures\setup-eval-gate-baseline.json"), "--text")
    Assert-True ($asEval.ExitCode -eq 0) ("setup eval fell below its thresholds (a planner regression? the seed and the failing setups are named below):" + [Environment]::NewLine + $asEval.Output)
    Assert-Match $asEval.Output "(?m)^Random \(seed \d+, repeat with --seed \d+\)" ("setup eval did not name its seed: " + $asEval.Output)
    Assert-Match $asEval.Output "(?m)^Thresholds: all passed" ("setup eval did not report its thresholds: " + $asEval.Output)
    Assert-True (Test-Path (Join-Path $asEvalDir "report.md")) "setup eval wrote no report.md."
    $asResults = Get-Content -Path (Join-Path $asEvalDir "results.json") -Raw | ConvertFrom-Json
    Assert-True (@($asResults.Rows).Count -eq (26 + $asRegressionCount) * 13) ("setup eval should score " + (26 + $asRegressionCount) + " setups (20 popular, 6 random, the regression set) x 13 targets, got " + @($asResults.Rows).Count + " rows.")
    Assert-True ($asResults.Seed -gt 0) "results.json carries no seed."
    # Hard gates (measured 2026-11-14 after fix round 3: 82.9 % of these rows, popular 87.1 %, typical DCS hangar 63.1 %, no destructive binding; no popular setup may lose the gun, the brakes or the throttle): the
    # gun on the trigger, both brakes or the brake button, rudder on the pedals, throttle on a lever, nothing on a detent.
    $asWarthogF16 = @($asResults.Rows | Where-Object { $_.Setup -eq "warthog-tfrp" -and $_.Target -eq "DCS:F-16C_50" })[0]
    Assert-True ($asWarthogF16.HardPass) ("Warthog + TFRP in the F-16 must pass every hard gate: " + (@($asWarthogF16.Gates | Where-Object { $_.Status -eq "fail" } | ForEach-Object { $_.Gate + " (" + $_.Detail + ")" }) -join "; "))
    $asT16 = @($asResults.Rows | Where-Object { $_.Setup -eq "t16000m-fcs" -and $_.Target -eq "DCS:F-16C_50" })[0]
    Assert-True (@($asT16.Gates | Where-Object { $_.Gate -eq "fire" -and $_.Status -eq "ok" }).Count -eq 1) "T.16000M (single-stage trigger) in the F-16: the gun must be on the trigger, not the camera detent."
    $asPad = @($asResults.Rows | Where-Object { $_.Setup -eq "gamepad-only" -and $_.Target -eq "DCS:F-16C_50" })[0]
    Assert-True ($null -ne $asPad -and @($asPad.OpsDone) -contains "gear" -and @($asPad.OpsDone) -contains "pitch") "a gamepad alone must fly the F-16 (pitch) and work the gear."
    $asTwin = @($asResults.Setups | Where-Object { $_.Id -eq "space-dual-t16000m" })[0]
    Assert-True (@($asTwin.Roles | Where-Object { $_ -match "= left-stick" }).Count -eq 1) "the second of two identical T.16000M must be the left stick."

    # --- Every game the wizard plans flies (review 6 B7 / L26): on typical stick + throttle setups each wizard game - the
    # 34 games of EngineSetupEval.WizardGames, FSX / P3D, IL-2 1946, Cliffs of Dover, Tiny Combat Arena, G-Rebels, Project
    # Wingman, Battlefield and HLL Vietnam included - reaches pitch, roll and throttle (or its collective / thrust); the
    # eval's "wizard flight axes" check fails otherwise. ------------------------------------------------------------------
    $asAxesDir = Join-Path $asDir "wizard-axes"
    $asAxes = Run-Engine @("setup", "eval", "--setups", "t16000m-fcs,x56,tflight-hotas4", "--random", "0", "--no-regression", "--families", "none",
        "--no-stress", "--no-pipeline", "--no-persona", "--no-typical", "--no-pad-alone", "--out-dir", $asAxesDir, "--text")
    Assert-True ($asAxes.ExitCode -eq 0) ("a wizard game misses pitch, roll or throttle on a stick + throttle setup:" + [Environment]::NewLine + $asAxes.Output)
    $asAxesLine = [regex]::Match($asAxes.Output, "(?m)^Wizard flight axes \(pitch, roll, throttle on stick \+ throttle setups\): (\d+) of (\d+) reached in (\d+) rows")
    Assert-True ($asAxesLine.Success -and $asAxesLine.Groups[1].Value -eq $asAxesLine.Groups[2].Value -and [int]$asAxesLine.Groups[3].Value -ge 3 * 33) ("setup eval did not check the wizard games' flight axes: " + $asAxes.Output)

    # --- Catalog descriptions are no printed labels (review 6 B10): the CH Fighterstick's "4-way hat Left (buttons 5-8)"
    # hats are used (the "4" took the UFC key 4), the CH Flightstick Pro's "Left / Right / Down position button" carry
    # no position (navigation) lights and the weapon release is bound. Catalog devices only (no real device is read). --
    $asNoDevices = Join-Path $asDir "no-devices.tsv"
    [System.IO.File]::WriteAllText($asNoDevices, "# JoyPro device`tVID`tPID`tHID product string`taxes`tbuttons`thats`r`n", $asUtf8)
    $asLabelPrevious = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = $asNoDevices
    try {
        $asCh = Run-Engine @("--load-corrections", "false", "--text", "setup", "plan", "--games", "DCS", "--only-aircraft", "F-16C_50", "--add-device", "ch-fighterstick", "--add-device", "ch-pro-throttle")
        Assert-True ($asCh.ExitCode -eq 0) ("setup plan with the CH Fighterstick failed: " + $asCh.Output)
        foreach ($asHat in @("stick.hat.2", "stick.hat.3", "stick.hat.4", "throttle.hat.2")) {
            Assert-Match $asCh.Output ("\(" + [regex]::Escape($asHat) + "\.(up|down|left|right) ") ("the CH hat " + $asHat + " stays empty: a catalog description was taken for a printed label.")
        }
        $asChPro = Run-Engine @("--load-corrections", "false", "--text", "setup", "plan", "--games", "DCS", "--only-aircraft", "F-16C_50", "--add-device", "ch-flightstick-pro")
        Assert-True ($asChPro.ExitCode -eq 0) ("setup plan with the CH Flightstick Pro failed: " + $asChPro.Output)
        Assert-True (-not ($asChPro.Output -match "position button`"\) => Lights")) ("the CH Flightstick Pro's position buttons got lights (description taken for a label): " + $asChPro.Output)
        Assert-Match $asChPro.Output "stick\.button\.thumb\.\d `"[A-Za-z]+ position button`"\) => LaunchWeapon" "the CH Flightstick Pro's weapon release is not on a thumb button."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $asLabelPrevious
    }

    # --- The gun on the trigger of every catalog grip (review 7 B12 / L44) -------------------------------------------
    # Every catalog stick or grip with a numbered trigger, planned alone for DCS (F-16), IL-2 and Star Citizen: the gun of
    # every game lands on a trigger without a modifier (a "detent" in a trigger label once kept it off three VKB grips).
    $asGrips = Run-Engine @("setup", "grip-sweep")
    Assert-True ($asGrips.ExitCode -eq 0) ("the gun is not on the trigger of every catalog grip:" + [Environment]::NewLine + $asGrips.Output)
    Assert-Match $asGrips.Output "(?m)^Grip sweep: [1-9]\d+ grips with a trigger" ("the grip sweep saw no grips: " + $asGrips.Output)

    # --- Where functions go (the slider "Same button for the same job" <-> "Where the real aircraft has it") ----------
    # Five positions on three popular setups' typical DCS hangar: cockpit-position fidelity never falls and cross-aircraft
    # consistency never rises as the slider moves to the real aircraft, the gun / throttle and no destructive binding hold at
    # every position (setup eval --placement-sweep fails otherwise).
    $asPlaceDir = Join-Path $asDir "placement"
    # (L21, round 7: six setups incl. the small ones - Honeycomb, T.Flight, T.16000M + button box - and the F-16 / A-10C alone
    # besides the hangar; round 8: the TCA too - its hangar countermeasures were a known limit until the flare rescue of
    # review 6 B8, and the gun, the release and the countermeasures must hold at every position, L30)
    $asPlace = Run-Engine @("setup", "eval", "--setups", "t16000m-fcs,warthog-tfrp,x56,honeycomb-alpha-bravo,tflight-hotas4,t16000m-buttonbox,tca-airbus", "--random", "0", "--no-regression", "--games", "MSFS2024",
        "--families", "F-16C_50", "--no-stress", "--no-pipeline", "--no-persona", "--no-typical", "--no-pad-alone", "--out-dir", $asPlaceDir,
        "--placement-sweep", "0,0.25,0.5,0.75,1", "--text")
    Assert-True ($asPlace.ExitCode -eq 0) ("the placement sweep failed (fidelity / consistency not monotonic, or a safety gate breaks at a position):" + [Environment]::NewLine + $asPlace.Output)
    Assert-True (@([regex]::Matches($asPlace.Output, "(?m)^Placement \d\.\d\d: .*cockpit-position fidelity")).Count -eq 5) ("the placement sweep did not report five positions: " + $asPlace.Output)
    $asPlaceJson = Get-Content -Path (Join-Path $asPlaceDir "placement.json") -Raw | ConvertFrom-Json
    Assert-True ($asPlaceJson.Points[4].Fidelity -gt $asPlaceJson.Points[0].Fidelity) "moving the slider to the real aircraft does not bring functions closer to the real HOTAS."
    Assert-True ($asPlaceJson.Points[4].Consistency -lt $asPlaceJson.Points[0].Consistency) "moving the slider to the real aircraft does not change the same-button-for-the-same-job grouping."
    # (review 7 M12 / L41: the other games alone at every position too - X4 kept roll, Elite no hardpoints on SYS)
    Assert-True ([int]$asPlaceJson.Points[0].GameRows -ge 7 * 8) ("the placement sweep did not plan the other games alone: " + $asPlaceJson.Points[0].GameRows + " rows.")

    # --- head tracking yes (L24): no look-around button, no padlock / look-ahead, the recenter bound -------------------
    $asHtDir = Join-Path $asDir "headtracking"
    $asHt = Run-Engine @("setup", "eval", "--setups", "t16000m-fcs,x56,tca-airbus", "--random", "0", "--no-regression", "--games", "IL2Game,StarCitizen,MSFS2024",
        "--families", "F-16C_50,A-10C II", "--no-stress", "--no-pipeline", "--no-persona", "--no-typical", "--no-pad-alone", "--head-tracking", "yes", "--out-dir", $asHtDir, "--text")
    Assert-True ($asHt.ExitCode -eq 0) ("setup eval --head-tracking yes failed: " + $asHt.Output)
    $asHtJson = Get-Content -Path (Join-Path $asHtDir "results.json") -Raw | ConvertFrom-Json
    $asHtLook = @($asHtJson.Rows | ForEach-Object { $row = $_; @($row.Gates | Where-Object { $_.Gate -eq "look" -and $_.Status -eq "fail" }) | ForEach-Object { $row.Setup + " x " + $row.Target + ": " + $_.Detail } })
    Assert-True ($asHtLook.Count -eq 0) ("with head tracking no look button may be bound and the recenter must be: " + ($asHtLook -join "; "))

    # --- Auto Group + Fill all on the setup's profile, and the novice case (IL-2 + WinWing URSA MINOR) ----------------
    $asPipeDir = Join-Path $asDir "pipeline"
    # (review 6 B9 / L34, round 8: the Mi-8 too - its cyclic trigger was lost by setup apply; no essential gate may be lost
    # from the setup alone to the all-games plan or from that plan to Auto Group + Fill all (zero tolerance by default), and
    # the gun, the weapon release, the countermeasures and the throttle hold after the pipeline)
    $asPipe = Run-Engine @("setup", "eval", "--setups", "warthog-tfrp,x56", "--random", "0", "--games", "IL2Game,StarCitizen",
        "--families", "F-16C_50,mi-8mt", "--no-stress", "--out-dir", $asPipeDir, "--min-pipeline-coverage", "90", "--max-wrong", "60", "--max-pipeline-destructive", "0", "--no-typical", "--no-pad-alone",
        "--pipeline-zero-gates", "fire,release,countermeasures,throttle", "--text")
    Assert-True ($asPipe.ExitCode -eq 0) ("setup eval with Auto Group / Fill all or the novice case fell below its thresholds:" + [Environment]::NewLine + $asPipe.Output)
    Assert-NotMatch $asPipe.Output "lost after Auto Group|lost in the all-games plan" ("an essential was lost on the quick path: " + $asPipe.Output)
    $asPipeJson = Get-Content -Path (Join-Path $asPipeDir "results.json") -Raw | ConvertFrom-Json
    Assert-True (@($asPipeJson.PipelineRows).Count -eq 2 * 4) ("Auto Group / Fill all should score 2 setups x 4 targets, got " + @($asPipeJson.PipelineRows).Count + " rows.")
    foreach ($asMi8 in @($asPipeJson.PipelineRows | Where-Object { $_.Target -eq "DCS:mi-8mt" })) {
        Assert-True (@($asMi8.Gates | Where-Object { $_.Gate -eq "fire" -and $_.Status -eq "ok" }).Count -eq 1) ($asMi8.Setup + ": the Mi-8's trigger must survive Auto Group + Fill all.")
    }

    # --- the quick path's one plan for DCS and IL-2 keeps the F/A-18's throttles (review 6 B6) -------------------------
    $env:JOYPRO_FAKE_DEVICES = Join-Path $asEvalDir "devices-warthog-tfrp.tsv"
    $asTwoGamesFile = Join-Path $asDir "two-games.json"
    $asTwoGames = Run-Engine @("setup", "plan", "--games", "DCS,IL2Game", "--only-aircraft", "F-16C_50,FA-18C_hornet", "--out-plan", $asTwoGamesFile, "--text")
    Assert-True ($asTwoGames.ExitCode -eq 0) ("setup plan for DCS + IL-2 failed: " + $asTwoGames.Output)
    $asTwoGamesJson = Get-Content -Path $asTwoGamesFile -Raw | ConvertFrom-Json
    foreach ($asSide in @("throttleleft", "throttleright")) {
        Assert-True (@($asTwoGamesJson.Relations | Where-Object { $_.Function -eq $asSide -and $_.Input -and @($_.Nodes | Where-Object { $_.Aircraft -eq "FA-18C_hornet" }).Count -gt 0 }).Count -ge 1) ("DCS + IL-2 in one plan: the F/A-18's " + $asSide + " has no lever.")
    }
    Assert-True (Test-Path (Join-Path $asPipeDir "persona.md")) "setup eval wrote no persona.md (the novice case)."
    $asPersona = Get-Content -Path (Join-Path $asPipeDir "persona.json") -Raw | ConvertFrom-Json
    $asUnknown = @($asPersona.Setups | Where-Object { $_.Id -eq "ursa-unknown" })[0]
    Assert-True (@($asUnknown.Roles | Where-Object { $_ -match "not recognised: generic layout" }).Count -eq 2) "the unknown URSA MINOR stick and throttle must be set up generically."
    $asFighter = @($asPersona.Rows | Where-Object { $_.Setup -eq "ursa-fighter" })[0]
    foreach ($asOp in @("pitch", "roll", "yaw", "throttle", "prop_pitch", "fire", "release", "gear", "flaps", "trim", "view", "comms")) {
        Assert-True (@($asFighter.OpsDone) -contains $asOp) ("the novice case (IL-2, URSA MINOR Fighter) misses " + $asOp + ": " + (@($asFighter.Gaps | ForEach-Object { $_.Op + " (" + $_.Why + ")" }) -join "; "))
    }

    # --- the pad persona: ACE COMBAT 8 alone with the Setup wizard's defaults (no Auto Group, no Fill all) -----------
    # T.Flight HOTAS 4 and VKB Gunfighter + Virpil MT-50CM + MFG Crosswind: every flight control reaches the pad.
    $asPadDir = Join-Path $asDir "pad"
    $asPad = Run-Engine @("setup", "eval-pad", "--out-dir", $asPadDir, "--text")
    Assert-True ($asPad.ExitCode -eq 0) ("the ACE COMBAT 8 pad persona failed (the wizard alone must put the flight controls on the pad):" + [Environment]::NewLine + $asPad.Output)
    Assert-Match $asPad.Output "(?m)^Pad persona: passed" ("setup eval-pad did not pass: " + $asPad.Output)
    $asPadJson = Get-Content -Path (Join-Path $asPadDir "pad-persona.json") -Raw | ConvertFrom-Json
    Assert-True (@($asPadJson.Rows).Count -eq 2) ("the pad persona should score 2 setups, got " + @($asPadJson.Rows).Count + " rows.")
    foreach ($asPadRow in @($asPadJson.Rows)) {
        foreach ($asPadGate in @("pitch", "roll", "throttle", "yaw", "fire", "release", "pad target", "countermeasures")) {
            Assert-True (@($asPadRow.Gates | Where-Object { $_.Gate -eq $asPadGate -and $_.Status -eq "ok" }).Count -eq 1) ($asPadRow.Setup + ": ACE COMBAT 8 misses " + $asPadGate + " after the wizard.")
        }
        Assert-True ([double]$asPadRow.Quality -lt 100 -or [int]$asPadRow.Placed -ge 14) ($asPadRow.Setup + ": the wizard's quality must not read 100 % with few functions placed.")
    }
    Assert-True (Test-Path (Join-Path $asPadDir "pad-persona.md")) "setup eval-pad wrote no pad-persona.md."
    # The wizard plans ACE COMBAT 8 (games support and setup games agree).
    $asPadGames = Run-Engine @("setup", "games", "--games", "AceCombat8")
    Assert-True ($asPadGames.ExitCode -eq 0 -and @(($asPadGames.Output | ConvertFrom-Json).Games)[0].Wizard -eq $true) ("setup games must offer the wizard for ACE COMBAT 8: " + $asPadGames.Output)
    # A flying game whose table has no pitch / roll yet (Project Wingman): the quality must not read 100 %.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $asPadDir "devices-ac8-tflight.tsv"
    $asNoAxes = Run-Engine @("setup", "plan", "--games", "ProjectWingman", "--modifiers", "0", "--prefer-fewer", "--text")
    if ($asNoAxes.Output -match "catalog has no pitch") {
        Assert-NotMatch $asNoAxes.Output "ProjectWingman: 100\.0 %" ("a game without flight axes in the catalog must not score 100 %: " + $asNoAxes.Output)
    }

    # --- setup games: binds of the user's own in the game vs nothing assigned (read-only) ---------------------------
    $env:JOYPRO_TEST_ASSUME_INSTALLED = "1"
    $env:JOYPRO_INSTALL_DETECTION = "off"
    $asSaved = Join-Path $asDir "SavedGames-DCS"
    $asDiff = Join-Path $asSaved "Config\Input\F-16C_50\joystick\Smoke Stick {12345678-0000-0000-0000-000000000001}.diff.lua"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $asDiff) | Out-Null
    [System.IO.File]::WriteAllText($asDiff, "local diff = {`n  [`"keyDiffs`"] = {`n    [`"d_trim`"] = {`n      [`"name`"] = `"Trim`",`n      [`"added`"] = {`n        [1] = { [`"key`"] = `"JOY_BTN5`", },`n      },`n    },`n  },`n}`nreturn diff`n", $asUtf8)
    $asEmpty = Join-Path $asDir "SavedGames-Empty"
    New-Item -ItemType Directory -Force -Path (Join-Path $asEmpty "Config\Input") | Out-Null
    $asBefore = (Get-ChildItem -Path $asSaved -Recurse -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ","
    $asProfile = Join-Path $asDir "games.pr0file"
    $asGames = Run-Engine @("--profile", $asProfile, "setup", "games", "--games", "DCS", "--path", ("DCS=" + $asSaved))
    Assert-True ($asGames.ExitCode -eq 0) ("setup games failed: " + $asGames.Output)
    $asGamesJson = $asGames.Output | ConvertFrom-Json
    $asDcs = @($asGamesJson.Games)[0]
    Assert-True ($asDcs.Status -eq "game" -and $asDcs.GameBinds -ge 1 -and $asDcs.Wizard) ("DCS with a bind of the user's own must be 'game': " + $asGames.Output)
    # --status-only (the Start page): a game the profile has no binds for is still read, and says the same.
    $asGamesQuick = Run-Engine @("--profile", $asProfile, "setup", "games", "--games", "DCS", "--path", ("DCS=" + $asSaved), "--status-only")
    $asDcsQuick = @(($asGamesQuick.Output | ConvertFrom-Json).Games)[0]
    Assert-True ($asGamesQuick.ExitCode -eq 0 -and $asDcsQuick.Status -eq "game" -and $asDcsQuick.GameBindsRead -eq $true -and $asDcsQuick.GameBinds -eq $asDcs.GameBinds) ("setup games --status-only must read a game the profile has no binds for: " + $asGamesQuick.Output)
    $asGamesEmpty = Run-Engine @("--profile", $asProfile, "setup", "games", "--games", "DCS", "--path", ("DCS=" + $asEmpty))
    Assert-True ($asGamesEmpty.ExitCode -eq 0) ("setup games failed: " + $asGamesEmpty.Output)
    Assert-True (@(($asGamesEmpty.Output | ConvertFrom-Json).Games)[0].Status -eq "empty") ("DCS without binds must be 'empty': " + $asGamesEmpty.Output)
    $asAfter = (Get-ChildItem -Path $asSaved -Recurse -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join ","
    Assert-True ($asBefore -eq $asAfter) "setup games changed a control file of the game (it must only read)."
    Assert-True (@(Get-ChildItem -Path $asSaved -Recurse -File).Count -eq 1) "setup games wrote a file into the game folder."

    # --- a plan for the owned modules only, and its preview -------------------------------------------------------
    $env:JOYPRO_FAKE_DEVICES = Join-Path $asEvalDir "devices-warthog-tfrp.tsv"
    $asPlanFile = Join-Path $asDir "plan.json"
    $asPlan = Run-Engine @("setup", "plan", "--games", "DCS", "--only-aircraft", "F-16C_50", "--priorities", "F-16C_50", "--grip", "044F:0402=thrustmaster-warthog-stick", "--out-plan", $asPlanFile, "--text")
    Assert-True ($asPlan.ExitCode -eq 0) ("setup plan --only-aircraft failed: " + $asPlan.Output)
    Assert-NotMatch $asPlan.Output "(?m)^  - FA-18C_hornet" "--only-aircraft F-16C_50 planned the F/A-18 as well."
    $asCheck = Run-Engine @("setup", "check", "--plan", $asPlanFile, "--text")
    Assert-True ($asCheck.ExitCode -eq 0) ("setup check of the module plan failed: " + $asCheck.Output)
    $asPreview = Run-Engine @("setup", "preview", "--plan", $asPlanFile)
    Assert-True ($asPreview.ExitCode -eq 0) ("setup preview failed: " + $asPreview.Output)
    $asPreviewJson = $asPreview.Output | ConvertFrom-Json
    Assert-True (@($asPreviewJson.Games | Where-Object { $_.Game -eq "DCS" -and @($_.Aircraft) -contains "F-16C_50" }).Count -eq 1) ("setup preview must list DCS / F-16C_50: " + $asPreview.Output)
    $asSheets = Join-Path $asDir "sheets"
    $asDraw = Run-Engine @("setup", "preview", "--plan", $asPlanFile, "--game", "DCS", "--aircraft", "F-16C_50", "--out-dir", $asSheets)
    Assert-True ($asDraw.ExitCode -eq 0) ("setup preview --aircraft failed: " + $asDraw.Output)
    $asDrawJson = $asDraw.Output | ConvertFrom-Json
    Assert-True (@($asDrawJson.Sheets).Count -eq 3) ("setup preview should draw the stick, the throttle and the pedals: " + $asDraw.Output)
    Assert-True (@($asDrawJson.Sheets | Where-Object { -not (Test-Path $_.File) }).Count -eq 0) "a preview sheet file is missing."
    $asNone = Run-Engine @("--profile", (Join-Path $asDir "none.pr0file"), "setup", "preview", "--from-profile")
    Assert-True ($asNone.ExitCode -eq 0) ("setup preview --from-profile failed: " + $asNone.Output)
    Assert-True (@(($asNone.Output | ConvertFrom-Json).Games).Count -eq 0) "a profile without binds has nothing to preview."
    $asBad = Run-Engine @("setup", "preview")
    Assert-True ($asBad.ExitCode -ne 0) "setup preview without --plan or --from-profile must fail."
}
finally {
    $env:JOYPRO_DATA_DIR = $asPreviousData
    $env:JOYPRO_FAKE_DEVICES = $asPreviousDevices
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $asPreviousAssume
    $env:JOYPRO_INSTALL_DETECTION = $asPreviousDetection
}
