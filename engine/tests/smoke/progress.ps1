# Live progress of long commands (EngineProgress, --status / --progress, engine/docs/ENGINE_CLI_WIKI.md). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin).
# A DCS install with a stand-in for DCS's bin\luae.exe (tests\fixtures\fake-luae, built here with the .NET Framework
# csc.exe): three modules with four input profiles. The headless DCS refresh must print one progress line per module,
# per profile and per written file with an increasing count and the right total; plain output has none; --progress
# prints them without the status chatter; a cancel stops the refresh before anything is written (exit code 3).

function Get-ProgressLines {
    param([string]$Text, [string]$Phase)
    $found = @()
    foreach ($line in ($Text -split "`r?`n")) {
        $m = [Regex]::Match($line, '^\[engine-progress\] (\d+)/(\d+|\?) ([^:]+?)(?:: (.*))?$')
        if ($m.Success -and $m.Groups[3].Value -eq $Phase) {
            $found += [PSCustomObject]@{ Current = [int]$m.Groups[1].Value; Total = $m.Groups[2].Value; Item = $m.Groups[4].Value }
        }
    }
    return ,$found
}

function Assert-ProgressCounts {
    param([string]$Text, [string]$Phase, [int]$Total, [string]$What)
    $lines = Get-ProgressLines $Text $Phase
    Assert-True ($lines.Count -ge $Total) ("$What should print a progress line per item of '" + $Phase + "' (got " + $lines.Count + "): " + $Text)
    $previous = 0
    foreach ($line in $lines) {
        Assert-True ($line.Total -eq [string]$Total) ("$What should report the total " + $Total + " for '" + $Phase + "', not " + $line.Total + ".")
        Assert-True ($line.Current -ge $previous) ("$What progress must not go backwards in '" + $Phase + "' (" + $line.Current + " after " + $previous + ").")
        $previous = $line.Current
    }
    Assert-True ($lines[0].Current -eq 1) ("$What progress of '" + $Phase + "' should start at 1, not " + $lines[0].Current + ".")
    Assert-True ($previous -eq $Total) ("$What progress of '" + $Phase + "' should end at " + $Total + ", not " + $previous + ".")
    return $lines
}

$pgDir = Join-Path $tempDir "progress"
$pgInstall = Join-Path $pgDir "dcs-install"
$pgSaved = Join-Path $pgDir "saved-games"
$pgRoot = Join-Path $pgDir "joypro"
New-Item -Path (Join-Path $pgInstall "bin"), (Join-Path $pgSaved "Config\Input"), (Join-Path $pgRoot "DB\DCS"), (Join-Path $pgRoot "CleanProfile\DCS"), (Join-Path $pgRoot "KeyboardCleanProfile\DCS") -ItemType Directory -Force | Out-Null

$pgCsc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Assert-True (Test-Path $pgCsc) "The .NET Framework compiler (csc.exe) is needed for the fake luae.exe: $pgCsc"
$pgLuae = Join-Path $pgInstall "bin\luae.exe"
& $pgCsc /nologo /target:exe ("/out:" + $pgLuae) (Join-Path $engineDir "tests\fixtures\fake-luae\FakeLuae.cs") | Out-Host
Assert-True (($LASTEXITCODE -eq 0) -and (Test-Path $pgLuae)) "Building the fake luae.exe failed."

