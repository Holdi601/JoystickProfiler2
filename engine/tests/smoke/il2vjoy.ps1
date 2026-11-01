# IL-2 import and vJoy devices (docs/virtual-joystick.md "vJoy devices in imports"). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir). Never touches the PC's vJoy: JOYPRO_VJOY_FAKE (two configured vJoy devices) and
# JOYPRO_FAKE_DEVICES (vJoy devices 1 and 2 told apart by their HID collection, and a real stick). The game folder's
# devices.txt holds all three (IL-2's GUID form: the last 8 bytes reversed). Checks:
#   - an ignored device named without GUID ("vJoy Device") leaves out every vJoy device of that name, not one of them;
#   - --vjoy-tunnel 1 leaves out vJoy device 1 only (as in the DCS import);
#   - a device token that is not connected and matches several devices never picks the tunnel device, prefers a real
#     device over vJoy unless it names vJoy, and the message names the device, not a raw GUID.
$ivDir = Join-Path $tempDir "il2-vjoy"
$ivFake = Join-Path $ivDir "fake"
$ivRoot = Join-Path $ivDir "il2"
$ivInput = Join-Path $ivRoot "data\input"
$ivUtf8 = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force -Path $ivFake, $ivInput | Out-Null
$ivVJoy1 = "vJoy Device {7A000001-B53F-11F1-8002-444553540000}"
$ivVJoy2 = "vJoy Device {7A000002-B53F-11F1-8002-444553540000}"
$ivStick = "IV Stick {7A000003-1111-2222-8003-444553540000}"
[System.IO.File]::WriteAllText((Join-Path $ivFake "vjoy.txt"),
    "installed=2.1.9.1`r`ndevice=1 axes=x,y buttons=32 povs=0 discrete=0`r`ndevice=2 axes=x,y buttons=32 povs=0 discrete=0`r`n", $ivUtf8)
$ivDevices = Join-Path $ivDir "devices.tsv"
[System.IO.File]::WriteAllText($ivDevices,
    ($ivVJoy1 + "`t1234`tBEAD`tvJoy - Virtual Joystick`tX,Y`t32`t0`t\\?\hid#hidclass&col01#1&2d595ca7&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}`r`n") +
    ($ivVJoy2 + "`t1234`tBEAD`tvJoy - Virtual Joystick`tX,Y`t32`t0`t\\?\hid#hidclass&col02#1&2d595ca7&1&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}`r`n") +
    ($ivStick + "`t3344`t80CC`tIV Stick`tX,Y`t32`t1`r`n"), $ivUtf8)
[System.IO.File]::WriteAllText((Join-Path $ivInput "devices.txt"),
    "configId,guid,model|`r`n0,%227A000001-B53F-11F1-0000545345440280%22,vJoy%20Device|`r`n1,%227A000002-B53F-11F1-0000545345440280%22,vJoy%20Device|`r`n2,%227A000003-1111-2222-0000545345440380%22,IV%20Stick|`r`n", $ivUtf8)
[System.IO.File]::WriteAllText((Join-Path $ivInput "global.actions"),
    "// Input map preset.`r`n&actions=action,command,invert|`r`ngear, joy0_b0, 0|`r`nflaps_up, joy1_b1, 0|`r`nrpc_fire, joy2_b2, 0|", $ivUtf8)

$ivPrevious = @{ Fake = $env:JOYPRO_VJOY_FAKE; Devices = $env:JOYPRO_FAKE_DEVICES }
$env:JOYPRO_VJOY_FAKE = $ivFake
$env:JOYPRO_FAKE_DEVICES = $ivDevices

function Get-IvImport([string]$Name, [string[]]$Extra) {
    $profileFile = Join-Path $ivDir ($Name + ".pr0file")
    $run = Run-Engine (@("--profile", $profileFile, "--remap-missing-devices", "false", "il2", "import", $ivRoot, "--confirm", "false", "--text") + $Extra)
    Assert-True ($run.ExitCode -eq 0) ("il2 import (" + $Name + ") failed: " + $run.Output)
    $list = Run-Engine @("--profile", $profileFile, "--remap-missing-devices", "false", "binds", "list")
    Assert-True ($list.ExitCode -eq 0) ("binds list (" + $Name + ") failed: " + $list.Output)
    $devices = @(($list.Output | ConvertFrom-Json).Binds | ForEach-Object { $_.Joystick.ToUpperInvariant() } | Sort-Object -Unique)
    return [PSCustomObject]@{ Output = $run.Output; Devices = $devices }
}

function Get-IvUpper([string[]]$Names) { return @($Names | ForEach-Object { $_.ToUpperInvariant() }) }

try {
    $ivAll = Get-IvImport "all" @()
    Assert-SetEquivalent $ivAll.Devices (Get-IvUpper @($ivVJoy1, $ivVJoy2, $ivStick)) "il2 import without options must read every device, vJoy included."

    # An ignored device named without GUID: every device of that name stays out (it was one picked of the two).
    $ivIgnore = Get-IvImport "ignore-vjoy" @("--ignore-device", "vJoy Device")
    Assert-SetEquivalent $ivIgnore.Devices (Get-IvUpper @($ivStick)) ("il2 import --ignore-device 'vJoy Device' must leave out both vJoy devices: " + $ivIgnore.Output)
    Assert-NotMatch $ivIgnore.Output "Auto-selected '[^']*\{" ("the device choice must name the device, not a raw GUID: " + $ivIgnore.Output)

    # The tunnel device is left out (native game), the other vJoy device stays a normal device.
    $ivTunnel = Get-IvImport "tunnel1" @("--vjoy-tunnel", "1")
    Assert-SetEquivalent $ivTunnel.Devices (Get-IvUpper @($ivVJoy2, $ivStick)) ("il2 import --vjoy-tunnel 1 must leave out only vJoy device 1: " + $ivTunnel.Output)

    # A vJoy device that is not connected (another GUID) among several vJoy devices: never the tunnel device.
    $ivPick = Get-IvImport "pick-vjoy" @("--device", "vJoy Device {7A000009-B53F-11F1-8002-444553540000}", "--vjoy-tunnel", "1")
    Assert-SetEquivalent $ivPick.Devices (Get-IvUpper @($ivVJoy2)) ("il2 import --device <missing vJoy> must pick vJoy device 2, never the tunnel device: " + $ivPick.Output)
    Assert-Match $ivPick.Output "vJoy device 2" ("the choice must name the vJoy device by its number: " + $ivPick.Output)
    Assert-NotMatch $ivPick.Output "(?i)selected '[^']*\{7A000002" ("the choice must not name the device by its raw GUID: " + $ivPick.Output)

    # A token that does not name vJoy prefers the real device over a vJoy device with a closer name.
    $ivReal = Get-IvImport "pick-real" @("--device", "Joy Device {7A0000AA-0000-0000-0000-000000000000}")
    Assert-SetEquivalent $ivReal.Devices (Get-IvUpper @($ivStick)) ("il2 import --device 'Joy Device {...}' must prefer the real device over vJoy: " + $ivReal.Output)
    # Same input twice: same choice.
    $ivReal2 = Get-IvImport "pick-real-2" @("--device", "Joy Device {7A0000AA-0000-0000-0000-000000000000}")
    Assert-SetEquivalent $ivReal2.Devices $ivReal.Devices "the device choice must be the same on every run."
}
finally {
    $env:JOYPRO_VJOY_FAKE = $ivPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $ivPrevious.Devices
}
