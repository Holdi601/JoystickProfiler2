using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// 2.5.0 (user decisions on export and import, docs/export.md):
// - the first export of a game this profile never imported from or exported to, while the game has binds of its own,
//   asks once: Import first / Export anyway / Cancel (engine profile game-state);
// - the import shows a muted line once per game that changes made in the game come in through "Watch for changes";
// - the vJoy device JoyPro forwards into (Settings > Games) is the only vJoy device left out of an import;
// - the Move panel says what a move does in the game with the current export mode;
// - "Only add" marks the binds it could not write (the game uses the button for another function): engine export-marks,
//   a chip on the action in the relation editor and a card in the Check with its fixes.
public sealed partial class MainWindowViewModel
{
    // ---- vJoy: the device JoyPro tunnels into ----------------------------------------------------------------------

    // 0 = none (every vJoy device is a normal device, e.g. one VoiceAttack drives), 1-16 = that vJoy device is JoyPro's
    // own output and is left out of every import. Per PC (export-settings.json "import").
    [ObservableProperty]
    private int vJoyTunnelDevice;

    public IReadOnlyList<string> VJoyTunnelOptions { get; } =
        [N("None"), .. Enumerable.Range(1, 16).Select(i => "vJoy " + i.ToString(CultureInfo.InvariantCulture))];

    partial void OnVJoyTunnelDeviceChanged(int value)
    {
        if (value is < 0 or > 16)
            VJoyTunnelDevice = 0;
    }

    private void AddVJoyTunnelArgs(List<string> args)
    {
        if (VJoyTunnelDevice is >= 1 and <= 16)
            args.AddRange(["--vjoy-tunnel", VJoyTunnelDevice.ToString(CultureInfo.InvariantCulture)]);
    }

    // ---- The Move panel: what the move does in the game ---------------------------------------------------------------

    // Merge and "Only add" keep the game's other binds: the old button keeps the action there until an export with
    // "my profile wins" or "Replace on my devices" (unless another action now uses that button).
    public string MoveActionGameNote => EffectiveExportMode is "merge" or "add"
        ? T("In the game the old button keeps this action until you export with 'Update, my profile wins' or 'Replace on my devices'.")
        : string.Empty;

    public bool HasMoveActionGameNote => MoveActionGameNote.Length > 0;

    // ---- First export of a game: import first? -------------------------------------------------------------------------

    public const string ConfirmFirstExportKey = "games.first-export-replaces";
    public const string ConfirmExportRemovesKey = "games.export-removes-binds";

    public bool AskBeforeFirstExport
    {
        get => ListPrefs.ShouldAsk(ConfirmFirstExportKey);
        set => SetConfirmationAsked(ConfirmFirstExportKey, value);
    }

    public bool AskBeforeExportRemoves
    {
        get => ListPrefs.ShouldAsk(ConfirmExportRemovesKey);
        set => SetConfirmationAsked(ConfirmExportRemovesKey, value);
    }

    internal enum FirstExportChoice
    {
        // Not asked (imported or exported before, nothing in the game, switched off) or "Export anyway".
        Export,
        ExportConfirmed,
        ImportFirst,
        Cancel
    }

    // The game's config path the export writes (empty: the engine finds it).
    private string ExportPathOf(string gameId) => gameId switch
    {
        "DCS" => ExportDcsPath ?? string.Empty,
        "IL2Game" => ExportIl2Path ?? string.Empty,
        "StarCitizen" => ExportStarCitizenPath ?? string.Empty,
        _ => exportSettings.RegistryPaths.TryGetValue(gameId, out string? path) ? path : string.Empty
    };

