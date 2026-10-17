# Device reference pictures (engine devices reference, docs/device-reference.md). Dot-sourced by engine_smoke_test.ps1
# (Run-Engine, Assert-*, $tempDir, $engineDir, $script:EngineExe). Nothing is downloaded; the user's own definitions go
# to a temporary JOYPRO_DATA_DIR.

$drDir = Join-Path $tempDir "devicereference"
$drData = Join-Path $drDir "data"
New-Item -Path (Join-Path $drData "Devices") -ItemType Directory -Force | Out-Null
$drPreviousData = $env:JOYPRO_DATA_DIR
$drPreviousKb = $env:JOYPRO_DEVICE_KB
$env:JOYPRO_DATA_DIR = $drData
$env:JOYPRO_DEVICE_KB = $null
try {
    # Every model of the shipped catalog: the schematic builds and every part sits inside the picture.
    $drCheck = Run-Engine @("devices", "reference", "check", "--text")
    Assert-True ($drCheck.ExitCode -eq 0) ("devices reference check found problems: " + $drCheck.Output)
    Assert-Match $drCheck.Output "(?m)^Problems: 0\r?$" "devices reference check did not report zero problems."
    $drModels = [int]([regex]::Match($drCheck.Output, "Device Reference Check: (\d+) models").Groups[1].Value)
    Assert-True ($drModels -ge 300) "devices reference check covered only $drModels models."

    $drList = Run-Engine @("devices", "reference", "list", "warthog", "--text")
    Assert-True ($drList.ExitCode -eq 0) "devices reference list failed: $($drList.Output)"
    Assert-Match $drList.Output "(?m)^  - thrustmaster-warthog-stick \| .+ \| stick \| grip\.fighter \| inputs \d+/\d+ \| 044F:0402 \| catalog" "devices reference list lacks the Warthog stick."

    # Identification: USB id + name, name only, a renamed device by its USB id, an unknown one.
    $drId = Run-Engine @("devices", "reference", "identify", "--vidpid", "044F:0402", "--name", "HOTAS Warthog Joystick", "--text")
    Assert-Match $drId.Output "(?m)^Match: thrustmaster-warthog-stick \|" "identify did not find the Warthog stick."
    Assert-Match $drId.Output "(?m)^  choice: thrustmaster-fa18c-hornet-grip" "identify did not offer the other grips of the Warthog base."
    $drMcg = Run-Engine @("devices", "reference", "identify", "--vidpid", "231D:0125", "--name", "VKB-Sim Gunfighter Modern Combat Pro Twist", "--text")
    Assert-Match $drMcg.Output "(?m)^Match: vkb-gunfighter-mcg-pro \|" "identify did not tell the MCG Pro from the MCG Ultimate by its name."
    Assert-Match $drMcg.Output "(?m)^Base: vkb-gunfighter-base" "identify did not name the Gunfighter base."
    $drX52 = Run-Engine @("devices", "reference", "identify", "--name", "Saitek X52 Pro Flight Control System", "--text")
    Assert-Match $drX52.Output "(?m)^Match: logitech-x52-pro \|" "identify by name failed for the X52 Pro."
    $drUnknown = Run-Engine @("devices", "reference", "identify", "--vidpid", "1209:F00D", "--name", "Home Cockpit Box XYZ", "--text")
    Assert-Match $drUnknown.Output "(?m)^Match: none" "an unknown device was matched."
    $drJson = Run-Engine @("devices", "reference", "identify", "--vidpid", "044F:0404", "--name", "HOTAS Warthog Throttle")
    Assert-Match $drJson.Output '"Model":"thrustmaster-hotas-warthog-throttle"' "identify JSON lacks the Warthog throttle."
    $drBad = Run-Engine @("devices", "reference", "identify", "--buttons", "x", "--name", "a")
    Assert-True ($drBad.ExitCode -ne 0) "identify accepted --buttons x."

    # Grips on one base told apart by the inputs the device reports (synthetic catalog).
    $drKb = Join-Path $drDir "kb"
    New-Item -Path $drKb -ItemType Directory -Force | Out-Null
    @'
{ "vendor": "Test", "devices": [
  { "id": "t-base", "name": "Test Base", "kind": "base", "identify": { "vidPids": ["AAAA:0002"], "names": ["Test Base"] },
    "axes": [ { "input": "JOY_X", "control": "stick.axis.roll" }, { "input": "JOY_Y", "control": "stick.axis.pitch" } ] },
  { "id": "t-grip-small", "name": "Small Grip", "kind": "stick", "hand": "right", "base": "t-base",
    "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1" }, { "input": "JOY_BTN12", "control": "stick.button.thumb.1" } ] },
  { "id": "t-grip-big", "name": "Big Grip", "kind": "stick", "hand": "right", "base": "t-base",
    "controls": [ { "input": "JOY_BTN1", "control": "stick.trigger.stage1" }, { "input": "JOY_BTN30", "control": "stick.button.thumb.1" } ] } ] }
