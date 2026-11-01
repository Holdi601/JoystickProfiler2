# Engine fixes found while rebuilding the author's 542-relation profile (docs/releases/v2.6.0.md):
#   1. A save of a command that runs without load corrections (devices exchange) keeps the profile's DB fingerprint:
#      the next load still runs the DB correction pass (before: the current fingerprint was stamped, the pass skipped).
#   2. devices exchange vJoy -> vJoy with another GUID (next to a bare "vJoy Device" entry) works like other devices.
#   3. relations import keeps the action info the profile stored with its nodes (titles and categories per aircraft).
#   9. One axis with the plane's throttle and the helicopter's collective of a game without aircraft lists (War Thunder)
#      is no "two actions of one aircraft"; two fixed-wing actions on one relation still are.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir). Needs no real game lists.

$rbDir = Join-Path $tempDir "rebuild251"
New-Item -Path $rbDir -ItemType Directory -Force | Out-Null
$rbUtf8 = New-Object System.Text.UTF8Encoding($false)
$rbPrevious = @{ Data = $env:JOYPRO_DATA_DIR; Index = $env:JOYPRO_ACTION_INDEX_DIR; History = $env:JOYPRO_PROFILE_HISTORY }
$env:JOYPRO_DATA_DIR = Join-Path $rbDir "data"
$env:JOYPRO_PROFILE_HISTORY = $null

function Get-RbJson([string]$ProfileFile, [string[]]$Arguments) {
    $out = Join-Path $rbDir ("result-" + [Guid]::NewGuid().ToString("N") + ".json")
    $run = Run-Engine (@("--profile", $ProfileFile, "--remap-missing-devices", "false", "--out", $out) + $Arguments)
    Assert-True ($run.ExitCode -eq 0) ("engine " + ($Arguments -join " ") + " failed: " + $run.Output)
    return ([System.IO.File]::ReadAllText($out) | ConvertFrom-Json)
}

function New-RbBind([string]$Device, [string]$Button, [string]$Axis) {
    return @{ Joystick = $Device; JAxis = $Axis; JButton = $Button; AllReformers = @(); Curvature = @(0.0); AdditionalImportInfo = ""; AliasJoystick = "" }
}

