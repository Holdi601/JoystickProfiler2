# Several actions from one relation to another (the relation editor's multi-select, docs/relation-multi-select.md;
# dot-sourced by engine_smoke_test.ps1: Run-Engine, Assert-*, $tempDir).
#   - relations item copy-to: the actions with their aircraft (a switched-off one stays off) into another relation of the
#     same kind; the source keeps them; an action the target has gains the missing aircraft; another kind is refused
#     unless --force; one profile version (undo takes it back);
#   - relations item move-to: --to an existing relation, --new a new relation of the source's kind without a bind,
#     --delete-empty deletes the emptied source, a taken name is refused;
#   - relations mass move-actions: every action of several relations into one, --delete-empty deletes the emptied ones.
$rtDir = Join-Path $tempDir "relation-transfer"
New-Item -ItemType Directory -Force -Path $rtDir | Out-Null
$rtProfile = Join-Path $rtDir "transfer.pr0file"
$rtDev = "Transfer Stick {CCCCCCCC-3333-0000-0000-000000000001}"
$rtBase = @("--profile", $rtProfile, "--remap-missing-devices", "false")
Assert-True ((Run-Engine @("profile", "new", $rtProfile)).ExitCode -eq 0) "profile new (transfer) failed."
foreach ($rtRel in @(
        @("Weapons", "--button", "JOY_BTN1", @("DCS:d3001pnilu3001cd2vd1vpnilvunil:F-16C_50|A-10C_2", "DCS:d3002pnilu3002cd2vd1vpnilvunil:F-16C_50")),
        @("Gear", "--button", "JOY_BTN2", @("DCS:d68pnilunilcdnilvdnilvpnilvunil:F-16C_50")),
        @("Lights", "--button", "", @("DCS:d145pnilunilcdnilvdnilvpnilvunil:A-10C_2")),
        @("Pitch", "--axis", "JOY_Y", @("DCS:a2001cdnil:F-16C_50")))) {
    $rtArgs = $rtBase + @("relations", "add", $rtRel[0], $rtRel[1])
    foreach ($rtNode in $rtRel[3]) { $rtArgs += @("--node", $rtNode) }
    if ($rtRel[2].Length -gt 0) { $rtArgs += @("--device", $rtDev, "--input", $rtRel[2]) }
    $rtAdd = Run-Engine ($rtArgs + @("--text"))
    Assert-True ($rtAdd.ExitCode -eq 0) ("relations add " + $rtRel[0] + " failed: " + $rtAdd.Output)
}
# A-10C_2 switched off in Weapons' first action: the copy keeps it off.
Assert-True ((Run-Engine ($rtBase + @("relations", "item", "disable", "Weapons", "--node", "DCS:d3001pnilu3001cd2vd1vpnilvunil:A-10C_2"))).ExitCode -eq 0) "item disable failed."

# copy-to: both actions into Gear; Weapons keeps them; one undo takes the copy back.
$rtCopy = Run-Engine ($rtBase + @("relations", "item", "copy-to", "Weapons", "--node", "DCS:d3001pnilu3001cd2vd1vpnilvunil", "--node", "DCS:d3002pnilu3002cd2vd1vpnilvunil", "--to", "Gear", "--text"))
Assert-True ($rtCopy.ExitCode -eq 0) ("relations item copy-to failed: " + $rtCopy.Output)
Assert-Match $rtCopy.Output "Copied 2 action\(s\) from 'Weapons' to 'Gear'" "copy-to should name what it copied."
$rtGear = (Run-Engine ($rtBase + @("relations", "show", "Gear", "--text"))).Output
Assert-Match $rtGear "d3001pnilu3001cd2vd1vpnilvunil" "Gear should have the copied action."
Assert-Match $rtGear "d3002pnilu3002cd2vd1vpnilvunil" "Gear should have the second copied action."
$rtGearJson = (Run-Engine ($rtBase + @("relations", "show", "Gear"))).Output | ConvertFrom-Json
$rtCopied = @($rtGearJson.Nodes | Where-Object { $_.Id -eq "d3001pnilu3001cd2vd1vpnilvunil" })[0]
Assert-True ($null -ne $rtCopied) "the copied action is missing in the JSON of Gear."
Assert-True ($rtCopied.Aircraft."A-10C_2" -eq $false) "the switched-off aircraft must stay off in the copy."
Assert-True ($rtCopied.Aircraft."F-16C_50" -eq $true) "the switched-on aircraft must stay on in the copy."
Assert-Match (Run-Engine ($rtBase + @("relations", "show", "Weapons", "--text"))).Output "d3002pnilu3002cd2vd1vpnilvunil" "copy-to must leave the source as it was."
Assert-True ((Run-Engine ($rtBase + @("profile", "history", "undo", "--text"))).ExitCode -eq 0) "undo of the copy failed."
Assert-NotMatch (Run-Engine ($rtBase + @("relations", "show", "Gear", "--text"))).Output "d3001pnilu3001cd2vd1vpnilvunil" "one undo takes the whole copy back."

