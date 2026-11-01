# Detected control spots (engine devices reference detect, docs/device-reference.md, experimental) on a drawn device
# photo: a dark body with five round buttons on a plain background. Checks the JSON (spots inside the picture with a
# score, no input assigned, most buttons found), --max, --text, and the refusals (an unknown model, --eval on a photo
# without curated positions, a photo of the model that is not on this PC).
# Dot-sourced by engine_smoke_test.ps1 (Run-Engine, Assert-*, $tempDir). Needs no game lists and downloads nothing.

Add-Type -AssemblyName System.Drawing
$ddDir = Join-Path $tempDir "devicedetect"
New-Item -Path $ddDir -ItemType Directory -Force | Out-Null
$ddPreviousData = $env:JOYPRO_DATA_DIR
$ddPreviousBackup = $env:JOYPRO_PHOTO_BACKUP
$env:JOYPRO_DATA_DIR = Join-Path $ddDir "data"
$env:JOYPRO_PHOTO_BACKUP = ""

try {
    $ddPhoto = Join-Path $ddDir "device.png"
    $ddButtons = @(@(0.35, 0.30), @(0.50, 0.30), @(0.65, 0.30), @(0.42, 0.55), @(0.58, 0.55))
    $ddBitmap = New-Object System.Drawing.Bitmap 640, 640
    $ddG = [System.Drawing.Graphics]::FromImage($ddBitmap)
    $ddG.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $ddG.Clear([System.Drawing.Color]::FromArgb(244, 244, 244))
    $ddG.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(42, 45, 51))), 141, 96, 358, 416)
    $ddIndex = 0
    foreach ($b in $ddButtons) {
        $x = $b[0] * 640; $y = $b[1] * 640
        $ddG.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(154, 160, 168))), $x - 20, $y - 20, 40, 40)
        $inner = if ($ddIndex % 2 -eq 0) { [System.Drawing.Color]::FromArgb(216, 220, 224) } else { [System.Drawing.Color]::FromArgb(16, 16, 18) }
        $ddG.FillEllipse((New-Object System.Drawing.SolidBrush $inner), $x - 15, $y - 15, 30, 30)
        $ddIndex++
    }
    $ddG.Dispose()
    $ddBitmap.Save($ddPhoto, [System.Drawing.Imaging.ImageFormat]::Png)
    $ddBitmap.Dispose()

    $ddOut = Join-Path $ddDir "spots.json"
    $ddRun = Run-Engine @("--out", $ddOut, "devices", "reference", "detect", "thrustmaster-t16000m", "--photo", $ddPhoto)
    Assert-True ($ddRun.ExitCode -eq 0) ("devices reference detect failed: " + $ddRun.Output)
    $ddJson = [IO.File]::ReadAllText($ddOut) | ConvertFrom-Json
    Assert-True ($ddJson.Model -eq "thrustmaster-t16000m" -and $ddJson.Width -eq 640 -and $ddJson.Height -eq 640) "devices reference detect did not name the model and the photo's size."
    Assert-True (-not $ddJson.Assigned) "devices reference detect must not claim inputs for its spots."
    $ddSpots = @($ddJson.Spots)
    Assert-True ($ddSpots.Count -ge 5 -and $ddSpots.Count -le 80) ("devices reference detect gave " + $ddSpots.Count + " spots.")
    foreach ($s in $ddSpots) {
        Assert-True ($s.X -gt 0.2 -and $s.X -lt 0.8 -and $s.Y -gt 0.13 -and $s.Y -lt 0.82) ("a detected spot lies outside the device: " + $s.X + ", " + $s.Y)
        Assert-True ($s.Score -ge 0 -and $s.Score -le 1) "a detected spot has a score outside 0..1."
    }
    $ddFound = 0
    foreach ($b in $ddButtons) {
        if (@($ddSpots | Where-Object { [Math]::Sqrt([Math]::Pow(($_.X - $b[0]) * 640, 2) + [Math]::Pow(($_.Y - $b[1]) * 640, 2)) -le 0.02 * 905 }).Count -gt 0) { $ddFound++ }
    }
    Assert-True ($ddFound -ge 4) ("devices reference detect found only " + $ddFound + " of the 5 drawn buttons.")

    $ddMax = Run-Engine @("--out", $ddOut, "devices", "reference", "detect", "thrustmaster-t16000m", "--photo", $ddPhoto, "--max", "3")
    Assert-True ($ddMax.ExitCode -eq 0) ("devices reference detect --max failed: " + $ddMax.Output)
    Assert-True (@(([IO.File]::ReadAllText($ddOut) | ConvertFrom-Json).Spots).Count -le 3) "devices reference detect --max 3 gave more than 3 spots."

    $ddText = Run-Engine @("devices", "reference", "detect", "thrustmaster-t16000m", "--photo", $ddPhoto, "--text")
    Assert-True ($ddText.ExitCode -eq 0 -and $ddText.Output -match "No input is assigned to a spot") "devices reference detect --text lacks its note."

    $ddUnknown = Run-Engine @("devices", "reference", "detect", "no-such-device-model", "--photo", $ddPhoto)
    Assert-True ($ddUnknown.ExitCode -ne 0 -and $ddUnknown.Output -match "not in the device catalog") "devices reference detect accepted an unknown model."
    $ddEval = Run-Engine @("devices", "reference", "detect", "thrustmaster-t16000m", "--photo", $ddPhoto, "--eval")
    Assert-True ($ddEval.ExitCode -ne 0 -and $ddEval.Output -match "curated positions") "devices reference detect --eval ran on a photo without curated positions."
    $ddMissing = Run-Engine @("devices", "reference", "detect", "thrustmaster-t16000m", "--photo", "main")
    Assert-True ($ddMissing.ExitCode -ne 0 -and $ddMissing.Output -match "not on this PC") "devices reference detect did not say that the maker's photo is not on this PC."
    Write-Host "devices reference detect: $ddFound of 5 drawn buttons found, refusals ok."
}
finally {
    $env:JOYPRO_DATA_DIR = $ddPreviousData
    $env:JOYPRO_PHOTO_BACKUP = $ddPreviousBackup
}
