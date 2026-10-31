# The DCS keyboard is never taken over (docs/games/dcs.md "Keyboard"): a temporary Saved Games and a temporary JoyPro
# data folder with keyboard clean profiles of two test aircraft. Dot-sourced by engine_smoke_test.ps1 (Run-Engine,
# Assert-*, $tempDir). Checks:
#  - keep keyboard defaults (the default): no keyboard file is written or changed;
#  - --keep-keyboard-defaults false in every mode: the profile's keys go in, only a DCS default key the profile binds to
#    another action is removed, every other default key stays (up to 2.4.0 all of them were removed);
#  - --clear-keyboard-defaults true: every default key goes (the user asked for it);
#  - a keyboard file an older export wrote with every default removed gets its default keys back, also with keep on;
#  - an aircraft the profile has no keyboard binds for keeps its keyboard file byte for byte;
#  - undo export (backups restore) puts the keyboard file back;
#  - dcs strip-defaults and dcs device-luas never touch a keyboard folder of the install;
#  - every written keyboard file loads in DCS's own luae.exe (when DCS is installed here).
$kbRoot = Join-Path $tempDir "dcs-keyboard"
$kbData = Join-Path $kbRoot "data"
$kbProfile = Join-Path $kbRoot "keyboard.pr0file"
$kbCleanDir = Join-Path $kbData "GameData\KeyboardCleanProfile\DCS"
New-Item -ItemType Directory -Force -Path $kbCleanDir | Out-Null
$kbUtf8 = New-Object System.Text.UTF8Encoding($false)
$kbClean = @'
local diff = {
	["keyDiffs"] = {
		["d1"] = { ["name"] = "Gear", ["removed"] = { [1] = { ["key"] = "G", }, }, },
		["d2"] = { ["name"] = "Flaps", ["removed"] = { [1] = { ["key"] = "F", }, }, },
		["d3"] = { ["name"] = "Hook", ["removed"] = { [1] = { ["key"] = "H", ["reformers"] = { [1] = "LCtrl", }, }, }, },
	},
}
return diff
'@
[System.IO.File]::WriteAllText((Join-Path $kbCleanDir "KbTest.cf"), $kbClean, $kbUtf8)
[System.IO.File]::WriteAllText((Join-Path $kbCleanDir "KbOther.cf"), $kbClean, $kbUtf8)

