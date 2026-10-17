# Elite Dangerous axis and game settings (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineDir).
# Fixture elite-settings: Smoke Stick (12345678) roll/pitch, Smoke Throttle (ABCD0001) vertical thrust with the game's
# DefaultPlatformDeadzone symbol and a forward-only throttle; the value settings of the Ship preset around them.
$esDir = Join-Path $tempDir "elite-settings"
$esBinds = Join-Path $esDir "Custom.4.2.binds"
$esProfile = Join-Path $tempDir "elite-settings.pr0file"
$esOriginal = Join-Path $engineDir "tests\fixtures\elite-settings\Custom.4.2.binds"
New-Item -Path $esDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\elite-settings\*") -Destination $esDir -Force
$previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\bms\devices.tsv"

# Lines of the file that differ from the fixture (same line count: JoyPro edits values in place).
function Get-EliteChangedLines([string]$Path) {
    $now = [IO.File]::ReadAllText($Path) -split "`r`n"
    $was = [IO.File]::ReadAllText($esOriginal) -split "`r`n"
    Assert-True ($now.Count -eq $was.Count) "elite settings export must not add or remove lines."
    return @(for ($i = 0; $i -lt $now.Count; $i++) { if ($now[$i] -ne $was[$i]) { $now[$i].Trim() } })
}

