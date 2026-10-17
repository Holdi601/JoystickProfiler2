# Falcon BMS axis response (bms model) and game settings (ffb model FalconBMS). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir, $engineDir). Fixtures: the launcher-layout device files of fixtures\bms\Config plus
# fixtures\bms\settings: the stock Falcon BMS User.cfg with a user line and a launcher override block, the stock
# Feedback.ini, and the launcher's new-pilot .pop (468 bytes).

$bmsSetConfig = Join-Path $tempDir "bms-settings\\User\\Config"
New-Item -Path $bmsSetConfig -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\bms\\Config\\*") -Destination $bmsSetConfig -Force
Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\bms\\settings\\*") -Destination $bmsSetConfig -Force
$bmsSetProfile = Join-Path $tempDir "bms-settings.pr0file"
$bmsSetCfg = Join-Path $bmsSetConfig "Falcon BMS User.cfg"
$bmsSetIni = Join-Path $bmsSetConfig "Feedback.ini"
$bmsSetPop = Join-Path $bmsSetConfig "Smoke.pop"
$bmsSetStick = Join-Path $bmsSetConfig "Setup.v100.Smoke Stick {11111111-2222-3333-4444-555555555555}.xml"
$bmsSetThrottle = Join-Path $bmsSetConfig "Setup.v100.Smoke Throttle {66666666-7777-8888-9999-000000000000}.xml"
$bmsSetThrottleDevice = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
$bmsSetIniBefore = [System.IO.File]::ReadAllText($bmsSetIni)
$bmsSetPopBefore = [System.IO.File]::ReadAllBytes($bmsSetPop)
$previousBmsSetFake = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
try {
    $bmsModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $bmsModels.Output "bms" "binds tuning models should list the Falcon BMS model."
    $bmsFfbModels = Run-Engine @("ffb", "models", "--text")
    Assert-Match $bmsFfbModels.Output "FalconBMS - Falcon BMS" "ffb models should list the Falcon BMS settings."

    # Import: launcher steps, one-sided throttle, User.cfg's exponential curve of roll (the line above the marker).
    $bmsSetImport = Run-Engine @("--profile", $bmsSetProfile, "bms", "import", $bmsSetConfig, "--text")
    Assert-True ($bmsSetImport.ExitCode -eq 0) ("bms import with settings failed: " + $bmsSetImport.Output)
    $bmsPitchShow = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "show", "Falcon BMS Pitch", "--game", "FalconBMS", "--text")
    Assert-Match $bmsPitchShow.Output "game:FalconBMS: .*n\.deadzoneStep=1;n\.saturationStep=1" ("bms import should store the launcher steps as the relation's BMS tuning: " + $bmsPitchShow.Output)
    Assert-Match $bmsPitchShow.Output "base: inv=0;sl=0;dz=0;sx=1" "bms import must not put BMS's steps into the base tuning (DCS)."
    $bmsRollShow = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "show", "Falcon BMS Roll", "--game", "FalconBMS", "--text")
    Assert-Match $bmsRollShow.Output "game:FalconBMS: .*n\.axisExp=20" ("bms import should read g_nAxisExp_AXIS_ROLL from Falcon BMS User.cfg: " + $bmsRollShow.Output)
    $bmsThrottleShow = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "show", "Falcon BMS Throttle", "--game", "FalconBMS", "--text")
    Assert-Match $bmsThrottleShow.Output "game:FalconBMS: inv=1;sl=1;.*n\.saturationStep=2" ("bms import should read the throttle as a one-sided, inverted axis with its saturation step: " + $bmsThrottleShow.Output)

    # Preview: dead zone/saturation ints of the half travel (DirectInput), exponent as linear-to-cubic blend.
    $bmsPitchPreview = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "preview", "Falcon BMS Pitch", "--game", "FalconBMS", "--points", "5", "--text")
    Assert-Match $bmsPitchPreview.Output "Model: bms" "the BMS preview should use the bms model."
    Assert-Match $bmsPitchPreview.Output "\+0\.500 -> \+0\.521277" ("bms preview: Small/Small = 0.01 dead zone, full at 0.95: " + $bmsPitchPreview.Output)
    $bmsRollPreview = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "preview", "Falcon BMS Roll", "--game", "FalconBMS", "--points", "5", "--text")
    Assert-Match $bmsRollPreview.Output "-0\.500 -> -0\.425000" ("bms preview: exponent 20 = 0.8 x + 0.2 x^3: " + $bmsRollPreview.Output)

    # The round trip writes nothing.
    $bmsCfgBefore = [System.IO.File]::ReadAllText($bmsSetCfg)
    $bmsStickBefore = [System.IO.File]::ReadAllText($bmsSetStick)
    $bmsSetRound = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--text")
    Assert-True ($bmsSetRound.ExitCode -eq 0) ("bms export after the settings import failed: " + $bmsSetRound.Output)
    Assert-True (([System.IO.File]::ReadAllText($bmsSetCfg) -eq $bmsCfgBefore) -and ([System.IO.File]::ReadAllText($bmsSetStick) -eq $bmsStickBefore)) "bms import/export round trip must leave User.cfg and the device files as they were."

    # Per-axis values and game settings.
    $bmsSetPitch = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "set", "Falcon BMS Pitch", "--scope", "game:FalconBMS", "--param", "deadzoneStep=2", "--param", "saturationStep=3", "--param", "axisExp=35", "--param", "axisSat=10", "--text")
    Assert-True ($bmsSetPitch.ExitCode -eq 0) ("binds tuning set for BMS pitch failed: " + $bmsSetPitch.Output)
    $bmsPitchPreview2 = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "preview", "Falcon BMS Pitch", "--game", "FalconBMS", "--points", "5", "--text")
    # u = 0.5: (0.5 - 0.05) / (0.85 - 0.05) = 0.5625; / 0.9 = 0.625; 0.65 * 0.625 + 0.35 * 0.625^3 = 0.491699
    Assert-Match $bmsPitchPreview2.Output "\+0\.500 -> \+0\.491699" ("bms preview with every pitch setting: " + $bmsPitchPreview2.Output)
    $bmsYawBase = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "set", "Falcon BMS Yaw", "--scope", "base", "--curvature", "0.3", "--deadzone", "0.05", "--text")
    Assert-True ($bmsYawBase.ExitCode -eq 0) "binds tuning set for the yaw base failed."
    $bmsYawPreview = Run-Engine @("--profile", $bmsSetProfile, "binds", "tuning", "preview", "Falcon BMS Yaw", "--game", "FalconBMS", "--points", "5", "--text")
    Assert-Match $bmsYawPreview.Output "from base \(fitted to the base curve\)" "BMS yaw without its own tuning should be fitted to the base curve."
    # Steps from the base; the base curvature is not turned into g_nAxisExp until its formula is measured in game.
    Assert-Match $bmsYawPreview.Output "n\.deadzoneStep=2;n\.saturationStep=0" ("the fit should take the nearest launcher steps of the base: " + $bmsYawPreview.Output)
    Assert-NotMatch $bmsYawPreview.Output "n\.axisExp" "the fit must not set an exponent (unverified formula)."
    $bmsFfbSet = Run-Engine @("--profile", $bmsSetProfile, "ffb", "set", "--device", "*", "--game", "FalconBMS", "--param", "ffbEnabled=true", "--param", "flightControlsDeadband=0", "--param", "ffbDeadBand=0", "--param", "autoCenterGain=6000", "--param", "autoCenterStrength=7000", "--param", "stallGain=2500", "--param", "analogIdleCutoff=true", "--text")
    Assert-True ($bmsFfbSet.ExitCode -eq 0) ("ffb set for BMS failed: " + $bmsFfbSet.Output)
    $bmsFfbDevice = Run-Engine @("--profile", $bmsSetProfile, "ffb", "set", "--device", $bmsSetThrottleDevice, "--game", "FalconBMS", "--param", "abDetent=80", "--param", "idleDetent=5", "--text")
    Assert-True ($bmsFfbDevice.ExitCode -eq 0) ("ffb set of the BMS throttle detents failed: " + $bmsFfbDevice.Output)
    $bmsFfbWrongScope = Run-Engine @("--profile", $bmsSetProfile, "ffb", "set", "--device", "*", "--game", "FalconBMS", "--param", "abDetent=80", "--text")
    Assert-True ($bmsFfbWrongScope.ExitCode -ne 0) "the AB detent is per device; ffb set --device * must refuse it."

    # Refused while the launcher runs (a copy of ping.exe named like it).
    $bmsFakeDir = Join-Path $tempDir "bms-proc"
    New-Item -Path $bmsFakeDir -ItemType Directory -Force | Out-Null
    $bmsFakeExe = Join-Path $bmsFakeDir "FalconBMS_Alternative_Launcher.exe"
    Copy-Item -Path (Join-Path $env:SystemRoot "System32\\PING.EXE") -Destination $bmsFakeExe -Force
    $bmsFake = Start-Process -FilePath $bmsFakeExe -ArgumentList @("-n", "60", "127.0.0.1") -WindowStyle Hidden -PassThru
    $previousBmsIgnore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
    try {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $null
        $bmsRunning = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--text")
        Assert-True ($bmsRunning.ExitCode -ne 0) "bms export must refuse while the BMS launcher runs."
        Assert-Match $bmsRunning.Output "is running \(FalconBMS_Alternative_Launcher\)" ("bms export should name the running launcher: " + $bmsRunning.Output)
        Assert-True ([System.IO.File]::ReadAllText($bmsSetCfg) -eq $bmsCfgBefore) "a refused bms export must not write User.cfg."
    }
    finally {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $previousBmsIgnore
        if (-not $bmsFake.HasExited) { Stop-Process -Id $bmsFake.Id -Force -ErrorAction SilentlyContinue }
        $bmsFake.WaitForExit(5000) | Out-Null
    }

    $bmsSetDry = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--dry-run", "--text")
    Assert-Match $bmsSetDry.Output "Dry run: would Falcon BMS User\.cfg: set g_nAxisExp_AXIS_PITCH 35" ("bms dry run should report the User.cfg change: " + $bmsSetDry.Output)
    Assert-True ([System.IO.File]::ReadAllText($bmsSetCfg) -eq $bmsCfgBefore) "bms dry run must not write User.cfg."

    $bmsSetExport = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--text")
    Assert-True ($bmsSetExport.ExitCode -eq 0) ("bms export with settings failed: " + $bmsSetExport.Output)

    # User.cfg: everything above the launcher's marker (the part its Launch keeps), launcher block untouched, CRLF.
    $bmsCfgText = [System.IO.File]::ReadAllText($bmsSetCfg)
    $bmsCfgLines = $bmsCfgText -split "`r`n"
    $bmsMarker = [Array]::IndexOf($bmsCfgLines, "// LAUNCHER OVERRIDES BEGIN HERE - DO NOT EDIT OR ADD BELOW THIS LINE")
    Assert-True ($bmsMarker -gt 0) "bms export must keep the launcher's marker line."
    $bmsAbove = ($bmsCfgLines[0..($bmsMarker - 1)] -join "`n")
    foreach ($bmsLine in @("set g_nAxisExp_AXIS_PITCH 35", "set g_nAxisSat_AXIS_PITCH 10", "set g_nAxisExp_AXIS_ROLL 20", "set g_fFlightControlsDeadband 0.0", "set g_nForceFeedbackDeadBand 0", "set g_bUseAnalogIdleCutoff 1")) {
        Assert-Match $bmsAbove ("(?m)^" + [regex]::Escape($bmsLine) + "( |$)") ("bms export should write '" + $bmsLine + "' above the launcher's marker: " + $bmsCfgText)
    }
    Assert-True ($bmsCfgText.EndsWith("set g_bExportRTTTextures 0 // LAUNCHER OVERRIDE`r`n")) "bms export must leave the launcher's override block as it was."
    Assert-True ($bmsCfgText.StartsWith($bmsCfgBefore.Substring(0, $bmsCfgBefore.IndexOf("set g_nAxisExp_AXIS_ROLL")))) "bms export must keep the stock header lines of User.cfg."
    Assert-NotMatch $bmsCfgText "[^\r]\n" "bms export must keep User.cfg's CRLF line ends."
    Assert-NotMatch $bmsAbove "// (SETUP|LAUNCHER) OVERRIDE" "JoyPro's User.cfg lines must not look like launcher lines (the launcher drops those)."
    Assert-NotMatch $bmsAbove "g_nAxisSat_AXIS_ROLL|g_nAxisExp_AXIS_CURSOR|AXIS_YAW" "bms export must not add 0 (default) lines for axes without a value."
    Assert-True (Test-Path ($bmsSetCfg + ".jpbak")) "bms export should back up User.cfg."

    # Feedback.ini: gains in place, comments and CRLF kept.
    $bmsIniText = [System.IO.File]::ReadAllText($bmsSetIni)
    Assert-Match $bmsIniText "\[Effect2\]\r\nflags=0x22\r\nduration=-1\r\nsamplePeriod=0\r\ngain=6000\r\n" "bms export should set the auto-centre gain in [Effect2]."
    Assert-Match $bmsIniText "conditionPositiveCoefficient = 7000;\r\nconditionNegativeCoefficient = 7000;" "bms export should set the auto-centre coefficients keeping the file's format."
    Assert-True (([regex]::Matches($bmsIniText, "gain=2500")).Count -eq 2) "bms export should set the stall gain in [Effect7] and [Effect8]."
    Assert-True (([regex]::Matches($bmsIniText, "gain=10000")).Count -eq ([regex]::Matches($bmsSetIniBefore, "gain=10000")).Count - 2) "bms export must leave the other effects' gains."
    Assert-True ($bmsIniText.Contains("#effect2 is auto-center")) "bms export must keep Feedback.ini's comments."
    Assert-True (Test-Path ($bmsSetIni + ".jpbak")) "bms export should back up Feedback.ini."

    # Pilot file: byte 388 only.
    $bmsPopAfter = [System.IO.File]::ReadAllBytes($bmsSetPop)
    Assert-True ($bmsPopAfter.Length -eq 468 -and $bmsPopAfter[388] -eq 1) "bms export should set Enable Force Feedback (pilot file byte 388)."
    $bmsPopDiff = @(0..467 | Where-Object { $bmsPopAfter[$_] -ne $bmsSetPopBefore[$_] })
    Assert-True ($bmsPopDiff.Count -eq 1 -and $bmsPopDiff[0] -eq 388) ("bms export must change only byte 388 of the pilot file: " + ($bmsPopDiff -join ","))

    # Device files: pitch steps, throttle detents before <axis>, no dead zone on the one-sided throttle.
    [xml]$bmsSetStickXml = [System.IO.File]::ReadAllText($bmsSetStick)
    Assert-True ($bmsSetStickXml.JoyAssgn.axis.AxAssgn[1].Deadzone -eq "Medium" -and $bmsSetStickXml.JoyAssgn.axis.AxAssgn[1].Saturation -eq "Large") "bms export should write pitch's launcher steps (Medium/Large)."
    $bmsThrottleText = [System.IO.File]::ReadAllText($bmsSetThrottle)
    Assert-Match $bmsThrottleText "<JoyAssgn [^>]*>\r\n  <detentPosition>\r\n    <AB>52429</AB>\r\n    <IDLE>3277</IDLE>\r\n  </detentPosition>\r\n  <axis>" ("bms export should write the throttle detents (80 % / 5 % of 65536) as the launcher does: " + $bmsThrottleText.Substring(0, 400))
    [xml]$bmsSetThrottleXml = $bmsThrottleText
    Assert-True ($bmsSetThrottleXml.JoyAssgn.axis.AxAssgn[2].Deadzone -eq "None" -and $bmsSetThrottleXml.JoyAssgn.axis.AxAssgn[2].Saturation -eq "Medium") "bms export must keep the throttle's saturation and write no dead zone on it."

    # A second export changes nothing; a merge keeps a value the user changed in User.cfg.
    $bmsAfterFirst = [System.IO.File]::ReadAllText($bmsSetCfg)
    $bmsSetAgain = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--text")
    Assert-True ($bmsSetAgain.ExitCode -eq 0 -and [System.IO.File]::ReadAllText($bmsSetCfg) -eq $bmsAfterFirst -and [System.IO.File]::ReadAllText($bmsSetThrottle) -eq $bmsThrottleText) "a second bms export must not change User.cfg or the device files."
    Assert-NotMatch $bmsSetAgain.Output "Feedback\.ini|Smoke\.pop" "a second bms export must not rewrite Feedback.ini or the pilot file."
    [System.IO.File]::WriteAllText($bmsSetCfg, $bmsAfterFirst.Replace("set g_nAxisExp_AXIS_PITCH 35", "set g_nAxisExp_AXIS_PITCH 50"))
    $bmsSetMerge = Run-Engine @("--profile", $bmsSetProfile, "bms", "export", $bmsSetConfig, "--mode", "merge", "--text")
    Assert-Match $bmsSetMerge.Output "g_nAxisExp_AXIS_PITCH stays 50" ("bms merge should keep a curve value changed in User.cfg: " + $bmsSetMerge.Output)
    Assert-Match ([System.IO.File]::ReadAllText($bmsSetCfg)) "set g_nAxisExp_AXIS_PITCH 50" "bms merge must not overwrite the user's User.cfg value."

    # An export without BMS binds still writes the game settings.
    $bmsOnlyProfile = Join-Path $tempDir "bms-settings-only.pr0file"
    $bmsOnlySet = Run-Engine @("--profile", $bmsOnlyProfile, "ffb", "set", "--device", "*", "--game", "FalconBMS", "--param", "stallGain=1234", "--text")
    Assert-True ($bmsOnlySet.ExitCode -eq 0) "ffb set in a profile without BMS binds failed."
    $bmsOnly = Run-Engine @("--profile", $bmsOnlyProfile, "bms", "export", $bmsSetConfig, "--text")
    Assert-True ($bmsOnly.ExitCode -eq 0) ("bms export of settings only failed: " + $bmsOnly.Output)
    Assert-Match $bmsOnly.Output "only the game settings were written" "bms export without binds should say it wrote only the settings."
    Assert-True (([regex]::Matches([System.IO.File]::ReadAllText($bmsSetIni), "gain=1234")).Count -eq 2) "bms export without binds should still write Feedback.ini."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousBmsSetFake
}
