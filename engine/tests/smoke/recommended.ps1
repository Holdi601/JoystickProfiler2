# The recommended action per game (docs/relation-builder.md "Recommended action", EngineActionChoice with
# DB\Functions\action-variants.json) on fixtures of Elite, MSFS 2024, Arma 3, IL-2 Korea, FSX and FSX: SE
# (engine\tests\fixtures\recommended\DB, read through JOYPRO_ACTION_INDEX_DIR): one recommended pitch axis per game, the
# others explained in one line (alternate set, landing mode, combined axis, IL-2's Mouse control), IL-2's mouse turret
# aim and free look named by JoyPro and no pitch axis at all (DB\Functions\ActionTexts\IL2Korea.tsv), plane and
# helicopter pitch both recommended in Arma 3 and MSFS, the MSFS event shown by its menu name. In relations search,
# relations suggest and the catalog (relations browse); relations action-info --coverage counts them.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs no real game lists.

$rcDir = Join-Path $tempDir "recommended"
New-Item -Path $rcDir -ItemType Directory -Force | Out-Null
$rcPreviousData = $env:JOYPRO_DATA_DIR
$rcPreviousIndex = $env:JOYPRO_ACTION_INDEX_DIR
$env:JOYPRO_DATA_DIR = Join-Path $rcDir "data"
$env:JOYPRO_ACTION_INDEX_DIR = Join-Path $engineDir "tests\fixtures\recommended\DB"

function Invoke-RcJson([string[]]$Arguments) {
    $out = Join-Path $rcDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return [IO.File]::ReadAllText($out) | ConvertFrom-Json
}

function Get-RcRow($Rows, [string]$Game, [string]$Id) {
    return @($Rows | Where-Object { $_.Game -eq $Game -and $_.Id -eq $Id })[0]
}

function Assert-RcRows($Rows, [string]$What) {
    $recommended = @{ "EliteDangerous" = "PitchAxisRaw"; "MSFS2024" = "KEY_AXIS_ELEVATOR_SET"; "IL2Korea" = "rpc_pitch"; "FSX" = "AXIS_ELEVATOR_SET"; "FSXSE" = "AXIS_ELEVATOR_SET" }
    foreach ($game in $recommended.Keys) {
        $row = Get-RcRow $Rows $game $recommended[$game]
        Assert-True ($null -ne $row -and $row.Recommended -eq $true) ($What + ": " + $game + " " + $recommended[$game] + " is not the recommended pitch axis.")
    }
    $variants = @(
        @("EliteDangerous", "PitchAxisAlternate", "alternate set"),
        @("EliteDangerous", "PitchAxis_Landing", "landing mode"),
        @("MSFS2024", "KEY_AXIS_AILERONS_ELEVATOR_SET", "two controls on one axis"),
        @("MSFS2024", "KEY_ELEVATOR_SET", "does not say how it differs"),
        @("IL2Korea", "mc_pitch", "^for mouse control$")
    )
    foreach ($v in $variants) {
        $row = Get-RcRow $Rows $v[0] $v[1]
        Assert-True ($null -ne $row -and $row.Recommended -ne $true -and $row.Variant -match $v[2]) ($What + ": " + $v[0] + " " + $v[1] + " is not explained as a variant ('" + $v[2] + "'): " + ($row | ConvertTo-Json -Compress -Depth 2))
    }
    # IL-2's mouse turret aim (rb_) and Mouse control free look are no aircraft pitch: never recommended, shown by
    # JoyPro's own name with its explanation (the raw "Rb pitch" / "Mc pitch free" never).
    foreach ($id in @("rb_pitch", "mc_pitch_free")) {
        $row = Get-RcRow $Rows "IL2Korea" $id
        if ($null -eq $row) { continue }
        Assert-True ($row.Recommended -ne $true -and $row.Readable -match "^Mouse " -and $row.ExplanationSource -eq "joypro") ($What + ": IL-2 Korea " + $id + " is not shown by JoyPro's own name: " + ($row | ConvertTo-Json -Compress -Depth 2))
    }
    $plane = Get-RcRow $Rows "Arma3" "Axis.PlanePitch"
    $heli = Get-RcRow $Rows "Arma3" "Axis.HeliCyclicPitch"
    Assert-True ($plane.Recommended -eq $true -and $plane.RecommendedFor -eq "for planes") ($What + ": Arma 3 plane pitch is not recommended for planes.")
    Assert-True ($heli.Recommended -eq $true -and $heli.RecommendedFor -eq "for helicopters") ($What + ": Arma 3 helicopter pitch is not recommended for helicopters.")
    $cyclic = Get-RcRow $Rows "MSFS2024" "KEY_AXIS_CYCLIC_LONGITUDINAL_SET"
    Assert-True ($cyclic.Recommended -eq $true -and $cyclic.RecommendedFor -eq "for helicopters") ($What + ": the MSFS cyclic is not recommended for helicopters.")
    $elevator = Get-RcRow $Rows "MSFS2024" "KEY_AXIS_ELEVATOR_SET"
    Assert-True ($elevator.Readable -eq "Elevator Axis") ($What + ": the MSFS elevator axis is not shown by its menu name ('" + $elevator.Readable + "').")
    $alternate = Get-RcRow $Rows "EliteDangerous" "PitchAxisAlternate"
    Assert-True ($alternate.Group -eq "EliteDangerous:PitchAxisRaw:EliteDangerous") ($What + ": the Elite alternate set is not grouped under the recommended pitch axis.")
}

