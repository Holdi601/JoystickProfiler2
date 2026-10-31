# IL-2 Sturmovik: 1946 (engine il21946): settings.ini binds, conf.ini axis response, paths from Users\all.ini, refresh-db,
# scan/import/export modes. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).
# Fixture: synthetic content in the shape Steam's 4.13.4m writes (verified in the game 2026-10-12, docs/games/il2-1946.md:
# blank lines first, the game's section order, default axes device first, binds made in the game axis first, default.ini
# with blanks around '=', conf.ini with the old 11-point keys next to the 13-value ones), copied to the temp folder;
# three fake devices = JoystickDevice0..2, JoystickDevice3 is not connected.
$i46Game = Join-Path $tempDir "il21946\IL 2 Sturmovik 1946"
New-Item -Path $i46Game -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il21946\game\*") -Destination $i46Game -Recurse -Force
$i46Settings = Join-Path $i46Game "Users\doe\settings.ini"
$i46Conf = Join-Path $i46Game "conf.ini"
$i46SettingsOriginal = [System.IO.File]::ReadAllText($i46Settings)
$i46ConfOriginal = [System.IO.File]::ReadAllText($i46Conf)
$i46Profile = Join-Path $tempDir "il21946.pr0file"
$i46Stick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
# Refresh-db writes the command list into the game-data folder (<data>\GameData): a temp program root (JoyPro's own DB
# folders only) and a temp data folder, so neither the development DB nor the other smoke parts see test entries.
$i46Root = Join-Path $tempDir "il21946-root"
New-Item -Path (Join-Path $i46Root "DB") -ItemType Directory -Force | Out-Null
foreach ($own in @("Functions", "Devices", "Llm")) {
    Copy-Item -Path (Join-Path $engineBin ("DB\" + $own)) -Destination (Join-Path $i46Root "DB") -Recurse -Force
}
$previousI46Fake = $env:JOYPRO_FAKE_DEVICES
$previousI46Data = $env:JOYPRO_DATA_DIR
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\il21946\devices.tsv"
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "il21946-data"
Push-Location $i46Root
try {
    $i46List = Run-Engine @("games", "integrations", "--text")
    Assert-Match $i46List.Output "IL21946" "games integrations should list IL-2 1946."

    # The pilot's settings.ini comes from Users\all.ini ([current] 0 = doe).
    $i46Set = Run-Engine @("games", "install", "set", "IL21946", $i46Game, "--text")
    Assert-True ($i46Set.ExitCode -eq 0) ("games install set IL21946 failed: " + $i46Set.Output)
    $i46Paths = Run-Engine @("il21946", "paths", "--text")
    Assert-Match $i46Paths.Output ([regex]::Escape("Users\doe\settings.ini")) ("il21946 paths should find the current pilot's settings.ini: " + $i46Paths.Output)

    # Catalog: JoyPro's own list plus the commands the game's files name.
    $i46Refresh = Run-Engine @("il21946", "refresh-db", "--path", $i46Game, "--text")
    Assert-True ($i46Refresh.ExitCode -eq 0) ("il21946 refresh-db failed: " + $i46Refresh.Output)
    $i46Catalog = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\IL21946\actions.tsv")).Replace("`r`n", "`n")
    Assert-True (-not (Test-Path (Join-Path $i46Root "DB\IL21946"))) "il21946 refresh-db must not write into the program folder."
    Assert-Match $i46Catalog "(?m)^move\.aileron\tAilerons \(roll\)\taxis\tAxes" "the IL-2 1946 catalog should have JoyPro's aileron axis."
    Assert-Match $i46Catalog "(?m)^pilot\.SmokeTemplateCommand\t" "refresh-db should add commands named by Users\default.ini."
    Assert-Match $i46Catalog "(?m)^move\.SmokeTemplateAxis\t[^\t]+\taxis\t" "a command of [HotKey move] is an axis."

    # Scan: axes, buttons, hats (pov number = device * 4 + hat), keys with modifiers; a missing device is reported.
    $i46Scan = Run-Engine @("il21946", "scan", $i46Settings, "--text")
    Assert-True ($i46Scan.ExitCode -eq 0) ("il21946 scan failed: " + $i46Scan.Output)
    Assert-Match $i46Scan.Output ([regex]::Escape("move.power <- Smoke Throttle {66666666-7777-8888-9999-000000000000} JOY_Z")) "scan should read the inverted throttle axis on JoystickDevice1."
    Assert-Match $i46Scan.Output ([regex]::Escape("pilot.AIRCRAFT_TRIM_R_PLUS <- " + $i46Stick + " JOY_BTN_POV1_R")) "scan should read Pov90 JoystickPov0 as hat 1 right."
    Assert-Match $i46Scan.Output "pilot\.AIRCRAFT_DROP_TANKS <- Keyboard T \+ Keyboard LeftControl" "scan should read 'Ctrl T' as T with LeftControl."
    Assert-Match $i46Scan.Output "JoystickDevice3 \(Windows device number 4\) is not connected" "scan should name the device that is not connected."
    Assert-NotMatch $i46Scan.Output "MouseAXE" "mouse inputs are not JoyPro inputs."

    # Import: relations named after the catalog, conf.ini values as game:IL21946 tuning (not the base).
    $i46Import = Run-Engine @("--profile", $i46Profile, "il21946", "import", $i46Settings, "--include-keyboard", "--text")
    Assert-True ($i46Import.ExitCode -eq 0) ("il21946 import failed: " + $i46Import.Output)
    $i46Elevator = Run-Engine @("--profile", $i46Profile, "binds", "tuning", "show", "Elevator (pitch)", "--game", "IL21946", "--text")
    Assert-Match $i46Elevator.Output "game:IL21946: .*n\.c1=90;.*n\.deadBand=20;n\.filter=30" ("import should store conf.ini 1Y as the elevator's IL-2 1946 tuning: " + $i46Elevator.Output)
    Assert-Match $i46Elevator.Output "(?m)^\s*base: inv=0;sl=0;dz=0;" "import must not put the game's dead band into the base."
    $i46Power = Run-Engine @("--profile", $i46Profile, "binds", "tuning", "show", "Throttle", "--game", "IL21946", "--text")
    Assert-Match $i46Power.Output "game:IL21946: inv=1;sl=1;.*n\.oneSided=2" ("import should read '-power' with 1Z1 one-sided 2 as an inverted one-sided axis: " + $i46Power.Output)
    $i46Preview = Run-Engine @("--profile", $i46Profile, "binds", "tuning", "preview", "Rudder (yaw)", "--game", "IL21946", "--points", "5", "--text")
    Assert-Match $i46Preview.Output "Model: il21946" "the IL-2 1946 preview should use the il21946 model."
    # Rudder 1RZ2: point 5 = 90 % of linear at 50 % stick -> 0.45.
    Assert-Match $i46Preview.Output "\+0\.500 -> \+0\.450000" ("the IL-2 1946 curve is percent of linear: " + $i46Preview.Output)

    # Round trip: nothing changes.
    $i46Round = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--include-keyboard", "--text")
    Assert-True ($i46Round.ExitCode -eq 0) ("il21946 export after import failed: " + $i46Round.Output)
    Assert-True ([System.IO.File]::ReadAllText($i46Settings) -eq $i46SettingsOriginal) ("il21946 import/export must leave settings.ini as it was: " + $i46Round.Output)
    Assert-True ([System.IO.File]::ReadAllText($i46Conf) -eq $i46ConfOriginal) ("il21946 import/export must leave conf.ini as it was: " + $i46Round.Output)

    # Binds made in the game after the import: the engine on stick button 7, the air brake on stick button 8. The profile
    # puts the hook on button 7 (a conflict) and moves the air brake to button 4.
    $i46Game2 = $i46SettingsOriginal.Replace("Joystick1 JoystickDevice0=Gear", "Joystick1 JoystickDevice0=Gear`r`nJoystick6 JoystickDevice0=AIRCRAFT_TOGGLE_ENGINE`r`nJoystick7 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE")
    [System.IO.File]::WriteAllText($i46Settings, $i46Game2)
    Run-Engine @("--profile", $i46Profile, "relations", "add", "I46 Airbrake", "--button", "--node", "IL21946:pilot.AIRCRAFT_TOGGLE_AIRBRAKE", "--device", $i46Stick, "--input", "JOY_BTN4", "--text") | Out-Null
    Run-Engine @("--profile", $i46Profile, "relations", "add", "A46 Hook", "--button", "--node", "IL21946:pilot.AIRCRAFT_CARRIERHOOK", "--device", $i46Stick, "--input", "JOY_BTN7", "--text") | Out-Null
    Run-Engine @("--profile", $i46Profile, "relations", "add", "I46 Mixture", "--axis", "--node", "IL21946:move.mixture", "--device", "Smoke Throttle {66666666-7777-8888-9999-000000000000}", "--input", "JOY_RZ", "--text") | Out-Null
    $i46Dry = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--mode", "merge", "--dry-run", "--text")
    Assert-Match $i46Dry.Output "Dry run: \d+ line\(s\) of settings\.ini would change" ("il21946 dry run should report the changes: " + $i46Dry.Output)
    Assert-True ([System.IO.File]::ReadAllText($i46Settings) -eq $i46Game2) "il21946 dry run must not write."

    # Only add: removes nothing, skips the hook on the engine's input and reports it.
    $i46Add = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--mode", "add")
    Assert-True ($i46Add.ExitCode -eq 0) ("il21946 add export failed: " + $i46Add.Output)
    $i46AddResult = $i46Add.Output.Substring($i46Add.Output.IndexOf('{')) | ConvertFrom-Json
    Assert-True (@($i46AddResult.SkippedTakenInputs | Where-Object { $_.Action -eq "pilot.AIRCRAFT_CARRIERHOOK" -and $_.GameAction -eq "pilot.AIRCRAFT_TOGGLE_ENGINE" -and $_.Input -eq "JOY_BTN7" }).Count -eq 1) ("add should report the hook as a skipped multi bind: " + $i46Add.Output)
    $i46Text = [System.IO.File]::ReadAllText($i46Settings).Replace("`r`n", "`n")
    Assert-Match $i46Text "(?m)^Joystick6 JoystickDevice0=AIRCRAFT_TOGGLE_ENGINE$" "add must keep the game's bind on the hook's input."
    Assert-NotMatch $i46Text "AIRCRAFT_CARRIERHOOK" "add must not write a multi bind."
    Assert-Match $i46Text "(?m)^Joystick7 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE$" "add must keep the air brake's old input."
    Assert-Match $i46Text "(?m)^Joystick3 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE$" "add should write the air brake's new input."

    # Merge: the conflicting game bind goes, the air brake keeps its old input; the rest stays.
    [System.IO.File]::WriteAllText($i46Settings, $i46Game2)
    $i46Merge = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--mode", "merge", "--text")
    Assert-True ($i46Merge.ExitCode -eq 0) ("il21946 merge export failed: " + $i46Merge.Output)
    $i46Text = [System.IO.File]::ReadAllText($i46Settings).Replace("`r`n", "`n")
    Assert-Match $i46Text "\[HotKey pilot\]\n(?:[^\[]*\n)?Joystick3 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE\n" "merge should add the air brake to [HotKey pilot]."
    Assert-Match $i46Text "(?m)^Joystick7 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE$" "merge must keep the air brake's old input."
    Assert-Match $i46Text "(?m)^Joystick6 JoystickDevice0=AIRCRAFT_CARRIERHOOK$" "merge should write the hook on button 7."
    Assert-NotMatch $i46Text "AIRCRAFT_TOGGLE_ENGINE" "merge should remove the game's bind on the hook's input."
    Assert-Match $i46Text "(?m)^Joystick1 JoystickDevice0=Gear$" "merge must keep the game's Gear bind."
    Assert-Match $i46Text "(?m)^MouseLeft=Fire$" "merge must keep mouse binds."
    Assert-Match $i46Text "(?m)^AXE_RZ JoystickDevice1=mixture$" "a new axis is written axis first, as the game writes a bind made on its Controls page."
    Assert-True (Test-Path ($i46Settings + ".jpbak")) "the IL-2 1946 export should back up settings.ini."

    # Merge-overwrite: the air brake fully moves; keyboard (not exported) and the missing device stay.
    $i46Over = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--mode", "merge-overwrite", "--text")
    Assert-True ($i46Over.ExitCode -eq 0) ("il21946 merge-overwrite export failed: " + $i46Over.Output)
    $i46Text = [System.IO.File]::ReadAllText($i46Settings).Replace("`r`n", "`n")
    Assert-NotMatch $i46Text "(?m)^Joystick7 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE$" "merge-overwrite should remove the air brake's old input."
    Assert-Match $i46Text "(?m)^Joystick3 JoystickDevice0=AIRCRAFT_TOGGLE_AIRBRAKE$" "merge-overwrite should keep the air brake's new input."
    Assert-Match $i46Text "(?m)^Joystick6 JoystickDevice0=AIRCRAFT_CARRIERHOOK$" "merge-overwrite should keep the hook on button 7."
    Assert-Match $i46Text "(?m)^G=Gear$" "merge-overwrite must keep the keyboard bind of Gear."
    Assert-Match $i46Text "(?m)^Joystick4 JoystickDevice3=Brake$" "merge-overwrite must keep the binds of a device that is not connected."

    # Axis response: own values go to conf.ini [rts_joystick] 1X (device 0, X).
    $i46Tune = Run-Engine @("--profile", $i46Profile, "binds", "tuning", "set", "Ailerons (roll)", "--scope", "game:IL21946", "--param", "deadBand=12", "--param", "c1=50", "--param", "c2=60", "--text")
    Assert-True ($i46Tune.ExitCode -eq 0) ("binds tuning set for game:IL21946 failed: " + $i46Tune.Output)
    $i46TuneExport = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--text")
    Assert-True ($i46TuneExport.ExitCode -eq 0) ("il21946 export with tuning failed: " + $i46TuneExport.Output)
    $i46ConfText = [System.IO.File]::ReadAllText($i46Conf).Replace("`r`n", "`n")
    Assert-Match $i46ConfText "(?m)^1X=12 50 60 100 100 100 100 100 100 100 100 0 0$" ("export should write the aileron response to conf.ini: " + $i46ConfText)
    Assert-Match $i46ConfText "(?m)^1RZ2=10 50 60 70 80 90 100 100 100 100 100 0 0$" "export must keep the other axes' responses."
    Assert-Match $i46ConfText "(?m)^TexQual=3$" "export must keep the rest of conf.ini."
    Assert-True (Test-Path ($i46Conf + ".jpbak")) "the IL-2 1946 export should back up conf.ini."

    # A base curve is fitted: saturation X 0.5 is above linear (capped at 100), curvature 0.5 below linear.
    $i46FitProfile = Join-Path $tempDir "il21946-fit.pr0file"
    Run-Engine @("--profile", $i46FitProfile, "relations", "add", "I46 Fit", "--axis", "--node", "IL21946:move.elevator", "--device", $i46Stick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $i46FitProfile, "binds", "set", "I46 Fit", "--deadzone", "0.02", "--curvature", "0.5", "--text") | Out-Null
    $i46Fit = Run-Engine @("--profile", $i46FitProfile, "binds", "tuning", "show", "I46 Fit", "--game", "IL21946", "--text")
    Assert-Match $i46Fit.Output "fitted to the base curve\): .*n\.c10=100;.*n\.deadBand=20;" ("the il21946 fit should turn dead zone 0.02 into dead band 20 and end at 100: " + $i46Fit.Output)

    # Clean: the written devices keep only the profile's binds; other devices, keys and the missing device stay.
    $i46Delete = Run-Engine @("--profile", $i46Profile, "relations", "mass", "delete", "--relation", "Fire guns and cannons", "--text")
    Assert-True ($i46Delete.ExitCode -eq 0) ("relations mass delete failed: " + $i46Delete.Output)
    $i46Clean = Run-Engine @("--profile", $i46Profile, "il21946", "export", $i46Settings, "--mode", "clean", "--text")
    Assert-True ($i46Clean.ExitCode -eq 0) ("il21946 clean export failed: " + $i46Clean.Output)
    $i46Text = [System.IO.File]::ReadAllText($i46Settings).Replace("`r`n", "`n")
    Assert-NotMatch $i46Text "Joystick0 JoystickDevice0=Weapon01" "clean should release the stick's bind that the profile no longer has."
    Assert-Match $i46Text "(?m)^AXE_RZ JoystickDevice2=rudder$" "clean must keep the binds of the profile."
    Assert-Match $i46Text "(?m)^Joystick4 JoystickDevice3=Brake$" "clean must keep the binds of a device that is not connected."
    Assert-Match $i46Text "(?m)^A=autopilot$" "clean must keep keyboard binds."
    Assert-Match $i46Text "(?m)^speed=5000$" "export must keep the other sections."

    # Nuke: a profile with stick binds only clears the other connected devices.
    $i46NukeProfile = Join-Path $tempDir "il21946-nuke.pr0file"
    Run-Engine @("--profile", $i46NukeProfile, "relations", "add", "I46 Nuke Aileron", "--axis", "--node", "IL21946:move.aileron", "--device", $i46Stick, "--input", "JOY_X", "--text") | Out-Null
    $i46Nuke = Run-Engine @("--profile", $i46NukeProfile, "il21946", "export", $i46Settings, "--mode", "merge", "--nuke-untouched-devices", "--text")
    Assert-True ($i46Nuke.ExitCode -eq 0) ("il21946 nuke export failed: " + $i46Nuke.Output)
    $i46Text = [System.IO.File]::ReadAllText($i46Settings).Replace("`r`n", "`n")
    Assert-NotMatch $i46Text "JoystickDevice1=|JoystickDevice2=" "nuke should clear the throttle's and pedals' binds."
    Assert-Match $i46Text "(?m)^JoystickDevice0 AXE_X=aileron$" "nuke must keep the exported stick bind (in the default's device-first form)."
    Assert-Match $i46Text "(?m)^Joystick4 JoystickDevice3=Brake$" "nuke must keep the binds of a device that is not connected."
}
finally {
    Pop-Location
    $env:JOYPRO_FAKE_DEVICES = $previousI46Fake
    $env:JOYPRO_DATA_DIR = $previousI46Data
}
