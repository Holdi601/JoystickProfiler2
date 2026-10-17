# Writes docs/games/support.md, the game support page, from the engine's code and docs/games/support-notes.json:
#   powershell -ExecutionPolicy Bypass -File tools\game_support.ps1 [-CheckOnly] [-Configuration Debug] [-Platform x64] [-EngineExePath <engine.exe>]
#
# The page comes from "engine games support --markdown --notes docs/games/support-notes.json": the feature marks from
# the game integrations (interfaces, axis and settings models, watcher, telemetry, vJoy/Xbox modes, input limits,
# function tables, cockpit database), the verification results and footnotes from the notes.
# -CheckOnly   writes nothing; fails when the page is out of date or the notes do not match the code (a registered
#              game without an entry, an unknown game or cell, a note that claims more than the code, a partial mark
#              without a reason). The engine smoke test runs it.
param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$EngineExePath = "",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$notes = Join-Path $root "docs\games\support-notes.json"
$page = Join-Path $root "docs\games\support.md"

if ([string]::IsNullOrWhiteSpace($EngineExePath)) {
    $EngineExePath = Join-Path $root ("engine\bin\" + $Platform + "\" + $Configuration + "\engine.exe")
    if (-not (Test-Path $EngineExePath)) {
        Write-Host "Building the engine ($Configuration|$Platform)..."
        dotnet build (Join-Path $root "engine\engine.csproj") -c $Configuration | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    }
}
if (-not (Test-Path $EngineExePath)) { throw "engine.exe not found: $EngineExePath" }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("joypro-support-" + [Guid]::NewGuid().ToString("N") + ".md")
try {
    & $EngineExePath games support --markdown --notes $notes --out $temp | Out-Null
    $exit = $LASTEXITCODE
    if (-not (Test-Path $temp)) { throw "engine games support wrote no page (exit code $exit)." }
    if ($exit -ne 0) { throw "The notes do not match the code (engine games support exit code $exit; the reasons are printed above)." }
    $content = [System.IO.File]::ReadAllText($temp, [System.Text.Encoding]::UTF8)
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $temp
}

if ($CheckOnly) {
    if (-not (Test-Path $page)) { throw "Missing: $page. Run tools/game_support.ps1." }
    $existing = [System.IO.File]::ReadAllText($page, [System.Text.Encoding]::UTF8)
    if (($existing -replace "`r`n", "`n").TrimEnd("`n") -ne ($content -replace "`r`n", "`n").TrimEnd("`n")) {
        throw "docs/games/support.md is out of date. Run tools/game_support.ps1."
    }
    Write-Host "Game support page is up to date: $page"
    exit 0
}

[System.IO.File]::WriteAllText($page, $content, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $page"
