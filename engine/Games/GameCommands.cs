using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // CLI for registry games: engine <game> paths|catalog|scan|import|export|refresh-db
    internal static class GameCommands
    {
        public static readonly string[] Actions = { "paths", "catalog", "scan", "import", "export", "refresh-db" };

        // refresh-db reads the game's packed archives for the command list (War Thunder's *.vromfs.bin). On by default
        // since the user approved it (2026-10-12); --no-game-archives (GUI: Settings > Games) turns it off and leaves
        // JoyPro's own list (docs/games/war-thunder.md, docs/licensing-audit.md). --read-game-archives is still accepted.
        public static bool ReadGameArchives { get; private set; } = true;

        // refresh-db --oodle-dll <path>: the user agreed that JoyPro may use this Oodle DLL of another installed game to
        // read Hell Let Loose: Vietnam's files (docs/games/hll-vietnam.md "Oodle"). Null = not allowed.
        public static string ForeignOodleDll { get; private set; }

        public static bool IsMutating(List<string> command)
        {
            string action = command == null || command.Count == 0 ? string.Empty : command[0].ToLowerInvariant();
            return action == "import";
        }

        public static int Run(IGameIntegration game, List<string> command, bool textOutput, out string payload)
        {
            payload = string.Empty;
            string action = command.Count == 0 ? "paths" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            // VTOL VR's BYOJoystick mod on this PC (status, prepare the install from the Steam Workshop).
            if (action == "mod")
            {
                if (!(game is VtolVrIntegration))
                    return Fail(game.DisplayName + " has no mod JoyPro installs.");
                return VtolVrMod.RunCommand(args.Where(a => !string.Equals(a, "--text", StringComparison.OrdinalIgnoreCase)).ToList(), textOutput, out payload);
            }
            // Hell Let Loose: Vietnam's Oodle situation (own DLL, or one of another installed game the GUI asks about).
            if (action == "oodle")
            {
                HllVietnamIntegration hllv = game as HllVietnamIntegration;
                if (hllv == null)
                    return Fail(game.DisplayName + " needs no Oodle library.");
                string oodlePath = PopValue(args, "--path");
                payload = hllv.OodleStatus(oodlePath ?? args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)), textOutput);
                return 0;
            }

            string pathOption = PopValue(args, "--path");
            string modeRaw = PopValue(args, "--mode");
            bool dryRun = PopFlag(args, "--dry-run");
            string gameProfile = PopValue(args, "--game-profile");
            string aircraft = PopValue(args, "--aircraft");
            bool replace = PopFlag(args, "--replace");
            bool includeKeyboard = PopSwitch(args, "--include-keyboard");
            bool nuke = PopSwitch(args, "--nuke-untouched-devices");
            bool includeDisconnectedGiven = args.Any(a => string.Equals(a, "--include-disconnected", StringComparison.OrdinalIgnoreCase));
            bool includeDisconnected = PopSwitch(args, "--include-disconnected");
            bool includeSimDefaultsGiven = args.Any(a => string.Equals(a, "--include-sim-defaults", StringComparison.OrdinalIgnoreCase));
            bool includeSimDefaults = PopSwitch(args, "--include-sim-defaults");
            // refresh-db: read the game's packed archives (War Thunder's *.vromfs.bin) unless --no-game-archives (or
            // --read-game-archives false) says otherwise.
            bool readArchivesGiven = args.Any(a => string.Equals(a, "--read-game-archives", StringComparison.OrdinalIgnoreCase));
            bool readArchives = PopSwitch(args, "--read-game-archives");
            bool noArchives = PopSwitch(args, "--no-game-archives");
            ReadGameArchives = !noArchives && (!readArchivesGiven || readArchives);
            // hllv refresh-db: the Oodle DLL of another installed game the user allowed (GUI asks first); loaded from
            // where it is, only for this refresh.
            ForeignOodleDll = PopValue(args, "--oodle-dll");
            PopFlag(args, "--merge");
            string positionalPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (positionalPath != null)
                args.Remove(positionalPath);

            List<string> unknown = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown " + game.GameId + " " + action + " argument(s): " + string.Join(" ", unknown));

            string explicitPath = string.IsNullOrWhiteSpace(pathOption) ? positionalPath : pathOption;
            if (!string.IsNullOrWhiteSpace(gameProfile))
            {
                IGameProfileSelection selection = game as IGameProfileSelection;
                if (selection == null)
                    return Fail(game.DisplayName + " has no named profiles; --game-profile is not supported.");
                selection.SelectedProfile = gameProfile.Trim();
            }
            if (!string.IsNullOrWhiteSpace(aircraft))
            {
                IGameAircraftScope scope = game as IGameAircraftScope;
                if (scope == null)
                    return Fail(game.DisplayName + " has no aircraft list; --aircraft is not supported.");
                if (action != "export")
                    return Fail("--aircraft is only valid with " + game.Aliases[0] + " export.");
                scope.ExportAircraft = aircraft.Trim();
            }

            // Import and scan read everything the game has of the user's (MSFS: Steam and Microsoft Store profiles beside
            // JoyPro's preset package).
            IGameImportReading importReading = game as IGameImportReading;
            if (importReading != null)
                importReading.ImportReading = action == "import" || action == "scan";
            // MSFS keeps the profiles of every device it ever saw: import takes the connected ones unless asked.
            IGameDisconnectedDevices disconnected = game as IGameDisconnectedDevices;
            if (disconnected != null)
                disconnected.IncludeDisconnectedDevices = includeDisconnected;
            else if (includeDisconnectedGiven && includeDisconnected)
                return Fail(game.DisplayName + " imports every device it has binds for; --include-disconnected is only for Microsoft Flight Simulator.");
            // MSFS: default presets the sim ships are taken only where its records show it uses them, unless asked.
            IGameSimDefaults simDefaults = game as IGameSimDefaults;
            if (simDefaults != null)
                simDefaults.IncludeUnknownSimDefaults = includeSimDefaults;
            else if (includeSimDefaultsGiven && includeSimDefaults)
                return Fail(game.DisplayName + " has no default presets of its own; --include-sim-defaults is only for Microsoft Flight Simulator.");

            // The environment of the run (install, config folders, drives, attributes, devices): once, in the log.
            if (action == "scan" || action == "import" || action == "export" || action == "refresh-db")
            {
                List<string> environmentPaths = new List<string>();
                if (!string.IsNullOrWhiteSpace(explicitPath))
                    environmentPaths.Add(explicitPath);
                try
                {
                    environmentPaths.AddRange(GameInstalls.WithConfigOverride(game.GameId, game.DiscoverConfigPaths(new List<string>())));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    JoyPro.Logging.JoyProLog.Debug("Diagnostics", game.GameId + ": config paths not found: " + ex.Message);
                }
                EngineDiagnostics.LogEnvironment(game.GameId, environmentPaths);
            }

            switch (action)
            {
                case "paths":
                    return RunPaths(game, textOutput, out payload);
                case "catalog":
                    return RunCatalog(game, textOutput, out payload);
                case "scan":
                    return RunScan(game, explicitPath, textOutput, out payload);
                case "import":
                    return RunImport(game, explicitPath, !replace, includeKeyboard, textOutput, out payload);
                case "export":
                    return RunExport(game, explicitPath, modeRaw, dryRun, includeKeyboard, nuke, textOutput, out payload);
                case "refresh-db":
                    return RunRefresh(game, explicitPath, dryRun, textOutput, out payload);
                default:
                    return Fail("Unknown " + game.GameId + " action: " + action + ". Use " + string.Join(", ", Actions) + ".");
            }
        }

        private static int RunPaths(IGameIntegration game, bool textOutput, out string payload)
        {
            List<string> warnings = new List<string>();
            List<string> paths = GameInstalls.WithConfigOverride(game.GameId, game.DiscoverConfigPaths(warnings));
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new { Game = game.GameId, DisplayName = game.DisplayName, ConfigPaths = paths, Warnings = warnings });
                return 0;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(game.DisplayName + " Config Paths");
            sb.AppendLine("Paths: " + paths.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string path in paths)
                sb.AppendLine("  - " + path);
            AppendList(sb, "Warnings", warnings);
            payload = sb.ToString();
            return 0;
        }

        private static int RunCatalog(IGameIntegration game, bool textOutput, out string payload)
        {
            List<string> warnings = new List<string>();
            GameCatalog catalog = GameCatalogStore.Load(game, warnings);
            if (!textOutput)
            {
                payload = EngineSerialization.ToJson(new
                {
                    Game = game.GameId,
                    Source = catalog.SourcePath,
                    Actions = catalog.Actions.Select(a => new { a.Id, a.Title, a.IsAxis, a.Category }).ToList(),
                    Warnings = warnings
                });
                return 0;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(game.DisplayName + " Action Catalog");
            sb.AppendLine("Source: " + catalog.SourcePath);
            sb.AppendLine("Actions: " + catalog.Actions.Count.ToString(CultureInfo.InvariantCulture));
            foreach (GameAction a in catalog.Actions)
                sb.AppendLine("  - " + a.Id + " | " + (a.IsAxis ? "axis" : "button") + " | " + a.Category + " | " + a.Title);
            AppendList(sb, "Warnings", warnings);
            payload = sb.ToString();
            return 0;
        }

        private static int RunScan(IGameIntegration game, string explicitPath, bool textOutput, out string payload)
        {
            payload = string.Empty;
            GameScanResult result;
            if (!TryReadGameFile(game, explicitPath, "scan", out result))
            {
                payload = Render(result, textOutput, null);
                return 1;
            }

            payload = Render(result, textOutput, result.Bindings);
            return result.Errors.Count == 0 ? 0 : 1;
        }

        private static int RunImport(IGameIntegration game, string explicitPath, bool merge, bool includeKeyboard, bool textOutput, out string payload)
        {
            GameScanResult result;
            if (!TryReadGameFile(game, explicitPath, "import", out result))
            {
                payload = Render(result, textOutput, null);
                return 1;
            }

            GameCatalog catalog = GameCatalogStore.Load(game, new List<string>());
            List<GameInputBinding> toImport = WithoutKeyboard(result.Bindings, includeKeyboard, result, "imported");
            List<GameInputBinding> applied = GameProfileBridge.ApplyImport(game, catalog, toImport, merge, result);
            result.Steps.Add(merge ? "Merged into the loaded profile." : "Replaced the relations of the loaded profile.");
            IGameSettingsFile settingsFile = game as IGameSettingsFile;
            if (settingsFile != null)
                ImportSettings(game, settingsFile, result);
            IGameOwnershipTracking tracking = game as IGameOwnershipTracking;
            if (tracking != null)
                tracking.RecordImport(result.ConfigPath, applied, GameDeviceDirectory.Discover(result.Warnings), result);
            // A game never exported (no watcher record yet) is watched from its import on (engine sync).
            if (result.Errors.Count == 0 && string.IsNullOrWhiteSpace(explicitPath) && !string.IsNullOrWhiteSpace(result.ConfigPath))
                EngineSync.RecordAfterImport(game.GameId, result.ConfigPath, result.Warnings);
            payload = Render(result, textOutput, result.Bindings);
            return result.Errors.Count == 0 ? 0 : 1;
        }

        // The game-wide settings of the file become the profile's settings for the game (device "*"), so the GUI shows
        // them and an export writes them back unchanged.
        private static void ImportSettings(IGameIntegration game, IGameSettingsFile settingsFile, GameIoResult result)
        {
            Dictionary<string, double> read = settingsFile.ReadSettings(result.ConfigPath, result.Warnings);
            if (read == null || read.Count == 0)
                return;
            if (InternalDataManagement.FfbOverrides == null)
                InternalDataManagement.FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string key = JoyPro.Engine.Axis.FfbSettings.Key(JoyPro.Engine.Axis.FfbSettings.AnyDevice, JoyPro.Engine.Axis.BindTuning.GameScope(game.GameId));
            string stored;
            Dictionary<string, double> values = InternalDataManagement.FfbOverrides.TryGetValue(key, out stored)
                ? JoyPro.Engine.Axis.FfbSettings.Parse(stored)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> value in read)
                values[value.Key] = value.Value;
            InternalDataManagement.FfbOverrides[key] = JoyPro.Engine.Axis.FfbSettings.Format(values);
            result.Steps.Add("Game settings imported: " + JoyPro.Engine.Axis.FfbSettings.Format(read));
        }

        private static bool TryReadGameFile(IGameIntegration game, string explicitPath, string action, out GameScanResult result)
        {
            List<string> warnings = new List<string>();
            string path = ResolveConfigPath(game, explicitPath, warnings);
            GameIoResult catalogResult = new GameIoResult();
            GameCatalog catalog = LoadOrBuildCatalog(game, false, catalogResult);
            warnings.AddRange(catalogResult.Warnings);
            GameDeviceDirectory devices = GameDeviceDirectory.Discover(warnings);

            if (string.IsNullOrWhiteSpace(path))
            {
                result = new GameScanResult();
                result.Warnings.AddRange(warnings);
                result.Errors.Add("No " + game.DisplayName + " config file found. Pass the path explicitly.");
            }
            else
            {
                result = game.Read(path, catalog, devices) ?? new GameScanResult();
                result.Warnings.InsertRange(0, warnings);
                result.ConfigPath = path;
            }

            result.Steps.InsertRange(0, catalogResult.Steps);
            result.Game = game.GameId;
            result.Action = action;
            result.BindingsRead = result.Bindings.Count;
            JoyPro.Logging.JoyProLog.Info("Import", game.GameId + " " + action + " read " + (string.IsNullOrWhiteSpace(path) ? "(no config file found)" : path) + ": " +
                                                    result.Bindings.Count.ToString(CultureInfo.InvariantCulture) + " binding(s), " + devices.Devices.Count.ToString(CultureInfo.InvariantCulture) +
                                                    " device(s), " + result.Errors.Count.ToString(CultureInfo.InvariantCulture) + " error(s)" +
                                                    (result.Errors.Count > 0 ? ": " + string.Join(" | ", result.Errors) : string.Empty));
            return result.Errors.Count == 0;
        }

        private static int RunExport(IGameIntegration game, string explicitPath, string modeRaw, bool dryRun, bool includeKeyboard, bool nuke, bool textOutput, out string payload)
        {
            GameIoResult result = new GameIoResult();
            result.Game = game.GameId;
            result.Action = "export";
            result.DryRun = dryRun;

            GameExportMode mode;
            if (!TryParseMode(modeRaw, out mode))
            {
                result.Errors.Add("Invalid export mode '" + modeRaw + "'. Use clean, add, merge or merge-overwrite.");
                payload = Render(result, textOutput, null);
                return 1;
            }
            result.Mode = ModeToString(mode);

            List<string> warnings = new List<string>();
            string path = ResolveConfigPath(game, explicitPath, warnings);
            result.Warnings.AddRange(warnings);
            if (string.IsNullOrWhiteSpace(path))
            {
                result.Errors.Add("No " + game.DisplayName + " config file found. Pass the path explicitly.");
                payload = Render(result, textOutput, null);
                return 1;
            }
            result.ConfigPath = path;

            List<string> running = GameProcessGuard.FindRunning(game);
            if (running.Count > 0 && !dryRun)
            {
                result.Errors.Add(game.DisplayName + " is running (" + string.Join(", ", running) + "). Close the game first; it overwrites its config on exit.");
                payload = Render(result, textOutput, null);
                return 1;
            }

            GameExportRequest request = new GameExportRequest
            {
                ConfigPath = path,
                Mode = mode,
                DryRun = dryRun,
                IncludeKeyboard = includeKeyboard,
                NukeUnusedDevices = nuke,
                Catalog = LoadOrBuildCatalog(game, dryRun, result),
                Devices = GameDeviceDirectory.Discover(result.Warnings)
            };

            List<GameInputBinding> bindings = WithoutKeyboard(GameProfileBridge.CollectBindings(game, result), includeKeyboard, result, "exported");
            foreach (GameInputBinding binding in bindings)
            {
                if (request.Catalog.Actions.Count > 0 && !request.Catalog.Contains(binding.ActionId))
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "': '" + binding.ActionId + "' is not a " + game.DisplayName + " flight action JoyPro manages; skipped.");
                    continue;
                }
                request.Bindings.Add(binding);
            }
            JoyPro.Logging.JoyProLog.Info("Export", game.GameId + " export to " + path + ": mode=" + result.Mode + ", dry-run=" + dryRun + ", include-keyboard=" + includeKeyboard +
                                                    ", nuke-untouched-devices=" + nuke + ", " + request.Bindings.Count.ToString(CultureInfo.InvariantCulture) + " bind(s), " +
                                                    result.BindingsSkipped.ToString(CultureInfo.InvariantCulture) + " skipped, catalog " + request.Catalog.Actions.Count.ToString(CultureInfo.InvariantCulture) +
                                                    " action(s), " + request.Devices.Devices.Count.ToString(CultureInfo.InvariantCulture) + " device(s)");

            // Also in clean mode: an export without binds could only erase the user's flight bindings. Game-wide
            // settings the profile holds for the game are still written.
            IGameSettingsFile settingsFile = game as IGameSettingsFile;
            // (A game that also exports its settings itself, such as BMS with per-device detents, takes that path below.)
            if (request.Bindings.Count == 0 && settingsFile != null && !(game is IGameSettingsExport) &&
                JoyPro.Engine.Axis.FfbSettings.Resolve(InternalDataManagement.FfbOverrides, JoyPro.Engine.Axis.FfbSettings.AnyDevice, game.GameId, null).Count > 0)
            {
                result.Warnings.Add("The profile has no " + game.DisplayName + " binds; only the game-wide settings are written.");
                MergeInto(result, settingsFile.WriteSettings(request) ?? new GameIoResult());
                RecordSync(game, path, dryRun, result);
                payload = Render(result, textOutput, null);
                return result.Errors.Count == 0 ? 0 : 1;
            }
            if (request.Bindings.Count == 0)
            {
                IGameSettingsExport settings = game as IGameSettingsExport;
                if (settings != null && settings.HasSettings())
                {
                    result.Warnings.Add("The profile has no " + game.DisplayName + " binds; only the game settings were written.");
                    MergeInto(result, settings.WriteSettings(request) ?? new GameIoResult());
                    RecordSync(game, path, dryRun, result);
                    payload = Render(result, textOutput, null);
                    return result.Errors.Count == 0 ? 0 : 1;
                }
                result.Warnings.Add("The profile has no " + game.DisplayName + " binds. Nothing was written.");
                payload = Render(result, textOutput, null);
                return 0;
            }

            WarnDeviceLimit(game, request.Bindings, result);
            GameIoResult written = game.Write(request) ?? new GameIoResult();
            MergeInto(result, written);
            // JoyPro's own data of the exported binds (names, groups, modifiers, tuning) for a later import.
            if (!dryRun && written.Errors.Count == 0)
            {
                // The file read back as written: an import compares with it to see what was changed in the game.
                List<GameInputBinding> readBack = new List<GameInputBinding>();
                try
                {
                    GameScanResult back = game.Read(path, request.Catalog, request.Devices);
                    if (back != null && back.Bindings != null)
                        readBack = back.Bindings;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("Could not read the exported file back for JoyPro's metadata: " + ex.Message);
                }
                GameMetadataStore.Save(path, game.GameId, request.Bindings, readBack, result.Warnings);
            }
            RecordSync(game, path, dryRun, result);
            if (!string.IsNullOrWhiteSpace(game.Notes))
                result.Steps.Add(game.Notes);
            payload = Render(result, textOutput, request.Bindings);
            return result.Errors.Count == 0 ? 0 : 1;
        }

        private static int RunRefresh(IGameIntegration game, string sourcePath, bool dryRun, bool textOutput, out string payload)
        {
            GameIoResult result = new GameIoResult();
            result.Game = game.GameId;
            result.Action = "refresh-db";
            if (!game.SupportsCatalogRefresh)
            {
                result.Errors.Add(game.DisplayName + " has no DB refresh; its catalog ships with JoyPro.");
                payload = Render(result, textOutput, null);
                return 1;
            }

            GameCatalog current;
            GameCatalog refreshed = BuildCatalog(game, sourcePath, dryRun, result, out current);
            if (refreshed == null)
            {
                payload = Render(result, textOutput, null);
                return 1;
            }
            result.Steps.Add("Actions: " + current.Actions.Count.ToString(CultureInfo.InvariantCulture) + " -> " + refreshed.Actions.Count.ToString(CultureInfo.InvariantCulture));
            payload = Render(result, textOutput, null);
            return 0;
        }

        // refresh-db's work: reads the game (or sourcePath) and writes the catalog and the DB files next to it when
        // something changed (never on a dry run). Null when nothing could be read (result.Errors says why).
        private static GameCatalog BuildCatalog(IGameIntegration game, string sourcePath, bool dryRun, GameIoResult result, out GameCatalog current)
        {
            // Reading the game writes nothing of JoyPro's: the user may cancel until the catalog is written (EngineProgress).
            EngineProgress.SetCancelable(true);
            EngineProgress.Step(1, 3, game.DisplayName + ", reading JoyPro's command list", GameCatalogStore.CatalogPath(game));
            current = GameCatalogStore.Load(game, File.Exists(GameCatalogStore.CatalogPath(game)) ? result.Warnings : new List<string>());
            EngineProgress.ThrowIfCancelled("reading " + game.DisplayName + "'s files");
            EngineProgress.Step(2, 3, game.DisplayName + ", reading the game's files", string.IsNullOrWhiteSpace(sourcePath) ? "the installed game" : sourcePath);
            GameCatalog refreshed = game.RefreshCatalog(sourcePath, current, result);
            if (refreshed == null || refreshed.Actions.Count == 0)
            {
                if (result.Errors.Count == 0)
                    result.Errors.Add("No " + game.DisplayName + " actions could be read; the catalog was left unchanged.");
                EngineProgress.SetCancelable(false);
                return null;
            }
            EngineProgress.ThrowIfCancelled("reading " + game.DisplayName + "'s files");
            EngineProgress.SetCancelable(false);

            // Files are written only when something changed (the catalog, or DB files next to it a game refreshes too),
            // and never on a dry run.
            result.DryRun = dryRun;
            if (dryRun)
                result.Steps.Add("Dry run: nothing was written.");
            else
            {
                EngineProgress.Step(3, 3, game.DisplayName + ", writing the command list", refreshed.Actions.Count.ToString(CultureInfo.InvariantCulture) + " actions");
                if (!SameCatalog(current, refreshed) || !File.Exists(GameCatalogStore.CatalogPath(game)))
                {
                    GameCatalogStore.Save(game, refreshed, game.DisplayName + " action catalog, refreshed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    result.FilesWritten.Add(GameCatalogStore.CatalogPath(game));
                }
                IGameCatalogRefreshFiles extraFiles = game as IGameCatalogRefreshFiles;
                if (extraFiles != null)
                    result.FilesWritten.AddRange(extraFiles.SaveRefreshFiles(result.Warnings));
                if (result.FilesWritten.Count == 0)
                    result.Steps.Add("The catalog already has every action the source lists; nothing was written.");
                DbUpdateCheck.RecordCurrent(game.GameId, result.Warnings);
            }
            return refreshed;
        }

        // Set (tests) to 1: a missing command list is not built on demand.
        public const string NoCatalogBuildVariable = "JOYPRO_NO_CATALOG_BUILD";

        // A fresh install ships no command lists (docs/licensing-audit.md): the first scan, import or export of a game
        // without one builds it from the installed game, as refresh-db would (with progress; a dry run keeps it in
        // memory). Without a list every bind of the game would be left out. When the game cannot be read the reason
        // is reported and the command goes on with an empty list.
        internal static GameCatalog LoadOrBuildCatalog(IGameIntegration game, bool dryRun, GameIoResult result)
        {
            string path = GameCatalogStore.CatalogPath(game);
            if (File.Exists(path) || !game.SupportsCatalogRefresh || Environment.GetEnvironmentVariable(NoCatalogBuildVariable) == "1")
                return GameCatalogStore.Load(game, result.Warnings);

            JoyPro.Logging.JoyProLog.Info("Catalog", game.GameId + ": no command list at " + path + "; building it from the game.");
            GameIoResult build = new GameIoResult();
            GameCatalog current;
            GameCatalog built = BuildCatalog(game, null, dryRun, build, out current);
            result.Warnings.AddRange(build.Warnings);
            if (built == null)
            {
                result.Warnings.Add("JoyPro has no " + game.DisplayName + " command list yet, and building it from the installed game did not work: " +
                                    string.Join(" ", build.Errors) + " Without it the game's binds cannot be matched; build it with Games > DB refresh (engine " + game.Aliases[0] + " refresh-db, --path <file> for a file of the game).");
                return new GameCatalog { GameId = game.GameId, SourcePath = path };
            }
            result.Steps.Add("JoyPro had no " + game.DisplayName + " command list yet: built it from the installed game (" + built.Actions.Count.ToString(CultureInfo.InvariantCulture) +
                             " actions" + (dryRun ? ", dry run: not saved" : ", saved to " + path) + ").");
            result.Steps.AddRange(build.Steps.Where(s => !s.StartsWith("Dry run", StringComparison.Ordinal)).Select(s => "  " + s));
            return dryRun ? built : GameCatalogStore.Load(game, result.Warnings);
        }

        // The written config is JoyPro's own state: its fingerprint is recorded, so the game config watcher does not ask
        // about it (engine sync).
        private static void RecordSync(IGameIntegration game, string path, bool dryRun, GameIoResult result)
        {
            if (!dryRun && result.Errors.Count == 0)
                EngineSync.RecordAfterExport(game.GameId, path, result.Warnings);
        }

        private static bool SameCatalog(GameCatalog a, GameCatalog b)
        {
            if (a == null || b == null || a.Actions.Count != b.Actions.Count)
                return false;
            for (int i = 0; i < a.Actions.Count; i++)
            {
                GameAction x = a.Actions[i];
                GameAction y = b.Actions[i];
                if (x.Id != y.Id || x.Title != y.Title || x.IsAxis != y.IsAxis || x.Category != y.Category || (x.NativeId ?? string.Empty) != (y.NativeId ?? string.Empty))
                    return false;
            }
            return true;
        }

        private static string ResolveConfigPath(IGameIntegration game, string explicitPath, List<string> warnings)
        {
            // A registry key (Unity PlayerPrefs games) is no file path.
            if (!string.IsNullOrWhiteSpace(explicitPath) && GameConfigLocation.IsRegistryKey(explicitPath.Trim().Trim('"')))
                return explicitPath.Trim().Trim('"');
            IGameConfigPathResolver resolver = game as IGameConfigPathResolver;
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath.Trim().Trim('"')));
                return resolver != null ? resolver.ResolveConfigPath(full) : full;
            }
            string found = GameInstalls.WithConfigOverride(game.GameId, game.DiscoverConfigPaths(warnings)).FirstOrDefault() ?? string.Empty;
            return resolver != null && found.Length > 0 ? resolver.ResolveConfigPath(found) : found;
        }

        private static void MergeInto(GameIoResult target, GameIoResult source)
        {
            target.BindingsWritten += source.BindingsWritten;
            target.BindingsSkipped += source.BindingsSkipped;
            target.ConflictsKept += source.ConflictsKept;
            target.FilesWritten.AddRange(source.FilesWritten);
            target.Steps.AddRange(source.Steps);
            target.Warnings.AddRange(source.Warnings);
            target.Errors.AddRange(source.Errors);
        }

        public static bool TryParseMode(string raw, out GameExportMode mode)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "merge-overwrite":
                case "mergeoverwrite":
                case "overwrite":
                    mode = GameExportMode.MergeOverwrite;
                    return true;
                case "merge":
                    mode = GameExportMode.Merge;
                    return true;
                case "add":
                    mode = GameExportMode.Add;
                    return true;
                case "clean":
                    mode = GameExportMode.Clean;
                    return true;
                default:
                    mode = GameExportMode.MergeOverwrite;
                    return false;
            }
        }

        public static string ModeToString(GameExportMode mode)
        {
            switch (mode)
            {
                case GameExportMode.Clean: return "clean";
                case GameExportMode.Add: return "add";
                case GameExportMode.Merge: return "merge";
                default: return "merge-overwrite";
            }
        }

        private static string Render(GameIoResult result, bool textOutput, List<GameInputBinding> bindings)
        {
            if (!textOutput)
            {
                return EngineSerialization.ToJson(new
                {
                    result.Game,
                    result.Action,
                    result.ConfigPath,
                    result.Mode,
                    result.DryRun,
                    result.BindingsRead,
                    result.BindingsWritten,
                    result.BindingsSkipped,
                    result.RelationsImported,
                    result.NodesAddedToExisting,
                    result.ConflictsKept,
                    result.FilesWritten,
                    Bindings = (bindings ?? new List<GameInputBinding>()).Select(b => new { b.ActionId, b.Device, b.Input, b.IsAxis, b.Reformers, b.Inverted, b.RelationName }).ToList(),
                    result.Steps,
                    result.Warnings,
                    result.Errors
                });
            }

            StringBuilder sb = new StringBuilder();
            IGameIntegration game;
            string title = GameRegistry.TryResolve(result.Game, out game) ? game.DisplayName : result.Game;
            sb.AppendLine(title + " " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.Action ?? string.Empty));
            sb.AppendLine("Config: " + (string.IsNullOrWhiteSpace(result.ConfigPath) ? "<none>" : result.ConfigPath));
            if (!string.IsNullOrWhiteSpace(result.Mode))
                sb.AppendLine("Mode: " + result.Mode);
            if (result.DryRun)
                sb.AppendLine("Dry Run: true");
            sb.AppendLine("Bindings Read: " + result.BindingsRead.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Bindings Written: " + result.BindingsWritten.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Bindings Skipped: " + result.BindingsSkipped.ToString(CultureInfo.InvariantCulture));
            if (result.Action == "import")
            {
                sb.AppendLine("Relations Imported: " + result.RelationsImported.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Nodes Added To Existing: " + result.NodesAddedToExisting.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Conflicts Kept: " + result.ConflictsKept.ToString(CultureInfo.InvariantCulture));
            }
            AppendList(sb, "Files Written", result.FilesWritten);
            if (bindings != null && bindings.Count > 0)
                AppendList(sb, "Bindings", bindings.Select(b => b.Describe()).ToList());
            AppendList(sb, "Steps", result.Steps);
            AppendList(sb, "Warnings", result.Warnings);
            AppendList(sb, "Errors", result.Errors);
            return sb.ToString();
        }

        private static void AppendList(StringBuilder sb, string header, List<string> items)
        {
            if (items == null || items.Count == 0)
                return;
            sb.AppendLine(header + ":");
            foreach (string item in items)
                sb.AppendLine("  - " + item);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 1;
        }

        private static string PopValue(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Count)
                return string.Empty;
            string value = args[index + 1];
            args.RemoveRange(index, 2);
            return value;
        }

        private static bool PopFlag(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;
            args.RemoveAt(index);
            return true;
        }

        // "--name", "--name true" or "--name false" (the DCS export spells its switches with a value).
        private static bool PopSwitch(List<string> args, string name)
        {
            int index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return false;
            bool value = true;
            if (index + 1 < args.Count && (args[index + 1].Equals("true", StringComparison.OrdinalIgnoreCase) || args[index + 1].Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                value = args[index + 1].Equals("true", StringComparison.OrdinalIgnoreCase);
                args.RemoveAt(index + 1);
            }
            args.RemoveAt(index);
            return value;
        }

        // Keyboard binds are imported and exported only on request (--include-keyboard).
        // One-device games (IGameDeviceLimit) in native mode: binds on more joysticks than the game reads cannot all work.
        private static void WarnDeviceLimit(IGameIntegration game, List<GameInputBinding> bindings, GameIoResult result)
        {
            IGameDeviceLimit limit = game as IGameDeviceLimit;
            if (limit == null || EngineVJoy.LoadSettings().IsAnyVirtual(game.GameId))
                return;
            List<string> devices = bindings.Where(b => EngineVJoy.IsJoystickDevice(b.Device)).Select(b => b.Device).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (devices.Count > limit.MaxJoysticks)
                result.Warnings.Add(game.DisplayName + " reads " + (limit.MaxJoysticks == 0 ? "no joystick" : limit.MaxJoysticks == 1 ? "one joystick" : limit.MaxJoysticks.ToString(CultureInfo.InvariantCulture) + " joysticks") +
                                    ", the binds use " + devices.Count.ToString(CultureInfo.InvariantCulture) + " (" + string.Join(", ", devices) + "). " + limit.DeviceLimitNote +
                                    " Switch it to virtual mode: engine vjoy mode " + game.GameId + " " + limit.SuggestedMode + ".");
        }

        private static List<GameInputBinding> WithoutKeyboard(List<GameInputBinding> bindings, bool includeKeyboard, GameIoResult result, string verb)
        {
            if (includeKeyboard)
                return bindings;
            int keyboard = bindings.Count(b => b.IsKeyboard);
            if (keyboard > 0)
                result.Steps.Add(keyboard.ToString(CultureInfo.InvariantCulture) + " keyboard bind(s) not " + verb + " (add --include-keyboard to include them).");
            return bindings.Where(b => !b.IsKeyboard).ToList();
        }
    }
}
