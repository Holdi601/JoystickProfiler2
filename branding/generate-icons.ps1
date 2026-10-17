param(
    # The repository root (default: the folder above this script).
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    # The variants (docs/branding/variants*.png): icon A/B/C, LineArt/Formation/Stencil/Hud/Metal/Integrated or
    # FacetA/B/C or AngleA/B/C; title W1/W2/W3, a round 2 concept name, W3FacetA/B/C or W3AngleA/B/C.
    [string]$Icon = "TipBoldAligned",
    [string]$Title = "W3TipBoldAligned",
    # The engine icon's mark: round 8 PromptTopBlue/ArmBlue/CursorBlue/RowBlue, round 9 TraceTopBlue/HookBlue/RowTickBlue/LastBlue/Sketch
    # round 10 Trace10Exact/Large/Ticks3/BarBlue round 11 Trace11Exact/Large/BarBlue
    # or round 12 Trace12T3/T4/T5/T3Up/T4Up/T5Up.
    [string]$Engine = "Trace12T3"
)

# JoyPro's mark, icons and title are vector shapes (branding/generate-branding.py writes
# gui/JoyPro.Gui/Styles/Branding.axaml). This script
#   1. writes Branding.axaml from the shapes with the chosen variants,
#   2. renders the icons from it into branding/base_joypro.ico (mark), gui/JoyPro.Gui/Assets/joypro2_gui.ico
#      (mark + "2": GUI and tray) and engine/assets/engine_console.ico (mark + ">_"), 16 to 256 px, and the previews
#      into docs/branding (BrandingRenderTests, offscreen; nothing is shown).
# No font is used: every letter and numeral is path data drawn for JoyPro.

$ErrorActionPreference = "Stop"
Push-Location $Root
try {
    python -I (Join-Path $Root "branding\generate-branding.py") $Root $Icon $Title $Engine
    if ($LASTEXITCODE -ne 0) { throw "generate-branding.py failed" }
    dotnet build gui/JoyPro.Gui.Tests -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    $env:JOYPRO_BRANDING_OUT = Join-Path $Root "docs\branding"
    $env:JOYPRO_BRANDING_REPO = $Root
    try {
        dotnet test gui/JoyPro.Gui.Tests --no-build -nologo --filter "FullyQualifiedName~BrandingRenderTests"
        if ($LASTEXITCODE -ne 0) { throw "BrandingRenderTests failed" }
    }
    finally {
        Remove-Item Env:JOYPRO_BRANDING_OUT, Env:JOYPRO_BRANDING_REPO -ErrorAction SilentlyContinue
    }
    Write-Host "Icons and previews written (icon $Icon, title $Title, engine $Engine)."
}
finally {
    Pop-Location
}
