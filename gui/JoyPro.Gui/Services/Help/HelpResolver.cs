using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace JoyPro.Gui.Services.Help;

// Where a help text comes from: the control's wiki entry, the control's own words (tooltip, label, the list row's kind),
// the page (a click on an empty place), or nothing (the GUI test fails on that).
public enum HelpSource
{
    None,
    Page,
    Derived,
    Catalog
}

// What the help popup shows, already in the user's language.
public sealed record HelpTopic(string Title, string KindName, string What, string Fits, string Next, string WikiUrl, HelpSource Source,
    IReadOnlyList<HelpWord> Words, IReadOnlyList<HelpMenuEntry> MenuEntries, string Page, string Key)
{
    public bool HasWords => Words.Count > 0;

    public bool HasMenuEntries => MenuEntries.Count > 0;

    public bool HasNext => Next.Length > 0;

    // The plain first sentence (at most 12 words, HelpEntry.Easy / HelpPageText.Easy), empty: none; then What is first.
    public string Plain { get; init; } = string.Empty;

    public bool HasPlain => Plain.Length > 0;

    // The line the popup leads with: the plain sentence, else what it does.
    public string Lead => HasPlain ? Plain : What;

    // What it does in full, shown in the details when the plain sentence leads.
    public bool HasWhatDetail => HasPlain && What.Length > 0 && !string.Equals(What, Plain, StringComparison.Ordinal);

    public bool HasFits => Fits.Length > 0;
}

public sealed record HelpWord(string Word, string Explanation);

public sealed record HelpMenuEntry(string Title, string What);

