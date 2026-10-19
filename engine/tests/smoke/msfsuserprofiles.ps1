# MSFS import of the user's own profiles (docs/games/msfs.md "Import"): Steam remote folders and the Microsoft Store's
# save containers (wgs: containers.index, container.<n>, blobs named by GUID), JoyPro's preset package only besides, and
# the command list built on demand in an empty data folder of a release-like program folder. Dot-sourced by
# engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin). Stand-in user folders
# (JOYPRO_TEST_MSFS_HOME), so no real profile is read. Fixtures: tests\fixtures\msfs\user-profiles (anonymised copies of
# real MSFS 2020 Steam profiles; MSFS 2024 profiles in the sim's user format, the X-56 one from Asobo's own preset).

function Write-MupString([System.IO.BinaryWriter]$W, [string]$Text) {
    $W.Write([int]$Text.Length)
    $W.Write([System.Text.Encoding]::Unicode.GetBytes($Text))
}

# A wgs user folder with one container per entry of $Containers (@{ Name; Blobs = @(@{ Name; Bytes; Time }) }).
function New-MupWgsStore([string]$WgsFolder, [string]$Family, [object[]]$Containers) {
    $user = Join-Path $WgsFolder "0009000A1B2C3D_69F80140"
    New-Item -Path $user -ItemType Directory -Force | Out-Null
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]14); $w.Write([int]$Containers.Count); $w.Write([uint32]0)
    Write-MupString $w ($Family.Replace("_8wekyb3d8bbwe", "_1.0.0.0_x64__8wekyb3d8bbwe") + "!App")
    $w.Write([uint64]133700000000000000); $w.Write([uint32]0)
    Write-MupString $w "00000000-0000-0000-0000-000000000000"
    $w.Write([uint64]0)
    foreach ($container in $Containers) {
        $folder = [Guid]::NewGuid()
        Write-MupString $w $container.Name; Write-MupString $w $container.Name; Write-MupString $w '"0x8DCC4B5A1E2F3A4"'
        $w.Write([byte]1); $w.Write([uint32]0); $w.Write($folder.ToByteArray()); $w.Write([uint64]133700000000000000); $w.Write((New-Object byte[] 16))
        $dir = Join-Path $user $folder.ToString("N").ToUpperInvariant()
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        $cms = New-Object System.IO.MemoryStream
        $cw = New-Object System.IO.BinaryWriter($cms)
        $cw.Write([uint32]4); $cw.Write([int]@($container.Blobs).Count)
        foreach ($blob in @($container.Blobs)) {
            $file = [Guid]::NewGuid()
            $name = New-Object byte[] 128
            $nameBytes = [System.Text.Encoding]::Unicode.GetBytes($blob.Name)
            [Array]::Copy($nameBytes, $name, [Math]::Min(128, $nameBytes.Length))
            $cw.Write($name); $cw.Write($file.ToByteArray()); $cw.Write($file.ToByteArray())
            $blobPath = Join-Path $dir $file.ToString("N").ToUpperInvariant()
            [System.IO.File]::WriteAllBytes($blobPath, [byte[]]$blob.Bytes)
            if ($blob.Time) { [System.IO.File]::SetLastWriteTime($blobPath, $blob.Time) }
        }
        [System.IO.File]::WriteAllBytes((Join-Path $dir "container.1"), $cms.ToArray())
    }
    [System.IO.File]::WriteAllBytes((Join-Path $user "containers.index"), $ms.ToArray())
    return $user
}

# zlib (78 9C, deflate, Adler-32) of a file's bytes.
function Get-MupZlib([byte[]]$Bytes) {
    $out = New-Object System.IO.MemoryStream
    $out.WriteByte(0x78); $out.WriteByte(0x9C)
    $deflate = New-Object System.IO.Compression.DeflateStream($out, [System.IO.Compression.CompressionMode]::Compress, $true)
    $deflate.Write($Bytes, 0, $Bytes.Length); $deflate.Dispose()
    [uint32]$a = 1; [uint32]$b = 0
    foreach ($x in $Bytes) { $a = ($a + $x) % 65521; $b = ($b + $a) % 65521 }
    $adler = [BitConverter]::GetBytes([uint32](($b * 65536) + $a)); [Array]::Reverse($adler)
    $out.Write($adler, 0, 4)
    return $out.ToArray()
}

