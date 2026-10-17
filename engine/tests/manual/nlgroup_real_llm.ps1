# Group by description against a REAL local model (docs/natural-language-grouping.md). Not part of the smoke test: it
# needs your own llama.cpp (llama-server.exe) and a GGUF model, and it fills the graphics card while a request runs.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File engine/tests/manual/nlgroup_real_llm.ps1 `
#       -Server C:\llama.cpp\llama-server.exe -Model D:\models\Qwen3-8B-Q4_K_M.gguf [-Requests "flares","master arm"]
#
# Every request runs twice: offline rules only (--llm off) and with the model (--llm on). The engine starts
# llama-server on 127.0.0.1 for one request and kills it afterwards; this script also kills every llama-server it finds
# that was not running before (finally block, also on errors) and checks with tasklist that none of them is left.
# Nothing is downloaded and the user's own settings are not touched (temporary JOYPRO_DATA_DIR).
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Model,
    [string[]]$Requests = @(
        "flares",
        "chaff and flares on the F-16 and F/A-18",
        "speed brake for all jets",
        "trim hat for warbirds in IL-2",
        "master arm",
        "gear and flaps on helicopters",
        "pop decoys on the Tomcat",
        "the button that drops bombs for WW2 bombers",
        "countermeasures on all helicopters except the Huey",
        "air brake on the hog and the warthog"
    ),
    [string]$RequestFile = "",
    [string]$GpuLayers = "",
    [string]$Engine = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
# powershell -File passes "a,b" as one string: -RequestFile (one request per line) or "a;b" give several.
if ($RequestFile.Length -gt 0) { $Requests = @(Get-Content $RequestFile | Where-Object { $_.Trim().Length -gt 0 }) }
elseif ($Requests.Count -eq 1 -and $Requests[0].Contains(";")) { $Requests = @($Requests[0].Split(";") | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 }) }
if ($Engine.Length -eq 0) {
    $Engine = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "bin\x64\Debug\engine.exe"
}
if (-not (Test-Path $Engine)) { throw "engine.exe not found: $Engine (build the engine first)." }
if (-not (Test-Path $Server)) { throw "llama-server.exe not found: $Server" }
if (-not (Test-Path $Model)) { throw "GGUF not found: $Model" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("joypro-llm-real-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$previousData = $env:JOYPRO_DATA_DIR
$before = @(Get-Process -Name "llama*" -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$rows = New-Object System.Collections.Generic.List[object]

function Invoke-FromText([string]$Request, [string]$Mode) {
    $out = Join-Path $work ("r-" + [Guid]::NewGuid().ToString("N") + ".json")
    $profilePath = Join-Path $work "real.pr0file"
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $log = & $Engine --profile $profilePath --out $out relations from-text $Request --dry-run --llm $Mode 2>&1 | Out-String
    $ErrorActionPreference = $previous
    if (-not (Test-Path $out)) { return [pscustomobject]@{ Json = $null; Log = $log } }
    return [pscustomobject]@{ Json = ([IO.File]::ReadAllText($out) | ConvertFrom-Json); Log = $log }
}

function Get-Functions($Json) {
    return (@($Json.Understood.Functions | ForEach-Object { ($_ -split " \[")[0] }) -join ", ")
}

try {
    $env:JOYPRO_DATA_DIR = Join-Path $work "data"
    $useArgs = @("llm", "use-local", "--server", $Server, "--model", $Model, "--text")
    if ($GpuLayers.Length -gt 0) { $useArgs += @("--gpu-layers", $GpuLayers) }
    & $Engine @useArgs | Out-Host
    foreach ($request in $Requests) {
        Write-Host ("=== " + $request)
        $off = Invoke-FromText $request "off"
        $on = Invoke-FromText $request "on"
        $leftNow = @(Get-Process -Name "llama*" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
        $row = [pscustomobject]@{
            Request = $request
            Offline = if ($null -eq $off.Json) { "ERROR" } else { (Get-Functions $off.Json) + " for " + $off.Json.Understood.Aircraft + " (" + $off.Json.Understood.AircraftCount + ")" }
            Model = if ($null -eq $on.Json) { "ERROR: " + $on.Log.Trim() } else { (Get-Functions $on.Json) + " for " + $on.Json.Understood.Aircraft + " (" + $on.Json.Understood.AircraftCount + ")" }
            Method = if ($null -eq $on.Json) { "" } else { $on.Json.Method }
            Note = if ($null -eq $on.Json) { "" } else { $on.Json.Llm.Note }
            Answer = if ($null -eq $on.Json) { "" } else { $on.Json.Llm.Answer }
            Seconds = if ($null -eq $on.Json) { 0 } else { [Math]::Round($on.Json.Llm.DurationMs / 1000.0, 1) }
            PromptTokens = if ($null -eq $on.Json) { 0 } else { $on.Json.Llm.PromptTokens }
            Warnings = if ($null -eq $on.Json) { "" } else { (@($on.Json.Warnings | Where-Object { $_ -like "Local model*" }) -join "; ") }
            ProcessExited = if ($null -eq $on.Json) { $false } else { $on.Json.Llm.ProcessExited }
            LeftRunning = $leftNow.Count
        }
        $rows.Add($row)
        $row | Format-List | Out-Host
        if ($leftNow.Count -gt 0) { throw "llama-server still running after '$request' (pid " + (($leftNow | ForEach-Object { $_.Id }) -join ", ") + ")." }
    }
}
finally {
    $left = @(Get-Process -Name "llama*" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
    foreach ($process in $left) {
        Write-Host ("Stopping leftover " + $process.ProcessName + " pid " + $process.Id)
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
    $tasklist = (tasklist /FI "IMAGENAME eq llama*" | Out-String)
    Write-Host $tasklist
    $still = @(Get-Process -Name "llama*" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
    $env:JOYPRO_DATA_DIR = $previousData
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    if ($still.Count -gt 0) { throw "llama-server processes are still running: " + (($still | ForEach-Object { $_.Id }) -join ", ") }
}

$table = New-Object System.Text.StringBuilder
[void]$table.AppendLine("| Request | Offline rules | With model | s | Model note |")
[void]$table.AppendLine("|---|---|---|---|---|")
foreach ($row in $rows) {
    [void]$table.AppendLine("| " + $row.Request + " | " + $row.Offline + " | " + $row.Model + " | " + $row.Seconds + " | " + $row.Note + " |")
}
Write-Host $table.ToString()
if ($OutFile.Length -gt 0) {
    [IO.File]::WriteAllText($OutFile, ($rows | ConvertTo-Json -Depth 4))
}