// F1's help mode: finds the control under a click (the button, field, chip, list row, tab ... not the text inside it)
// and its help text. A control is found in the catalog by its command, its name, its label or its tooltip (in the
// language shown, so the label of the catalog is translated first); a control without an entry gets a text from its own
// words and the kind of control.
public static class HelpResolver
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> CommandProperties = new();
    private static readonly object LabelGate = new();
    private static string labelLanguage = string.Empty;
    private static Dictionary<string, List<HelpEntry>> byLabel = new(StringComparer.Ordinal);
    private static Dictionary<string, List<HelpEntry>> byTooltip = new(StringComparer.Ordinal);

    // The custom drawn controls (pictures, curves, canvases) a click can land on, with what they are.
    private static readonly Dictionary<string, string> DrawingTexts = new(StringComparer.Ordinal)
    {
        ["DeviceMarkerCanvas"] = N("The picture of your device with a marker on every button. Click a marker to see or change what that button does."),
        ["AxisCurveView"] = N("The curve of this axis: left is the stick at rest, right is the stick all the way. Drag the points to change it."),
        ["DetentCurveView"] = N("The throttle's range with its notches. Drag a notch to where your throttle clicks in."),
        ["AxisBandEditor"] = N("The parts of this axis's range. Drag the edges to change them."),
        ["FfbCurveEditor"] = N("How strong the force feedback is at each speed or angle. Drag the points to change it."),
        ["FfbGraphCanvas"] = N("The force feedback graph: how the game's data turns into force on your stick."),
        ["MultiViewCanvas"] = N("Several device pictures side by side. Drag one to move it."),
        ["CalloutSheetCanvas"] = N("The cheat sheet: your device with a table of what each button does. Drag a table to move it."),
        ["CockpitPlanView"] = N("A drawing of the real cockpit with its switches."),
        ["TurntableView"] = N("A 3D view of the device. Drag to turn it."),
        ["VirtualFunctionGraphView"] = N("The virtual function as a drawing: what goes in and what comes out."),
        ["VirtualFunctionFlowView"] = N("The virtual function's steps from your button to the virtual device."),
        ["TelemetryTimelineView"] = N("The recorded flight data over time. Drag to look at a moment."),
        ["ColorField"] = N("A colour. Click it to pick another colour."),
        ["FontPickerField"] = N("A font. Click it to pick another font."),
        ["UsageCharts"] = N("A chart of what you played most."),
        ["SchematicPainter"] = N("A drawing of your device."),
        ["LineArtImage"] = N("A drawing of your device."),
        ["DeviceThumbnail"] = N("A small picture of the device."),
        ["SheetStylePreview"] = N("A preview of how the cheat sheet looks."),
        ["ThemePreviewHost"] = N("A preview of the colours you picked."),
        ["ManualPageView"] = N("A page of the device's manual.")
    };

    // Controls without a wiki entry of their own, by name.
    private static readonly Dictionary<string, string> NamedTexts = new(StringComparer.Ordinal)
    {
        ["EngineOutputTextBox"] = N("What JoyPro's engine wrote during the last action. You can select and copy the text, for example for a problem report."),
        // The Virtual Joystick page's tabs (a tab's own text: "Shows another part of this page" says nothing).
        ["VirtualJoystickOverviewTab"] = N("Shows the steps and your games for the pretend controller."),
        ["VirtualJoystickMappingTab"] = N("Shows which stick button is which controller button."),
        ["VirtualJoystickFunctionsTab"] = N("Shows extra functions you build from your buttons."),
        ["VirtualJoystickLiveTestTab"] = N("Shows the pretend controller moving with your stick. Click Test, then move your stick.")
    };

    // ---- the control under a click ----

    // The control the help is about for a visual under the pointer: the closest help target above it, lifted to the
    // control whose template it belongs to (the arrow of a drop-down list is the list). Null: an empty place (the page)
    // or a scroll bar.
    public static Control? FindTarget(Visual? hit, Visual? stopAt = null)
    {
        for (Visual? v = hit; v != null && !ReferenceEquals(v, stopAt); v = v.GetVisualParent())
        {
            if (v is ScrollBar)
                return null;
            if (v is not Control control || !IsHelpTarget(control))
                continue;
            Control target = OwningField(control) ?? control;
            while (target.TemplatedParent is Control owner && !ReferenceEquals(owner, target))
            {
                if (owner is ScrollBar)
                    return null;
                if (!IsHelpTarget(owner))
                    break;
                target = owner;
            }
            return target;
        }
        return null;
    }

    public static bool IsHelpTarget(Control control)
    {
        switch (control)
        {
            case Button:
            case MenuItem:
            case TextBox:
            case ComboBox:
            case Slider:
            case NumericUpDown:
            case ListBoxItem:
            case TabItem:
            case TreeViewItem:
            case Expander:
            case AutoCompleteBox:
                return true;
            case Border border when border.Classes.Contains("chip"):
                return true;
        }
        return DrawingTexts.ContainsKey(control.GetType().Name);
    }

    // A help target that the walk over a page counts: shown, has a size, not a part of another control's template.
    public static bool IsStandaloneTarget(Control control)
    {
        if (!IsHelpTarget(control) || !control.IsEffectivelyVisible || control.Bounds.Width < 1 || control.Bounds.Height < 1)
            return false;
        if (OwningField(control) != null)
            return false;
        for (AvaloniaObject? owner = control.TemplatedParent; owner != null; owner = (owner as Control)?.TemplatedParent)
        {
            if (owner is ScrollBar || owner is Control c && IsHelpTarget(c))
                return false;
        }
        return true;
    }

    // A field JoyPro builds from other controls (the font and colour pickers): their list or box is part of them.
    private static Control? OwningField(Control control)
    {
        int level = 0;
        for (Visual? v = control.GetVisualParent(); v != null && level < 6; v = v.GetVisualParent(), level++)
        {
            if (v is Control c && c.GetType().Name is "FontPickerField" or "ColorField")
                return c;
        }
        return null;
    }

    public static string KindOf(Control control) => control switch
    {
        RadioButton => "RadioButton",
        CheckBox => "CheckBox",
        ToggleSwitch => "ToggleSwitch",
        ToggleButton => "ToggleButton",
        Button => "Button",
        MenuItem => "MenuItem",
        TextBox when control.GetType().Name.Contains("Number", StringComparison.Ordinal) => "NumberBox",
        TextBox => "TextBox",
        ComboBox => "ComboBox",
        Slider => "Slider",
        NumericUpDown => "NumberBox",
        ListBoxItem or TreeViewItem => "ListItem",
        TabItem => "Tab",
        Expander => "Expander",
        AutoCompleteBox => "TextBox",
        Border => "Chip",
        _ => "Drawing"
    };

    // ---- the text ----

    public static HelpTopic PageTopic(string page)
    {
        HelpPageText text = HelpCatalog.Page(page);
        return new HelpTopic(T(text.Title), T(HelpCatalog.KindName("Page")), T(text.What), T(text.Fits), T(text.Next), HelpCatalog.WikiUrl(text.Slug),
            HelpSource.Page, Words(text.What + " " + text.Fits), [], text.Slug, "page:" + text.Slug) { Plain = text.Easy.Length > 0 ? T(text.Easy) : string.Empty };
    }

    // The help of a control; page: the page it is on ("header" for the bars, the open page otherwise).
    public static HelpTopic Resolve(Control control, string page)
    {
        string kind = KindOf(control);
        // The bottom bar's page buttons are page buttons, not on/off switches.
        if (control.Name?.StartsWith("Nav_", StringComparison.Ordinal) == true)
            kind = "PageButton";
        HelpEntry? entry = Match(control, page);
        string shown = ShownText(control);
        string tip = TooltipText(control);
        if (entry != null)
        {
            // Why it matters: the page the control is on (a component card on the Virtual Joystick page is about that page,
            // even though its entry is written on Settings).
            HelpPageText fits = HelpCatalog.Page(FitsPageOf(control, page));
            string title = FirstText(shown, T(entry.Label), tip, T(HelpCatalog.KindName(kind)));
            // A text not translated yet into the language shown gives way to one that is (the control's tooltip, the
            // page's next step), so the popup never mixes languages.
            string next = entry.Next.Length > 0 ? Translated(entry.Next, T(fits.Next)) : T(fits.Next);
            string whatText = Translated(entry.What, tip.Length > 0 ? EndSentence(tip) : string.Empty);
            return new HelpTopic(title, T(HelpCatalog.KindName(kind)), whatText, T(fits.Fits), next, HelpCatalog.WikiUrl(entry.Page, entry.Anchor),
                HelpSource.Catalog, Words(entry.What + " " + entry.Next), MenuEntries(control, page), entry.Page, entry.Key)
            {
                Plain = entry.Easy.Length > 0 ? Translated(entry.Easy, string.Empty) : string.Empty
            };
        }

        HelpPageText pageText = HelpCatalog.Page(FitsPageOf(control, page));
        string what = DerivedWhat(control, kind, shown, tip, out string hint);
        HelpSource source = hint.Length > 0 ? HelpSource.Derived : HelpSource.None;
        string titleText = FirstText(hint, T(HelpCatalog.KindName(kind)));
        return new HelpTopic(Trim(titleText, 90), T(HelpCatalog.KindName(kind)), what, T(pageText.Fits), T(pageText.Next), HelpCatalog.WikiUrl(pageText.Slug),
            source, [], MenuEntries(control, page), pageText.Slug, kind + ":" + hint);
    }

    private static string DerivedWhat(Control control, string kind, string shown, string tip, out string hint)
    {
        string kindWhat = T(HelpCatalog.KindWhat(kind));
        if (kind == "ListItem" && control.DataContext != null && HelpCatalog.RowTexts.TryGetValue(control.DataContext.GetType().Name, out string? row))
        {
            hint = FirstText(shown, control.DataContext.GetType().Name);
            return T(row);
        }
        if (kind == "Drawing" && DrawingTexts.TryGetValue(control.GetType().Name, out string? drawing))
        {
            hint = T(HelpCatalog.KindName(kind));
            return T(drawing);
        }
        if (control.Name is { } name && NamedTexts.TryGetValue(name, out string? named))
        {
            // A tab is called by its header ("Live test"), other named controls by their kind.
            hint = control is TabItem ? FirstText(shown, T(HelpCatalog.KindName(kind))) : T(HelpCatalog.KindName(kind));
            return T(named);
        }
        string label = NearbyLabel(control);
        if (label.Length == 0 && control.FindAncestorOfType<DataGridCell>() is { } cell && DataGridColumn.GetColumnContainingElement(cell) is { Header: string header })
            label = header;
        string watermark = control is TextBox box ? box.Watermark ?? string.Empty : control is ComboBox combo ? combo.PlaceholderText ?? string.Empty : string.Empty;
        string automation = AutomationProperties.GetName(control) ?? string.Empty;
        hint = FirstText(shown, label, automation, watermark, tip);
        List<string> parts = [];
        if (tip.Length > 0 && !string.Equals(tip, hint, StringComparison.Ordinal))
            parts.Add(EndSentence(tip));
        else if (watermark.Length > 0 && !string.Equals(watermark, hint, StringComparison.Ordinal))
            parts.Add(EndSentence(watermark));
        parts.Add(kindWhat);
        if (tip.Length > 0 && string.Equals(tip, hint, StringComparison.Ordinal))
            parts.Insert(0, EndSentence(tip));
        return string.Join(" ", parts.Distinct());
    }

    // The page whose bigger picture fits a control: a page button of the bottom bar is about its page.
    private static string FitsPageOf(Control control, string page)
    {
        string? name = control.Name;
        if (name != null && name.StartsWith("Nav_", StringComparison.Ordinal))
            return name[4..] == "check" ? "check" : name[4..];
        if (name == "EngineOutputQuickToggle")
            return "output";
        // The help buttons at the top belong to the help, not to the profile bar they sit in.
        if (name is "HelpButton" or "PageTourButton" or "HelpModeEndButton")
            return "help";
        return page;
    }

    // ---- the catalog entry of a control ----

    public static HelpEntry? Match(Control control, string page)
    {
        IReadOnlyList<HelpEntry> entries = HelpCatalog.Entries;
        if (entries.Count == 0)
            return null;
        string? command = CommandName(control);
        if (command != null && Pick(entries.Where(e => e.Command == command), page) is { } byCommand)
            return byCommand;
        if (!string.IsNullOrEmpty(control.Name) && Pick(entries.Where(e => e.Name == control.Name), page) is { } byName)
            return byName;
        EnsureLabelIndex();
        string shown = ShownText(control);
        string tip = TooltipText(control);
        List<HelpEntry>? labelled = null, tipped = null;
        if (shown.Length > 0)
            byLabel.TryGetValue(shown, out labelled);
        if (tip.Length > 0)
            byTooltip.TryGetValue(tip, out tipped);
        if (labelled != null && tipped != null && Pick(labelled.Intersect(tipped), page) is { } both)
            return both;
        if (labelled != null && Pick(labelled, page) is { } byLabelEntry)
            return byLabelEntry;
        if (tipped != null && Pick(tipped, page) is { } byTip)
            return byTip;
        return null;
    }

    private static HelpEntry? Pick(IEnumerable<HelpEntry> candidates, string page)
    {
        List<HelpEntry> list = candidates.ToList();
        return list.FirstOrDefault(e => e.Page == page) ?? list.FirstOrDefault();
    }

    private static void EnsureLabelIndex()
    {
        lock (LabelGate)
        {
            string language = Localization.Loc.Current.Code;
            if (language == labelLanguage && byLabel.Count > 0)
                return;
            Dictionary<string, List<HelpEntry>> labels = new(StringComparer.Ordinal);
            Dictionary<string, List<HelpEntry>> tips = new(StringComparer.Ordinal);
            foreach (HelpEntry e in HelpCatalog.Entries)
            {
                if (e.Label.Length > 0 && !e.Label.Contains('{'))
                    Add(labels, Clean(T(e.Label)), e);
                if (e.Tooltip.Length > 0 && !e.Tooltip.Contains('{'))
                    Add(tips, Clean(T(e.Tooltip)), e);
            }
            byLabel = labels;
            byTooltip = tips;
            labelLanguage = language;
        }

        static void Add(Dictionary<string, List<HelpEntry>> map, string key, HelpEntry e)
        {
            if (key.Length == 0)
                return;
            if (!map.TryGetValue(key, out List<HelpEntry>? list))
                map[key] = list = [];
            list.Add(e);
        }
    }

    // The name of the view model's command property behind a button or menu entry (the command object is compared with
    // the ICommand properties of the DataContexts above the control).
    public static string? CommandName(Control control)
    {
        ICommand? command = control switch
        {
            Button button => button.Command,
            MenuItem item => item.Command,
            _ => null
        };
        if (command == null)
            return null;
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        foreach (object? context in Contexts(control))
        {
            if (context == null || !seen.Add(context))
                continue;
            if (CommandMaps.GetValue(context, BuildCommandMap).TryGetValue(command, out string? name))
                return name;
        }
        return null;
    }

    private static IEnumerable<object?> Contexts(Control control)
    {
        yield return control.DataContext;
        foreach (Visual v in control.GetVisualAncestors())
            if (v is StyledElement element)
                yield return element.DataContext;
        foreach (ILogical l in control.GetLogicalAncestors())
            if (l is StyledElement element)
                yield return element.DataContext;
        yield return TopLevel.GetTopLevel(control)?.DataContext;
    }

    // Per view model object: its command objects -> property names (made once; the generated commands are cached by the
    // view model, so the objects stay the same).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<ICommand, string>> CommandMaps = new();

    private static Dictionary<ICommand, string> BuildCommandMap(object context)
    {
        Dictionary<ICommand, string> map = new(ReferenceEqualityComparer.Instance);
        foreach (PropertyInfo property in CommandProperties.GetOrAdd(context.GetType(), FindCommandProperties))
        {
            try
            {
                if (property.GetValue(context) is ICommand value)
                    map.TryAdd(value, property.Name);
            }
            catch (Exception)
            {
            }
        }
        return map;
    }

    private static PropertyInfo[] FindCommandProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0)
            .ToArray();

    // ---- the words of a control ----

    // What the control shows as its name: its content or header text, or the first text inside it.
    public static string ShownText(Control control)
    {
        object? content = control switch
        {
            ToggleSwitch toggle => toggle.Content ?? toggle.OnContent,
            HeaderedSelectingItemsControl headered => headered.Header,
            HeaderedContentControl headered => headered.Header,
            ContentControl contentControl => contentControl.Content,
            _ => null
        };
        if (content is string text)
            return Clean(text);
        if (content is TextBlock block)
            return Clean(block.Text ?? string.Empty);
        if (content is Control inner)
            return FirstTextIn(inner);
        if (control is ListBoxItem or TreeViewItem or Border or TabItem)
            return FirstTextIn(control);
        return string.Empty;
    }

    public static string TooltipText(Control control)
    {
        object? tip = ToolTip.GetTip(control);
        return tip switch
        {
            string s => Clean(s),
            Control c => Clean(string.Join(" ", c.GetLogicalDescendants().Prepend(c).OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)))),
            _ => string.Empty
        };
    }

    private static string FirstTextIn(Control control)
    {
        foreach (TextBlock block in control.GetLogicalDescendants().Prepend(control).OfType<TextBlock>())
        {
            string text = Clean(block.Text ?? string.Empty);
            if (text.Length > 0 && block.IsVisible)
                return text;
        }
        foreach (Visual v in control.GetVisualDescendants())
        {
            if (v is TextBlock block && block.IsVisible && Clean(block.Text ?? string.Empty) is { Length: > 0 } text)
                return text;
        }
        return string.Empty;
    }

    // The text label just before a field (the TextBlock before it in the same panel, also one level up).
    private static string NearbyLabel(Control control)
    {
        Control current = control;
        for (int level = 0; level < 3; level++)
        {
            if (current.GetVisualParent() is not Panel panel)
            {
                if (current.GetVisualParent() is Control up)
                {
                    current = up;
                    continue;
                }
                break;
            }
            int index = panel.Children.IndexOf(current);
            int row = Grid.GetRow(current);
            for (int i = index - 1; i >= 0; i--)
            {
                Control sibling = panel.Children[i];
                if (!sibling.IsVisible)
                    continue;
                if (panel is Grid && Grid.GetRow(sibling) != row)
                    continue;
                string text = sibling is TextBlock block ? Clean(block.Text ?? string.Empty) : string.Empty;
                if (text.Length > 0)
                    return Trim(text.TrimEnd(':'), 80);
            }
            current = panel;
        }
        return string.Empty;
    }

    private static IReadOnlyList<HelpMenuEntry> MenuEntries(Control control, string page)
    {
        if (control is not Button { Flyout: MenuFlyout flyout })
            return [];
        List<HelpMenuEntry> list = [];
        foreach (MenuItem item in flyout.Items.OfType<MenuItem>())
        {
            string header = item.Header as string ?? string.Empty;
            if (header.Length == 0)
                continue;
            string what = string.Empty;
            HelpEntry? entry = HelpCatalog.Entries.FirstOrDefault(e => e.Kind == "MenuItem" && e.Label.Length > 0 && Clean(T(e.Label)) == Clean(header) && e.Page == page)
                ?? HelpCatalog.Entries.FirstOrDefault(e => e.Kind == "MenuItem" && e.Label.Length > 0 && Clean(T(e.Label)) == Clean(header));
            if (entry != null)
                what = T(entry.What);
            else if (TooltipText(item) is { Length: > 0 } tip)
                what = EndSentence(tip);
            list.Add(new HelpMenuEntry(Clean(header), what));
        }
        return list;
    }

    private static IReadOnlyList<HelpWord> Words(string english) =>
        HelpCatalog.WordsIn(english).Select(w => new HelpWord(T(w.Word), T(w.Explanation))).ToList();

    // ---- helpers ----

    private static string Translated(string english, string fallback)
    {
        string text = T(english);
        if (fallback.Length > 0 && !ReferenceEquals(Localization.Loc.Current, Localization.Languages.English) &&
            string.Equals(text, english, StringComparison.Ordinal))
            return fallback;
        return text;
    }

    private static string Clean(string text) =>
        string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string FirstText(params string[] texts) => texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim() ?? string.Empty;

    private static string EndSentence(string text)
    {
        text = text.Trim();
        return text.Length == 0 || ".!?:".Contains(text[^1]) ? text : text + ".";
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
