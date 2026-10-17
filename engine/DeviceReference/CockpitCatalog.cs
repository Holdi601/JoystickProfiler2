#if NET
#nullable disable
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.DeviceReference
{
    // The cockpit database (docs/cockpits.md): JoyPro's own reference of real and fictional cockpits. Per aircraft its
    // variants (version, block, tranche, lot, mark, ship), per variant the controls of the stick and throttle (cyclic
    // and collective, yoke and quadrant, side-stick, hand controllers) with what each direction does and the JoyPro
    // function id it maps to, the key panels around the pilot, sources and links to real photos (shown only after the
    // user's consent). Read from DB\Cockpits\*.json (aircraft) and DB\Cockpits\drawings\*.json (JoyPro's own vector
    // drawings of the grips). Shared by the engine (.NET Framework, C# 7.3) and the GUI (linked file).

    public sealed class CockpitSource
    {
        // A short id the items of the variant cite ("src": "m1"); empty for a source only listed.
        public string Id = string.Empty;
        public string Title = string.Empty;
        public string Url = string.Empty;
        // A document that comes with a game, relative to the game's folder ("Mods/aircraft/A-10C_2/Doc/... EN.pdf"),
        // where it has no web address.
        public string File = string.Empty;
        // manual (the real aircraft's flight manual or a public excerpt), game-manual (DCS, BMS, IL-2, MSFS ...),
        // wikipedia, reference (books, museums, official pages), tutorial (videos, community guides), film (the work itself),
        // game-files (the game's own control lists and bind files).
        public string Kind = string.Empty;
        // What the source covers: "HOTAS", "panels", "variants" ...
        public string Covers = string.Empty;

        public CockpitSource Clone()
        {
            return (CockpitSource)MemberwiseClone();
        }
    }

    // A column of a variant's HOTAS table: a sensor of interest (TAD, TGP, HUD ...), a master mode (NAV, A-A, A-G), a
    // flight mode (SCM, NAV, quantum), a seat's mode or a condition. Every row of the table says what it does in each.
    public sealed class CockpitMode
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        // soi, master, mode, condition.
        public string Kind = string.Empty;
        public string Note = string.Empty;
    }

    // One numbered area of the cockpit overview (HUD, front dash, left console ...), where it is on the overview
    // drawing and the panels it holds.
    public sealed class CockpitOverviewArea
    {
        public int N;
        public string Name = string.Empty;
        // The plan area (glareshield, front-left ... see CockpitPanel.Area).
        public string Area = string.Empty;
        // Where the callout line ends on the overview drawing, 0..1 (negative = not placed).
        public double X = -1;
        public double Y = -1;
        public string Text = string.Empty;
        public List<string> Panels = new List<string>();
        public string Src = string.Empty;
        public string Page = string.Empty;
    }

    public sealed class CockpitOverview
    {
        // The line drawing of the whole cockpit (DB\Cockpits\line-drawings\<id>.json); empty = JoyPro's plan.
        public string Drawing = string.Empty;
        public string Text = string.Empty;
        public List<CockpitOverviewArea> Areas = new List<CockpitOverviewArea>();

        public CockpitOverview Clone()
        {
            CockpitOverview copy = (CockpitOverview)MemberwiseClone();
            copy.Areas = Areas.Select(a => new CockpitOverviewArea
            {
                N = a.N, Name = a.Name, Area = a.Area, X = a.X, Y = a.Y, Text = a.Text, Panels = new List<string>(a.Panels), Src = a.Src, Page = a.Page
            }).ToList();
            return copy;
        }
    }

    // One numbered control of a panel (a switch, knob, button, lever, gauge ...), as in the manual's numbered list.
    public sealed class CockpitPanelItem
    {
        public int N;
        public string Name = string.Empty;
        // switch2, switch3, knob, selector, button, lever, handle, wheel, guard, gauge, display, light, indicator, other.
        public string Type = string.Empty;
        public List<string> Positions = new List<string>();
        public string Text = string.Empty;
        public double X = -1;
        public double Y = -1;
        public string Fn = string.Empty;
        public string Src = string.Empty;
        public string Page = string.Empty;
    }

    public sealed class CockpitPhoto
    {
        // The picture's address (fetched on the user's PC after consent) and the page it is shown on.
        public string Url = string.Empty;
        public string Page = string.Empty;
        // cockpit, front-panel, stick, throttle, collective, cyclic, yoke, side-console, panel, exterior, still.
        public string View = string.Empty;
        public string Credit = string.Empty;
        // public-domain, cc0, cc-by-2.0, cc-by-sa-3.0, cc-by-sa-4.0, copyrighted, unknown.
        public string License = string.Empty;
        // The variant the photo shows (empty = the aircraft in general).
        public string Variant = string.Empty;
        public string Caption = string.Empty;
        public string Confidence = string.Empty;
    }

    public sealed class CockpitLink
    {
        public string Title = string.Empty;
        public string Url = string.Empty;
    }

    // One thing a control does in one direction ("fwd", "press", "stage2"; "" = the control's only action).
    public sealed class CockpitFunction
    {
        public string Dir = string.Empty;
        // short, long, press, hold, double, release ("" = any press).
        public string Press = string.Empty;
        // A condition the row needs: "ground", "air" (weight on wheels), a master mode, a seat ... ("" = always).
        public string When = string.Empty;
        // The text for every mode (shown where By has no entry for a mode).
        public string Text = string.Empty;
        // The JoyPro function id (DB\Functions\slots.json); empty when JoyPro has none for it.
        public string Fn = string.Empty;
        public string Note = string.Empty;
        // Mode id -> what the row does in that mode (the HOTAS table's cells); "" = no function in that mode.
        public Dictionary<string, string> By = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Mode id -> the JoyPro function id in that mode.
        public Dictionary<string, string> FnBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Src = string.Empty;
        public string Page = string.Empty;
        // Set by the catalog: Text was taken from the default mode's cell.
        public bool TextFromMode;

        public bool Modal { get { return By.Count > 0; } }

        // What the row does in a mode: its cell, else the text for every mode.
        public string TextIn(string mode)
        {
            string text;
            if (!string.IsNullOrEmpty(mode) && By.TryGetValue(mode, out text))
                return text ?? string.Empty;
            return TextFromMode ? string.Empty : Text;
        }

        public string FnIn(string mode)
        {
            string fn;
            if (!string.IsNullOrEmpty(mode) && FnBy.TryGetValue(mode, out fn))
                return fn ?? string.Empty;
            return By.ContainsKey(mode ?? string.Empty) && FnBy.Count > 0 ? string.Empty : Fn;
        }

        // The row's key within its control: direction, press and condition.
        public string RowKey { get { return Dir + "|" + Press + "|" + When; } }

        public CockpitFunction Clone()
        {
            CockpitFunction copy = (CockpitFunction)MemberwiseClone();
            copy.By = new Dictionary<string, string>(By, StringComparer.OrdinalIgnoreCase);
            copy.FnBy = new Dictionary<string, string>(FnBy, StringComparer.OrdinalIgnoreCase);
            return copy;
        }
    }

    public sealed class CockpitControl
    {
        // The control's id, the control vocabulary's part id where there is one ("stick.hat.tms", "throttle.china",
        // "collective.hat.1"): docs/devices.md. Unique within the variant.
        public string Id = string.Empty;
        // stick, throttle, cyclic, collective, yoke, sidestick, quadrant, rhc, thc, pedals, panel.
        public string Device = string.Empty;
        public string Name = string.Empty;
        // hat4, hat8, hat2, rocker, button, trigger, lever, switch2, switch3, slider, wheel, knob, ministick, axis,
        // gimbal, twist, detent, guard, encoder, selector.
        public string Type = string.Empty;
        // Where it sits and how it is reached ("4-way hat at the top right, under the thumb").
        public string Where = string.Empty;
        // The drawing's part the control is drawn on (default: Id). Lets a generic drawing carry any control.
        public string Part = string.Empty;
        public string Note = string.Empty;
        // The callout number on the device's page (the manual's figure number); 0 = numbered in list order.
        public int N;
        public string Src = string.Empty;
        public string Page = string.Empty;
        public List<CockpitFunction> Functions = new List<CockpitFunction>();

        public string PartId { get { return Part.Length > 0 ? Part : Id; } }

        public CockpitControl Clone()
        {
            CockpitControl copy = (CockpitControl)MemberwiseClone();
            copy.Functions = Functions.Select(f => f.Clone()).ToList();
            return copy;
        }
    }

    public sealed class CockpitPanel
    {
        // Unique within the variant ("left-front-dash"); the overview's areas name panels by it.
        public string Id = string.Empty;
        public string Name = string.Empty;
        // glareshield, front-left, front-center, front-right, left-console, right-console, center-pedestal, overhead,
        // left-side, right-side, floor, rear, hud, helmet, seat, stick, throttle.
        public string Area = string.Empty;
        public string Text = string.Empty;
        // JoyPro's line drawing of the panel (DB\Cockpits\line-drawings\<id>.json); empty = the numbered list only.
        public string Drawing = string.Empty;
        public string Src = string.Empty;
        public string Page = string.Empty;
        public string Confidence = string.Empty;
        public List<CockpitPanelItem> Items = new List<CockpitPanelItem>();

        public string Key { get { return Id.Length > 0 ? Id : CockpitCatalog.Slug(Name); } }

        public CockpitPanel Clone()
        {
            CockpitPanel copy = (CockpitPanel)MemberwiseClone();
            copy.Items = Items.Select(i => new CockpitPanelItem
            {
                N = i.N, Name = i.Name, Type = i.Type, Positions = new List<string>(i.Positions), Text = i.Text, X = i.X, Y = i.Y,
                Fn = i.Fn, Src = i.Src, Page = i.Page
            }).ToList();
            return copy;
        }
    }

    public sealed class CockpitGameRef
    {
        // DCS, FalconBMS, IL2Game, MSFS2020, MSFS2024, XPlane11, XPlane12, WarThunder, StarCitizen, EliteDangerous, X4,
        // VTOLVR, NuclearOption ... (JoyPro's game ids).
        public string Game = string.Empty;
        // The game's aircraft name where JoyPro's function tables know it (DB\Functions\<game>.tsv column "plane", e.g.
        // "F-16C_50"); empty = the game as a whole.
        public string Plane = string.Empty;
        public string Note = string.Empty;
    }

    public sealed class CockpitVariant
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public List<string> Aliases = new List<string>();
        public string Years = string.Empty;
        // Another variant's id whose data this one starts from (its own fields replace, its controls replace by id).
        public string Inherits = string.Empty;
        public List<string> RemoveControls = new List<string>();
        // Pilot, WSO, CPG, RIO, gunner ...: the seat these controls belong to (empty = the pilot / only seat).
        public string Seat = string.Empty;
        // A seat of another variant (the AH-64D CPG is a seat of the AH-64D): listed under that variant's seats.
        public string SeatOf = string.Empty;
        // The columns of the HOTAS table (empty = one column) and the one the setup wizard places by.
        public List<CockpitMode> Modes = new List<CockpitMode>();
        public string DefaultMode = string.Empty;
        public CockpitOverview Overview;
        public List<CockpitGameRef> Games = new List<CockpitGameRef>();
        public List<CockpitControl> Controls = new List<CockpitControl>();
        // Device -> drawing id (DB\Cockpits\drawings). A device without one gets the generic drawing of its kind.
        public Dictionary<string, string> Drawings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<CockpitPanel> Panels = new List<CockpitPanel>();
        public string Cockpit = string.Empty;
        public string Notes = string.Empty;
        public List<CockpitSource> Sources = new List<CockpitSource>();
        // high: the controls come from the aircraft's or the game's manual; medium: consistent secondary sources;
        // low: partly inferred (said in Notes).
        public string Confidence = string.Empty;

        // Set by the catalog.
        public CockpitAircraft Aircraft;
        public bool Resolved;

        public CockpitVariant Clone()
        {
            CockpitVariant copy = (CockpitVariant)MemberwiseClone();
            copy.Aliases = new List<string>(Aliases);
            copy.RemoveControls = new List<string>(RemoveControls);
            copy.Games = Games.Select(g => new CockpitGameRef { Game = g.Game, Plane = g.Plane, Note = g.Note }).ToList();
            copy.Controls = Controls.Select(c => c.Clone()).ToList();
            copy.Drawings = new Dictionary<string, string>(Drawings, StringComparer.OrdinalIgnoreCase);
            copy.Panels = Panels.Select(p => p.Clone()).ToList();
            copy.Sources = Sources.Select(s => s.Clone()).ToList();
            copy.Modes = Modes.Select(m => new CockpitMode { Id = m.Id, Name = m.Name, Kind = m.Kind, Note = m.Note }).ToList();
            copy.Overview = Overview == null ? null : Overview.Clone();
            return copy;
        }

        // The mode the setup wizard and the single-column views use: DefaultMode, else the first mode.
        public string DefaultModeId
        {
            get { return DefaultMode.Length > 0 ? DefaultMode : Modes.Count > 0 ? Modes[0].Id : string.Empty; }
        }

        public CockpitPanel Panel(string id)
        {
            return Panels.FirstOrDefault(p => string.Equals(p.Key, id ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        public CockpitSource Source(string id)
        {
            return string.IsNullOrEmpty(id) ? null : Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // "DCS A-10C II Flight Manual (EN), p. 109" for an item's src and page.
        public string Cite(string src, string page)
        {
            CockpitSource s = Source(src) ?? (Sources.Count == 1 && src.Length == 0 && page.Length > 0 ? Sources[0] : null);
            string title = s == null ? string.Empty : s.Title;
            if (page.Length > 0)
                title = (title.Length > 0 ? title + ", " : string.Empty) + "p. " + page;
            return title;
        }
    }

    public sealed class CockpitAircraft
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public List<string> Aliases = new List<string>();
        // fixed-wing, helicopter, space.
        public string Category = string.Empty;
        // A craft of a film, a series or a game: no drawing of its design, only a generic diagram and links.
        public bool Fiction;
        public string Franchise = string.Empty;
        public string Maker = string.Empty;
        public List<string> Nations = new List<string>();
        public string Role = string.Empty;
        // ww1, interwar, ww2, korea, cold-war-early, cold-war-late, modern, future.
        public string Era = string.Empty;
        public string Summary = string.Empty;
        // Only a template other variants inherit from: not listed.
        public bool Hidden;
        public List<CockpitPhoto> Photos = new List<CockpitPhoto>();
        public List<CockpitLink> Links = new List<CockpitLink>();
        public List<CockpitVariant> Variants = new List<CockpitVariant>();
        public string File = string.Empty;

        public bool IsSpace { get { return string.Equals(Category, "space", StringComparison.OrdinalIgnoreCase); } }

        public bool IsHelicopter { get { return string.Equals(Category, "helicopter", StringComparison.OrdinalIgnoreCase); } }
    }

    // A part JoyPro draws on a drawing (a hat, a button, a trigger ...) at X, Y (drawing units) with radius R and an
    // angle in degrees (0 = upright).
    public sealed class CockpitDrawingPart
    {
        public string Id = string.Empty;
        public string Type = "button";
        public double X, Y, R = 24, Angle;
        public string Label = string.Empty;
    }

    // JoyPro's own vector drawing of a grip, a throttle or a panel (DB\Cockpits\drawings\<id>.json).
    public sealed class CockpitDrawing
    {
        public string Id = string.Empty;
        // stick, throttle, cyclic, collective, yoke, sidestick, quadrant, rhc, thc, panel.
        public string Kind = string.Empty;
        public string Name = string.Empty;
        public string View = string.Empty;
        public string Origin = string.Empty;
        public bool Generic;
        public double Width = 1000, Height = 1000;
        public List<SchematicShape> Shapes = new List<SchematicShape>();
        public List<CockpitDrawingPart> Parts = new List<CockpitDrawingPart>();
        public string File = string.Empty;
        // Where the drawing is used without the variants naming it: variant ids and aircraft ids (every variant of the
        // aircraft, except those in "except"); the device is the drawing's kind unless "device" says otherwise.
        public List<string> ForVariants = new List<string>();
        public List<string> ForAircraft = new List<string>();
        public List<string> Except = new List<string>();
        public string Device = string.Empty;

        public string DeviceOrKind { get { return Device.Length > 0 ? Device : Kind; } }

        public CockpitDrawingPart PartOf(string id)
        {
            return Parts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class CockpitFilter
    {
        public string Query = string.Empty;
        public string Game = string.Empty;
        public string Era = string.Empty;
        public string Role = string.Empty;
        public string Nation = string.Empty;
        // fixed-wing, helicopter, space, fiction, real ("" = all).
        public string Category = string.Empty;
    }

    public sealed class CockpitCatalog
    {
        public const string FolderVariable = "JOYPRO_COCKPITS_DB";
        public const string FormatName = "joypro-cockpits";
        public const string DrawingFormatName = "joypro-cockpit-drawing";

        public List<CockpitAircraft> Aircraft = new List<CockpitAircraft>();
        public Dictionary<string, CockpitDrawing> Drawings = new Dictionary<string, CockpitDrawing>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
        public string Folder = string.Empty;

        private readonly Dictionary<string, CockpitVariant> variantsById = new Dictionary<string, CockpitVariant>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CockpitVariant> resolved = new Dictionary<string, CockpitVariant>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] StickDevices = { "stick", "cyclic", "yoke", "sidestick", "rhc" };
        public static readonly string[] ThrottleDevices = { "throttle", "collective", "quadrant", "thc" };
        public static readonly string[] Categories = { "fixed-wing", "helicopter", "space" };
        public static readonly string[] Eras = { "ww1", "interwar", "ww2", "korea", "cold-war-early", "cold-war-late", "modern", "future" };

        // DB\Cockpits next to the engine (or JOYPRO_COCKPITS_DB).
        public static string CatalogFolder(string engineFolder)
        {
            string overrideFolder = Environment.GetEnvironmentVariable(FolderVariable);
            if (!string.IsNullOrWhiteSpace(overrideFolder))
                return overrideFolder;
            return Path.Combine(engineFolder ?? string.Empty, "DB", "Cockpits");
        }

        public static CockpitCatalog Load(string folder)
        {
            CockpitCatalog catalog = new CockpitCatalog { Folder = folder ?? string.Empty };
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                catalog.Warnings.Add("No cockpit database at " + (folder ?? "(none)"));
                return catalog;
            }
            string drawings = Path.Combine(folder, "drawings");
            if (Directory.Exists(drawings))
            {
                foreach (string file in Directory.GetFiles(drawings, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        CockpitDrawing drawing = ParseDrawing(MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>);
                        drawing.File = Path.GetFileName(file);
                        if (drawing.Id.Length == 0)
                            drawing.Id = Path.GetFileNameWithoutExtension(file);
                        if (catalog.Drawings.ContainsKey(drawing.Id))
                            catalog.Warnings.Add("Drawing " + drawing.Id + " is defined twice (" + file + ")");
                        catalog.Drawings[drawing.Id] = drawing;
                    }
                    catch (Exception ex)
                    {
                        catalog.Warnings.Add(Path.GetFileName(file) + ": " + ex.Message);
                    }
                }
            }
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    catalog.AddFile(MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>, Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    catalog.Warnings.Add(Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            return catalog;
        }

        // Adds the aircraft of one file (format joypro-cockpits). Also used for drafts (tools/cockpit_db.py check).
        public void AddFile(Dictionary<string, object> root, string fileName)
        {
            if (root == null)
                throw new InvalidDataException("not a JSON object");
            string format = Text(root, "format");
            if (format.Length > 0 && !string.Equals(format, FormatName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("format is '" + format + "', not " + FormatName);
            foreach (Dictionary<string, object> item in Objects(root, "aircraft"))
            {
                CockpitAircraft aircraft = ParseAircraft(item);
                aircraft.File = fileName ?? string.Empty;
                if (aircraft.Id.Length == 0)
                {
                    Warnings.Add(fileName + ": an aircraft without id (" + aircraft.Name + ")");
                    continue;
                }
                if (Aircraft.Any(a => string.Equals(a.Id, aircraft.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Warnings.Add(fileName + ": aircraft " + aircraft.Id + " is defined twice");
                    continue;
                }
                Aircraft.Add(aircraft);
                foreach (CockpitVariant v in aircraft.Variants)
                {
                    v.Aircraft = aircraft;
                    if (v.Id.Length == 0)
                    {
                        Warnings.Add(fileName + ": a variant of " + aircraft.Id + " has no id");
                        continue;
                    }
                    if (variantsById.ContainsKey(v.Id))
                        Warnings.Add(fileName + ": variant " + v.Id + " is defined twice");
                    else
                        variantsById[v.Id] = v;
                }
            }
            resolved.Clear();
        }

        public IEnumerable<CockpitAircraft> Listed { get { return Aircraft.Where(a => !a.Hidden); } }

        public CockpitAircraft Find(string aircraft)
        {
            string key = (aircraft ?? string.Empty).Trim();
            if (key.Length == 0)
                return null;
            CockpitAircraft found = Aircraft.FirstOrDefault(a => string.Equals(a.Id, key, StringComparison.OrdinalIgnoreCase));
            if (found != null)
                return found;
            string norm = Norm(key);
            found = Aircraft.FirstOrDefault(a => Norm(a.Name) == norm || a.Aliases.Any(x => Norm(x) == norm));
            if (found != null)
                return found;
            // A variant's id or name names its aircraft too.
            CockpitVariant v = FindVariantAnywhere(key);
            return v == null ? null : v.Aircraft;
        }

        private CockpitVariant FindVariantAnywhere(string key)
        {
            CockpitVariant v;
            if (variantsById.TryGetValue(key, out v))
                return v;
            string norm = Norm(key);
            return variantsById.Values.FirstOrDefault(x => Norm(x.Name) == norm || x.Aliases.Any(a => Norm(a) == norm))
                   ?? VariantOfPlane(key);
        }

        // The variant a game's own aircraft name stands for ("F-16C_50", "FA-18C_hornet", "AH-64D_BLK_II_PLT"): the one
        // that lists the plane itself, else one that inherits it.
        public CockpitVariant VariantOfPlane(string plane)
        {
            string key = (plane ?? string.Empty).Trim();
            if (key.Length == 0)
                return null;
            Func<CockpitGameRef, bool> same = g => string.Equals(g.Plane, key, StringComparison.OrdinalIgnoreCase);
            return variantsById.Values.Where(v => v.Aircraft == null || !v.Aircraft.Hidden).FirstOrDefault(v => v.Games.Any(same))
                   ?? variantsById.Values.Where(v => v.Aircraft == null || !v.Aircraft.Hidden).FirstOrDefault(v => Resolve(v).Games.Any(same));
        }

        // The variant of an aircraft (by id, name or alias; null/empty = the first one), resolved (inheritance applied).
        public CockpitVariant FindVariant(string aircraft, string variant)
        {
            CockpitAircraft a = Find(aircraft);
            if (a == null)
            {
                CockpitVariant only = string.IsNullOrWhiteSpace(variant) ? FindVariantAnywhere(aircraft ?? string.Empty) : null;
                return only == null ? null : Resolve(only);
            }
            CockpitVariant v = null;
            string key = (variant ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                // "f-16c-blk50" given as the aircraft: that variant.
                CockpitVariant named = FindVariantAnywhere(aircraft);
                v = named != null && named.Aircraft == a && !string.Equals(a.Id, (aircraft ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
                    ? named
                    : a.Variants.FirstOrDefault();
            }
            else
            {
                string norm = Norm(key);
                v = a.Variants.FirstOrDefault(x => string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
                    ?? a.Variants.FirstOrDefault(x => Norm(x.Name) == norm || x.Aliases.Any(al => Norm(al) == norm))
                    ?? a.Variants.FirstOrDefault(x => Norm(x.Id).Contains(norm) || Norm(x.Name).Contains(norm));
            }
            return v == null ? null : Resolve(v);
        }

        public CockpitVariant Variant(string id)
        {
            CockpitVariant v;
            return variantsById.TryGetValue(id ?? string.Empty, out v) ? Resolve(v) : null;
        }

        // The variant with its parents' data (Inherits), cached. A cycle or a missing parent leaves the variant as it is
        // (Check reports it).
        public CockpitVariant Resolve(CockpitVariant variant)
        {
            if (variant == null)
                return null;
            if (variant.Resolved)
                return variant;
            CockpitVariant cached;
            if (resolved.TryGetValue(variant.Id, out cached))
                return cached;
            CockpitVariant result = ResolveChain(variant, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            ApplyDefaultMode(result);
            result.Resolved = true;
            if (variant.Id.Length > 0)
                resolved[variant.Id] = result;
            return result;
        }

        // A row with cells per mode and no text for every mode gets the default mode's cell as its text (and function
        // id): what the one-column views, the search and the setup wizard read.
        private static void ApplyDefaultMode(CockpitVariant v)
        {
            string mode = v.DefaultModeId;
            foreach (CockpitControl c in v.Controls)
            {
                foreach (CockpitFunction f in c.Functions)
                {
                    if (f.Text.Length == 0 && f.Modal)
                    {
                        string text;
                        if (!f.By.TryGetValue(mode, out text) || string.IsNullOrWhiteSpace(text))
                            text = v.Modes.Select(m => f.By.ContainsKey(m.Id) ? f.By[m.Id] : string.Empty).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                                   ?? f.By.Values.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
                        f.Text = text;
                        f.TextFromMode = true;
                    }
                    if (f.Fn.Length == 0 && f.FnBy.Count > 0)
                    {
                        string fn;
                        f.Fn = f.FnBy.TryGetValue(mode, out fn) && !string.IsNullOrEmpty(fn) ? fn : f.FnBy.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? string.Empty;
                    }
                }
            }
        }

        public static string Slug(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in (text ?? string.Empty).ToLowerInvariant())
            {
                if (ch < 128 && char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        private CockpitVariant ResolveChain(CockpitVariant variant, HashSet<string> seen)
        {
            CockpitVariant own = variant.Clone();
            own.Aircraft = variant.Aircraft;
            CockpitVariant parentRaw;
            if (variant.Inherits.Length == 0 || !seen.Add(variant.Id) || !variantsById.TryGetValue(variant.Inherits, out parentRaw))
                return own;
            CockpitVariant parent = ResolveChain(parentRaw, seen);
            CockpitVariant merged = parent.Clone();
            merged.Aircraft = variant.Aircraft;
            merged.Id = own.Id;
            merged.Name = own.Name;
            merged.Aliases = own.Aliases;
            merged.Inherits = own.Inherits;
            merged.RemoveControls = own.RemoveControls;
            if (own.Years.Length > 0) merged.Years = own.Years;
            if (own.Seat.Length > 0) merged.Seat = own.Seat;
            merged.SeatOf = own.SeatOf;
            if (own.Modes.Count > 0) merged.Modes = own.Modes;
            if (own.DefaultMode.Length > 0) merged.DefaultMode = own.DefaultMode;
            if (own.Overview != null) merged.Overview = own.Overview;
            if (own.Games.Count > 0) merged.Games = own.Games;
            if (own.Panels.Count > 0) merged.Panels = own.Panels;
            if (own.Cockpit.Length > 0) merged.Cockpit = own.Cockpit;
            merged.Notes = own.Notes.Length > 0 ? own.Notes : parent.Notes;
            if (own.Confidence.Length > 0) merged.Confidence = own.Confidence;
            foreach (KeyValuePair<string, string> d in own.Drawings)
                merged.Drawings[d.Key] = d.Value;
            merged.Controls.RemoveAll(c => own.RemoveControls.Any(r => string.Equals(r, c.Id, StringComparison.OrdinalIgnoreCase)));
            foreach (CockpitControl c in own.Controls)
            {
                int at = merged.Controls.FindIndex(x => string.Equals(x.Id, c.Id, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                    merged.Controls[at] = c;
                else
                    merged.Controls.Add(c);
            }
            List<CockpitSource> sources = new List<CockpitSource>(own.Sources);
            foreach (CockpitSource s in parent.Sources)
            {
                if (!sources.Any(x => string.Equals(x.Url, s.Url, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Title, s.Title, StringComparison.OrdinalIgnoreCase)))
                    sources.Add(s);
            }
            merged.Sources = sources;
            return merged;
        }

        // ------------------------------------------------------------------ searching

        public List<CockpitAircraft> Search(CockpitFilter filter)
        {
            filter = filter ?? new CockpitFilter();
            List<string> terms = Terms(filter.Query);
            List<KeyValuePair<CockpitAircraft, int>> hits = new List<KeyValuePair<CockpitAircraft, int>>();
            foreach (CockpitAircraft a in Listed)
            {
                if (!Matches(a, filter))
                    continue;
                int score = terms.Count == 0 ? 1 : Score(a, terms);
                if (score > 0)
                    hits.Add(new KeyValuePair<CockpitAircraft, int>(a, score));
            }
            return hits.OrderByDescending(h => terms.Count == 0 ? 0 : h.Value).ThenBy(h => h.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(h => h.Key).ToList();
        }

        private bool Matches(CockpitAircraft a, CockpitFilter f)
        {
            if (f.Era.Length > 0 && !string.Equals(a.Era, f.Era, StringComparison.OrdinalIgnoreCase))
                return false;
            if (f.Role.Length > 0 && a.Role.IndexOf(f.Role, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (f.Nation.Length > 0 && !a.Nations.Any(n => string.Equals(n, f.Nation, StringComparison.OrdinalIgnoreCase)))
                return false;
            string cat = f.Category.Trim().ToLowerInvariant();
            if (cat == "fiction" && !a.Fiction)
                return false;
            if (cat == "real" && a.Fiction)
                return false;
            if (cat.Length > 0 && cat != "fiction" && cat != "real" && !string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase))
                return false;
            if (f.Game.Length > 0 && !a.Variants.Any(v => Resolve(v).Games.Any(g => GameMatches(g.Game, f.Game))))
                return false;
            return true;
        }

        public static bool GameMatches(string game, string wanted)
        {
            string g = Norm(game), w = Norm(wanted);
            if (g.Length == 0 || w.Length == 0)
                return false;
            if (g == w)
                return true;
            // "MSFS" finds MSFS2020 and MSFS2024, "XPlane" both X-Planes, "IL2" the IL-2 games.
            return g.StartsWith(w, StringComparison.Ordinal) && w.Length >= 2;
        }

        private int Score(CockpitAircraft a, List<string> terms)
        {
            string head = Norm(a.Id + " " + a.Name + " " + string.Join(" ", a.Aliases));
            string body = Norm(a.Maker + " " + a.Franchise + " " + a.Role + " " + string.Join(" ", a.Nations) + " " + a.Summary);
            StringBuilder vb = new StringBuilder();
            foreach (CockpitVariant v in a.Variants)
            {
                vb.Append(' ').Append(v.Id).Append(' ').Append(v.Name).Append(' ').Append(string.Join(" ", v.Aliases));
                foreach (CockpitGameRef g in v.Games)
                    vb.Append(' ').Append(g.Game).Append(' ').Append(g.Plane);
            }
            string variants = Norm(vb.ToString());
            int score = 0;
            foreach (string t in terms)
            {
                if (head.Contains(t)) score += 10;
                else if (variants.Contains(t)) score += 6;
                else if (body.Contains(t)) score += 2;
                else return 0;
            }
            return score;
        }

        // A function / control search across all variants ("TMS", "china hat", "gear"): (variant, control, function).
        public List<Tuple<CockpitVariant, CockpitControl, CockpitFunction>> SearchControls(string query, int limit)
        {
            List<string> terms = Terms(query);
            List<Tuple<CockpitVariant, CockpitControl, CockpitFunction>> hits = new List<Tuple<CockpitVariant, CockpitControl, CockpitFunction>>();
            if (terms.Count == 0)
                return hits;
            foreach (CockpitAircraft a in Listed)
            {
                foreach (CockpitVariant raw in a.Variants)
                {
                    CockpitVariant v = Resolve(raw);
                    foreach (CockpitControl c in v.Controls)
                    {
                        string control = Norm(c.Id + " " + c.Name + " " + c.Where);
                        foreach (CockpitFunction f in c.Functions)
                        {
                            string text = control + " " + Norm(f.Text + " " + f.Fn);
                            if (terms.All(t => text.Contains(t)))
                            {
                                hits.Add(Tuple.Create(v, c, f));
                                if (limit > 0 && hits.Count >= limit)
                                    return hits;
                            }
                        }
                    }
                }
            }
            return hits;
        }

        private static List<string> Terms(string query)
        {
            return (query ?? string.Empty).Split(new[] { ' ', ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Norm).Where(t => t.Length > 0).Distinct().ToList();
        }

        public static string Norm(string text)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in (text ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else if (ch == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------ devices of a variant

        public static bool IsStickDevice(string device)
        {
            return StickDevices.Contains((device ?? string.Empty).ToLowerInvariant());
        }

        public static bool IsThrottleDevice(string device)
        {
            return ThrottleDevices.Contains((device ?? string.Empty).ToLowerInvariant());
        }

        // The devices of a variant in the order they are shown: the stick side (stick, cyclic, yoke, side-stick, RHC),
        // the throttle side, then pedals and panels.
        public static List<string> DevicesOf(CockpitVariant variant)
        {
            List<string> devices = variant.Controls.Select(c => (c.Device ?? string.Empty).ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
            return devices.OrderBy(d => IsStickDevice(d) ? 0 : IsThrottleDevice(d) ? 1 : d == "pedals" ? 2 : 3)
                .ThenBy(d => Array.IndexOf(StickDevices, d) >= 0 ? Array.IndexOf(StickDevices, d) : Array.IndexOf(ThrottleDevices, d))
                .ThenBy(d => d, StringComparer.Ordinal).ToList();
        }

        // "Stick", "Cyclic", "Side-stick", "Throttle quadrant" ...
        public static string DeviceTitle(string device)
        {
            switch ((device ?? string.Empty).ToLowerInvariant())
            {
                case "stick": return "Stick";
                case "throttle": return "Throttle";
                case "cyclic": return "Cyclic";
                case "collective": return "Collective";
                case "yoke": return "Yoke";
                case "sidestick": return "Side-stick";
                case "quadrant": return "Throttle quadrant";
                case "rhc": return "Rotational hand controller";
                case "thc": return "Translational hand controller";
                case "pedals": return "Pedals";
                case "panel": return "Panels";
                default: return device ?? string.Empty;
            }
        }

        // The drawing a device of the variant is shown on: the variant's own, else the generic one of the device's kind.
        public CockpitDrawing DrawingFor(CockpitVariant variant, string device)
        {
            string id;
            CockpitDrawing drawing;
            if (variant != null && variant.Drawings.TryGetValue(device ?? string.Empty, out id) && Drawings.TryGetValue(id ?? string.Empty, out drawing))
                return drawing;
            if (variant != null && (variant.Aircraft == null || !variant.Aircraft.Fiction))
            {
                // A drawing that names the variant, else one that names its aircraft (or the variant it inherits from).
                List<CockpitDrawing> candidates = Drawings.Values
                    .Where(d => !d.Generic && string.Equals(d.DeviceOrKind, device ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                                !d.Except.Contains(variant.Id, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
                drawing = candidates.FirstOrDefault(d => d.ForVariants.Contains(variant.Id, StringComparer.OrdinalIgnoreCase))
                          ?? candidates.FirstOrDefault(d => variant.Inherits.Length > 0 && d.ForVariants.Contains(variant.Inherits, StringComparer.OrdinalIgnoreCase))
                          ?? candidates.FirstOrDefault(d => variant.Aircraft != null && d.ForAircraft.Contains(variant.Aircraft.Id, StringComparer.OrdinalIgnoreCase));
                if (drawing != null)
                    return drawing;
            }
            return Drawings.TryGetValue(GenericDrawingId(device, variant != null && variant.Aircraft != null && variant.Aircraft.IsSpace), out drawing) ? drawing : null;
        }

        public static string GenericDrawingId(string device, bool space)
        {
            switch ((device ?? string.Empty).ToLowerInvariant())
            {
                case "stick": return space ? "generic-space-stick" : "generic-stick";
                case "sidestick": return space ? "generic-space-stick" : "generic-sidestick";
                case "rhc": return "generic-space-stick";
                case "throttle": return space ? "generic-space-throttle" : "generic-throttle";
                case "thc": return "generic-space-throttle";
                case "cyclic": return "generic-cyclic";
                case "collective": return "generic-collective";
                case "yoke": return "generic-yoke";
                case "quadrant": return "generic-quadrant";
                case "pedals": return "generic-pedals";
                default: return "generic-panel";
            }
        }

        // ------------------------------------------------------------------ checking

        public static readonly string[] ControlTypes =
        {
            "hat4", "hat8", "hat2", "rocker", "button", "trigger", "lever", "switch2", "switch3", "slider", "wheel", "knob",
            "ministick", "axis", "gimbal", "twist", "detent", "guard", "encoder", "selector", "paddle"
        };

        public static readonly string[] Devices =
        {
            "stick", "throttle", "cyclic", "collective", "yoke", "sidestick", "quadrant", "rhc", "thc", "pedals", "panel"
        };

        public static readonly string[] SourceKinds = { "manual", "game-manual", "game-files", "wikipedia", "reference", "tutorial", "film" };

        public static readonly string[] Presses = { "", "short", "long", "press", "hold", "double", "release" };

        public static readonly string[] ModeKinds = { "soi", "master", "mode", "condition", "seat" };

        public static readonly string[] PanelItemTypes =
        {
            "switch2", "switch3", "switch", "knob", "selector", "button", "pushbutton", "lever", "handle", "wheel", "guard", "gauge",
            "display", "light", "indicator", "keypad", "rocker", "hat", "slider", "encoder", "pedal", "other"
        };

        public static readonly string[] PartTypes =
        {
            "hat4", "hat8", "hat2", "rocker", "button", "release", "trigger", "lever", "paddle", "switch2", "switch3", "slider",
            "wheel", "knob", "ministick", "gimbal", "axis", "twist", "detent", "guard", "encoder", "selector", "label"
        };

        // Everything wrong with the database. functionIds: the JoyPro function ids (DB\Functions\slots.json); null = not
        // checked. Each problem starts with the aircraft or drawing it is about.
        public List<string> Check(ICollection<string> functionIds, bool complete = true)
        {
            List<string> problems = new List<string>(Warnings);
            foreach (CockpitDrawing d in Drawings.Values)
            {
                problems.AddRange(CheckDrawing(d).Select(p => "drawing " + d.Id + ": " + p));
                foreach (string v in d.ForVariants.Concat(d.Except).Where(v => !variantsById.ContainsKey(v)))
                    problems.Add("drawing " + d.Id + ": unknown variant " + v);
                foreach (string a in d.ForAircraft)
                {
                    CockpitAircraft found = Aircraft.FirstOrDefault(x => string.Equals(x.Id, a, StringComparison.OrdinalIgnoreCase));
                    if (found == null)
                        problems.Add("drawing " + d.Id + ": unknown aircraft " + a);
                    else if (found.Fiction && !d.Generic)
                        problems.Add("drawing " + d.Id + ": a fictional craft may only use generic drawings (" + a + ")");
                }
                if (d.Device.Length > 0 && !Devices.Contains(d.Device))
                    problems.Add("drawing " + d.Id + ": device '" + d.Device + "' is not known");
            }
            foreach (CockpitAircraft a in Aircraft)
            {
                string at = a.Id + " (" + a.File + ")";
                if (a.Name.Length == 0) problems.Add(at + ": no name");
                if (!Categories.Contains(a.Category)) problems.Add(at + ": category '" + a.Category + "' is not one of " + string.Join(", ", Categories));
                if (!a.Hidden && !Eras.Contains(a.Era)) problems.Add(at + ": era '" + a.Era + "' is not one of " + string.Join(", ", Eras));
                if (a.Variants.Count == 0) problems.Add(at + ": no variant");
                if (a.Fiction && a.Franchise.Length == 0) problems.Add(at + ": a fictional craft names its franchise");
                foreach (CockpitPhoto p in a.Photos)
                {
                    if (!IsWebAddress(p.Url) && !IsWebAddress(p.Page)) problems.Add(at + ": photo without an address");
                    if (p.Url.Length > 0 && !IsWebAddress(p.Url)) problems.Add(at + ": photo address is not https: " + p.Url);
                    if (p.Page.Length > 0 && !IsWebAddress(p.Page)) problems.Add(at + ": photo page is not https: " + p.Page);
                    if (p.Variant.Length > 0 && !a.Variants.Any(v => string.Equals(v.Id, p.Variant, StringComparison.OrdinalIgnoreCase)))
                        problems.Add(at + ": photo of unknown variant " + p.Variant);
                }
                foreach (CockpitVariant raw in a.Variants)
                {
                    string vat = a.Id + "/" + raw.Id;
                    if (raw.Inherits.Length > 0 && !variantsById.ContainsKey(raw.Inherits))
                        problems.Add(vat + ": inherits unknown variant " + raw.Inherits);
                    if (raw.Inherits.Length > 0 && InheritsCycle(raw))
                        problems.Add(vat + ": inheritance cycle");
                    if (raw.Name.Length == 0) problems.Add(vat + ": no name");
                    CockpitVariant v = Resolve(raw);
                    problems.AddRange(CheckVariant(v, a, functionIds, complete).Select(p => vat + ": " + p));
                }
            }
            return problems;
        }

        private bool InheritsCycle(CockpitVariant v)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CockpitVariant at = v;
            while (at != null && at.Inherits.Length > 0)
            {
                if (!seen.Add(at.Id))
                    return true;
                variantsById.TryGetValue(at.Inherits, out at);
            }
            return false;
        }

        private List<string> CheckVariant(CockpitVariant v, CockpitAircraft a, ICollection<string> functionIds, bool complete)
        {
            List<string> problems = new List<string>();
            if (!v.Controls.Any(c => IsStickDevice(c.Device) && c.Functions.Count > 0))
                problems.Add("no stick-side control (stick, cyclic, yoke, side-stick or RHC)");
            if (!v.Controls.Any(c => IsThrottleDevice(c.Device) && c.Functions.Count > 0))
                problems.Add("no throttle-side control (throttle, collective, quadrant or THC)");
            if (v.Sources.Count == 0 && !a.Hidden)
                problems.Add("no source");
            if (!new[] { "high", "medium", "low" }.Contains(v.Confidence) && !a.Hidden)
                problems.Add("confidence '" + v.Confidence + "' is not high, medium or low");
            foreach (CockpitSource s in v.Sources)
            {
                if (s.Title.Length == 0) problems.Add("a source without a title");
                if (s.Url.Length > 0 && !IsWebAddress(s.Url)) problems.Add("source address is not https: " + s.Url);
                if (!SourceKinds.Contains(s.Kind)) problems.Add("source kind '" + s.Kind + "' is not one of " + string.Join(", ", SourceKinds));
            }
            HashSet<string> modes = new HashSet<string>(v.Modes.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitControl c in v.Controls)
            {
                string cat = "control " + c.Id;
                if (c.Id.Length == 0) problems.Add("a control without id (" + c.Name + ")");
                else if (!ids.Add(c.Id)) problems.Add(cat + " twice");
                if (!Devices.Contains(c.Device)) problems.Add(cat + ": device '" + c.Device + "' is not one of " + string.Join(", ", Devices));
                if (!ControlTypes.Contains(c.Type)) problems.Add(cat + ": type '" + c.Type + "' is not one of " + string.Join(", ", ControlTypes));
                if (c.Name.Length == 0) problems.Add(cat + ": no name");
                if (c.Where.Length == 0) problems.Add(cat + ": no 'where' (position or role)");
                if (c.Functions.Count == 0) problems.Add(cat + ": no function");
                HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CockpitFunction f in c.Functions)
                {
                    string row = f.Dir + (f.Press.Length > 0 ? " " + f.Press : string.Empty) + (f.When.Length > 0 ? " (" + f.When + ")" : string.Empty);
                    if (!dirs.Add(f.RowKey)) problems.Add(cat + ": row '" + row + "' twice");
                    if (!IsDirection(f.Dir)) problems.Add(cat + ": direction '" + f.Dir + "' is not known");
                    if (!Presses.Contains(f.Press)) problems.Add(cat + ": press '" + f.Press + "' is not one of " + string.Join(", ", Presses.Where(p => p.Length > 0)));
                    if (f.Text.Length == 0 && !f.By.Values.Any(t => t.Length > 0)) problems.Add(cat + ": a function without text");
                    foreach (string fn in new[] { f.Fn }.Concat(f.FnBy.Values).Where(x => x.Length > 0))
                    {
                        if (functionIds != null && !functionIds.Contains(fn)) problems.Add(cat + ": function id '" + fn + "' does not exist");
                    }
                    foreach (string mode in f.By.Keys.Concat(f.FnBy.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!modes.Contains(mode)) problems.Add(cat + " " + row + ": mode '" + mode + "' is not one of the variant's modes");
                    }
                    // Every row says what it does in every declared mode: a text for all, or a cell per mode.
                    if (modes.Count > 0 && (f.Text.Length == 0 || f.TextFromMode))
                    {
                        List<string> missing = modes.Where(m => !f.By.ContainsKey(m)).ToList();
                        if (missing.Count > 0) problems.Add(cat + " " + row + ": no function for mode(s) " + string.Join(", ", missing));
                    }
                    if (f.Src.Length > 0 && v.Source(f.Src) == null) problems.Add(cat + " " + row + ": unknown source '" + f.Src + "'");
                }
                if (c.Src.Length > 0 && v.Source(c.Src) == null) problems.Add(cat + ": unknown source '" + c.Src + "'");
            }
            foreach (IGrouping<string, CockpitControl> device in v.Controls.Where(c => c.N > 0).GroupBy(c => c.Device, StringComparer.OrdinalIgnoreCase))
            {
                foreach (IGrouping<int, CockpitControl> twice in device.GroupBy(c => c.N).Where(g => g.Count() > 1))
                    problems.Add(device.Key + ": callout number " + twice.Key + " twice");
            }
            problems.AddRange(CheckManual(v, a, complete));
            foreach (KeyValuePair<string, string> d in v.Drawings)
            {
                CockpitDrawing drawing;
                if (!Drawings.TryGetValue(d.Value ?? string.Empty, out drawing))
                {
                    problems.Add("drawing '" + d.Value + "' for " + d.Key + " does not exist");
                    continue;
                }
                if (a.Fiction && !drawing.Generic)
                    problems.Add("a fictional craft may only use generic drawings (" + d.Value + ")");
                foreach (CockpitControl c in v.Controls.Where(x => string.Equals(x.Device, d.Key, StringComparison.OrdinalIgnoreCase) && x.Part.Length > 0))
                {
                    if (drawing.PartOf(c.Part) == null)
                        problems.Add("control " + c.Id + ": part '" + c.Part + "' is not on drawing " + drawing.Id);
                }
            }
            if (v.SeatOf.Length > 0 && !variantsById.ContainsKey(v.SeatOf))
                problems.Add("seat of unknown variant " + v.SeatOf);
            return problems;
        }

        // The manual-like parts of a variant: the HOTAS table's modes, the overview's numbered areas, the numbered panels
        // and what every item cites. complete: also what every listed variant must have (an overview, numbered panels).
        private List<string> CheckManual(CockpitVariant v, CockpitAircraft a, bool complete)
        {
            List<string> problems = new List<string>();
            bool must = complete && !a.Hidden;
            HashSet<string> modeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitMode m in v.Modes)
            {
                if (m.Id.Length == 0) problems.Add("a mode without id (" + m.Name + ")");
                else if (!modeIds.Add(m.Id)) problems.Add("mode " + m.Id + " twice");
                if (!ModeKinds.Contains(m.Kind)) problems.Add("mode " + m.Id + ": kind '" + m.Kind + "' is not one of " + string.Join(", ", ModeKinds));
            }
            if (v.DefaultMode.Length > 0 && !modeIds.Contains(v.DefaultMode))
                problems.Add("default mode '" + v.DefaultMode + "' is not one of the modes");
            foreach (CockpitSource s in v.Sources.Where(s => s.Id.Length > 0).GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.First()))
                problems.Add("source id " + s.Id + " twice");
            HashSet<string> panelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitPanel p in v.Panels)
            {
                string at = "panel " + (p.Key.Length > 0 ? p.Key : "?");
                if (p.Name.Length == 0) problems.Add("a panel without a name");
                if (!panelIds.Add(p.Key)) problems.Add(at + " twice");
                if (p.Src.Length > 0 && v.Source(p.Src) == null) problems.Add(at + ": unknown source '" + p.Src + "'");
                if (p.Confidence.Length > 0 && !new[] { "high", "medium", "low" }.Contains(p.Confidence)) problems.Add(at + ": confidence '" + p.Confidence + "' is not high, medium or low");
                if (must && p.Items.Count == 0) problems.Add(at + ": no numbered items");
                List<int> numbers = p.Items.Select(i => i.N).OrderBy(n => n).ToList();
                if (numbers.Count > 0 && !numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
                    problems.Add(at + ": items are not numbered 1.." + numbers.Count + " (" + string.Join(",", numbers) + ")");
                foreach (CockpitPanelItem i in p.Items)
                {
                    if (i.Name.Length == 0) problems.Add(at + " item " + i.N + ": no name");
                    if (i.Type.Length > 0 && !PanelItemTypes.Contains(i.Type)) problems.Add(at + " item " + i.N + ": type '" + i.Type + "' is not one of " + string.Join(", ", PanelItemTypes));
                    if (i.Src.Length > 0 && v.Source(i.Src) == null) problems.Add(at + " item " + i.N + ": unknown source '" + i.Src + "'");
                    if ((i.X >= 0 || i.Y >= 0) && (i.X < 0 || i.X > 1 || i.Y < 0 || i.Y > 1)) problems.Add(at + " item " + i.N + ": position outside 0..1");
                }
            }
            if (v.Overview == null)
            {
                if (must) problems.Add("no overview");
            }
            else
            {
                if (must && v.Overview.Areas.Count == 0) problems.Add("overview: no areas");
                List<int> numbers = v.Overview.Areas.Select(x => x.N).OrderBy(n => n).ToList();
                if (numbers.Count > 0 && !numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
                    problems.Add("overview: areas are not numbered 1.." + numbers.Count);
                foreach (CockpitOverviewArea x in v.Overview.Areas)
                {
                    if (x.Name.Length == 0) problems.Add("overview area " + x.N + ": no name");
                    foreach (string p in x.Panels.Where(p => !panelIds.Contains(p)))
                        problems.Add("overview area " + x.N + ": unknown panel '" + p + "'");
                    if (x.Src.Length > 0 && v.Source(x.Src) == null) problems.Add("overview area " + x.N + ": unknown source '" + x.Src + "'");
                    if ((x.X >= 0 || x.Y >= 0) && (x.X < 0 || x.X > 1 || x.Y < 0 || x.Y > 1)) problems.Add("overview area " + x.N + ": position outside 0..1");
                }
                if (a.Fiction && v.Overview.Drawing.Length > 0) problems.Add("overview: a fictional craft has no drawing of its design");
            }
            if (a.Fiction && v.Panels.Any(p => p.Drawing.Length > 0)) problems.Add("panels: a fictional craft has no drawing of its design");
            if (must && v.Panels.Count == 0) problems.Add("no panels");
            return problems;
        }

        private static List<string> CheckDrawing(CockpitDrawing d)
        {
            List<string> problems = new List<string>();
            if (d.Width <= 0 || d.Height <= 0) problems.Add("no size");
            if (!Devices.Contains(d.Kind)) problems.Add("kind '" + d.Kind + "' is not one of " + string.Join(", ", Devices));
            if (d.Origin.Length == 0) problems.Add("no origin (how it was drawn)");
            if (d.Shapes.Count == 0) problems.Add("no shapes");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitDrawingPart p in d.Parts)
            {
                if (!ids.Add(p.Id)) problems.Add("part " + p.Id + " twice");
                if (!PartTypes.Contains(p.Type)) problems.Add("part " + p.Id + ": type '" + p.Type + "' is not known");
                if (p.X - p.R < 0 || p.Y - p.R < 0 || p.X + p.R > d.Width || p.Y + p.R > d.Height) problems.Add("part " + p.Id + " reaches out of the drawing");
            }
            foreach (SchematicShape s in d.Shapes)
            {
                if (s.Kind == "path" && !ValidPath(s.Path)) problems.Add("a path with commands other than absolute M L Q C Z: " + Shorten(s.Path));
            }
            return problems;
        }

        private static string Shorten(string text)
        {
            return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
        }

        // Path data JoyPro's painters read: absolute M, L, Q, C, Z with "x,y" pairs.
        public static bool ValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            foreach (char ch in path)
            {
                if (char.IsLetter(ch) && "MLQCZ".IndexOf(ch) < 0)
                    return false;
            }
            return path.TrimStart().StartsWith("M", StringComparison.Ordinal);
        }

        public static bool IsWebAddress(string url)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) && uri.Scheme == "https";
        }

        public static readonly string[] Directions =
        {
            "", "up", "down", "left", "right", "press", "fwd", "aft", "center", "in", "out", "cw", "ccw", "stage1", "stage2",
            "x", "y", "pitch", "roll", "yaw", "axis", "long", "double", "hold", "upleft", "upright", "downleft", "downright",
            "pull", "push", "on", "off"
        };

        public static bool IsDirection(string dir)
        {
            string d = (dir ?? string.Empty).ToLowerInvariant();
            if (Directions.Contains(d))
                return true;
            int n;
            return d.StartsWith("pos", StringComparison.Ordinal) && int.TryParse(d.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
        }

        public static bool IsAxisDirection(string dir)
        {
            switch ((dir ?? string.Empty).ToLowerInvariant())
            {
                case "x": case "y": case "pitch": case "roll": case "yaw": case "axis":
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ parsing

        public static CockpitAircraft ParseAircraft(Dictionary<string, object> item)
        {
            CockpitAircraft a = new CockpitAircraft
            {
                Id = Text(item, "id"),
                Name = Text(item, "name"),
                Aliases = Strings(item, "aliases").ToList(),
                Category = Text(item, "category").ToLowerInvariant(),
                Fiction = Bool(item, "fiction"),
                Franchise = Text(item, "franchise"),
                Maker = Text(item, "maker"),
                Nations = Strings(item, "nations").Concat(Strings(item, "nation")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Role = Text(item, "role"),
                Era = Text(item, "era").ToLowerInvariant(),
                Summary = Text(item, "summary"),
                Hidden = Bool(item, "hidden")
            };
            foreach (Dictionary<string, object> p in Objects(item, "photos"))
            {
                a.Photos.Add(new CockpitPhoto
                {
                    Url = Text(p, "url"), Page = Text(p, "page"), View = Text(p, "view"), Credit = Text(p, "credit"),
                    License = Text(p, "license"), Variant = Text(p, "variant"), Caption = Text(p, "caption"), Confidence = Text(p, "confidence")
                });
            }
            foreach (Dictionary<string, object> l in Objects(item, "links"))
                a.Links.Add(new CockpitLink { Title = Text(l, "title"), Url = Text(l, "url") });
            foreach (Dictionary<string, object> v in Objects(item, "variants"))
                a.Variants.Add(ParseVariant(v));
            return a;
        }

        public static CockpitVariant ParseVariant(Dictionary<string, object> item)
        {
            CockpitVariant v = new CockpitVariant
            {
                Id = Text(item, "id"),
                Name = Text(item, "name"),
                Aliases = Strings(item, "aliases").ToList(),
                Years = Text(item, "years"),
                Inherits = Text(item, "inherits"),
                RemoveControls = Strings(item, "removeControls").ToList(),
                Seat = Text(item, "seat"),
                Cockpit = Text(item, "cockpit"),
                Notes = Text(item, "notes"),
                Confidence = Text(item, "confidence").ToLowerInvariant()
            };
            foreach (Dictionary<string, object> g in Objects(item, "games"))
                v.Games.Add(new CockpitGameRef { Game = Text(g, "game"), Plane = Text(g, "plane"), Note = Text(g, "note") });
            foreach (Dictionary<string, object> c in Objects(item, "controls"))
                v.Controls.Add(ParseControl(c));
            Dictionary<string, object> drawings = Object(item, "drawings");
            if (drawings != null)
            {
                foreach (KeyValuePair<string, object> d in drawings)
                {
                    string id = d.Value as string;
                    if (!string.IsNullOrWhiteSpace(id))
                        v.Drawings[d.Key.ToLowerInvariant()] = id.Trim();
                }
            }
            v.SeatOf = Text(item, "seatOf");
            v.DefaultMode = Text(item, "defaultMode");
            foreach (Dictionary<string, object> m in Objects(item, "modes"))
                v.Modes.Add(new CockpitMode { Id = Text(m, "id"), Name = Text(m, "name").Length > 0 ? Text(m, "name") : Text(m, "id"), Kind = Text(m, "kind").ToLowerInvariant(), Note = Text(m, "note") });
            Dictionary<string, object> overview = Object(item, "overview");
            if (overview != null)
            {
                v.Overview = new CockpitOverview { Drawing = Text(overview, "drawing"), Text = Text(overview, "text") };
                foreach (Dictionary<string, object> a in Objects(overview, "areas"))
                {
                    v.Overview.Areas.Add(new CockpitOverviewArea
                    {
                        N = (int)Number(a, "n", 0), Name = Text(a, "name"), Area = Text(a, "area").ToLowerInvariant(),
                        X = Number(a, "x", -1), Y = Number(a, "y", -1), Text = Text(a, "text"), Panels = Strings(a, "panels").ToList(),
                        Src = Text(a, "src"), Page = Text(a, "page")
                    });
                }
            }
            foreach (Dictionary<string, object> p in Objects(item, "panels"))
            {
                CockpitPanel panel = new CockpitPanel
                {
                    Id = Text(p, "id"), Name = Text(p, "name"), Area = Text(p, "area").ToLowerInvariant(), Text = Text(p, "text"),
                    Drawing = Text(p, "drawing"), Src = Text(p, "src"), Page = Text(p, "page"), Confidence = Text(p, "confidence").ToLowerInvariant()
                };
                foreach (Dictionary<string, object> i in Objects(p, "items"))
                {
                    panel.Items.Add(new CockpitPanelItem
                    {
                        N = (int)Number(i, "n", 0), Name = Text(i, "name"), Type = Text(i, "type").ToLowerInvariant(),
                        Positions = Strings(i, "positions").ToList(), Text = Text(i, "text"), X = Number(i, "x", -1), Y = Number(i, "y", -1),
                        Fn = Text(i, "fn"), Src = Text(i, "src"), Page = Text(i, "page")
                    });
                }
                v.Panels.Add(panel);
            }
            foreach (Dictionary<string, object> s in Objects(item, "sources"))
            {
                v.Sources.Add(new CockpitSource
                {
                    Id = Text(s, "id"), Title = Text(s, "title"), Url = Text(s, "url"), File = Text(s, "file"),
                    Kind = Text(s, "kind").ToLowerInvariant(), Covers = Text(s, "covers")
                });
            }
            return v;
        }

        private static Dictionary<string, string> TextMap(Dictionary<string, object> item, string key)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> obj = Object(item, key);
            if (obj != null)
            {
                foreach (KeyValuePair<string, object> pair in obj)
                    map[pair.Key] = pair.Value == null || pair.Value is IDictionary || pair.Value is IList ? string.Empty : Convert.ToString(pair.Value, CultureInfo.InvariantCulture).Trim();
            }
            return map;
        }

        public static CockpitControl ParseControl(Dictionary<string, object> item)
        {
            CockpitControl c = new CockpitControl
            {
                Id = Text(item, "id"),
                Device = Text(item, "device").ToLowerInvariant(),
                Name = Text(item, "name"),
                Type = Text(item, "type").ToLowerInvariant(),
                Where = Text(item, "where"),
                Part = Text(item, "part"),
                Note = Text(item, "note"),
                N = (int)Number(item, "n", 0),
                Src = Text(item, "src"),
                Page = Text(item, "page")
            };
            foreach (Dictionary<string, object> f in Objects(item, "functions"))
            {
                c.Functions.Add(new CockpitFunction
                {
                    Dir = Text(f, "dir").ToLowerInvariant(), Press = Text(f, "press").ToLowerInvariant(), When = Text(f, "when"),
                    Text = Text(f, "text"), Fn = Text(f, "fn"), Note = Text(f, "note"), By = TextMap(f, "by"), FnBy = TextMap(f, "fnBy"),
                    Src = Text(f, "src"), Page = Text(f, "page")
                });
            }
            return c;
        }

        public static CockpitDrawing ParseDrawing(Dictionary<string, object> item)
        {
            if (item == null)
                throw new InvalidDataException("not a JSON object");
            string format = Text(item, "format");
            if (format.Length > 0 && !string.Equals(format, DrawingFormatName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("format is '" + format + "', not " + DrawingFormatName);
            CockpitDrawing d = new CockpitDrawing
            {
                Id = Text(item, "id"),
                Kind = Text(item, "kind").ToLowerInvariant(),
                Name = Text(item, "name"),
                View = Text(item, "view"),
                Origin = Text(item, "origin"),
                Generic = Bool(item, "generic"),
                Width = Number(item, "width", 1000),
                Height = Number(item, "height", 1000),
                ForVariants = Strings(item, "variants").ToList(),
                ForAircraft = Strings(item, "aircraft").ToList(),
                Except = Strings(item, "except").ToList(),
                Device = Text(item, "device").ToLowerInvariant()
            };
            foreach (Dictionary<string, object> s in Objects(item, "shapes"))
            {
                d.Shapes.Add(new SchematicShape
                {
                    Kind = Text(s, "kind").Length > 0 ? Text(s, "kind").ToLowerInvariant() : (Text(s, "path").Length > 0 ? "path" : "rect"),
                    Role = Text(s, "role").Length > 0 ? Text(s, "role") : "body",
                    Path = Text(s, "path"),
                    X = Number(s, "x", 0), Y = Number(s, "y", 0), W = Number(s, "w", 0), H = Number(s, "h", 0), R = Number(s, "r", 0),
                    Text = Text(s, "text"),
                    FontSize = Number(s, "size", 20)
                });
            }
            foreach (Dictionary<string, object> p in Objects(item, "parts"))
            {
                d.Parts.Add(new CockpitDrawingPart
                {
                    Id = Text(p, "id"), Type = Text(p, "type").Length > 0 ? Text(p, "type").ToLowerInvariant() : "button",
                    X = Number(p, "x", 0), Y = Number(p, "y", 0), R = Number(p, "r", 24), Angle = Number(p, "angle", 0), Label = Text(p, "label")
                });
            }
            return d;
        }

        // ------------------------------------------------------------------ JSON helpers

        internal static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null || value is IDictionary || value is IList)
                return string.Empty;
            return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        internal static bool Bool(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        internal static double Number(Dictionary<string, object> item, string key, double fallback)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return fallback;
            if (value is double)
                return (double)value;
            double d;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
        }

        internal static Dictionary<string, object> Object(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static IEnumerable<Dictionary<string, object>> Objects(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value))
                return Enumerable.Empty<Dictionary<string, object>>();
            List<object> list = value as List<object>;
            return list == null ? Enumerable.Empty<Dictionary<string, object>>() : list.OfType<Dictionary<string, object>>();
        }

        internal static IEnumerable<string> Strings(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null)
                return Enumerable.Empty<string>();
            string single = value as string;
            if (single != null)
                return single.Trim().Length > 0 ? new[] { single.Trim() } : Enumerable.Empty<string>();
            List<object> list = value as List<object>;
            return list == null
                ? Enumerable.Empty<string>()
                : list.Where(o => o != null && !(o is IDictionary)).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture).Trim()).Where(s => s.Length > 0);
        }
    }
}
