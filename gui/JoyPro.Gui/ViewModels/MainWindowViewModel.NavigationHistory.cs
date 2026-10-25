using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// Back and Forward in the header (docs/ui/navigation.md), apart from the profile's Undo: the places the user was (page,
// sub-view, open relation or device, search texts, open cards, scroll positions), Services/NavigationHistory.cs.
// A page change pushes the page that is left by itself; jumps inside a page ("Open both relations" on a Check card,
// a relation opened from the aircraft check or the assistant) push first. Alt+Left / Alt+Right and the mouse's back and
// forward buttons do the same as the header's arrows (MainWindow). The window captures and restores the scroll offsets
// (INavigationScrollHost); without a window (tests) the rest works the same.
public sealed partial class MainWindowViewModel
{
    public NavigationHistory Navigation { get; } = new();

    // The window: the scroll positions of the page's lists (null without a view).
    public INavigationScrollHost? NavigationScrollHost { get; set; }

    public bool CanNavigateBack => Navigation.CanGoBack;

    public bool CanNavigateForward => Navigation.CanGoForward;

    public string NavigateBackTip => Navigation.PeekBack is { } entry
        ? T("Back to {0} (Alt+Left or the mouse's back button)", DescribeNavigationPlace(entry))
        : T("Back (Alt+Left): nothing to go back to yet");

    public string NavigateForwardTip => Navigation.PeekForward is { } entry
        ? T("Forward to {0} (Alt+Right or the mouse's forward button)", DescribeNavigationPlace(entry))
        : T("Forward (Alt+Right): nothing to go forward to");

