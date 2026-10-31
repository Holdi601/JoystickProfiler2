# Battlefield 6 / 2042 as one-device games (docs/games/battlefield.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine,
# Assert-*, $tempDir, $engineDir, $engineBin). Never touches the installed game: a synthetic PROFSAVEbf6mp_profile (plus
# a sibling PROFSAVE_profile) in a temp folder, fake devices, a temp JoyPro data folder.
#  1. bf6 refresh-db: the jet and helicopter concepts from the profile (axis / button), nothing else.
#  2. export before the joystick numbering is confirmed: the joystick binds are refused, the file stays byte-identical.
#  3. export with a confirmed table (JOYPRO_BATTLEFIELD_MAP): axes as two halves (negate 0 / 1 slots), an inverted axis
#     swaps them, buttons and a hat get new joystick slots in sorted order, the sibling profile is written too, lines
#     JoyPro does not own stay; scan reads it back; a second device is refused; a second export changes nothing.
#  4. vjoy status suggests the virtual joystick for the one-device game.
$bfDir = Join-Path $tempDir "battlefield"
$bfSettings = Join-Path $bfDir "settings\steam"
New-Item -ItemType Directory -Force -Path $bfSettings | Out-Null
$bfFixture = Join-Path $engineDir "tests\fixtures\battlefield\PROFSAVEbf6mp_profile"
$bfMp = Join-Path $bfSettings "PROFSAVEbf6mp_profile"
$bfSp = Join-Path $bfSettings "PROFSAVE_profile"
Copy-Item $bfFixture $bfMp
Copy-Item $bfFixture $bfSp
# refresh-db writes into the game-data folder (<data>\GameData), never into the program folder.
$bfCatalogDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\Battlefield6"
$bfCatalogBackup = Join-Path $bfDir "catalog-backup"
$bfHadCatalog = Test-Path $bfCatalogDir
if ($bfHadCatalog) {
    Copy-Item -Path $bfCatalogDir -Destination $bfCatalogBackup -Recurse -Force
}
$bfPrevious = @{ Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION; BfMap = $env:JOYPRO_BATTLEFIELD_MAP; Fake = $env:JOYPRO_VJOY_FAKE }
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_BATTLEFIELD_MAP = Join-Path $bfDir "no-table.tsv"
$bfFake = Join-Path $bfDir "fake"
New-Item -ItemType Directory -Force -Path $bfFake | Out-Null
[System.IO.File]::WriteAllText((Join-Path $bfFake "vjoy.txt"), "installed=2.1.9.1`r`ndevice=1 axes=x,y,z,rx,ry,rz,sl0,sl1 buttons=32 povs=4 discrete=0 ffb=no`r`n")
[System.IO.File]::WriteAllText((Join-Path $bfFake "hidhide-state.txt"), "--cloak-off`r`n--inv-on`r`n")
$env:JOYPRO_VJOY_FAKE = $bfFake
$bfStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$bfThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$bfProfile = Join-Path $bfDir "bf6.pr0file"
$bfShippedMap = Join-Path $engineBin "TOOLS\Conversions\Battlefield6JoystickMap.tsv"
$bfShippedBackup = Join-Path $bfDir "shipped-map-backup.tsv"
try {
    # ---- 0. before the game wrote its profile: JoyPro's own jet/helicopter table (Battlefield 2042 too)
    if (-not (Test-Path (Join-Path $bfCatalogDir "actions.tsv"))) {
        $bfKnown = Run-Engine @("relations", "browse", "--game", "Battlefield6", "--axis", "--text")
        Assert-Match $bfKnown.Output "Catalog: Battlefield6 \| 13 actions" ("Without a list the catalog must show JoyPro's own Battlefield axes: " + $bfKnown.Output)
        Assert-Match $bfKnown.Output "jet\.ConceptPitch \| Jet: Pitch \| axis" ("The Battlefield table must have the jet pitch axis: " + $bfKnown.Output)
    }
    $bf2042Known = Run-Engine @("relations", "search", "fire", "--game", "Battlefield2042", "--text")
    Assert-Match $bf2042Known.Output "Battlefield2042:jet\.ConceptFire" ("Battlefield 2042 must be found through JoyPro's own table: " + $bf2042Known.Output)

    # ---- 1. catalog from the user's profile
    $bfRefresh = Run-Engine @("bf6", "refresh-db", $bfMp, "--text")
    Assert-True ($bfRefresh.ExitCode -eq 0) "bf6 refresh-db failed. Output: $($bfRefresh.Output)"
    $bfCatalog = [System.IO.File]::ReadAllText((Join-Path $bfCatalogDir "actions.tsv"))
    Assert-Match $bfCatalog "(?m)^jet\.ConceptPitch\tJet: Pitch\taxis\tJet" "Pitch must be a jet axis."
    Assert-Match $bfCatalog "(?m)^jet\.ConceptMoveFB\tJet: Throttle \(forward/back\)\taxis\tJet" "The throttle must be a jet axis."
    Assert-Match $bfCatalog "(?m)^jet\.ConceptFire\tJet: Fire\tbutton\tJet" "Fire must be a jet button."
    Assert-Match $bfCatalog "(?m)^helicopter\.ConceptYaw\tHelicopter: Yaw\taxis\tHelicopter" "The helicopter yaw must be in the catalog."
    Assert-NotMatch $bfCatalog "transport\." "Only jet and helicopter concepts belong in the catalog."

    Run-Engine @("--profile", $bfProfile, "relations", "add", "Pitch", "--axis", "--node", "Battlefield6:jet.ConceptPitch", "--device", $bfStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Roll", "--axis", "--node", "Battlefield6:jet.ConceptRoll", "--device", $bfStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Throttle", "--axis", "--node", "Battlefield6:jet.ConceptMoveFB", "--device", $bfStick, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "binds", "set", "Throttle", "--inverted", "true", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Fire", "--button", "--node", "Battlefield6:jet.ConceptFire", "--device", $bfStick, "--input", "JOY_BTN1", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Zoom", "--button", "--node", "Battlefield6:jet.ConceptZoom", "--device", $bfStick, "--input", "JOY_BTN_POV1_U", "--text") | Out-Null

    # ---- 2. not confirmed yet: nothing is written for the joystick
    $bfOriginal = [System.IO.File]::ReadAllBytes($bfMp)
    $bfRefused = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--text")
    Assert-Match $bfRefused.Output "joystick bind\(s\) not written: JoyPro's joystick numbering for Battlefield 6 is not confirmed yet" "Unconfirmed numbering must stop the joystick binds."
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$bfOriginal, [byte[]][System.IO.File]::ReadAllBytes($bfMp))) "Without a confirmed table the profile must stay byte-identical."

    # ---- 3. confirmed table
    $env:JOYPRO_BATTLEFIELD_MAP = Join-Path $engineDir "tests\fixtures\battlefield\joystick-map.tsv"
    $bfExport = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--text")
    Assert-True ($bfExport.ExitCode -eq 0) "bf6 export failed. Output: $($bfExport.Output)"
    $bfText = [System.IO.File]::ReadAllText($bfMp)
    Assert-True (-not $bfText.Contains("`r")) "The profile must keep LF line ends."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptPitch\.4\.axis 12$" "Pitch: Y+ in the negate-0 joystick slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptPitch\.5\.axis 14$" "Pitch: Y- in the negate-1 joystick slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptRoll\.3\.axis 11$" "Roll: X+ in the negate-0 slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptRoll\.5\.axis 13$" "Roll: X- in the negate-1 slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptMoveFB\.3\.button 37$" "Inverted throttle: Z- (a button code) in the negate-0 slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptMoveFB\.4\.button 36$" "Inverted throttle: Z+ in the negate-1 slot."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptMoveFB\.3\.axis 56$" "A half on a button code leaves the axis unbound."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptFire\.3\.button 0\nGstKeyBinding\.jet\.ConceptFire\.3\.negate 0\nGstKeyBinding\.jet\.ConceptFire\.3\.type 3$" "Fire gets a new joystick slot with button 1."
    Assert-Match $bfText "(?m)^GstKeyBinding\.jet\.ConceptZoom\.3\.button 44$" "Zoom gets hat 1 up."
    Assert-Match $bfText "(?m)^GstKeyBinding\.transport\.ConceptMoveFB\.1\.axis 1$" "Binds outside the catalog stay."
    Assert-Match $bfText "(?m)^GstInput\.JoystickDeadzoneAxis 0\.150000$" "Other settings stay."
    [string[]]$bfKeys = @($bfText -split "`n" | Where-Object { $_ -like "GstKeyBinding.*" } | ForEach-Object { $_.Split(' ')[0] })
    [string[]]$bfSorted = $bfKeys.Clone()
    [Array]::Sort($bfSorted, [StringComparer]::Ordinal)
    Assert-True (($bfKeys -join "|") -ceq ($bfSorted -join "|")) "The GstKeyBinding lines must stay sorted."
    Assert-True ([System.IO.File]::ReadAllText($bfSp) -eq $bfText) "The sibling profile must get the same binds."
    Assert-True (Test-Path ($bfMp + ".jpbak")) "A backup must be written before the first overwrite."

    $bfScan = Run-Engine @("--profile", $bfProfile, "bf6", "scan", $bfMp, "--text")
    Assert-Match $bfScan.Output "jet\.ConceptPitch <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Y" "scan must read Pitch back on the stick."
    Assert-Match $bfScan.Output "jet\.ConceptMoveFB <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_Z" "scan must read the throttle halves back as JOY_Z."
    Assert-Match $bfScan.Output "jet\.ConceptZoom <- VJ Stick \{11111111-2222-3333-4444-000000000001\} JOY_BTN_POV1_U" "scan must read the hat back."

    $bfAgain = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--text")
    Assert-Match $bfAgain.Output "already up to date" "A second export must change nothing."

    Run-Engine @("--profile", $bfProfile, "relations", "add", "Gear", "--button", "--node", "Battlefield6:helicopter.ConceptFire", "--device", $bfThrottle, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Brake", "--button", "--node", "Battlefield6:helicopter.ConceptFire", "--device", $bfThrottle, "--input", "JOY_BTN4", "--text") | Out-Null
    $bfTwo = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--dry-run", "--text")
    Assert-Match $bfTwo.Output "Battlefield 6 reads one joystick" "A bind on a second device must be refused in native mode."

    # ---- 4. one-device game: the status suggests the virtual joystick
    $bfStatus = Run-Engine @("--profile", $bfProfile, "vjoy", "status", "--text")
    Assert-Match $bfStatus.Output "Battlefield6 \| native \| .*one-device game: virtual suggested" "status must suggest vJoy for Battlefield 6."

    # ---- 5. virtual mode: one vJoy device without Rx/Ry (the game takes a stick with Rx/Ry for an Xbox pad)
    Run-Engine @("--profile", $bfProfile, "relations", "add", "Look", "--axis", "--node", "Battlefield6:helicopter.ConceptYaw", "--device", $bfThrottle, "--input", "JOY_RX", "--text") | Out-Null
    $bfVirtual = Run-Engine @("--profile", $bfProfile, "vjoy", "mode", "Battlefield6", "virtual", "--text")
    Assert-True ($bfVirtual.ExitCode -eq 0) "vjoy mode Battlefield6 virtual failed. Output: $($bfVirtual.Output)"
    Assert-Match $bfVirtual.Output "VJ Throttle \{11111111-2222-3333-4444-000000000002\} JOY_RX -> vJoy 1 JOY_RZ" "A new axis must skip vJoy Rx/Ry for Battlefield."
    Assert-NotMatch $bfVirtual.Output "-> vJoy 1 JOY_R[XY]" "No input may land on vJoy Rx/Ry."
    $bfConfigure = Run-Engine @("--profile", $bfProfile, "vjoy", "configure-vjoy", "--dry-run", "--text")
    Assert-Match $bfConfigure.Output "vJoyConfig 1 -f -a x y z rz sl0 sl1 " "configure-vjoy must leave Rx/Ry out of the vJoy device."
    $bfVirtualExport = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--dry-run", "--text")
    Assert-Match $bfVirtualExport.Output "jet\.ConceptPitch <- vJoy Device \{89D63500-B53F-11F1-8002-444553540000\} JOY_Y" "In virtual mode the binds go to the vJoy device."
    Assert-NotMatch $bfVirtualExport.Output "has the most binds" "In virtual mode every bind is on the one vJoy device."
    Run-Engine @("--profile", $bfProfile, "vjoy", "mode", "Battlefield6", "native", "--text") | Out-Null

    # ---- 6. the confirmed table is JoyPro's shipped data: <engine>\TOOLS\Conversions\Battlefield6JoystickMap.tsv
    #         (read without JOYPRO_BATTLEFIELD_MAP), and release packaging keeps it while it strips the game DB folders.
    $env:JOYPRO_BATTLEFIELD_MAP = ""
    if (Test-Path $bfShippedMap) {
        Copy-Item $bfShippedMap $bfShippedBackup -Force
    }
    Copy-Item (Join-Path $engineDir "tests\fixtures\battlefield\joystick-map.tsv") $bfShippedMap -Force
    $bfShippedExport = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--dry-run", "--text")
    Assert-NotMatch $bfShippedExport.Output "numbering for Battlefield 6 is not confirmed" "The table in TOOLS\Conversions must open the joystick writes."
    Remove-Item $bfShippedMap -Force
    $bfNoTable = Run-Engine @("--profile", $bfProfile, "bf6", "export", $bfMp, "--dry-run", "--text")
    Assert-Match $bfNoTable.Output "numbering for Battlefield 6 is not confirmed" "Without the table the joystick writes stay closed."
    $bfRelease = Join-Path $bfDir "release-engine"
    New-Item -ItemType Directory -Force -Path (Join-Path $bfRelease "DB\Battlefield6"), (Join-Path $bfRelease "DB\Functions"), (Join-Path $bfRelease "TOOLS\Conversions") | Out-Null
    Set-Content -Path (Join-Path $bfRelease "DB\Battlefield6\actions.tsv") -Value "jet.ConceptPitch`tJet: Pitch`taxis`tJet"
    Set-Content -Path (Join-Path $bfRelease "TOOLS\Conversions\Battlefield6JoystickMap.tsv") -Value "JOY_X`taxis`t9"
    Import-Module (Join-Path (Split-Path -Parent $engineDir) "tools\ReleaseContent.psm1") -Force -DisableNameChecking
    Remove-JoyProGameData -EngineDir $bfRelease | Out-Null
    Assert-True (Test-Path (Join-Path $bfRelease "TOOLS\Conversions\Battlefield6JoystickMap.tsv")) "Release packaging must keep the Battlefield joystick table."
    Assert-True (-not (Test-Path (Join-Path $bfRelease "DB\Battlefield6"))) "Release packaging must strip the game DB folder."
}
finally {
    if (Test-Path $bfShippedBackup) {
        Copy-Item $bfShippedBackup $bfShippedMap -Force
    }
    elseif (Test-Path $bfShippedMap) {
        Remove-Item $bfShippedMap -Force
    }
    $env:JOYPRO_FAKE_DEVICES = $bfPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $bfPrevious.Detection
    $env:JOYPRO_BATTLEFIELD_MAP = $bfPrevious.BfMap
    $env:JOYPRO_VJOY_FAKE = $bfPrevious.Fake
    if (Test-Path $bfCatalogDir) {
        Remove-Item -Path $bfCatalogDir -Recurse -Force
    }
    if ($bfHadCatalog) {
        Copy-Item -Path $bfCatalogBackup -Destination $bfCatalogDir -Recurse -Force
    }
}

