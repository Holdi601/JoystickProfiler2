# The quality gate (docs/testing.md "Quality gate"): invariant checks on real data through the real view model and the
# real engine (gui/JoyPro.Gui.Tests/QualityGate). Builds, runs the gate and prints where the report is.
#   powershell -ExecutionPolicy Bypass -File tools\quality_gate.ps1 -Profile <copy-source.pr0file> [-Out <folder>]
#     -Data user|shipped   user (default): a copy of the game lists of this PC (%LOCALAPPDATA%\JoyPro\GameData, or -GameData)
#                          shipped: only what the program folder ships (a CI-like run)
#     -Profile             the profile the checks work on (always a copy; without it the profile checks are skipped)
#     -Only I1,I5          only these invariants (I1..I11)
#     -Planes / -Relations  limit the DCS aircraft / profile relations opened (default: all)
#     -Configuration       Release (default) or Debug; -NoBuild skips the build
# Exit code 0 = no findings; 1 = findings (see <Out>\report.md, findings.json, timings.csv, engine-calls.csv).
# The gate writes nothing outside -Out: game folders are only read, exports go to copies or run as dry runs, and it
# lays pages out in roots that are never shown (no window on the desktop).
param(
    [Alias("Profile")][string]$ProfileFile = "",
    [string]$GameData = "",
    [ValidateSet("user", "shipped")][string]$Data = "user",
    [string]$Out = "",
    [string[]]$Only = @(),
    [string]$Planes = "",
    [string]$Relations = "",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path ([System.IO.Path]::GetTempPath()) ("JoyPro2-quality-gate-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $NoBuild) {
    Write-Host "Building GUI tests and engine ($Configuration)"
    & dotnet build (Join-Path $root "gui\JoyPro.Gui.Tests") -c $Configuration -p:AutoStopRunningJoyProGuiOnBuild=false -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
}

$env:JOYPRO_QUALITY_GATE = "1"
$env:JOYPRO_QUALITY_GATE_OUT = $Out
$env:JOYPRO_QUALITY_GATE_DATA = $Data
if ($GameData) { $env:JOYPRO_QUALITY_GATE_GAMEDATA = $GameData }
if ($ProfileFile) {
    if (-not (Test-Path $ProfileFile)) { throw "Profile not found: $ProfileFile" }
    $env:JOYPRO_QUALITY_GATE_PROFILE = (Resolve-Path $ProfileFile).Path
}
if ($Planes) { $env:JOYPRO_QUALITY_GATE_PLANES = $Planes }
if ($Relations) { $env:JOYPRO_QUALITY_GATE_RELATIONS = $Relations }

$filter = "FullyQualifiedName~JoyPro.Gui.Tests.QualityGate"
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_.Trim() })
if ($Only.Count -gt 0) {
    $filter = ($Only | ForEach-Object { "(FullyQualifiedName~QualityGate&FullyQualifiedName~." + $_.Trim() + "_)" }) -join "|"
}
Write-Host "Quality gate: data $Data, profile '$ProfileFile', output $Out"
& dotnet test (Join-Path $root "gui\JoyPro.Gui.Tests") -c $Configuration --no-build --filter $filter --logger "trx;LogFileName=quality-gate.trx" --results-directory $Out
$code = $LASTEXITCODE
$report = Join-Path $Out "report.md"
if (Test-Path $report) {
    Write-Host ""
    Get-Content $report | Where-Object { $_ -match "^(- Findings|## I)" } | ForEach-Object { Write-Host $_ }
    Write-Host "Report: $report"
}
exit $code