    private void InitializeNavigationHistory()
    {
        Navigation.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
            OnPropertyChanged(nameof(NavigateBackTip));
            OnPropertyChanged(nameof(NavigateForwardTip));
        };
    }

    // The page that is left goes into the history (before the new page shows: its scroll positions are still there).
    partial void OnCurrentStageChanging(UiStage value)
    {
        if (value != CurrentStage && !loadingUiSettings)
            RememberPlace();
    }

    // Layout: Device pictures / Cheat sheets is a place of its own.
    partial void OnIsCheatSheetsSectionChanging(bool value)
    {
        if (value != IsCheatSheetsSection && CurrentStage == UiStage.Layout)
            RememberPlace();
    }

    partial void OnFfbTelemetryTabChanging(int value)
    {
        if (value != FfbTelemetryTab && CurrentStage == UiStage.FfbTelemetry)
            RememberPlace();
    }

    // Called before a jump: the place now goes into the history (nothing while a place is restored).
    internal void RememberPlace()
    {
        if (Navigation.IsRestoring)
            return;
        Navigation.Push(CaptureNavigationPlace());
    }

    [RelayCommand]
    private void NavigateBack()
    {
        NavigationEntry? target = Navigation.GoBack(CaptureNavigationPlace());
        if (target != null)
            RestoreNavigationPlace(target);
    }

    [RelayCommand]
    private void NavigateForward()
    {
        NavigationEntry? target = Navigation.GoForward(CaptureNavigationPlace());
        if (target != null)
            RestoreNavigationPlace(target);
    }

    // The place as it is now.
    internal NavigationEntry CaptureNavigationPlace()
    {
        UiStage stage = CurrentStage;
        string subView = stage switch
        {
            UiStage.Layout => IsCheatSheetsSection ? "cheatsheets" : "pictures",
            UiStage.Relations => builderPanel.ToString().ToLowerInvariant(),
            UiStage.FfbTelemetry => "tab" + FfbTelemetryTab.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Empty
        };
        string selection = string.Empty;
        string label = string.Empty;
        if (stage == UiStage.Relations && SelectedRelation != null)
        {
            selection = SelectedRelation.Name;
            label = SelectedRelation.Name;
        }
        else if (stage == UiStage.Layout && !IsCheatSheetsSection && SelectedLayoutSuggestion != null)
        {
            selection = SelectedLayoutSuggestion.Device;
            label = SelectedLayoutSuggestion.DisplayName;
        }

        Dictionary<string, string> texts = new(StringComparer.Ordinal)
        {
            ["relations"] = RelationSearchText ?? string.Empty,
            ["binds"] = BindSearchText ?? string.Empty,
            ["check"] = CheckSearchText ?? string.Empty,
            ["check-kind"] = SelectedCheckKind?.Rule ?? string.Empty,
            ["check-sort"] = SelectedCheckSort?.Rule ?? string.Empty,
            ["settings"] = SettingsSearchText ?? string.Empty
        };

        Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
        foreach (CheckIssueSection section in CheckSections)
        {
            expanded["check-section:" + section.Kind] = section.IsExpanded;
            foreach (CheckIssueCard card in section.Cards)
                expanded[CheckCardKey(card)] = card.IsExpanded;
        }
        if (stage == UiStage.Settings)
        {
            foreach (SettingsSectionItem section in SettingsSections)
                expanded["settings:" + section.Id] = section.IsExpanded;
        }

        return new NavigationEntry
        {
            Stage = stage,
            SubView = subView,
            Selection = selection,
            Label = label,
            Texts = texts,
            Expanded = expanded,
            Scroll = NavigationScrollHost?.CaptureScroll() ?? new Dictionary<string, (double X, double Y)>()
        };
    }

    private static string CheckCardKey(CheckIssueCard card) => "check-card:" + card.Kind + "|" + card.Headline + "|" + card.RelationsSort;

    // Puts a place back: search texts, page, sub-view, selection, open cards; the window then scrolls the lists.
    internal void RestoreNavigationPlace(NavigationEntry entry)
    {
        using (Navigation.BeginRestore())
        {
            RestoreNavigationTexts(entry);
            if (CurrentStage != entry.Stage)
                CurrentStage = entry.Stage;
            RestoreNavigationSubView(entry);
            RestoreNavigationSelection(entry);
            RestoreNavigationExpanded(entry);
        }
        NavigationScrollHost?.RestoreScroll(entry.Scroll);
    }

    // Only the searches of the page that comes back (a search typed on another page since then stays).
    private void RestoreNavigationTexts(NavigationEntry entry)
    {
        string Text(string key) => entry.Texts.TryGetValue(key, out string? value) ? value : string.Empty;
        switch (entry.Stage)
        {
            case UiStage.Relations when !string.Equals(RelationSearchText, Text("relations"), StringComparison.Ordinal):
                RelationSearchText = Text("relations");
                break;
            case UiStage.Binds when !string.Equals(BindSearchText, Text("binds"), StringComparison.Ordinal):
                BindSearchText = Text("binds");
                break;
            case UiStage.Settings when !string.Equals(SettingsSearchText, Text("settings"), StringComparison.Ordinal):
                SettingsSearchText = Text("settings");
                break;
            case UiStage.Export:
                if (!string.Equals(CheckSearchText, Text("check"), StringComparison.Ordinal))
                    CheckSearchText = Text("check");
                string kind = Text("check-kind");
                if ((SelectedCheckKind?.Rule ?? string.Empty) != kind)
                    SelectedCheckKind = CheckKindChoices.FirstOrDefault(c => c.Rule == kind);
                string sort = Text("check-sort");
                if ((SelectedCheckSort?.Rule ?? string.Empty) != sort)
                    SelectedCheckSort = CheckSortChoices.FirstOrDefault(c => c.Rule == sort);
                break;
        }
    }

    private void RestoreNavigationSubView(NavigationEntry entry)
    {
        switch (entry.Stage)
        {
            case UiStage.Layout:
                IsCheatSheetsSection = entry.SubView == "cheatsheets";
                break;
            case UiStage.Relations when Enum.TryParse(entry.SubView, ignoreCase: true, out BuilderPanelKind panel) && panel != builderPanel:
                SetBuilderPanel(panel);
                break;
            case UiStage.FfbTelemetry when entry.SubView.StartsWith("tab", StringComparison.Ordinal) &&
                                           int.TryParse(entry.SubView[3..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int tab):
                FfbTelemetryTab = tab;
                break;
        }
    }

    private void RestoreNavigationSelection(NavigationEntry entry)
    {
        if (entry.Stage == UiStage.Relations)
        {
            if (entry.Selection.Length == 0)
            {
                if (SelectedRelation != null)
                    SelectedRelation = null;
                return;
            }
            if (string.Equals(SelectedRelation?.Name, entry.Selection, StringComparison.Ordinal))
                return;
            RelationListItem? relation = Relations.FirstOrDefault(r => string.Equals(r.Name, entry.Selection, StringComparison.Ordinal))
                                         ?? allRelations.FirstOrDefault(r => string.Equals(r.Name, entry.Selection, StringComparison.Ordinal));
            // A relation deleted since then: the page shows, the selection stays.
            if (relation != null)
                SelectedRelation = relation;
        }
        else if (entry.Stage == UiStage.Layout && entry.Selection.Length > 0 &&
                 !string.Equals(SelectedLayoutSuggestion?.Device, entry.Selection, StringComparison.Ordinal) &&
                 LayoutSuggestions.FirstOrDefault(d => string.Equals(d.Device, entry.Selection, StringComparison.Ordinal)) is { } device)
        {
            SelectedLayoutSuggestion = device;
        }
    }

    private void RestoreNavigationExpanded(NavigationEntry entry)
    {
        if (entry.Expanded.Count == 0 || entry.Stage is not (UiStage.Export or UiStage.Settings))
            return;
        IEnumerable<CheckIssueSection> sections = entry.Stage == UiStage.Export ? CheckSections : Array.Empty<CheckIssueSection>();
        foreach (CheckIssueSection section in sections)
        {
            if (entry.Expanded.TryGetValue("check-section:" + section.Kind, out bool open) && section.IsExpanded != open)
                section.IsExpanded = open;
            foreach (CheckIssueCard card in section.Cards)
            {
                if (entry.Expanded.TryGetValue(CheckCardKey(card), out bool cardOpen) && card.IsExpanded != cardOpen)
                    card.IsExpanded = cardOpen;
            }
        }
        if (entry.Stage == UiStage.Settings)
        {
            foreach (SettingsSectionItem section in SettingsSections)
            {
                if (entry.Expanded.TryGetValue("settings:" + section.Id, out bool open) && section.IsExpanded != open)
                    section.IsExpanded = open;
            }
        }
    }

    // "Export", "Relations: 'Pitch'", "Layout: Cheat sheets" (the header's tooltips).
    internal string DescribeNavigationPlace(NavigationEntry entry)
    {
        string page = NavPageDefaults.FirstOrDefault(d => d.Stage == entry.Stage).Title is { Length: > 0 } title ? T(title) : entry.Stage.ToString();
        if (entry.Label.Length > 0)
            return T("{0}: '{1}'", page, entry.Label);
        if (entry.Stage == UiStage.Layout && entry.SubView == "cheatsheets")
            return T("{0}: {1}", page, T("Cheat sheets"));
        return page;
    }
}
