# DCS default binds in the DCS install (engine dcs strip-defaults / dcs device-luas, docs/games/dcs.md): on a synthetic
# DCS folder (Mods, Config, a Saved Games mod) in a temporary JoyPro data folder. Dot-sourced by engine_smoke_test.ps1
# ($tempDir, Run-Engine, Assert-*). The fake folder has no luae.exe of its own; --no-load-check keeps the real DCS
# install's luae.exe out of the test (the load check with DCS's own loader is verified on copies, docs/games/dcs.md).
$sdRoot = Join-Path $tempDir "dcs-strip"
$sdInstall = Join-Path $sdRoot "DCS World"
$sdSaved = Join-Path $sdRoot "Saved Games\DCS"
$sdData = Join-Path $sdRoot "data"
$sdJoy = Join-Path $sdInstall "Mods\aircraft\Test\Input\Test\joystick"
$sdUi = Join-Path $sdInstall "Config\Input\UiLayer\joystick"
$sdCommon = Join-Path $sdInstall "Config\Input\Aircrafts\Default\joystick"
$sdModJoy = Join-Path $sdSaved "Mods\aircraft\ModX\Input\ModX\joystick"
foreach ($dir in @($sdJoy, $sdUi, $sdCommon, $sdModJoy)) { New-Item -Path $dir -ItemType Directory -Force | Out-Null }
'{ "version": "2.9.99.1" }' | Set-Content -Path (Join-Path $sdInstall "autoupdate.cfg") -Encoding ASCII

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-SdFile([string]$Path, [string]$Text, [bool]$Bom = $false) {
    $bytes = $utf8NoBom.GetBytes($Text)
    if ($Bom) { $bytes = [byte[]](0xEF, 0xBB, 0xBF) + $bytes }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}
Write-SdFile (Join-Path $sdJoy "default.lua") "local base = dofile(folder .. 'helper_include.lua')`r`nreturn {`r`n keyCommands = {`r`n  {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN1'}}},`r`n },`r`n axisCommands = {`r`n  {action = 2, name = 'Pitch', combos = {{key = 'JOY_Y'}}},`r`n },`r`n}`r`n"
Write-SdFile (Join-Path $sdJoy "helper_include.lua") "return { 'shared values' }`n"
Write-SdFile (Join-Path $sdJoy "Test Stick.lua") "return { keyCommands = { {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN2'}}} }, axisCommands = {} }`n" $true
Write-SdFile (Join-Path $sdJoy "Test Stick.diff.lua") "local diff = { keyDiffs = {} }`nreturn diff`n"
Write-SdFile (Join-Path $sdUi "default.lua") "return { keyCommands = { {down = 3, name = 'Screenshot', combos = {{key = 'JOY_BTN9'}}} }, axisCommands = {} }`n"
Write-SdFile (Join-Path $sdUi "VR Controller.diff.lua") "local diff = { keyDiffs = {} }`nreturn diff`n"
Write-SdFile (Join-Path $sdCommon "default.lua") "return external_profile('Config/Input/Aircrafts/common_joystick_binding.lua')`n"
Write-SdFile (Join-Path $sdModJoy "default.lua") "return { keyCommands = { {down = 4, name = 'Mod', combos = {{key = 'JOY_BTN4'}}} }, axisCommands = {} }`n"
Write-SdFile (Join-Path $sdModJoy "notes.lua") "-- no input profile here`nreturn {}`n"