try {
    $esModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $esModels.Output "(?m)^elite\b" "binds tuning models should list the elite model."
    $esFfbModels = Run-Engine @("ffb", "models", "--text")
    Assert-Match $esFfbModels.Output "EliteDangerous - Elite Dangerous" "ffb models should list the Elite Dangerous game settings."

    $esScan = Run-Engine @("elite", "scan", "--path", $esBinds, "--text")
    Assert-True ($esScan.ExitCode -eq 0) ("elite scan of the settings fixture failed: " + $esScan.Output)
    Assert-Match $esScan.Output "(?m)^Bindings Read:\s+4\s*$" "elite scan should read the four axes."
    Assert-Match $esScan.Output "headlookAccumulate=1.*yawToRollMode=0, yawToRollModeFaOff=0.*yawToRollSensitivity=0\.4.*forward only" ("elite scan should report the preset's game settings: " + $esScan.Output)

    # Import: invert in JoyPro's (DCS) direction = file XOR Elite's stock direction; dead zone, symbol, throttle range.
    $esImport = Run-Engine @("--profile", $esProfile, "elite", "import", "--path", $esBinds, "--replace", "--text")
    Assert-True ($esImport.ExitCode -eq 0) ("elite import failed: " + $esImport.Output)
    $esPitch = "Elite Dangerous (experimental) Pitch Axis"
    $esVertical = "Elite Dangerous (experimental) Vertical Thrust Axis"
    $esThrottle = "Elite Dangerous (experimental) Throttle Axis"
    $esPitchShow = Run-Engine @("--profile", $esProfile, "binds", "tuning", "show", $esPitch, "--game", "EliteDangerous", "--text")
    Assert-Match $esPitchShow.Output "base: inv=0;" ("Elite's inverted pitch (stock direction) must import as a relation without invert: " + $esPitchShow.Output)
    Assert-Match $esPitchShow.Output "game:EliteDangerous: inv=0;sl=0;dz=0\.05;" ("elite import should keep the pitch dead zone as the game's tuning: " + $esPitchShow.Output)
    $esVerticalShow = Run-Engine @("--profile", $esProfile, "binds", "tuning", "show", $esVertical, "--game", "EliteDangerous", "--text")
    Assert-Match $esVerticalShow.Output "game:EliteDangerous: inv=1;sl=0;dz=0;.*n\.platformDeadzone=1" ("a vertical thrust axis that Elite does not invert is inverted in JoyPro's direction; the dead zone symbol is kept: " + $esVerticalShow.Output)
    $esThrottleShow = Run-Engine @("--profile", $esProfile, "binds", "tuning", "show", $esThrottle, "--game", "EliteDangerous", "--text")
    Assert-Match $esThrottleShow.Output "game:EliteDangerous: inv=0;sl=1;" ("a forward-only Elite throttle (Inverted 1) should import as a one-sided throttle without invert: " + $esThrottleShow.Output)

    # The imported profile writes the file back unchanged.
    $esRoundTrip = Run-Engine @("--profile", $esProfile, "elite", "export", "--path", $esBinds, "--text")
    Assert-True ($esRoundTrip.ExitCode -eq 0) ("elite export of the imported profile failed: " + $esRoundTrip.Output)
    Assert-Match $esRoundTrip.Output "already matches" ("elite export right after the import must change nothing: " + $esRoundTrip.Output)

    # Preview: Elite's dead zone, linear outside (0.05 at the centre).
    $esPreview = Run-Engine @("--profile", $esProfile, "binds", "tuning", "preview", $esPitch, "--game", "EliteDangerous", "--points", "5", "--text")
    Assert-Match $esPreview.Output "-0\.500 -> -0\.47368" ("elite preview should apply the dead zone linearly: " + $esPreview.Output)

    # Export of changed values: invert XOR, dead zone, centred throttle = full range, the symbol given up for a number.
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esPitch, "--scope", "game:EliteDangerous", "--inverted", "true", "--deadzone", "0.12", "--text") | Out-Null
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esThrottle, "--scope", "game:EliteDangerous", "--slider", "false", "--text") | Out-Null
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esVertical, "--scope", "game:EliteDangerous", "--param", "platformDeadzone=0", "--deadzone", "0.2", "--text") | Out-Null
    $esExport = Run-Engine @("--profile", $esProfile, "elite", "export", "--path", $esBinds, "--text")
    Assert-True ($esExport.ExitCode -eq 0) ("elite export of the changed tuning failed: " + $esExport.Output)
    $esChanged = Get-EliteChangedLines $esBinds
    Assert-SetEquivalent $esChanged @('<Inverted Value="0" />', '<Deadzone Value="0.12000000" />', '<Deadzone Value="0.20000000" />', '<ThrottleRange Value="" />') ("elite export should change only the pitch invert/dead zone, the vertical thrust dead zone and the throttle range: " + ($esChanged -join " | "))
    $esText = [IO.File]::ReadAllText($esBinds)
    Assert-Match $esText "<PitchAxisRaw>\r\n\t\t<Binding Device=`"12345678`" Key=`"Joy_YAxis`" />\r\n\t\t<Inverted Value=`"0`" />\r\n\t\t<Deadzone Value=`"0.12000000`" />" "an inverted JoyPro pitch is Elite's non-inverted pitch."
    Assert-NotMatch $esText "[^\r]\n" "elite settings export must keep CRLF line ends."
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esThrottle, "--scope", "game:EliteDangerous", "--slider", "true", "--text") | Out-Null
    $esBack = Run-Engine @("--profile", $esProfile, "elite", "export", "--path", $esBinds, "--text")
    Assert-Match ([IO.File]::ReadAllText($esBinds)) '<ThrottleRange Value="Bindings_ThrottleForewardOnly" />' ("a one-sided throttle relation should make the throttle forward-only again: " + $esBack.Output)

    # Game-wide settings (device *): the value elements, in the game's formats; nothing else changes.
    Copy-Item -Path $esOriginal -Destination $esBinds -Force
    $esFfb = Run-Engine @("--profile", $esProfile, "ffb", "set", "--device", "*", "--game", "EliteDangerous", "--param", "yawToRollMode=2", "--param", "yawToRollModeFaOff=1", "--param", "yawToRollSensitivity=0.75", "--param", "throttleIncrement=4", "--param", "headlookAccumulate=false", "--param", "headlookIncrement=2", "--param", "headlookSensitivity=0.4", "--text")
    Assert-True ($esFfb.ExitCode -eq 0) ("ffb set for Elite failed: " + $esFfb.Output)
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "clear", $esPitch, "--scope", "game:EliteDangerous", "--text") | Out-Null
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "clear", $esVertical, "--scope", "game:EliteDangerous", "--text") | Out-Null
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esPitch, "--scope", "game:EliteDangerous", "--deadzone", "0.05", "--text") | Out-Null
    Run-Engine @("--profile", $esProfile, "binds", "tuning", "set", $esVertical, "--scope", "game:EliteDangerous", "--inverted", "true", "--param", "platformDeadzone=1", "--text") | Out-Null
    $esSettings = Run-Engine @("--profile", $esProfile, "elite", "export", "--path", $esBinds, "--text")
    Assert-True ($esSettings.ExitCode -eq 0) ("elite export with game settings failed: " + $esSettings.Output)
    $esChanged = Get-EliteChangedLines $esBinds
    Assert-SetEquivalent $esChanged @('<YawToRollMode Value="Bindings_YawIntoRollLowRoll" />', '<YawToRollMode_FAOff Value="Bindings_YawIntoRollNone" />', '<YawToRollSensitivity Value="0.75000000" />', '<ThrottleIncrement Value="0.25000000" />', '<HeadlookMode Value="Bindings_HeadlookModeDirect" />', '<HeadlookIncrement Value="0.33333334" />', '<HeadlookSensitivity Value="0.40000001" />') ("elite export should write exactly the game settings: " + ($esChanged -join " | "))
    $esAgain = Run-Engine @("--profile", $esProfile, "elite", "export", "--path", $esBinds, "--text")
    Assert-Match $esAgain.Output "already matches" ("a second elite export with game settings should change nothing: " + $esAgain.Output)

    # A profile without Elite binds still exports the game settings, and releases no slot even in clean/nuke mode.
    Copy-Item -Path $esOriginal -Destination $esBinds -Force
    $esOnly = Join-Path $tempDir "elite-settings-only.pr0file"
    Run-Engine @("--profile", $esOnly, "ffb", "set", "--device", "*", "--game", "EliteDangerous", "--param", "yawToRollMode=1", "--text") | Out-Null
    $esDry = Run-Engine @("--profile", $esOnly, "elite", "export", "--path", $esBinds, "--mode", "clean", "--nuke-untouched-devices", "--dry-run", "--text")
    Assert-Match $esDry.Output "YawToRollMode = `"Bindings_YawIntoRollTime`"" ("a dry run should list the settings it would write: " + $esDry.Output)
    Assert-True ([IO.File]::ReadAllText($esBinds) -eq [IO.File]::ReadAllText($esOriginal)) "an elite dry run must not write."
    $esOnlyExport = Run-Engine @("--profile", $esOnly, "elite", "export", "--path", $esBinds, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($esOnlyExport.ExitCode -eq 0) ("elite settings-only export failed: " + $esOnlyExport.Output)
    $esChanged = Get-EliteChangedLines $esBinds
    Assert-SetEquivalent $esChanged @('<YawToRollMode Value="Bindings_YawIntoRollTime" />') ("a profile without Elite binds should only write the game settings: " + ($esChanged -join " | "))
    Assert-True (Test-Path ($esBinds + ".jpbak")) "elite settings export should back up the binds file."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
}

