# X-Plane 12 response curves (axis model "xplane"), control settings (ffb model "XPlane12") and the X-Plane 12 use codes;
# then X-Plane 11 (engine xplane11: its own catalog without the X-Plane 12 axes, its own settings model) and the
# install folders of both versions.
# Fixture: an X-Plane 12 style Joysticks.prf (fixtures\xplane\curves) with curves of every interpolation mode, a
# feather range, a curve on an unassigned axis and one on a view axis, calibration values and control sensitivity.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).

$xpcDir = Join-Path $tempDir "xplane-curves"
$xpcPrf = Join-Path $xpcDir "X-Plane Joystick Settings.prf"
$xpcProfile = Join-Path $tempDir "xplane-curves.pr0file"
New-Item -Path $xpcDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\xplane\curves\X-Plane Joystick Settings.prf") -Destination $xpcPrf -Force
$xpcOriginal = [System.IO.File]::ReadAllText($xpcPrf)
$xpcStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$xpcPedals = "Smoke Pedals {88888888-8888-8888-8888-888888888888}"
$xpcInv = [Globalization.CultureInfo]::InvariantCulture

function Get-XpcLine([string]$Text, [string]$Key) {
    $m = [regex]::Match($Text, "(?m)^" + [regex]::Escape($Key) + " (.*)$")
    if ($m.Success) { return $m.Groups[1].Value } else { return $null }
}

function Get-XpcCalibration([string]$Text) {
    return (([regex]::Matches($Text, "(?m)^_joy_(center|null|lo_raw_dat|hi_raw_dat|axis_is_calibrated_override|axis_wants_relaxed_calibration)\d+ .*$") | ForEach-Object { $_.Value }) -join "`n")
}

function Get-XpcPreview([string]$Relation, [string]$Game, [int]$Points) {
    $r = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "preview", $Relation, "--game", $Game, "--points", [string]$Points)
    Assert-True ($r.ExitCode -eq 0) ("binds tuning preview " + $Relation + " --game " + $Game + " failed.")
    $json = $r.Output.Substring($r.Output.IndexOf('{')) | ConvertFrom-Json
    return @($json.Points | ForEach-Object { [double]$_[1] })
}

$previousXpcFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\xplane\devices.tsv"
try {
    # Scan: every curve on a flight axis is read (stops, mode, fixed range).
    $xpcScan = Run-Engine @("xplane12", "scan", $xpcPrf, "--text")
    Assert-True ($xpcScan.ExitCode -eq 0) "xplane scan of the curves fixture failed."
    Assert-Match $xpcScan.Output "Response curve of Roll \(axis 0\): 3 stops, CATMULL_ROM\." "xplane scan should read the CATMULL_ROM roll curve."
    Assert-Match $xpcScan.Output "Response curve of Nosewheel tiller \(axis 28\): 4 stops, CUBIC_AVG\." "xplane scan should read the CUBIC_AVG tiller curve with its null zone."
    Assert-Match $xpcScan.Output "Response curve of Prop \(axis 26\): 2 stops, LINEAR, feather_range\." "xplane scan should read the feather range of the prop curve."

    # Import: the curves become the relations' X-Plane tuning; the round trip leaves the file byte-identical.
    $xpcImport = Run-Engine @("--profile", $xpcProfile, "xplane12", "import", $xpcPrf, "--text")
    Assert-True ($xpcImport.ExitCode -eq 0) "xplane import of the curves fixture failed."
    $xpcRollShow = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "show", "X-Plane 12 Roll", "--text")
    Assert-Match $xpcRollShow.Output "game:XPlane12: .*n\.cpN=3;.*n\.cpx1=0\.403005;.*n\.cpy1=0\.299401;.*n\.interp=1" "xplane import should store the roll curve's stops and CATMULL_ROM as game:XPlane tuning."
    $xpcTillerShow = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "show", "X-Plane 12 Nosewheel tiller", "--text")
    Assert-Match $xpcTillerShow.Output "game:XPlane12: .*n\.cpx1=0\.159836;.*n\.interp=2" "xplane import should store the tiller's CUBIC_AVG curve."
    $xpcPropShow = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "show", "X-Plane 12 Prop", "--text")
    Assert-Match $xpcPropShow.Output "game:XPlane12: inv=0;sl=1;.*n\.rangePt0=5;n\.rangePt1=10;n\.rangePt2=15;n\.rangeType=2" "xplane import should store the prop's feather range (one-sided axis)."
    $xpcPitchShow = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "show", "X-Plane 12 Pitch", "--text")
    Assert-NotMatch $xpcPitchShow.Output "game:XPlane12" "xplane import must not store a tuning for an axis without a curve."
    $xpcRoundTrip = Run-Engine @("--profile", $xpcProfile, "xplane12", "export", $xpcPrf, "--text")
    Assert-True ($xpcRoundTrip.ExitCode -eq 0) "xplane export after the curves import failed."
    Assert-True ([System.IO.File]::ReadAllText($xpcPrf) -eq $xpcOriginal) "xplane curves import/export round trip must leave the preferences byte-identical."
    $xpcOwnership = [System.IO.File]::ReadAllText($xpcPrf + ".joypro.json")
    Assert-Match $xpcOwnership '"curve:0"' "xplane import should take over the imported roll curve."
    Assert-NotMatch $xpcOwnership '"curve:29"' "xplane import must not take over the curve of a view axis."

    # Preview = X-Plane's interpolation: the stops themselves, CATMULL_ROM through them, symmetric on centred axes.
    $xpcRollPreview = Get-XpcPreview "X-Plane 12 Roll" "XPlane12" 201
    Assert-True ([Math]::Abs($xpcRollPreview[200] - 1.0) -lt 1e-9 -and [Math]::Abs($xpcRollPreview[100]) -lt 1e-9) "xplane roll preview must run from 0 at the centre to 1."
    Assert-True ([Math]::Abs($xpcRollPreview[60] + $xpcRollPreview[140]) -lt 1e-9) "xplane roll preview must be symmetric on a centred axis."
    $xpcTillerPreview = Get-XpcPreview "X-Plane 12 Nosewheel tiller" "XPlane12" 201
    Assert-True ([Math]::Abs($xpcTillerPreview[150] - 0.25) -lt 1e-9) "xplane tiller preview must pass through its stop (0.5, 0.25)."

    # The spline modes against values measured in X-Plane 12.4 (tools\e2e\games\XPlaneCurves.ps1, 2026-10-05).
    $null = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Pitch", "--scope", "game:XPlane12", "--curve-points", "0:0,0.12:0.57,0.4:0.74,0.52:0.79,1:1", "--param", "interp=1", "--text")
    $xpcCatmull = Get-XpcPreview "X-Plane 12 Pitch" "XPlane12" 21
    Assert-True ([Math]::Abs($xpcCatmull[11] - 0.491594) -lt 1e-4 -and [Math]::Abs($xpcCatmull[15] - 0.773788) -lt 1e-4) "xplane CATMULL_ROM preview must match X-Plane's measured values."
    $null = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Pitch", "--scope", "game:XPlane12", "--curve-points", "0:0,0.33:0.1,0.66:0.29,0.95:0.87,1:1", "--param", "interp=2", "--text")
    $xpcCubic = Get-XpcPreview "X-Plane 12 Pitch" "XPlane12" 21
    Assert-True ([Math]::Abs($xpcCubic[11] - 0.012705) -lt 1e-4 -and [Math]::Abs($xpcCubic[15] - 0.162185) -lt 1e-4) "xplane CUBIC_AVG preview must match X-Plane's measured values."

    # An explicit curve set with --curve-points is written on the axis's index (pitch = stick Y = axis 1), LINEAR exact.
    $xpcSetPitch = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Pitch", "--scope", "game:XPlane12", "--curve-points", "0:0,0.5:0.2,1:1", "--param", "interp=0", "--text")
    Assert-True ($xpcSetPitch.ExitCode -eq 0) "binds tuning set --curve-points for X-Plane pitch failed."
    $xpcPitchPreview = Get-XpcPreview "X-Plane 12 Pitch" "XPlane12" 5
    Assert-True ([Math]::Abs($xpcPitchPreview[3] - 0.2) -lt 1e-9 -and [Math]::Abs($xpcPitchPreview[1] + 0.2) -lt 1e-9) "xplane LINEAR preview must hit the stop 0.5 -> 0.2 on both sides."
    # Roll becomes CUBIC_AVG (the stops stay); prop CATMULL_ROM with its feather range kept.
    $null = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Roll", "--scope", "game:XPlane12", "--param", "interp=2", "--text")
    $null = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Prop", "--scope", "game:XPlane12", "--param", "interp=1", "--text")
    # Yaw has no X-Plane tuning: the base (dead zone 0.1, curvature 0.3) is fitted as LINEAR stops.
    $xpcYawBase = Run-Engine @("--profile", $xpcProfile, "binds", "set", "X-Plane 12 Yaw", "--deadzone", "0.1", "--curvature", "0.3", "--text")
    Assert-True ($xpcYawBase.ExitCode -eq 0) "binds set of the X-Plane yaw base failed."
    $xpcYawXp = Get-XpcPreview "X-Plane 12 Yaw" "XPlane12" 201
    $xpcYawDcs = Get-XpcPreview "X-Plane 12 Yaw" "DCS" 201
    $xpcYawErr = 0.0
    for ($i = 0; $i -lt 201; $i++) { $xpcYawErr = [Math]::Max($xpcYawErr, [Math]::Abs($xpcYawXp[$i] - $xpcYawDcs[$i])) }
    Assert-True ($xpcYawErr -le 0.0015) ("xplane fit of the base curve must stay within 0.0015 of DCS's curve (max difference " + $xpcYawErr.ToString("0.000000", $xpcInv) + ").")
    # A relation on the new copilot pitch code (68) on the stick's RZ axis (axis 2), which has a curve made in X-Plane.
    $xpcCopilot = Run-Engine @("--profile", $xpcProfile, "relations", "add", "XP Copilot pitch", "--axis", "--node", "XPlane12:axis/copilot_pitch", "--device", $xpcStick, "--input", "JOY_RZ", "--text")
    Assert-True ($xpcCopilot.ExitCode -eq 0) "relations add for X-Plane copilot pitch failed."
    # A fixed range that does not belong to the axis (mixture cutoff on the throttle) is not written.
    $null = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Throttle", "--scope", "game:XPlane12", "--param", "rangeType=3", "--param", "rangePt0=10", "--text")
    # Control settings: control response pitch / copilot roll, control-loaded pedals. Stability augmentation is X-Plane 11 only.
    $xpcFfbStability = Run-Engine @("--profile", $xpcProfile, "ffb", "set", "--device", "*", "--game", "XPlane12", "--param", "pitchStability=0.5", "--text")
    Assert-True ($xpcFfbStability.ExitCode -ne 0) "ffb set must refuse the X-Plane 11 stability augmentation for X-Plane 12."
    $xpcFfb = Run-Engine @("--profile", $xpcProfile, "ffb", "set", "--device", "*", "--game", "XPlane12", "--param", "pitchResponse=0.25", "--param", "copilotRollResponse=0.75", "--param", "discretizeFlaps=true", "--text")
    Assert-True ($xpcFfb.ExitCode -eq 0) "ffb set of the X-Plane game settings failed."
    $xpcFfbDevice = Run-Engine @("--profile", $xpcProfile, "ffb", "set", "--device", $xpcPedals, "--game", "XPlane12", "--param", "controlLoaded=true", "--text")
    Assert-True ($xpcFfbDevice.ExitCode -eq 0) "ffb set of the X-Plane control-loaded flag failed."

    $xpcExport = Run-Engine @("--profile", $xpcProfile, "xplane12", "export", $xpcPrf, "--text")
    Assert-True ($xpcExport.ExitCode -eq 0) "xplane export with curves and settings failed."
    $xpcAfter = [System.IO.File]::ReadAllText($xpcPrf)
    Assert-True ((Get-XpcLine $xpcAfter "axis_response_curve1") -eq "INTERP_MODE LINEAR | 0.000000 0.000000  | 0.500000 0.200000  | 1.000000 1.000000 ") "xplane export should write the pitch curve on axis 1 in X-Plane's format."
    Assert-True ((Get-XpcLine $xpcAfter "axis_response_curve0") -eq "INTERP_MODE CUBIC_AVG | 0.000000 0.000000  | 0.403005 0.299401  | 1.000000 1.000000 ") "xplane export should change only the roll curve's mode."
    Assert-True ((Get-XpcLine $xpcAfter "axis_response_curve26") -eq "FIXED_RANGE_TYPE feather_range FIXED_PT_0 5 FIXED_PT_1 10 FIXED_PT_2 15 ; INTERP_MODE CATMULL_ROM | 0.000000 0.000000  | 1.000000 1.000000 ") "xplane export must keep the prop's feather range when its curve changes."
    Assert-Match (Get-XpcLine $xpcAfter "axis_response_curve50") "^INTERP_MODE LINEAR \| 0\.000000 0\.000000  \| 0\.100000 0\.000000  \| " "xplane export should write the fitted yaw curve with the dead zone as a flat first stop."
    Assert-True ((Get-XpcLine $xpcAfter "axis_response_curve2") -eq (Get-XpcLine $xpcOriginal "axis_response_curve2")) "xplane export must keep a curve made in X-Plane when JoyPro's tuning is linear."
    Assert-Match $xpcExport.Output "XP Copilot pitch.*response curve made in X-Plane; JoyPro's tuning is linear, so it stays" "xplane export should say why it keeps the curve made in X-Plane."
    Assert-Match $xpcAfter "(?m)^_joy_AXIS_use2 68$" "xplane export should write the copilot pitch use code 68."
    Assert-True ((Get-XpcLine $xpcAfter "axis_response_curve29") -eq (Get-XpcLine $xpcOriginal "axis_response_curve29")) "xplane export must keep the curve of a view axis."
    Assert-Match $xpcExport.Output "fixed range mixture_cutoff_range does not belong to this axis" "xplane export should refuse a fixed range on the wrong kind of axis."
    Assert-True ($null -eq (Get-XpcLine $xpcAfter "axis_response_curve25")) "xplane export must not write a throttle curve for a wrong fixed range alone."
    Assert-Match $xpcAfter "(?m)^_joy_prat1 0\.250000$" "xplane export should write the pitch control response."
    Assert-Match $xpcAfter "(?m)^_joy_prat69 0\.750000$" "xplane export should write the copilot roll control response (use 69)."
    Assert-Match $xpcAfter "(?m)^_joy_prat2 1\.000000$" "xplane export must keep control responses JoyPro does not set."
    Assert-Match $xpcAfter "(?m)^_discretize_flap_axis 1$" "xplane export should write the flap detent option."
    Assert-NotMatch $xpcAfter "(?m)^_joy_damp" "xplane export must not add _joy_damp lines."
    Assert-Match $xpcAfter "(?m)^_joy_ffb_axis50 1$" "xplane export should mark the control-loaded pedals' yaw axis."
    Assert-Match $xpcAfter "(?m)^_joy_ffb_axis0 0$" "xplane export must leave the stick's axes not control-loaded."
    Assert-True ((Get-XpcCalibration $xpcAfter) -eq (Get-XpcCalibration $xpcOriginal)) "xplane export must never change calibration (centre, null zone, raw range)."
    Assert-True (Test-Path ($xpcPrf + ".jpbak")) "xplane curves export should back up the preferences."

    # A curve changed in X-Plane: merge keeps it, merge-overwrite replaces it.
    $xpcUserCurve = "INTERP_MODE LINEAR | 0.000000 0.000000  | 0.700000 0.300000  | 1.000000 1.000000 "
    [System.IO.File]::WriteAllText($xpcPrf, ($xpcAfter -replace "(?m)^axis_response_curve0 .*$", ("axis_response_curve0 " + $xpcUserCurve)))
    $xpcMerge = Run-Engine @("--profile", $xpcProfile, "xplane12", "export", $xpcPrf, "--mode", "merge", "--text")
    Assert-True ($xpcMerge.ExitCode -eq 0) "xplane merge export failed."
    Assert-Match $xpcMerge.Output "X-Plane 12 Roll.*response curve made in X-Plane; merge keeps it" "xplane merge must keep a curve changed in X-Plane."
    Assert-True ((Get-XpcLine ([System.IO.File]::ReadAllText($xpcPrf)) "axis_response_curve0") -eq $xpcUserCurve) "xplane merge must leave the changed curve as it is."
    $xpcOverwrite = Run-Engine @("--profile", $xpcProfile, "xplane12", "export", $xpcPrf, "--text")
    Assert-Match $xpcOverwrite.Output "X-Plane 12 Roll.*replaced the response curve made in X-Plane" "xplane merge-overwrite should report the replaced curve."
    Assert-Match (Get-XpcLine ([System.IO.File]::ReadAllText($xpcPrf)) "axis_response_curve0") "^INTERP_MODE CUBIC_AVG " "xplane merge-overwrite should write JoyPro's roll curve."

    # Linear again: JoyPro's own curve is removed (the pitch curve JoyPro wrote).
    $xpcLinear = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Pitch", "--scope", "game:XPlane12", "--curve-points", "none", "--text")
    Assert-True ($xpcLinear.ExitCode -eq 0) "binds tuning set --curve-points none failed."
    $xpcLinearExport = Run-Engine @("--profile", $xpcProfile, "xplane12", "export", $xpcPrf, "--text")
    Assert-True ($xpcLinearExport.ExitCode -eq 0) "xplane export with a linear pitch failed."
    $xpcFinal = [System.IO.File]::ReadAllText($xpcPrf)
    Assert-True ($null -eq (Get-XpcLine $xpcFinal "axis_response_curve1")) "xplane export should remove JoyPro's own curve when the tuning is linear."
    Assert-True ((Get-XpcLine $xpcFinal "axis_response_curve2") -eq (Get-XpcLine $xpcOriginal "axis_response_curve2")) "xplane export must still keep the curve made in X-Plane."

    # Editing the dead zone of an imported curve replaces its stops (the GUI's fields define the curve then).
    $xpcTillerEdit = Run-Engine @("--profile", $xpcProfile, "binds", "tuning", "set", "X-Plane 12 Nosewheel tiller", "--scope", "game:XPlane12", "--deadzone", "0.2", "--text")
    Assert-True ($xpcTillerEdit.ExitCode -eq 0) "binds tuning set --deadzone for the X-Plane tiller failed."
    Assert-NotMatch $xpcTillerEdit.Output "n\.cpN=" "a changed dead zone must replace the imported stops."
    $xpcTillerAfter = Get-XpcPreview "X-Plane 12 Nosewheel tiller" "XPlane12" 11
    Assert-True ([Math]::Abs($xpcTillerAfter[6]) -lt 1e-9 -and $xpcTillerAfter[10] -gt 0.99) "the tiller's new curve must start with the 0.2 dead zone."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousXpcFakeDevices
}

