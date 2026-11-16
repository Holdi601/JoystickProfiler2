using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;

namespace JoyPro.Gui.ViewModels;

// What the welcome and the tours remember (ui-settings.json "tours"): the welcome was answered (once per user, also for
// users coming from 2.6), the path picked, essentials or every feature, the tours seen, and the tour in progress with its
// step (it goes on after a restart).
public sealed class ToursSettings
{
    public bool WelcomeDone { get; set; }

    public string WelcomeVersion { get; set; } = string.Empty;

    public string Choice { get; set; } = string.Empty;

    public bool EveryFeature { get; set; }

    public List<string> Seen { get; set; } = [];

    public string ActiveTour { get; set; } = string.Empty;

    public int ActiveStep { get; set; }

    public bool ActiveEveryFeature { get; set; }

    // Easy words: big cards, the shortest texts (German "du"), help that leads with one plain sentence.
    public bool EasyWords { get; set; }
}

// One answer of the welcome ("What do you want to do?").
public sealed partial class WelcomeOption : ObservableObject
{
    public WelcomeOption(string id, string title, string description)
    {
        Id = id;
        Title = title;
        Description = description;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isSuggested;

    // One answer at a time: the welcome unticks the others (no radio group name, so windows never share a group).
    internal Action<WelcomeOption>? Picked { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            Picked?.Invoke(this);
    }
}

// A tour in Settings > Tours.
public sealed class TourListItem(string id, string title, string description, bool seen)
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public bool Seen { get; } = seen;

    public string SeenText => Seen ? T("seen") : string.Empty;
}

// The welcome (docs/ui/help-and-tours.md): everyone who opens 2.7.0 or newer for the first time is asked once what they
// want to do, with the answer suggested from the installed games, and essentials or every feature. Each answer starts its
// own path: the pad tour, the DCS tour, the quick path (MainWindowViewModel.AutoSetupStart.cs: the setup run by itself up to its review), the newcomer's tour or the
// JoyPro 1 tour. Never on test and sandbox runs; "--welcome" (the "JoyPro 2 - Tour" entry) always opens it.
// The tours point at the real controls (the window dims the rest and outlines the control) and wait for the user's click
// or page change; Back / Next / Skip / End. The step in progress is saved, so a tour goes on after a restart.
public sealed partial class MainWindowViewModel
{
    private ToursSettings toursState = new();
    private bool welcomeAfterSetup;

    // Tests: the welcome's once-per-user rule with the test run's own settings (the sandbox otherwise blocks it).
    internal bool WelcomeIgnoresSandbox { get; set; }

    public ObservableCollection<WelcomeOption> WelcomeOptions { get; } = [];

