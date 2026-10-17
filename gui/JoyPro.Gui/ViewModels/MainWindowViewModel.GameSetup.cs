using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Logging;

namespace JoyPro.Gui.ViewModels;

// The game setup: shown once at the first start (after the startup detection, ui-settings.json gameSetupDone), and
// again from Settings > Games > Run setup again. It says what JoyPro found and lets the user tick the games they have,
// link installation folders (portable installs, other drives) and config locations. Done writes them through the
// engine (games install set|clear, games config set|clear, games install disable|enable), so the CLI and the GUI agree.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<GameSetupItem> GameSetupItems { get; } = [];

    private bool gameSetupDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameSetupTitle))]
    private bool gameSetupFirstStart;

    [ObservableProperty]
    private string gameSetupSummary = string.Empty;

    [ObservableProperty]
    private string gameSetupStatus = string.Empty;

    public string GameSetupTitle => GameSetupFirstStart ? T("Welcome to JoyPro2: your games") : T("Your games");

    // The main window opens the setup window when this is raised.
    public event Action? GameSetupRequested;

    // The first start: once, after the detection has answered; never for test instances.
    public bool ShouldShowFirstStartSetup => persistUiFiles && !gameSetupDone && gameInstallsKnown && GameInstalls.Count > 0;

    private void ShowFirstStartSetupIfNeeded()
    {
        if (!ShouldShowFirstStartSetup)
            return;
        BuildGameSetup(firstStart: true);
        GameSetupRequested?.Invoke();
    }

    // Settings > Games: detect again, then the same setup as at the first start.
    [RelayCommand]
    private async Task RunGameSetupAgainAsync()
    {
        await RefreshGameInstallsAsync();
        if (GameInstalls.Count == 0)
        {
            StatusText = T("The game setup needs the engine's game list (engine games installs); see Engine Output.");
            return;
        }
        BuildGameSetup(firstStart: false);
        GameSetupRequested?.Invoke();
    }

    public void BuildGameSetup(bool firstStart)
    {
        GameSetupFirstStart = firstStart;
        GameSetupStatus = string.Empty;
        GameSetupItems.Clear();
        // Found games first, then the ones JoyPro did not find; the engine's order within each.
        foreach (GameInstallItem install in GameInstalls.OrderBy(g => g.Installed || g.Overridden || g.Detected ? 0 : 1))
            GameSetupItems.Add(new GameSetupItem(install));

        List<GameInstallItem> found = GameInstalls.Where(g => g.Detected).ToList();
        List<GameInstallItem> linked = GameInstalls.Where(g => g.Overridden).ToList();
        int notFound = GameInstalls.Count - found.Count - linked.Count;
        List<string> lines = [];
        lines.Add(found.Count == 0
            ? T("JoyPro did not find any of its {0} games on this PC.", GameInstalls.Count)
            // A few found games are named; many are only counted (the list below shows each of them).
            : T("JoyPro found {0} of its {1} games on this PC{2}", found.Count, GameInstalls.Count, (found.Count <= 5 ? ": " + string.Join(", ", found.Select(g => g.Name + " (" + g.Source + ")")) + "." : T(" (listed below, found ones first)."))));
        if (linked.Count > 0)
            lines.Add(T("Linked by you: {0}.", string.Join(", ", linked.Select(g => g.Name))));
        if (notFound > 0)
            lines.Add(T("{0} not found: they stay unticked and JoyPro finds them once they are installed. If you have one of them (a portable install, another drive), tick it and link its folder.", notFound));
        lines.Add(T("Untick a found game you do not have to hide it."));
        GameSetupSummary = string.Join(" ", lines);
    }

    // "Later" or closing the window: nothing is written or hidden; the first-start setup is not shown again
    // (Settings > Games runs it again).
    public void SkipGameSetup()
    {
        MarkGameSetupDone();
    }

    // Done: the links, config locations and "I have this game" ticks go to the engine; then the games are detected
    // again. False (and GameSetupStatus says why) when the engine refused something.
    public async Task<bool> ApplyGameSetupAsync()
    {
        List<string> problems = [];
        foreach (GameSetupItem item in GameSetupItems)
        {
            if (item.LinkChanged)
            {
                List<string> args = item.HasLink
                    ? ["games", "install", "set", item.GameId, item.LinkedFolder]
                    : ["games", "install", "clear", item.GameId];
                await RunSetupStepAsync(args, item.Name, problems);
            }
            if (item.ConfigLinkChanged)
            {
                List<string> args = item.HasConfigLink
                    ? ["games", "config", "set", item.GameId, item.ConfigLink]
                    : ["games", "config", "clear", item.GameId];
                await RunSetupStepAsync(args, item.Name, problems);
            }
        }
        // Only games the user unticked (found or linked ones) or marked "I don't have it" are hidden; a game JoyPro did
        // not find stays "not installed", so it is detected once it is installed.
        List<string> disable = GameSetupItems.Where(i => i.HideOnDone).Select(i => i.GameId).ToList();
        List<string> enable = GameSetupItems.Where(i => i.ShowOnDone).Select(i => i.GameId).ToList();
        if (disable.Count > 0)
            await RunSetupStepAsync(["games", "install", "disable", .. disable], "Hiding the games you do not have", problems);
        if (enable.Count > 0)
            await RunSetupStepAsync(["games", "install", "enable", .. enable], "Showing the games you have", problems);

        await RefreshGameInstallsAsync();
        MarkGameSetupDone();
        if (problems.Count > 0)
        {
            GameSetupStatus = string.Join(" ", problems);
            return false;
        }
        int owned = GameSetupItems.Count(i => i.HasGame);
        bool written = disable.Count + enable.Count > 0 || GameSetupItems.Any(i => i.LinkChanged || i.ConfigLinkChanged);
        StatusText = written
            ? Plural("Game setup saved: {0} game you have; changes written.", "Game setup saved: {0} games you have; changes written.", owned)
            : Plural("Game setup saved: {0} game you have.", "Game setup saved: {0} games you have.", owned);
        // JoyPro ships no game data: the command list of every game the user has is built now from the game's own files
        // (later ones from the Start page notice or the game card's Update).
        _ = BuildCommandListsAfterSetupAsync();
        return true;
    }

    private async Task BuildCommandListsAfterSetupAsync()
    {
        try
        {
            await RunDbUpdateCheckAsync(["db", "check-updates"], "Checking which games need a command list...");
            if (HasMissingCommandLists)
                await BuildMissingCommandListsAsync();
        }
        catch (Exception ex)
        {
            JoyProLog.Warn("Setup", "Building the command lists after the setup failed: " + ex.Message);
        }
    }

    private async Task RunSetupStepAsync(List<string> args, string what, List<string> problems)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false,
            busyLabel: "Game setup: " + what + "...", forceText: false);
        if (result == null)
            problems.Add(T("{0}: the engine did not run.", what));
        else if (result.ExitCode != 0)
            problems.Add(what + ": " + BuildFailureStatus(result));
    }

    private void MarkGameSetupDone()
    {
        if (gameSetupDone)
            return;
        gameSetupDone = true;
        SaveUiSettings();
    }
}
