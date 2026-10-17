# Telemetry force feedback (docs/ffb-telemetry.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir, $engineDir, $Configuration). Works on temp copies only and never moves a stick: JoyPro.Ffb always runs with
# the dry sink (--sink dry and JOYPRO_FFB_DRY=1), fed by its own synthetic flight (JoyPro.Ffb simulate) over loopback
# UDP / a local HTTP stub.
#  1. engine ffb telemetry config: set/clear per scope, validation, the file JoyPro.Ffb reads.
#  2. engine ffb telemetry setup dcs: a Hooks script; Export.lua untouched; idempotent; remove; backups.
#  3. engine ffb telemetry setup il2: startup.cfg with and without another tool's telemetry section; remove restores it.
#  4. JoyPro.Ffb run per game (DCS, IL-2, War Thunder): buffet rises with AoA, gear rumble only while the gear moves,
#     gun bursts while firing, runway rumble on the ground, switched-off effects stay off.
#  5. Telemetry timeout -> one STOP ALL, nothing until telemetry comes back (with "JoyPro provides the centring spring":
#     only springs are held, no STOP ALL); game exit -> STOP ALL and the process ends.
#  6. Force cap holds; resources over 60 s (CPU, working set, handles) stay small and flat; idle costs no CPU.
$ffbtDir = Join-Path $tempDir "ffb-telemetry"
$ffbtData = Join-Path $ffbtDir "data"
New-Item -Path $ffbtData -ItemType Directory -Force | Out-Null
$ffbtRepo = Split-Path -Parent $engineDir
$ffbtProject = Join-Path $ffbtRepo "gui\JoyPro.Ffb\JoyPro.Ffb.csproj"
$ffbtExe = Join-Path $ffbtRepo ("gui\JoyPro.Ffb\bin\" + $Configuration + "\net9.0-windows\JoyPro.Ffb.exe")
$ffbtPrevious = @{
    Data = $env:JOYPRO_DATA_DIR
    Dry = $env:JOYPRO_FFB_DRY
    Ignore = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
    Detection = $env:JOYPRO_INSTALL_DETECTION
}
$ffbtProcesses = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$ffbtUtf8 = New-Object System.Text.UTF8Encoding $false

# A loopback port free for TCP (War Thunder stub) and UDP (DCS, IL-2). Random ports below the ephemeral range: the OS
# hands out ephemeral ports in sequence, and a whole run of them can sit in a Hyper-V UDP exclusion range.
function Get-FfbtFreePort {
    $random = New-Object System.Random
    for ($i = 0; $i -lt 200; $i++) {
        $port = $random.Next(20000, 45000)
        $tcp = $null
        $udp = $null
        try {
            $tcp = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
            $tcp.Start()
            $udp = New-Object System.Net.Sockets.UdpClient((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Loopback, $port)))
            return $port
        }
        catch {
            continue
        }
        finally {
            if ($null -ne $udp) { $udp.Close() }
            if ($null -ne $tcp) { $tcp.Stop() }
        }
    }
    throw "No free loopback port found."
}

function Start-Ffbt {
    param([string[]]$Arguments, [string]$Name)
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $process = Start-Process -FilePath $ffbtExe -ArgumentList $quoted -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $ffbtDir ($Name + ".out.txt")) -RedirectStandardError (Join-Path $ffbtDir ($Name + ".err.txt"))
    $null = $process.Handle   # keeps the handle open, so ExitCode is still readable after the exit
    $ffbtProcesses.Add($process) | Out-Null
    return $process
}

function Wait-Ffbt {
    param([System.Diagnostics.Process]$Process, [int]$Seconds, [string]$What)
    Assert-True ($Process.WaitForExit($Seconds * 1000)) ($What + " did not end within " + $Seconds + " s.")
}

function Write-FfbtConfig {
    # Centring off by default here: the effect checks look at one effect at a time (JoyPro's own centring spring is
    # checked in 5a).
    param([string]$Path, [double]$MaxForce, [string[]]$OffEffects, [double]$Timeout = 0.5, [bool]$Centring = $false)
    $effects = @()
    foreach ($game in @("DCS", "IL2Game", "WarThunder")) {
        $list = @($OffEffects | ForEach-Object { '"' + $_ + '": { "enabled": false, "strength": 1 }' }) -join ", "
        $effects += ('"' + $game + '": { "enabled": true, "effects": { ' + $list + ' } }')
    }
    $json = '{ "format": "joypro-ffb-telemetry-config", "version": 1, "enabled": true, "strength": 1, "maxForce": ' +
        $MaxForce.ToString([System.Globalization.CultureInfo]::InvariantCulture) + ', "tickRateHz": 50, "timeoutSeconds": ' +
        $Timeout.ToString([System.Globalization.CultureInfo]::InvariantCulture) + ', "centringSpring": ' + $(if ($Centring) { "true" } else { "false" }) +
        ', "games": { ' + ($effects -join ", ") + ' } }'
    [System.IO.File]::WriteAllText($Path, $json, $ffbtUtf8)
}

# The dry sink's record: one JSON object per line ({"t","effects":[...]} or {"t","stopAll":true}).
function Read-FfbtRecord {
    param([string]$Path)
    Assert-True (Test-Path $Path) ("JoyPro.Ffb wrote no record at " + $Path)
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line.Trim().Length -gt 0) {
            $list.Add(($line | ConvertFrom-Json)) | Out-Null
        }
    }
    return ,$list
}

