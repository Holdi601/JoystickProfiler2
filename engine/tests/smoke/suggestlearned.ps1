# What relations suggest learned from the 2026-11-04 profile rework (docs/suggest-additions.md):
#   sister games    - the same command in MSFS 2020/2024 and FSX/Prepar3D (KEY_AP_MASTER = AP_MASTER), X-Plane 11/12;
#   pan / tilt      - "Pan left" (FSX, Cliffs of Dover, Nuclear Option) is a look left, not the pan axis; "View pan
#                     pitch axis" is the vertical look axis;
#   toggle stand-in - a game without "stop" gets its engine toggle into "LEFT engine STOP" (medium);
#   learned tables  - the catalog rows of the reworked profile (Canopy close holds Cliffs of Dover's canopy toggle);
#   style per game  - a propeller-pitch relation holding two games' throttle-up buttons gets Falcon BMS's throttle up,
#                     not for a game that has a propeller control of its own;
#   function map    - autopilot vertical speed, the G1000 PFD softkeys (X-Plane's second unit is the MFD);
#   item add        - a game without aircraft gets the game itself as its aircraft (no "<none>").
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir); needs the development game lists.

$slDir = Join-Path $tempDir "suggestlearned"
$slData = Join-Path $slDir "data"
New-Item -Path $slData -ItemType Directory -Force | Out-Null
$slPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = $slData

function Invoke-SlJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $slDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

function Get-SlSuggestion($Result, [string]$Game, [string]$Id) {
    return @($Result.Suggestions | Where-Object { $_.Game -eq $Game -and $_.Id -eq $Id }) | Select-Object -First 1
}

