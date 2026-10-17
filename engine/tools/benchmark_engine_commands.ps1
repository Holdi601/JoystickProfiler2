param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [int]$Warmup = 1,
    [int]$Iterations = 3,
    [string]$OutputPath = ""
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

function Resolve-EngineExe {
    param(
        [string]$EngineDir,
        [string]$Configuration,
        [string]$Platform
    )

    $candidate = Join-Path $EngineDir ("bin\" + $Platform + "\" + $Configuration + "\engine.exe")
    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).Path
    }

    Write-Host "Building engine ($Configuration|$Platform) for benchmark..."
    dotnet build (Join-Path $EngineDir "engine.csproj") -c $Configuration -p:Platform=$Platform | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dotnet build failed."
    Assert-True (Test-Path $candidate) "engine.exe not found after build: $candidate"
    return (Resolve-Path $candidate).Path
}

function Invoke-EngineTimed {
    param(
        [string]$EngineExe,
        [string[]]$Args
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $nativePrefExists = $false
    $previousNativePreference = $null
    if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
        $nativePrefExists = $true
        $previousNativePreference = $global:PSNativeCommandUseErrorActionPreference
    }
    try {
        $ErrorActionPreference = "Continue"
        if ($nativePrefExists) {
            $global:PSNativeCommandUseErrorActionPreference = $false
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $output = & $EngineExe @Args 2>&1
        $exitCode = $LASTEXITCODE
        $sw.Stop()
    }
    finally {
        if ($nativePrefExists) {
            $global:PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $lines = @()
    if ($null -eq $output) {
        $lines = @()
    }
    elseif ($output -is [System.Array]) {
        $lines = $output | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                if ($_.Exception -and -not [string]::IsNullOrWhiteSpace($_.Exception.Message)) {
                    $_.Exception.Message
                }
                else {
                    $_.ToString()
                }
            }
            else {
                $_.ToString()
            }
        }
    }
    else {
        if ($output -is [System.Management.Automation.ErrorRecord] -and $output.Exception -and -not [string]::IsNullOrWhiteSpace($output.Exception.Message)) {
            $lines = @($output.Exception.Message)
        }
        else {
            $lines = @($output.ToString())
        }
    }

    $bench = @{}
    foreach ($line in $lines) {
        if ($line -match "\[engine-benchmark\]\s+([^=]+)=([0-9]+)\s+ms") {
            $bench[$Matches[1].Trim()] = [int64]$Matches[2]
            continue
        }
        if ($line -match "\[engine-status\]\s+Finished\s+([a-zA-Z0-9\\._-]+)\s+in\s+([0-9]+)\s+ms") {
            $bench[$Matches[1].Trim()] = [int64]$Matches[2]
        }
    }
    if (-not $bench.ContainsKey("run.total")) {
        $bench["run.total"] = [int64][math]::Round($sw.Elapsed.TotalMilliseconds, 0)
    }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        ElapsedMs = [double]$sw.Elapsed.TotalMilliseconds
        Lines = $lines
        Benchmarks = $bench
    }
}

function New-CommandSpec {
    param(
        [string]$Name,
        [string[]]$Args
    )

    return [PSCustomObject]@{
        Name = $Name
        Args = $Args
    }
}

$engineDir = Split-Path -Parent $PSScriptRoot
$engineExe = Resolve-EngineExe -EngineDir $engineDir -Configuration $Configuration -Platform $Platform

$dcsFixture = Join-Path $engineDir "tests/fixtures/dcs-saved-games"
$il2Fixture = Join-Path $engineDir "tests/fixtures/il2-game"
Assert-True (Test-Path $dcsFixture) "Missing DCS fixture folder: $dcsFixture"
Assert-True (Test-Path $il2Fixture) "Missing IL2 fixture folder: $il2Fixture"

$benchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("engine-bench-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $benchRoot | Out-Null

try {
    $dcsImportProfile = Join-Path $benchRoot "dcs_import.pr0file"
    $dcsExportRoot = Join-Path $benchRoot "dcs_export"
    $il2ImportProfile = Join-Path $benchRoot "il2_import.pr0file"
    $il2ExportRoot = Join-Path $benchRoot "il2_export"

    $commands = @(
        (New-CommandSpec -Name "summary" -Args @("--benchmark", "true", "summary")),
        (New-CommandSpec -Name "db summary" -Args @("--benchmark", "true", "db", "summary")),
        (New-CommandSpec -Name "db check" -Args @("--benchmark", "true", "db", "check")),
        (New-CommandSpec -Name "devices inputs" -Args @("--benchmark", "true", "devices", "inputs")),
        (New-CommandSpec -Name "dcs scan" -Args @("--benchmark", "true", "dcs", "scan", $dcsFixture)),
        (New-CommandSpec -Name "dcs import" -Args @("--benchmark", "true", "dcs", "import", $dcsFixture, "--save-profile", $dcsImportProfile)),
        (New-CommandSpec -Name "dcs export" -Args @("--benchmark", "true", "--profile", $dcsImportProfile, "dcs", "export", $dcsExportRoot, "--mode", "merge-overwrite")),
        (New-CommandSpec -Name "il2 scan" -Args @("--benchmark", "true", "il2", "scan", $il2Fixture)),
        (New-CommandSpec -Name "il2 import" -Args @("--benchmark", "true", "il2", "import", $il2Fixture, "--save-profile", $il2ImportProfile)),
        (New-CommandSpec -Name "il2 export" -Args @("--benchmark", "true", "--profile", $il2ImportProfile, "il2", "export", $il2ExportRoot, "--mode", "merge-overwrite")),
        (New-CommandSpec -Name "profile validate" -Args @("--benchmark", "true", "--profile", $dcsImportProfile, "profile", "validate"))
    )

    $results = New-Object System.Collections.Generic.List[object]
    $internalTotals = @{}
    $internalCounts = @{}

    foreach ($spec in $commands) {
        Write-Host ("Benchmarking: " + $spec.Name)

        for ($i = 0; $i -lt $Warmup; $i++) {
            $warm = Invoke-EngineTimed -EngineExe $engineExe -Args $spec.Args
            Assert-True ($warm.ExitCode -eq 0) ("Warmup failed for '" + $spec.Name + "'.")
        }

        $times = New-Object System.Collections.Generic.List[double]
        for ($i = 0; $i -lt $Iterations; $i++) {
            $run = Invoke-EngineTimed -EngineExe $engineExe -Args $spec.Args
            Assert-True ($run.ExitCode -eq 0) ("Benchmark run failed for '" + $spec.Name + "'.")
            [void]$times.Add($run.ElapsedMs)

            foreach ($entry in $run.Benchmarks.GetEnumerator()) {
                if (-not $internalTotals.ContainsKey($entry.Key)) {
                    $internalTotals[$entry.Key] = [double]0
                    $internalCounts[$entry.Key] = 0
                }
                $internalTotals[$entry.Key] += [double]$entry.Value
                $internalCounts[$entry.Key] += 1
            }
        }

        $sorted = $times | Sort-Object
        $avg = ($times | Measure-Object -Average).Average
        $min = $sorted[0]
        $max = $sorted[$sorted.Count - 1]

        [void]$results.Add([PSCustomObject]@{
            Name = $spec.Name
            AverageMs = [math]::Round($avg, 2)
            MinMs = [math]::Round($min, 2)
            MaxMs = [math]::Round($max, 2)
        })
    }

    $ordered = $results | Sort-Object -Property AverageMs -Descending
    Write-Host ""
    Write-Host "Command benchmark (sorted by average ms):"
    $ordered | Format-Table -AutoSize | Out-Host

    $internal = New-Object System.Collections.Generic.List[object]
    foreach ($key in $internalTotals.Keys) {
        $count = [int]$internalCounts[$key]
        if ($count -le 0) {
            continue
        }
        $avg = [double]$internalTotals[$key] / [double]$count
        [void]$internal.Add([PSCustomObject]@{
            Metric = $key
            AverageMs = [math]::Round($avg, 2)
            Samples = $count
        })
    }
    $internalOrdered = $internal | Sort-Object -Property AverageMs -Descending
    Write-Host ""
    Write-Host "Internal timing hotspots (from --benchmark output):"
    $internalOrdered | Format-Table -AutoSize | Out-Host

    $out = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Join-Path $engineDir "docs\ENGINE_BENCHMARK.md"
    }
    else {
        $OutputPath
    }
    $out = [System.IO.Path]::GetFullPath($out)
    $outDir = Split-Path -Parent $out
    if (-not (Test-Path $outDir)) {
        New-Item -Path $outDir -ItemType Directory -Force | Out-Null
    }

    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add("# Engine Command Benchmark")
    [void]$lines.Add("")
    [void]$lines.Add("Generated: " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"))
    [void]$lines.Add("Configuration: " + $Configuration + " | Platform: " + $Platform)
    [void]$lines.Add("Warmup runs: " + $Warmup + " | Measured runs: " + $Iterations)
    [void]$lines.Add("")
    [void]$lines.Add("## Command Runtimes (ms)")
    [void]$lines.Add("")
    [void]$lines.Add("| Command | Avg | Min | Max |")
    [void]$lines.Add("|---|---:|---:|---:|")
    foreach ($row in $ordered) {
        [void]$lines.Add("| " + $row.Name + " | " + $row.AverageMs + " | " + $row.MinMs + " | " + $row.MaxMs + " |")
    }

    [void]$lines.Add("")
    [void]$lines.Add("## Internal Hotspots (ms)")
    [void]$lines.Add("")
    [void]$lines.Add("| Metric | Avg | Samples |")
    [void]$lines.Add("|---|---:|---:|")
    foreach ($row in $internalOrdered) {
        [void]$lines.Add("| " + $row.Metric + " | " + $row.AverageMs + " | " + $row.Samples + " |")
    }

    Set-Content -Path $out -Value ($lines -join "`r`n") -Encoding UTF8
    Write-Host ""
    Write-Host ("Wrote benchmark report: " + $out)
}
finally {
    if (Test-Path $benchRoot) {
        Remove-Item -Path $benchRoot -Recurse -Force
    }
}
