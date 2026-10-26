# The one data folder (engine/JoyProDataFolder.cs, docs/installs.md "Data folder"). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir). A temp folder stands in for %LOCALAPPDATA% (JOYPRO_LOCALAPPDATA); the user's own
# folders are never touched.
#  1. A 2.3 layout (JoyPro + JoyPro2): the first engine start moves JoyPro into JoyPro2; only JoyPro2 is left.
#  2. A second start changes nothing and logs nothing about the data folder.
#  3. The junction JoyPro -> JoyPro2 that 2.4.0 left is removed as a link (JoyPro2's files stay); a junction to
#     another folder stays.
#  4. With JOYPRO_DATA_DIR (portable setup) nothing under %LOCALAPPDATA% is moved.
$dfLocal = Join-Path $tempDir "datafolder-localappdata"
$dfLegacy = Join-Path $dfLocal "JoyPro"
$dfTarget = Join-Path $dfLocal "JoyPro2"
New-Item -ItemType Directory -Force -Path (Join-Path $dfLegacy "GameData\DB\DCS"), (Join-Path $dfLegacy "ProfileHistory\p-1"), (Join-Path $dfTarget "Logs") | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "GameData\DB\DCS\Alpha-1.html"), "<html>alpha</html>")
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "ProfileHistory\p-1\history.json"), "{}")
[System.IO.File]::WriteAllText((Join-Path $dfLegacy "games-disabled.json"), "[`"WarThunder`"]")
[System.IO.File]::WriteAllText((Join-Path $dfTarget "Logs\gui-old.log"), "old log")

$dfPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Local = $env:JOYPRO_LOCALAPPDATA; NoMigration = $env:JOYPRO_NO_DATA_MIGRATION; Log = $env:JOYPRO_LOG_DIR }
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
    Assert-True (-not (Test-Path -LiteralPath $dfLegacy)) "datafolder: the old JoyPro folder (or a junction) is still there after the move."

    # A second start: nothing to do (only "version": no command that could load the user's last profile), no log line.
    $dfLogDir = Join-Path $tempDir "datafolder-logs"
    $env:JOYPRO_LOG_DIR = $dfLogDir
    $dfSecond = Run-Engine @("version")
    $env:JOYPRO_LOG_DIR = $dfPrevious.Log
    Assert-True ($dfSecond.ExitCode -eq 0) "datafolder: a second start failed: $($dfSecond.Output)"
    Assert-True ((Get-Content -Raw (Join-Path $dfTarget "data-migration.json")) -eq $dfState) "datafolder: a second start changed data-migration.json."
    Assert-True (Test-Path (Join-Path $dfTarget "GameData\DB\DCS\Alpha-1.html")) "datafolder: a second start lost the moved data."
    $dfLogText = (Get-ChildItem -Path $dfLogDir -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
    Assert-True ($dfLogText -notmatch "Data folder ") "datafolder: a start after the move still logs the data folder check."

    # The junction 2.4.0 left: removed as a link, JoyPro2's files stay.
    cmd /c mklink /J "$dfLegacy" "$dfTarget" | Out-Null
    Assert-True (Test-Path (Join-Path $dfLegacy "games-disabled.json")) "datafolder: could not make the 2.4.0 junction for the test."
    $dfThird = Run-Engine @("version")
    Assert-True ($dfThird.ExitCode -eq 0) "datafolder: a start with the 2.4.0 junction failed: $($dfThird.Output)"
    Assert-True (-not (Test-Path -LiteralPath $dfLegacy)) "datafolder: the 2.4.0 junction JoyPro -> JoyPro2 was not removed."
    Assert-True (Test-Path (Join-Path $dfTarget "GameData\DB\DCS\Alpha-1.html")) "datafolder: removing the junction lost data."

    # A junction to another folder is not JoyPro's: it stays, and so do the files behind it.
    $dfOther = Join-Path $tempDir "datafolder-other"
    New-Item -ItemType Directory -Force -Path $dfOther | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dfOther "mine.txt"), "mine")
    cmd /c mklink /J "$dfLegacy" "$dfOther" | Out-Null
    $dfFourth = Run-Engine @("version")
    Assert-True ($dfFourth.ExitCode -eq 0) "datafolder: a start with a foreign junction failed."
    Assert-True (Test-Path (Join-Path $dfLegacy "mine.txt")) "datafolder: a junction to another folder was removed."
    Assert-True (Test-Path (Join-Path $dfOther "mine.txt")) "datafolder: the files behind a foreign junction are gone."
    cmd /c rmdir "$dfLegacy" | Out-Null

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
    $env:JOYPRO_LOG_DIR = $dfPrevious.Log
    # A junction left by a failed check goes as a link (its files are inside the temp folder).
    if (Test-Path -LiteralPath $dfLegacy) {
        $dfItem = Get-Item -LiteralPath $dfLegacy -Force
        if ($dfItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { cmd /c rmdir "$dfLegacy" | Out-Null }
    }
}
Write-Host "datafolder smoke: OK"
