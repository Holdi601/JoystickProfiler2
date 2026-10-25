using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// One visible row of the cockpit guide tree (area > panel > control) with its "What does it do?" explanation.
public sealed partial class CockpitGuideRow : ObservableObject
{
    public CockpitGuideRow(CockpitGuideNode node, int depth, bool expanded, bool details, string relations)
    {
        Node = node;
        Depth = depth;
        isExpanded = expanded;
        showDetails = details;
        Relations = relations;
    }

    public CockpitGuideNode Node { get; }

    public int Depth { get; }

    public string Key => Node.Key;

    public bool IsArea => Node.Kind == "area";

    public bool IsPanel => Node.Kind == "panel";

    public bool IsControl => Node.Kind == "control";

    public bool HasChildren => Node.Children.Count > 0;

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Chevron))] private bool isExpanded;

    [ObservableProperty] private bool showDetails;

    public string Chevron => HasChildren ? (IsExpanded ? "▾" : "▸") : string.Empty;

    public string Name => Node.Name;

    // "Switch · 3 positions", "Panel · 12 controls", "Area · 4 panels".
    public string Subtitle
    {
        get
        {
            if (IsArea)
                return Loc.Plural("{0} panel", "{0} panels", Node.Children.Count);
            if (IsPanel)
                return Loc.Plural("{0} control", "{0} controls", Node.ControlCount);
            List<string> parts = [CockpitGuideExplain.TypeName(Node.Type)];
            if (Node.Count > 1)
                parts.Add(Loc.Plural("{0} of them", "{0} of them", Node.Count));
            if (Node.Positions.Count > 0)
                parts.Add(Loc.Plural("{0} position", "{0} positions", Node.Positions.Count));
            if (Node.Generic)
                parts.Add(Node.Template.Length > 0 ? Loc.T("general description") : Loc.T("not explained yet"));
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    // What it does (the guide's text, else a general description of its kind of control).
    public string Explanation => Node.Text.Length > 0 ? Node.Text : CockpitGuideExplain.Generic(Node);

    public bool HasExplanation => Explanation.Length > 0;

    public string Context => Node.Context;

    public bool HasContext => Node.Context.Length > 0;

    public string Note => Node.Note.Length == 0 ? string.Empty : Loc.T("JoyPro's catalog: {0}", Node.Note);

    public bool HasNote => Node.Note.Length > 0;

    public string GenericHint => !Node.Generic ? string.Empty
        : Node.Template.Length > 0 ? Loc.T("A general description of this kind of control, with the game's own name for it.")
        : Loc.T("JoyPro has no plain-language explanation of this control yet; this is a general description of its kind.");

    public bool IsGeneric => Node.Generic;

    // "OFF: no power ..." one line per position.
    public string PositionsText => string.Join("\n", Node.Positions.Select(p => p.Text.Length > 0 ? PositionName(p.Name) + ": " + p.Text : PositionName(p.Name)));

    // "Position 2" (an animated part's state without a printed name) in the user's language.
    private static string PositionName(string name) =>
        name.StartsWith("Position ", StringComparison.Ordinal) && int.TryParse(name.AsSpan(9), out int n) ? Loc.T("Position {0}", n) : name;

    public bool HasPositions => Node.Positions.Count > 0;

    // The game's names of the control (when the guide names it differently) and its bindable commands.
    public string GameText
    {
        get
        {
            List<string> labels = Node.Hits.Select(h => h.Label).Where(l => l.Length > 0 && !string.Equals(l, Node.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            return labels.Count == 0 ? string.Empty : Loc.T("In the game: {0}", string.Join("; ", labels));
        }
    }

    public bool HasGameText => GameText.Length > 0;

    public string CommandsText
    {
        get
        {
            // Readable names (X-Plane's command paths as words); the ids in the tooltip.
            List<string> names = Node.Commands.Select(c => CockpitManualPages.CommandName(c.Name)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
                return string.Empty;
            return Loc.T("Commands to bind: {0}", string.Join("; ", names.Take(8)) + (names.Count > 8 ? "; ..." : string.Empty));
        }
    }

    // The game's own command ids (the tooltip of the commands).
    public string CommandIds => string.Join(Environment.NewLine, Node.Commands.Select(c => c.Id.Length > 0 ? c.Id : c.Name).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12));

    public bool HasCommands => CommandsText.Length > 0;

    // The open profile's relations that bind it (after Compare with my setup).
    public string Relations { get; }

    public bool HasRelations => Relations.Length > 0;

    public string Source => Node.Source.Length == 0 ? string.Empty : Loc.T("Source: {0}", Node.Source);

    public bool HasSource => Node.Source.Length > 0;

    public bool HasDetails => HasExplanation || HasContext || HasPositions || HasCommands || HasNote;

    // Shown on a picture of the manual pages.
    public bool HasPicture => Node.HasPicture || Node.Descendants().Any(d => d.HasPicture);

    public double NameWeight => IsControl ? 400 : 600;
}

// The general descriptions of the kinds of controls (for controls without a written explanation, e.g. War Thunder,
// Nuclear Option, Star Citizen) and the type names.
public static class CockpitGuideExplain
{
    public static string TypeName(string type) => type switch
    {
        "switch2" => Loc.T("Two-position switch"),
        "switch3" => Loc.T("Three-position switch"),
        "switch" => Loc.T("Switch"),
        "knob" => Loc.T("Knob"),
        "selector" => Loc.T("Selector"),
        "button" => Loc.T("Button"),
        "lever" => Loc.T("Lever"),
        "handle" => Loc.T("Handle"),
        "wheel" => Loc.T("Wheel"),
        "guard" or "cover" => Loc.T("Cover"),
        "gauge" => Loc.T("Gauge"),
        "display" or "screen" => Loc.T("Display"),
        "light" or "indicator" => Loc.T("Light"),
        "keypad" => Loc.T("Keypad"),
        "rocker" => Loc.T("Rocker switch"),
        "hat" => Loc.T("Hat switch"),
        "slider" => Loc.T("Slider"),
        "encoder" => Loc.T("Rotary knob"),
        "pedal" => Loc.T("Pedal"),
        "trigger" => Loc.T("Trigger"),
        "axis" => Loc.T("Axis"),
        _ => Loc.T("Control")
    };

    public static string Generic(CockpitGuideNode n)
    {
        string name = n.Name;
        return n.Type switch
        {
            "switch2" or "switch3" or "switch" => Loc.T("\"{0}\" is a switch. Flipping it turns something on or off, or to another setting; each position does what its label says.", name),
            "button" => Loc.T("\"{0}\" is a push button. Pressing it does one thing, like a doorbell.", name),
            "knob" or "encoder" => Loc.T("\"{0}\" is a knob. Turning it sets a value up or down, like the volume knob of a radio.", name),
            "selector" => Loc.T("\"{0}\" is a selector. Turning it picks one of several settings.", name),
            "lever" or "handle" => Loc.T("\"{0}\" is a lever. Moving it changes what its name says, step by step or all at once.", name),
            "wheel" => Loc.T("\"{0}\" is a wheel. Rolling it sets a value finely.", name),
            "axis" or "slider" or "pedal" => Loc.T("\"{0}\" moves smoothly over its whole range, like the gas pedal of a car.", name),
            "hat" => Loc.T("\"{0}\" is a small hat switch pushed with the thumb in several directions; each direction has its own job.", name),
            "trigger" => Loc.T("\"{0}\" is a trigger pulled with a finger.", name),
            "rocker" => Loc.T("\"{0}\" is a rocker switch. Pressing one end or the other moves a value up or down.", name),
            "light" or "indicator" => Loc.T("\"{0}\" is a light. It shows the pilot that something is on, or that something is wrong.", name),
            "gauge" or "display" or "screen" => Loc.T("\"{0}\" shows information to the pilot.", name),
            "guard" or "cover" => Loc.T("\"{0}\" is a protective cover over a switch, so that it is not moved by accident.", name),
            "keypad" => Loc.T("\"{0}\" is a set of keys for typing numbers and letters.", name),
            _ => Loc.T("\"{0}\" is a control of this cockpit.", name)
        };
    }
}

// The cockpit guide on the Cockpits page (docs/cockpits.md "The cockpit guide"): the variant's areas, panels and every
// control as a tree with an explanation to expand; a click on a control opens its page and marks its callout, a click on
// a callout selects its control; the search filters the tree.
public sealed partial class CockpitsViewModel
{
    private CockpitGuideTree? guideTree;
    private string guideKey = string.Empty;
    private readonly HashSet<string> expandedGuide = new(StringComparer.Ordinal);
    private readonly HashSet<string> detailedGuide = new(StringComparer.Ordinal);
    // game command id -> "relation (input)" of the profile (Compare with my setup).
    private Dictionary<string, string> comparedCommands = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CockpitGuideRow> GuideRows { get; } = [];

    [ObservableProperty] private CockpitGuideRow? selectedGuideRow;

    // The tag of the page item marked on the page (the selected control's callout).
    [ObservableProperty] private string selectedTag = string.Empty;

    [ObservableProperty] private string guideSearchText = string.Empty;

    public CockpitGuideTree? GuideTree => guideTree;

    public bool HasGuideTree => guideTree != null && guideTree.Roots.Count > 0;

    public string GuideIntro => guideTree?.Intro ?? string.Empty;

    public bool HasGuideIntro => GuideIntro.Length > 0;

    public string GuideStatus
    {
        get
        {
            if (guideTree == null || guideTree.Roots.Count == 0)
                return T("No controls known for this cockpit.");
            string counts = T("{0} areas, {1} panels, {2} controls", guideTree.Roots.Count, guideTree.All().Count(n => n.Kind == "panel"), guideTree.Controls);
            string explained = guideTree.Explained == guideTree.Controls
                ? T("all explained")
                : T("{0} explained", guideTree.Explained);
            return counts + " · " + explained + (guideTree.HasGuide ? string.Empty : " · " + T("no plain-language guide for this aircraft yet"));
        }
    }

    public bool HasGuideSearchHits => GuideSearchText.Trim().Length > 0;

    public string GuideSearchStatus => !HasGuideSearchHits ? string.Empty
        : Plural("{0} match", "{0} matches", guideTree?.Search(GuideSearchText).Count(n => n.Kind == "control" || n.Kind == "panel") ?? 0);

    // The tree of the variant: built again for another variant, picture set or language.
    private void BuildGuide(CockpitVariant? v, CockpitPictureSet? pictures)
    {
        string language = Loc.Current.Code;
        string key = (v?.Id ?? string.Empty) + "|" + (pictures?.Folder ?? string.Empty) + "|" + (pictures?.Built ?? string.Empty) + "|" + language;
        if (key == guideKey && guideTree != null)
            return;
        guideKey = key;
        expandedGuide.Clear();
        detailedGuide.Clear();
        SelectedTag = string.Empty;
        try
        {
            guideTree = v == null || catalog == null ? null
                : CockpitGuideTree.Build(catalog, v, pictures, CockpitGuideLibrary.For(CockpitService.Folder() ?? string.Empty), language);
        }
        catch (Exception)
        {
            guideTree = null;
        }
        // The areas open at first, the panels closed.
        if (guideTree != null)
        {
            foreach (CockpitGuideNode area in guideTree.Roots)
                expandedGuide.Add(area.Key);
        }
        RefreshGuideRows();
        OnPropertyChanged(nameof(GuideTree));
        OnPropertyChanged(nameof(HasGuideTree));
        OnPropertyChanged(nameof(GuideIntro));
        OnPropertyChanged(nameof(HasGuideIntro));
        OnPropertyChanged(nameof(GuideStatus));
    }

    // The visible rows: the expanded parts of the tree, or the search hits with their areas and panels.
    private void RefreshGuideRows(string? select = null)
    {
        select ??= SelectedGuideRow?.Key;
        GuideRows.Clear();
        if (guideTree == null)
            return;
        HashSet<CockpitGuideNode>? show = null;
        HashSet<CockpitGuideNode>? hits = null;
        if (GuideSearchText.Trim().Length > 0)
        {
            hits = new HashSet<CockpitGuideNode>(guideTree.Search(GuideSearchText));
            show = new HashSet<CockpitGuideNode>();
            foreach (CockpitGuideNode n in hits)
            {
                for (CockpitGuideNode? p = n; p != null; p = p.Parent)
                    show.Add(p);
                // A panel or area that matches shows its controls.
                foreach (CockpitGuideNode d in n.Descendants())
                    show.Add(d);
            }
        }
        foreach (CockpitGuideNode root in guideTree.Roots)
            AddRows(root, 0, show);
        OnPropertyChanged(nameof(GuideSearchStatus));
        OnPropertyChanged(nameof(HasGuideSearchHits));
        CockpitGuideRow? again = select == null ? null : GuideRows.FirstOrDefault(r => r.Key == select);
        if (!ReferenceEquals(again, SelectedGuideRow))
        {
            selectingGuide = true;
            SelectedGuideRow = again;
            selectingGuide = false;
        }
    }

    private void AddRows(CockpitGuideNode n, int depth, HashSet<CockpitGuideNode>? show)
    {
        if (show != null && !show.Contains(n))
            return;
        bool expanded = show != null ? n.Children.Any(show.Contains) : expandedGuide.Contains(n.Key);
        GuideRows.Add(new CockpitGuideRow(n, depth, expanded, detailedGuide.Contains(n.Key), RelationsOf(n)));
        if (!expanded)
            return;
        foreach (CockpitGuideNode c in n.Children)
            AddRows(c, depth + 1, show);
    }

    partial void OnGuideSearchTextChanged(string value) => RefreshGuideRows();

    [RelayCommand]
    private void ToggleGuideRow(CockpitGuideRow? row)
    {
        if (row == null || !row.HasChildren)
            return;
        if (!expandedGuide.Remove(row.Key))
            expandedGuide.Add(row.Key);
        if (GuideSearchText.Trim().Length > 0)
            GuideSearchText = string.Empty;
        else
            RefreshGuideRows(row.Key);
    }

    [RelayCommand]
    private void ToggleGuideDetails(CockpitGuideRow? row)
    {
        if (row == null)
            return;
        row.ShowDetails = !row.ShowDetails;
        if (row.ShowDetails)
            detailedGuide.Add(row.Key);
        else
            detailedGuide.Remove(row.Key);
    }

    [RelayCommand]
    private void ExpandAllGuide()
    {
        if (guideTree == null)
            return;
        foreach (CockpitGuideNode n in guideTree.All().Where(n => n.Children.Count > 0))
            expandedGuide.Add(n.Key);
        RefreshGuideRows();
    }

    [RelayCommand]
    private void CollapseAllGuide()
    {
        expandedGuide.Clear();
        if (guideTree != null)
        {
            foreach (CockpitGuideNode area in guideTree.Roots)
                expandedGuide.Add(area.Key);
        }
        RefreshGuideRows();
    }

    private bool selectingGuide;

    // A row picked: its page opens and its callout is marked.
    partial void OnSelectedGuideRowChanged(CockpitGuideRow? value)
    {
        if (selectingGuide || value == null)
            return;
        ShowNode(value.Node);
    }

    // Opens the page that shows a node (its first callout on a picture, else its first page) and marks it there.
    public bool ShowNode(CockpitGuideNode node)
    {
        IEnumerable<CockpitGuideHit> hits = node.Hits.Concat(node.Descendants().SelectMany(d => d.Hits));
        CockpitGuideHit? hit = hits.Where(h => h.Page.Length > 0 && Pages.Any(p => string.Equals(p.Page.Id, h.Page, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(h => h.Tag.Length > 0 ? 0 : 1).ThenBy(h => h.Page.StartsWith("game:", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault();
        if (hit == null)
        {
            SelectedTag = string.Empty;
            return false;
        }
        OpenPage(hit.Page);
        // A panel or area marks every control of it on its page.
        SelectedTag = node.Kind == "control" ? hit.Tag : string.Join("|", node.Descendants().SelectMany(d => d.Hits).Where(h => h.Page == hit.Page && h.Tag.Length > 0).Select(h => h.Tag).Distinct());
        return true;
    }

    // A callout or list line clicked on a page: its control in the tree, expanded and explained.
    public bool SelectTag(string tag)
    {
        CockpitGuideNode? node = guideTree?.NodeOfTag(tag);
        if (node == null)
            return false;
        if (GuideSearchText.Length > 0)
            GuideSearchText = string.Empty;
        for (CockpitGuideNode? p = node.Parent; p != null; p = p.Parent)
            expandedGuide.Add(p.Key);
        detailedGuide.Add(node.Key);
        RefreshGuideRows(node.Key);
        selectingGuide = true;
        SelectedGuideRow = GuideRows.FirstOrDefault(r => r.Key == node.Key);
        selectingGuide = false;
        string first = tag.Split('|')[0];
        // The page that shows the tag (the one clicked on, or the first page that has it).
        if (CurrentPage == null || !CurrentPage.Items.Any(i => Views.Controls.ManualPageView.IsSelected(i.Tag, first)))
        {
            CockpitPageItem? page = Pages.FirstOrDefault(p => p.Page.Items.Any(i => Views.Controls.ManualPageView.IsSelected(i.Tag, first)));
            if (page != null)
                SelectedPage = page;
        }
        SelectedTag = first;
        GuideRowRevealRequested?.Invoke(this, SelectedGuideRow);
        return true;
    }

    // The view scrolls the tree to a row (a callout was clicked).
    public event EventHandler<CockpitGuideRow?>? GuideRowRevealRequested;

    // "✓ relation (input)" of the open profile for a node: by its game commands and its grip controls.
    private string RelationsOf(CockpitGuideNode n)
    {
        if (compared.Count == 0 && comparedCommands.Count == 0)
            return string.Empty;
        List<string> found = [];
        foreach (CockpitPictureCommand c in n.Commands)
        {
            if (c.Id.Length > 0 && comparedCommands.TryGetValue(c.Id, out string? r))
                found.Add(r);
        }
        foreach (string id in n.ControlIds)
            found.AddRange(compared.Where(kv => kv.Key.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value));
        found = found.SelectMany(f => f.Split(", ")).Distinct().ToList();
        return found.Count == 0 ? string.Empty : T("In your profile: {0}", string.Join("; ", found.Take(6)));
    }

    // The Commands of "engine cockpits compare": every game command the profile binds for the variant's games.
    private void ApplyCompareCommands(System.Text.Json.JsonElement root)
    {
        comparedCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Commands", out System.Text.Json.JsonElement commands) && commands.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement c in commands.EnumerateArray())
            {
                string id = c.TryGetProperty("Id", out System.Text.Json.JsonElement i) ? i.GetString() ?? string.Empty : string.Empty;
                string name = c.TryGetProperty("Relation", out System.Text.Json.JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
                string input = c.TryGetProperty("Input", out System.Text.Json.JsonElement x) ? x.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0 || name.Length == 0)
                    continue;
                string text = input.Length > 0 ? name + " (" + input + ")" : name;
                comparedCommands[id] = comparedCommands.TryGetValue(id, out string? have) && !have.Contains(text, StringComparison.Ordinal) ? have + ", " + text : text;
            }
        }
        RefreshGuideRows();
    }

    private void ClearCompareCommands()
    {
        comparedCommands.Clear();
        RefreshGuideRows();
    }
}
