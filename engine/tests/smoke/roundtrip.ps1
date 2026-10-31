# Import -> export round trips (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineDir,
# $starCitizenDbCatalogPath). Users import the binds they have in a game and export with the defaults (clean + nuke):
# the game must end up exactly as it was. What the import cannot hold (a device that is not connected, mouse lines,
# half-axis binds) stays untouched; the game's active defaults come in on import and are not written back as changes.
# "Update, my profile wins" (merge-overwrite) moves an action the profile moved and keeps a bind the game added; plain
# merge removes nothing. Fixtures: engine\tests\fixtures\roundtrip (synthetic) and fixtures\elite, fixtures\nuclearoption.

$rtFixtures = Join-Path $engineDir "tests\fixtures\roundtrip"
$rtPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES

function Rt-Json([string]$Text) {
    $start = $Text.IndexOf("{")
    Assert-True ($start -ge 0) ("expected JSON output: " + $Text)
    return $Text.Substring($start) | ConvertFrom-Json
}

# IL-2 action lines as a sorted list "action|command|invert" (comments and padding do not count).
function Rt-Il2Lines([string]$Path) {
    return @(Get-Content -Path $Path | ForEach-Object { ($_ -split "//")[0].Trim() } |
        Where-Object { $_.Length -gt 0 -and -not $_.StartsWith("&") } |
        ForEach-Object { (($_.TrimEnd("|") -split ",") | ForEach-Object { $_.Trim() }) -join "|" } | Sort-Object)
}

# Nuclear Option's fake registry decoded: value name -> text.
function Rt-Registry([string]$Path) {
    $values = @{}
    $json = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) {
        $values[$p.Name] = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p.Value)).TrimEnd([char]0)
    }
    return $values
}

