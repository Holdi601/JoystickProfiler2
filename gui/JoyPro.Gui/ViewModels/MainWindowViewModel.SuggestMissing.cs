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

// "Suggest missing" (docs/relation-builder.md): the same function in the games and aircraft the open relation has no
// action for yet, first; actions another relation uses apart ("Used in other relations", Move here); the rest (more
// actions for aircraft the relation covers already) last. Tick and "Add ticked", or Add per row. No AI: engine
// relations suggest (Missing, NewGame, NewPlanes). For the whole profile: "Suggest missing for every relation"
// (relations suggest --all --missing): a review list, relation by relation, of what each lacks.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<SuggestionSectionItem> BuilderSuggestionSections { get; } = [];

    public int BuilderMissingCount => BuilderSuggestions.Count(s => s.IsMissing && !s.HasConflict);

    public int BuilderTickedCount => BuilderSuggestions.Count(s => s.IsChecked && !s.HasConflict);

    public bool HasBuilderTicked => BuilderTickedCount > 0;

    public string BuilderAddTickedLabel => T("Add ticked ({0})", BuilderTickedCount);

    // The open relation's header button: "Suggest missing (12)".
    public string SuggestMissingLabel => allBuilderSuggestions.Count > 0 || !IsBuilderSuggestLoading
        ? (BuilderMissingCount > 0 ? T("Suggest missing ({0})", BuilderMissingCount) : T("Suggest missing"))
        : T("Suggest missing");

    private void RebuildBuilderSuggestionSections()
    {
        BuilderSuggestionSections.Clear();
        static IEnumerable<BuilderSuggestionItem> Ordered(IEnumerable<BuilderSuggestionItem> items) =>
            items.OrderByDescending(s => s.IsHigh ? 2 : s.IsMedium ? 1 : 0).ThenByDescending(s => s.Score).ThenBy(s => s.GameLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase);
        List<BuilderSuggestionItem> newGames = Ordered(BuilderSuggestions.Where(s => s.IsNewGame && !s.HasConflict)).ToList();
        List<BuilderSuggestionItem> newAircraft = Ordered(BuilderSuggestions.Where(s => s.IsNewAircraft && !s.HasConflict)).ToList();
        List<BuilderSuggestionItem> elsewhere = Ordered(BuilderSuggestions.Where(s => s.HasConflict)).ToList();
        List<BuilderSuggestionItem> more = Ordered(BuilderSuggestions.Where(s => !s.IsMissing && !s.HasConflict)).ToList();
        if (newGames.Count > 0)
            BuilderSuggestionSections.Add(new SuggestionSectionItem(T("Games not in this relation yet"), T("The same function in a game the relation has no action of"), newGames, true));
        if (newAircraft.Count > 0)
            BuilderSuggestionSections.Add(new SuggestionSectionItem(T("Aircraft not in this relation yet"), T("The same function for aircraft the relation has no action for"), newAircraft, true));
        if (elsewhere.Count > 0)
            BuilderSuggestionSections.Add(new SuggestionSectionItem(T("Used in other relations"), T("Fits, but another relation has it: Move here takes it over"), elsewhere, false));
        if (more.Count > 0)
            BuilderSuggestionSections.Add(new SuggestionSectionItem(T("More for aircraft already in it"), T("Other actions of the same function for aircraft the relation covers"), more, true));
        OnPropertyChanged(nameof(BuilderMissingCount));
        OnPropertyChanged(nameof(SuggestMissingLabel));
    }

    private void OnBuilderSuggestionTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuilderSuggestionItem.IsChecked))
            OnBuilderSuggestionTicksChanged();
    }

    private void OnBuilderSuggestionTicksChanged()
    {
        OnPropertyChanged(nameof(BuilderTickedCount));
        OnPropertyChanged(nameof(HasBuilderTicked));
        OnPropertyChanged(nameof(BuilderAddTickedLabel));
        OnPropertyChanged(nameof(BuilderMissingCount));
        OnPropertyChanged(nameof(SuggestMissingLabel));
    }

    // The open relation's "Suggest missing": the suggestions panel, freshly asked.
    [RelayCommand]
    internal async Task SuggestMissingAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Open a relation first (or use 'Suggest missing for every relation').");
            return;
        }
        SetBuilderPanel(BuilderPanelKind.Suggested);
        await LoadBuilderSuggestionsAsync(SelectedRelation.Name);
        StatusText = BuilderMissingCount > 0
            ? T("{0} actions of games and aircraft '{1}' does not have yet.", BuilderMissingCount, SelectedRelation.Name)
            : BuilderSuggestions.Any(s => s.HasConflict) ? T("'{0}' lacks nothing JoyPro knows that no other relation uses.", SelectedRelation.Name)
            : T("'{0}' lacks nothing JoyPro knows.", SelectedRelation.Name);
    }

    [RelayCommand]
    private async Task AddTickedBuilderSuggestionsAsync()
    {
        List<BuilderSuggestionItem> chosen = BuilderSuggestions.Where(s => s.IsChecked && !s.HasConflict).ToList();
        if (chosen.Count == 0)
            return;
        List<string> options = [];
        foreach (BuilderSuggestionItem item in chosen)
        {
            options.Add("--add");
            options.Add(item.NodeSpec);
            RemoveBuilderSuggestionRow(item);
        }
        await ApplyToBuilderRelationAsync(options, T("Adding {0} actions...", chosen.Count), Plural("{0} suggested action added.", "{0} suggested actions added.", chosen.Count));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Suggest missing for every relation (a review list)
    // ---------------------------------------------------------------------------------------------------------

    public ObservableCollection<MissingReviewRelationItem> MissingReview { get; } = [];

    [ObservableProperty]
    private bool isMissingReviewLoading;

    [ObservableProperty]
    private string missingReviewStatus = string.Empty;

    public bool IsBuilderMissingAllPanel => builderPanel == BuilderPanelKind.MissingAll;

    public bool HasMissingReview => MissingReview.Count > 0;

    public int MissingReviewTickedCount => MissingReview.Sum(r => r.Items.Count(i => i.IsChecked));

    public bool HasMissingReviewTicked => MissingReviewTickedCount > 0;

    public string MissingReviewAddLabel => T("Add ticked ({0})", MissingReviewTickedCount);

    [RelayCommand]
    internal async Task SuggestMissingForAllAsync()
    {
        SetBuilderPanel(BuilderPanelKind.MissingAll);
        OnPropertyChanged(nameof(IsBuilderMissingAllPanel));
        IsMissingReviewLoading = true;
        MissingReviewStatus = T("Checking every relation for the games and aircraft it lacks (this takes a moment with a big profile)...");
        try
        {
            EngineCommandResult? result = await RunEngineAsync(["relations", "suggest", "--all", "--missing", "--min", "medium", "--per-relation", "25"],
                includeProfile: true, mutatesProfile: false, busyLabel: T("Suggest missing for every relation..."), forceText: false, background: true);
            ClearMissingReview();
            if (result == null || result.ExitCode != 0)
            {
                MissingReviewStatus = T("Could not check the relations: {0}", result?.StandardError.Trim() ?? StatusText);
                return;
            }
            ParseMissingReview(result.StandardOutput);
            int items = MissingReview.Sum(r => r.Items.Count);
            int elsewhere = MissingReview.Sum(r => r.UsedElsewhere);
            MissingReviewStatus = MissingReview.Count == 0
                ? T("No relation lacks anything JoyPro knows (medium or high confidence) that no other relation uses.")
                : T("{0} relations can grow: {1} actions of games and aircraft they do not have yet, none used by another relation.", MissingReview.Count, items) +
                  (elsewhere > 0 ? " " + T("{0} more fit but are used elsewhere: open the relation to move them.", elsewhere) : string.Empty);
        }
        finally
        {
            IsMissingReviewLoading = false;
        }
    }

    private void ClearMissingReview()
    {
        foreach (MissingReviewRelationItem relation in MissingReview)
            relation.TicksChanged -= OnMissingReviewTicksChanged;
        MissingReview.Clear();
        OnMissingReviewTicksChanged(this, EventArgs.Empty);
    }

    internal void ParseMissingReview(string output)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (!doc.RootElement.TryGetProperty("Relations", out JsonElement relations) || relations.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement relation in relations.EnumerateArray())
            {
                List<BuilderSuggestionItem> items = [];
                if (relation.TryGetProperty("Items", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        items.Add(new BuilderSuggestionItem(ReadJsonString(item, "Game"), ReadJsonString(item, "Id"), ReadJsonString(item, "Title"), ReadStringArray(item, "Planes"),
                            ReadJsonString(item, "Confidence"), ReadJsonInt(item, "Score"), ReadJsonString(item, "Why"), ReadStringArray(item, "Reasons"), ReadStringArray(item, "InOtherRelations"))
                        {
                            Missing = ReadJsonString(item, "Missing"),
                            NewPlanes = ReadStringArray(item, "NewPlanes")
                        });
                    }
                }
                if (items.Count == 0)
                    continue;
                MissingReviewRelationItem row = new(ReadJsonString(relation, "Relation"), ReadJsonBool(relation, "IsAxis"), items,
                    ReadJsonInt(relation, "NewGames"), ReadJsonInt(relation, "UsedElsewhere"), ReadJsonInt(relation, "Suggestions"));
                row.TicksChanged += OnMissingReviewTicksChanged;
                // The first relations open; the others show their summary (hundreds of relations stay quick).
                row.IsExpanded = MissingReview.Count < 3;
                MissingReview.Add(row);
            }
        }
        catch (JsonException)
        {
            MissingReviewStatus = T("Could not read the result.");
        }
        OnPropertyChanged(nameof(HasMissingReview));
    }

    private void OnMissingReviewTicksChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MissingReviewTickedCount));
        OnPropertyChanged(nameof(HasMissingReviewTicked));
        OnPropertyChanged(nameof(MissingReviewAddLabel));
        OnPropertyChanged(nameof(HasMissingReview));
    }

    // The ticked actions into their relations (one engine call per relation; each is one undo step).
    [RelayCommand]
    internal async Task AddTickedMissingReviewAsync()
    {
        List<MissingReviewRelationItem> relations = MissingReview.Where(r => r.Items.Any(i => i.IsChecked)).ToList();
        int added = 0;
        int failed = 0;
        foreach (MissingReviewRelationItem relation in relations)
        {
            List<BuilderSuggestionItem> chosen = relation.Items.Where(i => i.IsChecked).ToList();
            List<string> args = ["relations", "suggest", "apply", relation.Name];
            foreach (BuilderSuggestionItem item in chosen)
                args.AddRange(["--add", item.NodeSpec]);
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
                busyLabel: T("Adding {0} actions to '{1}'...", chosen.Count, relation.Name));
            if (result == null || result.ExitCode != 0)
            {
                failed++;
                continue;
            }
            added += chosen.Count;
            relation.RemoveItems(chosen);
            if (relation.Items.Count == 0)
            {
                relation.TicksChanged -= OnMissingReviewTicksChanged;
                MissingReview.Remove(relation);
            }
        }
        OnMissingReviewTicksChanged(this, EventArgs.Empty);
        await RefreshRelationsAsync();
        StatusText = T("{0} actions added to {1} relations. Ctrl+Z undoes them one relation at a time.", added, relations.Count - failed) +
                     (failed > 0 ? " " + T("{0} could not be changed.", failed) : string.Empty);
        MissingReviewStatus = StatusText;
    }

    [RelayCommand]
    private void TickAllMissingReview()
    {
        foreach (MissingReviewRelationItem relation in MissingReview)
            relation.IsChecked = true;
    }

    [RelayCommand]
    private void ClearMissingReviewTicks()
    {
        foreach (MissingReviewRelationItem relation in MissingReview)
            relation.IsChecked = false;
    }

    [RelayCommand]
    private void OpenMissingReviewRelation(MissingReviewRelationItem? relation)
    {
        if (relation == null)
            return;
        SelectRelationByName(relation.Name);
        _ = SuggestMissingAsync();
    }
}
