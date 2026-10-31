# Star Citizen axis settings (docs/games/starcitizen.md), dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir, $engineDir, $starCitizenDbCatalogPath; the main script restores the DB catalog afterwards).
# Fixtures: engine\tests\smoke\fixtures\starcitizen (catalog with option groups and a joystick option tree, a user
# actionmaps.xml with option groups and deviceoptions, attributes.xml, a defaultProfile.xml for the DB refresh).

$scFixtures = Join-Path $engineDir "tests\smoke\fixtures\starcitizen"
$scUtf8 = New-Object System.Text.UTF8Encoding($false)
$scStick = "Test Stick {11111111-1111-1111-1111-111111111111}"

function ScNew-Profile([string]$name, [string]$actionmapsText) {
    $root = Join-Path $tempDir ("sc_axis_" + $name)
    $dir = Join-Path $root "LIVE\USER\Client\0\Profiles\default"
    New-Item -Path $dir -ItemType Directory -Force | Out-Null
    $file = Join-Path $dir "actionmaps.xml"
    [System.IO.File]::WriteAllText($file, $actionmapsText, $scUtf8)
    return [PSCustomObject]@{ Root = $root; Dir = $dir; File = $file }
}

# ---- DB refresh: option group per action and the joystick option tree go into the catalog --------------------------
$scRefreshDb = Join-Path $tempDir "sc_axis_db"
$scRefreshEmpty = Join-Path $tempDir "sc_axis_empty_install"
New-Item -Path $scRefreshEmpty -ItemType Directory -Force | Out-Null
$scRefresh = Run-Engine @("db", "refresh-starcitizen", "--source", (Join-Path $scFixtures "defaultProfile.xml"), "--install-path", $scRefreshEmpty, "--include-user-profiles", "false", "--db-path", $scRefreshDb, "--text")
Assert-True ($scRefresh.ExitCode -eq 0) ("db refresh-starcitizen with a defaultProfile.xml failed: " + $scRefresh.Output)
Assert-Match $scRefresh.Output "(?m)^Actions With Option Group:\s+2\s*$" "db refresh-starcitizen should count the actions with an option group."
Assert-Match $scRefresh.Output "(?m)^Joystick Option Groups:\s+4\s*$" "db refresh-starcitizen should keep the joystick option tree."
$scRefreshCatalog = [System.IO.File]::ReadAllText((Join-Path $scRefreshDb "AllBinds.xml"))
Assert-Match $scRefreshCatalog '<action name="v_pitch"><!--@ui_CIPitch//true--><optionGroup name="flight_move_pitch"/><defaultJoystick input="y"/></action>' "the catalog should keep the action line older builds read and add the option group and the game's default joystick input after the comment."
Assert-Match $scRefreshCatalog '<action name="v_fire"><!--@ui_CIFire//false--><defaultJoystick input="button1"/></action>' "an action without option group keeps the old catalog line plus the game's default joystick input."
Assert-Match $scRefreshCatalog '<optiontree type="joystick"' "the catalog should contain the joystick option tree."
Assert-NotMatch $scRefreshCatalog '<optiontree type="keyboard"' "the catalog should only keep the joystick option tree."

# The fixture catalog for the rest of the checks.
Copy-Item -Path (Join-Path $scFixtures "catalog.xml") -Destination $starCitizenDbCatalogPath -Force

