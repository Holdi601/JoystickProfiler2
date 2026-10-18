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
    // engine cockpits ...: the cockpit database (docs/cockpits.md). JoyPro's reference of real and fictional cockpits:
    // per aircraft and variant the stick and throttle controls with their functions, JoyPro's drawings, the panels,
    // sources and photo links. The GUI's Cockpits page reads the same files (linked CockpitCatalog); the setup wizard
    // takes a layout with "cockpits get <aircraft> <variant>".
    internal static partial class Program
    {
        private static CockpitCatalog cockpitCatalog;

        internal static CockpitCatalog LoadCockpitCatalog()
        {
            if (cockpitCatalog == null)
                cockpitCatalog = CockpitCatalog.Load(CockpitCatalog.CatalogFolder(MainStructure.PROGPATH));
            return cockpitCatalog;
        }

        // The JoyPro function ids and names (DB\Functions\slots.json).
        private static Dictionary<string, string> CockpitFunctionNames()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(EngineNlGroup.FunctionsFolder(), "slots.json");
            string overrideFolder = Environment.GetEnvironmentVariable("JOYPRO_FUNCTIONS_DB");
            if (!string.IsNullOrWhiteSpace(overrideFolder))
                file = Path.Combine(overrideFolder, "slots.json");
            if (!File.Exists(file))
                return names;
            List<object> slots = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as List<object>;
            if (slots == null)
                return names;
            foreach (Dictionary<string, object> slot in slots.OfType<Dictionary<string, object>>())
            {
                object id, name;
                if (slot.TryGetValue("id", out id) && id is string)
                    names[(string)id] = slot.TryGetValue("name", out name) && name is string ? (string)name : (string)id;
            }
            return names;
        }

        // The game actions of the function ids for a game and aircraft (DB\Functions\<game>.tsv: plane, slot, action ...).
        private static Dictionary<string, List<string>> CockpitGameActions(string game, string plane)
        {
            Dictionary<string, List<string>> actions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(game))
                return actions;
            string file = Path.Combine(EngineNlGroup.FunctionsFolder(), game + ".tsv");
            if (!File.Exists(file))
                return actions;
            string wanted = string.IsNullOrWhiteSpace(plane) ? game : plane;
            foreach (string line in File.ReadLines(file, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] cols = line.Split('\t');
                if (cols.Length < 3 || !string.Equals(cols[0], wanted, StringComparison.OrdinalIgnoreCase))
                    continue;
                List<string> list;
                if (!actions.TryGetValue(cols[1], out list))
                    actions[cols[1]] = list = new List<string>();
                if (!list.Contains(cols[2], StringComparer.OrdinalIgnoreCase))
                    list.Add(cols[2]);
            }
            return actions;
        }

        // cockpits list [query] [--game G] [--era E] [--role R] [--nation N] [--category fixed-wing|helicopter|space|fiction|real]
        // cockpits search <text> [--limit n]
        // cockpits get <aircraft> [<variant>] [--json]
        // cockpits check
        // cockpits render <aircraft> [<variant>] --png <file> [--device <device>] [--style dark|light] [--print-width <px>]
        // cockpits compare <aircraft> [<variant>] [--game <game>]   (with --profile: which functions the profile binds)
        // cockpits photos [query] [--coverage]
        // cockpits coverage
        // cockpits drawings
        // cockpits pictures build|status ...  (EngineCockpitPictures.cs)
        // cockpits guide <aircraft> [<variant>] [--lang de] [--search words] [--no-pictures]   the cockpit guide as a tree
        // cockpits guide --coverage [--game G] [--no-pictures]   per variant: its guide, picture set and what is explained
        private static int RunCockpits(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "list" : command[0].ToLowerInvariant();
            List<string> rest = command.Skip(1).ToList();
            PopFlag(rest, "--json");
            CockpitCatalog catalog = LoadCockpitCatalog();
            switch (action)
            {
                case "list":
                    return CockpitsList(catalog, rest, outPath, textOutput);
                case "search":
                    return CockpitsSearch(catalog, rest, outPath, textOutput);
                case "get":
                case "show":
                    return CockpitsGet(catalog, rest, outPath, textOutput);
                case "check":
                    return CockpitsCheck(catalog, rest, outPath, textOutput);
                case "render":
                    return CockpitsRender(catalog, rest, textOutput);
                case "compare":
                    return CockpitsCompare(catalog, rest, outPath, textOutput);
                case "photos":
                    return CockpitsPhotos(catalog, rest, outPath, textOutput);
                case "coverage":
                    return CockpitsCoverage(catalog, outPath, textOutput);
                case "drawings":
                    return CockpitsDrawings(catalog, outPath, textOutput);
                case "pictures":
                    return CockpitsPictures(rest, outPath, textOutput);
                case "guide":
                    return CockpitsGuide(catalog, rest, outPath, textOutput);
                default:
                    return Fail("Unknown cockpits action: " + action + " (list, search, get, check, render, compare, photos, coverage, drawings, pictures, guide)");
            }
        }

        private static CockpitFilter CockpitFilterOf(List<string> rest)
        {
            CockpitFilter filter = new CockpitFilter
            {
                Game = PopOptionValue(rest, "--game") ?? string.Empty,
                Era = PopOptionValue(rest, "--era") ?? string.Empty,
                Role = PopOptionValue(rest, "--role") ?? string.Empty,
                Nation = PopOptionValue(rest, "--nation") ?? string.Empty,
                Category = PopOptionValue(rest, "--category") ?? string.Empty
            };
            filter.Query = string.Join(" ", rest);
            return filter;
        }

        private static object CockpitAircraftSummary(CockpitCatalog catalog, CockpitAircraft a)
        {
            return new
            {
                a.Id, a.Name, a.Category, a.Fiction, a.Franchise, a.Maker, a.Nations, a.Role, a.Era, a.Aliases,
                Photos = a.Photos.Count,
                Variants = a.Variants.Select(v => catalog.Resolve(v)).Select(v => new
                {
                    v.Id, v.Name, v.Years, v.Seat, v.Confidence,
                    Games = v.Games.Select(g => new { g.Game, g.Plane }).ToList(),
                    Controls = v.Controls.Count
                }).ToList()
            };
        }

        private static int CockpitsList(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            CockpitFilter filter = CockpitFilterOf(rest);
            List<CockpitAircraft> found = catalog.Search(filter);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpits: " + found.Count.ToString(CultureInfo.InvariantCulture) + " aircraft, " +
                              found.Sum(a => a.Variants.Count).ToString(CultureInfo.InvariantCulture) + " variants");
                foreach (CockpitAircraft a in found)
                {
                    sb.AppendLine("  - " + a.Id + " | " + a.Name + " | " + a.Category + (a.Fiction ? " (fiction: " + a.Franchise + ")" : string.Empty) + " | " +
                                  a.Era + " | " + a.Role + " | " + string.Join(", ", a.Nations));
                    foreach (CockpitVariant v in a.Variants.Select(catalog.Resolve))
                        sb.AppendLine("      " + v.Id + " | " + v.Name + (v.Games.Count > 0 ? " | " + string.Join(", ", v.Games.Select(g => g.Plane.Length > 0 ? g.Game + ":" + g.Plane : g.Game)) : string.Empty));
                }
                foreach (string warning in catalog.Warnings)
                    sb.AppendLine("Warning: " + warning);
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(found.Select(a => CockpitAircraftSummary(catalog, a)).ToList()), outPath);
            }
            return 0;
        }

        private static int CockpitsSearch(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            string limitText = PopOptionValue(rest, "--limit");
            int limit;
            if (!int.TryParse(limitText ?? "50", NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit <= 0)
                limit = 50;
            CockpitFilter filter = CockpitFilterOf(rest);
            if (filter.Query.Trim().Length == 0)
                return Fail("cockpits search needs a text (an aircraft, a variant, a control or a function).");
            List<CockpitAircraft> aircraft = catalog.Search(filter).Take(limit).ToList();
            List<Tuple<CockpitVariant, CockpitControl, CockpitFunction>> controls = catalog.SearchControls(filter.Query, limit);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Aircraft: " + aircraft.Count.ToString(CultureInfo.InvariantCulture));
                foreach (CockpitAircraft a in aircraft)
                    sb.AppendLine("  - " + a.Id + " | " + a.Name + " | " + string.Join(", ", a.Variants.Select(v => v.Id)));
                sb.AppendLine("Controls: " + controls.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Tuple<CockpitVariant, CockpitControl, CockpitFunction> hit in controls)
                    sb.AppendLine("  - " + hit.Item1.Aircraft.Id + "/" + hit.Item1.Id + " | " + hit.Item2.Name + " " + CockpitSchematic.DirectionWords(hit.Item3.Dir) + " | " + hit.Item3.Text + (hit.Item3.Fn.Length > 0 ? " | " + hit.Item3.Fn : string.Empty));
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Aircraft = aircraft.Select(a => CockpitAircraftSummary(catalog, a)).ToList(),
                    Controls = controls.Select(h => new
                    {
                        Aircraft = h.Item1.Aircraft.Id, Variant = h.Item1.Id, Control = h.Item2.Id, ControlName = h.Item2.Name, h.Item2.Device,
                        h.Item3.Dir, h.Item3.Text, h.Item3.Fn
                    }).ToList()
                }), outPath);
            }
            return 0;
        }

        private static CockpitVariant CockpitVariantOf(CockpitCatalog catalog, List<string> rest, string verb, out string error)
        {
            error = null;
            if (rest.Count == 0)
            {
                error = "cockpits " + verb + " needs an aircraft (and optionally a variant): cockpits " + verb + " f-16 f-16c-blk50";
                return null;
            }
            string aircraft = rest[0];
            string variant = rest.Count > 1 ? string.Join(" ", rest.Skip(1)) : string.Empty;
            CockpitVariant v = catalog.FindVariant(aircraft, variant);
            if (v == null)
                error = catalog.Find(aircraft) == null ? "No aircraft '" + aircraft + "' in the cockpit database (cockpits list)." : "No variant '" + variant + "' of " + aircraft + ".";
            return v;
        }

        // The control's part of the control vocabulary for one direction (docs/devices.md): what Auto Assign and the setup
        // wizard place it on.
        internal static string CockpitVocabulary(CockpitControl c, CockpitFunction f)
        {
            return CockpitSchematic.Vocabulary(c, f);
        }

        private static int CockpitsGet(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            string error;
            CockpitVariant v = CockpitVariantOf(catalog, rest, "get", out error);
            if (v == null)
                return Fail(error);
            CockpitAircraft a = v.Aircraft;
            Dictionary<string, string> names = CockpitFunctionNames();
            List<KeyValuePair<CockpitGameRef, Dictionary<string, List<string>>>> games = v.Games
                .Select(g => new KeyValuePair<CockpitGameRef, Dictionary<string, List<string>>>(g, CockpitGameActions(g.Game, g.Plane))).ToList();
            List<object> devices = new List<object>();
            foreach (string device in CockpitCatalog.DevicesOf(v))
            {
                CockpitDrawing drawing = catalog.DrawingFor(v, device);
                List<CockpitControl> deviceControls = v.Controls.Where(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase)).ToList();
                Dictionary<CockpitControl, CockpitDrawingPart> placed = drawing == null ? new Dictionary<CockpitControl, CockpitDrawingPart>() : CockpitSchematic.Place(drawing, deviceControls);
                devices.Add(new
                {
                    Device = device,
                    Title = CockpitCatalog.DeviceTitle(device),
                    Drawing = drawing == null ? string.Empty : drawing.Id,
                    DrawingName = drawing == null ? string.Empty : drawing.Name,
                    Generic = drawing == null || drawing.Generic,
                    Controls = deviceControls.Select(c =>
                    {
                        CockpitDrawingPart part;
                        placed.TryGetValue(c, out part);
                        return new
                        {
                            c.Id, c.Name, c.Type, c.Where, c.Note,
                            Part = part == null ? c.PartId : part.Id,
                            Placed = part != null,
                            X = part == null ? 0 : Math.Round(part.X / drawing.Width, 4),
                            Y = part == null ? 0 : Math.Round(part.Y / drawing.Height, 4),
                            Functions = c.Functions.Select(f =>
                            {
                                string fnName;
                                return new
                                {
                                    f.Dir, f.Press, f.When, f.Text, f.Fn, f.Note,
                                    // The HOTAS table's cells: mode id -> text (and function id) where they differ per mode.
                                    By = f.By, FnBy = f.FnBy,
                                    FnName = f.Fn.Length > 0 && names.TryGetValue(f.Fn, out fnName) ? fnName : string.Empty,
                                    Vocabulary = CockpitVocabulary(c, f),
                                    Actions = f.Fn.Length == 0
                                        ? new List<object>()
                                        : games.Where(g => g.Value.ContainsKey(f.Fn))
                                            .SelectMany(g => g.Value[f.Fn].Select(act => (object)new { g.Key.Game, g.Key.Plane, Action = act })).ToList()
                                };
                            }).ToList()
                        };
                    }).ToList()
                });
            }
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(a.Name + " / " + v.Name + (v.Years.Length > 0 ? " (" + v.Years + ")" : string.Empty));
                sb.AppendLine("Aircraft: " + a.Id + " | Variant: " + v.Id + " | Confidence: " + v.Confidence + (a.Fiction ? " | Fiction: " + a.Franchise : string.Empty));
                if (v.Games.Count > 0)
                    sb.AppendLine("Games: " + string.Join(", ", v.Games.Select(g => g.Plane.Length > 0 ? g.Game + ":" + g.Plane : g.Game)));
                int n = 0;
                foreach (string device in CockpitCatalog.DevicesOf(v))
                {
                    CockpitDrawing drawing = catalog.DrawingFor(v, device);
                    sb.AppendLine(CockpitCatalog.DeviceTitle(device) + " (drawing: " + (drawing == null ? "none" : drawing.Id) + ")");
                    List<CockpitControl> listed = v.Controls.Where(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase)).ToList();
                    Dictionary<CockpitControl, CockpitDrawingPart> on = drawing == null ? new Dictionary<CockpitControl, CockpitDrawingPart>() : CockpitSchematic.Place(drawing, listed);
                    foreach (CockpitControl c in listed)
                    {
                        n++;
                        CockpitDrawingPart part;
                        string placement = drawing == null || !on.TryGetValue(c, out part) ? " (not on the drawing)" : drawing.Generic && part.Id != c.Id ? " (on generic part " + part.Id + ")" : string.Empty;
                        sb.AppendLine("  " + n.ToString(CultureInfo.InvariantCulture) + ". " + c.Name + " [" + c.Id + ", " + c.Type + "] - " + c.Where + placement);
                        foreach (CockpitFunction f in c.Functions)
                            sb.AppendLine("      " + (f.Dir.Length > 0 ? CockpitSchematic.DirectionWords(f.Dir) + ": " : string.Empty) + f.Text + (f.Fn.Length > 0 ? "  -> " + f.Fn : string.Empty));
                    }
                }
                if (v.Panels.Count > 0)
                {
                    sb.AppendLine("Panels:");
                    foreach (CockpitPanel p in v.Panels)
                        sb.AppendLine("  - " + p.Name + " (" + CockpitSchematic.AreaTitle(p.Area) + ")" + (p.Text.Length > 0 ? ": " + p.Text : string.Empty));
                }
                sb.AppendLine("Sources:");
                foreach (CockpitSource s in v.Sources)
                    sb.AppendLine("  - " + s.Title + " (" + s.Kind + ")" + (s.Url.Length > 0 ? " " + s.Url : string.Empty));
                EmitPayload(sb.ToString(), outPath);
                return 0;
            }
            EmitPayload(EngineSerialization.ToJson(new
            {
                Format = "joypro-cockpit-layout",
                Version = 1,
                Aircraft = new { a.Id, a.Name, a.Category, a.Fiction, a.Franchise, a.Maker, a.Nations, a.Role, a.Era, a.Summary, a.Aliases },
                Variant = new { v.Id, v.Name, v.Aliases, v.Years, v.Seat, v.SeatOf, v.Inherits, v.Confidence, v.Notes, v.Cockpit },
                Games = v.Games.Select(g => new { g.Game, g.Plane, g.Note }).ToList(),
                // The HOTAS table's columns; DefaultMode is the column the functions' Text and Fn come from.
                Modes = v.Modes.Select(m => new { m.Id, m.Name, m.Kind, m.Note }).ToList(),
                DefaultMode = v.DefaultModeId,
                Devices = devices,
                Overview = v.Overview == null ? null : new
                {
                    v.Overview.Drawing, v.Overview.Text,
                    Areas = v.Overview.Areas.Select(x => new { x.N, x.Name, x.Area, x.X, x.Y, x.Text, x.Panels, Source = v.Cite(x.Src, x.Page) }).ToList()
                },
                Panels = v.Panels.Select(p => new
                {
                    Id = p.Key, p.Name, p.Area, p.Text, p.Drawing, p.Confidence, Source = v.Cite(p.Src, p.Page),
                    Items = p.Items.Select(i => new { i.N, i.Name, i.Type, i.Positions, i.Text, i.X, i.Y, i.Fn }).ToList()
                }).ToList(),
                Sources = v.Sources.Select(s => new { s.Id, s.Title, s.Url, s.File, s.Kind, s.Covers }).ToList(),
                Photos = a.Photos.Where(p => p.Variant.Length == 0 || string.Equals(p.Variant, v.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new { p.Url, p.Page, p.View, p.Credit, p.License, p.Caption }).ToList(),
                Links = a.Links.Select(l => new { l.Title, l.Url }).ToList()
            }), outPath);
            return 0;
        }

        private static int CockpitsCheck(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            Dictionary<string, string> names = CockpitFunctionNames();
            // --structure: only what is wrong, not what is still missing (an overview, numbered panels).
            bool complete = !PopFlag(rest, "--structure");
            List<string> problems = catalog.Check(names.Count > 0 ? new HashSet<string>(names.Keys, StringComparer.OrdinalIgnoreCase) : null, complete);
            problems.AddRange(CockpitManualPages.CheckDrawings(catalog));
            // The cockpit guide (DB\Cockpits\guide): its rules and that its variants exist.
            problems.AddRange(CockpitGuideLibrary.Load(CockpitGuideLibrary.FolderOf(catalog.Folder)).Check(catalog).Select(p => "guide: " + p));
            // Every variant's sheets build and every drawn part lies inside its drawing.
            foreach (CockpitAircraft a in catalog.Aircraft)
            {
                foreach (CockpitVariant raw in a.Variants)
                {
                    CockpitVariant v = catalog.Resolve(raw);
                    try
                    {
                        foreach (CockpitSheet sheet in CockpitSchematic.SheetsOf(catalog, v))
                        {
                            CalloutCheck check = CalloutLayouter.Check(sheet.Sheet);
                            if (check.Overlaps > 0 || check.Outside > 0)
                                problems.Add(a.Id + "/" + v.Id + ": the " + sheet.Device + " sheet has " + check.Overlaps.ToString(CultureInfo.InvariantCulture) + " overlaps, " + check.Outside.ToString(CultureInfo.InvariantCulture) + " outside");
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add(a.Id + "/" + v.Id + ": the sheets do not build: " + ex.Message);
                    }
                }
            }
            int variants = catalog.Aircraft.Sum(a => a.Variants.Count);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpit Check: " + catalog.Aircraft.Count.ToString(CultureInfo.InvariantCulture) + " aircraft, " + variants.ToString(CultureInfo.InvariantCulture) +
                              " variants, " + catalog.Drawings.Count.ToString(CultureInfo.InvariantCulture) + " drawings");
                foreach (string p in problems)
                    sb.AppendLine("  - " + p);
                sb.AppendLine("Problems: " + problems.Count.ToString(CultureInfo.InvariantCulture));
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new { Aircraft = catalog.Aircraft.Count, Variants = variants, Drawings = catalog.Drawings.Count, Problems = problems }), outPath);
            }
            return problems.Count == 0 ? 0 : 1;
        }

        // cockpits render <aircraft> [<variant>] --page all|overview|stick|throttle|<device>|panels|panel:<id> [--png <file>]
        // [--pdf <file>] [--style dark|light] [--scale s] [--highlight words] [--pictures picture|drawing|none] [--drawing-style ...]: the Cockpits page's
        // manual pages. Several pages with --png: <file>.<page>.png each.
        private static int CockpitsRenderPages(CockpitCatalog catalog, List<string> rest, string which, string png, string pdf, bool textOutput)
        {
            string style = (PopOptionValue(rest, "--style") ?? "light").ToLowerInvariant();
            string scaleText = PopOptionValue(rest, "--scale");
            string highlight = PopOptionValue(rest, "--highlight") ?? string.Empty;
            // The pictures made from the user's games (cockpits pictures build): picture (default), drawing, or none.
            string pictures = (PopOptionValue(rest, "--pictures") ?? "picture").ToLowerInvariant();
            if (pictures != "picture" && pictures != "drawing" && pictures != "none")
                return Fail("--pictures is picture, drawing or none.");
            double scale = 1.0;
            if (scaleText != null && (!double.TryParse(scaleText, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0.1 || scale > 4))
                return Fail("Invalid --scale: " + scaleText);
            if (style != "dark" && style != "light")
                return Fail("--style is dark or light.");
            if (string.IsNullOrWhiteSpace(png) && string.IsNullOrWhiteSpace(pdf))
                return Fail("cockpits render --page needs --png <file> or --pdf <file>.");
            bool dark = style == "dark";
            DrawingStyle drawingStyle = DrawingStyle.FromOptions(rest, DrawingStyle.FromSettings(DeviceCatalog.DataRoot(), true)) ?? DrawingStyle.Default;
            string error;
            CockpitVariant v = CockpitVariantOf(catalog, rest, "render", out error);
            if (v == null)
                return Fail(error);
            ManualPageOptions options = new ManualPageOptions { Dark = dark, Colors = drawingStyle.Resolve(dark), Folder = catalog.Folder, Highlight = highlight };
            if (pictures != "none")
            {
                options.Pictures = CockpitPictureSet.For(v);
                options.PictureMode = pictures;
            }
            options.Guide = CockpitGuideTree.Build(catalog, v, options.Pictures, CockpitGuideLibrary.For(catalog.Folder));
            List<ManualPage> all = CockpitManualPages.Build(catalog, v, options);
            string w = which.ToLowerInvariant();
            List<ManualPage> pages = all.Where(p =>
                w == "all" || p.Id == w || p.Id == "device:" + w || p.Id == "game:" + w || (w == "panels" && p.Kind == "panel") || (w == "game" && p.Id.StartsWith("game:", StringComparison.Ordinal)) ||
                (w == "stick" && p.Kind == "device" && CockpitCatalog.IsStickDevice(p.Id.Substring(7))) ||
                (w == "throttle" && p.Kind == "device" && CockpitCatalog.IsThrottleDevice(p.Id.Substring(7)))).ToList();
            if (w == "stick" || w == "throttle")
                pages = pages.Take(1).ToList();
            if (pages.Count == 0)
                return Fail("No page '" + which + "' for " + v.Id + " (pages: " + string.Join(", ", all.Select(p => p.Id)) + ").");
            List<Bitmap> bitmaps = new List<Bitmap>();
            List<string> files = new List<string>();
            try
            {
                foreach (ManualPage page in pages)
                    bitmaps.Add(EngineManualPageRenderer.Render(page, scale, options.Colors, dark));
                if (!string.IsNullOrWhiteSpace(png))
                {
                    string full = Path.GetFullPath(png);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    for (int i = 0; i < pages.Count; i++)
                    {
                        string file = pages.Count == 1 ? full : Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + "." + CockpitCatalog.Slug(pages[i].Id) + ".png");
                        bitmaps[i].Save(file, ImageFormat.Png);
                        files.Add(file);
                    }
                }
                if (!string.IsNullOrWhiteSpace(pdf))
                {
                    string full = Path.GetFullPath(pdf);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    // A4 width for the page width.
                    EngineManualPageRenderer.SavePdf(bitmaps, full, 595.0 / bitmaps.Max(b => b.Width));
                    files.Add(full);
                }
            }
            finally
            {
                foreach (Bitmap b in bitmaps)
                    b.Dispose();
            }
            if (textOutput)
            {
                foreach (string f in files)
                    Console.WriteLine("Rendered: " + f);
                foreach (ManualPage page in pages)
                    Console.WriteLine("  - " + page.Id + " | " + page.Title + " | " + page.Items.Count.ToString(CultureInfo.InvariantCulture) + " items");
            }
            else
            {
                Console.WriteLine(EngineSerialization.ToJson(new { Files = files, Pages = pages.Select(p => new { p.Id, p.Kind, p.Title, p.Width, p.Height }).ToList() }));
            }
            return 0;
        }

        private static int CockpitsRender(CockpitCatalog catalog, List<string> rest, bool textOutput)
        {
            string pageOption = PopOptionValue(rest, "--page");
            string pdf = PopOptionValue(rest, "--pdf");
            if (pageOption != null || pdf != null)
                return CockpitsRenderPages(catalog, rest, pageOption ?? "all", PopOptionValue(rest, "--png"), pdf, textOutput);
            string png = PopOptionValue(rest, "--png");
            string device = PopOptionValue(rest, "--device");
            string style = (PopOptionValue(rest, "--style") ?? "dark").ToLowerInvariant();
            string widthText = PopOptionValue(rest, "--print-width");
            bool drawingOnly = PopFlag(rest, "--drawing-only");
            if (style != "dark" && style != "light")
                return Fail("--style is dark or light.");
            int printWidth = 0;
            if (widthText != null && (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out printWidth) || printWidth < 0))
                return Fail("Invalid --print-width: " + widthText);
            if (string.IsNullOrWhiteSpace(png))
                return Fail("cockpits render needs --png <file>.");
            string drawingId = PopOptionValue(rest, "--drawing");
            bool dark = style == "dark";
            CockpitVariant v;
            List<CockpitSheet> sheets;
            if (!string.IsNullOrWhiteSpace(drawingId))
            {
                // A drawing on its own: every part with its id and type (to check a drawing while it is made).
                CockpitDrawing drawing;
                if (!catalog.Drawings.TryGetValue(drawingId, out drawing))
                    return Fail("No drawing '" + drawingId + "' (cockpits drawings).");
                List<CockpitControl> parts = drawing.Parts.Where(p => p.Type != "label").Select(p => new CockpitControl
                {
                    Id = p.Id, Device = drawing.Kind, Name = p.Id, Type = p.Type, Where = p.Id,
                    Functions = new List<CockpitFunction> { new CockpitFunction { Text = p.Type + " r" + p.R.ToString("0", CultureInfo.InvariantCulture) } }
                }).ToList();
                v = new CockpitVariant { Id = drawing.Id, Name = drawing.Name, Aircraft = new CockpitAircraft { Id = "drawing", Name = "Drawing " + drawing.Id } };
                sheets = new List<CockpitSheet> { CockpitSchematic.Build(drawing, parts, drawing.Kind, 0) };
            }
            else
            {
                string error;
                v = CockpitVariantOf(catalog, rest, "render", out error);
                if (v == null)
                    return Fail(error);
                sheets = CockpitSchematic.SheetsOf(catalog, v)
                    .Where(s => string.IsNullOrWhiteSpace(device) || string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (sheets.Count == 0)
                return Fail("No drawing to render for " + v.Id + (string.IsNullOrWhiteSpace(device) ? string.Empty : " (" + device + ")") + ".");
            List<Bitmap> pages = new List<Bitmap>();
            try
            {
                foreach (CockpitSheet sheet in sheets)
                {
                    using (Bitmap picture = EngineSchematicBitmap.Render(sheet.Schematic, (int)Math.Round(sheet.Schematic.Width), dark))
                    {
                        if (drawingOnly)
                        {
                            pages.Add(new Bitmap(picture));
                            continue;
                        }
                        CalloutRenderOptions options = new CalloutRenderOptions
                        {
                            Dark = dark,
                            PrintWidth = printWidth,
                            Game = "Cockpit reference",
                            Plane = v.Aircraft.Name + " - " + v.Name,
                            Device = sheet.Title + (sheet.Drawing.Generic ? " (generic drawing)" : string.Empty)
                        };
                        pages.Add(EngineCalloutRenderer.Render(sheet.Sheet, picture, sheet.TextOf, options));
                    }
                }
                string full = Path.GetFullPath(png);
                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using (Bitmap page = SideBySide(pages, dark))
                    page.Save(full, ImageFormat.Png);
                if (textOutput)
                {
                    Console.WriteLine("Rendered: " + full);
                    foreach (CockpitSheet sheet in sheets)
                        Console.WriteLine("  - " + sheet.Device + " | " + sheet.Drawing.Id + " | tables " + sheet.Sheet.Tables.Count.ToString(CultureInfo.InvariantCulture) +
                                          " | lines " + sheet.Sheet.Lines.Count.ToString(CultureInfo.InvariantCulture) + " | not on the drawing " + sheet.Unplaced.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    Console.WriteLine(EngineSerialization.ToJson(new
                    {
                        File = full,
                        Sheets = sheets.Select(s => new { s.Device, Drawing = s.Drawing.Id, Tables = s.Sheet.Tables.Count, Lines = s.Sheet.Lines.Count, s.Unplaced }).ToList()
                    }));
                }
                return 0;
            }
            finally
            {
                foreach (Bitmap b in pages)
                    b.Dispose();
            }
        }

        // The device pages next to each other, tops aligned.
        private static Bitmap SideBySide(List<Bitmap> pages, bool dark)
        {
            int gap = pages.Count > 1 ? 32 : 0;
            int width = pages.Sum(p => p.Width) + gap * (pages.Count - 1);
            int height = pages.Max(p => p.Height);
            Bitmap page = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(page))
            {
                g.Clear(dark ? ColorTranslator.FromHtml("#2B3038") : Color.White);
                int x = 0;
                foreach (Bitmap p in pages)
                {
                    g.DrawImage(p, x, 0, p.Width, p.Height);
                    x += p.Width + gap;
                }
            }
            return page;
        }

        // Which of the variant's functions the profile has relations for (the game's action of the function for the
        // variant's aircraft in that game), and how they are bound.
        private static int CockpitsCompare(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            string onlyGame = PopOptionValue(rest, "--game");
            string error;
            CockpitVariant v = CockpitVariantOf(catalog, rest, "compare", out error);
            if (v == null)
                return Fail(error);
            List<object> rows = new List<object>();
            int matched = 0, total = 0;
            List<CockpitGameRef> games = v.Games.Where(g => string.IsNullOrWhiteSpace(onlyGame) || CockpitCatalog.GameMatches(g.Game, onlyGame)).ToList();
            List<KeyValuePair<CockpitGameRef, Dictionary<string, List<string>>>> actions = games
                .Select(g => new KeyValuePair<CockpitGameRef, Dictionary<string, List<string>>>(g, CockpitGameActions(g.Game, g.Plane))).ToList();
            StringBuilder sb = new StringBuilder();
            foreach (CockpitControl c in v.Controls)
            {
                foreach (CockpitFunction f in c.Functions)
                {
                    if (f.Fn.Length == 0)
                        continue;
                    total++;
                    List<object> relations = new List<object>();
                    foreach (KeyValuePair<CockpitGameRef, Dictionary<string, List<string>>> g in actions)
                    {
                        List<string> ids;
                        if (!g.Value.TryGetValue(f.Fn, out ids))
                            continue;
                        foreach (Relation r in InternalDataManagement.AllRelations.Values)
                        {
                            if (r == null || r.NODES == null)
                                continue;
                            bool has = r.NODES.Any(n => n != null && string.Equals(n.Game ?? "DCS", g.Key.Game, StringComparison.OrdinalIgnoreCase) &&
                                                        ids.Any(id => string.Equals(id, n.ID, StringComparison.OrdinalIgnoreCase)) &&
                                                        (g.Key.Plane.Length == 0 || n.AIRCRAFT == null || n.AIRCRAFT.Count == 0 || n.GetStateAircraft(g.Key.Plane) == PlaneState.ACTIVE));
                            if (!has)
                                continue;
                            Bind b = r.bind;
                            string input = b == null ? string.Empty : (!string.IsNullOrWhiteSpace(b.JAxis) ? b.JAxis : b.JButton ?? string.Empty);
                            relations.Add(new { Relation = r.NAME, g.Key.Game, g.Key.Plane, Device = b == null ? string.Empty : b.Joystick ?? string.Empty, Input = input });
                        }
                    }
                    if (relations.Count > 0)
                        matched++;
                    rows.Add(new { Control = c.Id, ControlName = c.Name, c.Device, f.Dir, f.Text, f.Fn, Relations = relations });
                    if (textOutput)
                    {
                        sb.AppendLine("  - " + c.Name + (f.Dir.Length > 0 ? " " + CockpitSchematic.DirectionWords(f.Dir) : string.Empty) + " | " + f.Text + " | " + f.Fn + " | " +
                                      (relations.Count == 0 ? "no relation" : string.Join("; ", relations.Select(o => EngineSerialization.ToJson(o)))));
                    }
                }
            }
            // Every game command the profile binds for the variant's games and aircraft (the cockpit guide ties them to the
            // game's cockpit controls: a DCS command id, an X-Plane command, an IL-2 action, an MSFS event).
            List<object> commands = new List<object>();
            foreach (Relation r in InternalDataManagement.AllRelations.Values)
            {
                if (r == null || r.NODES == null)
                    continue;
                Bind b = r.bind;
                string input = b == null ? string.Empty : (!string.IsNullOrWhiteSpace(b.JAxis) ? b.JAxis : b.JButton ?? string.Empty);
                foreach (CockpitGameRef g in games)
                {
                    foreach (var n in r.NODES.Where(n => n != null && !string.IsNullOrEmpty(n.ID) && string.Equals(n.Game ?? "DCS", g.Game, StringComparison.OrdinalIgnoreCase) &&
                                                         (g.Plane.Length == 0 || n.AIRCRAFT == null || n.AIRCRAFT.Count == 0 || n.GetStateAircraft(g.Plane) == PlaneState.ACTIVE)))
                        commands.Add(new { g.Game, Id = n.ID, Relation = r.NAME, Device = b == null ? string.Empty : b.Joystick ?? string.Empty, Input = input });
                }
            }
            if (textOutput)
            {
                EmitPayload("Compare: " + v.Aircraft.Id + "/" + v.Id + " | functions with a relation: " + matched.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) +
                            " | relations in the profile: " + InternalDataManagement.AllRelations.Count.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + sb, outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new { Aircraft = v.Aircraft.Id, Variant = v.Id, Matched = matched, Total = total, Functions = rows, Commands = commands }), outPath);
            }
            return 0;
        }

        // The cockpit guide of a variant (docs/cockpits.md "The cockpit guide"): areas > panels > controls with what each
        // does, the bigger picture, the positions, the game's commands and where each is shown on the manual pages.
        private static int CockpitsGuide(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            string language = PopOptionValue(rest, "--lang") ?? string.Empty;
            string search = PopOptionValue(rest, "--search") ?? string.Empty;
            bool noPictures = PopFlag(rest, "--no-pictures");
            string onlyGame = PopOptionValue(rest, "--game");
            if (PopFlag(rest, "--coverage"))
                return CockpitsGuideCoverage(catalog, onlyGame, noPictures, language, outPath, textOutput);
            string error;
            CockpitVariant v = CockpitVariantOf(catalog, rest, "guide", out error);
            if (v == null)
                return Fail(error);
            CockpitPictureSet pictures = noPictures ? null : CockpitPictureSet.For(v);
            CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, CockpitGuideLibrary.For(catalog.Folder), language);
            List<CockpitGuideNode> hits = search.Length > 0 ? tree.Search(search) : null;
            Func<CockpitGuideNode, object> node = null;
            node = n => new
            {
                n.Kind, n.Key, n.Name, n.Type, n.Text, n.Context, n.Note, n.Where, n.Count, n.Source, n.Shared, n.Generic,
                Positions = n.Positions.Select(p => new { p.Name, p.Text }).ToList(),
                Hits = n.Hits.Select(h => new { h.Page, h.Tag, h.N, h.Label }).ToList(),
                Commands = n.Commands.Select(c => new { c.Name, c.Category, c.Id }).ToList(),
                n.Fns, n.ControlIds,
                Children = n.Children.Select(node).ToList()
            };
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpit guide: " + v.Aircraft.Id + "/" + v.Id + " | guide: " + (tree.HasGuide ? tree.GuideId : "(none)") + " | language: " + tree.Language +
                              " | pictures: " + (pictures == null ? "-" : pictures.Game + "/" + System.IO.Path.GetFileName(pictures.Folder)) + " | areas: " + tree.Roots.Count.ToString(CultureInfo.InvariantCulture) + " | controls: " + tree.Controls.ToString(CultureInfo.InvariantCulture) +
                              ", explained: " + tree.Explained.ToString(CultureInfo.InvariantCulture) + " | game controls: " + tree.GameControlsExplained.ToString(CultureInfo.InvariantCulture) +
                              "/" + tree.GameControls.ToString(CultureInfo.InvariantCulture) + (pictures == null ? " (no pictures on this PC)" : string.Empty));
                Action<CockpitGuideNode, int> write = null;
                write = (n, depth) =>
                {
                    if (hits != null && !hits.Contains(n) && !n.Descendants().Any(hits.Contains))
                        return;
                    string pad = new string(' ', 2 + depth * 2);
                    sb.AppendLine(pad + (n.Kind == "area" ? "# " : n.Kind == "panel" ? "## " : "- ") + n.Name + (n.Type.Length > 0 ? " [" + n.Type + "]" : string.Empty) +
                                  (n.Hits.Count > 0 ? " @" + string.Join(",", n.Hits.Where(h => h.Page.Length > 0).Select(h => h.Page + (h.Tag.Length > 0 ? "#" + h.Tag : string.Empty)).Distinct().Take(3)) : string.Empty));
                    if (n.Text.Length > 0)
                        sb.AppendLine(pad + "    " + n.Text);
                    if (n.Context.Length > 0)
                        sb.AppendLine(pad + "    > " + n.Context);
                    if (n.Note.Length > 0)
                        sb.AppendLine(pad + "    (" + n.Note + ")");
                    foreach (CockpitGuidePosition p in n.Positions)
                        sb.AppendLine(pad + "    * " + p.Name + (p.Text.Length > 0 ? ": " + p.Text : string.Empty));
                    foreach (CockpitGuideNode c in n.Children)
                        write(c, depth + 1);
                };
                foreach (CockpitGuideNode r in tree.Roots)
                    write(r, 0);
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Aircraft = v.Aircraft.Id, Variant = v.Id, Guide = tree.GuideId, tree.Title, tree.Intro, tree.Language,
                    Pictures = pictures == null ? string.Empty : pictures.Game + "/" + System.IO.Path.GetFileName(pictures.Folder),
                    Controls = tree.Controls, tree.Explained, tree.GameControls, tree.GameControlsExplained,
                    Sources = tree.Sources.Select(x => new { x.Id, x.Title, x.Url, x.File }).ToList(),
                    Areas = tree.Roots.Select(node).ToList(),
                    Found = hits == null ? null : hits.Select(h => h.Key).ToList()
                }), outPath);
            }
            return 0;
        }

        // cockpits guide --coverage [--game G]: per variant its guide, its picture set and how much is explained.
        private static int CockpitsGuideCoverage(CockpitCatalog catalog, string onlyGame, bool noPictures, string language, string outPath, bool textOutput)
        {
            CockpitGuideLibrary library = CockpitGuideLibrary.For(catalog.Folder);
            List<object> rows = new List<object>();
            StringBuilder sb = new StringBuilder();
            int controls = 0, explained = 0, game = 0, gameExplained = 0, guided = 0, templated = 0;
            foreach (CockpitAircraft a in catalog.Listed)
            {
                foreach (CockpitVariant raw in a.Variants)
                {
                    CockpitVariant v = catalog.Resolve(raw);
                    if (!string.IsNullOrWhiteSpace(onlyGame) && !v.Games.Any(g => CockpitCatalog.GameMatches(g.Game, onlyGame)))
                        continue;
                    CockpitPictureSet pictures = noPictures ? null : CockpitPictureSet.For(v);
                    CockpitGuideTree tree = CockpitGuideTree.Build(catalog, v, pictures, library, language);
                    string set = pictures == null ? string.Empty : pictures.Game + "/" + System.IO.Path.GetFileName(pictures.Folder);
                    int c = tree.Controls, e = tree.Explained;
                    controls += c;
                    explained += e;
                    templated += tree.Templated;
                    game += tree.GameControls;
                    gameExplained += tree.GameControlsExplained;
                    if (tree.HasGuide)
                        guided++;
                    string games = string.Join(",", v.Games.Select(g => g.Game).Distinct());
                    rows.Add(new { Aircraft = a.Id, Variant = v.Id, Games = games, Guide = tree.GuideId, Pictures = set, Controls = c, Explained = e, tree.GameControls, tree.GameControlsExplained });
                    sb.AppendLine("  " + a.Id + "/" + v.Id + " | " + games + " | guide: " + (tree.HasGuide ? tree.GuideId : "-") + " | pictures: " + (set.Length > 0 ? set : "-") +
                                  " | controls " + e.ToString(CultureInfo.InvariantCulture) + "/" + c.ToString(CultureInfo.InvariantCulture) + " explained | game controls " +
                                  tree.GameControlsExplained.ToString(CultureInfo.InvariantCulture) + "/" + tree.GameControls.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (textOutput)
                EmitPayload("Cockpit guide coverage" + (string.IsNullOrWhiteSpace(onlyGame) ? string.Empty : " (" + onlyGame + ")") + ": variants " + rows.Count.ToString(CultureInfo.InvariantCulture) +
                            ", with a guide " + guided.ToString(CultureInfo.InvariantCulture) + " | controls explained " + explained.ToString(CultureInfo.InvariantCulture) + "/" + controls.ToString(CultureInfo.InvariantCulture) +
                            ", with a general description " + templated.ToString(CultureInfo.InvariantCulture) +
                            " | game controls explained " + gameExplained.ToString(CultureInfo.InvariantCulture) + "/" + game.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + sb, outPath);
            else
                EmitPayload(EngineSerialization.ToJson(new { Variants = rows.Count, Guided = guided, Controls = controls, Explained = explained, Templated = templated, GameControls = game, GameControlsExplained = gameExplained, Rows = rows }), outPath);
            return 0;
        }

        private static int CockpitsPhotos(CockpitCatalog catalog, List<string> rest, string outPath, bool textOutput)
        {
            bool coverage = PopFlag(rest, "--coverage");
            string query = string.Join(" ", rest);
            List<CockpitAircraft> aircraft = query.Trim().Length == 0 ? catalog.Listed.ToList() : catalog.Search(new CockpitFilter { Query = query });
            var photos = aircraft.SelectMany(a => a.Photos.Select(p => new { Aircraft = a.Id, a.Fiction, p.Url, p.Page, p.View, p.Credit, p.License, p.Variant, p.Caption, p.Confidence })).ToList();
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpit photos: " + photos.Count.ToString(CultureInfo.InvariantCulture) + " of " + aircraft.Count(a => a.Photos.Count > 0).ToString(CultureInfo.InvariantCulture) + "/" +
                              aircraft.Count.ToString(CultureInfo.InvariantCulture) + " aircraft");
                if (!coverage)
                {
                    foreach (var p in photos)
                        sb.AppendLine("  - " + p.Aircraft + " | " + p.View + " | " + (p.Url.Length > 0 ? p.Url : "(page only)") + " | " + p.Page + " | " + p.License);
                }
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(coverage
                    ? (object)new { Photos = photos.Count, Aircraft = aircraft.Count, WithPhotos = aircraft.Count(a => a.Photos.Count > 0) }
                    : photos), outPath);
            }
            return 0;
        }

        private static int CockpitsCoverage(CockpitCatalog catalog, string outPath, bool textOutput)
        {
            List<CockpitAircraft> listed = catalog.Listed.ToList();
            List<CockpitVariant> variants = listed.SelectMany(a => a.Variants.Select(catalog.Resolve)).ToList();
            var byCategory = listed.GroupBy(a => a.Fiction ? "fiction" : a.Category).OrderBy(g => g.Key)
                .Select(g => new { Category = g.Key, Aircraft = g.Count(), Variants = g.Sum(a => a.Variants.Count) }).ToList();
            var byGame = variants.SelectMany(v => v.Games.Select(g => new { g.Game, Variant = v })).GroupBy(x => x.Game, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key).Select(g => new { Game = g.Key, Variants = g.Select(x => x.Variant.Id).Distinct().Count(), Aircraft = g.Select(x => x.Variant.Aircraft.Id).Distinct().Count() }).ToList();
            int ownDrawings = variants.Count(v => CockpitCatalog.DevicesOf(v).Any(d => { CockpitDrawing dr = catalog.DrawingFor(v, d); return dr != null && !dr.Generic; }));
            int withFn = variants.Sum(v => v.Controls.Sum(c => c.Functions.Count(f => f.Fn.Length > 0)));
            int functions = variants.Sum(v => v.Controls.Sum(c => c.Functions.Count));
            var confidence = variants.GroupBy(v => v.Confidence).OrderBy(g => g.Key).Select(g => new { Confidence = g.Key, Variants = g.Count() }).ToList();
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpit coverage: " + listed.Count.ToString(CultureInfo.InvariantCulture) + " aircraft, " + variants.Count.ToString(CultureInfo.InvariantCulture) + " variants, " +
                              catalog.Drawings.Count.ToString(CultureInfo.InvariantCulture) + " drawings (" + catalog.Drawings.Values.Count(d => !d.Generic).ToString(CultureInfo.InvariantCulture) + " of real grips)");
                foreach (var c in byCategory)
                    sb.AppendLine("  " + c.Category + ": " + c.Aircraft.ToString(CultureInfo.InvariantCulture) + " aircraft, " + c.Variants.ToString(CultureInfo.InvariantCulture) + " variants");
                sb.AppendLine("Games:");
                foreach (var g in byGame)
                    sb.AppendLine("  " + g.Game + ": " + g.Aircraft.ToString(CultureInfo.InvariantCulture) + " aircraft, " + g.Variants.ToString(CultureInfo.InvariantCulture) + " variants");
                sb.AppendLine("Variants with JoyPro's drawing of the real grip: " + ownDrawings.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Functions: " + functions.ToString(CultureInfo.InvariantCulture) + ", with a JoyPro function id: " + withFn.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Confidence: " + string.Join(", ", confidence.Select(c => (c.Confidence.Length > 0 ? c.Confidence : "-") + " " + c.Variants.ToString(CultureInfo.InvariantCulture))));
                sb.AppendLine("Photos: " + listed.Sum(a => a.Photos.Count).ToString(CultureInfo.InvariantCulture) + " links for " + listed.Count(a => a.Photos.Count > 0).ToString(CultureInfo.InvariantCulture) + " aircraft");
                // Controls no drawing shows (they go to "Other controls"): per drawing, with their types.
                List<string> unplaced = new List<string>();
                foreach (CockpitVariant v in variants)
                {
                    foreach (string device in CockpitCatalog.DevicesOf(v))
                    {
                        CockpitDrawing drawing = catalog.DrawingFor(v, device);
                        if (drawing == null)
                            continue;
                        List<CockpitControl> controls = v.Controls.Where(c => string.Equals(c.Device, device, StringComparison.OrdinalIgnoreCase) && c.Functions.Count > 0).ToList();
                        Dictionary<CockpitControl, CockpitDrawingPart> placed = CockpitSchematic.Place(drawing, controls);
                        foreach (CockpitControl c in controls.Where(c => !placed.ContainsKey(c)))
                            unplaced.Add(drawing.Id + " | " + v.Aircraft.Id + "/" + v.Id + " | " + c.Id + " | " + c.Type);
                    }
                }
                List<string> generic = variants.Where(v => !v.Aircraft.Fiction)
                    .SelectMany(v => CockpitCatalog.DevicesOf(v).Where(d => CockpitCatalog.IsStickDevice(d) || CockpitCatalog.IsThrottleDevice(d))
                        .Where(d => { CockpitDrawing dr = catalog.DrawingFor(v, d); return dr == null || dr.Generic; })
                        .Select(d => v.Aircraft.Id + "/" + v.Id + " " + d)).ToList();
                sb.AppendLine("Real aircraft devices on a generic drawing: " + generic.Count.ToString(CultureInfo.InvariantCulture));
                foreach (string g in generic)
                    sb.AppendLine("  * " + g);
                sb.AppendLine("Controls not on their drawing: " + unplaced.Count.ToString(CultureInfo.InvariantCulture));
                foreach (string u in unplaced)
                    sb.AppendLine("  - " + u);
                // The manual pages per game: variants, with an overview, HOTAS rows, mode columns, panels, items, drawings.
                sb.AppendLine("Manual pages per game (variants | overview | HOTAS rows | modal variants / mode columns | panels | items | line drawings):");
                foreach (var g in variants.SelectMany(v => (v.Games.Count > 0 ? v.Games.Select(x => x.Game) : new[] { v.Aircraft.Fiction ? "(fiction)" : "(real)" }).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Select(game => new { Game = game, V = v })).GroupBy(x => x.Game, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key))
                {
                    List<CockpitVariant> vs = g.Select(x => x.V).Distinct().ToList();
                    int[] s = ManualStats(catalog, vs);
                    sb.AppendLine("  " + g.Key + ": " + string.Join(" | ", s.Select(n => n.ToString(CultureInfo.InvariantCulture))));
                }
                int[] all = ManualStats(catalog, variants);
                sb.AppendLine("  all: " + string.Join(" | ", all.Select(n => n.ToString(CultureInfo.InvariantCulture))));
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Aircraft = listed.Count, Variants = variants.Count, Drawings = catalog.Drawings.Count,
                    RealDrawings = catalog.Drawings.Values.Count(d => !d.Generic), Categories = byCategory, Games = byGame,
                    VariantsWithRealDrawing = ownDrawings, Functions = functions, FunctionsWithId = withFn, Confidence = confidence,
                    Photos = listed.Sum(a => a.Photos.Count), AircraftWithPhotos = listed.Count(a => a.Photos.Count > 0)
                }), outPath);
            }
            return 0;
        }

        // variants, with an overview, HOTAS rows, modal variants, mode columns, panels, numbered items, line drawings.
        private static int[] ManualStats(CockpitCatalog catalog, List<CockpitVariant> vs)
        {
            int drawings = 0;
            foreach (CockpitVariant v in vs)
            {
                if (v.Overview != null && CockpitManualPages.LineArt(catalog.Folder, v.Overview.Drawing) != null)
                    drawings++;
                drawings += v.Panels.Count(p => p.Drawing.Length > 0);
                drawings += CockpitCatalog.DevicesOf(v).Count(d => CockpitManualPages.DeviceArt(catalog.Folder, v, d) != null);
            }
            return new[]
            {
                vs.Count, vs.Count(v => v.Overview != null && v.Overview.Areas.Count > 0),
                vs.Sum(v => v.Controls.Where(c => c.Device != "panel").Sum(c => c.Functions.Count)),
                vs.Count(v => v.Modes.Count > 0), vs.Sum(v => v.Modes.Count),
                vs.Sum(v => v.Panels.Count), vs.Sum(v => v.Panels.Sum(p => p.Items.Count)), drawings
            };
        }

        private static int CockpitsDrawings(CockpitCatalog catalog, string outPath, bool textOutput)
        {
            List<CockpitDrawing> drawings = catalog.Drawings.Values.OrderBy(d => d.Generic).ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Cockpit drawings: " + drawings.Count.ToString(CultureInfo.InvariantCulture));
                foreach (CockpitDrawing d in drawings)
                    sb.AppendLine("  - " + d.Id + " | " + d.Kind + " | " + d.Name + (d.Generic ? " | generic" : string.Empty) + " | parts: " + string.Join(", ", d.Parts.Select(p => p.Id)));
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(drawings.Select(d => new
                {
                    d.Id, d.Kind, d.Name, d.View, d.Generic, d.Origin, d.Width, d.Height,
                    Parts = d.Parts.Select(p => new { p.Id, p.Type, p.X, p.Y, p.R, p.Angle }).ToList()
                }).ToList()), outPath);
            }
            return 0;
        }
    }
}
