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
        RoleOptions = SetupRoles.All.Select(r => new SetupRoleOption(r, SetupRoles.Title(r))).ToList();
        UpdateSteps();
    }

    public ObservableCollection<SetupStepItem> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGamesStep), nameof(IsDevicesStep), nameof(IsPrioritiesStep), nameof(IsPlanStep), nameof(IsReviewStep), nameof(CanGoBack), nameof(NextText), nameof(StepHeading))]
    private int currentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoNext))]
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

    public string StepHeading => CurrentStep switch
    {
        0 => "1. Which games should JoyPro set up?",
        1 => "2. Your devices and what each one is for",
        2 => "3. What matters most to you",
        3 => "4. The plan",
        _ => T("5. Review: what goes where")
    };

    partial void OnCurrentStepChanged(int value) => UpdateSteps();

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

    private void UpdateRules()
    {
        Rules.Clear();
        foreach (string rule in SetupRoles.Rules(Devices.Select(d => d.Device).ToList()))
            Rules.Add(rule);
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
        ? T("Group by function: the same function is on the same button in every aircraft.")
        : Realism < 67
            ? T("Balanced: functions grouped, but aircraft with a very different real stick get their own buttons.")
            : T("Like the real aircraft: each button does what the real aircraft's button at that place does.");

    partial void OnAircraftFilterChanged(string value) => FilterAircraft();

    partial void OnModifiersChanged(int value) => InvalidatePlan();

    partial void OnPreferFewerChanged(bool value) => InvalidatePlan();

    partial void OnRealismChanged(double value) => InvalidatePlan();

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
                allAircraft.Add(new SetupAircraftItem(Text(item, T("Key")), Text(item, T("Game")), Text(item, "ClassTitle")));
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
    }

    private void FilterAircraft()
    {
        HashSet<string> listed = Tiers.SelectMany(t => t.Aircraft).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string filter = AircraftFilter.Trim();
        Aircraft.Clear();
        foreach (SetupAircraftItem plane in allAircraft.Where(a => !listed.Contains(a.Key) &&
                     (filter.Length == 0 || a.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) || a.Game.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                      a.ClassTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))))
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
            int kept = Plan.Relations.Count(r => r.Kept);
            return T("{0} functions placed{1}, {2} not placed, {3}.", placed, (kept > 0 ? T(", {0} of your binds kept", kept) : string.Empty), Plan.NotPlaced.Count, (Plan.Modifiers.Count == 0 ? T("no modifier") : Plural("{0} modifier", "{0} modifiers", Plan.Modifiers.Count)));
        }
    }

    [ObservableProperty]
    private bool applied;

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
            PreferFewer ? "--prefer-fewer" : "--use-all-modifiers", "--realism", (Realism / 100.0).ToString("0.###", CultureInfo.InvariantCulture)];
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
            EngineCommandResult? result = await runEngine(["cockpits", "get", plane.Key, "--json"], false, false, T("Reading the cockpit of {0}...", plane.Key), true);
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
            PlanNotes.Add(note);
        Quality.Clear();
        foreach (SetupQuality quality in made.Quality)
            Quality.Add(new SetupQualityRow(quality));
        PlanModifiers.Clear();
        foreach (SetupPlanModifier modifier in made.Modifiers)
            PlanModifiers.Add(modifier.Name + " = " + SetupPlanText.DeviceName(made, modifier.Device) + " " + modifier.Input +
                              (modifier.Control.Length > 0 ? " (" + modifier.Control + ")" : string.Empty) + (modifier.Forced ? " - added because the inputs are too few" : string.Empty) +
                              " - " + modifier.Reason);
        PlanRules.Clear();
        foreach (string rule in made.Rules)
            PlanRules.Add(rule);
        OnPropertyChanged(nameof(HasNoPlanModifiers));
        RefreshNotPlaced();
        OnPropertyChanged(nameof(PlanHeadline));
    }

    private void RefreshNotPlaced()
    {
        NotPlaced.Clear();
        if (Plan != null)
        {
            foreach (SetupNotPlaced item in Plan.NotPlaced.OrderByDescending(n => n.Importance))
                NotPlaced.Add(new SetupNotPlacedRow(item));
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
        foreach (SetupDevice d in Plan.Devices.Where(d => SetupRoles.Binds(d.Role)))
            ReviewDevices.Add(new SetupRoleOption(d.Device, SetupRoles.Short(d) + " (" + SetupRoles.Title(d.Role) + ")"));
        ReviewAircraft.Clear();
        ReviewAircraft.Add(new SetupRoleOption(AllAircraft, T("All aircraft (relation names)")));
        foreach (SetupQuality quality in Plan.Quality.OrderBy(q => q.Tier < 0 ? int.MaxValue : q.Tier).ThenBy(q => q.Aircraft, StringComparer.OrdinalIgnoreCase))
            ReviewAircraft.Add(new SetupRoleOption(quality.Aircraft, quality.Aircraft + (quality.Tier >= 0 ? T(" (priority {0})", (quality.Tier + 1)) : string.Empty)));
        SelectedReviewDevice = ReviewDevices.FirstOrDefault(d => d.Id == device) ?? ReviewDevices.FirstOrDefault();
        SelectedReviewAircraft = ReviewAircraft.FirstOrDefault(a => a.Id == aircraft) ?? (ReviewAircraft.Count > 1 && Plan.Priorities.Count > 0 ? ReviewAircraft[1] : ReviewAircraft.FirstOrDefault());
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
        foreach (SetupPlanRelation relation in Plan.Relations.Where(r => r.Device == SelectedReviewDevice.Id && r.Input.Length > 0 &&
                                                                         (aircraft.Length == 0 || r.Aircraft.Contains(aircraft, StringComparer.OrdinalIgnoreCase) || r.Overlap.Contains(aircraft, StringComparer.OrdinalIgnoreCase)))
                     .OrderBy(r => r.Modifier, StringComparer.Ordinal).ThenBy(r => InputOrder(r.Input)).ThenBy(r => r.Input, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            ReviewRows.Add(new SetupReviewRow(relation));
        OnPropertyChanged(nameof(HasNotPlaced));
    }

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
                    (device != null ? SetupRoles.Short(device) : input.Device) + " " + input.Input + (layer.Length > 0 ? " + " + layer : string.Empty) +
                    " (" + (input.Label.Length > 0 ? input.Label : input.Control) + ")"));
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
            Status = error;
            return;
        }
        Status = done;
        Applied = false;
        sheets.Clear();
        planVersion++;
        RefreshNotPlaced();
        RefreshReviewRows();
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
        try
        {
            SheetImage = new Bitmap(sheet.File);
            SheetFile = sheet.File;
            SheetNote = string.Empty;
        }
        catch (Exception)
        {
            SheetImage = null;
            SheetNote = T("The sheet could not be shown.");
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
                    list.Add(new SetupSheetItem(Text(item, T("Device")), string.Empty, Text(item, T("File"))));
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
            Status = problems[0];
            return;
        }
        Directory.CreateDirectory(workFolder);
        string file = Path.Combine(workFolder, "plan-apply.json");
        File.WriteAllText(file, SetupPlanJson.Write(Plan, true), new UTF8Encoding(false));
        EngineCommandResult? result = await Run(["setup", "apply", "--plan", file], true, "Setting up your controls");
        if (result == null || result.ExitCode != 0)
            return;
        Applied = true;
        Status = T("Done: the relations and binds are in your profile (one step: Undo takes all of it back). Export writes them to your games.");
        if (onApplied != null)
            await onApplied();
    }

    [RelayCommand]
    private void Export() => onExport?.Invoke();

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
            Device = Text(item, T("Device")),
            Model = Text(item, T("Model")),
            ModelId = Text(item, "ModelId"),
            Kind = Text(item, T("Kind")),
            Hand = Text(item, T("Hand")),
            Connected = Bool(item, "Connected"),
            Generic = Bool(item, "Generic"),
            ForceFeedback = Bool(item, "ForceFeedback"),
            NeedsGrip = Bool(item, "NeedsGrip"),
            VidPid = Text(item, "VidPid"),
            Buttons = Int(item, "Buttons"),
            Hats = Int(item, "Hats"),
            Axes = Int(item, "Axes"),
            ProfileUses = Int(item, "ProfileUses"),
            Role = Text(item, T("Role")),
            ProposedRole = Text(item, "ProposedRole"),
            Reason = Text(item, T("Reason")),
            Grip = Text(item, T("Grip"))
        };
        foreach (JsonElement grip in Array(item, "Grips"))
            device.Grips.Add(new SetupGripChoice { Id = Text(grip, T("Id")), Name = Text(grip, T("Name")), Image = Text(grip, T("Image")) });
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
