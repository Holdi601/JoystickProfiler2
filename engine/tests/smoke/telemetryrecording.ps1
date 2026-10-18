# Telemetry recording (docs/telemetry-recording.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir, $engineDir, $Configuration). Temp folders only; no game: JoyPro.Ffb's synthetic DCS flight (simulate) feeds
# JoyPro.Ffb record over loopback UDP.
#  1. engine telemetry config: off by default, set global and per game values.
#  2. engine telemetry games: the ten games.
#  3. A recorded session: sessions / show / preview / export csv; ACMI refused (the synthetic flight has no position).
#  4. A recording killed mid-session: listed as unfinished, telemetry recover finishes it.
#  5. delete.
$trDir = Join-Path $tempDir "telemetry-recording"
$trData = Join-Path $trDir "data"
$trFolder = Join-Path $trDir "recordings"
New-Item -Path $trData -ItemType Directory -Force | Out-Null
$trRepo = Split-Path -Parent $engineDir
$trProject = Join-Path $trRepo "gui\JoyPro.Ffb\JoyPro.Ffb.csproj"
$trExe = Join-Path $trRepo ("gui\JoyPro.Ffb\bin\" + $Configuration + "\net9.0-windows\JoyPro.Ffb.exe")
$trPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Ignore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES }
$trProcesses = New-Object System.Collections.Generic.List[System.Diagnostics.Process]

function Get-TrFreeUdpPort {
    $random = New-Object System.Random
    for ($i = 0; $i -lt 200; $i++) {
        $port = $random.Next(20000, 45000)
        try {
            $udp = New-Object System.Net.Sockets.UdpClient((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Loopback, $port)))
            $udp.Dispose()
            return $port
        }
        catch { }
    }
    throw "No free UDP port found."
}

