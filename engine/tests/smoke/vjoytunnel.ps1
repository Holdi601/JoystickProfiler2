# vJoy devices in imports (docs/virtual-joystick.md "The vJoy device JoyPro tunnels into"). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir). Never touches the PC's vJoy: JOYPRO_VJOY_FAKE (vjoy.txt with
# two configured devices) and JOYPRO_FAKE_DEVICES (vJoy devices 1 and 2, told apart by their HID collection &col01 /
# &col02). Checks:
#   - without --vjoy-tunnel both vJoy devices are imported like any other device (nothing excludes vJoy by default);
#   - --vjoy-tunnel 1 leaves out only vJoy device 1, --vjoy-tunnel 2 only device 2; the scan does the same;
#   - a number outside 1-16 is refused; "none" leaves nothing out.
$vtDir = Join-Path $tempDir "vjoy-tunnel"
$vtFake = Join-Path $vtDir "fake"
$vtSaved = Join-Path $vtDir "saved"
$vtUtf8 = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force -Path $vtFake | Out-Null
$vtVJoy1 = "vJoy Device {7A000001-B53F-11F1-8002-444553540000}"
$vtVJoy2 = "vJoy Device {7A000002-B53F-11F1-8002-444553540000}"
$vtStick = "VT Stick {7A000003-0000-0000-0000-000000000003}"
[System.IO.File]::WriteAllText((Join-Path $vtFake "vjoy.txt"),
    "installed=2.1.9.1`r`ndevice=1 axes=x,y buttons=32 povs=0 discrete=0`r`ndevice=2 axes=x,y buttons=32 povs=0 discrete=0`r`n", $vtUtf8)
$vtDevices = Join-Path $vtDir "devices.tsv"
[System.IO.File]::WriteAllText($vtDevices,
    ($vtVJoy1 + "`t1234`tBEAD`tvJoy - Virtual Joystick`tX,Y`t32`t0`t\\?\hid#hidclass&col01#1&2d595ca7&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}`r`n") +
    ($vtVJoy2 + "`t1234`tBEAD`tvJoy - Virtual Joystick`tX,Y`t32`t0`t\\?\hid#hidclass&col02#1&2d595ca7&1&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}`r`n") +
    ($vtStick + "`t3344`t80CB`tVT Stick`tX,Y`t32`t1`r`n"), $vtUtf8)
function Write-VtDiff([string]$Device, [string]$Id, [string]$Key) {
    $path = Join-Path $vtSaved ("Config\Input\SmokeVJ\joystick\" + $Device + ".diff.lua")
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, "local diff = {`n  [`"keyDiffs`"] = {`n    [`"" + $Id + "`"] = {`n      [`"name`"] = `"" + $Id + "`",`n      [`"added`"] = {`n        [1] = { [`"key`"] = `"" + $Key + "`", },`n      },`n    },`n  },`n}`nreturn diff`n", $vtUtf8)
}
Write-VtDiff $vtVJoy1 "vt_from_vjoy1" "JOY_BTN1"
Write-VtDiff $vtVJoy2 "vt_from_vjoy2" "JOY_BTN2"
Write-VtDiff $vtStick "vt_from_stick" "JOY_BTN3"

$vtPrevious = @{ Fake = $env:JOYPRO_VJOY_FAKE; Devices = $env:JOYPRO_FAKE_DEVICES }
$env:JOYPRO_VJOY_FAKE = $vtFake
$env:JOYPRO_FAKE_DEVICES = $vtDevices

function Get-VtUpper([string[]]$Names) { return @($Names | ForEach-Object { $_.ToUpperInvariant() }) }

function Get-VtImportedDevices([string]$Name, [string[]]$Extra) {
    $profileFile = Join-Path $vtDir ($Name + ".pr0file")
    $run = Run-Engine (@("--profile", $profileFile, "--remap-missing-devices", "false", "dcs", "import", $vtSaved, "--confirm", "false", "--text") + $Extra)
    Assert-True ($run.ExitCode -eq 0) ("dcs import (" + $Name + ") failed: " + $run.Output)
    $list = Run-Engine @("--profile", $profileFile, "--remap-missing-devices", "false", "binds", "list")
    Assert-True ($list.ExitCode -eq 0) ("binds list (" + $Name + ") failed: " + $list.Output)
    # (DCS writes the GUID's last parts in lower case; compared without case.)
    return @(($list.Output | ConvertFrom-Json).Binds | ForEach-Object { $_.Joystick.ToUpperInvariant() } | Sort-Object -Unique)
}

try {
    $vtAll = Get-VtImportedDevices "all" @()
    Assert-SetEquivalent $vtAll (Get-VtUpper @($vtVJoy1, $vtVJoy2, $vtStick)) "without --vjoy-tunnel every vJoy device is imported like any other device."
    $vtNone = Get-VtImportedDevices "none" @("--vjoy-tunnel", "none")
    Assert-SetEquivalent $vtNone (Get-VtUpper @($vtVJoy1, $vtVJoy2, $vtStick)) "--vjoy-tunnel none leaves nothing out."
    $vtOne = Get-VtImportedDevices "tunnel1" @("--vjoy-tunnel", "1")
    Assert-SetEquivalent $vtOne (Get-VtUpper @($vtVJoy2, $vtStick)) "--vjoy-tunnel 1 must leave out only vJoy device 1."
    $vtTwo = Get-VtImportedDevices "tunnel2" @("--vjoy-tunnel", "2")
    Assert-SetEquivalent $vtTwo (Get-VtUpper @($vtVJoy1, $vtStick)) "--vjoy-tunnel 2 must leave out only vJoy device 2."

    $vtScan = Run-Engine @("dcs", "scan", $vtSaved, "--vjoy-tunnel", "1", "--text")
    Assert-True ($vtScan.ExitCode -eq 0) ("dcs scan with --vjoy-tunnel failed: " + $vtScan.Output)
    Assert-True ($vtScan.Output.ToUpperInvariant().Contains($vtVJoy1.ToUpperInvariant()) -and $vtScan.Output -match "(?m)^Ignored Devices:") ("the scan must list vJoy device 1 as ignored: " + $vtScan.Output)

    $vtBad = Run-Engine @("dcs", "scan", $vtSaved, "--vjoy-tunnel", "17", "--text")
    Assert-True ($vtBad.ExitCode -ne 0 -and $vtBad.Output -match "vjoy-tunnel needs a vJoy device number") ("--vjoy-tunnel 17 must be refused: " + $vtBad.Output)
}
finally {
    $env:JOYPRO_VJOY_FAKE = $vtPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $vtPrevious.Devices
}