# Time spans in which an effect with this tag was sent (gaps < JoinGap join), with its magnitudes.
function Get-FfbtSegments {
    param($Record, [string]$Tag, [double]$JoinGap = 0.15)
    $segments = New-Object System.Collections.Generic.List[object]
    $current = $null
    foreach ($entry in $Record) {
        if ($null -eq $entry.effects) {
            if ($null -ne $current) { $segments.Add($current) | Out-Null; $current = $null }
            continue
        }
        $hits = @($entry.effects | Where-Object { $_.tag -eq $Tag })
        if ($hits.Count -eq 0) {
            if ($null -ne $current) { $segments.Add($current) | Out-Null; $current = $null }
            continue
        }
        $magnitude = ($hits | Measure-Object -Property peak -Maximum).Maximum
        if ($null -eq $current -or ($entry.t - $current.End) -gt $JoinGap) {
            if ($null -ne $current) { $segments.Add($current) | Out-Null }
            $current = [PSCustomObject]@{ Start = $entry.t; End = $entry.t; Times = (New-Object System.Collections.Generic.List[double]); Values = (New-Object System.Collections.Generic.List[double]) }
        }
        $current.End = $entry.t
        $current.Times.Add($entry.t) | Out-Null
        $current.Values.Add($magnitude) | Out-Null
    }
    if ($null -ne $current) { $segments.Add($current) | Out-Null }
    return ,$segments
}

function Get-FfbtMean {
    param($Segment, [double]$From, [double]$To)
    $sum = 0.0
    $count = 0
    for ($i = 0; $i -lt $Segment.Times.Count; $i++) {
        if ($Segment.Times[$i] -ge $From -and $Segment.Times[$i] -le $To) {
            $sum += $Segment.Values[$i]
            $count++
        }
    }
    if ($count -eq 0) { return 0.0 }
    return $sum / $count
}

function Assert-FfbtCap {
    param($Record, [double]$Cap, [string]$What)
    foreach ($entry in $Record) {
        if ($null -eq $entry.effects) { continue }
        foreach ($axis in @("X", "Y")) {
            $sum = (@($entry.effects | Where-Object { $_.axis -eq $axis }) | Measure-Object -Property peak -Sum).Sum
            if ($null -eq $sum) { $sum = 0 }
            Assert-True ($sum -le $Cap + 1e-4) ($What + ": axis " + $axis + " got " + $sum + " at t=" + $entry.t + ", more than the cap " + $Cap + ".")
        }
    }
}

# How late this PC ran one flight, in seconds, measured in the run itself (see Assert-FfbtFlight for why):
#  - JoyPro.Ffb: the longest step between two dry-sink updates while effects play, minus the 50 Hz tick;
#  - the "game": the simulator's longest gap between two sends (UDP) or two /state answers (War Thunder), minus its
#    nominal period (DCS 60 Hz, IL-2 50 Hz, War Thunder's 20 Hz /state poll).
# Also asserts that the run really ticks at 50 Hz while effects play (median step), so a slow or stalling tick loop
# fails here instead of only widening the tolerance.
function Get-FfbtLateness {
    # -SkipStopAll: steps to or from a STOP ALL are left out (the telemetry timeout test checks exactly those: the step
    # to the STOP ALL and the pause after it).
    param([string]$Game, $Record, [string]$SimOutput, [double]$SimPeriod, [switch]$SkipStopAll)
    $first = -1
    $last = -1
    for ($i = 0; $i -lt $Record.Count; $i++) {
        if ($null -ne $Record[$i].effects -and @($Record[$i].effects).Count -gt 0) {
            if ($first -lt 0) { $first = $i }
            $last = $i
        }
    }
    $runLate = 0.0
    if ($first -ge 0 -and $last -gt $first) {
        $steps = New-Object System.Collections.Generic.List[double]
        for ($i = $first + 1; $i -le $last; $i++) {
            if ($SkipStopAll -and ($Record[$i].stopAll -or $Record[$i - 1].stopAll)) { continue }
            $steps.Add([double]$Record[$i].t - [double]$Record[$i - 1].t) | Out-Null
        }
        $sorted = @($steps | Sort-Object)
        $median = $sorted[[int][math]::Floor($sorted.Count / 2)]
        Assert-True ($median -le 0.03) ($Game + ": JoyPro.Ffb should update the sink at 50 Hz while effects play; the median step was " + $median + " s.")
        $runLate = $sorted[-1] - 0.02
    }
    $simLate = 0.0
    if ($SimOutput -match 'longest gap ([0-9.]+) s') {
        $simLate = [double]::Parse($Matches[1], [System.Globalization.CultureInfo]::InvariantCulture) - $SimPeriod
    }
    else {
        Assert-True $false ($Game + ": the simulator did not report its longest gap: " + $SimOutput)
    }
    return [math]::Max(0.0, [math]::Max($runLate, $simLate))
}

