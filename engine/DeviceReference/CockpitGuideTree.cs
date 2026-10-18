#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.DeviceReference
{
    // Where a node of the guide tree is shown: a manual page (CockpitManualPages ids: "game:<view id>", "panel:<panel
    // key>", "device:<device>", "overview") and the tag of its callout or list line on that page ("click:<callout id>",
    // "item:<panel>:<n>", "control:<control id>", "area:<n>").
    public sealed class CockpitGuideHit
    {
        public string Page = string.Empty;
        public string Tag = string.Empty;
        public int N;
        // The game's own name of the control (the callout's label), or the catalog item's name.
        public string Label = string.Empty;
    }

    // One row of the guide tree: an area (rough section), a panel, or a control.
    public sealed class CockpitGuideNode
    {
        // area, panel, control.
        public string Kind = string.Empty;
        // Unique within the tree: "area:<id>", "panel:<id>", "control:<panel>/<id>"; game controls without a guide entry
        // "game:<view>/<callout>", catalog items "item:<panel>:<n>", grip controls "grip:<id>".
        public string Key = string.Empty;
        public string Name = string.Empty;
        // What it does (plain words) and the bigger picture. Empty Text: no written explanation (Generic).
        public string Text = string.Empty;
        public string Context = string.Empty;
        // The catalog's or the game's short technical note (shown under a generic explanation).
        public string Note = string.Empty;
        public string Type = string.Empty;
        public string Where = string.Empty;
        public int Count;
        public string Source = string.Empty;
        public List<CockpitGuidePosition> Positions = new List<CockpitGuidePosition>();
        public List<CockpitGuideNode> Children = new List<CockpitGuideNode>();
        public CockpitGuideNode Parent;
        public List<CockpitGuideHit> Hits = new List<CockpitGuideHit>();
        // The game's bindable commands of the controls it is (DCS commands, X-Plane commands, IL-2 actions, MSFS events).
        public List<CockpitPictureCommand> Commands = new List<CockpitPictureCommand>();
        // JoyPro function ids (catalog items and HOTAS rows) and the catalog's grip control ids.
        public List<string> Fns = new List<string>();
        public List<string> ControlIds = new List<string>();
        // From a shared guide of the game (common: true), not the aircraft's own.
        public bool Shared;
        // The explanation is a general one of its function (CockpitGuideTemplates: "landing gear", "flaps"), filled in
        // with the control's own name; the id of that template.
        public string Template = string.Empty;

        // No written explanation: the GUI explains it by its type.
        public bool Generic { get { return Kind == "control" && (Text.Length == 0 || Template.Length > 0); } }

        public bool HasPicture { get { return Hits.Any(h => h.Page.Length > 0 && h.Tag.Length > 0); } }

        public IEnumerable<CockpitGuideNode> Descendants()
        {
            foreach (CockpitGuideNode c in Children)
            {
                yield return c;
                foreach (CockpitGuideNode d in c.Descendants())
                    yield return d;
            }
        }

        public int ControlCount { get { return Kind == "control" ? 1 : Descendants().Count(n => n.Kind == "control"); } }

        // Everything the search looks at.
        public string SearchText
        {
            get
            {
                return string.Join(" ", new[] { Name, Text, Context, Note }
                    .Concat(Positions.Select(p => p.Name + " " + p.Text))
                    .Concat(Hits.Select(h => h.Label))
                    .Concat(Commands.Select(c => c.Name)));
            }
        }

        // The first sentence of the explanation (the printed pages' compact form).
        public string Short
        {
            get
            {
                string t = Text.Length > 0 ? Text : Note;
                Match m = Regex.Match(t, @"^.*?[.:;](\s|$)");
                string s = m.Success && m.Length >= 20 ? m.Value.Trim() : t;
                return s.Length > 160 ? s.Substring(0, 157).TrimEnd() + "..." : s;
            }
        }
    }

    // The guide of a variant as a tree: the aircraft's own guide (its areas, panels and every control), the shared
    // guides of its games (only the controls this cockpit has), and every game control, catalog item and grip control
    // no guide explains (under their area and panel, with the game's or the catalog's name).
    public sealed class CockpitGuideTree
    {
        public string GuideId = string.Empty;
        public string Title = string.Empty;
        public string Intro = string.Empty;
        public string Language = "en";
        public List<CockpitSource> Sources = new List<CockpitSource>();
        public List<CockpitGuideNode> Roots = new List<CockpitGuideNode>();
        // A page tag ("click:<id>", "item:<panel>:<n>", "control:<id>", "area:<n>") -> its node.
        public Dictionary<string, CockpitGuideNode> ByTag = new Dictionary<string, CockpitGuideNode>(StringComparer.OrdinalIgnoreCase);
        // view id + "|" + callout id -> its node.
        public Dictionary<string, CockpitGuideNode> ByCallout = new Dictionary<string, CockpitGuideNode>(StringComparer.OrdinalIgnoreCase);
        // view id -> its title by the guide's panels ("ELEC control panel, EPU control panel").
        public Dictionary<string, string> ViewTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int GameControls;
        public int GameControlsExplained;

        public bool HasGuide { get { return GuideId.Length > 0; } }

        public IEnumerable<CockpitGuideNode> All()
        {
            foreach (CockpitGuideNode r in Roots)
            {
                yield return r;
                foreach (CockpitGuideNode d in r.Descendants())
                    yield return d;
            }
        }

        public int Controls { get { return All().Count(n => n.Kind == "control"); } }

        public int Explained { get { return All().Count(n => n.Kind == "control" && !n.Generic); } }

        // Controls with only a general description of their function (a template).
        public int Templated { get { return All().Count(n => n.Kind == "control" && n.Template.Length > 0); } }

        public CockpitGuideNode Node(string key)
        {
            return All().FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // The node of a page item's tag ("click:..|fn:.." works too).
        public CockpitGuideNode NodeOfTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;
            foreach (string part in tag.Split('|'))
            {
                CockpitGuideNode n;
                if (ByTag.TryGetValue(part, out n))
                    return n;
            }
            return null;
        }

        // The nodes whose text has every word of the query (controls, panels, areas).
        public List<CockpitGuideNode> Search(string query)
        {
            List<string> terms = (query ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(CockpitCatalog.Norm).Where(t => t.Length > 0).ToList();
            if (terms.Count == 0)
                return new List<CockpitGuideNode>();
            return All().Where(n =>
            {
                string t = CockpitCatalog.Norm(n.SearchText);
                return terms.All(q => t.Contains(q));
            }).ToList();
        }

        // ------------------------------------------------------------------ building

        private sealed class Builder
        {
            public CockpitGuideTree Tree;
            public CockpitVariant Variant;
            public Dictionary<CockpitGuideArea, CockpitGuideNode> Areas = new Dictionary<CockpitGuideArea, CockpitGuideNode>();
            public Dictionary<CockpitGuidePanel, CockpitGuideNode> Panels = new Dictionary<CockpitGuidePanel, CockpitGuideNode>();
            public Dictionary<CockpitGuideControl, CockpitGuideNode> Controls = new Dictionary<CockpitGuideControl, CockpitGuideNode>();
            public Dictionary<string, CockpitGuideNode> Generic = new Dictionary<string, CockpitGuideNode>(StringComparer.OrdinalIgnoreCase);
            public CockpitGuide Own;
            public HashSet<CockpitGuide> Shared = new HashSet<CockpitGuide>();

            public CockpitGuideNode AreaNode(CockpitGuide g, CockpitGuideArea a)
            {
                CockpitGuideNode n;
                if (Areas.TryGetValue(a, out n))
                    return n;
                // A shared guide's area joins the own area of the same place.
                if (g != Own)
                {
                    n = Tree.Roots.FirstOrDefault(r => r.Where.Length > 0 && r.Where == a.Where);
                    if (n != null)
                        return Areas[a] = n;
                }
                n = new CockpitGuideNode { Kind = "area", Key = "area:" + a.Id, Name = a.Name, Text = a.Text, Where = a.Where, Shared = g != Own };
                while (Tree.Roots.Any(r => r.Key == n.Key))
                    n.Key += "+";
                Tree.Roots.Add(n);
                return Areas[a] = n;
            }

            public CockpitGuideNode PanelNode(CockpitGuide g, CockpitGuideArea a, CockpitGuidePanel p)
            {
                CockpitGuideNode n;
                if (Panels.TryGetValue(p, out n))
                    return n;
                CockpitGuideNode area = AreaNode(g, a);
                n = new CockpitGuideNode
                {
                    Kind = "panel", Key = "panel:" + p.Id, Name = p.Name, Text = p.Text, Context = p.Context, Where = a.Where, Parent = area,
                    Source = g.Cite(p.Src, p.Page), Shared = g != Own
                };
                while (area.Children.Any(c => c.Key == n.Key) || Tree.All().Any(x => x.Key == n.Key))
                    n.Key += "+";
                area.Children.Add(n);
                return Panels[p] = n;
            }

            public CockpitGuideNode ControlNode(CockpitGuide g, CockpitGuideArea a, CockpitGuidePanel p, CockpitGuideControl c)
            {
                CockpitGuideNode n;
                if (Controls.TryGetValue(c, out n))
                    return n;
                CockpitGuideNode panel = PanelNode(g, a, p);
                n = new CockpitGuideNode
                {
                    Kind = "control", Key = "control:" + p.Id + "/" + c.Id, Name = c.Name, Text = c.Text, Context = c.Context, Type = c.Type,
                    Where = a.Where, Count = c.Count, Parent = panel, Source = g.Cite(c.Src.Length > 0 ? c.Src : p.Src, c.Page.Length > 0 ? c.Page : p.Page),
                    Positions = c.Positions.Select(x => new CockpitGuidePosition { Name = x.Name, Text = x.Text }).ToList(), Shared = g != Own
                };
                if (c.Fn.Length > 0)
                    n.Fns.Add(c.Fn);
                panel.Children.Add(n);
                return Controls[c] = n;
            }

            // The area of a game control, catalog item or grip control no guide explains: the own area of that place,
            // else a new one named for the place.
            public CockpitGuideNode PlaceArea(string where)
            {
                string key = CockpitSchematic.AreaKey(where);
                if (key == "stick" || key == "throttle")
                    key = "hotas";
                if (key.Length == 0)
                    key = "other";
                CockpitGuideNode n = Tree.Roots.FirstOrDefault(r => r.Where == key);
                if (n != null)
                    return n;
                n = new CockpitGuideNode { Kind = "area", Key = "place:" + key, Name = PlaceTitle(key), Where = key };
                Tree.Roots.Add(n);
                return n;
            }

            public CockpitGuideNode PlacePanel(CockpitGuideNode area, string key, string name)
            {
                CockpitGuideNode n;
                if (Generic.TryGetValue(key, out n))
                    return n;
                n = new CockpitGuideNode { Kind = "panel", Key = key, Name = name, Where = area.Where, Parent = area };
                area.Children.Add(n);
                return Generic[key] = n;
            }
        }

        public static string PlaceTitle(string where)
        {
            switch (where)
            {
                case "hotas": return "Stick and throttle";
                case "other": return "Other controls";
                default: return CockpitSchematic.AreaTitle(where);
            }
        }

        private static readonly string[] TourOrder =
        {
            "glareshield", "front-left", "front-center", "front-right", "left-console", "right-console", "center-pedestal", "overhead",
            "left-side", "right-side", "floor", "rear", "seat", "helmet", "hotas", "other"
        };

        // The tree of a variant. pictures: the set of the user's game (null: catalog and guide only); language: the
        // guide's translation to use ("de"), empty for English.
        public static CockpitGuideTree Build(CockpitCatalog catalog, CockpitVariant v, CockpitPictureSet pictures, CockpitGuideLibrary library, string language = null)
        {
            CockpitGuideTree tree = new CockpitGuideTree { Language = string.IsNullOrEmpty(language) ? "en" : language };
            if (v == null)
                return tree;
            library = library ?? new CockpitGuideLibrary();
            Builder b = new Builder { Tree = tree, Variant = v };
            string ownId = library.GuideIdFor(v);
            CockpitGuide own = ownId == null ? null : library.Resolve(ownId, language);
            b.Own = own;
            List<CockpitGuide> shared = library.CommonIdsFor(v, pictures == null ? null : pictures.Game).Where(id => id != ownId)
                .Select(id => library.Resolve(id, language)).Where(g => g != null).ToList();
            int seat = pictures != null ? pictures.SeatOf(v)
                : v.SeatOf.Length > 0 || Regex.IsMatch(v.Seat ?? string.Empty, "cpg|rio|wso|operator|gunner|copilot|co-pilot|rear|instructor", RegexOptions.IgnoreCase) ? 2 : 1;
            if (own != null)
            {
                tree.GuideId = own.Id;
                tree.Title = own.Title;
                tree.Intro = own.Intro;
                tree.Sources = own.Sources.Select(s => s.Clone()).ToList();
                foreach (CockpitGuideArea a in own.Areas.Where(a => a.Seat == 0 || a.Seat == seat))
                    foreach (CockpitGuidePanel p in a.Panels)
                        foreach (CockpitGuideControl c in p.Controls)
                            b.ControlNode(own, a, p, c);
            }
            List<CockpitGuide> order = new List<CockpitGuide>();
            if (own != null)
                order.Add(own);
            order.AddRange(shared);
            CockpitGuideMatcher matcher = new CockpitGuideMatcher(order);
            // item: and control: keys -> the guide control (first in guide order).
            Dictionary<string, Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl>> catalogKeys =
                new Dictionary<string, Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl>>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitGuide g in order)
            {
                foreach (Tuple<CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> t in g.AllControls())
                {
                    if (t.Item1.Seat > 0 && t.Item1.Seat != seat)
                        continue;
                    foreach (string k in t.Item3.Match.Where(k => k.StartsWith("item:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("control:", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!catalogKeys.ContainsKey(k))
                            catalogKeys[k] = Tuple.Create(g, t.Item1, t.Item2, t.Item3);
                    }
                }
            }

            // The game's controls (the picture set's panels, grips and, for screenshots, the aircraft's input events).
            if (pictures != null)
            {
                bool screenshot = string.Equals(pictures.Source, "screenshot", StringComparison.OrdinalIgnoreCase);
                List<string> devices = CockpitCatalog.DevicesOf(v);
                foreach (CockpitPictureView view in pictures.ViewsOf(seat))
                {
                    if (view.Kind == "overview" && !screenshot)
                        continue;
                    string page = view.Kind == "panel" ? "game:" + view.Id : view.Kind == "overview" ? "overview" :
                        "device:" + (view.Device == "throttle" ? devices.FirstOrDefault(CockpitCatalog.IsThrottleDevice) : devices.FirstOrDefault(CockpitCatalog.IsStickDevice));
                    foreach (CockpitPictureCallout c in view.Callouts)
                    {
                        if (c.Id.StartsWith("panel:", StringComparison.Ordinal) || c.Id.StartsWith("device:", StringComparison.Ordinal))
                            continue;
                        tree.GameControls++;
                        string tag = view.Kind == "panel" ? "click:" + c.Id : view.Kind == "overview" ? "area:" + c.N.ToString(CultureInfo.InvariantCulture) : string.Empty;
                        CockpitGuideHit hit = new CockpitGuideHit { Page = page.EndsWith(":", StringComparison.Ordinal) ? string.Empty : page, Tag = tag, N = c.N, Label = c.Label };
                        Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> m = matcher.Match(c, seat);
                        CockpitGuideNode node;
                        if (m != null)
                        {
                            tree.GameControlsExplained++;
                            node = b.ControlNode(m.Item1, m.Item2, m.Item3, m.Item4);
                        }
                        else
                        {
                            // Not in a guide: under the game's own panel name, with the game's name for it.
                            CockpitGuideNode area = b.PlaceArea(view.Kind == "device" ? "hotas" : view.Area);
                            string panelName = c.Panel.Length > 0 ? c.Panel : view.Kind == "device" ? CockpitCatalog.DeviceTitle(view.Device) : view.Title;
                            CockpitGuideNode panel = b.PlacePanel(area, "gamepanel:" + CockpitCatalog.Norm(panelName) + "@" + area.Where, panelName);
                            node = panel.Children.FirstOrDefault(x => x.Key == "game:" + c.Id);
                            if (node == null)
                            {
                                node = new CockpitGuideNode { Kind = "control", Key = "game:" + c.Id, Name = c.Label.Length > 0 ? c.Label : c.Id, Type = GameType(c.Kind), Where = area.Where, Parent = panel };
                                panel.Children.Add(node);
                            }
                        }
                        node.Hits.Add(hit);
                        foreach (CockpitPictureCommand cmd in c.Commands)
                        {
                            if (!node.Commands.Any(x => string.Equals(x.Id, cmd.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, cmd.Name, StringComparison.OrdinalIgnoreCase)))
                                node.Commands.Add(cmd);
                        }
                        if (!tree.ByCallout.ContainsKey(view.Id + "|" + c.Id))
                            tree.ByCallout[view.Id + "|" + c.Id] = node;
                        if (tag.Length > 0 && !tree.ByTag.ContainsKey(tag))
                            tree.ByTag[tag] = node;
                        // The positions the picture set knows from the part's animation (a control no guide explains).
                        if (m == null && node.Positions.Count == 0 && c.Positions.Count > 0)
                            node.Positions = c.Positions.Select(x => new CockpitGuidePosition { Name = x }).ToList();
                    }
                }
            }

            // The model's other animated controls (pictures.json "controls"): those that are no callout of a picture are
            // listed too, at the pictures they are visible on; their positions come from the animation's range.
            if (pictures != null)
            {
                foreach (CockpitPictureControl pc in pictures.Controls.Where(x => x.Seat == seat || x.Seat <= 0))
                {
                    if (pc.Id.StartsWith("grip:", StringComparison.Ordinal) || pc.Id.StartsWith("panel:", StringComparison.Ordinal))
                        continue;
                    CockpitGuideNode node = null;
                    foreach (CockpitPictureSpot spot in pc.Views)
                    {
                        if (tree.ByCallout.TryGetValue(spot.View + "|" + pc.Id, out node))
                            break;
                    }
                    if (node == null)
                        node = tree.ByCallout.Where(kv => kv.Key.EndsWith("|" + pc.Id, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).FirstOrDefault();
                    if (node == null)
                    {
                        tree.GameControls++;
                        CockpitPictureCallout c = new CockpitPictureCallout
                        {
                            Id = pc.Id, Label = pc.Label, Kind = pc.Kind, Panel = pc.Panel, Device = pc.Device,
                            Commands = pc.Commands.Where(x => !string.IsNullOrEmpty(x)).Select(x => new CockpitPictureCommand { Id = x, Name = x }).ToList()
                        };
                        CockpitPictureView first = pc.Views.Select(spot => pictures.View(spot.View)).FirstOrDefault(x => x != null);
                        Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> m = matcher.Match(c, seat);
                        if (m != null)
                        {
                            tree.GameControlsExplained++;
                            node = b.ControlNode(m.Item1, m.Item2, m.Item3, m.Item4);
                        }
                        else
                        {
                            CockpitGuideNode area = b.PlaceArea(first == null ? "other" : first.Kind == "device" ? "hotas" : first.Area);
                            string panelName = pc.Panel.Length > 0 ? pc.Panel : first != null && first.Kind == "panel" ? first.Title : PlaceTitle("other");
                            CockpitGuideNode panel = b.PlacePanel(area, "gamepanel:" + CockpitCatalog.Norm(panelName) + "@" + area.Where, panelName);
                            node = panel.Children.FirstOrDefault(x => x.Key == "game:" + pc.Id);
                            if (node == null)
                            {
                                node = new CockpitGuideNode { Kind = "control", Key = "game:" + pc.Id, Name = pc.Label.Length > 0 ? pc.Label : pc.Id, Type = GameType(pc.Kind), Where = area.Where, Parent = panel };
                                panel.Children.Add(node);
                            }
                        }
                        foreach (CockpitPictureCommand cmd in c.Commands.Where(x => !node.Commands.Any(y => string.Equals(y.Id, x.Id, StringComparison.OrdinalIgnoreCase))))
                            node.Commands.Add(cmd);
                        foreach (CockpitPictureSpot spot in pc.Views)
                        {
                            CockpitPictureView view = pictures.View(spot.View);
                            if (view != null && view.Kind == "panel")
                                node.Hits.Add(new CockpitGuideHit { Page = "game:" + view.Id, Tag = string.Empty, N = spot.N, Label = pc.Label });
                        }
                        if (node.Hits.Count == 0)
                            node.Hits.Add(new CockpitGuideHit { Label = pc.Label });
                    }
                    // A control no guide explains: its kind and positions from the model.
                    if (node.Text.Length == 0)
                    {
                        string type = GameType(pc.Kind);
                        if (type != "other" && (node.Type.Length == 0 || node.Type == "other" || node.Type == "switch"))
                            node.Type = type;
                        int states = pc.Positions.Count;
                        if (node.Positions.Count == 0 && states >= 2 && states <= 12)
                            node.Positions = Enumerable.Range(1, states).Select(i => new CockpitGuidePosition { Name = "Position " + i.ToString(CultureInfo.InvariantCulture) }).ToList();
                    }
                }
            }

            // JoyPro's catalog panels and their numbered items.
            foreach (CockpitPanel p in v.Panels)
            {
                CockpitGuideNode dbPanel = null;
                foreach (CockpitPanelItem i in p.Items)
                {
                    string key = "item:" + p.Key + ":" + i.N.ToString(CultureInfo.InvariantCulture);
                    CockpitGuideHit hit = new CockpitGuideHit { Page = "panel:" + p.Key, Tag = key, N = i.N, Label = i.Name };
                    Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> m;
                    CockpitGuideNode node;
                    if (catalogKeys.TryGetValue(key, out m))
                    {
                        node = b.ControlNode(m.Item1, m.Item2, m.Item3, m.Item4);
                    }
                    else
                    {
                        if (dbPanel == null)
                        {
                            // The guide's panel that is this catalog panel, else the catalog panel under its area.
                            KeyValuePair<CockpitGuidePanel, CockpitGuideNode> same = b.Panels.FirstOrDefault(x => string.Equals(x.Key.Db, p.Key, StringComparison.OrdinalIgnoreCase));
                            dbPanel = same.Value;
                            if (dbPanel == null)
                            {
                                CockpitGuideNode area = b.PlaceArea(p.Area);
                                dbPanel = b.PlacePanel(area, "dbpanel:" + p.Key, p.Name);
                                if (dbPanel.Note.Length == 0)
                                    dbPanel.Note = p.Text;
                                dbPanel.Hits.Add(new CockpitGuideHit { Page = "panel:" + p.Key, Tag = string.Empty, Label = p.Name });
                                if (dbPanel.Source.Length == 0)
                                    dbPanel.Source = v.Cite(p.Src, p.Page);
                            }
                        }
                        node = new CockpitGuideNode
                        {
                            Kind = "control", Key = key, Name = i.Name, Note = i.Text, Type = i.Type, Where = dbPanel.Where, Parent = dbPanel,
                            Positions = i.Positions.Select(x => new CockpitGuidePosition { Name = x }).ToList(), Source = v.Cite(i.Src.Length > 0 ? i.Src : p.Src, i.Page.Length > 0 ? i.Page : p.Page)
                        };
                        dbPanel.Children.Add(node);
                    }
                    node.Hits.Add(hit);
                    if (i.Fn.Length > 0 && !node.Fns.Contains(i.Fn))
                        node.Fns.Add(i.Fn);
                    if (!tree.ByTag.ContainsKey(key))
                        tree.ByTag[key] = node;
                }
            }

            // The grips' controls (the HOTAS tables).
            foreach (CockpitControl c in v.Controls.Where(x => x.Functions.Count > 0))
            {
                string key = "control:" + c.Id;
                CockpitGuideHit hit = new CockpitGuideHit { Page = "device:" + c.Device, Tag = key, Label = c.Name };
                Tuple<CockpitGuide, CockpitGuideArea, CockpitGuidePanel, CockpitGuideControl> m;
                CockpitGuideNode node;
                if (catalogKeys.TryGetValue(key, out m))
                {
                    node = b.ControlNode(m.Item1, m.Item2, m.Item3, m.Item4);
                }
                else
                {
                    CockpitGuideNode area = b.PlaceArea("hotas");
                    CockpitGuideNode panel = b.PlacePanel(area, "grip:" + c.Device, CockpitCatalog.DeviceTitle(c.Device));
                    node = new CockpitGuideNode
                    {
                        Kind = "control", Key = "grip:" + c.Id, Name = c.Name, Type = GripType(c.Type), Where = "hotas", Parent = panel,
                        Note = (c.Where.Length > 0 ? c.Where + ". " : string.Empty) + string.Join(" ", c.Functions.Select(f => FunctionLine(v, f)).Where(s => s.Length > 0).Distinct()),
                        Source = v.Cite(c.Src, c.Page)
                    };
                    panel.Children.Add(node);
                }
                node.Hits.Add(hit);
                if (!node.ControlIds.Contains(c.Id))
                    node.ControlIds.Add(c.Id);
                foreach (CockpitFunction f in c.Functions)
                {
                    foreach (string fn in new[] { f.Fn }.Concat(f.FnBy.Values).Where(x => !string.IsNullOrEmpty(x)))
                    {
                        if (!node.Fns.Contains(fn))
                            node.Fns.Add(fn);
                    }
                }
                if (!tree.ByTag.ContainsKey(key))
                    tree.ByTag[key] = node;
            }

            // Shared-guide panels and areas that got no control stay out; the own guide's stay (they are this aircraft's).
            foreach (CockpitGuideNode area in tree.Roots.ToList())
            {
                area.Children.RemoveAll(p => p.Children.Count == 0);
                if (area.Children.Count == 0)
                    tree.Roots.Remove(area);
            }
            // The places no guide area covers: in the tour order after the guide's own areas.
            List<CockpitGuideNode> guided = tree.Roots.Where(r => !r.Key.StartsWith("place:", StringComparison.Ordinal)).ToList();
            List<CockpitGuideNode> places = tree.Roots.Where(r => r.Key.StartsWith("place:", StringComparison.Ordinal))
                .OrderBy(r => Array.IndexOf(TourOrder, r.Where) < 0 ? 99 : Array.IndexOf(TourOrder, r.Where)).ToList();
            if (own == null)
                guided = guided.OrderBy(r => Array.IndexOf(TourOrder, r.Where) < 0 ? 99 : Array.IndexOf(TourOrder, r.Where)).ToList();
            tree.Roots = guided.Concat(places).ToList();
            foreach (CockpitGuideNode n in tree.All())
            {
                foreach (CockpitGuideNode c in n.Children)
                    c.Parent = n;
            }
            // The game views' titles by the guide panels of their controls.
            if (pictures != null)
            {
                foreach (CockpitPictureView view in pictures.Views.Where(x => x.Kind == "panel"))
                {
                    List<string> names = view.Callouts.Select(c =>
                        {
                            CockpitGuideNode n;
                            return tree.ByCallout.TryGetValue(view.Id + "|" + c.Id, out n) && n.Parent != null ? n.Parent.Name : null;
                        }).Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                    if (names.Count > 0)
                        tree.ViewTitles[view.Id] = names.Count <= 2 ? string.Join(", ", names) : names[0] + ", " + names[1] + ", ...";
                }
            }
            // Controls no guide explains: the general explanation of their function, with their own name.
            CockpitGuideTemplates templates = library.Templates(language);
            if (templates != null)
            {
                foreach (CockpitGuideNode n in tree.All().Where(x => x.Kind == "control" && x.Text.Length == 0))
                    templates.Apply(n);
            }
            return tree;
        }

        private static string FunctionLine(CockpitVariant v, CockpitFunction f)
        {
            string text = f.Text.Length > 0 ? f.Text : f.TextIn(v.DefaultModeId);
            if (text.Length == 0)
                return string.Empty;
            string dir = f.Dir.Length == 0 ? string.Empty : CockpitManualPages.ActionWords(f) + (f.Press.Length > 0 ? " (" + CockpitManualPages.PressWords(f.Press).ToLowerInvariant() + ")" : string.Empty) + ": ";
            return dir + text.TrimEnd('.') + ".";
        }

        // The guide's type of a picture callout's kind (switch, button, knob, lever, part, event ...).
        public static string GameType(string kind)
        {
            switch ((kind ?? string.Empty).ToLowerInvariant())
            {
                case "switch": return "switch";
                case "switch-2": return "switch2";
                case "switch-3": return "switch3";
                case "switch-n": return "selector";
                case "button": return "button";
                case "knob": case "rotary": return "knob";
                case "lever": return "lever";
                case "axis": return "axis";
                case "hat": return "hat";
                case "trigger": return "trigger";
                case "screen": return "display";
                case "cover": return "cover";
                case "wheel": return "wheel";
                case "pedal": return "pedal";
                case "event": return "other";
                default: return "other";
            }
        }

        public static string GripType(string type)
        {
            switch (type ?? string.Empty)
            {
                case "hat4": case "hat8": case "hat2": return "hat";
                case "trigger": return "trigger";
                case "button": return "button";
                case "rocker": return "rocker";
                case "switch2": case "switch3": return "switch";
                case "slider": return "slider";
                case "wheel": return "wheel";
                case "knob": case "encoder": return "knob";
                case "lever": case "paddle": case "detent": return "lever";
                case "axis": case "gimbal": case "twist": case "ministick": return "axis";
                default: return "other";
            }
        }
    }
}
