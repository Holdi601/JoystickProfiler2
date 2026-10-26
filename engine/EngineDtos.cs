using System;
using System.Collections.Generic;

namespace JoyPro.Engine
{
    public sealed class EngineRelationsDocument
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public List<EngineRelationDto> Relations { get; set; }

        public EngineRelationsDocument()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            Relations = new List<EngineRelationDto>();
        }
    }

    public sealed class EngineBindsDocument
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public List<EngineBindDto> Binds { get; set; }

        public EngineBindsDocument()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            Binds = new List<EngineBindDto>();
        }
    }

    public sealed class EngineRelationDto
    {
        public string Name { get; set; }
        public bool IsAxis { get; set; }
        public List<string> Groups { get; set; }
        // The function the user picked (catalog slot id or "none"); null = automatic (EngineRelationPurpose).
        public string Function { get; set; }
        public List<EngineRelationItemDto> Nodes { get; set; }
        public EngineBindDto Bind { get; set; }
        // "relations list --titles" only: the distinct titles of the relation's actions (every switched-on aircraft).
        public List<string> Titles { get; set; }

        public EngineRelationDto()
        {
            Groups = new List<string>();
            Nodes = new List<EngineRelationItemDto>();
        }
    }

    public sealed class EngineRelationItemDto
    {
        public string Id { get; set; }
        public string Game { get; set; }
        public Dictionary<string, bool> Aircraft { get; set; }
        // Per active aircraft the title and category the profile stored for this node (a localization key already
        // turned into readable text): the GUI's fallback when the aircraft's current list no longer has the command.
        public Dictionary<string, string> StoredTitles { get; set; }
        public Dictionary<string, string> StoredCategories { get; set; }

        public EngineRelationItemDto()
        {
            Aircraft = new Dictionary<string, bool>();
            StoredTitles = new Dictionary<string, string>();
            StoredCategories = new Dictionary<string, string>();
        }
    }

    public sealed class EngineBindDto
    {
        public string RelationName { get; set; }
        public bool IsAxis { get; set; }
        public string Joystick { get; set; }
        public string JAxis { get; set; }
        public string JButton { get; set; }
        public bool? Inverted { get; set; }
        public bool? Slider { get; set; }
        public double Deadzone { get; set; }
        public List<double> Curvature { get; set; }
        public double SaturationX { get; set; }
        public double SaturationY { get; set; }
        public List<string> AllReformers { get; set; }
        public string AdditionalImportInfo { get; set; }
        public string AliasJoystick { get; set; }
        public bool DoubleTap { get; set; }
        public Dictionary<string, string> TuningOverrides { get; set; }

        public EngineBindDto()
        {
            Curvature = new List<double>();
            AllReformers = new List<string>();
            AdditionalImportInfo = string.Empty;
            AliasJoystick = string.Empty;
        }
    }

    public sealed class EngineBindWithRelationDto
    {
        public EngineBindDto Bind { get; set; }
        public EngineRelationDto Relation { get; set; }
    }

    public sealed class EngineDbDocument
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        // <data>\GameData: where the lists built from the user's games live (GameDataPaths).
        public string GameDataRoot { get; set; }
        public string DbPath { get; set; }
        public string CleanProfilePath { get; set; }
        public string KeyboardCleanProfilePath { get; set; }
        public bool KeyboardCheckEnabled { get; set; }
        public List<EngineDbModuleDto> Modules { get; set; }
        public List<string> Errors { get; set; }

        public EngineDbDocument()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            GameDataRoot = string.Empty;
            DbPath = string.Empty;
            CleanProfilePath = string.Empty;
            KeyboardCleanProfilePath = string.Empty;
            KeyboardCheckEnabled = false;
            Modules = new List<EngineDbModuleDto>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineDbModuleDto
    {
        public string Name { get; set; }
        public string DbFile { get; set; }
        public string CleanFile { get; set; }
        public string KeyboardCleanFile { get; set; }
        public bool HasCleanFile { get; set; }
        public bool HasKeyboardCleanFile { get; set; }
        public bool CleanMatchedByAlias { get; set; }
        public bool KeyboardCleanMatchedByAlias { get; set; }
        public bool ExcludedFromCleanCheck { get; set; }
        public List<string> Missing { get; set; }

        public EngineDbModuleDto()
        {
            Name = string.Empty;
            DbFile = string.Empty;
            CleanFile = string.Empty;
            KeyboardCleanFile = string.Empty;
            HasCleanFile = false;
            HasKeyboardCleanFile = false;
            CleanMatchedByAlias = false;
            KeyboardCleanMatchedByAlias = false;
            ExcludedFromCleanCheck = false;
            Missing = new List<string>();
        }
    }

    public sealed class EngineDbCatalogDocument
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        public string DbRootPath { get; set; }
        // <data>\GameData\DB: the game lists built on this PC; a game's DbPath is there unless only the program folder
        // has its list (GameDataPaths).
        public string GameDataDbPath { get; set; }
        public List<EngineDbGameCatalogDto> Games { get; set; }
        public List<string> Errors { get; set; }

        public EngineDbCatalogDocument()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            DbRootPath = string.Empty;
            GameDataDbPath = string.Empty;
            Games = new List<EngineDbGameCatalogDto>();
            Errors = new List<string>();
        }
    }

    public sealed class EngineDbGameCatalogDto
    {
        public string Game { get; set; }
        public string SourceFolder { get; set; }
        public string DbPath { get; set; }
        public List<string> Planes { get; set; }

        public EngineDbGameCatalogDto()
        {
            Game = string.Empty;
            SourceFolder = string.Empty;
            DbPath = string.Empty;
            Planes = new List<string>();
        }
    }

    public sealed class EngineDcsImportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string SavedGamesPath { get; set; }
        public string InputPath { get; set; }
        public bool Merge { get; set; }
        public List<string> SelectedPlanes { get; set; }
        public List<string> SelectedDevices { get; set; }
        public List<string> IgnoredDevices { get; set; }
        public List<string> MatchedPlanes { get; set; }
        public List<string> MatchedDevices { get; set; }
        public bool IncludeDefaults { get; set; }
        public bool IncludeKeyboard { get; set; }
        public bool IgnoreEasyPlanes { get; set; }
        public bool ImportInverted { get; set; }
        public bool ImportSlider { get; set; }
        public bool ImportCurvature { get; set; }
        public bool ImportDeadzone { get; set; }
        public bool ImportSaturationX { get; set; }
        public bool ImportSaturationY { get; set; }
        public int FilesScanned { get; set; }
        public int BackupFilesScanned { get; set; }
        public int EntriesParsed { get; set; }
        public int ForceFeedbackEntriesParsed { get; set; }
        public int DefaultEntriesParsed { get; set; }
        // Include defaults: defaults not added because the user's file removes them, he bound the action on that device
        // himself, or he uses the input (key and modifiers) for another action of that aircraft.
        public int DefaultEntriesRemovedByUser { get; set; }
        public int DefaultEntriesReplacedByOwnBinds { get; set; }
        public int DefaultEntriesOnOwnInputs { get; set; }
        // Defaults of an action another device already has a default for in that aircraft (one device per action).
        public int DefaultEntriesOnOtherDevice { get; set; }
        // Defaults on an input DCS gives to two actions of an aircraft: the first one keeps it.
        public int DefaultEntriesOnSharedInputs { get; set; }
        // DCS's generic joystick defaults (default.lua) of devices without defaults of their own: not imported.
        public int GenericJoystickDefaultsSkipped { get; set; }
        public int ImportedRelations { get; set; }
        public int ImportedBinds { get; set; }
        public int ForceFeedbackDevicesImported { get; set; }
        public int SkippedEntries { get; set; }
        // .jp binds dropped because the .diff.lua no longer has them (removed in DCS).
        public int StaleBackupEntriesSkipped { get; set; }
        // One device under several instance GUIDs (Windows gave it a new ID over time): read as one device
        // (MergeSameNameDevices). DCS loads only one file per aircraft; the binds of the other files of that aircraft
        // are older copies and skipped.
        public bool OnlyConnected { get; set; }
        public bool MergeSameNameDevices { get; set; }
        public int OlderDeviceCopyFilesSkipped { get; set; }
        public int OlderDeviceCopyEntriesSkipped { get; set; }
        // "old {GUID} -> live {GUID}" per device read as one.
        public List<string> MergedDevices { get; set; }
        // Same-name devices connected at the same time (real twins): kept apart.
        public List<string> TwinDevices { get; set; }
        // --only-connected: files of devices that are not connected.
        public int NotConnectedFilesSkipped { get; set; }
        public int NotConnectedEntriesSkipped { get; set; }
        public List<string> NotConnectedDevices { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineDcsImportResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            SavedGamesPath = string.Empty;
            InputPath = string.Empty;
            Merge = false;
            SelectedPlanes = new List<string>();
            SelectedDevices = new List<string>();
            IgnoredDevices = new List<string>();
            MatchedPlanes = new List<string>();
            MatchedDevices = new List<string>();
            IncludeDefaults = false;
            IncludeKeyboard = true;
            IgnoreEasyPlanes = false;
            ImportInverted = false;
            ImportSlider = false;
            ImportCurvature = false;
            ImportDeadzone = false;
            ImportSaturationX = false;
            ImportSaturationY = false;
            FilesScanned = 0;
            BackupFilesScanned = 0;
            EntriesParsed = 0;
            ForceFeedbackEntriesParsed = 0;
            DefaultEntriesParsed = 0;
            ImportedRelations = 0;
            ImportedBinds = 0;
            ForceFeedbackDevicesImported = 0;
            SkippedEntries = 0;
            StaleBackupEntriesSkipped = 0;
            OnlyConnected = false;
            MergeSameNameDevices = true;
            MergedDevices = new List<string>();
            TwinDevices = new List<string>();
            NotConnectedDevices = new List<string>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineDcsImportOptions
    {
        public List<string> PlaneFilter { get; set; }
        public List<string> DeviceFilter { get; set; }
        public List<string> IgnoreDeviceFilter { get; set; }
        public bool IncludeDefaults { get; set; }
        public bool IncludeKeyboard { get; set; }
        public bool IgnoreEasyPlanes { get; set; }
        public bool ImportInverted { get; set; }
        public bool ImportSlider { get; set; }
        public bool ImportCurvature { get; set; }
        public bool ImportDeadzone { get; set; }
        public bool ImportSaturationX { get; set; }
        public bool ImportSaturationY { get; set; }
        // Skip the files of devices that are not connected (counted in the result).
        public bool OnlyConnected { get; set; }
        // One device under several instance GUIDs is read as one: the connected GUID, else the GUID of the newest file
        // (unless two or more of them are connected at once: real twins stay apart).
        public bool MergeSameNameDevices { get; set; }
        public Action<string> StatusSink { get; set; }

        public EngineDcsImportOptions()
        {
            PlaneFilter = new List<string>();
            DeviceFilter = new List<string>();
            IgnoreDeviceFilter = new List<string>();
            IncludeDefaults = false;
            IncludeKeyboard = true;
            IgnoreEasyPlanes = false;
            ImportInverted = false;
            ImportSlider = false;
            ImportCurvature = false;
            ImportDeadzone = false;
            ImportSaturationX = false;
            ImportSaturationY = false;
            OnlyConnected = false;
            MergeSameNameDevices = true;
            StatusSink = null;
        }
    }

    public sealed class EngineIl2ImportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        public string InputPath { get; set; }
        public bool Merge { get; set; }
        public List<string> SelectedDevices { get; set; }
        public List<string> IgnoredDevices { get; set; }
        public bool ImportInverted { get; set; }
        public bool ImportCurvature { get; set; }
        public bool ImportDeadzone { get; set; }
        public string GlobalActionsFile { get; set; }
        public string JpActionsFile { get; set; }
        public string DevicesFile { get; set; }
        public string ResponsesFile { get; set; }
        public int ActionLinesParsed { get; set; }
        public int KeyboardEntriesParsed { get; set; }
        public int JoystickEntriesParsed { get; set; }
        public int CandidatesParsed { get; set; }
        public int CandidatesFilteredByDevice { get; set; }
        public int ImportedRelations { get; set; }
        public int ImportedBinds { get; set; }
        public int MergedIntoExisting { get; set; }
        public int SkippedEntries { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineIl2ImportResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            InputPath = string.Empty;
            Merge = false;
            SelectedDevices = new List<string>();
            IgnoredDevices = new List<string>();
            ImportInverted = false;
            ImportCurvature = false;
            ImportDeadzone = false;
            GlobalActionsFile = string.Empty;
            JpActionsFile = string.Empty;
            DevicesFile = string.Empty;
            ResponsesFile = string.Empty;
            ActionLinesParsed = 0;
            KeyboardEntriesParsed = 0;
            JoystickEntriesParsed = 0;
            CandidatesParsed = 0;
            CandidatesFilteredByDevice = 0;
            ImportedRelations = 0;
            ImportedBinds = 0;
            MergedIntoExisting = 0;
            SkippedEntries = 0;
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineIl2ImportOptions
    {
        public List<string> DeviceFilter { get; set; }
        public List<string> IgnoreDeviceFilter { get; set; }
        public bool ImportInverted { get; set; }
        public bool ImportCurvature { get; set; }
        public bool ImportDeadzone { get; set; }
        public Action<string> StatusSink { get; set; }

        public EngineIl2ImportOptions()
        {
            DeviceFilter = new List<string>();
            IgnoreDeviceFilter = new List<string>();
            ImportInverted = false;
            ImportCurvature = false;
            ImportDeadzone = false;
            StatusSink = null;
        }
    }

    public sealed class EngineIl2ExportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        public string InputPath { get; set; }
        public string Mode { get; set; }
        public string GlobalActionsFile { get; set; }
        public string JpActionsFile { get; set; }
        public string DevicesFile { get; set; }
        public string ResponsesFile { get; set; }
        public int RelationsScanned { get; set; }
        public int NodesScanned { get; set; }
        public int ActionLinesGenerated { get; set; }
        public int ActionLinesWritten { get; set; }
        public int DeviceCount { get; set; }
        public int AxisResponsesWritten { get; set; }
        public int FilesWritten { get; set; }
        public int SkippedEntries { get; set; }
        public List<string> WrittenFiles { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineIl2ExportResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            InputPath = string.Empty;
            Mode = string.Empty;
            GlobalActionsFile = string.Empty;
            JpActionsFile = string.Empty;
            DevicesFile = string.Empty;
            ResponsesFile = string.Empty;
            RelationsScanned = 0;
            NodesScanned = 0;
            ActionLinesGenerated = 0;
            ActionLinesWritten = 0;
            DeviceCount = 0;
            AxisResponsesWritten = 0;
            FilesWritten = 0;
            SkippedEntries = 0;
            WrittenFiles = new List<string>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineStarCitizenImportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        public string ProfileDirectory { get; set; }
        public string ActionmapsFile { get; set; }
        public string MetadataFile { get; set; }
        public bool Merge { get; set; }
        public bool IncludeKeyboard { get; set; }
        public List<string> SelectedDevices { get; set; }
        public List<string> IgnoredDevices { get; set; }
        public List<string> MatchedDevices { get; set; }
        public int ActionsParsed { get; set; }
        public int RebindEntriesParsed { get; set; }
        public int CandidatesParsed { get; set; }
        public int CandidatesFilteredByDevice { get; set; }
        public int ImportedRelations { get; set; }
        public int ImportedBinds { get; set; }
        public int MergedIntoExisting { get; set; }
        public int SkippedEntries { get; set; }
        public int AxisSettingsImported { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineStarCitizenImportResult()
        {
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            ProfileDirectory = string.Empty;
            ActionmapsFile = string.Empty;
            MetadataFile = string.Empty;
            Merge = false;
            IncludeKeyboard = true;
            SelectedDevices = new List<string>();
            IgnoredDevices = new List<string>();
            MatchedDevices = new List<string>();
            ActionsParsed = 0;
            RebindEntriesParsed = 0;
            CandidatesParsed = 0;
            CandidatesFilteredByDevice = 0;
            ImportedRelations = 0;
            ImportedBinds = 0;
            MergedIntoExisting = 0;
            SkippedEntries = 0;
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineStarCitizenImportOptions
    {
        public List<string> DeviceFilter { get; set; }
        public List<string> IgnoreDeviceFilter { get; set; }
        public bool IncludeKeyboard { get; set; }
        public string SourcePath { get; set; }
        public Action<string> StatusSink { get; set; }

        public EngineStarCitizenImportOptions()
        {
            DeviceFilter = new List<string>();
            IgnoreDeviceFilter = new List<string>();
            IncludeKeyboard = true;
            SourcePath = string.Empty;
            StatusSink = null;
        }
    }

    public sealed class EngineStarCitizenExportResult
    {
        public string FormatVersion { get; set; }
        public string GeneratedUtc { get; set; }
        public string RootPath { get; set; }
        public string ProfileDirectory { get; set; }
        public string ActionmapsFile { get; set; }
        public string MetadataFile { get; set; }
        public string Mode { get; set; }
        public bool KeepKeyboardDefaults { get; set; }
        public bool PreferLocalOrder { get; set; }
        public int RelationsScanned { get; set; }
        public int NodesScanned { get; set; }
        public int ExistingEntriesRead { get; set; }
        public int EntriesWritten { get; set; }
        public int ActionsWritten { get; set; }
        public int CategoriesWritten { get; set; }
        public int JoystickSlotsUsed { get; set; }
        public int FilesWritten { get; set; }
        public int MergeConflictsKept { get; set; }
        public int MergeConflictsOverwritten { get; set; }
        public int DefaultJoystickBindsCleared { get; set; }
        public int MergeConflictsAdded { get; set; }
        public int SkippedEntries { get; set; }
        public List<string> JoystickOptions { get; set; }
        public int OptionGroupsWritten { get; set; }
        public int DeviceOptionsWritten { get; set; }
        public List<string> AxisSettings { get; set; }
        public List<string> GameSettings { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }

        public EngineStarCitizenExportResult()
        {
            AxisSettings = new List<string>();
            GameSettings = new List<string>();
            FormatVersion = "1";
            GeneratedUtc = DateTime.UtcNow.ToString("o");
            RootPath = string.Empty;
            ProfileDirectory = string.Empty;
            ActionmapsFile = string.Empty;
            MetadataFile = string.Empty;
            Mode = string.Empty;
            KeepKeyboardDefaults = false;
            PreferLocalOrder = true;
            RelationsScanned = 0;
            NodesScanned = 0;
            ExistingEntriesRead = 0;
            EntriesWritten = 0;
            ActionsWritten = 0;
            CategoriesWritten = 0;
            JoystickSlotsUsed = 0;
            FilesWritten = 0;
            MergeConflictsKept = 0;
            MergeConflictsOverwritten = 0;
            MergeConflictsAdded = 0;
            SkippedEntries = 0;
            JoystickOptions = new List<string>();
            Errors = new List<string>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineStarCitizenExportOptions
    {
        public string SourcePath { get; set; }
        public bool KeepKeyboardDefaults { get; set; }
        public bool PreferLocalOrder { get; set; }
        // Keyboard binds are written only when asked for; nuke releases ship binds of devices the export does not use.
        public bool IncludeKeyboard { get; set; }
        public bool NukeUnusedDevices { get; set; }
        public Action<string> StatusSink { get; set; }

        public EngineStarCitizenExportOptions()
        {
            SourcePath = string.Empty;
            KeepKeyboardDefaults = false;
            PreferLocalOrder = true;
            StatusSink = null;
        }
    }
}
