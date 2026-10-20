using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// What the redesigned pages show besides the engine data: the Start page dashboard (profile counts, connected devices,
// next steps, recent activity), empty states and multi-selection of the Relation Studio and the selected bind's device
// picture.
public sealed partial class MainWindowViewModel
{
    // ---- Dashboard (Start page) ----

    public ObservableCollection<NextStepItem> NextSteps { get; } = [];

    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];

    public ObservableCollection<DeviceCardItem> DashboardDevices { get; } = [];

    public int ProfileRelationCount => allRelations.Count;

    public int ProfileAxisCount => allRelations.Count(r => r.IsAxis);

    public int ProfileBoundCount => allRelations.Count(r => r.IsBound);

    public int ProfileUnboundCount => Math.Max(0, ProfileRelationCount - ProfileBoundCount);

    public double ProfileBoundPercent => ProfileRelationCount == 0 ? 0 : 100.0 * ProfileBoundCount / ProfileRelationCount;

    public string ProfileBoundText => ProfileRelationCount == 0 ? T("nothing to bind yet") : T("{0} of {1} bound", ProfileBoundCount, ProfileRelationCount);

    public int ModifierCount => ModifierItems.Count(m => !m.IsPending);

    public int ConnectedDeviceCount => DeviceCards.Count(c => c.Connected);

    public int InstalledGameCount => GameCards.Count(g => g.Installed);

    public bool HasDashboardDevices => DashboardDevices.Count > 0;

    public bool HasRecentActivity => RecentActivity.Count > 0;

    // ---- Relation Studio ----

    public bool HasAnyRelations => allRelations.Count > 0;

    public bool HasNoRelations => allRelations.Count == 0;

    public bool HasNoRelationMatches => allRelations.Count > 0 && Relations.Count == 0;

    public bool HasRelationSelected => SelectedRelation != null;

    public bool HasNoRelationSelected => SelectedRelation == null;

    public bool HasRelationNodes => RelationNodes.Count > 0;

    // The open relation's actions: only with a relation open (without one the editor shows how to start instead).
    public bool ShowRelationNodeList => SelectedRelation != null && RelationNodes.Count > 0;

    public bool HasRelationSearchResults => RelationNodeSearchResults.Count > 0;

    public int RelationSelectionCount => relationMassSelectedNames.Count;

    public bool HasRelationMultiSelection => relationMassSelectedNames.Count > 1;

    // The whole-profile tools that take the place of the relation editor (Auto Assign lives on the Binds page).
    public bool IsAnyRelationToolOpen => IsAutoGroupPanelOpen || IsDescribeGroupPanelOpen || IsAiPlanPanelOpen;

    public bool IsNoRelationToolOpen => !IsAnyRelationToolOpen;

    [ObservableProperty]
    private bool showNewRelationForm;

    [ObservableProperty]
    private bool showProfileOptions;

    // The database search's category filter (DCS: the DB's categories; Star Citizen: the action maps). A category alone
    // (no search text) lists all commands of it.
    private const string AllRelationNodeCategories = "All categories";

    public ObservableCollection<string> RelationNodeCategoryOptions { get; } = [AllRelationNodeCategories];

    [ObservableProperty]
    private string selectedRelationNodeCategory = AllRelationNodeCategories;

    partial void OnSelectedRelationNodeCategoryChanged(string value)
    {
        if (rebuildingRelationNodeCategories || value == null)
            return;
        if (relationNodeSearchResultSource.Count > 0 || !string.IsNullOrWhiteSpace(RelationNodeSearchText) || value != AllRelationNodeCategories)
            _ = SearchRelationNodesCommand.ExecuteAsync(null);
    }

    private bool rebuildingRelationNodeCategories;

    private string relationNodeCategoryGame = string.Empty;

    private void RebuildRelationNodeCategoryOptions(List<DbNodeCatalogEntry> source)
    {
        string game = source.Count == 0 ? string.Empty : source[0].Game;
        if (game == relationNodeCategoryGame && RelationNodeCategoryOptions.Count > 1)
            return;
        relationNodeCategoryGame = game;
        List<string> categories = source.Select(e => e.Category).Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        string selected = SelectedRelationNodeCategory ?? AllRelationNodeCategories;
        rebuildingRelationNodeCategories = true;
        try
        {
            RelationNodeCategoryOptions.Clear();
            RelationNodeCategoryOptions.Add(AllRelationNodeCategories);
            foreach (string category in categories)
                RelationNodeCategoryOptions.Add(category);
            SelectedRelationNodeCategory = RelationNodeCategoryOptions.Contains(selected) ? selected : AllRelationNodeCategories;
        }
        finally
        {
            rebuildingRelationNodeCategories = false;
        }
    }

    // ---- Bind Studio ----

    // The Bind Studio lists every relation, bound or not (BuildBindRows): it is only empty without relations and binds.
    public bool HasAnyBinds => allRelations.Count > 0 || allBinds.Count > 0;

    public bool HasNoBinds => !HasAnyBinds;

    public bool HasBindSelected => SelectedBind != null;

    public bool HasNoBindSelected => SelectedBind == null;

    // The selected bind's device picture (Settings > Devices, engine hud devices), when JoyPro knows the model.
    public Avalonia.Media.IImage? SelectedBindDevicePicture =>
        SelectedBind == null ? null : DeviceCards.FirstOrDefault(c => SameDevice(c.Device, SelectedBind.Device))?.Picture;

    public bool HasSelectedBindDevicePicture => SelectedBindDevicePicture != null;

    // ---- Modifiers ----

    public bool HasModifiers => ModifierItems.Count > 0;

    public bool HasNoModifiers => ModifierItems.Count == 0;

    public bool HasModifierSelected => SelectedModifierItem != null;

    private bool designInitialized;
    private bool dashboardRefreshQueued;

    // Called once from the constructor.
    private void InitializeDesign()
    {
        if (designInitialized)
            return;
        designInitialized = true;
        Relations.CollectionChanged += (_, _) => QueueDashboardRefresh();
        Binds.CollectionChanged += (_, _) => QueueDashboardRefresh();
        ModifierItems.CollectionChanged += (_, _) => QueueDashboardRefresh();
        DeviceCards.CollectionChanged += (_, _) => QueueDashboardRefresh();
        GameCards.CollectionChanged += (_, _) => QueueDashboardRefresh();
        RelationNodes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRelationNodes));
            OnPropertyChanged(nameof(ShowRelationNodeList));
            OnPropertyChanged(nameof(HasBuilderNoNodes));
            OnPropertyChanged(nameof(BuilderSuggestEmptyText));
            OnBuilderNodesChanged();
        };
        RelationNodeSearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRelationSearchResults));
        PropertyChanged += OnDesignPropertyChanged;
        RebuildDashboard();
    }

    private void OnDesignPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsAutoGroupPanelOpen):
            case nameof(IsDescribeGroupPanelOpen):
            case nameof(IsAiPlanPanelOpen):
            case nameof(IsAutoAssignPanelOpen):
                OnPropertyChanged(nameof(IsAnyRelationToolOpen));
                OnPropertyChanged(nameof(IsNoRelationToolOpen));
                OnPropertyChanged(nameof(IsOrganizeOpen));
                BuilderOnScreen();
                break;
            case nameof(LlmEnabled):
                OnPropertyChanged(nameof(IsBuilderChatAvailable));
                OnBuilderLayoutChanged();
                break;
            case nameof(RelationMassSelectionSummary):
                OnPropertyChanged(nameof(RelationSelectionCount));
                OnPropertyChanged(nameof(HasRelationMultiSelection));
                break;
            case nameof(SelectedRelation):
                OnPropertyChanged(nameof(HasRelationSelected));
                OnPropertyChanged(nameof(HasNoRelationSelected));
                OnPropertyChanged(nameof(ShowRelationNodeList));
                OnPropertyChanged(nameof(HasBuilderNoNodes));
                OnPropertyChanged(nameof(ShowBuilderSuggestEmpty));
                OnPropertyChanged(nameof(BuilderAddButtonLabel));
                OnPropertyChanged(nameof(BuilderSearchHint));
                OnBuilderLayoutChanged();
                OnPropertyChanged(nameof(ShowStartSmall));
                break;
            case nameof(SelectedBind):
                OnPropertyChanged(nameof(HasBindSelected));
                OnPropertyChanged(nameof(HasNoBindSelected));
                OnPropertyChanged(nameof(SelectedBindDevicePicture));
                OnPropertyChanged(nameof(HasSelectedBindDevicePicture));
                break;
            case nameof(SelectedModifierItem):
                OnPropertyChanged(nameof(HasModifierSelected));
                break;
            case nameof(ProfilePath):
            case nameof(WatchGameConfigs):
            case nameof(HasDbUpdates):
                QueueDashboardRefresh();
                break;
        }
    }

    // Many collection changes come in a burst (a list is rebuilt item by item): the dashboard follows once afterwards.
    private void QueueDashboardRefresh()
    {
        // Only the UI thread's view model has a page to update (view model tests run on their own threads).
        if (dashboardRefreshQueued || !Dispatcher.UIThread.CheckAccess())
            return;
        dashboardRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            dashboardRefreshQueued = false;
            RebuildDashboard();
        }, DispatcherPriority.Background);
    }

    private void RebuildDashboard()
    {
        foreach (string name in new[]
                 {
                     nameof(ProfileRelationCount), nameof(ProfileAxisCount), nameof(ProfileBoundCount), nameof(ProfileUnboundCount),
                     nameof(ProfileBoundPercent), nameof(ProfileBoundText), nameof(ModifierCount), nameof(ConnectedDeviceCount),
                     nameof(InstalledGameCount), nameof(HasAnyRelations), nameof(HasNoRelations), nameof(HasNoRelationMatches),
                     nameof(HasAnyBinds), nameof(HasNoBinds), nameof(HasModifiers), nameof(HasNoModifiers),
                     nameof(SelectedBindDevicePicture), nameof(HasSelectedBindDevicePicture)
                 })
        {
            OnPropertyChanged(name);
        }

        List<DeviceCardItem> devices = DeviceCards.Where(c => c.Connected).ToList();
        if (!devices.SequenceEqual(DashboardDevices))
        {
            DashboardDevices.Clear();
            foreach (DeviceCardItem device in devices)
                DashboardDevices.Add(device);
            OnPropertyChanged(nameof(HasDashboardDevices));
        }

        RebuildNextSteps();
    }

    private void RebuildNextSteps()
    {
        List<NextStepItem> steps = [];
        if (!HasProfile)
        {
            steps.Add(new NextStepItem(T("Bring in your binds"), T("Pick a game under Your games and press Import: JoyPro makes a profile for you."),
                "Icon.Import", T("Show my games"), RefreshGameCardsCommand, isPrimary: true));
            if (CanOpenLastProfile)
                steps.Add(new NextStepItem(T("Open your last profile"), LastProfilePath, "Icon.History", T("Open last"), OpenLastProfileCommand));
        }
        else if (allRelations.Count == 0)
        {
            steps.Add(new NextStepItem(T("No relations yet"), T("Start with Auto Group (the same function of every aircraft and game in one relation), or import a game."),
                "Icon.Wand", T("Auto Group"), StartAutoGroupCommand, isPrimary: true));
            steps.Add(new NextStepItem(T("Make one by hand"), T("A relation groups the game commands one button of your devices does."),
                "Icon.Plus", T("Add relation"), StartNewRelationCommand));
        }
        else
        {
            if (ProfileUnboundCount > 0)
            {
                steps.Add(new NextStepItem(ProfileUnboundCount == 1 ? T("1 relation has no button yet") : T("{0} relations have no button yet", ProfileUnboundCount),
                    T("Press the device button you want for each one, or let Auto Assign lay them out on your devices."),
                    "Icon.Gamepad", T("Bind them"), GoBindsCommand, isPrimary: true));
            }
            DeviceCardItem? unused = DeviceCards.FirstOrDefault(c => c.Connected && !c.InProfile && !c.IsVirtual);
            if (unused != null)
            {
                steps.Add(new NextStepItem(T("{0} is not used yet", unused.Title), T("Auto Assign puts your relations on the best controls of this device."),
                    "Icon.Joystick", T("Auto Assign"), StartAutoAssignCommand));
            }
            if (ProfileBoundCount > 0)
            {
                steps.Add(new NextStepItem(T("Write your binds into your games"), T("Export puts them into every game you have; JoyPro backs up the game files first."),
                    "Icon.Export", T("Export"), GoExportCommand, isPrimary: ProfileUnboundCount == 0));
            }
            if (ModifierCount == 0 && ProfileBoundCount > 4)
            {
                steps.Add(new NextStepItem(T("More functions on the same buttons"), T("A modifier (shift button) gives every other button a second job while you hold it."),
                    "Icon.Keycap", T("Modifiers"), GoModifiersCommand));
            }
        }

        NextSteps.Clear();
        foreach (NextStepItem step in steps.Take(4))
            NextSteps.Add(step);
        OnPropertyChanged(nameof(HasNextSteps));
    }

    public bool HasNextSteps => NextSteps.Count > 0;

    [RelayCommand]
    private void ToggleProfileOptions() => ShowProfileOptions = !ShowProfileOptions;

    [RelayCommand]
    private void StartAutoGroup()
    {
        CurrentStage = UiStage.Relations;
        if (!IsAutoGroupPanelOpen)
            ToggleAutoGroupPanel();
    }

    // "Put relations on my buttons automatically" (Auto Assign) is a Binds page tool.
    [RelayCommand]
    private void StartAutoAssign()
    {
        CurrentStage = UiStage.Binds;
        if (!IsAutoAssignPanelOpen)
            _ = ToggleAutoAssignPanelCommand.ExecuteAsync(null);
    }

    // "New relation": its name, then the actions ticked in the search over every game.
    [RelayCommand]
    private void StartNewRelation()
    {
        CurrentStage = UiStage.Relations;
        StartNewBuilderRelation();
    }

    [RelayCommand]
    private void CloseNewRelationForm() => ShowNewRelationForm = false;

    // Leaving the Relations page: its top tools (the auto-build wizard, the AI panels, the new relation form) close, so
    // the page opens in its normal state (the open relation's view) next time.
    private void ResetRelationsPageTools()
    {
        if (IsAnyRelationToolOpen)
            CloseRelationTools();
        if (ShowNewRelationForm)
            ShowNewRelationForm = false;
    }

    [RelayCommand]
    private void CloseRelationTools()
    {
        IsAutoGroupPanelOpen = false;
        IsDescribeGroupPanelOpen = false;
        IsAiPlanPanelOpen = false;
    }

    // The window focuses the name box of the new relation form.
    public event EventHandler? NewRelationFormRequested;

    // The Binds page with the relation selected (from the relation's card: "Bind it").
    [RelayCommand]
    private void BindSelectedRelation()
    {
        string name = SelectedRelation?.Name ?? string.Empty;
        CurrentStage = UiStage.Binds;
        BindListItem? bind = Binds.FirstOrDefault(b => string.Equals(b.RelationName, name, StringComparison.OrdinalIgnoreCase));
        if (bind == null && name.Length > 0)
        {
            BindFilter.ClearAll();
            bind = Binds.FirstOrDefault(b => string.Equals(b.RelationName, name, StringComparison.OrdinalIgnoreCase));
        }
        if (bind != null)
            SelectedBind = bind;
    }

    // Delete the relations ticked in the list (several: engine relations mass delete on the selection), asked first.
    [RelayCommand]
    private async Task DeleteSelectedRelationsAsync()
    {
        List<string> names = relationMassSelectedNames.ToList();
        if (names.Count <= 1)
        {
            await DeleteSelectedRelationAsync();
            return;
        }
        if (!await ConfirmAsync(T("Delete {0} relations?", names.Count),
                T("{0}{1} and their binds leave the profile. Undo (Ctrl+Z) brings them back.", string.Join(", ", names.Take(8)), (names.Count > 8 ? " and " + (names.Count - 8) + " more" : string.Empty)), T("Delete"), isDanger: true))
            return;
        List<string> args = ["relations", "mass", "delete", "--relations", string.Join(",", names)];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Deleting relations..."));
        if (result?.ExitCode == 0)
        {
            RelationNodes.Clear();
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
        }
    }

    // ---- Recent activity: engine sync status (what JoyPro last exported to / imported from each game) ----

    [RelayCommand]
    private async Task RefreshDashboardAsync()
    {
        await LoadDeviceCardsAsync();
        EngineCommandResult? result = await RunEngineAsync(["sync", "status"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Reading recent activity..."), forceText: false, background: true);
        if (result != null && result.ExitCode == 0)
            UpdateRecentActivity(result.StandardOutput);
    }

    private void UpdateRecentActivity(string syncStatusOutput)
    {
        List<ActivityItem> items = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(syncStatusOutput));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Games", out JsonElement games) && games.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement game in games.EnumerateArray())
                {
                    string recorded = ReadJsonString(game, "RecordedUtc");
                    if (recorded.Length == 0)
                        continue;
                    string id = ReadJsonString(game, "Game");
                    string name = ReadJsonString(game, "DisplayName");
                    DateTime? when = DateTime.TryParse(recorded, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                        ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                        : null;
                    string reason = ReadJsonString(game, "Reason").ToLowerInvariant();
                    string what = reason switch
                    {
                        "export" => "Exported",
                        "import" => "Imported",
                        "accept" or "accepted" => "Took over the game's changes",
                        "apply" => "Took over the game's changes",
                        "ignore" => "Kept the profile",
                        "" => "Synced",
                        _ => char.ToUpperInvariant(reason[0]) + reason[1..]
                    };
                    items.Add(new ActivityItem(id, name.Length > 0 ? name : DisplayText.Game(id), what, when, ReadJsonString(game, "State") == "changed"));
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        RecentActivity.Clear();
        foreach (ActivityItem item in items.OrderByDescending(i => i.WhenUtc ?? DateTime.MinValue).Take(6))
            RecentActivity.Add(item);
        OnPropertyChanged(nameof(HasRecentActivity));
    }

    // Undo / Redo walk the profile history (MainWindowViewModel.ProfileHistory.cs, docs/profile-history.md): every save is
    // a version, also across sessions.
}
