# One command to build JoyPro2 and put it where you want it (no questions asked):
#
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1                         build into E:\JoyPro2 and start it
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Target D:\Games\JoyPro2  another folder
#   ... -NoStart              only build
#   ... -Release              the folder holds only what a release ships (no game data: the first start builds it)
#   ... -Package              also build the release package (zip + .sha256) for the self-updater
#   ... -PackageDir E:\JoyPro2-Packages   where the package goes (default: <repository>\artifacts)
#   ... -Debug                Debug instead of Release build
#
# Or double-click Deploy-JoyPro2.cmd in the repository root (same options, e.g. Deploy-JoyPro2.cmd -Target E:\Test).
# JoyPro programs started from the target folder (GUI, tray watcher, FFB and vJoy hosts) are closed first, because their
# files are replaced. Your profiles, settings and game lists live outside the program folder and are kept.
param(
    [string]$Target = "E:\JoyPro2",
    [switch]$NoStart,
    [switch]$Release,
    [switch]$Package,
    [string]$PackageDir = "",
    [switch]$Debug
)

$ErrorActionPreference = "Stop"
$configuration = if ($Debug) { "Debug" } else { "Release" }
$targetFull = [System.IO.Path]::GetFullPath($Target)
$started = [Diagnostics.Stopwatch]::StartNew()

# Close every JoyPro program running from the target folder (build_local.ps1 only closes the GUI).
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like "JoyPro*" -or $_.ProcessName -eq "engine"
    } | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
foreach ($process in $running) {
    Write-Host ("Closing " + $process.ProcessName + " (" + $process.Id + ") from " + $targetFull)
    $process | Stop-Process -Force
}
if ($running.Count -gt 0) { Start-Sleep -Milliseconds 800 }

$buildArgs = @{ OutDir = $targetFull; Configuration = $configuration; NoStart = $true }
if ($Release) { $buildArgs.ReleaseData = $true }
& (Join-Path $PSScriptRoot "build_local.ps1") @buildArgs

if ($Package) {
    $packageArgs = @{ Configuration = $configuration }
    if (-not [string]::IsNullOrWhiteSpace($PackageDir)) { $packageArgs.OutDir = [System.IO.Path]::GetFullPath($PackageDir) }
    & (Join-Path $PSScriptRoot "publish_release.ps1") @packageArgs
}

$exe = Join-Path $targetFull "JoyPro.Gui.exe"
Write-Host ("JoyPro2 is ready in " + $targetFull + " (" + [int]$started.Elapsed.TotalSeconds + " s).")
if (-not $NoStart) {
    Start-Process -FilePath $exe -WorkingDirectory $targetFull
}
