# Arma Reforger DB refresh from the game's own files: Enfusion PAK archives (FORM/PAC1 with HEAD, DATA and the FILE
# directory tree; stored and zlib entries) holding Configs/System/chimeraInputCommon.conf, keyBindingMenu.conf and
# Language/localization.en_us.conf, plus the user's own input configs. The archives are built here. Runs in a temporary
# JoyPro folder with JOYPRO_DATA_DIR and install detection off, so the shipped catalog and the real game stay untouched.
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir, $engineDir, $engineBin).

function Write-ArpBigEndian([System.IO.BinaryWriter]$Writer, [long]$Value) {
    $Writer.Write([byte](($Value -shr 24) -band 0xFF)); $Writer.Write([byte](($Value -shr 16) -band 0xFF))
    $Writer.Write([byte](($Value -shr 8) -band 0xFF)); $Writer.Write([byte]($Value -band 0xFF))
}

function Get-ArpZlib([byte[]]$Data) {
    $deflated = New-Object System.IO.MemoryStream
    $deflate = New-Object System.IO.Compression.DeflateStream($deflated, [System.IO.Compression.CompressionMode]::Compress, $true)
    $deflate.Write($Data, 0, $Data.Length); $deflate.Dispose()
    [long]$a = 1; [long]$b = 0
    foreach ($byte in $Data) { $a = ($a + $byte) % 65521; $b = ($b + $a) % 65521 }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($out)
    $w.Write([byte]0x78); $w.Write([byte]0x9C); $w.Write($deflated.ToArray()); Write-ArpBigEndian $w (($b -shl 16) -bor $a); $w.Flush()
    return ,$out.ToArray()
}

# Files: archive path -> text; paths in $Compressed are stored with zlib.
function New-ArpPak([string]$Path, [hashtable]$Files, [string[]]$Compressed) {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $dataStart = 12 + 8 + 28 + 8
    $data = New-Object System.IO.MemoryStream
    $entries = @{}
    foreach ($name in ($Files.Keys | Sort-Object)) {
        $raw = $utf8.GetBytes($Files[$name])
        $zip = $Compressed -contains $name
        $stored = if ($zip) { Get-ArpZlib $raw } else { $raw }
        $entries[$name] = @{ Offset = $dataStart + $data.Length; Packed = $stored.Length; Size = $raw.Length; Zip = $zip }
        $data.Write($stored, 0, $stored.Length)
    }
    # Directory tree: folder = 0, name, uint32 count; file = 1, name, offset, stored size, size, 0, 0 0, compression, level, time.
    $root = @{}
    foreach ($name in $entries.Keys) {
        $parts = $name.Split('/'); $node = $root
        for ($i = 0; $i -lt $parts.Length - 1; $i++) { if (-not $node.ContainsKey($parts[$i])) { $node[$parts[$i]] = @{} }; $node = $node[$parts[$i]] }
        $node[$parts[-1]] = $entries[$name]
    }
    $tree = New-Object System.IO.MemoryStream
    $tw = New-Object System.IO.BinaryWriter($tree)
    $writeNode = $null
    $writeNode = {
        param($Name, $Node)
        $nameBytes = $utf8.GetBytes($Name)
        if ($Node.ContainsKey("Offset")) {
            $tw.Write([byte]1); $tw.Write([byte]$nameBytes.Length); $tw.Write($nameBytes)
            $tw.Write([uint32]$Node.Offset); $tw.Write([uint32]$Node.Packed); $tw.Write([uint32]$Node.Size); $tw.Write([uint32]0)
            $tw.Write([byte]0); $tw.Write([byte]0); $tw.Write([byte]$(if ($Node.Zip) { 1 } else { 0 })); $tw.Write([byte]$(if ($Node.Zip) { 6 } else { 0 })); $tw.Write([uint32]1777371140)
        }
        else {
            $tw.Write([byte]0); $tw.Write([byte]$nameBytes.Length); $tw.Write($nameBytes); $tw.Write([uint32]$Node.Count)
            foreach ($child in ($Node.Keys | Sort-Object)) { & $writeNode $child $Node[$child] }
        }
    }
    & $writeNode "" $root
    $tw.Flush()
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($out)
    $ascii = [System.Text.Encoding]::ASCII
    $total = 4 + 8 + 28 + 8 + $data.Length + 8 + $tree.Length
    $w.Write($ascii.GetBytes("FORM")); Write-ArpBigEndian $w $total; $w.Write($ascii.GetBytes("PAC1"))
    $w.Write($ascii.GetBytes("HEAD")); Write-ArpBigEndian $w 28; $w.Write([byte[]](3, 0, 1, 0)); $w.Write((New-Object byte[] 24))
    $w.Write($ascii.GetBytes("DATA")); Write-ArpBigEndian $w $data.Length; $w.Write($data.ToArray())
    $w.Write($ascii.GetBytes("FILE")); Write-ArpBigEndian $w $tree.Length; $w.Write($tree.ToArray())
    $w.Flush()
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
}

