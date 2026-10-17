using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // engine devices reference ...: the device reference pictures (docs/device-reference.md). The catalog
    // (DB\Devices plus the user's own definitions in <data>\Devices), identification of a device from its USB id, name
    // and inputs, and JoyPro's own schematic of a model with the markers where its parts are.
    internal static partial class Program
    {
        internal static DeviceCatalog LoadReferenceCatalog()
        {
            return DeviceCatalog.Load(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH), DeviceCatalog.UserFolder());
        }

        // devices reference list [query]
        // devices reference identify --vidpid <VID:PID> --name <HID product name> [--instance <DirectInput name>]
        //     [--buttons <n>] [--hats <n>] [--axes X,Y,RZ]
        // devices reference show <id> [--base <id>] [--svg <file>] [--light] [--no-badges]
        // devices reference check [query]   (every model's schematic builds, all parts placed inside the picture)
        // devices reference drawings [query] [--check] [--model <id> [--view <view>] --svg <file> | --png <file>] [--light]
        //     (JoyPro's line drawings: per drawing the model, view, paths and how many curated positions it carries)
        private static int RunDevicesReference(List<string> args, string outPath, bool textOutput)
        {
            string action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            List<string> rest = args.Skip(1).ToList();
            DeviceCatalog catalog = LoadReferenceCatalog();
            switch (action)
            {
                case "list":
                {
                    List<RefDevice> devices = catalog.Search(string.Join(" ", rest));
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Device Reference Models: " + devices.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (RefDevice d in devices)
                        {
                            int known = d.AllControls.Count(c => c.Input.Length > 0);
                            sb.AppendLine("  - " + d.Id + " | " + d.Name + " | " + d.Kind + " | " + SchematicBuilder.ShapeOf(d) + " | inputs " +
                                          known.ToString(CultureInfo.InvariantCulture) + "/" + d.AllControls.Count().ToString(CultureInfo.InvariantCulture) +
                                          " | " + (d.VidPids.Count > 0 ? string.Join(",", d.VidPids) : "-") + " | " + d.Origin);
                        }
                        foreach (string warning in catalog.Warnings)
                            sb.AppendLine("Warning: " + warning);
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(EngineSerialization.ToJson(devices.Select(d => new
                        {
                            d.Id, d.Name, d.Vendor, d.Kind, d.Hand, d.Base, d.Confidence, d.Origin, d.VidPids, d.Names,
                            Shape = SchematicBuilder.ShapeOf(d),
                            Photo = DeviceCatalog.OfficialPhoto(d),
                            d.ProductUrl,
                            Inputs = d.AllControls.Count(c => c.Input.Length > 0),
                            Controls = d.AllControls.Count()
                        }).ToList()), outPath);
                    }
                    return 0;
                }
                case "identify":
                {
                    DeviceProbe probe = new DeviceProbe
                    {
                        VidPid = PopOptionValue(rest, "--vidpid") ?? string.Empty,
                        ProductName = PopOptionValue(rest, "--name") ?? string.Empty,
                        InstanceName = PopOptionValue(rest, "--instance") ?? string.Empty
                    };
                    string buttons = PopOptionValue(rest, "--buttons");
                    string hats = PopOptionValue(rest, "--hats");
                    string axes = PopOptionValue(rest, "--axes");
                    int n;
                    if (buttons != null)
                    {
                        if (!int.TryParse(buttons, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0)
                            return Fail("Invalid --buttons value: " + buttons);
                        probe.Buttons = n;
                    }
                    if (hats != null)
                    {
                        if (!int.TryParse(hats, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0)
                            return Fail("Invalid --hats value: " + hats);
                        probe.Hats = n;
                    }
                    if (axes != null)
                        probe.Axes = axes.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(a => a.Trim().ToUpperInvariant()).Select(a => a.StartsWith("JOY_", StringComparison.Ordinal) ? a : "JOY_" + a).ToList();
                    if (rest.Count > 0)
                        return Fail("Unknown devices reference identify argument(s): " + string.Join(" ", rest));
                    if (probe.VidPid.Length == 0 && probe.Name.Length == 0)
                        return Fail("devices reference identify needs --vidpid and/or --name.");
                    DeviceMatch match = DeviceIdentifier.Identify(catalog, probe);
                    DeviceQuestion question = DeviceChoices.Question(match, probe, catalog);
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Device: " + (probe.Name.Length > 0 ? probe.Name : "?") + " | " + (probe.VidPid.Length > 0 ? probe.VidPid : "?"));
                        sb.AppendLine("Match: " + (match.Model != null ? match.Model.Id + " | " + match.Model.Name : match.Choices.Count > 0 ? "grip needed" : "none"));
                        sb.AppendLine("Method: " + match.Method);
                        sb.AppendLine("Reason: " + match.Reason);
                        if (match.BaseModel != null)
                            sb.AppendLine("Base: " + match.BaseModel.Id);
                        foreach (RefDevice choice in match.Choices)
                            sb.AppendLine("  choice: " + choice.Id + " | " + choice.Name);
                        if (question != null)
                        {
                            sb.AppendLine("Ask: " + question.Kind + " - " + question.Title + (question.GuessedClass.Length > 0 ? " (looks like: " + question.GuessedClass + ")" : string.Empty));
                            foreach (RankedCandidate candidate in question.Candidates)
                                sb.AppendLine("  " + candidate.Score.ToString(CultureInfo.InvariantCulture) + "% " + candidate.Model.Id + " | " + candidate.Why);
                        }
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(EngineSerialization.ToJson(new
                        {
                            Model = match.Model == null ? string.Empty : match.Model.Id,
                            Name = match.Model == null ? string.Empty : match.Model.Name,
                            Base = match.BaseModel == null ? string.Empty : match.BaseModel.Id,
                            match.Method,
                            match.Reason,
                            Choices = match.Choices.Select(c => c.Id).ToList(),
                            Ask = question == null ? string.Empty : question.Kind,
                            AskTitle = question == null ? string.Empty : question.Title,
                            GuessedClass = question == null ? string.Empty : question.GuessedClass,
                            Candidates = question == null ? new List<object>() : question.Candidates.Select(c => (object)new { c.Model.Id, c.Score, c.Why }).ToList()
                        }), outPath);
                    }
                    return 0;
                }
                case "photos":
                    return RunDevicePhotos(catalog, rest, outPath, textOutput);
                case "photo-add":
                    return RunDevicePhotoAdd(catalog, rest, outPath);
                case "layout-check":
                    return RunDeviceLayoutCheck(catalog, rest, outPath);
                case "drawings":
                    return RunDeviceLineArt(catalog, rest, outPath, textOutput);
                case "show":
                {
                    string svg = PopOptionValue(rest, "--svg");
                    string baseId = PopOptionValue(rest, "--base");
                    bool light = PopFlag(rest, "--light");
                    bool noBadges = PopFlag(rest, "--no-badges");
                    if (rest.Count != 1)
                        return Fail("devices reference show needs one model id.");
                    RefDevice model = catalog.Find(rest[0]);
                    if (model == null)
                        return Fail("Device '" + rest[0] + "' is not in the device catalog.");
                    RefDevice baseModel = baseId != null ? catalog.Find(baseId) : catalog.BaseOf(model);
                    DeviceSchematic schematic = SchematicBuilder.Build(model, baseModel);
                    if (!string.IsNullOrWhiteSpace(svg))
                    {
                        string full = Path.GetFullPath(svg);
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        File.WriteAllText(full, schematic.ToSvg(!light, !noBadges), new UTF8Encoding(false));
                    }
                    if (textOutput)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine(model.Name + " (" + model.Id + ") | " + schematic.Shape + " | " + DeviceSchematic.F(schematic.Width) + " x " + DeviceSchematic.F(schematic.Height));
                        foreach (SchematicMarker m in schematic.Markers)
                        {
                            sb.AppendLine("  " + m.Number.ToString(CultureInfo.InvariantCulture) + ". " + m.Name + " [" + m.Type + ", " + m.Region + "] at " +
                                          m.X.ToString("0.000", CultureInfo.InvariantCulture) + "," + m.Y.ToString("0.000", CultureInfo.InvariantCulture));
                            foreach (SchematicInput input in m.Inputs)
                                sb.AppendLine("      " + (input.Input.Length > 0 ? input.Input : "?") + " = " + input.Name);
                        }
                        if (!string.IsNullOrWhiteSpace(svg))
                            sb.AppendLine("SVG: " + Path.GetFullPath(svg));
                        EmitPayload(sb.ToString(), outPath);
                    }
                    else
                    {
                        EmitPayload(EngineSerialization.ToJson(schematic), outPath);
                    }
                    return 0;
                }
                case "check":
                {
                    List<RefDevice> devices = catalog.Search(string.Join(" ", rest));
                    List<string> problems = new List<string>();
                    int markers = 0, extra = 0;
                    foreach (RefDevice model in devices)
                    {
                        try
                        {
                            DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
                            markers += schematic.Markers.Count;
                            extra += schematic.Markers.Count(m => m.Region == "extra");
                            problems.AddRange(schematic.Problems().Select(p => model.Id + ": " + p));
                        }
                        catch (Exception ex)
                        {
                            problems.Add(model.Id + ": schematic failed: " + ex.Message);
                        }
                    }
                    problems.AddRange(catalog.Warnings);
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Device Reference Check: " + devices.Count.ToString(CultureInfo.InvariantCulture) + " models, " +
                                  markers.ToString(CultureInfo.InvariantCulture) + " markers (" + extra.ToString(CultureInfo.InvariantCulture) + " in the extra strip)");
                    sb.AppendLine("Problems: " + problems.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (string problem in problems)
                        sb.AppendLine("  - " + problem);
                    EmitPayload(sb.ToString(), outPath);
                    return problems.Count == 0 ? 0 : 1;
                }
                default:
                    return Fail("Unknown devices reference action: " + action + " (use list, identify, show, check, photos, photo-add or layout-check).");
            }
        }

        // devices reference photos [query] [--coverage]: the reference photos per model (view, address, official host,
        // content hash) and the curated position sets; --coverage only the numbers.
        private static int RunDevicePhotos(DeviceCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            bool coverageOnly = PopFlag(rest, "--coverage");
            List<RefDevice> devices = catalog.Search(string.Join(" ", rest));
            List<DeviceLayoutSet> curated = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH)), catalog.Warnings);
            int grips = devices.Count(d => d.IsGrip);
            int withPhoto = devices.Count(d => DevicePhotoDb.OfficialPhotos(d).Count > 0);
            int multiView = devices.Count(d => DevicePhotoDb.OfficialPhotos(d).Count > 1);
            int withHash = devices.Count(d => DevicePhotoDb.OfficialPhotos(d).Any(p => p.Sha256.Length > 0));
            HashSet<string> curatedModels = new HashSet<string>(curated.Select(c => c.Model), StringComparer.OrdinalIgnoreCase);
            int withCurated = devices.Count(d => curatedModels.Contains(d.Id));
            int drawn = devices.Count(d =>
            {
                try
                {
                    return DeviceClasses.DrawingIsRecognizable(d, SchematicBuilder.Build(d, catalog.BaseOf(d)));
                }
                catch (Exception)
                {
                    return false;
                }
            });
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device photos: " + devices.Count.ToString(CultureInfo.InvariantCulture) + " models (" + grips.ToString(CultureInfo.InvariantCulture) + " grips)");
            sb.AppendLine("  with a reference photo: " + withPhoto.ToString(CultureInfo.InvariantCulture) + ", with several views: " + multiView.ToString(CultureInfo.InvariantCulture) +
                          ", with a photo hash on record: " + withHash.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("  with curated marker positions: " + withCurated.ToString(CultureInfo.InvariantCulture) + " (" + curated.Count.ToString(CultureInfo.InvariantCulture) + " position sets)");
            sb.AppendLine("  JoyPro drawing shown (recognizable): " + drawn.ToString(CultureInfo.InvariantCulture) + ", class outline otherwise: " + (devices.Count - drawn).ToString(CultureInfo.InvariantCulture));
            if (!coverageOnly)
            {
                foreach (RefDevice d in devices)
                {
                    List<DevicePhoto> photos = DevicePhotoDb.OfficialPhotos(d);
                    sb.AppendLine("  - " + d.Id + " | " + photos.Count.ToString(CultureInfo.InvariantCulture) + " photo(s)" + (curatedModels.Contains(d.Id) ? " | curated" : string.Empty));
                    foreach (DevicePhoto p in d.Photos)
                        sb.AppendLine("      " + p.View + " | " + p.Url + (DevicePhotoDb.IsOfficial(d, p.Url) ? string.Empty : " | NOT an official host") + (p.Sha256.Length > 0 ? " | sha256 " + p.Sha256.Substring(0, 12) : string.Empty));
                }
            }
            if (!textOutput && !coverageOnly)
            {
                EmitPayload(EngineSerialization.ToJson(devices.Select(d => new
                {
                    d.Id, d.Name, d.Kind, Grip = d.IsGrip,
                    Photos = d.Photos.Select(p => new { p.View, p.Url, p.Page, p.Sha256, p.Confidence, Official = DevicePhotoDb.IsOfficial(d, p.Url), p.Shows }).ToList(),
                    Curated = curated.Where(c => string.Equals(c.Model, d.Id, StringComparison.OrdinalIgnoreCase)).Select(c => new { c.View, c.PhotoUrl, c.PhotoSha256, Markers = c.Markers.Count }).ToList()
                }).ToList()), outPath);
                return 0;
            }
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        // devices reference photo-add --model <id> --url <https photo> --view <front|side|back|top|base|three-quarter|sheet>
        //     [--page <product page>] [--confidence high|medium|low] [--shows a,b] [--sha256 <hex>]
        // Adds (or updates) a photo reference in DB\DevicePhotos\<vendor>.json next to the catalog. The address must be on
        // one of the vendor's official hosts. The photo itself is never stored in the catalog.
        private static int RunDevicePhotoAdd(DeviceCatalog catalog, List<string> rest, string outPath)
        {
            string modelId = PopOptionValue(rest, "--model");
            string url = PopOptionValue(rest, "--url");
            string view = (PopOptionValue(rest, "--view") ?? "front").Trim().ToLowerInvariant();
            string page = PopOptionValue(rest, "--page") ?? string.Empty;
            string confidence = PopOptionValue(rest, "--confidence") ?? "medium";
            string shows = PopOptionValue(rest, "--shows") ?? string.Empty;
            string sha = (PopOptionValue(rest, "--sha256") ?? string.Empty).Trim().ToLowerInvariant();
            if (rest.Count > 0)
                return Fail("Unknown devices reference photo-add argument(s): " + string.Join(" ", rest));
            RefDevice model = catalog.Find(modelId);
            if (model == null)
                return Fail("Device '" + modelId + "' is not in the device catalog.");
            if (!DevicePhotoDb.IsOfficial(model, url))
                return Fail("The address is not on an official host of " + model.Vendor + " (" + string.Join(", ", model.OfficialHosts) + "): " + url);
            string folder = DevicePhotoDb.PhotoFolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, Path.GetFileNameWithoutExtension(model.File).Length > 0 ? Path.GetFileNameWithoutExtension(model.File) + ".json" : DeviceVocabulary.Slug(model.Vendor) + ".json");
            List<DevicePhoto> photos = File.Exists(file) ? DevicePhotoDb.ParsePhotoFile(File.ReadAllText(file, Encoding.UTF8)) : new List<DevicePhoto>();
            photos.RemoveAll(p => string.Equals(p.Model, model.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
            DevicePhoto photo = new DevicePhoto { Model = model.Id, Url = url, View = view, Page = page, Confidence = confidence, Sha256 = sha };
            photo.Shows.AddRange(shows.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));
            photos.Add(photo);
            Dictionary<string, object> root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["photos"] = photos.OrderBy(p => p.Model, StringComparer.Ordinal).ThenBy(p => p.View, StringComparer.Ordinal).Select(p => (object)DevicePhotoDb.PhotoToJson(p)).ToList()
            };
            WriteWithRetry(file, MiniJson.Write(root, true) + Environment.NewLine);
            EmitPayload("Photo reference added: " + model.Id + " | " + view + " | " + url + Environment.NewLine + "File: " + file + Environment.NewLine, outPath);
            return 0;
        }

        // Sync tools and virus scanners briefly map freshly written files ("user-mapped section open"): a short wait and a
        // retry instead of a crash.
        private static void WriteWithRetry(string file, string text)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(file, text, new UTF8Encoding(false));
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    System.Threading.Thread.Sleep(200 * attempt);
                }
            }
        }

        // devices reference layout-check: every curated position set (DB\DeviceLayouts) names a catalog model and one of
        // its referenced photos, carries that photo's content hash (and the same hash as the photo reference when it has
        // one), and only parts the model has. The exit code is the number of problems.
        private static int RunDeviceLayoutCheck(DeviceCatalog catalog, List<string> rest, string outPath)
        {
            List<string> warnings = new List<string>();
            List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH)), warnings);
            List<string> problems = new List<string>(warnings);
            foreach (DeviceLayoutSet set in sets)
            {
                string name = Path.GetFileName(set.File);
                RefDevice model = catalog.Find(set.Model);
                if (model == null)
                {
                    problems.Add(name + ": model '" + set.Model + "' is not in the catalog");
                    continue;
                }
                if (set.PhotoKind != "vendor")
                    problems.Add(name + ": curated sets are for the makers' photos (kind vendor)");
                DevicePhoto photo = model.Photos.FirstOrDefault(p => string.Equals(p.Url, set.PhotoUrl, StringComparison.OrdinalIgnoreCase));
                if (photo == null)
                    problems.Add(name + ": the photo is not a referenced photo of " + model.Id + ": " + set.PhotoUrl);
                if (set.PhotoSha256.Length != 64)
                    problems.Add(name + ": no SHA-256 of the photo content");
                else if (photo != null && photo.Sha256.Length > 0 && !string.Equals(photo.Sha256, set.PhotoSha256, StringComparison.OrdinalIgnoreCase))
                    problems.Add(name + ": the photo reference has another hash than the position set");
                Dictionary<string, SchematicMarker> parts = SchematicBuilder.Build(model, catalog.BaseOf(model)).Markers
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (string part in set.Markers.Keys.Concat(set.Candidates.Keys).Concat(set.Unplaced.Keys).Where(k => !parts.ContainsKey(k)))
                    problems.Add(name + ": '" + part + "' is no part of " + model.Id);
                foreach (string part in set.Markers.Keys.Where(k => set.Unplaced.ContainsKey(k)))
                    problems.Add(name + ": '" + part + "' is placed and also listed as not placed");
                // The control groups of a curated set are the catalog's: every input it lists belongs to that part.
                foreach (KeyValuePair<string, DeviceLayoutPart> pair in set.Parts)
                {
                    SchematicMarker marker;
                    if (!parts.TryGetValue(pair.Key, out marker))
                    {
                        problems.Add(name + ": parts lists '" + pair.Key + "', which is no part of " + model.Id);
                        continue;
                    }
                    foreach (DeviceLayoutPartInput input in pair.Value.Inputs.Where(i => !marker.Inputs.Any(m => string.Equals(m.Input, i.Input, StringComparison.OrdinalIgnoreCase) &&
                                                                                                           string.Equals(m.Control, i.Control, StringComparison.OrdinalIgnoreCase))))
                        problems.Add(name + ": " + pair.Key + " lists " + input.Input + " (" + input.Control + "), which the catalog does not give that part");
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device layouts: " + sets.Count.ToString(CultureInfo.InvariantCulture) + " curated position set(s), " + sets.Sum(s => s.Markers.Count).ToString(CultureInfo.InvariantCulture) + " positions");
            sb.AppendLine("Problems: " + problems.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string problem in problems)
                sb.AppendLine("  - " + problem);
            EmitPayload(sb.ToString(), outPath);
            return problems.Count == 0 ? 0 : 1;
        }

        // JoyPro's line drawings (DB\DeviceDrawings): the list with the positions each one carries, --check (every
        // drawing names a catalog model, its path data is plain M L Q C Z, a drawing of a curated photo has that photo's
        // size and its positions on the silhouette), and one drawing as SVG or PNG.
        private static int RunDeviceLineArt(DeviceCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            bool check = PopFlag(rest, "--check");
            bool light = PopFlag(rest, "--light");
            string modelId = PopOptionValue(rest, "--model");
            string view = PopOptionValue(rest, "--view");
            string svg = PopOptionValue(rest, "--svg");
            string png = PopOptionValue(rest, "--png");
            DrawingStyle style = DrawingStyle.FromOptions(rest, DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true)) ?? DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true);
            DrawingColors colors = style.Resolve(!light);
            string catalogFolder = DeviceCatalog.CatalogFolder(MainStructure.PROGPATH);
            List<string> warnings = new List<string>();
            List<DeviceLineArt> arts = DeviceLineArtDb.LoadFolder(DeviceLineArtDb.FolderOf(catalogFolder), warnings);
            List<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(catalogFolder), null);

            if (modelId != null)
            {
                DeviceLineArt art = DeviceLineArtDb.OfModel(arts, modelId).FirstOrDefault(a => view == null || string.Equals(a.View, view, StringComparison.OrdinalIgnoreCase));
                if (art == null)
                    return Fail("JoyPro has no line drawing of '" + modelId + "'" + (view != null ? " (" + view + ")" : string.Empty) + ".");
                if (!string.IsNullOrWhiteSpace(svg))
                {
                    string full = Path.GetFullPath(svg);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, art.ToSvg(colors), new UTF8Encoding(false));
                }
                if (!string.IsNullOrWhiteSpace(png))
                {
                    string full = Path.GetFullPath(png);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    using (System.Drawing.Bitmap picture = EngineLineArtBitmap.Render(art, (int)Math.Max(64, Math.Min(4000, art.Width * 2)), colors))
                        picture.Save(full, System.Drawing.Imaging.ImageFormat.Png);
                }
                EmitPayload(art.Model + " | " + art.View + " | " + art.Paths.Count.ToString(CultureInfo.InvariantCulture) + " paths | " +
                            art.Fills.Count.ToString(CultureInfo.InvariantCulture) + " fills | " + DeviceSchematic.F(art.Width) + " x " + DeviceSchematic.F(art.Height) +
                            " | style " + style + Environment.NewLine, outPath);
                return 0;
            }

            string query = string.Join(" ", rest).Trim();
            List<string> problems = new List<string>(warnings);
            StringBuilder sb = new StringBuilder();
            int withPositions = 0;
            HashSet<string> models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<object> rows = new List<object>();
            foreach (DeviceLineArt art in arts.Where(a => query.Length == 0 || a.Model.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string name = Path.GetFileName(art.File);
                RefDevice model = catalog.Find(art.Model);
                if (model == null)
                    problems.Add(name + ": model '" + art.Model + "' is not in the catalog");
                models.Add(art.Model);
                DeviceLayoutSet set = sets.FirstOrDefault(s => string.Equals(s.Model, art.Model, StringComparison.OrdinalIgnoreCase) &&
                                                               string.Equals(DeviceLineArt.OpaqueKey(s.PhotoSha256), art.Key, StringComparison.OrdinalIgnoreCase));
                int covered = 0, markers = set == null ? 0 : set.Markers.Count;
                if (set != null)
                {
                    withPositions++;
                    covered = set.Markers.Values.Count(xy => art.Covers(xy[0], xy[1], 0.03));
                    if (check && covered < markers)
                    {
                        foreach (KeyValuePair<string, double[]> pair in set.Markers.Where(p => !art.Covers(p.Value[0], p.Value[1], 0.03)))
                            problems.Add(name + ": the position of " + pair.Key + " is off the drawing's silhouette");
                    }
                    if (check && set.Callouts != null && (Math.Abs(set.Callouts.PictureWidth / set.Callouts.PictureHeight - art.Width / art.Height) > 0.01))
                        problems.Add(name + ": the drawing's aspect differs from its photo's sheet");
                }
                if (check)
                {
                    if (!art.Paths.Any(p => p.IsOutline))
                        problems.Add(name + ": no silhouette");
                    if (art.Key.Length != 16)
                        problems.Add(name + ": no key");
                    string raw = File.ReadAllText(art.File, Encoding.UTF8);
                    if (raw.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("<image", StringComparison.OrdinalIgnoreCase) >= 0)
                        problems.Add(name + ": carries an address or embedded picture data");
                }
                rows.Add(new { art.Model, art.View, art.Class, Paths = art.Paths.Count, Positions = markers, OnDrawing = covered, File = name });
                sb.AppendLine("  - " + art.Model + " | " + art.View + " | " + art.Class + " | " + art.Paths.Count.ToString(CultureInfo.InvariantCulture) + " paths | positions " +
                              (set == null ? "-" : covered.ToString(CultureInfo.InvariantCulture) + "/" + markers.ToString(CultureInfo.InvariantCulture)));
            }
            if (textOutput || check)
            {
                StringBuilder head = new StringBuilder();
                head.AppendLine("Line drawings: " + rows.Count.ToString(CultureInfo.InvariantCulture) + " of " + models.Count.ToString(CultureInfo.InvariantCulture) +
                                " model(s), " + withPositions.ToString(CultureInfo.InvariantCulture) + " with curated positions");
                head.Append(sb);
                if (check)
                {
                    head.AppendLine("Problems: " + problems.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (string problem in problems)
                        head.AppendLine("  - " + problem);
                }
                EmitPayload(head.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(rows), outPath);
            }
            return check && problems.Count > 0 ? 1 : 0;
        }
    }
}
