# MSFS 2020 / 2024 axis settings (docs/games/msfs.md, engine\AxisModels\Msfs.cs): the device's Sensitivity page per physical axis,
# the per-action override (flag 4096 + <Axis/>), flag 1024 (no sensitivity curve) and invert. Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Fixtures: anonymised copies of real MSFS 2020
# profiles (VPC MT-50 throttle with reactivity 2000 on rX, an early version-1238 WarBRD file) and a 2024 export with
# per-action overrides as MSFS's own default profiles have them.

$mcDir = Join-Path $tempDir "msfs-curves"
$mcRemote = Join-Path $mcDir "remote"
$mc2024 = Join-Path $mcDir "override2024.xml"
$mcProfile = Join-Path $mcDir "curves.pr0file"
$mc2024Profile = Join-Path $mcDir "override.pr0file"
New-Item -Path $mcRemote -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\msfs\curves\*") -Destination $mcRemote -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\msfs\override2024.xml") -Destination $mc2024 -Force
$mcThrottleFile = Join-Path $mcRemote "inputprofile_0000000011"
$mcStickFile = Join-Path $mcRemote "inputprofile_0000000012"
$mcThrottleText = [System.IO.File]::ReadAllText($mcThrottleFile)
$mcStickText = [System.IO.File]::ReadAllText($mcStickFile)
$mc2024Text = [System.IO.File]::ReadAllText($mc2024)
$mcThrottle = "THROTTLE AXIS SET EX1"
$mcElevator = "AXIS ELEVATOR SET"
$mcElevator2024 = "AXIS ELEVATOR SET"
$mcAilerons = "AXIS AILERONS SET"
$mcRudder = "AXIS RUDDER SET"
$mcWarBrd = "RIGHT VPC Stick WarBRD {CCCCCCCC-0000-1111-2222-333333333333}"
$mcPreviousDataDir = $env:JOYPRO_DATA_DIR
# Stand-in user folders without profiles: an import or scan of a package path also reads the user's own MSFS profiles,
# which must not be the real ones here (smoke\msfsuserprofiles.ps1 tests those).
$mcPreviousMsfsHome = $env:JOYPRO_TEST_MSFS_HOME
$env:JOYPRO_TEST_MSFS_HOME = Join-Path $mcDir "no-user-profiles"
$env:JOYPRO_DATA_DIR = Join-Path $mcDir "data"
try {
    # Import: the Sensitivity page of the bound axis becomes the relation's MSFS tuning (only values that differ from
    # the defaults), invert from flag 128; the round trip leaves both files byte-identical.
    $mcImport = Run-Engine @("--profile", $mcProfile, "msfs2020", "import", $mcRemote, "--include-disconnected", "--text")
    Assert-True ($mcImport.ExitCode -eq 0) ("msfs curves import failed: " + $mcImport.Output)
    $mcShow = Run-Engine @("--profile", $mcProfile, "binds", "tuning", "show", $mcThrottle, "--game", "MSFS2020", "--text")
    Assert-Match $mcShow.Output "MSFS2020 uses \(msfs, from game:MSFS2020\): inv=1;.*n\.reactivity=2000" ("msfs import should read the device axis reactivity (AxisResponseRate) and invert: " + $mcShow.Output)
    $mcRoundTrip = Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--text")
    Assert-True ($mcRoundTrip.ExitCode -eq 0) "msfs curves export after import failed."
    Assert-True ([System.IO.File]::ReadAllText($mcThrottleFile) -eq $mcThrottleText) "msfs import/export must leave the throttle profile (reactivity 2000) byte-identical."
    Assert-True ([System.IO.File]::ReadAllText($mcStickFile) -eq $mcStickText) "msfs import/export must leave the version-1238 profile byte-identical."

    # Sensitivity page values in file units; the preview follows the curve measured in MSFS 2020 (mirrored halves:
    # Sensitivity + acts on the negative stick half; v(1+s)/(1-s+2sv) after the dead zone).
    Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", $mcThrottle, "--scope", "game:MSFS2020", "--param", "axisDeadZone=10", "--param", "sensitivityPlus=-30", "--text") | Out-Null
    $mcPreview = Run-Engine @("--profile", $mcProfile, "binds", "tuning", "preview", $mcThrottle, "--game", "MSFS2020", "--points", "5", "--text")
    Assert-Match $mcPreview.Output "Model: msfs" "msfs should preview with its own model."
    Assert-Match $mcPreview.Output "\+0\.500 -> -0\.444444" ("msfs preview: positive stick half (sensitivity - 0) only has the dead zone, inverted: " + $mcPreview.Output)
    Assert-Match $mcPreview.Output "-0\.500 -> \+0\.301075" ("msfs preview: negative stick half with sensitivity + -30 and dead zone 10, inverted: " + $mcPreview.Output)
    Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", $mcElevator, "--scope", "game:MSFS2020", "--param", "axisDeadZone=5", "--text") | Out-Null
    $mcAddRudder = Run-Engine @("--profile", $mcProfile, "relations", "add", "MSFS Rudder", "--axis", "--node", "MSFS2020:KEY_AXIS_RUDDER_SET", "--device", $mcWarBrd, "--input", "JOY_RZ", "--text")
    Assert-True ($mcAddRudder.ExitCode -eq 0) "relations add for the MSFS rudder failed."
    Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", "MSFS Rudder", "--scope", "game:MSFS2020", "--param", "axisDeadZone=3", "--param", "neutral=-4", "--text") | Out-Null
    $mcExport = Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--text")
    Assert-True ($mcExport.ExitCode -eq 0) ("msfs export of axis settings failed: " + $mcExport.Output)
    Assert-Match $mcExport.Output "also applies to KEY_COCKPIT_CAMERA_ZOOM_AXIS" "msfs export should say when a changed Sensitivity page also applies to another action on that axis (the camera zoom)."
    $mcThrottleAfter = [System.IO.File]::ReadAllText($mcThrottleFile)
    Assert-Match $mcThrottleAfter '<Axis AxisName="rX" AxisSensitivy="-30" AxisSensitivyMinus="0" AxisNeutral="0" AxisDeadZone="10" AxisOutDeadZone="0" AxisResponseRate="2000"/>' "msfs export should write the rX Sensitivity page in place (reactivity kept)."
    $mcBefore = $mcThrottleText -split "`n"
    $mcAfterLines = $mcThrottleAfter -split "`n"
    Assert-True ($mcBefore.Count -eq $mcAfterLines.Count) "msfs export must not add or remove lines of the throttle profile."
    $mcDiffs = @(0..($mcBefore.Count - 1) | Where-Object { $mcBefore[$_] -ne $mcAfterLines[$_] })
    Assert-True ($mcDiffs.Count -eq 1) ("msfs export should only change the rX line, changed: " + $mcDiffs.Count)
    $mcStickAfter = [System.IO.File]::ReadAllText($mcStickFile)
    Assert-Match $mcStickAfter "\t\t<Axis AxisName=`"Y`" AxisSensitivy=`"0`" AxisDeadZone=`"5`"/>\n\t\t<Axis AxisName=`"rZ`" AxisSensitivy=`"0`" AxisNeutral=`"-4`" AxisDeadZone=`"3`"/>\n\t\t<Axis AxisName=`"SliderX`"" ("msfs export should edit a version-1238 line with its own attributes and add a missing axis in the sim's order: " + $mcStickAfter)

    # One Sensitivity page per device axis: a second relation on the same axis with other values is reported.
    Run-Engine @("--profile", $mcProfile, "relations", "add", "MSFS Rudder 2", "--axis", "--node", "MSFS2020:KEY_AXIS_LEFT_BRAKE_SET", "--device", $mcWarBrd, "--input", "JOY_RZ", "--text") | Out-Null
    $mcConflictSet = Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", "MSFS Rudder 2", "--scope", "game:MSFS2020", "--param", "axisDeadZone=7", "--text")
    Assert-Match $mcConflictSet.Output "Warning: Microsoft Flight Simulator 2020 keeps .*axisDeadZone.* once per device axis; relation 'MSFS Rudder'" ("binds tuning set should warn about another relation on the same MSFS device axis: " + $mcConflictSet.Output)
    $mcConflictExport = Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--text")
    Assert-Match $mcConflictExport.Output "MSFS has one Sensitivity page per device axis, but the relations on it differ" "msfs export should warn about relations with different values on one device axis."
    Assert-Match ([System.IO.File]::ReadAllText($mcStickFile)) 'AxisName="rZ" AxisSensitivy="0" AxisNeutral="-4" AxisDeadZone="3"' "msfs export should write the first relation's values on a shared device axis."
    Run-Engine @("--profile", $mcProfile, "relations", "mass", "delete", "--relation", "MSFS Rudder 2", "--text") | Out-Null

    # An override in an MSFS 2020 profile goes to the device page (documented for 2024 only).
    Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", $mcThrottle, "--scope", "game:MSFS2020", "--param", "axisOverride=1", "--text") | Out-Null
    $mc2020Override = Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--text")
    Assert-Match $mc2020Override.Output "axis override\) are an MSFS 2024 feature" "msfs export should not write a per-action override into an MSFS 2020 profile."
    Assert-Match ([System.IO.File]::ReadAllText($mcThrottleFile)) 'ActionName="KEY_THROTTLE_AXIS_SET_EX1" Flag="132">' "msfs 2020 throttle must keep flag 132 without the override bit."
    Run-Engine @("--profile", $mcProfile, "binds", "tuning", "set", $mcThrottle, "--scope", "game:MSFS2020", "--param", "axisOverride=0", "--text") | Out-Null

    # Merge keeps a Sensitivity page changed in the game.
    [System.IO.File]::WriteAllText($mcThrottleFile, ([System.IO.File]::ReadAllText($mcThrottleFile)).Replace('AxisSensitivy="-30" AxisSensitivyMinus="0" AxisNeutral="0" AxisDeadZone="10"', 'AxisSensitivy="-30" AxisSensitivyMinus="0" AxisNeutral="0" AxisDeadZone="20"'), (New-Object System.Text.UTF8Encoding($false)))
    $mcMerge = Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--mode", "merge", "--text")
    Assert-Match $mcMerge.Output "axis rX has Sensitivity settings made in the game; merge keeps them" ("msfs merge should keep a Sensitivity page changed in the game: " + $mcMerge.Output)
    Assert-Match ([System.IO.File]::ReadAllText($mcThrottleFile)) 'AxisName="rX" AxisSensitivy="-30" AxisSensitivyMinus="0" AxisNeutral="0" AxisDeadZone="20"' "msfs merge must not overwrite the game's dead zone."
    Run-Engine @("--profile", $mcProfile, "msfs2020", "export", $mcRemote, "--text") | Out-Null
    Assert-Match ([System.IO.File]::ReadAllText($mcThrottleFile)) 'AxisName="rX" AxisSensitivy="-30" AxisSensitivyMinus="0" AxisNeutral="0" AxisDeadZone="10"' "msfs merge-overwrite should write JoyPro's dead zone again."

    # MSFS 2024: per-action override (flag 4096 + <Axis/> before <Primary>) and flag 1024 are read and written.
    $mc2024Import = Run-Engine @("--profile", $mc2024Profile, "msfs2024", "import", $mc2024, "--include-disconnected", "--text")
    Assert-True ($mc2024Import.ExitCode -eq 0) "msfs 2024 override import failed."
    $mcAileronShow = Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "show", $mcAilerons, "--game", "MSFS2024", "--text")
    Assert-Match $mcAileronShow.Output "from game:MSFS2024\): .*n\.axisDeadZone=12;n\.axisOverride=1;n\.sensitivityMinus=-25;n\.sensitivityPlus=-25" ("msfs import should read the per-action override: " + $mcAileronShow.Output)
    $mcElevatorShow = Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "show", $mcElevator2024, "--game", "MSFS2024", "--text")
    Assert-Match $mcElevatorShow.Output "from game:MSFS2024\): .*n\.axisDeadZone=2;n\.noSensitivityCurve=1;n\.sensitivityMinus=-19;n\.sensitivityPlus=-19" ("msfs import should read flag 1024 and the device page: " + $mcElevatorShow.Output)
    Run-Engine @("--profile", $mc2024Profile, "msfs2024", "export", $mc2024, "--text") | Out-Null
    Assert-True ([System.IO.File]::ReadAllText($mc2024) -eq $mc2024Text) "msfs 2024 import/export with overrides must leave the export byte-identical."

    Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "set", $mcAilerons, "--scope", "game:MSFS2024", "--param", "sensitivityPlus=-40", "--text") | Out-Null
    Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "set", $mcRudder, "--scope", "game:MSFS2024", "--param", "axisOverride=0", "--text") | Out-Null
    Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "set", $mcElevator2024, "--scope", "game:MSFS2024", "--inverted", "true", "--text") | Out-Null
    $mcElevatorPreview = Run-Engine @("--profile", $mc2024Profile, "binds", "tuning", "preview", $mcElevator2024, "--game", "MSFS2024", "--points", "5", "--text")
    Assert-Match $mcElevatorPreview.Output "\+0\.500 -> -0\.500000" ("msfs preview with flag 1024 should be the raw (inverted) axis: " + $mcElevatorPreview.Output)
    $mc2024Export = Run-Engine @("--profile", $mc2024Profile, "msfs2024", "export", $mc2024, "--text")
    Assert-True ($mc2024Export.ExitCode -eq 0) ("msfs 2024 override export failed: " + $mc2024Export.Output)
    $mc2024After = [System.IO.File]::ReadAllText($mc2024)
    Assert-Match $mc2024After '(?s)ActionName="KEY_AXIS_AILERONS_SET" ValueEvent="0.000000" Delay="2.000000" Flag="4100">\n\t\t\t<Axis AxisSensitivy="-40" AxisSensitivyMinus="-25" AxisNeutral="0" AxisDeadZone="12" AxisOutDeadZone="0" AxisResponseRate="-1"/>\n\t\t\t<Primary>' "msfs 2024 export should edit the per-action override in place."
    Assert-Match $mc2024After 'ActionName="KEY_AXIS_RUDDER_SET" ValueEvent="0.000000" Delay="0.000000" Flag="4">\n\t\t\t<Primary>' "msfs 2024 export should drop flag 4096 and the per-action <Axis/> when the override is switched off."
    Assert-Match $mc2024After '<Axis AxisName="rZ" AxisSensitivy="-50" AxisSensitivyMinus="-50" AxisNeutral="0" AxisDeadZone="25" AxisOutDeadZone="0" AxisResponseRate="-1"/>' "msfs 2024 export should move the rudder's values to the device page."
    Assert-Match $mc2024After 'ActionName="KEY_AXIS_ELEVATOR_SET" ValueEvent="0.000000" Delay="2.000000" Flag="1156">' "msfs export should keep flag 1024 when it sets invert (1028 -> 1156)."
    # Backups and replaces do not depend on File.Copy/Replace/Move (2.1.0: "The specified file could not be encrypted."
    # when a preset carried the EFS mark): with those failing like that (SafeFileIo test fault), the export still works.
    $env:JOYPRO_TEST_FAULT_ENCRYPTION = "copy,replace,move"
    try {
        $mcEfs = Run-Engine @("--profile", $mc2024Profile, "msfs2024", "export", $mc2024, "--text")
    } finally {
        Remove-Item Env:JOYPRO_TEST_FAULT_ENCRYPTION -ErrorAction SilentlyContinue
    }
    Assert-True ($mcEfs.ExitCode -eq 0) ("msfs 2024 export with File.Copy failing (EFS) should still work: " + $mcEfs.Output)
    Assert-NotMatch $mcEfs.Output "could not be encrypted" "msfs 2024 export must not fail or warn on an EFS copy fault."

    # Releasing an axis with an override leaves no stale <Axis/> behind.
    Run-Engine @("--profile", $mc2024Profile, "relations", "mass", "delete", "--relation", $mcAilerons, "--text") | Out-Null
    Run-Engine @("--profile", $mc2024Profile, "msfs2024", "export", $mc2024, "--text") | Out-Null
    $mc2024Released = [System.IO.File]::ReadAllText($mc2024)
    Assert-Match $mc2024Released 'ActionName="KEY_AXIS_AILERONS_SET" ValueEvent="0.000000" Delay="2.000000" Flag="2"/>' "msfs 2024 export should turn a released override axis into an empty element."
    Assert-NotMatch $mc2024Released 'AxisSensitivy="-40"' "msfs 2024 export must not leave the released action's <Axis/> behind."
    Assert-True (@(Get-ChildItem -Path $mcRemote -Filter "*.jpbak").Count -eq 0) "msfs backups must not go into the Steam folder."

    # MSFS 2024 Community package: an export to a Community folder creates joypro-input-profiles with one preset per
    # device (matched by the USB product ID), only JoyPro's binds, the device axis page, layout.json and manifest.json;
    # an import reads it back.
    $mpPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $mpDir = Join-Path $mcDir "package"
    $mpCommunity = Join-Path $mpDir "Community"
    New-Item -Path $mpCommunity -ItemType Directory -Force | Out-Null
    $mpDevice = "vJoy Device {5A5A0001-B53F-11f1-8002-444553540000}"
    Set-Content -Path (Join-Path $mpDir "devices.tsv") -Value ($mpDevice + "`t1234`tBEAD`tvJoy - Virtual Joystick") -Encoding UTF8
    $env:JOYPRO_FAKE_DEVICES = Join-Path $mpDir "devices.tsv"
    try {
        $mpProfile = Join-Path $mpDir "package.pr0file"
        Run-Engine @("--profile", $mpProfile, "relations", "add", "Package Ailerons", "--axis", "--node", "MSFS2024:KEY_AXIS_AILERONS_SET", "--device", $mpDevice, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $mpProfile, "relations", "add", "Package Flaps", "--button", "--node", "MSFS2024:KEY_FLAPS_DECR", "--device", $mpDevice, "--input", "JOY_BTN7", "--text") | Out-Null
        Run-Engine @("--profile", $mpProfile, "binds", "tuning", "set", "Package Ailerons", "--scope", "game:MSFS2024", "--param", "axisDeadZone=15", "--text") | Out-Null
        $mpExport = Run-Engine @("--profile", $mpProfile, "msfs", "export", $mpCommunity, "--text")
        Assert-True ($mpExport.ExitCode -eq 0) ("msfs 2024 package export failed: " + $mpExport.Output)
        $mpPackage = Join-Path $mpCommunity "joypro-input-profiles"
        $mpPresets = @(Get-ChildItem -Path (Join-Path $mpPackage "InputProfiles\Categories\Airplane") -Filter "*.xml")
        Assert-True ($mpPresets.Count -eq 1) "msfs 2024 package export should create one airplane preset for the device."
        $mpText = [System.IO.File]::ReadAllText($mpPresets[0].FullName)
        Assert-Match $mpText '<DefaulftInput Primary="1" PlatformAvailability="PC">' "the preset needs the package root element."
        Assert-Match $mpText 'ProductID="BEAD" CompositeID="0" HWVer="1.0.0.0"' "the preset must carry the device's USB product ID (the sim matches presets by it)."
        Assert-Match $mpText '(?s)<Context ContextName="SURFACES">\s*<Action ActionName="KEY_AXIS_AILERONS_SET" Flag="4100" ValueEvent="0">\s*<Axis [^>]*AxisDeadZone="15"[^>]*/>\s*<Primary>\s*<KEY Information="[^"]*">1042</KEY>' "the preset should bind the ailerons to vJoy Y in the SURFACES context, with the axis settings as a per-action override (flag 4096; MSFS 2024 ignores a preset's <Axes>)."
        Assert-Match $mpText '(?s)<Context ContextName="AIRCRAFT">\s*<Action ActionName="KEY_FLAPS_DECR" Flag="2" ValueEvent="0">\s*<Primary>\s*<KEY Information="[^"]*">6</KEY>' "the preset should bind flaps decrease to button 7 (code 6)."
        $mpLayout = [System.IO.File]::ReadAllText((Join-Path $mpPackage "layout.json"))
        Assert-Match $mpLayout ('"path": "inputprofiles/categories/airplane/' + [regex]::Escape($mpPresets[0].Name.ToLowerInvariant()) + '"') "layout.json must list the preset."
        Assert-Match ([System.IO.File]::ReadAllText((Join-Path $mpPackage "manifest.json"))) '"package_order_hint": "INPUT"' "manifest.json should mark an input package."
        $mpImport = Run-Engine @("--profile", (Join-Path $mpDir "import.pr0file"), "msfs2024", "scan", $mpPackage, "--include-disconnected", "--text")
        Assert-True ($mpImport.ExitCode -eq 0) ("msfs scan of the package failed: " + $mpImport.Output)
        Assert-Match $mpImport.Output "KEY_FLAPS_DECR" "msfs scan should read the package presets."
        $mpAgain = Run-Engine @("--profile", $mpProfile, "msfs", "export", $mpCommunity, "--text")
        Assert-True ($mpAgain.ExitCode -eq 0) "msfs 2024 package export (second run) failed."
        Assert-True (@(Get-ChildItem -Path (Join-Path $mpPackage "InputProfiles\Categories\Airplane") -Filter "*.xml").Count -eq 1) "a second export must reuse the device's preset."
        Assert-True ([System.IO.File]::ReadAllText($mpPresets[0].FullName) -eq $mpText) "a second export with the same profile must not change the preset."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $mpPreviousFakeDevices
    }
}
finally {
    $env:JOYPRO_DATA_DIR = $mcPreviousDataDir
    $env:JOYPRO_TEST_MSFS_HOME = $mcPreviousMsfsHome
}

