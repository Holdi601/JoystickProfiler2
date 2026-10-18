# Replace a device (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineDir).
# Fake connected devices (JOYPRO_FAKE_DEVICES): the same throttle under a new instance GUID (new Windows install),
# one of two identical pedals, an unrelated stick, and a VKB grip that replaces a Warthog stick.
#   - devices auto-remap: same product + new GUID only, unique pairs only; never another product.
#   - (the load correction of profiles saved with another device list, --remap-missing-devices, uses the same rules)
#   - devices replace: same input ids, by layout from the device catalog (trigger -> trigger, weapon release ->
#     weapon release, TMS up -> TMS up), modifiers follow, inputs that do not fit stay, merge choices.
$drDir = Join-Path $tempDir "device-replace"
New-Item -ItemType Directory -Force -Path $drDir | Out-Null
$drUtf8 = New-Object System.Text.UTF8Encoding $false
# The modifier separator of a bind (kept out of the file: Windows PowerShell reads scripts without a BOM as ANSI).
$drS = [string][char]0x00A7
$drOldThrottle = "Throttle - HOTAS Warthog {CCCCCCCC-0000-0000-0000-000000000001}"
$drNewThrottle = "Throttle - HOTAS Warthog {CCCCCCCC-0000-0000-0000-000000000009}"
$drOldPedalA = "Twin Pedals {EEEEEEEE-0000-0000-0000-000000000001}"
$drOldPedalB = "Twin Pedals {EEEEEEEE-0000-0000-0000-000000000002}"
$drNewPedal = "Twin Pedals {EEEEEEEE-0000-0000-0000-000000000009}"
$drLonely = "Lonely Stick {FFFFFFFF-0000-0000-0000-000000000001}"
$drOther = "Other Stick {FFFFFFFF-0000-0000-0000-000000000009}"
$drWarthog = "Joystick - HOTAS Warthog {AAAAAAAA-0000-0000-0000-000000000001}"
$drVkb = "VKB-Sim Gunfighter Modern Combat Pro Twist {BBBBBBBB-0000-0000-0000-000000000001}"
$drDevices = Join-Path $drDir "devices.tsv"
[System.IO.File]::WriteAllText($drDevices,
    ($drNewThrottle + "`t044F`t0404`tThrottle - HOTAS Warthog`tX,Y,Z,RZ,SLIDER1`t32`t1`r`n") +
    ($drNewPedal + "`t1234`t5678`tTwin Pedals`tX,Y,RZ`t0`t0`r`n") +
    ($drOther + "`t1111`t2222`tOther Stick`tX,Y`t12`t1`r`n") +
    ($drVkb + "`t231D`t0125`tVKB-Sim Gunfighter Modern Combat Pro Twist`tX,Y,Z,RX,RY,RZ,SLIDER1`t128`t2`r`n"), $drUtf8)

