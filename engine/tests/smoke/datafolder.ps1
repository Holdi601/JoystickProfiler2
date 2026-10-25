# The one data folder (engine/JoyProDataFolder.cs, docs/installs.md "Data folder"). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir). A temp folder stands in for %LOCALAPPDATA% (JOYPRO_LOCALAPPDATA); the user's own
# folders are never touched.
#  1. A 2.3 layout (JoyPro + JoyPro2): the first engine start moves JoyPro into JoyPro2 and leaves a junction.
#  2. A second start changes nothing.
#  3. With JOYPRO_DATA_DIR (portable setup) nothing under %LOCALAPPDATA% is moved.
$dfLocal = Join-Path $tempDir "datafolder-localappdata"
$dfLegacy = Join-Path $dfLocal "JoyPro"
$dfTarget = Join-Path $dfLocal "JoyPro2"
New-Item -ItemType Directory -Force -Path (Join-Path $dfLegacy "GameData\DB\DCS"), (Join-Path $dfLegacy "ProfileHistory\p-1"), (Join-Path $dfTarget "Logs") | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "GameData\DB\DCS\Alpha-1.html"), "<html>alpha</html>")
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "ProfileHistory\p-1\history.json"), "{}")
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "games-disabled.json"), "[`"WarThunder`"]")
[System.IO.File]::WriteAllText((Join-Path $dfTarget "Logs\gui-old.log"), "old log")

$dfPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Local = $env:JOYPRO_LOCALAPPDATA; NoMigration = $env:JOYPRO_NO_DATA_MIGRATION }
try {
    $env:JOYPRO_DATA_DIR = $null
    $env:JOYPRO_NO_DATA_MIGRATION = $null
    $env:JOYPRO_LOCALAPPDATA = $dfLocal

    $dfFirst = Run-Engine @("version")
    Assert-True ($dfFirst.ExitCode -eq 0) "datafolder: engine version failed after the move: $($dfFirst.Output)"
    Assert-True (Test-Path (Join-Path $dfTarget "GameData\DB\DCS\Alpha-1.html")) "datafolder: the game data was not moved into JoyPro2."
    Assert-True (Test-Path (Join-Path $dfTarget "ProfileHistory\p-1\history.json")) "datafolder: the profile history was not moved into JoyPro2."
    Assert-True (Test-Path (Join-Path $dfTarget "games-disabled.json")) "datafolder: games-disabled.json was not moved into JoyPro2."
    Assert-True (Test-Path (Join-Path $dfTarget "Logs\gui-old.log")) "datafolder: JoyPro2's own files are gone."
    $dfState = Get-Content -Raw (Join-Path $dfTarget "data-migration.json")
    Assert-Match $dfState '"status": "done"' "datafolder: data-migration.json does not say done."
    $dfLink = Get-Item -LiteralPath $dfLegacy -Force -ErrorAction SilentlyContinue
    Assert-True ($null -ne $dfLink -and ($dfLink.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) "datafolder: no junction JoyPro -> JoyPro2 for older installs."
    Assert-True (Test-Path (Join-Path $dfLegacy "GameData\DB\DCS\Alpha-1.html")) "datafolder: an older install does not find its data through the junction."

    # A second start: nothing to do (only "version": no command that could load the user's last profile).
    $dfSecond = Run-Engine @("version")
    Assert-True ($dfSecond.ExitCode -eq 0) "datafolder: a second start failed: $($dfSecond.Output)"
    Assert-True ((Get-Content -Raw (Join-Path $dfTarget "data-migration.json")) -eq $dfState) "datafolder: a second start changed data-migration.json."
    Assert-True (Test-Path (Join-Path $dfTarget "GameData\DB\DCS\Alpha-1.html")) "datafolder: a second start lost the moved data."

    # Portable: JOYPRO_DATA_DIR set, a new old folder stays where it is.
    $dfLocal2 = Join-Path $tempDir "datafolder-localappdata-portable"
    New-Item -ItemType Directory -Force -Path (Join-Path $dfLocal2 "JoyPro\GameData") | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dfLocal2 "JoyPro\GameData\keep.txt"), "keep")
    $env:JOYPRO_LOCALAPPDATA = $dfLocal2
    $env:JOYPRO_DATA_DIR = Join-Path $tempDir "datafolder-portable-data"
    $dfPortable = Run-Engine @("version")
    Assert-True ($dfPortable.ExitCode -eq 0) "datafolder: engine version failed in a portable setup."
    Assert-True (Test-Path (Join-Path $dfLocal2 "JoyPro\GameData\keep.txt")) "datafolder: a portable setup moved %LOCALAPPDATA%\JoyPro."
    Assert-True (-not (Test-Path (Join-Path $dfLocal2 "JoyPro2"))) "datafolder: a portable setup created %LOCALAPPDATA%\JoyPro2."
}
finally {
    $env:JOYPRO_DATA_DIR = $dfPrevious.Data
    $env:JOYPRO_LOCALAPPDATA = $dfPrevious.Local
    $env:JOYPRO_NO_DATA_MIGRATION = $dfPrevious.NoMigration
    # The junction goes as a link (its files are in JoyPro2, inside the temp folder).
    if (Test-Path -LiteralPath $dfLegacy) { cmd /c rmdir "$dfLegacy" | Out-Null }
}
Write-Host "datafolder smoke: OK"
