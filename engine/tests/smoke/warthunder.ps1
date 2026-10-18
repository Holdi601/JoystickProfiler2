# War Thunder axis settings (warthunder model), base presets and game-wide settings. Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Works on temp copies of the fixtures only.
$wtsDir = Join-Path $tempDir "warthunder-settings"
New-Item -Path $wtsDir -ItemType Directory -Force | Out-Null
$wtsBlk = Join-Path $wtsDir "machine.blk"
$wtsProfile = Join-Path $wtsDir "wt-settings.pr0file"
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine_settings.blk") -Destination $wtsBlk -Force
$wtsOriginal = [System.IO.File]::ReadAllText($wtsBlk)
$wtsStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$wtsPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\warthunder\devices.tsv"
try {
    # The model: the game's dialog preview (axiscontrols.nut) y = sign * |clamp(x*kMul+kAdd) after the dead zone|^(1+nonlinearity).
    $wtsModels = Run-Engine @("binds", "tuning", "models", "--text")
    Assert-Match $wtsModels.Output "warthunder" "binds tuning models should list the warthunder model."

    # Import: every attribute of the Axis setup dialog; attributes the file does not set come from the base preset
    # (hotkey.thrustmaster_warthog: ailerons dead zone 0.02, nonlinearity 1, multiplier 1.1; throttle inverted).
    $wtsImport = Run-Engine @("--profile", $wtsProfile, "warthunder", "import", $wtsBlk, "--text")
    Assert-True ($wtsImport.ExitCode -eq 0) ("warthunder import of the settings fixture failed: " + $wtsImport.Output)
    Assert-NotMatch $wtsImport.Output "is not in JoyPro's table" "the warthog base preset should be in base-presets.tsv."
    $wtsRoll = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "show", "War Thunder Roll axis", "--game", "WarThunder", "--text")
    Assert-Match $wtsRoll.Output "game:WarThunder: inv=0;sl=0;[^\r\n]*n\.innerDeadzone=0\.02;n\.kAdd=0;n\.keepDisabledValue=0;n\.kMul=1\.1;n\.nonlinearity=1;n\.rangeMax=1;n\.rangeMin=-1;n\.relative=0;n\.relSens=1;n\.relStep=0" ("warthunder import should fill missing attributes from the base preset: " + $wtsRoll.Output)
    $wtsPitch = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "show", "War Thunder Pitch axis", "--game", "WarThunder", "--text")
    Assert-Match $wtsPitch.Output "game:WarThunder: inv=1;[^\r\n]*n\.kAdd=0\.28;[^\r\n]*n\.kMul=1\.5;n\.nonlinearity=2;" ("warthunder import should read inverse, kMul, kAdd and nonlinearity: " + $wtsPitch.Output)
    $wtsYaw = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "show", "War Thunder Yaw axis", "--game", "WarThunder", "--text")
    Assert-Match $wtsYaw.Output "game:WarThunder: [^\r\n]*n\.innerDeadzone=0\.1;[^\r\n]*n\.keepDisabledValue=1;" ("warthunder import should read keepDisabledValue: " + $wtsYaw.Output)
    $wtsThrottle = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "show", "War Thunder Throttle axis", "--game", "WarThunder", "--text")
    Assert-Match $wtsThrottle.Output "game:WarThunder: inv=1;sl=1;[^\r\n]*n\.innerDeadzone=0;[^\r\n]*n\.rangeMin=0;n\.relative=1;n\.relSens=0\.64;n\.relStep=0\.3" ("warthunder import should read relative control and the one-sided range: " + $wtsThrottle.Output)
    # Game-wide settings become the profile's settings (device *), in the game's units.
    $wtsFfbShow = Run-Engine @("--profile", $wtsProfile, "ffb", "show", "--device", "*", "--game", "WarThunder", "--text")
    Assert-Match $wtsFfbShow.Output "aileronsMultiplier=79;autotrim=1;elevatorMultiplier=64;forceGain=50;" ("warthunder import should read the game-wide settings: " + $wtsFfbShow.Output)
    Assert-Match $wtsFfbShow.Output "holdThrottleForWEP=0" "warthunder import should read holdThrottleForWEP from controls params."

    # Round trip: nothing changes.
    $wtsRoundTrip = Run-Engine @("--profile", $wtsProfile, "warthunder", "export", $wtsBlk, "--text")
    Assert-True ($wtsRoundTrip.ExitCode -eq 0) ("warthunder export after import failed: " + $wtsRoundTrip.Output)
    Assert-True ([System.IO.File]::ReadAllText($wtsBlk) -eq $wtsOriginal) "warthunder import/export of every axis setting must leave machine.blk byte-identical."

    # Dead zone 0 is written (it used to be skipped); a value equal to the base preset drops the line (the game's diff
    # style); other values are written, 0 included.
    Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "set", "War Thunder Roll axis", "--scope", "game:WarThunder", "--param", "innerDeadzone=0", "--param", "nonlinearity=0.5", "--text") | Out-Null
    Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "set", "War Thunder Pitch axis", "--scope", "game:WarThunder", "--param", "kMul=1.1", "--param", "kAdd=0", "--param", "relative=0", "--text") | Out-Null
    Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "set", "War Thunder Throttle axis", "--scope", "game:WarThunder", "--inverted", "false", "--param", "relSens=0.25", "--param", "rangeMax=0.8", "--text") | Out-Null
    $wtsExport = Run-Engine @("--profile", $wtsProfile, "warthunder", "export", $wtsBlk, "--text")
    Assert-True ($wtsExport.ExitCode -eq 0) ("warthunder export of the tuning failed: " + $wtsExport.Output)
    $wtsAfter = [System.IO.File]::ReadAllText($wtsBlk)
    Assert-Match $wtsAfter "(?s)ailerons\{\s*axisId:i=7\s*innerDeadzone:r=0\s*nonlinearity:r=0\.5\s*\}" ("warthunder export should write dead zone 0 and the nonlinearity: " + $wtsAfter)
    Assert-Match $wtsAfter "(?s)elevator\{\s*axisId:i=8\s*inverse:b=yes\s*nonlinearity:r=2\s*\}" ("warthunder export should drop kMul/kAdd lines equal to the base preset: " + $wtsAfter)
    Assert-Match $wtsAfter "(?s)throttle\{\s*axisId:i=12\s*relative:b=yes\s*relSens:r=0\.25\s*relStep:r=0\.3\s*rangeMin:r=0\s*inverse:b=no\s*rangeMax:r=0\.8\s*\}" ("warthunder export should write 'inverse:b=no' against the inverted base and the range: " + $wtsAfter)
    Assert-Match $wtsAfter "(?s)gm_throttle\{\s*axisId:i=13\s*kMul:r=0\.02\s*\}" "warthunder export must not touch non-flight axes."
    Assert-Match $wtsAfter "(?s)settings\{\s*aileronsMultiplier:r=0\.79" "warthunder export without changed settings must keep settings{}."
    # Re-import gives the same values (what the game uses).
    $wtsReimport = Run-Engine @("warthunder", "scan", $wtsBlk, "--text")
    Assert-True ($wtsReimport.ExitCode -eq 0) "warthunder scan after export failed."

    # Preview: the game's formula. Pitch now: inverted, nonlinearity 2, kMul 1.1 (base), dead zone 0.02 (base):
    # x = -0.5 -> v = 0.55 -> (0.55 - 0.02) / 0.98 = 0.540816 -> ^3 = 0.158179.
    $wtsPreview = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "preview", "War Thunder Pitch axis", "--game", "WarThunder", "--points", "5", "--text")
    Assert-Match $wtsPreview.Output "-0\.500 -> \+0\.15817[89]" ("warthunder preview should evaluate the game's curve: " + $wtsPreview.Output)
    # One-sided throttle with range 0..0.8 shows 0.8 at full travel as +0.6 (0..1 drawn as -1..1).
    $wtsThrottlePreview = Run-Engine @("--profile", $wtsProfile, "binds", "tuning", "preview", "War Thunder Throttle axis", "--game", "WarThunder", "--points", "3", "--text")
    Assert-Match $wtsThrottlePreview.Output "\+1\.000 -> \+0\.6(0000)?" ("warthunder preview should map the one-sided range: " + $wtsThrottlePreview.Output)

    # Game-wide settings: only the keys JoyPro sets change, in their blocks.
    $wtsBefore = [System.IO.File]::ReadAllLines($wtsBlk)
    Run-Engine @("--profile", $wtsProfile, "ffb", "set", "--device", "*", "--game", "WarThunder", "--param", "forceGain=80", "--param", "joyFX=0", "--param", "holdThrottleForWEP=true", "--param", "helicopterPedalsMultiplier=50", "--text") | Out-Null
    $wtsSettingsExport = Run-Engine @("--profile", $wtsProfile, "warthunder", "export", $wtsBlk, "--text")
    Assert-True ($wtsSettingsExport.ExitCode -eq 0) ("warthunder export of the game-wide settings failed: " + $wtsSettingsExport.Output)
    $wtsSettingsAfter = [System.IO.File]::ReadAllLines($wtsBlk)
    $wtsChanged = @(Compare-Object $wtsBefore $wtsSettingsAfter)
    Assert-True ($wtsChanged.Count -eq 8) ("warthunder settings export should change exactly 4 lines: " + ($wtsChanged | Out-String))
    $wtsSettingsText = [System.IO.File]::ReadAllText($wtsBlk)
    Assert-Match $wtsSettingsText "(?s)settings\{[^}]*helicopterPedalsMultiplier:r=0\.5[^}]*forceGain:r=1\.6" "warthunder export should write forceGain as UI / 50 and multipliers as % / 100."
    Assert-Match $wtsSettingsText "(?s)OPTIONS_MODE_GAMEPLAY\{\s*USEROPT_JOYFX:b=no\s*USEROPT_HELPERS_MODE:i=2" "warthunder export should switch force feedback off in OPTIONS_MODE_GAMEPLAY."
    Assert-Match $wtsSettingsText "(?s)OPTIONS_MODE_TRAINING\{\s*USEROPT_JOYFX:b=yes" "warthunder export must not touch other option modes."
    Assert-Match $wtsSettingsText "(?s)params\{\s*holdThrottleForWEP:b=yes\s*trackIrZoom:b=no" "warthunder export should set holdThrottleForWEP in controls params."
    Assert-Match $wtsSettingsText "(?s)__allowedConfigOverrides\{\s*invertY:b=no" "warthunder export must not touch config{}."

    # Settings without binds: an export of a profile without War Thunder binds still writes the game-wide settings.
    $wtsOnlyBlk = Join-Path $wtsDir "settings-only.blk"
    $wtsOnlyProfile = Join-Path $wtsDir "settings-only.pr0file"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine_settings.blk") -Destination $wtsOnlyBlk -Force
    Run-Engine @("--profile", $wtsOnlyProfile, "ffb", "set", "--device", "*", "--game", "WarThunder", "--param", "autotrim=false", "--param", "invertT=true", "--text") | Out-Null
    $wtsOnly = Run-Engine @("--profile", $wtsOnlyProfile, "warthunder", "export", $wtsOnlyBlk, "--text")
    Assert-True ($wtsOnly.ExitCode -eq 0) ("warthunder settings-only export failed: " + $wtsOnly.Output)
    $wtsOnlyText = [System.IO.File]::ReadAllText($wtsOnlyBlk)
    Assert-Match $wtsOnlyText "USEROPT_AUTOTRIM:b=no" "warthunder settings-only export should write autotrim."
    Assert-Match $wtsOnlyText "USEROPT_HELPERS_MODE:i=3" "autotrim off should set the full-real control mode (the game derives autotrim from it)."
    Assert-Match $wtsOnlyText "invertT:b=yes" "warthunder settings-only export should write invertT."
    Assert-Match $wtsOnlyText "(?s)elevator\{\s*axisId:i=8\s*inverse:b=yes\s*nonlinearity:r=2\s*kMul:r=1\.5" "warthunder settings-only export must not touch the axes."
    Assert-True (Test-Path ($wtsOnlyBlk + ".jpbak")) "warthunder settings-only export should back up the file."

    # A file without the blocks gets them (machine.blk of a fresh install has settings{} but maybe no gui_options).
    $wtsBareBlk = Join-Path $wtsDir "bare.blk"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine.blk") -Destination $wtsBareBlk -Force
    $wtsBare = Run-Engine @("--profile", $wtsOnlyProfile, "warthunder", "export", $wtsBareBlk, "--text")
    Assert-True ($wtsBare.ExitCode -eq 0) ("warthunder settings export into a file without gui_options failed: " + $wtsBare.Output)
    Assert-Match ([System.IO.File]::ReadAllText($wtsBareBlk)) "(?s)gui_options\{\s*OPTIONS_MODE_GAMEPLAY\{\s*USEROPT_AUTOTRIM:b=no\s*USEROPT_HELPERS_MODE:i=3\s*\}\s*\}\s*\}\s*$" "warthunder export should create gui_options{OPTIONS_MODE_GAMEPLAY{}} at the end of content{}."

    # Base inheritance: a relation without War Thunder tuning gets the base (DCS) values fitted to the game's model:
    # multiplier 1 / saturation X, dead zone x multiplier (exact for the linear part), nonlinearity closest to the curve.
    $wtsFitProfile = Join-Path $wtsDir "fit.pr0file"
    $wtsFitBlk = Join-Path $wtsDir "fit.blk"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine.blk") -Destination $wtsFitBlk -Force
    Run-Engine @("--profile", $wtsFitProfile, "relations", "add", "WT Fit Roll", "--axis", "--node", "WarThunder:ailerons", "--device", $wtsStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $wtsFitProfile, "binds", "tuning", "set", "WT Fit Roll", "--scope", "base", "--deadzone", "0.1", "--saturation-x", "0.8", "--curvature", "0.2", "--text") | Out-Null
    $wtsFit = Run-Engine @("--profile", $wtsFitProfile, "binds", "tuning", "show", "WT Fit Roll", "--game", "WarThunder", "--text")
    Assert-Match $wtsFit.Output "WarThunder uses \(warthunder, fitted from base\)|WarThunder uses \(warthunder, from base" ("warthunder should fit the base when the relation has no War Thunder tuning: " + $wtsFit.Output)
    Assert-Match $wtsFit.Output "n\.innerDeadzone=0\.125;n\.kAdd=0;n\.kMul=1\.25;n\.nonlinearity=[0-9.]+" ("warthunder fit should be kMul = 1/0.8, dead zone = 0.1 x 1.25: " + $wtsFit.Output)
    $wtsFitExport = Run-Engine @("--profile", $wtsFitProfile, "warthunder", "export", $wtsFitBlk, "--text")
    Assert-True ($wtsFitExport.ExitCode -eq 0) ("warthunder export of a fitted tuning failed: " + $wtsFitExport.Output)
    Assert-Match ([System.IO.File]::ReadAllText($wtsFitBlk)) "(?s)ailerons\{\s*axisId:i=7\s*mouseAxisId:i=-1\s*nonlinearity:r=[0-9.]+\s*innerDeadzone:r=0\.125\s*kMul:r=1\.25\s*\}" "warthunder export should write the fitted values (kAdd 0 = base, no line)."

    # An unknown base preset is reported: missing attributes are then only the code defaults.
    $wtsUnknownBlk = Join-Path $wtsDir "unknown.blk"
    [System.IO.File]::WriteAllText($wtsUnknownBlk, $wtsOriginal.Replace("hotkey.thrustmaster_warthog.blk", "hotkey.some_future_preset.blk"))
    $wtsUnknown = Run-Engine @("warthunder", "scan", $wtsUnknownBlk, "--text")
    Assert-Match $wtsUnknown.Output "base preset 'hotkey\.some_future_preset' is not in JoyPro's table" "warthunder scan should warn about a base preset it has no values for."

    # The game must be closed: a process named aces refuses the export (the game rewrites machine.blk on exit).
    $wtsFakeGame = Join-Path $wtsDir "aces.exe"
    Copy-Item -Path (Join-Path $env:SystemRoot "System32\PING.EXE") -Destination $wtsFakeGame -Force
    $wtsPreviousIgnore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
    $wtsGame = Start-Process -FilePath $wtsFakeGame -ArgumentList @("-n", "30", "127.0.0.1") -WindowStyle Hidden -PassThru
    try {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = "0"
        $wtsBeforeRefusal = [System.IO.File]::ReadAllText($wtsOnlyBlk)
        $wtsRefused = Run-Engine @("--profile", $wtsOnlyProfile, "warthunder", "export", $wtsOnlyBlk, "--text")
        Assert-True ($wtsRefused.ExitCode -ne 0) "warthunder export must be refused while aces runs."
        Assert-Match $wtsRefused.Output "War Thunder is running" "warthunder export should say why it refused."
        Assert-True ([System.IO.File]::ReadAllText($wtsOnlyBlk) -eq $wtsBeforeRefusal) "a refused warthunder export must not write."
    }
    finally {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $wtsPreviousIgnore
        if (-not $wtsGame.HasExited) { Stop-Process -Id $wtsGame.Id -Force -ErrorAction SilentlyContinue }
        $wtsGame.WaitForExit(5000) | Out-Null
    }
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $wtsPreviousFakeDevices
}

