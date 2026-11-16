# Kneeboard pages (engine layout kneeboard, docs/device-reference.md "Kneeboard"): the cheat sheets of each aircraft in the DCS kneeboard
# folders of fake Saved Games folders (DCS and DCS.openbeta) and in a folder of choice. Only JoyPro's own pictures are
# written or replaced; the pictures of others stay byte for byte. Dot-sourced by engine_smoke_test.ps1 (Run-Engine,
# Assert-*, $tempDir); everything stays in $tempDir (the test sandbox refuses any other write).

$kbDir = Join-Path $tempDir "kneeboard"
New-Item -Path $kbDir -ItemType Directory -Force | Out-Null
$kbPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $kbDir "data"
Add-Type -AssemblyName System.Drawing

function Get-KbHash([string]$Path) { (Get-FileHash -Algorithm SHA256 -Path $Path).Hash }
function Get-KbSize([string]$Path) {
    $image = [System.Drawing.Image]::FromFile($Path)
    try { return "$($image.Width)x$($image.Height)" } finally { $image.Dispose() }
}

try {
    $kbDevice = "KB Stick {7A7A7A7A-0000-1111-2222-333333333333}"
    $kbProfile = Join-Path $kbDir "kneeboard.pr0file"
    foreach ($kbRel in @(
            @("KB TMS Up", "DCS:d3010pnilu3010cd16vd1vpnilvu0:F-16C_50", "JOY_BTN1"),
            @("KB TMS Up easy", "DCS:d3010pnilu3010cd16vd1vpnilvu0:F-16C_50_easy", "JOY_BTN2"),
            @("KB Pilot", "DCS:d1:F-14", "JOY_BTN3"),
            @("KB RIO", "DCS:d2:F-14_RIO", "JOY_BTN4"),
            @("KB Hornet", "DCS:d3:FA-18C_hornet", "JOY_BTN5"),
            @("KB Unknown", "DCS:d4:KB Test Jet", "JOY_BTN6"),
            @("KB Menu", "DCS:d5:UiLayer", "JOY_BTN7"))) {
        $kbAdd = Run-Engine @("--profile", $kbProfile, "relations", "add", $kbRel[0], "--button", "--node", $kbRel[1], "--device", $kbDevice, "--input", $kbRel[2], "--text")
        Assert-True ($kbAdd.ExitCode -eq 0) ("kneeboard fixture: relations add " + $kbRel[0] + " failed: " + $kbAdd.Output)
    }
    $kbLayout = Join-Path $kbDir "stick.callouts.json"
    $kbSheet = Run-Engine @("layout", "callouts", "--model", "vkb-gunfighter-mcg-pro", "--device", $kbDevice, "--save", $kbLayout, "--text")
    Assert-True (Test-Path $kbLayout) ("kneeboard fixture: the stick's sheet was not saved: " + $kbSheet.Output)

    # Two fake Saved Games folders, each with DCS's Config folder, and pictures of others already on the kneeboard.
    $kbDcs = Join-Path $kbDir "Saved Games\DCS"
    $kbBeta = Join-Path $kbDir "Saved Games\DCS.openbeta"
    foreach ($kbRoot in @($kbDcs, $kbBeta)) { New-Item -Path (Join-Path $kbRoot "Config\Input") -ItemType Directory -Force | Out-Null }
    $kbViperFolder = Join-Path $kbDcs "Kneeboard\F-16C_50"
    New-Item -Path $kbViperFolder -ItemType Directory -Force | Out-Null
    $kbForeign = Join-Path $kbViperFolder "01_my_checklist.png"
    [IO.File]::WriteAllBytes($kbForeign, [byte[]](1..200))
    # A picture of someone else that happens to have JoyPro's name: never replaced (it lacks JoyPro's mark).
    $kbNameClash = Join-Path $kbViperFolder "JoyPro_F-16C_50_KB Stick.png"
    [IO.File]::WriteAllBytes($kbNameClash, [byte[]](5..90))
    $kbGlobal = Join-Path $kbDcs "Kneeboard\all_aircraft.jpg"
    [IO.File]::WriteAllBytes($kbGlobal, [byte[]](7..77))
    $kbForeignHashes = @{}
    foreach ($kbFile in @($kbForeign, $kbNameClash, $kbGlobal)) { $kbForeignHashes[$kbFile] = Get-KbHash $kbFile }

    $kbArgs = @("--profile", $kbProfile, "layout", "kneeboard", "--saved-games", $kbDcs, "--saved-games", $kbBeta, "--device", $kbDevice,
        "--layout-map", ($kbDevice + "=" + $kbLayout))
    $kbFirst = Run-Engine ($kbArgs + @("--text"))
    Assert-True ($kbFirst.ExitCode -eq 0) ("layout kneeboard failed: " + $kbFirst.Output)
    Assert-Match $kbFirst.Output "(?m)^Folders: 2" "layout kneeboard did not use both Saved Games folders: $($kbFirst.Output)"
    # Folder names: the unit types DCS uses (the F-14's seats share the F-14B's folders), the unknown aircraft its own name.
    Assert-Match $kbFirst.Output "(?m)^  - F-14 -> F-14B, F-14A-135-GR, F-14A-135-GR-Early, F-14A-95-GR\r?$" "the F-14 has the wrong kneeboard folders: $($kbFirst.Output)"
    Assert-Match $kbFirst.Output "(?m)^  - F-14_RIO -> F-14B," "the RIO seat has the wrong kneeboard folders."
    Assert-Match $kbFirst.Output "(?m)^  - FA-18C_hornet -> FA-18C_hornet\r?$" "the Hornet's kneeboard folder is wrong."
    Assert-Match $kbFirst.Output "(?m)^  - KB Test Jet -> KB Test Jet \(folder name guessed\)" "an unknown aircraft must use its own name and say so: $($kbFirst.Output)"
    Assert-Match $kbFirst.Output "F-16C_50_easy: the same kneeboard as F-16C_50" "the game-mode copy must be left out while its aircraft has binds."
    Assert-NotMatch $kbFirst.Output "(?m)^  - UiLayer" "a menu layer must get no kneeboard."
    Assert-Match $kbFirst.Output "a picture of someone else with this name; left as it is" "the picture of someone else with JoyPro's name must be left as it is."
    foreach ($kbRoot in @($kbDcs, $kbBeta)) {
        foreach ($kbExpected in @("F-14B\JoyPro_F-14_KB Stick.png", "F-14B\JoyPro_F-14_RIO_KB Stick.png", "F-14A-95-GR\JoyPro_F-14_KB Stick.png",
                "FA-18C_hornet\JoyPro_FA-18C_hornet_KB Stick.png", "KB Test Jet\JoyPro_KB Test Jet_KB Stick.png")) {
            Assert-True (Test-Path (Join-Path $kbRoot ("Kneeboard\" + $kbExpected))) ("kneeboard picture missing: " + $kbRoot + "\Kneeboard\" + $kbExpected)
        }
        Assert-True (-not (Test-Path (Join-Path $kbRoot "Kneeboard\F-16C_50_easy"))) "the game-mode copy got a folder of its own."
    }
    Assert-True (Test-Path (Join-Path $kbBeta "Kneeboard\F-16C_50\JoyPro_F-16C_50_KB Stick.png")) "the F-16 page is missing in DCS.openbeta."
    $kbHornet = Join-Path $kbDcs "Kneeboard\FA-18C_hornet\JoyPro_FA-18C_hornet_KB Stick.png"
    Assert-True ((Get-KbSize $kbHornet) -eq "1536x2048") "the kneeboard page is not 1536x2048 (3:4 portrait): $(Get-KbSize $kbHornet)"
    Assert-True ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($kbHornet), 0, 200) -match "JoyPro kneeboard") "the kneeboard page lacks JoyPro's mark."
    foreach ($kbFile in $kbForeignHashes.Keys) { Assert-True ((Get-KbHash $kbFile) -eq $kbForeignHashes[$kbFile]) ("a picture of someone else was changed: " + $kbFile) }

    # Again with another size: JoyPro's pictures are replaced, nothing else changes.
    $kbHornetHash = Get-KbHash $kbHornet
    $kbSecond = Run-Engine ($kbArgs + @("--size", "1024", "--text"))
    Assert-True ($kbSecond.ExitCode -eq 0) ("layout kneeboard (again) failed: " + $kbSecond.Output)
    $kbWritten = [int]([regex]::Match($kbSecond.Output, "(?m)^Pictures Written: (\d+)").Groups[1].Value)
    $kbReplaced = [int]([regex]::Match($kbSecond.Output, "(?m)^Pictures Replaced: (\d+)").Groups[1].Value)
    Assert-True ($kbWritten -gt 0 -and $kbReplaced -eq $kbWritten) "the second export must replace every JoyPro picture: written $kbWritten, replaced $kbReplaced."
    Assert-True ((Get-KbSize $kbHornet) -eq "768x1024") "--size 1024 did not make a 768x1024 page."
    Assert-True ((Get-KbHash $kbHornet) -ne $kbHornetHash) "the JoyPro picture was not replaced."
    foreach ($kbFile in $kbForeignHashes.Keys) { Assert-True ((Get-KbHash $kbFile) -eq $kbForeignHashes[$kbFile]) ("a picture of someone else was changed by the second export: " + $kbFile) }

    # One aircraft, a postfix; an aircraft without binds; JSON.
    $kbOne = Run-Engine ($kbArgs + @("--plane", "FA-18C_hornet", "--postfix", "_VR"))
    Assert-True ($kbOne.ExitCode -eq 0) ("layout kneeboard --plane failed: " + $kbOne.Output)
    Assert-Match $kbOne.Output '"PicturesWritten":2' "--plane FA-18C_hornet must write one page per Saved Games folder: $($kbOne.Output)"
    Assert-True (Test-Path (Join-Path $kbDcs "Kneeboard\FA-18C_hornet\JoyPro_FA-18C_hornet_KB Stick_VR.png")) "the postfix is not in the file name."
    # The page header names the aircraft as the module does (entry.lua, else the cockpit database), not by its id.
    Assert-Match $kbOne.Output '"Plane":"FA-18C_hornet","Title":"(?!FA-18C_hornet")[^"]+"' "the kneeboard header must use the aircraft's readable name: $($kbOne.Output)"
    $kbNone = Run-Engine ($kbArgs + @("--plane", "AJS37", "--text"))
    Assert-True ($kbNone.ExitCode -ne 0) "an aircraft without binds must fail."
    Assert-Match $kbNone.Output "AJS37: no binds for it in this profile" "the failure must say the aircraft has no binds: $($kbNone.Output)"

    # --remove-old: an old JoyPro page (a device that left the profile) goes, the pictures of others stay.
    $kbOld = Join-Path $kbDcs "Kneeboard\FA-18C_hornet\JoyPro_FA-18C_hornet_Old Throttle.png"
    Copy-Item -Path $kbHornet -Destination $kbOld
    $kbRemove = Run-Engine ($kbArgs + @("--remove-old", "--text"))
    Assert-True ($kbRemove.ExitCode -eq 0) ("layout kneeboard --remove-old failed: " + $kbRemove.Output)
    Assert-True (-not (Test-Path $kbOld)) "--remove-old kept the old JoyPro page."
    Assert-True (Test-Path (Join-Path $kbDcs "Kneeboard\FA-18C_hornet\JoyPro_FA-18C_hornet_KB Stick_VR.png")) "--remove-old removed a page with another postfix."
    foreach ($kbFile in $kbForeignHashes.Keys) { Assert-True ((Get-KbHash $kbFile) -eq $kbForeignHashes[$kbFile]) ("--remove-old touched a picture of someone else: " + $kbFile) }

    # A folder of choice (OpenKneeboard's Folder tab, other games): <folder>\<game>\<aircraft>\; --page original keeps the
    # cheat sheet in its own shape.
    $kbFolder = Join-Path $kbDir "My kneeboard"
    $kbToFolder = Run-Engine @("--profile", $kbProfile, "layout", "kneeboard", "--target", "folder", "--folder", $kbFolder, "--plane", "FA-18C_hornet",
        "--device", $kbDevice, "--layout-map", ($kbDevice + "=" + $kbLayout), "--size", "1600", "--page", "original", "--text")
    Assert-True ($kbToFolder.ExitCode -eq 0) ("layout kneeboard --target folder failed: " + $kbToFolder.Output)
    $kbFolderPage = Join-Path $kbFolder "DCS\FA-18C_hornet\JoyPro_FA-18C_hornet_KB Stick.png"
    Assert-True (Test-Path $kbFolderPage) "the page is missing in the folder of choice."
    $kbFolderSize = (Get-KbSize $kbFolderPage) -split "x"
    Assert-True ([int]$kbFolderSize[0] -eq 1600 -and [int]$kbFolderSize[1] -lt 1600) "the folder page should keep the sheet's landscape shape, 1600 wide: $($kbFolderSize -join 'x')"

    # The list page (the default): a busy device goes on to page 2 (..._p2.png); with fewer binds the old page 2 goes.
    # One page with all devices lists every device; no GUID in any file name.
    $kbBusyProfile = Join-Path $kbDir "busy.pr0file"
    for ($kbN = 1; $kbN -le 70; $kbN++) {
        $null = Run-Engine @("--profile", $kbBusyProfile, "relations", "add", ("Busy " + $kbN), "--button", "--node", ("DCS:d" + (5000 + $kbN) + ":FA-18C_hornet"),
            "--device", $kbDevice, "--input", ("JOY_BTN" + $kbN))
    }
    $null = Run-Engine @("--profile", $kbBusyProfile, "relations", "add", "Busy pedal", "--button", "--node", "DCS:d4999:FA-18C_hornet", "--device", "KB Pedals {7B7B7B7B-0000-1111-2222-333333333333}", "--input", "JOY_BTN1")
    $kbListFolder = Join-Path $kbDir "List pages"
    $kbBusy = Run-Engine @("--profile", $kbBusyProfile, "layout", "kneeboard", "--target", "folder", "--folder", $kbListFolder, "--device", $kbDevice,
        "--layout-map", ($kbDevice + "=" + $kbLayout), "--size", "1024", "--text")
    Assert-True ($kbBusy.ExitCode -eq 0) ("layout kneeboard (list page) failed: " + $kbBusy.Output)
    $kbListDir = Join-Path $kbListFolder "DCS\FA-18C_hornet"
    Assert-True (Test-Path (Join-Path $kbListDir "JoyPro_FA-18C_hornet_KB Stick.png")) "the list page is missing: $($kbBusy.Output)"
    Assert-True (Test-Path (Join-Path $kbListDir "JoyPro_FA-18C_hornet_KB Stick_p2.png")) "70 binds should go on to page 2: $($kbBusy.Output)"
    Assert-True ((Get-KbSize (Join-Path $kbListDir "JoyPro_FA-18C_hornet_KB Stick.png")) -eq "768x1024") "the list page is not 768x1024."
    $kbGone = Run-Engine @("--profile", $kbBusyProfile, "relations", "mass", "delete", "--relations", ((21..70 | ForEach-Object { "Busy " + $_ }) -join ","))
    Assert-True ($kbGone.ExitCode -eq 0) ("relations mass delete failed: " + $kbGone.Output)
    $kbFewer = Run-Engine @("--profile", $kbBusyProfile, "layout", "kneeboard", "--target", "folder", "--folder", $kbListFolder, "--device", $kbDevice,
        "--layout-map", ($kbDevice + "=" + $kbLayout), "--size", "1024", "--picture", "none", "--text")
    Assert-True ($kbFewer.ExitCode -eq 0) ("layout kneeboard (fewer binds) failed: " + $kbFewer.Output)
    Assert-True (-not (Test-Path (Join-Path $kbListDir "JoyPro_FA-18C_hornet_KB Stick_p2.png"))) "the old page 2 was kept after the binds got fewer: $($kbFewer.Output)"
    $kbAll = Run-Engine @("--profile", $kbBusyProfile, "layout", "kneeboard", "--target", "folder", "--folder", $kbListFolder, "--combine", "true",
        "--layout-map", ($kbDevice + "=" + $kbLayout), "--text")
    Assert-True ($kbAll.ExitCode -eq 0 -and (Test-Path (Join-Path $kbListDir "JoyPro_FA-18C_hornet_All devices.png"))) "one page with all devices is missing: $($kbAll.Output)"
    Assert-True (@(Get-ChildItem -Path $kbListFolder -Recurse -Filter "*{*").Count -eq 0) "a file name has a device GUID."
    Assert-True ((Run-Engine ($kbArgs + @("--picture", "side"))).ExitCode -ne 0) "layout kneeboard accepted --picture side."

    # A dry run writes nothing; wrong options fail.
    $kbDryFolder = Join-Path $kbDir "Saved Games\DCS.dry"
    New-Item -Path (Join-Path $kbDryFolder "Config") -ItemType Directory -Force | Out-Null
    $kbDry = Run-Engine @("--profile", $kbProfile, "layout", "kneeboard", "--saved-games", $kbDryFolder, "--device", $kbDevice, "--layout-map", ($kbDevice + "=" + $kbLayout), "--dry-run", "--text")
    Assert-True ($kbDry.ExitCode -eq 0) ("layout kneeboard --dry-run failed: " + $kbDry.Output)
    Assert-True (-not (Test-Path (Join-Path $kbDryFolder "Kneeboard"))) "--dry-run wrote pictures."
    Assert-True ((Run-Engine @("--profile", $kbProfile, "layout", "kneeboard", "--target", "printer")).ExitCode -ne 0) "layout kneeboard accepted --target printer."
    Assert-True ((Run-Engine @("--profile", $kbProfile, "layout", "kneeboard", "--target", "folder")).ExitCode -ne 0) "layout kneeboard --target folder without --folder was accepted."
    Assert-True ((Run-Engine ($kbArgs + @("--size", "100"))).ExitCode -ne 0) "layout kneeboard accepted --size 100."
    Assert-True ((Run-Engine ($kbArgs + @("--postfix", "a/b"))).ExitCode -ne 0) "layout kneeboard accepted a postfix with a slash."
}
finally {
    $env:JOYPRO_DATA_DIR = $kbPreviousData
}
