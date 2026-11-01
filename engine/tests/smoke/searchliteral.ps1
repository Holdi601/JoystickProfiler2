# Titles with every typed word in the Add actions search (docs/relation-builder.md "Ranking"; testers on 2.5.0): a title
# that has all the words lists first, brackets and punctuation of the query do not matter, and a word that is generic on
# its own ("master", "reset", "inc") still finds every title with it. The meaning of a DCS command with a trailing tag
# ("TACAN Channel (xx0x) - [Inc]", "[Toggle]") comes from that tag, not from the dash before it.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir); needs the development game lists.

$slDir = Join-Path $tempDir "searchliteral"
New-Item -Path $slDir -ItemType Directory -Force | Out-Null
$slPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $slDir "data"

function Invoke-SlJson([string[]]$Arguments) {
    $out = Join-Path $slDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    $slF4 = "F-4E-45MC|F-4E-45MC_WSO"
    # The tester's queries, as the Relations page asks (best 12 rows per game): the [Inc] actions of both seats first.
    foreach ($q in @("tacan channel (xx0x) - inc", "tacan channel (xx0x) - [inc]", "TACAN Channel (xx0x) - [Inc]")) {
        $r = Invoke-SlJson @("relations", "search", $q, "--plane", $slF4, "--limit", "200", "--per-game", "12")
        $first = @($r.Results)
        Assert-True ($first.Count -ge 2 -and $first[0].Title -eq "TACAN Channel (xx0x) - [Inc]" -and $first[1].Title -eq "TACAN Channel (xx0x) - [Inc]") ("search '" + $q + "' does not list TACAN Channel (xx0x) - [Inc] of both seats first (first: " + (@($first | Select-Object -First 3 | ForEach-Object { $_.Title }) -join " / ") + ").")
        Assert-True ($first[0].Literal) ("search '" + $q + "': the [Inc] row is not marked as a title with every word.")
    }
    # [Inc] and [Next] mean increase / next, [Dec] decrease (the dash before the tag is no "minus").
    $slAll = Invoke-SlJson @("relations", "search", "tacan channel (xx0x)", "--plane", $slF4, "--game", "DCS", "--limit", "400")
    foreach ($tag in @("Inc", "Dec", "Next")) {
        $rows = @($slAll.Results | Where-Object { $_.Title -eq ("TACAN Channel (xx0x) - [" + $tag + "]") })
        Assert-True ($rows.Count -eq 2) ("search 'tacan channel (xx0x)' does not list [" + $tag + "] of both seats (" + $rows.Count + ").")
        foreach ($row in $rows) {
            $dirs = @($row.Functions | ForEach-Object { if ($_ -match ":(.+)$") { $Matches[1] } })
            if ($tag -eq "Dec") { Assert-True ($dirs -contains "decrease") ("[Dec] is not read as decrease (" + ($row.Functions -join ", ") + ").") }
            else { Assert-True (-not ($dirs -contains "decrease")) ("[" + $tag + "] is read as decrease (" + ($row.Functions -join ", ") + ").") }
        }
    }
    # A lone "-" at the end of a query is a separator while typing, not "decrease": [Dec], [Inc] and [Next] are all among
    # the best rows, and neither decrease nor increase is read.
    $slDash = Invoke-SlJson @("relations", "search", "tacan channel (xx0x) -", "--plane", $slF4, "--limit", "200", "--per-game", "12")
    foreach ($tag in @("Dec", "Inc", "Next")) {
        Assert-True (@($slDash.Results | Where-Object { $_.Title -eq ("TACAN Channel (xx0x) - [" + $tag + "]") }).Count -eq 2) ("search 'tacan channel (xx0x) -' does not list [" + $tag + "] of both seats among the best rows.")
    }
    Assert-True (@($slDash.Resolved | Where-Object { $_.Dir -eq "decrease" -or $_.Dir -eq "increase" }).Count -eq 0) ("search 'tacan channel (xx0x) -' reads decrease / increase: " + (@($slDash.Resolved | ForEach-Object { $_.Name + " " + $_.Dir }) -join ", "))

    # The meaning search still works: "increase tacan channel" finds the Inc actions.
    $slMeaning = Invoke-SlJson @("relations", "search", "increase tacan channel", "--plane", $slF4, "--limit", "200", "--per-game", "12")
    Assert-True (@($slMeaning.Results | Where-Object { $_.Title -like "TACAN Channel*[[]Inc[]]" }).Count -ge 2) "search 'increase tacan channel' does not find the TACAN Inc actions."

    # F-14 "master": every title with the word (35 in the development list), all of them before anything else.
    $slMaster = Invoke-SlJson @("relations", "search", "master", "--plane", "F-14", "--game", "DCS", "--limit", "400")
    $slMasterRows = @($slMaster.Results)
    $slWithWord = @($slMasterRows | Where-Object { $_.Title -match "(?i)\bmaster" })
    Assert-True ($slWithWord.Count -ge 34) ("search 'master' (F-14) lists only " + $slWithWord.Count + " titles with the word.")
    $slLastLiteral = -1; $slFirstOther = [int]::MaxValue
    for ($i = 0; $i -lt $slMasterRows.Count; $i++) {
        if ($slMasterRows[$i].Title -match "(?i)\bmaster") { $slLastLiteral = $i } else { $slFirstOther = [Math]::Min($slFirstOther, $i) }
    }
    Assert-True ($slLastLiteral -lt $slFirstOther) "search 'master' (F-14): a title without the word comes before a title with it."
    Assert-True (@($slMasterRows | Where-Object { $_.Title -eq "MASTER RESET" }).Count -eq 1) "search 'master' (F-14) does not list MASTER RESET (all capitals)."
    $slMaster12 = Invoke-SlJson @("relations", "search", "master", "--plane", "F-14", "--limit", "200", "--per-game", "12")
    Assert-True (@($slMaster12.Results | Where-Object { $_.Title -notmatch "(?i)\bmaster" }).Count -eq 0) "search 'master' (F-14, best 12): a row without the word is among the best rows."
    # The whole title typed comes first; a three-word title by two of its words is found.
    $slReset = Invoke-SlJson @("relations", "search", "master reset", "--plane", "F-14", "--limit", "200", "--per-game", "12")
    Assert-True (@($slReset.Results)[0].Title -eq "MASTER RESET") ("search 'master reset' (F-14) does not list MASTER RESET first (first: " + @($slReset.Results)[0].Title + ").")
    $slCaution = Invoke-SlJson @("relations", "search", "master caution", "--plane", "F-14", "--limit", "200", "--per-game", "12")
    Assert-True (@($slCaution.Results | Where-Object { $_.Title -eq "Master Caution Reset" }).Count -eq 1) "search 'master caution' (F-14) does not find Master Caution Reset."
    $slResetOnly = Invoke-SlJson @("relations", "search", "reset", "--plane", "F-14", "--game", "DCS", "--limit", "400")
    Assert-True (@($slResetOnly.Results | Where-Object { $_.Title -eq "MASTER RESET" }).Count -eq 1) "search 'reset' (F-14) does not list MASTER RESET."
}
finally {
    $env:JOYPRO_DATA_DIR = $slPreviousData
}
