# Several inputs per relation (experimental, docs/multi-bind.md; dot-sourced by engine_smoke_test.ps1: Run-Engine,
# Assert-*, $tempDir, $engineDir).
#   - setting off (no --multi-bind): binds extra add is refused, list works, exports write the main bind only and name
#     the extra inputs once; a profile without extra inputs is saved byte for byte as before (no new member);
#   - setting on: add / set / move / remove with numbers 2.., binds list and relations show list them, binds tuning
#     --bind 2 tunes the extra input alone, history undo takes an added input back out;
#   - checks: a relation's own extra input is no conflict, another relation on that input is;
#   - DCS export writes the extra input into its device's file, a re-import (JoyPro's .jp) gives it back as an extra
#     input; Star Citizen (one joystick input per action) writes the main bind and names the relation;
#   - devices copy-to: device A's binds get device B's matching inputs (preview changes nothing, apply is one version).
$mbDir = Join-Path $tempDir "multi-bind"
New-Item -ItemType Directory -Force -Path $mbDir | Out-Null
$mbA = "Grip A {AAAAAAAA-1111-0000-0000-000000000001}"
$mbB = "MOZA AB9 Base {BBBBBBBB-2222-0000-0000-000000000001}"
$mbProfile = Join-Path $mbDir "multi.pr0file"
$mbPreviousData = $env:JOYPRO_DATA_DIR
$mbPreviousFake = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = Join-Path $mbDir "data"
$mbDevices = Join-Path $mbDir "devices.tsv"
[System.IO.File]::WriteAllText($mbDevices, ($mbA + "`t3344`t8130`tL-VPC Stick MT-50CM2`tX,Y`t31`t0`r`n") + ($mbB + "`t346E`t1000`tMOZA AB9 Base`tX,Y`t32`t1`r`n"), (New-Object System.Text.UTF8Encoding $false))
$env:JOYPRO_FAKE_DEVICES = $mbDevices
try {
    $mbNew = Run-Engine @("profile", "new", $mbProfile)
    Assert-True ($mbNew.ExitCode -eq 0) ("profile new failed: " + $mbNew.Output)
    foreach ($mbRel in @(@("Weapon Release", "--button", "DCS:d3001pnilu3001cd2vd1vpnilvunil:F-16C_50", "JOY_BTN2"), @("Pitch", "--axis", "DCS:a2001cdnil:F-16C_50", "JOY_Y"))) {
        $mbAdd = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "add", $mbRel[0], $mbRel[1], "--node", $mbRel[2], "--device", $mbA, "--input", $mbRel[3], "--text")
        Assert-True ($mbAdd.ExitCode -eq 0) ("relations add " + $mbRel[0] + " failed: " + $mbAdd.Output)
    }
    $mbNode = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "item", "add", "Weapon Release", "--node", "StarCitizen:v_attack1_group1", "--force", "--text")
    Assert-True ($mbNode.ExitCode -eq 0) ("relations item add (Star Citizen) failed: " + $mbNode.Output)
    $mbBase = Join-Path $mbDir "base.pr0file"
    Copy-Item $mbProfile $mbBase -Force
    $mbBaseSaved = Join-Path $mbDir "base-saved.pr0file"
    Assert-True ((Run-Engine @("--profile", $mbBase, "--remap-missing-devices", "false", "profile", "save", $mbBaseSaved)).ExitCode -eq 0) "profile save of the base failed."
    Assert-True (-not ([System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($mbBaseSaved)).Contains("ExtraBinds"))) "a profile without extra inputs must not carry the ExtraBinds member."

    # Setting off: changing is refused, listing works.
    $mbOff = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "extra", "add", "Weapon Release", "--device", $mbB, "--input", "JOY_BTN2", "--text")
    Assert-True ($mbOff.ExitCode -ne 0) "binds extra add must be refused with the setting off."
    Assert-Match $mbOff.Output "experimental setting" "the refusal should name the experimental setting."

    # Setting on: add (button with a modifier, axis with its own dead zone), list, set, move, remove.
    $mbOn = @("--multi-bind", "on", "--profile", $mbProfile, "--remap-missing-devices", "false")
    $mbAddB = Run-Engine ($mbOn + @("binds", "extra", "add", "Weapon Release", "--device", $mbB, "--input", "JOY_BTN2", "--modifier", "RShift", "--text"))
    Assert-True ($mbAddB.ExitCode -eq 0) ("binds extra add failed: " + $mbAddB.Output)
    Assert-Match $mbAddB.Output "Added input 2" "binds extra add should name the new input 2."
    $mbAddAxis = Run-Engine ($mbOn + @("binds", "extra", "add", "Pitch", "--device", $mbB, "--input", "JOY_Y", "--deadzone", "0.2", "--text"))
    Assert-True ($mbAddAxis.ExitCode -eq 0) ("binds extra add of an axis failed: " + $mbAddAxis.Output)
    $mbDup = Run-Engine ($mbOn + @("binds", "extra", "add", "Weapon Release", "--device", $mbA, "--input", "JOY_BTN2", "--text"))
    Assert-True ($mbDup.ExitCode -ne 0) "the main input again must be refused."
    $mbList = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "list", "--text")
    Assert-Match $mbList.Output ("\+ input 2: device=" + [regex]::Escape($mbB) + " \| input=JOY_BTN2 \| modifiers\(1\)=RShift") "binds list should list the extra input with its modifier."
    $mbShow = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "show", "Pitch", "--text")
    Assert-Match $mbShow.Output "More inputs \(experimental\): 1" "relations show should count the extra inputs."
    Assert-Match $mbShow.Output "tuning=false/0.2/1/1/0" "the extra axis input keeps its own dead zone."
    $mbTune = Run-Engine ($mbOn + @("binds", "tuning", "set", "Pitch", "--bind", "2", "--scope", "game:DCS", "--curvature", "0.4", "--text"))
    Assert-True ($mbTune.ExitCode -eq 0) ("binds tuning --bind 2 failed: " + $mbTune.Output)
    $mbTuneShow = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "tuning", "show", "Pitch", "--text")
    Assert-NotMatch $mbTuneShow.Output "game:DCS" "binds tuning --bind 2 must leave the main bind's scopes alone."

    # History: the add is one version; undo takes it out, redo brings it back.
    $mbAdd3 = Run-Engine ($mbOn + @("binds", "extra", "add", "Weapon Release", "--device", $mbB, "--input", "JOY_BTN9", "--text"))
    Assert-True ($mbAdd3.ExitCode -eq 0) ("third input failed: " + $mbAdd3.Output)
    $mbUndo = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "profile", "history", "undo", "--text")
    Assert-True ($mbUndo.ExitCode -eq 0) ("history undo failed: " + $mbUndo.Output)
    Assert-NotMatch (Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "list", "--text")).Output "JOY_BTN9" "undo should take the added input out."
    Assert-True ((Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "profile", "history", "redo", "--text")).ExitCode -eq 0) "history redo failed."
    Assert-Match (Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "list", "--text")).Output "input 3: device=.*JOY_BTN9" "redo should bring input 3 back."
    $mbMove = Run-Engine ($mbOn + @("binds", "extra", "move", "Weapon Release", "3", "--to", "2", "--text"))
    Assert-Match $mbMove.Output "2: .*JOY_BTN9" "move --to 2 should reorder the extra inputs."
    $mbRemove = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "binds", "extra", "remove", "Weapon Release", "2", "--text")
    Assert-True ($mbRemove.ExitCode -eq 0) ("binds extra remove (setting off) failed: " + $mbRemove.Output)
    Assert-NotMatch $mbRemove.Output "(?m)^  \d+: .*JOY_BTN9" "remove 2 should take the moved input out."

    # Checks: the own extra input is no conflict; a second relation on it is.
    $mbConf = Run-Engine ($mbOn + @("relations", "conflicts", "--text"))
    Assert-Match $mbConf.Output "Inputs that do two things: 0" "a relation's own extra input must not be a conflict."
    $mbOther = Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "add", "Pickle", "--button", "--node", "DCS:d3001pnilu3001cd2vd1vpnilvunil:F-16C_50", "--device", $mbB, "--input", "JOY_BTN2", "--modifier", "RShift", "--text")
    Assert-True ($mbOther.ExitCode -eq 0) ("relations add Pickle failed: " + $mbOther.Output)
    $mbConf2 = Run-Engine ($mbOn + @("relations", "conflicts", "--text"))
    Assert-Match $mbConf2.Output "Inputs that do two things: 1" "another relation on the extra input must be a conflict."
    $mbValidate = Run-Engine ($mbOn + @("profile", "validate", "--text"))
    Assert-Match $mbValidate.Output "Button Item key found in multiple Relation" "profile validate should name the collision."
    Assert-NotMatch (Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "conflicts", "--text")).Output "Inputs that do two things: 1" "with the setting off the extra input is not checked."
    Assert-True ((Run-Engine @("--profile", $mbProfile, "--remap-missing-devices", "false", "relations", "mass", "delete", "--relation", "Pickle")).ExitCode -eq 0) "deleting Pickle failed."

    # DCS: off = main bind only plus one note; on = both device files; the .jp brings the extra input back.
    $mbPreviousAssume = $env:JOYPRO_TEST_ASSUME_INSTALLED
    $env:JOYPRO_TEST_ASSUME_INSTALLED = "1"
    try {
        foreach ($mbMode in @("off", "on")) {
            $mbSg = Join-Path $mbDir ("dcs-" + $mbMode)
            New-Item -ItemType Directory -Force -Path (Join-Path $mbSg "Config\Input") | Out-Null
            $mbExport = Run-Engine @("--multi-bind", $mbMode, "--profile", $mbProfile, "--remap-missing-devices", "false", "dcs", "export", $mbSg, "--mode", "add", "--text")
            Assert-True ($mbExport.ExitCode -eq 0) ("dcs export (" + $mbMode + ") failed: " + $mbExport.Output)
            $mbBFile = Join-Path $mbSg "Config\Input\F-16C_50\joystick\MOZA AB9 Base {BBBBBBBB-2222-0000-0000-000000000001}.diff.lua"
            if ($mbMode -eq "off") {
                Assert-True (-not (Test-Path $mbBFile)) "with the setting off the extra input's device gets no file."
                Assert-Match $mbExport.Output "extra input\(s\) of 2 relation\(s\) were not exported to DCS" "the export should name the extra inputs it left out."
            }
            else {
                Assert-True (Test-Path $mbBFile) "with the setting on the extra input's device gets its file."
                $mbLua = [System.IO.File]::ReadAllText($mbBFile)
                Assert-Match $mbLua "JOY_BTN2" "the extra button should be in the device's file."
                Assert-Match $mbLua "RShift" "the extra button keeps its modifier."
            }
        }
        $mbBack = Join-Path $mbDir "back.pr0file"
        Assert-True ((Run-Engine @("profile", "new", $mbBack)).ExitCode -eq 0) "profile new (re-import) failed."
        $mbImport = Run-Engine @("--multi-bind", "on", "--profile", $mbBack, "--remap-missing-devices", "false", "dcs", "import", (Join-Path $mbDir "dcs-on"), "--only-connected", "false", "--text")
        Assert-True ($mbImport.ExitCode -eq 0) ("dcs import failed: " + $mbImport.Output)
        $mbBackList = Run-Engine @("--profile", $mbBack, "--remap-missing-devices", "false", "binds", "list", "--text")
        Assert-Match $mbBackList.Output ("(?m)^Weapon Release \| type=button \| device=" + [regex]::Escape($mbA)) "the re-import should keep Weapon Release on device A."
        Assert-Match $mbBackList.Output ("\+ input 2: device=" + [regex]::Escape($mbB) + " \| input=JOY_BTN2 \| modifiers\(1\)=RShift") "the re-import should give the extra input back."
        Assert-NotMatch $mbBackList.Output "Weapon Release__" "the extra input must not become a copied relation."
        $mbBackOff = Join-Path $mbDir "back-off.pr0file"
        Assert-True ((Run-Engine @("profile", "new", $mbBackOff)).ExitCode -eq 0) "profile new (re-import off) failed."
        Assert-True ((Run-Engine @("--profile", $mbBackOff, "--remap-missing-devices", "false", "dcs", "import", (Join-Path $mbDir "dcs-on"), "--only-connected", "false", "--text")).ExitCode -eq 0) "dcs import (off) failed."
        Assert-NotMatch (Run-Engine @("--profile", $mbBackOff, "--remap-missing-devices", "false", "binds", "list", "--text")).Output "\+ input 2" "with the setting off the import makes no extra inputs."

        # Star Citizen keeps one joystick input per action: the main bind and a warning per relation.
        $mbSc = Join-Path $mbDir "sc"
        New-Item -ItemType Directory -Force -Path $mbSc | Out-Null
        Copy-Item (Join-Path $engineDir "tests\smoke\fixtures\starcitizen\actionmaps.xml") (Join-Path $mbSc "actionmaps.xml") -Force
        $mbScExport = Run-Engine ($mbOn + @("starcitizen", "export", $mbSc, "--mode", "clean", "--text"))
        Assert-Match $mbScExport.Output "Star Citizen keeps one joystick input per action: relation 'Weapon Release' was exported with its main input only" "Star Citizen should name the relation whose extra input it cannot hold."
    }
    finally {
        $env:JOYPRO_TEST_ASSUME_INSTALLED = $mbPreviousAssume
    }

    # Join inputs: two relations doing the same on other inputs become one with two inputs (preview first).
    $mbJoin = Join-Path $mbDir "join.pr0file"
    Assert-True ((Run-Engine @("profile", "new", $mbJoin)).ExitCode -eq 0) "profile new (join) failed."
    foreach ($mbRel in @(@("Gear", $mbA, "JOY_BTN5"), @("Gear (2)", $mbB, "JOY_BTN5"))) {
        Assert-True ((Run-Engine @("--profile", $mbJoin, "--remap-missing-devices", "false", "relations", "add", $mbRel[0], "--button", "--node", "DCS:d68pnilunilcdnilvdnilvpnilvunil:F-16C_50", "--device", $mbRel[1], "--input", $mbRel[2], "--text")).ExitCode -eq 0) ("relations add " + $mbRel[0] + " failed.")
    }
    $mbJoinPreview = Run-Engine @("--profile", $mbJoin, "--remap-missing-devices", "false", "relations", "join-inputs", "--text")
    Assert-Match $mbJoinPreview.Output "Would join: 1" "join-inputs should find the two relations."
    $mbJoinApply = Run-Engine @("--multi-bind", "on", "--profile", $mbJoin, "--remap-missing-devices", "false", "relations", "join-inputs", "--apply", "--text")
    Assert-Match $mbJoinApply.Output "Relations removed: 1, inputs added: 1" "join-inputs --apply should keep one relation with two inputs."
    $mbJoinList = Run-Engine @("--profile", $mbJoin, "--remap-missing-devices", "false", "binds", "list", "--text")
    Assert-Match $mbJoinList.Output "Binds: 1" "one relation should be left."

    # Copy to another device: the preview changes nothing; apply adds B's inputs as extra inputs (one version).
    $mbCopy = Join-Path $mbDir "copy.pr0file"
    Copy-Item $mbBase $mbCopy -Force
    $mbCopyBefore = [System.IO.File]::ReadAllBytes($mbCopy)
    $mbCopyDry = Run-Engine @("--profile", $mbCopy, "--remap-missing-devices", "false", "devices", "copy-to", $mbA, $mbB, "--dry-run", "--text")
    Assert-True ($mbCopyDry.ExitCode -eq 0) ("devices copy-to --dry-run failed: " + $mbCopyDry.Output)
    Assert-Match $mbCopyDry.Output "Would copy 2 input\(s\)" "the preview should count both inputs."
    Assert-True ([Convert]::ToBase64String($mbCopyBefore) -eq [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($mbCopy))) "the preview must not change the profile."
    $mbCopyOff = Run-Engine @("--profile", $mbCopy, "--remap-missing-devices", "false", "devices", "copy-to", $mbA, $mbB, "--text")
    Assert-True ($mbCopyOff.ExitCode -ne 0) "devices copy-to must be refused with the setting off."
    $mbCopyApply = Run-Engine @("--multi-bind", "on", "--profile", $mbCopy, "--remap-missing-devices", "false", "devices", "copy-to", $mbA, $mbB, "--text")
    Assert-Match $mbCopyApply.Output "Copied 2 input\(s\)" "copy-to should add both inputs."
    $mbCopyList = Run-Engine @("--profile", $mbCopy, "--remap-missing-devices", "false", "binds", "list", "--text")
    Assert-Match $mbCopyList.Output ("Pitch .*\r?\n\s+\+ input 2: device=" + [regex]::Escape($mbB) + " \| input=JOY_Y") "Pitch should be on B's JOY_Y too."
    Assert-Match $mbCopyList.Output ("Weapon Release .*\r?\n\s+\+ input 2: device=" + [regex]::Escape($mbB) + " \| input=JOY_BTN2") "Weapon Release should be on B's BTN 2 too."
    $mbCopyAgain = Run-Engine @("--multi-bind", "on", "--profile", $mbCopy, "--remap-missing-devices", "false", "devices", "copy-to", $mbA, $mbB, "--dry-run", "--text")
    Assert-Match $mbCopyAgain.Output "Would copy 0 input\(s\).*2 already there" "a second copy finds both inputs there."
    $mbHistory = Run-Engine @("--profile", $mbCopy, "--remap-missing-devices", "false", "profile", "history", "undo", "--text")
    Assert-True ($mbHistory.ExitCode -eq 0) ("undo of the copy failed: " + $mbHistory.Output)
    Assert-NotMatch (Run-Engine @("--profile", $mbCopy, "--remap-missing-devices", "false", "binds", "list", "--text")).Output "\+ input 2" "one undo takes the whole copy back."

    # Clearing the main bind: the next input becomes the relation's bind.
    $mbClear = Join-Path $mbDir "clear.pr0file"
    Copy-Item $mbCopy $mbClear -Force
    Assert-True ((Run-Engine @("--multi-bind", "on", "--profile", $mbClear, "--remap-missing-devices", "false", "binds", "extra", "add", "Weapon Release", "--device", $mbB, "--input", "JOY_BTN7")).ExitCode -eq 0) "add for the clear check failed."
    Assert-True ((Run-Engine @("--profile", $mbClear, "--remap-missing-devices", "false", "binds", "clear", "Weapon Release")).ExitCode -eq 0) "binds clear failed."
    Assert-Match (Run-Engine @("--profile", $mbClear, "--remap-missing-devices", "false", "binds", "list", "--text")).Output ("(?m)^Weapon Release \| type=button \| device=" + [regex]::Escape($mbB) + " \| input=JOY_BTN7") "after clear the extra input should be the bind."
    # Removing every extra input gives the bytes of a profile that never had one.
    $mbStrip = Join-Path $mbDir "strip.pr0file"
    Copy-Item $mbBase $mbStrip -Force
    Assert-True ((Run-Engine @("--multi-bind", "on", "--profile", $mbStrip, "--remap-missing-devices", "false", "binds", "extra", "add", "Pitch", "--device", $mbB, "--input", "JOY_Y")).ExitCode -eq 0) "add for the strip check failed."
    Assert-True ((Run-Engine @("--profile", $mbStrip, "--remap-missing-devices", "false", "binds", "extra", "remove", "Pitch", "--all")).ExitCode -eq 0) "remove --all failed."
    $mbStripSaved = Join-Path $mbDir "strip-saved.pr0file"
    Assert-True ((Run-Engine @("--profile", $mbStrip, "--remap-missing-devices", "false", "profile", "save", $mbStripSaved)).ExitCode -eq 0) "profile save (strip) failed."
    Assert-True (-not ([System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($mbStripSaved)).Contains("ExtraBinds"))) "a profile whose extra inputs were all removed must not carry the member."
}
finally {
    $env:JOYPRO_DATA_DIR = $mbPreviousData
    $env:JOYPRO_FAKE_DEVICES = $mbPreviousFake
}
