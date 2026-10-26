using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The Binds page's read-only peek at the selected row's relation (docs/relation-builder.md "Binds page: what the
// relation does"): its actions grouped by game, each with its title, axis / button and its aircraft (count, the names
// on a click), and "Open in Relations". Collapsible; open / closed is remembered (list-preferences.json "BindPeekOpen").
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<BindPeekGroup> BindPeekGroups { get; } = [];

    private string bindPeekRelation = string.Empty;
    private int bindPeekVersion;

    public bool IsBindPeekOpen
    {
        get => ListPrefs.BindPeekOpen;
        set
        {
            if (ListPrefs.BindPeekOpen == value)
                return;
            ListPrefs.BindPeekOpen = value;
            SaveListPreferences();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BindPeekToggleText));
            OnPropertyChanged(nameof(ShowBindPeekEmpty));
            if (value)
                _ = LoadBindPeekAsync(SelectedBind?.RelationName ?? string.Empty);
        }
    }

    public int BindPeekActionCount => BindPeekGroups.Sum(g => g.Actions.Count);

    public bool HasBindPeekActions => BindPeekActionCount > 0;

    // Open, read, and the relation has no actions.
    public bool ShowBindPeekEmpty => IsBindPeekOpen && bindPeekRelation.Length > 0 && !HasBindPeekActions;

    // "What 'Pitch' does: 3 actions in 2 games"
    public string BindPeekTitle => BindPeekActionCount == 1
        ? T("What it does: 1 action in 1 game")
        : BindPeekGroups.Count == 1
            ? T("What it does: {0} actions in 1 game", BindPeekActionCount)
            : T("What it does: {0} actions in {1} games", BindPeekActionCount, BindPeekGroups.Count);

    public string BindPeekToggleText => IsBindPeekOpen ? T("Hide") : T("Show");

    [RelayCommand]
    private void ToggleBindPeek() => IsBindPeekOpen = !IsBindPeekOpen;

    // The relation's actions (engine relations show), only while the peek is open.
    internal async Task LoadBindPeekAsync(string relation)
    {
        int version = ++bindPeekVersion;
        if (relation.Length == 0)
        {
            SetBindPeek(string.Empty, []);
            return;
        }
        if (!IsBindPeekOpen)
            return;
        EngineCommandResult? result = await RunEngineAsync(["relations", "show", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading relation details..."), forceText: true, background: true);
        if (version != bindPeekVersion || result == null || result.ExitCode != 0)
            return;
        bool axis = IsAxisRelation(relation);
        List<BindPeekAction> actions = [];
        Dictionary<string, string> storedTitles = ParseStoredNodeTitles(result.StandardOutput);
        // The other games' titles (an MSFS or War Thunder action the profile stored without a title showed its id):
        // "relations aircraft" names every node in its game, as the relation editor shows it.
        Dictionary<string, string> gameTitles = await LoadBindPeekGameTitlesAsync(relation, result.StandardOutput);
        if (version != bindPeekVersion)
            return;
        foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = RelationNodeLine.Match(raw.TrimEnd());
            if (!match.Success)
                continue;
            string game = match.Groups["game"].Value.Trim();
            string id = match.Groups["id"].Value.Trim();
            string planesRaw = match.Groups["aircraft"].Value.Trim();
            List<string> planes = planesRaw.Equals("<none>", StringComparison.OrdinalIgnoreCase) ? []
                : planesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            // One list per game (IL-2, Star Citizen ...): the game itself is no aircraft.
            if (planes.Count == 1 && string.Equals(planes[0], game, StringComparison.OrdinalIgnoreCase))
                planes.Clear();
            string firstPlane = planes.Count > 0 ? planes[0] : planesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).FirstOrDefault() ?? "Default";
            string title = ResolveNodeTitle(game, planes.Count > 0 ? planes[0] : "Default", id, StoredNodeTitle(storedTitles, id, firstPlane));
            if (string.Equals(title, id, StringComparison.OrdinalIgnoreCase) && gameTitles.TryGetValue(game + "|" + id, out string? gameTitle) && gameTitle.Length > 0)
                title = gameTitle;
            actions.Add(new BindPeekAction(game, title.Length > 0 ? title : T("Command not in this aircraft's current list"), axis, planes, id));
        }
        SetBindPeek(relation, actions);
    }

    // "game|id" -> the action's title in its game, for the relation's nodes of games other than DCS (only asked when
    // the relation has such a node).
    private async Task<Dictionary<string, string>> LoadBindPeekGameTitlesAsync(string relation, string showOutput)
    {
        Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
        bool otherGames = showOutput.Split('\n').Select(l => RelationNodeLine.Match(l.TrimEnd())).Any(m => m.Success && !m.Groups["game"].Value.Trim().Equals("DCS", StringComparison.OrdinalIgnoreCase));
        if (!otherGames)
            return titles;
        EngineCommandResult? result = await RunEngineAsync(["relations", "aircraft", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading relation details..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return titles;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    string title = ReadJsonString(node, "Title");
                    if (title.Length > 0)
                        titles[ReadJsonString(node, "Game") + "|" + ReadJsonString(node, "Id")] = title;
                }
            }
        }
        catch (JsonException)
        {
        }
        return titles;
    }

    private void SetBindPeek(string relation, List<BindPeekAction> actions)
    {
        bindPeekRelation = relation;
        BindPeekGroups.Clear();
        foreach (IGrouping<string, BindPeekAction> game in actions.GroupBy(a => a.Game, StringComparer.OrdinalIgnoreCase))
            BindPeekGroups.Add(new BindPeekGroup(game.Key, game.ToList()));
        OnPropertyChanged(nameof(BindPeekActionCount));
        OnPropertyChanged(nameof(HasBindPeekActions));
        OnPropertyChanged(nameof(ShowBindPeekEmpty));
        OnPropertyChanged(nameof(BindPeekTitle));
    }

    // "Open in Relations": the relation editor with this relation.
    [RelayCommand]
    internal async Task OpenBindPeekInRelationsAsync()
    {
        string relation = SelectedBind?.RelationName ?? string.Empty;
        if (relation.Length == 0)
            return;
        CurrentStage = UiStage.Relations;
        await OpenBuilderRelationAsync(relation);
    }
}