# ---- Import: the file's option groups and device options become game:StarCitizen tuning ------------------------------
$scFixtureText = [System.IO.File]::ReadAllText((Join-Path $scFixtures "actionmaps.xml")).Replace("`r`n", "`n").Replace("`n", "`r`n")
$scImport = ScNew-Profile "import" $scFixtureText
# Relation names as JoyPro wrote them (actionmaps.jp).
$scMeta = $scFixtureText.Replace('<rebind input="js1_y"/>', '<rebind input="js1_y"/><!--SC Pitch//true-->').Replace('<rebind input="js1_rotz"/>', '<rebind input="js1_rotz"/><!--SC Roll//true-->').Replace('<rebind input="js1_x"/>', '<rebind input="js1_x"/><!--SC Yaw//true-->')
[System.IO.File]::WriteAllText((Join-Path $scImport.Dir "actionmaps.jp"), $scMeta, $scUtf8)
[System.IO.File]::Copy((Join-Path $scFixtures "attributes.xml"), (Join-Path $scImport.Dir "attributes.xml"), $true)
$scProfile = Join-Path $tempDir "sc_axis.pr0file"
$scImported = Run-Engine @("--profile", $scProfile, "starcitizen", "import", $scImport.Root, "--text")
Assert-True ($scImported.ExitCode -eq 0) ("starcitizen import with axis settings failed: " + $scImported.Output)
Assert-Match $scImported.Output "(?m)^Axis Settings Imported:\s+2\s*$" "starcitizen import should take over the axis settings of yaw and roll (pitch is neutral)."
$scYawShow = Run-Engine @("--profile", $scProfile, "binds", "tuning", "show", "SC Yaw", "--game", "StarCitizen", "--text")
Assert-Match $scYawShow.Output "game:StarCitizen: inv=1;sl=0;dz=0\.1;sx=1;sy=1;cv=0;n\.exponent=2" "import should store yaw's invert/exponent (option group) and dead zone (deviceoptions) as Star Citizen tuning."
Assert-Match $scYawShow.Output "base: inv=0;sl=0;dz=0;" "import must keep the base at JoyPro's defaults."
$scRollShow = Run-Engine @("--profile", $scProfile, "binds", "tuning", "show", "SC Roll", "--game", "StarCitizen", "--text")
Assert-Match $scRollShow.Output "game:StarCitizen: inv=0;sl=0;dz=0;sx=0\.9;sy=1;cv=0\|0\.02\|0\.05\|0\.1\|0\.16\|0\.25\|0\.36\|0\.49\|0\.64\|0\.81\|1" "import should store roll's curve and saturation."
$scPitchShow = Run-Engine @("--profile", $scProfile, "binds", "tuning", "show", "SC Pitch", "--game", "StarCitizen", "--text")
Assert-NotMatch $scPitchShow.Output "game:StarCitizen:" "a neutral group must not add Star Citizen tuning."

# ---- Export unchanged: the round trip keeps every byte ---------------------------------------------------------------
$scBefore = [System.IO.File]::ReadAllText($scImport.File)
$scRoundTrip = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-True ($scRoundTrip.ExitCode -eq 0) ("starcitizen export after import failed: " + $scRoundTrip.Output)
Assert-True ([System.IO.File]::ReadAllText($scImport.File) -eq $scBefore) ("import + export without changes must keep actionmaps.xml byte-identical: " + [System.IO.File]::ReadAllText($scImport.File))
Assert-True (Test-Path ($scImport.File + ".jpbak")) "starcitizen export should back up actionmaps.xml (GameExportSafety)."

