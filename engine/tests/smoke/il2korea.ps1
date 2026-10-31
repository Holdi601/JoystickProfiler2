# IL-2 Series: Korea (registry game) on a synthetic general.actions + known.devices.json (docs/games/il2-korea.md).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Moved here from the inline part so it
# runs on its own; checks scan/import/round trip, axis responses, force feedback, Relative Control and the export modes.
# Registry game IL-2 Korea: synthetic general.actions + known.devices.json (Smoke Stick = device 1, Old Pedals =
# device 3 and not connected, Smoke Throttle known to the game without a device number).
$koDir = Join-Path $tempDir "il2korea\\input"
New-Item -Path $koDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\il2korea\\input\\*") -Destination $koDir -Force
$koActions = Join-Path $koDir "general.actions"
$koKnown = Join-Path $koDir "known.devices.json"
$koProfile = Join-Path $tempDir "il2korea.pr0file"
$koStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$koThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
$previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\il2korea\\devices.tsv"
try {
    $koScan = Run-Engine @("il2korea", "scan", $koActions, "--text")
    Assert-True ($koScan.ExitCode -eq 0) "il2korea scan failed."
    Assert-Match $koScan.Output "(?m)^Bindings Read:\s+10\s*$" "il2korea scan should read 7 joystick and 3 keyboard inputs (button pairs and the mouse are not JoyPro inputs)."
    Assert-Match $koScan.Output "gear <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN3" "il2korea scan should map dev1_b2 (0-based) to the device numbered 1 in known.devices.json."
    Assert-Match $koScan.Output "airbrakes_on <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_R" "il2korea scan should read hats (pov0_90 = first hat, right)."
    Assert-Match $koScan.Output "canopy_open_close <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN6 \+ Keyboard LeftShift" "il2korea scan should read a keyboard modifier."
    Assert-Match $koScan.Output "rpc_yaw <- Old Pedals \{99999999-8888-7777-6666-555555555555\} JOY_RZ" "il2korea scan should name a device that is not connected after known.devices.json."

    $koImport = Run-Engine @("--profile", $koProfile, "il2korea", "import", $koActions, "--text")
    Assert-True ($koImport.ExitCode -eq 0) "il2korea import failed."
    Assert-True (Test-Path ($koActions + ".joypro.json")) "il2korea import should record JoyPro's inputs."
    $koActionsText = [System.IO.File]::ReadAllText($koActions)
    $koKnownText = [System.IO.File]::ReadAllText($koKnown)
    $koRound = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
    Assert-True ($koRound.ExitCode -eq 0) "il2korea export after import failed."
    Assert-True (([System.IO.File]::ReadAllText($koActions) -eq $koActionsText) -and ([System.IO.File]::ReadAllText($koKnown) -eq $koKnownText)) "il2korea import/export round trip must leave both files byte-identical."

    # Axis response (general.pitch / shared.responses): imported as the relation's IL-2 Korea tuning, not as its
    # base (which DCS uses); the preview reproduces the table the game compiled for these settings.
    $koPitchFile = Join-Path $koDir "general.pitch"
    $koSharedFile = Join-Path $koDir "shared.responses"
    $koPitchText = [System.IO.File]::ReadAllText($koPitchFile)
    $koSharedText = [System.IO.File]::ReadAllText($koSharedFile)
    Assert-True (([System.IO.File]::ReadAllText($koPitchFile) -eq $koPitchText) -and ([System.IO.File]::ReadAllText($koSharedFile) -eq $koSharedText)) "il2korea round trip must leave the response files as they were."
    $koPitchShow = Run-Engine @("--profile", $koProfile, "binds", "tuning", "show", "Pitch (Elevator)", "--game", "IL2Korea", "--text")
    Assert-Match $koPitchShow.Output "game:IL2Korea: .*n\.deadZone0=3;.*n\.offset=39;n\.sensitivity=22" "il2korea import should store general.pitch as the relation's IL-2 Korea tuning."
    Assert-Match $koPitchShow.Output "base: inv=1;sl=0;dz=0;sx=1;sy=1;cv=0\s" "il2korea import must not put Korea's response into the base tuning (DCS)."
    $koPreview = Run-Engine @("--profile", $koProfile, "binds", "tuning", "preview", "Pitch (Elevator)", "--game", "IL2Korea", "--points", "11", "--text")
    Assert-Match $koPreview.Output "-0\.200 -> \+0\.477850" "il2korea preview must match the table the game compiled (exponent 22, centre dead zone 3, offset 39; the inverted axis mirrors the input)."
    $koRollSet = Run-Engine @("--profile", $koProfile, "binds", "tuning", "set", "Roll (Ailerons)", "--scope", "game:IL2Korea", "--param", "sensitivity=60", "--param", "deadZone1=4", "--text")
    Assert-True ($koRollSet.ExitCode -eq 0) "binds tuning set for the Korea roll relation failed."
    $koYawSet = Run-Engine @("--profile", $koProfile, "binds", "tuning", "set", "Yaw (Rudder)", "--scope", "base", "--curvature", "0.3", "--deadzone", "0.05", "--text")
    Assert-True ($koYawSet.ExitCode -eq 0) "binds tuning set for the yaw base failed."
    $koCurveExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
    Assert-True ($koCurveExport.ExitCode -eq 0) "il2korea export with axis tuning failed."
    $koShared = [System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json
    Assert-True ($koShared.curves.rpc_roll.sensitivity -eq 60 -and $koShared.curves.rpc_roll.deadZone1 -eq 4) "il2korea export should write the relation's Korea tuning to shared.responses."
    Assert-True ($koShared.curves.rpc_yaw.sensitivity -eq 93 -and $koShared.curves.rpc_yaw.deadZone0 -eq 5) "il2korea export should fit the base curve (DCS curvature 0.3, dead zone 0.05) to Korea's exponent when the relation has no Korea tuning."
    Assert-True ([System.IO.File]::ReadAllText($koPitchFile) -eq $koPitchText) "il2korea export must leave an unchanged pitch curve as it was."
    # A response the user changed in the game: merge keeps it, merge-overwrite sets the profile's again.
    [System.IO.File]::WriteAllText($koSharedFile, ([System.IO.File]::ReadAllText($koSharedFile) -replace '"sensitivity": 60', '"sensitivity": 61'), (New-Object System.Text.UTF8Encoding($false)))
    $koCurveMerge = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge", "--text")
    Assert-True ($koCurveMerge.ExitCode -eq 0) "il2korea merge export failed."
    Assert-Match $koCurveMerge.Output "rpc_roll has response settings made in the game; merge keeps them" "il2korea merge should report the kept in-game response."
    Assert-True (([System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json).curves.rpc_roll.sensitivity -eq 61) "il2korea merge must keep a response changed in the game."
    $koCurveOverwrite = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
    Assert-True (([System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json).curves.rpc_roll.sensitivity -eq 60) "il2korea merge-overwrite should set the profile's response again."

    # Game-wide force feedback and the axis noise filter (engine ffb --device *): Settings > Input devices, which
    # Korea keeps in data\NSData\UserData\UserSettings.json "input"; other keys and startup.cfg untouched.
    $koUserData = Join-Path (Split-Path -Parent $koDir) "NSData\UserData"
    New-Item -ItemType Directory -Force -Path $koUserData | Out-Null
    $koSettings = Join-Path $koUserData "UserSettings.json"
    $koSettingsText = "{`r`n  `"camera`": {`r`n    `"shake`": 1`r`n  },`r`n  `"input`": {`r`n    `"mouseSensitivity`": 4.0,`r`n    `"joyFilter`": 0.0,`r`n    `"ffEnabled`": true,`r`n    `"ffAmplitude`": 100.0,`r`n    `"ffForce`": 100.0,`r`n    `"exclusive`": true`r`n  }`r`n}"
    [System.IO.File]::WriteAllText($koSettings, $koSettingsText, (New-Object System.Text.UTF8Encoding($false)))
    $koStartup = Join-Path (Split-Path -Parent $koDir) "startup.cfg"
    [System.IO.File]::WriteAllText($koStartup, "[KEY = force_feedback]`r`n`tforce = 1.0`r`n[END]`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $koFfb = Run-Engine @("--profile", $koProfile, "ffb", "set", "--device", "*", "--game", "IL2Korea", "--param", "ffEnabled=false", "--param", "ffForce=50", "--param", "ffForceFactorMax=178", "--param", "joyFilter=38", "--text")
    Assert-True ($koFfb.ExitCode -eq 0) ("ffb set for IL-2 Korea failed: " + $koFfb.Output)
    $koFfbExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
    Assert-True ($koFfbExport.ExitCode -eq 0) "il2korea export with force feedback settings failed."
    $koSettingsAfter = [System.IO.File]::ReadAllText($koSettings)
    $koSettingsWant = "{`r`n  `"camera`": {`r`n    `"shake`": 1`r`n  },`r`n  `"input`": {`r`n    `"mouseSensitivity`": 4.0,`r`n    `"joyFilter`": 38.0,`r`n    `"ffEnabled`": false,`r`n    `"ffAmplitude`": 100.0,`r`n    `"ffForce`": 50.0,`r`n    `"exclusive`": true,`r`n    `"ffForceFactorMax`": 178.0`r`n  }`r`n}"
    Assert-True ($koSettingsAfter -eq $koSettingsWant) ("il2korea export should set the Input devices keys in UserSettings.json in place: " + $koSettingsAfter)
    Assert-True ([System.IO.File]::ReadAllText($koStartup) -eq "[KEY = force_feedback]`r`n`tforce = 1.0`r`n[END]`r`n") "il2korea export must not write startup.cfg (the game does not read force feedback from it)."

    # Relative Control ("*" before the token) and the one-sided kind of throttle-like axes come in as the relation's
    # IL-2 Korea tuning and go back out unchanged; the built-in curve and control surface loading drop-downs of the
    # primary controls are tuning values written to the response file.
    $koRelDir = Join-Path $tempDir "il2korea-rel\\input"
    New-Item -Path $koRelDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\il2korea\\input\\*") -Destination $koRelDir -Force
    $koRelActions = Join-Path $koRelDir "general.actions"
    $koRelText = [System.IO.File]::ReadAllText($koRelActions).Replace("`"actions`": {`n", "`"actions`": {`n    `"rpc_all_engines_throttles`": [`n      `"*-dev1_axis_t`"`n    ],`n")
    # Relative Control's dead zone and sensitivity (the diamond dialog) live in general.actions "additive", shown value / 100.
    $koRelText = $koRelText.TrimEnd().TrimEnd('}').TrimEnd() + ",`n  `"additive`": {`n    `"rpc_all_engines_throttles`": {`n      `"deadzone`": 0.17,`n      `"sensitivity`": 0.11`n    }`n  }`n}`n"
    [System.IO.File]::WriteAllText($koRelActions, $koRelText, (New-Object System.Text.UTF8Encoding($false)))
    $koRelProfile = Join-Path $tempDir "il2korea-rel.pr0file"
    $koRelImport = Run-Engine @("--profile", $koRelProfile, "il2korea", "import", $koRelActions, "--text")
    Assert-True ($koRelImport.ExitCode -eq 0) ("il2korea import with a relative axis failed: " + $koRelImport.Output)
    $koRelShow = Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "show", "Throttle control", "--game", "IL2Korea", "--text")
    Assert-Match $koRelShow.Output "game:IL2Korea: inv=1;sl=1;.*n\.relative=1" ("il2korea import should keep Relative Control, the inversion and the one-sided kind of the throttle: " + $koRelShow.Output)
    Assert-Match $koRelShow.Output "n\.relativeDeadZone=17\b" ("il2korea import should read Relative Control's dead zone (0.17 -> 17 %): " + $koRelShow.Output)
    Assert-Match $koRelShow.Output "n\.relativeSensitivity=11\b" ("il2korea import should read Relative Control's sensitivity (0.11 -> 11): " + $koRelShow.Output)
    $koRelBefore = [System.IO.File]::ReadAllText($koRelActions)
    $koRelExport = Run-Engine @("--profile", $koRelProfile, "il2korea", "export", $koRelActions, "--text")
    Assert-True ($koRelExport.ExitCode -eq 0) "il2korea export with a relative axis failed."
    Assert-True ([System.IO.File]::ReadAllText($koRelActions) -eq $koRelBefore) "il2korea export must write a relative inverted axis back as *-dev1_axis_t."
    Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "set", "Throttle control", "--scope", "game:IL2Korea", "--slider", "true", "--inverted", "false", "--param", "deadZone0=10", "--param", "relative=1", "--param", "relativeDeadZone=80", "--param", "relativeSensitivity=20", "--text") | Out-Null
    $koRelPreview = Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "preview", "Throttle control", "--game", "IL2Korea", "--points", "11", "--text")
    Assert-Match $koRelPreview.Output "-0\.800 -> -1\.000000" "a one-sided Korea axis keeps -1 for the low dead zone (10 % of the whole travel)."
    Assert-Match $koRelPreview.Output "\+0\.000 -> -0\.11111" "a one-sided Korea axis runs the curve over the whole travel."
    Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "set", "Pitch (Elevator)", "--scope", "game:IL2Korea", "--param", "harmonization=2", "--param", "normalized=0", "--text") | Out-Null
    Run-Engine @("--profile", $koRelProfile, "il2korea", "export", $koRelActions, "--mode", "merge-overwrite", "--text") | Out-Null
    $koRelPitch = ([System.IO.File]::ReadAllText((Join-Path $koRelDir "general.pitch")) | ConvertFrom-Json).curves.rpc_pitch
    Assert-True ($koRelPitch.harmonization -eq 2 -and $koRelPitch.normalized -eq 0) ("il2korea export should write the built-in curve and control surface loading: " + ($koRelPitch | ConvertTo-Json -Compress))
    $koRelAdditive = ([System.IO.File]::ReadAllText($koRelActions) | ConvertFrom-Json).additive.rpc_all_engines_throttles
    Assert-True ($koRelAdditive.deadzone -eq 0.8 -and $koRelAdditive.sensitivity -eq 0.2) ("il2korea export should write Relative Control's dead zone and sensitivity / 100: " + ($koRelAdditive | ConvertTo-Json -Compress))

    # Binds made in the game: flaps on stick button 10 (the profile moves flaps to button 8) and the air brake on stick
    # button 11 (the profile puts "retract air brakes" there: a conflict).
    $koUser = $koActionsText.Replace("`"key_f`"`n", "`"key_f`",`n      `"dev1_b9`"`n").Replace("`"dev1_pov0_90`"`n", "`"dev1_pov0_90`",`n      `"dev1_b10`"`n")
    [System.IO.File]::WriteAllText($koActions, $koUser, (New-Object System.Text.UTF8Encoding($false)))
    $koAddFlaps = Run-Engine @("--profile", $koProfile, "relations", "add", "Korea Flaps", "--button", "--node", "IL2Korea:flaps", "--device", $koStick, "--input", "JOY_BTN8", "--text")
    Assert-True ($koAddFlaps.ExitCode -eq 0) "relations add for Korea flaps failed."
    $koAddBrake = Run-Engine @("--profile", $koProfile, "relations", "add", "Korea Airbrake In", "--button", "--node", "IL2Korea:airbrakes_off", "--device", $koStick, "--input", "JOY_BTN11", "--text")
    Assert-True ($koAddBrake.ExitCode -eq 0) "relations add for the Korea air brake failed."
    $koMerge = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge", "--dry-run", "--text")
    Assert-True ([System.IO.File]::ReadAllText($koActions) -eq $koUser) "il2korea dry run must not write."

    # Only add: removes nothing, skips the bind on the input the game uses for another action and reports it.
    $koAdd = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "add")
    Assert-True ($koAdd.ExitCode -eq 0) ("il2korea add export failed: " + $koAdd.Output)
    $koAddResult = $koAdd.Output.Substring($koAdd.Output.IndexOf('{')) | ConvertFrom-Json
    $koAddSkip = @($koAddResult.SkippedTakenInputs | Where-Object { $_.Action -eq "airbrakes_off" -and $_.GameAction -eq "airbrakes_on" -and $_.Input -eq "JOY_BTN11" })
    Assert-True ($koAddSkip.Count -eq 1) ("il2korea add should report the bind on the air brake's input as a skipped multi bind: " + $koAdd.Output)
    $koAddJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    Assert-True ((@($koAddJson.actions.flaps) -join ",") -eq "key_f,dev1_b9,dev1_b7") ("il2korea add should add the moved flaps and keep the old input: " + (@($koAddJson.actions.flaps) -join ","))
    Assert-True ((@($koAddJson.actions.airbrakes_on) -join ",") -eq "dev1_pov0_90,dev1_b10") "il2korea add must not remove the game's air brake bind."
    Assert-True (-not (@($koAddJson.actions.airbrakes_off) -contains "dev1_b10")) "il2korea add must not write a multi bind."
    Assert-True ((@($koAddJson.actions.rpc_yaw) -join ",") -eq "key_x/key_z,dev3_axis_t") "il2korea add must keep the device that is not connected."

    # Merge: removes the conflicting game bind, keeps flaps' old input.
    [System.IO.File]::WriteAllText($koActions, $koUser, (New-Object System.Text.UTF8Encoding($false)))
    $koMerge = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge", "--text")
    Assert-True ($koMerge.ExitCode -eq 0) ("il2korea merge export failed: " + $koMerge.Output)
    $koMergeJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    Assert-True ((@($koMergeJson.actions.airbrakes_on) -join ",") -eq "dev1_pov0_90") "il2korea merge should remove the game's bind on the input the profile gives another action."
    Assert-True ((@($koMergeJson.actions.airbrakes_off) -contains "dev1_b10")) "il2korea merge should write the profile's bind on that input."
    Assert-True ((@($koMergeJson.actions.flaps) -contains "dev1_b9") -and (@($koMergeJson.actions.flaps) -contains "dev1_b7")) "il2korea merge should keep flaps' old input and add the new one."
    Assert-True ((@($koMergeJson.actions.cam_zoom) -join ",") -eq "key_add/key_subtract,mouse_axis_z") "il2korea merge must keep mouse lines and button pairs."

    # Merge-overwrite: also the moved action's old input goes; the keyboard (not exported) and other devices stay.
    [System.IO.File]::WriteAllText($koActions, $koUser, (New-Object System.Text.UTF8Encoding($false)))
    $koOver = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge-overwrite", "--text")
    Assert-True ($koOver.ExitCode -eq 0) ("il2korea merge-overwrite export failed: " + $koOver.Output)
    $koOverJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    Assert-True ((@($koOverJson.actions.flaps) -join ",") -eq "key_f,dev1_b7") ("il2korea merge-overwrite should move flaps (old stick input gone, keyboard kept): " + (@($koOverJson.actions.flaps) -join ","))
    Assert-True ((@($koOverJson.actions.airbrakes_on) -join ",") -eq "dev1_pov0_90") "il2korea merge-overwrite should remove the conflicting game bind."
    Assert-True ((@($koOverJson.actions.rpc_yaw) -join ",") -eq "key_x/key_z,dev3_axis_t") "il2korea merge-overwrite must keep the device that is not connected."
    Assert-True ((@($koOverJson.actions.all_guns_fire) -join ",") -eq "key_space,mouse_b0,dev1_b0") "il2korea merge-overwrite must keep keyboard and mouse inputs."
    [System.IO.File]::WriteAllText($koActions, $koUser, (New-Object System.Text.UTF8Encoding($false)))

    # The throttle has no device number yet: JoyPro gives it the lowest free one (2) in known.devices.json.
    $koAddGear = Run-Engine @("--profile", $koProfile, "relations", "add", "Korea Gear Throttle", "--button", "--node", "IL2Korea:gear_up", "--device", $koThrottle, "--input", "JOY_BTN4", "--text")
    Assert-True ($koAddGear.ExitCode -eq 0) "relations add for Korea gear up on the throttle failed."
    $koExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
    Assert-True ($koExport.ExitCode -eq 0) "il2korea merge-overwrite export failed."
    Assert-Match $koExport.Output "device number 2" "il2korea export should report the device number it gave the throttle."
    $koAfter = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    $koKnownAfter = [System.IO.File]::ReadAllText($koKnown) | ConvertFrom-Json
    Assert-True ($koKnownAfter.knownDevices."66666666-7777-8888-0000000000009999".deviceId -eq 2) "il2korea export should number the throttle in known.devices.json."
    Assert-True (@($koAfter.devices | Where-Object { $_.deviceId -eq 2 -and $_.ident -eq "abcd_0001" }).Count -eq 1) "il2korea export should list the throttle under devices."
    Assert-True ((@($koAfter.actions.gear_up) -contains "dev2_b3")) "il2korea export should write the throttle's button."
    Assert-True ((@($koAfter.actions.flaps) -contains "dev1_b7") -and -not (@($koAfter.actions.flaps) -contains "dev1_b9")) "il2korea merge-overwrite should move flaps off the game's old input."
    Assert-True ((@($koAfter.actions.rpc_pitch) -contains "-dev1_axis_y") -and (@($koAfter.actions.rpc_pitch) -contains "key_down/key_up")) "il2korea export must keep inverted axes and button pairs."
    Assert-True (Test-Path ($koActions + ".jpbak")) "il2korea export should back up the actions file."

    # Clean takes over the devices it writes and keeps the keyboard; nuke also clears connected devices the profile does
    # not use, never one that is not connected (Old Pedals, device 3).
    $koClearGuns = Run-Engine @("--profile", $koProfile, "binds", "clear", "Fire all guns", "--text")
    Assert-True ($koClearGuns.ExitCode -eq 0) ("binds clear on the imported Korea guns relation failed: " + $koClearGuns.Output)
    $koClean = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "clean", "--text")
    Assert-True ($koClean.ExitCode -eq 0) "il2korea clean export failed."
    $koCleanJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    Assert-True ((@($koCleanJson.actions.all_guns_fire) -join ",") -eq "key_space,mouse_b0") "il2korea clean should release unbound inputs of written devices and keep keyboard and mouse."
    Assert-True ((@($koCleanJson.actions.rpc_yaw) -contains "dev3_axis_t")) "il2korea clean without nuke must keep devices JoyPro does not write."
    $koNuke = Run-Engine @("--profile", $koProfile, "binds", "clear", "Yaw (Rudder)", "--text")
    $koNuke = Run-Engine @("--profile", $koProfile, "binds", "clear", "Korea Gear Throttle", "--text")
    Assert-True ($koNuke.ExitCode -eq 0) ("binds clear on the Korea throttle relation failed: " + $koNuke.Output)
    [System.IO.File]::WriteAllText($koActions, ([System.IO.File]::ReadAllText($koActions).Replace("`"flaps`": [", "`"landing_lights`": [`n      `"dev2_b6`"`n    ],`n    `"flaps`": [")), (New-Object System.Text.UTF8Encoding($false)))
    $koNuke = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($koNuke.ExitCode -eq 0) "il2korea nuke export failed."
    $koNukeJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
    Assert-True ((@($koNukeJson.actions.rpc_yaw) -join ",") -eq "key_x/key_z,dev3_axis_t") "il2korea nuke must keep a device that is not connected and the keyboard."
    Assert-True (@($koNukeJson.actions.landing_lights).Count -eq 0 -and -not (@($koNukeJson.actions.gear_up) -contains "dev2_b3")) "il2korea nuke should clear the connected throttle the profile does not use."
    Assert-True ((@($koNukeJson.actions.gear) -contains "key_g")) "il2korea nuke must keep keyboard inputs."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
}