'@ | Set-Content -Path (Join-Path $drKb "test.json") -Encoding UTF8
    $env:JOYPRO_DEVICE_KB = $drKb
    $drSmall = Run-Engine @("devices", "reference", "identify", "--vidpid", "AAAA:0002", "--name", "Test Base", "--buttons", "12", "--axes", "X,Y", "--text")
    Assert-Match $drSmall.Output "(?m)^Match: t-grip-small \|" "the grip was not told apart by its 12 buttons."
    Assert-Match $drSmall.Output "(?m)^Method: signature" "the grip was not found by its inputs."
    $drBoth = Run-Engine @("devices", "reference", "identify", "--vidpid", "AAAA:0002", "--name", "Test Base", "--text")
    Assert-Match $drBoth.Output "(?m)^Match: grip needed" "a base without input information did not ask for the grip."
    $env:JOYPRO_DEVICE_KB = $null

    # JoyPro's schematic as JSON and SVG.
    $drSvg = Join-Path $drDir "warthog.svg"
    $drShow = Run-Engine @("devices", "reference", "show", "thrustmaster-warthog-stick", "--svg", $drSvg, "--text")
    Assert-True ($drShow.ExitCode -eq 0) "devices reference show failed: $($drShow.Output)"
    Assert-Match $drShow.Output "grip\.fighter \| 1000 x 1300" "show did not draw the fighter grip."
    Assert-Match $drShow.Output "(?m)^      JOY_BTN1 = " "show did not list the trigger input."
    Assert-True (Test-Path $drSvg) "show --svg wrote no file."
    Assert-Match ([IO.File]::ReadAllText($drSvg)) "^<svg " "the SVG file is not an SVG."
    $drShowJson = Run-Engine @("devices", "reference", "show", "logitech-x52-pro")
    Assert-Match $drShowJson.Output '"Markers":\[' "show JSON lacks the markers."
    $drMissing = Run-Engine @("devices", "reference", "show", "no-such-model")
    Assert-True ($drMissing.ExitCode -ne 0) "show accepted an unknown model."

    # A user definition (marker assistant / import) in <data>\Devices: listed, used by the catalog for Auto Assign too.
    @'
{ "vendor": "User", "devices": [ { "id": "user-home-box", "name": "Home Box", "kind": "panel", "origin": "user",
  "identify": { "vidPids": [], "names": ["Home Box"] },
  "controls": [ { "input": "JOY_BTN1", "control": "panel.button.1", "label": "MASTER ARM" } ],
  "reference": { "markers": { "panel.button.1": [0.25, 0.5] } } } ] }
