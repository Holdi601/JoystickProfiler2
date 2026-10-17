# The function knowledge map (DB\Functions\function-map.json, engine\EngineFunctionMap.cs) on fixtures: a few actions
# of DCS (two catalog aircraft and an aircraft the catalog does not know), Star Citizen, War Thunder, Elite and Tiny
# Combat Arena, MSFS 2024 and FSX (engine\tests\fixtures\function-map\DB, read through JOYPRO_ACTION_INDEX_DIR instead of
# the game lists). Checks relations functions (and the map cache), a query table for relations search / resolve (names,
# synonyms, game terms, directions, descriptions, a typo), the canonical flight axis first in its game, relations
# suggest from the map and Auto Group's map rows.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs no real game lists.

$fmDir = Join-Path $tempDir "functionmap"
New-Item -Path $fmDir -ItemType Directory -Force | Out-Null
$fmPreviousData = $env:JOYPRO_DATA_DIR
$fmPreviousIndex = $env:JOYPRO_ACTION_INDEX_DIR
$env:JOYPRO_DATA_DIR = Join-Path $fmDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = Join-Path $engineDir "tests\fixtures\function-map\DB"

function Invoke-FmJson([string[]]$Arguments) {
    $out = Join-Path $fmDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    # The map and its coverage of the fixture lists; the second run reads the cache.
    $fmList = Invoke-FmJson @("relations", "functions", "--coverage")
    Assert-True ($fmList.Totals.Functions -ge 150) "the function map has fewer than 150 functions."
    Assert-True ($fmList.Totals.Synonyms -ge 2000) "the function map has too few synonyms."
    Assert-True (-not $fmList.FromCache) "the first relations functions run cannot come from the cache."
    $fmThrottle = @($fmList.Functions | Where-Object { $_.Id -eq "throttle" })[0]
    Assert-True ($null -ne $fmThrottle -and @($fmThrottle.Synonyms) -contains "collective" -and @($fmThrottle.Synonyms) -contains "thrust") "relations functions lacks the throttle synonyms."
    foreach ($game in @("DCS", "StarCitizen", "WarThunder", "EliteDangerous", "TinyCombatArena")) {
        $c = @($fmList.Coverage | Where-Object { $_.Game -eq $game })[0]
        Assert-True ($null -ne $c -and $c.Mapped -ge 4) ("the function map mapped too few fixture actions of " + $game + ".")
    }
    $fmCached = Invoke-FmJson @("relations", "functions")
    Assert-True ($fmCached.FromCache) "the second relations functions run did not read the map cache."
    $fmFiltered = Invoke-FmJson @("relations", "functions", "--query", "flare")
    Assert-True (@($fmFiltered.Functions | Where-Object { $_.Id -eq "flare" }).Count -eq 1) "relations functions --query flare did not list the flare function."

    # Query table: query -> expected function (read as, not only related), expected hits (game:id), direction.
    $fmTable = @(
        @{ Q = "throttle"; F = "throttle"; Hits = @("WarThunder:throttle", "EliteDangerous:ThrottleAxis", "StarCitizen:spaceship_movement|v_strafe_longitudinal", "DCS:a2087cdnil", "DCS:a9901cdnil", "TinyCombatArena:ThrottleAxis") },
        @{ Q = "thrust"; F = "throttle"; Hits = @("DCS:a2004cdnil", "WarThunder:throttle") },
        @{ Q = "collective"; F = "throttle"; Hits = @("DCS:a2087cdnil", "WarThunder:helicopter_collective"); First = @("DCS:a2087cdnil", "WarThunder:helicopter_collective") },
        @{ Q = "gear"; F = "gear"; Hits = @("WarThunder:ID_GEAR", "DCS:d430pnilunilcdnilvdnilvpnilvunil", "DCS:d9902pnilunilcdnilvdnilvpnilvunil", "EliteDangerous:LandingGearToggle") },
        @{ Q = "gear up"; F = "gear"; Dir = "up"; Hits = @("DCS:d430pnilunilcdnilvdnilvpnilvunil", "DCS:d9902pnilunilcdnilvdnilvpnilvunil"); Not = @("DCS:d431pnilunilcdnilvdnilvpnilvunil", "DCS:d9903pnilunilcdnilvdnilvpnilvunil") },
        @{ Q = "flare"; F = "flare"; Hits = @("TinyCombatArena:Flares", "DCS:d3006pnilu3006cd50vd1vpnilvu0", "DCS:d9904pnilunilcdnilvdnilvpnilvunil", "DCS:d3014pnilu3014cd16vd1vpnilvu0") },
        @{ Q = "trim"; F = "trim_pitch"; Hits = @("DCS:dnilp96u215cdnilvdnilvpnilvunil", "WarThunder:trim_elevator", "DCS:d9905pnilunilcdnilvdnilvpnilvunil") },
        @{ Q = "zoom"; F = "zoom"; Hits = @("DCS:dnilp37u291cdnilvdnilvpnilvunil", "TinyCombatArena:CameraZoomIn", "WarThunder:zoom") },
        @{ Q = "TMS"; F = "tms"; Hits = @("DCS:d3010pnilu3010cd16vd1vpnilvu0", "DCS:d9906pnilunilcdnilvdnilvpnilvunil") },
        @{ Q = "tms aft"; F = "tms"; Dir = "down"; Hits = @("DCS:d3011pnilu3011cd16vd-1vpnilvu0", "DCS:d9906pnilunilcdnilvdnilvpnilvunil"); Not = @("DCS:d3010pnilu3010cd16vd1vpnilvu0") },
        @{ Q = "make the plane go faster"; F = "throttle"; Hits = @("WarThunder:throttle") },
        @{ Q = "release flares"; F = "flare"; Hits = @("TinyCombatArena:Flares") },
        @{ Q = "lower the landing gear"; F = "gear"; Dir = "down"; Hits = @("DCS:d431pnilunilcdnilvdnilvpnilvunil") },
        @{ Q = "move sideways"; F = "strafe_lateral"; Hits = @("EliteDangerous:LateralThrustRaw", "StarCitizen:spaceship_movement|v_strafe_lateral") },
        @{ Q = "talk to the tower"; F = "comm_menu"; Hits = @("DCS:d179pnilunilcdnilvdnilvpnilvunil") }
    )
    foreach ($row in $fmTable) {
        $r = Invoke-FmJson @("relations", "search", $row.Q, "--limit", "80")
        $read = @($r.Resolved | Where-Object { -not $_.Related })
        $fn = @($read | Where-Object { $_.Id -eq $row.F })
        Assert-True ($fn.Count -ge 1) ("search '" + $row.Q + "' was not read as " + $row.F + " (read as " + (@($read | ForEach-Object { $_.Id }) -join ", ") + ").")
        if ($row.Dir) { Assert-True ($fn[0].Dir -eq $row.Dir) ("search '" + $row.Q + "' lost the direction " + $row.Dir + " (" + $fn[0].Dir + ").") }
        $keys = @($r.Results | ForEach-Object { $_.Game + ":" + $_.Id })
        foreach ($hit in $row.Hits) {
            Assert-True ($keys -contains $hit) ("search '" + $row.Q + "' did not find " + $hit + ".")
        }
        foreach ($miss in @($row.Not)) {
            if ($miss) { Assert-True (-not ($keys -contains $miss)) ("search '" + $row.Q + "' found the opposite direction " + $miss + ".") }
        }
        if ($row.First) {
            Assert-True ($row.First -contains $keys[0]) ("search '" + $row.Q + "' did not rank a collective first (" + $keys[0] + ").")
        }
        Assert-True ($r.ElapsedMilliseconds -lt 200) ("search '" + $row.Q + "' took " + $r.ElapsedMilliseconds + " ms.")
        Assert-True (@($r.Groups).Count -ge 1 -and @($r.FunctionGroups).Count -ge 1) ("search '" + $row.Q + "' has no groups.")
    }
    # Exact title first, then the function by its own words, then synonyms.
    $fmRank = Invoke-FmJson @("relations", "search", "throttle", "--game", "DCS|WarThunder", "--axis")
    $fmFirst = @($fmRank.Results)[0]
    Assert-True ($fmFirst.Match -eq "exact" -or $fmFirst.Match -eq "function") ("search 'throttle' did not rank its own words first (" + $fmFirst.Match + ").")
    Assert-True (@($fmRank.Results | Where-Object { -not $_.IsAxis -or ($_.Game -ne "DCS" -and $_.Game -ne "WarThunder") }).Count -eq 0) "search --game/--axis let other rows through."
    # The canonical flight axis leads its game (MSFS / FSX fixtures, asked like the GUI: --limit 200 --per-game 12):
    # "pitch" is MSFS's elevator axis, not the hot-air balloon's burner pitch, the cyclic or the gyroscope axis.
    $fmPrimary = @(
        @{ Q = "pitch"; First = @{ MSFS2024 = "KEY_AXIS_ELEVATOR_SET"; FSX = "AXIS_ELEVATOR_SET" } },
        @{ Q = "roll"; First = @{ MSFS2024 = "KEY_AXIS_AILERONS_SET"; FSX = "AXIS_AILERONS_SET" } },
        @{ Q = "yaw"; First = @{ MSFS2024 = "KEY_AXIS_RUDDER_SET"; FSX = "AXIS_RUDDER_SET" } },
        @{ Q = "rudder"; First = @{ MSFS2024 = "KEY_AXIS_RUDDER_SET"; FSX = "AXIS_RUDDER_SET" } },
        @{ Q = "throttle"; First = @{ MSFS2024 = "KEY_THROTTLE_AXIS_SET_EX1"; FSX = "AXIS_THROTTLE_SET" } },
        @{ Q = "elevator"; First = @{ MSFS2024 = "KEY_AXIS_ELEVATOR_SET"; FSX = "AXIS_ELEVATOR_SET" } }
    )
    foreach ($row in $fmPrimary) {
        foreach ($axisOnly in @($false, $true)) {
            $kind = @($(if ($axisOnly) { "--axis" }))
            $r = Invoke-FmJson (@("relations", "search", $row.Q, "--limit", "200", "--per-game", "12") + $kind)
            foreach ($game in $row.First.Keys) {
                $first = @($r.Results | Where-Object { $_.Game -eq $game })[0]
                Assert-True ($null -ne $first -and $first.Id -eq $row.First[$game]) ("search '" + $row.Q + "' " + ($kind -join " ") + " did not rank " + $row.First[$game] + " first in " + $game + " (first: " + $(if ($first) { $first.Id + " '" + $first.Title + "'" } else { "none" }) + ").")
            }
        }
    }
    $fmPitch = Invoke-FmJson @("relations", "search", "pitch", "--game", "MSFS2024", "--limit", "200")
    foreach ($notPitch in @("KEY_AXIS_BURNER_PITCH_SET", "KEY_BURNER_PITCH_INC", "KEY_GYRO_AXIS_ELEVATOR_SET", "KEY_PROP_PITCH_AXIS_SET_EX1", "KEY_AP_PITCH_REF_INC_UP", "KEY_SYNC_FLIGHT_DIRECTOR_PITCH", "KEY_AXIS_ELEV_TRIM_SET")) {
        $hit = @($fmPitch.Results | Where-Object { $_.Id -eq $notPitch })[0]
        Assert-True ($null -eq $hit -or -not (@($hit.Functions) | Where-Object { $_ -match '^pitch(:|$)' })) ("search 'pitch' read " + $notPitch + " as the pitch function.")
    }
    $fmPitchSecond = @($fmPitch.Results)[1]
    Assert-True ($fmPitchSecond.Id -eq "KEY_AXIS_CYCLIC_LONGITUDINAL_SET") ("search 'pitch' in MSFS 2024 should list the helicopter cyclic after the elevator (second: " + $fmPitchSecond.Id + ").")
    $fmGyro = Invoke-FmJson @("relations", "search", "yaw", "--game", "MSFS2024", "--limit", "200")
    Assert-True (@($fmGyro.Results | Where-Object { $_.Id -eq "KEY_GYRO_AXIS_RUDDER_SET" -and (@($_.Functions) -contains "yaw") }).Count -eq 0) "the MSFS gyroscope yaw axis must not be the yaw function."
    $fmResolvePitch = Invoke-FmJson @("relations", "resolve", "pitch", "--game", "MSFS2024")
    Assert-True ($fmResolvePitch.Games[0].Actions[0].Id -eq "KEY_AXIS_ELEVATOR_SET") ("resolve 'pitch' did not list MSFS's elevator axis first (" + $fmResolvePitch.Games[0].Actions[0].Id + ").")

    $fmTypo = Invoke-FmJson @("relations", "search", "throtle")
    Assert-True ($fmTypo.DidYouMean -eq "throttle") ("search 'throtle' gave no 'did you mean throttle' (" + $fmTypo.DidYouMean + ").")
    Assert-True (@($fmTypo.FunctionIds) -contains "throttle") "search 'throtle' did not find the throttle function."

    # relations resolve: query -> function ids and the actions per game (the local AI's tool).
    $fmResolve = Invoke-FmJson @("relations", "resolve", "drop countermeasures")
    Assert-True (@($fmResolve.Functions | Where-Object { $_.Id -eq "cm_dispense" }).Count -eq 1) "resolve 'drop countermeasures' did not name the countermeasure function."
    Assert-True (@($fmResolve.Games).Count -ge 2) "resolve 'drop countermeasures' found too few games."
    $fmResolveUp = Invoke-FmJson @("relations", "resolve", "gear up", "--per-game", "0")
    Assert-True ($fmResolveUp.Functions[0].Id -eq "gear" -and $fmResolveUp.Functions[0].Dir -eq "up") "resolve 'gear up' lost the direction."
    Assert-True (@($fmResolveUp.Games | ForEach-Object { $_.Actions } | Where-Object { $_.Dir -eq "down" }).Count -eq 0) "resolve 'gear up' listed gear down."

    # Suggestions from the map: a throttle relation grows into the collective and Star Citizen's forward thrust.
    $fmProfile = Join-Path $fmDir "fm.pr0file"
    $fmAdd = Run-Engine @("--profile", $fmProfile, "relations", "add", "Throttle", "--axis", "--node", "DCS:a2004cdnil:F-16C_50", "--text")
    Assert-True ($fmAdd.ExitCode -eq 0) ("relations add for the function map failed: " + $fmAdd.Output)
    $fmSuggest = Invoke-FmJson @("--profile", $fmProfile, "relations", "suggest", "Throttle")
    Assert-True (@($fmSuggest.MapFunctions | Where-Object { $_.Id -eq "throttle" }).Count -eq 1) "suggest did not read the relation as the throttle function."
    $fmSc = @($fmSuggest.Suggestions | Where-Object { $_.Id -eq "spaceship_movement|v_strafe_longitudinal" })[0]
    Assert-True ($null -ne $fmSc) "suggest did not propose Star Citizen's forward thrust for a throttle relation."
    Assert-True (@($fmSc.Reasons | Where-Object { $_ -match "^Same function: Throttle axis" }).Count -ge 1) "the Star Citizen suggestion has no 'Same function: Throttle axis' reason."
    $fmNew = @($fmSuggest.Suggestions | Where-Object { $_.Id -eq "a9901cdnil" })[0]
    Assert-True ($null -ne $fmNew -and $fmNew.Why -match "^Same function: Throttle") "suggest did not propose the throttle of an aircraft the catalog does not know."
    Assert-True (@($fmSuggest.Suggestions | Where-Object { -not $_.IsAxis }).Count -eq 0) "suggest proposed buttons for an axis relation."
    $fmApply = Run-Engine @("--profile", $fmProfile, "relations", "suggest", "apply", "Throttle", "--never", "DCS:a9901cdnil:Z-99 Testjet", "--text")
    Assert-True ($fmApply.ExitCode -eq 0) "suggest apply --never failed."
    $fmAfter = Invoke-FmJson @("--profile", $fmProfile, "relations", "suggest", "Throttle")
    Assert-True (@($fmAfter.Suggestions | Where-Object { $_.Id -eq "a9901cdnil" }).Count -eq 0) "a dismissed map suggestion came back."

    # Auto Group: a game the catalog does not know joins by the map.
    $fmGroup = Invoke-FmJson @("--profile", $fmProfile, "relations", "auto-group", "--planes", "TinyCombatArena", "--dry-run")
    Assert-True ($fmGroup.UseMap -and $fmGroup.MapRows -ge 3) ("auto-group added too few map rows (" + $fmGroup.MapRows + ").")
    Assert-True (@($fmGroup.MapAircraft) -contains "TinyCombatArena") "auto-group did not bring in Tiny Combat Arena."
    Assert-True (@($fmGroup.Relations | Where-Object { $_.Name -eq "Pitch" -and @($_.Aircraft) -contains "TinyCombatArena" }).Count -eq 1) "auto-group did not put Tiny Combat Arena's pitch into Pitch."
    $fmNoMap = Invoke-FmJson @("--profile", $fmProfile, "relations", "auto-group", "--planes", "TinyCombatArena", "--dry-run", "--no-map")
    Assert-True ($fmNoMap.MapRows -eq 0 -and @($fmNoMap.Relations).Count -eq 0) "auto-group --no-map still used the map."
}
finally {
    $env:JOYPRO_DATA_DIR = $fmPreviousData
    $env:JOYPRO_ACTION_INDEX_DIR = $fmPreviousIndex
}
