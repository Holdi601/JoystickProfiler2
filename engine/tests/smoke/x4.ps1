# X4: Foundations axis and game settings (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineDir).
# Fixture x4-settings: Smoke Stick pinned to slot 2 (pitch Y, primary steering X), Smoke Throttle to slot 3 (throttle
# Z), a gamepad pitch on the unpinned slot 1; <config> as X4 writes it (no bidirectionalthrottle = On, as the game's menu shows).
$xsDir = Join-Path $tempDir "x4-settings\5678"
$xsMap = Join-Path $xsDir "inputmap.xml"
$xsConfig = Join-Path $xsDir "config.xml"
$xsProfile = Join-Path $tempDir "x4-settings.pr0file"
$xsFixture = Join-Path $engineDir "tests\fixtures\x4-settings\5678"
New-Item -Path $xsDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $xsFixture "*") -Destination $xsDir -Force
$previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\bms\devices.tsv"

# Lines of a file that are not in the fixture's copy, and the fixture's lines that are gone.
function Get-X4Diff([string]$Path, [string]$Name) {
    $now = [IO.File]::ReadAllText($Path) -split "`n"
    $was = [IO.File]::ReadAllText((Join-Path $xsFixture $Name)) -split "`n"
    return [pscustomobject]@{
        Added = @($now | Where-Object { $was -notcontains $_ } | ForEach-Object { $_.Trim() })
        Removed = @($was | Where-Object { $now -notcontains $_ } | ForEach-Object { $_.Trim() })
    }
}

function Reset-X4Settings {
    Copy-Item -Path (Join-Path $xsFixture "*") -Destination $xsDir -Force
}

