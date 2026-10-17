# The game support page (engine games support, docs/games/support.md): every game JoyPro handles has a row, the notes
# cannot leave a game out or claim more than the code, and the page in the repository is up to date
# (tools/game_support.ps1 -CheckOnly). Needs no game data: the marks come from the code, the shipped DB\Functions and
# DB\Cockpits and docs/games/support-notes.json.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir).

$gsRoot = Split-Path -Parent $engineDir
$gsNotes = Join-Path $gsRoot "docs\games\support-notes.json"
$gsJson = Join-Path $tempDir "game-support.json"
$gsRun = Run-Engine @("games", "support", "--notes", $gsNotes, "--out", $gsJson)
Assert-True ($gsRun.ExitCode -eq 0) ("engine games support failed (the notes do not match the code): " + $gsRun.Output)
$gsReport = [System.IO.File]::ReadAllText($gsJson, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$gsIds = @($gsReport.Rows | ForEach-Object { $_.GameId })

$gsIntegrations = (Run-Engine @("games", "integrations")).Output | ConvertFrom-Json
$gsExpected = @("DCS", "IL2Game", "StarCitizen") + @($gsIntegrations.Integrations | ForEach-Object { $_.GameId })
foreach ($gsId in $gsExpected) {
    Assert-True ($gsIds -contains $gsId) ("The game support page has no row for " + $gsId + ".")
}
Assert-True (@($gsReport.Rows | Where-Object { $_.Level -eq "documented" }).Count -ge 15) "The game support page should list the documented-only titles."
foreach ($gsRow in $gsReport.Rows) {
    Assert-True (@($gsRow.Cells.PSObject.Properties).Count -eq 17) ("Every row has the 17 feature cells: " + $gsRow.GameId)
}

# Facts the matrix takes from the code.
$gsDcs = $gsReport.Rows | Where-Object { $_.GameId -eq "DCS" }
Assert-True ($gsDcs.Cells.telemetry.Mark -eq [string][char]0x2713) "DCS has telemetry force feedback (EngineFfbTelemetry)."
Assert-True ($gsDcs.AxisModel -eq "dcs") "DCS writes curves in its own model."
$gsAc8 = $gsReport.Rows | Where-Object { $_.GameId -eq "AceCombat8" }
Assert-True ($gsAc8.Level -eq "xbox") "ACE COMBAT 8 is a virtual Xbox pad game (IGameDeviceLimit)."
$gsBf6 = $gsReport.Rows | Where-Object { $_.GameId -eq "Battlefield6" }
Assert-True ($gsBf6.Level -eq "virtual" -and $gsBf6.MaxJoysticks -eq 1) "Battlefield 6 reads one joystick (IGameDeviceLimit)."

# Notes that leave a game out or claim more than the code fail.
$gsNotesText = [System.IO.File]::ReadAllText($gsNotes, [System.Text.Encoding]::UTF8)
$gsMissing = Join-Path $tempDir "support-notes-missing.json"
[System.IO.File]::WriteAllText($gsMissing, $gsNotesText.Replace('"id": "Wardogs"', '"id": "doc:Wardogs", "level": "documented"'), (New-Object System.Text.UTF8Encoding($false)))
$gsBad = Run-Engine @("games", "support", "--notes", $gsMissing, "--text")
Assert-True ($gsBad.ExitCode -ne 0) "Notes without a registered game must fail."
Assert-Match $gsBad.Output "no entry for Wardogs" "The failure should name the game without notes."
$gsClaim = Join-Path $tempDir "support-notes-claim.json"
$gsCheck = [string][char]0x2713
[System.IO.File]::WriteAllText($gsClaim, $gsNotesText.Replace('"tuning": { "note": "G-Rebels: invert only." }', '"tuning": { "mark": "' + $gsCheck + '", "note": "G-Rebels: invert only." }'), (New-Object System.Text.UTF8Encoding($false)))
$gsBad = Run-Engine @("games", "support", "--notes", $gsClaim, "--text")
Assert-True ($gsBad.ExitCode -ne 0) "A note that claims more than the code must fail."
Assert-Match $gsBad.Output "GRebels/tuning" "The failure should name the cell."

# The HTML page (the GUI's "Supported games") has every title.
$gsHtml = Join-Path $tempDir "game-support.html"
$gsRun = Run-Engine @("games", "support", "--html", "--out", $gsHtml)
Assert-True ($gsRun.ExitCode -eq 0) ("engine games support --html failed: " + $gsRun.Output)
$gsHtmlText = [System.IO.File]::ReadAllText($gsHtml, [System.Text.Encoding]::UTF8)
Assert-Match $gsHtmlText "<title>JoyPro2 - Supported games</title>" "The HTML page needs its title."
Assert-Match $gsHtmlText "Hell Let Loose: Vietnam" "The HTML page should list every title."

# docs/games/support.md is up to date.
& powershell -ExecutionPolicy Bypass -File (Join-Path $gsRoot "tools\game_support.ps1") -EngineExePath $script:EngineExe -CheckOnly | Out-Host
Assert-True ($LASTEXITCODE -eq 0) "docs/games/support.md is out of date. Run tools/game_support.ps1."
