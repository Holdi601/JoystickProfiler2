# The engine server (engine serve, docs/testing.md "Speed"): the app runs its interactive commands in one long-running
# engine. Checked here: the profile copy it hands every command serializes to the same bytes as the profile read from
# the file, also after a save (serve --self-test); a sequence of commands gives in the server exactly what each gives in
# a process of its own (output, exit code, saved profile; serve --script); the protocol answers a command and the
# server ends on 'Q' and on a closed stdin.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$svDir = Join-Path $tempDir "server"
New-Item -Path $svDir -ItemType Directory -Force | Out-Null
$svPreviousData = $env:JOYPRO_DATA_DIR
$svPreviousOut = $env:JOYPRO_SCRIPT_OUT
try {
    $env:JOYPRO_DATA_DIR = Join-Path $svDir "data-setup"
    $svStick = "Server Stick {00000000-0000-0000-0000-0000000000S1}"
    $svProfile = Join-Path $svDir "server.pr0file"
    Assert-True ((Run-Engine @("profile", "new", $svProfile, "--text")).ExitCode -eq 0) "profile new failed."
    foreach ($i in 1..12) {
        $add = Run-Engine @("--profile", $svProfile, "relations", "add", ("Relation " + $i), "--button", "--node", ("DCS:server_" + $i + ":A-10C_2|F-16C_50"), "--device", $svStick, "--input", ("JOY_BTN" + $i), "--text")
        Assert-True ($add.ExitCode -eq 0) ("relations add failed: " + $add.Output)
    }
    Assert-True ((Run-Engine @("--profile", $svProfile, "relations", "item", "remove", "Relation 3", "--node", "DCS:server_3:A-10C_2", "--text")).ExitCode -eq 0) "relations item remove failed."

    $selfTest = Run-Engine @("serve", "--self-test", $svProfile)
    Assert-True ($selfTest.ExitCode -eq 0) ("serve --self-test: the profile copy differs from the profile read from the file: " + $selfTest.Output)
    Assert-Match $selfTest.Output "same bytes: True, separate graph: True" "serve --self-test did not report its checks."
    # The load correction the server keeps: the same state as one that runs; a changed file or DB list runs it again.
    Assert-Match $selfTest.Output "corrected: True, kept: True, same state as a run: True, run again after a file change: True, after a DB change: True" ("serve --self-test: the kept load correction differs: " + $selfTest.Output)
    $legacyFixture = Join-Path $PSScriptRoot "..\fixtures\legacy-ids\legacy-ids.pr0file"
    $legacyTest = Run-Engine @("serve", "--self-test", $legacyFixture)
    Assert-True ($legacyTest.ExitCode -eq 0) ("serve --self-test on an older profile failed: " + $legacyTest.Output)

    # The same commands in processes and in one server, each side on its own copy and data folder.
    $svCommands = @(
        @("relations", "list"),
        @("relations", "show", "Relation 2"),
        @("relations", "add", "New one", "--axis", "--node", "DCS:server_axis:F-16C_50"),
        @("relations", "show", "New one"),
        @("relations", "rename", "New one", "Renamed one"),
        @("relations", "group", "add", "Renamed one", "Smoke"),
        @("binds", "set", "Renamed one", "--device", $svStick, "--input", "JOY_X"),
        @("relations", "item", "remove", "Relation 5", "--node", "DCS:server_5:F-16C_50"),
        @("relations", "show", "does not exist"),
        @("binds", "list"),
        @("relations", "add", "After removal", "--button", "--node", "DCS:server_after:A-10C_2"),
        @("profile", "save"),
        @("relations", "list")
    )
    $svSides = @{}
    foreach ($side in @("process", "server")) {
        $folder = Join-Path $svDir $side
        New-Item -Path $folder -ItemType Directory -Force | Out-Null
        $copy = Join-Path $folder "p.pr0file"
        Copy-Item -LiteralPath $svProfile -Destination $copy -Force
        $svSides[$side] = @{ Folder = $folder; Profile = $copy; Outputs = New-Object System.Collections.Generic.List[string] }
    }
    $env:JOYPRO_DATA_DIR = Join-Path $svSides["process"].Folder "data"
    foreach ($command in $svCommands) {
        $run = Run-Engine (@("--profile", $svSides["process"].Profile, "--status", "false") + $command)
        $svSides["process"].Outputs.Add("exit " + $run.ExitCode)
    }
    $env:JOYPRO_DATA_DIR = Join-Path $svSides["server"].Folder "data"
    $svScript = Join-Path $svDir "script.json"
    $lines = foreach ($command in $svCommands) { ConvertTo-Json -Compress -InputObject ([string[]](@("--profile", $svSides["server"].Profile, "--status", "false") + $command)) }
    [IO.File]::WriteAllLines($svScript, [string[]]$lines)
    $env:JOYPRO_SCRIPT_OUT = Join-Path $svDir "script-out"
    $scripted = Run-Engine @("serve", "--script", $svScript)
    $env:JOYPRO_SCRIPT_OUT = $svPreviousOut
    for ($i = 0; $i -lt $svCommands.Count; $i++) {
        $file = Join-Path $svDir ("script-out\" + ($i + 1).ToString("D4") + ".txt")
        Assert-True (Test-Path $file) ("serve --script wrote no result for command " + ($i + 1))
        $first = (Get-Content -LiteralPath $file -TotalCount 1)
        Assert-True ($first -eq $svSides["process"].Outputs[$i]) ("command '" + ($svCommands[$i] -join " ") + "': " + $svSides["process"].Outputs[$i] + " in a process, " + $first + " in the server.")
    }
    $processBytes = [IO.File]::ReadAllBytes($svSides["process"].Profile)
    $serverBytes = [IO.File]::ReadAllBytes($svSides["server"].Profile)
    Assert-True ([Linq.Enumerable]::SequenceEqual($processBytes, $serverBytes)) ("the profile saved by the server differs from the one saved by processes (" + $processBytes.Length + " / " + $serverBytes.Length + " bytes).")
    # An older profile (its load correction runs): "profile load" corrects it, the change after it gets the kept
    # correction in the server and a new one in each process; the saved profiles must be the same.
    $kcCommands = @(
        @("profile", "load"),
        @("relations", "add", "Kept correction", "--button", "--node", "DCS:server_kept:A-10C_2"),
        @("relations", "list")
    )
    $kcSides = @{}
    foreach ($side in @("process", "server")) {
        $folder = Join-Path $svDir ("kept-" + $side)
        New-Item -Path $folder -ItemType Directory -Force | Out-Null
        $copy = Join-Path $folder "older.pr0file"
        Copy-Item -LiteralPath $legacyFixture -Destination $copy -Force
        $kcSides[$side] = @{ Folder = $folder; Profile = $copy; Outputs = New-Object System.Collections.Generic.List[string] }
    }
    $env:JOYPRO_DATA_DIR = Join-Path $kcSides["process"].Folder "data"
    foreach ($command in $kcCommands) {
        $args1 = if ($command[0] -eq "profile" -and $command[1] -eq "load") { @("--status", "false", "profile", "load", $kcSides["process"].Profile) } else { @("--profile", $kcSides["process"].Profile, "--status", "false") + $command }
        $run = Run-Engine $args1
        $kcSides["process"].Outputs.Add("exit " + $run.ExitCode)
    }
    $env:JOYPRO_DATA_DIR = Join-Path $kcSides["server"].Folder "data"
    $kcScript = Join-Path $svDir "script-kept.json"
    $kcLines = foreach ($command in $kcCommands) {
        $args1 = if ($command[0] -eq "profile" -and $command[1] -eq "load") { @("--status", "false", "profile", "load", $kcSides["server"].Profile) } else { @("--profile", $kcSides["server"].Profile, "--status", "false") + $command }
        ConvertTo-Json -Compress -InputObject ([string[]]$args1)
    }
    [IO.File]::WriteAllLines($kcScript, [string[]]$kcLines)
    $env:JOYPRO_SCRIPT_OUT = Join-Path $svDir "script-kept-out"
    $kcScripted = Run-Engine @("serve", "--script", $kcScript)
    $env:JOYPRO_SCRIPT_OUT = $svPreviousOut
    for ($i = 0; $i -lt $kcCommands.Count; $i++) {
        $file = Join-Path $svDir ("script-kept-out\" + ($i + 1).ToString("D4") + ".txt")
        Assert-True (Test-Path $file) ("serve --script (older profile) wrote no result for command " + ($i + 1))
        $first = (Get-Content -LiteralPath $file -TotalCount 1)
        Assert-True ($first -eq $kcSides["process"].Outputs[$i]) ("older profile, command '" + ($kcCommands[$i] -join " ") + "': " + $kcSides["process"].Outputs[$i] + " in a process, " + $first + " in the server.")
    }
    $kcProcessBytes = [IO.File]::ReadAllBytes($kcSides["process"].Profile)
    $kcServerBytes = [IO.File]::ReadAllBytes($kcSides["server"].Profile)
    Assert-True ([Linq.Enumerable]::SequenceEqual($kcProcessBytes, $kcServerBytes)) ("older profile: the profile saved by the server (kept load correction) differs from the one saved by processes (" + $kcProcessBytes.Length + " / " + $kcServerBytes.Length + " bytes).")

    $listProcess = Run-Engine @("--profile", $svSides["process"].Profile, "relations", "list", "--text")
    $listServer = Get-Content -LiteralPath (Join-Path $svDir ("script-out\" + $svCommands.Count.ToString("D4") + ".txt")) -Raw
    Assert-Match $listServer "Renamed one" "the server's last relations list misses the renamed relation."

    # The protocol: hello, one command, its output and exit code; 'Q' ends the server, so does a closed stdin.
    $env:JOYPRO_DATA_DIR = Join-Path $svDir "data-protocol"
    foreach ($ending in @("quit", "close")) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $script:EngineExe
        $psi.Arguments = "serve"
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.EnvironmentVariables["JOYPRO_SERVER_WARMUP"] = "off"
        $server = [System.Diagnostics.Process]::Start($psi)
        try {
            $out = $server.StandardOutput.BaseStream
            $in = $server.StandardInput.BaseStream
            function Read-SvFrame {
                $header = New-Object byte[] 5
                $read = 0
                while ($read -lt 5) { $n = $out.Read($header, $read, 5 - $read); if ($n -le 0) { return $null }; $read += $n }
                $length = [BitConverter]::ToInt32($header, 1)
                $payload = New-Object byte[] $length
                $read = 0
                while ($read -lt $length) { $n = $out.Read($payload, $read, $length - $read); if ($n -le 0) { return $null }; $read += $n }
                return [pscustomobject]@{ Kind = [char]$header[0]; Text = [Text.Encoding]::UTF8.GetString($payload) }
            }
            $hello = Read-SvFrame
            Assert-True ($null -ne $hello -and $hello.Kind -eq 'H') "the engine server did not say hello."
            $request = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Compress @{ Args = @("--profile", $svProfile, "--status", "false", "relations", "list", "--text"); Env = @{} }))
            $frame = New-Object byte[] (5 + $request.Length)
            $frame[0] = [byte][char]'R'
            [BitConverter]::GetBytes($request.Length).CopyTo($frame, 1)
            $request.CopyTo($frame, 5)
            $in.Write($frame, 0, $frame.Length)
            $in.Flush()
            $stdout = $null
            $exit = $null
            while ($null -eq $exit) {
                $answer = Read-SvFrame
                Assert-True ($null -ne $answer) "the engine server ended before it answered."
                if ($answer.Kind -eq 'O') { $stdout = $answer.Text }
                if ($answer.Kind -eq 'X') { $exit = [int]$answer.Text }
            }
            Assert-True ($exit -eq 0) ("relations list in the server exited with " + $exit)
            Assert-Match $stdout "(?m)^Relation 1 \|" "the server's relations list misses a relation."
            if ($ending -eq "quit") {
                $quit = [byte[]]@([byte][char]'Q', 0, 0, 0, 0)
                $in.Write($quit, 0, 5)
                $in.Flush()
            }
            else {
                $in.Close()
            }
            Assert-True ($server.WaitForExit(10000)) ("the engine server did not end after " + $ending + ".")
        }
        finally {
            if (-not $server.HasExited) { $server.Kill() }
            $server.Dispose()
        }
    }
}
finally {
    $env:JOYPRO_DATA_DIR = $svPreviousData
    $env:JOYPRO_SCRIPT_OUT = $svPreviousOut
}
