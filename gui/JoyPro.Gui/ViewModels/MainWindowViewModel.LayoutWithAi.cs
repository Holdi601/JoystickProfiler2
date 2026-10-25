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
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Lay out with AI" on the Binds page (docs/layout-with-ai.md): the user describes in plain words how they like their
// controls ("throttle and left-hand stick, trim on the hat, curves softer for helis"); JoyPro shows what it understood
// and every change of binds, axis curves, force feedback and the virtual joystick (engine arrange ask), the ticked ones
// are applied in one history version (engine arrange apply; Undo takes all of them back). It works offline through
// Auto Assign's planner and the phrase rules; the local AI model only helps to understand. The preferences are kept in
// the profile and used again.
public sealed partial class MainWindowViewModel
{
    // The phrase rules read English and German (with the local AI on, every language works): German examples in the
    // German app, English ones in every other language.
    public IReadOnlyList<string> LayoutExamples => Loc.Current.Code == "de" ? GermanLayoutExamples : EnglishLayoutExamples;

    private static readonly string[] EnglishLayoutExamples =
    [
        "throttle and left-hand stick", // nonloc
        "for space games use the left stick and its twist axis instead of pedals", // nonloc
        "trim on the hat, curves softer for helis", // nonloc
        "no modifiers, use a mode switch", // nonloc
        "less ffb shake for helis", // nonloc
        "vjoy for Star Citizen" // nonloc
    ];

    private static readonly string[] GermanLayoutExamples =
    [
        "Gas und linker Stick", // nonloc
        "bei Weltraumspielen linker Stick mit Twist statt Pedalen", // nonloc
        "Trimmung auf den Hat, weichere Kurven für Helis", // nonloc
        "keine Modifier, nimm einen Moduswahlschalter", // nonloc
        "weniger Rütteln bei Helis", // nonloc
        "vJoy für Star Citizen" // nonloc
    ];

    public ObservableCollection<LayoutPreferenceItem> LayoutPreferences { get; } = [];

    public ObservableCollection<string> LayoutNotUnderstood { get; } = [];

    public ObservableCollection<LayoutChangeGroupItem> LayoutChangeGroups { get; } = [];

    public ObservableCollection<string> LayoutNotes { get; } = [];

    [ObservableProperty]
    private bool isLayoutPanelOpen;

    [ObservableProperty]
    private string layoutRequest = string.Empty;

    // Also make relations for the functions of your games you do not have yet (like the Setup wizard).
    [ObservableProperty]
    private bool layoutAddMissing;

    [ObservableProperty]
    private string layoutStatus = string.Empty;

    [ObservableProperty]
    private string layoutSummary = string.Empty;

    [ObservableProperty]
    private bool layoutApplied;

    private string layoutProposalFile = string.Empty;

    public bool HasLayoutPreferences => LayoutPreferences.Count > 0;
    public bool HasLayoutPreview => LayoutChangeGroups.Count > 0;
    public bool HasLayoutNotUnderstood => LayoutNotUnderstood.Count > 0;
    public bool HasLayoutNotes => LayoutNotes.Count > 0;
    public bool CanApplyLayout => HasLayoutPreview && !LayoutApplied;

    partial void OnLayoutAddMissingChanged(bool value) => SaveAutoPlacement();

    partial void OnLayoutRequestChanged(string value) => SaveAutoPlacement();

    partial void OnLayoutAppliedChanged(bool value) => OnPropertyChanged(nameof(CanApplyLayout));

    private void ApplyLayoutSettings(AutoPlacementSettings settings)
    {
        LayoutAddMissing = settings.LayoutAddMissing;
        LayoutRequest = settings.LayoutRequest ?? string.Empty;
    }

    private void LayoutSettingsForSave(AutoPlacementSettings settings)
    {
        settings.LayoutAddMissing = LayoutAddMissing;
        settings.LayoutRequest = LayoutRequest;
    }

