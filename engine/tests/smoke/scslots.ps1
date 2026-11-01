# Star Citizen joystick slots (2.5.1, the author's profile): one physical device under several names - the game's
# "name  {PIDVID}" in actionmaps.xml, JoyPro's connected instance "name {instance GUID}", a stale second entry of the same
# stick - takes one slot. Before, a connected stick the file had on js1 took a second slot under its instance name, the
# 8 slots ran out and devices with binds were left out unless --prefer-local-order false.
# Eight fake devices (JOYPRO_FAKE_DEVICES, products as DirectInput names them: JOYPRO_FAKE_SC_PRODUCTS=pidvid) in the
# startup order of JOYPRO_DI_ORDER_OVERRIDE; the stick and the throttle are js1/js2 of the file (and the stick a second
# time on js3); devices A and B are 1st and 2nd in the startup order and have binds.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $starCitizenDbCatalogPath).

$ssDir = Join-Path $tempDir "scslots"
$ssScDir = Join-Path $ssDir "sc\default"
New-Item -Path $ssScDir -ItemType Directory -Force | Out-Null
$ssUtf8 = New-Object System.Text.UTF8Encoding($false)
$ssPrevious = @{ Fake = $env:JOYPRO_FAKE_DEVICES; Products = $env:JOYPRO_FAKE_SC_PRODUCTS; Order = $env:JOYPRO_DI_ORDER_OVERRIDE }
$ssCatalogBackup = if (Test-Path $starCitizenDbCatalogPath) { [System.IO.File]::ReadAllBytes($starCitizenDbCatalogPath) } else { $null }

