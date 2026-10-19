# Arma 3, Arma 2 / OA and Arma Reforger: response settings per physical axis, device and game settings, the Arma 3
# full-axis throttle, the split of the old "arma" tuning family and Reforger's order rules / filters / game settings.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Synthetic fixtures only.

$armaStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$armaThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
$armaPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
try {
    # ---- Arma 3 ------------------------------------------------------------------------------------------------
    $a3Dir = Join-Path $tempDir "arma-tuning\a3"
    New-Item -Path $a3Dir -ItemType Directory -Force | Out-Null
    $a3File = Join-Path $a3Dir "Tuning.Arma3Profile"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\arma3\Tuning.Arma3Profile") -Destination $a3File -Force
    $a3Pro = Join-Path $a3Dir "a3.pr0file"
    Run-Engine @("--profile", $a3Pro, "relations", "add", "A3 Pitch", "--axis", "--node", "Arma3:Axis.PlanePitch", "--device", $armaStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $a3Pro, "relations", "add", "A3 Throttle", "--axis", "--node", "Arma3:Axis.PlaneThrottle", "--device", $armaThrottle, "--input", "JOY_Z", "--text") | Out-Null
    $a3Set = Run-Engine @("--profile", $a3Pro, "binds", "tuning", "set", "A3 Pitch", "--scope", "game:Arma3", "--param", "sensitivityPos=0.5", "--param", "sensitivityNeg=-0.25", "--param", "deadZone=0.05", "--param", "steepness=1.4", "--text")
    Assert-True ($a3Set.ExitCode -eq 0) "binds tuning set with Arma 3 parameters failed: $($a3Set.Output)"
    Run-Engine @("--profile", $a3Pro, "binds", "tuning", "set", "A3 Throttle", "--scope", "game:Arma3", "--param", "steepness=2.4", "--text") | Out-Null
    $a3Range = Run-Engine @("--profile", $a3Pro, "binds", "tuning", "set", "A3 Pitch", "--scope", "game:Arma3", "--param", "sensitivityPos=5", "--text")
    Assert-True ($a3Range.ExitCode -ne 0) "an Arma 3 sensitivity outside -1..1 must be refused."
    $a3Preview = Run-Engine @("--profile", $a3Pro, "binds", "tuning", "preview", "A3 Pitch", "--game", "Arma3", "--points", "5", "--text")
    Assert-Match $a3Preview.Output "Model: arma3, from game:Arma3" "Arma 3 should use its own axis model."
    Assert-Match $a3Preview.Output "\+0\.500 -> \+0\.9399" "arma3 preview: cube-root blend after the DirectInput dead zone, times steepness 1.5 (0.94 at half travel)."
    Assert-Match $a3Preview.Output "-0\.500 -> -0\.5727" "arma3 preview: the negative half uses its own (cubic) sensitivity."
    Run-Engine @("--profile", $a3Pro, "ffb", "set", "--device", "*", "--game", "Arma3", "--param", "RTDEnabled=1", "--param", "RTDAutoTrim=1", "--text") | Out-Null
    Run-Engine @("--profile", $a3Pro, "ffb", "set", "--device", $armaStick, "--game", "Arma3", "--param", "analogClickHigh=0.6", "--text") | Out-Null
    $a3WrongScope = Run-Engine @("--profile", $a3Pro, "ffb", "set", "--device", "*", "--game", "Arma3", "--param", "analogClickHigh=0.6", "--text")
    Assert-True ($a3WrongScope.ExitCode -ne 0) "analogClickHigh is per device; device * must be refused."

    $a3Dry = Run-Engine @("--profile", $a3Pro, "arma3", "export", $a3File, "--mode", "clean", "--dry-run", "--text")
    Assert-Match $a3Dry.Output "setting\(s\) would change" "an arma3 dry run should count the settings."
    Assert-True ([System.IO.File]::ReadAllText($a3File) -eq [System.IO.File]::ReadAllText((Join-Path $engineDir "tests\fixtures\arma3\Tuning.Arma3Profile"))) "an arma3 dry run must not write."

    $a3Export = Run-Engine @("--profile", $a3Pro, "arma3", "export", $a3File, "--mode", "clean", "--text")
    Assert-True ($a3Export.ExitCode -eq 0) "arma3 export with tunings failed: $($a3Export.Output)"
    $a3Text = [System.IO.File]::ReadAllText($a3File)
    Assert-Match $a3Text "(?m)^keyHeliThrottlePos\[\]=\{197130,197122\};" "Arma 3 Thrust (Analog) must get both halves, negative (lever forward) first."
    Assert-Match $a3Text "(?m)^keyHeliForward\[\]=\{17,200,196873\};" "arma3 export should write the pitch's negative half."
    Assert-Match $a3Text "(?m)^\t\tsensitivity\[\]=\{0,0\.5,0,0,0,0,0,0,0,-0\.25,0,0,0,0,0,0\};" "arma3 export should write the Y axis sensitivity per half (a = +, a+8 = -)."
    Assert-Match $a3Text "(?m)^\t\tdeadZone\[\]=\{0\.07,0\.05\d*,0,0,0,0\.07,0,0\};" "arma3 export should write the Y dead zone (floats as the game prints them) and keep the others."
    Assert-Match $a3Text "(?s)deadZone\[\]=\{[^}]*\};\r?\n\t\tsteepness\[\]=\{1,1\.5,1,1,1,1,1,1\};\r?\n\t\tmode=""Custom"";" "arma3 export should create steepness[] after deadZone[] with 1.4 snapped to the game's 1.5."
    Assert-Match $a3Export.Output "not one of the game's choices" "arma3 export should say that it snapped the steepness."
    Assert-Match $a3Text "(?m)^\t\tanalogClickHigh=0\.6\d*;" "arma3 export should set the stick's analog click threshold."
    Assert-Match $a3Text "(?s)name=""Smoke Throttle"";\s*offset=512;\s*isXInput=0;\s*steepness\[\]=\{1,1,2\.4\d*,1,1,1,1,1\};\s*mode=""Custom"";" "arma3 export should enable the throttle and create its steepness[] in the game's order."
    Assert-Match $a3Export.Output "JoyPro enabled it" "arma3 export should say it enabled a disabled device."
    Assert-Match $a3Text "(?m)^RTDEnabled=1;" "arma3 export should set the advanced flight model in place."
    Assert-Match $a3Text "(?m)^RTDStressDamage=0;\r?\nRTDAutoTrim=1;" "arma3 export should add a missing RTD option after the others."
    Assert-Match $a3Text "(?m)^keyReloadMagazine\[\]=\{19\};" "arma3 export must not touch other lines."
    $a3Again = Run-Engine @("--profile", $a3Pro, "arma3", "export", $a3File, "--mode", "clean", "--text")
    Assert-Match $a3Again.Output "already matches" "a second arma3 export should change nothing."

    # One curve per physical axis: a second relation on the same stick axis with other values is reported.
    Run-Engine @("--profile", $a3Pro, "relations", "add", "A3 Cyclic", "--axis", "--node", "Arma3:Axis.HeliCyclicPitch", "--device", $armaStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $a3Pro, "binds", "tuning", "set", "A3 Cyclic", "--scope", "game:Arma3", "--param", "sensitivityPos=-1", "--text") | Out-Null
    $a3Conflict = Run-Engine @("--profile", $a3Pro, "arma3", "export", $a3File, "--mode", "clean", "--text")
    Assert-Match $a3Conflict.Output "one curve per physical axis: 'A3 Cyclic' is written" "arma3 export should warn when two relations put different curves on one axis (the first relation wins)."
    Assert-Match ([System.IO.File]::ReadAllText($a3File)) "(?m)^		sensitivity\[\]=\{0,-1,0,0,0,0,0,0,0,-0\.25," "the conflicting export should write the first relation's value and keep the other half."

    # Import with JoyPro's metadata next to the file (written by the exports above): the relations come back as they
    # were (names, groups, exact tuning), and exporting the import changes nothing.
    $a3Before = [System.IO.File]::ReadAllText($a3File)
    Assert-True (Test-Path ($a3File + ".joypro-meta.json")) "arma3 export should write JoyPro's metadata next to the profile."
    $a3MetaPro = Join-Path $a3Dir "a3-meta-import.pr0file"
    $a3MetaImport = Run-Engine @("--profile", $a3MetaPro, "arma3", "import", $a3File, "--text")
    Assert-True ($a3MetaImport.ExitCode -eq 0) "arma3 import with metadata failed."
    $a3MetaPitch = Run-Engine @("--profile", $a3MetaPro, "binds", "tuning", "show", "A3 Pitch", "--game", "Arma3", "--text")
    Assert-Match $a3MetaPitch.Output "game:Arma3: .*n\.deadZone=0\.05;n\.sensitivityNeg=-0\.25;n\.sensitivityPos=0\.5;n\.steepness=1\.4" "arma3 import should restore 'A3 Pitch' with its exact JoyPro tuning from the metadata."
    $a3MetaCyclic = Run-Engine @("--profile", $a3MetaPro, "relations", "show", "A3 Cyclic", "--text")
    Assert-True ($a3MetaCyclic.ExitCode -eq 0) "arma3 import should restore the relation 'A3 Cyclic' by name."
    Run-Engine @("--profile", $a3MetaPro, "arma3", "export", $a3File, "--text") | Out-Null
    Assert-True ([System.IO.File]::ReadAllText($a3File) -eq $a3Before) "arma3 import/export with metadata must leave the profile byte-identical."

    # Without the metadata (a file JoyPro never wrote): the device arrays become the binds' Arma 3 tuning (the plane
    # pitch joins the cyclic relation: same stick axis); exporting the import changes nothing.
    Move-Item -LiteralPath ($a3File + ".joypro-meta.json") -Destination ($a3File + ".meta-aside") -Force
    $a3ImportPro = Join-Path $a3Dir "a3-import.pr0file"
    $a3Import = Run-Engine @("--profile", $a3ImportPro, "arma3", "import", $a3File, "--text")
    Assert-True ($a3Import.ExitCode -eq 0) "arma3 import of the tuned profile failed."
    $a3Shown = Run-Engine @("--profile", $a3ImportPro, "binds", "tuning", "show", "Helicopter cyclic pitch (HeliCyclicForward / HeliCyclicBack)", "--game", "Arma3", "--text")
    Assert-Match $a3Shown.Output "game:Arma3: .*n\.deadZone=0\.05;n\.sensitivityNeg=-0\.25;n\.sensitivityPos=-1;n\.steepness=1\.5" "arma3 import should store the device's axis settings as the bind's Arma 3 tuning."
    Run-Engine @("--profile", $a3ImportPro, "arma3", "export", $a3File, "--text") | Out-Null
    Assert-True ([System.IO.File]::ReadAllText($a3File) -eq $a3Before) "arma3 import/export of the tuned profile must leave it byte-identical."
    Remove-Item -LiteralPath ($a3File + ".joypro-meta.json") -ErrorAction SilentlyContinue
    Move-Item -LiteralPath ($a3File + ".meta-aside") -Destination ($a3File + ".joypro-meta.json") -Force

    # Settings only: a profile without Arma 3 binds still writes its game options, and no key line.
    $a3SettingsPro = Join-Path $a3Dir "a3-settings.pr0file"
    Run-Engine @("--profile", $a3SettingsPro, "ffb", "set", "--device", "*", "--game", "Arma3", "--param", "RTDWindDynamic=1", "--text") | Out-Null
    $a3Settings = Run-Engine @("--profile", $a3SettingsPro, "arma3", "export", $a3File, "--mode", "clean", "--text")
    Assert-Match $a3Settings.Output "only the game settings were written" "an arma3 export without binds should write the settings only."
    $a3SettingsText = [System.IO.File]::ReadAllText($a3File)
    Assert-Match $a3SettingsText "(?m)^RTDWindDynamic=1;" "the settings-only export should set RTDWindDynamic."
    Assert-True ($a3SettingsText.Replace("RTDWindDynamic=1;", "RTDWindDynamic=0;") -eq $a3Before) "the settings-only export must not touch binds."

    # ---- Arma 2 / OA -------------------------------------------------------------------------------------------
    $a2Dir = Join-Path $tempDir "arma-tuning\a2"
    New-Item -Path $a2Dir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\arma2\*") -Destination $a2Dir -Force
    $oaFile = Join-Path $a2Dir "Smoke.ArmA2OAProfile"
    $oaPro = Join-Path $a2Dir "oa.pr0file"
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\bms\devices.tsv"
    $a2ThrottleName = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    Run-Engine @("--profile", $oaPro, "relations", "add", "OA Pitch", "--axis", "--node", "Arma2OA:Axis.Pitch", "--device", $armaStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $oaPro, "relations", "add", "OA Roll", "--axis", "--node", "Arma2OA:Axis.Roll", "--device", $armaStick, "--input", "JOY_X", "--text") | Out-Null
    $oaSet = Run-Engine @("--profile", $oaPro, "binds", "tuning", "set", "OA Pitch", "--scope", "game:Arma2OA", "--param", "sensitivity=-0.5", "--text")
    Assert-True ($oaSet.ExitCode -eq 0) "binds tuning set for Arma 2 OA failed: $($oaSet.Output)"
    # family:arma2 reaches Arma 2 and OA; the old family:arma still gives its invert.
    Run-Engine @("--profile", $oaPro, "binds", "tuning", "set", "OA Roll", "--scope", "family:arma2", "--param", "sensitivity=1", "--text") | Out-Null
    Run-Engine @("--profile", $oaPro, "binds", "tuning", "set", "OA Pitch", "--scope", "family:arma", "--inverted", "true", "--text") | Out-Null
    $oaShowA3 = Run-Engine @("--profile", $oaPro, "binds", "tuning", "show", "OA Pitch", "--game", "Arma3", "--text")
    Assert-Match $oaShowA3.Output "Arma3 uses \(arma3, from family:arma\): inv=1" "the old family:arma should still reach Arma 3 (invert)."
    $oaShowA2 = Run-Engine @("--profile", $oaPro, "binds", "tuning", "show", "OA Roll", "--game", "Arma2", "--text")
    Assert-Match $oaShowA2.Output "Arma2 uses \(arma2, from family:arma2\)" "family:arma2 should reach Arma 2."
    $oaPreview = Run-Engine @("--profile", $oaPro, "binds", "tuning", "preview", "OA Pitch", "--game", "Arma2OA", "--points", "5", "--text")
    # v = (0.5 - 0.07) / 0.93 = 0.46237; slider p = -0.5 (left, flatter): v^(10^0.5) = 0.0872
    Assert-Match $oaPreview.Output "\+0\.500 -> \+0\.0872" "arma2 preview: v^(10^-p) after the fixed 7 % dead zone."
    Run-Engine @("--profile", $oaPro, "ffb", "set", "--device", $a2ThrottleName, "--game", "Arma2OA", "--param", "enabled=0", "--text") | Out-Null
    $oaExport = Run-Engine @("--profile", $oaPro, "arma2oa", "export", $oaFile, "--mode", "clean", "--text")
    Assert-True ($oaExport.ExitCode -eq 0) "arma2oa export with sensitivity failed: $($oaExport.Output)"
    $oaText = [System.IO.File]::ReadAllText($oaFile)
    Assert-Match $oaText "(?m)^\t\tisXInput=0;\r?\n\t\tsensitivity\[\]=\{10,0\.31622776,1,1,1,1,1,1,10,0\.31622776,1,1,1,1,1,1\};\r?\n\t\tmode=""Custom"";" "arma2oa export should create sensitivity[] with s = 10^p on both halves (X: p=1 -> 10, Y: p=-0.5 -> 0.316)."
    Assert-Match $oaText "(?m)^joystickSensitivity\[\]=\{0\.5,0\.5,0\.5,0\.5,0\.5,0\.5,1,1,0\.5,0\.5,0\.5,0\.5,0\.5,0\.5,1,1\};" "arma2oa export must leave joystickSensitivity[] alone."
    Assert-Match $oaText "(?s)offset=512;\s*isXInput=0;\s*mode=""Disable"";" "arma2oa export should keep the throttle switched off as set."
    $oaImport = Run-Engine @("--profile", (Join-Path $a2Dir "oa-import.pr0file"), "arma2oa", "scan", $oaFile, "--text")
    Assert-True ($oaImport.ExitCode -eq 0) "arma2oa scan of the tuned profile failed."
    # With JoyPro's metadata the relation comes back by name; without it (next) the slider is read from the file.
    $oaMetaPro = Join-Path $a2Dir "oa-meta-import.pr0file"
    Run-Engine @("--profile", $oaMetaPro, "arma2oa", "import", $oaFile, "--text") | Out-Null
    $oaMetaShown = Run-Engine @("--profile", $oaMetaPro, "binds", "tuning", "show", "OA Pitch", "--game", "Arma2OA", "--text")
    Assert-Match $oaMetaShown.Output "game:Arma2OA: .*n\.sensitivity=-0\.5" "arma2oa import with metadata should restore 'OA Pitch' with its slider."
    Remove-Item -LiteralPath ($oaFile + ".joypro-meta.json") -Force
    $oaImportPro = Join-Path $a2Dir "oa-import.pr0file"
    Run-Engine @("--profile", $oaImportPro, "arma2oa", "import", $oaFile, "--text") | Out-Null
    $oaShown = Run-Engine @("--profile", $oaImportPro, "binds", "tuning", "show", "Pitch: nose down / nose up (HeliForward / HeliBack)", "--game", "Arma2OA", "--text")
    if ($oaShown.ExitCode -ne 0) {
        $oaRelations = Run-Engine @("--profile", $oaImportPro, "relations", "list", "--text")
        throw "arma2oa import: pitch relation not found. Relations: $($oaRelations.Output)"
    }
    Assert-Match $oaShown.Output "game:Arma2OA: .*n\.sensitivity=-0\.5" "arma2oa import should read the slider back (p = log10 s)."

    # ---- Arma Reforger -----------------------------------------------------------------------------------------
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
    $arSave = Join-Path $tempDir "arma-tuning\reforger\profile\.save"
    $arPresetDir = Join-Path $arSave "settings\customInputConfigs"
    $arSettingsDir = Join-Path $arSave "app1874880_user1\settings"
    New-Item -Path $arPresetDir -ItemType Directory -Force | Out-Null
    New-Item -Path $arSettingsDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\settings\EngineUserSettings.conf") -Destination $arSettingsDir -Force
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\settings\GameUserSettings.conf") -Destination $arSettingsDir -Force
    $arPreset = Join-Path $arPresetDir "JoyPro.conf"
    $arPro = Join-Path $tempDir "arma-tuning\reforger\ar.pr0file"
    Run-Engine @("--profile", $arPro, "relations", "add", "R Pitch", "--axis", "--node", "ArmaReforger:Axis.HelicopterCyclicPitch", "--device", $armaStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $arPro, "relations", "add", "R Start", "--button", "--node", "ArmaReforger:HelicopterEngineStart", "--device", $armaThrottle, "--input", "JOY_BTN5", "--text") | Out-Null
    $arSet = Run-Engine @("--profile", $arPro, "binds", "tuning", "set", "R Pitch", "--scope", "game:ArmaReforger", "--param", "multiplier=0.5", "--param", "useInvert=1", "--text")
    Assert-True ($arSet.ExitCode -eq 0) "binds tuning set for Reforger failed: $($arSet.Output)"
    Run-Engine @("--profile", $arPro, "ffb", "set", "--device", "*", "--game", "ArmaReforger", "--param", "hotasCollective=1", "--param", "forceFeedbackIntensity=2", "--text") | Out-Null
    $arExport = Run-Engine @("--profile", $arPro, "reforger", "export", $arPreset, "--text")
    Assert-True ($arExport.ExitCode -eq 0) "reforger export with settings failed: $($arExport.Output)"
    $arText = [System.IO.File]::ReadAllText($arPreset)
    Assert-Match $arText 'Input "joystick3:button4"' "reforger should keep the throttle in the slot its order rule gives it (3)."
    Assert-Match $arText '(?s)Input "joystick0:axis1-"\s*Filter InputFilterValue "\{[0-9A-F]{16}\}" \{\s*Multiplier 0\.5\s*UseInvert 1\s*\}' "reforger should write the axis filter (Multiplier, UseInvert)."
    $arEngine = [System.IO.File]::ReadAllText((Join-Path $arSettingsDir "EngineUserSettings.conf"))
    Assert-Match $arEngine '(?s)DeviceNameContains "Smoke Throttle"\s*DeviceSlotNumber 3\s*\}\s*JoystickOrderRule \{\s*DeviceNameContains "Smoke Stick"\s*DeviceSlotNumber 0\s*\}\s*\}' "reforger should append an order rule for the stick (lowest free slot) inside DeviceOrderRules."
    $arGame = [System.IO.File]::ReadAllText((Join-Path $arSettingsDir "GameUserSettings.conf"))
    Assert-Match $arGame '(?s)SCR_GameplaySettings "\{5A2B6E2F0E3B0001\}" \{\s*m_bMouseControlAircraft 1\s*m_eHotasCollective 1\s*\}' "reforger should add the HOTAS collective mode inside SCR_GameplaySettings."
    Assert-Match $arExport.Output "no SCR_ControllerSettings section" "reforger must not create the controller settings section for the force feedback intensity."
    $arAgain = Run-Engine @("--profile", $arPro, "reforger", "export", $arPreset, "--text")
    Assert-Match $arAgain.Output "The preset already matches" "a second reforger export should keep the blocks (same filter)."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $arSettingsDir "EngineUserSettings.conf")) -eq $arEngine) "a second reforger export must not change the order rules."
    $arMetaPro = Join-Path $tempDir "arma-tuning\reforger\ar-meta-import.pr0file"
    Run-Engine @("--profile", $arMetaPro, "reforger", "import", $arPreset, "--text") | Out-Null
    $arMetaShown = Run-Engine @("--profile", $arMetaPro, "binds", "tuning", "show", "R Pitch", "--game", "ArmaReforger", "--text")
    Assert-Match $arMetaShown.Output "game:ArmaReforger: .*n\.multiplier=0\.5;n\.useInvert=1" "reforger import with metadata should restore 'R Pitch' with its filter."
    Move-Item -LiteralPath ($arPreset + ".joypro-meta.json") -Destination ($arPreset + ".meta-aside") -Force
    $arImportPro = Join-Path $tempDir "arma-tuning\reforger\ar-import.pr0file"
    Run-Engine @("--profile", $arImportPro, "reforger", "import", $arPreset, "--text") | Out-Null
    Move-Item -LiteralPath ($arPreset + ".meta-aside") -Destination ($arPreset + ".joypro-meta.json") -Force
    $arScan = Run-Engine @("reforger", "scan", $arPreset, "--text")
    Assert-Match $arScan.Output "HelicopterEngineStart <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN5" "reforger scan should map joystick3 back to the throttle through the order rule."
    $arShown = Run-Engine @("--profile", $arImportPro, "binds", "tuning", "show", "Cyclic pitch (HelicopterCyclicForward / HelicopterCyclicBack)", "--game", "ArmaReforger", "--text")
    Assert-Match $arShown.Output "game:ArmaReforger: .*n\.multiplier=0\.5;n\.useInvert=1" "reforger import should read the filter back as the bind's tuning."

    # A settings file without the known section: no rules, the DirectInput order is used.
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\settings\NoInputSection.conf") -Destination (Join-Path $arSettingsDir "EngineUserSettings.conf") -Force
    $arNoSection = Run-Engine @("--profile", $arPro, "reforger", "export", $arPreset, "--mode", "clean", "--text")
    Assert-Match $arNoSection.Output "no InputDeviceUserSettings section" "reforger must refuse to create order rules without the section."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $arSettingsDir "EngineUserSettings.conf")) -eq [System.IO.File]::ReadAllText((Join-Path $engineDir "tests\fixtures\reforger\settings\NoInputSection.conf"))) "reforger must leave an unknown EngineUserSettings.conf alone."
    Assert-Match ([System.IO.File]::ReadAllText($arPreset)) 'Input "joystick1:button4"' "without order rules the throttle is joystick1 (the game's VID/PID order)."

    # 1.9 (Steam): Reforger*Settings.conf, rules with ids and the game's long device name ("<HID manufacturer> <product>").
    $ar19Save = Join-Path $tempDir "arma-tuning\reforger19\profile\.save"
    $ar19Presets = Join-Path $ar19Save "settings\customInputConfigs"
    $ar19Settings = Join-Path $ar19Save "app1874880_user2\settings"
    New-Item -Path $ar19Presets -ItemType Directory -Force | Out-Null
    New-Item -Path $ar19Settings -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\reforger\settings19\*") -Destination $ar19Settings -Force
    $ar19Preset = Join-Path $ar19Presets "JoyPro.conf"
    $ar19Bad = Run-Engine @("--profile", $arPro, "ffb", "set", "--device", $armaStick, "--game", "ArmaReforger", "--param", "joystickSlot=4", "--text")
    Assert-True ($ar19Bad.ExitCode -ne 0) "a Reforger joystick slot above 3 must be refused (the game indexes a 4-entry table)."
    Run-Engine @("--profile", $arPro, "ffb", "set", "--device", $armaStick, "--game", "ArmaReforger", "--param", "joystickSlot=2", "--text") | Out-Null
    $ar19Export = Run-Engine @("--profile", $arPro, "reforger", "export", $ar19Preset, "--text")
    Assert-True ($ar19Export.ExitCode -eq 0) "reforger export into a 1.9 profile failed: $($ar19Export.Output)"
    $ar19Engine = [System.IO.File]::ReadAllText((Join-Path $ar19Settings "ReforgerEngineSettings.conf"))
    Assert-Match $ar19Engine '(?s)DeviceNameContains "Vendor Smoke Throttle \(ARM\)"\s*DeviceSlotNumber 0\s*\}\s*JoystickOrderRule "\{[0-9A-F]{16}\}" \{\s*DeviceNameContains "Smoke Stick"\s*DeviceSlotNumber 2\s*\}' "reforger should keep the game's throttle rule and add the stick at its chosen slot 2, with an id, by its HID name."
    $ar19Text = [System.IO.File]::ReadAllText($ar19Preset)
    Assert-Match $ar19Text 'Input "joystick0:button4"' "the throttle keeps slot 0 from the game's own rule (long name)."
    Assert-Match $ar19Text 'Input "joystick2:axis1-"' "the stick binds go to its chosen slot 2."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $ar19Settings "ReforgerGameSettings.conf"))) '(?m)^\s*m_eHotasCollective 1\r?$' "reforger should write the HOTAS collective mode into ReforgerGameSettings.conf."
    Run-Engine @("--profile", $arPro, "ffb", "set", "--device", $armaStick, "--game", "ArmaReforger", "--param", "joystickSlot=-1", "--text") | Out-Null
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $armaPreviousFakeDevices
}
