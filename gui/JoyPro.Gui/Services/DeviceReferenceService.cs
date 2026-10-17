using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using JoyPro.DeviceReference;
using SkiaSharp;

namespace JoyPro.Gui.Services;

// The device reference pictures (docs/device-reference.md) without the UI: the device catalog (shipped DB\Devices plus
// the user's own definitions), identification, JoyPro's schematic of a model, and what the user keeps per model:
// his own photo, marker positions per picture, edited legend texts, the setting for manufacturer pictures.
//
// Everything the user makes lives in <data>\DeviceReference (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro, like the
// engine's data); user device definitions in <data>\Devices, where the engine reads them too.
public static class DeviceReferenceService
{
    private static readonly object Gate = new();
    private static DeviceCatalog? catalog;
    private static string catalogKey = string.Empty;

    // The folder of the shipped catalog; set by the GUI from the engine path. Null = found automatically.
    public static Func<string?> CatalogFolderOverride { get; set; } = () => null;

    // The engine.exe the GUI uses (its DB\Devices is the shipped catalog).
    public static string EnginePath { get; set; } = string.Empty;

    public static string DataFolder()
    {
        string? root = Environment.GetEnvironmentVariable(DeviceCatalog.DataFolderVariable);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro");
        return Path.Combine(root, "DeviceReference");
    }

    public static string UserDefinitionFolder() => DeviceCatalog.UserFolder();