try {
    $slProfile = Join-Path $slDir "learned.pr0file"
    Assert-True ((Run-Engine @("profile", "new", $slProfile, "--text")).ExitCode -eq 0) "profile new failed."
    $slSeeds = @(
        @("Autopilot - Master", "--button", "MSFS2024:KEY_AP_MASTER:MSFS2024"),
        @("Look Left", "--button", "Arma2:LookLeft:Arma2"),
        @("Look Vertical", "--axis", "Arma2:Axis.LookVertical:Arma2"),
        @("Canopy close", "--button", "VTOLVR:Canopy Close:VTOLVR"),
        @("LEFT engine STOP", "--button", "VTOLVR:Engine Left Off:VTOLVR"),
        @("Autopilot - VS Up", "--button", "MSFS2024:KEY_AP_VS_VAR_INC:MSFS2024"),
        @("G1000 PFD SK3", "--button", "XPlane12:sim/GPS/g1000n1_softkey3:XPlane12")
    )
    foreach ($seed in $slSeeds) {
        $add = Run-Engine @("--profile", $slProfile, "relations", "add", $seed[0], $seed[1], "--node", $seed[2], "--text")
        Assert-True ($add.ExitCode -eq 0) ("relations add " + $seed[0] + " failed: " + $add.Output)
    }

    # Sister games: the same SimConnect event in MSFS 2020 and FSX / Prepar3D ("KEY_" dropped), high.
    $slAp = Invoke-SlJson $slProfile @("relations", "suggest", "Autopilot - Master")
    foreach ($pair in @(@("MSFS2020", "KEY_AP_MASTER"), @("FSX", "AP_MASTER"), @("P3D6", "AP_MASTER"))) {
        $s = Get-SlSuggestion $slAp $pair[0] $pair[1]
        Assert-True ($null -ne $s) ("suggest did not propose the sister game's " + $pair[0] + ":" + $pair[1] + " for MSFS 2024's KEY_AP_MASTER.")
        Assert-True ($s.Confidence -eq "high") ($pair[0] + ":" + $pair[1] + " (same command in a sister game) should be high.")
    }
    Assert-Match ((Get-SlSuggestion $slAp "FSX" "AP_MASTER").Reasons -join " ") "same simulator family" "the sister-game reason is missing."

    # Pan / tilt: a named direction beats the pan axis word.
    $slLook = Invoke-SlJson $slProfile @("relations", "suggest", "Look Left")
    foreach ($pair in @(@("FSX", "PAN_LEFT"), @("IL2CoD", "viewPan.PanLeft"), @("NuclearOption", "Pan View.Negative"))) {
        Assert-True ($null -ne (Get-SlSuggestion $slLook $pair[0] $pair[1])) ("Look Left did not get " + $pair[0] + ":" + $pair[1] + ".")
    }
    foreach ($pair in @(@("FSX", "PAN_RIGHT"), @("IL2CoD", "viewPan.PanRight"), @("NuclearOption", "Pan View.Positive"), @("FSX", "PAN_UP"))) {
        Assert-True ($null -eq (Get-SlSuggestion $slLook $pair[0] $pair[1])) ("Look Left must not get " + $pair[0] + ":" + $pair[1] + ".")
    }
    $slVertical = Invoke-SlJson $slProfile @("relations", "suggest", "Look Vertical")
    Assert-True ($null -ne (Get-SlSuggestion $slVertical "FSX" "AXIS_PAN_PITCH")) "Look Vertical did not get FSX's 'View pan pitch axis'."
    Assert-True ($null -eq (Get-SlSuggestion $slVertical "FSX" "AXIS_PAN_HEADING")) "Look Vertical must not get FSX's 'View pan heading axis'."

    # Toggle stand-in: IL-2 1946 has only engine toggles ("Toggle all engines"), so one goes into "LEFT engine STOP" too (medium,
    # with the reason; the reworked profile has it in START, a catalog row); never for DCS. Cliffs of Dover's canopy toggle is a
    # catalog row of "Canopy close" since the 2026-11-04 profile rework.
    $slStop = Invoke-SlJson $slProfile @("relations", "suggest", "LEFT engine STOP")
    $slToggle = @($slStop.Suggestions | Where-Object { $_.Game -eq "IL21946" -and $_.Source -eq "toggle stand-in" }) | Select-Object -First 1
    Assert-True ($null -ne $slToggle) "LEFT engine STOP did not get IL-2 1946's engine toggle (the game has no stop)."
    Assert-True ($slToggle.Confidence -eq "medium") "a toggle standing in for a direction should be medium."
    Assert-Match ($slToggle.Reasons -join " ") "its toggle does it" "the toggle stand-in reason is missing."
    Assert-True (@($slStop.Suggestions | Where-Object { $_.Game -eq "DCS" -and $_.Source -eq "toggle stand-in" }).Count -eq 0) "a DCS toggle must not stand in for a direction."
    $slCanopy = Invoke-SlJson $slProfile @("relations", "suggest", "Canopy close")
    Assert-True ($null -ne (Get-SlSuggestion $slCanopy "IL2CoD" "common.door_toggle")) "Canopy close did not get Cliffs of Dover's canopy toggle."

    # Function map additions: autopilot vertical speed across sims, the G1000 PFD softkeys (X-Plane's second unit = MFD).
    $slVs = Invoke-SlJson $slProfile @("relations", "suggest", "Autopilot - VS Up")
    Assert-True ($null -ne (Get-SlSuggestion $slVs "XPlane12" "sim/autopilot/vertical_speed_up")) "Autopilot - VS Up did not get X-Plane's 'Autopilot VVI up'."
    Assert-True ($null -ne (Get-SlSuggestion $slVs "FSX" "AP_VS_VAR_INC")) "Autopilot - VS Up did not get FSX's 'Selected vertical speed up'."
    Assert-True ($null -eq (Get-SlSuggestion $slVs "XPlane12" "sim/autopilot/vertical_speed_down")) "Autopilot - VS Up must not get the VVI down."
    $slPfd = Invoke-SlJson $slProfile @("relations", "suggest", "G1000 PFD SK3", "--min", "medium")
    Assert-True (@($slPfd.MapFunctions | Where-Object { $_.Id -eq "pfd_softkey" -and $_.Dir -eq "3" }).Count -ge 1) "X-Plane's 'G1000 pilot PFD Softkey 3' was not read as PFD softkey 3."
    Assert-True ($null -ne (Get-SlSuggestion $slPfd "XPlane11" "sim/GPS/g1000n1_softkey3")) "the PFD softkey did not go to X-Plane 11."
    Assert-True ($null -eq (Get-SlSuggestion $slPfd "XPlane12" "sim/GPS/g1000n2_softkey3")) "the copilot unit (MFD) softkey must not go into a PFD relation."

    # Style per game: "Prop fine" is propeller pitch up (X-Plane), and in two games (VTOL VR, Tiny Combat Arena) the
    # throttle-up button: games without a propeller control get their throttle up too (Falcon BMS), a game with one of
    # its own (Cliffs of Dover) does not.
    $slRpm = Run-Engine @("--profile", $slProfile, "relations", "add", "Prop fine", "--button", "--node", "XPlane12:sim/engines/prop_up_1:XPlane12",
        "--node", "XPlane12:sim/engines/prop_up_2:XPlane12", "--node", "XPlane11:sim/engines/prop_up_1:XPlane11",
        "--node", "VTOLVR:Collective Up:VTOLVR", "--node", "TinyCombatArena:ThrottleAxis.Positive:TinyCombatArena", "--text")
    Assert-True ($slRpm.ExitCode -eq 0) ("relations add Prop fine failed: " + $slRpm.Output)
    $slRpmSuggest = Invoke-SlJson $slProfile @("relations", "suggest", "Prop fine")
    $slBms = Get-SlSuggestion $slRpmSuggest "FalconBMS" "AFThrottleUp"
    Assert-True ($null -ne $slBms) "Prop fine (throttle up in VTOL VR and Tiny Combat Arena) did not get Falcon BMS's throttle up."
    Assert-True ($slBms.Confidence -eq "medium") "a style-per-game suggestion should be medium."
    Assert-Match ($slBms.Reasons -join " ") "Your style: in " "the style-per-game reason is missing."
    Assert-True ($null -ne (Get-SlSuggestion $slRpmSuggest "Arma2" "HeliUp")) "Prop fine did not get Arma 2's collective up."
    Assert-True ($null -eq (Get-SlSuggestion $slRpmSuggest "IL2CoD" "pilot.thtl_plus")) "a game with its own propeller control must get that, not its throttle."

    # relations item add / relations add of a game without aircraft: the game is the aircraft.
    $slItem = Run-Engine @("--profile", $slProfile, "relations", "item", "add", "Canopy close", "--node", "WarThunder:ID_TOGGLE_COCKPIT_DOOR", "--force", "--text")
    Assert-True ($slItem.ExitCode -eq 0) ("relations item add without an aircraft failed: " + $slItem.Output)
    Assert-Match $slItem.Output "game=WarThunder, id=ID_TOGGLE_COCKPIT_DOOR, aircraft=WarThunder" "relations item add gave a game without aircraft no aircraft."
    Assert-NotMatch $slItem.Output "aircraft=<none>" "relations item add left an action with no aircraft."
    $slNew = Run-Engine @("--profile", $slProfile, "relations", "add", "Gear one game", "--button", "--node", "XPlane12:sim/flight_controls/landing_gear_up", "--text")
    Assert-True ($slNew.ExitCode -eq 0) ("relations add without an aircraft failed: " + $slNew.Output)
    Assert-Match $slNew.Output "game=XPlane12, id=sim/flight_controls/landing_gear_up, aircraft=XPlane12" "relations add gave a game without aircraft no aircraft."
    $slShow = Run-Engine @("--profile", $slProfile, "relations", "show", "Canopy close", "--text")
    Assert-Match $slShow.Output "aircraft=WarThunder" "the added action lost its aircraft after the profile was opened again."
}
finally {
    $env:JOYPRO_DATA_DIR = $slPreviousData
}