    [ObservableProperty]
    private bool isWelcomeOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WelcomeEssentials))]
    private bool welcomeEveryFeature;

    public bool WelcomeEssentials
    {
        get => !WelcomeEveryFeature;
        set => WelcomeEveryFeature = !value;
    }

    [ObservableProperty]
    private string welcomeSuggestion = string.Empty;

    // The welcome's main button follows the answer: "Close" for "No tour" (nothing starts), "Start" otherwise.
    public bool IsWelcomeNoTourPicked => (WelcomeOptions.FirstOrDefault(o => o.IsSelected)?.Id ?? TourCatalog.NoTour) == TourCatalog.NoTour;

    public bool IsWelcomeTourPicked => !IsWelcomeNoTourPicked;

    public string WelcomeStartLabel => IsWelcomeNoTourPicked ? T("Close") : T("Start");

    public string WelcomeStartTip => IsWelcomeNoTourPicked
        ? T("Closes the welcome without a tour")
        : T("Starts the way you picked");

    private void RaiseWelcomeChoiceChanged()
    {
        OnPropertyChanged(nameof(IsWelcomeNoTourPicked));
        OnPropertyChanged(nameof(IsWelcomeTourPicked));
        OnPropertyChanged(nameof(WelcomeStartLabel));
        OnPropertyChanged(nameof(WelcomeStartTip));
    }

    // The installed games the suggestion was made from (tests can set their own list).
    internal Func<IReadOnlyList<string>>? InstalledGamesForWelcome { get; set; }

    // The connected devices the suggestion was made from (tests): a flight stick, throttle or pedals connected (null: not
    // known yet), and only a gamepad.
    internal Func<(bool? Stick, bool GamepadOnly)>? DevicesForWelcome { get; set; }

    // The games that have no buttons set yet (tests; else the Start page's "nothing assigned" games).
    internal Func<IReadOnlyCollection<string>>? GamesWithoutBindsForWelcome { get; set; }

    // What the Start page knows about the connected devices: a flight device (null until the devices were looked at
    // once), and whether a gamepad is all there is.
    internal (bool? Stick, bool GamepadOnly) WelcomeDevices()
    {
        if (DevicesForWelcome != null)
            return DevicesForWelcome();
        if (!deviceCardsLoadedOnce)
            return (null, false);
        List<DeviceCardItem> real = DeviceCards.Where(c => c.Connected && !c.IsVirtual && !c.IsKeyboardLike).ToList();
        bool stick = real.Any(c => c.Kind is "stick" or "hotas" or "yoke" or "base" or "throttle" or "quadrant" or "collective" or "pedals");
        bool pad = real.Any(IsGamepadCard);
        return (stick, pad && !stick);
    }

    private static bool IsGamepadCard(DeviceCardItem card) =>
        card.Kind == "gamepad" || (card.Kind == "controller" && card.ModelName.Length == 0); // nonloc

    private IReadOnlyCollection<string> WelcomeGamesWithoutBinds() =>
        GamesWithoutBindsForWelcome?.Invoke() ?? AutoSetupNewGames.Select(g => g.GameId).ToList();

    // The devices or the games' binds became known while the welcome is open: the suggestion follows (an answer the user
    // picked stays).
    private void RefreshOpenWelcome()
    {
        if (IsWelcomeOpen)
            BuildWelcomeOptions();
    }

    private string welcomeSuggested = string.Empty;

    public bool ShouldShowWelcome => persistUiFiles && !toursState.WelcomeDone && (WelcomeIgnoresSandbox || !IsWelcomeBlockedRun());

    internal ToursSettings ToursState => toursState;

    private static bool IsWelcomeBlockedRun() =>
        UserFolders.IsSandboxed || Environment.GetEnvironmentVariable("JOYPRO_NO_WELCOME") == "1";

    // After the startup detection: the welcome once, after the first-start game setup when that shows.
    internal void ShowWelcomeAfterStartupChecks(bool setupShown)
    {
        if (setupShown)
            welcomeAfterSetup = true;
        else
            ShowWelcomeIfNeeded();
        if (!IsWelcomeOpen && !welcomeAfterSetup)
            ResumeSavedTour();
    }

    private void OnGameSetupClosedForWelcome()
    {
        if (!welcomeAfterSetup)
            return;
        welcomeAfterSetup = false;
        // The first start's "Done, and set everything up for me": the quick setup opens instead of the welcome (one
        // window at a time); the welcome counts as answered with the quick way.
        if (firstStartQuickPath)
        {
            MarkWelcomeDone(TourCatalog.Quick);
            return;
        }
        ShowWelcomeIfNeeded();
    }

    private bool firstStartQuickPath;

    public void BeginFirstStartQuickPath() => firstStartQuickPath = true;

    // The games could not be saved: the window stays open with the reason (the welcome counts as answered with the quick
    // way, which the user picked).
    public void CancelFirstStartQuickPath() => firstStartQuickPath = false;

    public void ShowWelcomeIfNeeded()
    {
        if (ShouldShowWelcome)
            OpenWelcome();
    }

    // The welcome now (Settings > Tours, the ? menu, "--welcome").
    [RelayCommand]
    public void OpenWelcome()
    {
        IsHelpModeOn = false;
        BuildWelcomeOptions();
        IsWelcomeOpen = true;
    }

    // "--welcome" from a start entry or a second start: the welcome even when it was answered before.
    public void RequestWelcome() => OpenWelcome();

    public IReadOnlyList<string> InstalledGameIds()
    {
        if (InstalledGamesForWelcome != null)
            return InstalledGamesForWelcome();
        List<string> ids = GameInstalls.Where(g => g.Installed && !g.Disabled).Select(g => g.GameId).ToList();
        if (ids.Count == 0)
            ids = GameCards.Where(c => c.Installed).Select(c => c.GameId).ToList();
        return ids;
    }

    private void BuildWelcomeOptions()
    {
        IReadOnlyList<string> games = InstalledGameIds();
        (bool? stick, bool gamepadOnly) = WelcomeDevices();
        string suggested = TourCatalog.SuggestPath(games, HasJoyPro1Profile, stick, gamepadOnly, WelcomeGamesWithoutBinds());
        string selected = WelcomeOptions.FirstOrDefault(o => o.IsSelected)?.Id ?? string.Empty;
        // (an answer the user did not change follows a new suggestion)
        if (selected == welcomeSuggested)
            selected = string.Empty;
        welcomeSuggested = suggested;
        WelcomeOptions.Clear();
        // The flight-stick way to pad games only when a flight stick is connected (or the devices are not known yet).
        foreach (TourDefinition path in TourCatalog.Paths.Where(p => p.Id != TourCatalog.Pad || stick != false))
            WelcomeOptions.Add(new WelcomeOption(path.Id, T(EasyWords && path.EasyTitle.Length > 0 ? path.EasyTitle : path.Title),
                T(EasyWords && path.EasyDescription.Length > 0 ? path.EasyDescription : path.Description)));
        WelcomeOptions.Add(new WelcomeOption(TourCatalog.NoTour, T("No tour"),
            EasyWords ? T("Maybe later. The Help button has the tours.") : T("Start any tour later: the Help button > Tours, or Settings > Tours.")));
        foreach (WelcomeOption option in WelcomeOptions)
        {
            option.IsSuggested = option.Id == suggested;
            option.Picked = picked =>
            {
                foreach (WelcomeOption other in WelcomeOptions)
                    if (!ReferenceEquals(other, picked))
                        other.IsSelected = false;
                RaiseWelcomeChoiceChanged();
            };
        }
        string pick = selected.Length > 0 ? selected : suggested;
        foreach (WelcomeOption option in WelcomeOptions)
            option.IsSelected = option.Id == pick;
        RaiseWelcomeChoiceChanged();
        WelcomeEveryFeature = toursState.EveryFeature;
        WelcomeSuggestion = WelcomeSuggestionText(suggested, games, gamepadOnly);
    }

    private string WelcomeSuggestionText(string suggested, IReadOnlyList<string> games, bool gamepadOnly = false)
    {
        List<string> names = games.Select(GameNameOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        // Only an Xbox controller and only games that read it: nothing to set up, the controller already works.
        if (gamepadOnly && names.Count > 0 && games.All(g => TourCatalog.PadGames.Contains(g)))
            return EasyWords
                ? T("Good news! Your controller already works in {0}. Just start the game and play.", JoinAnd(names))
                : T("Good news: your controller already works in {0}. Just start the game and play. JoyPro is for flight sticks and throttles; your controller needs nothing from it.", JoinAnd(names));
        if (suggested == TourCatalog.JoyPro1 && HasJoyPro1Profile)
            return EasyWords
                ? T("JoyPro found your JoyPro 1 profile. So it picked an answer for you.")
                : T("JoyPro found your JoyPro 1 profile, so the answer marked \"suggested\" is picked. Pick another if it fits you better.");
        if (names.Count == 0)
            return EasyWords
                ? T("JoyPro found no game yet. So it picked the quick way for you.")
                : T("JoyPro has not found a game yet, so the quick way is picked. Pick another answer if it fits you better.");
        string found = string.Join(", ", names) + (games.Count > names.Count ? ", ..." : string.Empty);
        return EasyWords
            ? T("JoyPro found {0}. So it picked an answer for you.", found)
            : T("JoyPro found {0} on this PC, so the answer marked \"suggested\" is picked. Pick another if it fits you better.", found);
    }

    private string GameNameOf(string gameId) =>
        GameInstalls.FirstOrDefault(g => string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? GameCards.FirstOrDefault(c => string.Equals(c.GameId, gameId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? gameId;

    [RelayCommand]
    private async Task StartWelcomeChoiceAsync()
    {
        string choice = WelcomeOptions.FirstOrDefault(o => o.IsSelected)?.Id ?? TourCatalog.NoTour;
        MarkWelcomeDone(choice);
        IsWelcomeOpen = false;
        if (choice == TourCatalog.NoTour)
        {
            StatusText = T("No tour. The Help button > Tours or Settings > Tours starts one any time.");
            return;
        }
        if (choice == TourCatalog.Quick)
        {
            // The quick setup first; its "Write to my game" starts the quick tour (QuickExportAsync).
            await StartQuickSetupAsync(tourAfter: true);
            return;
        }
        StartTour(choice, WelcomeEveryFeature);
    }

    // ---- easy words ----

    // Settings > Tours and the welcome: big cards, the shortest texts (German "du"), help that leads with one plain
    // sentence and keeps the details behind "More details". Kept in ui-settings.json ("tours" > "easyWords").
    [ObservableProperty]
    private bool easyWords;

    partial void OnEasyWordsChanged(bool value)
    {
        if (toursState.EasyWords != value)
        {
            toursState.EasyWords = value;
            SaveUiSettings();
        }
        IsHelpDetailsOpen = !value;
        if (IsWelcomeOpen)
            BuildWelcomeOptions();
        if (ActiveTour != null)
            RaiseTourCard();
        else
            RaiseTourTexts();
        OnPropertyChanged(nameof(TourIsBig));
        OnPropertyChanged(nameof(TourList));
    }

    // "Not now" / "No tour": the welcome counts as answered (Settings > Tours brings it back).
    [RelayCommand]
    private void SkipWelcome()
    {
        MarkWelcomeDone(TourCatalog.NoTour);
        IsWelcomeOpen = false;
        StatusText = T("No tour. The Help button > Tours or Settings > Tours starts one any time.");
    }

    private void MarkWelcomeDone(string choice)
    {
        toursState.WelcomeDone = true;
        toursState.WelcomeVersion = AppInfo.Version;
        toursState.Choice = choice;
        toursState.EveryFeature = WelcomeEveryFeature;
        ToursEveryFeature = WelcomeEveryFeature;
        SaveUiSettings();
    }

    // ---- tours ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTourActive), nameof(TourIsBig), nameof(VirtualJoystickNavTitle))]
    private TourDefinition? activeTour;

    // The game a tour is about ("{0}" in its texts): the games of the last quick setup, else the installed ones.
    private IReadOnlyList<string> tourGameIds = [];

    public IReadOnlyList<string> TourGameIds => tourGameIds.Count > 0 ? tourGameIds : InstalledGameIds();

    public string TourGameName
    {
        get
        {
            List<string> names = TourGameIds.Select(GameNameOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            return names.Count == 0 ? T("your game") : JoinAnd(names);
        }
    }

    private IReadOnlyList<TourStep> activeSteps = [];
    private bool activeEveryFeature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTourStep), nameof(TourTitle), nameof(TourText), nameof(TourCounter), nameof(TourCanGoBack),
        nameof(TourNextLabel), nameof(TourWaits), nameof(TourWaitHint), nameof(TourTargetKey))]
    private int tourStepIndex;

    public bool IsTourActive => ActiveTour != null;

    public bool TourIsBig => ActiveTour?.Big == true || EasyWords;

    public TourStep? CurrentTourStep => ActiveTour != null && TourStepIndex >= 0 && TourStepIndex < activeSteps.Count ? activeSteps[TourStepIndex] : null;

    public string TourTitle => CurrentTourStep is not { } step
        ? string.Empty
        : TourWaitsForSetup
            ? T("Finish the setup window first")
            : T(EasyWords && step.EasyTitle.Length > 0 ? step.EasyTitle : step.Title);

    public string TourText
    {
        get
        {
            if (CurrentTourStep is not { } step)
                return string.Empty;
            // The quick setup window is open: the card waits for it (never runs ahead of it).
            if (TourWaitsForSetup && step.Waiting.Length > 0)
                return StepText(EasyWords && step.WaitingEasy.Length > 0 ? step.WaitingEasy : step.Waiting);
            // Next or Skip on a step whose missing part breaks what follows: what will not work, before it goes on.
            if (tourSkipWarned && step.WarnSkip.Length > 0)
                return StepText(EasyWords && step.WarnSkipEasy.Length > 0 ? step.WarnSkipEasy : step.WarnSkip);
            if (TourInSecondPart && step.Then.Length > 0)
                return StepText(EasyWords && step.ThenEasy.Length > 0 ? step.ThenEasy : step.Then);
            if (EasyWords && step.Easy.Length > 0)
                return StepText(step.Easy);
            return StepText(step.Text) + (step.Extra.Length > 0 && !EasyWords ? "\n\n" + StepText(step.Extra) : string.Empty);
        }
    }

    // The quick setup window, opened by the pad tour's setup step, is still open: the card waits for it.
    private bool tourWaitsForSetup;

    public bool TourWaitsForSetup => tourWaitsForSetup && CurrentTourStep?.Action == TourActions.QuickSetup;

    // The step's control was clicked (TourConditions.Clicked: Test, then "now move your stick").
    private bool tourStepClicked;

    // Next or Skip was pressed once on a step with WarnSkip: the card says what will not work; the next press goes on.
    private bool tourSkipWarned;

    // The quick tour started right after "Write to my game": it begins with "It worked!".
    private bool tourJustWritten;

    // The step a tour started at (the pad steps after the first start's quick path): the counter counts from there.
    private int tourFirstIndex;

    // The second part of a two-part step (TourStep.ThenWhen) shows.
    public bool TourInSecondPart => CurrentTourStep?.ThenWhen switch
    {
        TourConditions.Clicked => tourStepClicked,
        TourConditions.ViGEmConsent => VirtualJoystickViGEmComponent?.Consent == true,
        _ => false
    };

    // The layer listens for a click on the step's control (a waiting step, or a two-part step).
    public bool TourListensForClick => CurrentTourStep is { } step && !TourWaitsForSetup && (step.WaitClick || step.ThenWhen.Length > 0);

    // "{0}" in a step's text is the tour's game.
    private string StepText(string english) => english.Contains("{0}", StringComparison.Ordinal) ? T(english, TourGameName) : T(english);

    public string TourCounter => ActiveTour == null ? string.Empty : T("Step {0} of {1}", TourStepIndex - tourFirstIndex + 1, activeSteps.Count - tourFirstIndex);

    // The small drawn picture of the step (TourPictures), empty: none.
    public string TourPicture => CurrentTourStep?.Picture ?? string.Empty;

    public bool TourHasPicture => TourPicture.Length > 0;

    public string TourPictureCaption => TourHasPicture ? T(JoyPro.Gui.Views.Controls.TourPicture.Caption(TourPicture)) : string.Empty;

    // The card's own button that does the step ("Set it all up for me"); hidden while its window is open.
    public bool TourShowsQuickSetupAction => CurrentTourStep?.Action == TourActions.QuickSetup && !TourWaitsForSetup;

    // The waiting hint, except where the card's own button is the one to click (one blue button at a time), while the
    // setup window is open, in a step's second part and after the skip warning.
    public bool TourShowsWaitHint => TourWaits && !TourShowsQuickSetupAction && !TourWaitsForSetup && !TourInSecondPart && !tourSkipWarned;

    private void RaiseTourTexts()
    {
        OnPropertyChanged(nameof(TourTitle));
        OnPropertyChanged(nameof(TourText));
        OnPropertyChanged(nameof(TourWaitHint));
        OnPropertyChanged(nameof(TourNextLabel));
        OnPropertyChanged(nameof(TourWordsLabel));
    }

    // Everything the card shows; the layer looks for the step's control again (its target may have changed).
    private void RaiseTourCard()
    {
        RaiseTourTexts();
        OnPropertyChanged(nameof(TourCounter));
        OnPropertyChanged(nameof(TourWaits));
        OnPropertyChanged(nameof(TourCanGoBack));
        OnPropertyChanged(nameof(TourTargetKey));
        OnPropertyChanged(nameof(TourShowsJoyPro1Offer));
        OnPropertyChanged(nameof(TourShowsQuickSetupAction));
        OnPropertyChanged(nameof(TourShowsWaitHint));
        OnPropertyChanged(nameof(TourShowsSkip));
        OnPropertyChanged(nameof(TourWaitsForSetup));
        OnPropertyChanged(nameof(TourInSecondPart));
        OnPropertyChanged(nameof(TourPicture));
        OnPropertyChanged(nameof(TourHasPicture));
        OnPropertyChanged(nameof(TourPictureCaption));
        TourStepChanged?.Invoke();
    }

    public bool TourCanGoBack => TourStepIndex > tourFirstIndex;

    // While the setup window is open, Next brings it to the front.
    public string TourNextLabel => TourWaitsForSetup ? T("Show the setup window") : TourStepIndex >= activeSteps.Count - 1 ? T("Finish") : T("Next");

    // Skip on the last step would do the same as Finish: hidden there. While the card waits for the setup window, Skip
    // would run ahead of it: hidden too (Next brings the window to the front).
    public bool TourShowsSkip => TourStepIndex < activeSteps.Count - 1 && !TourWaitsForSetup;

    public bool TourWaits => CurrentTourStep is { } step && (step.Waits || step.WaitFor.Length > 0);

    public string TourWaitHint => CurrentTourStep?.WaitStage != null
        ? (EasyWords ? T("Click the glowing button. Or click Next.") : T("Waiting for you to click the glowing button. Or press Next, and JoyPro opens the page for you."))
        : CurrentTourStep?.WaitClick == true
            ? (EasyWords ? T("Click the glowing button. Or click Next.") : T("Waiting for you to click the glowing button. Or press Next to go on."))
            : CurrentTourStep?.WaitFor == TourConditions.BindSelected
                ? (EasyWords ? T("Click a row in the list. Or click Next.") : T("Waiting for you to click a row in the list. Or press Next, and JoyPro picks the first one."))
                : string.Empty;

    public string TourTargetKey => CurrentTourStep is not { } step
        ? string.Empty
        : TourWaitsForSetup
            ? "none"
            : TourInSecondPart && step.ThenTarget.Length > 0 ? step.ThenTarget : step.Target;

    // "Easier words" on every tour card (the first start may skip the welcome, where the switch is offered).
    public string TourWordsLabel => EasyWords ? T("Normal words") : T("Easier words");

    [RelayCommand]
    private void ToggleEasyWords() => EasyWords = !EasyWords;

    // The window finds the step's control and draws the highlight when this is raised.
    public event Action? TourStepChanged;

    public IReadOnlyList<TourListItem> TourList =>
        TourCatalog.Paths.Select(t => new TourListItem(t.Id, T(EasyWords && t.EasyTitle.Length > 0 ? t.EasyTitle : t.Title),
                T(EasyWords && t.EasyDescription.Length > 0 ? t.EasyDescription : t.Description), toursState.Seen.Contains(t.Id)))
            .Concat(TourCatalog.PageTourPages.Select(p => TourCatalog.PageTour(p)!)
                .Select(t => new TourListItem(t.Id, T("Tour of the {0} page", T(t.Title)), T(t.Description), toursState.Seen.Contains(t.Id))))
            .ToList();

    // Settings > Tours: essentials or every feature for the tours started there (kept with the welcome's answer).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToursEssentials))]
    private bool toursEveryFeature;

    partial void OnToursEveryFeatureChanged(bool value)
    {
        if (toursState.EveryFeature == value)
            return;
        toursState.EveryFeature = value;
        SaveUiSettings();
    }

    public bool ToursEssentials
    {
        get => !ToursEveryFeature;
        set => ToursEveryFeature = !value;
    }

    // countFromStep: the counter starts at that step (a tour joined in the middle); justWritten: the quick tour right
    // after "Write to my game" (it begins with "It worked!").
    public void StartTour(string id, bool everyFeature, int step = 0, bool countFromStep = false, bool justWritten = false)
    {
        TourDefinition? tour = TourCatalog.Find(id);
        if (tour == null)
            return;
        IsHelpModeOn = false;
        IsWelcomeOpen = false;
        activeEveryFeature = everyFeature;
        activeSteps = tour.StepsFor(everyFeature);
        if (activeSteps.Count == 0)
            return;
        tourJustWritten = justWritten;
        tourWaitsForSetup = false;
        int start = Math.Clamp(step, 0, activeSteps.Count - 1);
        tourFirstIndex = countFromStep ? start : 0;
        ActiveTour = tour;
        GoToTourStep(start);
    }

    // Settings > Tours: a tour with the essentials / every feature choice of the list (the quick tour too: it shows
    // where to change a button later; the quick setup itself is the Start page's "Set it all up for me").
    [RelayCommand]
    private void StartTourById(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            StartTour(id, ToursEveryFeature);
    }

    // The tour's own button on the card ("Set it all up for me"): does the step for the user.
    [RelayCommand]
    private void TourDoAction()
    {
        if (CurrentTourStep?.Action == TourActions.QuickSetup)
        {
            // The card waits for the setup window (it never runs ahead of it): "Set up the pretend controller" there goes
            // on to the pad steps (ContinueWithPadSteps); closing it brings the step back.
            tourWaitsForSetup = true;
            RaiseTourCard();
            _ = StartQuickSetupAsync();
        }
    }

    // The quick setup's window closed. Applied (the buttons are in JoyPro): the pad tour goes on with the pad steps;
    // closed without them: the setup step shows again.
    public void OnSetupWizardWindowClosed()
    {
        if (!TourWaitsForSetup)
            return;
        tourWaitsForSetup = false;
        if (SetupWizard is { Applied: true } wizard && ActiveTour?.Id == TourCatalog.Pad)
            ContinueWithPadSteps(wizard.ChosenGames);
        else
            RaiseTourCard();
    }

    // The quick setup's "Set up the pretend controller" (the games are pad games): the Virtual Joystick page and the pad tour's steps
    // there (the helper driver when it is missing, Test, when to press Start).
    public void ContinueWithPadSteps(IReadOnlyCollection<string> gameIds)
    {
        if (gameIds.Count > 0)
            tourGameIds = gameIds.ToList();
        tourWaitsForSetup = false;
        if (ActiveTour?.Id == TourCatalog.Pad)
        {
            // The pad tour is running (it opened the quick setup): opening the page goes on to its next step.
            CurrentStage = UiStage.VirtualJoystick;
            if (CurrentTourStep is { } step && step.Stage != UiStage.VirtualJoystick)
                GoToTourStep(TourCatalog.PadFinishIndex(activeSteps));
            return;
        }
        CurrentStage = UiStage.VirtualJoystick;
        TourDefinition pad = TourCatalog.Find(TourCatalog.Pad)!;
        StartTour(TourCatalog.Pad, everyFeature: false, TourCatalog.PadFinishIndex(pad.StepsFor(false)), countFromStep: true);
    }

    // After the quick path's "Write to my game": the quick tour (where to change a button later, how to write again).
    public void StartQuickTourAfterSetup(IReadOnlyCollection<string> gameIds)
    {
        if (gameIds.Count > 0)
            tourGameIds = gameIds.ToList();
        // The success first, on the Start page (not on the Export page and its warnings).
        CurrentStage = UiStage.Start;
        StartTour(TourCatalog.Quick, everyFeature: false, justWritten: true);
    }

    // A step left out now (TourStep.SkipIf).
    private bool SkipsStep(TourStep step) => step.SkipIf switch
    {
        TourConditions.ViGEmInstalled => ViGEmInstalled,
        TourConditions.PadGamesOnly => TourGameIds.Count > 0 && TourGameIds.All(g => TourCatalog.PadGames.Contains(g)),
        TourConditions.NotJustWritten => !tourJustWritten,
        TourConditions.FromTheStart => tourFirstIndex == 0,
        _ => false
    };

    [RelayCommand]
    private void StartPageTour() => StartTour("page:" + CurrentHelpPage, everyFeature: true);

    [RelayCommand]
    private void TourNext()
    {
        if (CurrentTourStep is not { } step)
            return;
        if (TourWaitsForSetup)
        {
            // The setup window is open: Next brings it to the front (the card keeps waiting for it).
            if (SetupWizard != null)
                SetupWizardRequested?.Invoke();
            else
                OnSetupWizardWindowClosed();
            return;
        }
        if (step.Action == TourActions.QuickSetup)
        {
            // Next does the step: the quick setup opens, the card waits for its window.
            TourDoAction();
            return;
        }
        if (WarnsBeforeSkip(step))
            return;
        if (step.WaitFor == TourConditions.BindSelected)
        {
            // Next does the step: the first game control is picked (the pick goes on by itself).
            SelectFirstBind();
            if (ReferenceEquals(CurrentTourStep, step))
                AdvanceTour();
            return;
        }
        if (step.WaitStage is UiStage wanted && CurrentStage != wanted)
        {
            // Next does the step: the page opens (also a page hidden in the bar), and the stage change goes on.
            CurrentStage = wanted;
            if (CurrentTourStep == step)
                AdvanceTour();
            return;
        }
        AdvanceTour();
    }

    [RelayCommand]
    private void TourSkip()
    {
        // The card waits for the setup window: Skip does what Next does (the window to the front), never runs ahead.
        if (TourWaitsForSetup)
        {
            TourNext();
            return;
        }
        if (CurrentTourStep is { } step && WarnsBeforeSkip(step))
            return;
        AdvanceTour();
    }

    // The first Next or Skip on a step whose missing part breaks what follows (the helper ViGEmBus): the card says what
    // will not work; the second press goes on.
    private bool WarnsBeforeSkip(TourStep step)
    {
        if (step.WarnSkip.Length == 0 || tourSkipWarned || SkipsStep(step))
            return false;
        tourSkipWarned = true;
        RaiseTourCard();
        return true;
    }

    private void SelectFirstBind()
    {
        if (SelectedBind == null && Binds.Count > 0)
            SelectedBind = Binds[0];
    }

    [RelayCommand]
    private void TourBack()
    {
        int index = TourStepIndex - 1;
        while (index > tourFirstIndex && SkipsStep(activeSteps[index]))
            index--;
        if (index >= tourFirstIndex && index < TourStepIndex)
            GoToTourStep(index);
    }

    [RelayCommand]
    private void EndTour() => FinishTour(completed: false);

    // The window saw a click on the step's control.
    public void TourTargetClicked()
    {
        if (CurrentTourStep is not { } step || TourWaitsForSetup)
            return;
        if (step.Action == TourActions.QuickSetup)
        {
            // The page's own "Set it all up for me" ran the quick setup: the card waits for its window.
            tourWaitsForSetup = true;
            RaiseTourCard();
            return;
        }
        if (step.ThenWhen == TourConditions.Clicked)
        {
            // Test was clicked: the card stays and says to move the stick; Next goes on.
            tourStepClicked = true;
            RaiseTourCard();
            return;
        }
        if (step.ThenWhen.Length > 0)
        {
            RaiseTourCard();
            return;
        }
        if (step.WaitClick)
            AdvanceTour();
    }

    private void OnTourStageChanged(UiStage stage)
    {
        if (CurrentTourStep?.WaitStage == stage)
            AdvanceTour();
    }

    private void AdvanceTour()
    {
        if (ActiveTour == null)
            return;
        int next = TourStepIndex + 1;
        while (next < activeSteps.Count && SkipsStep(activeSteps[next]))
            next++;
        if (next >= activeSteps.Count)
            FinishTour(completed: true);
        else
            GoToTourStep(next);
    }

    private void GoToTourStep(int index)
    {
        // A step left out now (the helper is installed already) goes on to the next one.
        while (index < activeSteps.Count - 1 && SkipsStep(activeSteps[index]))
            index++;
        tourStepClicked = false;
        tourSkipWarned = false;
        if (activeSteps[index].Action != TourActions.QuickSetup)
            tourWaitsForSetup = false;
        TourStepIndex = index;
        OnPropertyChanged(nameof(CurrentTourStep));
        TourStep step = activeSteps[index];
        if (step.Stage is UiStage stage && step.WaitStage == null && CurrentStage != stage)
            CurrentStage = stage;
        if (step.Target.StartsWith("section:", StringComparison.Ordinal))
            OpenSettingsSection((object)step.Target[8..]);
        // Record input shows only with a game control picked: the step picks one when none is.
        if (step.Prepare == TourPrepare.SelectBind)
            SelectFirstBind();
        WatchTourConsent(step);
        toursState.ActiveTour = ActiveTour?.Id ?? string.Empty;
        toursState.ActiveStep = index;
        toursState.ActiveEveryFeature = activeEveryFeature;
        SaveUiSettings();
        RaiseTourCard();
    }

    // The helper's license box (TourConditions.ViGEmConsent), ticked here or in the list of components: the card goes
    // on to "now click Install" and points at the button.
    private ComponentItem? watchedConsent;

    private void WatchTourConsent(TourStep? step)
    {
        if (watchedConsent != null)
            watchedConsent.PropertyChanged -= OnWatchedConsentChanged;
        watchedConsent = step?.ThenWhen == TourConditions.ViGEmConsent ? VirtualJoystickViGEmComponent : null;
        if (watchedConsent != null)
            watchedConsent.PropertyChanged += OnWatchedConsentChanged;
    }

    private void OnWatchedConsentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComponentItem.Consent) && CurrentTourStep?.ThenWhen == TourConditions.ViGEmConsent)
        {
            // Ticked after Next said "go on without it": the card now says what to do next ("Now click Install"), and
            // the next Next warns again.
            tourSkipWarned = false;
            RaiseTourCard();
        }
    }

    private void FinishTour(bool completed)
    {
        if (ActiveTour is not { } tour)
            return;
        if (completed && !toursState.Seen.Contains(tour.Id))
            toursState.Seen.Add(tour.Id);
        toursState.ActiveTour = string.Empty;
        toursState.ActiveStep = 0;
        ActiveTour = null;
        activeSteps = [];
        tourWaitsForSetup = false;
        tourJustWritten = false;
        tourFirstIndex = 0;
        WatchTourConsent(null);
        SaveUiSettings();
        OnPropertyChanged(nameof(TourList));
        StatusText = completed
            ? T("Tour finished. Press F1 any time and click something to learn what it does.")
            : T("Tour ended. The Help button > Tours starts it again.");
        TourStepChanged?.Invoke();
    }

    // A tour that was running when JoyPro closed goes on at its step.
    public void ResumeSavedTour()
    {
        if (ActiveTour != null || toursState.ActiveTour.Length == 0 || TourCatalog.Find(toursState.ActiveTour) == null)
            return;
        StartTour(toursState.ActiveTour, toursState.ActiveEveryFeature, toursState.ActiveStep);
    }

    // Settings > Tours: the welcome is asked again at the next start; the seen marks go.
    [RelayCommand]
    private void ResetTours()
    {
        // Easy words is a setting of its own: it stays.
        toursState = new ToursSettings { EasyWords = EasyWords };
        SaveUiSettings();
        OnPropertyChanged(nameof(TourList));
        StatusText = T("The welcome shows again at the next start.");
    }

    private void InitializeTours()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CurrentStage))
            {
                OnHelpStageChanged();
                OnTourStageChanged(CurrentStage);
            }
            // The helper driver was found installed while its step shows: that step is done.
            else if (e.PropertyName == nameof(ViGEmInstalled) && ViGEmInstalled && CurrentTourStep?.SkipIf == TourConditions.ViGEmInstalled)
            {
                AdvanceTour();
            }
            // A game control was picked while the step asks for one: done.
            else if (e.PropertyName == nameof(SelectedBind) && SelectedBind != null && CurrentTourStep?.WaitFor == TourConditions.BindSelected)
            {
                AdvanceTour();
            }
        };
    }
}
