# IL-2 Sturmovik: Cliffs of Dover (engine il2cod) and Cliffs of Dover Blitz (engine il2codblitz): confUser.ini binds and
# [Config pilotMove] axis settings, Blitz device names "<name>-<id>" and the original game's "Joystick<n>", chords,
# refresh-db, scan/import/export modes. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir,
# $engineDir, $engineBin). Fixtures: synthetic files in the documented format (docs/games/il2-cliffs-of-dover.md; not
# yet verified on a real install). Fake devices with interface paths (Blitz id = hash of the path: stick D064369 = 7 digits,
# throttle 23DE943F, pedals 86009047 but named with an older id in the file, box "Smoke Box+Plus" 499E0325 not in the file).
$codDir = Join-Path $tempDir "il2cod"
New-Item -Path (Join-Path $codDir "blitz"), (Join-Path $codDir "classic"), (Join-Path $codDir "install") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2cod\blitz\confUser.ini") -Destination (Join-Path $codDir "blitz") -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2cod\classic\confUser.ini") -Destination (Join-Path $codDir "classic") -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2cod\install\confUser.ini") -Destination (Join-Path $codDir "install") -Force
$codBlitz = Join-Path $codDir "blitz\confUser.ini"
$codClassic = Join-Path $codDir "classic\confUser.ini"
$codBlitzOriginal = [System.IO.File]::ReadAllText($codBlitz)
$codClassicOriginal = [System.IO.File]::ReadAllText($codClassic)
$codProfile = Join-Path $tempDir "il2codblitz.pr0file"
$codStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$codBox = "Smoke Box {0000BEEF-1111-2222-3333-444444444444}"
function Get-CodText([string]$Path) { return [System.IO.File]::ReadAllText($Path).Replace("`r`n", "`n") }
# Refresh-db writes the command list into the game-data folder (<data>\GameData): a temp program root (JoyPro's own DB
# folders only) and a temp data folder, so neither the development DB nor the other smoke parts see test entries.
$codRoot = Join-Path $tempDir "il2cod-root"
New-Item -Path (Join-Path $codRoot "DB") -ItemType Directory -Force | Out-Null
foreach ($own in @("Functions", "Devices", "Llm")) {
    Copy-Item -Path (Join-Path $engineBin ("DB\" + $own)) -Destination (Join-Path $codRoot "DB") -Recurse -Force
}
$previousCodFake = $env:JOYPRO_FAKE_DEVICES
$previousCodData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\il2cod\devices.tsv"
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "il2cod-data"
$codLists = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB"
Push-Location $codRoot
try {
    $codList = Run-Engine @("games", "integrations", "--text")
    Assert-Match $codList.Output "IL2CoDBlitz" "games integrations should list Cliffs of Dover Blitz."
    Assert-Match $codList.Output "IL2CoD\b" "games integrations should list Cliffs of Dover."

    # Catalog: JoyPro's own list plus the user's file and the install's default file.
    $codRefresh = Run-Engine @("il2codblitz", "refresh-db", "--path", $codBlitz, "--text")
    Assert-True ($codRefresh.ExitCode -eq 0) ("il2codblitz refresh-db failed: " + $codRefresh.Output)
    $codCatalog = Get-CodText (Join-Path $codLists "IL2CoDBlitz\actions.tsv")
    Assert-True (-not (Test-Path (Join-Path $codRoot "DB\IL2CoDBlitz"))) "il2codblitz refresh-db must not write into the program folder."
    Assert-Match $codCatalog "(?m)^pilotMove\.aileron\tAilerons \(roll\)\taxis\tAxes$" "the Blitz catalog should have JoyPro's aileron axis."
    Assert-Match $codCatalog "(?m)^pilot\.autorudder_off\t" "refresh-db should add commands named by the user's file."
    Assert-NotMatch $codCatalog "builder_thing" "refresh-db must not take mission builder commands."
    $codRefreshClassic = Run-Engine @("il2cod", "refresh-db", "--path", (Join-Path $codDir "install"), "--text")
    Assert-True ($codRefreshClassic.ExitCode -eq 0) ("il2cod refresh-db failed: " + $codRefreshClassic.Output)
    Assert-Match (Get-CodText (Join-Path $codLists "IL2CoD\actions.tsv")) "(?m)^pilotMove\.SmokeDefaultAxis\t[^\t]+\taxis\t" "il2cod refresh-db should read the install folder's confUser.ini."

    # Scan: Blitz names (no zero padding), chords as modifiers, hats, a device that is not connected.
    $codScan = Run-Engine @("il2codblitz", "scan", $codBlitz, "--text")
    Assert-True ($codScan.ExitCode -eq 0) ("il2codblitz scan failed: " + $codScan.Output)
    Assert-Match $codScan.Output "pilotMove\.rudder <- Smoke Pedals \{0AAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\} JOY_RZ" "scan should match 'Smoke_Pedals-AAAAAAA' to the pedals."
    Assert-Match $codScan.Output ([regex]::Escape("pilot.autorudder_off <- " + $codStick + " JOY_BTN19 + " + $codStick + " JOY_BTN13")) "a chord's first trigger should become the modifier."
    Assert-Match $codScan.Output ([regex]::Escape("viewPan.PanRight <- " + $codStick + " JOY_BTN_POV1_R")) "scan should read 'Pov90 <device>+Pov' as hat 1 right."
    Assert-Match $codScan.Output "'Other_Stick-12345678' is not connected" "scan should name the device that is not connected."
    Assert-NotMatch $codScan.Output "builder_thing" "scan must not read the mission builder's section."

    # Import and round trip: names, chords, the [Config pilotMove] values (game tuning) come back unchanged.
    $codImport = Run-Engine @("--profile", $codProfile, "il2codblitz", "import", $codBlitz, "--include-keyboard", "--text")
    Assert-True ($codImport.ExitCode -eq 0) ("il2codblitz import failed: " + $codImport.Output)
    $codRudder = Run-Engine @("--profile", $codProfile, "binds", "tuning", "show", "Rudder (yaw)", "--game", "IL2CoDBlitz", "--text")
    Assert-Match $codRudder.Output "game:IL2CoDBlitz: .*n\.deadZoneCentre=0\.25;n\.deadZoneEnd=0;n\.sensitivity=1" ("import should store [Config pilotMove] rudder as the Blitz tuning: " + $codRudder.Output)
    $codModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $codModels.Output "(?m)^il2cod - IL-2 Sturmovik: Cliffs of Dover" "binds tuning models should list the il2cod model."
    $codRound = Run-Engine @("--profile", $codProfile, "il2codblitz", "export", $codBlitz, "--include-keyboard", "--text")
    Assert-True ($codRound.ExitCode -eq 0) ("il2codblitz export after import failed: " + $codRound.Output)
    # The only change: the pedals' line named an older id (another USB port), the export writes the current one.
    Assert-True ([System.IO.File]::ReadAllText($codBlitz) -eq $codBlitzOriginal.Replace("Smoke_Pedals-AAAAAAA+", "Smoke_Pedals-86009047+")) ("il2codblitz import/export must leave confUser.ini as it was but the pedals' current id: " + $codRound.Output)

    # Merge: new binds (a known device keeps its name from the file, a new device gets "<name>-<id>").
    Run-Engine @("--profile", $codProfile, "relations", "add", "COD Airbrake", "--button", "--node", "IL2CoDBlitz:pilot.airbrake", "--device", $codStick, "--input", "JOY_BTN4", "--text") | Out-Null
    Run-Engine @("--profile", $codProfile, "relations", "add", "COD Flaps Box", "--button", "--node", "IL2CoDBlitz:pilot.landingflaps_u", "--device", $codBox, "--input", "JOY_BTN1", "--text") | Out-Null
    $codMerge = Run-Engine @("--profile", $codProfile, "il2codblitz", "export", $codBlitz, "--mode", "merge", "--text")
    Assert-True ($codMerge.ExitCode -eq 0) ("il2codblitz merge export failed: " + $codMerge.Output)
    $codText = Get-CodText $codBlitz
    Assert-Match $codText "(?m)^Smoke_Stick-D064369\+Key3=airbrake$" "merge should add the air brake with the stick's name from the file."
    Assert-Match $codText "(?m)^Smoke_Box_Plus-499E0325\+Key0=landingflaps_u$" "a device the file does not know gets '<product name with _ for blank and +>-<path hash>'."
    Assert-Match $codText "(?m)^Smoke_Stick-D064369\+Key1=trigger0$" "merge must keep the game's bind on button 2."
    Assert-True (Test-Path ($codBlitz + ".jpbak")) "the Cliffs of Dover export should back up confUser.ini."

    # Axis settings: own values go to [Config pilotMove] in the line's format.
    $codTune = Run-Engine @("--profile", $codProfile, "binds", "tuning", "set", "Ailerons (roll)", "--scope", "game:IL2CoDBlitz", "--param", "sensitivity=0.5", "--text")
    Assert-True ($codTune.ExitCode -eq 0) ("binds tuning set for game:IL2CoDBlitz failed: " + $codTune.Output)
    $codTuneExport = Run-Engine @("--profile", $codProfile, "il2codblitz", "export", $codBlitz, "--text")
    Assert-True ($codTuneExport.ExitCode -eq 0) ("il2codblitz export with tuning failed: " + $codTuneExport.Output)
    $codText = Get-CodText $codBlitz
    Assert-Match $codText "(?m)^aileron=0\.5 0\.12 0\.02$" ("export should write the aileron sensitivity: " + $codText)
    Assert-Match $codText "(?m)^elevator=0\.997872340425532 0\.01 0\.02$" "export must keep the game's digits of unchanged values."
    Assert-Match $codText "(?m)^throttle=0\.05 0\.95 0\.02 0\.02 0\.02$" "export must keep the throttle's five values."

    # Clean: the written devices keep only the profile's binds; builder, unknown devices and other sections stay.
    $codDelete = Run-Engine @("--profile", $codProfile, "relations", "mass", "delete", "--relation", "Fire guns", "--text")
    Assert-True ($codDelete.ExitCode -eq 0) ("relations mass delete failed: " + $codDelete.Output)
    # A ground vehicle bind on the stick (not flight): clean, nuke and conflicts never release it, also where the profile
    # puts a flight command on the same button.
    $codVehicle = [System.IO.File]::ReadAllText($codBlitz)
    $codVehicle = $codVehicle.Insert($codVehicle.IndexOf("[ChatWindow]"), "[HotKey vehicle]`r`nSmoke_Stick-D064369+Key7=vehicle_fire`r`n`r`n")
    [System.IO.File]::WriteAllText($codBlitz, $codVehicle)
    Run-Engine @("--profile", $codProfile, "relations", "add", "COD Gear Stick", "--button", "--node", "IL2CoDBlitz:pilot.uc", "--device", $codStick, "--input", "JOY_BTN8", "--text") | Out-Null
    $codClean = Run-Engine @("--profile", $codProfile, "il2codblitz", "export", $codBlitz, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($codClean.ExitCode -eq 0) ("il2codblitz clean export failed: " + $codClean.Output)
    $codText = Get-CodText $codBlitz
    Assert-Match $codText "(?m)^Smoke_Stick-D064369\+Key7=vehicle_fire$" "clean and nuke must keep the vehicle bind on a written device."
    Assert-Match $codText "(?m)^Smoke_Stick-D064369\+Key7=uc$" "clean should write the flight command on the vehicle's button (another context)."
    Assert-NotMatch $codText "Key1=trigger0" "clean should release the stick's bind the profile no longer has."
    Assert-Match $codText "(?m)^Smoke_Stick-D064369\+Key5=builder_thing$" "clean must not touch the mission builder's section."
    Assert-Match $codText "(?m)^Other_Stick-12345678\+AXE_U=-1pitch$" "clean and nuke must keep the binds of a device that is not connected."
    Assert-Match $codText "(?m)^Other_Stick-12345678\+Key2=landingflaps_d$" "nuke must keep the binds of a device that is not connected."
    Assert-Match $codText "(?m)^x=10$" "export must keep the other sections."

    # The original game: "Joystick" / "Joystick<n>" by Windows' order.
    $codClassicProfile = Join-Path $tempDir "il2cod-classic.pr0file"
    $codClassicScan = Run-Engine @("il2cod", "scan", $codClassic, "--text")
    Assert-Match $codClassicScan.Output ([regex]::Escape("pilotMove.aileron <- " + $codStick + " JOY_X")) "il2cod should read 'Joystick' as the first device."
    Assert-Match $codClassicScan.Output "pilot\.uc <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN4" "il2cod should read 'Joystick1' as the second device."
    Assert-Match $codClassicScan.Output "pilotMove\.underc <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN6" "il2cod should read a button on an axis command ('Joystick1+Key5=1underc')."
    $codClassicImport = Run-Engine @("--profile", $codClassicProfile, "il2cod", "import", $codClassic, "--text")
    Assert-True ($codClassicImport.ExitCode -eq 0) ("il2cod import failed: " + $codClassicImport.Output)
    $codClassicRound = Run-Engine @("--profile", $codClassicProfile, "il2cod", "export", $codClassic, "--text")
    Assert-True ($codClassicRound.ExitCode -eq 0 -and [System.IO.File]::ReadAllText($codClassic) -eq $codClassicOriginal) ("il2cod import/export must leave confUser.ini as it was: " + $codClassicRound.Output)
    Run-Engine @("--profile", $codClassicProfile, "relations", "add", "COD Classic Gear", "--button", "--node", "IL2CoD:pilot.uc", "--device", $codBox, "--input", "JOY_BTN2", "--text") | Out-Null
    $codClassicExport = Run-Engine @("--profile", $codClassicProfile, "il2cod", "export", $codClassic, "--mode", "add", "--text")
    Assert-True ($codClassicExport.ExitCode -eq 0) ("il2cod export failed: " + $codClassicExport.Output)
    $codClassicText = Get-CodText $codClassic
    Assert-Match $codClassicText "(?m)^Joystick3\+Key1=uc$" "il2cod should write the fourth device as 'Joystick3'."
    Assert-Match $codClassicText "(?m)^Joystick1\+Key3=uc$" "add must keep the game's other input of the command."

    # The original's process is "Launcher", like Falcon BMS's launcher: only a Launcher.exe in the game's folder blocks an
    # export. A copy of ping.exe named Launcher.exe stands in for both (it runs ~60 s).
    $codPreviousIgnore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
    $codLaunchers = @()
    try {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $null
        $codOther = Join-Path $tempDir "il2cod-guard\Falcon BMS\Launcher.exe"
        $codOwn = Join-Path $tempDir "il2cod-guard\IL-2 Sturmovik Cliffs of Dover\Launcher.exe"
        foreach ($exe in @($codOther, $codOwn)) {
            New-Item -Path (Split-Path -Parent $exe) -ItemType Directory -Force | Out-Null
            Copy-Item -Path (Join-Path $env:SystemRoot "System32\PING.EXE") -Destination $exe -Force
        }
        $codLaunchers += Start-Process -FilePath $codOther -ArgumentList "-n 60 127.0.0.1" -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 500
        $codGuardOther = Run-Engine @("--profile", $codClassicProfile, "il2cod", "export", $codClassic, "--mode", "add", "--text")
        Assert-True ($codGuardOther.ExitCode -eq 0 -and $codGuardOther.Output -notmatch "is running") ("another program's Launcher.exe must not block the il2cod export: " + $codGuardOther.Output)
        $codLaunchers += Start-Process -FilePath $codOwn -ArgumentList "-n 60 127.0.0.1" -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 500
        $codGuardOwn = Run-Engine @("--profile", $codClassicProfile, "il2cod", "export", $codClassic, "--mode", "add", "--text")
        Assert-True ($codGuardOwn.ExitCode -ne 0 -and $codGuardOwn.Output -match "is running") ("Cliffs of Dover's own Launcher.exe must block the il2cod export: " + $codGuardOwn.Output)
    }
    finally {
        foreach ($p in $codLaunchers) { if ($p -and -not $p.HasExited) { $p.Kill() } }
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $codPreviousIgnore
    }
}
finally {
    Pop-Location
    $env:JOYPRO_FAKE_DEVICES = $previousCodFake
    $env:JOYPRO_DATA_DIR = $previousCodData
}
