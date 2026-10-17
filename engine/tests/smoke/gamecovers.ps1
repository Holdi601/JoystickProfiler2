# Game covers from the web (engine media, engine/EngineGameCovers.cs, docs/export.md#pictures). Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $script:EngineExe). Never downloads anything from the internet:
# the cover list is replaced (JOYPRO_COVER_CATALOG) by addresses of "engine llm fake-server" on loopback.
# Covered: a download (and the type check skipping a web page first), a logo, the cache (no second request), a refused
# file (too big; image type but not a picture) that is not tried again on the next run, blocked mode (flag and
# variable: no request and no downloaded file used), and that the real list has an https source for every game.

$gcDir = Join-Path $tempDir "gamecovers"
$gcData = Join-Path $gcDir "data"
$gcServe = Join-Path $gcDir "serve"
New-Item -Path $gcData, $gcServe -ItemType Directory -Force | Out-Null
$gcPreviousData = $env:JOYPRO_DATA_DIR
$gcPreviousCatalog = $env:JOYPRO_COVER_CATALOG
$gcPreviousNoInternet = $env:JOYPRO_NO_INTERNET_PICTURES
$gcServer = $null

function Invoke-GcMedia([string[]]$Arguments) {
    $out = Join-Path $gcDir ("media-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out, "media", "games") + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

function Get-GcGame($Result, [string]$Id) {
    return @($Result.Json.Games | Where-Object { $_.GameId -eq $Id }) | Select-Object -First 1
}

function Get-GcRequests([string]$Name) {
    if (-not (Test-Path $gcServerLog)) { return 0 }
    return @([IO.File]::ReadAllLines($gcServerLog) | Where-Object { $_ -match ("^GET /files/" + [Regex]::Escape($Name) + "$") }).Count
}

try {
    # The real list: every game JoyPro knows has at least one https source (and a page it was taken from).
    $env:JOYPRO_COVER_CATALOG = $null
    $gcSourcesOut = Join-Path $gcDir "sources.json"
    $gcSources = Run-Engine @("--out", $gcSourcesOut, "media", "sources")
    Assert-True ($gcSources.ExitCode -eq 0) ("media sources failed: " + $gcSources.Output)
    $gcSourceList = [IO.File]::ReadAllText($gcSourcesOut) | ConvertFrom-Json
    Assert-True (@($gcSourceList.Games).Count -ge 30) "media sources should list every game."
    foreach ($game in @($gcSourceList.Games)) {
        Assert-True (@($game.Sources).Count -ge 1) ("no cover source for " + $game.GameId)
        foreach ($source in @($game.Sources)) {
            Assert-True ($source.Url -like "https://*") ($game.GameId + ": the cover source must be https: " + $source.Url)
            Assert-True ($source.SourcePage -like "https://*") ($game.GameId + ": the cover source needs its page: " + $source.Url)
            Assert-True (@("cover", "logo") -contains $source.Kind) ($game.GameId + ": unknown kind " + $source.Kind)
        }
    }

    # Files of the fake server: a JPEG, a PNG, a web page, a picture that is too big (6 MB > 5 MB) and an image type
    # that is no picture.
    $gcJpeg = [byte[]](@(0xFF, 0xD8, 0xFF, 0xE0) + (1..60 | ForEach-Object { [byte]($_ % 251) }))
    $gcPng = [byte[]](@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) + (1..40 | ForEach-Object { [byte]$_ }))
    [IO.File]::WriteAllBytes((Join-Path $gcServe "cover.jpg"), $gcJpeg)
    [IO.File]::WriteAllBytes((Join-Path $gcServe "logo.png"), $gcPng)
    [IO.File]::WriteAllText((Join-Path $gcServe "page.html"), "<html><body>not a picture</body></html>")
    $gcBig = New-Object byte[] (6 * 1024 * 1024)
    $gcBig[0] = 0xFF; $gcBig[1] = 0xD8; $gcBig[2] = 0xFF
    [IO.File]::WriteAllBytes((Join-Path $gcServe "big.jpg"), $gcBig)
    [IO.File]::WriteAllText((Join-Path $gcServe "fake.jpg"), "this is text with an image type")

    $gcPortFile = Join-Path $gcDir "port.txt"
    $gcServerLog = Join-Path $gcDir "server.log"
    $gcServer = Start-Process -FilePath $script:EngineExe -ArgumentList @("llm", "fake-server", "--port", "0", "--port-file", ('"' + $gcPortFile + '"'), "--serve", ('"' + $gcServe + '"'), "--log", ('"' + $gcServerLog + '"'), "--lifetime", "180") -PassThru -WindowStyle Hidden
    $gcWait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $gcPortFile) -and $gcWait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
    Assert-True (Test-Path $gcPortFile) "the fake server did not start."
    $gcBase = "http://127.0.0.1:" + ([IO.File]::ReadAllText($gcPortFile).Trim()) + "/files/"

    # Prepar3D and FSX: not on Steam (FSX: no Steam app id), so nothing comes from this PC's Steam cache first.
    $gcCatalog = @{
        P3D4 = @(@{ Url = ($gcBase + "page.html"); Kind = "cover"; SourcePage = "https://example.invalid/" }, @{ Url = ($gcBase + "cover.jpg"); Kind = "cover"; SourcePage = "https://example.invalid/" });
        P3D5 = @(@{ Url = ($gcBase + "logo.png"); Kind = "logo"; SourcePage = "https://example.invalid/" });
        P3D6 = @(@{ Url = ($gcBase + "big.jpg"); Kind = "cover"; SourcePage = "https://example.invalid/" });
        FSX = @(@{ Url = ($gcBase + "fake.jpg"); Kind = "cover"; SourcePage = "https://example.invalid/" })
    }
    $gcCatalogFile = Join-Path $gcDir "covers.json"
    [IO.File]::WriteAllText($gcCatalogFile, ($gcCatalog | ConvertTo-Json -Depth 5))
    $env:JOYPRO_COVER_CATALOG = $gcCatalogFile
    $env:JOYPRO_DATA_DIR = $gcData
    $env:JOYPRO_NO_INTERNET_PICTURES = $null
    $gcCovers = Join-Path $gcData "Media\Covers"

    # First run: downloads.
    $gcFirst = Invoke-GcMedia @()
    Assert-True ($gcFirst.ExitCode -eq 0 -and $null -ne $gcFirst.Json) ("media games with the fake covers failed: " + $gcFirst.Output)
    $gcP3d4 = Get-GcGame $gcFirst "P3D4"
    Assert-True ($gcP3d4.CoverSource -eq "download" -and $gcP3d4.CoverKind -eq "cover") ("P3D4 should get the downloaded cover: " + ($gcP3d4 | ConvertTo-Json -Compress))
    Assert-True ($gcP3d4.Cover -eq (Join-Path $gcCovers "P3D4.jpg")) ("the cover must be kept as <data>\Media\Covers\P3D4.jpg, not " + $gcP3d4.Cover)
    Assert-True ([Linq.Enumerable]::SequenceEqual([IO.File]::ReadAllBytes($gcP3d4.Cover), $gcJpeg)) "the kept cover differs from the served one."
    Assert-True ($gcP3d4.CoverUrl -eq ($gcBase + "cover.jpg")) "the cover should name its address."
    Assert-True ((Get-GcRequests "page.html") -eq 1) "the web page should have been asked for first (and refused)."
    $gcP3d5 = Get-GcGame $gcFirst "P3D5"
    Assert-True ($gcP3d5.CoverSource -eq "download" -and $gcP3d5.CoverKind -eq "logo" -and $gcP3d5.Cover -eq (Join-Path $gcCovers "P3D5.png")) ("P3D5 should get the downloaded logo: " + ($gcP3d5 | ConvertTo-Json -Compress))
    $gcP3d6 = Get-GcGame $gcFirst "P3D6"
    Assert-True ($gcP3d6.Cover -eq "") "a picture over the size limit must be refused."
    Assert-True (-not (Test-Path (Join-Path $gcCovers "P3D6.jpg"))) "a refused picture must not be kept."
    $gcFsx = Get-GcGame $gcFirst "FSX"
    Assert-True ($gcFsx.Cover -eq "") "a file that is no picture must be refused, whatever its type says."
    $gcWarnings = (@($gcFirst.Json.Warnings) -join " | ")
    Assert-Match $gcWarnings "P3D6.*too big" "the size refusal should be a warning."
    Assert-Match $gcWarnings "FSX.*not a JPEG, PNG or WebP" "the picture check should be a warning."
    # Games of the real list are not downloaded while the list is replaced.
    Assert-True (@($gcFirst.Json.Games | Where-Object { $_.CoverSource -eq "download" -and $_.GameId -notin @("P3D4", "P3D5") }).Count -eq 0) "only the test list may be downloaded."

    # Second run: from the cache, no request at all (refused games are tried again a day later).
    $gcBefore = [IO.File]::ReadAllLines($gcServerLog).Count
    $gcSecond = Invoke-GcMedia @()
    $gcAgain = Get-GcGame $gcSecond "P3D4"
    Assert-True ($gcAgain.CoverSource -eq "download" -and $gcAgain.Cover -eq $gcP3d4.Cover) "the second run should use the kept cover."
    Assert-True ((Get-GcGame $gcSecond "P3D5").CoverKind -eq "logo") "the kept logo should stay a logo."
    Assert-True ([IO.File]::ReadAllLines($gcServerLog).Count -eq $gcBefore) ("the second run asked the server again: " + ([IO.File]::ReadAllLines($gcServerLog) | Select-Object -Skip $gcBefore) -join " | ")
    Assert-True ((Get-GcRequests "cover.jpg") -eq 1 -and (Get-GcRequests "big.jpg") -eq 1) "each cover must be downloaded once."

    # Blocked: --no-internet-pictures and JOYPRO_NO_INTERNET_PICTURES: no request, and the kept files are not used.
    $gcBlocked = Invoke-GcMedia @("--no-internet-pictures")
    Assert-True ($gcBlocked.ExitCode -eq 0) ("media games --no-internet-pictures failed: " + $gcBlocked.Output)
    Assert-True ((Get-GcGame $gcBlocked "P3D4").Cover -eq "" -and (Get-GcGame $gcBlocked "P3D5").Cover -eq "") "--no-internet-pictures must not show a downloaded cover."
    $env:JOYPRO_NO_INTERNET_PICTURES = "1"
    $env:JOYPRO_DATA_DIR = Join-Path $gcDir "data-blocked"
    $gcBlockedEnv = Invoke-GcMedia @()
    Assert-True ((Get-GcGame $gcBlockedEnv "P3D4").Cover -eq "") "JOYPRO_NO_INTERNET_PICTURES must block downloads."
    Assert-True (-not (Test-Path (Join-Path $gcDir "data-blocked\Media\Covers"))) "blocked mode wrote a cover folder."
    Assert-True ([IO.File]::ReadAllLines($gcServerLog).Count -eq $gcBefore) "blocked mode asked the server."
}
finally {
    if ($null -ne $gcServer -and -not $gcServer.HasExited) {
        Stop-Process -Id $gcServer.Id -Force -ErrorAction SilentlyContinue
    }
    $env:JOYPRO_DATA_DIR = $gcPreviousData
    $env:JOYPRO_COVER_CATALOG = $gcPreviousCatalog
    $env:JOYPRO_NO_INTERNET_PICTURES = $gcPreviousNoInternet
}
