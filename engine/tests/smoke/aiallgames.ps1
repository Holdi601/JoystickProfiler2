# Every AI path understands every game (docs/ai-plans.md "Every game"): the AI matrix (engine relations ai-matrix) asks
# the JoyPro assistant, Manage / Edit with AI, the fill, By description and the relation chat typical requests for a few
# games of each kind (DCS, a sim, a space game, a game without its own function table) and checks what the local model
# would be shown; a question gets an answer about that game's action by name; a game without its own function table
# has derived rows on the supported games page. Needs the development game lists.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$aiDir = Join-Path $tempDir "aiallgames"
New-Item -Path (Join-Path $aiDir "data") -ItemType Directory -Force | Out-Null
$aiPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $aiDir "data"
try {
    $aiProfile = Join-Path $aiDir "games.pr0file"
    $aiRoll = Run-Engine @("--profile", $aiProfile, "relations", "from-text", "roll in DCS", "--name", "Roll")
    Assert-True ($aiRoll.ExitCode -eq 0) ("from-text Roll failed: " + $aiRoll.Output)
    $aiGear = Run-Engine @("--profile", $aiProfile, "relations", "from-text", "gear toggle in DCS", "--name", "Gear TOGGLE")
    Assert-True ($aiGear.ExitCode -eq 0) ("from-text Gear TOGGLE failed: " + $aiGear.Output)

    $aiOut = Join-Path $aiDir "matrix.json"
    $aiMatrix = Run-Engine @("--profile", $aiProfile, "--out", $aiOut, "relations", "ai-matrix", "--games", "DCS|XPlane12|StarCitizen|EliteDangerous|FSX|ProjectWingman")
    Assert-True ($aiMatrix.ExitCode -eq 0) ("relations ai-matrix failed: " + $aiMatrix.Output)
    $aiResult = [IO.File]::ReadAllText($aiOut) | ConvertFrom-Json
    $aiFailed = @($aiResult.Cells | Where-Object { -not $_.Pass } | ForEach-Object { $_.Game + " | " + $_.Path + " | " + $_.Request + " -> " + $_.Verdict })
    Assert-True ($aiResult.Total -ge 100) ("the AI matrix asked too few cells: " + $aiResult.Total)
    Assert-True ($aiFailed.Count -eq 0) ("AI matrix failures: " + ($aiFailed -join "; "))

    # The matrix changed nothing.
    $aiList = Run-Engine @("--profile", $aiProfile, "--text", "relations", "list")
    Assert-Match $aiList.Output "Relations: 2" "relations ai-matrix must not save the profile."

    # A question: JoyPro's answer about the game's action, by its readable title, no change and no id.
    $aiAsk = Run-Engine @("--profile", $aiProfile, "--text", "relations", "assistant", "what does boost do in Elite Dangerous", "--llm", "off")
    Assert-True ($aiAsk.ExitCode -eq 0) ("assistant question failed: " + $aiAsk.Output)
    Assert-Match $aiAsk.Output "In Elite Dangerous, `"Engine Boost`"" "the assistant should answer about Elite's Engine Boost."
    Assert-NotMatch $aiAsk.Output "UseBoostJuice" "the answer must not show the action's id."

    # Short game names: "xp12", "msfs", "sc" reach their games in Manage with AI.
    $aiPlanOut = Join-Path $aiDir "plan.json"
    $aiPlan = Run-Engine @("--profile", $aiProfile, "--out", $aiPlanOut, "relations", "plan", "ask", "make a relation for landing gear in xp12", "--llm", "off")
    Assert-True ($aiPlan.ExitCode -eq 0) ("plan ask failed: " + $aiPlan.Output)
    $aiDetails = (@(([IO.File]::ReadAllText($aiPlanOut) | ConvertFrom-Json).Steps)[0].Details) -join "`n"
    Assert-Match $aiDetails "X-Plane 12: Landing gear" "Manage with AI should find the X-Plane 12 landing gear for 'xp12'."
    Assert-NotMatch $aiDetails "sim/flight_controls" "Manage with AI must not show X-Plane command ids."

    # A game without a function table of its own gets derived rows (search by function, suggestions, Auto Assign).
    $aiSupportOut = Join-Path $aiDir "support.json"
    $aiSupport = Run-Engine @("--out", $aiSupportOut, "games", "support")
    Assert-True ($aiSupport.ExitCode -eq 0) ("games support failed: " + $aiSupport.Output)
    $aiRows = ([IO.File]::ReadAllText($aiSupportOut) | ConvertFrom-Json).Rows
    foreach ($aiGame in @("FSX", "ProjectWingman", "IL21946")) {
        $aiRow = @($aiRows | Where-Object { $_.GameId -eq $aiGame }) | Select-Object -First 1
        Assert-True ($null -ne $aiRow -and $aiRow.Functions -gt 0) ($aiGame + " has no function table rows on the supported games page.")
    }
}
finally {
    $env:JOYPRO_DATA_DIR = $aiPreviousData
}
