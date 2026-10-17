# The cockpit database (engine cockpits ..., docs/cockpits.md). Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*,
# $tempDir). Nothing is downloaded: the photos are links, the engine never fetches one.

$cpDir = Join-Path $tempDir "cockpits"
New-Item -Path $cpDir -ItemType Directory -Force | Out-Null

# The whole database checks clean: stick and throttle side per variant, known function ids, sources, drawings.
$cpCheck = Run-Engine @("cockpits", "check", "--text")
Assert-True ($cpCheck.ExitCode -eq 0) "cockpits check found problems: $($cpCheck.Output)"
Assert-Match $cpCheck.Output "(?m)^Problems: 0\r?$" "cockpits check did not report zero problems."

# List with filters (JSON for the GUI and scripts).
$cpList = Run-Engine @("cockpits", "list", "--game", "DCS")
Assert-True ($cpList.ExitCode -eq 0) "cockpits list failed: $($cpList.Output)"
$cpAll = $cpList.Output | ConvertFrom-Json
Assert-True (@($cpAll | Where-Object { $_.Id -eq "f-16" }).Count -eq 1) "cockpits list --game DCS lacks the F-16."
$cpFiction = (Run-Engine @("cockpits", "list", "--category", "real")).Output | ConvertFrom-Json
Assert-True (@($cpFiction | Where-Object { $_.Fiction }).Count -eq 0) "cockpits list --category real lists fictional craft."

# Search finds a control across aircraft.
$cpSearch = Run-Engine @("cockpits", "search", "TMS", "--text")
Assert-Match $cpSearch.Output "(?m)^  - f-16/f-16c-block-50 \| (Target Management Switch \(TMS\)|TMS)" "cockpits search TMS does not find the F-16's TMS."

# get: the layout the setup wizard takes (vocabulary per direction and the game's actions).
$cpGet = Run-Engine @("cockpits", "get", "f-16", "f-16c-block-50", "--json")
Assert-True ($cpGet.ExitCode -eq 0) "cockpits get failed: $($cpGet.Output)"
$cpLayout = $cpGet.Output | ConvertFrom-Json
Assert-True ($cpLayout.Format -eq "joypro-cockpit-layout") "cockpits get is not a joypro-cockpit-layout."
$cpStick = $cpLayout.Devices | Where-Object { $_.Device -eq "stick" }
$cpTms = $cpStick.Controls | Where-Object { $_.Id -eq "stick.hat.tms" }
# The manual says forward (fwd); the control vocabulary calls a hat's forward "up".
$cpUp = @($cpTms.Functions | Where-Object { ($_.Dir -eq "up" -or $_.Dir -eq "fwd") -and $_.Fn -eq "target_management_system_up" })[0]
Assert-True ($cpUp.Fn -eq "target_management_system_up" -and $cpUp.Vocabulary -eq "stick.hat.tms.up") "TMS up lacks its function id or vocabulary: $($cpUp | ConvertTo-Json -Compress)"
Assert-True (@($cpUp.Actions | Where-Object { $_.Game -eq "DCS" -and $_.Plane -eq "F-16C_50" }).Count -ge 1) "TMS up lacks its DCS action."
Assert-True ($cpTms.Placed -and $cpStick.Drawing -eq "f16-sidestick") "the TMS is not placed on JoyPro's F-16 drawing."
$cpPitch = ($cpStick.Controls | Where-Object { $_.Id -eq "stick.gimbal" }).Functions | Where-Object { $_.Dir -eq "pitch" }
Assert-True ($cpPitch.Vocabulary -eq "stick.axis.pitch") "the pitch axis does not map to stick.axis.pitch."
$cpByVariant = Run-Engine @("cockpits", "get", "f-16c-block-50")
Assert-True ($cpByVariant.ExitCode -eq 0 -and ($cpByVariant.Output | ConvertFrom-Json).Variant.Id -eq "f-16c-block-50") "cockpits get <variant id> does not find the variant."
$cpMissing = Run-Engine @("cockpits", "get", "no-such-aircraft")
Assert-True ($cpMissing.ExitCode -ne 0) "cockpits get accepted an unknown aircraft."