# X-Plane 11 (engine xplane11): the same Joysticks.prf format, but its own catalog (axis use codes up to 66, its own
# Commands.txt) and settings model (stability augmentation, no copilot or control-loaded axes). The copy of the curves
# fixture gets an X-Plane 12 use code (68, copilot pitch) on the stick's RZ axis and X-Plane 11's _joy_damp1 line.
$xp11Dir = Join-Path $tempDir "xplane11"
$xp11Prf = Join-Path $xp11Dir "X-Plane Joystick Settings.prf"
$xp11Profile = Join-Path $tempDir "xplane11.pr0file"
New-Item -Path $xp11Dir -ItemType Directory -Force | Out-Null
$xp11Text = ([System.IO.File]::ReadAllText((Join-Path $engineDir "tests\fixtures\xplane\curves\X-Plane Joystick Settings.prf")) -replace "(?m)^_joy_AXIS_use2 0(\r?)$", "_joy_AXIS_use2 68`$1") + "_joy_damp1 0.000000`n"
[System.IO.File]::WriteAllText($xp11Prf, $xp11Text)
$previousXp11FakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\xplane\devices.tsv"
try {
    $xp11Scan = Run-Engine @("xplane11", "scan", $xp11Prf, "--text")
    Assert-True ($xp11Scan.ExitCode -eq 0) "xplane11 scan failed."
    Assert-Match $xp11Scan.Output "axis/roll <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_X" "xplane11 scan should read the roll axis."
    Assert-NotMatch $xp11Scan.Output "axis/copilot_pitch" "xplane11 scan must not read an X-Plane 12 axis use (copilot pitch)."
    $xp11Import = Run-Engine @("--profile", $xp11Profile, "xplane11", "import", $xp11Prf, "--text")
    Assert-True ($xp11Import.ExitCode -eq 0) "xplane11 import failed."
    $xp11Roll = Run-Engine @("--profile", $xp11Profile, "binds", "tuning", "show", "X-Plane 11 Roll", "--text")
    Assert-Match $xp11Roll.Output "game:XPlane11: .*n\.cpN=3;.*n\.interp=1" "xplane11 import should store the roll curve as game:XPlane11 tuning."
    $xp11Copilot = Run-Engine @("--profile", $xp11Profile, "relations", "add", "XP11 Copilot pitch", "--axis", "--node", "XPlane11:axis/copilot_pitch", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_Y", "--text")
    Assert-True ($xp11Copilot.ExitCode -eq 0) "relations add for an X-Plane 11 copilot pitch node failed."
    $xp11Stability = Run-Engine @("--profile", $xp11Profile, "ffb", "set", "--device", "*", "--game", "XPlane11", "--param", "pitchStability=0.5", "--param", "pitchResponse=0.3", "--text")
    Assert-True ($xp11Stability.ExitCode -eq 0) "ffb set of the X-Plane 11 stability augmentation failed."
    $xp11Loaded = Run-Engine @("--profile", $xp11Profile, "ffb", "set", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--game", "XPlane11", "--param", "controlLoaded=true", "--text")
    Assert-True ($xp11Loaded.ExitCode -ne 0) "ffb set must refuse X-Plane 12's control-loaded flag for X-Plane 11."
    $xp11Export = Run-Engine @("--profile", $xp11Profile, "xplane11", "export", $xp11Prf, "--text")
    Assert-True ($xp11Export.ExitCode -eq 0) "xplane11 export failed."
    Assert-Match $xp11Export.Output "XP11 Copilot pitch.*'axis/copilot_pitch' is not a X-Plane 11 flight action" "xplane11 export should skip an X-Plane 12 axis."
    $xp11After = [System.IO.File]::ReadAllText($xp11Prf)
    Assert-Match $xp11After "(?m)^_joy_damp1 0\.500000\r?$" "xplane11 export should write the stability augmentation."
    Assert-Match $xp11After "(?m)^_joy_prat1 0\.300000\r?$" "xplane11 export should write the pitch control response."
    Assert-Match $xp11After "(?m)^_joy_AXIS_use2 68\r?$" "xplane11 export must leave an axis use it does not know as it is."
    Assert-Match $xp11After "(?m)^_joy_AXIS_use1 1\r?$" "xplane11 export must keep the pitch axis."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousXp11FakeDevices
}

# Installs of both versions side by side: each game finds the preferences of its own folder, and a folder set for the
# single X-Plane game of older JoyPro versions ("XPlane" in game-installs.json) goes to the version it shows.
$xpiRoot = Join-Path $tempDir "xplane-installs"
$xpiPreviousData = $env:JOYPRO_DATA_DIR
$xpiPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $xpiRoot "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
try {
    foreach ($xpiVersion in @("11", "12")) {
        $xpiPrefs = Join-Path $xpiRoot ("X-Plane " + $xpiVersion + "\Output\preferences")
        New-Item -Path $xpiPrefs -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $xpiRoot ("X-Plane " + $xpiVersion + "\X-Plane.exe")), "")
        Copy-Item -Path (Join-Path $engineDir "tests\fixtures\xplane\X-Plane Joystick Settings.prf") -Destination (Join-Path $xpiPrefs "X-Plane Joystick Settings.prf") -Force
    }
    $xpiSet11 = Run-Engine @("games", "install", "set", "XPlane11", (Join-Path $xpiRoot "X-Plane 11"), "--text")
    Assert-True ($xpiSet11.ExitCode -eq 0) "games install set XPlane11 failed."
    $xpiSet12 = Run-Engine @("games", "install", "set", "xplane12", (Join-Path $xpiRoot "X-Plane 12"), "--text")
    Assert-True ($xpiSet12.ExitCode -eq 0) "games install set xplane12 failed."
    $xpiPaths11 = Run-Engine @("xplane11", "paths", "--text")
    Assert-Match $xpiPaths11.Output ([regex]::Escape((Join-Path $xpiRoot "X-Plane 11\Output\preferences\X-Plane Joystick Settings.prf"))) "xplane11 paths should list X-Plane 11's preferences."
    Assert-NotMatch $xpiPaths11.Output "X-Plane 12" "xplane11 paths must not list X-Plane 12's preferences."
    $xpiPaths12 = Run-Engine @("xplane", "paths", "--text")
    Assert-Match $xpiPaths12.Output ([regex]::Escape((Join-Path $xpiRoot "X-Plane 12\Output\preferences\X-Plane Joystick Settings.prf"))) "xplane (X-Plane 12) paths should list X-Plane 12's preferences."
    Assert-NotMatch $xpiPaths12.Output "X-Plane 11" "xplane12 paths must not list X-Plane 11's preferences."
    $xpiList = Run-Engine @("games", "installs", "--text")
    Assert-Match $xpiList.Output "XPlane12 \(X-Plane 12\): installed" "games installs should list X-Plane 12."
    Assert-Match $xpiList.Output "XPlane11 \(X-Plane 11\): installed" "games installs should list X-Plane 11."
    Assert-Match $xpiList.Output "MSFS2024 \(Microsoft Flight Simulator 2024\)" "games installs should list MSFS 2024."
    Assert-Match $xpiList.Output "MSFS2020 \(Microsoft Flight Simulator 2020\)" "games installs should list MSFS 2020."
    Assert-Match $xpiList.Output "HLLVietnam \(Hell Let Loose: Vietnam\)" "games installs should list Hell Let Loose: Vietnam (back as a virtual Xbox pad game)."

    # Settings of the single X-Plane / MSFS game (older JoyPro): the folder belongs to the version it shows, and
    # "I do not have it" counts for every version.
    [System.IO.File]::WriteAllText((Join-Path $env:JOYPRO_DATA_DIR "game-installs.json"), "{`r`n  `"XPlane`": `"" + (Join-Path $xpiRoot "X-Plane 11").Replace("\", "\\") + "`"`r`n}`r`n")
    [System.IO.File]::WriteAllText((Join-Path $env:JOYPRO_DATA_DIR "games-disabled.json"), "[`r`n  `"MSFS`"`r`n]`r`n")
    $xpiLegacy = Run-Engine @("games", "installs", "--text")
    Assert-Match $xpiLegacy.Output ("XPlane11 \(X-Plane 11\): installed - " + [regex]::Escape((Join-Path $xpiRoot "X-Plane 11")) + " \[override\]") "an old X-Plane folder setting should go to X-Plane 11 (its folder)."
    Assert-Match $xpiLegacy.Output "MSFS2024 \(Microsoft Flight Simulator 2024\): you do not have it" "an old 'I do not have MSFS' should hide MSFS 2024."
    Assert-Match $xpiLegacy.Output "MSFS2020 \(Microsoft Flight Simulator 2020\): you do not have it" "an old 'I do not have MSFS' should hide MSFS 2020."
}
finally {
    $env:JOYPRO_DATA_DIR = $xpiPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $xpiPreviousDetection
}
