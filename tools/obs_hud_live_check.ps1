# Live check of the HUD page for OBS (docs/hud.md "Live check in OBS"), without the screen: no screen capture, no
# mouse or keyboard input, no JoyPro window.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\obs_hud_live_check.ps1 [-Obs] [-UserHud | -HudJson <file>] [-Out <folder>]
#
# 1. The page: the opt-in test HudObsLiveTests (gui/JoyPro.Gui.Tests) starts the real HudStreamServer by Start HUD of a
#    view model in a sandbox (<Out>\page\data) with the user's four sticks and a fake device stream moving every axis,
#    and checks GET /hud, /hud/state and the live channel /hud/events. Without -Obs that is all (the dry run).
# 2. -Obs: OBS Studio is started from a private copy of its program folder in portable mode (<Out>\obs-portable), with
#    a copy ("mirror") of the user's %APPDATA%\obs-studio as its configuration. Only that copy is changed: a scene
#    collection "JoyProHudTest" with a Browser Source on the page, the built-in obs-websocket switched on (127.0.0.1,
#    a random password, port -WebSocketPort) and start minimised to the tray. The Browser Source's picture is taken
#    through obs-websocket (GetSourceScreenshot, three shots two seconds apart: drawn and moving), then OBS is closed.
#    Safety: never runs while obs64 runs; the user's %APPDATA%\obs-studio is mirrored before (<Out>\obs-backup) and
#    restored exactly after (robocopy /MIR), then compared file by file (size + SHA-256) with the state before.
#
#   -Out        the output folder (default %TEMP%\JoyPro-obs-hud-live): live-check.md, obs-live-check.md, shots
#   -Port       the page's port (default 18765, not JoyPro's 8765, so a running JoyPro is never in the way)
#   -UserHud    copies %APPDATA%\JoyPro2\hud.json (read only) into the sandbox; -HudJson <file> another one; else the demo
#   -Seconds    how long the page stays up for OBS (default 120; ended early once the shots are taken)
#   -ObsDir     OBS's program folder (default C:\Program Files\obs-studio)
#   -NoBuild    skips building the test project
# Exit code 0 = every check passed.
param(
    [switch]$Obs,
    [string]$Out = (Join-Path $env:TEMP "JoyPro-obs-hud-live"),
    [int]$Port = 18765,
    [switch]$UserHud,
    [string]$HudJson = "",
    [int]$Seconds = 120,
    [string]$ObsDir = "C:\Program Files\obs-studio",
    [int]$WebSocketPort = 44655,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tests = Join-Path $root "gui\JoyPro.Gui.Tests"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$pageOut = Join-Path $Out "page"
$log = Join-Path $Out "run.log"
Set-Content $log "" -Encoding UTF8
function Write-Log([string]$m) { $l = (Get-Date -Format "HH:mm:ss") + "  " + $m; Write-Host $l; Add-Content $log $l -Encoding UTF8 }

$failures = New-Object System.Collections.Generic.List[string]
function Test-Result([bool]$ok, [string]$what) {
    if ($ok) { Write-Log ("ok    " + $what) } else { Write-Log ("FAIL  " + $what); $failures.Add($what) }
}

# ---- Guards ---------------------------------------------------------------------------------------------------------
if ($Obs) {
    if (Get-Process obs64, obs32, obs -ErrorAction SilentlyContinue) { throw "OBS is running: not touching it. Close OBS and run again." }
    if (-not (Test-Path (Join-Path $ObsDir "bin\64bit\obs64.exe"))) { throw "OBS not found in $ObsDir." }
}
function Test-PortFree([int]$p) {
    try { $l = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), $p; $l.Start(); $l.Stop(); return $true } catch { return $false }
}
if (-not (Test-PortFree $Port)) { throw "Port $Port is in use: pick another with -Port." }
if ($Obs -and -not (Test-PortFree $WebSocketPort)) { throw "Port $WebSocketPort is in use: pick another with -WebSocketPort." }
if ($UserHud) { $HudJson = Join-Path $env:APPDATA "JoyPro2\hud.json" }
if ($HudJson -and -not (Test-Path $HudJson)) { throw "hud.json not found: $HudJson" }

