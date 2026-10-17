using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The Relations page (docs/relation-builder.md), JoyPro 1 style: the relation list on the left; on the right the open
// relation (name edited in place, its actions with game, aircraft, title, kind and remove) and one of three panels:
//   Add actions        one search over every game (engine relations search, synonyms from the engine), rows with tick
//                      boxes grouped by game, filter chips (games, aircraft, axis/button); "Add selected" puts every
//                      ticked row into the relation, or creates the new relation with them.
//   Suggested          what JoyPro's function catalog says fits the open relation (engine relations suggest), with the
//                      reason: one-click add, add all, dismiss.
//   JoyPro assistant   a sentence changes relations directly (engine relations assistant): the local AI model works
//                      through tools; without it the offline brain understands simple commands. One Undo per answer.
// Binding stays on the Binds page.
public sealed partial class MainWindowViewModel
{
    private enum BuilderPanelKind
    {
        Add,
        Suggested,
        Assistant,
        Catalog,
        MissingAll
    }

    private BuilderPanelKind builderPanel = BuilderPanelKind.Add;

    // While the editor reloads the relation itself (after an add), the selection hook does not load it a second time.
    private bool suppressRelationAutoLoad;

    public bool IsBuilderAddPanel => builderPanel == BuilderPanelKind.Add || (builderPanel == BuilderPanelKind.Suggested && SelectedRelation == null);

    public bool IsBuilderSuggestedPanel => builderPanel == BuilderPanelKind.Suggested && SelectedRelation != null;

    public bool IsBuilderAssistantPanel => builderPanel == BuilderPanelKind.Assistant;

    // The catalog browser: every action of a game or aircraft by its categories (MainWindowViewModel.Catalog.cs).
    public bool IsBuilderCatalogPanel => builderPanel == BuilderPanelKind.Catalog;

    private void SetBuilderPanel(BuilderPanelKind panel)
    {
        builderPanel = panel;
        OnBuilderLayoutChanged();
    }

    [RelayCommand]
    private void ShowBuilderFind() => SetBuilderPanel(BuilderPanelKind.Add);

    [RelayCommand]
    private void ShowBuilderSuggested() => SetBuilderPanel(BuilderPanelKind.Suggested);

    [RelayCommand]
    private void ShowBuilderAssistant() => SetBuilderPanel(BuilderPanelKind.Assistant);

    // Without an open relation the editor shows the new relation's name box, or how to start.
    public bool ShowNewRelationCard => ShowNewRelationForm && SelectedRelation == null;

    public bool ShowStartSmall => !ShowNewRelationForm && SelectedRelation == null;

    public bool HasBuilderNoNodes => HasRelationSelected && !HasRelationNodes;

    partial void OnShowNewRelationFormChanged(bool value) => OnBuilderLayoutChanged();