# One flight per game: simulate (synthetic loop, 13 s) + run (dry). Spring and G-force off, so the buffet is alone on Y.
# Effect lengths are checked against the flight's script with a tolerance derived from this run's own lateness
# (Get-FfbtLateness). Measuring in simulation time instead is not possible: the dry sink records JoyPro.Ffb's clock
# (when each effect was sent), the game's clock is not in the record, and War Thunder's answers carry no time at all.
# Wall-clock bounds alone were flaky on a busy PC (several builds in parallel: "gear rumble lasted 0.65 s"): when the
# simulator or JoyPro.Ffb is not scheduled for a while, an effect starts late or ends early by up to that delay, and a
# step of more than half the gear travel within one frame is not counted as movement at all. The fixed bounds below
# already cover an idle PC (measured lateness up to about 0.12 s); only the lateness E beyond $ffbtIdleLateness widens
# them: each edge of an effect may move by E, so the bounds widen by 2 E, and updates up to the longest measured step
# apart stay one segment. On an idle PC the checks are exactly as strict as before; a missing, stuck or one-tick
# effect, or a sink that is not fed at 50 Hz, still fails under any load.
$ffbtIdleLateness = 0.15
function Get-FfbtExcess { param([double]$Lateness) return [math]::Max(0.0, $Lateness - $ffbtIdleLateness) }
function Assert-FfbtFlight {
    param([string]$Game, $Record, [bool]$Hits, [double]$Lateness = 0.0)
    Assert-True ($Record.Count -gt 200) ($Game + ": too few dry sink updates (" + $Record.Count + ").")
    $slack = 2 * (Get-FfbtExcess $Lateness)
    $join = [math]::Max(0.15, $Lateness + 0.05)
    $note = " (tolerance +/- " + [math]::Round($slack, 3) + " s for a lateness of " + [math]::Round($Lateness, 3) + " s in this run)"
    foreach ($off in @("spring", "gforce")) {
        Assert-True ((Get-FfbtSegments $Record $off).Count -eq 0) ($Game + ": the switched-off effect '" + $off + "' was sent.")
    }
    $buffet = Get-FfbtSegments $Record "buffet" $join
    Assert-True ($buffet.Count -ge 1) ($Game + ": no stall buffet during the pull to 22 deg AoA.")
    $b = $buffet | Sort-Object { $_.End - $_.Start } -Descending | Select-Object -First 1
    Assert-True (($b.End - $b.Start) -ge (0.8 - $slack) -and ($b.End - $b.Start) -le (2.6 + $slack)) ($Game + ": buffet lasted " + ($b.End - $b.Start) + " s; expected about 1.9 s (AoA above 10 deg)" + $note + ".")
    $early = Get-FfbtMean $b $b.Start ($b.Start + 0.4)
    $late = Get-FfbtMean $b ($b.End - 0.4) $b.End
    Assert-True ($late -gt $early + 0.05) ($Game + ": buffet should grow with AoA (start " + $early + ", end " + $late + ").")
    $gear = Get-FfbtSegments $Record "gear" $join
    Assert-True ($gear.Count -ge 1) ($Game + ": no gear rumble while the gear moved.")
    foreach ($segment in $gear) {
        $length = $segment.End - $segment.Start
        Assert-True ($length -ge (0.7 - $slack) -and $length -le (1.8 + $slack)) ($Game + ": gear rumble lasted " + $length + " s; the gear moves for 1 s (+0.3 s hold)" + $note + ".")
    }
    $guns = Get-FfbtSegments $Record "guns" $join
    Assert-True ($guns.Count -ge 1) ($Game + ": no gun bursts while firing.")
    $g = $guns | Sort-Object { $_.End - $_.Start } -Descending | Select-Object -First 1
    Assert-True (($g.End - $g.Start) -ge (0.6 - $slack) -and ($g.End - $g.Start) -le (1.6 + $slack)) ($Game + ": gun bursts lasted " + ($g.End - $g.Start) + " s; the guns fire for 1 s" + $note + ".")
    Assert-True ((@($g.Values | Where-Object { $_ -gt 0.05 }).Count -gt 3) -and (@($g.Values | Where-Object { $_ -lt 0.001 }).Count -gt 3)) ($Game + ": gun fire should come as pulsed bursts (on and off).")
    $runway = Get-FfbtSegments $Record "runway" $join
    Assert-True ($runway.Count -ge 1) ($Game + ": no runway rumble during the landing roll.")
    $r = $runway | Sort-Object { $_.End - $_.Start } -Descending | Select-Object -First 1
    Assert-True (($r.End - $r.Start) -ge (1.0 - $slack) -and ($r.End - $r.Start) -le (2.6 + $slack)) ($Game + ": runway rumble lasted " + ($r.End - $r.Start) + " s; the roll takes 2 s" + $note + ".")
    if ($Hits) {
        Assert-True ((Get-FfbtSegments $Record "hits").Count -ge 1) ($Game + ": no jolt for the hit.")
    }
}

