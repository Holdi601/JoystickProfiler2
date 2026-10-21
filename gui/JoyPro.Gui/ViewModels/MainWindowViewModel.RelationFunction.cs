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
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The function chip under the open relation's name (docs/relation-builder.md "The function chip"). The engine
// (relations suggest: FunctionState) shows a function only when it really covers the relation: (nearly) all of its
// actions are it, its name is it, or the user picked it. A relation whose aircraft use it for different functions (a
// button position, like the real cockpit) gets the neutral "Different functions per aircraft" chip instead. A click
// opens a small picker: a catalog function, "None" or "Automatic" (engine relations function --set/--none/--auto).
public sealed partial class MainWindowViewModel
{
    // set, cleared, named, covered, mixed, unknown (engine EngineRelationPurpose); empty while nothing is loaded.
    [ObservableProperty]
    private string builderFunctionState = string.Empty;

    [ObservableProperty]
    private string builderFunctionQuery = string.Empty;

    private readonly Dictionary<bool, List<RelationFunctionChoice>> builderFunctionCatalog = [];

    public ObservableCollection<RelationFunctionChoice> BuilderFunctionChoices { get; } = [];

    public bool IsBuilderFunctionMixed => BuilderFunctionState == "mixed";

    public bool IsBuilderFunctionSetByUser => BuilderFunctionState is "set" or "cleared";

    // The chip shows once the engine said what the relation is (a function, a button position, or none: then a small
    // "Set function" chip so the user can still pick one).
    public bool ShowBuilderFunctionChip => HasRelationSelected && BuilderFunctionState.Length > 0;

    public string BuilderFunctionChipText =>
        HasBuilderFunction ? T("Function: {0}", BuilderFunctionText)
        : IsBuilderFunctionMixed ? T("Different functions per aircraft")
        : BuilderFunctionState == "cleared" ? T("Function: none")
        : T("Set function");

    public string BuilderFunctionChipTooltip =>
        HasBuilderFunction
            ? T("What this relation does in every aircraft and game: '{0}' ({1}). JoyPro uses it to suggest the same function in your other games and aircraft (Suggested, AI fill) and to place the relation on a fitting input (Auto Assign). Click to change it.",
                BuilderFunctionText, BuilderFunctionSourceText)
            : IsBuilderFunctionMixed
                ? T("This relation is a button position: each aircraft uses it for its own function. JoyPro suggests no function for it (only the same input of closely related aircraft). Click to give it one function anyway.")
                : BuilderFunctionState == "cleared"
                    ? T("You said this relation has no single function: JoyPro suggests nothing by function for it. Click to change it.")
                    : T("JoyPro does not know one function for this relation. Click to pick the function it stands for, so suggestions can find it in your other games and aircraft.");

    private string BuilderFunctionSourceText => BuilderFunctionState switch
    {
        "set" => T("picked by you"),
        "named" => T("from its name"),
        _ => T("all of its actions are this function")
    };

    public bool HasBuilderFunctionChoices => BuilderFunctionChoices.Count > 0;

    partial void OnBuilderFunctionStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsBuilderFunctionMixed));
        OnPropertyChanged(nameof(IsBuilderFunctionSetByUser));
        OnPropertyChanged(nameof(ShowBuilderFunctionChip));
        OnPropertyChanged(nameof(BuilderSuggestEmptyText));
        OnBuilderFunctionChipChanged();
    }

    private void OnBuilderFunctionChipChanged()
    {
        OnPropertyChanged(nameof(BuilderFunctionChipText));
        OnPropertyChanged(nameof(BuilderFunctionChipTooltip));
    }

    partial void OnBuilderFunctionQueryChanged(string value) => FilterBuilderFunctionChoices();

    // relations suggest's verdict: the chip's text and kind. Older engines send no FunctionState: a slot then counts as
    // the relation's function.
    private void ReadBuilderFunction(JsonElement root)
    {
        string state = ReadJsonString(root, "FunctionState");
        string label = ReadJsonString(root, "FunctionLabel");
        if (label.Length == 0)
            label = ReadJsonString(root, "SlotName");
        if (state.Length == 0)
            state = label.Length > 0 ? "covered" : "unknown";
        bool hasFunction = state is "set" or "named" or "covered";
        BuilderFunctionText = hasFunction ? label : string.Empty;
        BuilderFunctionState = state;
        OnBuilderFunctionChipChanged();
    }

    // The flyout opens: the catalog functions of the relation's kind (read once per kind).
    [RelayCommand]
    internal async Task OpenBuilderFunctionPickerAsync()
    {
        RelationListItem? relation = SelectedRelation;
        if (relation == null)
            return;
        BuilderFunctionQuery = string.Empty;
        if (!builderFunctionCatalog.ContainsKey(relation.IsAxis))
        {
            EngineCommandResult? result = await RunEngineAsync(["relations", "function", "--list", relation.IsAxis ? "--axis" : "--button"], includeProfile: false, mutatesProfile: false,
                busyLabel: N("Reading the function catalog..."), forceText: false, background: true);
            List<RelationFunctionChoice> list = [];
            if (result != null && result.ExitCode == 0)
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in doc.RootElement.EnumerateArray())
                        {
                            string id = ReadJsonString(item, "Id");
                            if (id.Length > 0)
                                list.Add(new RelationFunctionChoice(id, ReadJsonString(item, "Name")));
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
            if (list.Count > 0)
                builderFunctionCatalog[relation.IsAxis] = list;
        }
        FilterBuilderFunctionChoices();
    }

    private void FilterBuilderFunctionChoices()
    {
        BuilderFunctionChoices.Clear();
        if (SelectedRelation == null || !builderFunctionCatalog.TryGetValue(SelectedRelation.IsAxis, out List<RelationFunctionChoice>? all))
        {
            OnPropertyChanged(nameof(HasBuilderFunctionChoices));
            return;
        }
        string[] words = (BuilderFunctionQuery ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (RelationFunctionChoice choice in all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Id.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(200))
            BuilderFunctionChoices.Add(choice);
        OnPropertyChanged(nameof(HasBuilderFunctionChoices));
    }

    [RelayCommand]
    private Task SetBuilderFunctionAsync(RelationFunctionChoice? choice) =>
        choice == null ? Task.CompletedTask : ChangeBuilderFunctionAsync(["--set", choice.Id], T("'{0}' now stands for '{1}'. Ctrl+Z undoes it.", SelectedRelation?.Name ?? string.Empty, choice.Name));

    [RelayCommand]
    private Task ClearBuilderFunctionAsync() =>
        ChangeBuilderFunctionAsync(["--none"], T("'{0}' has no single function now: each aircraft uses it for its own. Ctrl+Z undoes it.", SelectedRelation?.Name ?? string.Empty));

    [RelayCommand]
    private Task AutoBuilderFunctionAsync() =>
        ChangeBuilderFunctionAsync(["--auto"], T("JoyPro reads the function of '{0}' from its actions again. Ctrl+Z undoes it.", SelectedRelation?.Name ?? string.Empty));

    private async Task ChangeBuilderFunctionAsync(string[] change, string done)
    {
        RelationListItem? relation = SelectedRelation;
        if (relation == null)
            return;
        EngineCommandResult? result = await RunEngineAsync(["relations", "function", relation.Name, .. change], includeProfile: true, mutatesProfile: true,
            busyLabel: N("Changing the relation's function..."));
        if (result == null || result.ExitCode != 0)
            return;
        await LoadBuilderSuggestionsAsync(relation.Name);
        StatusText = done;
    }
}

// One function of the catalog in the function picker.
public sealed record RelationFunctionChoice(string Id, string Name);