    private void OnBuilderLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowNewRelationCard));
        OnPropertyChanged(nameof(ShowStartSmall));
        OnPropertyChanged(nameof(IsBuilderAddPanel));
        OnPropertyChanged(nameof(IsBuilderSuggestedPanel));
        OnPropertyChanged(nameof(IsBuilderAssistantPanel));
        OnPropertyChanged(nameof(IsBuilderCatalogPanel));
        OnPropertyChanged(nameof(IsBuilderMissingAllPanel));
        OnPropertyChanged(nameof(BuilderAddButtonLabel));
        OnPropertyChanged(nameof(CatalogAddLabel));
        OnPropertyChanged(nameof(BuilderSearchHint));
        OnPropertyChanged(nameof(CanChangeBuilderKind));
        OnPropertyChanged(nameof(BuilderKindHint));
        OnAssistantModeChanged();
    }

    // The selected relation changed (MainWindowViewModel.OnSelectedRelationChanged).
    private void BuilderFollowSelection(RelationListItem? value)
    {
        BuilderRenameText = value?.Name ?? string.Empty;
        if (!string.Equals(builderLastSearchKind, BuilderEffectiveKind, StringComparison.Ordinal))
            _ = RefreshBuilderSearchForKindAsync();
        else
            OnBuilderKindChanged();
        if (value == null)
        {
            BuilderRelationName = string.Empty;
            ClearBuilderSuggestions();
            MarkBuilderRowsInRelation();
            return;
        }
        if (suppressRelationAutoLoad || !IsRelationsStage || IsAnyRelationToolOpen)
            return;
        ShowNewRelationForm = false;
        SeedBuilderSearch(value.Name);
        _ = LoadBuilderSuggestionsAsync(value.Name);
        _ = EnsureBuilderLlmStatusAsync();
    }

    // An opened relation: Add actions searches for its name ("Pitch"), not for what the last relation was ("gear");
    // a copy number ("Pitch (2)") is left out. The search runs as typed (after the usual pause).
    private void SeedBuilderSearch(string relationName)
    {
        string query = System.Text.RegularExpressions.Regex.Replace(relationName ?? string.Empty, @"\s*\(\d+\)\s*$", string.Empty).Trim();
        if (query.Length > 0 && !string.Equals((BuilderSearchText ?? string.Empty).Trim(), query, StringComparison.Ordinal))
            BuilderSearchText = query;
    }

    // Coming back to the Relations page or closing a tool: the open relation's suggestions are loaded if they are not yet.
    private void BuilderOnScreen()
    {
        if (SelectedRelation != null && IsRelationsStage && !IsAnyRelationToolOpen && !string.Equals(BuilderRelationName, SelectedRelation.Name, StringComparison.Ordinal))
            _ = LoadBuilderSuggestionsAsync(SelectedRelation.Name);
        // Actions with aircraft where the same id means something else (a notice with one-step repair).
        if (IsRelationsStage)
            _ = RefreshAircraftCheckAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The open relation: rename in place
    // ---------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    private string builderRenameText = string.Empty;

    // Enter or leaving the name box: engine relations rename (the bind stays with it).
    [RelayCommand]
    internal async Task CommitBuilderRenameAsync()
    {
        RelationListItem? relation = SelectedRelation;
        string wanted = (BuilderRenameText ?? string.Empty).Trim();
        if (relation == null || wanted.Length == 0 || string.Equals(wanted, relation.Name, StringComparison.Ordinal))
        {
            if (relation != null && wanted.Length == 0)
                BuilderRenameText = relation.Name;
            return;
        }
        if (allRelations.Any(r => !ReferenceEquals(r, relation) && string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = T("A relation named '{0}' exists already.", wanted);
            BuilderRenameText = relation.Name;
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["relations", "rename", relation.Name, wanted], includeProfile: true, mutatesProfile: true,
            busyLabel: "Renaming '" + relation.Name + "'...");
        if (result == null || result.ExitCode != 0)
        {
            BuilderRenameText = relation.Name;
            return;
        }
        string old = relation.Name;
        await OpenBuilderRelationAsync(wanted, seedSearch: false);
        await RefreshBindsAsync();
        StatusText = T("'{0}' is now '{1}'. Ctrl+Z undoes it.", old, wanted);
    }

    [RelayCommand]
    private void CancelBuilderRename() => BuilderRenameText = SelectedRelation?.Name ?? string.Empty;

    // ---------------------------------------------------------------------------------------------------------
    // Suggested for this relation
    // ---------------------------------------------------------------------------------------------------------

    private readonly List<BuilderSuggestionItem> allBuilderSuggestions = [];
    private int builderSuggestVersion;
    private bool builderLlmStatusRequested;

    public ObservableCollection<BuilderSuggestionItem> BuilderSuggestions { get; } = [];

    public ObservableCollection<BuilderGameChip> BuilderSuggestionGames { get; } = [];

    public ObservableCollection<BuilderDismissedItem> BuilderDismissed { get; } = [];

    [ObservableProperty]
    private string builderRelationName = string.Empty;

    [ObservableProperty]
    private string builderFunctionText = string.Empty;

    [ObservableProperty]
    private string builderSuggestStatus = string.Empty;

    [ObservableProperty]
    private bool isBuilderSuggestLoading;

    // Weak matches (similar titles only) are hidden until asked for.
    [ObservableProperty]
    private bool builderShowWeakMatches;

    public bool HasBuilderSuggestions => BuilderSuggestions.Count > 0;

    public bool ShowBuilderSuggestEmpty => HasRelationSelected && !IsBuilderSuggestLoading && BuilderSuggestions.Count == 0;

    public bool HasBuilderSuggestionGames => BuilderSuggestionGames.Count > 1;

    // "Add all": every row shown that no other relation uses.
    public int BuilderAddAllCount => BuilderSuggestions.Count(s => !s.HasConflict);

    public string BuilderAddAllLabel => T("Add all ({0})", BuilderAddAllCount.ToString(CultureInfo.InvariantCulture));

    public bool CanAddAllSuggestions => BuilderAddAllCount > 0;

    public bool HasBuilderDismissed => BuilderDismissed.Count > 0;

    public string BuilderDismissedHeader => T("Dismissed for this relation ({0})", BuilderDismissed.Count.ToString(CultureInfo.InvariantCulture));

    public bool HasBuilderFunction => BuilderFunctionText.Length > 0;

    public int BuilderWeakCount => allBuilderSuggestions.Count(s => s.IsLow);

    public string BuilderWeakLabel => T("Also weak matches ({0})", BuilderWeakCount.ToString(CultureInfo.InvariantCulture));

    public string BuilderSuggestedTabLabel => T("Suggested for this relation{0}", (allBuilderSuggestions.Count > 0 ? " (" + BuilderSuggestions.Count.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty));

    // The empty state of the panel: why nothing is suggested, and what to do.
    public string BuilderSuggestEmptyText =>
        !HasRelationNodes
            ? T("This relation has no actions yet. Add the first ones under Add actions; then JoyPro suggests the same function in your other games and aircraft.")
            : allBuilderSuggestions.Count > 0
                ? T("Nothing fits with these filters. Tick another game or show the weak matches.")
                : BuilderFunctionText.Length > 0
                    ? T("Everything JoyPro knows for this function is in the relation. Add more by hand under Add actions.")
                    : T("JoyPro does not know this function yet. Add the same action of another game under Add actions, then it can suggest more.");

    partial void OnIsBuilderSuggestLoadingChanged(bool value) => OnBuilderSuggestionsChanged();

    partial void OnBuilderFunctionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasBuilderFunction));
        OnPropertyChanged(nameof(BuilderSuggestEmptyText));
    }

    partial void OnBuilderShowWeakMatchesChanged(bool value) => ApplyBuilderSuggestionFilter();

    private void OnBuilderSuggestionsChanged()
    {
        OnPropertyChanged(nameof(HasBuilderSuggestions));
        OnPropertyChanged(nameof(ShowBuilderSuggestEmpty));
        OnPropertyChanged(nameof(HasBuilderSuggestionGames));
        OnPropertyChanged(nameof(BuilderAddAllCount));
        OnPropertyChanged(nameof(BuilderAddAllLabel));
        OnPropertyChanged(nameof(CanAddAllSuggestions));
        OnPropertyChanged(nameof(BuilderSuggestEmptyText));
        OnPropertyChanged(nameof(HasBuilderDismissed));
        OnPropertyChanged(nameof(BuilderDismissedHeader));
        OnPropertyChanged(nameof(BuilderWeakCount));
        OnPropertyChanged(nameof(BuilderWeakLabel));
        OnPropertyChanged(nameof(HasBuilderNoNodes));
        OnPropertyChanged(nameof(BuilderSuggestedTabLabel));
        OnBuilderSuggestionTicksChanged();
    }

    private void ClearBuilderSuggestions()
    {
        allBuilderSuggestions.Clear();
        BuilderSuggestions.Clear();
        BuilderSuggestionGames.Clear();
        BuilderDismissed.Clear();
        RelationMisfits.Clear();
        BuilderFunctionText = string.Empty;
        BuilderSuggestStatus = string.Empty;
        OnPropertyChanged(nameof(HasRelationMisfits));
        OnBuilderSuggestionsChanged();
    }

    internal async Task LoadBuilderSuggestionsAsync(string relation)
    {
        int version = ++builderSuggestVersion;
        BuilderRelationName = relation;
        IsBuilderSuggestLoading = true;
        try
        {
            EngineCommandResult? result = await RunEngineAsync(["relations", "suggest", relation, "--min", "low"], includeProfile: true, mutatesProfile: false,
                busyLabel: "Finding suggestions for '" + relation + "'...", forceText: false, background: true);
            if (version != builderSuggestVersion)
                return;
            allBuilderSuggestions.Clear();
            BuilderDismissed.Clear();
            RelationMisfits.Clear();
            BuilderFunctionText = string.Empty;
            if (result == null)
                return;
            if (result.ExitCode != 0)
            {
                BuilderSuggestStatus = result.StandardError.Trim();
                return;
            }
            ParseBuilderSuggestions(result.StandardOutput);
        }
        finally
        {
            if (version == builderSuggestVersion)
            {
                IsBuilderSuggestLoading = false;
                RebuildBuilderGameChips();
                ApplyBuilderSuggestionFilter();
                OnPropertyChanged(nameof(HasRelationMisfits));
            }
        }
        _ = LoadBuilderSuggestionPicturesAsync(version);
    }

    private void ParseBuilderSuggestions(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            List<string> functions = [];
            if (root.TryGetProperty("Functions", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement function in list.EnumerateArray())
                {
                    double weight = function.TryGetProperty("Weight", out JsonElement w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 1;
                    string name = ReadJsonString(function, "Name");
                    if (name.Length > 0)
                        functions.Add(weight >= 0.99 ? name : name + " (" + Math.Round(weight * 100).ToString(CultureInfo.InvariantCulture) + "%)");
                }
            }
            if (functions.Count == 0 && ReadJsonString(root, "SlotName") is { Length: > 0 } slot)
                functions.Add(slot);
            BuilderFunctionText = string.Join(" · ", functions);
            if (root.TryGetProperty("Suggestions", out JsonElement suggestions) && suggestions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in suggestions.EnumerateArray())
                {
                    int score = item.TryGetProperty("Score", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
                    allBuilderSuggestions.Add(new BuilderSuggestionItem(
                        ReadJsonString(item, "Game"), ReadJsonString(item, "Id"), ReadJsonString(item, "Title"), ReadStringArray(item, "Planes"),
                        ReadJsonString(item, "Confidence"), score, ReadJsonString(item, "Why"), ReadStringArray(item, "Reasons"), ReadStringArray(item, "InOtherRelations"))
                    {
                        MeaningNote = ReadJsonString(item, "MeaningNote"),
                        Missing = ReadJsonString(item, "Missing"),
                        NewPlanes = ReadStringArray(item, "NewPlanes")
                    });
                }
            }
            if (root.TryGetProperty("Dismissed", out JsonElement dismissed) && dismissed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dismissed.EnumerateArray())
                    BuilderDismissed.Add(new BuilderDismissedItem(ReadJsonString(item, "Game"), ReadJsonString(item, "Id"), ReadJsonString(item, "Title"), ReadStringArray(item, "Planes")));
            }
            if (root.TryGetProperty("Misfits", out JsonElement misfits) && misfits.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in misfits.EnumerateArray())
                {
                    RelationMisfits.Add(new RelationMisfitItem(ReadJsonString(item, "Game"), ReadJsonString(item, "Id"), ReadJsonString(item, "Title"),
                        ReadStringArray(item, "Planes"), ReadJsonString(item, "Reason"), ReadJsonString(item, "MoveTo")));
                }
            }
        }
        catch (JsonException)
        {
            BuilderSuggestStatus = T("Could not read the suggestions.");
        }
    }

    private void RebuildBuilderGameChips()
    {
        HashSet<string> ticked = BuilderSuggestionGames.Where(c => c.IsSelected).Select(c => c.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BuilderGameChip chip in BuilderSuggestionGames)
            chip.PropertyChanged -= OnBuilderGameChipChanged;
        BuilderSuggestionGames.Clear();
        foreach (IGrouping<string, BuilderSuggestionItem> game in allBuilderSuggestions.Where(s => BuilderShowWeakMatches || !s.IsLow)
                     .GroupBy(s => s.Game, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            BuilderGameChip chip = new(game.Key, game.Count()) { IsSelected = ticked.Contains(game.Key) };
            chip.PropertyChanged += OnBuilderGameChipChanged;
            BuilderSuggestionGames.Add(chip);
        }
    }

    private void OnBuilderGameChipChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuilderGameChip.IsSelected))
            ApplyBuilderSuggestionFilter();
    }

    private void ApplyBuilderSuggestionFilter()
    {
        HashSet<string> games = BuilderSuggestionGames.Where(c => c.IsSelected).Select(c => c.GameId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BuilderSuggestionItem item in BuilderSuggestions)
            item.PropertyChanged -= OnBuilderSuggestionTicked;
        BuilderSuggestions.Clear();
        foreach (BuilderSuggestionItem item in allBuilderSuggestions)
        {
            if (!BuilderShowWeakMatches && item.IsLow)
                continue;
            if (games.Count > 0 && !games.Contains(item.Game))
                continue;
            BuilderSuggestions.Add(item);
            item.PropertyChanged += OnBuilderSuggestionTicked;
        }
        RebuildBuilderSuggestionSections();
        int hidden = allBuilderSuggestions.Count - BuilderSuggestions.Count;
        BuilderSuggestStatus = allBuilderSuggestions.Count == 0
            ? string.Empty
            : BuilderSuggestions.Count.ToString(CultureInfo.InvariantCulture) + " suggested" +
              (hidden > 0 ? T(" ({0} hidden by the filters)", hidden.ToString(CultureInfo.InvariantCulture)) : string.Empty);
        OnBuilderSuggestionsChanged();
    }

    [RelayCommand]
    private void ClearBuilderGameFilter()
    {
        foreach (BuilderGameChip chip in BuilderSuggestionGames)
            chip.IsSelected = false;
    }

    private async Task LoadBuilderSuggestionPicturesAsync(int version)
    {
        foreach (BuilderSuggestionItem item in allBuilderSuggestions.ToList())
        {
            if (version != builderSuggestVersion)
                return;
            if (!item.IsDcs)
            {
                item.Picture ??= await LoadGameCoverAsync(item.Game, 48);
                continue;
            }
            foreach (PlaneChoice choice in item.PlanePictures.ToList())
                choice.Image ??= await LoadAircraftPictureAsync(choice.Name);
            // DCS: the first aircraft's module icon stands for the row.
            item.Picture ??= item.PlanePictures.Select(p => p.Image).FirstOrDefault(i => i != null);
        }
    }

    [RelayCommand]
    private async Task AddBuilderSuggestionAsync(BuilderSuggestionItem? item)
    {
        if (item == null)
            return;
        RemoveBuilderSuggestionRow(item);
        await ApplyToBuilderRelationAsync(["--add", item.NodeSpec], T("Adding '{0}'...", item.Title), T("'{0}' ({1}) added.", item.Title, item.Where));
    }

    // A command another relation uses: it leaves that relation and comes here.
    [RelayCommand]
    private async Task MoveBuilderSuggestionHereAsync(BuilderSuggestionItem? item)
    {
        if (item == null)
            return;
        RemoveBuilderSuggestionRow(item);
        await ApplyToBuilderRelationAsync(["--take", item.NodeSpec], T("Moving '{0}' here...", item.Title),
            T("'{0}' moved here from '{1}'.", item.Title, string.Join("', '", item.OtherRelations)));
    }

    // Never suggested again for this relation (added switched off); "Dismissed" can restore it.
    [RelayCommand]
    private async Task DismissBuilderSuggestionAsync(BuilderSuggestionItem? item)
    {
        if (item == null)
            return;
        RemoveBuilderSuggestionRow(item);
        await ApplyToBuilderRelationAsync(["--never", item.NodeSpec], T("Dismissing '{0}'...", item.Title), T("'{0}' dismissed for this relation.", item.Title));
    }

    [RelayCommand]
    private async Task AddAllBuilderSuggestionsAsync()
    {
        List<BuilderSuggestionItem> chosen = BuilderSuggestions.Where(s => !s.HasConflict).ToList();
        if (chosen.Count == 0)
            return;
        List<string> options = [];
        foreach (BuilderSuggestionItem item in chosen)
        {
            options.Add("--add");
            options.Add(item.NodeSpec);
            RemoveBuilderSuggestionRow(item);
        }
        await ApplyToBuilderRelationAsync(options, T("Adding {0} actions...", chosen.Count.ToString(CultureInfo.InvariantCulture)),
            chosen.Count.ToString(CultureInfo.InvariantCulture) + " suggested actions added.");
    }

    [RelayCommand]
    private async Task RestoreBuilderDismissedAsync(BuilderDismissedItem? item)
    {
        if (item == null)
            return;
        await ApplyToBuilderRelationAsync(["--remove", item.Spec], T("Restoring '{0}'...", item.Title), T("'{0}' can be suggested again.", item.Title));
    }

    private void RemoveBuilderSuggestionRow(BuilderSuggestionItem item)
    {
        allBuilderSuggestions.Remove(item);
        BuilderSuggestions.Remove(item);
        item.PropertyChanged -= OnBuilderSuggestionTicked;
        RebuildBuilderSuggestionSections();
        OnBuilderSuggestionsChanged();
    }

    // engine relations suggest apply <open relation> <options>, then the relation and its suggestions are reloaded.
    private async Task<bool> ApplyToBuilderRelationAsync(List<string> options, string busyLabel, string done)
    {
        string relation = SelectedRelation?.Name ?? BuilderRelationName;
        if (relation.Length == 0)
            return false;
        List<string> args = ["relations", "suggest", "apply", relation];
        args.AddRange(options);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: busyLabel);
        if (result == null || result.ExitCode != 0)
        {
            BuilderSuggestStatus = T("Could not change '{0}': {1}", relation, (result?.StandardError.Trim() ?? string.Empty));
            await LoadBuilderSuggestionsAsync(relation);
            return false;
        }
        await ReloadBuilderRelationAsync(relation);
        // After the reload: its own engine calls write the status line too.
        StatusText = T("{0} Ctrl+Z undoes it.", done);
        return true;
    }

    // The relation's actions, its card's counts and what is suggested now.
    private async Task ReloadBuilderRelationAsync(string relation)
    {
        await ShowRelationAsync(relation);
        RelationListItem? card = allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.Ordinal));
        if (card != null)
        {
            card.Games = string.Join(", ", RelationNodes.Select(n => n.Game).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            card.Planes = RelationNodes.Sum(n => n.ActivePlanesText.Length == 0 ? 0 : n.ActivePlanesText.Split(',').Length).ToString(CultureInfo.InvariantCulture);
            // The filters' games and aircraft follow (the list stays as it is until the next filter change).
            card.ActiveGamePlanes = RelationNodes
                .SelectMany(n => n.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase)
                    ? n.ActivePlanesText.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => "DCS|" + p)
                    : [n.Game + "|" + n.Game])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            RebuildRelationSubjects();
        }
        MarkBuilderRowsInRelation();
        await LoadBuilderSuggestionsAsync(relation);
        if (AircraftCheckDrop > 0)
            _ = RefreshAircraftCheckAsync();
    }

    // The relations with something to add (engine relations suggest --all): walks through them one by one.
    [RelayCommand]
    private async Task NextRelationWithSuggestionsAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["relations", "suggest", "--all", "--min", "medium"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Looking for relations that can grow..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        List<string> order = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Relations", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                order.AddRange(list.EnumerateArray().Select(i => ReadJsonString(i, "Relation")).Where(n => n.Length > 0));
        }
        catch (JsonException)
        {
        }
        if (order.Count == 0)
        {
            StatusText = T("No relation has more suggestions.");
            return;
        }
        string current = SelectedRelation?.Name ?? string.Empty;
        string next = order.FirstOrDefault(n => string.Compare(n, current, StringComparison.OrdinalIgnoreCase) > 0) ?? order[0];
        SelectRelationByName(next);
    }

    private async Task EnsureBuilderLlmStatusAsync()
    {
        if (builderLlmStatusRequested || LlmModels.Count > 0)
            return;
        builderLlmStatusRequested = true;
        EngineCommandResult? result = await RunEngineAsync(["llm", "status"], includeProfile: false, mutatesProfile: false, busyLabel: N("Checking the local AI model..."),
            forceText: false, background: true);
        if (result != null && result.ExitCode == 0)
            ApplyLlmStatus(result.StandardOutput);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Add actions: one search over every game, tick rows, "Add selected"
    // ---------------------------------------------------------------------------------------------------------

    private int builderSearchVersion;
    private string builderLastSearchKind = "any";
    // Ticked games: every row of them; every game: the best rows of each (an engine run takes about 0.6 s with the map's
    // cache, so typing waits 300 ms before it searches).
    private const int BuilderSearchLimit = 400;
    private const int BuilderSearchLimitAll = 200;
    private const int BuilderSearchPerGame = 12;
    private const int BuilderRowsShown = 160;
    private const int BuilderGameChipsShown = 8;

    private readonly List<BuilderSearchResultItem> allBuilderSearchResults = [];

    // The list as shown: a header per game, then its rows (BuilderSearchGroupItem, BuilderSearchResultItem).
    public ObservableCollection<object> BuilderSearchRows { get; } = [];

    // The rows of the last search (all games, before the game chips); kept for the tests and pictures.
    public IReadOnlyList<BuilderSearchResultItem> BuilderSearchResults => allBuilderSearchResults;

    // Game chips of the results with their counts (none ticked = every game); the first ones as chips, the rest in "more".
    public ObservableCollection<BuilderGameChip> BuilderSearchGames { get; } = [];

    public ObservableCollection<BuilderGameChip> BuilderSearchGamesTop { get; } = [];

    public ObservableCollection<BuilderGameChip> BuilderSearchGamesMore { get; } = [];

    public bool HasMoreBuilderSearchGames => BuilderSearchGamesMore.Count > 0;

    public string BuilderMoreGamesLabel => T("+{0} more games", BuilderSearchGamesMore.Count.ToString(CultureInfo.InvariantCulture));

    // Aircraft the search is limited to (chips with a remove button); picked from BuilderPlaneOptions.
    public ObservableCollection<string> BuilderSearchPlanes { get; } = [];

    public ObservableCollection<string> BuilderPlaneOptions { get; } = [];

    [ObservableProperty]
    private string builderSearchText = string.Empty;

    [ObservableProperty]
    private string builderSearchStatus = string.Empty;

    [ObservableProperty]
    private string builderPlaneFilterText = string.Empty;

    [ObservableProperty]
    private bool isBuilderSearching;

    // "any", "axis" or "button"; an open relation takes only its own kind.
    [ObservableProperty]
    private string builderKindFilter = "any";

    // A relation with actions takes only its own kind; an empty or new one shows every kind (an empty "button" relation
    // must not hide every throttle axis).
    private bool BuilderKindFixed => SelectedRelation != null && HasRelationNodes;

    public string BuilderEffectiveKind => BuilderKindFixed ? (SelectedRelation!.IsAxis ? "axis" : "button") : BuilderKindFilter;

    public bool IsBuilderKindAny => BuilderEffectiveKind == "any";

    public bool IsBuilderKindAxis => BuilderEffectiveKind == "axis";

    public bool IsBuilderKindButton => BuilderEffectiveKind == "button";

    public bool CanChangeBuilderKind => !BuilderKindFixed;

    public string BuilderKindHint => !BuilderKindFixed
        ? T("Axes and buttons can be filtered; a relation holds only one kind.")
        : T("'{0}' is {1}", SelectedRelation!.Name, (SelectedRelation.IsAxis ? T("an axis relation: only axes are shown.") : T("a button relation: only buttons are shown.")));

    // "Did you mean: throttle" (relations search DidYouMean): one click searches that.
    [ObservableProperty]
    private string builderDidYouMean = string.Empty;

    public bool HasBuilderDidYouMean => BuilderDidYouMean.Length > 0;

    partial void OnBuilderDidYouMeanChanged(string value) => OnPropertyChanged(nameof(HasBuilderDidYouMean));

    [RelayCommand]
    private void UseBuilderDidYouMean()
    {
        if (BuilderDidYouMean.Length > 0)
            BuilderSearchText = BuilderDidYouMean;
    }

    // The rows grouped by game (default) or by the function they are (relations search FunctionGroups).
    [ObservableProperty]
    private bool builderGroupByFunction;

    partial void OnBuilderGroupByFunctionChanged(bool value) => RebuildBuilderSearchRows();

    // Function id -> name, and the order of the functions, from the last search.
    private readonly List<(string Id, string Name)> builderFunctionGroups = [];

    // Per game: how many actions the search found in all (relations search Groups Total).
    private readonly Dictionary<string, int> builderGameTotals = new(StringComparer.OrdinalIgnoreCase);

    // The relation's nodes changed: rows in it are marked, and its kind may now decide the search.
    private void OnBuilderNodesChanged()
    {
        MarkBuilderRowsInRelation();
        OnBuilderKindChanged();
        if (!string.Equals(builderLastSearchKind, BuilderEffectiveKind, StringComparison.Ordinal) && (BuilderSearchText ?? string.Empty).Trim().Length > 0)
            _ = DebouncedBuilderSearchAsync();
    }

    public bool HasBuilderSearchResults => BuilderSearchRows.Count > 0;

    public bool HasBuilderSearchPlanes => BuilderSearchPlanes.Count > 0;

    public int BuilderCheckedCount => allBuilderSearchResults.Count(r => r.IsChecked && r.CanCheck);

    public bool HasBuilderChecked => BuilderCheckedCount > 0;

    public string BuilderAddButtonLabel => SelectedRelation != null
        ? T("Add selected ({0})", BuilderCheckedCount.ToString(CultureInfo.InvariantCulture))
        : T("Create relation with {0}{1}", BuilderCheckedCount.ToString(CultureInfo.InvariantCulture), (BuilderCheckedCount == 1 ? " action" : " actions"));

    public string BuilderSearchHint => SelectedRelation != null
        ? T("Search every game for what '{0}' does (\"throttle\" also finds thrust and collective). Tick what belongs and press Add selected.", SelectedRelation.Name)
        : T("Search every game for what the new relation does (\"throttle\", \"flare\", \"gear\"). Tick what belongs and press Create relation.");

    partial void OnBuilderSearchTextChanged(string value) => _ = DebouncedBuilderSearchAsync();

    partial void OnBuilderKindFilterChanged(string value)
    {
        OnBuilderKindChanged();
        _ = SearchBuilderActionsAsync();
    }

    private void OnBuilderKindChanged()
    {
        OnPropertyChanged(nameof(BuilderEffectiveKind));
        OnPropertyChanged(nameof(IsBuilderKindAny));
        OnPropertyChanged(nameof(IsBuilderKindAxis));
        OnPropertyChanged(nameof(IsBuilderKindButton));
        OnPropertyChanged(nameof(CanChangeBuilderKind));
        OnPropertyChanged(nameof(BuilderKindHint));
        CatalogFollowKind();
    }

    // The open relation changed: its kind decides what the search shows.
    private async Task RefreshBuilderSearchForKindAsync()
    {
        OnBuilderKindChanged();
        if ((BuilderSearchText ?? string.Empty).Trim().Length > 0)
            await SearchBuilderActionsAsync();
    }

    [RelayCommand]
    private void SetBuilderKind(string? kind)
    {
        if (!CanChangeBuilderKind)
            return;
        BuilderKindFilter = kind is "axis" or "button" ? kind : "any";
    }

    partial void OnBuilderPlaneFilterTextChanged(string value) => RebuildBuilderPlaneOptions();

    private async Task DebouncedBuilderSearchAsync()
    {
        int version = ++builderSearchVersion;
        await Task.Delay(300);
        if (version == builderSearchVersion)
            await SearchBuilderActionsAsync();
    }

    // The aircraft of every game JoyPro has a list for, filtered by the typed text.
    private void RebuildBuilderPlaneOptions()
    {
        string filter = (BuilderPlaneFilterText ?? string.Empty).Trim();
        IEnumerable<string> planes = dbNodeCatalogByGame
            .SelectMany(p => p.Value.Select(e => e.Plane))
            .Where(p => p.Length > 0 && !BuilderSearchPlanes.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => filter.Length == 0 || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(60);
        BuilderPlaneOptions.Clear();
        foreach (string plane in planes)
            BuilderPlaneOptions.Add(plane);
    }

    [RelayCommand]
    private async Task OpenBuilderPlanePickerAsync()
    {
        await EnsureDbNodeCatalogLoadedAsync();
        RebuildBuilderPlaneOptions();
    }

    [RelayCommand]
    private async Task AddBuilderSearchPlaneAsync(string? plane)
    {
        if (string.IsNullOrWhiteSpace(plane) || BuilderSearchPlanes.Contains(plane, StringComparer.OrdinalIgnoreCase))
            return;
        BuilderSearchPlanes.Add(plane.Trim());
        OnPropertyChanged(nameof(HasBuilderSearchPlanes));
        RebuildBuilderPlaneOptions();
        if (BuilderSearchText.Trim().Length > 0)
            await SearchBuilderActionsAsync();
    }

    [RelayCommand]
    private async Task RemoveBuilderSearchPlaneAsync(string? plane)
    {
        if (plane == null || !BuilderSearchPlanes.Remove(plane))
            return;
        OnPropertyChanged(nameof(HasBuilderSearchPlanes));
        RebuildBuilderPlaneOptions();
        if (BuilderSearchText.Trim().Length > 0)
            await SearchBuilderActionsAsync();
    }

    // engine relations search over every game. It needs no profile (the profile only marks what relations use a row).
    // Without a ticked game chip: the best rows of every game (--per-game); with chips: every row of those games, the
    // chips stay as they were. Ticks survive a new search when the same action comes back.
    [RelayCommand]
    internal async Task SearchBuilderActionsAsync()
    {
        int version = ++builderSearchVersion;
        string query = (BuilderSearchText ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            SetBuilderSearchResults([], rebuildChips: true);
            BuilderSearchStatus = string.Empty;
            BuilderDidYouMean = string.Empty;
            return;
        }
        List<string> games = BuilderSearchGames.Where(c => c.IsSelected).Select(c => c.GameId).ToList();
        List<string> args = ["relations", "search", query];
        if (games.Count > 0)
            args.AddRange(["--game", string.Join("|", games)]);
        if (BuilderSearchPlanes.Count > 0)
            args.AddRange(["--plane", string.Join("|", BuilderSearchPlanes)]);
        string kind = BuilderEffectiveKind;
        builderLastSearchKind = kind;
        if (kind == "axis")
            args.Add("--axis");
        else if (kind == "button")
            args.Add("--button");
        if (games.Count > 0)
            args.AddRange(["--limit", BuilderSearchLimit.ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["--limit", BuilderSearchLimitAll.ToString(CultureInfo.InvariantCulture), "--per-game", BuilderSearchPerGame.ToString(CultureInfo.InvariantCulture)]);
        IsBuilderSearching = true;
        try
        {
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: N("Searching every game..."), forceText: false, background: true);
            if (version != builderSearchVersion)
                return;
            if (result == null)
            {
                SetBuilderSearchResults([], rebuildChips: games.Count == 0);
                BuilderSearchStatus = T("The search could not run: {0}", StatusText);
                return;
            }
            if (result.ExitCode != 0)
            {
                SetBuilderSearchResults([], rebuildChips: games.Count == 0);
                BuilderSearchStatus = result.StandardError.Trim();
                return;
            }
            int total = 0;
            List<string> functions = [];
            List<BuilderSearchResultItem> found = [];
            string didYouMean = string.Empty;
            builderFunctionGroups.Clear();
            if (games.Count == 0)
                builderGameTotals.Clear();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                JsonElement root = doc.RootElement;
                total = root.TryGetProperty("Total", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                functions = ReadStringArray(root, "Functions");
                didYouMean = ReadJsonString(root, "DidYouMean");
                if (root.TryGetProperty("FunctionGroups", out JsonElement functionGroups) && functionGroups.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement group in functionGroups.EnumerateArray())
                        builderFunctionGroups.Add((ReadJsonString(group, "Function"), ReadJsonString(group, "Name")));
                }
                if (games.Count == 0 && root.TryGetProperty("Groups", out JsonElement gameGroups) && gameGroups.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement group in gameGroups.EnumerateArray())
                    {
                        int all = group.TryGetProperty("Total", out JsonElement gt) && gt.ValueKind == JsonValueKind.Number ? gt.GetInt32() : 0;
                        builderGameTotals[ReadJsonString(group, "Game")] = all;
                    }
                }
                if (root.TryGetProperty("Results", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                        found.Add(ParseBuilderSearchHit(item));
                }
            }
            catch (JsonException)
            {
                BuilderSearchStatus = T("Could not read the search result.");
            }
            // A new query (or other filters) folds every game again; the same query keeps what was expanded.
            string searchKey = query + "\u0001" + kind + "\u0001" + string.Join("|", BuilderSearchPlanes);
            if (!string.Equals(searchKey, builderExpandedSearchKey, StringComparison.Ordinal))
            {
                builderExpandedGames.Clear();
                builderExpandedSearchKey = searchKey;
            }
            // Ticks (and changed aircraft ticks) stay on the actions that come back.
            KeepBuilderTicks(allBuilderSearchResults, found);
            BuilderDidYouMean = string.Equals(didYouMean, query, StringComparison.OrdinalIgnoreCase) ? string.Empty : didYouMean;
            SetBuilderSearchResults(found, rebuildChips: games.Count == 0);
            // The same query again: the games the user expanded show all their rows again.
            if (builderExpandedGames.Count > 0)
                await ExpandBuilderGamesAsync(builderExpandedGames.ToList());
            int gameCount = found.Select(r => r.Game).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            string understood = functions.Count > 0 ? T(" · understood as {0}", string.Join(", ", functions.Take(4))) : string.Empty;
            BuilderSearchStatus = found.Count == 0
                ? T("Nothing found for \"{0}\"{1}{2}{3}. Try other words: what it does, or its name in the game.", query, (BuilderSearchPlanes.Count > 0 ? T(" with these aircraft") : string.Empty), (kind != "any" ? T(" among the {0}{1}", kind, (kind == "axis" ? "es" : "s")) : string.Empty), (games.Count > 0 ? T(" in the ticked games") : string.Empty))
                : T("{0}{1}{2} in {3}{4}{5}", found.Count.ToString(CultureInfo.InvariantCulture), (total > found.Count ? T(" of {0}", total.ToString(CultureInfo.InvariantCulture)) : string.Empty), (found.Count == 1 ? " action" : " actions"), gameCount.ToString(CultureInfo.InvariantCulture), (gameCount == 1 ? " game" : " games"), understood);
        }
        finally
        {
            if (version == builderSearchVersion)
                IsBuilderSearching = false;
        }
    }

    private void SetBuilderSearchResults(List<BuilderSearchResultItem> found, bool rebuildChips)
    {
        foreach (BuilderSearchResultItem item in allBuilderSearchResults)
            item.PropertyChanged -= OnBuilderSearchRowChanged;
        allBuilderSearchResults.Clear();
        allBuilderSearchResults.AddRange(found);
        foreach (BuilderSearchResultItem item in allBuilderSearchResults)
            item.PropertyChanged += OnBuilderSearchRowChanged;
        if (rebuildChips)
        {
            foreach (BuilderGameChip chip in BuilderSearchGames)
                chip.PropertyChanged -= OnBuilderSearchGameChanged;
            BuilderSearchGames.Clear();
            BuilderSearchGamesTop.Clear();
            BuilderSearchGamesMore.Clear();
            foreach (IGrouping<string, BuilderSearchResultItem> game in allBuilderSearchResults.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Group: g, Total: builderGameTotals.TryGetValue(g.Key, out int all) && all > 0 ? all : g.Count()))
                         .OrderByDescending(g => g.Total).ThenBy(g => DisplayText.Game(g.Group.Key), StringComparer.OrdinalIgnoreCase).Select(g => g.Group))
            {
                int count = builderGameTotals.TryGetValue(game.Key, out int all) && all > 0 ? all : game.Count();
                BuilderGameChip chip = new(game.Key, count);
                chip.PropertyChanged += OnBuilderSearchGameChanged;
                BuilderSearchGames.Add(chip);
                if (BuilderSearchGamesTop.Count < BuilderGameChipsShown)
                    BuilderSearchGamesTop.Add(chip);
                else
                    BuilderSearchGamesMore.Add(chip);
            }
            OnPropertyChanged(nameof(HasMoreBuilderSearchGames));
            OnPropertyChanged(nameof(BuilderMoreGamesLabel));
        }
        MarkBuilderRowsInRelation();
        RebuildBuilderSearchRows();
    }

    // A game chip: the search runs again for the ticked games (all their rows), or for every game.
    private void OnBuilderSearchGameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuilderGameChip.IsSelected))
            _ = SearchBuilderActionsAsync();
    }

    private void OnBuilderSearchRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuilderSearchResultItem.IsChecked) or nameof(BuilderSearchResultItem.IsInOpenRelation))
            OnBuilderCheckedChanged();
    }

    private void OnBuilderCheckedChanged()
    {
        OnPropertyChanged(nameof(BuilderCheckedCount));
        OnPropertyChanged(nameof(HasBuilderChecked));
        OnPropertyChanged(nameof(BuilderAddButtonLabel));
    }

    // A header per game (or per function), then its rows; at most BuilderRowsShown rows.
    private void RebuildBuilderSearchRows()
    {
        BuilderSearchRows.Clear();
        int budget = BuilderRowsShown;
        bool filtered = BuilderSearchGames.Any(c => c.IsSelected);
        if (BuilderGroupByFunction)
        {
            List<string> order = builderFunctionGroups.Select(g => g.Name).Where(n => n.Length > 0).ToList();
            foreach (IGrouping<string, BuilderSearchResultItem> function in allBuilderSearchResults.GroupBy(r => r.FunctionName.Length > 0 ? r.FunctionName : "Other matches", StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => order.IndexOf(g.Key) is int i && i >= 0 ? i : int.MaxValue).ThenByDescending(g => g.Count()))
            {
                List<BuilderSearchResultItem> rows = function.ToList();
                int take = Math.Max(Math.Min(rows.Count, 6), Math.Min(rows.Count, budget));
                List<BuilderSearchResultItem> shown = rows.Take(take).ToList();
                budget = Math.Max(0, budget - shown.Count);
                BuilderSearchRows.Add(new BuilderSearchGroupItem(string.Empty, shown, rows.Count - shown.Count, function.Key));
                foreach (BuilderSearchResultItem row in shown)
                    BuilderSearchRows.Add(row);
            }
        }
        else
        {
            foreach (IGrouping<string, BuilderSearchResultItem> game in allBuilderSearchResults.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(g => builderGameTotals.TryGetValue(g.Key, out int all) && all > 0 ? all : g.Count()).ThenBy(g => DisplayText.Game(g.Key), StringComparer.OrdinalIgnoreCase))
            {
                List<BuilderSearchResultItem> rows = game.ToList();
                bool expanded = builderExpandedGames.Contains(game.Key);
                // An expanded game shows every row it has; the others their best rows: every game at least a few, the
                // big ones share what is left (without a ticked game chip at most BuilderSearchPerGame); "Show n more"
                // loads the rest.
                int take = Math.Max(Math.Min(rows.Count, 6), Math.Min(rows.Count, budget));
                if (expanded)
                    take = rows.Count;
                else if (!filtered)
                    take = Math.Min(take, Math.Max(BuilderSearchPerGame, Math.Min(rows.Count, 6)));
                List<BuilderSearchResultItem> shown = rows.Take(take).ToList();
                budget = Math.Max(0, budget - shown.Count);
                int all = !filtered && builderGameTotals.TryGetValue(game.Key, out int known) && known > rows.Count ? known : rows.Count;
                BuilderSearchRows.Add(new BuilderSearchGroupItem(game.Key, shown, Math.Max(0, all - shown.Count)) { IsExpanded = expanded });
                foreach (BuilderSearchResultItem row in shown)
                    BuilderSearchRows.Add(row);
            }
        }
        OnPropertyChanged(nameof(CanExpandAllBuilderGames));
        OnPropertyChanged(nameof(CanCollapseAllBuilderGames));
        OnPropertyChanged(nameof(HasBuilderSearchResults));
        OnBuilderCheckedChanged();
    }

    // Rows whose every aircraft is in the open relation are shown as "already in this relation".
    private void MarkBuilderRowsInRelation()
    {
        Dictionary<string, HashSet<string>> present = new(StringComparer.OrdinalIgnoreCase);
        if (SelectedRelation != null)
        {
            foreach (RelationNodeItem node in RelationNodes)
            {
                string key = node.Game + "|" + node.Id;
                if (!present.TryGetValue(key, out HashSet<string>? planes))
                    present[key] = planes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string plane in node.ActivePlanesText.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                    planes.Add(plane);
                if (node.Game != "DCS")
                    planes.Add(node.Game);
            }
        }
        foreach (BuilderSearchResultItem row in allBuilderSearchResults.Concat(CatalogRows()))
        {
            bool inside = present.TryGetValue(row.Game + "|" + row.Id, out HashSet<string>? planes) &&
                          (row.Game != "DCS" || row.SelectedPlanes.Count > 0 && row.SelectedPlanes.All(p => planes.Contains(p)));
            row.IsInOpenRelation = inside;
            if (inside)
                row.IsChecked = false;
        }
        OnBuilderCheckedChanged();
    }

    [RelayCommand]
    private void ClearBuilderChecks()
    {
        foreach (BuilderSearchResultItem row in allBuilderSearchResults)
            row.IsChecked = false;
    }

    [RelayCommand]
    private void CheckAllShownBuilderResults()
    {
        foreach (BuilderSearchResultItem row in BuilderSearchRows.OfType<BuilderSearchResultItem>().Where(r => r.CanCheck))
            row.IsChecked = true;
    }

    // "Add selected": every ticked row into the open relation, or a new relation (named in the name box, else after the
    // first ticked action) that starts with them.
    [RelayCommand]
    internal async Task AddCheckedBuilderResultsAsync()
    {
        List<BuilderSearchResultItem> chosen = allBuilderSearchResults.Where(r => r.IsChecked && r.CanCheck).ToList();
        await AddBuilderRowsAsync(chosen, text => BuilderSearchStatus = text, seedSearch: (BuilderSearchText ?? string.Empty).Trim().Length == 0);
    }

    // Ticked rows (of the search or of the catalog) into the open relation, or a new relation with them.
    private async Task<bool> AddBuilderRowsAsync(List<BuilderSearchResultItem> chosen, Action<string> setStatus, bool seedSearch)
    {
        if (chosen.Count == 0)
        {
            setStatus(T("Tick the actions to add first."));
            return false;
        }
        if (SelectedRelation != null && chosen.Any(c => c.IsAxis != SelectedRelation.IsAxis))
        {
            setStatus(T("'{0}' is {1}, or make a new relation for them (New relation).", SelectedRelation.Name, (SelectedRelation.IsAxis ? "an axis relation; untick the buttons" : "a button relation; untick the axes")));
            return false;
        }
        if (SelectedRelation != null)
        {
            List<string> options = [];
            foreach (BuilderSearchResultItem item in chosen)
            {
                options.Add("--add");
                options.Add(item.NodeSpec);
            }
            string relation = SelectedRelation.Name;
            if (await ApplyToBuilderRelationAsync(options, T("Adding {0} actions to '{1}'...", chosen.Count.ToString(CultureInfo.InvariantCulture), relation),
                    T("{0}{1} added to '{2}' ({3}).", chosen.Count.ToString(CultureInfo.InvariantCulture), (chosen.Count == 1 ? " action" : " actions"), relation, GameCountsText(chosen))))
            {
                foreach (BuilderSearchResultItem item in chosen)
                    item.IsChecked = false;
                return true;
            }
            return false;
        }
        if (chosen.Select(c => c.IsAxis).Distinct().Count() > 1)
        {
            setStatus(T("A relation holds axes or buttons, not both: untick one kind (or use the Axes / Buttons filter)."));
            return false;
        }
        string typed = (RelationNameInput ?? string.Empty).Trim();
        if (typed.Length > 0 && RelationNameExists(typed))
        {
            setStatus(T("A relation named '{0}' exists already: pick another name, or open it on the left.", typed));
            return false;
        }
        string name = typed.Length > 0 ? typed : UniqueRelationName(RelationNameFromTitle(chosen[0].Title));
        List<string> args = ["relations", "add", name, chosen[0].IsAxis ? "--axis" : "--button"];
        foreach (BuilderSearchResultItem item in chosen)
            args.AddRange(["--node", item.NodeSpec]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: "Creating relation '" + name + "'...");
        if (result == null || result.ExitCode != 0)
        {
            setStatus(T("Could not create '{0}': {1}", name, (result?.StandardError.Trim() ?? string.Empty)));
            return false;
        }
        RelationNameInput = string.Empty;
        ShowNewRelationForm = false;
        foreach (BuilderSearchResultItem item in chosen)
            item.IsChecked = false;
        // The search that found its actions stays (its rows now show "already in this relation").
        await OpenBuilderRelationAsync(name, seedSearch: seedSearch);
        StatusText = T("Relation '{0}' created with {1}{2} ({3}). Ctrl+Z undoes it.", name, chosen.Count.ToString(CultureInfo.InvariantCulture), (chosen.Count == 1 ? " action" : " actions"), GameCountsText(chosen));
        return true;
    }

    // "DCS 12 aircraft, IL-2 1, Star Citizen 2"
    private static string GameCountsText(IEnumerable<BuilderSearchResultItem> rows) =>
        string.Join(", ", rows.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Game: g.Key, Count: g.Key == "DCS" ? g.SelectMany(r => r.Planes).Distinct(StringComparer.OrdinalIgnoreCase).Count() : g.Count()))
            .OrderByDescending(g => g.Count)
            .Select(g => DisplayText.Game(g.Game) + " " + g.Count.ToString(CultureInfo.InvariantCulture) + (g.Game == "DCS" ? " aircraft" : string.Empty)));

    // Reloads the list and opens the relation (its actions and suggestions loaded here, not twice by the selection hook).
    // seedSearch: Add actions searches for what the relation is (its name), not for the previous relation's words.
    private async Task OpenBuilderRelationAsync(string name, bool seedSearch = true)
    {
        suppressRelationAutoLoad = true;
        try
        {
            RelationSearchText = string.Empty;
            await RefreshRelationsAsync();
            // The filters would hide it: they go off.
            if (RelationHiddenByFilter(name))
                RelationFilter.ClearAll();
            SelectedRelation = Relations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
                               ?? Relations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            suppressRelationAutoLoad = false;
        }
        if (SelectedRelation != null)
        {
            BuilderRenameText = SelectedRelation.Name;
            if (seedSearch)
                SeedBuilderSearch(SelectedRelation.Name);
            await ShowRelationAsync(SelectedRelation.Name);
            MarkBuilderRowsInRelation();
            await LoadBuilderSuggestionsAsync(SelectedRelation.Name);
            _ = EnsureBuilderLlmStatusAsync();
        }
        OnBuilderKindChanged();
        OnBuilderLayoutChanged();
    }

    internal static string RelationNameFromTitle(string title)
    {
        string text = (title ?? string.Empty).Trim();
        // DCS prefixes ("29Fulcrum:Inp:", "Input.Generic.") say nothing to a person.
        int colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1 && !text.Contains(' ', StringComparison.Ordinal))
            text = text[(colon + 1)..];
        text = text.Replace('_', ' ').Trim();
        return text.Length == 0 ? T("New relation") : text.Length > 60 ? text[..60].Trim() : text;
    }

    private string UniqueRelationName(string name)
    {
        if (!RelationNameExists(name))
            return name;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            if (!RelationNameExists(candidate))
                return candidate;
        }
        return name + " " + Guid.NewGuid().ToString("N")[..6];
    }

    [RelayCommand]
    private void StartNewBuilderRelation()
    {
        CloseRelationTools();
        SelectedRelation = null;
        ShowNewRelationForm = true;
        SetBuilderPanel(BuilderPanelKind.Add);
        MarkBuilderRowsInRelation();
        OnBuilderKindChanged();
        NewRelationFormRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The JoyPro assistant (engine relations assistant)
    // ---------------------------------------------------------------------------------------------------------

    public ObservableCollection<AssistantMessageItem> AssistantMessages { get; } = [];

    [ObservableProperty]
    private string assistantInput = string.Empty;

    [ObservableProperty]
    private bool isAssistantRunning;

    // The relation "Expand with AI" works on (empty: the whole profile).
    [ObservableProperty]
    private string assistantContextRelation = string.Empty;

    // The model can be used (llm status Ready): switched on, downloaded or the user's own llama.cpp found.
    [ObservableProperty]
    private bool llmReady;

    public bool HasAssistantMessages => AssistantMessages.Count > 0;

    public bool HasAssistantContext => AssistantContextRelation.Length > 0;

    public bool IsBuilderChatAvailable => LlmEnabled && LlmReady;

    public string AssistantModeText => IsBuilderChatAvailable
        ? T("Local AI model ready{0}: say it in your own words. It runs on this PC only.", (SelectedLlmModel != null && !IsLlmUsingLocal ? " (" + SelectedLlmModel.Label + ")" : IsLlmUsingLocal ? T(" (your own model)") : string.Empty))
        : LlmEnabled
            ? T("The local AI model is switched on but not ready, so JoyPro uses its offline brain: simple commands only (see the examples).")
            : T("Offline brain: simple commands like \"Throttle ALL: all throttle axes\". Free sentences need the optional local AI model.");

    public string AssistantPlaceholder => HasAssistantContext
        ? T("What should '{0}' also hold, or lose? Explain your logic...", AssistantContextRelation)
        : IsBuilderChatAvailable
            ? T("e.g. Add all inputs that bind thrust to one axis into one relation called Throttle ALL, including collectives")
            : T("e.g. Throttle ALL: all throttle axes");

    public IReadOnlyList<string> AssistantExamples => HasAssistantContext
        ? (IsBuilderChatAvailable
            ? ["Suggest what else fits here and add it", "Add the helicopter collectives", "Remove everything that is not an engine throttle"]
            : ["add the collectives", "remove War Thunder", "rename to " + AssistantContextRelation + " ALL"])
        : (IsBuilderChatAvailable
            ? ["Add all inputs that bind all thrust to one axis into one relation called Throttle ALL, including collectives and spaceship forward/backward thrusters",
               "Make a relation Flares with the flare release of every aircraft and game"]
            : ["Throttle ALL: all throttle axes", "Flares: flare release", "Gear: landing gear toggle"]);

    partial void OnLlmReadyChanged(bool value) => OnAssistantModeChanged();

    partial void OnAssistantContextRelationChanged(string value)
    {
        OnPropertyChanged(nameof(HasAssistantContext));
        OnAssistantModeChanged();
    }

    private void OnAssistantModeChanged()
    {
        OnPropertyChanged(nameof(IsBuilderChatAvailable));
        OnPropertyChanged(nameof(AssistantModeText));
        OnPropertyChanged(nameof(AssistantPlaceholder));
        OnPropertyChanged(nameof(AssistantExamples));
    }

    // The window focuses the assistant's input box.
    public event EventHandler? AssistantFocusRequested;

    [RelayCommand]
    private void OpenAssistant()
    {
        AssistantContextRelation = string.Empty;
        SetBuilderPanel(BuilderPanelKind.Assistant);
        _ = EnsureBuilderLlmStatusAsync();
        AssistantFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    // "Expand with AI" on the open relation: the assistant works on it.
    [RelayCommand]
    private void ExpandWithAi()
    {
        AssistantContextRelation = SelectedRelation?.Name ?? string.Empty;
        SetBuilderPanel(BuilderPanelKind.Assistant);
        _ = EnsureBuilderLlmStatusAsync();
        AssistantFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearAssistantContext() => AssistantContextRelation = string.Empty;

    [RelayCommand]
    private void UseAssistantExample(string? text)
    {
        AssistantInput = text ?? string.Empty;
        AssistantFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    internal async Task AskAssistantAsync()
    {
        string message = (AssistantInput ?? string.Empty).Trim();
        if (message.Length == 0 || IsAssistantRunning)
            return;
        string context = AssistantContextRelation;
        if (context.Length > 0 && !allRelations.Any(r => string.Equals(r.Name, context, StringComparison.Ordinal)))
            context = AssistantContextRelation = string.Empty;
        AssistantMessages.Add(new AssistantMessageItem(true, context.Length > 0 ? T("{0}  (in '{1}')", message, context) : message));
        OnPropertyChanged(nameof(HasAssistantMessages));
        AssistantInput = string.Empty;
        List<string> args = ["relations", "assistant", message, "--llm", LlmEnabled ? "auto" : "off"];
        if (context.Length > 0)
            args.AddRange(["--relation", context]);
        // The profile history (MainWindowViewModel.ProfileHistory.cs): the engine records one version per command, so
        // the version this answer made is what Undo takes back.
        RefreshHistoryState();
        int before = historyLog?.UndoSubject?.Id ?? 0;
        IsAssistantRunning = true;
        EngineCommandResult? result;
        try
        {
            result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
                busyLabel: IsBuilderChatAvailable ? "The JoyPro assistant is working (local AI model)..." : "The JoyPro assistant is working...", forceText: false,
                historyNote: "JoyPro assistant: " + Shorten(message, 60));
        }
        finally
        {
            IsAssistantRunning = false;
        }
        if (result == null)
        {
            AddAssistantAnswer(new AssistantMessageItem(false, T("JoyPro could not run the assistant. {0}", StatusText)));
            return;
        }
        if (result.ExitCode != 0)
        {
            AddAssistantAnswer(new AssistantMessageItem(false, T("That did not work: {0}", FirstErrorLine(result.StandardError))));
            return;
        }
        AssistantMessageItem answer = ParseAssistantAnswer(result.StandardOutput);
        RefreshHistoryState();
        int after = historyLog?.UndoSubject?.Id ?? 0;
        if (answer.Changed && after != 0 && after != before)
            answer.UndoVersion = after;
        AddAssistantAnswer(answer);
        if (!answer.Changed)
        {
            StatusText = answer.NeedsModel ? T("That needs the local AI model; nothing changed.") : T("Nothing changed.");
            return;
        }
        string focus = answer.FocusRelation.Length > 0 ? answer.FocusRelation : SelectedRelation?.Name ?? string.Empty;
        if (focus.Length > 0)
            await OpenBuilderRelationAsync(focus);
        else
            await RefreshRelationsAsync();
        if (context.Length > 0 && !string.Equals(context, focus, StringComparison.Ordinal) && !allRelations.Any(r => r.Name == context))
            AssistantContextRelation = focus;
        StatusText = T("The assistant changed {0}{1}. Undo in its answer (or Ctrl+Z) takes it all back.", answer.Relations.Count.ToString(CultureInfo.InvariantCulture), (answer.Relations.Count == 1 ? " relation" : " relations"));
    }

    private void AddAssistantAnswer(AssistantMessageItem item)
    {
        AssistantMessages.Add(item);
        OnPropertyChanged(nameof(HasAssistantMessages));
    }

    private static string FirstErrorLine(string text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)?.Replace("engine error:", string.Empty).Trim() ?? string.Empty;

    // engine relations assistant JSON -> the answer bubble: the reply, per relation what was created, renamed, added and
    // removed per game, the steps, rejected ids and notes.
    internal static AssistantMessageItem ParseAssistantAnswer(string output)
    {
        AssistantMessageItem item = new(false, string.Empty);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            item.Text = ReadJsonString(root, "Reply");
            item.Changed = ReadJsonBool(root, "Changed");
            item.NeedsModel = ReadJsonBool(root, "NeedsModel");
            item.FocusRelation = ReadJsonString(root, "FocusRelation");
            string method = ReadJsonString(root, "Method");
            item.Method = method.Length > 0 ? "Answered by the " + method : string.Empty;
            if (root.TryGetProperty("Relations", out JsonElement relations) && relations.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement relation in relations.EnumerateArray())
                {
                    List<string> parts = [];
                    if (ReadJsonBool(relation, "Created"))
                        parts.Add("created");
                    string renamed = ReadJsonString(relation, "RenamedFrom");
                    if (renamed.Length > 0)
                        parts.Add(T("renamed from '{0}'", renamed));
                    string added = Counts(relation, "Added");
                    if (added.Length > 0)
                        parts.Add("added " + added);
                    string removed = Counts(relation, "Removed");
                    if (removed.Length > 0)
                        parts.Add("removed " + removed);
                    List<string> also = ReadStringArray(relation, "AlsoIn");
                    string text = string.Join(" · ", parts) + (also.Count > 0 ? T(" · some are also in '{0}'", string.Join("', '", also.Take(3))) : string.Empty);
                    int actions = relation.TryGetProperty("Actions", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                    if (actions > 0)
                        text += T(" · now {0}{1}", actions.ToString(CultureInfo.InvariantCulture), (actions == 1 ? " action" : " actions"));
                    item.Relations.Add(new AssistantRelationSummary(ReadJsonString(relation, "Name"), text));
                }
            }
            if (root.TryGetProperty("Steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    string call = ReadJsonString(step, "Call");
                    string res = ReadJsonString(step, "Result");
                    string error = ReadJsonString(step, "Error");
                    List<string> rejected = ReadStringArray(step, "Rejected");
                    List<string> skipped = ReadStringArray(step, "Skipped");
                    string line = (call.Length > 0 ? call : T("(no tool call)")) + (res.Length > 0 ? " → " + Shorten(res, 160) : error.Length > 0 ? " → " + Shorten(error, 160) : string.Empty);
                    if (rejected.Count > 0)
                        line += T(" · rejected ids: {0}", string.Join(", ", rejected));
                    if (skipped.Count > 0)
                        line += T(" · skipped (wrong kind): {0}", string.Join(", ", skipped.Take(8)));
                    item.Steps.Add(line);
                }
            }
            List<string> notes = [];
            List<string> rejectedIds = ReadStringArray(root, "Rejected");
            if (rejectedIds.Count > 0)
                notes.Add(T("JoyPro rejected ids the AI made up: {0}.", string.Join(", ", rejectedIds)));
            if (root.TryGetProperty("Llm", out JsonElement llm) && llm.ValueKind == JsonValueKind.Object)
            {
                string note = ReadJsonString(llm, "Note");
                if (note.Length > 0 && !ReadJsonBool(llm, "Used"))
                    notes.Add(T("The local AI model was not used: {0}", note));
            }
            notes.AddRange(ReadStringArray(root, "Warnings").Where(w => !w.Contains("action catalog not found", StringComparison.OrdinalIgnoreCase)).Distinct().Take(4));
            item.Notes = string.Join("\n", notes);
            if (item.Text.Length == 0)
                item.Text = item.Changed ? T("Done.") : T("Nothing changed.");
        }
        catch (JsonException)
        {
            item.Text = T("Could not read the assistant's answer.");
        }
        return item;
    }

    private static string Counts(JsonElement relation, string property)
    {
        if (!relation.TryGetProperty(property, out JsonElement counts) || counts.ValueKind != JsonValueKind.Object)
            return string.Empty;
        return string.Join(", ", counts.EnumerateObject()
            .Select(p => (p.Name, Count: p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0))
            .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Name + " " + p.Count.ToString(CultureInfo.InvariantCulture) + (p.Name == "DCS" ? " aircraft" : string.Empty)));
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // Undo of one answer: the history's undo, while this answer's version is the last change.
    [RelayCommand]
    private async Task UndoAssistantTurnAsync(AssistantMessageItem? item)
    {
        if (item == null || !item.CanUndo)
            return;
        RefreshHistoryState();
        if (item.UndoVersion == 0 || historyLog?.UndoSubject?.Id != item.UndoVersion)
        {
            StatusText = T("Other changes came after this answer: undo them first with Ctrl+Z (each press takes back one change), or pick a version in History.");
            return;
        }
        await UndoLastChangeAsync();
        item.IsUndone = true;
        string focus = item.FocusRelation;
        if (focus.Length > 0 && allRelations.Any(r => r.Name == focus))
            await OpenBuilderRelationAsync(focus);
        StatusText = T("The assistant's changes are undone.");
    }

    [RelayCommand]
    private async Task OpenAssistantRelationAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !allRelations.Any(r => r.Name == name))
            return;
        await OpenBuilderRelationAsync(name);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The whole profile: "Group all my actions automatically" (Auto Group or a description)
    // ---------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowOrganizeByFunction()
    {
        IsDescribeGroupPanelOpen = false;
        IsAutoGroupPanelOpen = true;
        SelectedAutoGroupPreset ??= AutoGroupPresets[0];
    }

    [RelayCommand]
    private void ShowOrganizeByDescription()
    {
        IsAutoGroupPanelOpen = false;
        IsDescribeGroupPanelOpen = true;
    }

    public bool IsOrganizeOpen => IsAutoGroupPanelOpen || IsDescribeGroupPanelOpen;

    [RelayCommand]
    private void ToggleOrganize()
    {
        if (IsOrganizeOpen)
        {
            IsAutoGroupPanelOpen = false;
            IsDescribeGroupPanelOpen = false;
            return;
        }
        ShowOrganizeByFunction();
    }
}
