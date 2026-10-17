# Game data lives outside the program folder (GameDataPaths, docs/installs.md "Game data"). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). A temp program folder (JoyPro's own
# DB\Functions plus small DCS and IL-2 lists, as a development build or an older JoyPro leaves them) and a temp data
# folder (JOYPRO_DATA_DIR), never the user's %LOCALAPPDATA%\JoyPro.
#  1. Migration: the first engine start copies the program folder's game lists into <data>\GameData; reads use them.
#  2. A refresh writes only into <data>\GameData; the program folder stays byte-identical.
#  3. A rebuild (program folder replaced with older lists) leaves the refreshed lists in use.
#  4. A program folder list with a newer game build (source-version.txt) replaces the game-data copy, which is kept
#     under GameData\Replaced.
function Get-GdSnapshot([string]$Path) {
    return (@(Get-ChildItem -Path $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Path.Length) + "|" + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function Set-GdStamp([string]$Folder, [string]$Version, [string]$Modules, [string]$Recorded) {
    New-Item -Path $Folder -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $Folder "source-version.txt"),
        "# Game build this DB belongs to (written by JoyPro on DB refresh; see engine db check-updates).`r`nmodules=$Modules`r`nversion=$Version`r`nrecorded=$Recorded`r`n",
        (New-Object System.Text.UTF8Encoding $false))
}
function New-GdProgramFolder([string]$Root, [string]$Version, [string]$Modules) {
    # A rebuild replaces everything the build copies (the folder itself stays: it is the current location).
    foreach ($gdOld in @("DB", "CleanProfile")) {
        if (Test-Path (Join-Path $Root $gdOld)) { Remove-Item -Path (Join-Path $Root $gdOld) -Recurse -Force }
    }
    New-Item -Path (Join-Path $Root "DB\DCS"), (Join-Path $Root "DB\IL2"), (Join-Path $Root "CleanProfile\DCS") -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineBin "DB\Functions") -Destination (Join-Path $Root "DB") -Recurse -Force
    foreach ($plane in @("Alpha-1", "Bravo-2")) {
        [System.IO.File]::WriteAllText((Join-Path $Root ("DB\DCS\" + $plane + ".html")), "<html><body><h1>DCS input layout</h1></body></html>")
        [System.IO.File]::WriteAllText((Join-Path $Root ("CleanProfile\DCS\" + $plane + ".cf")), "local diff = {}`r`nreturn diff`r`n")
    }
    Set-GdStamp (Join-Path $Root "DB\DCS") $Version $Modules "2026-10-11 08:12"
    [System.IO.File]::WriteAllText((Join-Path $Root "DB\IL2\IL2.actions"), "// JoyPro's IL-2 Great Battles command list.`r`n&actions=action,command,invert|`r`n")
}

$gdDir = Join-Path $tempDir "gamedata"
$gdRoot = Join-Path $gdDir "program"
$gdData = Join-Path $gdDir "data"
$gdGameData = Join-Path $gdData "GameData"
New-GdProgramFolder $gdRoot "2.9.30.28718" "ModA,ModB"
$gdIl2Game = Join-Path $gdDir "il2-game"
New-Item -Path (Join-Path $gdIl2Game "data\input") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2-game\data\input\*") -Destination (Join-Path $gdIl2Game "data\input") -Force

$gdPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Detection = $env:JOYPRO_INSTALL_DETECTION }
$env:JOYPRO_DATA_DIR = $gdData
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $gdRoot
try {
    # ---- 1. migration on the first start
    $gdProgramBefore = Get-GdSnapshot $gdRoot
    $gdPaths = Run-Engine @("db", "paths", "--text")
    Assert-True ($gdPaths.ExitCode -eq 0) ("db paths failed: " + $gdPaths.Output)
    Assert-Match $gdPaths.Output ("(?m)^Game data: " + [regex]::Escape($gdGameData)) "db paths should name the game-data folder."
    Assert-Match $gdPaths.Output "(?m)^\s+DB\\DCS \| game-data \| 2\.9\.30\.28718 \| " "db paths should read the DCS list from the game-data folder after the migration."
    Assert-Match $gdPaths.Output "(?m)^\s+CleanProfile\\DCS \| game-data \| " "db paths should read the DCS clean profiles from the game-data folder."
    Assert-NotMatch $gdPaths.Output "(?m)^\s+DB\\Functions" "JoyPro's own DB folders are no game data."
    foreach ($gdFile in @("DB\DCS\Alpha-1.html", "DB\DCS\Bravo-2.html", "DB\DCS\source-version.txt", "CleanProfile\DCS\Alpha-1.cf", "DB\IL2\IL2.actions")) {
        Assert-True (Test-Path (Join-Path $gdGameData $gdFile)) ("the first start should copy " + $gdFile + " into the game-data folder.")
    }
    Assert-True (-not (Test-Path (Join-Path $gdGameData "DB\Functions"))) "JoyPro's own data must stay in the program folder."
    $gdSummary = Run-Engine @("db", "summary", "--text")
    Assert-Match $gdSummary.Output ("(?m)^DB Path: " + [regex]::Escape((Join-Path $gdGameData "DB\DCS"))) "db summary should show the game-data DCS list."
    Assert-Match $gdSummary.Output ("(?m)^Clean Path: " + [regex]::Escape((Join-Path $gdGameData "CleanProfile\DCS"))) "db summary should show the game-data clean profiles."

    # ---- 2. a refresh writes into the game-data folder only
    $gdIl2 = Run-Engine @("db", "refresh-il2", "--path", $gdIl2Game, "--text")
    Assert-True ($gdIl2.ExitCode -eq 0) ("db refresh-il2 into the game-data folder failed: " + $gdIl2.Output)
    Assert-Match $gdIl2.Output ([regex]::Escape((Join-Path $gdGameData "DB\IL2\IL2.actions"))) "db refresh-il2 should name the game-data list it wrote."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $gdGameData "DB\IL2\IL2.actions"))) "(?m)^rpc_fire," "the refreshed IL-2 list should have the game's new action."
    $gdCheck = Run-Engine @("db", "check-updates", "--accept", "IL2Game", "--text")
    Assert-True ($gdCheck.ExitCode -eq 0) ("db check-updates failed: " + $gdCheck.Output)
    Assert-True ((Get-GdSnapshot $gdRoot) -eq $gdProgramBefore) "refreshes and update checks must not write into the program folder."

    # The DB fingerprint (a profile's load correction runs when it differs) counts the lists, not the build stamps: an
    # update check that records a stamp must not make the next save correct every relation (found by the live watcher
    # test: a Take over also added 41 aircraft to each DCS relation after the GUI's first update check).
    $gdFpRegex = '(?m)^DB Fingerprint:\s+(sha256:[0-9a-f]{64})'
    $gdFp1 = [regex]::Match((Run-Engine @("db", "fingerprint", "--text")).Output, $gdFpRegex).Groups[1].Value
    $gdIl2Stamp = Join-Path $gdGameData "DB\IL2\source-version.txt"
    $gdIl2StampOld = if (Test-Path $gdIl2Stamp) { [System.IO.File]::ReadAllBytes($gdIl2Stamp) } else { $null }
    Set-GdStamp (Join-Path $gdGameData "DB\IL2") "fingerprint-stamp-test" "" "2026-10-17 10:44"
    $gdFp2 = [regex]::Match((Run-Engine @("db", "fingerprint", "--text")).Output, $gdFpRegex).Groups[1].Value
    Assert-True ($gdFp1 -and $gdFp1 -eq $gdFp2) ("a new source-version.txt must not change the DB fingerprint: " + $gdFp1 + " / " + $gdFp2)
    if ($null -ne $gdIl2StampOld) { [System.IO.File]::WriteAllBytes($gdIl2Stamp, $gdIl2StampOld) } else { Remove-Item -LiteralPath $gdIl2Stamp -Force }
    $gdIl2List = Join-Path $gdGameData "DB\IL2\IL2.actions"
    $gdIl2ListOld = [System.IO.File]::ReadAllBytes($gdIl2List)
    [System.IO.File]::AppendAllText($gdIl2List, "`r`n")
    $gdFp3 = [regex]::Match((Run-Engine @("db", "fingerprint", "--text")).Output, $gdFpRegex).Groups[1].Value
    Assert-True ($gdFp3 -and $gdFp3 -ne $gdFp1) "a changed command list must change the DB fingerprint."
    [System.IO.File]::WriteAllBytes($gdIl2List, $gdIl2ListOld)

    # ---- 3. a rebuild replaces the program folder with older lists: the refreshed ones stay in use
    [System.IO.File]::WriteAllText((Join-Path $gdGameData "DB\DCS\Charlie-3.html"), "<html><body><h1>DCS input layout</h1></body></html>")
    Set-GdStamp (Join-Path $gdGameData "DB\DCS") "2.9.30.28718" "ModA,ModB,ModC" "2026-10-11 19:52"
    New-GdProgramFolder $gdRoot "2.9.30.28718" "ModA,ModB"
    $gdPlanes = Run-Engine @("db", "planes", "--game", "DCS", "--text")
    Assert-True ($gdPlanes.ExitCode -eq 0) ("db planes after the rebuild failed: " + $gdPlanes.Output)
    Assert-Match $gdPlanes.Output "(?m)^DCS \(3\)" ("after a rebuild the refreshed DCS list (3 aircraft) must stay in use: " + $gdPlanes.Output)
    Assert-Match $gdPlanes.Output "(?m)^\s+- Charlie-3\s*$" "the aircraft only the refreshed list has must stay."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $gdGameData "DB\DCS\source-version.txt"))) "modules=ModA,ModB,ModC" "an older program folder list must not replace the refreshed list's build."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $gdGameData "DB\IL2\IL2.actions"))) "(?m)^rpc_fire," "a rebuild must not take the refreshed IL-2 action away."
    Assert-True (-not (Test-Path (Join-Path $gdGameData "Replaced"))) "nothing is replaced when the program folder's list is not newer."

    # ---- 4. a newer list in the program folder (higher game build) replaces the game-data copy, the old one is kept
    Set-GdStamp (Join-Path $gdRoot "DB\DCS") "2.9.31.1" "ModA,ModB" "2026-10-12 09:00"
    [System.IO.File]::WriteAllText((Join-Path $gdRoot "DB\DCS\Delta-4.html"), "<html><body><h1>DCS input layout</h1></body></html>")
    $gdNewer = Run-Engine @("db", "planes", "--game", "DCS", "--text")
    Assert-True ($gdNewer.ExitCode -eq 0) ("db planes after a newer program list failed: " + $gdNewer.Output)
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $gdGameData "DB\DCS\source-version.txt"))) "version=2\.9\.31\.1" "a program folder list of a newer game build should replace the game-data copy."
    Assert-True (Test-Path (Join-Path $gdGameData "DB\DCS\Delta-4.html")) "the newer list's aircraft should come in."
    $gdKept = @(Get-ChildItem -Path (Join-Path $gdGameData "Replaced") -Recurse -Filter "source-version.txt" -ErrorAction SilentlyContinue)
    Assert-True ($gdKept.Count -eq 1 -and ([System.IO.File]::ReadAllText($gdKept[0].FullName) -match "modules=ModA,ModB,ModC")) "the replaced game-data list must be kept under GameData\Replaced."
    $gdAgain = Run-Engine @("db", "paths", "--text")
    Assert-Match $gdAgain.Output "(?m)^\s+DB\\DCS \| game-data \| 2\.9\.31\.1 \| " "after the replacement the game-data list is read."
    Assert-True (@(Get-ChildItem -Path (Join-Path $gdGameData "Replaced") -Directory).Count -eq 1) "an equal list must not be replaced again on the next start."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $gdPrevious.Data
    $env:JOYPRO_INSTALL_DETECTION = $gdPrevious.Detection
}