# render: the sheets as one picture (stick and throttle side by side), and a drawing on its own.
$cpPng = Join-Path $cpDir "f-16.png"
$cpRender = Run-Engine @("cockpits", "render", "f-16", "--png", $cpPng, "--print-width", "0", "--text")
Assert-True ($cpRender.ExitCode -eq 0 -and (Test-Path $cpPng)) "cockpits render wrote no picture: $($cpRender.Output)"
Assert-Match $cpRender.Output "(?m)^  - throttle \| f16-throttle \| tables \d+ \| lines \d+ \| not on the drawing 0" "the F-16 throttle sheet is incomplete."
$cpDrawing = Run-Engine @("cockpits", "render", "--drawing", "generic-yoke", "--png", (Join-Path $cpDir "yoke.png"), "--style", "light", "--text")
Assert-True ($cpDrawing.ExitCode -eq 0) "cockpits render --drawing failed: $($cpDrawing.Output)"

# The manual pages: the A-10C II stick with its HOTAS table per sensor of interest, and every page as one PDF.
$cpPage = Join-Path $cpDir "a10-stick.png"
$cpPages = Run-Engine @("cockpits", "render", "a-10", "a-10c-ii", "--page", "stick", "--png", $cpPage, "--text")
Assert-True ($cpPages.ExitCode -eq 0 -and (Test-Path $cpPage)) "cockpits render --page wrote no page: $($cpPages.Output)"
Assert-Match $cpPages.Output "(?m)^  - device:stick \| Stick \| \d+ items" "the A-10C II stick page is missing."
$cpPdf = Join-Path $cpDir "a10.pdf"
$cpPdfRun = Run-Engine @("cockpits", "render", "a-10", "a-10c-ii", "--pdf", $cpPdf, "--scale", "0.5", "--text")
Assert-True ($cpPdfRun.ExitCode -eq 0 -and (Test-Path $cpPdf) -and (Get-Item $cpPdf).Length -gt 20000) "cockpits render --pdf wrote no PDF: $($cpPdfRun.Output)"
$cpModes = Run-Engine @("cockpits", "get", "a-10", "a-10c-ii", "--json")
Assert-Match $cpModes.Output '"DefaultMode":\s*"hud"' "cockpits get gives no default mode for the A-10C II."

# compare: which functions a profile holds (a relation on the F-16's TMS up).
$cpProfile = Join-Path $cpDir "compare.pr0file"
Run-Engine @("--profile", $cpProfile, "relations", "add", "TMS Up", "--button", "--node", "DCS:d3010pnilu3010cd16vd1vpnilvu0:F-16C_50", "--text") | Out-Null
$cpCompare = Run-Engine @("--profile", $cpProfile, "cockpits", "compare", "f-16", "f-16c-block-50")
Assert-True ($cpCompare.ExitCode -eq 0) "cockpits compare failed: $($cpCompare.Output)"
$cpCompared = $cpCompare.Output.Substring($cpCompare.Output.IndexOf("{")) | ConvertFrom-Json
Assert-True ($cpCompared.Matched -ge 1 -and $cpCompared.Total -gt 10) "cockpits compare did not find the TMS relation: $($cpCompare.Output)"

# photos and coverage.
$cpPhotos = Run-Engine @("cockpits", "photos", "--coverage", "--text")
Assert-Match $cpPhotos.Output "(?m)^Cockpit photos: \d+" "cockpits photos --coverage printed no count."
$cpCoverage = Run-Engine @("cockpits", "coverage", "--text")
Assert-Match $cpCoverage.Output "(?m)^Cockpit coverage: \d+ aircraft" "cockpits coverage printed no numbers."
