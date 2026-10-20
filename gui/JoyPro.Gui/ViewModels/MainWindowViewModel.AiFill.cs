using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Models
{
    // One proposed action of a fill (engine EngineRelationFill): the aircraft, what it is, which path found it
    // (Rules / AI / both) and why.
    public sealed partial class AiFillActionItem : ObservableObject
    {
        public AiFillActionItem(AiFillRelationItem owner, string spec, string game, string plane, string title, string category, string source, string confidence, string why, bool selected)
        {
            Owner = owner;
            Spec = spec;
            Game = game;
            Plane = plane;
            Title = title;
            Category = category;
            Source = source;
            Confidence = confidence;
            Why = why;
            isSelected = selected;
        }

        public AiFillRelationItem Owner { get; }
        public string Spec { get; }
        public string Game { get; }
        public string Plane { get; }
        public string Title { get; }
        public string Category { get; }
        public string Source { get; }
        public string Confidence { get; }
        public string Why { get; }
        public bool IsAmbiguous => Confidence == "ambiguous";
        public string Where => Game == "DCS" ? Plane : Game;
        public string Line => Title + (Category.Length > 0 ? " [" + Category + "]" : string.Empty);
        public string SourceText => (Source == "both" ? "Rules + AI" : Source == "AI" ? "AI" : "Rules") + " · " + Confidence;

        [ObservableProperty]
        private bool isSelected;

        [ObservableProperty]
        private bool isVisible = true;

        partial void OnIsSelectedChanged(bool value) => Owner.ChildChanged();
    }

    // A relation of a fill preview: its proposed actions, a checkbox for all of them.
    public sealed partial class AiFillRelationItem : ObservableObject
    {
        private bool syncing;

        public AiFillRelationItem(string relation, IReadOnlyList<string> noMatch)
        {
            Relation = relation;
            NoMatch = noMatch.Count == 0 ? string.Empty : string.Join(", ", noMatch);
        }

        public string Relation { get; }
        public string NoMatch { get; }
        public bool HasNoMatch => NoMatch.Length > 0;
        public ObservableCollection<AiFillActionItem> Actions { get; } = [];
        public string Header => Relation + "  (" + Actions.Count(a => a.IsSelected).ToString(CultureInfo.InvariantCulture) + " / " + Actions.Count.ToString(CultureInfo.InvariantCulture) + ")";
        public bool HasAmbiguous => Actions.Any(a => a.IsAmbiguous);

        [ObservableProperty]
        private bool? isSelected = true;

        [ObservableProperty]
        private bool isVisible = true;

        [ObservableProperty]
        private bool isExpanded;

        partial void OnIsSelectedChanged(bool? value)
        {
            if (syncing || value == null)
                return;
            syncing = true;
            foreach (AiFillActionItem action in Actions.Where(a => a.IsVisible))
                action.IsSelected = value.Value;
            syncing = false;
            OnPropertyChanged(nameof(Header));
        }

        internal void ChildChanged()
        {
            if (syncing)
                return;
            syncing = true;
            int on = Actions.Count(a => a.IsSelected);
            IsSelected = on == 0 ? false : on == Actions.Count ? true : null;
            syncing = false;
            OnPropertyChanged(nameof(Header));
        }
    }
}

namespace JoyPro.Gui.ViewModels
{
    // The preview of a fill step (Manage with AI): a summary on top, the relations (expandable) with their actions,
    // a checkbox per relation and per action, filters by aircraft and confidence. Apply hands the ticked actions back
    // to the engine as add steps (one history step with the plan's other ticked steps).
    public sealed partial class MainWindowViewModel
    {
        private string AllAircraftChoice => T("All aircraft");

        public ObservableCollection<AiFillRelationItem> AiFillRelations { get; } = [];

        public ObservableCollection<string> AiFillAircraftChoices { get; } = [];

        public IReadOnlyList<string> AiFillConfidenceChoices { get; } = ["all", "high", "medium", "ambiguous"];

        [ObservableProperty]
        private string aiFillSummary = string.Empty;