$drPreviousFake = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = $drDevices
try {
    $drProfile = Join-Path $drDir "replace.pr0file"
    $drSetup = @(
        @("Thr Gear", "--button", $drOldThrottle, "JOY_BTN3"),
        @("Thr Power", "--axis", $drOldThrottle, "JOY_Z"),
        @("Pedal Yaw", "--axis", $drOldPedalA, "JOY_RZ"),
        @("Pedal Yaw B", "--axis", $drOldPedalB, "JOY_RZ"),
        @("Lonely Fire", "--button", $drLonely, "JOY_BTN1"),
        @("WH Fire", "--button", $drWarthog, "JOY_BTN1"),
        @("WH Release", "--button", $drWarthog, "JOY_BTN2"),
        @("WH TMS Up", "--button", $drWarthog, "JOY_BTN7"),
        @("WH Roll", "--axis", $drWarthog, "JOY_X"),
        @("WH Odd", "--button", $drWarthog, "JOY_BTN200"),
        @("VKB Own", "--button", $drVkb, "JOY_BTN14")
    )
    $drIndex = 0
    foreach ($drRelation in $drSetup) {
        $drIndex++
        $drAdd = Run-Engine @("--profile", $drProfile, "--remap-missing-devices", "false", "relations", "add", $drRelation[0], $drRelation[1], "--node", ("DCS:dr_" + $drIndex + ":F-16C_50"), "--device", $drRelation[2], "--input", $drRelation[3], "--text")
        Assert-True ($drAdd.ExitCode -eq 0) ("relations add " + $drRelation[0] + " failed: " + $drAdd.Output)
    }
    $drMod = Run-Engine @("--profile", $drProfile, "--remap-missing-devices", "false", "binds", "set", "WH Roll", "--modifier", ("Pinky" + $drS + $drWarthog + $drS + "JOY_BTN1" + $drS + "False"), "--text")
    Assert-True ($drMod.ExitCode -eq 0) ("binds set with a modifier on the Warthog failed: " + $drMod.Output)
    $drModThr = Run-Engine @("--profile", $drProfile, "--remap-missing-devices", "false", "binds", "set", "Thr Power", "--modifier", ("ThrShift" + $drS + $drOldThrottle + $drS + "JOY_BTN5" + $drS + "False"), "--text")
    Assert-True ($drModThr.ExitCode -eq 0) ("binds set with a modifier on the throttle failed: " + $drModThr.Output)

    # auto-remap preview: the throttle only.
    $drBefore = [System.IO.File]::ReadAllBytes($drProfile)
    $drDry = Run-Engine @("--profile", $drProfile, "devices", "auto-remap", "--dry-run")
    Assert-True ($drDry.ExitCode -eq 0) ("devices auto-remap --dry-run failed: " + $drDry.Output)
    $drDryJson = ($drDry.Output -split "`r?`n" | Where-Object { $_ -notmatch '^Auto-saved' }) -join "`n" | ConvertFrom-Json
    Assert-True (@($drDryJson.Remapped).Count -eq 1 -and $drDryJson.Remapped[0].OldDevice -eq $drOldThrottle -and $drDryJson.Remapped[0].NewDevice -eq $drNewThrottle) ("auto-remap should move only the throttle with the new GUID: " + $drDry.Output)
    Assert-True (@($drDryJson.Skipped | Where-Object { $_.OldDevice -eq $drOldPedalA -or $_.OldDevice -eq $drOldPedalB }).Count -eq 2) ("two old pedals and one connected must be left for Replace device: " + $drDry.Output)
    Assert-True (@($drDryJson.Remapped | Where-Object { $_.NewDevice -eq $drOther }).Count -eq 0) "auto-remap must never pick another product."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($drProfile)) -eq [Convert]::ToBase64String($drBefore)) "devices auto-remap --dry-run must not save the profile."

    # auto-remap applied: the throttle and its modifier move, the inputs stay; a second run finds nothing and saves nothing.
    $drApply = Run-Engine @("--profile", $drProfile, "devices", "auto-remap", "--text")
    Assert-True ($drApply.ExitCode -eq 0) ("devices auto-remap failed: " + $drApply.Output)
    Assert-Match $drApply.Output "Remapped: 1 \(applied\)" ("devices auto-remap should apply the throttle: " + $drApply.Output)
    $drBinds = Run-Engine @("--profile", $drProfile, "--load-corrections", "false", "binds", "list", "--text")
    Assert-Match $drBinds.Output ("Thr Gear \| type=button \| device=" + [regex]::Escape($drNewThrottle) + " \| input=JOY_BTN3") ("auto-remap should keep the input: " + $drBinds.Output)
    Assert-Match $drBinds.Output ([regex]::Escape("ThrShift" + $drS + $drNewThrottle + $drS + "JOY_BTN5")) ("auto-remap should move the throttle's modifier: " + $drBinds.Output)
    $drAfterApply = [System.IO.File]::ReadAllBytes($drProfile)
    $drAgain = Run-Engine @("--profile", $drProfile, "devices", "auto-remap", "--text")
    Assert-Match $drAgain.Output "Remapped: 0" ("a second auto-remap should find nothing: " + $drAgain.Output)
    Assert-NotMatch $drAgain.Output "Auto-saved profile" "an auto-remap that changes nothing must not save."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($drProfile)) -eq [Convert]::ToBase64String($drAfterApply)) "an auto-remap that changes nothing must leave the profile file as it is."

    # Replace by layout: Warthog stick -> VKB grip.
    $drPreview = Run-Engine @("--profile", $drProfile, "devices", "replace", "Joystick - HOTAS Warthog", "VKB-Sim Gunfighter Modern Combat Pro Twist", "--dry-run")
    Assert-True ($drPreview.ExitCode -eq 0) ("devices replace --dry-run failed: " + $drPreview.Output)
    $drPlan = ($drPreview.Output -split "`r?`n" | Where-Object { $_ -notmatch '^Auto-saved' }) -join "`n" | ConvertFrom-Json
    Assert-True ($drPlan.Mode -eq "layout" -and $drPlan.NeedsMergeChoice -and $drPlan.TargetBinds -eq 1) ("the preview should map by layout and ask how to merge: " + $drPreview.Output)
    $drMap = @{}
    foreach ($m in $drPlan.Mappings) { $drMap[$m.OldInput] = $m }
    Assert-True ($drMap["JOY_BTN1"].NewInput -eq "JOY_BTN1" -and $drMap["JOY_BTN1"].Method -eq "same part") "trigger stage 1 should go to the VKB trigger stage 1."
    Assert-True ($drMap["JOY_BTN2"].NewInput -eq "JOY_BTN24") ("the weapon release should go to the VKB red button: " + ($drMap["JOY_BTN2"] | ConvertTo-Json))
    Assert-True ($drMap["JOY_BTN7"].NewInput -eq "JOY_BTN14" -and @($drMap["JOY_BTN7"].TargetRelations) -contains "VKB Own") ("TMS up should go to the VKB TMS up and show the bind already there: " + ($drMap["JOY_BTN7"] | ConvertTo-Json))
    Assert-True ($drMap["JOY_X"].NewInput -eq "JOY_X") "roll should stay roll."
    Assert-True (-not $drMap["JOY_BTN200"].NewInput) "an input the new device does not have must stay unmapped."
    $drNoMerge = Run-Engine @("--profile", $drProfile, "devices", "replace", $drWarthog, $drVkb, "--text")
    Assert-True ($drNoMerge.ExitCode -ne 0) "devices replace onto a device with binds must ask how to merge."
    Assert-Match $drNoMerge.Output "--merge add" "the refused replace should name the merge choices."

    $drKeepProfile = Join-Path $drDir "keep.pr0file"
    Copy-Item $drProfile $drKeepProfile
    $drKeep = Run-Engine @("--profile", $drKeepProfile, "devices", "replace", $drWarthog, $drVkb, "--merge", "keep", "--text")
    Assert-True ($drKeep.ExitCode -eq 0) ("devices replace --merge keep failed: " + $drKeep.Output)
    $drKeepBinds = Run-Engine @("--profile", $drKeepProfile, "--load-corrections", "false", "binds", "list", "--text")
    Assert-Match $drKeepBinds.Output ("WH TMS Up \| type=button \| device=" + [regex]::Escape($drWarthog)) "merge keep: an old bind on an input the new device uses stays on the old device."
    Assert-Match $drKeepBinds.Output ("VKB Own \| type=button \| device=" + [regex]::Escape($drVkb) + " \| input=JOY_BTN14") "merge keep: the new device's bind stays."

    $drReplace = Run-Engine @("--profile", $drProfile, "devices", "replace", $drWarthog, $drVkb, "--merge", "replace", "--map", "JOY_BTN2=JOY_BTN5", "--text")
    Assert-True ($drReplace.ExitCode -eq 0) ("devices replace --merge replace failed: " + $drReplace.Output)
    $drAfter = Run-Engine @("--profile", $drProfile, "--load-corrections", "false", "binds", "list", "--text")
    Assert-Match $drAfter.Output ("WH Fire \| type=button \| device=" + [regex]::Escape($drVkb) + " \| input=JOY_BTN1") ("replace should move the trigger bind: " + $drAfter.Output)
    Assert-Match $drAfter.Output ("WH Release \| type=button \| device=" + [regex]::Escape($drVkb) + " \| input=JOY_BTN5") "--map should win over the layout."
    Assert-Match $drAfter.Output ("WH TMS Up \| type=button \| device=" + [regex]::Escape($drVkb) + " \| input=JOY_BTN14") "replace: the old device's bind wins."
    Assert-NotMatch $drAfter.Output "VKB Own \| type=button \| device=" "merge replace should remove the new device's overlapping bind."
    Assert-Match $drAfter.Output ("WH Odd \| type=button \| device=" + [regex]::Escape($drWarthog)) "an unmapped input stays on the old device."
    Assert-Match $drAfter.Output ([regex]::Escape("Pinky" + $drS + $drVkb + $drS + "JOY_BTN1")) ("the modifier should follow its input: " + $drAfter.Output)

    # Same product: same input ids.
    $drSame = Run-Engine @("--profile", $drProfile, "devices", "replace", $drOldPedalA, $drNewPedal, "--dry-run", "--text")
    Assert-Match $drSame.Output "Mode: same" ("two pedals of the same product should keep their input ids: " + $drSame.Output)
    Assert-Match $drSame.Output "JOY_RZ .*-> JOY_RZ" "the same product maps the same input."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $drPreviousFake
}
