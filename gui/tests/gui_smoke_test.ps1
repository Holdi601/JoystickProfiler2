param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$guiRoot = Split-Path -Parent $scriptRoot
$repoRoot = Split-Path -Parent $guiRoot

Push-Location $repoRoot
try {
    Write-Host "[gui-smoke] Running JoyPro GUI smoke tests ($Configuration)..." -ForegroundColor Cyan
    dotnet test "gui/JoyPro.Gui.Tests/JoyPro.Gui.Tests.csproj" -c $Configuration -v minimal
}
finally {
    Pop-Location
}