$arpInput = @'
ActionManager {
 Actions {
  Action CharacterSmokeJump {
   InputSource InputSourceValue "{0000000000000001}" {
    Input "keyboard:KC_SPACE"
   }
  }
  Action HelicopterSmokeShared {
   InputSource InputSourceSum "{0000000000000002}" {
    Sources {
     InputSourceValue "{0000000000000003}" {
      FilterPreset "toggle"
      Input "keyboard:KC_H"
      Filter InputFilterDown "{0000000000000004}" {
      }
     }
    }
   }
  }
 }
 Contexts {
  ActionContext CharacterGeneralContext {
   Priority 10
   ActionRefs {
    "CharacterSmokeJump"
   }
  }
  ActionContext HelicopterContext {
   Priority 10
   Flags 0x2 0
   Actions {
    Action HelicopterCyclicForward {
     Type AnalogRelative
     Limit 1
     InputSource InputSourceSum "{0000000000000010}" {
      Sources {
       InputSourceValue "{0000000000000011}" {
        FilterPreset "forward"
        Input "keyboard:KC_W"
       }
      }
     }
    }
    Action HelicopterAutohoverToggle {
     InputSource InputSourceSum "{0000000000000012}" {
      Sources {
       InputSourceValue "{0000000000000013}" {
        FilterPreset "click"
        Input "keyboard:KC_T"
       }
      }
     }
    }
    Action HelicopterSmokeFire {
     InputSource InputSourceSum "{0000000000000020}" {
      Sources {
       InputSourceValue "{0000000000000021}" {
        Input "gamepad0:shoulder_right"
        Filter InputFilterPressed "{0000000000000022}" {
         Threshold 0.5
        }
       }
       InputSourceValue "{0000000000000023}" {
        Input "mouse:button0"
        Filter InputFilterPressed "{0000000000000024}" {
         Threshold 0.995
         ActivateDeactivationThreshold 1
        }
       }
      }
     }
    }
    Action HelicopterSmokeZeroing {
     Type AnalogRelative
     InputSource InputSourceSum "{0000000000000030}" {
      Sources {
       InputSourceValue "{0000000000000031}" {
        FilterPreset "up"
        Input "keyboard:KC_PRIOR"
        Filter InputFilterRepeat "{0000000000000032}" {
        }
       }
       InputSourceValue "{0000000000000033}" {
        FilterPreset "gamepad:down"
        Input "gamepad0:pad_down"
        Filter InputFilterRepeat "{0000000000000034}" {
         Multiplier -1
        }
       }
       InputSourceValue "{0000000000000035}" {
        FilterPreset "down"
        Input "keyboard:KC_NEXT"
        Filter InputFilterRepeat "{0000000000000036}" {
         Multiplier -1
        }
       }
      }
     }
    }
   }
   ActionRefs {
    "HelicopterSmokeShared"
   }
  }
  ActionContext AirplaneContext {
   Priority 10
   Actions {
    Action AirplaneSmokeElevator {
     Type AnalogRelative
     InputSource InputSourceSum "{0000000000000040}" {
      Sources {
       InputSourceValue "{0000000000000041}" {
        Input "keyboard:KC_UP"
       }
       InputSourceValue "{0000000000000042}" {
        Input "gamepad0:left_thumb_vertical"
       }
      }
     }
    }
   }
  }
 }
}
'@
$arpMenu = @'
SCR_KeyBindingMenuConfig {
 m_KeyBindingCategories {
  SCR_KeyBindingCategory "{0000000000000100}" {
   m_sName "Helicopter"
   m_KeyBindingEntries {
    SCR_KeyBindingEntry "{0000000000000101}" {
     m_sActionName "separator"
     m_sDisplayName "#AR-Smoke_Long"
    }
    SCR_KeyBindingEntry "{0000000000000102}" {
     m_sActionName "HelicopterSmokeFire"
     m_sDisplayName "#AR-Smoke_Fire"
     m_sPreset "hold"
    }
    SCR_KeyBindingEntry "{0000000000000103}" {
     m_sActionName "HelicopterSmokeZeroing"
     m_sDisplayName "#AR-Smoke_RangeUp"
     m_sPreset "up"
    }
    SCR_KeyBindingEntry "{0000000000000104}" {
     m_sActionName "HelicopterSmokeZeroing"
     m_sDisplayName "#AR-Smoke_Missing"
     m_sPreset "down"
    }
   }
  }
 }
}
'@
# A text continued over two lines ("..."\) and an escaped quote must not shift the ids against the texts.
$arpLocalization = "StringTableRuntime {`n Ids {`n  `"AR-Smoke_Long`"`n  `"AR-Smoke_Fire`"`n  `"AR-Smoke_RangeUp`"`n }`n Texts {`n  `"Line one with \`"quotes\`"`"\`n  `" and line two`"`n  `"Smoke fire`"`n  `"Smoke range up`"`n }`n}`n"

