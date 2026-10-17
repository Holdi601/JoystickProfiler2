# IL-2 Great Battles DB refresh (dot-sourced by engine_smoke_test.ps1; Run-Engine, Assert-*, $tempDir, $engineBin).
# A game folder with data\input\global.actions as the game writes it ("id, input, invert| // description"), a
# current.map with the compiled halves of an axis, and JoyPro's own jp.actions (skipped). New actions are appended to
# DB\IL2\IL2.actions with the game's description; known lines stay as they are; nothing is written when nothing is new.
$irDir = Join-Path $tempDir "il2-refresh"
$irGame = Join-Path $irDir "game"
$irRoot = Join-Path $irDir "joypro"
$irInput = Join-Path $irGame "data\input"
New-Item -Path $irInput, (Join-Path $irRoot "DB\IL2"), (Join-Path $irRoot "CleanProfile") -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\IL2\*") -Destination (Join-Path $irRoot "DB\IL2") -Force
$irAscii = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $irInput "global.actions"),
    "// Input map preset.`r`n// It is not recommended to change anything here,`r`n// but it is still possible on your own risk.`r`n &actions=action,command,invert|`r`n" +
    "rpc_pitch,         key_down/key_up,     0| // Renamed by the game`r`n" +
    "rpc_pitch,         joy0_axis_y,         0|`r`n" +
    "smoke_new_button,  key_j,               0| // Smoke new button`r`n" +
    "smoke_new_axis,    ,                    0| // Smoke new axis`r`n" +
    "rpc_smoke_lever,   ,                    0| // Smoke lever`r`n" +
    "smoke_pair,        key_a/key_b,         0| // Smoke key pair`r`n", $irAscii)
[System.IO.File]::WriteAllText((Join-Path $irInput "current.map"), "action(`"smoke_new_axis_p`",`"State`",`"joy0_axis_z`");`r`n", $irAscii)
[System.IO.File]::WriteAllText((Join-Path $irInput "jp.actions"), "smoke_jp_only, key_k, 0| //b JoyPro file`r`n", $irAscii)
# The engine's first start copies the program folder's list ($irRoot) into the game-data folder (<data>\GameData); the
# refresh reads and writes that copy only.
$irBefore = [System.IO.File]::ReadAllBytes((Join-Path $irRoot "DB\IL2\IL2.actions"))
$irCatalogPath = Join-Path $irDir "data\GameData\DB\IL2\IL2.actions"
$irPreviousData = $env:JOYPRO_DATA_DIR
$irPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$env:JOYPRO_DATA_DIR = Join-Path $irDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
Push-Location $irRoot
try {
    $irSet = Run-Engine @("games", "install", "set", "il2", $irGame, "--text")
    Assert-True ($irSet.ExitCode -eq 0) ("games install set il2 failed: " + $irSet.Output)
    $irDry = Run-Engine @("db", "refresh-il2", "--dry-run", "--text")
    Assert-True ($irDry.ExitCode -eq 0) ("db refresh-il2 --dry-run failed: " + $irDry.Output)
    Assert-Match $irDry.Output "(?m)^New Actions: 4\s*$" ("db refresh-il2 should find the four new actions of the game's file: " + $irDry.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($irCatalogPath)) -eq [Convert]::ToBase64String($irBefore)) "a db refresh-il2 dry run must not write."

    $ir = Run-Engine @("db", "refresh-il2", "--text")
    Assert-True ($ir.ExitCode -eq 0) ("db refresh-il2 from the installation failed: " + $ir.Output)
    $irText = [System.IO.File]::ReadAllText($irCatalogPath)
    Assert-Match $irText "(?m)^smoke_new_button,\s+,\s+0\| //b Smoke new button\r$" "a new button should be appended with the game's description."
    Assert-Match $irText "(?m)^smoke_new_axis,\s+,\s+0\| //a Smoke new axis\r$" "an action current.map compiles as an axis (_p/_n) should be an axis."
    Assert-Match $irText "(?m)^rpc_smoke_lever,\s+,\s+0\| //a Smoke lever\r$" "a new plane control (rpc_) should be an axis."
    Assert-Match $irText "(?m)^smoke_pair,\s+,\s+0\| //a Smoke key pair\r$" "an action with a key pair should be an axis."
    Assert-NotMatch $irText "smoke_jp_only" "JoyPro's own jp.actions must not be read."
    Assert-NotMatch $irText "Renamed by the game" "known actions must keep their title (relation names come from it)."
    $irAfter = [System.IO.File]::ReadAllBytes($irCatalogPath)
    Assert-True ([Convert]::ToBase64String([byte[]]$irAfter[0..($irBefore.Length - 1)]) -eq [Convert]::ToBase64String($irBefore)) "db refresh-il2 must keep every existing byte of the catalog."

    $irAgain = Run-Engine @("il2", "refresh-db", (Join-Path $irInput "global.actions"), "--text")
    Assert-True ($irAgain.ExitCode -eq 0) ("il2 refresh-db with the file failed: " + $irAgain.Output)
    Assert-Match $irAgain.Output "(?m)^New Actions: 0\s*$" ("a second refresh should find nothing new: " + $irAgain.Output)
    Assert-Match $irAgain.Output "(?m)^Written: false" ("a second refresh must not write: " + $irAgain.Output)
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($irCatalogPath)) -eq [Convert]::ToBase64String($irAfter)) "an unchanged IL-2 catalog must not be rewritten."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $engineBin "DB\IL2\IL2.actions")) -notmatch "smoke_new_button") "the shipped IL-2 catalog must stay untouched."
    Assert-True ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $irRoot "DB\IL2\IL2.actions"))) -eq [Convert]::ToBase64String($irBefore)) "db refresh-il2 must not write into the program folder."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $irPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $irPreviousDetection
}
