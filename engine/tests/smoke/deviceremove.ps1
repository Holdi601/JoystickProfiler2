# Remove a device (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir).
# Two devices of the same model (an old instance GUID and the one in use): removing the old one takes only its binds and
# the modifiers on it. The other instance keeps its binds and modifiers, a modifier of the removed device comes off the
# binds of other devices (the bind stays), and a keyboard modifier that only the removed device's binds used is listed
# as "Modifiers Without Binds" (the GUI keeps it in the library).
# Relation merge: relations bound the same keep that bind, different binds go.
$rmDir = Join-Path $tempDir "device-remove"
New-Item -ItemType Directory -Force -Path $rmDir | Out-Null
# The modifier separator of a bind (kept out of the file: Windows PowerShell reads scripts without a BOM as ANSI).
$rmS = [string][char]0x00A7
$rmOld = "Joystick (Remove Keypad) {DDDDDDDD-0000-0000-0000-000000000001}"
$rmKept = "Joystick (Remove Keypad) {DDDDDDDD-0000-0000-0000-000000000002}"
$rmStick = "Remove Other Stick {DDDDDDDD-0000-0000-0000-000000000003}"
$rmProfile = Join-Path $rmDir "remove.pr0file"

$rmSetup = @(
    @("Old Fire", $rmOld, "JOY_BTN1"),
    @("Old Kb", $rmOld, "JOY_BTN3"),
    @("Kept Fire", $rmKept, "JOY_BTN1"),
    @("Kept Hat", $rmKept, "JOY_BTN2"),
    @("Stick Fire", $rmStick, "JOY_BTN1")
)
$rmIndex = 0
foreach ($rmRelation in $rmSetup) {
    $rmIndex++
    $rmAdd = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "relations", "add", $rmRelation[0], "--button", "--node", ("DCS:rm_" + $rmIndex + ":F-16C_50"), "--device", $rmRelation[1], "--input", $rmRelation[2], "--text")
    Assert-True ($rmAdd.ExitCode -eq 0) ("relations add " + $rmRelation[0] + " failed: " + $rmAdd.Output)
}
$rmMods = @(
    @("Kept Fire", ("KeptShift" + $rmS + $rmKept + $rmS + "JOY_BTN20" + $rmS + "False")),
    @("Kept Hat", ("KeptShift" + $rmS + $rmKept + $rmS + "JOY_BTN20" + $rmS + "False")),
    @("Old Kb", ("KbAlt" + $rmS + "Keyboard" + $rmS + "LeftAlt" + $rmS + "False")),
    @("Stick Fire", ("OldShift" + $rmS + $rmOld + $rmS + "JOY_BTN5" + $rmS + "False"))
)
foreach ($rmMod in $rmMods) {
    $rmSet = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "binds", "set", $rmMod[0], "--modifier", $rmMod[1], "--text")
    Assert-True ($rmSet.ExitCode -eq 0) ("binds set " + $rmMod[0] + " with a modifier failed: " + $rmSet.Output)
}

$rmRemove = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "--text", "devices", "remove", $rmOld, "--confirm", "false")
Assert-True ($rmRemove.ExitCode -eq 0) ("devices remove of the old keypad failed: " + $rmRemove.Output)
Assert-Match $rmRemove.Output "(?m)^Removed Binds:\s+2\s*$" ("devices remove must remove exactly the 2 binds of the old instance: " + $rmRemove.Output)
Assert-Match $rmRemove.Output "(?m)^Removed Binds \(Reformers\):\s+1\s*$" ("devices remove must take its modifier off the other device's bind: " + $rmRemove.Output)
Assert-Match $rmRemove.Output "(?m)^Modifiers Without Binds:\s+1\s*$" ("devices remove must list the keyboard modifier that lost its binds: " + $rmRemove.Output)
Assert-True ($rmRemove.Output.Contains("  - KbAlt" + $rmS + "Keyboard" + $rmS + "LeftAlt")) ("devices remove must name KbAlt as without binds: " + $rmRemove.Output)

