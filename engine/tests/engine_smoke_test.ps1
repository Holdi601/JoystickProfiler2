param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Match {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if (-not ($Text -match $Pattern)) {
        throw $Message
    }
}

function Assert-NotMatch {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Message
    )

    if ($Text -match $Pattern) {
        throw $Message
    }
}

function Assert-SetEquivalent {
    param(
        [object[]]$Left,
        [object[]]$Right,
        [string]$Message
    )

    $leftValues = @($Left | Where-Object { $null -ne $_ } | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_.Length -gt 0 } | Sort-Object -Unique)
    $rightValues = @($Right | Where-Object { $null -ne $_ } | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_.Length -gt 0 } | Sort-Object -Unique)

    if ($leftValues.Count -ne $rightValues.Count) {
        throw ($Message + " (count mismatch: left=" + $leftValues.Count + ", right=" + $rightValues.Count + ")")
    }

    for ($i = 0; $i -lt $leftValues.Count; $i++) {
        if (-not [string]::Equals($leftValues[$i], $rightValues[$i], [System.StringComparison]::Ordinal)) {
            throw ($Message + " (difference at index " + $i + ": left='" + $leftValues[$i] + "', right='" + $rightValues[$i] + "')")
        }
    }
}

function Run-Engine {
    param(
        [string[]]$EngineArgs
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & $script:EngineExe @EngineArgs 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($null -eq $output) {
        $text = ""
    }
    elseif ($output -is [System.Array]) {
        $text = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    }
    else {
        $text = $output.ToString()
    }
    return [PSCustomObject]@{
        ExitCode = $exitCode
        Output = $text
    }
}

$engineDir = Split-Path -Parent $PSScriptRoot
$engineProject = Join-Path $engineDir "engine.csproj"

$msbuildCandidates = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)

$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
Assert-True ($null -ne $msbuild) "MSBuild.exe not found."

Write-Host "Building engine ($Configuration|$Platform)..."
& $msbuild $engineProject /m /p:Configuration=$Configuration /p:Platform=$Platform | Out-Host
Assert-True ($LASTEXITCODE -eq 0) "MSBuild failed."

$engineBin = Join-Path $engineDir ("bin\" + $Platform + "\" + $Configuration)
$script:EngineExe = Join-Path $engineBin "engine.exe"
Assert-True (Test-Path $script:EngineExe) "engine.exe not found at: $script:EngineExe"

# The development copies of the game lists (EngineOnly_Runtime\DB\<game>, CleanProfile, KeyboardCleanProfile) are
# extracted from installed games and live only in the maintainer's private repository; a public checkout has none
# (JoyPro builds them on the user's PC). Without them the parts that read a real game catalog are skipped, each with a
# message, and the rest runs as usual.
$script:HasDevGameData = Test-Path (Join-Path $engineBin "DB\DCS")
$script:SkippedForGameData = New-Object System.Collections.Generic.List[string]
function Skip-WithoutGameData {
    param([string]$Part)
    if ($script:HasDevGameData) { return $false }
    $script:SkippedForGameData.Add($Part)
    Write-Host ("SKIP (needs the development game lists): " + $Part)
    return $true
}
if (-not $script:HasDevGameData) {
    Write-Host "No development game lists in $engineBin\DB (public checkout): checks against real game catalogs are skipped."
}

# The background logs of every engine and JoyPro.Ffb run below (docs/logging.md) go into a temp folder of their own,
# never into the user's %LOCALAPPDATA%\JoyPro2\Logs and not into the JOYPRO_DATA_DIR folders the parts compare. The
# folder is removed when the test passes and kept (path printed) when it fails.
$previousSmokeLogDir = $env:JOYPRO_LOG_DIR
$smokeLogDir = Join-Path ([System.IO.Path]::GetTempPath()) ("engine-smoke-logs-" + [Guid]::NewGuid().ToString("N"))
$env:JOYPRO_LOG_DIR = $smokeLogDir
Write-Host "Engine logs of this run: $smokeLogDir"
# No game cover is downloaded from the internet (smoke\gamecovers.ps1 uses a loopback fake server instead).
$previousNoInternetPictures = $env:JOYPRO_NO_INTERNET_PICTURES
$env:JOYPRO_NO_INTERNET_PICTURES = "1"
# Every engine run below has a temporary JoyPro data folder (game data, backups, ownership, layouts), never the user's
# %LOCALAPPDATA%\JoyPro: the engine copies the program folder's game lists into <data>\GameData on its first start.
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("engine-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -Path $tempDir -ItemType Directory | Out-Null
$previousSmokeDataDir = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $tempDir "joypro-data"
# The test sandbox (docs/testing.md "Test sandbox"): every engine below refuses writes outside temp, its data folder and
# this root (exit code 77, stderr, <root>\sandbox-violations.log), so no part can reach a real game or user folder.
$previousSandbox = $env:JOYPRO_TEST_SANDBOX
$smokeSandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("engine-smoke-sandbox-" + [Guid]::NewGuid().ToString("N"))
New-Item -Path $smokeSandbox -ItemType Directory | Out-Null
$env:JOYPRO_TEST_SANDBOX = $smokeSandbox

Write-Host "Running CLI smoke tests..."

$help = Run-Engine @("--help")
Assert-True ($help.ExitCode -eq 0) "engine --help failed."
Assert-True ($help.Output -match "engine - JoyPro CLI engine") "Help output missing banner."
Assert-True ($help.Output -match "--load-corrections") "Help output missing --load-corrections option."
Assert-True ($help.Output -match "--status true\|false") "Help output missing --status option."
Assert-True ($help.Output -match "--benchmark true\|false") "Help output missing --benchmark option."
Assert-True ($help.Output -match "--auto-add-db-items") "Help output missing --auto-add-db-items option."
Assert-True ($help.Output -match "--remap-missing-devices") "Help output missing --remap-missing-devices option."
Assert-True ($help.Output -match "--confirm-device-remap") "Help output missing --confirm-device-remap option."
Assert-True ($help.Output -match "--correction-mode") "Help output missing --correction-mode option."
Assert-True ($help.Output -match "--correction-auto-threshold") "Help output missing --correction-auto-threshold option."
Assert-True ($help.Output -match "--confirm-corrections") "Help output missing --confirm-corrections option."
Assert-True ($help.Output -match "--correction-report") "Help output missing --correction-report option."
Assert-True ($help.Output -match "relations group list") "Help output missing relations group list command."
Assert-True ($help.Output -match "relations group rename") "Help output missing relations group rename command."
Assert-True ($help.Output -match "relations mass delete") "Help output missing relations mass delete command."
Assert-True ($help.Output -match "relations mass merge") "Help output missing relations mass merge command."
Assert-True ($help.Output -match "relations mass split") "Help output missing relations mass split command."
Assert-True ($help.Output -match "relations mass export") "Help output missing relations mass export command."
Assert-True ($help.Output -match "binds modifier list") "Help output missing binds modifier list command."
Assert-True ($help.Output -match "binds modifier rename") "Help output missing binds modifier rename command."
Assert-True ($help.Output -match "binds set") "Help output missing binds set command."
Assert-True ($help.Output -match "binds clear") "Help output missing binds clear command."
Assert-True ($help.Output -match "engine games paths") "Help output missing games paths command."
Assert-True ($help.Output -match "--keep-keyboard-defaults") "Help output missing --keep-keyboard-defaults option."
Assert-True ($help.Output -match "--nuke-untouched-devices") "Help output missing --nuke-untouched-devices option."
Assert-True ($help.Output -match "engine dcs defaults cut") "Help output missing dcs defaults cut command."
Assert-True ($help.Output -match "engine dcs defaults restore") "Help output missing dcs defaults restore command."
Assert-True ($help.Output -match "engine dcs refresh clean") "Help output missing dcs refresh clean command."
Assert-True ($help.Output -match "engine dcs refresh id") "Help output missing dcs refresh id command."
Assert-True ($help.Output -match "engine dcs refresh preview-clicks") "Help output missing dcs refresh preview-clicks command."
Assert-True ($help.Output -match "--install-path") "Help output missing dcs defaults --install-path option."
Assert-True ($help.Output -match "--backup-path") "Help output missing dcs defaults --backup-path option."
Assert-True ($help.Output -match "--clicks-xml") "Help output missing dcs refresh --clicks-xml option."
Assert-True ($help.Output -match "--clicks-dir") "Help output missing dcs refresh --clicks-dir option."
Assert-True ($help.Output -match "--preview-image") "Help output missing dcs refresh --preview-image option."
Assert-True ($help.Output -match "--modules-to-scan") "Help output missing dcs refresh --modules-to-scan option."
Assert-True ($help.Output -match "--menu-wait-seconds") "Help output missing dcs refresh --menu-wait-seconds option."
Assert-True ($help.Output -match "--headless-lua") "Help output missing dcs refresh --headless-lua option."
Assert-True ($help.Output -match "dcs refresh clean .*--headless-lua true\|false") "Help output missing dcs refresh clean --headless-lua option."
Assert-True ($help.Output -match "dcs refresh id .*--plane <name>") "Help output missing dcs refresh --plane option."
Assert-True ($help.Output -match "engine dcs ffb list") "Help output missing dcs ffb list command."
Assert-True ($help.Output -match "engine dcs ffb set") "Help output missing dcs ffb set command."
Assert-True ($help.Output -match "engine dcs ffb clear") "Help output missing dcs ffb clear command."
Assert-True ($help.Output -match "--invert-x") "Help output missing dcs ffb --invert-x option."
Assert-True ($help.Output -match "--swap-axis") "Help output missing dcs ffb --swap-axis option."
Assert-True ($help.Output -match "engine devices inputs") "Help output missing devices inputs command."
Assert-True ($help.Output -match "engine devices exchange") "Help output missing devices exchange command."
Assert-True ($help.Output -match "engine devices remove") "Help output missing devices remove command."
Assert-True ($help.Output -match "engine starcitizen scan") "Help output missing starcitizen scan command."
Assert-True ($help.Output -match "engine starcitizen import") "Help output missing starcitizen import command."
Assert-True ($help.Output -match "engine starcitizen export") "Help output missing starcitizen export command."
Assert-True ($help.Output -match "engine starcitizen list-devices") "Help output missing starcitizen list-devices command."
Assert-True ($help.Output -match "engine profile merge") "Help output missing profile merge command."
Assert-True ($help.Output -match "profile merge .*--mode left\|right\|ask") "Help output missing profile merge mode documentation."
Assert-True ($help.Output -match "engine profile validate") "Help output missing profile validate command."
Assert-True ($help.Output -match "profile validate .*--fail-on-warnings true\|false") "Help output missing profile validate --fail-on-warnings option."
Assert-True ($help.Output -match "engine db fingerprint") "Help output missing db fingerprint command."
Assert-True ($help.Output -match "engine layout render") "Help output missing layout render command."
Assert-True ($help.Output -match "--layout-map") "Help output missing --layout-map option."

$summary = Run-Engine @("summary")
Assert-True ($summary.ExitCode -eq 0) "engine summary failed."
Assert-True ($summary.Output -match "relations=\d+.*binds=\d+.*modifiers=\d+") "Summary output has unexpected format."

$summaryWithStatus = Run-Engine @("--status", "true", "summary")
Assert-True ($summaryWithStatus.ExitCode -eq 0) "engine --status true summary failed."
Assert-True ($summaryWithStatus.Output -match "\[engine-status\]") "status output missing [engine-status] markers."

$summaryWithBenchmark = Run-Engine @("--benchmark", "true", "summary")
Assert-True ($summaryWithBenchmark.ExitCode -eq 0) "engine --benchmark true summary failed."
Assert-True ($summaryWithBenchmark.Output -match "\[engine-benchmark\]") "benchmark output missing [engine-benchmark] markers."

$summaryNoCorrections = Run-Engine @("--load-corrections", "false", "summary")
Assert-True ($summaryNoCorrections.ExitCode -eq 0) "engine --load-corrections false summary failed."

$summaryNoRemap = Run-Engine @("--remap-missing-devices", "false", "summary")
Assert-True ($summaryNoRemap.ExitCode -eq 0) "engine --remap-missing-devices false summary failed."

$summaryStrictMode = Run-Engine @("--correction-mode", "strict", "--confirm-corrections", "false", "summary")
Assert-True ($summaryStrictMode.ExitCode -eq 0) "engine --correction-mode strict --confirm-corrections false summary failed."

$summaryThreshold = Run-Engine @("--correction-auto-threshold", "98", "summary")
Assert-True ($summaryThreshold.ExitCode -eq 0) "engine --correction-auto-threshold 98 summary failed."

$correctionReportPath = Join-Path ([System.IO.Path]::GetTempPath()) ("engine-corrections-" + [Guid]::NewGuid().ToString("N") + ".json")
try {
    $summaryWithReport = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--correction-report", $correctionReportPath, "summary")
    Assert-True ($summaryWithReport.ExitCode -eq 0) "engine --correction-report summary failed."
    Assert-True (Test-Path $correctionReportPath) "correction report file not created."
    $correctionReportContent = Get-Content -Raw $correctionReportPath
    Assert-True ($correctionReportContent -match '"Entries"') "correction report output missing Entries payload."
}
finally {
    if (Test-Path $correctionReportPath) {
        Remove-Item -Path $correctionReportPath -Force
    }
}

$relationsList = Run-Engine @("relations", "list", "--text")
Assert-True ($relationsList.ExitCode -eq 0) "engine relations list --text failed."
Assert-True ($relationsList.Output -match "(?m)^Relations:\s+\d+") "Relations list output has unexpected format."

$bindsList = Run-Engine @("binds", "list", "--text")
Assert-True ($bindsList.ExitCode -eq 0) "engine binds list --text failed."
Assert-True ($bindsList.Output -match "(?m)^Binds:\s+\d+") "Binds list output has unexpected format."

$dbSummary = Run-Engine @("db", "summary", "--text")
Assert-True ($dbSummary.ExitCode -eq 0) "engine db summary --text failed."
Assert-True ($dbSummary.Output -match "(?m)^DB Check:\s+DCS") "DB summary output missing DCS header."

$dbGames = Run-Engine @("db", "games", "--text")
Assert-True ($dbGames.ExitCode -eq 0) "engine db games --text failed."
if (-not (Skip-WithoutGameData "db games lists the DCS catalog")) {
Assert-True ($dbGames.Output -match "(?m)^DB Games:\s+[1-9]\d*") "DB games output has unexpected format."
Assert-True ($dbGames.Output -match "(?m)^DCS\s+\|") "DB games output missing DCS entry."
}

$gamePaths = Run-Engine @("games", "paths", "--text")
Assert-True ($gamePaths.ExitCode -eq 0) "engine games paths --text failed."
Assert-True ($gamePaths.Output -match "(?m)^Game Paths\s*$") "games paths output missing header."
Assert-True ($gamePaths.Output -match "(?m)^DCS\s*$") "games paths output missing DCS section."
Assert-True ($gamePaths.Output -match "(?m)^IL2Game\s*$") "games paths output missing IL2Game section."
Assert-True ($gamePaths.Output -match "(?m)^StarCitizen\s*$") "games paths output missing StarCitizen section."

if (-not (Skip-WithoutGameData "db planes (DCS, IL-2) and db check against the real catalogs")) {
$dbPlanesDcs = Run-Engine @("db", "planes", "--game", "DCS", "--text")
Assert-True ($dbPlanesDcs.ExitCode -eq 0) "engine db planes --game DCS --text failed."
Assert-True ($dbPlanesDcs.Output -match "(?m)^DB Planes:\s+[1-9]\d*") "DB planes output has unexpected format."
Assert-True ($dbPlanesDcs.Output -match "(?m)^DCS\s+\(") "DB planes output missing DCS section."
Assert-True ($dbPlanesDcs.Output -match "(?m)^\s+-\s+") "DB planes output missing plane entries."

$dbPlanesIl2 = Run-Engine @("db", "planes", "--game", "IL2Game", "--text")
Assert-True ($dbPlanesIl2.ExitCode -eq 0) "engine db planes --game IL2Game --text failed."
Assert-True ($dbPlanesIl2.Output -match "(?m)^IL2Game\s+\(") "DB planes output missing IL2Game section."

$dbCheck = Run-Engine @("db", "check", "--text")
Assert-True ($dbCheck.ExitCode -eq 0) "engine db check --text failed."
Assert-True ($dbCheck.Output -match "db check ok") "DB check output missing success marker."
}

$dbFingerprint = Run-Engine @("db", "fingerprint", "--text")
Assert-True ($dbFingerprint.ExitCode -eq 0) "engine db fingerprint --text failed."
Assert-True ($dbFingerprint.Output -match "(?m)^DB Fingerprint:\s+sha256:[0-9a-f]{64}\s*$") "db fingerprint output missing digest."

$devicesInputs = Run-Engine @("devices", "inputs", "--text")
Assert-True ($devicesInputs.ExitCode -eq 0) "engine devices inputs --text failed."
Assert-True ($devicesInputs.Output -match "(?m)^Device Input Catalog\s*$") "devices inputs output missing header."
Assert-True ($devicesInputs.Output -match "(?m)^Joystick Axes:\s+8\s*$") "devices inputs output missing joystick axis count."
Assert-True ($devicesInputs.Output -match "(?m)^Joystick Buttons:\s+128\s*$") "devices inputs output missing joystick button count."
Assert-True ($devicesInputs.Output -match "(?m)^\s+-\s+JOY_X\s*$") "devices inputs output missing JOY_X token."
Assert-True ($devicesInputs.Output -match "(?m)^\s+-\s+JOY_BTN128\s*$") "devices inputs output missing JOY_BTN128 token."
Assert-True ($devicesInputs.Output -match "(?m)^Keyboard Keys \(DCS\):\s+[1-9]\d*\s*$") "devices inputs output missing DCS keyboard section."
Assert-True ($devicesInputs.Output -match "(?m)^Keyboard Keys \(IL2 format, use with --format il2\):\s+[1-9]\d*\s*$") "devices inputs output missing IL2 keyboard section."

$devicesInputsOff = Run-Engine @("devices", "inputs", "--include-off", "--text")
Assert-True ($devicesInputsOff.ExitCode -eq 0) "engine devices inputs --include-off --text failed."
Assert-True ($devicesInputsOff.Output -match "(?m)^Joystick _OFF Variants:\s+[1-9]\d*\s*$") "devices inputs --include-off output missing _OFF section."
Assert-True ($devicesInputsOff.Output -match "(?m)^\s+-\s+JOY_BTN1_OFF\s*$") "devices inputs --include-off output missing JOY_BTN1_OFF token."

$devicesConnected = Run-Engine @("devices", "list", "--connected")
Assert-True ($devicesConnected.ExitCode -eq 0) "engine devices list --connected failed."
$devicesConnectedJson = ConvertFrom-Json $devicesConnected.Output
Assert-True ($null -ne $devicesConnectedJson.Connected) "engine devices list --connected did not return Connected list."

$starCitizenListAuto = Run-Engine @("starcitizen", "list-devices", "--text")
Assert-True ($starCitizenListAuto.ExitCode -eq 0) "engine starcitizen list-devices --text failed."
Assert-True ($starCitizenListAuto.Output -match "(?m)^Star Citizen Devices\s*$") "starcitizen list-devices output missing header."
Assert-True ($starCitizenListAuto.Output -match "(?m)^Devices:\s+[1-9]\d*\s*$") "starcitizen list-devices output missing device count."
Assert-True ($starCitizenListAuto.Output -match "(?m)^\s+-\s+Keyboard\s*$") "starcitizen list-devices output missing keyboard fallback."

# The game exports below write temp copies; they must not fail because the developer is playing one of the games.
$previousIgnoreRunningGames = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
$env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = "1"
# The exports below write temp copies of every game's files: every game counts as installed (a section below tests
# the refusal for games that are not).
$previousAssumeInstalled = $env:JOYPRO_TEST_ASSUME_INSTALLED
$env:JOYPRO_TEST_ASSUME_INSTALLED = "1"

try {
    $relationsJson = Join-Path $tempDir "relations.json"
    $bindsJson = Join-Path $tempDir "binds.json"
    $savedGames = Join-Path $tempDir "saved-games"
    $savedPlaneDir = Join-Path $savedGames "Config\\Input\\F-16C_50"
    $savedInput = Join-Path $savedGames "Config\\Input\\F-16C_50\\joystick"
    $savedDiff = Join-Path $savedInput "Test Device.diff.lua"
    $savedBackup = Join-Path $savedInput "Test Device.jp"
    $savedModifiers = Join-Path $savedPlaneDir "modifiers.lua"
    $importProfile = Join-Path $tempDir "imported_from_dcs.pr0file"
    $importedRelationsJson = Join-Path $tempDir "imported_relations.json"
    $importedBindsJson = Join-Path $tempDir "imported_binds.json"
    $savedExport = Join-Path $tempDir "saved-games-export"
    $dcsDefaultsSavedGames = Join-Path $tempDir "dcs-defaults-saved-games"
    $dcsDefaultsInstall = Join-Path $tempDir "dcs-install"
    $dcsDefaultsJoystick = Join-Path $dcsDefaultsInstall "Mods\\aircraft\\A-10C\\Input\\A-10C\\joystick"
    $dcsDefaultsDefaultFile = Join-Path $dcsDefaultsJoystick "default.lua"
    $dcsDefaultsCustomFile = Join-Path $dcsDefaultsJoystick "vkb_custom.lua"
    $dcsDefaultsBackupDefaultFile = Join-Path $dcsDefaultsSavedGames "BACKUP_JOYSTICK_DEFAULTS\\A-10C\\Input\\A-10C\\joystick\\default.lua"
    $dcsDefaultsBackupCustomFile = Join-Path $dcsDefaultsSavedGames "BACKUP_JOYSTICK_DEFAULTS\\A-10C\\Input\\A-10C\\joystick\\vkb_custom.lua"
    $dcsRefreshPreviewImage = Join-Path $tempDir "dcs_refresh_preview.png"
    $legacyProfileJson = Join-Path $tempDir "legacy_profile.json"
    $legacyCorrectionReport = Join-Path $tempDir "legacy_corrections.json"
    $fingerprintedProfile = Join-Path $tempDir "fingerprinted_profile.pr0file"
    $fingerprintMatchReport = Join-Path $tempDir "fingerprint_match_report.json"
    $layoutSmokeImage = Join-Path $tempDir "layout_smoke.png"
    $layoutSmokeOut = Join-Path $tempDir "layout_render"
    $starCitizenRoot = Join-Path $tempDir "starcitizen"
    $starCitizenProfileDir = Join-Path $starCitizenRoot "LIVE\\USER\\Client\\0\\Profiles\\default"
    $starCitizenActionmaps = Join-Path $starCitizenProfileDir "actionmaps.xml"
    $starCitizenImportProfile = Join-Path $tempDir "starcitizen_import.pr0file"
    $starCitizenExportRoot = Join-Path $tempDir "starcitizen_export"
    $starCitizenExportProfileDir = Join-Path $starCitizenExportRoot "LIVE\\USER\\Client\\0\\Profiles\\default"
    $starCitizenExportActionmaps = Join-Path $starCitizenExportProfileDir "actionmaps.xml"
    $starCitizenExportMetadata = Join-Path $starCitizenExportProfileDir "actionmaps.jp"
    $starCitizenOrderRoot = Join-Path $tempDir "starcitizen_order"
    $starCitizenOrderProfileDir = Join-Path $starCitizenOrderRoot "LIVE\\USER\\Client\\0\\Profiles\\default"
    $starCitizenOrderActionmaps = Join-Path $starCitizenOrderProfileDir "actionmaps.xml"
    $starCitizenOrderProfile = Join-Path $tempDir "starcitizen_order_import.pr0file"
    $starCitizenOrderExportRoot = Join-Path $tempDir "starcitizen_order_export"
    $starCitizenOrderExportProfileDir = Join-Path $starCitizenOrderExportRoot "LIVE\\USER\\Client\\0\\Profiles\\default"
    $starCitizenOrderExportActionmaps = Join-Path $starCitizenOrderExportProfileDir "actionmaps.xml"
    # The engine reads game lists from the game-data folder (<data>\GameData) first: the test catalog goes there, the
    # program folder's copy stays untouched.
    $starCitizenDbDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\\DB\\StarCitizen"
    $starCitizenDbCatalogPath = Join-Path $starCitizenDbDir "AllBinds.xml"
    $starCitizenDbCatalogBackup = Join-Path $tempDir "starcitizen_db_catalog_backup.xml"
    $starCitizenDbCatalogExisted = Test-Path $starCitizenDbCatalogPath

@'
{
  "Relations": {},
  "Binds": {},
  "JoystickAliases": {},
  "JoystickFileImages": {},
  "JoystickLayoutExport": "",
  "PlaneAliases": {},
  "JoysticksPGuids": {},
  "modifierNameChanges": [],
  "JoystickFFB": {}
}
'@ | Set-Content -Path $legacyProfileJson -Encoding UTF8

    Add-Type -AssemblyName System.Drawing
    $layoutBitmap = New-Object System.Drawing.Bitmap 48, 48
    try {
        $layoutGraphics = [System.Drawing.Graphics]::FromImage($layoutBitmap)
        try {
            $layoutGraphics.Clear([System.Drawing.Color]::DarkSlateGray)
        }
        finally {
            $layoutGraphics.Dispose()
        }
        $layoutBitmap.Save($layoutSmokeImage, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $layoutBitmap.Dispose()
    }

    $layoutRender = Run-Engine @("layout", "render", "--path", $layoutSmokeOut, "--device", "SmokeStick", "--layout-map", ("SmokeStick=" + $layoutSmokeImage), "--include-planes", "false", "--include-overview", "true", "--text")
    Assert-True ($layoutRender.ExitCode -eq 0) "engine layout render smoke command failed."
    Assert-True ($layoutRender.Output -match "(?m)^Total Images Written:\s+1\s*$") "layout render output missing expected image count."
    Assert-True (Test-Path (Join-Path $layoutSmokeOut "SmokeStick.png")) "layout render output file missing."

    $layoutStyledOut = Join-Path $tempDir "layout-styled"
    $layoutStyled = Run-Engine @("layout", "render", "--path", $layoutStyledOut, "--device", "SmokeStick", "--layout-map", ("SmokeStick=" + $layoutSmokeImage), "--include-planes", "false", "--include-overview", "true", "--font", "Segoe UI", "--font-size", "14", "--text-color", "#FFFFFF", "--outline-color", "#000000", "--label-background", "#88000000", "--text")
    Assert-True ($layoutStyled.ExitCode -eq 0) "layout render with text style options failed."
    Assert-True (Test-Path (Join-Path $layoutStyledOut "SmokeStick.png")) "layout render with text style options wrote no image."

    New-Item -Path $starCitizenProfileDir -ItemType Directory -Force | Out-Null
    New-Item -Path $starCitizenExportProfileDir -ItemType Directory -Force | Out-Null
    New-Item -Path $starCitizenDbDir -ItemType Directory -Force | Out-Null

@'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard {00000000-0000-0000-0000-000000000000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Test Stick {11111111-1111-1111-1111-111111111111}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="fire"><!--Fire Weapon//false-->
    <rebind input="js1_button37"/><!--Fire Trigger//false-->
    <rebind input="kb1_lctrl+k"/><!--Fire Keyboard//false-->
   </action>
   <action name="pitch"><!--Pitch Axis//true-->
    <rebind input="js1_y"/>
   </action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@ | Set-Content -Path $starCitizenActionmaps -Encoding UTF8

    if ($starCitizenDbCatalogExisted) {
        # Restore the shipped catalog afterwards, not whatever an aborted earlier run left in bin.
        $starCitizenRepoCatalog = Join-Path (Split-Path -Parent $engineDir) "EngineOnly_Runtime\\DB\\StarCitizen\\AllBinds.xml"
        $starCitizenCatalogSource = if (Test-Path $starCitizenRepoCatalog) { $starCitizenRepoCatalog } else { $starCitizenDbCatalogPath }
        Copy-Item -Path $starCitizenCatalogSource -Destination $starCitizenDbCatalogBackup -Force
    }

@'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard {00000000-0000-0000-0000-000000000000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Test Stick {11111111-1111-1111-1111-111111111111}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="fire"><!--Fire Weapon//false--></action>
   <action name="pitch"><!--Pitch Axis//true--></action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@ | Set-Content -Path $starCitizenDbCatalogPath -Encoding UTF8

    $starCitizenScan = Run-Engine @("starcitizen", "scan", $starCitizenRoot, "--text")
    Assert-True ($starCitizenScan.ExitCode -eq 0) "engine starcitizen scan failed."
    Assert-True ($starCitizenScan.Output -match "(?m)^Actions Parsed:\s+[1-9]\d*\s*$") "starcitizen scan output missing parsed action count."
    Assert-True ($starCitizenScan.Output -match "(?m)^Candidates Parsed:\s+[1-9]\d*\s*$") "starcitizen scan output missing parsed candidate count."
    Assert-True ($starCitizenScan.Output -match "(?m)^Matched Devices:\s+.*Test Stick") "starcitizen scan output missing resolved device."

    $starCitizenList = Run-Engine @("sc", "list-devices", $starCitizenRoot, "--text")
    Assert-True ($starCitizenList.ExitCode -eq 0) "engine sc list-devices failed."
    Assert-True ($starCitizenList.Output -match "(?m)^\s+-\s+Keyboard\s*$") "sc list-devices output missing keyboard."
    Assert-True ($starCitizenList.Output -match "(?m)^\s+-\s+Test Stick") "sc list-devices output missing joystick device."

    $starCitizenImport = Run-Engine @("--profile", $starCitizenImportProfile, "starcitizen", "import", $starCitizenRoot, "--merge", "--include-keyboard", "true", "--text")
    Assert-True ($starCitizenImport.ExitCode -eq 0) "engine starcitizen import failed."
    Assert-True (Test-Path $starCitizenImportProfile) "engine starcitizen import did not save profile."
    Assert-True ($starCitizenImport.Output -match "(?m)^Imported Relations:\s+\d+\s*$") "starcitizen import output missing imported relation count."
    Assert-True ($starCitizenImport.Output -match "(?m)^Imported Binds:\s+\d+\s*$") "starcitizen import output missing imported bind count."
    Assert-True ($starCitizenImport.Output -match "(?m)^Merged Into Existing:\s+\d+\s*$") "starcitizen import output missing merge count."
    $starCitizenImportCount = [int]([regex]::Match($starCitizenImport.Output, "(?m)^Imported Relations:\s+(\d+)\s*$").Groups[1].Value)
    $starCitizenMergedCount = [int]([regex]::Match($starCitizenImport.Output, "(?m)^Merged Into Existing:\s+(\d+)\s*$").Groups[1].Value)
    Assert-True (($starCitizenImportCount + $starCitizenMergedCount) -ge 1) "starcitizen import applied no entries (neither imported nor merged)."

    $starCitizenExport = Run-Engine @("--profile", $starCitizenImportProfile, "starcitizen", "export", $starCitizenExportRoot, "--mode", "merge-overwrite", "--prefer-local-order", "true", "--keep-keyboard-defaults", "true", "--include-keyboard", "true", "--text")
    Assert-True ($starCitizenExport.ExitCode -eq 0) "engine starcitizen export failed."
    Assert-True ($starCitizenExport.Output -match "(?m)^Mode:\s+merge-overwrite\s*$") "starcitizen export output missing mode."
    Assert-True ($starCitizenExport.Output -match "(?m)^Files Written:\s+2\s*$") "starcitizen export output missing written-file count."
    Assert-True (Test-Path $starCitizenExportActionmaps) "starcitizen export did not write actionmaps.xml."
    Assert-True (Test-Path $starCitizenExportMetadata) "starcitizen export did not write actionmaps.jp."

    $starCitizenExportXml = Get-Content -Raw $starCitizenExportActionmaps
    Assert-Match $starCitizenExportXml 'input="kb1_lctrl\+k"' "starcitizen export did not write expected keyboard token."
    # --prefer-local-order keeps connected devices on their real js slots. With 8+ connected devices the
    # disconnected Test Stick cannot get a slot, which is correct behavior, so skip the slot checks there.
    if ($starCitizenExport.Output -match "Test Stick \{11111111-1111-1111-1111-111111111111\}' has no free joystick slot") {
        Write-Host "SKIP: Star Citizen Test Stick slot checks (all 8 joystick slots are taken by connected devices)."
    }
    else {
        Assert-Match $starCitizenExportXml 'Product="Test Stick \{11111111-1111-1111-1111-111111111111\}"' "starcitizen export did not keep expected joystick product mapping."
        $starCitizenTestStickSlotMatch = [regex]::Match($starCitizenExportXml, 'type="joystick"\s+instance="(\d+)"\s+Product="Test Stick \{11111111-1111-1111-1111-111111111111\}"')
        Assert-True ($starCitizenTestStickSlotMatch.Success) "starcitizen export did not include Test Stick options slot."
        $starCitizenTestStickSlot = [int]$starCitizenTestStickSlotMatch.Groups[1].Value
        Assert-Match $starCitizenExportXml ('input="js' + $starCitizenTestStickSlot + '_button37"') "starcitizen export did not write expected joystick token for Test Stick slot."
        Assert-Match $starCitizenExportXml ('input="js' + $starCitizenTestStickSlot + '_y"') "starcitizen export did not write expected axis token for Test Stick slot."
    }

    # Node IDs written by older GUI builds (bare action) and by the DB correction index (actionmap___action).
    $starCitizenIdsProfile = Join-Path $tempDir "starcitizen_ids.pr0file"
    $starCitizenIdsRoot = Join-Path $tempDir "starcitizen_ids_export"
    New-Item -Path (Join-Path $starCitizenIdsRoot "LIVE\\USER\\Client\\0\\Profiles\\default") -ItemType Directory -Force | Out-Null
    $scIdsPitch = Run-Engine @("--profile", $starCitizenIdsProfile, "relations", "add", "SC Bare Pitch", "--axis", "--node", "StarCitizen:pitch", "--device", "Test Stick {11111111-1111-1111-1111-111111111111}", "--input", "JOY_Y", "--text")
    Assert-True ($scIdsPitch.ExitCode -eq 0) "relations add with a bare Star Citizen node failed."
    $scIdsFire = Run-Engine @("--profile", $starCitizenIdsProfile, "relations", "add", "SC Index Fire", "--button", "--node", "StarCitizen:spaceship_movement___fire", "--device", "Test Stick {11111111-1111-1111-1111-111111111111}", "--input", "JOY_BTN3", "--text")
    Assert-True ($scIdsFire.ExitCode -eq 0) "relations add with a correction-index Star Citizen node failed."
    $scIdsExport = Run-Engine @("--profile", $starCitizenIdsProfile, "starcitizen", "export", $starCitizenIdsRoot, "--mode", "merge-overwrite", "--text")
    Assert-NotMatch $scIdsExport.Output "not found in catalog" "starcitizen export must resolve bare and actionmap___action node IDs."

    # The export edits actionmaps.xml in place: only ship rebinds change; mouse rebinds, activation modes, device
    # options, modifiers and on-foot actions stay byte-identical. Clean releases ship binds of the written devices,
    # nuke those of unused devices.
    $scInPlaceRoot = Join-Path $tempDir "starcitizen_inplace"
    $scInPlaceDir = Join-Path $scInPlaceRoot "LIVE\\USER\\Client\\0\\Profiles\\default"
    New-Item -Path $scInPlaceDir -ItemType Directory -Force | Out-Null
    $scInPlaceFile = Join-Path $scInPlaceDir "actionmaps.xml"
    $scInPlaceText = @'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard  {6F1D2B61-D5A0-11CF-BFC7-444553540000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Test Stick {11111111-1111-1111-1111-111111111111}">
   <flight_move_pitch invert="1"/>
  </options>
  <options type="joystick" instance="2" Product="Other Stick {22222222-2222-3333-4444-555555555555}"/>
  <modifiers>
   <mod input="js1_button5"/>
  </modifiers>
  <actionmap name="player">
   <action name="attack1">
    <rebind input="mo1_mouse1"/>
   </action>
  </actionmap>
  <actionmap name="spaceship_movement">
   <action name="pitch">
    <rebind input="js1_y"/>
   </action>
   <action name="yawx">
    <rebind input="js1_x"/>
    <rebind input="mo1_maxis_x"/>
   </action>
   <action name="rollx">
    <rebind input="js2_rotz"/>
   </action>
   <action name="strafex">
    <rebind input="kb1_space" activationMode="press"/>
   </action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@
    [System.IO.File]::WriteAllText($scInPlaceFile, $scInPlaceText.Replace("`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $scInPlaceOriginal = [System.IO.File]::ReadAllText($scInPlaceFile)
    $scInPlaceProfile = Join-Path $tempDir "starcitizen_inplace.pr0file"
    $scInPlaceAdd = Run-Engine @("--profile", $scInPlaceProfile, "relations", "add", "SC InPlace Pitch", "--axis", "--node", "StarCitizen:spaceship_movement|pitch", "--device", "Test Stick {11111111-1111-1111-1111-111111111111}", "--input", "JOY_X", "--text")
    Assert-True ($scInPlaceAdd.ExitCode -eq 0) "relations add for the in-place Star Citizen check failed."
    $scInPlaceWalk = Run-Engine @("--profile", $scInPlaceProfile, "relations", "add", "SC InPlace Walk", "--button", "--node", "StarCitizen:player|attack1", "--device", "Test Stick {11111111-1111-1111-1111-111111111111}", "--input", "JOY_BTN1", "--text")
    Assert-True ($scInPlaceWalk.ExitCode -eq 0) "relations add for an on-foot Star Citizen action failed."
    $scInPlaceExport = Run-Engine @("--profile", $scInPlaceProfile, "starcitizen", "export", $scInPlaceRoot, "--prefer-local-order", "false", "--text")
    Assert-True ($scInPlaceExport.ExitCode -eq 0) "starcitizen in-place export failed."
    Assert-Match $scInPlaceExport.Output "JoyPro only writes Star Citizen ship controls" "starcitizen export must skip on-foot actions."
    $scInPlaceAfter = [System.IO.File]::ReadAllText($scInPlaceFile)
    Assert-True ($scInPlaceAfter -eq $scInPlaceOriginal.Replace('<rebind input="js1_y"/>', '<rebind input="js1_x"/>')) "starcitizen export must change only the ship rebind JoyPro writes and keep everything else byte-identical."
    $scInPlaceAgain = Run-Engine @("--profile", $scInPlaceProfile, "starcitizen", "export", $scInPlaceRoot, "--prefer-local-order", "false", "--text")
    Assert-True ([System.IO.File]::ReadAllText($scInPlaceFile) -eq $scInPlaceAfter) "a repeated starcitizen export must not change the file."
    $scInPlaceClean = Run-Engine @("--profile", $scInPlaceProfile, "starcitizen", "export", $scInPlaceRoot, "--mode", "clean", "--prefer-local-order", "false", "--text")
    Assert-True ($scInPlaceClean.ExitCode -eq 0) "starcitizen in-place clean export failed."
    $scInPlaceCleanText = [System.IO.File]::ReadAllText($scInPlaceFile)
    Assert-NotMatch $scInPlaceCleanText '"js1_x"/>\s*<rebind input="mo1_maxis_x"' "starcitizen clean must release other ship binds of a device JoyPro writes."
    Assert-Match $scInPlaceCleanText 'mo1_maxis_x' "starcitizen clean must keep mouse rebinds."
    Assert-Match $scInPlaceCleanText 'js2_rotz' "starcitizen clean must keep binds of devices JoyPro does not write."
    Assert-Match $scInPlaceCleanText 'kb1_space" activationMode="press"' "starcitizen clean must keep keyboard rebinds unless keyboard binds are included."
    Assert-Match $scInPlaceCleanText '<flight_move_pitch invert="1"/>' "starcitizen export must keep device options."
    Assert-Match $scInPlaceCleanText 'mo1_mouse1' "starcitizen export must keep on-foot actions."
    $scInPlaceNuke = Run-Engine @("--profile", $scInPlaceProfile, "starcitizen", "export", $scInPlaceRoot, "--nuke-untouched-devices", "true", "--prefer-local-order", "false", "--text")
    Assert-True ($scInPlaceNuke.ExitCode -eq 0) "starcitizen export with nuke failed."
    $scInPlaceNukeText = [System.IO.File]::ReadAllText($scInPlaceFile)
    Assert-NotMatch $scInPlaceNukeText 'js2_rotz' "starcitizen nuke must release ship binds of unused devices."
    Assert-Match $scInPlaceNukeText '<rebind input="js1_x"/>' "starcitizen nuke must keep the profile's binds."

    New-Item -Path $starCitizenOrderProfileDir -ItemType Directory -Force | Out-Null
    New-Item -Path $starCitizenOrderExportProfileDir -ItemType Directory -Force | Out-Null

@'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard {00000000-0000-0000-0000-000000000000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Alpha Stick {AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"/>
  <options type="joystick" instance="2" Product="Beta Stick {BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="fire_alpha"><!--Alpha Fire//false-->
    <rebind input="js1_button1"/>
   </action>
   <action name="fire_beta"><!--Beta Fire//false-->
    <rebind input="js2_button2"/>
   </action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@ | Set-Content -Path $starCitizenOrderActionmaps -Encoding UTF8

@'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard {00000000-0000-0000-0000-000000000000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Alpha Stick {AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"/>
  <options type="joystick" instance="2" Product="Beta Stick {BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="fire_alpha"><!--Alpha Fire//false--></action>
   <action name="fire_beta"><!--Beta Fire//false--></action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@ | Set-Content -Path $starCitizenDbCatalogPath -Encoding UTF8

    $starCitizenOrderImport = Run-Engine @("--profile", $starCitizenOrderProfile, "starcitizen", "import", $starCitizenOrderRoot, "--merge", "--text")
    Assert-True ($starCitizenOrderImport.ExitCode -eq 0) "engine starcitizen order import failed."
    Assert-True (Test-Path $starCitizenOrderProfile) "engine starcitizen order import did not save profile."
    Assert-True ($starCitizenOrderImport.Output -match "(?m)^Imported Relations:\s+\d+\s*$") "starcitizen order import missing imported relation count."
    Assert-True ($starCitizenOrderImport.Output -match "(?m)^Imported Binds:\s+\d+\s*$") "starcitizen order import missing imported bind count."
    Assert-True ($starCitizenOrderImport.Output -match "(?m)^Merged Into Existing:\s+\d+\s*$") "starcitizen order import missing merge count."
    $starCitizenOrderImportCount = [int]([regex]::Match($starCitizenOrderImport.Output, "(?m)^Imported Relations:\s+(\d+)\s*$").Groups[1].Value)
    $starCitizenOrderMergedCount = [int]([regex]::Match($starCitizenOrderImport.Output, "(?m)^Merged Into Existing:\s+(\d+)\s*$").Groups[1].Value)
    Assert-True (($starCitizenOrderImportCount + $starCitizenOrderMergedCount) -ge 1) "starcitizen order import applied no entries (neither imported nor merged)."

@'
<ActionMaps>
 <ActionProfiles version="1" optionsVersion="2" rebindVersion="2" profileName="default">
  <options type="keyboard" instance="1" Product="Keyboard {00000000-0000-0000-0000-000000000000}"/>
  <options type="gamepad" instance="1" Product="Controller (Gamepad)"/>
  <options type="joystick" instance="1" Product="Beta Stick {BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}"/>
  <options type="joystick" instance="2" Product="Alpha Stick {AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"/>
  <modifiers />
  <actionmap name="spaceship_movement">
   <action name="fire_alpha"><!--Alpha Fire//false--></action>
   <action name="fire_beta"><!--Beta Fire//false--></action>
  </actionmap>
 </ActionProfiles>
</ActionMaps>
'@ | Set-Content -Path $starCitizenOrderExportActionmaps -Encoding UTF8

    [Environment]::SetEnvironmentVariable(
        "JOYPRO_DI_ORDER_OVERRIDE",
        "Beta Stick {BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB};Alpha Stick {AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",
        "Process")
    $starCitizenOrderExport = Run-Engine @("--profile", $starCitizenOrderProfile, "starcitizen", "export", $starCitizenOrderExportRoot, "--mode", "merge-overwrite", "--prefer-local-order", "true", "--keep-keyboard-defaults", "true", "--text")
    [Environment]::SetEnvironmentVariable("JOYPRO_DI_ORDER_OVERRIDE", $null, "Process")
    Assert-True ($starCitizenOrderExport.ExitCode -eq 0) "engine starcitizen order export failed."
    Assert-True ($starCitizenOrderExport.Output -match "(?m)^Mode:\s+merge-overwrite\s*$") "starcitizen order export output missing mode."
    Assert-True (Test-Path $starCitizenOrderExportActionmaps) "starcitizen order export did not write actionmaps.xml."

    $starCitizenOrderExportXml = Get-Content -Raw $starCitizenOrderExportActionmaps
    Assert-Match $starCitizenOrderExportXml 'instance="1" Product="Beta Stick \{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB\}"' "starcitizen order export did not keep local slot 1 product."
    Assert-Match $starCitizenOrderExportXml 'instance="2" Product="Alpha Stick \{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA\}"' "starcitizen order export did not keep local slot 2 product."
    Assert-Match $starCitizenOrderExportXml '(?is)<action name="fire_alpha".*?<rebind input="js2_button1"' "starcitizen order export did not remap Alpha action to local slot 2."
    Assert-Match $starCitizenOrderExportXml '(?is)<action name="fire_beta".*?<rebind input="js1_button2"' "starcitizen order export did not remap Beta action to local slot 1."

    $legacyLoad = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--correction-report", $legacyCorrectionReport, "profile", "load", $legacyProfileJson)
    Assert-True ($legacyLoad.ExitCode -eq 0) "engine profile load legacy_profile.json failed."
    Assert-True (Test-Path $legacyCorrectionReport) "legacy correction report missing."
    $legacyCorrectionPayload = Get-Content -Raw $legacyCorrectionReport
    Assert-True ($legacyCorrectionPayload -match '"Kind"\s*:\s*"db-fingerprint-check"') "legacy correction report missing db-fingerprint-check entry."
    Assert-True ($legacyCorrectionPayload -match '"Action"\s*:\s*"auto"') "legacy profile should trigger correction run due missing fingerprint."
    Assert-True ($legacyCorrectionPayload -match 'no DB fingerprint') "legacy correction report missing missing-fingerprint reason."

    $saveFingerprintProfile = Run-Engine @("profile", "save", $fingerprintedProfile)
    Assert-True ($saveFingerprintProfile.ExitCode -eq 0) "engine profile save fingerprinted_profile.pr0file failed."
    Assert-True (Test-Path $fingerprintedProfile) "fingerprinted profile file missing."

    $matchedLoad = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--correction-report", $fingerprintMatchReport, "profile", "load", $fingerprintedProfile)
    Assert-True ($matchedLoad.ExitCode -eq 0) "engine profile load fingerprinted_profile.pr0file failed."
    Assert-True (Test-Path $fingerprintMatchReport) "fingerprint match report missing."
    $fingerprintMatchPayload = Get-Content -Raw $fingerprintMatchReport
    Assert-True ($fingerprintMatchPayload -match '"Kind"\s*:\s*"db-fingerprint-check"') "fingerprint match report missing db-fingerprint-check entry."
    Assert-True ($fingerprintMatchPayload -match '"Action"\s*:\s*"skipped"') "matching fingerprint should skip correction run."
    Assert-True ($fingerprintMatchPayload -match 'matches current DB fingerprint') "fingerprint match report missing match reason."

    $exportRelations = Run-Engine @("relations", "export", $relationsJson)
    Assert-True ($exportRelations.ExitCode -eq 0) "engine relations export failed."
    Assert-True (Test-Path $relationsJson) "relations export file not created."
    Assert-True ((Get-Content -Raw $relationsJson) -match '"Relations"') "relations export file is not valid JSON payload."

    $exportBinds = Run-Engine @("binds", "export", $bindsJson)
    Assert-True ($exportBinds.ExitCode -eq 0) "engine binds export failed."
    Assert-True (Test-Path $bindsJson) "binds export file not created."
    Assert-True ((Get-Content -Raw $bindsJson) -match '"Binds"') "binds export file is not valid JSON payload."

    New-Item -Path $savedInput -ItemType Directory -Force | Out-Null
@'
local diff = {
  ["axisDiffs"] = {
    ["a2001cdnil"] = {
      ["name"] = "Pitch",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_Y",
        },
      },
    },
  },
  ["ffDiffs"] = {
    ["invertX"] = true,
    ["invertY"] = false,
    ["swapAxes"] = true,
  },
  ["keyDiffs"] = {
    ["d3001pnilunilcdnilvdnilvpnilvunil"] = {
      ["name"] = "Trigger",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN1",
          ["reformers"] = {
            [1] = "LAlt",
          },
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path $savedDiff -Encoding UTF8
@'
local diff = {
  ["axisDiffs"] = {
    ["a2001cdnil"] = {
      ["name"] = "Pitch",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_Y",
          ["JPK"] = "JP Pitch Relation",
          ["JGRP"] = {
            [1] = "Combat",
          },
        },
      },
    },
  },
  ["ffDiffs"] = {
    ["invertX"] = false,
    ["invertY"] = true,
    ["swapAxes"] = false,
  },
  ["keyDiffs"] = {
    ["d3001pnilunilcdnilvdnilvpnilvunil"] = {
      ["name"] = "Trigger",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN1",
          ["JPK"] = "JP Trigger Relation",
          ["JGRP"] = {
            [1] = "Combat",
          },
          ["reformers"] = {
            [1] = "LAlt",
          },
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path $savedBackup -Encoding UTF8
@'
local modifiers = {
  ["LAlt"] = {
    ["device"] = "Keyboard",
    ["key"] = "LAlt",
    ["JPK"] = "LAlt",
    ["switch"] = false,
  },
}
return modifiers
'@ | Set-Content -Path $savedModifiers -Encoding UTF8

    $dcsScan = Run-Engine @("dcs", "scan", $savedGames, "--text")
    Assert-True ($dcsScan.ExitCode -eq 0) "engine dcs scan failed."
    Assert-True ($dcsScan.Output -match "Files Scanned:\s+2") "engine dcs scan did not detect expected file count."
    Assert-True ($dcsScan.Output -match "Backup Files Scanned \(.jp\):\s+1") "engine dcs scan did not detect backup file count."
    Assert-True ($dcsScan.Output -match "Entries Parsed:\s+4") "engine dcs scan did not parse expected entries."
    Assert-True ($dcsScan.Output -match "Force Feedback Entries Parsed:\s+2") "engine dcs scan did not parse expected ffDiffs entries."

    $dcsImport = Run-Engine @("dcs", "import", $savedGames, "--text", "--save-profile", $importProfile)
    Assert-True ($dcsImport.ExitCode -eq 0) "engine dcs import failed."
    Assert-True (Test-Path $importProfile) "engine dcs import did not write profile."
    Assert-True ($dcsImport.Output -match "Imported Relations:\s+2") "engine dcs import did not import expected relation count."
    Assert-True ($dcsImport.Output -match "Force Feedback Devices Imported:\s+1") "engine dcs import did not import expected ffDiffs device count."

    $ffbList = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--path", $savedGames, "--confirm", "false")
    Assert-True ($ffbList.ExitCode -eq 0) "engine dcs ffb list failed."
    $ffbListJson = ConvertFrom-Json $ffbList.Output
    $ffbRows = @($ffbListJson.Devices | ForEach-Object { ($_.Device.ToString() + "|" + $_.HasSetting + "|" + $_.InvertX + "|" + $_.InvertY + "|" + $_.SwapAxis + "|" + $_.Connected + "|" + $_.Profile) })
    Assert-True ($ffbRows.Count -ge 1) "engine dcs ffb list did not return expected device rows."

    New-Item -Path $dcsDefaultsJoystick -ItemType Directory -Force | Out-Null
@'
return {
  ["name"] = "Default"
}
'@ | Set-Content -Path $dcsDefaultsDefaultFile -Encoding UTF8
@'
return {
  ["name"] = "Stick Specific"
}
'@ | Set-Content -Path $dcsDefaultsCustomFile -Encoding UTF8

    $dcsDefaultsCut = Run-Engine @("dcs", "defaults", "cut", $dcsDefaultsSavedGames, $dcsDefaultsInstall, "--text")
    Assert-True ($dcsDefaultsCut.ExitCode -eq 0) "engine dcs defaults cut failed."
    Assert-True ($dcsDefaultsCut.Output -match "(?m)^Action:\s+cut") "engine dcs defaults cut output missing action."
    Assert-True (Test-Path $dcsDefaultsDefaultFile) "engine dcs defaults cut should keep default.lua in install folder."
    Assert-True (-not (Test-Path $dcsDefaultsCustomFile)) "engine dcs defaults cut should move stick-specific file out of install folder."
    Assert-True (Test-Path $dcsDefaultsBackupDefaultFile) "engine dcs defaults cut did not backup default.lua."
    Assert-True (Test-Path $dcsDefaultsBackupCustomFile) "engine dcs defaults cut did not move stick-specific file to backup."

    Remove-Item -Path $dcsDefaultsDefaultFile -Force

    $dcsDefaultsRestore = Run-Engine @("dcs", "defaults", "restore", $dcsDefaultsSavedGames, $dcsDefaultsInstall, "--text")
    Assert-True ($dcsDefaultsRestore.ExitCode -eq 0) "engine dcs defaults restore failed."
    Assert-True ($dcsDefaultsRestore.Output -match "(?m)^Action:\s+restore") "engine dcs defaults restore output missing action."
    Assert-True (Test-Path $dcsDefaultsDefaultFile) "engine dcs defaults restore did not restore default.lua."
    Assert-True (Test-Path $dcsDefaultsCustomFile) "engine dcs defaults restore did not restore stick-specific file."

    $dcsRefreshPreview = Run-Engine @(
        "dcs", "refresh", "preview-clicks",
        $dcsDefaultsSavedGames,
        $dcsDefaultsInstall,
        "--window-left", "0",
        "--window-top", "0",
        "--window-width", "1920",
        "--window-height", "1080",
        "--preview-image", $dcsRefreshPreviewImage,
        "--text")
    Assert-True ($dcsRefreshPreview.ExitCode -eq 0) "engine dcs refresh preview-clicks failed."
    Assert-True ($dcsRefreshPreview.Output -match "(?m)^Action:\s+preview-clicks") "engine dcs refresh preview-clicks output missing action."
    Assert-True ($dcsRefreshPreview.Output -match "(?m)^Clicks:\s+[1-9]\d*") "engine dcs refresh preview-clicks output missing click list."

$dcsRefreshCleanDryRun = Run-Engine @(
    "dcs", "refresh", "clean",
    $dcsDefaultsSavedGames,
        $dcsDefaultsInstall,
        "--dry-run",
        "--confirm", "false",
        "--launch", "false",
        "--window-left", "0",
        "--window-top", "0",
        "--window-width", "1920",
        "--window-height", "1080",
        "--text")
Assert-True ($dcsRefreshCleanDryRun.ExitCode -eq 0) "engine dcs refresh clean --dry-run failed."
Assert-True ($dcsRefreshCleanDryRun.Output -match "(?m)^Action:\s+clean") "engine dcs refresh clean --dry-run output missing action."
Assert-True ($dcsRefreshCleanDryRun.Output -match "(?m)^Dry Run:\s+true") "engine dcs refresh clean --dry-run output missing dry-run marker."

$dcsRefreshCleanHeadlessDryRun = Run-Engine @(
    "dcs", "refresh", "clean",
    $dcsDefaultsSavedGames,
    $dcsDefaultsInstall,
    "--headless-lua", "true",
    "--plane", "A-10C",
    "--dry-run",
    "--confirm", "false",
    "--text")
Assert-True ($dcsRefreshCleanHeadlessDryRun.ExitCode -eq 0) "engine dcs refresh clean --headless-lua --dry-run failed."
Assert-True ($dcsRefreshCleanHeadlessDryRun.Output -match "(?m)^Headless Lua:\s+true") "headless clean dry-run output missing headless marker."
Assert-True ($dcsRefreshCleanHeadlessDryRun.Output -match "(?m)^Requested Planes:\s+A-10C") "headless clean dry-run output missing requested plane marker."
Assert-True ($dcsRefreshCleanHeadlessDryRun.Output -match "(?m)^Matched Planes:\s+A-10C") "headless clean dry-run output missing matched plane marker."

$dcsRefreshIdDryRun = Run-Engine @(
    "dcs", "refresh", "id",
    $dcsDefaultsSavedGames,
        $dcsDefaultsInstall,
        "--dry-run",
        "--confirm", "false",
        "--launch", "false",
        "--modules-to-scan", "0",
        "--window-left", "0",
        "--window-top", "0",
        "--window-width", "1920",
        "--window-height", "1080",
        "--text")
Assert-True ($dcsRefreshIdDryRun.ExitCode -eq 0) "engine dcs refresh id --dry-run failed."
Assert-True ($dcsRefreshIdDryRun.Output -match "(?m)^Action:\s+id") "engine dcs refresh id --dry-run output missing action."
Assert-True ($dcsRefreshIdDryRun.Output -match "(?m)^Dry Run:\s+true") "engine dcs refresh id --dry-run output missing dry-run marker."

# DCS commands named by a localization key ("Input.SpitfireLFMkIX.pitch_minus", "29Fulcrum:Inp:..."): a DB built before
# the refresh translated them still shows DCS's English text (l10n\en\LC_MESSAGES\input.mo of the DCS folder), in the
# search and the catalog, and never the key.
function New-MoFile {
    param([string]$Path, [System.Collections.IDictionary]$Entries)
    $keys = @($Entries.Keys | Sort-Object -CaseSensitive)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $count = $keys.Count
    $origTable = 28
    $transTable = $origTable + 8 * $count
    $dataStart = $transTable + 8 * $count
    $data = New-Object System.Collections.Generic.List[byte]
    $origEntries = @()
    $transEntries = @()
    foreach ($key in $keys) {
        $bytes = $utf8.GetBytes([string]$key)
        $origEntries += , @($bytes.Length, ($dataStart + $data.Count))
        $data.AddRange([byte[]]$bytes); $data.Add(0)
    }
    foreach ($key in $keys) {
        $bytes = $utf8.GetBytes([string]$Entries[$key])
        $transEntries += , @($bytes.Length, ($dataStart + $data.Count))
        $data.AddRange([byte[]]$bytes); $data.Add(0)
    }
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    foreach ($v in @([uint32]2500072158, 0, $count, $origTable, $transTable, 0, $dataStart)) { $writer.Write([uint32]$v) }
    foreach ($e in $origEntries + $transEntries) { $writer.Write([uint32]$e[0]); $writer.Write([uint32]$e[1]) }
    $writer.Write($data.ToArray())
    $writer.Flush()
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
}

$previousNamesDataDir = $env:JOYPRO_DATA_DIR
$previousNamesDetection = $env:JOYPRO_INSTALL_DETECTION
try {
    $env:JOYPRO_DATA_DIR = Join-Path $tempDir "names-data"
    $env:JOYPRO_INSTALL_DETECTION = "off"
    $namesInstall = Join-Path $tempDir "names-install\DCS World"
    New-MoFile (Join-Path $namesInstall "l10n\en\LC_MESSAGES\input.mo") ([ordered]@{
        "Input.TestKeyPlane.pitch_minus" = "Engine RPM DECREASE"
        "Input.TestKeyPlane.pitch_plus" = "Engine RPM INCREASE"
        "29Test:Inp:Gear Lever - UP" = "Gear Lever - UP"
        "29Test:Cat:Gear Panel" = "Gear Panel"
        "Gear up" = "Landing Gear - UP (reworded)"
    })
    $namesDb = Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\DCS"
    New-Item -ItemType Directory -Force -Path $namesDb | Out-Null
    $namesRow = { param($t, $c, $i) "        <tr>`n          <td></td>`n          <td>$t</td>`n          <td>$c</td>`n          <td>$i</td>`n        </tr>`n" }
    $namesHtml = "<html><body><table>`n" +
        (& $namesRow "Input.TestKeyPlane.pitch_minus" "Engine Controls" "dnilp3007unilcd2vdnilvp-1vunil") +
        (& $namesRow "Input.TestKeyPlane.pitch_plus" "Engine Controls" "dnilp3007unilcd2vdnilvp1vunil") +
        (& $namesRow "29Test:Inp:Gear Lever - UP" "29Test:Cat:Gear Panel" "d3001pnilunilcd5vd1vpnilvunil") +
        (& $namesRow "Input.TestKeyPlane.canopy_open" "Systems" "d3002pnilunilcd5vd1vpnilvunil") +
        (& $namesRow "Gear up" "Systems" "d68pnilunilcdnilvdnilvpnilvunil") +
        "</table></body></html>`n"
    [System.IO.File]::WriteAllText((Join-Path $namesDb "TestKeyPlane.html"), $namesHtml, (New-Object System.Text.UTF8Encoding($false)))
    $namesSet = Run-Engine @("games", "install", "set", "dcs", $namesInstall, "--text")
    Assert-True ($namesSet.ExitCode -eq 0) "games install set dcs (names fixture) failed. Output: $($namesSet.Output)"

    $namesSearch = Run-Engine @("relations", "search", "engine rpm", "--game", "DCS", "--plane", "TestKeyPlane", "--text")
    Assert-True ($namesSearch.ExitCode -eq 0) "relations search (localization keys) failed. Output: $($namesSearch.Output)"
    Assert-Match $namesSearch.Output "Engine RPM DECREASE" "relations search did not find the English name of a localization-key command."
    Assert-Match $namesSearch.Output "Engine RPM INCREASE" "relations search did not find the second English name."
    Assert-NotMatch $namesSearch.Output "Input\.TestKeyPlane" "relations search showed a raw localization key."

    # The key or the id still finds the command (shown by its readable title, never by the key).
    $namesKey = Run-Engine @("relations", "search", "pitch_minus", "--game", "DCS", "--plane", "TestKeyPlane", "--text")
    Assert-Match $namesKey.Output "Engine RPM DECREASE" "relations search did not find a command by its key."
    Assert-NotMatch $namesKey.Output "Input\.TestKeyPlane" "relations search by key showed the raw key."
    $namesId = Run-Engine @("relations", "search", "dnilp3007unilcd2vdnilvp1vunil", "--game", "DCS", "--plane", "TestKeyPlane", "--text")
    Assert-Match $namesId.Output "Engine RPM INCREASE \| Command id" "relations search did not find a command by its id."
    $namesBrowse = Run-Engine @("relations", "browse", "--game", "DCS", "--plane", "TestKeyPlane", "--text")
    Assert-True ($namesBrowse.ExitCode -eq 0) "relations browse (localization keys) failed. Output: $($namesBrowse.Output)"
    Assert-Match $namesBrowse.Output "Gear Lever - UP" "relations browse did not translate a <module>:Inp: key."
    Assert-Match $namesBrowse.Output "Gear Panel" "relations browse did not translate a <module>:Cat: category."
    Assert-Match $namesBrowse.Output "Canopy open" "relations browse did not make a key without English text readable."
    Assert-Match $namesBrowse.Output "Gear up" "relations browse reworded a name that already is English text."
    Assert-NotMatch $namesBrowse.Output "Input\.TestKeyPlane|29Test:" "relations browse showed a raw localization key."
    $null = Run-Engine @("games", "install", "unset", "dcs", "--text")
}
finally {
    $env:JOYPRO_DATA_DIR = $previousNamesDataDir
    $env:JOYPRO_INSTALL_DETECTION = $previousNamesDetection
}

$dcsRefreshIdHeadlessFilteredDryRun = Run-Engine @(
    "dcs", "refresh", "id",
    $dcsDefaultsSavedGames,
    $dcsDefaultsInstall,
    "--headless-lua", "true",
    "--plane", "A-10C",
    "--dry-run",
    "--confirm", "false",
    "--text")
Assert-True ($dcsRefreshIdHeadlessFilteredDryRun.ExitCode -eq 0) "engine dcs refresh id --headless-lua --plane --dry-run failed."
Assert-True ($dcsRefreshIdHeadlessFilteredDryRun.Output -match "(?m)^Headless Lua:\s+true") "headless dry-run output missing headless marker."
Assert-True ($dcsRefreshIdHeadlessFilteredDryRun.Output -match "(?m)^Requested Planes:\s+A-10C") "headless dry-run output missing requested plane marker."
Assert-True ($dcsRefreshIdHeadlessFilteredDryRun.Output -match "(?m)^Matched Planes:\s+A-10C") "headless dry-run output missing matched plane marker."

# Device folders directly under Input mean the Input root is the profile; they must never become planes.
$rootInputJoystick = Join-Path $dcsDefaultsInstall "Mods\\aircraft\\RootInputMod\\Input\\joystick"
New-Item -Path $rootInputJoystick -ItemType Directory -Force | Out-Null
'return {}' | Set-Content -Path (Join-Path $rootInputJoystick "default.lua") -Encoding UTF8
$dcsRefreshIdHeadlessAllDryRun = Run-Engine @(
    "dcs", "refresh", "id",
    $dcsDefaultsSavedGames,
    $dcsDefaultsInstall,
    "--headless-lua", "true",
    "--dry-run",
    "--confirm", "false",
    "--text")
Assert-True ($dcsRefreshIdHeadlessAllDryRun.ExitCode -eq 0) "engine dcs refresh id --headless-lua --dry-run (all planes) failed."
Assert-Match $dcsRefreshIdHeadlessAllDryRun.Output "(?m)^Matched Planes:.*\bRootInputMod\b" "headless discovery should name an Input-root profile after its module folder."
Assert-NotMatch $dcsRefreshIdHeadlessAllDryRun.Output "(?m)^Matched Planes:.*\bjoystick\b" "headless discovery must not treat Input\joystick as a plane."

$dcsRefreshIdHeadlessUnknownDryRun = Run-Engine @(
    "dcs", "refresh", "id",
    $dcsDefaultsSavedGames,
    $dcsDefaultsInstall,
    "--headless-lua", "true",
    "--plane", "A-10C",
    "--plane", "NotADcsProfile",
    "--dry-run",
    "--confirm", "false",
    "--text")
Assert-True ($dcsRefreshIdHeadlessUnknownDryRun.ExitCode -eq 0) "headless dry-run with one unknown plane should still succeed."
Assert-Match $dcsRefreshIdHeadlessUnknownDryRun.Output "(?m)^Matched Planes:\s+A-10C\s*$" "headless dry-run must not fuzzy-map an unknown plane to another profile."
Assert-Match $dcsRefreshIdHeadlessUnknownDryRun.Output "NotADcsProfile' is not a DCS input profile" "headless dry-run should warn about the skipped unknown plane."

$dcsRefreshIdHeadlessOnlyUnknownDryRun = Run-Engine @(
    "dcs", "refresh", "id",
    $dcsDefaultsSavedGames,
    $dcsDefaultsInstall,
    "--headless-lua", "true",
    "--plane", "NotADcsProfile",
    "--dry-run",
    "--confirm", "false",
    "--text")
Assert-True ($dcsRefreshIdHeadlessOnlyUnknownDryRun.ExitCode -ne 0) "headless dry-run with only unknown planes should fail."

$newProfilePath = Join-Path $tempDir "new_profile\\fresh.pr0file"
$newProfile = Run-Engine @("--profile", $importProfile, "profile", "new", $newProfilePath)
Assert-True ($newProfile.ExitCode -eq 0) "engine profile new failed."
Assert-True (Test-Path $newProfilePath) "engine profile new did not write the profile file."
$newProfileSummary = Run-Engine @("--profile", $newProfilePath, "relations", "list", "--text")
Assert-True ($newProfileSummary.ExitCode -eq 0) "relations list on new profile failed."
Assert-Match $newProfileSummary.Output "(?m)^Relations:\s+0\s*$" "engine profile new should create an empty profile, not a copy of the loaded one."

    $importedSummary = Run-Engine @("--profile", $importProfile, "summary")
    Assert-True ($importedSummary.ExitCode -eq 0) "engine summary on imported profile failed."
    Assert-True ($importedSummary.Output -match "relations=2.*binds=2") "imported profile summary does not match expected counts."
    $importedRelations = Run-Engine @("--profile", $importProfile, "relations", "export", $importedRelationsJson)
    Assert-True ($importedRelations.ExitCode -eq 0) "engine relations export of imported profile failed."
    $importedRelationsPayload = Get-Content -Raw $importedRelationsJson
    Assert-True ($importedRelationsPayload -match "JP Pitch Relation") "imported relation names did not preserve JP metadata (pitch)."
    Assert-True ($importedRelationsPayload -match "JP Trigger Relation") "imported relation names did not preserve JP metadata (trigger)."
    Assert-True ($importedRelationsPayload -match '"Combat"') "imported relation groups did not preserve JP metadata."
    $importedBinds = Run-Engine @("--profile", $importProfile, "binds", "export", $importedBindsJson)
    Assert-True ($importedBinds.ExitCode -eq 0) "engine binds export of imported profile failed."
    $importedBindsPayload = Get-Content -Raw $importedBindsJson
    Assert-True ($importedBindsPayload -match '"AllReformers"\s*:\s*\[\s*"LAlt') "imported modifiers were not restored from modifiers.lua."
    $relationDeviceMatch = [regex]::Match($importedBindsPayload, '"Joystick"\s*:\s*"(?<device>[^"]+)"')
    Assert-True ($relationDeviceMatch.Success) "engine imported binds payload missing Joystick value."
    $relationDeviceName = $relationDeviceMatch.Groups["device"].Value.Trim()
    Assert-True (-not [string]::IsNullOrWhiteSpace($relationDeviceName)) "engine imported binds payload produced empty Joystick value."

    $ffbListInitial = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--device", $relationDeviceName, "--confirm", "false", "--text")
    Assert-True ($ffbListInitial.ExitCode -eq 0) "engine dcs ffb list failed."
    Assert-True ($ffbListInitial.Output -match "invertX=false,\s*invertY=true,\s*swapAxis=false,\s*hasSetting=true") "engine dcs ffb list did not reflect imported backup-priority ffDiffs values."

    $ffbSet = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "set", $relationDeviceName, "--invert-x", "true", "--invert-y", "false", "--swap-axis", "true", "--confirm", "false", "--text")
    Assert-True ($ffbSet.ExitCode -eq 0) "engine dcs ffb set failed."
    Assert-True ($ffbSet.Output -match "(?m)^Action:\s+set") "engine dcs ffb set output missing action."
    Assert-True ($ffbSet.Output -match "(?m)^Invert X:\s+true") "engine dcs ffb set did not apply invert-x."
    Assert-True ($ffbSet.Output -match "(?m)^Swap Axis:\s+true") "engine dcs ffb set did not apply swap-axis."

    $ffbListAfterSet = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--device", $relationDeviceName, "--confirm", "false", "--text")
    Assert-True ($ffbListAfterSet.ExitCode -eq 0) "engine dcs ffb list after set failed."
    Assert-True ($ffbListAfterSet.Output -match "invertX=true,\s*invertY=false,\s*swapAxis=true,\s*hasSetting=true") "engine dcs ffb set values were not persisted."

    $ffbClear = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "clear", $relationDeviceName, "--confirm", "false", "--text")
    Assert-True ($ffbClear.ExitCode -eq 0) "engine dcs ffb clear failed."
    Assert-True ($ffbClear.Output -match "(?m)^Action:\s+clear") "engine dcs ffb clear output missing action."
    Assert-True ($ffbClear.Output -match "(?m)^Removed:\s+1") "engine dcs ffb clear did not remove expected setting."

    $ffbListAfterClear = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--device", $relationDeviceName, "--confirm", "false", "--text")
    Assert-True ($ffbListAfterClear.ExitCode -eq 0) "engine dcs ffb list after clear failed."
    Assert-True ($ffbListAfterClear.Output -match "invertX=false,\s*invertY=false,\s*swapAxis=false,\s*hasSetting=false") "engine dcs ffb clear did not remove stored setting."

    $ffbClearAll = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "clear", "--all", "--confirm", "false", "--text")
    Assert-True ($ffbClearAll.ExitCode -eq 0) "engine dcs ffb clear --all failed."

    $autoProfile = Join-Path $tempDir "autosave\\nested\\new_profile.pr0file"
    $autoAddRelation = Run-Engine @("--profile", $autoProfile, "relations", "add", "Auto Save Relation", "--button", "--node", "DCS:d3001pnilunilcdnilvdnilvpnilvunil", "--text")
    Assert-True ($autoAddRelation.ExitCode -eq 0) "engine relations add with missing --profile path failed."
    Assert-True (Test-Path $autoProfile) "engine did not auto-create/save profile path for mutating command."
    $autoAddGroup = Run-Engine @("--profile", $autoProfile, "relations", "group", "add", "Auto Save Relation", "Persisted", "--text")
    Assert-True ($autoAddGroup.ExitCode -eq 0) "engine relations group add on auto-created profile failed."
    $autoShow = Run-Engine @("--profile", $autoProfile, "relations", "show", "Auto Save Relation", "--text")
    Assert-True ($autoShow.ExitCode -eq 0) "engine relations show on auto-created profile failed."
    Assert-True ($autoShow.Output -match "(?m)^Groups:\s+Persisted") "auto-saved profile changes were not persisted across commands."
    $nonMutatingProfile = Join-Path $tempDir "autosave\\list_only\\non_mutating.pr0file"
    $groupListNoMutation = Run-Engine @("--profile", $nonMutatingProfile, "relations", "group", "list", "--text")
    Assert-True ($groupListNoMutation.ExitCode -eq 0) "engine relations group list failed on missing profile path."
    Assert-True (-not (Test-Path $nonMutatingProfile)) "non-mutating relations group list unexpectedly created profile file."

    if (-not (Skip-WithoutGameData "Auto Group and Suggest additions on the DCS, Falcon BMS and NS430 catalogs")) {
    $agProfile = Join-Path $tempDir "autogroup\\auto_group.pr0file"
    $agPlanes = "F-16C_50|FA-18C_hornet"
    $agDry = Run-Engine @("--profile", $agProfile, "relations", "auto-group", "--planes", $agPlanes, "--dry-run", "--text")
    Assert-True ($agDry.ExitCode -eq 0) "relations auto-group --dry-run failed. Output: $($agDry.Output)"
    Assert-Match $agDry.Output "(?m)^Relations Created: [1-9]" "relations auto-group --dry-run did not preview new relations."
    Assert-True (-not (Test-Path $agProfile)) "relations auto-group --dry-run unexpectedly saved the profile."
    $agRun = Run-Engine @("--profile", $agProfile, "relations", "auto-group", "--planes", $agPlanes, "--text")
    Assert-True ($agRun.ExitCode -eq 0) "relations auto-group failed. Output: $($agRun.Output)"
    Assert-True (Test-Path $agProfile) "relations auto-group did not save the profile."
    $agPitch = Run-Engine @("--profile", $agProfile, "relations", "show", "Pitch", "--text")
    Assert-True ($agPitch.ExitCode -eq 0) "auto-grouped relation Pitch missing."
    Assert-Match $agPitch.Output "(?m)^Groups:.*AutoGroup" "auto-grouped relation is not tagged AutoGroup."
    Assert-Match $agPitch.Output "id=a2001cdnil, aircraft=F-16C_50" "auto-grouped Pitch lacks the F-16C."
    Assert-Match $agPitch.Output "id=a2001cdnil, aircraft=.*FA-18C_hornet" "auto-grouped Pitch lacks the F/A-18C."
    $agAgain = Run-Engine @("--profile", $agProfile, "relations", "auto-group", "--planes", $agPlanes, "--text")
    Assert-True ($agAgain.ExitCode -eq 0) "second relations auto-group failed."
    Assert-Match $agAgain.Output "(?m)^Nodes Added: 0" "second relations auto-group added nodes again."
    $agPurist = Run-Engine @("--profile", $agProfile, "relations", "auto-group", "--preset", "purist", "--replace", "--planes", "F-16C_50", "--text")
    Assert-True ($agPurist.ExitCode -eq 0) "relations auto-group --preset purist --replace failed. Output: $($agPurist.Output)"
    Assert-Match $agPurist.Output "(?m)^Relations Removed: [1-9]" "relations auto-group --replace did not remove the earlier AutoGroup relations."
    $agTms = Run-Engine @("--profile", $agProfile, "relations", "show", "Target Management System Up [F-16C_50]", "--text")
    Assert-True ($agTms.ExitCode -eq 0) "purist preset did not create a per-aircraft relation for a non-basic function."
    $agPuristPitch = Run-Engine @("--profile", $agProfile, "relations", "show", "Pitch", "--text")
    Assert-True ($agPuristPitch.ExitCode -eq 0) "purist preset did not keep the shared basic relation Pitch."
    Assert-NotMatch $agPuristPitch.Output "FA-18C_hornet" "relations auto-group --replace kept nodes of the removed relation."
    $agBad = Run-Engine @("--profile", $agProfile, "relations", "auto-group", "--preset", "bogus", "--text")
    Assert-True ($agBad.ExitCode -ne 0) "relations auto-group accepted an unknown preset."

    # A relation the user named differently is recognized by its function and receives the missing aircraft.
    $agOwnProfile = Join-Path $tempDir "autogroup\\own_names.pr0file"
    $agOwnAdd = Run-Engine @("--profile", $agOwnProfile, "relations", "add", "My Pitch Axis", "--axis", "--node", "DCS:a2001cdnil:F-16C_50|FA-18C_hornet", "--text")
    Assert-True ($agOwnAdd.ExitCode -eq 0) "relations add for the learned-name auto-group check failed."
    $agOwnRun = Run-Engine @("--profile", $agOwnProfile, "relations", "auto-group", "--planes", "F-16C_50|FA-18C_hornet|A-10C", "--text")
    Assert-True ($agOwnRun.ExitCode -eq 0) "relations auto-group on a user-named profile failed. Output: $($agOwnRun.Output)"
    $agOwnShow = Run-Engine @("--profile", $agOwnProfile, "relations", "show", "My Pitch Axis", "--text")
    Assert-Match $agOwnShow.Output "aircraft=.*A-10C" "auto-group did not add the A-10C pitch to the user's own pitch relation."
    Assert-NotMatch $agOwnRun.Output "(?m)^  - Pitch \(" "auto-group created a second pitch relation next to the user's own one."

    # A command the user switched off for a plane stays off.
    $agOffProfile = Join-Path $tempDir "autogroup\\disabled.pr0file"
    $agOffAdd = Run-Engine @("--profile", $agOffProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50|FA-18C_hornet", "--text")
    Assert-True ($agOffAdd.ExitCode -eq 0) "relations add for the disabled-entry auto-group check failed."
    $agOffDisable = Run-Engine @("--profile", $agOffProfile, "relations", "item", "disable", "Pitch", "--node", "DCS:a2001cdnil:F-16C_50", "--text")
    Assert-True ($agOffDisable.ExitCode -eq 0) "relations item disable for the disabled-entry auto-group check failed."
    $agOffRun = Run-Engine @("--profile", $agOffProfile, "relations", "auto-group", "--planes", "F-16C_50|FA-18C_hornet", "--text")
    Assert-True ($agOffRun.ExitCode -eq 0) "relations auto-group on a profile with a disabled entry failed."
    Assert-Match $agOffRun.Output "(?m)^Kept Disabled By You: 1" "auto-group did not report the user-disabled entry."
    $agOffShow = Run-Engine @("--profile", $agOffProfile, "relations", "show", "Pitch", "--text")
    Assert-NotMatch $agOffShow.Output "aircraft=(.*, )?F-16C_50(,|\s*$)" "auto-group re-enabled a pitch entry the user had disabled."

    # Auto Group spans games: the DCS F-16C and Falcon BMS axes land in the same relations.
    # (Star Citizen is not used here: the SC tests above swap in a synthetic SC catalog.)
    $agGamesRun = Run-Engine @("--profile", (Join-Path $tempDir "autogroup\\games.pr0file"), "relations", "auto-group", "--planes", "F-16C_50|FalconBMS", "--text")
    Assert-True ($agGamesRun.ExitCode -eq 0) "relations auto-group across games failed. Output: $($agGamesRun.Output)"
    $agGamesPitch = Run-Engine @("--profile", (Join-Path $tempDir "autogroup\\games.pr0file"), "relations", "show", "Pitch", "--text")
    Assert-Match $agGamesPitch.Output "game=DCS, id=a2001cdnil, aircraft=F-16C_50" "cross-game Pitch lacks the DCS F-16C."
    Assert-Match $agGamesPitch.Output "game=FalconBMS, id=\S+, aircraft=FalconBMS" "cross-game Pitch lacks Falcon BMS."
    $agGamesYaw = Run-Engine @("--profile", (Join-Path $tempDir "autogroup\\games.pr0file"), "relations", "show", "Rudder", "--text")
    Assert-Match $agGamesYaw.Output "game=FalconBMS, id=axis/Yaw, aircraft=FalconBMS" "cross-game Rudder lacks the Falcon BMS yaw axis."

    # Input layers (NS430 GPS) are part of the catalog and are never split by wing type.
    $agLayerRun = Run-Engine @("--profile", (Join-Path $tempDir "autogroup\\layers.pr0file"), "relations", "auto-group", "--preset", "wing", "--planes", "NS430|Default", "--dry-run", "--text")
    Assert-True ($agLayerRun.ExitCode -eq 0) "relations auto-group for input layers failed."
    Assert-Match $agLayerRun.Output "(?m)^  - NS430 CDI \(button, new" "auto-group did not create the NS430 CDI relation."
    Assert-Match $agLayerRun.Output "(?m)^  - Pitch \(axis, new, \+1 nodes\): Default" "auto-group split the Default layer by wing type."

    # Suggest additions: the relation's function and commands give the missing aircraft, with the reason; "never" adds
    # a command switched off so it is not proposed again.
    $sgProfile = Join-Path $tempDir "suggest\\suggest.pr0file"
    Run-Engine @("--profile", $sgProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--text") | Out-Null
    $sgList = Run-Engine @("--profile", $sgProfile, "relations", "suggest", "Pitch", "--min", "high", "--text")
    Assert-True ($sgList.ExitCode -eq 0) "relations suggest failed. Output: $($sgList.Output)"
    Assert-Match $sgList.Output "(?m)^Function: Pitch \(pitch\)" "relations suggest did not recognise the relation's function."
    Assert-Match $sgList.Output "(?m)^  - \[high\] DCS:a2001cdnil:([^\r\n]*\|)?FA-18C_hornet(\|| \| )" "relations suggest did not propose the F/A-18C pitch axis."
    Assert-Match $sgList.Output "is the 'Pitch' function in .*\(function catalog, checked against the manuals\)" "relations suggest gave no catalog reason."
    Assert-Match $sgList.Output "Same DCS command id in .* as in F-16C_50, same title\." "relations suggest gave no same-id reason."
    $sgApply = Run-Engine @("--profile", $sgProfile, "relations", "suggest", "apply", "Pitch", "--add", "DCS:a2001cdnil:FA-18C_hornet", "--never", "DCS:a2001cdnil:A-10C", "--text")
    Assert-True ($sgApply.ExitCode -eq 0) "relations suggest apply failed. Output: $($sgApply.Output)"
    Assert-Match $sgApply.Output "(?m)^Added: 1\r?$" "relations suggest apply did not add the accepted command."
    $sgShow = Run-Engine @("--profile", $sgProfile, "relations", "show", "Pitch", "--text")
    Assert-Match $sgShow.Output "aircraft=F-16C_50, FA-18C_hornet" "relations suggest apply did not enable the accepted aircraft."
    $sgAgain = Run-Engine @("--profile", $sgProfile, "relations", "suggest", "Pitch", "--min", "high", "--text")
    Assert-NotMatch $sgAgain.Output "(?m)^  - \[[a-z]+\] DCS:a2001cdnil:([^\r\n]*\|)?(FA-18C_hornet|A-10C)(\|| \| )" "relations suggest proposed an added or a 'never' command again."
    $sgAll = Run-Engine @("--profile", $sgProfile, "relations", "suggest", "--all", "--text")
    Assert-Match $sgAll.Output "(?m)^  - Pitch \| [1-9]\d* suggestions" "relations suggest --all did not list the relation."
    }

    # An entry an old auto-fill put into the wrong relation is flagged with where it belongs, and can be moved or removed.
    $mfProfile = Join-Path $tempDir "suggest\\misfit.pr0file"
    Run-Engine @("--profile", $mfProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50", "--node", "DCS:a2002cdnil:F-16C_50|A-10C", "--text") | Out-Null
    Run-Engine @("--profile", $mfProfile, "relations", "add", "Roll", "--axis", "--node", "DCS:a2002cdnil:FA-18C_hornet", "--text") | Out-Null
    $mfList = Run-Engine @("--profile", $mfProfile, "relations", "suggest", "Pitch", "--min", "high", "--text")
    Assert-True ($mfList.ExitCode -eq 0) "relations suggest with a misfit failed. Output: $($mfList.Output)"
    Assert-Match $mfList.Output "(?m)^  ! DCS:a2002cdnil:.*under 'Roll'.*Move to 'Roll'\." "relations suggest did not flag the roll axis in the pitch relation."
    $mfFix = Run-Engine @("--profile", $mfProfile, "relations", "suggest", "apply", "Pitch", "--move", "DCS:a2002cdnil:F-16C_50=>Roll", "--remove", "DCS:a2002cdnil:A-10C", "--text")
    Assert-True ($mfFix.ExitCode -eq 0) "relations suggest apply --move/--remove failed. Output: $($mfFix.Output)"
    Assert-Match $mfFix.Output "(?m)^Moved: 1\r?$" "relations suggest apply did not move the misfit."
    Assert-Match $mfFix.Output "(?m)^Removed: 1\r?$" "relations suggest apply did not remove the misfit."
    $mfRoll = Run-Engine @("--profile", $mfProfile, "relations", "show", "Roll", "--text")
    Assert-Match $mfRoll.Output "id=a2002cdnil, aircraft=F-16C_50, FA-18C_hornet" "the moved entry did not land in the Roll relation."
    $mfPitch = Run-Engine @("--profile", $mfProfile, "relations", "show", "Pitch", "--text")
    Assert-NotMatch $mfPitch.Output "a2002cdnil" "the misfit is still in the Pitch relation."

    # A stray button in an axis relation (A-10C II 'UFC 3 Key' in Pitch) seeds no suggestion, shows under "Looks
    # wrong" with a move to the button relation of its function, and does not count as "same id, different meaning".
    $stProfile = Join-Path $tempDir "suggest\\stray.pr0file"
    Run-Engine @("--profile", $stProfile, "relations", "add", "Pitch", "--axis", "--node", "DCS:a2001cdnil:F-16C_50|FA-18C_hornet", "--text") | Out-Null
    Run-Engine @("--profile", $stProfile, "relations", "add", "UFC__3", "--button", "--node", "DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C", "--text") | Out-Null
    Run-Engine @("--profile", $stProfile, "relations", "item", "add", "Pitch", "--node", "DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C II", "--text") | Out-Null
    $stList = Run-Engine @("--profile", $stProfile, "relations", "suggest", "Pitch", "--min", "low", "--text")
    Assert-True ($stList.ExitCode -eq 0) "relations suggest with a stray button failed. Output: $($stList.Output)"
    Assert-Match $stList.Output "(?m)^Function: Pitch \(pitch\)" "a stray button changed the relation's function."
    Assert-Match $stList.Output "(?m)^  ! DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C II \| 'UFC 3 Key' is a button in A-10C II; 'Pitch' is an axis relation\. Move to 'UFC__3'\." "the stray button is not flagged with a move to its button relation."
    Assert-NotMatch $stList.Output "UFC 3 Key'?\r?$|Same function as A-10C II 'UFC" "a stray button seeded suggestions."
    Assert-Match $stList.Output "(?m)^  - \[high\] DCS:a2001cdnil:" "a stray button pushed out the pitch axes."
    $stCheck = Run-Engine @("--profile", $stProfile, "relations", "check-aircraft", "--relation", "Pitch")
    Assert-Match $stCheck.Output '"OtherKindNodes":1' "check-aircraft counted the stray button's aircraft."
    $stMove = Run-Engine @("--profile", $stProfile, "relations", "suggest", "apply", "Pitch", "--move", "DCS:d1297pnilu1297cdnilvdnilvpnilvunil:A-10C II=>UFC__3", "--text")
    Assert-Match $stMove.Output "(?m)^Moved: 1\r?$" "the stray button could not move to its button relation."
    $stUfc = Run-Engine @("--profile", $stProfile, "relations", "show", "UFC__3", "--text")
    Assert-Match $stUfc.Output "id=d1297pnilu1297cdnilvdnilvpnilvunil, aircraft=A-10C, A-10C II" "the stray button did not land in its button relation."

    # One action of one aircraft is never switched on in two relations by the load correction (a tester's AH-64 force
    # trim on a MFBB and a MOZA button), and relations duplicates names and resolves one that is there. A JSON profile
    # has no DB fingerprint, so the correction runs.
    $dupDir = Join-Path $tempDir "duplicates"
    New-Item -ItemType Directory -Force -Path $dupDir | Out-Null
    $dupProfile = Join-Path $dupDir "dup.pr0file"
    $dupTrim = "d3007pnilu3007cd25vd1vpnilvu0"
    $dupJson = @'
{ "Relations": {
    "Trim Right": { "NAME": "Trim Right", "ISAXIS": false, "Groups": [], "NODES": [ { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "AH-64D_BLK_II_CPG": true, "AH-64D_BLK_II_PLT": true } } ] },
    "Mode Button 07": { "NAME": "Mode Button 07", "ISAXIS": false, "Groups": [], "NODES": [ { "ID": "d3001pnilu3001cd28vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "F-16C_50": true } }, { "ID": "d3007pnilu3007cd25vd1vpnilvu0", "Game": "DCS", "AIRCRAFT": { "FA-18C_hornet": true } } ] } },
  "Binds": {
    "Trim Right": { "Joystick": "MOZA AB9 FFB Base {021126F0-9255-11f1-8001-444553540000}", "JButton": "JOY_BTN4", "JAxis": "" },
    "Mode Button 07": { "Joystick": "Total Controls MFBB {037D9BD0-D2C1-11f0-8011-444553540000}", "JButton": "JOY_BTN39", "JAxis": "" } } }
'@
    [System.IO.File]::WriteAllText($dupProfile, $dupJson)
    $dupFixed = Run-Engine @("--profile", $dupProfile, "--load-corrections", "true", "--auto-add-db-items", "true", "--correction-mode", "balanced", "--confirm-corrections", "false", "relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:$dupTrim", "--on", "FA-18C_hornet", "--text")
    Assert-True ($dupFixed.ExitCode -eq 0) "relations aircraft set with load corrections failed. Output: $($dupFixed.Output)"
    $dupNone = Run-Engine @("--profile", $dupProfile, "--load-corrections", "false", "relations", "duplicates", "--text")
    Assert-Match $dupNone.Output "(?m)^Actions on in more than one relation: 0 " "the load correction switched an action on that another relation has. Output: $($dupNone.Output)"
    Run-Engine @("--profile", $dupProfile, "--load-corrections", "false", "relations", "aircraft", "set", "Mode Button 07", "--node", "DCS:$dupTrim", "--on", "AH-64D_BLK_II_PLT", "--text") | Out-Null
    $dupCheck = Run-Engine @("--profile", $dupProfile, "--load-corrections", "false", "--text", "profile", "validate", "--fail-on-warnings", "false")
    Assert-Match $dupCheck.Output "AH-64D Block II \(Pilot\): 'Force Trim/Hold Mode Switch - AL/Right' is in 2 relations: 'Mode Button 07' \(Total Controls MFBB . BTN 39\) and 'Trim Right' \(MOZA AB9 FFB Base . BTN 4\)\. DCS gets two binds for it\." "validate does not name the duplicate in words. Output: $($dupCheck.Output)"
    Assert-NotMatch $dupCheck.Output "${dupTrim}__" "validate shows a raw id."
    $dupKeep = Run-Engine @("--profile", $dupProfile, "--load-corrections", "false", "relations", "duplicates", "--keep", "Trim Right", "--node", "DCS:$dupTrim", "--plane", "AH-64D_BLK_II_PLT", "--text")
    Assert-True ($dupKeep.ExitCode -eq 0) "relations duplicates --keep failed. Output: $($dupKeep.Output)"
    Assert-Match $dupKeep.Output "(?m)^Actions on in more than one relation: 0 " "relations duplicates --keep left the duplicate."
    $dupShow = Run-Engine @("--profile", $dupProfile, "--load-corrections", "false", "relations", "show", "Trim Right", "--text")
    Assert-Match $dupShow.Output "aircraft=AH-64D_BLK_II_CPG, AH-64D_BLK_II_PLT" "relations duplicates --keep took the action out of the kept relation."

    # Export tiles: the games (DCS: aircraft) of the profile, and a DCS export of the ticked aircraft only.
    $tileProfile = Join-Path $tempDir "export_tiles\\tiles.pr0file"
    $tileStick = "Tile Stick {00000000-0000-0000-0000-0000000000C1}"
    Run-Engine @("--profile", $tileProfile, "relations", "add", "Tile Adv Mode", "--button", "--node", "DCS:d3015pnilunilcd2vd1vpnilvunil:F-16C_50|A-10C_2", "--device", $tileStick, "--input", "JOY_BTN3", "--text") | Out-Null
    Run-Engine @("--profile", $tileProfile, "relations", "add", "Tile Unbound", "--button", "--node", "DCS:d_unbound:F-16C_50", "--text") | Out-Null
    $tileGames = Run-Engine @("--profile", $tileProfile, "profile", "games", "--text")
    Assert-True ($tileGames.ExitCode -eq 0) "profile games failed. Output: $($tileGames.Output)"
    Assert-Match $tileGames.Output "(?m)^  DCS World \[DCS, export: dcs\]: 1 of 2 relation\(s\) bound" "profile games did not count the DCS relations."
    Assert-Match $tileGames.Output "(?m)^    - F-16C_50: 1 of 2\r?$" "profile games did not count the F-16C relations."
    Assert-Match $tileGames.Output "(?m)^    - A-10C_2: 1 of 1\r?$" "profile games did not list the A-10C."
    $tileRoot = Join-Path $tempDir "export_tiles\\saved-games"
    New-Item -ItemType Directory -Force -Path $tileRoot | Out-Null
    $tileExport = Run-Engine @("--profile", $tileProfile, "dcs", "export", $tileRoot, "--planes", "F-16C_50|NoSuchPlane", "--text")
    Assert-True ($tileExport.ExitCode -eq 0) "dcs export --planes failed. Output: $($tileExport.Output)"
    Assert-True (Test-Path (Join-Path $tileRoot "Config\\Input\\F-16C_50\\joystick")) "dcs export --planes did not write the ticked aircraft."
    Assert-True (-not (Test-Path (Join-Path $tileRoot "Config\\Input\\A-10C_2"))) "dcs export --planes wrote an aircraft that was not ticked."
    Assert-Match $tileExport.Output "Nothing to export for NoSuchPlane" "dcs export --planes did not warn about an aircraft without binds."

    # Overlay: the bound buttons of the aircraft (with the command title), and the DCS hook in a temp Saved Games folder.
    $ovMap = Run-Engine @("--profile", $tileProfile, "overlay", "map", "--plane", "F-16C_50", "--text")
    Assert-True ($ovMap.ExitCode -eq 0) "overlay map failed. Output: $($ovMap.Output)"
    Assert-Match $ovMap.Output "(?m)^Game: DCS, aircraft: F-16C_50" "overlay map did not take the aircraft."
    if (-not (Skip-WithoutGameData "overlay map shows the DCS command title")) {
    Assert-Match $ovMap.Output "(?m)^  - Tile Stick \{00000000-0000-0000-0000-0000000000C1\} JOY_BTN3: Tile Adv Mode \(ADV MODE Switch - Depressed\)" "overlay map lacks the bound button with its title."
    }
    Assert-NotMatch $ovMap.Output "Tile Unbound" "overlay map listed an unbound relation."
    $ovUnit = Run-Engine @("--profile", $tileProfile, "overlay", "map", "--unit", "A-10C_2", "--text")
    Assert-Match $ovUnit.Output "(?m)^Game: DCS, aircraft: A-10C_2" "overlay map --unit did not resolve the profile's aircraft."
    $ovHookRoot = Join-Path $tempDir "overlay\\DCS"
    New-Item -ItemType Directory -Force -Path (Join-Path $ovHookRoot "Scripts") | Out-Null
    $ovExportLua = Join-Path $ovHookRoot "Scripts\\Export.lua"
    [System.IO.File]::WriteAllText($ovExportLua, "dofile(lfs.writedir()..[[Scripts\Other.lua]])`r`n")
    $ovBefore = [System.IO.File]::ReadAllBytes($ovExportLua)
    $previousIgnoreRunning = $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = "1"
    try {
        $ovInstall = Run-Engine @("overlay", "dcs-hook", "install", "--path", $ovHookRoot, "--text")
        Assert-True ($ovInstall.ExitCode -eq 0) "overlay dcs-hook install failed. Output: $($ovInstall.Output)"
        Assert-Match $ovInstall.Output "(?m)^Installed: yes" "overlay dcs-hook install did not report the hook."
        $ovLuaText = [System.IO.File]::ReadAllText($ovExportLua)
        Assert-Match $ovLuaText "Scripts\\Other\.lua" "overlay dcs-hook install lost the other Export.lua line."
        Assert-Match $ovLuaText "pcall\(function\(\) local lfs = require\('lfs'\); dofile\(lfs\.writedir\(\) \.\. \[\[Scripts\\JoyProOverlay\.lua\]\]\) end\) -- JoyPro2 overlay" "overlay dcs-hook install did not add the protected dofile line."
        $ovHook = [System.IO.File]::ReadAllBytes((Join-Path $ovHookRoot "Scripts\\JoyProOverlay.lua"))
        Assert-True ($ovHook[0] -ne 0xEF) "the overlay hook has a BOM (DCS's Lua 5.1 rejects it)."
        $ovAgain = Run-Engine @("overlay", "dcs-hook", "install", "--path", $ovHookRoot, "--text")
        Assert-True ($ovAgain.ExitCode -eq 0) "a second overlay dcs-hook install failed. Output: $($ovAgain.Output)"
        Assert-True (([regex]::Matches([System.IO.File]::ReadAllText($ovExportLua), "JoyPro2 overlay")).Count -eq 1) "a second install added the Export.lua line twice."
        $ovRemove = Run-Engine @("overlay", "dcs-hook", "remove", "--path", $ovHookRoot, "--text")
        Assert-True ($ovRemove.ExitCode -eq 0) "overlay dcs-hook remove failed. Output: $($ovRemove.Output)"
        Assert-True (-not (Test-Path (Join-Path $ovHookRoot "Scripts\\JoyProOverlay.lua"))) "overlay dcs-hook remove left the hook file."
        $ovAfter = [System.IO.File]::ReadAllBytes($ovExportLua)
        Assert-True ([System.Linq.Enumerable]::SequenceEqual($ovBefore, $ovAfter)) "overlay dcs-hook remove did not restore Export.lua byte for byte."
        $ovBackups = Run-Engine @("backups", "list", "--game", "DCS", "--text")
        Assert-Match $ovBackups.Output "overlay dcs-hook install" "overlay dcs-hook install made no backup."
    }
    finally {
        $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $previousIgnoreRunning
    }

    # Pictures: never fails, also without DCS installed (aircraft only: game covers may come from the internet).
    $media = Run-Engine @("media", "aircraft")
    Assert-True ($media.ExitCode -eq 0) "engine media failed. Output: $($media.Output)"
    Assert-Match $media.Output '"Aircraft"' "engine media returned no aircraft list."

    # DCS swapped the MiG-21 red light ids: the old "-" id must be corrected to the new "-" command, never to "+".
    $minusProfileJson = Join-Path $tempDir "minus_sign_profile.json"
    $minusReport = Join-Path $tempDir "minus_sign_corrections.json"
@'
{
  "Relations": {
    "Red Minus": {
      "NAME": "Red Minus", "ISAXIS": false, "Groups": [],
      "NODES": [ { "ID": "dnilp3030u3030cd46vdnilvp1vu0", "Game": "DCS", "AIRCRAFT": { "MiG-21Bis": true },
                   "AllInputs": [ { "ID": "dnilp3030u3030cd46vdnilvp1vu0", "Plane": "MiG-21Bis", "Title": "Cockpit Red Lights -", "IsAxis": false, "Cat": "Lights" } ],
                   "OtherInputs": [] } ]
    }
  },
  "Binds": {}, "JoystickAliases": {}, "JoystickFileImages": {}, "JoystickLayoutExport": "", "PlaneAliases": {},
  "JoysticksPGuids": {}, "modifierNameChanges": [], "JoystickFFB": {}
}
'@ | Set-Content -Path $minusProfileJson -Encoding UTF8
    $minusLoad = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--correction-report", $minusReport, "--profile", $minusProfileJson, "relations", "auto-group", "--dry-run", "--text")
    Assert-True ($minusLoad.ExitCode -eq 0) "minus-sign correction load failed. Output: $($minusLoad.Output)"
    $minusPayload = Get-Content -Raw $minusReport
    if (-not (Skip-WithoutGameData "load correction of the MiG-21 red light ids (DCS catalog)")) {
    Assert-Match $minusPayload '"NewId"\s*:\s*"dnilp3330u3330cd46vdnilvp1vu0"' "load correction did not map the old 'Cockpit Red Lights -' id to the new '-' command."
    Assert-NotMatch $minusPayload '"NewId"\s*:\s*"dnilp3030u3030cd46vdnilvp-1vu0"' "load correction mapped 'Cockpit Red Lights -' to the '+' command."
    }

    # Auto Assign: fake devices + a small device catalog (stick with named hats, a base with two grips, a labelled panel,
    # an unknown throttle). Checks identification, grip choice, exact positions, labelled switches with their direction,
    # one input shared by relations of different aircraft, apply, kept binds and the user's style on --overwrite.
    $aaFixtureDir = Join-Path $engineDir "tests\\fixtures\\autoassign"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $previousDeviceKb = $env:JOYPRO_DEVICE_KB
    $env:JOYPRO_FAKE_DEVICES = Join-Path $aaFixtureDir "devices.tsv"
    $env:JOYPRO_DEVICE_KB = Join-Path $aaFixtureDir "kb"
    try {
        $aaList = Run-Engine @("devices", "kb", "list", "grip", "--text")
        Assert-True ($aaList.ExitCode -eq 0) "devices kb list failed. Output: $($aaList.Output)"
        Assert-Match $aaList.Output "(?m)^  - aa-grip-a \| AA Grip A \| stick \| grip of aa-base" "devices kb list did not find the grip of the test base."
        $aaShow = Run-Engine @("devices", "kb", "show", "aa-panel", "--text")
        Assert-Match $aaShow.Output "JOY_BTN4 = panel.toggle.1.down `"LAUNCH BAR EXTEND`"" "devices kb show did not list the labelled panel switch."
        $aaIdentify = Run-Engine @("devices", "kb", "identify", "--text")
        Assert-True ($aaIdentify.ExitCode -eq 0) "devices kb identify failed. Output: $($aaIdentify.Output)"
        Assert-Match $aaIdentify.Output "AA Test Base \{[^}]+\} => grip needed" "devices kb identify did not ask for the grip of a base with several grips."
        Assert-Match $aaIdentify.Output "grip: aa-grip-b \| AA Grip B \| https://example.invalid/grip-b.png" "devices kb identify did not offer the grip with its picture."
        Assert-Match $aaIdentify.Output "AA Test Throttle \{[^}]+\} => .*\[throttle\] \(generic\)" "devices kb identify did not give the unknown throttle a generic layout."

        $aaProfile = Join-Path $tempDir "autoassign\\profile.json"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $aaProfile) | Out-Null
        Copy-Item (Join-Path $aaFixtureDir "profile.json") $aaProfile -Force
        $aaGrip = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--profile", $aaProfile, "relations", "auto-assign", "--grip", "AA Test Base=aa-grip-b", "--dry-run", "--text")
        Assert-True ($aaGrip.ExitCode -eq 0) "relations auto-assign with a grip choice failed. Output: $($aaGrip.Output)"
        Assert-Match $aaGrip.Output "AA Test Base \{[^}]+\} => AA Grip B \[stick\] - left out, same role as AA Test Stick" "a second right-hand stick was not left out as an alternative."

        $aaDry = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--profile", $aaProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--dry-run", "--explain", "GearIn", "--text")
        Assert-True ($aaDry.ExitCode -eq 0) "relations auto-assign --dry-run failed. Output: $($aaDry.Output)"
        Assert-Match $aaDry.Output "(?m)^Assigned: 10\r?$" "relations auto-assign did not place all ten test relations."
        Assert-Match $aaDry.Output "(?m)^  - Pitch => AA Test Stick \{[^}]+\} JOY_Y \(stick.axis.pitch\)" "Pitch did not land on the stick's pitch axis."
        Assert-Match $aaDry.Output "(?m)^  - Target Management System Up => AA Test Stick \{[^}]+\} JOY_BTN4 \(stick.hat.tms.up\)" "TMS up did not land on the TMS hat."
        Assert-Match $aaDry.Output "(?m)^  - TrimLeft => AA Test Stick \{[^}]+\} JOY_BTN_POV1_L \(stick.hat.trim.left\)" "Trim left did not land on the trim hat."
        Assert-Match $aaDry.Output "(?m)^  - GearIn => AA Test Panel \{[^}]+\} JOY_BTN1 \(panel.lever.gear.up" "Gear up did not land on the gear lever's up position."
        Assert-Match $aaDry.Output "(?m)^  - LaunchBarExtend => AA Test Panel \{[^}]+\} JOY_BTN4 \(panel.toggle.1.down `"LAUNCH BAR EXTEND`"\)" "Launch bar extend did not follow the switch label."
        $aaThrottle = [regex]::Match($aaDry.Output, "(?m)^  - ThrottleLeft => (.+?)\r?$").Groups[1].Value
        Assert-True ($aaThrottle.Length -gt 0) "ThrottleLeft was not assigned."
        Assert-Match $aaDry.Output ("(?m)^  - ThrottleLeft29 => " + [regex]::Escape($aaThrottle) + "\r?$") "relations of different aircraft did not share one throttle axis."
        Assert-Match $aaDry.Output "(?m)^Explain:" "relations auto-assign --explain printed no explanation."
        Assert-NotMatch $aaDry.Output "Trigger|weapon_release" "a function landed on the trigger / weapon release without asking for it."

        $aaApply = Run-Engine @("--confirm-corrections", "false", "--confirm-device-remap", "false", "--profile", $aaProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--text")
        Assert-True ($aaApply.ExitCode -eq 0) "relations auto-assign failed. Output: $($aaApply.Output)"
        $aaBinds = Run-Engine @("--profile", $aaProfile, "binds", "list", "--text")
        Assert-Match $aaBinds.Output "(?m)^Pitch \| type=axis \| device=AA Test Stick \{[^}]+\} \| input=JOY_Y" "auto-assign did not save the Pitch bind."
        $aaAgain = Run-Engine @("--confirm-corrections", "false", "--profile", $aaProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--dry-run", "--text")
        Assert-Match $aaAgain.Output "(?m)^Kept Existing Binds: 10\r?$" "auto-assign did not keep the existing binds."
        Assert-Match $aaAgain.Output "(?m)^Assigned: 0\r?$" "auto-assign re-assigned relations that are already bound."
        $aaStyle = Run-Engine @("--confirm-corrections", "false", "--profile", $aaProfile, "relations", "auto-assign", "--ignore-device", "AA Test Base", "--overwrite", "--dry-run", "--text")
        Assert-Match $aaStyle.Output "(?m)^Assigned: 10 \(10 on the same control as before\)\r?$" "auto-assign --overwrite did not keep the user's layout."
        $aaBad = Run-Engine @("--profile", $aaProfile, "relations", "auto-assign", "--max-modifiers", "3", "--dry-run", "--text")
        Assert-True ($aaBad.ExitCode -ne 0) "relations auto-assign accepted more than two modifiers."

        # HUD: each connected device with its inputs, the role of its axes and the grips a base takes.
        $hudText = Run-Engine @("hud", "devices", "--text")
        Assert-True ($hudText.ExitCode -eq 0) "hud devices failed. Output: $($hudText.Output)"
        Assert-Match $hudText.Output "(?m)^HUD Devices: 4\r?$" "hud devices did not list the four connected devices."
        Assert-Match $hudText.Output "Inputs: 16 buttons, 1 hats, axes JOY_X=Roll, JOY_Y=Pitch, JOY_Z=Twist" "hud devices lacks the stick's axis roles."
        Assert-Match $hudText.Output "(?m)^    Grips: aa-grip-a, aa-grip-b\r?$" "hud devices did not offer the base's grips."
        $hudGrip = Run-Engine @("hud", "devices", "--grip", "AA Test Base=aa-grip-a")
        Assert-True ($hudGrip.ExitCode -eq 0) "hud devices --grip failed. Output: $($hudGrip.Output)"
        Assert-Match $hudGrip.Output '"ModelId":"aa-grip-a"' "hud devices --grip did not use the picked grip."
        Assert-Match $hudGrip.Output '"Image":"https://example.invalid/grip-a' "hud devices did not give the grip's picture."
        $hudBad = Run-Engine @("hud", "devices", "--grip", "no-equals-sign")
        Assert-True ($hudBad.ExitCode -ne 0) "hud devices accepted a --grip without device=id."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
        $env:JOYPRO_DEVICE_KB = $previousDeviceKb
    }

    $roundTripProfile = Join-Path $tempDir "roundtrip\\nested\\import_then_edit.pr0file"
    $roundTripImport = Run-Engine @("dcs", "import", $savedGames, "--text", "--save-profile", $roundTripProfile)
    Assert-True ($roundTripImport.ExitCode -eq 0) "engine dcs import into roundtrip profile failed."
    Assert-True (Test-Path $roundTripProfile) "engine dcs import did not create roundtrip profile path."
    $roundTripAddRelation = Run-Engine @("--profile", $roundTripProfile, "relations", "add", "Roundtrip Add Relation", "--button", "--node", "DCS:custom_roundtrip_id:F-16C_50", "--device", "Smoke Stick", "--input", "JOY_BTN7", "--text")
    Assert-True ($roundTripAddRelation.ExitCode -eq 0) "engine relations add with bind for roundtrip profile failed."
    $roundTripAddGroup = Run-Engine @("--profile", $roundTripProfile, "relations", "group", "add", "Roundtrip Add Relation", "PersistedGroup", "--text")
    Assert-True ($roundTripAddGroup.ExitCode -eq 0) "engine relations group add for roundtrip profile failed."
    $roundTripShowRelation = Run-Engine @("--profile", $roundTripProfile, "relations", "show", "Roundtrip Add Relation", "--text")
    Assert-True ($roundTripShowRelation.ExitCode -eq 0) "engine relations show for roundtrip profile failed."
    Assert-True ($roundTripShowRelation.Output -match "(?m)^Groups:\s+PersistedGroup") "roundtrip relation group was not persisted."
    Assert-True ($roundTripShowRelation.Output -match "id=custom_roundtrip_id, aircraft=F-16C_50") "roundtrip relation node was not persisted."
    Assert-True ($roundTripShowRelation.Output -match "joystick=Smoke Stick, input=JOY_BTN7") "roundtrip relation bind was not persisted."
    $roundTripShowBind = Run-Engine @("--profile", $roundTripProfile, "binds", "show", "Roundtrip Add Relation", "--text")
    Assert-True ($roundTripShowBind.ExitCode -eq 0) "engine binds show for roundtrip profile failed."
    Assert-True ($roundTripShowBind.Output -match "joystick=Smoke Stick, input=JOY_BTN7") "roundtrip bind lookup did not match expected persisted input."

    $validationProfile = Join-Path $tempDir "validation\\validation.pr0file"
    $validationWarnOne = Run-Engine @("--profile", $validationProfile, "relations", "add", "Validation Warn One", "--button", "--node", "DCS:val_warn_node:A-10C_2", "--device", "Validation Stick A", "--input", "JOY_BTN1", "--text")
    Assert-True ($validationWarnOne.ExitCode -eq 0) "profile validate fixture: failed creating warning relation one."
    $validationWarnTwo = Run-Engine @("--profile", $validationProfile, "relations", "add", "Validation Warn Two", "--button", "--node", "DCS:val_warn_node:A-10C_2", "--device", "Validation Stick B", "--input", "JOY_BTN2", "--text")
    Assert-True ($validationWarnTwo.ExitCode -eq 0) "profile validate fixture: failed creating warning relation two."

    $profileValidateWarnings = Run-Engine @("--profile", $validationProfile, "profile", "validate", "--text")
    Assert-True ($profileValidateWarnings.ExitCode -eq 0) "engine profile validate should pass when only warnings exist."
    Assert-True ($profileValidateWarnings.Output -match "(?m)^Profile Validation\s*$") "profile validate text output missing header."
    Assert-True ($profileValidateWarnings.Output -match "(?m)^Warnings:\s+[1-9]\d*\s*$") "profile validate did not report warnings."
    Assert-True ($profileValidateWarnings.Output -match "(?m)^Errors:\s+0\s*$") "profile validate should report zero errors for warning-only fixture."
    Assert-True ($profileValidateWarnings.Output -match "(?m)^Relation Warnings:\s+[1-9]\d*\s*$") "profile validate did not report relation warning bucket."

    $profileValidateWarningsFail = Run-Engine @("--profile", $validationProfile, "profile", "validate", "--fail-on-warnings", "true", "--text")
    Assert-True ($profileValidateWarningsFail.ExitCode -ne 0) "engine profile validate --fail-on-warnings true should fail when warnings exist."
    Assert-True ($profileValidateWarningsFail.Output -match "profile validation failed: warnings") "profile validate --fail-on-warnings failure reason missing."

    $validationErrorOne = Run-Engine @("--profile", $validationProfile, "relations", "add", "Validation Error One", "--button", "--node", "DCS:val_error_a:A-10C_2", "--device", "Validation Stick C", "--input", "JOY_BTN3", "--text")
    Assert-True ($validationErrorOne.ExitCode -eq 0) "profile validate fixture: failed creating error relation one."
    $validationErrorTwo = Run-Engine @("--profile", $validationProfile, "relations", "add", "Validation Error Two", "--button", "--node", "DCS:val_error_b:A-10C_2", "--device", "Validation Stick C", "--input", "JOY_BTN3", "--text")
    Assert-True ($validationErrorTwo.ExitCode -eq 0) "profile validate fixture: failed creating error relation two."

    $profileValidateErrors = Run-Engine @("--profile", $validationProfile, "profile", "validate", "--text")
    Assert-True ($profileValidateErrors.ExitCode -ne 0) "engine profile validate should fail when errors exist."
    Assert-True ($profileValidateErrors.Output -match "(?m)^Errors:\s+[1-9]\d*\s*$") "profile validate did not report error count for error fixture."
    Assert-True ($profileValidateErrors.Output -match "(?m)^Bind Errors:\s+[1-9]\d*\s*$") "profile validate did not report bind error bucket."
    Assert-True ($profileValidateErrors.Output -match "profile validation failed: errors") "profile validate error failure reason missing."

    $mergeBaseProfile = Join-Path $tempDir "profile_merge\\base.pr0file"
    $mergeLeftProfile = Join-Path $tempDir "profile_merge\\left.pr0file"
    $mergeRightProfile = Join-Path $tempDir "profile_merge\\right.pr0file"
    $mergeOutRightProfile = Join-Path $tempDir "profile_merge\\merged_right.pr0file"
    $mergeOutLeftProfile = Join-Path $tempDir "profile_merge\\merged_left.pr0file"
    $mergeSharedName = "Merge Shared Smoke"
    $mergeLeftOnlyName = "Merge Left Only Smoke"
    $mergeRightOnlyName = "Merge Right Only Smoke"

    $mergeBaseShared = Run-Engine @("--profile", $mergeBaseProfile, "relations", "add", $mergeSharedName, "--button", "--node", "DCS:merge_shared_smoke:A-10C_2", "--device", "Merge Left Stick", "--input", "JOY_BTN1", "--text")
    Assert-True ($mergeBaseShared.ExitCode -eq 0) "profile merge fixture: failed creating base shared relation."
    $mergeBaseLeftOnly = Run-Engine @("--profile", $mergeBaseProfile, "relations", "add", $mergeLeftOnlyName, "--button", "--node", "DCS:merge_left_only_smoke:A-10C_2", "--device", "Merge Left Stick", "--input", "JOY_BTN2", "--text")
    Assert-True ($mergeBaseLeftOnly.ExitCode -eq 0) "profile merge fixture: failed creating base left-only relation."
    $mergeSaveLeft = Run-Engine @("--profile", $mergeBaseProfile, "profile", "save", $mergeLeftProfile)
    Assert-True ($mergeSaveLeft.ExitCode -eq 0) "profile merge fixture: failed saving left profile."
    $mergeSaveRight = Run-Engine @("--profile", $mergeBaseProfile, "profile", "save", $mergeRightProfile)
    Assert-True ($mergeSaveRight.ExitCode -eq 0) "profile merge fixture: failed saving right profile."

    $mergeRightBindSet = Run-Engine @("--profile", $mergeRightProfile, "binds", "set", $mergeSharedName, "--device", "Merge Right Stick", "--input", "JOY_BTN9", "--text")
    Assert-True ($mergeRightBindSet.ExitCode -eq 0) "profile merge fixture: failed mutating right shared bind."
    $mergeRightOnlyAdd = Run-Engine @("--profile", $mergeRightProfile, "relations", "add", $mergeRightOnlyName, "--button", "--node", "DCS:merge_right_only_smoke:A-10C_2", "--device", "Merge Right Stick", "--input", "JOY_BTN8", "--text")
    Assert-True ($mergeRightOnlyAdd.ExitCode -eq 0) "profile merge fixture: failed adding right-only relation."

    $profileMergeRight = Run-Engine @("profile", "merge", $mergeLeftProfile, $mergeRightProfile, "--mode", "right", "--save", $mergeOutRightProfile)
    Assert-True ($profileMergeRight.ExitCode -eq 0) "engine profile merge --mode right failed."
    Assert-True ($profileMergeRight.Output -match "Conflict mode:\s+right") "profile merge right mode output missing mode marker."
    Assert-True ($profileMergeRight.Output -match "Conflicts:\s+1") "profile merge right mode did not report expected conflict count."
    Assert-True (Test-Path $mergeOutRightProfile) "profile merge right mode did not create output profile."

    $profileMergeLeft = Run-Engine @("profile", "merge", $mergeLeftProfile, $mergeRightProfile, "--mode", "left", "--save", $mergeOutLeftProfile)
    Assert-True ($profileMergeLeft.ExitCode -eq 0) "engine profile merge --mode left failed."
    Assert-True ($profileMergeLeft.Output -match "Conflict mode:\s+left") "profile merge left mode output missing mode marker."
    Assert-True ($profileMergeLeft.Output -match "Conflicts:\s+1") "profile merge left mode did not report expected conflict count."
    Assert-True (Test-Path $mergeOutLeftProfile) "profile merge left mode did not create output profile."

    $mergeShowSharedRight = Run-Engine @("--profile", $mergeOutRightProfile, "binds", "show", $mergeSharedName, "--text")
    Assert-True ($mergeShowSharedRight.ExitCode -eq 0) "profile merge right mode bind lookup failed."
    Assert-True ($mergeShowSharedRight.Output -match "joystick=Merge Right Stick, input=JOY_BTN9") "profile merge right mode did not apply right-side conflict overwrite."
    $mergeShowSharedLeft = Run-Engine @("--profile", $mergeOutLeftProfile, "binds", "show", $mergeSharedName, "--text")
    Assert-True ($mergeShowSharedLeft.ExitCode -eq 0) "profile merge left mode bind lookup failed."
    Assert-True ($mergeShowSharedLeft.Output -match "joystick=Merge Left Stick, input=JOY_BTN1") "profile merge left mode did not keep left-side conflict value."

    $mergeShowRightOnlyInRight = Run-Engine @("--profile", $mergeOutRightProfile, "relations", "show", $mergeRightOnlyName, "--text")
    Assert-True ($mergeShowRightOnlyInRight.ExitCode -eq 0) "profile merge right mode missing right-only relation."
    Assert-True ($mergeShowRightOnlyInRight.Output -match "id=merge_right_only_smoke") "profile merge right mode did not preserve right-only relation node."
    $mergeShowRightOnlyInLeft = Run-Engine @("--profile", $mergeOutLeftProfile, "relations", "show", $mergeRightOnlyName, "--text")
    Assert-True ($mergeShowRightOnlyInLeft.ExitCode -eq 0) "profile merge left mode missing non-conflicting right-only relation."
    Assert-True ($mergeShowRightOnlyInLeft.Output -match "id=merge_right_only_smoke") "profile merge left mode did not preserve non-conflicting right-only relation."

    $massProfile = Join-Path $tempDir "mass_ops\\mass_ops.pr0file"
    $massAddOne = Run-Engine @("--profile", $massProfile, "relations", "add", "Mass Btn One", "--button", "--node", "DCS:mass_btn_one:F-16C_50", "--device", "Mass Stick", "--input", "JOY_BTN1", "--group", "Mass", "--text")
    Assert-True ($massAddOne.ExitCode -eq 0) "engine relations add for mass profile (one) failed."
    $massAddTwo = Run-Engine @("--profile", $massProfile, "relations", "add", "Mass Btn Two", "--button", "--node", "DCS:mass_btn_two:F-16C_50", "--device", "Mass Stick", "--input", "JOY_BTN2", "--group", "Mass", "--text")
    Assert-True ($massAddTwo.ExitCode -eq 0) "engine relations add for mass profile (two) failed."
    $massAddAxis = Run-Engine @("--profile", $massProfile, "relations", "add", "Mass Axis", "--axis", "--node", "DCS:mass_axis:F-16C_50", "--device", "Mass Stick", "--input", "JOY_X", "--group", "MassAxis", "--text")
    Assert-True ($massAddAxis.ExitCode -eq 0) "engine relations add for mass profile (axis) failed."

    $massPrefix = Run-Engine @("--profile", $massProfile, "relations", "mass", "add-prefix", "PRE_", "--relation", "Mass Btn One", "--relation", "Mass Btn Two", "--text")
    Assert-True ($massPrefix.ExitCode -eq 0) "engine relations mass add-prefix failed."
    Assert-True ($massPrefix.Output -match "(?m)^Renamed Relations:\s+2") "relations mass add-prefix did not rename expected relation count."

    $massDuplicate = Run-Engine @("--profile", $massProfile, "relations", "mass", "duplicate", "--relation", "PRE_Mass Btn One", "--text")
    Assert-True ($massDuplicate.ExitCode -eq 0) "engine relations mass duplicate failed."
    Assert-True ($massDuplicate.Output -match "(?m)^Created Relations:\s+1") "relations mass duplicate did not create expected relation count."

    $massMerge = Run-Engine @("--profile", $massProfile, "relations", "mass", "merge", "Merged Mass", "--relations", "PRE_Mass Btn One,PRE_Mass Btn Two", "--text")
    Assert-True ($massMerge.ExitCode -eq 0) "engine relations mass merge failed."
    Assert-True ($massMerge.Output -match "(?m)^Created Relations:\s+1") "relations mass merge did not create merged relation."
    Assert-True ($massMerge.Output -match "Merged Mass") "relations mass merge output missing merged relation name."

    $massSplit = Run-Engine @("--profile", $massProfile, "relations", "mass", "split", "Split", "--plane", "DCS:F-16C_50", "--relation", "Merged Mass", "--text")
    Assert-True ($massSplit.ExitCode -eq 0) "engine relations mass split failed."
    Assert-True ($massSplit.Output -match "(?m)^Created Relations:\s+1") "relations mass split did not create expected relation count."
    Assert-True ($massSplit.Output -match "Merged Mass_Split") "relations mass split output missing expected split relation name."

    $massRemovePart = Run-Engine @("--profile", $massProfile, "relations", "mass", "remove-part", "PRE_", "--query", "PRE_", "--text")
    Assert-True ($massRemovePart.ExitCode -eq 0) "engine relations mass remove-part failed."
    Assert-True ($massRemovePart.Output -match "(?m)^Renamed Relations:\s+[1-9]\d*") "relations mass remove-part did not rename expected relations."

    $massExportRl = Join-Path $tempDir "mass_ops\\exported_relations.rl"
    $massExport = Run-Engine @("--profile", $massProfile, "relations", "mass", "export", $massExportRl, "--all", "--text")
    Assert-True ($massExport.ExitCode -eq 0) "engine relations mass export failed."
    Assert-True (Test-Path $massExportRl) "relations mass export did not create .rl file."
    $massExportFileInfo = Get-Item $massExportRl
    Assert-True ($massExportFileInfo.Length -gt 0) "relations mass export wrote empty .rl file."

    $massDelete = Run-Engine @("--profile", $massProfile, "relations", "mass", "delete", "--query", "Mass Axis", "--text")
    Assert-True ($massDelete.ExitCode -eq 0) "engine relations mass delete failed."
    Assert-True ($massDelete.Output -match "(?m)^Deleted Relations:\s+1") "relations mass delete did not delete expected relation count."

    $groupAdd = Run-Engine @("--profile", $importProfile, "relations", "group", "add", "JP Trigger Relation", "Ops", "--text")
    Assert-True ($groupAdd.ExitCode -eq 0) "engine relations group add failed."
    Assert-True ($groupAdd.Output -match "(?m)^Groups:\s+.*(Combat.*Ops|Ops.*Combat)") "relations group add did not update groups as expected."

    $groupRemove = Run-Engine @("--profile", $importProfile, "relations", "group", "remove", "JP Trigger Relation", "Combat", "--text")
    Assert-True ($groupRemove.ExitCode -eq 0) "engine relations group remove failed."
    Assert-True ($groupRemove.Output -match "(?m)^Groups:\s+Ops") "relations group remove did not keep persisted non-removed group."
    $groupListRelation = Run-Engine @("--profile", $importProfile, "relations", "group", "list", "JP Trigger Relation", "--text")
    Assert-True ($groupListRelation.ExitCode -eq 0) "engine relations group list <relation> failed."
    Assert-True ($groupListRelation.Output -match "(?m)^Groups:\s+1") "relations group list <relation> did not return expected group count."
    Assert-True ($groupListRelation.Output -match "(?m)^\s+-\s+Ops") "relations group list <relation> missing expected group name."
    $groupListGlobal = Run-Engine @("--profile", $importProfile, "relations", "group", "list", "--text")
    Assert-True ($groupListGlobal.ExitCode -eq 0) "engine relations group list failed."
    Assert-True ($groupListGlobal.Output -match "(?m)^\s+-\s+Ops\s+\(") "relations group list global missing expected group usage."
    $groupRename = Run-Engine @("--profile", $importProfile, "relations", "group", "rename", "Ops", "Operations", "--text")
    Assert-True ($groupRename.ExitCode -eq 0) "engine relations group rename failed."
    Assert-True ($groupRename.Output -match "(?m)^Updated Relations:\s+[1-9]\d*") "relations group rename did not report updated relations."
    $groupShowAfterRename = Run-Engine @("--profile", $importProfile, "relations", "show", "JP Trigger Relation", "--text")
    Assert-True ($groupShowAfterRename.ExitCode -eq 0) "engine relations show after group rename failed."
    Assert-True ($groupShowAfterRename.Output -match "(?m)^Groups:\s+Operations") "relations group rename did not persist renamed group."

    $modifierAdd = Run-Engine @("--profile", $importProfile, "binds", "modifier", "add", "JP Trigger Relation", "LShift", "--text")
    Assert-True ($modifierAdd.ExitCode -eq 0) "engine binds modifier add failed."
    Assert-True ($modifierAdd.Output -match "(?m)^Bind Reformers:\s+.*(LAlt.*LShift|LShift.*LAlt)") "binds modifier add did not include expected modifiers."

    $modifierRemove = Run-Engine @("--profile", $importProfile, "binds", "modifier", "remove", "JP Trigger Relation", "LAlt", "--text")
    Assert-True ($modifierRemove.ExitCode -eq 0) "engine binds modifier remove failed."
    Assert-True ($modifierRemove.Output -match "(?m)^Bind Reformers:\s+LShift") "binds modifier remove did not preserve persisted non-removed modifier."
    $modifierListBind = Run-Engine @("--profile", $importProfile, "binds", "modifier", "list", "JP Trigger Relation", "--text")
    Assert-True ($modifierListBind.ExitCode -eq 0) "engine binds modifier list <relation> failed."
    Assert-True ($modifierListBind.Output -match "(?m)^Modifiers:\s+1") "binds modifier list <relation> did not report expected modifier count."
    Assert-True ($modifierListBind.Output -match "(?m)^\s+-\s+LShift") "binds modifier list <relation> missing expected modifier."
    $modifierListGlobal = Run-Engine @("--profile", $importProfile, "binds", "modifier", "list", "--text")
    Assert-True ($modifierListGlobal.ExitCode -eq 0) "engine binds modifier list failed."
    Assert-True ($modifierListGlobal.Output -match "(?m)^\s+-\s+LShift\s+\(") "binds modifier list global missing expected modifier usage."
    $modifierRenameGlobal = Run-Engine @("--profile", $importProfile, "binds", "modifier", "rename", "LShift", "LShiftHold", "--text")
    Assert-True ($modifierRenameGlobal.ExitCode -eq 0) "engine binds modifier rename failed."
    Assert-True ($modifierRenameGlobal.Output -match "(?m)^Updated Relations:\s+[1-9]\d*") "binds modifier rename did not report updated relations."
    $modifierShowAfterRename = Run-Engine @("--profile", $importProfile, "binds", "show", "JP Trigger Relation", "--text")
    Assert-True ($modifierShowAfterRename.ExitCode -eq 0) "engine binds show after modifier rename failed."
    Assert-True ($modifierShowAfterRename.Output -match "(?m)^Bind Reformers:\s+LShiftHold") "binds modifier rename did not persist renamed modifier."

    $bindSetButton = Run-Engine @("--profile", $importProfile, "binds", "set", "JP Trigger Relation", "--device", "Smoke Stick", "--input", "JOY_BTN9", "--double-tap", "true", "--text")
    Assert-True ($bindSetButton.ExitCode -eq 0) "engine binds set on button relation failed."
    Assert-True ($bindSetButton.Output -match "joystick=Smoke Stick, input=JOY_BTN9") "binds set did not update button bind input/device."
    Assert-True ($bindSetButton.Output -match "(?m)^Bind Inverted/Slider/DoubleTap:\s+false\s+/\s+false\s+/\s+true") "binds set did not report updated double-tap value."
    $bindSetModifierViaSet = Run-Engine @("--profile", $importProfile, "binds", "set", "JP Trigger Relation", "--modifier", "LAlt", "--text")
    Assert-True ($bindSetModifierViaSet.ExitCode -eq 0) "engine binds set --modifier failed."
    Assert-True ($bindSetModifierViaSet.Output -match "(?m)^Bind Reformers:\s+LAlt") "binds set --modifier did not apply modifier replacement."
    $bindClearModifiersViaSet = Run-Engine @("--profile", $importProfile, "binds", "set", "JP Trigger Relation", "--clear-modifiers", "--text")
    Assert-True ($bindClearModifiersViaSet.ExitCode -eq 0) "engine binds set --clear-modifiers failed."
    Assert-True ($bindClearModifiersViaSet.Output -match "(?m)^Bind Reformers:\s+<none>") "binds set --clear-modifiers did not remove all modifiers."

    $bindSetAxis = Run-Engine @("--profile", $importProfile, "binds", "set", "JP Pitch Relation", "--device", "Smoke Axis", "--input", "JOY_X", "--inverted", "true", "--slider", "true", "--curvature", "0.1,0.2", "--deadzone", "0.15", "--saturation-x", "0.9", "--saturation-y", "0.8", "--text")
    Assert-True ($bindSetAxis.ExitCode -eq 0) "engine binds set on axis relation failed."
    Assert-True ($bindSetAxis.Output -match "joystick=Smoke Axis, input=JOY_X") "binds set did not update axis bind input/device."
    Assert-True ($bindSetAxis.Output -match "(?m)^Bind Inverted/Slider/DoubleTap:\s+true\s+/\s+true\s+/\s+false") "binds set did not apply inverted/slider settings."
    Assert-True ($bindSetAxis.Output -match "(?m)^Bind Curvature:\s+0.1,\s+0.2") "binds set did not apply curvature values."
    Assert-True ($bindSetAxis.Output -match "(?m)^Bind Deadzone/SatX/SatY:\s+0.15\s+/\s+0.9\s+/\s+0.8") "binds set did not apply deadzone/saturation values."

    $bindClearInputOnly = Run-Engine @("--profile", $importProfile, "binds", "clear", "JP Trigger Relation", "--input-only", "--text")
    Assert-True ($bindClearInputOnly.ExitCode -eq 0) "engine binds clear --input-only failed."
    Assert-True ($bindClearInputOnly.Output -match "(?m)^Input Only:\s+True") "binds clear --input-only output missing mode flag."
    Assert-True ($bindClearInputOnly.Output -match "(?m)^Cleared Input:\s+True") "binds clear --input-only did not clear input."
    $bindShowAfterClearInput = Run-Engine @("--profile", $importProfile, "binds", "show", "JP Trigger Relation", "--text")
    Assert-True ($bindShowAfterClearInput.ExitCode -eq 0) "engine binds show after clear --input-only failed."
    Assert-True ($bindShowAfterClearInput.Output -match "joystick=Smoke Stick, input=<none>") "bind clear --input-only did not keep bind while clearing input."

    $bindClearFull = Run-Engine @("--profile", $importProfile, "binds", "clear", "JP Trigger Relation", "--text")
    Assert-True ($bindClearFull.ExitCode -eq 0) "engine binds clear failed."
    Assert-True ($bindClearFull.Output -match "(?m)^Removed Bind:\s+True") "binds clear did not remove bind as expected."
    $relationAfterClear = Run-Engine @("--profile", $importProfile, "relations", "show", "JP Trigger Relation", "--text")
    Assert-True ($relationAfterClear.ExitCode -eq 0) "engine relations show after binds clear failed."
    Assert-True ($relationAfterClear.Output -match "(?m)^Bind:\s+<none>") "binds clear did not detach bind from relation."
    $bindClearAgain = Run-Engine @("--profile", $importProfile, "binds", "clear", "JP Trigger Relation", "--text")
    Assert-True ($bindClearAgain.ExitCode -eq 0) "engine binds clear should be safe when relation is already unbound."
    Assert-True ($bindClearAgain.Output -notmatch "NullReferenceException") "engine binds clear threw null reference on already-unbound relation."
    $unbindWithNullPresent = Run-Engine @("--profile", $importProfile, "binds", "unbind", "--input", "JOY_X", "--format", "dcs", "--all", "--text")
    Assert-True ($unbindWithNullPresent.ExitCode -eq 0) "engine binds unbind should still work when another relation is already unbound."
    Assert-True ($unbindWithNullPresent.Output -match "Updated Relations:\s+[1-9]\d*") "engine binds unbind did not report updated relations with null bind present."
    Assert-True ($unbindWithNullPresent.Output -notmatch "NullReferenceException") "engine binds unbind threw null reference when null bind existed in profile."
    $restorePitchBind = Run-Engine @("--profile", $importProfile, "binds", "set", "JP Pitch Relation", "--device", "Smoke Axis", "--input", "JOY_X", "--inverted", "true", "--slider", "true", "--curvature", "0.1,0.2", "--deadzone", "0.15", "--saturation-x", "0.9", "--saturation-y", "0.8", "--text")
    Assert-True ($restorePitchBind.ExitCode -eq 0) "engine binds set restore after unbind null-safety check failed."

    $deviceOpsAdd = Run-Engine @("--profile", $importProfile, "relations", "add", "Device Ops Relation", "--axis", "--node", "DCS:device_ops_axis:F-16C_50", "--device", "Device Ops Old Stick", "--input", "JOY_Z", "--text")
    Assert-True ($deviceOpsAdd.ExitCode -eq 0) "engine relations add for device ops relation failed."
    $deviceOpsSeed = Run-Engine @("--profile", $importProfile, "relations", "add", "Device Ops Seed", "--button", "--node", "DCS:device_ops_seed:F-16C_50", "--device", "Device Ops New Stick", "--input", "JOY_BTN6", "--text")
    Assert-True ($deviceOpsSeed.ExitCode -eq 0) "engine relations add for device ops seed relation failed."
    $deviceOpsFfbSet = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "set", "Device Ops Old Stick", "--invert-x", "true", "--invert-y", "false", "--swap-axis", "true", "--confirm", "false", "--text")
    Assert-True ($deviceOpsFfbSet.ExitCode -eq 0) "engine dcs ffb set for device ops relation failed."

    $deviceExchange = Run-Engine @("--profile", $importProfile, "devices", "exchange", "Device Ops Old Stick", "Device Ops New Stick", "--confirm", "false", "--text")
    Assert-True ($deviceExchange.ExitCode -eq 0) "engine devices exchange failed."
    Assert-True ($deviceExchange.Output -match "(?m)^Updated Binds \(Device\):\s+[1-9]\d*") "devices exchange did not update bind joystick references."
    Assert-True ($deviceExchange.Output -match "(?m)^Updated Joystick FFB:\s+[1-9]\d*") "devices exchange did not update FFB device references."
    $deviceOpsShowAfterExchange = Run-Engine @("--profile", $importProfile, "binds", "show", "Device Ops Relation", "--text")
    Assert-True ($deviceOpsShowAfterExchange.ExitCode -eq 0) "engine binds show for exchanged device relation failed."
    Assert-True ($deviceOpsShowAfterExchange.Output -match "joystick=Device Ops New Stick") "devices exchange did not persist new device name on bind."
    $deviceOpsFfbAfterExchange = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--device", "Device Ops New Stick", "--confirm", "false", "--text")
    Assert-True ($deviceOpsFfbAfterExchange.ExitCode -eq 0) "engine dcs ffb list after devices exchange failed."
    Assert-True ($deviceOpsFfbAfterExchange.Output -match "hasSetting=true") "devices exchange did not move FFB settings to new device."

    # New Windows install: same device, new instance GUID. Exchange must move only that instance, not a second
    # device of the same model.
    $guidProfile = Join-Path $tempDir "exchange_guid.pr0file"
    $guidOld = "Twin Stick {AAAAAAAA-0000-0000-0000-000000000001}"
    $guidOther = "Twin Stick {AAAAAAAA-0000-0000-0000-000000000002}"
    $guidNew = "Twin Stick {BBBBBBBB-0000-0000-0000-000000000001}"
    $guidA = Run-Engine @("--profile", $guidProfile, "relations", "add", "Twin A", "--button", "--node", "DCS:twin_a:F-16C_50", "--device", $guidOld, "--input", "JOY_BTN1", "--text")
    Assert-True ($guidA.ExitCode -eq 0) "relations add for the GUID exchange check failed."
    $guidB = Run-Engine @("--profile", $guidProfile, "relations", "add", "Twin B", "--button", "--node", "DCS:twin_b:F-16C_50", "--device", $guidOther, "--input", "JOY_BTN2", "--text")
    Assert-True ($guidB.ExitCode -eq 0) "relations add for the second same-model device failed."
    $guidExchange = Run-Engine @("--profile", $guidProfile, "--remap-missing-devices", "false", "devices", "exchange", "--old", $guidOld, "--new", $guidNew, "--confirm", "false", "--text")
    Assert-True ($guidExchange.ExitCode -eq 0) ("devices exchange with a new GUID of the same device failed: " + $guidExchange.Output)
    $guidBinds = Run-Engine @("--profile", $guidProfile, "--remap-missing-devices", "false", "binds", "list", "--text")
    Assert-Match $guidBinds.Output 'Twin A \| type=button \| device=Twin Stick \{BBBBBBBB-0000-0000-0000-000000000001\}' "devices exchange should move the old instance to the new GUID."
    Assert-Match $guidBinds.Output 'Twin B \| type=button \| device=Twin Stick \{AAAAAAAA-0000-0000-0000-000000000002\}' "devices exchange must not move another device of the same model."

    $deviceRemove = Run-Engine @("--profile", $importProfile, "devices", "remove", "Device Ops New Stick", "--confirm", "false", "--text")
    Assert-True ($deviceRemove.ExitCode -eq 0) "engine devices remove failed."
    Assert-True ($deviceRemove.Output -match "(?m)^Removed Binds:\s+[1-9]\d*") "devices remove did not remove bind references."
    Assert-True ($deviceRemove.Output -match "(?m)^Removed Joystick FFB:\s+[1-9]\d*") "devices remove did not clear FFB references."
    $deviceOpsShowAfterRemove = Run-Engine @("--profile", $importProfile, "relations", "show", "Device Ops Relation", "--text")
    Assert-True ($deviceOpsShowAfterRemove.ExitCode -eq 0) "engine relations show after devices remove failed."
    Assert-True ($deviceOpsShowAfterRemove.Output -match "(?m)^Bind:\s+<none>") "devices remove did not detach bind from relation."
    $deviceOpsFfbAfterRemove = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "list", "--device", "Device Ops New Stick", "--confirm", "false", "--text")
    Assert-True ($deviceOpsFfbAfterRemove.ExitCode -eq 0) "engine dcs ffb list after devices remove failed."
    Assert-True ($deviceOpsFfbAfterRemove.Output -match "hasSetting=false") "devices remove did not clear stored FFB settings."

    $itemAdd = Run-Engine @("--profile", $importProfile, "relations", "item", "add", "JP Trigger Relation", "--id", "custom_item_id", "--game", "DCS", "--planes", "F-14B", "--text")
    Assert-True ($itemAdd.ExitCode -eq 0) "engine relations item add failed."
    Assert-True ($itemAdd.Output -match "id=custom_item_id, aircraft=F-14B") "relations item add did not add expected item/plane."

    $itemEnable = Run-Engine @("--profile", $importProfile, "relations", "item", "enable", "JP Trigger Relation", "--id", "d3001pnilunilcdnilvdnilvpnilvunil", "--game", "DCS", "--planes", "F-22A", "--text")
    Assert-True ($itemEnable.ExitCode -eq 0) "engine relations item enable failed."
    Assert-True ($itemEnable.Output -match "aircraft=.*F-22A") "relations item enable did not activate expected plane."

    $itemDisable = Run-Engine @("--profile", $importProfile, "relations", "item", "disable", "JP Trigger Relation", "--id", "d3001pnilunilcdnilvdnilvpnilvunil", "--game", "DCS", "--planes", "F-16C_50", "--text")
    Assert-True ($itemDisable.ExitCode -eq 0) "engine relations item disable failed."
    Assert-True ($itemDisable.Output -match "id=d3001pnilunilcdnilvdnilvpnilvunil, aircraft=F-22A") "relations item disable did not preserve persisted enabled plane."

    $itemRemove = Run-Engine @("--profile", $importProfile, "relations", "item", "remove", "JP Trigger Relation", "--id", "d3001pnilunilcdnilvdnilvpnilvunil", "--game", "DCS", "--text")
    Assert-True ($itemRemove.ExitCode -eq 0) "engine relations item remove failed."
    Assert-True ($itemRemove.Output -match "(?m)^Nodes:\s+1") "relations item remove did not preserve persisted additional node."

    $restoreFfbBeforeExport = Run-Engine @("--profile", $importProfile, "dcs", "ffb", "set", "Smoke Axis", "--invert-x", "false", "--invert-y", "true", "--swap-axis", "false", "--confirm", "false", "--text")
    Assert-True ($restoreFfbBeforeExport.ExitCode -eq 0) "engine dcs ffb set restore before export failed."

    $dcsExport = Run-Engine @("--profile", $importProfile, "dcs", "export", $savedExport, "--text")
    Assert-True ($dcsExport.ExitCode -eq 0) "engine dcs export failed."
    Assert-True ($dcsExport.Output -match "Files Written:\s+[1-9]\d*") "engine dcs export did not report written files."
    $exportedDiffs = Get-ChildItem -Path $savedExport -Recurse -Filter *.diff.lua -ErrorAction SilentlyContinue
    $exportedBackups = Get-ChildItem -Path $savedExport -Recurse -Filter *.jp -ErrorAction SilentlyContinue
    Assert-True ($null -ne $exportedDiffs -and $exportedDiffs.Count -ge 1) "engine dcs export did not create any diff.lua files."
    Assert-True ($null -ne $exportedBackups -and $exportedBackups.Count -ge 1) "engine dcs export did not create any .jp backup files."
    # Every export is backed up first; files the export created are removed again by a restore.
    $dcsBackups = Run-Engine @("backups", "list", "--game", "dcs", "--text")
    Assert-True ($dcsBackups.ExitCode -eq 0) "engine backups list failed. Output: $($dcsBackups.Output)"
    Assert-Match $dcsBackups.Output "(?m)^  - \S+_DCS \| DCS \| .* \| [1-9]\d* files \| engine dcs export" "dcs export did not create a backup set."
    $dcsRestoreDry = Run-Engine @("backups", "restore", "latest", "--game", "dcs", "--dry-run", "--text")
    Assert-True ($dcsRestoreDry.ExitCode -eq 0) "engine backups restore --dry-run failed. Output: $($dcsRestoreDry.Output)"
    Assert-Match $dcsRestoreDry.Output "(?m)^Files Removed: [1-9]" "a restore of the first dcs export should remove the files it created."
    Assert-True (@(Get-ChildItem -Path $savedExport -Recurse -Filter *.diff.lua).Count -ge 1) "backups restore --dry-run changed files."
    # The export also writes removal-only files for the other aircraft (DCS defaults): the metadata is in a file with binds.
    $firstBackupPayload = @($exportedBackups | ForEach-Object { Get-Content -Raw $_.FullName } | Where-Object { $_ -match '"added"' })[0]
    Assert-True ($firstBackupPayload -match '"JPK"') "engine dcs export backup file missing JPK metadata."
    Assert-True ($firstBackupPayload -match '"JGRP"') "engine dcs export backup file missing JGRP metadata."
    $modeProfile = Join-Path $tempDir "dcs_export_modes.pr0file"
    $modeAddConflict = Run-Engine @("--profile", $modeProfile, "relations", "add", "Smoke Conflict Trigger", "--button", "--node", "DCS:d_conflict:A-10C_2", "--device", "Smoke Stick", "--input", "JOY_BTN1", "--group", "Combat", "--text")
    Assert-True ($modeAddConflict.ExitCode -eq 0) "failed creating mode test relation d_conflict."
    $modeAddNew = Run-Engine @("--profile", $modeProfile, "relations", "add", "Smoke New Trigger", "--button", "--node", "DCS:d_new:A-10C_2", "--device", "Smoke Stick", "--input", "JOY_BTN5", "--group", "Combat", "--text")
    Assert-True ($modeAddNew.ExitCode -eq 0) "failed creating mode test relation d_new."
    $modeAddAxis = Run-Engine @("--profile", $modeProfile, "relations", "add", "Smoke Axis Override", "--axis", "--node", "DCS:a_conflict:A-10C_2", "--device", "Smoke Stick", "--input", "JOY_Y", "--group", "Axes", "--text")
    Assert-True ($modeAddAxis.ExitCode -eq 0) "failed creating mode test relation a_conflict."
    $modeAddModifier = Run-Engine @("--profile", $modeProfile, "binds", "modifier", "add", "Smoke Conflict Trigger", "SmokeMod", "--text")
    Assert-True ($modeAddModifier.ExitCode -eq 0) "failed adding mode test modifier to Smoke Conflict Trigger."
    Assert-Match $modeAddModifier.Output "SmokeMod" "mode test modifier add output missing SmokeMod."
    $modeSetAxis = Run-Engine @("--profile", $modeProfile, "binds", "set", "Smoke Axis Override", "--inverted", "true", "--slider", "true", "--deadzone", "0.1", "--curvature", "0.2,0.3", "--saturation-x", "0.85", "--saturation-y", "0.75", "--text")
    Assert-True ($modeSetAxis.ExitCode -eq 0) "failed configuring axis settings for mode test relation."
    $modeAddKeyboard = Run-Engine @("--profile", $modeProfile, "relations", "add", "Keyboard Existing Override", "--button", "--node", "DCS:k_existing:A-10C_2", "--device", "Keyboard", "--input", "K", "--group", "Keyboard", "--text")
    Assert-True ($modeAddKeyboard.ExitCode -eq 0) "failed creating mode test keyboard relation."
    $modeSetFfb = Run-Engine @("--profile", $modeProfile, "dcs", "ffb", "set", "Smoke Stick", "--invert-x", "true", "--invert-y", "false", "--swap-axis", "true", "--confirm", "false", "--text")
    Assert-True ($modeSetFfb.ExitCode -eq 0) "failed configuring mode test FFB settings."

    $createDcsModeFixture = {
        param([string]$RootPath)

        $planeDir = Join-Path $RootPath "Config\\Input\\A-10C_2"
        $joyDir = Join-Path $planeDir "joystick"
        $keyboardDir = Join-Path $planeDir "keyboard"
        New-Item -Path $joyDir -ItemType Directory -Force | Out-Null
        New-Item -Path $keyboardDir -ItemType Directory -Force | Out-Null

@'
local diff = {
  ["axisDiffs"] = {
    ["a_conflict"] = {
      ["name"] = "Pitch Existing",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_X",
          ["filter"] = {
            ["deadzone"] = 0.25,
          },
        },
      },
    },
  },
  ["ffDiffs"] = {
    ["ignore"] = false,
    ["invertX"] = false,
    ["invertY"] = false,
    ["shake"] = 0.4,
    ["swapAxes"] = false,
    ["trimmer"] = 0.75,
  },
  ["keyDiffs"] = {
    ["d_conflict"] = {
      ["name"] = "Trigger Existing",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN2",
        },
      },
    },
    ["d_existing_only"] = {
      ["name"] = "Existing Only",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN8",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path (Join-Path $joyDir "Smoke Stick.diff.lua") -Encoding UTF8

@'
local diff = {
  ["axisDiffs"] = {
    ["a_conflict"] = {
      ["name"] = "Pitch Existing",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_X",
          ["JPK"] = "Legacy Axis",
          ["JGRP"] = {
            [1] = "Legacy",
          },
        },
      },
    },
  },
  ["ffDiffs"] = {
    ["invertX"] = false,
    ["invertY"] = false,
    ["swapAxes"] = false,
  },
  ["keyDiffs"] = {
    ["d_conflict"] = {
      ["name"] = "Trigger Existing",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN2",
          ["JPK"] = "Legacy Trigger",
          ["JGRP"] = {
            [1] = "Legacy",
          },
        },
      },
    },
    ["d_existing_only"] = {
      ["name"] = "Existing Only",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN8",
          ["JPK"] = "Legacy Existing",
          ["JGRP"] = {
            [1] = "Legacy",
          },
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path (Join-Path $joyDir "Smoke Stick.jp") -Encoding UTF8

@'
local diff = {
  ["axisDiffs"] = {},
  ["keyDiffs"] = {
    ["d_legacy"] = {
      ["name"] = "Legacy Device",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN10",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path (Join-Path $joyDir "Legacy Stick.diff.lua") -Encoding UTF8

@'
local diff = {
  ["axisDiffs"] = {},
  ["keyDiffs"] = {
    ["d_legacy"] = {
      ["name"] = "Legacy Device",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN10",
          ["JPK"] = "Legacy Device Relation",
          ["JGRP"] = {
            [1] = "Legacy",
          },
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path (Join-Path $joyDir "Legacy Stick.jp") -Encoding UTF8

@'
local diff = {
  ["axisDiffs"] = {},
  ["keyDiffs"] = {
    ["k_existing"] = {
      ["name"] = "Keyboard Existing",
      ["added"] = {
        [1] = {
          ["key"] = "Space",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path (Join-Path $keyboardDir "Keyboard.diff.lua") -Encoding UTF8

@'
local modifiers = {
  ["LAlt"] = {
    ["device"] = "Keyboard",
    ["key"] = "LAlt",
    ["JPK"] = "LAlt",
    ["switch"] = false,
  },
  ["LegacyOnly"] = {
    ["device"] = "Keyboard",
    ["key"] = "RAlt",
    ["JPK"] = "LegacyOnly",
    ["switch"] = false,
  },
  ["SmokeMod"] = {
    ["device"] = "Keyboard",
    ["key"] = "LCtrl",
    ["JPK"] = "SmokeMod",
    ["switch"] = false,
  },
}
return modifiers
'@ | Set-Content -Path (Join-Path $planeDir "modifiers.lua") -Encoding UTF8
    }

    $modeCleanRoot = Join-Path $tempDir "saved-games-mode-clean"
    & $createDcsModeFixture $modeCleanRoot
    $modeClean = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeCleanRoot, "--mode", "clean", "--text")
    Assert-True ($modeClean.ExitCode -eq 0) "dcs export clean mode failed."
    Assert-Match $modeClean.Output "Mode:\s+clean" "dcs export clean mode did not report mode."
    $cleanStickDiff = Join-Path $modeCleanRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua"
    $cleanText = Get-Content -Raw $cleanStickDiff
    Assert-Match $cleanText '"JOY_BTN1"' "clean mode should keep generated d_conflict input."
    Assert-Match $cleanText '"JOY_BTN5"' "clean mode should include generated d_new input."
    Assert-Match $cleanText '"JOY_Y"' "clean mode should keep generated a_conflict axis input."
    Assert-Match $cleanText '"filter"' "clean mode should export axis filter settings."
    Assert-Match $cleanText '"ffDiffs"' "clean mode should include ffDiffs section."
    Assert-True (($cleanText -match '"shake"\]\s*=\s*0\.4,') -and ($cleanText -match '"trimmer"\]\s*=\s*0\.75,') -and ($cleanText -match '"ignore"\]\s*=\s*false,')) "clean mode must keep the device's other force feedback settings (trimmer, shake, ignore)."
    Assert-Match $cleanText '"invertX"\]\s*=\s*true' "clean mode should apply generated FFB invertX value."
    Assert-Match $cleanText '"swapAxes"\]\s*=\s*true' "clean mode should apply generated FFB swapAxes value."
    Assert-NotMatch $cleanText '"JOY_BTN2"' "clean mode should remove existing d_conflict input."
    Assert-NotMatch $cleanText '"d_existing_only"' "clean mode should remove existing-only entries."
    Assert-NotMatch $cleanText '"JPK"' "clean mode diff.lua must not contain JPK metadata."
    Assert-NotMatch $cleanText '"JGRP"' "clean mode diff.lua must not contain JGRP metadata."
    Assert-True (Test-Path (Join-Path $modeCleanRoot "Config\\Input\\A-10C_2\\joystick\\Legacy Stick.diff.lua")) "clean mode should keep untouched device files when nuke is disabled."
    $cleanKeyboardText = Get-Content -Raw (Join-Path $modeCleanRoot "Config\\Input\\A-10C_2\\keyboard\\Keyboard.diff.lua")
    Assert-Match $cleanKeyboardText '"Space"' "clean mode with default keep-keyboard-defaults should preserve keyboard file."
    $cleanModifiersText = Get-Content -Raw (Join-Path $modeCleanRoot "Config\\Input\\A-10C_2\\modifiers.lua")
    Assert-Match $cleanModifiersText '\["SmokeMod"\]' "clean mode should export generated SmokeMod modifier."
    Assert-Match $cleanModifiersText '\["key"\]\s*=\s*"SmokeMod"' "clean mode should overwrite SmokeMod key with generated modifier value."
    Assert-NotMatch $cleanModifiersText '\["LegacyOnly"\]' "clean mode should remove existing-only modifiers."

    $modeMergeRoot = Join-Path $tempDir "saved-games-mode-merge"
    & $createDcsModeFixture $modeMergeRoot
    $modeMerge = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeMergeRoot, "--mode", "merge", "--text")
    Assert-True ($modeMerge.ExitCode -eq 0) "dcs export merge mode failed."
    Assert-Match $modeMerge.Output "Mode:\s+merge" "dcs export merge mode did not report mode."
    $mergeText = Get-Content -Raw (Join-Path $modeMergeRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua")
    Assert-Match $mergeText '"JOY_BTN2"' "merge mode should keep existing d_conflict input."
    Assert-Match $mergeText '"JOY_BTN5"' "merge mode should still add non-conflicting d_new input."
    Assert-Match $mergeText '"d_existing_only"' "merge mode should retain existing-only entries."
    Assert-Match $mergeText '"JOY_X"' "merge mode should keep existing axis on conflict."
    Assert-NotMatch $mergeText '"JOY_Y"' "merge mode should not overwrite conflicting axis in merge mode."
    Assert-Match $mergeText '"ffDiffs"' "merge mode should include ffDiffs section."
    Assert-True (($mergeText -match '"shake"\]\s*=\s*0\.4,') -and ($mergeText -match '"trimmer"\]\s*=\s*0\.75,') -and ($mergeText -match '"ignore"\]\s*=\s*false,')) "merge mode must keep the device's other force feedback settings (trimmer, shake, ignore)."
    Assert-Match $mergeText '"invertX"\]\s*=\s*false' "merge mode should keep existing FFB invertX on conflict."
    Assert-Match $mergeText '"swapAxes"\]\s*=\s*false' "merge mode should keep existing FFB swapAxes on conflict."
    Assert-NotMatch $mergeText '"JPK"' "merge mode diff.lua must not contain JPK metadata."
    Assert-NotMatch $mergeText '"JGRP"' "merge mode diff.lua must not contain JGRP metadata."
    $mergeModifiersText = Get-Content -Raw (Join-Path $modeMergeRoot "Config\\Input\\A-10C_2\\modifiers.lua")
    Assert-Match $mergeModifiersText '\["LegacyOnly"\]' "merge mode should retain existing-only modifier entries."
    Assert-Match $mergeModifiersText '\["SmokeMod"\][\s\S]*?\["key"\]\s*=\s*"LCtrl"' "merge mode should keep existing SmokeMod modifier on conflict."

    $modeOverwriteRoot = Join-Path $tempDir "saved-games-mode-overwrite"
    & $createDcsModeFixture $modeOverwriteRoot
    $modeOverwrite = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeOverwriteRoot, "--mode", "merge-overwrite", "--text")
    Assert-True ($modeOverwrite.ExitCode -eq 0) "dcs export merge-overwrite mode failed."
    Assert-Match $modeOverwrite.Output "Mode:\s+merge-overwrite" "dcs export merge-overwrite mode did not report mode."
    $overwriteText = Get-Content -Raw (Join-Path $modeOverwriteRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua")
    Assert-Match $overwriteText '"JOY_BTN1"' "merge-overwrite mode should overwrite d_conflict input."
    Assert-Match $overwriteText '"JOY_BTN5"' "merge-overwrite mode should keep generated d_new input."
    Assert-Match $overwriteText '"d_existing_only"' "merge-overwrite mode should retain non-conflicting existing entry."
    Assert-Match $overwriteText '"JOY_Y"' "merge-overwrite mode should overwrite axis conflict."
    Assert-Match $overwriteText '"saturationX"' "merge-overwrite mode should include exported axis filter details."
    Assert-Match $overwriteText '"ffDiffs"' "merge-overwrite mode should include ffDiffs section."
    Assert-True (($overwriteText -match '"shake"\]\s*=\s*0\.4,') -and ($overwriteText -match '"trimmer"\]\s*=\s*0\.75,') -and ($overwriteText -match '"ignore"\]\s*=\s*false,')) "merge-overwrite mode must keep the device's other force feedback settings (trimmer, shake, ignore)."
    Assert-Match $overwriteText '"invertX"\]\s*=\s*true' "merge-overwrite mode should overwrite FFB invertX."
    Assert-Match $overwriteText '"swapAxes"\]\s*=\s*true' "merge-overwrite mode should overwrite FFB swapAxes."
    Assert-NotMatch $overwriteText '"JOY_BTN2"' "merge-overwrite mode should remove conflicting existing key input."
    Assert-NotMatch $overwriteText '"JOY_X"' "merge-overwrite mode should remove conflicting existing axis input."
    Assert-NotMatch $overwriteText '"JPK"' "merge-overwrite diff.lua must not contain JPK metadata."
    Assert-NotMatch $overwriteText '"JGRP"' "merge-overwrite diff.lua must not contain JGRP metadata."
    $overwriteBackupText = Get-Content -Raw (Join-Path $modeOverwriteRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.jp")
    Assert-Match $overwriteBackupText '"JPK"' "merge-overwrite backup .jp should include JPK metadata."
    Assert-Match $overwriteBackupText '"JGRP"' "merge-overwrite backup .jp should include JGRP metadata."
    $overwriteModifiersText = Get-Content -Raw (Join-Path $modeOverwriteRoot "Config\\Input\\A-10C_2\\modifiers.lua")
    Assert-Match $overwriteModifiersText '\["LegacyOnly"\]' "merge-overwrite mode should retain non-conflicting existing modifiers."
    Assert-Match $overwriteModifiersText '\["SmokeMod"\][\s\S]*?\["key"\]\s*=\s*"SmokeMod"' "merge-overwrite mode should overwrite SmokeMod modifier on conflict."

    # DCS default binds (JoyPro1 behaviour): every device file JoyPro writes drops the plane's defaults from
    # CleanProfile\DCS\<plane>.cf and keeps removals the file already had; a bind JoyPro sets is not removed.
    # Files are written without a BOM because DCS's Lua 5.1 rejects one.
    $dcsDefaultsRoot = Join-Path $tempDir "saved-games-defaults"
    $dcsDefaultsJoyDir = Join-Path $dcsDefaultsRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsDefaultsJoyDir -ItemType Directory -Force | Out-Null
    $dcsDefaultsFile = Join-Path $dcsDefaultsJoyDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"
@'
local diff = {
  ["keyDiffs"] = {
    ["d_user_cleared"] = {
      ["name"] = "User Cleared",
      ["removed"] = {
        [1] = {
          ["key"] = "JOY_BTN9",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path $dcsDefaultsFile -Encoding UTF8
    $dcsDefaultsProfile = Join-Path $tempDir "dcs-defaults.pr0file"
    $dcsDefaultsAdd = Run-Engine @("--profile", $dcsDefaultsProfile, "relations", "add", "Smoke A10 Default Button", "--button", "--node", "DCS:d554pnilu638cdnilvdnilvpnilvunil:A-10C", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_BTN1", "--text")
    Assert-True ($dcsDefaultsAdd.ExitCode -eq 0) "relations add for the DCS default-bind check failed."
    $dcsDefaultsExport = Run-Engine @("--profile", $dcsDefaultsProfile, "dcs", "export", $dcsDefaultsRoot, "--mode", "merge-overwrite", "--text")
    Assert-True ($dcsDefaultsExport.ExitCode -eq 0) "dcs export for the default-bind check failed."
    $dcsDefaultsBytes = [System.IO.File]::ReadAllBytes($dcsDefaultsFile)
    Assert-True ($dcsDefaultsBytes[0] -eq 0x6C) "dcs export must write diff.lua without a BOM (DCS's Lua 5.1 cannot load it otherwise)."
    $dcsDefaultsModifiers = [System.IO.File]::ReadAllBytes((Join-Path $dcsDefaultsRoot "Config\\Input\\A-10C\\modifiers.lua"))
    Assert-True ($dcsDefaultsModifiers[0] -eq 0x6C) "dcs export must write modifiers.lua without a BOM."
    $dcsDefaultsText = [System.IO.File]::ReadAllText($dcsDefaultsFile)
    $dcsHasCleanProfiles = -not (Skip-WithoutGameData "DCS exports remove the aircraft's default binds (CleanProfile\DCS)")
    if ($dcsHasCleanProfiles) {
    Assert-Match $dcsDefaultsText '(?s)\["a2001cdnil"\] = \{\s*\["name"\] = "[^"]*",\s*\["removed"\] = \{\s*\[1\] = \{\s*\["key"\] = "JOY_Y"' "dcs export must remove the plane's default binds on the device (CleanProfile)."
    }
    Assert-Match $dcsDefaultsText '(?s)\["d_user_cleared"\] = \{\s*\["name"\] = "User Cleared",\s*\["removed"\] = \{\s*\[1\] = \{\s*\["key"\] = "JOY_BTN9"' "dcs export must keep removals the file already had."
    $dcsDefaultsBlock = [regex]::Match($dcsDefaultsText, '(?s)\["d554pnilu638cdnilvdnilvpnilvunil"\] = \{(.*?)\r?\n    \},')
    Assert-True ($dcsDefaultsBlock.Success -and $dcsDefaultsBlock.Groups[1].Value -match '"JOY_BTN1"' -and $dcsDefaultsBlock.Groups[1].Value -notmatch '\["removed"\]') "dcs export must not remove a default bind the profile sets."
    # Nuke in a plane with a clean profile: an unused device loses its binds and gets the plane's default removals.
    $dcsNukeRoot = Join-Path $tempDir "saved-games-defaults-nuke"
    $dcsNukeJoyDir = Join-Path $dcsNukeRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsNukeJoyDir -ItemType Directory -Force | Out-Null
    $dcsNukePedals = Join-Path $dcsNukeJoyDir "Old Pedals {99999999-8888-7777-6666-555555555555}.diff.lua"
@'
local diff = {
  ["axisDiffs"] = {
    ["a2003cdnil"] = {
      ["name"] = "Rudder",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_RZ",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path $dcsNukePedals -Encoding UTF8
    $dcsNuke = Run-Engine @("--profile", $dcsDefaultsProfile, "dcs", "export", $dcsNukeRoot, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($dcsNuke.ExitCode -eq 0) "dcs clean export with nuke failed."
    $dcsNukePedalsText = [System.IO.File]::ReadAllText($dcsNukePedals)
    Assert-NotMatch $dcsNukePedalsText '\["added"\]' "nuke must remove every bind of an unused device."
    if ($dcsHasCleanProfiles) {
    Assert-Match $dcsNukePedalsText '(?s)\["a2001cdnil"\] = \{\s*\["name"\] = "[^"]*",\s*\["removed"\] = \{\s*\[1\] = \{\s*\["key"\] = "JOY_Y"' "nuke must remove the plane's DCS defaults from an unused device."
    }
    Assert-True ([System.IO.File]::ReadAllBytes($dcsNukePedals)[0] -eq 0x6C) "nuke must write files without a BOM."
    # Clean mode covers every aircraft: the written device also loses its DCS defaults in aircraft the profile does
    # not bind (here F-16C_50, which has a clean profile); --clean-all-aircraft false keeps the other aircraft.
    $dcsOtherPlaneFile = Join-Path $dcsNukeRoot "Config\\Input\\F-16C_50\\joystick\\Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"
    if ($dcsHasCleanProfiles) {
    Assert-True (Test-Path -LiteralPath $dcsOtherPlaneFile) "dcs clean export must clear the written device in the other aircraft too."
    Assert-Match ([System.IO.File]::ReadAllText($dcsOtherPlaneFile)) '\["removed"\]' "the other aircraft's file must remove that aircraft's DCS defaults."
    Assert-NotMatch ([System.IO.File]::ReadAllText($dcsOtherPlaneFile)) '\["added"\]' "the other aircraft's file must not add binds."
    }
    $dcsOnlyProfileRoot = Join-Path $tempDir "saved-games-clean-profile-planes"
    $dcsOnlyProfile = Run-Engine @("--profile", $dcsDefaultsProfile, "dcs", "export", $dcsOnlyProfileRoot, "--mode", "clean", "--clean-all-aircraft", "false", "--text")
    Assert-True ($dcsOnlyProfile.ExitCode -eq 0) "dcs clean export with --clean-all-aircraft false failed."
    Assert-True (-not (Test-Path (Join-Path $dcsOnlyProfileRoot "Config\\Input\\F-16C_50"))) "--clean-all-aircraft false must not touch other aircraft."

    # Device type defaults: DCS loads "<device type>.lua" of a plane when it exists, so the refresh stores those
    # defaults as CleanProfile\DCS\<plane>\<device type>.cf and the export uses them instead of the generic file.
    # (In the game-data folder, where the refresh writes them and the export reads them first.)
    $dcsTypeCleanDir = Join-Path $env:JOYPRO_DATA_DIR "GameData\\CleanProfile\\DCS\\A-10C"
    $dcsTypeCleanFile = Join-Path $dcsTypeCleanDir "Smoke Stick.cf"
    $dcsTypeCleanDirExisted = Test-Path $dcsTypeCleanDir
    New-Item -Path $dcsTypeCleanDir -ItemType Directory -Force | Out-Null
@'
local diff = {
  ["keyDiffs"] = {
    ["d_type_default"] = {
      ["name"] = "Type Default",
      ["removed"] = {
        [1] = {
          ["key"] = "JOY_BTN30",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -Path $dcsTypeCleanFile -Encoding UTF8
    try {
        $dcsTypeRoot = Join-Path $tempDir "saved-games-device-type"
        $dcsTypeExport = Run-Engine @("--profile", $dcsDefaultsProfile, "dcs", "export", $dcsTypeRoot, "--mode", "merge-overwrite", "--text")
        Assert-True ($dcsTypeExport.ExitCode -eq 0) "dcs export with a device type clean profile failed."
        $dcsTypeText = [System.IO.File]::ReadAllText((Join-Path $dcsTypeRoot "Config\\Input\\A-10C\\joystick\\Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"))
        Assert-Match $dcsTypeText '"JOY_BTN30"' "dcs export must remove the device type's own defaults (CleanProfile\DCS\<plane>\<type>.cf)."
        Assert-NotMatch $dcsTypeText '"JOY_Y"' "dcs export must not use the generic defaults when the device type has its own."
    }
    finally {
        Remove-Item -LiteralPath $dcsTypeCleanFile -Force -ErrorAction SilentlyContinue
        if (-not $dcsTypeCleanDirExisted) { Remove-Item -LiteralPath $dcsTypeCleanDir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # DCS names a device with the DirectInput name untrimmed (" VKB-Sim ...  {GUID}"); JoyPro trims it. The export
    # writes the file DCS reads (name taken from an existing file when the device is not connected), and the
    # import turns that name back into JoyPro's.
    $dcsSpaceRoot = Join-Path $tempDir "saved-games-space-names"
    $dcsSpaceDir = Join-Path $dcsSpaceRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsSpaceDir -ItemType Directory -Force | Out-Null
    $dcsSpaceDcsName = " Spacey Stick  {22222222-3333-4444-5555-666666666666}"
    $dcsSpaceFile = Join-Path $dcsSpaceDir ($dcsSpaceDcsName + ".diff.lua")
@'
local diff = {
  ["keyDiffs"] = {
    ["d_spacey_old"] = {
      ["name"] = "Spacey Old",
      ["added"] = {
        [1] = {
          ["key"] = "JOY_BTN7",
        },
      },
    },
  },
}
return diff
'@ | Set-Content -LiteralPath $dcsSpaceFile -Encoding UTF8
    $dcsSpaceScan = Run-Engine @("dcs", "scan", $dcsSpaceRoot, "--text")
    Assert-True ($dcsSpaceScan.ExitCode -eq 0) "dcs scan of a DCS-named device file failed."
    Assert-Match $dcsSpaceScan.Output 'Spacey Stick \{22222222-3333-4444-5555-666666666666\}' "dcs import must map the DCS device name to JoyPro's trimmed name."
    Assert-NotMatch $dcsSpaceScan.Output ' Spacey Stick  \{' "dcs import must not keep DCS's spaces in the device name."
    $dcsSpaceProfile = Join-Path $tempDir "dcs-space.pr0file"
    $dcsSpaceAdd = Run-Engine @("--profile", $dcsSpaceProfile, "relations", "add", "Smoke Spacey Button", "--button", "--node", "DCS:d554pnilu638cdnilvdnilvpnilvunil:A-10C", "--device", "Spacey Stick {22222222-3333-4444-5555-666666666666}", "--input", "JOY_BTN3", "--text")
    Assert-True ($dcsSpaceAdd.ExitCode -eq 0) "relations add for the DCS device name check failed."
    $dcsSpaceExport = Run-Engine @("--profile", $dcsSpaceProfile, "dcs", "export", $dcsSpaceRoot, "--mode", "merge-overwrite", "--text")
    Assert-True ($dcsSpaceExport.ExitCode -eq 0) "dcs export for the DCS device name check failed."
    Assert-Match ([System.IO.File]::ReadAllText($dcsSpaceFile)) '"JOY_BTN3"' "dcs export must write into the file named as DCS names the device."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $dcsSpaceDir "Spacey Stick {22222222-3333-4444-5555-666666666666}.diff.lua"))) "dcs export must not write a second file under the trimmed name."
    # Files an older JoyPro wrote under the trimmed name (ignored by DCS) are removed once the DCS-named file is written.
    $dcsLegacyRoot = Join-Path $tempDir "saved-games-legacy-names"
    $dcsLegacyDir = Join-Path $dcsLegacyRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsLegacyDir -ItemType Directory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dcsLegacyDir ($dcsSpaceDcsName + ".diff.lua")) -Value "local diff = {}`r`nreturn diff" -Encoding ASCII
    $dcsLegacyDiff = Join-Path $dcsLegacyDir "Spacey Stick {22222222-3333-4444-5555-666666666666}.diff.lua"
    $dcsLegacyJp = Join-Path $dcsLegacyDir "Spacey Stick {22222222-3333-4444-5555-666666666666}.jp"
    Set-Content -LiteralPath $dcsLegacyDiff -Value "local diff = {}`r`nreturn diff" -Encoding ASCII
    Set-Content -LiteralPath $dcsLegacyJp -Value "local diff = {}`r`nreturn diff" -Encoding ASCII
    $dcsLegacyExport = Run-Engine @("--profile", $dcsSpaceProfile, "dcs", "export", $dcsLegacyRoot, "--mode", "merge-overwrite", "--text")
    Assert-True ($dcsLegacyExport.ExitCode -eq 0) "dcs export over legacy-named files failed."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $dcsLegacyDir ($dcsSpaceDcsName + ".diff.lua")))) '"JOY_BTN3"' "dcs export must write the DCS-named file when legacy files exist."
    Assert-True (-not (Test-Path -LiteralPath $dcsLegacyDiff) -and -not (Test-Path -LiteralPath $dcsLegacyJp)) "dcs export must remove the legacy trimmed-name files."

    # DCS saves a diff without binds equal to a default (JoyPro wrote JOY_Y on pitch, DCS dropped it): the .jp
    # still has it, so add/merge keep it. In merge, an action the file only drops defaults on takes the profile's bind.
    $dcsNormRoot = Join-Path $tempDir "saved-games-normalized"
    $dcsNormJoyDir = Join-Path $dcsNormRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsNormJoyDir -ItemType Directory -Force | Out-Null
    $dcsNormFile = Join-Path $dcsNormJoyDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"
    $dcsNormLf = [char]10
    $dcsNormDiff = (@(
        'local diff = {', '  ["axisDiffs"] = {', '    ["a2003cdnil"] = {', '      ["name"] = "Rudder",', '      ["removed"] = {',
        '        [1] = {', '          ["key"] = "JOY_RZ",', '        },', '      },', '    },', '  },', '}', 'return diff', '') -join $dcsNormLf)
    [System.IO.File]::WriteAllText($dcsNormFile, $dcsNormDiff)
    $dcsNormJp = (@(
        'local diff = {', '  ["axisDiffs"] = {', '    ["a2001cdnil"] = {', '      ["name"] = "Pitch",', '      ["added"] = {',
        '        [1] = {', '          ["key"] = "JOY_Y",', '        },', '      },', '    },', '  },', '}', 'return diff', '') -join $dcsNormLf)
    [System.IO.File]::WriteAllText((Join-Path $dcsNormJoyDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.jp"), $dcsNormJp)
    $dcsNormProfile = Join-Path $tempDir "dcs-normalized.pr0file"
    $dcsNormAdd = Run-Engine @("--profile", $dcsNormProfile, "relations", "add", "Smoke A10 Rudder", "--axis", "--node", "DCS:a2003cdnil:A-10C", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--input", "JOY_RX", "--text")
    Assert-True ($dcsNormAdd.ExitCode -eq 0) "relations add for the DCS normalized-default check failed."
    $dcsNormExport = Run-Engine @("--profile", $dcsNormProfile, "dcs", "export", $dcsNormRoot, "--mode", "merge", "--text")
    Assert-True ($dcsNormExport.ExitCode -eq 0) "dcs merge export for the normalized-default check failed."
    $dcsNormText = [System.IO.File]::ReadAllText($dcsNormFile)
    $dcsNormPitch = [regex]::Match($dcsNormText, '(?s)\["a2001cdnil"\] = \{(.*?)\n    \},')
    if ($dcsHasCleanProfiles) { Assert-True ($dcsNormPitch.Success -and $dcsNormPitch.Groups[1].Value -match '\["added"\][\s\S]*"JOY_Y"' -and $dcsNormPitch.Groups[1].Value -notmatch '\["removed"\]') "dcs merge export must keep a default bind DCS dropped from the diff but the .jp has." }
    $dcsNormRudder = [regex]::Match($dcsNormText, '(?s)\["a2003cdnil"\] = \{(.*?)\n    \},')
    Assert-True ($dcsNormRudder.Success -and $dcsNormRudder.Groups[1].Value -match '\["added"\][\s\S]*"JOY_RX"' -and $dcsNormRudder.Groups[1].Value -match '\["removed"\][\s\S]*"JOY_RZ"') "dcs merge export must add the profile's bind to an action the file only drops defaults on."

    # Import: the .jp keeps what JoyPro last wrote. A bind the user removed in DCS (gone from the diff, not a default)
    # must not come back from it; a default DCS dropped from the diff still counts; the .jp's relation name is used.
    $dcsStaleRoot = Join-Path $tempDir "saved-games-stale-jp"
    $dcsStaleJoyDir = Join-Path $dcsStaleRoot "Config\\Input\\A-10C\\joystick"
    New-Item -Path $dcsStaleJoyDir -ItemType Directory -Force | Out-Null
    $dcsStaleDiff = (@(
        'local diff = {', '  ["keyDiffs"] = {', '    ["d554pnilu638cdnilvdnilvpnilvunil"] = {', '      ["name"] = "Kept",', '      ["added"] = {',
        '        [1] = {', '          ["key"] = "JOY_BTN1",', '        },', '      },', '    },', '  },', '}', 'return diff', '') -join $dcsNormLf)
    [System.IO.File]::WriteAllText((Join-Path $dcsStaleJoyDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.diff.lua"), $dcsStaleDiff)
    $dcsStaleJp = (@(
        'local diff = {', '  ["axisDiffs"] = {', '    ["a2001cdnil"] = {', '      ["name"] = "Pitch",', '      ["added"] = {',
        '        [1] = {', '          ["key"] = "JOY_Y",', '          ["JPK"] = "Smoke Pitch",', '        },', '      },', '    },', '  },',
        '  ["keyDiffs"] = {', '    ["d554pnilu638cdnilvdnilvpnilvunil"] = {', '      ["name"] = "Kept",', '      ["added"] = {',
        '        [1] = {', '          ["key"] = "JOY_BTN1",', '          ["JPK"] = "Smoke Kept",', '        },', '      },', '    },',
        '    ["d_removed_in_dcs"] = {', '      ["name"] = "Removed In DCS",', '      ["added"] = {',
        '        [1] = {', '          ["key"] = "JOY_BTN7",', '          ["JPK"] = "Smoke Removed",', '        },', '      },', '    },', '  },',
        '}', 'return diff', '') -join $dcsNormLf)
    [System.IO.File]::WriteAllText((Join-Path $dcsStaleJoyDir "Smoke Stick {11111111-2222-3333-4444-555555555555}.jp"), $dcsStaleJp)
    $dcsStaleProfile = Join-Path $tempDir "dcs-stale-jp.pr0file"
    $dcsStaleImport = Run-Engine @("--profile", $dcsStaleProfile, "dcs", "import", $dcsStaleRoot, "--confirm", "false", "--text")
    Assert-True ($dcsStaleImport.ExitCode -eq 0) "dcs import for the stale .jp check failed."
    if ($dcsHasCleanProfiles) {
    Assert-Match $dcsStaleImport.Output 'Backup Binds Removed In DCS \(\.jp, skipped\):\s+1' "dcs import must report the .jp bind removed in DCS."
    }
    $dcsStaleBinds = Run-Engine @("--profile", $dcsStaleProfile, "binds", "list", "--text")
    Assert-Match $dcsStaleBinds.Output 'Smoke Kept' "dcs import must take the relation name from the .jp for a bind the diff has."
    if ($dcsHasCleanProfiles) {
    Assert-Match $dcsStaleBinds.Output 'Smoke Pitch' "dcs import must keep a default bind DCS dropped from the diff."
    }
    Assert-NotMatch $dcsStaleBinds.Output 'Smoke Removed|JOY_BTN7' "dcs import must not bring back a bind removed in DCS from the .jp."

    $modeAddRoot = Join-Path $tempDir "saved-games-mode-add"
    & $createDcsModeFixture $modeAddRoot
    $modeAdd = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeAddRoot, "--mode", "add", "--text")
    Assert-True ($modeAdd.ExitCode -eq 0) "dcs export add mode failed."
    Assert-Match $modeAdd.Output "Mode:\s+add" "dcs export add mode did not report mode."
    $addText = Get-Content -Raw (Join-Path $modeAddRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua")
    Assert-Match $addText '"JOY_BTN1"' "add mode should include generated d_conflict input."
    Assert-Match $addText '"JOY_BTN2"' "add mode should keep existing d_conflict input."
    Assert-Match $addText '"JOY_BTN5"' "add mode should include generated d_new input."
    Assert-Match $addText '"JOY_X"' "add mode should keep existing axis input."
    Assert-Match $addText '"JOY_Y"' "add mode should also include generated axis input."
    Assert-Match $addText '"d_existing_only"' "add mode should retain existing-only entries."
    Assert-Match $addText '"ffDiffs"' "add mode should include ffDiffs section."
    Assert-Match $addText '"invertX"\]\s*=\s*false' "add mode should keep existing FFB invertX value."
    Assert-Match $addText '"swapAxes"\]\s*=\s*false' "add mode should keep existing FFB swapAxes value."
    Assert-NotMatch $addText '"JPK"' "add mode diff.lua must not contain JPK metadata."
    Assert-NotMatch $addText '"JGRP"' "add mode diff.lua must not contain JGRP metadata."
    $addModifiersText = Get-Content -Raw (Join-Path $modeAddRoot "Config\\Input\\A-10C_2\\modifiers.lua")
    Assert-Match $addModifiersText '\["LegacyOnly"\]' "add mode should retain non-conflicting existing modifiers."
    Assert-Match $addModifiersText '\["SmokeMod"\][\s\S]*?\["key"\]\s*=\s*"SmokeMod"' "add mode should overwrite SmokeMod modifier on conflict."

    $modeKeyboardOverrideRoot = Join-Path $tempDir "saved-games-mode-keyboard-override"
    & $createDcsModeFixture $modeKeyboardOverrideRoot
    $modeKeyboardOverride = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeKeyboardOverrideRoot, "--mode", "merge-overwrite", "--keep-keyboard-defaults", "false", "--text")
    Assert-True ($modeKeyboardOverride.ExitCode -eq 0) "dcs export with keep-keyboard-defaults false failed."
    $keyboardOverrideText = Get-Content -Raw (Join-Path $modeKeyboardOverrideRoot "Config\\Input\\A-10C_2\\keyboard\\Keyboard.diff.lua")
    Assert-Match $keyboardOverrideText '"key"\]\s*=\s*"K"' "keep-keyboard-defaults false should export keyboard relation value."
    Assert-NotMatch $keyboardOverrideText '"key"\]\s*=\s*"Space"' "keep-keyboard-defaults false should overwrite conflicting keyboard value in merge-overwrite mode."
    Assert-NotMatch $keyboardOverrideText '"JPK"' "keyboard diff.lua must not contain JPK metadata."
    Assert-NotMatch $keyboardOverrideText '"JGRP"' "keyboard diff.lua must not contain JGRP metadata."

    $modeNukeRoot = Join-Path $tempDir "saved-games-mode-nuke"
    & $createDcsModeFixture $modeNukeRoot
    $modeNuke = Run-Engine @("--profile", $modeProfile, "dcs", "export", $modeNukeRoot, "--mode", "merge-overwrite", "--keep-keyboard-defaults", "true", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($modeNuke.ExitCode -eq 0) "dcs export nuke mode failed."
    Assert-Match $modeNuke.Output "Device Files Cleared \(Nuke\):\s+[1-9]\d*" "dcs export nuke mode did not report clearing unused devices."
    # Nuke empties unused devices instead of deleting their files (a missing file would bring DCS defaults back).
    $nukeLegacyText = Get-Content -Raw (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\joystick\\Legacy Stick.diff.lua")
    Assert-NotMatch $nukeLegacyText '"JOY_BTN10"' "nuke mode should remove all binds of an unused device."
    Assert-True (Test-Path (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\joystick\\Legacy Stick.jp")) "nuke mode should keep the unused device's backup file (emptied)."
    Assert-Match (Get-Content -Raw (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua")) '"JOY_BTN5"' "nuke mode must keep the binds of used devices."
    Assert-True (Test-Path (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\joystick\\Smoke Stick.diff.lua")) "nuke mode should keep exported joystick diff.lua file."
    Assert-True (Test-Path (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\keyboard\\Keyboard.diff.lua")) "nuke mode should preserve keyboard file when keep-keyboard-defaults is true."
    $nukeKeyboardText = Get-Content -Raw (Join-Path $modeNukeRoot "Config\\Input\\A-10C_2\\keyboard\\Keyboard.diff.lua")
    Assert-Match $nukeKeyboardText '"Space"' "nuke mode should keep original keyboard default file content when keep-keyboard-defaults is true."

    $modeRoots = @(
        $modeCleanRoot,
        $modeMergeRoot,
        $modeOverwriteRoot,
        $modeAddRoot,
        $modeKeyboardOverrideRoot,
        $modeNukeRoot
    )
    foreach ($root in $modeRoots) {
        $diffFiles = Get-ChildItem -Path $root -Recurse -Filter *.diff.lua -ErrorAction SilentlyContinue
        foreach ($diffFile in $diffFiles) {
            $diffText = Get-Content -Raw $diffFile.FullName
            Assert-NotMatch $diffText '"JPK"' ("diff.lua should never contain JPK metadata: " + $diffFile.FullName)
            Assert-NotMatch $diffText '"JGRP"' ("diff.lua should never contain JGRP metadata: " + $diffFile.FullName)
        }
    }

    # DCS axis tuning per aircraft: the same axis bind with different filters in two aircraft keeps each filter
    # (the second aircraft's becomes the relation's plane tuning instead of being dropped).
    $dcsPlanesRoot = Join-Path $tempDir "dcs-plane-tuning"
    $dcsPlanesDevice = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    foreach ($pt in @(@{ Plane = "A-10C_2"; Filter = "[`"curvature`"] = { [1] = 0.1, }, [`"deadzone`"] = 0," }, @{ Plane = "F-16C_50"; Filter = "[`"curvature`"] = { [1] = 0.4, }, [`"deadzone`"] = 0.05," })) {
        $ptDir = Join-Path $dcsPlanesRoot ("Config\\Input\\" + $pt.Plane + "\\joystick")
        New-Item -Path $ptDir -ItemType Directory -Force | Out-Null
        $ptText = "local diff = {`n  [`"axisDiffs`"] = {`n    [`"a2001cdnil`"] = {`n      [`"added`"] = {`n        [1] = {`n          [`"filter`"] = { " + $pt.Filter + " [`"invert`"] = false, [`"saturationX`"] = 1, [`"saturationY`"] = 1, [`"slider`"] = false, },`n          [`"key`"] = `"JOY_Y`",`n        },`n      },`n      [`"name`"] = `"Pitch`",`n    },`n  },`n}`nreturn diff`n"
        [System.IO.File]::WriteAllText((Join-Path $ptDir ($dcsPlanesDevice + ".diff.lua")), $ptText, (New-Object System.Text.UTF8Encoding($false)))
    }
    $dcsPlanesProfile = Join-Path $tempDir "dcs-plane-tuning.pr0file"
    $dcsPlanesImport = Run-Engine @("--profile", $dcsPlanesProfile, "dcs", "import", $dcsPlanesRoot, "--confirm", "false", "--import-curvature", "--import-deadzone", "--text")
    Assert-True ($dcsPlanesImport.ExitCode -eq 0) "dcs import of the per-aircraft filter fixture failed."
    $dcsPlanesRelations = Run-Engine @("--profile", $dcsPlanesProfile, "relations", "list", "--text")
    $dcsPlanesName = [regex]::Match($dcsPlanesRelations.Output, "(?m)^([^|\r\n]+?) \| type=axis").Groups[1].Value
    Assert-True (([regex]::Matches($dcsPlanesRelations.Output, "type=axis")).Count -eq 1) "dcs import should join the same axis bind of two aircraft into one relation."
    $dcsPlanesF16 = Run-Engine @("--profile", $dcsPlanesProfile, "binds", "tuning", "show", $dcsPlanesName, "--game", "DCS", "--plane", "F-16C_50", "--text")
    $dcsPlanesA10 = Run-Engine @("--profile", $dcsPlanesProfile, "binds", "tuning", "show", $dcsPlanesName, "--game", "DCS", "--plane", "A-10C_2", "--text")
    Assert-Match $dcsPlanesF16.Output "F-16C_50 uses \(dcs, from plane:DCS:F-16C_50\): inv=0;sl=0;dz=0\.05;sx=1;sy=1;cv=0\.4" "dcs import should keep the second aircraft's filter as its plane tuning."
    Assert-Match $dcsPlanesA10.Output "A-10C_2 uses \(dcs, from base\): inv=0;sl=0;dz=0;sx=1;sy=1;cv=0\.1" "dcs import should keep the first aircraft's filter as the base."
    $dcsPlanesOut = Join-Path $tempDir "dcs-plane-tuning-out"
    Copy-Item -Path $dcsPlanesRoot -Destination $dcsPlanesOut -Recurse -Force
    $dcsPlanesExport = Run-Engine @("--profile", $dcsPlanesProfile, "dcs", "export", $dcsPlanesOut, "--mode", "merge-overwrite", "--text")
    Assert-True ($dcsPlanesExport.ExitCode -eq 0) "dcs export with plane tuning failed."
    $dcsF16Text = [System.IO.File]::ReadAllText((Join-Path $dcsPlanesOut ("Config\\Input\\F-16C_50\\joystick\" + $dcsPlanesDevice + ".diff.lua")))
    $dcsA10Text = [System.IO.File]::ReadAllText((Join-Path $dcsPlanesOut ("Config\\Input\\A-10C_2\\joystick\" + $dcsPlanesDevice + ".diff.lua")))
    Assert-Match $dcsF16Text '\["curvature"\] = \{\s*\[1\] = 0\.4,' "dcs export should write the F-16's own curvature."
    Assert-Match $dcsF16Text '\["deadzone"\] = 0\.05,' "dcs export should write the F-16's own dead zone."
    Assert-Match $dcsA10Text '\["curvature"\] = \{\s*\[1\] = 0\.1,' "dcs export should write the A-10's curvature."
    Assert-NotMatch $dcsA10Text '"deadzone"\] = 0\.05' "dcs export must not give the A-10 the F-16's dead zone."

    # Force feedback settings per device and aircraft (engine ffb): only the F-16 gets them, in its ffDiffs.
    $ffbSet = Run-Engine @("--profile", $dcsPlanesProfile, "ffb", "set", "--device", $dcsPlanesDevice, "--game", "DCS", "--plane", "F-16C_50", "--param", "trimmer=0.8", "--param", "ignore=true", "--text")
    Assert-True ($ffbSet.ExitCode -eq 0) "ffb set for the F-16 failed."
    $ffbWide = Run-Engine @("--profile", $dcsPlanesProfile, "ffb", "set", "--device", $dcsPlanesDevice, "--game", "IL2Korea", "--param", "ffEnabled=0", "--text")
    Assert-True ($ffbWide.ExitCode -ne 0) "ffb set must refuse a game-wide setting for a single device."
    $ffbOut = Join-Path $tempDir "dcs-ffb-out"
    Copy-Item -Path $dcsPlanesRoot -Destination $ffbOut -Recurse -Force
    $ffbExport = Run-Engine @("--profile", $dcsPlanesProfile, "dcs", "export", $ffbOut, "--mode", "merge-overwrite", "--text")
    Assert-True ($ffbExport.ExitCode -eq 0) "dcs export with ffb settings failed."
    $ffbF16 = [System.IO.File]::ReadAllText((Join-Path $ffbOut ("Config\Input\F-16C_50\joystick\" + $dcsPlanesDevice + ".diff.lua")))
    $ffbA10 = [System.IO.File]::ReadAllText((Join-Path $ffbOut ("Config\Input\A-10C_2\joystick\" + $dcsPlanesDevice + ".diff.lua")))
    Assert-Match $ffbF16 '\["ffDiffs"\] = \{\s*\["ignore"\] = true,\s*\["trimmer"\] = 0\.8,' "dcs export should write the F-16's force feedback settings into ffDiffs."
    Assert-NotMatch $ffbA10 '"ffDiffs"' "dcs export must not give the A-10 the F-16's force feedback settings."

    # IL-2: an export must never wipe the user's existing bindings.
    $il2Root = Join-Path $tempDir "il2"
    $il2Input = Join-Path $il2Root "data\\input"
    $il2GlobalActions = Join-Path $il2Input "global.actions"
    New-Item -Path $il2Input -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\il2-game\\data\\input\\*") -Destination $il2Input -Force
    $il2GlobalBefore = Get-Content -Raw $il2GlobalActions

    $il2EmptyProfile = Join-Path $tempDir "il2_empty.pr0file"
    $il2EmptyExport = Run-Engine @("--profile", $il2EmptyProfile, "il2", "export", $il2Root, "--mode", "merge", "--text")
    Assert-True ($il2EmptyExport.ExitCode -eq 0) "il2 export with an empty profile failed."
    Assert-True ((Get-Content -Raw $il2GlobalActions) -eq $il2GlobalBefore) "il2 export with an empty profile must not change global.actions."

    $il2ImportProfile = Join-Path $tempDir "il2_import.pr0file"
    $il2Import = Run-Engine @("--profile", $il2ImportProfile, "il2", "import", $il2Root, "--confirm", "false", "--text")
    Assert-True ($il2Import.ExitCode -eq 0) "il2 import of the fixture failed."
    $il2Export = Run-Engine @("--profile", $il2ImportProfile, "il2", "export", $il2Root, "--mode", "merge-overwrite", "--text")
    Assert-True ($il2Export.ExitCode -eq 0) "il2 export after import failed."
    Assert-True (Test-Path ($il2GlobalActions + ".jpbak")) "il2 export should back up global.actions before overwriting it."
    Assert-Match (Get-Content -Raw $il2GlobalActions) "rpc_fire" "il2 import/export round trip lost rpc_fire."

    # IL-2 writes the last 8 GUID bytes reversed as one group; import must turn them back into the device's name.
    # Conflicts are per device; clean keeps keyboard lines and other devices, nuke clears the other joysticks;
    # an export that changes nothing writes nothing.
    $il2DevRoot = Join-Path $tempDir "il2dev"
    $il2DevInput = Join-Path $il2DevRoot "data\\input"
    New-Item -Path $il2DevInput -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $il2DevInput "devices.txt"), "configId,guid,model|`r`n0,%2211111111-2222-3333-0000545345440280%22,Smoke%20Stick|`r`n1,%2266666666-7777-8888-0000545345441180%22,Old%20Pedals|`r`n")
    [System.IO.File]::WriteAllText((Join-Path $il2DevInput "global.actions"), "// Input map preset.`r`n&actions=action,command,invert|`r`ngear, key_g, 0|`r`ngear, joy0_b0, 0|`r`nrpc_yaw, joy1_axis_z, 0|")
    $il2DevProfile = Join-Path $tempDir "il2dev.pr0file"
    $il2DevImport = Run-Engine @("--profile", $il2DevProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--text")
    Assert-True ($il2DevImport.ExitCode -eq 0) "il2 import of the reversed-GUID fixture failed."
    $il2DevBinds = Run-Engine @("--profile", $il2DevProfile, "binds", "list", "--text")
    Assert-Match $il2DevBinds.Output "Smoke Stick \{11111111-2222-3333-8002-444553540000\}" "il2 import must turn IL-2's reversed GUID back into the device name."
    # A response row without any dead zone is a centred axis: importing it must not make the shared bind a slider
    # (the DCS export would then write slider = true on pitch/roll/yaw).
    [System.IO.File]::WriteAllText((Join-Path $il2DevInput "current.responses"), "type1Devices=actionId%2CcenterDeadZone%2CsensitivityBallance%2CsideDeadZone%7C%0D%0Arpc_yaw%2C0%2C0%2C0.0%7C%0D%0A")
    $il2RespProfile = Join-Path $tempDir "il2resp.pr0file"
    Run-Engine @("--profile", $il2RespProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--import-deadzone", "--text") | Out-Null
    $il2RespBinds = Run-Engine @("--profile", $il2RespProfile, "relations", "show", "rpc_yaw__Old_Pedals_66666666-7777-8888-8011-444553540000__JOY_Z__rpc_yaw__single", "--text")
    Assert-Match $il2RespBinds.Output "Bind Inverted/Slider/DoubleTap: \S+ / false" "il2 import of a response row without dead zones must not mark the axis as a slider."
    Assert-NotMatch $il2RespBinds.Output "Bind Inverted/Slider/DoubleTap: \S+ / true" "il2 import must not turn a centred axis into a slider."
    # Responses the base holds (curvature = 2 x balance, dead zone = 4 x centre) stay in the base; one with both dead
    # zones is kept as the relation's IL-2 tuning and written back as it was.
    $il2YawRelation = "rpc_yaw__Old_Pedals_66666666-7777-8888-8011-444553540000__JOY_Z__rpc_yaw__single"
    $il2ResponsesFile = Join-Path $il2DevInput "current.responses"
    [System.IO.File]::WriteAllText($il2ResponsesFile, "type1Devices=actionId%2CcenterDeadZone%2CsensitivityBallance%2CsideDeadZone%7C%0D%0Arpc_yaw%2C0.05%2C0.2%2C0")
    $il2TuneProfile = Join-Path $tempDir "il2tune.pr0file"
    Run-Engine @("--profile", $il2TuneProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--import-deadzone", "--import-curvature", "--text") | Out-Null
    $il2TuneShow = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "show", $il2YawRelation, "--game", "IL2Game", "--text")
    Assert-Match $il2TuneShow.Output "uses \(il2, from base, fitted to the base curve\): .*n\.centerDeadZone=0\.05;n\.sensitivityBallance=0\.2;n\.sideDeadZone=0" "il2 import of a response the base holds should keep it in the base."
    [System.IO.File]::WriteAllText($il2ResponsesFile, "type1Devices=actionId%2CcenterDeadZone%2CsensitivityBallance%2CsideDeadZone%7C%0D%0Arpc_yaw%2C0.06%2C0.26%2C0.1")
    Run-Engine @("--profile", $il2TuneProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--import-deadzone", "--import-curvature", "--text") | Out-Null
    $il2TuneShow = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "show", $il2YawRelation, "--game", "IL2Game", "--text")
    Assert-Match $il2TuneShow.Output "uses \(il2, from game:IL2Game\): .*n\.centerDeadZone=0\.06;n\.sensitivityBallance=0\.26;n\.sideDeadZone=0\.1" "il2 import of a response with both dead zones should store it as the relation's IL-2 tuning."
    Remove-Item -LiteralPath $il2ResponsesFile -Force
    $il2TuneExport = Run-Engine @("--profile", $il2TuneProfile, "il2", "export", $il2DevRoot, "--mode", "merge-overwrite", "--text")
    Assert-True ($il2TuneExport.ExitCode -eq 0) "il2 export of an IL-2 tuning failed."
    Assert-Match ([System.IO.File]::ReadAllText($il2ResponsesFile)) "rpc_yaw%2C0\.06%2C0\.26%2C0\.1(%7C|$)" "il2 export should write the relation's IL-2 tuning to current.responses."
    # Settings made for the IL-2 family in Korea's units reach Great Battles converted.
    Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "clear", $il2YawRelation, "--scope", "game:IL2Game", "--text") | Out-Null
    Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "set", $il2YawRelation, "--scope", "family:il2", "--param", "sensitivity=40", "--param", "deadZone0=4", "--text") | Out-Null
    Run-Engine @("--profile", $il2TuneProfile, "il2", "export", $il2DevRoot, "--mode", "merge-overwrite", "--text") | Out-Null
    Assert-Match ([System.IO.File]::ReadAllText($il2ResponsesFile)) "rpc_yaw%2C0\.04%2C0\.2%2C0(%7C|$)" "il2 export should convert IL-2 family settings in Korea units."
    $il2TunePreview = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "preview", $il2YawRelation, "--game", "IL2Game", "--points", "5", "--text")
    Assert-Match $il2TunePreview.Output "-0\.500 -> -0\.34509[67]" "il2 preview should evaluate the Great Battles curve (balance 0.2, centre 0.04)."
    Remove-Item -LiteralPath $il2ResponsesFile -Force
    # One-sided axes are type2 rows (high/low dead zone, linear over the whole travel); a type2 row wins over a type1
    # row in the game, so JoyPro writes an action in one section only. Pitch with a centre shift goes to global.pitch.
    $il2PitchFile = Join-Path (Split-Path -Parent $il2ResponsesFile) "global.pitch"
    Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "clear", $il2YawRelation, "--scope", "family:il2", "--text") | Out-Null
    Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "set", $il2YawRelation, "--scope", "game:IL2Game", "--slider", "true", "--param", "highValuesDeadZone=0.1", "--param", "lowValuesDeadZone=0.06", "--text") | Out-Null
    $il2SliderPreview = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "preview", $il2YawRelation, "--game", "IL2Game", "--points", "11", "--text")
    Assert-Match $il2SliderPreview.Output "-0\.800 -> -0\.90476" "il2 preview of a one-sided axis should be linear between the low (0.06) and high (0.1) dead zone."
    Run-Engine @("--profile", $il2TuneProfile, "il2", "export", $il2DevRoot, "--mode", "merge-overwrite", "--text") | Out-Null
    $il2TypeText = [System.IO.File]::ReadAllText($il2ResponsesFile)
    Assert-Match $il2TypeText "type2Devices=actionId%2ChighValuesDeadZone%2ClowValuesDeadZone%7C%0D%0Arpc_all_engines_throttles%2C0%2C0%7C%0D%0Arpc_yaw%2C0\.1%2C0\.06&" ("il2 export should write a one-sided axis as a type2 row: " + $il2TypeText)
    Assert-True (-not ($il2TypeText -match "type1Devices=[^&]*rpc_yaw")) "il2 export must not also write the one-sided axis as a type1 row."
    Assert-True (-not (Test-Path -LiteralPath $il2PitchFile)) "il2 export must not create global.pitch without a pitch shift."
    # Import: the type2 row comes back as a slider with its dead zones; a game-made global.pitch row with a shift too.
    [System.IO.File]::WriteAllText($il2PitchFile, "type3Devices=actionId%2CcenterDeadZone%2CpitchShift%2CsensitivityBallance%2CsideDeadZone%7C%0D%0Arpc_yaw%2C0%2C0.2%2C0.1%2C0")
    Run-Engine @("--profile", $il2TuneProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--import-deadzone", "--import-curvature", "--text") | Out-Null
    $il2PitchShow = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "show", $il2YawRelation, "--game", "IL2Game", "--text")
    Assert-Match $il2PitchShow.Output "n\.pitchShift=0\.2" ("il2 import should read the global.pitch row (it wins over current.responses): " + $il2PitchShow.Output)
    Remove-Item -LiteralPath $il2PitchFile -Force
    Run-Engine @("--profile", $il2TuneProfile, "il2", "import", $il2DevRoot, "--confirm", "false", "--import-deadzone", "--import-curvature", "--text") | Out-Null
    $il2SliderShow = Run-Engine @("--profile", $il2TuneProfile, "binds", "tuning", "show", $il2YawRelation, "--game", "IL2Game", "--text")
    Assert-Match $il2SliderShow.Output "n\.highValuesDeadZone=0\.1;n\.lowValuesDeadZone=0\.06" ("il2 import should read a type2 row as the one-sided dead zones: " + $il2SliderShow.Output)
    Remove-Item -LiteralPath $il2ResponsesFile -Force
    # Settings > Input devices (force feedback, joystick noise filter) live in data\input\current.devices, one URL query
    # line the game writes on ACCEPT: JoyPro edits its keys in place, the mouse keys stay, and it does not create the file.
    $il2DevicesFile = Join-Path (Split-Path -Parent $il2ResponsesFile) "current.devices"
    Run-Engine @("--profile", $il2TuneProfile, "ffb", "set", "--device", "*", "--game", "IL2Game", "--param", "enabled=false", "--param", "force=0.7", "--param", "amplitude=0.6", "--param", "joystickFilter=0.25", "--text") | Out-Null
    $il2NoDevices = Run-Engine @("--profile", $il2TuneProfile, "il2", "export", $il2DevRoot, "--mode", "merge-overwrite", "--text")
    Assert-True (-not (Test-Path -LiteralPath $il2DevicesFile)) "il2 export must not create current.devices (its mouse values have no known defaults)."
    Assert-Match $il2NoDevices.Output "current\.devices" "il2 export should say that current.devices does not exist yet."
    [System.IO.File]::WriteAllText($il2DevicesFile, "ffEnabled=1&ffShaking=1&mouseAcceleration=0&ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0.13")
    Run-Engine @("--profile", $il2TuneProfile, "il2", "export", $il2DevRoot, "--mode", "merge-overwrite", "--text") | Out-Null
    Assert-True ([System.IO.File]::ReadAllText($il2DevicesFile) -eq "ffEnabled=0&ffShaking=0.6&mouseAcceleration=0&ffForce=0.7&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0.25") ("il2 export should set force feedback and the noise filter in current.devices: " + [System.IO.File]::ReadAllText($il2DevicesFile))
    Run-Engine @("--profile", $il2TuneProfile, "ffb", "clear", "--device", "*", "--game", "IL2Game", "--text") | Out-Null
    Remove-Item -LiteralPath $il2DevicesFile -Force
    # A profile with force feedback settings but no IL-2 binds still writes them (binds stay untouched).
    $il2FfbOnlyProfile = Join-Path $tempDir "il2ffbonly.pr0file"
    [System.IO.File]::WriteAllText($il2DevicesFile, "ffEnabled=1&ffShaking=1&mouseAcceleration=0&ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0")
    Run-Engine @("--profile", $il2FfbOnlyProfile, "ffb", "set", "--device", "*", "--game", "IL2Game", "--param", "joystickFilter=0.4", "--text") | Out-Null
    $il2GlobalBefore = [System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $il2ResponsesFile) "global.actions"))
    Run-Engine @("--profile", $il2FfbOnlyProfile, "il2", "export", $il2DevRoot, "--mode", "merge", "--text") | Out-Null
    Assert-True ([System.IO.File]::ReadAllText($il2DevicesFile) -eq "ffEnabled=1&ffShaking=1&mouseAcceleration=0&ffForce=1&mouseSensitivity=4&mouseInvertYAxis=0&joystickFilter=0.4") ("il2 export without binds should still write the noise filter: " + [System.IO.File]::ReadAllText($il2DevicesFile))
    Assert-True ([System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $il2ResponsesFile) "global.actions")) -eq $il2GlobalBefore) "il2 export without binds must leave global.actions unchanged."
    Remove-Item -LiteralPath $il2DevicesFile -Force
    $il2CleanProfile = Join-Path $tempDir "il2clean.pr0file"
    Run-Engine @("--profile", $il2CleanProfile, "relations", "add", "IL2 Gear", "--button", "--node", "IL2Game:gear", "--device", "Smoke Stick {11111111-2222-3333-8002-444553540000}", "--input", "JOY_BTN5", "--text") | Out-Null
    $il2Clean = Run-Engine @("--profile", $il2CleanProfile, "il2", "export", $il2DevRoot, "--mode", "clean", "--text")
    Assert-True ($il2Clean.ExitCode -eq 0) "il2 clean export failed."
    $il2CleanText = [System.IO.File]::ReadAllText((Join-Path $il2DevInput "global.actions"))
    Assert-Match $il2CleanText "gear, joy0_b4" "il2 clean export must write the profile's bind on the device's old number."
    Assert-NotMatch $il2CleanText "joy0_b0" "il2 clean export must release the other binds of the device it writes."
    Assert-Match $il2CleanText "gear, key_g" "il2 clean export must keep keyboard lines."
    Assert-Match $il2CleanText "rpc_yaw, joy1_axis_z" "il2 clean export must keep the binds of devices the profile does not use."
    Assert-Match ([System.IO.File]::ReadAllText((Join-Path $il2DevInput "devices.txt"))) "11111111-2222-3333-0000545345440280" "il2 export must write the device GUID in IL-2's form."
    $il2Stamp = (Get-Item (Join-Path $il2DevInput "global.actions")).LastWriteTimeUtc
    Start-Sleep -Milliseconds 50
    Run-Engine @("--profile", $il2CleanProfile, "il2", "export", $il2DevRoot, "--mode", "add", "--text") | Out-Null
    Assert-True ((Get-Item (Join-Path $il2DevInput "global.actions")).LastWriteTimeUtc -eq $il2Stamp) "il2 add export of binds the file already has must not rewrite global.actions."
    $il2Nuke = Run-Engine @("--profile", $il2CleanProfile, "il2", "export", $il2DevRoot, "--mode", "clean", "--nuke-untouched-devices", "true", "--text")
    Assert-True ($il2Nuke.ExitCode -eq 0) "il2 nuke export failed."
    $il2NukeText = [System.IO.File]::ReadAllText((Join-Path $il2DevInput "global.actions"))
    Assert-NotMatch $il2NukeText "joy1_" "il2 nuke must clear the joysticks the profile does not use."
    Assert-Match $il2NukeText "gear, key_g" "il2 nuke must keep keyboard lines."

    # Registry game WARDOGS: synthetic GameUserSettings.ini and fake devices (no hardware needed).
    $wdFixtureDir = Join-Path $engineDir "tests\\fixtures\\wardogs"
    $wdIni = Join-Path $tempDir "wardogs\\GameUserSettings.ini"
    $wdProfile = Join-Path $tempDir "wardogs.pr0file"
    New-Item -Path (Split-Path $wdIni) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $wdFixtureDir "GameUserSettings.ini") -Destination $wdIni -Force
    $wdOriginalText = [System.IO.File]::ReadAllText($wdIni)
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $wdFixtureDir "devices.tsv"
    try {
        $wdIntegrations = Run-Engine @("games", "integrations", "--text")
        Assert-True ($wdIntegrations.ExitCode -eq 0) "games integrations failed."
        Assert-Match $wdIntegrations.Output "Wardogs \| WARDOGS" "games integrations should list WARDOGS."

        $wdScan = Run-Engine @("wardogs", "scan", $wdIni, "--text")
        Assert-True ($wdScan.ExitCode -eq 0) "wardogs scan failed."
        Assert-Match $wdScan.Output "(?m)^Bindings Read:\s+7\s*$" "wardogs scan should read 7 bindings from the fixture."
        Assert-Match $wdScan.Output "Pitch <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_Y" "wardogs scan should map the HID identifier with spaces to the JoyPro device."
        Assert-Match $wdScan.Output "LookPitch\.Positive <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN7" "wardogs scan should read axis button pairs."

        $wdImport = Run-Engine @("--profile", $wdProfile, "wardogs", "import", $wdIni, "--text")
        Assert-True ($wdImport.ExitCode -eq 0) "wardogs import failed."
        Assert-Match $wdImport.Output "(?m)^Relations Imported:\s+7\s*$" "wardogs import should create 7 relations."

        $wdRoundTrip = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--text")
        Assert-True ($wdRoundTrip.ExitCode -eq 0) "wardogs export after import failed."
        # WARDOGS names devices by their DirectInput name; the throttle line still has the HID name an older JoyPro
        # wrote (the game shows it as not connected), so the export repairs exactly that and nothing else.
        Assert-Match $wdRoundTrip.Output "Fixed the WARDOGS device name 'ABCD:0001:Smoke Throttle \(ARM\)' -> 'ABCD:0001:Smoke Throttle'" "wardogs export should repair a device name the game does not know."
        # With HOTAS off the game ignores joystick binds: an export that writes binds turns it on.
        $wdHotasIni = Join-Path $tempDir "wardogs-hotas\\GameUserSettings.ini"
        New-Item -Path (Split-Path $wdHotasIni) -ItemType Directory -Force | Out-Null
        [System.IO.File]::WriteAllText($wdHotasIni, $wdOriginalText.Replace("bUseHOTASHelicopters=True", "bUseHOTASHelicopters=False"))
        $wdHotas = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdHotasIni, "--mode", "clean", "--text")
        Assert-Match $wdHotas.Output "Turned on 'Enable HOTAS'" "wardogs export should report turning HOTAS on."
        Assert-Match ([System.IO.File]::ReadAllText($wdHotasIni)) "(?m)^bUseHOTASHelicopters=True" "wardogs export with binds must turn HOTAS on."
        $wdOriginalText = $wdOriginalText.Replace("ABCD:0001:Smoke Throttle (ARM)", "ABCD:0001:Smoke Throttle")
        Assert-True ([System.IO.File]::ReadAllText($wdIni) -eq $wdOriginalText) "wardogs import/export round trip must leave the file byte-identical apart from the repaired device name."

        $wdSetFire = Run-Engine @("--profile", $wdProfile, "binds", "set", "Fire", "--input", "JOY_BTN3", "--text")
        Assert-True ($wdSetFire.ExitCode -eq 0) "binds set on the imported WARDOGS relation failed."
        $wdDryRun = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--dry-run", "--text")
        Assert-Match $wdDryRun.Output "1 line\(s\) of the joystick section would change" "wardogs dry run should report one changed line."
        Assert-True ([System.IO.File]::ReadAllText($wdIni) -eq $wdOriginalText) "wardogs dry run must not write."

        $wdExport = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--text")
        Assert-True ($wdExport.ExitCode -eq 0) "wardogs export failed."
        $wdAfter = [System.IO.File]::ReadAllText($wdIni)
        Assert-Match $wdAfter 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick",_aaaaljahlggohoapfcfef=2\)' "wardogs export should write the new Fire button (0-based)."
        Assert-Match $wdAfter "Action=UnknownFutureAction" "wardogs merge-overwrite export must keep actions JoyPro does not manage."
        Assert-Match $wdAfter "(?m)^bUseVSync=False\r?$" "wardogs export must keep other sections."
        $wdChangedLines = @(Compare-Object ($wdOriginalText -split "`r`n") ($wdAfter -split "`r`n")).Count
        Assert-True ($wdChangedLines -eq 2) ("wardogs export should change exactly one line (diff entries: " + $wdChangedLines + ").")
        Assert-True (Test-Path ($wdIni + ".jpbak")) "wardogs export should back up the ini."

        # WARDOGS also has infantry: clean and nuke only release cataloged helicopter actions.
        $wdClean = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--mode", "clean", "--text")
        Assert-True ($wdClean.ExitCode -eq 0) "wardogs clean export failed."
        $wdCleanText = [System.IO.File]::ReadAllText($wdIni)
        Assert-Match $wdCleanText "Action=UnknownFutureAction" "wardogs clean export must keep actions JoyPro does not manage."
        Assert-Match $wdCleanText 'Action=Fire,Binding=\(DeviceIdentifier="1234:5678:Smoke Stick"' "wardogs clean export must keep the profile's binds."
        if (-not (Skip-WithoutGameData "WARDOGS nuke on the cataloged helicopter actions (DB\Wardogs)")) {
        foreach ($wdThrottleRelation in @("Collective / throttle", "Look down (button)", "Look up (button)")) {
            $wdUnbind = Run-Engine @("--profile", $wdProfile, "binds", "clear", $wdThrottleRelation, "--text")
            Assert-True ($wdUnbind.ExitCode -eq 0) ("binds clear failed for " + $wdThrottleRelation)
        }
        $wdKeep = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--text")
        Assert-Match ([System.IO.File]::ReadAllText($wdIni)) 'ABCD:0001:Smoke Throttle"' "wardogs merge-overwrite without nuke must keep binds of devices the profile no longer uses."
        $wdNuke = Run-Engine @("--profile", $wdProfile, "wardogs", "export", $wdIni, "--nuke-untouched-devices", "--text")
        Assert-True ($wdNuke.ExitCode -eq 0) "wardogs export with nuke failed."
        $wdNukeText = [System.IO.File]::ReadAllText($wdIni)
        Assert-True (([regex]::Matches($wdNukeText, 'ABCD:0001:Smoke Throttle"')).Count -eq 1) "wardogs nuke should clear the helicopter binds of a device the profile does not use."
        Assert-Match $wdNukeText 'Action=UnknownFutureAction,Binding=\(DeviceIdentifier="ABCD:0001:Smoke Throttle"' "wardogs nuke must keep actions JoyPro does not manage (with the device name repaired)."
        Assert-Match $wdNukeText 'DeviceIdentifier="1234:5678:Smoke Stick"' "wardogs nuke must keep the binds of used devices."
        }

        $wdNoSectionIni = Join-Path $tempDir "wardogs\\NoSection.ini"
        Set-Content -Path $wdNoSectionIni -Value "[/Script/Engine.GameUserSettings]`r`nbUseVSync=False" -Encoding ASCII
        $wdNoSection = Run-Engine @("wardogs", "scan", $wdNoSectionIni, "--text")
        Assert-True ($wdNoSection.ExitCode -ne 0) "wardogs scan without joystick section should fail."
        Assert-Match $wdNoSection.Output "Open the HOTAS settings in the game once" "wardogs should explain how to create the joystick section."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    if (-not (Skip-WithoutGameData "Arma 3, Arma 2 and Arma 2: OA scan/import/export (DB\Arma3, DB\Arma2, DB\Arma2OA)")) {
    # Registry game Arma 3: synthetic profile; half-axis composites, ownership sidecar, untouched extra binds.
    $a3Profile = Join-Path $tempDir "arma3\\Smoke.Arma3Profile"
    $a3JoyProProfile = Join-Path $tempDir "arma3.pr0file"
    New-Item -Path (Split-Path $a3Profile) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\arma3\\Smoke.Arma3Profile") -Destination $a3Profile -Force
    $a3OriginalText = [System.IO.File]::ReadAllText($a3Profile)
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\wardogs\\devices.tsv"
    try {
        $a3Scan = Run-Engine @("arma3", "scan", $a3Profile, "--text")
        Assert-True ($a3Scan.ExitCode -eq 0) "arma3 scan failed."
        Assert-Match $a3Scan.Output "(?m)^Bindings Read:\s+5\s*$" "arma3 scan should read 5 bindings from the fixture."
        Assert-Match $a3Scan.Output "Axis\.HeliCyclicPitch <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_Y" "arma3 scan should combine half axes into Axis.HeliCyclicPitch."
        Assert-Match $a3Scan.Output "AutoHover <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN10" "arma3 scan should decode joystick buttons (0-based codes)."

        $a3Import = Run-Engine @("--profile", $a3JoyProProfile, "arma3", "import", $a3Profile, "--text")
        Assert-True ($a3Import.ExitCode -eq 0) "arma3 import failed."
        Assert-Match $a3Import.Output "(?m)^Conflicts Kept:\s+0\s*$" "arma3 import should take both collective axes (one relation per bind), not drop the second."
        Assert-Match $a3Import.Output "(?m)^Relations Imported:\s+5\s*$" "arma3 import should create a relation for each of the 5 binds."
        Assert-True (Test-Path ($a3Profile + ".joypro.json")) "arma3 import should record the codes JoyPro took over."

        $a3RoundTrip = Run-Engine @("--profile", $a3JoyProProfile, "arma3", "export", $a3Profile, "--text")
        Assert-True ($a3RoundTrip.ExitCode -eq 0) "arma3 export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($a3Profile) -eq $a3OriginalText) "arma3 import/export round trip must leave the profile byte-identical."

        $a3Set = Run-Engine @("--profile", $a3JoyProProfile, "binds", "set", "Auto hover", "--input", "JOY_BTN3", "--text")
        Assert-True ($a3Set.ExitCode -eq 0) "binds set on the imported Arma 3 relation failed."
        $a3Export = Run-Engine @("--profile", $a3JoyProProfile, "arma3", "export", $a3Profile, "--text")
        Assert-True ($a3Export.ExitCode -eq 0) "arma3 export after rebind failed."
        $a3After = [System.IO.File]::ReadAllText($a3Profile)
        Assert-Match $a3After "(?m)^keyAutoHover\[\]=\{45,131330\};" "arma3 export should replace only JoyPro's old code and keep the keyboard key."
        Assert-Match $a3After "(?m)^keyHeliCollectiveRaiseCont\[\]=\{197130,197134\};" "arma3 export must keep the extra collective axis the user bound in the game."
        Assert-Match $a3After "(?m)^keyReloadMagazine\[\]=\{19,131330\};" "arma3 export must not touch non-flight actions."
        Assert-Match $a3After "(?m)^keyHeliCyclicForward\[\]=\{17,200,196873\};" "arma3 export must keep keyboard codes on flight actions."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry games Arma 2: Operation Arrowhead and Arma 2: synthetic profiles after the game detected the Smoke Stick
    # (offset 256, with the game's own joystick defaults) and the Smoke Throttle (offset 512; disabled in Arma 2).
    # OA writes Thrust (Analogue) as a full axis (both halves, negative first); Arma 2 uses one half; 32 buttons only.
    $a2Dir = Join-Path $tempDir "arma2"
    New-Item -Path $a2Dir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\arma2\\*") -Destination $a2Dir -Force
    $a2OaProfile = Join-Path $a2Dir "Smoke.ArmA2OAProfile"
    $a2Profile = Join-Path $a2Dir "Smoke.ArmA2Profile"
    $a2Stick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $a2Throttle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
    try {
        $oaScan = Run-Engine @("arma2oa", "scan", $a2OaProfile, "--text")
        Assert-True ($oaScan.ExitCode -eq 0) "arma2oa scan failed. Output: $($oaScan.Output)"
        Assert-Match $oaScan.Output "Axis\.Pitch <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_Y" "arma2oa scan should combine HeliForward/HeliBack into Axis.Pitch."
        Assert-Match $oaScan.Output "ToggleWeapons <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN3" "arma2oa scan should decode the game's default stick buttons."

        $oaJoyPro = Join-Path $tempDir "arma2oa.pr0file"
        Run-Engine @("--profile", $oaJoyPro, "relations", "add", "OA Pitch", "--axis", "--node", "Arma2OA:Axis.Pitch", "--device", $a2Stick, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $oaJoyPro, "relations", "add", "OA Throttle", "--axis", "--node", "Arma2OA:Axis.Throttle", "--device", $a2Throttle, "--input", "JOY_Z", "--text") | Out-Null
        Run-Engine @("--profile", $oaJoyPro, "relations", "add", "OA Gear", "--button", "--node", "Arma2OA:LandGear", "--device", $a2Throttle, "--input", "JOY_BTN5", "--text") | Out-Null
        Run-Engine @("--profile", $oaJoyPro, "relations", "add", "OA Flaps", "--button", "--node", "Arma2OA:FlapsDown", "--device", $a2Stick, "--input", "JOY_BTN40", "--text") | Out-Null
        $oaExport = Run-Engine @("--profile", $oaJoyPro, "arma2oa", "export", $a2OaProfile, "--mode", "clean", "--text")
        Assert-True ($oaExport.ExitCode -eq 0) "arma2oa export failed. Output: $($oaExport.Output)"
        Assert-Match $oaExport.Output "reads only the first 32 buttons" "arma2oa export should refuse buttons above 32."
        $oaText = [System.IO.File]::ReadAllText($a2OaProfile)
        Assert-Match $oaText "(?m)^keyHeliThrottlePos\[\]=\{197130,197122\};" "arma2oa export should write Thrust (Analogue) as a full axis, negative half first."
        Assert-Match $oaText "(?m)^keyHeliForward\[\]=\{17,200,1048579,196873\};" "arma2oa export should keep keyboard and mouse codes and write the stick's negative Y half."
        Assert-Match $oaText "(?m)^keyHeliBack\[\]=\{31,208,1048578,196865\};" "arma2oa export should write the positive Y half to HeliBack."
        Assert-Match $oaText "(?m)^keyLandGear\[\]=\{-1660944350,131588\};" "arma2oa export should keep the RCtrl combo and add throttle button 5."
        Assert-Match $oaText "(?m)^keyHeliCyclicLeft\[\]=\{30,203\};" "arma2oa clean export should release the game's default stick codes on flight actions."
        Assert-Match $oaText "(?m)^keyFire\[\]=\{" "arma2oa export must keep non-flight lines."
        Assert-True ($oaText.Contains("`r`n") -and (Get-Content -Path $a2OaProfile -Encoding Byte -TotalCount 1)[0] -ne 0xEF) "arma2oa export must keep CRLF and no BOM."
        $oaAgain = Run-Engine @("--profile", $oaJoyPro, "arma2oa", "export", $a2OaProfile, "--mode", "clean", "--text")
        Assert-Match $oaAgain.Output "already matches" "a second arma2oa export should change nothing."
        Run-Engine @("--profile", $oaJoyPro, "binds", "set", "OA Throttle", "--inverted", "true", "--text") | Out-Null
        Run-Engine @("--profile", $oaJoyPro, "arma2oa", "export", $a2OaProfile, "--mode", "clean", "--text") | Out-Null
        Assert-Match ([System.IO.File]::ReadAllText($a2OaProfile)) "(?m)^keyHeliThrottlePos\[\]=\{197122,197130\};" "an inverted full axis should start with the positive half."

        $a2JoyPro = Join-Path $tempDir "arma2.pr0file"
        Run-Engine @("--profile", $a2JoyPro, "relations", "add", "A2 Throttle", "--axis", "--node", "Arma2:Axis.Throttle", "--device", $a2Stick, "--input", "JOY_Z", "--text") | Out-Null
        Run-Engine @("--profile", $a2JoyPro, "relations", "add", "A2 Gear", "--button", "--node", "Arma2:LandGear", "--device", $a2Throttle, "--input", "JOY_BTN2", "--text") | Out-Null
        $a2Export = Run-Engine @("--profile", $a2JoyPro, "arma2", "export", $a2Profile, "--mode", "clean", "--text")
        Assert-True ($a2Export.ExitCode -eq 0) "arma2 export failed. Output: $($a2Export.Output)"
        Assert-Match $a2Export.Output 'was switched off in Arma 2 \(mode "Disable"\); JoyPro enabled it' "arma2 export should enable a disabled device that gets binds (and say so)."
        $a2Text = [System.IO.File]::ReadAllText($a2Profile)
        Assert-Match $a2Text '(?s)name="Smoke Throttle \(ARM\)";\s*offset=512;\s*isXInput=0;\s*mode="Custom";' "arma2 export should switch the throttle to mode Custom."
        Assert-Match $a2Text "(?m)^keyLandGear\[\]=\{[^}]*131585\};" "arma2 export should write the bind of the device it enabled."
        Assert-Match $a2Text "(?m)^keyHeliThrottlePos\[\]=\{196874\};" "arma2 export should write one half for the throttle (no full axis in Arma 2)."
        Assert-Match $a2Text "(?m)^keyHeliForward\[\]=\{17,200,1048579\};" "arma2 clean export should release the stick's default pitch code."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }
    }

    # Registry games VTOL VR: the BYOJoystick mod files (one per vehicle seat, DirectInput GUIDs) and the game's own
    # inputbindings.cfg (Rewired maps keyed by device model; only rudder and toe brakes). Fake devices include the real
    # MFG Crosswind V2 and Thrustmaster T-Rudder ids.
    $vtolFixture = Join-Path $engineDir "tests\\fixtures\\vtolvr"
    $vtolSaveData = Join-Path $tempDir "vtol\\steamapps\\common\\VTOL VR\\SaveData"
    New-Item -Path $vtolSaveData -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $vtolFixture "byoj\\BYOJ_FA26B.xml") -Destination $vtolSaveData -Force
    $vtolNativeDir = Join-Path $tempDir "vtol\\Roaming\\SaveData"
    New-Item -Path $vtolNativeDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $vtolFixture "native\\*") -Destination $vtolNativeDir -Force
    $vtolNative = Join-Path $vtolNativeDir "inputbindings.cfg"
    $vtolNativeOriginal = [System.IO.File]::ReadAllBytes($vtolNative)
    $vtolStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $vtolThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    $vtolPedals = "MFG Crosswind V2 {42ce2930-0a3f-11f0-8001-444553540000}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $vtolFixture "devices.tsv"
    try {
        $byojScan = Run-Engine @("vtolvr", "scan", $vtolSaveData, "--text")
        Assert-True ($byojScan.ExitCode -eq 0) "vtolvr scan failed. Output: $($byojScan.Output)"
        Assert-Match $byojScan.Output "Joystick Pitch <- BYOJ Device \{8A3C7E20-6B1F-11ef-8001-444553540000\} JOY_Y" "vtolvr scan should read BYOJoystick axis binds."
        Assert-Match $byojScan.Output "Fire Countermeasures <- .* JOY_BTN3 \+ .* JOY_BTN5" "vtolvr scan should read BYOJoystick modifier binds."
        Assert-Match $byojScan.Output "Landing Gear Toggle <- Keyboard G" "vtolvr scan should read BYOJoystick keyboard binds."

        if (-not (Skip-WithoutGameData "VTOL VR BYOJoystick export to the seat files (DB\VTOLVR targets)")) {
        $byojProfile = Join-Path $tempDir "vtolvr.pr0file"
        Run-Engine @("--profile", $byojProfile, "relations", "add", "V Pitch", "--axis", "--node", "VTOLVR:Joystick Pitch", "--device", $vtolStick, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $byojProfile, "relations", "add", "V Throttle", "--axis", "--node", "VTOLVR:Throttle", "--device", $vtolThrottle, "--input", "JOY_Z", "--text") | Out-Null
        Run-Engine @("--profile", $byojProfile, "relations", "add", "V Gear", "--button", "--node", "VTOLVR:Landing Gear Toggle", "--device", $vtolThrottle, "--input", "JOY_BTN5", "--text") | Out-Null
        Run-Engine @("--profile", $byojProfile, "relations", "add", "V Diagonal", "--button", "--node", "VTOLVR:Fire Weapon", "--device", $vtolStick, "--input", "JOY_BTN_POV1_UR", "--text") | Out-Null
        Run-Engine @("--profile", $byojProfile, "binds", "set", "V Pitch", "--deadzone", "0.05", "--inverted", "true", "--text") | Out-Null
        $byojExport = Run-Engine @("--profile", $byojProfile, "vtolvr", "export", "--path", $vtolSaveData, "--mode", "clean", "--text")
        Assert-True ($byojExport.ExitCode -eq 0) "vtolvr export failed. Output: $($byojExport.Output)"
        Assert-Match $byojExport.Output "no diagonals" "vtolvr export should refuse hat diagonals."
        $byojAv42 = [System.IO.File]::ReadAllText((Join-Path $vtolSaveData "BYOJ_AV42C.xml"))
        Assert-Match $byojAv42 "<Target>Joystick Pitch</Target>\s*<JoystickId>11111111-2222-3333-4444-555555555555</JoystickId>\s*<Input>Y</Input>\s*<RequiresModifier>false</RequiresModifier>\s*<Invert>true</Invert>\s*<Deadzone>0.05</Deadzone>" "vtolvr export should write the pitch axis with invert and deadzone into each seat."
        $byojAh94 = [System.IO.File]::ReadAllText((Join-Path $vtolSaveData "BYOJ_AH94Front.xml"))
        Assert-Match $byojAh94 "<Target>Landing Gear Toggle</Target>" "vtolvr export should write the gear into the AH-94 front seat."
        Assert-NotMatch $byojAh94 "<Target>Throttle</Target>" "vtolvr export must not write targets a seat does not have (no throttle in the AH-94)."
        Assert-True (-not (Test-Path (Join-Path $vtolSaveData "BYOJ_EF24GRear.xml"))) "vtolvr export must not create a seat file without any of the exported targets."
        $byojFa26 = [System.IO.File]::ReadAllText((Join-Path $vtolSaveData "BYOJ_FA26B.xml"))
        Assert-Match $byojFa26 "<Key>G</Key>" "vtolvr export must keep keyboard binds."
        Assert-Match $byojFa26 "<JoystickId>42cec570-0a3f-11f0-800b-444553540000</JoystickId>" "vtolvr export must keep binds of devices it does not export."
        $byojAgain = Run-Engine @("--profile", $byojProfile, "vtolvr", "export", "--path", $vtolSaveData, "--mode", "clean", "--text")
        Assert-Match $byojAgain.Output "already match" "a second vtolvr export should change nothing."
        }

        $nativeScan = Run-Engine @("vtolvr-native", "scan", $vtolNative, "--text")
        Assert-True ($nativeScan.ExitCode -eq 0) "vtolvr-native scan failed. Output: $($nativeScan.Output)"
        Assert-Match $nativeScan.Output "Rudder <- MFG Crosswind V2 \{42ce2930-0a3f-11f0-8001-444553540000\} JOY_RZ" "vtolvr-native scan should map Rewired Axis 2 of the Crosswind to RZ."
        $nativeProfile = Join-Path $tempDir "vtolvr-native.pr0file"
        Run-Engine @("--profile", $nativeProfile, "relations", "add", "N Rudder", "--axis", "--node", "VTOLVRNative:Rudder", "--device", $vtolPedals, "--input", "JOY_RZ", "--text") | Out-Null
        Run-Engine @("--profile", $nativeProfile, "relations", "add", "N Brake L", "--axis", "--node", "VTOLVRNative:Wheel Brake L", "--device", $vtolPedals, "--input", "JOY_X", "--text") | Out-Null
        Run-Engine @("--profile", $nativeProfile, "relations", "add", "N Brake R", "--axis", "--node", "VTOLVRNative:Wheel Brake R", "--device", $vtolPedals, "--input", "JOY_Y", "--text") | Out-Null
        $nativeSame = Run-Engine @("--profile", $nativeProfile, "vtolvr-native", "export", $vtolNative, "--text")
        Assert-Match $nativeSame.Output "already matches" "a vtolvr-native export of the same binds should change nothing."
        Assert-Match $nativeSame.Output "Hardware Control Rudder is on" "vtolvr-native export should warn while Hardware Control Rudder is off."
        Assert-True ([Convert]::ToBase64String($vtolNativeOriginal) -eq [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($vtolNative))) "vtolvr-native must not touch an unchanged file."
        Run-Engine @("--profile", $nativeProfile, "binds", "set", "N Brake R", "--inverted", "true", "--text") | Out-Null
        $nativeClean = Run-Engine @("--profile", $nativeProfile, "vtolvr-native", "export", $vtolNative, "--mode", "clean", "--text")
        Assert-True ($nativeClean.ExitCode -eq 0) "vtolvr-native export failed. Output: $($nativeClean.Output)"
        $nativeText = [System.IO.File]::ReadAllText($vtolNative)
        Assert-Match $nativeText "<actionId>6</actionId><elementType>0</elementType><elementIdentifierId>1</elementIdentifierId><axisRange>0</axisRange><invert>true</invert>" "vtolvr-native export should invert the right brake."
        Assert-Match $nativeText "hardwareName=""T-Rudder"".*<axisMaps /></JoystickMap>" "vtolvr-native clean export should clear the other pedals."
        Assert-True ($nativeText.EndsWith("}`n`r`n") -and -not $nativeText.Contains("`r`n`t")) "vtolvr-native export must keep LF lines and the game's file ending."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    if (-not (Skip-WithoutGameData "Arma Reforger, War Thunder, X-Plane 12, Falcon BMS, IL-2 Korea, X4, Elite Dangerous, MSFS 2020/2024 scan/import/export (their DB command lists)")) {
    # Registry game Arma Reforger: JoyPro preset file; only helicopter Action blocks may change.
    $arPreset = Join-Path $tempDir "reforger\\Smoke.conf"
    $arNewPreset = Join-Path $tempDir "reforger\\JoyPro.conf"
    $arProfile = Join-Path $tempDir "reforger.pr0file"
    New-Item -Path (Split-Path $arPreset) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\reforger\\Smoke.conf") -Destination $arPreset -Force
    $arOriginalText = [System.IO.File]::ReadAllText($arPreset)
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\wardogs\\devices.tsv"
    try {
        $arScan = Run-Engine @("reforger", "scan", $arPreset, "--text")
        Assert-True ($arScan.ExitCode -eq 0) "reforger scan failed."
        Assert-Match $arScan.Output "(?m)^Bindings Read:\s+3\s*$" "reforger scan should read 3 bindings (cyclic pitch, autohover, engine start)."
        Assert-Match $arScan.Output "HelicopterEngineStart <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN18" "reforger scan should map joystick1 to the second DirectInput device."

        $arImport = Run-Engine @("--profile", $arProfile, "reforger", "import", $arPreset, "--text")
        Assert-True ($arImport.ExitCode -eq 0) "reforger import failed."
        $arRoundTrip = Run-Engine @("--profile", $arProfile, "reforger", "export", $arPreset, "--text")
        Assert-True ($arRoundTrip.ExitCode -eq 0) "reforger export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($arPreset) -eq $arOriginalText) "reforger import/export round trip must leave the preset byte-identical."

        $arSet = Run-Engine @("--profile", $arProfile, "binds", "set", "Autohover toggle", "--input", "JOY_BTN4", "--text")
        Assert-True ($arSet.ExitCode -eq 0) "binds set on the imported Reforger relation failed."
        $arExport = Run-Engine @("--profile", $arProfile, "reforger", "export", $arPreset, "--text")
        Assert-Match $arExport.Output "1 action block\(s\) changed" "reforger export should change exactly the rebound action."
        $arAfter = [System.IO.File]::ReadAllText($arPreset)
        Assert-Match $arAfter 'Input "joystick0:button3"' "reforger export should write the new button (0-based)."
        Assert-Match $arAfter '(?s)Action CharacterFire \{\s*InputSource InputSourceSum "\{0000000000000001\}"' "reforger export must keep non-helicopter blocks untouched."
        Assert-Match $arAfter '"\{0000000000050000\}"' "reforger export must keep unchanged helicopter blocks (and their ids)."

        $arCreate = Run-Engine @("--profile", $arProfile, "reforger", "export", $arNewPreset, "--text")
        Assert-True ($arCreate.ExitCode -eq 0) "reforger export into a new JoyPro preset failed."
        $arCreated = [System.IO.File]::ReadAllText($arNewPreset)
        Assert-Match $arCreated '(?s)^ActionManager \{\s*Actions \{' "reforger should create a complete preset file."
        Assert-Match $arCreated 'FilterPreset "forward"\s*Input "joystick0:axis1-"' "reforger should write the negative half to HelicopterCyclicForward."
        Assert-Match $arCreated 'Filter InputFilterHold "\{[0-9A-F]{16}\}"' "reforger should write the vanilla filter class for engine start."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game War Thunder: synthetic text machine.blk; only cataloged flight shortcuts and axes may change.
    $wtBlk = Join-Path $tempDir "warthunder\\machine.blk"
    $wtProfile = Join-Path $tempDir "warthunder.pr0file"
    New-Item -Path (Split-Path $wtBlk) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\warthunder\\machine.blk") -Destination $wtBlk -Force
    $wtOriginalText = [System.IO.File]::ReadAllText($wtBlk)
    $wtStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\warthunder\\devices.tsv"
    try {
        $wtScan = Run-Engine @("warthunder", "scan", $wtBlk, "--text")
        Assert-True ($wtScan.ExitCode -eq 0) "warthunder scan failed."
        Assert-Match $wtScan.Output "(?m)^Bindings Read:\s+7\s*$" "warthunder scan should read 7 flight bindings from the fixture."
        Assert-Match $wtScan.Output "throttle <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_X" "warthunder scan should subtract the device axis offset."
        Assert-Match $wtScan.Output "ID_FLAPS <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_U \+ Keyboard LeftControl" "warthunder scan should decode hat buttons and keyboard modifiers."
        Assert-Match $wtScan.Output "ID_TOGGLE_ENGINE <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN2 \+ Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN4" "warthunder scan should decode joystick modifiers."
        Assert-NotMatch $wtScan.Output "ID_FIRE_GM" "warthunder scan must ignore non-flight shortcuts."

        $wtImport = Run-Engine @("--profile", $wtProfile, "warthunder", "import", $wtBlk, "--text")
        Assert-True ($wtImport.ExitCode -eq 0) "warthunder import failed."
        Assert-Match $wtImport.Output "(?m)^Relations Imported:\s+7\s*$" "warthunder import should create 7 relations."
        Assert-True (Test-Path ($wtBlk + ".joypro.json")) "warthunder import should record the shortcuts JoyPro took over."

        $wtRoundTrip = Run-Engine @("--profile", $wtProfile, "warthunder", "export", $wtBlk, "--text")
        Assert-True ($wtRoundTrip.ExitCode -eq 0) "warthunder export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($wtBlk) -eq $wtOriginalText) "warthunder import/export round trip must leave machine.blk byte-identical."

        $wtSetGuns = Run-Engine @("--profile", $wtProfile, "binds", "set", "Small-calibre guns", "--input", "JOY_BTN3", "--text")
        Assert-True ($wtSetGuns.ExitCode -eq 0) "binds set on the imported War Thunder relation failed."
        $wtSetThrottle = Run-Engine @("--profile", $wtProfile, "binds", "set", "Throttle axis", "--input", "JOY_SLIDER1", "--text")
        Assert-True ($wtSetThrottle.ExitCode -eq 0) "binds set on the War Thunder throttle failed."
        $wtAddBrake = Run-Engine @("--profile", $wtProfile, "relations", "add", "WT Airbrake", "--button", "--node", "WarThunder:ID_AIR_BRAKE", "--device", $wtStick, "--input", "JOY_BTN5", "--text")
        Assert-True ($wtAddBrake.ExitCode -eq 0) "relations add for War Thunder airbrake failed."
        $wtAddDiagonal = Run-Engine @("--profile", $wtProfile, "relations", "add", "WT Rockets", "--button", "--node", "WarThunder:ID_ROCKETS", "--device", $wtStick, "--input", "JOY_BTN_POV1_UR", "--text")
        Assert-True ($wtAddDiagonal.ExitCode -eq 0) "relations add for War Thunder rockets failed."

        $wtDryRun = Run-Engine @("--profile", $wtProfile, "warthunder", "export", $wtBlk, "--dry-run", "--text")
        Assert-Match $wtDryRun.Output "Dry run: 2 controls section\(s\) would change" "warthunder dry run should report the hotkeys and axes sections."
        Assert-True ([System.IO.File]::ReadAllText($wtBlk) -eq $wtOriginalText) "warthunder dry run must not write."

        $wtExport = Run-Engine @("--profile", $wtProfile, "warthunder", "export", $wtBlk, "--text")
        Assert-True ($wtExport.ExitCode -eq 0) "warthunder export after rebind failed."
        Assert-Match $wtExport.Output "only the four main directions" "warthunder export should skip hat diagonals with a reason."
        $wtAfter = [System.IO.File]::ReadAllText($wtBlk)
        Assert-Match $wtAfter "(?s)ID_FIRE_MGUNS\{\s*mouseButton:i=0\s*\}\s*ID_FIRE_MGUNS\{\s*joyButton:i=30\s*\}" "warthunder export should replace JoyPro's old shortcut and keep the mouse shortcut."
        Assert-NotMatch $wtAfter "joyButton:i=28\r?\n" "warthunder export should drop the shortcut JoyPro owned before."
        Assert-Match $wtAfter "(?s)ID_AIR_BRAKE\{\s*joyButton:i=32\s*\}" "warthunder export should replace the empty 'unbound' marker."
        Assert-Match $wtAfter "(?s)\bthrottle\{\s*axisId:i=16\s*relative:b=no" "warthunder export should add the device axis offset and keep other axis attributes."
        Assert-Match $wtAfter "(?s)elevator\{\s*axisId:i=8\s*inverse:b=yes\s*innerDeadzone:r=0\.05" "warthunder export should keep unchanged axes."
        Assert-Match $wtAfter "(?s)gm_throttle\{\s*axisId:i=13" "warthunder export must not touch non-flight axes."
        Assert-Match $wtAfter "(?s)ID_FIRE_GM\{\s*joyButton:i=29" "warthunder export must not touch non-flight shortcuts."
        Assert-Match $wtAfter "fullScreenMode:b=yes" "warthunder export must keep other sections."
        Assert-True (Test-Path ($wtBlk + ".jpbak")) "warthunder export should back up machine.blk."

        $wtClear = Run-Engine @("--profile", $wtProfile, "binds", "clear", "Toggle Gear", "--text")
        Assert-True ($wtClear.ExitCode -eq 0) "binds clear on the War Thunder gear relation failed."
        $wtClean = Run-Engine @("--profile", $wtProfile, "warthunder", "export", $wtBlk, "--mode", "clean", "--text")
        Assert-True ($wtClean.ExitCode -eq 0) "warthunder clean export failed."
        $wtCleaned = [System.IO.File]::ReadAllText($wtBlk)
        Assert-Match $wtCleaned "(?s)ID_GEAR\{\s*keyboardKey:i=34\s*\}" "warthunder clean export must keep keyboard shortcuts."
        Assert-NotMatch $wtCleaned "joyButton:i=50\r?\n" "warthunder clean export should drop joystick shortcuts JoyPro does not bind."
        Assert-Match $wtCleaned "(?s)ID_FIRE_GM\{\s*joyButton:i=29" "warthunder clean export must keep non-flight shortcuts."
        Assert-Match $wtCleaned "(?s)ID_BOMBS_SERIES\{\s*joyButton:i=5" "warthunder clean export must keep shortcuts of devices that are not connected."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game X-Plane 12 (engine xplane12): synthetic joystick preferences with two identical pedals (told apart by HID path)
    # and a slot whose device is not connected.
    $xpPrf = Join-Path $tempDir "xplane\\X-Plane Joystick Settings.prf"
    $xpProfile = Join-Path $tempDir "xplane.pr0file"
    New-Item -Path (Split-Path $xpPrf) -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\xplane\\X-Plane Joystick Settings.prf") -Destination $xpPrf -Force
    $xpOriginalText = [System.IO.File]::ReadAllText($xpPrf)
    $xpStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $xpPedalsA = "Smoke Pedals {77777777-7777-7777-7777-777777777777}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\xplane\\devices.tsv"
    try {
        $xpScan = Run-Engine @("xplane12", "scan", $xpPrf, "--text")
        Assert-True ($xpScan.ExitCode -eq 0) "xplane scan failed."
        Assert-Match $xpScan.Output "(?m)^Bindings Read:\s+11\s*$" "xplane scan should read 11 flight bindings from the fixture."
        Assert-Match $xpScan.Output "axis/yaw <- Smoke Pedals \{88888888-8888-8888-8888-888888888888\} JOY_RZ" "xplane scan should tell identical pedals apart by HID path."
        Assert-Match $xpScan.Output "sim/flight_controls/pitch_trim_up <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_D" "xplane scan should map the hat buttons behind the device buttons."
        Assert-Match $xpScan.Output "X-Plane slot 4 \(0457:08AE\) is not connected" "xplane scan should report slots of devices that are not connected."
        Assert-NotMatch $xpScan.Output "sim/view/default_view" "xplane scan must ignore non-flight commands."

        $xpImport = Run-Engine @("--profile", $xpProfile, "xplane12", "import", $xpPrf, "--text")
        Assert-True ($xpImport.ExitCode -eq 0) "xplane import failed."
        Assert-Match $xpImport.Output "(?m)^Relations Imported:\s+11\s*$" "xplane import should create 11 relations."
        $xpRoundTrip = Run-Engine @("--profile", $xpProfile, "xplane12", "export", $xpPrf, "--text")
        Assert-True ($xpRoundTrip.ExitCode -eq 0) "xplane export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($xpPrf) -eq $xpOriginalText) "xplane import/export round trip must leave the preferences byte-identical."

        $xpSetGuns = Run-Engine @("--profile", $xpProfile, "binds", "set", "Fire armed guns", "--input", "JOY_BTN3", "--text")
        Assert-True ($xpSetGuns.ExitCode -eq 0) "binds set on the imported X-Plane relation failed."
        $xpSetYaw = Run-Engine @("--profile", $xpProfile, "binds", "set", "Yaw", "--device", $xpPedalsA, "--input", "JOY_RZ", "--text")
        Assert-True ($xpSetYaw.ExitCode -eq 0) "binds set for X-Plane yaw failed."
        $xpAddFlaps = Run-Engine @("--profile", $xpProfile, "relations", "add", "XP Flaps up", "--button", "--node", "XPlane12:sim/flight_controls/flaps_up", "--device", $xpStick, "--input", "JOY_BTN_POV1_UR", "--text")
        Assert-True ($xpAddFlaps.ExitCode -eq 0) "relations add for X-Plane flaps up failed."
        $xpAddServos = Run-Engine @("--profile", $xpProfile, "relations", "add", "XP Servos", "--button", "--node", "XPlane:sim/autopilot/servos_off_any", "--device", $xpStick, "--input", "JOY_BTN2", "--text")
        Assert-True ($xpAddServos.ExitCode -eq 0) "relations add for X-Plane servos failed."

        $xpMerge = Run-Engine @("--profile", $xpProfile, "xplane12", "export", $xpPrf, "--mode", "merge", "--dry-run", "--text")
        Assert-Match $xpMerge.Output "already has 'sim/view/default_view' in X-Plane; merge keeps it" "xplane merge must keep the user's command on an occupied button."
        Assert-True ([System.IO.File]::ReadAllText($xpPrf) -eq $xpOriginalText) "xplane dry run must not write."

        $xpExport = Run-Engine @("--profile", $xpProfile, "xplane12", "export", $xpPrf, "--text")
        Assert-True ($xpExport.ExitCode -eq 0) "xplane export after rebind failed."
        Assert-Match $xpExport.Output "replaced 'sim/view/default_view'" "xplane merge-overwrite should report the replaced user command."
        $xpAfter = [System.IO.File]::ReadAllText($xpPrf)
        Assert-Match $xpAfter "(?m)^_joy_BUTN_use0 sim/none/none\r?$" "xplane export should release the button JoyPro used before."
        Assert-Match $xpAfter "(?m)^_joy_BUTN_use2 sim/weapons/fire_guns\r?$" "xplane export should write the new button (slot 0, 0-based)."
        Assert-Match $xpAfter "(?m)^_joy_BUTN_use13 sim/flight_controls/flaps_up\r?$" "xplane export should map hat diagonals behind the device buttons."
        Assert-Match $xpAfter "(?m)^_joy_AXIS_use50 0\r?$" "xplane export should release the old yaw axis on the other pedals."
        Assert-Match $xpAfter "(?m)^_joy_AXIS_use75 3\r?$" "xplane export should write yaw to the slot of the matching pedals."
        Assert-Match $xpAfter "(?m)^_joy_AXIS_use29 41\r?$" "xplane export must keep non-flight axes."
        Assert-Match $xpAfter "(?m)^_joy_BUTN_use_desc161 Check Item\r?$" "xplane export must keep plugin commands and their descriptions."
        Assert-Match $xpAfter "(?m)^_joy_center1 0\.500000\r?$" "xplane export must keep calibration."
        Assert-True (Test-Path ($xpPrf + ".jpbak")) "xplane export should back up the preferences."

        $xpClear = Run-Engine @("--profile", $xpProfile, "binds", "clear", "Flaps down a notch", "--text")
        Assert-True ($xpClear.ExitCode -eq 0) "binds clear on the X-Plane flaps relation failed."
        $xpClean = Run-Engine @("--profile", $xpProfile, "xplane12", "export", $xpPrf, "--mode", "clean", "--text")
        Assert-True ($xpClean.ExitCode -eq 0) "xplane clean export failed."
        $xpCleaned = [System.IO.File]::ReadAllText($xpPrf)
        Assert-Match $xpCleaned "(?m)^_joy_BUTN_use162 sim/none/none\r?$" "xplane clean export should release flight commands JoyPro does not bind."
        Assert-Match $xpCleaned "(?m)^_joy_BUTN_use640 sim/autopilot/servos_off_any\r?$" "xplane clean export must keep slots of devices that are not connected."
        Assert-Match $xpCleaned "(?m)^_joy_AXIS_use100 1\r?$" "xplane clean export must keep axes of devices that are not connected."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game Falcon BMS: synthetic launcher device files (stick with shift layer, throttle, a device that is not
    # connected). Only cataloged callbacks and flight axes change; views, sim functions and FOV stay.
    $bmsConfig = Join-Path $tempDir "bms\\Config"
    $bmsProfile = Join-Path $tempDir "bms.pr0file"
    New-Item -Path $bmsConfig -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\bms\\Config\\*") -Destination $bmsConfig -Force
    $bmsStickFile = Join-Path $bmsConfig "Setup.v100.Smoke Stick {11111111-2222-3333-4444-555555555555}.xml"
    $bmsThrottleFile = Join-Path $bmsConfig "Setup.v100.Smoke Throttle {66666666-7777-8888-9999-000000000000}.xml"
    $bmsPedalsFile = Join-Path $bmsConfig "Setup.v100.Old Pedals {99999999-8888-7777-6666-555555555555}.xml"
    $bmsStickText = [System.IO.File]::ReadAllText($bmsStickFile)
    $bmsThrottleText = [System.IO.File]::ReadAllText($bmsThrottleFile)
    $bmsPedalsText = [System.IO.File]::ReadAllText($bmsPedalsFile)
    $bmsStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $bmsThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
    try {
        $bmsScan = Run-Engine @("bms", "scan", $bmsConfig, "--text")
        Assert-True ($bmsScan.ExitCode -eq 0) "bms scan failed."
        Assert-Match $bmsScan.Output "(?m)^Bindings Read:\s+15\s*$" "bms scan should read 15 flight bindings from the fixture."
        Assert-Match $bmsScan.Output "SimDMSUp <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN5 \+ Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN4" "bms scan should read the shift layer as a modifier on the SimHotasPinkyShift button."
        Assert-Match $bmsScan.Output "axis/Yaw <- Old Pedals \{99999999-8888-7777-6666-555555555555\} JOY_RZ" "bms scan should name devices that are not connected after their device file."
        Assert-Match $bmsScan.Output "AFElevatorTrimUp <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_U" "bms scan should read hat directions."
        Assert-NotMatch $bmsScan.Output "OTWView|SimMotionFreeze|axis/FOV|SimTriggerSecondDetent" "bms scan must ignore views, sim functions, the FOV axis and release slots."

        $bmsImport = Run-Engine @("--profile", $bmsProfile, "bms", "import", $bmsConfig, "--text")
        Assert-True ($bmsImport.ExitCode -eq 0) "bms import failed."
        Assert-Match $bmsImport.Output "(?m)^Relations Imported:\s+15\s*$" "bms import should create 15 relations (AFBrakesOut on stick and throttle: one relation each)."
        Assert-True (Test-Path (Join-Path $bmsConfig "JoyPro.joypro.json")) "bms import should record JoyPro's inputs next to the device files."
        $bmsRoundTrip = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--text")
        Assert-True ($bmsRoundTrip.ExitCode -eq 0) "bms export after import failed."
        Assert-True (([System.IO.File]::ReadAllText($bmsStickFile) -eq $bmsStickText) -and ([System.IO.File]::ReadAllText($bmsThrottleFile) -eq $bmsThrottleText)) "bms import/export round trip must leave the device files byte-identical."

        $bmsSetPickle = Run-Engine @("--profile", $bmsProfile, "binds", "set", "STICK: WEAPON RELEASE (Pickle)", "--input", "JOY_BTN3", "--text")
        Assert-True ($bmsSetPickle.ExitCode -eq 0) "binds set on the imported BMS pickle relation failed."
        $bmsSetRoll = Run-Engine @("--profile", $bmsProfile, "binds", "set", "Roll", "--device", $bmsThrottle, "--input", "JOY_X", "--text")
        Assert-True ($bmsSetRoll.ExitCode -eq 0) "binds set for BMS roll failed."
        $bmsAddDms = Run-Engine @("--profile", $bmsProfile, "relations", "add", "BMS DMS Down", "--button", "--node", "FalconBMS:SimDMSDown", "--device", $bmsStick, "--input", "JOY_BTN6", "--modifier", "BMS Shift", "--text")
        Assert-True ($bmsAddDms.ExitCode -eq 0) "relations add for the shifted BMS DMS down failed."
        $bmsAddCat = Run-Engine @("--profile", $bmsProfile, "relations", "add", "BMS CAT", "--button", "--node", "FalconBMS:SimCATSwitch", "--device", $bmsStick, "--input", "JOY_BTN6", "--text")
        Assert-True ($bmsAddCat.ExitCode -eq 0) "relations add for BMS CAT failed."
        $bmsInferProfile = Join-Path $tempDir "bms-infer.pr0file"
        $bmsInfer = Run-Engine @("--profile", $bmsInferProfile, "relations", "add", "BMS Brakes", "--node", "FalconBMS:AFBrakesOut", "--text")
        Assert-True ($bmsInfer.ExitCode -eq 0) "relations add without a type for a BMS callback failed."
        $bmsInferList = Run-Engine @("--profile", $bmsInferProfile, "relations", "list", "--text")
        Assert-Match $bmsInferList.Output "BMS Brakes \| type=button" "relations add must take the type of registry-game actions from the catalog (not from DCS-style prefixes)."

        $bmsMerge = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--mode", "merge", "--dry-run", "--text")
        Assert-Match $bmsMerge.Output "already has 'OTWViewLeft' in BMS; merge keeps it" "bms merge must keep the user's callback on an occupied button."
        Assert-Match $bmsMerge.Output "BMS already has this axis on Smoke Stick JOY_X; merge keeps it" "bms merge must not move an axis function the user assigned."
        Assert-True ([System.IO.File]::ReadAllText($bmsStickFile) -eq $bmsStickText) "bms dry run must not write."

        $bmsExport = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--text")
        Assert-True ($bmsExport.ExitCode -eq 0) "bms export after rebind failed."
        Assert-Match $bmsExport.Output "replaced 'OTWViewLeft'" "bms merge-overwrite should report the replaced user callback."
        $bmsAfterText = [System.IO.File]::ReadAllText($bmsStickFile)
        $bmsAfterBytes = [System.IO.File]::ReadAllBytes($bmsStickFile)
        Assert-True ($bmsAfterBytes[0] -eq 0x3C -and $bmsAfterText.StartsWith("<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<JoyAssgn ")) "bms export must keep the declaration and CRLF line ends without a BOM."
        Assert-NotMatch $bmsAfterText "[^\r]\n" "bms export must keep CRLF line ends."
        [xml]$bmsStickXml = $bmsAfterText
        [xml]$bmsThrottleXml = [System.IO.File]::ReadAllText($bmsThrottleFile)
        $bmsDx = $bmsStickXml.JoyAssgn.dx.DxAssgn
        Assert-True ($bmsDx[1].assign.Assgn[0].Callback -eq "SimDoNothing") "bms export should release the button JoyPro used before."
        Assert-True ($bmsDx[2].assign.Assgn[0].Callback -eq "SimPickle") "bms export should write the new button (press slot)."
        Assert-True ($bmsDx[5].assign.Assgn[0].Callback -eq "SimCATSwitch") "bms export should write the press slot."
        Assert-True ($bmsDx[5].assign.Assgn[1].Callback -eq "SimDMSDown") "bms export should write shifted binds to the press+shift slot."
        Assert-True ([string]::IsNullOrEmpty($bmsStickXml.JoyAssgn.axis.AxAssgn[0].AxisName) -and $bmsStickXml.JoyAssgn.axis.AxAssgn[0].AssgnDate -eq "1998-12-12T12:00:00") "bms export should release the old roll axis."
        Assert-True ($bmsThrottleXml.JoyAssgn.axis.AxAssgn[0].AxisName -eq "Roll" -and $bmsThrottleXml.JoyAssgn.axis.AxAssgn[0].AssgnDate -ne "1998-12-12T12:00:00") "bms export should give roll to the throttle's X axis with a new date."
        Assert-True ($bmsThrottleXml.JoyAssgn.axis.AxAssgn[7].AxisName -eq "FOV") "bms export must keep the FOV axis."
        Assert-True (Test-Path ($bmsStickFile + ".jpbak")) "bms export should back up the device file."
        Assert-True ([System.IO.File]::ReadAllText($bmsPedalsFile) -eq $bmsPedalsText) "bms export must not touch unchanged device files."

        $bmsClear = Run-Engine @("--profile", $bmsProfile, "binds", "clear", "STICK: TMS Up", "--text")
        Assert-True ($bmsClear.ExitCode -eq 0) "binds clear on the BMS TMS up relation failed."
        $bmsClean = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--mode", "clean", "--text")
        Assert-True ($bmsClean.ExitCode -eq 0) "bms clean export failed."
        [xml]$bmsCleanStick = [System.IO.File]::ReadAllText($bmsStickFile)
        [xml]$bmsCleanThrottle = [System.IO.File]::ReadAllText($bmsThrottleFile)
        $bmsCleanDx = $bmsCleanStick.JoyAssgn.dx.DxAssgn
        Assert-True ($bmsCleanDx[4].assign.Assgn[0].Callback -eq "SimDoNothing") "bms clean export should release callbacks JoyPro does not bind."
        Assert-True ($bmsCleanDx[6].assign.Assgn[2].Callback -eq "SimDoNothing") "bms clean export should release flight callbacks in release slots."
        Assert-True ($bmsCleanDx[3].assign.Assgn[0].Callback -eq "SimHotasPinkyShift") "bms clean export must keep the shift button while shifted binds use it."
        # BMS is flight only: clean takes over the devices JoyPro writes (views and sim callbacks included).
        Assert-True ($bmsCleanStick.JoyAssgn.pov.PovAssgn[0].direction.DirAssgn[0].Callback.string[1] -eq "SimDoNothing") "bms clean export should release view callbacks on a device JoyPro writes."
        Assert-True ($bmsCleanThrottle.JoyAssgn.dx.DxAssgn[1].assign.Assgn[0].Callback -eq "SimDoNothing") "bms clean export should release sim callbacks on a device JoyPro writes."
        Assert-True ([string]::IsNullOrEmpty($bmsCleanThrottle.JoyAssgn.axis.AxAssgn[7].AxisName)) "bms clean export should release the FOV axis on a device JoyPro writes."
        Assert-True ($bmsCleanStick.JoyAssgn.pov.PovAssgn[0].direction.DirAssgn[0].Callback.string[0] -eq "AFElevatorTrimUp") "bms clean export must keep the profile's hat binds."

        # Nuke: a device file without binds in the profile is emptied; without nuke it stays.
        $bmsSpareFile = Join-Path $bmsConfig "Setup.v100.Spare Stick {44444444-3333-2222-1111-000000000000}.xml"
        Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\bms\\Config\\Setup.v100.Smoke Stick {11111111-2222-3333-4444-555555555555}.xml") -Destination $bmsSpareFile -Force
        $bmsKeepSpare = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--text")
        Assert-True ($bmsKeepSpare.ExitCode -eq 0) "bms export with a spare device failed."
        [xml]$bmsSpareKept = [System.IO.File]::ReadAllText($bmsSpareFile)
        Assert-True ($bmsSpareKept.JoyAssgn.dx.DxAssgn[1].assign.Assgn[0].Callback -eq "SimPickle") "bms export without nuke must keep binds of devices the profile does not use."
        $bmsNuke = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsConfig, "--nuke-untouched-devices", "--text")
        Assert-True ($bmsNuke.ExitCode -eq 0) "bms export with nuke failed."
        [xml]$bmsSpareNuked = [System.IO.File]::ReadAllText($bmsSpareFile)
        Assert-True (@($bmsSpareNuked.JoyAssgn.dx.DxAssgn | ForEach-Object { $_.assign.Assgn } | Where-Object { $_.Callback -ne "SimDoNothing" }).Count -eq 0) "bms nuke should release every button of an unused device."
        Assert-True (@($bmsSpareNuked.JoyAssgn.axis.AxAssgn | Where-Object { -not [string]::IsNullOrEmpty($_.AxisName) }).Count -eq 0) "bms nuke should release every axis of an unused device."
        [xml]$bmsNukeStick = [System.IO.File]::ReadAllText($bmsStickFile)
        Assert-True ($bmsNukeStick.JoyAssgn.dx.DxAssgn[2].assign.Assgn[0].Callback -eq "SimPickle") "bms nuke must keep the binds of used devices."

        # A connected device of the profile without a device file gets one in the launcher's layout.
        $bmsFreshConfig = Join-Path $tempDir "bms-fresh\\User\\Config"
        New-Item -Path $bmsFreshConfig -ItemType Directory -Force | Out-Null
        Copy-Item -Path $bmsStickFile -Destination $bmsFreshConfig -Force
        $bmsFresh = Run-Engine @("--profile", $bmsProfile, "bms", "export", $bmsFreshConfig, "--text")
        Assert-True ($bmsFresh.ExitCode -eq 0) "bms export without the throttle's device file failed."
        Assert-Match $bmsFresh.Output "Created the BMS device file for Smoke Throttle" "bms export should report the created device file."
        $bmsFreshThrottle = Join-Path $bmsFreshConfig "Setup.v100.Smoke Throttle {66666666-7777-8888-9999-000000000000}.xml"
        Assert-True (Test-Path $bmsFreshThrottle) "bms export should create the device file of a connected device."
        [xml]$bmsFreshXml = [System.IO.File]::ReadAllText($bmsFreshThrottle)
        Assert-True (@($bmsFreshXml.JoyAssgn.axis.AxAssgn).Count -eq 8 -and @($bmsFreshXml.JoyAssgn.pov.PovAssgn).Count -eq 4 -and @($bmsFreshXml.JoyAssgn.dx.DxAssgn).Count -eq 128) "a created bms device file needs the launcher's layout (8 axes, 4 hats, 128 buttons)."
        Assert-True ($bmsFreshXml.JoyAssgn.axis.AxAssgn[0].AxisName -eq "Roll") "a created bms device file should hold the profile's binds."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game IL-2 Korea: synthetic general.actions + known.devices.json (Smoke Stick = device 1, Old Pedals =
    # device 3 and not connected, Smoke Throttle known to the game without a device number).
    $koDir = Join-Path $tempDir "il2korea\\input"
    New-Item -Path $koDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\il2korea\\input\\*") -Destination $koDir -Force
    $koActions = Join-Path $koDir "general.actions"
    $koKnown = Join-Path $koDir "known.devices.json"
    $koProfile = Join-Path $tempDir "il2korea.pr0file"
    $koStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $koThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\il2korea\\devices.tsv"
    try {
        $koScan = Run-Engine @("il2korea", "scan", $koActions, "--text")
        Assert-True ($koScan.ExitCode -eq 0) "il2korea scan failed."
        Assert-Match $koScan.Output "(?m)^Bindings Read:\s+10\s*$" "il2korea scan should read 7 joystick and 3 keyboard inputs (button pairs and the mouse are not JoyPro inputs)."
        Assert-Match $koScan.Output "gear <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN3" "il2korea scan should map dev1_b2 (0-based) to the device numbered 1 in known.devices.json."
        Assert-Match $koScan.Output "airbrakes_on <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_R" "il2korea scan should read hats (pov0_90 = first hat, right)."
        Assert-Match $koScan.Output "canopy_open_close <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN6 \+ Keyboard LeftShift" "il2korea scan should read a keyboard modifier."
        Assert-Match $koScan.Output "rpc_yaw <- Old Pedals \{99999999-8888-7777-6666-555555555555\} JOY_RZ" "il2korea scan should name a device that is not connected after known.devices.json."

        $koImport = Run-Engine @("--profile", $koProfile, "il2korea", "import", $koActions, "--text")
        Assert-True ($koImport.ExitCode -eq 0) "il2korea import failed."
        Assert-True (Test-Path ($koActions + ".joypro.json")) "il2korea import should record JoyPro's inputs."
        $koActionsText = [System.IO.File]::ReadAllText($koActions)
        $koKnownText = [System.IO.File]::ReadAllText($koKnown)
        $koRound = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
        Assert-True ($koRound.ExitCode -eq 0) "il2korea export after import failed."
        Assert-True (([System.IO.File]::ReadAllText($koActions) -eq $koActionsText) -and ([System.IO.File]::ReadAllText($koKnown) -eq $koKnownText)) "il2korea import/export round trip must leave both files byte-identical."

        # Axis response (general.pitch / shared.responses): imported as the relation's IL-2 Korea tuning, not as its
        # base (which DCS uses); the preview reproduces the table the game compiled for these settings.
        $koPitchFile = Join-Path $koDir "general.pitch"
        $koSharedFile = Join-Path $koDir "shared.responses"
        $koPitchText = [System.IO.File]::ReadAllText($koPitchFile)
        $koSharedText = [System.IO.File]::ReadAllText($koSharedFile)
        Assert-True (([System.IO.File]::ReadAllText($koPitchFile) -eq $koPitchText) -and ([System.IO.File]::ReadAllText($koSharedFile) -eq $koSharedText)) "il2korea round trip must leave the response files as they were."
        $koPitchShow = Run-Engine @("--profile", $koProfile, "binds", "tuning", "show", "Pitch (Elevator)", "--game", "IL2Korea", "--text")
        Assert-Match $koPitchShow.Output "game:IL2Korea: .*n\.deadZone0=3;.*n\.offset=39;n\.sensitivity=22" "il2korea import should store general.pitch as the relation's IL-2 Korea tuning."
        Assert-Match $koPitchShow.Output "base: inv=1;sl=0;dz=0;sx=1;sy=1;cv=0\s" "il2korea import must not put Korea's response into the base tuning (DCS)."
        $koPreview = Run-Engine @("--profile", $koProfile, "binds", "tuning", "preview", "Pitch (Elevator)", "--game", "IL2Korea", "--points", "11", "--text")
        Assert-Match $koPreview.Output "-0\.200 -> \+0\.477850" "il2korea preview must match the table the game compiled (exponent 22, centre dead zone 3, offset 39; the inverted axis mirrors the input)."
        $koRollSet = Run-Engine @("--profile", $koProfile, "binds", "tuning", "set", "Roll (Ailerons)", "--scope", "game:IL2Korea", "--param", "sensitivity=60", "--param", "deadZone1=4", "--text")
        Assert-True ($koRollSet.ExitCode -eq 0) "binds tuning set for the Korea roll relation failed."
        $koYawSet = Run-Engine @("--profile", $koProfile, "binds", "tuning", "set", "Yaw (Rudder)", "--scope", "base", "--curvature", "0.3", "--deadzone", "0.05", "--text")
        Assert-True ($koYawSet.ExitCode -eq 0) "binds tuning set for the yaw base failed."
        $koCurveExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
        Assert-True ($koCurveExport.ExitCode -eq 0) "il2korea export with axis tuning failed."
        $koShared = [System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json
        Assert-True ($koShared.curves.rpc_roll.sensitivity -eq 60 -and $koShared.curves.rpc_roll.deadZone1 -eq 4) "il2korea export should write the relation's Korea tuning to shared.responses."
        Assert-True ($koShared.curves.rpc_yaw.sensitivity -eq 93 -and $koShared.curves.rpc_yaw.deadZone0 -eq 5) "il2korea export should fit the base curve (DCS curvature 0.3, dead zone 0.05) to Korea's exponent when the relation has no Korea tuning."
        Assert-True ([System.IO.File]::ReadAllText($koPitchFile) -eq $koPitchText) "il2korea export must leave an unchanged pitch curve as it was."
        # A response the user changed in the game: merge keeps it, merge-overwrite sets the profile's again.
        [System.IO.File]::WriteAllText($koSharedFile, ([System.IO.File]::ReadAllText($koSharedFile) -replace '"sensitivity": 60', '"sensitivity": 61'), (New-Object System.Text.UTF8Encoding($false)))
        $koCurveMerge = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge", "--text")
        Assert-True ($koCurveMerge.ExitCode -eq 0) "il2korea merge export failed."
        Assert-Match $koCurveMerge.Output "rpc_roll has response settings made in the game; merge keeps them" "il2korea merge should report the kept in-game response."
        Assert-True (([System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json).curves.rpc_roll.sensitivity -eq 61) "il2korea merge must keep a response changed in the game."
        $koCurveOverwrite = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
        Assert-True (([System.IO.File]::ReadAllText($koSharedFile) | ConvertFrom-Json).curves.rpc_roll.sensitivity -eq 60) "il2korea merge-overwrite should set the profile's response again."

        # Game-wide force feedback and the axis noise filter (engine ffb --device *): Settings > Input devices, which
        # Korea keeps in data\NSData\UserData\UserSettings.json "input"; other keys and startup.cfg untouched.
        $koUserData = Join-Path (Split-Path -Parent $koDir) "NSData\UserData"
        New-Item -ItemType Directory -Force -Path $koUserData | Out-Null
        $koSettings = Join-Path $koUserData "UserSettings.json"
        $koSettingsText = "{`r`n  `"camera`": {`r`n    `"shake`": 1`r`n  },`r`n  `"input`": {`r`n    `"mouseSensitivity`": 4.0,`r`n    `"joyFilter`": 0.0,`r`n    `"ffEnabled`": true,`r`n    `"ffAmplitude`": 100.0,`r`n    `"ffForce`": 100.0,`r`n    `"exclusive`": true`r`n  }`r`n}"
        [System.IO.File]::WriteAllText($koSettings, $koSettingsText, (New-Object System.Text.UTF8Encoding($false)))
        $koStartup = Join-Path (Split-Path -Parent $koDir) "startup.cfg"
        [System.IO.File]::WriteAllText($koStartup, "[KEY = force_feedback]`r`n`tforce = 1.0`r`n[END]`r`n", (New-Object System.Text.UTF8Encoding($false)))
        $koFfb = Run-Engine @("--profile", $koProfile, "ffb", "set", "--device", "*", "--game", "IL2Korea", "--param", "ffEnabled=false", "--param", "ffForce=50", "--param", "ffForceFactorMax=178", "--param", "joyFilter=38", "--text")
        Assert-True ($koFfb.ExitCode -eq 0) ("ffb set for IL-2 Korea failed: " + $koFfb.Output)
        $koFfbExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
        Assert-True ($koFfbExport.ExitCode -eq 0) "il2korea export with force feedback settings failed."
        $koSettingsAfter = [System.IO.File]::ReadAllText($koSettings)
        $koSettingsWant = "{`r`n  `"camera`": {`r`n    `"shake`": 1`r`n  },`r`n  `"input`": {`r`n    `"mouseSensitivity`": 4.0,`r`n    `"joyFilter`": 38.0,`r`n    `"ffEnabled`": false,`r`n    `"ffAmplitude`": 100.0,`r`n    `"ffForce`": 50.0,`r`n    `"exclusive`": true,`r`n    `"ffForceFactorMax`": 178.0`r`n  }`r`n}"
        Assert-True ($koSettingsAfter -eq $koSettingsWant) ("il2korea export should set the Input devices keys in UserSettings.json in place: " + $koSettingsAfter)
        Assert-True ([System.IO.File]::ReadAllText($koStartup) -eq "[KEY = force_feedback]`r`n`tforce = 1.0`r`n[END]`r`n") "il2korea export must not write startup.cfg (the game does not read force feedback from it)."

        # Relative Control ("*" before the token) and the one-sided kind of throttle-like axes come in as the relation's
        # IL-2 Korea tuning and go back out unchanged; the built-in curve and control surface loading drop-downs of the
        # primary controls are tuning values written to the response file.
        $koRelDir = Join-Path $tempDir "il2korea-rel\\input"
        New-Item -Path $koRelDir -ItemType Directory -Force | Out-Null
        Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\il2korea\\input\\*") -Destination $koRelDir -Force
        $koRelActions = Join-Path $koRelDir "general.actions"
        $koRelText = [System.IO.File]::ReadAllText($koRelActions).Replace("`"actions`": {`n", "`"actions`": {`n    `"rpc_all_engines_throttles`": [`n      `"*-dev1_axis_t`"`n    ],`n")
        # Relative Control's dead zone and sensitivity (the diamond dialog) live in general.actions "additive", shown value / 100.
        $koRelText = $koRelText.TrimEnd().TrimEnd('}').TrimEnd() + ",`n  `"additive`": {`n    `"rpc_all_engines_throttles`": {`n      `"deadzone`": 0.17,`n      `"sensitivity`": 0.11`n    }`n  }`n}`n"
        [System.IO.File]::WriteAllText($koRelActions, $koRelText, (New-Object System.Text.UTF8Encoding($false)))
        $koRelProfile = Join-Path $tempDir "il2korea-rel.pr0file"
        $koRelImport = Run-Engine @("--profile", $koRelProfile, "il2korea", "import", $koRelActions, "--text")
        Assert-True ($koRelImport.ExitCode -eq 0) ("il2korea import with a relative axis failed: " + $koRelImport.Output)
        $koRelShow = Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "show", "Throttle control", "--game", "IL2Korea", "--text")
        Assert-Match $koRelShow.Output "game:IL2Korea: inv=1;sl=1;.*n\.relative=1" ("il2korea import should keep Relative Control, the inversion and the one-sided kind of the throttle: " + $koRelShow.Output)
        Assert-Match $koRelShow.Output "n\.relativeDeadZone=17\b" ("il2korea import should read Relative Control's dead zone (0.17 -> 17 %): " + $koRelShow.Output)
        Assert-Match $koRelShow.Output "n\.relativeSensitivity=11\b" ("il2korea import should read Relative Control's sensitivity (0.11 -> 11): " + $koRelShow.Output)
        $koRelBefore = [System.IO.File]::ReadAllText($koRelActions)
        $koRelExport = Run-Engine @("--profile", $koRelProfile, "il2korea", "export", $koRelActions, "--text")
        Assert-True ($koRelExport.ExitCode -eq 0) "il2korea export with a relative axis failed."
        Assert-True ([System.IO.File]::ReadAllText($koRelActions) -eq $koRelBefore) "il2korea export must write a relative inverted axis back as *-dev1_axis_t."
        Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "set", "Throttle control", "--scope", "game:IL2Korea", "--slider", "true", "--inverted", "false", "--param", "deadZone0=10", "--param", "relative=1", "--param", "relativeDeadZone=80", "--param", "relativeSensitivity=20", "--text") | Out-Null
        $koRelPreview = Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "preview", "Throttle control", "--game", "IL2Korea", "--points", "11", "--text")
        Assert-Match $koRelPreview.Output "-0\.800 -> -1\.000000" "a one-sided Korea axis keeps -1 for the low dead zone (10 % of the whole travel)."
        Assert-Match $koRelPreview.Output "\+0\.000 -> -0\.11111" "a one-sided Korea axis runs the curve over the whole travel."
        Run-Engine @("--profile", $koRelProfile, "binds", "tuning", "set", "Pitch (Elevator)", "--scope", "game:IL2Korea", "--param", "harmonization=2", "--param", "normalized=0", "--text") | Out-Null
        Run-Engine @("--profile", $koRelProfile, "il2korea", "export", $koRelActions, "--mode", "merge-overwrite", "--text") | Out-Null
        $koRelPitch = ([System.IO.File]::ReadAllText((Join-Path $koRelDir "general.pitch")) | ConvertFrom-Json).curves.rpc_pitch
        Assert-True ($koRelPitch.harmonization -eq 2 -and $koRelPitch.normalized -eq 0) ("il2korea export should write the built-in curve and control surface loading: " + ($koRelPitch | ConvertTo-Json -Compress))
        $koRelAdditive = ([System.IO.File]::ReadAllText($koRelActions) | ConvertFrom-Json).additive.rpc_all_engines_throttles
        Assert-True ($koRelAdditive.deadzone -eq 0.8 -and $koRelAdditive.sensitivity -eq 0.2) ("il2korea export should write Relative Control's dead zone and sensitivity / 100: " + ($koRelAdditive | ConvertTo-Json -Compress))

        # A user bind made in the game (not JoyPro's): merge keeps it, merge-overwrite replaces it on the same device.
        $koUser = $koActionsText.Replace("`"key_f`"`n", "`"key_f`",`n      `"dev1_b9`"`n")
        [System.IO.File]::WriteAllText($koActions, $koUser, (New-Object System.Text.UTF8Encoding($false)))
        $koAddFlaps = Run-Engine @("--profile", $koProfile, "relations", "add", "Korea Flaps", "--button", "--node", "IL2Korea:flaps", "--device", $koStick, "--input", "JOY_BTN8", "--text")
        Assert-True ($koAddFlaps.ExitCode -eq 0) "relations add for Korea flaps failed."
        $koMerge = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "merge", "--dry-run", "--text")
        Assert-Match $koMerge.Output "flaps already has 'dev1_b9' on that device in the game; merge keeps it" "il2korea merge must keep the user's input on the same device."
        Assert-True ([System.IO.File]::ReadAllText($koActions) -eq $koUser) "il2korea dry run must not write."

        # The throttle has no device number yet: JoyPro gives it the lowest free one (2) in known.devices.json.
        $koAddGear = Run-Engine @("--profile", $koProfile, "relations", "add", "Korea Gear Throttle", "--button", "--node", "IL2Korea:gear_up", "--device", $koThrottle, "--input", "JOY_BTN4", "--text")
        Assert-True ($koAddGear.ExitCode -eq 0) "relations add for Korea gear up on the throttle failed."
        $koExport = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--text")
        Assert-True ($koExport.ExitCode -eq 0) "il2korea merge-overwrite export failed."
        Assert-Match $koExport.Output "device number 2" "il2korea export should report the device number it gave the throttle."
        $koAfter = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
        $koKnownAfter = [System.IO.File]::ReadAllText($koKnown) | ConvertFrom-Json
        Assert-True ($koKnownAfter.knownDevices."66666666-7777-8888-0000000000009999".deviceId -eq 2) "il2korea export should number the throttle in known.devices.json."
        Assert-True (@($koAfter.devices | Where-Object { $_.deviceId -eq 2 -and $_.ident -eq "abcd_0001" }).Count -eq 1) "il2korea export should list the throttle under devices."
        Assert-True ((@($koAfter.actions.gear_up) -contains "dev2_b3")) "il2korea export should write the throttle's button."
        Assert-True ((@($koAfter.actions.flaps) -contains "dev1_b7") -and -not (@($koAfter.actions.flaps) -contains "dev1_b9")) "il2korea merge-overwrite should replace the user's input on the same device."
        Assert-True ((@($koAfter.actions.rpc_pitch) -contains "-dev1_axis_y") -and (@($koAfter.actions.rpc_pitch) -contains "key_down/key_up")) "il2korea export must keep inverted axes and button pairs."
        Assert-True (Test-Path ($koActions + ".jpbak")) "il2korea export should back up the actions file."

        # Clean takes over the devices it writes and keeps the keyboard; nuke also clears devices the profile does not use.
        $koClearGuns = Run-Engine @("--profile", $koProfile, "binds", "clear", "Fire all guns", "--text")
        Assert-True ($koClearGuns.ExitCode -eq 0) ("binds clear on the imported Korea guns relation failed: " + $koClearGuns.Output)
        $koClean = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "clean", "--text")
        Assert-True ($koClean.ExitCode -eq 0) "il2korea clean export failed."
        $koCleanJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
        Assert-True ((@($koCleanJson.actions.all_guns_fire) -join ",") -eq "key_space,mouse_b0") "il2korea clean should release unbound inputs of written devices and keep keyboard and mouse."
        Assert-True ((@($koCleanJson.actions.rpc_yaw) -contains "dev3_axis_t")) "il2korea clean without nuke must keep devices JoyPro does not write."
        $koNuke = Run-Engine @("--profile", $koProfile, "binds", "clear", "Yaw (Rudder)", "--text")
        $koNuke = Run-Engine @("--profile", $koProfile, "il2korea", "export", $koActions, "--mode", "clean", "--nuke-untouched-devices", "--text")
        Assert-True ($koNuke.ExitCode -eq 0) "il2korea nuke export failed."
        $koNukeJson = [System.IO.File]::ReadAllText($koActions) | ConvertFrom-Json
        Assert-True (-not (@($koNukeJson.actions.rpc_yaw) -contains "dev3_axis_t") -and (@($koNukeJson.actions.rpc_yaw) -contains "key_x/key_z")) "il2korea nuke should clear an unused device and keep the keyboard."
        Assert-True ((@($koNukeJson.actions.gear) -contains "key_g")) "il2korea nuke must keep keyboard inputs."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game X4: synthetic inputmap.xml + config.xml (Smoke Stick pinned to slot 2, Smoke Throttle to slot 3,
    # slot 1 unpinned). Only space-flight ids on pinned slots change; on-foot, menu, keyboard and slot-1 lines stay.
    $x4Dir = Join-Path $tempDir "x4\\1234"
    $x4Map = Join-Path $x4Dir "inputmap.xml"
    $x4Profile = Join-Path $tempDir "x4.pr0file"
    New-Item -Path $x4Dir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\x4\\1234\\*") -Destination $x4Dir -Force
    $x4Stick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
    try {
        $x4Scan = Run-Engine @("x4", "scan", "--path", $x4Map, "--text")
        Assert-True ($x4Scan.ExitCode -eq 0) "x4 scan failed."
        Assert-Match $x4Scan.Output "(?m)^Bindings Read:\s+5\s*$" "x4 scan should read 5 flight bindings from the fixture."
        Assert-Match $x4Scan.Output "INPUT_STATE_FIRE_PRIMARY_WEAPON <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN6" "x4 scan should map slot 2 to the pinned Smoke Stick."
        Assert-Match $x4Scan.Output "INPUT_ACTION_TOGGLE_TRAVEL_MODE <- Keyboard D1 \+ Keyboard LeftShift" "x4 scan should read keyboard binds with Shift."
        Assert-Match $x4Scan.Output "slot 1 is not pinned" "x4 scan should warn about the unpinned slot 1."

        Run-Engine @("--profile", $x4Profile, "relations", "add", "X4 Pitch", "--axis", "--node", "X4:INPUT_RANGE_STEERING_PITCH", "--device", $x4Stick, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $x4Profile, "relations", "add", "X4 Fire", "--button", "--node", "X4:INPUT_STATE_FIRE_PRIMARY_WEAPON", "--device", $x4Stick, "--input", "JOY_BTN1", "--text") | Out-Null
        Run-Engine @("--profile", $x4Profile, "relations", "add", "X4 Spare", "--button", "--node", "X4:INPUT_STATE_BOOST", "--device", "Spare Stick {44444444-3333-2222-1111-000000000000}", "--input", "JOY_BTN2", "--text") | Out-Null
        $x4Clean = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--mode", "clean", "--text")
        Assert-True ($x4Clean.ExitCode -eq 0) "x4 clean export failed. Output: $($x4Clean.Output)"
        Assert-Match $x4Clean.Output "no fixed X4 slot yet" "x4 export should skip devices without an X4 slot."
        $x4Text = [System.IO.File]::ReadAllText($x4Map)
        Assert-Match $x4Text '<range id="INPUT_RANGE_STEERING_PITCH" source="INPUT_SOURCE_JOYAXES_2" code="INPUT_JOYAXIS_Y"/>' "x4 export should write the pitch axis to slot 2."
        Assert-Match $x4Text '<state id="INPUT_STATE_FIRE_PRIMARY_WEAPON" source="INPUT_SOURCE_JOYBUTTONS_2" code="INPUT_XBUTTON_A"/>' "x4 export should write button 1 as INPUT_XBUTTON_A on slot 2."
        Assert-NotMatch $x4Text 'INPUT_XBUTTON_RIGHT_SHOULDER' "x4 clean export should release the old flight bind on slot 2."
        Assert-Match $x4Text 'INPUT_ACTION_WIDGET_BACK" source="INPUT_SOURCE_JOYBUTTONS_2"' "x4 clean export must keep menu binds on slot 2."
        Assert-Match $x4Text 'INPUT_STATE_FP_INTERACTION_MENU" source="INPUT_SOURCE_JOYBUTTONS_2"' "x4 clean export must keep on-foot binds."
        Assert-Match $x4Text 'INPUT_RANGE_THROTTLE" source="INPUT_SOURCE_JOYAXES_3"' "x4 clean export must keep slots the export does not write."
        Assert-Match $x4Text 'INPUT_STATE_BOOST" source="INPUT_SOURCE_KEYBOARD"' "x4 export must keep keyboard binds without --include-keyboard."
        Assert-Match $x4Text 'INPUT_STATE_FIRE_PRIMARY_WEAPON" source="INPUT_SOURCE_JOYAXES" code="INPUT_JOYAXIS_RZ" sgn="-1"' "x4 export must keep binds of the unpinned slot 1."
        Assert-NotMatch $x4Text "`r" "x4 export must keep LF line ends."
        Assert-True ((Get-Content -Path $x4Map -Encoding Byte -TotalCount 1)[0] -eq 0x3C) "x4 export must not add a BOM."
        Assert-True (Test-Path ($x4Map + ".jpbak")) "x4 export should back up inputmap.xml."
        $null = [xml]$x4Text  # the export must leave valid XML

        $x4Again = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--mode", "clean", "--text")
        Assert-Match $x4Again.Output "already matches" "a second x4 export should change nothing."
        $x4Nuke = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--mode", "clean", "--nuke-untouched-devices", "--text")
        Assert-True ($x4Nuke.ExitCode -eq 0) "x4 nuke export failed."
        $x4NukeText = [System.IO.File]::ReadAllText($x4Map)
        Assert-NotMatch $x4NukeText 'source="INPUT_SOURCE_JOYAXES_3"|source="INPUT_SOURCE_JOYBUTTONS_3"' "x4 nuke should release the flight binds of the pinned but unused slot 3."
        Assert-Match $x4NukeText 'INPUT_RANGE_STEERING_PITCH" source="INPUT_SOURCE_JOYAXES_2"' "x4 nuke must keep the used slot."

        # Restoring the backup of the first export brings back the original file byte for byte; the restore can be undone.
        $x4Backups = Run-Engine @("backups", "list", "--game", "x4", "--text")
        $x4Sets = @([regex]::Matches($x4Backups.Output, "(?m)^  - (\S+_X4) \| X4") | ForEach-Object { $_.Groups[1].Value })
        Assert-True ($x4Sets.Count -ge 2) "x4 exports did not create one backup set each. Output: $($x4Backups.Output)"
        $x4Restore = Run-Engine @("backups", "restore", $x4Sets[$x4Sets.Count - 1], "--text")
        Assert-True ($x4Restore.ExitCode -eq 0) "engine backups restore failed. Output: $($x4Restore.Output)"
        Assert-Match $x4Restore.Output "(?m)^Undo: engine backups restore \S+_X4" "backups restore did not back up the current file first."
        $x4Original = [System.IO.File]::ReadAllBytes((Join-Path $engineDir "tests\\fixtures\\x4\\1234\\inputmap.xml"))
        $x4Restored = [System.IO.File]::ReadAllBytes($x4Map)
        Assert-True ([Convert]::ToBase64String($x4Original) -eq [Convert]::ToBase64String($x4Restored)) "backups restore did not bring back the original inputmap.xml."
        $x4Unknown = Run-Engine @("backups", "restore", "does-not-exist", "--text")
        Assert-True ($x4Unknown.ExitCode -ne 0) "backups restore accepted an unknown backup id."

        # A game that is not installed is refused (export, backup restore) until the user sets its folder.
        $previousAssume = $env:JOYPRO_TEST_ASSUME_INSTALLED
        $previousDetection = $env:JOYPRO_INSTALL_DETECTION
        $env:JOYPRO_TEST_ASSUME_INSTALLED = $null
        $env:JOYPRO_INSTALL_DETECTION = "off"
        try {
            $giList = Run-Engine @("games", "installs", "--game", "x4", "--text")
            Assert-True ($giList.ExitCode -eq 0) "games installs failed. Output: $($giList.Output)"
            Assert-Match $giList.Output "(?m)^  X4 \(X4: Foundations\): not installed" "games installs did not report X4 as not installed."
            $giExport = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--text")
            Assert-True ($giExport.ExitCode -ne 0) "x4 export ran although X4 is not installed."
            Assert-Match $giExport.Output "X4: Foundations is not installed" "the refused export did not say why."
            $giRestore = Run-Engine @("backups", "restore", $x4Sets[0], "--dry-run", "--text")
            Assert-True ($giRestore.ExitCode -ne 0) "backups restore ran although X4 is not installed."
            $giFolder = Join-Path $tempDir "installs\X4 Foundations"
            New-Item -ItemType Directory -Force -Path $giFolder | Out-Null
            $giSet = Run-Engine @("games", "install", "set", "x4", $giFolder, "--text")
            Assert-True ($giSet.ExitCode -eq 0) "games install set failed. Output: $($giSet.Output)"
            Assert-Match $giSet.Output "(?m)^  X4 \(X4: Foundations\): installed - .*X4 Foundations \[override\]" "games install set did not make the folder the installation."
            $giAllowed = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--dry-run", "--text")
            Assert-True ($giAllowed.ExitCode -eq 0) "x4 export was refused although its folder is set. Output: $($giAllowed.Output)"
            $giClear = Run-Engine @("games", "install", "clear", "x4", "--text")
            Assert-Match $giClear.Output "(?m)^  X4 \(X4: Foundations\): not installed" "games install clear did not remove the folder."
            $giMissing = Run-Engine @("games", "install", "set", "x4", (Join-Path $tempDir "installs\no-such-folder"), "--text")
            Assert-True ($giMissing.ExitCode -ne 0) "games install set accepted a folder that does not exist."
            $giUnknown = Run-Engine @("games", "install", "set", "no-such-game", $giFolder, "--text")
            Assert-True ($giUnknown.ExitCode -ne 0) "games install set accepted an unknown game."

            # "I do not have this game" (first-start setup): the game counts as not installed, its own folder is kept,
            # and enable brings it back.
            $null = Run-Engine @("games", "install", "set", "x4", $giFolder, "--text")
            $giDisable = Run-Engine @("games", "install", "disable", "x4", "arma2", "--text")
            Assert-True ($giDisable.ExitCode -eq 0) "games install disable failed. Output: $($giDisable.Output)"
            Assert-Match $giDisable.Output "(?m)^  X4 \(X4: Foundations\): you do not have it - .*X4 Foundations \[override\]" "games install disable did not mark X4 (or lost its folder)."
            Assert-Match $giDisable.Output "(?m)^  Arma2 \(Arma 2\): you do not have it" "games install disable did not take a second game."
            Assert-True (Test-Path (Join-Path $env:JOYPRO_DATA_DIR "games-disabled.json")) "games install disable did not write games-disabled.json to the data folder."
            $giDisabledExport = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--dry-run", "--text")
            Assert-True ($giDisabledExport.ExitCode -ne 0) "x4 export ran although the user does not have X4."
            Assert-Match $giDisabledExport.Output "You said you do not have X4: Foundations" "the refused export did not say the user does not have the game."
            $giDisabledJson = (Run-Engine @("games", "installs", "--game", "x4")).Output | ConvertFrom-Json
            Assert-True ($giDisabledJson.Games[0].Disabled -eq $true -and $giDisabledJson.Games[0].Installed -eq $false -and $giDisabledJson.Games[0].Overridden -eq $true) "games installs did not report X4 as disabled with its folder kept."
            Assert-True (@($giDisabledJson.Games[0].Markers) -contains "X4.exe") "games installs did not list the marker files."
            $giEnable = Run-Engine @("games", "install", "enable", "x4,arma2", "--text")
            Assert-Match $giEnable.Output "(?m)^  X4 \(X4: Foundations\): installed - .*X4 Foundations \[override\]" "games install enable did not bring X4 back."
            $giEnabledExport = Run-Engine @("--profile", $x4Profile, "x4", "export", "--path", $x4Map, "--dry-run", "--text")
            Assert-True ($giEnabledExport.ExitCode -eq 0) "x4 export was refused after games install enable. Output: $($giEnabledExport.Output)"
            $giUnset = Run-Engine @("games", "install", "unset", "x4", "--text")
            Assert-Match $giUnset.Output "(?m)^  X4 \(X4: Foundations\): not installed" "games install unset did not remove the folder."
            $giDisableUnknown = Run-Engine @("games", "install", "disable", "no-such-game", "--text")
            Assert-True ($giDisableUnknown.ExitCode -ne 0) "games install disable accepted an unknown game."

            # The user's config location comes first wherever the engine looks for the game's controls.
            $gcFolder = Join-Path $tempDir "installs\Saved Games\DCS.openbeta"
            New-Item -ItemType Directory -Force -Path $gcFolder | Out-Null
            $gcSet = Run-Engine @("games", "config", "set", "dcs", $gcFolder, "--text")
            Assert-True ($gcSet.ExitCode -eq 0) "games config set failed. Output: $($gcSet.Output)"
            Assert-Match $gcSet.Output "(?m)^    Config location \(yours\): .*DCS\.openbeta" "games config set did not report the config location."
            $gcPaths = Run-Engine @("games", "paths", "--game", "DCS", "--text")
            Assert-Match $gcPaths.Output "(?m)^  Preferred Config: .*DCS\.openbeta" "DCS discovery does not prefer the user's Saved Games folder."
            $gcX4 = Run-Engine @("games", "config", "set", "x4", $x4Map, "--text")
            Assert-True ($gcX4.ExitCode -eq 0) "games config set (file) failed. Output: $($gcX4.Output)"
            $gcX4Paths = Run-Engine @("x4", "paths", "--text")
            Assert-Match $gcX4Paths.Output ("(?m)^Paths: \d+\r?\n  - " + [regex]::Escape([System.IO.Path]::GetFullPath($x4Map))) "x4 paths does not list the user's config file first."
            $gcIl2 = Run-Engine @("games", "config", "set", "il2", $gcFolder, "--text")
            Assert-True ($gcIl2.ExitCode -ne 0) "games config set accepted IL-2, which keeps its controls in the installation."
            Assert-Match $gcIl2.Output "keeps its controls in its installation folder" "games config set il2 did not say why."
            $gcMissing = Run-Engine @("games", "config", "set", "dcs", (Join-Path $tempDir "installs\no-such-folder"), "--text")
            Assert-True ($gcMissing.ExitCode -ne 0) "games config set accepted a path that does not exist."
            $gcClear = Run-Engine @("games", "config", "clear", "dcs", "--text")
            Assert-NotMatch $gcClear.Output "Config location \(yours\)" "games config clear did not forget the config location."
            $null = Run-Engine @("games", "config", "unset", "x4", "--text")
            Assert-NotMatch (Get-Content -Raw (Join-Path $env:JOYPRO_DATA_DIR "game-configs.json")) "DCS|X4" "games config clear/unset left entries in game-configs.json."
        }
        finally {
            $env:JOYPRO_TEST_ASSUME_INSTALLED = $previousAssume
            $env:JOYPRO_INSTALL_DETECTION = $previousDetection
        }
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry game Elite Dangerous: synthetic Custom.4.2.binds (Smoke Stick = 12345678, Smoke Throttle = ABCD0001,
    # a missing device 25BB00D8). Only ship flight slots of joysticks change; keyboard, half-axis and on-foot stay.
    $edDir = Join-Path $tempDir "elite"
    $edBinds = Join-Path $edDir "Custom.4.2.binds"
    $edProfile = Join-Path $tempDir "elite.pr0file"
    New-Item -Path $edDir -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\elite\\*") -Destination $edDir -Force
    $edStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\bms\\devices.tsv"
    try {
        $edScan = Run-Engine @("elite", "scan", "--path", $edBinds, "--text")
        Assert-True ($edScan.ExitCode -eq 0) "elite scan failed."
        Assert-Match $edScan.Output "(?m)^Bindings Read:\s+7\s*$" "elite scan should read 7 flight bindings from the fixture."
        Assert-Match $edScan.Output "UseBoostJuice <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_BTN3" "elite scan should resolve VID+PID device ids."
        Assert-Match $edScan.Output "device 25BB00D8, which is not connected" "elite scan should warn about a missing device."

        Run-Engine @("--profile", $edProfile, "relations", "add", "Elite Pitch", "--axis", "--node", "EliteDangerous:PitchAxisRaw", "--device", $edStick, "--input", "JOY_Y", "--text") | Out-Null
        Run-Engine @("--profile", $edProfile, "relations", "add", "Elite Fire", "--button", "--node", "EliteDangerous:PrimaryFire", "--device", $edStick, "--input", "JOY_BTN7", "--text") | Out-Null
        $edClean = Run-Engine @("--profile", $edProfile, "elite", "export", "--path", $edBinds, "--mode", "clean", "--text")
        Assert-True ($edClean.ExitCode -eq 0) "elite clean export failed. Output: $($edClean.Output)"
        $edText = [System.IO.File]::ReadAllText($edBinds)
        Assert-Match $edText "<PitchAxisRaw>\r\n\t\t<Binding Device=`"12345678`" Key=`"Joy_YAxis`" />\r\n\t\t<Inverted Value=`"1`" />\r\n\t\t<Deadzone Value=`"0.00000000`" />" "elite export should write the pitch axis over the missing device (a relation without invert is Elite's stock pitch, Inverted 1)."
        Assert-Match $edText "<PrimaryFire>\r\n\t\t<Primary Device=`"Keyboard`" Key=`"Key_Space`" />\r\n\t\t<Secondary Device=`"12345678`" Key=`"Joy_7`" />" "elite export should replace the stick's fire button and keep the keyboard slot."
        Assert-Match $edText "<SecondaryFire>\r\n\t\t<Primary Device=`"\{NoDevice\}`" Key=`"`" />\r\n\t\t<Secondary Device=`"\{NoDevice\}`" Key=`"`" />" "elite clean export should release flight binds of a written device that the profile does not use."
        Assert-Match $edText "Key=`"Joy_POV1Right`">\r\n\t\t\t<Modifier Device=`"12345678`" Key=`"Pos_Joy_RZAxis`" />" "elite clean export must keep half-axis modifier binds JoyPro cannot represent."
        Assert-Match $edText "<HumanoidJumpButton>\r\n\t\t<Primary Device=`"12345678`" Key=`"Joy_5`" />" "elite export must never touch on-foot elements."
        Assert-Match $edText "<Secondary Device=`"ABCD0001`" Key=`"Joy_3`" />" "elite clean export must keep devices the export does not write."
        Assert-NotMatch $edText "[^\r]\n" "elite export must keep CRLF line ends."
        Assert-True ((Get-Content -Path $edBinds -Encoding Byte -TotalCount 1)[0] -eq 0x3C) "elite export must not add a BOM."
        Assert-True (Test-Path ($edBinds + ".jpbak")) "elite export should back up the binds file."

        $edAgain = Run-Engine @("--profile", $edProfile, "elite", "export", "--path", $edBinds, "--mode", "clean", "--text")
        Assert-Match $edAgain.Output "already matches" "a second elite export should change nothing."
        $edNuke = Run-Engine @("--profile", $edProfile, "elite", "export", "--path", $edBinds, "--mode", "clean", "--nuke-untouched-devices", "--text")
        Assert-True ($edNuke.ExitCode -eq 0) "elite nuke export failed."
        $edNukeText = [System.IO.File]::ReadAllText($edBinds)
        Assert-NotMatch $edNukeText "ABCD0001" "elite nuke should release flight binds of joysticks the export does not use."
        Assert-Match $edNukeText "<Primary Device=`"Keyboard`" Key=`"Key_Tab`" />" "elite nuke must keep keyboard slots."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
    }

    # Registry games MSFS 2020 / 2024: synthetic 2020 Steam folder (several profiles per device, engine msfs2020) and a 2024 export (engine msfs2024).
    $msRemote = Join-Path $tempDir "msfs\\remote"
    $ms2024 = Join-Path $tempDir "msfs\\export2024.xml"
    $msProfile = Join-Path $tempDir "msfs.pr0file"
    $ms2024Profile = Join-Path $tempDir "msfs2024.pr0file"
    New-Item -Path $msRemote -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\msfs\\remote\\*") -Destination $msRemote -Force
    Copy-Item -Path (Join-Path $engineDir "tests\\fixtures\\msfs\\export2024.xml") -Destination $ms2024 -Force
    $msSting = Join-Path $msRemote "inputprofile_0000000001"
    $msStingText = [System.IO.File]::ReadAllText($msSting)
    $msDefaultText = [System.IO.File]::ReadAllText((Join-Path $msRemote "inputprofile_0000000002"))
    $ms2024Text = [System.IO.File]::ReadAllText($ms2024)
    $msStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $previousDataDir = $env:JOYPRO_DATA_DIR
    $env:JOYPRO_DATA_DIR = Join-Path $tempDir "joypro-data"
    try {
        $msAmbiguous = Run-Engine @("msfs2020", "scan", $msRemote, "--include-disconnected", "--text")
        # Scan and import take the most recently saved profile of a device with several (and say which); an export
        # still needs one picked (--game-profile below).
        Assert-Match $msAmbiguous.Output "Device 'Smoke Stick' has 2 MSFS profiles; imported the most recently saved one, '(Sting|Default)' \(others: '(Sting|Default)'\)\. Pick another with --game-profile" "msfs scan should take the newest of several profiles of a device and name the others."

        $msScan = Run-Engine @("msfs2020", "scan", $msRemote, "--game-profile", "Sting", "--include-disconnected", "--text")
        Assert-True ($msScan.ExitCode -eq 0) "msfs scan failed."
        Assert-Match $msScan.Output "(?m)^Bindings Read:\s+7\s*$" "msfs scan should read the 6 flight bindings and the camera bind of the 'Sting' profiles."
        Assert-Match $msScan.Output "KEY_FLAPS_INCR <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN_POV1_U \+ Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN1" "msfs scan should decode hat codes and button combinations."
        Assert-Match $msScan.Output "KEY_THROTTLE_AXIS_SET_EX1 <- Smoke Throttle \{66666666-7777-8888-9999-000000000000\} JOY_SLIDER1" "msfs scan should decode slider codes without a connected device."
        Assert-Match $msScan.Output "KEY_CYCLE_PILOTVIEW_NEXT <- Smoke Stick" "msfs scan should read the camera bind (MODES context: the sim's General binds)."

        $msImport = Run-Engine @("--profile", $msProfile, "msfs2020", "import", $msRemote, "--game-profile", "Sting", "--include-disconnected", "--text")
        Assert-True ($msImport.ExitCode -eq 0) "msfs import failed."
        Assert-Match $msImport.Output "(?m)^Relations Imported:\s+7\s*$" "msfs import should create 7 relations (gear on buttons 3 and 12: one relation each; the camera bind)."
        Assert-True (Test-Path (Join-Path $env:JOYPRO_DATA_DIR "Ownership\\MSFS")) "msfs import should keep ownership data outside the Steam folder."
        Assert-True (@(Get-ChildItem -Path $msRemote -Filter "*.joypro.json").Count -eq 0) "msfs must not write JoyPro files into the Steam folder."

        $msRoundTrip = Run-Engine @("--profile", $msProfile, "msfs2020", "export", $msRemote, "--game-profile", "Sting", "--text")
        Assert-True ($msRoundTrip.ExitCode -eq 0) "msfs export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($msSting) -eq $msStingText) "msfs import/export round trip must leave the profile byte-identical."

        $msSetGear = Run-Engine @("--profile", $msProfile, "binds", "set", "Gear Toggle", "--input", "JOY_BTN5", "--text")
        Assert-True ($msSetGear.ExitCode -eq 0) "binds set on the imported MSFS relation failed."
        $msSetElevator = Run-Engine @("--profile", $msProfile, "binds", "set", "Elevator Axis", "--inverted", "false", "--text")
        Assert-True ($msSetElevator.ExitCode -eq 0) "binds set --inverted on the MSFS elevator failed."
        $msAddAp = Run-Engine @("--profile", $msProfile, "relations", "add", "MSFS AP", "--button", "--node", "MSFS:KEY_AP_MASTER", "--device", $msStick, "--input", "JOY_BTN2", "--text")
        Assert-True ($msAddAp.ExitCode -eq 0) "relations add for MSFS autopilot failed."

        $msExport = Run-Engine @("--profile", $msProfile, "msfs2020", "export", $msRemote, "--game-profile", "Sting", "--text")
        Assert-True ($msExport.ExitCode -eq 0) "msfs export after rebind failed."
        Assert-NotMatch $msExport.Output "context is unknown" "msfs should learn the 2020 context of a new action from the other profiles."
        $msAfter = [System.IO.File]::ReadAllText($msSting)
        Assert-Match $msAfter '(?s)ActionName="KEY_GEAR_TOGGLE" Flag="2">\s*<Primary>\s*<KEY Information="Joystick Button 12">11</KEY>\s*</Primary>\s*<Secondary>\s*<KEY Information="Joystick Button 5">4</KEY>' "msfs export should replace JoyPro's gear button and keep the user's second button."
        Assert-Match $msAfter 'ActionName="KEY_AXIS_ELEVATOR_SET" Flag="4">' "msfs export should clear the invert flag."
        Assert-Match $msAfter '(?s)<Context ContextName="PLANE">.*ActionName="KEY_AP_MASTER" Flag="2">\s*<Primary>\s*<KEY Information="Joystick Button 2">1</KEY>' "msfs export should add a new action to its MSFS 2020 context."
        Assert-Match $msAfter '(?s)<Context ContextName="MODES">\s*<Action ActionName="KEY_CYCLE_PILOTVIEW_NEXT" Flag="2">' "msfs export must keep camera contexts."
        Assert-True ([System.IO.File]::ReadAllText((Join-Path $msRemote "inputprofile_0000000002")) -eq $msDefaultText) "msfs export must not touch profiles that were not picked."
        Assert-True (@(Get-ChildItem -Path $msRemote -Filter "*.jpbak").Count -eq 0) "msfs backups must not go into the Steam folder."

        $ms2024Import = Run-Engine @("--profile", $ms2024Profile, "msfs2024", "import", $ms2024, "--include-disconnected", "--text")
        Assert-True ($ms2024Import.ExitCode -eq 0) "msfs 2024 import failed."
        $ms2024RoundTrip = Run-Engine @("--profile", $ms2024Profile, "msfs2024", "export", $ms2024, "--text")
        Assert-True ($ms2024RoundTrip.ExitCode -eq 0) "msfs 2024 export after import failed."
        Assert-True ([System.IO.File]::ReadAllText($ms2024) -eq $ms2024Text) "msfs 2024 import/export round trip must leave the export byte-identical."
        $ms2024Flaps = Run-Engine @("--profile", $ms2024Profile, "relations", "add", "MSFS Flaps axis", "--axis", "--node", "MSFS2024:KEY_AXIS_FLAPS_SET", "--device", $msStick, "--input", "JOY_SLIDER1", "--text")
        Assert-True ($ms2024Flaps.ExitCode -eq 0) "relations add for the MSFS 2024 flaps axis failed."
        $ms2024Delete = Run-Engine @("--profile", $ms2024Profile, "relations", "mass", "delete", "--relation", "AP Master", "--text")
        Assert-True ($ms2024Delete.ExitCode -eq 0) "relations mass delete for the MSFS 2024 autopilot failed."
        $ms2024Export = Run-Engine @("--profile", $ms2024Profile, "msfs2024", "export", $ms2024, "--text")
        Assert-True ($ms2024Export.ExitCode -eq 0) "msfs 2024 export failed."
        $ms2024After = [System.IO.File]::ReadAllText($ms2024)
        Assert-Match $ms2024After '(?s)ActionName="KEY_AXIS_FLAPS_SET" ValueEvent="0.000000" Delay="0.000000" Flag="4">\s*<Primary>\s*<KEY Information="Joystick Slider X ">514</KEY>' "msfs 2024 export should expand an unbound action and set the axis flag."
        Assert-Match $ms2024After 'ActionName="KEY_AP_MASTER" ValueEvent="0.000000" Delay="2.000000" Flag="2"/>' "msfs 2024 export should turn a released action back into an empty element."
        Assert-Match $ms2024After 'ActionName="KEY_AP_ALT_VAR_INC" ValueEvent="0.000000" Delay="0.000000" Flag="2"/>' "msfs 2024 export must keep untouched actions."
    }
    finally {
        $env:JOYPRO_DATA_DIR = $previousDataDir
    }
    }

    # Game update detection: a recorded old build makes an installed game "game-updated"; nothing is written with --no-record.
    $upStampRoots = @((Join-Path $engineBin "DB"), (Join-Path $env:JOYPRO_DATA_DIR "GameData\DB"))
    $upStamps = @(Get-ChildItem -Path $upStampRoots -Recurse -Filter "source-version.txt" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName + "|" + $_.LastWriteTimeUtc.Ticks })
    $upCheck = Run-Engine @("db", "check-updates", "--no-record", "--text")
    Assert-True ($upCheck.ExitCode -eq 0) "db check-updates failed."
    Assert-Match $upCheck.Output "(?m)^DB Update Check\s*$" "db check-updates should print its report."
    $upStampsAfter = @(Get-ChildItem -Path $upStampRoots -Recurse -Filter "source-version.txt" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName + "|" + $_.LastWriteTimeUtc.Ticks })
    Assert-True (($upStamps -join ";") -eq ($upStampsAfter -join ";")) "db check-updates --no-record must not write build stamps."
    $upJson = Run-Engine @("db", "check-updates", "--no-record")
    $upReport = $upJson.Output | ConvertFrom-Json
    Assert-True ($upReport.Games.Count -ge 10) "db check-updates should report DCS, IL-2, Star Citizen and the registry games."

    # An interrupted UI-driven DCS clean refresh leaves a marker; the next refresh restores the user's Input folder.
    $rcSaved = Join-Path $tempDir "dcs-recover\\saved"
    $rcInstall = Join-Path $tempDir "dcs-recover\\install"
    $rcBackupFile = Join-Path $rcSaved "Config\\CLInput\\Input\\F-16C_50\\joystick\\Stick.diff.lua"
    New-Item -Path (Split-Path $rcBackupFile) -ItemType Directory -Force | Out-Null
    New-Item -Path $rcInstall -ItemType Directory -Force | Out-Null
    Set-Content -Path $rcBackupFile -Value "user bind"
    Set-Content -Path (Join-Path $rcSaved "Config\\JoyPro_refresh_restore.txt") -Value @((Join-Path $rcSaved "Config\\Input"), (Join-Path $rcSaved "Config\\CLInput\\Input"), "2026-09-30")
    $rcRun = Run-Engine @("dcs", "refresh", "clean", $rcSaved, $rcInstall, "--dry-run", "--confirm", "false", "--text")
    Assert-Match $rcRun.Output "earlier DCS refresh was interrupted; your DCS Input folder was restored" "dcs refresh should restore the Input folder of an interrupted refresh."
    Assert-True (Test-Path (Join-Path $rcSaved "Config\\Input\\F-16C_50\\joystick\\Stick.diff.lua")) "the restored Input folder should contain the user's binds."
    Assert-True (-not (Test-Path (Join-Path $rcSaved "Config\\JoyPro_refresh_restore.txt"))) "the restore marker should be removed after restoring."

    # Layout pictures: the model comes from VID:PID/HID name, only layouts of the same product are picked, and a
    # user picture gets markers for the device's real inputs.
    $previousFakeDevices = $env:JOYPRO_FAKE_DEVICES
    $previousDataDir = $env:JOYPRO_DATA_DIR
    $env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\\fixtures\\xplane\\devices.tsv"
    $env:JOYPRO_DATA_DIR = Join-Path $tempDir "joypro-data"
    try {
        $lyIdentify = Run-Engine @("layout", "identify", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--text")
        Assert-True ($lyIdentify.ExitCode -eq 0) "layout identify failed."
        Assert-Match $lyIdentify.Output "model=Smoke Stick \| kind=Stick \| vid:pid=1234:5678 \| connected=yes" "layout identify should use the HID name, VID:PID and the device's axes."
        Assert-Match $lyIdentify.Output "search=https://www\.google\.com/search\?tbm=isch&q=Smoke%20Stick" "layout identify should search for the model name, not the device GUID."

        $lyVkb = Run-Engine @("layout", "suggest", "--device", "VKB-Sim Gunfighter Modern Combat Pro Twist {1A2B3C4D-0A3F-11f0-8011-444553540000}", "--text")
        Assert-True ($lyVkb.ExitCode -eq 0) "layout suggest for a VKB Gunfighter failed."
        # No pictures of other people are bundled (docs/licensing-audit.md P1): JoyPro's drawing or a named placeholder.
        Assert-Match $lyVkb.Output "Template: (JoyPro drawing of VKB|Placeholder for VKB-Sim Gunfighter)" "layout suggest should use JoyPro's drawing or a named placeholder for a VKB Gunfighter."
        Assert-NotMatch $lyVkb.Output "Source: catalog" "layout suggest must not find a bundled layout picture."

        $lyWarthog = Run-Engine @("layout", "suggest", "--device", "Thrustmaster HOTAS Warthog {00000000-0000-0000-0000-00000000AAAA}", "--text")
        Assert-True ($lyWarthog.ExitCode -eq 0) "layout suggest for a Warthog failed."
        Assert-NotMatch $lyWarthog.Output "Template: VKB" "layout suggest must not show a picture of another vendor's stick."
        Assert-Match $lyWarthog.Output "Template: Placeholder for Thrustmaster HOTAS Warthog" "layout suggest should fall back to a named placeholder."

        Add-Type -AssemblyName System.Drawing
        $lyPicture = Join-Path $tempDir "smoke-stick.png"
        $bitmap = New-Object System.Drawing.Bitmap 800, 600
        try { $bitmap.Save($lyPicture, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
        $lyUser = Run-Engine @("layout", "suggest", "--device", "Smoke Stick {11111111-2222-3333-4444-555555555555}", "--backdrop", $lyPicture, "--text")
        Assert-True ($lyUser.ExitCode -eq 0) "layout suggest with a user picture failed."
        Assert-Match $lyUser.Output "Match: Picture chosen by the user\." "layout suggest should use the user's picture."
        Assert-Match $lyUser.Output "Markers: total=19, button=12, axis=3, pov=4, slider=0" "layout suggest should place markers for the device's real buttons, axes and hat."
        Assert-True (@(Get-ChildItem -Path (Join-Path $env:JOYPRO_DATA_DIR "AutoLayouts") -Filter "*.layout").Count -ge 1) "layout suggest should write the generated layout to the JoyPro data folder."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $previousFakeDevices
        $env:JOYPRO_DATA_DIR = $previousDataDir
    }

    # Per-game smoke tests (engine\tests\smoke\*.ps1), dot-sourced here: they use Run-Engine, the Assert-* helpers,
    # $tempDir and $engineDir, and clean up what they change.
    # The parts that read real game catalogs (DB\<game>, CleanProfile) run only with the development game lists. A new
    # part that needs them goes into this list (check: it must pass in a checkout without EngineOnly_Runtime\DB\<game>).
    $smokeNeedingGameData = @("aiallgames.ps1", "arma.ps1", "bms.ps1", "elite.ps1", "grebels.ps1", "il2.ps1", "legacyids.ps1", "msfs.ps1",
        "nlgroup.ps1", "nuclearoption.ps1", "progress.ps1", "reforger.ps1", "relationassistant.ps1", "relationbuilder.ps1", "searchranking.ps1", "autogroupown.ps1", "setupwizard.ps1", "autoassignmatrix.ps1", "sync.ps1",
        "tinycombatarena.ps1", "vtolvr.ps1", "wardogs.ps1", "warthunder.ps1", "x4.ps1", "xplane.ps1")
    foreach ($gameSmoke in @(Get-ChildItem -Path (Join-Path $engineDir "tests\smoke") -Filter "*.ps1" -ErrorAction SilentlyContinue | Sort-Object Name)) {
        if ($smokeNeedingGameData -contains $gameSmoke.Name -and (Skip-WithoutGameData ("smoke\" + $gameSmoke.Name))) {
            continue
        }
        Write-Host ("Smoke: " + $gameSmoke.Name)
        . $gameSmoke.FullName
    }

    $wikiScript = Join-Path $engineDir "tools\update_cli_wiki.ps1"
    Assert-True (Test-Path $wikiScript) "Wiki update script missing at: $wikiScript"
    & powershell -ExecutionPolicy Bypass -File $wikiScript -Configuration $Configuration -Platform $Platform -CheckOnly | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "CLI wiki is out of date. Run tools/update_cli_wiki.ps1."
}
finally {
    $env:JOYPRO_TEST_IGNORE_RUNNING_GAMES = $previousIgnoreRunningGames
    $env:JOYPRO_DATA_DIR = $previousSmokeDataDir
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $previousAssumeInstalled
    if ($starCitizenDbCatalogExisted) {
        if (Test-Path $starCitizenDbCatalogBackup) {
            Copy-Item -Path $starCitizenDbCatalogBackup -Destination $starCitizenDbCatalogPath -Force
        }
    }
    elseif (Test-Path $starCitizenDbCatalogPath) {
        Remove-Item -Path $starCitizenDbCatalogPath -Force
    }

    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force
    }
    $env:JOYPRO_LOG_DIR = $previousSmokeLogDir
    $env:JOYPRO_NO_INTERNET_PICTURES = $previousNoInternetPictures
    $env:JOYPRO_TEST_SANDBOX = $previousSandbox
}

# No engine of the run tried to write outside the sandbox (a part that swallowed the exit code is caught here).
$sandboxViolations = Join-Path $smokeSandbox "sandbox-violations.log"
Assert-True (-not (Test-Path $sandboxViolations)) ("An engine run tried to write outside the test sandbox (refused):" + [Environment]::NewLine +
    ((Get-Content -Path $sandboxViolations -TotalCount 60 -ErrorAction SilentlyContinue) -join [Environment]::NewLine))
Remove-Item -Path $smokeSandbox -Recurse -Force -ErrorAction SilentlyContinue

# Every run wrote its background log (header, command, exit code) into the smoke log folder.
$smokeLogs = @(Get-ChildItem -Path $smokeLogDir -Filter "engine-*.log" -ErrorAction SilentlyContinue)
Assert-True ($smokeLogs.Count -gt 0) "No engine log was written to $smokeLogDir."
$smokeLogText = ($smokeLogs | ForEach-Object { Get-Content -Raw -Path $_.FullName }) -join [Environment]::NewLine
Assert-True ($smokeLogText -match "=== engine \S+ started") "The engine log has no start header."
Assert-True ($smokeLogText -match "\[Engine\] Run finished: exit code \d+") "The engine log has no exit line."
Assert-True (-not ($smokeLogText -match [Regex]::Escape($env:USERPROFILE + "\"))) "The engine log contains the user profile path (privacy scrubbing)."
Remove-Item -Path $smokeLogDir -Recurse -Force -ErrorAction SilentlyContinue

if ($script:SkippedForGameData.Count -gt 0) {
    Write-Host ("Skipped " + $script:SkippedForGameData.Count + " part(s) that need the development game lists (not in a public checkout):")
    foreach ($part in $script:SkippedForGameData) { Write-Host ("  - " + $part) }
}
Write-Host "PASS: engine smoke test completed."



