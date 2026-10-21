# The relation editor (docs/relation-builder.md): start a relation from one action, grow it from its suggestions
# (engine relations suggest with reasons, game filter, dismiss/restore, --take from another relation), find actions of
# every game (engine relations search) and change it from a sentence (engine relations chat) with the offline rules and
# the local model through the fake llama-server (JOYPRO_LLM_FAKE_RESPONSE: invalid ids must be rejected).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir).

$rbDir = Join-Path $tempDir "relationbuilder"
$rbData = Join-Path $rbDir "data"
New-Item -Path $rbData -ItemType Directory -Force | Out-Null
$rbPreviousData = $env:JOYPRO_DATA_DIR
$rbPreviousFake = $env:JOYPRO_LLM_FAKE_RESPONSE
$rbPreviousFakeLog = $env:JOYPRO_LLM_FAKE_LOG
$env:JOYPRO_DATA_DIR = $rbData
$env:JOYPRO_LLM_FAKE_RESPONSE = $null
$env:JOYPRO_LLM_FAKE_LOG = $null

function Invoke-RbJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $rbDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--out", $out) + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

try {
    $rbProfile = Join-Path $rbDir "builder.pr0file"
    $f16Fwd = "DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50"
    $hornetFwd = "DCS:d3033pnilu3033cd13vd-1vpnilvu0:FA-18C_hornet"

    # Start small: one action makes the relation; another relation already uses the Hornet's dispense switch.
    $rbAdd = Run-Engine @("--profile", $rbProfile, "relations", "add", "Flare", "--button", "--node", $f16Fwd, "--text")
    Assert-True ($rbAdd.ExitCode -eq 0) ("relations add for the builder failed: " + $rbAdd.Output)
    Run-Engine @("--profile", $rbProfile, "relations", "add", "Hornet dispense", "--button", "--node", $hornetFwd, "--text") | Out-Null

    # Fits this relation: the same function in other aircraft and games, with the short reason and the conflict.
    $rbSuggest = Invoke-RbJson $rbProfile @("relations", "suggest", "Flare", "--min", "medium")
    Assert-True ($rbSuggest.ExitCode -eq 0) ("relations suggest failed: " + $rbSuggest.Output)
    Assert-True ($rbSuggest.Json.Slot -eq "cms_fwd_start_program_or_just_flare") "the builder's relation was not read as CMS forward."
    Assert-True (@($rbSuggest.Json.Functions | Where-Object { $_.Slot -eq "cms_fwd_start_program_or_just_flare" -and $_.Weight -eq 1 }).Count -eq 1) "the relation's meaning (weighted functions) is missing."
    $rbBms = @($rbSuggest.Json.Suggestions | Where-Object { $_.Game -eq "FalconBMS" -and $_.Id -eq "SimCMSUp" }) | Select-Object -First 1
    Assert-True ($null -ne $rbBms) "suggest did not propose Falcon BMS CMS Up for a CMS forward relation."
    Assert-True ($rbBms.Confidence -eq "high") "Falcon BMS CMS Up should be high confidence."
    Assert-Match $rbBms.Why "^Same function as F-16C_50 'Countermeasures Management Switch - Fwd'" "the suggestion's short reason should name the relation's own entry."
    $rbHornet = @($rbSuggest.Json.Suggestions | Where-Object { $_.Id -eq "d3033pnilu3033cd13vd-1vpnilvu0" }) | Select-Object -First 1
    Assert-True ($null -ne $rbHornet -and @($rbHornet.InOtherRelations) -contains "Hornet dispense") "a suggestion another relation uses must say so."
    $rbNamed = Invoke-RbJson $rbProfile @("relations", "suggest", "--relation", "Flare", "--min", "high")
    Assert-True ($rbNamed.ExitCode -eq 0 -and $rbNamed.Json.Relation -eq "Flare") "relations suggest --relation <name> did not work like the positional name."
    $rbGames = Invoke-RbJson $rbProfile @("relations", "suggest", "Flare", "--game", "FalconBMS|WarThunder")
    Assert-True (@($rbGames.Json.Suggestions | Where-Object { $_.Game -ne "FalconBMS" -and $_.Game -ne "WarThunder" }).Count -eq 0) "suggest --game let other games through."
    Assert-True (@($rbGames.Json.Suggestions).Count -ge 2) "suggest --game lost the filtered games."

    # One click add, dismiss (never comes back, listed as dismissed), restore, and take the Hornet from the other relation.
    $rbApply = Run-Engine @("--profile", $rbProfile, "relations", "suggest", "apply", "Flare", "--add", "FalconBMS:SimCMSUp:FalconBMS", "--never", "WarThunder:ID_FLARES:WarThunder", "--take", $hornetFwd, "--text")
    Assert-True ($rbApply.ExitCode -eq 0) ("suggest apply with --take failed: " + $rbApply.Output)
    Assert-Match $rbApply.Output "(?m)^Taken: 1 \(from Hornet dispense\)" "--take did not take the Hornet's switch from the other relation."
    $rbOther = Run-Engine @("--profile", $rbProfile, "relations", "show", "Hornet dispense", "--text")
    Assert-NotMatch $rbOther.Output "d3033pnilu3033cd13vd-1vpnilvu0" "--take left the command in the other relation."
    $rbAfter = Invoke-RbJson $rbProfile @("relations", "suggest", "Flare")
    Assert-True (@($rbAfter.Json.Suggestions | Where-Object { $_.Id -eq "ID_FLARES" -or $_.Id -eq "SimCMSUp" -or $_.Id -eq "d3033pnilu3033cd13vd-1vpnilvu0" }).Count -eq 0) "an added, taken or dismissed command was proposed again."
    Assert-True (@($rbAfter.Json.Dismissed | Where-Object { $_.Id -eq "ID_FLARES" }).Count -eq 1) "the dismissed command is not listed as dismissed."
    Run-Engine @("--profile", $rbProfile, "relations", "suggest", "apply", "Flare", "--remove", "WarThunder:ID_FLARES:WarThunder", "--text") | Out-Null
    $rbRestored = Invoke-RbJson $rbProfile @("relations", "suggest", "Flare")
    Assert-True (@($rbRestored.Json.Suggestions | Where-Object { $_.Id -eq "ID_FLARES" }).Count -eq 1) "a restored command did not come back as a suggestion."

    # The function of a relation (EngineRelationPurpose): only one that (nearly) all of its actions are. A button
    # position (CMS in the F-16, airbrake in the Viggen, flaps in the F-5, airbrake retract in the Hornet) has none:
    # no majority function, no function-based suggestion; the user can set one, set "None" or go back to automatic.
    $rbBoat = Run-Engine @("--profile", $rbProfile, "relations", "add", "Boat Aft", "--button", "--node", "DCS:d3014pnilu3014cd16vd1vpnilvu0:F-16C_50",
        "--node", "DCS:d3601pnilu3602cd22vd1vpnilvu1:AJS37", "--node", "DCS:d3005pnilunilcd2vd-1vpnilvunil:F-5E-3", "--node", "DCS:d3035pnilunilcd13vd1vpnilvunil:FA-18C_hornet", "--text")
    Assert-True ($rbBoat.ExitCode -eq 0) ("relations add for the button position failed: " + $rbBoat.Output)
    $rbBoatFunction = Invoke-RbJson $rbProfile @("relations", "function", "Boat Aft")
    Assert-True ($rbBoatFunction.ExitCode -eq 0 -and $rbBoatFunction.Json.State -eq "mixed" -and $rbBoatFunction.Json.Slot -eq "") ("a button position was given a function: " + $rbBoatFunction.Output)
    $rbBoatSuggest = Invoke-RbJson $rbProfile @("relations", "suggest", "Boat Aft", "--min", "low")
    Assert-True ($rbBoatSuggest.Json.FunctionState -eq "mixed" -and $rbBoatSuggest.Json.Slot -eq "" -and @($rbBoatSuggest.Json.Functions).Count -eq 0) "suggest read a majority function into a button position."
    Assert-True (@($rbBoatSuggest.Json.Suggestions | Where-Object { $_.Source -eq "catalog" -or $_.Source -eq "function map" -or $_.Source -eq "title" -or $_.Source -eq "your style" }).Count -eq 0) "a button position got function-based suggestions."
    $rbBoatSet = Invoke-RbJson $rbProfile @("relations", "function", "Boat Aft", "--set", "Flaps Down")
    Assert-True ($rbBoatSet.ExitCode -eq 0 -and $rbBoatSet.Json.State -eq "set" -and $rbBoatSet.Json.Slot -eq "flapsdown" -and $rbBoatSet.Json.SetByUser) ("relations function --set failed: " + $rbBoatSet.Output)
    $rbBoatSetSuggest = Invoke-RbJson $rbProfile @("relations", "suggest", "Boat Aft", "--min", "low")
    Assert-True ($rbBoatSetSuggest.Json.Slot -eq "flapsdown" -and $rbBoatSetSuggest.Json.FunctionSetByUser) "the function the user set is not the relation's function (not saved?)."
    $rbBoatNone = Invoke-RbJson $rbProfile @("relations", "function", "Boat Aft", "--none")
    Assert-True ($rbBoatNone.Json.State -eq "cleared") "relations function --none did not clear the function."
    $rbBoatAuto = Invoke-RbJson $rbProfile @("relations", "function", "Boat Aft", "--auto")
    Assert-True ($rbBoatAuto.Json.State -eq "mixed") "relations function --auto did not go back to automatic."
    $rbWrongKind = Run-Engine @("--profile", $rbProfile, "relations", "function", "Boat Aft", "--set", "pitch", "--text")
    Assert-True ($rbWrongKind.ExitCode -ne 0) "an axis function was set on a button relation."
    $rbChoices = Invoke-RbJson $rbProfile @("relations", "function", "--list", "--button", "--query", "flaps")
    Assert-True (@($rbChoices.Json | Where-Object { $_.Id -eq "flapsdown" }).Count -eq 1 -and @($rbChoices.Json | Where-Object { $_.IsAxis }).Count -eq 0) "relations function --list --button did not list the flap functions."

    # Find actions: fuzzy, across games, with the function words, filters and the relations that use a hit.
    $rbSearch = Invoke-RbJson $rbProfile @("relations", "search", "flare", "--limit", "400")
    Assert-True ($rbSearch.ExitCode -eq 0) ("relations search failed: " + $rbSearch.Output)
    Assert-True (@($rbSearch.Json.FunctionIds) -contains "flare") "search did not read 'flare' as the flare function of the function map."
    Assert-True (@($rbSearch.Json.Results | Where-Object { $_.Game -eq "FalconBMS" -and $_.Id -eq "SimCMSUp" -and @($_.InRelations) -contains "Flare" }).Count -eq 1) "search did not find BMS CMS Up through the function words, or not its relation."
    $rbTypo = Invoke-RbJson $rbProfile @("relations", "search", "countermesures", "dispnse", "--limit", "50")
    Assert-True (@($rbTypo.Json.Results | Where-Object { $_.Title -match "Countermeasures" }).Count -gt 0) "search did not forgive two typos."
    $rbWt = Invoke-RbJson $rbProfile @("relations", "search", "gear", "up", "--game", "WarThunder", "--button")
    Assert-True (@($rbWt.Json.Results | Where-Object { $_.Id -eq "ID_GEAR" }).Count -eq 1) "search 'gear up' in War Thunder should still find Toggle Gear."
    Assert-True (@($rbWt.Json.Results | Where-Object { $_.Game -ne "WarThunder" -or $_.IsAxis }).Count -eq 0) "search --game/--button let other rows through."
    # Pick from a game's list: a whole category, no words.
    $rbCategory = Invoke-RbJson $rbProfile @("relations", "search", "--game", "WarThunder", "--category", "Helicopter", "--limit", "500")
    Assert-True ($rbCategory.ExitCode -eq 0 -and @($rbCategory.Json.Results).Count -gt 5) ("search --category without words failed: " + $rbCategory.Output)
    Assert-True (@($rbCategory.Json.Results | Where-Object { $_.Category -ne "Helicopter" }).Count -eq 0) "search --category let other categories through."

    # Chat, offline rules: add for a class, remove an aircraft, a game without a function word.
    $rbChat = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "add flare release for all jets", "--llm", "off")
    Assert-True ($rbChat.ExitCode -eq 0) ("relations chat failed: " + $rbChat.Output)
    Assert-True ($rbChat.Json.Intent -eq "add") "chat did not read an add."
    Assert-True (@($rbChat.Json.Add | Where-Object { @($_.Planes) -contains "A-10C" }).Count -eq 1) "chat 'add flare release for all jets' did not add the A-10C."
    Assert-True (@($rbChat.Json.Add | Where-Object { @($_.Planes) -contains "ka-50" }).Count -eq 0) "chat added a helicopter for 'jets'."
    Assert-True ($rbChat.Json.Applied -eq $false) "chat must not change the relation without --apply."
    $rbWar = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "also War Thunder", "--llm", "off")
    Assert-True (@($rbWar.Json.Add | Where-Object { $_.Game -eq "WarThunder" -and $_.Id -eq "ID_FLARES" }).Count -eq 1) "chat 'also War Thunder' did not add War Thunder's countermeasures."
    $rbApplied = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "add flare release for all jets", "--llm", "off", "--apply")
    Assert-True ($rbApplied.Json.Applied -eq $true -and $rbApplied.Json.Added -gt 0) "chat --apply did not add."
    $rbRemove = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "remove the A-10", "--llm", "off")
    Assert-True ($rbRemove.Json.Intent -eq "remove") "chat did not read a remove."
    Assert-True (@($rbRemove.Json.Remove | Where-Object { @($_.Planes) -contains "A-10C" }).Count -eq 1) "chat 'remove the A-10' did not remove the A-10C."
    Assert-True (@($rbRemove.Json.Remove | Where-Object { @($_.Planes) -contains "F-16C_50" }).Count -eq 0) "chat 'remove the A-10' removed the F-16."

    # Chat, local model through the fake server: only ids of the lists count; invented ids and ids in the wrong list
    # are rejected and listed; invalid JSON keeps the offline rules.
    $rbPrompt = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "remove the A-10", "--llm", "off", "--show-prompt")
    $rbPromptText = [string]$rbPrompt.Json.Llm.Prompt
    Assert-Match $rbPromptText "In the relation now \(N ids, only for remove\):" "the chat prompt lacks the N list."
    Assert-Match $rbPromptText "Candidates to add \(C ids, only for add\):" "the chat prompt lacks the C list."
    Assert-Match $rbPromptText 'cid ::= ' "the chat grammar lacks the C ids."
    $rbUser = $rbPromptText.Substring($rbPromptText.LastIndexOf("=== user"))
    $rbA10 = ([regex]::Match($rbUser, '(?m)^(N\d+) DCS A-10C "')).Groups[1].Value
    $rbFirstC = ([regex]::Match($rbUser, '(?m)^(C\d+) ')).Groups[1].Value
    Assert-True ($rbA10.Length -gt 0 -and $rbFirstC.Length -gt 0) "the chat prompt lacks the A-10C N line or any C line."
    $rbAnswer = Join-Path $rbDir "answer.json"
    $rbLog = Join-Path $rbDir "fake.log"
    [IO.File]::WriteAllText($rbAnswer, '{"note":"take the A-10 out","add":["' + $rbFirstC + '","C99999","' + $rbA10 + '"],"remove":["' + $rbA10 + '","N88888","' + $rbFirstC + '"]}')
    $rbOff = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "remove the A-10", "--llm", "on")
    Assert-True ($rbOff.ExitCode -ne 0) "chat --llm on must fail while the local model is off."
    $env:JOYPRO_LLM_FAKE_RESPONSE = $rbAnswer
    $env:JOYPRO_LLM_FAKE_LOG = $rbLog
    $rbLlm = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "remove the A-10", "--llm", "on")
    Assert-True ($rbLlm.ExitCode -eq 0) ("chat with the fake model failed: " + $rbLlm.Output)
    Assert-True ($rbLlm.Json.Llm.Used -eq $true) "chat --llm on did not ask the model."
    Assert-Match $rbLlm.Json.Method "local model" "chat should report the local model as method."
    Assert-SetEquivalent @($rbLlm.Json.Rejected) @("C99999", $rbA10, "N88888", $rbFirstC) "chat must reject invented ids and ids in the wrong list."
    Assert-True (@($rbLlm.Json.Remove | Where-Object { @($_.Planes) -contains "A-10C" }).Count -eq 1) "the model's valid remove id was not used."
    Assert-True (@($rbLlm.Json.Add).Count -eq 1) "the model's one valid add id was not used."
    Assert-True ($rbLlm.Json.Llm.ProcessExited -eq $true -and $null -eq (Get-Process -Id $rbLlm.Json.Llm.ProcessId -ErrorAction SilentlyContinue)) "the model process must be gone after the chat."
    $rbLogText = [IO.File]::ReadAllText($rbLog)
    Assert-Match $rbLogText '"grammar":"root ::=' "the chat request carries no GBNF grammar."
    Assert-Match $rbLogText 'relation assistant of JoyPro' "the chat request lacks the chat system prompt."
    [IO.File]::WriteAllText($rbAnswer, 'not json at all')
    $rbJunk = Invoke-RbJson $rbProfile @("relations", "chat", "Flare", "remove the A-10", "--llm", "on")
    Assert-True ($rbJunk.ExitCode -eq 0 -and $rbJunk.Json.Method -eq "offline rules") "an invalid model answer must keep the offline rules."
    Assert-True (@($rbJunk.Json.Remove | Where-Object { @($_.Planes) -contains "A-10C" }).Count -eq 1) "the offline result was lost after an invalid model answer."
}
finally {
    $env:JOYPRO_DATA_DIR = $rbPreviousData
    $env:JOYPRO_LLM_FAKE_RESPONSE = $rbPreviousFake
    $env:JOYPRO_LLM_FAKE_LOG = $rbPreviousFakeLog
}
