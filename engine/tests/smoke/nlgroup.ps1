# Group by description (engine relations from-text, docs/natural-language-grouping.md) and the optional local model
# (engine llm). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $script:EngineExe).
# Never downloads anything from the internet: the downloader runs against "engine llm fake-server" on loopback with a
# test catalog (JOYPRO_LLM_CATALOG), the model path against the same stub (JOYPRO_LLM_FAKE_RESPONSE).

$nlDir = Join-Path $tempDir "nlgroup"
$nlData = Join-Path $nlDir "data"
New-Item -Path $nlData -ItemType Directory -Force | Out-Null
$nlPreviousData = $env:JOYPRO_DATA_DIR
$nlPreviousFake = $env:JOYPRO_LLM_FAKE_RESPONSE
$nlPreviousFakeLog = $env:JOYPRO_LLM_FAKE_LOG
$nlPreviousCatalog = $env:JOYPRO_LLM_CATALOG
$env:JOYPRO_DATA_DIR = $nlData
$env:JOYPRO_LLM_FAKE_RESPONSE = $null
$env:JOYPRO_LLM_FAKE_LOG = $null
$env:JOYPRO_LLM_CATALOG = $null
$nlServer = $null

# Runs from-text with JSON written to a file (stderr stays apart) and returns the parsed result plus the exit code.
function Invoke-FromText([string]$Profile, [string[]]$Arguments) {
    $out = Join-Path $nlDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $Profile, "--out", $out, "relations", "from-text") + $Arguments)
    $json = $null
    if (Test-Path $out) {
        $json = [IO.File]::ReadAllText($out) | ConvertFrom-Json
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = $run.Output; Json = $json }
}

function Get-NlProposal($Result, [string]$Slot) {
    return @($Result.Json.Proposals | Where-Object { @($_.Slots) -contains $Slot }) | Select-Object -First 1
}

function Test-NlPlane($Proposal, [string]$Plane, [string]$Game = "", [string]$Id = "") {
    foreach ($node in @($Proposal.Nodes)) {
        if ($Game.Length -gt 0 -and $node.Game -ne $Game) { continue }
        if ($Id.Length -gt 0 -and $node.Id -ne $Id) { continue }
        if (@($node.Planes) -contains $Plane) { return $true }
    }
    return $false
}

function Invoke-Llm([string[]]$Arguments) {
    $out = Join-Path $nlDir ("llm-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--out", $out, "llm") + $Arguments)
    $json = $null
    $text = ""
    if (Test-Path $out) {
        $text = [IO.File]::ReadAllText($out)
        if ($text.Trim().StartsWith("{")) { $json = $text | ConvertFrom-Json }
    }
    return [pscustomobject]@{ ExitCode = $run.ExitCode; Output = ($run.Output + [Environment]::NewLine + $text); Json = $json }
}

