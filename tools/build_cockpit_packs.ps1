# Builds the cockpit picture packs of a release (docs/cockpits.md "Picture packs"): one small zip per aircraft set of
# a pictures folder as "engine cockpits pictures build" makes it (<Source>\<game>\<set>\pictures.json with its PNGs and
# *.drawing.json, <Source>\<game>\index.json), plus manifest.json. The packs are release assets (GitHub releases
# "cockpit-pictures-v1" and the further feeds it names), never part of the repository; this script only writes the
# output folder and uploads nothing.
#
#   powershell -File tools\build_cockpit_packs.ps1 [-Source <pictures root>] [-Out <folder>] [-Game DCS,XPlane12]
#                                                  [-Quality 90] [-MaxSide 1600] [-Tag cockpit-pictures-v1]
#                                                  [-Feeds <tag>,...] [-MaxAssets 1000] [-ManifestOnly] [-Force]
#                                                  [-Only <game>/<set>,...]
#
# Feeds (a GitHub release takes at most 1000 assets): the main release cockpit-pictures-v1 has every game except those
# with a release of their own (War Thunder: -Tag cockpit-pictures-wt-v1, default output ...\cockpit-packs-wt); its
# manifest names them in "feeds" (-Feeds, default: the known further tags). A release with more than MaxAssets - 1 packs
# is split: the rest goes to <Out>\<Tag>-part2 (tag <Tag>-part2, ...), named in the first part's "feeds". A pack keeps
# its part while that part has room. -ManifestOnly writes the manifests again from the zips that are there (new feeds,
# a new split) without reading the pictures. -Only <game>/<set>,... (wildcards: XPlane12/*) reads only these sets of the
# source (a named set that is not there any more is left out); every other pack of the previous manifests stays as it
# is (zip, version, part), also one whose set is not in the source.
#
# Per pack: the pictures as JPEG (quality 90, at most 1600 px on the long side; a picture with transparent parts gets
# <name>.mask.png, an 8-bit grey mask of its transparency; PNG only when a picture cannot be converted), pictures.json
# with the new names, the drawings, CREDITS.txt and pack.json. manifest.json lists per pack: id (<game>/<set>), game,
# set, planes (the game's aircraft names of the set), asset (the zip), size, sha256, version, built, picturesBuilt.
# A set whose pictures did not change keeps its zip and version; a changed one gets the next version (the GUI updates
# installed packs to a newer version). Default source: <data>\GameData\CockpitPictures (JOYPRO_DATA_DIR, else
# %LOCALAPPDATA%\JoyPro2); default output: <JOYPRO_RELEASE_DIR>\cockpit-packs (the environment variable). The output must be outside the
# repository.
param(
    [string]$Source = "",
    [string]$Out = "",
    [string[]]$Game = @(),
    [int]$Quality = 90,
    [int]$MaxSide = 1600,
    [string]$Tag = "cockpit-pictures-v1",
    [string[]]$Feeds = $null,
    [int]$MaxAssets = 1000,
    [switch]$ManifestOnly,
    [switch]$Force,
    [string[]]$Only = @()
)
$ErrorActionPreference = "Stop"

# The main release and the further ones: their games and default output folders.
$mainTag = "cockpit-pictures-v1"
$feedGames = [ordered]@{ "cockpit-pictures-wt-v1" = @("WarThunder") }
$feedOut = @{ "cockpit-pictures-v1" = "cockpit-packs"; "cockpit-pictures-wt-v1" = "cockpit-packs-wt" }

# System.Drawing's JPEG encoder of Windows PowerShell (5.1); PowerShell 7 runs the script there.
if ($PSVersionTable.PSEdition -eq "Core") {
    $argsList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Quality", $Quality, "-MaxSide", $MaxSide, "-Tag", $Tag, "-MaxAssets", $MaxAssets)
    if ($Out -ne "") { $argsList += @("-Out", $Out) }
    if ($Source -ne "") { $argsList += @("-Source", $Source) }
    if ($Game.Count -gt 0) { $argsList += @("-Game", ($Game -join ",")) }
    if ($PSBoundParameters.ContainsKey("Feeds")) { $argsList += @("-Feeds", (@($Feeds) -join ",")) }
    if ($ManifestOnly) { $argsList += "-ManifestOnly" }
    if ($Force) { $argsList += "-Force" }
    if ($Only.Count -gt 0) { $argsList += @("-Only", ($Only -join ",")) }
    & powershell.exe @argsList
    exit $LASTEXITCODE
}