    // The shipped catalog: JOYPRO_DEVICE_KB, the engine's DB\Devices, the bundled engine next to the GUI, or (when run
    // from the source tree) EngineOnly_Runtime\DB\Devices.
    public static string CatalogFolder()
    {
        string? fixedFolder = CatalogFolderOverride();
        if (!string.IsNullOrWhiteSpace(fixedFolder))
            return fixedFolder;
        string? kb = Environment.GetEnvironmentVariable(DeviceCatalog.KbFolderVariable);
        if (!string.IsNullOrWhiteSpace(kb))
            return kb;
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(EnginePath))
        {
            string? dir = File.Exists(EnginePath) ? Path.GetDirectoryName(EnginePath) : EnginePath;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                candidates.Add(Path.Combine(dir, "DB", "Devices"));
                candidates.Add(Path.Combine(dir, "engine", "DB", "Devices"));
            }
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engine", "DB", "Devices"));
        DirectoryInfo? walk = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && walk != null; i++, walk = walk.Parent)
            candidates.Add(Path.Combine(walk.FullName, "EngineOnly_Runtime", "DB", "Devices"));
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public static DeviceCatalog Catalog
    {
        get
        {
            lock (Gate)
            {
                string key = CatalogFolder() + "|" + UserDefinitionFolder();
                if (catalog == null || catalogKey != key)
                {
                    catalog = DeviceCatalog.Load(CatalogFolder(), UserDefinitionFolder());
                    catalogKey = key;
                }
                return catalog;
            }
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            catalog = null;
            curated = null;
        }
    }

    public static DeviceMatch Identify(DeviceProbe probe) => DeviceIdentifier.Identify(Catalog, probe);

    public static DeviceSchematic Schematic(RefDevice model) => SchematicBuilder.Build(model, Catalog.BaseOf(model));

    // ------------------------------------------------------------------ settings

    private static string SettingsFile() => Path.Combine(DataFolder(), "settings.json");

    // Makers' product photos may be offered at all: the photo mode is not "never" (DevicePhotoConsent). The old
    // "Manufacturer photos" switch is folded into the per-device consent: off = "never", on = "ask" (or "always").
    public static bool VendorPhotosEnabled
    {
        get => DevicePhotoConsent.Mode != DevicePhotoConsent.Never;
        set
        {
            if (value != VendorPhotosEnabled)
                DevicePhotoConsent.Mode = value ? DevicePhotoConsent.Ask : DevicePhotoConsent.Never;
        }
    }

    // Game covers missing on this PC may be downloaded (Steam's store, the publisher's site): its own setting, on by
    // default (Settings > Privacy). The engine gets --no-internet-pictures when it is off.
    public static bool GameCoversEnabled
    {
        get => ReadSettings().GameCovers;
        set
        {
            lock (Gate)
            {
                ReferenceSettings settings = ReadSettings();
                settings.GameCovers = value;
                WriteJson(SettingsFile(), settings);
            }
        }
    }

    // Downloads stay possible in general; which device photo may be fetched is decided per device (DevicePhotoConsent,
    // checked by DeviceImageCache before every download and before a cached photo is shown).
    public static void ApplySettings()
    {
        DeviceImageCache.InternetPicturesEnabled = true;
    }

    internal static ReferenceSettings ReadSettings() => ReadJson<ReferenceSettings>(SettingsFile()) ?? new ReferenceSettings();

    internal static void WriteSettings(ReferenceSettings settings) => WriteJson(SettingsFile(), settings);

    public sealed class ReferenceSettings
    {
        // The old switch (before 2026-10-12): false is taken over as the photo mode "never".
        public bool VendorPhotos { get; set; } = true;

        // ask (default), always or never: DevicePhotoConsent.
        public string? PhotoMode { get; set; }

        // The answer per device model: "yes" or "no".
        public Dictionary<string, string> PhotoAnswers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public bool GameCovers { get; set; } = true;

        // How JoyPro's own drawings are painted (DrawingStyle spec "mode=cel;preset=joypro;..."): on screen (Layout page,
        // device cards) and in exports / cheat sheets (empty = the screen's). The engine reads both from this file.
        public string? DrawingStyle { get; set; }

        public string? ExportDrawingStyle { get; set; }
    }

    // The drawing style on screen: cel-shaded in JoyPro's colours unless the user chose otherwise.
    public static DrawingStyle ScreenDrawingStyle
    {
        get => JoyPro.DeviceReference.DrawingStyle.Parse(ReadSettings().DrawingStyle);
        set
        {
            lock (Gate)
            {
                ReferenceSettings settings = ReadSettings();
                settings.DrawingStyle = value.ToString();
                WriteJson(SettingsFile(), settings);
            }
            DrawingStyleChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    // The drawing style of exports and cheat sheets: the user's choice in the export options, else the screen's.
    public static DrawingStyle ExportDrawingStyle
    {
        get
        {
            ReferenceSettings settings = ReadSettings();
            return JoyPro.DeviceReference.DrawingStyle.Parse(string.IsNullOrWhiteSpace(settings.ExportDrawingStyle) ? settings.DrawingStyle : settings.ExportDrawingStyle);
        }
        set
        {
            lock (Gate)
            {
                ReferenceSettings settings = ReadSettings();
                settings.ExportDrawingStyle = value.ToString();
                WriteJson(SettingsFile(), settings);
            }
        }
    }

    // The engine options of the export style (the engine would read the same from the settings file; explicit
    // options make an export reproducible).
    public static List<string> ExportDrawingArguments()
    {
        DrawingStyle style = ExportDrawingStyle;
        return ["--drawing-style", style.ToString()];
    }

    // Raised when the screen style changed: pictures of JoyPro's drawings repaint.
    public static event EventHandler? DrawingStyleChanged;

    // ------------------------------------------------------------------ the grip on a base

    private static string GripFile() => Path.Combine(DataFolder(), "grips.json");

    // The grip the user picked for a base that takes several (Layout page), by device: the JoyPro name with its GUID
    // first, else the same device name without it (Windows gives a device a new GUID now and then). Null = none picked.
    public static string? GripOf(string device)
    {
        Dictionary<string, string>? grips = ReadJson<Dictionary<string, string>>(GripFile());
        if (grips == null || string.IsNullOrWhiteSpace(device))
            return null;
        string wanted = device.Trim();
        foreach (KeyValuePair<string, string> grip in grips)
        {
            if (string.Equals(grip.Key, wanted, StringComparison.OrdinalIgnoreCase) && grip.Value.Length > 0)
                return grip.Value;
        }
        string name = DeviceProbe.RemoveGuid(wanted);
        return grips.Where(g => g.Value.Length > 0 && string.Equals(DeviceProbe.RemoveGuid(g.Key), name, StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Value).FirstOrDefault();
    }

    public static void SetGrip(string device, string gripId)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(gripId))
            return;
        Dictionary<string, string> grips = ReadJson<Dictionary<string, string>>(GripFile()) ?? [];
        Dictionary<string, string> clean = new(grips, StringComparer.OrdinalIgnoreCase) { [device.Trim()] = gripId.Trim() };
        WriteJson(GripFile(), clean);
    }

    // What the user said a device is (the "Which one is it?" question): a catalog model id, or "class:<class>" for a
    // device JoyPro does not know (DeviceClasses). Kept in grips.json with the grips (same key: the device).
    public static string? ChoiceOf(string device) => GripOf(device);

    public static void SetChoice(string device, string choice) => SetGrip(device, choice);

    public const string ClassChoicePrefix = "class:";

    // ------------------------------------------------------------------ marker positions per photo (exact or none)

    private static List<DeviceLayoutSet>? curated;
    private static string curatedKey = string.Empty;

    // JoyPro's curated position sets (DB\DeviceLayouts next to the catalog): each one for exactly one photo content.
    public static List<DeviceLayoutSet> CuratedLayouts()
    {
        lock (Gate)
        {
            string folder = DevicePhotoDb.LayoutFolderOf(CatalogFolder());
            if (curated == null || curatedKey != folder)
            {
                curated = DeviceLayoutSet.LoadFolder(folder, null);
                curatedKey = folder;
            }
            return curated;
        }
    }

    // JoyPro's line drawings (DB\DeviceDrawings next to the catalog), read once.
    public static List<DeviceLineArt> LineArts() => DeviceLineArtDb.Cached(DeviceLineArtDb.FolderOf(CatalogFolder()));

    // The line drawings of a model, the most neutral view first, each with the content key of the photo it lies on (its
    // curated positions and sheet use that key): the photo's SHA-256 when a curated set or a photo reference of the
    // model has it, else the drawing's own key.
    public static List<(DeviceLineArt Art, string Key)> LineArtsOf(RefDevice model)
    {
        List<(DeviceLineArt, string)> result = [];
        if (model == null)
            return result;
        List<DeviceLineArt> arts = DeviceLineArtDb.OfModel(LineArts(), model.Id);
        if (arts.Count == 0)
            return result;
        List<string> hashes = CuratedLayouts().Where(s => string.Equals(s.Model, model.Id, StringComparison.OrdinalIgnoreCase)).Select(s => s.PhotoSha256)
            .Concat(model.Photos.Select(p => p.Sha256)).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (DeviceLineArt art in arts)
        {
            string sha = DeviceLineArtDb.PhotoHashOf(art, hashes);
            result.Add((art, sha.Length > 0 ? sha : art.Key));
        }
        // Drawings that carry JoyPro's positions first.
        return result.OrderByDescending(r => CuratedLayouts().Any(s => string.Equals(s.Model, model.Id, StringComparison.OrdinalIgnoreCase) &&
                                                                     string.Equals(s.PictureKey, r.Item2, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // JoyPro's curated positions exist for this picture content (a photo's hash, the key of a line drawing on it).
    public static bool HasCuratedPositions(string modelId, string pictureKey) =>
        CuratedLayouts().Any(s => string.Equals(s.Model, modelId, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(s.PictureKey, pictureKey, StringComparison.OrdinalIgnoreCase) && s.Markers.Count > 0);

    // The positions the user placed (or imported) on pictures: <data>\DeviceReference\Layouts\<model>.<picture>.json.
    public static string UserLayoutFolder() => Path.Combine(DataFolder(), "Layouts");

    private static string UserLayoutFile(string modelId, string pictureKey) =>
        Path.Combine(UserLayoutFolder(), DeviceVocabulary.Slug(modelId) + "." + DeviceVocabulary.Slug(pictureKey.Length > 16 ? pictureKey[..16] : pictureKey) + ".json");

    public static DeviceLayoutSet? UserLayout(string modelId, string pictureKey)
    {
        string file = UserLayoutFile(modelId, pictureKey);
        try
        {
            return File.Exists(file) ? DeviceLayoutSet.Parse(File.ReadAllText(file, Encoding.UTF8), file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string SaveUserLayout(DeviceLayoutSet set)
    {
        string file = UserLayoutFile(set.Model, set.PictureKey);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        DeviceLayoutSet stored = new()
        {
            Model = set.Model,
            PhotoKind = set.PhotoKind,
            PhotoUrl = set.PhotoUrl,
            PhotoSha256 = set.PhotoSha256,
            View = set.View,
            Markers = new Dictionary<string, double[]>(set.Markers, StringComparer.OrdinalIgnoreCase),
            Source = set.Source.Length > 0 ? set.Source : T("placed by the user"),
            Confidence = set.Confidence,
            Note = set.Note
        };
        File.WriteAllText(file, stored.ToJson(), new UTF8Encoding(false));
        return file;
    }

    public static void RemoveUserLayout(string modelId, string pictureKey)
    {
        string file = UserLayoutFile(modelId, pictureKey);
        if (File.Exists(file))
            File.Delete(file);
    }

    // Where the parts are on one picture of a model, by part id (0..1 of the picture): JoyPro's curated set for exactly
    // this photo content, overridden part by part by what the user placed. pictureKey = the photo's SHA-256. Empty =
    // JoyPro knows no position on it (then no marker is shown). source tells where they came from.
    public static Dictionary<string, double[]> KnownPositions(string modelId, string pictureKey, out string source)
    {
        Dictionary<string, double[]> positions = new(StringComparer.OrdinalIgnoreCase);
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(pictureKey))
            return positions;
        DeviceLayoutSet? curatedSet = CuratedLayouts().FirstOrDefault(s => string.Equals(s.Model, modelId, StringComparison.OrdinalIgnoreCase) &&
                                                                           string.Equals(s.PictureKey, pictureKey, StringComparison.OrdinalIgnoreCase));
        if (curatedSet != null)
        {
            foreach (KeyValuePair<string, double[]> pair in curatedSet.Markers)
                positions[pair.Key] = pair.Value;
            source = "JoyPro's positions for this photo";
        }
        DeviceLayoutSet? user = UserLayout(modelId, pictureKey);
        if (user != null && user.Markers.Count > 0)
        {
            foreach (KeyValuePair<string, double[]> pair in user.Markers)
                positions[pair.Key] = pair.Value;
            source = curatedSet != null ? "JoyPro's positions and yours" : "your positions";
        }
        return positions;
    }

    // ------------------------------------------------------------------ callout sheets (tables and lines) per picture

    // The tables and leader lines of one picture of a model (joypro-device-layout with "callouts" and the picture's
    // local copy): <data>\DeviceReference\Callouts\<model>.<picture>.json. The cheat sheets render these files.
    public static string CalloutFolder() => Path.Combine(DataFolder(), "Callouts");

    public static string CalloutFile(string modelId, string pictureKey) =>
        Path.Combine(CalloutFolder(), DeviceVocabulary.Slug(modelId) + "." + DeviceVocabulary.Slug(pictureKey.Length > 16 ? pictureKey[..16] : pictureKey) + ".json");

    public static DeviceLayoutSet? LoadCallouts(string modelId, string pictureKey)
    {
        string file = CalloutFile(modelId, pictureKey);
        try
        {
            return File.Exists(file) ? DeviceLayoutSet.Parse(File.ReadAllText(file, Encoding.UTF8), file) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // JoyPro's shipped sheet for exactly this picture (the curated set's tables and lines), as a copy the user may edit;
    // null when there is none.
    public static CalloutSheet? CuratedCallouts(string modelId, string pictureKey)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(pictureKey))
            return null;
        DeviceLayoutSet? set = CuratedLayouts().FirstOrDefault(s => s.Callouts != null &&
                                                                    string.Equals(s.Model, modelId, StringComparison.OrdinalIgnoreCase) &&
                                                                    string.Equals(s.PictureKey, pictureKey, StringComparison.OrdinalIgnoreCase));
        return set?.Callouts == null ? null : CalloutSheet.FromJsonObject(set.Callouts.ToJsonObject());
    }

    public static string SaveCallouts(DeviceLayoutSet set, string pictureKey)
    {
        string file = CalloutFile(set.Model, pictureKey);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, set.ToJson(includeLocal: true), new UTF8Encoding(false));
        return file;
    }

    public static void RemoveCallouts(string modelId, string pictureKey)
    {
        string file = CalloutFile(modelId, pictureKey);
        if (File.Exists(file))
            File.Delete(file);
    }

    private static string CalloutDevicesFile() => Path.Combine(CalloutFolder(), "devices.json");

    // The sheet a device's cheat sheets use: the one of the picture last shown for it on the Layout page.
    public static string? CalloutFileOf(string device)
    {
        Dictionary<string, string>? map = ReadJson<Dictionary<string, string>>(CalloutDevicesFile());
        string key = DeviceProbe.RemoveGuid(device ?? string.Empty).Trim();
        return map != null && key.Length > 0 && map.TryGetValue(key, out string? file) && File.Exists(file) ? file : null;
    }

    public static void SetCalloutFileOf(string device, string file)
    {
        string key = DeviceProbe.RemoveGuid(device ?? string.Empty).Trim();
        if (key.Length == 0)
            return;
        lock (Gate)
        {
            Dictionary<string, string> map = new(ReadJson<Dictionary<string, string>>(CalloutDevicesFile()) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if (map.TryGetValue(key, out string? old) && string.Equals(old, file, StringComparison.OrdinalIgnoreCase))
                return;
            map[key] = file;
            WriteJson(CalloutDevicesFile(), map);
        }
    }

    // A curated set made for this photo address whose content was another one: the maker changed the photo.
    public static bool CuratedPhotoChanged(string modelId, string url, string pictureKey) =>
        !string.IsNullOrWhiteSpace(url) &&
        CuratedLayouts().Any(s => string.Equals(s.Model, modelId, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(s.PhotoUrl, url, StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(s.PictureKey, pictureKey, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ the user's photo

    public static string? UserPhoto(string modelId)
    {
        string folder = Path.Combine(DataFolder(), "Photos");
        if (!Directory.Exists(folder))
            return null;
        string slug = DeviceVocabulary.Slug(modelId);
        return Directory.GetFiles(folder, slug + ".*").FirstOrDefault();
    }

    // Copies the user's picture of a model into JoyPro's folder (the original may move); replaces an earlier one.
    public static string SetUserPhoto(string modelId, string sourceFile)
    {
        string folder = Path.Combine(DataFolder(), "Photos");
        Directory.CreateDirectory(folder);
        string slug = DeviceVocabulary.Slug(modelId);
        foreach (string old in Directory.GetFiles(folder, slug + ".*"))
            File.Delete(old);
        string ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (ext.Length == 0)
            ext = ".png";
        string target = Path.Combine(folder, slug + ext);
        File.Copy(sourceFile, target, overwrite: true);
        RemovePictureMarkers(modelId, "user");
        return target;
    }

    public static void RemoveUserPhoto(string modelId)
    {
        string folder = Path.Combine(DataFolder(), "Photos");
        if (Directory.Exists(folder))
        {
            foreach (string old in Directory.GetFiles(folder, DeviceVocabulary.Slug(modelId) + ".*"))
                File.Delete(old);
        }
        RemovePictureMarkers(modelId, "user");
    }

    // ------------------------------------------------------------------ marker positions and legend texts per picture

    // Marker positions the user placed on one picture of a model (picture = schematic, vendor or user), 0..1 of the
    // picture, by marker id (the part's vocabulary id).
    public static Dictionary<string, double[]> PictureMarkers(string modelId, string picture)
    {
        Dictionary<string, double[]>? saved = ReadJson<Dictionary<string, double[]>>(MarkerFile(modelId, picture));
        return saved == null ? new(StringComparer.OrdinalIgnoreCase) : new(saved, StringComparer.OrdinalIgnoreCase);
    }

    public static void SavePictureMarkers(string modelId, string picture, IDictionary<string, double[]> markers)
    {
        Dictionary<string, double[]> clean = markers
            .Where(m => m.Value != null && m.Value.Length >= 2)
            .ToDictionary(m => m.Key, m => new[] { Math.Round(Math.Clamp(m.Value[0], 0, 1), 4), Math.Round(Math.Clamp(m.Value[1], 0, 1), 4) }, StringComparer.OrdinalIgnoreCase);
        WriteJson(MarkerFile(modelId, picture), clean);
    }

    public static void RemovePictureMarkers(string modelId, string picture)
    {
        string file = MarkerFile(modelId, picture);
        if (File.Exists(file))
            File.Delete(file);
    }

    private static string MarkerFile(string modelId, string picture) =>
        Path.Combine(DataFolder(), "Markers", DeviceVocabulary.Slug(modelId) + "." + DeviceVocabulary.Slug(picture) + ".json");

    // Legend texts the user typed for a model: the name of an input and the text shown for it, by input.
    public static Dictionary<string, LegendText> LegendTexts(string modelId)
    {
        Dictionary<string, LegendText>? saved = ReadJson<Dictionary<string, LegendText>>(LegendFile(modelId));
        return saved == null ? new(StringComparer.OrdinalIgnoreCase) : new(saved, StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveLegendTexts(string modelId, IDictionary<string, LegendText> texts)
    {
        WriteJson(LegendFile(modelId), texts.Where(t => !string.IsNullOrWhiteSpace(t.Value.Name) || !string.IsNullOrWhiteSpace(t.Value.Text))
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static string LegendFile(string modelId) => Path.Combine(DataFolder(), "Legend", DeviceVocabulary.Slug(modelId) + ".json");

    public sealed class LegendText
    {
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    // ------------------------------------------------------------------ user device definitions

    // Saves a definition (catalog format) into <data>\Devices\<id>.json; the catalog is read again.
    public static string SaveUserDefinition(RefDevice device)
    {
        string folder = UserDefinitionFolder();
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, DeviceVocabulary.Slug(device.Id) + ".json");
        device.Origin = "user";
        File.WriteAllText(file, DefinitionJson(device), new UTF8Encoding(false));
        Reload();
        return file;
    }

    // The definition as a shareable catalog file: {"vendor": ..., "devices": [ {...} ]}.
    public static string DefinitionJson(RefDevice device)
    {
        Dictionary<string, object> root = new(StringComparer.Ordinal)
        {
            ["vendor"] = device.Vendor.Length > 0 ? device.Vendor : "User",
            ["devices"] = new List<object> { DeviceCatalog.ToJson(device) }
        };
        return MiniJson.Write(root, indented: true);
    }

    // Reads a shared definition file (catalog format, one or several devices) into the user's definitions.
    public static List<RefDevice> ImportDefinitions(string file)
    {
        List<RefDevice> devices = DeviceCatalog.ParseFile(File.ReadAllText(file, Encoding.UTF8), file);
        foreach (RefDevice device in devices)
            SaveUserDefinition(device);
        return devices;
    }

    // ------------------------------------------------------------------ photos

    // The bounds of the device on a photo (0..1: left, top, right, bottom): the area that is not the plain background
    // around it. The whole picture when that cannot be told.
    public static double[] PhotoContentBounds(string file)
    {
        try
        {
            using SKBitmap? source = SKBitmap.Decode(file);
            if (source == null)
                return [0, 0, 1, 1];
            using SKBitmap cut = ImageCutout.Cut(source);
            int w = cut.Width, h = cut.Height;
            int left = w, top = h, right = -1, bottom = -1;
            for (int y = 0; y < h; y += 2)
            {
                for (int x = 0; x < w; x += 2)
                {
                    if (cut.GetPixel(x, y).Alpha < 64)
                        continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
            if (right <= left || bottom <= top || (right - left) * (bottom - top) < w * h / 50)
                return [0, 0, 1, 1];
            return [left / (double)w, top / (double)h, (right + 1) / (double)w, (bottom + 1) / (double)h];
        }
        catch (Exception)
        {
            return [0, 0, 1, 1];
        }
    }

    // A point of the schematic (0..1) moved onto a photo: the schematic's device bounds onto the photo's device
    // bounds. A first guess the user corrects by dragging.
    public static (double X, double Y) MapToPhoto(double x, double y, double[] schematicBody, double[] photoBody)
    {
        double sw = Math.Max(1e-6, schematicBody[2] - schematicBody[0]), sh = Math.Max(1e-6, schematicBody[3] - schematicBody[1]);
        double u = (x - schematicBody[0]) / sw, v = (y - schematicBody[1]) / sh;
        return (Math.Clamp(photoBody[0] + u * (photoBody[2] - photoBody[0]), 0, 1), Math.Clamp(photoBody[1] + v * (photoBody[3] - photoBody[1]), 0, 1));
    }

    // ------------------------------------------------------------------ json helpers

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static T? ReadJson<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8), JsonOptions) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }

    public static string Percent(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
}