$pgModules = [ordered]@{ "ModA" = @("Alpha-1"); "ModB" = @("Bravo-2", "Bravo-2 Gunner"); "ModC" = @("Charlie-3") }
foreach ($module in $pgModules.Keys) {
    $moduleDir = Join-Path $pgInstall ("Mods\aircraft\" + $module)
    $profiles = @()
    foreach ($unit in $pgModules[$module]) {
        $joystick = Join-Path $moduleDir ("Input\" + $unit + "\joystick")
        New-Item -Path $joystick -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $joystick "default.lua") -Value "return {}" -Encoding ASCII
        $profiles += "    [`"$unit`"] = current_mod_path .. '/Input/$unit',"
    }
    $entry = "declare_plugin(`"$module`", {`r`n  InputProfiles = {`r`n" + ($profiles -join "`r`n") + "`r`n  },`r`n})`r`n"
    [System.IO.File]::WriteAllText((Join-Path $moduleDir "entry.lua"), $entry)
}
$pgUnits = @("Alpha-1", "Bravo-2", "Bravo-2 Gunner", "Charlie-3")
$pgRefreshArgs = @("dcs", "refresh", "id", "--path", $pgSaved, "--install-path", $pgInstall, "--headless-lua", "true", "--launch", "false", "--keep-running", "false", "--ocr", "false", "--confirm", "false", "--text")
$pgPreviousData = $env:JOYPRO_DATA_DIR
$pgPreviousCancel = $env:JOYPRO_CANCEL_FILE
$env:JOYPRO_DATA_DIR = Join-Path $pgDir "data"
# The refresh writes into the game-data folder (<data>\GameData), never into the program folder ($pgRoot).
$pgLists = Join-Path $env:JOYPRO_DATA_DIR "GameData"
Push-Location $pgRoot
try {
    # --status true: the human status lines and the progress lines (modules, profiles, written files).
    $pgRun = Run-Engine (@("--status", "true") + $pgRefreshArgs)
    Assert-True ($pgRun.ExitCode -eq 0) ("dcs refresh id with the fake luae.exe failed: " + $pgRun.Output)
    Assert-Match $pgRun.Output "\[engine-status\] " "--status true should still print the status lines."
    Assert-ProgressCounts $pgRun.Output "DCS World, reading aircraft modules" 3 "dcs refresh id" | Out-Null
    $pgInputs = Assert-ProgressCounts $pgRun.Output "DCS World, reading inputs" 4 "dcs refresh id"
    Assert-SetEquivalent ($pgInputs | ForEach-Object { $_.Item }) $pgUnits "dcs refresh id should name every profile it reads."
    Assert-ProgressCounts $pgRun.Output "DCS World, writing the command lists" 4 "dcs refresh id" | Out-Null
    foreach ($unit in $pgUnits) {
        Assert-True (Test-Path (Join-Path $pgLists ("DB\DCS\" + $unit + ".html"))) ("dcs refresh id should write the command list of " + $unit + ".")
    }
    Assert-True (@(Get-ChildItem -Path (Join-Path $pgRoot "DB\DCS") -File).Count -eq 0) "dcs refresh id must not write into the program folder."
    Assert-NotMatch $pgRun.Output "\[engine-cancel\]" "without JOYPRO_CANCEL_FILE the engine must not offer a cancel."

    # Plain output stays as it was: no progress or status lines.
    $pgPlain = Run-Engine $pgRefreshArgs
    Assert-True ($pgPlain.ExitCode -eq 0) ("dcs refresh id without --status failed: " + $pgPlain.Output)
    Assert-NotMatch $pgPlain.Output "\[engine-(progress|status)\]" "without --status/--progress the output must have no progress or status lines."

    # --progress true: only the machine-readable lines.
    $pgOnly = Run-Engine (@("--progress", "true") + $pgRefreshArgs)
    Assert-True ($pgOnly.ExitCode -eq 0) ("dcs refresh id --progress true failed: " + $pgOnly.Output)
    Assert-ProgressCounts $pgOnly.Output "DCS World, reading inputs" 4 "dcs refresh id --progress" | Out-Null
    Assert-NotMatch $pgOnly.Output "\[engine-status\]" "--progress true alone must not print the status lines."

    # The clean profile build: DCS's Lua clears the fake stick per profile, then the clean profiles are written.
    $pgClean = Run-Engine @("--progress", "true", "dcs", "refresh", "clean", "--path", $pgSaved, "--install-path", $pgInstall, "--headless-lua", "true", "--launch", "false", "--keep-running", "false", "--ocr", "false", "--confirm", "false", "--text")
    Assert-True ($pgClean.ExitCode -eq 0) ("dcs refresh clean with the fake luae.exe failed: " + $pgClean.Output)
    Assert-ProgressCounts $pgClean.Output "DCS World, clearing the binds" 4 "dcs refresh clean" | Out-Null
    Assert-ProgressCounts $pgClean.Output "DCS World, writing the clean profiles" 4 "dcs refresh clean" | Out-Null
    Assert-True (Test-Path (Join-Path $pgLists "CleanProfile\DCS\Bravo-2 Gunner.cf")) "dcs refresh clean should write the clean profile of every aircraft."
    Assert-True (@(Get-ChildItem -Path (Join-Path $pgRoot "CleanProfile\DCS") -File).Count -eq 0) "dcs refresh clean must not write into the program folder."

    # Cancel: the user presses Cancel while DCS's Lua reads the second profile. The engine stops luae.exe, writes
    # nothing into the DB and exits with code 3.
    Remove-Item -Path (Join-Path $pgLists "DB\DCS\*.html") -Force
    $pgCancelFile = Join-Path $pgDir "cancel.request"
    $env:JOYPRO_CANCEL_FILE = $pgCancelFile
    $env:FAKE_LUAE_CANCEL_AT = "2"
    try {
        $pgCancel = Run-Engine (@("--progress", "true") + $pgRefreshArgs)
    }
    finally {
        Remove-Item Env:\FAKE_LUAE_CANCEL_AT -ErrorAction SilentlyContinue
    }
    Assert-True ($pgCancel.ExitCode -eq 3) ("a cancelled dcs refresh id should exit with code 3: " + $pgCancel.ExitCode + " " + $pgCancel.Output)
    Assert-Match $pgCancel.Output "\[engine-cancel\] available" "dcs refresh id should offer the cancel while it reads."
    Assert-Match $pgCancel.Output "\[engine-cancel\] cancelled" "dcs refresh id should report the cancel."
    Assert-Match $pgCancel.Output "Cancelled by the user while reading the DCS aircraft inputs" ("the cancel should say where it stopped: " + $pgCancel.Output)
    Assert-True (@(Get-ChildItem -Path (Join-Path $pgLists "DB\DCS") -Filter "*.html").Count -eq 0) "a cancelled dcs refresh id must not write any command list."
    Assert-True (@(Get-Process -Name "luae" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $pgLuae }).Count -eq 0) "a cancelled dcs refresh id must stop luae.exe."
    Remove-Item -Path $pgCancelFile -Force -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $pgPreviousData
    $env:JOYPRO_CANCEL_FILE = $pgPreviousCancel
}

# A registry game's refresh-db: three steps; a cancel asked for before the catalog is written leaves it untouched.
$pwDir = Join-Path $tempDir "progress-warthunder"
$pwGame = Join-Path $pwDir "game"
$pwRoot = Join-Path $pwDir "joypro"
New-Item -Path $pwGame, (Join-Path $pwRoot "DB\WarThunder"), (Join-Path $pwRoot "DB\Functions"), (Join-Path $pwRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\WarThunder\*") -Destination (Join-Path $pwRoot "DB\WarThunder") -Force
Copy-Item -Path (Join-Path $engineBin "DB\Functions\WarThunder.tsv") -Destination (Join-Path $pwRoot "DB\Functions") -Force
Set-Content -Path (Join-Path $pwGame "gui.vromfs.bin") -Value "not read with --no-game-archives" -Encoding ASCII
$pwCatalog = Join-Path $pwDir "data\GameData\DB\WarThunder\actions.tsv"
$pwPreviousData = $env:JOYPRO_DATA_DIR
$pwPreviousCancel = $env:JOYPRO_CANCEL_FILE
$env:JOYPRO_DATA_DIR = Join-Path $pwDir "data"
Push-Location $pwRoot
try {
    $pwCancelFile = Join-Path $pwDir "cancel.request"
    Set-Content -Path $pwCancelFile -Value "cancel" -Encoding ASCII
    $env:JOYPRO_CANCEL_FILE = $pwCancelFile
    # No engine ran with this data folder yet: the list is still only in the program folder.
    $pwBefore = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $pwRoot "DB\WarThunder\actions.tsv")))
    $pwCancel = Run-Engine @("--progress", "true", "warthunder", "refresh-db", $pwGame, "--no-game-archives", "--text")
    Assert-True ($pwCancel.ExitCode -eq 3) ("a cancelled warthunder refresh-db should exit with code 3: " + $pwCancel.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($pwCatalog)) -eq $pwBefore) "a cancelled refresh-db must leave the catalog untouched."
    Remove-Item -Path $pwCancelFile -Force
    $env:JOYPRO_CANCEL_FILE = $pwPreviousCancel

    $pwRun = Run-Engine @("--status", "true", "warthunder", "refresh-db", $pwGame, "--no-game-archives", "--text")
    Assert-True ($pwRun.ExitCode -eq 0) ("warthunder refresh-db --status true failed: " + $pwRun.Output)
    $pwSteps = @(($pwRun.Output -split "`r?`n") | Where-Object { $_ -match '^\[engine-progress\] \d+/3 War Thunder, ' })
    Assert-True ($pwSteps.Count -eq 3) ("warthunder refresh-db should report its three steps: " + $pwRun.Output)
    for ($i = 0; $i -lt 3; $i++) {
        Assert-True ($pwSteps[$i].StartsWith("[engine-progress] " + ($i + 1) + "/3 ")) ("warthunder refresh-db step " + ($i + 1) + " is out of order: " + $pwSteps[$i])
    }
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $pwPreviousData
    $env:JOYPRO_CANCEL_FILE = $pwPreviousCancel
}
