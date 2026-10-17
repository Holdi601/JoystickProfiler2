# The JoyPro assistant of the Relations page (docs/relation-builder.md, engine relations assistant): simple commands
# with the offline brain (create and fill, add, remove, rename, "needs the model"), and the tool loop with the local
# model through the fake llama-server (JOYPRO_LLM_FAKE_RESPONSE with "#next" between the scripted answers): one model
# process per turn, invented ids rejected, a button skipped in an axis relation, an invalid answer answered with an
# error, the step limit, an empty new relation not kept, and the model process gone afterwards.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$raDir = Join-Path $tempDir "relationassistant"
$raData = Join-Path $raDir "data"
New-Item -Path $raData -ItemType Directory -Force | Out-Null
$raPreviousData = $env:JOYPRO_DATA_DIR
$raPreviousFake = $env:JOYPRO_LLM_FAKE_RESPONSE
$raPreviousFakeLog = $env:JOYPRO_LLM_FAKE_LOG
$env:JOYPRO_DATA_DIR = $raData
$env:JOYPRO_LLM_FAKE_RESPONSE = $null
$env:JOYPRO_LLM_FAKE_LOG = $null

function Invoke-RaJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $raDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--out", $out) + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

function Get-RaRelation([string]$ProfileFile, [string]$Name) {
    $list = Invoke-RaJson $ProfileFile @("relations", "list")
    return @($list.Json.Relations | Where-Object { $_.Name -eq $Name }) | Select-Object -First 1
}