    internal async Task<FirstExportChoice> AskAboutFirstExportAsync(string gameId, string gameName)
    {
        if (!HasProfile || gameId.Length == 0 || !ListPrefs.ShouldAsk(ConfirmFirstExportKey))
            return FirstExportChoice.Export;
        List<string> args = ["profile", "game-state", "--game", gameId];
        string path = ExportPathOf(gameId).Trim();
        if (path.Length > 0)
            args.AddRange(["--path", path]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: "Checking the game before the export", forceText: false, background: true); // nonloc (background: not shown)
        if (result == null || result.ExitCode != 0)
            return FirstExportChoice.Export;
        bool imported, exported, gameHasBinds;
        int count;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("GameHasBinds", out _))
                return FirstExportChoice.Export;
            imported = ReadJsonBool(root, "Imported");
            exported = ReadJsonBool(root, "Exported");
            gameHasBinds = ReadJsonBool(root, "GameHasBinds");
            count = ReadJsonInt(root, "GameBindCount");
        }
        catch (JsonException)
        {
            return FirstExportChoice.Export;
        }
        if (imported || exported || !gameHasBinds)
            return FirstExportChoice.Export;
        JoyProLog.Info("FirstExport", gameId + ": first export of this profile, the game has " + count + " bind(s) of its own; asking");
        ConfirmChoice answer = await ConfirmChoiceAsync(T("First export to {0}", gameName),
            T("Export replaces the binds in {0} with your profile (a backup is made, Undo export brings them back). Import first?", gameName),
            T("Export anyway"), T("Import first"), ConfirmFirstExportKey, secondaryIsPrimary: true);
        switch (answer)
        {
            case ConfirmChoice.Secondary:
                OpenImportFor(gameId, gameName);
                return FirstExportChoice.ImportFirst;
            case ConfirmChoice.Confirm:
                return FirstExportChoice.ExportConfirmed;
            default:
                StatusText = T("Export to {0} cancelled: nothing was written.", gameName);
                return FirstExportChoice.Cancel;
        }
    }

    // "Import first": the Start page's import of that game (one click on "Import now").
    private void OpenImportFor(string gameId, string gameName)
    {
        GameCardItem? card = GameCards.FirstOrDefault(c => string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase));
        CurrentStage = UiStage.Start;
        if (card != null)
            StartImportWizard(card);
        StatusText = T("Import {0} first: check the options and press 'Import now'. Export afterwards.", gameName);
    }

    // ---- The watcher hint in the import ------------------------------------------------------------------------------

    public const string WatcherHintKey = "games.import-watcher-hint";

    public bool ShowWatcherHintInImport
    {
        get => ListPrefs.ShouldAsk(WatcherHintKey);
        set => SetConfirmationAsked(WatcherHintKey, value);
    }

    [ObservableProperty]
    private bool importWatcherHintVisible;

    [ObservableProperty]
    private string importWatcherHintText = string.Empty;

    [ObservableProperty]
    private bool importWatcherOffersTurnOn;

    [ObservableProperty]
    private string importWatcherTurnOnText = string.Empty;

    private GameCardItem? importWatcherHintGame;

    // The profile has relations with switched-on actions of this game.
    internal bool ProfileHasGame(string gameId) =>
        allRelations.Any(r => r.ActiveGamePlanes.Any(p => p.StartsWith(gameId + "|", StringComparison.OrdinalIgnoreCase)));

    private bool IsGameWatched(GameCardItem card) => WatchGameConfigs && card.Watch;

    // The import of a game opened: once per game (remembered), while a profile with that game is open.
    private void UpdateImportWatcherHint(GameCardItem? game)
    {
        importWatcherHintGame = game;
        bool show = game != null && HasProfile && ProfileHasGame(game.GameId) && ListPrefs.ShouldAsk(WatcherHintKey) &&
                    !ListPrefs.WatcherHintShownGames.Contains(game.GameId, StringComparer.OrdinalIgnoreCase);
        ImportWatcherHintVisible = show;
        if (!show || game == null)
            return;
        ImportWatcherHintText = T("{0} is in your profile already. Changes you make in the game come in through 'Watch for changes' on its card; re-importing is fine too, e.g. to try a new JoyPro version.", game.Name);
        ImportWatcherOffersTurnOn = !IsGameWatched(game);
        ImportWatcherTurnOnText = T("Turn on watching for {0}", game.Name);
        ListPrefs.WatcherHintShownGames.Add(game.GameId);
        SaveListPreferences();
    }

    [RelayCommand]
    private void TurnOnWatchingFromImport()
    {
        GameCardItem? game = importWatcherHintGame;
        if (game == null)
            return;
        if (!WatchGameConfigs)
            WatchGameConfigs = true;
        if (!game.Watch)
            game.Watch = true;
        ImportWatcherOffersTurnOn = false;
        StatusText = T("JoyPro watches {0} now: change binds in the game and JoyPro offers to take them over after it closed.", game.Name);
    }

    [RelayCommand]
    private void HideWatcherHintForGood()
    {
        SetConfirmationAsked(WatcherHintKey, false);
        ImportWatcherHintVisible = false;
    }

    // "Report watcher problems": Settings > Report a problem with the text started.
    [RelayCommand]
    private void ReportWatcherProblem()
    {
        if (!(ProblemDescription ?? string.Empty).TrimStart().StartsWith("Watcher:", StringComparison.OrdinalIgnoreCase))
            ProblemDescription = "Watcher: " + (ProblemDescription ?? string.Empty).Trim();
        CurrentStage = UiStage.Settings;
        OpenSettingsSection("report");
    }

    // ---- "Only add": binds not written because the game uses the button for another function --------------------------

    // The marks the profile keeps (engine export-marks list); dismissed ones show no card.
    public List<ExportMarkItem> ExportMarks { get; private set; } = [];

    private readonly List<CheckIssueCard> exportSkipCards = [];

    internal async Task LoadExportMarksAsync()
    {
        if (!HasProfile)
        {
            ApplyExportMarks([]);
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["export-marks", "list"], includeProfile: true, mutatesProfile: false,
            busyLabel: "Reading the export marks", forceText: false, background: true); // nonloc (background: not shown)
        if (result == null || result.ExitCode != 0)
            return;
        ApplyExportMarks(ParseExportMarks(result.StandardOutput));
    }

    internal static List<ExportMarkItem> ParseExportMarks(string output)
    {
        List<ExportMarkItem> marks = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Marks", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return marks;
            foreach (JsonElement item in list.EnumerateArray())
            {
                List<string> modifiers = [];
                if (item.TryGetProperty("Modifiers", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
                    modifiers.AddRange(mods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString() ?? string.Empty).Where(m => m.Length > 0));
                marks.Add(new ExportMarkItem
                {
                    Game = ReadJsonString(item, "Game"),
                    Aircraft = ReadJsonString(item, "Aircraft"),
                    Relation = ReadJsonString(item, "Relation"),
                    ActionId = ReadJsonString(item, "ActionId"),
                    Action = ReadJsonString(item, "Action"),
                    Device = ReadJsonString(item, "Device"),
                    Input = ReadJsonString(item, "Input"),
                    Modifiers = modifiers,
                    GameActionId = ReadJsonString(item, "GameActionId"),
                    GameAction = ReadJsonString(item, "GameAction"),
                    Date = ReadJsonString(item, "Date"),
                    Dismissed = ReadJsonBool(item, "Dismissed")
                });
            }
        }
        catch (JsonException)
        {
        }
        return marks;
    }

    internal void ApplyExportMarks(List<ExportMarkItem> marks)
    {
        ExportMarks = marks;
        ApplyExportMarksToNodes();
        exportSkipCards.Clear();
        foreach (IGrouping<string, ExportMarkItem> group in marks.Where(m => !m.Dismissed)
                     .GroupBy(m => m.Game + "|" + m.Relation + "|" + m.ActionId + "|" + m.Device + "|" + m.Input, StringComparer.OrdinalIgnoreCase))
            exportSkipCards.Add(ExportSkipCard(group.ToList()));
        OnPropertyChanged(nameof(ExportMarks));
        checkDataVersion++;
        BuildCheckSections();
    }

    // The chip on the action's row: "Not exported: button taken in DCS by 'Gear Up' (F-16C_50, 2026-10-31)".
    private void ApplyExportMarksToNodes()
    {
        string relation = SelectedRelation?.Name ?? string.Empty;
        foreach (RelationNodeItem node in RelationNodes)
        {
            List<ExportMarkItem> mine = relation.Length == 0 ? [] : ExportMarks.Where(m => !m.Dismissed &&
                string.Equals(m.Relation, relation, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Game, node.Game, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.ActionId, node.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            node.ExportSkipText = mine.Count == 0 ? string.Empty : string.Join(Environment.NewLine, mine
                .GroupBy(m => m.GameAction + "|" + m.Date, StringComparer.OrdinalIgnoreCase)
                .Select(g => ExportMarkChipText(g.First(), g.Select(m => m.Aircraft).Where(a => a.Length > 0 && !string.Equals(a, g.First().Game, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
        }
    }

    internal static string ExportMarkChipText(ExportMarkItem mark, IReadOnlyList<string> aircraft)
    {
        string game = DisplayText.Game(mark.Game);
        string other = mark.GameAction.Length > 0 ? mark.GameAction : mark.GameActionId;
        // "(F-16C_50; 2026-10-31)" or "(2026-10-31)": aircraft and date in one place of the sentence.
        string when = aircraft.Count == 0 ? mark.Date : string.Join(", ", aircraft) + "; " + mark.Date;
        return T("Not exported: input taken in {0} by '{1}' ({2})", game, other, when);
    }

    // "Not written to DCS World" in the check's card style: the button and its relation, what the game does with the
    // button instead, the aircraft, and the fixes (export with "Update", move it, keep it as it is).
    private CheckIssueCard ExportSkipCard(List<ExportMarkItem> marks)
    {
        ExportMarkItem first = marks[0];
        string game = DisplayText.Game(first.Game);
        string button = ButtonWords(first.Device, first.Input, first.Modifiers);
        string other = first.GameAction.Length > 0 ? first.GameAction : first.GameActionId;
        string action = first.Action.Length > 0 ? DisplayText.ActionTitle(first.Action) : T("a command without a name");
        List<string> aircraft = marks.Select(m => m.Aircraft).Where(a => a.Length > 0 && !string.Equals(a, first.Game, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string where = aircraft.Count == 0 ? game : PlanesWords(first.Game, aircraft);
        CheckFix update = new()
        {
            Text = T("Export with 'Update' to replace it"),
            Tip = T("Exports {0} once with 'Update': your bind replaces the game's bind on this button. Your export setting stays as it is.", game),
            Command = new AsyncRelayCommand(() => ExportWithUpdateAsync(first.Game)),
            IsRecommended = true
        };
        string date = first.Date;
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            date = day.ToString("d", CultureInfo.CurrentCulture);
        return new CheckIssueCard
        {
            Kind = "export-skips",
            Severity = "warning",
            Title = T("Not written to {0}", game),
            ButtonsLine = SideWords(button, first.Relation),
            Sentence = T("{0} already uses this button for '{1}'. 'Only add' never removes the game's own binds, so '{2}' was left out in {3}.", game, other, action, where),
            Rows = aircraft.Count <= 1 ? [] : [.. aircraft.Select(a => new CheckTableRow { Left = PlanesWords(first.Game, [a]), Right = "'" + action + "'" })],
            RowsHeaderLeft = T("Aircraft"),
            RowsHeaderRight = T("Command left out"),
            CausedText = date.Length > 0 ? T("Found by the export on {0}", date) : string.Empty,
            Recommended = update,
            Fixes =
            [
                new CheckFix { Text = T("Move to another button"), Tip = T("Opens the move dialog: press a free button for '{0}'.", action), Command = new RelayCommand(() => OpenMoveForMark(first, aircraft)) },
                new CheckFix { Text = T("Keep as is"), Tip = T("The game keeps its own bind; the check does not show this again."), Command = new AsyncRelayCommand(() => DismissExportMarkAsync(first)) }
            ],
            OpenButtons = [OpenFix(first.Relation, button, first.Game, [first.ActionId], aircraft)],
            Game = first.Game,
            RelationsSort = first.Relation,
            AircraftSort = string.Join(",", aircraft),
            RelationGroup = first.Relation,
            AircraftGroup = aircraft.Count == 0 ? game : FirstPlaneWords(first.Game, aircraft),
            SearchText = string.Join(" ", game, button, other, action, first.Relation, string.Join(" ", aircraft), where),
            GroupLabel = game,
            Relations = [first.Relation]
        };
    }

    // "Export with 'Update' to replace it": that game once with "Update, keep the game's binds" (it removes the game's
    // bind on the same button); the chosen mode stays.
    private async Task ExportWithUpdateAsync(string gameId)
    {
        string modeBefore = ExportMode;
        EngineCommandResult? result;
        using (SuppressExportSettings())
        {
            ExportMode = "merge";
            try
            {
                result = gameId switch
                {
                    "DCS" => await RunDcsExportAsync(null),
                    "IL2Game" => await RunIl2ExportAsync(),
                    "StarCitizen" => await RunStarCitizenExportAsync(),
                    _ => await ExportRegistryGameByIdAsync(gameId)
                };
            }
            finally
            {
                ExportMode = modeBefore;
            }
        }
        await LoadExportMarksAsync();
        StatusText = result?.ExitCode == 0
            ? T("Exported to {0} with 'Update': your binds replaced the game's binds on the same buttons.", DisplayText.Game(gameId))
            : T("The export to {0} did not work.", DisplayText.Game(gameId));
    }

    private void OpenMoveForMark(ExportMarkItem mark, List<string> aircraft)
    {
        string action = mark.Action.Length > 0 ? mark.Action : mark.ActionId;
        OpenMoveAction(mark.Relation, [new MoveActionChoice(action, mark.Game, BuildNodeSpec(mark.Game, mark.ActionId, string.Join(",", aircraft)),
            aircraft.Count > 0 ? string.Join(", ", aircraft) : string.Empty)]);
    }

    private async Task DismissExportMarkAsync(ExportMarkItem mark)
    {
        List<string> args = ["export-marks", "dismiss", "--game", mark.Game, "--relation", mark.Relation];
        if (mark.Aircraft.Length > 0)
            args.AddRange(["--aircraft", mark.Aircraft]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
            busyLabel: N("Keeping the bind as it is"), forceText: false);
        if (result?.ExitCode != 0)
            return;
        foreach (ExportMarkItem same in ExportMarks.Where(m => string.Equals(m.Game, mark.Game, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(m.Relation, mark.Relation, StringComparison.OrdinalIgnoreCase) &&
                     (mark.Aircraft.Length == 0 || string.Equals(m.Aircraft, mark.Aircraft, StringComparison.OrdinalIgnoreCase))))
            same.Dismissed = true;
        ApplyExportMarks(ExportMarks);
        StatusText = T("'{0}' stays as it is; the Check does not show it again.", mark.Relation);
    }

    // After an export: the marks again, and the status says how many binds of that game were not written.
    private async Task<string> ExportMarksNoteAsync(string gameId)
    {
        await LoadExportMarksAsync();
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        int count = ExportMarks.Count(m => !m.Dismissed && string.Equals(m.Game, gameId, StringComparison.OrdinalIgnoreCase) && m.Date == today);
        return count == 0 ? string.Empty
            : T("Binds not written to {0}: {1} (the game uses their buttons for other functions, see Check).", DisplayText.Game(gameId), count);
    }
}

// A bind "Only add" did not write (engine export-marks list).
public sealed class ExportMarkItem
{
    public string Game { get; init; } = string.Empty;
    public string Aircraft { get; init; } = string.Empty;
    public string Relation { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Device { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public List<string> Modifiers { get; init; } = [];
    public string GameActionId { get; init; } = string.Empty;
    public string GameAction { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public bool Dismissed { get; set; }
}
