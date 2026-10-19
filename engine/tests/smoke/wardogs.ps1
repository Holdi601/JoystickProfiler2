# WARDOGS axis tuning (wardogs model: Sensitivity, DeadZone, invert) and the game-wide HOTAS switches (device "*").
# Dot-sourced by engine_smoke_test.ps1 ($engineDir, $tempDir, Run-Engine, Assert-*). Fixture: synthetic ini with
# Pitch 1.35 / 0.12 and Roll 0.65 / 0.00 (the game saves Sensitivity on 0.05 steps).
$wdtFixture = Join-Path $engineDir "tests\fixtures\wardogs\tuning\GameUserSettings.ini"
$wdtDir = Join-Path $tempDir "wardogs-tuning"
New-Item -Path $wdtDir -ItemType Directory -Force | Out-Null
$wdtIni = Join-Path $wdtDir "GameUserSettings.ini"
Copy-Item -Path $wdtFixture -Destination $wdtIni -Force
$wdtOriginal = [System.IO.File]::ReadAllText($wdtIni)
$wdtProfile = Join-Path $tempDir "wardogs-tuning.pr0file"
$wdtPitch = "Cyclic pitch (nose up/down)"
$wdtRoll = "Cyclic roll"

function Get-WardogsAxisLine([string]$Text, [string]$Axis) {
    $m = [regex]::Match($Text, "(?m)^" + $Axis + "=\(.*\)\r?$")
    return $m.Value
}

$previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
try {
    $wdtModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $wdtModels.Output "(?m)^wardogs - WARDOGS" "binds tuning models should list the wardogs model."
    Assert-Match $wdtModels.Output "sensitivity \(Sensitivity, Number 0\.1\.\.2\)" "the wardogs model should offer Sensitivity 0.1..2."

    # Import: the HOTAS page's values become game:Wardogs tuning (not the base).
    $wdtImport = Run-Engine @("--profile", $wdtProfile, "wardogs", "import", $wdtIni, "--text")
    Assert-True ($wdtImport.ExitCode -eq 0) "wardogs import (tuning fixture) failed. Output: $($wdtImport.Output)"
    $wdtShow = Run-Engine @("--profile", $wdtProfile, "binds", "tuning", "show", $wdtPitch, "--game", "Wardogs", "--text")
    Assert-Match $wdtShow.Output "game:Wardogs: inv=1;.*n\.deadZone=0\.12;n\.sensitivity=1\.35" "wardogs import should store Pitch sensitivity 1.35 and dead zone 0.12 as game:Wardogs tuning."
    Assert-Match $wdtShow.Output "(?m)^\s*base: inv=1;sl=0;dz=0;" "wardogs import must not put the game's dead zone into the base."
    Assert-Match $wdtShow.Output "Wardogs uses \(wardogs, from game:Wardogs\)" "Wardogs should resolve the wardogs model from its own scope."
    $wdtShowRoll = Run-Engine @("--profile", $wdtProfile, "binds", "tuning", "show", $wdtRoll, "--game", "Wardogs", "--text")
    Assert-Match $wdtShowRoll.Output "n\.deadZone=0;n\.sensitivity=0\.65" "wardogs import should keep a dead zone of 0."

    # Round trip: the same values go back, byte for byte.
    $wdtRound = Run-Engine @("--profile", $wdtProfile, "wardogs", "export", $wdtIni, "--text")
    Assert-True ($wdtRound.ExitCode -eq 0) "wardogs export after the tuning import failed. Output: $($wdtRound.Output)"
    Assert-True ([System.IO.File]::ReadAllText($wdtIni) -eq $wdtOriginal) "wardogs import/export of sensitivity and dead zone must leave the file byte-identical."

    # Own values: always written, also a dead zone of 0 (the old export skipped it).
    $wdtSet = Run-Engine @("--profile", $wdtProfile, "binds", "tuning", "set", $wdtPitch, "--scope", "game:Wardogs", "--param", "sensitivity=1.85", "--param", "deadZone=0", "--text")
    Assert-True ($wdtSet.ExitCode -eq 0) "binds tuning set for game:Wardogs failed. Output: $($wdtSet.Output)"
    $wdtTooHigh = Run-Engine @("--profile", $wdtProfile, "binds", "tuning", "set", $wdtPitch, "--scope", "game:Wardogs", "--param", "sensitivity=3", "--text")
    Assert-True ($wdtTooHigh.ExitCode -ne 0) "a WARDOGS sensitivity above 2 must be refused (the scope's model range, not Korea's)."
    $wdtPreview = Run-Engine @("--profile", $wdtProfile, "binds", "tuning", "preview", $wdtPitch, "--game", "Wardogs", "--points", "5", "--text")
    Assert-Match $wdtPreview.Output "\+0\.500 -> -0\.925000" "the wardogs preview should scale by the sensitivity (inverted pitch, 0.5 * 1.85)."
    Assert-Match $wdtPreview.Output "\+1\.000 -> -1\.000000" "the wardogs preview should clip at full deflection."
    $wdtExport = Run-Engine @("--profile", $wdtProfile, "wardogs", "export", $wdtIni, "--text")
    Assert-True ($wdtExport.ExitCode -eq 0) "wardogs export with own tuning failed. Output: $($wdtExport.Output)"
    $wdtText = [System.IO.File]::ReadAllText($wdtIni)
    Assert-Match (Get-WardogsAxisLine $wdtText "Pitch") "Sensitivity=1\.850000,DeadZone=0\.000000\)" "wardogs export should write sensitivity 1.85 and a dead zone of 0."
    Assert-Match (Get-WardogsAxisLine $wdtText "Roll") "Sensitivity=0\.650000,DeadZone=0\.000000\)" "wardogs export must keep the other axes' values."
    Assert-Match $wdtText "_aaaaoabiaaaacohedinbn=1\.000000,_aaabgcjobaejlnkbppedn=False" "wardogs export must keep the hidden fields."

    # Fit from the base: dead zone carried, saturation -> slope, curvature not representable (warning), a slider's
    # dead zone not carried (it would sit at mid collective).
    $wdtBaseProfile = Join-Path $tempDir "wardogs-base.pr0file"
    Run-Engine @("--profile", $wdtBaseProfile, "relations", "add", "WD Roll", "--axis", "--node", "Wardogs:Roll", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $wdtBaseProfile, "relations", "add", "WD Collective", "--axis", "--node", "Wardogs:Throttle", "--device", "Smoke Throttle {66666666-7777-8888-9999-000000000000}", "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $wdtBaseProfile, "binds", "set", "WD Roll", "--deadzone", "0.1", "--saturation-x", "0.8", "--curvature", "0.3", "--text") | Out-Null
    Run-Engine @("--profile", $wdtBaseProfile, "binds", "set", "WD Collective", "--deadzone", "0.2", "--slider", "true", "--text") | Out-Null
    $wdtFit = Run-Engine @("--profile", $wdtBaseProfile, "binds", "tuning", "show", "WD Roll", "--game", "Wardogs", "--text")
    Assert-Match $wdtFit.Output "from base, fitted to the base curve\): .*n\.deadZone=0\.1;n\.sensitivity=1\.3" "the wardogs fit should keep the dead zone and turn saturation X 0.8 into sensitivity 1.3 (1.29 snapped to the slider's 0.05 steps)."
    $wdtFitCollective = Run-Engine @("--profile", $wdtBaseProfile, "binds", "tuning", "show", "WD Collective", "--game", "Wardogs", "--text")
    Assert-Match $wdtFitCollective.Output "fitted to the base curve\): .*n\.deadZone=0;n\.sensitivity=1" "a slider's base dead zone must not leak into WARDOGS' centred dead zone."
    Copy-Item -Path $wdtFixture -Destination $wdtIni -Force
    $wdtBaseExport = Run-Engine @("--profile", $wdtBaseProfile, "wardogs", "export", $wdtIni, "--text")
    Assert-True ($wdtBaseExport.ExitCode -eq 0) "wardogs export of fitted base values failed. Output: $($wdtBaseExport.Output)"
    Assert-Match $wdtBaseExport.Output "WARDOGS has no response curve" "wardogs export should say that the curvature is not exported."
    $wdtBaseText = [System.IO.File]::ReadAllText($wdtIni)
    Assert-Match (Get-WardogsAxisLine $wdtBaseText "Roll") "Sensitivity=1\.300000,DeadZone=0\.100000\)" "wardogs export should write the fitted roll values."
    Assert-Match (Get-WardogsAxisLine $wdtBaseText "Throttle") "Sensitivity=1\.000000,DeadZone=0\.000000\)" "wardogs export should write the collective without the slider's dead zone."

    # Game-wide switches (device "*"): Self-Centering Collective and Enable HOTAS, in [/Script/WDGame.WDUserSettings].
    $wdtFfbModels = Run-Engine @("ffb", "models", "--text")
    Assert-Match $wdtFfbModels.Output "selfCenteringCollective \(Self-Centering Collective, true/false, default 0, game-wide" "ffb models should list WARDOGS' Self-Centering Collective."
    $wdtWide = Run-Engine @("--profile", $wdtBaseProfile, "ffb", "set", "--device", "*", "--game", "Wardogs", "--param", "selfCenteringCollective=true", "--param", "enableHotas=true", "--text")
    Assert-True ($wdtWide.ExitCode -eq 0) "ffb set for WARDOGS failed. Output: $($wdtWide.Output)"
    $wdtWideExport = Run-Engine @("--profile", $wdtBaseProfile, "wardogs", "export", $wdtIni, "--text")
    Assert-Match $wdtWideExport.Output "Set bCollectiveSelfCentering=True" "wardogs export should report the self-centering switch."
    $wdtWideText = [System.IO.File]::ReadAllText($wdtIni)
    Assert-Match $wdtWideText "(?m)^bCollectiveSelfCentering=True\r?$" "wardogs export should turn Self-Centering Collective on."
    Assert-Match $wdtWideText "(?m)^bUseHOTASHelicopters=True\r?$" "wardogs export should keep Enable HOTAS on."
    Assert-Match $wdtWideText "(?m)^SomeOtherSetting=42\r?$" "wardogs export must keep other user settings."

    # Enable HOTAS off on purpose: no forced switch-on, a warning instead.
    Run-Engine @("--profile", $wdtBaseProfile, "ffb", "set", "--device", "*", "--game", "Wardogs", "--param", "enableHotas=false", "--text") | Out-Null
    $wdtOff = Run-Engine @("--profile", $wdtBaseProfile, "wardogs", "export", $wdtIni, "--text")
    Assert-Match $wdtOff.Output "'Enable HOTAS' is off in the profile" "wardogs export should warn when the profile switches HOTAS off."
    Assert-Match ([System.IO.File]::ReadAllText($wdtIni)) "(?m)^bUseHOTASHelicopters=False\r?$" "wardogs export should write Enable HOTAS off when the profile says so."

    # Settings only: a profile without WARDOGS binds still writes its game-wide settings and leaves the binds alone.
    Copy-Item -Path $wdtFixture -Destination $wdtIni -Force
    $wdtSettingsProfile = Join-Path $tempDir "wardogs-settings.pr0file"
    Run-Engine @("--profile", $wdtSettingsProfile, "ffb", "set", "--device", "*", "--game", "Wardogs", "--param", "selfCenteringCollective=true", "--text") | Out-Null
    $wdtOnly = Run-Engine @("--profile", $wdtSettingsProfile, "wardogs", "export", $wdtIni, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($wdtOnly.ExitCode -eq 0) "wardogs settings-only export failed. Output: $($wdtOnly.Output)"
    Assert-Match $wdtOnly.Output "only the game settings were written" "wardogs export without binds should say it writes the settings only."
    $wdtOnlyText = [System.IO.File]::ReadAllText($wdtIni)
    Assert-True ($wdtOnlyText -eq $wdtOriginal.Replace("bCollectiveSelfCentering=False", "bCollectiveSelfCentering=True")) "a settings-only wardogs export (even clean + nuke) must change only the setting."
    Assert-True (Test-Path ($wdtIni + ".jpbak")) "the wardogs settings export should back up the ini."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
}