if ($MaxAssets -lt 2) { throw "-MaxAssets must be at least 2 (one pack and manifest.json)." }
if ($Out -eq "") {
    if (-not $feedOut.ContainsKey($Tag)) { throw "No default output folder for the tag $($Tag): give -Out." }
    if ([string]::IsNullOrWhiteSpace($env:JOYPRO_RELEASE_DIR)) { throw "Give -Out or set JOYPRO_RELEASE_DIR (the folder that holds the release folders)." }
    $Out = Join-Path $env:JOYPRO_RELEASE_DIR $feedOut[$Tag]
}
$Game = @($Game | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })
$Only = @($Only | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
if ($ManifestOnly -and $Only.Count -gt 0) { throw "-Only and -ManifestOnly do not go together." }
# Games of the release: -Game, else the tag's games; the main release leaves out the games of the further releases.
$skipGames = @()
if ($Game.Count -eq 0) {
    if ($feedGames.Contains($Tag)) { $Game = @($feedGames[$Tag]) }
    else { $skipGames = @($feedGames.Values | ForEach-Object { $_ }) }
}
if ($PSBoundParameters.ContainsKey("Feeds")) { $Feeds = @($Feeds | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }) }
elseif ($Tag -eq $mainTag) { $Feeds = @($feedGames.Keys) }
else { $Feeds = @() }
foreach ($f in $Feeds) { if ($f -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "Not a release tag: $f" } }

