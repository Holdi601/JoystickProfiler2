# A release install ships no game data (docs/licensing-audit.md, tools/ReleaseContent.psm1): only DB\Functions,
# DB\Devices, DB\Llm and TOOLS\Conversions. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir,
# $engineDir, $engineBin). The engine must start in such a folder, report the missing command lists instead of
# recording a baseline, build lists from nothing, and keep a profile's nodes of games it has no list of.
$fiRoot = Join-Path $tempDir "fresh-install"
$fiData = Join-Path $tempDir "fresh-install-data"
New-Item -Path (Join-Path $fiRoot "DB"), (Join-Path $fiRoot "TOOLS"), $fiData -ItemType Directory -Force | Out-Null
foreach ($own in @("Functions", "Devices", "Llm")) {
    Copy-Item -Path (Join-Path $engineBin ("DB\" + $own)) -Destination (Join-Path $fiRoot "DB") -Recurse -Force
}
Copy-Item -Path (Join-Path $engineBin "TOOLS\Conversions") -Destination (Join-Path $fiRoot "TOOLS") -Recurse -Force

# A profile with a DCS relation, made where the development DB is (the engine folder).
$fiProfile = Join-Path $tempDir "fresh-install.pr0file"
$fiAdd = Run-Engine @("--profile", $fiProfile, "relations", "add", "Fresh DCS Relation", "--button", "--node", "DCS:d3001pnilunilcdnilvdnilvpnilvunil", "--text")
Assert-True ($fiAdd.ExitCode -eq 0) ("relations add for the fresh-install profile failed: " + $fiAdd.Output)
$fiBefore = Run-Engine @("--profile", $fiProfile, "relations", "list", "--text")

$fiPreviousData = $env:JOYPRO_DATA_DIR
$fiPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = $fiData
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $fiRoot
try {
    # Installed games without a list are "no-catalog"; nothing is recorded as the build of a list that does not exist.
    $fiCheck = Run-Engine @("db", "check-updates")
    Assert-True ($fiCheck.ExitCode -eq 0) ("db check-updates in a fresh install failed: " + $fiCheck.Output)
    $fiReport = $fiCheck.Output | ConvertFrom-Json
    foreach ($state in @($fiReport.Games)) {
        Assert-True ($state.State -in @("no-catalog", "not-installed")) ("a fresh install must not count " + $state.Game + " as " + $state.State + ".")
    }
    $fiGameFolders = @(Get-ChildItem -Path (Join-Path $fiRoot "DB"), (Join-Path $fiData "GameData\DB") -Directory -ErrorAction SilentlyContinue | Where-Object { @("Functions", "Devices", "Llm") -notcontains $_.Name } | ForEach-Object Name)
    Assert-True ($fiGameFolders.Count -eq 0) ("db check-updates must not create game folders in a fresh install: " + ($fiGameFolders -join ", "))

    # The profile's DCS node stays as it is although there is no DCS list to correct it against.
    $fiAfter = Run-Engine @("--profile", $fiProfile, "relations", "list", "--text")
    Assert-True ($fiAfter.ExitCode -eq 0) ("relations list in a fresh install failed: " + $fiAfter.Output)
    Assert-Match $fiAfter.Output "Fresh DCS Relation" "the relation must survive loading in a fresh install."
    Assert-True (($fiAfter.Output -join "`n") -eq ($fiBefore.Output -join "`n")) ("loading a profile without the DCS list must not change its nodes:`n" + ($fiBefore.Output -join "`n") + "`n---`n" + ($fiAfter.Output -join "`n"))

    # IL-2 Great Battles: the list is built from the game's key mapping file alone.
    $fiIl2 = Join-Path $tempDir "fresh-install-il2"
    New-Item -Path (Join-Path $fiIl2 "data\input") -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2-game\data\input\*") -Destination (Join-Path $fiIl2 "data\input") -Force
    $fiIl2Refresh = Run-Engine @("db", "refresh-il2", "--path", $fiIl2, "--text")
    Assert-True ($fiIl2Refresh.ExitCode -eq 0) ("db refresh-il2 without a list failed: " + $fiIl2Refresh.Output)
    # Lists built on the user's PC go into the game-data folder (<data>\GameData); a program folder under Program
    # Files is not writable.
    $fiIl2Catalog = [System.IO.File]::ReadAllText((Join-Path $fiData "GameData\DB\IL2\IL2.actions"))
    Assert-Match $fiIl2Catalog "&actions=action,command,invert\|" "the new IL-2 list should have the game's header line."
    Assert-Match $fiIl2Catalog "rpc_roll,\s+,\s+0\| //a " "the new IL-2 list should have the fixture's axis."
    Assert-Match $fiIl2Catalog "(?m)^rpc_fire,\s+," "the new IL-2 list should have the fixture's second action."

    # WARDOGS and Arma 3: the axes JoyPro manages come from the code, not from a shipped list. (A refresh needs the game
    # installed; linked folders stand in for it.)
    foreach ($fiGame in @("Wardogs", "Arma3")) {
        $fiFolder = Join-Path $tempDir ("fresh-install-" + $fiGame)
        New-Item -Path $fiFolder -ItemType Directory -Force | Out-Null
        $fiLink = Run-Engine @("games", "install", "set", $fiGame, $fiFolder, "--text")
        Assert-True ($fiLink.ExitCode -eq 0) ("games install set " + $fiGame + " failed: " + $fiLink.Output)
    }
    $fiWardogs = Run-Engine @("wardogs", "refresh-db", "--path", (Join-Path $engineDir "tests\fixtures\wardogs\GameUserSettings.ini"), "--text")
    Assert-True ($fiWardogs.ExitCode -eq 0) ("wardogs refresh-db without a list failed: " + $fiWardogs.Output)
    $fiWardogsCatalog = [System.IO.File]::ReadAllText((Join-Path $fiData "GameData\DB\Wardogs\actions.tsv"))
    Assert-Match $fiWardogsCatalog "(?m)^Pitch\tPitch\taxis\tFlight" "the new WARDOGS list should have the pitch axis."
    Assert-Match $fiWardogsCatalog "(?m)^Throttle\.Positive\t" "the new WARDOGS list should have the axis button halves."

    $fiArma = Run-Engine @("arma3", "refresh-db", "--path", (Join-Path $engineDir "tests\fixtures\arma3\Smoke.Arma3Profile"), "--text")
    Assert-True ($fiArma.ExitCode -eq 0) ("arma3 refresh-db without a list failed: " + $fiArma.Output)
    $fiArmaCatalog = [System.IO.File]::ReadAllText((Join-Path $fiData "GameData\DB\Arma3\actions.tsv"))
    Assert-Match $fiArmaCatalog "(?m)^Axis\.PlanePitch\t[^\t]+\taxis\t" "the new Arma 3 list should have JoyPro's axis relations."

    # Arma 3 without a first start: the actions from the game's config (Dta\bin.pbo, binarized config.bin, class
    # CfgDefaultKeysPresets >> Default >> Mappings) and their names from Dta\languagecore_f.pbo's stringtable.xml.
    # Synthetic PBOs in the shape of the game's (stored files, a "Vers" header entry).
    function New-FiZ([System.IO.BinaryWriter]$W, [string]$Text) { $W.Write([System.Text.Encoding]::ASCII.GetBytes($Text)); $W.Write([byte]0) }
    function New-FiPbo([string]$Path, [string]$Name, [byte[]]$Data) {
        $ms = New-Object System.IO.MemoryStream
        $w = New-Object System.IO.BinaryWriter($ms)
        New-FiZ $w ""; $w.Write([uint32]0x56657273); foreach ($i in 1..4) { $w.Write([int]0) }
        New-FiZ $w "prefix"; New-FiZ $w "smoke"; New-FiZ $w ""
        New-FiZ $w $Name; $w.Write([int]0); $w.Write([int]$Data.Length); $w.Write([int]0); $w.Write([int]0); $w.Write([int]$Data.Length)
        New-FiZ $w ""; foreach ($i in 1..5) { $w.Write([int]0) }
        $w.Write($Data); $w.Write([byte]0)
        $w.Flush()
        [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    }
    $raMs = New-Object System.IO.MemoryStream
    $raW = New-Object System.IO.BinaryWriter($raMs)
    $raW.Write([byte[]](0, 0x72, 0x61, 0x50)); $raW.Write([int]0); $raW.Write([int]8); $raEnum = $raMs.Position; $raW.Write([int]0)
    $raPatch = @()
    foreach ($raClass in @("CfgDefaultKeysPresets", "Default", "Mappings")) {
        New-FiZ $raW ""; $raW.Write([byte]1); $raW.Write([byte]0); New-FiZ $raW $raClass
        $raPatch += $raMs.Position; $raW.Write([int]0)
    }
    $raBodies = @($raMs.Position)
    New-FiZ $raW ""; $raW.Write([byte]4)
    foreach ($raAction in @("heliCyclicForward", "moveForward", "launchCM", "heliCollectiveRaiseCont")) {
        $raW.Write([byte]2); New-FiZ $raW $raAction; $raW.Write([byte]1); $raW.Write([byte]2); $raW.Write([int]17)
    }
    $raEnd = $raMs.Position; $raW.Write([int]0); $raW.Flush()
    $raBytes = $raMs.ToArray()
    # Each class entry points to the next class's body; the last one to the arrays.
    $raBodyStarts = @(($raPatch[0] + 4), ($raPatch[1] + 4), $raBodies[0])
    for ($i = 0; $i -lt 3; $i++) { [BitConverter]::GetBytes([int]$raBodyStarts[$i]).CopyTo($raBytes, $raPatch[$i]) }
    [BitConverter]::GetBytes([int]$raEnd).CopyTo($raBytes, $raEnum)
    $fiArmaGame = Join-Path $tempDir "fresh-install-arma3-game"
    New-Item -Path (Join-Path $fiArmaGame "Dta") -ItemType Directory -Force | Out-Null
    New-FiPbo (Join-Path $fiArmaGame "Dta\bin.pbo") "config.bin" $raBytes
    $fiTable = "<?xml version=`"1.0`" encoding=`"utf-8`"?><Project name=`"A3`"><Package name=`"Core`"><Container name=`"Smoke`"><Key ID=`"str_usract_heli_cyclic_forward`"><Original>Smoke cyclic forward</Original><English>Smoke cyclic forward</English><German>Zyklisch vor</German></Key></Container></Package></Project>"
    New-FiPbo (Join-Path $fiArmaGame "Dta\languagecore_f.pbo") "stringtable.xml" ([System.Text.Encoding]::UTF8.GetBytes($fiTable))
    # Its own data folder: no list from the profile refresh above, as on a PC where the game was never started.
    $fiArmaData = Join-Path $tempDir "fresh-install-arma3-data"
    $env:JOYPRO_DATA_DIR = $fiArmaData
    Assert-True ((Run-Engine @("games", "install", "set", "Arma3", $fiArmaGame, "--text")).ExitCode -eq 0) "games install set Arma3 (game config) failed."
    $fiArmaGameRefresh = Run-Engine @("arma3", "refresh-db", "--path", $fiArmaGame, "--text")
    $env:JOYPRO_DATA_DIR = $fiData
    Assert-True ($fiArmaGameRefresh.ExitCode -eq 0) ("arma3 refresh-db from the game's config failed: " + $fiArmaGameRefresh.Output)
    Assert-Match $fiArmaGameRefresh.Output "4 actions \(CfgDefaultKeysPresets/Default/Mappings\)" ("the refresh should read the config's action list: " + $fiArmaGameRefresh.Output)
    $fiArmaCatalog = [System.IO.File]::ReadAllText((Join-Path $fiArmaData "GameData\DB\Arma3\actions.tsv"))
    Assert-Match $fiArmaCatalog "(?m)^HeliCyclicForward\tSmoke cyclic forward\tbutton\tFlight\r?$" "a flight action of the config should come in with the stringtable's English name."
    Assert-Match $fiArmaCatalog "(?m)^LaunchCM\tLaunch CM\tbutton\tOther\r?$" "an action JoyPro manages besides flight should come in."
    Assert-NotMatch $fiArmaCatalog "(?m)^MoveForward\t" "on-foot actions must not become catalog actions."
    Assert-NotMatch $fiArmaCatalog "(?m)^HeliCollectiveRaiseCont\t" "the analogue half of an axis relation is not a button."
    $fiProgramFolders = @(Get-ChildItem -Path (Join-Path $fiRoot "DB") -Directory | Where-Object { @("Functions", "Devices", "Llm") -notcontains $_.Name } | ForEach-Object Name)
    Assert-True ($fiProgramFolders.Count -eq 0) ("building lists must not write into the program folder: " + ($fiProgramFolders -join ", "))
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $fiPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $fiPreviousDetection
}
