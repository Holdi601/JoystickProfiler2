# Auto Assign's combination matrix in short (docs/auto-assign.md#combination-matrix) and Lay out with AI
# (docs/layout-with-ai.md) on fake device sets with the shipped device and function catalogs:
# - setup plan + setup check on a small stick + throttle, a pit with a take-off panel and two MFDs, two sticks for space
#   and a small set with a mode switch (phase layers on): every check passes, MFD keys in OSB order, a panel label for
#   its own function, the mode switch makes a phase layer;
# - relations auto-assign --phase-layers on on a profile the plan made;
# - arrange ask / apply / prefs: the phrases, a preview, one history version, the kept preferences used again.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs the DCS game list.

$amDir = Join-Path $tempDir "autoassignmatrix"
$amData = Join-Path $amDir "data"
New-Item -Path $amData -ItemType Directory -Force | Out-Null
$amPreviousData = $env:JOYPRO_DATA_DIR
$amPreviousDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = $amData
$amFixtures = Join-Path $engineDir "tests\fixtures\autoassign-matrix"

try {
    foreach ($amSet in @("stick-throttle", "pit-panels-mfds", "space-dual-sticks", "mode-switch-small")) {
        $env:JOYPRO_FAKE_DEVICES = Join-Path $amFixtures ($amSet + ".tsv")
        $amPlanFile = Join-Path $amDir ($amSet + ".json")
        $amArgs = @("setup", "plan", "--games", "DCS,StarCitizen", "--priorities", "F-16C_50>FA-18C_hornet|A-10C II>AH-64D_BLK_II_PLT|UH-1H>StarCitizen", "--modifiers", "1", "--prefer-fewer", "--realism", "0.3", "--out-plan", $amPlanFile, "--text")
        if ($amSet -eq "mode-switch-small") { $amArgs += @("--phase-layers", "on") }
        $amPlan = Run-Engine $amArgs
        Assert-True ($amPlan.ExitCode -eq 0) ("setup plan failed for " + $amSet + ": " + $amPlan.Output)
        $amCheck = Run-Engine @("setup", "check", "--plan", $amPlanFile, "--text")
        Assert-True ($amCheck.ExitCode -eq 0) ("setup check failed for " + $amSet + ": " + $amCheck.Output)
        Assert-Match $amCheck.Output "(?m)^Checks: all passed" ("a plan of " + $amSet + " broke a rule: " + $amCheck.Output)
        if ($amSet -eq "pit-panels-mfds") {
            Assert-Match $amPlan.Output "JOY_BTN\d+ \(mfd\.left\.osb\.T1\) => MFD__LEFT__T1" "the left MFD's T1 key did not get the left MFD's T1 function."
            Assert-Match $amPlan.Output "`"LDG GEAR UP`"\) => GearIn" "gear up did not follow the take-off panel's label."
            Assert-NotMatch $amPlan.Output "\(mfd\.(left|right)\.osb\.[TBLR]\d+\) => (?!MFD__)" "an MFD key got a function that is no MFD function."
        }
        if ($amSet -eq "mode-switch-small") {
            Assert-Match $amPlan.Output "phase layer\(s\) on a mode switch" "--phase-layers on made no phase layer on the X56 throttle's switch."
            Assert-Match $amPlan.Output "\[phase layer: Air to (air|ground)\]" "the plan does not name the phase of its layer."
        }
    }

    # Auto Assign with phase layers on a profile the plan made.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $amFixtures "mode-switch-small.tsv"
    $amProfile = Join-Path $amDir "mode-switch.pr0file"
    $amApply = Run-Engine @("--profile", $amProfile, "setup", "apply", "--plan", (Join-Path $amDir "mode-switch-small.json"), "--text")
    Assert-True ($amApply.ExitCode -eq 0) ("setup apply failed: " + $amApply.Output)
    $amAuto = Run-Engine @("--profile", $amProfile, "relations", "auto-assign", "--overwrite", "--no-style", "--phase-layers", "on", "--dry-run", "--text")
    Assert-True ($amAuto.ExitCode -eq 0) ("relations auto-assign --phase-layers on failed: " + $amAuto.Output)
    Assert-Match $amAuto.Output "JoyPro Mode A2(A|G) = X56 Rhino Throttle" "Auto Assign made no phase layer on the throttle's mode switch."
    $amBadPhase = Run-Engine @("--profile", $amProfile, "relations", "auto-assign", "--phase-layers", "sometimes", "--dry-run")
    Assert-True ($amBadPhase.ExitCode -ne 0) "relations auto-assign accepted --phase-layers sometimes."

    # Lay out with AI: understood phrases, a preview, apply in one history version, the preferences kept and used again.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $amFixtures "stick-throttle.tsv"
    $amLayoutProfile = Join-Path $amDir "layout.pr0file"
    $amProposal = Join-Path $amDir "proposal.json"
    $amAsk = Run-Engine @("--profile", $amLayoutProfile, "arrange", "ask", "trim on the hat, curves softer for helis, no modifiers", "--llm", "off", "--games", "DCS,StarCitizen", "--add-missing", "--proposal", $amProposal, "--text")
    Assert-True ($amAsk.ExitCode -eq 0) ("arrange ask failed: " + $amAsk.Output)
    Assert-Match $amAsk.Output "\[new\] Every aircraft: trim on the hat\." "arrange ask did not understand 'trim on the hat'."
    Assert-Match $amAsk.Output "\[new\] Helicopters: softer curves for pitch, roll \(curvature \+0\.15\)\." "arrange ask did not understand 'curves softer for helis'."
    Assert-Match $amAsk.Output "\[new\] No modifier buttons\." "arrange ask did not understand 'no modifiers'."
    Assert-Match $amAsk.Output "(?m)^  - tuning \| Pitch \[plane:DCS:UH-1H\]" "arrange ask made no softer pitch curve for the UH-1H."
    Assert-NotMatch $amAsk.Output "(?m)^  - tuning \| Pitch \[plane:DCS:F-16C_50\]" "arrange ask softened a jet's curve."
    Assert-NotMatch $amAsk.Output "\+ JoyPro Shift" "arrange ask used a modifier although the words said no modifiers."
    $amApplyLayout = Run-Engine @("--profile", $amLayoutProfile, "arrange", "apply", "--proposal", $amProposal, "--skip", "tuning:Pitch|plane:DCS:UH-1H", "--text")
    Assert-True ($amApplyLayout.ExitCode -eq 0) ("arrange apply failed: " + $amApplyLayout.Output)
    Assert-Match $amApplyLayout.Output "(?m)^Binds Set: [1-9]" "arrange apply set no bind."
    $amHistory = Run-Engine @("--profile", $amLayoutProfile, "profile", "history", "--text")
    Assert-Match $amHistory.Output "Lay out with AI: \d+ binds" "arrange apply made no history version of its own."
    $amTuning = Run-Engine @("--profile", $amLayoutProfile, "binds", "tuning", "show", "Pitch", "--text")
    Assert-Match $amTuning.Output "plane:DCS:Ka-50 III: .*cv=0\.15" "arrange apply did not set the helicopter curve."
    Assert-NotMatch $amTuning.Output "plane:DCS:UH-1H:" "arrange apply set a curve that was skipped."
    $amPrefs = Run-Engine @("--profile", $amLayoutProfile, "arrange", "prefs", "--text")
    Assert-Match $amPrefs.Output "(?m)^Saved preferences: 3" "arrange apply did not keep the three preferences."
    $amAgain = Run-Engine @("--profile", $amLayoutProfile, "arrange", "ask", "--llm", "off", "--games", "DCS,StarCitizen", "--text")
    Assert-Match $amAgain.Output "\[saved\] Every aircraft: trim on the hat\." "the kept preferences were not used again."
    Assert-Match $amAgain.Output "(?m)^Changes: 0 binds, 0 splits, 1 axis curves" "the kept preferences did not give the same layout again (only the skipped curve is left)."
    $amRemove = Run-Engine @("--profile", $amLayoutProfile, "arrange", "prefs", "remove", "modifiers", "--text")
    Assert-Match $amRemove.Output "(?m)^Saved preferences: 2" "arrange prefs remove did not forget the preference."
    $amEmpty = Run-Engine @("--profile", (Join-Path $amDir "empty.pr0file"), "arrange", "ask", "--llm", "off")
    Assert-True ($amEmpty.ExitCode -ne 0) "arrange ask without words and without kept preferences did not fail."
}
finally {
    $env:JOYPRO_DATA_DIR = $amPreviousData
    $env:JOYPRO_FAKE_DEVICES = $amPreviousDevices
}
