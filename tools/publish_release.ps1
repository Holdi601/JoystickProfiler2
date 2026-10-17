# Builds a JoyPro2 release package for the self-updater:
#   <OutDir>\JoyPro2-<version>-win-x64.zip          (self-contained GUI + engine, no .NET 9 needed on the user's PC)
#   <OutDir>\JoyPro2-<version>-win-x64.zip.sha256   (checked by the updater before installing)
# Publish both files as assets of a GitHub release tagged v<version> in a repository the updater can read
# (gui/JoyPro.Gui/Services/AppInfo.cs), e.g.:
#   gh release create v<version> <zip> <zip>.sha256 --title "JoyPro2 <version>" --notes-file notes.md
#
# The package holds no game data (THIRD_PARTY_NOTICES.md, section 1.4): only JoyPro's own DB folders (Functions, Devices, Llm) ship;
# game command lists, DCS clean profiles and screenshots are built on the user's PC (tools/ReleaseContent.psm1).
#   -DryRun   publishes nothing: lists what the package would leave out of the current engine build
#             (engine\bin\x64\<Configuration>) and what it keeps.
param(
    [string]$OutDir = "",
    [string]$Configuration = "Release",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot "ReleaseContent.psm1") -Force -DisableNameChecking

if ($DryRun) {
    $engineBuild = Join-Path $root "engine\bin\x64\$Configuration"
    if (-not (Test-Path (Join-Path $engineBuild "engine.exe"))) {
        throw "No engine build in $engineBuild; build $Configuration first (dotnet build gui\JoyPro.Gui\JoyPro.Gui.csproj -c $Configuration)."
    }
    $plan = Remove-JoyProGameData -EngineDir $engineBuild -DryRun
    Write-Host "Dry run ($engineBuild): the package would leave out"
    if ($plan.Removed.Count -eq 0) { Write-Host "  (nothing)" }
    foreach ($item in $plan.Removed) { Write-Host ("  engine\" + $item) }
    Write-Host ("and keep JoyPro's own data: " + (($plan.KeptDb | ForEach-Object { "engine\" + $_ }) -join ", ") + ", engine\TOOLS\Conversions, engine\LayoutCatalog.")
    return
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $root "artifacts"
}

[xml]$project = Get-Content (Join-Path $root "gui\JoyPro.Gui\JoyPro.Gui.csproj")
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "No <Version> in JoyPro.Gui.csproj." }

$name = "JoyPro2-$version-win-x64"
$publishDir = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

# A fresh build: incremental builds keep binaries compiled with debug symbols (their debug directory names this
# folder). Only this configuration's output folders of the engine and the GUI projects are removed.
$buildFolders = @((Join-Path $root "engine\obj\x64\$Configuration"), (Join-Path $root "engine\bin\x64\$Configuration"))
foreach ($projectDir in Get-ChildItem -Path (Join-Path $root "gui") -Directory -Filter "JoyPro.*" | Where-Object { $_.Name -notlike "*.Tests" }) {
    $buildFolders += (Join-Path $projectDir.FullName "obj\$Configuration")
    $buildFolders += (Join-Path $projectDir.FullName "bin\$Configuration")
}
foreach ($folder in $buildFolders) {
    for ($try = 1; (Test-Path $folder) -and $try -le 5; $try++) {
        try { Remove-Item -Recurse -Force $folder -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
    # A folder another process (indexer, sync tool) still holds open may stay, empty: no file may stay.
    if ((Test-Path $folder) -and @(Get-ChildItem -Path $folder -Recurse -File -Force).Count -gt 0) { throw "Could not clean $folder." }
}
Write-Host "Publishing JoyPro2 $version ($Configuration) to $publishDir"
# No debug symbols: a .pdb (and the debug directory of every .dll/.exe) names the build machine's source folder.
& dotnet publish (Join-Path $root "gui\JoyPro.Gui\JoyPro.Gui.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:AutoStopRunningJoyProGuiOnBuild=false -p:DebugType=none -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

foreach ($required in @("JoyPro.Gui.exe", "engine\engine.exe", "engine\DB", "engine\LayoutCatalog")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "The package misses $required." }
}

# The engine is copied from its whole build folder (engine\bin\x64\<Configuration>): it holds the development copies of
# game data (EngineOnly_Runtime) and can hold leftovers of old builds and test runs (the SlimDX installer of JoyPro 1,
# tmp_* debug profiles, debug symbols with local paths). Only JoyPro's own data ships.
$engineDir = Join-Path $publishDir "engine"
$pruned = Remove-JoyProGameData -EngineDir $engineDir
foreach ($item in $pruned.Removed) { Write-Host "Left out of the package: engine\$item" }
Assert-NoGameData -EngineDir $engineDir

# Nothing in the package may name the build machine's folders (the source folder, the user profile).
Get-ChildItem -Path $publishDir -Recurse -File -Filter *.pdb | Remove-Item -Force
$localPaths = @($root, $env:USERPROFILE) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') }
$leaks = New-Object System.Collections.Generic.List[string]
foreach ($file in Get-ChildItem -Path $publishDir -Recurse -File) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    foreach ($path in $localPaths) {
        if ($ascii.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $utf16.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $leaks.Add($file.FullName.Substring($publishDir.Length + 1) + " names " + $path)
            break
        }
    }
}
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Write-Host $_ }
    throw ("The package names local folders in " + $leaks.Count + " file(s).")
}
# Third-party notices (THIRD_PARTY_NOTICES.md, section 4.3): this list plus the notice files the packages carry.
$licenseDir = Join-Path $publishDir "licenses"
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") (Join-Path $publishDir "THIRD_PARTY_NOTICES.md") -Force
# JoyPro's own license (GPL-3.0-or-later, unmodified text) and the author's notice travel with every package.
Copy-Item (Join-Path $root "LICENSE") (Join-Path $publishDir "LICENSE.txt") -Force
Copy-Item (Join-Path $root "NOTICE.md") (Join-Path $publishDir "NOTICE.md") -Force
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$noticeFiles = @(
    @{ From = "skiasharp.nativeassets.win32\2.88.9\THIRD-PARTY-NOTICES.txt"; To = "SkiaSharp-THIRD-PARTY-NOTICES.txt" },
    @{ From = "harfbuzzsharp.nativeassets.win32\8.3.1.1\THIRD-PARTY-NOTICES.txt"; To = "HarfBuzzSharp-THIRD-PARTY-NOTICES.txt" },
    @{ From = "avalonia.angle.windows.natives\2.1.25547.20250602\LICENSE"; To = "ANGLE-LICENSE.txt" },
    @{ From = "communitytoolkit.mvvm\8.2.1\ThirdPartyNotices.txt"; To = "CommunityToolkit-ThirdPartyNotices.txt" }
)
$runtimePack = Get-ChildItem -Path (Join-Path $nugetRoot "microsoft.netcore.app.runtime.win-x64") -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
if ($runtimePack) {
    # The runtime pack names the file THIRD-PARTY-NOTICES.TXT (9.0.x), older packs ThirdPartyNotices.txt.
    $runtimeNotice = @("THIRD-PARTY-NOTICES.TXT", "ThirdPartyNotices.txt") | Where-Object { Test-Path (Join-Path $runtimePack.FullName $_) } | Select-Object -First 1
    if (-not $runtimeNotice) { $runtimeNotice = "THIRD-PARTY-NOTICES.TXT" }
    $noticeFiles += @{ From = "microsoft.netcore.app.runtime.win-x64\$($runtimePack.Name)\$runtimeNotice"; To = "dotnet-ThirdPartyNotices.txt" }
}
foreach ($notice in $noticeFiles) {
    $source = Join-Path $nugetRoot $notice.From
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $licenseDir $notice.To) -Force
    } else {
        Write-Warning "Third-party notice not found (package version changed? update THIRD_PARTY_NOTICES.md): $source"
    }
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline

Write-Host "Package:  $zip"
Write-Host "Checksum: $zip.sha256 ($hash)"
Write-Host "Next: gh release create v$version `"$zip`" `"$zip.sha256`" --title `"JoyPro2 $version`" --notes-file <notes.md>"
