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

    partial void OnCatalogFilterTextChanged(string value) => ApplyCatalogFilter();

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
            catch (JsonException)
            {
                CatalogStatus = T("Could not read the catalog.");
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

    private void SetCatalogRows(List<CatalogRowItem> rows, List<string> categoryOrder)
    {
        foreach (CatalogRowItem row in catalogRows)
            row.Row.PropertyChanged -= OnCatalogRowChanged;
        catalogRows.Clear();
        catalogRows.AddRange(rows);
        foreach (CatalogRowItem row in catalogRows)
            row.Row.PropertyChanged += OnCatalogRowChanged;
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
        foreach (string name in categoryOrder.Concat(byCategory.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (byCategory.TryGetValue(name, out List<CatalogRowItem>? list))
                CatalogCategories.Add(new CatalogCategoryItem(name, list));
        }
        MarkBuilderRowsInRelation();
        ApplyCatalogFilter();
        OnPropertyChanged(nameof(HasCatalogRows));
        OnCatalogCheckedChanged();
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
        foreach (CatalogCategoryItem category in CatalogCategories)
        {
            bool nameHit = filtering && words.All(w => category.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            List<CatalogRowItem> matching = category.Rows.Where(r => (!CatalogOnlyFree || !r.IsUsed) &&
                (!filtering || nameHit || words.All(w => (r.Row.Title + " " + r.Row.Id).Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
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
