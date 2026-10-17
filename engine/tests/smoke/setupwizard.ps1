# The Setup wizard (docs/setup-wizard.md) end to end on a fake device set like a real pit
# (tests\fixtures\setupwizard\devices.tsv: VKB MCG Pro on Gunfighter, VPforce Rhino, Virpil WarBRD + Alpha-L, VPC MT-50CM
# throttle, MFG Crosswind, WinWing takeoff panel, vJoy, Wooting) with the shipped device catalog and function catalog:
# roles, the user's role choice, the aircraft list, a plan with priorities / modifiers / realism (left-stick translation,
# panel labels, no-collective rule, determinism), a fake cockpit database entry at realism 100 %, apply (one history
# version) and the callout sheets.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs the DCS game list.

$swDir = Join-Path $tempDir "setupwizard"
$swData = Join-Path $swDir "data"
New-Item -Path $swData -ItemType Directory -Force | Out-Null
$swPreviousData = $env:JOYPRO_DATA_DIR
$swPreviousDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = $swData
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\setupwizard\devices.tsv"

try {
    # Roles: one device per hand, the left stick for space sims, extras and alternatives.
    $swDevices = Run-Engine @("setup", "devices", "--text")
    Assert-True ($swDevices.ExitCode -eq 0) ("setup devices failed: " + $swDevices.Output)
    Assert-Match $swDevices.Output "VKB-Sim Gunfighter Modern Combat Pro Twist \{[^}]+\} => main-stick" "the VKB was not the main stick."
    Assert-Match $swDevices.Output "L-VPC Stick WarBRD \{[^}]+\} => left-stick" "the left WarBRD was not the left stick."
    Assert-Match $swDevices.Output "LEFT VPC Throttle MT-50 CM \{[^}]+\} => throttle" "the MT-50 was not the throttle."
    Assert-Match $swDevices.Output "MFG Crosswind V2 \{[^}]+\} => pedals" "the Crosswind was not the pedals."
    Assert-Match $swDevices.Output "WINWING F18 TAKEOFF PANEL \{[^}]+\} => panel" "the takeoff panel was not a panel."
    Assert-Match $swDevices.Output "vJoy Device \{[^}]+\} => virtual" "vJoy was not left as a virtual device."
    Assert-Match $swDevices.Output "Wooting Two HE \(ARM\) \{[^}]+\} => keyboard-joystick" "the Wooting was not the keyboard joystick extra."
    Assert-Match $swDevices.Output "Rhino FFB Joystick \{[^}]+\} => unused .*grip needed" "the second right-hand stick (Rhino) was not an alternative."
    Assert-Match $swDevices.Output "Helicopters: no collective: the collective goes on the throttle axis" "the no-collective rule is missing."
    Assert-Match $swDevices.Output "Space sims: translation on the left stick" "the left-stick translation rule is missing."

    $swRole = Run-Engine @("setup", "devices", "--role", "Rhino FFB Joystick=heli-stick", "--grip", "Rhino FFB Joystick=vpforce-rhino-tm-f16-grip", "--text")
    Assert-Match $swRole.Output "Rhino FFB Joystick \{[^}]+\} => heli-stick" "the user's role choice was not taken."
    Assert-Match $swRole.Output "except helicopters" "the helicopter stick rule is missing."
    $swBadRole = Run-Engine @("setup", "devices", "--role", "Rhino FFB Joystick=co-pilot", "--text")
    Assert-Match $swBadRole.Output "Unknown role 'co-pilot'" "an unknown role was not reported."

    $swAircraft = Run-Engine @("setup", "aircraft", "--games", "DCS,StarCitizen", "--text")
    Assert-True ($swAircraft.ExitCode -eq 0) ("setup aircraft failed: " + $swAircraft.Output)
    Assert-Match $swAircraft.Output "(?m)^  - F-16C_50 \| DCS \| Fixed wing" "setup aircraft lacks the F-16."
    Assert-Match $swAircraft.Output "(?m)^  - StarCitizen \| StarCitizen \| Spaceships" "setup aircraft lacks Star Citizen."
    Assert-True ((Run-Engine @("setup", "aircraft")).ExitCode -ne 0) "setup aircraft without --games did not fail."

    # The plan (realism 30 %, F-16 first, F/A-18 and A-10C II equal, then Star Citizen, one modifier, as few as possible).
    $swPlanFile = Join-Path $swDir "plan.json"
    $swPlanArgs = @("setup", "plan", "--games", "DCS,StarCitizen", "--priorities", "F-16C_50>FA-18C_hornet|A-10C II>StarCitizen", "--modifiers", "1", "--prefer-fewer", "--realism", "0.3")
    $swPlan = Run-Engine ($swPlanArgs + @("--out-plan", $swPlanFile, "--text"))
    Assert-True ($swPlan.ExitCode -eq 0) ("setup plan failed: " + $swPlan.Output)
    Assert-True (Test-Path $swPlanFile) "setup plan --out-plan wrote no plan."
    Assert-Match $swPlan.Output "(?m)^Priorities: F-16C_50 > FA-18C_hornet = A-10C II > StarCitizen" "the priority list was not read with the equal tier."
    Assert-Match $swPlan.Output "JOY_X \(stick\.axis\.roll\) => SPACE_STRAFE_HOR .*translation on the left stick" "space strafe left/right is not on the left stick's X."
    Assert-Match $swPlan.Output "JOY_Y \(stick\.axis\.pitch\) => SPACE_STRAFE_FWDAFT" "space strafe forward/back is not on the left stick's Y."
    Assert-Match $swPlan.Output "JOY_Z \(stick\.axis\.twist\) => SPACE_STRAFE_VER" "space strafe up/down is not on the left stick's twist."
    Assert-Match $swPlan.Output "JOY_Y \(stick\.axis\.pitch\) => Pitch \|" "Pitch is not on a stick's pitch axis."
    Assert-Match $swPlan.Output "\(pedals\.axis\.rudder\) => Rudder" "Rudder is not on the pedals."
    Assert-Match $swPlan.Output "`"LDG GEAR UP`"\) => GearIn" "gear up did not follow the panel label."
    Assert-Match $swPlan.Output "`"ANTI SKID ON`"\) => AntiSkidOn" "anti skid did not follow the panel label."
    Assert-Match $swPlan.Output "(?m)^  - F-16C_50 \[priority 1\]: \d" "the F-16's quality line is missing."
    Assert-NotMatch $swPlan.Output "vJoy Device \{[^}]+\}:|Wooting Two HE \(ARM\) \{[^}]+\}:" "the plan bound a virtual or keyboard device."
    $swPlanJson = [IO.File]::ReadAllText($swPlanFile) | ConvertFrom-Json
    $swCollective = @($swPlanJson.Relations | Where-Object { $_.Function -eq "throttleleftnonab" -and $_.Device -ne "" })
    Assert-True ($swCollective.Count -gt 0 -and $swCollective[0].Device -like "LEFT VPC Throttle*") "without a collective the helicopters' collective is not on the throttle."
    $swUsed = @($swPlanJson.Relations | Where-Object { $_.Device -ne "" -and -not $_.Kept }).Count
    Assert-True ($swUsed -gt 200) ("the plan placed only " + $swUsed + " functions.")

    # Deterministic: the same plan again.
    $swPlanFile2 = Join-Path $swDir "plan2.json"
    Run-Engine ($swPlanArgs + @("--out-plan", $swPlanFile2)) | Out-Null
    Assert-True ([IO.File]::ReadAllText($swPlanFile) -eq [IO.File]::ReadAllText($swPlanFile2)) "two plans with the same choices differ."

    # Realism 100 % with the cockpit database (DB\Cockpits, docs/cockpits.md) and an entry the caller passes (the answer
    # of "engine cockpits get", here the flat form; it wins for its aircraft): the TMS on the real TMS hat.
    $swCockpit = Join-Path $swDir "cockpit-f16.json"
    '{ "aircraft": "F-16C_50", "positions": [ { "control": "stick.hat.tms.up", "functions": ["target_management_system_up"] } ] }' | Set-Content -Path $swCockpit -Encoding UTF8
    $swRealFile = Join-Path $swDir "plan-real.json"
    $swReal = Run-Engine @("setup", "plan", "--games", "DCS", "--priorities", "F-16C_50", "--realism", "1", "--cockpit-file", $swCockpit, "--out-plan", $swRealFile, "--text")
    Assert-True ($swReal.ExitCode -eq 0) ("setup plan with a cockpit entry failed: " + $swReal.Output)
    Assert-Match $swReal.Output "Cockpit positions: cockpit database: cockpit-f16, DB\\Cockpits" "the cockpit entry and the cockpit database were not used."
    $swRealJson = [IO.File]::ReadAllText($swRealFile) | ConvertFrom-Json
    $swTms = @($swRealJson.Relations | Where-Object { $_.Function -eq "target_management_system_up" -and ($_.Aircraft -contains "F-16C_50") })
    Assert-True ($swTms.Count -eq 1 -and $swTms[0].Control -eq "stick.hat.tms.up" -and $swTms[0].Reason -match "real aircraft|its group") "the F-16's TMS up is not on its real position."
    Assert-True ($swTms[0].Aircraft -contains "A-10C II") "the A-10C II's TMS (from the cockpit database) does not share the real TMS hat."
    # The answer of "engine cockpits get" itself is read too (joypro-cockpit-layout).
    $swLayout = Join-Path $swDir "cockpit-get-f16.json"
    (Run-Engine @("cockpits", "get", "F-16C_50", "--json")).Output | Set-Content -Path $swLayout -Encoding UTF8
    $swGet = Run-Engine @("setup", "plan", "--games", "DCS", "--priorities", "F-16C_50", "--realism", "1", "--cockpit-file", $swLayout, "--text")
    Assert-Match $swGet.Output "Cockpit positions: cockpit database: cockpit-get-f16, DB\\Cockpits" "the answer of cockpits get was not read."
    $swGrouped = @([IO.File]::ReadAllText($swPlanFile) | ConvertFrom-Json | ForEach-Object { $_.Relations } | Where-Object { $_.Function -eq "laser_1nd_detent" -and $_.Device -ne "" })
    Assert-True ($swGrouped.Count -eq 1) "at realism 30 % the laser (1st detent) is not one relation for all aircraft."

    # Bad options.
    Assert-True ((Run-Engine @("setup", "plan", "--games", "DCS", "--realism", "2x")).ExitCode -ne 0) "setup plan accepted a bad realism."
    Assert-True ((Run-Engine @("setup", "plan", "--modifiers", "1")).ExitCode -ne 0) "setup plan without --games did not fail."

    # Apply: relations, binds and one history version; the plan made again keeps them.
    $swProfile = Join-Path $swDir "wizard.pr0file"
    $swApply = Run-Engine @("--profile", $swProfile, "setup", "apply", "--plan", $swPlanFile, "--text")
    Assert-True ($swApply.ExitCode -eq 0) ("setup apply failed: " + $swApply.Output)
    Assert-Match $swApply.Output "(?m)^Binds Set: [1-9]\d+" "setup apply set no binds."
    $swBinds = Run-Engine @("--profile", $swProfile, "binds", "list", "--text")
    Assert-Match $swBinds.Output "(?m)^SPACE_STRAFE_HOR \| type=axis \| device=L-VPC Stick WarBRD \{[^}]+\} \| input=JOY_X" "the applied plan lacks the left stick's strafe bind."
    $swHistory = Run-Engine @("--profile", $swProfile, "profile", "history", "list", "--text")
    Assert-Match $swHistory.Output "Setup wizard: \d+ relations, \d+ binds" "the apply is not one named history version."
    $swAgain = Run-Engine (@("--profile", $swProfile) + $swPlanArgs + @("--text"))
    Assert-Match $swAgain.Output "(?m)^Kept Binds: [1-9]\d+" "a second plan did not keep the applied binds."
    $swDry = Run-Engine @("--profile", $swProfile, "setup", "apply", "--plan", $swPlanFile, "--dry-run", "--text")
    Assert-Match $swDry.Output "(?m)^Dry Run: True" "setup apply --dry-run did not say so."

    # A plan that puts two functions of one aircraft on one input is refused.
    $swBad = [IO.File]::ReadAllText($swPlanFile) | ConvertFrom-Json
    $swPlaced = @($swBad.Relations | Where-Object { $_.Device -ne "" -and -not $_.Kept -and -not $_.IsAxis -and ($_.Aircraft -contains "F-16C_50") })
    $swPlaced[1].Device = $swPlaced[0].Device
    $swPlaced[1].Input = $swPlaced[0].Input
    $swPlaced[1].Modifier = $swPlaced[0].Modifier
    $swBadFile = Join-Path $swDir "plan-bad.json"
    ($swBad | ConvertTo-Json -Depth 8) | Set-Content -Path $swBadFile -Encoding UTF8
    $swBadApply = Run-Engine @("--profile", (Join-Path $swDir "bad.pr0file"), "setup", "apply", "--plan", $swBadFile, "--text")
    Assert-True ($swBadApply.ExitCode -ne 0) "setup apply accepted two functions of the F-16 on one input."

    # Callout sheets of the plan for the F-16.
    $swSheets = Run-Engine @("setup", "render", "--plan", $swPlanFile, "--out-dir", (Join-Path $swDir "sheets"), "--aircraft", "F-16C_50", "--width", "900", "--text")
    Assert-True ($swSheets.ExitCode -eq 0) ("setup render failed: " + $swSheets.Output)
    Assert-Match $swSheets.Output "(?m)^Setup Sheets: [3-9]" "setup render drew too few device sheets."
    Assert-True (@(Get-ChildItem (Join-Path $swDir "sheets") -Filter "*.png").Count -ge 3) "setup render wrote no pictures."
}
finally {
    $env:JOYPRO_DATA_DIR = $swPreviousData
    $env:JOYPRO_FAKE_DEVICES = $swPreviousDevices
}
