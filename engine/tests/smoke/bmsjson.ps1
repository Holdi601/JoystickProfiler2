# Falcon BMS 4.38.2 (Launcher 3): the binds live in User\Config\JSON, one file per device and aircraft profile. Dot-sourced
# by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Fixtures: fixtures\bms-json\Config, built from a
# real 4.38.2 config with the smoke devices of fixtures\bms\devices.tsv (stick and throttle connected, Old Pedals not):
# the launcher's JSON files (F-16 and F-15ABCD), the matching keyboard files, a few lines of both key files, and a legacy
# Setup.v100 file the export must leave alone. The part builds its own command list from the fixture's key files
# (refresh-db into its own data folder), so it needs no game data.

$bjConfig = Join-Path $tempDir "bms-json\Falcon BMS 4.38\User\Config"
New-Item -Path $bjConfig -ItemType Directory -Force | Out-Null
# One spelling of every path (the file list below comes from the file system and keys the original bytes).
$bjConfig = (Get-Item -LiteralPath $bjConfig).FullName
Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\bms-json\\Config\\*") -Destination $bjConfig -Recurse -Force
$bjJson = Join-Path $bjConfig "JSON"
$bjStick16 = Join-Path $bjJson "DeviceBindings_F-16_12345678_Smoke Stick.json"
$bjStick15 = Join-Path $bjJson "DeviceBindings_F-15ABCD_12345678_Smoke Stick.json"
$bjThrottle16 = Join-Path $bjJson "DeviceBindings_F-16_ABCD0001_Smoke Throttle.json"
$bjThrottle15 = Join-Path $bjJson "DeviceBindings_F-15ABCD_ABCD0001_Smoke Throttle.json"
$bjPedals16 = Join-Path $bjJson "DeviceBindings_F-16_99998888_Old Pedals.json"
$bjLegacy = Join-Path $bjConfig "Setup.v100.Smoke Stick {11111111-2222-3333-4444-555555555555}.xml"
$bjStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
$bjThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
$bjProfile = Join-Path $tempDir "bms-json.pr0file"
$bjFiles = @(Get-ChildItem -Path $bjJson -Filter "DeviceBindings_*.json" | ForEach-Object { $_.FullName })
$bjOriginal = @{}
foreach ($bjFile in $bjFiles) { $bjOriginal[$bjFile] = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($bjFile)) }
$bjLegacyText = [System.IO.File]::ReadAllText($bjLegacy)

function Get-BjJson([string]$Path) { return ([System.IO.File]::ReadAllText($Path) | ConvertFrom-Json) }
function Get-BjButton($Doc, [int]$Index) { return @($Doc.button_bindings | Where-Object { $_.button_index -eq $Index -and $_.shift_state -eq "Unshifted" -and $_.trigger -eq "Press" }) }
function Get-BjAxis($Doc, [string]$Name) { return @($Doc.axis_bindings | Where-Object { $_.logical_axis_name -eq $Name })[0] }

