using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// From a Check card into the relation editor and back (docs/ui/check.md):
//   - "Replace 'X' in 'relation' with...": the relation opens with Add actions searching for the command's name (without
//     its direction: "TACAN Channel (xx0x)" for "TACAN Channel (xx0x) - [Dec]") and its aircraft as the filter; the
//     ticked action takes the old one's place for those aircraft in one step (relations suggest apply --add --remove,
//     one Ctrl+Z);
//   - "Back to the check": a relation opened from a card shows a button that returns to the Check list at that card (the
//     same place as the header's Back arrow, Alt+Left).
public sealed partial class MainWindowViewModel
{
    // The action a Check card asked to replace: in this relation, this game's id for these aircraft.
    private sealed record BuilderReplaceRequest(string Relation, string Game, string Id, string Title, IReadOnlyList<string> Planes);

    private BuilderReplaceRequest? builderReplace;

    // The place a relation was opened from (a Check card, or another relation's search row), and that relation.
    private NavigationEntry? checkReturnPlace;
    private string checkReturnRelation = string.Empty;

    // The relation the search row's tag was in (empty: opened from the Check).
    private string returnFromRelation = string.Empty;

    public string ReturnToCheckLabel => returnFromRelation.Length > 0 ? T("Back to '{0}'", returnFromRelation) : T("Back to the check");

    public string ReturnToCheckTip => returnFromRelation.Length > 0
        ? T("Back to '{0}' with its search as it was (the same as the Back arrow at the top, Alt+Left)", returnFromRelation)
        : T("Back to the Check list at the card that opened this relation (the same as the Back arrow at the top, Alt+Left)");

    // A change made in the relation since it was opened from the Check: the check runs again on the way back.
    private bool checkChangedSinceOpen;

    public bool IsBuilderReplacing => builderReplace != null && SelectedRelation != null &&
                                      string.Equals(SelectedRelation.Name, builderReplace.Relation, StringComparison.OrdinalIgnoreCase);

    public string BuilderReplaceText => builderReplace == null ? string.Empty
        : T("Replacing '{0}' in '{1}' ({2}): tick the action that belongs there instead and press Replace.", builderReplace.Title, builderReplace.Relation,
            AircraftOrGameWords(builderReplace.Game, builderReplace.Planes));

    public bool CanReturnToCheck => checkReturnPlace != null && CurrentStage == UiStage.Relations && SelectedRelation != null &&
                                    string.Equals(SelectedRelation.Name, checkReturnRelation, StringComparison.OrdinalIgnoreCase);

    private string AircraftOrGameWords(string game, IReadOnlyList<string> planes) =>
        game.Equals("DCS", StringComparison.OrdinalIgnoreCase) && planes.Count > 0 ? AircraftWords(game, planes) : DisplayText.Game(game);

    // The relation editor's buttons and texts follow the open relation and page; another relation opened ends the way
    // back to the check (the header's Back still has it).
    private void OnCheckReturnStateChanged(bool relationChanged = false)
    {
        if (relationChanged && SelectedRelation != null && checkReturnPlace != null &&
            !string.Equals(SelectedRelation.Name, checkReturnRelation, StringComparison.OrdinalIgnoreCase))
        {
            checkReturnPlace = null;
            checkReturnRelation = string.Empty;
        }
        if (builderReplace != null && (SelectedRelation == null || !string.Equals(SelectedRelation.Name, builderReplace.Relation, StringComparison.OrdinalIgnoreCase)))
            builderReplace = null;
        OnPropertyChanged(nameof(IsBuilderReplacing));
        OnPropertyChanged(nameof(BuilderReplaceText));
        OnPropertyChanged(nameof(CanReturnToCheck));
        OnPropertyChanged(nameof(ReturnToCheckLabel));
        OnPropertyChanged(nameof(ReturnToCheckTip));
        OnPropertyChanged(nameof(BuilderAddButtonLabel));
    }

    // Opening a relation from a card remembers the Check list (OpenCheckRelationAsync).
    private void RememberCheckReturn(string relation, string fromRelation = "")
    {
        checkReturnPlace = Navigation.PeekBack;
        checkReturnRelation = relation;
        returnFromRelation = fromRelation;
        checkChangedSinceOpen = false;
        OnCheckReturnStateChanged();
    }

    // "Back to the check": the Check list at the card that opened the relation.
    [RelayCommand]
    internal async Task ReturnToCheckAsync()
    {
        NavigationEntry? place = checkReturnPlace;
        if (place == null)
            return;
        bool changed = checkChangedSinceOpen && returnFromRelation.Length == 0;
        returnFromRelation = string.Empty;
        checkReturnPlace = null;
        checkReturnRelation = string.Empty;
        builderReplace = null;
        if (ReferenceEquals(Navigation.PeekBack, place))
            NavigateBack();
        else
        {
            RememberPlace();
            RestoreNavigationPlace(place);
        }
        OnCheckReturnStateChanged();
        // The card may be gone now: the check reads the profile again.
        if (changed)
            await ValidateProfileAsync();
    }

    // The tag of a search row or a suggestion: one other relation opens at once, several are listed under the row (a name
    // opens it).
    [RelayCommand]
    internal async Task OpenOtherRelationsTagAsync(object? item)
    {
        if (item is not IOtherRelationsTag row || row.OtherRelations.Count == 0)
            return;
        if (row.HasSeveralOtherRelations)
        {
            row.IsOtherRelationsExpanded = !row.IsOtherRelationsExpanded;
            return;
        }
        await OpenRelationFromSearchAsync(row.OtherRelations[0]);
    }

