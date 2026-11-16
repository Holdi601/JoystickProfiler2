using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The Start page's auto setup (docs/setup-wizard.md#start-page). Once the installed games are known, JoyPro reads per
// game whether the user already has binds there (engine setup games: the game's control files are only read):
//   - nothing assigned anywhere: the setup is front and center ("Set up your devices for X, Y, Z");
//   - binds of the user's own in the game: they stay; import them first, then the wizard fills only the gaps (its plan
//     keeps every bind of the profile);
//   - already in the profile: fill the gaps.
// The engine is asked in the background once the Start page shows and the app is idle (never in the way of the app's
// own engine calls), and again only when the games, the devices or the profile changed since its last answer.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<AutoSetupGameItem> AutoSetupNewGames { get; } = [];

    public ObservableCollection<AutoSetupGameItem> AutoSetupOwnGames { get; } = [];

    public ObservableCollection<AutoSetupGameItem> AutoSetupProfileGames { get; } = [];

    public bool ShowAutoSetupCard => AutoSetupNewGames.Count + AutoSetupOwnGames.Count + AutoSetupProfileGames.Count > 0;

    public bool HasAutoSetupNewGames => AutoSetupNewGames.Count > 0;

    public bool HasAutoSetupOwnGames => AutoSetupOwnGames.Count > 0;

    public bool HasAutoSetupProfileGames => AutoSetupProfileGames.Count > 0;

    // Only games the user already has binds for: the card shows them as the secondary rows only.
    public bool AutoSetupOnlySecondary => !HasAutoSetupNewGames && ShowAutoSetupCard;

    public string AutoSetupHeadline => HasAutoSetupNewGames
        ? T("Set up your devices for {0}", JoinGameNames(AutoSetupNewGames))
        : T("Your games already have binds");

    public string AutoSetupIntro => HasAutoSetupNewGames
        ? T("Nothing is assigned in these games yet. JoyPro puts their important functions on your stick, throttle and pedals where pilots expect them. You see everything before your profile changes.")
        : T("JoyPro keeps your binds and only fills the gaps.");

    public string AutoSetupOwnText => T("Your own binds in {0} stay. Import them first, then JoyPro only fills the gaps.", JoinGameNames(AutoSetupOwnGames));

    public string AutoSetupProfileText => T("Already in your profile: {0}. JoyPro can fill the gaps; your binds stay.", JoinGameNames(AutoSetupProfileGames));

    // After "Import, then fill the gaps": the import's result offers the setup for that game.
    private string? setupAfterImportGame;

    public bool OffersFillGapsAfterImport => IsImportWizardDoneStep && !ImportWizardFailed && setupAfterImportGame != null &&
                                             string.Equals(setupAfterImportGame, ImportWizardGame?.GameId, StringComparison.OrdinalIgnoreCase);

    private static string JoinGameNames(IEnumerable<AutoSetupGameItem> games) => JoinAnd(games.Select(g => g.Name));

    // ---- the look at the games (engine setup games), in the background ----
    // It must never be noticed (docs/setup-wizard.md#start-page): it waits until the Start page shows and the app has
    // had nothing to do for a moment, runs in an engine process of its own in Windows' background mode (not through
    // the queue of the app's engine calls, no busy state, the engine server keeps running), stops when the Start page
    // is left, and its answer is kept until the games, the devices or the profile change.

    // How long the app must be idle before the look starts, and how often that is checked (tests shorten them).
    internal int AutoSetupIdleMs { get; set; } = 1200;

    internal int AutoSetupPollMs { get; set; } = 150;

    // The key of the answer the card shows ("" = none yet).
    private string autoSetupKey = string.Empty;
    // Bumped by everything that may change the answer without changing the key's other parts (an import, an export,
    // the wizard's Apply, a game's config files changed, Refresh on the dashboard).
    private int autoSetupGeneration;
    private CancellationTokenSource? autoSetupCts;
    private Task? autoSetupTask;
    private bool autoSetupRunning;

    // Engine runs of the look (tests).
    internal int AutoSetupRuns { get; private set; }

    internal bool AutoSetupPending => autoSetupTask is { IsCompleted: false };

    internal string AutoSetupKeyForTests => AutoSetupKeyNow(GameCards.Where(c => c.Installed).ToList()) + " / kept " + autoSetupKey;

    // What the answer depends on: the installed games, the profile (file, size of its lists, last save), the devices
    // and every change the app was told about.
    private string AutoSetupKeyNow(IReadOnlyList<GameCardItem> installed)
    {
        long saved = 0;
        if (HasProfile)
        {
            try
            {
                saved = File.Exists(ProfilePath) ? File.GetLastWriteTimeUtc(ProfilePath).Ticks : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                saved = 0;
            }
        }
        string devices = string.Join(";", ConnectedDevices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
        return string.Join(",", installed.Select(c => c.GameId)) + "|" + (HasProfile ? ProfilePath : string.Empty) + "|" + ProfileRelationCount + "|" +
               ProfileBoundCount + "|" + saved.ToString(CultureInfo.InvariantCulture) + "|" + devices + "|" + autoSetupGeneration.ToString(CultureInfo.InvariantCulture);
    }

    // The games or the profile may have changed (force: something changed that the kept answer cannot see): the look
    // runs again once the Start page shows and the app is idle; a kept answer that still fits stays as it is.
    private void RequestAutoSetupStatus(bool force = false)
    {
        if (force)
            autoSetupGeneration++;
        if (CurrentStage != UiStage.Start)
            return;
        // One look waits already: it reads the key when it starts, and looks again when the key changed meanwhile.
        if (autoSetupTask is { IsCompleted: false })
            return;
        List<GameCardItem> installed = GameCards.Where(c => c.Installed).ToList();
        if (installed.Count == 0)
        {
            autoSetupKey = AutoSetupKeyNow(installed);
            ApplyAutoSetupStatus([]);
            return;
        }
        if (AutoSetupKeyNow(installed) == autoSetupKey)
            return;
        CancellationTokenSource cts = new();
        autoSetupCts = cts;
        autoSetupTask = WaitThenLookAtGamesAsync(cts);
    }

    // The connected devices were read again: the key has them, so a changed list makes the look run again.
    private void OnAutoSetupDevicesChanged() => RequestAutoSetupStatus();

    // Leaving the Start page: a waiting or running look stops (its engine process ends), the next visit asks again.
    private void CancelAutoSetupStatus()
    {
        CancellationTokenSource? cts = autoSetupCts;
        autoSetupCts = null;
        if (cts == null)
            return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ended meanwhile.
        }
    }

    private async Task WaitThenLookAtGamesAsync(CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            // The app idle (no engine call queued or running) for AutoSetupIdleMs in a row.
            int idleFor = 0;
            while (idleFor < AutoSetupIdleMs)
            {
                await Task.Delay(AutoSetupPollMs, token);
                idleFor = IsBusy || pendingEngineCalls > 0 ? 0 : idleFor + AutoSetupPollMs;
            }
            if (CurrentStage == UiStage.Start)
                await LookAtGamesAsync(token, background: true);
        }
        catch (OperationCanceledException)
        {
            // Left the Start page.
        }
        finally
        {
            if (ReferenceEquals(autoSetupCts, cts))
                autoSetupCts = null;
            cts.Dispose();
        }
        // Something changed while it waited or ran (a game list that came in, a save), or the Start page showed again
        // while this one was stopping: once more, with the new key.
        if (CurrentStage == UiStage.Start && autoSetupCts == null && !autoSetupRunning)
        {
            List<GameCardItem> installed = GameCards.Where(c => c.Installed).ToList();
            if (installed.Count > 0 && AutoSetupKeyNow(installed) != autoSetupKey)
            {
                CancellationTokenSource again = new();
                autoSetupCts = again;
                autoSetupTask = WaitThenLookAtGamesAsync(again);
            }
        }
    }

    // One engine run of setup games and the card from its answer. background: in Windows' background mode (the Start
    // page's own look); else at normal priority (the user asked: the quick path).
    private async Task LookAtGamesAsync(CancellationToken token, bool background)
    {
        List<GameCardItem> installed = GameCards.Where(c => c.Installed).ToList();
        string key = AutoSetupKeyNow(installed);
        if (installed.Count == 0)
        {
            autoSetupKey = key;
            ApplyAutoSetupStatus([]);
            return;
        }
        string enginePath = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath))
            return;
        List<string> args = [];
        if (HasProfile && File.Exists(ProfilePath))
            args.AddRange(["--profile", ProfilePath]);
        // Only the answer counts: no progress lines, no load corrections (the profile is only counted, never saved), and
        // the game files of a game the profile already has binds for stay unread (--status-only).
        args.AddRange(["--status", "false", "--benchmark", "false", "--load-corrections", "false",
            "setup", "games", "--games", string.Join(",", installed.Select(c => c.GameId)), "--status-only"]);
        autoSetupRunning = true;
        AutoSetupRuns++;
        try
        {
            EngineCommandResult result = await cliClient.RunStreamingAsync(enginePath, args,
                new EngineRunOptions { LowPriority = background, KillOnCancel = true }, token);
            token.ThrowIfCancellationRequested();
            // A failed look is not repeated until something changes (no retry loop in the background).
            autoSetupKey = key;
            if (result.ExitCode != 0)
            {
                JoyPro.Logging.JoyProLog.Warn("AutoSetup", "The look at the games failed (exit code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " +
                    JoyPro.Logging.JoyProLog.Truncate(result.StandardError.Trim(), 2000));
                return;
            }
            List<AutoSetupGameItem> items = [];
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement game in games.EnumerateArray())
                {
                    string id = game.TryGetProperty("Game", out JsonElement g) ? g.GetString() ?? string.Empty : string.Empty;
                    bool wizard = game.TryGetProperty("Wizard", out JsonElement w) && w.ValueKind == JsonValueKind.True;
                    GameCardItem? card = installed.FirstOrDefault(c => string.Equals(c.GameId, id, StringComparison.OrdinalIgnoreCase));
                    if (!wizard || card == null)
                        continue;
                    string status = game.TryGetProperty("Status", out JsonElement s) ? s.GetString() ?? "empty" : "empty";
                    int gameBinds = game.TryGetProperty("GameBinds", out JsonElement gb) && gb.TryGetInt32(out int gbv) ? gbv : 0;
                    int profileBinds = game.TryGetProperty("ProfileBinds", out JsonElement pb) && pb.TryGetInt32(out int pbv) ? pbv : 0;
                    items.Add(new AutoSetupGameItem(card.GameId, card.Name, status, gameBinds, profileBinds));
                }
            }
            ApplyAutoSetupStatus(items);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            autoSetupKey = key;
            JoyPro.Logging.JoyProLog.Warn("AutoSetup", "The game state could not be read", ex);
        }
        finally
        {
            autoSetupRunning = false;
        }
    }

    internal void ApplyAutoSetupStatus(IEnumerable<AutoSetupGameItem> items)
    {
        List<AutoSetupGameItem> list = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Replace(AutoSetupNewGames, list.Where(i => i.Status == "empty"));
        Replace(AutoSetupOwnGames, list.Where(i => i.Status == "game"));
        Replace(AutoSetupProfileGames, list.Where(i => i.Status == "profile"));
        OnPropertyChanged(nameof(ShowAutoSetupCard));
        OnPropertyChanged(nameof(HasAutoSetupNewGames));
        OnPropertyChanged(nameof(HasAutoSetupOwnGames));
        OnPropertyChanged(nameof(HasAutoSetupProfileGames));
        OnPropertyChanged(nameof(AutoSetupOnlySecondary));
        OnPropertyChanged(nameof(AutoSetupHeadline));
        OnPropertyChanged(nameof(AutoSetupIntro));
        OnPropertyChanged(nameof(AutoSetupOwnText));
        OnPropertyChanged(nameof(AutoSetupProfileText));
        // The next steps follow the card (no second blue button while it offers "Set it all up for me").
        RebuildNextSteps();
        RefreshOpenWelcome();
    }

    private static void Replace(ObservableCollection<AutoSetupGameItem> target, IEnumerable<AutoSetupGameItem> items)
    {
        target.Clear();
        foreach (AutoSetupGameItem item in items)
            target.Add(item);
    }

    // "Set up my devices...": the wizard with the games that have nothing assigned ticked.
    [RelayCommand]
    private void OpenAutoSetup()
    {
        List<string> games = AutoSetupNewGames.Select(g => g.GameId).ToList();
        OpenSetupWizardFor(games.Count > 0 ? games : AutoSetupProfileGames.Select(g => g.GameId).ToList());
    }

    // "Fill the gaps": the wizard for one game (or every game already in the profile); the plan keeps the profile's binds.
    [RelayCommand]
    private void FillAutoSetupGaps(AutoSetupGameItem? game)
    {
        OpenSetupWizardFor(game != null ? [game.GameId] : AutoSetupProfileGames.Select(g => g.GameId).ToList());
    }

    // "Import, then fill the gaps": the import wizard for the game first; its result offers the setup.
    [RelayCommand]
    private void ImportThenFillGaps(AutoSetupGameItem? game)
    {
        GameCardItem? card = game == null ? null : GameCards.FirstOrDefault(c => string.Equals(c.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
        if (card == null)
            return;
        setupAfterImportGame = card.GameId;
        StartImportWizard(card);
        OnPropertyChanged(nameof(OffersFillGapsAfterImport));
    }

    [RelayCommand]
    private void FillGapsAfterImport()
    {
        string? game = setupAfterImportGame;
        setupAfterImportGame = null;
        RestartImportWizard();
        OnPropertyChanged(nameof(OffersFillGapsAfterImport));
        if (game != null)
            OpenSetupWizardFor([game]);
    }

    // ---- the quick path ("Just give me binds, quickly") ----

    // From the first start ("Done, and give me binds quickly") and the Start page's card: the games with nothing assigned
    // (else the ones already in the profile), the setup run by itself up to its review, then "Use these binds" and
    // "Write to my game" (QuickExportAsync).
    [RelayCommand]
    private Task QuickSetupAsync() => StartQuickSetupAsync();

    // A quick setup is running (its engine steps): a second request only brings its window to the front.
    private bool quickSetupRunning;
    // The welcome's or the first start's quick answer: "Write to my game" starts the quick tour.
    private bool quickTourAfterWrite;

    internal bool QuickSetupRunning => quickSetupRunning;

    public async Task StartQuickSetupAsync(bool tourAfter = false)
    {
        if (tourAfter)
            quickTourAfterWrite = true;
        if (quickSetupRunning && SetupWizard != null)
        {
            SetupWizardRequested?.Invoke();
            return;
        }
        quickSetupRunning = true;
        try
        {
            await RunQuickSetupAsync();
        }
        finally
        {
            quickSetupRunning = false;
        }
    }

    private async Task RunQuickSetupAsync()
    {
        // The user asked: a look that waits for the idle app stops, and one runs now (normal priority) with what the
        // games have right now.
        CancelAutoSetupStatus();
        for (int i = 0; i < 100 && autoSetupRunning; i++)
            await Task.Delay(50);
        autoSetupGeneration++;
        await LookAtGamesAsync(CancellationToken.None, background: false);
        List<string> games = AutoSetupNewGames.Select(g => g.GameId).ToList();
        if (games.Count == 0)
            games = AutoSetupProfileGames.Select(g => g.GameId).ToList();
        SetupWizard = CreateSetupWizard(games);
        OnPropertyChanged(nameof(SetupWizard));
        SetupWizard.QuickMode = true;
        SetupWizardRequested?.Invoke();
        await SetupWizard.RunQuickAsync();
    }

    // "Write to my game" of the quick path: the Export page with only these games ticked, then their export. The click
    // is the user's answer, so the Export page's questions ("Remove binds?", "Import first?") are not asked here. A game
    // with nothing on its sticks yet asks nothing; a game that already has buttons there asks ONE plain question
    // (QuickWriteQuestionAsync): use the new ones (the chosen export mode, clean + nuke by default; JoyPro keeps a backup)
    // or keep the old ones too ("Only add" for this write: nothing in the game is removed). Cancel writes nothing and the
    // setup window says how to go on.
    internal async Task QuickExportAsync(IReadOnlyCollection<string> gameIds)
    {
        CurrentStage = UiStage.Export;
        await RefreshExportGamesAsync();
        using (SuppressExportSettings())
        {
            foreach (ExportGameItem tile in ExportGames)
                tile.IsSelected = gameIds.Contains(tile.GameId, StringComparer.OrdinalIgnoreCase);
        }
        List<ExportGameItem> ticked = ExportGames.Where(t => t.IsSelected && t.CanExport).ToList();
        if (ticked.Count == 0)
        {
            StatusText = T("JoyPro has nothing to write for {0} yet.", JoinAnd(gameIds.Select(DisplayText.Game)));
            return;
        }
        QuickWriteChoice choice = await QuickWriteQuestionAsync(ticked);
        if (choice == QuickWriteChoice.Cancel)
        {
            StatusText = EasyWords
                ? T("Nothing was written. Click Write to my game when you are ready.")
                : T("Nothing was written yet. When you are ready, press Write to my game again.");
            if (SetupWizard != null)
                SetupWizard.Status = StatusText;
            SetupWizardRequested?.Invoke();
            return;
        }
        if (choice == QuickWriteChoice.KeepOld)
        {
            // This write only: "Only add" never removes a game bind; the user's export mode stays as it was.
            string modeBefore = ExportMode;
            bool nukeBefore = ExportNukeUntouchedDevices;
            using (SuppressExportSettings())
            {
                ExportMode = "add";
                ExportNukeUntouchedDevices = false;
                try
                {
                    await ExportTickedGamesAsync(askFirst: false);
                }
                finally
                {
                    ExportMode = modeBefore;
                    ExportNukeUntouchedDevices = nukeBefore;
                }
            }
        }
        else
            await ExportTickedGamesAsync(askFirst: false);
        string kept = choice == QuickWriteChoice.KeepOld && LastExportedGames.Count > 0
            ? T("{0}: your new buttons are in, and your old ones stayed. Where a button was already taken, the old one stayed; Check shows those.",
                JoinAnd(LastExportedGames.Select(DisplayText.Game)))
            : string.Empty;
        if (kept.Length > 0)
            StatusText = kept;
        if (SetupWizard != null)
            SetupWizard.Status = StatusText;
        // Written: the setup window has done its job; the quick tour shows where to change a button later.
        if (LastExportedGames.Count > 0)
        {
            SetupWizard?.RequestClose();
            quickTourAfterWrite = false;
            StartQuickTourAfterSetup(gameIds.ToList());
            // The status line says what happened to the old buttons (the tour's page change does not hide it).
            if (kept.Length > 0)
                StatusText = kept;
        }
    }

    internal enum QuickWriteChoice
    {
        UseNew,
        KeepOld,
        Cancel
    }

    // The one question of the quick write, only when a game already has stick buttons of its own (engine profile
    // game-state: GameJoystickBindCount; a game this profile imported or exported before counts as set, as does a game
    // the engine could not read). The new buttons are the main answer (they are what the user just made); keeping the
    // old ones is the second; the window's close is Cancel. Not asked when the user switched off both export
    // questions (Settings > Confirmations), nor when the export mode removes nothing ("Only add").
    internal async Task<QuickWriteChoice> QuickWriteQuestionAsync(IReadOnlyList<ExportGameItem> games)
    {
        bool removes = !string.Equals(EffectiveExportMode, "add", StringComparison.OrdinalIgnoreCase) || ExportNukeUntouchedDevices;
        if (!removes || (!ListPrefs.ShouldAsk(ConfirmExportRemovesKey) && !ListPrefs.ShouldAsk(ConfirmFirstExportKey)))
            return QuickWriteChoice.UseNew;
        List<string> set = [];
        foreach (ExportGameItem game in games)
        {
            if (await GameHasStickButtonsAsync(game.GameId))
                set.Add(game.Name);
        }
        if (set.Count == 0)
            return QuickWriteChoice.UseNew;
        string names = JoinAnd(set);
        ConfirmChoice answer = await ConfirmChoiceAsync(
            EasyWords ? T("Your game has buttons already") : T("{0} already has buttons set", names),
            EasyWords
                ? T("{0} has buttons already. Use the new ones? JoyPro keeps the old ones safe. You can go back.", names)
                : T("{0} already has buttons set. Use the new ones instead? JoyPro keeps a copy of the old ones, and you can undo.", names),
            T("Use the new ones"), T("Keep the old ones too"));
        return answer switch
        {
            ConfirmChoice.Confirm => QuickWriteChoice.UseNew,
            ConfirmChoice.Secondary => QuickWriteChoice.KeepOld,
            _ => QuickWriteChoice.Cancel
        };
    }

    // The game has binds on game devices (not the keyboard or mouse) that did not come from this profile's last write.
    private async Task<bool> GameHasStickButtonsAsync(string gameId)
    {
        if (!HasProfile || gameId.Length == 0)
            return false;
        List<string> args = ["profile", "game-state", "--game", gameId];
        string path = ExportPathOf(gameId).Trim();
        if (path.Length > 0)
            args.AddRange(["--path", path]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: "Checking the game before the export", forceText: false, background: true); // nonloc (background: not shown)
        if (result == null || result.ExitCode != 0)
            return true;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("GameHasBinds", out _))
                return true;
            // Imported or written by this profile before: the game's buttons are (also) this profile's, its files
            // were not read; asked, so a button made in the game since is never removed silently.
            if (ReadJsonBool(root, "Imported") || ReadJsonBool(root, "Exported"))
                return true;
            if (!ReadJsonBool(root, "GameFilesRead"))
                return ReadJsonString(root, "ConfigPath").Length > 0;
            return ReadJsonInt(root, "GameJoystickBindCount") > 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // The quick path's "Set up the pretend controller" (only pad games, which get no export): the setup window closes, and the Virtual
    // Joystick page shows the pad steps (the helper driver when it is missing, Test, when to press Start).
    internal void QuickSetUpPad(IReadOnlyCollection<string> gameIds)
    {
        SetupWizard?.RequestClose();
        quickTourAfterWrite = false;
        ContinueWithPadSteps(gameIds);
    }

    public void OpenSetupWizardFor(IReadOnlyCollection<string> gameIds)
    {
        if (SetupWizard != null && !SetupWizard.Applied)
            SetupWizard.ChooseGames(gameIds);
        else
            SetupWizard = CreateSetupWizard(gameIds);
        OnPropertyChanged(nameof(SetupWizard));
        SetupWizardRequested?.Invoke();
    }
}
