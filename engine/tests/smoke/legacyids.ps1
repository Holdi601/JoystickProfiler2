# Profiles saved before MSFS and X-Plane became one game per version (engine\Games\LegacyGameIds.cs). Fixture
# fixtures\legacy-ids\legacy-ids.pr0file was written by the engine of commit 70b83ec (single games "MSFS" and
# "XPlane", Hell Let Loose: Vietnam still supported):
#   Legacy Pitch         axis    XPlane:axis/pitch, MSFS:KEY_AXIS_ELEVATOR_SET; tuning game:XPlane (3 stops), game:MSFS (dz 0.1)
#   Legacy Copilot pitch axis    XPlane:axis/copilot_pitch (an X-Plane 12 axis)
#   Legacy Gear          button  MSFS:KEY_GEAR_TOGGLE, XPlane:sim/flight_controls/landing_gear_toggle, HLLVietnam:Driver_RollLeft
#   Legacy C172 flaps    button  MSFS:KEY_FLAPS_INCR limited to asobo-aircraft-c172sp-as1000
#   ffb set --game XPlane: * pitchResponse 0.25, pitchStability 0.5, copilotRollResponse 0.75; stick controlLoaded 1
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).

$lgProfile = Join-Path $tempDir "legacy-ids.pr0file"
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\legacy-ids\legacy-ids.pr0file") -Destination $lgProfile -Force

# Nodes: one per version; X-Plane 11 only gets actions its catalog has; node state (aircraft) is kept.
$lgGear = Run-Engine @("--profile", $lgProfile, "relations", "show", "Legacy Gear", "--text")
Assert-True ($lgGear.ExitCode -eq 0) ("relations show of a legacy relation failed: " + $lgGear.Output)
Assert-Match $lgGear.Output "game=MSFS2024, id=KEY_GEAR_TOGGLE" "a legacy MSFS node should become an MSFS 2024 node."
Assert-Match $lgGear.Output "game=MSFS2020, id=KEY_GEAR_TOGGLE" "a legacy MSFS node should become an MSFS 2020 node."
Assert-Match $lgGear.Output "game=XPlane12, id=sim/flight_controls/landing_gear_toggle" "a legacy X-Plane node should become an X-Plane 12 node."
Assert-Match $lgGear.Output "game=XPlane11, id=sim/flight_controls/landing_gear_toggle" "a legacy X-Plane node should become an X-Plane 11 node (the command exists in X-Plane 11)."
Assert-NotMatch $lgGear.Output "game=MSFS,|game=XPlane," "no node may keep a legacy game id."
Assert-Match $lgGear.Output "game=HLLVietnam, id=Driver_RollLeft" "a node of a game JoyPro dropped and brought back (HLL Vietnam) stays in the profile (nothing is lost)."
$lgCopilot = Run-Engine @("--profile", $lgProfile, "relations", "show", "Legacy Copilot pitch", "--text")
Assert-Match $lgCopilot.Output "game=XPlane12, id=axis/copilot_pitch" "an X-Plane 12 axis should stay an X-Plane 12 node."
Assert-NotMatch $lgCopilot.Output "game=XPlane11" "an axis X-Plane 11 does not have must not get an X-Plane 11 node."
$lgFlaps = Run-Engine @("--profile", $lgProfile, "relations", "show", "Legacy C172 flaps", "--text")
Assert-Match $lgFlaps.Output "game=MSFS2024, id=KEY_FLAPS_INCR, aircraft=asobo-aircraft-c172sp-as1000" "an aircraft-limited MSFS node should keep its aircraft (2024)."
Assert-Match $lgFlaps.Output "game=MSFS2020, id=KEY_FLAPS_INCR, aircraft=asobo-aircraft-c172sp-as1000" "an aircraft-limited MSFS node should keep its aircraft (2020)."

