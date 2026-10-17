param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [int]$Iterations = 2,
    [int]$Warmup = 1,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Resolve-EngineExe {
    param([string]$EngineDir, [string]$Configuration, [string]$Platform)
    $candidate = Join-Path $EngineDir ("bin\" + $Platform + "\" + $Configuration + "\engine.exe")
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    dotnet build (Join-Path $EngineDir "engine.csproj") -c $Configuration -p:Platform=$Platform | Out-Host
    Assert-True ($LASTEXITCODE -eq 0) "dotnet build failed."
    Assert-True (Test-Path $candidate) "engine.exe not found: $candidate"
    return (Resolve-Path $candidate).Path
}

function Invoke-EngineTimed {
    param([string]$EngineExe, [string[]]$CmdArgs)
    $prev = $ErrorActionPreference
    $nativePrefExists = $false
    $prevNative = $null
    if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
        $nativePrefExists = $true
        $prevNative = $global:PSNativeCommandUseErrorActionPreference
    }
    try {
        $ErrorActionPreference = "Continue"
        if ($nativePrefExists) {
            $global:PSNativeCommandUseErrorActionPreference = $false
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $output = & $EngineExe @CmdArgs 2>&1
        $exitCode = $LASTEXITCODE
        $sw.Stop()
    } finally {
        if ($nativePrefExists) {
            $global:PSNativeCommandUseErrorActionPreference = $prevNative
        }
        $ErrorActionPreference = $prev
    }

    $lines = @()
    if ($null -eq $output) { $lines = @() }
    elseif ($output -is [System.Array]) {
        $lines = $output | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                if ($_.Exception -and -not [string]::IsNullOrWhiteSpace($_.Exception.Message)) {
                    $_.Exception.Message
                } else {
                    $_.ToString()
                }
            } else {
                $_.ToString()
            }
        }
    }
    else {
        if ($output -is [System.Management.Automation.ErrorRecord] -and $output.Exception -and -not [string]::IsNullOrWhiteSpace($output.Exception.Message)) {
            $lines = @($output.Exception.Message)
        } else {
            $lines = @($output.ToString())
        }
    }

    $bench = @{}
    foreach ($line in $lines) {
        if ($line -match "\[engine-benchmark\]\s+([^=]+)=([0-9]+)\s+ms") {
            $bench[$Matches[1].Trim()] = [int64]$Matches[2]
        }
    }
    if (-not $bench.ContainsKey("run.total")) { $bench["run.total"] = [int64][math]::Round($sw.Elapsed.TotalMilliseconds, 0) }

    return [PSCustomObject]@{
        ExitCode = $exitCode
        ElapsedMs = [double]$sw.Elapsed.TotalMilliseconds
        Lines = $lines
        Bench = $bench
    }
}

function Get-FailureReason {
    param([string[]]$Lines)
    if ($null -eq $Lines -or $Lines.Count -eq 0) {
        return "command exited non-zero with no output."
    }

    $preferred = $Lines | Where-Object {
        $_ -match "(?i)failed|error|validation failed|no relation items were changed|unknown|requires|not found"
    }
    if ($preferred.Count -gt 0) {
        return ($preferred | Select-Object -Last 3) -join " | "
    }

    return ($Lines | Select-Object -Last 6) -join " | "
}

function New-Spec {
    param([string]$Name, [string[]]$CommandArguments, [bool]$Mutating = $false)
    [PSCustomObject]@{ Name = $Name; Args = $CommandArguments; Mutating = $Mutating }
}

function Expand-Args {
    param([string[]]$InputArgs, [hashtable]$Map)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($arg in $InputArgs) {
        $v = $arg
        foreach ($k in $Map.Keys) { $v = $v.Replace($k, [string]$Map[$k]) }
        [void]$out.Add($v)
    }
    return $out.ToArray()
}

$engineDir = Split-Path -Parent $PSScriptRoot
$engineExe = Resolve-EngineExe -EngineDir $engineDir -Configuration $Configuration -Platform $Platform

$dcsFixture = Join-Path $engineDir "tests/fixtures/dcs-saved-games"
$il2Fixture = Join-Path $engineDir "tests/fixtures/il2-game"
Assert-True (Test-Path $dcsFixture) "Missing DCS fixture: $dcsFixture"
Assert-True (Test-Path $il2Fixture) "Missing IL2 fixture: $il2Fixture"