$rmBinds = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "binds", "list")
Assert-True ($rmBinds.ExitCode -eq 0) ("binds list after devices remove failed: " + $rmBinds.Output)
$rmDoc = $rmBinds.Output | ConvertFrom-Json
$rmByName = @{}
foreach ($rmBind in @($rmDoc.Binds)) { $rmByName[$rmBind.RelationName] = $rmBind }
Assert-True (-not $rmByName.ContainsKey("Old Fire") -and -not $rmByName.ContainsKey("Old Kb")) "devices remove left a bind of the removed instance."
foreach ($rmName in @("Kept Fire", "Kept Hat")) {
    Assert-True ($rmByName.ContainsKey($rmName)) ("devices remove took the bind '" + $rmName + "' of the other instance of the same model.")
    Assert-True ($rmByName[$rmName].Joystick -eq $rmKept) ("bind '" + $rmName + "' moved off the kept instance.")
    Assert-True (@($rmByName[$rmName].AllReformers) -contains ("KeptShift" + $rmS + $rmKept + $rmS + "JOY_BTN20" + $rmS + "False")) ("devices remove took the modifier KeptShift of the kept instance off '" + $rmName + "'.")
}
Assert-True ($rmByName.ContainsKey("Stick Fire")) "devices remove took the bind of another device that only used its modifier."
Assert-True (@($rmByName["Stick Fire"].AllReformers).Count -eq 0) "devices remove left its modifier on another device's bind."

$rmModList = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "--text", "binds", "modifier", "list")
Assert-True ($rmModList.ExitCode -eq 0) ("binds modifier list after devices remove failed: " + $rmModList.Output)
Assert-Match $rmModList.Output "(?m)^\s+- KeptShift \(2\)" ("the kept instance's modifier is gone after devices remove: " + $rmModList.Output)
Assert-NotMatch $rmModList.Output "(?m)^\s+- OldShift" "the removed device's modifier is still in the profile."

# Relation merge: the same bind on both relations stays on the merged relation; different binds cannot be one.
$rmSame = @(@("Same A", "JOY_BTN7"), @("Same B", "JOY_BTN7"), @("Diff A", "JOY_BTN8"), @("Diff B", "JOY_BTN9"))
$rmIndex = 10
foreach ($rmRelation in $rmSame) {
    $rmIndex++
    $rmAdd = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "relations", "add", $rmRelation[0], "--button", "--node", ("DCS:rm_" + $rmIndex + ":F-16C_50"), "--device", $rmKept, "--input", $rmRelation[1], "--text")
    Assert-True ($rmAdd.ExitCode -eq 0) ("relations add " + $rmRelation[0] + " failed: " + $rmAdd.Output)
}
$rmMergeSame = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "relations", "mass", "merge", "Same Merged", "--relations", "Same A,Same B", "--text")
Assert-True ($rmMergeSame.ExitCode -eq 0) ("relations mass merge (same bind) failed: " + $rmMergeSame.Output)
$rmMergeDiff = Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "relations", "mass", "merge", "Diff Merged", "--relations", "Diff A,Diff B", "--text")
Assert-True ($rmMergeDiff.ExitCode -eq 0) ("relations mass merge (different binds) failed: " + $rmMergeDiff.Output)
$rmDoc = (Run-Engine @("--profile", $rmProfile, "--remap-missing-devices", "false", "binds", "list")).Output | ConvertFrom-Json
$rmMerged = @($rmDoc.Binds) | Where-Object { $_.RelationName -eq "Same Merged" }
Assert-True ($null -ne $rmMerged -and $rmMerged.Joystick -eq $rmKept -and $rmMerged.JButton -eq "JOY_BTN7") "relations mass merge dropped the bind both relations had."
Assert-True ($null -eq (@($rmDoc.Binds) | Where-Object { $_.RelationName -eq "Diff Merged" })) "relations mass merge invented a bind from two different binds."
Assert-True (@($rmDoc.Binds | Where-Object { $_.RelationName -eq "Kept Fire" }).Count -eq 1) "relations mass merge touched another relation's bind."