    [RelayCommand]
    private async Task ToggleLayoutPanelAsync()
    {
        IsLayoutPanelOpen = !IsLayoutPanelOpen;
        if (IsLayoutPanelOpen && !IsBindsStage)
            CurrentStage = UiStage.Binds;
        if (IsLayoutPanelOpen && HasProfile && !HasLayoutPreview)
            await LoadLayoutPreferencesAsync();
    }

    [RelayCommand]
    private void UseLayoutExample(string? example)
    {
        if (string.IsNullOrWhiteSpace(example))
            return;
        string text = LayoutRequest.Trim();
        LayoutRequest = text.Length == 0 ? example : text.TrimEnd(',', '.', ' ') + ", " + example;
    }

    // The preferences kept in the profile (engine arrange prefs).
    private async Task LoadLayoutPreferencesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["arrange", "prefs"], includeProfile: true, mutatesProfile: false, busyLabel: N("Reading your layout preferences..."), forceText: false, background: true);
        if (result == null || result.ExitCode != 0)
            return;
        LayoutPreferences.Clear();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    LayoutPreferences.Add(PreferenceItem(item, isNew: false));
            }
        }
        catch (JsonException)
        {
        }
        OnPropertyChanged(nameof(HasLayoutPreferences));
    }

    // The preview: what JoyPro understood and every change (nothing changes yet).
    [RelayCommand]
    private async Task AskLayoutAsync()
    {
        if (!HasProfile)
        {
            LayoutStatus = T("Open or make a profile first.");
            return;
        }
        if (LayoutRequest.Trim().Length == 0 && LayoutPreferences.Count == 0)
        {
            LayoutStatus = T("Describe how you like your controls first, e.g. \"throttle and left-hand stick\".");
            return;
        }
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2", "layout-ai", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        layoutProposalFile = Path.Combine(folder, "proposal.json");
        List<string> args = ["arrange", "ask", LayoutRequest.Trim(), "--llm", LlmEnabled ? "auto" : "off", "--proposal", layoutProposalFile];
        if (LayoutAddMissing)
            args.Add("--add-missing");
        foreach (AutoAssignDeviceItem device in AutoAssignDevices.Where(d => d.SelectedGrip != null))
            args.AddRange(["--grip", device.Device + "=" + device.SelectedGrip!.Id]);
        LayoutStatus = string.Empty;
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Reading your preferences and laying out your controls..."), forceText: false);
        if (result == null)
            return;
        if (result.ExitCode != 0)
        {
            LayoutStatus = Loc.TranslateLines((result.StandardError + Environment.NewLine + result.StandardOutput).Trim());
            return;
        }
        ShowLayoutProposal(result.StandardOutput);
    }

    internal void ShowLayoutProposal(string output)
    {
        LayoutPreferences.Clear();
        LayoutNotUnderstood.Clear();
        LayoutChangeGroups.Clear();
        LayoutNotes.Clear();
        LayoutApplied = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            HashSet<string> fresh = root.TryGetProperty("Fresh", out JsonElement freshList) && freshList.ValueKind == JsonValueKind.Array
                ? freshList.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)
                : [];
            if (root.TryGetProperty("Preferences", out JsonElement preferences) && preferences.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in preferences.EnumerateArray())
                    LayoutPreferences.Add(PreferenceItem(item, fresh.Contains(ReadJsonString(item, "Key"))));
            }
            foreach (string clause in Strings(root, "NotUnderstood"))
                LayoutNotUnderstood.Add(clause);
            foreach (string note in Strings(root, "Notes").Concat(Strings(root, "Warnings")).Distinct())
                LayoutNotes.Add(EngineText.Tr(note));
            List<LayoutChangeItem> changes = [];
            if (root.TryGetProperty("Changes", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    // The plain-word texts (OldText, NewText, ScopeText) where the engine gives them; Old / New are apply's values.
                    // A bind's before / after is a device and its control (names: only the input words are translated).
                    bool bind = ReadJsonString(item, "Kind") == "bind";
                    string Shown(string text, string value) => bind ? EngineText.InputsOnly(text.Length > 0 ? text : value)
                        : EngineText.Tr(string.Join(", ", (text.Length > 0 ? text : value).Split(", ").Select(Loc.Translate)));
                    // A bind's new place in the app's language from its parts (device, input, control, label).
                    string after = bind && ReadJsonString(item, "Input").Length > 0
                        ? ReadableNames.Device(ReadJsonString(item, "Device")) + " " + ReadableNames.Where(ReadJsonString(item, "Input"), ReadJsonString(item, "Control"), ReadJsonString(item, "Label")) +
                          (ReadJsonString(item, "Scope").Length > 0 ? " + " + ReadJsonString(item, "Scope") : string.Empty)
                        : Shown(ReadJsonString(item, "NewText"), ReadJsonString(item, "New"));
                    changes.Add(new LayoutChangeItem(ReadJsonString(item, "Id"), ReadJsonString(item, "Kind"), ReadJsonString(item, "Title"),
                        bind ? ReadJsonString(item, "Scope") : Shown(ReadJsonString(item, "ScopeText"), ReadJsonString(item, "Scope")),
                        Shown(ReadJsonString(item, "OldText"), ReadJsonString(item, "Old")), after,
                        EngineText.Tr(ReadJsonString(item, "Reason"))));
                }
            }
            // Groups: what the preferences moved first, then the places for unbound relations, the rest by kind.
            AddLayoutGroup("split", T("Relations split by aircraft class"), changes.Where(c => c.Kind == "split"));
            AddLayoutGroup("bind-moved", T("Binds that move"), changes.Where(c => c.Kind == "bind" && c.Before.Length > 0));
            AddLayoutGroup("bind-new", T("Unbound relations that get a place"), changes.Where(c => c.Kind == "bind" && c.Before.Length == 0));
            AddLayoutGroup("tuning", T("Axis curves"), changes.Where(c => c.Kind == "tuning"));
            AddLayoutGroup("ffb", T("Force feedback"), changes.Where(c => c.Kind == "ffb"));
            AddLayoutGroup("vjoy", T("Virtual joystick"), changes.Where(c => c.Kind == "vjoy"));
            if (LayoutChangeGroups.Count > 0)
                LayoutChangeGroups[0].IsExpanded = true;
            int binds = changes.Count(c => c.Kind == "bind");
            LayoutSummary = changes.Count == 0
                ? T("Nothing to change: your layout already fits these preferences.")
                : T("{0} binds, {1} axis curves, {2} force feedback settings, {3} virtual joystick settings. Untick what you do not want, then Apply.",
                    binds, changes.Count(c => c.Kind == "tuning"), changes.Count(c => c.Kind == "ffb"), changes.Count(c => c.Kind == "vjoy"));
            LayoutStatus = LayoutNotUnderstood.Count > 0
                ? (LlmEnabled ? T("JoyPro did not understand everything (see below); say it in other words.") : T("JoyPro did not understand everything (see below); say it more simply or switch on the local AI (Settings > Local AI)."))
                : string.Empty;
        }
        catch (JsonException)
        {
            LayoutStatus = T("The preview could not be read.");
        }
        OnPropertyChanged(nameof(HasLayoutPreferences));
        OnPropertyChanged(nameof(HasLayoutPreview));
        OnPropertyChanged(nameof(HasLayoutNotUnderstood));
        OnPropertyChanged(nameof(HasLayoutNotes));
        OnPropertyChanged(nameof(CanApplyLayout));
    }

    private void AddLayoutGroup(string kind, string title, IEnumerable<LayoutChangeItem> changes)
    {
        List<LayoutChangeItem> list = changes.ToList();
        if (list.Count > 0)
            LayoutChangeGroups.Add(new LayoutChangeGroupItem(kind, title, list));
    }

    private static IEnumerable<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                yield return item.GetString()!;
        }
    }

    // The preference in the user's language: the template translated, its values too (scope, function, control).
    internal static LayoutPreferenceItem PreferenceItem(JsonElement item, bool isNew)
    {
        string template = ReadJsonString(item, "Template");
        List<object?> args = [];
        if (item.TryGetProperty("Args", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            args.AddRange(list.EnumerateArray().Select(a => (object?)string.Join(", ", (a.GetString() ?? string.Empty).Split(", ").Select(w => T(w)))));  // "pitch, roll": each word
        // The scope ("Helicopters in DCS") from the class and the game, so every word is translated.
        if (template.StartsWith("{0}", StringComparison.Ordinal) && args.Count > 0)
            args[0] = ScopeText(ReadJsonString(item, "Class"), ReadJsonString(item, "Game"));
        string text = T(template, args.ToArray());
        return new LayoutPreferenceItem(ReadJsonString(item, "Key"), text, ReadJsonString(item, "Text"), isNew, ReadJsonString(item, "Source") == "ai");
    }

    private static string ScopeText(string cls, string game)
    {
        string title = cls switch
        {
            "rotary" => T("Helicopters"),
            "space" => T("Spaceships"),
            "fixed" => T("Fixed wing"),
            _ => string.Empty
        };
        if (game.Length > 0)
            return title.Length > 0 ? T("{0} in {1}", title, game) : game;
        return title.Length > 0 ? title : T("Every aircraft");
    }

    // Applies the ticked changes in one engine call = one history version (Undo takes all of it back).
    [RelayCommand]
    private async Task ApplyLayoutAsync()
    {
        if (!HasLayoutPreview || layoutProposalFile.Length == 0)
            return;
        List<string> args = ["arrange", "apply", "--proposal", layoutProposalFile];
        foreach (LayoutChangeItem change in LayoutChangeGroups.SelectMany(g => g.Changes).Where(c => !c.IsSelected))
            args.AddRange(["--skip", change.Id]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Applying your layout..."));
        if (result == null)
            return;
        if (result.ExitCode != 0)
        {
            LayoutStatus = Loc.TranslateLines((result.StandardError + Environment.NewLine + result.StandardOutput).Trim());
            return;
        }
        LayoutApplied = true;
        int chosen = LayoutChangeGroups.SelectMany(g => g.Changes).Count(c => c.IsSelected);
        LayoutStatus = T("Applied {0} changes as one step: Undo (Ctrl+Z) takes all of them back. Your preferences are kept in the profile.", chosen);
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
        await RefreshModifiersAsync();
    }

    [RelayCommand]
    private async Task UndoLayoutAsync()
    {
        if (!LayoutApplied)
            return;
        await UndoLastChangeAsync();
        LayoutApplied = false;
        LayoutStatus = T("Undone: your binds are as before.");
    }

    [RelayCommand]
    private void DiscardLayout()
    {
        LayoutChangeGroups.Clear();
        LayoutNotUnderstood.Clear();
        LayoutNotes.Clear();
        LayoutSummary = string.Empty;
        LayoutApplied = false;
        LayoutStatus = string.Empty;
        OnPropertyChanged(nameof(HasLayoutPreview));
        OnPropertyChanged(nameof(HasLayoutNotUnderstood));
        OnPropertyChanged(nameof(HasLayoutNotes));
        OnPropertyChanged(nameof(CanApplyLayout));
    }

    // Forgets a kept preference (engine arrange prefs remove), then makes the preview again when one is shown.
    [RelayCommand]
    private async Task RemoveLayoutPreferenceAsync(LayoutPreferenceItem? item)
    {
        if (item == null)
            return;
        if (!item.IsNew)
        {
            EngineCommandResult? result = await RunEngineAsync(["arrange", "prefs", "remove", item.Key], includeProfile: true, mutatesProfile: true, busyLabel: N("Forgetting the preference..."), forceText: false);
            if (result == null || result.ExitCode != 0)
                return;
        }
        LayoutPreferences.Remove(item);
        OnPropertyChanged(nameof(HasLayoutPreferences));
        if (HasLayoutPreview)
            DiscardLayout();
    }

    [RelayCommand]
    private async Task ClearLayoutPreferencesAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(["arrange", "prefs", "clear"], includeProfile: true, mutatesProfile: true, busyLabel: N("Forgetting the preferences..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;
        LayoutPreferences.Clear();
        OnPropertyChanged(nameof(HasLayoutPreferences));
        DiscardLayout();
    }
}