$benchRoot = Join-Path ([IO.Path]::GetTempPath()) ("engine-bench-matrix-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $benchRoot | Out-Null

$seedProfile = Join-Path $benchRoot "seed.pr0file"
$seedProfileBackup = Join-Path $benchRoot "seed_backup.pr0file"
$mergeLeft = Join-Path $benchRoot "merge_left.pr0file"
$mergeRight = Join-Path $benchRoot "merge_right.pr0file"
$mergeOut = Join-Path $benchRoot "merge_out.pr0file"
$relExport = Join-Path $benchRoot "relations.json"
$bindExport = Join-Path $benchRoot "binds.json"
$massExport = Join-Path $benchRoot "mass_export.json"
$dcsImportProfile = Join-Path $benchRoot "dcs_import.pr0file"
$il2ImportProfile = Join-Path $benchRoot "il2_import.pr0file"
$validateProfile = Join-Path $benchRoot "validate_profile.pr0file"
$dcsExportPath = Join-Path $benchRoot "dcs_export"
$il2ExportPath = Join-Path $benchRoot "il2_export"

$defaultsSaved = Join-Path $benchRoot "dcs_saved"
$defaultsInstall = Join-Path $benchRoot "dcs_install"
$defaultsBackup = Join-Path $benchRoot "dcs_backup"
$defaultsJoy = Join-Path $defaultsInstall "Mods\aircraft\A-10C\Input\A-10C\joystick"
New-Item -ItemType Directory -Force -Path $defaultsJoy | Out-Null
New-Item -ItemType Directory -Force -Path $defaultsSaved | Out-Null
New-Item -ItemType Directory -Force -Path $defaultsBackup | Out-Null
"return {}" | Set-Content -Path (Join-Path $defaultsJoy "default.lua") -Encoding UTF8
"return {}" | Set-Content -Path (Join-Path $defaultsJoy "custom_stick.lua") -Encoding UTF8

$benchTag = ([guid]::NewGuid().ToString("N")).Substring(0, 10)
$relationButton = "BenchButton_" + $benchTag
$relationAxis = "BenchAxis_" + $benchTag
$relationOldDevice = "BenchOldDevice_" + $benchTag
$relationNewDeviceSeed = "BenchNewDeviceSeed_" + $benchTag
$relationMassA = "BenchMassA_" + $benchTag
$relationMassB = "BenchMassB_" + $benchTag
$relationAdded = "BenchAdded_" + $benchTag
$relationMerged = "BenchMerged_" + $benchTag
$groupOps = "Ops_" + $benchTag
$groupOpsRenamed = "OpsRenamed_" + $benchTag
$groupMass = "MassOps_" + $benchTag
$groupPerf = "PerfGroup_" + $benchTag
$modifierBase = "BenchMod_" + $benchTag
$modifierAdded = "AddedMod_" + $benchTag
$modifierRenamed = "BenchModRenamed_" + $benchTag
$deviceStick = "Bench Stick A " + $benchTag
$deviceOld = "Bench Old Device " + $benchTag
$deviceNew = "Bench New Device " + $benchTag
$deviceRight = "Right Device " + $benchTag
$nodeButton = "d_" + $benchTag + "_button"
$nodeAxis = "a_" + $benchTag + "_axis"
$nodeOld = "d_" + $benchTag + "_old"
$nodeNew = "d_" + $benchTag + "_new"
$nodeMassA = "d_" + $benchTag + "_mass_a"
$nodeMassB = "d_" + $benchTag + "_mass_b"
$nodeItemAdded = "d_" + $benchTag + "_item_added"

function Setup {
    param([string[]]$CommandArgs, [string]$Label)
    $run = Invoke-EngineTimed -EngineExe $engineExe -CmdArgs $CommandArgs
    if ($run.ExitCode -ne 0) {
        throw ("Setup failed " + $Label + ": " + (Get-FailureReason -Lines $run.Lines))
    }
    if ($Label -ne "merge right" -and -not (Test-Path $seedProfile)) {
        throw ("Setup did not produce seed profile after " + $Label + ": " + (Get-FailureReason -Lines $run.Lines))
    }
}

Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationButton,"--button","--node",("DCS:" + $nodeButton + ":F-16C_50"),"--group",$groupOps,"--device",$deviceStick,"--input","JOY_BTN1") -Label "BenchButton"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationAxis,"--axis","--node",("DCS:" + $nodeAxis + ":F-16C_50"),"--group",$groupOps,"--device",$deviceStick,"--input","JOY_X") -Label "BenchAxis"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationOldDevice,"--button","--node",("DCS:" + $nodeOld + ":F-16C_50"),"--device",$deviceOld,"--input","JOY_BTN2") -Label "BenchOldDevice"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationNewDeviceSeed,"--button","--node",("DCS:" + $nodeNew + ":F-16C_50"),"--device",$deviceNew,"--input","JOY_BTN3") -Label "BenchNewDeviceSeed"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationMassA,"--button","--node",("DCS:" + $nodeMassA + ":F-16C_50"),"--group",$groupMass,"--device",$deviceStick,"--input","JOY_BTN4") -Label "BenchMassA"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","add",$relationMassB,"--button","--node",("DCS:" + $nodeMassB + ":F-16C_50"),"--group",$groupMass,"--device",$deviceStick,"--input","JOY_BTN5") -Label "BenchMassB"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"binds","modifier","add",$relationButton,$modifierBase) -Label "BenchMod"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","item","disable",$relationButton,"--node",("DCS:" + $nodeButton + ":F-18C_hornet")) -Label "seed disable f18"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"dcs","ffb","set",$deviceOld,"--path",$dcsFixture,"--invert-x","true","--invert-y","false","--swap-axis","true","--confirm","false") -Label "ffb"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"relations","export",$relExport) -Label "rel export"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"binds","export",$bindExport) -Label "bind export"
Setup -CommandArgs @("--load-corrections","false","--profile",$seedProfile,"profile","save",$mergeLeft) -Label "merge left"
Setup -CommandArgs @("--load-corrections","false","--profile",$mergeRight,"relations","add",$relationButton,"--button","--node",("DCS:" + $nodeButton + ":F-16C_50"),"--device",$deviceRight,"--input","JOY_BTN9") -Label "merge right"
Setup -CommandArgs @("--load-corrections","false","dcs","import",$dcsFixture,"--save-profile",$validateProfile,"--confirm","false") -Label "validate profile"
if(-not (Test-Path $seedProfile)){
    $children = if(Test-Path $benchRoot){ (Get-ChildItem -Name $benchRoot) -join ", " } else { "<bench-root-missing>" }
    throw ("seed profile missing before backup copy; benchRoot contents: " + $children)
}
Copy-Item -Path $seedProfile -Destination $seedProfileBackup -Force

