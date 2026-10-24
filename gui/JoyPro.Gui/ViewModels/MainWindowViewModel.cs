using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;
using DcsCommandNames = JoyPro.Engine.DcsCommandNames;

namespace JoyPro.Gui.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly Regex RelationListLine = new(
        @"^(?<name>.+?) \| type=(?<type>[^|]+)\s+\| bind=(?<bind>[^|]+)\s+\| mods=(?<mods>[^|]+)\s+\| games=(?<games>[^|]+)\s+\| planes=(?<planes>[^|]+)\s+\| groups=(?<groups>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BindListLine = new(
        @"^(?<name>.+?) \| type=(?<type>[^|]+)\s+\| device=(?<device>[^|]+)\s+\| input=(?<input>[^|]+)\s+\| modifiers\((?<count>\d+)\)=(?<mods>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RelationNodeLine = new(
        @"^\s*\[(?<index>\d+)\]\s+game=(?<game>[^,]+),\s+id=(?<id>[^,]+),\s+aircraft=(?<aircraft>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ModifierListLine = new(
        @"^\s*-\s*(?<name>.+?)\s*\((?<count>\d+)\)\s*:\s*(?<relations>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DcsDbRowLine = new(
        @"<tr>\s*<td>.*?</td>\s*<td>(?<command>.*?)</td>\s*<td>.*?</td>\s*<td>(?<hash>.*?)</td>\s*</tr>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex DcsDbSearchRowLine = new(
        @"<tr>\s*<td>(?<category>.*?)</td>\s*<td>(?<command>.*?)</td>\s*<td>(?<description>.*?)</td>\s*<td>(?<hash>.*?)</td>\s*</tr>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Il2ActionLine = new(
        @"^\s*(?<action>[^,]+),.*?//[a-z]\s*(?<title>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DeviceGuidInBraces = new(
        @"\{(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiWhitespace = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DcsFfbListLine = new(
        @"^\s*-\s*(?<device>.+?)\s+\|\s+invertX=(?<invertX>true|false),\s*invertY=(?<invertY>true|false),\s*swapAxis=(?<swapAxis>true|false),\s*hasSetting=(?<hasSetting>true|false),\s*connected=(?<connected>true|false),\s*profile=(?<profile>true|false)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const char ModifierReformerSeparator = '\u00A7';
    private const char LegacyReformerSeparator = '\u00F5';
    private const char DecodeReplacementSeparator = '\uFFFD';

    private readonly IEngineCliClient cliClient;
    private readonly IJoystickCaptureService joystickCaptureService;
    private CancellationTokenSource? recordInputCts;
    private CancellationTokenSource? layoutMarkerFlashCts;
    private CancellationTokenSource? layoutTableFlashCts;
    private CancellationTokenSource? layoutConnectorFlashCts;
    private RelationNodeItem? editingRelationNode;
    private readonly Dictionary<string, string> dcsDbFilesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> dcsTitleCacheByPlane = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> il2TitleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DbNodeCatalogEntry>> dbNodeCatalogByGame = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> layoutTemplateByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DbNodeSearchResultItem> relationNodeSearchResultSource = [];
    private readonly SemaphoreSlim dbNodeCatalogLock = new(1, 1);
    private readonly SemaphoreSlim engineCallGate = new(1, 1);
    private int pendingEngineCalls;
    private readonly HashSet<string> relationMassSelectedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool dcsDbFilesIndexed;
    private bool il2TitleCacheLoaded;
    private bool dbNodeCatalogLoaded;
    private bool quickBindOptionsInitialized;
    private bool importCatalogInitialized;
    private bool syncingDcsRefreshPlaneOptions;
    private bool restoringBindSelection;
    private bool dcsRefreshPlaneQuickSelectGuard;
    private bool syncingLayoutDeviceOptions;
    private bool layoutDeviceQuickSelectGuard;
    private bool syncingLayoutGameOptions;
    private bool layoutGameQuickSelectGuard;
    private bool syncingLayoutPlaneOptions;
    private bool layoutPlaneQuickSelectGuard;
    private bool syncingDcsFfbDeviceSettings;
    private bool suppressStageAutoRefresh;
    private string layoutEditorContextDevice = string.Empty;
    private Point? layoutEditorConnectorSourcePoint;
    private bool syncingLayoutEditorModifierOptions;
    private readonly Dictionary<SelectableTagItem, PropertyChangedEventHandler> layoutEditorModifierOptionHandlers = new();
    private LayoutEditorConnectorItem? observedLayoutEditorConnector;
    private bool syncingLayoutEditorConnectorDraft;

    // The settings files follow the JOYPRO_DATA_DIR in force when the view model is made (not the first one of the
    // process): a test or a second session with its own data folder gets its own files.
    private readonly string SessionLogPath = Path.Combine(AppInfo.SettingsFolder, "engine-output.log");

    private readonly string UiSettingsPath = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    private static readonly JsonSerializerOptions UiSettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private bool loadingUiSettings;

    // Off for test instances: they must not touch the user's settings, HUD layout or session log in %APPDATA%\JoyPro2.
    private readonly bool persistUiFiles;

    [ObservableProperty]
    private UiStage currentStage = UiStage.Start;

    [ObservableProperty]
    private string enginePath = string.Empty;

    partial void OnEnginePathChanged(string value)
    {
        InvalidateDbCaches();
        PrefillLayoutAutomationPaths();
    }

    private void InvalidateDbCaches()
    {
        dcsDbFilesByKey.Clear();
        dcsTitleCacheByPlane.Clear();
        il2TitleCache.Clear();
        dbNodeCatalogByGame.Clear();
        dcsDbFilesIndexed = false;
        il2TitleCacheLoaded = false;
        dbNodeCatalogLoaded = false;
        importCatalogInitialized = false;
    }

    [ObservableProperty]
    private string profilePath = string.Empty;

    partial void OnProfilePathChanged(string value)
    {
        pendingModifiersByName.Clear();
        RefreshModifierValidationState();
        RefreshRelationValidationState();
        OnFfbProfileChanged();
        OnProfileDisplayChanged();
        OnHistoryProfileChanged();
    }

    [ObservableProperty]
    private string lastProfilePath = string.Empty;

    partial void OnLastProfilePathChanged(string value)
    {
        OnPropertyChanged(nameof(CanOpenLastProfile));
        SaveUiSettings();
    }

    // "Open the last profile at start" (on by default; MainWindowViewModel.ProfileHistory.cs).
    [ObservableProperty]
    private bool loadLastProfileOnStartup = true;

    partial void OnLoadLastProfileOnStartupChanged(bool value) => SaveUiSettings();

    [ObservableProperty]
    private string mergeRightProfilePath = string.Empty;

    [ObservableProperty]
    private string mergeMode = "right";

    [ObservableProperty]
    private string statusText = N("Ready");

    [ObservableProperty]
    private string outputText = "Welcome to JoyPro2.\nPick New/Open/Import to start.";

    [ObservableProperty]
    private string lastCommandText = string.Empty;

    [ObservableProperty]
    private string lastRunDuration = "n/a";

    [ObservableProperty]
    private string lastExitCode = "n/a";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool engineOutputExpanded;

    [ObservableProperty]
    private bool includeStatusUpdates;

    [ObservableProperty]
    private bool includeBenchmark;

    [ObservableProperty]
    private bool autoCreateProfileOnImport = true;

    [ObservableProperty]
    private bool useTextOutput = true;

    [ObservableProperty]
    private bool useLoadCorrections = true;

    [ObservableProperty]
    private bool autoAddDbItems = true;

    [ObservableProperty]
    private bool remapMissingDevices = true;

    [ObservableProperty]
    private bool confirmDeviceRemap;

    [ObservableProperty]
    private string correctionMode = "balanced";

    [ObservableProperty]
    private string correctionAutoThreshold = string.Empty;

    [ObservableProperty]
    private bool confirmCorrections;

    [ObservableProperty]
    private string correctionReportPath = string.Empty;

    [ObservableProperty]
    private string themeMode = "System";

    partial void OnThemeModeChanged(string value) => SaveUiSettings();

    [ObservableProperty]
    private bool showMassOperations;

    [ObservableProperty]
    private bool showAiGrouping;

    [ObservableProperty]
    private bool showQuickBindActions;

    [ObservableProperty]
    private bool showModifierLibrary;

    [ObservableProperty]
    private bool openVisualAssignMode;

    [ObservableProperty]
    private string relationSearchText = string.Empty;

    [ObservableProperty]
    private string bindSearchText = string.Empty;

    [ObservableProperty]
    private string bindDeviceFilterText = string.Empty;

    [ObservableProperty]
    private string importDcsPath = string.Empty;

    partial void OnImportDcsPathChanged(string value)
    {
        importCatalogInitialized = false;
        RefreshDcsKnownDevices();
        RebuildImportDeviceOptions();
        List<string> planes = DiscoverDcsConfigPlanes(value);
        if (planes.Count > 0)
            RebuildImportPlaneOptions(planes);
        else if (ImportPlaneOptions.Count == 0 && DbPlanes.Count > 0)
            RebuildImportPlaneOptions(DbPlanes);
        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
        if (NormalizeRelationGameToken(RelationNodeGameInput).Equals("DCS", StringComparison.OrdinalIgnoreCase))
            _ = RefreshRelationNodePlaneOptionsAsync();
    }

    [ObservableProperty]
    private string importIl2Path = string.Empty;

    [ObservableProperty]
    private string importStarCitizenPath = string.Empty;

    [ObservableProperty]
    private string starCitizenSourcePath = string.Empty;

    [ObservableProperty]
    private string importPlanesCsv = string.Empty;

    [ObservableProperty]
    private string importDevicesCsv = string.Empty;

    [ObservableProperty]
    private string importIgnoreDevicesCsv = string.Empty;

    [ObservableProperty]
    private bool importAllDcsPlanes = true;

    [ObservableProperty]
    private bool importUseSelectedDevices;

    [ObservableProperty]
    private bool importKeyboardCommands = true;

    [ObservableProperty]
    private bool importStarCitizenIncludeKeyboard;

    [ObservableProperty]
    private bool importIgnoreEasyPlanes;

    [ObservableProperty]
    private bool importIncludeDefaults;

    [ObservableProperty]
    private bool importMerge;

    [ObservableProperty]
    private bool importInverted;

    [ObservableProperty]
    private bool importSlider;

    [ObservableProperty]
    private bool importCurvature;

    [ObservableProperty]
    private bool importDeadzone;

    [ObservableProperty]
    private bool importSaturationX;

    [ObservableProperty]
    private bool importSaturationY;

    [ObservableProperty]
    private string relationNameInput = string.Empty;

    [ObservableProperty]
    private string relationCreateValidationMessage = string.Empty;

    [ObservableProperty]
    private bool newRelationIsAxis;

    [ObservableProperty]
    private string relationFromInput = string.Empty;

    [ObservableProperty]
    private string relationGroupInput = string.Empty;

    [ObservableProperty]
    private string relationDeviceInput = string.Empty;

    [ObservableProperty]
    private string relationInputInput = string.Empty;

    [ObservableProperty]
    private string relationModifierInput = string.Empty;

    [ObservableProperty]
    private string relationNodeGameInput = "DCS";

    [ObservableProperty]
    private string relationNodeSearchText = string.Empty;

    [ObservableProperty]
    private string relationNodeSearchStatus = string.Empty;

    [ObservableProperty]
    private DbNodeSearchResultItem? selectedRelationNodeSearchResult;

    [ObservableProperty]
    private string relationNodeIdInput = string.Empty;

    [ObservableProperty]
    private string relationNodePlanesInput = string.Empty;

    [ObservableProperty]
    private string relationGroupAddCsv = string.Empty;

    [ObservableProperty]
    private string relationGroupRenameOld = string.Empty;

    [ObservableProperty]
    private string relationGroupRenameNew = string.Empty;

    [ObservableProperty]
    private string relationImportPath = string.Empty;

    [ObservableProperty]
    private string relationExportPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRelationMassSplit))]
    private string relationMassOperation = "delete";

    [ObservableProperty]
    private string relationMassValue = string.Empty;

    [ObservableProperty]
    private string relationMassQuery = string.Empty;

    [ObservableProperty]
    private string relationMassPlanesCsv = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRelationMassQueryTarget))]
    private string relationMassTarget = "selected";

    public bool IsRelationMassSplit => string.Equals(RelationMassOperation?.Trim(), "split", StringComparison.OrdinalIgnoreCase);

    public bool IsRelationMassQueryTarget => string.Equals(RelationMassTarget?.Trim(), "query", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private string relationMassSelectionSummary = "0 relations selected";

    [ObservableProperty]
    private string bindDeviceInput = string.Empty;

    [ObservableProperty]
    private string bindInputValue = string.Empty;

    [ObservableProperty]
    private string modifierCreateName = string.Empty;

    [ObservableProperty]
    private string modifierCreateValidationMessage = string.Empty;

    [ObservableProperty]
    private string modifierCreateDevice = string.Empty;

    [ObservableProperty]
    private string modifierCreateInput = string.Empty;

    [ObservableProperty]
    private bool modifierCreateInverted;

    [ObservableProperty]
    private string modifierCreateTargetRelation = string.Empty;

    [ObservableProperty]
    private string bindModifierCsv = string.Empty;

    [ObservableProperty]
    private bool bindClearModifiers;

    [ObservableProperty]
    private bool bindClearInput;

    [ObservableProperty]
    private bool bindInverted;

    [ObservableProperty]
    private bool bindSlider;

    [ObservableProperty]
    private string bindCurvatureCsv = string.Empty;

    [ObservableProperty]
    private string bindDeadzoneValue = string.Empty;

    [ObservableProperty]
    private string bindSaturationXValue = string.Empty;

    [ObservableProperty]
    private string bindSaturationYValue = string.Empty;

    [ObservableProperty]
    private bool bindApplyCurvature;

    [ObservableProperty]
    private bool bindApplyDeadzone;

    [ObservableProperty]
    private bool bindApplySaturationX;

    [ObservableProperty]
    private bool bindApplySaturationY;

    [ObservableProperty]
    private bool bindDoubleTap;

    [ObservableProperty]
    private bool selectedBindIsAxis;

    [ObservableProperty]
    private bool recordAxisMode;

    // "axis" or "button" (English; RecordModeText is the word in the user's language).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordModeText))]
    private string recordModeLabel = "button";

    public string RecordModeText => RecordModeLabel == "axis" ? T("axis") : T("button");

    [ObservableProperty]
    private string recordTargetSummary = "Current bind: <unbound>";

    [ObservableProperty]
    private string selectedBindRelationDisplay = "<none>";

    [ObservableProperty]
    private string selectedBindDeviceDisplay = "<unbound>";

    [ObservableProperty]
    private string selectedBindInputDisplay = "<unbound>";

    [ObservableProperty]
    private string recordActionLabel = "Start Record for <unbound>";

    [ObservableProperty]
    private string recordTimeoutMs = "6000";

    [ObservableProperty]
    private string recordWarmupMs = "300";

    [ObservableProperty]
    private string recordAxisThresholdPercent = "30";

    [ObservableProperty]
    private string recordStatusText = N("Idle");

    [ObservableProperty]
    private bool isRecordingInput;

    [ObservableProperty]
    private bool clearInputOnly;

    [ObservableProperty]
    private string unbindTarget = string.Empty;

    [ObservableProperty]
    private string unbindDevice = string.Empty;

    [ObservableProperty]
    private string unbindInput = string.Empty;

    [ObservableProperty]
    private string unbindGame = string.Empty;

    [ObservableProperty]
    private bool unbindAll;

    [ObservableProperty]
    private string modifierNameInput = string.Empty;

    [ObservableProperty]
    private string modifierRemoveCsv = string.Empty;

    [ObservableProperty]
    private string modifierRenameOld = string.Empty;

    [ObservableProperty]
    private string modifierRenameNew = string.Empty;

    [ObservableProperty]
    private string bindsImportPath = string.Empty;

    [ObservableProperty]
    private string bindsExportPath = string.Empty;

    [ObservableProperty]
    private string layoutExportPath = string.Empty;

    [ObservableProperty]
    private string layoutDevicesCsv = string.Empty;

    [ObservableProperty]
    private string layoutMapInput = string.Empty;

    [ObservableProperty]
    private string layoutGameCsv = string.Empty;

    [ObservableProperty]
    private bool layoutSelectAllDevices;

    [ObservableProperty]
    private bool layoutSelectNoneDevices;

    [ObservableProperty]
    private bool layoutSelectAllGames;

    [ObservableProperty]
    private bool layoutSelectNoneGames;

    [ObservableProperty]
    private string layoutPlaneCsv = string.Empty;

    [ObservableProperty]
    private bool layoutSelectAllPlanes;

    [ObservableProperty]
    private bool layoutSelectNonePlanes;

    [ObservableProperty]
    private bool layoutIncludeOverview = true;

    [ObservableProperty]
    private bool layoutIncludePlanes = true;

    [ObservableProperty]
    private bool layoutSkipMissing;

    [ObservableProperty]
    private string layoutFontFamily = "Segoe UI";

    [ObservableProperty]
    private string layoutFontSize = "16";

    [ObservableProperty]
    private string layoutTextColor = "#FFFFFF";

    [ObservableProperty]
    private string layoutOutlineColor = "#000000";

    [ObservableProperty]
    private string layoutBackgroundColor = "#88000000";

    [ObservableProperty]
    private bool layoutSuggestIncludeConnected = true;

    [ObservableProperty]
    private bool layoutSuggestIncludeProfileDevices;

    [ObservableProperty]
    private bool layoutSuggestApplyToProfile;

    [ObservableProperty]
    private string layoutCatalogPath = string.Empty;

    [ObservableProperty]
    private string layoutLegacyLayoutDirs = string.Empty;

    [ObservableProperty]
    private string layoutLegacyOutputDirs = string.Empty;

    [ObservableProperty]
    private string layoutSuggestStatus = string.Empty;

    [ObservableProperty]
    private bool layoutDrawMarkers = true;

    [ObservableProperty]
    private bool layoutDrawLegendTable = true;

    [ObservableProperty]
    private string layoutMarkerSize = "10";

    // The legend table of the exported pictures (engine defaults: 460 px wide, 20 rows, 11 pt).
    [ObservableProperty]
    private string layoutLegendWidth = "460";

    [ObservableProperty]
    private string layoutLegendMaxRows = "20";

    [ObservableProperty]
    private string layoutLegendFontSize = "11";

    [ObservableProperty]
    private string layoutModifierColors = "LAlt=#38BDF8,LCtrl=#F59E0B,LShift=#34D399,RAlt=#A78BFA,RShift=#FB7185";

    [ObservableProperty]
    private string layoutButtonSymbol = "circle";

    [ObservableProperty]
    private string layoutAxisSymbol = "diamond";

    [ObservableProperty]
    private string layoutPovSymbol = "triangle";

    [ObservableProperty]
    private string layoutSliderSymbol = "bar";

    [ObservableProperty]
    private string layoutEditorSourcePath = string.Empty;

    [ObservableProperty]
    private string layoutEditorOutputPath = string.Empty;

    [ObservableProperty]
    private string layoutEditorStatus = "Select a suggestion to load it into the embedded editor.";

    [ObservableProperty]
    private string layoutEditorNewToken = string.Empty;

    [ObservableProperty]
    private string layoutEditorNewType = "button";

    [ObservableProperty]
    private string layoutEditorNewModifier = string.Empty;

    [ObservableProperty]
    private string layoutEditorModifierPreview = "<none>";

    [ObservableProperty]
    private double layoutEditorCanvasWidth = 1280;

    [ObservableProperty]
    private double layoutEditorCanvasHeight = 720;

    [ObservableProperty]
    private double layoutEditorZoom = 1.0;

    [ObservableProperty]
    private Bitmap? layoutEditorPreviewImage;

    [ObservableProperty]
    private LayoutEditorMarkerItem? selectedLayoutEditorMarker;

    [ObservableProperty]
    private LayoutEditorTableItem? selectedLayoutEditorTable;

    [ObservableProperty]
    private string layoutEditorNewTableName = "Primary Controls";

    [ObservableProperty]
    private string layoutEditorNewTableMode = "panel";

    [ObservableProperty]
    private string layoutEditorNewTableWidth = "360";

    [ObservableProperty]
    private string layoutEditorNewTableHeight = "220";

    [ObservableProperty]
    private string layoutEditorNewTableColumns = "1";

    [ObservableProperty]
    private string layoutEditorNewTableBackgroundColor = "#F6F8FB";

    [ObservableProperty]
    private string layoutEditorNewTableBackgroundOpacity = "0.93";

    [ObservableProperty]
    private bool layoutEditorNewTableDropShadow = true;

    [ObservableProperty]
    private bool layoutPlaceMarkerMode;

    [ObservableProperty]
    private bool layoutPlaceTableMode;

    [ObservableProperty]
    private bool layoutPlaceConnectorPointMode;

    [ObservableProperty]
    private bool layoutPlaceConnectorSourceMode;

    [ObservableProperty]
    private LayoutEditorConnectorItem? selectedLayoutEditorConnector;

    [ObservableProperty]
    private string layoutEditorNewConnectorName = "Connector";

    [ObservableProperty]
    private string layoutEditorNewConnectorColor = "#E2E8F0";

    [ObservableProperty]
    private string layoutEditorNewConnectorWidth = "2";

    [ObservableProperty]
    private string layoutEditorNewConnectorPoints = string.Empty;

    [ObservableProperty]
    private string layoutEditorNewConnectorRoute = "orthogonal";

    [ObservableProperty]
    private string layoutEditorNewConnectorSteps = "2";

    [ObservableProperty]
    private string layoutEditorNewConnectorTargetInput = string.Empty;

    [ObservableProperty]
    private string layoutEditorConnectorSourcePointLabel = "<none>";

    [ObservableProperty]
    private string exportMode = "merge";

    [ObservableProperty]
    private string exportDcsPath = string.Empty;

    [ObservableProperty]
    private string exportIl2Path = string.Empty;

    [ObservableProperty]
    private string exportStarCitizenPath = string.Empty;

    [ObservableProperty]
    private bool exportKeepKeyboardDefaults = true;

    [ObservableProperty]
    private bool exportNukeUntouchedDevices;

    // DCS: the written devices also lose their DCS defaults in every other aircraft (clean mode: their binds too).
    [ObservableProperty]
    private bool exportDcsCleanAllAircraft = true;

    [ObservableProperty]
    private bool exportStarCitizenKeepKeyboardDefaults = true;

    [ObservableProperty]
    private bool exportStarCitizenPreferLocalOrder = true;

    [ObservableProperty]
    private bool validationShowErrors = true;

    [ObservableProperty]
    private bool validationShowWarnings;

    [ObservableProperty]
    private string validationSummary = N("Not run yet.");

    [ObservableProperty]
    private string conflictSummary = "No conflict scan executed yet.";

    [ObservableProperty]
    private string dcsSavedGamesPath = string.Empty;

    [ObservableProperty]
    private string dcsInstallPath = string.Empty;

    [ObservableProperty]
    private string dcsRefreshPlanesCsv = string.Empty;

    [ObservableProperty]
    private bool dcsRefreshSelectAllPlanes;

    [ObservableProperty]
    private bool dcsRefreshSelectNonePlanes;

    [ObservableProperty]
    private bool dcsRefreshUseOcr;

    [ObservableProperty]
    private bool dcsRefreshLaunch = true;

    [ObservableProperty]
    private bool dcsRefreshKeepRunning;

    [ObservableProperty]
    private bool dcsRefreshHeadlessLua = true;

    [ObservableProperty]
    private bool dcsRefreshDryRun;

    [ObservableProperty]
    private string dcsClicksXmlPath = string.Empty;

    [ObservableProperty]
    private string dcsClicksDirPath = string.Empty;

    [ObservableProperty]
    private string dcsPreviewImagePath = string.Empty;

    [ObservableProperty]
    private string dcsExePath = string.Empty;

    [ObservableProperty]
    private string dcsFfbDevice = string.Empty;

    [ObservableProperty]
    private bool dcsFfbInvertX;

    [ObservableProperty]
    private bool dcsFfbInvertY;

    [ObservableProperty]
    private bool dcsFfbSwapAxis;

    [ObservableProperty]
    private string dcsFfbFilterText = string.Empty;

    [ObservableProperty]
    private bool dcsFfbShowConnectedOnly;

    [ObservableProperty]
    private bool dcsFfbShowProfileOnly;

    [ObservableProperty]
    private bool dcsFfbAutoApplyOnToggle;

    [ObservableProperty]
    private string deviceRemoveName = string.Empty;

    [ObservableProperty]
    private string exchangeOldDevice = string.Empty;

    [ObservableProperty]
    private string exchangeNewDevice = string.Empty;

    [ObservableProperty]
    private string dbGameFilter = string.Empty;

    [ObservableProperty]
    private string dbPlaneFilter = string.Empty;

    [ObservableProperty]
    private string selectedDbGame = string.Empty;

    [ObservableProperty]
    private string customCommandText = string.Empty;

    [ObservableProperty]
    private bool customCommandIncludeProfile = true;

    [ObservableProperty]
    private string customCommandExtraGlobal = string.Empty;

    [ObservableProperty]
    private string globalEditorCommand = string.Empty;

    [ObservableProperty]
    private string globalOutputPath = string.Empty;

    [ObservableProperty]
    private RelationListItem? selectedRelation;

    [ObservableProperty]
    private BindListItem? selectedBind;

    [ObservableProperty]
    private ModifierListItem? selectedModifierItem;

    [ObservableProperty]
    private RelationNodeItem? selectedRelationNode;

    [ObservableProperty]
    private ValidationIssueItem? selectedValidationIssue;

    [ObservableProperty]
    private LayoutSuggestionItem? selectedLayoutSuggestion;

    public ObservableCollection<RelationListItem> Relations { get; } = [];
    public RangeObservableCollection<RelationNodeItem> RelationNodes { get; } = [];
    public ObservableCollection<string> RelationNodeGameOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> RelationNodePlaneOptions { get; } = [];
    public ObservableCollection<DbNodeSearchResultItem> RelationNodeSearchResults { get; } = [];
    public ObservableCollection<BindListItem> Binds { get; } = [];
    public ObservableCollection<string> QuickBindDeviceOptions { get; } = [];
    public ObservableCollection<string> QuickBindInputOptions { get; } = [];
    public ObservableCollection<ModifierListItem> ModifierItems { get; } = [];
    public ObservableCollection<string> Modifiers { get; } = [];
    public ObservableCollection<string> ModifierNames { get; } = [];
    public ObservableCollection<string> ModifierTargetRelationOptions { get; } = [];
    public ObservableCollection<string> SelectedModifierRelations { get; } = [];
    public ObservableCollection<SelectableTagItem> RelationGroupFilters { get; } = [];
    public ObservableCollection<SelectableTagItem> RelationGroupRemoveCandidates { get; } = [];
    public ObservableCollection<string> RelationGroupNames { get; } = [];
    public ObservableCollection<SelectableTagItem> BindGroupFilters { get; } = [];
    public ObservableCollection<string> ConnectedDevices { get; } = [];
    public ObservableCollection<string> ProfileDevices { get; } = [];
    public ObservableCollection<string> DcsKnownDevices { get; } = [];
    public ObservableCollection<SelectableTagItem> ImportPlaneOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> DcsRefreshPlaneOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> LayoutPlaneOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> LayoutDeviceOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> LayoutGameOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> ImportDeviceOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> ImportIgnoreDeviceOptions { get; } = [];
    public ObservableCollection<DcsFfbDeviceSettingItem> DcsFfbDeviceSettings { get; } = [];
    public ObservableCollection<DcsFfbDeviceSettingItem> VisibleDcsFfbDeviceSettings { get; } = [];
    public ObservableCollection<string> DeviceInputsCatalog { get; } = [];
    public ObservableCollection<string> DbGames { get; } = [];
    public ObservableCollection<string> DbPlanes { get; } = [];
    public ObservableCollection<LayoutSuggestionItem> LayoutSuggestions { get; } = [];
    public ObservableCollection<LayoutEditorMarkerItem> LayoutEditorMarkers { get; } = [];
    public ObservableCollection<LayoutEditorTableItem> LayoutEditorTables { get; } = [];
    public ObservableCollection<LayoutEditorConnectorItem> LayoutEditorConnectors { get; } = [];
    private static readonly Avalonia.Collections.AvaloniaList<LayoutEditorConnectorPointHandleItem> EmptyConnectorPointHandles = [];

    public Avalonia.Collections.AvaloniaList<LayoutEditorConnectorPointHandleItem> SelectedLayoutEditorConnectorPointHandles =>
        SelectedLayoutEditorConnector?.PointHandles ?? EmptyConnectorPointHandles;
    public ObservableCollection<string> LayoutEditorInputOptions { get; } = [];
    public ObservableCollection<string> LayoutEditorMarkerTokenOptions { get; } = [];
    public ObservableCollection<SelectableTagItem> LayoutEditorModifierOptions { get; } = [];
    public ObservableCollection<ValidationIssueItem> ValidationIssues { get; } = [];

    private readonly List<RelationListItem> allRelations = [];
    private readonly List<BindListItem> allBinds = [];
    private readonly Dictionary<string, List<ModifierReformerMatch>> modifierReformerCacheByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingModifierDefinition> pendingModifiersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ValidationIssueItem> allValidationIssues = [];

    public bool CanRun => !IsBusy;
    public bool IsStartStage => CurrentStage == UiStage.Start;
    public bool IsRelationsStage => CurrentStage == UiStage.Relations;
    public bool IsBindsStage => CurrentStage == UiStage.Binds;
    public bool IsModifiersStage => CurrentStage == UiStage.Modifiers;
    public bool IsLayoutStage => CurrentStage == UiStage.Layout;
    public bool IsExportStage => CurrentStage == UiStage.Export;
    public bool IsSettingsStage => CurrentStage == UiStage.Settings;
    public bool IsAdvancedStage => CurrentStage == UiStage.Advanced;
    public bool IsHudStage => CurrentStage == UiStage.Hud;
    public bool HasModifierTargetRelations => ModifierTargetRelationOptions.Count > 0;
    public bool CanOpenLastProfile => !string.IsNullOrWhiteSpace(LastProfilePath) && File.Exists(LastProfilePath);
    public bool CanCreateRelation => EvaluateCanCreateRelation();
    public bool CanCreateModifier => EvaluateCanCreateModifier();
    public bool CanRenameModifier => EvaluateCanRenameModifier();
    public bool ShowDcsPlaneSelection => !ImportAllDcsPlanes;
    public bool ShowImportDeviceSelection => ImportUseSelectedDevices;
    public bool HasRelationCreateValidationMessage => !string.IsNullOrWhiteSpace(RelationCreateValidationMessage);
    public bool HasRelationNodeSearchStatus => !string.IsNullOrWhiteSpace(RelationNodeSearchStatus);
    public bool HasModifierCreateValidationMessage => !string.IsNullOrWhiteSpace(ModifierCreateValidationMessage);
    public bool HasLayoutEditorPreview => LayoutEditorPreviewImage != null;
    public bool CanSaveLayoutEditorTemplate => !string.IsNullOrWhiteSpace(LayoutEditorSourcePath) && !string.IsNullOrWhiteSpace(LayoutEditorOutputPath);
    public double LayoutEditorFontSizeValue => ParseOrDefault(LayoutFontSize, 14, 8, 72);
    public double LayoutEditorScaledCanvasWidth => Math.Max(1, LayoutEditorCanvasWidth * Math.Max(0.25, LayoutEditorZoom));
    public double LayoutEditorScaledCanvasHeight => Math.Max(1, LayoutEditorCanvasHeight * Math.Max(0.25, LayoutEditorZoom));
    public string LayoutEditorZoomPercentLabel => (Math.Round(LayoutEditorZoom * 100.0, 0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
    public event Action<string>? WarningRequested;
    public string ModifierCapturePreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModifierCreateDevice) && string.IsNullOrWhiteSpace(ModifierCreateInput))
                return T("No input picked yet: press Record and then the shift button, or pick device and input.");
            string device = string.IsNullOrWhiteSpace(ModifierCreateDevice) ? T("(no device)") : DeviceLabelForStatus(ModifierCreateDevice);
            string input = string.IsNullOrWhiteSpace(ModifierCreateInput) ? T("(no input)") : ModifierCreateInput.Trim();
            return T("Shift button: {0} {1}", device, input);
        }
    }

    public MainWindowViewModel(
        IEngineCliClient? cliClient = null,
        IJoystickCaptureService? joystickCaptureService = null,
        bool loadPersistedUiSettings = true)
    {
        this.cliClient = cliClient ?? new EngineCliClient();
        this.joystickCaptureService = joystickCaptureService ?? new JoystickCaptureService();
        persistUiFiles = loadPersistedUiSettings;
        AutoImportOldLayouts = loadPersistedUiSettings;
        LayoutEditorMarkers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSaveLayoutEditorTemplate));
            RebuildLayoutEditorInputOptions();
            RebuildLayoutEditorMarkerTokenOptions();
            RebuildLayoutEditorModifierOptions();
        };
        LayoutEditorTables.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSaveLayoutEditorTemplate));
        ExportGames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ExportsMsfs2024));
        LayoutEditorConnectors.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSaveLayoutEditorTemplate));
        InitializeLayoutWorkspace();
        InitializeSettingsPage();
        if (loadPersistedUiSettings)
        {
            LoadUiSettings();
            // The language before any page is made (empty: the Windows display language), docs/localization.md.
            ApplyLanguageChoice(languageChoice);
            InitializeSessionLog();
            StartLlmServerWatch();
        }
        ApplyLlmServerSnapshot(LlmServerSnapshot.Stopped);
        EnginePath = TryResolveEnginePath();
        // The app's engine server reads the game lists while the app starts (EngineServerPool).
        if (loadPersistedUiSettings && this.cliClient is EngineCliClient)
            _ = EngineServerPool.PrewarmAsync(EnginePath);
        PrefillLayoutAutomationPaths();
        DcsSavedGamesPath = GuessSavedGamesDcsPath();
        DcsInstallPath = GuessDcsInstallPath();
        ImportDcsPath = DcsSavedGamesPath;
        ExportDcsPath = DcsSavedGamesPath;
        ImportStarCitizenPath = GuessStarCitizenProfilePath();
        ExportStarCitizenPath = ImportStarCitizenPath;
        PrefillStarCitizenSourcePath();
        PrefillDcsRefreshPaths();
        // The Export page's last settings on top of the prefilled paths (MainWindowViewModel.ExportSettings.cs).
        InitializeExportSettings(loadPersistedUiSettings);
        ApplyTheme(ThemeMode);
        RefreshDcsKnownDevices();
        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        RebuildLayoutGameOptions(BuildLayoutGameCandidates());
        RebuildImportDeviceOptions();
        SeedDcsFfbDeviceSettingsFromKnownDevices();
        RebuildVisibleDcsFfbDeviceSettings();
        UpdateRecordTargetContext();
        RebuildRelationNodeGameOptions();
        RebuildLayoutEditorInputOptions();
        RebuildLayoutEditorMarkerTokenOptions();
        RebuildLayoutEditorModifierOptions();
        RefreshRelationValidationState();
        RefreshModifierValidationState();

        InitializeFfbTelemetry(loadPersistedUiSettings);
        InitializeTelemetryRecording(loadPersistedUiSettings);
        InitializeVirtualJoystick(loadPersistedUiSettings);
        InitializeDesign();
        InitializeProfileHistory();
        // The last profile opens once the window shows (MainWindow.OnWindowOpened -> AutoOpenLastProfileOnStartupAsync),
        // so the start stays as fast as without a profile.
    }

    partial void OnCurrentStageChanged(UiStage value)
    {
        OnPropertyChanged(nameof(IsStartStage));
        BusyProgress.OnStartPage = value == UiStage.Start;
        if (value != UiStage.Start)
            OnStartPageLeft();
        OnPropertyChanged(nameof(IsRelationsStage));
        if (value == UiStage.Relations)
            BuilderOnScreen();
        else
            ResetRelationsPageTools();
        OnPropertyChanged(nameof(IsBindsStage));
        OnPropertyChanged(nameof(IsModifiersStage));
        OnPropertyChanged(nameof(IsLayoutStage));
        OnPropertyChanged(nameof(IsExportStage));
        OnPropertyChanged(nameof(IsSettingsStage));
        if (value == UiStage.Settings)
        {
            SyncFfbSettingsSection();
            SyncTelemetryRecordingSection();
        }
        OnPropertyChanged(nameof(IsAdvancedStage));
        OnPropertyChanged(nameof(IsHudStage));
        OnHudStageChanged(value == UiStage.Hud);
        OnPropertyChanged(nameof(IsFfbTelemetryStage));
        OnFfbTelemetryStageChanged(value == UiStage.FfbTelemetry);
        OnPropertyChanged(nameof(IsVirtualJoystickStage));
        OnVirtualJoystickStageChanged(value == UiStage.VirtualJoystick);
        OnCockpitsStageChanged(value == UiStage.Cockpits);
        OnComponentsStageChanged(value);
        UpdateAxisTuningMonitor();
        UpdateLiveInputMonitor();
        if (value != UiStage.Binds)
            CloseDetentWizard();

        if (suppressStageAutoRefresh)
            return;

        // The Binds page shows the profile's current relations and binds (relations may have changed on other pages).
        if (value == UiStage.Binds && !IsBusy && !string.IsNullOrWhiteSpace(ProfilePath))
            _ = RefreshBindsPageAsync();

        if (value == UiStage.Binds && !quickBindOptionsInitialized)
        {
            quickBindOptionsInitialized = true;
            _ = RefreshQuickBindOptionsAsync();
        }

        if (value == UiStage.Modifiers && !string.IsNullOrWhiteSpace(ProfilePath))
        {
            _ = RefreshModifiersAsync();
            _ = RefreshQuickBindOptionsAsync();
        }

        if (value == UiStage.Relations && !IsBusy)
            _ = RefreshRelationNodeCatalogAsync();

        if (value == UiStage.Start && !importCatalogInitialized && !IsBusy)
            _ = RefreshImportCatalogAsync();

        if (value == UiStage.Settings || value == UiStage.Export)
            _ = RefreshGameInstallsAsync();
        if (value == UiStage.Settings && !IsBusy)
        {
            PrefillStarCitizenSourcePath();
            PrefillDcsRefreshPaths();
            RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
            _ = DcsFfbListAsync();
        }

        if (value == UiStage.Layout && !IsBusy)
            _ = RefreshLayoutStageDataAsync();
        if (value == UiStage.Export && !IsBusy)
            _ = RefreshExportStageDataAsync();
        if (value == UiStage.Export)
        {
            _ = RefreshExportGamesAsync();
            // The backups list (and which tiles can undo an export).
            _ = RefreshGameBackupsAsync();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanRunDcs));
        OnPropertyChanged(nameof(CanRunIl2));
        OnPropertyChanged(nameof(CanRunStarCitizen));
        OnPropertyChanged(nameof(CanRunRegistryGame));
    }

    partial void OnRelationSearchTextChanged(string value) => SetRelationSearch(value);
    partial void OnRelationNameInputChanged(string value) => RefreshRelationValidationState();
    partial void OnBindSearchTextChanged(string value) => SetBindSearch(value);
    partial void OnBindDeviceFilterTextChanged(string value) => ApplyBindFilter();
    partial void OnImportPlanesCsvChanged(string value)
    {
        RebuildImportPlaneOptions(ImportPlaneOptions.Select(v => v.Name));
        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
        if (NormalizeRelationGameToken(RelationNodeGameInput).Equals("DCS", StringComparison.OrdinalIgnoreCase))
            _ = RefreshRelationNodePlaneOptionsAsync();
    }
    partial void OnDcsSavedGamesPathChanged(string value)
    {
        RefreshDcsKnownDevices();
        RebuildImportDeviceOptions();
        PrefillDcsRefreshPaths();
        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        SeedDcsFfbDeviceSettingsFromKnownDevices();
    }
    partial void OnDcsInstallPathChanged(string value)
    {
        PrefillDcsRefreshPaths();
    }
    partial void OnDcsRefreshPlanesCsvChanged(string value)
    {
        if (syncingDcsRefreshPlaneOptions)
            return;

        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
    }
    partial void OnDcsRefreshSelectAllPlanesChanged(bool value)
    {
        if (!value || dcsRefreshPlaneQuickSelectGuard)
            return;

        SetAllDcsRefreshPlaneSelections(true);
        ResetDcsRefreshPlaneQuickToggles();
    }
    partial void OnDcsRefreshSelectNonePlanesChanged(bool value)
    {
        if (!value || dcsRefreshPlaneQuickSelectGuard)
            return;

        SetAllDcsRefreshPlaneSelections(false);
        ResetDcsRefreshPlaneQuickToggles();
    }
    partial void OnImportDevicesCsvChanged(string value) => RebuildImportDeviceOptions();
    partial void OnImportIgnoreDevicesCsvChanged(string value) => RebuildImportIgnoreDeviceOptions();
    partial void OnImportAllDcsPlanesChanged(bool value) => OnPropertyChanged(nameof(ShowDcsPlaneSelection));
    partial void OnImportUseSelectedDevicesChanged(bool value) => OnPropertyChanged(nameof(ShowImportDeviceSelection));
    partial void OnImportStarCitizenPathChanged(string value) => PrefillStarCitizenSourcePath();
    partial void OnLayoutPlaneCsvChanged(string value)
    {
        NotifyCheatSheetPlan();
        if (syncingLayoutPlaneOptions)
            return;

        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
    }
    partial void OnLayoutDevicesCsvChanged(string value)
    {
        NotifyCheatSheetPlan();
        if (syncingLayoutDeviceOptions)
            return;

        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
    }
    partial void OnLayoutGameCsvChanged(string value)
    {
        NotifyCheatSheetPlan();
        if (syncingLayoutGameOptions)
            return;

        RebuildLayoutGameOptions(BuildLayoutGameCandidates());
    }
    partial void OnLayoutSelectAllDevicesChanged(bool value)
    {
        if (!value || layoutDeviceQuickSelectGuard)
            return;

        SetAllLayoutDeviceSelections(true);
        ResetLayoutDeviceQuickToggles();
    }
    partial void OnLayoutSelectNoneDevicesChanged(bool value)
    {
        if (!value || layoutDeviceQuickSelectGuard)
            return;

        SetAllLayoutDeviceSelections(false);
        ResetLayoutDeviceQuickToggles();
    }
    partial void OnLayoutSelectAllGamesChanged(bool value)
    {
        if (!value || layoutGameQuickSelectGuard)
            return;

        SetAllLayoutGameSelections(true);
        ResetLayoutGameQuickToggles();
    }
    partial void OnLayoutSelectNoneGamesChanged(bool value)
    {
        if (!value || layoutGameQuickSelectGuard)
            return;

        SetAllLayoutGameSelections(false);
        ResetLayoutGameQuickToggles();
    }
    partial void OnLayoutSelectAllPlanesChanged(bool value)
    {
        if (!value || layoutPlaneQuickSelectGuard)
            return;

        SetAllLayoutPlaneSelections(true);
        ResetLayoutPlaneQuickToggles();
    }
    partial void OnLayoutSelectNonePlanesChanged(bool value)
    {
        if (!value || layoutPlaneQuickSelectGuard)
            return;

        SetAllLayoutPlaneSelections(false);
        ResetLayoutPlaneQuickToggles();
    }
    partial void OnDcsFfbFilterTextChanged(string value) => RebuildVisibleDcsFfbDeviceSettings();
    partial void OnDcsFfbShowConnectedOnlyChanged(bool value) => RebuildVisibleDcsFfbDeviceSettings();
    partial void OnDcsFfbShowProfileOnlyChanged(bool value) => RebuildVisibleDcsFfbDeviceSettings();
    partial void OnBindDeviceInputChanged(string value)
    {
        UpdateRecordTargetContext();
        if (syncingBindDeviceChoice)
            return;
        // A selected bind, Record or a typed name: the hand pick shows that device and its inputs.
        if (!BindDeviceChoices.Any(c => string.Equals(c.Device, (value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)))
            RebuildBindPickOptions();
        else
        {
            SyncBindDeviceChoice();
            RebuildBindInputChoices();
        }
    }
    partial void OnBindInputValueChanged(string value) => UpdateRecordTargetContext();
    partial void OnRelationNodeGameInputChanged(string value)
    {
        if (RelationNodeSearchResults.Count > 0)
        {
            string normalized = NormalizeRelationGameToken(value);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = value?.Trim() ?? string.Empty;
            RelationNodeSearchStatus = T("Game changed to {0}. Existing results kept; click Search to refresh.", normalized);
        }
        _ = RefreshRelationNodePlaneOptionsAsync();
    }
    partial void OnRelationNodeSearchStatusChanged(string value) => OnPropertyChanged(nameof(HasRelationNodeSearchStatus));
    partial void OnSelectedRelationNodeSearchResultChanged(DbNodeSearchResultItem? value)
    {
        if (value == null)
        {
            if (editingRelationNode == null)
                ApplyRelationNodePlaneFilterToSearchResults();
            return;
        }

        RelationNodeIdInput = value.NodeId;
        if (!string.Equals(RelationNodeGameInput, value.Game, StringComparison.OrdinalIgnoreCase))
            RelationNodeGameInput = value.Game;
        editingRelationNode = null;

        string plane = (value.Plane ?? string.Empty).Trim();
        List<string> preferredPlanes = plane.Length > 0 && !plane.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? [plane]
            : [];
        _ = RefreshRelationNodePlaneOptionsAsync(preferredPlanes);
    }

    partial void OnSelectedRelationNodeChanged(RelationNodeItem? value)
    {
        if (value == null)
        {
            if (SelectedRelationNodeSearchResult == null && editingRelationNode == null)
                _ = RefreshRelationNodePlaneOptionsAsync();
            return;
        }

        List<string> preferred = ParseCsv(value.ActivePlanesText);
        _ = RefreshRelationNodePlaneOptionsAsync(preferred);
    }
    partial void OnSelectedLayoutSuggestionChanged(LayoutSuggestionItem? value)
    {
        if (value == null)
            return;
        // A weak match says where to pick the right model.
        LayoutSuggestStatus = string.IsNullOrWhiteSpace(value.TemplateName) ? string.Empty
            : value.Score < 60 ? T("Picture: {0} (match {1}%). Not your device? Model... > Choose another model", value.TemplateName, value.Score.ToString(CultureInfo.InvariantCulture))
            : T("Picture: {0} (match {1}%)", value.TemplateName, value.Score.ToString(CultureInfo.InvariantCulture));
        bool otherDevice = !string.Equals(layoutEditorContextDevice, (value.Device ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        layoutEditorContextDevice = (value.Device ?? string.Empty).Trim();
        // The device reference (docs/device-reference.md) of the picked device at once: your photo, else the maker's
        // photo, else JoyPro's drawing, with markers and the legend. A layout file of the user's own is shown instead
        // (both stay one click apart in the picture choice).
        if (layoutEditorContextDevice.Length > 0 && (otherDevice || deviceReference?.HasModel != true))
            _ = ShowDeviceReferenceForAsync(layoutEditorContextDevice);
        // A 2D layout picked for this device (Layout page, kept over restarts) is its layout file.
        string? variantLayout = layoutEditorContextDevice.Length > 0 ? VariantLayoutOf(layoutEditorContextDevice) : null;
        if (variantLayout != null)
            savedLayoutDevices.Add(layoutEditorContextDevice);
        if (otherDevice)
        {
            ClearLayoutUndo();
            LayoutShowsLayoutFile = variantLayout != null || PrefersLayoutFile(value);
            // The page with several views belongs to the last device.
            if (IsMultiViewOpen)
                CloseMultiView();
        }
        if (variantLayout == null)
            RememberLayoutDeviceMapping(layoutEditorContextDevice, value.LayoutPath);
        string candidate = TryFindFirstExistingFile(
        [
            variantLayout ?? string.Empty,
            value.LayoutPath,
            value.BackdropPath
        ]);

        OnPropertyChanged(nameof(LayoutWorkspaceDeviceTitle));
        OnPropertyChanged(nameof(LayoutEmptyTitle));
        OnPropertyChanged(nameof(LayoutEmptyHint));
        if (candidate.Length == 0)
        {
            // No picture of this device yet: an empty workspace (with "use your own picture"), not the last device's.
            LayoutEditorMarkers.Clear();
            LayoutEditorTables.Clear();
            LayoutEditorConnectors.Clear();
            Bitmap? previous = LayoutEditorPreviewImage;
            LayoutEditorPreviewImage = null;
            previous?.Dispose();
            LayoutEditorSourcePath = string.Empty;
            LayoutEditorOutputPath = string.Empty;
            LayoutShowsLayoutFile = false;
            RebuildWorkspacePictures();
            LayoutEditorStatus = T("No layout file of {0} yet: JoyPro's reference picture is shown.", DeviceDisplayName(value.Device));
            return;
        }

        // The same picture again (the list was refreshed): keep the markers and their unsaved edits.
        if (string.Equals(NormalizeUiPath(LayoutEditorSourcePath), NormalizeUiPath(candidate), StringComparison.OrdinalIgnoreCase) &&
            (LayoutEditorMarkers.Count > 0 || LayoutEditorPreviewImage != null) && value.SuggestedTables.Count == 0)
        {
            RefreshLayoutMarkerCommands();
            return;
        }

        if (!string.Equals(LayoutEditorSourcePath, candidate, StringComparison.OrdinalIgnoreCase))
            LayoutEditorSourcePath = candidate;

        if (value.SuggestedTables.Count > 0)
        {
            LayoutEditorTables.Clear();
            foreach (LayoutEditorTableItem table in value.SuggestedTables)
            {
                LayoutEditorTables.Add(new LayoutEditorTableItem
                {
                    Name = table.Name,
                    Mode = NormalizeLayoutTableMode(table.Mode),
                    X = table.X,
                    Y = table.Y,
                    Width = table.Width,
                    Height = table.Height,
                    Columns = table.Columns,
                    BackgroundColor = NormalizeLayoutTableBackgroundColor(table.BackgroundColor),
                    BackgroundOpacity = Math.Clamp(table.BackgroundOpacity, 0, 1),
                    DropShadow = table.DropShadow
                });
            }
            SelectedLayoutEditorTable = LayoutEditorTables.FirstOrDefault();
        }

        // Make selection feel immediate: choosing a suggestion loads it directly into the embedded editor.
        _ = LoadLayoutEditorSourceAsync();
    }
    partial void OnLayoutEditorSourcePathChanged(string value)
    {
        OnPropertyChanged(nameof(CanSaveLayoutEditorTemplate));
        if (string.IsNullOrWhiteSpace(LayoutEditorOutputPath))
            LayoutEditorOutputPath = BuildDefaultLayoutEditorOutputPath(value);
    }
    partial void OnLayoutEditorOutputPathChanged(string value) => OnPropertyChanged(nameof(CanSaveLayoutEditorTemplate));
    partial void OnLayoutEditorPreviewImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasLayoutEditorPreview));
        OnPropertyChanged(nameof(HasLayoutWorkspacePicture));
        OnPropertyChanged(nameof(HasNoLayoutWorkspacePicture));
    }
    partial void OnLayoutEditorCanvasWidthChanged(double value) => OnPropertyChanged(nameof(LayoutEditorScaledCanvasWidth));
    partial void OnLayoutEditorCanvasHeightChanged(double value) => OnPropertyChanged(nameof(LayoutEditorScaledCanvasHeight));
    partial void OnLayoutEditorZoomChanged(double value)
    {
        double clamped = Math.Clamp(value, Views.Controls.DeviceMarkerCanvas.MinZoom, Views.Controls.DeviceMarkerCanvas.MaxZoom);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            LayoutEditorZoom = clamped;
            return;
        }

        OnPropertyChanged(nameof(LayoutEditorZoomPercentLabel));
        OnPropertyChanged(nameof(LayoutEditorScaledCanvasWidth));
        OnPropertyChanged(nameof(LayoutEditorScaledCanvasHeight));
    }
    partial void OnSelectedLayoutEditorMarkerChanged(LayoutEditorMarkerItem? value)
    {
        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers)
        {
            marker.IsSelected = ReferenceEquals(marker, value);
            if (!ReferenceEquals(marker, value))
                marker.IsFlashing = false;
        }
        OnLayoutWorkspaceSelectionChanged();

        if (value == null)
        {
            layoutMarkerFlashCts?.Cancel();
            RebuildLayoutEditorInputOptions();
            RebuildLayoutEditorModifierOptions();
            return;
        }

        LayoutEditorNewToken = value.Token;
        LayoutEditorNewType = string.IsNullOrWhiteSpace(value.Type) ? "button" : value.Type;
        LayoutEditorNewModifier = value.Modifier ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value.Token))
            LayoutEditorNewConnectorTargetInput = value.Token.Trim();
        RebuildLayoutEditorInputOptions();
        RebuildLayoutEditorModifierOptions();
        TriggerLayoutMarkerFlash(value);
    }
    partial void OnSelectedLayoutEditorTableChanged(LayoutEditorTableItem? value)
    {
        foreach (LayoutEditorTableItem table in LayoutEditorTables)
        {
            table.IsSelected = ReferenceEquals(table, value);
            if (!ReferenceEquals(table, value))
                table.IsFlashing = false;
        }

        if (value == null)
        {
            layoutTableFlashCts?.Cancel();
            return;
        }

        LayoutEditorNewTableName = value.Name ?? "Table";
        LayoutEditorNewTableMode = NormalizeLayoutTableMode(value.Mode);
        LayoutEditorNewTableWidth = Math.Max(80, value.Width).ToString(CultureInfo.InvariantCulture);
        LayoutEditorNewTableHeight = Math.Max(60, value.Height).ToString(CultureInfo.InvariantCulture);
        LayoutEditorNewTableColumns = Math.Max(1, value.Columns).ToString(CultureInfo.InvariantCulture);
        LayoutEditorNewTableBackgroundColor = string.IsNullOrWhiteSpace(value.BackgroundColor) ? "#F6F8FB" : value.BackgroundColor.Trim();
        LayoutEditorNewTableBackgroundOpacity = Math.Clamp(value.BackgroundOpacity, 0, 1).ToString("0.##", CultureInfo.InvariantCulture);
        LayoutEditorNewTableDropShadow = value.DropShadow;
        TriggerLayoutTableFlash(value);
    }
    private void TriggerLayoutMarkerFlash(LayoutEditorMarkerItem marker)
    {
        layoutMarkerFlashCts?.Cancel();
        CancellationTokenSource cts = new();
        layoutMarkerFlashCts = cts;
        _ = FlashLayoutMarkerAsync(marker, cts.Token);
    }

    private void TriggerLayoutTableFlash(LayoutEditorTableItem table)
    {
        layoutTableFlashCts?.Cancel();
        CancellationTokenSource cts = new();
        layoutTableFlashCts = cts;
        _ = FlashLayoutTableAsync(table, cts.Token);
    }

    private static async Task FlashLayoutMarkerAsync(LayoutEditorMarkerItem marker, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 2; i++)
            {
                marker.IsFlashing = true;
                await Task.Delay(130, cancellationToken);
                marker.IsFlashing = false;
                await Task.Delay(90, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            marker.IsFlashing = false;
        }
    }

    private static async Task FlashLayoutTableAsync(LayoutEditorTableItem table, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 2; i++)
            {
                table.IsFlashing = true;
                await Task.Delay(130, cancellationToken);
                table.IsFlashing = false;
                await Task.Delay(90, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            table.IsFlashing = false;
        }
    }

    partial void OnSelectedLayoutEditorConnectorChanged(LayoutEditorConnectorItem? value)
    {
        OnPropertyChanged(nameof(SelectedLayoutEditorConnectorPointHandles));

        if (!ReferenceEquals(observedLayoutEditorConnector, null))
            observedLayoutEditorConnector.PropertyChanged -= OnObservedLayoutEditorConnectorPropertyChanged;
        observedLayoutEditorConnector = value;
        if (!ReferenceEquals(observedLayoutEditorConnector, null))
            observedLayoutEditorConnector.PropertyChanged += OnObservedLayoutEditorConnectorPropertyChanged;

        foreach (LayoutEditorConnectorItem connector in LayoutEditorConnectors)
        {
            connector.IsSelected = ReferenceEquals(connector, value);
            if (!ReferenceEquals(connector, value))
                connector.IsFlashing = false;
        }

        if (value == null)
        {
            layoutConnectorFlashCts?.Cancel();
            return;
        }

        syncingLayoutEditorConnectorDraft = true;
        LayoutEditorNewConnectorName = value.Name ?? "Connector";
        LayoutEditorNewConnectorColor = string.IsNullOrWhiteSpace(value.Color) ? "#E2E8F0" : value.Color;
        LayoutEditorNewConnectorWidth = Math.Max(1, value.StrokeWidth).ToString(CultureInfo.InvariantCulture);
        LayoutEditorNewConnectorRoute = NormalizeLayoutConnectorRoute(value.Route);
        LayoutEditorNewConnectorSteps = Math.Clamp(value.Steps, 0, 8).ToString(CultureInfo.InvariantCulture);
        LayoutEditorNewConnectorTargetInput = value.TargetToken ?? string.Empty;
        LayoutEditorNewConnectorPoints = value.PointsCsv ?? string.Empty;
        syncingLayoutEditorConnectorDraft = false;
        if (value.Points.Count > 0)
        {
            Point source = ClampCanvasPoint(value.Points[0]);
            layoutEditorConnectorSourcePoint = source;
            LayoutEditorConnectorSourcePointLabel =
                source.X.ToString("0.##", CultureInfo.InvariantCulture) +
                "," +
                source.Y.ToString("0.##", CultureInfo.InvariantCulture);
        }
        if (!string.IsNullOrWhiteSpace(value.TargetToken))
        {
            LayoutEditorMarkerItem? target = LayoutEditorMarkers.FirstOrDefault(v =>
                string.Equals(v.Token, value.TargetToken, StringComparison.OrdinalIgnoreCase));
            if (target != null)
                TriggerLayoutMarkerFlash(target);
        }
        TriggerLayoutConnectorFlash(value);
    }

    private void OnObservedLayoutEditorConnectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (syncingLayoutEditorConnectorDraft || sender is not LayoutEditorConnectorItem connector)
            return;
        if (!ReferenceEquals(connector, SelectedLayoutEditorConnector))
            return;

        syncingLayoutEditorConnectorDraft = true;
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.Name))
            LayoutEditorNewConnectorName = connector.Name ?? "Connector";
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.Color))
            LayoutEditorNewConnectorColor = string.IsNullOrWhiteSpace(connector.Color) ? "#E2E8F0" : connector.Color;
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.StrokeWidth))
            LayoutEditorNewConnectorWidth = Math.Max(1, connector.StrokeWidth).ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.Route))
            LayoutEditorNewConnectorRoute = NormalizeLayoutConnectorRoute(connector.Route);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.Steps))
            LayoutEditorNewConnectorSteps = Math.Clamp(connector.Steps, 0, 8).ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.TargetToken))
            LayoutEditorNewConnectorTargetInput = connector.TargetToken ?? string.Empty;
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(LayoutEditorConnectorItem.PointsCsv))
            LayoutEditorNewConnectorPoints = connector.PointsCsv ?? string.Empty;
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(LayoutEditorConnectorItem.PointHandles) ||
            e.PropertyName == nameof(LayoutEditorConnectorItem.Points))
            OnPropertyChanged(nameof(SelectedLayoutEditorConnectorPointHandles));
        syncingLayoutEditorConnectorDraft = false;
    }

    private void TriggerLayoutConnectorFlash(LayoutEditorConnectorItem connector)
    {
        layoutConnectorFlashCts?.Cancel();
        CancellationTokenSource cts = new();
        layoutConnectorFlashCts = cts;
        _ = FlashLayoutConnectorAsync(connector, cts.Token);
    }

    private static async Task FlashLayoutConnectorAsync(LayoutEditorConnectorItem connector, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 2; i++)
            {
                connector.IsFlashing = true;
                await Task.Delay(130, cancellationToken);
                connector.IsFlashing = false;
                await Task.Delay(90, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            connector.IsFlashing = false;
        }
    }
    partial void OnLayoutFontSizeChanged(string value) => OnPropertyChanged(nameof(LayoutEditorFontSizeValue));
    partial void OnLayoutEditorNewTypeChanged(string value)
    {
        RebuildLayoutEditorInputOptions();
        string normalizedType = NormalizeLayoutInputType(value);
        string current = (LayoutEditorNewToken ?? string.Empty).Trim();
        if (current.Length == 0 || IsLayoutInputTypeMatch(current, normalizedType))
            return;

        string replacement = LayoutEditorInputOptions.FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(replacement))
            LayoutEditorNewToken = replacement;
    }

    partial void OnLayoutEditorNewConnectorRouteChanged(string value)
    {
        string normalized = NormalizeLayoutConnectorRoute(value);
        if (!string.Equals(normalized, value, StringComparison.OrdinalIgnoreCase))
        {
            LayoutEditorNewConnectorRoute = normalized;
            return;
        }

        if (string.Equals(normalized, "diagonal", StringComparison.OrdinalIgnoreCase))
            LayoutEditorNewConnectorSteps = "0";
        else if (string.Equals(normalized, "orthogonal", StringComparison.OrdinalIgnoreCase) &&
                 ParseOrDefault(LayoutEditorNewConnectorSteps, 2, 0, 8) < 1)
            LayoutEditorNewConnectorSteps = "2";
    }

    partial void OnLayoutEditorNewModifierChanged(string value)
    {
        LayoutEditorModifierPreview = string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    partial void OnLayoutEditorNewTableBackgroundOpacityChanged(string value)
    {
        double opacity = ParseDoubleOrDefault(value, 0.93, 0, 1);
        string normalized = opacity.ToString("0.##", CultureInfo.InvariantCulture);
        if (!string.Equals((value ?? string.Empty).Trim(), normalized, StringComparison.Ordinal))
            LayoutEditorNewTableBackgroundOpacity = normalized;
    }

    partial void OnLayoutPlaceMarkerModeChanged(bool value)
    {
        if (!value)
            return;

        if (LayoutPlaceTableMode)
            LayoutPlaceTableMode = false;
        if (LayoutPlaceConnectorPointMode)
            LayoutPlaceConnectorPointMode = false;
        if (LayoutPlaceConnectorSourceMode)
            LayoutPlaceConnectorSourceMode = false;
    }
    partial void OnLayoutPlaceTableModeChanged(bool value)
    {
        if (!value)
            return;

        if (LayoutPlaceMarkerMode)
            LayoutPlaceMarkerMode = false;
        if (LayoutPlaceConnectorPointMode)
            LayoutPlaceConnectorPointMode = false;
        if (LayoutPlaceConnectorSourceMode)
            LayoutPlaceConnectorSourceMode = false;
    }
    partial void OnLayoutPlaceConnectorPointModeChanged(bool value)
    {
        if (!value)
            return;

        if (LayoutPlaceMarkerMode)
            LayoutPlaceMarkerMode = false;
        if (LayoutPlaceTableMode)
            LayoutPlaceTableMode = false;
        if (LayoutPlaceConnectorSourceMode)
            LayoutPlaceConnectorSourceMode = false;
    }

    partial void OnLayoutPlaceConnectorSourceModeChanged(bool value)
    {
        if (!value)
            return;

        if (LayoutPlaceMarkerMode)
            LayoutPlaceMarkerMode = false;
        if (LayoutPlaceTableMode)
            LayoutPlaceTableMode = false;
        if (LayoutPlaceConnectorPointMode)
            LayoutPlaceConnectorPointMode = false;
    }

    [RelayCommand]
    private void LayoutEditorZoomIn()
    {
        LayoutEditorZoom = Math.Round(Math.Clamp(LayoutEditorZoom * 1.25, Views.Controls.DeviceMarkerCanvas.MinZoom, Views.Controls.DeviceMarkerCanvas.MaxZoom), 3, MidpointRounding.AwayFromZero);
    }

    [RelayCommand]
    private void LayoutEditorZoomOut()
    {
        LayoutEditorZoom = Math.Round(Math.Clamp(LayoutEditorZoom / 1.25, Views.Controls.DeviceMarkerCanvas.MinZoom, Views.Controls.DeviceMarkerCanvas.MaxZoom), 3, MidpointRounding.AwayFromZero);
    }

    [RelayCommand]
    private void LayoutEditorZoomReset()
    {
        LayoutEditorZoom = 1.0;
    }
    partial void OnModifierCreateNameChanged(string value) => RefreshModifierValidationState();
    partial void OnModifierCreateDeviceChanged(string value)
    {
        OnPropertyChanged(nameof(ModifierCapturePreview));
        RefreshModifierValidationState();
    }
    partial void OnModifierCreateInputChanged(string value)
    {
        OnPropertyChanged(nameof(ModifierCapturePreview));
        RefreshModifierValidationState();
    }
    partial void OnModifierRenameOldChanged(string value) => RefreshModifierValidationState();
    partial void OnModifierRenameNewChanged(string value) => RefreshModifierValidationState();
    partial void OnRelationCreateValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasRelationCreateValidationMessage));
    partial void OnModifierCreateValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasModifierCreateValidationMessage));
    partial void OnSelectedModifierItemChanged(ModifierListItem? value)
    {
        RefreshSelectedModifierRelations();
        if (value != null)
            ModifierRenameOld = value.Name;
        RefreshModifierValidationState();
    }

    partial void OnSelectedRelationChanged(RelationListItem? value)
    {
        // The list is filled again and the same relation comes back (RebuildVisibleRelations): nothing to load.
        if (rebuildingVisibleRelations)
            return;
        // No relation open: its actions leave the editor with it (only the empty state shows then).
        if (value == null && RelationNodes.Count > 0)
        {
            SelectedRelationNode = null;
            RelationNodes.Clear();
        }
        if (value != null && !suppressRelationAutoLoad)
        {
            _ = ShowRelationAsync(value.Name);
        }
        UpdateRecordTargetContext();
        BuilderFollowSelection(value);
        UpdateRelationCheck();
    }

    private void RefreshRelationValidationState()
    {
        RelationCreateValidationMessage = BuildRelationCreateValidationMessage();
        OnPropertyChanged(nameof(CanCreateRelation));
    }

    private void RefreshModifierValidationState()
    {
        ModifierCreateValidationMessage = BuildModifierCreateValidationMessage();
        OnPropertyChanged(nameof(CanCreateModifier));
        OnPropertyChanged(nameof(CanRenameModifier));
    }

    private bool EvaluateCanCreateRelation()
    {
        string name = (RelationNameInput ?? string.Empty).Trim();
        return name.Length > 0 && !RelationNameExists(name);
    }

    private bool EvaluateCanCreateModifier()
    {
        string name = (ModifierCreateName ?? string.Empty).Trim();
        string device = (ModifierCreateDevice ?? string.Empty).Trim();
        string input = (ModifierCreateInput ?? string.Empty).Trim();

        if (name.Length == 0 || IsPlaceholderToken(device) || IsPlaceholderToken(input))
            return false;

        if (ModifierNameExists(name))
            return false;

        return !ModifierInputAlreadyUsed(device, input);
    }

    private string BuildRelationCreateValidationMessage()
    {
        string name = (RelationNameInput ?? string.Empty).Trim();
        if (name.Length == 0)
            return string.Empty;

        return RelationNameExists(name)
            ? T("Relation '{0}' already exists.", name)
            : string.Empty;
    }

    private string BuildModifierCreateValidationMessage()
    {
        string name = (ModifierCreateName ?? string.Empty).Trim();
        if (name.Length == 0)
            return string.Empty;

        if (ModifierNameExists(name))
            return T("Modifier '{0}' already exists.", name);

        string device = (ModifierCreateDevice ?? string.Empty).Trim();
        string input = (ModifierCreateInput ?? string.Empty).Trim();
        if (IsPlaceholderToken(device) || IsPlaceholderToken(input))
            return string.Empty;

        string? duplicate = FindModifierNameByDeviceInput(device, input);
        return duplicate != null
            ? T("Input already used by modifier '{0}'.", duplicate)
            : string.Empty;
    }

    private bool RelationNameExists(string name)
    {
        string normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return false;

        if (allRelations.Any(v => string.Equals(v.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return Relations.Any(v => string.Equals(v.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool EvaluateCanRenameModifier()
    {
        string oldName = (ModifierRenameOld ?? string.Empty).Trim();
        string newName = (ModifierRenameNew ?? string.Empty).Trim();

        if (oldName.Length == 0 || newName.Length == 0)
            return false;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ModifierNameExists(oldName))
            return false;

        return !ModifierNameExists(newName);
    }

    private bool ModifierNameExists(string name)
    {
        string normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return false;

        if (pendingModifiersByName.ContainsKey(normalized))
            return true;

        if (ModifierItems.Any(v => string.Equals(v.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (ModifierNames.Any(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return Modifiers.Any(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool ModifierInputAlreadyUsed(string device, string input)
        => FindModifierNameByDeviceInput(device, input) != null;

    private string? FindModifierNameByDeviceInput(string device, string input)
    {
        string normalizedDevice = NormalizeDeviceToken(device);
        string normalizedInput = NormalizeInputToken(input);
        if (normalizedDevice.Length == 0 || normalizedInput.Length == 0)
            return null;

        foreach (PendingModifierDefinition pending in pendingModifiersByName.Values)
        {
            if (NormalizeDeviceToken(pending.Device) == normalizedDevice &&
                NormalizeInputToken(pending.Input) == normalizedInput)
                return pending.Name;
        }

        foreach (KeyValuePair<string, List<ModifierReformerMatch>> entry in modifierReformerCacheByName)
        {
            List<ModifierReformerMatch>? matches = entry.Value;
            if (matches == null || matches.Count == 0)
                continue;

            for (int i = 0; i < matches.Count; i++)
            {
                ModifierReformerMatch match = matches[i];
                if (NormalizeDeviceToken(match.Device) == normalizedDevice &&
                    NormalizeInputToken(match.Input) == normalizedInput)
                    return string.IsNullOrWhiteSpace(match.Name)
                        ? entry.Key
                        : match.Name;
            }
        }

        foreach (ModifierListItem item in ModifierItems)
        {
            string itemDevice = (item.PrimaryDevice ?? string.Empty).Trim();
            string itemInput = (item.PrimaryInput ?? string.Empty).Trim();
            if (IsPlaceholderToken(itemDevice) || IsPlaceholderToken(itemInput))
                continue;

            if (NormalizeDeviceToken(itemDevice) == normalizedDevice &&
                NormalizeInputToken(itemInput) == normalizedInput)
                return item.Name;
        }

        return null;
    }

    private static bool IsPlaceholderToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
            return true;

        return token.Equals("<none>", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("<unknown>", StringComparison.OrdinalIgnoreCase);
    }

    public void SetRelationMassSelection(IEnumerable<RelationListItem> selectedRelations)
    {
        relationMassSelectedNames.Clear();
        foreach (RelationListItem relation in selectedRelations)
        {
            if (!string.IsNullOrWhiteSpace(relation.Name))
                relationMassSelectedNames.Add(relation.Name.Trim());
        }

        UpdateRelationMassSelectionSummary();
    }

    partial void OnSelectedBindChanged(BindListItem? value)
    {
        // Bind on the picture binds the relation picked on the left.
        if (!restoringBindSelection)
            OnVisualAssignRelationPicked();
        if (restoringBindSelection)
        {
            // Same relation re-selected after a list rebuild: keep editor values and skip engine calls.
            SelectedBindIsAxis = value != null && IsAxisRelation(value.RelationName);
            UpdateRecordTargetContext();
            return;
        }

        // The peek at the relation's actions (MainWindowViewModel.BindPeek.cs).
        _ = LoadBindPeekAsync(value?.RelationName ?? string.Empty);
        if (value != null)
        {
            if (value.HasBind)
                _ = ShowBindAsync(value.RelationName);
            else
                _ = ShowRelationAsync(value.RelationName);

            if (!string.IsNullOrWhiteSpace(value.Device) && !value.Device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase))
                BindDeviceInput = value.Device;
            else
                BindDeviceInput = string.Empty;
            if (!string.IsNullOrWhiteSpace(value.Input) && !value.Input.Equals("<unbound>", StringComparison.OrdinalIgnoreCase))
                BindInputValue = value.Input;
            else
                BindInputValue = string.Empty;

            if (!string.IsNullOrWhiteSpace(BindDeviceInput))
                ModifierCreateDevice = BindDeviceInput;
            if (!string.IsNullOrWhiteSpace(BindInputValue))
                ModifierCreateInput = BindInputValue;
            if (value.HasBind)
                ModifierCreateTargetRelation = value.RelationName;

            LoadBindBehaviorEditor(value);
        }
        SelectedBindIsAxis = value != null && IsAxisRelation(value.RelationName);
        UpdateRecordTargetContext();
        SyncModifierSelectionFromSelectedBind();
        OnAxisTuningBindSelectionChanged();
        OnDetentBindSelectionChanged();
    }

    // Set Bind sends the behavior checkboxes, so they must start from the stored values of the selected bind.
    private void LoadBindBehaviorEditor(BindListItem bind)
    {
        BindInverted = bind.Inverted ?? false;
        BindSlider = bind.Slider ?? false;
        BindDoubleTap = bind.DoubleTap ?? false;
        BindDeadzoneValue = bind.Deadzone?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        BindSaturationXValue = bind.SaturationX?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        BindSaturationYValue = bind.SaturationY?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        BindCurvatureCsv = bind.CurvatureCsv ?? string.Empty;
        BindApplyCurvature = false;
        BindApplyDeadzone = false;
        BindApplySaturationX = false;
        BindApplySaturationY = false;
    }

    private bool IsAxisRelation(string relationName)
    {
        string name = (relationName ?? string.Empty).Trim();
        if (name.Length == 0)
            return false;

        BindListItem? bind = allBinds.FirstOrDefault(b => string.Equals(b.RelationName, name, StringComparison.OrdinalIgnoreCase));
        if (bind != null)
            return bind.IsAxis || string.Equals(bind.Type?.Trim(), "axis", StringComparison.OrdinalIgnoreCase);

        RelationListItem? relation = allRelations.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        return relation != null && string.Equals(relation.Type?.Trim(), "axis", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task RefreshQuickBindOptionsAsync()
    {
        if (!string.IsNullOrWhiteSpace(ProfilePath))
            await RefreshDevicesAsync();
        RefreshDcsKnownDevices();
        await RefreshDeviceInputCatalogAsync();
        RebuildQuickBindOptions();
    }

    [RelayCommand]
    private async Task RefreshImportCatalogAsync()
    {
        RefreshDcsKnownDevices();
        await RefreshDevicesAsync();
        await RefreshImportDcsPlaneOptionsAsync();
        if (RegistryGames.Count == 0)
            await RefreshRegistryGamesAsync();
        importCatalogInitialized = true;
    }

    [RelayCommand]
    private async Task RefreshDcsWorkspaceAsync()
    {
        RefreshDcsKnownDevices();
        await RefreshDevicesAsync();
        await RefreshImportDcsPlaneOptionsAsync();
        importCatalogInitialized = true;
    }

    [RelayCommand]
    private async Task RefreshIl2WorkspaceAsync()
    {
        await RefreshDevicesAsync();
        importCatalogInitialized = true;
        StatusText = T("IL-2 workspace options refreshed.");
    }

    [RelayCommand]
    private async Task RefreshStarCitizenWorkspaceAsync()
    {
        List<string> args = ["starcitizen", "list-devices"];
        if (!string.IsNullOrWhiteSpace(ImportStarCitizenPath))
            args.AddRange(["--path", ImportStarCitizenPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath))
            args.AddRange(["--source", StarCitizenSourcePath.Trim()]);

        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Refreshing Star Citizen options..."));
        await RefreshDevicesAsync();
        importCatalogInitialized = true;
    }

    [RelayCommand]
    private void GoStart() => CurrentStage = UiStage.Start;

    [RelayCommand]
    private void GoRelations() => CurrentStage = UiStage.Relations;

    [RelayCommand]
    private void GoBinds() => CurrentStage = UiStage.Binds;

    [RelayCommand]
    private void GoModifiers() => CurrentStage = UiStage.Modifiers;

    // The old "Sort by" keys of the Binds page: that key becomes the Binds sort's first level (again: turned around).
    [RelayCommand]
    private void SortBindsByColumn(string? columnKey)
    {
        string key = (columnKey ?? string.Empty).Trim() switch
        {
            "RelationName" => "name",
            "Device" => "device",
            "Input" => "input",
            "BindState" => "bound",
            "TypeDisplay" => "kind",
            var other => other.ToLowerInvariant()
        };
        if (key.Length == 0)
            return;
        EnsureListFilters();
        BindSort.SetPrimary(key);
    }

    [RelayCommand]
    private void GoLayout() => CurrentStage = UiStage.Layout;

    [RelayCommand]
    private void GoExport()
    {
        bool wasExport = CurrentStage == UiStage.Export;
        CurrentStage = UiStage.Export;
        // Entering the page loads the backups itself.
        if (wasExport)
            _ = RefreshGameBackupsAsync();
    }

    [RelayCommand]
    private void GoSettings() => CurrentStage = UiStage.Settings;

    [RelayCommand]
    private void GoAdvanced() => CurrentStage = UiStage.Advanced;

    [RelayCommand]
    private void OpenImportWorkspace()
    {
        CurrentStage = UiStage.Start;
        StatusText = T("Configure import options in Start Workspace and run import there.");
    }

    [RelayCommand]
    private void SetThemeLight() => ApplyTheme("Light");

    [RelayCommand]
    private void SetThemeDark() => ApplyTheme("Dark");

    [RelayCommand]
    private void SetThemeSystem() => ApplyTheme("System");

    [RelayCommand]
    private async Task RunEngineSummaryAsync()
    {
        await RunEngineAsync(["summary"], includeProfile: false, mutatesProfile: false, busyLabel: N("Loading engine summary..."));
    }

    [RelayCommand]
    private async Task RunProfileSummaryAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            StatusText = T("Set profile path first.");
            return;
        }

        await RunEngineAsync(["profile", "summary"], includeProfile: true, mutatesProfile: false, busyLabel: N("Loading profile summary..."));
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await RunProfileSummaryAsync();
        await RefreshRelationsAsync();
        await RefreshBindsAndModifiersAfterChangeAsync();
        await RefreshDevicesAsync();
        await RefreshImportDcsPlaneOptionsAsync();
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        pendingModifiersByName.Clear();
        if (string.IsNullOrWhiteSpace(ProfilePath))
            ProfilePath = CreateFreshAutoProfilePath();

        // "profile new" writes an empty profile to the chosen file (the save dialog already confirmed overwriting).
        EngineCommandResult? result = await RunEngineAsync(["profile", "new", ProfilePath.Trim()], includeProfile: false, mutatesProfile: false, busyLabel: N("Creating profile..."));
        if (result?.ExitCode == 0)
        {
            RememberLastProfilePath(ProfilePath);
            bool previousSuppress = suppressStageAutoRefresh;
            suppressStageAutoRefresh = true;
            try
            {
                CurrentStage = UiStage.Relations;
                await RefreshRelationsAsync();
                await RefreshBindsAndModifiersAfterChangeAsync();
            }
            finally
            {
                suppressStageAutoRefresh = previousSuppress;
            }
        }
    }

    [RelayCommand]
    private async Task OpenLastProfileAsync()
    {
        await OpenLastProfileInternalAsync(showMissingStatus: true);
    }

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        pendingModifiersByName.Clear();
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            StatusText = T("Set profile path to load.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["profile", "load", ProfilePath],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: N("Loading profile..."));

        if (result?.ExitCode == 0)
        {
            RememberLastProfilePath(ProfilePath);
            // Modifiers that were not on a bind yet (saved by the autosave) come back.
            RestoreProfileDrafts();
            RefreshHistoryState();
            bool previousSuppress = suppressStageAutoRefresh;
            suppressStageAutoRefresh = true;
            try
            {
                // The last profile opened at start leaves the Start page in front; opening a profile by hand shows it.
                if (!openingAtStartup)
                    CurrentStage = UiStage.Relations;
                await RefreshRelationsAsync();
                await RefreshBindsAndModifiersAfterChangeAsync();
                // Devices that came back with a new ID (new PC, Windows reinstall) get their binds, with a notice.
                HasDeviceRemapNotice = false;
                await RunDeviceAutoRemapAsync();
                // The device names of the profile (after the remap: an alias moves with its device).
                await LoadDeviceAliasesAsync();
            }
            finally
            {
                suppressStageAutoRefresh = previousSuppress;
            }
        }
    }

    [RelayCommand]
    private async Task ClearWorkspaceAsync()
    {
        // Pending edits of the profile are saved before it closes.
        await FlushAutosaveAsync();
        pendingModifiersByName.Clear();
        modifierReformerCacheByName.Clear();
        relationMassSelectedNames.Clear();
        allRelations.Clear();
        allBinds.Clear();
        allValidationIssues.Clear();
        DeviceAliases.Replace(new Dictionary<string, string>());
        editingRelationNode = null;

        SelectedRelation = null;
        SelectedBind = null;
        SelectedModifierItem = null;
        SelectedRelationNode = null;
        SelectedRelationNodeSearchResult = null;
        SelectedValidationIssue = null;

        Relations.Clear();
        RelationNodes.Clear();
        relationNodeSearchResultSource.Clear();
        RelationNodeSearchResults.Clear();
        Binds.Clear();
        Modifiers.Clear();
        ModifierNames.Clear();
        ModifierItems.Clear();
        SelectedModifierRelations.Clear();
        ValidationIssues.Clear();
        LayoutEditorMarkers.Clear();
        LayoutEditorTables.Clear();
        LayoutEditorConnectors.Clear();
        LayoutEditorMarkerTokenOptions.Clear();
        foreach ((SelectableTagItem option, PropertyChangedEventHandler handler) in layoutEditorModifierOptionHandlers)
            option.PropertyChanged -= handler;
        layoutEditorModifierOptionHandlers.Clear();
        LayoutEditorModifierOptions.Clear();
        SelectedLayoutEditorMarker = null;
        SelectedLayoutEditorTable = null;
        SelectedLayoutEditorConnector = null;
        layoutEditorConnectorSourcePoint = null;
        layoutEditorContextDevice = string.Empty;
        layoutTemplateByDevice.Clear();
        LayoutEditorSourcePath = string.Empty;
        LayoutEditorOutputPath = string.Empty;
        LayoutEditorNewToken = string.Empty;
        LayoutEditorNewType = "button";
        LayoutEditorNewModifier = string.Empty;
        LayoutEditorModifierPreview = "<none>";
        LayoutEditorNewTableName = "Primary Controls";
        LayoutEditorNewTableMode = "panel";
        LayoutEditorNewTableWidth = "360";
        LayoutEditorNewTableHeight = "220";
        LayoutEditorNewTableColumns = "1";
        LayoutEditorNewTableBackgroundColor = "#F6F8FB";
        LayoutEditorNewTableBackgroundOpacity = "0.93";
        LayoutEditorNewTableDropShadow = true;
        LayoutEditorNewConnectorName = "Connector";
        LayoutEditorNewConnectorColor = "#E2E8F0";
        LayoutEditorNewConnectorWidth = "2";
        LayoutEditorNewConnectorPoints = string.Empty;
        LayoutEditorNewConnectorRoute = "orthogonal";
        LayoutEditorNewConnectorSteps = "2";
        LayoutEditorNewConnectorTargetInput = string.Empty;
        LayoutEditorConnectorSourcePointLabel = "<none>";
        LayoutPlaceMarkerMode = true;
        LayoutPlaceTableMode = false;
        LayoutPlaceConnectorPointMode = false;
        LayoutPlaceConnectorSourceMode = false;
        LayoutEditorCanvasWidth = 1280;
        LayoutEditorCanvasHeight = 720;
        LayoutEditorZoom = 1.0;
        LayoutEditorStatus = T("Select a suggestion to load it into the embedded editor.");
        Bitmap? oldLayoutPreview = LayoutEditorPreviewImage;
        LayoutEditorPreviewImage = null;
        oldLayoutPreview?.Dispose();

        RelationSearchText = string.Empty;
        BindSearchText = string.Empty;
        BindDeviceFilterText = string.Empty;
        RelationNameInput = string.Empty;
        RelationFromInput = string.Empty;
        RelationGroupInput = string.Empty;
        RelationDeviceInput = string.Empty;
        RelationInputInput = string.Empty;
        RelationModifierInput = string.Empty;
        RelationNodeSearchText = string.Empty;
        RelationNodeSearchStatus = string.Empty;
        RelationNodeIdInput = string.Empty;
        RelationNodePlanesInput = string.Empty;
        RelationGroupAddCsv = string.Empty;
        RelationGroupRenameOld = string.Empty;
        RelationGroupRenameNew = string.Empty;
        ModifierCreateName = string.Empty;
        ModifierCreateDevice = string.Empty;
        ModifierCreateInput = string.Empty;
        ModifierRenameOld = string.Empty;
        ModifierRenameNew = string.Empty;
        ModifierCreateTargetRelation = string.Empty;
        RecordStatusText = T("Idle");
        LastCommandText = string.Empty;
        LastRunDuration = "n/a";
        LastExitCode = "n/a";
        ValidationSummary = T("Not run yet.");
        ConflictSummary = T("No conflict scan executed yet.");
        LayoutDevicesCsv = string.Empty;
        LayoutGameCsv = string.Empty;
        LayoutMapInput = string.Empty;
        LayoutSelectAllDevices = false;
        LayoutSelectNoneDevices = false;
        LayoutSelectAllGames = false;
        LayoutSelectNoneGames = false;

        ProfilePath = string.Empty;
        CurrentStage = UiStage.Start;
        OutputText = T("Workspace cleared.\nPick New/Open/Import to start.");
        StatusText = T("Workspace cleared. Use New, Open, or Import to continue.");

        RebuildGroupFilters(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        RebuildVisibleRelations();
        RebuildVisibleBinds();
        RebuildQuickBindOptions();
        RebuildModifierTargetRelationOptions();
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        RebuildLayoutGameOptions(BuildLayoutGameCandidates());
        UpdateRelationMassSelectionSummary();
        RefreshSelectedModifierRelations();
        RefreshRelationValidationState();
        RefreshModifierValidationState();
    }

    private async Task OpenLastProfileInternalAsync(bool showMissingStatus)
    {
        string candidate = (LastProfilePath ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            if (showMissingStatus)
                StatusText = T("No last profile saved yet.");
            return;
        }

        if (!File.Exists(candidate))
        {
            if (showMissingStatus)
                StatusText = T("Last profile not found on disk: {0}", candidate);
            return;
        }

        // The open profile's pending edits are saved before another one opens.
        await FlushAutosaveAsync();
        ProfilePath = candidate;
        await LoadProfileAsync();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            StatusText = T("Set profile path before saving.");
            return;
        }

        // JoyPro saves by itself; Save (Ctrl+S) saves the pending edits at once and writes the file again.
        await FlushAutosaveAsync();
        await RunEngineAsync(["profile", "save", ProfilePath], includeProfile: true, mutatesProfile: true, busyLabel: N("Saving profile..."));
    }

    [RelayCommand]
    private async Task SaveLastProfileAsync()
    {
        await RunEngineAsync(["profile", "save-last"], includeProfile: true, mutatesProfile: true, busyLabel: N("Saving profile..."));
    }

    [RelayCommand]
    private async Task MergeProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            StatusText = T("Set left profile path.");
            return;
        }
        if (string.IsNullOrWhiteSpace(MergeRightProfilePath))
        {
            StatusText = T("Set right profile path.");
            return;
        }

        string mode = string.IsNullOrWhiteSpace(MergeMode) ? "right" : MergeMode.Trim().ToLowerInvariant();
        if (!await ConfirmAsync(T("Merge {0} into your profile?", Path.GetFileName(MergeRightProfilePath.Trim())),
                T("Its relations, binds and modifiers are added to {0}. {1}A copy of your profile is kept next to it first.", ProfileDisplayName, (mode == "left" ? "Where both have the same relation, yours stays. " : "Where both have the same relation, the added profile's is taken. ")), T("Merge")))
            return;
        string? profileCopy = BackupProfileBeforeChange("before-merge");
        List<string> args =
        [
            "profile",
            "merge",
            ProfilePath.Trim(),
            MergeRightProfilePath.Trim(),
            "--mode",
            mode,
            "--save",
            ProfilePath.Trim()
        ];

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Merging profiles..."));
        if (result?.ExitCode == 0)
        {
            if (profileCopy != null)
                StatusText = T("Profiles merged. Your profile before the merge: {0}", profileCopy);
            await RefreshRelationsAsync();
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task ImportDcsAsync()
    {
        List<string> selectedPlanes = BuildEffectiveDcsPlaneFilter();
        if (!ImportAllDcsPlanes && selectedPlanes.Count == 0)
        {
            StatusText = T("Select at least one DCS aircraft or provide Additional planes CSV.");
            return;
        }

        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["dcs", "import"];
        if (!string.IsNullOrWhiteSpace(ImportDcsPath))
            args.AddRange(["--path", ImportDcsPath.Trim()]);
        if (ImportMerge)
            args.Add("--merge");
        if (ImportIncludeDefaults)
            args.Add("--include-defaults");

        foreach (string plane in selectedPlanes)
            args.AddRange(["--plane", plane]);
        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);
        args.AddRange(["--include-keyboard", ImportKeyboardCommands ? "true" : "false"]);
        args.AddRange(["--ignore-easy-planes", ImportIgnoreEasyPlanes ? "true" : "false"]);

        if (ImportInverted)
            args.Add("--import-inverted");
        if (ImportSlider)
            args.Add("--import-slider");
        if (ImportCurvature)
            args.Add("--import-curvature");
        if (ImportDeadzone)
            args.Add("--import-deadzone");
        if (ImportSaturationX)
            args.Add("--import-saturation-x");
        if (ImportSaturationY)
            args.Add("--import-saturation-y");

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Importing from DCS..."));
        if (result?.ExitCode == 0)
        {
            bool previousSuppress = suppressStageAutoRefresh;
            suppressStageAutoRefresh = true;
            try
            {
                CurrentStage = UiStage.Relations;
                await RefreshRelationsAsync();
                await RefreshBindsAndModifiersAfterChangeAsync();
            }
            finally
            {
                suppressStageAutoRefresh = previousSuppress;
            }
        }
    }

    [RelayCommand]
    private async Task ImportIl2Async()
    {
        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["il2", "import"];
        if (!string.IsNullOrWhiteSpace(ImportIl2Path))
            args.AddRange(["--path", ImportIl2Path.Trim()]);
        if (ImportMerge)
            args.Add("--merge");

        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);

        if (ImportInverted)
            args.Add("--import-inverted");
        if (ImportCurvature)
            args.Add("--import-curvature");
        if (ImportDeadzone)
            args.Add("--import-deadzone");

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Importing from IL-2..."));
        if (result?.ExitCode == 0)
        {
            bool previousSuppress = suppressStageAutoRefresh;
            suppressStageAutoRefresh = true;
            try
            {
                CurrentStage = UiStage.Relations;
                await RefreshRelationsAsync();
                await RefreshBindsAndModifiersAfterChangeAsync();
            }
            finally
            {
                suppressStageAutoRefresh = previousSuppress;
            }
        }
    }

    [RelayCommand]
    private async Task ImportStarCitizenAsync()
    {
        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["starcitizen", "import"];
        if (!string.IsNullOrWhiteSpace(ImportStarCitizenPath))
            args.AddRange(["--path", ImportStarCitizenPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath))
            args.AddRange(["--source", StarCitizenSourcePath.Trim()]);
        if (ImportMerge)
            args.Add("--merge");

        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);
        args.AddRange(["--include-keyboard", ImportStarCitizenIncludeKeyboard ? "true" : "false"]);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Importing from Star Citizen..."));
        await AfterCommandListBuiltAsync(result, "StarCitizen");
        if (result?.ExitCode == 0)
        {
            bool previousSuppress = suppressStageAutoRefresh;
            suppressStageAutoRefresh = true;
            try
            {
                CurrentStage = UiStage.Relations;
                await RefreshRelationsAsync();
                await RefreshBindsAndModifiersAfterChangeAsync();
            }
            finally
            {
                suppressStageAutoRefresh = previousSuppress;
            }
        }
    }

    [RelayCommand]
    private async Task ScanDcsAsync()
    {
        List<string> selectedPlanes = BuildEffectiveDcsPlaneFilter();
        if (!ImportAllDcsPlanes && selectedPlanes.Count == 0)
        {
            StatusText = T("Select at least one DCS aircraft or provide Additional planes CSV.");
            return;
        }

        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["dcs", "scan"];
        if (!string.IsNullOrWhiteSpace(ImportDcsPath))
            args.AddRange(["--path", ImportDcsPath.Trim()]);
        if (ImportIncludeDefaults)
            args.Add("--include-defaults");
        foreach (string plane in selectedPlanes)
            args.AddRange(["--plane", plane]);
        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);
        args.AddRange(["--include-keyboard", ImportKeyboardCommands ? "true" : "false"]);
        args.AddRange(["--ignore-easy-planes", ImportIgnoreEasyPlanes ? "true" : "false"]);

        await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: N("Scanning DCS..."));
    }

    [RelayCommand]
    private async Task ScanIl2Async()
    {
        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["il2", "scan"];
        if (!string.IsNullOrWhiteSpace(ImportIl2Path))
            args.AddRange(["--path", ImportIl2Path.Trim()]);
        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);
        await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: N("Scanning IL-2..."));
    }

    [RelayCommand]
    private async Task ScanStarCitizenAsync()
    {
        List<string> selectedDevices = BuildEffectiveImportDeviceFilter();
        List<string> ignoredDevices = BuildEffectiveImportIgnoredDeviceFilter();
        if (ImportUseSelectedDevices && selectedDevices.Count == 0)
        {
            StatusText = T("Select at least one device or disable 'Use selected devices only'.");
            return;
        }

        List<string> args = ["starcitizen", "scan"];
        if (!string.IsNullOrWhiteSpace(ImportStarCitizenPath))
            args.AddRange(["--path", ImportStarCitizenPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath))
            args.AddRange(["--source", StarCitizenSourcePath.Trim()]);
        foreach (string device in selectedDevices)
            args.AddRange(["--device", device]);
        foreach (string ignored in ignoredDevices)
            args.AddRange(["--ignore-device", ignored]);
        args.AddRange(["--include-keyboard", ImportStarCitizenIncludeKeyboard ? "true" : "false"]);

        await RunEngineAsync(args, includeProfile: HasProfile, mutatesProfile: false, busyLabel: N("Scanning Star Citizen..."));
    }

    [RelayCommand]
    private async Task RefreshRelationsAsync()
    {
        // JSON: every action's game and switched-on aircraft (the filters) and the bind's modifiers (the bind label).
        EngineCommandResult? result = await RunEngineAsync(["relations", "list"], includeProfile: true, mutatesProfile: false, busyLabel: N("Loading relations..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        ParseRelations(result.StandardOutput);
    }

    [RelayCommand]
    private async Task ShowSelectedRelationAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }

        await ShowRelationAsync(SelectedRelation.Name);
    }

    internal async Task ShowRelationAsync(string relationName)
    {
        if (string.IsNullOrWhiteSpace(relationName))
            return;

        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "show", relationName],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Loading relation details..."));

        if (result == null || result.ExitCode != 0)
            return;

        ParseRelationNodes(result.StandardOutput);
        // Each action's aircraft with their own meaning of the id (MainWindowViewModel.RelationAircraft.cs).
        _ = LoadRelationNodeAircraftAsync(relationName);
        await RefreshRelationNodePlaneOptionsAsync();
    }

    [RelayCommand]
    private async Task AddRelationAsync()
    {
        if (!CanCreateRelation)
        {
            StatusText = string.IsNullOrWhiteSpace(RelationCreateValidationMessage)
                ? T("Relation name is required.")
                : RelationCreateValidationMessage;
            return;
        }

        string createdRelationName = RelationNameInput.Trim();
        List<string> args = ["relations", "add", createdRelationName];
        string selectedPlanesCsv = BuildSelectedRelationNodePlanesCsv();
        string nodeIdForAdd = (SelectedRelationNodeSearchResult?.NodeId ?? string.Empty).Trim();
        string nodeGameForAdd = NormalizeRelationGameToken(
            SelectedRelationNodeSearchResult?.Game ?? RelationNodeGameInput);
        bool hasFromRelation = !string.IsNullOrWhiteSpace(RelationFromInput);
        bool hasNode = !string.IsNullOrWhiteSpace(nodeIdForAdd);
        if (!hasFromRelation && !hasNode)
        {
            StatusText = T("Select a DB node or set 'Copy from relation' before adding.");
            WarningRequested?.Invoke(StatusText);
            return;
        }

        if (!string.IsNullOrWhiteSpace(nodeIdForAdd))
            args.AddRange(["--node", BuildNodeSpec(nodeGameForAdd, nodeIdForAdd, selectedPlanesCsv)]);
        if (!string.IsNullOrWhiteSpace(RelationFromInput))
            args.AddRange(["--from", RelationFromInput.Trim()]);
        if (!string.IsNullOrWhiteSpace(RelationDeviceInput))
            args.AddRange(["--device", RelationDeviceInput.Trim()]);
        if (!string.IsNullOrWhiteSpace(RelationInputInput))
            args.AddRange(["--input", RelationInputInput.Trim()]);
        if (!string.IsNullOrWhiteSpace(RelationModifierInput))
            args.AddRange(["--modifier", RelationModifierInput.Trim()]);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Adding relation..."));
        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            SelectRelationByName(createdRelationName);
            await RefreshBindsAsync();
        }
    }

    // Duplicates the open relation ("<name> (copy)") with its actions, aircraft, groups, tuning and bind, and opens the copy.
    // One engine call, so one undo step.
    [RelayCommand]
    private Task DuplicateSelectedRelationAsync() => DuplicateRelationCoreAsync(SelectedRelation, withBind: true);

    // The same without the bind: the same actions for another input (the copy is not bound).
    [RelayCommand]
    private Task DuplicateSelectedRelationWithoutBindAsync() => DuplicateRelationCoreAsync(SelectedRelation, withBind: false);

    // The relation card's context menu: the card that was right-clicked.
    [RelayCommand]
    private Task DuplicateRelationAsync(RelationListItem? relation) => DuplicateRelationCoreAsync(relation ?? SelectedRelation, withBind: true);

    [RelayCommand]
    private Task DuplicateRelationWithoutBindAsync(RelationListItem? relation) => DuplicateRelationCoreAsync(relation ?? SelectedRelation, withBind: false);

    [RelayCommand]
    private async Task DeleteRelationAsync(RelationListItem? relation)
    {
        if (relation != null && !ReferenceEquals(relation, SelectedRelation))
            SelectedRelation = relation;
        await DeleteSelectedRelationAsync();
    }

    private async Task DuplicateRelationCoreAsync(RelationListItem? relation, bool withBind)
    {
        if (relation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }

        HashSet<string> before = new(allRelations.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        List<string> args = ["relations", "mass", "duplicate", "--relation", relation.Name];
        if (!withBind)
            args.Add("--no-bind");
        EngineCommandResult? result = await RunEngineAsync(
            args,
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: withBind ? N("Duplicating relation...") : N("Duplicating relation without its bind..."));

        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
            string? created = allRelations.Select(r => r.Name).FirstOrDefault(name => !before.Contains(name));
            if (created != null)
            {
                SelectRelationByName(created);
                StatusText = withBind
                    ? T("Duplicated as \"{0}\". Ctrl+Z undoes it.", created)
                    : T("Duplicated as \"{0}\" without the bind: bind it on the Binds page. Ctrl+Z undoes it.", created);
            }
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedRelationAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }
        if (!await ConfirmAsync(T("Delete the relation \"{0}\"?", SelectedRelation.Name),
                T("The relation and its bind leave the profile. Undo (Ctrl+Z) brings it back."), T("Delete"), isDanger: true))
            return;

        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "mass", "delete", "--relation", SelectedRelation.Name],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Deleting relation..."));

        if (result?.ExitCode == 0)
        {
            RelationNodes.Clear();
            await RefreshRelationsAsync();
            await RefreshAndKeepModifiersAsync(modifiersBefore);
        }
    }

    [RelayCommand]
    private async Task AddNodeToSelectedRelationAsync()
    {
        if (SelectedRelation == null)
        {
            if (!string.IsNullOrWhiteSpace(RelationNameInput))
            {
                StatusText = T("No relation selected. Creating new relation from draft with selected DB node...");
                await AddRelationAsync();
                return;
            }

            StatusText = T("Select a relation first, or enter a new relation name and click Add.");
            WarningRequested?.Invoke(StatusText);
            return;
        }
        if (SelectedRelationNodeSearchResult == null ||
            string.IsNullOrWhiteSpace(SelectedRelationNodeSearchResult.NodeId))
        {
            StatusText = T("Search and select a DB node result first.");
            WarningRequested?.Invoke(StatusText);
            return;
        }

        string nodeId = SelectedRelationNodeSearchResult.NodeId.Trim();
        string nodeGame = NormalizeRelationGameToken(SelectedRelationNodeSearchResult.Game);
        string nodeSpec = BuildNodeSpec(nodeGame, nodeId, BuildSelectedRelationNodePlanesCsv());
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "item", "add", SelectedRelation.Name, "--node", nodeSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Adding relation node..."));

        if (result?.ExitCode == 0)
            await ShowRelationAsync(SelectedRelation.Name);
    }

    [RelayCommand]
    private async Task RemoveSelectedNodeAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }
        if (SelectedRelationNode == null)
        {
            StatusText = T("Select a node first.");
            return;
        }

        string nodeSpec = BuildNodeSpec(SelectedRelationNode.Game, SelectedRelationNode.Id, SelectedRelationNode.ActivePlanesText);
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "item", "remove", SelectedRelation.Name, "--node", nodeSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Removing relation node..."));

        if (result?.ExitCode == 0)
            await ShowRelationAsync(SelectedRelation.Name);
    }

    [RelayCommand]
    private async Task RemoveNodeAsync(RelationNodeItem? node)
    {
        if (node == null)
            return;

        // The x of an action asks first ("Do not ask again" switches it off; Settings > Confirmations on again).
        string relation = SelectedRelation?.Name ?? string.Empty;
        string what = node.TitlesSummary.Length > 0 && !node.TitlesSummary.StartsWith('<') ? node.TitlesSummary : node.Id;
        string where = DisplayText.Game(node.Game) + (node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase) && node.PlanesSummary.Length > 0 && !node.PlanesSummary.StartsWith('<')
            ? ": " + node.PlanesSummary : string.Empty);
        if (!await ConfirmAsync(T("Remove this action?"),
                T("'{0}' ({1}) leaves the relation '{2}'. Its bind stays with the relation. Ctrl+Z undoes it.", what, where, relation),
                T("Remove"), isDanger: false, dontAskKey: ConfirmRemoveActionKey))
            return;

        SelectedRelationNode = node;
        await RemoveSelectedNodeAsync();
    }

    [RelayCommand]
    private async Task EditNodeAsync(RelationNodeItem? node)
    {
        if (node == null)
        {
            StatusText = T("Select a node first.");
            return;
        }

        bool sameNodeSelected = SelectedRelationNode != null &&
                                string.Equals(SelectedRelationNode.Id, node.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(SelectedRelationNode.Game, node.Game, StringComparison.OrdinalIgnoreCase);
        bool canApplyPlaneUpdate = sameNodeSelected &&
                                   editingRelationNode != null &&
                                   string.Equals(editingRelationNode.Id, node.Id, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(editingRelationNode.Game, node.Game, StringComparison.OrdinalIgnoreCase);

        if (canApplyPlaneUpdate)
        {
            string currentPlanes = NormalizeNodePlaneCsv(node.ActivePlanesText);
            string selectedPlanes = NormalizeNodePlaneCsv(BuildSelectedRelationNodePlanesCsv());
            if (!string.Equals(currentPlanes, selectedPlanes, StringComparison.OrdinalIgnoreCase))
            {
                await ApplyNodeEditAsync();
                return;
            }
        }

        await LoadNodeEditorStateAsync(node);
    }

    [RelayCommand]
    private void ClearRelationSelection()
    {
        SelectedRelation = null;
        SelectedRelationNode = null;
        editingRelationNode = null;
        RelationNodes.Clear();
        SetRelationMassSelection(Array.Empty<RelationListItem>());
        StatusText = T("Relation selection cleared.");
    }

    [RelayCommand]
    private async Task ClearNodeSelectionAsync()
    {
        SelectedRelationNode = null;
        SelectedRelationNodeSearchResult = null;
        editingRelationNode = null;
        RelationNodeIdInput = string.Empty;
        await RefreshRelationNodePlaneOptionsAsync();
        ApplyRelationNodePlaneFilterToSearchResults();
        StatusText = T("Node/search selection cleared.");
    }

    private async Task LoadNodeEditorStateAsync(RelationNodeItem node)
    {
        if (node == null)
            return;

        editingRelationNode = node;
        SelectedRelationNodeSearchResult = null;
        RelationNodeGameInput = NormalizeRelationGameToken(node.Game);
        RelationNodeIdInput = node.Id;

        List<string> selectedPlanes = ParseCsv(node.ActivePlanesText);
        await RefreshRelationNodePlaneOptionsAsync(selectedPlanes);
        RelationNodePlanesInput = BuildSelectedRelationNodePlanesCsv();
        StatusText = T("Node loaded. Search/select a new DB node and click Replace Selected Node, or keep the current one.");
    }

    [RelayCommand]
    private async Task ApplyNodeEditAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }
        if (editingRelationNode == null)
        {
            StatusText = T("Use Edit on a node row first.");
            return;
        }
        string nodeId = (SelectedRelationNodeSearchResult?.NodeId ?? RelationNodeIdInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            StatusText = T("Search and select a DB node result first.");
            return;
        }

        string oldPlanes = editingRelationNode.ActivePlanesText.Equals("<none>", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : editingRelationNode.ActivePlanesText;
        string oldSpec = BuildNodeSpec(editingRelationNode.Game, editingRelationNode.Id, oldPlanes);
        string nodeGame = NormalizeRelationGameToken(
            SelectedRelationNodeSearchResult?.Game ?? RelationNodeGameInput);
        string newSpec = BuildNodeSpec(nodeGame, nodeId, NormalizeNodePlaneCsv(BuildSelectedRelationNodePlanesCsv()));

        if (string.Equals(oldSpec, newSpec, StringComparison.Ordinal))
        {
            editingRelationNode = null;
            StatusText = T("Node unchanged.");
            return;
        }

        EngineCommandResult? removeResult = await RunEngineAsync(
            ["relations", "item", "remove", SelectedRelation.Name, "--node", oldSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Removing old node..."));
        if (removeResult?.ExitCode != 0)
            return;

        EngineCommandResult? addResult = await RunEngineAsync(
            ["relations", "item", "add", SelectedRelation.Name, "--node", newSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Adding edited node..."));
        if (addResult?.ExitCode != 0)
            return;

        editingRelationNode = null;
        await ShowRelationAsync(SelectedRelation.Name);
        StatusText = T("Node updated.");
    }

    [RelayCommand]
    private async Task DisableSelectedNodeAsync()
    {
        if (SelectedRelation == null || SelectedRelationNode == null)
        {
            StatusText = T("Select a relation and node first.");
            return;
        }

        string nodeSpec = BuildNodeSpec(SelectedRelationNode.Game, SelectedRelationNode.Id, SelectedRelationNode.ActivePlanesText);
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "item", "disable", SelectedRelation.Name, "--node", nodeSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Disabling node..."));

        if (result?.ExitCode == 0)
            await ShowRelationAsync(SelectedRelation.Name);
    }

    [RelayCommand]
    private async Task EnableSelectedNodeAsync()
    {
        if (SelectedRelation == null || SelectedRelationNode == null)
        {
            StatusText = T("Select a relation and node first.");
            return;
        }

        string nodeSpec = BuildNodeSpec(SelectedRelationNode.Game, SelectedRelationNode.Id, SelectedRelationNode.ActivePlanesText);
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "item", "enable", SelectedRelation.Name, "--node", nodeSpec],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Enabling node..."));

        if (result?.ExitCode == 0)
            await ShowRelationAsync(SelectedRelation.Name);
    }

    [RelayCommand]
    private async Task AddRelationGroupsAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }
        List<string> groups = ParseCsv(RelationGroupAddCsv);
        if (groups.Count == 0)
        {
            StatusText = T("Enter at least one group.");
            return;
        }

        List<string> args = ["relations", "group", "add", SelectedRelation.Name];
        args.AddRange(groups);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Adding relation groups..."));
        if (result?.ExitCode == 0)
            await RefreshRelationsAsync();
    }

    [RelayCommand]
    private async Task RemoveRelationGroupsAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }

        List<string> args = ["relations", "group", "remove", SelectedRelation.Name];
        List<string> groups = RelationGroupRemoveCandidates
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
        {
            StatusText = T("Select one or more groups to remove.");
            return;
        }
        args.AddRange(groups);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Removing relation groups..."));
        if (result?.ExitCode == 0)
            await RefreshRelationsAsync();
    }

    [RelayCommand]
    private async Task RemoveAllRelationGroupsAsync()
    {
        if (SelectedRelation == null)
        {
            StatusText = T("Select a relation first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "group", "remove", SelectedRelation.Name, "--all"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Removing all relation groups..."));
        if (result?.ExitCode == 0)
            await RefreshRelationsAsync();
    }

    [RelayCommand]
    private void ClearGroupRemoveSelection()
    {
        foreach (SelectableTagItem item in RelationGroupRemoveCandidates)
            item.IsSelected = false;
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(RelationGroupRenameOld) || string.IsNullOrWhiteSpace(RelationGroupRenameNew))
        {
            StatusText = T("Old and new group names are required.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "group", "rename", RelationGroupRenameOld.Trim(), RelationGroupRenameNew.Trim()],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Renaming group..."));
        if (result?.ExitCode == 0)
            await RefreshRelationsAsync();
    }

    [RelayCommand]
    private async Task ListSelectedRelationGroupsAsync()
    {
        List<string> args = ["relations", "group", "list"];
        if (SelectedRelation != null)
            args.Add(SelectedRelation.Name);
        await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Listing relation groups..."));
    }

    [RelayCommand]
    private async Task RunRelationMassOperationAsync()
    {
        string op = (RelationMassOperation ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(op))
        {
            StatusText = T("Pick a mass operation.");
            return;
        }

        List<string> args = ["relations", "mass", op];
        if (op is "merge" or "split" or "add-prefix" or "add-postfix" or "remove-part" or "export")
        {
            if (string.IsNullOrWhiteSpace(RelationMassValue))
            {
                StatusText = T("This mass operation requires a value.");
                return;
            }
            args.Add(RelationMassValue.Trim());
        }

        if (op == "split")
        {
            List<string> splitPlanes = ParseCsv(RelationMassPlanesCsv);
            if (splitPlanes.Count == 0)
            {
                StatusText = T("Split needs the planes that move into the new relation (Planes field).");
                return;
            }
            args.AddRange(["--planes", string.Join(",", splitPlanes)]);
        }

        string target = (RelationMassTarget ?? string.Empty).Trim().ToLowerInvariant();
        switch (target)
        {
            case "all":
                args.Add("--all");
                break;
            case "query":
                if (string.IsNullOrWhiteSpace(RelationMassQuery))
                {
                    StatusText = T("Query target requires a query text.");
                    return;
                }
                args.AddRange(["--query", RelationMassQuery.Trim()]);
                break;
            default:
                List<string> selectedNames = relationMassSelectedNames
                    .Where(name => allRelations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (selectedNames.Count == 0 && SelectedRelation != null)
                    selectedNames.Add(SelectedRelation.Name);

                if (selectedNames.Count == 0)
                {
                    StatusText = T("Select one or more relations for mass operation, or switch target to all/query.");
                    return;
                }

                if (selectedNames.Count == 1)
                    args.AddRange(["--relation", selectedNames[0]]);
                else
                    args.AddRange(["--relations", string.Join(",", selectedNames)]);
                break;
        }

        // Never add --query to another target: the engine unions it with --relation(s), so a leftover
        // query text would widen "selected" to every matching relation (e.g. for delete).
        if (op == "delete" && !await ConfirmAsync(T("Delete {0}?", (target == "all" ? "all relations" : target == "query" ? "the matching relations" : "the selected relations")),
                T("The relations and their binds leave the profile. Undo (Ctrl+Z) brings them back."), T("Delete"), isDanger: true))
            return;
        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Running mass operation..."));
        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            if (op is "delete" or "merge")
                await RefreshAndKeepModifiersAsync(modifiersBefore);
            else
                await RefreshBindsAsync();
        }
    }

    [RelayCommand]
    private async Task ImportRelationsAsync()
    {
        if (string.IsNullOrWhiteSpace(RelationImportPath))
        {
            StatusText = T("Set import path first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "import", RelationImportPath.Trim()],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Importing relations..."));

        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            await RefreshBindsAsync();
        }
    }

    [RelayCommand]
    private async Task ExportRelationsAsync()
    {
        if (string.IsNullOrWhiteSpace(RelationExportPath))
        {
            StatusText = T("Set export path first.");
            return;
        }

        await RunEngineAsync(
            ["relations", "export", RelationExportPath.Trim()],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Exporting relations..."));
    }

    private async Task RefreshBindsPageAsync()
    {
        await RefreshRelationsAsync();
        await RefreshBindsAsync();
    }

    [RelayCommand]
    private async Task RefreshBindsAsync()
    {
        if (allRelations.Count == 0)
            await RefreshRelationsAsync();

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "list"],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Loading binds..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        ParseBinds(result.StandardOutput);
        OnBindsReloadedForPictures();
    }

    [RelayCommand]
    private async Task ShowSelectedBindAsync()
    {
        if (SelectedBind == null)
        {
            StatusText = T("Select a bind first.");
            return;
        }

        if (SelectedBind.HasBind)
            await ShowBindAsync(SelectedBind.RelationName);
        else
            await ShowRelationAsync(SelectedBind.RelationName);
    }

    private async Task ShowBindAsync(string bindOrRelation)
    {
        if (string.IsNullOrWhiteSpace(bindOrRelation))
            return;
        await RunEngineAsync(["binds", "show", bindOrRelation], includeProfile: true, mutatesProfile: false, busyLabel: N("Loading bind details..."));
    }

    private void ParseRelations(string output)
    {
        allRelations.Clear();
        HashSet<string> groups = new(StringComparer.OrdinalIgnoreCase);

        if (TryParseRelationsFromJson(output, out List<RelationListItem> jsonRelations))
        {
            for (int i = 0; i < jsonRelations.Count; i++)
            {
                RelationListItem item = jsonRelations[i];
                allRelations.Add(item);
                foreach (string group in item.Groups.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    groups.Add(group.Trim());
            }

            RebuildGroupFilters(groups);
            RebuildVisibleRelations();
            RebuildQuickBindOptions();
            RebuildModifierTargetRelationOptions();
            relationMassSelectedNames.RemoveWhere(name => !allRelations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
            UpdateRelationMassSelectionSummary();
            UpdateRecordTargetContext();
            RefreshRelationValidationState();
            // The Binds page lists every relation (bound or not): keep it in step with the relation list.
            RebuildVisibleBinds();
            // The aircraft chips' tooltips name the aircraft as the modules do.
            _ = LoadRelationAircraftNamesAsync();
            return;
        }

        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("Relations:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length == 0)
                continue;

            if (!TryParseRelationTextLine(line, out RelationListItem item))
                continue;

            allRelations.Add(item);

            foreach (string group in item.Groups.Split(',', StringSplitOptions.RemoveEmptyEntries))
                groups.Add(group.Trim());
        }

        RebuildGroupFilters(groups);
        RebuildVisibleRelations();
        RebuildQuickBindOptions();
        RebuildModifierTargetRelationOptions();
        relationMassSelectedNames.RemoveWhere(name => !allRelations.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
        UpdateRelationMassSelectionSummary();
        UpdateRecordTargetContext();
        RefreshRelationValidationState();
        RebuildVisibleBinds();
    }

    private static bool TryParseRelationTextLine(string line, out RelationListItem item)
    {
        item = new RelationListItem();
        if (string.IsNullOrWhiteSpace(line))
            return false;

        Match strict = RelationListLine.Match(line.TrimEnd());
        if (strict.Success)
        {
            item = new RelationListItem
            {
                Name = strict.Groups["name"].Value.Trim(),
                Type = strict.Groups["type"].Value.Trim(),
                Bind = strict.Groups["bind"].Value.Trim(),
                Games = strict.Groups["games"].Value.Trim(),
                Planes = strict.Groups["planes"].Value.Trim(),
                Groups = strict.Groups["groups"].Value.Trim()
            };
            return !string.IsNullOrWhiteSpace(item.Name);
        }

        string[] segments = line.Split('|');
        if (segments.Length < 2)
            return false;

        string name = segments[0].Trim();
        if (name.Length == 0)
            return false;

        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < segments.Length; i++)
        {
            string segment = segments[i].Trim();
            if (segment.Length == 0)
                continue;

            int eq = segment.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = segment[..eq].Trim();
            string value = segment[(eq + 1)..].Trim();
            if (key.Length == 0)
                continue;
            fields[key] = value;
        }

        if (!fields.ContainsKey("type") &&
            !fields.ContainsKey("bind") &&
            !fields.ContainsKey("games") &&
            !fields.ContainsKey("planes") &&
            !fields.ContainsKey("groups"))
        {
            return false;
        }

        item = new RelationListItem
        {
            Name = name,
            Type = fields.TryGetValue("type", out string? type) && !string.IsNullOrWhiteSpace(type)
                ? type.Trim()
                : "button",
            Bind = fields.TryGetValue("bind", out string? bind) && !string.IsNullOrWhiteSpace(bind)
                ? bind.Trim()
                : "unbound",
            Games = fields.TryGetValue("games", out string? games) && !string.IsNullOrWhiteSpace(games)
                ? games.Trim()
                : "<none>",
            Planes = fields.TryGetValue("planes", out string? planes) && !string.IsNullOrWhiteSpace(planes)
                ? planes.Trim()
                : "0",
            Groups = fields.TryGetValue("groups", out string? groups) && !string.IsNullOrWhiteSpace(groups)
                ? groups.Trim()
                : "<none>"
        };
        return true;
    }

    private bool TryParseRelationsFromJson(string output, out List<RelationListItem> parsedRelations)
    {
        parsedRelations = [];
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Relations", out JsonElement relationsEl) ||
                relationsEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement relationEl in relationsEl.EnumerateArray())
            {
                if (relationEl.ValueKind != JsonValueKind.Object)
                    continue;

                string name = GetJsonString(relationEl, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                bool isAxis = relationEl.TryGetProperty("IsAxis", out JsonElement axisEl) &&
                              axisEl.ValueKind == JsonValueKind.True;

                List<string> groups = [];
                if (relationEl.TryGetProperty("Groups", out JsonElement groupsEl) &&
                    groupsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement groupEl in groupsEl.EnumerateArray())
                    {
                        if (groupEl.ValueKind != JsonValueKind.String)
                            continue;
                        string group = (groupEl.GetString() ?? string.Empty).Trim();
                        if (group.Length > 0)
                            groups.Add(group);
                    }
                }

                HashSet<string> games = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> planes = new(StringComparer.OrdinalIgnoreCase);
                // The (game, aircraft) of the switched-on actions, for the filters ("DCS|A-10C", "IL2Game|IL2Game").
                HashSet<string> activeGamePlanes = new(StringComparer.OrdinalIgnoreCase);
                if (relationEl.TryGetProperty("Nodes", out JsonElement nodesEl) &&
                    nodesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement nodeEl in nodesEl.EnumerateArray())
                    {
                        if (nodeEl.ValueKind != JsonValueKind.Object)
                            continue;

                        string game = GetJsonString(nodeEl, "Game");
                        if (string.IsNullOrWhiteSpace(game))
                            game = "DCS";
                        games.Add(game.Trim());

                        bool isDcsNode = game.Trim().Equals("DCS", StringComparison.OrdinalIgnoreCase);
                        if (!nodeEl.TryGetProperty("Aircraft", out JsonElement aircraftEl))
                        {
                            if (!isDcsNode)
                                activeGamePlanes.Add(game.Trim() + "|" + game.Trim());
                            continue;
                        }

                        if (aircraftEl.ValueKind == JsonValueKind.Object)
                        {
                            bool any = false;
                            bool none = true;
                            foreach (JsonProperty planeProp in aircraftEl.EnumerateObject())
                            {
                                none = false;
                                if (planeProp.Name.Length == 0 || !JsonElementTruthy(planeProp.Value))
                                    continue;
                                // A game's own id (MSFS2024: "MSFS2024" = all aircraft) is no aircraft: an imported MSFS relation
                                // showed "1 aircraft" although it applies to every aircraft.
                                if (isDcsNode || !IsAllAircraftScope(game.Trim(), planeProp.Name.Trim()))
                                    planes.Add(planeProp.Name.Trim());
                                any = true;
                                if (isDcsNode)
                                    activeGamePlanes.Add("DCS|" + planeProp.Name.Trim());
                            }
                            if (!isDcsNode && (any || none))
                                activeGamePlanes.Add(game.Trim() + "|" + game.Trim());
                        }
                        else if (aircraftEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement planeEl in aircraftEl.EnumerateArray())
                            {
                                if (planeEl.ValueKind != JsonValueKind.String)
                                    continue;
                                string plane = (planeEl.GetString() ?? string.Empty).Trim();
                                if (plane.Length > 0)
                                {
                                    if (isDcsNode || !IsAllAircraftScope(game.Trim(), plane))
                                        planes.Add(plane);
                                    activeGamePlanes.Add(isDcsNode ? "DCS|" + plane : game.Trim() + "|" + game.Trim());
                                }
                            }
                        }
                    }
                }

                string bindLabel = "unbound";
                List<string> bindModifiers = [];
                if (relationEl.TryGetProperty("Bind", out JsonElement bindEl) &&
                    bindEl.ValueKind == JsonValueKind.Object)
                {
                    if (bindEl.TryGetProperty("AllReformers", out JsonElement reformersEl) && reformersEl.ValueKind == JsonValueKind.Array)
                    {
                        bindModifiers = reformersEl.EnumerateArray()
                            .Where(r => r.ValueKind == JsonValueKind.String)
                            .Select(r => ModifierNameFromReformer(r.GetString() ?? string.Empty))
                            .Where(n => n.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    string device = GetJsonString(bindEl, "Joystick");
                    string input = GetJsonString(bindEl, "JButton");
                    if (string.IsNullOrWhiteSpace(input))
                        input = GetJsonString(bindEl, "JAxis");
                    if (!string.IsNullOrWhiteSpace(device) && !string.IsNullOrWhiteSpace(input))
                        bindLabel = device.Trim() + "::" + input.Trim();
                }

                parsedRelations.Add(new RelationListItem
                {
                    Name = name.Trim(),
                    Type = isAxis ? "axis" : "button",
                    Bind = bindLabel,
                    Games = games.Count == 0
                        ? "<none>"
                        : string.Join(", ", games.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)),
                    Planes = planes.Count.ToString(CultureInfo.InvariantCulture),
                    PlaneIds = planes.ToList(),
                    Groups = groups.Count == 0
                        ? "<none>"
                        : string.Join(", ", groups.Distinct(StringComparer.OrdinalIgnoreCase)),
                    Modifiers = bindModifiers,
                    ActiveGamePlanes = activeGamePlanes
                });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool JsonElementTruthy(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt32(out int intValue) && intValue != 0,
            JsonValueKind.String => bool.TryParse(element.GetString(), out bool boolValue) && boolValue,
            _ => false
        };
    }

    private void ParseRelationNodes(string output)
    {
        Dictionary<string, string> storedTitles = ParseStoredNodeTitles(output);
        // All actions at once: one change of the list (item by item re-marked the search rows per action).
        List<RelationNodeItem> parsed = [];
        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = RelationNodeLine.Match(raw.TrimEnd());
            if (!match.Success)
                continue;

            string game = match.Groups["game"].Value.Trim();
            string nodeId = match.Groups["id"].Value.Trim();
            string activePlanesRaw = match.Groups["aircraft"].Value.Trim();
            string[] planes = activePlanesRaw.Equals("<none>", StringComparison.OrdinalIgnoreCase) ||
                              string.IsNullOrWhiteSpace(activePlanesRaw)
                ? Array.Empty<string>()
                : activePlanesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .ToArray();

            // The engine lists only active planes; "<none>" means every plane of this node is disabled.
            string planesDisplay = planes.Length == 0
                ? T("No active aircraft")
                : string.Join(Environment.NewLine, planes);
            string titlesDisplay = planes.Length == 0
                ? ResolveNodeTitle(game, "Default", nodeId)
                : string.Join(Environment.NewLine, planes.Select(plane => ResolveNodeTitle(game, plane, nodeId, StoredNodeTitle(storedTitles, nodeId, plane))));
            string planesSummary = planes.Length == 0
                ? T("No active aircraft")
                : BuildNodeListSummary(planes);
            string titlesSummary = planes.Length == 0
                ? ResolveNodeTitle(game, "Default", nodeId)
                : BuildNodeListSummary(planes.Select(plane => ResolveNodeTitle(game, plane, nodeId, StoredNodeTitle(storedTitles, nodeId, plane))));

            RelationNodeItem node = new()
            {
                Game = game,
                Id = nodeId,
                ActivePlanesText = planes.Length == 0 ? string.Empty : string.Join(",", planes),
                PlanesDisplay = planesDisplay,
                TitlesDisplay = titlesDisplay,
                PlanesSummary = planesSummary,
                TitlesSummary = titlesSummary,
                NodeSpec = raw.Trim(),
                KindLabel = SelectedRelation == null ? string.Empty : SelectedRelation.IsAxis ? "axis" : "button"
            };
            if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string plane in planes.Take(12))
                    node.PlanePictures.Add(new PlaneChoice(plane));
            }
            parsed.Add(node);
            AddRelationNodeGameOption(game);
        }
        if (parsed.Count == 0)
            RelationNodes.Clear();
        else
            RelationNodes.ReplaceAll(parsed);
        _ = LoadRelationNodePicturesAsync(RelationNodes.ToList());
    }

    private async Task LoadRelationNodePicturesAsync(List<RelationNodeItem> nodes)
    {
        foreach (RelationNodeItem node in nodes)
        {
            if (!node.Game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                node.GameCover ??= await LoadGameCoverAsync(node.Game, 48);
            foreach (PlaneChoice choice in node.PlanePictures)
                choice.Image ??= await LoadAircraftPictureAsync(choice.Name);
            // DCS: the first aircraft's module icon is the card's picture (the relation editor shows one per action).
            node.GameCover ??= node.PlanePictures.Select(p => p.Image).FirstOrDefault(i => i != null);
        }
    }

    private static string BuildNodeListSummary(IEnumerable<string> values)
    {
        string[] normalized = values
            .Select(v => (v ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            return "<none>";

        const int previewCount = 3;
        if (normalized.Length <= previewCount)
            return string.Join(", ", normalized);

        int remaining = normalized.Length - previewCount;
        return T("{0} (+{1} more)", string.Join(", ", normalized.Take(previewCount)), remaining);
    }

    // True when the engine answered in JSON (the text list is the fallback).
    private bool ParseBinds(string output)
    {
        allBinds.Clear();
        modifierReformerCacheByName.Clear();

        if (TryParseBindsFromJson(output, out List<BindListItem> jsonBinds))
        {
            for (int i = 0; i < jsonBinds.Count; i++)
                allBinds.Add(jsonBinds[i]);
            AfterBindsParsed();
            return true;
        }

        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("Binds:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length == 0)
                continue;

            Match match = BindListLine.Match(line);
            if (!match.Success)
                continue;

            BindListItem item = new()
            {
                RelationName = match.Groups["name"].Value.Trim(),
                Type = NormalizeBindType(match.Groups["type"].Value.Trim()),
                TypeDisplay = NormalizeBindType(match.Groups["type"].Value.Trim()),
                HasBind = true,
                BindState = "Bound",
                Device = match.Groups["device"].Value.Trim(),
                Input = match.Groups["input"].Value.Trim(),
                ModifiersSummary = match.Groups["mods"].Value.Trim()
            };
            RegisterModifierReformersFromText(item.RelationName, item.ModifiersSummary);
            allBinds.Add(item);
        }
        AfterBindsParsed();
        return false;
    }

    private void AfterBindsParsed()
    {
        // After Set Bind, Record, Clear or Unbind the relations' bind column (read before) follows the bind list.
        bool boundChanged = SyncRelationBindsFromBinds();
        RebuildQuickBindOptions();
        RebuildVisibleBinds();
        UpdateRecordTargetContext();
        SyncModifierSelectionFromSelectedBind();
        RebuildModifierTargetRelationOptions();
        RefreshModifierValidationState();
        // Bound counts: Bind Studio header, Start page tile, next steps and device cards (the relation cards follow
        // their Bind property).
        if (boundChanged)
            RebuildDashboard();
    }

    private bool TryParseBindsFromJson(string output, out List<BindListItem> parsedBinds)
    {
        parsedBinds = [];
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("Binds", out JsonElement bindsEl) ||
                bindsEl.ValueKind != JsonValueKind.Array)
                return false;

            foreach (JsonElement bindEl in bindsEl.EnumerateArray())
            {
                if (bindEl.ValueKind != JsonValueKind.Object)
                    continue;

                string relationName = GetJsonString(bindEl, "RelationName");
                if (string.IsNullOrWhiteSpace(relationName))
                    continue;

                bool isAxis = bindEl.TryGetProperty("IsAxis", out JsonElement axisEl) &&
                              axisEl.ValueKind == JsonValueKind.True;
                string type = isAxis ? "axis" : "button";
                string device = GetJsonString(bindEl, "Joystick");
                string input = GetJsonString(bindEl, "JButton");
                if (string.IsNullOrWhiteSpace(input))
                    input = GetJsonString(bindEl, "JAxis");

                string normalizedDevice = string.IsNullOrWhiteSpace(device) ? "<unbound>" : device.Trim();
                string normalizedInput = string.IsNullOrWhiteSpace(input) ? "<unbound>" : input.Trim();
                bool hasBind = !normalizedDevice.Equals("<unbound>", StringComparison.OrdinalIgnoreCase) &&
                               !normalizedInput.Equals("<unbound>", StringComparison.OrdinalIgnoreCase);

                List<string> reformers = [];
                if (bindEl.TryGetProperty("AllReformers", out JsonElement reformersEl) &&
                    reformersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement reformerEl in reformersEl.EnumerateArray())
                    {
                        if (reformerEl.ValueKind != JsonValueKind.String)
                            continue;
                        string reformer = (reformerEl.GetString() ?? string.Empty).Trim();
                        if (reformer.Length == 0)
                            continue;
                        reformers.Add(reformer);
                        RegisterModifierReformer(relationName, reformer);
                    }
                }

                List<string> modifierNames = reformers
                    .Select(ModifierNameFromReformer)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string modifiersSummary = modifierNames.Count == 0
                    ? "<none>"
                    : string.Join(",", modifierNames);

                List<double> curvature = [];
                if (bindEl.TryGetProperty("Curvature", out JsonElement curvatureEl) &&
                    curvatureEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement pointEl in curvatureEl.EnumerateArray())
                    {
                        if (pointEl.ValueKind == JsonValueKind.Number)
                            curvature.Add(pointEl.GetDouble());
                    }
                }

                parsedBinds.Add(new BindListItem
                {
                    RelationName = relationName.Trim(),
                    Type = type,
                    TypeDisplay = type,
                    HasBind = hasBind,
                    BindState = hasBind ? "Bound" : "Unbound",
                    Device = normalizedDevice,
                    Input = normalizedInput,
                    ModifiersSummary = modifiersSummary,
                    IsAxis = isAxis,
                    Inverted = GetJsonNullableBool(bindEl, "Inverted"),
                    Slider = GetJsonNullableBool(bindEl, "Slider"),
                    DoubleTap = GetJsonNullableBool(bindEl, "DoubleTap"),
                    Deadzone = GetJsonNullableDouble(bindEl, "Deadzone"),
                    SaturationX = GetJsonNullableDouble(bindEl, "SaturationX"),
                    SaturationY = GetJsonNullableDouble(bindEl, "SaturationY"),
                    CurvatureCsv = string.Join(",", curvature.Select(v => v.ToString(CultureInfo.InvariantCulture)))
                });
            }

            return true;
        }
        catch
        {
            parsedBinds = [];
            return false;
        }
    }

    private static bool TryParseLayoutSuggestions(string output, out List<LayoutSuggestionItem> suggestions)
    {
        suggestions = [];
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (!doc.RootElement.TryGetProperty("Suggestions", out JsonElement suggestionsEl) ||
                suggestionsEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement element in suggestionsEl.EnumerateArray())
            {
                string device = element.TryGetProperty("Device", out JsonElement deviceEl)
                    ? (deviceEl.GetString() ?? string.Empty)
                    : string.Empty;
                string templateName = element.TryGetProperty("TemplateName", out JsonElement templateEl)
                    ? (templateEl.GetString() ?? string.Empty)
                    : string.Empty;
                string layoutPath = element.TryGetProperty("LayoutPath", out JsonElement layoutEl)
                    ? (layoutEl.GetString() ?? string.Empty)
                    : string.Empty;
                string backdropPath = element.TryGetProperty("BackdropPath", out JsonElement backdropEl)
                    ? (backdropEl.GetString() ?? string.Empty)
                    : string.Empty;
                string reason = element.TryGetProperty("MatchReason", out JsonElement reasonEl)
                    ? (reasonEl.GetString() ?? string.Empty)
                    : string.Empty;
                string mapSpec = element.TryGetProperty("LayoutMapSpec", out JsonElement mapEl)
                    ? (mapEl.GetString() ?? string.Empty)
                    : string.Empty;
                string source = element.TryGetProperty("Source", out JsonElement sourceEl)
                    ? (sourceEl.GetString() ?? string.Empty)
                    : string.Empty;
                int score = element.TryGetProperty("Score", out JsonElement scoreEl) && scoreEl.TryGetInt32(out int scoreValue)
                    ? scoreValue
                    : 0;
                int markerCount = element.TryGetProperty("MarkerCount", out JsonElement markerEl) && markerEl.TryGetInt32(out int markerValue)
                    ? markerValue
                    : 0;

                List<LayoutEditorTableItem> suggestedTables = [];
                if (element.TryGetProperty("Tables", out JsonElement tablesEl) && tablesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tableEl in tablesEl.EnumerateArray())
                    {
                        string tableName = GetJsonString(tableEl, "Name");
                        if (string.IsNullOrWhiteSpace(tableName))
                            tableName = "Table";
                        int columns = 1;
                        if (tableEl.TryGetProperty("Columns", out JsonElement columnsEl) && columnsEl.TryGetInt32(out int parsedColumns))
                            columns = Math.Max(1, parsedColumns);

                        suggestedTables.Add(new LayoutEditorTableItem
                        {
                            Name = tableName,
                            Mode = NormalizeLayoutTableMode(GetJsonString(tableEl, "Mode")),
                            X = TryGetJsonDouble(tableEl, "X"),
                            Y = TryGetJsonDouble(tableEl, "Y"),
                            Width = Math.Max(120, TryGetJsonDouble(tableEl, "Width")),
                            Height = Math.Max(70, TryGetJsonDouble(tableEl, "Height")),
                            Columns = columns,
                            BackgroundColor = NormalizeLayoutTableBackgroundColor(GetJsonString(tableEl, "BackgroundColor")),
                            BackgroundOpacity = Math.Clamp(TryGetJsonDoubleOrDefault(tableEl, "BackgroundOpacity", 0.93), 0, 1),
                            DropShadow = TryGetJsonBool(tableEl, "DropShadow", true)
                        });
                    }
                }

                LayoutSuggestionItem item = new LayoutSuggestionItem
                {
                    Model = GetJsonString(element, "Model"),
                    SearchUrl = GetJsonString(element, "SearchUrl"),
                    IsSelected = score >= 55 && !string.IsNullOrWhiteSpace(layoutPath),
                    Device = device,
                    TemplateName = templateName,
                    LayoutPath = layoutPath,
                    BackdropPath = backdropPath,
                    Score = score,
                    MatchReason = reason,
                    LayoutMapSpec = mapSpec,
                    MarkerCount = markerCount,
                    Source = source
                };
                foreach (LayoutEditorTableItem table in suggestedTables)
                    item.SuggestedTables.Add(table);

                suggestions.Add(item);
            }

            return suggestions.Count > 0 || suggestionsEl.GetArrayLength() == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseLayoutTemplateInspect(
        string output,
        out LayoutTemplateInspectDocument inspect,
        out string error)
    {
        inspect = new LayoutTemplateInspectDocument();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            error = T("Payload is empty.");
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;

            inspect.SourcePath = GetJsonString(root, "SourcePath");
            inspect.PreviewImagePath = GetJsonString(root, "PreviewImagePath");
            if (root.TryGetProperty("Width", out JsonElement widthEl) && widthEl.TryGetInt32(out int width))
                inspect.Width = Math.Max(0, width);
            if (root.TryGetProperty("Height", out JsonElement heightEl) && heightEl.TryGetInt32(out int height))
                inspect.Height = Math.Max(0, height);
            inspect.Joystick = GetJsonString(root, "Joystick");
            inspect.FontName = GetJsonString(root, "FontName");
            inspect.TextColor = GetJsonString(root, "TextColor");
            inspect.Credit = GetJsonString(root, "Credit");
            if (root.TryGetProperty("FontSize", out JsonElement fontSizeEl) && fontSizeEl.TryGetInt32(out int fontSize))
                inspect.FontSize = Math.Max(0, fontSize);

            if (root.TryGetProperty("Markers", out JsonElement markersEl) && markersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement markerEl in markersEl.EnumerateArray())
                {
                    LayoutEditorMarkerItem marker = new()
                    {
                        Token = GetJsonString(markerEl, "Token").Trim(),
                        Type = GetJsonString(markerEl, "Type").Trim(),
                        Modifier = GetJsonString(markerEl, "Modifier").Trim(),
                        X = TryGetJsonDouble(markerEl, "X"),
                        Y = TryGetJsonDouble(markerEl, "Y")
                    };
                    if (marker.Token.Length == 0)
                        continue;
                    if (marker.Type.Length == 0)
                        marker.Type = "button";
                    inspect.Markers.Add(marker);
                }
            }

            if (root.TryGetProperty("Tables", out JsonElement tablesEl) && tablesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tableEl in tablesEl.EnumerateArray())
                {
                    string tableName = GetJsonString(tableEl, "Name");
                    if (string.IsNullOrWhiteSpace(tableName))
                        tableName = "Table";

                    int columns = 1;
                    if (tableEl.TryGetProperty("Columns", out JsonElement columnsEl) && columnsEl.TryGetInt32(out int parsedColumns))
                        columns = Math.Max(1, parsedColumns);

                    inspect.Tables.Add(new LayoutEditorTableItem
                    {
                        Name = tableName,
                        Mode = NormalizeLayoutTableMode(GetJsonString(tableEl, "Mode")),
                        X = TryGetJsonDouble(tableEl, "X"),
                        Y = TryGetJsonDouble(tableEl, "Y"),
                        Width = Math.Max(120, TryGetJsonDouble(tableEl, "Width")),
                        Height = Math.Max(70, TryGetJsonDouble(tableEl, "Height")),
                        Columns = columns,
                        BackgroundColor = NormalizeLayoutTableBackgroundColor(GetJsonString(tableEl, "BackgroundColor")),
                        BackgroundOpacity = Math.Clamp(TryGetJsonDoubleOrDefault(tableEl, "BackgroundOpacity", 0.93), 0, 1),
                        DropShadow = TryGetJsonBool(tableEl, "DropShadow", true)
                    });
                }
            }

            if (root.TryGetProperty("Connectors", out JsonElement connectorsEl) && connectorsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement connectorEl in connectorsEl.EnumerateArray())
                {
                    string name = GetJsonString(connectorEl, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                        name = "Connector";
                    string color = GetJsonString(connectorEl, "Color");
                    if (string.IsNullOrWhiteSpace(color))
                        color = "#E2E8F0";
                    double connectorWidth = TryGetJsonDouble(connectorEl, "Width");
                    if (connectorWidth <= 0)
                        connectorWidth = 2;
                    string route = NormalizeLayoutConnectorRoute(GetJsonString(connectorEl, "Route"));
                    int steps = Math.Clamp((int)Math.Round(TryGetJsonDoubleOrDefault(connectorEl, "Steps", 2), MidpointRounding.AwayFromZero), 0, 8);
                    string targetToken = GetJsonString(connectorEl, "TargetToken").Trim();

                    List<Point> points = [];
                    if (connectorEl.TryGetProperty("Points", out JsonElement pointsEl) && pointsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement pointEl in pointsEl.EnumerateArray())
                        {
                            double x = TryGetJsonDouble(pointEl, "X");
                            double y = TryGetJsonDouble(pointEl, "Y");
                            points.Add(new Point(x, y));
                        }
                    }

                    if (points.Count < 2)
                        continue;

                    LayoutEditorConnectorItem connector = new()
                    {
                        Name = name,
                        Color = color,
                        StrokeWidth = Math.Clamp(connectorWidth, 1, 12),
                        Route = route,
                        Steps = steps,
                        TargetToken = targetToken
                    };
                    connector.SetPoints(points);
                    connector.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(points);
                    inspect.Connectors.Add(connector);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseLayoutTemplateSave(
        string output,
        out LayoutTemplateSaveDocument save,
        out string error)
    {
        save = new LayoutTemplateSaveDocument();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            error = T("Payload is empty.");
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            save.SourcePath = GetJsonString(root, "SourcePath");
            save.OutputPath = GetJsonString(root, "OutputPath");
            if (root.TryGetProperty("MarkerCount", out JsonElement markerCountEl) && markerCountEl.TryGetInt32(out int markerCount))
                save.MarkerCount = Math.Max(0, markerCount);
            if (root.TryGetProperty("TableCount", out JsonElement tableCountEl) && tableCountEl.TryGetInt32(out int tableCount))
                save.TableCount = Math.Max(0, tableCount);
            if (root.TryGetProperty("ConnectorCount", out JsonElement connectorCountEl) && connectorCountEl.TryGetInt32(out int connectorCount))
                save.ConnectorCount = Math.Max(0, connectorCount);
            if (root.TryGetProperty("ReplacedAll", out JsonElement replacedEl))
            {
                if (replacedEl.ValueKind == JsonValueKind.True)
                    save.ReplacedAll = true;
                else if (replacedEl.ValueKind == JsonValueKind.False)
                    save.ReplacedAll = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static double TryGetJsonDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double number))
            return number;
        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return 0;
    }

    private static double TryGetJsonDoubleOrDefault(JsonElement element, string propertyName, double fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double number))
            return number;
        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return fallback;
    }

    private static bool TryGetJsonBool(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return fallback;
        if (property.ValueKind == JsonValueKind.True)
            return true;
        if (property.ValueKind == JsonValueKind.False)
            return false;
        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out bool parsed))
            return parsed;
        return fallback;
    }

    private string ResolveLayoutEditorSourcePath()
    {
        List<string> candidates = [];
        if (SelectedLayoutSuggestion != null)
        {
            if (!string.IsNullOrWhiteSpace(SelectedLayoutSuggestion.LayoutPath))
                candidates.Add(SelectedLayoutSuggestion.LayoutPath);
            if (!string.IsNullOrWhiteSpace(SelectedLayoutSuggestion.BackdropPath))
                candidates.Add(SelectedLayoutSuggestion.BackdropPath);
        }
        if (!string.IsNullOrWhiteSpace(LayoutEditorSourcePath))
            candidates.Add(LayoutEditorSourcePath);

        string existing = TryFindFirstExistingFile(candidates);
        if (existing.Length > 0)
            return existing;
        return candidates.Count == 0 ? string.Empty : NormalizeUiPath(candidates[0]);
    }

    private string ResolveLayoutBackdropSearchDevice()
    {
        if (!string.IsNullOrWhiteSpace(SelectedLayoutSuggestion?.Device))
            return SelectedLayoutSuggestion.Device.Trim();

        LayoutSuggestionItem? checkedSuggestion = LayoutSuggestions.FirstOrDefault(v =>
            v.IsSelected && !string.IsNullOrWhiteSpace(v.Device));
        if (!string.IsNullOrWhiteSpace(checkedSuggestion?.Device))
            return checkedSuggestion.Device.Trim();
        
        return string.Empty;
    }

    private string ResolveLayoutEditorContextDevice()
    {
        if (!string.IsNullOrWhiteSpace(layoutEditorContextDevice))
            return layoutEditorContextDevice.Trim();

        return ResolveLayoutBackdropSearchDevice();
    }

    private void RememberLayoutDeviceMapping(string? deviceName, string? layoutPath)
    {
        string device = (deviceName ?? string.Empty).Trim();
        string path = NormalizeUiPath(layoutPath ?? string.Empty);
        if (device.Length == 0 || path.Length == 0)
            return;
        if (!path.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
            return;

        layoutTemplateByDevice[device] = path;
    }

    private bool TryResolveRememberedLayoutPathForDevice(string deviceName, out string layoutPath)
    {
        layoutPath = string.Empty;
        string device = (deviceName ?? string.Empty).Trim();
        if (device.Length == 0)
            return false;

        if (layoutTemplateByDevice.TryGetValue(device, out string? mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            string candidate = NormalizeUiPath(mapped);
            if (candidate.Length > 0 && candidate.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
            {
                layoutPath = candidate;
                return true;
            }
        }

        LayoutSuggestionItem? suggestion = LayoutSuggestions.FirstOrDefault(v =>
            string.Equals(v.Device, device, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(v.LayoutPath));
        if (suggestion == null)
            return false;

        string suggestionPath = NormalizeUiPath(suggestion.LayoutPath);
        if (suggestionPath.Length == 0 || !suggestionPath.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
            return false;

        layoutPath = suggestionPath;
        layoutTemplateByDevice[device] = suggestionPath;
        return true;
    }

    private static string BuildDefaultLayoutEditorOutputPath(string sourcePath)
    {
        string source = NormalizeUiPath(sourcePath);
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        string fileName = Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "layout";

        string outputDirectory = Path.GetDirectoryName(source) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "JoyPro2",
                "layouts");
        }

        string baseName = fileName + "_edited";
        return Path.Combine(outputDirectory, baseName + ".layout");
    }

    private static List<LayoutEditorTableItem> BuildDefaultLayoutEditorTables(
        double canvasWidth,
        double canvasHeight,
        IReadOnlyCollection<LayoutEditorMarkerItem> markers)
    {
        double width = Math.Max(640, canvasWidth);
        double height = Math.Max(420, canvasHeight);
        bool hasModifiers = markers.Any(v => !string.IsNullOrWhiteSpace(v.Modifier));
        bool includeSecondary = markers.Count > 24;

        List<LayoutEditorTableItem> tables =
        [
            new LayoutEditorTableItem
            {
                Name = "Primary Controls",
                Mode = "panel",
                X = Math.Round(Math.Max(10, width * 0.68), 2),
                Y = Math.Round(Math.Max(10, height * 0.04), 2),
                Width = Math.Round(Math.Max(240, width * 0.30), 2),
                Height = Math.Round(Math.Max(180, height * 0.42), 2),
                Columns = 1
            }
        ];

        if (includeSecondary)
        {
            tables.Add(new LayoutEditorTableItem
            {
                Name = "Secondary Controls",
                Mode = "panel",
                X = Math.Round(Math.Max(10, width * 0.68), 2),
                Y = Math.Round(Math.Max(20, height * 0.48), 2),
                Width = Math.Round(Math.Max(240, width * 0.30), 2),
                Height = Math.Round(Math.Max(120, height * 0.24), 2),
                Columns = 1
            });
        }

        if (hasModifiers)
        {
            tables.Add(new LayoutEditorTableItem
            {
                Name = "Modifier Layer",
                Mode = "panel",
                X = Math.Round(Math.Max(10, width * 0.68), 2),
                Y = Math.Round(Math.Max(20, height * 0.74), 2),
                Width = Math.Round(Math.Max(240, width * 0.30), 2),
                Height = Math.Round(Math.Max(100, height * 0.20), 2),
                Columns = 1
            });
        }

        return tables;
    }

    private void LoadLayoutEditorPreview(string previewPath, int widthHint, int heightHint)
    {
        string normalized = NormalizeUiPath(previewPath);
        Bitmap? next = null;
        int canvasWidth = Math.Max(0, widthHint);
        int canvasHeight = Math.Max(0, heightHint);

        if (!string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized))
        {
            try
            {
                next = new Bitmap(normalized);
                canvasWidth = next.PixelSize.Width;
                canvasHeight = next.PixelSize.Height;
            }
            catch (Exception ex)
            {
                // A broken preview must not stop the markers from loading.
                next = null;
                StatusText = T("Could not show the layout picture: {0}", ex.Message);
            }
        }

        Bitmap? old = LayoutEditorPreviewImage;
        LayoutEditorPreviewImage = next;
        old?.Dispose();

        LayoutEditorCanvasWidth = Math.Max(320, canvasWidth > 0 ? canvasWidth : 1280);
        LayoutEditorCanvasHeight = Math.Max(200, canvasHeight > 0 ? canvasHeight : 720);
    }

    private string EnsureUniqueLayoutEditorToken(string baseToken)
    {
        string token = (baseToken ?? string.Empty).Trim();
        if (token.Length == 0)
            token = "JOY_BTN";
        if (!LayoutEditorMarkers.Any(v => string.Equals(v.Token, token, StringComparison.OrdinalIgnoreCase)))
            return token;

        for (int i = 2; i < 10000; i++)
        {
            string candidate = token + "_" + i.ToString(CultureInfo.InvariantCulture);
            if (!LayoutEditorMarkers.Any(v => string.Equals(v.Token, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return token + "_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6];
    }

    private string EnsureUniqueLayoutConnectorName(string baseName)
    {
        string name = (baseName ?? string.Empty).Trim();
        if (name.Length == 0)
            name = "Connector";
        if (!LayoutEditorConnectors.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        for (int i = 2; i < 10000; i++)
        {
            string candidate = name + "_" + i.ToString(CultureInfo.InvariantCulture);
            if (!LayoutEditorConnectors.Any(v => string.Equals(v.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        return name + "_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6];
    }

    private List<Point> BuildDefaultConnectorPoints()
    {
        LayoutEditorMarkerItem? target = ResolveLayoutConnectorTargetMarker();
        Point end = target != null
            ? ClampCanvasPoint(new Point(target.X + 6, target.Y + 6))
            : ClampCanvasPoint(new Point(LayoutEditorCanvasWidth * 0.66, LayoutEditorCanvasHeight * 0.36));

        Point start = layoutEditorConnectorSourcePoint.HasValue
            ? ClampCanvasPoint(layoutEditorConnectorSourcePoint.Value)
            : (target != null
                ? ClampCanvasPoint(new Point(Math.Max(0, target.X - 180), Math.Max(0, target.Y - 100)))
                : ClampCanvasPoint(new Point(LayoutEditorCanvasWidth * 0.24, LayoutEditorCanvasHeight * 0.32)));

        string route = NormalizeLayoutConnectorRoute(LayoutEditorNewConnectorRoute);
        int steps = ParseOrDefault(LayoutEditorNewConnectorSteps, 2, 0, 8);

        if (string.Equals(route, "diagonal", StringComparison.OrdinalIgnoreCase))
            return [start, end];

        if (string.Equals(route, "manual", StringComparison.OrdinalIgnoreCase))
            return [start, end];

        int bends = Math.Max(1, steps);
        List<Point> points = [start];
        Point previous = start;
        for (int i = 1; i <= bends; i++)
        {
            double t = (double)i / (bends + 1.0);
            bool horizontal = i % 2 == 1;
            Point next = horizontal
                ? ClampCanvasPoint(new Point(start.X + ((end.X - start.X) * t), previous.Y))
                : ClampCanvasPoint(new Point(previous.X, start.Y + ((end.Y - start.Y) * t)));
            points.Add(next);
            previous = next;
        }

        points.Add(end);
        return points
            .Where((point, index) => index == 0 || point != points[index - 1])
            .ToList();
    }

    private LayoutEditorMarkerItem? ResolveLayoutConnectorTargetMarker()
    {
        string targetToken = (LayoutEditorNewConnectorTargetInput ?? string.Empty).Trim();
        if (targetToken.Length > 0)
        {
            LayoutEditorMarkerItem? byToken = LayoutEditorMarkers.FirstOrDefault(v =>
                string.Equals(v.Token, targetToken, StringComparison.OrdinalIgnoreCase));
            if (byToken != null)
                return byToken;
        }

        return SelectedLayoutEditorMarker;
    }

    private Point ClampCanvasPoint(Point point)
    {
        double x = Math.Clamp(point.X, 0, Math.Max(0, LayoutEditorCanvasWidth - 1));
        double y = Math.Clamp(point.Y, 0, Math.Max(0, LayoutEditorCanvasHeight - 1));
        return new Point(
            Math.Round(x, 2, MidpointRounding.AwayFromZero),
            Math.Round(y, 2, MidpointRounding.AwayFromZero));
    }

    private static string NormalizeLayoutTableMode(string mode)
    {
        string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "list" ? "list" : "panel";
    }

    private static string NormalizeLayoutTableBackgroundColor(string color)
    {
        string raw = (color ?? string.Empty).Trim();
        if (raw.Length == 0)
            return "#F6F8FB";
        if (!raw.StartsWith("#", StringComparison.Ordinal))
            raw = "#" + raw;

        if (raw.Length == 7 || raw.Length == 9)
            return raw.ToUpperInvariant();

        return "#F6F8FB";
    }

    private static string NormalizeLayoutConnectorRoute(string route)
    {
        string normalized = (route ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "diagonal" => "diagonal",
            "manual" => "manual",
            _ => "orthogonal"
        };
    }

    private static string NormalizeUiPath(string rawPath)
    {
        string path = (rawPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
            return string.Empty;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void RegisterModifierReformer(string relationName, string reformer)
    {
        if (!TryParseModifierReformer(reformer, out string name, out string device, out string input, out _))
            return;

        string normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return;

        if (!modifierReformerCacheByName.TryGetValue(normalizedName, out List<ModifierReformerMatch>? existing))
        {
            existing = [];
            modifierReformerCacheByName[normalizedName] = existing;
        }

        ModifierReformerMatch candidate = new(
            normalizedName,
            device,
            input,
            relationName ?? string.Empty,
            reformer);

        bool duplicate = existing.Any(match =>
            NormalizeDeviceToken(match.Device) == NormalizeDeviceToken(candidate.Device) &&
            NormalizeInputToken(match.Input) == NormalizeInputToken(candidate.Input) &&
            string.Equals(match.Relation, candidate.Relation, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
            existing.Add(candidate);
    }

    private void RegisterModifierReformersFromText(string relationName, string rawModifiers)
    {
        string value = (rawModifiers ?? string.Empty).Trim();
        if (value.Length == 0 ||
            value.Equals("<none>", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string reformer = token.Trim();
            if (reformer.Length == 0)
                continue;
            RegisterModifierReformer(relationName, reformer);
        }
    }

    // Mutating commands append "Auto-saved profile: ..." after the JSON payload, so cut the payload out.
    internal static string ExtractJsonPayload(string output)
    {
        string text = output ?? string.Empty;
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int contentStart = lineStart;
            while (contentStart < text.Length && (text[contentStart] == ' ' || text[contentStart] == '\t'))
                contentStart++;

            if (contentStart < text.Length && (text[contentStart] == '{' || text[contentStart] == '['))
            {
                char closer = text[contentStart] == '{' ? '}' : ']';
                int end = text.LastIndexOf(closer);
                if (end > contentStart)
                    return text.Substring(contentStart, end - contentStart + 1);
                break;
            }

            int nextLine = text.IndexOf('\n', lineStart);
            if (nextLine < 0)
                break;
            lineStart = nextLine + 1;
        }

        return text;
    }

    private static bool? GetJsonNullableBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? GetJsonNullableDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Number)
            return null;
        return property.GetDouble();
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
            return string.Empty;
        return property.GetString() ?? string.Empty;
    }

    [RelayCommand]
    private async Task SetSelectedBindAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }

        await SetBindFromEditorAsync(target);
    }

    // "binds set" for the relation with the device, input and behavior in the editor; true when the engine saved it.
    private async Task<bool> SetBindFromEditorAsync(string target)
    {
        List<string> args = ["binds", "set", target];
        if (!string.IsNullOrWhiteSpace(BindDeviceInput))
            args.AddRange(["--device", BindDeviceInput.Trim()]);
        if (!string.IsNullOrWhiteSpace(BindInputValue))
            args.AddRange(["--input", BindInputValue.Trim()]);

        args.AddRange(["--double-tap", BindDoubleTap ? "true" : "false"]);

        // The engine rejects every axis option on button relations.
        bool isAxis = IsAxisRelation(target);
        if (!isAxis)
            return await RunSetBindAsync(args, target);

        args.AddRange(["--inverted", BindInverted ? "true" : "false"]);
        args.AddRange(["--slider", BindSlider ? "true" : "false"]);

        if (BindApplyCurvature)
        {
            if (!TryValidateCurvatureCsv(BindCurvatureCsv, out string normalizedCurvature, out string curvatureError))
            {
                StatusText = curvatureError;
                return false;
            }
            args.AddRange(["--curvature", normalizedCurvature]);
        }

        if (BindApplyDeadzone)
        {
            if (!TryValidateAxisValue(BindDeadzoneValue, "Deadzone", out string normalizedDeadzone, maxInclusive: 1.0))
                return false;
            args.AddRange(["--deadzone", normalizedDeadzone]);
        }

        if (BindApplySaturationX)
        {
            if (!TryValidateAxisValue(BindSaturationXValue, "Saturation X", out string normalizedSatX, maxInclusive: 1.0))
                return false;
            args.AddRange(["--saturation-x", normalizedSatX]);
        }

        if (BindApplySaturationY)
        {
            if (!TryValidateAxisValue(BindSaturationYValue, "Saturation Y", out string normalizedSatY, maxInclusive: 1.0))
                return false;
            args.AddRange(["--saturation-y", normalizedSatY]);
        }

        return await RunSetBindAsync(args, target);
    }

    private async Task<bool> RunSetBindAsync(List<string> args, string target)
    {
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Setting bind..."));
        if (result?.ExitCode != 0)
            return false;

        await RefreshBindsAndModifiersAfterChangeAsync();
        if (!string.IsNullOrWhiteSpace(target))
            await ShowBindAsync(target);
        await ReloadAxisTuningAfterBindChangeAsync();
        StatusText = BoundStatus(target);
        return true;
    }

    [RelayCommand]
    private async Task ClearSelectedBindAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }

        List<string> args = ["binds", "clear", target];
        if (ClearInputOnly)
            args.Add("--input-only");

        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Clearing bind..."));
        if (result?.ExitCode == 0)
        {
            int kept = await RefreshAndKeepModifiersAsync(modifiersBefore);
            string keptText = kept > 0 ? " " + StatusText : string.Empty;
            StatusText = (ClearInputOnly ? T("Took the input off {0}.", target) : T("Cleared the bind of {0}.", target)) + keptText;
        }
    }

    [RelayCommand]
    private async Task ClearSelectedInputAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "clear", target, "--input-only"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Clearing bind input..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
            await ShowBindAsync(target);
            StatusText = T("Took the input off {0}.", target);
        }
    }

    [RelayCommand]
    private async Task UnbindInputAsync()
    {
        string target = string.IsNullOrWhiteSpace(UnbindTarget)
            ? (string.IsNullOrWhiteSpace(UnbindInput) ? string.Empty : UnbindInput.Trim())
            : UnbindTarget.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Enter input target or selector.");
            return;
        }

        List<string> args = ["binds", "unbind", target];
        if (!string.IsNullOrWhiteSpace(UnbindDevice))
            args.AddRange(["--device", UnbindDevice.Trim()]);
        if (!string.IsNullOrWhiteSpace(UnbindInput))
            args.AddRange(["--input", UnbindInput.Trim()]);
        if (!string.IsNullOrWhiteSpace(UnbindGame))
            args.AddRange(["--game", UnbindGame.Trim()]);
        if (UnbindAll)
            args.Add("--all");

        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Unbinding input..."));
        if (result?.ExitCode == 0)
        {
            int kept = await RefreshAndKeepModifiersAsync(modifiersBefore);
            string keptText = kept > 0 ? " " + StatusText : string.Empty;
            StatusText = T("Unbound {0}{1}.", target, (string.IsNullOrWhiteSpace(UnbindDevice) ? string.Empty : T(" on {0}", DeviceLabelForStatus(UnbindDevice)))) + keptText;
        }
    }

    // Set by the bind changes right after their binds list, used once by the next RefreshModifiersAsync.
    private bool bindsListedForModifiers;

    // After Set Bind, Clear or Unbind: the binds list again, then the modifiers without a second binds list.
    private async Task RefreshBindsAndModifiersAfterChangeAsync()
    {
        await RefreshBindsAsync();
        bindsListedForModifiers = true;
        await RefreshModifiersAsync();
    }

    [RelayCommand]
    private async Task RefreshModifiersAsync()
    {
        string previouslySelectedName = SelectedModifierItem?.Name ?? string.Empty;

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "modifier", "list"],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Loading modifiers..."));

        // A bind change listed the binds just before (RefreshBindsAsync): the modifiers' bind cache is up to date.
        bool bindsJustListed = bindsListedForModifiers;
        bindsListedForModifiers = false;
        if (result == null || result.ExitCode != 0)
            return;

        await EnsureModifierReformerCacheAsync(forceRefresh: !bindsJustListed);

        Modifiers.Clear();
        ModifierNames.Clear();
        ModifierItems.Clear();
        foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("Modifiers", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length == 0)
                continue;

            Match match = ModifierListLine.Match(line);
            if (!match.Success)
            {
                string fallback = line.Trim().TrimStart('-', ' ').Trim();
                if (fallback.Length > 0)
                {
                    Modifiers.Add(fallback);
                    ModifierNames.Add(fallback);
                    ModifierItems.Add(new ModifierListItem(fallback, 0, string.Empty, string.Empty));
                }
                continue;
            }

            string name = match.Groups["name"].Value.Trim();
            int usage = int.TryParse(match.Groups["count"].Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUsage)
                ? parsedUsage
                : 0;
            string usedByFull = match.Groups["relations"].Value.Trim();

            (string Device, string Input) primary = ResolveModifierPrimaryBinding(name);
            Modifiers.Add(name);
            ModifierNames.Add(name);
            ModifierItems.Add(new ModifierListItem(name, usage, string.Empty, usedByFull, primary.Device, primary.Input));
        }

        ReconcilePendingModifiersWithProfile();
        MergePendingModifiersIntoCollections();

        SyncModifierSelectionFromSelectedBind();
        if (!string.IsNullOrWhiteSpace(previouslySelectedName))
        {
            SelectedModifierItem = ModifierItems.FirstOrDefault(v =>
                string.Equals(v.Name, previouslySelectedName, StringComparison.OrdinalIgnoreCase));
        }
        if (SelectedModifierItem == null && ModifierItems.Count > 0)
            SelectedModifierItem = ModifierItems.FirstOrDefault(v => v.IsSelected) ?? ModifierItems[0];
        RefreshSelectedModifierRelations();
        RefreshModifierValidationState();
        RebuildLayoutEditorModifierOptions();
    }

    private (string Device, string Input) ResolveModifierPrimaryBinding(string modifierName)
    {
        if (string.IsNullOrWhiteSpace(modifierName))
            return (string.Empty, string.Empty);

        string key = modifierName.Trim();
        if (modifierReformerCacheByName.TryGetValue(key, out List<ModifierReformerMatch>? matches) &&
            matches != null &&
            matches.Count > 0)
        {
            ModifierReformerMatch best = matches
                .GroupBy(m => (NormalizeDeviceToken(m.Device), NormalizeInputToken(m.Input)))
                .OrderByDescending(g => g.Count())
                .Select(g => g.First())
                .First();
            return (best.Device, best.Input);
        }

        if (pendingModifiersByName.TryGetValue(key, out PendingModifierDefinition pending))
            return (pending.Device, pending.Input);

        return (string.Empty, string.Empty);
    }

    private void ReconcilePendingModifiersWithProfile()
    {
        if (pendingModifiersByName.Count == 0)
            return;

        HashSet<string> existing = ModifierItems
            .Where(v => !v.IsPending)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string key in pendingModifiersByName.Keys.ToList())
        {
            if (existing.Contains(key))
                pendingModifiersByName.Remove(key);
        }
    }

    private void MergePendingModifiersIntoCollections()
    {
        foreach (PendingModifierDefinition pending in pendingModifiersByName.Values
                     .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            ModifierListItem? existing = ModifierItems.FirstOrDefault(v =>
                string.Equals(v.Name, pending.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                continue;

            ModifierListItem item = new(pending.Name, 0, string.Empty, string.Empty, pending.Device, pending.Input, isPending: true);
            ModifierItems.Add(item);

            if (!Modifiers.Any(v => string.Equals(v, pending.Name, StringComparison.OrdinalIgnoreCase)))
                Modifiers.Add(pending.Name);
            if (!ModifierNames.Any(v => string.Equals(v, pending.Name, StringComparison.OrdinalIgnoreCase)))
                ModifierNames.Add(pending.Name);
        }
    }

    private bool TryCreateTemporaryModifier(string name, string device, string input, bool inverted, out string error)
    {
        error = string.Empty;

        string normalizedName = (name ?? string.Empty).Trim();
        string normalizedDevice = (device ?? string.Empty).Trim();
        string normalizedInput = (input ?? string.Empty).Trim();
        if (normalizedName.Length == 0 || normalizedDevice.Length == 0 || normalizedInput.Length == 0)
        {
            error = T("Modifier name, device and input are required.");
            return false;
        }

        bool existsInProfile = ModifierItems.Any(v =>
            !v.IsPending &&
            string.Equals(v.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existsInProfile)
        {
            error = T("Modifier '{0}' already exists in profile.", normalizedName);
            return false;
        }

        PendingModifierDefinition pending = new(normalizedName, normalizedDevice, normalizedInput, inverted);
        pendingModifiersByName[normalizedName] = pending;

        ModifierListItem? existing = ModifierItems.FirstOrDefault(v =>
            string.Equals(v.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            bool wasSelected = existing.IsSelected;
            int index = ModifierItems.IndexOf(existing);
            ModifierListItem replacement = new(normalizedName, 0, string.Empty, string.Empty, normalizedDevice, normalizedInput, isPending: true)
            {
                IsSelected = wasSelected
            };
            ModifierItems[index] = replacement;
            SelectedModifierItem = replacement;
        }
        else
        {
            ModifierListItem item = new(normalizedName, 0, string.Empty, string.Empty, normalizedDevice, normalizedInput, isPending: true)
            {
                IsSelected = true
            };
            ModifierItems.Add(item);
            SelectedModifierItem = item;
        }

        if (!Modifiers.Any(v => string.Equals(v, normalizedName, StringComparison.OrdinalIgnoreCase)))
            Modifiers.Add(normalizedName);
        if (!ModifierNames.Any(v => string.Equals(v, normalizedName, StringComparison.OrdinalIgnoreCase)))
            ModifierNames.Add(normalizedName);
        ModifierRenameOld = normalizedName;
        return true;
    }

    private List<string> BuildModifierAddArguments(IEnumerable<string> modifierNames)
    {
        List<string> output = [];
        foreach (string raw in modifierNames ?? Enumerable.Empty<string>())
        {
            string name = (raw ?? string.Empty).Trim();
            if (name.Length == 0)
                continue;

            if (pendingModifiersByName.TryGetValue(name, out PendingModifierDefinition pending))
            {
                output.Add(BuildModifierReformerString(pending.Name, pending.Device, pending.Input, pending.Inverted));
                continue;
            }

            output.Add(name);
        }

        return output
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [RelayCommand]
    private async Task AddModifierToSelectedBindAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }
        List<string> modifiers = GetSelectedModifierNames();
        if (modifiers.Count == 0)
            modifiers = ParseCsv(ModifierNameInput);
        if (modifiers.Count == 0)
        {
            StatusText = T("Select at least one modifier.");
            return;
        }

        List<string> args = ["binds", "modifier", "add", target];
        List<string> modifierArgs = BuildModifierAddArguments(modifiers);
        if (modifierArgs.Count == 0)
        {
            StatusText = T("Select at least one modifier.");
            return;
        }

        args.AddRange(modifierArgs);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Adding modifier..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
            if (!string.IsNullOrWhiteSpace(target))
                await ShowBindAsync(target);
        }
    }

    [RelayCommand]
    private async Task RemoveModifierFromSelectedBindAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }

        List<string> args = ["binds", "modifier", "remove", target];
        List<string> toRemove = GetSelectedModifierNames();
        if (toRemove.Count == 0)
            toRemove = ParseCsv(ModifierRemoveCsv);
        if (toRemove.Count == 0)
            args.Add("--all");
        else
            args.AddRange(toRemove);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Removing modifier..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
            if (!string.IsNullOrWhiteSpace(target))
                await ShowBindAsync(target);
        }
    }

    [RelayCommand]
    private async Task ClearModifiersFromSelectedBindAsync()
    {
        string target = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            StatusText = T("Select a bind or relation first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "modifier", "remove", target, "--all"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Clearing modifiers..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
            await ShowBindAsync(target);
        }
    }

    [RelayCommand]
    private async Task CreateModifierOnSelectedBindAsync()
    {
        string modifier = (ModifierCreateName ?? string.Empty).Trim();
        string device = (ModifierCreateDevice ?? string.Empty).Trim();
        string input = (ModifierCreateInput ?? string.Empty).Trim();
        if (modifier.Length == 0)
        {
            StatusText = T("Enter a modifier name.");
            return;
        }
        if (device.Length == 0)
        {
            StatusText = T("Enter a modifier device.");
            return;
        }
        if (input.Length == 0)
        {
            StatusText = T("Enter a modifier input.");
            return;
        }

        (string Name, string Relation, string Reformer)? duplicateAnywhere = await FindDuplicateModifierAnywhereAsync(device, input);
        if (duplicateAnywhere.HasValue)
        {
            string relationSuffix = string.IsNullOrWhiteSpace(duplicateAnywhere.Value.Relation)
                ? string.Empty
                : T(" in relation '{0}'", duplicateAnywhere.Value.Relation);
            StatusText = T("Warning: input already used by modifier '{0}'{1}.", duplicateAnywhere.Value.Name, relationSuffix);
            RecordStatusText = T("Duplicate modifier input detected: {0}", duplicateAnywhere.Value.Reformer);
            WarningRequested?.Invoke(StatusText);
            return;
        }

        string requestedTarget = (ModifierCreateTargetRelation ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedTarget))
            requestedTarget = (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();

        string target = ModifierTargetRelationOptions
            .FirstOrDefault(v => string.Equals(v, requestedTarget, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(target))
        {
            if (!TryCreateTemporaryModifier(modifier, device, input, ModifierCreateInverted, out string tempError))
            {
                StatusText = tempError;
                WarningRequested?.Invoke(StatusText);
                return;
            }

            ModifierCreateName = string.Empty;
            // Kept with the profile by the autosave (its drafts, docs/profile-history.md) until it is on a bind.
            StatusText = T("Temporary modifier '{0}' created. JoyPro keeps it with the profile; put it on a bind to use it in the games.", modifier);
            ScheduleAutosave();
            return;
        }

        (string Name, string Reformer)? duplicate = await FindDuplicateModifierOnTargetAsync(target, device, input);
        if (duplicate.HasValue)
        {
            StatusText = T("Warning: input already used by modifier '{0}' on relation '{1}'.", duplicate.Value.Name, target);
            RecordStatusText = T("Duplicate modifier input detected: {0}", duplicate.Value.Reformer);
            WarningRequested?.Invoke(StatusText);
            return;
        }

        string reformer = BuildModifierReformerString(modifier, device, input, ModifierCreateInverted);

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "modifier", "add", target, reformer],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Creating modifier..."));
        if (result?.ExitCode == 0)
        {
            ModifierCreateName = string.Empty;
            ModifierCreateTargetRelation = target;
            await RefreshBindsAndModifiersAfterChangeAsync();
            await ShowBindAsync(target);
            ModifierListItem? created = ModifierItems.FirstOrDefault(v => string.Equals(v.Name, modifier, StringComparison.OrdinalIgnoreCase));
            if (created != null)
            {
                created.IsSelected = true;
                SelectedModifierItem = created;
            }
            StatusText = T("Modifier created for relation '{0}'.", target);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedModifiersEverywhereAsync()
    {
        List<string> selectedModifiers = GetSelectedModifierNames();
        if (selectedModifiers.Count == 0)
        {
            StatusText = T("Select at least one modifier to delete.");
            return;
        }

        int removedTemporary = 0;
        foreach (string selected in selectedModifiers.ToList())
        {
            if (!pendingModifiersByName.Remove(selected))
                continue;

            removedTemporary++;
            ModifierListItem? pendingItem = ModifierItems.FirstOrDefault(v =>
                v.IsPending && string.Equals(v.Name, selected, StringComparison.OrdinalIgnoreCase));
            if (pendingItem != null)
                ModifierItems.Remove(pendingItem);
        }

        Dictionary<string, HashSet<string>> removeByRelation = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModifierListItem item in ModifierItems.Where(v => selectedModifiers.Contains(v.Name, StringComparer.OrdinalIgnoreCase)))
        {
            List<string> relations = ParseModifierRelationNames(item.UsedByFull);
            foreach (string relation in relations)
            {
                if (!removeByRelation.TryGetValue(relation, out HashSet<string>? modifiers))
                {
                    modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    removeByRelation[relation] = modifiers;
                }
                modifiers.Add(item.Name);
            }
        }

        if (removeByRelation.Count == 0)
        {
            StatusText = removedTemporary > 0
                ? T("Removed {0} temporary modifier(s).", removedTemporary.ToString(CultureInfo.InvariantCulture))
                : T("Selected modifier(s) are not attached to any relation.");
            RefreshModifierValidationState();
            return;
        }

        if (!await ConfirmAsync(T("Delete {0} everywhere?", string.Join(", ", selectedModifiers)),
                T("The modifier comes off {0} bind(s); the binds stay. Undo (Ctrl+Z) puts it back.", removeByRelation.Count.ToString(CultureInfo.InvariantCulture)),
                T("Delete"), isDanger: true))
            return;

        int successCount = 0;
        List<string> failed = [];
        foreach ((string relation, HashSet<string> modifiers) in removeByRelation.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<string> args = ["binds", "modifier", "remove", relation];
            args.AddRange(modifiers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
            EngineCommandResult? result = await RunEngineAsync(
                args,
                includeProfile: true,
                mutatesProfile: true,
                busyLabel: "Removing modifier(s) from " + relation + "...");
            if (result?.ExitCode == 0)
                successCount++;
            else
                failed.Add(relation);
        }

        await RefreshBindsAndModifiersAfterChangeAsync();

        if (failed.Count == 0)
        {
            StatusText = T("Removed selected modifier(s) from {0} relation(s).", successCount.ToString(CultureInfo.InvariantCulture));
            if (removedTemporary > 0)
                StatusText += T(" Also removed {0} temporary modifier(s).", removedTemporary.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            StatusText = T("Removed from {0} relation(s), failed on {1}.", successCount.ToString(CultureInfo.InvariantCulture), failed.Count.ToString(CultureInfo.InvariantCulture));
            if (removedTemporary > 0)
                StatusText += T(" Temporary removed: {0}.", removedTemporary.ToString(CultureInfo.InvariantCulture));
        }
    }

    [RelayCommand]
    private async Task ApplySelectedModifiersToBindAsync() => await AddModifierToSelectedBindAsync();

    [RelayCommand]
    private async Task DetachSelectedModifiersFromBindAsync() => await RemoveModifierFromSelectedBindAsync();

    [RelayCommand]
    private void ClearModifierSelection()
    {
        foreach (ModifierListItem item in ModifierItems)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void OpenModifierManager() => CurrentStage = UiStage.Modifiers;

    [RelayCommand]
    private async Task RenameModifierAsync()
    {
        if (string.IsNullOrWhiteSpace(ModifierRenameOld) || string.IsNullOrWhiteSpace(ModifierRenameNew))
        {
            StatusText = T("Modifier rename needs old and new names.");
            return;
        }

        string oldName = ModifierRenameOld.Trim();
        string newName = ModifierRenameNew.Trim();
        if (pendingModifiersByName.TryGetValue(oldName, out PendingModifierDefinition pending))
        {
            if (pendingModifiersByName.ContainsKey(newName) ||
                ModifierItems.Any(v => !v.IsPending && string.Equals(v.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = T("Modifier '{0}' already exists.", newName);
                return;
            }

            pendingModifiersByName.Remove(oldName);
            pendingModifiersByName[newName] = pending with { Name = newName };
            await RefreshModifiersAsync();
            ModifierRenameOld = newName;
            ModifierRenameNew = string.Empty;
            StatusText = T("Temporary modifier renamed.");
            return;
        }

        List<string> args =
        [
            "binds",
            "modifier",
            "rename",
            oldName,
            newName
        ];
        if (SelectedBind != null)
            args.Add(SelectedBind.RelationName);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true, busyLabel: N("Renaming modifier..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task ImportBindsAsync()
    {
        if (string.IsNullOrWhiteSpace(BindsImportPath))
        {
            StatusText = T("Set binds import path first.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "import", BindsImportPath.Trim()],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Importing binds..."));
        if (result?.ExitCode == 0)
        {
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task ExportBindsAsync()
    {
        if (string.IsNullOrWhiteSpace(BindsExportPath))
        {
            StatusText = T("Set binds export path first.");
            return;
        }

        await RunEngineAsync(
            ["binds", "export", BindsExportPath.Trim()],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Exporting binds..."));
    }

    [RelayCommand]
    private void ToggleVisualAssignMode() => OpenVisualAssignMode = !OpenVisualAssignMode;

    [RelayCommand]
    private async Task RecordNextInputAsync()
    {
        if (IsRecordingInput)
        {
            StatusText = T("Input recording is already running.");
            return;
        }

        // The relation the recorded input goes to: the one selected when Record was pressed.
        string target = (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            RecordStatusText = T("Pick a relation on the left first.");
            StatusText = T("Select a bind or relation first.");
            JoyProLog.Info("Record", "Record pressed without a selected relation");
            return;
        }

        int timeoutMs = ParseOrDefault(RecordTimeoutMs, 6000, 500, 120000);
        int warmupMs = ParseOrDefault(RecordWarmupMs, 300, 0, 5000);
        int thresholdPercent = ParseOrDefault(RecordAxisThresholdPercent, 30, 1, 100);
        bool axisMode = IsSelectedTargetAxisMode();

        JoystickCaptureService.CaptureOptions options = new()
        {
            AxisMode = axisMode,
            // Typing moves an analog keyboard's axes: not an axis move unless the user opted the device in.
            IgnoreAxesOf = AxisIgnorePredicate(),
            PreferredDevice = BindDeviceInput,
            TimeoutMs = timeoutMs,
            WarmupMs = warmupMs,
            AxisThresholdPercent = thresholdPercent,
            RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons
        };

        recordInputCts?.Dispose();
        recordInputCts = new CancellationTokenSource();

        try
        {
            IsRecordingInput = true;
            JoyProLog.Info("Record", "Record started for '" + target + "' (" + (axisMode ? "axis" : "button") + ", " +
                                     (SelectedBind?.HasBind == true ? "bound to " + SelectedBind.Device + " :: " + SelectedBind.Input : "unbound") + ")");
            RecordStatusText = axisMode
                ? T("Recording axis... move one axis at least {0}%.", thresholdPercent.ToString(CultureInfo.InvariantCulture))
                : T("Recording button/POV... press the next control.");
            StatusText = T("Waiting for input...");

            JoystickCaptureService.CaptureResult? capture = await joystickCaptureService.CaptureNextAsync(options, recordInputCts.Token);
            string captureDiagnostics = joystickCaptureService.LastDiagnostics;
            if (capture == null)
            {
                RecordStatusText = axisMode
                    ? T("No axis moved far enough within {0} s (this relation is an axis: buttons do not count).", (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture))
                    : T("No button or hat pressed within {0} s.", (timeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(captureDiagnostics))
                    RecordStatusText += " | " + captureDiagnostics;
                StatusText = T("Record timed out.");
                JoyProLog.Info("Record", "Record for '" + target + "' got no input; " + captureDiagnostics);
                return;
            }

            EnsureQuickBindOptionContains(capture.Device, capture.Input);
            BindDeviceInput = capture.Device;
            BindInputValue = capture.Input;
            UpdateRecordTargetContext();
            string captured = capture.Device + " :: " + capture.Input;

            // Record binds: the input goes straight to the relation, as the page promises. When the selection moved
            // on while recording, the fields hold the input and Set Bind applies it.
            string selectedNow = (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();
            if (!string.Equals(selectedNow, target, StringComparison.OrdinalIgnoreCase))
            {
                RecordStatusText = T("Captured: {0} (not bound: the selection changed to '{1}'; press Set Bind to use it there).", captured, selectedNow);
                StatusText = T("Input captured.");
                JoyProLog.Info("Record", "Captured " + captured + " for '" + target + "' but the selection changed to '" + selectedNow + "'; not bound");
                return;
            }

            RecordStatusText = T("Captured: {0}. Binding...", captured);
            bool bound = await SetBindFromEditorAsync(target);
            if (bound)
            {
                RecordStatusText = T("Bound '{0}' to {1}.", target, captured);
                StatusText = T("Bound {0} to {1}.", target, capture.Input);
                JoyProLog.Info("Record", "Bound '" + target + "' to " + captured);
            }
            else
            {
                RecordStatusText = T("Captured: {0}, but binding failed: {1}", captured, StatusText);
                JoyProLog.Warn("Record", "Captured " + captured + " for '" + target + "' but binds set failed: " + StatusText);
            }
        }
        catch (OperationCanceledException)
        {
            RecordStatusText = T("Recording canceled.");
            StatusText = T("Input recording canceled.");
            JoyProLog.Info("Record", "Record for '" + target + "' canceled");
        }
        catch (Exception ex)
        {
            RecordStatusText = T("Recording failed: {0}", ex.Message);
            StatusText = T("Input recording failed.");
            JoyProLog.Error("Record", "Record for '" + target + "' failed", ex);
        }
        finally
        {
            IsRecordingInput = false;
            recordInputCts?.Dispose();
            recordInputCts = null;
        }
    }

    [RelayCommand]
    private async Task RecordModifierInputAsync()
    {
        if (IsRecordingInput)
        {
            StatusText = T("Input recording is already running.");
            return;
        }

        int timeoutMs = ParseOrDefault(RecordTimeoutMs, 6000, 500, 120000);
        int warmupMs = ParseOrDefault(RecordWarmupMs, 300, 0, 5000);
        int thresholdPercent = ParseOrDefault(RecordAxisThresholdPercent, 30, 1, 100);

        JoystickCaptureService.CaptureOptions options = new()
        {
            AxisMode = false,
            IncludeKeyboard = true,
            PreferredDevice = ModifierCreateDevice,
            TimeoutMs = timeoutMs,
            WarmupMs = warmupMs,
            AxisThresholdPercent = thresholdPercent,
            RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons
        };

        recordInputCts?.Dispose();
        recordInputCts = new CancellationTokenSource();

        try
        {
            IsRecordingInput = true;
            RecordStatusText = T("Recording modifier input (keyboard or device)... press the next control.");
            StatusText = T("Waiting for modifier input...");

            JoystickCaptureService.CaptureResult? capture = await joystickCaptureService.CaptureNextAsync(options, recordInputCts.Token);
            string captureDiagnostics = joystickCaptureService.LastDiagnostics;
            if (capture == null)
            {
                RecordStatusText = T("No modifier input captured (timeout).");
                if (!string.IsNullOrWhiteSpace(captureDiagnostics))
                    RecordStatusText += " | " + captureDiagnostics;
                StatusText = T("Modifier record timed out.");
                return;
            }

            ModifierCreateDevice = capture.Device;
            ModifierCreateInput = capture.Input;
            RecordStatusText = "Modifier captured: " + capture.Device + " :: " + capture.Input;
            if (!string.IsNullOrWhiteSpace(captureDiagnostics))
                RecordStatusText += " | " + captureDiagnostics;
            StatusText = T("Modifier input captured and ready to create.");
            EnsureQuickBindOptionContains(capture.Device, capture.Input);
            ModifierCreateDevice = capture.Device;
            ModifierCreateInput = capture.Input;

            if (ModifierItems.Count == 0 && !string.IsNullOrWhiteSpace(ProfilePath))
                await RefreshModifiersAsync();

            (string Name, string Relation, string Reformer)? duplicate = await FindDuplicateModifierAnywhereAsync(capture.Device, capture.Input);
            if (duplicate.HasValue)
            {
                string relationSuffix = string.IsNullOrWhiteSpace(duplicate.Value.Relation)
                    ? string.Empty
                    : T(" in relation '{0}'", duplicate.Value.Relation);
                StatusText = T("Warning: input already used by modifier '{0}'{1}.", duplicate.Value.Name, relationSuffix);
                RecordStatusText = "Modifier captured: " + capture.Device + " :: " + capture.Input +
                                   " | Duplicate detected: " + duplicate.Value.Reformer;
                WarningRequested?.Invoke(StatusText);
            }
        }
        catch (OperationCanceledException)
        {
            RecordStatusText = T("Modifier recording canceled.");
            StatusText = T("Modifier recording canceled.");
        }
        catch (Exception ex)
        {
            RecordStatusText = T("Modifier recording failed: {0}", ex.Message);
            StatusText = T("Modifier recording failed.");
            JoyProLog.Error("Record", "Modifier record failed", ex);
        }
        finally
        {
            IsRecordingInput = false;
            recordInputCts?.Dispose();
            recordInputCts = null;
        }
    }

    [RelayCommand]
    private void CancelRecordInput()
    {
        if (!IsRecordingInput)
        {
            StatusText = T("No active input recording.");
            return;
        }

        JoyProLog.Info("Record", "Cancel Record pressed");
        recordInputCts?.Cancel();
    }

    [RelayCommand]
    private async Task SuggestLayoutsAsync()
    {
        List<string> args = ["layout", "suggest"];
        List<string> explicitDevices = ParseCsv(LayoutDevicesCsv);
        foreach (string device in explicitDevices)
            args.AddRange(["--device", device]);
        bool hasProfile = !string.IsNullOrWhiteSpace(ProfilePath);
        bool includeConnected = LayoutSuggestIncludeConnected;
        bool includeProfileDevices = LayoutSuggestIncludeProfileDevices && hasProfile;
        bool applyToProfile = LayoutSuggestApplyToProfile && hasProfile;

        List<string> notes = [];
        if (LayoutSuggestIncludeProfileDevices && !hasProfile)
            notes.Add(T("Profile devices skipped (no profile loaded)."));
        if (LayoutSuggestApplyToProfile && !hasProfile)
            notes.Add(T("Apply skipped (no profile loaded)."));
        if (!includeConnected && !includeProfileDevices && explicitDevices.Count == 0)
        {
            includeConnected = true;
            notes.Add(T("No device source selected; using connected DirectInput devices."));
        }

        if (includeConnected)
            args.Add("--connected");
        if (includeProfileDevices)
            args.Add("--profile-devices");
        foreach (string dir in ParseCsv(LayoutLegacyLayoutDirs))
            args.AddRange(["--legacy-layout-dir", dir]);
        foreach (string dir in ParseCsv(LayoutLegacyOutputDirs))
            args.AddRange(["--legacy-output-dir", dir]);
        if (!string.IsNullOrWhiteSpace(LayoutCatalogPath))
            args.AddRange(["--catalog", LayoutCatalogPath.Trim()]);
        args.AddRange(["--apply", applyToProfile ? "true" : "false"]);
        args.AddRange(["--apply-min-score", "55"]);

        bool includeProfile = includeProfileDevices || applyToProfile;
        if (notes.Count > 0)
            LayoutSuggestStatus = string.Join(" ", notes);
        EngineCommandResult? result = await RunEngineAsync(
            args,
            includeProfile: includeProfile,
            mutatesProfile: applyToProfile,
            busyLabel: N("Suggesting layouts..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        if (!TryParseLayoutSuggestions(result.StandardOutput, out List<LayoutSuggestionItem> parsed))
        {
            LayoutSuggestStatus = T("Layout suggestions returned, but could not parse JSON payload.");
            return;
        }
        // Interfaces of a device that report no input at all (a keyboard's other HID interfaces) are no device to show.
        if (includeConnected)
        {
            if (!referenceDevicesLoaded)
                await LoadDeviceReferenceDevicesAsync();
            parsed.RemoveAll(p => DeviceReferenceDevices.FirstOrDefault(d => string.Equals(d.Device, p.Device, StringComparison.OrdinalIgnoreCase)) is { } d &&
                                  d.Probe.Buttons == 0 && d.Probe.Hats == 0 && (d.Probe.Axes == null || d.Probe.Axes.Count == 0));
            // One device with several interfaces (a keyboard's gamepad mode next to its key interfaces): the interface
            // with axes or hats is the game device, the others are not listed.
            static bool GameLike(ReferenceDeviceItem d) => d.Probe.Hats > 0 || d.Probe.Axes is { Count: > 0 };
            List<ReferenceDeviceItem> listed = parsed.Select(p => DeviceReferenceDevices.FirstOrDefault(d => string.Equals(d.Device, p.Device, StringComparison.OrdinalIgnoreCase)))
                .OfType<ReferenceDeviceItem>().ToList();
            HashSet<string> drop = new(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, ReferenceDeviceItem> same in listed.GroupBy(d => d.Probe.VidPid + "|" + d.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Any(GameLike)))
            {
                foreach (ReferenceDeviceItem other in same.Where(d => !GameLike(d)))
                    drop.Add(other.Device);
            }
            parsed.RemoveAll(p => drop.Contains(p.Device));
        }

        // The device list of the Layout page keeps the devices of earlier answers (a search for the cheat-sheet devices
        // only answers for those) and the picked device stays picked.
        string previouslySelected = SelectedLayoutSuggestion?.Device ?? string.Empty;
        List<LayoutSuggestionItem> kept = LayoutSuggestions
            .Where(old => !parsed.Any(p => string.Equals(p.Device, old.Device, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        LayoutSuggestions.Clear();
        foreach (LayoutSuggestionItem item in parsed.Concat(kept))
            LayoutSuggestions.Add(item);
        if (previouslySelected.Length > 0)
            SelectedLayoutSuggestion = LayoutSuggestions.FirstOrDefault(s => string.Equals(s.Device, previouslySelected, StringComparison.OrdinalIgnoreCase));
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        UpdateLayoutMapInputFromRememberedMappings(BuildEffectiveLayoutDeviceFilter());

        LayoutSuggestStatus = parsed.Count == 0
            ? T("No layout suggestions found.")
            : string.Empty;

        if (applyToProfile)
            await RefreshRelationsAsync();
    }

    private async Task RefreshLayoutStageDataAsync()
    {
        layoutStageRefreshing = true;
        try
        {
            await CheckOldJoyProLayoutsAsync();
            await RefreshDevicesAsync();
            await SuggestLayoutsAsync();
        }
        finally
        {
            layoutStageRefreshing = false;
        }
        // The page shows a device at once: the one of a device card clicked on the Start page, the picked one, else the
        // first.
        if (layoutDeviceToShow is { Length: > 0 } device)
        {
            layoutDeviceToShow = null;
            await ShowWorkspaceDeviceAsync(device);
            return;
        }
        PickFirstLayoutDevice();
    }

    private async Task RefreshExportStageDataAsync()
    {
        await RefreshDevicesAsync();
        await RefreshDbGamesAsync();
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        RebuildLayoutGameOptions(BuildLayoutGameCandidates());
    }

    [RelayCommand]
    private void ApplySelectedLayoutSuggestions()
    {
        List<LayoutSuggestionItem> selected = LayoutSuggestions
            .Where(v => v.IsSelected && !string.IsNullOrWhiteSpace(v.LayoutPath))
            .ToList();

        if (selected.Count == 0 && SelectedLayoutSuggestion != null && !string.IsNullOrWhiteSpace(SelectedLayoutSuggestion.LayoutPath))
            selected.Add(SelectedLayoutSuggestion);
        if (selected.Count == 0)
            selected = LayoutSuggestions
                .Where(v => !string.IsNullOrWhiteSpace(v.LayoutPath))
                .ToList();

        if (selected.Count == 0)
        {
            LayoutSuggestStatus = T("No suggestion with layout path available to apply.");
            return;
        }

        List<string> deviceNames = selected
            .Select(v => (v.Device ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (LayoutSuggestionItem item in selected)
            RememberLayoutDeviceMapping(item.Device, item.LayoutPath);

        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        SetLayoutDeviceSelections(deviceNames);
        UpdateLayoutMapInputFromRememberedMappings(deviceNames);

        LayoutSuggestStatus = T("The cheat sheets use the picked picture for {0}{1}", selected.Count.ToString(CultureInfo.InvariantCulture), (selected.Count == 1 ? " device." : " devices."));
    }

    [RelayCommand]
    private async Task OpenSelectedLayoutInEditorAsync()
    {
        string source = ResolveLayoutEditorSourcePath();
        if (string.IsNullOrWhiteSpace(source))
        {
            LayoutEditorStatus = T("Select a suggestion (or set source path) first.");
            StatusText = T("No layout source selected for editor.");
            return;
        }

        layoutEditorContextDevice = ResolveLayoutBackdropSearchDevice();
        if (string.IsNullOrWhiteSpace(layoutEditorContextDevice) && SelectedLayoutSuggestion != null)
            layoutEditorContextDevice = (SelectedLayoutSuggestion.Device ?? string.Empty).Trim();
        if (SelectedLayoutSuggestion != null)
            RememberLayoutDeviceMapping(layoutEditorContextDevice, SelectedLayoutSuggestion.LayoutPath);
        else
            RememberLayoutDeviceMapping(layoutEditorContextDevice, source);

        LayoutEditorSourcePath = source;
        if (string.IsNullOrWhiteSpace(LayoutEditorOutputPath))
            LayoutEditorOutputPath = BuildDefaultLayoutEditorOutputPath(source);

        await LoadLayoutEditorSourceAsync();
    }

    // Opens an image search for the device's vendor and model (from VID:PID and HID name, see "layout identify").
    [RelayCommand]
    private async Task OpenLayoutBackdropWebSearchAsync()
    {
        string device = ResolveLayoutBackdropSearchDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            LayoutEditorStatus = T("Select a device in the suggestion list first.");
            StatusText = T("No device selected for the picture search.");
            return;
        }

        string url = SelectedLayoutSuggestion != null && string.Equals(SelectedLayoutSuggestion.Device, device, StringComparison.OrdinalIgnoreCase)
            ? SelectedLayoutSuggestion.SearchUrl
            : string.Empty;
        string model = SelectedLayoutSuggestion?.Model ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            EngineCommandResult? result = await RunEngineAsync(["layout", "identify", "--device", device], includeProfile: false, mutatesProfile: false, busyLabel: N("Identifying device..."), forceText: false);
            if (result == null || result.ExitCode != 0)
                return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
                if (doc.RootElement.TryGetProperty("Devices", out JsonElement devicesEl) && devicesEl.ValueKind == JsonValueKind.Array && devicesEl.GetArrayLength() > 0)
                {
                    url = GetJsonString(devicesEl[0], "SearchUrl");
                    model = GetJsonString(devicesEl[0], "SearchQuery");
                }
            }
            catch (JsonException)
            {
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            LayoutEditorStatus = T("Could not identify the device model.");
            return;
        }

        try
        {
            ExternalProcess.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            LayoutEditorStatus = T("Image search opened for: {0}. Save a good picture (or copy its image address) and use 'Use Picture'.", model);
            StatusText = T("Picture search opened in the browser.");
        }
        catch (Exception ex)
        {
            LayoutEditorStatus = T("Could not open browser for backdrop search: {0}", ex.Message);
            StatusText = T("Backdrop search failed to open browser.");
        }
    }

    [RelayCommand]
    private async Task LoadLayoutEditorSourceAsync()
    {
        string source = NormalizeUiPath(LayoutEditorSourcePath);
        if (string.IsNullOrWhiteSpace(source))
        {
            LayoutEditorStatus = T("Layout editor source path is empty.");
            StatusText = T("Set a layout source path first.");
            return;
        }

        if (!File.Exists(source))
        {
            LayoutEditorStatus = T("Source file not found: {0}", source);
            StatusText = T("Layout source file missing.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["layout", "template-inspect", "--source", source, "--max-markers", "10000"],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: N("Loading layout template into editor..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        if (!TryParseLayoutTemplateInspect(result.StandardOutput, out LayoutTemplateInspectDocument inspectDoc, out string parseError))
        {
            LayoutEditorStatus = T("Could not parse layout inspect payload: {0}", parseError);
            StatusText = T("Layout editor inspect payload parse failed.");
            return;
        }

        LayoutEditorSourcePath = NormalizeUiPath(inspectDoc.SourcePath);
        if (string.IsNullOrWhiteSpace(LayoutEditorOutputPath))
            LayoutEditorOutputPath = BuildDefaultLayoutEditorOutputPath(LayoutEditorSourcePath);
        LoadLayoutEditorPreview(inspectDoc.PreviewImagePath, inspectDoc.Width, inspectDoc.Height);
        ApplyLayoutTextStyle(inspectDoc.FontName, inspectDoc.FontSize, inspectDoc.TextColor);
        LayoutSourceCredit = (inspectDoc.Credit ?? string.Empty).Trim();

        LayoutEditorMarkers.Clear();
        foreach (LayoutEditorMarkerItem marker in inspectDoc.Markers)
            LayoutEditorMarkers.Add(marker);
        LayoutEditorTables.Clear();
        foreach (LayoutEditorTableItem table in inspectDoc.Tables)
            LayoutEditorTables.Add(table);
        LayoutEditorConnectors.Clear();
        foreach (LayoutEditorConnectorItem connector in inspectDoc.Connectors)
            LayoutEditorConnectors.Add(connector);
        layoutEditorConnectorSourcePoint = null;
        LayoutEditorConnectorSourcePointLabel = "<none>";
        if (LayoutEditorTables.Count == 0 && LayoutEditorMarkers.Count > 0)
        {
            foreach (LayoutEditorTableItem table in BuildDefaultLayoutEditorTables(LayoutEditorCanvasWidth, LayoutEditorCanvasHeight, LayoutEditorMarkers))
                LayoutEditorTables.Add(table);
        }

        RebuildLayoutEditorInputOptions();
        RebuildLayoutEditorMarkerTokenOptions();
        RebuildLayoutEditorModifierOptions();
        SelectedLayoutEditorMarker = LayoutEditorMarkers.FirstOrDefault();
        SelectedLayoutEditorTable = LayoutEditorTables.FirstOrDefault();
        SelectedLayoutEditorConnector = LayoutEditorConnectors.FirstOrDefault();
        LayoutEditorStatus =
            T("Loaded {0} marker(s), {1} table overlay(s), {2} connector(s) from {3}.", LayoutEditorMarkers.Count.ToString(CultureInfo.InvariantCulture), LayoutEditorTables.Count.ToString(CultureInfo.InvariantCulture), LayoutEditorConnectors.Count.ToString(CultureInfo.InvariantCulture), Path.GetFileName(LayoutEditorSourcePath));
        StatusText = T("Layout editor loaded template.");
        ClearLayoutUndo();
        RefreshLayoutMarkerCommands();
        RebuildWorkspacePictures();
        NotifyWorkspacePicture();
    }

    [RelayCommand]
    private void AddLayoutEditorMarker()
    {
        AddLayoutEditorMarkerAt(LayoutEditorCanvasWidth * 0.5, LayoutEditorCanvasHeight * 0.5);
    }

    public void AddLayoutEditorMarkerAt(double x, double y)
    {
        SyncLayoutEditorModifierFromOptionSelection();

        string baseToken = (LayoutEditorNewToken ?? string.Empty).Trim();
        if (baseToken.Length == 0)
            baseToken = "JOY_BTN" + (LayoutEditorMarkers.Count + 1).ToString(CultureInfo.InvariantCulture);

        string token = EnsureUniqueLayoutEditorToken(baseToken);
        string type = (LayoutEditorNewType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Length == 0)
            type = "button";

        LayoutEditorMarkerItem marker = new()
        {
            Token = token,
            Type = type,
            Modifier = (LayoutEditorNewModifier ?? string.Empty).Trim(),
            X = Math.Round(Math.Max(0, Math.Min(LayoutEditorCanvasWidth - 4, x)), 2),
            Y = Math.Round(Math.Max(0, Math.Min(LayoutEditorCanvasHeight - 4, y)), 2)
        };
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length > 0)
            marker.Command = FindRelationBoundTo(device, marker.Token, marker.Modifier);
        PushLayoutUndo("add " + marker.ShortLabel);
        LayoutEditorMarkers.Add(marker);
        SelectedLayoutEditorMarker = marker;
        LayoutEditorStatus = T("Added marker '{0}'. Drag it to the desired position.", token);
    }

    [RelayCommand]
    private void RemoveSelectedLayoutEditorMarker()
    {
        if (SelectedLayoutEditorMarker == null)
        {
            LayoutEditorStatus = T("Select a marker first.");
            return;
        }

        LayoutEditorMarkerItem marker = SelectedLayoutEditorMarker;
        PushLayoutUndo("remove " + marker.ShortLabel);
        LayoutEditorMarkers.Remove(marker);
        SelectedLayoutEditorMarker = LayoutEditorMarkers.FirstOrDefault();
        LayoutEditorStatus = T("Removed marker '{0}'.", marker.Token);
    }

    [RelayCommand]
    private void ApplySelectedLayoutEditorMarkerDraft()
    {
        if (SelectedLayoutEditorMarker == null)
        {
            LayoutEditorStatus = T("Select a marker first.");
            return;
        }

        SyncLayoutEditorModifierFromOptionSelection();

        string token = (LayoutEditorNewToken ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            LayoutEditorStatus = T("Marker token cannot be empty.");
            return;
        }

        bool tokenUsedByOther = LayoutEditorMarkers.Any(v =>
            !ReferenceEquals(v, SelectedLayoutEditorMarker) &&
            string.Equals(v.Token, token, StringComparison.OrdinalIgnoreCase));
        if (tokenUsedByOther)
        {
            LayoutEditorStatus = T("Marker token already exists: {0}", token);
            return;
        }

        PushLayoutUndo("edit " + SelectedLayoutEditorMarker.ShortLabel);
        SelectedLayoutEditorMarker.Token = token;
        SelectedLayoutEditorMarker.Type = string.IsNullOrWhiteSpace(LayoutEditorNewType)
            ? "button"
            : LayoutEditorNewType.Trim().ToLowerInvariant();
        SelectedLayoutEditorMarker.Modifier = (LayoutEditorNewModifier ?? string.Empty).Trim();
        RefreshLayoutMarkerCommands();
        LayoutEditorStatus = T("Updated marker '{0}'.", token);
    }

    [RelayCommand]
    private void AddLayoutEditorTable()
    {
        AddLayoutEditorTableAt(LayoutEditorCanvasWidth * 0.68, LayoutEditorCanvasHeight * 0.08);
    }

    public void AddLayoutEditorTableAt(double x, double y)
    {
        string tableName = (LayoutEditorNewTableName ?? string.Empty).Trim();
        if (tableName.Length == 0)
            tableName = "Table " + (LayoutEditorTables.Count + 1).ToString(CultureInfo.InvariantCulture);

        double width = ParseOrDefault(LayoutEditorNewTableWidth, 360, 120, 1800);
        double height = ParseOrDefault(LayoutEditorNewTableHeight, 220, 70, 1200);
        int columns = ParseOrDefault(LayoutEditorNewTableColumns, 1, 1, 6);
        string mode = NormalizeLayoutTableMode(LayoutEditorNewTableMode);
        string backgroundColor = NormalizeLayoutTableBackgroundColor(LayoutEditorNewTableBackgroundColor);
        double backgroundOpacity = ParseDoubleOrDefault(LayoutEditorNewTableBackgroundOpacity, 0.93, 0, 1);
        bool dropShadow = LayoutEditorNewTableDropShadow;

        LayoutEditorTableItem table = new()
        {
            Name = tableName,
            Mode = mode,
            X = Math.Round(Math.Max(0, Math.Min(LayoutEditorCanvasWidth - 30, x)), 2),
            Y = Math.Round(Math.Max(0, Math.Min(LayoutEditorCanvasHeight - 30, y)), 2),
            Width = Math.Round(width, 2),
            Height = Math.Round(height, 2),
            Columns = columns,
            BackgroundColor = backgroundColor,
            BackgroundOpacity = backgroundOpacity,
            DropShadow = dropShadow
        };
        LayoutEditorTables.Add(table);
        SelectedLayoutEditorTable = table;
        LayoutEditorStatus = T("Added table '{0}'. Drag to position and edit size in the table list.", table.Name);
    }

    [RelayCommand]
    private void RemoveSelectedLayoutEditorTable()
    {
        if (SelectedLayoutEditorTable == null)
        {
            LayoutEditorStatus = T("Select a table overlay first.");
            return;
        }

        LayoutEditorTableItem table = SelectedLayoutEditorTable;
        LayoutEditorTables.Remove(table);
        SelectedLayoutEditorTable = LayoutEditorTables.FirstOrDefault();
        LayoutEditorStatus = T("Removed table '{0}'.", table.Name);
    }

    [RelayCommand]
    private void ApplySelectedLayoutEditorTableDraft()
    {
        if (SelectedLayoutEditorTable == null)
        {
            LayoutEditorStatus = T("Select a table overlay first.");
            return;
        }

        string tableName = (LayoutEditorNewTableName ?? string.Empty).Trim();
        if (tableName.Length == 0)
            tableName = "Table";

        double width = ParseOrDefault(LayoutEditorNewTableWidth, 360, 120, 1800);
        double height = ParseOrDefault(LayoutEditorNewTableHeight, 220, 70, 1200);
        int columns = ParseOrDefault(LayoutEditorNewTableColumns, 1, 1, 6);
        string mode = NormalizeLayoutTableMode(LayoutEditorNewTableMode);
        string backgroundColor = NormalizeLayoutTableBackgroundColor(LayoutEditorNewTableBackgroundColor);
        double backgroundOpacity = ParseDoubleOrDefault(LayoutEditorNewTableBackgroundOpacity, 0.93, 0, 1);
        bool dropShadow = LayoutEditorNewTableDropShadow;

        SelectedLayoutEditorTable.Name = tableName;
        SelectedLayoutEditorTable.Mode = mode;
        SelectedLayoutEditorTable.Width = Math.Round(width, 2, MidpointRounding.AwayFromZero);
        SelectedLayoutEditorTable.Height = Math.Round(height, 2, MidpointRounding.AwayFromZero);
        SelectedLayoutEditorTable.Columns = columns;
        SelectedLayoutEditorTable.BackgroundColor = backgroundColor;
        SelectedLayoutEditorTable.BackgroundOpacity = backgroundOpacity;
        SelectedLayoutEditorTable.DropShadow = dropShadow;
        LayoutEditorStatus = T("Updated table '{0}' ({1}).", tableName, mode);
    }

    [RelayCommand]
    private void AddLayoutEditorConnector()
    {
        string name = (LayoutEditorNewConnectorName ?? string.Empty).Trim();
        if (name.Length == 0)
            name = "Connector " + (LayoutEditorConnectors.Count + 1).ToString(CultureInfo.InvariantCulture);
        name = EnsureUniqueLayoutConnectorName(name);

        string color = (LayoutEditorNewConnectorColor ?? string.Empty).Trim();
        if (color.Length == 0)
            color = "#E2E8F0";
        double width = ParseOrDefault(LayoutEditorNewConnectorWidth, 2, 1, 12);
        string route = NormalizeLayoutConnectorRoute(LayoutEditorNewConnectorRoute);
        int steps = ParseOrDefault(LayoutEditorNewConnectorSteps, 2, 0, 8);
        string targetToken = (LayoutEditorNewConnectorTargetInput ?? string.Empty).Trim();
        if (targetToken.Length == 0 && SelectedLayoutEditorMarker != null && !string.IsNullOrWhiteSpace(SelectedLayoutEditorMarker.Token))
            targetToken = SelectedLayoutEditorMarker.Token.Trim();

        List<Point> points = BuildDefaultConnectorPoints();
        if (points.Count < 2)
        {
            LayoutEditorStatus = T("Select a target input marker and set source point (or provide at least two connector points).");
            return;
        }

        LayoutEditorConnectorItem connector = new()
        {
            Name = name,
            Color = color,
            StrokeWidth = width,
            Route = route,
            Steps = steps,
            TargetToken = targetToken
        };
        connector.SetPoints(points);
        connector.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(points);

        LayoutEditorConnectors.Add(connector);
        SelectedLayoutEditorConnector = connector;
        LayoutEditorStatus = T("Added connector '{0}'.", name);
    }

    [RelayCommand]
    private void RemoveSelectedLayoutEditorConnector()
    {
        if (SelectedLayoutEditorConnector == null)
        {
            LayoutEditorStatus = T("Select a connector first.");
            return;
        }

        LayoutEditorConnectorItem connector = SelectedLayoutEditorConnector;
        LayoutEditorConnectors.Remove(connector);
        SelectedLayoutEditorConnector = LayoutEditorConnectors.FirstOrDefault();
        LayoutEditorStatus = T("Removed connector '{0}'.", connector.Name);
    }

    [RelayCommand]
    private void ApplySelectedLayoutEditorConnectorDraft()
    {
        if (SelectedLayoutEditorConnector == null && LayoutEditorConnectors.Count == 1)
            SelectedLayoutEditorConnector = LayoutEditorConnectors[0];

        if (SelectedLayoutEditorConnector == null)
        {
            LayoutEditorStatus = T("Select a connector first.");
            return;
        }

        string name = (LayoutEditorNewConnectorName ?? string.Empty).Trim();
        if (name.Length == 0)
            name = "Connector";
        bool duplicateName = LayoutEditorConnectors.Any(v =>
            !ReferenceEquals(v, SelectedLayoutEditorConnector) &&
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicateName)
        {
            LayoutEditorStatus = T("Connector name already exists: {0}", name);
            return;
        }

        string color = (LayoutEditorNewConnectorColor ?? string.Empty).Trim();
        if (color.Length == 0)
            color = "#E2E8F0";
        double width = ParseOrDefault(LayoutEditorNewConnectorWidth, 2, 1, 12);
        string route = NormalizeLayoutConnectorRoute(LayoutEditorNewConnectorRoute);
        int steps = ParseOrDefault(LayoutEditorNewConnectorSteps, 2, 0, 8);
        string targetToken = (LayoutEditorNewConnectorTargetInput ?? string.Empty).Trim();
        if (targetToken.Length == 0 && SelectedLayoutEditorMarker != null && !string.IsNullOrWhiteSpace(SelectedLayoutEditorMarker.Token))
            targetToken = SelectedLayoutEditorMarker.Token.Trim();
        string pointsRaw = (LayoutEditorNewConnectorPoints ?? string.Empty).Trim();
        if (pointsRaw.Length == 0)
            pointsRaw = SelectedLayoutEditorConnector.PointsCsv;
        if (pointsRaw.Length == 0)
            pointsRaw = LayoutEditorConnectorItem.ToPointsCsv(BuildDefaultConnectorPoints());

        LayoutEditorConnectorItem parser = new()
        {
            PointsCsv = pointsRaw
        };
        if (!parser.TryParsePointsCsv(out List<Point> points, out string parseError))
        {
            LayoutEditorStatus = T("Connector points invalid: {0}", parseError);
            return;
        }

        List<Point> clamped = points.Select(ClampCanvasPoint).ToList();
        SelectedLayoutEditorConnector.Name = name;
        SelectedLayoutEditorConnector.Color = color;
        SelectedLayoutEditorConnector.StrokeWidth = width;
        SelectedLayoutEditorConnector.Route = route;
        SelectedLayoutEditorConnector.Steps = steps;
        SelectedLayoutEditorConnector.TargetToken = targetToken;
        SelectedLayoutEditorConnector.SetPoints(clamped);
        SelectedLayoutEditorConnector.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(clamped);
        LayoutEditorNewConnectorPoints = SelectedLayoutEditorConnector.PointsCsv;
        LayoutEditorStatus = T("Updated connector '{0}'.", name);
    }

    public void AddLayoutConnectorPointAt(double x, double y)
    {
        if (SelectedLayoutEditorConnector == null)
        {
            LayoutEditorStatus = T("Select a connector first to add points.");
            return;
        }

        Point clamped = ClampCanvasPoint(new Point(x, y));
        List<Point> points = SelectedLayoutEditorConnector.Points.ToList();
        points.Add(clamped);
        SelectedLayoutEditorConnector.Route = "manual";
        SelectedLayoutEditorConnector.SetPoints(points);
        SelectedLayoutEditorConnector.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(points);
        LayoutEditorNewConnectorRoute = SelectedLayoutEditorConnector.Route;
        LayoutEditorNewConnectorPoints = SelectedLayoutEditorConnector.PointsCsv;
        LayoutEditorStatus = T("Added connector point ({0},{1}).", clamped.X.ToString("0.##", CultureInfo.InvariantCulture), clamped.Y.ToString("0.##", CultureInfo.InvariantCulture));
    }

    public void SetLayoutEditorConnectorSourcePoint(double x, double y)
    {
        Point clamped = ClampCanvasPoint(new Point(x, y));
        layoutEditorConnectorSourcePoint = clamped;
        LayoutEditorConnectorSourcePointLabel =
            clamped.X.ToString("0.##", CultureInfo.InvariantCulture) +
            "," +
            clamped.Y.ToString("0.##", CultureInfo.InvariantCulture);
        LayoutPlaceConnectorSourceMode = false;
        LayoutEditorStatus =
            T("Connector source set to ({0},{1}).", clamped.X.ToString("0.##", CultureInfo.InvariantCulture), clamped.Y.ToString("0.##", CultureInfo.InvariantCulture));
    }

    [RelayCommand]
    private void ClearLayoutEditorConnectorSourcePoint()
    {
        layoutEditorConnectorSourcePoint = null;
        LayoutEditorConnectorSourcePointLabel = "<none>";
        LayoutEditorStatus = T("Connector source point cleared.");
    }

    [RelayCommand]
    private async Task SaveLayoutEditorTemplateAsync()
    {
        if (!CanSaveLayoutEditorTemplate)
        {
            LayoutEditorStatus = T("Set source and output paths before saving.");
            StatusText = T("Layout editor save is missing required fields.");
            return;
        }

        string source = NormalizeUiPath(LayoutEditorSourcePath);
        string output = NormalizeUiPath(LayoutEditorOutputPath);
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            LayoutEditorStatus = T("Source file not found: {0}", source);
            StatusText = T("Layout editor source is missing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            LayoutEditorStatus = T("Output path is empty.");
            StatusText = T("Layout editor output path is missing.");
            return;
        }

        string markerJsonPath = Path.Combine(
            Path.GetTempPath(),
            "joypro2-layout-editor-markers-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json");
        string tableJsonPath = Path.Combine(
            Path.GetTempPath(),
            "joypro2-layout-editor-tables-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json");
        string connectorJsonPath = Path.Combine(
            Path.GetTempPath(),
            "joypro2-layout-editor-connectors-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json");

        try
        {
            LayoutTemplateMarkerSaveEnvelope payload = new()
            {
                Markers = LayoutEditorMarkers
                    .Select(v => new LayoutTemplateMarkerSaveDto
                    {
                        Token = (v.Token ?? string.Empty).Trim(),
                        Type = (v.Type ?? string.Empty).Trim(),
                        Modifier = (v.Modifier ?? string.Empty).Trim(),
                        X = v.X,
                        Y = v.Y
                    })
                    .ToList(),
                Tables = LayoutEditorTables
                    .Select(v => new LayoutTemplateTableSaveDto
                    {
                        Name = (v.Name ?? string.Empty).Trim(),
                        Mode = NormalizeLayoutTableMode(v.Mode),
                        X = v.X,
                        Y = v.Y,
                        Width = v.Width,
                        Height = v.Height,
                        Columns = Math.Max(1, v.Columns),
                        BackgroundColor = NormalizeLayoutTableBackgroundColor(v.BackgroundColor),
                        BackgroundOpacity = Math.Clamp(v.BackgroundOpacity, 0, 1),
                        DropShadow = v.DropShadow
                    })
                    .ToList(),
                Connectors = LayoutEditorConnectors
                    .Select(v =>
                    {
                        List<Point> points = v.Points.ToList();
                        if (v.TryParsePointsCsv(out List<Point> parsedPoints, out _))
                            points = parsedPoints;
                        points = points.Select(ClampCanvasPoint).ToList();
                        v.SetPoints(points);
                        v.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(points);
                        return new LayoutTemplateConnectorSaveDto
                        {
                            Name = (v.Name ?? string.Empty).Trim(),
                            Color = (v.Color ?? string.Empty).Trim(),
                            Width = Math.Clamp(v.StrokeWidth, 1, 12),
                            Route = NormalizeLayoutConnectorRoute(v.Route),
                            Steps = Math.Clamp(v.Steps, 0, 8),
                            TargetToken = (v.TargetToken ?? string.Empty).Trim(),
                            Points = points
                                .Select(p => new LayoutTemplateConnectorPointSaveDto
                                {
                                    X = p.X,
                                    Y = p.Y
                                })
                                .ToList()
                        };
                    })
                    .ToList()
            };
            string markerJson = JsonSerializer.Serialize(payload);
            File.WriteAllText(markerJsonPath, markerJson);
            string tableJson = JsonSerializer.Serialize(payload.Tables);
            File.WriteAllText(tableJsonPath, tableJson);
            string connectorJson = JsonSerializer.Serialize(payload.Connectors);
            File.WriteAllText(connectorJsonPath, connectorJson);

            EngineCommandResult? result = await RunEngineAsync(
                [
                    "layout",
                    "template-save",
                    "--source", source,
                    "--out", output,
                    "--markers-json", markerJsonPath,
                    "--tables-json", tableJsonPath,
                    "--connectors-json", connectorJsonPath,
                    "--replace-all", "true"
                ],
                includeProfile: false,
                mutatesProfile: false,
                busyLabel: N("Saving layout template..."),
                forceText: false);
            if (result == null || result.ExitCode != 0)
                return;

            if (!TryParseLayoutTemplateSave(result.StandardOutput, out LayoutTemplateSaveDocument saveDoc, out _))
            {
                LayoutEditorStatus = T("Saved template, but save payload could not be parsed.");
                StatusText = T("Layout template saved.");
                return;
            }

            LayoutEditorOutputPath = NormalizeUiPath(saveDoc.OutputPath);
            LayoutEditorStatus =
                T("Saved {0} marker(s), {1} table(s), {2} connector(s) to {3}.", saveDoc.MarkerCount.ToString(CultureInfo.InvariantCulture), saveDoc.TableCount.ToString(CultureInfo.InvariantCulture), saveDoc.ConnectorCount.ToString(CultureInfo.InvariantCulture), LayoutEditorOutputPath);
            StatusText = T("Layout template saved.");
            LayoutEditorSourcePath = LayoutEditorOutputPath;
            string contextDevice = ResolveLayoutEditorContextDevice();
            RememberLayoutDeviceMapping(contextDevice, LayoutEditorOutputPath);
            if (contextDevice.Length > 0)
                savedLayoutDevices.Add(contextDevice);
            OnLayoutFileSaved(contextDevice, LayoutEditorOutputPath);
            UpdateLayoutMapInputFromRememberedMappings(BuildEffectiveLayoutDeviceFilter());
            await LoadLayoutEditorSourceAsync();
        }
        finally
        {
            try
            {
                if (File.Exists(markerJsonPath))
                    File.Delete(markerJsonPath);
                if (File.Exists(tableJsonPath))
                    File.Delete(tableJsonPath);
                if (File.Exists(connectorJsonPath))
                    File.Delete(connectorJsonPath);
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }

    public void SelectLayoutEditorMarker(LayoutEditorMarkerItem? marker)
    {
        SelectedLayoutEditorMarker = marker;
    }

    public void HandleLayoutCanvasClick(double x, double y)
    {
        if (LayoutPlaceConnectorSourceMode)
        {
            SetLayoutEditorConnectorSourcePoint(x, y);
            return;
        }

        if (LayoutPlaceConnectorPointMode)
        {
            AddLayoutConnectorPointAt(x, y);
            return;
        }

        if (LayoutPlaceTableMode)
        {
            AddLayoutEditorTableAt(x, y);
            LayoutPlaceTableMode = false;
            LayoutPlaceMarkerMode = true;
            return;
        }

        if (LayoutPlaceMarkerMode)
        {
            AddLayoutEditorMarkerAt(x, y);
            return;
        }

        SelectedLayoutEditorMarker = null;
        SelectedLayoutEditorTable = null;
        SelectedLayoutEditorConnector = null;
    }

    public void MoveLayoutEditorMarker(LayoutEditorMarkerItem? marker, double x, double y)
    {
        if (marker == null)
            return;

        double oldX = marker.X;
        double oldY = marker.Y;
        double clampedX = Math.Max(0, Math.Min(LayoutEditorCanvasWidth - 4, x));
        double clampedY = Math.Max(0, Math.Min(LayoutEditorCanvasHeight - 4, y));
        marker.X = Math.Round(clampedX, 2, MidpointRounding.AwayFromZero);
        marker.Y = Math.Round(clampedY, 2, MidpointRounding.AwayFromZero);
        UpdateConnectedConnectorEndpoint(marker.Token, oldX, oldY, marker.X, marker.Y);
    }

    public void SelectLayoutEditorTable(LayoutEditorTableItem? table)
    {
        SelectedLayoutEditorTable = table;
    }

    public void SelectLayoutEditorConnector(LayoutEditorConnectorItem? connector)
    {
        SelectedLayoutEditorConnector = connector;
    }

    public void MoveLayoutEditorConnectorPoint(LayoutEditorConnectorItem? connector, int pointIndex, double x, double y, bool setManualRoute = true)
    {
        if (connector == null || pointIndex < 0)
            return;

        double clampedX = Math.Max(0, Math.Min(LayoutEditorCanvasWidth, x));
        double clampedY = Math.Max(0, Math.Min(LayoutEditorCanvasHeight, y));
        Point clamped = new(
            Math.Round(clampedX, 2, MidpointRounding.AwayFromZero),
            Math.Round(clampedY, 2, MidpointRounding.AwayFromZero));

        if (!connector.SetPointAt(pointIndex, clamped))
            return;

        if (setManualRoute)
            connector.Route = "manual";
        connector.PointsCsv = LayoutEditorConnectorItem.ToPointsCsv(connector.Points);
        if (ReferenceEquals(connector, SelectedLayoutEditorConnector))
        {
            LayoutEditorNewConnectorRoute = connector.Route;
            LayoutEditorNewConnectorPoints = connector.PointsCsv;
        }
    }

    public void MoveLayoutEditorTable(LayoutEditorTableItem? table, double x, double y)
    {
        if (table == null)
            return;

        double clampedX = Math.Max(0, Math.Min(LayoutEditorCanvasWidth - 30, x));
        double clampedY = Math.Max(0, Math.Min(LayoutEditorCanvasHeight - 30, y));
        table.X = Math.Round(clampedX, 2, MidpointRounding.AwayFromZero);
        table.Y = Math.Round(clampedY, 2, MidpointRounding.AwayFromZero);
    }

    private void UpdateConnectedConnectorEndpoint(string markerToken, double oldX, double oldY, double newX, double newY)
    {
        string token = (markerToken ?? string.Empty).Trim();
        if (token.Length == 0)
            return;

        foreach (LayoutEditorConnectorItem connector in LayoutEditorConnectors)
        {
            if (!string.Equals((connector.TargetToken ?? string.Empty).Trim(), token, StringComparison.OrdinalIgnoreCase))
                continue;
            if (connector.Points.Count == 0)
                continue;

            int endpointIndex = 0;
            if (connector.Points.Count > 1)
            {
                Point first = connector.Points[0];
                Point last = connector.Points[connector.Points.Count - 1];
                double firstDistance = (first.X - oldX) * (first.X - oldX) + (first.Y - oldY) * (first.Y - oldY);
                double lastDistance = (last.X - oldX) * (last.X - oldX) + (last.Y - oldY) * (last.Y - oldY);
                endpointIndex = lastDistance <= firstDistance ? connector.Points.Count - 1 : 0;
            }

            MoveLayoutEditorConnectorPoint(connector, endpointIndex, newX, newY, setManualRoute: false);
        }
    }

    public void ResizeLayoutEditorTable(LayoutEditorTableItem? table, double x, double y, double width, double height)
    {
        if (table == null)
            return;

        double minWidth = 120;
        double minHeight = 70;

        double clampedX = Math.Max(0, Math.Min(LayoutEditorCanvasWidth - 30, x));
        double clampedY = Math.Max(0, Math.Min(LayoutEditorCanvasHeight - 30, y));
        double clampedWidth = Math.Max(minWidth, width);
        double clampedHeight = Math.Max(minHeight, height);

        if (clampedX + clampedWidth > LayoutEditorCanvasWidth)
            clampedWidth = Math.Max(minWidth, LayoutEditorCanvasWidth - clampedX);
        if (clampedY + clampedHeight > LayoutEditorCanvasHeight)
            clampedHeight = Math.Max(minHeight, LayoutEditorCanvasHeight - clampedY);

        table.X = Math.Round(clampedX, 2, MidpointRounding.AwayFromZero);
        table.Y = Math.Round(clampedY, 2, MidpointRounding.AwayFromZero);
        table.Width = Math.Round(clampedWidth, 2, MidpointRounding.AwayFromZero);
        table.Height = Math.Round(clampedHeight, 2, MidpointRounding.AwayFromZero);
    }

    [RelayCommand]
    private async Task RenderLayoutAsync()
    {
        // Keep export deterministic: persist current editor state first so sidecars (.tables/.connectors)
        // always match what the user currently sees in Layout Studio.
        if (CanSaveLayoutEditorTemplate)
        {
            string editorSource = NormalizeUiPath(LayoutEditorSourcePath);
            string editorOutput = NormalizeUiPath(LayoutEditorOutputPath);
            if (!string.IsNullOrWhiteSpace(editorSource) &&
                !string.IsNullOrWhiteSpace(editorOutput) &&
                File.Exists(editorSource))
            {
                string previousStatus = StatusText;
                StatusText = T("Auto-saving current layout editor changes before PNG export...");
                await SaveLayoutEditorTemplateAsync();
                // If save failed, SaveLayoutEditorTemplateAsync reports a non-success status. Stop export in that case.
                if (!string.Equals(StatusText, "Layout template saved.", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(previousStatus))
                        StatusText = previousStatus;
                    return;
                }
            }
        }

        List<string> args = ["layout", "render"];
        if (!string.IsNullOrWhiteSpace(LayoutExportPath))
            args.AddRange(["--path", LayoutExportPath.Trim()]);

        List<string> selectedDevices = BuildEffectiveLayoutDeviceFilter();
        List<string> missingLayoutDevices = [];
        foreach (string device in selectedDevices)
        {
            args.AddRange(["--device", device]);
            string layoutPath = await ResolveCheatSheetLayoutAsync(device);
            if (layoutPath.Length == 0)
            {
                missingLayoutDevices.Add(device);
                continue;
            }

            args.AddRange(["--layout-map", device + "=" + layoutPath]);
        }

        UpdateLayoutMapInputFromRememberedMappings(selectedDevices);

        if (missingLayoutDevices.Count > 0)
        {
            string deviceList = string.Join(", ", missingLayoutDevices);
            StatusText = T("Layout export blocked: missing layout template for device(s): {0}", deviceList);
            CheatSheetStatus = T("No picture with markers yet for: {0}. Pick the device under Device pictures first (or untick it).", string.Join(", ", missingLayoutDevices.Select(DeviceDisplayName)));
            WarningRequested?.Invoke(StatusText + ". Open Layout tab, assign/load a template for each selected device, then export again.");
            return;
        }

        foreach (string game in BuildEffectiveLayoutGameFilter())
            args.AddRange(["--game", game]);
        foreach (string plane in BuildEffectiveLayoutPlaneFilter())
            args.AddRange(["--plane", plane]);
        args.AddRange(["--include-overview", LayoutIncludeOverview ? "true" : "false"]);
        args.AddRange(["--include-planes", LayoutIncludePlanes ? "true" : "false"]);
        args.AddRange(["--skip-missing-layouts", LayoutSkipMissing ? "true" : "false"]);
        args.AddRange(["--draw-markers", LayoutDrawMarkers ? "true" : "false"]);
        args.AddRange(["--draw-legend-table", LayoutDrawLegendTable ? "true" : "false"]);
        int markerSize = ParseOrDefault(LayoutMarkerSize, 10, 6, 36);
        args.AddRange(["--marker-size", markerSize.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["--legend-width", ParseOrDefault(LayoutLegendWidth, 460, 120, 2000).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["--legend-max-rows", ParseOrDefault(LayoutLegendMaxRows, 20, 1, 200).ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["--legend-font-size", ParseDoubleOrDefault(LayoutLegendFontSize, 11, 6, 48).ToString("0.##", CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(LayoutModifierColors))
            args.AddRange(["--modifier-colors", LayoutModifierColors.Trim()]);
        if (!string.IsNullOrWhiteSpace(LayoutButtonSymbol))
            args.AddRange(["--button-symbol", LayoutButtonSymbol.Trim()]);
        if (!string.IsNullOrWhiteSpace(LayoutAxisSymbol))
            args.AddRange(["--axis-symbol", LayoutAxisSymbol.Trim()]);
        if (!string.IsNullOrWhiteSpace(LayoutPovSymbol))
            args.AddRange(["--pov-symbol", LayoutPovSymbol.Trim()]);
        if (!string.IsNullOrWhiteSpace(LayoutSliderSymbol))
            args.AddRange(["--slider-symbol", LayoutSliderSymbol.Trim()]);
        // Same label style as the layout editor preview.
        if (!string.IsNullOrWhiteSpace(LayoutFontFamily))
            args.AddRange(["--font", LayoutFontFamily.Trim()]);
        args.AddRange(["--font-size", LayoutEditorFontSizeValue.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["--text-color", string.IsNullOrWhiteSpace(LayoutTextColor) ? "#FFFFFF" : LayoutTextColor.Trim()]);
        args.AddRange(["--outline-color", string.IsNullOrWhiteSpace(LayoutOutlineColor) ? "none" : LayoutOutlineColor.Trim()]);
        args.AddRange(["--label-background", string.IsNullOrWhiteSpace(LayoutBackgroundColor) ? "none" : LayoutBackgroundColor.Trim()]);
        // Sheets with tables and lines (the Layout page's callout sheets): their look and pages of all devices.
        args.AddRange(["--style", LayoutSheetLightStyle ? "light" : "dark"]);
        if (LayoutCombinePages)
            args.AddRange(["--combine", "true"]);
        // JoyPro's drawings in the sheets: the drawing style of the export options (lines or cel-shaded, colours).
        args.AddRange(DeviceReferenceService.ExportDrawingArguments());

        EngineCommandResult? rendered = await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Making cheat sheets..."));
        ShowCheatSheetResult(rendered);
    }

    [RelayCommand]
    private async Task ValidateProfileAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(
            ["profile", "validate", "--fail-on-warnings", "false"],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Validating profile..."));

        // The engine exits with 1 whenever errors were found; those are exactly the results to show.
        if (result == null || result.StandardOutput.IndexOf("Profile Validation", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        ParseValidation(result.StandardOutput);
        if (result.ExitCode != 0)
            StatusText = T("Validation found errors. See the issue list.");
    }

    [RelayCommand]
    private void OpenIssueContext()
    {
        if (SelectedValidationIssue == null)
        {
            StatusText = T("Select an issue first.");
            return;
        }

        string message = SelectedValidationIssue.Message;
        const string marker = "The Relations it was found in:";
        int idx = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            StatusText = T("No relation names found in this issue.");
            return;
        }

        string tail = message[(idx + marker.Length)..].Trim();
        string relation = tail.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(v => v.Trim())
                              .FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relation))
        {
            StatusText = T("No relation name parsed from issue.");
            return;
        }

        RelationListItem? match = allRelations.FirstOrDefault(v => string.Equals(v.Name, relation, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            CurrentStage = UiStage.Relations;
            SelectedRelation = match;
            StatusText = T("Opened relation from validation issue.");
        }
    }

    [RelayCommand]
    private async Task ExportDcsAsync() => await RunDcsExportAsync(null);

    // planes: only these aircraft (the ticked ones of the DCS export tile); null = all.
    private async Task<EngineCommandResult?> RunDcsExportAsync(IReadOnlyList<string>? planes)
    {
        List<string> args = ["dcs", "export"];
        if (!string.IsNullOrWhiteSpace(ExportDcsPath))
            args.AddRange(["--path", ExportDcsPath.Trim()]);
        args.AddRange(["--mode", string.IsNullOrWhiteSpace(ExportMode) ? "merge" : ExportMode.Trim()]);
        args.AddRange(["--keep-keyboard-defaults", ExportKeepKeyboardDefaults ? "true" : "false"]);
        args.AddRange(["--nuke-untouched-devices", ExportNukeUntouchedDevices ? "true" : "false"]);
        args.AddRange(["--clean-all-aircraft", ExportDcsCleanAllAircraft ? "true" : "false"]);
        if (planes != null && planes.Count > 0)
            args.AddRange(["--planes", string.Join("|", planes)]);
        return await RunEngineAsync(args, includeProfile: true, mutatesProfile: false,
            busyLabel: planes != null && planes.Count > 0 ? "Exporting DCS (" + planes.Count + " aircraft)..." : "Exporting DCS...");
    }

    [RelayCommand]
    private async Task ExportIl2Async() => await RunIl2ExportAsync();

    private async Task<EngineCommandResult?> RunIl2ExportAsync()
    {
        List<string> args = ["il2", "export"];
        if (!string.IsNullOrWhiteSpace(ExportIl2Path))
            args.AddRange(["--path", ExportIl2Path.Trim()]);
        args.AddRange(["--mode", string.IsNullOrWhiteSpace(ExportMode) ? "merge" : ExportMode.Trim()]);
        args.AddRange(["--nuke-untouched-devices", ExportNukeUntouchedDevices ? "true" : "false"]);
        return await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Exporting IL-2..."));
    }

    // Star Citizen DB: the action catalog (with option groups, the joystick option tree and the game's default
    // joystick binds) from the installed game's Data.p4k, or from StarCitizenSourcePath when set.
    [ObservableProperty]
    private bool starCitizenRefreshDryRun;

    // The game branch to read (LIVE, PTU, EPTU, ...); empty = the one the engine finds first.
    [ObservableProperty]
    private string starCitizenRefreshBranch = string.Empty;

    [RelayCommand]
    private async Task RefreshStarCitizenDbAsync()
    {
        List<string> args = ["db", "refresh-starcitizen"];
        AddStarCitizenRefreshSource(args);
        args.AddRange(["--dry-run", StarCitizenRefreshDryRun ? "true" : "false"]);
        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Refreshing the Star Citizen DB..."));
    }

    private void AddStarCitizenRefreshSource(List<string> args)
    {
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath))
            args.AddRange(["--source", StarCitizenSourcePath.Trim()]);
        if (!string.IsNullOrWhiteSpace(StarCitizenRefreshBranch))
            args.AddRange(["--branch", StarCitizenRefreshBranch.Trim()]);
    }

    // IL-2 DB: adds the actions of the installed game's key mapping files (data\input\*.actions) that DB\IL2\IL2.actions
    // lacks; known actions keep their titles. Uses the IL-2 export path when one is set.
    [ObservableProperty]
    private bool il2RefreshDryRun;

    [RelayCommand]
    private async Task RefreshIl2DbAsync()
    {
        List<string> args = ["db", "refresh-il2"];
        if (!string.IsNullOrWhiteSpace(ExportIl2Path))
            args.AddRange(["--path", ExportIl2Path.Trim()]);
        if (Il2RefreshDryRun)
            args.Add("--dry-run");
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Refreshing the IL-2 DB..."));
        if (result?.ExitCode == 0 && !Il2RefreshDryRun)
        {
            InvalidateDbCaches();
            RebuildRelationNodeGameOptions();
        }
    }

    [RelayCommand]
    private async Task ExportStarCitizenAsync() => await RunStarCitizenExportAsync();

    private async Task<EngineCommandResult?> RunStarCitizenExportAsync()
    {
        List<string> args = ["starcitizen", "export"];
        if (!string.IsNullOrWhiteSpace(ExportStarCitizenPath))
            args.AddRange(["--path", ExportStarCitizenPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath))
            args.AddRange(["--source", StarCitizenSourcePath.Trim()]);
        args.AddRange(["--mode", string.IsNullOrWhiteSpace(ExportMode) ? "merge" : ExportMode.Trim()]);
        args.AddRange(["--keep-keyboard-defaults", ExportStarCitizenKeepKeyboardDefaults ? "true" : "false"]);
        args.AddRange(["--prefer-local-order", ExportStarCitizenPreferLocalOrder ? "true" : "false"]);
        args.AddRange(["--include-keyboard", ImportStarCitizenIncludeKeyboard ? "true" : "false"]);
        args.AddRange(["--nuke-untouched-devices", ExportNukeUntouchedDevices ? "true" : "false"]);
        return await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: N("Exporting Star Citizen..."));
    }

    private void ParseValidation(string output)
    {
        List<ValidationIssueItem> all = [];
        string summary = T("Validation completed.");
        string currentType = T("Warning");

        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("Passed:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Warnings:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Errors:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Fail On Warnings:", StringComparison.OrdinalIgnoreCase))
            {
                summary += Environment.NewLine + line.Trim();
                continue;
            }

            // Section headers printed by "engine profile validate --text".
            if (line.StartsWith("Relation Errors:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Bind Errors:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Modifier Errors:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Duplicate Active Errors:", StringComparison.OrdinalIgnoreCase))
            {
                currentType = T("Error");
                continue;
            }
            if (line.StartsWith("Relation Warnings:", StringComparison.OrdinalIgnoreCase))
            {
                currentType = T("Warning");
                continue;
            }
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                string message = line.TrimStart()[2..].Trim();
                all.Add(new ValidationIssueItem
                {
                    Type = currentType,
                    Message = message,
                    ActionHint = currentType == "Error"
                        ? "Open context and fix blocking issue."
                        : "Open context and decide if warning is acceptable."
                });
            }
        }

        ValidationSummary = summary;
        allValidationIssues.Clear();
        allValidationIssues.AddRange(all);
        RebuildValidationFilter();

        ConflictSummary = T("Conflicts are resolved during profile merge operations (mode right or left).");
    }

    // The connected devices with VID:PID, HID name and DirectInput axes / buttons / hats (engine games devices), shown in
    // the output panel.
    [RelayCommand]
    private async Task ShowDeviceDetailsAsync()
    {
        await RunEngineAsync(["games", "devices"], includeProfile: false, mutatesProfile: false, busyLabel: N("Reading the connected devices..."));
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        bool includeProfileContext = !string.IsNullOrWhiteSpace(ProfilePath);
        List<string> args = ["devices", "list", "--connected"];
        if (includeProfileContext)
            args.Add("--profile-devices");

        EngineCommandResult? result = await RunEngineAsync(
            args,
            includeProfile: includeProfileContext,
            mutatesProfile: false,
            busyLabel: N("Loading devices..."));
        if (result == null || result.ExitCode != 0)
            return;

        ConnectedDevices.Clear();
        ProfileDevices.Clear();
        string section = string.Empty;
        foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("Connected:", StringComparison.OrdinalIgnoreCase))
            {
                section = "connected";
                continue;
            }
            if (line.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase))
            {
                section = "profile";
                continue;
            }
            if (line.StartsWith("Only Connected", StringComparison.OrdinalIgnoreCase))
            {
                section = "connected";
                continue;
            }
            if (line.StartsWith("Only Profile", StringComparison.OrdinalIgnoreCase))
            {
                section = "profile";
                continue;
            }
            if (line.StartsWith("Only In Profile", StringComparison.OrdinalIgnoreCase))
            {
                section = "profile";
                continue;
            }
            if (line.StartsWith("Warnings:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Errors:", StringComparison.OrdinalIgnoreCase))
            {
                section = "ignore";
                continue;
            }
            if (section == "ignore" || !line.TrimStart().StartsWith("- "))
                continue;

            // "Only Connected"/"Only In Profile" repeat devices from the main sections.
            string device = line.Trim().TrimStart('-', ' ').Trim();
            ObservableCollection<string> target = section == "profile" ? ProfileDevices : ConnectedDevices;
            if (!target.Contains(device, StringComparer.OrdinalIgnoreCase))
                target.Add(device);
        }

        RebuildQuickBindOptions();
        RebuildImportDeviceOptions();
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        UpdateLayoutMapInputFromRememberedMappings(BuildEffectiveLayoutDeviceFilter());
        SeedDcsFfbDeviceSettingsFromKnownDevices();
    }

    [RelayCommand]
    private async Task RefreshDeviceInputCatalogAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(
            ["devices", "inputs"],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: N("Loading device input catalog..."));
        if (result == null || result.ExitCode != 0)
            return;

        DeviceInputsCatalog.Clear();
        foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("- "))
                DeviceInputsCatalog.Add(line.Trim().TrimStart('-', ' ').Trim());
        }
        keyboardKeyCatalog.Clear();
        keyboardKeyCatalog.AddRange(ParseKeyboardKeyCatalog(result.StandardOutput));

        RebuildQuickBindOptions();
        RebuildLayoutEditorInputOptions();
    }

    [RelayCommand]
    private async Task ExchangeDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(ExchangeOldDevice) || string.IsNullOrWhiteSpace(ExchangeNewDevice))
        {
            StatusText = T("Old and new device names are required.");
            return;
        }
        if (!await ConfirmAsync(T("Move the binds of {0}?", ExchangeOldDevice.Trim()),
                T("Every bind and modifier of {0} moves to {1} in your profile. A copy of your profile is kept next to it first.", ExchangeOldDevice.Trim(), ExchangeNewDevice.Trim()),
                T("Move binds")))
            return;
        BackupProfileBeforeChange("before-device-exchange");

        EngineCommandResult? result = await RunEngineAsync(
            ["devices", "exchange", ExchangeOldDevice.Trim(), ExchangeNewDevice.Trim(), "--confirm", "false"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Exchanging device references..."));
        if (result?.ExitCode == 0)
        {
            await RefreshDevicesAsync();
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task RemoveDeviceFromProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceRemoveName))
        {
            StatusText = T("Device name is required.");
            return;
        }
        if (!await ConfirmAsync(T("Remove {0} from your profile?", DeviceRemoveName.Trim()),
                T("Every bind and modifier of this device is removed from the profile (the relations stay). A copy of your profile is kept next to it first."),
                T("Remove device"), isDanger: true))
            return;
        BackupProfileBeforeChange("before-device-remove");

        string removedDevice = DeviceRemoveName.Trim();
        List<PendingModifierDefinition> modifiersBefore = SnapshotProfileModifiers();
        EngineCommandResult? result = await RunEngineAsync(
            ["devices", "remove", removedDevice, "--confirm", "false"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Removing device references..."));
        if (result?.ExitCode == 0)
        {
            await RefreshDevicesAsync();
            await RefreshAndKeepModifiersAsync(modifiersBefore, removedDevice);
        }
    }

    [RelayCommand]
    private async Task RefreshDbSummaryAsync()
    {
        await RunEngineAsync(["db", "summary"], includeProfile: false, mutatesProfile: false, busyLabel: N("Loading DB summary..."));
    }

    [RelayCommand]
    private async Task RefreshDbCheckAsync()
    {
        await RunEngineAsync(["db", "check"], includeProfile: false, mutatesProfile: false, busyLabel: N("Checking DB integrity..."));
    }

    [RelayCommand]
    private async Task RefreshDbListAsync()
    {
        List<string> args = ["db", "list"];
        if (!string.IsNullOrWhiteSpace(DbPlaneFilter))
            args.Add(DbPlaneFilter.Trim());
        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Loading DB list..."));
    }

    [RelayCommand]
    private async Task RefreshDbGamesAsync()
    {
        List<string> args = ["db", "games"];
        bool applyUiFilters = CurrentStage == UiStage.Settings || CurrentStage == UiStage.Advanced;
        if (applyUiFilters && !string.IsNullOrWhiteSpace(DbGameFilter))
            args.Add(DbGameFilter.Trim());
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Loading DB games..."));
        if (result == null || result.ExitCode != 0)
            return;

        allDbGames.Clear();
        foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("DB Games:", StringComparison.OrdinalIgnoreCase))
                continue;
            // Error/warning sections of "db games" are not game names.
            if (line.StartsWith("Errors:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Warnings:", StringComparison.OrdinalIgnoreCase))
                break;
            if (line.Length == 0)
                continue;

            string game = line.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim() ?? string.Empty;
            if (game.Length == 0)
                continue;
            if (!allDbGames.Contains(game, StringComparer.OrdinalIgnoreCase))
                allDbGames.Add(game);
        }

        RebuildVisibleDbGames();
    }

    // Every game of the DB ("db games"); DbGames leaves out the games the user does not have (Settings > Games).
    private readonly List<string> allDbGames = [];

    private void RebuildVisibleDbGames()
    {
        DbGames.Clear();
        foreach (string game in allDbGames.Where(g => !IsGameHidden(g)))
            DbGames.Add(game);
        RebuildRelationNodeGameOptions();
        RebuildLayoutGameOptions(BuildLayoutGameCandidates());
    }

    [RelayCommand]
    private async Task RefreshDbPlanesAsync()
    {
        List<string> args = ["db", "planes"];
        bool applyUiFilters = CurrentStage == UiStage.Settings || CurrentStage == UiStage.Advanced;
        if (applyUiFilters && !string.IsNullOrWhiteSpace(DbPlaneFilter))
            args.Add(DbPlaneFilter.Trim());
        if (applyUiFilters && !string.IsNullOrWhiteSpace(SelectedDbGame))
            args.AddRange(["--game", SelectedDbGame.Trim()]);

        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Loading DB planes..."));
        if (result == null || result.ExitCode != 0)
            return;

        DbPlanes.Clear();
        foreach (string plane in ParseDbPlaneList(result.StandardOutput))
            DbPlanes.Add(plane);

        if (string.IsNullOrWhiteSpace(SelectedDbGame) ||
            SelectedDbGame.Equals("DCS", StringComparison.OrdinalIgnoreCase))
        {
            RebuildImportPlaneOptions(DbPlanes);
        }

        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
    }

    [RelayCommand]
    private async Task RefreshImportDcsPlaneOptionsAsync()
    {
        List<string> configPlanes = DiscoverDcsConfigPlanes(ImportDcsPath);
        if (configPlanes.Count > 0)
        {
            RebuildImportPlaneOptions(configPlanes);
            RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
            RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            ["db", "planes", "--game", "DCS"],
            includeProfile: false,
            mutatesProfile: false,
            busyLabel: N("Loading DCS plane list..."));
        if (result == null || result.ExitCode != 0)
            return;

        RebuildImportPlaneOptions(ParseDbPlaneList(result.StandardOutput));
        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        RebuildLayoutPlaneOptions(BuildLayoutPlaneCandidates());
        if (NormalizeRelationGameToken(RelationNodeGameInput).Equals("DCS", StringComparison.OrdinalIgnoreCase))
            await RefreshRelationNodePlaneOptionsAsync();
    }

    [RelayCommand]
    private async Task RefreshDbFingerprintAsync()
    {
        await RunEngineAsync(["db", "fingerprint"], includeProfile: false, mutatesProfile: false, busyLabel: N("Calculating DB fingerprint..."));
    }

    [RelayCommand]
    private async Task RefreshGamePathsAsync()
    {
        await RunEngineAsync(["games", "paths"], includeProfile: false, mutatesProfile: false, busyLabel: N("Discovering game paths..."));
    }

    [RelayCommand]
    private async Task RefreshGamesListAsync()
    {
        List<string> args = ["games", "list"];
        if (!string.IsNullOrWhiteSpace(DbGameFilter))
            args.AddRange(["--game", DbGameFilter.Trim()]);
        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Listing games..."));
    }

    [RelayCommand]
    private async Task DetectGamesAsync()
    {
        List<string> args = ["games", "detect"];
        if (!string.IsNullOrWhiteSpace(DbGameFilter))
            args.AddRange(["--game", DbGameFilter.Trim()]);
        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Detecting games..."));
    }

    [RelayCommand]
    private async Task DcsDefaultsCutAsync()
    {
        // The device files move with "dcs device-luas" now (JoyPro's backup folder, re-applied after DCS updates).
        await SetDcsRemoveDeviceFilesAsync(true);
    }

    // Puts back what an older JoyPro moved into Saved Games\DCS\BACKUP_JOYSTICK_DEFAULTS (engine dcs defaults restore).
    [RelayCommand]
    private async Task DcsDefaultsRestoreAsync()
    {
        if (!await ConfirmAsync(T("Put back the device files an older JoyPro moved?"),
                "The default files an older JoyPro moved into Saved Games\\DCS\\BACKUP_JOYSTICK_DEFAULTS go back into DCS. Close DCS first.", T("Put back")))
            return;
        List<string> args = ["dcs", "defaults", "restore"];
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            args.AddRange(["--install-path", DcsInstallPath.Trim()]);
        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Restoring DCS defaults..."));
    }

    [RelayCommand]
    private void AutoDetectSettingsPaths()
    {
        string detectedSavedGames = GuessSavedGamesDcsPath();
        if ((string.IsNullOrWhiteSpace(DcsSavedGamesPath) || !Directory.Exists(DcsSavedGamesPath)) &&
            !string.IsNullOrWhiteSpace(detectedSavedGames) &&
            Directory.Exists(detectedSavedGames))
        {
            DcsSavedGamesPath = detectedSavedGames;
        }

        string detectedInstall = GuessDcsInstallPath();
        if ((string.IsNullOrWhiteSpace(DcsInstallPath) || !Directory.Exists(DcsInstallPath)) &&
            !string.IsNullOrWhiteSpace(detectedInstall) &&
            Directory.Exists(detectedInstall))
        {
            DcsInstallPath = detectedInstall;
        }

        if (string.IsNullOrWhiteSpace(ImportDcsPath) || !Directory.Exists(ImportDcsPath))
            ImportDcsPath = DcsSavedGamesPath;
        if (string.IsNullOrWhiteSpace(ExportDcsPath) || !Directory.Exists(ExportDcsPath))
            ExportDcsPath = DcsSavedGamesPath;

        string detectedScPath = GuessStarCitizenProfilePath();
        if ((string.IsNullOrWhiteSpace(ImportStarCitizenPath) || !Directory.Exists(ImportStarCitizenPath)) &&
            !string.IsNullOrWhiteSpace(detectedScPath) &&
            Directory.Exists(detectedScPath))
        {
            ImportStarCitizenPath = detectedScPath;
        }
        if (string.IsNullOrWhiteSpace(ExportStarCitizenPath) || !Directory.Exists(ExportStarCitizenPath))
            ExportStarCitizenPath = ImportStarCitizenPath;

        PrefillStarCitizenSourcePath();
        PrefillDcsRefreshPaths();
        RefreshDcsKnownDevices();
        RebuildImportDeviceOptions();
        RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        SeedDcsFfbDeviceSettingsFromKnownDevices();
        StatusText = T("Settings paths auto-detected from existing files/folders.");
    }

    [RelayCommand]
    private async Task DcsRefreshIdAsync()
    {
        List<string> args = ["dcs", "refresh", "id"];
        ApplyDcsRefreshArgs(args);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Refreshing DCS ID DB..."));
        if (result?.ExitCode == 0 && !DcsRefreshDryRun)
        {
            InvalidateDbCaches();
            RebuildDcsRefreshPlaneOptions(BuildDcsRefreshPlaneCandidates());
        }
    }

    [RelayCommand]
    private async Task DcsRefreshCleanAsync()
    {
        List<string> args = ["dcs", "refresh", "clean"];
        ApplyDcsRefreshArgs(args);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Refreshing DCS clean DB..."));
        if (result?.ExitCode == 0 && !DcsRefreshDryRun)
            InvalidateDbCaches();
    }

    [RelayCommand]
    private async Task DcsPreviewClicksAsync()
    {
        List<string> args = ["dcs", "refresh", "preview-clicks"];
        args.AddRange(["--ocr", DcsRefreshUseOcr ? "true" : "false"]);
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            args.AddRange(["--install-path", DcsInstallPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsClicksXmlPath))
            args.AddRange(["--clicks-xml", DcsClicksXmlPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsClicksDirPath))
            args.AddRange(["--clicks-dir", DcsClicksDirPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsPreviewImagePath))
            args.AddRange(["--preview-image", DcsPreviewImagePath.Trim()]);

        await RunEngineAsync(args, includeProfile: false, mutatesProfile: false, busyLabel: N("Previewing DCS click map..."));
    }

    [RelayCommand]
    private async Task DcsFfbListAsync()
    {
        List<string> args = ["dcs", "ffb", "list"];
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        // FFB settings live in the profile; without --profile the engine would show whatever profile it last saw.
        EngineCommandResult? result = await RunEngineAsync(
            args,
            includeProfile: !string.IsNullOrWhiteSpace(ProfilePath),
            mutatesProfile: false,
            busyLabel: N("Loading DCS FFB settings..."));
        if (result == null || result.ExitCode != 0)
            return;

        ParseDcsFfbList(result.StandardOutput);
    }

    [RelayCommand]
    private async Task DcsFfbSetAsync()
    {
        if (DcsFfbDeviceSettings.Any(v => v.IsSelected))
        {
            await DcsFfbApplySelectedAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(DcsFfbDevice))
        {
            StatusText = T("FFB device name is required.");
            return;
        }

        EngineCommandResult? result = await RunDcsFfbSetForDeviceAsync(
            DcsFfbDevice.Trim(),
            DcsFfbInvertX,
            DcsFfbInvertY,
            DcsFfbSwapAxis,
            "Applying DCS FFB settings...");
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private async Task DcsFfbClearAsync()
    {
        if (DcsFfbDeviceSettings.Any(v => v.IsSelected))
        {
            await DcsFfbClearSelectedAsync();
            return;
        }

        EngineCommandResult? result = await RunDcsFfbClearForDeviceAsync(
            string.IsNullOrWhiteSpace(DcsFfbDevice) ? null : DcsFfbDevice.Trim(),
            clearAll: string.IsNullOrWhiteSpace(DcsFfbDevice),
            busyLabel: N("Clearing DCS FFB settings..."));
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private async Task DcsFfbApplyDeviceAsync(DcsFfbDeviceSettingItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Device))
        {
            StatusText = T("Select a valid FFB device.");
            return;
        }

        EngineCommandResult? result = await RunDcsFfbSetForDeviceAsync(
            item.Device.Trim(),
            item.InvertX,
            item.InvertY,
            item.SwapAxis,
            "Applying DCS FFB for " + item.Device + "...");
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private async Task DcsFfbClearDeviceAsync(DcsFfbDeviceSettingItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Device))
        {
            StatusText = T("Select a valid FFB device.");
            return;
        }

        EngineCommandResult? result = await RunDcsFfbClearForDeviceAsync(
            item.Device.Trim(),
            clearAll: false,
            busyLabel: "Clearing DCS FFB for " + item.Device + "...");
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private void SelectAllDcsFfbDevices()
    {
        foreach (DcsFfbDeviceSettingItem item in DcsFfbDeviceSettings)
            item.IsSelected = true;
        RebuildVisibleDcsFfbDeviceSettings();
    }

    [RelayCommand]
    private void ClearDcsFfbDeviceSelection()
    {
        foreach (DcsFfbDeviceSettingItem item in DcsFfbDeviceSettings)
            item.IsSelected = false;
        RebuildVisibleDcsFfbDeviceSettings();
    }

    [RelayCommand]
    private void SelectConnectedDcsFfbDevices()
    {
        foreach (DcsFfbDeviceSettingItem item in DcsFfbDeviceSettings)
            item.IsSelected = item.Connected;
        RebuildVisibleDcsFfbDeviceSettings();
    }

    [RelayCommand]
    private void SelectProfileDcsFfbDevices()
    {
        foreach (DcsFfbDeviceSettingItem item in DcsFfbDeviceSettings)
            item.IsSelected = item.Profile;
        RebuildVisibleDcsFfbDeviceSettings();
    }

    [RelayCommand]
    private async Task DcsFfbApplySelectedAsync()
    {
        List<DcsFfbDeviceSettingItem> selected = DcsFfbDeviceSettings
            .Where(v => v.IsSelected && !string.IsNullOrWhiteSpace(v.Device))
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = T("Select one or more FFB devices.");
            return;
        }

        foreach (DcsFfbDeviceSettingItem item in selected)
        {
            EngineCommandResult? result = await RunDcsFfbSetForDeviceAsync(
                item.Device.Trim(),
                item.InvertX,
                item.InvertY,
                item.SwapAxis,
                "Applying DCS FFB for " + item.Device + "...");
            if (result == null || result.ExitCode != 0)
                return;
        }

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private async Task DcsFfbClearSelectedAsync()
    {
        List<DcsFfbDeviceSettingItem> selected = DcsFfbDeviceSettings
            .Where(v => v.IsSelected && !string.IsNullOrWhiteSpace(v.Device))
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = T("Select one or more FFB devices.");
            return;
        }

        foreach (DcsFfbDeviceSettingItem item in selected)
        {
            EngineCommandResult? result = await RunDcsFfbClearForDeviceAsync(
                item.Device.Trim(),
                clearAll: false,
                busyLabel: "Clearing DCS FFB for " + item.Device + "...");
            if (result == null || result.ExitCode != 0)
                return;
        }

        await DcsFfbListAsync();
    }

    [RelayCommand]
    private async Task DcsFfbClearAllAsync()
    {
        if (!await ConfirmAsync(T("Clear every DCS force feedback setting?"),
                T("Swap and invert go back to DCS's defaults for every device."), T("Clear all"), isDanger: true))
            return;
        EngineCommandResult? result = await RunDcsFfbClearForDeviceAsync(
            device: null,
            clearAll: true,
            busyLabel: N("Clearing all DCS FFB settings..."));
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    private async Task<EngineCommandResult?> RunDcsFfbSetForDeviceAsync(
        string device,
        bool invertX,
        bool invertY,
        bool swapAxis,
        string busyLabel)
    {
        string normalizedDevice = (device ?? string.Empty).Trim();
        if (normalizedDevice.Length == 0)
            return null;

        List<string> args =
        [
            "dcs", "ffb", "set", normalizedDevice,
            "--invert-x", invertX ? "true" : "false",
            "--invert-y", invertY ? "true" : "false",
            "--swap-axis", swapAxis ? "true" : "false",
            "--confirm", "false"
        ];
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);

        // FFB settings are stored in the profile, so a change is only kept when --profile is passed.
        return await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: busyLabel);
    }

    private async Task<EngineCommandResult?> RunDcsFfbClearForDeviceAsync(
        string? device,
        bool clearAll,
        string busyLabel)
    {
        List<string> args = ["dcs", "ffb", "clear"];
        if (clearAll)
        {
            args.Add("--all");
        }
        else
        {
            string normalizedDevice = (device ?? string.Empty).Trim();
            if (normalizedDevice.Length == 0)
                return null;
            args.Add(normalizedDevice);
        }

        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        args.AddRange(["--confirm", "false"]);

        // FFB settings are stored in the profile, so a change is only kept when --profile is passed.
        return await RunEngineAsync(args, includeProfile: true, mutatesProfile: false, busyLabel: busyLabel);
    }

    private void ApplyDcsRefreshArgs(List<string> args)
    {
        if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            args.AddRange(["--path", DcsSavedGamesPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            args.AddRange(["--install-path", DcsInstallPath.Trim()]);

        args.AddRange(["--ocr", DcsRefreshUseOcr ? "true" : "false"]);
        args.AddRange(["--launch", DcsRefreshLaunch ? "true" : "false"]);
        args.AddRange(["--keep-running", DcsRefreshKeepRunning ? "true" : "false"]);
        args.AddRange(["--headless-lua", DcsRefreshHeadlessLua ? "true" : "false"]);
        if (DcsRefreshDryRun)
            args.Add("--dry-run");
        foreach (string plane in BuildEffectiveDcsRefreshPlaneFilter())
            args.AddRange(["--plane", plane]);
        if (!string.IsNullOrWhiteSpace(DcsClicksXmlPath))
            args.AddRange(["--clicks-xml", DcsClicksXmlPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsClicksDirPath))
            args.AddRange(["--clicks-dir", DcsClicksDirPath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsPreviewImagePath))
            args.AddRange(["--preview-image", DcsPreviewImagePath.Trim()]);
        if (!string.IsNullOrWhiteSpace(DcsExePath))
            args.AddRange(["--dcs-exe", DcsExePath.Trim()]);
    }

    [RelayCommand]
    private async Task RunCustomCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomCommandText))
        {
            StatusText = T("Enter a command.");
            return;
        }

        List<string> args = [];
        foreach (string extra in TokenizeCli(CustomCommandExtraGlobal))
            args.Add(extra);
        foreach (string token in TokenizeCli(CustomCommandText))
            args.Add(token);

        if (args.Count == 0)
        {
            StatusText = T("Command parsing returned no tokens.");
            return;
        }

        EngineCommandResult? result = await RunEngineAsync(
            args,
            includeProfile: CustomCommandIncludeProfile,
            mutatesProfile: false,
            busyLabel: N("Running custom command..."),
            forceText: false,
            isCustomCommand: true);

        if (result?.ExitCode == 0 && args.Count >= 2 &&
            args[0].Equals("relations", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ParseRelations(result.StandardOutput);
        }
        else if (result?.ExitCode == 0 && args.Count >= 2 &&
                 args[0].Equals("binds", StringComparison.OrdinalIgnoreCase) &&
                 args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ParseBinds(result.StandardOutput);
        }
    }

    [RelayCommand]
    private async Task RelationsEditAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(
            ["relations", "edit"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Opening relations editor..."),
            forceText: false);

        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    [RelayCommand]
    private async Task BindsEditAsync()
    {
        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "edit"],
            includeProfile: true,
            mutatesProfile: true,
            busyLabel: N("Opening binds editor..."),
            forceText: false);

        if (result?.ExitCode == 0)
        {
            await RefreshRelationsAsync();
            await RefreshBindsAndModifiersAfterChangeAsync();
        }
    }

    // Every relation of the profile and a read-only engine call with it (the journey's title checks).
    internal IReadOnlyList<RelationListItem> AllRelationsForTests() => allRelations;

    internal Task<EngineCommandResult?> RunEngineForTestsAsync(IEnumerable<string> commandArgs, bool text = true) =>
        RunEngineAsync(commandArgs, includeProfile: HasProfile, mutatesProfile: false, busyLabel: "Test", forceText: text, background: true);

    internal string ResolveNodeTitleForTests(string game, string plane, string nodeId) => ResolveNodeTitle(game, plane, nodeId);

    private async Task<EngineCommandResult?> RunEngineAsync(
        IEnumerable<string> commandArgs,
        bool includeProfile,
        bool mutatesProfile,
        string busyLabel,
        bool forceText = true,
        bool isCustomCommand = false,
        bool background = false,
        string? historyNote = null,
        Func<bool>? stillWanted = null)
    {
        List<string> commandList = commandArgs as List<string> ?? commandArgs.ToList();
        commandArgs = commandList;
        // Edits the profile does not have yet go in before an export or import reads it (docs/profile-history.md).
        if (!background && IsExportOrImportCommand(commandList) && NeedsAutosaveFlush)
            await FlushAutosaveAsync();

        string resolvedEnginePath = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(resolvedEnginePath) || !File.Exists(resolvedEnginePath))
        {
            StatusText = T("Engine path is missing. Set Engine Path first.");
            return null;
        }

        if ((includeProfile || mutatesProfile) && string.IsNullOrWhiteSpace(ProfilePath))
        {
            if (mutatesProfile && AutoCreateProfileOnImport)
            {
                ProfilePath = CreateFreshAutoProfilePath();
            }
            else
            {
                StatusText = T("Profile path required for this command.");
                return null;
            }
        }

        List<string> args = [];
        if (includeProfile || mutatesProfile)
        {
            EnsureProfileDirectoryExists(ProfilePath);
            args.Add("--profile");
            args.Add(ProfilePath);
        }

        // The busy area reads the status and progress lines live (MainWindowViewModel.BusyProgress.cs); they are taken
        // out of the result again unless the user asked for status updates in the output. Background checks stay quiet.
        args.Add("--status");
        args.Add(IncludeStatusUpdates || !background ? "true" : "false");
        args.Add("--benchmark");
        args.Add(IncludeBenchmark ? "true" : "false");

        // The name of this change in the profile history: the words of the busy area ("Adding relation").
        string note = historyNote ?? (mutatesProfile && !isCustomCommand ? busyLabel : string.Empty);
        note = (note ?? string.Empty).Trim().TrimEnd('.').Trim();
        if (note.Length > 0 && !background)
        {
            args.Add("--history-note");
            args.Add(note);
        }

        // --out redirects the payload into a file, which would leave the GUI parsers with nothing to read.
        if (isCustomCommand && !string.IsNullOrWhiteSpace(GlobalOutputPath))
        {
            args.Add("--out");
            args.Add(GlobalOutputPath.Trim());
        }
        if (!string.IsNullOrWhiteSpace(GlobalEditorCommand))
        {
            args.Add("--editor");
            args.Add(GlobalEditorCommand.Trim());
        }

        // The engine rejects every correction option unless load corrections are enabled, and the GUI
        // cannot answer confirmation prompts, so confirmations are always off.
        args.Add("--load-corrections");
        args.Add(UseLoadCorrections ? "true" : "false");
        string thresholdNote = string.Empty;
        if (UseLoadCorrections)
        {
            args.Add("--auto-add-db-items");
            args.Add(AutoAddDbItems ? "true" : "false");
            args.Add("--remap-missing-devices");
            args.Add(RemapMissingDevices ? "true" : "false");
            args.Add("--confirm-device-remap");
            args.Add("false");
            args.Add("--correction-mode");
            args.Add(string.IsNullOrWhiteSpace(CorrectionMode) ? "balanced" : CorrectionMode.Trim());

            string? normalizedThreshold = NormalizeCorrectionAutoThreshold(CorrectionAutoThreshold, out thresholdNote);
            if (!string.IsNullOrWhiteSpace(normalizedThreshold))
            {
                args.Add("--correction-auto-threshold");
                args.Add(normalizedThreshold);
            }
            args.Add("--confirm-corrections");
            args.Add("false");
            if (!string.IsNullOrWhiteSpace(CorrectionReportPath))
            {
                args.Add("--correction-report");
                args.Add(CorrectionReportPath.Trim());
            }
        }

        // Parsers need text output; the "Use Text Output" toggle only applies to hand-typed commands.
        if (isCustomCommand ? UseTextOutput : forceText)
            args.Add("--text");

        args.AddRange(commandArgs);

        // Queue engine calls instead of dropping them: background refreshes started by selection or stage
        // changes would otherwise swallow the user's next click without any feedback.
        pendingEngineCalls++;
        IsBusy = true;
        await engineCallGate.WaitAsync();
        // An engine command that uses the local AI model holds its idle stop until it ends (docs/natural-language-grouping.md).
        using IDisposable? aiWork = IsAiWorkCommand(commandList) ? llmServerMonitor.BeginWork() : null;
        try
        {
            // A read that waited behind other calls and is no longer wanted (a newer one of the same kind was asked
            // for meanwhile: the suggestions of the relation after the next add) does not run: queued reads made every
            // change after a big add wait for seconds of outdated engine runs.
            if (stillWanted != null && !stillWanted())
                return null;
            // Background checks (the game config watcher) keep the output pane and status line of the user's last command.
            if (background)
            {
                EngineCommandResult quiet = await cliClient.RunAsync(resolvedEnginePath, args);
                if (persistUiFiles)
                    AppendSessionLog(BuildOutputText(quiet, thresholdNote));
                return quiet;
            }

            showingBusyStatus = true;
            try
            {
                StatusText = T(busyLabel);
            }
            finally
            {
                showingBusyStatus = false;
            }
            LastCommandText = "engine " + string.Join(" ", args.Select(QuoteIfNeeded));

            // The engine saves the profile and records a version (the top bar shows "Saving..." meanwhile).
            bool writesProfile = mutatesProfile && !isCustomCommand;
            if (writesProfile)
                BeginProfileWrite();
            Dictionary<string, string> progressEnvironment = BeginBusyProgress(T(busyLabel));
            EngineCommandResult raw;
            try
            {
                raw = await cliClient.RunStreamingAsync(resolvedEnginePath, args,
                    new EngineRunOptions { StandardErrorLine = OnEngineStderrLine, Environment = progressEnvironment });
            }
            catch (Exception ex)
            {
                FinishBusyProgress(null);
                if (writesProfile)
                    EndProfileWrite(false, ex.Message);
                throw;
            }
            FinishBusyProgress(raw);
            if (writesProfile)
                EndProfileWrite(true, string.Empty);
            // Callers read stderr for the engine's warnings and errors: without the busy area's lines.
            EngineCommandResult result = new()
            {
                ExitCode = raw.ExitCode,
                Duration = raw.Duration,
                StandardOutput = raw.StandardOutput,
                StandardError = EngineProgressParser.StripProgress(raw.StandardError, keepStatus: IncludeStatusUpdates),
                Arguments = raw.Arguments
            };
            LastExitCode =result.ExitCode.ToString(CultureInfo.InvariantCulture);
            lastEngineFailureReason = result.ExitCode == 0 ? string.Empty : EngineFailureReason(result, 0);
            LastRunDuration = result.Duration.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

            OutputText = BuildOutputText(result, thresholdNote);
            // The session log (engine-output.log) keeps the progress too, thinned to a few lines per step.
            if (persistUiFiles)
                AppendSessionLog(BuildOutputText(new EngineCommandResult
                {
                    ExitCode = raw.ExitCode,
                    Duration = raw.Duration,
                    StandardOutput = raw.StandardOutput,
                    StandardError = EngineProgressParser.ThinForLog(raw.StandardError),
                    Arguments = raw.Arguments
                }, thresholdNote));
            if (result.ExitCode == 0 && (includeProfile || mutatesProfile))
                RememberLastProfilePath(ProfilePath);
            StatusText = result.ExitCode == 0
                ? EngineSuccessStatus(commandList, mutatesProfile, isCustomCommand, busyLabel)
                : LastEngineCallCancelled ? T("Cancelled: {0} stopped before anything was written.", T(busyLabel).TrimEnd('.')) : BuildFailureStatus(result);
            OnEngineCommandFinished(commandArgs as IReadOnlyList<string> ?? commandArgs.ToList(), result.ExitCode);
            return result;
        }
        catch (Exception ex)
        {
            StatusText = T("Command failed before execution completed.");
            OutputText = "$ " + LastCommandText + Environment.NewLine + ex;
            if (persistUiFiles)
                AppendSessionLog(OutputText);
            return null;
        }
        finally
        {
            engineCallGate.Release();
            pendingEngineCalls--;
            if (pendingEngineCalls <= 0)
            {
                pendingEngineCalls = 0;
                IsBusy = false;
                EndBusyProgress();
            }
        }
    }

    // The last status that says what happened (not the busy text of a running call): a list refresh after a change
    // (binds list after Set Bind, a page's refresh) keeps it instead of a bare "Done.".
    private string lastResultStatus = string.Empty;

    private bool showingBusyStatus;

    partial void OnStatusTextChanged(string value)
    {
        if (!showingBusyStatus)
            lastResultStatus = value ?? string.Empty;
    }

    // A read of the engine (lists, status) changes nothing: the status line keeps the result of the user's last step.
    // A change says which one it was ("Setting bind: done."); the flows say more where they know it ("Bound Pitch to ...").
    private string EngineSuccessStatus(IReadOnlyList<string> command, bool mutatesProfile, bool isCustomCommand, string busyLabel)
    {
        bool readOnly = !mutatesProfile && !isCustomCommand && !IsExportOrImportCommand(command);
        if (readOnly)
            return lastResultStatus.Length > 0 ? lastResultStatus : T("Ready.");
        string label = T(busyLabel ?? string.Empty).Trim().TrimEnd('.').Trim();
        return label.Length == 0 ? T("Done.") : T("{0}: done.", label);
    }

    // Surfaces the engine's error in the status bar so failures are visible without the output pane: the first item of
    // the report's "Errors:" section (stderr or stdout), else the first "engine error:" line, else the first stderr line.
    // The whole first error of the last engine call that failed (the status line shows it shortened); the import
    // wizard shows all of it, e.g. every folder an MSFS import searched.
    private string lastEngineFailureReason = string.Empty;

    // "Failed: " in the user's language (the export tiles take the reason out again).
    internal static string FailedPrefix => T("Failed: {0}", string.Empty);

    private static string BuildFailureStatus(EngineCommandResult result)
    {
        string reason = EngineFailureReason(result);
        return reason.Length == 0 ? T("Failed (see output).") : FailedPrefix + Translate(reason);
    }

    // The first error the engine reports, shortened for the status line (maxLength 0: the whole text).
    internal static string EngineFailureReason(EngineCommandResult result, int maxLength = 160)
    {
        string Shorten(string text) => maxLength > 3 && text.Length > maxLength ? text[..(maxLength - 3)] + "..." : text;
        foreach (string stream in new[] { result.StandardError ?? string.Empty, result.StandardOutput ?? string.Empty })
        {
            string[] lines = stream.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.Equals(lines[i].Trim(), "Errors:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? item = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                if (item != null && item.StartsWith("- ", StringComparison.Ordinal))
                    return Shorten(item[2..].Trim());
            }
        }
        foreach (string stream in new[] { result.StandardError ?? string.Empty, result.StandardOutput ?? string.Empty })
        {
            string? engineError = stream.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("engine error:", StringComparison.OrdinalIgnoreCase));
            if (engineError != null)
                return Shorten(engineError[T("engine error:").Length..].Trim());
        }
        string firstError = (result.StandardError ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        return Shorten(firstError);
    }

    private string BuildOutputText(EngineCommandResult result, string thresholdNote)
    {
        StringBuilder sb = new();
        sb.AppendLine("$ " + LastCommandText);
        if (!string.IsNullOrWhiteSpace(thresholdNote))
            sb.AppendLine("[gui-note] " + thresholdNote);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sb.AppendLine(result.StandardOutput.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("[stderr]");
            sb.AppendLine(result.StandardError.TrimEnd());
        }
        return sb.ToString();
    }

    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static List<string> ParseCsv(string csv)
    {
        return string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                 .Select(v => v.Trim())
                 .Where(v => v.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }

    private static string NormalizeNodePlaneCsv(string csv)
    {
        List<string> normalized = ParseCsv(csv)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0
            ? string.Empty
            : string.Join(",", normalized);
    }

    private List<string> BuildEffectiveDcsPlaneFilter()
    {
        if (ImportAllDcsPlanes)
            return [];

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in ImportPlaneOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Name))
                selected.Add(item.Name.Trim());
        }

        foreach (string plane in ParseCsv(ImportPlanesCsv))
            selected.Add(plane);

        if (ImportIgnoreEasyPlanes)
            selected.RemoveWhere(IsEasyPlaneName);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> BuildEffectiveImportDeviceFilter()
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        if (ImportUseSelectedDevices)
        {
            foreach (SelectableTagItem item in ImportDeviceOptions)
            {
                if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Name))
                    selected.Add(item.Name.Trim());
            }
        }

        foreach (string device in ParseCsv(ImportDevicesCsv))
            selected.Add(device);

        if (!ImportUseSelectedDevices && string.IsNullOrWhiteSpace(ImportDevicesCsv))
            return [];

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> BuildEffectiveImportIgnoredDeviceFilter()
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in ImportIgnoreDeviceOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Name))
                selected.Add(item.Name.Trim());
        }

        foreach (string device in ParseCsv(ImportIgnoreDevicesCsv))
            selected.Add(device);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildImportPlaneOptions(IEnumerable<string> planeNames)
    {
        HashSet<string> selected = ImportPlaneOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (planeNames != null)
        {
            foreach (string raw in planeNames)
            {
                string plane = (raw ?? string.Empty).Trim();
                if (plane.Length > 0)
                    all.Add(plane);
            }
        }
        foreach (string raw in ParseCsv(ImportPlanesCsv))
            all.Add(raw);

        ImportPlaneOptions.Clear();
        foreach (string plane in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            ImportPlaneOptions.Add(new SelectableTagItem(plane, selected.Contains(plane)));
    }

    private void RebuildDcsRefreshPlaneOptions(IEnumerable<string> planeNames)
    {
        HashSet<string> selected = DcsRefreshPlaneOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in ParseCsv(DcsRefreshPlanesCsv))
            selected.Add(raw);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (planeNames != null)
        {
            foreach (string raw in planeNames)
            {
                string plane = (raw ?? string.Empty).Trim();
                if (plane.Length > 0)
                    all.Add(plane);
            }
        }

        foreach (SelectableTagItem old in DcsRefreshPlaneOptions)
            old.PropertyChanged -= OnDcsRefreshPlaneOptionChanged;

        DcsRefreshPlaneOptions.Clear();
        foreach (string plane in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            SelectableTagItem item = new(plane, selected.Contains(plane));
            item.PropertyChanged += OnDcsRefreshPlaneOptionChanged;
            DcsRefreshPlaneOptions.Add(item);
        }

        SyncDcsRefreshPlaneCsvFromSelections();
    }

    private void OnDcsRefreshPlaneOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            SyncDcsRefreshPlaneCsvFromSelections();
    }

    private void SyncDcsRefreshPlaneCsvFromSelections()
    {
        if (syncingDcsRefreshPlaneOptions)
            return;

        try
        {
            syncingDcsRefreshPlaneOptions = true;
            DcsRefreshPlanesCsv = string.Join(
                ",",
                DcsRefreshPlaneOptions
                    .Where(v => v.IsSelected)
                    .Select(v => v.Name.Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            syncingDcsRefreshPlaneOptions = false;
        }
    }

    private void SetAllDcsRefreshPlaneSelections(bool selected)
    {
        try
        {
            syncingDcsRefreshPlaneOptions = true;
            foreach (SelectableTagItem item in DcsRefreshPlaneOptions)
                item.IsSelected = selected;
        }
        finally
        {
            syncingDcsRefreshPlaneOptions = false;
        }

        SyncDcsRefreshPlaneCsvFromSelections();
    }

    private void ResetDcsRefreshPlaneQuickToggles()
    {
        try
        {
            dcsRefreshPlaneQuickSelectGuard = true;
            DcsRefreshSelectAllPlanes = false;
            DcsRefreshSelectNonePlanes = false;
        }
        finally
        {
            dcsRefreshPlaneQuickSelectGuard = false;
        }
    }

    private List<string> BuildDcsRefreshPlaneCandidates()
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

        foreach (string plane in DiscoverDcsConfigPlanes(DcsSavedGamesPath))
            candidates.Add(plane);
        foreach (string plane in DiscoverDcsConfigPlanes(ImportDcsPath))
            candidates.Add(plane);
        foreach (string plane in DbPlanes)
        {
            string normalized = (plane ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }
        foreach (string plane in DiscoverDcsPlanesFromLocalDbFolder())
            candidates.Add(plane);
        foreach (string plane in ParseCsv(DcsRefreshPlanesCsv))
            candidates.Add(plane);

        return candidates
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> DiscoverDcsPlanesFromLocalDbFolder()
    {
        string enginePath = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(enginePath) || !File.Exists(enginePath))
            return [];

        string? engineDir = Path.GetDirectoryName(enginePath);
        if (string.IsNullOrWhiteSpace(engineDir))
            return [];

        string dbDcs = GameDataLocations.DbFolder(engineDir, "DCS");
        if (!Directory.Exists(dbDcs))
            return [];

        try
        {
            return Directory.GetFiles(dbDcs, "*.html", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private List<string> BuildEffectiveDcsRefreshPlaneFilter()
    {
        // Everything selected means "refresh all": let the engine use every DCS input profile it finds
        // instead of sending the list, which can contain names that are no DCS profile (old DB files, CSV).
        if (DcsRefreshPlaneOptions.Count > 0 && DcsRefreshPlaneOptions.All(v => v.IsSelected))
            return [];

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in DcsRefreshPlaneOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Name))
                selected.Add(item.Name.Trim());
        }

        foreach (string plane in ParseCsv(DcsRefreshPlanesCsv))
            selected.Add(plane);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> BuildLayoutPlaneCandidates()
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

        foreach (string plane in DbPlanes)
        {
            string normalized = (plane ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (SelectableTagItem item in ImportPlaneOptions)
        {
            string normalized = (item?.Name ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (SelectableTagItem item in DcsRefreshPlaneOptions)
        {
            string normalized = (item?.Name ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string plane in ParseCsv(LayoutPlaneCsv))
            candidates.Add(plane);

        return candidates
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> BuildLayoutDeviceCandidates()
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);

        foreach (string device in ConnectedDevices)
        {
            string normalized = (device ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string device in ProfileDevices)
        {
            string normalized = (device ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string device in DcsKnownDevices)
        {
            string normalized = (device ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (LayoutSuggestionItem suggestion in LayoutSuggestions)
        {
            string normalized = (suggestion.Device ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string key in layoutTemplateByDevice.Keys)
        {
            string normalized = (key ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string device in ParseCsv(LayoutDevicesCsv))
            candidates.Add(device);

        return candidates
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildLayoutDeviceOptions(IEnumerable<string> deviceNames)
    {
        HashSet<string> selected = LayoutDeviceOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in ParseCsv(LayoutDevicesCsv))
            selected.Add(raw);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (deviceNames != null)
        {
            foreach (string raw in deviceNames)
            {
                string device = (raw ?? string.Empty).Trim();
                if (device.Length > 0)
                    all.Add(device);
            }
        }

        foreach (SelectableTagItem old in LayoutDeviceOptions)
            old.PropertyChanged -= OnLayoutDeviceOptionChanged;

        LayoutDeviceOptions.Clear();
        foreach (string device in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            SelectableTagItem item = new(device, selected.Contains(device));
            item.PropertyChanged += OnLayoutDeviceOptionChanged;
            LayoutDeviceOptions.Add(item);
        }

        SyncLayoutDevicesCsvFromSelections();
    }

    private void OnLayoutDeviceOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            SyncLayoutDevicesCsvFromSelections();
    }

    private void SyncLayoutDevicesCsvFromSelections()
    {
        if (syncingLayoutDeviceOptions)
            return;

        try
        {
            syncingLayoutDeviceOptions = true;
            LayoutDevicesCsv = string.Join(
                ",",
                LayoutDeviceOptions
                    .Where(v => v.IsSelected)
                    .Select(v => (v.Value ?? string.Empty).Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            syncingLayoutDeviceOptions = false;
        }
    }

    private void SetAllLayoutDeviceSelections(bool selected)
    {
        try
        {
            syncingLayoutDeviceOptions = true;
            foreach (SelectableTagItem item in LayoutDeviceOptions)
                item.IsSelected = selected;
        }
        finally
        {
            syncingLayoutDeviceOptions = false;
        }

        SyncLayoutDevicesCsvFromSelections();
    }

    private void ResetLayoutDeviceQuickToggles()
    {
        try
        {
            layoutDeviceQuickSelectGuard = true;
            LayoutSelectAllDevices = false;
            LayoutSelectNoneDevices = false;
        }
        finally
        {
            layoutDeviceQuickSelectGuard = false;
        }
    }

    private void SetLayoutDeviceSelections(IEnumerable<string> selectedDevices)
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        if (selectedDevices != null)
        {
            foreach (string raw in selectedDevices)
            {
                string normalized = (raw ?? string.Empty).Trim();
                if (normalized.Length > 0)
                    selected.Add(normalized);
            }
        }

        try
        {
            syncingLayoutDeviceOptions = true;
            foreach (SelectableTagItem item in LayoutDeviceOptions)
                item.IsSelected = selected.Contains(item.Value);
        }
        finally
        {
            syncingLayoutDeviceOptions = false;
        }

        SyncLayoutDevicesCsvFromSelections();
        ResetLayoutDeviceQuickToggles();
    }

    private List<string> BuildEffectiveLayoutDeviceFilter()
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in LayoutDeviceOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Value))
                selected.Add(item.Value.Trim());
        }

        foreach (string device in ParseCsv(LayoutDevicesCsv))
            selected.Add(device);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateLayoutMapInputFromRememberedMappings(IEnumerable<string> selectedDevices)
    {
        List<string> mapped = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        if (selectedDevices != null)
        {
            foreach (string raw in selectedDevices)
            {
                string device = (raw ?? string.Empty).Trim();
                if (device.Length == 0 || !seen.Add(device))
                    continue;
                if (!TryResolveRememberedLayoutPathForDevice(device, out string layoutPath))
                    continue;
                mapped.Add("--layout-map \"" + device + "=" + layoutPath + "\"");
            }
        }

        LayoutMapInput = string.Join(" ", mapped);
    }

    private List<string> BuildLayoutGameCandidates()
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        // DbGames already leaves out the games the user does not have; so do the three built-in ones.
        foreach (string game in new[] { "DCS", "IL2Game", "StarCitizen" }.Where(g => !IsGameHidden(g)))
            candidates.Add(game);

        foreach (string game in DbGames)
        {
            string normalized = (game ?? string.Empty).Trim();
            if (normalized.Length > 0)
                candidates.Add(normalized);
        }

        foreach (string game in ParseCsv(LayoutGameCsv))
            candidates.Add(game);

        return candidates
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildLayoutGameOptions(IEnumerable<string> gameNames)
    {
        HashSet<string> selected = LayoutGameOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in ParseCsv(LayoutGameCsv))
            selected.Add(raw);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (gameNames != null)
        {
            foreach (string raw in gameNames)
            {
                string game = (raw ?? string.Empty).Trim();
                if (game.Length > 0)
                    all.Add(game);
            }
        }

        foreach (SelectableTagItem old in LayoutGameOptions)
            old.PropertyChanged -= OnLayoutGameOptionChanged;

        LayoutGameOptions.Clear();
        foreach (string game in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            SelectableTagItem item = new(game, selected.Contains(game));
            item.PropertyChanged += OnLayoutGameOptionChanged;
            LayoutGameOptions.Add(item);
        }

        SyncLayoutGameCsvFromSelections();
    }

    private void OnLayoutGameOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            SyncLayoutGameCsvFromSelections();
    }

    private void SyncLayoutGameCsvFromSelections()
    {
        if (syncingLayoutGameOptions)
            return;

        try
        {
            syncingLayoutGameOptions = true;
            LayoutGameCsv = string.Join(
                ",",
                LayoutGameOptions
                    .Where(v => v.IsSelected)
                    .Select(v => (v.Value ?? string.Empty).Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            syncingLayoutGameOptions = false;
        }
    }

    private void SetAllLayoutGameSelections(bool selected)
    {
        try
        {
            syncingLayoutGameOptions = true;
            foreach (SelectableTagItem item in LayoutGameOptions)
                item.IsSelected = selected;
        }
        finally
        {
            syncingLayoutGameOptions = false;
        }

        SyncLayoutGameCsvFromSelections();
    }

    private void ResetLayoutGameQuickToggles()
    {
        try
        {
            layoutGameQuickSelectGuard = true;
            LayoutSelectAllGames = false;
            LayoutSelectNoneGames = false;
        }
        finally
        {
            layoutGameQuickSelectGuard = false;
        }
    }

    private List<string> BuildEffectiveLayoutGameFilter()
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in LayoutGameOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Value))
                selected.Add(item.Value.Trim());
        }

        foreach (string game in ParseCsv(LayoutGameCsv))
            selected.Add(game);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildLayoutPlaneOptions(IEnumerable<string> planeNames)
    {
        HashSet<string> selected = LayoutPlaneOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in ParseCsv(LayoutPlaneCsv))
            selected.Add(raw);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (planeNames != null)
        {
            foreach (string raw in planeNames)
            {
                string plane = (raw ?? string.Empty).Trim();
                if (plane.Length > 0)
                    all.Add(plane);
            }
        }

        foreach (SelectableTagItem old in LayoutPlaneOptions)
            old.PropertyChanged -= OnLayoutPlaneOptionChanged;

        LayoutPlaneOptions.Clear();
        foreach (string plane in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            SelectableTagItem item = new(plane, selected.Contains(plane));
            item.PropertyChanged += OnLayoutPlaneOptionChanged;
            LayoutPlaneOptions.Add(item);
        }

        SyncLayoutPlaneCsvFromSelections();
    }

    private void OnLayoutPlaneOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            SyncLayoutPlaneCsvFromSelections();
    }

    private void SyncLayoutPlaneCsvFromSelections()
    {
        if (syncingLayoutPlaneOptions)
            return;

        try
        {
            syncingLayoutPlaneOptions = true;
            LayoutPlaneCsv = string.Join(
                ",",
                LayoutPlaneOptions
                    .Where(v => v.IsSelected)
                    .Select(v => v.Name.Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            syncingLayoutPlaneOptions = false;
        }
    }

    private void SetAllLayoutPlaneSelections(bool selected)
    {
        try
        {
            syncingLayoutPlaneOptions = true;
            foreach (SelectableTagItem item in LayoutPlaneOptions)
                item.IsSelected = selected;
        }
        finally
        {
            syncingLayoutPlaneOptions = false;
        }

        SyncLayoutPlaneCsvFromSelections();
    }

    private void ResetLayoutPlaneQuickToggles()
    {
        try
        {
            layoutPlaneQuickSelectGuard = true;
            LayoutSelectAllPlanes = false;
            LayoutSelectNonePlanes = false;
        }
        finally
        {
            layoutPlaneQuickSelectGuard = false;
        }
    }

    private List<string> BuildEffectiveLayoutPlaneFilter()
    {
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in LayoutPlaneOptions)
        {
            if (item.IsSelected && !string.IsNullOrWhiteSpace(item.Name))
                selected.Add(item.Name.Trim());
        }

        foreach (string plane in ParseCsv(LayoutPlaneCsv))
            selected.Add(plane);

        return selected
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PrefillDcsRefreshPaths()
    {
        string enginePath = ResolveEnginePath();
        string engineDir = string.IsNullOrWhiteSpace(enginePath) ? string.Empty : Path.GetDirectoryName(enginePath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(DcsClicksXmlPath) || !File.Exists(DcsClicksXmlPath))
        {
            List<string> xmlCandidates = [];
            if (!string.IsNullOrWhiteSpace(DcsSavedGamesPath))
            {
                xmlCandidates.Add(Path.Combine(DcsSavedGamesPath.Trim(), "Config", "Clicks.xml"));
                xmlCandidates.Add(Path.Combine(DcsSavedGamesPath.Trim(), "Config", "Input", "Clicks.xml"));
            }
            if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            {
                xmlCandidates.Add(Path.Combine(DcsInstallPath.Trim(), "Config", "Clicks.xml"));
                xmlCandidates.Add(Path.Combine(DcsInstallPath.Trim(), "Config", "Input", "Clicks.xml"));
            }
            if (!string.IsNullOrWhiteSpace(engineDir))
                xmlCandidates.Add(Path.Combine(engineDir, "Clicks", "Clicks.xml"));

            string existingXml = TryFindFirstExistingFile(xmlCandidates);
            if (existingXml.Length > 0)
                DcsClicksXmlPath = existingXml;
        }

        string preferredClicksDir = string.Empty;
        if (!string.IsNullOrWhiteSpace(DcsClicksXmlPath) && File.Exists(DcsClicksXmlPath))
        {
            preferredClicksDir = Path.GetDirectoryName(DcsClicksXmlPath) ?? string.Empty;
            preferredClicksDir = preferredClicksDir.Trim();
        }

        string engineClicksDir = string.IsNullOrWhiteSpace(engineDir) ? string.Empty : Path.Combine(engineDir, "Clicks");
        bool shouldAdoptPreferredClicksDir =
            preferredClicksDir.Length > 0 &&
            (string.IsNullOrWhiteSpace(DcsClicksDirPath) ||
             !Directory.Exists(DcsClicksDirPath) ||
             (engineClicksDir.Length > 0 && string.Equals(DcsClicksDirPath.Trim(), engineClicksDir, StringComparison.OrdinalIgnoreCase)));
        if (shouldAdoptPreferredClicksDir)
        {
            DcsClicksDirPath = preferredClicksDir;
        }

        if (string.IsNullOrWhiteSpace(DcsClicksDirPath) || !Directory.Exists(DcsClicksDirPath))
        {
            List<string> dirCandidates = [];
            if (!string.IsNullOrWhiteSpace(DcsClicksXmlPath) && File.Exists(DcsClicksXmlPath))
            {
                string? xmlDir = Path.GetDirectoryName(DcsClicksXmlPath);
                if (!string.IsNullOrWhiteSpace(xmlDir))
                    dirCandidates.Add(xmlDir);
            }
            if (engineClicksDir.Length > 0)
                dirCandidates.Add(engineClicksDir);

            string existingDir = TryFindFirstExistingDirectory(dirCandidates);
            if (existingDir.Length > 0)
                DcsClicksDirPath = existingDir;
        }

        if (string.IsNullOrWhiteSpace(DcsExePath) || !File.Exists(DcsExePath))
        {
            List<string> exeCandidates = [];
            if (!string.IsNullOrWhiteSpace(DcsInstallPath))
            {
                string install = DcsInstallPath.Trim();
                exeCandidates.Add(Path.Combine(install, "bin", "DCS.exe"));
                exeCandidates.Add(Path.Combine(install, "bin-mt", "DCS.exe"));
                exeCandidates.Add(Path.Combine(install, "bin", "DCS_updater.exe"));
                exeCandidates.Add(Path.Combine(install, "bin-mt", "DCS_updater.exe"));
            }

            string guessedInstall = GuessDcsInstallPath();
            if (!string.IsNullOrWhiteSpace(guessedInstall))
            {
                exeCandidates.Add(Path.Combine(guessedInstall, "bin", "DCS.exe"));
                exeCandidates.Add(Path.Combine(guessedInstall, "bin-mt", "DCS.exe"));
                exeCandidates.Add(Path.Combine(guessedInstall, "bin", "DCS_updater.exe"));
                exeCandidates.Add(Path.Combine(guessedInstall, "bin-mt", "DCS_updater.exe"));
            }

            string existingExe = TryFindFirstExistingFile(exeCandidates);
            if (existingExe.Length > 0)
                DcsExePath = existingExe;
        }

        if (string.IsNullOrWhiteSpace(DcsPreviewImagePath))
        {
            string docsCandidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "JoyPro2",
                "dcs-click-preview.png");
            if (File.Exists(docsCandidate))
            {
                DcsPreviewImagePath = docsCandidate;
            }
        }
    }

    private void PrefillStarCitizenSourcePath()
    {
        if (!string.IsNullOrWhiteSpace(StarCitizenSourcePath) && Directory.Exists(StarCitizenSourcePath))
            return;

        List<string> baseCandidates = [];
        if (!string.IsNullOrWhiteSpace(ImportStarCitizenPath))
            baseCandidates.Add(ImportStarCitizenPath.Trim());
        if (!string.IsNullOrWhiteSpace(ExportStarCitizenPath))
            baseCandidates.Add(ExportStarCitizenPath.Trim());

        string guessed = GuessStarCitizenProfilePath();
        if (!string.IsNullOrWhiteSpace(guessed))
            baseCandidates.Add(guessed);

        List<string> dirCandidates = [];
        foreach (string raw in baseCandidates)
        {
            string path = (raw ?? string.Empty).Trim();
            if (path.Length == 0)
                continue;

            if (Directory.Exists(path))
            {
                dirCandidates.Add(path);
                dirCandidates.Add(Path.Combine(path, "Mappings"));
                dirCandidates.Add(Path.Combine(path, "defaultProfile"));
            }

            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                dirCandidates.Add(parent);
                dirCandidates.Add(Path.Combine(parent, "Mappings"));
                dirCandidates.Add(Path.Combine(parent, "defaultProfile"));
            }
        }

        foreach (string candidate in dirCandidates
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Select(v => v.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(candidate))
                continue;

            string actionMaps = Path.Combine(candidate, "actionmaps.xml");
            if (File.Exists(actionMaps))
            {
                StarCitizenSourcePath = candidate;
                return;
            }

            if (Directory.Exists(Path.Combine(candidate, "Mappings")))
            {
                StarCitizenSourcePath = Path.Combine(candidate, "Mappings");
                return;
            }
        }

        string existing = TryFindFirstExistingDirectory(dirCandidates);
        if (existing.Length > 0)
            StarCitizenSourcePath = existing;
    }

    private void PrefillLayoutAutomationPaths()
    {
        string engineExe = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engineExe) || !File.Exists(engineExe))
            return;

        string? engineDir = Path.GetDirectoryName(engineExe);
        if (string.IsNullOrWhiteSpace(engineDir))
            return;

        string catalog = Path.Combine(engineDir, "LayoutCatalog", "device-layout-catalog.json");
        if (string.IsNullOrWhiteSpace(LayoutCatalogPath) && File.Exists(catalog))
            LayoutCatalogPath = catalog;

        string templatesDir = Path.Combine(engineDir, "LayoutTemplates");
        if (!Directory.Exists(templatesDir))
            return;

        HashSet<string> dirs = ParseCsv(LayoutLegacyLayoutDirs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        dirs.Add(templatesDir);
        LayoutLegacyLayoutDirs = string.Join(",", dirs.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
    }

    private static string TryFindFirstExistingFile(IEnumerable<string> candidates)
    {
        foreach (string raw in candidates ?? [])
        {
            string candidate = (raw ?? string.Empty).Trim();
            if (candidate.Length == 0)
                continue;
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string TryFindFirstExistingDirectory(IEnumerable<string> candidates)
    {
        foreach (string raw in candidates ?? [])
        {
            string candidate = (raw ?? string.Empty).Trim();
            if (candidate.Length == 0)
                continue;
            if (Directory.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private void SeedDcsFfbDeviceSettingsFromKnownDevices()
    {
        HashSet<string> selected = DcsFfbDeviceSettings
            .Where(v => v.IsSelected)
            .Select(v => v.Device)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
        foreach (string device in DcsKnownDevices)
            known.Add(device);
        foreach (string device in ConnectedDevices)
            known.Add(device);
        foreach (string device in ProfileDevices)
            known.Add(device);
        List<DcsFfbDeviceSettingItem> merged = DcsFfbDeviceSettings.ToList();
        foreach (string device in known.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            if (merged.Any(v => string.Equals(v.Device, device, StringComparison.OrdinalIgnoreCase)))
                continue;

            merged.Add(new DcsFfbDeviceSettingItem(device)
            {
                IsSelected = selected.Contains(device),
                HasSetting = false,
                Connected = ConnectedDevices.Contains(device, StringComparer.OrdinalIgnoreCase),
                Profile = ProfileDevices.Contains(device, StringComparer.OrdinalIgnoreCase)
            });
        }

        ReplaceDcsFfbDeviceSettings(merged);
    }

    private void ParseDcsFfbList(string output)
    {
        HashSet<string> selected = DcsFfbDeviceSettings
            .Where(v => v.IsSelected)
            .Select(v => v.Device)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<DcsFfbDeviceSettingItem> parsed = [];
        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = DcsFfbListLine.Match(raw.TrimEnd());
            if (!match.Success)
                continue;

            string device = match.Groups["device"].Value.Trim();
            if (device.Length == 0)
                continue;

            DcsFfbDeviceSettingItem entry = new(device)
            {
                IsSelected = selected.Contains(device),
                InvertX = match.Groups["invertX"].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                InvertY = match.Groups["invertY"].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                SwapAxis = match.Groups["swapAxis"].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                HasSetting = match.Groups["hasSetting"].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                Connected = match.Groups["connected"].Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                Profile = match.Groups["profile"].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
            };
            parsed.Add(entry);
        }

        if (parsed.Count == 0)
        {
            SeedDcsFfbDeviceSettingsFromKnownDevices();
            return;
        }

        ReplaceDcsFfbDeviceSettings(parsed);
    }

    private void ReplaceDcsFfbDeviceSettings(IEnumerable<DcsFfbDeviceSettingItem> items)
    {
        foreach (DcsFfbDeviceSettingItem old in DcsFfbDeviceSettings)
            old.PropertyChanged -= OnDcsFfbDeviceSettingChanged;

        try
        {
            syncingDcsFfbDeviceSettings = true;
            DcsFfbDeviceSettings.Clear();

            foreach (DcsFfbDeviceSettingItem item in items
                         .Where(v => !string.IsNullOrWhiteSpace(v.Device))
                         .OrderBy(v => v.Device, StringComparer.OrdinalIgnoreCase))
            {
                item.PropertyChanged += OnDcsFfbDeviceSettingChanged;
                DcsFfbDeviceSettings.Add(item);
            }
        }
        finally
        {
            syncingDcsFfbDeviceSettings = false;
        }

        RebuildVisibleDcsFfbDeviceSettings();
    }

    private void RebuildVisibleDcsFfbDeviceSettings()
    {
        IEnumerable<DcsFfbDeviceSettingItem> source = DcsFfbDeviceSettings;
        if (DcsFfbShowConnectedOnly)
            source = source.Where(v => v.Connected);
        if (DcsFfbShowProfileOnly)
            source = source.Where(v => v.Profile);

        string filter = (DcsFfbFilterText ?? string.Empty).Trim();
        if (filter.Length > 0)
        {
            source = source.Where(v =>
                v.Device.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        List<DcsFfbDeviceSettingItem> visible = source
            .OrderBy(v => v.Device, StringComparer.OrdinalIgnoreCase)
            .ToList();

        VisibleDcsFfbDeviceSettings.Clear();
        foreach (DcsFfbDeviceSettingItem item in visible)
            VisibleDcsFfbDeviceSettings.Add(item);
    }

    private void OnDcsFfbDeviceSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DcsFfbDeviceSettingItem item)
            return;
        if (syncingDcsFfbDeviceSettings)
            return;

        if (e.PropertyName == nameof(DcsFfbDeviceSettingItem.Connected) ||
            e.PropertyName == nameof(DcsFfbDeviceSettingItem.Profile) ||
            e.PropertyName == nameof(DcsFfbDeviceSettingItem.IsSelected))
        {
            RebuildVisibleDcsFfbDeviceSettings();
        }

        if (!DcsFfbAutoApplyOnToggle)
            return;
        if (e.PropertyName != nameof(DcsFfbDeviceSettingItem.InvertX) &&
            e.PropertyName != nameof(DcsFfbDeviceSettingItem.InvertY) &&
            e.PropertyName != nameof(DcsFfbDeviceSettingItem.SwapAxis))
        {
            return;
        }

        _ = ApplyDcsFfbToggleChangeAsync(item);
    }

    private async Task ApplyDcsFfbToggleChangeAsync(DcsFfbDeviceSettingItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Device))
            return;

        EngineCommandResult? result = await RunDcsFfbSetForDeviceAsync(
            item.Device.Trim(),
            item.InvertX,
            item.InvertY,
            item.SwapAxis,
            "Applying DCS FFB toggle for " + item.Device + "...");
        if (result == null || result.ExitCode != 0)
            return;

        await DcsFfbListAsync();
    }

    private void RebuildRelationNodeGameOptions()
    {
        string current = (RelationNodeGameInput ?? string.Empty).Trim();
        if (current.Length == 0)
            current = "DCS";

        RelationNodeGameOptions.Clear();
        // One sorted list (the fixed games used to come first, the rest after them).
        List<string> games = ["DCS", "IL2Game", "StarCitizen", .. DbGames, .. dbNodeCatalogByGame.Keys];
        foreach (string game in games.OrderBy(v => NormalizeRelationGameToken(v), StringComparer.OrdinalIgnoreCase))
            AddRelationNodeGameOption(game);

        if (!RelationNodeGameOptions.Any(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase)))
            RelationNodeGameInput = RelationNodeGameOptions.Count > 0 ? RelationNodeGameOptions[0] : "DCS";
    }

    private void AddRelationNodeGameOption(string game)
    {
        string normalized = NormalizeRelationGameToken(game);
        // Games the user does not have (Settings > Games) are not offered.
        if (normalized.Length == 0 || IsGameHidden(normalized))
            return;
        if (!RelationNodeGameOptions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            RelationNodeGameOptions.Add(normalized);
    }

    [RelayCommand]
    private async Task RefreshRelationNodeCatalogAsync()
    {
        await RefreshDbGamesAsync();
        await EnsureDbNodeCatalogLoadedAsync();
        RebuildRelationNodeGameOptions();
        await RefreshRelationNodePlaneOptionsAsync();
    }

    private async Task RefreshRelationNodePlaneOptionsAsync(IEnumerable<string>? preferredSelected = null)
    {
        await EnsureDbNodeCatalogLoadedAsync();
        string game = NormalizeRelationGameToken(RelationNodeGameInput);
        if (game.Length == 0)
            game = "DCS";

        string scopedNodeId = (SelectedRelationNodeSearchResult?.NodeId ?? editingRelationNode?.Id ?? SelectedRelationNode?.Id ?? string.Empty).Trim();
        string scopedGame = NormalizeRelationGameToken((SelectedRelationNodeSearchResult?.Game ?? editingRelationNode?.Game ?? SelectedRelationNode?.Game) ?? string.Empty);
        if (scopedGame.Length == 0)
            scopedGame = game;
        if (!string.IsNullOrWhiteSpace(scopedNodeId) &&
            TryBuildRelationNodeScopedPlaneOptions(scopedGame, scopedNodeId, preferredSelected))
        {
            return;
        }

        if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase) &&
            ImportPlaneOptions.Count > 0)
        {
            RebuildRelationNodePlaneOptions(ImportPlaneOptions.Select(v => v.Name), preferredSelected);
            return;
        }

        string fallbackPlane = DbNodeCatalogEntry.DefaultPlaneFor(game);
        if (!dbNodeCatalogByGame.TryGetValue(game, out List<DbNodeCatalogEntry>? entries) ||
            entries.Count == 0)
        {
            RebuildRelationNodePlaneOptions([fallbackPlane, .. RegistryAircraftOf(game).Select(a => a.Key)], preferredSelected);
            return;
        }

        List<string> planes = entries
            .Select(v => v.Plane)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (planes.Count == 0)
            planes.Add(fallbackPlane);
        planes.AddRange(RegistryAircraftOf(game).Select(a => a.Key));

        RebuildRelationNodePlaneOptions(planes, preferredSelected);
    }

    private bool TryBuildRelationNodeScopedPlaneOptions(string game, string nodeId, IEnumerable<string>? preferredSelected)
    {
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(nodeId))
            return false;
        if (!dbNodeCatalogByGame.TryGetValue(game, out List<DbNodeCatalogEntry>? entries) ||
            entries.Count == 0)
            return false;

        List<DbNodeCatalogEntry> matches = entries
            .Where(v => !string.IsNullOrWhiteSpace(v.NodeId) &&
                        v.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Plane, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0)
            return false;

        HashSet<string> selected = preferredSelected == null
            ? RelationNodePlaneOptions
                .Where(v => v.IsSelected)
                .Select(v => v.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : preferredSelected
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, (string Title, string Description)> firstByPlane = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < matches.Count; i++)
        {
            DbNodeCatalogEntry entry = matches[i];
            string plane = string.IsNullOrWhiteSpace(entry.Plane) ? T("Default") : entry.Plane.Trim();
            if (!firstByPlane.ContainsKey(plane))
                firstByPlane.Add(plane, (entry.Title, entry.Description));
        }
        // Games with an aircraft list (MSFS): every aircraft next to the game name (= all aircraft).
        foreach (RegistryAircraft aircraft in RegistryAircraftOf(game))
            firstByPlane.TryAdd(aircraft.Key, (aircraft.Title, aircraft.Description));

        foreach (SelectableTagItem existing in RelationNodePlaneOptions)
            existing.PropertyChanged -= OnRelationNodePlaneOptionChanged;
        RelationNodePlaneOptions.Clear();

        foreach (KeyValuePair<string, (string Title, string Description)> pair in firstByPlane
                     .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            string plane = pair.Key;
            string display = BuildPlaneOptionDisplay(plane, pair.Value.Title, pair.Value.Description);
            SelectableTagItem item = new(display, selected.Contains(plane), plane);
            item.PropertyChanged += OnRelationNodePlaneOptionChanged;
            RelationNodePlaneOptions.Add(item);
        }

        RelationNodePlanesInput = BuildSelectedRelationNodePlanesCsv();
        return true;
    }

    private void RebuildRelationNodePlaneOptions(IEnumerable<string> planeNames, IEnumerable<string>? preferredSelected = null)
    {
        HashSet<string> selected = preferredSelected == null
            ? RelationNodePlaneOptions
                .Where(v => v.IsSelected)
                .Select(v => v.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : preferredSelected
                .Select(v => (v ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        if (planeNames != null)
        {
            foreach (string raw in planeNames)
            {
                string plane = (raw ?? string.Empty).Trim();
                if (plane.Length > 0)
                    all.Add(plane);
            }
        }

        foreach (SelectableTagItem existing in RelationNodePlaneOptions)
            existing.PropertyChanged -= OnRelationNodePlaneOptionChanged;
        RelationNodePlaneOptions.Clear();
        foreach (string plane in all.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            SelectableTagItem item = new(plane, selected.Contains(plane), plane);
            item.PropertyChanged += OnRelationNodePlaneOptionChanged;
            RelationNodePlaneOptions.Add(item);
        }

        RelationNodePlanesInput = BuildSelectedRelationNodePlanesCsv();
    }

    private string BuildSelectedRelationNodePlanesCsv()
    {
        List<string> selectedPlanes = RelationNodePlaneOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return selectedPlanes.Count == 0
            ? string.Empty
            : string.Join(",", selectedPlanes);
    }

    [RelayCommand]
    private async Task SearchRelationNodesAsync()
    {
        relationNodeSearchResultSource.Clear();
        RelationNodeSearchResults.Clear();
        SelectedRelationNodeSearchResult = null;
        RelationNodeSearchStatus = string.Empty;

        string query = (RelationNodeSearchText ?? string.Empty).Trim();
        string category = SelectedRelationNodeCategory == AllRelationNodeCategories ? string.Empty : (SelectedRelationNodeCategory ?? string.Empty).Trim();
        if (query.Length == 0 && category.Length == 0)
        {
            RelationNodeSearchStatus = T("Type a search term first (ID, title, or description).");
            return;
        }

        await EnsureDbNodeCatalogLoadedAsync();

        string game = NormalizeRelationGameToken(RelationNodeGameInput);
        if (game.Length == 0)
            game = "DCS";

        if (!dbNodeCatalogByGame.TryGetValue(game, out List<DbNodeCatalogEntry>? source) ||
            source.Count == 0)
        {
            RelationNodeSearchStatus = T("No DB node catalog loaded for game '{0}'.", game);
            return;
        }
        RebuildRelationNodeCategoryOptions(source);
        if (category.Length > 0 && !RelationNodeCategoryOptions.Contains(category))
            category = string.Empty;

        string[] terms = query
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0 && category.Length == 0)
        {
            RelationNodeSearchStatus = T("Type a search term first (ID, title, or description).");
            return;
        }

        // Every term matches the id, title, description or aircraft ("flaps spitfire"). The aircraft ticks filter before
        // the result cap: with the cap first, "flaps" (600+ rows over all aircraft) cut off the late aircraft (Spitfire).
        HashSet<string> tickedPlanes = SelectedRelationNodeSearchResult == null && editingRelationNode == null
            ? RelationNodePlaneOptions.Where(v => v.IsSelected && !string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        IEnumerable<DbNodeCatalogEntry> filtered = source.Where(entry =>
            (category.Length == 0 || entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
            (tickedPlanes.Count == 0 || tickedPlanes.Contains((entry.Plane ?? string.Empty).Trim())) &&
            terms.All(term => NodeEntryMatches(entry, term)));

        List<DbNodeCatalogEntry> ranked = filtered
            .Select(entry => new { Entry = entry, Score = ComputeNodeSearchScore(entry, terms) })
            .OrderByDescending(v => v.Score)
            .ThenBy(v => v.Entry.Plane, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Entry.NodeId, StringComparer.OrdinalIgnoreCase)
            .Take(2000)
            .Select(v => v.Entry)
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
        {
            DbNodeCatalogEntry entry = ranked[i];
            relationNodeSearchResultSource.Add(new DbNodeSearchResultItem
            {
                Game = entry.Game,
                Plane = entry.Plane,
                NodeId = entry.NodeId,
                Title = entry.Title,
                Description = entry.Description,
                NodeSpec = BuildNodeSpec(entry.Game, entry.NodeId, string.Empty),
                Category = entry.Category
            });
        }

        ApplyRelationNodePlaneFilterToSearchResults();
        string what = query.Length == 0 ? "category " + category : "'" + query + "'";
        RelationNodeSearchStatus = RelationNodeSearchResults.Count == 0
            ? T("No node matches for {0} in {1}.", what, game)
            : T("{0} match(es) in {1}. Select one and click Add to relation.", RelationNodeSearchResults.Count, game);
    }

    private static bool NodeEntryMatches(DbNodeCatalogEntry entry, string term) =>
        NodeTextMatches(entry.NodeId, entry.Title, entry.Description, entry.Plane, term) ||
        entry.Keys.Contains(term, StringComparison.OrdinalIgnoreCase);

    internal static bool NodeTextMatches(string? id, string? title, string? description, string? plane, string term) =>
        (id ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (title ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (description ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase) ||
        (plane ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase);

    private static int ComputeNodeSearchScore(DbNodeCatalogEntry entry, IReadOnlyList<string> terms)
    {
        int score = 0;
        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];
            if (entry.NodeId.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 5000;
            else if (entry.NodeId.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1200;

            if (entry.Title.Equals(term, StringComparison.OrdinalIgnoreCase))
                score += 2200;
            else if (entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 800;

            if (entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 300;
        }

        return score;
    }

    public void SortRelationNodeSearchResults(string sortMemberPath, ListSortDirection direction)
    {
        if (relationNodeSearchResultSource.Count == 0 && RelationNodeSearchResults.Count > 0)
            relationNodeSearchResultSource.AddRange(RelationNodeSearchResults);

        if (relationNodeSearchResultSource.Count <= 1)
            return;

        string key = (sortMemberPath ?? string.Empty).Trim();
        if (key.Length == 0)
            return;

        Func<DbNodeSearchResultItem, string> selector = key switch
        {
            nameof(DbNodeSearchResultItem.Game) => v => v.Game,
            nameof(DbNodeSearchResultItem.Plane) => v => v.Plane,
            nameof(DbNodeSearchResultItem.NodeId) => v => v.NodeId,
            nameof(DbNodeSearchResultItem.Title) => v => v.Title,
            nameof(DbNodeSearchResultItem.Description) => v => v.Description,
            nameof(DbNodeSearchResultItem.Category) => v => v.Category,
            _ => v => v.NodeId
        };

        List<DbNodeSearchResultItem> ordered = direction == ListSortDirection.Ascending
            ? relationNodeSearchResultSource.OrderBy(selector, StringComparer.OrdinalIgnoreCase).ToList()
            : relationNodeSearchResultSource.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase).ToList();

        relationNodeSearchResultSource.Clear();
        relationNodeSearchResultSource.AddRange(ordered);
        ApplyRelationNodePlaneFilterToSearchResults();
    }

    private void OnRelationNodePlaneOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableTagItem.IsSelected))
            return;

        RelationNodePlanesInput = BuildSelectedRelationNodePlanesCsv();
        if (SelectedRelationNodeSearchResult == null && editingRelationNode == null)
            ApplyRelationNodePlaneFilterToSearchResults();
    }

    private void ApplyRelationNodePlaneFilterToSearchResults()
    {
        if (relationNodeSearchResultSource.Count == 0)
        {
            RelationNodeSearchResults.Clear();
            return;
        }

        IEnumerable<DbNodeSearchResultItem> source = relationNodeSearchResultSource;
        bool usePlaneFilter = SelectedRelationNodeSearchResult == null && editingRelationNode == null;
        if (usePlaneFilter)
        {
            HashSet<string> selectedPlanes = RelationNodePlaneOptions
                .Where(v => v.IsSelected)
                .Select(v => v.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedPlanes.Count > 0)
                source = source.Where(v => selectedPlanes.Contains((v.Plane ?? string.Empty).Trim()));
        }

        RelationNodeSearchResults.Clear();
        foreach (DbNodeSearchResultItem item in source)
            RelationNodeSearchResults.Add(item);
    }

    private static string BuildPlaneOptionDisplay(string plane, string title, string description)
    {
        string cleanPlane = string.IsNullOrWhiteSpace(plane) ? T("Default") : plane.Trim();
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "<no title>" : title.Trim();
        string cleanDescription = string.IsNullOrWhiteSpace(description) ? "<no description>" : description.Trim();
        return cleanPlane + " | " + cleanTitle + " | " + cleanDescription;
    }

    private async Task EnsureDbNodeCatalogLoadedAsync()
    {
        if (dbNodeCatalogLoaded)
            return;

        await dbNodeCatalogLock.WaitAsync();
        try
        {
            if (dbNodeCatalogLoaded)
                return;

            BuildDbNodeCatalog();
            dbNodeCatalogLoaded = true;
        }
        finally
        {
            dbNodeCatalogLock.Release();
        }
    }

    private void BuildDbNodeCatalog()
    {
        dbNodeCatalogByGame.Clear();
        LoadDcsDbNodeCatalog();
        LoadIl2DbNodeCatalog();
        LoadStarCitizenDbNodeCatalog();
        LoadRegistryGameDbNodeCatalogs();
    }

    private void LoadDcsDbNodeCatalog()
    {
        string dcsDbDirectory = GetDcsDbDirectory();
        if (!Directory.Exists(dcsDbDirectory))
            return;

        List<DbNodeCatalogEntry> entries = [];
        foreach (string file in Directory.GetFiles(dcsDbDirectory, "*.html"))
        {
            string plane = Path.GetFileNameWithoutExtension(file);
            string html;
            try
            {
                html = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            // Titles stored as a localization key resolve like in the relation editor (DCS's English text, sibling file,
            // else "Flaps 1"); the key itself is never shown.
            Dictionary<string, string>? resolved = null;
            foreach (Match match in DcsDbSearchRowLine.Matches(html))
            {
                string nodeId = CleanHtmlCell(match.Groups["hash"].Value);
                if (string.IsNullOrWhiteSpace(nodeId))
                    continue;

                string title = CleanHtmlCell(match.Groups["command"].Value);
                string description = DcsCommandNames.ResolveCategory(CleanHtmlCell(match.Groups["description"].Value), DcsEnglishNames());
                // The key stays findable ("flaps_1"): the stored key, or the keys DCS's English catalog has for the title.
                string keys = DcsCommandNames.IsLocalizationKey(title) ? title.Trim() : string.Join(" ", DcsCommandNames.KeysFor(title, DcsEnglishNames()));
                if (IsRawDcsTitle(title, nodeId))
                {
                    resolved ??= LoadDcsTitleFile(file, DcsEnglishNames());
                    if (!resolved.TryGetValue(nodeId, out string? better) || string.IsNullOrWhiteSpace(better))
                        continue;
                    title = better;
                }

                entries.Add(new DbNodeCatalogEntry(
                    "DCS",
                    string.IsNullOrWhiteSpace(plane) ? "Default" : plane.Trim(),
                    nodeId,
                    string.IsNullOrWhiteSpace(title) ? nodeId : title,
                    description,
                    CleanHtmlCell(match.Groups["category"].Value)) { Keys = keys });
            }
        }

        dbNodeCatalogByGame["DCS"] = entries
            .Distinct(DbNodeCatalogEntryComparer.Instance)
            .ToList();
    }

    private void LoadIl2DbNodeCatalog()
    {
        string il2ActionsPath = Path.Combine(GetIl2DbDirectory(), "IL2.actions");
        if (!File.Exists(il2ActionsPath))
            return;

        List<DbNodeCatalogEntry> entries = [];
        try
        {
            foreach (string line in File.ReadLines(il2ActionsPath))
            {
                Match match = Il2ActionLine.Match(line);
                if (!match.Success)
                    continue;

                string action = match.Groups["action"].Value.Trim();
                if (action.Length == 0)
                    continue;

                string title = match.Groups["title"].Value.Trim();
                entries.Add(new DbNodeCatalogEntry(
                    "IL2Game",
                    "IL2Game",
                    action,
                    title.Length == 0 ? action : title,
                    title));
            }
        }
        catch
        {
            entries.Clear();
        }

        dbNodeCatalogByGame["IL2Game"] = entries
            .Distinct(DbNodeCatalogEntryComparer.Instance)
            .ToList();
    }

    private void LoadStarCitizenDbNodeCatalog()
    {
        string scDbDirectory = GetStarCitizenDbDirectory();
        if (!Directory.Exists(scDbDirectory))
            return;

        string actionMapsPath = Path.Combine(scDbDirectory, "actionmaps.xml");
        if (!File.Exists(actionMapsPath))
            return;

        List<DbNodeCatalogEntry> entries = [];
        try
        {
            XDocument doc = XDocument.Load(actionMapsPath);
            IEnumerable<XElement> actionMaps = doc.Descendants("actionmap");
            foreach (XElement actionMap in actionMaps)
            {
                string mapName = ((string?)actionMap.Attribute("name") ?? string.Empty).Trim();
                foreach (XElement action in actionMap.Elements("action"))
                {
                    string id = ((string?)action.Attribute("name") ?? string.Empty).Trim();
                    if (id.Length == 0)
                        continue;

                    XComment? comment = action.Nodes().OfType<XComment>().FirstOrDefault();
                    string commentText = comment?.Value ?? string.Empty;
                    string title = ParseStarCitizenTitleFromComment(commentText, id);
                    string description = T("Action map: {0}", (mapName.Length == 0 ? "<unknown>" : mapName));
                    // Node IDs as the engine's importer writes them: "actionmap|action".
                    entries.Add(new DbNodeCatalogEntry(
                        "StarCitizen",
                        "StarCitizen",
                        mapName.Length == 0 ? id : mapName + "|" + id,
                        title,
                        description,
                        mapName));
                }
            }
        }
        catch
        {
            entries.Clear();
        }

        dbNodeCatalogByGame["StarCitizen"] = entries
            .Distinct(DbNodeCatalogEntryComparer.Instance)
            .ToList();
    }

    private static string ParseStarCitizenTitleFromComment(string commentText, string fallbackId)
    {
        string comment = (commentText ?? string.Empty).Trim();
        if (comment.Length == 0)
            return fallbackId;

        int split = comment.IndexOf("//", StringComparison.Ordinal);
        if (split >= 0)
            comment = comment.Substring(0, split).Trim();

        return comment.Length == 0 ? fallbackId : comment;
    }

    private void RebuildImportDeviceOptions()
    {
        HashSet<string> selected = ImportDeviceOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> devices = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in ConnectedDevices)
        {
            string device = (raw ?? string.Empty).Trim();
            if (device.Length > 0)
                devices.Add(device);
        }
        foreach (string raw in ProfileDevices)
        {
            string device = (raw ?? string.Empty).Trim();
            if (device.Length > 0)
                devices.Add(device);
        }
        foreach (string raw in DcsKnownDevices)
        {
            string device = (raw ?? string.Empty).Trim();
            if (device.Length > 0)
                devices.Add(device);
        }
        foreach (string raw in ParseCsv(ImportDevicesCsv))
            devices.Add(raw);
        devices.Add(T("Keyboard"));

        ImportDeviceOptions.Clear();
        foreach (string device in devices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            ImportDeviceOptions.Add(new SelectableTagItem(device, selected.Contains(device)));

        RebuildImportIgnoreDeviceOptions();
    }

    private void RebuildImportIgnoreDeviceOptions()
    {
        HashSet<string> selected = ImportIgnoreDeviceOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> devices = new(StringComparer.OrdinalIgnoreCase);
        foreach (SelectableTagItem item in ImportDeviceOptions)
        {
            if (!string.IsNullOrWhiteSpace(item.Name))
                devices.Add(item.Name.Trim());
        }
        foreach (string raw in ParseCsv(ImportIgnoreDevicesCsv))
            devices.Add(raw);

        ImportIgnoreDeviceOptions.Clear();
        foreach (string device in devices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            ImportIgnoreDeviceOptions.Add(new SelectableTagItem(device, selected.Contains(device)));
    }

    private static List<string> ParseDbPlaneList(string output)
    {
        HashSet<string> planes = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
            return [];

        foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("DB Planes:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.EndsWith(")", StringComparison.Ordinal) && line.Contains('('))
                continue;
            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                string plane = line.TrimStart('-', ' ').Trim();
                if (plane.Length > 0)
                    planes.Add(plane);
                continue;
            }
            if (line.Length > 0 &&
                !line.Contains("|", StringComparison.Ordinal) &&
                !line.Contains("(", StringComparison.Ordinal))
            {
                planes.Add(line);
            }
        }

        return planes
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsEasyPlaneName(string value)
    {
        string token = (value ?? string.Empty).Trim();
        return token.EndsWith("_easy", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> DiscoverDcsConfigPlanes(string savedGamesPath)
    {
        HashSet<string> planes = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(savedGamesPath))
            return [];

        string inputRoot = Path.Combine(savedGamesPath.Trim(), "Config", "Input");
        if (!Directory.Exists(inputRoot))
            return [];

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(inputRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dir)?.Trim() ?? string.Empty;
                if (name.Length > 0)
                    planes.Add(name);
            }
        }
        catch
        {
            return [];
        }

        return planes
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void InitializeSessionLog()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SessionLogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(SessionLogPath))
                File.Delete(SessionLogPath);
            File.WriteAllText(SessionLogPath, string.Empty);
        }
        catch
        {
            // Ignore logging initialization issues.
        }
    }

    private void AppendSessionLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            string stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]"
                             + Environment.NewLine
                             + text.TrimEnd()
                             + Environment.NewLine
                             + Environment.NewLine;
            File.AppendAllText(SessionLogPath, stamped);
        }
        catch
        {
            // Ignore logging issues so UI flow never breaks.
        }
    }

    // The one title resolver of the GUI (DcsCommandNames): the aircraft's DB title (a localization key there becomes
    // DCS's English text, else the sibling file's title), else the title the profile stored for the node (engine
    // "relations show" stored-title lines), else a readable form of a key. Never the raw id or lua key.
    private string ResolveNodeTitle(string game, string plane, string nodeId, string? storedTitle = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return string.Empty;

        if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase))
        {
            string title = TryGetDcsNodeTitle(plane, nodeId);
            if (!string.IsNullOrWhiteSpace(title) && !IsRawDcsTitle(title, nodeId.Trim()))
                return title;
            string resolved = DcsCommandNames.Resolve(title, nodeId.Trim(), DcsEnglishNames(), storedTitle);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
            // The id is not in this aircraft's command list (any more) and the profile stored no title: say so instead
            // of showing the raw id.
            return T("Command not in this aircraft's current list");
        }
        else if (game.StartsWith("IL2", StringComparison.OrdinalIgnoreCase))
        {
            string title = TryGetIl2NodeTitle(nodeId);
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        if (!string.IsNullOrWhiteSpace(storedTitle) && !string.Equals(storedTitle.Trim(), nodeId.Trim(), StringComparison.OrdinalIgnoreCase))
            return storedTitle.Trim();
        return nodeId;
    }

    // "id|plane" -> stored title, from the "stored-title<TAB>plane<TAB>title<TAB>category" lines under each node line
    // of "relations show".
    internal static Dictionary<string, string> ParseStoredNodeTitles(string output)
    {
        Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
        string currentId = string.Empty;
        foreach (string raw in (output ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match node = RelationNodeLine.Match(line.TrimEnd());
            if (node.Success)
            {
                currentId = node.Groups["id"].Value.Trim();
                continue;
            }
            string trimmed = line.TrimStart();
            if (currentId.Length == 0 || !trimmed.StartsWith("stored-title\t", StringComparison.Ordinal))
                continue;
            string[] parts = trimmed.Split('\t');
            if (parts.Length >= 3 && parts[1].Trim().Length > 0 && parts[2].Trim().Length > 0)
                titles[currentId + "|" + parts[1].Trim()] = parts[2].Trim();
        }
        return titles;
    }

    internal static string? StoredNodeTitle(Dictionary<string, string> stored, string nodeId, string plane) =>
        stored.TryGetValue(nodeId.Trim() + "|" + (plane ?? string.Empty).Trim(), out string? title) ? title : null;

    // DCS's English command names of the DCS install JoyPro uses (empty without one).
    private IDictionary<string, string> DcsEnglishNames() => DcsCommandNames.EnglishCatalog(DcsInstallPath);

    // When a title lookup misses, the DB folder is looked at again (at most every few seconds per aircraft): a DB refresh
    // or a module installed after the first lookup otherwise left the editor on raw ids until a restart.
    private readonly Dictionary<string, DateTime> dcsTitleRecheckByPlane = new(StringComparer.OrdinalIgnoreCase);

    private string TryGetDcsNodeTitle(string plane, string nodeId)
    {
        // "Default" is the DB file name, never a translated word.
        string normalizedPlane = string.IsNullOrWhiteSpace(plane) ? "Default" : plane.Trim();
        string planeKey = NormalizeDbKey(normalizedPlane);
        string id = nodeId.Trim();
        if (!dcsTitleCacheByPlane.TryGetValue(planeKey, out Dictionary<string, string>? titleMap))
        {
            titleMap = LoadDcsTitleMap(normalizedPlane);
            dcsTitleCacheByPlane[planeKey] = titleMap;
            dcsTitleRecheckByPlane[planeKey] = DateTime.UtcNow;
        }
        else if (!titleMap.ContainsKey(id) &&
                 (!dcsTitleRecheckByPlane.TryGetValue(planeKey, out DateTime last) || DateTime.UtcNow - last > TimeSpan.FromSeconds(5)))
        {
            dcsTitleRecheckByPlane[planeKey] = DateTime.UtcNow;
            dcsDbFilesIndexed = false;
            titleMap = LoadDcsTitleMap(normalizedPlane);
            dcsTitleCacheByPlane[planeKey] = titleMap;
        }

        return titleMap.TryGetValue(id, out string? title)
            ? title
            : string.Empty;
    }

    // "Input.SpitfireLFMkIX.flaps_1", "29Fulcrum:Inp:...": some modules name their commands by a localization key, and a
    // DB built before the refresh translated them holds the key (DcsCommandNames).
    internal static bool IsRawDcsTitle(string title, string id) => DcsCommandNames.IsRawTitle(title, id);

    // "Input.SpitfireLFMkIX.flaps_1" -> "Flaps 1"; any other text stays as it is.
    internal static string ReadableDcsKey(string value) => DcsCommandNames.ReadableKey(value);

    // Titles of one DCS DB file; a title stored as a localization key takes DCS's English text (englishNames), else the
    // title of the same id from the aircraft's sibling files (SpitfireLFMkIX_easy, SpitfireLFMkIXCW, ...), else a
    // readable form of the key ("Flaps 1").
    internal static Dictionary<string, string> LoadDcsTitleFile(string filePath, IDictionary<string, string>? englishNames = null)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        ReadDcsTitleFile(filePath, map, skipRaw: false);
        List<string> raw = map.Where(p => IsRawDcsTitle(p.Value, p.Key)).Select(p => p.Key).ToList();
        if (raw.Count == 0)
            return map;

        Dictionary<string, string>? sibling = null;
        foreach (string id in raw)
        {
            string resolved = DcsCommandNames.Resolve(map[id], id, englishNames, null, () =>
            {
                if (sibling == null)
                {
                    sibling = new(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in DcsSiblingFiles(filePath))
                        ReadDcsTitleFile(file, sibling, skipRaw: true);
                }
                return sibling.TryGetValue(id, out string? better) ? better : null;
            });
            if (!string.IsNullOrWhiteSpace(resolved))
                map[id] = resolved;
            else
                map.Remove(id);
        }
        return map;
    }

    private Dictionary<string, string> LoadDcsTitleMap(string plane) =>
        TryResolveDcsPlaneFile(plane, out string filePath) ? LoadDcsTitleFile(filePath, DcsEnglishNames()) : new(StringComparer.OrdinalIgnoreCase);

    // The titles of one DB file as stored (id -> title), for the tests.
    internal static Dictionary<string, string> ReadDcsTitleRows(string filePath)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        ReadDcsTitleFile(filePath, map, skipRaw: false);
        return map;
    }

    private static void ReadDcsTitleFile(string filePath, Dictionary<string, string> map, bool skipRaw)
    {
        string html;
        try
        {
            html = File.ReadAllText(filePath);
        }
        catch
        {
            return;
        }

        foreach (Match match in DcsDbRowLine.Matches(html))
        {
            string hash = CleanHtmlCell(match.Groups["hash"].Value);
            if (string.IsNullOrWhiteSpace(hash) || map.ContainsKey(hash))
                continue;

            string command = CleanHtmlCell(match.Groups["command"].Value);
            if (skipRaw && IsRawDcsTitle(command, hash))
                continue;
            map[hash] = string.IsNullOrWhiteSpace(command) ? hash : command;
        }
    }

    // The aircraft's other DB files: "<name>_easy", "<name>CW", "<name>CW_easy" (same base name, another variant).
    internal static List<string> DcsSiblingFiles(string filePath)
    {
        string? folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];
        string name = Path.GetFileNameWithoutExtension(filePath);
        string stem = name.EndsWith("_easy", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        return Directory.GetFiles(folder, "*.html")
            .Where(f => !string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileNameWithoutExtension(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length)
            .ToList();
    }

    private bool TryResolveDcsPlaneFile(string plane, out string filePath)
    {
        filePath = string.Empty;
        EnsureDcsDbFileIndex();
        if (dcsDbFilesByKey.Count == 0)
            return false;

        string value = string.IsNullOrWhiteSpace(plane) ? "Default" : plane.Trim();
        string[] candidates =
        [
            value,
            value.Replace('_', ' '),
            value.Replace(' ', '_'),
            NormalizeDbKey(value),
            NormalizeDbKey(value.Replace('_', ' ')),
            NormalizeDbKey(value.Replace(' ', '_'))
        ];

        foreach (string candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (dcsDbFilesByKey.TryGetValue(candidate, out string? resolved))
            {
                filePath = resolved;
                return true;
            }
        }

        return false;
    }

    private void EnsureDcsDbFileIndex()
    {
        if (dcsDbFilesIndexed)
            return;

        dcsDbFilesIndexed = true;
        dcsDbFilesByKey.Clear();

        string dcsDbDirectory = GetDcsDbDirectory();
        if (!Directory.Exists(dcsDbDirectory))
            return;

        foreach (string file in Directory.GetFiles(dcsDbDirectory, "*.html"))
        {
            string planeName = Path.GetFileNameWithoutExtension(file);
            AddDcsDbFileIndex(planeName, file);
            AddDcsDbFileIndex(planeName.Replace('_', ' '), file);
            AddDcsDbFileIndex(planeName.Replace(' ', '_'), file);
            AddDcsDbFileIndex(NormalizeDbKey(planeName), file);
        }
    }

    private void AddDcsDbFileIndex(string key, string filePath)
    {
        if (string.IsNullOrWhiteSpace(key) || dcsDbFilesByKey.ContainsKey(key))
            return;
        dcsDbFilesByKey[key] = filePath;
    }

    private string TryGetIl2NodeTitle(string nodeId)
    {
        EnsureIl2TitleCache();
        return il2TitleCache.TryGetValue(nodeId.Trim(), out string? title)
            ? title
            : string.Empty;
    }

    private void EnsureIl2TitleCache()
    {
        if (il2TitleCacheLoaded)
            return;

        il2TitleCacheLoaded = true;
        il2TitleCache.Clear();

        string il2ActionsPath = Path.Combine(GetIl2DbDirectory(), "IL2.actions");
        if (!File.Exists(il2ActionsPath))
            return;

        try
        {
            foreach (string line in File.ReadLines(il2ActionsPath))
            {
                Match match = Il2ActionLine.Match(line);
                if (!match.Success)
                    continue;

                string action = match.Groups["action"].Value.Trim();
                if (string.IsNullOrWhiteSpace(action) || il2TitleCache.ContainsKey(action))
                    continue;

                string title = match.Groups["title"].Value.Trim();
                il2TitleCache[action] = string.IsNullOrWhiteSpace(title) ? action : title;
            }
        }
        catch
        {
            il2TitleCache.Clear();
        }
    }

    // The game lists built on this PC live in <data>\GameData, a development build's copies in the engine folder
    // (GameDataLocations, docs/installs.md "Game data").
    private string GetDcsDbDirectory() => GetGameDbDirectory("DCS");

    private string GetIl2DbDirectory() => GetGameDbDirectory("IL2");

    private string GetStarCitizenDbDirectory() => GetGameDbDirectory("StarCitizen");

    private string GetGameDbDirectory(string folder)
    {
        string engineExe = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engineExe))
            return string.Empty;
        string? engineDirectory = Path.GetDirectoryName(engineExe);
        if (string.IsNullOrWhiteSpace(engineDirectory))
            return string.Empty;
        return GameDataLocations.DbFolder(engineDirectory, folder);
    }

    private static string NormalizeRelationGameToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
            return string.Empty;

        if (token.Equals("DCS", StringComparison.OrdinalIgnoreCase))
            return "DCS";
        if (token.Equals("IL2", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
            return "IL2Game";
        if (token.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("SC", StringComparison.OrdinalIgnoreCase))
            return "StarCitizen";
        return token;
    }

    private static string NormalizeDbKey(string value)
    {
        StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static string CleanHtmlCell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string withoutTags = HtmlTagRegex.Replace(value, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static string BuildNodeSpec(string game, string nodeId, string planesCsv)
    {
        string g = NormalizeRelationGameToken(game);
        if (string.IsNullOrWhiteSpace(g))
            g = "DCS";
        string id = nodeId.Trim();
        string normalizedPlanes = NormalizePlanesCsv(planesCsv);
        string planes = string.IsNullOrWhiteSpace(normalizedPlanes) ? string.Empty : ":" + normalizedPlanes;
        return g + ":" + id + planes;
    }

    private static string NormalizePlanesCsv(string planesCsv)
    {
        if (string.IsNullOrWhiteSpace(planesCsv))
            return string.Empty;

        string value = planesCsv.Trim();
        if (value.Equals("<none>", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return value;
    }

    private static IEnumerable<string> TokenizeCli(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        List<string> tokens = [];
        StringBuilder current = new();
        bool inQuotes = false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static int ParseOrDefault(string value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static double ParseDoubleOrDefault(string value, double fallback, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static string? NormalizeCorrectionAutoThreshold(string rawValue, out string note)
    {
        note = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        string value = rawValue.Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
        {
            if (parsedInt < 0 || parsedInt > 200)
            {
                note = T("Ignoring invalid correction threshold '{0}' (allowed: 0..200).", value);
                return null;
            }
            return parsedInt.ToString(CultureInfo.InvariantCulture);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsedDouble))
        {
            int normalized;
            if (parsedDouble >= 0 && parsedDouble <= 2)
                normalized = (int)Math.Round(parsedDouble * 100.0, MidpointRounding.AwayFromZero);
            else if (parsedDouble >= 0 && parsedDouble <= 200)
                normalized = (int)Math.Round(parsedDouble, MidpointRounding.AwayFromZero);
            else
            {
                note = T("Ignoring invalid correction threshold '{0}' (allowed: 0..200).", value);
                return null;
            }

            normalized = Math.Clamp(normalized, 0, 200);
            if (!string.Equals(value, normalized.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                note = T("Normalized correction threshold '{0}' -> '{1}'.", value, normalized.ToString(CultureInfo.InvariantCulture));
            }
            return normalized.ToString(CultureInfo.InvariantCulture);
        }

        note = T("Ignoring invalid correction threshold '{0}' (allowed: 0..200).", value);
        return null;
    }

    private void SelectRelationByName(string relationName)
    {
        string target = (relationName ?? string.Empty).Trim();
        if (target.Length == 0)
            return;

        RelationListItem? match = Relations.FirstOrDefault(v =>
            string.Equals(v.Name, target, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            // The filters hide it: they go off (the search box too).
            if (RelationFilter.IsFiltered)
                RelationFilter.ClearAll();

            match = Relations.FirstOrDefault(v =>
                string.Equals(v.Name, target, StringComparison.OrdinalIgnoreCase));
        }

        if (match != null)
            SelectedRelation = match;
    }

    private void RebuildVisibleRelations()
    {
        RebuildRelationSubjects();
        ApplyRelationFilter();
    }

    // The list box shows these relations (filtered and sorted).
    private void FillRelationList(List<RelationListItem> shown)
    {
        // Clearing the list drops the list box's selection: the open relation is selected again afterwards (by name, the
        // items are new), without loading it again. Before, the editor kept the relation's actions with no relation
        // selected, and the "how to start" steps were drawn over them.
        string? openRelation = SelectedRelation?.Name;
        rebuildingVisibleRelations = true;
        RelationListItem? reselect = null;
        try
        {
            if (!Relations.SequenceEqual(shown))
            {
                // The multi-select list box keeps the indexes of its selection through a Reset and reads them from the
                // emptied list (ArgumentOutOfRange after a JoyPro assistant answer created a relation): the selection
                // goes first and comes back below.
                if (SelectedRelation != null)
                    SelectedRelation = null;
                Relations.Clear();
                foreach (RelationListItem relation in shown)
                    Relations.Add(relation);
            }
            if (openRelation != null)
            {
                reselect = Relations.FirstOrDefault(r => string.Equals(r.Name, openRelation, StringComparison.Ordinal))
                           ?? Relations.FirstOrDefault(r => string.Equals(r.Name, openRelation, StringComparison.OrdinalIgnoreCase));
                SelectedRelation = reselect;
            }
        }
        finally
        {
            rebuildingVisibleRelations = false;
        }
        // The open relation is gone (deleted, renamed, filtered out): the editor shows no relation.
        if (openRelation != null && reselect == null)
            OnSelectedRelationChanged(null);
    }

    // RebuildVisibleRelations is filling the list again (the selection drops and comes back).
    private bool rebuildingVisibleRelations;

    // The rows of every relation (bound or not) and their filter subjects, built when relations or binds change.
    private List<BindListItem> bindRows = [];
    private List<FilterSubject> bindSubjects = [];

    private void RebuildVisibleBinds()
    {
        EnsureListFilters();
        bindRows = BuildBindRows();
        bindSubjects = BuildBindSubjects(bindRows);
        BindFilter.SetSubjects(bindSubjects);
        ApplyBindFilter();
    }

    private void ApplyBindFilter()
    {
        Stopwatch watch = Stopwatch.StartNew();
        string deviceFilter = BindDeviceFilterText?.Trim() ?? string.Empty;
        List<BindListItem> passing = [];
        for (int i = 0; i < bindRows.Count; i++)
        {
            BindListItem row = bindRows[i];
            if (!BindFilter.Matches(i < bindSubjects.Count ? bindSubjects[i] : null))
                continue;
            if (deviceFilter.Length > 0 && !row.Device.Contains(deviceFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            passing.Add(row);
        }
        IEnumerable<BindListItem> query = ApplyBindSorting(passing);

        // Clearing the list drops the grid selection; without restoring it, bind actions would silently
        // fall back to SelectedRelation, which can be a different relation than the one on screen.
        string? selectedRelationName = SelectedBind?.RelationName;
        restoringBindSelection = true;
        try
        {
            Binds.Clear();
            foreach (BindListItem bind in query)
            {
                bind.DeviceIcon = DeviceIconFor(bind.Device);
                Binds.Add(bind);
            }

            if (!string.IsNullOrWhiteSpace(selectedRelationName))
            {
                SelectedBind = Binds.FirstOrDefault(b =>
                    string.Equals(b.RelationName, selectedRelationName, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            restoringBindSelection = false;
        }
        watch.Stop();
        BindFilterMilliseconds = watch.Elapsed.TotalMilliseconds;
        OnPropertyChanged(nameof(HasAnyBinds));
        OnPropertyChanged(nameof(HasNoBinds));
        OnPropertyChanged(nameof(HasNoBindMatches));
    }

    // Relations exist, the filters hide every one.
    public bool HasNoBindMatches => bindRows.Count > 0 && Binds.Count == 0;

    private List<BindListItem> BuildBindRows()
    {
        Dictionary<string, BindListItem> boundByRelation = allBinds
            .Where(v => !string.IsNullOrWhiteSpace(v.RelationName))
            .GroupBy(v => v.RelationName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<BindListItem> rows = [];
        foreach (RelationListItem relation in allRelations)
        {
            bool relationHasBind = TryParseRelationBindToken(relation.Bind, out string relationDevice, out string relationInput);
            if (boundByRelation.TryGetValue(relation.Name, out BindListItem? bound))
            {
                // The engine's list may hold a relation without device and input: that one is not bound.
                bool hasBind = bound.HasBind || relationHasBind;
                rows.Add(new BindListItem
                {
                    RelationName = relation.Name,
                    Type = NormalizeBindType(string.IsNullOrWhiteSpace(relation.Type) ? bound.Type : relation.Type),
                    TypeDisplay = NormalizeBindType(string.IsNullOrWhiteSpace(relation.Type) ? bound.Type : relation.Type),
                    HasBind = hasBind,
                    BindState = hasBind ? "Bound" : "Unbound",
                    Device = string.IsNullOrWhiteSpace(bound.Device)
                        ? (string.IsNullOrWhiteSpace(relationDevice) ? "<unbound>" : relationDevice)
                        : bound.Device,
                    Input = string.IsNullOrWhiteSpace(bound.Input)
                        ? (string.IsNullOrWhiteSpace(relationInput) ? "<unbound>" : relationInput)
                        : bound.Input,
                    ModifiersSummary = string.IsNullOrWhiteSpace(bound.ModifiersSummary) ? "<none>" : bound.ModifiersSummary
                });
                CopyBindBehavior(rows[^1], bound);
            }
            else if (relationHasBind)
            {
                rows.Add(new BindListItem
                {
                    RelationName = relation.Name,
                    Type = NormalizeBindType(relation.Type),
                    TypeDisplay = NormalizeBindType(relation.Type),
                    HasBind = true,
                    BindState = "Bound",
                    Device = string.IsNullOrWhiteSpace(relationDevice) ? "<unbound>" : relationDevice,
                    Input = string.IsNullOrWhiteSpace(relationInput) ? "<unbound>" : relationInput,
                    ModifiersSummary = "<unknown>"
                });
            }
            else
            {
                rows.Add(new BindListItem
                {
                    RelationName = relation.Name,
                    Type = NormalizeBindType(relation.Type),
                    TypeDisplay = NormalizeBindType(relation.Type),
                    HasBind = false,
                    BindState = "Unbound",
                    Device = "<unbound>",
                    Input = "<unbound>",
                    ModifiersSummary = "<none>"
                });
            }
        }

        HashSet<string> listed = rows.Select(v => v.RelationName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (BindListItem bound in allBinds)
        {
            if (!listed.Add(bound.RelationName))
                continue;

            rows.Add(new BindListItem
            {
                RelationName = bound.RelationName,
                Type = NormalizeBindType(bound.Type),
                TypeDisplay = NormalizeBindType(bound.Type),
                HasBind = true,
                BindState = "Bound",
                Device = string.IsNullOrWhiteSpace(bound.Device) ? "<unbound>" : bound.Device,
                Input = string.IsNullOrWhiteSpace(bound.Input) ? "<unbound>" : bound.Input,
                ModifiersSummary = string.IsNullOrWhiteSpace(bound.ModifiersSummary) ? "<none>" : bound.ModifiersSummary
            });
            CopyBindBehavior(rows[^1], bound);
        }

        foreach (BindListItem row in rows)
            row.IsAxis = string.Equals(row.Type, "axis", StringComparison.OrdinalIgnoreCase);

        return rows;
    }

    private static void CopyBindBehavior(BindListItem target, BindListItem source)
    {
        target.Inverted = source.Inverted;
        target.Slider = source.Slider;
        target.DoubleTap = source.DoubleTap;
        target.Deadzone = source.Deadzone;
        target.SaturationX = source.SaturationX;
        target.SaturationY = source.SaturationY;
        target.CurvatureCsv = source.CurvatureCsv;
    }

    private IEnumerable<BindListItem> ApplyBindSorting(IEnumerable<BindListItem> source)
    {
        Dictionary<string, RelationListItem> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (RelationListItem relation in allRelations)
            byName.TryAdd(relation.Name, relation);
        return BindSort.Sort(source, bind => SortRowOf(bind, byName.GetValueOrDefault(bind.RelationName)));
    }

    private void RebuildQuickBindOptions()
    {
        string snapshotBindDevice = BindDeviceInput ?? string.Empty;
        string snapshotBindInput = BindInputValue ?? string.Empty;
        string snapshotModifierDevice = ModifierCreateDevice ?? string.Empty;
        string snapshotModifierInput = ModifierCreateInput ?? string.Empty;

        HashSet<string> devices = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> inputs = new(StringComparer.OrdinalIgnoreCase);

        foreach (RelationListItem relation in allRelations)
        {
            if (!TryParseRelationBindToken(relation.Bind, out string relationDevice, out string relationInput))
                continue;
            if (!string.IsNullOrWhiteSpace(relationDevice))
                devices.Add(relationDevice.Trim());
            if (!string.IsNullOrWhiteSpace(relationInput))
                inputs.Add(relationInput.Trim());
        }

        foreach (BindListItem bind in allBinds)
        {
            if (!string.IsNullOrWhiteSpace(bind.Device) && !bind.Device.Equals("<unbound>", StringComparison.OrdinalIgnoreCase))
                devices.Add(bind.Device.Trim());
            if (!string.IsNullOrWhiteSpace(bind.Input) && !bind.Input.Equals("<unbound>", StringComparison.OrdinalIgnoreCase))
                inputs.Add(bind.Input.Trim());
        }

        foreach (string device in ProfileDevices)
        {
            if (!string.IsNullOrWhiteSpace(device))
                devices.Add(device.Trim());
        }

        foreach (string device in ConnectedDevices)
        {
            if (!string.IsNullOrWhiteSpace(device))
                devices.Add(device.Trim());
        }

        foreach (string device in DcsKnownDevices)
        {
            if (!string.IsNullOrWhiteSpace(device))
                devices.Add(device.Trim());
        }

        devices.Add(T("Keyboard"));

        foreach (string input in DeviceInputsCatalog)
        {
            if (!string.IsNullOrWhiteSpace(input))
                inputs.Add(input.Trim());
        }

        if (!string.IsNullOrWhiteSpace(BindDeviceInput))
            devices.Add(BindDeviceInput.Trim());
        if (!string.IsNullOrWhiteSpace(BindInputValue))
            inputs.Add(BindInputValue.Trim());
        if (!string.IsNullOrWhiteSpace(ModifierCreateDevice))
            devices.Add(ModifierCreateDevice.Trim());
        if (!string.IsNullOrWhiteSpace(ModifierCreateInput))
            inputs.Add(ModifierCreateInput.Trim());

        // The driver entries Windows lists ("HID-compliant game controller", "vJoy Driver") are no device to bind to.
        devices.RemoveWhere(d => DriverDeviceNames.Contains(d) && !string.Equals(d, snapshotBindDevice.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(d, snapshotModifierDevice.Trim(), StringComparison.OrdinalIgnoreCase));

        QuickBindDeviceOptions.Clear();
        foreach (string device in devices.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            QuickBindDeviceOptions.Add(device);

        QuickBindInputOptions.Clear();
        foreach (string input in inputs.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            QuickBindInputOptions.Add(input);

        if (!string.IsNullOrWhiteSpace(snapshotBindDevice))
            BindDeviceInput = snapshotBindDevice;
        if (!string.IsNullOrWhiteSpace(snapshotBindInput))
            BindInputValue = snapshotBindInput;
        if (!string.IsNullOrWhiteSpace(snapshotModifierDevice))
            ModifierCreateDevice = snapshotModifierDevice;
        if (!string.IsNullOrWhiteSpace(snapshotModifierInput))
            ModifierCreateInput = snapshotModifierInput;

        RebuildLayoutEditorInputOptions();
        RebuildBindPickOptions();
    }

    private void RebuildLayoutEditorInputOptions()
    {
        string normalizedType = NormalizeLayoutInputType(LayoutEditorNewType);
        string current = (LayoutEditorNewToken ?? string.Empty).Trim();
        HashSet<string> options = new(StringComparer.OrdinalIgnoreCase);

        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers)
        {
            string token = (marker.Token ?? string.Empty).Trim();
            if (token.Length == 0)
                continue;
            string markerType = NormalizeLayoutInputType(marker.Type);
            if (markerType == normalizedType)
                options.Add(token);
        }

        foreach (string input in DeviceInputsCatalog)
        {
            string token = (input ?? string.Empty).Trim();
            if (token.Length == 0 || !IsLayoutInputTypeMatch(token, normalizedType))
                continue;
            options.Add(token);
        }

        foreach (string input in QuickBindInputOptions)
        {
            string token = (input ?? string.Empty).Trim();
            if (token.Length == 0 || !IsLayoutInputTypeMatch(token, normalizedType))
                continue;
            options.Add(token);
        }

        foreach (string fallback in GetDefaultLayoutInputsForType(normalizedType))
            options.Add(fallback);

        if (current.Length > 0 && IsLayoutInputTypeMatch(current, normalizedType))
            options.Add(current);

        List<string> sorted = options
            .OrderBy(GetLayoutInputSortGroup)
            .ThenBy(GetLayoutInputSortNumber)
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LayoutEditorInputOptions.Clear();
        foreach (string item in sorted)
            LayoutEditorInputOptions.Add(item);

        RebuildLayoutEditorMarkerTokenOptions();
    }

    private void RebuildLayoutEditorMarkerTokenOptions()
    {
        string snapshot = (LayoutEditorNewConnectorTargetInput ?? string.Empty).Trim();
        List<string> markerTokens = LayoutEditorMarkers
            .Select(v => (v.Token ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LayoutEditorMarkerTokenOptions.Clear();
        foreach (string token in markerTokens)
            LayoutEditorMarkerTokenOptions.Add(token);

        if (snapshot.Length == 0)
        {
            if (SelectedLayoutEditorMarker != null && !string.IsNullOrWhiteSpace(SelectedLayoutEditorMarker.Token))
                LayoutEditorNewConnectorTargetInput = SelectedLayoutEditorMarker.Token.Trim();
            else if (LayoutEditorMarkerTokenOptions.Count > 0)
                LayoutEditorNewConnectorTargetInput = LayoutEditorMarkerTokenOptions[0];
            return;
        }

        if (!LayoutEditorMarkerTokenOptions.Any(v => string.Equals(v, snapshot, StringComparison.OrdinalIgnoreCase)))
            LayoutEditorNewConnectorTargetInput = snapshot;
    }

    private void RebuildLayoutEditorModifierOptions()
    {
        HashSet<string> selected = new(SplitLayoutModifierNames(LayoutEditorNewModifier), StringComparer.OrdinalIgnoreCase);
        HashSet<string> available = new(selected, StringComparer.OrdinalIgnoreCase);

        foreach (string modifier in ModifierNames)
        {
            string normalized = (modifier ?? string.Empty).Trim();
            if (normalized.Length > 0)
                available.Add(normalized);
        }

        foreach (LayoutEditorMarkerItem marker in LayoutEditorMarkers)
        {
            foreach (string modifier in SplitLayoutModifierNames(marker.Modifier))
                available.Add(modifier);
        }

        foreach ((SelectableTagItem option, PropertyChangedEventHandler handler) in layoutEditorModifierOptionHandlers)
            option.PropertyChanged -= handler;
        layoutEditorModifierOptionHandlers.Clear();
        LayoutEditorModifierOptions.Clear();

        syncingLayoutEditorModifierOptions = true;
        try
        {
            foreach (string modifier in available.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            {
                SelectableTagItem option = new(modifier, selected.Contains(modifier));
                PropertyChangedEventHandler handler = (_, args) =>
                {
                    if (args.PropertyName == nameof(SelectableTagItem.IsSelected))
                        SyncLayoutEditorModifierFromOptionSelection();
                };
                option.PropertyChanged += handler;
                layoutEditorModifierOptionHandlers[option] = handler;
                LayoutEditorModifierOptions.Add(option);
            }
        }
        finally
        {
            syncingLayoutEditorModifierOptions = false;
        }

        SyncLayoutEditorModifierFromOptionSelection();
    }

    private void SyncLayoutEditorModifierFromOptionSelection()
    {
        if (syncingLayoutEditorModifierOptions)
            return;

        string csv = string.Join(
            ",",
            LayoutEditorModifierOptions
                .Where(v => v.IsSelected)
                .Select(v => (v.Name ?? string.Empty).Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        if (!string.Equals(LayoutEditorNewModifier, csv, StringComparison.Ordinal))
            LayoutEditorNewModifier = csv;
        else
            LayoutEditorModifierPreview = string.IsNullOrWhiteSpace(csv) ? "<none>" : csv;
    }

    private static IEnumerable<string> SplitLayoutModifierNames(string csv)
    {
        return (csv ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetDefaultLayoutInputsForType(string normalizedType)
    {
        switch (normalizedType)
        {
            case "axis":
                return
                [
                    "JOY_X",
                    "JOY_Y",
                    "JOY_Z",
                    "JOY_RX",
                    "JOY_RY",
                    "JOY_RZ"
                ];
            case "slider":
                return
                [
                    "JOY_SLIDER1",
                    "JOY_SLIDER2"
                ];
            case "pov":
            {
                List<string> pov = [];
                for (int hat = 1; hat <= 8; hat++)
                {
                    foreach (string dir in new[] { "U", "D", "L", "R" })
                        pov.Add("JOY_BTN_POV" + hat.ToString(CultureInfo.InvariantCulture) + "_" + dir);
                }

                return pov;
            }
            default:
                return Enumerable
                    .Range(1, 128)
                    .Select(i => "JOY_BTN" + i.ToString(CultureInfo.InvariantCulture))
                    .ToList();
        }
    }

    private static string NormalizeLayoutInputType(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "axis" => "axis",
            "pov" => "pov",
            "slider" => "slider",
            _ => "button"
        };
    }

    private static bool IsLayoutInputTypeMatch(string input, string normalizedType)
    {
        string token = (input ?? string.Empty).Trim();
        if (token.Length == 0)
            return false;

        string upper = token.ToUpperInvariant();
        string detectedType;
        if (upper.Contains("POV", StringComparison.Ordinal))
            detectedType = "pov";
        else if (upper.Contains("SLIDER", StringComparison.Ordinal))
            detectedType = "slider";
        else if (upper.StartsWith("JOY_BTN", StringComparison.Ordinal))
            detectedType = "button";
        else if (upper.StartsWith("JOY_", StringComparison.Ordinal))
            detectedType = "axis";
        else
            detectedType = "button";

        return string.Equals(detectedType, normalizedType, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLayoutInputSortGroup(string input)
    {
        string upper = (input ?? string.Empty).Trim().ToUpperInvariant();
        if (upper.StartsWith("JOY_BTN", StringComparison.Ordinal))
            return 0;
        if (upper.StartsWith("JOY_", StringComparison.Ordinal))
            return 1;
        return 2;
    }

    private static int GetLayoutInputSortNumber(string input)
    {
        string value = input ?? string.Empty;
        string digits = new(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return int.MaxValue;
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : int.MaxValue;
    }

    private void RefreshDcsKnownDevices()
    {
        HashSet<string> discovered = new(StringComparer.OrdinalIgnoreCase);
        AddDcsKnownDevicesFromSavedGamesRoot(DcsSavedGamesPath, discovered);
        AddDcsKnownDevicesFromSavedGamesRoot(ImportDcsPath, discovered);
        AddDcsKnownDevicesFromSavedGamesRoot(ExportDcsPath, discovered);

        DcsKnownDevices.Clear();
        foreach (string device in discovered.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            DcsKnownDevices.Add(device);
        RebuildImportDeviceOptions();
        RebuildLayoutDeviceOptions(BuildLayoutDeviceCandidates());
        UpdateLayoutMapInputFromRememberedMappings(BuildEffectiveLayoutDeviceFilter());
        SeedDcsFfbDeviceSettingsFromKnownDevices();
    }

    private static void AddDcsKnownDevicesFromSavedGamesRoot(string savedGamesPath, HashSet<string> output)
    {
        if (output == null || string.IsNullOrWhiteSpace(savedGamesPath))
            return;

        string inputRoot = Path.Combine(savedGamesPath.Trim(), "Config", "Input");
        if (!Directory.Exists(inputRoot))
            return;

        IEnumerable<string> aircraftDirs;
        try
        {
            aircraftDirs = Directory.EnumerateDirectories(inputRoot);
        }
        catch
        {
            return;
        }

        foreach (string aircraftDir in aircraftDirs)
        {
            IEnumerable<string> deviceTypeDirs;
            try
            {
                deviceTypeDirs = Directory.EnumerateDirectories(aircraftDir);
            }
            catch
            {
                continue;
            }

            foreach (string deviceTypeDir in deviceTypeDirs)
            {
                IEnumerable<string> diffFiles;
                try
                {
                    diffFiles = Directory.EnumerateFiles(deviceTypeDir, "*.diff.lua", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string diffFile in diffFiles)
                {
                    string fileName = Path.GetFileName(diffFile);
                    if (string.IsNullOrWhiteSpace(fileName) || fileName.Length <= ".diff.lua".Length)
                        continue;
                    string device = fileName[..^(".diff.lua".Length)].Trim();
                    if (device.Length == 0)
                        continue;
                    output.Add(device);
                }
            }
        }
    }

    private static bool TryParseRelationBindToken(string relationBind, out string device, out string input)
    {
        device = string.Empty;
        input = string.Empty;

        string value = (relationBind ?? string.Empty).Trim();
        if (value.Length == 0 ||
            value.Equals("unbound", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("<none>", StringComparison.OrdinalIgnoreCase))
            return false;

        int separator = value.LastIndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator >= value.Length - 2)
        {
            device = value;
            return true;
        }

        device = value[..separator].Trim();
        input = value[(separator + 2)..].Trim();
        return device.Length > 0 || input.Length > 0;
    }

    private List<string> GetSelectedModifierNames()
    {
        List<string> selected = ModifierItems
            .Where(v => v.IsSelected && !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => v.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0 && SelectedModifierItem != null && !string.IsNullOrWhiteSpace(SelectedModifierItem.Name))
            selected.Add(SelectedModifierItem.Name.Trim());
        return selected;
    }

    private void SyncModifierSelectionFromSelectedBind()
    {
        if (ModifierItems.Count == 0)
            return;

        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        string summary = SelectedBind?.ModifiersSummary ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(summary) &&
            !summary.Equals("<none>", StringComparison.OrdinalIgnoreCase) &&
            !summary.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string token in summary.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = token.Trim();
                if (normalized.Length > 0)
                    selected.Add(normalized);
            }
        }

        foreach (ModifierListItem item in ModifierItems)
            item.IsSelected = selected.Contains(item.Name);
    }

    private void RefreshSelectedModifierRelations()
    {
        SelectedModifierRelations.Clear();
        ModifierListItem? selected = SelectedModifierItem ?? ModifierItems.FirstOrDefault(v => v.IsSelected);
        if (selected == null)
            return;

        foreach (string relation in ParseModifierRelationNames(selected.UsedByFull))
            SelectedModifierRelations.Add(relation);
    }

    private static List<string> ParseModifierRelationNames(string raw)
    {
        return (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RebuildModifierTargetRelationOptions()
    {
        List<string> targets = BuildBindRows()
            .Where(v => v.HasBind && !string.IsNullOrWhiteSpace(v.RelationName))
            .Select(v => v.RelationName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModifierTargetRelationOptions.Clear();
        foreach (string relation in targets)
            ModifierTargetRelationOptions.Add(relation);
        OnPropertyChanged(nameof(HasModifierTargetRelations));

        if (targets.Count == 0)
        {
            ModifierCreateTargetRelation = string.Empty;
            return;
        }

        string current = (ModifierCreateTargetRelation ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(current) &&
            targets.Contains(current, StringComparer.OrdinalIgnoreCase))
            return;

        string preferred = (SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(preferred) &&
            targets.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            ModifierCreateTargetRelation = preferred;
            return;
        }

        ModifierCreateTargetRelation = string.Empty;
    }

    private static string BuildModifierReformerString(string name, string device, string input, bool inverted)
    {
        string safeName = SanitizeReformerToken(name, "Modifier");
        string safeDevice = SanitizeReformerToken(device, "Keyboard");
        string safeInput = SanitizeReformerToken(input, safeName);
        return safeName + ModifierReformerSeparator + safeDevice + ModifierReformerSeparator + safeInput + ModifierReformerSeparator + (inverted ? T("True") : T("False"));
    }

    private static string SanitizeReformerToken(string value, string fallback)
    {
        string token = (value ?? string.Empty)
            .Replace(ModifierReformerSeparator, ' ')
            .Replace(LegacyReformerSeparator, ' ')
            .Replace(DecodeReplacementSeparator, ' ')
            .Replace("Â§", " ", StringComparison.Ordinal)
            .Replace("Ã‚Â§", " ", StringComparison.Ordinal)
            .Replace("Ãµ", " ", StringComparison.Ordinal)
            .Trim();
        if (token.Length == 0)
            token = fallback;
        return token;
    }

    private bool IsSelectedTargetAxisMode()
    {
        string type = string.Empty;
        if (SelectedBind != null)
            type = SelectedBind.TypeDisplay;

        if (string.IsNullOrWhiteSpace(type))
        {
            string relationName = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? string.Empty;
            RelationListItem? relation = allRelations.FirstOrDefault(v =>
                string.Equals(v.Name, relationName, StringComparison.OrdinalIgnoreCase));
            if (relation != null)
                type = relation.Type;
        }

        type = NormalizeBindType(type);
        return type.Equals("axis", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateRecordTargetContext()
    {
        bool axisMode = IsSelectedTargetAxisMode();
        RecordModeLabel = axisMode ? "axis" : "button";

        string device = !string.IsNullOrWhiteSpace(BindDeviceInput) ? BindDeviceInput.Trim() : "<unbound>";
        string input = !string.IsNullOrWhiteSpace(BindInputValue) ? BindInputValue.Trim() : "<unbound>";
        string relation = SelectedBind?.RelationName ?? SelectedRelation?.Name ?? "<none>";

        SelectedBindRelationDisplay = relation;
        SelectedBindDeviceDisplay = device;
        SelectedBindInputDisplay = input;
        RecordTargetSummary = "Target: " + relation + " | " + device + " :: " + input;
        RecordActionLabel = "Start Record (" + device + " :: " + input + ")";
    }

    private void EnsureQuickBindOptionContains(string device, string input)
    {
        string normalizedDevice = (device ?? string.Empty).Trim();
        string normalizedInput = (input ?? string.Empty).Trim();

        if (!string.IsNullOrWhiteSpace(normalizedDevice) &&
            !QuickBindDeviceOptions.Any(v => string.Equals(v, normalizedDevice, StringComparison.OrdinalIgnoreCase)))
            QuickBindDeviceOptions.Add(normalizedDevice);
        if (!string.IsNullOrWhiteSpace(normalizedInput) &&
            !QuickBindInputOptions.Any(v => string.Equals(v, normalizedInput, StringComparison.OrdinalIgnoreCase)))
            QuickBindInputOptions.Add(normalizedInput);
    }

    private async Task<(string Name, string Reformer)?> FindDuplicateModifierOnTargetAsync(string targetRelation, string device, string input)
    {
        if (string.IsNullOrWhiteSpace(targetRelation) ||
            string.IsNullOrWhiteSpace(device) ||
            string.IsNullOrWhiteSpace(input))
            return null;

        await EnsureModifierReformerCacheAsync();

        foreach (KeyValuePair<string, List<ModifierReformerMatch>> entry in modifierReformerCacheByName)
        {
            List<ModifierReformerMatch> matches = entry.Value;
            if (matches == null || matches.Count == 0)
                continue;

            foreach (ModifierReformerMatch match in matches)
            {
                if (!string.Equals(match.Relation, targetRelation, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (NormalizeDeviceToken(match.Device) == NormalizeDeviceToken(device) &&
                    NormalizeInputToken(match.Input) == NormalizeInputToken(input))
                    return (match.Name, match.Reformer);
            }
        }

        return null;
    }

    private async Task<(string Name, string Relation, string Reformer)?> FindDuplicateModifierAnywhereAsync(string device, string input)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(input))
            return null;

        string normalizedDevice = NormalizeDeviceToken(device);
        string normalizedInput = NormalizeInputToken(input);

        foreach (PendingModifierDefinition pending in pendingModifiersByName.Values)
        {
            if (NormalizeDeviceToken(pending.Device) == normalizedDevice &&
                NormalizeInputToken(pending.Input) == normalizedInput)
            {
                string reformer = BuildModifierReformerString(pending.Name, pending.Device, pending.Input, pending.Inverted);
                return (pending.Name, "<temporary>", reformer);
            }
        }

        await EnsureModifierReformerCacheAsync(forceRefresh: true);

        foreach (KeyValuePair<string, List<ModifierReformerMatch>> entry in modifierReformerCacheByName)
        {
            List<ModifierReformerMatch>? matches = entry.Value;
            if (matches == null || matches.Count == 0)
                continue;

            foreach (ModifierReformerMatch match in matches)
            {
                if (NormalizeDeviceToken(match.Device) == normalizedDevice &&
                    NormalizeInputToken(match.Input) == normalizedInput)
                    return (match.Name, match.Relation, match.Reformer);
            }
        }

        return null;
    }

    private async Task EnsureModifierReformerCacheAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && modifierReformerCacheByName.Count > 0)
            return;

        bool ready = await WaitForEngineIdleAsync(5000);
        if (!ready)
            return;

        EngineCommandResult? result = await RunEngineAsync(
            ["binds", "list"],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Loading bind cache..."),
            forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        bool json = ParseBinds(result.StandardOutput);
        if (modifierReformerCacheByName.Count > 0)
            return;
        // A JSON list without a single modifier on any bind: the text list has none either.
        if (json && allBinds.All(b => string.IsNullOrWhiteSpace(b.ModifiersSummary) || b.ModifiersSummary == "<none>"))
            return;

        // Fallback when JSON output could not be parsed cleanly.
        ready = await WaitForEngineIdleAsync(5000);
        if (!ready)
            return;

        EngineCommandResult? textResult = await RunEngineAsync(
            ["--text", "binds", "list"],
            includeProfile: true,
            mutatesProfile: false,
            busyLabel: N("Loading bind cache..."),
            forceText: false);
        if (textResult == null || textResult.ExitCode != 0)
            return;

        ParseBinds(textResult.StandardOutput);
    }

    private async Task<bool> WaitForEngineIdleAsync(int timeoutMs)
    {
        int waited = 0;
        const int stepMs = 50;
        while (IsBusy && waited < timeoutMs)
        {
            await Task.Delay(stepMs);
            waited += stepMs;
        }

        return !IsBusy;
    }

    private readonly record struct ModifierReformerMatch(
        string Name,
        string Device,
        string Input,
        string Relation,
        string Reformer);

    private readonly record struct PendingModifierDefinition(
        string Name,
        string Device,
        string Input,
        bool Inverted);

    private static List<ModifierReformerMatch> ExtractModifierReformerMatches(
        string output,
        string fallbackRelation,
        out List<string> discoveredRelations)
    {
        discoveredRelations = [];
        List<ModifierReformerMatch> matches = [];
        string safeOutput = output ?? string.Empty;
        if (safeOutput.Length == 0)
            return matches;

        if (TryExtractModifierReformerMatchesFromJson(safeOutput, fallbackRelation, out List<ModifierReformerMatch> jsonMatches, out List<string> jsonRelations))
        {
            discoveredRelations.AddRange(jsonRelations);
            return jsonMatches;
        }

        bool inReformerSection = false;
        string currentRelation = fallbackRelation ?? string.Empty;
        foreach (string raw in safeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            Match modifierLine = ModifierListLine.Match(line);
            if (modifierLine.Success)
            {
                string rawRelations = modifierLine.Groups["relations"].Value;
                foreach (string relation in rawRelations.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string normalized = relation.Trim();
                    if (normalized.Length > 0)
                        discoveredRelations.Add(normalized);
                }
            }

            if (line.StartsWith("Relation:", StringComparison.OrdinalIgnoreCase))
            {
                currentRelation = line[T("Relation:").Length..].Trim();
                inReformerSection = false;
                continue;
            }
            if (line.StartsWith("Reformers:", StringComparison.OrdinalIgnoreCase))
            {
                inReformerSection = true;
                continue;
            }
            if (line.StartsWith("Modifiers:", StringComparison.OrdinalIgnoreCase))
            {
                inReformerSection = false;
                continue;
            }

            if (!inReformerSection || !line.StartsWith("-", StringComparison.Ordinal))
                continue;

            string reformer = line.TrimStart('-', ' ').Trim();
            if (!TryParseModifierReformer(reformer, out string name, out string device, out string input, out _))
                continue;

            matches.Add(new ModifierReformerMatch(name, device, input, currentRelation, reformer));
        }

        discoveredRelations = discoveredRelations
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches;
    }

    private static bool TryExtractModifierReformerMatchesFromJson(
        string output,
        string fallbackRelation,
        out List<ModifierReformerMatch> matches,
        out List<string> discoveredRelations)
    {
        matches = [];
        discoveredRelations = [];
        if (string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            JsonElement root = doc.RootElement;
            string relation = fallbackRelation ?? string.Empty;
            if (root.TryGetProperty("Relation", out JsonElement relationEl) &&
                relationEl.ValueKind == JsonValueKind.String)
                relation = relationEl.GetString() ?? relation;

            if (root.TryGetProperty("Entries", out JsonElement entriesEl) &&
                entriesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entriesEl.EnumerateArray())
                {
                    if (!entry.TryGetProperty("Relations", out JsonElement relationsEl) ||
                        relationsEl.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (JsonElement relEl in relationsEl.EnumerateArray())
                    {
                        if (relEl.ValueKind != JsonValueKind.String)
                            continue;
                        string? rel = relEl.GetString();
                        if (!string.IsNullOrWhiteSpace(rel))
                            discoveredRelations.Add(rel.Trim());
                    }
                }
            }

            if (root.TryGetProperty("Reformers", out JsonElement reformersEl) &&
                reformersEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement reformerEl in reformersEl.EnumerateArray())
                {
                    if (reformerEl.ValueKind != JsonValueKind.String)
                        continue;

                    string reformer = reformerEl.GetString() ?? string.Empty;
                    if (!TryParseModifierReformer(reformer, out string name, out string device, out string input, out _))
                        continue;

                    matches.Add(new ModifierReformerMatch(name, device, input, relation, reformer));
                }
            }

            discoveredRelations = discoveredRelations
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseModifierReformer(
        string reformer,
        out string name,
        out string device,
        out string input,
        out bool inverted)
    {
        name = string.Empty;
        device = string.Empty;
        input = string.Empty;
        inverted = false;

        if (string.IsNullOrWhiteSpace(reformer))
            return false;

        string normalized = NormalizeModifierReformerDelimiters(reformer);
        string[] parts = normalized.Split(ModifierReformerSeparator);
        if (parts.Length < 4)
            return false;

        name = parts[0].Trim();
        device = parts[1].Trim();
        input = parts[2].Trim();
        _ = bool.TryParse(parts[3].Trim(), out inverted);
        return name.Length > 0 && device.Length > 0 && input.Length > 0;
    }

    private static string ModifierNameFromReformer(string reformer)
    {
        if (TryParseModifierReformer(reformer, out string name, out _, out _, out _))
            return name;

        string normalized = NormalizeModifierReformerDelimiters(reformer);
        if (normalized.Length == 0)
            return string.Empty;

        int splitIndex = normalized.IndexOf(ModifierReformerSeparator);
        return splitIndex <= 0 ? normalized : normalized[..splitIndex].Trim();
    }

    private static string NormalizeModifierReformerDelimiters(string reformer)
    {
        string normalized = (reformer ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return string.Empty;

        // Accept legacy/mojibake separators that can occur when console output encoding differs.
        normalized = normalized
            .Replace("Ã‚Â§", "§", StringComparison.Ordinal)
            .Replace("Â§", "§", StringComparison.Ordinal)
            .Replace("Ãµ", "§", StringComparison.Ordinal)
            .Replace(LegacyReformerSeparator, ModifierReformerSeparator)
            .Replace(DecodeReplacementSeparator, ModifierReformerSeparator);

        return normalized;
    }

    private static string NormalizeDeviceToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
            return string.Empty;

        Match guidMatch = DeviceGuidInBraces.Match(token);
        if (guidMatch.Success)
            return "{" + guidMatch.Groups["id"].Value.ToUpperInvariant() + "}";

        token = MultiWhitespace.Replace(token, " ");
        return token.ToUpperInvariant();
    }

    private static string NormalizeInputToken(string value)
    {
        string token = (value ?? string.Empty).Trim();
        if (token.Length == 0)
            return string.Empty;

        if (token.StartsWith("JOY_", StringComparison.OrdinalIgnoreCase))
            return token.ToUpperInvariant();

        string compact = token
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

        return compact switch
        {
            "LCTRL" => "LEFTCONTROL",
            "LEFTCTRL" => "LEFTCONTROL",
            "LEFTCONTROL" => "LEFTCONTROL",
            "RCTRL" => "RIGHTCONTROL",
            "RIGHTCTRL" => "RIGHTCONTROL",
            "RIGHTCONTROL" => "RIGHTCONTROL",
            "LSHIFT" => "LEFTSHIFT",
            "LEFTSHIFT" => "LEFTSHIFT",
            "RSHIFT" => "RIGHTSHIFT",
            "RIGHTSHIFT" => "RIGHTSHIFT",
            "LALT" => "LEFTALT",
            "LEFTALT" => "LEFTALT",
            "RALT" => "RIGHTALT",
            "RIGHTALT" => "RIGHTALT",
            "RETURN" => "ENTER",
            "ESC" => "ESCAPE",
            "PGUP" => "PAGEUP",
            "PGDN" => "PAGEDOWN",
            _ => compact
        };
    }

    private static string NormalizeBindType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "button";
        string trimmed = value.Trim();
        return trimmed.Equals("axis", StringComparison.OrdinalIgnoreCase) ? "axis" : "button";
    }

    private bool TryValidateCurvatureCsv(string value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        List<string> parts = string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();
        if (parts.Count == 0)
        {
            error = T("Curvature is enabled but no value was provided.");
            return false;
        }

        List<string> normalizedValues = [];
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                error = T("Invalid curvature value '{0}'.", part);
                return false;
            }

            if (parsed < -1.0 || parsed > 1.0)
            {
                error = T("Curvature value '{0}' must be between -1 and 1.", part);
                return false;
            }

            normalizedValues.Add(parsed.ToString("0.###", CultureInfo.InvariantCulture));
        }

        normalized = string.Join(",", normalizedValues);
        return true;
    }

    private bool TryValidateAxisValue(string value, string label, out string normalized, double maxInclusive)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            StatusText = T("{0} is enabled but no value was provided.", label);
            return false;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            StatusText = T("{0} must be a numeric value.", label);
            return false;
        }

        if (parsed < 0 || parsed > maxInclusive)
        {
            StatusText = T("{0} must be between 0 and {1}.", label, maxInclusive.ToString("0.###", CultureInfo.InvariantCulture));
            return false;
        }

        normalized = parsed.ToString("0.###", CultureInfo.InvariantCulture);
        return true;
    }

    private void RebuildValidationFilter()
    {
        IEnumerable<ValidationIssueItem> filtered = allValidationIssues.Where(v =>
            (ValidationShowErrors && v.Type.Equals("Error", StringComparison.OrdinalIgnoreCase)) ||
            (ValidationShowWarnings && v.Type.Equals("Warning", StringComparison.OrdinalIgnoreCase)));

        ValidationIssues.Clear();
        foreach (ValidationIssueItem issue in filtered)
            ValidationIssues.Add(issue);
    }

    partial void OnValidationShowErrorsChanged(bool value) => RebuildValidationFilter();
    partial void OnValidationShowWarningsChanged(bool value) => RebuildValidationFilter();

    private void RebuildGroupFilters(IEnumerable<string> groups)
    {
        HashSet<string> relationSelected = RelationGroupFilters
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> removeSelected = RelationGroupRemoveCandidates
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> bindSelected = BindGroupFilters
            .Where(v => v.IsSelected)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RelationGroupFilters.Clear();
        RelationGroupRemoveCandidates.Clear();
        RelationGroupNames.Clear();
        BindGroupFilters.Clear();

        foreach (string group in groups.Where(v => !string.IsNullOrWhiteSpace(v))
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            string name = group.Trim();

            SelectableTagItem relationTag = new(name, relationSelected.Contains(name));
            relationTag.PropertyChanged += OnRelationGroupFilterChanged;
            RelationGroupFilters.Add(relationTag);

            SelectableTagItem removeTag = new(name, removeSelected.Contains(name));
            RelationGroupRemoveCandidates.Add(removeTag);
            RelationGroupNames.Add(name);

            SelectableTagItem bindTag = new(name, bindSelected.Contains(name));
            bindTag.PropertyChanged += OnBindGroupFilterChanged;
            BindGroupFilters.Add(bindTag);
        }

        if (string.IsNullOrWhiteSpace(RelationGroupRenameOld) && RelationGroupNames.Count > 0)
            RelationGroupRenameOld = RelationGroupNames[0];
        else if (!string.IsNullOrWhiteSpace(RelationGroupRenameOld) &&
                 !RelationGroupNames.Any(v => string.Equals(v, RelationGroupRenameOld, StringComparison.OrdinalIgnoreCase)))
            RelationGroupRenameOld = RelationGroupNames.FirstOrDefault() ?? string.Empty;
    }

    private void UpdateRelationMassSelectionSummary()
    {
        int count = relationMassSelectedNames.Count;
        RelationMassSelectionSummary = count == 1
            ? "1 relation selected"
            : $"{count} relations selected";
    }

    private void OnRelationGroupFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            RebuildVisibleRelations();
    }

    private void OnBindGroupFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableTagItem.IsSelected))
            RebuildVisibleBinds();
    }

    private void ApplyTheme(string mode)
    {
        ThemeMode = mode;
        if (Application.Current == null)
            return;

        ThemeVariant variant = mode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        // The application lives on its UI thread; a view model made on another thread (tests next to the render tests'
        // Avalonia thread) posts the change instead of throwing "Call from invalid thread".
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Application.Current.RequestedThemeVariant = variant;
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Application.Current != null) Application.Current.RequestedThemeVariant = variant; });
    }

    private void RememberLastProfilePath(string? path)
    {
        string normalized = (path ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return;

        if (string.Equals(LastProfilePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // The path is unchanged, but the file may only exist now (e.g. right after New).
            OnPropertyChanged(nameof(CanOpenLastProfile));
            return;
        }

        LastProfilePath = normalized;
    }

    private void LoadUiSettings()
    {
        try
        {
            if (!File.Exists(UiSettingsPath))
                return;

            string json = File.ReadAllText(UiSettingsPath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            UiSettingsState? state = JsonSerializer.Deserialize<UiSettingsState>(json, UiSettingsJsonOptions);
            if (state == null)
                return;

            loadingUiSettings = true;
            if (!string.IsNullOrWhiteSpace(state.LastProfilePath))
                LastProfilePath = state.LastProfilePath.Trim();
            LoadLastProfileOnStartup = state.AutoOpenLastProfile ?? true;
            gameSetupDone = state.GameSetupDone;
            WatchGameConfigs = state.WatchGameConfigs;
            gameWatchChoices = new JoyPro.Watch.GameWatchChoices(state.WatchedGames);
            StartWithWindows = state.StartWithWindows;
            CloseToTray = state.CloseToTray;
            ReadWarThunderArchives = state.ReadWarThunderArchives;
            FollowInputOnBinds = state.FollowInputOnBinds;
            ShowFollowInputNote = state.ShowFollowJumpNote;
            axisInputOptIns.Clear();
            foreach (string device in state.AxisInputDevices ?? [])
                if (!string.IsNullOrWhiteSpace(device))
                    axisInputOptIns.Add(device.Trim());
            RecordReleaseOfHeldButtons = state.RecordReleaseOfHeldButtons;
            HllvOodleAnswer = state.HllvOodleAnswer ?? string.Empty;
            HllvOodleFile = state.HllvOodleFile ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(state.ThemeMode))
                ThemeMode = state.ThemeMode.Trim();
            languageChoice = state.Language?.Trim() ?? string.Empty;
            if (OverlayCorners.Contains(state.OverlayCorner))
                OverlayCorner = state.OverlayCorner;
            OverlayMaxLines = Math.Clamp(state.OverlayMaxLines, 1, 20);
            OverlaySeconds = (decimal)Math.Clamp(state.OverlaySeconds, 1, 30);
            OverlayFontSize = (decimal)Math.Clamp(state.OverlayFontSize, 10, 60);
            OverlayShowDevice = state.OverlayShowDevice;
            ShowDeviceAliases = state.ShowDeviceAliases;
            OverlayShowHeader = state.OverlayShowHeader;
            OverlayClickThrough = state.OverlayClickThrough;
            OverlayOnlyInGame = state.OverlayOnlyInGame;
            CheckUpdatesOnStartup = state.CheckUpdatesOnStartup ?? true;
            UpdateRepository = state.UpdateRepository ?? string.Empty;
            SkippedUpdateVersion = state.SkippedUpdateVersion ?? string.Empty;
            if (state.LlmIdleMinutes is double idleMinutes && idleMinutes > 0)
                LlmIdleMinutes = IsOldLlmIdleDefault(state) ? DefaultLlmIdleMinutes : Math.Clamp((decimal)idleMinutes, 0.5m, 120m);
            ApplySettingsLayout(state.SettingsSectionOrder, state.SettingsSectionsOpen);
            ApplyNavPageLayout(state.NavPageOrder, state.HiddenNavPages);
            if (!string.IsNullOrWhiteSpace(state.OverlayPlaneChoice))
            {
                if (!OverlayPlaneOptions.Contains(state.OverlayPlaneChoice))
                    OverlayPlaneOptions.Add(state.OverlayPlaneChoice);
                OverlayPlaneChoice = state.OverlayPlaneChoice;
            }
        }
        catch
        {
            // Ignore settings load errors.
        }
        finally
        {
            loadingUiSettings = false;
        }
    }

    private void SaveUiSettings()
    {
        if (loadingUiSettings || !persistUiFiles)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(UiSettingsPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            UiSettingsState state = new()
            {
                LastProfilePath = LastProfilePath,
                AutoOpenLastProfile = LoadLastProfileOnStartup,
                GameSetupDone = gameSetupDone,
                WatchGameConfigs = WatchGameConfigs,
                WatchedGames = new Dictionary<string, bool>(gameWatchChoices.Choices),
                StartWithWindows = StartWithWindows,
                CloseToTray = CloseToTray,
                ReadWarThunderArchives = ReadWarThunderArchives,
                FollowInputOnBinds = FollowInputOnBinds,
                ShowFollowJumpNote = ShowFollowInputNote,
                AxisInputDevices = axisInputOptIns.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(),
                RecordReleaseOfHeldButtons = RecordReleaseOfHeldButtons,
                HllvOodleAnswer = HllvOodleAnswer,
                HllvOodleFile = HllvOodleFile,
                ThemeMode = ThemeMode,
                Language = languageChoice,
                OverlayCorner = OverlayCorner,
                OverlayMaxLines = (int)OverlayMaxLines,
                OverlaySeconds = (double)OverlaySeconds,
                OverlayFontSize = (double)OverlayFontSize,
                OverlayShowDevice = OverlayShowDevice,
                ShowDeviceAliases = ShowDeviceAliases,
                OverlayShowHeader = OverlayShowHeader,
                OverlayClickThrough = OverlayClickThrough,
                OverlayOnlyInGame = OverlayOnlyInGame,
                OverlayPlaneChoice = OverlayPlaneChoice,
                CheckUpdatesOnStartup = CheckUpdatesOnStartup,
                UpdateRepository = UpdateRepository,
                SkippedUpdateVersion = SkippedUpdateVersion,
                LlmIdleMinutes = (double)LlmIdleMinutes,
                LlmIdleDefault = (double)DefaultLlmIdleMinutes,
                SettingsSectionOrder = SettingsSectionOrderForSave(),
                SettingsSectionsOpen = OpenSettingsSectionsForSave(),
                NavPageOrder = NavPageOrderForSave(),
                HiddenNavPages = HiddenNavPagesForSave()
            };

            string json = JsonSerializer.Serialize(state, UiSettingsJsonOptions);
            File.WriteAllText(UiSettingsPath, json);
        }
        catch
        {
            // Ignore settings save errors.
        }
    }

    private string ResolveEnginePath()
    {
        if (!string.IsNullOrWhiteSpace(EnginePath))
        {
            string expanded = EnginePath.Trim();
            if (File.Exists(expanded))
                return expanded;
            if (Directory.Exists(expanded))
            {
                string candidate = Path.Combine(expanded, "engine.exe");
                if (File.Exists(candidate))
                {
                    EnginePath = candidate;
                    return candidate;
                }

                candidate = Path.Combine(expanded, "engine", "engine.exe");
                if (File.Exists(candidate))
                {
                    EnginePath = candidate;
                    return candidate;
                }
            }
        }

        string detected = TryResolveEnginePath();
        if (!string.IsNullOrWhiteSpace(detected))
            EnginePath = detected;
        return EnginePath;
    }

    private static string TryResolveEnginePath()
    {
        List<string> candidates = [];
        string cwd = Environment.CurrentDirectory;
        string baseDir = AppContext.BaseDirectory;

        candidates.Add(Path.Combine(baseDir, "engine", "engine.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "engine", "engine.exe"));
        candidates.Add(Path.Combine(cwd, "JoyPro2", "engine", "bin", "x64", "Release", "engine.exe"));
        candidates.Add(Path.Combine(cwd, "JoyPro2", "engine", "bin", "x64", "Debug", "engine.exe"));
        candidates.Add(Path.Combine(cwd, "..", "..", "engine", "bin", "x64", "Release", "engine.exe"));
        candidates.Add(Path.Combine(cwd, "..", "..", "engine", "bin", "x64", "Debug", "engine.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "..", "engine", "bin", "x64", "Release", "engine.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "..", "engine", "bin", "x64", "Debug", "engine.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "engine", "bin", "x64", "Release", "engine.exe"));
        candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "engine", "bin", "x64", "Debug", "engine.exe"));

        foreach (string candidate in candidates.Select(Path.GetFullPath))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return string.Empty;
    }

    private static string GuessSavedGamesDcsPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string stable = Path.Combine(home, "Saved Games", "DCS");
        string openBeta = Path.Combine(home, "Saved Games", "DCS.openbeta");
        if (Directory.Exists(stable))
            return stable;
        if (Directory.Exists(openBeta))
            return openBeta;
        return stable;
    }

    private static string GuessDcsInstallPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(programFiles, "Eagle Dynamics", "DCS World"),
            Path.Combine(programFiles, "Eagle Dynamics", "DCS World OpenBeta"),
            @"G:\Program Files\Eagle Dynamics\DCS World",
            @"G:\Program Files\Eagle Dynamics\DCS World OpenBeta"
        ];
        return candidates.FirstOrDefault(Directory.Exists) ?? string.Empty;
    }

    private static string GuessStarCitizenProfilePath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates =
        [
            Path.Combine(programFiles, "Roberts Space Industries", "StarCitizen", "LIVE", "USER", "Client", "0", "Profiles"),
            Path.Combine(programFiles, "Roberts Space Industries", "StarCitizen", "PTU", "USER", "Client", "0", "Profiles"),
            Path.Combine(programFilesX86, "Roberts Space Industries", "StarCitizen", "LIVE", "USER", "Client", "0", "Profiles"),
            Path.Combine(programFilesX86, "Roberts Space Industries", "StarCitizen", "PTU", "USER", "Client", "0", "Profiles"),
            Path.Combine(programFiles, "Roberts Space Industries", "StarCitizen"),
            Path.Combine(programFilesX86, "Roberts Space Industries", "StarCitizen")
        ];
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    private static void EnsureProfileDirectoryExists(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(profile));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    // Documents\JoyPro2\profiles (JOYPRO_PROFILES_DIR replaces it: tests).
    internal static string ProfilesFolder()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("JOYPRO_PROFILES_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JoyPro2", "profiles")
            : overrideDir;
    }

    private static string CreateFreshAutoProfilePath()
    {
        string root = ProfilesFolder();
        Directory.CreateDirectory(root);
        // A name a user reads ("My profile", "My profile 2", ...); Rename... gives it the user's own.
        string candidate = Path.Combine(root, "My profile.pr0file");
        int suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(root, "My profile " + suffix.ToString(CultureInfo.InvariantCulture) + ".pr0file");
            suffix++;
        }

        return candidate;
    }

    private sealed class UiSettingsState
    {
        public string LastProfilePath { get; set; } = string.Empty;

        // "Open the last profile at start" (docs/profile-history.md): on unless switched off. A new key, so the old
        // loadLastProfileOnStartup (off by default before autosave) does not keep it off.
        public bool? AutoOpenLastProfile { get; set; }

        // The first-start game setup was shown (Settings > Games > Run setup again shows it again).
        public bool GameSetupDone { get; set; }
        public bool WatchGameConfigs { get; set; } = true;

        // The Start page's "Watch for changes" ticks, only the explicit ones (game id -> watched; JoyPro.Watch
        // GameWatchChoices). The tray host reads them too.
        public Dictionary<string, bool>? WatchedGames { get; set; }

        // Tray mode: the Run entry (HKCU) for the tray host is written from this setting; closing the window hands the
        // watcher over to the tray host. The tray host reads both (and writes only watchGameConfigs).
        public bool StartWithWindows { get; set; }

        public bool CloseToTray { get; set; }

        // War Thunder's command list is read from the game's packed archives (on by default since the user approved it
        // on 2026-10-12; a new key, so the old opt-in "AllowWarThunderArchives" = false of earlier builds is not taken
        // as the user turning it off).
        public bool ReadWarThunderArchives { get; set; } = true;

        // Binds page: pressing a button selects its relation (on by default).
        public bool FollowInputOnBinds { get; set; } = true;

        // The note over the list after a follow-input jump: off by default since 2.2.2 (users took the note that pops up
        // at every press for a tooltip). A new key, so the "showFollowInputNote" = true every earlier build saved does
        // not turn it on again.
        public bool ShowFollowJumpNote { get; set; }

        // Analog keyboards whose axes count for follow input and recording (Settings > Devices; names without GUID).
        public List<string>? AxisInputDevices { get; set; }

        // Record: releasing a button that rests pressed counts (off by default).
        public bool RecordReleaseOfHeldButtons { get; set; }

        // Hell Let Loose: Vietnam may use another game's Oodle library: "yes", "no" or "" (not asked), and that file.
        public string HllvOodleAnswer { get; set; } = string.Empty;

        public string HllvOodleFile { get; set; } = string.Empty;

        public string ThemeMode { get; set; } = "System";

        // Settings > Language: a language code (Localization/Languages.cs), empty for the Windows display language.
        public string? Language { get; set; }

        public string OverlayCorner { get; set; } = "Top left";

        public int OverlayMaxLines { get; set; } = 6;

        public double OverlaySeconds { get; set; } = 4;

        public double OverlayFontSize { get; set; } = 20;

        public bool OverlayShowDevice { get; set; }

        // Settings > Devices > Device names: the alias (default) or the device's real name.
        public bool ShowDeviceAliases { get; set; } = true;

        public bool OverlayShowHeader { get; set; } = true;

        public bool OverlayClickThrough { get; set; } = true;

        public bool OverlayOnlyInGame { get; set; } = true;

        public string OverlayPlaneChoice { get; set; } = MainWindowViewModel.OverlayAutomaticPlane;

        // Settings > About JoyPro and updates (docs/updates.md): check at start (on unless switched off), the
        // repository the updates come from ("owner/repo", empty = JoyPro's own) and the version the user skipped.
        public bool? CheckUpdatesOnStartup { get; set; }

        public string? UpdateRepository { get; set; }

        public string? SkippedUpdateVersion { get; set; }

        // The local AI model stops after this many minutes without AI work (null: 30).
        public double? LlmIdleMinutes { get; set; }

        // The default when the file was written (null: written by JoyPro 2.2.17 or older, when the default was 2 minutes and every
        // save wrote it): a 2 there was JoyPro's default, not the user's choice, and becomes the new default.
        public double? LlmIdleDefault { get; set; }

        // The Settings page: the sections in the user's order and the open ones (null: JoyPro's order, Games open).
        public List<string>? SettingsSectionOrder { get; set; }

        public List<string>? SettingsSectionsOpen { get; set; }

        // The bottom bar's pages in the user's order and the ones he hid (Settings > Pages, MainWindowViewModel.NavPages.cs).
        public List<string>? NavPageOrder { get; set; }

        public List<string>? HiddenNavPages { get; set; }
    }

    private sealed class LayoutTemplateInspectDocument
    {
        public string SourcePath { get; set; } = string.Empty;

        public string PreviewImagePath { get; set; } = string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }

        public List<LayoutEditorMarkerItem> Markers { get; set; } = [];

        public List<LayoutEditorTableItem> Tables { get; set; } = [];

        public List<LayoutEditorConnectorItem> Connectors { get; set; } = [];

        // The device the layout was made for and JoyPro 1's text look (empty for layouts without one).
        public string Joystick { get; set; } = string.Empty;

        public string FontName { get; set; } = string.Empty;

        public int FontSize { get; set; }

        public string TextColor { get; set; } = string.Empty;

        // Who made the sheet (a 2D layout): shown under the picture.
        public string Credit { get; set; } = string.Empty;
    }

    private sealed class LayoutTemplateSaveDocument
    {
        public string SourcePath { get; set; } = string.Empty;

        public string OutputPath { get; set; } = string.Empty;

        public int MarkerCount { get; set; }

        public int TableCount { get; set; }

        public int ConnectorCount { get; set; }

        public bool ReplacedAll { get; set; }
    }

    private sealed class LayoutTemplateMarkerSaveEnvelope
    {
        public List<LayoutTemplateMarkerSaveDto> Markers { get; set; } = [];

        public List<LayoutTemplateTableSaveDto> Tables { get; set; } = [];

        public List<LayoutTemplateConnectorSaveDto> Connectors { get; set; } = [];
    }

    private sealed class LayoutTemplateMarkerSaveDto
    {
        public string Token { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Modifier { get; set; } = string.Empty;

        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class LayoutTemplateTableSaveDto
    {
        public string Name { get; set; } = string.Empty;

        public string Mode { get; set; } = "panel";

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public int Columns { get; set; }

        public string BackgroundColor { get; set; } = "#F6F8FB";

        public double BackgroundOpacity { get; set; } = 0.93;

        public bool DropShadow { get; set; } = true;
    }

    private sealed class LayoutTemplateConnectorSaveDto
    {
        public string Name { get; set; } = string.Empty;

        public string Color { get; set; } = "#E2E8F0";

        public double Width { get; set; } = 2;

        public string Route { get; set; } = "orthogonal";

        public int Steps { get; set; } = 2;

        public string TargetToken { get; set; } = string.Empty;

        public List<LayoutTemplateConnectorPointSaveDto> Points { get; set; } = [];
    }

    private sealed class LayoutTemplateConnectorPointSaveDto
    {
        public double X { get; set; }

        public double Y { get; set; }
    }

    private sealed class DbNodeCatalogEntry
    {
        public DbNodeCatalogEntry(string game, string plane, string nodeId, string title, string description, string category = "")
        {
            Category = (category ?? string.Empty).Trim();
            Game = NormalizeRelationGameToken(game);
            Plane = string.IsNullOrWhiteSpace(plane) || (plane.Trim().Equals("Default", StringComparison.OrdinalIgnoreCase) && Game != "DCS")
                ? DefaultPlaneFor(Game)
                : plane.Trim();
            NodeId = (nodeId ?? string.Empty).Trim();
            Title = string.IsNullOrWhiteSpace(title) ? NodeId : title.Trim();
            Description = (description ?? string.Empty).Trim();
        }

        // Games without aircraft store node state under the game name, like the engine's importers.
        public static string DefaultPlaneFor(string game)
        {
            string token = NormalizeRelationGameToken(game);
            return token.Length == 0 || token.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? T("Default") : token;
        }

        public string Game { get; }

        public string Plane { get; }

        public string NodeId { get; }

        public string Title { get; }

        public string Description { get; }

        // The DB's category (DCS: the category column of the plane's HTML; Star Citizen: the action map).
        public string Category { get; }

        // DCS: the localization keys of the command ("Input.SpitfireLFMkIX.flaps_1"), searchable but never shown.
        public string Keys { get; init; } = string.Empty;
    }

    private sealed class DbNodeCatalogEntryComparer : IEqualityComparer<DbNodeCatalogEntry>
    {
        public static readonly DbNodeCatalogEntryComparer Instance = new();

        public bool Equals(DbNodeCatalogEntry? x, DbNodeCatalogEntry? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x == null || y == null)
                return false;

            return string.Equals(x.Game, y.Game, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Plane, y.Plane, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.NodeId, y.NodeId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Description, y.Description, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(DbNodeCatalogEntry obj)
        {
            if (obj == null)
                return 0;

            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Game ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Plane ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.NodeId ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Title ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Description ?? string.Empty));
        }
    }
}