# DB refresh from the game's .binds files: a game folder with a stock preset (Products\<product>\ControlSchemes). New
# ship flight elements come in with their category and a title made from the name; SRV, on-foot and unknown elements
# stay out, known actions keep their title, and an unchanged catalog is not written again.
$erDir = Join-Path $tempDir "elite-refresh"
$erGame = Join-Path $erDir "game"
$erRoot = Join-Path $erDir "joypro"
$erSchemes = Join-Path $erGame "Products\elite-dangerous-odyssey-64\ControlSchemes"
New-Item -Path $erSchemes, (Join-Path $erRoot "DB\EliteDangerous"), (Join-Path $erRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\EliteDangerous\*") -Destination (Join-Path $erRoot "DB\EliteDangerous") -Force
$erAxis = "`t`t<Binding Device=`"{NoDevice}`" Key=`"`" />`r`n`t`t<Inverted Value=`"0`" />`r`n`t`t<Deadzone Value=`"0.00000000`" />`r`n"
$erButton = "`t`t<Primary Device=`"Keyboard`" Key=`"Key_J`" />`r`n`t`t<Secondary Device=`"{NoDevice}`" Key=`"`" />`r`n"
$erPreset = Join-Path $erSchemes "SmokeStick.binds"
[System.IO.File]::WriteAllText($erPreset,
    "<?xml version=`"1.0`" encoding=`"UTF-8`" ?>`r`n<Root PresetName=`"SmokeStick`" MajorVersion=`"4`" MinorVersion=`"0`">`r`n`t<KeyboardLayout>en-US</KeyboardLayout>`r`n" +
    "`t<ThrottleRange Value=`"`" />`r`n`t<YawAxisRaw>`r`n$erAxis`t</YawAxisRaw>`r`n`t<SmokePitchAxis_Landing>`r`n$erAxis`t</SmokePitchAxis_Landing>`r`n" +
    "`t<MultiCrewSmokeFovButton>`r`n$erButton`t</MultiCrewSmokeFovButton>`r`n`t<HumanoidSmokeJumpButton>`r`n$erButton`t</HumanoidSmokeJumpButton>`r`n" +
    "`t<BuggySmokeHorn>`r`n$erButton`t</BuggySmokeHorn>`r`n`t<SmokeMystery>`r`n$erButton`t</SmokeMystery>`r`n</Root>`r`n", (New-Object System.Text.UTF8Encoding $true))
# The list the engine reads and the refresh writes: the game-data folder's (<data>\GameData), copied from the program
# folder ($erRoot) on the engine's first start.
$erCatalogPath = Join-Path $erDir "data\GameData\DB\EliteDangerous\actions.tsv"
$erPreviousData = $env:JOYPRO_DATA_DIR
$erPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $erDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $erRoot
try {
    # A refresh of a game that is not installed is refused, also with a path.
    $erSet = Run-Engine @("games", "install", "set", "EliteDangerous", $erGame, "--text")
    Assert-True ($erSet.ExitCode -eq 0) ("games install set EliteDangerous failed: " + $erSet.Output)
    $erYawBefore = ([regex]::Match([System.IO.File]::ReadAllText($erCatalogPath), "(?m)^YawAxisRaw\t[^\r\n]*")).Value
    $er = Run-Engine @("elite", "refresh-db", $erGame, "--text")
    Assert-True ($er.ExitCode -eq 0) ("elite refresh-db from the game's control schemes failed: " + $er.Output)
    Assert-Match $er.Output "from 1 \.binds file" ("elite refresh-db should read the stock preset: " + $er.Output)
    $erCatalog = [System.IO.File]::ReadAllText($erCatalogPath)
    Assert-Match $erCatalog "(?m)^SmokePitchAxis_Landing\tSmoke Pitch Axis \(Landing\)\taxis\tship_flight_landing\r?$" "a new landing axis should come in as an axis of ship_flight_landing."
    Assert-Match $erCatalog "(?m)^MultiCrewSmokeFovButton\tMulticrew: Smoke Field of View\tbutton\tship_multicrew\r?$" "a new multicrew button should get the catalog's title style."
    Assert-NotMatch $erCatalog "HumanoidSmokeJumpButton|BuggySmokeHorn|SmokeMystery" "on-foot, SRV and unknown elements must not become catalog actions."
    Assert-Match $erCatalog ("(?m)^" + [regex]::Escape($erYawBefore) + "\r?$") "known actions must keep their title and category."
    $erWritten = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($erCatalogPath))
    $erAgain = Run-Engine @("elite", "refresh-db", "--path", $erGame, "--text")
    Assert-Match $erAgain.Output "nothing was written" ("a second elite refresh-db should find nothing new: " + $erAgain.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($erCatalogPath)) -eq $erWritten) "an unchanged catalog must not be rewritten."

    # Without a path: the installation's stock presets (and, read only, the presets the game wrote in Options\Bindings).
    [System.IO.File]::WriteAllText($erPreset, [System.IO.File]::ReadAllText($erPreset).Replace("SmokeMystery", "SmokeSecondFire_Landing"), (New-Object System.Text.UTF8Encoding $true))
    $erDry = Run-Engine @("elite", "refresh-db", "--dry-run", "--text")
    Assert-True ($erDry.ExitCode -eq 0) ("elite refresh-db from the installation failed: " + $erDry.Output)
    Assert-Match $erDry.Output "SmokeSecondFire_Landing" ("elite refresh-db without a path should read the installation's control schemes: " + $erDry.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($erCatalogPath)) -eq $erWritten) "an elite refresh-db dry run must not write."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\EliteDangerous\actions.tsv")) -notmatch "SmokePitchAxis_Landing") "the shipped Elite catalog must stay untouched."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $erRoot "DB\EliteDangerous\actions.tsv")) -notmatch "SmokePitchAxis_Landing") "elite refresh-db must not write into the program folder."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $erPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $erPreviousDetection
}