try {
    $env:JOYPRO_DATA_DIR = $ffbtData
    $env:JOYPRO_FFB_DRY = "1"
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = "1"
    $env:JOYPRO_INSTALL_DETECTION = "off"

    Write-Host "Building JoyPro.Ffb ($Configuration)..."
    & dotnet build $ffbtProject -c $Configuration -v q -nologo | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dotnet build of JoyPro.Ffb failed."
    Assert-True (Test-Path $ffbtExe) ("JoyPro.Ffb.exe not found at " + $ffbtExe)

    # 1. Settings.
    $ffbtEffects = Run-Engine @("ffb", "telemetry", "effects", "--text")
    Assert-Match $ffbtEffects.Output "buffet - Stall / AoA buffet" "ffb telemetry effects should list the built-in effects."
    $ffbtSet = Run-Engine @("ffb", "telemetry", "config", "set", "--param", "enabled=true", "--param", "max-force=0.5", "--param", "device={AD5168C0-B52F-11F1-8001-444553540000}", "--text")
    Assert-True ($ffbtSet.ExitCode -eq 0) ("ffb telemetry config set failed: " + $ffbtSet.Output)
    $ffbtSet = Run-Engine @("ffb", "telemetry", "config", "set", "--game", "il2", "--effect", "buffet", "--param", "enabled=false", "--param", "strength=0.5", "--text")
    Assert-True ($ffbtSet.ExitCode -eq 0) ("ffb telemetry config set --effect failed: " + $ffbtSet.Output)
    $ffbtSet = Run-Engine @("ffb", "telemetry", "config", "set", "--game", "DCS", "--plane", "F-16C_50", "--param", "graph=viper.jpffb.json", "--param", "strength=0.8", "--text")
    Assert-True ($ffbtSet.ExitCode -eq 0) ("ffb telemetry config set --plane failed: " + $ffbtSet.Output)
    $ffbtBad = Run-Engine @("ffb", "telemetry", "config", "set", "--param", "max-force=1.5", "--text")
    Assert-True ($ffbtBad.ExitCode -ne 0) "max-force above 1 must be refused."
    $ffbtBad = Run-Engine @("ffb", "telemetry", "config", "set", "--game", "WarThunder", "--param", "url=http://example.com:8111/", "--text")
    Assert-True ($ffbtBad.ExitCode -ne 0) "A War Thunder URL off this PC must be refused."
    $ffbtBad = Run-Engine @("ffb", "telemetry", "config", "set", "--game", "Elite", "--param", "enabled=true", "--text")
    Assert-True ($ffbtBad.ExitCode -ne 0) "An unsupported game must be refused."
    $ffbtConfigPath = Join-Path $ffbtData "Ffb\Telemetry\telemetry-ffb.json"
    $ffbtConfig = Get-Content -Raw $ffbtConfigPath | ConvertFrom-Json
    Assert-True ($ffbtConfig.enabled -and $ffbtConfig.maxForce -eq 0.5) "config set should write enabled and maxForce."
    Assert-True ($ffbtConfig.games.IL2Game.effects.buffet.enabled -eq $false -and $ffbtConfig.games.IL2Game.effects.buffet.strength -eq 0.5) "config set --effect should write the effect switch and strength."
    Assert-True ($ffbtConfig.games.DCS.aircraft.'F-16C_50'.graph -eq "viper.jpffb.json") "config set --plane should write the aircraft graph."
    $ffbtShow = Run-Engine @("ffb", "telemetry", "config", "show")
    Assert-Match $ffbtShow.Output '"maxForce":0.5' "config show (JSON) should return the settings."
    $ffbtClear = Run-Engine @("ffb", "telemetry", "config", "clear", "--game", "DCS", "--plane", "F-16C_50", "--text")
    Assert-True ($ffbtClear.ExitCode -eq 0) ("config clear --plane failed: " + $ffbtClear.Output)
    Assert-True ($null -eq (Get-Content -Raw $ffbtConfigPath | ConvertFrom-Json).games.DCS.aircraft.'F-16C_50') "config clear --plane should drop the aircraft."
    # Centring spring and per-device directions (the direction test saves them).
    $ffbtSet = Run-Engine @("ffb", "telemetry", "config", "set", "--param", "centring-spring=false", "--param", "centring-stiffness=0.45", "--text")
    Assert-True ($ffbtSet.ExitCode -eq 0) ("config set centring failed: " + $ffbtSet.Output)
    $ffbtSet = Run-Engine @("ffb", "telemetry", "config", "set", "--device", "{ad5168c0-b52f-11f1-8001-444553540000}", "--param", "invert-y=true", "--text")
    Assert-True ($ffbtSet.ExitCode -eq 0) ("config set --device failed: " + $ffbtSet.Output)
    $ffbtBad = Run-Engine @("ffb", "telemetry", "config", "set", "--device", "Rhino", "--param", "invert-y=true", "--text")
    Assert-True ($ffbtBad.ExitCode -ne 0) "--device needs an instance guid."
    $ffbtBad = Run-Engine @("ffb", "telemetry", "config", "set", "--device", "{AD5168C0-B52F-11F1-8001-444553540000}", "--param", "strength=1", "--text")
    Assert-True ($ffbtBad.ExitCode -ne 0) "--device only takes invert-x / invert-y."
    $ffbtConfig = Get-Content -Raw $ffbtConfigPath | ConvertFrom-Json
    Assert-True ($ffbtConfig.centringSpring -eq $false -and $ffbtConfig.centringStiffness -eq 0.45) "config set should write the centring spring settings."
    Assert-True ($ffbtConfig.devices.'{AD5168C0-B52F-11F1-8001-444553540000}'.invertY -eq $true -and $ffbtConfig.devices.'{AD5168C0-B52F-11F1-8001-444553540000}'.invertX -eq $false) "config set --device should write the device's directions."
    $ffbtClear = Run-Engine @("ffb", "telemetry", "config", "clear", "--device", "{AD5168C0-B52F-11F1-8001-444553540000}", "--text")
    Assert-True ($ffbtClear.ExitCode -eq 0) ("config clear --device failed: " + $ffbtClear.Output)
    Assert-True ($null -eq (Get-Content -Raw $ffbtConfigPath | ConvertFrom-Json).devices.'{AD5168C0-B52F-11F1-8001-444553540000}') "config clear --device should drop the device."
    $ffbtStatus = Run-Engine @("ffb", "telemetry", "status", "--text")
    Assert-True ($ffbtStatus.ExitCode -eq 0) ("ffb telemetry status failed: " + $ffbtStatus.Output)
    Assert-Match $ffbtStatus.Output "force cap: 50 %" "status should show the force cap."
    Assert-Match $ffbtStatus.Output "War Thunder: on, http://127\.0\.0\.1:8111/" "status should list War Thunder's web server."

    # 2. DCS: the Hooks script; Export.lua (other tools) stays byte-identical.
    $ffbtDcs = Join-Path $ffbtDir "SavedGames\DCS"
    New-Item -Path (Join-Path $ffbtDcs "Scripts") -ItemType Directory -Force | Out-Null
    $ffbtExportLua = Join-Path $ffbtDcs "Scripts\Export.lua"
    [System.IO.File]::WriteAllText($ffbtExportLua, "local lfs=require('lfs'); dofile(lfs.writedir()..[[Scripts\TacviewGameExport.lua]])`r`n", $ffbtUtf8)
    $ffbtExportBefore = [System.IO.File]::ReadAllBytes($ffbtExportLua)
    $ffbtHook = Join-Path $ffbtDcs "Scripts\Hooks\JoyProFfb.lua"
    $ffbtDry = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--dry-run", "--text")
    Assert-True ($ffbtDry.ExitCode -eq 0 -and $ffbtDry.Output -match "Create .*JoyProFfb\.lua") ("setup dcs --dry-run should list the hook: " + $ffbtDry.Output)
    Assert-True (-not (Test-Path $ffbtHook)) "setup dcs --dry-run must not write."
    $ffbtInstall = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--text")
    Assert-True ($ffbtInstall.ExitCode -eq 0 -and (Test-Path $ffbtHook)) ("setup dcs should write the hook: " + $ffbtInstall.Output)
    $ffbtHookText = [System.IO.File]::ReadAllText($ffbtHook)
    Assert-Match $ffbtHookText "local PORT = 41992" "the DCS hook should send to JoyPro's DCS port."
    Assert-Match $ffbtHookText "setUserCallbacks" "the DCS hook should register with Sim/DCS.setUserCallbacks."
    Assert-Match $ffbtHookText "LoIsOwnshipExportAllowed" "the DCS hook should respect the server's export permission."
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$ffbtExportBefore, [System.IO.File]::ReadAllBytes($ffbtExportLua))) "setup dcs must not change Export.lua."
    $ffbtAgain = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--text")
    Assert-Match $ffbtAgain.Output "nothing to change" "setup dcs twice should change nothing."
    Run-Engine @("ffb", "telemetry", "config", "set", "--game", "DCS", "--param", "port=41993", "--text") | Out-Null
    $ffbtStale = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--dry-run", "--text")
    Assert-Match $ffbtStale.Output "outdated" "setup should notice a hook with an old port."
    $ffbtUpdate = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--text")
    Assert-Match $ffbtUpdate.Output "Update .*JoyProFfb\.lua" "setup dcs should update an outdated hook."
    Assert-Match ([System.IO.File]::ReadAllText($ffbtHook)) "local PORT = 41993" "the updated hook should use the new port."
    Run-Engine @("ffb", "telemetry", "config", "set", "--game", "DCS", "--param", "port=0", "--text") | Out-Null
    $ffbtForeign = Join-Path $ffbtDir "SavedGames\DCS.foreign"
    New-Item -Path (Join-Path $ffbtForeign "Scripts\Hooks") -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $ffbtForeign "Scripts\Hooks\JoyProFfb.lua"), "-- someone else's file", $ffbtUtf8)
    $ffbtRefused = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtForeign, "--text")
    Assert-True ($ffbtRefused.ExitCode -ne 0) "setup dcs must not overwrite a JoyProFfb.lua JoyPro did not write."
    $ffbtRemove = Run-Engine @("ffb", "telemetry", "setup", "dcs", "--path", $ffbtDcs, "--remove", "--text")
    Assert-True ($ffbtRemove.ExitCode -eq 0 -and -not (Test-Path $ffbtHook)) ("setup dcs --remove should delete the hook: " + $ffbtRemove.Output)
    Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$ffbtExportBefore, [System.IO.File]::ReadAllBytes($ffbtExportLua))) "setup dcs --remove must not change Export.lua."
    $ffbtBackups = Run-Engine @("backups", "list", "--game", "DCS", "--text")
    Assert-Match $ffbtBackups.Output "ffb telemetry setup" "the DCS hook changes should be in a backup set."

    # 3. IL-2: startup.cfg without a telemetry section (JoyPro adds one) and with another tool's section (one addrN).
    $ffbtIl2 = Join-Path $ffbtDir "IL2"
    New-Item -Path (Join-Path $ffbtIl2 "data") -ItemType Directory -Force | Out-Null
    $ffbtStartup = Join-Path $ffbtIl2 "data\startup.cfg"
    foreach ($fixture in @("startup_plain.cfg", "startup_other_tool.cfg")) {
        Copy-Item -Path (Join-Path $engineDir ("tests\fixtures\ffbtelemetry\" + $fixture)) -Destination $ffbtStartup -Force
        $before = [System.IO.File]::ReadAllBytes($ffbtStartup)
        $install = Run-Engine @("ffb", "telemetry", "setup", "il2", "--path", $ffbtIl2, "--text")
        Assert-True ($install.ExitCode -eq 0 -and $install.Output -match "Installed: yes") ("setup il2 (" + $fixture + ") failed: " + $install.Output)
        $text = [System.IO.File]::ReadAllText($ffbtStartup)
        Assert-Match $text '(?s)\[KEY = telemetrydevice\][^\[]*addr\d* = "127\.0\.0\.1:41994"[^\[]*enable = true[^\[]*\[END\]' ("setup il2 should send telemetry to JoyPro (" + $fixture + "): " + $text)
        Assert-NotMatch $text "[^`r]`n" ("setup il2 must keep the CRLF line ends (" + $fixture + ").")
        if ($fixture -eq "startup_other_tool.cfg") {
            Assert-Match $text 'addr = "127\.0\.0\.1"\r\n\taddr1 = "127\.0\.0\.1:29373"\r\n\taddr2 = "127\.0\.0\.1:41994"' "setup il2 should add addr2 after the other tool's addresses."
            Assert-Match $text '(?s)\[KEY = motiondevice\]\r\n\taddr = "127\.0\.0\.1"\r\n\tdecimation = 2\r\n\tenable = true\r\n\tport = 4321' "setup il2 must not touch the motion device."
        }
        $twice = Run-Engine @("ffb", "telemetry", "setup", "il2", "--path", $ffbtIl2, "--text")
        Assert-Match $twice.Output "nothing to change" ("setup il2 twice should change nothing (" + $fixture + ").")
        $remove = Run-Engine @("ffb", "telemetry", "setup", "il2", "--path", $ffbtIl2, "--remove", "--text")
        Assert-True ($remove.ExitCode -eq 0) ("setup il2 --remove failed: " + $remove.Output)
        Assert-True ([System.Linq.Enumerable]::SequenceEqual([byte[]]$before, [System.IO.File]::ReadAllBytes($ffbtStartup))) ("setup il2 --remove should restore startup.cfg byte for byte (" + $fixture + "): " + [System.IO.File]::ReadAllText($ffbtStartup))
    }
    $ffbtWt = Run-Engine @("ffb", "telemetry", "setup", "warthunder", "--text")
    Assert-Match $ffbtWt.Output "Nothing to set up" "War Thunder needs no setup."

    # 4. The process per game: synthetic flight -> dry sink.
    $ffbtFlightConfig = Join-Path $ffbtDir "flight.json"
    Write-FfbtConfig -Path $ffbtFlightConfig -MaxForce 1.0 -OffEffects @("spring", "gforce")
    $ffbtRuns = @()
    foreach ($game in @("DCS", "IL2Game", "WarThunder")) {
        $port = Get-FfbtFreePort
        $record = Join-Path $ffbtDir ("flight-" + $game + ".jsonl")
        $runArgs = @("run", "--game", $game, "--sink", "dry", "--record", $record, "--config", $ffbtFlightConfig, "--no-game-check", "--no-status-file", "--duration", "15")
        if ($game -eq "WarThunder") { $runArgs += @("--wt-url", ("http://127.0.0.1:" + $port + "/")) } else { $runArgs += @("--port", [string]$port) }
        $run = Start-Ffbt -Arguments $runArgs -Name ("run-" + $game)
        Start-Sleep -Milliseconds 700
        $sim = Start-Ffbt -Arguments @("simulate", "--game", $game, "--port", [string]$port, "--seconds", "13.5") -Name ("sim-" + $game)
        $ffbtRuns += [PSCustomObject]@{ Game = $game; Run = $run; Sim = $sim; Record = $record }
    }
    foreach ($r in $ffbtRuns) {
        Wait-Ffbt $r.Sim 30 ("JoyPro.Ffb simulate " + $r.Game)
        Wait-Ffbt $r.Run 30 ("JoyPro.Ffb run " + $r.Game)
        Assert-True ($r.Run.ExitCode -eq 0) ("JoyPro.Ffb run " + $r.Game + " ended with " + $r.Run.ExitCode + ": " + (Get-Content -Raw (Join-Path $ffbtDir ("run-" + $r.Game + ".out.txt"))))
        $record = Read-FfbtRecord $r.Record
        $simOutput = Get-Content -Raw (Join-Path $ffbtDir ("sim-" + $r.Game + ".out.txt"))
        $simPeriod = switch ($r.Game) { "DCS" { 1.0 / 60 } "IL2Game" { 1.0 / 50 } default { 0.05 } }
        $lateness = Get-FfbtLateness -Game $r.Game -Record $record -SimOutput $simOutput -SimPeriod $simPeriod
        Write-Host ("  " + $r.Game + ": lateness of this run " + [math]::Round($lateness, 3) + " s (" + $simOutput.Trim() + ")")
        Assert-FfbtFlight -Game $r.Game -Record $record -Hits ($r.Game -eq "IL2Game") -Lateness $lateness
        Assert-FfbtCap $record 1.0 $r.Game
        $out = Get-Content -Raw (Join-Path $ffbtDir ("run-" + $r.Game + ".out.txt"))
        Assert-Match $out "active" ($r.Game + ": the status line should say active: " + $out)
    }
    Assert-Match (Get-Content -Raw (Join-Path $ffbtDir "run-DCS.out.txt")) "active - Synthetic-F" "DCS: the status should name the aircraft from the hook's plane line."
    Assert-Match (Get-Content -Raw (Join-Path $ffbtDir "run-WarThunder.out.txt")) "active - synthetic_fighter" "War Thunder: the status should name the vehicle from /indicators."

    # 5a. Telemetry timeout: the game goes quiet from 3 s to 5 s.
    $ffbtTimeoutConfig = Join-Path $ffbtDir "timeout.json"
    Write-FfbtConfig -Path $ffbtTimeoutConfig -MaxForce 0.6 -OffEffects @()
    $port = Get-FfbtFreePort
    $timeoutRecord = Join-Path $ffbtDir "timeout.jsonl"
    $run = Start-Ffbt -Arguments @("run", "--game", "DCS", "--sink", "dry", "--record", $timeoutRecord, "--config", $ffbtTimeoutConfig, "--no-game-check", "--no-status-file", "--port", [string]$port, "--duration", "8") -Name "run-timeout"
    Start-Sleep -Milliseconds 700
    $sim = Start-Ffbt -Arguments @("simulate", "--game", "DCS", "--port", [string]$port, "--seconds", "7", "--gap-from", "3", "--gap-to", "5") -Name "sim-timeout"
    Wait-Ffbt $sim 20 "simulate (timeout)"
    Wait-Ffbt $run 20 "run (timeout)"
    $record = Read-FfbtRecord $timeoutRecord
    $longest = 0.0
    $gapStart = 0.0
    for ($i = 1; $i -lt $record.Count; $i++) {
        if ($null -eq $record[$i].effects) { continue }
        $previous = $null
        for ($j = $i - 1; $j -ge 0; $j--) { if ($null -ne $record[$j].effects) { $previous = $record[$j]; break } }
        if ($null -ne $previous -and ($record[$i].t - $previous.t) -gt $longest) { $longest = $record[$i].t - $previous.t; $gapStart = $previous.t }
    }
    # The game is quiet for 2 s; forces keep going for the 0.5 s timeout, then stop until the next frame: ~1.5 s pause.
    # Same load tolerance as the flights (Get-FfbtLateness without the steps around the STOP ALL, which are what is
    # checked here). The STOP ALL is one tick: a stall can land on exactly that step, so on a busy PC it may take up
    # to twice the longest other step of this run (seen under load: 0.42 s with other steps up to 0.32 s); idle it
    # must come within 0.1 s.
    $lateness = Get-FfbtLateness -Game "timeout" -Record $record -SimOutput (Get-Content -Raw (Join-Path $ffbtDir "sim-timeout.out.txt")) -SimPeriod (1.0 / 60) -SkipStopAll
    $excess = Get-FfbtExcess $lateness
    $stopAllowed = [math]::Max(0.1, $(if ($excess -gt 0) { 2 * ($lateness + 0.02) } else { 0.1 }))
    Assert-True ($longest -gt (1.2 - 2 * $excess) -and $longest -lt (1.9 + 2 * $excess)) ("timeout: the effects should pause for the 2 s gap minus the 0.5 s timeout (longest pause " + $longest + " s; lateness of this run " + [math]::Round($lateness, 3) + " s).")
    $stops = @($record | Where-Object { $_.stopAll -and $_.t -gt $gapStart -and $_.t -lt $gapStart + $longest })
    Assert-True ($stops.Count -eq 1) ("timeout: exactly one STOP ALL while the game is quiet, got " + $stops.Count + ".")
    $stopDelay = $stops[0].t - $gapStart
    Assert-True ($stopDelay -le $stopAllowed) ("timeout: STOP ALL came " + $stopDelay + " s after the last update; it should follow at once (lateness of this run " + [math]::Round($lateness, 3) + " s).")

    # 5a2. The same gap with "JoyPro provides the centring spring": the springs (with their trim) are held -- refreshed
    # about once a second, nothing else -- and no STOP ALL until the run ends.
    $ffbtHoldConfig = Join-Path $ffbtDir "hold.json"
    Write-FfbtConfig -Path $ffbtHoldConfig -MaxForce 0.6 -OffEffects @() -Centring $true
    $port = Get-FfbtFreePort
    $holdRecord = Join-Path $ffbtDir "hold.jsonl"
    $run = Start-Ffbt -Arguments @("run", "--game", "DCS", "--sink", "dry", "--record", $holdRecord, "--config", $ffbtHoldConfig, "--no-game-check", "--no-status-file", "--port", [string]$port, "--duration", "8") -Name "run-hold"
    Start-Sleep -Milliseconds 700
    $sim = Start-Ffbt -Arguments @("simulate", "--game", "DCS", "--port", [string]$port, "--seconds", "7", "--gap-from", "3", "--gap-to", "5") -Name "sim-hold"
    Wait-Ffbt $sim 20 "simulate (hold)"
    Wait-Ffbt $run 20 "run (hold)"
    $record = Read-FfbtRecord $holdRecord
    $springOnly = @($record | Where-Object { $null -ne $_.effects -and $_.effects.Count -gt 0 -and @($_.effects | Where-Object { $_.type -ne "Spring" }).Count -eq 0 })
    Assert-True ($springOnly.Count -ge 1) "hold: while the game is quiet only springs should be sent (none found)."
    Assert-True (@($springOnly | Where-Object { @($_.effects | Where-Object { $_.axis -eq "X" }).Count -ge 1 -and @($_.effects | Where-Object { $_.axis -eq "Y" }).Count -ge 1 }).Count -ge 1) "hold: both axes should keep a spring."
    $forces = @($record | Where-Object { $null -ne $_.effects -and @($_.effects | Where-Object { $_.type -ne "Spring" }).Count -gt 0 })
    Assert-True ($forces.Count -gt 50) "hold: forces should be sent before and after the gap."
    $firstForce = $forces[0].t
    $lastForce = $forces[$forces.Count - 1].t
    Assert-True (@($springOnly | Where-Object { $_.t -gt $firstForce -and $_.t -lt $lastForce }).Count -ge 1) "hold: the springs should be held during the gap, between the forces."
    $holdStops = @($record | Where-Object { $_.stopAll -and $_.t -gt $firstForce -and $_.t -lt $lastForce })
    Assert-True ($holdStops.Count -eq 0) ("hold: no STOP ALL while the game is quiet, got " + $holdStops.Count + ".")
    Assert-True ($record[$record.Count - 1].stopAll) "hold: the run should still end with STOP ALL."

    # 5b. Game exit: JoyPro.Ffb follows the game process and ends with STOP ALL when it exits.
    $ffbtGame = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 60") -PassThru -WindowStyle Hidden
    $ffbtProcesses.Add($ffbtGame) | Out-Null
    $port = Get-FfbtFreePort
    $exitRecord = Join-Path $ffbtDir "game-exit.jsonl"
    $run = Start-Ffbt -Arguments @("run", "--game", "DCS", "--sink", "dry", "--record", $exitRecord, "--config", $ffbtTimeoutConfig, "--game-pid", [string]$ffbtGame.Id, "--port", [string]$port, "--duration", "30") -Name "run-exit"
    Start-Sleep -Milliseconds 700
    $sim = Start-Ffbt -Arguments @("simulate", "--game", "DCS", "--port", [string]$port, "--seconds", "6") -Name "sim-exit"
    Start-Sleep -Seconds 3
    Stop-Process -Id $ffbtGame.Id -Force
    Wait-Ffbt $run 5 "JoyPro.Ffb after the game exited"
    $record = Read-FfbtRecord $exitRecord
    Assert-True (@($record | Where-Object { $null -ne $_.effects }).Count -gt 50) "game exit: forces should have been sent while the game ran."
    Assert-True ($record[$record.Count - 1].stopAll) "game exit: the last thing sent should be STOP ALL."
    Assert-Match (Get-Content -Raw (Join-Path $ffbtDir "run-exit.out.txt")) "exit: game exited" "game exit: JoyPro.Ffb should end because the game exited."
    $ffbtStatusFile = Get-Content -Raw (Join-Path $ffbtData "Ffb\Telemetry\status.json") | ConvertFrom-Json
    Assert-True ($ffbtStatusFile.state -eq "exited" -and $ffbtStatusFile.reason -eq "game exited") "game exit: status.json should say exited."
    Wait-Ffbt $sim 20 "simulate (game exit)"

    # 6. Resources: 60 s of telemetry at 60 Hz with every effect and a 50 % cap, then 10 s without telemetry (measured
    # 2026-10-11: ~1.5 % of one core active, 0 idle, private bytes ~18 MB flat, 16 threads).
    $ffbtResConfig = Join-Path $ffbtDir "resources.json"
    Write-FfbtConfig -Path $ffbtResConfig -MaxForce 0.5 -OffEffects @() -Centring $true
    $port = Get-FfbtFreePort
    $resRecord = Join-Path $ffbtDir "resources.jsonl"
    $run = Start-Ffbt -Arguments @("run", "--game", "DCS", "--sink", "dry", "--record", $resRecord, "--config", $ffbtResConfig, "--no-game-check", "--no-status-file", "--port", [string]$port, "--duration", "80") -Name "run-res"
    Start-Sleep -Milliseconds 700
    $sim = Start-Ffbt -Arguments @("simulate", "--game", "DCS", "--port", [string]$port, "--seconds", "62") -Name "sim-res"
    # Samples every 2 s while telemetry runs (15..61 s), then two without telemetry (66, 76 s).
    $samples = @()
    foreach ($at in @(@(for ($s = 15; $s -le 59; $s += 2) { $s }) + @(61, 66, 76))) {
        $wait = $at - ((Get-Date) - $run.StartTime).TotalSeconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
        $run.Refresh()
        $samples += [PSCustomObject]@{
            At = $at
            Cpu = $run.TotalProcessorTime.TotalSeconds
            WorkingSetMb = [math]::Round($run.WorkingSet64 / 1MB, 1)
            PrivateMb = [math]::Round($run.PrivateMemorySize64 / 1MB, 1)
            Handles = $run.HandleCount
            Threads = $run.Threads.Count
        }
    }
    Wait-Ffbt $run 20 "run (resources)"
    Wait-Ffbt $sim 20 "simulate (resources)"
    $samples | Format-Table -AutoSize | Out-String | Write-Host
    $s15 = $samples | Where-Object { $_.At -eq 15 }
    $s61 = $samples | Where-Object { $_.At -eq 61 }
    $s66 = $samples | Where-Object { $_.At -eq 66 }
    $s76 = $samples | Where-Object { $_.At -eq 76 }
    $active = ($s61.Cpu - $s15.Cpu) / ($s61.At - $s15.At)
    $idle = ($s76.Cpu - $s66.Cpu) / ($s76.At - $s66.At)
    Write-Host ("JoyPro.Ffb CPU while active: " + [math]::Round($active * 100, 2) + " % of one core; idle: " + [math]::Round($idle * 100, 3) + " %")
    Assert-True ($active -lt 0.05) ("JoyPro.Ffb used " + [math]::Round($active * 100, 2) + " % of a core with telemetry at 60 Hz; the budget is 5 %.")
    Assert-True ($idle -lt 0.005) ("JoyPro.Ffb used " + [math]::Round($idle * 100, 3) + " % of a core without telemetry; it should be idle.")
    Assert-True ($s61.WorkingSetMb -lt 80) ("JoyPro.Ffb working set " + $s61.WorkingSetMb + " MB is too large.")
    # Growth is a trend, not the difference of two samples. Measured 2026-10-12 over 7 minutes of telemetry (idle PC and
    # 64 busy threads on 32 cores): private bytes reach a plateau of ~17.4 MB once warmed up (under heavy load only
    # after ~50 s, because the process starts slowly) and stay there; single samples jump by 2-7 MB for a few seconds
    # when the thread pool adds threads or a collection is due, and fall back. Comparing the 15 s and 61 s samples alone
    # therefore failed under load ("grew from 17.9 to 24.1 MB") without a leak. So: after a warm-up (from 21 s), the
    # median of the later half of the window must not be above the median of the earlier half by 3 MB or more (a leak
    # of ~0.15 MB/s or more), and the plateau must stay small (the GC heap limit in JoyPro.Ffb.csproj keeps it near
    # 18 MB; without it the process sits near 58 MB). Handles and threads are compared the same way.
    $window = @($samples | Where-Object { $_.At -ge 21 -and $_.At -le 61 })
    $half = [int][math]::Floor($window.Count / 2)
    $earlier = @($window | Select-Object -First $half)
    $later = @($window | Select-Object -Last $half)
    function Get-FfbtMedian { param([double[]]$Values) $sorted = @($Values | Sort-Object); return $sorted[[int][math]::Floor($sorted.Count / 2)] }
    $privateEarlier = Get-FfbtMedian @($earlier | ForEach-Object { $_.PrivateMb })
    $privateLater = Get-FfbtMedian @($later | ForEach-Object { $_.PrivateMb })
    Assert-True (($privateLater - $privateEarlier) -lt 3) ("JoyPro.Ffb private memory keeps growing: median " + $privateEarlier + " MB (21-39 s) -> " + $privateLater + " MB (43-61 s).")
    Assert-True ($privateLater -lt 40) ("JoyPro.Ffb private memory " + $privateLater + " MB (median 43-61 s) is too large; expected about 18 MB.")
    $handlesEarlier = Get-FfbtMedian @($earlier | ForEach-Object { $_.Handles })
    $handlesLater = Get-FfbtMedian @($later | ForEach-Object { $_.Handles })
    Assert-True (($handlesLater - $handlesEarlier) -lt 25) ("JoyPro.Ffb handles keep growing: median " + $handlesEarlier + " -> " + $handlesLater + ".")
    $threadsEarlier = Get-FfbtMedian @($earlier | ForEach-Object { $_.Threads })
    $threadsLater = Get-FfbtMedian @($later | ForEach-Object { $_.Threads })
    Assert-True (($threadsLater - $threadsEarlier) -lt 5) ("JoyPro.Ffb threads keep growing: median " + $threadsEarlier + " -> " + $threadsLater + ".")
    $record = Read-FfbtRecord $resRecord
    Assert-FfbtCap $record 0.5 "resources run (every effect, cap 50 %)"
    Assert-True (@($record | Where-Object { $null -ne $_.effects -and $_.effects.Count -ge 4 }).Count -gt 10) "resources run: several effects should play at once (the cap test needs them)."
}
finally {
    foreach ($process in $ffbtProcesses) {
        try { if (-not $process.HasExited) { $process.Kill() } } catch { }
    }
    $env:JOYPRO_DATA_DIR = $ffbtPrevious.Data
    $env:JOYPRO_FFB_DRY = $ffbtPrevious.Dry
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $ffbtPrevious.Ignore
    $env:JOYPRO_INSTALL_DETECTION = $ffbtPrevious.Detection
}