# ---- Export target: --path wins over --source (the import's folder); source first wrote into the live profile -------
$scCopyRoot = Join-Path $tempDir "sc_export_copy"
Copy-Item -LiteralPath $scImport.Root -Destination $scCopyRoot -Recurse -Force
$scCopyFile = $scImport.File.Replace($scImport.Root, $scCopyRoot)
Remove-Item -LiteralPath ($scCopyFile + ".jpbak") -Force -ErrorAction SilentlyContinue
$scSourceStamp = (Get-Item -LiteralPath $scImport.File).LastWriteTimeUtc
Start-Sleep -Milliseconds 50
$scPathExport = Run-Engine @("--profile", $scProfile, "starcitizen", "export", "--path", $scCopyRoot, "--source", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-True ($scPathExport.ExitCode -eq 0) ("starcitizen export with --path and --source failed: " + $scPathExport.Output)
Assert-True (Test-Path ($scCopyFile + ".jpbak")) "starcitizen export with --path must write into the --path folder."
Assert-True ((Get-Item -LiteralPath $scImport.File).LastWriteTimeUtc -eq $scSourceStamp) "starcitizen export with --path must not touch the --source folder."

# ---- Export of changed settings ----------------------------------------------------------------------------------------
$scSet = Run-Engine @("--profile", $scProfile, "binds", "tuning", "set", "SC Pitch", "--scope", "game:StarCitizen", "--inverted", "true", "--param", "exponent=1.5", "--deadzone", "0.05", "--saturation-x", "0.95", "--text")
Assert-True ($scSet.ExitCode -eq 0) ("binds tuning set for Star Citizen failed: " + $scSet.Output)
$scPreview = Run-Engine @("--profile", $scProfile, "binds", "tuning", "preview", "SC Pitch", "--game", "StarCitizen", "--points", "5", "--text")
Assert-Match $scPreview.Output "Model: starcitizen, from game:StarCitizen" "Star Citizen should use its own axis model."
Assert-Match $scPreview.Output "\+0\.500 -> -0\.353553" "starcitizen preview: dead zone 0.05 / saturation 0.95 -> 0.5, exponent 1.5 -> 0.3536, inverted."
Assert-Match $scPreview.Output "\+1\.000 -> -1\.000000" "starcitizen preview: full deflection gives full output."
$scCurve = Run-Engine @("--profile", $scProfile, "binds", "tuning", "set", "SC Yaw", "--scope", "game:StarCitizen", "--inverted", "false", "--deadzone", "0", "--curvature", "0,0.01,0.04,0.09,0.16,0.25,0.36,0.49,0.64,0.81,1", "--text")
Assert-True ($scCurve.ExitCode -eq 0) "binds tuning set with a Star Citizen curve failed."
$scRollExp = Run-Engine @("--profile", $scProfile, "binds", "tuning", "set", "SC Roll", "--scope", "game:StarCitizen", "--saturation-x", "1", "--curvature", "0", "--param", "exponent=2.2", "--text")
Assert-True ($scRollExp.ExitCode -eq 0) "binds tuning set with a Star Citizen exponent failed."
# Two relations on one option group (v_pitch_mouse shares flight_move_pitch) and one physical axis (v_view_pitch on y).
$scAddB = Run-Engine @("--profile", $scProfile, "relations", "add", "SC Pitch B", "--axis", "--node", "StarCitizen:spaceship_movement|v_pitch_mouse", "--device", $scStick, "--input", "JOY_RX", "--text")
Assert-True ($scAddB.ExitCode -eq 0) "relations add for a second pitch relation failed."
$scSetB = Run-Engine @("--profile", $scProfile, "binds", "tuning", "set", "SC Pitch B", "--scope", "game:StarCitizen", "--inverted", "false", "--text")
Assert-True ($scSetB.ExitCode -eq 0) "binds tuning set for the second pitch relation failed."
$scAddView = Run-Engine @("--profile", $scProfile, "relations", "add", "SC View Pitch", "--axis", "--node", "StarCitizen:spaceship_view|v_view_pitch", "--device", $scStick, "--input", "JOY_Y", "--text")
Assert-True ($scAddView.ExitCode -eq 0) "relations add for view pitch failed."
$scSetView = Run-Engine @("--profile", $scProfile, "binds", "tuning", "set", "SC View Pitch", "--scope", "game:StarCitizen", "--deadzone", "0.2", "--param", "exponent=1", "--text")
Assert-True ($scSetView.ExitCode -eq 0) "binds tuning set for view pitch failed."

$scExport = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-True ($scExport.ExitCode -eq 0) ("starcitizen export with axis settings failed: " + $scExport.Output)
$scText = [System.IO.File]::ReadAllText($scImport.File)
$scJs1 = [regex]::Match($scText, '(?s)<options type="joystick" instance="1"[^>]*>.*?</options>').Value
$scJs2 = [regex]::Match($scText, '(?s)<options type="joystick" instance="2"[^>]*>.*?</options>').Value
$scDev = [regex]::Match($scText, '(?s)<deviceoptions name="Test Stick[^>]*>.*?</deviceoptions>').Value
Assert-Match $scJs1 '<flight_move_pitch invert="1" exponent="1\.5"/>' ("export should write the pitch group of js1: " + $scJs1)
Assert-Match $scJs1 '(?s)<flight_move_yaw invert="0">\r\n    <nonlinearity_curve>\r\n     <point in="0\.1" out="0\.01"/>.*<point in="0\.9" out="0\.81"/>\r\n    </nonlinearity_curve>\r\n   </flight_move_yaw>' ("export should replace yaw's exponent by the curve (exclusive) and keep invert explicit: " + $scJs1)
Assert-Match $scJs1 '<flight_move_roll exponent="2\.2"/>' ("export should replace roll's curve by the exponent: " + $scJs1)
Assert-Match $scJs1 '<flight_view_pitch exponent="1"/>' ("view pitch inherits exponent 2.5 from flight_view; a linear tuning must be written: " + $scJs1)
Assert-Match $scJs1 '<flight_move_strafe_vertical invert="1"/>' "export must keep option groups JoyPro does not write."
Assert-Match $scJs2 '<flight_move_pitch invert="1"/>' "export must keep the option groups of other devices."
Assert-Match $scDev '<option input="x" deadzone="0"/>' ("export should set yaw's dead zone to 0 (the attribute exists): " + $scDev)
Assert-Match $scDev '<option input="rotz" saturation="1"/>' ("export should set roll's saturation to 1: " + $scDev)
Assert-Match $scDev '<option input="y" deadzone="0\.05" saturation="0\.95"/>' ("export should add pitch's dead zone and saturation for axis y: " + $scDev)
Assert-Match $scExport.Output "share option group flight_move_pitch on js1 with different settings; 'SC Pitch' wins" "two relations on one option group should warn; the first by name wins."
Assert-Match $scExport.Output "use axis y of js1 with a different dead zone/saturation; 'SC Pitch' wins" "two relations on one physical axis should warn; the first by name wins."
Assert-Match $scExport.Output "(?m)^Option Groups Written:\s+4\s*$" "export should count the option groups it wrote."
Assert-True (Test-Path ($scImport.File + ".joypro.json")) "export should record the axis settings it wrote."
$scAgainText = $scText
$scAgain = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-True ([System.IO.File]::ReadAllText($scImport.File) -eq $scAgainText) "a repeated export with axis settings must not change the file."
$scReimportProfile = Join-Path $tempDir "sc_axis_reimport.pr0file"
$scReimport = Run-Engine @("--profile", $scReimportProfile, "starcitizen", "import", $scImport.Root, "--text")
Assert-True ($scReimport.ExitCode -eq 0) "re-import of the exported axis settings failed."
$scReShow = Run-Engine @("--profile", $scReimportProfile, "binds", "tuning", "show", "SC Pitch", "--game", "StarCitizen", "--text")
Assert-Match $scReShow.Output "game:StarCitizen: inv=1;sl=0;dz=0\.05;sx=0\.95;sy=1;cv=0;n\.exponent=1\.5" "re-import should give the exported pitch settings back."

# ---- Merge keeps a value changed in the game; merge-overwrite sets the profile's again --------------------------------
[System.IO.File]::WriteAllText($scImport.File, $scText.Replace('<flight_move_pitch invert="1" exponent="1.5"/>', '<flight_move_pitch invert="1" exponent="1.8"/>'), $scUtf8)
$scMerge = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--mode", "merge", "--prefer-local-order", "false", "--text")
Assert-True ($scMerge.ExitCode -eq 0) "starcitizen merge export failed."
Assert-Match $scMerge.Output "flight_move_pitch on js1 has settings made in the game; merge keeps them" "merge should report the kept in-game option group."
Assert-Match ([System.IO.File]::ReadAllText($scImport.File)) 'exponent="1\.8"' "merge must keep an option group changed in the game."
$scOverwrite = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-Match ([System.IO.File]::ReadAllText($scImport.File)) '<flight_move_pitch invert="1" exponent="1\.5"/>' "merge-overwrite should set the profile's option group again."

# ---- A slot that changes device: the option groups move with the device -----------------------------------------------
$scMoveText = @'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard  {6F1D2B61-D5A0-11CF-BFC7-444553540000}"/>
  <options type="joystick" instance="1" Product="Other Stick {22222222-2222-3333-4444-555555555555}">
   <flight_move_roll invert="1"/>
  </options>
  <options type="joystick" instance="2" Product="Test Stick {11111111-1111-1111-1111-111111111111}">
   <flight_move_yaw invert="1"/>
  </options>
  <modifiers />
 </ActionProfiles>
</ActionMaps>
'@.Replace("`r`n", "`n").Replace("`n", "`r`n")
$scMove = ScNew-Profile "move" $scMoveText
$scMoveProfile = Join-Path $tempDir "sc_axis_move.pr0file"
$scMoveAdd = Run-Engine @("--profile", $scMoveProfile, "relations", "add", "SC Move Fire", "--button", "--node", "StarCitizen:spaceship_movement|v_fire", "--device", $scStick, "--input", "JOY_BTN1", "--text")
Assert-True ($scMoveAdd.ExitCode -eq 0) "relations add for the slot move check failed."
$scMoveExport = Run-Engine @("--profile", $scMoveProfile, "starcitizen", "export", $scMove.Root, "--prefer-local-order", "false", "--text")
Assert-True ($scMoveExport.ExitCode -eq 0) ("starcitizen export with a slot change failed: " + $scMoveExport.Output)
$scMoved = [System.IO.File]::ReadAllText($scMove.File)
$scMovedJs1 = [regex]::Match($scMoved, '(?s)<options type="joystick" instance="1"[^>]*>.*?</options>').Value
Assert-Match $scMovedJs1 'Product="Test Stick' "the export should put Test Stick on js1."
Assert-Match $scMovedJs1 '<flight_move_yaw invert="1"/>' ("Test Stick's option groups must move with it to js1: " + $scMovedJs1)
Assert-NotMatch $scMovedJs1 'flight_move_roll' "Other Stick's option groups must not stay on Test Stick's slot."
Assert-Match $scMoveExport.Output "the axis settings of 'Other Stick" "dropping the option groups of a device without a slot should warn."

# ---- Game-wide settings: attributes.xml in place -----------------------------------------------------------------------
$scAttrFile = Join-Path $scImport.Dir "attributes.xml"
$scAttrBefore = [System.IO.File]::ReadAllText($scAttrFile)
$scFfb = Run-Engine @("--profile", $scProfile, "ffb", "set", "--device", "*", "--game", "StarCitizen", "--param", "Vibration=false", "--param", "PilotEspStrength=0.8", "--param", "FlightRelativeThrottleTrapTime=0.5", "--text")
Assert-True ($scFfb.ExitCode -eq 0) ("ffb set for Star Citizen game settings failed: " + $scFfb.Output)
$scFfbBad = Run-Engine @("--profile", $scProfile, "ffb", "set", "--device", "Test Stick", "--game", "StarCitizen", "--param", "Vibration=1", "--text")
Assert-True ($scFfbBad.ExitCode -ne 0) "Star Citizen game settings are game-wide (device *)."
$scAttrExport = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-True ($scAttrExport.ExitCode -eq 0) "starcitizen export with game settings failed."
Assert-Match $scAttrExport.Output "(?m)^Files Written:\s+3\s*$" "starcitizen export should count attributes.xml."
$scNl = if ($scAttrBefore.Contains("`r`n")) { "`r`n" } else { "`n" }
$scAttrExpected = $scAttrBefore.Replace('<Attr name="FlightCoreDisabledSensitivityRotation" value="2"/>' + $scNl, '<Attr name="FlightCoreDisabledSensitivityRotation" value="2"/>' + $scNl + ' <Attr name="FlightRelativeThrottleTrapTime" value="0.5"/>' + $scNl).Replace('<Attr name="PilotEspStrength" value="1"/>', '<Attr name="PilotEspStrength" value="0.8"/>').Replace('<Attr name="TurretsEspStrength" value="1"/>' + $scNl, '<Attr name="TurretsEspStrength" value="1"/>' + $scNl + ' <Attr name="Vibration" value="0"/>' + $scNl)
Assert-True ([System.IO.File]::ReadAllText($scAttrFile) -eq $scAttrExpected) ("attributes.xml should get the values in place, new names at their sorted place: " + [System.IO.File]::ReadAllText($scAttrFile))
Assert-True (Test-Path ($scAttrFile + ".jpbak")) "attributes.xml should be backed up before the first write."
$scAttrAgain = Run-Engine @("--profile", $scProfile, "starcitizen", "export", $scImport.Root, "--prefer-local-order", "false", "--text")
Assert-Match $scAttrAgain.Output "(?m)^Files Written:\s+2\s*$" "unchanged game settings must not rewrite attributes.xml."

# ---- Clean switches off the game's default joystick binds on js1 (defaultJoystick in the catalog) -------------------
# defaultProfile.xml gives v_pitch "y", v_yaw "x" and v_fire "button1" on the joystick; they act on js1 until the action
# has a js rebind. Clean writes an empty rebind "js1_ " for the defaults JoyPro does not bind itself; merge-overwrite
# leaves them; an import ignores the empty rebinds. Only a connected device's defaults are switched off: Test Stick is
# faked as connected here.
Copy-Item -Path (Join-Path $scRefreshDb "AllBinds.xml") -Destination $starCitizenDbCatalogPath -Force
$scPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\roundtrip\starcitizen-devices.tsv"
try {
    $scDefProfile = Join-Path $tempDir "sc_defaults.pr0file"
    $scDefAdd = Run-Engine @("--profile", $scDefProfile, "relations", "add", "SC Def Pitch", "--axis", "--node", "StarCitizen:spaceship_movement|v_pitch", "--device", $scStick, "--input", "JOY_Y", "--text")
    Assert-True ($scDefAdd.ExitCode -eq 0) "relations add for the default-bind check failed."
    $scDefClean = ScNew-Profile "defaults_clean" $scFixtureText
    $scDefCleanExport = Run-Engine @("--profile", $scDefProfile, "starcitizen", "export", $scDefClean.Root, "--mode", "clean", "--prefer-local-order", "false", "--text")
    Assert-True ($scDefCleanExport.ExitCode -eq 0) ("starcitizen clean export for the default-bind check failed: " + $scDefCleanExport.Output)
    $scDefCleanText = [System.IO.File]::ReadAllText($scDefClean.File)
    if ($scDefCleanText -match '<options type="joystick" instance="1"[^>]*Product="Test Stick') {
        Assert-Match $scDefCleanExport.Output "(?m)^Default Joystick Binds Cleared:\s+2\s*$" "clean should switch off the two defaults JoyPro does not bind (v_yaw, v_fire)."
        Assert-Match $scDefCleanText '(?s)<action name="v_yaw">\s*<rebind input="js1_ "\s*/>' "clean should unbind v_yaw's default joystick axis on js1."
        Assert-Match $scDefCleanText '(?s)<action name="v_fire">\s*<rebind input="js1_ "\s*/>' "clean should unbind v_fire's default joystick button on js1."
        Assert-Match $scDefCleanText '(?s)<action name="v_pitch">.*?<rebind input="js1_y"' "clean keeps JoyPro's own bind of v_pitch."
        $scDefImport = Run-Engine @("--profile", (Join-Path $tempDir "sc_defaults_import.pr0file"), "starcitizen", "import", $scDefClean.Root, "--text")
        Assert-True ($scDefImport.ExitCode -eq 0) "starcitizen import of a file with cleared binds failed."
        Assert-NotMatch $scDefImport.Output "Skipping Star Citizen rebind 'js1_ '" "import must not warn about cleared binds."
    }
    else {
        Write-Host "SKIP: Star Citizen default-bind clean check (Test Stick did not get js1 on this machine)."
    }
    $scDefMerge = ScNew-Profile "defaults_merge" $scFixtureText
    $scDefMergeExport = Run-Engine @("--profile", $scDefProfile, "starcitizen", "export", $scDefMerge.Root, "--mode", "merge-overwrite", "--prefer-local-order", "false", "--text")
    Assert-True ($scDefMergeExport.ExitCode -eq 0) "starcitizen merge-overwrite export for the default-bind check failed."
    Assert-NotMatch ([System.IO.File]::ReadAllText($scDefMerge.File)) 'input="js1_ "' "merge-overwrite must leave the game's default joystick binds."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $scPreviousFakeDevices
    Copy-Item -Path (Join-Path $scFixtures "catalog.xml") -Destination $starCitizenDbCatalogPath -Force
}
