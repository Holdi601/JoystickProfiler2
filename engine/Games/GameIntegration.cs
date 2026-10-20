using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // One bindable action of a game, as stored in DB\<Folder>\actions.tsv.
    internal sealed class GameAction
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public bool IsAxis { get; set; }
        public string Category { get; set; }
        // Optional fifth column: the game's own id of the action where the file stores numbers instead of names
        // (Nuclear Option: the Rewired action id; pole rows carry the id of their axis action).
        public string NativeId { get; set; }

        public GameAction()
        {
            Id = string.Empty;
            Title = string.Empty;
            Category = string.Empty;
            NativeId = string.Empty;
        }
    }

    internal sealed class GameCatalog
    {
        private readonly Dictionary<string, GameAction> byId = new Dictionary<string, GameAction>(StringComparer.OrdinalIgnoreCase);

        public string GameId { get; set; }
        public string SourcePath { get; set; }
        public List<GameAction> Actions { get; private set; }

        public GameCatalog()
        {
            GameId = string.Empty;
            SourcePath = string.Empty;
            Actions = new List<GameAction>();
        }

        public void Add(GameAction action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Id) || byId.ContainsKey(action.Id))
                return;
            Actions.Add(action);
            byId[action.Id] = action;
        }

        public GameAction Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            GameAction action;
            return byId.TryGetValue(id.Trim(), out action) ? action : null;
        }

        public bool Contains(string id)
        {
            return Find(id) != null;
        }
    }

    // A binding in JoyPro terms: JoyPro device string, JoyPro input token and modifiers.
    internal sealed class GameInputBinding
    {
        public string ActionId { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public bool IsAxis { get; set; }
        public List<string> Reformers { get; set; }
        public bool Inverted { get; set; }
        public double Deadzone { get; set; }
        public double SaturationX { get; set; }
        public double SaturationY { get; set; }
        public List<double> Curvature { get; set; }
        public string RelationName { get; set; }
        public string Title { get; set; }
        // Export: the axis tuning the game uses for this bind (resolved from the relation's scopes, in the game's
        // model). Import: the game's own response settings for the bind, if the game has any (stored for the game,
        // not as the bind's base).
        public AxisTuning Tuning { get; set; }
        // Import: keep Tuning as the game's tuning even when it holds no dead zone or curve (a one-sided throttle the
        // game reads from a game-wide range setting would otherwise be dropped as "neutral").
        public bool KeepTuning { get; set; }

        public GameInputBinding()
        {
            ActionId = string.Empty;
            Device = string.Empty;
            Input = string.Empty;
            Reformers = new List<string>();
            SaturationX = 1.0;
            SaturationY = 1.0;
            Curvature = new List<double> { 0.0 };
            RelationName = string.Empty;
            Title = string.Empty;
        }

        public bool IsKeyboard
        {
            get { return string.Equals((Device ?? string.Empty).Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase); }
        }

        public string Describe()
        {
            string modifiers = Reformers == null || Reformers.Count == 0
                ? string.Empty
                : " + " + string.Join(" + ", Reformers.Select(DescribeReformer));
            return ActionId + " <- " + Device + " " + Input + modifiers;
        }

        private static string DescribeReformer(string reformer)
        {
            string[] parts = (reformer ?? string.Empty).Split('§');
            return parts.Length >= 3 ? parts[1] + " " + parts[2] : reformer;
        }
    }

    // How far an integration supports a feature (IGameIntegration.KeyboardBinds, ModifierBinds).
    internal enum GameFeature
    {
        No,
        Partial,
        Yes
    }

    internal enum GameExportMode
    {
        Clean,
        Add,
        Merge,
        MergeOverwrite
    }

    internal class GameIoResult
    {
        public string Game { get; set; }
        public string Action { get; set; }
        public string ConfigPath { get; set; }
        public string Mode { get; set; }
        public bool DryRun { get; set; }
        public int BindingsRead { get; set; }
        public int BindingsWritten { get; set; }
        public int BindingsSkipped { get; set; }
        public int RelationsImported { get; set; }
        public int NodesAddedToExisting { get; set; }
        public int ConflictsKept { get; set; }
        public List<string> FilesWritten { get; private set; }
        public List<string> Steps { get; private set; }
        public List<string> Warnings { get; private set; }
        public List<string> Errors { get; private set; }
        // Games that write into the user's own profiles (MSFS 2024): per device and category which profile was
        // updated, or why not.
        public List<GameProfileUpdate> ProfileUpdates { get; private set; }

        public GameIoResult()
        {
            ProfileUpdates = new List<GameProfileUpdate>();
            Game = string.Empty;
            Action = string.Empty;
            ConfigPath = string.Empty;
            Mode = string.Empty;
            FilesWritten = new List<string>();
            Steps = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }

    internal sealed class GameProfileUpdate
    {
        public string Device { get; set; }
        public string Category { get; set; }
        // The profile's name as the game shows it ("" when none was written).
        public string Profile { get; set; }
        // updated, unchanged, dry-run, import-file (not written into the profile; the import file has the binds).
        public string Status { get; set; }
        public string Reason { get; set; }
        public int Binds { get; set; }
        // "<relation> (<action>) (<category>) - <reason>" of every bind that did not land.
        public List<string> NotWritten { get; set; }

        public GameProfileUpdate()
        {
            Device = Category = Profile = Status = Reason = string.Empty;
            NotWritten = new List<string>();
        }
    }

    internal sealed class GameScanResult : GameIoResult
    {
        public List<GameInputBinding> Bindings { get; private set; }

        public GameScanResult()
        {
            Bindings = new List<GameInputBinding>();
        }
    }

    internal sealed class GameExportRequest
    {
        public string ConfigPath { get; set; }
        public GameExportMode Mode { get; set; }
        public bool DryRun { get; set; }
        // --include-keyboard: keyboard binds reach Write only when the user asked for them.
        public bool IncludeKeyboard { get; set; }
        // --nuke-untouched-devices: devices without binds in the export lose their binds too (see ExclusiveFlightSim).
        public bool NukeUnusedDevices { get; set; }
        // The profile has no binds for the game but settings (engine ffb set ... --game G): only those are written,
        // no bind is touched (set by games whose IGameSettingsExport.WriteSettings runs Write in this mode).
        public bool SettingsOnly { get; set; }
        public GameCatalog Catalog { get; set; }
        public GameDeviceDirectory Devices { get; set; }
        public List<GameInputBinding> Bindings { get; set; }

        public GameExportRequest()
        {
            ConfigPath = string.Empty;
            Mode = GameExportMode.MergeOverwrite;
            Bindings = new List<GameInputBinding>();
        }
    }

    // For games with fixed gamepad layouts JoyPro knows (XInput-only games in virtual Xbox mode): action id -> the Unreal
    // gamepad key (Gamepad_LeftX ...) the game uses for it in the layout the user picked; the planner pins the physical
    // input of that action to this control (EngineVJoy.GamepadLayout).
    internal interface IGameGamepadLayout
    {
        Dictionary<string, string> GamepadLayout(GameCatalog catalog, List<string> warnings);
    }

    // For games that store gamepad binds (XInput-only games in virtual Xbox mode, docs/virtual-joystick.md): in that mode
    // the export's bindings come on device EngineVJoy.XboxDeviceName with XB_* inputs (XboxControls), and Write stores them
    // as the game's gamepad binds.
    internal interface IGameGamepadBinds
    {
    }

    // For "one-device" games (docs/games/one-device-games.md): games that read only one joystick, or no joystick at all
    // but Xbox pads (XInput). engine vjoy status and the GUI suggest the virtual mode for them, and an export in native
    // mode warns when the binds use more joysticks than the game reads.
    internal interface IGameDeviceLimit
    {
        // Joysticks (DirectInput) the game reads at once; 0 = none (keyboard/mouse and XInput pads only).
        int MaxJoysticks { get; }
        // The virtual mode JoyPro suggests: "virtual" (one vJoy device) or "xbox" (the virtual Xbox 360 pad).
        string SuggestedMode { get; }
        // Why, in one sentence for the user.
        string DeviceLimitNote { get; }
        // vJoy axes the game must not see ("rx", "ry": Battlefield takes a joystick with Rx/Ry for an Xbox pad); in
        // virtual mode JoyPro gives no input these axes and configure-vjoy leaves them out. Empty when none.
        IReadOnlyList<string> AvoidVJoyAxes { get; }
    }

    // For games with several named profiles per device in one place (MSFS): the name picked with --game-profile.
    internal interface IGameProfileSelection
    {
        string SelectedProfile { get; set; }
    }

    // For games with a list of installed aircraft (MSFS: DB\MSFS\aircraft.tsv) whose controls are not stored per
    // aircraft by JoyPro's export: a node can still be limited to some aircraft (AIRCRAFT keys other than the game id).
    // The export writes the nodes for all aircraft (no AIRCRAFT entry or the game id active) and, when the user picks
    // one with --aircraft, also the nodes limited to that aircraft; the other limited nodes are skipped with a warning.
    // MSFS 2024 export: write into the user's own active profiles (--user-profiles, default on) and import files for
    // every device (--import-files; else only where the direct write could not update).
    internal interface IGameUserProfileWrite
    {
        bool WriteUserProfiles { get; set; }
        bool AlwaysWriteImportFiles { get; set; }
    }

    internal interface IGameAircraftScope
    {
        string ExportAircraft { get; set; }
        // The aircraft keys JoyPro knows (empty when the list was never refreshed).
        IReadOnlyList<string> AircraftKeys();
        // Why limited relations cannot be exported per aircraft, for the export's warnings.
        string PerAircraftNote { get; }
    }

    // For games with a generic process name (Cliffs of Dover's "Launcher" is also Falcon BMS's): a running process with
    // the name only counts as the game when its exe is the game's own (GameProcessGuard).
    internal interface IGameProcessFilter
    {
        bool IsGameProcess(string exePath);
    }

    // For games whose import reads more than the export writes (MSFS: the user's own profiles of Steam and the Microsoft
    // Store beside JoyPro's preset package). Set by import and scan only.
    internal interface IGameImportReading
    {
        bool ImportReading { get; set; }
    }

    // For games that keep the binds of every device they ever saw (MSFS): import takes only the connected devices
    // unless this is set (--include-disconnected).
    internal interface IGameDisconnectedDevices
    {
        bool IncludeDisconnectedDevices { get; set; }
    }

    // For games that run untouched devices on a default preset they ship (MSFS): import takes such a preset only when
    // the game's own records say it is used, unless this is set (--include-sim-defaults).
    internal interface IGameSimDefaults
    {
        bool IncludeUnknownSimDefaults { get; set; }
    }

    // For games where one action holds a list of inputs: records which inputs JoyPro owns, so an
    // overwrite export can replace JoyPro's own inputs without touching inputs the user added in the game.
    internal interface IGameOwnershipTracking
    {
        void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result);
    }

    // For games whose config file also holds game-wide control settings (FfbSettings model of the game, device "*"):
    // import reads them into the profile, and an export writes them even when the profile has no binds for the game.
    internal interface IGameSettingsFile
    {
        // The game-wide settings the file sets, in the FfbModel's units.
        Dictionary<string, double> ReadSettings(string configPath, List<string> warnings);
        // Writes only the game-wide settings (no binds to export).
        GameIoResult WriteSettings(GameExportRequest request);
    }

    // For the game config watcher (engine sync): the game-wide settings (device "*") the config holds now, in the
    // FfbModel's units, without importing them (games that implement IGameSettingsFile use ReadSettings instead).
    internal interface IGameSettingsReader
    {
        Dictionary<string, double> ReadGameSettings(string configPath, List<string> warnings);
    }

    // For the game config watcher, beside IGameSettingsReader: games whose game-wide settings belong to another FfbModel
    // than their GameId (VTOL VR native: VTOLVR) or live in files other than the config path (watched with it).
    internal interface IGameSettingsSource
    {
        // The game of the settings' FfbModel and ffb scope (engine ffb set --game <SettingsGame>).
        string SettingsGame { get; }
        // The files besides the config path that hold them, existing or not (empty when the config holds them).
        List<string> SettingsFiles(string configPath);
    }

    // For games with game-wide settings JoyPro manages in files of their own (engine ffb set --game <game>): an export
    // without binds of the game still writes them (Write writes them together with the binds).
    internal interface IGameSettingsExport
    {
        bool HasSettings();
        GameIoResult WriteSettings(GameExportRequest request);
    }

    // For games whose catalog refresh also adds rows to DB files next to actions.tsv (X4: order.tsv and functions.tsv,
    // VTOL VR: seats.tsv). RefreshCatalog only prepares them, so a probe that calls it writes nothing; refresh-db saves
    // them after the catalog. Returns the files written (none when nothing changed).
    internal interface IGameCatalogRefreshFiles
    {
        List<string> SaveRefreshFiles(List<string> warnings);
    }

    // A game supported through the registry. Implementations only deal with their file format;
    // profile access, CLI parsing, catalogs and safety checks are shared.
    // A game whose config file may also be given as a folder (its installation, launcher folder or input folder) or as
    // the game's executable: the integration names the file it means.
    internal interface IGameConfigPathResolver
    {
        string ResolveConfigPath(string path);
    }

    internal interface IGameIntegration
    {
        // Value of RelationItem.Game and of the plane key for this game.
        string GameId { get; }
        string DisplayName { get; }
        // CLI area names, lower case.
        IReadOnlyList<string> Aliases { get; }
        string DbFolder { get; }
        bool Experimental { get; }
        // A game that is only about flying, where JoyPro takes over the devices it writes: clean mode clears every
        // bind of those devices and nuke clears the devices the profile does not use. In games with other vehicles or
        // on-foot play both only touch the cataloged flight actions (aircraft, helicopters, spaceships).
        bool ExclusiveFlightSim { get; }
        // Short user-facing note, e.g. what the user has to do in the game after an export.
        string Notes { get; }
        // Executables that rewrite the config on exit; exports are refused while one runs.
        IReadOnlyList<string> ProcessNames { get; }
        bool SupportsCatalogRefresh { get; }
        // What Read and Write do with keyboard binds (only with --include-keyboard) and with modifier buttons (a bind's
        // Reformers). The game support page (engine games support, docs/games/support.md) shows them, so a change of
        // the reader or writer changes them here too.
        GameFeature KeyboardBinds { get; }
        GameFeature ModifierBinds { get; }

        List<string> DiscoverConfigPaths(List<string> warnings);
        GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices);
        GameIoResult Write(GameExportRequest request);
        // Builds a catalog from the installed game or its files; null when nothing could be read.
        GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result);
    }
}
