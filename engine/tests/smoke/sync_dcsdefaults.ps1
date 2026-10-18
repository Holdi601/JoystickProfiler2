# Game config watcher, DCS defaults (docs/sync-watcher.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir). Brings its own clean profile, so it needs no game lists. Everything is a temp copy in a temporary JoyPro
# folder (JOYPRO_DATA_DIR).
# DCS writes a diff.lua against the defaults of the device type (CleanProfile\DCS\<plane>\<type>.cf, here a
# T-Pendular-Rudder-like device with its rudder on JOY_Z by default). A JoyPro bind equal to such a default is dropped
# from the diff when DCS saves: the bind is still active and must not be reported as removed. A real removal (a
# "removed" entry) and a bind the user added in the game are reported.

$sdRoot = Join-Path $tempDir "sync-dcsdefaults"
$sdPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $sdRoot "data"
$sdUtf8 = New-Object System.Text.UTF8Encoding($false)

function SdJson([string]$Text) {
    $line = @($Text -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith("{") }) | Select-Object -First 1
    Assert-True ($null -ne $line) ("no JSON in engine output: " + $Text)
    return ConvertFrom-Json $line
}

try {
    $sdTypeFolder = Join-Path $env:JOYPRO_DATA_DIR "GameData\CleanProfile\DCS\F-16C_50"
    New-Item -Path $sdTypeFolder -ItemType Directory -Force | Out-Null
    $sdDefaults = "local diff = {`n`t[""axisDiffs""] = {`n`t`t[""a2003cdnil""] = {`n`t`t`t[""name""] = ""Rudder"",`n`t`t`t[""removed""] = {`n`t`t`t`t[1] = {`n`t`t`t`t`t[""key""] = ""JOY_Z"",`n`t`t`t`t},`n`t`t`t},`n`t`t},`n`t},`n}`nreturn diff`n"
    [System.IO.File]::WriteAllText((Join-Path $sdTypeFolder "Sync Pedals.cf"), $sdDefaults, $sdUtf8)

    $sdProfile = Join-Path $sdRoot "dcs.pr0file"
    $sdSaved = Join-Path $sdRoot "dcs-saved-games"
    $sdPedals = "Sync Pedals {11111111-2222-3333-4444-5555555555BB}"
    Run-Engine @("--profile", $sdProfile, "relations", "add", "Pedal Rudder", "--axis", "--node", "DCS:a2003cdnil:F-16C_50", "--device", $sdPedals, "--input", "JOY_Z", "--text") | Out-Null
    Run-Engine @("--profile", $sdProfile, "relations", "add", "Pedal Button", "--button", "--node", "DCS:d3001pnilunilcd2vd1vpnilvunil:F-16C_50", "--device", $sdPedals, "--input", "JOY_BTN1", "--text") | Out-Null
    $sdExport = Run-Engine @("--profile", $sdProfile, "dcs", "export", $sdSaved, "--text")
    Assert-True ($sdExport.ExitCode -eq 0) ("dcs export for the defaults sync test failed: " + $sdExport.Output)
    $sdDiffFile = @(Get-ChildItem -Path (Join-Path $sdSaved "Config\Input\F-16C_50\joystick") -Filter "Sync Pedals*.diff.lua")[0].FullName
    $sdText = [System.IO.File]::ReadAllText($sdDiffFile).Replace("`r`n", "`n")
    Assert-Match $sdText '(?s)\["a2003cdnil"\] = \{.*?\["added"\]' "the export should write the rudder bind (equal to the device default) into the diff."
    Assert-True ((SdJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $sdSaved)).Output).Games[0].State -eq "unchanged") "sync status right after the export should be unchanged."

    # DCS saves the diff against the device defaults: the rudder equals the default and leaves the diff.
    $sdDropped = [regex]::Replace($sdText, '(?s)\n(\s*)\["a2003cdnil"\] = \{.*?\n\1\},', '')
    Assert-NotMatch $sdDropped 'a2003cdnil' "the fixture should drop the rudder entry as DCS does."
    [System.IO.File]::WriteAllText($sdDiffFile, $sdDropped.Replace("`n", "`r`n"), $sdUtf8)
    Assert-True ((SdJson (Run-Engine @("sync", "status", "--game", "DCS", "--path", $sdSaved)).Output).Games[0].State -eq "changed") "the rewritten diff.lua is changed."
    $sdQuiet = Run-Engine @("--profile", $sdProfile, "sync", "diff", "--game", "DCS", "--path", $sdSaved)
    Assert-True ($sdQuiet.ExitCode -eq 0) ("sync diff (DCS defaults) failed: " + $sdQuiet.Output)
    Assert-True (@((SdJson $sdQuiet.Output).Changes).Count -eq 0) ("a bind DCS dropped because it equals the device default must not count as removed: " + $sdQuiet.Output)

    # The user unbinds the default rudder in DCS (a "removed" entry) and binds a new action: both are reported.
    $sdUser = ([regex]'\["axisDiffs"\] = \{\n').Replace($sdDropped, "`$0    [""a2003cdnil""] = {`n      [""name""] = ""Rudder"",`n      [""removed""] = {`n        [1] = {`n          [""key""] = ""JOY_Z"",`n        },`n      },`n    },`n", 1)
    $sdUser = ([regex]'\["keyDiffs"\] = \{\n').Replace($sdUser, "`$0    [""d3005pnilunilcd2vd1vpnilvunil""] = {`n      [""name""] = ""User Added"",`n      [""added""] = {`n        [1] = {`n          [""key""] = ""JOY_BTN2"",`n        },`n      },`n    },`n", 1)
    Assert-Match $sdUser '(?s)a2003cdnil.*removed' "the fixture should hold the rudder removal."
    Assert-Match $sdUser 'd3005pnilunilcd2vd1vpnilvunil' "the fixture should hold the user's new bind."
    [System.IO.File]::WriteAllText($sdDiffFile, $sdUser.Replace("`n", "`r`n"), $sdUtf8)
    $sdReal = Run-Engine @("--profile", $sdProfile, "sync", "diff", "--game", "DCS", "--path", $sdSaved)
    $sdChanges = @((SdJson $sdReal.Output).Changes)
    Assert-True ($sdChanges.Count -eq 2) ("sync diff (DCS defaults) should report the removal and the new bind only: " + $sdReal.Output)
    $sdRemoved = @($sdChanges | Where-Object { $_.Kind -eq "removed" })[0]
    Assert-True ($null -ne $sdRemoved -and $sdRemoved.ActionId -eq "a2003cdnil" -and @($sdRemoved.Relations) -contains "Pedal Rudder") ("a default the user unbound in DCS should be reported as removed: " + $sdReal.Output)
    $sdAdded = @($sdChanges | Where-Object { $_.Kind -eq "added" })[0]
    Assert-True ($null -ne $sdAdded -and $sdAdded.ActionId -eq "d3005pnilunilcd2vd1vpnilvunil" -and $sdAdded.Input -eq "JOY_BTN2") ("a bind the user added in DCS should be reported: " + $sdReal.Output)
}
finally {
    $env:JOYPRO_DATA_DIR = $sdPreviousData
}