$previousBjFake = $env:JOYPRO_FAKE_DEVICES
$previousBjData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "bms-json-data"
try {
    # The command list from the key files: the F-15's own callbacks are for the F-15 only; views and sim functions stay out.
    $bjRefresh = Run-Engine @("bms", "refresh-db", $bjConfig, "--text")
    Assert-True ($bjRefresh.ExitCode -eq 0) ("bms refresh-db of the 4.38.2 fixture failed: " + $bjRefresh.Output)
    Assert-Match $bjRefresh.Output "Callbacks of aircraft F-15ABCD read from .*: 1 only this aircraft has" ("bms refresh-db should read the F-15 key file named by its keyboard file: " + $bjRefresh.Output)
    $bjCatalog = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\\DB\\FalconBMS\\actions.tsv"))
    Assert-Match $bjCatalog "(?m)^SimF15BitReset\t[^\r\n]*\tbutton\t2\.02 BIT PANEL\t\tF-15ABCD\r?$" "the F-15's own callback should be in the catalog for the F-15 only."
    Assert-Match $bjCatalog "(?m)^SimHookToggle\tGEAR: HOOK Switch - Toggle\tbutton\t3\.05 GEAR PANEL\r?$" "a callback both aircraft have should be in the catalog for all aircraft (F-16 title)."
    Assert-NotMatch $bjCatalog "OTWViewLeft|TimeAccelerate" "views and simulation callbacks must stay out of the BMS catalog (F-15 chapters are numbered differently)."
    $bjAircraft = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\\DB\\FalconBMS\\aircraft.tsv"))
    Assert-Match $bjAircraft "(?m)^F-16\tF-16\r?$" "refresh-db should list the F-16 profile."
    Assert-Match $bjAircraft "(?m)^F-15ABCD\tF-15ABCD\r?$" "refresh-db should list the F-15ABCD profile."

    # Scan: axes from the F-16 file (shared by all aircraft), unshifted press binds per aircraft, the not-connected pedals
    # by their file; a bind of a kind JoyPro does not write is reported, not read.
    $bjScan = Run-Engine @("bms", "scan", $bjConfig, "--text")
    Assert-True ($bjScan.ExitCode -eq 0) ("bms scan of the 4.38.2 fixture failed: " + $bjScan.Output)
    Assert-Match $bjScan.Output "(?m)^Bindings Read:\s+9\s*$" ("bms scan should read 9 binds from the JSON files: " + $bjScan.Output)
    Assert-Match $bjScan.Output "axis/Pitch <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_Y" "bms scan should read the stick's pitch axis from the JSON file."
    Assert-Match $bjScan.Output "SimF15BitReset <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN7" "bms scan should read an F-15 bind (button_index 6 = JOY_BTN7)."
    Assert-Match $bjScan.Output "axis/Yaw <- Old Pedals \{99999999-8888-7777-6666-555555555555\} JOY_RZ" "bms scan should name a device that is not connected after its file."
    Assert-Match $bjScan.Output "1 shifted or release bind\(s\) in the BMS binding files were not read" "bms scan should report the entry it does not read."
    Assert-NotMatch $bjScan.Output "OTWViewLeft" "bms scan must leave view callbacks out."

    # Import and export again: nothing changes, byte for byte.
    $bjImport = Run-Engine @("--profile", $bjProfile, "bms", "import", $bjConfig, "--text")
    Assert-True ($bjImport.ExitCode -eq 0) ("bms import of the 4.38.2 fixture failed: " + $bjImport.Output)
    Assert-Match $bjImport.Output "(?m)^Relations Imported:\s+9\s*$" ("bms import should create 9 relations: " + $bjImport.Output)
    $bjRelations = Run-Engine @("--profile", $bjProfile, "relations", "list", "--text")
    Assert-Match $bjRelations.Output "(?m)^BIT: RCALL/RSET Switch - RSET \(Reset\) \|[^\r\n]*planes=1" "the imported F-15 bind should be limited to its aircraft."
    $bjRound = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjConfig, "--text")
    Assert-True ($bjRound.ExitCode -eq 0) ("bms export after the 4.38.2 import failed: " + $bjRound.Output)
    Assert-Match $bjRound.Output "The BMS binding files already match" ("the round trip should find nothing to change: " + $bjRound.Output)
    foreach ($bjFile in $bjFiles) {
        Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($bjFile)) -eq $bjOriginal[$bjFile]) ("bms import/export round trip must leave " + $bjFile + " byte-identical.")
    }

    # Changes: pickle moves (F-16 only), roll moves to the throttle, the hook moves (both aircraft), a hat bind is reported.
    Assert-True ((Run-Engine @("--profile", $bjProfile, "binds", "set", "STICK: WEAPON RELEASE (Pickle)", "--input", "JOY_BTN3", "--text")).ExitCode -eq 0) "binds set for the BMS pickle failed."
    Assert-True ((Run-Engine @("--profile", $bjProfile, "binds", "set", "Roll", "--device", $bjThrottle, "--input", "JOY_X", "--text")).ExitCode -eq 0) "binds set for BMS roll failed."
    Assert-True ((Run-Engine @("--profile", $bjProfile, "binds", "set", "GEAR: HOOK Switch - Toggle", "--input", "JOY_BTN9", "--text")).ExitCode -eq 0) "binds set for the BMS hook failed."
    Assert-True ((Run-Engine @("--profile", $bjProfile, "relations", "add", "DMS on hat", "--button", "--node", "FalconBMS:SimDMSUp", "--device", $bjStick, "--input", "JOY_BTN_POV1_U", "--text")).ExitCode -eq 0) "relations add for the hat failed."

    $bjDry = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjConfig, "--dry-run", "--text")
    Assert-Match $bjDry.Output "Dry run: \d+ BMS binding file\(s\) would change" ("bms dry run should count the JSON files: " + $bjDry.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($bjStick16)) -eq $bjOriginal[$bjStick16]) "bms dry run must not write the JSON files."

    $bjExport = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjConfig, "--text")
    Assert-True ($bjExport.ExitCode -eq 0) ("bms export into the 4.38.2 layout failed: " + $bjExport.Output)
    Assert-Match $bjExport.Output "DMS on hat' \(SimDMSUp\): JoyPro does not write hat binds" "bms export should report the hat bind it cannot write yet."
    Assert-Match $bjExport.Output "bind\(s\) not written for aircraft F-15ABCD: its key file has no such callback" "bms export should say which binds the F-15 cannot take."
    $s16 = Get-BjJson $bjStick16
    $s15 = Get-BjJson $bjStick15
    $t16 = Get-BjJson $bjThrottle16
    $t15 = Get-BjJson $bjThrottle15
    Assert-True ((Get-BjButton $s16 0).Count -eq 0) "bms export should release the pickle's old button (F-16)."
    Assert-True ((Get-BjButton $s16 2)[0].callback_name -eq "SimPickle" -and (Get-BjButton $s16 2)[0].invoke -eq "Default" -and (Get-BjButton $s16 2)[0].assignment_index -eq 0) "bms export should write the pickle as an unshifted press entry (F-16)."
    Assert-True ((Get-BjButton $s15 2).Count -eq 0) "bms export must not put an F-16 callback into the F-15 file."
    Assert-True ((Get-BjButton $t16 8)[0].callback_name -eq "SimHookToggle" -and (Get-BjButton $t15 8)[0].callback_name -eq "SimHookToggle") "a callback both aircraft have goes into both files."
    Assert-True ((Get-BjButton $t16 3).Count -eq 0 -and (Get-BjButton $t15 3).Count -eq 0) "the moved hook must leave its old button in both files."
    Assert-True ((Get-BjButton $s16 5)[0].callback_name -eq "OTWViewLeft") "merge-overwrite must keep the view bind JoyPro does not manage."
    Assert-True (@($s16.button_bindings | Where-Object { $_.shift_state -ne "Unshifted" }).Count -eq 1) "the entry JoyPro does not write must stay."
    Assert-True ($null -eq (Get-BjAxis $s16 "Roll").physical_axis_index -and $null -eq (Get-BjAxis $s15 "Roll").physical_axis_index) "bms export should take roll off the stick in every aircraft file."
    Assert-True ((Get-BjAxis $t16 "Roll").physical_axis_index -eq 0 -and (Get-BjAxis $t15 "Roll").physical_axis_index -eq 0) "bms export should give roll to the throttle's X axis in every aircraft file."
    Assert-True ((Get-BjAxis $s16 "Pitch").physical_axis_index -eq 1) "bms export must keep pitch on the stick."
    Assert-True ((Get-BjButton $t15 6)[0].callback_name -eq "SimF15BitReset") "bms export must keep the F-15 bind."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($bjPedals16)) -eq $bjOriginal[$bjPedals16]) "bms export must not touch the file of a device that is not connected."
    Assert-True ([System.IO.File]::ReadAllText($bjLegacy) -eq $bjLegacyText) "with the JSON layout, bms export must leave the launcher's Setup.v100 files alone (the launcher rebuilds them)."
    Assert-True (Test-Path ($bjStick16 + ".jpbak")) "bms export should back up the JSON file before it writes."
    $bjStickText = [System.IO.File]::ReadAllText($bjStick16)
    Assert-True ($bjStickText.StartsWith("{`r`n  `"schema_version`": 2,`r`n") -and $bjStickText.EndsWith("`r`n}`r`n") -and ([System.IO.File]::ReadAllBytes($bjStick16)[0] -eq 0x7B)) "bms export must write the launcher's layout (CRLF, two-space indent, no BOM)."
    Assert-NotMatch $bjStickText "[^\r]\n" "bms export must keep CRLF line ends."

    $bjAgain = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjConfig, "--text")
    Assert-Match $bjAgain.Output "The BMS binding files already match" "a second bms export must change nothing."

    # Clean: the aircraft files JoyPro writes buttons into lose the binds it does not set (the view), the unknown entry
    # stays; the F-15 file of the stick (no F-15 button of the profile there) keeps its binds.
    $bjClean = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjConfig, "--mode", "clean", "--text")
    Assert-True ($bjClean.ExitCode -eq 0) ("bms clean export into the 4.38.2 layout failed: " + $bjClean.Output)
    $s16 = Get-BjJson $bjStick16
    Assert-True ((Get-BjButton $s16 5).Count -eq 0) "bms clean should release the view bind on a device JoyPro writes."
    Assert-True ((Get-BjButton (Get-BjJson $bjStick15) 5)[0].callback_name -eq "OTWViewLeft") "bms clean must keep the binds of an aircraft file JoyPro writes no button into (F-15 stick)."
    Assert-True (@($s16.button_bindings | Where-Object { $_.shift_state -ne "Unshifted" }).Count -eq 1) "bms clean must keep entries of kinds JoyPro does not write."
    Assert-True ((Get-BjButton (Get-BjJson $bjThrottle15) 6)[0].callback_name -eq "SimF15BitReset") "bms clean must keep the profile's F-15 bind."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($bjPedals16)) -eq $bjOriginal[$bjPedals16]) "bms clean must not clear a device that is not connected."

    # The watcher looks at the JSON files, not at the device files the launcher rebuilds.
    $bjSync = Run-Engine @("sync", "status", "--game", "FalconBMS", "--path", $bjConfig)
    Assert-Match $bjSync.Output "JSON`",`"Filters`":\[`"DeviceBindings_\*\.json`"\]" ("the BMS watcher should watch the JSON binding files: " + $bjSync.Output)
    Assert-NotMatch $bjSync.Output "Setup\.v100" "the BMS watcher must not watch the device files the 4.38.2 launcher rebuilds."

    # The new launcher before its first start with the devices: no device files yet, a clear message, nothing written.
    $bjEmpty = Join-Path $tempDir "bms-json-empty\\User\\Config"
    New-Item -Path (Join-Path $bjEmpty "JSON") -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $bjJson "KeyboardBindings_F-16.json") -Destination (Join-Path $bjEmpty "JSON") -Force
    $bjNone = Run-Engine @("--profile", $bjProfile, "bms", "export", $bjEmpty, "--text")
    Assert-True ($bjNone.ExitCode -ne 0) "bms export without JSON device files must fail."
    Assert-Match $bjNone.Output "Start the BMS launcher once with the devices connected" ("bms export without JSON device files should say what to do: " + $bjNone.Output)
    Assert-True (@(Get-ChildItem -Path $bjEmpty -Recurse -File).Count -eq 1) "bms export without JSON device files must not create files."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $previousBjFake
    $env:JOYPRO_DATA_DIR = $previousBjData
}