try {
    # ---- Elite Dangerous: a device that is not connected (25BB00D8) and half-axis modifiers survive clean + nuke ----
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\bms\devices.tsv"
    $rtEliteDir = Join-Path $tempDir "rt-elite"
    New-Item -Path $rtEliteDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\elite\*") -Destination $rtEliteDir -Force
    $rtElite = Join-Path $rtEliteDir "Custom.4.2.binds"
    $rtEliteBefore = [System.IO.File]::ReadAllText($rtElite)
    $rtEliteProfile = Join-Path $tempDir "rt-elite.pr0file"
    $rtEliteImport = Run-Engine @("--profile", $rtEliteProfile, "elite", "import", "--path", $rtElite, "--replace", "--text")
    Assert-True ($rtEliteImport.ExitCode -eq 0) ("round trip: elite import failed: " + $rtEliteImport.Output)
    $rtEliteExport = Run-Engine @("--profile", $rtEliteProfile, "elite", "export", "--path", $rtElite, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($rtEliteExport.ExitCode -eq 0) ("round trip: elite clean + nuke export failed: " + $rtEliteExport.Output)
    Assert-True ([System.IO.File]::ReadAllText($rtElite) -eq $rtEliteBefore) ("round trip: elite import + clean + nuke export must leave the preset byte-identical (the binds of the device that is not connected stay): " + $rtEliteExport.Output)

    # ---- IL-2 Great Battles: mouse lines, key_return, an inverted axis and a one-sided throttle row ----------------
    # Test Stick and Smoke Pedals are connected, Gone Stick (joy2) is not; Smoke Pedals' joy1_axis_rz line is one the
    # import cannot read. Neither of those is ever released.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $rtFixtures "il2-devices.tsv"
    $rtIl2Root = Join-Path $tempDir "rt-il2"
    $rtIl2Input = Join-Path $rtIl2Root "data\input"
    New-Item -Path $rtIl2Input -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $rtFixtures "il2\data\input\*") -Destination $rtIl2Input -Force
    $rtIl2Actions = Join-Path $rtIl2Input "global.actions"
    $rtIl2Responses = Join-Path $rtIl2Input "current.responses"
    $rtIl2TextBefore = [System.IO.File]::ReadAllText($rtIl2Actions).Replace("`r`n", "`n").TrimEnd("`n")
    $rtIl2LinesBefore = Rt-Il2Lines $rtIl2Actions
    $rtIl2ResponsesBefore = [System.IO.File]::ReadAllText($rtIl2Responses)
    $rtIl2Profile = Join-Path $tempDir "rt-il2.pr0file"
    # The GUI's default import: without --import-inverted/--import-curvature/--import-deadzone.
    $rtIl2Import = Run-Engine @("--profile", $rtIl2Profile, "il2", "import", "--path", $rtIl2Root, "--text")
    Assert-True ($rtIl2Import.ExitCode -eq 0) ("round trip: il2 import failed: " + $rtIl2Import.Output)
    $rtIl2Export = Run-Engine @("--profile", $rtIl2Profile, "il2", "export", "--path", $rtIl2Root, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($rtIl2Export.ExitCode -eq 0) ("round trip: il2 clean + nuke export failed: " + $rtIl2Export.Output)
    Assert-SetEquivalent (Rt-Il2Lines $rtIl2Actions) $rtIl2LinesBefore "round trip: il2 import + clean + nuke export must keep every action line (mouse lines, key_return, the inverted axes)"
    Assert-True ([System.IO.File]::ReadAllText($rtIl2Actions).Replace("`r`n", "`n").TrimEnd("`n") -eq $rtIl2TextBefore) ("round trip: il2 import + clean + nuke export must leave global.actions as it was (order, padding, comments; the game writes CRLF): " + [System.IO.File]::ReadAllText($rtIl2Actions))
    Assert-True ([System.IO.File]::ReadAllText($rtIl2Responses) -eq $rtIl2ResponsesBefore) ("round trip: il2 export must keep current.responses (the one-sided throttle row, the pitch row): " + [System.IO.File]::ReadAllText($rtIl2Responses))

    # Clean + nuke of a profile with one bind: the connected Smoke Pedals lose their readable line, the line the import
    # cannot read, the device that is not connected, the mouse and the keyboard keep theirs.
    $rtIl2NukeRoot = Join-Path $tempDir "rt-il2-nuke"
    New-Item -Path (Join-Path $rtIl2NukeRoot "data\input") -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $rtFixtures "il2\data\input\*") -Destination (Join-Path $rtIl2NukeRoot "data\input") -Force
    $rtIl2NukeProfile = Join-Path $tempDir "rt-il2-nuke.pr0file"
    Run-Engine @("--profile", $rtIl2NukeProfile, "relations", "add", "IL2 Gear", "--button", "--node", "IL2Game:gear", "--device", "Test Stick {00000000-0000-0000-0000-000000000000}", "--input", "JOY_BTN5", "--text") | Out-Null
    $rtIl2Nuke = Run-Engine @("--profile", $rtIl2NukeProfile, "il2", "export", "--path", $rtIl2NukeRoot, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($rtIl2Nuke.ExitCode -eq 0) ("round trip: il2 clean + nuke of a one-bind profile failed: " + $rtIl2Nuke.Output)
    $rtIl2NukeLines = Rt-Il2Lines (Join-Path $rtIl2NukeRoot "data\input\global.actions")
    Assert-True ($rtIl2NukeLines -contains "gear|joy0_b4|0") "round trip: il2 clean must write the profile's bind."
    Assert-True (-not ($rtIl2NukeLines -contains "bc_head_cvc|joy0_b1|0")) "round trip: il2 clean must release the other binds of the device it writes."
    Assert-True (-not ($rtIl2NukeLines -contains "rpc_yaw|joy1_axis_z|0")) "round trip: il2 nuke must release a connected device the profile does not use."
    Assert-True ($rtIl2NukeLines -contains "rpc_yaw|joy1_axis_rz|0") "round trip: il2 nuke must keep a line the import cannot read."
    Assert-True ($rtIl2NukeLines -contains "gear|joy2_b3|0") "round trip: il2 nuke must keep the binds of a device that is not connected."
    foreach ($kept in @("cam_zoom|key_lshift+mouse_axis_z|0", "all_guns_fire|mouse_b0|0", "chattoall|key_return|0", "cam_trans_fb|mouse_b1+mouse_axis_y|0")) {
        Assert-True ($rtIl2NukeLines -contains $kept) ("round trip: il2 clean + nuke must keep " + $kept)
    }

    # Export modes. The game binds bc_head_cfovr on joy0_b9; the profile moves bc_head_cvc (joy0_b1 -> joy0_b5) and
    # puts gear on joy0_b9.
    [System.IO.File]::AppendAllText($rtIl2Actions, "`r`nbc_head_cfovr,                                     joy0_b9,                                          0|")
    $rtIl2Binds = (Rt-Json (Run-Engine @("--profile", $rtIl2Profile, "binds", "list")).Output).Binds
    $rtIl2Moved = $rtIl2Binds | Where-Object { $_.JButton -eq "JOY_BTN2" -and $_.Joystick -like "Test Stick*" } | Select-Object -First 1
    Assert-True ($null -ne $rtIl2Moved) "round trip: il2 import should have a relation for bc_head_cvc on joy0_b1."
    Run-Engine @("--profile", $rtIl2Profile, "binds", "set", $rtIl2Moved.RelationName, "--input", "JOY_BTN6") | Out-Null
    Run-Engine @("--profile", $rtIl2Profile, "relations", "add", "IL2 Gear on 10", "--button", "--node", "IL2Game:gear", "--device", "Test Stick {00000000-0000-0000-0000-000000000000}", "--input", "JOY_BTN10", "--text") | Out-Null
    $rtIl2Before = Rt-Il2Lines $rtIl2Actions
    # Only add: removes nothing; gear is not put on the game's joy0_b9 (no multi-bind) and is reported.
    $rtIl2Add = Run-Engine @("--profile", $rtIl2Profile, "il2", "export", "--path", $rtIl2Root, "--mode", "add", "--nuke-untouched-devices", "true")
    Assert-True ($rtIl2Add.ExitCode -eq 0) ("round trip: il2 add export failed: " + $rtIl2Add.Output)
    $rtIl2AfterAdd = Rt-Il2Lines $rtIl2Actions
    Assert-True (@($rtIl2Before | Where-Object { $rtIl2AfterAdd -notcontains $_ }).Count -eq 0) "round trip: il2 add must not remove any line (not even with nuke)."
    Assert-True (-not ($rtIl2AfterAdd -contains "gear|joy0_b9|0")) "round trip: il2 add must not put a second action on the game's input."
    Assert-True ($rtIl2AfterAdd -contains "bc_head_cvc|joy0_b5|0") "round trip: il2 add must write the moved action on its new button."
    $rtIl2Skipped = @((Rt-Json $rtIl2Add.Output).SkippedTakenInputs | Where-Object { $_.Action -eq "gear" -and $_.GameAction -eq "bc_head_cfovr" -and $_.Input -eq "JOY_BTN10" })
    Assert-True ($rtIl2Skipped.Count -eq 1) ("round trip: il2 add must report the bind it left out: " + $rtIl2Add.Output)
    # Merge: the game's bind on the same input goes (conflict); the moved action keeps its old button.
    $rtIl2Plain = Run-Engine @("--profile", $rtIl2Profile, "il2", "export", "--path", $rtIl2Root, "--mode", "merge", "--text")
    Assert-True ($rtIl2Plain.ExitCode -eq 0) ("round trip: il2 merge export failed: " + $rtIl2Plain.Output)
    $rtIl2AfterMerge = Rt-Il2Lines $rtIl2Actions
    Assert-True ($rtIl2AfterMerge -contains "gear|joy0_b9|0") "round trip: il2 merge must write the profile's action on the game's input."
    Assert-True (-not ($rtIl2AfterMerge -contains "bc_head_cfovr|joy0_b9|0")) "round trip: il2 merge must remove the game's bind on the same input (conflict)."
    Assert-True ($rtIl2AfterMerge -contains "bc_head_cvc|joy0_b1|0") "round trip: il2 merge must keep the moved action's old button."
    Assert-SetEquivalent @($rtIl2AfterAdd | Where-Object { $rtIl2AfterMerge -notcontains $_ }) @("bc_head_cfovr|joy0_b9|0") "round trip: il2 merge removes only the conflict"
    # Update, my profile wins: the moved action leaves its old button; mouse lines stay.
    $rtIl2Overwrite = Run-Engine @("--profile", $rtIl2Profile, "il2", "export", "--path", $rtIl2Root, "--mode", "merge-overwrite", "--text")
    Assert-True ($rtIl2Overwrite.ExitCode -eq 0) ("round trip: il2 merge-overwrite export failed: " + $rtIl2Overwrite.Output)
    $rtIl2AfterOverwrite = Rt-Il2Lines $rtIl2Actions
    Assert-True ($rtIl2AfterOverwrite -contains "bc_head_cvc|joy0_b5|0") "round trip: il2 merge-overwrite must write the moved action on its new button."
    Assert-True (-not ($rtIl2AfterOverwrite -contains "bc_head_cvc|joy0_b1|0")) "round trip: il2 merge-overwrite must remove the moved action from its old button."
    Assert-True ($rtIl2AfterOverwrite -contains "gear|joy2_b3|0") "round trip: il2 merge-overwrite must keep gear on the device that is not connected."
    Assert-True ($rtIl2AfterOverwrite -contains "cam_zoom|key_lshift+mouse_axis_z|0") "round trip: il2 merge-overwrite must keep mouse lines."

    # ---- Star Citizen: the game's default joystick binds on js1 come in and are not written back -----------------
    # Test Stick (js1) is connected, Gone Throttle (js2) is not; the on-foot map "player" and mouse rebinds are not
    # JoyPro's and stay byte-identical in every mode.
    $env:JOYPRO_FAKE_DEVICES = Join-Path $rtFixtures "starcitizen-devices.tsv"
    Copy-Item -Path (Join-Path $rtFixtures "starcitizen\catalog.xml") -Destination $starCitizenDbCatalogPath -Force
    $rtScDir = Join-Path $tempDir "rt-sc\default"
    New-Item -Path $rtScDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $rtFixtures "starcitizen\actionmaps.xml") -Destination $rtScDir -Force
    $rtSc = Join-Path $rtScDir "actionmaps.xml"
    $rtScBefore = [System.IO.File]::ReadAllText($rtSc)
    $rtScPlayer = { param($text) ([regex]::Match($text, '(?s)<actionmap name="player">.*?</actionmap>')).Value }
    $rtScPlayerBefore = & $rtScPlayer $rtScBefore
    $rtScProfile = Join-Path $tempDir "rt-sc.pr0file"
    $rtScImport = Run-Engine @("--profile", $rtScProfile, "starcitizen", "import", "--path", $rtScDir, "--text")
    Assert-True ($rtScImport.ExitCode -eq 0) ("round trip: starcitizen import failed: " + $rtScImport.Output)
    # v_fire (button1) and v_pitch (y) act on js1; v_roll's default is switched off ("js1_ "), v_boost is rebound,
    # v_yaw sits on js2 (not connected; the game's file names the device).
    Assert-Match $rtScImport.Output "(?m)^Imported Binds:\s+4\s*$" ("round trip: starcitizen import should bring in the two active defaults, the rebind and the js2 bind: " + $rtScImport.Output)
    $rtScBinds = (Rt-Json (Run-Engine @("--profile", $rtScProfile, "binds", "list")).Output).Binds
    Assert-True (@($rtScBinds | Where-Object { $_.JAxis -eq "JOY_Y" -and $_.Joystick -like "Test Stick*" }).Count -eq 1) "round trip: starcitizen import should take v_pitch's default on js1 (y)."
    Assert-True (@($rtScBinds | Where-Object { $_.JAxis -eq "JOY_X" }).Count -eq 0) "round trip: a default switched off in the game (js1_ ) must not come in."
    foreach ($rtScOrder in @("false", "true")) {
        $rtScExport = Run-Engine @("--profile", $rtScProfile, "starcitizen", "export", "--path", $rtScDir, "--mode", "clean", "--nuke-untouched-devices", "true", "--prefer-local-order", $rtScOrder, "--text")
        Assert-True ($rtScExport.ExitCode -eq 0) ("round trip: starcitizen clean + nuke export failed: " + $rtScExport.Output)
        Assert-True ([System.IO.File]::ReadAllText($rtSc) -eq $rtScBefore) ("round trip: starcitizen import + clean + nuke export (prefer local order " + $rtScOrder + ") must keep actionmaps.xml byte-identical (slots, no rebind for a default, no default switched off): " + [System.IO.File]::ReadAllText($rtSc))
    }

    # Export modes. The profile drops v_boost, puts v_target_cycle on js1_button7 (the game's v_boost) and moves v_fire
    # from its default button1 to button9. Star Citizen has one joystick slot per action: a move replaces it in every
    # mode that writes.
    $rtScFire = $rtScBinds | Where-Object { $_.JButton -eq "JOY_BTN1" } | Select-Object -First 1
    Run-Engine @("--profile", $rtScProfile, "binds", "set", $rtScFire.RelationName, "--input", "JOY_BTN9") | Out-Null
    $rtScBoost = $rtScBinds | Where-Object { $_.JButton -eq "JOY_BTN7" } | Select-Object -First 1
    Run-Engine @("--profile", $rtScProfile, "binds", "clear", $rtScBoost.RelationName) | Out-Null
    Run-Engine @("--profile", $rtScProfile, "relations", "add", "SC Target", "--button", "--node", "StarCitizen:spaceship_movement|v_target_cycle:StarCitizen", "--device", "Test Stick {11111111-1111-1111-1111-111111111111}", "--input", "JOY_BTN7", "--text") | Out-Null
    # Only add: nothing changes; v_fire's slot holds its active default, v_target_cycle's input is v_boost's (reported).
    $rtScAdd = Run-Engine @("--profile", $rtScProfile, "starcitizen", "export", "--path", $rtScDir, "--mode", "add", "--nuke-untouched-devices", "true", "--prefer-local-order", "false")
    Assert-True ($rtScAdd.ExitCode -eq 0) ("round trip: starcitizen add export failed: " + $rtScAdd.Output)
    Assert-True ([System.IO.File]::ReadAllText($rtSc) -eq $rtScBefore) ("round trip: starcitizen add must not change anything here (not even with nuke): " + [System.IO.File]::ReadAllText($rtSc))
    $rtScSkipped = @((Rt-Json $rtScAdd.Output).SkippedTakenInputs | Where-Object { $_.Action -match "v_target_cycle" -and $_.GameAction -match "v_boost" })
    Assert-True ($rtScSkipped.Count -eq 1) ("round trip: starcitizen add must report the bind it left out: " + $rtScAdd.Output)
    # Merge: v_target_cycle takes js1_button7, v_boost's rebind becomes "js1_ " (its default button2 must not come
    # back), v_fire's slot gets button9; js2, the player map and the mouse stay.
    $rtScMerge = Run-Engine @("--profile", $rtScProfile, "starcitizen", "export", "--path", $rtScDir, "--mode", "merge", "--prefer-local-order", "false", "--text")
    Assert-True ($rtScMerge.ExitCode -eq 0) ("round trip: starcitizen merge export failed: " + $rtScMerge.Output)
    $rtScMerged = [System.IO.File]::ReadAllText($rtSc)
    Assert-Match $rtScMerged '<action name="v_target_cycle">\s*<rebind input="js1_button7"/>' "round trip: starcitizen merge must write the profile's action on the game's input."
    Assert-Match $rtScMerged '<action name="v_boost">\s*<rebind input="js1_ "/>' ("round trip: starcitizen merge must release the game's bind on the same input (conflict): " + $rtScMerged)
    Assert-Match $rtScMerged '<action name="v_fire">\s*<rebind input="js1_button9"/>' "round trip: starcitizen merge must write the moved default as a rebind."
    Assert-Match $rtScMerged '<rebind input="js2_rotz"/>' "round trip: starcitizen merge must keep the bind of the device that is not connected."
    Assert-True ((& $rtScPlayer $rtScMerged) -eq $rtScPlayerBefore) "round trip: starcitizen merge must leave the on-foot map byte-identical."
    $rtScOverwrite = Run-Engine @("--profile", $rtScProfile, "starcitizen", "export", "--path", $rtScDir, "--mode", "merge-overwrite", "--nuke-untouched-devices", "true", "--prefer-local-order", "false", "--text")
    Assert-True ($rtScOverwrite.ExitCode -eq 0) ("round trip: starcitizen merge-overwrite export failed: " + $rtScOverwrite.Output)
    $rtScOver = [System.IO.File]::ReadAllText($rtSc)
    Assert-True ($rtScOver -eq $rtScMerged) ("round trip: starcitizen merge-overwrite after merge must not change more (one slot per action, js2 not connected): " + $rtScOver)
    Assert-True ((& $rtScPlayer $rtScOver) -eq $rtScPlayerBefore) "round trip: starcitizen merge-overwrite must leave the on-foot map byte-identical."

    # ---- Nuclear Option: the saved map of a device that is not connected stays; no empty map for a new device -----
    $env:JOYPRO_FAKE_DEVICES = Join-Path $rtFixtures "nuclearoption-devices.tsv"
    $rtNoDir = Join-Path $tempDir "rt-nuclearoption"
    New-Item -Path $rtNoDir -ItemType Directory -Force | Out-Null
    $rtNo = Join-Path $rtNoDir "registry.json"
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\nuclearoption\registry.json") -Destination $rtNo -Force
    $rtNoBefore = Rt-Registry $rtNo
    $rtNoProfile = Join-Path $tempDir "rt-nuclearoption.pr0file"
    $rtNoImport = Run-Engine @("--profile", $rtNoProfile, "nuclearoption", "import", "--path", $rtNo, "--replace", "--text")
    Assert-True ($rtNoImport.ExitCode -eq 0) ("round trip: nuclearoption import failed: " + $rtNoImport.Output)
    $rtNoExport = Run-Engine @("--profile", $rtNoProfile, "nuclearoption", "export", "--path", $rtNo, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($rtNoExport.ExitCode -eq 0) ("round trip: nuclearoption clean + nuke export failed: " + $rtNoExport.Output)
    $rtNoAfter = Rt-Registry $rtNo
    Assert-SetEquivalent @($rtNoAfter.Keys) @($rtNoBefore.Keys) "round trip: nuclearoption clean + nuke must not add or remove stored values (no empty map for a device the game has no map of)"
    foreach ($name in $rtNoBefore.Keys) {
        Assert-True ($rtNoAfter[$name] -eq $rtNoBefore[$name]) ("round trip: nuclearoption clean + nuke changed '" + $name + "': " + $rtNoExport.Output)
    }
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $rtPreviousFakeDevices
}