$arpDir = Join-Path $tempDir "reforger-refresh"
$arpGame = Join-Path $arpDir "Arma Reforger"
$arpRoot = Join-Path $arpDir "joypro"
$arpPresets = Join-Path $arpDir "Documents\My Games\ArmaReforger\profile\.save\settings\customInputConfigs"
New-Item -Path (Join-Path $arpGame "addons\data"), (Join-Path $arpGame "addons\core"), (Join-Path $arpRoot "DB\ArmaReforger"), (Join-Path $arpRoot "CleanProfile"), $arpPresets -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $engineBin "DB\ArmaReforger\*") -Destination (Join-Path $arpRoot "DB\ArmaReforger") -Force
New-ArpPak (Join-Path $arpGame "addons\core\data.pak") @{ "configs/inputCommon.conf" = "ActionManager {`n}`n" } @()
New-ArpPak (Join-Path $arpGame "addons\data\data.pak") @{ "Configs/System/chimeraInputCommon.conf" = $arpInput; "Configs/System/keyBindingMenu.conf" = $arpMenu; "Configs/Other/readme.txt" = "x" } @("Configs/System/chimeraInputCommon.conf")
New-ArpPak (Join-Path $arpGame "addons\data\data001.pak") @{ "Language/localization.en_us.conf" = $arpLocalization; "Language/localization.de_de.conf" = "x" } @("Language/localization.en_us.conf")
# The user's own preset names a flight action the game's files do not have (a mod).
[System.IO.File]::WriteAllText((Join-Path $arpPresets "User.conf"), "ActionManager {`n Actions {`n  Action HelicopterSmokeUserOnly {`n   InputSource InputSourceSum `"{0000000000000201}`" {`n    Sources {`n     InputSourceValue `"{0000000000000202}`" {`n      Input `"joystick0:button2`"`n     }`n    }`n   }`n  }`n  Action CharacterSmokeUserOnly {`n  }`n }`n}`n", (New-Object System.Text.UTF8Encoding $false))

# The list the engine reads and the refresh writes: the game-data folder's (<data>\GameData), copied from the program
# folder ($arpRoot) on the engine's first start.
$arpCatalogPath = Join-Path $arpDir "data\GameData\DB\ArmaReforger\actions.tsv"
$arpProgramCatalog = [System.IO.File]::ReadAllText((Join-Path $arpRoot "DB\ArmaReforger\actions.tsv"))
$arpShippedPath = Join-Path $engineBin "DB\ArmaReforger\actions.tsv"
$arpShipped = [System.IO.File]::ReadAllText($arpShippedPath)
$arpPreviousData = $env:JOYPRO_DATA_DIR
$arpPreviousDetection = $env:JOYPRO_INSTALL_DETECTION
$arpPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_DATA_DIR = Join-Path $arpDir "data"
$env:JOYPRO_INSTALL_DETECTION = "off"
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
Push-Location $arpRoot
try {
    $arpSet = Run-Engine @("games", "install", "set", "reforger", $arpGame, "--text")
    Assert-True ($arpSet.ExitCode -eq 0) ("games install set reforger failed: " + $arpSet.Output)
    $arpConfig = Run-Engine @("games", "config", "set", "reforger", $arpPresets, "--text")
    Assert-True ($arpConfig.ExitCode -eq 0) ("games config set reforger failed: " + $arpConfig.Output)
    $arpBefore = [System.IO.File]::ReadAllText($arpCatalogPath)

    $arpDry = Run-Engine @("reforger", "refresh-db", "--dry-run", "--text")
    Assert-True ($arpDry.ExitCode -eq 0) ("reforger refresh-db --dry-run failed: " + $arpDry.Output)
    Assert-True ([System.IO.File]::ReadAllText($arpCatalogPath) -eq $arpBefore) "reforger refresh-db --dry-run must not write the catalog."

    $arp = Run-Engine @("reforger", "refresh-db", "--text")
    Assert-True ($arp.ExitCode -eq 0) ("reforger refresh-db from the game's PAK archives failed: " + $arp.Output)
    Assert-Match $arp.Output "Read 3 of 3 input file\(s\) from 3 PAK archive\(s\)" ("reforger refresh-db should find the input config, keybind menu and string table in the archives: " + $arp.Output)
    Assert-Match $arp.Output "flight action\(s\) in HelicopterContext, AirplaneContext; 5 new" ("reforger refresh-db should read the two flight contexts: " + $arp.Output)
    $arpCatalog = [System.IO.File]::ReadAllText($arpCatalogPath)
    foreach ($line in @(
        "HelicopterSmokeFire`tSmoke fire`tbutton`tHelicopter`tHelicopterSmokeFire|hold|InputFilterPressed|Threshold 0.995;ActivateDeactivationThreshold 1",
        "HelicopterSmokeZeroing.Up`tSmoke range up`tbutton`tHelicopter`tHelicopterSmokeZeroing|up|InputFilterRepeat|",
        "HelicopterSmokeZeroing.Down`tSmoke zeroing (down)`tbutton`tHelicopter`tHelicopterSmokeZeroing|down|InputFilterRepeat|Multiplier -1",
        "HelicopterSmokeShared`tSmoke shared`tbutton`tHelicopter`tHelicopterSmokeShared|toggle|InputFilterDown|",
        "AirplaneSmokeElevator`tSmoke elevator`taxis`tAirplane`r`n",
        "HelicopterSmokeUserOnly`tSmoke user only`tbutton`tHelicopter`tHelicopterSmokeUserOnly|||")) {
        Assert-True ($arpCatalog.Contains($line)) ("the refreshed Reforger catalog should contain '" + $line.Trim() + "': " + $arpCatalog)
    }
    Assert-NotMatch $arpCatalog "CharacterSmoke" "actions of non-flight contexts (and non-flight actions of the user's configs) must not become catalog actions."
    Assert-NotMatch $arpCatalog "(?m)^HelicopterCyclicForward\t" "the half axes stay inside JoyPro's composite axis rows."
    foreach ($known in ($arpBefore -split "`r?`n" | Where-Object { $_ -match "^[A-Za-z]" })) {
        Assert-True ($arpCatalog.Contains($known + "`r`n")) ("a known Reforger action must keep its id, title and category: " + $known)
    }
    Assert-True ([System.IO.File]::ReadAllText($arpShippedPath) -eq $arpShipped) "the shipped Reforger catalog must stay untouched."
    Assert-True ([System.IO.File]::ReadAllText((Join-Path $arpRoot "DB\ArmaReforger\actions.tsv")) -eq $arpProgramCatalog) "the refresh must not write into the program folder."

    $arpAgain = Run-Engine @("reforger", "refresh-db", "--text")
    Assert-True ($arpAgain.ExitCode -eq 0) ("a second reforger refresh-db failed: " + $arpAgain.Output)
    Assert-Match $arpAgain.Output "nothing was written" ("a second reforger refresh-db must write nothing: " + $arpAgain.Output)
    Assert-True ([System.IO.File]::ReadAllText($arpCatalogPath) -eq $arpCatalog) "a second reforger refresh-db must leave the catalog byte-identical."

    # The refreshed actions export with the filter of the game's own source and read back to their rows.
    $arpProfile = Join-Path $arpDir "refresh.pr0file"
    $arpStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    Run-Engine @("--profile", $arpProfile, "relations", "add", "Smoke Range Down", "--button", "--node", "ArmaReforger:HelicopterSmokeZeroing.Down", "--device", $arpStick, "--input", "JOY_BTN5", "--text") | Out-Null
    Run-Engine @("--profile", $arpProfile, "relations", "add", "Smoke Range Up", "--button", "--node", "ArmaReforger:HelicopterSmokeZeroing.Up", "--device", $arpStick, "--input", "JOY_BTN6", "--text") | Out-Null
    Run-Engine @("--profile", $arpProfile, "relations", "add", "Smoke Elevator", "--axis", "--node", "ArmaReforger:AirplaneSmokeElevator", "--device", $arpStick, "--input", "JOY_Y", "--text") | Out-Null
    $arpPreset = Join-Path $arpPresets "JoyPro.conf"
    $arpExport = Run-Engine @("--profile", $arpProfile, "reforger", "export", $arpPreset, "--text")
    Assert-True ($arpExport.ExitCode -eq 0) ("reforger export of refreshed actions failed: " + $arpExport.Output)
    $arpWritten = [System.IO.File]::ReadAllText($arpPreset)
    Assert-Match $arpWritten '(?s)Action HelicopterSmokeZeroing \{.*FilterPreset "down"\s*Input "joystick0:button4"\s*Filter InputFilterRepeat "\{[0-9A-F]{16}\}" \{\s*Multiplier -1\s*\}' "the down row should be written with the game's down filter (InputFilterRepeat, Multiplier -1)."
    Assert-Match $arpWritten 'FilterPreset "up"\s*Input "joystick0:button5"\s*Filter InputFilterRepeat "\{[0-9A-F]{16}\}" \{\s*\}' "the up row should go to the same action with the up preset."
    Assert-Match $arpWritten '(?s)Action AirplaneSmokeElevator \{.*Input "joystick0:axis1"' "a refreshed whole-axis action should get the whole axis."
    $arpScan = Run-Engine @("reforger", "scan", $arpPreset, "--text")
    Assert-Match $arpScan.Output "(?m)^Bindings Read:\s+3\s*$" ("reforger scan should read the three refreshed binds back: " + $arpScan.Output)
    Assert-Match $arpScan.Output "HelicopterSmokeZeroing\.Down <- Smoke Stick \{11111111-2222-3333-4444-555555555555\} JOY_BTN5" "the down source should read back to its own row."
}
finally {
    Pop-Location
    $env:JOYPRO_DATA_DIR = $arpPreviousData
    $env:JOYPRO_INSTALL_DETECTION = $arpPreviousDetection
    $env:JOYPRO_FAKE_DEVICES = $arpPreviousFakeDevices
}

