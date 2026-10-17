# Builds JoyPro2 (GUI + engine) into a test folder outside the repository and starts it:
#   powershell -ExecutionPolicy Bypass -File tools\build_local.ps1            (build + start)
#   powershell -ExecutionPolicy Bypass -File tools\build_local.ps1 -NoStart   (build only)
#   ... -ReleaseData   the test copy holds only what a release ships: no game data (command lists, DCS clean profiles;
#                      also deletes those the test copy built itself), so the first start builds them like a fresh install
# The test copy runs from its own folder, so development builds in the repository do not stop it, and a cloud-sync
# client (OneDrive and the like) does not lock its files. Needs the .NET 9 SDK and Visual Studio 2022 (MSBuild, .NET Framework 4.8) for the engine.
param(
    [string]$OutDir = "C:\JoyPro2-Local",
    [string]$Configuration = "Release",
    [switch]$NoStart,
    [switch]$ReleaseData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $OutDir "JoyPro.Gui.exe"

# The test copy itself must be closed: its files are replaced.
$running = Get-Process -Name "JoyPro.Gui" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith([System.IO.Path]::GetFullPath($OutDir), [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Closing the running test copy in $OutDir"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Write-Host "Building JoyPro2 ($Configuration) into $OutDir"
& dotnet publish (Join-Path $root "gui\JoyPro.Gui\JoyPro.Gui.csproj") -c $Configuration -r win-x64 --self-contained false -o $OutDir -p:AutoStopRunningJoyProGuiOnBuild=false
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
foreach ($required in @("JoyPro.Gui.exe", "engine\engine.exe", "engine\DB")) {
    if (-not (Test-Path (Join-Path $OutDir $required))) { throw "The build misses $required." }
}
if ($ReleaseData) {
    Import-Module (Join-Path $PSScriptRoot "ReleaseContent.psm1") -Force -DisableNameChecking
    $pruned = Remove-JoyProGameData -EngineDir (Join-Path $OutDir "engine")
    foreach ($item in $pruned.Removed) { Write-Host "Removed like a release: engine\$item" }
}

Write-Host "Done: $exe"
if (-not $NoStart) {
    Start-Process -FilePath $exe -WorkingDirectory $OutDir
}
