# Screenshots of built website pages in headless Microsoft Edge, to look at the site at desktop and phone width, light
# and dark, without a server (the pages work from file://):
#   powershell -ExecutionPolicy Bypass -File website\screenshot_pages.ps1 -Out <folder> [-Pages index.html,gui/export.html]
# Files: <page>-<desktop|phone>-<light|dark>.png. Edge runs headless with its own temporary profile (no window).
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string[]]$Pages = @("index.html", "flows/export.html", "gui/export.html", "cli/dcs.html", "project/jp1-vs-jp2.html"),
    [int]$DesktopHeight = 1100,
    [int]$PhoneHeight = 1600
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$dist = Join-Path $PSScriptRoot "dist"
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found." }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$profileDir = Join-Path ([System.IO.Path]::GetTempPath()) ("joypro-site-edge-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
try {
    foreach ($page in ($Pages | ForEach-Object { $_ -split "," } | Where-Object { $_ })) {
        $file = Join-Path $dist $page
        if (-not (Test-Path $file)) { Write-Host "missing: $page"; continue }
        $uri = ([System.Uri]$file).AbsoluteUri
        $name = ($page -replace '[\\/]', '_') -replace '\.html$', ''
        foreach ($view in @("desktop", "phone")) {
            foreach ($theme in @("light", "dark")) {
                $png = Join-Path $Out ($name + "-" + $view + "-" + $theme + ".png")
                $target = $uri + "?theme=" + $theme
                $size = "1280,$DesktopHeight"
                if ($view -eq "phone") {
                    # A window cannot be narrower than about 500 px: the page goes into a 390 px frame, the picture is cut to it.
                    $wrapper = Join-Path $profileDir "phone.html"
                    [System.IO.File]::WriteAllText($wrapper, "<!doctype html><html><body style='margin:0;background:#888'><iframe src='$target' style='border:0;width:390px;height:${PhoneHeight}px;display:block'></iframe></body></html>")
                    $target = ([System.Uri]$wrapper).AbsoluteUri
                    $size = "600,$PhoneHeight"
                }
                $edgeArgs = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=$profileDir",
                    "--allow-file-access-from-files", "--window-size=$size", "--virtual-time-budget=5000", "--screenshot=$png", $target)
                Start-Process -FilePath $edge -ArgumentList $edgeArgs -WindowStyle Hidden -Wait
                if ($view -eq "phone" -and (Test-Path $png)) {
                    $img = [System.Drawing.Image]::FromFile($png)
                    $crop = New-Object System.Drawing.Bitmap 390, $img.Height
                    $g = [System.Drawing.Graphics]::FromImage($crop)
                    $rect = New-Object System.Drawing.Rectangle 0, 0, 390, $img.Height
                    $g.DrawImage($img, $rect, $rect, [System.Drawing.GraphicsUnit]::Pixel)
                    $g.Dispose(); $img.Dispose()
                    $crop.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $crop.Dispose()
                }
                Write-Host ("{0} {1}" -f $png, $(if (Test-Path $png) { "ok" } else { "FAILED" }))
            }
        }
    }
} finally {
    Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
}
