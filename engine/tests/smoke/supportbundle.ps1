# The support file (engine support bundle / unpack, docs/logging.md "Support file"): with --profile it holds the open
# profile and its history (cleaned: the user folder inside the profile's strings becomes %USERPROFILE%), the games'
# current bind files, the logs with the last errors, JoyPro's state and summary.txt with the profile and the number of
# history versions; the history restores with engine profile history after engine support unpack; --include-profile
# false leaves the personal parts out; the history size cap keeps the newest versions and names the others.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir). Only temp folders: an IL-2 install of its own,
# no detection of the PC's games.

Add-Type -AssemblyName System.IO.Compression.FileSystem
$sbRoot = Join-Path $tempDir "supportbundle"
$sbData = Join-Path $sbRoot "data"
$sbLogs = Join-Path $sbRoot "logs"
New-Item -Path $sbData, $sbLogs -ItemType Directory -Force | Out-Null
$sbPrevious = @{
    Data = $env:JOYPRO_DATA_DIR; Logs = $env:JOYPRO_LOG_DIR; Assume = $env:JOYPRO_TEST_ASSUME_INSTALLED; Detection = $env:JOYPRO_INSTALL_DETECTION; Jp1 = $env:JOYPRO_JOYPRO1_DIR
}
$env:JOYPRO_DATA_DIR = $sbData
$env:JOYPRO_LOG_DIR = $sbLogs
$env:JOYPRO_TEST_ASSUME_INSTALLED = $null
$env:JOYPRO_INSTALL_DETECTION = "off"
$sbUtf8 = New-Object System.Text.UTF8Encoding($false)

function Sb-Json([string]$Text) {
    $line = @($Text -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith("{") }) | Select-Object -First 1
    Assert-True ($null -ne $line) ("no JSON in the support bundle output: " + $Text)
    return ConvertFrom-Json $line
}

function Sb-Entries([string]$Zip) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $map = @{}
        foreach ($entry in $archive.Entries) {
            $bytes = New-Object byte[] $entry.Length
            $stream = $entry.Open()
            $read = 0
            while ($read -lt $bytes.Length) { $n = $stream.Read($bytes, $read, $bytes.Length - $read); if ($n -le 0) { break }; $read += $n }
            $stream.Dispose()
            $map[$entry.FullName] = $bytes
        }
        return $map
    }
    finally {
        $archive.Dispose()
    }
}

