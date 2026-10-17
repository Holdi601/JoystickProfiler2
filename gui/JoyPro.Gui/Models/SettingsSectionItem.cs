using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One section of the Settings page (Views/Controls/SettingsSection.cs): open or closed, where it stands (the user drags
// sections and their tabs around) and whether it matches the settings search. The order and the open sections are kept
// in ui-settings.json (settingsSectionOrder, settingsSectionsOpen).
public sealed partial class SettingsSectionItem : ObservableObject
{
    public SettingsSectionItem(string id, string title, string keywords, string? tabTitle = null)
    {
        Id = id;
        Title = title;
        Keywords = keywords;
        TabTitle = string.IsNullOrWhiteSpace(tabTitle) ? title : tabTitle;
    }

    public string Id { get; }

    public string Title { get; }

    // The short name on the section's tab at the top of the page.
    public string TabTitle { get; }

    // Words the search finds the section by besides its title and the texts on it (synonyms, setting names).
    public string Keywords { get; }

    // The texts of the section's settings, read from the page once it is built (labels, check boxes, buttons, tips).
    public string ViewText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(ToggleTip))]
    private bool isExpanded;

    // The settings search shows the matching sections open (without changing what the user opened).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool openedBySearch;

    [ObservableProperty]
    private bool matchesSearch = true;

    public bool IsContentVisible => IsExpanded || OpenedBySearch;

    public string ToggleTip => IsExpanded ? T("Close this section") : T("Open this section");

    public bool Matches(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return true;
        // English and the language shown (the page's texts are read in it).
        string text = Title + " " + JoyPro.Gui.Localization.Loc.T(Title) + " " + JoyPro.Gui.Localization.Loc.T(TabTitle) + " " + Keywords + " " + ViewText;
        return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    public override string ToString() => Title;
}

// The order of the sections: the user's order, with sections that are new in this version put where they stand in
// JoyPro's own order (after the section they follow there), and sections that no longer exist left out.
public static class SettingsSectionOrder
{
    public static List<string> Merge(IReadOnlyList<string> defaults, IReadOnlyList<string>? saved)
    {
        HashSet<string> known = new(defaults, StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        foreach (string id in saved ?? [])
        {
            string? match = defaults.FirstOrDefault(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
            if (match != null && !order.Contains(match, StringComparer.OrdinalIgnoreCase))
                order.Add(match);
        }
        for (int i = 0; i < defaults.Count; i++)
        {
            string id = defaults[i];
            if (order.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue;
            // After the nearest section before it in JoyPro's order, or first.
            int at = 0;
            for (int j = i - 1; j >= 0; j--)
            {
                int index = order.FindIndex(o => string.Equals(o, defaults[j], StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    at = index + 1;
                    break;
                }
            }
            order.Insert(at, id);
        }
        return order.Where(known.Contains).ToList();
    }

    // The search text as words (every word must be found).
    public static List<string> Words(string? search) =>
        (search ?? string.Empty).Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