$map = @{
    "%SEED%" = $seedProfile
    "%WORK%" = $seedProfile
    "%DCS%" = $dcsFixture
    "%IL2%" = $il2Fixture
    "%MLEFT%" = $mergeLeft
    "%MRIGHT%" = $mergeRight
    "%MOUT%" = $mergeOut
    "%RELEXPORT%" = $relExport
    "%BINDEXPORT%" = $bindExport
    "%MASSEXPORT%" = $massExport
    "%DCSIMPORT%" = $dcsImportProfile
    "%IL2IMPORT%" = $il2ImportProfile
    "%VALIDATEPROFILE%" = $validateProfile
    "%DCSEXPORTPATH%" = $dcsExportPath
    "%IL2EXPORTPATH%" = $il2ExportPath
    "%DSAVED%" = $defaultsSaved
    "%DINSTALL%" = $defaultsInstall
    "%DBACKUP%" = $defaultsBackup
    "%REL_BUTTON%" = $relationButton
    "%REL_AXIS%" = $relationAxis
    "%REL_OLD_DEVICE%" = $relationOldDevice
    "%REL_NEW_DEVICE_SEED%" = $relationNewDeviceSeed
    "%REL_MASS_A%" = $relationMassA
    "%REL_MASS_B%" = $relationMassB
    "%REL_ADDED%" = $relationAdded
    "%REL_MERGED%" = $relationMerged
    "%GROUP_OPS%" = $groupOps
    "%GROUP_OPS_RENAMED%" = $groupOpsRenamed
    "%GROUP_PERF%" = $groupPerf
    "%MOD_BASE%" = $modifierBase
    "%MOD_ADDED%" = $modifierAdded
    "%MOD_RENAMED%" = $modifierRenamed
    "%DEVICE_STICK%" = $deviceStick
    "%DEVICE_OLD%" = $deviceOld
    "%DEVICE_NEW%" = $deviceNew
    "%NODE_BUTTON%" = $nodeButton
    "%NODE_ITEM_ADDED%" = $nodeItemAdded
}