function Start-TrRecord {
    param([int]$Port, [int]$Seconds)
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $trExe
    $info.Arguments = "record --game DCS --port $Port --no-game-check --force --duration $Seconds --folder `"$trFolder`""
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($info)
    $trProcesses.Add($process)
    return $process
}

try {
    $env:JOYPRO_DATA_DIR = $trData
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = "1"
    if (-not (Test-Path $trExe)) {
        Write-Host "Building JoyPro.Ffb ($Configuration)..."
        & dotnet build $trProject -c $Configuration -v q -nologo | Out-Host
        Assert-True ($LASTEXITCODE -eq 0) "dotnet build of JoyPro.Ffb failed."
    }
    Assert-True (Test-Path $trExe) ("JoyPro.Ffb.exe not found at " + $trExe)

    # 1. Settings: off by default.
    $trShow = Run-Engine @("telemetry", "config", "show")
    Assert-True ($trShow.ExitCode -eq 0) ("telemetry config show failed: " + $trShow.Output)
    $trConfig = ($trShow.Output | ConvertFrom-Json).Config
    Assert-True ($trConfig.enabled -eq $false) "telemetry recording must be off by default."
    Assert-True ($trConfig.sampleHz -eq 20) "the default sample rate is 20 per second."
    $trSet = Run-Engine @("telemetry", "config", "set", "--param", "enabled=true", "--param", "sample-hz=50", "--param", "keep-days=30", "--text")
    Assert-True ($trSet.ExitCode -eq 0) ("telemetry config set failed: " + $trSet.Output)
    $trSet = Run-Engine @("telemetry", "config", "set", "--game", "il2", "--param", "enabled=false", "--text")
    Assert-True ($trSet.ExitCode -eq 0 -and $trSet.Output -match "IL2Game: not recorded") ("telemetry config set --game failed: " + $trSet.Output)
    $trBad = Run-Engine @("telemetry", "config", "set", "--param", "sample-hz=fast")
    Assert-True ($trBad.ExitCode -ne 0) "telemetry config set must refuse a bad number."
    $trFile = Get-Content (Join-Path $trData "Telemetry\telemetry-recording.json") -Raw | ConvertFrom-Json
    Assert-True ($trFile.enabled -eq $true -and $trFile.sampleHz -eq 50 -and $trFile.keepDays -eq 30 -and $trFile.games.IL2Game.enabled -eq $false) "telemetry-recording.json does not hold the settings."

    # 2. The games.
    $trGames = (Run-Engine @("telemetry", "games")).Output | ConvertFrom-Json
    Assert-True (@($trGames.Games).Count -eq 10) ("telemetry games: expected 10 games, got " + @($trGames.Games).Count)
    Assert-True ((@($trGames.Games | Where-Object { $_.Id -eq "IL2Game" })[0].Recorded) -eq $false) "telemetry games: IL-2 should not be recorded."

    # 3. A recorded session.
    $trPort = Get-TrFreeUdpPort
    $trRecord = Start-TrRecord -Port $trPort -Seconds 14
    Start-Sleep -Milliseconds 1500
    & $trExe simulate --game DCS --port $trPort --seconds 6 | Out-Null
    Assert-True ($trRecord.WaitForExit(30000)) "JoyPro.Ffb record did not end after its duration."
    Assert-True ($trRecord.ExitCode -eq 0) ("JoyPro.Ffb record exit code " + $trRecord.ExitCode + ": " + $trRecord.StandardOutput.ReadToEnd())
    $trSessions = (Run-Engine @("telemetry", "sessions", "--folder", $trFolder)).Output | ConvertFrom-Json
    Assert-True (@($trSessions.Sessions).Count -eq 1) ("expected one recorded session, got " + @($trSessions.Sessions).Count)
    $trSession = @($trSessions.Sessions)[0]
    Assert-True ($trSession.Complete -eq $true -and $trSession.Game -eq "DCS") "the recorded session should be complete and DCS."
    Assert-True ($trSession.Frames -ge 100) ("the session has only " + $trSession.Frames + " frames (50 per second for about 6 s).")
    Assert-True ($trSession.RawPackets -ge 100) ("the session has only " + $trSession.RawPackets + " raw packets.")
    $trShowSession = Run-Engine @("telemetry", "show", "latest", "--folder", $trFolder, "--text")
    Assert-True ($trShowSession.ExitCode -eq 0 -and $trShowSession.Output -match "dcs\.ias") ("telemetry show failed: " + $trShowSession.Output)
    $trCsvDir = Join-Path $trDir "csv"
    $trCsv = Run-Engine @("telemetry", "export", "1", "--format", "csv", "--to", $trCsvDir, "--folder", $trFolder)
    Assert-True ($trCsv.ExitCode -eq 0) ("telemetry export csv failed: " + $trCsv.Output)
    $trStem = [System.IO.Path]::GetFileNameWithoutExtension($trSession.FileName)
    $trTable = Get-Content (Join-Path $trCsvDir ($trStem + ".csv"))
    Assert-True ($trTable.Count -ge 100 -and $trTable[0] -match "^time_s,") "the CSV table is too short or has no header."
    Assert-True (Test-Path (Join-Path $trCsvDir ($trStem + "-channels\dcs.g.csv"))) "the CSV export has no file per channel."
    $trAcmi = Run-Engine @("telemetry", "export", "1", "--format", "acmi-zip", "--folder", $trFolder)
    Assert-True ($trAcmi.ExitCode -ne 0 -and $trAcmi.Output -match "position") ("ACMI export of a session without position must be refused: " + $trAcmi.Output)
    $trPreview = (Run-Engine @("telemetry", "preview", "latest", "--folder", $trFolder, "--points", "40")).Output | ConvertFrom-Json
    Assert-True (@($trPreview.Series).Count -ge 2 -and @($trPreview.Series)[0].Values.Count -ge 2) "telemetry preview returned no series."

    # 4. Killed mid-session, then recovered.
    $trPort = Get-TrFreeUdpPort
    $trRecord = Start-TrRecord -Port $trPort -Seconds 60
    Start-Sleep -Milliseconds 1500
    & $trExe simulate --game DCS --port $trPort --seconds 4 | Out-Null
    Start-Sleep -Milliseconds 2500
    $trRecord.Kill()
    $trRecord.WaitForExit(5000) | Out-Null
    $trSessions = (Run-Engine @("telemetry", "sessions", "--folder", $trFolder)).Output | ConvertFrom-Json
    Assert-True (@($trSessions.Sessions).Count -eq 2) ("expected two sessions after the kill, got " + @($trSessions.Sessions).Count)
    $trCut = @($trSessions.Sessions | Where-Object { -not $_.Complete })
    Assert-True ($trCut.Count -eq 1) "the killed recording should be listed as unfinished."
    $trRecover = (Run-Engine @("telemetry", "recover", "--folder", $trFolder)).Output | ConvertFrom-Json
    Assert-True (@($trRecover.Recovered).Count -eq 1) "telemetry recover did not finish the killed recording."
    $trSessions = (Run-Engine @("telemetry", "sessions", "--folder", $trFolder)).Output | ConvertFrom-Json
    Assert-True (@($trSessions.Sessions | Where-Object { -not $_.Complete }).Count -eq 0) "after recover every session is complete."
    Assert-True ((@($trSessions.Sessions | Where-Object { $_.EndReason -match "recovered" })[0].Frames) -ge 20) "the recovered session lost its frames."

    # 5. Delete.
    $trDelete = Run-Engine @("telemetry", "delete", "1", "--folder", $trFolder, "--text")
    Assert-True ($trDelete.ExitCode -eq 0) ("telemetry delete failed: " + $trDelete.Output)
    $trSessions = (Run-Engine @("telemetry", "sessions", "--folder", $trFolder)).Output | ConvertFrom-Json
    Assert-True (@($trSessions.Sessions).Count -eq 1) "telemetry delete did not delete the session."
}
finally {
    foreach ($process in $trProcesses) {
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
    }
    $env:JOYPRO_DATA_DIR = $trPrevious.Data
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $trPrevious.Ignore
}
