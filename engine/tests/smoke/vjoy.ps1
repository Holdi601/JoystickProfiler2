# Virtual joystick mode (docs/virtual-joystick.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir, $engineDir, $Configuration). Never touches the PC's vJoy or HidHide: JOYPRO_VJOY_FAKE points the engine at
# files in a temp folder (vjoy.txt, hidhide-state.txt, the calls it would have run), JOYPRO_FAKE_DEVICES lists two
# sticks and the vJoy device, and JoyPro.VJoy only runs with the dry sink against devices that do not exist.
#  1. engine vjoy status/mode/plan/map: per-game virtual mode, the stable table (X stays X, free axis otherwise,
#     modifiers and hats), new inputs append, nothing moves, --prune/--reset, capacity per vJoy device.
#  2. configure-vjoy: --dry-run runs nothing; otherwise one call per device that lacks something; never shrinks.
#  3. dcs export in virtual mode writes the binds (and the modifier) to the vJoy device; the profile keeps the physical
#     devices; dcs import turns vJoy binds back into the physical inputs; native mode writes the physical devices again.
#  4. apply-hidhide/restore-hidhide/recover against the fake HidHide: dry run, block list, parked dormant entries,
#     vJoy shown, exact restore, declined prompt, stale owner, the user's allow list.
#  5. forwarder-config and a dry JoyPro.VJoy run (ready/stats/exit, no allocation in the loop).
#  6. the virtual Xbox pad.
#  7. virtual functions (add with free outputs, reserved in the table, test, keys only on request, forwarder-config,
#     a dry run with a function), per-game input limits, configure-vjoy --max (dry run only).
$vjDir = Join-Path $tempDir "vjoy"
$vjFake = Join-Path $vjDir "fake"
$vjData = Join-Path $vjDir "data"
$vjFixture = Join-Path $engineDir "tests\fixtures\vjoy"
$vjProfile = Join-Path $vjDir "virtual.pr0file"
$vjSaved = Join-Path $vjDir "saved-games"
$vjDcsExe = Join-Path $vjDir "games\DCS World\bin\DCS.exe"
$vjStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$vjThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$vjSection = [string][char]0xA7
$vjUtf8 = New-Object System.Text.UTF8Encoding $false
New-Item -ItemType Directory -Force -Path $vjFake, $vjData, $vjSaved, (Split-Path -Parent $vjDcsExe) | Out-Null
[System.IO.File]::WriteAllText($vjDcsExe, "", $vjUtf8)
Copy-Item (Join-Path $vjFixture "hidhide-gaming.json") (Join-Path $vjFake "hidhide-gaming.json")
$vjDormant = "--cloak-off`r`n--inv-on`r`n--dev-hide `"HID\HIDCLASS\1&2d595ca7&1&0000`"`r`n--dev-hide `"HID\VID_16D0&PID_0A38\c&33bf0f2b&0&0000`"`r`n--app-reg `"C:\Games\MSFS\FlightSimulator.exe`"`r`n"
[System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-state.txt"), $vjDormant, $vjUtf8)
[System.IO.File]::WriteAllText((Join-Path $vjFake "vjoy.txt"), "installed=2.1.9.1`r`ndevice=1 axes=x,y,z,rx,ry,rz,sl0,sl1 buttons=1 povs=4 discrete=0`r`n", $vjUtf8)

$vjPrevious = @{
    Fake = $env:JOYPRO_VJOY_FAKE
    Devices = $env:JOYPRO_FAKE_DEVICES
    Data = $env:JOYPRO_DATA_DIR
    Detection = $env:JOYPRO_INSTALL_DETECTION
    Dry = $env:JOYPRO_VJOY_DRY
}
$env:JOYPRO_VJOY_FAKE = $vjFake
$env:JOYPRO_FAKE_DEVICES = Join-Path $vjFixture "devices.tsv"
$env:JOYPRO_DATA_DIR = $vjData
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_VJOY_DRY = "1"

function Get-VjHidHide {
    return [System.IO.File]::ReadAllText((Join-Path $vjFake "hidhide-state.txt"))
}

function Get-VjSorted([string]$Text) {
    return ((($Text -split "`r?`n") | Where-Object { $_ -ne "" } | Sort-Object) -join "|")
}

try {
    # ---- 1. mode / plan / map
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--device", $vjStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Roll", "--axis", "--node", "DCS:a2002cdnil:F-16C_50", "--device", $vjStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Thr X", "--axis", "--node", "DCS:a2004cdnil:F-16C_50", "--device", $vjThrottle, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Thr Z", "--axis", "--node", "DCS:a2005cdnil:F-16C_50", "--device", $vjThrottle, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Fire", "--button", "--node", "DCS:d3015pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $vjStick, "--input", "JOY_BTN1", "--modifier", ("Shift" + $vjSection + $vjThrottle + $vjSection + "JOY_BTN7" + $vjSection + "False"), "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Hat", "--button", "--node", "DCS:d3016pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $vjStick, "--input", "JOY_BTN_POV1_U", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "relations", "add", "IL2 Only", "--button", "--node", "IL2Game:somekey", "--device", $vjThrottle, "--input", "JOY_BTN5", "--text") | Out-Null

    $vjStatus = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
    Assert-True ($vjStatus.ExitCode -eq 0) "vjoy status failed. Output: $($vjStatus.Output)"
    Assert-Match $vjStatus.Output "vJoy: installed 2\.1\.9\.1 \(fake\)" "vjoy status must report the (fake) vJoy installation."
    Assert-Match $vjStatus.Output "vJoy device 1: x,y,z,rx,ry,rz,sl0,sl1, 1 buttons, 4 hats \| vJoy Device \{89D63500" "vjoy status must parse the vJoy device and find it in DirectInput."
    Assert-Match $vjStatus.Output "HidHide: installed fake, cloaking off, inverse \(block list\), 2 hidden device\(s\), 1 program\(s\)" "vjoy status must read HidHide's state."
    Assert-Match $vjStatus.Output "Mapping: 0 slot\(s\)" "Without a virtual game there must be no slot."
    $vjStatusJson = Run-Engine @("--profile", $vjProfile, "vjoy", "status")
    $vjStatusObject = $vjStatusJson.Output | ConvertFrom-Json
    Assert-True ($vjStatusObject.RefreshRateHz -ge 60) "vjoy status must report a refresh rate of at least 60 Hz."
    Assert-True ($vjStatusObject.Session.Phase -eq "none") "vjoy status must report no session yet."

    $vjBadMode = Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "NoSuchGame", "virtual")
    Assert-True ($vjBadMode.ExitCode -ne 0) "vjoy mode must refuse an unknown game."
    $vjModeDry = Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "DCS", "virtual", "--dry-run", "--text")
    Assert-Match $vjModeDry.Output "Dry run: DCS World now uses" "vjoy mode --dry-run must say it saved nothing."
    $vjAfterDry = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
    Assert-Match $vjAfterDry.Output "Mapping: 0 slot\(s\)" "vjoy mode --dry-run must not save the mapping."

    $vjMode = Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "DCS", "virtual", "--text")
    Assert-True ($vjMode.ExitCode -eq 0) "vjoy mode DCS virtual failed. Output: $($vjMode.Output)"
    Assert-Match $vjMode.Output "Slots: 7 \(added 7" "vjoy mode must give the 7 used inputs a slot (2+2 axes, button, modifier button, hat)."
    Assert-Match $vjMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_X -> vJoy 1 JOY_X" "An axis keeps its own axis when it is free."
    Assert-Match $vjMode.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_X -> vJoy 1 JOY_Z" "An axis whose own axis is taken gets the first free one."
    Assert-Match $vjMode.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_Z -> vJoy 1 JOY_RX" "The throttle's Z gets the next free axis."
    Assert-Match $vjMode.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN7 -> vJoy 1 JOY_BTN2" "The modifier button must get a slot."
    Assert-Match $vjMode.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} POV1 -> vJoy 1 POV1" "The hat must get a whole hat slot."
    Assert-NotMatch $vjMode.Output "JOY_BTN5" "Inputs of games in native mode (IL-2) must not get a slot."
    Assert-Match $vjMode.Output "needs x,y,z,rx, 2 buttons, 1 hats; has x,y,z,rx,ry,rz,sl0,sl1, 1 buttons, 4 hats -> configure: vJoyConfig 1 -f -a x y z rx ry rz sl0 sl1 -b 32 -p 4" "vjoy mode must say the vJoy device lacks a button and how JoyPro configures it."

    # Stable: a new input appends, nothing that exists moves.
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VJ Gear", "--button", "--node", "DCS:d3001pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $vjThrottle, "--input", "JOY_BTN2", "--text") | Out-Null
    $vjPlan = Run-Engine @("--profile", $vjProfile, "vjoy", "plan", "--text")
    Assert-Match $vjPlan.Output "Plan \(nothing saved\)" "vjoy plan must not save."
    Assert-Match $vjPlan.Output "JOY_BTN2 -> vJoy 1 JOY_BTN3 \(new\)" "A new button goes to the next free vJoy button."
    Assert-Match $vjPlan.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN7 -> vJoy 1 JOY_BTN2\r?\n" "An existing slot must not move."
    $vjMap = Run-Engine @("--profile", $vjProfile, "vjoy", "map", "--text")
    Assert-Match $vjMap.Output "Mapping saved in the profile" "vjoy map must save the new slot."
    $vjMapAgain = Run-Engine @("--profile", $vjProfile, "vjoy", "map", "--text")
    Assert-Match $vjMapAgain.Output "The mapping is up to date" "A second vjoy map must change nothing."
    Run-Engine @("--profile", $vjProfile, "relations", "mass", "delete", "--relation", "VJ Gear", "--text") | Out-Null
    $vjKept = Run-Engine @("--profile", $vjProfile, "vjoy", "map", "--text")
    Assert-Match $vjKept.Output "unused 1" "A slot no relation uses any more stays (stable) and counts as unused."
    $vjPrune = Run-Engine @("--profile", $vjProfile, "vjoy", "map", "--prune", "--text")
    Assert-Match $vjPrune.Output "removed: VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN2" "vjoy map --prune must drop the unused slot."

    # Capacity: only one vJoy device allowed and more than 8 axes -> error; device range 1-2 spreads them.
    $vjCapProfile = Join-Path $vjDir "capacity.pr0file"
    $vjAxes = @("JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2")
    for ($i = 0; $i -lt 8; $i++) {
        Run-Engine @("--profile", $vjCapProfile, "relations", "add", ("Cap T" + $i), "--axis", "--node", ("DCS:a" + (3000 + $i) + "cdnil:F-16C_50"), "--device", $vjThrottle, "--input", $vjAxes[$i], "--text") | Out-Null
    }
    Run-Engine @("--profile", $vjCapProfile, "relations", "add", "Cap S", "--axis", "--node", "DCS:a3100cdnil:F-16C_50", "--device", $vjStick, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $vjCapProfile, "vjoy", "settings", "--last-device", "1", "--text") | Out-Null
    $vjFull = Run-Engine @("--profile", $vjCapProfile, "vjoy", "mode", "DCS", "virtual", "--text")
    Assert-True ($vjFull.ExitCode -ne 0) "Nine axes on one allowed vJoy device must fail."
    Assert-Match $vjFull.Output "No free vJoy axis left on devices 1-1" "The capacity error must name the device range."
    Run-Engine @("--profile", $vjCapProfile, "vjoy", "settings", "--last-device", "2", "--text") | Out-Null
    $vjTwo = Run-Engine @("--profile", $vjCapProfile, "vjoy", "mode", "DCS", "virtual", "--text")
    Assert-True ($vjTwo.ExitCode -eq 0) "With two vJoy devices allowed the ninth axis must fit. Output: $($vjTwo.Output)"
    Assert-Match $vjTwo.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_RZ -> vJoy 1 JOY_RZ" "The stick (first by name) keeps its RZ."
    Assert-Match $vjTwo.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_RZ -> vJoy 1 JOY_SLIDER1" "The throttle's RZ takes the first free axis."
    Assert-Match $vjTwo.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_SLIDER2 -> vJoy 2 JOY_SLIDER2" "The ninth axis must go to vJoy device 2 on its own axis."
    Assert-Match $vjTwo.Output "vJoy device 2: 1 slot\(s\).*-> configure: vJoyConfig 2 -f -a x y z rx ry rz sl0 sl1 -b 0 -p 0" "vJoy device 2 must be planned for configuration."

    # ---- 2. configure-vjoy
    $vjCallsFile = Join-Path $vjFake "vjoyconfig-calls.txt"
    $vjConfDry = Run-Engine @("--profile", $vjProfile, "vjoy", "configure-vjoy", "--dry-run", "--text")
    Assert-Match $vjConfDry.Output "Dry run: vJoy was not changed.*vJoyConfig 1 -f -a x y z rx ry rz sl0 sl1 -b 32 -p 4" "configure-vjoy --dry-run must show the vJoyConfig call."
    Assert-True (-not (Test-Path $vjCallsFile)) "configure-vjoy --dry-run must run nothing."
    $vjConf = Run-Engine @("--profile", $vjProfile, "vjoy", "configure-vjoy", "--text")
    Assert-True ($vjConf.ExitCode -eq 0) "configure-vjoy failed. Output: $($vjConf.Output)"
    Assert-True (@(Get-Content $vjCallsFile).Count -eq 1) "configure-vjoy must run vJoyConfig once (one prompt)."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $vjFake "vjoy.txt"))) "device=1 axes=x,y,z,rx,ry,rz,sl0,sl1 buttons=32 povs=4" "The fake driver must have the new configuration."
    $vjConfAgain = Run-Engine @("--profile", $vjProfile, "vjoy", "configure-vjoy", "--text")
    Assert-Match $vjConfAgain.Output "nothing to configure" "configure-vjoy must not run again once the devices fit."
    Assert-True (@(Get-Content $vjCallsFile).Count -eq 1) "A second configure-vjoy must not call vJoyConfig."

    # ---- 3. export / import on the vJoy slots
    $vjExport = Run-Engine @("--profile", $vjProfile, "dcs", "export", $vjSaved, "--text")
    Assert-True ($vjExport.ExitCode -eq 0) "dcs export in virtual mode failed. Output: $($vjExport.Output)"
    $vjJoystickDir = Join-Path $vjSaved "Config\Input\F-16C_50\joystick"
    $vjFiles = @(Get-ChildItem -Path $vjJoystickDir -Filter "*.diff.lua" | Select-Object -ExpandProperty Name)
    Assert-True ($vjFiles.Count -eq 1 -and $vjFiles[0] -like "vJoy Device {89D63500-B53F-11*") "dcs export in virtual mode must write only the vJoy device file. Files: $($vjFiles -join ', ')"
    $vjLua = [System.IO.File]::ReadAllText((Join-Path $vjJoystickDir $vjFiles[0]))
    Assert-Match $vjLua '\["a2004cdnil"\][\s\S]*?\["key"\] = "JOY_Z"' "The throttle's X must be exported on vJoy Z."
    Assert-Match $vjLua '\["a2005cdnil"\][\s\S]*?\["key"\] = "JOY_RX"' "The throttle's Z must be exported on vJoy RX."
    Assert-Match $vjLua '\["d3016pnilunilcd2vd1vpnilvunil"\][\s\S]*?\["key"\] = "JOY_BTN_POV1_U"' "The hat direction must be exported on the vJoy hat."
    $vjModifiers = [System.IO.File]::ReadAllText((Join-Path $vjSaved "Config\Input\F-16C_50\modifiers.lua"))
    Assert-Match $vjModifiers '\["Shift"\] = \{\s*\["device"\] = "vJoy Device \{89D63500-B53F-11F1-8002-444553540000\}",\s*\["key"\] = "JOY_BTN2"' "The modifier must be exported on its vJoy button."
    $vjBinds = Run-Engine @("--profile", $vjProfile, "binds", "list", "--text")
    Assert-Match $vjBinds.Output "VJ Thr X \| type=axis \| device=VJ Throttle \{11111111-2222-3333-4444-000000000002\} \| input=JOY_X" "The profile must keep the physical devices after a virtual export."
    Assert-NotMatch $vjBinds.Output "vJoy Device" "The profile must not point at vJoy after a virtual export."

    $vjImportProfile = Join-Path $vjDir "imported.pr0file"
    Copy-Item $vjProfile $vjImportProfile
    $vjImport = Run-Engine @("--profile", $vjImportProfile, "dcs", "import", $vjSaved, "--text")
    Assert-True ($vjImport.ExitCode -eq 0) "dcs import of the vJoy files failed. Output: $($vjImport.Output)"
    $vjImported = Run-Engine @("--profile", $vjImportProfile, "binds", "list", "--text")
    Assert-Match $vjImported.Output "VJ Thr Z \| type=axis \| device=VJ Throttle \{11111111-2222-3333-4444-000000000002\} \| input=JOY_Z" "dcs import must turn the vJoy axis back into the physical one."
    Assert-Match $vjImported.Output ("VJ Fire \| type=button \| device=VJ Stick \{11111111-2222-3333-4444-000000000001\} \| input=JOY_BTN1 \| modifiers\(1\)=Shift" + $vjSection + "VJ Throttle") "dcs import must turn the vJoy modifier back into the physical button."
    Assert-NotMatch $vjImported.Output "vJoy Device" "dcs import in virtual mode must not leave vJoy binds in the profile."

    # A missing vJoy device stops the export with a clear message instead of writing binds nobody can use.
    $vjNoVJoy = Join-Path $vjDir "devices-novjoy.tsv"
    Get-Content (Join-Path $vjFixture "devices.tsv") | Where-Object { $_ -notmatch "^vJoy" } | Set-Content $vjNoVJoy
    $env:JOYPRO_FAKE_DEVICES = $vjNoVJoy
    $vjMissing = Run-Engine @("--profile", $vjProfile, "dcs", "export", (Join-Path $vjDir "saved-novjoy"), "--text")
    $env:JOYPRO_FAKE_DEVICES = Join-Path $vjFixture "devices.tsv"
    Assert-True ($vjMissing.ExitCode -ne 0) "dcs export in virtual mode without a vJoy device must fail."
    Assert-Match $vjMissing.Output "vJoy device\(s\) 1 are not there" "The missing vJoy device must be named."

    # ---- 4. HidHide
    $vjHideDry = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--dry-run", "--text")
    Assert-True ($vjHideDry.ExitCode -ne 0) "apply-hidhide without a known game executable must fail."
    Assert-Match $vjHideDry.Output "No game executable is known" "Without a known DCS.exe the block list cannot be built."
    Run-Engine @("--profile", $vjProfile, "vjoy", "exe", "DCS", "--add", $vjDcsExe, "--text") | Out-Null
    $vjHideDry = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--dry-run", "--text")
    Assert-Match $vjHideDry.Output "Dry run: HidHide was not changed" "apply-hidhide --dry-run must say so."
    Assert-Match $vjHideDry.Output '--dev-hide "HID\\VID_3344&PID_80CB&Col01\\c&2eaeded2&0&0000"' "The stick's instance path must come from HidHide's own device list."
    Assert-Match $vjHideDry.Output '--dev-hide "HID\\VID_03EB&PID_2054\\c&7187b8&0&0000"' "The throttle's instance path must be derived from its HID path."
    Assert-Match $vjHideDry.Output '--dev-unhide "HID\\HIDCLASS\\1&2d595ca7&1&0000"' "The vJoy device the user hid must be shown for the session."
    Assert-Match $vjHideDry.Output '--app-unreg "C:\\Games\\MSFS\\FlightSimulator.exe"' "Dormant entries must be parked."
    Assert-Match $vjHideDry.Output "--cloak-on\r?\n" "Cloaking must come last."
    Assert-True ((Get-VjHidHide) -eq $vjDormant) "apply-hidhide --dry-run must not change HidHide."
    Assert-True (-not (Test-Path (Join-Path $vjFake "hidhide-calls.txt"))) "apply-hidhide --dry-run must not call HidHideCLI."

    # Declined prompt: nothing changed, no session left.
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-exit.txt"), "-1223", $vjUtf8)
    $vjDeclined = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--text")
    Remove-Item (Join-Path $vjFake "hidhide-exit.txt")
    Assert-True ($vjDeclined.ExitCode -eq 2) "A declined prompt must exit with 2."
    Assert-Match $vjDeclined.Output "administrator prompt was declined" "A declined prompt must be explained."
    Assert-True ((Get-VjHidHide) -eq $vjDormant) "A declined prompt must leave HidHide unchanged."
    Assert-True (-not (Test-Path (Join-Path $vjData "VirtualJoystick\hidhide-session.txt"))) "A declined prompt must not leave a session behind."

    $vjApply = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--owner-pid", "$PID", "--text")
    Assert-True ($vjApply.ExitCode -eq 0) "apply-hidhide failed. Output: $($vjApply.Output)"
    $vjActive = Get-VjHidHide
    Assert-Match $vjActive "--cloak-on" "HidHide must be on during the session."
    Assert-NotMatch $vjActive "HIDCLASS" "vJoy must not be hidden during the session."
    Assert-NotMatch $vjActive "FlightSimulator" "The dormant program entry must be parked."
    Assert-Match $vjActive ([regex]::Escape($vjDcsExe)) "DCS must be on the block list."
    Assert-True (@(Get-Content (Join-Path $vjFake "hidhide-calls.txt")).Count -eq 2) "apply-hidhide must call HidHideCLI once (the declined call counts as the first)."
    $vjSessionStatus = (Run-Engine @("--profile", $vjProfile, "vjoy", "status")).Output | ConvertFrom-Json
    Assert-True ($vjSessionStatus.Session.Phase -eq "active" -and -not $vjSessionStatus.Session.Stale) "The session must be active and owned by this shell."
    $vjRecoverLive = Run-Engine @("vjoy", "recover", "--text")
    Assert-Match $vjRecoverLive.Output "still in use by process" "recover must not touch a session whose owner runs."
    $vjRestore = Run-Engine @("vjoy", "restore-hidhide", "--text")
    Assert-True ($vjRestore.ExitCode -eq 0) "restore-hidhide failed. Output: $($vjRestore.Output)"
    Assert-True ((Get-VjSorted (Get-VjHidHide)) -eq (Get-VjSorted $vjDormant)) "restore-hidhide must give HidHide back exactly its previous state."
    Assert-True (@(Get-ChildItem (Join-Path $vjData "VirtualJoystick\history") -Filter "hidhide-session-*.txt").Count -ge 1) "A restored session must be kept in the history."

    # Crash: the owner is gone -> stale -> recover restores.
    $vjGone = Start-Process -FilePath "cmd.exe" -ArgumentList "/c", "exit" -PassThru -WindowStyle Hidden
    $vjGone.WaitForExit()
    $vjApply2 = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--owner-pid", "$($vjGone.Id)", "--text")
    Assert-True ($vjApply2.ExitCode -eq 0) "apply-hidhide for the crash check failed. Output: $($vjApply2.Output)"
    $vjStale = (Run-Engine @("--profile", $vjProfile, "vjoy", "status")).Output | ConvertFrom-Json
    Assert-True ($vjStale.Session.Stale) "A session whose owner is gone must be stale."
    $vjRefused = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--text")
    Assert-True ($vjRefused.ExitCode -ne 0) "apply-hidhide must refuse while a stale session waits for recovery."
    $vjRecover = Run-Engine @("vjoy", "recover", "--text")
    Assert-True ($vjRecover.ExitCode -eq 0) "recover failed. Output: $($vjRecover.Output)"
    Assert-True ((Get-VjSorted (Get-VjHidHide)) -eq (Get-VjSorted $vjDormant)) "recover must give HidHide back exactly its previous state."

    # The user's own allow list stays an allow list: the forwarder is allowed, DCS is not.
    $vjAllow = "--cloak-on`r`n--inv-off`r`n--dev-hide `"HID\VID_16D0&PID_0A38\c&33bf0f2b&0&0000`"`r`n--app-reg `"C:\Tools\joystick_gremlin.exe`"`r`n"
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-state.txt"), $vjAllow, $vjUtf8)
    $vjTrusted = Join-Path $vjDir "JoyPro.Gui.exe"
    [System.IO.File]::WriteAllText($vjTrusted, "", $vjUtf8)
    $vjAllowApply = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--trusted", $vjTrusted, "--text")
    Assert-Match $vjAllowApply.Output "Strategy: allow-list" "An active allow list must be kept."
    Assert-Match (Get-VjHidHide) ([regex]::Escape($vjTrusted)) "JoyPro must be allowed on the user's allow list."
    Assert-NotMatch (Get-VjHidHide) ([regex]::Escape($vjDcsExe)) "DCS must not be allowed."
    Run-Engine @("vjoy", "restore-hidhide", "--text") | Out-Null
    Assert-True ((Get-VjSorted (Get-VjHidHide)) -eq (Get-VjSorted $vjAllow)) "restore-hidhide must give the allow list back unchanged."

    # Reads that fail are never taken as "off"/"empty". Without administrator rights the hidden-device list is denied:
    # status and the dry run say so; the real step (elevated, here the child process) reads everything.
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-state.txt"), $vjDormant, $vjUtf8)
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-deny.txt"), "devices`r`n", $vjUtf8)
    $vjDenied = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
    Assert-Match $vjDenied.Output "cloaking off, inverse \(block list\), hidden devices unknown, 1 program\(s\)" "status must show the denied device list as unknown, not as empty."
    Assert-Match $vjDenied.Output "not read: devices: Error code 0x0005 Access is denied" "status must say why the device list is unknown."
    $vjDeniedDry = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--dry-run", "--text")
    Assert-Match $vjDeniedDry.Output "\(partial: see notes\)" "A dry run without the device list must say it is partial."
    Assert-Match $vjDeniedDry.Output "hidden devices could not be read" "A dry run must name the part it could not read."
    $vjDeniedApply = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--owner-pid", "$PID", "--text")
    Assert-True ($vjDeniedApply.ExitCode -eq 0) "The elevated step must read the device list itself. Output: $($vjDeniedApply.Output)"
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $vjData "VirtualJoystick\hidhide-session.txt"))) "previous=--inv-on" "The journal must hold the inverse mode as read (on)."
    Run-Engine @("vjoy", "restore-hidhide", "--text") | Out-Null
    Assert-True ((Get-VjSorted (Get-VjHidHide)) -eq (Get-VjSorted $vjDormant)) "restore after a denied non-elevated read must give HidHide back exactly."
    Remove-Item (Join-Path $vjFake "hidhide-deny.txt")

    # A query that hangs, even in the elevated step: nothing is changed, no session is written.
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-hang.txt"), "inverse`r`n", $vjUtf8)
    $vjHung = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--text")
    Assert-True ($vjHung.ExitCode -ne 0) "apply-hidhide must fail when HidHide cannot be read."
    Assert-Match $vjHung.Output "inverse mode could not be read.*Nothing was changed" "apply-hidhide must say which part could not be read."
    Assert-True ((Get-VjHidHide) -eq $vjDormant) "A failed read must leave HidHide unchanged."
    Assert-True (-not (Test-Path (Join-Path $vjData "VirtualJoystick\hidhide-session.txt"))) "A failed read must not leave a session behind."
    # With a session running, a restore that cannot read keeps the session and changes nothing.
    Remove-Item (Join-Path $vjFake "hidhide-hang.txt")
    Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--owner-pid", "$PID", "--text") | Out-Null
    $vjDuring = Get-VjHidHide
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-hang.txt"), "devices`r`n", $vjUtf8)
    $vjHungRestore = Run-Engine @("vjoy", "restore-hidhide", "--text")
    Assert-True ($vjHungRestore.ExitCode -ne 0) "restore-hidhide must fail when HidHide cannot be read."
    Assert-True ((Get-VjHidHide) -eq $vjDuring) "A restore that cannot read must not change HidHide."
    Assert-True (Test-Path (Join-Path $vjData "VirtualJoystick\hidhide-session.txt")) "A restore that cannot read must keep the session."
    Remove-Item (Join-Path $vjFake "hidhide-hang.txt")
    Run-Engine @("vjoy", "restore-hidhide", "--text") | Out-Null
    Assert-True ((Get-VjSorted (Get-VjHidHide)) -eq (Get-VjSorted $vjDormant)) "The restore after the hang must give HidHide back exactly."

    # A journal whose inverse mode before the session is unknown: the restore leaves the inverse mode alone.
    $vjJournalDir = Join-Path $vjData "VirtualJoystick"
    $vjUnknownJournal = "# JoyPro HidHide session v1`r`nphase=active`r`nowner-pid=0`r`nowner-start=0`r`nstrategy=block-list`r`ngames=DCS`r`nset-cloak=true`r`nset-inverse=true`r`nadded-app=$vjDcsExe`r`nprevious=--cloak-off`r`n"
    [System.IO.File]::WriteAllText((Join-Path $vjJournalDir "hidhide-session.txt"), $vjUnknownJournal, $vjUtf8)
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-state.txt"), "--cloak-on`r`n--inv-on`r`n--app-reg `"$vjDcsExe`"`r`n", $vjUtf8)
    $vjUnknownRestore = Run-Engine @("vjoy", "restore-hidhide", "--text")
    Assert-True ($vjUnknownRestore.ExitCode -eq 0) "restore with an unknown previous inverse mode failed. Output: $($vjUnknownRestore.Output)"
    Assert-Match (Get-VjHidHide) "--inv-on" "An unknown previous inverse mode must never be set to off."
    Assert-Match (Get-VjHidHide) "--cloak-off" "Cloaking JoyPro switched on must go off again."
    [System.IO.File]::WriteAllText((Join-Path $vjFake "hidhide-state.txt"), $vjDormant, $vjUtf8)

    # The real HidHideCLI path with a scripted stand-in: one query per call, time limit, ended with its children.
    $vjScript = Join-Path $vjDir "HidHideCLI-standin.cmd"
    $vjScriptText = "@echo off`r`nif `"%1`"==`"--cloak-state`" (echo --cloak-off& exit /b 0)`r`nif `"%1`"==`"--inv-state`" (ping -n 30 127.0.0.3 >nul& exit /b 0)`r`nif `"%1`"==`"--app-list`" (echo --app-reg `"C:\Games\MSFS\FlightSimulator.exe`"& exit /b 0)`r`nif `"%1`"==`"--dev-list`" (echo Error code 0x0005 - Access is denied.& exit /b 5)`r`nexit /b 1`r`n"
    [System.IO.File]::WriteAllText($vjScript, $vjScriptText, (New-Object System.Text.ASCIIEncoding))
    $env:JOYPRO_VJOY_FAKE = ""
    $env:JOYPRO_HIDHIDE_CLI = $vjScript
    # 5 s: long enough for a cmd.exe start on a busy PC, far below the stand-in's ~29 s hang.
    $env:JOYPRO_HIDHIDE_TIMEOUT_MS = "5000"
    try {
        $vjWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $vjReal = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
        $vjWatch.Stop()
        Assert-True ($vjReal.ExitCode -eq 0) "status with a hanging HidHideCLI failed. Output: $($vjReal.Output)"
        Assert-True ($vjWatch.Elapsed.TotalSeconds -lt 20) "status must not hang on HidHideCLI (took $($vjWatch.Elapsed.TotalSeconds) s)."
        Assert-Match $vjReal.Output "cloaking off, inverse mode unknown, hidden devices unknown, 1 program\(s\)" "status must keep the parts it read and mark the others unknown."
        Assert-Match $vjReal.Output "not read: --inv-state: no answer within 5000 ms \(ended\)" "status must report the hanging query."
        Assert-Match $vjReal.Output "not read: --dev-list: Error code 0x0005 - Access is denied" "status must report the denied query."
        $vjDryReal = Run-Engine @("--profile", $vjProfile, "vjoy", "apply-hidhide", "--dry-run", "--text")
        Assert-Match $vjDryReal.Output "inverse mode, hidden devices could not be read" "The dry run must name what it could not read."
        Start-Sleep -Milliseconds 500
        $vjLeft = @(Get-CimInstance Win32_Process -Filter "Name='PING.EXE' OR Name='cmd.exe'" | Where-Object { $_.CommandLine -match "127\.0\.0\.3|HidHideCLI-standin" })
        Assert-True ($vjLeft.Count -eq 0) "A HidHideCLI call that ran over its time limit must be ended with its children ($($vjLeft.Count) left)."
    }
    finally {
        $env:JOYPRO_VJOY_FAKE = $vjFake
        $env:JOYPRO_HIDHIDE_CLI = ""
        $env:JOYPRO_HIDHIDE_TIMEOUT_MS = ""
    }

    # ---- 5. forwarder table and a dry JoyPro.VJoy run
    $vjTable = Join-Path $vjDir "forwarder.json"
    $vjConfig = Run-Engine @("--profile", $vjProfile, "vjoy", "forwarder-config", "--out", $vjTable, "--text")
    Assert-True ($vjConfig.ExitCode -eq 0) "forwarder-config failed. Output: $($vjConfig.Output)"
    $vjTableObject = Get-Content $vjTable -Raw | ConvertFrom-Json
    Assert-True (@($vjTableObject.sources).Count -eq 2 -and @($vjTableObject.slots).Count -eq 7) "The forwarder table must list 2 sources and 7 slots."
    Assert-True ((@($vjTableObject.slots | Where-Object { $_.kind -eq "hat" }).Count) -eq 1) "The forwarder table must hold the hat slot."

    $vjRepo = Split-Path -Parent $engineDir
    $vjExe = Join-Path $vjRepo ("gui\JoyPro.VJoy\bin\" + $Configuration + "\net9.0-windows\JoyPro.VJoy.exe")
    if (-not (Test-Path $vjExe)) {
        & dotnet build (Join-Path $vjRepo "gui\JoyPro.VJoy\JoyPro.VJoy.csproj") -c $Configuration | Out-Null
    }
    Assert-True (Test-Path $vjExe) "JoyPro.VJoy.exe was not built: $vjExe"
    # Sources that do not exist: the run touches no device and, with the dry sink (JOYPRO_VJOY_DRY=1), no vJoy.
    $vjGhost = '{"version":1,"vjoyDevices":[1],"sources":[{"device":"Ghost {00000000-0000-0000-0000-00000000F00D}","name":"Ghost","instanceGuid":"00000000-0000-0000-0000-00000000f00d"}],"slots":[{"source":0,"kind":"axis","index":0,"device":1,"target":0},{"source":0,"kind":"button","index":0,"device":1,"target":0}]}'
    $vjGhostFile = Join-Path $vjDir "ghost.json"
    [System.IO.File]::WriteAllText($vjGhostFile, $vjGhost, $vjUtf8)
    $vjRun = & $vjExe run --config $vjGhostFile --sink vjoy --duration 3 2>&1 | ForEach-Object { $_.ToString() }
    $vjRunText = $vjRun -join "`n"
    Assert-Match $vjRunText "source 0 missing Ghost" "JoyPro.VJoy must report the missing device."
    Assert-Match $vjRunText "ready sink=dry rate=\d+" "JOYPRO_VJOY_DRY=1 must force the dry sink."
    Assert-Match $vjRunText "stats passes=\d+ hz=\d+" "JoyPro.VJoy must report stats."
    Assert-Match $vjRunText "exit duration" "JoyPro.VJoy must end after --duration."
    $vjLastStats = @($vjRun | Where-Object { $_ -like "stats *" }) | Select-Object -Last 1
    $vjAlloc = [long](($vjLastStats -split ' ' | Where-Object { $_ -like "loop-alloc=*" }) -replace "loop-alloc=", "")
    Assert-True ($vjAlloc -lt 4096) "The forwarding loop must not allocate per pass (loop-alloc=$vjAlloc)."
    $vjEmpty = & $vjExe run --config $vjTable --sink dry --duration 1 2>&1 | ForEach-Object { $_.ToString() }
    Assert-Match ($vjEmpty -join "`n") "ready sink=dry" "JoyPro.VJoy must start with the engine's table."

    # ---- 6. the virtual Xbox pad (mode xbox): its own table per game, what does not fit, forwarding (dry)
    $vjXbStatus0 = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
    Assert-Match $vjXbStatus0.Output "ViGEmBus \(virtual Xbox pad\): not installed \(https://github.com/nefarius/ViGEmBus/releases\)" "status must say ViGEmBus is missing and link the official installer."
    [System.IO.File]::AppendAllText((Join-Path $vjFake "vjoy.txt"), "vigem=1.22.0`r`n", $vjUtf8)
    $vjXb = Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "DCS", "xbox", "--text")
    Assert-True ($vjXb.ExitCode -eq 0) "vjoy mode DCS xbox failed. Output: $($vjXb.Output)"
    Assert-Match $vjXb.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_X -> Xbox XB_LX \(Left stick X\)" "The first axis goes to the left stick X."
    Assert-Match $vjXb.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_Z -> Xbox XB_RY \(Right stick Y\)" "The fourth axis goes to the right stick Y."
    Assert-Match $vjXb.Output "VJ Stick \{11111111-2222-3333-4444-000000000001\} POV1 -> Xbox XB_DPAD \(D-pad\)" "A hat goes to the D-pad."
    Assert-Match $vjXb.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_BTN7 -> Xbox XB_B \(B\)" "The modifier button gets a pad button."
    # Too many axes for one pad: six fit (2 sticks, 2 triggers), the rest is listed.
    $vjXbProfile = Join-Path $vjDir "xbox-capacity.pr0file"
    for ($i = 0; $i -lt 8; $i++) {
        Run-Engine @("--profile", $vjXbProfile, "relations", "add", ("Xb T" + $i), "--axis", "--node", ("DCS:a" + (3000 + $i) + "cdnil:F-16C_50"), "--device", $vjThrottle, "--input", $vjAxes[$i], "--text") | Out-Null
    }
    $vjXbFull = Run-Engine @("--profile", $vjXbProfile, "vjoy", "mode", "DCS", "xbox", "--text")
    Assert-True ($vjXbFull.ExitCode -eq 0) "An Xbox table with leftovers must still be saved. Output: $($vjXbFull.Output)"
    Assert-Match $vjXbFull.Output "JOY_RZ -> Xbox XB_RT \(Right trigger\)" "The sixth axis goes to the right trigger."
    Assert-Match $vjXbFull.Output "does not fit: VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_SLIDER1 \(Xb T6\) does not fit: every Xbox axis" "What does not fit must be listed with its relation."
    $vjXbStatus = Run-Engine @("--profile", $vjProfile, "vjoy", "status", "--text")
    Assert-Match $vjXbStatus.Output "ViGEmBus \(virtual Xbox pad\): installed 1\.22\.0 \(fake\)" "status must report ViGEmBus."
    Assert-Match $vjXbStatus.Output "DCS \| xbox \| " "status must show the game's Xbox mode."
    $vjXbTable = Join-Path $vjDir "forwarder-xbox.json"
    Run-Engine @("--profile", $vjProfile, "vjoy", "forwarder-config", "--out", $vjXbTable, "--xbox-game", "DCS", "--text") | Out-Null
    $vjXbObject = Get-Content $vjXbTable -Raw | ConvertFrom-Json
    Assert-True ($vjXbObject.xboxGame -eq "DCS" -and @($vjXbObject.xbox).Count -eq 7 -and @($vjXbObject.slots).Count -eq 0) "The forwarder table must hold DCS's 7 Xbox slots and no vJoy slot (DCS is not on vJoy any more)."
    $vjXbBad = Run-Engine @("--profile", $vjProfile, "vjoy", "forwarder-config", "--xbox-game", "IL2Game")
    Assert-True ($vjXbBad.ExitCode -ne 0) "forwarder-config must refuse a game that is not in Xbox mode."
    # A dry run with an Xbox table (ghost devices, JOYPRO_VJOY_DRY=1: no ViGEm pad is created).
    $vjXbGhost = '{"version":1,"vjoyDevices":[],"sources":[{"device":"Ghost {00000000-0000-0000-0000-00000000F00D}","name":"Ghost","instanceGuid":"00000000-0000-0000-0000-00000000f00d"}],"slots":[],"xboxGame":"DCS","xbox":[{"source":0,"kind":"axis","index":0,"target":"XB_LX"},{"source":0,"kind":"button","index":0,"target":"XB_A"}]}'
    $vjXbGhostFile = Join-Path $vjDir "ghost-xbox.json"
    [System.IO.File]::WriteAllText($vjXbGhostFile, $vjXbGhost, $vjUtf8)
    $vjXbRun = (& $vjExe run --config $vjXbGhostFile --xbox-sink vigem --duration 2 --monitor 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
    Assert-Match $vjXbRun "ready sink=dry .*xbox=dry:2:DCS" "JOYPRO_VJOY_DRY=1 must force the dry Xbox sink."
    Assert-Match $vjXbRun "xout 0 0 0 0 0 0 0000" "The live view must show the neutral Xbox pad."
    Assert-Match $vjXbRun "exit duration" "JoyPro.VJoy with an Xbox table must end after --duration."
    Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "DCS", "virtual", "--text") | Out-Null

    # ---- 7. virtual functions, per-game limits, vJoy maximum (docs/virtual-joystick.md "Virtual functions")
    $vfTypes = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "types", "--text")
    Assert-Match $vfTypes.Output "bands - Axis bands" "functions types must list the axis bands."
    Assert-Match $vfTypes.Output "buttons-hat - Buttons to hat" "functions types must list every type."
    $vfAdd = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "add", "--game", "DCS", "--type", "bands", "--name", "Throttle detents", "--input", ("in=" + $vjThrottle + "|JOY_Z"), "--edges", "0.05,0.95", "--output", "band3=auto:enter", "--text")
    Assert-True ($vfAdd.ExitCode -eq 0) "functions add failed. Output: $($vfAdd.Output)"
    Assert-Match $vfAdd.Output "Added the function f1 `"Throttle detents`" \(Axis bands\) for DCS World" "functions add must name the new function."
    $vfList = (Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "list", "--game", "DCS")).Output | ConvertFrom-Json
    $vfFunction = @($vfList.Functions)[0].Function
    $vfTargets = @($vfFunction.Outputs | ForEach-Object { $_.Target })
    Assert-True ($vfTargets.Count -eq 3 -and ($vfTargets | Where-Object { $_ -notmatch "^JOY_BTN\d+$" }).Count -eq 0) "Every band must get a free vJoy button. Got: $($vfTargets -join ', ')"
    Assert-True ((@($vfFunction.Outputs | Where-Object { $_.Role -eq "band3" })[0].Mode) -eq "enter") "--output band3=auto:enter must keep the mode."
    $vfSlots = (Run-Engine @("--profile", $vjProfile, "vjoy", "plan")).Output | ConvertFrom-Json
    $vfSlotTargets = @($vfSlots.Slots | Where-Object { $_.Device -eq 1 } | ForEach-Object { $_.Target })
    Assert-True ((@($vfTargets | Where-Object { $vfSlotTargets -contains $_ })).Count -eq 0) "Function outputs must not collide with the table's slots."
    # Reserved: a new input gets the next free button, never a function output.
    Run-Engine @("--profile", $vjProfile, "relations", "add", "VF Reserve", "--button", "--node", "DCS:d3017pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $vjStick, "--input", "JOY_BTN9", "--text") | Out-Null
    $vfPlan = Run-Engine @("--profile", $vjProfile, "vjoy", "plan", "--text")
    Assert-Match $vfPlan.Output "\+ 3 function output\(s\)" "The plan must count the function outputs on the vJoy device."
    $vfNew = [regex]::Match($vfPlan.Output, "JOY_BTN9 -> vJoy 1 (JOY_BTN\d+) \(new\)")
    Assert-True ($vfNew.Success -and ($vfTargets -notcontains $vfNew.Groups[1].Value)) "The planner must not give a function output to a new input. Plan: $($vfPlan.Output)"
    Run-Engine @("--profile", $vjProfile, "relations", "mass", "delete", "--relation", "VF Reserve", "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "vjoy", "map", "--prune", "--text") | Out-Null
    # Simulated: nothing goes anywhere.
    $vfTest = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "test", "--game", "DCS", "--id", "f1", "--text")
    Assert-Match $vfTest.Output "band2 \(JOY_BTN\d+\) -> on" "functions test must show the middle band firing."
    Assert-Match $vfTest.Output "band3 \(JOY_BTN\d+\) -> on" "functions test must show the afterburner band's pulse."
    $vfStream = (Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "test", "--game", "DCS", "--id", "f1", "--stream", "0:in=0.5; 100:in=0.97; 400:in=0.5", "--duration", "600")).Output | ConvertFrom-Json
    $vfPulse = @($vfStream.Events | Where-Object { $_.Output -eq "band3" })
    Assert-True ($vfPulse.Count -eq 2 -and [double]$vfPulse[1].TimeMs - [double]$vfPulse[0].TimeMs -ge 75 -and [double]$vfPulse[1].TimeMs - [double]$vfPulse[0].TimeMs -le 90) "An enter pulse must last about 80 ms. Events: $($vfStream.Events | ConvertTo-Json -Compress)"
    # Keys only on request.
    $vfSpec = Join-Path $vjDir "macro-spec.json"
    [System.IO.File]::WriteAllText($vfSpec, '{"Type":"macro","Name":"Keys","Steps":[{"Output":"KEY_A","Action":"tap","DelayMs":0,"HoldMs":50}]}', $vjUtf8)
    $vfMacro = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "add", "--game", "DCS", "--type", "macro", "--spec", ("@" + $vfSpec), "--dry-run", "--text")
    Assert-Match $vfMacro.Output "switch on `"Keyboard output`"" "A key step without --keyboard on must be flagged."
    Assert-Match $vfMacro.Output "Nothing was saved" "functions add --dry-run must save nothing."
    # The forwarder gets the function, resolved, and its device as a source.
    $vfTable = Join-Path $vjDir "forwarder-functions.json"
    Run-Engine @("--profile", $vjProfile, "vjoy", "forwarder-config", "--out", $vfTable, "--game", "DCS", "--text") | Out-Null
    $vfTableObject = Get-Content $vfTable -Raw | ConvertFrom-Json
    $vfForwarded = @($vfTableObject.functions)
    Assert-True ($vfForwarded.Count -eq 1 -and [int]$vfForwarded[0].Inputs[0].Source -ge 0 -and $vfForwarded[0].Inputs[0].Kind -eq "axis") "forwarder-config must pass the function resolved."
    Assert-True ((@($vfTableObject.sources | Where-Object { $_.device -eq $vjThrottle })).Count -eq 1) "The function's device must be a source of the forwarder."
    # A dry run with ghost devices: the function runs every pass, the live view reports it, nothing is allocated.
    $vfGhost = '{"version":1,"vjoyDevices":[1],"sources":[{"device":"Ghost {00000000-0000-0000-0000-00000000F00D}","name":"Ghost","instanceGuid":"00000000-0000-0000-0000-00000000f00d"}],"slots":[],' +
        '"functions":[{"Id":"g1","Name":"Turbo","Type":"turbo","Enabled":true,"Inputs":[{"Role":"in","Device":"Ghost {00000000-0000-0000-0000-00000000F00D}","Input":"JOY_BTN1","Source":0,"Kind":"button","Index":0,"Direction":-1}],' +
        '"Outputs":[{"Role":"out","Target":"JOY_BTN40","VJoyDevice":1,"Kind":"button","DeviceIndex":0,"Index":39}],"Params":{"onMs":50,"offMs":50}}]}'
    $vfGhostFile = Join-Path $vjDir "ghost-functions.json"
    [System.IO.File]::WriteAllText($vfGhostFile, $vfGhost, $vjUtf8)
    $vfRun = & $vjExe run --config $vfGhostFile --sink vjoy --duration 3 --monitor 2>&1 | ForEach-Object { $_.ToString() }
    $vfRunText = $vfRun -join "`n"
    Assert-Match $vfRunText "ready sink=dry .*functions=1" "JoyPro.VJoy must load the virtual functions."
    Assert-Match $vfRunText "fn g1 0 0 0" "The live view must report the function's state."
    $vfStats = @($vfRun | Where-Object { $_ -like "stats *" }) | Select-Object -Last 1
    $vfAlloc = [long](($vfStats -split ' ' | Where-Object { $_ -like "loop-alloc=*" }) -replace "loop-alloc=", "")
    Assert-True ($vfAlloc -lt 4096) "The loop with virtual functions must not allocate per pass (loop-alloc=$vfAlloc)."
    # Per-game limits: a game that reads only 32 buttons is warned about outputs above them.
    Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "Arma2OA", "virtual", "--text") | Out-Null
    $vfArma = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "add", "--game", "Arma2OA", "--type", "toggle", "--output", "out=JOY_BTN40@1", "--text")
    Assert-Match $vfArma.Output "reads only the first 32 buttons of a joystick: this function's outputs on button 40 on vJoy 1 will not work there" "A function output above the game's 32 buttons must be warned about."
    $vfArmaStatus = (Run-Engine @("--profile", $vjProfile, "vjoy", "status")).Output | ConvertFrom-Json
    $vfArmaGame = @($vfArmaStatus.Games | Where-Object { $_.Id -eq "Arma2OA" })[0]
    Assert-True ((@($vfArmaGame.LimitWarnings) -join " ") -match "reads only the first 32 buttons") "status must carry the game's limit warning for its card."
    Assert-True ($vfArmaGame.InputLimit.Buttons -eq 32 -and $vfArmaGame.InputLimit.Confidence -eq "verified") "status must carry what the game reads."
    $vfLimits = Run-Engine @("--profile", $vjProfile, "vjoy", "limits", "--text")
    Assert-Match $vfLimits.Output "AceCombat7: reads 32 buttons" "engine vjoy limits must list ACE COMBAT 7's 32 buttons."
    Assert-Match $vfLimits.Output "StarCitizen: what it reads of a joystick is not known" "Unknown limits must say so (and warn about nothing)."
    $vfArmaId = @(((Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "list", "--game", "Arma2OA")).Output | ConvertFrom-Json).Functions)[0].Id
    Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "remove", "--game", "Arma2OA", "--id", $vfArmaId, "--text") | Out-Null
    Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "Arma2OA", "native", "--text") | Out-Null
    # vJoy's maximum: dry run only (on a real PC vJoyConfig needs a Windows restart).
    $vfCalls = @(Get-Content $vjCallsFile -ErrorAction SilentlyContinue).Count
    $vfMax = Run-Engine @("--profile", $vjProfile, "vjoy", "configure-vjoy", "--max", "--dry-run", "--text")
    Assert-Match $vfMax.Output "vJoyConfig 1 -f -a x y z rx ry rz sl0 sl1 -b 128 -p 4" "configure-vjoy --max must set 8 axes, 128 buttons and 4 hats."
    Assert-Match $vfMax.Output "Windows must restart" "configure-vjoy --max must say Windows must restart."
    Assert-True (@(Get-Content $vjCallsFile -ErrorAction SilentlyContinue).Count -eq $vfCalls) "configure-vjoy --max --dry-run must run nothing."
    $vfRemove = Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "remove", "--game", "DCS", "--id", "f1", "--text")
    Assert-Match $vfRemove.Output "its virtual outputs are free again" "functions remove must free the outputs."
    $vfEmpty = (Run-Engine @("--profile", $vjProfile, "vjoy", "functions", "list")).Output | ConvertFrom-Json
    Assert-True (@($vfEmpty.Functions).Count -eq 0) "No function may be left."

    # Back to native: the export writes the physical devices again.
    $vjNative = Run-Engine @("--profile", $vjProfile, "vjoy", "mode", "DCS", "native", "--text")
    Assert-True ($vjNative.ExitCode -eq 0) "vjoy mode DCS native failed."
    $vjNativeSaved = Join-Path $vjDir "saved-native"
    New-Item -ItemType Directory -Force -Path $vjNativeSaved | Out-Null
    Run-Engine @("--profile", $vjProfile, "dcs", "export", $vjNativeSaved, "--text") | Out-Null
    $vjNativeFiles = @(Get-ChildItem -Path (Join-Path $vjNativeSaved "Config\Input\F-16C_50\joystick") -Filter "*.diff.lua" | Select-Object -ExpandProperty Name)
    Assert-True (($vjNativeFiles | Where-Object { $_ -like "VJ Stick*" }).Count -eq 1 -and ($vjNativeFiles | Where-Object { $_ -like "vJoy*" }).Count -eq 0) "In native mode the export must write the physical devices. Files: $($vjNativeFiles -join ', ')"
}
finally {
    $env:JOYPRO_VJOY_FAKE = $vjPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $vjPrevious.Devices
    $env:JOYPRO_DATA_DIR = $vjPrevious.Data
    $env:JOYPRO_INSTALL_DETECTION = $vjPrevious.Detection
    $env:JOYPRO_VJOY_DRY = $vjPrevious.Dry
}