$specs = @(
    (New-Spec "summary" @("--benchmark","true","summary")),

    (New-Spec "profile summary" @("--benchmark","true","--profile","%SEED%","profile","summary")),
    (New-Spec "profile load" @("--benchmark","true","--profile","%SEED%","profile","load","%SEED%")),
    (New-Spec "profile save" @("--benchmark","true","--profile","%SEED%","profile","save","%MOUT%")),
    (New-Spec "profile save-last" @("--benchmark","true","--profile","%SEED%","profile","save-last")),
    (New-Spec "profile validate" @("--benchmark","true","--load-corrections","false","--profile","%VALIDATEPROFILE%","profile","validate","--fail-on-warnings","false")),
    (New-Spec "profile merge" @("--benchmark","true","profile","merge","%MLEFT%","%MRIGHT%","--mode","left","--save","%MOUT%")),

    (New-Spec "dcs scan" @("--benchmark","true","dcs","scan","%DCS%")),
    (New-Spec "dcs import" @("--benchmark","true","dcs","import","%DCS%","--save-profile","%DCSIMPORT%","--confirm","false")),
    (New-Spec "dcs export" @("--benchmark","true","--profile","%SEED%","dcs","export","%DCSEXPORTPATH%","--mode","merge-overwrite")),
    (New-Spec "dcs defaults cut" @("--benchmark","true","dcs","defaults","cut","%DSAVED%","%DINSTALL%","--backup-path","%DBACKUP%")),
    (New-Spec "dcs defaults restore" @("--benchmark","true","dcs","defaults","restore","%DSAVED%","%DINSTALL%","--backup-path","%DBACKUP%")),
    (New-Spec "dcs refresh preview-clicks" @("--benchmark","true","dcs","refresh","preview-clicks","--path","%DSAVED%","--install-path","%DINSTALL%","--preview-clicks")),
    (New-Spec "dcs refresh clean" @("--benchmark","true","dcs","refresh","clean","%DSAVED%","%DINSTALL%","--headless-lua","true","--launch","false","--dry-run","--menu-wait-seconds","1","--ocr-menu-timeout-seconds","1","--settle-seconds","1")),
    (New-Spec "dcs refresh id" @("--benchmark","true","dcs","refresh","id","%DSAVED%","%DINSTALL%","--headless-lua","true","--launch","false","--dry-run","--modules-to-scan","1","--menu-wait-seconds","1","--ocr-menu-timeout-seconds","1","--settle-seconds","1")),
    (New-Spec "dcs ffb list" @("--benchmark","true","--profile","%SEED%","dcs","ffb","list","--path","%DCS%","--confirm","false")),
    (New-Spec "dcs ffb set" @("--benchmark","true","--profile","%WORK%","dcs","ffb","set","%DEVICE_OLD%","--path","%DCS%","--invert-x","true","--invert-y","false","--swap-axis","true","--confirm","false") $true),
    (New-Spec "dcs ffb clear" @("--benchmark","true","--profile","%WORK%","dcs","ffb","clear","%DEVICE_OLD%","--path","%DCS%","--confirm","false") $true),

    (New-Spec "il2 scan" @("--benchmark","true","il2","scan","%IL2%")),
    (New-Spec "il2 import" @("--benchmark","true","il2","import","%IL2%","--save-profile","%IL2IMPORT%","--confirm","false")),
    (New-Spec "il2 export" @("--benchmark","true","--profile","%IL2IMPORT%","il2","export","%IL2EXPORTPATH%","--mode","merge-overwrite")),

    (New-Spec "db summary" @("--benchmark","true","db","summary")),
    (New-Spec "db list" @("--benchmark","true","db","list","break")),
    (New-Spec "db check" @("--benchmark","true","db","check")),
    (New-Spec "db games" @("--benchmark","true","db","games")),
    (New-Spec "db planes" @("--benchmark","true","db","planes","--game","DCS")),
    (New-Spec "db fingerprint" @("--benchmark","true","db","fingerprint")),

    (New-Spec "games paths" @("--benchmark","true","games","paths")),
    (New-Spec "games list" @("--benchmark","true","games","list")),
    (New-Spec "games detect" @("--benchmark","true","games","detect")),

    (New-Spec "devices list" @("--benchmark","true","devices","list")),
    (New-Spec "devices list connected" @("--benchmark","true","devices","list","--connected")),
    (New-Spec "devices list profile" @("--benchmark","true","--profile","%SEED%","devices","list","--profile-devices")),
    (New-Spec "devices inputs" @("--benchmark","true","devices","inputs")),
    (New-Spec "devices exchange" @("--benchmark","true","--profile","%WORK%","devices","exchange","%DEVICE_OLD%","%DEVICE_NEW%","--confirm","false") $true),
    (New-Spec "devices remove" @("--benchmark","true","--profile","%WORK%","devices","remove","%DEVICE_NEW%","--confirm","false") $true),

    (New-Spec "relations list" @("--benchmark","true","--profile","%SEED%","relations","list")),
    (New-Spec "relations show" @("--benchmark","true","--profile","%SEED%","relations","show","%REL_BUTTON%")),
    (New-Spec "relations add" @("--benchmark","true","--profile","%WORK%","relations","add","%REL_ADDED%","--button","--node","DCS:%NODE_ITEM_ADDED%:F-16C_50","--device","%DEVICE_STICK%","--input","JOY_BTN8") $true),
    (New-Spec "relations group list" @("--benchmark","true","--profile","%SEED%","relations","group","list")),
    (New-Spec "relations group add" @("--benchmark","true","--profile","%WORK%","relations","group","add","%REL_BUTTON%","%GROUP_PERF%") $true),
    (New-Spec "relations group remove" @("--benchmark","true","--profile","%WORK%","relations","group","remove","%REL_BUTTON%","%GROUP_OPS%") $true),
    (New-Spec "relations group rename" @("--benchmark","true","--profile","%WORK%","relations","group","rename","%GROUP_OPS%","%GROUP_OPS_RENAMED%") $true),
    (New-Spec "relations item add" @("--benchmark","true","--profile","%WORK%","relations","item","add","%REL_BUTTON%","--id","%NODE_ITEM_ADDED%","--game","DCS","--planes","F-16C_50") $true),
    (New-Spec "relations item remove" @("--benchmark","true","--profile","%WORK%","relations","item","remove","%REL_BUTTON%","--node","DCS:%NODE_BUTTON%:F-16C_50") $true),
    (New-Spec "relations item enable" @("--benchmark","true","--profile","%WORK%","relations","item","enable","%REL_BUTTON%","--node","DCS:%NODE_BUTTON%:F-18C_hornet") $true),
    (New-Spec "relations item disable" @("--benchmark","true","--profile","%WORK%","relations","item","disable","%REL_BUTTON%","--node","DCS:%NODE_BUTTON%:F-16C_50") $true),
    (New-Spec "relations mass delete" @("--benchmark","true","--profile","%WORK%","relations","mass","delete","--relation","%REL_MASS_B%") $true),
    (New-Spec "relations mass duplicate" @("--benchmark","true","--profile","%WORK%","relations","mass","duplicate","--relation","%REL_MASS_A%") $true),
    (New-Spec "relations mass merge" @("--benchmark","true","--profile","%WORK%","relations","mass","merge","%REL_MERGED%","--relations","%REL_MASS_A%,%REL_MASS_B%") $true),
    (New-Spec "relations mass split" @("--benchmark","true","--profile","%WORK%","relations","mass","split","_Split","--plane","DCS:F-16C_50","--relation","%REL_MASS_A%") $true),
    (New-Spec "relations mass add-prefix" @("--benchmark","true","--profile","%WORK%","relations","mass","add-prefix","Pre_","--relation","%REL_MASS_A%") $true),
    (New-Spec "relations mass add-postfix" @("--benchmark","true","--profile","%WORK%","relations","mass","add-postfix","_Post","--relation","%REL_MASS_A%") $true),
    (New-Spec "relations mass remove-part" @("--benchmark","true","--profile","%WORK%","relations","mass","remove-part","Bench","--relation","%REL_MASS_A%") $true),
    (New-Spec "relations mass export" @("--benchmark","true","--profile","%SEED%","relations","mass","export","%MASSEXPORT%","--format","json","--relation","%REL_MASS_A%")),
    (New-Spec "relations export" @("--benchmark","true","--profile","%SEED%","relations","export","%RELEXPORT%")),
    (New-Spec "relations import" @("--benchmark","true","--profile","%WORK%","relations","import","%RELEXPORT%") $true),
    (New-Spec "relations edit" @("--benchmark","true","--profile","%WORK%","--editor","cmd /c type {file} >nul","relations","edit") $true),

    (New-Spec "binds list" @("--benchmark","true","--profile","%SEED%","binds","list")),
    (New-Spec "binds show" @("--benchmark","true","--profile","%SEED%","binds","show","%REL_BUTTON%")),
    (New-Spec "binds set" @("--benchmark","true","--profile","%WORK%","binds","set","%REL_BUTTON%","--device","%DEVICE_STICK%","--input","JOY_BTN7") $true),
    (New-Spec "binds clear" @("--benchmark","true","--profile","%WORK%","binds","clear","%REL_BUTTON%","--input-only") $true),
    (New-Spec "binds modifier list" @("--benchmark","true","--profile","%SEED%","binds","modifier","list")),
    (New-Spec "binds modifier list relation" @("--benchmark","true","--profile","%SEED%","binds","modifier","list","%REL_BUTTON%")),
    (New-Spec "binds modifier add" @("--benchmark","true","--profile","%WORK%","binds","modifier","add","%REL_BUTTON%","%MOD_ADDED%") $true),
    (New-Spec "binds modifier remove" @("--benchmark","true","--profile","%WORK%","binds","modifier","remove","%REL_BUTTON%","%MOD_BASE%") $true),
    (New-Spec "binds modifier rename" @("--benchmark","true","--profile","%WORK%","binds","modifier","rename","%MOD_BASE%","%MOD_RENAMED%") $true),
    (New-Spec "binds unbind" @("--benchmark","true","--profile","%WORK%","binds","unbind","--device","%DEVICE_STICK%","--input","JOY_BTN1","--all") $true),
    (New-Spec "binds export" @("--benchmark","true","--profile","%SEED%","binds","export","%BINDEXPORT%")),
    (New-Spec "binds import" @("--benchmark","true","--profile","%WORK%","binds","import","%BINDEXPORT%") $true),
    (New-Spec "binds edit" @("--benchmark","true","--profile","%WORK%","--editor","cmd /c type {file} >nul","binds","edit") $true)
)