        [ObservableProperty]
        private string aiFillAircraftFilter = string.Empty;

        [ObservableProperty]
        private string aiFillConfidenceFilter = "all";

        public bool HasAiFill => AiFillRelations.Count > 0;

        partial void OnAiFillAircraftFilterChanged(string value) => ApplyFillFilter();

        partial void OnAiFillConfidenceFilterChanged(string value) => ApplyFillFilter();

        private void ClearFill()
        {
            AiFillRelations.Clear();
            AiFillAircraftChoices.Clear();
            AiFillSummary = string.Empty;
            OnPropertyChanged(nameof(HasAiFill));
        }

        private void ReadFill(JsonElement fill)
        {
            AiFillSummary = ReadJsonString(fill, "Summary");
            HashSet<string> aircraft = new(StringComparer.OrdinalIgnoreCase);
            if (fill.TryGetProperty("Relations", out JsonElement relations) && relations.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement relation in relations.EnumerateArray())
                {
                    AiFillRelationItem item = new(ReadJsonString(relation, "Relation"), ReadStringArray(relation, "NoMatch"));
                    if (relation.TryGetProperty("Actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement action in actions.EnumerateArray())
                        {
                            AiFillActionItem a = new(item, ReadJsonString(action, "Spec"), ReadJsonString(action, "Game"), ReadJsonString(action, "Plane"), ReadJsonString(action, "Title"),
                                ReadJsonString(action, "Category"), ReadJsonString(action, "Source"), ReadJsonString(action, "Confidence"), ReadJsonString(action, "Why"), ReadJsonBool(action, "Selected"));
                            item.Actions.Add(a);
                            aircraft.Add(a.Where);
                        }
                    }
                    if (item.Actions.Count == 0)
                        continue;
                    item.ChildChanged();
                    AiFillRelations.Add(item);
                }
            }
            AiFillAircraftChoices.Add(AllAircraftChoice);
            foreach (string plane in aircraft.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                AiFillAircraftChoices.Add(plane);
            AiFillAircraftFilter = AllAircraftChoice;
            AiFillConfidenceFilter = "all";
            ApplyFillFilter();
            OnPropertyChanged(nameof(HasAiFill));
        }

        private void ApplyFillFilter()
        {
            foreach (AiFillRelationItem relation in AiFillRelations)
            {
                foreach (AiFillActionItem action in relation.Actions)
                {
                    action.IsVisible = (string.IsNullOrEmpty(AiFillAircraftFilter) || AiFillAircraftFilter == AllAircraftChoice || string.Equals(action.Where, AiFillAircraftFilter, StringComparison.OrdinalIgnoreCase)) &&
                                       (AiFillConfidenceFilter == "all" || action.Confidence == AiFillConfidenceFilter ||
                                        (AiFillConfidenceFilter == "medium" && action.Confidence == "high"));
                }
                relation.IsVisible = relation.Actions.Any(a => a.IsVisible);
            }
        }

        // The plan's ticked steps plus one add step per relation with its ticked fill actions.
        internal string BuildAiApplyOps(IReadOnlyList<AiPlanStepItem> chosen)
        {
            JsonArray result = [];
            JsonArray? ops = JsonNode.Parse(aiPlanOps) as JsonArray;
            foreach (AiPlanStepItem step in chosen)
            {
                if (ops != null && step.Index >= 0 && step.Index < ops.Count && ops[step.Index] != null)
                    result.Add(ops[step.Index]!.DeepClone());
            }
            foreach (AiFillRelationItem relation in AiFillRelations)
            {
                List<string> specs = relation.Actions.Where(a => a.IsSelected && a.Spec.Length > 0).Select(a => a.Spec).Distinct().ToList();
                if (specs.Count == 0)
                    continue;
                JsonArray list = [];
                foreach (string spec in specs)
                    list.Add(spec);
                result.Add(new JsonObject { ["op"] = "add", ["relation"] = relation.Relation, ["specs"] = list });
            }
            return result.ToJsonString();
        }
    }
}
