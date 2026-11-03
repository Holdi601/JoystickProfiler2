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

    # Every word covered (2.6.1, the tester's report on 2.6.0): "master reset" in the F-14 is exactly the two titles with
    # both words, understood as the master caution reset only (not every "master" function by its first word).
    $slBoth = Invoke-SlJson @("relations", "search", "master reset", "--plane", "F-14", "--game", "DCS", "--limit", "400")
    $slBothTitles = @($slBoth.Results | ForEach-Object { $_.Title })
    Assert-True ($slBothTitles.Count -eq 2 -and $slBothTitles[0] -eq "MASTER RESET" -and $slBothTitles[1] -eq "Master Caution Reset") ("search 'master reset' (F-14) does not list exactly MASTER RESET, Master Caution Reset: " + ($slBothTitles -join " / "))
    Assert-True (@($slBoth.Results | Where-Object { -not $_.Literal }).Count -eq 0) "search 'master reset' (F-14): a row without both words is listed."
    $slBothFunctions = @($slBoth.Resolved | ForEach-Object { $_.Name })
    Assert-True ($slBothFunctions.Count -eq 1 -and $slBothFunctions[0] -eq "Master caution reset") ("search 'master reset' (F-14) is understood as " + ($slBothFunctions -join ", ") + ", not only as Master caution reset.")
    # A single word is never cut: all 35 F-14 titles with "master".
    Assert-True ($slMasterRows.Count -ge 35 -and $slWithWord.Count -ge 35) ("search 'master' (F-14) lists " + $slMasterRows.Count + " rows, " + $slWithWord.Count + " with the word (35 expected).")
    # Must keep: a single word, a direction, a meaning of the whole phrase, a step query, a sentence and German.
    $slPinky = Invoke-SlJson @("relations", "search", "pinky", "--limit", "200", "--per-game", "6")
    Assert-True (@($slPinky.Results | Where-Object { $_.Title -match "(?i)\bpinky" }).Count -ge 3) "search 'pinky' finds fewer than three pinky switches."
    foreach ($case in @(
            @{ Q = "gear up"; F = "gear"; Dir = "up" },
            @{ Q = "recenter view"; F = "view_center"; Dir = "" },
            @{ Q = "increase tacan channel"; F = "nav_radio"; Dir = "increase" },
            @{ Q = "drop flares"; F = "flare"; Dir = "" },
            @{ Q = "make the plane go faster"; F = "throttle"; Dir = "increase" },
            @{ Q = "fahrwerk ausfahren"; F = "gear"; Dir = "" })) {
        $r = Invoke-SlJson @("relations", "search", $case.Q, "--limit", "200", "--per-game", "6")
        $top = @($r.Resolved | Where-Object { -not $_.Related } | Select-Object -First 1)
        Assert-True ($top.Count -eq 1 -and $top[0].Id -eq $case.F) ("search '" + $case.Q + "' is no longer understood as " + $case.F + " first (" + (@($r.Resolved | ForEach-Object { $_.Id }) -join ", ") + ").")
        if ($case.Dir) { Assert-True ($top[0].Dir -eq $case.Dir) ("search '" + $case.Q + "' reads the direction " + $top[0].Dir + ", not " + $case.Dir + ".") }
        $own = @($r.Results | Where-Object { @($_.Functions | Where-Object { $_ -like ($case.F + "*") }).Count -gt 0 })
        Assert-True ($own.Count -ge 5) ("search '" + $case.Q + "' lists only " + $own.Count + " rows of " + $case.F + ".")
    }
    $slInc = Invoke-SlJson @("relations", "search", "tacan channel (xx0x) - inc", "--plane", $slF4, "--game", "DCS", "--limit", "400")
    Assert-True (@($slInc.Results | Where-Object { $_.Title -eq "TACAN Channel (xx0x) - [Inc]" }).Count -eq 2) "search 'tacan channel (xx0x) - inc' does not list both seats' [Inc]."

    # Same function and Related (2.6.2, the testers' "brakes" report on 2.6.1; docs/relation-builder.md "Same function and
    # Related"): "brakes" with Axes in DCS for the tester's aircraft (F-4E pilot and WSO, Spitfire). "brakes" is "brake":
    # every Wheel Brake axis has every word (section "name"); the switches found only by their category "Gears, Brakes,
    # Hook" are "related", by "category", with the word as typed, and no "Title match".
    $slBrakes = Invoke-SlJson @("relations", "search", "brakes", "--axis", "--game", "DCS", "--plane", ($slF4 + "|SpitfireLFMkIX"), "--limit", "400")
    foreach ($title in @("Wheel Brake", "Wheel Brake Left", "Wheel Brake Right", "Wheel Brake (Both)", "Wheel Brake (Left)", "Wheel Brake (Right)", "Wheel Brakes (analog)")) {
        $row = @($slBrakes.Results | Where-Object { $_.Title -eq $title })
        Assert-True ($row.Count -eq 1 -and $row[0].Literal -and $row[0].Section -eq "name" -and $row[0].Function -eq "wheel_brakes") ("search 'brakes' (F-4E, Spitfire): '" + $title + "' is no name match of the wheel brakes (" + (@($row | ForEach-Object { $_.Section + " " + $_.Function }) -join ", ") + ").")
    }
    $slBrakeRelated = @($slBrakes.Results | Where-Object { $_.Section -eq "related" })
    Assert-True ($slBrakeRelated.Count -eq 7) ("search 'brakes' (F-4E, Spitfire): " + $slBrakeRelated.Count + " related rows, 7 expected: " + (@($slBrakeRelated | ForEach-Object { $_.Title }) -join " / "))
    foreach ($row in $slBrakeRelated) {
        Assert-True ($row.RelatedBy -eq "category" -and @($row.CategoryWords) -contains "brakes" -and $row.Why -like "Category match*") ("search 'brakes': related row '" + $row.Title + "' is not marked as found by its category (" + $row.RelatedBy + ", " + $row.Why + ").")
    }
    Assert-True (@($slBrakes.Results | Where-Object { $_.Title -match "(?i)\bbrake" -and $_.Section -ne "name" }).Count -eq 0) "search 'brakes': a title with 'Brake' is not a name match."
    Assert-True (@($slBrakes.Results | Where-Object { $_.Section -eq "name" -and $_.Function -eq "wheel_brakes" -and $_.Understood -eq "Wheel brakes" -and $_.UnderstoodBy -eq "name" }).Count -ge 6) "search 'brakes': the wheel brakes rows do not say they were understood as Wheel brakes by name."
    # A synonym: "thrust" finds the throttles as the same function, by a synonym of Throttle.
    $slThrust = Invoke-SlJson @("relations", "search", "thrust", "--axis", "--game", "DCS", "--plane", ($slF4 + "|SpitfireLFMkIX"), "--limit", "400")
    Assert-True (@($slThrust.Results).Count -ge 5 -and @($slThrust.Results | Where-Object { $_.Section -ne "function" -or $_.UnderstoodBy -ne "synonym" -or $_.Understood -ne "Throttle" }).Count -eq 0) ("search 'thrust' (F-4E, Spitfire): the throttles are not 'function' rows found by a synonym of Throttle (" + (@($slThrust.Results | ForEach-Object { $_.Title + ":" + $_.Section + "/" + $_.UnderstoodBy }) -join ", ") + ").")
    Assert-True (@($slThrust.Results | Where-Object { $_.Title -eq "Throttle Left" -and $_.ActionFunction -eq "Throttle (left) axis" -and $_.QueryTerm -eq "thrust" }).Count -eq 1) "search 'thrust': Throttle Left does not name its function and the synonym that found it."
    # A description stays "Same function" (search by meaning keeps working with the sections).
    $slFaster = Invoke-SlJson @("relations", "search", "make the plane go faster", "--limit", "200", "--per-game", "6")
    Assert-True (@($slFaster.Results | Where-Object { $_.Section -eq "function" -and $_.UnderstoodBy -eq "description" -and $_.Function -eq "throttle" }).Count -ge 5) "search 'make the plane go faster': the throttles are no 'function' rows found by the description."
    # Plurals with "es": "switches" has every word of "MASTER Switch" (before 2.6.2: "switche" matched no "Switch").
    $slSwitches = Invoke-SlJson @("relations", "search", "master switches", "--plane", "F-16C_50", "--game", "DCS", "--limit", "400")
    Assert-True (@($slSwitches.Results | Where-Object { $_.Literal -and $_.Title -like "MASTER Switch*" }).Count -ge 3) "search 'master switches' (F-16C) does not name-match the MASTER Switch titles."
    # The whole catalog (Browse) has no sections: it lists, it does not search.
    $slBrowse = Invoke-SlJson @("relations", "browse", "--game", "DCS", "--plane", "F-4E-45MC", "--axis")
    Assert-True (@($slBrowse.Results | Where-Object { $_.Section }).Count -eq 0) "relations browse marks rows with a search section."
}
finally {
    $env:JOYPRO_DATA_DIR = $slPreviousData
}