'@ | Set-Content -Path (Join-Path $drData "Devices\user-home-box.json") -Encoding UTF8
    $drUser = Run-Engine @("devices", "reference", "list", "home box", "--text")
    Assert-Match $drUser.Output "(?m)^  - user-home-box \| Home Box \| panel \| panel \| inputs 1/1 \| - \| user" "the user's definition is not listed."
    $drUserShow = Run-Engine @("devices", "reference", "show", "user-home-box", "--text")
    Assert-Match $drUserShow.Output "MASTER ARM \[button, face\] at 0\.250,0\.500" "the user's marker position was not used."
    $drKbShow = Run-Engine @("devices", "kb", "show", "user-home-box", "--text")
    Assert-Match $drKbShow.Output 'JOY_BTN1 = panel.button.1 "MASTER ARM"' "devices kb does not know the user's definition."

    # layout suggest for a connected device the catalog knows: JoyPro's drawing with markers on the real parts
    # instead of a placeholder (no picture of another product, nothing downloaded).
    $drFake = Join-Path $drDir "devices.tsv"
    @(
        "# JoyPro device`tVID`tPID`tHID product string`taxes`tbuttons`thats",
        "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000AB}`t044F`t0402`tHOTAS Warthog Joystick`tX,Y`t19`t1"
    ) | Set-Content -Path $drFake -Encoding UTF8
    $drPreviousFake = $env:JOYPRO_FAKE_DEVICES
    $env:JOYPRO_FAKE_DEVICES = $drFake
    try {
        $drLayout = Run-Engine @("layout", "suggest", "--device", "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000AB}", "--text")
        Assert-True ($drLayout.ExitCode -eq 0) "layout suggest for a connected Warthog failed: $($drLayout.Output)"
        Assert-Match $drLayout.Output "Template: JoyPro drawing of Thrustmaster HOTAS Warthog Joystick" "layout suggest did not use JoyPro's drawing of the Warthog."
        Assert-Match $drLayout.Output "Markers: total=\d+, button=19, axis=2, pov=4" "layout suggest did not put a marker on each Warthog input."
        Assert-True (@(Get-ChildItem -Path (Join-Path $drData "AutoLayouts") -Filter "*_drawing.png").Count -ge 1) "the drawing was not written to the data folder."
    }
    finally {
        $env:JOYPRO_FAKE_DEVICES = $drPreviousFake
    }

    # "Which one is it?": a Virpil base that does not name its grip asks, the matching left grip first; a VKB grip
    # named by its device does not ask; an unknown device asks what it is.
    $drWarbrd = Run-Engine @("devices", "reference", "identify", "--vidpid", "3344:80CB", "--name", "L-VPC Stick WarBRD", "--buttons", "31", "--hats", "0", "--axes", "X,Y,Z,RX,RY,SLIDER1", "--text")
    Assert-Match $drWarbrd.Output "(?m)^Ask: grip - Which grip is on your Virpil VPC WarBRD Base\?" "the WarBRD base did not ask for its grip."
    Assert-Match $drWarbrd.Output "(?m)^Ask: .*\r?\n  \d+% virpil-alpha-l-v2 \|" "the Alpha L was not the most likely grip of the left WarBRD."
    Assert-True ($drMcg.Output -notmatch "(?m)^Ask:") "the VKB MCG Pro (named by its device) was asked for."
    $drAskUnknown = Run-Engine @("devices", "reference", "identify", "--vidpid", "1209:F00D", "--name", "Home Cockpit Rudder", "--axes", "X,Y,RZ", "--text")
    Assert-Match $drAskUnknown.Output "(?m)^Ask: unknown - Which device is this\? \(looks like: pedals\)" "an unknown device was not asked for (or its class not guessed)."

    # The photo database and the curated positions.
    $drPhotos = Run-Engine @("devices", "reference", "photos", "--coverage", "--text")
    Assert-True ($drPhotos.ExitCode -eq 0) "devices reference photos failed: $($drPhotos.Output)"
    $drWithPhoto = [int]([regex]::Match($drPhotos.Output, "with a reference photo: (\d+)").Groups[1].Value)
    $drViews = [int]([regex]::Match($drPhotos.Output, "with several views: (\d+)").Groups[1].Value)
    Assert-True ($drWithPhoto -ge 230 -and $drViews -ge 50) "photo coverage dropped: $($drPhotos.Output)"
    $drLayouts = Run-Engine @("devices", "reference", "layout-check")
    Assert-True ($drLayouts.ExitCode -eq 0) "devices reference layout-check found problems: $($drLayouts.Output)"
    Assert-Match $drLayouts.Output "(?m)^Problems: 0\r?$" "layout-check did not report zero problems."
    # JoyPro's own drawings: every drawing checked, one drawn in every style and colour preset (the page colour of the
    # preset in the corner, the drawing's ink on the page), and a callout sheet on it.
    $drDrawings = Run-Engine @("devices", "reference", "drawings", "--check")
    Assert-True ($drDrawings.ExitCode -eq 0) "devices reference drawings --check found problems: $($drDrawings.Output)"
    Assert-Match $drDrawings.Output "(?m)^Problems: 0\r?$" "drawings --check did not report zero problems."
    $drArtCount = [int]([regex]::Match($drDrawings.Output, "Line drawings: (\d+)").Groups[1].Value)
    Assert-True ($drArtCount -ge 100) "only $drArtCount drawings of the devices are shipped."
    $drArtModel = [regex]::Match($drDrawings.Output, "(?m)^  - ([a-z0-9-]+) \| main \| \w+ \| \d+ paths \| positions (\d+)/\2").Groups[1].Value
    Assert-True ($drArtModel.Length -gt 0) "no drawing of a main photo with all its curated positions on it."
    $drPresets = @{ "joypro" = $null; "manual" = "#0B0F14"; "blueprint" = "#1D4E89"; "print" = "#FFFFFF" }
    foreach ($drPreset in $drPresets.Keys) {
        foreach ($drMode in @("lines", "cel")) {
            $drArtPng = Join-Path $drDir ("drawing-" + $drPreset + "-" + $drMode + ".png")
            $drArt = Run-Engine @("devices", "reference", "drawings", "--model", $drArtModel, "--png", $drArtPng, "--light", "--drawing-style", $drMode, "--drawing-preset", $drPreset)
            Assert-True ($drArt.ExitCode -eq 0 -and (Test-Path $drArtPng)) "drawing $drPreset/$drMode was not rendered: $($drArt.Output)"
            $drImage = [System.Drawing.Bitmap]::FromFile($drArtPng)
            try {
                $drCorner = $drImage.GetPixel(1, 1)
                $drHex = "#{0:X2}{1:X2}{2:X2}" -f $drCorner.R, $drCorner.G, $drCorner.B
                $drWant = if ($null -ne $drPresets[$drPreset]) { $drPresets[$drPreset] } else { "#F8FAFC" }
                Assert-True ($drHex -eq $drWant) "drawing $drPreset/$drMode has the page colour $drHex instead of $drWant."
                $drInked = $false
                for ($drX = 0; $drX -lt $drImage.Width -and -not $drInked; $drX += 2) {
                    $drP = $drImage.GetPixel($drX, [int]($drImage.Height / 2))
                    if ([Math]::Abs($drP.R - $drCorner.R) + [Math]::Abs($drP.G - $drCorner.G) + [Math]::Abs($drP.B - $drCorner.B) -gt 120) { $drInked = $true }
                }
                Assert-True $drInked "drawing $drPreset/$drMode shows nothing on the middle row."
            }
            finally { $drImage.Dispose() }
        }
    }
    $drArtSvg = Join-Path $drDir "drawing.svg"
    $drSvg = Run-Engine @("devices", "reference", "drawings", "--model", $drArtModel, "--svg", $drArtSvg, "--drawing-style", "mode=lines;preset=manual")
    Assert-True ($drSvg.ExitCode -eq 0) "drawing SVG failed: $($drSvg.Output)"
    $drSvgText = Get-Content -Raw $drArtSvg
    Assert-True ($drSvgText -match 'fill="#0B0F14"' -and $drSvgText -match 'stroke="#F2F2F2"' -and $drSvgText -notmatch '<image') "the drawing's SVG is not the manual style or holds a picture."
    $drSheetPng = Join-Path $drDir "drawing-sheet.png"
    $drSheet = Run-Engine @("layout", "callouts", "--model", $drArtModel, "--sample", "--png", $drSheetPng, "--print-width", "1200", "--drawing-style", "cel")
    Assert-True ($drSheet.ExitCode -eq 0 -and (Test-Path $drSheetPng)) "layout callouts on JoyPro's drawing failed: $($drSheet.Output)"
    Assert-True ($drSheet.Output -notmatch "no line drawing") "the callout sheet did not find JoyPro's drawing."

    $drNotOfficial = Run-Engine @("devices", "reference", "photo-add", "--model", "vkb-gunfighter-mcg-pro", "--url", "https://example.com/mcg.jpg", "--view", "side")
    Assert-True ($drNotOfficial.ExitCode -ne 0) "photo-add accepted an address that is not on VKB's hosts."

    # A JoyPro 1 layout matches a device by the device name stored in it, not by its GUID, and LEFT/L- are the same
    # hand. The layout is made by the engine itself (a picture of the device), then searched as a JoyPro 1 folder.
    $drPng = Join-Path $drDir "stick.png"
    Add-Type -AssemblyName System.Drawing
    $drBitmap = New-Object System.Drawing.Bitmap 320, 240
    $drBitmap.Save($drPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $drBitmap.Dispose()
    $drOld = Run-Engine @("layout", "suggest", "--device", "LEFT Smoke Stick Thing {11111111-0000-0000-0000-000000000001}", "--backdrop", $drPng, "--apply", "false")
    Assert-True ($drOld.ExitCode -eq 0) "layout suggest --backdrop failed: $($drOld.Output)"
    $drOldFile = Get-ChildItem -Path (Join-Path $drData "AutoLayouts") -Filter "auto_left_smoke_stick_thing*.layout" | Select-Object -First 1
    Assert-True ($null -ne $drOldFile) "the layout of the test stick was not written."
    $drLegacy = Join-Path $drDir "LayoutImages"
    New-Item -Path $drLegacy -ItemType Directory -Force | Out-Null
    Copy-Item $drOldFile.FullName (Join-Path $drLegacy "MyOldStick.layout")
    $drInspect = Run-Engine @("layout", "template-inspect", "--source", (Join-Path $drLegacy "MyOldStick.layout"))
    Assert-Match $drInspect.Output '"Joystick":"LEFT Smoke Stick Thing"' "template-inspect does not report the layout's device."
    # The layout's preview picture goes into <data>\LayoutPreview (JOYPRO_DATA_DIR), not the user's %LOCALAPPDATA%.
    $drPreviews = @(Get-ChildItem -Path (Join-Path $drData "LayoutPreview") -Filter "preview_*.png" -ErrorAction SilentlyContinue)
    Assert-True ($drPreviews.Count -ge 1) "template-inspect did not write its preview into <data>\LayoutPreview."
    Assert-True ($drInspect.Output.Contains(((Join-Path $drData "LayoutPreview") -replace '\\', '\\'))) "template-inspect names a preview outside <data>\LayoutPreview."
    $drMatch = Run-Engine @("layout", "suggest", "--device", "L-Smoke Stick Thing {22222222-0000-0000-0000-000000000002}", "--legacy-layout-dir", $drLegacy, "--apply", "false", "--text")
    Assert-Match $drMatch.Output "Template: MyOldStick" "the old layout was not matched by its device name (other GUID, L- instead of LEFT)."
    Assert-Match $drMatch.Output "Source: legacy-layout-dir" "the old layout does not count as the user's own."
    $drOther = Run-Engine @("layout", "suggest", "--device", "R-Smoke Stick Thing {33333333-0000-0000-0000-000000000003}", "--legacy-layout-dir", $drLegacy, "--apply", "false", "--text")
    Assert-True ($drOther.Output -notmatch "Template: MyOldStick") "the left stick's layout was used for the right stick."
    # Taken over into <data>\Layouts\JoyPro1: found without naming the folder.
    New-Item -Path (Join-Path $drData "Layouts\JoyPro1") -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $drLegacy "MyOldStick.layout") (Join-Path $drData "Layouts\JoyPro1\MyOldStick.layout")
    $drTaken = Run-Engine @("layout", "suggest", "--device", "L-Smoke Stick Thing {22222222-0000-0000-0000-000000000002}", "--apply", "false", "--text")
    Assert-Match $drTaken.Output "Template: MyOldStick" "a layout in <data>\Layouts\JoyPro1 was not found."

    $drBadAction = Run-Engine @("devices", "reference", "paint")
    Assert-True ($drBadAction.ExitCode -ne 0) "an unknown devices reference action was accepted."
}
finally {
    $env:JOYPRO_DATA_DIR = $drPreviousData
    $env:JOYPRO_DEVICE_KB = $drPreviousKb
}
