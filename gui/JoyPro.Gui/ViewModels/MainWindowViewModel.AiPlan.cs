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

namespace JoyPro.Gui.Models
{
    // One step of an AI plan (engine relations plan ask): what it does, before and after, ticked to apply.
    public sealed partial class AiPlanStepItem : ObservableObject
    {
        public AiPlanStepItem(int index, string text, string before, string after, string error, IReadOnlyList<string> details, IReadOnlyList<string> warnings)
        {
            Index = index;
            Text = text;
            Before = before;
            After = after;
            Error = error;
            Details = details.Count == 0 ? string.Empty : string.Join(Environment.NewLine, details);
            Warnings = string.Join(Environment.NewLine, warnings);
            isSelected = error.Length == 0;
        }

        public int Index { get; }
        public string Text { get; }
        public string Before { get; }
        public string After { get; }
        public string Error { get; }
        public string Details { get; }
        public string Warnings { get; }
        public bool IsValid => Error.Length == 0;
        public bool HasError => Error.Length > 0;
        public bool HasDetails => Details.Length > 0;
        public bool HasWarnings => Warnings.Length > 0;
        public bool HasChange => Before.Length > 0 || After.Length > 0;

        // A preview step (fill): its result is chosen below it, per relation and action.
        public bool IsPreview { get; init; }

        [ObservableProperty]
        private bool isSelected;
    }
}

namespace JoyPro.Gui.ViewModels
{
    // "Manage with AI" (Relations list header, the whole profile) and "Edit with AI" (the open relation only), see
    // docs/ai-plans.md: the request becomes a checked plan (engine relations plan ask), one checkbox per step, and the
    // ticked steps run in one engine call (relations plan apply) = one history step. Without the local model a few
    // requests still work through offline rules.
    public sealed partial class MainWindowViewModel
    {
        private string aiPlanOps = "[]";

        public ObservableCollection<AiPlanStepItem> AiPlanSteps { get; } = [];

        [ObservableProperty]
        private bool isAiPlanPanelOpen;

        // Empty: the whole profile; else the relation Edit with AI may change.
        [ObservableProperty]
        private string aiPlanScope = string.Empty;

        [ObservableProperty]
        private string aiPlanText = string.Empty;

        [ObservableProperty]
        private string aiPlanStatus = string.Empty;

        public bool HasAiPlanSteps => AiPlanSteps.Count > 0;

        public bool IsAiPlanScoped => AiPlanScope.Length > 0;

        public string AiPlanTitle => IsAiPlanScoped ? T("Edit \"{0}\" with AI", AiPlanScope) : T("Manage relations with AI");

        public string AiPlanHint => IsAiPlanScoped
            ? T("Say what to change in this relation: its actions, aircraft ticks, name, groups or axis tuning, e.g. \"untick the helicopters\", \"add the A-10C\", \"deadzone 5 %\". Nothing outside this relation changes.")
            : T("Say what to do with your relations, e.g. \"append _sc to every relation that contains Star Citizen\", \"delete all relations that have no bind\", \"new relation with pitch of all games but no helicopters\". You see every step before anything changes.");

        partial void OnAiPlanScopeChanged(string value)
        {
            OnPropertyChanged(nameof(IsAiPlanScoped));
            OnPropertyChanged(nameof(AiPlanTitle));
            OnPropertyChanged(nameof(AiPlanHint));
        }

        partial void OnIsAiPlanPanelOpenChanged(bool value)
        {
            OnPropertyChanged(nameof(IsAnyRelationToolOpen));
            OnPropertyChanged(nameof(IsNoRelationToolOpen));
        }

        [RelayCommand]
        private void ShowManageWithAi()
        {
            if (IsAiPlanPanelOpen && !IsAiPlanScoped)
            {
                IsAiPlanPanelOpen = false;
                return;
            }
            OpenAiPlan(string.Empty);
        }

        [RelayCommand]
        private void ShowEditWithAi()
        {
            string relation = SelectedRelation?.Name ?? string.Empty;
            if (relation.Length == 0)
            {
                StatusText = T("Open a relation first.");
                return;
            }
            OpenAiPlan(relation);
        }

        private void OpenAiPlan(string scope)
        {
            IsAutoGroupPanelOpen = false;
            IsDescribeGroupPanelOpen = false;
            if (!string.Equals(AiPlanScope, scope, StringComparison.Ordinal))
                DiscardAiPlan();
            AiPlanScope = scope;
            IsAiPlanPanelOpen = true;
            _ = EnsureLlmStatusAsync();
        }

        [RelayCommand]
        private async Task AskAiPlanAsync()
        {
            string request = (AiPlanText ?? string.Empty).Trim();
            if (request.Length == 0)
            {
                AiPlanStatus = T("Say what to do first.");
                return;
            }
            // The setting as the engine has it: a request right after the start must not go out with "off" because the
            // status was not read yet (2.2.15: "no offline rule ... switch on the local AI model" with the model on).
            await EnsureLlmStatusAsync();
            List<string> args = BuildAiPlanAskArguments(request);
            bool withModel = LlmEnabled && LlmReady;
            // The model loads first when it is not loaded: the busy area and the chip say so.
            IsLlmStarting = withModel && !LlmServerRunning;
            string busy = !withModel ? N("Making a plan...")
                : LlmServerRunning ? T("Making a plan with the local AI model ({0})...", AiModelName)
                : T("Starting the AI model ({0}) and making a plan...", AiModelName);
            EngineCommandResult? result;
            try
            {
                result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: busy, forceText: false);
            }
            finally
            {
                IsLlmStarting = false;
            }
            AiPlanSteps.Clear();
            ClearFill();
            aiPlanOps = "[]";
            OnPropertyChanged(nameof(HasAiPlanSteps));
            if (result == null)
                return;
            _ = PollLlmServerAsync();
            ParseAiPlan(result);
        }