try {
    # An IL-2 install of its own (the only installed game) with its input files and JoyPro's .jp next to them.
    $sbIl2 = Join-Path $sbRoot "il2"
    $sbInput = Join-Path $sbIl2 "data\input"
    New-Item -Path $sbInput -ItemType Directory -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $sbInput "global.actions"), "// smoke actions`r`nactions,keys`r`n", $sbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $sbInput "devices.txt"), "configId,guid,model|`r`n0,%22{SMOKE-GUID}%22,Smoke Stick|`r`n", $sbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $sbInput "global.actions.jp"), "smoke metadata", $sbUtf8)
    $sbMap = "{`r`n  `"IL2Game`": `"" + $sbIl2.Replace("\", "\\") + "`"`r`n}`r`n"
    [System.IO.File]::WriteAllText((Join-Path $sbData "game-installs.json"), $sbMap, $sbUtf8)
    [System.IO.File]::WriteAllText((Join-Path $sbData "game-configs.json"), $sbMap, $sbUtf8)

    # A log with an error and its stack trace (the errors part reads them).
    $sbLog = Join-Path $sbLogs ("gui-" + (Get-Date).ToString("yyyyMMdd") + ".log")
    [System.IO.File]::WriteAllText($sbLog, (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff") + " ERROR gui#1 s:smoke c:- t:1 [Smoke] Something failed`r`n    System.InvalidOperationException: smoke boom`r`n       at Smoke.Test.Run()`r`n" +
        (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff") + " INFO  gui#1 s:smoke c:- t:1 [Smoke] fine`r`n", $sbUtf8)

    # A profile with three versions; one relation name holds the user folder (it must not reach the zip).
    $sbProfile = Join-Path $sbRoot "profiles\support.pr0file"
    Assert-True ((Run-Engine @("profile", "new", $sbProfile, "--text")).ExitCode -eq 0) "support bundle: profile new failed."
    Start-Sleep -Milliseconds 2200
    $sbPathRelation = "Path " + $env:USERPROFILE + "\Saved Games\smoke"
    Assert-True ((Run-Engine @("--profile", $sbProfile, "relations", "add", $sbPathRelation, "--button", "--node", "DCS:sb_path:A-10C_2", "--text")).ExitCode -eq 0) "support bundle: relations add failed."
    Start-Sleep -Milliseconds 2200
    Assert-True ((Run-Engine @("--profile", $sbProfile, "relations", "add", "Gear", "--button", "--node", "DCS:sb_gear:A-10C_2", "--text")).ExitCode -eq 0) "support bundle: second relations add failed."

    # JoyPro 1's profiles (Documents\JoyPro, here a folder of its own): an old profile and JoyPro 1's last.pr0file,
    # both holding the user folder in a relation name.
    $sbJp1 = Join-Path $sbRoot "joypro1"
    New-Item -Path $sbJp1 -ItemType Directory -Force | Out-Null
    Copy-Item $sbProfile (Join-Path $sbJp1 "Old Hornet.pr0file")
    Copy-Item $sbProfile (Join-Path $sbJp1 "last.pr0file")
    [System.IO.File]::WriteAllText((Join-Path $sbJp1 "not-a-profile.txt"), "notes", $sbUtf8)
    $env:JOYPRO_JOYPRO1_DIR = $sbJp1

    # --list shows the parts without writing anything.
    $sbList = Run-Engine @("--profile", $sbProfile, "support", "bundle", "--list", "--text")
    Assert-True ($sbList.ExitCode -eq 0) ("support bundle --list failed: " + $sbList.Output)
    Assert-Match $sbList.Output "(?m)^  \+ profile \|" "support bundle --list does not list the profile part."
    Assert-Match $sbList.Output "(?m)^  \+ joypro1 \| .*\| 2 file\(s\)" "support bundle --list does not list JoyPro 1's two profiles."
    Assert-Match $sbList.Output "(?m)^Profile: support\.pr0file .*3 version\(s\), 3 in this file" "support bundle --list does not name the profile and its versions."

    # With the profile (the default with --profile).
    $sbZip = Join-Path $sbRoot "with-profile.zip"
    $sbRun = Run-Engine @("--profile", $sbProfile, "--out", $sbZip, "support", "bundle")
    Assert-True ($sbRun.ExitCode -eq 0 -and (Test-Path $sbZip)) ("support bundle failed: " + $sbRun.Output)
    $sbJson = Sb-Json $sbRun.Output
    Assert-True ($sbJson.IncludeProfile -and $sbJson.Profile -eq "support.pr0file" -and $sbJson.HistoryVersions -eq 3 -and $sbJson.HistoryIncluded -eq 3) ("support bundle JSON: " + $sbRun.Output)
    $sbEntries = Sb-Entries $sbZip
    foreach ($name in @("summary.txt", "profile/support.pr0file", "profile/history/history.json", "errors/last-errors.txt", "devices/devices.txt", "devices/vjoy-hidhide.txt",
                        "state/data/game-installs.json", "state/versions.txt", "games/IL2Game/current/global.actions", "games/IL2Game/current/devices.txt",
                        "games/IL2Game/current/global.actions.jp", "games/IL2Game/current-files.txt")) {
        Assert-True ($sbEntries.ContainsKey($name)) ("the support file misses " + $name + ": " + (($sbEntries.Keys | Sort-Object) -join ", "))
    }
    Assert-True (@($sbEntries.Keys | Where-Object { $_ -like "profile/history/blobs/*.gz" -and $_ -notlike "*.items.gz" }).Count -eq 3) "the support file should hold the 3 stored versions."
    foreach ($name in @("joypro1/Old Hornet.pr0file", "joypro1/last.pr0file")) {
        Assert-True ($sbEntries.ContainsKey($name)) ("the support file misses JoyPro 1's " + $name + ": " + (($sbEntries.Keys | Sort-Object) -join ", "))
        $sbJp1Text = [System.Text.Encoding]::UTF8.GetString($sbEntries[$name])
        Assert-True (-not $sbJp1Text.Contains($env:USERPROFILE) -and $sbJp1Text.Contains("%USERPROFILE%\Saved Games\smoke")) ("JoyPro 1's " + $name + " in the support file still holds the user folder.")
    }
    Assert-True (@($sbEntries.Keys | Where-Object { $_ -like "joypro1/*" }).Count -eq 2) "only the .pr0file files of JoyPro 1's folder belong in the support file."
    $sbSummary = $sbUtf8.GetString($sbEntries["summary.txt"])
    Assert-Match $sbSummary "(?m)^Profile: support\.pr0file \(.*\), history: 3 version\(s\), 3 in this file" "summary.txt does not name the profile and its history versions."
    Assert-Match $sbSummary "(?m)^  game-binds " "summary.txt does not list the parts with their sizes."
    Assert-Match $sbUtf8.GetString($sbEntries["errors/last-errors.txt"]) "smoke boom" "errors/last-errors.txt misses the error with its stack trace."
    $sbProfileText = [System.Text.Encoding]::UTF8.GetString($sbEntries["profile/support.pr0file"])
    Assert-True (-not $sbProfileText.Contains($env:USERPROFILE) -and $sbProfileText.Contains("%USERPROFILE%\Saved Games\smoke")) "the profile in the support file still holds the user folder."
    foreach ($key in $sbEntries.Keys) {
        if ($key -like "*.gz" -or $key -like "*.pr0file") { continue }
        Assert-True (-not $sbUtf8.GetString($sbEntries[$key]).Contains($env:USERPROFILE + "\")) ("the support file entry " + $key + " holds the user folder.")
    }
    Assert-True ([System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($sbProfile)).Contains($env:USERPROFILE)) "the original profile must stay unchanged."

    # A maintainer restores a version from the zip in an isolated data folder.
    $sbCase = Join-Path $sbRoot "case"
    $sbUnpack = Run-Engine @("support", "unpack", $sbZip, "--to", $sbCase, "--text")
    Assert-True ($sbUnpack.ExitCode -eq 0) ("support unpack failed: " + $sbUnpack.Output)
    $sbCaseProfile = Join-Path $sbCase "profile\support.pr0file"
    Assert-True (Test-Path $sbCaseProfile) "support unpack did not write the profile."
    $env:JOYPRO_DATA_DIR = Join-Path $sbCase "data"
    $sbHistory = Run-Engine @("--profile", $sbCaseProfile, "profile", "history", "list", "--text")
    Assert-Match $sbHistory.Output "3 version\(s\)" ("the unpacked history does not list 3 versions: " + $sbHistory.Output)
    Assert-Match $sbHistory.Output "Relation 'Path %USERPROFILE%\\Saved Games\\smoke' created" "the unpacked history is not cleaned."
    Assert-Match (Run-Engine @("--profile", $sbCaseProfile, "relations", "list", "--text")).Output "(?m)^Gear \|" "the unpacked profile is not the newest version."
    $sbRestore = Run-Engine @("--profile", $sbCaseProfile, "profile", "history", "restore", "1", "--text")
    Assert-Match $sbRestore.Output "^Restored the version of" ("profile history restore of the unpacked history failed: " + $sbRestore.Output)
    Assert-Match (Run-Engine @("--profile", $sbCaseProfile, "relations", "list", "--text")).Output "(?m)^Relations: 0" "the restored first version should have no relations."
    $env:JOYPRO_DATA_DIR = $sbData

    # --include-profile false leaves the profile, its history and the games' bind files out.
    $sbZipNo = Join-Path $sbRoot "without-profile.zip"
    $sbRunNo = Run-Engine @("--profile", $sbProfile, "--out", $sbZipNo, "support", "bundle", "--include-profile", "false")
    Assert-True ($sbRunNo.ExitCode -eq 0) ("support bundle --include-profile false failed: " + $sbRunNo.Output)
    $sbEntriesNo = Sb-Entries $sbZipNo
    Assert-True (@($sbEntriesNo.Keys | Where-Object { $_ -like "profile/*" -or $_ -like "games/*/current*" -or $_ -like "joypro1/*" }).Count -eq 0) ("--include-profile false still holds personal parts: " + (($sbEntriesNo.Keys | Sort-Object) -join ", "))
    Assert-True ($sbEntriesNo.ContainsKey("summary.txt") -and $sbEntriesNo.ContainsKey("errors/last-errors.txt")) "--include-profile false lost the other parts."
    Assert-Match $sbUtf8.GetString($sbEntriesNo["summary.txt"]) "(?m)^Profile: not included" "summary.txt should say the profile is not included."

    # The history cap: the newest version and the whole index, the others named.
    $sbCap = Sb-Json (Run-Engine @("--profile", $sbProfile, "support", "bundle", "--list", "--history-cap-mb", "0.000001")).Output
    Assert-True ($sbCap.HistoryIncluded -eq 1 -and @($sbCap.HistoryLeftOut).Count -eq 2) ("the history cap should keep only the newest version: " + ($sbCap | ConvertTo-Json -Depth 3))
    Assert-True (@($sbCap.Notes | Where-Object { $_ -match "2 older version\(s\) left out" }).Count -eq 1) "the history cap is not named in the notes."

    # No profile open: none in the zip, summary.txt says so.
    $sbZipNone = Join-Path $sbRoot "no-profile.zip"
    Assert-True ((Run-Engine @("--out", $sbZipNone, "support", "bundle", "--include-profile", "true")).ExitCode -eq 0) "support bundle without a profile failed."
    $sbEntriesNone = Sb-Entries $sbZipNone
    Assert-Match $sbUtf8.GetString($sbEntriesNone["summary.txt"]) "(?m)^Profile: none open" "summary.txt should say that no profile is open."
    Assert-True ($sbEntriesNone.ContainsKey("games/IL2Game/current/global.actions")) "the box on without a profile should still hold the games' bind files."
    Assert-True ($sbEntriesNone.ContainsKey("joypro1/last.pr0file")) "the box on without a profile should still hold JoyPro 1's profiles."
}
finally {
    $env:JOYPRO_JOYPRO1_DIR = $sbPrevious.Jp1
    $env:JOYPRO_DATA_DIR = $sbPrevious.Data
    $env:JOYPRO_LOG_DIR = $sbPrevious.Logs
    $env:JOYPRO_TEST_ASSUME_INSTALLED = $sbPrevious.Assume
    $env:JOYPRO_INSTALL_DETECTION = $sbPrevious.Detection
}