try {
    # Vocabulary: every slot, aircraft and game the words name exists in the catalog.
    $nlCheck = Run-Engine @("relations", "from-text", "--check", "--text")
    Assert-True ($nlCheck.ExitCode -eq 0) ("relations from-text --check failed: " + $nlCheck.Output)
    Assert-Match $nlCheck.Output "(?m)^Problems: 0" ("language.json names unknown slots, aircraft or games: " + $nlCheck.Output)

    # Phrasings -> relations. Each row: request, expected slots (exactly), aircraft that must be in a proposal
    # (slot|plane|game|id, id optional), aircraft that must not be in any proposal.
    $nlProfile = Join-Path $nlDir "preview.pr0file"
    $nlCases = @(
        @{ Request = "speed brake in and out on all jets"; Slots = @("airbakeextend", "airbrakeretract");
           Has = @("airbakeextend|A-10C|DCS|d578pnilu579cdnilvdnilvpnilvunil", "airbakeextend|F-16C_50|DCS|", "airbrakeretract|FA-18C_hornet|DCS|", "airbakeextend|MSFS2024|MSFS2024|KEY_SPOILERS_ON", "airbakeextend|MSFS2020|MSFS2020|KEY_SPOILERS_ON", "airbakeextend|XPlane12|XPlane12|sim/flight_controls/speed_brakes_down_all", "airbakeextend|XPlane11|XPlane11|sim/flight_controls/speed_brakes_down_all");
           Not = @("P-51D", "UH-1H", "StarCitizen") },
        @{ Request = "put gear up/down of every DCS warbird"; Slots = @("gearin", "gearout");
           Has = @("gearin|P-51D|DCS|", "gearout|SpitfireLFMkIX|DCS|", "gearin|FW-190D9|DCS|");
           Not = @("F-16C_50", "MSFS2024", "MSFS2020", "IL2Game") },
        @{ Request = "all trim hat up actions for helicopters"; Slots = @("trim_nosedown");
           Has = @("trim_nosedown|AH-64D_BLK_II_PLT|DCS|d3056pnilu3056cd25vd1vpnilvu0", "trim_nosedown|SA342 Pilot|DCS|", "trim_nosedown|MSFS2024|MSFS2024|KEY_ELEV_TRIM_DN", "trim_nosedown|MSFS2020|MSFS2020|KEY_ELEV_TRIM_DN");
           Not = @("F-16C_50", "UH-1H", "IL2Game") },
        @{ Request = "flaps for all MSFS airliners and IL-2 bombers"; Slots = @("flapsup", "flapsdown", "flapsneutral", "flaps_toggle");
           Has = @("flapsup|MSFS2024|MSFS2024|KEY_FLAPS_UP", "flapsup|MSFS2020|MSFS2020|KEY_FLAPS_UP", "flapsup|IL2Game|IL2Game|flaps_up", "flapsdown|IL2Game|IL2Game|flaps_dn");
           Not = @("XPlane12", "XPlane11", "F-16C_50", "WarThunder") },
        # One version of a sim that has a game per version: only that version.
        @{ Request = "gear up and down in MSFS 2020 and X-Plane 11"; Slots = @("gearin", "gearout");
           Has = @("gearin|MSFS2020|MSFS2020|KEY_GEAR_UP", "gearout|XPlane11|XPlane11|sim/flight_controls/landing_gear_down");
           Not = @("MSFS2024", "XPlane12", "F-16C_50") },
        @{ Request = "flaps up and down in War Thunder"; Slots = @("flapsup", "flapsdown");
           Has = @("flapsup|WarThunder|WarThunder|ID_FLAPS_UP", "flapsdown|WarThunder|WarThunder|ID_FLAPS_DOWN");
           Not = @("MSFS2024", "MSFS2020", "P-51D") },
        @{ Request = "CMS forward for all DCS jets except the A-10"; Slots = @("cms_fwd_start_program_or_just_flare");
           Has = @("cms_fwd_start_program_or_just_flare|F-16C_50|DCS|", "cms_fwd_start_program_or_just_flare|FA-18C_hornet|DCS|");
           Not = @("A-10C", "A-10C II", "FalconBMS") },
        @{ Request = "lower the gear and raise the flaps on the F-16 and the Hornet"; Slots = @("gearout", "flapsup");
           Has = @("gearout|F-16C_50|DCS|", "flapsup|FA-18C_hornet|DCS|");
           Not = @("A-10C", "MSFS2024", "MSFS2020") },
        @{ Request = "TMS up on F-16"; Slots = @("target_management_system_up");
           Has = @("target_management_system_up|F-16C_50|DCS|");
           Not = @("FA-18C_hornet", "FalconBMS") },
        @{ Request = "master arm on and off in BMS"; Slots = @("master_arm_on", "master_arm_off");
           Has = @("master_arm_on|FalconBMS|FalconBMS|");
           Not = @("F-16C_50") },
        @{ Request = "pitch, roll and rudder for DCS helicopters"; Slots = @("pitch", "roll", "rudder");
           Has = @("pitch|UH-1H|DCS|", "rudder|mi-8mt|DCS|");
           Not = @("F-16C_50", "MSFS2024", "MSFS2020") },
        # Flares are the countermeasures dispenser (CMS forward / flare button) without a model, too.
        @{ Request = "flares"; Slots = @("cms_fwd_start_program_or_just_flare");
           Has = @("cms_fwd_start_program_or_just_flare|F-16C_50|DCS|", "cms_fwd_start_program_or_just_flare|AH-64D_BLK_II_PLT|DCS|", "cms_fwd_start_program_or_just_flare|FA-18C_hornet|DCS|");
           Not = @() },
        @{ Request = "pop flares on the Viper"; Slots = @("cms_fwd_start_program_or_just_flare");
           Has = @("cms_fwd_start_program_or_just_flare|F-16C_50|DCS|");
           Not = @("FA-18C_hornet", "A-10C") },
        @{ Request = "chaff and flares on the F-16 and F/A-18"; Slots = @("cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff");
           Has = @("cms_fwd_start_program_or_just_flare|F-16C_50|DCS|", "cms_aft_panic_or_just_chaff|FA-18C_hornet|DCS|");
           Not = @("A-10C", "F-14") },
        @{ Request = "countermeasure dispenser for helicopters"; Slots = @("cms_fwd_start_program_or_just_flare", "cms_aft_panic_or_just_chaff", "cms_left", "cms_right", "cms_depress_ecm");
           Has = @("cms_fwd_start_program_or_just_flare|AH-64D_BLK_II_PLT|DCS|", "cms_aft_panic_or_just_chaff|AH-64D_BLK_II_PLT|DCS|");
           Not = @("F-16C_50") },
        @{ Request = "lower the hook and fold the wings on the Tomcat"; Slots = @("tailhook_down", "wingfoldfold");
           Has = @("tailhook_down|F-14|DCS|");
           Not = @("FA-18C_hornet") }
    )
    foreach ($case in $nlCases) {
        $r = Invoke-FromText $nlProfile @($case.Request, "--dry-run", "--llm", "off")
        Assert-True ($r.ExitCode -eq 0 -and $null -ne $r.Json) ("from-text '" + $case.Request + "' failed: " + $r.Output)
        $slots = @($r.Json.Proposals | ForEach-Object { $_.Slots } | ForEach-Object { $_ })
        Assert-SetEquivalent $slots $case.Slots ("from-text '" + $case.Request + "' picked the wrong functions: " + ($slots -join ", "))
        foreach ($has in $case.Has) {
            $p = $has.Split("|")
            $proposal = Get-NlProposal $r $p[0]
            Assert-True ($null -ne $proposal) ("from-text '" + $case.Request + "' has no relation for " + $p[0])
            Assert-True (Test-NlPlane $proposal $p[1] $p[2] $p[3]) ("from-text '" + $case.Request + "': " + $p[0] + " lacks " + $p[1] + " " + $p[3])
        }
        foreach ($plane in $case.Not) {
            foreach ($proposal in @($r.Json.Proposals)) {
                Assert-True (-not (Test-NlPlane $proposal $plane)) ("from-text '" + $case.Request + "' must not include " + $plane + " (" + $proposal.Name + ")")
            }
        }
        foreach ($proposal in @($r.Json.Proposals)) {
            foreach ($node in @($proposal.Nodes)) {
                Assert-True ($node.Confidence -gt 0 -and $node.Confidence -le 1) ("from-text node without a confidence: " + $node.Spec)
                Assert-True (@($node.Explanations).Count -eq @($node.Planes).Count) ("from-text node without an explanation per aircraft: " + $node.Spec)
            }
        }
        Assert-True (-not (Test-Path $nlProfile)) ("from-text --dry-run saved the profile ('" + $case.Request + "').")
    }
    $nlAxis = Invoke-FromText $nlProfile @("pitch for DCS helicopters", "--dry-run", "--llm", "off")
    Assert-True ((Get-NlProposal $nlAxis "pitch").IsAxis -eq $true) "from-text pitch should be an axis relation."
    $nlText = Run-Engine @("--profile", $nlProfile, "relations", "from-text", "speed brake in and out on all jets", "--dry-run", "--llm", "off", "--text")
    Assert-Match $nlText.Output "(?m)^  - Speed Brake Out \(jets\) \(button, new" "from-text text output should name the relation by function and aircraft."
    Assert-Match $nlText.Output 'A-10C: throttle: speed brake switch aft = "[^"]+"( \[[^\]]*\])? \(catalog confirmed\); jets' "from-text text output should explain each aircraft with the game's own command name."

    # The prompt the local model would get (no model runs): what each function does, the related CMS directions for
    # "flares", the game's command names, few-shot examples and the grammar; it fits the model's context.
    $nlPrompt = Invoke-FromText $nlProfile @("flares on the Viper", "--dry-run", "--llm", "off", "--show-prompt", "--prompt-model", "qwen3-8b-q4_k_m")
    Assert-True ($nlPrompt.ExitCode -eq 0) ("from-text --show-prompt failed: " + $nlPrompt.Output)
    $nlPromptText = [string]$nlPrompt.Json.Llm.Prompt
    Assert-Match $nlPromptText "(?m)^# Flares \(countermeasures \(CMS/CMDS dispenser\); also: [^:]*ir decoy[^:]*\): drops flares" "the prompt should say what flares do."
    Assert-Match $nlPromptText "(?m)^F\d+ CMS Aft \(Panic / Chaff\) \[button; \d+ aircraft" "the prompt should offer the related CMS directions for flares."
    Assert-Match $nlPromptText 'F-16C_50 "Countermeasures Management Switch - Fwd"' "the prompt should show the game's own command names."
    Assert-Match $nlPromptText "(?m)^S\d+ aircraft: F-16C_50 \(DCS; viper" "the prompt should list the named aircraft with its nicknames."
    Assert-Match $nlPromptText "(?m)^=== assistant" "the prompt should carry few-shot examples."
    Assert-Match $nlPromptText "(?m)^root ::= " "the prompt should come with its grammar."
    Assert-True ($nlPrompt.Json.Llm.PromptTokens -gt 0 -and $nlPrompt.Json.Llm.PromptTokens -le (8192 - 220)) ("the 8B prompt does not fit its context: " + $nlPrompt.Json.Llm.PromptTokens)
    $nlTiny = Invoke-FromText $nlProfile @("the thing that slows the jet down in the air", "--dry-run", "--llm", "off", "--prompt-model", "qwen2.5-0.5b-instruct-q4_k_m")
    Assert-True ($nlTiny.Json.Llm.PromptTokens -gt 0 -and $nlTiny.Json.Llm.PromptTokens -le (2048 - 120)) ("the 0.5B prompt was not shrunk to its context: " + $nlTiny.Json.Llm.PromptTokens)
    Assert-Match ([string]$nlTiny.Json.Llm.Prompt) "Speed Brake" "the shrunk prompt lost the speed brake."
    $nlBad = Invoke-FromText $nlProfile @("make coffee", "--dry-run", "--llm", "off")
    Assert-True ($nlBad.ExitCode -ne 0) "from-text accepted a request without any function."
    Assert-True (@($nlBad.Json.NotUnderstood) -contains "coffee") "from-text should list the words it did not understand."

    # Create: only the ticked relation, minus one skipped aircraft; again = nothing new; a command already used in
    # another relation stays there.
    $nlCreate = Join-Path $nlDir "create.pr0file"
    $nlAdd = Run-Engine @("--profile", $nlCreate, "relations", "add", "My Airbrake", "--node", "DCS:d578pnilu579cdnilvdnilvpnilvunil:A-10C", "--text")
    Assert-True ($nlAdd.ExitCode -eq 0) ("relations add for the from-text kept check failed: " + $nlAdd.Output)
    $nlRun = Invoke-FromText $nlCreate @("speed brake in and out on all jets", "--llm", "off", "--select", "Speed Brake Out (jets)", "--skip-node", "DCS:d578pnilu579cdnilvdnilvpnilvunil:A-10C II")
    Assert-True ($nlRun.ExitCode -eq 0) ("from-text create failed: " + $nlRun.Output)
    Assert-True ($nlRun.Json.RelationsCreated -eq 1) ("from-text --select should create one relation, created " + $nlRun.Json.RelationsCreated)
    Assert-True (@($nlRun.Json.Kept | Where-Object { $_ -like "A-10C d578*My Airbrake*" }).Count -eq 1) ("from-text should keep the A-10C command in 'My Airbrake': " + ($nlRun.Json.Kept -join "; "))
    $nlShow = Run-Engine @("--profile", $nlCreate, "relations", "show", "Speed Brake Out (jets)", "--text")
    Assert-True ($nlShow.ExitCode -eq 0) "from-text did not create 'Speed Brake Out (jets)'."
    Assert-Match $nlShow.Output "(?m)^Groups:.*FromText" "from-text relations should be tagged FromText."
    Assert-Match $nlShow.Output "aircraft=.*F-16C_50" "from-text relation lacks the F-16C."
    Assert-NotMatch $nlShow.Output "aircraft=(.*, )?A-10C II(,|\s*$)" "from-text --skip-node still added the A-10C II."
    Assert-NotMatch $nlShow.Output "aircraft=(.*, )?A-10C(,|\s*$)" "from-text moved the A-10C command out of the user's relation."
    $nlNotSelected = Run-Engine @("--profile", $nlCreate, "relations", "show", "Speed Brake In (jets)", "--text")
    Assert-True ($nlNotSelected.ExitCode -ne 0) "from-text created a relation that was not selected."
    $nlAgain = Invoke-FromText $nlCreate @("speed brake out on all jets", "--llm", "off", "--skip-node", "DCS:d578pnilu579cdnilvdnilvpnilvunil:A-10C II")
    Assert-True ($nlAgain.ExitCode -eq 0 -and $nlAgain.Json.NodesAdded -eq 0) ("a second from-text run added nodes again: " + $nlAgain.Output)

    # Local model through the fake llama-server: ids outside the candidate lists are dropped, the process is started
    # for the request and gone afterwards.
    $nlAnswer = Join-Path $nlDir "answer.json"
    $nlLog = Join-Path $nlDir "fake.log"
    [IO.File]::WriteAllText($nlAnswer, '{"functions":["F2","F999","F1"],"include":["S777","S2"],"exclude":[],"note":"speed brake out and in on the named aircraft"}')
    $nlOff = Invoke-FromText $nlProfile @("speed brake in and out on all jets", "--dry-run", "--llm", "on")
    Assert-True ($nlOff.ExitCode -ne 0) "from-text --llm on must fail while the local model is off and not downloaded."
    Assert-Match $nlOff.Output "local AI model is off" "from-text --llm on should say the model is off."
    $env:JOYPRO_LLM_FAKE_RESPONSE = $nlAnswer
    $env:JOYPRO_LLM_FAKE_LOG = $nlLog
    $nlLlm = Invoke-FromText $nlProfile @("speed brake in and out on all jets", "--dry-run", "--llm", "on")
    Assert-True ($nlLlm.ExitCode -eq 0) ("from-text with the fake model failed: " + $nlLlm.Output)
    Assert-True ($nlLlm.Json.Llm.Used -eq $true) "from-text --llm on did not ask the model."
    Assert-SetEquivalent @($nlLlm.Json.Llm.Dropped) @("F999", "S777") "from-text must drop ids the model invented."
    Assert-SetEquivalent @($nlLlm.Json.Proposals | ForEach-Object { $_.Slots } | ForEach-Object { $_ }) @("airbakeextend", "airbrakeretract") "the model's valid function ids were not used."
    Assert-Match $nlLlm.Json.Method "local model" "from-text should report the local model as method."
    Assert-True ($nlLlm.Json.Llm.ProcessExited -eq $true) "the model process must exit after the request."
    Assert-True ($nlLlm.Json.Llm.ProcessId -gt 0) "the model process id is missing."
    Assert-True ($null -eq (Get-Process -Id $nlLlm.Json.Llm.ProcessId -ErrorAction SilentlyContinue)) "the model process is still running after the request."
    $nlLogText = [IO.File]::ReadAllText($nlLog)
    Assert-Match $nlLogText "POST /completion" "the fake model got no completion request."
    Assert-Match $nlLogText '"grammar":"root ::=' "the completion request carries no GBNF grammar."
    Assert-Match $nlLogText 'speed brake in and out on all jets' "the prompt lacks the request."
    Assert-Match $nlLogText '"repeat_penalty":1' "the completion request must switch the repetition penalty off (ids repeat on purpose)."
    Assert-Match $nlLogText '"top_k":1' "the completion request must be greedy."
    Assert-Match $nlLogText 'Speed Brake \(drag devices; also: [^)]*airbrake[^)]*\): extends or retracts the speed brake' "the prompt should say what the function does."
    Assert-Match $nlLogText 'assistant\\n\{\\"note\\":\\"flares and chaff on the F-4E' "the prompt should carry the few-shot answers."
    $nlFirstPid = $nlLlm.Json.Llm.ProcessId
    $nlLlm2 = Invoke-FromText $nlProfile @("speed brake in and out on all jets", "--dry-run", "--llm", "on")
    Assert-True ($nlLlm2.Json.Llm.ProcessId -ne $nlFirstPid -and $nlLlm2.Json.Llm.ProcessExited -eq $true) "each request must start (and end) its own model process."
    [IO.File]::WriteAllText($nlAnswer, 'this is not json')
    $nlJunk = Invoke-FromText $nlProfile @("speed brake in and out on all jets", "--dry-run", "--llm", "on")
    Assert-True ($nlJunk.ExitCode -eq 0) "an invalid model answer must fall back to the offline rules."
    Assert-Match $nlJunk.Json.Method "offline" "an invalid model answer must keep the offline result."
    Assert-True (@($nlJunk.Json.Warnings | Where-Object { $_ -like "*not valid JSON*" }).Count -eq 1) "an invalid model answer should be reported."
    [IO.File]::WriteAllText($nlAnswer, '{"functions":["F42"],"include":["S999"],"exclude":[],"note":""}')
    $nlInvalid = Invoke-FromText $nlProfile @("gear up for DCS warbirds", "--dry-run", "--llm", "on")
    Assert-Match $nlInvalid.Json.Method "offline" "a model answer with only unknown ids must keep the offline result."
    Assert-SetEquivalent @($nlInvalid.Json.Proposals | ForEach-Object { $_.Slots } | ForEach-Object { $_ }) @("gearin") "the offline result was lost after an invalid model answer."
    # Class and game picked together narrow each other (warbirds + IL-2 = IL-2's warbirds, not all warbirds + IL-2).
    $nlMergeAnswer = '{"note":"trim hat up for IL-2 warbirds","functions":["F1"],"include":[],"exclude":[]}'
    $nlMergePrompt = Invoke-FromText $nlProfile @("trim hat up for warbirds in IL-2", "--dry-run", "--llm", "off", "--show-prompt")
    # Only the real request (the few-shot examples above it have S ids of their own).
    $nlMergeText = [string]$nlMergePrompt.Json.Llm.Prompt
    $nlMergeText = $nlMergeText.Substring($nlMergeText.LastIndexOf("=== user"))
    $nlWarbirdId = ([regex]::Match($nlMergeText, "(?m)^(S\d+) class: warbirds")).Groups[1].Value
    $nlIl2Id = ([regex]::Match($nlMergeText, "(?m)^(S\d+) game: IL-2 \(")).Groups[1].Value
    Assert-True ($nlWarbirdId.Length -gt 0 -and $nlIl2Id.Length -gt 0) "the prompt lacks the warbird class or the IL-2 game line."
    [IO.File]::WriteAllText($nlAnswer, $nlMergeAnswer.Replace('"include":[]', '"include":["' + $nlWarbirdId + '","' + $nlIl2Id + '"]'))
    $nlMerge = Invoke-FromText $nlProfile @("trim hat up for warbirds in IL-2", "--dry-run", "--llm", "on")
    Assert-Match $nlMerge.Json.Method "local model" "the merge answer was not used."
    Assert-True ($nlMerge.Json.Understood.AircraftCount -eq 1) ("warbirds + IL-2 from the model must mean IL-2's warbirds, got " + $nlMerge.Json.Understood.AircraftCount + " aircraft (" + $nlMerge.Json.Understood.Aircraft + ").")
    $env:JOYPRO_LLM_FAKE_RESPONSE = $null
    $env:JOYPRO_LLM_FAKE_LOG = $null

    # Your own llama.cpp + GGUF (find-local / use-local): nothing is downloaded; the server applies the GGUF's chat
    # template (/v1/chat/completions with messages, thinking off). The fake server stands in for llama-server.
    $nlOwn = Join-Path $nlDir "own"
    New-Item -Path $nlOwn -ItemType Directory -Force | Out-Null
    $nlOwnServer = Join-Path $nlOwn "llama-server.exe"
    [IO.File]::WriteAllText($nlOwnServer, "stub")
    $nlOwnModel = Join-Path $nlOwn "My-Model-Q4_K_M.gguf"
    foreach ($nlGguf in @($nlOwnModel, (Join-Path $nlOwn "mmproj-My-Model-F16.gguf"))) {
        $nlStream = [IO.File]::Create($nlGguf)
        $nlStream.SetLength(52MB)
        $nlStream.Dispose()
    }
    $nlFind = Invoke-Llm @("find-local", "--root", $nlOwn)
    Assert-True ($nlFind.ExitCode -eq 0 -and $null -ne $nlFind.Json) ("llm find-local failed: " + $nlFind.Output)
    Assert-True ($nlFind.Json.BestServer -eq $nlOwnServer) ("llm find-local did not find the llama-server.exe: " + $nlFind.Output)
    Assert-True ($nlFind.Json.BestModel -eq $nlOwnModel) ("llm find-local did not pick the GGUF: " + $nlFind.Output)
    Assert-True (@($nlFind.Json.Models).Count -eq 1) "llm find-local must skip vision projector (mmproj) files."
    $nlUseBad = Invoke-Llm @("use-local", "--server", (Join-Path $nlOwn "missing.exe"), "--model", $nlOwnModel)
    Assert-True ($nlUseBad.ExitCode -ne 0) "llm use-local accepted a missing llama-server.exe."
    $nlUse = Invoke-Llm @("use-local", "--server", $nlOwnServer, "--model", $nlOwnModel, "--gpu-layers", "20")
    Assert-True ($nlUse.ExitCode -eq 0) ("llm use-local failed: " + $nlUse.Output)
    $nlUseStatus = Invoke-Llm @("status")
    Assert-True ($nlUseStatus.Json.UsingLocal -eq $true -and $nlUseStatus.Json.Ready -eq $true -and $nlUseStatus.Json.Enabled -eq $true -and $nlUseStatus.Json.SelectedModel -eq "local") ("llm status with your own model: " + $nlUseStatus.Output)
    Assert-True ($nlUseStatus.Json.LocalGpuLayers -eq "20") "llm use-local --gpu-layers was not kept."
    $nlOwnLog = Join-Path $nlDir "own.log"
    [IO.File]::WriteAllText($nlAnswer, '{"note":"flares on all aircraft","functions":["F1"],"include":["S1"],"exclude":[]}')
    $env:JOYPRO_LLM_FAKE_RESPONSE = $nlAnswer
    $env:JOYPRO_LLM_FAKE_LOG = $nlOwnLog
    $nlOwnRun = Invoke-FromText $nlProfile @("flares", "--dry-run", "--llm", "on")
    $env:JOYPRO_LLM_FAKE_RESPONSE = $null
    $env:JOYPRO_LLM_FAKE_LOG = $null
    Assert-True ($nlOwnRun.ExitCode -eq 0 -and $nlOwnRun.Json.Llm.Used -eq $true -and $nlOwnRun.Json.Llm.Model -eq "local") ("from-text with your own model failed: " + $nlOwnRun.Output)
    Assert-SetEquivalent @($nlOwnRun.Json.Proposals | ForEach-Object { $_.Slots } | ForEach-Object { $_ }) @("cms_fwd_start_program_or_just_flare") "flares through your own model should be CMS forward."
    Assert-True ($nlOwnRun.Json.Llm.ProcessExited -eq $true) "your own model's process must exit after the request."
    $nlOwnLogText = [IO.File]::ReadAllText($nlOwnLog)
    Assert-Match $nlOwnLogText "POST /v1/chat/completions" "your own GGUF should get its own chat template (chat completions)."
    Assert-Match $nlOwnLogText '"messages":\[' "the chat request lacks the messages."
    Assert-Match $nlOwnLogText '"enable_thinking":false' "the chat request should switch thinking off."
    Assert-Match $nlOwnLogText '"grammar":"root ::=' "the chat request carries no grammar."
    $nlClear = Invoke-Llm @("use-local", "--clear")
    Assert-True ($nlClear.ExitCode -eq 0) "llm use-local --clear failed."
    $nlCleared = Invoke-Llm @("status")
    Assert-True ($nlCleared.Json.UsingLocal -eq $false -and $nlCleared.Json.Enabled -eq $false) "llm use-local --clear should forget your model and switch it off."

    # Hardware -> model choice (injected values; the table in docs/natural-language-grouping.md).
    $nlHw = @(
        @{ Args = @("--ram-gb", "32", "--vram-gb", "12", "--gpu", "NVIDIA GeForce RTX 4070", "--cores", "8", "--avx2", "true"); Model = "qwen3-8b-q4_k_m"; Gpu = $true },
        @{ Args = @("--ram-gb", "16", "--vram-gb", "6", "--gpu-vendor", "amd", "--cores", "6", "--avx2", "true"); Model = "qwen3-4b-q4_k_m"; Gpu = $true },
        @{ Args = @("--ram-gb", "32", "--vram-gb", "0", "--cores", "8", "--avx2", "true"); Model = "qwen3-4b-q4_k_m"; Gpu = $false },
        @{ Args = @("--ram-gb", "16", "--vram-gb", "0", "--cores", "4", "--avx2", "true"); Model = "qwen2.5-1.5b-instruct-q4_k_m"; Gpu = $false },
        @{ Args = @("--ram-gb", "16", "--vram-gb", "1", "--gpu", "Intel UHD Graphics", "--cores", "4", "--avx2", "true"); Model = "qwen2.5-1.5b-instruct-q4_k_m"; Gpu = $false },
        @{ Args = @("--ram-gb", "8", "--vram-gb", "0", "--cores", "4", "--avx2", "false"); Model = "qwen2.5-0.5b-instruct-q4_k_m"; Gpu = $false },
        @{ Args = @("--ram-gb", "2", "--vram-gb", "0", "--cores", "2", "--avx2", "true"); Model = ""; Gpu = $false }
    )
    foreach ($hw in $nlHw) {
        $rec = Invoke-Llm (@("recommend") + $hw.Args)
        Assert-True ($rec.ExitCode -eq 0 -and $null -ne $rec.Json) ("llm recommend failed for " + ($hw.Args -join " ") + ": " + $rec.Output)
        Assert-True ($rec.Json.ModelId -eq $hw.Model) ("llm recommend " + ($hw.Args -join " ") + " chose '" + $rec.Json.ModelId + "', expected '" + $hw.Model + "'.")
        Assert-True (($rec.Json.GpuLayers -gt 0) -eq $hw.Gpu) ("llm recommend " + ($hw.Args -join " ") + " GPU offload " + $rec.Json.GpuLayers)
    }
    $nlStatus = Invoke-Llm @("status")
    Assert-True ($nlStatus.ExitCode -eq 0 -and $nlStatus.Json.Enabled -eq $false) "llm status should report the model off by default."
    Assert-True (@($nlStatus.Json.Models).Count -ge 4) "llm status should list the model catalog."
    Assert-True (@($nlStatus.Json.Models | Where-Object { $_.Installed }).Count -eq 0) "llm status reports a model installed in a fresh data folder."
    Assert-True (@($nlStatus.Json.Models | Where-Object { $_.License -eq "" -or $_.SizeBytes -le 0 }).Count -eq 0) "every catalog model needs a size and a license."

    # Downloader against the loopback stub: consent first, resume a partial file, verify SHA-256, refuse a wrong one.
    $nlServe = Join-Path $nlDir "serve"
    New-Item -Path $nlServe -ItemType Directory -Force | Out-Null
    $nlModelBytes = New-Object byte[] (3 * 1024 * 1024 + 123)
    (New-Object System.Random 42).NextBytes($nlModelBytes)
    [IO.File]::WriteAllBytes((Join-Path $nlServe "test-model.gguf"), $nlModelBytes)
    $nlRuntimeSrc = Join-Path $nlDir "runtime-src"
    New-Item -Path (Join-Path $nlRuntimeSrc "bin") -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $nlRuntimeSrc "bin\llama-server.exe"), "not a real server")
    $nlZip = Join-Path $nlServe "llama-b9999-bin-win-cpu-x64.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($nlRuntimeSrc, $nlZip)
    $nlSha = { param($path) (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant() }
    $nlPortFile = Join-Path $nlDir "port.txt"
    $nlServerLog = Join-Path $nlDir "server.log"
    $nlServer = Start-Process -FilePath $script:EngineExe -ArgumentList @("llm", "fake-server", "--port", "0", "--port-file", ('"' + $nlPortFile + '"'), "--serve", ('"' + $nlServe + '"'), "--log", ('"' + $nlServerLog + '"'), "--lifetime", "240") -PassThru -WindowStyle Hidden
    $nlWait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $nlPortFile) -and $nlWait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 100 }
    Assert-True (Test-Path $nlPortFile) "the fake server did not start."
    $nlBase = "http://127.0.0.1:" + ([IO.File]::ReadAllText($nlPortFile).Trim())
    # CUDA build: llama.cpp's CUDA archive plus the CUDA runtime archive of the same CUDA version.
    $nlCudaZip = Join-Path $nlServe "llama-b9999-bin-win-cuda-12.4-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($nlRuntimeSrc, $nlCudaZip)
    $nlCudartSrc = Join-Path $nlDir "cudart-src"
    New-Item -Path $nlCudartSrc -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $nlCudartSrc "cudart64_12.dll"), "not a real dll")
    $nlCudartZip = Join-Path $nlServe "cudart-llama-bin-win-cuda-12.4-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($nlCudartSrc, $nlCudartZip)
    $nlAsset = { param($zip) @{ name = (Split-Path $zip -Leaf); browser_download_url = ($nlBase + "/files/" + (Split-Path $zip -Leaf)); size = (Get-Item $zip).Length; digest = ("sha256:" + (& $nlSha $zip)) } }
    $nlRelease = @{ tag_name = "b9999"; draft = $false; prerelease = $true; assets = @((& $nlAsset $nlZip), (& $nlAsset $nlCudartZip), (& $nlAsset $nlCudaZip)) }
    # A release list as GitHub's: newest first a tool release without builds (llama.cpp's v0.6.0 since October 2026)
    # and a draft, both skipped (the builds themselves are marked pre-release).
    [IO.File]::WriteAllText((Join-Path $nlServe "nightly-tag.txt"), "b9999")
    $nlToolRelease = @{ tag_name = "v0.6.0"; draft = $false; prerelease = $false; assets = @((& $nlAsset (Join-Path $nlServe "nightly-tag.txt"))) }
    $nlPreRelease = @{ tag_name = "b10000"; draft = $true; prerelease = $true; assets = @(@{ name = "llama-b10000-bin-win-cpu-x64.zip"; browser_download_url = ($nlBase + "/files/missing.zip"); size = 1; digest = ("sha256:" + ("0" * 64)) }) }
    [IO.File]::WriteAllText((Join-Path $nlServe "release.json"), (ConvertTo-Json -InputObject @($nlToolRelease, $nlPreRelease, $nlRelease) -Depth 5))
    $nlCatalogFile = Join-Path $nlDir "models.json"
    $nlCatalog = @{
        runtime = @{ name = "llama.cpp (llama-server)"; releaseApi = ($nlBase + "/files/release.json"); assets = @{ cpu = "llama-*-bin-win-cpu-x64.zip"; vulkan = "llama-*-bin-win-vulkan-x64.zip"; cuda = "llama-*-bin-win-cuda-*-x64.zip" }; extraAssets = @{ cuda = "cudart-llama-bin-win-cuda-*-x64.zip" }; approxSizeMb = @{ cpu = 1; vulkan = 1; cuda = 2 }; license = "MIT"; licenseUrl = "https://example.invalid/LICENSE"; homepage = "https://example.invalid/llama.cpp" };
        prompt = @{ system = @("test") };
        models = @(
            @{ id = "test-model"; label = "Test Model"; tier = "tiny"; url = ($nlBase + "/files/test-model.gguf"); file = "test-model.gguf"; sizeBytes = $nlModelBytes.Length; sha256 = (& $nlSha (Join-Path $nlServe "test-model.gguf")); license = "Apache-2.0"; licenseUrl = "https://example.invalid/apache"; source = "https://example.invalid/test-model" },
            @{ id = "bad-model"; label = "Bad Model"; tier = "small"; url = ($nlBase + "/files/test-model.gguf"); file = "bad-model.gguf"; sizeBytes = $nlModelBytes.Length; sha256 = ("0" * 64); license = "Apache-2.0"; licenseUrl = "https://example.invalid/apache"; source = "https://example.invalid/bad-model" }
        )
    }
    [IO.File]::WriteAllText($nlCatalogFile, ($nlCatalog | ConvertTo-Json -Depth 6))
    $env:JOYPRO_LLM_CATALOG = $nlCatalogFile

    $nlNoConsent = Invoke-Llm @("download", "test-model", "--runtime", "cpu", "--text")
    Assert-True ($nlNoConsent.ExitCode -ne 0) "llm download must not start without --accept-license."
    Assert-Match $nlNoConsent.Output "Apache-2\.0" "llm download without consent should show the model license."
    Assert-Match $nlNoConsent.Output "3 MB" "llm download without consent should show the size."
    Assert-True (-not (Test-Path (Join-Path $nlData "Llm\models\test-model.gguf"))) "llm download wrote the model without consent."
    $nlPart = Join-Path $nlData "Llm\models\test-model.gguf.part"
    New-Item -Path (Split-Path $nlPart) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllBytes($nlPart, $nlModelBytes[0..(1024 * 1024 - 1)])
    $nlDownload = Invoke-Llm @("download", "test-model", "--runtime", "cpu", "--accept-license", "--text")
    Assert-True ($nlDownload.ExitCode -eq 0) ("llm download against the stub failed: " + $nlDownload.Output)
    Assert-Match $nlDownload.Output "Resumed test-model\.gguf" "llm download should resume the partial file."
    Assert-Match ([IO.File]::ReadAllText($nlServerLog)) "range 1048576" "llm download should ask for the rest only (HTTP Range)."
    Assert-True ((& $nlSha (Join-Path $nlData "Llm\models\test-model.gguf")) -eq (& $nlSha (Join-Path $nlServe "test-model.gguf"))) "the resumed model differs from the source."
    Assert-True (Test-Path (Join-Path $nlData "Llm\runtime\cpu\bin\llama-server.exe")) "llm download did not unpack the runtime."
    Assert-Match $nlDownload.Output "llama\.cpp b9999" "llm download should take the newest release with builds (not the tool release or the draft)."
    $nlAfter = Invoke-Llm @("status")
    Assert-True ($nlAfter.Json.Ready -eq $true -and $nlAfter.Json.SelectedModel -eq "test-model" -and $nlAfter.Json.RuntimeInstalled -eq $true) ("llm status after the download: " + $nlAfter.Output)
    $nlCuda = Invoke-Llm @("download", "test-model", "--runtime", "cuda", "--accept-license", "--text")
    Assert-True ($nlCuda.ExitCode -eq 0) ("llm download --runtime cuda against the stub failed: " + $nlCuda.Output)
    Assert-True ((Test-Path (Join-Path $nlData "Llm\runtime\cuda\bin\llama-server.exe")) -and (Test-Path (Join-Path $nlData "Llm\runtime\cuda\cudart64_12.dll"))) "llm download --runtime cuda must unpack the CUDA build and its CUDA runtime."
    $nlCudaStatus = Invoke-Llm @("status")
    Assert-True ($nlCudaStatus.Json.RuntimeFlavor -eq "cuda" -and $nlCudaStatus.Json.RuntimeChoice -eq "cuda" -and $nlCudaStatus.Json.Ready -eq $true) ("llm status after the CUDA download: " + $nlCudaStatus.Output)
    $nlBadDownload = Invoke-Llm @("download", "bad-model", "--accept-license", "--no-runtime", "--text")
    Assert-True ($nlBadDownload.ExitCode -ne 0) "llm download accepted a file with the wrong SHA-256."
    Assert-Match $nlBadDownload.Output "SHA-256" "llm download should name the checksum failure."
    Assert-True (-not (Test-Path (Join-Path $nlData "Llm\models\bad-model.gguf")) -and -not (Test-Path (Join-Path $nlData "Llm\models\bad-model.gguf.part"))) "a file with the wrong SHA-256 must be deleted."
    $nlRemove = Invoke-Llm @("remove", "--all", "--text")
    Assert-True ($nlRemove.ExitCode -eq 0) "llm remove --all failed."
    Assert-True (-not (Test-Path (Join-Path $nlData "Llm\models\test-model.gguf")) -and -not (Test-Path (Join-Path $nlData "Llm\runtime"))) "llm remove --all left files behind."
    $nlRemoved = Invoke-Llm @("status")
    Assert-True ($nlRemoved.Json.Enabled -eq $false) "llm remove --all should switch the model off."
}
finally {
    if ($null -ne $nlServer -and -not $nlServer.HasExited) {
        Stop-Process -Id $nlServer.Id -Force -ErrorAction SilentlyContinue
    }
    $env:JOYPRO_DATA_DIR = $nlPreviousData
    $env:JOYPRO_LLM_FAKE_RESPONSE = $nlPreviousFake
    $env:JOYPRO_LLM_FAKE_LOG = $nlPreviousFakeLog
    $env:JOYPRO_LLM_CATALOG = $nlPreviousCatalog
}