$rows = New-Object System.Collections.Generic.List[object]
$metricTotals = @{}
$metricCounts = @{}

for($i=0; $i -lt $specs.Count; $i++){
    $spec = $specs[$i]
    Write-Host ("[" + ($i+1) + "/" + $specs.Count + "] " + $spec.Name)

    $localMap = @{}
    foreach($k in $map.Keys){ $localMap[$k] = $map[$k] }
    if($spec.Mutating){
        if(-not (Test-Path $seedProfile) -and (Test-Path $seedProfileBackup)){
            Copy-Item -Path $seedProfileBackup -Destination $seedProfile -Force
        }
        $work = Join-Path $benchRoot ("work_" + $i.ToString("D3") + ".pr0file")
        Copy-Item -Path $seedProfile -Destination $work -Force
        $localMap["%WORK%"] = $work
    } else {
        $localMap["%WORK%"] = $seedProfile
    }

    $commandArgs = Expand-Args -InputArgs $spec.Args -Map $localMap
    $w = if($spec.Mutating){0}else{$Warmup}
    $n = if($spec.Mutating){1}else{[Math]::Max(1,$Iterations)}

    $failed = $false
    $reason = ""
    for($j=0; $j -lt $w; $j++){
        $r = Invoke-EngineTimed -EngineExe $engineExe -CmdArgs $commandArgs
        if($r.ExitCode -ne 0){ $failed = $true; $reason = Get-FailureReason -Lines $r.Lines; break }
    }

    $elapsed = @(); $runTotal = @(); $cmdExec = @()
    if(-not $failed){
        for($j=0; $j -lt $n; $j++){
            $r = Invoke-EngineTimed -EngineExe $engineExe -CmdArgs $commandArgs
            if($r.ExitCode -ne 0){ $failed = $true; $reason = Get-FailureReason -Lines $r.Lines; break }
            $elapsed += [double]$r.ElapsedMs
            if($r.Bench.ContainsKey("run.total")){ $runTotal += [double]$r.Bench["run.total"] }
            if($r.Bench.ContainsKey("command.execute")){ $cmdExec += [double]$r.Bench["command.execute"] }
            foreach($entry in $r.Bench.GetEnumerator()){
                if(-not $metricTotals.ContainsKey($entry.Key)){ $metricTotals[$entry.Key]=0.0; $metricCounts[$entry.Key]=0 }
                $metricTotals[$entry.Key] += [double]$entry.Value
                $metricCounts[$entry.Key] += 1
            }
        }
    }

    if($failed){
        $rows.Add([pscustomobject]@{Name=$spec.Name;Status="fail";Runs=0;AvgRunTotal=$null;AvgExec=$null;Min=$null;Max=$null;Reason=$reason}) | Out-Null
    } else {
        $rows.Add([pscustomobject]@{
            Name=$spec.Name;Status="ok";Runs=$elapsed.Count;
            AvgRunTotal=[math]::Round((($runTotal|Measure-Object -Average).Average),2);
            AvgExec=if($cmdExec.Count -gt 0){[math]::Round((($cmdExec|Measure-Object -Average).Average),2)}else{$null};
            Min=[math]::Round((($elapsed|Measure-Object -Minimum).Minimum),2);
            Max=[math]::Round((($elapsed|Measure-Object -Maximum).Maximum),2);
            Reason=""
        }) | Out-Null
    }
}

