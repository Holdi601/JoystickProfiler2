using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// "Move to another button..." (tester question on 2.4.1: "I simply want to change the 'Toggle Master Warning' from one
// button to another"). In JoyPro a relation is one input, so changing the relation's input would move every action of
// it. This moves one action (one game's action, with its aircraft) instead: press the new button or pick device and
// input; the action joins the relation that already uses that input, else gets a relation of its own named after it
// (engine relations item move-to-input, one undo step). Reachable from an action of the relation editor (context menu)
// and from the Binds page (the bind row's context menu and each action of the "What it does" peek).
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<MoveActionChoice> MoveActionChoices { get; } = [];

    [ObservableProperty]
    private bool isMoveActionOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveActionTitle))]
    [NotifyPropertyChangedFor(nameof(HasMoveActionChoice))]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveAction))]
    private MoveActionChoice? selectedMoveAction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveAction))]
    private string moveActionDevice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyMoveAction))]
    private string moveActionInput = string.Empty;

    [ObservableProperty]
    private bool moveActionKeepModifiers = true;

    [ObservableProperty]
    private string moveActionPreview = string.Empty;

    [ObservableProperty]
    private string moveActionStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveActionRecordLabel))]
    private bool isMoveActionRecording;

    private string moveActionRelation = string.Empty;
    private string moveActionModifiers = string.Empty;
    private int moveActionPreviewVersion;
    private CancellationTokenSource? moveActionRecordCts;

    public string MoveActionTitle => SelectedMoveAction == null
        ? T("Move an action to another button")
        : T("Move '{0}' to another button", SelectedMoveAction.Title);

    public string MoveActionIntro => T("'{0}' is one input with all its actions. Only the action you pick moves; the others stay on their button.", moveActionRelation);

    public bool HasMoveActionChoice => SelectedMoveAction != null;

    // Several actions (opened from a bind row): the user picks the one to move.
    public bool ShowMoveActionPicker => MoveActionChoices.Count > 1;

    public bool HasMoveActionModifiers => moveActionModifiers.Length > 0;

    public string MoveActionKeepModifiersText => T("Keep the modifiers ({0})", moveActionModifiers);

    public bool HasMoveActionPreview => MoveActionPreview.Length > 0;

    public bool HasMoveActionStatus => MoveActionStatus.Length > 0;

    public string MoveActionRecordLabel => IsMoveActionRecording ? T("Press the new button now.") : T("Press the new button");

    public bool CanApplyMoveAction => SelectedMoveAction != null && MoveActionDevice.Trim().Length > 0 && MoveActionInput.Trim().Length > 0;

    partial void OnMoveActionPreviewChanged(string value) => OnPropertyChanged(nameof(HasMoveActionPreview));

    partial void OnMoveActionStatusChanged(string value) => OnPropertyChanged(nameof(HasMoveActionStatus));

    partial void OnSelectedMoveActionChanged(MoveActionChoice? value) => _ = PreviewMoveActionAsync();

    partial void OnMoveActionDeviceChanged(string value) => _ = PreviewMoveActionAsync();

    partial void OnMoveActionInputChanged(string value) => _ = PreviewMoveActionAsync();

    partial void OnMoveActionKeepModifiersChanged(bool value) => _ = PreviewMoveActionAsync();

    // The relation editor: an action's context menu.
    [RelayCommand]
    internal void OpenMoveActionForNode(RelationNodeItem? node)
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        if (node == null || relation.Length == 0)
            return;
        string title = node.TitlesSummary.Length > 0 && !node.TitlesSummary.StartsWith('<') ? node.TitlesSummary : node.Id;
        OpenMoveAction(relation, [new MoveActionChoice(title, node.Game, BuildNodeSpec(node.Game, node.Id, node.ActivePlanesText),
            node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? node.PlanesSummary : string.Empty)]);
    }

    // The Binds page: an action of the "What it does" peek.
    [RelayCommand]
    internal void OpenMoveActionForPeek(BindPeekAction? action)
    {
        string relation = SelectedBind?.RelationName ?? string.Empty;
        if (action == null || relation.Length == 0 || action.Id.Length == 0)
            return;
        OpenMoveAction(relation, [ChoiceOf(action)]);
    }

    // The Binds page: the bind row's context menu (every action of the relation to pick from).
    [RelayCommand]
    internal async Task OpenMoveActionForBindAsync(BindListItem? bind)
    {
        bind ??= SelectedBind;
        if (bind == null || bind.RelationName.Length == 0)
            return;
        List<MoveActionChoice> choices = await LoadMoveActionChoicesAsync(bind.RelationName);
        if (choices.Count == 0)
        {
            StatusText = T("'{0}' has no action to move.", bind.RelationName);
            return;
        }
        OpenMoveAction(bind.RelationName, choices);
    }

    private void OpenMoveAction(string relation, List<MoveActionChoice> choices)
    {
        moveActionRelation = relation;
        RelationListItem? item = allRelations.FirstOrDefault(r => string.Equals(r.Name, relation, StringComparison.OrdinalIgnoreCase));
        moveActionModifiers = item == null ? string.Empty : string.Join(" + ", item.ModifierChips);
        MoveActionChoices.Clear();
        foreach (MoveActionChoice choice in choices)
            MoveActionChoices.Add(choice);
        MoveActionPreview = string.Empty;
        MoveActionStatus = string.Empty;
        MoveActionKeepModifiers = true;
        // Device and input start empty: the new button is pressed or picked; the device list starts at the current one.
        MoveActionDevice = item != null && item.IsBound ? item.BindDeviceFull : string.Empty;
        MoveActionInput = string.Empty;
        SelectedMoveAction = MoveActionChoices.FirstOrDefault();
        OnPropertyChanged(nameof(ShowMoveActionPicker));
        OnPropertyChanged(nameof(HasMoveActionModifiers));
        OnPropertyChanged(nameof(MoveActionKeepModifiersText));
        OnPropertyChanged(nameof(MoveActionIntro));
        OnPropertyChanged(nameof(MoveActionTitle));
        IsMoveActionOpen = true;
        JoyProLog.Info("MoveAction", "Opened for '" + relation + "' (" + choices.Count + " action(s))");
    }

    private static MoveActionChoice ChoiceOf(BindPeekAction action) =>
        new(action.Title, action.Game, BuildNodeSpec(action.Game, action.Id, string.Join(",", action.Planes)), action.HasPlanes ? action.PlanesText : string.Empty);

    // The relation's actions as the peek reads them (engine relations show).
    private async Task<List<MoveActionChoice>> LoadMoveActionChoicesAsync(string relation)
    {
        List<MoveActionChoice> choices = [];
        EngineCommandResult? result = await RunEngineAsync(["relations", "show", relation], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading relation details..."), forceText: true, background: true);
        if (result == null || result.ExitCode != 0)
            return choices;
        Dictionary<string, string> storedTitles = ParseStoredNodeTitles(result.StandardOutput);
        // The other games' titles (an MSFS action would show its id), as the peek names them.
        Dictionary<string, string> gameTitles = await LoadBindPeekGameTitlesAsync(relation, result.StandardOutput);
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
            if (planes.Count == 1 && string.Equals(planes[0], game, StringComparison.OrdinalIgnoreCase))
                planes.Clear();
            string title = ResolveNodeTitle(game, planes.Count > 0 ? planes[0] : "Default", id, StoredNodeTitle(storedTitles, id, planes.FirstOrDefault() ?? "Default"));
            if (string.Equals(title, id, StringComparison.OrdinalIgnoreCase) && gameTitles.TryGetValue(game + "|" + id, out string? gameTitle) && gameTitle.Length > 0)
                title = gameTitle;
            choices.Add(new MoveActionChoice(title.Length > 0 ? title : id, game, BuildNodeSpec(game, id, string.Join(",", planes)),
                planes.Count > 0 ? Plural("{0} aircraft", "{0} aircraft", planes.Count) : string.Empty));
        }
        return choices;
    }

    private List<string> MoveActionArgs(bool dryRun)
    {
        List<string> args = ["relations", "item", "move-to-input", moveActionRelation, "--node", SelectedMoveAction!.NodeSpec,
            "--device", MoveActionDevice.Trim(), "--input", MoveActionInput.Trim()];
        if (MoveActionKeepModifiers && HasMoveActionModifiers)
            args.Add("--keep-modifiers");
        if (dryRun)
            args.Add("--dry-run");
        return args;
    }

    // Where the action would go (engine --dry-run), told before Move is pressed.
    private async Task PreviewMoveActionAsync()
    {
        int version = ++moveActionPreviewVersion;
        if (!IsMoveActionOpen || !CanApplyMoveAction)
        {
            MoveActionPreview = string.Empty;
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(MoveActionArgs(dryRun: true), includeProfile: true, mutatesProfile: false,
            busyLabel: N("Checking where the action goes..."), forceText: false, background: true);
        if (version != moveActionPreviewVersion)
            return;
        if (result == null || result.ExitCode != 0)
        {
            MoveActionPreview = result == null ? string.Empty : BuildFailureStatus(result);
            return;
        }
        MoveActionPreview = DescribeMove(result.StandardOutput, done: false);
    }

    private string DescribeMove(string output, bool done)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            string to = ReadJsonString(root, "To");
            string from = ReadJsonString(root, "From");
            string text = ReadJsonBool(root, "Joined")
                ? (done ? T("Moved: it is in '{0}' now, the relation on that button.", to) : T("It joins '{0}', the relation that already uses that button.", to))
                : (done ? T("Moved: it has its own relation '{0}' on that button now.", to) : T("No relation uses that button yet: it gets its own relation '{0}'.", to));
            if (ReadJsonBool(root, "FromRemoved"))
                text += " " + T("'{0}' has no other action and goes away.", from);
            return text;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // Press the new button: the Binds page's record (same capture, same options).
    [RelayCommand]
    private async Task RecordMoveActionInputAsync()
    {
        if (IsMoveActionRecording)
        {
            moveActionRecordCts?.Cancel();
            return;
        }
        int timeoutMs = ParseOrDefault(RecordTimeoutMs, 6000, 500, 120000);
        bool axis = IsAxisRelation(moveActionRelation);
        JoystickCaptureService.CaptureOptions options = new()
        {
            AxisMode = axis,
            IgnoreAxesOf = AxisIgnorePredicate(),
            TimeoutMs = timeoutMs,
            WarmupMs = ParseOrDefault(RecordWarmupMs, 300, 0, 5000),
            AxisThresholdPercent = ParseOrDefault(RecordAxisThresholdPercent, 30, 1, 100),
            RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons
        };
        moveActionRecordCts?.Dispose();
        moveActionRecordCts = new CancellationTokenSource();
        try
        {
            IsMoveActionRecording = true;
            MoveActionStatus = axis ? T("Move the new axis now.") : T("Press the new button now.");
            JoystickCaptureService.CaptureResult? capture = await joystickCaptureService.CaptureNextAsync(options, moveActionRecordCts.Token);
            if (capture == null)
            {
                MoveActionStatus = T("No button or hat pressed within {0} s.", (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture));
                return;
            }
            MoveActionStatus = string.Empty;
            MoveActionDevice = capture.Device;
            MoveActionInput = capture.Input;
        }
        catch (OperationCanceledException)
        {
            MoveActionStatus = T("Recording canceled.");
        }
        finally
        {
            IsMoveActionRecording = false;
            moveActionRecordCts?.Dispose();
            moveActionRecordCts = null;
        }
    }

    [RelayCommand]
    internal async Task ApplyMoveActionAsync()
    {
        if (!CanApplyMoveAction || SelectedMoveAction == null)
        {
            MoveActionStatus = T("Press the new button or pick a device and an input first.");
            return;
        }
        string title = SelectedMoveAction.Title;
        string from = moveActionRelation;
        EngineCommandResult? result = await RunEngineAsync(MoveActionArgs(dryRun: false), includeProfile: true, mutatesProfile: true,
            busyLabel: N("Moving the action..."), forceText: false, historyNote: T("Moving '{0}' to another button", title));
        if (result == null || result.ExitCode != 0)
        {
            MoveActionStatus = result == null ? T("The engine could not be started.") : BuildFailureStatus(result);
            return;
        }
        string to = string.Empty;
        bool fromRemoved = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            to = ReadJsonString(doc.RootElement, "To");
            fromRemoved = ReadJsonBool(doc.RootElement, "FromRemoved");
        }
        catch (JsonException)
        {
        }
        string message = DescribeMove(result.StandardOutput, done: true);
        JoyProLog.Info("MoveAction", "Moved '" + title + "' from '" + from + "' to '" + to + "' (" + MoveActionDevice + " " + MoveActionInput + ")");
        CloseMoveAction();
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
        // The relation editor stays on the relation the action came from (or shows where it went when that one is gone).
        if (CurrentStage == UiStage.Relations)
            await OpenBuilderRelationAsync(fromRemoved || to.Length == 0 ? (to.Length > 0 ? to : from) : from, seedSearch: false);
        else if (SelectedBind != null)
            _ = LoadBindPeekAsync(SelectedBind.RelationName);
        StatusText = "'" + title + "': " + message;
    }

    [RelayCommand]
    private void CancelMoveAction()
    {
        moveActionRecordCts?.Cancel();
        CloseMoveAction();
    }

    private void CloseMoveAction()
    {
        IsMoveActionOpen = false;
        MoveActionChoices.Clear();
        SelectedMoveAction = null;
        MoveActionPreview = string.Empty;
        MoveActionStatus = string.Empty;
    }
}

// One action that can move: its title, game and node spec (game:id[:planes]) as engine relations item takes it.
public sealed class MoveActionChoice
{
    public MoveActionChoice(string title, string game, string nodeSpec, string where)
    {
        Title = title;
        Game = game;
        NodeSpec = nodeSpec;
        Where = where;
    }

    public string Title { get; }

    public string Game { get; }

    public string NodeSpec { get; }

    public string Where { get; }

    public string Label => Title + " (" + DisplayText.Game(Game) + (Where.Length > 0 ? ", " + Where : string.Empty) + ")";

    public override string ToString() => Label;
}