# The keys of every ["removed"] block (brace matched), in file order.
function Get-KbRemovedKeys([string]$Text) {
    $keys = @()
    foreach ($m in [regex]::Matches($Text, '\["removed"\]\s*=\s*\{')) {
        $depth = 1
        $i = $m.Index + $m.Length
        while ($i -lt $Text.Length -and $depth -gt 0) {
            if ($Text[$i] -eq '{') { $depth++ } elseif ($Text[$i] -eq '}') { $depth-- }
            $i++
        }
        $block = $Text.Substring($m.Index + $m.Length, $i - $m.Index - $m.Length)
        foreach ($k in [regex]::Matches($block, '\["key"\]\s*=\s*"([^"]+)"')) { $keys += $k.Groups[1].Value }
    }
    return ,$keys
}
function New-KbSavedGames([string]$Name) {
    $root = Join-Path $kbRoot $Name
    $other = Join-Path $root "Config\Input\KbOther\keyboard"
    New-Item -ItemType Directory -Force -Path $other | Out-Null
    # The user's own keyboard file of an aircraft the profile has no keyboard binds for.
    [System.IO.File]::WriteAllText((Join-Path $other "Keyboard.diff.lua"), "local diff = {`n`t[`"keyDiffs`"] = {`n`t`t[`"d1`"] = {`n`t`t`t[`"name`"] = `"Gear`",`n`t`t`t[`"added`"] = {`n`t`t`t`t[1] = {`n`t`t`t`t`t[`"key`"] = `"J`",`n`t`t`t`t},`n`t`t`t},`n`t`t},`n`t},`n}`nreturn diff`n", $kbUtf8)
    return $root
}
function Read-KbFile([string]$Root, [string]$Plane) {
    $file = Join-Path $Root ("Config\Input\" + $Plane + "\keyboard\Keyboard.diff.lua")
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    return [System.IO.File]::ReadAllText($file)
}

$kbPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Installed = $env:JOYPRO_TEST_ASSUME_INSTALLED }
$env:JOYPRO_DATA_DIR = $kbData
$env:JOYPRO_TEST_ASSUME_INSTALLED = "1"
$kbWritten = New-Object System.Collections.Generic.List[string]
try {
    # The profile: Flaps on G (G is Gear's default key), a stick bind in KbTest and in KbOther, no keyboard bind in KbOther.
    $kbAdd = Run-Engine @("--profile", $kbProfile, "relations", "add", "Kb Flaps", "--button", "--node", "DCS:d2:KbTest", "--device", "Keyboard", "--input", "G", "--text")
    Assert-True ($kbAdd.ExitCode -eq 0) ("dcskeyboard: relations add Kb Flaps failed: " + $kbAdd.Output)
    $kbAdd = Run-Engine @("--profile", $kbProfile, "relations", "add", "Kb Stick Gear", "--button", "--node", "DCS:d1:KbTest|KbOther", "--device", "Smoke Stick", "--input", "JOY_BTN1", "--text")
    Assert-True ($kbAdd.ExitCode -eq 0) ("dcskeyboard: relations add Kb Stick Gear failed: " + $kbAdd.Output)

    # 1. Keep keyboard defaults (the default): no keyboard file is written.
    $kbKeep = New-KbSavedGames "keep"
    $kbOtherBefore = (Get-FileHash -LiteralPath (Join-Path $kbKeep "Config\Input\KbOther\keyboard\Keyboard.diff.lua")).Hash
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbKeep, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: export with the keyboard kept failed: " + $kbRun.Output)
    Assert-True ($null -eq (Read-KbFile $kbKeep "KbTest")) "dcskeyboard: keep keyboard defaults must not write a keyboard file."
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $kbKeep "Config\Input\KbOther\keyboard\Keyboard.diff.lua")).Hash -eq $kbOtherBefore) "dcskeyboard: clean + nuke changed the keyboard file of an aircraft without keyboard binds."

    # 2. Keyboard binds written, in every mode: only Gear's default G goes; F (Flaps) and LCtrl+H (Hook) stay. "Only add"
    # never deletes (2.5.0): it keeps Gear's G, does not write Flaps on it and lists that bind.
    $kbAddSaved = New-KbSavedGames "write-add"
    $kbAddRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbAddSaved, "--mode", "add", "--keep-keyboard-defaults", "false", "--text")
    Assert-True ($kbAddRun.ExitCode -eq 0) ("dcskeyboard: export (add, keyboard written) failed: " + $kbAddRun.Output)
    $kbAddText = Read-KbFile $kbAddSaved "KbTest"
    Assert-True ($null -eq $kbAddText -or (@(Get-KbRemovedKeys $kbAddText).Count -eq 0)) "dcskeyboard: add must not remove a default key."
    Assert-Match $kbAddRun.Output "Binds Not Written, Button Taken In The Game \(Only Add\): 1" "dcskeyboard: add must list Flaps on Gear's key G as not written."
    foreach ($kbMode in @("clean", "merge", "merge-overwrite")) {
        $kbSaved = New-KbSavedGames ("write-" + $kbMode)
        $kbOtherBefore = (Get-FileHash -LiteralPath (Join-Path $kbSaved "Config\Input\KbOther\keyboard\Keyboard.diff.lua")).Hash
        $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbSaved, "--mode", $kbMode, "--keep-keyboard-defaults", "false", "--nuke-untouched-devices", "true", "--text")
        Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: export (" + $kbMode + ", keyboard written) failed: " + $kbRun.Output)
        $kbText = Read-KbFile $kbSaved "KbTest"
        Assert-True ($null -ne $kbText) ("dcskeyboard: " + $kbMode + ": the keyboard file of KbTest was not written.")
        Assert-Match $kbText '\["d2"\][\s\S]*?\["added"\][\s\S]*?\["key"\]\s*=\s*"G"' ("dcskeyboard: " + $kbMode + ": the profile's key G for Flaps is missing.")
        $kbRemoved = Get-KbRemovedKeys $kbText
        # "My profile wins" keeps the keyboard's default keys of the profile's actions (Flaps' F): the keyboard is never
        # taken over (2.5.0).
        $kbExpected = "G"
        Assert-True ((($kbRemoved | Sort-Object) -join ",") -eq $kbExpected) ("dcskeyboard: " + $kbMode + ": only " + $kbExpected + " may be removed, the file removes: " + ($kbRemoved -join ","))
        Assert-Match $kbRun.Output "(?m)^Keyboard Default Keys Removed: 1\r?$" ("dcskeyboard: " + $kbMode + ": the export must report one removed default key.")
        Assert-True ((Get-FileHash -LiteralPath (Join-Path $kbSaved "Config\Input\KbOther\keyboard\Keyboard.diff.lua")).Hash -eq $kbOtherBefore) ("dcskeyboard: " + $kbMode + ": the keyboard file of KbOther (no keyboard binds) changed.")
        $kbWritten.Add((Join-Path $kbSaved "Config\Input\KbTest\keyboard\Keyboard.diff.lua"))
    }

    # 3. Asked for: every default key of KbTest goes.
    $kbClear = New-KbSavedGames "clear"
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbClear, "--mode", "merge", "--keep-keyboard-defaults", "false", "--clear-keyboard-defaults", "true", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: export with --clear-keyboard-defaults failed: " + $kbRun.Output)
    $kbClearText = Read-KbFile $kbClear "KbTest"
    $kbRemoved = Get-KbRemovedKeys $kbClearText
    Assert-True ((($kbRemoved | Sort-Object) -join ",") -eq "F,G,H") ("dcskeyboard: --clear-keyboard-defaults must remove every default key, the file removes: " + ($kbRemoved -join ","))
    $kbWritten.Add((Join-Path $kbClear "Config\Input\KbTest\keyboard\Keyboard.diff.lua"))

    # 4. A file an older export wrote (every default removed): the default keys come back, in merge and with keep on.
    $kbRepair = Join-Path $kbRoot "repair"
    Copy-Item -Recurse -Path $kbClear -Destination $kbRepair
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbRepair, "--mode", "merge", "--keep-keyboard-defaults", "false", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: repair export failed: " + $kbRun.Output)
    $kbRemoved = Get-KbRemovedKeys (Read-KbFile $kbRepair "KbTest")
    Assert-True (($kbRemoved -join ",") -eq "G") ("dcskeyboard: a merge export must give back the default keys an older export removed, the file removes: " + ($kbRemoved -join ","))
    Assert-Match $kbRun.Output "The DCS default keys are back on the keyboard of KbTest" "dcskeyboard: the repair must be reported."
    $kbWritten.Add((Join-Path $kbRepair "Config\Input\KbTest\keyboard\Keyboard.diff.lua"))

    $kbRepairKeep = Join-Path $kbRoot "repair-keep"
    Copy-Item -Recurse -Path $kbClear -Destination $kbRepairKeep
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbRepairKeep, "--mode", "clean", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: repair export with the keyboard kept failed: " + $kbRun.Output)
    $kbRepairKeepText = Read-KbFile $kbRepairKeep "KbTest"
    $kbRemoved = Get-KbRemovedKeys $kbRepairKeepText
    Assert-True (($kbRemoved -join ",") -eq "G") ("dcskeyboard: with the keyboard kept, an old all-removing file must get its defaults back (its own G stays bound), the file removes: " + ($kbRemoved -join ","))
    Assert-Match $kbRepairKeepText '"G"' "dcskeyboard: the repair must keep the file's own keys."
    $kbWritten.Add((Join-Path $kbRepairKeep "Config\Input\KbTest\keyboard\Keyboard.diff.lua"))

    # A file the user changed themselves (only some defaults removed) stays as it is with keep on.
    $kbUser = New-KbSavedGames "user-file"
    $kbUserDir = Join-Path $kbUser "Config\Input\KbTest\keyboard"
    New-Item -ItemType Directory -Force -Path $kbUserDir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $kbUserDir "Keyboard.diff.lua"), "local diff = {`n`t[`"keyDiffs`"] = {`n`t`t[`"d2`"] = {`n`t`t`t[`"name`"] = `"Flaps`",`n`t`t`t[`"removed`"] = {`n`t`t`t`t[1] = {`n`t`t`t`t`t[`"key`"] = `"F`",`n`t`t`t`t},`n`t`t`t},`n`t`t},`n`t},`n}`nreturn diff`n", $kbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kbUserDir "Keyboard.jp"), [System.IO.File]::ReadAllText((Join-Path $kbUserDir "Keyboard.diff.lua")), $kbUtf8)
    $kbUserBefore = (Get-FileHash -LiteralPath (Join-Path $kbUserDir "Keyboard.diff.lua")).Hash
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbUser, "--mode", "clean", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: export over the user's own keyboard file failed: " + $kbRun.Output)
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $kbUserDir "Keyboard.diff.lua")).Hash -eq $kbUserBefore) "dcskeyboard: the user's own keyboard file (some defaults removed) must stay as it is."

    # 5. Undo export: the keyboard file comes back as it was before the last export (the cleared one of step 3).
    $kbUndo = Join-Path $kbRoot "undo"
    Copy-Item -Recurse -Path $kbClear -Destination $kbUndo
    $kbUndoBefore = (Get-FileHash -LiteralPath (Join-Path $kbUndo "Config\Input\KbTest\keyboard\Keyboard.diff.lua")).Hash
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbUndo, "--mode", "merge", "--keep-keyboard-defaults", "false", "--text")
    Assert-True ($kbRun.ExitCode -eq 0) ("dcskeyboard: export before the undo failed: " + $kbRun.Output)
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $kbUndo "Config\Input\KbTest\keyboard\Keyboard.diff.lua")).Hash -ne $kbUndoBefore) "dcskeyboard: the export before the undo did not change the keyboard file."
    $kbRestore = Run-Engine @("backups", "restore", "latest", "--game", "DCS", "--text")
    Assert-True ($kbRestore.ExitCode -eq 0) ("dcskeyboard: backups restore failed: " + $kbRestore.Output)
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $kbUndo "Config\Input\KbTest\keyboard\Keyboard.diff.lua")).Hash -eq $kbUndoBefore) "dcskeyboard: undo export did not put the keyboard file back."
    $kbFresh = New-KbSavedGames "undo-new"
    $kbRun = Run-Engine @("--profile", $kbProfile, "dcs", "export", $kbFresh, "--mode", "merge", "--keep-keyboard-defaults", "false", "--text")
    Assert-True ($null -ne (Read-KbFile $kbFresh "KbTest")) "dcskeyboard: the export before the second undo wrote no keyboard file."
    $kbRestore = Run-Engine @("backups", "restore", "latest", "--game", "DCS", "--text")
    Assert-True ($kbRestore.ExitCode -eq 0) ("dcskeyboard: second backups restore failed: " + $kbRestore.Output)
    Assert-True ($null -eq (Read-KbFile $kbFresh "KbTest")) "dcskeyboard: undo export must remove a keyboard file the export created."

    # 6. The DCS install: strip-defaults and device-luas never touch a keyboard folder.
    $kbInstall = Join-Path $kbRoot "DCS World"
    $kbInstallJoy = Join-Path $kbInstall "Mods\aircraft\KbTest\Input\KbTest\joystick"
    $kbInstallKb = Join-Path $kbInstall "Mods\aircraft\KbTest\Input\KbTest\keyboard"
    $kbInstallKbJoy = Join-Path $kbInstallKb "joystick"
    foreach ($dir in @($kbInstallJoy, $kbInstallKb, $kbInstallKbJoy)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    '{ "version": "2.9.99.1" }' | Set-Content -Path (Join-Path $kbInstall "autoupdate.cfg") -Encoding ASCII
    $kbProfileLua = "return { keyCommands = { {down = 1, name = 'Gear', combos = {{key = 'G'}}} }, axisCommands = {} }`n"
    [System.IO.File]::WriteAllText((Join-Path $kbInstallJoy "default.lua"), "return { keyCommands = { {down = 1, name = 'Gear', combos = {{key = 'JOY_BTN1'}}} }, axisCommands = {} }`n", $kbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kbInstallKb "default.lua"), $kbProfileLua, $kbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kbInstallKb "Keyboard.lua"), $kbProfileLua, $kbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kbInstallKbJoy "default.lua"), $kbProfileLua, $kbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kbInstallKbJoy "Keyboard.lua"), $kbProfileLua, $kbUtf8)
    $kbInstallSaved = Join-Path $kbRoot "install-saved\DCS"
    New-Item -ItemType Directory -Force -Path $kbInstallSaved | Out-Null
    $kbKeyboardHashes = @{}
    foreach ($f in Get-ChildItem -Path $kbInstallKb -Recurse -File) { $kbKeyboardHashes[$f.FullName] = (Get-FileHash -LiteralPath $f.FullName).Hash }
    $kbStripArgs = @("--install-path", $kbInstall, "--saved-games", $kbInstallSaved, "--no-load-check")
    $kbStrip = Run-Engine (@("dcs", "strip-defaults", "apply") + $kbStripArgs + @("--text"))
    Assert-True ($kbStrip.ExitCode -eq 0) ("dcskeyboard: strip-defaults apply failed: " + $kbStrip.Output)
    Assert-Match $kbStrip.Output "(?m)^Patched: 1\r?$" "dcskeyboard: strip-defaults must patch only the joystick default.lua."
    $kbMove = Run-Engine (@("dcs", "device-luas", "remove", "--install-path", $kbInstall, "--saved-games", $kbInstallSaved, "--text"))
    Assert-True ($kbMove.ExitCode -eq 0) ("dcskeyboard: device-luas remove failed: " + $kbMove.Output)
    Assert-NotMatch $kbMove.Output "KbTest\\keyboard\\" "dcskeyboard: device-luas must not list a file of a keyboard folder."
    foreach ($key in $kbKeyboardHashes.Keys) {
        Assert-True ((Test-Path -LiteralPath $key) -and ((Get-FileHash -LiteralPath $key).Hash -eq $kbKeyboardHashes[$key])) ("dcskeyboard: strip-defaults / device-luas changed or moved a keyboard file: " + $key)
        Assert-True (-not (Test-Path -LiteralPath ($key + ".nocombo.bak"))) ("dcskeyboard: strip-defaults made a backup of a keyboard file: " + $key)
    }
    $kbPut = Run-Engine (@("dcs", "strip-defaults", "restore") + $kbStripArgs + @("--text"))
    Assert-True ($kbPut.ExitCode -eq 0) ("dcskeyboard: strip-defaults restore failed: " + $kbPut.Output)
    $kbPut = Run-Engine (@("dcs", "device-luas", "--restore", "--install-path", $kbInstall, "--saved-games", $kbInstallSaved, "--text"))
    Assert-True ($kbPut.ExitCode -eq 0) ("dcskeyboard: device-luas restore failed: " + $kbPut.Output)

    # 7. DCS's own Lua loads every written keyboard file and finds the removed keys where the checks above expect them.
    $kbLuae = @("G:\Program Files\Eagle Dynamics\DCS World\bin-mt\luae.exe", "G:\Program Files\Eagle Dynamics\DCS World\bin\luae.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($kbLuae) {
        $kbList = Join-Path $kbRoot "written.txt"
        @($kbWritten | ForEach-Object { $_.Replace("\", "/") }) | Set-Content -Path $kbList -Encoding ascii
        $kbScript = Join-Path $kbRoot "load.lua"
        Set-Content -Path $kbScript -Encoding ascii -Value @(
            'local bad = 0',
            'for path in io.lines(arg[1]) do',
            '  local f, err = loadfile(path)',
            '  if not f then bad = bad + 1 print("ERR " .. path .. " " .. tostring(err)) else',
            '    local ok, diff = pcall(f)',
            '    if not ok or type(diff) ~= "table" or type(diff.keyDiffs) ~= "table" then bad = bad + 1 print("BAD " .. path) end',
            '  end',
            'end',
            'print("FAILED " .. bad)')
        $kbLoad = (& $kbLuae $kbScript $kbList 2>&1 | Out-String)
        Assert-True ($kbLoad -match "FAILED 0") ("dcskeyboard: DCS's Lua could not load the written keyboard files: " + $kbLoad)
    }
    else {
        Write-Host "SKIP (DCS not installed here): DCS luae load test of the keyboard files"
    }
    Write-Host "DCS keyboard smoke checks passed."
}
finally {
    $env:JOYPRO_DATA_DIR = $kbPrevious.Data
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $kbPrevious.Installed
}
