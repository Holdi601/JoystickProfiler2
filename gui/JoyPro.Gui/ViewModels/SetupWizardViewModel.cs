using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Setup;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The Setup wizard (docs/setup-wizard.md): 1 games (the ones watched on the Start page), 2 devices and their roles,
// 3 priorities (aircraft order, modifiers, realism), 4 the plan, 5 the review (sheets per device, what could not be
// placed, quality per aircraft; move, swap, remove), then Apply (one engine call = one undo step / history version)
// and Export. Every step runs the engine (engine setup devices | aircraft | plan | render | apply), like the CLI.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class SetupWizardViewModel : ObservableObject
{
    // Runs an engine command; quiet = a background check that leaves the status line alone (cockpit entries).
    public delegate Task<EngineCommandResult?> EngineRunner(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string busyLabel, bool quiet);

    public static readonly string[] StepTitles = [N("Games"), N("Devices"), N("Priorities"), N("Plan"), N("Review")];
    private const string AllAircraft = "";

    private readonly EngineRunner runEngine;
    private readonly Func<Task>? onApplied;
    private readonly Action? onExport;
    private readonly string workFolder;
    private readonly List<SetupAircraftItem> allAircraft = [];
    private readonly Dictionary<string, Task<List<SetupSheetItem>>> sheets = new(StringComparer.OrdinalIgnoreCase);
    private string aircraftGames = string.Empty;
    private int planVersion;

    public SetupWizardViewModel(EngineRunner runEngine, IEnumerable<SetupGameChoice> games, Func<Task>? onApplied = null, Action? onExport = null, string? workFolder = null)
    {
        this.runEngine = runEngine;
        this.onApplied = onApplied;
        this.onExport = onExport;
        this.workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "JoyPro2", "setup-wizard", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < StepTitles.Length; i++)
            Steps.Add(new SetupStepItem(i, T(StepTitles[i])));
        foreach (SetupGameChoice game in games)
            Games.Add(game);
        RoleOptions = SetupRoles.All.Select(r => new SetupRoleOption(r, EngineText.Tr(SetupRoles.Title(r)))).ToList();
        UpdateSteps();
    }

    public ObservableCollection<SetupStepItem> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGamesStep), nameof(IsDevicesStep), nameof(IsPrioritiesStep), nameof(IsPlanStep), nameof(IsReviewStep), nameof(CanGoBack), nameof(NextText), nameof(StepHeading),
        nameof(ShowsGamesStep), nameof(ShowsDevicesStep), nameof(ShowsPrioritiesStep), nameof(ShowsPlanStep), nameof(ShowsNext), nameof(ShowsBack), nameof(ShowsQuickHeadTracking))]
    private int currentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoNext), nameof(CanApply))]
    private bool isWorking;

    [ObservableProperty]
    private string status = string.Empty;

    public bool IsGamesStep => CurrentStep == 0;
    public bool IsDevicesStep => CurrentStep == 1;
    public bool IsPrioritiesStep => CurrentStep == 2;
    public bool IsPlanStep => CurrentStep == 3;
    public bool IsReviewStep => CurrentStep == 4;
    public bool CanGoBack => CurrentStep > 0 && !IsWorking;
    public bool CanGoNext => !IsWorking && CurrentStep < StepTitles.Length - 1;
    public string NextText => CurrentStep == 2 ? T("Make the plan") : CurrentStep == 3 ? T("Review") : T("Next");

    public string StepHeading => QuickMode ? QuickHeading : CurrentStep switch
    {
        0 => T("1. Which games should JoyPro set up?"),
        1 => T("2. Your devices and what each one is for"),
        2 => T("3. What matters most to you"),
        3 => T("4. The plan"),
        _ => T("5. Review: what goes where")
    };

    // The quick path shows no step strip and no step numbers: one plain heading for what is on the screen.
    private string QuickHeading => IsReviewStep && !QuickStuck ? T("Look: what goes where")
        : QuickAsksAircraft ? (Easy ? T("Which planes do you fly?") : T("Which aircraft do you fly?"))
        : QuickPadWorks ? T("Your controller already works")
        : QuickStuck ? T("One thing first")
        : T("Setting it up for you...");

    // The wizard's steps; the quick path shows only its card and the review (never a device list, a Next of the wizard or
    // a step number).
    public bool ShowsGamesStep => IsGamesStep && !QuickMode;

    public bool ShowsDevicesStep => IsDevicesStep && !QuickMode;

    public bool ShowsPrioritiesStep => IsPrioritiesStep && !QuickMode;

    public bool ShowsPlanStep => IsPlanStep && !QuickMode;

    public bool ShowsNext => !QuickMode && !IsReviewStep;

    // The quick review goes back to its aircraft question (when it asked one); the wizard goes back a step.
    public bool ShowsBack => !QuickMode || (IsReviewStep && hadAircraftQuestion && !Applied);

    // The main window's Easy words (German "du", the shortest texts) for the quick path's sentences.
    public Func<bool>? EasyWordsSource { get; set; }

    private bool Easy => EasyWordsSource?.Invoke() == true;

    partial void OnCurrentStepChanged(int value)
    {
        UpdateSteps();
        OnPropertyChanged(nameof(AsksHeadTracking));
        OnPropertyChanged(nameof(ShowsQuickHeadTracking));
    }

    private void UpdateSteps()
    {
        foreach (SetupStepItem step in Steps)
        {
            step.IsCurrent = step.Index == CurrentStep;
            step.IsDone = step.Index < CurrentStep;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (QuickMode)
        {
            // The quick review: back to "Which aircraft do you fly?" (a new answer makes the setup again).
            if (IsReviewStep && hadAircraftQuestion && !Applied)
                AskAircraft();
            return;
        }
        if (CurrentStep > 0)
            CurrentStep--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case 0:
                if (ChosenGames.Count == 0)
                {
                    Status = T("Tick at least one game.");
                    return;
                }
                CurrentStep = 1;
                if (Devices.Count == 0)
                    await DetectDevicesAsync();
                break;
            case 1:
                if (!Devices.Any(d => SetupRoles.Binds(d.Device.Role)))
                {
                    Status = T("Give at least one device a role (main stick, throttle, panel ...).");
                    return;
                }
                CurrentStep = 2;
                await LoadAircraftAsync();
                break;
            case 2:
                CurrentStep = 3;
                await MakePlanAsync();
                break;
            case 3:
                if (Plan == null)
                {
                    await MakePlanAsync();
                    if (Plan == null)
                        return;
                }
                CurrentStep = 4;
                await BuildReviewAsync();
                break;
        }
    }

    // ---- 1 games ----

    public ObservableCollection<SetupGameChoice> Games { get; } = [];

    public List<string> ChosenGames => Games.Where(g => g.IsChosen).Select(g => g.GameId).ToList();

    public bool HasGames => Games.Count > 0;

    // ---- 2 devices ----

    public IReadOnlyList<SetupRoleOption> RoleOptions { get; }

    public ObservableCollection<SetupDeviceItem> Devices { get; } = [];

    public ObservableCollection<string> Rules { get; } = [];

    [RelayCommand]
    private async Task DetectDevicesAsync()
    {
        List<string> args = ["setup", "devices"];
        AddDeviceChoices(args);
        EngineCommandResult? result = await Run(args, false, "Detecting your devices...");
        if (result == null || result.ExitCode != 0)
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
            Devices.Clear();
            foreach (JsonElement item in Array(doc.RootElement, "Devices"))
            {
                SetupDevice device = ReadDevice(item);
                SetupDeviceItem row = new(device, Int(item, "Inputs"), RoleOptions) { Changed = UpdateRules };
                Devices.Add(row);
            }
        }
        catch (JsonException)
        {
            Status = T("The device list could not be read; see Engine Output.");
            return;
        }
        UpdateRules();
        Status = T("{0} devices. Check each role; change it in the list if JoyPro guessed wrong.", Devices.Count);
        InvalidatePlan();
    }

    // The rules in English (the quick path picks its notes from them) and as shown, in the language shown.
    private readonly List<string> englishRules = [];

    private void UpdateRules()
    {
        Rules.Clear();
        englishRules.Clear();
        foreach (string rule in SetupRoles.Rules(Devices.Select(d => d.Device).ToList()))
        {
            englishRules.Add(rule);
            Rules.Add(EngineText.Tr(rule));
        }
        InvalidatePlan();
    }

    private void AddDeviceChoices(List<string> args)
    {
        foreach (SetupDeviceItem device in Devices)
        {
            if (device.SelectedRole != null && device.SelectedRole.Id != device.Device.ProposedRole)
                args.AddRange(["--role", device.Device.Device + "=" + device.SelectedRole.Id]);
            if (device.SelectedGrip != null)
                args.AddRange(["--grip", device.Device.Device + "=" + device.SelectedGrip.Id]);
        }
    }

    // ---- 3 priorities ----

    public ObservableCollection<SetupAircraftItem> Aircraft { get; } = [];

    public ObservableCollection<SetupTierItem> Tiers { get; } = [];

    public IReadOnlyList<int> ModifierChoices { get; } = [0, 1, 2, 3, 4];

    [ObservableProperty]
    private string aircraftFilter = string.Empty;

    [ObservableProperty]
    private SetupAircraftItem? selectedAircraft;

    [ObservableProperty]
    private SetupTierItem? selectedTier;

    [ObservableProperty]
    private SetupAircraftItem? selectedTierAircraft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifierText))]
    private int modifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifierText))]
    private bool preferFewer = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RealismText))]
    private double realism = 50;

    public bool HasTiers => Tiers.Count > 0;

    public string ModifierText => Modifiers == 0
        ? T("No modifiers. JoyPro adds one only when your devices have too few buttons for the most important functions, and tells you.")
        : T("{0}. More only when your devices have too few buttons for the most important functions.", (PreferFewer ? T("Up to {0}, as few as possible", Modifiers) : T("Up to {0}, used where they help", Modifiers)));

    public string RealismText => Realism < 34
        ? T("Same button for the same job: a function is on the same button in every aircraft.")
        : Realism < 67
            ? T("In between: the same button for the same job, but aircraft with a very different real stick get their own buttons.")
            : T("Where the real aircraft has it: each button does what the real aircraft's button at that place does.");

    partial void OnAircraftFilterChanged(string value) => FilterAircraft();

    partial void OnModifiersChanged(int value) => ChoiceChanged();

    partial void OnPreferFewerChanged(bool value) => ChoiceChanged();

    partial void OnRealismChanged(double value) => ChoiceChanged();

    // Phase layers on a latched mode switch (engine setup plan --phase-layers): up = air to air, down = air to ground.
    public IReadOnlyList<string> PhaseLayerChoices => PhaseLayerChoice.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseLayersText))]
    private string phaseLayers = PhaseLayerChoice.Automatic;

    public string PhaseLayersText => PhaseLayers switch
    {
        PhaseLayerChoice.Always => T("Always: a latched switch on your throttle or collective becomes a mode switch. Up = air to air, down = air to ground; your buttons do the functions of that phase. Flying, take-off and landing stay the same in every position."),
        PhaseLayerChoice.Off => T("Off: no mode switch; only modifier buttons you hold."),
        _ => T("Automatic: JoyPro uses a latched switch as a mode switch only when your buttons are too few for the functions of one combat phase (air to air, air to ground).")
    };

    partial void OnPhaseLayersChanged(string value) => ChoiceChanged();

    // "Do you use head tracking or VR?" (docs/setup-wizard.md#head-tracking): asked for the sims that support it; yes = no
    // buttons for looking around there (only the view recenter), no = looking around on a grip hat. "yes", "no" or empty
    // (not asked yet: planned like no).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadTrackingYes))]
    [NotifyPropertyChangedFor(nameof(HeadTrackingNo))]
    private string headTrackingAnswer = string.Empty;

    public bool HeadTrackingYes
    {
        get => HeadTrackingAnswer == HeadTrackingChoice.Yes;
        set
        {
            if (value)
                HeadTrackingAnswer = HeadTrackingChoice.Yes;
        }
    }

    public bool HeadTrackingNo
    {
        get => HeadTrackingAnswer == HeadTrackingChoice.No;
        set
        {
            if (value)
                HeadTrackingAnswer = HeadTrackingChoice.No;
        }
    }

    // The question shows when a chosen game is one of those sims.
    public bool AsksHeadTracking => ChosenGames.Any(HeadTrackingChoice.Concerns);

    partial void OnHeadTrackingAnswerChanged(string value)
    {
        ChoiceChanged();
        // (the quick path makes its plan again with the answer)
        if (QuickMode && !IsWorking && CurrentStep >= 3 && !Applied)
            _ = RemakeQuickPlanAsync();
    }

    private async Task RemakeQuickPlanAsync()
    {
        headTrackingChangedInReview = true;
        await MakePlanAsync();
        if (Plan != null)
        {
            await BuildReviewAsync();
            UpdateQuickSummary();
        }
    }

    private Action<SetupWizardViewModel>? settingsChanged;

    // The choices of the last wizard (kept in the settings); later changes are reported back.
    public void UseSettings(AutoPlacementSettings settings, Action<SetupWizardViewModel>? changed)
    {
        settingsChanged = null;
        Modifiers = ModifierChoices.Contains(settings.WizardModifiers) ? settings.WizardModifiers : 0;
        PreferFewer = settings.WizardPreferFewer;
        Realism = Math.Clamp(settings.WizardRealism, 0, 100);
        PhaseLayers = PhaseLayerChoice.FromArgument(settings.WizardPhaseLayers);
        HeadTrackingAnswer = HeadTrackingChoice.Normalize(settings.WizardHeadTracking);
        settingsChanged = changed;
    }

    private void ChoiceChanged()
    {
        InvalidatePlan();
        settingsChanged?.Invoke(this);
    }

    private async Task LoadAircraftAsync()
    {
        string games = string.Join(",", ChosenGames);
        if (games == aircraftGames && allAircraft.Count > 0)
            return;
        EngineCommandResult? result = await Run(["setup", "aircraft", "--games", games], false, "Reading the aircraft of your games...");
        if (result == null || result.ExitCode != 0)
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
            allAircraft.Clear();
            foreach (JsonElement item in Array(doc.RootElement, "Aircraft"))
            {
                string key = Text(item, "Key"), game = Text(item, "Game");
                // A game's own entry is the game, by the name the Start page shows.
                string name = key == game ? Games.FirstOrDefault(g => string.Equals(g.GameId, game, StringComparison.OrdinalIgnoreCase))?.Name ?? Text(item, "Name") : Text(item, "Name");
                allAircraft.Add(new SetupAircraftItem(key, game, Text(item, "ClassTitle"), name, Text(item, "Module"), Bool(item, "Installed"),
                    item.TryGetProperty("Popular", out JsonElement popular) && popular.ValueKind == JsonValueKind.Number ? popular.GetInt32() : -1, Bool(item, "Seat")));
            }
        }
        catch (JsonException)
        {
            Status = T("The aircraft list could not be read; see Engine Output.");
            return;
        }
        aircraftGames = games;
        // Aircraft of games that are no longer chosen leave the priority list.
        foreach (SetupTierItem tier in Tiers.ToList())
        {
            foreach (SetupAircraftItem plane in tier.Aircraft.Where(a => allAircraft.All(x => x.Key != a.Key)).ToList())
                tier.Aircraft.Remove(plane);
        }
        RemoveEmptyTiers();
        FilterAircraft();
        Status = T("{0} aircraft / games. Put the ones you fly most at the top; aircraft in one row count the same.", allAircraft.Count);
        PutCockpitAircraftFirst();
    }

    // ---- a cockpit layout picked on the Cockpits page ("Use this layout", docs/cockpits.md) ----

    private CockpitLayoutRequest? cockpitLayout;

    public CockpitLayoutRequest? CockpitLayout => cockpitLayout;

    public bool HasCockpitLayout => cockpitLayout != null;

    public string CockpitLayoutText => cockpitLayout == null ? string.Empty
        : T("Cockpit layout: {0} {1} (from the Cockpits page). Its aircraft comes first and the plan places its functions as in this cockpit.", cockpitLayout.AircraftName, cockpitLayout.VariantName);

    // The layout preview window (LayoutPreviewViewModel): the main window opens it with a plan file (null: the profile).
    public Action<string?>? OpenPreview { get; set; }

    private int previewFiles;

    // The plan as it is now (with the review's changes) for the layout preview window; null without a plan.
    public string? WritePreviewPlan()
    {
        if (Plan == null)
            return null;
        Directory.CreateDirectory(workFolder);
        string file = Path.Combine(workFolder, "plan-preview-" + (++previewFiles).ToString(CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(file, SetupPlanJson.Write(Plan, false), new UTF8Encoding(false));
        return file;
    }

    // "Where is what?": every device of the plan (after Apply: of the profile) for one game and aircraft at a time.
    [RelayCommand]
    private void ShowLayoutPreview()
    {
        OpenPreview?.Invoke(Applied ? null : WritePreviewPlan());
    }

    // The Start page's auto setup asked for these games (an open wizard): they are ticked, the others not, and the wizard
    // starts again at its first step.
    public void ChooseGames(IReadOnlyCollection<string> gameIds)
    {
        if (gameIds.Count == 0)
            return;
        foreach (SetupGameChoice game in Games)
            game.IsChosen = gameIds.Contains(game.GameId, StringComparer.OrdinalIgnoreCase);
        InvalidatePlan();
        CurrentStep = 0;
    }

    // The variant picked on the Cockpits page: its aircraft goes to the top of the priority list (once the aircraft of
    // the chosen games are read) and the plan reads that variant's stick and throttle layout (engine cockpits get
    // <aircraft> <variant> --json) for it, instead of the aircraft's first variant.
    public void UseCockpitLayout(CockpitLayoutRequest request)
    {
        cockpitLayout = request;
        OnPropertyChanged(nameof(CockpitLayout));
        OnPropertyChanged(nameof(HasCockpitLayout));
        OnPropertyChanged(nameof(CockpitLayoutText));
        InvalidatePlan();
        PutCockpitAircraftFirst();
        Status = CockpitLayoutText;
    }

    // An aircraft of the wizard's list that the picked cockpit names ("DCS:F-16C_50"; a game-wide entry by the game).
    private bool IsCockpitLayoutAircraft(SetupAircraftItem plane)
    {
        if (cockpitLayout == null)
            return false;
        foreach (string entry in cockpitLayout.Games)
        {
            int colon = entry.IndexOf(':');
            string name = colon < 0 ? entry : entry[(colon + 1)..];
            if (string.Equals(name.Trim(), plane.Key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void PutCockpitAircraftFirst()
    {
        if (cockpitLayout == null || allAircraft.Count == 0)
            return;
        SetupAircraftItem? plane = allAircraft.FirstOrDefault(IsCockpitLayoutAircraft);
        if (plane == null || (Tiers.Count > 0 && Tiers[0].Aircraft.Contains(plane)))
            return;
        foreach (SetupTierItem tier in Tiers)
            tier.Aircraft.Remove(plane);
        RemoveEmptyTiers();
        SetupTierItem first = new();
        first.Aircraft.Add(plane);
        Tiers.Insert(0, first);
        AfterTierChange();
    }

    private void FilterAircraft()
    {
        HashSet<string> listed = Tiers.SelectMany(t => t.Aircraft).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string filter = AircraftFilter.Trim();
        Aircraft.Clear();
        foreach (SetupAircraftItem plane in allAircraft.Where(a => !listed.Contains(a.Key) &&
                     (filter.Length == 0 || a.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                      a.Game.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.ClassTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))))
            Aircraft.Add(plane);
    }

    // Puts aircraft into the priority list (tests, and the GUI's double click): a new row at the end.
    public void AddPriority(string key, bool equalToLast = false)
    {
        SetupAircraftItem? plane = allAircraft.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
        if (plane == null || Tiers.Any(t => t.Aircraft.Contains(plane)))
            return;
        SetupTierItem tier = equalToLast && Tiers.Count > 0 ? Tiers[^1] : NewTier();
        tier.Aircraft.Add(plane);
        AfterTierChange();
    }

    public void SetAircraftForTests(IEnumerable<SetupAircraftItem> aircraft, string games)
    {
        allAircraft.Clear();
        allAircraft.AddRange(aircraft);
        aircraftGames = games;
        FilterAircraft();
    }

    [RelayCommand]
    private void AddAsNewPriority()
    {
        if (SelectedAircraft == null)
            return;
        SetupTierItem tier = NewTier();
        tier.Aircraft.Add(SelectedAircraft);
        SelectedTier = tier;
        AfterTierChange();
    }

    [RelayCommand]
    private void AddToSelectedPriority()
    {
        if (SelectedAircraft == null)
            return;
        SetupTierItem tier = SelectedTier ?? (Tiers.Count > 0 ? Tiers[^1] : NewTier());
        tier.Aircraft.Add(SelectedAircraft);
        AfterTierChange();
    }

    [RelayCommand]
    private void RemovePriority()
    {
        if (SelectedTier == null)
            return;
        if (SelectedTierAircraft != null && SelectedTier.Aircraft.Contains(SelectedTierAircraft))
            SelectedTier.Aircraft.Remove(SelectedTierAircraft);
        else
            SelectedTier.Aircraft.Clear();
        RemoveEmptyTiers();
        AfterTierChange();
    }

    [RelayCommand]
    private void PriorityUp() => MoveTier(-1);

    [RelayCommand]
    private void PriorityDown() => MoveTier(1);

    // The selected row counts the same as the one above it.
    [RelayCommand]
    private void MergeWithAbove()
    {
        if (SelectedTier == null)
            return;
        int index = Tiers.IndexOf(SelectedTier);
        if (index <= 0)
            return;
        SetupTierItem above = Tiers[index - 1];
        foreach (SetupAircraftItem plane in SelectedTier.Aircraft.ToList())
            above.Aircraft.Add(plane);
        Tiers.RemoveAt(index);
        SelectedTier = above;
        AfterTierChange();
    }

    // The selected aircraft gets a row of its own right below its current one.
    [RelayCommand]
    private void SplitFromRow()
    {
        if (SelectedTier == null || SelectedTierAircraft == null || SelectedTier.Aircraft.Count < 2)
            return;
        int index = Tiers.IndexOf(SelectedTier);
        SelectedTier.Aircraft.Remove(SelectedTierAircraft);
        SetupTierItem tier = new();
        tier.Aircraft.Add(SelectedTierAircraft);
        Tiers.Insert(index + 1, tier);
        AfterTierChange();
    }

    // Drag and drop: moves an aircraft into another row (or a new row at the end), equal to the aircraft there.
    public void DropAircraft(SetupAircraftItem plane, SetupTierItem? target)
    {
        foreach (SetupTierItem tier in Tiers)
            tier.Aircraft.Remove(plane);
        if (target == null || !Tiers.Contains(target))
            target = NewTier();
        target.Aircraft.Add(plane);
        RemoveEmptyTiers();
        AfterTierChange();
    }

    // Drag and drop of a whole row onto another: it takes that row's place.
    public void MoveTierTo(SetupTierItem tier, SetupTierItem target)
    {
        int from = Tiers.IndexOf(tier);
        int to = Tiers.IndexOf(target);
        if (from < 0 || to < 0 || from == to)
            return;
        Tiers.Move(from, to);
        AfterTierChange();
    }

    private void MoveTier(int delta)
    {
        if (SelectedTier == null)
            return;
        int index = Tiers.IndexOf(SelectedTier);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Tiers.Count)
            return;
        SetupTierItem tier = SelectedTier;
        Tiers.Move(index, target);
        SelectedTier = tier;
        AfterTierChange();
    }

    private SetupTierItem NewTier()
    {
        SetupTierItem tier = new();
        Tiers.Add(tier);
        return tier;
    }

    private void RemoveEmptyTiers()
    {
        foreach (SetupTierItem tier in Tiers.Where(t => t.Aircraft.Count == 0).ToList())
            Tiers.Remove(tier);
    }

    private void AfterTierChange()
    {
        for (int i = 0; i < Tiers.Count; i++)
        {
            Tiers[i].Rank = i + 1;
            Tiers[i].Refresh();
        }
        OnPropertyChanged(nameof(HasTiers));
        FilterAircraft();
        InvalidatePlan();
    }

    public string PriorityArgument() => string.Join(">", Tiers.Where(t => t.Aircraft.Count > 0).Select(t => string.Join("|", t.Aircraft.Select(a => a.Key))));

    // ---- 4 plan ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(PlanHeadline))]
    private SetupPlan? plan;

    public bool HasPlan => Plan != null;

    public ObservableCollection<string> PlanNotes { get; } = [];

    public ObservableCollection<SetupQualityRow> Quality { get; } = [];

    public ObservableCollection<string> PlanModifiers { get; } = [];

    public ObservableCollection<SetupNotPlacedRow> NotPlaced { get; } = [];

    public ObservableCollection<string> PlanRules { get; } = [];

    public bool HasNoPlanModifiers => PlanModifiers.Count == 0;

    public string PlanHeadline
    {
        get
        {
            if (Plan == null)
                return T("No plan yet.");
            int placed = Plan.Placed.Count(r => !r.Kept);
            // The quick path: plain and calm. What did not fit is shown under "Change something", the important ones first.
            if (QuickMode)
            {
                // "Important, no room" only for an essential nothing else covers: not a wheel brake axis next to the
                // wheel brake button, not a lever the devices do not have (propeller, mixture, a second engine).
                bool essentials = !NotPlaced.Any(r => r.IsImportantGap);
                if (NotPlaced.Count == 0)
                    return T("All {0} game controls are set.", placed);
                return essentials
                    ? T("All important buttons are set. Some rare extras had no room ({0}). That is OK.", NotPlaced.Count)
                    : Easy
                        ? T("{0} game controls are on your stick. A few important ones did not fit. Click Change something to see them.", placed)
                        : T("{0} game controls are on your stick. A few important ones had no room: Change something shows them.", placed);
            }
            int kept = Plan.Relations.Count(r => r.Kept);
            return T("{0} functions placed{1}, {2} not placed, {3}.", placed, (kept > 0 ? T(", {0} of your binds kept", kept) : string.Empty), Plan.NotPlaced.Count, (Plan.Modifiers.Count == 0 ? T("no modifier") : Plural("{0} modifier", "{0} modifiers", Plan.Modifiers.Count)));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(ShowsBack))]
    private bool applied;

    // One main button at a time: after "Use these buttons" the quick path's next button is the only blue one.
    public bool CanApply => !IsWorking && !(QuickMode && Applied);

    // The window closes itself (the quick path after "Write to my game" or "Set up the pretend controller").
    public event Action? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke();

    public string PlanFile => Path.Combine(workFolder, "plan.json");

    private void InvalidatePlan()
    {
        if (Plan == null)
            return;
        Plan = null;
        Applied = false;
        sheets.Clear();
    }

    [RelayCommand]
    private async Task MakePlanAsync()
    {
        Directory.CreateDirectory(workFolder);
        List<string> args = ["setup", "plan", "--games", string.Join(",", ChosenGames), "--modifiers", Modifiers.ToString(CultureInfo.InvariantCulture),
            PreferFewer ? "--prefer-fewer" : "--use-all-modifiers", "--realism", (Realism / 100.0).ToString("0.###", CultureInfo.InvariantCulture),
            "--phase-layers", PhaseLayerChoice.ToArgument(PhaseLayers)];
        if (HeadTrackingAnswer.Length > 0)
            args.AddRange(["--head-tracking", HeadTrackingAnswer]);
        // The quick path: only the aircraft the user said they fly (their seats come with them).
        if (QuickMode && QuickAircraft.Count > 0 && ChosenAircraftKeys().Count > 0)
            args.AddRange(["--only-aircraft", string.Join(",", ChosenAircraftKeys())]);
        string priorities = PriorityArgument();
        if (priorities.Length > 0)
            args.AddRange(["--priorities", priorities]);
        AddDeviceChoices(args);
        foreach (string file in await CockpitEntriesAsync())
            args.AddRange(["--cockpit-file", file]);
        if (File.Exists(PlanFile))
            File.Delete(PlanFile);
        args.AddRange(["--out-plan", PlanFile]);
        EngineCommandResult? result = await Run(args, false, "Making the plan...");
        if (result == null || result.ExitCode != 0)
            return;
        SetupPlan? made = ReadPlanFile(PlanFile) ?? ReadPlanText(result.StandardOutput);
        if (made == null)
        {
            Status = T("The plan could not be read; see Engine Output.");
            return;
        }
        LoadPlan(made);
        Status = PlanHeadline;
    }

    // The cockpit database (engine cockpits get <aircraft> --json): real HOTAS positions of the priority aircraft for
    // the realism slider. Without it (older engine, unknown aircraft) the plan uses the knowledge map.
    private async Task<List<string>> CockpitEntriesAsync()
    {
        List<string> files = [];
        if (Realism < 1)
            return files;
        foreach (SetupAircraftItem plane in Tiers.SelectMany(t => t.Aircraft).Take(12))
        {
            // The variant picked on the Cockpits page for its aircraft, else the aircraft's first variant.
            IReadOnlyList<string> get = IsCockpitLayoutAircraft(plane) ? cockpitLayout!.EngineArguments : ["cockpits", "get", plane.Key, "--json"];
            EngineCommandResult? result = await runEngine(get, false, false, T("Reading the cockpit of {0}...", plane.Key), true);
            if (result == null || result.ExitCode != 0)
            {
                // No cockpit database in this engine: stop asking.
                if (result != null && (result.StandardError + result.StandardOutput).Contains("Unknown command area", StringComparison.OrdinalIgnoreCase))
                    break;
                continue;
            }
            string json = MainWindowViewModel.ExtractJsonPayload(result.StandardOutput);
            if (json.Length == 0 || (json[0] != '{' && json[0] != '['))
                continue;
            string file = Path.Combine(workFolder, "cockpit-" + Safe(plane.Key) + ".json");
            File.WriteAllText(file, json, new UTF8Encoding(false));
            files.Add(file);
        }
        return files;
    }

    public void LoadPlan(SetupPlan made)
    {
        Plan = made;
        Applied = false;
        planVersion++;
        sheets.Clear();
        PlanNotes.Clear();
        foreach (string note in made.Notes.Concat(made.Warnings))
            PlanNotes.Add(WithGameNames(EngineText.Tr(note)));
        Quality.Clear();
        foreach (SetupQuality quality in made.Quality)
            Quality.Add(new SetupQualityRow(quality));
        PlanModifiers.Clear();
        foreach (SetupPlanModifier modifier in made.Modifiers)
            PlanModifiers.Add(modifier.Name + " = " + SetupPlanText.DeviceName(made, modifier.Device) + " " + ReadableNames.Where(modifier.Input, modifier.Control) +
                              (modifier.Forced ? " - " + T("added because the inputs are too few") : string.Empty) + " - " + EngineText.Tr(modifier.Reason));
        PlanRules.Clear();
        foreach (string rule in made.Rules)
            PlanRules.Add(EngineText.Tr(rule));
        OnPropertyChanged(nameof(HasNoPlanModifiers));
        RefreshNotPlaced();
        OnPropertyChanged(nameof(PlanHeadline));
    }

    // "... of IL2Game" -> "... of IL-2 Sturmovik: Great Battles": the plan's notes name the games as the Start page does.
    private string WithGameNames(string text)
    {
        foreach (SetupGameChoice game in Games.Where(g => g.Name.Length > 0 && g.Name != g.GameId))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(game.GameId) + @"(?![\w-])", game.Name.Replace("$", "$$"));
        return text;
    }

    private void RefreshNotPlaced()
    {
        NotPlaced.Clear();
        if (Plan != null)
        {
            foreach (SetupNotPlaced item in Plan.NotPlaced.OrderByDescending(n => n.Importance))
            {
                // Quick path: an alternative the plan covers (ThrottleLeft / ThrottleRight while the throttle is placed)
                // is no gap for the user.
                if (QuickMode && SetupNotPlacedRow.CoveredByPlan(Plan, item))
                    continue;
                NotPlaced.Add(new SetupNotPlacedRow(item, QuickMode));
            }
        }
        OnPropertyChanged(nameof(HasNotPlaced));
        OnPropertyChanged(nameof(PlanHeadline));
    }

    private static SetupPlan? ReadPlanFile(string file)
    {
        try
        {
            return File.Exists(file) ? SetupPlanJson.Read(File.ReadAllText(file, Encoding.UTF8)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SetupPlan? ReadPlanText(string output)
    {
        try
        {
            return SetupPlanJson.Read(MainWindowViewModel.ExtractJsonPayload(output));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- 5 review ----

    public ObservableCollection<SetupRoleOption> ReviewDevices { get; } = [];

    public ObservableCollection<SetupRoleOption> ReviewAircraft { get; } = [];

    // A game with one setup for all of it (ACE COMBAT, IL-2) has nothing to pick: "All aircraft together" and the game
    // itself show the same; the choice shows only with two aircraft or more.
    public bool ShowsAircraftChoice => ReviewAircraft.Count > 2;

    public ObservableCollection<SetupReviewRow> ReviewRows { get; } = [];

    public ObservableCollection<SetupTargetItem> MoveTargets { get; } = [];

    public ObservableCollection<SetupReviewRow> SwapCandidates { get; } = [];

    [ObservableProperty]
    private SetupRoleOption? selectedReviewDevice;

    [ObservableProperty]
    private SetupRoleOption? selectedReviewAircraft;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    private SetupReviewRow? selectedRow;

    [ObservableProperty]
    private SetupNotPlacedRow? selectedNotPlaced;

    [ObservableProperty]
    private SetupTargetItem? selectedMoveTarget;

    [ObservableProperty]
    private SetupReviewRow? selectedSwapRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSheet))]
    private Bitmap? sheetImage;

    [ObservableProperty]
    private string sheetNote = string.Empty;

    public bool HasSheet => SheetImage != null;

    public bool HasSelectedRow => SelectedRow != null;

    public bool HasNotPlaced => NotPlaced.Count > 0;

    private async Task BuildReviewAsync()
    {
        if (Plan == null)
            return;
        string? device = SelectedReviewDevice?.Id;
        string? aircraft = SelectedReviewAircraft?.Id;
        ReviewDevices.Clear();
        // The quick path: short names, a gamepad is a controller held with both hands (not "Main stick (right hand)").
        foreach (SetupDevice d in Plan.Devices.Where(d => SetupRoles.Binds(d.Role)))
            ReviewDevices.Add(new SetupRoleOption(d.Device, QuickMode
                ? QuickDeviceName(d) + " (" + (IsGamepad(d) ? T("Controller") : EngineText.Tr(SetupRoles.Title(d.Role))) + ")"
                : SetupRoles.Short(d) + " (" + EngineText.Tr(SetupRoles.Title(d.Role)) + ")"));
        ReviewAircraft.Clear();
        reviewPickKeys.Clear();
        List<SetupAircraftPick> chosen = QuickMode ? QuickAircraft.Where(a => a.IsChosen).ToList() : [];
        if (chosen.Count > 0)
        {
            // The quick path: only the aircraft the user chose, by the names a person reads (the first one shown first).
            if (chosen.Count > 1)
                ReviewAircraft.Add(new SetupRoleOption(AllAircraft, T("All aircraft together")));
            foreach (SetupAircraftPick pick in chosen)
            {
                ReviewAircraft.Add(new SetupRoleOption(PickPrefix + pick.Name, pick.Name));
                reviewPickKeys[PickPrefix + pick.Name] = new HashSet<string>(pick.Keys, StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            // The quick path speaks no JoyPro words ("relation").
            ReviewAircraft.Add(new SetupRoleOption(AllAircraft, QuickMode ? T("All aircraft together") : T("All aircraft (relation names)")));
            foreach (SetupQuality quality in Plan.Quality.OrderBy(q => q.Tier < 0 ? int.MaxValue : q.Tier).ThenBy(q => q.Aircraft, StringComparer.OrdinalIgnoreCase))
                ReviewAircraft.Add(new SetupRoleOption(quality.Aircraft, AircraftName(quality.Aircraft) + (quality.Tier >= 0 ? T(" (priority {0})", (quality.Tier + 1)) : string.Empty)));
        }
        OnPropertyChanged(nameof(ShowsAircraftChoice));
        SelectedReviewDevice = ReviewDevices.FirstOrDefault(d => d.Id == device) ?? ReviewDevices.FirstOrDefault();
        SelectedReviewAircraft = ReviewAircraft.FirstOrDefault(a => a.Id == aircraft)
                                 ?? (chosen.Count > 0 ? ReviewAircraft.FirstOrDefault(a => a.Id.StartsWith(PickPrefix, StringComparison.Ordinal)) : null)
                                 ?? (ReviewAircraft.Count > 1 && Plan.Priorities.Count > 0 ? ReviewAircraft[1] : ReviewAircraft.FirstOrDefault());
        RefreshReviewRows();
        await ShowSheetAsync();
    }

    partial void OnSelectedReviewDeviceChanged(SetupRoleOption? value)
    {
        RefreshReviewRows();
        _ = ShowSheetAsync();
    }

    partial void OnSelectedReviewAircraftChanged(SetupRoleOption? value)
    {
        RefreshReviewRows();
        _ = ShowSheetAsync();
    }

    partial void OnSelectedRowChanged(SetupReviewRow? value) => RefreshTargets(value?.Relation);

    partial void OnSelectedNotPlacedChanged(SetupNotPlacedRow? value)
    {
        if (value == null || Plan == null)
            return;
        SelectedRow = null;
        RefreshTargets(Plan.Relations.FirstOrDefault(r => r.Name == value.Item.Relation));
    }

    public void RefreshReviewRows()
    {
        ReviewRows.Clear();
        if (Plan == null || SelectedReviewDevice == null)
            return;
        string aircraft = SelectedReviewAircraft?.Id ?? AllAircraft;
        List<SetupPlanRelation> rows;
        if (QuickMode && reviewPickKeys.Count > 0)
        {
            // The quick path: the chosen aircraft (one, or all of them together). A relation of a shared DCS layer (Default,
            // the ground units) shows only when the aircraft has no relation of that function of its own.
            HashSet<string> keys = aircraft.Length > 0 && reviewPickKeys.TryGetValue(aircraft, out HashSet<string>? picked)
                ? picked
                : reviewPickKeys.Values.SelectMany(k => k).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> known = allAircraft.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<SetupPlanRelation> onDevice = Plan.Relations.Where(r => r.Device == SelectedReviewDevice.Id && r.Input.Length > 0).ToList();
            List<SetupPlanRelation> own = onDevice.Where(r => r.Aircraft.Any(keys.Contains)).ToList();
            HashSet<string> ownFunctions = own.Select(r => r.Function).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = own.Concat(onDevice.Where(r => !own.Contains(r) && r.Aircraft.All(a => !known.Contains(a)) && r.Overlap.Any(keys.Contains) && !ownFunctions.Contains(r.Function))).ToList();
            // The flight controls and weapons first, then the rest; the most important first.
            rows = rows.OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => CategoryOrder(r.Category)).ThenByDescending(r => r.Importance).ThenBy(r => InputOrder(r.Input)).ThenBy(r => r.Input, StringComparer.Ordinal)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            rows = Plan.Relations.Where(r => r.Device == SelectedReviewDevice.Id && r.Input.Length > 0 &&
                                             (aircraft.Length == 0 || r.Aircraft.Contains(aircraft, StringComparer.OrdinalIgnoreCase) || r.Overlap.Contains(aircraft, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => QuickMode ? CategoryOrder(r.Category) : 0).ThenBy(r => InputOrder(r.Input)).ThenBy(r => r.Input, StringComparer.Ordinal)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // An aircraft's only throttle lever is "Throttle" (the F-16's lever is the slot "ThrottleRight").
        List<SetupPlanRelation> levers = QuickMode ? Plan.Relations.Where(r => r.Device.Length > 0 && r.IsAxis && FunctionNames.IsThrottleLever(r.Function) && rows.Contains(r)).ToList() : [];
        foreach (SetupPlanRelation relation in rows)
            ReviewRows.Add(new SetupReviewRow(relation, QuickMode, levers.Count == 1 && levers[0] == relation ? FunctionNames.Translate("Throttle") : string.Empty)); // nonloc
        OnPropertyChanged(nameof(HasNotPlaced));
    }

    // The aircraft keys of a quick review choice ("pick:F-16C bl.50").
    private readonly Dictionary<string, HashSet<string>> reviewPickKeys = new(StringComparer.Ordinal);

    private const string PickPrefix = "pick:"; // nonloc

    private static int CategoryOrder(string category) => category switch
    {
        SetupImportance.Flight => 0,
        SetupImportance.Weapons => 1,
        _ => 2
    };

    // The name a person reads for an aircraft key (the full wizard's review): the engine's name, else the key.
    private string AircraftName(string key) =>
        allAircraft.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) is { } plane ? plane.Display : key;

    private void RefreshTargets(SetupPlanRelation? relation)
    {
        MoveTargets.Clear();
        SwapCandidates.Clear();
        if (Plan == null || relation == null || relation.Kept)
            return;
        List<string> layers;
        foreach (SetupInput input in SetupPlanEdit.Targets(Plan, relation, out layers).OrderBy(i => i.Device, StringComparer.Ordinal).ThenBy(i => InputOrder(i.Input)))
        {
            foreach (string layer in layers)
            {
                if (SetupPlanEdit.Conflicts(Plan, relation, input.Device, input.Input, layer, null).Count > 0)
                    continue;
                if (input.Device == relation.Device && string.Equals(input.Input, relation.Input, StringComparison.OrdinalIgnoreCase) && layer == relation.Modifier)
                    continue;
                SetupDevice? device = Plan.Devices.FirstOrDefault(d => d.Device == input.Device);
                MoveTargets.Add(new SetupTargetItem(input.Device, input.Input, layer,
                    (device != null ? SetupRoles.Short(device) : ReadableNames.Device(input.Device)) + " " + ReadableNames.Where(input.Input, input.Control, input.Label) + (layer.Length > 0 ? " + " + layer : string.Empty)));
            }
        }
        foreach (SetupReviewRow row in ReviewRows.Where(r => r.Relation != relation && !r.IsKept && r.Relation.IsAxis == relation.IsAxis))
            SwapCandidates.Add(row);
    }

    [RelayCommand]
    private void MoveSelected()
    {
        SetupPlanRelation? relation = SelectedRow?.Relation ?? (SelectedNotPlaced != null ? Plan?.Relations.FirstOrDefault(r => r.Name == SelectedNotPlaced.Item.Relation) : null);
        if (Plan == null || relation == null || SelectedMoveTarget == null)
        {
            Status = T("Pick a function and the input to move it to.");
            return;
        }
        Edited(SetupPlanEdit.Move(Plan, relation, SelectedMoveTarget.Device, SelectedMoveTarget.Input, SelectedMoveTarget.Modifier), T("{0} moved to {1}.", relation.Name, SelectedMoveTarget.Text));
    }

    [RelayCommand]
    private void SwapSelected()
    {
        if (Plan == null || SelectedRow == null || SelectedSwapRow == null)
        {
            Status = T("Pick two functions to swap.");
            return;
        }
        Edited(SetupPlanEdit.Swap(Plan, SelectedRow.Relation, SelectedSwapRow.Relation), T("{0} and {1} swapped.", SelectedRow.Relation.Name, SelectedSwapRow.Relation.Name));
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Plan == null || SelectedRow == null)
            return;
        Edited(SetupPlanEdit.Remove(Plan, SelectedRow.Relation), T("{0} is left out of the plan.", SelectedRow.Relation.Name));
    }

    [RelayCommand]
    private void RestoreSelected()
    {
        if (Plan == null || SelectedRow == null)
            return;
        Edited(SetupPlanEdit.Restore(Plan, SelectedRow.Relation), T("{0} is back in the plan.", SelectedRow.Relation.Name));
    }

    private void Edited(string error, string done)
    {
        if (error.Length > 0)
        {
            Status = EngineText.Tr(error);
            return;
        }
        Status = done;
        Applied = false;
        sheets.Clear();
        planVersion++;
        RefreshNotPlaced();
        RefreshReviewRows();
        if (QuickMode)
            UpdateQuickSummary();
        _ = ShowSheetAsync();
    }

    public Task RefreshSheetAsync() => ShowSheetAsync();

    public string SheetFile { get; private set; } = string.Empty;

    // The shown sheet full size in the picture viewer (print it, keep it next to the sim).
    [RelayCommand]
    private void OpenSheet()
    {
        if (SheetFile.Length > 0 && File.Exists(SheetFile))
            JoyPro.Gui.Services.ExternalProcess.Start(new System.Diagnostics.ProcessStartInfo(SheetFile) { UseShellExecute = true });
    }

    // The callout sheet of the shown device (engine setup render): JoyPro's drawing with a table per control group.
    private async Task ShowSheetAsync()
    {
        if (Plan == null || SelectedReviewDevice == null)
            return;
        string aircraft = SelectedReviewAircraft?.Id ?? AllAircraft;
        string device = SelectedReviewDevice.Id;
        int version = planVersion;
        // One engine call per plan version and aircraft draws every device; later requests wait for it.
        if (!sheets.TryGetValue(aircraft, out Task<List<SetupSheetItem>>? pending))
        {
            pending = RenderSheetsAsync(Plan, aircraft, version);
            sheets[aircraft] = pending;
        }
        List<SetupSheetItem> list = await pending;
        if (version != planVersion)
            return;
        SetupSheetItem? sheet = list.FirstOrDefault(s => s.Device == device);
        if (SelectedReviewDevice?.Id != device || (SelectedReviewAircraft?.Id ?? AllAircraft) != aircraft)
            return;
        if (sheet == null || !File.Exists(sheet.File))
        {
            SheetImage = null;
            SheetNote = T("No drawing of this device: the table lists what goes where.");
            return;
        }
        // A sheet the engine just wrote can be held by another reader for a moment (a virus scanner, the folder sync):
        // read again a few times instead of showing an empty panel.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                SheetImage = new Bitmap(sheet.File);
                SheetFile = sheet.File;
                SheetNote = string.Empty;
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(150 * (attempt + 1));
                if (version != planVersion || SelectedReviewDevice?.Id != device)
                    return;
            }
            catch (Exception)
            {
                SheetImage = null;
                SheetFile = sheet.File;
                SheetNote = T("No picture this time: the list shows what goes where.");
                return;
            }
        }
    }

    private async Task<List<SetupSheetItem>> RenderSheetsAsync(SetupPlan plan, string aircraft, int version)
    {
        Directory.CreateDirectory(workFolder);
        string tag = version.ToString(CultureInfo.InvariantCulture) + "-" + (aircraft.Length == 0 ? "all" : Safe(aircraft));
        string file = Path.Combine(workFolder, "plan-review-" + tag + ".json");
        File.WriteAllText(file, SetupPlanJson.Write(plan, false), new UTF8Encoding(false));
        List<string> args = ["setup", "render", "--plan", file, "--out-dir", Path.Combine(workFolder, "sheets-" + tag), "--width", "1600"];
        args.AddRange(JoyPro.Gui.Services.DeviceReferenceService.ExportDrawingArguments());
        if (aircraft.Length > 0)
            args.AddRange(["--aircraft", aircraft]);
        EngineCommandResult? result = await runEngine(args, false, false, T("Drawing the device sheets..."), true);
        List<SetupSheetItem> list = [];
        if (result != null && result.ExitCode == 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
                foreach (JsonElement item in Array(doc.RootElement, "Sheets"))
                    list.Add(new SetupSheetItem(Text(item, "Device"), string.Empty, Text(item, "File")));
            }
            catch (JsonException)
            {
            }
        }
        return list;
    }

    // ---- apply ----

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Plan == null)
            return;
        List<string> problems = SetupPlanEdit.Validate(Plan);
        if (problems.Count > 0)
        {
            Status = EngineText.Tr(problems[0]);
            return;
        }
        Directory.CreateDirectory(workFolder);
        string file = Path.Combine(workFolder, "plan-apply.json");
        File.WriteAllText(file, SetupPlanJson.Write(Plan, true), new UTF8Encoding(false));
        EngineCommandResult? result = await Run(["setup", "apply", "--plan", file], true, "Setting up your controls");
        if (result == null || result.ExitCode != 0)
            return;
        Applied = true;
        Status = !QuickMode
            ? T("Done: the relations and binds are in your profile (one step: Undo takes all of it back). Export writes them to your games.")
            : PadGamesOnly
                ? T("Saved in JoyPro. Now set up the pretend controller: press \"Set up the pretend controller\".")
                : T("Saved in JoyPro. {0} does not know it yet: press \"Write to my game\".", ChosenGameNames);
        if (onApplied != null)
            await onApplied();
    }

    [RelayCommand]
    private void Export() => onExport?.Invoke();

    // ---- the quick path ("Just give me binds, quickly") ----

    // The first start's quick path and the Start page's quick button: JoyPro runs the steps by itself with its defaults
    // (the devices it found and their roles, every aircraft equal, no modifier) and stops at the review. The words stay
    // plain: what is set up, what JoyPro does not know, then "Use these binds" and "Write to my game".
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText), nameof(ApplyTip), nameof(ExportText), nameof(ExportTip), nameof(CanApply), nameof(ShowsChangeTools),
        nameof(ShowsSheetPanel), nameof(ReviewListColumn), nameof(ReviewListSpan), nameof(ReviewListRowSpan), nameof(OpenSheetText), nameof(StepHeading),
        nameof(ShowsGamesStep), nameof(ShowsDevicesStep), nameof(ShowsPrioritiesStep), nameof(ShowsPlanStep), nameof(ShowsNext), nameof(ShowsBack), nameof(ShowsQuickHeadTracking))]
    private bool quickMode;

    // The quick path's games only take an Xbox pad (ACE COMBAT ...): they get no export; "Set up the pretend controller" goes on to the
    // Virtual Joystick page instead of "Write to my game".
    public bool PadGamesOnly => ChosenGames.Count > 0 && ChosenGames.All(g => JoyPro.Gui.Services.Help.TourCatalog.PadGames.Contains(g));

    // The quick review keeps the tools to change the plan (move, swap, remove, the reasons, what did not fit) behind
    // "Change something"; the full wizard always shows them.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChangeTools), nameof(ChangeSomethingText), nameof(ReviewListRowSpan))]
    private bool changeSomething;

    public bool ShowsChangeTools => !QuickMode || ChangeSomething;

    public string ChangeSomethingText => ChangeSomething ? T("Hide the change tools") : T("Change something");

    [RelayCommand]
    private void ToggleChangeSomething() => ChangeSomething = !ChangeSomething;

    // The small device sheet: only in the full wizard. The quick review shows the list full width and opens the picture
    // big on request (a sheet squeezed beside the list cannot be read).
    public bool ShowsSheetPanel => !QuickMode;

    public int ReviewListColumn => QuickMode ? 0 : 1;

    public int ReviewListSpan => QuickMode ? 2 : 1;

    // The quick review's list takes the whole height while the change tools are hidden.
    public int ReviewListRowSpan => ShowsChangeTools ? 1 : 3;

    public string OpenSheetText => QuickMode ? T("Show the picture big") : T("Open the sheet");

    [ObservableProperty]
    private string quickSummary = string.Empty;

    public ObservableCollection<string> QuickNotes { get; } = [];

    public bool HasQuickNotes => QuickNotes.Count > 0;

    public string ApplyText => QuickMode ? T("Use these buttons") : T("Apply to my profile");

    public string ApplyTip => !QuickMode
        ? T("Writes the relations and binds into your profile in one step (Undo takes all of it back)")
        : PadGamesOnly
            ? T("Puts these buttons into JoyPro (Undo takes them back). Then \"Set up the pretend controller\" sends them to the game.")
            : T("Puts these buttons into JoyPro (Undo takes them back). Your game changes only when you press \"Write to my game\".");

    public string ExportText => !QuickMode ? T("Export to my games") : PadGamesOnly ? T("Set up the pretend controller") : T("Write to my game");

    public string ExportTip => !QuickMode
        ? T("Opens the Export page, where you write the binds into your games")
        : PadGamesOnly
            ? T("Opens the Virtual Joystick page: the pretend controller that sends your stick to the game")
            : T("Writes these buttons into the game. JoyPro keeps a backup, so you can undo it.");

    public string ChosenGameNames => JoinNames(Games.Where(g => g.IsChosen).Select(g => g.Name).ToList());

    // "A", "A and B", "A, B and C" in the language shown.
    private static string JoinNames(IReadOnlyList<string> names) => JoinAnd(names);

    // The quick path stopped before its review (no game, no stick, no plan): it says why, and offers "Try again" and
    // "Set it up step by step" (the usual steps), never a dead end.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepHeading), nameof(ShowsQuickHeadTracking))]
    private bool quickStuck;

    [RelayCommand]
    private Task QuickTryAgainAsync() => RunQuickAsync();

    [RelayCommand]
    private void QuickStepByStep()
    {
        QuickMode = false;
        QuickStuck = false;
        QuickPadWorks = false;
        QuickAsksAircraft = false;
        QuickLines.Clear();
        Status = string.Empty;
        CurrentStep = 0;
    }

    // ---- the quick path: only a gamepad and only pad games (ACE COMBAT): the controller already works ----

    // The only device is an Xbox controller and every chosen game reads it: nothing to set up. The window says so; the
    // pad remap (other buttons, stick curves through the pretend controller) stays an optional extra.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepHeading), nameof(ShowsQuickHeadTracking))]
    private bool quickPadWorks;

    // "Make it feel better (optional)": the user wants the pad remap anyway.
    private bool padExtrasWanted;

    // "Close and play": the window closes; the main window ends a pad tour that waits for it.
    public Action? PlayAsIs { get; set; }

    [RelayCommand]
    private void QuickPlayAsIs()
    {
        PlayAsIs?.Invoke();
        RequestClose();
    }

    [RelayCommand]
    private Task QuickPadExtrasAsync()
    {
        padExtrasWanted = true;
        return RunQuickAsync();
    }

    // ---- the quick path: "Which aircraft do you fly?" (DCS: one answer per plane, by the names a person reads) ----

    public ObservableCollection<SetupAircraftPick> QuickAircraft { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepHeading), nameof(ShowsQuickHeadTracking))]
    private bool quickAsksAircraft;

    private bool aircraftAnswered;
    private bool hadAircraftQuestion;

    // The keys of the chosen aircraft and their seats, for engine setup plan --only-aircraft.
    private List<string> ChosenAircraftKeys() => QuickAircraft.Where(a => a.IsChosen).SelectMany(a => a.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // One answer per plane of the chosen games that has several (DCS): by the name a person reads, its seats and
    // same-named variants with it; the ones on this PC first, the most popular first. Ticked: the ones on this PC, else
    // the most popular one. An earlier answer stays.
    private void BuildAircraftPicks()
    {
        HashSet<string> before = QuickAircraft.Where(a => a.IsChosen).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool answeredBefore = QuickAircraft.Count > 0;
        QuickAircraft.Clear();
        // (the aircraft the engine names: a game's own aircraft list, today DCS)
        List<SetupAircraftItem> planes = allAircraft.Where(a => a.Game != a.Key && a.Name.Length > 0).ToList();
        foreach (IGrouping<string, SetupAircraftItem> group in planes.Where(a => !a.Seat).GroupBy(a => a.Display, StringComparer.OrdinalIgnoreCase))
        {
            HashSet<string> modules = group.Select(a => a.Module).Where(m => m.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> keys = group.Select(a => a.Key).Concat(planes.Where(a => a.Seat && modules.Contains(a.Module)).Select(a => a.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int popular = group.Where(a => a.Popular >= 0).Select(a => a.Popular).DefaultIfEmpty(int.MaxValue).Min();
            QuickAircraft.Add(new SetupAircraftPick(group.Key, keys, group.Any(a => a.Installed), popular));
        }
        List<SetupAircraftPick> ordered = QuickAircraft.OrderByDescending(a => a.Installed).ThenBy(a => a.Popular).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        QuickAircraft.Clear();
        bool anyInstalled = ordered.Any(a => a.Installed);
        foreach (SetupAircraftPick pick in ordered)
        {
            pick.IsChosen = answeredBefore ? before.Contains(pick.Name) : anyInstalled ? pick.Installed : ReferenceEquals(pick, ordered[0]);
            pick.Changed = InvalidatePlan;
            QuickAircraft.Add(pick);
        }
    }

    private void AskAircraft()
    {
        QuickAsksAircraft = true;
        QuickLines.Clear();
        QuickWarnings.Clear();
        OnPropertyChanged(nameof(HasQuickWarnings));
        CurrentStep = 2;
        Status = string.Empty;
        bool installed = QuickAircraft.Any(a => a.Installed);
        QuickSummary = Easy
            ? T("Tick the planes you fly. Then click Next.")
            : installed
                ? T("Tick the aircraft you fly, and JoyPro sets up their buttons. The ones on this PC are ticked.")
                : T("Tick the aircraft you fly, and JoyPro sets up their buttons. The most popular one is ticked.");
        OnPropertyChanged(nameof(ShowsBack));
    }

    [RelayCommand]
    private async Task QuickAircraftNextAsync()
    {
        if (!QuickAircraft.Any(a => a.IsChosen))
        {
            Status = Easy ? T("Tick one plane first.") : T("Tick at least one aircraft.");
            return;
        }
        aircraftAnswered = true;
        hadAircraftQuestion = true;
        QuickAsksAircraft = false;
        await FinishQuickAsync();
    }

    // ---- the quick review's lines: what the head-tracking answer changed, which real button the shift is ----

    public ObservableCollection<string> QuickLines { get; } = [];

    public bool HasQuickLines => QuickLines.Count > 0;

    // What must not be missed on the quick review (independent review 7 B11): the flares or the weapon release behind a
    // shift button, no button left for the flares. Shown in the warning colour right under the summary.
    public ObservableCollection<string> QuickWarnings { get; } = [];

    public bool HasQuickWarnings => QuickWarnings.Count > 0;

    private bool headTrackingChangedInReview;

    // The head-tracking question on the quick review (not on its question and error screens).
    public bool ShowsQuickHeadTracking => QuickMode && AsksHeadTracking && IsReviewStep && !QuickStuck && !QuickAsksAircraft && !QuickPadWorks;

    // The question with Easy words ("du" in German) or the full one.
    public string HeadTrackingQuestion => Easy ? T("Do you look around by moving your head (VR or a head tracker)?") : T("Do you use head tracking or VR?");

    public string HeadTrackingYesText => Easy ? T("Yes") : T("Yes – I use head tracking, face tracking, Tobii or VR, or I plan to (no view buttons on the HOTAS)");

    public string HeadTrackingNoText => Easy ? T("No, I use a button") : T("No – put looking around on a hat of my stick or throttle");

    // The quick summary again from the plan (after the head-tracking answer and every change): one count everywhere.
    internal void UpdateQuickSummary()
    {
        if (Plan == null)
            return;
        List<SetupDevice> used = Plan.Devices.Where(d => SetupRoles.Binds(d.Role)).ToList();
        int placed = Plan.Placed.Count(r => !r.Kept);
        string devices = JoinNames(used.Select(QuickDeviceName).ToList());
        // Short sentences, the blue button named by its colour: the list is what to look at (the picture opens big).
        QuickSummary = Easy
            ? T("JoyPro put {1} game controls on your {0}. Look at the list. Then click the blue button.", devices, placed)
            : T("JoyPro put {2} game controls for {1} on your {0}. Look at the list. Then press the blue button.", devices, ChosenGameNames, placed);
        QuickLines.Clear();
        if (headTrackingChangedInReview && AsksHeadTracking)
        {
            if (HeadTrackingYes)
            {
                SetupPlanRelation? center = Plan.Placed.FirstOrDefault(r => string.Equals(r.Function, "view_center", StringComparison.OrdinalIgnoreCase)); // nonloc
                QuickLines.Add(center != null
                    ? T("Head tracking: the look-around buttons are gone. Re-centre the view with {0}.", PlaceOf(center))
                    : T("Head tracking: the look-around buttons are gone. Re-centre the view with your head tracker's own key."));
            }
            else
                QuickLines.Add(T("No head tracking: looking around is on a hat of your stick or throttle."));
        }
        foreach (SetupPlanModifier modifier in Plan.Modifiers.Where(m => Plan.Placed.Any(r => r.Modifier == m.Name)))
            QuickLines.Add(T("Shift button: {0}. Hold it, and other buttons do a second job. The list calls it \"{1}\".", ButtonPlace(modifier.Device, modifier.Input, modifier.Control), modifier.Name));
        AddPlanNotes();
        OnPropertyChanged(nameof(HasQuickLines));
        OnPropertyChanged(nameof(HasQuickWarnings));
    }

    // The plan's notes a beginner needs, in plain words (the full wizard shows them all on its Plan step): no button for
    // the flares, no button to re-centre the view, nothing left for looking around; and every survival function (flares,
    // chaff, weapon release, gun) that sits behind a shift button.
    private void AddPlanNotes()
    {
        QuickWarnings.Clear();
        if (Plan == null)
            return;
        foreach (string note in Plan.Notes.Concat(Plan.Warnings).Distinct(StringComparer.Ordinal))
        {
            if (note.StartsWith("Countermeasures:", StringComparison.Ordinal)) // nonloc
                QuickWarnings.Add(T("Important: no button was left for the flares. Click Change something and give them a button: flares keep you alive."));
            else if (note.StartsWith("Head tracking: no input", StringComparison.Ordinal) && !QuickLines.Contains(T("Head tracking: the look-around buttons are gone. Re-centre the view with your head tracker's own key."))) // nonloc
                QuickLines.Add(T("Head tracking: no button was left to re-centre the view. Use the re-centre key of your head tracker."));
            else if (note.StartsWith("Looking around: no free hat", StringComparison.Ordinal)) // nonloc
                QuickLines.Add(T("Looking around: no hat was left for it. Use the mouse, the game's view keys or head tracking."));
            else if (note.StartsWith("Looking around in ", StringComparison.Ordinal)) // nonloc
                QuickLines.Add(WithGameNames(EngineText.Tr(note)));
        }
        foreach (SetupPlanRelation relation in Plan.Placed.Where(r => !r.Kept && r.Modifier.Length > 0 && (IsSurvival(r.Function) || r.Reason.StartsWith("survival-critical", StringComparison.Ordinal))) // nonloc
                     .OrderByDescending(r => r.Importance))
        {
            string text = T("Important: {0} is behind the shift button. Press {1}.", FunctionNames.Plain(relation.Name, relation.Function), PlaceOf(relation));
            if (!QuickWarnings.Contains(text))
                QuickWarnings.Add(text);
        }
    }

    // Flares, chaff, the weapon release and the gun: what keeps a pilot alive in a fight.
    private static bool IsSurvival(string function)
    {
        string id = (function ?? string.Empty).ToLowerInvariant();
        return id.StartsWith("cms_fwd", StringComparison.Ordinal) || id.StartsWith("cms_aft", StringComparison.Ordinal) || id.Contains("flare") || id.Contains("chaff") ||
               id.Contains("countermeasure") || id is "launchweapon" or "trigger2ndstage"; // nonloc
    }

    // "Button 23 on your stick while you hold Button 21 (Pinky switch) on your stick".
    private string PlaceOf(SetupPlanRelation relation)
    {
        string where = ButtonPlace(relation.Device, relation.Input, relation.Control, relation.Label);
        SetupPlanModifier? shift = Plan?.Modifiers.FirstOrDefault(m => m.Name == relation.Modifier);
        return shift == null || relation.Modifier.Length == 0 ? where : T("{0} while you hold {1}", where, ButtonPlace(shift.Device, shift.Input, shift.Control));
    }

    // A button by its printed name or position, and the device it is on.
    private string ButtonPlace(string device, string input, string control, string label = "")
    {
        if (label.Length == 0 && Plan?.Inputs.FirstOrDefault(i => i.Device == device && string.Equals(i.Input, input, StringComparison.OrdinalIgnoreCase)) is { } known)
            label = known.Label;
        string where = ReadableNames.Where(input, control, label);
        SetupDevice? owner = Plan?.Devices.FirstOrDefault(d => d.Device == device);
        if (owner == null || Plan!.Devices.Count(d => SetupRoles.Binds(d.Role)) < 2)
            return where;
        return owner.Role is SetupRoles.Throttle or SetupRoles.Collective ? T("{0} on your throttle", where)
            : owner.Role is SetupRoles.MainStick or SetupRoles.HeliStick or SetupRoles.LeftStick ? T("{0} on your stick", where)
            : T("{0} on {1}", where, QuickDeviceName(owner));
    }

    // A device as the quick path names it: an Xbox controller is an Xbox controller, a long catalog name loses its
    // bracket ("WinCtrl URSA MINOR Combat Throttle (Metal / EX)" -> "WinCtrl URSA MINOR Combat Throttle").
    internal static string QuickDeviceName(SetupDevice device)
    {
        if (IsGamepad(device) && (device.ModelId.Contains("xbox", StringComparison.OrdinalIgnoreCase) || device.Model.Contains("Xbox", StringComparison.OrdinalIgnoreCase)))
            return T("Xbox controller");
        string name = SetupRoles.Short(device);
        if (name.Length > 32)
        {
            string shorter = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^()]*\)\s*$", string.Empty).Trim();
            if (shorter.Length > 0)
                name = shorter;
        }
        return name;
    }

    internal static bool IsGamepad(SetupDevice device) => string.Equals(device.Kind, "gamepad", StringComparison.OrdinalIgnoreCase); // nonloc

    public async Task RunQuickAsync()
    {
        QuickMode = true;
        QuickStuck = false;
        QuickPadWorks = false;
        QuickAsksAircraft = false;
        headTrackingChangedInReview = false;
        QuickNotes.Clear();
        QuickLines.Clear();
        QuickWarnings.Clear();
        OnPropertyChanged(nameof(HasQuickNotes));
        OnPropertyChanged(nameof(HasQuickLines));
        OnPropertyChanged(nameof(HasQuickWarnings));
        OnPropertyChanged(nameof(HeadTrackingQuestion));
        OnPropertyChanged(nameof(HeadTrackingYesText));
        OnPropertyChanged(nameof(HeadTrackingNoText));
        if (ChosenGames.Count == 0)
        {
            QuickSummary = T("JoyPro found no game it can set up. Start your game once (so it makes its control files), then try again.");
            QuickStuck = true;
            return;
        }
        CurrentStep = 1;
        await DetectDevicesAsync();
        List<SetupDeviceItem> used = Devices.Where(d => SetupRoles.Binds(d.Device.Role)).ToList();
        if (used.Count == 0)
        {
            // One plain sentence, one blue button (Try again); no device list, no question, no step number.
            QuickSummary = T("JoyPro can't see a stick yet. Plug it in (USB), wait 5 seconds, then click Try again.");
            QuickStuck = true;
            Status = string.Empty;
            return;
        }
        // Only an Xbox controller and only games that read it: it already works, nothing to set up.
        if (!padExtrasWanted && PadGamesOnly && used.All(d => IsGamepad(d.Device)))
        {
            QuickSummary = Easy
                ? T("Good news! Your controller already works in {0}. Just start the game and play.", ChosenGameNames)
                : T("Good news: your controller already works in {0}. Just start the game and play. JoyPro is for flight sticks and throttles; your controller needs nothing from it.", ChosenGameNames);
            QuickPadWorks = true;
            Status = string.Empty;
            return;
        }
        // What JoyPro does not know is said first: it is set up anyway, like a usual device of its kind.
        foreach (string rule in englishRules.Where(r => r.StartsWith("JoyPro does not know", StringComparison.Ordinal) || r.StartsWith("Button numbers not published", StringComparison.Ordinal)))
            QuickNotes.Add(EngineText.Tr(rule));
        OnPropertyChanged(nameof(HasQuickNotes));
        CurrentStep = 2;
        await LoadAircraftAsync();
        BuildAircraftPicks();
        // A game with many aircraft (DCS): which ones does the user fly?
        if (QuickAircraft.Count > 1 && !aircraftAnswered)
        {
            AskAircraft();
            return;
        }
        await FinishQuickAsync();
    }

    private async Task FinishQuickAsync()
    {
        CurrentStep = 3;
        await MakePlanAsync();
        if (Plan == null)
        {
            QuickSummary = T("JoyPro could not make the setup; see the message below.");
            QuickStuck = true;
            return;
        }
        CurrentStep = 4;
        await BuildReviewAsync();
        UpdateQuickSummary();
        OnPropertyChanged(nameof(ShowsBack));
    }

    // ---- helpers ----

    private async Task<EngineCommandResult?> Run(IReadOnlyList<string> args, bool mutates, string label)
    {
        IsWorking = true;
        Status = label;
        try
        {
            EngineCommandResult? result = await runEngine(args, true, mutates, label, false);
            if (result == null)
                Status = T("The engine could not be started.");
            else if (result.ExitCode != 0)
                Status = FirstLine(result.StandardError) is { Length: > 0 } error ? error : "The engine reported an error; see Engine Output.";
            return result;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static string FirstLine(string text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    private static SetupDevice ReadDevice(JsonElement item)
    {
        SetupDevice device = new()
        {
            Device = Text(item, "Device"),
            Model = Text(item, "Model"),
            ModelId = Text(item, "ModelId"),
            Kind = Text(item, "Kind"),
            Hand = Text(item, "Hand"),
            Connected = Bool(item, "Connected"),
            Generic = Bool(item, "Generic"),
            ForceFeedback = Bool(item, "ForceFeedback"),
            NeedsGrip = Bool(item, "NeedsGrip"),
            VidPid = Text(item, "VidPid"),
            Buttons = Int(item, "Buttons"),
            Hats = Int(item, "Hats"),
            Axes = Int(item, "Axes"),
            ProfileUses = Int(item, "ProfileUses"),
            Role = Text(item, "Role"),
            ProposedRole = Text(item, "ProposedRole"),
            Reason = Text(item, "Reason"),
            Grip = Text(item, "Grip")
        };
        foreach (JsonElement grip in Array(item, "Grips"))
            device.Grips.Add(new SetupGripChoice { Id = Text(grip, "Id"), Name = Text(grip, "Name"), Image = Text(grip, "Image") });
        return device;
    }

    private static IEnumerable<JsonElement> Array(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];

    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : 0;

    private static int InputOrder(string input)
    {
        if (input.StartsWith("JOY_BTN", StringComparison.Ordinal) && int.TryParse(input.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return 100 + n;
        return input.StartsWith("JOY_BTN_POV", StringComparison.Ordinal) ? 1000 : 0;
    }

    private static string Safe(string name)
    {
        StringBuilder sb = new();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