$ok = $rows | Where-Object {$_.Status -eq "ok"} | Sort-Object AvgRunTotal -Descending
$fail = $rows | Where-Object {$_.Status -eq "fail"}

Write-Host ""
Write-Host ("Success: " + $ok.Count + "/" + $rows.Count)
$ok | Select-Object Name,Runs,AvgRunTotal,AvgExec,Min,Max | Format-Table -AutoSize | Out-Host

if($fail.Count -gt 0){
    Write-Host ""
    Write-Host "Failures:"
    $fail | Select-Object Name,Reason | Format-Table -Wrap | Out-Host
}

$metrics = New-Object System.Collections.Generic.List[object]
foreach($k in $metricTotals.Keys){
    $count = [int]$metricCounts[$k]
    if($count -gt 0){
        $metrics.Add([pscustomobject]@{Metric=$k;AvgMs=[math]::Round($metricTotals[$k]/$count,2);Samples=$count}) | Out-Null
    }
}
$metrics = $metrics | Sort-Object AvgMs -Descending
Write-Host ""
Write-Host "Internal metrics:" 
$metrics | Format-Table -AutoSize | Out-Host

$out = if([string]::IsNullOrWhiteSpace($OutputPath)){ Join-Path $engineDir "docs\ENGINE_BENCHMARK_COMMAND_MATRIX.md" } else { [IO.Path]::GetFullPath($OutputPath) }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Engine Command Matrix Benchmark") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("Generated: " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")) | Out-Null
$lines.Add("Success: " + $ok.Count + "/" + $rows.Count) | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| Command | Status | Runs | Avg run.total (ms) | Avg command.execute (ms) | Min elapsed (ms) | Max elapsed (ms) |") | Out-Null
$lines.Add("|---|---|---:|---:|---:|---:|---:|") | Out-Null
foreach($r in $rows){
    $lines.Add("| " + $r.Name + " | " + $r.Status + " | " + $r.Runs + " | " + $r.AvgRunTotal + " | " + $r.AvgExec + " | " + $r.Min + " | " + $r.Max + " |") | Out-Null
}
if($fail.Count -gt 0){
    $lines.Add("") | Out-Null
    $lines.Add("## Failures") | Out-Null
    $lines.Add("") | Out-Null
    foreach($r in $fail){ $lines.Add("- " + $r.Name + ": " + $r.Reason) | Out-Null }
}
$lines.Add("") | Out-Null
$lines.Add("## Internal Metrics") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| Metric | Avg (ms) | Samples |") | Out-Null
$lines.Add("|---|---:|---:|") | Out-Null
foreach($m in $metrics){ $lines.Add("| " + $m.Metric + " | " + $m.AvgMs + " | " + $m.Samples + " |") | Out-Null }
$lines | Set-Content -Path $out -Encoding UTF8
Write-Host ("Wrote report: " + $out)

if(Test-Path $benchRoot){ Remove-Item -Path $benchRoot -Recurse -Force }


