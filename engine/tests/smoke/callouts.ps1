# Tables and lines (engine layout callouts / layout render of a device layout with "callouts", docs/device-reference.md).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir). Nothing is downloaded: JoyPro's drawings only.

$coDir = Join-Path $tempDir "callouts"
New-Item -Path $coDir -ItemType Directory -Force | Out-Null
$coPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $coDir "data"
try {
    # Every catalog model's drawing: tables without overlap, orthogonal lines, everything on the canvas, every part linked.
    $coAll = Run-Engine @("layout", "callouts", "--check-all")
    Assert-True ($coAll.ExitCode -eq 0) "layout callouts --check-all found problems: $($coAll.Output)"
    Assert-Match $coAll.Output "(?m)^Problems: 0\r?$" "layout callouts --check-all did not report zero problems."
    $coModels = [int]([regex]::Match($coAll.Output, "Callout Check: (\d+) models").Groups[1].Value)
    Assert-True ($coModels -ge 200) "layout callouts --check-all covered only $coModels models."

    # One sheet: checked, drawn and saved in the shareable format.
    $coDevice = "Joystick - HOTAS Warthog {AAAAAAAA-0000-0000-0000-000000000001}"
    $coPng = Join-Path $coDir "warthog.png"
    $coFile = Join-Path $coDir "warthog.callouts.json"
    $coOne = Run-Engine @("layout", "callouts", "--model", "thrustmaster-warthog-stick", "--device", $coDevice, "--png", $coPng, "--save", $coFile, "--print-width", "0", "--text")
    Assert-True ($coOne.ExitCode -eq 0) "layout callouts failed: $($coOne.Output)"
    Assert-Match $coOne.Output "(?m)^Result: ok" "the Warthog stick's sheet has problems: $($coOne.Output)"
    Assert-Match $coOne.Output "(?m)^  - stick\.hat\.cms \| (left|right|top|bottom) \| CMS hat \| rows 5 \|.*\| line" "the CMS hat has no table with five rows and a line."
    Assert-True (Test-Path $coPng) "layout callouts --png wrote no picture."
    $coSaved = Get-Content -Path $coFile -Raw
    Assert-Match $coSaved '"format": "joypro-device-layout"' "the saved sheet is not a device layout."
    Assert-Match $coSaved '"callouts":' "the saved device layout has no tables."
    $coJson = Run-Engine @("layout", "callouts", "--layout", $coFile)
    Assert-Match $coJson.Output '"Sheet":\{"version":1' "layout callouts JSON lacks the sheet."

    # A keypad (the shipped sheet of WinWing's PFP 4): grid tables, one line each, drawn (no photo here: a warning).
    $coKeypad = Join-Path (Split-Path -Parent $engineDir) "EngineOnly_Runtime\DB\DeviceLayouts\winwing-pfp-4.main.json"
    $coKeypadPng = Join-Path $coDir "pfp4.png"
    $coGrid = Run-Engine @("layout", "callouts", "--layout", $coKeypad, "--png", $coKeypadPng, "--sample", "--print-width", "0", "--text")
    Assert-True ($coGrid.ExitCode -eq 0) "layout callouts of the PFP 4 failed: $($coGrid.Output)"
    Assert-Match $coGrid.Output "(?m)^Result: ok" "the PFP 4's shipped sheet has problems: $($coGrid.Output)"
    Assert-True (([regex]::Matches($coGrid.Output, "(?m)^  - grid:.*\| line")).Count -ge 3) "the PFP 4's keys are not in grid tables with a line: $($coGrid.Output)"
    Assert-True (Test-Path $coKeypadPng) "the PFP 4's sheet was not drawn."
    $coRegrid = Run-Engine @("layout", "callouts", "--layout", $coKeypad, "--arrange", "--text")
    Assert-Match $coRegrid.Output "(?m)^Check: tables [1-9],.*overlaps 0" "arranging the PFP 4 again does not give a few grid tables: $($coRegrid.Output)"

    # CLI parity: the cheat sheet of "layout render" with that file is the same picture.
    $coOut = Join-Path $coDir "render"
    $coRender = Run-Engine @("layout", "render", "--path", $coOut, "--device", $coDevice, "--layout-map", ($coDevice + "=" + $coFile), "--include-planes", "false", "--print-width", "0", "--text")
    Assert-True ($coRender.ExitCode -eq 0) "layout render of a callout layout failed: $($coRender.Output)"
    $coRendered = Get-ChildItem -Path $coOut -Filter "*.png" | Select-Object -First 1
    Assert-True ($null -ne $coRendered) "layout render wrote no picture of the callout layout."
    $coA = [IO.File]::ReadAllBytes($coPng)
    $coB = [IO.File]::ReadAllBytes($coRendered.FullName)
    Assert-True ($coA.Length -eq $coB.Length -and [Linq.Enumerable]::SequenceEqual($coA, $coB)) "layout render and layout callouts drew different pictures of the same sheet."

    # Light style, print width and a page of all devices.
    $coDevice2 = "Throttle - HOTAS Warthog {AAAAAAAA-0000-0000-0000-000000000002}"
    $coFile2 = Join-Path $coDir "throttle.callouts.json"
    $null = Run-Engine @("layout", "callouts", "--model", "thrustmaster-hotas-warthog-throttle", "--save", $coFile2)
    $coOut2 = Join-Path $coDir "render2"
    $coCombined = Run-Engine @("layout", "render", "--path", $coOut2, "--device", $coDevice, "--device", $coDevice2,
        "--layout-map", ($coDevice + "=" + $coFile), "--layout-map", ($coDevice2 + "=" + $coFile2), "--include-planes", "false", "--style", "light", "--combine", "true", "--text")
    Assert-True ($coCombined.ExitCode -eq 0) "layout render --combine failed: $($coCombined.Output)"
    Assert-Match $coCombined.Output "(?m)^Combined Pages Written: 1" "layout render --combine wrote no page of all devices."
    Assert-True (Test-Path (Join-Path $coOut2 "All devices.png")) "the page of all devices is missing."
    Add-Type -AssemblyName System.Drawing
    $coImage = [System.Drawing.Image]::FromFile($coRendered.FullName.Replace($coOut, $coOut2))
    try {
        Assert-True ($coImage.Width -eq 3508) "the printed sheet is not 3508 px wide (A4 at 300 dpi): $($coImage.Width)"
    }
    finally {
        $coImage.Dispose()
    }
    $coBadStyle = Run-Engine @("layout", "render", "--path", $coOut2, "--style", "neon")
    Assert-True ($coBadStyle.ExitCode -ne 0) "layout render accepted --style neon."

    # The menu titles of a device's binds (the Layout page's preview), and wrong arguments.
    $coTitles = Run-Engine @("layout", "titles", "--device", $coDevice)
    Assert-True ($coTitles.ExitCode -eq 0) "layout titles failed: $($coTitles.Output)"
    Assert-Match $coTitles.Output '"Targets":\[' "layout titles JSON lacks the targets."
    $coNoModel = Run-Engine @("layout", "callouts", "--model", "no-such-model")
    Assert-True ($coNoModel.ExitCode -ne 0) "layout callouts accepted an unknown model."
    $coHalf = Run-Engine @("layout", "callouts", "--model", "thrustmaster-warthog-stick", "--game", "DCS")
    Assert-True ($coHalf.ExitCode -ne 0) "layout callouts accepted --game without --plane."

    # The layout file a profile uses for a device (Reset to default clears it, its undo sets it back); the device
    # matches with another GUID.
    $coProfile = Join-Path $coDir "images.pr0file"
    Run-Engine @("--profile", $coProfile, "relations", "add", "Images", "--button", "--node", "DCS:d1:F-16C_50", "--text") | Out-Null
    $coSet = Run-Engine @("--profile", $coProfile, "layout", "image", $coDevice, "--set", "C:\layouts\warthog.layout")
    Assert-True ($coSet.ExitCode -eq 0 -and $coSet.Output -match '"Current":"C:\\\\layouts\\\\warthog.layout"') "layout image --set failed: $($coSet.Output)"
    $coOther = Run-Engine @("--profile", $coProfile, "layout", "image", "Joystick - HOTAS Warthog {BBBBBBBB-0000-0000-0000-000000000002}", "--clear")
    Assert-True ($coOther.ExitCode -eq 0 -and $coOther.Output -match '"Previous":"C:\\\\layouts\\\\warthog.layout"' -and $coOther.Output -match '"Current":""') "layout image --clear (other GUID) failed: $($coOther.Output)"
    $coNone = Run-Engine @("--profile", $coProfile, "layout", "image", $coDevice)
    Assert-True ($coNone.Output -match '"Current":""') "layout image --clear was not saved: $($coNone.Output)"

    # A page with several views (MultiViewLayout): each view drawn on its own, the page, and layout render with it.
    $coSheetA = Join-Path $coDir "view-a.json"
    $coSheetB = Join-Path $coDir "view-b.json"
    Run-Engine @("layout", "callouts", "--model", "thrustmaster-warthog-stick", "--save", $coSheetA, "--text") | Out-Null
    Run-Engine @("layout", "callouts", "--model", "winwing-viperace-ex-joystick-grip", "--save", $coSheetB, "--text") | Out-Null
    $coPage = Join-Path $coDir "page.json"
    $coPageJson = '{"format":"joypro-multiview","version":1,"model":"thrustmaster-warthog-stick","page":"a4-landscape","views":[' +
        '{"sheet":' + (ConvertTo-Json $coSheetA) + ',"x":50,"y":50,"scale":0.4},{"sheet":' + (ConvertTo-Json $coSheetB) + ',"x":1700,"y":50,"scale":0.35}]}'
    [IO.File]::WriteAllText($coPage, $coPageJson)
    $coViews = Join-Path $coDir "views"
    $coPagePng = Join-Path $coDir "page.png"
    $coMulti = Run-Engine @("layout", "multiview", $coPage, "--views", $coViews, "--png", $coPagePng, "--print-width", "1200")
    Assert-True ($coMulti.ExitCode -eq 0 -and (Test-Path (Join-Path $coViews "view-1.png")) -and (Test-Path $coPagePng)) "layout multiview did not draw the views and the page: $($coMulti.Output)"
    Add-Type -AssemblyName System.Drawing
    $coImage = [System.Drawing.Image]::FromFile($coPagePng)
    try { Assert-True ($coImage.Width -eq 1200) "the page was not drawn at the print width." } finally { $coImage.Dispose() }
    $coOut3 = Join-Path $coDir "render-page"
    $coRenderPage = Run-Engine @("layout", "render", "--path", $coOut3, "--layout-map", ($coDevice + "=" + $coPage), "--devices", $coDevice, "--include-planes", "false", "--print-width", "800", "--text")
    Assert-True ($coRenderPage.ExitCode -eq 0 -and @(Get-ChildItem $coOut3 -Filter *.png).Count -eq 1) "layout render did not print the page with several views: $($coRenderPage.Output)"
}
finally {
    $env:JOYPRO_DATA_DIR = $coPreviousData
}
