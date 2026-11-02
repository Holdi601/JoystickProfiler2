# Commands that DCS mods add (docs/games/dcs.md "Commands from mods"; DcsModInputs, DcsFoundCommands). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin, the test sandbox).
# A tester's F-16C showed "C & I Knob - UFC else BACKUP (2-way Switch)" (Quaggles' Input Command Injector with a
# community pack in Saved Games\DCS.openbeta\InputCommands) that JoyPro's list did not have.
#  A. The refresh reads the Saved Games folder of the install's DCS variant (DCS.openbeta for an OpenBeta install, also
#     when Saved Games\DCS is given) and merges InputCommands when the injector is installed (auto), on, or not (off).
#  B. db check-updates: a list recorded before JoyPro knew the mods counts as outdated when mod input files exist (none:
#     no prompt), again when they change, are removed, or the injector gets installed; a refresh makes it current. The
#     safety net: a command of the user's .diff.lua that the list lacks is known ("found in your DCS files", exact id and
#     name, a hint in relations aircraft) and suggests a rebuild; a rebuild or "Keep DB" stops asking.
#  C. With the real DCS install on this PC (read only) and a temporary Saved Games: the headless refresh puts the pack's
#     commands into F-16C_50 and AJS37 with DCS's own ids, and check-updates goes from outdated to current.
# Nothing is written into a real Saved Games folder or the DCS install (sandbox; the real Config\Input is compared).

function Get-McJson {
    param([string]$Text)
    $start = $Text.IndexOf('{')
    $end = $Text.LastIndexOf('}')
    Assert-True ($start -ge 0 -and $end -gt $start) ("dcsmodcommands: no JSON in the output: " + $Text)
    return $Text.Substring($start, $end - $start + 1) | ConvertFrom-Json
}

function Get-McDcsState {
    $check = Run-Engine @("db", "check-updates")
    Assert-True ($check.ExitCode -eq 0) ("dcsmodcommands: db check-updates failed: " + $check.Output)
    return @((Get-McJson $check.Output).Games | Where-Object { $_.Game -eq "DCS" })[0]
}

function Get-McInputFingerprint {
    $parts = @()
    foreach ($dir in @(Get-ChildItem -Path (Join-Path $env:USERPROFILE "Saved Games") -Directory -Filter "DCS*" -ErrorAction SilentlyContinue)) {
        $mcCfgInput = Join-Path $dir.FullName "Config\Input"
        if (-not (Test-Path $mcCfgInput)) { continue }
        $files = @(Get-ChildItem -Path $mcCfgInput -Recurse -File -ErrorAction SilentlyContinue)
        $newest = ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
        $parts += ($dir.Name + ":" + $files.Count + ":" + $(if ($newest) { $newest.LastWriteTimeUtc.Ticks } else { 0 }))
    }
    return ($parts -join "|")
}

$mcDir = Join-Path $tempDir "dcsmodcommands"
$mcFixture = Join-Path $engineDir "tests\fixtures\dcsmodcommands\InputCommands"
$mcRealInputBefore = Get-McInputFingerprint
$mcPreviousData = $env:JOYPRO_DATA_DIR
$mcCsc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Assert-True (Test-Path $mcCsc) "dcsmodcommands: the .NET Framework compiler (csc.exe) is needed for the fake luae.exe."

