# IL-2 Korea installations (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineDir).
# Fake drives (JOYPRO_TEST_DRIVES) with the Steam layout (steamapps\common\IL2Series\bin\game\IL2Series.exe +
# data\input) and the standalone layout of 1C Game Studios' installer (<folder>\Launcher.exe, game in <folder>\game),
# fake uninstall entries (JOYPRO_TEST_UNINSTALL_FILE). Detection finds both; a folder set by the user works whether it
# is the root, the launcher folder, bin\game, the game's exe or the launcher's exe; the key mapping file is found from
# any of them.
$kiDir = Join-Path $tempDir "il2korea-installs"
$kiSteamDrive = Join-Path $kiDir "drive-steam"
$kiStandaloneDrive = Join-Path $kiDir "drive-standalone"
$kiSteam = Join-Path $kiSteamDrive "SteamLibrary\steamapps\common\IL2Series"
$kiApp = Join-Path $kiStandaloneDrive "Games\IL2Series"
$kiGame = Join-Path $kiApp "game"
$kiOther = Join-Path $kiStandaloneDrive "Korea elsewhere"
$kiUtf8 = New-Object System.Text.UTF8Encoding $false
foreach ($kiRoot in @($kiSteam, $kiGame, (Join-Path $kiOther "game"))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $kiRoot "bin\game"), (Join-Path $kiRoot "data\input") | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $kiRoot "bin\game\IL2Series.exe"), "fake", $kiUtf8)
}
[System.IO.File]::WriteAllText((Join-Path $kiApp "Launcher.exe"), "fake", $kiUtf8)
[System.IO.File]::WriteAllText((Join-Path $kiOther "Launcher.exe"), "fake", $kiUtf8)
New-Item -ItemType Directory -Force -Path (Join-Path $kiApp "cache") | Out-Null
Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2korea\input\*") -Destination (Join-Path $kiGame "data\input") -Force
New-Item -ItemType Directory -Force -Path (Join-Path $kiSteamDrive "SteamLibrary\steamapps") | Out-Null
[System.IO.File]::WriteAllText((Join-Path $kiSteamDrive "SteamLibrary\steamapps\appmanifest_247970.acf"),
    "`"AppState`"`n{`n`t`"appid`"`t`t`"247970`"`n`t`"name`"`t`t`"Korea. IL-2 Series`"`n`t`"installdir`"`t`t`"IL2Series`"`n}`n", $kiUtf8)
$kiUninstall = Join-Path $kiDir "uninstall.txt"
[System.IO.File]::WriteAllText($kiUninstall,
    "[{222bc1b4-be6e-4451-9291-ef5aeb7c64a2}_is1]`r`nDisplayName=Korea. IL2 Series`r`nInstallLocation=" + $kiOther + "\`r`n" +
    "[Steam App 15320]`r`nDisplayName=IL-2 Sturmovik: 1946`r`nInstallLocation=" + $kiDir + "`r`n", $kiUtf8)

$kiPrevious = @{
    Data = $env:JOYPRO_DATA_DIR; Drives = $env:JOYPRO_TEST_DRIVES; Uninstall = $env:JOYPRO_TEST_UNINSTALL_FILE
    Detection = $env:JOYPRO_INSTALL_DETECTION; Assume = $env:JOYPRO_TEST_ASSUME_INSTALLED
}
$env:JOYPRO_DATA_DIR = Join-Path $kiDir "data"
$kiPreviousFake = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\il2korea\devices.tsv"
$env:JOYPRO_INSTALL_DETECTION = $null
$env:JOYPRO_TEST_ASSUME_INSTALLED = $null
try {
    # Steam: the appmanifest of a Steam library.
    $env:JOYPRO_TEST_DRIVES = $kiSteamDrive
    $env:JOYPRO_TEST_UNINSTALL_FILE = Join-Path $kiDir "none.txt"
    $kiFound = (Run-Engine @("games", "installs", "--game", "IL2Korea")).Output | ConvertFrom-Json
    Assert-True ($kiFound.Games[0].Installed -and $kiFound.Games[0].InstallPath -eq $kiSteam -and $kiFound.Games[0].Source -eq "steam") ("IL-2 Korea on Steam not found: " + ($kiFound | ConvertTo-Json -Depth 4))

    # Standalone in a usual folder (<drive>\Games\IL2Series): the game folder is its "game" subfolder.
    $env:JOYPRO_TEST_DRIVES = $kiStandaloneDrive
    $kiFound = (Run-Engine @("games", "installs", "--game", "IL2Korea")).Output | ConvertFrom-Json
    Assert-True ($kiFound.Games[0].Installed -and $kiFound.Games[0].InstallPath -eq $kiGame -and $kiFound.Games[0].MarkerFound) ("the standalone IL-2 Korea in Games\IL2Series was not found as " + $kiGame + ": " + ($kiFound | ConvertTo-Json -Depth 4))
    $kiPaths = Run-Engine @("il2korea", "paths", "--text")
    Assert-Match $kiPaths.Output ([regex]::Escape((Join-Path $kiGame "data\input\general.actions"))) ("il2korea paths did not find the standalone key mapping: " + $kiPaths.Output)

    # Standalone in any folder: the installer's uninstall entry (AppId key, "Korea. IL2 Series", launcher folder).
    Remove-Item -Recurse -Force $kiApp
    $env:JOYPRO_TEST_UNINSTALL_FILE = $kiUninstall
    $kiFound = (Run-Engine @("games", "installs", "--game", "IL2Korea")).Output | ConvertFrom-Json
    Assert-True ($kiFound.Games[0].Installed -and $kiFound.Games[0].InstallPath -eq (Join-Path $kiOther "game") -and $kiFound.Games[0].Source -eq "Windows apps list") ("the standalone IL-2 Korea was not found from its uninstall entry: " + ($kiFound | ConvertTo-Json -Depth 4))
    $kiMissing = Run-Engine @("il2korea", "paths", "--text")
    Assert-Match $kiMissing.Output "no key mapping file yet .*press ACCEPT" ("il2korea paths should say the game has not written its key mapping yet: " + $kiMissing.Output)
    $kiScanMissing = Run-Engine @("il2korea", "scan", (Join-Path $kiOther "Launcher.exe"), "--text")
    Assert-True ($kiScanMissing.ExitCode -ne 0) "il2korea scan of an installation without a key mapping should fail."
    Assert-Match $kiScanMissing.Output ([regex]::Escape((Join-Path $kiOther "game\data\input\general.actions")) + ".*ACCEPT") ("il2korea scan should name the file the game writes and how: " + $kiScanMissing.Output)

    # Nothing found: the user's folder, whatever they pick.
    $env:JOYPRO_TEST_UNINSTALL_FILE = Join-Path $kiDir "none.txt"
    $env:JOYPRO_TEST_DRIVES = Join-Path $kiDir "no-drive"
    New-Item -ItemType Directory -Force -Path $kiApp, (Join-Path $kiGame "bin\game"), (Join-Path $kiGame "data\input"), (Join-Path $kiApp "cache") | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $kiGame "bin\game\IL2Series.exe"), "fake", $kiUtf8)
    [System.IO.File]::WriteAllText((Join-Path $kiApp "Launcher.exe"), "fake", $kiUtf8)
    Copy-Item -Path (Join-Path $engineDir "tests\fixtures\il2korea\input\*") -Destination (Join-Path $kiGame "data\input") -Force
    $kiNone = (Run-Engine @("games", "installs", "--game", "IL2Korea")).Output | ConvertFrom-Json
    Assert-True (-not $kiNone.Games[0].Installed) "IL-2 Korea should not be found without Steam, places or uninstall entry."
    $kiPicks = @(
        @{ Pick = $kiApp; Root = $kiGame },
        @{ Pick = (Join-Path $kiApp "Launcher.exe"); Root = $kiGame },
        @{ Pick = $kiGame; Root = $kiGame },
        @{ Pick = (Join-Path $kiGame "bin\game"); Root = $kiGame },
        @{ Pick = (Join-Path $kiGame "bin\game\IL2Series.exe"); Root = $kiGame },
        @{ Pick = (Join-Path $kiGame "data\input"); Root = $kiGame },
        @{ Pick = (Join-Path $kiApp "cache"); Root = $kiGame },
        @{ Pick = (Join-Path $kiSteam "bin\game\IL2Series.exe"); Root = $kiSteam },
        @{ Pick = $kiSteam; Root = $kiSteam }
    )
    foreach ($kiPick in $kiPicks) {
        $kiSet = Run-Engine @("games", "install", "set", "IL2Korea", $kiPick.Pick)
        Assert-True ($kiSet.ExitCode -eq 0) ("games install set IL2Korea " + $kiPick.Pick + " failed: " + $kiSet.Output)
        $kiSetJson = $kiSet.Output | ConvertFrom-Json
        Assert-True ($kiSetJson.Games[0].Installed -and $kiSetJson.Games[0].MarkerFound -and $kiSetJson.Games[0].InstallPath -eq $kiPick.Root) ("picking " + $kiPick.Pick + " should make " + $kiPick.Root + " the installation: " + $kiSet.Output)
    }
    $null = Run-Engine @("games", "install", "set", "IL2Korea", (Join-Path $kiApp "Launcher.exe"))
    $kiScan = Run-Engine @("il2korea", "scan", "--text")
    Assert-True ($kiScan.ExitCode -eq 0) ("il2korea scan of the standalone set by its launcher failed: " + $kiScan.Output)
    Assert-Match $kiScan.Output "(?m)^Bindings Read:\s+10\s*$" ("il2korea scan should read the standalone key mapping: " + $kiScan.Output)
    foreach ($kiExplicit in @($kiApp, (Join-Path $kiApp "Launcher.exe"), (Join-Path $kiGame "bin\game\IL2Series.exe"), (Join-Path $kiGame "data\input"))) {
        $kiScanAt = Run-Engine @("il2korea", "scan", $kiExplicit, "--text")
        Assert-Match $kiScanAt.Output "(?m)^Bindings Read:\s+10\s*$" ("il2korea scan " + $kiExplicit + " should find general.actions: " + $kiScanAt.Output)
    }
    # An old setting that kept the picked exe as it was still works.
    [System.IO.File]::WriteAllText((Join-Path $env:JOYPRO_DATA_DIR "game-installs.json"),
        "{`r`n  `"IL2Korea`": `"" + (Join-Path $kiGame "bin\game\IL2Series.exe").Replace("\", "\\") + "`"`r`n}`r`n", $kiUtf8)
    $kiOld = (Run-Engine @("games", "installs", "--game", "IL2Korea")).Output | ConvertFrom-Json
    Assert-True ($kiOld.Games[0].Installed -and $kiOld.Games[0].InstallPath -eq $kiGame) ("a saved exe path should count as its game folder: " + ($kiOld | ConvertTo-Json -Depth 4))
    $kiMissingSet = Run-Engine @("games", "install", "set", "IL2Korea", (Join-Path $kiApp "no-such.exe"))
    Assert-True ($kiMissingSet.ExitCode -ne 0) "games install set accepted a file that does not exist."
}
finally {
    $env:JOYPRO_DATA_DIR = $kiPrevious.Data
    $env:JOYPRO_FAKE_DEVICES = $kiPreviousFake
    $env:JOYPRO_TEST_DRIVES = $kiPrevious.Drives
    $env:JOYPRO_TEST_UNINSTALL_FILE = $kiPrevious.Uninstall
    $env:JOYPRO_INSTALL_DETECTION = $kiPrevious.Detection
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $kiPrevious.Assume
}
