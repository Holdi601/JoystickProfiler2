# Detent mode for throttles (engine\AxisDetents.cs, EngineAxisDetents*.cs, docs/axis-curves.md "Detent mode"): the
# shipped gate data, binds detent merge (plan, apply, undo through the history) of two throttle relations on one device
# axis (one for aircraft with an afterburner, one with full power at the device's detent), the device calibration and
# its checks, own values per aircraft, what each game resolves (DCS user curve / dead zone + saturation, X-Plane curve
# points, IL-2 Great Battles full at the detent), the DCS export into a temp Saved Games folder (per-aircraft filters)
# and, when DCS is installed, DCS's own Lua loading every written file.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).

$dtDir = Join-Path $tempDir "detent"
New-Item -Path $dtDir -ItemType Directory -Force | Out-Null
$dtPreviousData = $env:JOYPRO_DATA_DIR
$dtPreviousInstalled = $env:JOYPRO_TEST_ASSUME_INSTALLED
$env:JOYPRO_DATA_DIR = Join-Path $dtDir "data"
$env:JOYPRO_TEST_ASSUME_INSTALLED = "1"

function Invoke-DtJson([string[]]$Arguments) {
    $out = Join-Path $dtDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    # The shipped gate data.
    $dtGates = Invoke-DtJson @("binds", "detent", "gates", "--game", "DCS")
    $dtF16 = @($dtGates.Aircraft | Where-Object { $_.Plane -eq "F-16C_50" })
    Assert-True ($dtF16.Count -eq 1 -and [Math]::Abs($dtF16[0].Afterburner - 0.775) -lt 1e-9 -and $dtF16[0].Confidence -eq "medium") "gate data: the F-16C should start its afterburner at 0.775 (medium)."
    Assert-True (@($dtGates.Aircraft | Where-Object { $_.Plane -eq "A-10C" -and $_.NoAfterburner }).Count -eq 1) "gate data: the A-10C has no afterburner."
    Assert-True (@($dtGates.Aircraft | Where-Object { $_.Source -match "[A-Z]:\\" }).Count -eq 0) "gate data must not carry absolute paths of this PC."

    $dtProfile = Join-Path $dtDir "detent.pr0file"
    $dtDevice = "Smoke Throttle {00000000-0000-0000-0000-0000000000D7}"
    $dtAdd = Run-Engine @("--profile", $dtProfile, "relations", "add", "Throttle", "--axis", "--node", "DCS:a2004cdnil:F-16C_50|M-2000C", "--node", "XPlane12:sim/joystick/throttle_ratio_all:XPlane12", "--node", "IL2Game:rpc_all_engines_throttles:IL2Game", "--device", $dtDevice, "--input", "JOY_RX", "--text")
    Assert-True ($dtAdd.ExitCode -eq 0) ("relations add Throttle failed: " + $dtAdd.Output)
    $dtAdd = Run-Engine @("--profile", $dtProfile, "relations", "add", "Throttle NoAB", "--axis", "--node", "DCS:a2004cdnil:A-10C", "--device", $dtDevice, "--input", "JOY_RX", "--text")
    Assert-True ($dtAdd.ExitCode -eq 0) ("relations add 'Throttle NoAB' failed: " + $dtAdd.Output)
    Run-Engine @("--profile", $dtProfile, "binds", "set", "Throttle", "--inverted", "true", "--slider", "true", "--text") | Out-Null
    $dtSet = Run-Engine @("--profile", $dtProfile, "binds", "set", "Throttle NoAB", "--inverted", "true", "--slider", "true", "--saturation-x", "0.8", "--text")
    Assert-True ($dtSet.ExitCode -eq 0) ("binds set for 'Throttle NoAB' failed: " + $dtSet.Output)

    # The plan: the device's detent read from the relation with full power at 80 %, nothing changes yet.
    $dtPlan = Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "merge")
    Assert-True (@($dtPlan.Proposals).Count -eq 1) "merge plan: one device axis with two throttle relations expected."
    $dtProposal = $dtPlan.Proposals[0]
    Assert-True ($dtProposal.Into -eq "Throttle") ("merge plan: the relation that stays should be 'Throttle', got " + $dtProposal.Into)
    Assert-True ($dtProposal.Calibration.Inferred -and [Math]::Abs($dtProposal.Calibration.Detent - 0.8) -lt 0.002) ("merge plan: the detent should be read as 0.8, got " + $dtProposal.Calibration.Detent)
    $dtA10 = @($dtProposal.Targets | Where-Object { $_.Plane -eq "A-10C" })
    Assert-True ($dtA10.Count -eq 1 -and $dtA10[0].Target -eq "full") "merge plan: the A-10C should get full power at the detent."
    $dtShowBefore = Run-Engine @("--profile", $dtProfile, "relations", "show", "Throttle NoAB", "--text")
    Assert-True ($dtShowBefore.ExitCode -eq 0) "the merge plan must not change the profile."

    # Apply, then the history takes it back and gives it again.
    $dtApply = Run-Engine @("--profile", $dtProfile, "binds", "detent", "merge", "--relations", "Throttle,Throttle NoAB", "--into", "Throttle", "--apply", "--text")
    Assert-True ($dtApply.ExitCode -eq 0) ("merge --apply failed: " + $dtApply.Output)
    $dtGone = Run-Engine @("--profile", $dtProfile, "relations", "show", "Throttle NoAB", "--text")
    Assert-True ($dtGone.ExitCode -ne 0) "after the merge 'Throttle NoAB' should be gone."
    $dtShow = Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "show", "Throttle")
    Assert-True ($dtShow.Settings.On -and $dtShow.Settings.NoAfterburner -eq "full") "after the merge detent mode should be on, no afterburner = full."
    Assert-True ($dtShow.Calibration.Source -eq "profile") "after the merge the read detent is stored as the calibration (source profile)."
    $dtUndo = Run-Engine @("--profile", $dtProfile, "profile", "history", "undo", "--text")
    Assert-True ($dtUndo.ExitCode -eq 0) ("history undo failed: " + $dtUndo.Output)
    Assert-True ((Run-Engine @("--profile", $dtProfile, "relations", "show", "Throttle NoAB", "--text")).ExitCode -eq 0) "undo did not bring 'Throttle NoAB' back."
    $dtRedo = Run-Engine @("--profile", $dtProfile, "profile", "history", "redo", "--text")
    Assert-True ($dtRedo.ExitCode -eq 0 -and (Run-Engine @("--profile", $dtProfile, "relations", "show", "Throttle NoAB", "--text")).ExitCode -ne 0) "redo did not merge again."

    # The calibration and its checks.
    $dtBad = Run-Engine @("--profile", $dtProfile, "binds", "detent", "calibrate", "Throttle", "--idle", "0.5", "--detent", "0.51", "--text")
    Assert-True ($dtBad.ExitCode -ne 0 -and $dtBad.Output -match "2 %") "a detent right at idle must be refused."
    $dtBad = Run-Engine @("--profile", $dtProfile, "binds", "detent", "calibrate", "Throttle", "--idle", "0.02", "--detent", "0.8", "--cutoff", "0.3", "--text")
    Assert-True ($dtBad.ExitCode -ne 0 -and $dtBad.Output -match "behind idle") "a cut-off in front of idle must be refused."
    $dtCal = Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "calibrate", "Throttle", "--idle", "0.02", "--detent", "0.75", "--source", "wizard")
    Assert-True ($dtCal.Calibration.Source -eq "wizard" -and [Math]::Abs($dtCal.Calibration.DetentTravel - 0.75) -lt 1e-9) "calibrate did not store the wizard's detent."

    # Groups: the merge kept what the old curve gave the F-16C at the detent (0.8 + margin) as its own value; back to
    # the game data's 0.775.
    $dtF16Group = @($dtCal.Groups | Where-Object { @($_.Aircraft | Where-Object { $_.Plane -eq "F-16C_50" }).Count -gt 0 })
    Assert-True ($dtF16Group.Count -eq 1 -and [Math]::Abs($dtF16Group[0].AbStart - 0.81) -lt 1e-6 -and $dtF16Group[0].Aircraft[0].Source -eq "relation") ("the merge should keep the F-16C's old afterburner start 0.81: " + ($dtF16Group | ConvertTo-Json -Depth 3 -Compress))
    $dtCal = Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--target", "DCS:F-16C_50=default", "--target", "DCS:M-2000C=default")
    $dtF16Group = @($dtCal.Groups | Where-Object { @($_.Aircraft | Where-Object { $_.Plane -eq "F-16C_50" }).Count -gt 0 })
    Assert-True ($dtF16Group.Count -eq 1 -and [Math]::Abs($dtF16Group[0].AbStart - 0.775) -lt 1e-9) "the F-16C group should use the game data's 0.775."
    $dtF16Fit = @($dtF16Group[0].Games | Where-Object { $_.Game -eq "DCS" })[0].Fit
    Assert-True ($dtF16Fit.Method -eq "user curve" -and $dtF16Fit.MaxError -lt 0.02) ("the F-16C's DCS curve: " + $dtF16Fit.Method + ", error " + $dtF16Fit.MaxError)
    $dtAtDetent = @($dtF16Fit.Throttle | Where-Object { [Math]::Abs($_[0] - 0.76) -lt 1e-6 })
    Assert-True ($dtAtDetent.Count -eq 1) "the throttle samples should have a point at 76 % of the travel."

    # Own value per aircraft, then back to the game data.
    $dtOwn = Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--target", "DCS:F-16C_50=ab:0.85")
    Assert-True (@($dtOwn.Groups | Where-Object { $_.Target -eq "ab:0.85" -and @($_.Aircraft | Where-Object { $_.Plane -eq "F-16C_50" -and $_.Source -eq "relation" }).Count -eq 1 }).Count -eq 1) "the F-16C's own afterburner start was not used."
    $dtBadTarget = Run-Engine @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--target", "DCS:F-16C_50=ab:2", "--text")
    Assert-True ($dtBadTarget.ExitCode -ne 0) "an afterburner start above 1 must be refused."

    # What the games resolve.
    $dtPreview = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "preview", "Throttle", "--game", "DCS", "--plane", "F-16C_50", "--points", "101")
    Assert-True ($dtPreview.Source -eq "detent") ("DCS / F-16C should resolve to detent mode, got " + $dtPreview.Source)
    # DCS thrust: -1 is full; the relation is inverted, so the game throttle is (1 - y) / 2. Raw 0.75 = the detent.
    $dtY = @($dtPreview.Points | Where-Object { [Math]::Abs($_[0] - 0.5) -lt 1e-6 })[0][1]
    Assert-True ([Math]::Abs((1 - $dtY) / 2 - 0.84) -lt 0.003) ("the F-16C's throttle at the detent should be 0.85 - margin 0.01, got " + ((1 - $dtY) / 2))
    $dtA10Preview = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "DCS", "--plane", "A-10C")
    Assert-True ($dtA10Preview.Resolved.Source -eq "detent" -and $dtA10Preview.Resolved.Tuning -match "sx=0\.75") ("the A-10C should get full power at the detent with saturation X 0.75: " + $dtA10Preview.Resolved.Tuning)
    Assert-True (-not ($dtA10Preview.Scopes.PSObject.Properties.Name -contains "detent")) "tuning show must not list the detent settings as a scope."
    $dtXp = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "XPlane12")
    Assert-True ($dtXp.Resolved.Source -eq "base") "X-Plane has no gate data: the unknown default (linear) keeps its normal curve."
    # IL-2 flew the relation's (linear) curve before the merge, so it keeps it; with the game data (no afterburner) it
    # gets full power at the detent: a high dead zone of 25 % of the travel.
    $dtIl2 = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "IL2Game")
    Assert-True ($dtIl2.Resolved.Source -eq "base") ("IL-2 should keep the relation's curve after the merge: " + $dtIl2.Resolved.Source)
    Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--target", "IL2Game:IL2Game=default") | Out-Null
    $dtIl2 = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "IL2Game")
    Assert-True ($dtIl2.Resolved.Source -eq "detent" -and $dtIl2.Resolved.Tuning -match "n\.(high|low)ValuesDeadZone=0\.25") ("IL-2 (no afterburner) should get full at the detent as a dead zone of 0.25: " + $dtIl2.Resolved.Tuning)
    Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--target", "XPlane12:XPlane12=ab:0.6") | Out-Null
    $dtXp = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "XPlane12")
    Assert-True ($dtXp.Resolved.Source -eq "detent" -and $dtXp.Resolved.Tuning -match "n\.cpN=") ("X-Plane with an afterburner start should get curve points: " + $dtXp.Resolved.Tuning)

    # The DCS export: per-aircraft filters in a temp Saved Games folder.
    $dtRoot = Join-Path $dtDir "saved-games"
    New-Item -ItemType Directory -Force -Path $dtRoot | Out-Null
    $dtExport = Run-Engine @("--profile", $dtProfile, "dcs", "export", $dtRoot, "--text")
    Assert-True ($dtExport.ExitCode -eq 0) ("dcs export failed: " + $dtExport.Output)
    $dtF16File = Get-ChildItem -Path (Join-Path $dtRoot "Config\Input\F-16C_50\joystick") -Filter "Smoke Throttle*.diff.lua" | Select-Object -First 1
    $dtA10File = Get-ChildItem -Path (Join-Path $dtRoot "Config\Input\A-10C\joystick") -Filter "Smoke Throttle*.diff.lua" | Select-Object -First 1
    Assert-True ($null -ne $dtF16File -and $null -ne $dtA10File) "the export did not write the throttle for the F-16C and the A-10C."
    $dtF16Text = [IO.File]::ReadAllText($dtF16File.FullName)
    $dtA10Text = [IO.File]::ReadAllText($dtA10File.FullName)
    Assert-True ($dtF16Text -match '\[11\]\s*=' -and $dtF16Text -match '"slider"\]\s*=\s*true') "the F-16C's filter should be an 11-point user curve on a slider."
    Assert-True ($dtA10Text -match '"saturationX"\]\s*=\s*0\.75') "the A-10C's filter should end at the detent (saturation X 0.75)."
    $dtLuae = "G:\Program Files\Eagle Dynamics\DCS World\bin-mt\luae.exe"
    if (Test-Path $dtLuae) {
        $dtList = Join-Path $dtDir "written.txt"
        @(Get-ChildItem -Path (Join-Path $dtRoot "Config\Input") -Recurse -Filter "*.lua" | ForEach-Object { $_.FullName.Replace("\", "/") }) | Set-Content -Path $dtList -Encoding ascii
        $dtScript = Join-Path $dtDir "load.lua"
        Set-Content -Path $dtScript -Encoding ascii -Value @(
            'local bad = 0',
            'for path in io.lines(arg[1]) do local f, err = loadfile(path) if not f then bad = bad + 1 print("ERR " .. path .. " " .. tostring(err)) end end',
            'print("FAILED " .. bad)')
        $dtLoad = (& $dtLuae $dtScript $dtList 2>&1 | Out-String)
        Assert-True ($dtLoad -match "FAILED 0") ("DCS's Lua could not load the written files: " + $dtLoad)
    }
    else {
        Write-Host "SKIP (DCS not installed here): DCS luae load test of the detent export"
    }

    # Off: every game is back on the relation's curve.
    Invoke-DtJson @("--profile", $dtProfile, "binds", "detent", "set", "Throttle", "--off") | Out-Null
    $dtOff = Invoke-DtJson @("--profile", $dtProfile, "binds", "tuning", "show", "Throttle", "--game", "DCS", "--plane", "F-16C_50")
    Assert-True ($dtOff.Resolved.Source -eq "base") "with detent mode off the F-16C should use the base again."
    Write-Host "Detent mode smoke checks passed."
}
finally {
    $env:JOYPRO_DATA_DIR = $dtPreviousData
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $dtPreviousInstalled
}