# Catalog refresh from the sim's own files (no --path): Asobo's presets in <install>\Packages\fs-input-profiles and
# the menu names of fs-base\en-US.locPak. New actions and known ones still titled by their raw event name get the menu
# name, readable ("Toggle Smoke Refresh"), else a readable title from the event name; other known titles stay.
$mrDir = Join-Path $tempDir "msfs-refresh"
$mrGame = Join-Path $mrDir "MSFS2024"
$mrRoot = Join-Path $mrDir "joypro"
$mrPresets = Join-Path $mrGame "Packages\fs-input-profiles\InputProfiles\Categories\Airplane"
New-Item -Path $mrPresets, (Join-Path $mrGame "Packages\fs-base"), (Join-Path $mrRoot "DB\MSFS2024"), (Join-Path $mrRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\MSFS2024\*") -Destination (Join-Path $mrRoot "DB\MSFS2024") -Force
# A title someone gave an action (not its raw event name) must survive the refresh.
$mrList = Join-Path $mrRoot "DB\MSFS2024\actions.tsv"
[System.IO.File]::WriteAllText($mrList, ([regex]::Replace([System.IO.File]::ReadAllText($mrList), "(?m)^KEY_FLAPS_UP\t[^\t]*\t", "KEY_FLAPS_UP`tFlaps up (my name)`t")), (New-Object System.Text.UTF8Encoding $false))
[System.IO.File]::WriteAllText((Join-Path $mrPresets "Keyboard_2024_Planes.xml"), "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<DefaulftInput Primary=`"1`" PlatformAvailability=`"ALL`">`r`n  <Version Num=`"4670`" />`r`n  <Device DeviceName=`"Keyboard 2024 Planes`" GUID=`"884b96c3-56ef-11d1-bc8c-00a0c91405dd`" ProductID=`"884B`" CompositeID=`"0`" HWVer=`"1.0.0.0`">`r`n    <Context ContextName=`"AIRCRAFT`">`r`n      <Action ActionName=`"KEY_SMOKE_REFRESH_TOGGLE`" Flag=`"2`" ValueEvent=`"0`">`r`n        <Primary>`r`n          <KEY Information=`"J`">74</KEY>`r`n        </Primary>`r`n      </Action>`r`n      <Action ActionName=`"KEY_GEAR_TOGGLE`" Flag=`"2`" ValueEvent=`"0`">`r`n        <Primary>`r`n          <KEY Information=`"G`">71</KEY>`r`n        </Primary>`r`n      </Action>`r`n    </Context>`r`n    <Context ContextName=`"MENU`">`r`n      <Action ActionName=`"KEY_SMOKE_MENU_ONLY`" Flag=`"2`" ValueEvent=`"0`">`r`n        <Primary>`r`n          <KEY Information=`"M`">77</KEY>`r`n        </Primary>`r`n      </Action>`r`n    </Context>`r`n  </Device>`r`n</DefaulftInput>`r`n", (New-Object System.Text.UTF8Encoding $true))
[System.IO.File]::WriteAllText((Join-Path $mrGame "Packages\fs-base\en-US.locPak"), "{ `"LocalisationPackage`": { `"Language`": `"en-US`", `"Strings`": { `"INPUT.KEY_SMOKE_REFRESH_TOGGLE`": `"TOGGLE SMOKE REFRESH`", `"INPUT.KEY_SMOKE_REFRESH_TOGGLE_DESC`": `"Long text`", `"INPUT.KEY_GEAR_TOGGLE`": `"RENAMED GEAR`", `"INPUT.KEY_SMOKE_NAMED_ONLY`": `"SMOKE NAMED ONLY`", `"INPUT.KEY_HELI_SMOKE_BEEP`": `"SMOKE BEEP`", `"INPUT.KEY_MENU_SMOKE_OPEN`": `"OPEN SMOKE MENU`", `"INPUT.KEY_AXIS_CAMERA_SMOKE`": `"SMOKE CAMERA AXIS`" } } }", (New-Object System.Text.UTF8Encoding $false))
$mrPreviousData = $env:JOYPRO_DATA_DIR
$mrPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $mrDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $mrRoot
try {
    $mrSet = Run-Engine @("games", "install", "set", "msfs2024", $mrGame, "--text")
    Assert-True ($mrSet.ExitCode -eq 0) ("games install set msfs2024 failed: " + $mrSet.Output)
    Assert-Match ([System.IO.File]::ReadAllText($mrList)) "(?m)^KEY_GEAR_TOGGLE\tGEAR TOGGLE\t" "the shipped MSFS list should still title KEY_GEAR_TOGGLE by its raw event name (test premise)."
    Assert-Match ([System.IO.File]::ReadAllText($mrList)) "(?m)^KEY_FLAPS_UP\tFlaps up \(my name\)\t" "the test could not give KEY_FLAPS_UP its own title."
    $mr = Run-Engine @("msfs2024", "refresh-db", "--text")
    Assert-True ($mr.ExitCode -eq 0) ("msfs refresh-db from the game files failed: " + $mr.Output)
    Assert-Match $mr.Output "Game presets read: 1 of 1" ("msfs refresh-db should read the install's presets: " + $mr.Output)
    # Written into the game-data folder (<data>\GameData), never into the program folder ($mrRoot).
    $mrCatalog = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\MSFS2024\actions.tsv"))
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $mrRoot "DB\MSFS2024\actions.tsv")) -notmatch "KEY_SMOKE_REFRESH_TOGGLE") "msfs refresh-db must not write into the program folder."
    Assert-Match $mrCatalog "(?m)^KEY_SMOKE_REFRESH_TOGGLE\tToggle Smoke Refresh\tbutton\tAIRCRAFT" "a new flight action should come in with the sim's menu name, readable."
    Assert-NotMatch $mrCatalog "KEY_SMOKE_MENU_ONLY" "actions of non-flight contexts must not become catalog actions."
    # Flight events the locPak names but no preset binds come in by JoyPro's rules (name -> context); menus and cameras do not.
    Assert-Match $mrCatalog "(?m)^KEY_SMOKE_NAMED_ONLY\tSmoke Named Only\tbutton\tAIRCRAFT" "a flight event only the locPak names should come in (AIRCRAFT context)."
    Assert-Match $mrCatalog "(?m)^KEY_HELI_SMOKE_BEEP\tSmoke Beep\tbutton\tHELICOPTER" "a helicopter event only the locPak names should get the HELICOPTER context."
    Assert-NotMatch $mrCatalog "KEY_MENU_SMOKE_OPEN|KEY_AXIS_CAMERA_SMOKE" "menu and camera events of the locPak must not become catalog actions."
    # Raw event names become readable: the menu name where the locPak has one, else one from the event name.
    Assert-Match $mrCatalog "(?m)^KEY_GEAR_TOGGLE\tRenamed Gear\t" "a known action titled by its raw event name should get the sim's menu name."
    Assert-Match $mrCatalog "(?m)^KEY_AXIS_ELEVATOR_SET\tElevator Axis\taxis\t" "a raw event name the locPak does not list should get a readable title from the event name."
    Assert-Match $mrCatalog "(?m)^KEY_THROTTLE1_AXIS_SET_EX1\tThrottle 1 Axis\taxis\t" "the readable title should drop EX1 and split the engine number."
    Assert-Match $mrCatalog "(?m)^KEY_FLAPS_UP\tFlaps up \(my name\)\t" "a known title that is not the raw event name must stay (relation names come from it)."
    Assert-Match $mr.Output "Readable names \(new actions and raw event names\): \d+ from the game's menu, \d+ from the event name" ("refresh-db should report the readable names: " + $mr.Output)
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\MSFS2024\actions.tsv")) -notmatch "KEY_SMOKE_REFRESH_TOGGLE") "the shipped MSFS catalog must stay untouched."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $mrPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $mrPreviousDetection
}

# MSFS aircraft list: refresh-db finds the installed aircraft (synthetic install: a 2020 Official package with
# [FLTSIM.n] titles and a livery, Community2024 packages (packed one listed in layout.json, a 2024 modular one), a
# 2024 content list); relations can be limited to an aircraft; export writes those only for --aircraft.
# Temp JoyPro root (DB copy), so the shipped DB is never written.
$maRoot = Join-Path $tempDir "msfs-aircraft\root"
$maInstall = Join-Path $tempDir "msfs-aircraft\install"
$maRemote = Join-Path $tempDir "msfs-aircraft\remote"
$maProfile = Join-Path $tempDir "msfs-aircraft\aircraft.pr0file"
New-Item -Path (Join-Path $maRoot "DB\MSFS2024"), (Join-Path $maRoot "CleanProfile"), $maRemote, (Join-Path $maInstall "Input") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\MSFS2024\actions.tsv") -Destination (Join-Path $maRoot "DB\MSFS2024") -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\msfs\aircraft-install\*") -Destination $maInstall -Recurse -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\msfs\export2024.xml") -Destination (Join-Path $maInstall "Input\smoke_preset.xml") -Force
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\msfs\remote\*") -Destination $maRemote -Force
$maStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$maSting = Join-Path $maRemote "inputprofile_0000000001"
# The aircraft list is built into the game-data folder (<data>\GameData).
$maAircraftFile = Join-Path $tempDir "msfs-aircraft\data\GameData\DB\MSFS2024\aircraft.tsv"
$maPreviousData = $env:JOYPRO_DATA_DIR
$maPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "msfs-aircraft\data"
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $maRoot
try {
    $maSet = Run-Engine @("games", "install", "set", "msfs2024", $maInstall, "--text")
    Assert-True ($maSet.ExitCode -eq 0) "games install set msfs failed. Output: $($maSet.Output)"
    $maDry = Run-Engine @("msfs2024", "refresh-db", "--dry-run", "--text")
    Assert-True ($maDry.ExitCode -eq 0) "msfs refresh-db --dry-run failed. Output: $($maDry.Output)"
    Assert-Match $maDry.Output "MSFS 2024 aircraft found: 4 \(both sims: 4\)" "msfs refresh-db should find the 4 synthetic aircraft (liveries, AI traffic, shared and switched-off packages left out)."
    Assert-True (-not (Test-Path $maAircraftFile)) "msfs refresh-db --dry-run must not write the aircraft list."

    $maRefresh = Run-Engine @("msfs2024", "refresh-db", "--text")
    Assert-True ($maRefresh.ExitCode -eq 0) "msfs refresh-db failed. Output: $($maRefresh.Output)"
    Assert-True (Test-Path $maAircraftFile) "msfs2024 refresh-db did not write DB\MSFS2024\aircraft.tsv."
    $maRows = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($maAircraftFile)) {
        if ($line.Length -gt 0 -and -not $line.StartsWith("#")) { $maRows[$line.Split("`t")[0]] = $line.Split("`t") }
    }
    Assert-True ($maRows.Count -eq 4) "aircraft.tsv should list 4 aircraft, has $($maRows.Count)."
    $maTrainer = $maRows["smoke-aircraft-trainer"]
    Assert-True ($null -ne $maTrainer -and $maTrainer[1] -eq "Smoke Trainer" -and $maTrainer[2] -eq "airplane" -and $maTrainer[3] -eq "2020,2024" -and $maTrainer[4] -eq "SMK1") "aircraft.tsv: the 2020 package should have the manifest title, category, both sims and the ICAO type. Row: $($maTrainer -join ' | ')"
    Assert-True ($maTrainer[7] -eq "Smoke Trainer Blue; with a semicolon|Smoke Trainer Red|Smoke Trainer Floats") "aircraft.tsv: the variants should be the user-selectable [FLTSIM.n] titles of both SimObjects. Got: $($maTrainer[7])"
    Assert-True ($maRows["smoke-heli"][2] -eq "helicopter" -and $maRows["smoke-heli"][3] -eq "2024") "aircraft.tsv: a packed Community2024 package should come from its layout.json (Rotorcraft = helicopter)."
    Assert-True ($maRows["smoke-modular"][1] -eq "Smoke Jet A" -and $maRows["smoke-modular"][2] -eq "glider" -and $maRows["smoke-modular"][8] -eq "aircraft.cfg") "aircraft.tsv: a 2024 modular SimObject should take its title from the preset's aircraft.cfg."
    Assert-True ($maRows["microsoft-aircraft-smoke-glider"][1] -eq "Smoke Glider" -and $maRows["microsoft-aircraft-smoke-glider"][8] -eq "package name") "aircraft.tsv: a streamed 2024 aircraft should be named after its package (fs24- prefix removed)."
    Assert-True (-not $maRows.ContainsKey("smoke-aircraft-gone") -and -not $maRows.ContainsKey("smoke-aircraft-streamonly")) "aircraft.tsv must leave out switched-off packages and the streamed cache when a content list exists."
    $maAgain = Run-Engine @("msfs2024", "refresh-db", "--text")
    Assert-NotMatch $maAgain.Output "aircraft\.tsv" "a second msfs refresh-db must not rewrite an unchanged aircraft list."
    Assert-True (-not ((Test-Path (Join-Path $engineBin "DB\MSFS2024\aircraft.tsv")) -and ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\MSFS2024\aircraft.tsv")) -match "smoke-heli"))) "the shipped MSFS aircraft list must stay untouched."
    Assert-True (-not (Test-Path (Join-Path $maRoot "DB\MSFS2024\aircraft.tsv"))) "msfs refresh-db must not write the aircraft list into the program folder."

    $maPlanes = Run-Engine @("db", "planes", "--game", "MSFS2024", "--text")
    Assert-Match $maPlanes.Output "(?m)^MSFS2024 \(5\)" "db planes --game MSFS should list the game (all aircraft) and the 4 aircraft."
    Assert-Match $maPlanes.Output "(?m)^\s+- smoke-heli\s*$" "db planes --game MSFS should list the aircraft keys."

    # A relation for all aircraft and one limited to an aircraft (--node MSFS2024:<action>:<aircraft>).
    $maAll = Run-Engine @("--profile", $maProfile, "relations", "add", "MSFS Parking", "--button", "--node", "MSFS2024:KEY_PARKING_BRAKES", "--device", $maStick, "--input", "JOY_BTN7", "--text")
    Assert-True ($maAll.ExitCode -eq 0) "relations add for an MSFS relation for all aircraft failed."
    $maOnly = Run-Engine @("--profile", $maProfile, "relations", "add", "MSFS AP Trainer", "--button", "--node", "MSFS2024:KEY_AP_MASTER:smoke-aircraft-trainer", "--device", $maStick, "--input", "JOY_BTN2", "--text")
    Assert-True ($maOnly.ExitCode -eq 0) "relations add limited to an MSFS aircraft failed. Output: $($maOnly.Output)"
    Assert-Match (Run-Engine @("--profile", $maProfile, "relations", "show", "MSFS AP Trainer", "--text")).Output "smoke-aircraft-trainer" "the relation should be limited to the MSFS aircraft."

    $maExport = Run-Engine @("--profile", $maProfile, "msfs2024", "export", $maRemote, "--game-profile", "Sting", "--text")
    Assert-True ($maExport.ExitCode -eq 0) "msfs export with an aircraft-limited relation failed. Output: $($maExport.Output)"
    Assert-Match $maExport.Output "Relation 'MSFS AP Trainer' is limited to Microsoft Flight Simulator 2024 aircraft smoke-aircraft-trainer and was not exported\. MSFS 2024 input presets from JoyPro apply to every aircraft" "msfs export should explain why an aircraft-limited relation is skipped."
    $maAfter = [System.IO.File]::ReadAllText($maSting)
    Assert-Match $maAfter '(?s)ActionName="KEY_PARKING_BRAKES" Flag="2">\s*<Primary>\s*<KEY Information="Joystick Button 7">6</KEY>' "msfs export should write the relation for all aircraft."
    Assert-NotMatch $maAfter 'KEY_AP_MASTER' "msfs export without --aircraft must not write a relation limited to one aircraft."

    $maUnknown = Run-Engine @("--profile", $maProfile, "msfs2024", "export", $maRemote, "--game-profile", "Sting", "--aircraft", "no-such-aircraft", "--dry-run", "--text")
    Assert-Match $maUnknown.Output "Aircraft 'no-such-aircraft' is not in JoyPro's Microsoft Flight Simulator 2024 aircraft list" "msfs export --aircraft should warn about an aircraft that is not in the list."
    $maPicked = Run-Engine @("--profile", $maProfile, "msfs2024", "export", $maRemote, "--game-profile", "Sting", "--aircraft", "smoke-aircraft-trainer", "--text")
    Assert-True ($maPicked.ExitCode -eq 0) "msfs export --aircraft failed. Output: $($maPicked.Output)"
    Assert-Match $maPicked.Output "Aircraft picked: smoke-aircraft-trainer\. Exported the relations for all aircraft and 1 bind\(s\) limited to it" "msfs export --aircraft should report the picked aircraft."
    $maPickedText = [System.IO.File]::ReadAllText($maSting)
    Assert-Match $maPickedText '(?s)ActionName="KEY_AP_MASTER" Flag="2">\s*<Primary>\s*<KEY Information="Joystick Button 2">1</KEY>' "msfs export --aircraft should write the relation limited to the picked aircraft."
    Assert-Match $maPickedText 'ActionName="KEY_PARKING_BRAKES"' "msfs export --aircraft must keep the relations for all aircraft."

    $maScanAircraft = Run-Engine @("msfs2024", "scan", $maRemote, "--aircraft", "smoke-aircraft-trainer", "--include-disconnected", "--text")
    Assert-True ($maScanAircraft.ExitCode -ne 0) "--aircraft should only be accepted by msfs export."
    $maOther = Run-Engine @("xplane", "catalog", "--aircraft", "x", "--text")
    Assert-Match $maOther.Output "has no aircraft list; --aircraft is not supported" "--aircraft should be refused for games without an aircraft list."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $maPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $maPreviousDetection
}
