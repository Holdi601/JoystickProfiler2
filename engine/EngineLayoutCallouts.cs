using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // engine layout callouts: the Layout page's callout sheet (tables around the device picture, orthogonal leader lines;
    // docs/device-reference.md "Tables and lines") from the command line: arranged automatically for JoyPro's drawing of
    // a model or for a saved device layout, checked (overlaps, slanted lines, crossings, unlinked parts), rendered like
    // the cheat sheets of "layout render".
    internal static partial class Program
    {
        // layout callouts (--model <id> | --layout <device layout .json>) [--picture <file>] [--schematic] [--arrange]
        //     [--device <JoyPro device name>] [--game <game> --plane <plane>] [--sample] [--png <file>] [--light]
        //     [--print-width <px>] [--save <file.json>] [--drawing-style lines|cel|<spec>] [--drawing-preset joypro|manual|
        //     blueprint|print|custom] [--drawing-ink #rgb] [--drawing-paper #rgb] [--drawing-accent #rgb] [sheet style options,
        //     EngineSheetStyleOptions.cs; the layout's own style wins]
        // A model with JoyPro's line drawing of a curated photo gets that drawing, its positions and its shipped sheet;
        // --schematic (or a model without one) uses JoyPro's schematic drawing.

        // The curated set of a model whose photo JoyPro has a line drawing of, as a set on that drawing ("lineart"),
        // the most neutral view first; null when there is none.
        internal static DeviceLayoutSet LineArtSetOf(string modelId)
        {
            string catalogFolder = DeviceCatalog.CatalogFolder(MainStructure.PROGPATH);
            List<DeviceLineArt> arts = DeviceLineArtDb.Cached(DeviceLineArtDb.FolderOf(catalogFolder));
            if (arts.Count == 0)
                return null;
            IEnumerable<DeviceLayoutSet> sets = DeviceLayoutSet.LoadFolder(DevicePhotoDb.LayoutFolderOf(catalogFolder), null)
                .Where(s => string.Equals(s.Model, modelId, StringComparison.OrdinalIgnoreCase) && s.PhotoSha256.Length > 0)
                .OrderBy(s => s.PhotoKind == "lineart" ? 0 : 1).ThenBy(s => DeviceLineArtDb.ViewRank(s.View)).ThenByDescending(s => s.Markers.Count);
            foreach (DeviceLayoutSet curated in sets)
            {
                DeviceLineArt art = DeviceLineArtDb.OfPhoto(arts, modelId, curated.PhotoSha256);
                if (art == null)
                    continue;
                DeviceLayoutSet copy = DeviceLayoutSet.Parse(curated.ToJson(false), curated.File);
                copy.PhotoKind = "lineart";
                if (copy.Callouts == null)
                {
                    DeviceCatalog catalog = LoadReferenceCatalog();
                    RefDevice model = catalog.Find(modelId);
                    if (model != null)
                        copy.Callouts = CalloutLayouter.Arrange(SchematicBuilder.Build(model, catalog.BaseOf(model)), copy.Markers, art.Width, art.Height, null);
                }
                return copy;
            }
            return null;
        }
        private static int RunLayoutCallouts(List<string> args, string outPath, bool textOutput)
        {
            string modelId = PopOptionValue(args, "--model") ?? string.Empty;
            string layoutFile = PopOptionValue(args, "--layout") ?? string.Empty;
            string pictureFile = PopOptionValue(args, "--picture") ?? string.Empty;
            string device = PopOptionValue(args, "--device") ?? string.Empty;
            string game = PopOptionValue(args, "--game") ?? string.Empty;
            string plane = PopOptionValue(args, "--plane") ?? string.Empty;
            string png = PopOptionValue(args, "--png") ?? string.Empty;
            string save = PopOptionValue(args, "--save") ?? string.Empty;
            int printWidth = PopOptionInt(args, "--print-width", 3508);
            bool light = PopFlag(args, "--light");
            bool arrange = PopFlag(args, "--arrange");
            bool sample = PopFlag(args, "--sample");
            bool schematicOnly = PopFlag(args, "--schematic");
            SheetStyle sheetStyle = PopSheetStyleOptions(args, out string sheetStyleError);
            if (sheetStyleError.Length > 0)
                return Fail(sheetStyleError);
            DrawingStyle optionStyle = DrawingStyle.FromOptions(args, DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true));
            if (optionStyle != null)
                EngineCalloutRenderer.ExportStyle = optionStyle;
            else if (sheetStyle != null && sheetStyle.Drawing.Length > 0)
                EngineCalloutRenderer.ExportStyle = DrawingStyle.Parse(sheetStyle.Drawing);
            if (PopFlag(args, "--check-all"))
                return CheckAllCallouts(string.Join(" ", args), outPath);
            if (args.Count > 0 && modelId.Length == 0 && layoutFile.Length == 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                modelId = args[0];
                args.RemoveAt(0);
            }
            if (args.Count > 0)
                return Fail("Unknown layout callouts argument(s): " + string.Join(" ", args));
            if (modelId.Length == 0 && layoutFile.Length == 0)
                return Fail("layout callouts needs --model <catalog id> or --layout <device layout .json>.");
            if (game.Length > 0 != (plane.Length > 0))
                return Fail("layout callouts needs --game and --plane together.");

            List<string> warnings = new List<string>();
            DeviceCatalog catalog = LoadReferenceCatalog();
            DeviceLayoutSet set;
            if (layoutFile.Length > 0)
            {
                string path = NormalizePath(layoutFile);
                if (!File.Exists(path))
                    return Fail("Device layout not found: " + layoutFile);
                try
                {
                    set = DeviceLayoutSet.Parse(File.ReadAllText(path, Encoding.UTF8), path);
                }
                catch (Exception ex)
                {
                    return Fail("Device layout '" + layoutFile + "' could not be read: " + ex.Message);
                }
            }
            else
            {
                RefDevice found = catalog.Find(modelId);
                if (found == null)
                    return Fail("Unknown device model: " + modelId);
                DeviceSchematic drawing = SchematicBuilder.Build(found, catalog.BaseOf(found));
                // JoyPro's line drawing of the model with its curated positions and shipped sheet comes first (the
                // look of a manual's button diagram); --schematic asks for JoyPro's schematic drawing instead.
                DeviceLayoutSet lineArt = pictureFile.Length == 0 && !schematicOnly ? LineArtSetOf(found.Id) : null;
                if (lineArt != null)
                {
                    set = lineArt;
                }
                else
                {
                    // JoyPro's drawing knows every part's position; a picture of the user's without positions knows none.
                    set = new DeviceLayoutSet { Model = found.Id, PhotoKind = pictureFile.Length > 0 ? "user" : "drawing" };
                    if (pictureFile.Length == 0)
                    {
                        foreach (SchematicMarker m in drawing.Markers)
                            set.Markers[m.Id] = new[] { m.X, m.Y };
                    }
                }
            }

            RefDevice model = catalog.Find(set.Model);
            if (model == null && set.Callouts == null)
                return Fail("The device layout names a model that is not in the catalog: " + set.Model);

            Bitmap picture = null;
            try
            {
                CalloutSheet sheet;
                if (set.Callouts != null && !arrange)
                {
                    sheet = set.Callouts;
                }
                else
                {
                    DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
                    double pw = schematic.Width, ph = schematic.Height;
                    if (set.Callouts != null)
                    {
                        pw = set.Callouts.PictureWidth;
                        ph = set.Callouts.PictureHeight;
                    }
                    else if (pictureFile.Length > 0)
                    {
                        using (Image probe = Image.FromFile(NormalizePath(pictureFile)))
                        {
                            pw = probe.Width;
                            ph = probe.Height;
                        }
                    }
                    else if (set.PhotoKind == "lineart")
                    {
                        // A set on JoyPro's drawing: the drawing's own frame.
                        DeviceLineArt art = DeviceLineArtDb.OfPhoto(DeviceLineArtDb.Cached(DeviceLineArtDb.FolderOf(DeviceCatalog.CatalogFolder(MainStructure.PROGPATH))), set.Model, set.PhotoSha256);
                        if (art != null)
                        {
                            pw = art.Width;
                            ph = art.Height;
                        }
                    }
                    sheet = CalloutLayouter.Arrange(schematic, set.Markers, pw, ph, set.Callouts);
                }
                set.Callouts = sheet;
                CalloutCheck check = CalloutLayouter.Check(sheet);

                if (png.Length > 0)
                {
                    // The layout's own sheet style over the given one; its page colour says whether the page is dark.
                    SheetStyle style = sheet.Style ?? sheetStyle;
                    if (style != null && !light && !style.IsDarkPage(true))
                        light = true;
                    if (sheet.Style != null && sheet.Style.Drawing.Length > 0 && optionStyle == null)
                        EngineCalloutRenderer.ExportStyle = DrawingStyle.Parse(sheet.Style.Drawing);
                    picture = EngineCalloutRenderer.LoadPicture(set, sheet, pictureFile.Length > 0 ? NormalizePath(pictureFile) : null, !light, warnings);
                    Func<string, int, string> text;
                    if (sample)
                        text = (input, column) => column > 0 ? string.Empty : SampleText(sheet, input);
                    else if (device.Length > 0)
                        text = EngineLayoutRenderer.CalloutText(device, sheet, game.Length > 0 ? game : null, plane);
                    else
                        text = (input, column) => string.Empty;
                    string alias = device.Length > 0 ? EngineLayoutRenderer.ResolveJoystickAlias(device) : model != null ? model.Name : set.Model;
                    LayoutVisualOptions visual = new LayoutVisualOptions { LightStyle = light, PrintWidth = Math.Max(0, Math.Min(12000, printWidth)), Style = style };
                    CalloutRenderOptions options = game.Length > 0
                        ? EngineLayoutRenderer.CalloutOptions(visual, game, plane, alias)
                        : EngineLayoutRenderer.CalloutOptions(visual, "All games", "Relation names", alias);
                    EngineCalloutRenderer.ApplyStyle(options, set);
                    string target = NormalizePath(png);
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    using (Bitmap rendered = EngineCalloutRenderer.Render(sheet, picture, text, options))
                        rendered.Save(JoyPro.Engine.TestSandbox.EnsureWritable(target, "Image save"), ImageFormat.Png);
                }
                if (save.Length > 0)
                {
                    string target = NormalizePath(save);
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(target, set.ToJson(false), new UTF8Encoding(false));
                }

                if (textOutput)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Layout Callouts: " + set.Model + " | picture " + Size(sheet.PictureWidth, sheet.PictureHeight) + " | sheet " + Size(sheet.Width, sheet.Height));
                    sb.AppendLine("Check: " + check);
                    sb.AppendLine("Result: " + (check.Ok ? "ok" : "problems"));
                    foreach (CalloutTable t in sheet.Tables)
                    {
                        double[] r = sheet.RectOf(t);
                        sb.AppendLine("  - " + t.Id + " | " + t.Side + " | " + t.Title + " | rows " + t.VisibleRows().Count.ToString(CultureInfo.InvariantCulture) +
                                      " | " + Size(r[0], r[1]) + " " + Size(r[2], r[3]) + (sheet.LineOf(t.Id) != null ? " | line" : string.Empty));
                    }
                    foreach (string problem in check.Problems)
                        sb.AppendLine("Problem: " + problem);
                    foreach (string warning in warnings)
                        sb.AppendLine("Warning: " + warning);
                    EmitPayload(sb.ToString(), outPath);
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"Model\":").Append(EngineSerialization.ToJson(set.Model));
                    sb.Append(",\"Check\":").Append(EngineSerialization.ToJson(check));
                    sb.Append(",\"Warnings\":").Append(EngineSerialization.ToJson(warnings));
                    sb.Append(",\"Sheet\":").Append(MiniJson.Write(sheet.ToJsonObject(), indented: false));
                    sb.Append('}');
                    EmitPayload(sb.ToString(), outPath);
                }
                return 0;
            }
            catch (Exception ex)
            {
                return Fail("layout callouts failed: " + ex.Message);
            }
            finally
            {
                if (picture != null)
                    picture.Dispose();
            }
        }

        // layout titles --device <JoyPro device name>: for every game and aircraft the device's binds are active in, the
        // game's own menu title of each bound input ("JOY_BTN5", "Modifier+JOY_BTN5"), and the device's main modifier.
        private static int RunLayoutTitles(List<string> args, string outPath, bool textOutput)
        {
            string device = PopOptionValue(args, "--device") ?? string.Empty;
            if (args.Count > 0)
                return Fail("Unknown layout titles argument(s): " + string.Join(" ", args));
            if (device.Length == 0)
                return Fail("layout titles needs --device <JoyPro device name>.");
            SortedDictionary<string, SortedDictionary<string, string>> targets = EngineLayoutRenderer.TitlesFor(device);
            string modifier = EngineLayoutRenderer.PrimaryModifierOf(device);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Layout Titles: " + device + " | " + targets.Count.ToString(CultureInfo.InvariantCulture) + " aircraft | modifier " + (modifier.Length > 0 ? modifier : "-"));
                foreach (KeyValuePair<string, SortedDictionary<string, string>> target in targets)
                {
                    string[] parts = target.Key.Split('\u001F');
                    sb.AppendLine("  " + parts[0] + " / " + parts[1] + ": " + target.Value.Count.ToString(CultureInfo.InvariantCulture) + " titles");
                    foreach (KeyValuePair<string, string> title in target.Value)
                        sb.AppendLine("    " + title.Key + " = " + title.Value);
                }
                EmitPayload(sb.ToString(), outPath);
                return 0;
            }
            List<object> list = new List<object>();
            foreach (KeyValuePair<string, SortedDictionary<string, string>> target in targets)
            {
                string[] parts = target.Key.Split('\u001F');
                list.Add(new Dictionary<string, object> { { "Game", parts[0] }, { "Plane", parts[1] }, { "Titles", target.Value.ToDictionary(p => p.Key, p => (object)p.Value) } });
            }
            Dictionary<string, object> root = new Dictionary<string, object> { { "Device", device }, { "Modifier", modifier }, { "Targets", list } };
            EmitPayload(MiniJson.Write(root, indented: false), outPath);
            return 0;
        }

        // layout callouts --check-all [query]: the sheet of JoyPro's drawing of every catalog model (with inputs) is
        // arranged and checked. Exit code 1 when a sheet has overlapping tables, slanted lines, something outside the
        // canvas, a line through a table or a positioned part without a line.
        private static int CheckAllCallouts(string query, string outPath)
        {
            bool verbose = query.IndexOf("--verbose", StringComparison.Ordinal) >= 0;
            query = query.Replace("--verbose", string.Empty).Trim();
            DeviceCatalog catalog = LoadReferenceCatalog();
            int models = 0, bad = 0, tables = 0, lines = 0, crossings = 0, worst = 0, shared = 0;
            string worstModel = string.Empty;
            StringBuilder problems = new StringBuilder();
            foreach (RefDevice model in catalog.Search(query).Where(m => m.AllControls.Any(c => c.Input.Length > 0)).OrderBy(m => m.Id, StringComparer.Ordinal))
            {
                DeviceSchematic schematic = SchematicBuilder.Build(model, catalog.BaseOf(model));
                Dictionary<string, double[]> known = schematic.Markers.ToDictionary(m => m.Id, m => new[] { m.X, m.Y }, StringComparer.OrdinalIgnoreCase);
                CalloutSheet sheet = CalloutLayouter.Arrange(schematic, known, schematic.Width, schematic.Height, null);
                CalloutCheck check = CalloutLayouter.Check(sheet);
                models++;
                tables += check.Tables;
                lines += check.Lines;
                crossings += check.Crossings;
                shared += check.Shared;
                if (check.Crossings > worst)
                {
                    worst = check.Crossings;
                    worstModel = model.Id;
                }
                if (verbose)
                    problems.AppendLine("  " + model.Id + ": " + check.Tables.ToString(CultureInfo.InvariantCulture) + " tables, " + check.Crossings.ToString(CultureInfo.InvariantCulture) +
                                        " crossings, " + check.Shared.ToString(CultureInfo.InvariantCulture) + " shared");
                if (!check.Ok)
                {
                    bad++;
                    problems.AppendLine("  - " + model.Id + ": " + check + (check.Problems.Count > 0 ? " | " + check.Problems[0] : string.Empty));
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Callout Check: " + models.ToString(CultureInfo.InvariantCulture) + " models, " + tables.ToString(CultureInfo.InvariantCulture) + " tables, " +
                          lines.ToString(CultureInfo.InvariantCulture) + " lines, " + crossings.ToString(CultureInfo.InvariantCulture) + " crossings (most: " +
                          worst.ToString(CultureInfo.InvariantCulture) + (worstModel.Length > 0 ? " " + worstModel : string.Empty) + "), " +
                          shared.ToString(CultureInfo.InvariantCulture) + " shared tracks");
            sb.AppendLine("Problems: " + bad.ToString(CultureInfo.InvariantCulture));
            sb.Append(problems);
            EmitPayload(sb.ToString(), outPath);
            return bad == 0 ? 0 : 1;
        }

        // --sample: what a row shows on a preview without a profile: the input in short ("BTN 5", "POV1 up", "X").
        private static string SampleText(CalloutSheet sheet, string input)
        {
            string i = (input ?? string.Empty).ToUpperInvariant();
            if (i.StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
                return "POV" + DeviceVocabulary.HatNumber(i).ToString(CultureInfo.InvariantCulture) + " " + DeviceVocabulary.HatDirection(i);
            if (i.StartsWith("JOY_BTN", StringComparison.Ordinal))
                return "Button " + i.Substring(7);
            return i.StartsWith("JOY_", StringComparison.Ordinal) ? "Axis " + i.Substring(4) : i;
        }

        private static string Size(double a, double b)
        {
            return Math.Round(a).ToString(CultureInfo.InvariantCulture) + "x" + Math.Round(b).ToString(CultureInfo.InvariantCulture);
        }
    }
}
