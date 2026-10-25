# DCS import of a device Windows gave new instance GUIDs over time (dot-sourced by engine_smoke_test.ps1; Run-Engine,
# Assert-*, $tempDir). DCS keeps one "<device> {GUID}.diff.lua" per GUID but loads only the attached one's; JoyPro's
# export comment (JPK) names the relation. A synthetic Saved Games folder: "Smoke Tartarus" under three GUIDs (01 old,
# 02 newest, 03 oldest), three aircraft, a modifier on the old GUID, a keyboard file and an unrelated stick.
#   - default (same-name devices as one): one relation per function, no "__N", the older copies of an aircraft skipped
#     and counted, the modifier moved to the live GUID; the scan reports the same.
#   - the connected GUID is the live one (JOYPRO_FAKE_DEVICES); without a connected one the newest file.
#   - two of them connected at once are twins: kept apart, nothing skipped.
#   - --merge-same-name false: every GUID its own device, but one relation per GUID (no copy per aircraft).
#   - --only-connected true: the files of devices that are not connected are skipped and counted.
#   - relations merge-copies repairs a profile an older import copied (dry run first, one undo step).
$dcUtf8 = New-Object System.Text.UTF8Encoding($false)
$dcRoot = Join-Path $tempDir "dcs-device-copies"
$dcSaved = Join-Path $dcRoot "saved"
$dcG1 = "11111111-0000-0000-0000-000000000001"
$dcG2 = "11111111-0000-0000-0000-000000000002"
$dcG3 = "11111111-0000-0000-0000-000000000003"
$dcOther = "Smoke Other Stick {22222222-0000-0000-0000-000000000009}"
function New-DcDiff([string[]]$Entries) {
    return "local diff = {`n  [`"keyDiffs`"] = {`n" + ($Entries -join "") + "  },`n}`nreturn diff`n"
}
function New-DcEntry([string]$Id, [string]$Key, [string]$Jpk, [string]$Reformer = "") {
    $ref = if ($Reformer) { "          [`"reformers`"] = { [1] = `"" + $Reformer + "`", },`n" } else { "" }
    return "    [`"" + $Id + "`"] = {`n      [`"added`"] = {`n        [1] = {`n          [`"key`"] = `"" + $Key + "`",`n          [`"JPK`"] = `"" + $Jpk + "`",`n" + $ref + "        },`n      },`n      [`"name`"] = `"" + $Jpk + "`",`n    },`n"
}
function Write-DcFile([string]$Plane, [string]$Folder, [string]$Device, [string]$Text, [datetime]$When) {
    $dir = Join-Path $dcSaved ("Config\Input\" + $Plane + "\" + $Folder)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir ($Device + ".diff.lua")
    [System.IO.File]::WriteAllText($file, $Text, $dcUtf8)
    [System.IO.File]::SetLastWriteTimeUtc($file, $When)
}
$dcOld = [datetime]::new(2021, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$dcNew = [datetime]::new(2022, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$dcOldest = [datetime]::new(2020, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$dcPrev = New-DcEntry "d_prev" "JOY_BTN12" "Kneeboard Prev"
$dcNext = New-DcEntry "d_next" "JOY_BTN13" "Kneeboard Next"
$dcMod = New-DcEntry "d_mod" "JOY_BTN1" "Mod Thing" "Shift20"
foreach ($plane in @("F-16C_50", "F-14", "A-10C_2")) {
    if ($plane -ne "A-10C_2") {
        # 01 (old, with a bind removed since) and 02 (newest, the live one).
        Write-DcFile $plane "joystick" ("Smoke Tartarus {" + $dcG1 + "}") (New-DcDiff @($dcPrev, (New-DcEntry "d_gone" "JOY_BTN5" "Old Only"))) $dcOld
        Write-DcFile $plane "joystick" ("Smoke Tartarus {" + $dcG2 + "}") (New-DcDiff @($dcPrev, $dcNext, $dcMod)) $dcNew
    } else {
        # No live file here: 01 (newer) is read, 03 (oldest) is an older copy.
        Write-DcFile $plane "joystick" ("Smoke Tartarus {" + $dcG1 + "}") (New-DcDiff @($dcPrev, $dcNext)) $dcOld
        Write-DcFile $plane "joystick" ("Smoke Tartarus {" + $dcG3 + "}") (New-DcDiff @($dcPrev, (New-DcEntry "d_gone3" "JOY_BTN6" "Older Only"))) $dcOldest
    }
}
Write-DcFile "F-16C_50" "joystick" $dcOther (New-DcDiff @((New-DcEntry "d_other" "JOY_BTN3" "Other Fire"))) $dcNew
Write-DcFile "F-16C_50" "keyboard" "Keyboard" (New-DcDiff @((New-DcEntry "d_kb" "K" "Kb Thing"))) $dcNew
[System.IO.File]::WriteAllText((Join-Path $dcSaved "Config\Input\F-16C_50\modifiers.lua"),
    "local modifiers = {`n  [`"Shift20`"] = {`n    [`"device`"] = `"Smoke Tartarus {" + $dcG1 + "}`",`n    [`"key`"] = `"JOY_BTN20`",`n    [`"switch`"] = false,`n  },`n}`nreturn modifiers`n", $dcUtf8)

$dcNone = Join-Path $dcRoot "none.tsv"
$dcLive2 = Join-Path $dcRoot "live2.tsv"
$dcTwins = Join-Path $dcRoot "twins.tsv"
[System.IO.File]::WriteAllText($dcNone, "# no devices`r`n", $dcUtf8)
[System.IO.File]::WriteAllText($dcLive2, ("Smoke Tartarus {" + $dcG2 + "}`t1532`t0244`tSmoke Tartarus`r`n"), $dcUtf8)
[System.IO.File]::WriteAllText($dcTwins, ("Smoke Tartarus {" + $dcG1 + "}`t1532`t0244`tSmoke Tartarus`r`nSmoke Tartarus {" + $dcG2 + "}`t1532`t0244`tSmoke Tartarus`r`n"), $dcUtf8)

function Get-DcRelations([string]$Profile) {
    $list = Run-Engine @("--profile", $Profile, "--remap-missing-devices", "false", "relations", "list", "--text")
    Assert-True ($list.ExitCode -eq 0) ("relations list failed: " + $list.Output)
    return $list.Output
}
function Count-Dc([string]$Text, [string]$Pattern) {
    return ([regex]::Matches($Text, $Pattern)).Count
}

$dcPreviousFake = $env:JOYPRO_FAKE_DEVICES
try {
    # ---- default, 02 connected
    $env:JOYPRO_FAKE_DEVICES = $dcLive2
    $dcScan = Run-Engine @("--remap-missing-devices", "false", "dcs", "scan", $dcSaved, "--include-keyboard", "true", "--text")
    Assert-True ($dcScan.ExitCode -eq 0) ("dcs scan of the device copies failed: " + $dcScan.Output)
    Assert-Match $dcScan.Output "Older Copies Of A Device Skipped: 6 bind\(s\) in 3 file\(s\)" "dcs scan must report the older copies of a device it skips."
    Assert-Match $dcScan.Output ("Smoke Tartarus \{" + $dcG1 + "\} -> Smoke Tartarus \{" + $dcG2 + "\} \(connected\)") "dcs scan must name the device read as one (connected GUID)."
    $dcProfile = Join-Path $dcRoot "default.pr0file"
    $dcImport = Run-Engine @("--profile", $dcProfile, "--remap-missing-devices", "false", "dcs", "import", $dcSaved, "--include-keyboard", "true", "--confirm", "false", "--text")
    Assert-True ($dcImport.ExitCode -eq 0) ("dcs import of the device copies failed: " + $dcImport.Output)
    Assert-Match $dcImport.Output "Older Copies Of A Device Skipped: 6 bind\(s\) in 3 file\(s\)" "dcs import must report the older copies of a device it skips."
    $dcRel = Get-DcRelations $dcProfile
    Assert-True ((Count-Dc $dcRel "(?m)^Kneeboard Prev") -eq 1) ("one relation for 'Kneeboard Prev' expected:`n" + $dcRel)
    Assert-True ((Count-Dc $dcRel "(?m)^Kneeboard Next") -eq 1) ("one relation for 'Kneeboard Next' expected:`n" + $dcRel)
    Assert-NotMatch $dcRel "__\d+ \|" "dcs import must not make __N copies for a device with a new ID."
    Assert-NotMatch $dcRel "Old Only|Older Only" "dcs import must skip the binds of the older copies of a device."
    Assert-Match $dcRel "(?m)^Kb Thing" "dcs import must keep the keyboard binds."
    Assert-Match $dcRel "(?m)^Other Fire" "dcs import must keep another device (not connected) by default."
    $dcShow = Run-Engine @("--profile", $dcProfile, "--remap-missing-devices", "false", "relations", "show", "Kneeboard Prev", "--text")
    Assert-Match $dcShow.Output "A-10C_2" "the merged relation must have the aircraft read from the old GUID."
    Assert-Match $dcShow.Output "F-14" "the merged relation must have every aircraft."
    Assert-Match $dcShow.Output ("Smoke Tartarus \{" + $dcG2 + "\}") "the merged relation must be on the live GUID."
    $dcBinds = Run-Engine @("--profile", $dcProfile, "--remap-missing-devices", "false", "binds", "show", "Mod Thing", "--text")
    Assert-Match $dcBinds.Output "Shift20" "the modifier must stay on the bind."
    Assert-NotMatch $dcBinds.Output $dcG1 "the modifier of the old GUID must move to the live GUID."

    # ---- nothing connected: the GUID of the newest file (02) is the live one
    $env:JOYPRO_FAKE_DEVICES = $dcNone
    $dcNoneScan = Run-Engine @("--remap-missing-devices", "false", "dcs", "scan", $dcSaved, "--text")
    Assert-Match $dcNoneScan.Output ("Smoke Tartarus \{" + $dcG3 + "\} -> Smoke Tartarus \{" + $dcG2 + "\} \(newest file\)") "without a connected device the newest file's GUID must be the live one."

    # ---- twins: 01 and 02 connected at once
    $env:JOYPRO_FAKE_DEVICES = $dcTwins
    $dcTwinProfile = Join-Path $dcRoot "twins.pr0file"
    $dcTwin = Run-Engine @("--profile", $dcTwinProfile, "--remap-missing-devices", "false", "dcs", "import", $dcSaved, "--confirm", "false", "--text")
    Assert-True ($dcTwin.ExitCode -eq 0) ("dcs import with twin devices failed: " + $dcTwin.Output)
    Assert-Match $dcTwin.Output "Older Copies Of A Device Skipped: 0 bind\(s\) in 0 file\(s\)" "twin devices must not be collapsed."
    Assert-Match $dcTwin.Output "Twin Devices Kept Apart: Smoke Tartarus \(2 connected\)" "the import must say it kept the twins apart."
    $dcTwinRel = Get-DcRelations $dcTwinProfile
    Assert-True ((Count-Dc $dcTwinRel "(?m)^Kneeboard Prev") -eq 3) ("twins: one 'Kneeboard Prev' per GUID expected (3):`n" + $dcTwinRel)

    # ---- --merge-same-name false: one relation per GUID, never one per aircraft
    $env:JOYPRO_FAKE_DEVICES = $dcLive2
    $dcApartProfile = Join-Path $dcRoot "apart.pr0file"
    $dcApart = Run-Engine @("--profile", $dcApartProfile, "--remap-missing-devices", "false", "dcs", "import", $dcSaved, "--merge-same-name", "false", "--confirm", "false", "--text")
    Assert-True ($dcApart.ExitCode -eq 0) ("dcs import --merge-same-name false failed: " + $dcApart.Output)
    $dcApartRel = Get-DcRelations $dcApartProfile
    Assert-True ((Count-Dc $dcApartRel "(?m)^Kneeboard Prev") -eq 3) ("--merge-same-name false: 3 'Kneeboard Prev' (one per GUID) expected:`n" + $dcApartRel)
    Assert-Match $dcApartRel "(?m)^Old Only" "--merge-same-name false must read every GUID's file."

    # ---- --only-connected true: the unrelated stick is skipped and counted, the merged Tartarus stays
    $dcOnlyProfile = Join-Path $dcRoot "only.pr0file"
    $dcOnlyScan = Run-Engine @("--remap-missing-devices", "false", "dcs", "scan", $dcSaved, "--only-connected", "true", "--text")
    Assert-Match $dcOnlyScan.Output "Not Connected Devices Skipped: 1 bind\(s\) in 1 file\(s\) \(Smoke Other Stick" "dcs scan --only-connected must report the device that is not connected."
    $dcOnly = Run-Engine @("--profile", $dcOnlyProfile, "--remap-missing-devices", "false", "dcs", "import", $dcSaved, "--only-connected", "true", "--include-keyboard", "true", "--confirm", "false", "--text")
    Assert-True ($dcOnly.ExitCode -eq 0) ("dcs import --only-connected failed: " + $dcOnly.Output)
    Assert-Match $dcOnly.Output "Not Connected Devices Skipped: 1 bind\(s\) in 1 file\(s\)" "dcs import --only-connected must count the skipped device."
    $dcOnlyRel = Get-DcRelations $dcOnlyProfile
    Assert-NotMatch $dcOnlyRel "(?m)^Other Fire" "--only-connected must skip the device that is not connected."
    Assert-True ((Count-Dc $dcOnlyRel "(?m)^Kneeboard Prev") -eq 1) "--only-connected must keep the connected device (its old GUIDs merged into it)."
    Assert-Match $dcOnlyRel "(?m)^Kb Thing" "--only-connected must keep the keyboard."

    # ---- relations merge-copies on a profile an older import copied (made here by hand: base + two copies)
    $dcRepair = Join-Path $dcRoot "repair.pr0file"
    $dcRepairSetup = @(
        @("Prev Page", ("Smoke Tartarus {" + $dcG2 + "}"), "DCS:d_prev:F-16C_50"),
        @("Prev Page__1", ("Smoke Tartarus {" + $dcG1 + "}"), "DCS:d_prev:F-14"),
        @("Prev Page__2", ("Smoke Tartarus {" + $dcG2 + "}"), "DCS:d_prev:A-10C_2"),
        @("Prev Page__3", ("Smoke Tartarus {" + $dcG2 + "}"), "DCS:d_prev2:AV8BNA")
    )
    foreach ($r in $dcRepairSetup) {
        $input = if ($r[0] -eq "Prev Page__3") { "JOY_BTN14" } else { "JOY_BTN12" }
        $add = Run-Engine @("--profile", $dcRepair, "--remap-missing-devices", "false", "relations", "add", $r[0], "--button", "--node", $r[2], "--device", $r[1], "--input", $input, "--text")
        Assert-True ($add.ExitCode -eq 0) ("relations add " + $r[0] + " failed: " + $add.Output)
    }
    $dcBefore = [System.IO.File]::ReadAllBytes($dcRepair)
    $dcDry = Run-Engine @("--profile", $dcRepair, "--remap-missing-devices", "false", "relations", "merge-copies", "--dry-run", "--text")
    Assert-True ($dcDry.ExitCode -eq 0) ("relations merge-copies --dry-run failed: " + $dcDry.Output)
    Assert-Match $dcDry.Output "Families: 1 \(dry run" "merge-copies --dry-run must find the copy family."
    Assert-Match $dcDry.Output "copies=2 \| aircraft=3" "merge-copies must merge the two copies with the same input (another GUID of the same device too), not the one on another input."
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$dcBefore, [byte[]][System.IO.File]::ReadAllBytes($dcRepair))) "merge-copies --dry-run must not change the profile."
    $dcMerge = Run-Engine @("--profile", $dcRepair, "--remap-missing-devices", "false", "relations", "merge-copies", "--text")
    Assert-True ($dcMerge.ExitCode -eq 0) ("relations merge-copies failed: " + $dcMerge.Output)
    $dcRepairRel = Get-DcRelations $dcRepair
    Assert-True ((Count-Dc $dcRepairRel "(?m)^Prev Page") -eq 2) ("merge-copies: 'Prev Page' and the copy on another input expected:`n" + $dcRepairRel)
    Assert-Match $dcRepairRel "(?m)^Prev Page__3" "merge-copies must keep a copy with another input."
    $dcRepairShow = Run-Engine @("--profile", $dcRepair, "--remap-missing-devices", "false", "relations", "show", "Prev Page", "--text")
    foreach ($plane in @("F-16C_50", "F-14", "A-10C_2")) { Assert-Match $dcRepairShow.Output $plane ("merge-copies must keep " + $plane + " in the merged relation.") }
    $dcHistory = Run-Engine @("--profile", $dcRepair, "profile", "history", "list", "--text")
    Assert-Match $dcHistory.Output "Merging copied relations" "merge-copies must be one profile version with its note."
} finally {
    $env:JOYPRO_FAKE_DEVICES = $dcPreviousFake
}