# A fake DCS install with the stand-in luae.exe (tests\fixtures\fake-luae) and one module (unit Alpha-1).
function New-McInstall {
    param([string]$Path, [string]$Variant)
    New-Item -Path (Join-Path $Path "bin"), (Join-Path $Path "Mods\aircraft\ModA\Input\Alpha-1\joystick") -ItemType Directory -Force | Out-Null
    & $mcCsc /nologo /target:exe ("/out:" + (Join-Path $Path "bin\luae.exe")) (Join-Path $engineDir "tests\fixtures\fake-luae\FakeLuae.cs") | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dcsmodcommands: building the fake luae.exe failed."
    Set-Content -Path (Join-Path $Path "Mods\aircraft\ModA\Input\Alpha-1\joystick\default.lua") -Value "return {}" -Encoding ASCII
    [System.IO.File]::WriteAllText((Join-Path $Path "Mods\aircraft\ModA\entry.lua"), "declare_plugin(`"ModA`", {`r`n  InputProfiles = {`r`n    [`"Alpha-1`"] = current_mod_path .. '/Input/Alpha-1',`r`n  },`r`n})`r`n")
    [System.IO.File]::WriteAllText((Join-Path $Path "autoupdate.cfg"), "{`r`n `"version`": `"2.9.99.1`",`r`n `"arch`": `"x86_64`"`r`n}`r`n")
    if ($Variant) { [System.IO.File]::WriteAllText((Join-Path $Path "dcs_variant.txt"), $Variant) }
}

# The Input Command Injector as its 1.1.0 hook installs it (only its marker matters to JoyPro).
function Install-McInjectorHook {
    param([string]$Install)
    New-Item -Path (Join-Path $Install "Scripts\Hooks") -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $Install "Scripts\Hooks\QuagglesInputCommandInjector.lua") -Encoding ASCII -Value "local quagglesLogName = 'Quaggles.InputCommandInjector' -- JoyPro smoke stand-in"
}

try {
    # ---- A. The Saved Games folder of the install's variant, and when InputCommands are merged ----
    $env:JOYPRO_DATA_DIR = Join-Path $mcDir "data-a"
    $mcBeta = Join-Path $mcDir "DCS World OpenBeta"
    New-McInstall $mcBeta "openbeta"
    $mcSavedA = Join-Path $mcDir "saved-a"
    New-Item -Path (Join-Path $mcSavedA "DCS\Config\Input"), (Join-Path $mcSavedA "DCS.openbeta\Config\Input") -ItemType Directory -Force | Out-Null
    Copy-Item -Path $mcFixture -Destination (Join-Path $mcSavedA "DCS.openbeta") -Recurse
    $mcArgsA = @("dcs", "refresh", "id", "--path", (Join-Path $mcSavedA "DCS"), "--install-path", $mcBeta, "--headless-lua", "true", "--confirm", "false", "--dry-run")
    $mcA = Run-Engine $mcArgsA
    Assert-True ($mcA.ExitCode -eq 0) ("dcsmodcommands: dcs refresh id --dry-run failed: " + $mcA.Output)
    $mcAJson = Get-McJson $mcA.Output
    Assert-True ($mcAJson.SavedGamesPath -like "*\DCS.openbeta") ("an OpenBeta install must read Saved Games\DCS.openbeta, not " + $mcAJson.SavedGamesPath)
    Assert-True ([string]::IsNullOrEmpty($mcAJson.InputCommandsPath)) "InputCommands must not be merged when the injector is not installed (auto)."
    Assert-True ([string]::IsNullOrEmpty($mcAJson.InputCommandInjector)) "no injector is installed in the fake DCS."
    $mcOn = Get-McJson (Run-Engine ($mcArgsA + @("--input-commands", "on"))).Output
    Assert-True ($mcOn.InputCommandsPath -like "*\DCS.openbeta\InputCommands") ("--input-commands on must merge DCS.openbeta\InputCommands: " + $mcOn.InputCommandsPath)
    Install-McInjectorHook $mcBeta
    $mcAuto = Get-McJson (Run-Engine $mcArgsA).Output
    Assert-True ($mcAuto.InputCommandInjector -eq "hook") ("the injector's hook in Scripts\Hooks must be found: " + $mcAuto.InputCommandInjector)
    Assert-True ($mcAuto.InputCommandsPath -like "*\DCS.openbeta\InputCommands") "with the injector installed, auto must merge InputCommands."
    $mcOff = Get-McJson (Run-Engine ($mcArgsA + @("--input-commands", "off"))).Output
    Assert-True ([string]::IsNullOrEmpty($mcOff.InputCommandsPath)) "--input-commands off must leave InputCommands out."
    $mcBad = Run-Engine ($mcArgsA + @("--input-commands", "maybe"))
    Assert-True ($mcBad.ExitCode -ne 0) "an invalid --input-commands value must be refused."

    # ---- B. Outdated detection and the safety net (fake DCS, fake luae) ----
    $env:JOYPRO_DATA_DIR = Join-Path $mcDir "data-b"
    $mcInstall = Join-Path $mcDir "DCS World"
    New-McInstall $mcInstall $null
    $mcSaved = Join-Path $mcDir "saved-b\DCS"
    New-Item -Path (Join-Path $mcSaved "Config\Input") -ItemType Directory -Force | Out-Null
    $mcLists = Join-Path $env:JOYPRO_DATA_DIR "GameData"
    New-Item -Path (Join-Path $mcLists "DB\DCS"), (Join-Path $mcLists "CleanProfile\DCS") -ItemType Directory -Force | Out-Null
    $mcRow = { param($t, $c, $i) "        <tr>`r`n          <td></td>`r`n          <td>$t</td>`r`n          <td>$c</td>`r`n          <td>$i</td>`r`n        </tr>" }
    $mcHtml = "<section>`r`n    <table >`r`n        <tr>`r`n          <th>Combos</th>`r`n          <th>Command</th>`r`n          <th>Category</th>`r`n          <th>Hash</th>`r`n        </tr>`r`n" +
              (& $mcRow "Smoke Gear Up" "Systems" "d3001pnilunilcd5vd1vpnilvunil") + "`r`n" + (& $mcRow "Smoke Pitch" "Flight Control" "a2001cdnil") + "`r`n    </table>`r`n</section>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $mcLists "DB\DCS\Alpha-1.html"), $mcHtml)
    [System.IO.File]::WriteAllText((Join-Path $mcLists "CleanProfile\DCS\Alpha-1.cf"), "local diff = {}`r`nreturn diff`r`n")
    foreach ($mcCfg in @(@("games", "install", "set", "DCS", $mcInstall), @("games", "config", "set", "DCS", $mcSaved))) {
        $mcSet = Run-Engine $mcCfg
        Assert-True ($mcSet.ExitCode -eq 0) ("dcsmodcommands: " + ($mcCfg -join " ") + " failed: " + $mcSet.Output)
    }

    # An existing user: the list was recorded before JoyPro knew the mods. No mod sources: nothing to ask.
    $mcKeep = Run-Engine @("db", "check-updates", "--accept", "DCS")
    Assert-True ($mcKeep.ExitCode -eq 0) ("dcsmodcommands: check-updates --accept DCS failed: " + $mcKeep.Output)
    $mcState = Get-McDcsState
    Assert-True ($mcState.State -eq "up-to-date") ("a DCS without mod input files must stay up to date: " + ($mcState | ConvertTo-Json -Compress))

    # The injector's folder appears (the tester's case after updating JoyPro): outdated at once, with the plain reason.
    Copy-Item -Path $mcFixture -Destination $mcSaved -Recurse
    Set-Content -Path (Join-Path $mcSaved "InputCommands\settings.lua") -Value "settings = {}" -Encoding ASCII
    $mcState = Get-McDcsState
    Assert-True ($mcState.State -eq "game-updated" -and $mcState.Reason -eq "mods") ("InputCommands must make the DCS list outdated (reason mods): " + ($mcState | ConvertTo-Json -Compress))
    Assert-Match $mcState.Message "Your DCS mods changed the controls list; rebuild the command list" "the outdated message must say what to do in plain words."
    Assert-Match (($mcState.Details) -join " ") "3 InputCommands file" "the details must count the InputCommands files (not the injector's settings.lua)."

    # The injector's own settings and state (rewritten by every DCS start) do not count.
    Set-Content -Path (Join-Path $mcSaved "InputCommands\state.lua") -Value "state = {}" -Encoding ASCII
    $mcRefreshArgs = @("dcs", "refresh", "id", "--path", $mcSaved, "--install-path", $mcInstall, "--headless-lua", "true", "--confirm", "false")
    $mcRefresh = Run-Engine $mcRefreshArgs
    Assert-True ($mcRefresh.ExitCode -eq 0) ("dcsmodcommands: dcs refresh id (fake luae) failed: " + $mcRefresh.Output)
    Assert-True ([string]::IsNullOrEmpty((Get-McJson $mcRefresh.Output).InputCommandsPath)) "without the injector, the refresh must leave InputCommands out (auto)."
    $mcState = Get-McDcsState
    Assert-True ($mcState.State -eq "up-to-date") ("after the rebuild the DCS list must be up to date: " + ($mcState | ConvertTo-Json -Compress))
    Set-Content -Path (Join-Path $mcSaved "InputCommands\state.lua") -Value "state = { lastUpdateCheck = 1 }" -Encoding ASCII
    Assert-True ((Get-McDcsState).State -eq "up-to-date") "the injector's state.lua must not make the list outdated."

    # Installing the injector changes what DCS shows: outdated again; the refresh then merges (fake luae: no real ids).
    Install-McInjectorHook $mcInstall
    $mcState = Get-McDcsState
    Assert-True ($mcState.State -eq "game-updated" -and $mcState.Reason -eq "mods") "installing the injector must make the list outdated."
    $mcRefresh = Run-Engine $mcRefreshArgs
    Assert-True ($mcRefresh.ExitCode -eq 0) ("dcsmodcommands: dcs refresh id with the injector failed: " + $mcRefresh.Output)
    Assert-True ((Get-McJson $mcRefresh.Output).InputCommandsPath -like "*\InputCommands") "with the injector installed, the refresh must merge InputCommands."
    Assert-True ((Get-McDcsState).State -eq "up-to-date") "after the rebuild with the injector the list must be up to date."

    # A changed pack file, then a removed pack: outdated each time.
    Add-Content -Path (Join-Path $mcSaved "InputCommands\F-16C\Input\F-16C\joystick\default.lua") -Value "-- changed" -Encoding ASCII
    Assert-True ((Get-McDcsState).Reason -eq "mods") "a changed InputCommands file must make the list outdated."
    Remove-Item -Path (Join-Path $mcSaved "InputCommands") -Recurse -Force
    $mcState = Get-McDcsState
    Assert-True ($mcState.Reason -eq "mods" -and ((($mcState.Details) -join " ") -match "removed")) ("removed mod files must make the list outdated: " + ($mcState | ConvertTo-Json -Compress))
    Assert-True ((Run-Engine @("db", "check-updates", "--accept", "DCS")).ExitCode -eq 0) "check-updates --accept DCS failed."
    Assert-True ((Get-McDcsState).State -eq "up-to-date") "Keep DB must make the list current."

    # A module in Saved Games\Mods\aircraft is a new module; a later change of its input files is a mod change.
    $mcModInput = Join-Path $mcSaved "Mods\aircraft\SmokeMod\Input\SmokeMod\joystick"
    New-Item -Path $mcModInput -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $mcModInput "default.lua") -Value "return { keyCommands = {} }" -Encoding ASCII
    Assert-True ((Get-McDcsState).State -eq "game-updated") "a new Saved Games module must make the list outdated."
    Assert-True ((Run-Engine @("db", "check-updates", "--accept", "DCS")).ExitCode -eq 0) "check-updates --accept DCS failed."
    Add-Content -Path (Join-Path $mcModInput "default.lua") -Value "-- a new command" -Encoding ASCII
    Assert-True ((Get-McDcsState).Reason -eq "mods") "a changed input file of a Saved Games module must count as a mod change."
    Assert-True ((Run-Engine @("db", "check-updates", "--accept", "DCS")).ExitCode -eq 0) "check-updates --accept DCS failed."

    # The safety net: a .diff.lua binds a command the list lacks (exact id and DCS's name), another one the list has,
    # and one of an aircraft without a list (not recorded: without a list every command would be "missing"). The list
    # is the one made above (the fake luae's refreshes wrote a list without commands).
    [System.IO.File]::WriteAllText((Join-Path $mcLists "DB\DCS\Alpha-1.html"), $mcHtml)
    $mcDiffDir = Join-Path $mcSaved "Config\Input\Alpha-1\joystick"
    New-Item -Path $mcDiffDir, (Join-Path $mcSaved "Config\Input\NoList-9\joystick") -ItemType Directory -Force | Out-Null
    $mcDiff = "local diff = {`r`n`t[`"keyDiffs`"] = {`r`n" +
              "`t`t[`"d3001pnilu3001cd35vd1vpnilvu0`"] = {`r`n`t`t`t[`"added`"] = {`r`n`t`t`t`t[1] = {`r`n`t`t`t`t`t[`"key`"] = `"JOY_BTN11`",`r`n`t`t`t`t`t[`"JPK`"] = `"Smoke Mod Relation`",`r`n`t`t`t`t},`r`n`t`t`t},`r`n`t`t`t[`"name`"] = `"Smoke mod command - UFC else BACKUP (2-way Switch)`",`r`n`t`t},`r`n" +
              "`t`t[`"d3001pnilunilcd5vd1vpnilvunil`"] = {`r`n`t`t`t[`"added`"] = {`r`n`t`t`t`t[1] = {`r`n`t`t`t`t`t[`"key`"] = `"JOY_BTN12`",`r`n`t`t`t`t},`r`n`t`t`t},`r`n`t`t`t[`"name`"] = `"Smoke Gear Up`",`r`n`t`t},`r`n" +
              "`t},`r`n}`r`nreturn diff`r`n"
    [System.IO.File]::WriteAllText((Join-Path $mcDiffDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"), $mcDiff)
    [System.IO.File]::WriteAllText((Join-Path $mcSaved "Config\Input\NoList-9\joystick\Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"), $mcDiff)
    $mcScan = Run-Engine @("dcs", "scan", $mcSaved, "--text")
    Assert-True ($mcScan.ExitCode -eq 0) ("dcsmodcommands: dcs scan failed: " + $mcScan.Output)
    Assert-Match $mcScan.Output "Commands Found In Your DCS Files \(not in the command list\): 1" ("dcs scan must find exactly the one command the list lacks: " + $mcScan.Output)
    $mcFound = Join-Path $mcLists "DcsCommandsFromYourFiles.tsv"
    Assert-True (Test-Path $mcFound) "the found commands must be kept in the game-data folder."
    $mcFoundText = [System.IO.File]::ReadAllText($mcFound)
    Assert-Match $mcFoundText "Alpha-1\td3001pnilu3001cd35vd1vpnilvu0\tSmoke mod command - UFC else BACKUP \(2-way Switch\)\tkey\tnew" "the found command must keep the file's exact id and name."
    Assert-NotMatch $mcFoundText "NoList-9" "an aircraft without a command list must not record commands."
    Assert-NotMatch $mcFoundText "Smoke Gear Up" "a command the list has must not be recorded."
    $mcState = Get-McDcsState
    Assert-True ($mcState.State -eq "game-updated" -and $mcState.Reason -eq "binds") ("commands of the bind files the list lacks must suggest a rebuild: " + ($mcState | ConvertTo-Json -Compress))
    Assert-Match (($mcState.Details) -join " ") "Smoke mod command" "the details must name the command found in the bind files."

    # JoyPro knows the command now: the relation's aircraft show it with its name and the "from your DCS files" hint.
    $mcProfile = Join-Path $mcDir "found.pr0file"
    $mcImport = Run-Engine @("--profile", $mcProfile, "--remap-missing-devices", "false", "dcs", "import", $mcSaved, "--planes", "Alpha-1", "--confirm", "false", "--text")
    Assert-True ($mcImport.ExitCode -eq 0) ("dcsmodcommands: dcs import failed: " + $mcImport.Output)
    $mcAircraft = Run-Engine @("--profile", $mcProfile, "relations", "aircraft", "Smoke Mod Relation")
    Assert-True ($mcAircraft.ExitCode -eq 0) ("dcsmodcommands: relations aircraft failed: " + $mcAircraft.Output)
    $mcNode = @((Get-McJson $mcAircraft.Output).Nodes)[0]
    $mcEntry = @($mcNode.Aircraft | Where-Object { $_.Plane -eq "Alpha-1" })[0]
    Assert-True ($null -ne $mcEntry -and $mcEntry.FromYourFiles -eq $true -and $mcEntry.Match -ne "unknown") ("the found command must be known, with the hint: " + $mcAircraft.Output)
    Assert-True ($mcEntry.Title -eq "Smoke mod command - UFC else BACKUP (2-way Switch)") ("the found command must show DCS's name: " + $mcEntry.Title)

    # A rebuild that does not bring it (here: the fake luae) keeps it known, without asking again.
    $mcRefresh = Run-Engine $mcRefreshArgs
    Assert-True ($mcRefresh.ExitCode -eq 0) ("dcsmodcommands: dcs refresh id after the scan failed: " + $mcRefresh.Output)
    Assert-True ((Get-McDcsState).State -eq "up-to-date") "after a rebuild the found commands must not ask for another one."
    Assert-Match ([System.IO.File]::ReadAllText($mcFound)) "d3001pnilu3001cd35vd1vpnilvu0\t[^\t]*\tkey\tkept" "a found command the rebuild did not bring must stay known (kept)."

    # ---- C. The real DCS install (read only) with a temporary Saved Games ----
    $mcRealInstall = @("G:\Program Files\Eagle Dynamics\DCS World", "C:\Program Files\Eagle Dynamics\DCS World", "D:\Program Files\Eagle Dynamics\DCS World") |
        Where-Object { (Test-Path (Join-Path $_ "bin\luae.exe")) -and (Test-Path (Join-Path $_ "Mods\aircraft\F-16C\entry.lua")) } | Select-Object -First 1
    if (-not $mcRealInstall -or -not $script:HasDevGameData) {
        Write-Host "SKIP dcsmodcommands part C: no DCS install with the F-16C (or no development game lists) on this PC."
    }
    else {
        $env:JOYPRO_DATA_DIR = Join-Path $mcDir "data-c"
        $mcRealSaved = Join-Path $mcDir "saved-c\DCS"
        New-Item -Path (Join-Path $mcRealSaved "Config\Input") -ItemType Directory -Force | Out-Null
        foreach ($mcCfg in @(@("games", "install", "set", "DCS", $mcRealInstall), @("games", "config", "set", "DCS", $mcRealSaved))) {
            Assert-True ((Run-Engine $mcCfg).ExitCode -eq 0) ("dcsmodcommands: " + ($mcCfg -join " ") + " failed.")
        }
        Assert-True ((Run-Engine @("db", "check-updates", "--accept", "DCS")).ExitCode -eq 0) "check-updates --accept DCS (real DCS) failed."
        Copy-Item -Path $mcFixture -Destination $mcRealSaved -Recurse
        $mcState = Get-McDcsState
        Assert-True ($mcState.State -eq "game-updated" -and $mcState.Reason -eq "mods") ("the real DCS list must be outdated once InputCommands exist: " + ($mcState | ConvertTo-Json -Compress))
        # The user's DCS has no injector: --input-commands on merges as the injector would.
        $mcReal = Run-Engine @("dcs", "refresh", "id", "--path", $mcRealSaved, "--install-path", $mcRealInstall, "--headless-lua", "true", "--planes", "F-16C_50,AJS37", "--input-commands", "on", "--confirm", "false")
        Assert-True ($mcReal.ExitCode -eq 0) ("dcsmodcommands: the real headless refresh failed: " + $mcReal.Output)
        $mcRealJson = Get-McJson $mcReal.Output
        Assert-SetEquivalent @($mcRealJson.InputCommandsFilesMerged) @("Aircrafts/common_joystick_binding.lua", "AJS37/Input/joystick/default.lua", "F-16C/Input/F-16C/joystick/default.lua") "the refresh must merge the pack's three files the way the injector maps them."
        $mcF16 = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\DCS\F-16C_50.html"))
        Assert-Match $mcF16 "JoyPro test C &amp; I Knob - UFC else BACKUP \(2-way Switch\)</td>\s*<td>[^<]*</td>\s*<td>d3001pnilu3001cd35vd1vpnilvu0</td>|JoyPro test C & I Knob - UFC else BACKUP \(2-way Switch\)</td>\s*<td>[^<]*</td>\s*<td>d3001pnilu3001cd35vd1vpnilvu0</td>" "F-16C_50 must have the pack's 'UFC else BACKUP' command with DCS's id d3001pnilu3001cd35vd1vpnilvu0."
        Assert-Match $mcF16 "BACKUP else UFC \(2-way Switch\)</td>\s*<td>[^<]*</td>\s*<td>d3001pnilu3001cd35vd0vpnilvu1</td>" "F-16C_50 must have the pack's 'BACKUP else UFC' command with DCS's id d3001pnilu3001cd35vd0vpnilvu1."
        Assert-Match $mcF16 "JoyPro test common command" "the common_joystick_binding.lua of the pack must reach the F-16C_50."
        $mcAjs = [System.IO.File]::ReadAllText((Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\DCS\AJS37.html"))
        Assert-Match $mcAjs "JoyPro test autothrottle \(hold\)</td>\s*<td>[^<]*</td>\s*<td>d3402pnilu3402cd\d+vd1vpnilvu0</td>" "AJS37 (input files without a profile folder) must have the pack's command."
        $mcState = Get-McDcsState
        Assert-True ($mcState.State -eq "up-to-date") ("after the rebuild the real DCS list must be up to date: " + ($mcState | ConvertTo-Json -Compress))
    }
}
finally {
    $env:JOYPRO_DATA_DIR = $mcPreviousData
}
Assert-True ((Get-McInputFingerprint) -eq $mcRealInputBefore) "dcsmodcommands: the real Saved Games\DCS*\Config\Input changed during the test."
