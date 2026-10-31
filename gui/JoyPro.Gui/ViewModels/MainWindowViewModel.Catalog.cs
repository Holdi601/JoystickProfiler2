using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// "Open catalog" on the Relations page (docs/relation-builder.md "Browse the catalog"): not what the search finds, but
// everything a game or aircraft has, by the game's own categories (DCS "HOTAS", "Landing gear panel"; games without
// categories by JoyPro's function categories), each action with what uses it already (in which relation, bound to
// which input) and a tick. Ticked actions go into the open relation (or a new one) like the search's: a DCS action
// takes the ticked aircraft of its aircraft selector (the browsed aircraft, more with one tick each).
// engine relations browse [--game g --plane p] [--axis|--button].
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<CatalogGameOption> CatalogGames { get; } = [];

    public ObservableCollection<CatalogPlaneOption> CatalogPlanes { get; } = [];

    public ObservableCollection<CatalogCategoryItem> CatalogCategories { get; } = [];

    private readonly List<CatalogRowItem> catalogRows = [];
    // The engine's category order (the catalog's own order).
    private readonly List<string> catalogCategoryOrder = [];
    private string catalogLoadedKey = string.Empty;
    private int catalogVersion;
    private bool catalogOverviewLoaded;

    [ObservableProperty]
    private CatalogGameOption? catalogGame;

    [ObservableProperty]
    private CatalogPlaneOption? catalogPlane;

    [ObservableProperty]
    private string catalogFilterText = string.Empty;

    // Only actions no relation uses yet.
    [ObservableProperty]
    private bool catalogOnlyFree;

    [ObservableProperty]
    private string catalogStatus = string.Empty;

    [ObservableProperty]
    private bool isCatalogLoading;

    public bool CatalogHasPlanes => CatalogGame?.HasPlanes == true;

    public bool HasCatalogRows => catalogRows.Count > 0;

    public int CatalogCheckedCount => catalogRows.Count(r => r.Row.IsChecked && r.Row.CanCheck);

    public bool HasCatalogChecked => CatalogCheckedCount > 0;

    public string CatalogAddLabel => SelectedRelation != null
        ? T("Add selected ({0})", CatalogCheckedCount)
        : Plural("Create relation with {0} action", "Create relation with {0} actions", CatalogCheckedCount);

    // The rows of the catalog (for MarkBuilderRowsInRelation and the tests).
    internal IEnumerable<BuilderSearchResultItem> CatalogRows() => catalogRows.Select(r => r.Row);

    internal IReadOnlyList<CatalogRowItem> CatalogRowItems => catalogRows;

    // A game was hidden or shown again (Settings > Games): the catalog's games, the action search's results and game
    // chips and the suggestions leave hidden games out; the catalog reads its games again when it opens next.
    private void RefreshHiddenGamesInCatalog()
    {
        catalogOverviewLoaded = false;
        foreach (CatalogGameOption hidden in CatalogGames.Where(g => IsGameHidden(g.Game)).ToList())
            CatalogGames.Remove(hidden);
        if (CatalogGame != null && IsGameHidden(CatalogGame.Game))
            CatalogGame = CatalogGames.FirstOrDefault();
        if (allBuilderSearchResults.Any(r => IsGameHidden(r.Game)))
            SetBuilderSearchResults(allBuilderSearchResults.Where(r => !IsGameHidden(r.Game)).ToList(), rebuildChips: true);
        if (allBuilderSuggestions.RemoveAll(s => IsGameHidden(s.Game)) > 0)
        {
            RebuildBuilderGameChips();
            ApplyBuilderSuggestionFilter();
        }
    }

    // "Open catalog": the panel, the games, the open relation's game and aircraft picked.
    [RelayCommand]
    internal async Task OpenCatalogAsync()
    {
        SetBuilderPanel(BuilderPanelKind.Catalog);
        await EnsureCatalogOverviewAsync();
        if (CatalogGame == null && CatalogGames.Count > 0)
        {
            // The open relation's first DCS aircraft, else its first game, else DCS.
            RelationNodeItem? dcs = SelectedRelation != null ? RelationNodes.FirstOrDefault(n => n.Game == "DCS" && n.ActivePlanesText.Length > 0) : null;
            string game = dcs != null ? "DCS" : SelectedRelation != null ? RelationNodes.Select(n => n.Game).FirstOrDefault(g => g.Length > 0) ?? "DCS" : "DCS";
            CatalogGameOption? option = CatalogGames.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase)) ?? CatalogGames[0];
            string plane = dcs?.ActivePlanesText.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0) ?? string.Empty;
            await SelectCatalogAsync(option.Game, plane);
        }
        else if (CatalogGame != null && !string.Equals(catalogLoadedKey, CatalogKey(), StringComparison.Ordinal))
        {
            await LoadCatalogListAsync();
        }
    }

    // Browse this game (and aircraft) at once (also for the tests and the journey).
    internal async Task SelectCatalogAsync(string game, string plane)
    {
        await EnsureCatalogOverviewAsync();
        CatalogGameOption? option = CatalogGames.FirstOrDefault(g => string.Equals(g.Game, game, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            CatalogStatus = T("JoyPro has no command list of {0} on this PC.", DisplayText.Game(game));
            return;
        }
        suppressCatalogLoad = true;
        try
        {
            CatalogGame = option;
            CatalogPlane = option.HasPlanes
                ? CatalogPlanes.FirstOrDefault(p => string.Equals(p.Plane, plane, StringComparison.OrdinalIgnoreCase)) ?? CatalogPlanes.FirstOrDefault(p => p.InProfile) ?? CatalogPlanes.FirstOrDefault()
                : null;
        }
        finally
        {
            suppressCatalogLoad = false;
        }
        await LoadCatalogListAsync();
    }

    private bool suppressCatalogLoad;

    partial void OnCatalogGameChanged(CatalogGameOption? value)
    {
        bool before = suppressCatalogLoad;
        suppressCatalogLoad = true;
        try
        {
            CatalogPlanes.Clear();
            foreach (CatalogPlaneOption plane in value?.Planes ?? [])
                CatalogPlanes.Add(plane);
            CatalogPlane = value?.HasPlanes == true ? CatalogPlanes.FirstOrDefault(p => p.InProfile) ?? CatalogPlanes.FirstOrDefault() : null;
        }
        finally
        {
            suppressCatalogLoad = before;
        }
        OnPropertyChanged(nameof(CatalogHasPlanes));
        if (!suppressCatalogLoad)
            _ = LoadCatalogListAsync();
    }

    partial void OnCatalogPlaneChanged(CatalogPlaneOption? value)
    {
        if (!suppressCatalogLoad && value != null)
            _ = LoadCatalogListAsync();
    }

    partial void OnCatalogFilterTextChanged(string value)
    {
        ApplyCatalogFilter();
        _ = RefreshCatalogMeaningAsync(value);
    }

    // The filter by meaning: what the engine's search finds for the words in this game's list ("fast travel" = the
    // jump drive, "drop the wheels" = gear down), besides the rows whose title has the words.
    private HashSet<string> catalogMeaningKeys = new(StringComparer.OrdinalIgnoreCase);
    private string catalogMeaningText = string.Empty;

    internal async Task RefreshCatalogMeaningAsync(string text)
    {
        string wanted = (text ?? string.Empty).Trim();
        CatalogGameOption? game = CatalogGame;
        if (wanted.Length < 3 || game == null)
        {
            if (catalogMeaningKeys.Count > 0)
            {
                catalogMeaningKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                catalogMeaningText = string.Empty;
                ApplyCatalogFilter();
            }
            return;
        }
        // Typing: only the last text asks the engine.
        await Task.Delay(CatalogMeaningDelayMs);
        if (!string.Equals((CatalogFilterText ?? string.Empty).Trim(), wanted, StringComparison.Ordinal) || game != CatalogGame)
            return;
        List<string> args = ["relations", "search", wanted, "--game", game.Game, "--limit", "200"];
        if (game.HasPlanes && CatalogPlane != null)
            args.AddRange(["--plane", CatalogPlane.Plane]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: T("Looking up \"{0}\"...", wanted), forceText: false, background: true);
        if (result == null || result.ExitCode != 0 || !string.Equals((CatalogFilterText ?? string.Empty).Trim(), wanted, StringComparison.Ordinal))
            return;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Results", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    // Rows found as the function the words mean, not plain word matches (those the title filter has).
                    string match = ReadJsonString(row, "Match");
                    if (match is "exact" or "function" or "synonym" or "described" or "catalog")
                        keys.Add(ReadJsonString(row, "Game") + "|" + ReadJsonString(row, "Id"));
                }
            }
        }
        catch (JsonException)
        {
            return;
        }
        catalogMeaningKeys = keys;
        catalogMeaningText = wanted;
        ApplyCatalogFilter();
    }

    internal static int CatalogMeaningDelayMs = 300;

    partial void OnCatalogOnlyFreeChanged(bool value) => ApplyCatalogFilter();

    private async Task EnsureCatalogOverviewAsync()
    {
        if (catalogOverviewLoaded)
            return;
        EngineCommandResult? result = await RunEngineAsync(["relations", "browse"], includeProfile: HasProfile, mutatesProfile: false,
            busyLabel: T("Reading the command lists..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
        {
            CatalogStatus = T("The catalog could not be read: {0}", result?.StandardError.Trim() ?? StatusText);
            return;
        }
        HashSet<string> profilePlanes = allRelations.SelectMany(r => r.ActiveGamePlanes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CatalogGames.Clear();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                List<CatalogGameOption> options = [];
                foreach (JsonElement game in games.EnumerateArray())
                {
                    string id = ReadJsonString(game, "Game");
                    // A hidden game (Settings > Games) is not offered.
                    if (IsGameHidden(id))
                        continue;
                    List<CatalogPlaneOption> planes = [];
                    if (game.TryGetProperty("Planes", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement plane in list.EnumerateArray())
                        {
                            string name = ReadJsonString(plane, "Plane");
                            planes.Add(new CatalogPlaneOption(name, ReadJsonInt(plane, "Actions"), ReadJsonInt(plane, "InRelations"), profilePlanes.Contains(id + "|" + name)));
                        }
                    }
                    // The aircraft of the profile first, then the others (both by name).
                    planes = planes.OrderByDescending(p => p.InProfile).ThenBy(p => p.Plane, StringComparer.OrdinalIgnoreCase).ToList();
                    options.Add(new CatalogGameOption(id, ReadJsonInt(game, "Actions"), planes));
                }
                // The profile's games first.
                HashSet<string> profileGames = profilePlanes.Select(p => p[..p.IndexOf('|')]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (CatalogGameOption option in options.OrderByDescending(o => profileGames.Contains(o.Game)).ThenBy(o => DisplayText.Game(o.Game), StringComparer.OrdinalIgnoreCase))
                    CatalogGames.Add(option);
            }
            catalogOverviewLoaded = CatalogGames.Count > 0;
        }
        catch (JsonException)
        {
            CatalogStatus = T("Could not read the catalog.");
        }
    }

    private string CatalogKey() => (CatalogGame?.Game ?? string.Empty) + "|" + (CatalogPlane?.Plane ?? string.Empty) + "|" + BuilderEffectiveKind;

    private async Task LoadCatalogListAsync()
    {
        CatalogGameOption? game = CatalogGame;
        if (game == null || (game.HasPlanes && CatalogPlane == null))
            return;
        int version = ++catalogVersion;
        string key = CatalogKey();
        List<string> args = ["relations", "browse", "--game", game.Game];
        if (game.HasPlanes && CatalogPlane != null)
            args.AddRange(["--plane", CatalogPlane.Plane]);
        string kind = BuilderEffectiveKind;
        if (kind == "axis")
            args.Add("--axis");
        else if (kind == "button")
            args.Add("--button");
        IsCatalogLoading = true;
        try
        {
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false,
                busyLabel: T("Opening the catalog of {0}...", DisplayText.Game(game.Game) + (CatalogPlane != null ? " " + CatalogPlane.Plane : string.Empty)), forceText: false, background: true);
            if (version != catalogVersion)
                return;
            if (result == null || result.ExitCode != 0)
            {
                SetCatalogRows([], []);
                CatalogStatus = T("The catalog could not be read: {0}", result?.StandardError.Trim() ?? StatusText);
                return;
            }
            List<CatalogRowItem> rows = [];
            List<(string Name, int Count)> categories = [];
            string source = "game";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                JsonElement root = doc.RootElement;
                source = ReadJsonString(root, "CategorySource");
                if (root.TryGetProperty("Categories", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    categories.AddRange(list.EnumerateArray().Select(c => (ReadJsonString(c, "Name"), ReadJsonInt(c, "Count"))));
                if (root.TryGetProperty("Results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in results.EnumerateArray())
                        rows.Add(CatalogRowOf(ParseBuilderSearchHit(item)));
                }
            }
            catch (JsonException ex)
            {
                SetCatalogRows([], []);
                CatalogStatus = T("Could not read the catalog.") + " " + ex.Message;
                JoyProLog.Warn("Catalog", "relations browse --game " + game.Game + ": the output could not be read: " + ex.Message);
                return;
            }
            catalogLoadedKey = key;
            SetCatalogRows(rows, categories.Select(c => c.Name).ToList());
            int used = rows.Count(r => r.IsUsed);
            CatalogStatus = DisplayText.Game(game.Game) + (CatalogPlane != null ? " " + CatalogPlane.Plane : string.Empty) + ": " +
                            T("{0} actions in {1} categories", rows.Count, categories.Count) + (kind == "axis" ? " (" + T("axes") + ")" : kind == "button" ? " (" + T("buttons") + ")" : string.Empty) +
                            " · " + T("{0} used by your relations", used) +
                            (source == "joypro" ? " · " + T("the game has no categories: grouped by what they do") : string.Empty);
        }
        finally
        {
            if (version == catalogVersion)
                IsCatalogLoading = false;
        }
    }

    private CatalogRowItem CatalogRowOf(BuilderSearchResultItem row)
    {
        string used = row.InRelations.Count == 0 ? string.Empty
            : T("in '{0}'", string.Join("', '", row.InRelations.Take(3))) + (row.InRelations.Count > 3 ? " +" + (row.InRelations.Count - 3).ToString(CultureInfo.InvariantCulture) : string.Empty);
        RelationListItem? bound = row.InRelations
            .Select(name => allRelations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(r => r != null && r.IsBound);
        return new CatalogRowItem(row, used, bound != null ? T("bound: {0}", bound.BindLabel) : string.Empty);
    }

    private void SetCatalogRows(List<CatalogRowItem> rows, List<string> categoryOrder, bool keepExpanded = false)
    {
        EnsureActionSorts();
        catalogCategoryOrder.Clear();
        catalogCategoryOrder.AddRange(categoryOrder);
        HashSet<string> expanded = CatalogCategories.Where(c => keepExpanded && c.IsExpanded).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogRowItem row in catalogRows)
            row.Row.PropertyChanged -= OnCatalogRowChanged;
        catalogRows.Clear();
        catalogRows.AddRange(rows);
        foreach (CatalogRowItem row in catalogRows)
            row.Row.PropertyChanged += OnCatalogRowChanged;
        // The rows of the open relation are known before the categories are ordered by what matters for it.
        MarkBuilderRowsInRelation();
        CatalogCategories.Clear();
        Dictionary<string, List<CatalogRowItem>> byCategory = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogRowItem row in rows)
        {
            foreach (string name in CatalogCategoryNames(row.Row.Category))
            {
                if (!byCategory.TryGetValue(name, out List<CatalogRowItem>? list))
                    byCategory[name] = list = [];
                list.Add(row);
            }
        }
        // A first sort level "Category" orders the categories by name; otherwise the engine's order.
        IEnumerable<string> names = categoryOrder.Concat(byCategory.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        if (CatalogSort.PrimaryKey == "category")
            names = CatalogSort.PrimaryDescending
                ? names.OrderByDescending(n => n, Comparer<string>.Create(ListSortRow.Natural))
                : names.OrderBy(n => n, Comparer<string>.Create(ListSortRow.Natural));
        else
        {
            // Own order: the categories that matter for the open relation first (its own actions, then the recommended
            // actions of its function, then any action of its function, then any recommended action), the rest by name.
            List<string> ordered = names.ToList();
            names = ordered.OrderByDescending(n => byCategory.TryGetValue(n, out List<CatalogRowItem>? l) ? CatalogCategoryRelevance(l) : 0)
                .ThenBy(n => n, Comparer<string>.Create(ListSortRow.Natural));
        }
        string plane = CatalogPlane?.Plane ?? string.Empty;
        foreach (string name in names)
        {
            if (byCategory.TryGetValue(name, out List<CatalogRowItem>? list))
            {
                List<CatalogRowItem> sorted = CatalogSort.HasLevels ? CatalogSort.Sort(list, r => SortRowOfAction(r.Row, plane) with { Category = name }).ToList() : list;
                CatalogCategoryItem category = new(name, sorted);
                if (expanded.Contains(name))
                    category.IsExpanded = true;
                CatalogCategories.Add(category);
            }
        }
        MarkBuilderRowsInRelation();
        ApplyCatalogFilter();
        OnPropertyChanged(nameof(HasCatalogRows));
        OnCatalogCheckedChanged();
    }

    // 4: holds an action of the open relation; 3: a recommended action of the relation's function; 2: an action of that
    // function; 1: a recommended action; 0: none (Star Citizen's "Character customizer" pitch for a Pitch relation).
    internal int CatalogCategoryRelevance(IEnumerable<CatalogRowItem> rows)
    {
        string function = (BuilderFunctionText ?? string.Empty).Trim();
        int best = 0;
        foreach (CatalogRowItem item in rows)
        {
            BuilderSearchResultItem row = item.Row;
            bool fits = function.Length > 0 && row.FunctionName.Length > 0 &&
                        (row.FunctionName.Contains(function, StringComparison.OrdinalIgnoreCase) || function.Contains(row.FunctionName, StringComparison.OrdinalIgnoreCase));
            int score = row.IsInOpenRelation ? 4 : row.IsRecommended && fits ? 3 : fits ? 2 : row.IsRecommended ? 1 : 0;
            if (score > best)
                best = score;
            if (best == 4)
                break;
        }
        return best;
    }

    // DCS "Control Stick; HOTAS" is in both.
    internal static List<string> CatalogCategoryNames(string category)
    {
        List<string> names = (category ?? string.Empty).Split(';').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            names.Add("Other");
        return names;
    }

    private void OnCatalogRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BuilderSearchResultItem.IsChecked) or nameof(BuilderSearchResultItem.IsInOpenRelation))
            OnCatalogCheckedChanged();
    }

    private void OnCatalogCheckedChanged()
    {
        OnPropertyChanged(nameof(CatalogCheckedCount));
        OnPropertyChanged(nameof(HasCatalogChecked));
        OnPropertyChanged(nameof(CatalogAddLabel));
    }

    // The filter text and "only free": a category with a match opens while a filter is typed.
    private void ApplyCatalogFilter()
    {
        string[] words = (CatalogFilterText ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool filtering = words.Length > 0;
        // The engine's meaning matches count for this text only.
        HashSet<string> meaning = string.Equals(catalogMeaningText, (CatalogFilterText ?? string.Empty).Trim(), StringComparison.Ordinal) ? catalogMeaningKeys : [];
        foreach (CatalogCategoryItem category in CatalogCategories)
        {
            bool nameHit = filtering && words.All(w => category.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            List<CatalogRowItem> matching = category.Rows.Where(r => (!CatalogOnlyFree || !r.IsUsed) &&
                (!filtering || nameHit || meaning.Contains(r.Row.Game + "|" + r.Row.Id) || words.All(w => (r.Row.Title + " " + r.Row.Id).Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                        (r.Row.Game == "DCS" && JoyPro.Engine.DcsCommandNames.KeyMatches(w, r.Row.Title, DcsEnglishNames()))))).ToList();
            category.Matching = matching.Count;
            category.IsVisible = matching.Count > 0;
            if (filtering && matching.Count > 0)
                category.IsExpanded = true;
            FillCategoryRows(category, matching);
        }
    }

    private static void FillCategoryRows(CatalogCategoryItem category, List<CatalogRowItem> matching)
    {
        category.VisibleRows.Clear();
        if (!category.IsExpanded)
            return;
        foreach (CatalogRowItem row in matching)
            category.VisibleRows.Add(row);
    }

    [RelayCommand]
    private void ToggleCatalogCategory(CatalogCategoryItem? category)
    {
        if (category == null)
            return;
        category.IsExpanded = !category.IsExpanded;
        ApplyCatalogFilter();
    }

    [RelayCommand]
    private void ExpandAllCatalog()
    {
        foreach (CatalogCategoryItem category in CatalogCategories)
            category.IsExpanded = true;
        ApplyCatalogFilter();
    }

    [RelayCommand]
    private void CollapseAllCatalog()
    {
        foreach (CatalogCategoryItem category in CatalogCategories)
            category.IsExpanded = false;
        ApplyCatalogFilter();
    }

    [RelayCommand]
    private void ClearCatalogChecks()
    {
        foreach (CatalogRowItem row in catalogRows)
            row.Row.IsChecked = false;
    }

    [RelayCommand]
    internal async Task AddCheckedCatalogAsync()
    {
        List<BuilderSearchResultItem> chosen = catalogRows.Select(r => r.Row).Where(r => r.IsChecked && r.CanCheck).Distinct().ToList();
        await AddBuilderRowsAsync(chosen, text => CatalogStatus = text, seedSearch: false);
        MarkBuilderRowsInRelation();
        foreach (CatalogCategoryItem category in CatalogCategories)
            category.RefreshTick();
    }

    // The relation's kind decides what the catalog shows (an axis relation: only axes).
    private void CatalogFollowKind()
    {
        if (IsBuilderCatalogPanel && CatalogGame != null && !string.Equals(catalogLoadedKey, CatalogKey(), StringComparison.Ordinal))
            _ = LoadCatalogListAsync();
    }
}