try {
    $xsFfbModels = Run-Engine @("ffb", "models", "--text")
    Assert-Match $xsFfbModels.Output "X4 - X4: Foundations" "ffb models should list the X4 game settings."

    $xsScan = Run-Engine @("x4", "scan", "--path", $xsMap, "--text")
    Assert-True ($xsScan.ExitCode -eq 0) ("x4 scan of the settings fixture failed: " + $xsScan.Output)
    Assert-Match $xsScan.Output "(?m)^Bindings Read:\s+4\s*$" "x4 scan should read the three axes of the pinned slots and the keyboard bind."
    Assert-Match $xsScan.Output "deadzone=0.*invertSteeringPitch=1.*rumbleIntensity=100, autoRoll=1, gamepadMode=2" ("x4 scan should report the game settings of both files: " + $xsScan.Output)

    # Import: X4's per-control invert in JoyPro's direction (file XOR the HOTAS default), the throttle two-sided
    # (a missing bidirectionalthrottle is On in the game).
    $xsImport = Run-Engine @("--profile", $xsProfile, "x4", "import", "--path", $xsMap, "--replace", "--text")
    Assert-True ($xsImport.ExitCode -eq 0) ("x4 import failed: " + $xsImport.Output)
    $xsPitch = "X4: Foundations (experimental) Steer Up-Down"
    $xsThrottle = "X4: Foundations (experimental) Throttle"
    $xsYaw = "X4: Foundations (experimental) Primary Steering Axis"
    $xsPitchShow = Run-Engine @("--profile", $xsProfile, "binds", "tuning", "show", $xsPitch, "--game", "X4", "--text")
    Assert-Match $xsPitchShow.Output "base: inv=0;" ("X4's inverted pitch (its HOTAS default) must import as a relation without invert: " + $xsPitchShow.Output)
    $xsThrottleShow = Run-Engine @("--profile", $xsProfile, "binds", "tuning", "show", $xsThrottle, "--game", "X4", "--text")
    Assert-NotMatch $xsThrottleShow.Output "sl=1" ("an X4 throttle in a file without bidirectionalthrottle (On) should import two-sided: " + $xsThrottleShow.Output)
    Assert-NotMatch $xsThrottleShow.Output "inv=1" ("an X4 throttle should import not inverted: " + $xsThrottleShow.Output)
    $xsYawShow = Run-Engine @("--profile", $xsProfile, "binds", "tuning", "show", $xsYaw, "--game", "X4", "--text")
    Assert-Match $xsYawShow.Output "base: inv=0;" ("the primary steering axis (not inverted by default) imports without invert: " + $xsYawShow.Output)

    $xsRoundTrip = Run-Engine @("--profile", $xsProfile, "x4", "export", "--path", $xsMap, "--text")
    Assert-True ($xsRoundTrip.ExitCode -eq 0) ("x4 export of the imported profile failed: " + $xsRoundTrip.Output)
    Assert-Match $xsRoundTrip.Output "already matches" ("x4 export right after the import must change nothing: " + $xsRoundTrip.Output)

    # Relation invert -> invert_steering_pitch (XOR), a one-sided throttle -> bidirectional throttle off, written
    # explicitly (added last in <config>).
    Run-Engine @("--profile", $xsProfile, "binds", "set", $xsPitch, "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $xsProfile, "binds", "tuning", "set", $xsThrottle, "--scope", "game:X4", "--slider", "true", "--text") | Out-Null
    $xsExport = Run-Engine @("--profile", $xsProfile, "x4", "export", "--path", $xsMap, "--text")
    Assert-True ($xsExport.ExitCode -eq 0) ("x4 export of the changed relations failed: " + $xsExport.Output)
    $xsDiff = Get-X4Diff $xsMap "inputmap.xml"
    Assert-SetEquivalent $xsDiff.Added @('<param name="invert_steering_pitch" value="false"/>', '<param name="bidirectionalthrottle" value="false"/>') ("x4 export should change the pitch inversion and add bidirectional throttle off: " + ($xsDiff.Added -join " | "))
    Assert-SetEquivalent $xsDiff.Removed @('<param name="invert_steering_pitch" value="true"/>') ("x4 export should replace only the pitch inversion: " + ($xsDiff.Removed -join " | "))
    $xsText = [IO.File]::ReadAllText($xsMap)
    Assert-Match $xsText "<param name=`"yaw_into_roll`" value=`"0`"/>\n    <param name=`"bidirectionalthrottle`" value=`"false`"/>\n  </config>" "x4 export should add a missing param as the last line of <config>."
    Assert-NotMatch $xsText "`r" "x4 settings export must keep LF line ends."
    Assert-Match $xsExport.Output "INPUT_RANGE_STEERING_PITCH is also bound on another device" ("x4 export should warn that the inversion also applies to the gamepad's pitch: " + $xsExport.Output)
    Assert-True ([IO.File]::ReadAllText($xsConfig) -eq [IO.File]::ReadAllText((Join-Path $xsFixture "config.xml"))) "x4 export must not touch config.xml without Settings > Game values."

    # Two pitch relations that disagree: the inversion stays and the export says why.
    Run-Engine @("--profile", $xsProfile, "relations", "add", "X4 Pitch 2", "--axis", "--node", "X4:INPUT_RANGE_STEERING_PITCH", "--device", "Smoke Throttle {66666666-7777-8888-9999-000000000000}", "--input", "JOY_RX", "--text") | Out-Null
    $xsDisagree = Run-Engine @("--profile", $xsProfile, "x4", "export", "--path", $xsMap, "--text")
    Assert-Match $xsDisagree.Output "the relations disagree on invert" ("x4 export should warn when the relations of one control disagree: " + $xsDisagree.Output)
    Assert-Match ([IO.File]::ReadAllText($xsMap)) '<param name="invert_steering_pitch" value="false"/>' "x4 export must leave the inversion as it is when the relations disagree."
    Run-Engine @("--profile", $xsProfile, "binds", "clear", "X4 Pitch 2", "--text") | Out-Null

    # Game-wide settings: explicit values win; Settings > Game goes to config.xml in place.
    Reset-X4Settings
    $xsFfb = Run-Engine @("--profile", $xsProfile, "ffb", "set", "--device", "*", "--game", "X4", "--param", "deadzone=7", "--param", "invertSteeringPitch=true", "--param", "adaptiveSteering=true", "--param", "rumbleIntensity=40", "--param", "autoRoll=false", "--param", "gamepadMode=1", "--text")
    Assert-True ($xsFfb.ExitCode -eq 0) ("ffb set for X4 failed: " + $xsFfb.Output)
    $xsSettings = Run-Engine @("--profile", $xsProfile, "x4", "export", "--path", $xsMap, "--text")
    Assert-True ($xsSettings.ExitCode -eq 0) ("x4 export with game settings failed: " + $xsSettings.Output)
    Assert-Match $xsSettings.Output "reverses the relation's direction" ("an explicit inversion against the relation should be reported: " + $xsSettings.Output)
    $xsDiff = Get-X4Diff $xsMap "inputmap.xml"
    Assert-SetEquivalent $xsDiff.Added @('<param name="deadzone" value="7"/>', '<param name="stick_steering_adaptive" value="true"/>', '<param name="bidirectionalthrottle" value="false"/>') ("x4 export should write the dead zone, adaptive steering and bidirectional throttle: " + ($xsDiff.Added -join " | "))
    $xsConfigDiff = Get-X4Diff $xsConfig "config.xml"
    Assert-SetEquivalent $xsConfigDiff.Added @('<autoroll>false</autoroll>', '<rumbleintensity>0.40</rumbleintensity>', '<gamepadmode>1</gamepadmode>') ("x4 export should set the config.xml values in place: " + ($xsConfigDiff.Added -join " | "))
    Assert-True ($xsConfigDiff.Removed.Count -eq 3) "x4 export should replace exactly three config.xml lines."
    Assert-True (Test-Path ($xsConfig + ".jpbak")) "x4 export should back up config.xml."
    $xsAgain = Run-Engine @("--profile", $xsProfile, "x4", "export", "--path", $xsMap, "--text")
    Assert-Match $xsAgain.Output "already matches" ("a second x4 export with game settings should change nothing: " + $xsAgain.Output)

    # A profile without X4 binds exports the game settings only (no release, even with clean and nuke).
    Reset-X4Settings
    $xsOnly = Join-Path $tempDir "x4-settings-only.pr0file"
    Run-Engine @("--profile", $xsOnly, "ffb", "set", "--device", "*", "--game", "X4", "--param", "deadzone=12", "--text") | Out-Null
    $xsOnlyExport = Run-Engine @("--profile", $xsOnly, "x4", "export", "--path", $xsMap, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($xsOnlyExport.ExitCode -eq 0) ("x4 settings-only export failed: " + $xsOnlyExport.Output)
    $xsDiff = Get-X4Diff $xsMap "inputmap.xml"
    Assert-SetEquivalent $xsDiff.Added @('<param name="deadzone" value="12"/>') ("a profile without X4 binds should only write the game settings: " + ($xsDiff.Added -join " | "))
    Assert-True ($xsDiff.Removed.Count -eq 1) "the settings-only export must not release any bind."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
}

# Catalog refresh from the installed game (no --path): the Controls menu (gameoptions.lua) and the English texts from
# the .cat/.dat archives, packed here from engine\tests\fixtures\x4-catalog. 01 holds an older gameoptions.lua that 02
# overrides, and a path with a space. Without X4.exe the numeric ids are named from order.tsv. Runs in a temporary
# JoyPro folder so the shipped catalog stays untouched.
function New-X4Archive([string]$Cat, [System.Collections.Specialized.OrderedDictionary]$Files) {
    $index = New-Object System.Text.StringBuilder
    $data = New-Object System.IO.MemoryStream
    foreach ($name in $Files.Keys) {
        $bytes = $Files[$name]
        [void]$index.Append($name + " " + $bytes.Length + " 1769440880 00000000000000000000000000000000`n")
        $data.Write($bytes, 0, $bytes.Length)
    }
    [System.IO.File]::WriteAllText($Cat, $index.ToString(), (New-Object System.Text.UTF8Encoding $false))
    [System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Cat, ".dat"), $data.ToArray())
}
$xcFixture = Join-Path $engineDir "tests\fixtures\x4-catalog"
$xcDir = Join-Path $tempDir "x4-catalog"
$xcGame = Join-Path $xcDir "X4 Foundations"
$xcRoot = Join-Path $xcDir "joypro"
New-Item -Path $xcGame, (Join-Path $xcRoot "DB\X4"), (Join-Path $xcRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\X4\*") -Destination (Join-Path $xcRoot "DB\X4") -Force
$xcOld = New-Object System.Collections.Specialized.OrderedDictionary
$xcOld["libraries/some file.xml"] = [System.Text.Encoding]::UTF8.GetBytes("<x/>")
$xcOld["ui/addons/ego_gameoptions/gameoptions.lua"] = [System.Text.Encoding]::UTF8.GetBytes("-- older version without the tables")
New-X4Archive (Join-Path $xcGame "01.cat") $xcOld
$xcNew = New-Object System.Collections.Specialized.OrderedDictionary
$xcNew["t/0001-l044.xml"] = [System.IO.File]::ReadAllBytes((Join-Path $xcFixture "t\0001-l044.xml"))
$xcNew["ui/addons/ego_gameoptions/gameoptions.lua"] = [System.IO.File]::ReadAllBytes((Join-Path $xcFixture "ui\addons\ego_gameoptions\gameoptions.lua"))
New-X4Archive (Join-Path $xcGame "02.cat") $xcNew
New-X4Archive (Join-Path $xcGame "02_sig.cat") (New-Object System.Collections.Specialized.OrderedDictionary)
$xcPreviousData = $env:JOYPRO_DATA_DIR
$xcPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $xcDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
# The refresh writes into the game-data folder (<data>\GameData), never into the program folder ($xcRoot).
$xcList = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\X4"
Push-Location $xcRoot
try {
    $xcSet = Run-Engine @("games", "install", "set", "x4", $xcGame, "--text")
    Assert-True ($xcSet.ExitCode -eq 0) ("games install set x4 failed: " + $xcSet.Output)
    $xcOrderBefore = [System.IO.File]::ReadAllText((Join-Path $xcRoot "DB\X4\order.tsv"))
    $xc = Run-Engine @("x4", "refresh-db", "--text")
    Assert-True ($xc.ExitCode -eq 0) ("x4 refresh-db from the game's archives failed: " + $xc.Output)
    Assert-Match $xc.Output "6 controls, 3 space flight, 2 new; control names from order\.tsv" ("x4 refresh-db should read the menu from the newer archive: " + $xc.Output)
    Assert-Match $xc.Output "actions 9999 has no known id" "a menu control without a known id should be reported and skipped."
    $xcCatalog = [System.IO.File]::ReadAllText((Join-Path $xcList "actions.tsv"))
    Assert-Match $xcCatalog "(?m)^INPUT_RANGE_STEERING_YAW\tSmoke Yaw & Roll\taxis\tspace/Smoke Group\r?$" "a new menu axis should come in with its game title (translator comment removed)."
    Assert-Match $xcCatalog "(?m)^INPUT_ACTION_TOGGLE_AUTO_SPEED\tSmoke Function\tbutton\tspace/Smoke Group; function 1\r?$" "a menu function should come in under its defining control and function name."
    Assert-Match $xcCatalog "(?m)^INPUT_RANGE_THROTTLE\tThrottle\taxis\tspace/Steering: Analog\r?$" "known controls must keep their title and category."
    Assert-NotMatch $xcCatalog "INPUT_RANGE_MOUSEWHEEL|INPUT_ACTION_DEBUG_7|INPUT_STATE_DIALOG_MENU" "mouse-only rows, the Options Menu group and the menus section are no flight controls."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $xcList "functions.tsv"))) "(?m)^INPUT_ACTION_TOGGLE_AUTO_SPEED\tINPUT_ACTION_TOGGLE_AUTO_SPEED,INPUT_ACTION_TOGGLECURSOR\r?$" "a new menu function should be added to functions.tsv."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $xcList "order.tsv")) -eq $xcOrderBefore) "without X4.exe order.tsv must stay as it is."
    $xcAgain = Run-Engine @("x4", "refresh-db", $xcFixture, "--text")
    Assert-True ($xcAgain.ExitCode -eq 0) ("x4 refresh-db from extracted files failed: " + $xcAgain.Output)
    Assert-Match $xcAgain.Output "nothing was written" ("a second refresh (extracted files) should find nothing new and write nothing: " + $xcAgain.Output)
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\X4\actions.tsv")) -notmatch "INPUT_ACTION_TOGGLE_AUTO_SPEED") "the shipped X4 catalog must stay untouched."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $xcPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $xcPreviousDetection
}