# ---- 7. export modes (confirmed table; only jet / helicopter slots, jet and helicopter are different vehicles):
#   add       never frees a slot; a bind on an input another action of the same vehicle uses is skipped and reported
#   merge     frees the other action's slot on the profile's input (conflict), keeps the action's bound slots (a bind that
#   merge-overwrite  merge + the action's other joystick slots (and keyboard keys with --include-keyboard) are freed and
$bmDir = Join-Path $tempDir "battlefield-modes"
New-Item -ItemType Directory -Force -Path $bmDir | Out-Null
$bmFile = Join-Path $bmDir "PROFSAVEbf6mp_profile"
# Fire has stick button 1 (code 0) in a joystick slot.
$bmText = [System.IO.File]::ReadAllText($bfFixture).Replace("GstKeyBinding.jet.ConceptFire.2.type 0`n", "GstKeyBinding.jet.ConceptFire.2.type 0`nGstKeyBinding.jet.ConceptFire.3.axis 56`nGstKeyBinding.jet.ConceptFire.3.button 0`nGstKeyBinding.jet.ConceptFire.3.negate 0`nGstKeyBinding.jet.ConceptFire.3.type 3`n")
[System.IO.File]::WriteAllText($bmFile, $bmText)
$bmProfile = Join-Path $bmDir "modes.pr0file"
function Get-BmNonFlight([string]$Text) { return (($Text -split "`n") | Where-Object { $_ -notmatch '^GstKeyBinding\.(jet|helicopter)\.' }) -join "`n" }
function Get-BmFireSlot([string]$Text, [int]$Code) { return [regex]::IsMatch($Text, "(?m)^GstKeyBinding\.jet\.ConceptFire\.(\d)\.button $Code\nGstKeyBinding\.jet\.ConceptFire\.\1\.negate 0\nGstKeyBinding\.jet\.ConceptFire\.\1\.type 3$") }
$bmNonFlight = Get-BmNonFlight $bmText
$bmPrevious = @{ Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION; BfMap = $env:JOYPRO_BATTLEFIELD_MAP; Data = $env:JOYPRO_DATA_DIR }
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_BATTLEFIELD_MAP = Join-Path $engineDir "tests\fixtures\battlefield\joystick-map.tsv"
$env:JOYPRO_DATA_DIR = Join-Path $bmDir "data"
try {
    Assert-True (Get-BmFireSlot $bmText 0) "the battlefield mode fixture should have Fire on joystick code 0."
    Run-Engine @("bf6", "refresh-db", $bmFile, "--text") | Out-Null
    $bmStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
    # Moved: fire from stick button 1 to button 3. Conflict: zoom on Space, the jet fire's key (the helicopter fire has
    # Space too: another vehicle, it stays).
    Run-Engine @("--profile", $bmProfile, "relations", "add", "BM Fire", "--button", "--node", "Battlefield6:jet.ConceptFire", "--device", $bmStick, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $bmProfile, "relations", "add", "BM Zoom", "--button", "--node", "Battlefield6:jet.ConceptZoom", "--device", "Keyboard", "--input", "Space", "--text") | Out-Null

    $bmAdd = Run-Engine @("--profile", $bmProfile, "bf6", "export", $bmFile, "--mode", "add", "--include-keyboard")
    Assert-True ($bmAdd.ExitCode -eq 0) ("bf6 add export failed: " + $bmAdd.Output)
    $bmAddJson = ($bmAdd.Output | Out-String | ForEach-Object { $_.Substring($_.IndexOf("{")) } | ConvertFrom-Json)
    Assert-True (@($bmAddJson.SkippedTakenInputs | Where-Object { $_.Action -eq "jet.ConceptZoom" -and $_.GameAction -eq "jet.ConceptFire" }).Count -eq 1) ("bf6 add should report zoom on the fire key: " + $bmAdd.Output)
    $bmAfter = [System.IO.File]::ReadAllText($bmFile)
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptFire\.0\.button 57$" "bf6 add must keep the fire key."
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptZoom\.1\.button 255$" "bf6 add must not write zoom on the fire key."
    # Fire's only joystick slot is bound: add and merge keep it (the game's slots are not multiplied).
    Assert-True ((Get-BmNonFlight $bmAfter) -eq $bmNonFlight) "bf6 add must leave the non-flight lines identical."

    $bmMerge = Run-Engine @("--profile", $bmProfile, "bf6", "export", $bmFile, "--mode", "merge", "--include-keyboard", "--text")
    Assert-True ($bmMerge.ExitCode -eq 0) ("bf6 merge export failed: " + $bmMerge.Output)
    $bmAfter = [System.IO.File]::ReadAllText($bmFile)
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptFire\.0\.button 255$" "bf6 merge should free the fire key the profile gives zoom (conflict)."
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptZoom\.1\.button 57$" "bf6 merge should write zoom on Space."
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.helicopter\.ConceptFire\.0\.button 57$" "bf6 merge must keep the helicopter's Space (another vehicle)."
    Assert-True ((Get-BmFireSlot $bmAfter 0) -and -not (Get-BmFireSlot $bmAfter 2)) "bf6 merge must keep fire's bound joystick slot."
    Assert-True ((Get-BmNonFlight $bmAfter) -eq $bmNonFlight) "bf6 merge must leave the non-flight lines identical."

    $bmOverwrite = Run-Engine @("--profile", $bmProfile, "bf6", "export", $bmFile, "--mode", "merge-overwrite", "--include-keyboard", "--text")
    Assert-True ($bmOverwrite.ExitCode -eq 0) ("bf6 merge-overwrite export failed: " + $bmOverwrite.Output)
    $bmAfter = [System.IO.File]::ReadAllText($bmFile)
    Assert-True ((-not (Get-BmFireSlot $bmAfter 0)) -and (Get-BmFireSlot $bmAfter 2)) "bf6 merge-overwrite should free fire's old joystick slot and keep the new one."
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptPitch\.0\.button 208$" "bf6 merge-overwrite must keep actions the profile does not bind."
    Assert-True ((Get-BmNonFlight $bmAfter) -eq $bmNonFlight) "bf6 merge-overwrite must leave the non-flight lines identical."

    $bmClean = Run-Engine @("--profile", $bmProfile, "bf6", "export", $bmFile, "--mode", "clean", "--nuke-untouched-devices", "--include-keyboard", "--text")
    Assert-True ($bmClean.ExitCode -eq 0) ("bf6 clean + nuke export failed: " + $bmClean.Output)
    $bmAfter = [System.IO.File]::ReadAllText($bmFile)
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.helicopter\.ConceptFire\.0\.button 255$" "bf6 clean should release the other flight keys of the written keyboard."
    Assert-Match $bmAfter "(?m)^GstKeyBinding\.jet\.ConceptPitch\.0\.button 208$" "bf6 clean must keep keys on axis actions (the import does not read them)."
    Assert-True ((Get-BmNonFlight $bmAfter) -eq $bmNonFlight) "bf6 clean + nuke must leave the non-flight lines identical."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $bmPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $bmPrevious.Detection
    $env:JOYPRO_BATTLEFIELD_MAP = $bmPrevious.BfMap
    $env:JOYPRO_DATA_DIR = $bmPrevious.Data
}