# Arma Reforger export modes (only helicopter Action blocks; infantry and vehicle blocks never change):
#   add       keeps every source; a bind on a joystick input another helicopter action uses is skipped and reported
#   merge     removes the other action's source on the profile's input (conflict), keeps the action's old source
#   merge-overwrite  merge + the action loses its other joystick sources of connected devices (keyboard stays)
$armDir = Join-Path $tempDir "reforger-modes"
New-Item -Path $armDir -ItemType Directory -Force | Out-Null
$armPreset = Join-Path $armDir "Modes.conf"
$armText = [System.IO.File]::ReadAllText((Join-Path $engineDir "tests\fixtures\reforger\Smoke.conf"))
$armText = $armText.Replace("      Input `"joystick0:button1`"`n     }", "      Input `"joystick0:button1`"`n     }`n     InputSourceValue `"{0000000000040001}`" {`n      FilterPreset `"click`"`n      Input `"keyboard:KC_H`"`n     }")
[System.IO.File]::WriteAllText($armPreset, $armText)
$armProfile = Join-Path $armDir "modes.pr0file"
function Get-ArmNonFlight([string]$Text) { return [regex]::Match($Text, '(?s)  Action CharacterFire \{.*?\n  \}').Value }
$armNonFlight = Get-ArmNonFlight $armText
$armPreviousFakeDevices = $env:JOYPRO_FAKE_DEVICES
$env:JOYPRO_FAKE_DEVICES = Join-Path $engineDir "tests\fixtures\wardogs\devices.tsv"
try {
    Assert-True ($armText.Contains("keyboard:KC_H") -and $armNonFlight.Length -gt 0) "the reforger mode fixture should have the keyboard source and the infantry block."
    $armStick = "Smoke Stick {11111111-2222-3333-4444-555555555555}"
    $armThrottle = "Smoke Throttle {66666666-7777-8888-9999-000000000000}"
    # Moved: autohover from stick button 2 to button 4 (it also has keyboard H). Conflict: landing light on throttle
    # button 18 (engine start's). Wheel brake on stick button 1: the infantry fire (not a helicopter action).
    Run-Engine @("--profile", $armProfile, "relations", "add", "ARM Hover", "--button", "--node", "ArmaReforger:HelicopterAutohoverToggle", "--device", $armStick, "--input", "JOY_BTN4", "--text") | Out-Null
    Run-Engine @("--profile", $armProfile, "relations", "add", "ARM Light", "--button", "--node", "ArmaReforger:HelicopterLightsLandingToggle", "--device", $armThrottle, "--input", "JOY_BTN18", "--text") | Out-Null
    Run-Engine @("--profile", $armProfile, "relations", "add", "ARM Brake", "--button", "--node", "ArmaReforger:HelicopterWheelBrake", "--device", $armStick, "--input", "JOY_BTN1", "--text") | Out-Null

    $armAdd = Run-Engine @("--profile", $armProfile, "reforger", "export", $armPreset, "--mode", "add")
    Assert-True ($armAdd.ExitCode -eq 0) ("reforger add export failed: " + $armAdd.Output)
    $armAddJson = ($armAdd.Output | Out-String | ForEach-Object { $_.Substring($_.IndexOf("{")) } | ConvertFrom-Json)
    Assert-True (@($armAddJson.SkippedTakenInputs | Where-Object { $_.Action -eq "HelicopterLightsLandingToggle" -and $_.GameAction -eq "HelicopterEngineStart" }).Count -eq 1) ("reforger add should report the landing light on engine start's button: " + $armAdd.Output)
    $armAfter = [System.IO.File]::ReadAllText($armPreset)
    Assert-Match $armAfter '(?s)Action HelicopterAutohoverToggle \{.*joystick0:button1".*keyboard:KC_H".*joystick0:button3".*Action HelicopterEngineStart' "reforger add should keep autohover's sources and add the new one."
    Assert-Match $armAfter '"\{0000000000050000\}"' "reforger add must keep engine start."
    Assert-NotMatch $armAfter "Action HelicopterLightsLandingToggle" "reforger add must not write the landing light on engine start's button."
    Assert-Match $armAfter '(?s)Action HelicopterWheelBrake \{.*Input "joystick0:button0"' "reforger add should write the wheel brake beside the infantry fire."
    Assert-True ((Get-ArmNonFlight $armAfter) -eq $armNonFlight) "reforger add must leave the infantry block identical."

    $armMerge = Run-Engine @("--profile", $armProfile, "reforger", "export", $armPreset, "--mode", "merge", "--text")
    Assert-True ($armMerge.ExitCode -eq 0) ("reforger merge export failed: " + $armMerge.Output)
    $armAfter = [System.IO.File]::ReadAllText($armPreset)
    Assert-NotMatch $armAfter "Action HelicopterEngineStart" "reforger merge should remove engine start's source on the landing light's button (conflict)."
    Assert-Match $armAfter '(?s)Action HelicopterLightsLandingToggle \{.*Input "joystick1:button17"' "reforger merge should write the landing light."
    Assert-Match $armAfter '(?s)Action HelicopterAutohoverToggle \{.*joystick0:button1".*keyboard:KC_H".*joystick0:button3"' "reforger merge must keep autohover's old source."
    Assert-True ((Get-ArmNonFlight $armAfter) -eq $armNonFlight) "reforger merge must leave the infantry block identical."

    $armOverwrite = Run-Engine @("--profile", $armProfile, "reforger", "export", $armPreset, "--mode", "merge-overwrite", "--text")
    Assert-True ($armOverwrite.ExitCode -eq 0) ("reforger merge-overwrite export failed: " + $armOverwrite.Output)
    $armAfter = [System.IO.File]::ReadAllText($armPreset)
    Assert-NotMatch $armAfter 'joystick0:button1"' "reforger merge-overwrite should remove autohover's old source."
    Assert-Match $armAfter '(?s)Action HelicopterAutohoverToggle \{.*keyboard:KC_H".*joystick0:button3"' "reforger merge-overwrite must keep the keyboard source."
    Assert-Match $armAfter 'joystick0:axis1-"' "reforger merge-overwrite must keep actions the profile does not bind."
    Assert-True ((Get-ArmNonFlight $armAfter) -eq $armNonFlight) "reforger merge-overwrite must leave the infantry block identical."

    $armClean = Run-Engine @("--profile", $armProfile, "reforger", "export", $armPreset, "--mode", "clean", "--nuke-untouched-devices", "--text")
    Assert-True ($armClean.ExitCode -eq 0) ("reforger clean + nuke export failed: " + $armClean.Output)
    $armAfter = [System.IO.File]::ReadAllText($armPreset)
    Assert-NotMatch $armAfter 'joystick0:axis1-"' "reforger clean should release the written stick's other helicopter sources."
    Assert-True ((Get-ArmNonFlight $armAfter) -eq $armNonFlight) "reforger clean + nuke must leave the infantry block identical."
}
finally {
    $env:JOYPRO_FAKE_DEVICES = $armPreviousFakeDevices
}