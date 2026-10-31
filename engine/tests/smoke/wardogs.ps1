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

# Export modes (one bind per action/slot in WARDOGS; only cataloged helicopter actions count, other lines never change):
#   add       never replaces a bound slot; a bind on a button another cataloged action uses is skipped and reported
#   merge     writes the profile's binds, clears the other action's slot on the same input (conflict), keeps a second
#             line of the action (its old input)
#   merge-overwrite  merge + the action's second line goes (a moved action fully moves)
$wdmDir = Join-Path $tempDir "wardogs-modes"
New-Item -Path $wdmDir -ItemType Directory -Force | Out-Null
$wdmIni = Join-Path $wdmDir "GameUserSettings.ini"
# The throttle gets the name the game uses (no name repair in this test); Fire has a second, older line on button 6.
$wdmText = [System.IO.File]::ReadAllText((Join-Path $engineDir "tests\fixtures\wardogs\GameUserSettings.ini")).Replace("Smoke Throttle (ARM)", "Smoke Throttle")
$wdmText = $wdmText.Replace("ActionBindings=(Action=UnknownFutureAction", "ActionBindings=(Action=Fire,Binding=(DeviceIdentifier=`"1234:5678:Smoke Stick`",_aaaaljahlggohoapfcfef=5))`r`nActionBindings=(Action=UnknownFutureAction")
[System.IO.File]::WriteAllText($wdmIni, $wdmText)
$wdmProfile = Join-Path $wdmDir "modes.pr0file"
$wdmIds = @("Pitch", "Roll", "Yaw", "Throttle", "LookPitch", "LookYaw") + @(((Run-Engine @("wardogs", "catalog")).Output | Out-String | ForEach-Object { $_.Substring($_.IndexOf("{")) } | ConvertFrom-Json).Actions | ForEach-Object { $_.Id })
# Everything but the axis lines and the action lines of cataloged actions.
function Get-WdmNonFlight([string]$Text) {
    return (($Text -split "\r?\n") | Where-Object {
        $m = [regex]::Match($_, '^(?:(\w+)=\(DeviceIdentifier|ActionBindings=\(Action=(\w+))')
        -not ($m.Success -and ($wdmIds -contains ($m.Groups[1].Value + $m.Groups[2].Value)))
    }) -join "`n"
}
$wdmNonFlight = Get-WdmNonFlight $wdmText
$wdmPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
try {
    $wdmStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $wdmThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    # Moved: Fire to stick button 3. Conflict: look up on throttle button 9, which the game uses for look down.
    # Flares on throttle button 13: the game has UnknownFutureAction there (not cataloged: no conflict, no multi-bind).
    Run-Engine @("--profile", $wdmProfile, "relations", "add", "WDM Fire", "--button", "--node", "Wardogs:Fire", "--device", $wdmStick, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $wdmProfile, "relations", "add", "WDM Look up", "--button", "--node", "Wardogs:LookPitch.Positive", "--device", $wdmThrottle, "--input", "JOY_BTN9", "--text") | Out-Null
    Run-Engine @("--profile", $wdmProfile, "relations", "add", "WDM Flares", "--button", "--node", "Wardogs:Flares", "--device", $wdmThrottle, "--input", "JOY_BTN13", "--text") | Out-Null

    $wdmAdd = Run-Engine @("--profile", $wdmProfile, "wardogs", "export", $wdmIni, "--mode", "add")
    Assert-True ($wdmAdd.ExitCode -eq 0) ("wardogs add export failed: " + $wdmAdd.Output)
    $wdmAddJson = ($wdmAdd.Output | Out-String | ForEach-Object { $_.Substring($_.IndexOf("{")) } | ConvertFrom-Json)
    Assert-True (@($wdmAddJson.SkippedTakenInputs | Where-Object { $_.Action -eq "LookPitch.Positive" -and $_.GameAction -eq "LookPitch.Negative" }).Count -eq 1) ("wardogs add should report look up on look down's button: " + $wdmAdd.Output)
    $wdmAfter = [System.IO.File]::ReadAllText($wdmIni)
    Assert-Match $wdmAfter 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaaljahlggohoapfcfef=0\)' "wardogs add must keep the game's Fire bind."
    Assert-Match $wdmAfter 'Negative=\(DeviceIdentifier="ABCD:0001:Smoke Throttle",_aaaaljahlggohoapfcfef=8,' "wardogs add must keep look down."
    Assert-Match $wdmAfter 'Action=Flares,Binding=\(DeviceIdentifier="ABCD:0001:Smoke Throttle",_aaaaljahlggohoapfcfef=12\)' "wardogs add should write Flares beside the uncataloged action."
    Assert-True ((Get-WdmNonFlight $wdmAfter) -eq $wdmNonFlight) "wardogs add must leave the non-flight lines identical."

    $wdmMerge = Run-Engine @("--profile", $wdmProfile, "wardogs", "export", $wdmIni, "--mode", "merge", "--text")
    Assert-True ($wdmMerge.ExitCode -eq 0) ("wardogs merge export failed: " + $wdmMerge.Output)
    $wdmAfter = [System.IO.File]::ReadAllText($wdmIni)
    Assert-Match $wdmAfter 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaaljahlggohoapfcfef=2\)' "wardogs merge should write Fire's new button."
    Assert-Match $wdmAfter 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaaljahlggohoapfcfef=5\)' "wardogs merge must keep Fire's second (old) line."
    Assert-Match $wdmAfter 'Positive=\(DeviceIdentifier="ABCD:0001:Smoke Throttle",_aaaaljahlggohoapfcfef=8,' "wardogs merge should write look up."
    Assert-Match $wdmAfter 'Negative=\(DeviceIdentifier="",_aaaaljahlggohoapfcfef=-1,_aaaaidddockpolojoodpb=-1,_aaaamdcclmobefllhjkdm=Up\),_aaaaoabiaaaacohedinbn=1\.000000,_aaabgcjobaejlnkbppedn=False,_aaaaombggppmmijkohdph=\(DeviceIdentifier="",_aaaajcpgpllhacnlfdnkg=-1,_aaaajlbldnobdplelbhhg=0\.500000,_aaaakbblnlfadfmofnkml=True,_aaaaljahlggohoapfcfef=-1,_aaaaidddockpolojoodpb=-1,_aaaamdcclmobefllhjkdm=Up\),_aaaaochdddlogkjceogmo=\(DeviceIdentifier="",_aaaajcpgpllhacnlfdnkg=-1,_aaaajlbldnobdplelbhhg=0\.500000,_aaaakbblnlfadfmofnkml=True,_aaaaljahlggohoapfcfef=-1,_aaaaidddockpolojoodpb=-1,_aaaamdcclmobefllhjkdm=Up\),Sensitivity=1\.000000,DeadZone=0\.050000\)\r?\nLookYaw' "wardogs merge should clear look down on look up's button (conflict)."
    Assert-Match $wdmAfter '(?m)^Roll=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaajcpgpllhacnlfdnkg=0,' "wardogs merge must keep the game's other binds."
    Assert-True ((Get-WdmNonFlight $wdmAfter) -eq $wdmNonFlight) "wardogs merge must leave the non-flight lines identical."

    $wdmOverwrite = Run-Engine @("--profile", $wdmProfile, "wardogs", "export", $wdmIni, "--mode", "merge-overwrite", "--text")
    Assert-True ($wdmOverwrite.ExitCode -eq 0) ("wardogs merge-overwrite export failed: " + $wdmOverwrite.Output)
    $wdmAfter = [System.IO.File]::ReadAllText($wdmIni)
    Assert-NotMatch $wdmAfter '_aaaaljahlggohoapfcfef=5\)' "wardogs merge-overwrite should remove Fire's old line."
    Assert-Match $wdmAfter 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaaljahlggohoapfcfef=2\)' "wardogs merge-overwrite should keep Fire's new button."
    Assert-Match $wdmAfter '(?m)^Roll=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaajcpgpllhacnlfdnkg=0,' "wardogs merge-overwrite must keep binds of actions the profile does not bind."
    Assert-True ((Get-WdmNonFlight $wdmAfter) -eq $wdmNonFlight) "wardogs merge-overwrite must leave the non-flight lines identical."

    $wdmClean = Run-Engine @("--profile", $wdmProfile, "wardogs", "export", $wdmIni, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($wdmClean.ExitCode -eq 0) ("wardogs clean + nuke export failed: " + $wdmClean.Output)
    $wdmAfter = [System.IO.File]::ReadAllText($wdmIni)
    Assert-Match $wdmAfter '(?m)^Roll=\(DeviceIdentifier="",' "wardogs clean should release the written devices' other binds."
    Assert-True ((Get-WdmNonFlight $wdmAfter) -eq $wdmNonFlight) "wardogs clean + nuke must leave the non-flight lines identical."

    # Nuke never clears a device that is not connected.
    $wdmGone = $wdmText.Replace('Roll=(DeviceIdentifier="1234:5678:Smoke Stick"', 'Roll=(DeviceIdentifier="0FFF:0FFF:Gone Stick"')
    [System.IO.File]::WriteAllText($wdmIni, $wdmGone)
    $wdmNuke = Run-Engine @("--profile", $wdmProfile, "wardogs", "export", $wdmIni, "--mode", "merge", "--nuke-untouched-devices", "--text")
    Assert-True ($wdmNuke.ExitCode -eq 0) ("wardogs nuke export failed: " + $wdmNuke.Output)
    Assert-Match ([System.IO.File]::ReadAllText($wdmIni)) 'Roll=\(DeviceIdentifier="0FFF:0FFF:Gone Stick"' "wardogs nuke must keep binds of devices that are not connected."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $wdmPreviousFakeDevices
}