try {
    $ssDevices = @(
        @{ Name = "SC Dev A"; Guid = "000000a1"; Pid = 0x00A1 },
        @{ Name = "SC Dev B"; Guid = "000000a2"; Pid = 0x00A2 },
        @{ Name = "SC Dev C"; Guid = "000000a3"; Pid = 0x00A3 },
        @{ Name = "SC Dev D"; Guid = "000000a4"; Pid = 0x00A4 },
        @{ Name = "SC Stick"; Guid = "000000a5"; Pid = 0x00A5 },
        @{ Name = "SC Dev E"; Guid = "000000a6"; Pid = 0x00A6 },
        @{ Name = "SC Throttle"; Guid = "000000a7"; Pid = 0x00A7 },
        @{ Name = "SC Dev F"; Guid = "000000a8"; Pid = 0x00A8 })
    $ssVid = 0x3344
    $ssLines = @("# JoyPro device`tVID`tPID`tHID product string")
    $ssOrder = @()
    $ssPGuids = @{}
    foreach ($d in $ssDevices) {
        $d.Instance = $d.Name + " {" + $d.Guid.ToUpperInvariant() + "-0000-0000-0000-000000000000}"
        $d.Product = $d.Name + "  {" + ("{0:X4}{1:X4}" -f $d.Pid, $ssVid) + "-0000-0000-0000-504944564944}"
        $ssLines += ($d.Instance + "`t" + ("{0:X4}" -f $ssVid) + "`t" + ("{0:X4}" -f $d.Pid) + "`t" + $d.Name)
        $ssOrder += $d.Instance
        $ssPGuids[$d.Instance] = $d.Product
    }
    $ssFake = Join-Path $ssDir "devices.tsv"
    [System.IO.File]::WriteAllLines($ssFake, $ssLines, $ssUtf8)
    $env:JOYPRO_FAKE_DEVICES = $ssFake
    $env:JOYPRO_FAKE_SC_PRODUCTS = "pidvid"
    $env:JOYPRO_DI_ORDER_OVERRIDE = ($ssOrder -join ";")
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\roundtrip\starcitizen\catalog.xml") -Destination $starCitizenDbCatalogPath -Force

    $ssStick = $ssDevices[4]; $ssThrottle = $ssDevices[6]; $ssA = $ssDevices[0]; $ssB = $ssDevices[1]
    $ssActionmaps = @"
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard  {6F1D2B61-D5A0-11CF-BFC7-444553540000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="$($ssStick.Product)"/>
  <options type="joystick" instance="2" Product="$($ssThrottle.Product)"/>
  <options type="joystick" instance="3" Product="$($ssStick.Product)"/>
  <options type="joystick" instance="4"/>
  <options type="joystick" instance="5"/>
  <options type="joystick" instance="6"/>
  <options type="joystick" instance="7"/>
  <options type="joystick" instance="8"/>
  <modifiers />
 </ActionProfiles>
</ActionMaps>
"@
    $ssFile = Join-Path $ssScDir "actionmaps.xml"
    [System.IO.File]::WriteAllText($ssFile, $ssActionmaps, $ssUtf8)

    function New-SsBind([string]$Device, [string]$Button, [string]$Axis) {
        return @{ Joystick = $Device; JAxis = $Axis; JButton = $Button; AllReformers = @(); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
    }
    $ssDoc = @{
        Relations = @{
            "SS Pitch" = @{ NAME = "SS Pitch"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "spaceship_movement|v_pitch"; Game = "StarCitizen"; AIRCRAFT = @{ "StarCitizen" = $true } }) }
            "SS Yaw" = @{ NAME = "SS Yaw"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "spaceship_movement|v_yaw"; Game = "StarCitizen"; AIRCRAFT = @{ "StarCitizen" = $true } }) }
            "SS Target" = @{ NAME = "SS Target"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "spaceship_movement|v_target_cycle"; Game = "StarCitizen"; AIRCRAFT = @{ "StarCitizen" = $true } }) }
        }
        Binds = @{
            "SS Pitch" = (New-SsBind $ssStick.Instance "" "JOY_Y")
            "SS Yaw" = (New-SsBind $ssA.Instance "" "JOY_Z")
            "SS Target" = (New-SsBind $ssB.Instance "JOY_BTN5" "")
        }
        JoysticksPGuids = $ssPGuids
    }
    $ssProfile = Join-Path $ssDir "sc.pr0file"
    [System.IO.File]::WriteAllText($ssProfile, ($ssDoc | ConvertTo-Json -Depth 10), $ssUtf8)

    $ssExport = Run-Engine @("--profile", $ssProfile, "--load-corrections", "false", "starcitizen", "export", "--path", $ssScDir, "--text")
    Assert-True ($ssExport.ExitCode -eq 0) ("Star Citizen export for the slot check failed: " + $ssExport.Output)
    Assert-NotMatch $ssExport.Output "no free joystick slot|no joystick slot assigned" ("a device with binds got no joystick slot (one device under two names took two slots): " + $ssExport.Output)
    $ssWritten = [System.IO.File]::ReadAllText($ssFile)
    # v_pitch on the stick's y is the game's default on js1: the stick kept js1 (no rebind written for it).
    Assert-NotMatch $ssWritten '<action name="v_pitch">' ("the stick of js1 moved to another slot: " + $ssWritten)
    Assert-Match $ssWritten '<action name="v_yaw">\s*<rebind input="js[4-8]_z"/>' ("device A's bind is missing: " + $ssWritten)
    Assert-Match $ssWritten '<action name="v_target_cycle">\s*<rebind input="js[4-8]_button5"/>' ("device B's bind is missing: " + $ssWritten)
    $ssStickSlots = @([regex]::Matches($ssWritten, 'Product="' + [regex]::Escape($ssStick.Product) + '"')).Count
    # (2.5.1) The stale second entry of the stick (js3) leaves the file.
    Assert-True ($ssStickSlots -eq 1) ("the stick must keep one slot (another under its instance name, or the stale js3 left in the file): " + $ssWritten)
    Assert-NotMatch $ssWritten ([regex]::Escape($ssStick.Name + " {")) ("a slot was written with the stick's instance name: " + $ssWritten)

    # Clean + nuke with the slots the export plans (JoyPro names them by the connected instance, the file and
    # DirectInput by product): the written stick keeps only the profile's ship binds, the throttle and device C (connected, no
    # binds in the profile) lose their ship binds. Up to 2.5.1 no planned slot counted as connected, so clean, nuke and the
    # merge conflict released nothing on them (e2e 2026-11-01: Replace on my devices left two actions on vJoy's button 2).
    $ssCleanText = [System.IO.File]::ReadAllText($ssFile)
    $ssCleanText = $ssCleanText -replace '(<actionmap name="spaceship_movement">)', ('$1' + "`r`n   <action name=`"v_fire`">`r`n    <rebind input=`"js2_button7`"/>`r`n   </action>`r`n   <action name=`"v_boost`">`r`n    <rebind input=`"js1_button9`"/>`r`n   </action>`r`n   <action name=`"v_roll`">`r`n    <rebind input=`"js3_button6`"/>`r`n   </action>")
    Assert-Match $ssCleanText 'js2_button7' ("the clean check could not seed the file: " + $ssCleanText)
    [System.IO.File]::WriteAllText($ssFile, $ssCleanText, $ssUtf8)
    $ssClean = Run-Engine @("--profile", $ssProfile, "--load-corrections", "false", "starcitizen", "export", "--path", $ssScDir, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($ssClean.ExitCode -eq 0) ("Star Citizen clean + nuke export for the slot check failed: " + $ssClean.Output)
    $ssCleaned = [System.IO.File]::ReadAllText($ssFile)
    Assert-NotMatch $ssCleaned 'js1_button9' ("clean must release the other ship binds of the stick it writes (its slot counts as connected): " + $ssCleaned)
    Assert-NotMatch $ssCleaned 'js2_button7' ("nuke must release the ship binds of the connected throttle the profile does not use: " + $ssCleaned)
    Assert-NotMatch $ssCleaned 'js3_button6' ("nuke must release the ship binds of connected device C (its slot is named by the instance in the plan): " + $ssCleaned)
    Assert-Match $ssCleaned '<action name="v_yaw">\s*<rebind input="js[4-8]_z"/>' ("clean must keep device A's bind: " + $ssCleaned)
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $ssPrevious.Fake
    $env:JOYPRO_FAKE_SC_PRODUCTS = $ssPrevious.Products
    $env:JOYPRO_DI_ORDER_OVERRIDE = $ssPrevious.Order
    if ($null -ne $ssCatalogBackup) { [System.IO.File]::WriteAllBytes($starCitizenDbCatalogPath, $ssCatalogBackup) }
    elseif (Test-Path $starCitizenDbCatalogPath) { Remove-Item -Path $starCitizenDbCatalogPath -Force }
}
