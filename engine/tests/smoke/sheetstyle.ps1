# The cheat sheet style (docs/ui/layout-redesign.md "Sheet style"): --sheet-style presets and files, the single colour
# options of layout render / layout callouts / layout multiview, a device's own style in its layout (wins over the run's),
# a .layout file's ".style.json", wrong values refused. Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir). Nothing is downloaded: JoyPro's drawings only.

Add-Type -AssemblyName System.Drawing
$ssDir = Join-Path $tempDir "sheetstyle"
New-Item -Path $ssDir -ItemType Directory -Force | Out-Null
$ssPreviousData = $env:JOYPRO_DATA_DIR
$env:JOYPRO_DATA_DIR = Join-Path $ssDir "data"

function Get-SheetPixel {
    param([string]$File, [int]$X, [int]$Y)
    $bitmap = New-Object System.Drawing.Bitmap $File
    try { return $bitmap.GetPixel($X, $Y) } finally { $bitmap.Dispose() }
}

function Assert-Near {
    param($Color, [int]$R, [int]$G, [int]$B, [string]$Message)
    Assert-True ([Math]::Abs($Color.R - $R) -le 12 -and [Math]::Abs($Color.G - $G) -le 12 -and [Math]::Abs($Color.B - $B) -le 12) ($Message + " (got " + $Color + ")")
}