try {
    $rbVjoyOld = "vJoy Device {1478D3D0-F185-11EC-8003-444553540000}"
    $rbVjoyNew = "vJoy Device {89D63500-B53F-11F1-8002-444553540000}"
    $rbDoc = @{
        Relations = @{
            "RB Voice 1" = @{ NAME = "RB Voice 1"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "rb_voice_1"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "RB Voice 2" = @{ NAME = "RB Voice 2"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "rb_voice_2"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            "RB Bare" = @{ NAME = "RB Bare"; ISAXIS = $false; Groups = @(); NODES = @(@{ ID = "rb_bare"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true } }) }
            # Stored action info: DCS titles per aircraft and another game's input.
            "RB Stored" = @{ NAME = "RB Stored"; ISAXIS = $false; Groups = @(); NODES = @(
                @{ ID = "rb_stored_dcs"; Game = "DCS"; AIRCRAFT = @{ "F-16C_50" = $true; "A-10C" = $true }
                   AllInputs = @(@{ ID = "rb_stored_dcs"; Plane = "F-16C_50"; Title = "RB stored title F-16"; IsAxis = $false; Cat = "RB Panel" },
                                 @{ ID = "rb_stored_dcs"; Plane = "A-10C"; Title = "RB stored title A-10"; IsAxis = $false; Cat = "RB Panel" }) },
                @{ ID = "rb_stored_wt"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true }
                   OtherInputs = @(@{ ID = "rb_stored_wt"; Game = "WarThunder"; Plane = "WarThunder"; Title = "RB stored WT title"; IsAxis = $false; categ = "RB WT" }) }) }
        }
        Binds = @{
            "RB Voice 1" = (New-RbBind $rbVjoyOld "JOY_BTN1" "")
            "RB Voice 2" = (New-RbBind $rbVjoyOld "JOY_BTN2" "")
            "RB Bare" = (New-RbBind "vJoy Device" "JOY_BTN3" "")
        }
    }
    $rbProfile = Join-Path $rbDir "rebuild.pr0file"
    [System.IO.File]::WriteAllText($rbProfile, ($rbDoc | ConvertTo-Json -Depth 12), $rbUtf8)

    # ---- 2. vJoy -> vJoy with another GUID (a bare "vJoy Device" is in the profile too)
    $rbExchange = Run-Engine @("--profile", $rbProfile, "--remap-missing-devices", "false", "devices", "exchange", "--old", $rbVjoyOld, "--new", $rbVjoyNew, "--confirm", "false", "--text")
    Assert-True ($rbExchange.ExitCode -eq 0) ("devices exchange vJoy -> vJoy (other GUID) must work: " + $rbExchange.Output)
    $rbBinds = (Get-RbJson $rbProfile @("--load-corrections", "false", "binds", "list")).Binds
    $rbOnNew = @($rbBinds | Where-Object { $_.Joystick -eq $rbVjoyNew })
    Assert-True ($rbOnNew.Count -ge 2) ("devices exchange did not move the vJoy binds to the new GUID (" + (($rbBinds | ForEach-Object { $_.RelationName + "=" + $_.Joystick }) -join "; ") + ").")
    Assert-True (@($rbBinds | Where-Object { $_.Joystick -eq $rbVjoyOld }).Count -eq 0) "devices exchange left binds on the old vJoy GUID."

    # ---- 1. the save of devices exchange keeps the profile's DB fingerprint (none here): the next load corrects
    $rbReport = Join-Path $rbDir "load-report.json"
    $rbLoad = Run-Engine @("--profile", $rbProfile, "--remap-missing-devices", "false", "--correction-report", $rbReport, "profile", "load", $rbProfile, "--text")
    Assert-True ($rbLoad.ExitCode -eq 0) ("profile load after devices exchange failed: " + $rbLoad.Output)
    $rbGate = @(([System.IO.File]::ReadAllText($rbReport) | ConvertFrom-Json).Entries | Where-Object { $_.Kind -eq "db-fingerprint-check" })
    Assert-True ($rbGate.Count -eq 1) "the load report has no DB fingerprint check."
    Assert-True ($rbGate[0].Action -ne "skipped") ("a save without load corrections stamped the current DB fingerprint, the next load skipped the DB correction pass: " + $rbGate[0].Reason)

    # ---- 3. relations import keeps the stored action info
    $rbStoredProfile = Join-Path $rbDir "stored.pr0file"
    [System.IO.File]::WriteAllText($rbStoredProfile, ($rbDoc | ConvertTo-Json -Depth 12), $rbUtf8)
    $rbExport = Join-Path $rbDir "relations.json"
    $rbExportRun = Run-Engine @("--profile", $rbStoredProfile, "--load-corrections", "false", "relations", "export", $rbExport)
    Assert-True ($rbExportRun.ExitCode -eq 0) ("relations export failed: " + $rbExportRun.Output)
    $rbImportRun = Run-Engine @("--profile", $rbStoredProfile, "--load-corrections", "false", "relations", "import", $rbExport)
    Assert-True ($rbImportRun.ExitCode -eq 0) ("relations import failed: " + $rbImportRun.Output)
    $rbShown = Get-RbJson $rbStoredProfile @("--load-corrections", "false", "relations", "show", "RB Stored")
    $rbRelation = if ($rbShown.Relation) { $rbShown.Relation } else { $rbShown }
    $rbDcsNode = @($rbRelation.Nodes | Where-Object { $_.Id -eq "rb_stored_dcs" })[0]
    $rbWtNode = @($rbRelation.Nodes | Where-Object { $_.Id -eq "rb_stored_wt" })[0]
    Assert-True ($null -ne $rbDcsNode -and $rbDcsNode.StoredTitles."F-16C_50" -eq "RB stored title F-16" -and $rbDcsNode.StoredTitles."A-10C" -eq "RB stored title A-10") ("relations import dropped the stored DCS titles: " + ($rbDcsNode | ConvertTo-Json -Depth 5 -Compress))
    Assert-True ($rbDcsNode.StoredCategories."F-16C_50" -eq "RB Panel") "relations import dropped the stored DCS category."
    Assert-True ($null -ne $rbWtNode -and $rbWtNode.StoredTitles.WarThunder -eq "RB stored WT title") ("relations import dropped the other game's stored input: " + ($rbWtNode | ConvertTo-Json -Depth 5 -Compress))

    # ---- 9. throttle (planes) and collective (helicopters) of War Thunder on one axis: no conflict
    $env:JOYPRO_ACTION_INDEX_DIR = Join-Path $engineDir "tests\fixtures\function-map\DB"
    $rbWtDoc = @{
        Relations = @{
            "RB Throttle" = @{ NAME = "RB Throttle"; ISAXIS = $true; Groups = @(); NODES = @(
                @{ ID = "throttle"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } },
                @{ ID = "helicopter_collective"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
            "RB Two Plane Axes" = @{ NAME = "RB Two Plane Axes"; ISAXIS = $true; Groups = @(); NODES = @(
                @{ ID = "zoom"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } },
                @{ ID = "trim_elevator"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
            # The same in two relations on one input: plane + helicopter is no conflict, plane + plane is.
            "RB Plane Throttle" = @{ NAME = "RB Plane Throttle"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "throttle"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
            "RB Heli Collective" = @{ NAME = "RB Heli Collective"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "helicopter_collective"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
            "RB Plane Zoom" = @{ NAME = "RB Plane Zoom"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "zoom"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
            "RB Plane Trim" = @{ NAME = "RB Plane Trim"; ISAXIS = $true; Groups = @(); NODES = @(@{ ID = "trim_elevator"; Game = "WarThunder"; AIRCRAFT = @{ "WarThunder" = $true } }) }
        }
        Binds = @{
            "RB Throttle" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_Z")
            "RB Two Plane Axes" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_RX")
            "RB Plane Throttle" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_SLIDER1")
            "RB Heli Collective" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_SLIDER1")
            "RB Plane Zoom" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_SLIDER2")
            "RB Plane Trim" = (New-RbBind "RB Throttle Quadrant {00000000-0000-0000-0000-0000000000e1}" "" "JOY_SLIDER2")
        }
    }
    $rbWtProfile = Join-Path $rbDir "warthunder.pr0file"
    [System.IO.File]::WriteAllText($rbWtProfile, ($rbWtDoc | ConvertTo-Json -Depth 10), $rbUtf8)
    $rbConflicts = Get-RbJson $rbWtProfile @("--load-corrections", "false", "relations", "conflicts")
    $rbActionConflicts = @($rbConflicts.ActionConflicts)
    Assert-True (@($rbActionConflicts | Where-Object { $_.Relation -eq "RB Throttle" }).Count -eq 0) "War Thunder's plane throttle and helicopter collective on one axis were reported as two actions of one aircraft."
    Assert-True (@($rbActionConflicts | Where-Object { $_.Relation -eq "RB Two Plane Axes" }).Count -eq 1) "two fixed-wing actions of War Thunder on one axis must still be reported."
    $rbInputConflicts = @($rbConflicts.InputConflicts)
    Assert-True (@($rbInputConflicts | Where-Object { $_.Input -eq "JOY_SLIDER1" }).Count -eq 0) "the plane throttle and the helicopter collective of War Thunder in two relations on one axis were reported as one input doing two things."
    Assert-True (@($rbInputConflicts | Where-Object { $_.Input -eq "JOY_SLIDER2" }).Count -eq 1) "two fixed-wing axes of War Thunder in two relations on one input must still be reported."
    $rbValidate = Run-Engine @("--profile", $rbWtProfile, "--load-corrections", "false", "profile", "validate", "--text")
    Assert-NotMatch $rbValidate.Output "multiple active items for the plane WarThunder on the Relation RB Throttle" "profile validate still reports the throttle/collective axis."
    Assert-Match $rbValidate.Output "multiple active items for the plane WarThunder on the Relation RB Two Plane Axes" "profile validate lost the real two-actions error."
}
finally {
    $env:JOYPRO_DATA_DIR = $rbPrevious.Data
    $env:JOYPRO_ACTION_INDEX_DIR = $rbPrevious.Index
    $env:JOYPRO_PROFILE_HISTORY = $rbPrevious.History
}