    // Another relation that uses a search row's action opens; the search stays, and "Back to '<relation>'" (or the
    // header's Back) returns to the relation and search it came from.
    [RelayCommand]
    internal async Task OpenRelationFromSearchAsync(string? relation)
    {
        RelationListItem? item = allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.Ordinal)) ??
                                 allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            StatusText = T("The relation '{0}' is not there any more.", relation ?? string.Empty);
            return;
        }
        string from = SelectedRelation?.Name ?? string.Empty;
        RememberPlace();
        RememberCheckReturn(item.Name, from.Length > 0 ? from : T("the search"));
        suppressRelationAutoLoad = true;
        try
        {
            SelectedRelation = item;
        }
        finally
        {
            suppressRelationAutoLoad = false;
        }
        await ShowRelationAsync(item.Name);
        OnCheckReturnStateChanged();
        _ = LoadBuilderSuggestionsAsync(item.Name);
    }

    // "Replace 'X' in 'relation' with...": the relation opens (actions of the card marked), Add actions searches for the
    // command's name with its aircraft as the filter.
    internal async Task ReplaceCheckActionAsync(string relation, string game, string id, string title, IReadOnlyList<string> planes)
    {
        await OpenCheckRelationAsync(relation, game, [id], planes);
        if (SelectedRelation == null || !string.Equals(SelectedRelation.Name, relation, StringComparison.OrdinalIgnoreCase))
            return;
        builderReplace = new BuilderReplaceRequest(SelectedRelation.Name, game, id, title, planes.ToList());
        SetBuilderPanel(BuilderPanelKind.Add);
        BuilderSearchPlanes.Clear();
        if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string plane in planes.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                BuilderSearchPlanes.Add(plane);
        }
        OnPropertyChanged(nameof(HasBuilderSearchPlanes));
        RebuildBuilderPlaneOptions();
        OnCheckReturnStateChanged();
        string query = ReplaceSearchText(title);
        if (string.Equals(BuilderSearchText, query, StringComparison.Ordinal))
            await SearchBuilderActionsAsync();
        else
        {
            BuilderSearchText = query;
            await SearchBuilderActionsAsync();
        }
        StatusText = T("Pick the action that replaces '{0}' in '{1}'.", title, relation);
    }

    [RelayCommand]
    private void CancelBuilderReplace()
    {
        builderReplace = null;
        OnCheckReturnStateChanged();
    }

    // Replace mode: the ticked rows go in, the card's action leaves for its aircraft, in one engine call (one Ctrl+Z).
    private async Task<bool> ReplaceWithBuilderRowsAsync(List<BuilderSearchResultItem> chosen)
    {
        BuilderReplaceRequest? replace = builderReplace;
        if (replace == null)
            return false;
        if (chosen.Count == 0)
        {
            BuilderSearchStatus = T("Tick the action that takes the place of '{0}' first.", replace.Title);
            return false;
        }
        if (SelectedRelation != null && chosen.Any(c => c.IsAxis != SelectedRelation.IsAxis))
        {
            BuilderSearchStatus = T("'{0}' is {1}, or make a new relation for them (New relation).", SelectedRelation.Name, (SelectedRelation.IsAxis ? "an axis relation; untick the buttons" : "a button relation; untick the axes"));
            return false;
        }
        List<string> options = [];
        foreach (BuilderSearchResultItem item in chosen)
            options.AddRange(["--add", item.NodeSpec]);
        options.AddRange(["--remove", BuildNodeSpec(replace.Game, replace.Id, replace.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? string.Join(",", replace.Planes) : string.Empty)]);
        string picked = string.Join("', '", chosen.Select(c => c.DisplayTitle).Distinct(StringComparer.Ordinal));
        if (!await ApplyToBuilderRelationAsync(options, T("Replacing '{0}' in '{1}'...", replace.Title, replace.Relation),
                T("Replaced '{0}' with '{1}' in '{2}'.", replace.Title, picked, replace.Relation), fromSuggestions: false))
            return false;
        foreach (BuilderSearchResultItem item in chosen)
            item.IsChecked = false;
        builderReplace = null;
        checkChangedSinceOpen = true;
        OnCheckReturnStateChanged();
        return true;
    }

    private static readonly Regex TrailingDirectionTag = new(@"\s*-?\s*\[[^\]]*\]\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TrailingDirectionWord = new(
        @"\s*-?\s+(inc|dec|increase|decrease|up|down|left|right|cw|ccw|on|off|next|prev|previous|forward|fwd|aft|back|open|close|in|out|toggle|plus|minus|\+|-)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // What Add actions searches for to replace an action: its name without the direction or value at its end, so the
    // other directions come too ("TACAN Channel (xx0x) - [Dec]" -> "TACAN Channel (xx0x)": [Inc], [Next], 0..9).
    internal static string ReplaceSearchText(string title)
    {
        string text = (title ?? string.Empty).Trim();
        string cut = TrailingDirectionTag.Replace(text, string.Empty);
        if (cut == text)
            cut = TrailingDirectionWord.Replace(text, string.Empty);
        cut = cut.TrimEnd(' ', '-', ':').Trim();
        return cut.Length > 0 ? cut : text;
    }
}