# DB refresh straight from the game's archives: a game folder with gui.vromfs.bin (scripts/controls/shortcutslist/
# shortcutsgroup*.nut) and lang.vromfs.bin (lang/controls.csv). The fixtures are built here as stored 'VRFs' archives
# (the game's are zstd with masked bytes; the same reader handles both). Runs in a temporary JoyPro folder so the shipped
# catalog stays untouched.
function New-WtsVromfs([string]$Path, [hashtable]$Files) {
    $names = @($Files.Keys | Sort-Object)
    $count = $names.Count
    $namesOffset = 32
    $dataOffset = $namesOffset + 8 * $count
    $stringsOffset = $dataOffset + 16 * $count
    $strings = New-Object System.IO.MemoryStream
    $nameAt = @()
    foreach ($n in $names) { $nameAt += $stringsOffset + $strings.Length; $b = [System.Text.Encoding]::UTF8.GetBytes($n); $strings.Write($b, 0, $b.Length); $strings.WriteByte(0) }
    $contentOffset = $stringsOffset + $strings.Length
    $content = New-Object System.IO.MemoryStream
    $fileAt = @()
    foreach ($n in $names) { $fileAt += $contentOffset + $content.Length; $b = [System.Text.Encoding]::UTF8.GetBytes($Files[$n]); $content.Write($b, 0, $b.Length) }
    $body = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($body)
    $w.Write([int]$namesOffset); $w.Write([int]$count); $w.Write([long]0)
    $w.Write([int]$dataOffset); $w.Write([int]$count); $w.Write([long]0)
    foreach ($a in $nameAt) { $w.Write([long]$a) }
    for ($i = 0; $i -lt $count; $i++) { $w.Write([int]$fileAt[$i]); $w.Write([int][System.Text.Encoding]::UTF8.GetByteCount($Files[$names[$i]])); $w.Write([long]0) }
    $w.Write($strings.ToArray()); $w.Write($content.ToArray()); $w.Flush()
    $bodyBytes = $body.ToArray()
    $out = New-Object System.IO.MemoryStream
    $ow = New-Object System.IO.BinaryWriter($out)
    $ow.Write([System.Text.Encoding]::ASCII.GetBytes("VRFs")); $ow.Write([byte[]](0, 0, 0x50, 0x43)); $ow.Write([int]$bodyBytes.Length); $ow.Write([int]0); $ow.Write($bodyBytes); $ow.Flush()
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
}