# Another kind is refused (button actions into the axis Pitch), --force takes them.
$rtKind = Run-Engine ($rtBase + @("relations", "item", "copy-to", "Weapons", "--all", "--to", "Pitch", "--text"))
Assert-True ($rtKind.ExitCode -ne 0) "copy-to into a relation of the other kind must be refused."
Assert-Match $rtKind.Output "axis relation" ("the refusal should name the kind: " + $rtKind.Output)
$rtSelf = Run-Engine ($rtBase + @("relations", "item", "copy-to", "Weapons", "--all", "--to", "Weapons", "--text"))
Assert-True ($rtSelf.ExitCode -ne 0) "copy-to into the same relation must be refused."

# move-to an existing relation: the action leaves the source.
$rtMove = Run-Engine ($rtBase + @("relations", "item", "move-to", "Weapons", "--node", "DCS:d3002pnilu3002cd2vd1vpnilvunil", "--to", "Lights", "--text"))
Assert-True ($rtMove.ExitCode -eq 0) ("relations item move-to failed: " + $rtMove.Output)
Assert-NotMatch (Run-Engine ($rtBase + @("relations", "show", "Weapons", "--text"))).Output "d3002pnilu3002cd2vd1vpnilvunil" "move-to should take the action out of the source."
Assert-Match (Run-Engine ($rtBase + @("relations", "show", "Lights", "--text"))).Output "d3002pnilu3002cd2vd1vpnilvunil" "move-to should put the action into the target."

# move-to a new relation: the kind of the source, no bind; a taken name is refused; --delete-empty removes the source.
$rtTaken = Run-Engine ($rtBase + @("relations", "item", "move-to", "Weapons", "--all", "--new", "gear", "--text"))
Assert-True ($rtTaken.ExitCode -ne 0) "move-to --new with a taken name must be refused."
$rtNew = Run-Engine ($rtBase + @("relations", "item", "move-to", "Weapons", "--all", "--new", "Pickle", "--delete-empty", "--text"))
Assert-True ($rtNew.ExitCode -eq 0) ("relations item move-to --new failed: " + $rtNew.Output)
Assert-Match $rtNew.Output "\(new relation\)" "move-to --new should say it made a new relation."
Assert-Match $rtNew.Output "Deleted the relation 'Weapons'" "--delete-empty should delete the emptied source."
$rtList = (Run-Engine ($rtBase + @("relations", "list", "--text"))).Output
Assert-Match $rtList "Pickle" "the new relation should be listed."
Assert-NotMatch $rtList "(?m)^Weapons\b" "the emptied source should be gone."
$rtPickle = (Run-Engine ($rtBase + @("relations", "show", "Pickle"))).Output | ConvertFrom-Json
Assert-True ($rtPickle.IsAxis -eq $false) "the new relation keeps the button kind."
Assert-NotMatch (Run-Engine ($rtBase + @("binds", "list", "--text"))).Output "(?m)^Pickle \|" "the new relation has no bind."

# mass move-actions: Lights and Pickle into Gear; the emptied relations go.
$rtMass = Run-Engine ($rtBase + @("relations", "mass", "move-actions", "--relations", "Lights,Pickle", "--to", "Gear", "--delete-empty", "--text"))
Assert-True ($rtMass.ExitCode -eq 0) ("relations mass move-actions failed: " + $rtMass.Output)
$rtGearAfter = (Run-Engine ($rtBase + @("relations", "show", "Gear", "--text"))).Output
foreach ($rtId in @("d68pnilunilcdnilvdnilvpnilvunil", "d145pnilunilcdnilvdnilvpnilvunil", "d3001pnilu3001cd2vd1vpnilvunil", "d3002pnilu3002cd2vd1vpnilvunil")) {
    Assert-Match $rtGearAfter $rtId ("Gear should hold " + $rtId + " after move-actions.")
}
$rtListAfter = (Run-Engine ($rtBase + @("relations", "list", "--text"))).Output
Assert-NotMatch $rtListAfter "Lights|Pickle" "the emptied relations should be deleted."
Assert-Match (Run-Engine ($rtBase + @("binds", "list", "--text"))).Output ("(?m)^Gear \| type=button \| device=" + [regex]::Escape($rtDev) + " \| input=JOY_BTN2") "the target keeps its bind."
$rtMixed = Run-Engine ($rtBase + @("relations", "mass", "move-actions", "--relations", "Gear,Pitch", "--new", "Mixed", "--text"))
Assert-True ($rtMixed.ExitCode -ne 0) "move-actions of an axis and a button relation must be refused."