        internal List<string> BuildAiPlanAskArguments(string request)
        {
            List<string> args = ["relations", "plan", "ask", request, "--llm", LlmEnabled ? "auto" : "off"];
            if (IsAiPlanScoped)
            {
                args.Add("--relation");
                args.Add(AiPlanScope);
            }
            return args;
        }

        private void ParseAiPlan(EngineCommandResult result)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                JsonElement root = doc.RootElement;
                aiPlanOps = root.TryGetProperty("Ops", out JsonElement ops) && ops.ValueKind == JsonValueKind.Array ? ops.GetRawText() : "[]";
                if (root.TryGetProperty("Steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement step in steps.EnumerateArray())
                    {
                        int index = step.TryGetProperty("Index", out JsonElement i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : AiPlanSteps.Count;
                        AiPlanStepItem item = new(index, ReadJsonString(step, "Text"), ReadJsonString(step, "Before"), ReadJsonString(step, "After"),
                            ReadJsonString(step, "Error"), ReadStringArray(step, "Details"), ReadStringArray(step, "Warnings")) { IsPreview = ReadJsonBool(step, "Preview") };
                        AiPlanSteps.Add(item);
                        if (step.TryGetProperty("Fill", out JsonElement fill) && fill.ValueKind == JsonValueKind.Object)
                            ReadFill(fill);
                    }
                }
                List<string> lines = [];
                string method = ReadJsonString(root, "Method");
                if (method.Length > 0)
                    lines.Add(T("How: {0}", method));
                string reply = ReadJsonString(root, "Reply");
                bool modelUsed = root.TryGetProperty("Llm", out JsonElement llm) && llm.ValueKind == JsonValueKind.Object && ReadJsonBool(llm, "Used");
                if (reply.Length > 0)
                    lines.Add(modelUsed ? T("The local AI model says: {0}", reply) : reply);
                int valid = AiPlanSteps.Count(s => s.IsValid);
                if (AiPlanSteps.Count > 0)
                    lines.Add(T("{0} of {1} steps can run; rejected steps are unticked. Tick what you want, then Apply.", valid.ToString(CultureInfo.InvariantCulture), AiPlanSteps.Count.ToString(CultureInfo.InvariantCulture)));
                foreach (string warning in ReadStringArray(root, "Warnings"))
                    lines.Add(T("Note: {0}", warning));
                foreach (string error in ReadStringArray(root, "Errors"))
                    lines.Add(T("Error: {0}", error));
                AiPlanStatus = string.Join(Environment.NewLine, lines);
            }
            catch (JsonException)
            {
                AiPlanStatus = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
            }
            if (result.ExitCode != 0 && AiPlanSteps.Count == 0 && AiPlanStatus.Length == 0)
                AiPlanStatus = result.StandardError.Trim();
            OnPropertyChanged(nameof(HasAiPlanSteps));
        }

        [RelayCommand]
        private async Task ApplyAiPlanAsync()
        {
            List<AiPlanStepItem> chosen = AiPlanSteps.Where(s => s.IsSelected && s.IsValid && !s.IsPreview).ToList();
            string ops = BuildAiApplyOps(chosen);
            if (ops == "[]")
            {
                AiPlanStatus = T("Tick at least one step that can run.");
                return;
            }
            string file = AiPlanFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{\"ops\":" + ops + "}");
            List<string> args = ["relations", "plan", "apply", "--file", file];
            if (IsAiPlanScoped)
            {
                args.Add("--relation");
                args.Add(AiPlanScope);
            }
            string note = IsAiPlanScoped ? N("Edit with AI") : N("Manage with AI");
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: note, forceText: false, historyNote: note);
            if (result == null)
                return;
            int applied = 0;
            string newScope = AiPlanScope;
            List<string> failed = [];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement step in steps.EnumerateArray())
                    {
                        if (ReadJsonBool(step, "Ok"))
                            applied++;
                        else if (!ReadJsonBool(step, "Skipped"))
                            failed.Add(ReadJsonString(step, "Text") + ": " + ReadJsonString(step, "Error"));
                    }
                }
                string scope = ReadJsonString(doc.RootElement, "Scope");
                if (scope.Length > 0)
                    newScope = scope;
            }
            catch (JsonException)
            {
                AiPlanStatus = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
                return;
            }
            AiPlanSteps.Clear();
            ClearFill();
            aiPlanOps = "[]";
            OnPropertyChanged(nameof(HasAiPlanSteps));
            AiPlanStatus = T("Applied {0} steps in one change (Ctrl+Z undoes all of them).", applied.ToString(CultureInfo.InvariantCulture)) +
                           (failed.Count > 0 ? Environment.NewLine + T("Not applied: {0}", string.Join("; ", failed)) : string.Empty);
            AiPlanScope = newScope;
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
            if (IsAiPlanScoped)
            {
                RelationListItem? relation = Relations.FirstOrDefault(r => string.Equals(r.Name, AiPlanScope, StringComparison.Ordinal));
                if (relation != null)
                    SelectedRelation = relation;
            }
        }

        [RelayCommand]
        private void DiscardAiPlan()
        {
            ClearFill();
            AiPlanSteps.Clear();
            aiPlanOps = "[]";
            AiPlanStatus = string.Empty;
            OnPropertyChanged(nameof(HasAiPlanSteps));
        }

        [RelayCommand]
        private void CloseAiPlan()
        {
            IsAiPlanPanelOpen = false;
        }

        private static string AiPlanFile() => Path.Combine(Path.GetTempPath(), "JoyPro2", "ai-plan-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".json");
    }
}