$wtrDir = Join-Path $tempDir "warthunder-refresh"
$wtrGame = Join-Path $wtrDir "game"
$wtrRoot = Join-Path $wtrDir "joypro"
New-Item -Path $wtrGame, (Join-Path $wtrRoot "DB\WarThunder"), (Join-Path $wtrRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\WarThunder\*") -Destination (Join-Path $wtrRoot "DB\WarThunder") -Force
New-WtsVromfs (Join-Path $wtrGame "gui.vromfs.bin") @{
    "scripts/controls/shortcutslist/shortcutsgroupaircraft.nut" = "from `"x`" import y`r`nreturn [`r`n  { id = `"ID_SMOKE_REFRESH_FIRE`" }`r`n  { id = `"smoke_refresh_axis`" type = CONTROL_TYPE.AXIS }`r`n  { id = `"ID_SMOKE_HEADER`" type = CONTROL_TYPE.HEADER }`r`n]"
    "scripts/controls/shortcutslist/shortcutsgrouphelicopter.nut" = "return [`r`n  { id = `"ID_SMOKE_HELI_TRIM`" }`r`n]"
    "scripts/other.nut" = "return []"
}
New-WtsVromfs (Join-Path $wtrGame "lang.vromfs.bin") @{
    "lang/controls.csv" = "`"<ID|readonly|noverify>`";`"<English>`"`r`n`"hotkeys/ID_SMOKE_REFRESH_FIRE`";`"Smoke refresh fire`"`r`n`"controls/smoke_refresh_axis`";`"Smoke refresh axis`"`r`n"
}
$wtrPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $wtrDir "data"
# The refresh writes into the game-data folder (<data>\GameData), never into the program folder ($wtrRoot).
$wtrList = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\WarThunder"
Push-Location $wtrRoot
try {
    # With archive reading turned off (Settings > Games) JoyPro does not unpack the game's archives: the list comes
    # from JoyPro's own function list and no shortcut script is read.
    $wtrOwn = Run-Engine @("warthunder", "refresh-db", $wtrGame, "--no-game-archives", "--text")
    Assert-True ($wtrOwn.ExitCode -eq 0) ("warthunder refresh-db --no-game-archives failed: " + $wtrOwn.Output)
    Assert-Match $wtrOwn.Output "JoyPro's own function list" "warthunder refresh-db --no-game-archives must not unpack the archives."
    Assert-NotMatch $wtrOwn.Output "shortcut script" "warthunder refresh-db must not read the shortcut scripts with --no-game-archives."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $wtrList "actions.tsv")) -notmatch "ID_SMOKE_REFRESH_FIRE") "the list built with archive reading off must not contain actions from the archives."
    $wtrOff = Run-Engine @("warthunder", "refresh-db", $wtrGame, "--read-game-archives", "false", "--text")
    Assert-Match $wtrOff.Output "JoyPro's own function list" "warthunder refresh-db --read-game-archives false must not unpack the archives."

    # Default: the archives are read (the user approved it); no switch needed.
    $wtr = Run-Engine @("warthunder", "refresh-db", $wtrGame, "--text")
    Assert-True ($wtr.ExitCode -eq 0) ("warthunder refresh-db from the game's archives failed: " + $wtr.Output)
    Assert-Match $wtr.Output "Read 2 shortcut script\(s\)" "warthunder refresh-db should read both shortcut scripts from gui.vromfs.bin."
    $wtrCatalog = [System.IO.File]::ReadAllText((Join-Path $wtrList "actions.tsv"))
    Assert-Match $wtrCatalog "ID_SMOKE_REFRESH_FIRE\tSmoke refresh fire\tbutton\tAircraft" "the refreshed catalog should have the aircraft shortcut with its title from lang/controls.csv."
    Assert-Match $wtrCatalog "smoke_refresh_axis\tSmoke refresh axis\taxis\tAircraft" "the refreshed catalog should have the aircraft axis."
    Assert-Match $wtrCatalog "ID_SMOKE_HELI_TRIM\tID_SMOKE_HELI_TRIM\tbutton\tHelicopter" "the refreshed catalog should have the helicopter shortcut."
    Assert-NotMatch $wtrCatalog "ID_SMOKE_HEADER" "UI-only entries (headers) must not become actions."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\WarThunder\actions.tsv")) -notmatch "ID_SMOKE_REFRESH_FIRE") "the shipped catalog must stay untouched."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $wtrRoot "DB\WarThunder\actions.tsv")) -notmatch "ID_SMOKE_REFRESH_FIRE") "the refresh must not write into the program folder."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $wtrPreviousData
}

