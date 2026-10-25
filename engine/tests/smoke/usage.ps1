# Usage statistics (docs/usage-statistics.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir,
# $engineDir). Temp folders only: the synthetic fixture folder tests\fixtures\usage\root stands for the PC
# (JOYPRO_USAGE_ROOT: <root>\Games\<id> = installations, Saved Games, Documents, Steam), the store goes into a temp
# JOYPRO_DATA_DIR.
#  1. harvest: every source kind, then nothing twice; a moved log (Game.log into logbackups) is known.
#  2. sources: the live-tail targets.
#  3. ranking / stats: hours without double counting, references, facts; pin / exclude.
#  4. backups / restore.
#  5. Auto Assign: a new aircraft's unbound function lands where the most used aircraft has it (--usage-style off: not).
$usDir = Join-Path $tempDir "usage"
$usRoot = Join-Path $usDir "root"
$usData = Join-Path $usDir "data"
New-Item -ItemType Directory -Force -Path $usDir, $usData | Out-Null
Copy-Item -Recurse -Force (Join-Path $engineDir "tests\fixtures\usage\root") $usRoot
# git keeps no write times: the track and the replay get theirs back.
(Get-Item -LiteralPath (Join-Path $usRoot "Saved Games\DCS\Tracks\harrier-practice.trk")).LastWriteTimeUtc = [DateTime]::SpecifyKind([DateTime]"2026-01-12T21:00:00", "Utc")
(Get-Item -LiteralPath (Join-Path $usRoot "Games\WarThunder\Replays\#2026.01.16 18.00.00.wrpl")).LastWriteTimeUtc = [DateTime]::SpecifyKind([DateTime]"2026-01-16T18:25:00", "Utc")
$usPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Root = $env:JOYPRO_USAGE_ROOT; Fake = $env:JOYPRO_FAKE_DEVICES; Kb = $env:JOYPRO_DEVICE_KB }
try {
    $env:JOYPRO_DATA_DIR = $usData
    $env:JOYPRO_USAGE_ROOT = $usRoot

    # 1. harvest
    $usHarvest = Run-Engine @("usage", "harvest")
    Assert-True ($usHarvest.ExitCode -eq 0) ("usage harvest failed: " + $usHarvest.Output)
    $usReport = $usHarvest.Output | ConvertFrom-Json
    Assert-True ($usReport.SessionsNew -ge 12) ("usage harvest found too few sessions: " + $usReport.SessionsNew)
    foreach ($kind in @("dcs-log", "dcs-logbook", "dcs-track", "tacview", "bms-logbook", "il2-mission-report", "xplane-logbook", "xplane-log", "warthunder-replay", "elite-journal", "sc-log", "x4-save", "steam")) {
        $hit = @($usReport.Sources | Where-Object { $_.Kind -eq $kind -and ($_.Sessions -gt 0 -or $_.Totals -gt 0) })
        Assert-True ($hit.Count -gt 0) ("usage harvest got nothing from source " + $kind)
    }
    Assert-True (Test-Path (Join-Path $usData "Usage\sessions.jsonl")) "usage harvest wrote no sessions.jsonl"
    $usAgain = (Run-Engine @("usage", "harvest")).Output | ConvertFrom-Json
    Assert-True ($usAgain.FilesRead -eq 0 -and $usAgain.SessionsNew -eq 0) "a second usage harvest read known files again."
    Move-Item (Join-Path $usRoot "Games\StarCitizen\LIVE\Game.log") (Join-Path $usRoot "Games\StarCitizen\LIVE\logbackups\Game Build(100) 13 Jan 26 (20 00 00).log")
    $usMoved = (Run-Engine @("usage", "harvest")).Output | ConvertFrom-Json
    Assert-True ($usMoved.FilesRead -eq 0 -and $usMoved.SessionsNew -eq 0) "a log moved into logbackups counted again."
    $usText = Run-Engine @("usage", "harvest", "--text", "--dry-run")
    Assert-Match $usText.Output "Star Citizen game logs \(Game\.log\): 2 file\(s\)" "usage harvest --text does not list the Star Citizen logs."

    # 2. sources
    $usSources = (Run-Engine @("usage", "sources")).Output | ConvertFrom-Json
    Assert-True (@($usSources.Tail | Where-Object { $_.Game -eq "DCS" -and $_.Filter -eq "dcs.log" }).Count -eq 1) "usage sources: no live tail of dcs.log."
    Assert-True (@($usSources.Tail | Where-Object { $_.Game -eq "EliteDangerous" -and $_.Filter -eq "Journal.*.log" }).Count -eq 1) "usage sources: no live tail of the Elite journals."
    Assert-True ($usSources.LiveFolder -like "*\Usage\Live") "usage sources: no live folder."

    # 3. ranking and statistics
    $usRanking = (Run-Engine @("usage", "ranking")).Output | ConvertFrom-Json
    $usF16 = @($usRanking.Units | Where-Object { $_.Unit -eq "F-16C_50" })[0]
    Assert-True ([Math]::Abs($usF16.Hours - (70 / 60)) -lt 0.02) ("the F-16 counts Tacview and dcs.log twice: " + $usF16.Hours + " h")
    Assert-True ($usRanking.References."DCS|fixed_wing" -eq "DCS|F-16C_50") "the F-16 is not the reference of the DCS jets."
    $usStatsClock = [Diagnostics.Stopwatch]::StartNew()
    $usStats = Run-Engine @("usage", "stats")
    $usStatsClock.Stop()
    Assert-True ($usStats.ExitCode -eq 0) ("usage stats failed: " + $usStats.Output)
    $usStatsJson = $usStats.Output | ConvertFrom-Json
    Assert-True ($usStatsJson.HasData) "usage stats has no data."
    Assert-True ($usStatsJson.Totals.LongestStreakDays -ge 6) ("usage stats: streak " + $usStatsJson.Totals.LongestStreakDays)
    Assert-True (@($usStatsJson.Facts).Count -ge 3) "usage stats: too few fun facts."
    Assert-True (@($usStatsJson.Counters | Where-Object { $_.Name -eq "jumps" }).Count -eq 1) "usage stats: no jumps."
    Write-Host ("usage stats (process with start-up): " + $usStatsClock.ElapsedMilliseconds + " ms")
    $usStatsText = Run-Engine @("usage", "stats", "--text")
    Assert-Match $usStatsText.Output "Fun facts" "usage stats --text has no fun facts."
    $usPin = Run-Engine @("usage", "pin", "dcs", "FA-18C_hornet")
    Assert-True ($usPin.ExitCode -eq 0) "usage pin failed."
    $usExclude = Run-Engine @("usage", "exclude", "StarCitizen", "RSI_Polaris")
    Assert-True ($usExclude.ExitCode -eq 0) "usage exclude failed."
    $usRanked = (Run-Engine @("usage", "ranking")).Output | ConvertFrom-Json
    Assert-True ($usRanked.Units[0].Unit -eq "FA-18C_hornet" -and $usRanked.Units[0].Pinned) "a pinned unit does not rank first."
    Assert-True ($usRanked.References."DCS|fixed_wing" -eq "DCS|FA-18C_hornet") "a pinned unit is not the reference."
    Assert-True (($usRanked.Units | Select-Object -Last 1).Unit -eq "RSI_Polaris") "an excluded unit does not rank last."
    Run-Engine @("usage", "unpin", "DCS", "FA-18C_hornet") | Out-Null
    Run-Engine @("usage", "include", "StarCitizen", "RSI_Polaris") | Out-Null
    $usBad = Run-Engine @("usage", "pin")
    Assert-True ($usBad.ExitCode -ne 0) "usage pin without a game was accepted."

    # 4. backups
    $usBackups = (Run-Engine @("usage", "backups")).Output | ConvertFrom-Json
    Assert-True (@($usBackups.Days).Count -ge 1) "the usage store made no backup."
    $usRestore = Run-Engine @("usage", "restore", @($usBackups.Days)[0])
    Assert-True ($usRestore.ExitCode -eq 0) ("usage restore failed: " + $usRestore.Output)

    # 5. Auto Assign: the F-16's TMS up is on a panel button; the F-15E (not flown) gets it there too, unless
    # --usage-style off.
    $usAaDir = Join-Path $engineDir "tests\fixtures\autoassign"
    $env:JOYPRO_FAKE_DEVICES = Join-Path $usAaDir "devices.tsv"
    $env:JOYPRO_DEVICE_KB = Join-Path $usAaDir "kb"
    $usProfile = Join-Path $usDir "profile.json"
    Copy-Item (Join-Path $usAaDir "profile.json") $usProfile -Force
    $usAssign = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--profile", $usProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--text")
    Assert-True ($usAssign.ExitCode -eq 0) ("auto-assign for the usage style failed: " + $usAssign.Output)
    Run-Engine @("--profile", $usProfile, "binds", "set", "Target Management System Up", "--device", "AA Test Panel {aaaaaaaa-0000-0000-0000-000000000004}", "--input", "JOY_BTN7") | Out-Null
    Run-Engine @("--profile", $usProfile, "relations", "add", "Target Management System Up [F-15E]", "--button", "--node", "DCS:aa_tms_up_15e:F-15E") | Out-Null
    $usStyled = Run-Engine @("--confirm-corrections", "false", "--profile", $usProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--dry-run", "--text")
    Assert-Match $usStyled.Output "(?m)^  - Target Management System Up \[F-15E\] => AA Test Panel \{[^}]+\} JOY_BTN7" "the new aircraft did not follow the most used one's control."
    Assert-Match $usStyled.Output "Usage ranking: 1 function" "auto-assign did not say it followed the usage ranking."
    $usPlain = Run-Engine @("--usage-style", "off", "--confirm-corrections", "false", "--profile", $usProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--dry-run", "--text")
    Assert-Match $usPlain.Output "(?m)^  - Target Management System Up \[F-15E\] => AA Test Stick \{[^}]+\} JOY_BTN4 \(stick.hat.tms.up\)" "--usage-style off still followed the usage ranking."
    Write-Host "Usage statistics: OK"
}
finally {
    $env:JOYPRO_DATA_DIR = $usPrevious.Data
    $env:JOYPRO_USAGE_ROOT = $usPrevious.Root
    $env:JOYPRO_FAKE_DEVICES = $usPrevious.Fake
    $env:JOYPRO_DEVICE_KB = $usPrevious.Kb
}