// One game of the peek: "DCS (3)" and its actions.
public sealed class BindPeekGroup
{
    public BindPeekGroup(string game, IReadOnlyList<BindPeekAction> actions)
    {
        Game = game;
        Actions = actions;
    }

    public string Game { get; }

    public string Header => DisplayText.Game(Game) + " (" + Actions.Count + ")";

    public IReadOnlyList<BindPeekAction> Actions { get; }
}

// One action of the peek: its title, axis / button, its aircraft ("12 aircraft", the names on a click).
public sealed partial class BindPeekAction : ObservableObject
{
    public BindPeekAction(string game, string title, bool isAxis, IReadOnlyList<string> planes, string id = "")
    {
        Id = id ?? string.Empty;
        Game = game;
        Title = title;
        IsAxis = isAxis;
        Planes = planes;
    }

    public string Game { get; }

    // The action's id in its game ("Move to another button..." moves it by game:id).
    public string Id { get; }

    public string Title { get; }

    public bool IsAxis { get; }

    public IReadOnlyList<string> Planes { get; }

    public string KindText => IsAxis ? T("Axis") : T("Button");

    public bool HasPlanes => Planes.Count > 0;

    public string PlanesText => Plural("{0} aircraft", "{0} aircraft", Planes.Count);

    public string PlaneNames => string.Join(", ", Planes);

    [ObservableProperty]
    private bool isExpanded;

    // The search's match line under the bind row was clicked for this action: it is marked.
    [ObservableProperty]
    private bool isSearchHit;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
