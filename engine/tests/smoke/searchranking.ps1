# Search ranking on the real game lists (docs/relation-builder.md "How the search finds things"): a query that names a
# flight function ("pitch", "roll", "yaw", "throttle", "brakes") lists each game's main control first (Primary), asked
# like the Relations page does (--limit 200 --per-game 12, with and without --axis): MSFS 2020 / 2024 "AXIS ELEVATOR
# SET" (shown by its menu name: "Elevator Axis - Pitch axis (elevator)"), X-Plane "axis/pitch", DCS "Pitch", FSX / P3D, IL-2, War
# Thunder, Falcon BMS. In every game the function's rows come before the plain word matches (the balloon's "BURNER
# PITCH INC", "AP PITCH LEVELER").
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir); needs the development game lists.

$srDir = Join-Path $tempDir "searchranking"
New-Item -Path $srDir -ItemType Directory -Force | Out-Null
$srPreviousData = $env:JOYPRO_DATA_DIR
$srPreviousIndex = $env:JOYPRO_ACTION_INDEX_DIR
$env:JOYPRO_DATA_DIR = Join-Path $srDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = $null

function Invoke-SrJson([string[]]$Arguments) {
    $out = Join-Path $srDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

try {
    $srExpected = @(
        @{ Q = "pitch"; First = @{ DCS = "a2001cdnil"; MSFS2020 = "KEY_AXIS_ELEVATOR_SET"; MSFS2024 = "KEY_AXIS_ELEVATOR_SET"; XPlane11 = "axis/pitch"; XPlane12 = "axis/pitch";
                                   FSX = "AXIS_ELEVATOR_SET"; P3D6 = "AXIS_ELEVATOR_SET"; IL2Game = "rpc_pitch"; IL21946 = "move.elevator"; WarThunder = "elevator"; FalconBMS = "axis/Pitch" } },
        @{ Q = "roll"; First = @{ DCS = "a2002cdnil"; MSFS2020 = "KEY_AXIS_AILERONS_SET"; MSFS2024 = "KEY_AXIS_AILERONS_SET"; XPlane12 = "axis/roll"; FSX = "AXIS_AILERONS_SET";
                                  IL2Game = "rpc_roll"; WarThunder = "ailerons"; FalconBMS = "axis/Roll" } },
        @{ Q = "yaw"; First = @{ DCS = "a2003cdnil"; MSFS2020 = "KEY_AXIS_RUDDER_SET"; MSFS2024 = "KEY_AXIS_RUDDER_SET"; XPlane12 = "axis/yaw"; FSX = "AXIS_RUDDER_SET";
                                 IL2Game = "rpc_yaw"; WarThunder = "rudder"; FalconBMS = "axis/Yaw" } },
        @{ Q = "throttle"; First = @{ MSFS2020 = "KEY_THROTTLE_AXIS_SET_EX1"; MSFS2024 = "KEY_THROTTLE_AXIS_SET_EX1"; XPlane12 = "axis/throttle"; FSX = "AXIS_THROTTLE_SET";
                                      IL2Game = "rpc_all_engines_throttles"; WarThunder = "throttle"; FalconBMS = "axis/Throttle" } },
        @{ Q = "brakes"; First = @{ MSFS2020 = "KEY_AXIS_LEFT_BRAKE_SET"; MSFS2024 = "KEY_AXIS_LEFT_BRAKE_SET"; XPlane12 = "axis/left_toe_brake"; FSX = "AXIS_LEFT_BRAKE_SET" } }
    )
    foreach ($row in $srExpected) {
        foreach ($axisOnly in @($false, $true)) {
            $kind = @($(if ($axisOnly) { "--axis" }))
            $r = Invoke-SrJson (@("relations", "search", $row.Q, "--limit", "200", "--per-game", "12") + $kind)
            foreach ($game in $row.First.Keys) {
                $first = @($r.Results | Where-Object { $_.Game -eq $game })[0]
                Assert-True ($null -ne $first -and $first.Id -eq $row.First[$game]) ("search '" + $row.Q + "' " + ($kind -join " ") + " did not list " + $row.First[$game] + " first in " + $game +
                                                                                    " (first: " + $(if ($first) { $first.Id + " '" + $first.Title + "'" } else { "none" }) + ").")
                Assert-True ($first.Primary) ("search '" + $row.Q + "': " + $game + " " + $first.Id + " is not marked as the main control.")
            }
            # Every game: the function's rows (exact, function, synonym, described) before the plain word matches, and a
            # game with a main control lists it first.
            foreach ($group in @($r.Results | Group-Object Game)) {
                $rows = @($group.Group)
                $lastFunction = -1
                $firstWords = [int]::MaxValue
                for ($i = 0; $i -lt $rows.Count; $i++) {
                    if ($rows[$i].Match -eq "words" -or $rows[$i].Match -eq "catalog") { $firstWords = [Math]::Min($firstWords, $i) } else { $lastFunction = $i }
                }
                Assert-True ($lastFunction -lt $firstWords) ("search '" + $row.Q + "' " + ($kind -join " ") + ": in " + $group.Name + " a word match comes before a row of the function.")
                if (@($rows | Where-Object { $_.Primary }).Count -gt 0) {
                    Assert-True ($rows[0].Primary) ("search '" + $row.Q + "' " + ($kind -join " ") + ": in " + $group.Name + " the main control is not first (first: " + $rows[0].Id + ").")
                }
            }
        }
    }

    # The title says what an action is when its own title does not name the function; a title that names it stays as it is.
    $srPitch = Invoke-SrJson @("relations", "search", "pitch", "--limit", "200", "--per-game", "12")
    foreach ($game in @("MSFS2020", "MSFS2024")) {
        $elevator = @($srPitch.Results | Where-Object { $_.Game -eq $game -and $_.Id -eq "KEY_AXIS_ELEVATOR_SET" })[0]
        # The event name is shown as the sim's menu calls it ("Elevator Axis"), never as "AXIS ELEVATOR SET".
        Assert-True ($null -ne $elevator -and $elevator.Display -match "^Elevator Axis .+Pitch axis \(elevator\)$") ($game + " AXIS ELEVATOR SET is not shown as the pitch axis (" + $elevator.Display + ").")
        Assert-True ($elevator.Recommended -eq $true) ($game + " AXIS ELEVATOR SET is not the recommended pitch axis.")
        Assert-True ($elevator.FunctionLabel -eq "Pitch axis (elevator)") ($game + " AXIS ELEVATOR SET has the function label '" + $elevator.FunctionLabel + "'.")
    }
    $srDcs = @($srPitch.Results | Where-Object { $_.Game -eq "DCS" })[0]
    Assert-True ($srDcs.Display -eq $srDcs.Title) ("DCS 'Pitch' got a function label in its title (" + $srDcs.Display + ").")
    # A gunner seat's "Gun Up/Down" is pitch only to the function catalog: after the function's rows, marked "catalog".
    $srGun = @($srPitch.Results | Where-Object { $_.Game -eq "DCS" -and $_.Title -eq "Gun Up/Down" })
    $srDcsRows = @($srPitch.Results | Where-Object { $_.Game -eq "DCS" })
    foreach ($gun in $srGun) {
        Assert-True ($gun.Match -eq "catalog" -and -not $gun.Primary) ("DCS 'Gun Up/Down' is listed as " + $gun.Match + " for pitch.")
        Assert-True ([Array]::IndexOf($srDcsRows, $gun) -gt [Array]::IndexOf($srDcsRows, @($srDcsRows | Where-Object { $_.Title -eq "Flight Control Cyclic Pitch" })[0])) "DCS 'Gun Up/Down' comes before the helicopters' cyclic pitch."
    }
    $srBurner = @($srPitch.Results | Where-Object { $_.Game -eq "MSFS2024" -and $_.Id -eq "KEY_BURNER_PITCH_INC" })[0]
    Assert-True ($null -eq $srBurner -or ($srBurner.Match -eq "words" -and -not $srBurner.Primary -and ($srBurner.Display -eq $srBurner.Title -or $srBurner.Display -eq $srBurner.Readable))) "MSFS 2024 BURNER PITCH INC is read as the pitch function."
}
finally {
    $env:JOYPRO_DATA_DIR = $srPreviousData
    $env:JOYPRO_ACTION_INDEX_DIR = $srPreviousIndex
}