try {
    $raProfile = Join-Path $raDir "assistant.pr0file"
    $raStart = Run-Engine @("--profile", $raProfile, "relations", "add", "Flare", "--button", "--node", "DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50", "--text")
    Assert-True ($raStart.ExitCode -eq 0) ("relations add for the assistant failed: " + $raStart.Output)

    # Offline brain: "Name: what" creates the relation and fills it with the resolved axes of every game.
    $raThrottle = Invoke-RaJson $raProfile @("relations", "assistant", "Throttle ALL: all throttle axes", "--llm", "off")
    Assert-True ($raThrottle.ExitCode -eq 0) ("assistant offline create failed: " + $raThrottle.Output)
    Assert-Match $raThrottle.Json.Method "offline brain" "the assistant without --llm should use the offline brain."
    $raEntry = @($raThrottle.Json.Relations | Where-Object { $_.Name -eq "Throttle ALL" }) | Select-Object -First 1
    Assert-True ($null -ne $raEntry -and $raEntry.Created -eq $true) "the assistant did not create 'Throttle ALL'."
    Assert-True ($raThrottle.Json.FocusRelation -eq "Throttle ALL") "the created relation should be the one the GUI opens."
    $raAdded = @($raThrottle.Json.Changes | Where-Object { $_.Kind -eq "added" })
    Assert-True ($raAdded.Count -ge 20) "the offline brain added too few throttle axes."
    Assert-True (@($raAdded | Where-Object { $_.Game -eq "DCS" }).Count -gt 0 -and @($raAdded | Where-Object { $_.Game -ne "DCS" }).Count -gt 0) "the throttle axes should come from DCS and other games."
    Assert-True (@($raAdded | Where-Object { $_.Title -match "(?i)friction|trim|cutoff|mixture|designator" }).Count -eq 0) "the offline brain added side actions that only share the word throttle."
    $raRelation = Get-RaRelation $raProfile "Throttle ALL"
    Assert-True ($null -ne $raRelation -and $raRelation.IsAxis -eq $true -and @($raRelation.Nodes).Count -gt 10) "'Throttle ALL' is not an axis relation with its nodes in the profile."
    Assert-Match ($raEntry.Added | ConvertTo-Json -Compress) '"DCS":\d+' "the summary lacks the per-game counts."

    # rename, remove by game, add to an existing relation; a free sentence without the model only explains.
    $raRename = Invoke-RaJson $raProfile @("relations", "assistant", "rename Throttle ALL to Thrust ALL", "--llm", "off")
    Assert-True ($raRename.ExitCode -eq 0 -and $null -ne (Get-RaRelation $raProfile "Thrust ALL") -and $null -eq (Get-RaRelation $raProfile "Throttle ALL")) ("assistant rename failed: " + $raRename.Output)
    $raRemove = Invoke-RaJson $raProfile @("relations", "assistant", "remove War Thunder", "--relation", "Thrust ALL", "--llm", "off")
    Assert-True ($raRemove.ExitCode -eq 0 -and @($raRemove.Json.Changes | Where-Object { $_.Kind -eq "removed" -and $_.Game -eq "WarThunder" }).Count -ge 1) ("assistant remove failed: " + $raRemove.Output)
    Assert-True (@($raRemove.Json.Changes | Where-Object { $_.Kind -eq "removed" -and $_.Game -ne "WarThunder" }).Count -eq 0) "'remove War Thunder' removed other games."
    $raFree = Invoke-RaJson $raProfile @("relations", "assistant", "make my helicopters feel nicer please", "--llm", "off")
    Assert-True ($raFree.ExitCode -eq 0 -and $raFree.Json.NeedsModel -eq $true -and $raFree.Json.Changed -eq $false) "a free sentence without the model must change nothing and say it needs the model."
    $raOn = Invoke-RaJson $raProfile @("relations", "assistant", "add the collectives", "--relation", "Thrust ALL", "--llm", "on")
    Assert-True ($raOn.ExitCode -ne 0) "assistant --llm on must fail while the local model is off."
    $raPrompt = Invoke-RaJson $raProfile @("relations", "assistant", "add the collectives", "--relation", "Thrust ALL", "--show-prompt")
    Assert-Match $raPrompt.Json.Llm.Prompt 'add_to_relation' "the assistant prompt lacks the tool list."
    Assert-Match $raPrompt.Json.Llm.Prompt 'root ::= ' "the assistant prompt lacks the grammar."
    Assert-Match $raPrompt.Json.Llm.Prompt 'has the relation "Thrust ALL" open' "the assistant prompt lacks the open relation."

    # The tool loop with the fake model: one process for the turn, ids checked, wrong kind skipped, an invalid answer
    # gets an error back, then done.
    $raScript = Join-Path $raDir "script.txt"
    $raLog = Join-Path $raDir "fake.log"
    [IO.File]::WriteAllText($raScript, (@(
        '{"tool":"search","query":"landing gear","kind":"button"}',
        '#next',
        '{"tool":"resolve","query":"collective","kind":"axis"}',
        '#next',
        '{"tool":"create_relation","name":"Collectives","kind":"axis"}',
        '#next',
        '{"tool":"add_to_relation","relation":"Collectives","ids":["S2","A1","A99999"]}',
        '#next',
        'this is not a tool call',
        '#next',
        '{"tool":"done","reply":"Created Collectives."}'
    ) -join "`n"))
    $env:JOYPRO_LLM_FAKE_RESPONSE = $raScript
    $env:JOYPRO_LLM_FAKE_LOG = $raLog
    $raLoop = Invoke-RaJson $raProfile @("relations", "assistant", "Put every helicopter collective into a new relation Collectives", "--llm", "on")
    Assert-True ($raLoop.ExitCode -eq 0) ("assistant with the fake model failed: " + $raLoop.Output)
    Assert-True ($raLoop.Json.Llm.Used -eq $true -and $raLoop.Json.Finished -eq $true) "the assistant did not run the model to done."
    Assert-True (@($raLoop.Json.Steps).Count -eq 6) ("the tool loop should take 6 steps, took " + @($raLoop.Json.Steps).Count)
    Assert-True (@($raLoop.Json.Rejected) -contains "A99999") "an invented id was not rejected."
    $raAddStep = @($raLoop.Json.Steps | Where-Object { $_.Tool -eq "add_to_relation" }) | Select-Object -First 1
    Assert-True (@($raAddStep.Skipped) -contains "A1") "a button id was not skipped for an axis relation."
    Assert-True ($raLoop.Json.Steps[4].Error.Length -gt 0) "an invalid answer was not reported as an error."
    Assert-True ($raLoop.Json.Reply -eq "Created Collectives.") "the model's reply is missing."
    $raCollectives = Get-RaRelation $raProfile "Collectives"
    Assert-True ($null -ne $raCollectives -and $raCollectives.IsAxis -eq $true -and @($raCollectives.Nodes).Count -gt 0) "the model's relation is missing or empty."
    Assert-True (@($raLoop.Json.Changes | Where-Object { $_.Kind -eq "added" -and $_.Title -match "(?i)gear" }).Count -eq 0) "a landing gear button went into the axis relation."
    Assert-True ($raLoop.Json.Llm.ProcessExited -eq $true -and $null -eq (Get-Process -Id $raLoop.Json.Llm.ProcessId -ErrorAction SilentlyContinue)) "the model process must be gone after the turn."
    $raLogText = [IO.File]::ReadAllText($raLog)
    Assert-True (([regex]::Matches($raLogText, '(?m)^pid ')).Count -eq 1) "the turn should start one model process, not one per step."
    Assert-True (([regex]::Matches($raLogText, '(?m)^POST ')).Count -eq 6) "the turn should ask the model six times."
    Assert-Match $raLogText '"grammar":"root ::=' "the assistant request carries no GBNF grammar."
    Assert-Match $raLogText 'You are the JoyPro assistant' "the assistant request lacks its system prompt."
    Assert-Match $raLogText 'RESULT S1 search' "the tool results did not go back to the model."

    # A part of the request nobody looked up ("... including the parking brake"): done is refused once with an error.
    [IO.File]::WriteAllText($raScript, (@(
        '{"tool":"resolve","query":"landing gear","kind":"button"}',
        '#next',
        '{"tool":"create_relation","name":"Gear ALL","kind":"button"}',
        '#next',
        '{"tool":"add_to_relation","relation":"Gear ALL","ids":["S1"]}',
        '#next',
        '{"tool":"done","reply":"Done too early."}',
        '#next',
        '{"tool":"resolve","query":"parking brake","kind":"button"}',
        '#next',
        '{"tool":"done","reply":"Gear ALL has the gear; the parking brake is a separate function."}'
    ) -join "`n"))
    $raParts = Invoke-RaJson $raProfile @("relations", "assistant", "Put the landing gear into one relation called Gear ALL including the parking brake", "--llm", "on")
    Assert-True ($raParts.ExitCode -eq 0 -and $raParts.Json.Finished -eq $true -and @($raParts.Json.Steps).Count -eq 6) ("the parts check broke the loop: " + $raParts.Output)
    Assert-Match $raParts.Json.Steps[3].Error "parking brake" "done was not refused while a part of the request was not looked up."
    Assert-True ($raParts.Json.Reply -match "separate function") "the model's final reply is missing."

    # The step limit and a repeated call; an empty new relation is not kept.
    [IO.File]::WriteAllText($raScript, (@(
        '{"tool":"create_relation","name":"Nothing yet","kind":"button"}',
        '#next',
        '{"tool":"list_relations"}'
    ) -join "`n"))
    Remove-Item -Path $raLog -ErrorAction SilentlyContinue
    $raLimit = Invoke-RaJson $raProfile @("relations", "assistant", "list my relations forever", "--llm", "on", "--max-steps", "3")
    Assert-True ($raLimit.ExitCode -eq 0 -and @($raLimit.Json.Steps).Count -eq 3 -and $raLimit.Json.Finished -eq $false) ("the step limit did not stop the loop: " + $raLimit.Output)
    Assert-True ($raLimit.Json.Steps[2].Error -eq "repeated call") "a repeated identical call was not refused."
    Assert-True ($null -eq (Get-RaRelation $raProfile "Nothing yet")) "an empty relation the assistant created must not be kept."
    Assert-True ($null -eq (Get-Process -Id $raLimit.Json.Llm.ProcessId -ErrorAction SilentlyContinue)) "the model process must be gone after the step limit."
}
finally {
    $env:JOYPRO_DATA_DIR = $raPreviousData
    $env:JOYPRO_LLM_FAKE_RESPONSE = $raPreviousFake
    $env:JOYPRO_LLM_FAKE_LOG = $raPreviousFakeLog
}