try {
    $rcSearch = Invoke-RcJson @("relations", "search", "pitch", "--axis", "--limit", "200", "--per-game", "12")
    Assert-RcRows $rcSearch.Results "relations search pitch"

    $rcProfile = Join-Path $rcDir "pitch.pr0file"
    Run-Engine @("--profile", $rcProfile, "relations", "add", "Pitch", "--axis", "--node", "EliteDangerous:RollAxisRaw:EliteDangerous", "--text") | Out-Null
    $rcSuggest = Invoke-RcJson @("--profile", $rcProfile, "relations", "suggest", "Pitch", "--min", "low")
    Assert-True (@($rcSuggest.Suggestions).Count -gt 0) "relations suggest Pitch on the fixtures proposed nothing."
    # The relation has an Elite action already: nothing more is recommended for Elite there.
    Assert-True (@($rcSuggest.Suggestions | Where-Object { $_.Game -eq "EliteDangerous" -and $_.Recommended }).Count -eq 0) "relations suggest recommended a second Elite action for a relation that has one."
    $rcFsx = Get-RcRow $rcSuggest.Suggestions "FSX" "AXIS_ELEVATOR_SET"
    Assert-True ($null -ne $rcFsx -and $rcFsx.Recommended -eq $true -and $rcFsx.FunctionName -match "Pitch") "relations suggest did not recommend the FSX elevator axis with its function."
    $rcMc = Get-RcRow $rcSuggest.Suggestions "IL2Korea" "mc_pitch"
    Assert-True ($null -ne $rcMc -and $rcMc.Recommended -ne $true -and $rcMc.Variant -match "mouse control" -and $rcMc.ExplanationSource -eq "joypro" -and $rcMc.Readable -match "^Mouse control: ") "relations suggest did not explain IL-2 Korea's Mouse control pitch (mc_pitch) as a mouse variant with JoyPro's own text: $($rcMc | ConvertTo-Json -Compress -Depth 2)"
    $rcKorea = Get-RcRow $rcSuggest.Suggestions "IL2Korea" "rpc_pitch"
    # (Mouse control pitch; the mouse turret aim and free look are no aircraft pitch since JoyPro names them.)
    Assert-True ($rcKorea.Alternatives -ge 1) "relations suggest did not count the IL-2 Korea pitch alternatives."

    $rcBrowse = Invoke-RcJson @("relations", "browse", "--game", "EliteDangerous", "--axis")
    $rcRaw = Get-RcRow $rcBrowse.Results "EliteDangerous" "PitchAxisRaw"
    $rcLanding = Get-RcRow $rcBrowse.Results "EliteDangerous" "PitchAxis_Landing"
    Assert-True ($rcRaw.Recommended -eq $true -and $rcLanding.Recommended -ne $true -and $rcLanding.Variant -match "landing") "relations browse did not mark the recommended Elite pitch axis."

    # Opposite directions are never "the same thing" (report: DCS Spitfire "Engine RPM INCREASE" was a variant of
    # DECREASE); only real twins get the generic "the game does not say how it differs".
    $rcPairs = @(
        @("Engine RPM INCREASE", "Engine RPM DECREASE", "opposite"),
        @("Trim Up", "Trim Down", "opposite"),
        @("Landing Lights On", "Landing Lights Off", "opposite"),
        @("Rudder Trim Left", "Rudder Trim Right", "opposite"),
        @("Zoom Plus", "Zoom Minus", "opposite"),
        @("Canopy Open", "Canopy Close", "opposite"),
        @("Gear Extend", "Gear Retract", "opposite"),
        @("Elevator Set", "Elevator Axis", "twin"),
        @("Condition Lever 1 Axis", "Condition Lever Axis", "different")
    )
    foreach ($rcPair in $rcPairs) {
        $rcPairRun = Run-Engine @("relations", "pair", $rcPair[0], $rcPair[1], "--text")
        Assert-Match $rcPairRun.Output (": " + $rcPair[2] + "\s*$") ("relations pair '" + $rcPair[0] + "' / '" + $rcPair[1] + "' should be " + $rcPair[2] + ": " + $rcPairRun.Output)
    }

    # JSON on the console is plain ASCII (\uXXXX): the console code page has no curly quotes, and a best-fit quote broke
    # the GUI's catalog (2.2.17, MSFS 2024 listed nothing).
    $rcQuoted = "Pitch " + [char]0x201C + "quoted" + [char]0x201D + " " + [char]0x2013 + " " + [char]0x00E9
    Run-Engine @("--profile", $rcProfile, "relations", "add", $rcQuoted, "--axis", "--node", "EliteDangerous:PitchAxisRaw:EliteDangerous", "--text") | Out-Null
    $rcList = Run-Engine @("--profile", $rcProfile, "relations", "list")
    Assert-True ($rcList.ExitCode -eq 0) ("relations list failed: " + $rcList.Output)
    $rcJson = $rcList.Output.Substring($rcList.Output.IndexOf('{'))
    $rcJson = $rcJson.Substring(0, $rcJson.LastIndexOf('}') + 1)
    Assert-True (-not ($rcJson -match '[^\x00-\x7E]')) "relations list wrote JSON with characters outside ASCII to the console."
    Assert-True (@(($rcJson | ConvertFrom-Json).Relations | Where-Object { $_.Name -eq $rcQuoted }).Count -eq 1) "relations list JSON did not give back the relation name with curly quotes."

    $rcCoverage = Invoke-RcJson @("relations", "action-info", "--coverage")
    $rcElite = @($rcCoverage.Games | Where-Object { $_.Game -eq "EliteDangerous" })[0]
    Assert-True ($null -ne $rcElite -and $rcElite.Actions -eq 6 -and $rcElite.Readable -eq 6 -and $rcElite.Functions -ge 2) "relations action-info --coverage miscounted the Elite fixture."
    # IL-2 Korea: every action named and explained by JoyPro's table, no "Rb" / "Mc" left in a title.
    $rcIl2 = @($rcCoverage.Games | Where-Object { $_.Game -eq "IL2Korea" })[0]
    Assert-True ($null -ne $rcIl2 -and $rcIl2.Abbreviated -eq 0 -and $rcIl2.JoyProText -eq $rcIl2.Actions) "relations action-info --coverage: IL-2 Korea titles or explanations are missing: $($rcIl2 | ConvertTo-Json -Compress)"
}
finally {
    $env:JOYPRO_DATA_DIR = $rcPreviousData
    $env:JOYPRO_ACTION_INDEX_DIR = $rcPreviousIndex
}
