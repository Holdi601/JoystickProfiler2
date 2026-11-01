# The public-content check of tools\export_public.ps1 on the built website: personal data, private paths, internal
# notes, and the docs rules (no protection, legal or anti-cheat reasoning; facts only) on every page and the search
# index. The site's text files are copied under docs\ of a temporary tree, so the docs rules apply to all of them.
#   powershell -ExecutionPolicy Bypass -File website\check_public.ps1      (after python website\build.py)
# Exit code 0 = clean; the script throws with the offending lines otherwise.
$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "dist"
if (-not (Test-Path $dist)) { throw "Build the site first: python website\build.py" }
$exporter = Join-Path (Split-Path -Parent $PSScriptRoot) "tools\export_public.ps1"
if (-not (Test-Path $exporter)) { throw "tools\export_public.ps1 not found (it stays in the private repository)." }
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("joypro-site-check-" + [Guid]::NewGuid().ToString("N"))
try {
    $count = 0
    foreach ($file in Get-ChildItem -Path $dist -Recurse -File) {
        $ext = $file.Extension.ToLowerInvariant()
        if ($ext -notin @(".html", ".js", ".css", ".json", ".svg", ".txt")) { continue }
        if ($file.FullName -like "*\assets\vendor\*") { continue }   # the pinned third-party Mermaid build
        $relative = $file.FullName.Substring($dist.Length + 1)
        # .md: the docs rules of the checker apply to the whole text
        $target = Join-Path $stage ("docs\site\" + $relative + ".md")
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        # The text as a reader sees it (&lt;name&gt; is <name>), so the rules match what the page shows.
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($ext -eq ".html") { $text = [System.Net.WebUtility]::HtmlDecode(($text -replace '<[^>]+>', ' ')) }
        [System.IO.File]::WriteAllText($target, $text)
        $count++
    }
    # The site's own sources as the public repository will hold them (website\, without the build output).
    foreach ($file in Get-ChildItem -Path $PSScriptRoot -Recurse -File) {
        if ($file.FullName -like "*\dist\*" -or $file.FullName -like "*\vendor\*" -or $file.FullName -like "*\__pycache__\*") { continue }
        $target = Join-Path $stage ("website\" + $file.FullName.Substring($PSScriptRoot.Length + 1))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
        $count++
    }
    Write-Host "Checking $count website files..."
    & $exporter -CheckOnly -Destination $stage
} finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