# Tuning: game:XPlane / game:MSFS become the family scopes both versions read.
$lgTuning = Run-Engine @("--profile", $lgProfile, "binds", "tuning", "show", "Legacy Pitch", "--text")
Assert-Match $lgTuning.Output "family:xplane: .*n\.cpN=3;.*n\.cpy1=0\.2" ("game:XPlane tuning should move to family:xplane: " + $lgTuning.Output)
Assert-Match $lgTuning.Output "family:msfs: .*dz=0\.1" ("game:MSFS tuning should move to family:msfs: " + $lgTuning.Output)
Assert-NotMatch $lgTuning.Output "game:XPlane:|game:MSFS:" "no tuning may keep a legacy game scope."
$lgTuning11 = Run-Engine @("--profile", $lgProfile, "binds", "tuning", "show", "Legacy Pitch", "--game", "XPlane11", "--text")
Assert-Match $lgTuning11.Output "XPlane11 uses \(xplane, from family:xplane\)" ("X-Plane 11 should use the migrated tuning: " + $lgTuning11.Output)
$lgTuning2020 = Run-Engine @("--profile", $lgProfile, "binds", "tuning", "show", "Legacy Pitch", "--game", "MSFS2020", "--text")
Assert-Match $lgTuning2020.Output "MSFS2020 uses \(msfs, from family:msfs\)" ("MSFS 2020 should use the migrated tuning: " + $lgTuning2020.Output)

# Game settings: each version gets the settings its model knows.
$lgFfb = Run-Engine @("--profile", $lgProfile, "ffb", "show", "--text")
Assert-Match $lgFfb.Output "\*\|game:XPlane12: copilotRollResponse=0\.75;pitchResponse=0\.25" ("X-Plane 12 should get the response settings: " + $lgFfb.Output)
Assert-Match $lgFfb.Output "\*\|game:XPlane11: pitchResponse=0\.25;pitchStability=0\.5" ("X-Plane 11 should get the response and stability settings: " + $lgFfb.Output)
Assert-Match $lgFfb.Output "Smoke Stick \{11111111-2222-3333-4444-555555555555\}\|game:XPlane12: controlLoaded=1" ("the control-loaded flag should go to X-Plane 12: " + $lgFfb.Output)
Assert-NotMatch $lgFfb.Output "game:XPlane:|game:XPlane11: .*controlLoaded" "no setting may keep the legacy game, and X-Plane 11 has no control-loaded flag."

# The profile's games: both versions of each sim.
$lgGames = Run-Engine @("--profile", $lgProfile, "profile", "games", "--text")
Assert-Match $lgGames.Output "Microsoft Flight Simulator 2024 \[MSFS2024, export: registry\]: 3 of 3" ("profile games should list MSFS 2024: " + $lgGames.Output)
Assert-Match $lgGames.Output "Microsoft Flight Simulator 2020 \[MSFS2020, export: registry\]: 3 of 3" ("profile games should list MSFS 2020: " + $lgGames.Output)
Assert-Match $lgGames.Output "X-Plane 12 \[XPlane12, export: registry\]: 3 of 3" ("profile games should list X-Plane 12: " + $lgGames.Output)
Assert-Match $lgGames.Output "X-Plane 11 \[XPlane11, export: registry\]: 2 of 2" ("profile games should list X-Plane 11: " + $lgGames.Output)

# The old CLI names stay aliases of the newest version, and an old --node id adds a node per version.
$lgAlias = Run-Engine @("msfs", "catalog", "--text")
Assert-Match $lgAlias.Output "Microsoft Flight Simulator 2024" ("engine msfs should be MSFS 2024: " + $lgAlias.Output)
$lgAliasXp = Run-Engine @("xplane", "catalog", "--text")
Assert-Match $lgAliasXp.Output "X-Plane 12" ("engine xplane should be X-Plane 12: " + $lgAliasXp.Output)
$lgAdd = Run-Engine @("--profile", $lgProfile, "relations", "add", "Legacy Brakes", "--button", "--node", "MSFS:KEY_PARKING_BRAKES", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_BTN6", "--text")
Assert-True ($lgAdd.ExitCode -eq 0) ("relations add with an old MSFS node failed: " + $lgAdd.Output)
$lgBrakes = Run-Engine @("--profile", $lgProfile, "relations", "show", "Legacy Brakes", "--text")
Assert-Match $lgBrakes.Output "game=MSFS2024, id=KEY_PARKING_BRAKES" "--node MSFS:... should add an MSFS 2024 node."
Assert-Match $lgBrakes.Output "game=MSFS2020, id=KEY_PARKING_BRAKES" "--node MSFS:... should add an MSFS 2020 node."
$lgRemove = Run-Engine @("--profile", $lgProfile, "relations", "item", "remove", "Legacy Brakes", "--node", "MSFS:KEY_PARKING_BRAKES", "--text")
$lgBrakesAfter = Run-Engine @("--profile", $lgProfile, "relations", "show", "Legacy Brakes", "--text")
Assert-NotMatch $lgBrakesAfter.Output "game=MSFS20(20|24)" ("removing --node MSFS:... should remove the nodes of both versions: " + $lgRemove.Output + $lgBrakesAfter.Output)