$repo = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
if (-not $ManifestOnly) {
    if ($Source -eq "") {
        $data = $env:JOYPRO_DATA_DIR
        if ([string]::IsNullOrWhiteSpace($data)) {
            # The data folder of JoyPro2 2.4 and later; an older install still has %LOCALAPPDATA%\JoyPro.
            $data = Join-Path $env:LOCALAPPDATA "JoyPro2"
            if (-not (Test-Path (Join-Path $data "GameData"))) { $data = Join-Path $env:LOCALAPPDATA "JoyPro" }
        }
        $Source = Join-Path $data "GameData\CockpitPictures"
    }
    if (-not (Test-Path $Source)) { throw "No pictures folder: $Source" }
    $Source = (Resolve-Path $Source).Path
}
$outFull = [IO.Path]::GetFullPath($Out)
$repoRoot = $repo
$commonDir = (& git -C $repo rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($LASTEXITCODE -eq 0 -and $commonDir) { $repoRoot = Split-Path -Parent ([IO.Path]::GetFullPath($commonDir.Trim())) }
if ($outFull.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "The pack folder must be outside the repository: $outFull" }
New-Item -ItemType Directory -Force $outFull | Out-Null

Add-Type -ReferencedAssemblies System.Drawing, System.IO.Compression, System.IO.Compression.FileSystem -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class JoyProCockpitPacks
{
    public static string Sha256(string file)
    {
        using (FileStream s = File.OpenRead(file))
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }

    // What the pack is made from: pictures.json and the names and sizes of the pictures and drawings, plus the settings.
    public static string SourceHash(string folder, string settings)
    {
        StringBuilder sb = new StringBuilder(settings);
        sb.Append('|').Append(Sha256(Path.Combine(folder, "pictures.json")));
        foreach (string f in Directory.GetFiles(folder).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".drawing.json", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            sb.Append('|').Append(Path.GetFileName(f)).Append(':').Append(new FileInfo(f).Length);
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant();
    }

    // The JSON text value of a top-level field of pictures.json ("built", "module", "plane").
    public static string Field(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
    }

    public static string[] Planes(string json)
    {
        Match m = Regex.Match(json, "\"planes\"\\s*:\\s*\\[([^\\]]*)\\]");
        if (!m.Success)
            return new string[0];
        return Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>().Select(x => Regex.Unescape(x.Groups[1].Value)).ToArray();
    }

    // The set's files into the stage folder: pictures as JPEG (+ mask), pictures.json with the new names, the drawings.
    // Returns the number of pictures that stayed PNG.
    public static int Stage(string setFolder, string stage, int maxSide, long quality)
    {
        if (Directory.Exists(stage))
            Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        string json = File.ReadAllText(Path.Combine(setFolder, "pictures.json"), Encoding.UTF8);
        Dictionary<string, string> replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        int kept = 0;
        json = Regex.Replace(json, "\"image\"\\s*:\\s*\"([^\"\\\\]+)\"", delegate (Match m)
        {
            string image = m.Groups[1].Value;
            string done;
            if (replaced.TryGetValue(image, out done))
                return done;
            string source = Path.Combine(setFolder, image);
            string result = m.Value;
            if (File.Exists(source) && Path.GetFileName(image) == image)
            {
                string name = Path.GetFileNameWithoutExtension(image);
                try
                {
                    bool mask = ToJpeg(source, Path.Combine(stage, name + ".jpg"), Path.Combine(stage, name + ".mask.png"), maxSide, quality);
                    result = "\"image\":\"" + name + ".jpg\"" + (mask ? ",\"mask\":\"" + name + ".mask.png\"" : "");
                }
                catch (Exception)
                {
                    File.Copy(source, Path.Combine(stage, image), true);
                    kept++;
                }
            }
            replaced[image] = result;
            return result;
        });
        File.WriteAllText(Path.Combine(stage, "pictures.json"), json, new UTF8Encoding(false));
        foreach (string drawing in Directory.GetFiles(setFolder, "*.drawing.json"))
            File.Copy(drawing, Path.Combine(stage, Path.GetFileName(drawing)), true);
        return kept;
    }

    // The picture as JPEG (scaled to maxSide); true when it has transparent parts, written as the grey mask PNG.
    public static bool ToJpeg(string png, string jpg, string maskFile, int maxSide, long quality)
    {
        using (Bitmap src = new Bitmap(png))
        {
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
            using (Bitmap argb = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(argb))
                using (ImageAttributes edges = new ImageAttributes())
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    edges.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, edges);
                }
                Rectangle all = new Rectangle(0, 0, w, h);
                BitmapData data = argb.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] px = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                int stride = data.Stride;
                argb.UnlockBits(data);
                bool transparent = false;
                byte[] alpha = new byte[w * h];
                byte[] rgb = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + 4 * x, o = y * w + x;
                        byte a = px[i + 3];
                        alpha[o] = a;
                        if (a < 255)
                            transparent = true;
                        // Nothing there: a dark grey like most cockpits, so the JPEG's edges do not glow.
                        if (a == 0)
                        {
                            rgb[3 * o] = 0x24; rgb[3 * o + 1] = 0x24; rgb[3 * o + 2] = 0x24;
                        }
                        else
                        {
                            rgb[3 * o] = px[i]; rgb[3 * o + 1] = px[i + 1]; rgb[3 * o + 2] = px[i + 2];
                        }
                    }
                }
                using (Bitmap color = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    BitmapData cd = color.LockBits(all, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(rgb, y * w * 3, cd.Scan0 + y * cd.Stride, w * 3);
                    color.UnlockBits(cd);
                    ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (EncoderParameters p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                        color.Save(jpg, codec, p);
                    }
                }
                if (!transparent)
                    return false;
                using (Bitmap mask = new Bitmap(w, h, PixelFormat.Format8bppIndexed))
                {
                    ColorPalette palette = mask.Palette;
                    for (int k = 0; k < 256; k++)
                        palette.Entries[k] = Color.FromArgb(255, k, k, k);
                    mask.Palette = palette;
                    BitmapData md = mask.LockBits(all, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(alpha, y * w, md.Scan0 + y * md.Stride, w);
                    mask.UnlockBits(md);
                    mask.Save(maskFile, ImageFormat.Png);
                }
                return true;
            }
        }
    }

    // The stage folder as a flat zip (JPEGs stored, the rest compressed).
    public static void Zip(string stage, string zip)
    {
        string tmp = zip + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
        using (ZipArchive archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (string f in Directory.GetFiles(stage).OrderBy(f => f, StringComparer.Ordinal))
            {
                CompressionLevel level = f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                archive.CreateEntryFromFile(f, Path.GetFileName(f), level);
            }
        }
        if (File.Exists(zip))
            File.Delete(zip);
        File.Move(tmp, zip);
    }
}
"@