# ---- 1. The page (opt-in test, below-normal priority, no window) ----------------------------------------------------
function Start-Quiet([string]$file, [string[]]$arguments, [string]$stdout) {
    $p = Start-Process $file -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError ($stdout + ".err")
    $null = $p.Handle   # keeps the handle, else ExitCode stays empty (Windows PowerShell)
    try { $p.PriorityClass = "BelowNormal" } catch { }
    return $p
}
if (-not $NoBuild) {
    Write-Log "Building the test project..."
    $b = Start-Quiet "dotnet" @("build", "`"$tests`"", "-p:AutoStopRunningJoyProGuiOnBuild=false", "-nologo", "-v:q", "-clp:ErrorsOnly") (Join-Path $Out "build.log")
    $b.WaitForExit()
    if ($b.ExitCode -ne 0) { Get-Content (Join-Path $Out "build.log") -Tail 20; throw "Build failed (see $Out\build.log)." }
}
$env:JOYPRO_HUD_LIVE = $pageOut
$env:JOYPRO_HUD_LIVE_PORT = "$Port"
$env:JOYPRO_HUD_LIVE_SECONDS = if ($Obs) { "$Seconds" } else { "0" }
$env:JOYPRO_HUD_LIVE_HUDJSON = $HudJson
New-Item -ItemType Directory -Force $pageOut | Out-Null
Remove-Item (Join-Path $pageOut "ready.json"), (Join-Path $pageOut "stop") -ErrorAction SilentlyContinue
Write-Log ("Page check: port $Port, " + $(if ($HudJson) { "hud.json $HudJson" } else { "demo HUD" }))
$test = Start-Quiet "dotnet" @("test", "`"$tests`"", "--no-build", "--filter", "FullyQualifiedName~HudObsLiveTests", "-nologo", "--logger", "`"console;verbosity=normal`"") (Join-Path $Out "test.log")

$obsProc = $null
$backup = Join-Path $Out "obs-backup"
$userCfg = Join-Path $env:APPDATA "obs-studio"
$mirrored = $false
function Get-Manifest([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    $base = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
    Get-ChildItem $dir -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($base.Length) + "|" + $_.Length + "|" + (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}

try {
    if ($Obs) {
        # ---- 2a. Wait for the page ------------------------------------------------------------------------------------
        $ready = Join-Path $pageOut "ready.json"
        for ($i = 0; $i -lt 360 -and -not (Test-Path $ready) -and -not $test.HasExited; $i++) { Start-Sleep -Milliseconds 500 }
        if (-not (Test-Path $ready)) { throw "The page did not come up (see $Out\test.log and $pageOut\live-check.md)." }
        $url = "http://127.0.0.1:$Port/hud"
        Write-Log "Page is up: $url"

        # ---- 2b. Mirror the user's OBS settings (restored exactly at the end) -----------------------------------------
        if (Get-Process obs64, obs32, obs -ErrorAction SilentlyContinue) { throw "OBS was started meanwhile: not touching it." }
        if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
        $before = @(Get-Manifest $userCfg)
        if (Test-Path $userCfg) {
            robocopy $userCfg $backup /MIR /COPY:DAT /DCOPY:DAT /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Mirroring $userCfg failed (robocopy $LASTEXITCODE)." }
        }
        else { New-Item -ItemType Directory -Force $backup | Out-Null }
        $mirrored = $true
        $before | Set-Content (Join-Path $Out "obs-manifest-before.txt") -Encoding UTF8
        Write-Log ("OBS settings mirrored: " + $before.Count + " files -> $backup")

        # ---- 2c. Portable OBS with a copy of the mirrored settings ----------------------------------------------------
        $portable = Join-Path $Out "obs-portable"
        $exe = Join-Path $portable "bin\64bit\obs64.exe"
        $srcExe = Join-Path $ObsDir "bin\64bit\obs64.exe"
        if (-not (Test-Path $exe) -or (Get-Item $exe).VersionInfo.FileVersion -ne (Get-Item $srcExe).VersionInfo.FileVersion -or (Get-Item $exe).Length -ne (Get-Item $srcExe).Length) {
            Write-Log "Copying OBS's program folder (once per OBS version)..."
            robocopy $ObsDir $portable /MIR /XD config /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Copying OBS failed (robocopy $LASTEXITCODE)." }
        }
        Write-Log ("OBS " + (Get-Item $exe).VersionInfo.ProductVersion + " (portable copy)")
        Set-Content (Join-Path $portable "portable_mode.txt") "" -Encoding ASCII
        $cfgRoot = Join-Path $portable "config"
        $cfg = Join-Path $cfgRoot "obs-studio"
        if (Test-Path $cfgRoot) { Remove-Item $cfgRoot -Recurse -Force }
        New-Item -ItemType Directory -Force $cfg | Out-Null
        robocopy $backup $cfg /MIR /XD logs crashes updates profiler_data /NFL /NDL /NJH /NJS /R:1 /W:1 | Out-Null

        function Set-IniValues([string]$file, [string]$section, [hashtable]$values) {
            $lines = New-Object System.Collections.Generic.List[string]
            if (Test-Path $file) { $lines.AddRange([string[]](Get-Content $file -Encoding UTF8)) }
            $start = -1
            for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq "[$section]") { $start = $i; break } }
            if ($start -lt 0) { if ($lines.Count -gt 0) { $lines.Add("") }; $lines.Add("[$section]"); $start = $lines.Count - 1 }
            $end = $lines.Count
            for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith("[")) { $end = $i; break } }
            foreach ($key in $values.Keys) {
                $found = $false
                for ($i = $start + 1; $i -lt $end; $i++) {
                    if ($lines[$i] -match ("^" + [regex]::Escape($key) + "=")) { $lines[$i] = "$key=" + $values[$key]; $found = $true; break }
                }
                if (-not $found) { $lines.Insert($end, "$key=" + $values[$key]); $end++ }
            }
            [IO.File]::WriteAllLines($file, $lines, (New-Object System.Text.UTF8Encoding $false))
        }
        # Every place OBS keeps settings points into the copy (OBS 31+ [Locations]; the user's point to %APPDATA%).
        $iniRoot = $cfgRoot.Replace('\', '\\')
        Set-IniValues (Join-Path $cfg "global.ini") "Locations" @{ Configuration = $iniRoot; SceneCollections = $iniRoot; Profiles = $iniRoot; PluginManagerSettings = $iniRoot }
        Set-IniValues (Join-Path $cfg "global.ini") "General" @{ EnableAutoUpdates = "false" }
        $wsPassword = -join ((1..24) | ForEach-Object { [char[]]"abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789" | Get-Random })
        foreach ($ini in @("user.ini", "global.ini")) {
            Set-IniValues (Join-Path $cfg $ini) "BasicWindow" @{ SysTrayEnabled = "true"; SysTrayWhenStarted = "true" }
            Set-IniValues (Join-Path $cfg $ini) "General" @{ ConfirmOnExit = "false"; FirstRun = "true" }
            Set-IniValues (Join-Path $cfg $ini) "Basic" @{ SceneCollection = "JoyProHudTest"; SceneCollectionFile = "JoyProHudTest.json" }
            Set-IniValues (Join-Path $cfg $ini) "OBSWebSocket" @{ FirstLoad = "false"; ServerEnabled = "true"; ServerPort = "$WebSocketPort"; AlertsEnabled = "false"; AuthRequired = "true"; ServerPassword = $wsPassword }
        }
        $wsDir = Join-Path $cfg "plugin_config\obs-websocket"
        New-Item -ItemType Directory -Force $wsDir | Out-Null
        $wsJson = '{"alerts_enabled":false,"auth_required":true,"first_load":false,"server_enabled":true,"server_password":"' + $wsPassword + '","server_port":' + $WebSocketPort + '}'
        [IO.File]::WriteAllText((Join-Path $wsDir "config.json"), $wsJson, (New-Object System.Text.UTF8Encoding $false))

        # The scene collection: one scene with the Browser Source on the page (1920 x 1080, OBS's transparent default CSS).
        $bUuid = [guid]::NewGuid().ToString(); $sUuid = [guid]::NewGuid().ToString()
        $collection = @"
{
  "name": "JoyProHudTest",
  "current_scene": "JoyPro HUD",
  "current_program_scene": "JoyPro HUD",
  "scene_order": [ { "name": "JoyPro HUD" } ],
  "groups": [], "quick_transitions": [], "saved_projectors": [], "transitions": [],
  "current_transition": "Fade", "transition_duration": 300, "preview_locked": false, "scaling_enabled": false, "modules": {},
  "sources": [
    { "name": "JoyPro HUD", "uuid": "$sUuid", "id": "scene", "versioned_id": "scene", "enabled": true, "flags": 0, "mixers": 0,
      "settings": { "id_counter": 1, "custom_size": false, "items": [
        { "name": "JoyPro HUD page", "source_uuid": "$bUuid", "visible": true, "locked": false, "id": 1, "rot": 0.0,
          "pos": { "x": 0.0, "y": 0.0 }, "scale": { "x": 1.0, "y": 1.0 }, "align": 5, "bounds_type": 0, "bounds_align": 0,
          "bounds": { "x": 0.0, "y": 0.0 }, "crop_left": 0, "crop_top": 0, "crop_right": 0, "crop_bottom": 0,
          "group_item_backup": false, "scale_filter": "disable", "blend_method": "default", "blend_type": "normal",
          "private_settings": {} } ] } },
    { "name": "JoyPro HUD page", "uuid": "$bUuid", "id": "browser_source", "versioned_id": "browser_source", "enabled": true,
      "flags": 0, "mixers": 255, "sync": 0, "volume": 1.0, "balance": 0.5, "muted": false,
      "settings": { "url": "$url", "width": 1920, "height": 1080, "fps": 30, "reroute_audio": false, "shutdown": false, "restart_when_active": false } }
  ]
}
"@
        New-Item -ItemType Directory -Force (Join-Path $cfg "basic\scenes") | Out-Null
        [IO.File]::WriteAllText((Join-Path $cfg "basic\scenes\JoyProHudTest.json"), $collection, (New-Object System.Text.UTF8Encoding $false))

        # ---- 2d. OBS, minimised to the tray -----------------------------------------------------------------------------
        if (Get-Process obs64, obs32, obs -ErrorAction SilentlyContinue) { throw "OBS was started meanwhile: not touching it." }
        $obsArgs = '--portable --collection "JoyProHudTest" --scene "JoyPro HUD" --minimize-to-tray --disable-updater --disable-shutdown-check --disable-missing-files-check'
        $obsProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $obsArgs -WindowStyle Minimized -PassThru
        $null = $obsProc.Handle
        try { $obsProc.PriorityClass = "BelowNormal" } catch { }
        Write-Log "OBS started (pid $($obsProc.Id), minimised to the tray)"

        # ---- 2e. obs-websocket: identify, then screenshots of the Browser Source --------------------------------------
        Add-Type -AssemblyName System.Web.Extensions, System.Drawing
        $json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $json.MaxJsonLength = [int]::MaxValue
        function Send-Ws($ws, $obj) {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 10 -Compress))
            $ws.SendAsync((New-Object ArraySegment[byte] (, $bytes)), [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
        }
        function Receive-Ws($ws, [int]$timeoutMs = 20000) {
            $ms = New-Object IO.MemoryStream
            $buf = New-Object byte[] 1048576
            $cts = New-Object Threading.CancellationTokenSource $timeoutMs
            do {
                $r = $ws.ReceiveAsync((New-Object ArraySegment[byte] (, $buf)), $cts.Token).GetAwaiter().GetResult()
                $ms.Write($buf, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            return $json.DeserializeObject([Text.Encoding]::UTF8.GetString($ms.ToArray()))
        }
        function Get-Sha256Base64([string]$text) {
            $sha = [Security.Cryptography.SHA256]::Create()
            return [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))
        }
        $ws = $null
        for ($i = 0; $i -lt 60 -and -not $ws; $i++) {
            Start-Sleep 1
            if ($obsProc.HasExited) { throw "OBS ended (exit code $($obsProc.ExitCode)); see $cfg\logs." }
            $try = New-Object Net.WebSockets.ClientWebSocket
            try { $try.ConnectAsync([Uri]"ws://127.0.0.1:$WebSocketPort", [Threading.CancellationToken]::None).Wait(); $ws = $try } catch { $try.Dispose() }
        }
        if (-not $ws) { throw "obs-websocket did not answer on port $WebSocketPort." }
        $hello = Receive-Ws $ws
        $identify = @{ op = 1; d = @{ rpcVersion = 1 } }
        if ($hello.d.authentication) {
            $secret = Get-Sha256Base64 ($wsPassword + $hello.d.authentication.salt)
            $identify.d.authentication = Get-Sha256Base64 ($secret + $hello.d.authentication.challenge)
        }
        Send-Ws $ws $identify
        $identified = Receive-Ws $ws
        Test-Result ($identified.op -eq 2) ("obs-websocket " + $hello.d.obsWebSocketVersion + " identified (OBS " + $hello.d.obsStudioVersion + ")")
        $script:req = 0
        function Invoke-WsRequest($type, $data) {
            $script:req++
            $id = "jp$($script:req)"
            Send-Ws $ws @{ op = 6; d = @{ requestType = $type; requestId = $id; requestData = $data } }
            for ($k = 0; $k -lt 50; $k++) {
                $m = Receive-Ws $ws 30000
                if ($m.op -eq 7 -and $m.d.requestId -eq $id) { return $m.d }
            }
            throw "No answer to $type."
        }
        $settings = Invoke-WsRequest "GetInputSettings" @{ inputName = "JoyPro HUD page" }
        $srcUrl = $settings.responseData.inputSettings.url
        Test-Result ($settings.requestStatus.result -and $srcUrl -eq $url) ("Browser Source in OBS points to " + $(if ($srcUrl) { $srcUrl } else { "(no url in GetInputSettings: " + (($settings | ConvertTo-Json -Depth 6 -Compress) -replace '\s+', ' ') + ")" }))
        Start-Sleep 8   # the page loads, connects to /hud/events and draws

        function Measure-Png([string]$file) {
            $bmp = [Drawing.Bitmap]::FromFile($file)
            try {
                $seen = 0; $drawn = 0; $samples = New-Object System.Collections.Generic.List[int]
                for ($y = 0; $y -lt $bmp.Height; $y += 4) {
                    for ($x = 0; $x -lt $bmp.Width; $x += 4) {
                        $c = $bmp.GetPixel($x, $y); $seen++
                        if ($c.A -gt 32) { $drawn++ }
                        $samples.Add($c.ToArgb())
                    }
                }
                return @{ Width = $bmp.Width; Height = $bmp.Height; Drawn = $drawn; Seen = $seen; Samples = $samples }
            }
            finally { $bmp.Dispose() }
        }
        $shots = @()
        for ($n = 1; $n -le 3; $n++) {
            $shot = Invoke-WsRequest "GetSourceScreenshot" @{ sourceName = "JoyPro HUD page"; imageFormat = "png"; imageWidth = 1920; imageHeight = 1080 }
            if (-not $shot.requestStatus.result) { Test-Result $false ("GetSourceScreenshot " + $n + ": " + $shot.requestStatus.comment); break }
            $data = $shot.responseData.imageData
            $file = Join-Path $Out ("obs-browser-source-" + $n + ".png")
            [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($data.Substring($data.IndexOf(",") + 1)))
            $shots += , (Measure-Png $file)
            Write-Log ("Shot $n -> $file (" + $shots[-1].Width + " x " + $shots[-1].Height + ", " + [Math]::Round(100.0 * $shots[-1].Drawn / $shots[-1].Seen, 2) + " % drawn)")
            if ($n -lt 3) { Start-Sleep 2 }
        }
        if ($shots.Count -eq 3) {
            Test-Result ($shots[0].Width -eq 1920 -and $shots[0].Height -eq 1080) "the Browser Source is 1920 x 1080"
            $share = $shots[2].Drawn / [double]$shots[2].Seen
            Test-Result ($share -gt 0.002) ("the page draws the HUD in OBS (" + [Math]::Round(100 * $share, 2) + " % of the picture)")
            Test-Result ($share -lt 0.9) "the page's background is transparent in OBS (not a full card or colour)"
            $changed = 0
            for ($k = 0; $k -lt $shots[0].Samples.Count; $k++) { if ($shots[0].Samples[$k] -ne $shots[2].Samples[$k]) { $changed++ } }
            Test-Result ($changed -gt 0) ("the picture moves with the devices (" + $changed + " sampled pixels changed in 4 s)")
        }
        try { $ws.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [Threading.CancellationToken]::None).Wait(3000) | Out-Null } catch { }
    }
}
catch {
    $failures.Add("error: " + $_.Exception.Message)
    Write-Log ("ERROR " + $_.Exception.Message)
}
finally {
    # ---- Close OBS, end the page, restore the user's OBS settings exactly --------------------------------------------
    if ($obsProc -and -not $obsProc.HasExited) {
        & taskkill.exe /PID $obsProc.Id 2>$null | Out-Null
        if (-not $obsProc.WaitForExit(20000)) { Write-Log "OBS did not close: ending it"; try { $obsProc.Kill() } catch { }; $obsProc.WaitForExit(10000) | Out-Null }
        Write-Log "OBS closed"
    }
    Set-Content (Join-Path $pageOut "stop") "" -Encoding ASCII
    if (-not $test.WaitForExit(120000)) { Write-Log "The page check did not end: stopping it"; try { $test.Kill() } catch { } }
    if ($mirrored) {
        Start-Sleep 1
        if (Test-Path $userCfg) {
            robocopy $backup $userCfg /MIR /COPY:DAT /DCOPY:DAT /NFL /NDL /NJH /NJS /R:2 /W:1 | Out-Null
        }
        $after = @(Get-Manifest $userCfg)
        $after | Set-Content (Join-Path $Out "obs-manifest-after.txt") -Encoding UTF8
        $diff = @(Compare-Object $before $after)
        if ($diff.Count -gt 0) {
            robocopy $backup $userCfg /MIR /COPY:DAT /DCOPY:DAT /NFL /NDL /NJH /NJS /R:2 /W:1 | Out-Null
            $after = @(Get-Manifest $userCfg)
            $diff = @(Compare-Object $before $after)
        }
        Test-Result ($diff.Count -eq 0) ("the user's %APPDATA%\obs-studio is exactly as before (" + $after.Count + " files, size + SHA-256)" + $(if ($diff.Count) { ": " + ($diff | ForEach-Object { $_.SideIndicator + " " + $_.InputObject }) -join "; " } else { "" }))
    }
}

# ---- Report ---------------------------------------------------------------------------------------------------------
$pageReport = Join-Path $pageOut "live-check.md"
if (Test-Path $pageReport) {
    $page = Get-Content (Join-Path $pageOut "live-check.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    Test-Result ([bool]$page.passed) ("page check (" + $pageReport + ")")
    Get-Content $pageReport -Encoding UTF8 | ForEach-Object { Write-Host $_ }
}
else { Test-Result $false "page check ran (no $pageReport; see $Out\test.log)" }
$summary = @("# OBS HUD live check", "", ("Result: " + $(if ($failures.Count -eq 0) { "all checks passed." } else { $failures.Count.ToString() + " failed." })), "", '```') + (Get-Content $log -Encoding UTF8) + @('```')
$summary | Set-Content (Join-Path $Out "obs-live-check.md") -Encoding UTF8
Write-Log ("Report: " + (Join-Path $Out "obs-live-check.md"))
if ($failures.Count -gt 0) { exit 1 } else { exit 0 }
