<#
.SYNOPSIS
    Checks the function knowledge map and "Find actions" against the REAL game lists on this PC (read-only).

.DESCRIPTION
    Not part of the engine smoke test (that one has no real game data). Runs the engine with the game lists of this PC
    (or of -DataDir) and reports:
      1. Coverage: actions mapped to a function, per game (engine relations functions --coverage).
      2. Search quality on tools\relations\search-eval.tsv (descriptive, synonym, game-term, direction and typo queries):
         per query class the function hit rate (an expected function among the 3 functions the query was read as),
         precision@10 (of the 10 best rows, the share whose action is an expected function, with the direction when the
         query names one) and game recall (of the games that have an expected function, the share with such a row in
         the first -Limit rows). Queries with a fourth column also check that each named game's first row is its
         canonical axis ("pitch": MSFS's KEY_AXIS_ELEVATOR_SET, not the balloon burner); entries "Game=id" or
         "Game~title regex", separated by ';'.
      3. Mapping precision on the hand-labelled sample tools\relations\function-labels.tsv: the share of labelled actions
         whose first function (primary) or any function (weight >= 0.5) is the label.
    Nothing is written to the game lists or the profile; the engine only reads (and keeps its map cache in <data>\Cache).

.EXAMPLE
    powershell -File tools\relations\check-function-map.ps1
    powershell -File tools\relations\check-function-map.ps1 -DataDir C:\Temp\copy-of-JoyPro -Details
#>
param(
    [string]$Engine = "",
    [string]$DataDir = "",
    [int]$Limit = 80,
    [switch]$Details
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $here)
if ($Engine.Length -eq 0) { $Engine = Join-Path $repo "engine\bin\x64\Debug\engine.exe" }
if (-not (Test-Path $Engine)) { throw "engine.exe not found: $Engine (build the engine first)." }
if ($DataDir.Length -gt 0) { $env:JOYPRO_DATA_DIR = $DataDir }
$work = Join-Path ([IO.Path]::GetTempPath()) ("joypro-fmap-check-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-EngineJson([string[]]$Arguments) {
    $out = Join-Path $work ([Guid]::NewGuid().ToString("N") + ".json")
    & $Engine (@("--out", $out) + $Arguments) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "engine $($Arguments -join ' ') failed." }
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

function Test-Expected($functions, $expected, [bool]$axis = $false) {
    # functions: "id" / "id:dir" strings of an action; expected: "id" / "id:dir" entries. An axis without a direction fits a direction.
    foreach ($e in $expected) {
        $parts = $e.Split(':')
        foreach ($f in @($functions)) {
            $fp = ([string]$f).Split(':')
            if ($fp[0] -ne $parts[0]) { continue }
            if ($parts.Count -lt 2 -or ($fp.Count -ge 2 -and $fp[1] -eq $parts[1]) -or ($axis -and $fp.Count -lt 2)) { return $true }
        }
    }
    return $false
}

try {
    # 1. Coverage
    $functions = Invoke-EngineJson @("relations", "functions", "--coverage")
    Write-Host ("Function map: {0} functions, {1} names and synonyms, {2} game terms; {3} of {4} actions mapped ({5:P1})." -f `
        $functions.Totals.Functions, $functions.Totals.Synonyms, $functions.Totals.Terms, $functions.Totals.Mapped, $functions.Totals.Actions,
        ($functions.Totals.Mapped / [Math]::Max(1, $functions.Totals.Actions)))
    Write-Host ""
    Write-Host "| Game | Actions | Mapped | % | Catalog | Rules | Functions |"
    Write-Host "|---|---:|---:|---:|---:|---:|---:|"
    foreach ($c in $functions.Coverage) {
        Write-Host ("| {0} | {1} | {2} | {3:0.0} | {4} | {5} | {6} |" -f $c.Game, $c.Actions, $c.Mapped, $c.Percent, $c.Catalog, $c.Rules, $c.Functions)
    }
    $gamesOf = @{}
    foreach ($f in $functions.Functions) {
        $set = New-Object System.Collections.Generic.HashSet[string]
        if ($null -ne $f.Games) { foreach ($p in $f.Games.PSObject.Properties) { [void]$set.Add($p.Name) } }
        $gamesOf[$f.Id] = $set
    }

    # 2. Search quality
    $evalFile = Join-Path $here "search-eval.tsv"
    $queries = @(Get-Content -Encoding UTF8 $evalFile | Where-Object { $_.Trim().Length -gt 0 -and -not $_.TrimStart().StartsWith("#") } | ForEach-Object {
        $p = $_.Split("`t"); [pscustomobject]@{ Class = $p[0]; Query = $p[1]; Expected = @($p[2].Split('|') | Where-Object { $_.Length -gt 0 });
            First = @($(if ($p.Count -gt 3) { $p[3].Split(';') | Where-Object { $_.Trim().Length -gt 0 } } else { @() })) } })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $results = @(Invoke-EngineJson @("relations", "search", "--batch", $evalFile, "--limit", "$Limit"))
    # Windows PowerShell's ConvertFrom-Json hands a JSON array over as one object.
    if ($results.Count -eq 1 -and $results[0] -is [array]) { $results = @($results[0]) }
    $watch.Stop()
    if ($results.Count -ne $queries.Count) { throw "batch returned $($results.Count) results for $($queries.Count) queries." }
    $rows = @()
    $firstChecks = 0; $firstAbsent = 0; $firstMisses = @()
    for ($i = 0; $i -lt $queries.Count; $i++) {
        $q = $queries[$i]; $r = $results[$i]
        # The game's first row must be its canonical axis ("MSFS2024=KEY_AXIS_ELEVATOR_SET" by id, "DCS~^Throttle$" by title).
        foreach ($f in $q.First) {
            $m = [regex]::Match($f.Trim(), '^([^=~]+)([=~])(.+)$')
            if (-not $m.Success) { throw "search-eval.tsv: bad first-row expectation '$f' for '$($q.Query)'." }
            $game = $m.Groups[1].Value; $want = $m.Groups[3].Value
            $top = @($r.Results | Where-Object { $_.Game -eq $game } | Select-Object -First 1)
            if ($top.Count -eq 0) { $firstAbsent++; continue }
            $firstChecks++
            $ok = if ($m.Groups[2].Value -eq '=') { $top[0].Id -eq $want } else { $top[0].Title -match $want }
            if (-not $ok) { $firstMisses += ("  '{0}' in {1}: first row {2} '{3}', expected {4}" -f $q.Query, $game, $top[0].Id, $top[0].Title, $want) }
        }
        $read = @($r.Resolved | Where-Object { -not $_.Related } | Select-Object -First 3 | ForEach-Object { if ($_.Dir) { $_.Id + ":" + $_.Dir } else { $_.Id } })
        $hit = Test-Expected $read $q.Expected
        $top = @($r.Results | Select-Object -First 10)
        $good = @($top | Where-Object { Test-Expected $_.Functions $q.Expected $_.IsAxis }).Count
        $precision = if ($top.Count -gt 0) { $good / $top.Count } else { 0 }
        $wantGames = New-Object System.Collections.Generic.HashSet[string]
        foreach ($e in $q.Expected) { $id = $e.Split(':')[0]; if ($gamesOf.ContainsKey($id)) { $wantGames.UnionWith($gamesOf[$id]) } }
        $gotGames = New-Object System.Collections.Generic.HashSet[string]
        foreach ($h in $r.Results) { if (Test-Expected $h.Functions $q.Expected $h.IsAxis) { [void]$gotGames.Add($h.Game) } }
        $recall = if ($wantGames.Count -gt 0) { [double]@($gotGames | Where-Object { $wantGames.Contains($_) }).Count / $wantGames.Count } else { 0 }
        $rows += [pscustomobject]@{ Class = $q.Class; Query = $q.Query; Expected = ($q.Expected -join "|"); Read = ($read -join ", "); Hit = $hit;
            P10 = $precision; Recall = $recall; DidYouMean = $r.DidYouMean; Ms = $r.ElapsedMilliseconds }
    }
    Write-Host ""
    Write-Host ("Search: {0} queries in {1:0.0} s (one engine run; per query {2:0} ms on average, map load {3} ms once)." -f $queries.Count, $watch.Elapsed.TotalSeconds,
        (($rows | Measure-Object Ms -Average).Average), $results[0].LoadMilliseconds)
    Write-Host ""
    Write-Host "| Query class | Queries | Function read right | Precision@10 | Game recall@$Limit |"
    Write-Host "|---|---:|---:|---:|---:|"
    foreach ($g in ($rows | Group-Object Class)) {
        $n = $g.Count
        Write-Host ("| {0} | {1} | {2:P0} | {3:P0} | {4:P0} |" -f $g.Name, $n, (@($g.Group | Where-Object { $_.Hit }).Count / $n),
            (($g.Group | Measure-Object P10 -Average).Average), (($g.Group | Measure-Object Recall -Average).Average))
    }
    $all = $rows.Count
    Write-Host ("| all | {0} | {1:P0} | {2:P0} | {3:P0} |" -f $all, (@($rows | Where-Object { $_.Hit }).Count / $all), (($rows | Measure-Object P10 -Average).Average), (($rows | Measure-Object Recall -Average).Average))
    if ($firstChecks + $firstAbsent -gt 0) {
        Write-Host ""
        Write-Host ("Canonical axis first in its game: {0} of {1}{2}." -f ($firstChecks - $firstMisses.Count), $firstChecks,
            $(if ($firstAbsent -gt 0) { " ($firstAbsent expectation(s) for games without a list on this PC skipped)" } else { "" }))
        $firstMisses | ForEach-Object { Write-Host $_ }
    }
    $weak = @($rows | Where-Object { -not $_.Hit -or $_.P10 -lt 0.5 })
    if ($weak.Count -gt 0 -or $Details) {
        Write-Host ""
        Write-Host ("Weak queries ({0}):" -f $weak.Count)
        foreach ($w in ($(if ($Details) { $rows } else { $weak }))) {
            Write-Host ("  [{0}] '{1}' expected {2}; read as {3}; P@10 {4:P0}; recall {5:P0}{6}" -f $w.Class, $w.Query, $w.Expected, $(if ($w.Read) { $w.Read } else { "(nothing)" }), $w.P10, $w.Recall,
                $(if ($w.DidYouMean) { "; did you mean '" + $w.DidYouMean + "'" } else { "" }))
        }
    }

    # 3. Mapping precision on the hand-labelled sample
    $labelFile = Join-Path $here "function-labels.tsv"
    if (Test-Path $labelFile) {
        $dump = Join-Path $work "dump.tsv"
        & $Engine relations functions --dump $dump | Out-Null
        $mapped = @{}
        foreach ($line in [IO.File]::ReadAllLines($dump)) {
            if ($line.StartsWith("#")) { continue }
            $p = $line.Split("`t")
            $mapped[$p[0] + "|" + $p[1] + "|" + $p[2]] = $p[7]
        }
        # Lines are grouped into sets by "# set <name>: ..." comment lines.
        $set = "all"; $stats = [ordered]@{}; $misses = @()
        foreach ($line in [IO.File]::ReadAllLines($labelFile, [Text.Encoding]::UTF8)) {
            if ($line -match '^#\s*set\s+([^:]+):') { $set = $Matches[1].Trim(); continue }
            if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith("#")) { continue }
            if (-not $stats.Contains($set)) { $stats[$set] = [pscustomobject]@{ Labels = 0; Known = 0; Primary = 0; Any = 0 } }
            $s = $stats[$set]; $s.Labels++
            $p = $line.Split("`t")
            $key = $p[0] + "|" + $p[1] + "|" + $p[2]
            if (-not $mapped.ContainsKey($key)) { continue }
            $s.Known++
            $expected = @($p[3].Split('|'))
            $fns = @($mapped[$key].Split(';') | Where-Object { $_.Length -gt 0 } | ForEach-Object {
                $parts = $_.Split('@'); $w = [double]::Parse($parts[1].Split('/')[0], [Globalization.CultureInfo]::InvariantCulture)
                [pscustomobject]@{ Fn = $parts[0]; W = $w } })
            $none = $expected -contains "-"
            $first = @($fns | Select-Object -First 1 | ForEach-Object { $_.Fn })
            $strong = @($fns | Where-Object { $_.W -ge 0.5 } | ForEach-Object { $_.Fn })
            $okPrimary = if ($none) { $fns.Count -eq 0 } else { Test-Expected $first $expected }
            $okAny = if ($none) { $strong.Count -eq 0 } else { Test-Expected $strong $expected }
            if ($okPrimary) { $s.Primary++ }
            if ($okAny) { $s.Any++ }
            if (-not $okPrimary) { $misses += ("  [{0}] {1} | {2} -> {3} (label {4})" -f $set, $p[0], $p[4], $mapped[$key], $p[3]) }
        }
        Write-Host ""
        Write-Host "| Labelled set | Actions | Found here | Primary function right | Any function right |"
        Write-Host "|---|---:|---:|---:|---:|"
        foreach ($name in $stats.Keys) {
            $s = $stats[$name]
            Write-Host ("| {0} | {1} | {2} | {3:P0} | {4:P0} |" -f $name, $s.Labels, $s.Known, ($s.Primary / [Math]::Max(1, $s.Known)), ($s.Any / [Math]::Max(1, $s.Known)))
        }
        if ($misses.Count -gt 0) { Write-Host "Primary function differs from the label:"; $misses | ForEach-Object { Write-Host $_ } }
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