try {
    $ssDevice = "Joystick - HOTAS Warthog {AAAAAAAA-0000-0000-0000-000000000001}"
    $ssSheet = Join-Path $ssDir "warthog.json"

    # A preset by name: the dark sheet's page; the single options over it.
    $ssPng = Join-Path $ssDir "dark.png"
    $ssDark = Run-Engine @("layout", "callouts", "--model", "thrustmaster-warthog-stick", "--sample", "--png", $ssPng, "--save", $ssSheet, "--print-width", "0", "--sheet-style", "dark-sheet", "--text")
    Assert-True ($ssDark.ExitCode -eq 0) "layout callouts --sheet-style dark-sheet failed: $($ssDark.Output)"
    Assert-Near (Get-SheetPixel $ssPng 3 3) 0x0F 0x17 0x2A "the dark sheet preset's page is not dark blue"

    $ssPng2 = Join-Path $ssDir "options.png"
    $ssOptions = Run-Engine @("layout", "callouts", "--layout", $ssSheet, "--sample", "--png", $ssPng2, "--print-width", "0", "--sheet-style", "print",
        "--page-background", "#00FF00", "--table-background", "#FFFFFF", "--table-border", "#000000", "--header-background", "#FF0000", "--header-text", "#FFFFFF",
        "--table-text", "#000000", "--line-color", "#0000FF", "--font-size", "20", "--font-weight", "bold", "--modifier-colors", "LAlt=#38BDF8", "--text")
    Assert-True ($ssOptions.ExitCode -eq 0) "layout callouts with the sheet style options failed: $($ssOptions.Output)"
    Assert-Near (Get-SheetPixel $ssPng2 3 3) 0 255 0 "--page-background was not drawn"

    # Wrong values are refused with the option's name.
    $ssBad = Run-Engine @("layout", "callouts", "--layout", $ssSheet, "--sample", "--png", $ssPng2, "--table-border", "grey")
    Assert-True ($ssBad.ExitCode -ne 0) "a wrong --table-border value was accepted."
    Assert-Match $ssBad.Output "--table-border" "the error does not name --table-border."
    $ssBadWeight = Run-Engine @("layout", "callouts", "--layout", $ssSheet, "--sample", "--png", $ssPng2, "--font-weight", "heavy-ish")
    Assert-True ($ssBadWeight.ExitCode -ne 0) "a wrong --font-weight value was accepted."
    $ssBadFile = Run-Engine @("layout", "callouts", "--layout", $ssSheet, "--sample", "--png", $ssPng2, "--sheet-style", (Join-Path $ssDir "missing.json"))
    Assert-True ($ssBadFile.ExitCode -ne 0) "a missing --sheet-style file was accepted."

    # A style file as --sheet-style.
    $ssStyleFile = Join-Path $ssDir "mine.json"
    '{"format":"joypro-sheet-style","version":1,"name":"Mine","pageBackground":"#FF102030","tableBackground":"#FFEEEEEE","headerBackground":"#FF00AA00"}' | Set-Content -Path $ssStyleFile -Encoding UTF8
    $ssPng3 = Join-Path $ssDir "file.png"
    $ssFile = Run-Engine @("layout", "callouts", "--layout", $ssSheet, "--sample", "--png", $ssPng3, "--print-width", "0", "--sheet-style", $ssStyleFile)
    Assert-True ($ssFile.ExitCode -eq 0) "layout callouts --sheet-style <file> failed: $($ssFile.Output)"
    Assert-Near (Get-SheetPixel $ssPng3 3 3) 0x10 0x20 0x30 "the style file's page colour was not drawn"

    # A device's own style inside its layout wins over the run's style (layout render).
    $ssOwn = Get-Content -Path $ssSheet -Raw
    $ssOwn = $ssOwn -replace '"callouts":\s*\{', '"callouts": { "style": {"format":"joypro-sheet-style","version":1,"pageBackground":"#FFFF00FF"},'
    $ssOwnFile = Join-Path $ssDir "own.json"
    Set-Content -Path $ssOwnFile -Value $ssOwn -Encoding UTF8
    $ssOut = Join-Path $ssDir "render"
    $ssRender = Run-Engine @("layout", "render", "--path", $ssOut, "--device", $ssDevice, "--layout-map", ($ssDevice + "=" + $ssOwnFile), "--include-planes", "false",
        "--print-width", "0", "--page-background", "#00FF00", "--table-background", "#FFFFFF", "--marker-color-button", "#FF0000", "--text")
    Assert-True ($ssRender.ExitCode -eq 0) "layout render with a device's own style failed: $($ssRender.Output)"
    $ssRendered = Get-ChildItem -Path $ssOut -Filter "*.png" | Select-Object -First 1
    Assert-True ($null -ne $ssRendered) "layout render wrote no picture."
    Assert-Near (Get-SheetPixel $ssRendered.FullName 3 3) 255 0 255 "the device's own style did not win over the run's page colour"

    # A .layout picture with its own ".style.json" next to it and the marker colours of the run.
    $ssPicture = Join-Path $ssDir "stick.png"
    $ssBitmap = New-Object System.Drawing.Bitmap 64, 64
    try { $ssBitmap.Save($ssPicture, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $ssBitmap.Dispose() }
    '{"format":"joypro-sheet-style","version":1,"textColor":"#FF00FF00","tableBorder":"#FFFF0000"}' | Set-Content -Path ($ssPicture + ".style.json") -Encoding UTF8
    $ssLegacyOut = Join-Path $ssDir "legacy"
    $ssLegacy = Run-Engine @("layout", "render", "--path", $ssLegacyOut, "--device", "SmokeStick", "--layout-map", ("SmokeStick=" + $ssPicture), "--include-planes", "false",
        "--draw-markers", "true", "--draw-legend-table", "true", "--marker-color-button", "#FF0000", "--marker-color-axis", "#00FF00", "--marker-color-hat", "#0000FF",
        "--marker-color-slider", "#FFFF00", "--label-background", "none", "--outline-color", "none", "--text")
    Assert-True ($ssLegacy.ExitCode -eq 0) "layout render of a picture with its own style file failed: $($ssLegacy.Output)"
    Assert-True (Test-Path (Join-Path $ssLegacyOut "SmokeStick.png")) "layout render of a picture with its own style file wrote no picture."
}
finally {
    $env:JOYPRO_DATA_DIR = $ssPreviousData
}