function Get-SdTree {
    $map = @{}
    foreach ($root in @($sdInstall, $sdSaved)) {
        foreach ($file in Get-ChildItem -Path $root -Recurse -File) {
            $map[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $map
}
function Assert-SdTreeEquals($Expected, [string]$Message) {
    $now = Get-SdTree
    Assert-True ($now.Count -eq $Expected.Count) ($Message + " (file count " + $now.Count + " vs " + $Expected.Count + ": " + (($now.Keys | Where-Object { -not $Expected.ContainsKey($_) }) -join ", ") + ")")
    foreach ($key in $Expected.Keys) {
        Assert-True ($now.ContainsKey($key) -and $now[$key] -eq $Expected[$key]) ($Message + " (" + $key + ")")
    }
}

$sdOriginal = Get-SdTree
$sdPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = $sdData
try {
    $sdArgs = @("--install-path", $sdInstall, "--saved-games", $sdSaved, "--no-load-check")

    # Dry run: lists the four profile files (helper, notes and the template diffs are no profiles), writes nothing.
    $sdDry = Run-Engine (@("dcs", "strip-defaults", "apply", "--dry-run") + $sdArgs + @("--text"))
    Assert-True ($sdDry.ExitCode -eq 0) "dcs strip-defaults --dry-run failed. Output: $($sdDry.Output)"
    Assert-Match $sdDry.Output "(?m)^Profile Files: 5\r?$" "strip-defaults must find the 5 joystick profiles (install Mods, Config incl. UI layer and common, Saved Games mod)."
    Assert-Match $sdDry.Output "(?m)^DCS Version: 2\.9\.99\.1\r?$" "strip-defaults must read the DCS version."
    Assert-NotMatch $sdDry.Output "helper_include|notes\.lua|\.diff\.lua" "strip-defaults must skip non-profile files and diffs."
    Assert-SdTreeEquals $sdOriginal "a dry run must not change any file"

    # Apply: marker first (a BOM stays in front), the original kept byte for byte as .nocombo.bak.
    $sdApply = Run-Engine (@("dcs", "strip-defaults", "apply") + $sdArgs + @("--text"))
    Assert-True ($sdApply.ExitCode -eq 0) "dcs strip-defaults apply failed. Output: $($sdApply.Output)"
    Assert-Match $sdApply.Output "(?m)^Patched: 5\r?$" "strip-defaults must patch the 5 profiles."
    Assert-Match $sdApply.Output "(?m)^Strip Enabled: True\r?$" "apply must remember the switch."
    $sdDefault = Join-Path $sdJoy "default.lua"
    $sdText = [System.IO.File]::ReadAllText($sdDefault)
    Assert-True ($sdText.StartsWith("-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]`r`n")) "the patched file must start with the strip marker (CRLF kept)."
    Assert-Match $sdText "combos = nil" "the wrapper must clear the combos."
    Assert-Match $sdText "local base = dofile\(folder \.\. 'helper_include\.lua'\)" "the original text must stay inside the wrapper."
    Assert-True ((Get-FileHash -LiteralPath ($sdDefault + ".nocombo.bak")).Hash -eq $sdOriginal[$sdDefault]) "the backup must be the original byte for byte."
    $sdStickBytes = [System.IO.File]::ReadAllBytes((Join-Path $sdJoy "Test Stick.lua"))
    Assert-True ($sdStickBytes[0] -eq 0xEF -and $sdStickBytes[3] -eq 0x2D) "a UTF-8 BOM must stay in front of the marker."
    Assert-True (Test-Path -LiteralPath ((Join-Path $sdModJoy "default.lua") + ".nocombo.bak")) "the Saved Games mod must be patched too."
    Assert-True (-not (Test-Path -LiteralPath ((Join-Path $sdJoy "helper_include.lua") + ".nocombo.bak"))) "a helper without input commands must stay untouched."

    # A second apply changes nothing; status says all are done.
    $sdAgain = Run-Engine (@("dcs", "strip-defaults", "apply") + $sdArgs + @("--text"))
    Assert-Match $sdAgain.Output "(?m)^Already Patched: 5\r?$" "a second apply must find every file already patched."
    Assert-Match $sdAgain.Output "(?m)^Patched: 0\r?$" "a second apply must patch nothing."

    # A DCS update brings one original back (its old backup stays): status asks for a re-apply, auto does it and
    # replaces the stale backup with the new original.
    $sdUpdated = "return { keyCommands = { {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN3'}}} }, axisCommands = {} }`r`n"
    Write-SdFile $sdDefault $sdUpdated
    $sdStatus = Run-Engine (@("dcs", "strip-defaults", "status") + $sdArgs + @("--text"))
    Assert-Match $sdStatus.Output "(?m)^Needs Reapply: True\r?$" "status must notice the file DCS brought back."
    Assert-Match $sdStatus.Output "(?m)^Not Patched: 1\r?$" "status must count the unpatched file."
    $sdAuto = Run-Engine (@("dcs", "strip-defaults", "auto") + $sdArgs + @("--text"))
    Assert-True ($sdAuto.ExitCode -eq 0) "dcs strip-defaults auto failed. Output: $($sdAuto.Output)"
    Assert-Match $sdAuto.Output "(?m)^Patched: 1\r?$" "auto must patch the file again."
    Assert-True ([System.IO.File]::ReadAllText($sdDefault + ".nocombo.bak") -eq $sdUpdated) "auto must keep the updated original as the backup."

    # Restore: the originals come back, no backup left; the switch is off; auto does nothing then.
    $sdRestore = Run-Engine (@("dcs", "strip-defaults", "--restore") + $sdArgs + @("--text"))
    Assert-True ($sdRestore.ExitCode -eq 0) "dcs strip-defaults --restore failed. Output: $($sdRestore.Output)"
    Assert-Match $sdRestore.Output "(?m)^Restored: 5\r?$" "restore must put back the 5 originals."
    Assert-Match $sdRestore.Output "(?m)^Strip Enabled: False\r?$" "restore must switch the option off."
    Assert-True ([System.IO.File]::ReadAllText($sdDefault) -eq $sdUpdated) "restore must bring back the newest original."
    # The test's own "update" goes again, so the tree can be compared with the start.
    Write-SdFile $sdDefault "local base = dofile(folder .. 'helper_include.lua')`r`nreturn {`r`n keyCommands = {`r`n  {down = 1, name = 'Fire', combos = {{key = 'JOY_BTN1'}}},`r`n },`r`n axisCommands = {`r`n  {action = 2, name = 'Pitch', combos = {{key = 'JOY_Y'}}},`r`n },`r`n}`r`n"
    Assert-SdTreeEquals $sdOriginal "after restore every file must be as before"
    $sdIdle = Run-Engine (@("dcs", "strip-defaults", "auto") + $sdArgs + @("--text"))
    Assert-Match $sdIdle.Output "Nothing to do" "auto must do nothing while both options are off."
    Assert-SdTreeEquals $sdOriginal "auto with the options off must not change any file"

    # --skip-ui-layer keeps Config\Input\UiLayer as it is.
    $sdSkip = Run-Engine (@("dcs", "strip-defaults", "apply", "--skip-ui-layer") + $sdArgs + @("--text"))
    Assert-Match $sdSkip.Output "(?m)^Patched: 4\r?$" "--skip-ui-layer must leave the UI layer's profile out."
    Assert-True (-not (Test-Path -LiteralPath ((Join-Path $sdUi "default.lua") + ".nocombo.bak"))) "--skip-ui-layer must not patch the UI layer."
    $null = Run-Engine (@("dcs", "strip-defaults", "restore") + $sdArgs + @("--text"))
    Assert-SdTreeEquals $sdOriginal "restore after --skip-ui-layer must leave every file as before"

    # A file the community strip script patched (same marker and backup name) is restored by JoyPro.
    $sdModDefault = Join-Path $sdModJoy "default.lua"
    Copy-Item -LiteralPath $sdModDefault -Destination ($sdModDefault + ".nocombo.bak")
    Write-SdFile $sdModDefault ("-- [[DCS_STRIP_JOYSTICK_DEFAULTS v1]]`nlocal function original(...)`n" + [System.IO.File]::ReadAllText($sdModDefault) + "`nend`nreturn original(...)`n")
    $sdForeign = Run-Engine (@("dcs", "strip-defaults", "restore") + $sdArgs + @("--text"))
    Assert-Match $sdForeign.Output "(?m)^Restored: 1\r?$" "restore must undo a file the strip script patched."
    Assert-SdTreeEquals $sdOriginal "restoring the strip script's file must bring back the original"

    # Device files: "Test Stick.lua" and its template diff move into JoyPro's folder; default.lua, the helper that
    # default.lua names and the UI layer's VR file stay. Restore puts them back byte for byte.
    $sdList = Run-Engine (@("dcs", "device-luas", "list") + $sdArgs + @("--text"))
    Assert-Match $sdList.Output "(?m)^Device Files In Install: 3\r?$" "device-luas list must find Test Stick.lua, its diff and the mod's unreferenced notes.lua."
    Assert-Match $sdList.Output "Test Stick\.lua" "device-luas list must name the device file."
    Assert-NotMatch $sdList.Output "helper_include|VR Controller|\\default\.lua" "device-luas must keep default.lua, referenced files and the UI layer."
    $sdMove = Run-Engine (@("dcs", "device-luas", "remove") + $sdArgs + @("--text"))
    Assert-True ($sdMove.ExitCode -eq 0) "dcs device-luas remove failed. Output: $($sdMove.Output)"
    Assert-Match $sdMove.Output "(?m)^Device Files Moved: 3\r?$" "device-luas remove must move the 3 files."
    Assert-Match $sdMove.Output "(?m)^Clean Profiles Stale: True\r?$" "moving device files must mark the clean profiles as stale."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $sdJoy "Test Stick.lua"))) "the device file must leave the install."
    Assert-True (Test-Path -LiteralPath (Join-Path $sdJoy "helper_include.lua")) "a file default.lua names must stay."
    Assert-True (Test-Path -LiteralPath (Join-Path $sdData "DcsDefaults\DeviceLuas\manifest.json")) "the moved files must be listed in JoyPro's folder."
    $sdMoveStatus = Run-Engine (@("dcs", "strip-defaults", "status") + $sdArgs + @("--text"))
    Assert-Match $sdMoveStatus.Output "(?m)^Remove Device Files: True\r?$" "status must show the device-file switch."
    Assert-Match $sdMoveStatus.Output "(?m)^Device Files Kept By JoyPro: 3\r?$" "status must count JoyPro's copies."
    $sdPut = Run-Engine (@("dcs", "device-luas", "--restore") + $sdArgs + @("--text"))
    Assert-True ($sdPut.ExitCode -eq 0) "dcs device-luas --restore failed. Output: $($sdPut.Output)"
    Assert-Match $sdPut.Output "(?m)^Device Files Restored: 3\r?$" "device-luas restore must put back the 3 files."
    Assert-SdTreeEquals $sdOriginal "device-luas restore must leave every file as before"

    # Without a DCS folder nothing runs.
    $sdMissing = Run-Engine @("dcs", "strip-defaults", "apply", "--install-path", (Join-Path $sdRoot "nothing"), "--no-load-check", "--text")
    Assert-True ($sdMissing.ExitCode -ne 0) "strip-defaults must fail without a DCS folder."
}
finally {
    $env:JOYPRO_DATA_DIR = $sdPreviousData
}