$publishers = @{
    "DCS"           = @("DCS World", "Eagle Dynamics")
    "XPlane12"      = @("X-Plane 12", "Laminar Research")
    "XPlane11"      = @("X-Plane 11", "Laminar Research")
    "MSFS2020"      = @("Microsoft Flight Simulator 2020", "Microsoft / Asobo Studio")
    "MSFS2024"      = @("Microsoft Flight Simulator 2024", "Microsoft / Asobo Studio")
    "IL2Game"       = @("IL-2 Great Battles", "1C Game Studios")
    "IL2Korea"      = @("IL-2 Korea", "1C Game Studios")
    "NuclearOption" = @("Nuclear Option", "Shockfront Studios")
    "WarThunder"    = @("War Thunder", "Gaijin Entertainment")
}

function Safe-AssetName([string]$text) {
    $s = [regex]::Replace($text, "[^A-Za-z0-9._-]+", "_").Trim("_", ".", "-")
    if ($s -eq "") { $s = "pack" }
    return $s
}

function Json-Text([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($c in $s.ToCharArray()) {
        switch ($c) {
            '"' { [void]$sb.Append('\"') }
            '\' { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default { if ([int]$c -lt 32) { [void]$sb.Append(('\u{0:x4}' -f [int]$c)) } else { [void]$sb.Append($c) } }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# The parts of the release: part 1 is the folder itself (tag $Tag), part k the folder <Tag>-part<k> in it.
function Part-Tag([int]$k) { if ($k -le 1) { return $Tag } else { return "$Tag-part$k" } }
function Part-Folder([int]$k) { if ($k -le 1) { return $outFull } else { return (Join-Path $outFull (Part-Tag $k)) } }
function Part-Of([string]$name) {
    if ($name -match ('^' + [regex]::Escape($Tag) + '-part(\d+)$')) { return [int]$Matches[1] }
    return 0
}

# The previous build of the folder (all parts): unchanged sets keep their zip, asset name, version and part.
$manifestFile = Join-Path $outFull "manifest.json"
$previous = @{}
$previousPart = @{}
$oldParts = @(Get-ChildItem $outFull -Directory | Where-Object { (Part-Of $_.Name) -ge 2 } | ForEach-Object { Part-Of $_.Name } | Sort-Object)
foreach ($k in @(1) + $oldParts) {
    $file = Join-Path (Part-Folder $k) "manifest.json"
    if (-not (Test-Path $file)) { continue }
    $old = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($p in @($old.packs)) {
        if ($previous.ContainsKey($p.id)) { continue }
        $previous[$p.id] = $p
        $previousPart[$p.id] = $k
    }
}

$settingsTag = "jpeg-q$Quality-max$MaxSide-v1"
$now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$usedAssets = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$packs = New-Object System.Collections.ArrayList
$stageRoot = Join-Path $outFull ".stage"
$built = 0; $reused = 0; $pngKept = 0
$limit = 2GB - 1MB

# -ManifestOnly: the packs of the previous manifests whose zips are there, as they are.
if ($ManifestOnly) {
    foreach ($p in @($previous.Values | Sort-Object game, set)) {
        $k = $previousPart[$p.id]
        $zip = Join-Path (Part-Folder $k) $p.asset
        if (-not (Test-Path $zip)) { Write-Warning "$($p.id): $($p.asset) is missing, left out."; continue }
        if (-not $usedAssets.Add($p.asset)) { Write-Warning "$($p.id): $($p.asset) twice, left out."; continue }
        $size = (Get-Item $zip).Length
        if ($size -ge $limit) { throw "$($p.asset) is $([math]::Round($size / 1GB, 2)) GB: a release asset must stay below 2 GB." }
        [void]$packs.Add([pscustomobject][ordered]@{
            id = $p.id; game = $p.game; set = $p.set; planes = @($p.planes); asset = $p.asset; size = $size
            sha256 = [JoyProCockpitPacks]::Sha256($zip); version = [int]$p.version; built = $p.built; picturesBuilt = $p.picturesBuilt; sourceHash = $p.sourceHash
            low = [bool]$p.low; path = $zip; part = $k
        })
        $reused++
    }
}

foreach ($gameDir in $(if ($ManifestOnly) { @() } else { Get-ChildItem $Source -Directory | Sort-Object Name })) {
    $gameId = $gameDir.Name
    if ($gameId.StartsWith(".")) { continue }
    if ($Game.Count -gt 0 -and -not ($Game -contains $gameId)) { continue }
    if ($skipGames -contains $gameId) { Write-Host "$($gameId): in its own release, left out (-Game $gameId -Tag ... builds it)."; continue }
    $names = @{}
    $indexFile = Join-Path $gameDir.FullName "index.json"
    if (Test-Path $indexFile) {
        $index = Get-Content $indexFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($index.aircraft) {
            foreach ($prop in $index.aircraft.PSObject.Properties) {
                if (-not $names.ContainsKey($prop.Value)) { $names[$prop.Value] = New-Object System.Collections.ArrayList }
                [void]$names[$prop.Value].Add($prop.Name)
            }
        }
    }
    $pub = $publishers[$gameId]
    if (-not $pub) { $pub = @($gameId, "its publisher") }
    foreach ($setDir in Get-ChildItem $gameDir.FullName -Directory | Sort-Object Name) {
        $picturesFile = Join-Path $setDir.FullName "pictures.json"
        if (-not (Test-Path $picturesFile)) { continue }
        $set = $setDir.Name
        $id = "$gameId/$set"
        if ($Only.Count -gt 0 -and -not ($Only | Where-Object { $id -like $_ })) { continue }
        $json = [IO.File]::ReadAllText($picturesFile, [Text.Encoding]::UTF8)
        $planes = New-Object System.Collections.Generic.List[string]
        foreach ($n in @([JoyProCockpitPacks]::Planes($json)) + @([JoyProCockpitPacks]::Field($json, "plane")) + @($names[$set])) {
            if ($n -and -not $planes.Contains($n)) { $planes.Add($n) }
        }
        $planes.Sort([StringComparer]::OrdinalIgnoreCase)
        $picturesBuilt = [JoyProCockpitPacks]::Field($json, "built")
        $module = [JoyProCockpitPacks]::Field($json, "module")
        # The quality gate's verdict (pictures.json "quality": {"low": true}): the GUI offers another game's pack first.
        $low = $json -match '"quality"\s*:\s*\{[^}]*"low"\s*:\s*true'
        $hash = [JoyProCockpitPacks]::SourceHash($setDir.FullName, $settingsTag)
        $prev = $previous[$id]
        $asset = if ($prev -and $prev.asset) { $prev.asset } else { (Safe-AssetName "$gameId-$set") + ".zip" }
        $baseName = [IO.Path]::GetFileNameWithoutExtension($asset)
        $k = 2
        while (-not $usedAssets.Add($asset)) { $asset = "$baseName-$k.zip"; $k++ }
        $part = if ($prev) { [int]$previousPart[$id] } else { 0 }
        $zip = Join-Path (Part-Folder $part) $asset
        if (-not $Force -and $prev -and $prev.sourceHash -eq $hash -and (Test-Path $zip) -and ([JoyProCockpitPacks]::Sha256($zip) -eq $prev.sha256)) {
            $version = [int]$prev.version
            $packBuilt = $prev.built
            $reused++
        }
        else {
            $version = if ($prev) { [int]$prev.version + 1 } else { 1 }
            $packBuilt = $now
            $stage = Join-Path $stageRoot $baseName
            Write-Host ("{0}: building ({1})" -f $id, $set)
            $pngKept += [JoyProCockpitPacks]::Stage($setDir.FullName, $stage, $MaxSide, [long]$Quality)
            $credits = @(
                "Pictures rendered from $($pub[0]) by $($pub[1]) with JoyPro; the cockpit designs, textures and names belong to their respective owners.",
                "",
                "Aircraft: $set$(if ($module) { " (game module / package: $module, by its developer)" })",
                "Game: $($pub[0]) ($($pub[1]))",
                "Rendered: $picturesBuilt; pack version $version.",
                "",
                "JoyPro shows these pictures as a reference for setting up your controls. They are not part of JoyPro's source code",
                "and are not covered by JoyPro's license."
            ) -join "`r`n"
            [IO.File]::WriteAllText((Join-Path $stage "CREDITS.txt"), $credits + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
            $packJson = "{`"format`":`"joypro-cockpit-pack`",`"id`":$(Json-Text $id),`"game`":$(Json-Text $gameId),`"set`":$(Json-Text $set),`"version`":$version,`"built`":$(Json-Text $packBuilt),`"picturesBuilt`":$(Json-Text $picturesBuilt)}"
            [IO.File]::WriteAllText((Join-Path $stage "pack.json"), $packJson, (New-Object System.Text.UTF8Encoding($false)))
            [JoyProCockpitPacks]::Zip($stage, $zip)
            Remove-Item -Recurse -Force $stage
            $built++
        }
        $size = (Get-Item $zip).Length
        if ($size -ge $limit) { throw "$asset is $([math]::Round($size / 1GB, 2)) GB: a release asset must stay below 2 GB." }
        [void]$packs.Add([pscustomobject][ordered]@{
            id = $id; game = $gameId; set = $set; planes = @($planes); asset = $asset; size = $size
            sha256 = [JoyProCockpitPacks]::Sha256($zip); version = $version; built = $packBuilt; picturesBuilt = $picturesBuilt; sourceHash = $hash
            low = [bool]$low; path = $zip; part = $part
        })
    }
}
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }

# -Only: every other pack of the previous manifests, as it is.
if ($Only.Count -gt 0) {
    $have = @{}
    foreach ($p in $packs) { $have[$p.id] = $true }
    foreach ($p in @($previous.Values | Sort-Object game, set)) {
        if ($have.ContainsKey($p.id)) { continue }
        # A named set that is not in the source any more is gone.
        if ($Only | Where-Object { $p.id -like $_ }) { Write-Host "$($p.id): not in the source any more, left out."; continue }
        $k = $previousPart[$p.id]
        $zip = Join-Path (Part-Folder $k) $p.asset
        if (-not (Test-Path $zip)) { Write-Warning "$($p.id): $($p.asset) is missing, left out."; continue }
        if (-not $usedAssets.Add($p.asset)) { Write-Warning "$($p.id): $($p.asset) twice, left out."; continue }
        [void]$packs.Add([pscustomobject][ordered]@{
            id = $p.id; game = $p.game; set = $p.set; planes = @($p.planes); asset = $p.asset; size = (Get-Item $zip).Length
            sha256 = [JoyProCockpitPacks]::Sha256($zip); version = [int]$p.version; built = $p.built; picturesBuilt = $p.picturesBuilt; sourceHash = $p.sourceHash
            low = [bool]$p.low; path = $zip; part = $k
        })
        $reused++
    }
}

# The parts: at most MaxAssets - 1 packs each (plus manifest.json). A pack keeps its part while it has room, the
# others go to the first part with room; a zip whose part changed moves to that part's folder.
$cap = $MaxAssets - 1
$counts = @{}
foreach ($p in $packs) {
    $k = [int]$p.part
    if ($k -ge 1 -and [int]$counts[$k] -lt $cap) { $counts[$k] = [int]$counts[$k] + 1 } else { $p.part = 0 }
}
foreach ($p in @($packs | Where-Object { $_.part -eq 0 })) {
    $k = 1
    while ([int]$counts[$k] -ge $cap) { $k++ }
    $counts[$k] = [int]$counts[$k] + 1
    $p.part = $k
}
$partCount = 1
foreach ($p in $packs) { if ($p.part -gt $partCount) { $partCount = $p.part } }
foreach ($p in $packs) {
    $folder = Part-Folder $p.part
    $target = Join-Path $folder $p.asset
    if ($target -ne $p.path) {
        New-Item -ItemType Directory -Force $folder | Out-Null
        if (Test-Path $target) { Remove-Item -Force $target }
        Move-Item -LiteralPath $p.path -Destination $target
        $p.path = $target
    }
}
$partTags = @(2..$partCount | Where-Object { $partCount -ge 2 } | ForEach-Object { Part-Tag $_ })
$allFeeds = @($Feeds) + $partTags

# manifest.json per part (written by hand: stable formatting on Windows PowerShell); the first names the further feeds.
for ($k = 1; $k -le $partCount; $k++) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($p in @($packs | Where-Object { $_.part -eq $k })) {
        $planesJson = ($p.planes | ForEach-Object { Json-Text $_ }) -join ","
        [void]$lines.Add("    {`"id`":$(Json-Text $p.id),`"game`":$(Json-Text $p.game),`"set`":$(Json-Text $p.set),`"planes`":[$planesJson],`"asset`":$(Json-Text $p.asset),`"size`":$($p.size),`"sha256`":`"$($p.sha256)`",`"version`":$($p.version),`"built`":$(Json-Text $p.built),`"picturesBuilt`":$(Json-Text $p.picturesBuilt),`"sourceHash`":`"$($p.sourceHash)`"$(if ($p.low) { ',"low":true' })}")
    }
    $feedsLine = ""
    if ($k -eq 1 -and $allFeeds.Count -gt 0) { $feedsLine = "  `"feeds`": [" + (($allFeeds | ForEach-Object { Json-Text $_ }) -join ", ") + "],`r`n" }
    $manifest = "{`r`n  `"format`": `"joypro-cockpit-packs`",`r`n  `"packVersion`": 1,`r`n  `"tag`": $(Json-Text (Part-Tag $k)),`r`n  `"built`": `"$now`",`r`n" + $feedsLine + "  `"packs`": [`r`n" + ($lines -join ",`r`n") + "`r`n  ]`r`n}`r`n"
    $folder = Part-Folder $k
    New-Item -ItemType Directory -Force $folder | Out-Null
    [IO.File]::WriteAllText((Join-Path $folder "manifest.json"), $manifest, (New-Object System.Text.UTF8Encoding($false)))
}

for ($k = 1; $k -le $partCount; $k++) {
    $referenced = @($packs | Where-Object { $_.part -eq $k } | ForEach-Object { $_.asset })
    $stale = @(Get-ChildItem (Part-Folder $k) -Filter *.zip | Where-Object { $referenced -notcontains $_.Name })
    foreach ($s in $stale) { Write-Warning "Not in the manifest (left over from an earlier build, do not upload): $($s.FullName)" }
}
foreach ($k in $oldParts) {
    if ($k -gt $partCount) { Write-Warning "Not needed any more (do not upload; delete it and its release): $(Part-Folder $k)" }
}
$total = ($packs | Measure-Object -Property size -Sum).Sum
Write-Host ("{0} packs ({1} built, {2} unchanged), {3:N1} MB in {4}; largest {5:N1} MB." -f $packs.Count, $built, $reused, ($total / 1MB), $outFull, ((($packs | Measure-Object -Property size -Maximum).Maximum) / 1MB))
for ($k = 1; $k -le $partCount; $k++) {
    $partPacks = @($packs | Where-Object { $_.part -eq $k })
    $partSize = ($partPacks | Measure-Object -Property size -Sum).Sum + (Get-Item (Join-Path (Part-Folder $k) "manifest.json")).Length
    Write-Host ("Release {0}: {1} assets ({2} packs + manifest.json), {3:N1} MB in {4}" -f (Part-Tag $k), ($partPacks.Count + 1), $partPacks.Count, ($partSize / 1MB), (Part-Folder $k))
}
if ($allFeeds.Count -gt 0) { Write-Host ("Further feeds named in {0}: {1}" -f $manifestFile, ($allFeeds -join ", ")) }
if ($pngKept -gt 0) { Write-Warning "$pngKept pictures could not be converted and stayed PNG." }
