# The virtual Xbox pad beyond one game (docs/virtual-joystick.md "Virtual Xbox pad"). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Fake vJoy/ViGEm/HidHide, fake devices, a temp
# JoyPro data folder and the synthetic ACE COMBAT 8 save (tests\fixtures\acecombat8); nothing real is read or changed.
#  1. A game that reads only Xbox controllers (ACE COMBAT 8, Hell Let Loose: Vietnam) is on the pad without a mode
#     chosen: status, plan, forwarder-config; ACE COMBAT 7 (reads a joystick) stays native.
#  2. A hat direction whose action the game has on a pad button drives that button; an axis on actions the game has
#     only on pad buttons (yaw on LB/RB) gets JoyPro's "Axis ends to buttons" function instead of a stick.
#  3. The relation's axis curve for the game goes to the forwarder (the pad's games get no export); binds tuning show
#     names the pad games.
#  4. An explicit choice wins and persists; a pad slot no relation feeds any more goes without --reset.
#  5. Several games on the pad: each keeps its own table, the pick decides which one goes live, nothing is dropped.
#  6. Function outputs follow the game's target (Xbox pad controls for a pad game, retargeted on a switch).
$xpDir = Join-Path $tempDir "xboxpad"
$xpFake = Join-Path $xpDir "fake"
New-Item -ItemType Directory -Force -Path $xpFake | Out-Null
$xpCatalogDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\AceCombat8"
$xpCatalogBackup = Join-Path $xpDir "catalog-backup"
$xpHadCatalog = Test-Path $xpCatalogDir
if ($xpHadCatalog) {
    Copy-Item -Path $xpCatalogDir -Destination $xpCatalogBackup -Recurse -Force
}
[System.IO.File]::WriteAllText((Join-Path $xpFake "vjoy.txt"), "installed=2.1.9.1`r`nvigem=1.22.0`r`n")
[System.IO.File]::WriteAllText((Join-Path $xpFake "hidhide-state.txt"), "--cloak-off`r`n--inv-on`r`n")
$xpPrevious = @{ Fake = $env:JOYPRO_VJOY_FAKE; Devices = $env:JOYPRO_FAKE_DEVICES; Detection = $env:JOYPRO_INSTALL_DETECTION }
$env:JOYPRO_VJOY_FAKE = $xpFake
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\vjoy\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = "off"
$xpStick = "VJ Stick {11111111-2222-3333-4444-000000000001}"
$xpThrottle = "VJ Throttle {11111111-2222-3333-4444-000000000002}"
$xpProfile = Join-Path $xpDir "pad.pr0file"
$xpSave = Join-Path $xpDir "System.sav"
Copy-Item (Join-Path $engineDir "tests\fixtures\acecombat8\System.sav") $xpSave
function Get-XpGame($status, $id) { return @($status.Games | Where-Object { $_.Id -eq $id })[0] }
try {
    $xpRefresh = Run-Engine @("ac8", "refresh-db", $xpSave, "--text")
    Assert-True ($xpRefresh.ExitCode -eq 0) "ac8 refresh-db failed. Output: $($xpRefresh.Output)"
    Run-Engine @("--profile", $xpProfile, "relations", "add", "Pitch", "--axis", "--node", "AceCombat8:Pad_Pitch", "--device", $xpStick, "--input", "JOY_Y", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "add", "Roll", "--axis", "--node", "AceCombat8:Pad_Roll", "--device", $xpStick, "--input", "JOY_X", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "add", "Gun", "--button", "--node", "AceCombat8:Pad_FireMachineGun", "--device", $xpStick, "--input", "JOY_BTN1", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "add", "Target", "--button", "--node", "AceCombat8:Pad_SwitchTarget", "--device", $xpStick, "--input", "JOY_BTN_POV1_R", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "add", "Yaw", "--axis", "--node", "AceCombat8:Pad_YawLeft", "--device", $xpStick, "--input", "JOY_RZ", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "item", "add", "Yaw", "--node", "AceCombat8:Pad_YawRight", "--force", "--text") | Out-Null

    # ---- 1. on the pad by itself
    $xpStatus = (Run-Engine @("--profile", $xpProfile, "vjoy", "status")).Output | ConvertFrom-Json
    $xpAc8 = Get-XpGame $xpStatus "AceCombat8"
    Assert-True ($xpAc8.Mode -eq "xbox" -and $xpAc8.AutoMode -eq $true -and $xpAc8.ReadsOnlyXbox -eq $true) "ACE COMBAT 8 without a mode chosen must be on the Xbox pad by itself."
    $xpHll = Get-XpGame $xpStatus "HLLVietnam"
    Assert-True ($xpHll.Mode -eq "xbox" -and $xpHll.AutoMode -eq $true) "Hell Let Loose: Vietnam reads only Xbox controllers: Xbox pad by itself."
    $xpAc7 = Get-XpGame $xpStatus "AceCombat7"
    Assert-True ($xpAc7.Mode -eq "native" -and $xpAc7.AutoMode -eq $false) "ACE COMBAT 7 reads a joystick: it must stay native."
    Assert-True ($null -ne (@($xpStatus.XboxGames) | Where-Object { $_.Game -eq "AceCombat8" })) "The status must carry ACE COMBAT 8's pad table."
    $xpPlan = Run-Engine @("--profile", $xpProfile, "vjoy", "plan", "--text")
    Assert-Match $xpPlan.Output "Xbox games: .*AceCombat8" "plan must count ACE COMBAT 8 as an Xbox game."

    # ---- 2. a hat direction on a pad button, yaw from an axis
    $xpMap = Run-Engine @("--profile", $xpProfile, "vjoy", "map", "--text")
    Assert-True ($xpMap.ExitCode -eq 0) "vjoy map failed. Output: $($xpMap.Output)"
    Assert-Match $xpMap.Output "JOY_BTN_POV1_R -> Xbox XB_(A|B|X|Y) " "A hat direction on Switch target must drive the button the game has it on."
    Assert-NotMatch $xpMap.Output "POV1 -> Xbox XB_DPAD" "The whole hat must not go to the D-pad."
    Assert-NotMatch $xpMap.Output "JOY_RZ -> Xbox" "The yaw axis must not take a stick or trigger."
    # A game on the pad only by itself that the profile does not use (Hell Let Loose: Vietnam here) stays out of the map.
    Assert-NotMatch $xpMap.Output "HLLVietnam" ("vjoy map must not list a pad game the profile does not use. Output: " + $xpMap.Output)
    Assert-NotMatch $xpMap.Output "catalog not found" ("vjoy map must not read the catalog of an unused pad game. Output: " + $xpMap.Output)
    Assert-Match $xpMap.Output "JoyPro added the function" "The yaw axis on LB/RB must get JoyPro's axis-buttons function."
    $xpFunctions = Run-Engine @("--profile", $xpProfile, "vjoy", "functions", "list", "--game", "AceCombat8", "--text")
    Assert-Match $xpFunctions.Output "auto-pad-" "The function must be listed for ACE COMBAT 8."
    Assert-Match $xpFunctions.Output "LB" "Yaw left on LB."
    Assert-Match $xpFunctions.Output "RB" "Yaw right on RB."

    # ---- 3. the curve on the pad
    $xpSet = Run-Engine @("--profile", $xpProfile, "binds", "tuning", "set", "Roll", "--scope", "game:AceCombat8", "--deadzone", "0.2", "--text")
    Assert-True ($xpSet.ExitCode -eq 0) "binds tuning set game:AceCombat8 failed. Output: $($xpSet.Output)"
    $xpTable = Join-Path $xpDir "forwarder.json"
    $xpConfig = Run-Engine @("--profile", $xpProfile, "vjoy", "forwarder-config", "--out", $xpTable, "--text")
    Assert-True ($xpConfig.ExitCode -eq 0) "forwarder-config failed. Output: $($xpConfig.Output)"
    $xpTableObject = Get-Content $xpTable -Raw | ConvertFrom-Json
    Assert-True ($xpTableObject.xboxGame -eq "AceCombat8") "The pad table must be ACE COMBAT 8's (no mode chosen)."
    $xpRoll = @($xpTableObject.xbox | Where-Object { $_.kind -eq "axis" -and $_.index -eq 0 })[0]
    Assert-True ($null -ne $xpRoll -and $xpRoll.target -eq "XB_LX") "Roll must be on the left stick X. Table: $(Get-Content $xpTable -Raw)"
    Assert-True (@($xpRoll.curve).Count -eq 1025) "The roll slot must carry the curve (1025 points)."
    Assert-True ([Math]::Abs([double]$xpRoll.curve[563]) -lt 1e-9 -and [double]$xpRoll.curve[1024] -gt 0.99) "The curve must have the game's dead zone 0.2 and reach the end."
    $xpHatDir = @($xpTableObject.xbox | Where-Object { $_.kind -eq "hatdir" })[0]
    Assert-True ($null -ne $xpHatDir -and $xpHatDir.direction -eq 2) "The hat direction slot must go to the forwarder as hatdir, direction 2 (right)."
    Assert-True ($null -ne (@($xpTableObject.functions) | Where-Object { $_.Id -like "auto-pad-*" })) "The yaw function must run in the forwarder."
    $xpShow = (Run-Engine @("--profile", $xpProfile, "binds", "tuning", "show", "Roll")).Output
    $xpShowObject = $xpShow.Substring($xpShow.IndexOf("{")) | ConvertFrom-Json
    Assert-True (@($xpShowObject.XboxPadGames) -contains "AceCombat8") "binds tuning show must name ACE COMBAT 8 as a pad game."

    # ---- 4. the user's choice wins; stale pad slots go
    $xpNative = Run-Engine @("--profile", $xpProfile, "vjoy", "mode", "AceCombat8", "native", "--text")
    Assert-True ($xpNative.ExitCode -eq 0) "vjoy mode AceCombat8 native failed. Output: $($xpNative.Output)"
    $xpStatus = (Run-Engine @("--profile", $xpProfile, "vjoy", "status")).Output | ConvertFrom-Json
    $xpAc8 = Get-XpGame $xpStatus "AceCombat8"
    Assert-True ($xpAc8.Mode -eq "native" -and $xpAc8.AutoMode -eq $false) "An explicit native must win over the Xbox pad and persist."
    Run-Engine @("--profile", $xpProfile, "vjoy", "mode", "AceCombat8", "xbox", "--text") | Out-Null
    # The relation keeps its hat but no ACE COMBAT 8 action any more (the live test's stale D-pad).
    Run-Engine @("--profile", $xpProfile, "relations", "item", "add", "Target", "--node", "IL2Game:smoke_target", "--force", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "relations", "item", "remove", "Target", "--node", "AceCombat8:Pad_SwitchTarget", "--text") | Out-Null
    $xpMap = Run-Engine @("--profile", $xpProfile, "vjoy", "map", "--text")
    Assert-NotMatch $xpMap.Output "JOY_BTN_POV1_R -> Xbox" "A pad slot no relation feeds any more must go without --reset."

    # ---- 5. several games on the pad
    $xpAc7Mode = Run-Engine @("--profile", $xpProfile, "vjoy", "mode", "AceCombat7", "xbox", "--text")
    Assert-Match $xpAc7Mode.Output "Games on the Xbox pad: .*ACE COMBAT 7.*ACE COMBAT 8|Games on the Xbox pad: .*ACE COMBAT 8.*ACE COMBAT 7" ("Switching a second game to the pad must name both. Output: " + $xpAc7Mode.Output)
    $xpAc7Fn = Run-Engine @("--profile", $xpProfile, "vjoy", "functions", "add", "--game", "AceCombat7", "--type", "toggle", "--input", ("in=" + $xpThrottle + "|JOY_BTN5"), "--text")
    Assert-True ($xpAc7Fn.ExitCode -eq 0) "functions add for ACE COMBAT 7 failed. Output: $($xpAc7Fn.Output)"
    $xpPick = Run-Engine @("--profile", $xpProfile, "vjoy", "settings", "--xbox-pad-game", "AceCombat8", "--text")
    Assert-True ($xpPick.ExitCode -eq 0) "settings --xbox-pad-game failed. Output: $($xpPick.Output)"
    $xpConfig = Run-Engine @("--profile", $xpProfile, "vjoy", "forwarder-config", "--out", $xpTable, "--text")
    Assert-True ((Get-Content $xpTable -Raw | ConvertFrom-Json).xboxGame -eq "AceCombat8") "The pick must decide the live pad table."
    Assert-True (@((Get-Content $xpTable -Raw | ConvertFrom-Json).xboxWaiting) -contains "AceCombat7") "The other pad game (with a function) must be named as waiting, not dropped."
    Run-Engine @("--profile", $xpProfile, "vjoy", "settings", "--xbox-pad-game", "AceCombat7", "--text") | Out-Null
    Run-Engine @("--profile", $xpProfile, "vjoy", "forwarder-config", "--out", $xpTable, "--text") | Out-Null
    Assert-True ((Get-Content $xpTable -Raw | ConvertFrom-Json).xboxGame -eq "AceCombat7") "The other pick must take ACE COMBAT 7's table."
    Run-Engine @("--profile", $xpProfile, "vjoy", "forwarder-config", "--out", $xpTable, "--xbox-game", "AceCombat8", "--text") | Out-Null
    Assert-True ((Get-Content $xpTable -Raw | ConvertFrom-Json).xboxGame -eq "AceCombat8") "--xbox-game (the running game) must win over the pick."
    $xpStatus = (Run-Engine @("--profile", $xpProfile, "vjoy", "status")).Output | ConvertFrom-Json
    Assert-True ((Get-XpGame $xpStatus "AceCombat8").Mode -eq "xbox" -and (Get-XpGame $xpStatus "AceCombat7").Mode -eq "xbox") "Both games must stay on the pad."
    $badPick = Run-Engine @("--profile", $xpProfile, "vjoy", "settings", "--xbox-pad-game", "DCS", "--text")
    Assert-True ($badPick.ExitCode -ne 0) "A game not on the pad must be refused as the pick."

    # ---- 6. function outputs follow the target
    $xpHat = Run-Engine @("--profile", $xpProfile, "vjoy", "functions", "add", "--game", "AceCombat8", "--type", "hat-buttons", "--input", ("hat=" + $xpStick + "|POV1"), "--text")
    Assert-True ($xpHat.ExitCode -eq 0) "functions add hat-buttons failed. Output: $($xpHat.Output)"
    Assert-NotMatch $xpHat.Output "vJoy 1 " "A pad game's function must not get vJoy outputs."
    Assert-Match $xpHat.Output "XB_|Xbox|D-pad|LB|RB|Back|Start" "A pad game's function must get pad controls."
    Run-Engine @("--profile", $xpProfile, "vjoy", "functions", "add", "--game", "IL2Game", "--type", "toggle", "--input", ("in=" + $xpThrottle + "|JOY_BTN9"), "--text") | Out-Null
    $xpIl2 = Run-Engine @("--profile", $xpProfile, "vjoy", "mode", "IL2Game", "xbox", "--text")
    Assert-Match $xpIl2.Output "out -> " "Switching to the pad must retarget the function's vJoy output."
    $xpIl2List = Run-Engine @("--profile", $xpProfile, "vjoy", "functions", "list", "--game", "IL2Game", "--text")
    Assert-NotMatch $xpIl2List.Output "vJoy 1 JOY_BTN" "No vJoy output may stay on a pad game's function."
    Run-Engine @("--profile", $xpProfile, "vjoy", "mode", "IL2Game", "native", "--text") | Out-Null

    # ---- 7. the auto setup from the live test, for any profile (needs the DCS list for the function map)
    if (-not (Skip-WithoutGameData "smoke\xboxpad.ps1: auto setup")) {
        $asProfile = Join-Path $xpDir "auto.pr0file"
        $asPedals = "MFG Crosswind {11111111-2222-3333-4444-000000000009}"
        function Add-AsRelation([string[]]$Rest) { $r = Run-Engine (@("--profile", $asProfile, "relations", "add") + $Rest + @("--text")); Assert-True ($r.ExitCode -eq 0) ("relations add failed: " + ($Rest -join " ") + " Output: " + $r.Output) }
        Add-AsRelation @("Throttle", "--axis", "--node", "DCS:a2004cdnil:F-86F Sabre", "--device", $xpThrottle, "--input", "JOY_Z")
        Add-AsRelation @("Toe brakes", "--axis", "--node", "DCS:a2112cdnil:F-16C_50", "--device", $xpThrottle, "--input", "JOY_RX")
        # relations suggest puts ACE COMBAT 8's throttle down into the throttle relation, not the brakes.
        $asSuggest = Run-Engine @("--profile", $asProfile, "relations", "suggest", "Throttle", "--game", "AceCombat8", "--min", "low", "--text")
        Assert-Match $asSuggest.Output "Pad_ThrottleDown" ("relations suggest must offer Pad_ThrottleDown for the throttle. Output: " + $asSuggest.Output)
        $asSuggestBrake = Run-Engine @("--profile", $asProfile, "relations", "suggest", "Toe brakes", "--game", "AceCombat8", "--min", "low", "--text")
        Assert-NotMatch $asSuggestBrake.Output "Pad_ThrottleDown" ("relations suggest must not put Pad_ThrottleDown into a wheel brake relation. Output: " + $asSuggestBrake.Output)
        Run-Engine @("--profile", $asProfile, "relations", "item", "add", "Throttle", "--node", "AceCombat8:Pad_ThrottleUp", "--text") | Out-Null
        # The owner's case: the decelerate action sits in the toe brake relation.
        Run-Engine @("--profile", $asProfile, "relations", "item", "add", "Toe brakes", "--node", "AceCombat8:Pad_ThrottleDown", "--text") | Out-Null
        Add-AsRelation @("Rudder", "--axis", "--node", "DCS:a2003cdnil:A-10C", "--device", $asPedals, "--input", "JOY_RZ")
        Add-AsRelation @("Twist", "--axis", "--node", "DCS:a2003cdnil:F-16C_50", "--device", $xpStick, "--input", "JOY_RZ")
        Add-AsRelation @("CMS forward", "--button", "--node", "DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN3")
        Add-AsRelation @("Attack my target", "--button", "--node", "DCS:d118pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN_POV2_U")
        Add-AsRelation @("Cover me", "--button", "--node", "DCS:d119pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN_POV2_R")
        Add-AsRelation @("Rejoin", "--button", "--node", "DCS:d115pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN_POV2_D")
        Add-AsRelation @("RTB", "--button", "--node", "DCS:d1685pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN_POV2_L")
        Add-AsRelation @("Pitch", "--axis", "--node", "AceCombat8:Pad_Pitch", "--device", $xpStick, "--input", "JOY_Y")
        # The must-haves pad-check asks for besides the throttle: roll, gun, missile, target switch.
        Add-AsRelation @("Roll", "--axis", "--node", "AceCombat8:Pad_Roll", "--device", $xpStick, "--input", "JOY_X")
        Add-AsRelation @("Gun", "--button", "--node", "AceCombat8:Pad_FireMachineGun", "--device", $xpStick, "--input", "JOY_BTN1")
        $asNoMissile = (Run-Engine @("--profile", $asProfile, "vjoy", "pad-check", "--game", "AceCombat8")).Output | ConvertFrom-Json
        Assert-True (@(@($asNoMissile.Games)[0].Checks | Where-Object { $_.Name -eq "missile" -and $_.Ok -eq $false }).Count -eq 1) "pad-check must miss the missile when no relation has it."
        Add-AsRelation @("Missile", "--button", "--node", "AceCombat8:Pad_FireMissile", "--device", $xpStick, "--input", "JOY_BTN2")
        Add-AsRelation @("Change target", "--button", "--node", "AceCombat8:Pad_SwitchTarget", "--device", $xpStick, "--input", "JOY_BTN_POV1_R")
        $asMap = Run-Engine @("--profile", $asProfile, "vjoy", "map", "--text")
        Assert-Match $asMap.Output "High-G turn = Left trigger \+ Right trigger fully pressed\. Set my devices automatically puts it on a button" "Without a High-G relation the hold must be offered."
        # A button relation named "High-G" holds both triggers (the owner's live test).
        Add-AsRelation @("High-G", "--button", "--node", "DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50", "--device", $xpStick, "--input", "JOY_BTN4")
        $asFirst = $asMap
        $asMap = Run-Engine @("--profile", $asProfile, "vjoy", "map", "--text")
        Assert-Match $asMap.Output "High-G turn = Left trigger \+ Right trigger fully pressed while High-G" ("The High-G button must hold both triggers. Output: " + $asMap.Output)
        $asMap = $asFirst
        Assert-True ($asMap.ExitCode -eq 0) "vjoy map (auto setup) failed. Output: $($asMap.Output)"
        Assert-Match $asMap.Output "JOY_Z -> Xbox XB_TRIGGERS" ("The throttle must split both triggers. Output: " + $asMap.Output)
        Assert-NotMatch $asMap.Output "JOY_RX -> Xbox" "The toe brake axis must not take a trigger."
        Assert-Match $asMap.Output "Toe brakes .* gets no trigger" "The brake axis giving way must be said."
        Assert-Match $asMap.Output "JoyPro feeds them from the stick twist \(Twist" "Yaw must come from the stick twist when there is one."
        Assert-Match $asMap.Output "Flares = Left stick click \+ Right stick click together \(not confirmed in game yet\)" "The flare chord must be mapped, marked unconfirmed."
        Assert-Match $asMap.Output "CMS forward" "The flare chord must come from the CMS forward relation."
        Assert-Match $asMap.Output "menu commands on the D-pad" "The wingman D-pad must come from the AI-menu hat."
        # Look around (the right stick) with no axis on it and no free hat (POV1 has Change target, POV2 the wingmen): missed.
        $asNoLook = (Run-Engine @("--profile", $asProfile, "vjoy", "pad-check", "--game", "AceCombat8")).Output | ConvertFrom-Json
        Assert-True (@(@($asNoLook.Games)[0].Checks | Where-Object { $_.Name -eq "look" -and $_.Ok -eq $false }).Count -eq 1) "pad-check must miss look around when no axis or free hat feeds it."
        # Hats the profile trims with in DCS (the owner, 2026-11-14: look on the topmost hat of his stick): the stick's hat
        # wins over the throttle's (the stick carries pitch, roll and gun); the hat keeps trimming in DCS.
        Add-AsRelation @("Trim nose down", "--button", "--node", "DCS:dnilp95u215cdnilvdnilvpnilvunil:F-16C_50", "--device", $xpThrottle, "--input", "JOY_BTN_POV1_U")
        Add-AsRelation @("Trim (stick)", "--button", "--node", "DCS:dnilp96u215cdnilvdnilvpnilvunil:F-15C", "--device", $xpStick, "--input", "JOY_BTN_POV3_U")
        $asLook = Run-Engine @("--profile", $asProfile, "vjoy", "map", "--text")
        Assert-Match $asLook.Output "looks around with Right stick X / Right stick Y: .*hat POV3 of VJ Stick" ("Look around must go on the stick's free hat. Output: " + $asLook.Output)
        $asCheck = (Run-Engine @("--profile", $asProfile, "vjoy", "pad-check", "--game", "AceCombat8")).Output | ConvertFrom-Json
        $asGame = @($asCheck.Games)[0]
        Assert-True (@($asGame.Checks | Where-Object { $_.Name -eq "look" -and $_.Ok -eq $true }).Count -eq 1) ("pad-check must find look around fed. " + ($asGame.Checks | ConvertTo-Json -Compress))
        Assert-True ($asGame.Score -eq 1) ("pad-check must reach everything. " + ($asGame.Checks | ConvertTo-Json -Compress))
        $asTable = Join-Path $xpDir "auto-forwarder.json"
        Run-Engine @("--profile", $asProfile, "vjoy", "forwarder-config", "--out", $asTable, "--text") | Out-Null
        $asConfig = Get-Content $asTable -Raw | ConvertFrom-Json
        $asYaw = @($asConfig.functions | Where-Object { $_.Type -eq "axis-buttons" })[0]
        Assert-True ($null -ne $asYaw -and [double]$asYaw.Params.threshold -eq 0.35) "The yaw function must fire at the 0.35 end zone."
        $asChord = @($asConfig.functions | Where-Object { $_.Type -eq "macro" })[0]
        Assert-True ($null -ne $asChord -and [double]$asChord.Params.together -eq 1) "The flare macro must press its steps together."
        Assert-True ($null -ne (@($asConfig.functions) | Where-Object { $_.Type -eq "buttons-hat" })) "The D-pad function must run."
        $asLookFn = @($asConfig.functions | Where-Object { $_.Type -eq "hat-axes" })[0]
        Assert-True ($null -ne $asLookFn -and @($asLookFn.Inputs | Where-Object { $_.Input -eq "POV3" }).Count -eq 1 -and
                     @($asLookFn.Outputs | Where-Object { ($_.Role -eq "x" -and $_.Target -eq "XB_RX") -or ($_.Role -eq "y" -and $_.Target -eq "XB_RY") }).Count -eq 2) ("The look function must run: POV3 -> right stick. " + ($asLookFn | ConvertTo-Json -Compress -Depth 5))
        Assert-True ($null -ne (@($asConfig.functions) | Where-Object { $_.Type -eq "remap" -and @($_.Outputs | Where-Object { $_.Target -eq "XB_LT" -or $_.Target -eq "XB_RT" }).Count -eq 2 })) "The High-G remap must run on both triggers."
        # Without a twist: the rudder pedals.
        Run-Engine @("--profile", $asProfile, "relations", "mass", "delete", "--relation", "Twist", "--text") | Out-Null
        $asMap = Run-Engine @("--profile", $asProfile, "vjoy", "map", "--text")
        Assert-Match $asMap.Output "JoyPro feeds them from the rudder pedals \(Rudder" ("Without a twist yaw must come from the pedals. Output: " + $asMap.Output)

        # ---- 7b. ACE COMBAT 8 alone on a single-hat HOTAS (round 7): the device's own hats count, not only bound ones;
        # the wizard puts look left / right on the T.Flight's spring-return rocker; no free input at all = n/a (hardware).
        $sfDevices = $env:JOYPRO_FAKE_DEVICES
        try {
            $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\setupwizard\tflight-hotas4.tsv"
            $sfProfile = Join-Path $xpDir "ac8-tflight.pr0file"
            $sfPlan = Join-Path $xpDir "ac8-tflight-plan.json"
            Run-Engine @("profile", "new", $sfProfile) | Out-Null
            $sfPlanned = Run-Engine @("--profile", $sfProfile, "setup", "plan", "--games", "AceCombat8", "--out-plan", $sfPlan, "--text")
            Assert-Match $sfPlanned.Output "JOY_SLIDER1 \(throttle\.axis\.wheel\.1\) => Look left/right" ("Look left / right must go on the T.Flight's rocker. Output: " + $sfPlanned.Output)
            Run-Engine @("--profile", $sfProfile, "setup", "apply", "--plan", $sfPlan, "--text") | Out-Null
            $sfCheck = Run-Engine @("--profile", $sfProfile, "vjoy", "pad-check", "--game", "AceCombat8", "--text")
            Assert-Match $sfCheck.Output "ok   look: Look around is fed on Right stick X" ("pad-check must find look on the rocker. Output: " + $sfCheck.Output)
            # Without the rocker relation and with the hat's relations gone, the catalog hat (bound nowhere) takes look.
            foreach ($sfName in @("Look left/right", "Switch target", "Flares", "Change weapon")) {
                Run-Engine @("--profile", $sfProfile, "relations", "mass", "delete", "--relation", $sfName, "--text") | Out-Null
            }
            $sfMap = Run-Engine @("--profile", $sfProfile, "vjoy", "map", "--text")
            Assert-Match $sfMap.Output "looks around with Right stick X / Right stick Y: .*hat POV1 of T\.Flight Hotas 4" ("Look must go on the stick's catalog hat. Output: " + $sfMap.Output)
            # A one-hat device JoyPro does not know whose hat and axes all hold game functions: look is n/a, no miss.
            $sfSmall = Join-Path $xpDir "small-hotas.tsv"
            Set-Content -Path $sfSmall -Encoding UTF8 -Value @("# JoyPro device`tVID`tPID`tHID product string`taxes`tbuttons`thats", "Small Hotas {e7a1e7a1-0000-0000-0000-782422000009}`t1209`t0F01`tSmall Hotas`tX,Y,RZ,Z`t12`t1")
            $env:JOYPRO_FAKE_DEVICES = $sfSmall
            $sfSmallProfile = Join-Path $xpDir "ac8-small.pr0file"
            $sfSmallPlan = Join-Path $xpDir "ac8-small-plan.json"
            Run-Engine @("profile", "new", $sfSmallProfile) | Out-Null
            Run-Engine @("--profile", $sfSmallProfile, "setup", "plan", "--games", "AceCombat8", "--out-plan", $sfSmallPlan, "--text") | Out-Null
            Run-Engine @("--profile", $sfSmallProfile, "setup", "apply", "--plan", $sfSmallPlan, "--text") | Out-Null
            $sfSmallCheck = Run-Engine @("--profile", $sfSmallProfile, "vjoy", "pad-check", "--game", "AceCombat8", "--text")
            Assert-Match $sfSmallCheck.Output "n/a  look: n/a \(hardware limit\)" ("A one-hat device with nothing free must report look as a hardware limit. Output: " + $sfSmallCheck.Output)
            Assert-NotMatch $sfSmallCheck.Output "(?-i)MISS " ("No miss on the small HOTAS. Output: " + $sfSmallCheck.Output)
        }
        finally {
            $env:JOYPRO_FAKE_DEVICES = $sfDevices
        }

        # ---- 8. regression of the live test: DCS's base invert must not reverse the pad (owner's profile 2026-11-14)
        $rgProfile = Join-Path $xpDir "invert.pr0file"
        $rgVpc = "VPC Throttle {11111111-2222-3333-4444-000000000005}"
        $rgRhino = "VPC Rhino {11111111-2222-3333-4444-000000000006}"
        Run-Engine @("--profile", $rgProfile, "relations", "add", "ThrottleRightNonAB", "--axis", "--node", "DCS:a2004cdnil:F-16C_50", "--device", $rgVpc, "--input", "JOY_RY", "--text") | Out-Null
        Run-Engine @("--profile", $rgProfile, "relations", "item", "add", "ThrottleRightNonAB", "--node", "AceCombat8:Pad_ThrottleUp", "--node", "AceCombat8:Pad_ThrottleDown", "--text") | Out-Null
        Run-Engine @("--profile", $rgProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--device", $rgRhino, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $rgProfile, "relations", "item", "add", "Pitch", "--node", "AceCombat8:Pad_Pitch", "--text") | Out-Null
        foreach ($rgRelation in @("ThrottleRightNonAB", "Pitch")) {
            $rgSet = Run-Engine @("--profile", $rgProfile, "binds", "tuning", "set", $rgRelation, "--scope", "base", "--inverted", "true", "--text")
            Assert-True ($rgSet.ExitCode -eq 0) "binds tuning set base --inverted failed. Output: $($rgSet.Output)"
        }
        $rgTable = Join-Path $xpDir "invert-forwarder.json"
        Run-Engine @("--profile", $rgProfile, "vjoy", "forwarder-config", "--out", $rgTable, "--text") | Out-Null
        $rgConfig = Get-Content $rgTable -Raw | ConvertFrom-Json
        $rgThrottle = @($rgConfig.xbox | Where-Object { $_.kind -eq "axis" -and $_.index -eq 4 })[0]
        Assert-True ($null -ne $rgThrottle -and $rgThrottle.target -eq "XB_TRIGGERS") ("The throttle must split both triggers. Table: " + (Get-Content $rgTable -Raw))
        Assert-True ([double]@($rgThrottle.curve)[1024] -gt 0.99) "DCS's base invert must not reverse the pad throttle (lever forward = RT)."
        $rgPitch = @($rgConfig.xbox | Where-Object { $_.kind -eq "axis" -and $_.index -eq 1 })[0]
        Assert-True ($null -ne $rgPitch -and $rgPitch.target -eq "XB_LY") "Pitch must be on the left stick Y."
        Assert-True ([double]@($rgPitch.curve)[1024] -lt -0.99) "ACE COMBAT 8's pitch is reversed by the game's convention (forward = nose down with the default Pitch Controls)."
        # Flip one game's axis without touching DCS: the game's own scope.
        Run-Engine @("--profile", $rgProfile, "binds", "tuning", "set", "Pitch", "--scope", "game:AceCombat8", "--inverted", "false", "--text") | Out-Null
        Run-Engine @("--profile", $rgProfile, "vjoy", "forwarder-config", "--out", $rgTable, "--text") | Out-Null
        $rgPitch = @((Get-Content $rgTable -Raw | ConvertFrom-Json).xbox | Where-Object { $_.kind -eq "axis" -and $_.index -eq 1 })[0]
        Assert-True ([double]@($rgPitch.curve)[1024] -gt 0.99) "The game's own scope must decide the pad invert."
        # The Cobra button (DCS ASC Direct Control) holds High-G (both triggers) on the pad.
        Run-Engine @("--profile", $rgProfile, "relations", "add", "FBW disconnect", "--button", "--node", "DCS:d121pnilunilcdnilvdnilvpnilvunil:Su-27", "--device", $rgRhino, "--input", "JOY_BTN7", "--text") | Out-Null
        $rgHold = (Run-Engine @("--profile", $rgProfile, "vjoy", "pad-check", "--game", "AceCombat8")).Output | ConvertFrom-Json
        $rgHoldCheck = @(@($rgHold.Games)[0].Checks | Where-Object { $_.Name -eq "hold:high-g turn" })[0]
        Assert-True ($rgHoldCheck.Ok -eq $true) ("The Cobra button must hold High-G. " + ($rgHoldCheck | ConvertTo-Json -Compress))
        $rgShow = (Run-Engine @("--profile", $rgProfile, "binds", "tuning", "show", "Pitch")).Output
        $rgShowObject = $rgShow.Substring($rgShow.IndexOf("{")) | ConvertFrom-Json
        Assert-Match $rgShowObject.Scopes.base "inv=1" "DCS's (base) invert must stay."
    }
}
finally {
    $env:JOYPRO_VJOY_FAKE = $xpPrevious.Fake
    $env:JOYPRO_FAKE_DEVICES = $xpPrevious.Devices
    $env:JOYPRO_INSTALL_DETECTION = $xpPrevious.Detection
    if (Test-Path $xpCatalogDir) {
        Remove-Item -Path $xpCatalogDir -Recurse -Force
    }
    if ($xpHadCatalog) {
        Copy-Item -Path $xpCatalogBackup -Destination $xpCatalogDir -Recurse -Force
    }
}