$mupFixtures = Join-Path $engineDir "tests\fixtures\msfs\user-profiles"
$mupDir = Join-Path $tempDir "mup"
$mupPreviousHome = $env:JOYPRO_TEST_MSFS_HOME
$mupPreviousData = $env:JOYPRO_DATA_DIR
$mupPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$mupRead = { param([string]$Name) [System.IO.File]::ReadAllBytes((Join-Path $mupFixtures ("profiles2024\" + $Name))) }
try {
    $env:JOYPRO_INSTALL_DETECTION = "off"

    # --- The report's layout: MSFS 2024 from the Microsoft Store, no JoyPro package in Community2024, no command list.
    # A release-like program folder (JoyPro's own DB folders only) and an empty data folder.
    $mupRoot = Join-Path $mupDir "program"
    New-Item -Path (Join-Path $mupRoot "DB"), (Join-Path $mupRoot "TOOLS") -ItemType Directory -Force | Out-Null
    foreach ($own in @("Functions", "Devices", "Llm")) {
        Copy-Item -Path (Join-Path $engineBin ("DB\" + $own)) -Destination (Join-Path $mupRoot "DB") -Recurse -Force
    }
    Copy-Item -Path (Join-Path $engineBin "TOOLS\Conversions") -Destination (Join-Path $mupRoot "TOOLS") -Recurse -Force
    $mupHome = Join-Path $mupDir "h1"
    $env:JOYPRO_TEST_MSFS_HOME = $mupHome
    $env:JOYPRO_DATA_DIR = Join-Path $mupDir "d"
    $mupLocalCache = Join-Path $mupHome "Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache"
    $mupPackages = Join-Path $mupLocalCache "Packages"
    New-Item -Path (Join-Path $mupPackages "Community2024") -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $mupLocalCache "UserCfg.opt") -Value ('InstalledPackagesPath "' + $mupPackages + '"') -Encoding ASCII
    $mupWgs = Join-Path $mupHome "Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\SystemAppData\wgs"
    $mupNow = Get-Date
    New-MupWgsStore $mupWgs "Microsoft.Limitless_8wekyb3d8bbwe" @(
        @{ Name = "InputProfile_X56"; Blobs = @(@{ Name = "data"; Bytes = (& $mupRead "x56-stick-custom.xml"); Time = $mupNow.AddMinutes(-5) }) },
        @{ Name = "InputProfile_X56_Old"; Blobs = @(@{ Name = "data"; Bytes = (& $mupRead "x56-stick-older.xml"); Time = $mupNow.AddDays(-30) }) },
        @{ Name = "InputProfile_X56_Heli"; Blobs = @(@{ Name = "data"; Bytes = (& $mupRead "x56-stick-helicopter.xml"); Time = $mupNow.AddDays(-2) }) },
        @{ Name = "InputProfile_Crosswind"; Blobs = @(@{ Name = "data"; Bytes = (Get-MupZlib (& $mupRead "crosswind-2024.xml")); Time = $mupNow.AddDays(-1) }) },
        @{ Name = "InputProfile_Keyboard"; Blobs = @(@{ Name = "data"; Bytes = (& $mupRead "keyboard-2024.xml") }) },
        @{ Name = "Settings"; Blobs = @(@{ Name = "data"; Bytes = [byte[]](1..200 | ForEach-Object { [byte]($_ % 256) }) }) }
    ) | Out-Null
    Push-Location $mupRoot
    try {
        # Install detection is off for the test: the Store package folder stands for the installed sim.
        $mupInstall = Run-Engine @("games", "install", "set", "MSFS2024", (Join-Path $mupHome "Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe"), "--text")
        Assert-True ($mupInstall.ExitCode -eq 0) ("games install set MSFS2024 failed: " + $mupInstall.Output)
        $mupPaths = Run-Engine @("msfs2024", "paths", "--text")
        Assert-Match $mupPaths.Output ([regex]::Escape((Join-Path $mupPackages "Community2024\joypro-input-profiles"))) "msfs2024 paths should list JoyPro's package in the Store's Community2024 folder first."
        Assert-Match $mupPaths.Output ([regex]::Escape($mupWgs)) "msfs2024 paths should list the Store's save folder."

        $mupProfile = Join-Path $mupDir "store.pr0file"
        $mupImport = Run-Engine @("--profile", $mupProfile, "msfs2024", "import", "--include-disconnected", "--text")
        Assert-True ($mupImport.ExitCode -eq 0) ("msfs2024 import of Store profiles without JoyPro's package failed: " + $mupImport.Output)
        Assert-Match $mupImport.Output "had no Microsoft Flight Simulator 2024 command list yet: built it" "the import should build the missing command list first."
        Assert-True (Test-Path (Join-Path $env:JOYPRO_DATA_DIR "GameData\DB\MSFS2024\actions.tsv")) "the built command list should be saved in the data folder."
        Assert-Match $mupImport.Output "MSFS device profiles read: 3 from the Microsoft Store version" "the import should read the Store's X-56 (airplanes, helicopters) and Crosswind profiles."
        Assert-Match $mupImport.Output "imported the most recently saved one, 'X-56 Stick Custom'" "of two airplane profiles of the X-56 the newer one should be imported."
        Assert-Match $mupImport.Output "KEY_COCKPIT_QUICKVIEW_LEFT <- Saitek Pro Flight X-56 Rhino Stick .* JOY_BTN_POV1_L" "the camera bind (General category) should be imported."
        Assert-Match $mupImport.Output "1 MSFS keyboard, mouse or gamepad profile" "the keyboard profile should be reported."
        foreach ($expected in @("KEY_AXIS_ELEVATOR_SET <- Saitek Pro Flight X-56 Rhino Stick \{0000BBBB-0000-4000-8000-000000000201\} JOY_Y", "KEY_GEAR_TOGGLE <- .* JOY_BTN_POV1_D",
                                 "KEY_ROTOR_BRAKE_TOGGLE <- .* JOY_BTN4", "KEY_AXIS_RUDDER_SET <- MFG Crosswind V2 .* JOY_RZ", "KEY_AXIS_LEFT_BRAKE_SET <- MFG Crosswind V2 .* JOY_X")) {
            Assert-Match $mupImport.Output $expected ("msfs2024 Store import should have read " + $expected)
        }
        Assert-NotMatch $mupImport.Output "KEY_FLAPS_UP" "the older X-56 profile must not be imported."
        $mupElevator = (Run-Engine @("--profile", $mupProfile, "relations", "list", "--text")).Output
        Assert-Match $mupElevator "G LIMITER|G Limiter" "the G limiter relation should be there."
        $mupGLimiter = Run-Engine @("--profile", $mupProfile, "binds", "list", "--text")
        Assert-Match $mupGLimiter.Output "JOY_BTN6" "the G limiter bind (button 6 with button 3 as modifier) should be imported."
        $mupElevatorName = ([regex]::Match($mupElevator, "(?m)^([^|\r\n]*Elevator[^|\r\n]*?)\s*\|")).Groups[1].Value.Trim()
        $mupAileronName = ([regex]::Match($mupElevator, "(?m)^([^|\r\n]*Aileron[^|\r\n]*Axis[^|\r\n]*?)\s*\|")).Groups[1].Value.Trim()
        Assert-True ($mupElevatorName.Length -gt 0 -and $mupAileronName.Length -gt 0) ("the elevator and aileron relations should be there: " + $mupElevator)
        $mupElevatorTuning = Run-Engine @("--profile", $mupProfile, "binds", "tuning", "show", $mupElevatorName, "--game", "MSFS2024", "--text")
        Assert-Match $mupElevatorTuning.Output "axisDeadZone[=: ]+6" ("the elevator's per-action override (dead zone 6) should be imported: " + $mupElevatorTuning.Output)
        Assert-Match $mupElevatorTuning.Output "sensitivityPlus[=: ]+35" "the elevator's per-action sensitivity should be imported."
        $mupAileronTuning = Run-Engine @("--profile", $mupProfile, "binds", "tuning", "show", $mupAileronName, "--game", "MSFS2024", "--text")
        Assert-Match $mupAileronTuning.Output "game:MSFS2024: inv=1;" ("the inverted aileron axis (flag 128) should be imported: " + $mupAileronTuning.Output)
        Assert-Match $mupAileronTuning.Output "axisDeadZone[=: ]+3" "the aileron's device axis page (dead zone 3) should be imported."

        # The config watcher watches where a change made in the sim lands: the Store's save index, although JoyPro's
        # package does not exist.
        $mupStatus = Run-Engine @("sync", "status", "--game", "MSFS2024")
        Assert-Match $mupStatus.Output '"Directory":"[^"]*SystemAppData\\\\wgs","Filters":\["containers.index"\],"IncludeSubdirectories":true' ("sync status should watch the Store's save index: " + $mupStatus.Output)
        Assert-NotMatch $mupStatus.Output '"State":"missing"' "MSFS 2024 with Store profiles but without JoyPro's package is not 'missing' for the watcher."

        # Store profiles are read only: an export there is refused (the package in Community2024 is the way).
        $mupExport = Run-Engine @("--profile", $mupProfile, "msfs2024", "export", $mupWgs, "--text")
        Assert-True ($mupExport.ExitCode -ne 0) "an export into the Store's save folder must be refused."
        Assert-Match $mupExport.Output "read only" "the refused export should say why."

        # An index of another layout: every blob is checked instead.
        [System.IO.File]::WriteAllBytes((Join-Path (Get-ChildItem -Path $mupWgs -Directory | Select-Object -First 1).FullName "containers.index"), [byte[]](255, 255, 255, 255, 1, 2, 3))
        $mupScan = Run-Engine @("msfs2024", "scan", $mupWgs, "--include-disconnected", "--text")
        Assert-True ($mupScan.ExitCode -eq 0) ("msfs2024 scan with an unreadable save index failed: " + $mupScan.Output)
        Assert-Match $mupScan.Output "every saved file there is checked instead" "an unreadable index should be reported."
        Assert-Match $mupScan.Output "KEY_AXIS_RUDDER_SET <- MFG Crosswind V2" "the blobs should still be read without the index."
    }
    finally {
        Pop-Location
    }

    # --- No profile anywhere: the error names every folder searched.
    $mupEmpty = Join-Path $mupDir "h0"
    $env:JOYPRO_TEST_MSFS_HOME = $mupEmpty
    New-Item -Path (Join-Path $mupEmpty "Steam\userdata\12345\2537590\remote") -ItemType Directory -Force | Out-Null
    New-Item -Path (Join-Path $mupEmpty "Roaming\Microsoft Flight Simulator 2024\Packages\Community2024") -ItemType Directory -Force | Out-Null
    Set-Content -Path (Join-Path $mupEmpty "Roaming\Microsoft Flight Simulator 2024\UserCfg.opt") -Value ('InstalledPackagesPath "' + (Join-Path $mupEmpty "Roaming\Microsoft Flight Simulator 2024\Packages") + '"') -Encoding ASCII
    $mupNone = Run-Engine @("--profile", (Join-Path $mupDir "none.pr0file"), "msfs2024", "import", "--include-disconnected", "--text")
    Assert-True ($mupNone.ExitCode -ne 0) "an import without any MSFS 2024 profile should fail."
    Assert-Match $mupNone.Output "No Microsoft Flight Simulator 2024 input profiles found\. Searched:" "the error should say that nothing was found."
    Assert-Match $mupNone.Output ([regex]::Escape((Join-Path $mupEmpty "Steam\userdata\12345\2537590\remote")) + " \(no inputprofile_\* file\)") "the error should name the Steam folder searched."
    Assert-Match $mupNone.Output ([regex]::Escape("Microsoft.Limitless_8wekyb3d8bbwe\SystemAppData\wgs") + " \(not there\)") "the error should name the Store folder searched."
    Assert-NotMatch $mupNone.Output "has no presets yet" "a missing JoyPro package is no error of its own."

    # --- MSFS 2024 on Steam: inputprofile_* in the remote folder, plus JoyPro's package presets besides.
    $mupSteam = Join-Path $mupDir "h2"
    $env:JOYPRO_TEST_MSFS_HOME = $mupSteam
    $mupRemote = Join-Path $mupSteam "Steam\userdata\12345\2537590\remote"
    New-Item -Path $mupRemote -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $mupFixtures "profiles2024\x56-stick-custom.xml") -Destination (Join-Path $mupRemote "inputprofile_0000000201")
    Copy-Item -Path (Join-Path $mupFixtures "profiles2024\crosswind-2024.xml") -Destination (Join-Path $mupRemote "inputprofile_0000000202")
    $mupSteamPackages = Join-Path $mupSteam "Roaming\Microsoft Flight Simulator 2024\Packages"
    $mupPreset = Join-Path $mupSteamPackages "Community2024\joypro-input-profiles\InputProfiles\Categories\Airplane"
    New-Item -Path $mupPreset -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $mupFixtures "profiles2024\joypro-preset-vjoy.xml") -Destination (Join-Path $mupPreset "JoyPro_vJoy_Device_0000bbbb_2024_Planes.xml")
    Set-Content -Path (Join-Path $mupSteam "Roaming\Microsoft Flight Simulator 2024\UserCfg.opt") -Value ('InstalledPackagesPath "' + $mupSteamPackages + '"') -Encoding ASCII
    $mupSteamImport = Run-Engine @("--profile", (Join-Path $mupDir "steam.pr0file"), "msfs2024", "import", "--include-disconnected", "--text")
    Assert-True ($mupSteamImport.ExitCode -eq 0) ("msfs2024 import of Steam profiles failed: " + $mupSteamImport.Output)
    Assert-Match $mupSteamImport.Output "MSFS device profiles read: 1 JoyPro preset\(s\) \(Community2024 package\), 2 from Steam" "the Steam profiles and JoyPro's preset should both be read."
    Assert-Match $mupSteamImport.Output "KEY_TOGGLE_NAV_LIGHTS <- vJoy Device" "JoyPro's preset should be imported besides."
    Assert-Match $mupSteamImport.Output "KEY_AXIS_ELEVATOR_SET <- Saitek Pro Flight X-56" "the Steam profile should be imported."
    # The export target stays JoyPro's package: the Steam files are not written by an export to the package.
    $mupRemoteBefore = (Get-FileHash (Join-Path $mupRemote "inputprofile_0000000201")).Hash
    $mupSteamExport = Run-Engine @("--profile", (Join-Path $mupDir "steam.pr0file"), "msfs2024", "export", "--dry-run", "--text")
    Assert-True ($mupSteamExport.ExitCode -eq 0) ("msfs2024 export (dry run) after the Steam import failed: " + $mupSteamExport.Output)
    Assert-Match $mupSteamExport.Output "Config: .*joypro-input-profiles" "the export target should still be JoyPro's package."
    Assert-True ((Get-FileHash (Join-Path $mupRemote "inputprofile_0000000201")).Hash -eq $mupRemoteBefore) "the Steam profile must stay untouched."

    # --- MSFS 2020 from the Microsoft Store: real (anonymised) 2020 profiles in the save containers; import only.
    $mup2020 = Join-Path $mupDir "h3"
    $env:JOYPRO_TEST_MSFS_HOME = $mup2020
    $mup2020Wgs = Join-Path $mup2020 "Local\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\SystemAppData\wgs"
    $mup2020Containers = @(Get-ChildItem -Path (Join-Path $mupFixtures "steam2020") -File | ForEach-Object { @{ Name = $_.Name; Blobs = @(@{ Name = "data"; Bytes = [System.IO.File]::ReadAllBytes($_.FullName) }) } })
    New-MupWgsStore $mup2020Wgs "Microsoft.FlightSimulator_8wekyb3d8bbwe" $mup2020Containers | Out-Null
    Run-Engine @("games", "install", "set", "MSFS2020", (Join-Path $mup2020 "Local\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe"), "--text") | Out-Null
    $mup2020Paths = Run-Engine @("msfs2020", "paths", "--text")
    Assert-Match $mup2020Paths.Output ([regex]::Escape($mup2020Wgs)) "msfs2020 paths should list the Store's save folder."
    $mup2020Import = Run-Engine @("--profile", (Join-Path $mupDir "store2020.pr0file"), "msfs2020", "import", "--include-disconnected", "--text")
    Assert-True ($mup2020Import.ExitCode -eq 0) ("msfs2020 import from the Store failed: " + $mup2020Import.Output)
    Assert-Match $mup2020Import.Output "MSFS device profiles read: 4 from the Microsoft Store version" "the four device profiles (not the gamepad) should be read."
    Assert-Match $mup2020Import.Output "Bindings Read: 23" "the Store profiles should give the 20 flight binds and 3 camera zoom axes of the fixtures."
    Assert-Match $mup2020Import.Output "KEY_THROTTLE_AXIS_SET_EX1 <- LEFT VPC Throttle MT-50 CM .* JOY_RX" "the throttle axis should be read."
    $mup2020Export = Run-Engine @("--profile", (Join-Path $mupDir "store2020.pr0file"), "msfs2020", "export", $mup2020Wgs, "--text")
    Assert-True ($mup2020Export.ExitCode -ne 0) "an export into MSFS 2020's Store save folder must be refused."
}
finally {
    $env:JOYPRO_TEST_MSFS_HOME = $mupPreviousHome
    $env:JOYPRO_DATA_DIR = $mupPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $mupPreviousDetection
}