# Cloud sync: the game keeps the higher of machine.blk's "version:i" and the server's. Restoring JoyPro's backup over
# a file the game saved since (higher version) must raise the version above it, or the server copy comes back.
$wcDir = Join-Path $tempDir "warthunder-cloud"
$wcBlk = Join-Path $wcDir "Documents\My Games\WarThunder\Saves\4242\production\machine.blk"
$wcProfile = Join-Path $wcDir "cloud.pr0file"
New-Item -Path (Split-Path -Parent $wcBlk) -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\warthunder\machine.blk") -Destination $wcBlk -Force
$wcOriginal = [System.IO.File]::ReadAllText($wcBlk)
$wcPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $wcDir "data"
try {
    Run-Engine @("--profile", $wcProfile, "ffb", "set", "--device", "*", "--game", "WarThunder", "--param", "forceGain=80", "--text") | Out-Null
    $wcExport = Run-Engine @("--profile", $wcProfile, "warthunder", "export", $wcBlk, "--text")
    Assert-True ($wcExport.ExitCode -eq 0) ("warthunder export for the cloud test failed: " + $wcExport.Output)
    Assert-Match ([System.IO.File]::ReadAllText($wcBlk)) "\Aversion:i=13\r?\n" "a War Thunder export must keep machine.blk's version (equal version with new content: the game uploads the file)."
    # The game saves (version 13 -> 15), then the user restores JoyPro's backup.
    [System.IO.File]::WriteAllText($wcBlk, ([System.IO.File]::ReadAllText($wcBlk) -replace '\Aversion:i=13', 'version:i=15'))
    $wcRestore = Run-Engine @("backups", "restore", "latest", "--game", "warthunder", "--text")
    Assert-True ($wcRestore.ExitCode -eq 0) ("backups restore of machine.blk failed: " + $wcRestore.Output)
    Assert-Match $wcRestore.Output "War Thunder version raised" ("backups restore should say it raised the version: " + $wcRestore.Output)
    $wcRestored = [System.IO.File]::ReadAllText($wcBlk)
    Assert-Match $wcRestored "\Aversion:i=16\r?\n" "the restored machine.blk must get a version above the file it replaced (15 -> 16)."
    Assert-True (($wcRestored -replace '\Aversion:i=16', 'version:i=13') -eq $wcOriginal) "apart from the version the restore must bring back the original machine.blk."
}
finally {
    $env:JOYPRO_DATA_DIR = $wcPreviousData
}
