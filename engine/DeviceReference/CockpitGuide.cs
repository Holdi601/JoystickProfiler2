#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // The cockpit guide (docs/cockpits.md "The cockpit guide"): every panel and control of a cockpit explained in plain
    // words, as an area > panel > control tree. DB\Cockpits\guide\<id>.json (format joypro-cockpit-guide, shipped data
    // written offline from the games' own control lists and manuals), its part files (<id>.partN.json, "partOf") and
    // translations (<id>.<lang>.json, format joypro-cockpit-guide-text). Each control names the game controls it is
    // ("match": callout ids, cmd:, label:, item:, control:), so the guide ties the game's cockpit pictures, JoyPro's
    // catalog panels and the HOTAS controls together. Shared by the engine (C# 7.3) and the GUI (linked file);
    // tools/cockpit_guide.py has the same rules.

    public sealed class CockpitGuidePosition
    {
        public string Name = string.Empty;
        public string Text = string.Empty;
    }

    public sealed class CockpitGuideControl
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Type = string.Empty;
        public List<string> Match = new List<string>();
        public List<CockpitGuidePosition> Positions = new List<CockpitGuidePosition>();
        public string Text = string.Empty;
        public string Context = string.Empty;
        public int Count;
        public string Fn = string.Empty;
        public string Src = string.Empty;
        public string Page = string.Empty;

        public CockpitGuideControl Clone()
        {
            CockpitGuideControl c = (CockpitGuideControl)MemberwiseClone();
            c.Match = new List<string>(Match);
            c.Positions = Positions.Select(p => new CockpitGuidePosition { Name = p.Name, Text = p.Text }).ToList();
            return c;
        }
    }

    public sealed class CockpitGuidePanel
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Text = string.Empty;
        public string Context = string.Empty;
        // The catalog panel (CockpitPanel.Key) this panel is.
        public string Db = string.Empty;
        public string Src = string.Empty;
        public string Page = string.Empty;
        public List<CockpitGuideControl> Controls = new List<CockpitGuideControl>();

        public CockpitGuidePanel Clone()
        {
            CockpitGuidePanel p = (CockpitGuidePanel)MemberwiseClone();
            p.Controls = Controls.Select(c => c.Clone()).ToList();
            return p;
        }
    }

    public sealed class CockpitGuideArea
    {
        public string Id = string.Empty;
        // glareshield, front-left ... (CockpitGuide.Where); hotas for the grips.
        public string Where = string.Empty;
        public string Name = string.Empty;
        public string Text = string.Empty;
        // 0: every seat; 1, 2 ...: the seat of the picture set (CockpitPictureSet.SeatOf).
        public int Seat;
        public List<CockpitGuidePanel> Panels = new List<CockpitGuidePanel>();

        public CockpitGuideArea Clone()
        {
            CockpitGuideArea a = (CockpitGuideArea)MemberwiseClone();
            a.Panels = Panels.Select(p => p.Clone()).ToList();
            return a;
        }
    }

    public sealed class CockpitGuide
    {
        public const string FormatName = "joypro-cockpit-guide";
        public const string TextFormatName = "joypro-cockpit-guide-text";
        public const string FolderName = "guide";

        public static readonly string[] Where =
        {
            "glareshield", "front-left", "front-center", "front-right", "left-console", "right-console", "center-pedestal",
            "overhead", "left-side", "right-side", "floor", "rear", "seat", "helmet", "hotas", "other"
        };

        public static readonly string[] Types =
        {
            "switch2", "switch3", "switch", "knob", "selector", "button", "lever", "handle", "wheel", "guard", "gauge", "display",
            "light", "indicator", "keypad", "rocker", "hat", "slider", "encoder", "pedal", "trigger", "axis", "screen", "cover", "other"
        };

        public string Id = string.Empty;
        public string Title = string.Empty;
        public string PartOf = string.Empty;
        public string Inherits = string.Empty;
        public List<string> Remove = new List<string>();
        public List<string> Variants = new List<string>();
        public List<string> Games = new List<string>();
        public List<string> Sets = new List<string>();
        public bool Common;
        public string Intro = string.Empty;
        public string Language = "en";
        public List<CockpitSource> Sources = new List<CockpitSource>();
        public List<CockpitGuideArea> Areas = new List<CockpitGuideArea>();
        public string File = string.Empty;

        public CockpitGuide Clone()
        {
            CockpitGuide g = (CockpitGuide)MemberwiseClone();
            g.Remove = new List<string>(Remove);
            g.Variants = new List<string>(Variants);
            g.Games = new List<string>(Games);
            g.Sets = new List<string>(Sets);
            g.Sources = Sources.Select(s => s.Clone()).ToList();
            g.Areas = Areas.Select(a => a.Clone()).ToList();
            return g;
        }

        public IEnumerable<Tuple<CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl>> AllControls()
        {
            foreach (CockpitGuideArea a in Areas)
                foreach (CockpitGuidePanel p in a.Panels)
                    foreach (CockpitGuideControl c in p.Controls)
                        yield return Tuple.Create(a, p, c);
        }

        public CockpitSource Source(string id)
        {
            return string.IsNullOrEmpty(id) ? null : Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // "DCS F-16C Early Access Guide (EN), p. 64".
        public string Cite(string src, string page)
        {
            CockpitSource s = Source(src);
            string title = s == null ? string.Empty : s.Title;
            if (!string.IsNullOrEmpty(page))
                title = (title.Length > 0 ? title + ", " : string.Empty) + "p. " + page;
            return title;
        }

        // Adds areas: an area with the same id gets the panels (a panel with the same id gets the controls, a control with
        // the same id is replaced).
        public static void MergeAreas(List<CockpitGuideArea> target, IEnumerable<CockpitGuideArea> extra)
        {
            foreach (CockpitGuideArea a in extra)
            {
                CockpitGuideArea mine = target.FirstOrDefault(x => x.Id == a.Id);
                if (mine == null)
                {
                    target.Add(a.Clone());
                    continue;
                }
                if (a.Name.Length > 0) mine.Name = a.Name;
                if (a.Text.Length > 0) mine.Text = a.Text;
                if (a.Where.Length > 0) mine.Where = a.Where;
                if (a.Seat > 0) mine.Seat = a.Seat;
                foreach (CockpitGuidePanel p in a.Panels)
                {
                    CockpitGuidePanel pm = mine.Panels.FirstOrDefault(x => x.Id == p.Id);
                    if (pm == null)
                    {
                        mine.Panels.Add(p.Clone());
                        continue;
                    }
                    HashSet<string> ids = new HashSet<string>(p.Controls.Select(c => c.Id));
                    pm.Controls = pm.Controls.Where(c => !ids.Contains(c.Id)).Concat(p.Controls.Select(c => c.Clone())).ToList();
                    if (p.Name.Length > 0) pm.Name = p.Name;
                    if (p.Text.Length > 0) pm.Text = p.Text;
                    if (p.Context.Length > 0) pm.Context = p.Context;
                    if (p.Db.Length > 0) pm.Db = p.Db;
                    if (p.Src.Length > 0) pm.Src = p.Src;
                    if (p.Page.Length > 0) pm.Page = p.Page;
                }
            }
        }

        // ------------------------------------------------------------------ parsing

        public static CockpitGuide Parse(Dictionary<string, object> root, string file)
        {
            CockpitGuide g = new CockpitGuide
            {
                Id = DeviceCatalog.Text(root, "id"),
                Title = DeviceCatalog.Text(root, "title"),
                PartOf = DeviceCatalog.Text(root, "partOf"),
                Inherits = DeviceCatalog.Text(root, "inherits"),
                Remove = CockpitCatalog.Strings(root, "remove").ToList(),
                Variants = CockpitCatalog.Strings(root, "variants").ToList(),
                Games = CockpitCatalog.Strings(root, "games").ToList(),
                Sets = CockpitCatalog.Strings(root, "sets").ToList(),
                Common = Bool(root, "common"),
                Intro = DeviceCatalog.Text(root, "intro"),
                File = file ?? string.Empty
            };
            foreach (Dictionary<string, object> s in DeviceCatalog.Objects(root, "sources"))
            {
                g.Sources.Add(new CockpitSource
                {
                    Id = DeviceCatalog.Text(s, "id"), Title = DeviceCatalog.Text(s, "title"), Url = DeviceCatalog.Text(s, "url"),
                    File = DeviceCatalog.Text(s, "file"), Kind = DeviceCatalog.Text(s, "kind")
                });
            }
            foreach (Dictionary<string, object> a in DeviceCatalog.Objects(root, "areas"))
            {
                CockpitGuideArea area = new CockpitGuideArea
                {
                    Id = DeviceCatalog.Text(a, "id"), Where = DeviceCatalog.Text(a, "where"), Name = DeviceCatalog.Text(a, "name"),
                    Text = DeviceCatalog.Text(a, "text"), Seat = (int)Number(a, "seat")
                };
                foreach (Dictionary<string, object> p in DeviceCatalog.Objects(a, "panels"))
                {
                    CockpitGuidePanel panel = new CockpitGuidePanel
                    {
                        Id = DeviceCatalog.Text(p, "id"), Name = DeviceCatalog.Text(p, "name"), Text = DeviceCatalog.Text(p, "text"),
                        Context = DeviceCatalog.Text(p, "context"), Db = DeviceCatalog.Text(p, "db"), Src = DeviceCatalog.Text(p, "src"),
                        Page = DeviceCatalog.Text(p, "page")
                    };
                    foreach (Dictionary<string, object> c in DeviceCatalog.Objects(p, "controls"))
                    {
                        CockpitGuideControl control = new CockpitGuideControl
                        {
                            Id = DeviceCatalog.Text(c, "id"), Name = DeviceCatalog.Text(c, "name"), Type = DeviceCatalog.Text(c, "type"),
                            Match = CockpitCatalog.Strings(c, "match").ToList(), Text = DeviceCatalog.Text(c, "text"),
                            Context = DeviceCatalog.Text(c, "context"), Count = (int)Number(c, "count"), Fn = DeviceCatalog.Text(c, "fn"),
                            Src = DeviceCatalog.Text(c, "src"), Page = DeviceCatalog.Text(c, "page")
                        };
                        foreach (Dictionary<string, object> pos in DeviceCatalog.Objects(c, "positions"))
                            control.Positions.Add(new CockpitGuidePosition { Name = DeviceCatalog.Text(pos, "name"), Text = DeviceCatalog.Text(pos, "text") });
                        panel.Controls.Add(control);
                    }
                    area.Panels.Add(panel);
                }
                g.Areas.Add(area);
            }
            return g;
        }

        private static bool Bool(Dictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static double Number(Dictionary<string, object> item, string key)
        {
            object value;
            if (item == null || !item.TryGetValue(key, out value) || value == null || value is string)
                return 0;
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    // A translation of a guide (<id>.<lang>.json): names and texts by area id, panel id and "panel/control".
    public sealed class CockpitGuideText
    {
        public sealed class Entry
        {
            public string Name = string.Empty;
            public string Text = string.Empty;
            public string Context = string.Empty;
            public Dictionary<string, string> Positions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Guide = string.Empty;
        public string Language = string.Empty;
        public string Title = string.Empty;
        public string Intro = string.Empty;
        public Dictionary<string, Entry> Areas = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public Dictionary<string, Entry> Panels = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public Dictionary<string, Entry> Controls = new Dictionary<string, Entry>(StringComparer.Ordinal);
        public string File = string.Empty;

        public static CockpitGuideText Parse(Dictionary<string, object> root, string file)
        {
            CockpitGuideText t = new CockpitGuideText
            {
                Guide = DeviceCatalog.Text(root, "guide"),
                Language = DeviceCatalog.Text(root, "language"),
                Title = DeviceCatalog.Text(root, "title"),
                Intro = DeviceCatalog.Text(root, "intro"),
                File = file ?? string.Empty
            };
            Read(DeviceCatalog.Object(root, "areas"), t.Areas);
            Read(DeviceCatalog.Object(root, "panels"), t.Panels);
            Read(DeviceCatalog.Object(root, "controls"), t.Controls);
            return t;
        }

        private static void Read(Dictionary<string, object> map, Dictionary<string, Entry> into)
        {
            if (map == null)
                return;
            foreach (KeyValuePair<string, object> pair in map)
            {
                Dictionary<string, object> e = pair.Value as Dictionary<string, object>;
                if (e == null)
                    continue;
                Entry entry = new Entry { Name = DeviceCatalog.Text(e, "name"), Text = DeviceCatalog.Text(e, "text"), Context = DeviceCatalog.Text(e, "context") };
                Dictionary<string, object> pos = DeviceCatalog.Object(e, "positions");
                if (pos != null)
                {
                    foreach (KeyValuePair<string, object> p in pos)
                    {
                        string s = p.Value as string;
                        if (!string.IsNullOrWhiteSpace(s))
                            entry.Positions[p.Key] = s.Trim();
                    }
                }
                into[pair.Key] = entry;
            }
        }

        // The guide in this language: every name and text the translation has replaces the English one.
        public void ApplyTo(CockpitGuide g)
        {
            if (Title.Length > 0) g.Title = Title;
            if (Intro.Length > 0) g.Intro = Intro;
            g.Language = Language;
            foreach (CockpitGuideArea a in g.Areas)
            {
                Entry e;
                if (Areas.TryGetValue(a.Id, out e))
                {
                    if (e.Name.Length > 0) a.Name = e.Name;
                    if (e.Text.Length > 0) a.Text = e.Text;
                }
                foreach (CockpitGuidePanel p in a.Panels)
                {
                    if (Panels.TryGetValue(p.Id, out e))
                    {
                        if (e.Name.Length > 0) p.Name = e.Name;
                        if (e.Text.Length > 0) p.Text = e.Text;
                        if (e.Context.Length > 0) p.Context = e.Context;
                    }
                    foreach (CockpitGuideControl c in p.Controls)
                    {
                        if (!Controls.TryGetValue(p.Id + "/" + c.Id, out e))
                            continue;
                        if (e.Name.Length > 0) c.Name = e.Name;
                        if (e.Text.Length > 0) c.Text = e.Text;
                        if (e.Context.Length > 0) c.Context = e.Context;
                        foreach (CockpitGuidePosition pos in c.Positions)
                        {
                            string text;
                            if (e.Positions.TryGetValue(pos.Name, out text))
                                pos.Text = text;
                        }
                    }
                }
            }
        }
    }

    // The guides of DB\Cockpits\guide, read once per folder (again when a file changed).
    public sealed class CockpitGuideLibrary
    {
        public string Folder = string.Empty;
        public Dictionary<string, CockpitGuide> Guides = new Dictionary<string, CockpitGuide>(StringComparer.OrdinalIgnoreCase);
        // guide id -> language -> translation.
        public Dictionary<string, Dictionary<string, CockpitGuideText>> Texts = new Dictionary<string, Dictionary<string, CockpitGuideText>>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
        // _templates.json and its translations (_templates.<lang>.json) by language.
        public Dictionary<string, CockpitGuideTemplates> TemplateFiles = new Dictionary<string, CockpitGuideTemplates>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CockpitGuide> resolved = new Dictionary<string, CockpitGuide>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new object();
        private static CockpitGuideLibrary cached;
        private static string cachedStamp = string.Empty;

        // DB\Cockpits\guide of a cockpit database folder.
        public static string FolderOf(string cockpitFolder)
        {
            return Path.Combine(cockpitFolder ?? string.Empty, CockpitGuide.FolderName);
        }

        // The library of a cockpit database folder, read again when a guide file changed.
        public static CockpitGuideLibrary For(string cockpitFolder)
        {
            string folder = FolderOf(cockpitFolder);
            string stamp = Stamp(folder);
            lock (Gate)
            {
                if (cached != null && string.Equals(cached.Folder, folder, StringComparison.OrdinalIgnoreCase) && cachedStamp == stamp)
                    return cached;
                cached = Load(folder);
                cachedStamp = stamp;
                return cached;
            }
        }

        private static string Stamp(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return "none";
                string[] files = Directory.GetFiles(folder, "*.json");
                return files.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                       (files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks)).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "error";
            }
        }

        public static CockpitGuideLibrary Load(string folder)
        {
            CockpitGuideLibrary lib = new CockpitGuideLibrary { Folder = folder ?? string.Empty };
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return lib;
            List<CockpitGuide> parts = new List<CockpitGuide>();
            foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("_", StringComparison.Ordinal))
                {
                    if (name.StartsWith("_templates", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            CockpitGuideTemplates t = CockpitGuideTemplates.Parse(MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>);
                            if (t != null)
                                lib.TemplateFiles[t.Language] = t;
                        }
                        catch (Exception ex)
                        {
                            lib.Warnings.Add(name + ": " + ex.Message);
                        }
                    }
                    continue;
                }
                try
                {
                    Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                    string format = DeviceCatalog.Text(root, "format");
                    if (string.Equals(format, CockpitGuide.TextFormatName, StringComparison.OrdinalIgnoreCase))
                    {
                        CockpitGuideText t = CockpitGuideText.Parse(root, name);
                        Dictionary<string, CockpitGuideText> langs;
                        if (!lib.Texts.TryGetValue(t.Guide, out langs))
                            lib.Texts[t.Guide] = langs = new Dictionary<string, CockpitGuideText>(StringComparer.OrdinalIgnoreCase);
                        langs[t.Language] = t;
                        continue;
                    }
                    if (!string.Equals(format, CockpitGuide.FormatName, StringComparison.OrdinalIgnoreCase))
                    {
                        lib.Warnings.Add(name + ": format is not " + CockpitGuide.FormatName);
                        continue;
                    }
                    CockpitGuide g = CockpitGuide.Parse(root, name);
                    if (g.PartOf.Length > 0)
                        parts.Add(g);
                    else if (lib.Guides.ContainsKey(g.Id))
                        lib.Warnings.Add(name + ": guide " + g.Id + " is defined twice");
                    else
                        lib.Guides[g.Id] = g;
                }
                catch (Exception ex)
                {
                    lib.Warnings.Add(name + ": " + ex.Message);
                }
            }
            foreach (CockpitGuide part in parts)
            {
                CockpitGuide host;
                if (!lib.Guides.TryGetValue(part.PartOf, out host))
                {
                    lib.Warnings.Add(part.File + ": partOf " + part.PartOf + " does not exist");
                    continue;
                }
                CockpitGuide.MergeAreas(host.Areas, part.Areas);
                foreach (CockpitSource s in part.Sources.Where(s => !host.Sources.Any(h => h.Id == s.Id)))
                    host.Sources.Add(s);
            }
            return lib;
        }

        // A guide with its parents (inherits) and, when there is one, in a language.
        public CockpitGuide Resolve(string id, string language = null)
        {
            CockpitGuide g = ResolveEnglish(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (g == null || string.IsNullOrEmpty(language) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return g;
            g = g.Clone();
            // The parent's translation first, then the guide's own.
            List<string> chain = new List<string>();
            for (CockpitGuide x = Guides.ContainsKey(id) ? Guides[id] : null; x != null && !chain.Contains(x.Id); x = x.Inherits.Length > 0 && Guides.ContainsKey(x.Inherits) ? Guides[x.Inherits] : null)
                chain.Insert(0, x.Id);
            foreach (string gid in chain)
            {
                CockpitGuideText t = TextOf(gid, language);
                if (t != null)
                    t.ApplyTo(g);
            }
            return g;
        }

        // The general explanations by function in a language (its words added to the English ones; the English texts
        // where the language has none); null without _templates.json.
        public CockpitGuideTemplates Templates(string language)
        {
            CockpitGuideTemplates en;
            if (!TemplateFiles.TryGetValue("en", out en))
                return null;
            if (string.IsNullOrEmpty(language) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return en;
            CockpitGuideTemplates t;
            if (!TemplateFiles.TryGetValue(language, out t))
            {
                int dash = language.IndexOf('-');
                if (dash <= 0 || !TemplateFiles.TryGetValue(language.Substring(0, dash), out t))
                    return en;
            }
            return en.In(t);
        }

        // The translation of a guide in a language ("de", also "de-AT" -> "de"); null for none.
        public CockpitGuideText TextOf(string guide, string language)
        {
            Dictionary<string, CockpitGuideText> langs;
            if (string.IsNullOrEmpty(language) || !Texts.TryGetValue(guide, out langs))
                return null;
            CockpitGuideText t;
            if (langs.TryGetValue(language, out t))
                return t;
            int dash = language.IndexOf('-');
            return dash > 0 && langs.TryGetValue(language.Substring(0, dash), out t) ? t : null;
        }

        private CockpitGuide ResolveEnglish(string id, HashSet<string> seen)
        {
            CockpitGuide g;
            if (string.IsNullOrEmpty(id) || !Guides.TryGetValue(id, out g) || !seen.Add(id))
                return null;
            lock (resolved)
            {
                CockpitGuide hit;
                if (resolved.TryGetValue(id, out hit))
                    return hit;
            }
            CockpitGuide result = g;
            if (g.Inherits.Length > 0)
            {
                CockpitGuide parent = ResolveEnglish(g.Inherits, seen);
                if (parent != null)
                {
                    result = parent.Clone();
                    result.Id = g.Id;
                    result.Title = g.Title.Length > 0 ? g.Title : parent.Title;
                    result.Variants = new List<string>(g.Variants);
                    result.Games = g.Games.Count > 0 ? new List<string>(g.Games) : result.Games;
                    result.Sets = new List<string>(g.Sets);
                    result.Common = g.Common;
                    result.Intro = g.Intro.Length > 0 ? g.Intro : parent.Intro;
                    result.Inherits = g.Inherits;
                    result.File = g.File;
                    HashSet<string> remove = new HashSet<string>(g.Remove, StringComparer.Ordinal);
                    foreach (CockpitGuideArea a in result.Areas)
                    {
                        a.Panels = a.Panels.Where(p => !remove.Contains(p.Id)).ToList();
                        foreach (CockpitGuidePanel p in a.Panels)
                            p.Controls = p.Controls.Where(c => !remove.Contains(p.Id + "/" + c.Id)).ToList();
                    }
                    result.Areas = result.Areas.Where(a => a.Panels.Count > 0 || g.Areas.Any(x => x.Id == a.Id)).ToList();
                    CockpitGuide.MergeAreas(result.Areas, g.Areas);
                    foreach (CockpitSource s in g.Sources)
                    {
                        result.Sources.RemoveAll(x => x.Id == s.Id);
                        result.Sources.Add(s.Clone());
                    }
                }
            }
            lock (resolved)
                resolved[id] = result;
            return result;
        }

        // The variant's own guide: one that names the variant, else one that names the variant it inherits from or is a
        // seat of. Null when there is none.
        public string GuideIdFor(CockpitVariant v)
        {
            if (v == null)
                return null;
            foreach (string id in new[] { v.Id, v.SeatOf, v.Inherits }.Where(s => !string.IsNullOrEmpty(s)))
            {
                // A guide of the variant it inherits from or is a seat of only when it is about the same game (the MSFS
                // F/A-18E inherits the DCS F/A-18C's grips, not its cockpit).
                bool own = string.Equals(id, v.Id, StringComparison.OrdinalIgnoreCase);
                CockpitGuide g = Guides.Values.Where(x => !x.Common && x.Variants.Any(y => string.Equals(y, id, StringComparison.OrdinalIgnoreCase)) &&
                                                          (own || x.Games.Count == 0 || x.Games.Any(game => v.Games.Any(vg => string.Equals(vg.Game, game, StringComparison.OrdinalIgnoreCase)))))
                    .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (g != null)
                    return g.Id;
            }
            return null;
        }

        // The shared guides (common: true) of the variant's games, in id order.
        public List<string> CommonIdsFor(CockpitVariant v, string game = null)
        {
            if (v == null)
                return new List<string>();
            HashSet<string> games = new HashSet<string>(v.Games.Select(x => x.Game), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(game))
                games.Add(game);
            return Guides.Values.Where(g => g.Common && g.Games.Any(games.Contains)).Select(g => g.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------------------------ the rules (tools/cockpit_guide.py check)

        private static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9._-]*$");
        private static readonly Regex Banned = new Regex(@"\b(TODO|TBD|lorem|ipsum|FIXME|XXX)\b|\?\?|!", RegexOptions.IgnoreCase);

        private static void Length(List<string> problems, string where, string what, string value, int min, int max)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                problems.Add(where + ": no " + what);
                return;
            }
            if (value.Length < min)
                problems.Add(where + ": " + what + " too short (" + value.Length.ToString(CultureInfo.InvariantCulture) + " < " + min.ToString(CultureInfo.InvariantCulture) + ")");
            if (value.Length > max)
                problems.Add(where + ": " + what + " too long (" + value.Length.ToString(CultureInfo.InvariantCulture) + " > " + max.ToString(CultureInfo.InvariantCulture) + ")");
            if (Banned.IsMatch(value))
                problems.Add(where + ": " + what + " has a placeholder or an exclamation mark");
        }

        // Every guide (and translation) against the rules: names and texts, no duplicate texts, sane positions, known
        // variants (when a catalog is given).
        public List<string> Check(CockpitCatalog catalog = null)
        {
            List<string> problems = new List<string>(Warnings);
            foreach (CockpitGuide g in Guides.Values)
            {
                string name = g.File;
                if (!IdPattern.IsMatch(g.Id))
                    problems.Add(name + ": id '" + g.Id + "' is not lower case letters, digits, dots and dashes");
                if (g.Variants.Count == 0 && !g.Common)
                    problems.Add(name + ": names no variants (and is not common)");
                if (g.Common && g.Games.Count == 0)
                    problems.Add(name + ": a common guide must name its games");
                if (g.Inherits.Length > 0 && !Guides.ContainsKey(g.Inherits))
                    problems.Add(name + ": inherits '" + g.Inherits + "', which does not exist");
                if (g.Sources.Count == 0)
                    problems.Add(name + ": no sources");
                if (g.Intro.Length > 0)
                    Length(problems, name, "intro", g.Intro, 40, 1500);
                if (catalog != null)
                {
                    foreach (string v in g.Variants.Where(v => catalog.Variant(v) == null))
                        problems.Add(name + ": variant '" + v + "' is not in the cockpit database");
                }
                HashSet<string> areaIds = new HashSet<string>(), panelIds = new HashSet<string>();
                foreach (CockpitGuideArea a in g.Areas)
                {
                    string aw = name + " area " + a.Id;
                    if (!IdPattern.IsMatch(a.Id)) problems.Add(aw + ": bad id");
                    if (!areaIds.Add(a.Id)) problems.Add(aw + ": area id twice");
                    if (a.Where.Length > 0 && !CockpitGuide.Where.Contains(a.Where)) problems.Add(aw + ": unknown where '" + a.Where + "'");
                    if (a.Seat < 0 || a.Seat > 4) problems.Add(aw + ": seat must be 1, 2, 3 or 4");
                    Length(problems, aw, "name", a.Name, 2, 90);
                    if (g.Inherits.Length == 0 || a.Text.Length > 0)
                        Length(problems, aw, "text", a.Text, 15, 500);
                    foreach (CockpitGuidePanel p in a.Panels)
                    {
                        string pw = name + " panel " + p.Id;
                        if (!IdPattern.IsMatch(p.Id)) problems.Add(pw + ": bad id");
                        if (!panelIds.Add(p.Id)) problems.Add(pw + ": panel id twice");
                        Length(problems, pw, "name", p.Name, 2, 90);
                        Length(problems, pw, "text", p.Text, 40, 800);
                        Length(problems, pw, "context", p.Context, 30, 800);
                        if (p.Controls.Count == 0) problems.Add(pw + ": no controls");
                        HashSet<string> controlIds = new HashSet<string>();
                        foreach (CockpitGuideControl c in p.Controls)
                        {
                            string cw = pw + "/" + c.Id;
                            if (!IdPattern.IsMatch(c.Id)) problems.Add(cw + ": bad id");
                            if (!controlIds.Add(c.Id)) problems.Add(cw + ": control id twice in the panel");
                            Length(problems, cw, "name", c.Name, 2, 90);
                            Length(problems, cw, "text", c.Text, 40, 800);
                            Length(problems, cw, "context", c.Context, 30, 800);
                            if (!CockpitGuide.Types.Contains(c.Type)) problems.Add(cw + ": unknown type '" + c.Type + "'");
                            if (c.Text.Trim().Length > 0 && c.Text.Trim() == c.Context.Trim()) problems.Add(cw + ": text and context are the same");
                            HashSet<string> positionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (CockpitGuidePosition pos in c.Positions)
                            {
                                if (!positionNames.Add(pos.Name)) problems.Add(cw + ": position '" + pos.Name + "' twice");
                                Length(problems, cw, "position name", pos.Name, 1, 40);
                                Length(problems, cw + " " + pos.Name, "position text", pos.Text, 8, 400);
                            }
                            if (c.Type == "switch2" && c.Positions.Count != 2) problems.Add(cw + ": a switch2 has 2 positions, not " + c.Positions.Count.ToString(CultureInfo.InvariantCulture));
                            if (c.Type == "switch3" && c.Positions.Count != 3) problems.Add(cw + ": a switch3 has 3 positions, not " + c.Positions.Count.ToString(CultureInfo.InvariantCulture));
                            if (c.Positions.Count > 16) problems.Add(cw + ": more than 16 positions");
                        }
                    }
                }
            }
            foreach (CockpitGuide g in Guides.Values)
            {
                CockpitGuide r = Resolve(g.Id);
                if (r == null)
                    continue;
                // Within one seat: the same control of the pilot's and the back seat's cockpit may share its words.
                Dictionary<string, List<Tuple<int, string>>> seen = new Dictionary<string, List<Tuple<int, string>>>(StringComparer.Ordinal);
                foreach (Tuple<CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> t in r.AllControls())
                {
                    int seat = t.Item1.Seat;
                    foreach (string key in new[] { "text:" + Squash(t.Item3.Text), "context:" + Squash(t.Item3.Context) })
                    {
                        if (key.Length - key.IndexOf(':') - 1 < 60)
                            continue;
                        string who = t.Item2.Id + "/" + t.Item3.Id;
                        List<Tuple<int, string>> list;
                        if (!seen.TryGetValue(key, out list))
                            seen[key] = list = new List<Tuple<int, string>>();
                        Tuple<int, string> first = list.FirstOrDefault(x => (x.Item1 == seat || x.Item1 == 0 || seat == 0) && x.Item2 != who);
                        if (first != null)
                            problems.Add(g.Id + ": " + who + " has the same " + key.Substring(0, key.IndexOf(':')) + " as " + first.Item2);
                        list.Add(Tuple.Create(seat, who));
                    }
                }
            }
            // The general explanations by function: ids once, a text with the control's name, translations of known ids.
            CockpitGuideTemplates en;
            if (TemplateFiles.TryGetValue("en", out en))
            {
                foreach (CockpitGuideTemplates t in TemplateFiles.Values)
                {
                    HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (CockpitGuideTemplates.Template x in t.Items)
                    {
                        string w = "_templates." + t.Language + " " + x.Id;
                        if (!ids.Add(x.Id)) problems.Add(w + ": id twice");
                        if (x.Words.Count == 0 && t == en) problems.Add(w + ": no words");
                        if (x.Text.IndexOf("{name}", StringComparison.Ordinal) < 0) problems.Add(w + ": the text does not name the control ({name})");
                        Length(problems, w, "text", x.Text, 40, 500);
                        Length(problems, w, "context", x.Context, 30, 500);
                        if (t != en && !en.Items.Any(e => e.Id == x.Id)) problems.Add(w + ": not in the English templates");
                    }
                }
            }
            foreach (KeyValuePair<string, Dictionary<string, CockpitGuideText>> pair in Texts)
            {
                CockpitGuide r = Resolve(pair.Key);
                foreach (CockpitGuideText t in pair.Value.Values)
                {
                    if (r == null)
                    {
                        problems.Add(t.File + ": guide '" + t.Guide + "' does not exist");
                        continue;
                    }
                    Dictionary<string, CockpitGuidePanel> panels = r.Areas.SelectMany(a => a.Panels).GroupBy(p => p.Id).ToDictionary(x => x.Key, x => x.First());
                    foreach (string id in t.Areas.Keys.Where(id => !r.Areas.Any(a => a.Id == id)))
                        problems.Add(t.File + ": area '" + id + "' is not in the guide");
                    foreach (string id in t.Panels.Keys.Where(id => !panels.ContainsKey(id)))
                        problems.Add(t.File + ": panel '" + id + "' is not in the guide");
                    foreach (KeyValuePair<string, CockpitGuideText.Entry> e in t.Controls)
                    {
                        int slash = e.Key.IndexOf('/');
                        CockpitGuidePanel p;
                        CockpitGuideControl c = slash > 0 && panels.TryGetValue(e.Key.Substring(0, slash), out p) ? p.Controls.FirstOrDefault(x => x.Id == e.Key.Substring(slash + 1)) : null;
                        if (c == null)
                        {
                            problems.Add(t.File + ": control '" + e.Key + "' is not in the guide");
                            continue;
                        }
                        foreach (string pos in e.Value.Positions.Keys.Where(k => !c.Positions.Any(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase))))
                            problems.Add(t.File + ": " + e.Key + ": position '" + pos + "' is not a position of the control");
                    }
                }
            }
            return problems;
        }

        private static string Squash(string s)
        {
            return Regex.Replace((s ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        }
    }

    // ---------------------------------------------------------------------- matching the game's controls

    // Which guide control each game control (a picture callout) is: the first control in guide order whose match keys fit
    // (a glob of the callout id, cmd:<glob> of its command ids or names, label:<text>).
    public sealed class CockpitGuideMatcher
    {
        private sealed class Key
        {
            public CockpitGuide Guide;
            public CockpitGuideArea Area;
            public CockpitGuidePanel Panel;
            public CockpitGuideControl Control;
            public string Kind;
            public Regex Pattern;
            public string Exact;
            public string Label;
        }

        private readonly List<Key> keys = new List<Key>();

        public CockpitGuideMatcher(IEnumerable<CockpitGuide> guides)
        {
            foreach (CockpitGuide g in guides.Where(x => x != null))
            {
                foreach (Tuple<CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> t in g.AllControls())
                {
                    foreach (string k in t.Item3.Match)
                    {
                        Key key = new Key { Guide = g, Area = t.Item1, Panel = t.Item2, Control = t.Item3 };
                        if (k.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
                        {
                            key.Kind = "cmd";
                            Glob(key, k.Substring(4));
                        }
                        else if (k.StartsWith("label:", StringComparison.OrdinalIgnoreCase))
                        {
                            key.Kind = "label";
                            key.Label = CockpitPictureSet.Norm(k.Substring(6));
                            if (key.Label.Length == 0)
                                continue;
                        }
                        else if (k.StartsWith("item:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        else
                        {
                            key.Kind = "id";
                            Glob(key, k);
                        }
                        keys.Add(key);
                    }
                }
            }
        }

        private static void Glob(Key key, string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
                key.Exact = pattern;
            else
                key.Pattern = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool Fits(Key key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            return key.Exact != null ? string.Equals(key.Exact, value, StringComparison.OrdinalIgnoreCase) : key.Pattern.IsMatch(value);
        }

        // The guide control of a callout in a seat's view, or null.
        public Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> Match(CockpitPictureCallout callout, int seat)
        {
            if (callout == null)
                return null;
            string label = CockpitPictureSet.Norm(callout.Label);
            foreach (Key k in keys)
            {
                if (k.Area.Seat > 0 && k.Area.Seat != seat)
                    continue;
                bool hit = false;
                switch (k.Kind)
                {
                    case "id":
                        hit = Fits(k, callout.Id);
                        break;
                    case "label":
                        hit = label.Length > 0 && label == k.Label;
                        break;
                    case "cmd":
                        hit = callout.Commands.Any(c => Fits(k, c.Id) || Fits(k, c.Name));
                        break;
                }
                if (hit)
                    return Tuple.Create(k.Guide, k.Area, k.Panel, k.Control);
            }
            return null;
        }
    }

    // General explanations by function (DB\Cockpits\guide\_templates.json, format joypro-cockpit-guide-templates) for
    // controls no guide explains (War Thunder, Nuclear Option, Star Citizen, the long tail): the template with the
    // longest of its words in the control's name (else in the catalog's note) gives the text, "{name}" filled in.
    public sealed class CockpitGuideTemplates
    {
        public sealed class Template
        {
            public string Id = string.Empty;
            public List<string> Words = new List<string>();
            public string Text = string.Empty;
            public string Context = string.Empty;
        }

        public string Language = "en";
        public List<Template> Items = new List<Template>();

        public static CockpitGuideTemplates Parse(Dictionary<string, object> root)
        {
            if (root == null || !string.Equals(DeviceCatalog.Text(root, "format"), "joypro-cockpit-guide-templates", StringComparison.OrdinalIgnoreCase))
                return null;
            CockpitGuideTemplates t = new CockpitGuideTemplates { Language = DeviceCatalog.Text(root, "language") };
            if (t.Language.Length == 0)
                t.Language = "en";
            foreach (Dictionary<string, object> item in DeviceCatalog.Objects(root, "templates"))
            {
                t.Items.Add(new Template
                {
                    Id = DeviceCatalog.Text(item, "id"), Words = CockpitCatalog.Strings(item, "words").Select(w => w.ToLowerInvariant()).ToList(),
                    Text = DeviceCatalog.Text(item, "text"), Context = DeviceCatalog.Text(item, "context")
                });
            }
            return t;
        }

        // The English templates with a translation's texts and its words added.
        public CockpitGuideTemplates In(CockpitGuideTemplates other)
        {
            if (other == null)
                return this;
            CockpitGuideTemplates t = new CockpitGuideTemplates { Language = other.Language };
            foreach (Template e in Items)
            {
                Template o = other.Items.FirstOrDefault(x => x.Id == e.Id);
                t.Items.Add(new Template
                {
                    Id = e.Id, Words = e.Words.Concat(o == null ? Enumerable.Empty<string>() : o.Words).Distinct().ToList(),
                    Text = o != null && o.Text.Length > 0 ? o.Text : e.Text, Context = o != null && o.Context.Length > 0 ? o.Context : e.Context
                });
            }
            return t;
        }

        // The template for a name: the one with the longest matching word (whole words, any case); null for none.
        private static readonly Regex NonWordRun = new Regex(@"[^\p{L}\p{Nd}]+");
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PaddedWords =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public Template Find(string name)
        {
            string text = " " + NonWordRun.Replace((name ?? string.Empty).ToLowerInvariant(), " ") + " ";
            Template best = null;
            int bestLength = 0;
            foreach (Template t in Items)
            {
                foreach (string w in t.Words)
                {
                    // The template words' padded forms are worked out once (Find runs for every action described).
                    string word = PaddedWords.GetOrAdd(w ?? string.Empty, x => " " + NonWordRun.Replace(x, " ").Trim() + " ");
                    if (word.Length > bestLength + 0 && word.Trim().Length > 0 && text.Contains(word) && word.Length > bestLength)
                    {
                        best = t;
                        bestLength = word.Length;
                    }
                }
            }
            return best;
        }

        // Fills a control without an explanation: by its name, else by the catalog's note.
        public bool Apply(CockpitGuideNode n)
        {
            // Something that only shows (a gauge, a light, a screen) is explained as what it is, not by its function.
            string shows = n.Type == "gauge" || n.Type == "indicator" ? "gauge" : n.Type == "display" || n.Type == "screen" ? "display"
                : n.Type == "light" ? "lamp" : string.Empty;
            Template t = shows.Length > 0 ? Items.FirstOrDefault(x => x.Id == shows) : null;
            t = t ?? Find(n.Name) ?? Find(n.Note);
            if (t == null || t.Text.Length == 0)
                return false;
            n.Text = t.Text.Replace("{name}", n.Name);
            if (n.Context.Length == 0)
                n.Context = t.Context.Replace("{name}", n.Name);
            n.Template = t.Id;
            return true;
        }
    }
}
