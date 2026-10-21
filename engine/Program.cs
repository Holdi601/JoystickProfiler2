using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using ZstdSharp;

namespace JoyPro.Engine
{
    internal static partial class Program
    {
        private static string InvocationDirectory = Environment.CurrentDirectory;
        private static string LoadCorrectionReportPath = string.Empty;
        private static bool StatusUpdatesEnabled = false;
        private static bool BenchmarkEnabled = false;
        private static readonly Dictionary<string, long> BenchmarksMs =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ConnectedJoystickCacheLock = new object();
        private static List<string> ConnectedJoystickCache = null;
        private static List<string> ConnectedJoystickCacheWarnings = null;
        private static readonly object DcsInputDeviceCacheLock = new object();
        private static readonly Dictionary<string, List<string>> DcsInputDeviceCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object StarCitizenP4kIndexCacheLock = new object();
        private static readonly Dictionary<string, Dictionary<string, StarCitizenP4kEntryDescriptor>> StarCitizenP4kIndexCache =
            new Dictionary<string, Dictionary<string, StarCitizenP4kEntryDescriptor>>(StringComparer.OrdinalIgnoreCase);

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                JoyPro.Logging.JoyProLog.Error("Crash", "Unhandled exception (terminating: " + e.IsTerminating + ")", e.ExceptionObject as Exception);
                WriteCrashReport(e.ExceptionObject as Exception, args);
            };
            // Background log (docs/logging.md): header, every step, the files written and what the run printed.
            JoyPro.Logging.JoyProLog.Start("engine", EngineVersion, args);
            EngineLogging.CaptureConsole();
            Stopwatch runClock = Stopwatch.StartNew();
            int exitCode = 1;
            try
            {
                exitCode = Run(args ?? new string[0]);
                return exitCode;
            }
            catch (EngineCancelledException ex)
            {
                // The user cancelled at a safe point (EngineProgress): nothing is half written.
                EngineProgress.WriteCancelled();
                Console.Error.WriteLine("engine error: " + ex.Message);
                JoyPro.Logging.JoyProLog.Warn("Engine", "Command cancelled by the user: " + ex.Message);
                exitCode = EngineProgress.CancelledExitCode;
                return exitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("engine error: " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                MainStructure.NoteError(ex);
                JoyPro.Logging.JoyProLog.Error("Engine", "Unexpected engine error", ex);
                string report = WriteCrashReport(ex, args);
                if (report.Length > 0)
                    Console.Error.WriteLine("Crash report: " + report);
                exitCode = 1;
                return 1;
            }
            finally
            {
                EngineLogging.LogCapturedOutput();
                JoyPro.Logging.JoyProLog.Info("Engine", "Run finished: exit code " + exitCode.ToString(CultureInfo.InvariantCulture) +
                                                        " after " + runClock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                JoyPro.Logging.JoyProLog.Flush();
            }
        }

        // Unexpected engine errors land next to the GUI's crash reports (%LOCALAPPDATA%\JoyPro2\CrashReports, or
        // JOYPRO_DATA_DIR), where the GUI offers to report them.
        private static string WriteCrashReport(Exception exception, string[] args)
        {
            try
            {
                string root = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
                if (string.IsNullOrWhiteSpace(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JoyPro2");
                // Only engines the app started (JOYPRO_CALLER=gui) land where the app reports them; command-line and tool
                // runs keep theirs in CrashReports\cli, so the app does not show the crashes of someone else's scripts.
                string folder = Path.Combine(root, "CrashReports");
                if (!string.Equals(Environment.GetEnvironmentVariable("JOYPRO_CALLER"), "gui", StringComparison.OrdinalIgnoreCase))
                    folder = Path.Combine(folder, "cli");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "engine-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("JoyPro2 engine crash report");
                sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("Engine: " + EngineVersion);
                sb.AppendLine("OS: " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
                sb.AppendLine("Command: engine " + string.Join(" ", (args ?? new string[0]).Select(a => a.Contains(" ") ? "\"" + a + "\"" : a)));
                sb.AppendLine("Log: " + JoyPro.Logging.JoyProLog.Folder + " (session " + JoyPro.Logging.JoyProLog.SessionId + ")");
                sb.AppendLine();
                sb.AppendLine(exception == null ? "(no exception object)" : exception.ToString());
                // The game, its environment and the files the run read or changed (EngineDiagnostics).
                string snapshot = EngineDiagnostics.Snapshot();
                if (snapshot.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Environment and files of this run:");
                    sb.AppendLine(snapshot);
                }
                File.WriteAllText(path, JoyPro.Logging.LogScrubber.Scrub(sb.ToString()), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string EngineVersion
        {
            get
            {
                object[] attributes = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                return attributes.Length > 0
                    ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion
                    : typeof(Program).Assembly.GetName().Version.ToString();
            }
        }

        private static int Run(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }
            if (args[0] == "version" || args[0] == "--version")
            {
                Console.WriteLine(EngineVersion);
                return 0;
            }

            InvocationDirectory = Environment.CurrentDirectory;
            List<string> tokens = new List<string>(args);
            string profilePath = PopOptionValue(tokens, "--profile");
            string outPath = PopOptionValue(tokens, "--out");
            string editor = PopOptionValue(tokens, "--editor");
            bool textOutput = PopFlag(tokens, "--text");
            bool statusRequested = PopOptionBoolean(tokens, "--status", false, out _);
            bool progressRequested = PopOptionBoolean(tokens, "--progress", false, out _);
            bool benchmarkRequested = PopOptionBoolean(tokens, "--benchmark", false, out _);
            bool loadCorrections = PopOptionBoolean(tokens, "--load-corrections", true, out bool loadCorrectionsProvided);
            bool autoAddDbItems = PopOptionBoolean(tokens, "--auto-add-db-items", true, out bool autoAddDbItemsProvided);
            bool remapMissingDevices = PopOptionBoolean(tokens, "--remap-missing-devices", true, out bool remapMissingDevicesProvided);
            bool confirmDeviceRemap = PopOptionBoolean(tokens, "--confirm-device-remap", true, out bool confirmDeviceRemapProvided);
            bool confirmItemCorrections = PopOptionBoolean(tokens, "--confirm-corrections", true, out bool confirmItemCorrectionsProvided);
            string correctionModeRaw = PopOptionValue(tokens, "--correction-mode");
            string correctionAutoThresholdRaw = PopOptionValue(tokens, "--correction-auto-threshold");
            string correctionReportPath = PopOptionValue(tokens, "--correction-report");
            // What the change is called in the profile history (the GUI passes its own words, docs/profile-history.md).
            EngineProfileHistory.Note = PopOptionValue(tokens, "--history-note") ?? string.Empty;

            profilePath = NormalizePath(profilePath);
            EngineProfileHistory.ProfilePath = profilePath ?? string.Empty;
            outPath = NormalizePath(outPath);
            correctionReportPath = NormalizePath(correctionReportPath);
            LoadCorrectionReportPath = correctionReportPath ?? string.Empty;
            StatusUpdatesEnabled = statusRequested || benchmarkRequested;
            CommandChangedNothing = false;
            // Structured progress lines (EngineProgress): with --status as well, or alone with --progress.
            EngineProgress.Reset();
            EngineProgress.Enabled = statusRequested || progressRequested;
            BenchmarkEnabled = benchmarkRequested;
            ResetDiscoveryCaches();
            BenchmarksMs.Clear();
            Stopwatch totalRun = Stopwatch.StartNew();

            InternalDataManagement.LoadItemCorrectionMode correctionMode;
            string modeError;
            if (!TryParseLoadCorrectionMode(correctionModeRaw, out correctionMode, out modeError))
                return Fail(modeError);
            int? correctionAutoThreshold;
            string thresholdError;
            if (!TryParseCorrectionAutoApplyThreshold(correctionAutoThresholdRaw, out correctionAutoThreshold, out thresholdError))
                return Fail(thresholdError);

            if (!loadCorrections && autoAddDbItemsProvided && autoAddDbItems)
                return Fail("--auto-add-db-items true requires --load-corrections true.");
            if (!loadCorrections && remapMissingDevicesProvided && remapMissingDevices)
                return Fail("--remap-missing-devices true requires --load-corrections true.");
            if (!remapMissingDevices && confirmDeviceRemapProvided && confirmDeviceRemap)
                return Fail("--confirm-device-remap true requires --remap-missing-devices true.");
            if (!loadCorrections && !string.IsNullOrWhiteSpace(correctionModeRaw))
                return Fail("--correction-mode requires --load-corrections true.");
            if (!loadCorrections && !string.IsNullOrWhiteSpace(correctionAutoThresholdRaw))
                return Fail("--correction-auto-threshold requires --load-corrections true.");
            if (!loadCorrections && confirmItemCorrectionsProvided && confirmItemCorrections)
                return Fail("--confirm-corrections true requires --load-corrections true.");

            if (!loadCorrections)
            {
                autoAddDbItems = false;
                remapMissingDevices = false;
                confirmDeviceRemap = false;
                confirmItemCorrections = false;
            }

            if (!remapMissingDevices)
                confirmDeviceRemap = false;

            if (tokens.Count == 0)
            {
                PrintHelp();
                return 1;
            }

            string area = tokens[0].ToLowerInvariant();
            List<string> command = tokens.Skip(1).ToList();

            // Read-only commands should not trigger load-time correction prompts.
            bool runLoadCorrectionsForCommand = ShouldRunLoadCorrections(area, command);
            bool effectiveLoadCorrections = loadCorrections && runLoadCorrectionsForCommand;
            bool effectiveAutoAddDbItems = effectiveLoadCorrections && autoAddDbItems;
            bool effectiveRemapMissingDevices = effectiveLoadCorrections && remapMissingDevices;
            bool effectiveConfirmDeviceRemap = effectiveRemapMissingDevices && confirmDeviceRemap;
            bool effectiveConfirmItemCorrections = effectiveLoadCorrections && confirmItemCorrections;

            InternalDataManagement.EnableLegacyLoadCorrections = effectiveLoadCorrections;
            InternalDataManagement.AutoAddDbItemsOnLoad = effectiveAutoAddDbItems;
            InternalDataManagement.RemapMissingJoysticksOnLoad = effectiveRemapMissingDevices;
            InternalDataManagement.ConfirmMissingJoystickRemapOnLoad = effectiveConfirmDeviceRemap;
            InternalDataManagement.LoadCorrectionMessageSink = null;
            InternalDataManagement.LoadDeviceRemapConfirmation = ConfirmLoadDeviceRemap;
            InternalDataManagement.ItemCorrectionModeOnLoad = correctionMode;
            InternalDataManagement.ItemCorrectionAutoApplyScoreThresholdOnLoad = effectiveLoadCorrections ? correctionAutoThreshold : null;
            InternalDataManagement.ConfirmItemCorrectionsOnLoad = effectiveConfirmItemCorrections;
            InternalDataManagement.LoadItemCorrectionConfirmation = ConfirmLoadItemCorrection;

            JoyPro.Logging.JoyProLog.Info("Engine", "Command: " + area + " " + JoyPro.Logging.JoyProLog.JoinArguments(command) +
                                                    " | profile=" + (string.IsNullOrWhiteSpace(profilePath) ? "(none)" : profilePath + (File.Exists(profilePath) ? " (" + new FileInfo(profilePath).Length.ToString(CultureInfo.InvariantCulture) + " bytes)" : " (new)")) +
                                                    (string.IsNullOrWhiteSpace(outPath) ? string.Empty : " | out=" + outPath) +
                                                    " | text=" + textOutput + " | load-corrections=" + effectiveLoadCorrections +
                                                    (effectiveLoadCorrections ? " (auto-add=" + effectiveAutoAddDbItems + ", remap=" + effectiveRemapMissingDevices + ", mode=" + correctionMode + ")" : string.Empty) +
                                                    " | cwd=" + InvocationDirectory);

            EngineState state = new EngineState();
            Measure(
                "state.initialize",
                () =>
                {
                    state.Initialize(profilePath);
                    return 0;
                });
            WriteLoadCorrectionReportIfRequested();

            bool shouldAutoSaveProfile = ShouldAutoSaveProfile(profilePath, area, command);
            EngineProfileHistory.CommandLabel = EngineProfileHistory.DescribeCommand(area, command);
            // Before an import or export the profile is kept as a named version ("Before export to DCS").
            string historyCheckpoint = EngineProfileHistory.CheckpointLabel(area, command);
            if (historyCheckpoint != null && !string.IsNullOrWhiteSpace(profilePath) && File.Exists(profilePath))
                EngineProfileHistory.Checkpoint(profilePath, historyCheckpoint);
            GameBackups.Begin(BackupGame(area), "engine " + area + " " + string.Join(" ", command), command.Any(c => string.Equals(c, "--dry-run", StringComparison.OrdinalIgnoreCase)));
            // A DCS, IL-2 or Star Citizen run (import, export, scan, restore): its environment once in the log
            // (EngineDiagnostics; the other games log theirs in GameCommands with the path they were given).
            Games.IGameIntegration diagnosticsRegistryGame;
            if (BackupGame(area).Length > 0 && !Games.GameRegistry.TryResolve(area, out diagnosticsRegistryGame) &&
                command.Any(c => c == "import" || c == "export" || c == "scan" || c == "restore"))
            {
                try
                {
                    string ignoredName;
                    EngineDiagnostics.LogEnvironment(BackupGame(area), EngineSync.ConfigPathsOf(BackupGame(area), out ignoredName));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    JoyPro.Logging.JoyProLog.Debug("Diagnostics", "No environment of " + BackupGame(area) + ": " + ex.Message);
                }
            }
            int exitCode;
            string commandMetricName = BuildCommandMetricName(area, command);
            Status("Executing command: " + commandMetricName);

            // Virtual joystick mode: while a game with virtual mode is exported, imported or synced, its binds point at
            // the vJoy slots (EngineVJoy.GameScope, docs/virtual-joystick.md).
            EngineVJoy.GameScope vjoyScope = EngineVJoy.BeginGameCommand(BackupGame(area), area, command);
            if (!string.IsNullOrEmpty(vjoyScope.Error))
            {
                vjoyScope.Dispose();
                return Fail(vjoyScope.Error);
            }
            if (!string.IsNullOrEmpty(vjoyScope.Note))
                Status(vjoyScope.Note);

            try
            {
            switch (area)
            {
                case "vjoy":
                    exitCode = Measure("command.execute", () => RunVJoy(command, outPath, textOutput));
                    break;
                case "relations":
                    exitCode = Measure("command.execute", () => RunRelations(command, outPath, editor, textOutput));
                    break;
                case "binds":
                    exitCode = Measure("command.execute", () => RunBinds(command, outPath, editor, textOutput));
                    break;
                case "ffb":
                    exitCode = Measure("command.execute", () => RunFfb(command, outPath, textOutput));
                    break;
                case "telemetry":
                    exitCode = Measure("command.execute", () => RunTelemetry(command, outPath, textOutput));
                    break;
                case "profile":
                    exitCode = Measure("command.execute", () => RunProfile(command, outPath, textOutput));
                    break;
                case "db":
                    exitCode = Measure("command.execute", () => RunDb(command, outPath, textOutput));
                    break;
                case "devices":
                    exitCode = Measure("command.execute", () => RunDevices(command, outPath, textOutput));
                    break;
                case "dcs":
                    exitCode = Measure("command.execute", () => RunDcs(command, outPath, textOutput));
                    break;
                case "il2":
                    exitCode = Measure("command.execute", () => RunIl2(command, outPath, textOutput));
                    break;
                case "starcitizen":
                case "sc":
                    exitCode = Measure("command.execute", () => RunStarCitizen(command, outPath, textOutput));
                    break;
                case "backups":
                    exitCode = Measure("command.execute", () => RunBackups(command, outPath, textOutput));
                    break;
                case "media":
                    exitCode = Measure("command.execute", () => RunMedia(command, outPath, textOutput));
                    break;
                case "overlay":
                    exitCode = Measure("command.execute", () => RunOverlay(command, outPath, textOutput));
                    break;
                case "hud":
                    exitCode = Measure("command.execute", () => RunHud(command, outPath, textOutput));
                    break;
                case "games":
                    exitCode = Measure("command.execute", () => RunGames(command, outPath, textOutput));
                    break;
                case "layout":
                    exitCode = Measure("command.execute", () => RunLayout(command, outPath, textOutput));
                    break;
                case "sync":
                    exitCode = Measure("command.execute", () => RunSync(command, outPath, textOutput));
                    break;
                // engine support bundle [--game <id>] [--days N] [--list] --out <zip>: the support file (EngineSupportBundle).
                case "support":
                    exitCode = Measure("command.execute", () =>
                    {
                        string supportPayload;
                        int supportExit = EngineSupportBundle.Run(command, outPath, textOutput, out supportPayload);
                        if (supportExit != 0)
                            Console.Error.WriteLine(supportPayload);
                        else
                            Console.WriteLine(supportPayload);
                        return supportExit;
                    });
                    break;
                case "llm":
                    exitCode = Measure("command.execute", () => RunLlm(command, outPath, textOutput));
                    break;
                case "cockpits":
                    exitCode = Measure("command.execute", () => RunCockpits(command, outPath, textOutput));
                    break;
                case "setup":
                    exitCode = Measure("command.execute", () => RunSetup(command, outPath, textOutput));
                    break;
                // Also after global options: the GUI's start check runs "--status false --benchmark false version".
                case "version":
                case "--version":
                    Console.WriteLine(EngineVersion);
                    exitCode = 0;
                    break;
                case "summary":
                    exitCode = Measure(
                        "command.execute",
                        () =>
                        {
                            Console.WriteLine(Summary());
                            return 0;
                        });
                    break;
                default:
                    Games.IGameIntegration registryGame;
                    if (Games.GameRegistry.TryResolve(area, out registryGame))
                    {
                        exitCode = Measure("command.execute", () => RunRegistryGame(registryGame, command, outPath, textOutput));
                        break;
                    }
                    Console.Error.WriteLine("Unknown command area: " + area);
                    PrintHelp();
                    exitCode = 1;
                    break;
            }
            }
            finally
            {
                vjoyScope.Dispose();
            }
            // The files the run changed (stamps after writing); a failed run prints its environment and files.
            EngineDiagnostics.EndRun(exitCode);
            // Detent mode: games that could not express an aircraft's detent curve (the export used their normal curve).
            if (!string.Equals(area, "binds", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string warning in EngineAxisDetents.Warnings)
                    Console.WriteLine("Warning: " + warning);
            }

            // The export gave new inputs vJoy slots: they must be in the profile, or the next run could place them
            // elsewhere and the game's binds would move.
            if (exitCode == 0 && vjoyScope.MappingChanged && !command.Any(c => string.Equals(c, "--dry-run", StringComparison.OrdinalIgnoreCase)))
                shouldAutoSaveProfile = !string.IsNullOrWhiteSpace(profilePath);

            if (exitCode == 0 && shouldAutoSaveProfile && !CommandChangedNothing)
            {
                try
                {
                    Measure(
                        "profile.auto-save",
                        () =>
                        {
                            InternalDataManagement.SaveProfileTo(profilePath);
                            return 0;
                        });
                    Console.WriteLine("Auto-saved profile: " + profilePath);
                }
                catch (Exception ex)
                {
                    return Fail("Profile auto-save failed: " + ex.Message);
                }
            }

            totalRun.Stop();
            AddBenchmark("run.total", totalRun.ElapsedMilliseconds);
            if (BenchmarkEnabled)
                EmitBenchmarkSummary(commandMetricName, exitCode);
            return exitCode;
        }

        private static int RunRelations(List<string> command, string outPath, string editor, bool textOutput)
        {
            string action = command.Count == 0 ? "list" : command[0].ToLowerInvariant();
            switch (action)
            {
                case "list":
                    return EmitRelations(outPath, textOutput, command.Skip(1));
                case "show":
                    return ShowRelation(command, outPath, textOutput);
                case "add":
                    return AddRelation(command, outPath, textOutput);
                case "group":
                    return EditRelationGroups(command, outPath, textOutput);
                case "item":
                    return EditRelationItems(command, outPath, textOutput);
                case "mass":
                    return EditRelationMass(command, outPath, textOutput);
                case "export":
                    if (command.Count > 1)
                        outPath = NormalizePath(command[1]);
                    if (string.IsNullOrWhiteSpace(outPath))
                        return Fail("relations export requires a target file path.");
                    return EmitRelations(outPath, textOutput, command.Skip(2));
                case "import":
                    if (command.Count < 2)
                        return Fail("relations import requires a source file path.");
                    return ImportRelations(NormalizePath(command[1]));
                case "edit":
                    return EditRelations(editor);
                case "auto-group":
                    return AutoGroupRelations(command, outPath, textOutput);
                case "from-text":
                    return FromTextRelations(command, outPath, textOutput);
                case "auto-assign":
                    return AutoAssignRelations(command, outPath, textOutput);
                case "suggest":
                    return SuggestRelationItems(command, outPath, textOutput);
                case "search":
                    return SearchRelationActions(command, outPath, textOutput);
                case "browse":
                case "catalog":
                    return BrowseRelationActions(command, outPath, textOutput);
                case "functions":
                    return ListRelationFunctions(command, outPath, textOutput);
                case "resolve":
                    return ResolveRelationQuery(command, outPath, textOutput);
                case "chat":
                    return ChatRelation(command, outPath, textOutput);
                case "assistant":
                    return AssistantRelations(command, outPath, textOutput);
                case "plan":
                    return RelationPlanCommand(command, outPath, textOutput);
                case "rename":
                    return RenameRelationCommand(command, outPath, textOutput);
                case "function":
                    return RelationFunctionCommand(command, outPath, textOutput);
                case "aircraft":
                    return RelationNodeAircraft(command, outPath, textOutput);
                case "check-aircraft":
                    return CheckRelationAircraft(command, outPath, textOutput);
                case "meanings":
                    {
                        // relations meanings [--game g] [--top N]: ids with another meaning in some aircraft, per game.
                        List<string> args = command.Skip(1).ToList();
                        string game = PopOptionValue(args, "--game");
                        string topRaw = PopOptionValue(args, "--top");
                        int top;
                        if (!int.TryParse(topRaw ?? "20", NumberStyles.Integer, CultureInfo.InvariantCulture, out top))
                            return Fail("--top needs a number.");
                        if (args.Count > 0)
                            return Fail("Unknown relations meanings argument(s): " + string.Join(" ", args));
                        EngineMeaningStats stats = EngineIdMeanings.Stats(game, top);
                        EmitPayload(textOutput ? EngineIdMeanings.ToText(stats) : EngineSerialization.ToJson(stats), outPath);
                        return 0;
                    }
                case "action-info":
                    {
                        // relations action-info --coverage [--game g]: per game readable names, explanations and
                        // recommended actions (EngineActionInfo, EngineActionChoice).
                        List<string> args = command.Skip(1).ToList();
                        bool coverage = PopFlag(args, "--coverage");
                        string game = PopOptionValue(args, "--game");
                        if (!coverage || args.Count > 0)
                            return Fail("relations action-info --coverage [--game g]");
                        EngineActionCoverage result = EngineActionInfo.Coverage(game);
                        EmitPayload(textOutput ? EngineActionInfo.ToText(result) : EngineSerialization.ToJson(result), outPath);
                        return 0;
                    }
                default:
                    return Fail("Unknown relations action: " + action);
            }
        }

        private static int RunBinds(List<string> command, string outPath, string editor, bool textOutput)
        {
            string action = command.Count == 0 ? "list" : command[0].ToLowerInvariant();
            switch (action)
            {
                case "list":
                    return EmitBinds(outPath, textOutput, command.Skip(1));
                case "show":
                    return ShowBind(command, outPath, textOutput);
                case "set":
                    return SetBind(command, outPath, textOutput);
                case "clear":
                    return ClearBind(command, outPath, textOutput);
                case "tuning":
                    return RunBindTuning(command, outPath, textOutput);
                case "detent":
                    return RunBindDetent(command, outPath, textOutput);
                case "modifier":
                    return EditBindModifiers(command, outPath, textOutput);
                case "unbind":
                    return UnbindBindInputs(command, outPath, textOutput);
                case "export":
                    if (command.Count > 1)
                        outPath = NormalizePath(command[1]);
                    if (string.IsNullOrWhiteSpace(outPath))
                        return Fail("binds export requires a target file path.");
                    return EmitBinds(outPath, textOutput, command.Skip(2));
                case "import":
                    if (command.Count < 2)
                        return Fail("binds import requires a source file path.");
                    return ImportBinds(NormalizePath(command[1]));
                case "edit":
                    return EditBinds(editor);
                default:
                    return Fail("Unknown binds action: " + action);
            }
        }

        private static int RunProfile(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "summary" : command[0].ToLowerInvariant();
            switch (action)
            {
                case "summary":
                    Console.WriteLine(Summary());
                    return 0;
                case "load":
                    if (command.Count < 2)
                        return Fail("profile load requires a .pr0file path.");
                    string sourcePath = NormalizePath(command[1]);
                    InternalDataManagement.LoadProfile(sourcePath);
                    WriteLoadCorrectionReportIfRequested();
                    Console.WriteLine("Loaded profile: " + sourcePath);
                    Console.WriteLine(Summary());
                    return 0;
                case "save":
                    if (command.Count < 2)
                        return Fail("profile save requires a target .pr0file path.");
                    string targetPath = NormalizePath(command[1]);
                    InternalDataManagement.SaveProfileTo(targetPath);
                    Console.WriteLine("Saved profile: " + targetPath);
                    return 0;
                case "new":
                    {
                        if (command.Count < 2)
                            return Fail("profile new requires a target .pr0file path.");
                        string newPath = NormalizePath(command[1]);
                        string newDirectory = Path.GetDirectoryName(newPath);
                        if (!string.IsNullOrWhiteSpace(newDirectory))
                            Directory.CreateDirectory(newDirectory);
                        InternalDataManagement.NewFile(true);
                        InternalDataManagement.SaveProfileTo(newPath);
                        Console.WriteLine("Created empty profile: " + newPath);
                        return 0;
                    }
                case "save-last":
                    MainStructure.SaveMetaLast();
                    Console.WriteLine("Saved to last profile location.");
                    return 0;
                case "validate":
                    {
                        List<string> args = command.Skip(1).ToList();
                        bool failOnWarnings = PopOptionBoolean(args, "--fail-on-warnings", false, out _);
                        if (args.Count > 0)
                            return Fail("Unknown profile validate argument(s): " + string.Join(" ", args));

                        ProfileValidationResult result = ValidateCurrentProfile();
                        result.FailOnWarnings = failOnWarnings;
                        string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
                        EmitPayload(payload, outPath);

                        if (result.ErrorCount > 0)
                            return Fail("profile validation failed: errors = " + result.ErrorCount.ToString(CultureInfo.InvariantCulture) + ".");
                        if (failOnWarnings && result.WarningCount > 0)
                        {
                            return Fail(
                                "profile validation failed: warnings = " +
                                result.WarningCount.ToString(CultureInfo.InvariantCulture) +
                                " (--fail-on-warnings true).");
                        }

                        return 0;
                    }
                case "merge":
                    return RunProfileMerge(command.Skip(1).ToList());
                case "history":
                    // The profile's versions (docs/profile-history.md).
                    return RunProfileHistory(command.Skip(1).ToList(), outPath, textOutput);
                case "games":
                    {
                        // What an export to each game writes: the games (DCS: aircraft) with relations, and how many are bound.
                        EngineProfileGamesResult games = EngineProfileGames.Build();
                        EmitPayload(textOutput ? EngineProfileGames.ToText(games) : EngineSerialization.ToJson(games), outPath);
                        return 0;
                    }
                default:
                    return Fail("Unknown profile action: " + action);
            }
        }

        private static int RunProfileMerge(List<string> args)
        {
            List<string> options = args == null
                ? new List<string>()
                : new List<string>(args);

            string modeRaw = PopOptionValue(options, "--mode");
            string overwriteRaw = PopOptionValue(options, "--overwrite-mode");
            if (!string.IsNullOrWhiteSpace(overwriteRaw))
            {
                if (!string.IsNullOrWhiteSpace(modeRaw))
                    return Fail("profile merge uses either --mode or --overwrite-mode, not both.");
                modeRaw = overwriteRaw;
            }

            string savePath = PopOptionValue(options, "--save");
            if (string.IsNullOrWhiteSpace(savePath))
                savePath = PopOptionValue(options, "--save-profile");

            if (!TryParseProfileMergeMode(modeRaw, out ProfileMergeMode mode, out string modeError))
                return Fail(modeError);

            if (mode == ProfileMergeMode.Ask &&
                (!Environment.UserInteractive || Console.IsInputRedirected))
            {
                return Fail("profile merge --mode ask requires interactive console input.");
            }

            List<string> unknown = options
                .Where(v => !string.IsNullOrWhiteSpace(v) && v.StartsWith("--", StringComparison.Ordinal))
                .ToList();
            if (unknown.Count > 0)
                return Fail("Unknown profile merge argument(s): " + string.Join(" ", unknown));

            List<string> sources = options
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => NormalizePath(v))
                .ToList();

            if (sources.Count < 2)
            {
                return Fail(
                    "profile merge requires at least two source files: " +
                    "profile merge <left.pr0file> <right.pr0file> [<more.pr0file> ...].");
            }

            for (int i = 0; i < sources.Count; i++)
            {
                if (!File.Exists(sources[i]))
                    return Fail("profile merge source does not exist: " + sources[i]);
            }

            ProfileMergeData merged = NormalizeProfileForMerge(LoadProfileDocument(sources[0]));
            ProfileMergeResult result = new ProfileMergeResult
            {
                Mode = mode.ToString().ToLowerInvariant(),
                Sources = new List<string>(sources)
            };

            for (int i = 1; i < sources.Count; i++)
            {
                ProfileMergeData incoming = NormalizeProfileForMerge(LoadProfileDocument(sources[i]));
                MergeProfileData(merged, incoming, mode, result, sources[i]);
            }

            ApplyMergedProfileData(merged);

            Console.WriteLine("Merged profiles: " + sources.Count);
            Console.WriteLine("Conflict mode: " + result.Mode);
            Console.WriteLine("Conflicts: " + result.Conflicts);
            Console.WriteLine("Resolved Left: " + result.ResolvedLeft);
            Console.WriteLine("Resolved Right: " + result.ResolvedRight);
            Console.WriteLine(Summary());

            string normalizedSavePath = NormalizePath(savePath);
            if (!string.IsNullOrWhiteSpace(normalizedSavePath))
            {
                InternalDataManagement.SaveProfileTo(normalizedSavePath);
                Console.WriteLine("Saved merged profile: " + normalizedSavePath);
            }

            return 0;
        }

        private static int RunDb(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "summary" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            if (action == "refresh-starcitizen" || action == "refresh-sc" || action == "starcitizen-refresh")
            {
                string refusal = RefuseGameAction("StarCitizen", "refresh-db");
                return refusal != null ? Fail(refusal) : RunDbRefreshStarCitizen(args, outPath, textOutput);
            }
            if (action == "refresh-il2" || action == "il2-refresh")
                return RunDbRefreshIl2(args, outPath, textOutput);
            if (action == "check-updates")
                return RunDbCheckUpdates(args, outPath, textOutput);
            if (action == "paths")
                return RunDbPaths(args, outPath, textOutput);

            bool keyboardRequested = PopFlag(args, "--keyboard");
            bool noKeyboardRequested = PopFlag(args, "--no-keyboard");
            bool includeKeyboard = MainStructure.loadKeyboard || keyboardRequested;
            if (noKeyboardRequested)
                includeKeyboard = false;
            bool onlyMissing = PopFlag(args, "--missing");
            bool strictNames = PopFlag(args, "--strict-names");
            string gameFilter = PopOptionValue(args, "--game");
            string query = JoinQuery(args);

            if (!string.IsNullOrWhiteSpace(gameFilter) &&
                !action.Equals("planes", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("--game is only valid with 'db planes'.");
            }

            bool hasDcsCheckOptions = keyboardRequested || noKeyboardRequested || onlyMissing || strictNames;
            bool isDcsCheckAction =
                action.Equals("summary", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                action.Equals("check", StringComparison.OrdinalIgnoreCase);

            if (hasDcsCheckOptions && !isDcsCheckAction)
            {
                return Fail("--keyboard, --no-keyboard, --missing, and --strict-names are only valid with 'db summary', 'db list', or 'db check'.");
            }

            switch (action)
            {
                case "summary":
                    {
                    EngineDbDocument source = EngineDatabase.InspectDcsCleanProfiles(MainStructure.PROGPATH, includeKeyboard);
                    EngineDbDocument selected = string.IsNullOrWhiteSpace(query)
                        ? source
                        : EngineDatabase.Filter(source, query);
                    EmitPayload(textOutput ? EngineDatabase.ToTextSummary(selected) : EngineSerialization.ToJson(selected), outPath);
                    return 0;
                    }
                case "list":
                    {
                    EngineDbDocument source = EngineDatabase.InspectDcsCleanProfiles(MainStructure.PROGPATH, includeKeyboard);
                    EngineDbDocument selected = string.IsNullOrWhiteSpace(query)
                        ? source
                        : EngineDatabase.Filter(source, query);
                    EmitPayload(textOutput ? EngineDatabase.ToTextList(selected, onlyMissing) : EngineSerialization.ToJson(selected), outPath);
                    return 0;
                    }
                case "check":
                    {
                        EngineDbDocument source = EngineDatabase.InspectDcsCleanProfiles(MainStructure.PROGPATH, includeKeyboard);
                        EngineDbDocument selected = string.IsNullOrWhiteSpace(query)
                            ? source
                            : EngineDatabase.Filter(source, query);

                        string payload = textOutput
                            ? EngineDatabase.ToTextSummary(selected) + Environment.NewLine + EngineDatabase.ToTextList(selected, true)
                            : EngineSerialization.ToJson(selected);
                        EmitPayload(payload, outPath);

                        if (selected.Errors != null && selected.Errors.Count > 0)
                            return Fail("db check failed: " + string.Join(" | ", selected.Errors));

                        int missing = EngineDatabase.MissingCleanCount(selected) + EngineDatabase.MissingKeyboardCleanCount(selected);
                        if (missing > 0)
                            return Fail("db check failed: missing clean profile files = " + missing);
                        if (strictNames)
                        {
                            int aliasMismatches =
                                EngineDatabase.AliasMatchedCleanCount(selected) +
                                EngineDatabase.AliasMatchedKeyboardCleanCount(selected);
                            if (aliasMismatches > 0)
                                return Fail("db check failed: clean file name mismatches = " + aliasMismatches + " (alias-matched).");
                        }

                        Console.WriteLine("db check ok");
                        return 0;
                    }
                case "games":
                    {
                        EngineDbCatalogDocument source = EngineDatabase.InspectCatalog(MainStructure.PROGPATH);
                        EngineDbCatalogDocument selected = string.IsNullOrWhiteSpace(query)
                            ? source
                            : EngineDatabase.FilterCatalog(source, query, null);
                        EmitPayload(textOutput ? EngineDatabase.ToTextGames(selected) : EngineSerialization.ToJson(selected), outPath);
                        return 0;
                    }
                case "planes":
                    {
                        EngineDbCatalogDocument source = EngineDatabase.InspectCatalog(MainStructure.PROGPATH);
                        EngineDbCatalogDocument selected = EngineDatabase.FilterCatalog(source, gameFilter, query);
                        EmitPayload(textOutput ? EngineDatabase.ToTextPlanes(selected) : EngineSerialization.ToJson(selected), outPath);
                        return 0;
                    }
                case "fingerprint":
                    {
                        CoreDbFingerprint.FingerprintResult fingerprint = CoreDbFingerprint.Compute(MainStructure.PROGPATH);
                        if (textOutput)
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine("DB Fingerprint: " + (fingerprint.Fingerprint ?? string.Empty));
                            sb.AppendLine("Files: " + fingerprint.FileCount.ToString(CultureInfo.InvariantCulture));
                            sb.AppendLine("Bytes: " + fingerprint.TotalBytes.ToString(CultureInfo.InvariantCulture));
                            sb.AppendLine("Roots: " + (fingerprint.IncludedRoots == null ? 0 : fingerprint.IncludedRoots.Count).ToString(CultureInfo.InvariantCulture));
                            if (fingerprint.IncludedRoots != null)
                            {
                                for (int i = 0; i < fingerprint.IncludedRoots.Count; i++)
                                {
                                    sb.AppendLine("  - " + (fingerprint.IncludedRoots[i] ?? string.Empty));
                                }
                            }
                            if (fingerprint.Warnings != null && fingerprint.Warnings.Count > 0)
                            {
                                sb.AppendLine("Warnings: " + fingerprint.Warnings.Count.ToString(CultureInfo.InvariantCulture));
                                for (int i = 0; i < fingerprint.Warnings.Count; i++)
                                    sb.AppendLine("  - " + (fingerprint.Warnings[i] ?? string.Empty));
                            }
                            else
                            {
                                sb.AppendLine("Warnings: 0");
                            }

                            EmitPayload(sb.ToString().TrimEnd(), outPath);
                        }
                        else
                        {
                            EmitPayload(
                                EngineSerialization.ToJson(
                                    new
                                    {
                                        fingerprint = fingerprint.Fingerprint ?? string.Empty,
                                        fileCount = fingerprint.FileCount,
                                        totalBytes = fingerprint.TotalBytes,
                                        includedRoots = fingerprint.IncludedRoots ?? new List<string>(),
                                        warnings = fingerprint.Warnings ?? new List<string>()
                                    }),
                                outPath);
                        }
                        return 0;
                    }
                default:
                    return Fail("Unknown db action: " + action);
            }
        }

        // engine db paths: the program folder (JoyPro's own data), the game-data folder (lists built from the user's
        // games) and, per game folder, where JoyPro reads it (GameDataPaths, docs/installs.md "Game data").
        private static int RunDbPaths(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count > 0)
                return Fail("Unknown db paths argument(s): " + string.Join(" ", args));

            var folders = new List<object>();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DB Paths");
            sb.AppendLine("Program folder: " + GameDataPaths.ProgramRoot() + "   (JoyPro's own data: DB\\Functions, DB\\Devices, DB\\Llm, DB\\DevicePhotos, DB\\DeviceLayouts, TOOLS)");
            sb.AppendLine("Game data: " + GameDataPaths.UserRoot() + "   (built from your games: DB\\<game>, CleanProfile, KeyboardCleanProfile)");
            List<string> relative = GameDataPaths.GameDbFolders().Select(f => Path.Combine("DB", f.Key)).ToList();
            foreach (string top in new[] { "CleanProfile", "KeyboardCleanProfile" })
            {
                foreach (string root in new[] { GameDataPaths.UserRoot(), GameDataPaths.ProgramRoot() })
                {
                    string folder = Path.Combine(root, top);
                    if (Directory.Exists(folder))
                        relative.AddRange(Directory.GetDirectories(folder).Select(d => Path.Combine(top, Path.GetFileName(d))));
                }
            }
            foreach (string folder in relative.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string source = GameDataPaths.SourceOf(folder);
                string path = Path.Combine(source == "program" ? GameDataPaths.ProgramRoot() : GameDataPaths.UserRoot(), folder);
                string stamp = string.Empty;
                Dictionary<string, string> fields = GameDataPaths.ReadStampFields(Path.Combine(path, GameDataPaths.StampFile));
                if (fields.ContainsKey("version"))
                    stamp = fields["version"];
                else if (fields.ContainsKey("steam-build"))
                    stamp = "steam-build " + fields["steam-build"];
                folders.Add(new { Folder = folder, Source = source == "user" ? "game-data" : source, Path = path, Build = stamp });
                sb.AppendLine("  " + folder + " | " + (source == "user" ? "game-data" : source) + (stamp.Length > 0 ? " | " + stamp : string.Empty) + " | " + path);
            }
            EmitPayload(textOutput
                ? sb.ToString().TrimEnd()
                : EngineSerialization.ToJson(new { ProgramFolder = GameDataPaths.ProgramRoot(), GameDataFolder = GameDataPaths.UserRoot(), Folders = folders }), outPath);
            return 0;
        }

        private static int RunDbCheckUpdates(List<string> args, string outPath, bool textOutput)
        {
            List<string> accept = PopOptionValues(args, "--accept");
            bool acceptAll = PopFlag(args, "--accept-all");
            bool noRecord = PopFlag(args, "--no-record");
            if (args.Count > 0)
                return Fail("Unknown db check-updates argument(s): " + string.Join(" ", args));

            DbUpdateReport report = noRecord
                ? DbUpdateCheck.CheckWithoutRecording()
                : DbUpdateCheck.Check(ParseDelimitedOptionValues(accept), acceptAll);
            EmitPayload(textOutput ? DbUpdateCheck.ToText(report) : EngineSerialization.ToJson(report), outPath);
            return 0;
        }

        private static int RunDbRefreshStarCitizen(List<string> args, string outPath, bool textOutput)
        {
            List<string> options = args == null
                ? new List<string>()
                : new List<string>(args);

            string sourcePath = NormalizePath(PopOptionValue(options, "--source"));
            string pathOption = NormalizePath(PopOptionValue(options, "--path"));
            string installPathOption = NormalizePath(PopOptionValue(options, "--install-path"));
            string branchOption = PopOptionValue(options, "--branch");
            string dbPathOption = NormalizePath(PopOptionValue(options, "--db-path"));
            bool includeUserProfiles = PopOptionBoolean(options, "--include-user-profiles", true, out _);
            bool dryRun = PopOptionBoolean(options, "--dry-run", false, out _);

            string positionalPath = null;
            if (options.Count > 0 && !options[0].StartsWith("--", StringComparison.Ordinal))
            {
                positionalPath = NormalizePath(options[0]);
                options.RemoveAt(0);
            }

            if (options.Count > 0)
                return Fail("Unknown db refresh-starcitizen argument(s): " + string.Join(" ", options));

            if (string.IsNullOrWhiteSpace(sourcePath) &&
                !string.IsNullOrWhiteSpace(positionalPath) &&
                File.Exists(positionalPath))
            {
                sourcePath = positionalPath;
            }

            string installPath = string.IsNullOrWhiteSpace(installPathOption)
                ? pathOption
                : installPathOption;
            if (string.IsNullOrWhiteSpace(installPath))
                installPath = positionalPath;

            StarCitizenDbRefreshResult result = RefreshStarCitizenDb(
                sourcePath,
                installPath,
                branchOption,
                dbPathOption,
                includeUserProfiles,
                dryRun);

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);

            if (!result.Success)
            {
                string message = string.IsNullOrWhiteSpace(result.Error)
                    ? "db refresh-starcitizen failed."
                    : "db refresh-starcitizen failed: " + result.Error;
                return Fail(message);
            }

            if (!dryRun)
                DbUpdateCheck.RecordCurrent("StarCitizen", null);
            return 0;
        }

        // Star Citizen scan, import and export on a fresh install (no command list yet; release packages ship none):
        // the list is built from the installed game first, as db refresh-starcitizen does. True when it was built.
        internal static bool BuildStarCitizenDbOnDemand(List<string> warnings)
        {
            if (Environment.GetEnvironmentVariable(JoyPro.Engine.Games.GameCommands.NoCatalogBuildVariable) == "1")
                return false;
            JoyPro.Logging.JoyProLog.Info("Catalog", "StarCitizen: no command list; building it from the game.");
            StarCitizenDbRefreshResult result = RefreshStarCitizenDb(null, null, null, null, true, false);
            if (!result.Success)
            {
                warnings?.Add("JoyPro has no Star Citizen command list yet, and building it from the installed game did not work: " +
                              (string.IsNullOrWhiteSpace(result.Error) ? "no action map found." : result.Error) + " Build it with Games > DB refresh (engine db refresh-starcitizen).");
                return false;
            }
            DbUpdateCheck.RecordCurrent("StarCitizen", null);
            warnings?.Add("JoyPro had no Star Citizen command list yet: built it from the installed game (" + result.ActionCount.ToString(CultureInfo.InvariantCulture) +
                          " actions, " + result.DbPath + ").");
            return true;
        }

        private static StarCitizenDbRefreshResult RefreshStarCitizenDb(
            string sourcePath,
            string installPath,
            string branch,
            string dbPath,
            bool includeUserProfiles,
            bool dryRun)
        {
            StarCitizenDbRefreshResult result = new StarCitizenDbRefreshResult();
            result.DryRun = dryRun;
            result.IncludeUserProfiles = includeUserProfiles;
            result.RequestedSourcePath = sourcePath ?? string.Empty;
            result.RequestedInstallPath = installPath ?? string.Empty;
            result.RequestedBranch = branch ?? string.Empty;

            // JoyPro's own list lives in the game-data folder (GameDataPaths); a dry run only reads.
            string resolvedDbPath = !string.IsNullOrWhiteSpace(dbPath) ? dbPath
                : dryRun ? GameDataPaths.DbFolder("StarCitizen")
                : GameDataPaths.WritableDbFolder("StarCitizen");
            resolvedDbPath = NormalizePath(resolvedDbPath);
            result.DbPath = resolvedDbPath;

            // Nothing is written until the DB files at the end: the user may cancel until then (EngineProgress).
            EngineProgress.SetCancelable(true);
            EngineProgress.Report(1, 1, "Star Citizen, looking for the game's action maps", installPath ?? sourcePath ?? string.Empty);
            List<string> discoveryWarnings = new List<string>();
            List<StarCitizenSourceCandidate> candidates = DiscoverStarCitizenActionMapCandidates(
                sourcePath,
                installPath,
                branch,
                includeUserProfiles,
                discoveryWarnings);

            result.SourceCandidates = candidates
                .Select(GetStarCitizenCandidateDisplayPath)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < discoveryWarnings.Count; i++)
                AddUnique(result.Warnings, discoveryWarnings[i]);

            if (candidates.Count == 0)
            {
                result.Error = "No Star Citizen action map source file found. Try --source <actionmaps.xml> or --install-path <StarCitizen root>.";
                return result;
            }

            List<StarCitizenParsedCatalog> parsedCatalogs = new List<StarCitizenParsedCatalog>();
            for (int i = 0; i < candidates.Count; i++)
            {
                StarCitizenSourceCandidate candidate = candidates[i];
                EngineProgress.ThrowIfCancelled("reading Star Citizen's action maps");
                EngineProgress.Report(i + 1, candidates.Count, "Star Citizen, reading the action maps", GetStarCitizenCandidateDisplayPath(candidate));
                if (!TryParseStarCitizenActionCatalog(candidate, out StarCitizenParsedCatalog parsed, out string parseError))
                {
                    if (!string.IsNullOrWhiteSpace(parseError))
                        AddUnique(result.Warnings, parseError);
                    continue;
                }
                parsedCatalogs.Add(parsed);
            }

            if (parsedCatalogs.Count == 0)
            {
                result.Error = "Found source candidates, but none could be parsed into actions.";
                return result;
            }

            StarCitizenParsedCatalog selected = parsedCatalogs
                .OrderByDescending(v => v.ActionCount)
                .ThenBy(v => v.CandidatePriority)
                .ThenBy(v => v.SourceFile, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (selected == null || selected.Actions.Count == 0)
            {
                result.Error = "Selected Star Citizen source has no actions.";
                return result;
            }

            // Option groups and the joystick option tree come from the game's defaultProfile.xml; when another
            // candidate (a user profile) was selected, take them from the source that has them.
            XElement optionTree = selected.OptionTree ?? parsedCatalogs.Select(v => v.OptionTree).FirstOrDefault(v => v != null);
            Dictionary<string, string> groupByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (StarCitizenActionEntry action in parsedCatalogs.SelectMany(v => v.Actions).Where(v => !string.IsNullOrWhiteSpace(v.OptionGroup)))
            {
                string groupKey = action.Category + "___" + action.Id;
                if (!groupByKey.ContainsKey(groupKey))
                    groupByKey[groupKey] = action.OptionGroup;
            }
            foreach (StarCitizenActionEntry action in selected.Actions.Where(v => string.IsNullOrWhiteSpace(v.OptionGroup)))
            {
                string group;
                if (groupByKey.TryGetValue(action.Category + "___" + action.Id, out group))
                    action.OptionGroup = group;
            }
            Dictionary<string, string> defaultJoystickByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (StarCitizenActionEntry action in parsedCatalogs.SelectMany(v => v.Actions).Where(v => !string.IsNullOrWhiteSpace(v.DefaultJoystick)))
            {
                string defaultKey = action.Category + "___" + action.Id;
                if (!defaultJoystickByKey.ContainsKey(defaultKey))
                    defaultJoystickByKey[defaultKey] = action.DefaultJoystick;
            }
            foreach (StarCitizenActionEntry action in selected.Actions.Where(v => string.IsNullOrWhiteSpace(v.DefaultJoystick)))
            {
                string defaultInput;
                if (defaultJoystickByKey.TryGetValue(action.Category + "___" + action.Id, out defaultInput))
                    action.DefaultJoystick = defaultInput;
            }
            result.OptionGroupActions = selected.Actions.Count(v => !string.IsNullOrWhiteSpace(v.OptionGroup));
            result.OptionTreeGroups = optionTree == null ? 0 : optionTree.Descendants().Count(e => string.Equals(e.Name.LocalName, "optiongroup", StringComparison.OrdinalIgnoreCase));
            if (optionTree == null)
                AddUnique(result.Warnings, "No joystick option tree found (defaultProfile.xml); Star Citizen axis settings use built-in defaults.");

            result.Success = true;
            result.SourceFile = selected.SourceFile;
            result.InstallPath = selected.InstallPath;
            result.Branch = selected.Branch;
            result.ActionMapCount = selected.ActionMapCount;
            result.ActionCount = selected.ActionCount;
            result.AxisCount = selected.AxisCount;
            result.ButtonCount = selected.ButtonCount;
            result.DuplicateActionsSkipped = selected.DuplicateActionsSkipped;

            string actionmapsOut = Path.Combine(resolvedDbPath ?? string.Empty, "actionmaps.xml");
            string allBindsOut = Path.Combine(resolvedDbPath ?? string.Empty, "AllBinds.xml");
            result.ActionmapsOutputFile = actionmapsOut;
            result.AllBindsOutputFile = allBindsOut;

            if (dryRun)
                return result;

            EngineProgress.ThrowIfCancelled("reading Star Citizen's action maps");
            EngineProgress.SetCancelable(false);
            try
            {
                if (string.IsNullOrWhiteSpace(resolvedDbPath))
                    throw new InvalidOperationException("Resolved DB path is empty.");
                Directory.CreateDirectory(resolvedDbPath);

                EngineProgress.Report(1, 2, "Star Citizen, writing the command list", Path.GetFileName(actionmapsOut));
                WriteStarCitizenActionCatalogXml(actionmapsOut, selected.Actions, optionTree);
                EngineProgress.Report(2, 2, "Star Citizen, writing the command list", Path.GetFileName(allBindsOut));
                WriteStarCitizenActionCatalogXml(allBindsOut, selected.Actions, optionTree);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = "Could not write Star Citizen DB files: " + ex.Message;
            }

            return result;
        }

        private static List<StarCitizenSourceCandidate> DiscoverStarCitizenActionMapCandidates(
            string sourcePath,
            string installPath,
            string branch,
            bool includeUserProfiles,
            List<string> warnings)
        {
            List<StarCitizenSourceCandidate> output = new List<StarCitizenSourceCandidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string branchFilter = (branch ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                AddStarCitizenCandidatesFromPath(
                    output,
                    seen,
                    sourcePath,
                    branchFilter,
                    includeUserProfiles,
                    0,
                    "explicit source",
                    warnings);
            }

            if (!string.IsNullOrWhiteSpace(installPath))
            {
                AddStarCitizenCandidatesFromPath(
                    output,
                    seen,
                    installPath,
                    branchFilter,
                    includeUserProfiles,
                    50,
                    "explicit install/path",
                    warnings);
            }
            else
            {
                List<string> autoWarnings = new List<string>();
                List<string> discoveredInstalls = GamePathDiscovery.DiscoverStarCitizenInstallPaths(autoWarnings);
                for (int i = 0; i < autoWarnings.Count; i++)
                    AddUnique(warnings, autoWarnings[i]);

                for (int i = 0; i < discoveredInstalls.Count; i++)
                {
                    AddStarCitizenCandidatesFromPath(
                        output,
                        seen,
                        discoveredInstalls[i],
                        branchFilter,
                        includeUserProfiles,
                        100 + (i * 20),
                        "auto-discovered install",
                        warnings);
                }

                if (includeUserProfiles)
                {
                    autoWarnings.Clear();
                    List<string> discoveredConfigs = GamePathDiscovery.DiscoverStarCitizenConfigPaths(autoWarnings);
                    for (int i = 0; i < autoWarnings.Count; i++)
                        AddUnique(warnings, autoWarnings[i]);

                    for (int i = 0; i < discoveredConfigs.Count; i++)
                    {
                        string configRoot = NormalizePath(discoveredConfigs[i]);
                        if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
                            continue;

                        string configActionMaps = Path.Combine(configRoot, "actionmaps.xml");
                        if (!File.Exists(configActionMaps))
                            continue;

                        string candidateBranch = DetectStarCitizenBranchFromPath(configActionMaps);
                        if (!string.IsNullOrWhiteSpace(branchFilter) &&
                            !string.Equals(candidateBranch, branchFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string candidateInstall = DetectStarCitizenInstallFromSourcePath(configActionMaps, candidateBranch);
                        AddStarCitizenCandidate(
                            output,
                            seen,
                            configActionMaps,
                            candidateInstall,
                            candidateBranch,
                            500 + i,
                            "auto-discovered config",
                            warnings);
                    }
                }
            }

            return output
                .OrderBy(v => v.Priority)
                .ThenBy(v => GetStarCitizenCandidateDisplayPath(v), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddStarCitizenCandidatesFromPath(
            List<StarCitizenSourceCandidate> output,
            HashSet<string> seen,
            string path,
            string branchFilter,
            bool includeUserProfiles,
            int basePriority,
            string reason,
            List<string> warnings)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (File.Exists(normalized))
            {
                string candidateBranch = DetectStarCitizenBranchFromPath(normalized);
                if (!string.IsNullOrWhiteSpace(branchFilter) &&
                    !string.Equals(candidateBranch, branchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string candidateInstall = DetectStarCitizenInstallFromSourcePath(normalized, candidateBranch);
                AddStarCitizenCandidate(output, seen, normalized, candidateInstall, candidateBranch, basePriority, reason, warnings);
                return;
            }

            if (!Directory.Exists(normalized))
            {
                AddUnique(warnings, "Star Citizen candidate path does not exist: " + normalized);
                return;
            }

            string directActionMaps = Path.Combine(normalized, "actionmaps.xml");
            if (File.Exists(directActionMaps))
            {
                string directBranch = DetectStarCitizenBranchFromPath(directActionMaps);
                if (string.IsNullOrWhiteSpace(branchFilter) ||
                    string.Equals(directBranch, branchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    string directInstall = DetectStarCitizenInstallFromSourcePath(directActionMaps, directBranch);
                    AddStarCitizenCandidate(
                        output,
                        seen,
                        directActionMaps,
                        directInstall,
                        directBranch,
                        basePriority,
                        reason + " (direct actionmaps.xml)",
                        warnings);
                }
            }

            if (LooksLikeStarCitizenBranchDirectory(normalized))
            {
                string candidateInstall = string.Empty;
                try
                {
                    DirectoryInfo branchInfo = new DirectoryInfo(normalized);
                    candidateInstall = branchInfo.Parent == null ? string.Empty : NormalizePath(branchInfo.Parent.FullName);
                }
                catch
                {
                    candidateInstall = string.Empty;
                }

                string candidateBranch = Path.GetFileName(normalized) ?? string.Empty;
                AddStarCitizenCandidatesFromBranchDirectory(
                    output,
                    seen,
                    normalized,
                    candidateInstall,
                    candidateBranch,
                    branchFilter,
                    includeUserProfiles,
                    basePriority,
                    reason,
                    warnings);
                return;
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(normalized, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "Could not enumerate Star Citizen path '" + normalized + "': " + ex.Message);
                return;
            }

            for (int i = 0; i < childDirectories.Length; i++)
            {
                string child = NormalizePath(childDirectories[i]);
                if (string.IsNullOrWhiteSpace(child) || !LooksLikeStarCitizenBranchDirectory(child))
                    continue;

                string candidateBranch = Path.GetFileName(child) ?? string.Empty;
                AddStarCitizenCandidatesFromBranchDirectory(
                    output,
                    seen,
                    child,
                    normalized,
                    candidateBranch,
                    branchFilter,
                    includeUserProfiles,
                    basePriority + (i * 5),
                    reason + " (branch)",
                    warnings);
            }
        }

        private static void AddStarCitizenCandidatesFromBranchDirectory(
            List<StarCitizenSourceCandidate> output,
            HashSet<string> seen,
            string branchDirectory,
            string installDirectory,
            string branch,
            string branchFilter,
            bool includeUserProfiles,
            int basePriority,
            string reason,
            List<string> warnings)
        {
            string normalizedBranchDir = NormalizePath(branchDirectory);
            if (string.IsNullOrWhiteSpace(normalizedBranchDir) || !Directory.Exists(normalizedBranchDir))
                return;

            string branchName = string.IsNullOrWhiteSpace(branch)
                ? (Path.GetFileName(normalizedBranchDir) ?? string.Empty)
                : branch.Trim();
            if (!string.IsNullOrWhiteSpace(branchFilter) &&
                !string.Equals(branchName, branchFilter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string normalizedInstall = NormalizePath(installDirectory);
            AddStarCitizenCandidate(
                output,
                seen,
                Path.Combine(normalizedBranchDir, "Data", "Libs", "Config", "defaultProfile.xml"),
                normalizedInstall,
                branchName,
                basePriority + 0,
                reason + " defaultProfile.xml",
                warnings);
            AddStarCitizenCandidate(
                output,
                seen,
                Path.Combine(normalizedBranchDir, "Data", "Libs", "Config", "defaultProfile", "actionmaps.xml"),
                normalizedInstall,
                branchName,
                basePriority + 1,
                reason + " defaultProfile",
                warnings);
            AddStarCitizenCandidate(
                output,
                seen,
                Path.Combine(normalizedBranchDir, "Data", "Libs", "Config", "profiles", "default", "actionmaps.xml"),
                normalizedInstall,
                branchName,
                basePriority + 2,
                reason + " profiles/default",
                warnings);
            AddStarCitizenCandidatesFromDataP4k(
                output,
                seen,
                normalizedBranchDir,
                normalizedInstall,
                branchName,
                basePriority + 6,
                reason + " Data.p4k",
                warnings);

            if (includeUserProfiles)
            {
                AddStarCitizenCandidate(
                    output,
                    seen,
                    Path.Combine(normalizedBranchDir, "USER", "Client", "0", "Profiles", "default", "actionmaps.xml"),
                    normalizedInstall,
                    branchName,
                    basePriority + 20,
                    reason + " USER profile",
                    warnings);
                AddStarCitizenCandidate(
                    output,
                    seen,
                    Path.Combine(normalizedBranchDir, "USER", "Client", "0", "Profiles", "default", "AllBinds.xml"),
                    normalizedInstall,
                    branchName,
                    basePriority + 21,
                    reason + " USER profile AllBinds",
                    warnings);
            }
        }

        private static void AddStarCitizenCandidatesFromDataP4k(
            List<StarCitizenSourceCandidate> output,
            HashSet<string> seen,
            string branchDirectory,
            string installPath,
            string branchName,
            int basePriority,
            string reason,
            List<string> warnings)
        {
            string normalizedBranchDir = NormalizePath(branchDirectory);
            if (string.IsNullOrWhiteSpace(normalizedBranchDir) || !Directory.Exists(normalizedBranchDir))
                return;

            string p4kPath = Path.Combine(normalizedBranchDir, "Data.p4k");
            if (!File.Exists(p4kPath))
                return;

            if (!TryGetStarCitizenP4kIndex(
                    p4kPath,
                    out Dictionary<string, StarCitizenP4kEntryDescriptor> entryIndex,
                    out string indexError))
            {
                AddUnique(
                    warnings,
                    "Could not inspect Star Citizen Data.p4k '" + p4kPath + "': " + (indexError ?? "unknown error"));
                return;
            }

            List<string> interestingEntries = entryIndex.Keys
                .Where(IsStarCitizenInterestingP4kEntry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (interestingEntries.Count == 0)
                return;

            // Prefer defaultProfile.xml first (full command catalog), then fallback actionmaps/allbinds.
            List<string> orderedEntries = interestingEntries
                .OrderBy(v => ScoreStarCitizenP4kEntry(v))
                .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < orderedEntries.Count; i++)
            {
                AddStarCitizenArchiveCandidate(
                    output,
                    seen,
                    p4kPath,
                    orderedEntries[i],
                    installPath,
                    branchName,
                    basePriority + i,
                    reason,
                    warnings);
            }
        }

        private static int ScoreStarCitizenP4kEntry(string entryPath)
        {
            string normalized = (entryPath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (normalized.EndsWith("data/libs/config/defaultprofile.xml", StringComparison.Ordinal))
                return 0;
            if (normalized.EndsWith("data/libs/config/defaultprofile/actionmaps.xml", StringComparison.Ordinal))
                return 1;
            if (normalized.EndsWith("data/libs/config/profiles/default/actionmaps.xml", StringComparison.Ordinal))
                return 2;
            if (normalized.EndsWith("user/client/0/profiles/default/actionmaps.xml", StringComparison.Ordinal))
                return 3;
            if (normalized.EndsWith("actionmaps.xml", StringComparison.Ordinal))
                return 4;
            if (normalized.EndsWith("allbinds.xml", StringComparison.Ordinal))
                return 5;
            return 10;
        }

        private static bool IsStarCitizenInterestingP4kEntry(string entryPath)
        {
            string normalized = (entryPath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (normalized.Length == 0 || normalized.EndsWith("/", StringComparison.Ordinal))
                return false;

            if (normalized.EndsWith("data/libs/config/defaultprofile.xml", StringComparison.Ordinal))
                return true;
            if (normalized.EndsWith("actionmaps.xml", StringComparison.Ordinal))
                return true;
            if (normalized.EndsWith("allbinds.xml", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static void AddStarCitizenArchiveCandidate(
            List<StarCitizenSourceCandidate> output,
            HashSet<string> seen,
            string archivePath,
            string archiveEntryPath,
            string installPath,
            string branch,
            int priority,
            string reason,
            List<string> warnings)
        {
            string normalizedArchive = NormalizePath(archivePath);
            string normalizedEntry = (archiveEntryPath ?? string.Empty).Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(normalizedArchive) || !File.Exists(normalizedArchive))
                return;
            if (string.IsNullOrWhiteSpace(normalizedEntry))
                return;

            string uniqueKey = normalizedArchive + "::" + normalizedEntry;
            if (!seen.Add(uniqueKey))
                return;

            StarCitizenSourceCandidate candidate = new StarCitizenSourceCandidate();
            candidate.FilePath = string.Empty;
            candidate.ArchivePath = normalizedArchive;
            candidate.ArchiveEntryPath = normalizedEntry;
            candidate.InstallPath = string.IsNullOrWhiteSpace(installPath)
                ? DetectStarCitizenInstallFromSourcePath(normalizedArchive, branch)
                : NormalizePath(installPath);
            candidate.Branch = string.IsNullOrWhiteSpace(branch)
                ? DetectStarCitizenBranchFromPath(normalizedArchive)
                : branch.Trim();
            candidate.Priority = priority;
            candidate.Reason = (reason ?? string.Empty) + " (" + normalizedEntry + ")";
            output.Add(candidate);

            if (warnings != null && string.IsNullOrWhiteSpace(candidate.Branch))
                AddUnique(warnings, "Detected Star Citizen archive source without branch marker: " + uniqueKey);
        }

        private static void AddStarCitizenCandidate(
            List<StarCitizenSourceCandidate> output,
            HashSet<string> seen,
            string filePath,
            string installPath,
            string branch,
            int priority,
            string reason,
            List<string> warnings)
        {
            string normalizedFile = NormalizePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedFile) || !File.Exists(normalizedFile))
                return;
            if (!seen.Add(normalizedFile))
                return;

            StarCitizenSourceCandidate candidate = new StarCitizenSourceCandidate();
            candidate.FilePath = normalizedFile;
            candidate.InstallPath = string.IsNullOrWhiteSpace(installPath)
                ? DetectStarCitizenInstallFromSourcePath(normalizedFile, branch)
                : NormalizePath(installPath);
            candidate.Branch = string.IsNullOrWhiteSpace(branch)
                ? DetectStarCitizenBranchFromPath(normalizedFile)
                : branch.Trim();
            candidate.Priority = priority;
            candidate.Reason = reason ?? string.Empty;
            output.Add(candidate);

            if (warnings != null && string.IsNullOrWhiteSpace(candidate.Branch))
            {
                AddUnique(warnings, "Detected Star Citizen source without branch marker: " + normalizedFile);
            }
        }

        private static string GetStarCitizenCandidateDisplayPath(StarCitizenSourceCandidate candidate)
        {
            if (candidate == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(candidate.FilePath))
                return candidate.FilePath;
            if (!string.IsNullOrWhiteSpace(candidate.ArchivePath) &&
                !string.IsNullOrWhiteSpace(candidate.ArchiveEntryPath))
            {
                return candidate.ArchivePath + "::" + candidate.ArchiveEntryPath;
            }

            return string.Empty;
        }

        private static bool LooksLikeStarCitizenBranchDirectory(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
                return false;

            string dataConfig = Path.Combine(normalized, "Data", "Libs", "Config");
            string userProfiles = Path.Combine(normalized, "USER", "Client", "0", "Profiles");
            return Directory.Exists(dataConfig) || Directory.Exists(userProfiles);
        }

        private static string DetectStarCitizenBranchFromPath(string path)
        {
            string normalized = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            char[] splitChars = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string[] parts = normalized.Split(splitChars, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = (parts[i] ?? string.Empty).Trim();
                if (token.Length == 0)
                    continue;
                if (string.Equals(token, "LIVE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "PTU", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "EPTU", StringComparison.OrdinalIgnoreCase))
                {
                    return token;
                }
            }
            return string.Empty;
        }

        private static string DetectStarCitizenInstallFromSourcePath(string sourcePath, string branch)
        {
            string normalized = NormalizePath(sourcePath);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            string branchName = (branch ?? string.Empty).Trim();
            if (branchName.Length == 0)
                branchName = DetectStarCitizenBranchFromPath(normalized);
            if (branchName.Length == 0)
                return string.Empty;

            string marker = Path.DirectorySeparatorChar + branchName + Path.DirectorySeparatorChar;
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex <= 0)
                return string.Empty;

            string prefix = normalized.Substring(0, markerIndex);
            return NormalizePath(prefix);
        }

        private static bool TryParseStarCitizenActionCatalog(
            StarCitizenSourceCandidate candidate,
            out StarCitizenParsedCatalog catalog,
            out string error)
        {
            catalog = new StarCitizenParsedCatalog();
            error = string.Empty;

            if (candidate == null ||
                (string.IsNullOrWhiteSpace(candidate.FilePath) &&
                 (string.IsNullOrWhiteSpace(candidate.ArchivePath) ||
                  string.IsNullOrWhiteSpace(candidate.ArchiveEntryPath))))
            {
                error = "Star Citizen source candidate is empty.";
                return false;
            }

            string sourceDisplayPath = GetStarCitizenCandidateDisplayPath(candidate);
            XDocument document;
            if (!TryLoadStarCitizenCandidateXml(candidate, out document, out string loadError))
            {
                error = string.IsNullOrWhiteSpace(loadError)
                    ? "Could not parse Star Citizen XML source: " + sourceDisplayPath
                    : loadError;
                return false;
            }

            catalog.SourceFile = sourceDisplayPath;
            catalog.InstallPath = candidate.InstallPath ?? string.Empty;
            catalog.Branch = candidate.Branch ?? string.Empty;
            catalog.CandidatePriority = candidate.Priority;
            catalog.CandidateReason = candidate.Reason ?? string.Empty;

            Dictionary<string, StarCitizenActionEntry> actionByKey =
                new Dictionary<string, StarCitizenActionEntry>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> actionMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<XElement> maps = document
                .Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "actionmap", StringComparison.OrdinalIgnoreCase));
            foreach (XElement actionMap in maps)
            {
                string category = ((string)actionMap.Attribute("name") ?? string.Empty).Trim();
                if (category.Length == 0)
                    category = "unknown";
                actionMaps.Add(category);

                IEnumerable<XElement> actions = actionMap
                    .Elements()
                    .Where(e => string.Equals(e.Name.LocalName, "action", StringComparison.OrdinalIgnoreCase));
                foreach (XElement action in actions)
                {
                    string id = ((string)action.Attribute("name") ?? string.Empty).Trim();
                    if (id.Length == 0)
                        continue;

                    string commentText = string.Empty;
                    XComment directComment = action.Nodes().OfType<XComment>().FirstOrDefault();
                    if (directComment != null)
                        commentText = (directComment.Value ?? string.Empty).Trim();
                    if (commentText.Length == 0)
                    {
                        XComment nestedComment = action.DescendantNodes().OfType<XComment>().FirstOrDefault();
                        if (nestedComment != null)
                            commentText = (nestedComment.Value ?? string.Empty).Trim();
                    }

                    bool isAxis;
                    string title;
                    TryParseStarCitizenActionComment(commentText, id, out title, out isAxis);
                    if (string.IsNullOrWhiteSpace(title) || string.Equals(title, id, StringComparison.OrdinalIgnoreCase))
                    {
                        string uiLabel = ((string)action.Attribute("UILabel") ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(uiLabel))
                            title = uiLabel;
                    }
                    bool inferredAxis = InferStarCitizenAxisFromActionAttributes(action, id);
                    // The option group (invert/exponent/curve of the joystick option tree): an attribute in the game's
                    // defaultProfile.xml, a child element in JoyPro's catalog.
                    string optionGroup = ((string)action.Attribute("optionGroup") ?? string.Empty).Trim();
                    if (optionGroup.Length == 0)
                    {
                        XElement groupElement = action.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "optionGroup", StringComparison.OrdinalIgnoreCase));
                        if (groupElement != null)
                            optionGroup = ((string)groupElement.Attribute("name") ?? string.Empty).Trim();
                    }

                    // Default joystick input: an attribute in the game's defaultProfile.xml, a child element in JoyPro's
                    // catalog. " ", "" and the 0/1 flags are no input.
                    string defaultJoystick = ((string)action.Attribute("joystick") ?? string.Empty).Trim();
                    if (defaultJoystick.Length == 0)
                    {
                        XElement defaultElement = action.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "defaultJoystick", StringComparison.OrdinalIgnoreCase));
                        if (defaultElement != null)
                            defaultJoystick = ((string)defaultElement.Attribute("input") ?? string.Empty).Trim();
                    }
                    if (defaultJoystick == "0" || defaultJoystick == "1")
                        defaultJoystick = string.Empty;

                    string key = category + "___" + id;
                    if (actionByKey.TryGetValue(key, out StarCitizenActionEntry existing))
                    {
                        catalog.DuplicateActionsSkipped++;
                        if (string.IsNullOrWhiteSpace(existing.OptionGroup) && optionGroup.Length > 0)
                            existing.OptionGroup = optionGroup;
                        if (string.IsNullOrWhiteSpace(existing.DefaultJoystick) && defaultJoystick.Length > 0)
                            existing.DefaultJoystick = defaultJoystick;
                        if ((string.IsNullOrWhiteSpace(existing.Title) ||
                             string.Equals(existing.Title, existing.Id, StringComparison.OrdinalIgnoreCase)) &&
                            !string.IsNullOrWhiteSpace(title) &&
                            !string.Equals(title, id, StringComparison.OrdinalIgnoreCase))
                        {
                            existing.Title = title;
                        }
                        existing.IsAxis = existing.IsAxis || isAxis || inferredAxis;
                        continue;
                    }

                    StarCitizenActionEntry entry = new StarCitizenActionEntry();
                    entry.Category = category;
                    entry.Id = id;
                    entry.Title = string.IsNullOrWhiteSpace(title) ? id : title;
                    entry.IsAxis = isAxis || inferredAxis;
                    entry.OptionGroup = optionGroup;
                    entry.DefaultJoystick = defaultJoystick;
                    actionByKey.Add(key, entry);
                }
            }

            catalog.OptionTree = document
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "optiontree", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals((string)e.Attribute("type"), "joystick", StringComparison.OrdinalIgnoreCase));
            catalog.ActionMapCount = actionMaps.Count;
            catalog.Actions = actionByKey.Values
                .OrderBy(v => v.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            catalog.ActionCount = catalog.Actions.Count;
            catalog.AxisCount = catalog.Actions.Count(v => v.IsAxis);
            catalog.ButtonCount = catalog.ActionCount - catalog.AxisCount;

            if (catalog.ActionCount == 0)
            {
                error = "No <action> entries found in Star Citizen source: " + sourceDisplayPath;
                return false;
            }

            return true;
        }

        private static bool TryLoadStarCitizenCandidateXml(
            StarCitizenSourceCandidate candidate,
            out XDocument document,
            out string error)
        {
            document = null;
            error = string.Empty;

            if (candidate == null)
            {
                error = "Star Citizen candidate is null.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(candidate.FilePath))
            {
                if (!File.Exists(candidate.FilePath))
                {
                    error = "Star Citizen source file does not exist: " + candidate.FilePath;
                    return false;
                }

                try
                {
                    byte[] bytes = File.ReadAllBytes(candidate.FilePath);
                    return TryLoadStarCitizenXDocumentFromBytes(
                        bytes,
                        candidate.FilePath,
                        out document,
                        out error);
                }
                catch (Exception ex)
                {
                    error = "Could not parse Star Citizen XML '" + candidate.FilePath + "': " + ex.Message;
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(candidate.ArchivePath) ||
                string.IsNullOrWhiteSpace(candidate.ArchiveEntryPath))
            {
                error = "Star Citizen candidate has no source path.";
                return false;
            }

            if (!File.Exists(candidate.ArchivePath))
            {
                error = "Star Citizen archive source does not exist: " + candidate.ArchivePath;
                return false;
            }

            string entryPath = candidate.ArchiveEntryPath.Replace('\\', '/');
            try
            {
                if (!TryReadStarCitizenP4kEntryBytes(
                        candidate.ArchivePath,
                        entryPath,
                        out byte[] entryBytes,
                        out string readError))
                {
                    error = string.IsNullOrWhiteSpace(readError)
                        ? "Could not read Star Citizen archive entry: " + candidate.ArchivePath + "::" + entryPath
                        : readError;
                    return false;
                }

                return TryLoadStarCitizenXDocumentFromBytes(
                    entryBytes,
                    candidate.ArchivePath + "::" + entryPath,
                    out document,
                    out error);
            }
            catch (Exception ex)
            {
                error = "Could not read Star Citizen archive source '" + candidate.ArchivePath + "::" + entryPath + "': " + ex.Message;
                return false;
            }
        }

        private static bool TryLoadStarCitizenXDocumentFromStream(
            Stream stream,
            string sourceLabel,
            out XDocument document,
            out string error)
        {
            document = null;
            error = string.Empty;

            if (stream == null)
            {
                error = "Star Citizen XML stream is null for " + sourceLabel + ".";
                return false;
            }

            byte[] bytes;
            using (MemoryStream memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            return TryLoadStarCitizenXDocumentFromBytes(bytes, sourceLabel, out document, out error);
        }

        private static bool TryLoadStarCitizenXDocumentFromBytes(
            byte[] bytes,
            string sourceLabel,
            out XDocument document,
            out string error)
        {
            document = null;
            error = string.Empty;

            if (bytes.Length == 0)
            {
                error = "Star Citizen XML stream is empty: " + sourceLabel;
                return false;
            }

            if (LooksLikeCryXmlBinary(bytes))
            {
                if (!TryParseCryXmlBinaryDocument(bytes, out document, out string cryError))
                {
                    error = "Could not parse CryXmlB source from " + sourceLabel + ": " + cryError;
                    return false;
                }

                // Debug aid: JOYPRO_SC_DUMP_DIR=<folder> saves every converted CryXmlB (binary XML) source as text.
                string dumpDir = Environment.GetEnvironmentVariable("JOYPRO_SC_DUMP_DIR");
                if (!string.IsNullOrWhiteSpace(dumpDir))
                {
                    try
                    {
                        Directory.CreateDirectory(dumpDir);
                        string dumpName = System.Text.RegularExpressions.Regex.Replace(sourceLabel ?? "source", "[^A-Za-z0-9._-]+", "_");
                        document.Save(Path.Combine(dumpDir, dumpName.Length > 120 ? dumpName.Substring(dumpName.Length - 120) : dumpName));
                    }
                    catch (Exception)
                    {
                    }
                }

                return true;
            }

            string text;
            try
            {
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                error = "Could not decode Star Citizen XML bytes (" + sourceLabel + "): " + ex.Message;
                return false;
            }

            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                error =
                    "Star Citizen archive entry is not plain XML: " +
                    sourceLabel +
                    ". This entry may be packed as CryXML; provide an extracted XML via --source.";
                return false;
            }

            try
            {
                document = XDocument.Parse(text, LoadOptions.None);
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not parse Star Citizen XML text from " + sourceLabel + ": " + ex.Message;
                return false;
            }
        }

        private static bool LooksLikeCryXmlBinary(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8)
                return false;

            return bytes[0] == (byte)'C' &&
                   bytes[1] == (byte)'r' &&
                   bytes[2] == (byte)'y' &&
                   bytes[3] == (byte)'X' &&
                   bytes[4] == (byte)'m' &&
                   bytes[5] == (byte)'l' &&
                   bytes[6] == (byte)'B' &&
                   bytes[7] == 0;
        }

        private static bool TryParseCryXmlBinaryDocument(
            byte[] bytes,
            out XDocument document,
            out string error)
        {
            document = null;
            error = string.Empty;

            try
            {
                if (bytes == null || bytes.Length < 44)
                {
                    error = "CryXmlB header is incomplete.";
                    return false;
                }
                if (!LooksLikeCryXmlBinary(bytes))
                {
                    error = "CryXmlB magic header not found.";
                    return false;
                }

                int nodeTableOffset = BitConverter.ToInt32(bytes, 12);
                int nodeTableCount = BitConverter.ToInt32(bytes, 16);
                int referenceTableOffset = BitConverter.ToInt32(bytes, 20);
                int referenceTableCount = BitConverter.ToInt32(bytes, 24);
                int orderTableOffset = BitConverter.ToInt32(bytes, 28);
                int orderTableCount = BitConverter.ToInt32(bytes, 32);
                int contentOffset = BitConverter.ToInt32(bytes, 36);
                int contentLength = BitConverter.ToInt32(bytes, 40);

                if (nodeTableOffset < 0 ||
                    nodeTableCount < 0 ||
                    referenceTableOffset < 0 ||
                    referenceTableCount < 0 ||
                    orderTableOffset < 0 ||
                    orderTableCount < 0 ||
                    contentOffset < 0 ||
                    contentLength < 0)
                {
                    error = "CryXmlB contains negative offsets/counts.";
                    return false;
                }

                long nodeTableEnd = (long)nodeTableOffset + ((long)nodeTableCount * 28L);
                long referenceTableEnd = (long)referenceTableOffset + ((long)referenceTableCount * 8L);
                long orderTableEnd = (long)orderTableOffset + ((long)orderTableCount * 4L);
                long contentEnd = (long)contentOffset + contentLength;
                if (nodeTableEnd > bytes.Length ||
                    referenceTableEnd > bytes.Length ||
                    orderTableEnd > bytes.Length ||
                    contentEnd > bytes.Length)
                {
                    error = "CryXmlB table ranges are outside file bounds.";
                    return false;
                }
                if (nodeTableCount <= 0)
                {
                    error = "CryXmlB has no nodes.";
                    return false;
                }

                List<CryXmlBinaryNode> nodes = new List<CryXmlBinaryNode>(nodeTableCount);
                int offset = nodeTableOffset;
                for (int i = 0; i < nodeTableCount; i++)
                {
                    CryXmlBinaryNode node = new CryXmlBinaryNode();
                    node.NodeNameOffset = BitConverter.ToInt32(bytes, offset + 0);
                    node.ItemType = BitConverter.ToInt32(bytes, offset + 4);
                    node.AttributeCount = BitConverter.ToInt16(bytes, offset + 8);
                    node.ChildCount = BitConverter.ToInt16(bytes, offset + 10);
                    node.ParentNodeId = BitConverter.ToInt32(bytes, offset + 12);
                    node.FirstAttributeIndex = BitConverter.ToInt32(bytes, offset + 16);
                    node.FirstChildIndex = BitConverter.ToInt32(bytes, offset + 20);
                    node.Reserved = BitConverter.ToInt32(bytes, offset + 24);
                    nodes.Add(node);
                    offset += 28;
                }

                List<CryXmlBinaryReference> references = new List<CryXmlBinaryReference>(referenceTableCount);
                offset = referenceTableOffset;
                for (int i = 0; i < referenceTableCount; i++)
                {
                    CryXmlBinaryReference reference = new CryXmlBinaryReference();
                    reference.NameOffset = BitConverter.ToInt32(bytes, offset + 0);
                    reference.ValueOffset = BitConverter.ToInt32(bytes, offset + 4);
                    references.Add(reference);
                    offset += 8;
                }

                List<int> orderTable = new List<int>(orderTableCount);
                offset = orderTableOffset;
                for (int i = 0; i < orderTableCount; i++)
                {
                    orderTable.Add(BitConverter.ToInt32(bytes, offset));
                    offset += 4;
                }

                Dictionary<int, string> stringTable = new Dictionary<int, string>();
                int contentStart = contentOffset;
                int contentStop = contentOffset + contentLength;
                while (contentStart < contentStop)
                {
                    int end = contentStart;
                    while (end < contentStop && bytes[end] != 0)
                        end++;

                    int relativeOffset = contentStart - contentOffset;
                    string value = end > contentStart
                        ? Encoding.UTF8.GetString(bytes, contentStart, end - contentStart)
                        : string.Empty;
                    if (!stringTable.ContainsKey(relativeOffset))
                        stringTable.Add(relativeOffset, value);

                    contentStart = end + 1;
                }

                XElement[] elements = new XElement[nodes.Count];
                for (int i = 0; i < nodes.Count; i++)
                {
                    CryXmlBinaryNode node = nodes[i];
                    string nodeName = GetCryXmlTableString(stringTable, node.NodeNameOffset, "node");
                    if (string.IsNullOrWhiteSpace(nodeName))
                        nodeName = "node";

                    XElement element;
                    try
                    {
                        element = new XElement(nodeName);
                    }
                    catch
                    {
                        element = new XElement("node");
                    }

                    for (int k = 0; k < node.AttributeCount; k++)
                    {
                        int attributeIndex = node.FirstAttributeIndex + k;
                        if (attributeIndex < 0 || attributeIndex >= references.Count)
                            continue;

                        CryXmlBinaryReference reference = references[attributeIndex];
                        string attributeName = GetCryXmlTableString(stringTable, reference.NameOffset, string.Empty);
                        if (string.IsNullOrWhiteSpace(attributeName))
                            continue;

                        string attributeValue = GetCryXmlTableString(stringTable, reference.ValueOffset, string.Empty);
                        try
                        {
                            element.SetAttributeValue(attributeName, attributeValue ?? string.Empty);
                        }
                        catch
                        {
                            // Ignore malformed attribute names; keep parsing the rest.
                        }
                    }

                    elements[i] = element;
                }

                for (int parentId = 0; parentId < nodes.Count; parentId++)
                {
                    CryXmlBinaryNode parentNode = nodes[parentId];
                    if (parentNode.ChildCount <= 0)
                        continue;

                    for (int k = 0; k < parentNode.ChildCount; k++)
                    {
                        int orderIndex = parentNode.FirstChildIndex + k;
                        if (orderIndex < 0 || orderIndex >= orderTable.Count)
                            continue;

                        int childId = orderTable[orderIndex];
                        if (childId < 0 || childId >= elements.Length || childId == parentId)
                            continue;

                        XElement parentElement = elements[parentId];
                        XElement childElement = elements[childId];
                        if (parentElement == null || childElement == null || childElement.Parent != null)
                            continue;

                        parentElement.Add(childElement);
                    }
                }

                for (int childId = 0; childId < nodes.Count; childId++)
                {
                    XElement childElement = elements[childId];
                    if (childElement == null || childElement.Parent != null)
                        continue;

                    int parentId = nodes[childId].ParentNodeId;
                    if (parentId < 0 || parentId >= nodes.Count || parentId == childId)
                        continue;

                    XElement parentElement = elements[parentId];
                    if (parentElement != null)
                        parentElement.Add(childElement);
                }

                int rootId = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].ParentNodeId < 0)
                    {
                        rootId = i;
                        break;
                    }
                }

                if (rootId < 0 || rootId >= elements.Length || elements[rootId] == null)
                {
                    error = "CryXmlB root node could not be resolved.";
                    return false;
                }

                document = new XDocument(elements[rootId]);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetCryXmlTableString(
            Dictionary<int, string> table,
            int relativeOffset,
            string fallback)
        {
            if (table != null && table.TryGetValue(relativeOffset, out string value))
                return value ?? string.Empty;
            return fallback ?? string.Empty;
        }

        private static bool TryGetStarCitizenP4kIndex(
            string archivePath,
            out Dictionary<string, StarCitizenP4kEntryDescriptor> index,
            out string error)
        {
            index = null;
            error = string.Empty;

            string normalizedArchive = NormalizePath(archivePath);
            if (string.IsNullOrWhiteSpace(normalizedArchive))
            {
                error = "Archive path is empty.";
                return false;
            }

            lock (StarCitizenP4kIndexCacheLock)
            {
                if (StarCitizenP4kIndexCache.TryGetValue(normalizedArchive, out Dictionary<string, StarCitizenP4kEntryDescriptor> cached))
                {
                    index = cached;
                    return true;
                }
            }

            if (!TryBuildStarCitizenP4kIndex(normalizedArchive, out Dictionary<string, StarCitizenP4kEntryDescriptor> built, out error))
                return false;

            lock (StarCitizenP4kIndexCacheLock)
            {
                if (!StarCitizenP4kIndexCache.ContainsKey(normalizedArchive))
                    StarCitizenP4kIndexCache.Add(normalizedArchive, built);
                index = StarCitizenP4kIndexCache[normalizedArchive];
            }

            return true;
        }

        private static bool TryBuildStarCitizenP4kIndex(
            string archivePath,
            out Dictionary<string, StarCitizenP4kEntryDescriptor> index,
            out string error)
        {
            index = new Dictionary<string, StarCitizenP4kEntryDescriptor>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;

            if (!File.Exists(archivePath))
            {
                error = "Archive file does not exist: " + archivePath;
                return false;
            }

            try
            {
                using (FileStream stream = File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!TryGetStarCitizenP4kCentralDirectoryInfo(
                            stream,
                            out long centralDirectoryOffset,
                            out long centralDirectorySize,
                            out error))
                    {
                        return false;
                    }

                    if (centralDirectoryOffset < 0 ||
                        centralDirectorySize <= 0 ||
                        centralDirectoryOffset + centralDirectorySize > stream.Length)
                    {
                        error = "P4K central directory range is invalid.";
                        return false;
                    }

                    stream.Position = centralDirectoryOffset;
                    long remaining = centralDirectorySize;
                    byte[] fixedHeader = new byte[46];
                    while (remaining >= 46)
                    {
                        if (!TryReadExact(stream, fixedHeader, 0, fixedHeader.Length))
                        {
                            error = "Could not read P4K central-directory header.";
                            return false;
                        }
                        remaining -= 46;

                        uint signature = BitConverter.ToUInt32(fixedHeader, 0);
                        if (signature != 0x02014b50U)
                            break;

                        ushort compressionMethod = BitConverter.ToUInt16(fixedHeader, 10);
                        uint compressedSize32 = BitConverter.ToUInt32(fixedHeader, 20);
                        uint uncompressedSize32 = BitConverter.ToUInt32(fixedHeader, 24);
                        ushort entryNameLength = BitConverter.ToUInt16(fixedHeader, 28);
                        ushort extraLength = BitConverter.ToUInt16(fixedHeader, 30);
                        ushort commentLength = BitConverter.ToUInt16(fixedHeader, 32);
                        uint localHeaderOffset32 = BitConverter.ToUInt32(fixedHeader, 42);

                        long variableLength = (long)entryNameLength + extraLength + commentLength;
                        if (variableLength < 0 || variableLength > remaining)
                        {
                            error = "P4K central-directory entry length is corrupt.";
                            return false;
                        }

                        byte[] entryNameBytes = new byte[entryNameLength];
                        if (!TryReadExact(stream, entryNameBytes, 0, entryNameBytes.Length))
                        {
                            error = "Could not read P4K central-directory entry name.";
                            return false;
                        }

                        byte[] extraBytes = new byte[extraLength];
                        if (extraBytes.Length > 0 && !TryReadExact(stream, extraBytes, 0, extraBytes.Length))
                        {
                            error = "Could not read P4K central-directory extra block.";
                            return false;
                        }

                        if (commentLength > 0)
                            stream.Position += commentLength;

                        remaining -= variableLength;

                        string entryPath = NormalizeStarCitizenArchiveEntryPath(DecodeZipEntryName(entryNameBytes));
                        if (string.IsNullOrWhiteSpace(entryPath))
                            continue;

                        long compressedSize = compressedSize32;
                        long uncompressedSize = uncompressedSize32;
                        long localHeaderOffset = localHeaderOffset32;
                        ParseZip64ExtraForSizes(
                            extraBytes,
                            compressedSize32 == 0xFFFFFFFFU,
                            uncompressedSize32 == 0xFFFFFFFFU,
                            localHeaderOffset32 == 0xFFFFFFFFU,
                            ref compressedSize,
                            ref uncompressedSize,
                            ref localHeaderOffset);

                        StarCitizenP4kEntryDescriptor descriptor = new StarCitizenP4kEntryDescriptor();
                        descriptor.EntryPath = entryPath;
                        descriptor.CompressionMethod = compressionMethod;
                        descriptor.CompressedSize = compressedSize;
                        descriptor.UncompressedSize = uncompressedSize;
                        descriptor.LocalHeaderOffset = localHeaderOffset;

                        if (!index.ContainsKey(entryPath))
                            index.Add(entryPath, descriptor);
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

        private static bool TryReadStarCitizenP4kEntryBytes(
            string archivePath,
            string archiveEntryPath,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = string.Empty;

            if (!TryGetStarCitizenP4kIndex(
                    archivePath,
                    out Dictionary<string, StarCitizenP4kEntryDescriptor> index,
                    out error))
            {
                return false;
            }

            string normalizedEntryPath = NormalizeStarCitizenArchiveEntryPath(archiveEntryPath);
            if (string.IsNullOrWhiteSpace(normalizedEntryPath))
            {
                error = "Archive entry path is empty.";
                return false;
            }

            if (!index.TryGetValue(normalizedEntryPath, out StarCitizenP4kEntryDescriptor descriptor))
            {
                error = "Star Citizen archive entry not found: " + archivePath + "::" + normalizedEntryPath;
                return false;
            }

            try
            {
                using (FileStream stream = File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!TryReadStarCitizenP4kEntryRaw(
                            stream,
                            descriptor,
                            out byte[] compressedBytes,
                            out ushort compressionMethod,
                            out long expectedUncompressedSize,
                            out error))
                    {
                        return false;
                    }

                    return TryDecompressStarCitizenP4kPayload(
                        compressedBytes,
                        compressionMethod,
                        expectedUncompressedSize,
                        descriptor.EntryPath,
                        out bytes,
                        out error);
                }
            }
            catch (Exception ex)
            {
                error = "Could not read Star Citizen archive entry '" + archivePath + "::" + normalizedEntryPath + "': " + ex.Message;
                return false;
            }
        }

        private static bool TryReadStarCitizenP4kEntryRaw(
            FileStream stream,
            StarCitizenP4kEntryDescriptor descriptor,
            out byte[] compressedBytes,
            out ushort compressionMethod,
            out long expectedUncompressedSize,
            out string error)
        {
            compressedBytes = null;
            compressionMethod = 0;
            expectedUncompressedSize = 0;
            error = string.Empty;

            if (stream == null || descriptor == null)
            {
                error = "P4K stream/descriptor is null.";
                return false;
            }

            if (descriptor.LocalHeaderOffset < 0 || descriptor.LocalHeaderOffset >= stream.Length)
            {
                error = "P4K local-header offset is out of range for entry: " + (descriptor.EntryPath ?? string.Empty);
                return false;
            }

            stream.Position = descriptor.LocalHeaderOffset;
            byte[] localHeader = new byte[30];
            if (!TryReadExact(stream, localHeader, 0, localHeader.Length))
            {
                error = "Could not read P4K local-header for entry: " + (descriptor.EntryPath ?? string.Empty);
                return false;
            }

            uint signature = BitConverter.ToUInt32(localHeader, 0);
            if (signature != 0x04034b50U && signature != 0x14034b50U)
            {
                error = "Unexpected P4K local-header signature for entry '" + (descriptor.EntryPath ?? string.Empty) + "'.";
                return false;
            }

            ushort localCompressionMethod = BitConverter.ToUInt16(localHeader, 8);
            uint localCompressedSize32 = BitConverter.ToUInt32(localHeader, 18);
            uint localUncompressedSize32 = BitConverter.ToUInt32(localHeader, 22);
            ushort localNameLength = BitConverter.ToUInt16(localHeader, 26);
            ushort localExtraLength = BitConverter.ToUInt16(localHeader, 28);

            byte[] localNameBytes = new byte[localNameLength];
            if (localNameBytes.Length > 0 && !TryReadExact(stream, localNameBytes, 0, localNameBytes.Length))
            {
                error = "Could not read P4K local-header name for entry: " + (descriptor.EntryPath ?? string.Empty);
                return false;
            }

            byte[] localExtraBytes = new byte[localExtraLength];
            if (localExtraBytes.Length > 0 && !TryReadExact(stream, localExtraBytes, 0, localExtraBytes.Length))
            {
                error = "Could not read P4K local-header extra for entry: " + (descriptor.EntryPath ?? string.Empty);
                return false;
            }

            long compressedSize = descriptor.CompressedSize > 0 ? descriptor.CompressedSize : localCompressedSize32;
            long uncompressedSize = descriptor.UncompressedSize > 0 ? descriptor.UncompressedSize : localUncompressedSize32;
            long ignoredOffset = descriptor.LocalHeaderOffset;
            ParseZip64ExtraForSizes(
                localExtraBytes,
                localCompressedSize32 == 0xFFFFFFFFU,
                localUncompressedSize32 == 0xFFFFFFFFU,
                false,
                ref compressedSize,
                ref uncompressedSize,
                ref ignoredOffset);

            if (compressedSize < 0 || compressedSize > int.MaxValue)
            {
                error = "P4K entry compressed size is invalid for '" + (descriptor.EntryPath ?? string.Empty) + "'.";
                return false;
            }
            if (stream.Position + compressedSize > stream.Length)
            {
                error = "P4K entry payload exceeds archive bounds for '" + (descriptor.EntryPath ?? string.Empty) + "'.";
                return false;
            }

            compressedBytes = new byte[(int)compressedSize];
            if (compressedBytes.Length > 0 && !TryReadExact(stream, compressedBytes, 0, compressedBytes.Length))
            {
                error = "Could not read P4K entry payload for '" + (descriptor.EntryPath ?? string.Empty) + "'.";
                return false;
            }

            compressionMethod = descriptor.CompressionMethod > 0
                ? descriptor.CompressionMethod
                : localCompressionMethod;
            expectedUncompressedSize = uncompressedSize;
            return true;
        }

        private static bool TryDecompressStarCitizenP4kPayload(
            byte[] compressedBytes,
            ushort compressionMethod,
            long expectedUncompressedSize,
            string entryPath,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = string.Empty;

            if (compressedBytes == null)
            {
                error = "Compressed payload is null for entry: " + (entryPath ?? string.Empty);
                return false;
            }

            try
            {
                switch (compressionMethod)
                {
                    case 0:
                        bytes = compressedBytes;
                        break;
                    case 8:
                        using (MemoryStream compressedStream = new MemoryStream(compressedBytes, writable: false))
                        using (DeflateStream inflate = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: false))
                        using (MemoryStream output = new MemoryStream())
                        {
                            inflate.CopyTo(output);
                            bytes = output.ToArray();
                        }
                        break;
                    case 20:
                    case 93:
                    case 100:
                        using (Decompressor decompressor = new Decompressor())
                        {
                            bytes = decompressor.Unwrap(compressedBytes).ToArray();
                        }
                        break;
                    default:
                        error = "Unsupported Star Citizen P4K compression method " + compressionMethod + " for entry '" + (entryPath ?? string.Empty) + "'.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = "Could not decompress Star Citizen P4K entry '" + (entryPath ?? string.Empty) + "': " + ex.Message;
                return false;
            }

            if (bytes == null)
                bytes = new byte[0];

            if (expectedUncompressedSize > 0 &&
                expectedUncompressedSize <= int.MaxValue &&
                bytes.Length == 0)
            {
                error = "Star Citizen P4K entry decompressed to empty payload: " + (entryPath ?? string.Empty);
                return false;
            }

            return true;
        }

        private static bool TryGetStarCitizenP4kCentralDirectoryInfo(
            FileStream stream,
            out long centralDirectoryOffset,
            out long centralDirectorySize,
            out string error)
        {
            centralDirectoryOffset = 0;
            centralDirectorySize = 0;
            error = string.Empty;

            if (stream == null)
            {
                error = "P4K stream is null.";
                return false;
            }
            if (stream.Length < 22)
            {
                error = "P4K file is too small.";
                return false;
            }

            int tailSize = (int)Math.Min(8 * 1024 * 1024, stream.Length);
            byte[] tail = new byte[tailSize];
            stream.Position = stream.Length - tailSize;
            if (!TryReadExact(stream, tail, 0, tail.Length))
            {
                error = "Could not read P4K tail block.";
                return false;
            }

            int eocdIndex = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 &&
                    tail[i + 1] == 0x4B &&
                    tail[i + 2] == 0x05 &&
                    tail[i + 3] == 0x06)
                {
                    eocdIndex = i;
                    break;
                }
            }
            if (eocdIndex < 0)
            {
                error = "P4K EOCD marker was not found.";
                return false;
            }

            uint cdSize32 = BitConverter.ToUInt32(tail, eocdIndex + 12);
            uint cdOffset32 = BitConverter.ToUInt32(tail, eocdIndex + 16);
            centralDirectorySize = cdSize32;
            centralDirectoryOffset = cdOffset32;

            int zip64LocatorIndex = eocdIndex - 20;
            bool hasZip64Locator = zip64LocatorIndex >= 0 &&
                                   tail[zip64LocatorIndex] == 0x50 &&
                                   tail[zip64LocatorIndex + 1] == 0x4B &&
                                   tail[zip64LocatorIndex + 2] == 0x06 &&
                                   tail[zip64LocatorIndex + 3] == 0x07;
            if (hasZip64Locator)
            {
                ulong zip64EocdOffset = BitConverter.ToUInt64(tail, zip64LocatorIndex + 8);
                if (zip64EocdOffset > (ulong)Math.Max(0L, stream.Length - 56))
                {
                    error = "P4K ZIP64 EOCD offset is out of range.";
                    return false;
                }

                stream.Position = (long)zip64EocdOffset;
                byte[] zip64Header = new byte[56];
                if (!TryReadExact(stream, zip64Header, 0, zip64Header.Length))
                {
                    error = "Could not read P4K ZIP64 EOCD block.";
                    return false;
                }
                if (zip64Header[0] == 0x50 &&
                    zip64Header[1] == 0x4B &&
                    zip64Header[2] == 0x06 &&
                    zip64Header[3] == 0x06)
                {
                    centralDirectorySize = (long)BitConverter.ToUInt64(zip64Header, 40);
                    centralDirectoryOffset = (long)BitConverter.ToUInt64(zip64Header, 48);
                }
            }

            if (centralDirectorySize <= 0)
            {
                error = "P4K central directory size is zero.";
                return false;
            }

            return true;
        }

        private static bool TryReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            if (stream == null || buffer == null)
                return false;

            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                    return false;
                total += read;
            }

            return true;
        }

        private static void ParseZip64ExtraForSizes(
            byte[] extraBytes,
            bool needsCompressedSize,
            bool needsUncompressedSize,
            bool needsLocalHeaderOffset,
            ref long compressedSize,
            ref long uncompressedSize,
            ref long localHeaderOffset)
        {
            if (extraBytes == null || extraBytes.Length < 4)
                return;

            int index = 0;
            while (index + 4 <= extraBytes.Length)
            {
                ushort headerId = BitConverter.ToUInt16(extraBytes, index);
                ushort dataLength = BitConverter.ToUInt16(extraBytes, index + 2);
                int dataStart = index + 4;
                int dataEnd = dataStart + dataLength;
                if (dataEnd > extraBytes.Length)
                    break;

                if (headerId == 0x0001)
                {
                    int cursor = dataStart;
                    if (needsUncompressedSize && cursor + 8 <= dataEnd)
                    {
                        uncompressedSize = (long)BitConverter.ToUInt64(extraBytes, cursor);
                        cursor += 8;
                    }
                    if (needsCompressedSize && cursor + 8 <= dataEnd)
                    {
                        compressedSize = (long)BitConverter.ToUInt64(extraBytes, cursor);
                        cursor += 8;
                    }
                    if (needsLocalHeaderOffset && cursor + 8 <= dataEnd)
                    {
                        localHeaderOffset = (long)BitConverter.ToUInt64(extraBytes, cursor);
                        cursor += 8;
                    }
                    break;
                }

                index = dataEnd;
            }
        }

        private static string NormalizeStarCitizenArchiveEntryPath(string path)
        {
            return (path ?? string.Empty)
                .Replace('\\', '/')
                .Trim();
        }

        private static string DecodeZipEntryName(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        private static bool InferStarCitizenAxisFromActionAttributes(XElement action, string actionId)
        {
            if (action == null)
                return false;

            string id = (actionId ?? string.Empty).Trim().ToLowerInvariant();
            // Steps, toggles, presses and mode switches are buttons even when their name mentions an axis word or a
            // default sits on the mouse wheel ("v_ifcs_throttle_swap_mode", "v_strafe_up", "v_view_cycle_fwd").
            if (System.Text.RegularExpressions.Regex.IsMatch(id,
                    @"(toggle|cycle|swap|_set_|reset|_mode|launch|ping|increase|decrease|_inc$|_dec$|_up$|_down$|_left$|_right$|_fwd$|_forward$|_back$|_backward$|_on$|_off$|_hold$|_short$|_long$|_max$|_min$)"))
                return false;

            string[] axisAttributeNames = new[] { "joystick", "gamepad", "mouse" };
            for (int i = 0; i < axisAttributeNames.Length; i++)
            {
                string token = ((string)action.Attribute(axisAttributeNames[i]) ?? string.Empty).Trim();
                if (LooksLikeAxisBindingToken(token))
                    return true;
            }

            if (id.Contains("_abs") ||
                id.Contains("_axis") ||
                id.EndsWith("_mouse", StringComparison.Ordinal) ||
                id.Contains("_pitch") ||
                id.Contains("_yaw") ||
                id.Contains("_roll") ||
                id.Contains("throttle") ||
                id.Contains("strafe"))
            {
                return true;
            }

            return false;
        }

        private static bool LooksLikeAxisBindingToken(string token)
        {
            string normalized = (token ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized == " ")
                return false;

            string[] parts = normalized.Split(new[] { '+', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;

                if (part.StartsWith("maxis_", StringComparison.Ordinal) ||
                    part.StartsWith("joy_", StringComparison.Ordinal))
                {
                    return true;
                }

                switch (part)
                {
                    case "x":
                    case "y":
                    case "z":
                    case "rx":
                    case "ry":
                    case "rz":
                    case "rotx":
                    case "roty":
                    case "rotz":
                    case "slider":
                    case "slider1":
                    case "slider2":
                    case "thumblx":
                    case "thumbly":
                    case "thumbrx":
                    case "thumbry":
                        return true;
                }
            }

            return false;
        }

        private static bool TryParseStarCitizenActionComment(
            string rawComment,
            string fallbackId,
            out string title,
            out bool isAxis)
        {
            title = (fallbackId ?? string.Empty).Trim();
            if (title.Length == 0)
                title = "unknown_action";
            isAxis = false;

            string comment = (rawComment ?? string.Empty).Trim();
            if (comment.Length == 0)
                return false;

            int split = comment.LastIndexOf("//", StringComparison.Ordinal);
            string titlePart = split >= 0 ? comment.Substring(0, split) : comment;
            string axisPart = split >= 0 ? comment.Substring(split + 2) : string.Empty;

            titlePart = titlePart.Trim();
            if (titlePart.Length > 0)
                title = titlePart;

            if (axisPart.Length > 0)
            {
                if (!TryParseOptionBoolean(axisPart.Trim(), out isAxis))
                {
                    string axisToken = axisPart.Trim().ToLowerInvariant();
                    isAxis = axisToken.Contains("true") ||
                             axisToken.Contains("axis") ||
                             axisToken.Contains("analog") ||
                             axisToken.Contains("abs");
                }
            }

            return true;
        }

        // Format (read by older builds too): <action name="id"><!--title//axis--></action>, unchanged; the option group
        // follows the comment as <optionGroup name="..."/>, and the game's joystick <optiontree> (defaults, parents,
        // which groups show curve/invert) sits before the action maps.
        private static void WriteStarCitizenActionCatalogXml(string outputPath, List<StarCitizenActionEntry> actions, XElement optionTree = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is empty.", nameof(outputPath));

            List<StarCitizenActionEntry> orderedActions = (actions ?? new List<StarCitizenActionEntry>())
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.Id))
                .OrderBy(v => v.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<ActionMaps>");
            sb.AppendLine(" <ActionProfiles version=\"1\" optionsVersion=\"2\" rebindVersion=\"2\" profileName=\"joypro_generated\">");
            if (optionTree != null)
            {
                XElement tree = new XElement(optionTree);
                foreach (XNode comment in tree.DescendantNodes().OfType<XComment>().ToList())
                    comment.Remove();
                string treeText = tree.ToString(SaveOptions.None).Replace("\r\n", "\n");
                foreach (string line in treeText.Split('\n'))
                {
                    // XElement indents by two spaces, the catalog (like the game) by one.
                    string body = line.TrimStart(' ');
                    sb.AppendLine(new string(' ', 2 + (line.Length - body.Length) / 2) + body);
                }
            }

            IEnumerable<IGrouping<string, StarCitizenActionEntry>> groups = orderedActions
                .GroupBy(v => string.IsNullOrWhiteSpace(v.Category) ? "unknown" : v.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, StarCitizenActionEntry> group in groups)
            {
                sb.Append("  <actionmap name=\"");
                sb.Append(EscapeXmlAttribute(group.Key));
                sb.AppendLine("\">");

                foreach (StarCitizenActionEntry action in group.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase))
                {
                    string title = string.IsNullOrWhiteSpace(action.Title) ? action.Id : action.Title;
                    sb.Append("   <action name=\"");
                    sb.Append(EscapeXmlAttribute(action.Id ?? string.Empty));
                    sb.Append("\"><!--");
                    sb.Append(SanitizeXmlCommentText(title));
                    sb.Append("//");
                    sb.Append(action.IsAxis ? "true" : "false");
                    sb.Append("-->");
                    if (!string.IsNullOrWhiteSpace(action.OptionGroup))
                    {
                        sb.Append("<optionGroup name=\"");
                        sb.Append(EscapeXmlAttribute(action.OptionGroup.Trim()));
                        sb.Append("\"/>");
                    }
                    if (!string.IsNullOrWhiteSpace(action.DefaultJoystick))
                    {
                        sb.Append("<defaultJoystick input=\"");
                        sb.Append(EscapeXmlAttribute(action.DefaultJoystick.Trim()));
                        sb.Append("\"/>");
                    }
                    sb.AppendLine("</action>");
                }

                sb.AppendLine("  </actionmap>");
            }

            sb.AppendLine(" </ActionProfiles>");
            sb.AppendLine("</ActionMaps>");

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        }

        private static string EscapeXmlAttribute(string value)
        {
            string text = value ?? string.Empty;
            if (text.Length == 0)
                return string.Empty;

            return text
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string SanitizeXmlCommentText(string value)
        {
            string text = (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (text.Length == 0)
                text = "unnamed action";

            while (text.IndexOf("--", StringComparison.Ordinal) >= 0)
                text = text.Replace("--", "-");
            if (text.EndsWith("-", StringComparison.Ordinal))
                text += " ";
            return text;
        }

        // engine sync status|accept|diff|apply: changes made in a game's own config since JoyPro wrote it (EngineSync).
        private static int RunSync(List<string> command, string outPath, bool textOutput)
        {
            string payload;
            int exitCode = EngineSync.Run(command, textOutput, out payload);
            if (!string.IsNullOrEmpty(payload))
                EmitPayload(payload, outPath);
            return exitCode;
        }

        // engine vjoy status|mode|plan|map|settings|exe|functions|limits|forwarder-config|configure-vjoy|apply-hidhide|restore-hidhide|recover
        // (EngineVJoy, docs/virtual-joystick.md).
        private static int RunVJoy(List<string> command, string outPath, bool textOutput)
        {
            string payload;
            int exitCode = EngineVJoy.Run(command, textOutput, out payload);
            if (string.IsNullOrEmpty(payload))
                return exitCode;
            // A plain-text refusal goes to stderr; results (JSON or text) to the output.
            if (exitCode != 0 && !payload.TrimStart().StartsWith("{", StringComparison.Ordinal) && !textOutput)
                Console.Error.WriteLine(payload);
            else
                EmitPayload(payload, outPath);
            return exitCode;
        }

        private static int RunRegistryGame(Games.IGameIntegration game, List<string> command, string outPath, bool textOutput)
        {
            string refusal = RefuseGameAction(game.GameId, command.Count == 0 ? string.Empty : command[0].ToLowerInvariant());
            if (refusal != null)
                return Fail(refusal);
            string payload;
            int exitCode = Games.GameCommands.Run(game, command, textOutput, out payload);
            if (!string.IsNullOrEmpty(payload))
                EmitPayload(payload, outPath);
            return exitCode;
        }

        private static int RunGameIntegrations(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count > 0)
                return Fail("Unknown games integrations argument(s): " + string.Join(" ", args));

            List<Games.IGameIntegration> games = Games.GameRegistry.All.ToList();
            string payload;
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Game Integrations: " + games.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Games.IGameIntegration game in games)
                {
                    sb.AppendLine("  - " + game.GameId + " | " + game.DisplayName +
                                  " | aliases=" + string.Join(",", game.Aliases) +
                                  " | experimental=" + (game.Experimental ? "true" : "false") +
                                  " | refresh-db=" + (game.SupportsCatalogRefresh ? "true" : "false"));
                    if (!string.IsNullOrWhiteSpace(game.Notes))
                        sb.AppendLine("      " + game.Notes);
                }
                payload = sb.ToString();
            }
            else
            {
                payload = EngineSerialization.ToJson(new
                {
                    Integrations = games.Select(g => new
                    {
                        g.GameId,
                        g.DisplayName,
                        Aliases = g.Aliases.ToList(),
                        g.DbFolder,
                        g.Experimental,
                        g.Notes,
                        g.SupportsCatalogRefresh,
                        SupportsGameProfile = g is Games.IGameProfileSelection,
                        SupportsAircraft = g is Games.IGameAircraftScope,
                        Actions = Games.GameCommands.Actions
                    }).ToList()
                });
            }

            EmitPayload(payload, outPath);
            return 0;
        }

        // The devices the registry games see: VID:PID, HID product string and DirectInput axes/buttons/hats.
        private static int RunGameDevices(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count > 0)
                return Fail("Unknown games devices argument(s): " + string.Join(" ", args));

            List<string> warnings = new List<string>();
            Games.GameDeviceDirectory directory = Games.GameDeviceDirectory.Discover(warnings);
            foreach (Games.GameDevice device in directory.Devices)
                directory.EnsureCapabilities(device, warnings);

            string payload;
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Game Devices: " + directory.Devices.Count.ToString(CultureInfo.InvariantCulture));
                foreach (Games.GameDevice device in directory.Devices)
                {
                    sb.AppendLine("  - " + device.JoyProName);
                    sb.AppendLine("      index=" + device.EnumerationIndex.ToString(CultureInfo.InvariantCulture) +
                                  " | vid:pid=" + (device.HasVidPid ? Games.GameDeviceDirectory.VidPidKey(device.VendorId, device.ProductId) : "?") +
                                  " | hid=\"" + device.HidProductString + "\"");
                    sb.AppendLine(device.CapabilitiesKnown
                        ? "      axes=" + string.Join(",", device.AxisSlots.Select(Games.GameInputTokens.AxisToken)) +
                          " | buttons=" + device.ButtonCount.ToString(CultureInfo.InvariantCulture) +
                          " | hats=" + device.PovCount.ToString(CultureInfo.InvariantCulture) +
                          (device.HidPath.Length > 0 ? Environment.NewLine + "      path=" + device.HidPath : string.Empty)
                        : "      capabilities unknown");
                }
                if (warnings.Count > 0)
                {
                    sb.AppendLine("Warnings:");
                    foreach (string warning in warnings)
                        sb.AppendLine("  - " + warning);
                }
                payload = sb.ToString();
            }
            else
            {
                payload = EngineSerialization.ToJson(new
                {
                    Devices = directory.Devices.Select(d => new
                    {
                        d.JoyProName,
                        d.EnumerationIndex,
                        VidPid = d.HasVidPid ? Games.GameDeviceDirectory.VidPidKey(d.VendorId, d.ProductId) : string.Empty,
                        d.HidProductString,
                        d.CapabilitiesKnown,
                        Axes = d.AxisSlots.Select(Games.GameInputTokens.AxisToken).ToList(),
                        Buttons = d.ButtonCount,
                        Hats = d.PovCount,
                        d.HidPath
                    }).ToList(),
                    Warnings = warnings
                });
            }

            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunGames(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "paths" : command[0].ToLowerInvariant();
            if (action == "integrations")
                return RunGameIntegrations(command.Skip(1).ToList(), outPath, textOutput);
            if (action == "installs" || action == "install" || action == "config")
                return RunGameInstalls(action, command.Skip(1).ToList(), outPath, textOutput);
            if (action == "devices")
                return RunGameDevices(command.Skip(1).ToList(), outPath, textOutput);
            if (action == "support")
            {
                string supportPayload;
                int supportExit;
                try
                {
                    supportExit = EngineGameSupport.Run(command.Skip(1).ToList(), textOutput, out supportPayload);
                }
                catch (ArgumentException ex)
                {
                    return Fail(ex.Message);
                }
                EmitPayload(supportPayload, outPath);
                return supportExit;
            }
            if (action != "paths" && action != "list" && action != "detect")
                return Fail("Unknown games action: " + action);

            List<string> args = command.Skip(1).ToList();
            List<string> gameOptions = PopOptionValues(args, "--game");
            gameOptions.AddRange(PopOptionValues(args, "--games"));
            if (args.Count > 0)
                return Fail("Unknown games argument(s): " + string.Join(" ", args));

            List<string> gameFilters = ParseDelimitedOptionValues(gameOptions);
            GamePathCatalog catalog = GamePathDiscovery.DiscoverAll();
            if (gameFilters.Count > 0)
            {
                catalog.Games = (catalog.Games ?? new List<GamePathEntry>())
                    .Where(g => g != null && gameFilters.Any(filter => GameMatchesFilter(g.Game, filter)))
                    .OrderBy(g => g.Game, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            string payload = textOutput ? ToText(catalog) : EngineSerialization.ToJson(catalog);
            EmitPayload(payload, outPath);
            return 0;
        }

        // engine games installs [--game <id>]: which games are installed and where (Steam, known places, Windows'
        // apps list, or the user's folder). engine games install set <game> <folder> | clear|unset <game>: the user's
        // folder. engine games install disable|enable <game> [<game> ...]: "I do not have this game" / "I have it".
        // engine games config set <game> <path> | clear|unset <game>: the user's config location of the game.
        private static int RunGameInstalls(string action, List<string> args, string outPath, bool textOutput)
        {
            List<string> warnings = new List<string>();
            if (action == "install" || action == "config")
            {
                string verb = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
                if (verb == "unset")
                    verb = "clear";
                string usage = action == "install"
                    ? "engine games install set <game> <folder> | clear <game> | disable <game> [<game> ...] | enable <game> [<game> ...]"
                    : "engine games config set <game> <file or folder> | clear <game>";
                if (action == "install" && (verb == "disable" || verb == "enable"))
                {
                    List<string> games = ParseDelimitedOptionValues(args.Skip(1).ToList());
                    if (games.Count == 0)
                        return Fail(usage);
                    string unknown = games.FirstOrDefault(g => !GameInstalls.GameIds.Contains(GameInstalls.Resolve(g)));
                    if (unknown != null)
                        return Fail("Unknown game: " + unknown + ". Known: " + string.Join(", ", GameInstalls.GameIds));
                    List<GameInstall> toggled = GameInstalls.SetDisabled(games, verb == "disable", warnings);
                    EmitPayload(textOutput ? GameInstalls.ToText(toggled) : EngineSerialization.ToJson(new { Games = toggled, Warnings = warnings }), outPath);
                    return 0;
                }
                if ((verb != "set" && verb != "clear") || args.Count < 2 || (verb == "set" && args.Count < 3) || args.Count > (verb == "set" ? 3 : 2))
                    return Fail(usage);
                string game = GameInstalls.Resolve(args[1]);
                if (game.Length == 0 || !GameInstalls.GameIds.Contains(game))
                    return Fail("Unknown game: " + args[1] + ". Known: " + string.Join(", ", GameInstalls.GameIds));
                string folder = verb == "set" ? args[2] : null;
                GameInstall changed;
                if (action == "install")
                {
                    // A folder, or the game's executable / launcher (its folder counts; GameInstalls.ResolveInstallFolder).
                    if (folder != null && !Directory.Exists(folder.Trim().Trim('"')) && !File.Exists(folder.Trim().Trim('"')))
                        return Fail("The folder does not exist: " + folder);
                    changed = GameInstalls.SetOverride(game, folder, warnings);
                }
                else
                {
                    if (folder != null && !Directory.Exists(folder) && !File.Exists(folder))
                        return Fail("The file or folder does not exist: " + folder);
                    try
                    {
                        changed = GameInstalls.SetConfigOverride(game, folder, warnings);
                    }
                    catch (ArgumentException ex)
                    {
                        return Fail(ex.Message);
                    }
                }
                changed.ConfigPaths = GameInstalls.ConfigPaths(changed.GameId, warnings);
                List<GameInstall> one = new List<GameInstall> { changed };
                EmitPayload(textOutput
                    ? GameInstalls.ToText(one) + string.Concat(warnings.Distinct().Select(w => Environment.NewLine + "  Note: " + w))
                    : EngineSerialization.ToJson(new { Games = one, Warnings = warnings }), outPath);
                return 0;
            }

            List<string> filters = ParseDelimitedOptionValues(PopOptionValues(args, "--game").Concat(PopOptionValues(args, "--games")).ToList());
            if (args.Count > 0)
                return Fail("Unknown games installs argument(s): " + string.Join(" ", args));
            List<GameInstall> installs = GameInstalls.All(warnings)
                .Where(i => filters.Count == 0 || filters.Any(f => GameInstalls.Resolve(f) == i.GameId))
                .ToList();
            foreach (GameInstall install in installs)
                install.ConfigPaths = GameInstalls.ConfigPaths(install.GameId, warnings);
            EmitPayload(textOutput ? GameInstalls.ToText(installs) : EngineSerialization.ToJson(new { Games = installs, Warnings = warnings }), outPath);
            return 0;
        }

        private static int RunLayout(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "render" : command[0].ToLowerInvariant();
            if (action == "inspect-template" || action == "inspect")
                action = "template-inspect";
            if (action == "save-template")
                action = "template-save";
            if (action != "render" &&
                action != "suggest" &&
                action != "identify" &&
                action != "callouts" &&
                action != "titles" &&
                action != "image" &&
                action != "multiview" &&
                action != "template-inspect" &&
                action != "template-save")
                return Fail("Unknown layout action: " + action);

            List<string> args = command.Skip(1).ToList();
            if (action == "image")
                return RunLayoutImage(args, outPath, textOutput);
            if (action == "multiview")
                return RunLayoutMultiView(args, outPath, textOutput);
            if (action == "callouts")
                return RunLayoutCallouts(args, outPath, textOutput);
            if (action == "titles")
                return RunLayoutTitles(args, outPath, textOutput);
            if (action == "identify")
            {
                List<string> identifyOptions = PopOptionValues(args, "--device");
                identifyOptions.AddRange(PopOptionValues(args, "--devices"));
                bool identifyConnected = PopFlag(args, "--connected");
                if (args.Count > 0)
                    return Fail("Unknown layout identify argument(s): " + string.Join(" ", args));

                List<string> identifyWarnings = new List<string>();
                Games.GameDeviceDirectory directory = Games.GameDeviceDirectory.Discover(identifyWarnings);
                List<string> identifyDevices = ParseDelimitedOptionValues(identifyOptions);
                if (identifyConnected || identifyDevices.Count == 0)
                    identifyDevices.AddRange(directory.Devices.Select(d => d.JoyProName));
                List<DeviceModelIdentity> identities = identifyDevices
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(d => DeviceModelIdentity.Resolve(d, directory))
                    .ToList();
                string identifyPayload = textOutput
                    ? DeviceModelIdentity.ToText(identities)
                    : EngineSerialization.ToJson(new { Devices = identities, Warnings = identifyWarnings });
                EmitPayload(identifyPayload, outPath);
                return 0;
            }

            if (action == "template-inspect")
            {
                string sourcePath = PopOptionValue(args, "--source");
                if (string.IsNullOrWhiteSpace(sourcePath))
                    sourcePath = PopOptionValue(args, "--template");
                if (string.IsNullOrWhiteSpace(sourcePath) &&
                    args.Count > 0 &&
                    !args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    sourcePath = args[0];
                    args.RemoveAt(0);
                }

                int maxMarkers = PopOptionInt(args, "--max-markers", 2000);
                if (args.Count > 0)
                    return Fail("Unknown layout template-inspect argument(s): " + string.Join(" ", args));
                if (string.IsNullOrWhiteSpace(sourcePath))
                    return Fail("layout template-inspect requires --source <path.layout-or-image>.");

                LayoutTemplateInspectResult inspectResult = EngineLayoutTemplateEditor.Inspect(NormalizePath(sourcePath), maxMarkers);
                if (inspectResult.Errors.Count > 0)
                    return Fail("layout template-inspect failed: " + string.Join(" | ", inspectResult.Errors));

                string inspectPayload = textOutput
                    ? EngineLayoutTemplateEditor.ToText(inspectResult)
                    : EngineSerialization.ToJson(inspectResult);
                EmitPayload(inspectPayload, outPath);
                return 0;
            }

            if (action == "template-save")
            {
                string sourcePath = PopOptionValue(args, "--source");
                if (string.IsNullOrWhiteSpace(sourcePath))
                    sourcePath = PopOptionValue(args, "--template");
                if (string.IsNullOrWhiteSpace(sourcePath) &&
                    args.Count > 0 &&
                    !args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    sourcePath = args[0];
                    args.RemoveAt(0);
                }

                string outputPath = PopOptionValue(args, "--out");
                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = PopOptionValue(args, "--output");
                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = PopOptionValue(args, "--path");
                if (string.IsNullOrWhiteSpace(outputPath) && !string.IsNullOrWhiteSpace(outPath))
                {
                    // Global --out is parsed before sub-command dispatch. For template-save,
                    // treat that value as the template output path when no action-level output was supplied.
                    outputPath = outPath;
                    outPath = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(outputPath) &&
                    args.Count > 0 &&
                    !args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    outputPath = args[0];
                    args.RemoveAt(0);
                }

                string markersJsonPath = PopOptionValue(args, "--markers-json");
                string markersJsonInline = PopOptionValue(args, "--markers");
                string tablesJsonPath = PopOptionValue(args, "--tables-json");
                string tablesJsonInline = PopOptionValue(args, "--tables");
                string connectorsJsonPath = PopOptionValue(args, "--connectors-json");
                string connectorsJsonInline = PopOptionValue(args, "--connectors");
                bool replaceAll = PopOptionBoolean(args, "--replace-all", true, out _);

                if (args.Count > 0)
                    return Fail("Unknown layout template-save argument(s): " + string.Join(" ", args));
                if (string.IsNullOrWhiteSpace(sourcePath))
                    return Fail("layout template-save requires --source <path.layout-or-image>.");
                if (string.IsNullOrWhiteSpace(outputPath))
                    return Fail("layout template-save requires --out <path.layout>.");

                string markersPayload = (markersJsonInline ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(markersPayload) && !string.IsNullOrWhiteSpace(markersJsonPath))
                {
                    string normalizedMarkersPath = NormalizePath(markersJsonPath);
                    if (string.IsNullOrWhiteSpace(normalizedMarkersPath) || !File.Exists(normalizedMarkersPath))
                        return Fail("markers json file not found: " + markersJsonPath);
                    markersPayload = File.ReadAllText(normalizedMarkersPath);
                }

                string tablesPayload = (tablesJsonInline ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tablesPayload) && !string.IsNullOrWhiteSpace(tablesJsonPath))
                {
                    string normalizedTablesPath = NormalizePath(tablesJsonPath);
                    if (string.IsNullOrWhiteSpace(normalizedTablesPath) || !File.Exists(normalizedTablesPath))
                        return Fail("tables json file not found: " + tablesJsonPath);
                    tablesPayload = File.ReadAllText(normalizedTablesPath);
                }

                string connectorsPayload = (connectorsJsonInline ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(connectorsPayload) && !string.IsNullOrWhiteSpace(connectorsJsonPath))
                {
                    string normalizedConnectorsPath = NormalizePath(connectorsJsonPath);
                    if (string.IsNullOrWhiteSpace(normalizedConnectorsPath) || !File.Exists(normalizedConnectorsPath))
                        return Fail("connectors json file not found: " + connectorsJsonPath);
                    connectorsPayload = File.ReadAllText(normalizedConnectorsPath);
                }

                if (!TryParseLayoutTemplatePayload(
                    markersPayload,
                    tablesPayload,
                    connectorsPayload,
                    out List<LayoutTemplateMarkerDto> markers,
                    out List<LayoutTemplateTableDto> tables,
                    out List<LayoutTemplateConnectorDto> connectors,
                    out string markerParseError))
                    return Fail("layout template-save invalid markers payload: " + markerParseError);

                LayoutTemplateSaveResult saveResult = EngineLayoutTemplateEditor.Save(
                    NormalizePath(sourcePath),
                    NormalizePath(outputPath),
                    markers,
                    tables,
                    connectors,
                    replaceAll);
                if (saveResult.Errors.Count > 0)
                    return Fail("layout template-save failed: " + string.Join(" | ", saveResult.Errors));

                string savePayload = textOutput
                    ? EngineLayoutTemplateEditor.ToText(saveResult)
                    : EngineSerialization.ToJson(saveResult);
                EmitPayload(savePayload, outPath);
                return 0;
            }

            if (action == "suggest")
            {
                List<string> suggestJoystickOptions = PopOptionValues(args, "--device");
                suggestJoystickOptions.AddRange(PopOptionValues(args, "--devices"));
                suggestJoystickOptions.AddRange(PopOptionValues(args, "--joystick"));
                suggestJoystickOptions.AddRange(PopOptionValues(args, "--joysticks"));
                bool includeConnected = PopFlag(args, "--connected");
                bool includeProfileDevices = PopFlag(args, "--profile-devices");

                List<string> legacyLayoutDirs = PopOptionValues(args, "--legacy-layout-dir");
                legacyLayoutDirs.AddRange(PopOptionValues(args, "--legacy-layout-dirs"));
                List<string> legacyOutputDirs = PopOptionValues(args, "--legacy-output-dir");
                legacyOutputDirs.AddRange(PopOptionValues(args, "--legacy-output-dirs"));
                string catalogPath = PopOptionValue(args, "--catalog");
                if (string.IsNullOrWhiteSpace(catalogPath))
                    catalogPath = PopOptionValue(args, "--catalog-path");
                bool apply = PopOptionBoolean(args, "--apply", false, out _);
                int minScoreToApply = PopOptionInt(args, "--apply-min-score", 55);
                string backdrop = PopOptionValue(args, "--backdrop");
                if (args.Count > 0)
                    return Fail("Unknown layout suggest argument(s): " + string.Join(" ", args));

                List<string> suggestJoystickFilters = ParseDelimitedOptionValues(suggestJoystickOptions);
                LayoutSuggestResult suggestResult = EngineLayoutSuggestion.Suggest(
                    suggestJoystickFilters,
                    includeConnected,
                    includeProfileDevices,
                    ParseDelimitedOptionValues(legacyLayoutDirs),
                    ParseDelimitedOptionValues(legacyOutputDirs),
                    NormalizePath(catalogPath),
                    apply,
                    minScoreToApply,
                    backdrop);

                if (suggestResult.Errors.Count > 0)
                    return Fail("layout suggest failed: " + string.Join(" | ", suggestResult.Errors));

                string suggestPayload = textOutput ? EngineLayoutSuggestion.ToText(suggestResult) : EngineSerialization.ToJson(suggestResult);
                EmitPayload(suggestPayload, outPath);
                return 0;
            }

            string exportPath = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(exportPath))
                exportPath = PopOptionValue(args, "--export-path");
            if (string.IsNullOrWhiteSpace(exportPath))
                exportPath = InternalDataManagement.JoystickLayoutExport;
            if (string.IsNullOrWhiteSpace(exportPath))
                exportPath = Path.Combine(InvocationDirectory, "LayoutExport");

            List<string> joystickOptions = PopOptionValues(args, "--device");
            joystickOptions.AddRange(PopOptionValues(args, "--devices"));
            joystickOptions.AddRange(PopOptionValues(args, "--joystick"));
            joystickOptions.AddRange(PopOptionValues(args, "--joysticks"));
            List<string> gameOptions = PopOptionValues(args, "--game");
            gameOptions.AddRange(PopOptionValues(args, "--games"));
            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            List<string> layoutMapOptions = PopOptionValues(args, "--layout-map");
            layoutMapOptions.AddRange(PopOptionValues(args, "--layout-maps"));
            bool includeOverview = PopOptionBoolean(args, "--include-overview", true, out _);
            bool includePlaneExports = PopOptionBoolean(args, "--include-planes", true, out _);
            bool skipMissingLayouts = PopOptionBoolean(args, "--skip-missing-layouts", true, out _);
            bool drawMarkers = PopOptionBoolean(args, "--draw-markers", false, out _);
            bool drawLegendTable = PopOptionBoolean(args, "--draw-legend-table", false, out _);
            int markerSize = PopOptionInt(args, "--marker-size", 10);
            int legendTableWidth = PopOptionInt(args, "--legend-width", 460);
            int legendMaxRows = PopOptionInt(args, "--legend-max-rows", 20);
            float legendFontSize = PopOptionFloat(args, "--legend-font-size", 11f);
            string modifierColors = PopOptionValue(args, "--modifier-colors");
            string buttonSymbol = PopOptionValue(args, "--button-symbol");
            string axisSymbol = PopOptionValue(args, "--axis-symbol");
            string povSymbol = PopOptionValue(args, "--pov-symbol");
            string sliderSymbol = PopOptionValue(args, "--slider-symbol");
            string labelFont = PopOptionValue(args, "--font");
            float labelFontSize = PopOptionFloat(args, "--font-size", 0f);
            string labelTextColor = PopOptionValue(args, "--text-color");
            string labelOutlineColor = PopOptionValue(args, "--outline-color");
            string labelBackground = PopOptionValue(args, "--label-background");
            string sheetStyle = PopOptionValue(args, "--style");
            int printWidth = PopOptionInt(args, "--print-width", 3508);
            bool combinePages = PopOptionBoolean(args, "--combine", false, out _);
            // JoyPro's drawings in the sheets: the drawing style of this run, else the user's (GUI settings).
            JoyPro.DeviceReference.DrawingStyle drawingStyle = JoyPro.DeviceReference.DrawingStyle.FromOptions(args,
                JoyPro.DeviceReference.DrawingStyle.FromSettings(JoyPro.DeviceReference.DeviceCatalog.DataRoot(), true));
            if (drawingStyle != null)
                EngineCalloutRenderer.ExportStyle = drawingStyle;

            if (args.Count > 0)
                return Fail("Unknown layout render argument(s): " + string.Join(" ", args));
            if (!string.IsNullOrWhiteSpace(sheetStyle) && !string.Equals(sheetStyle, "dark", StringComparison.OrdinalIgnoreCase) && !string.Equals(sheetStyle, "light", StringComparison.OrdinalIgnoreCase))
                return Fail("Invalid --style value '" + sheetStyle + "'. Use dark or light.");
            if (!includeOverview && !includePlaneExports)
                return Fail("layout render requires at least one output mode (--include-overview true and/or --include-planes true).");

            List<string> joystickFilters = ParseDelimitedOptionValues(joystickOptions);
            List<string> gameFilters = ParseDelimitedOptionValues(gameOptions);
            List<string> planeFilters = ParseDelimitedOptionValues(planeOptions);

            if (!EngineLayoutRenderer.TryParseLayoutMapSpecs(
                ParseDelimitedOptionValues(layoutMapOptions),
                out Dictionary<string, string> layoutMapOverrides,
                out string mapError))
                return Fail(mapError);

            LayoutRenderResult result = EngineLayoutRenderer.Render(
                NormalizePath(exportPath),
                joystickFilters,
                gameFilters,
                planeFilters,
                layoutMapOverrides,
                includeOverview,
                includePlaneExports,
                skipMissingLayouts,
                new LayoutVisualOptions
                {
                    DrawMarkers = drawMarkers,
                    DrawLegendTable = drawLegendTable,
                    MarkerSize = markerSize,
                    LegendTableWidth = legendTableWidth,
                    LegendMaxRows = legendMaxRows,
                    LegendFontSize = legendFontSize,
                    ModifierColorCsv = modifierColors ?? string.Empty,
                    ButtonSymbol = string.IsNullOrWhiteSpace(buttonSymbol) ? "circle" : buttonSymbol.Trim(),
                    AxisSymbol = string.IsNullOrWhiteSpace(axisSymbol) ? "diamond" : axisSymbol.Trim(),
                    PovSymbol = string.IsNullOrWhiteSpace(povSymbol) ? "triangle" : povSymbol.Trim(),
                    FontFamily = labelFont ?? string.Empty,
                    FontSize = labelFontSize,
                    TextColor = labelTextColor ?? string.Empty,
                    OutlineColor = labelOutlineColor ?? string.Empty,
                    LabelBackground = labelBackground ?? string.Empty,
                    SliderSymbol = string.IsNullOrWhiteSpace(sliderSymbol) ? "bar" : sliderSymbol.Trim(),
                    LightStyle = string.Equals(sheetStyle, "light", StringComparison.OrdinalIgnoreCase),
                    PrintWidth = Math.Max(0, Math.Min(12000, printWidth)),
                    Combine = combinePages
                });

            if (result.Errors.Count > 0)
                return Fail("layout render failed: " + string.Join(" | ", result.Errors));

            string payload = textOutput ? EngineLayoutRenderer.ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDcs(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "scan" : command[0].ToLowerInvariant();
            if (action == "ffb" || action == "force-feedback" || action == "forcefeedback")
                return RunDcsFfb(command, outPath, textOutput);
            string refusal = RefuseGameAction("DCS", action);
            if (refusal != null)
                return Fail(refusal);
            if (action == "defaults" || action == "stick-defaults" || action == "stickdefaults")
                return RunDcsDefaults(command, outPath, textOutput);
            if (action == "strip-defaults" || action == "device-luas" || action == "device-files")
                return RunDcsInstallDefaults(action, command, outPath, textOutput);
            if (action == "verify-clean" || action == "check-defaults")
                return RunDcsVerifyClean(command, outPath, textOutput);
            if (action == "refresh" || action == "refresh-db")
                return RunDcsRefresh(command, outPath, textOutput);

            List<string> args = command.Skip(1).ToList();
            bool merge = PopFlag(args, "--merge");
            bool saveLast = PopFlag(args, "--save-last");
            string saveProfile = PopOptionValue(args, "--save-profile");
            string mode = PopOptionValue(args, "--mode");
            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            List<string> ignoredDeviceOptions = PopOptionValues(args, "--ignore-device");
            ignoredDeviceOptions.AddRange(PopOptionValues(args, "--ignore-devices"));
            bool includeKeyboard = PopOptionBoolean(args, "--include-keyboard", true, out bool includeKeyboardProvided);
            includeKeyboard = PopOptionBoolean(args, "--import-keyboard", includeKeyboard, out bool importKeyboardProvided);
            bool ignoreEasyPlanes = PopOptionBoolean(args, "--ignore-easy-planes", false, out bool ignoreEasyPlanesProvided);
            if (PopFlag(args, "--ignore-easy"))
            {
                ignoreEasyPlanes = true;
                ignoreEasyPlanesProvided = true;
            }
            bool keepKeyboardDefaults = PopOptionBoolean(args, "--keep-keyboard-defaults", true, out bool keepKeyboardDefaultsProvided);
            bool nukeUntouchedDevices = PopOptionBoolean(args, "--nuke-untouched-devices", false, out bool nukeUntouchedDevicesProvided);
            bool cleanAllAircraft = PopOptionBoolean(args, "--clean-all-aircraft", true, out bool cleanAllAircraftProvided);
            bool includeDefaults = PopFlag(args, "--include-defaults");
            includeDefaults = includeDefaults || PopFlag(args, "--import-default");
            bool importInverted = PopFlag(args, "--import-inverted");
            bool importSlider = PopFlag(args, "--import-slider");
            bool importCurvature = PopFlag(args, "--import-curvature");
            bool importDeadzone = PopFlag(args, "--import-deadzone");
            bool importSaturationX = PopFlag(args, "--import-saturation-x") || PopFlag(args, "--import-satx");
            bool importSaturationY = PopFlag(args, "--import-saturation-y") || PopFlag(args, "--import-saty");
            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out bool confirmProvided);

            string path = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                path = args[0];
                args.RemoveAt(0);
            }

            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(path))
                path = pathOption;

            bool hasImportOnlyOptions =
                (planeOptions.Count > 0 && action != "export") ||
                deviceOptions.Count > 0 ||
                ignoredDeviceOptions.Count > 0 ||
                includeDefaults ||
                importInverted ||
                importSlider ||
                importCurvature ||
                importDeadzone ||
                importSaturationX ||
                importSaturationY ||
                includeKeyboardProvided ||
                importKeyboardProvided ||
                ignoreEasyPlanesProvided;
            bool hasExportOnlyOptions =
                !string.IsNullOrWhiteSpace(mode) ||
                keepKeyboardDefaultsProvided ||
                nukeUntouchedDevicesProvided ||
                cleanAllAircraftProvided;

            if (args.Count > 0)
                return Fail("Unknown dcs argument(s): " + string.Join(" ", args));
            if (string.IsNullOrWhiteSpace(path))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredDcs = GamePathDiscovery.DiscoverDcs(discoveryWarnings);
                if (!string.IsNullOrWhiteSpace(discoveredDcs.PreferredConfigPath))
                {
                    path = discoveredDcs.PreferredConfigPath;
                    Console.Error.WriteLine("Auto-detected DCS Saved Games path: " + path);
                }
                else
                {
                    for (int i = 0; i < discoveryWarnings.Count; i++)
                        Console.Error.WriteLine(discoveryWarnings[i]);
                }
            }
            if (string.IsNullOrWhiteSpace(path))
                return Fail("dcs " + action + " requires a DCS Saved Games path. Try: engine games paths --game DCS --text");
            if (action != "import" && (saveLast || !string.IsNullOrWhiteSpace(saveProfile)))
                return Fail("save options are only valid with 'dcs import'.");
            if (action != "export" && hasExportOnlyOptions)
                return Fail("--mode, --keep-keyboard-defaults, --nuke-untouched-devices and --clean-all-aircraft are only valid with 'dcs export'.");
            if (action == "export" && hasImportOnlyOptions)
                return Fail("DCS import filter/settings options are only valid with 'dcs scan' or 'dcs import'.");
            if (action == "export" && confirmProvided)
                return Fail("--confirm is only valid with 'dcs scan' or 'dcs import'.");

            path = NormalizePath(path);
            EngineDcsImportOptions importOptions = new EngineDcsImportOptions();
            importOptions.PlaneFilter = NormalizeDcsPlaneFilterTokens(ParseDelimitedOptionValues(planeOptions));
            List<string> requestedDeviceFilters = ParseDelimitedOptionValues(deviceOptions);
            List<string> requestedIgnoredDeviceFilters = ParseDelimitedOptionValues(ignoredDeviceOptions);
            bool hasRequestedDeviceResolution = requestedDeviceFilters.Count > 0 || requestedIgnoredDeviceFilters.Count > 0;
            if ((action == "scan" || action == "import") && hasRequestedDeviceResolution)
            {
                List<string> deviceResolutionWarnings = new List<string>();
                List<DeviceCandidate> candidates = BuildDcsDeviceCandidates(path, deviceResolutionWarnings);
                for (int i = 0; i < deviceResolutionWarnings.Count; i++)
                    Console.Error.WriteLine(deviceResolutionWarnings[i]);

                if (!TryResolveDeviceFilters(requestedDeviceFilters, candidates, confirmSuggestions, "DCS", out List<string> resolvedFilters, out string resolveError))
                    return Fail(resolveError);
                importOptions.DeviceFilter = resolvedFilters;

                if (!TryResolveDeviceFilters(requestedIgnoredDeviceFilters, candidates, confirmSuggestions, "DCS", out List<string> resolvedIgnoredFilters, out resolveError))
                    return Fail(resolveError);
                importOptions.IgnoreDeviceFilter = resolvedIgnoredFilters;
            }
            else
            {
                importOptions.DeviceFilter = requestedDeviceFilters;
                importOptions.IgnoreDeviceFilter = requestedIgnoredDeviceFilters;
            }
            importOptions.IncludeDefaults = includeDefaults;
            importOptions.IncludeKeyboard = includeKeyboard;
            importOptions.IgnoreEasyPlanes = ignoreEasyPlanes;
            importOptions.ImportInverted = importInverted;
            importOptions.ImportSlider = importSlider;
            importOptions.ImportCurvature = importCurvature;
            importOptions.ImportDeadzone = importDeadzone;
            importOptions.ImportSaturationX = importSaturationX;
            importOptions.ImportSaturationY = importSaturationY;
            importOptions.StatusSink = BuildStatusSink("dcs " + action);

            EngineDcsImportResult result;
            switch (action)
            {
                case "scan":
                    result = EngineDcsImport.Scan(path, importOptions);
                    break;
                case "import":
                    result = EngineDcsImport.Import(path, merge, importOptions);
                    if (result.Errors == null || result.Errors.Count == 0)
                    {
                        if (!string.IsNullOrWhiteSpace(saveProfile))
                        {
                            string target = NormalizePath(saveProfile);
                            InternalDataManagement.SaveProfileTo(target);
                        }
                        if (saveLast)
                        {
                            MainStructure.SaveMetaLast();
                        }
                    }
                    break;
                case "export":
                    {
                        string runningDcs = RefuseWhileRunning("DCS World", new[] { "DCS" });
                        if (runningDcs != null)
                            return Fail(runningDcs);
                        EngineDcsExportOptions exportOptions = new EngineDcsExportOptions();
                        exportOptions.Mode = string.IsNullOrWhiteSpace(mode) ? "merge-overwrite" : mode;
                        exportOptions.KeepKeyboardDefaults = keepKeyboardDefaults;
                        exportOptions.NukeUntouchedDevices = nukeUntouchedDevices;
                        exportOptions.CleanAllAircraft = cleanAllAircraft;
                        exportOptions.Planes = ParseDelimitedOptionValues(planeOptions);
                        exportOptions.StatusSink = BuildStatusSink("dcs export");
                        JoyPro.Logging.JoyProLog.Info("Export", "DCS export to " + path + ": mode=" + exportOptions.Mode + ", keep-keyboard-defaults=" + keepKeyboardDefaults +
                                                                ", nuke-untouched-devices=" + nukeUntouchedDevices + ", clean-all-aircraft=" + cleanAllAircraft +
                                                                ", planes=" + (exportOptions.Planes == null || exportOptions.Planes.Count == 0 ? "(all)" : string.Join(",", exportOptions.Planes)));
                        EngineDcsExportResult export = EngineDcsExport.ExportToSavedGames(path, exportOptions);
                        JoyPro.Logging.JoyProLog.Info("Export", "DCS export finished: " + (export.Errors == null ? 0 : export.Errors.Count).ToString(CultureInfo.InvariantCulture) + " error(s), " +
                                                                (export.Warnings == null ? 0 : export.Warnings.Count).ToString(CultureInfo.InvariantCulture) + " warning(s)");
                        if (export.Errors == null || export.Errors.Count == 0)
                            EngineSync.RecordAfterExport("DCS", path, export.Warnings);
                        string exportPayload = textOutput
                            ? EngineDcsExport.ToText(export)
                            : EngineSerialization.ToJson(export);
                        EmitPayload(exportPayload, outPath);

                        if (export.Errors != null && export.Errors.Count > 0)
                            return Fail("dcs export failed: " + string.Join(" | ", export.Errors));
                        return 0;
                    }
                default:
                    return Fail("Unknown dcs action: " + action);
            }

            string payload = textOutput
                ? EngineDcsImport.ToText(result)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);

            if (result.Errors != null && result.Errors.Count > 0)
                return Fail("dcs " + action + " failed: " + string.Join(" | ", result.Errors));

            return 0;
        }

        private static int RunIl2(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "scan" : command[0].ToLowerInvariant();
            if (action == "refresh-db" || action == "refresh")
                return RunDbRefreshIl2(command.Skip(1).ToList(), outPath, textOutput);
            string refusal = RefuseGameAction("IL2Game", action);
            if (refusal != null)
                return Fail(refusal);
            List<string> args = command.Skip(1).ToList();
            bool merge = PopFlag(args, "--merge");
            bool saveLast = PopFlag(args, "--save-last");
            string saveProfile = PopOptionValue(args, "--save-profile");
            string mode = PopOptionValue(args, "--mode");
            bool il2Nuke = PopOptionBoolean(args, "--nuke-untouched-devices", false, out bool il2NukeProvided);
            if (il2NukeProvided && action != "export")
                return Fail("--nuke-untouched-devices is only valid with 'il2 export'.");
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            List<string> ignoredDeviceOptions = PopOptionValues(args, "--ignore-device");
            ignoredDeviceOptions.AddRange(PopOptionValues(args, "--ignore-devices"));
            bool importInverted = PopFlag(args, "--import-inverted");
            bool importCurvature = PopFlag(args, "--import-curvature");
            bool importDeadzone = PopFlag(args, "--import-deadzone");
            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out bool confirmProvided);

            string path = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                path = args[0];
                args.RemoveAt(0);
            }

            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(path))
                path = pathOption;

            bool hasImportOnlyOptions =
                deviceOptions.Count > 0 ||
                ignoredDeviceOptions.Count > 0 ||
                importInverted ||
                importCurvature ||
                importDeadzone;

            if (args.Count > 0)
                return Fail("Unknown il2 argument(s): " + string.Join(" ", args));
            if (string.IsNullOrWhiteSpace(path))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredIl2 = GamePathDiscovery.DiscoverIl2(discoveryWarnings);
                if (!string.IsNullOrWhiteSpace(discoveredIl2.PreferredInstallPath))
                {
                    path = discoveredIl2.PreferredInstallPath;
                    Console.Error.WriteLine("Auto-detected IL2 root path: " + path);
                }
                else
                {
                    for (int i = 0; i < discoveryWarnings.Count; i++)
                        Console.Error.WriteLine(discoveryWarnings[i]);
                }
            }
            if (string.IsNullOrWhiteSpace(path))
                return Fail("il2 " + action + " requires an IL2 root path. Try: engine games paths --game IL2 --text");
            if (action != "import" && (saveLast || !string.IsNullOrWhiteSpace(saveProfile)))
                return Fail("save options are only valid with 'il2 import'.");
            if (action != "export" && !string.IsNullOrWhiteSpace(mode))
                return Fail("--mode is only valid with 'il2 export'.");
            if (action == "export" && hasImportOnlyOptions)
                return Fail("IL2 import filter/settings options are only valid with 'il2 scan' or 'il2 import'.");
            if (action == "export" && confirmProvided)
                return Fail("--confirm is only valid with 'il2 scan' or 'il2 import'.");

            path = NormalizePath(path);
            EngineIl2ImportOptions importOptions = new EngineIl2ImportOptions();
            List<string> requestedDeviceFilters = ParseDelimitedOptionValues(deviceOptions);
            List<string> requestedIgnoredDeviceFilters = ParseDelimitedOptionValues(ignoredDeviceOptions);
            bool hasRequestedDeviceResolution = requestedDeviceFilters.Count > 0 || requestedIgnoredDeviceFilters.Count > 0;
            if ((action == "scan" || action == "import") && hasRequestedDeviceResolution)
            {
                List<string> deviceResolutionWarnings = new List<string>();
                List<DeviceCandidate> candidates = BuildIl2DeviceCandidates(path, deviceResolutionWarnings);
                for (int i = 0; i < deviceResolutionWarnings.Count; i++)
                    Console.Error.WriteLine(deviceResolutionWarnings[i]);

                if (!TryResolveDeviceFilters(requestedDeviceFilters, candidates, confirmSuggestions, "IL2", out List<string> resolvedFilters, out string resolveError))
                    return Fail(resolveError);
                importOptions.DeviceFilter = resolvedFilters;

                if (!TryResolveDeviceFilters(requestedIgnoredDeviceFilters, candidates, confirmSuggestions, "IL2", out List<string> resolvedIgnoredFilters, out resolveError))
                    return Fail(resolveError);
                importOptions.IgnoreDeviceFilter = resolvedIgnoredFilters;
            }
            else
            {
                importOptions.DeviceFilter = requestedDeviceFilters;
                importOptions.IgnoreDeviceFilter = requestedIgnoredDeviceFilters;
            }
            importOptions.ImportInverted = importInverted;
            importOptions.ImportCurvature = importCurvature;
            importOptions.ImportDeadzone = importDeadzone;
            importOptions.StatusSink = BuildStatusSink("il2 " + action);

            switch (action)
            {
                case "scan":
                    {
                        EngineIl2ImportResult scan = EngineIl2.Scan(path, importOptions);
                        string scanPayload = textOutput
                            ? EngineIl2.ToText(scan)
                            : EngineSerialization.ToJson(scan);
                        EmitPayload(scanPayload, outPath);

                        if (scan.Errors != null && scan.Errors.Count > 0)
                            return Fail("il2 scan failed: " + string.Join(" | ", scan.Errors));
                        return 0;
                    }
                case "import":
                    {
                        EngineIl2ImportResult import = EngineIl2.Import(path, merge, importOptions);
                        if (import.Errors == null || import.Errors.Count == 0)
                        {
                            if (!string.IsNullOrWhiteSpace(saveProfile))
                            {
                                string target = NormalizePath(saveProfile);
                                InternalDataManagement.SaveProfileTo(target);
                            }
                            if (saveLast)
                            {
                                MainStructure.SaveMetaLast();
                            }
                        }

                        string importPayload = textOutput
                            ? EngineIl2.ToText(import)
                            : EngineSerialization.ToJson(import);
                        EmitPayload(importPayload, outPath);

                        if (import.Errors != null && import.Errors.Count > 0)
                            return Fail("il2 import failed: " + string.Join(" | ", import.Errors));
                        return 0;
                    }
                case "export":
                    {
                        string runningIl2 = RefuseWhileRunning("IL-2", new[] { "Il-2" });
                        if (runningIl2 != null)
                            return Fail(runningIl2);
                        string exportMode = string.IsNullOrWhiteSpace(mode) ? "merge-overwrite" : mode;
                        EngineIl2ExportResult export = EngineIl2.Export(path, exportMode, BuildStatusSink("il2 export"), il2Nuke);
                        if (export.Errors == null || export.Errors.Count == 0)
                            EngineSync.RecordAfterExport("IL2Game", path, export.Warnings);
                        string exportPayload = textOutput
                            ? EngineIl2.ToText(export)
                            : EngineSerialization.ToJson(export);
                        EmitPayload(exportPayload, outPath);

                        if (export.Errors != null && export.Errors.Count > 0)
                            return Fail("il2 export failed: " + string.Join(" | ", export.Errors));
                        return 0;
                    }
                default:
                    return Fail("Unknown il2 action: " + action);
            }
        }

        private static int RunStarCitizen(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "scan" : command[0].ToLowerInvariant();
            string refusal = RefuseGameAction("StarCitizen", action);
            if (refusal != null)
                return Fail(refusal);
            List<string> args = command.Skip(1).ToList();
            bool merge = PopFlag(args, "--merge");
            bool saveLast = PopFlag(args, "--save-last");
            string saveProfile = PopOptionValue(args, "--save-profile");
            string mode = PopOptionValue(args, "--mode");
            string sourcePath = PopOptionValue(args, "--source");
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            List<string> ignoredDeviceOptions = PopOptionValues(args, "--ignore-device");
            ignoredDeviceOptions.AddRange(PopOptionValues(args, "--ignore-devices"));
            // Keyboard binds are imported and exported only on request (flight/ship actions only).
            bool includeKeyboard = PopOptionBoolean(args, "--include-keyboard", false, out bool includeKeyboardProvided);
            bool keepKeyboardDefaults = PopOptionBoolean(args, "--keep-keyboard-defaults", false, out bool keepKeyboardDefaultsProvided);
            bool preferLocalOrder = PopOptionBoolean(args, "--prefer-local-order", true, out bool preferLocalOrderProvided);
            bool nukeUnusedDevices = PopOptionBoolean(args, "--nuke-untouched-devices", false, out bool nukeProvided);
            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out bool confirmProvided);

            string path = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                path = args[0];
                args.RemoveAt(0);
            }

            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(path))
                path = pathOption;

            bool hasImportOnlyOptions =
                deviceOptions.Count > 0 ||
                ignoredDeviceOptions.Count > 0;
            bool hasExportOnlyOptions =
                !string.IsNullOrWhiteSpace(mode) ||
                keepKeyboardDefaultsProvided ||
                preferLocalOrderProvided ||
                nukeProvided;

            if (args.Count > 0)
                return Fail("Unknown starcitizen argument(s): " + string.Join(" ", args));
            if (action != "import" && (saveLast || !string.IsNullOrWhiteSpace(saveProfile)))
                return Fail("save options are only valid with 'starcitizen import'.");
            if (action != "export" && hasExportOnlyOptions)
                return Fail("--mode, --keep-keyboard-defaults, --prefer-local-order and --nuke-untouched-devices are only valid with 'starcitizen export'.");
            if (action == "export" && hasImportOnlyOptions)
                return Fail("Star Citizen import filter/settings options are only valid with 'starcitizen scan' or 'starcitizen import'.");
            if (action == "export" && confirmProvided)
                return Fail("--confirm is only valid with 'starcitizen scan' or 'starcitizen import'.");
            if ((action == "devices" || action == "list" || action == "list-devices") && hasImportOnlyOptions)
                return Fail("Star Citizen import filter/settings options are not valid with 'starcitizen list-devices'.");

            if (string.IsNullOrWhiteSpace(path))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredSc = GamePathDiscovery.DiscoverStarCitizen(discoveryWarnings);
                if (!string.IsNullOrWhiteSpace(discoveredSc.PreferredConfigPath))
                {
                    path = discoveredSc.PreferredConfigPath;
                    Console.Error.WriteLine("Auto-detected Star Citizen profile path: " + path);
                }
                else if (!string.IsNullOrWhiteSpace(discoveredSc.PreferredInstallPath))
                {
                    path = discoveredSc.PreferredInstallPath;
                    Console.Error.WriteLine("Auto-detected Star Citizen install path: " + path);
                }
                else
                {
                    for (int i = 0; i < discoveryWarnings.Count; i++)
                        Console.Error.WriteLine(discoveryWarnings[i]);
                }
            }

            path = NormalizePath(path);
            sourcePath = NormalizePath(sourcePath);

            switch (action)
            {
                case "scan":
                case "import":
                    {
                        EngineStarCitizenImportOptions importOptions = new EngineStarCitizenImportOptions();
                        List<string> requestedDeviceFilters = ParseDelimitedOptionValues(deviceOptions);
                        List<string> requestedIgnoredDeviceFilters = ParseDelimitedOptionValues(ignoredDeviceOptions);
                        bool hasRequestedDeviceResolution = requestedDeviceFilters.Count > 0 || requestedIgnoredDeviceFilters.Count > 0;
                        if (hasRequestedDeviceResolution)
                        {
                            List<string> deviceResolutionWarnings = new List<string>();
                            List<DeviceCandidate> candidates = BuildStarCitizenDeviceCandidates(path, sourcePath, deviceResolutionWarnings);
                            for (int i = 0; i < deviceResolutionWarnings.Count; i++)
                                Console.Error.WriteLine(deviceResolutionWarnings[i]);

                            if (!TryResolveDeviceFilters(requestedDeviceFilters, candidates, confirmSuggestions, "StarCitizen", out List<string> resolvedFilters, out string resolveError))
                                return Fail(resolveError);
                            importOptions.DeviceFilter = resolvedFilters;

                            if (!TryResolveDeviceFilters(requestedIgnoredDeviceFilters, candidates, confirmSuggestions, "StarCitizen", out List<string> resolvedIgnoredFilters, out resolveError))
                                return Fail(resolveError);
                            importOptions.IgnoreDeviceFilter = resolvedIgnoredFilters;
                        }
                        else
                        {
                            importOptions.DeviceFilter = requestedDeviceFilters;
                            importOptions.IgnoreDeviceFilter = requestedIgnoredDeviceFilters;
                        }

                        importOptions.IncludeKeyboard = includeKeyboard;
                        importOptions.SourcePath = sourcePath;
                        importOptions.StatusSink = BuildStatusSink("starcitizen " + action);

                        EngineStarCitizenImportResult result = action == "scan"
                            ? EngineStarCitizen.Scan(path, importOptions)
                            : EngineStarCitizen.Import(path, merge, importOptions);

                        if (action == "import" && (result.Errors == null || result.Errors.Count == 0))
                        {
                            if (!string.IsNullOrWhiteSpace(saveProfile))
                            {
                                string target = NormalizePath(saveProfile);
                                InternalDataManagement.SaveProfileTo(target);
                            }
                            if (saveLast)
                            {
                                MainStructure.SaveMetaLast();
                            }
                        }

                        string payload = textOutput
                            ? EngineStarCitizen.ToText(result)
                            : EngineSerialization.ToJson(result);
                        EmitPayload(payload, outPath);

                        if (result.Errors != null && result.Errors.Count > 0)
                            return Fail("starcitizen " + action + " failed: " + string.Join(" | ", result.Errors));
                        return 0;
                    }
                case "export":
                    {
                        string runningSc = RefuseWhileRunning("Star Citizen", new[] { "StarCitizen" });
                        if (runningSc != null)
                            return Fail(runningSc);
                        string exportMode = string.IsNullOrWhiteSpace(mode) ? "merge-overwrite" : mode;
                        EngineStarCitizenExportOptions exportOptions = new EngineStarCitizenExportOptions();
                        exportOptions.SourcePath = sourcePath;
                        exportOptions.KeepKeyboardDefaults = keepKeyboardDefaults;
                        exportOptions.PreferLocalOrder = preferLocalOrder;
                        exportOptions.IncludeKeyboard = includeKeyboard;
                        exportOptions.NukeUnusedDevices = nukeUnusedDevices;
                        exportOptions.StatusSink = BuildStatusSink("starcitizen export");

                        EngineStarCitizenExportResult export = EngineStarCitizen.Export(path, exportMode, exportOptions);
                        if ((export.Errors == null || export.Errors.Count == 0) && !string.IsNullOrWhiteSpace(export.ActionmapsFile))
                            EngineSync.RecordAfterExport("StarCitizen", export.ActionmapsFile, export.Warnings);
                        string payload = textOutput
                            ? EngineStarCitizen.ToText(export)
                            : EngineSerialization.ToJson(export);
                        EmitPayload(payload, outPath);

                        if (export.Errors != null && export.Errors.Count > 0)
                            return Fail("starcitizen export failed: " + string.Join(" | ", export.Errors));
                        return 0;
                    }
                case "devices":
                case "list":
                case "list-devices":
                    {
                        List<string> warnings = new List<string>();
                        List<string> devices = EngineStarCitizen.DiscoverInputDevices(path, sourcePath, warnings);
                        StarCitizenDeviceListResult result = new StarCitizenDeviceListResult();
                        result.RootPath = path ?? string.Empty;
                        result.SourcePath = sourcePath ?? string.Empty;
                        result.Devices = devices
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        result.Warnings = warnings;

                        string payload = textOutput
                            ? ToText(result)
                            : EngineSerialization.ToJson(result);
                        EmitPayload(payload, outPath);
                        return 0;
                    }
                default:
                    return Fail("Unknown starcitizen action: " + action);
            }
        }

        private static int EmitRelations(string outPath, bool textOutput, IEnumerable<string> queryTokens)
        {
            string query = JoinQuery(queryTokens);
            EngineRelationsDocument doc = BuildFilteredRelationsDocument(query);
            string payload = textOutput ? EngineSerialization.ToText(doc) : EngineSerialization.ToJson(doc);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int EmitBinds(string outPath, bool textOutput, IEnumerable<string> queryTokens)
        {
            string query = JoinQuery(queryTokens);
            EngineBindsDocument doc = BuildFilteredBindsDocument(query);
            string payload = textOutput ? EngineSerialization.ToText(doc) : EngineSerialization.ToJson(doc);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int ShowRelation(List<string> command, string outPath, bool textOutput)
        {
            if (command.Count < 2)
                return Fail("relations show requires a relation name.");

            string query = string.Join(" ", command.Skip(1)).Trim();
            List<string> relationCandidates;
            string relationName;
            if (!TryResolveRelationName(query, out relationName, out relationCandidates))
            {
                return FailResolution("relation", query, relationCandidates);
            }

            EngineRelationsDocument relationsDoc = EngineSerialization.ExportRelations();
            EngineRelationDto relation = relationsDoc.Relations.FirstOrDefault(r => string.Equals(r.Name, relationName, StringComparison.Ordinal));
            if (relation == null)
                return Fail("Relation not found: " + relationName);

            string payload = textOutput
                ? EngineSerialization.ToText(relation)
                : EngineSerialization.ToJson(relation);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int ShowBind(List<string> command, string outPath, bool textOutput)
        {
            if (command.Count < 2)
                return Fail("binds show requires a bind name or relation name.");

            string query = string.Join(" ", command.Skip(1)).Trim();
            List<string> bindCandidates;
            string relationName;
            if (!TryResolveBindRelationName(query, out relationName, out bindCandidates))
            {
                return FailResolution("bind", query, bindCandidates);
            }

            EngineBindsDocument bindsDoc = EngineSerialization.ExportBinds();
            EngineBindDto bind = bindsDoc.Binds.FirstOrDefault(b => string.Equals(b.RelationName, relationName, StringComparison.Ordinal));
            if (bind == null)
                return Fail("Bind not found for relation: " + relationName);

            EngineRelationsDocument relationsDoc = EngineSerialization.ExportRelations();
            EngineRelationDto relation = relationsDoc.Relations.FirstOrDefault(r => string.Equals(r.Name, relationName, StringComparison.Ordinal));
            if (relation == null)
                return Fail("Related relation not found: " + relationName);

            EngineBindWithRelationDto response = new EngineBindWithRelationDto();
            response.Bind = bind;
            response.Relation = relation;

            string payload = textOutput
                ? EngineSerialization.ToText(response)
                : EngineSerialization.ToJson(response);
            EmitPayload(payload, outPath);
            return 0;
        }

        // relations aircraft <relation> [--node game:id]: every node with every aircraft that has its id, each with its own
        // title and category, on / off / not in the node, and same / near / differs to the node's meaning
        // (EngineIdMeanings). relations aircraft set <relation> --node game:id [--on p1|p2] [--off p3|p4]: one node's
        // aircraft selection in one step (one undo step).
        private static int RelationNodeAircraft(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool setting = args.Count > 0 && string.Equals(args[0], "set", StringComparison.OrdinalIgnoreCase);
            if (setting)
                args.RemoveAt(0);
            string nodeOption = PopOptionValue(args, "--node");
            List<string> on = ParsePlaneList(PopOptionValue(args, "--on"));
            List<string> off = ParsePlaneList(PopOptionValue(args, "--off"));
            // --only p1|p2: exactly these aircraft ticked (MSFS: the import's "only in some aircraft" list).
            List<string> only = ParsePlaneList(PopOptionValue(args, "--only"));
            string relationOption = PopOptionValue(args, "--relation");
            if (relationOption != null)
                args.Insert(0, relationOption);
            if (args.Count == 0)
                return Fail("relations aircraft [set] <relation> [--node game:id] [--on plane1|plane2] [--off plane3|plane4] [--only plane1|plane2]");
            string query = string.Join(" ", args).Trim();
            List<string> candidates;
            string relationName;
            if (!TryResolveRelationName(query, out relationName, out candidates))
                return FailResolution("relation", query, candidates);
            Relation relation = InternalDataManagement.AllRelations[relationName];
            string game = string.Empty;
            string id = string.Empty;
            if (!string.IsNullOrWhiteSpace(nodeOption))
            {
                string spec = nodeOption.Trim();
                int colon = spec.IndexOf(':');
                if (colon <= 0)
                    return Fail("--node needs game:id.");
                game = spec.Substring(0, colon);
                id = spec.Substring(colon + 1);
                int planes = id.LastIndexOf(':');
                if (planes > 0 && id.IndexOf('|', planes) >= 0)
                    id = id.Substring(0, planes);
            }
            EngineNodeAircraftResult result = new EngineNodeAircraftResult { Relation = relationName };
            if (setting)
            {
                if (id.Length == 0)
                    return Fail("relations aircraft set needs --node game:id.");
                if (on.Count == 0 && off.Count == 0 && only.Count == 0)
                    return Fail("relations aircraft set needs --on, --off or --only.");
                result.Changed = only.Count > 0
                    ? EngineIdMeanings.SetOnly(relation, game, id, only, result.Errors)
                    : EngineIdMeanings.SetSelection(relation, game, id, on, off, result.Errors);
                if (result.Errors.Count > 0)
                    return Fail(string.Join(" ", result.Errors));
                InternalDataManagement.ResyncRelations();
            }
            result.Nodes = EngineIdMeanings.RelationReport(relation, game, id);
            string payload = textOutput
                ? (setting ? "Changed: " + result.Changed.ToString(CultureInfo.InvariantCulture) + Environment.NewLine : string.Empty) + EngineIdMeanings.ToText(result.Nodes)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        // relations check-aircraft [--relation <name>] [--apply] [--include-same-function]: nodes whose switched-on
        // aircraft include ones where the id means something else than in most of them (EngineIdMeanings.Check);
        // --apply switches those aircraft off (one undo step; one tick in the node's aircraft list brings one back).
        private static int CheckRelationAircraft(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool apply = PopFlag(args, "--apply");
            bool includeSameFunction = PopFlag(args, "--include-same-function");
            string relationOption = PopOptionValue(args, "--relation");
            if (args.Count > 0)
                relationOption = string.Join(" ", args).Trim();
            if (!string.IsNullOrWhiteSpace(relationOption))
            {
                List<string> candidates;
                string relationName;
                if (!TryResolveRelationName(relationOption, out relationName, out candidates))
                    return FailResolution("relation", relationOption, candidates);
                relationOption = relationName;
            }
            EngineAircraftCheckResult result = EngineIdMeanings.Check(relationOption, apply, includeSameFunction);
            if (apply && result.Dropped > 0)
                InternalDataManagement.ResyncRelations();
            EmitPayload(textOutput ? EngineIdMeanings.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // relations suggest <relation> [--min high|medium|low] [--limit N] | --all [--min ...] |
        // apply <relation> [--add game:id:plane1|plane2]... [--never game:id:plane1|plane2]...
        private static int SuggestRelationItems(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            bool applying = args.Count > 0 && string.Equals(args[0], "apply", StringComparison.OrdinalIgnoreCase);
            if (applying)
                args.RemoveAt(0);
            // --relation <name> works like the relation as the first word.
            string relationOption = PopOptionValue(args, "--relation");
            if (relationOption != null)
                args.Insert(0, relationOption);
            if (applying)
            {
                List<string> add = PopOptionValues(args, "--add");
                List<string> never = PopOptionValues(args, "--never");
                List<string> remove = PopOptionValues(args, "--remove");
                List<string> move = PopOptionValues(args, "--move");
                List<string> take = PopOptionValues(args, "--take");
                if (args.Count != 1)
                    return Fail("relations suggest apply <relation> [--add game:id:planes]... [--never game:id:planes]... [--remove game:id:planes]... [--move game:id:planes=>relation]... [--take game:id:planes]...");
                EngineRelationSuggestApplyResult applied = EngineRelationSuggest.Apply(args[0], add, never, remove, move, take);
                if (applied.Errors.Count > 0 && applied.Added + applied.Rejected + applied.Removed + applied.Moved + applied.Taken == 0)
                    return Fail(string.Join(Environment.NewLine, applied.Errors));
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
                EmitPayload(textOutput
                    ? "Relation: " + applied.Relation + Environment.NewLine + "Added: " + applied.Added.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                      "Never: " + applied.Rejected.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + "Removed: " + applied.Removed.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                      "Moved: " + applied.Moved.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                      "Taken: " + applied.Taken.ToString(CultureInfo.InvariantCulture) + (applied.TakenFrom.Count > 0 ? " (from " + string.Join(", ", applied.TakenFrom) + ")" : string.Empty) + Environment.NewLine +
                      string.Join(Environment.NewLine, applied.Errors.Select(e => "Error: " + e))
                    : EngineSerialization.ToJson(applied), outPath);
                return 0;
            }

            string min = PopOptionValue(args, "--min") ?? "low";
            string limitRaw = PopOptionValue(args, "--limit");
            int limit = 0;
            if (limitRaw != null && (!int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 0))
                return Fail("--limit takes a number.");
            List<string> games = SplitList(PopOptionValues(args, "--game"));
            bool all = PopFlag(args, "--all");
            // --all --missing [--per-relation N]: per relation the free actions of games and aircraft it lacks.
            bool missing = PopFlag(args, "--missing");
            string perRelationRaw = PopOptionValue(args, "--per-relation");
            int perRelation = 25;
            if (perRelationRaw != null && (!int.TryParse(perRelationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out perRelation) || perRelation < 0))
                return Fail("--per-relation takes a number (0 = all).");
            if ((missing || perRelationRaw != null) && !all)
                return Fail("--missing and --per-relation go with --all.");
            if (all ? args.Count != 0 : args.Count != 1)
                return Fail("relations suggest <relation> [--min high|medium|low] [--limit N] [--game g1|g2] | relations suggest --all [--missing [--per-relation N]] [--min ...]");
            EngineRelationSuggestResult result = all
                ? missing ? EngineRelationSuggest.Missing(min, perRelation) : EngineRelationSuggest.Summary(min)
                : EngineRelationSuggest.Suggest(args[0], min, limit, games);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            EmitPayload(textOutput ? EngineRelationSuggest.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        private static List<string> SplitList(IEnumerable<string> values)
        {
            return values.SelectMany(v => (v ?? string.Empty).Split('|', ',')).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        }

        // relations chat <relation> "<message>" [--llm off|auto|on] [--show-prompt] [--apply]
        private static int ChatRelation(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            EngineRelationChat.Options options = new EngineRelationChat.Options();
            options.LlmMode = (PopOptionValue(args, "--llm") ?? string.Empty).Trim().ToLowerInvariant();
            options.ShowPrompt = PopFlag(args, "--show-prompt");
            options.Apply = PopFlag(args, "--apply");
            string relationOption = PopOptionValue(args, "--relation");
            if (relationOption != null)
                args.Insert(0, relationOption);
            if (options.LlmMode.Length > 0 && !EngineNlGroup.LlmModes.Contains(options.LlmMode))
                return Fail("Invalid --llm value: " + options.LlmMode + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
            List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown relations chat argument(s): " + string.Join(" ", unknown));
            if (args.Count < 2)
                return Fail("relations chat <relation> \"<message>\" [--llm off|auto|on] [--show-prompt] [--apply]");
            List<string> candidates;
            string relationName;
            if (!TryResolveRelationName(args[0], out relationName, out candidates))
                return FailResolution("relation", args[0], candidates);
            options.Relation = relationName;
            options.Message = string.Join(" ", args.Skip(1));
            EngineRelationChatResult result = EngineRelationChat.Run(options);
            if (result.Errors.Count > 0 && !result.Applied)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            if (result.Applied)
            {
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }
            EmitPayload(textOutput ? EngineRelationChat.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // relations search "<words>" [--game g1|g2] [--plane p1|p2] [--category <name>] [--axis|--button] [--limit N]
        private static int SearchRelationActions(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            List<string> games = SplitList(PopOptionValues(args, "--game"));
            List<string> planes = SplitList(PopOptionValues(args, "--plane"));
            bool axisOnly = PopFlag(args, "--axis");
            bool buttonOnly = PopFlag(args, "--button");
            string limitRaw = PopOptionValue(args, "--limit");
            int limit = 60;
            if (limitRaw != null && (!int.TryParse(limitRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 0))
                return Fail("--limit takes a number.");
            string category = PopOptionValue(args, "--category") ?? string.Empty;
            string perGameRaw = PopOptionValue(args, "--per-game");
            int perGame = 0;
            if (perGameRaw != null && (!int.TryParse(perGameRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out perGame) || perGame < 0))
                return Fail("--per-game takes a number (0 = no cap).");
            if (axisOnly && buttonOnly)
                return Fail("Use --axis or --button, not both.");
            string batch = PopOptionValue(args, "--batch");
            if (batch != null)
            {
                // One query per line (or the 2nd column of a tab-separated line, '#' lines skipped): a JSON array of
                // results, for evaluations (tools/relations/check-function-map.ps1).
                string batchFile = NormalizePath(batch);
                if (!File.Exists(batchFile))
                    return Fail("--batch file not found: " + batchFile);
                List<EngineRelationSearchResult> results = new List<EngineRelationSearchResult>();
                foreach (string line in File.ReadAllLines(batchFile, Encoding.UTF8))
                {
                    if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        continue;
                    string[] columns = line.Split('\t');
                    string q = (columns.Length > 1 ? columns[1] : columns[0]).Trim();
                    if (q.Length > 0)
                        results.Add(EngineRelationSearch.Search(q, games, planes, axisOnly ? true : buttonOnly ? false : (bool?)null, limit, category, perGame));
                }
                EmitPayload(textOutput ? string.Join(Environment.NewLine, results.Select(EngineRelationSearch.ToText)) : EngineSerialization.ToJson(results), outPath);
                return 0;
            }
            if (args.Count == 0 && category.Trim().Length == 0)
                return Fail("relations search \"<words>\" [--game g1|g2] [--plane p1|p2] [--category <name>] [--axis|--button] [--limit N] [--per-game N] [--batch <file>]");
            EngineRelationSearchResult result = EngineRelationSearch.Search(string.Join(" ", args), games, planes, axisOnly ? true : buttonOnly ? false : (bool?)null, limit, category, perGame);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            EmitPayload(textOutput ? EngineRelationSearch.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // relations browse [--game <game> [--plane p1|p2]] [--axis|--button]: every game with its aircraft, or the whole
        // list of one game (DCS: of the aircraft) with the game's categories ("Open catalog" on the Relations page).
        private static int BrowseRelationActions(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            List<string> games = SplitList(PopOptionValues(args, "--game"));
            List<string> planes = SplitList(PopOptionValues(args, "--plane"));
            bool axisOnly = PopFlag(args, "--axis");
            bool buttonOnly = PopFlag(args, "--button");
            if (axisOnly && buttonOnly)
                return Fail("Use --axis or --button, not both.");
            if (args.Count > 0)
                return Fail("relations browse [--game <game> [--plane p1|p2]] [--axis|--button]");
            EngineRelationBrowseResult result = EngineRelationSearch.Browse(games, planes, axisOnly ? true : buttonOnly ? false : (bool?)null);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                if (result.Game.Length == 0)
                {
                    sb.AppendLine("Games: " + result.Games.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (EngineBrowseGame game in result.Games)
                        sb.AppendLine("  " + game.Game + " | " + game.Actions.ToString(CultureInfo.InvariantCulture) + " actions" +
                                      (game.Planes.Count > 0 ? " | " + game.Planes.Count.ToString(CultureInfo.InvariantCulture) + " aircraft" : string.Empty));
                }
                else
                {
                    sb.AppendLine("Catalog: " + result.Game + (result.Planes.Count > 0 ? " " + string.Join(", ", result.Planes) : string.Empty) + " | " +
                                  result.Total.ToString(CultureInfo.InvariantCulture) + " actions");
                    foreach (EngineBrowseCategory category in result.Categories)
                    {
                        sb.AppendLine("  [" + category.Name + "] " + category.Count.ToString(CultureInfo.InvariantCulture) +
                                      (category.InRelations > 0 ? " (" + category.InRelations.ToString(CultureInfo.InvariantCulture) + " in relations)" : string.Empty));
                        foreach (EngineRelationSearchHit hit in result.Results.Where(h => EngineRelationSearch.CategoryNames(h.Category).Contains(category.Name, StringComparer.OrdinalIgnoreCase)))
                            sb.AppendLine("    " + hit.Id + " | " + hit.Title + (hit.IsAxis ? " | axis" : string.Empty) + (hit.InRelations.Count > 0 ? " | in " + string.Join(", ", hit.InRelations) : string.Empty));
                    }
                }
                EmitPayload(sb.ToString().TrimEnd(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(result), outPath);
            }
            return 0;
        }

        // relations functions [--category <id>] [--query "<words>"] [--coverage] [--dump <file.tsv>]
        private static int ListRelationFunctions(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string category = PopOptionValue(args, "--category") ?? string.Empty;
            string query = PopOptionValue(args, "--query") ?? string.Empty;
            string dump = PopOptionValue(args, "--dump") ?? string.Empty;
            bool coverage = PopFlag(args, "--coverage");
            if (args.Count > 0)
                return Fail("relations functions [--category <id>] [--query \"<words>\"] [--coverage] [--dump <file.tsv>]");
            EngineFunctionListResult result = EngineRelationFunctions.Functions(category, query, coverage, dump.Length > 0 ? NormalizePath(dump) : string.Empty);
            if (result.Totals.Functions == 0)
                return Fail(string.Join(Environment.NewLine, result.Warnings.DefaultIfEmpty("The function map is empty.")));
            EmitPayload(textOutput ? EngineRelationFunctions.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // relations resolve "<query>" [--game g1|g2] [--plane p1|p2] [--axis|--button] [--per-game N]
        private static int ResolveRelationQuery(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            List<string> games = SplitList(PopOptionValues(args, "--game"));
            List<string> planes = SplitList(PopOptionValues(args, "--plane"));
            bool axisOnly = PopFlag(args, "--axis");
            bool buttonOnly = PopFlag(args, "--button");
            string perGameRaw = PopOptionValue(args, "--per-game");
            int perGame = 25;
            if (perGameRaw != null && (!int.TryParse(perGameRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out perGame) || perGame < 0))
                return Fail("--per-game takes a number (0 = all).");
            if (axisOnly && buttonOnly)
                return Fail("Use --axis or --button, not both.");
            List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown relations resolve argument(s): " + string.Join(" ", unknown));
            if (args.Count == 0)
                return Fail("relations resolve \"<query>\" [--game g1|g2] [--plane p1|p2] [--axis|--button] [--per-game N]");
            EngineResolveResult result = EngineRelationFunctions.Resolve(string.Join(" ", args), games, planes, axisOnly ? true : buttonOnly ? false : (bool?)null, perGame);
            EmitPayload(textOutput ? EngineRelationFunctions.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        private static int AutoAssignRelations(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            AutoAssignOptions options = new AutoAssignOptions();
            foreach (string grip in PopOptionValues(args, "--grip"))
            {
                int split = grip.LastIndexOf('=');
                if (split <= 0)
                    return Fail("--grip expects \"<device or VID:PID>=<grip id>\".");
                options.GripChoices[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
            }
            options.AddedDevices.AddRange(PopOptionValues(args, "--add-device"));
            options.IgnoredDevices.AddRange(PopOptionValues(args, "--ignore-device"));
            foreach (string relation in PopOptionValues(args, "--relation"))
                options.Relations.Add(relation);
            string maxModifiers = PopOptionValue(args, "--max-modifiers");
            if (maxModifiers != null)
            {
                int parsed;
                if (!int.TryParse(maxModifiers, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > 2)
                    return Fail("--max-modifiers takes 0, 1 or 2.");
                options.MaxModifiers = parsed;
            }
            options.Overwrite = PopFlag(args, "--overwrite");
            options.IgnoreStyle = PopFlag(args, "--no-style");
            options.IncludeMissing = PopFlag(args, "--include-missing");
            foreach (string relation in PopOptionValues(args, "--explain"))
                options.Explain.Add(relation);
            options.DryRun = PopFlag(args, "--dry-run");
            if (args.Count > 0)
                return Fail("Unknown relations auto-assign argument(s): " + string.Join(" ", args));

            EngineAutoAssignResult result = EngineAutoAssign.Run(options);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            if (!options.DryRun)
            {
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }
            EmitPayload(textOutput ? EngineAutoAssign.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // devices kb list [query] | show <id> | identify [--grip dev=grip]... : the device knowledge base for Auto Assign.
        private static int RunDevicesKb(List<string> args, string outPath, bool textOutput)
        {
            string action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            List<string> warnings = new List<string>();
            if (action == "list")
            {
                List<KbDevice> devices = EngineDeviceKb.Search(args.Count > 1 ? args[1] : string.Empty, warnings);
                if (textOutput)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Device Catalog: " + devices.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (KbDevice d in devices)
                        sb.AppendLine("  - " + d.Id + " | " + d.Name + " | " + d.Kind + (d.Base.Length > 0 ? " | grip of " + d.Base : string.Empty) + " | " + d.Confidence);
                    foreach (string warning in warnings)
                        sb.AppendLine("Warning: " + warning);
                    EmitPayload(sb.ToString(), outPath);
                }
                else
                {
                    EmitPayload(EngineSerialization.ToJson(devices.Select(d => new { d.Id, d.Name, d.Vendor, d.Kind, d.Hand, d.Base, d.Image, d.Confidence, d.VidPids }).ToList()), outPath);
                }
                return 0;
            }
            if (action == "show")
            {
                if (args.Count < 2)
                    return Fail("devices kb show requires a device id.");
                KbDevice device = EngineDeviceKb.Find(args[1], warnings);
                if (device == null)
                    return Fail("Device '" + args[1] + "' is not in the device catalog.");
                if (textOutput)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine(device.Name + " (" + device.Id + ", " + device.Kind + ", " + device.Confidence + ")");
                    foreach (KbControl axis in device.Axes)
                        sb.AppendLine("  axis " + axis.Input + " = " + axis.Control);
                    foreach (KbControl control in device.Controls)
                        sb.AppendLine("  " + (control.Input.Length > 0 ? control.Input : "?") + " = " + control.Control + (control.Label.Length > 0 ? " \"" + control.Label + "\"" : string.Empty));
                    EmitPayload(sb.ToString(), outPath);
                }
                else
                {
                    EmitPayload(EngineSerialization.ToJson(device), outPath);
                }
                return 0;
            }
            if (action == "identify")
            {
                List<string> rest = args.Skip(1).ToList();
                Dictionary<string, string> grips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string grip in PopOptionValues(rest, "--grip"))
                {
                    int split = grip.LastIndexOf('=');
                    if (split > 0)
                        grips[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
                }
                List<string> added = PopOptionValues(rest, "--add-device");
                List<RosterDevice> roster = EngineDeviceKb.BuildRoster(Games.GameDeviceDirectory.Discover(warnings), grips, added, null, warnings);
                EngineAutoAssignResult result = new EngineAutoAssignResult();
                Dictionary<RosterDevice, RosterDevice> alternatives;
                result.Devices.AddRange(EngineAutoAssign.DescribeRoster(roster, out alternatives));
                result.Warnings.AddRange(warnings);
                EmitPayload(textOutput ? EngineAutoAssign.DevicesToText(result.Devices, result.Warnings) : EngineSerialization.ToJson(result.Devices), outPath);
                return 0;
            }
            return Fail("Unknown devices kb action: " + action + " (use list, show or identify).");
        }

        // engine relations from-text "<request>" [--games g1|g2] [--planes p1|p2] [--llm off|auto|on] [--substitutes]
        //   [--select <relation>]... [--skip-node game:id:plane1|plane2]... [--name <name>] [--min-confidence 0..1] [--dry-run]
        //   [--show-prompt [--prompt-model <id>|local]]   (the prompt the local model would get; no model runs)
        // engine relations from-text --check   (vocabulary self-test)
        private static int FromTextRelations(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (PopFlag(args, "--check"))
            {
                if (args.Count > 0)
                    return Fail("Unknown relations from-text --check argument(s): " + string.Join(" ", args));
                EngineNlVocabularyReport report = EngineNlGroup.CheckVocabulary();
                EmitPayload(textOutput ? EngineNlGroup.ToText(report) : EngineSerialization.ToJson(report), outPath);
                return report.Errors.Count == 0 ? 0 : 1;
            }
            EngineNlGroup.Options options = new EngineNlGroup.Options();
            options.DryRun = PopFlag(args, "--dry-run");
            options.Substitutes = PopFlag(args, "--substitutes");
            options.ShowPrompt = PopFlag(args, "--show-prompt");
            options.PromptModel = PopOptionValue(args, "--prompt-model") ?? string.Empty;
            if (options.PromptModel.Length > 0)
                options.ShowPrompt = true;
            options.Games = ParsePlaneList(PopOptionValue(args, "--games"));
            options.Planes = ParsePlaneList(PopOptionValue(args, "--planes"));
            options.LlmMode = (PopOptionValue(args, "--llm") ?? string.Empty).Trim().ToLowerInvariant();
            options.Select = PopOptionValues(args, "--select");
            options.SkipNodes = PopOptionValues(args, "--skip-node");
            options.Name = PopOptionValue(args, "--name") ?? string.Empty;
            string minConfidence = PopOptionValue(args, "--min-confidence");
            string request = PopOptionValue(args, "--request");
            if (!string.IsNullOrWhiteSpace(minConfidence))
            {
                double value;
                if (!double.TryParse(minConfidence, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0 || value > 1)
                    return Fail("Invalid --min-confidence value: " + minConfidence + " (0..1).");
                options.MinConfidence = value;
            }
            if (options.LlmMode.Length > 0 && !EngineNlGroup.LlmModes.Contains(options.LlmMode))
                return Fail("Invalid --llm value: " + options.LlmMode + ". Use " + string.Join(", ", EngineNlGroup.LlmModes) + ".");
            List<string> unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
                return Fail("Unknown relations from-text argument(s): " + string.Join(" ", unknown));
            if (string.IsNullOrWhiteSpace(request))
                request = string.Join(" ", args);
            options.Request = request.Trim();

            EngineNlGroupResult result = EngineNlGroup.Run(options);
            if (!options.DryRun && result.Errors.Count == 0 && result.NodesAdded > 0)
            {
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }
            EmitPayload(textOutput ? EngineNlGroup.ToText(result) : EngineSerialization.ToJson(result), outPath);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            return 0;
        }

        // engine llm status|recommend|download|remove|enable|disable|cancel|find-local|use-local|start|server|stop|fake-server: the optional local model
        // for relations from-text (EngineLlm). Nothing is downloaded without --accept-license.
        private static int RunLlm(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "status" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            switch (action)
            {
                case "status":
                case "recommend":
                    {
                        string ram = PopOptionValue(args, "--ram-gb");
                        string vram = PopOptionValue(args, "--vram-gb");
                        string gpu = PopOptionValue(args, "--gpu");
                        string vendor = PopOptionValue(args, "--gpu-vendor");
                        string cores = PopOptionValue(args, "--cores");
                        string avx2 = PopOptionValue(args, "--avx2");
                        if (args.Count > 0)
                            return Fail("Unknown llm " + action + " argument(s): " + string.Join(" ", args));
                        EngineLlm.Hardware hw;
                        if (ram == null && vram == null && gpu == null && vendor == null && cores == null && avx2 == null)
                        {
                            hw = EngineLlm.DetectHardware();
                        }
                        else
                        {
                            double ramValue = 0, vramValue = 0;
                            int coreValue = 0;
                            bool avxValue = false;
                            if (ram != null && !double.TryParse(ram, NumberStyles.Float, CultureInfo.InvariantCulture, out ramValue))
                                return Fail("Invalid --ram-gb value: " + ram);
                            if (vram != null && !double.TryParse(vram, NumberStyles.Float, CultureInfo.InvariantCulture, out vramValue))
                                return Fail("Invalid --vram-gb value: " + vram);
                            if (cores != null && !int.TryParse(cores, NumberStyles.Integer, CultureInfo.InvariantCulture, out coreValue))
                                return Fail("Invalid --cores value: " + cores);
                            if (avx2 != null && !TryParseOptionBoolean(avx2, out avxValue))
                                return Fail("Invalid --avx2 value: " + avx2 + " (true|false).");
                            hw = EngineLlm.InjectedHardware(ram == null ? (double?)null : ramValue, vram == null ? (double?)null : vramValue, gpu, vendor,
                                cores == null ? (int?)null : coreValue, avx2 == null ? (bool?)null : avxValue);
                        }
                        EngineLlmStatus status = EngineLlm.Status(hw);
                        if (action == "recommend")
                        {
                            EmitPayload(textOutput
                                ? "Recommended: " + (status.Recommendation.ModelId.Length > 0 ? status.Recommendation.ModelId : "none") + " [" + status.Recommendation.Tier + "] on " +
                                  (status.Recommendation.GpuLayers > 0 ? "GPU (" + status.Recommendation.Runtime + ")" : "CPU") + ", " +
                                  status.Recommendation.Threads.ToString(CultureInfo.InvariantCulture) + " threads" + Environment.NewLine + "Why: " + status.Recommendation.Reason
                                : EngineSerialization.ToJson(status.Recommendation), outPath);
                            return 0;
                        }
                        EmitPayload(textOutput ? EngineLlm.ToText(status) : EngineSerialization.ToJson(status), outPath);
                        return 0;
                    }
                case "download":
                    {
                        bool accept = PopFlag(args, "--accept-license");
                        bool noRuntime = PopFlag(args, "--no-runtime");
                        string runtime = PopOptionValue(args, "--runtime");
                        string model = PopOptionValue(args, "--model");
                        if (model == null && args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                        {
                            model = args[0];
                            args.RemoveAt(0);
                        }
                        if (args.Count > 0)
                            return Fail("Unknown llm download argument(s): " + string.Join(" ", args));
                        EngineLlmDownloadResult result = EngineLlm.Download(model, runtime, accept, noRuntime);
                        EmitPayload(textOutput ? LlmResultText(result) : EngineSerialization.ToJson(result), outPath);
                        return result.Errors.Count == 0 ? 0 : Fail(string.Join(Environment.NewLine, result.Errors));
                    }
                case "remove":
                    {
                        bool all = PopFlag(args, "--all");
                        string model = args.Count > 0 ? args[0] : null;
                        if (args.Count > 1)
                            return Fail("Unknown llm remove argument(s): " + string.Join(" ", args.Skip(1)));
                        EngineLlmDownloadResult result = EngineLlm.Remove(model, all);
                        EmitPayload(textOutput ? LlmResultText(result) : EngineSerialization.ToJson(result), outPath);
                        return result.Errors.Count == 0 ? 0 : Fail(string.Join(Environment.NewLine, result.Errors));
                    }
                case "enable":
                case "disable":
                    {
                        EngineLlm.Settings settings = EngineLlm.LoadSettings();
                        settings.Enabled = action == "enable";
                        if (args.Count == 1 && string.Equals(args[0], EngineLlm.LocalModelId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(settings.LocalServer) || string.IsNullOrWhiteSpace(settings.LocalModel))
                                return Fail("No own llama.cpp / GGUF set (engine llm use-local --server <llama-server.exe> --model <file.gguf>).");
                            settings.ModelId = EngineLlm.LocalModelId;
                        }
                        else if (args.Count > 0)
                        {
                            EngineLlm.Catalog catalog = EngineLlm.LoadCatalog();
                            EngineLlm.ModelInfo model = catalog.Models.FirstOrDefault(m => string.Equals(m.Id, args[0], StringComparison.OrdinalIgnoreCase));
                            if (model == null || args.Count > 1)
                                return Fail("Unknown model '" + string.Join(" ", args) + "'. Models: " + string.Join(", ", catalog.Models.Select(m => m.Id)) + ".");
                            settings.ModelId = model.Id;
                        }
                        EngineLlm.SaveSettings(settings);
                        EmitPayload(textOutput
                            ? "Local AI model " + (settings.Enabled ? "enabled" : "disabled") + (settings.ModelId.Length > 0 ? " (" + settings.ModelId + ")" : string.Empty) + "."
                            : EngineSerialization.ToJson(settings), outPath);
                        return 0;
                    }
                case "find-local":
                    {
                        List<string> roots = new List<string>();
                        string root;
                        while ((root = PopOptionValue(args, "--root")) != null)
                            roots.Add(NormalizePath(root));
                        if (args.Count > 0)
                            return Fail("Unknown llm find-local argument(s): " + string.Join(" ", args));
                        EngineLlmLocalScan scan = EngineLlm.FindLocal(EngineLlm.DetectHardware(), roots.Count > 0 ? roots : null);
                        EmitPayload(textOutput ? EngineLlm.ToText(scan) : EngineSerialization.ToJson(scan), outPath);
                        return 0;
                    }
                case "use-local":
                    {
                        bool clear = PopFlag(args, "--clear");
                        string server = NormalizePath(PopOptionValue(args, "--server"));
                        string model = NormalizePath(PopOptionValue(args, "--model"));
                        string layers = PopOptionValue(args, "--gpu-layers");
                        string context = PopOptionValue(args, "--context");
                        string template = PopOptionValue(args, "--template");
                        int contextValue = 0;
                        if (context != null && (!int.TryParse(context, NumberStyles.Integer, CultureInfo.InvariantCulture, out contextValue) || contextValue < 512))
                            return Fail("Invalid --context value: " + context + " (512 or more).");
                        if (args.Count > 0)
                            return Fail("Unknown llm use-local argument(s): " + string.Join(" ", args));
                        EngineLlmDownloadResult result = clear ? EngineLlm.ClearLocal() : EngineLlm.UseLocal(server, model, layers, contextValue, template);
                        EmitPayload(textOutput ? LlmResultText(result) : EngineSerialization.ToJson(result), outPath);
                        return result.Errors.Count == 0 ? 0 : Fail(string.Join(Environment.NewLine, result.Errors));
                    }
                case "stop":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown llm stop argument(s): " + string.Join(" ", args));
                        int pid;
                        bool stopped = EngineLlm.StopServer(out pid);
                        EmitPayload(textOutput
                            ? (stopped ? "Stopped the local AI model (process " + pid.ToString(CultureInfo.InvariantCulture) + ")." : "The local AI model is not running.")
                            : EngineSerialization.ToJson(new Dictionary<string, object> { { "Stopped", stopped }, { "Pid", pid } }), outPath);
                        return 0;
                    }
                case "start":
                    {
                        // Loads the chosen model now (Settings > Local AI > Start now): started from the app it stays
                        // loaded for the next request until the app's idle stop; from a command line it ends again.
                        if (args.Count > 0)
                            return Fail("Unknown llm start argument(s): " + string.Join(" ", args));
                        EngineLlm.ModelInfo model;
                        string problem;
                        if (!EngineLlm.TryGetUsableModel(out model, out problem))
                            return Fail("The local AI model cannot start: " + problem);
                        bool warm = EngineLlm.WarmSettings() != null;
                        string name = EngineRelationPlan.ModelName(model);
                        EngineProgress.Step(0, 1, "Starting the local AI model", name);
                        EngineLlm.Session session = EngineLlm.Session.Start(model, EngineRelationPlan.PlanContextSize(model));
                        try
                        {
                            if (session.Error.Length > 0)
                                return Fail("The local AI model (" + name + ") did not start: " + session.Error);
                            Dictionary<string, object> started = new Dictionary<string, object>
                            {
                                { "Started", true }, { "Model", name }, { "ModelId", model.Id }, { "Pid", session.ProcessId }, { "LoadMs", session.LoadMs }, { "StaysLoaded", warm }
                            };
                            EmitPayload(textOutput
                                ? "Started the local AI model " + name + " (process " + session.ProcessId.ToString(CultureInfo.InvariantCulture) + ", " +
                                  (session.LoadMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s)" + (warm ? "; it stays loaded until the app's idle stop." : "; no app keeps it, so it ends again.")
                                : EngineSerialization.ToJson(started), outPath);
                            return 0;
                        }
                        finally
                        {
                            session.Dispose();
                        }
                    }
                case "server":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown llm server argument(s): " + string.Join(" ", args));
                        Dictionary<string, object> server = EngineLlm.ServerStatus();
                        EmitPayload(textOutput
                            ? ((bool)server["Running"] ? "Running: " + server["Model"] + " (process " + server["Pid"] + ")" : "Not running.")
                            : EngineSerialization.ToJson(server), outPath);
                        return 0;
                    }
                case "cancel":
                    Directory.CreateDirectory(EngineLlm.DataRoot());
                    File.WriteAllText(EngineLlm.CancelFile(), "cancel", new UTF8Encoding(false));
                    EmitPayload(textOutput ? "Download cancel requested." : "{\"Cancel\":true}", outPath);
                    return 0;
                case "fake-server":
                    {
                        string port = PopOptionValue(args, "--port") ?? "0";
                        string portFile = NormalizePath(PopOptionValue(args, "--port-file"));
                        string response = NormalizePath(PopOptionValue(args, "--response"));
                        string serve = NormalizePath(PopOptionValue(args, "--serve"));
                        string log = NormalizePath(PopOptionValue(args, "--log"));
                        string lifetime = PopOptionValue(args, "--lifetime") ?? "300";
                        int portValue, lifetimeValue;
                        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out portValue) || portValue < 0 || portValue > 65535)
                            return Fail("Invalid --port value: " + port);
                        if (!int.TryParse(lifetime, NumberStyles.Integer, CultureInfo.InvariantCulture, out lifetimeValue) || lifetimeValue <= 0)
                            return Fail("Invalid --lifetime value: " + lifetime);
                        if (args.Count > 0)
                            return Fail("Unknown llm fake-server argument(s): " + string.Join(" ", args));
                        return EngineLlm.RunFakeServer(portValue, portFile, response, serve, log, lifetimeValue);
                    }
                default:
                    return Fail("Unknown llm action: " + action + " (use status, recommend, download, remove, enable, disable, cancel, find-local, use-local, start, server, stop).");
            }
        }

        private static string LlmResultText(EngineLlmDownloadResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (result.Model.Length > 0)
                sb.AppendLine("Model: " + result.Model);
            if (result.Runtime.Length > 0)
                sb.AppendLine("Runtime: " + result.Runtime);
            if (result.Consent.Length > 0)
                sb.AppendLine("Download: " + result.Consent);
            foreach (string message in result.Messages)
                sb.AppendLine(message);
            foreach (string error in result.Errors)
                sb.AppendLine("Error: " + error);
            return sb.ToString();
        }

        private static int AutoGroupRelations(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string preset = (PopOptionValue(args, "--preset") ?? "all").Trim().ToLowerInvariant();
            bool noSubstitutes = PopFlag(args, "--no-substitutes");
            bool replace = PopFlag(args, "--replace");
            bool dryRun = PopFlag(args, "--dry-run");
            bool noMap = PopFlag(args, "--no-map");
            List<string> planes = ParsePlaneList(PopOptionValue(args, "--planes"));
            if (args.Count > 0)
                return Fail("Unknown relations auto-group argument(s): " + string.Join(" ", args));
            if (!EngineAutoGroup.Presets.Contains(preset))
                return Fail("Invalid --preset value: " + preset + ". Use " + string.Join(", ", EngineAutoGroup.Presets) + ".");

            EngineAutoGroupResult result = EngineAutoGroup.Run(preset, !noSubstitutes, planes, replace, dryRun, !noMap);
            if (result.Errors.Count > 0)
                return Fail(string.Join(Environment.NewLine, result.Errors));
            if (!dryRun)
            {
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
            }

            EmitPayload(textOutput ? EngineAutoGroup.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        private static int AddRelation(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count == 0)
                return Fail("relations add requires at least a name and one source (--node/--id/--from).");

            string name = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                name = args[0];
                args.RemoveAt(0);
            }

            string namedOption = PopOptionValue(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
                name = namedOption;
            if (string.IsNullOrWhiteSpace(name))
                return Fail("relations add requires a relation name.");

            bool axisFlag = PopFlag(args, "--axis");
            bool buttonFlag = PopFlag(args, "--button");
            string typeOption = PopOptionValue(args, "--type");
            if (axisFlag && buttonFlag)
                return Fail("relations add cannot use --axis and --button together.");

            bool? explicitIsAxis = null;
            if (axisFlag)
                explicitIsAxis = true;
            if (buttonFlag)
                explicitIsAxis = false;
            if (!string.IsNullOrWhiteSpace(typeOption))
            {
                bool parsedType;
                if (!TryParseRelationType(typeOption, out parsedType))
                    return Fail("Invalid --type value: " + typeOption + ". Use axis or button.");
                if (explicitIsAxis.HasValue && explicitIsAxis.Value != parsedType)
                    return Fail("relations add received conflicting type options.");
                explicitIsAxis = parsedType;
            }

            List<string> fromQueries = PopOptionValues(args, "--from");
            List<string> nodeSpecs = PopOptionValues(args, "--node");
            string idOption = PopOptionValue(args, "--id");
            string gameOption = PopOptionValue(args, "--game");
            string planesOption = PopOptionValue(args, "--planes");
            List<string> groupOptions = PopOptionValues(args, "--group");
            groupOptions.AddRange(PopOptionValues(args, "--groups"));
            string bindDeviceOption = PopOptionValue(args, "--device");
            string bindInputOption = PopOptionValue(args, "--input");
            List<string> modifierOptions = PopOptionValues(args, "--modifier");
            modifierOptions.AddRange(PopOptionValues(args, "--mod"));
            modifierOptions.AddRange(PopOptionValues(args, "--reformer"));
            string bindFromOption = PopOptionValue(args, "--bind-from");

            if (args.Count > 0)
                return Fail("Unknown relations add argument(s): " + string.Join(" ", args));

            List<Relation> sourceRelations = new List<Relation>();
            for (int i = 0; i < fromQueries.Count; i++)
            {
                string fromQuery = fromQueries[i];
                List<string> candidates;
                string relationName;
                if (!TryResolveRelationName(fromQuery, out relationName, out candidates))
                    return FailResolution("relation", fromQuery, candidates);

                sourceRelations.Add(InternalDataManagement.AllRelations[relationName]);
            }

            string bindFromRelationName = null;
            if (!string.IsNullOrWhiteSpace(bindFromOption))
            {
                List<string> bindCandidates;
                if (!TryResolveBindRelationName(bindFromOption, out bindFromRelationName, out bindCandidates))
                    return FailResolution("bind", bindFromOption, bindCandidates);
            }

            List<NodeSpec> parsedNodes = new List<NodeSpec>();
            for (int i = 0; i < nodeSpecs.Count; i++)
            {
                NodeSpec spec;
                if (!TryParseNodeSpec(nodeSpecs[i], out spec))
                    return Fail("Invalid --node value '" + nodeSpecs[i] + "'. Use --node <game:id[:plane1|plane2]>.");
                parsedNodes.Add(spec);
            }

            if (!string.IsNullOrWhiteSpace(idOption))
            {
                NodeSpec single = new NodeSpec();
                single.Id = idOption.Trim();
                single.Game = string.IsNullOrWhiteSpace(gameOption) ? "DCS" : gameOption.Trim();
                single.Planes = ParsePlaneList(planesOption);
                parsedNodes.Add(single);
            }
            ApplyCatalogAircraft(parsedNodes);

            bool isAxis;
            if (explicitIsAxis.HasValue)
            {
                isAxis = explicitIsAxis.Value;
            }
            else if (sourceRelations.Count > 0)
            {
                isAxis = sourceRelations[0].ISAXIS;
            }
            else if (parsedNodes.Count > 0)
            {
                string inferError;
                if (!TryInferRelationTypeFromNodes(parsedNodes, out isAxis, out inferError))
                {
                    if (!string.IsNullOrWhiteSpace(inferError))
                        return Fail(inferError);
                    return Fail("relations add could not infer type from nodes. Use --axis/--button/--type.");
                }
            }
            else
            {
                return Fail("relations add requires --node (or --id with --game), --from, or an explicit type.");
            }

            for (int i = 0; i < sourceRelations.Count; i++)
            {
                if (sourceRelations[i].ISAXIS != isAxis)
                    return Fail("Cannot combine axis and button relations in one add command.");
            }

            Relation relation = new Relation();
            relation.NAME = EnsureUniqueRelationName(name.Trim());
            relation.ISAXIS = isAxis;
            relation.Groups = new List<string>();
            relation.NODES = new List<RelationItem>();

            for (int i = 0; i < sourceRelations.Count; i++)
            {
                Relation source = sourceRelations[i];
                if (source.Groups != null)
                {
                    for (int g = 0; g < source.Groups.Count; g++)
                    {
                        AddUniqueGroup(relation.Groups, source.Groups[g]);
                    }
                }
                if (source.NODES != null)
                {
                    for (int n = 0; n < source.NODES.Count; n++)
                    {
                        RelationItem sourceItem = source.NODES[n];
                        if (sourceItem == null)
                            continue;
                        relation.IncludeRelationItem(sourceItem.Copy());
                    }
                }
            }

            for (int i = 0; i < parsedNodes.Count; i++)
            {
                NodeSpec spec = parsedNodes[i];
                List<string> planes = PlanesForNewNode(spec);
                if (planes.Count == 0)
                {
                    relation.AddNode(spec.Id, spec.Game, relation.ISAXIS);
                }
                else
                {
                    for (int p = 0; p < planes.Count; p++)
                    {
                        relation.AddNode(spec.Id, spec.Game, relation.ISAXIS, planes[p], false);
                    }
                }
            }

            List<string> parsedGroups = ParseGroupOptions(groupOptions);
            for (int i = 0; i < parsedGroups.Count; i++)
            {
                AddUniqueGroup(relation.Groups, parsedGroups[i]);
            }

            if (relation.NODES == null || relation.NODES.Count == 0)
                return Fail("relations add produced no nodes. Use --node, --id/--game, or --from with valid inputs.");

            InternalDataManagement.AllRelations.Add(relation.NAME, relation);

            bool shouldAttachBind = !string.IsNullOrWhiteSpace(bindDeviceOption) ||
                                    !string.IsNullOrWhiteSpace(bindInputOption) ||
                                    modifierOptions.Count > 0 ||
                                    !string.IsNullOrWhiteSpace(bindFromRelationName);

            if (shouldAttachBind)
            {
                Bind bind = null;
                if (!string.IsNullOrWhiteSpace(bindFromRelationName))
                {
                    Bind sourceBind = InternalDataManagement.AllBinds[bindFromRelationName];
                    bind = sourceBind.Copy(relation);
                }
                else
                {
                    bind = new Bind(relation);
                }

                if (!string.IsNullOrWhiteSpace(bindDeviceOption))
                {
                    bind.Joystick = bindDeviceOption.Trim();
                    bind.JoyBackup = bind.Joystick;
                }

                if (!string.IsNullOrWhiteSpace(bindInputOption))
                {
                    if (relation.ISAXIS)
                    {
                        bind.JAxis = bindInputOption.Trim();
                        bind.JButton = "";
                    }
                    else
                    {
                        bind.JButton = bindInputOption.Trim();
                        bind.JAxis = "";
                    }
                }

                if (modifierOptions.Count > 0)
                {
                    bind.AllReformers = NormalizeModifierInputs(modifierOptions);
                }

                relation.bind = bind;
                if (InternalDataManagement.AllBinds.ContainsKey(relation.NAME))
                    InternalDataManagement.AllBinds[relation.NAME] = bind;
                else
                    InternalDataManagement.AllBinds.Add(relation.NAME, bind);
            }

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            EngineRelationsDocument relationDoc = BuildFilteredRelationsDocument(relation.NAME);
            EngineRelationDto created = relationDoc.Relations.FirstOrDefault(r => string.Equals(r.Name, relation.NAME, StringComparison.Ordinal));
            if (created == null)
                return Fail("Relation was added but could not be reloaded for display: " + relation.NAME);

            string payload = textOutput
                ? EngineSerialization.ToText(created)
                : EngineSerialization.ToJson(created);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int EditRelationGroups(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 1)
                return Fail("relations group requires: list [<relation-name>] | add|remove <relation-name> <group...> | rename <old-group> <new-group>.");

            string operation = args[0].ToLowerInvariant();
            if (operation == "list")
                return ListRelationGroups(args.Skip(1).ToList(), outPath, textOutput);
            if (operation == "rename")
                return RenameRelationGroup(args.Skip(1).ToList(), outPath, textOutput);

            if (args.Count < 2)
                return Fail("relations group requires: add|remove <relation-name> <group...>.");

            string relationQuery = args[1];
            List<string> groupInputs = args.Skip(2).ToList();
            bool clearAll = PopFlag(groupInputs, "--all");

            if (groupInputs.Any(v => v.StartsWith("--", StringComparison.Ordinal)))
                return Fail("Unknown relations group option(s): " + string.Join(" ", groupInputs.Where(v => v.StartsWith("--", StringComparison.Ordinal))));

            List<string> relationCandidates;
            string relationName;
            if (!TryResolveRelationName(relationQuery, out relationName, out relationCandidates))
                return FailResolution("relation", relationQuery, relationCandidates);
            if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                return Fail("Relation not found: " + relationName);

            Relation relation = InternalDataManagement.AllRelations[relationName];
            if (relation.Groups == null)
                relation.Groups = new List<string>();

            switch (operation)
            {
                case "add":
                    {
                        List<string> groupsToAdd = ParseGroupOptions(groupInputs);
                        if (groupsToAdd.Count == 0)
                            return Fail("relations group add requires at least one group name.");

                        for (int i = 0; i < groupsToAdd.Count; i++)
                            AddUniqueGroup(relation.Groups, groupsToAdd[i]);
                        break;
                    }
                case "remove":
                    {
                        if (clearAll)
                        {
                            relation.Groups.Clear();
                        }
                        else
                        {
                            List<string> groupsToRemove = ParseGroupOptions(groupInputs);
                            if (groupsToRemove.Count == 0)
                                return Fail("relations group remove requires at least one group name or --all.");

                            HashSet<string> removalSet = new HashSet<string>(groupsToRemove, StringComparer.OrdinalIgnoreCase);
                            relation.Groups = relation.Groups
                                .Where(g => !string.IsNullOrWhiteSpace(g) && !removalSet.Contains(g.Trim()))
                                .ToList();
                        }
                        break;
                    }
                default:
                    return Fail("Unknown relations group action: " + operation);
            }

            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return ShowRelation(new List<string> { "show", relationName }, outPath, textOutput);
        }

        private static int ListRelationGroups(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count > 1)
                return Fail("relations group list accepts at most one relation name.");

            if (args.Count == 1)
            {
                string relationQuery = args[0];
                List<string> relationCandidates;
                string relationName;
                if (!TryResolveRelationName(relationQuery, out relationName, out relationCandidates))
                    return FailResolution("relation", relationQuery, relationCandidates);
                if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                    return Fail("Relation not found: " + relationName);

                Relation relation = InternalDataManagement.AllRelations[relationName];
                List<string> groups = relation == null || relation.Groups == null
                    ? new List<string>()
                    : relation.Groups
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(v => v)
                        .ToList();

                RelationGroupListResult relationResult = new RelationGroupListResult();
                relationResult.Scope = "relation";
                relationResult.Relation = relationName;
                relationResult.Groups = groups;

                string relationPayload = textOutput
                    ? ToText(relationResult)
                    : EngineSerialization.ToJson(relationResult);
                EmitPayload(relationPayload, outPath);
                return 0;
            }

            Dictionary<string, HashSet<string>> map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                string relationName = entry.Key;
                Relation relation = entry.Value;
                if (relation == null || relation.Groups == null)
                    continue;

                for (int i = 0; i < relation.Groups.Count; i++)
                {
                    string raw = relation.Groups[i];
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string group = raw.Trim();
                    if (!map.ContainsKey(group))
                        map.Add(group, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    map[group].Add(relationName);
                }
            }

            RelationGroupListResult globalResult = new RelationGroupListResult();
            globalResult.Scope = "global";
            globalResult.Groups = map.Keys.OrderBy(v => v).ToList();
            globalResult.Entries = map
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp =>
                {
                    RelationGroupListEntry entry = new RelationGroupListEntry();
                    entry.Name = kvp.Key;
                    entry.Relations = kvp.Value.OrderBy(v => v).ToList();
                    entry.RelationCount = entry.Relations.Count;
                    return entry;
                })
                .ToList();

            string payload = textOutput
                ? ToText(globalResult)
                : EngineSerialization.ToJson(globalResult);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDcsFfb(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? (command[1] ?? string.Empty).Trim().ToLowerInvariant() : "list";
            if (string.IsNullOrWhiteSpace(action))
                action = "list";

            List<string> args = command.Skip(2).ToList();
            bool confirmProvided;
            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out confirmProvided);
            string savedGamesPath = NormalizePath(PopOptionValue(args, "--path"));

            switch (action)
            {
                case "list":
                    return RunDcsFfbList(args, savedGamesPath, confirmSuggestions, outPath, textOutput);
                case "set":
                    return RunDcsFfbSet(args, savedGamesPath, confirmSuggestions, outPath, textOutput);
                case "clear":
                    return RunDcsFfbClear(args, savedGamesPath, confirmSuggestions, outPath, textOutput);
                default:
                    return Fail("Unknown dcs ffb action: " + action);
            }
        }

        private static int RunDcsFfbList(
            List<string> args,
            string savedGamesPath,
            bool confirmSuggestions,
            string outPath,
            bool textOutput)
        {
            bool connectedOnly = PopFlag(args, "--connected");
            bool profileOnly =
                PopFlag(args, "--profile-devices") ||
                PopFlag(args, "--profile-only") ||
                PopFlag(args, "--in-profile");

            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                deviceOptions.Add(args[0]);
                args.RemoveAt(0);
            }

            if (args.Count > 0)
                return Fail("Unknown dcs ffb list argument(s): " + string.Join(" ", args));

            List<string> requested = ParseDelimitedOptionValues(deviceOptions);
            if (!TryResolveDcsFfbDevices(requested, savedGamesPath, confirmSuggestions, "DCS FFB list", out List<string> resolvedDevices, out List<string> warnings, out string resolveError))
                return Fail(resolveError);

            HashSet<string> connected = new HashSet<string>(BuildConnectedJoystickList(warnings), StringComparer.OrdinalIgnoreCase);
            HashSet<string> profile = new HashSet<string>(BuildProfileJoystickList(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> available = new HashSet<string>(profile, StringComparer.OrdinalIgnoreCase);
            foreach (string item in connected)
                available.Add(item);

            if (InternalDataManagement.JoystickFFB != null)
            {
                foreach (string raw in InternalDataManagement.JoystickFFB.Keys)
                {
                    string device = (raw ?? string.Empty).Trim();
                    if (device.Length > 0 && !string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        available.Add(device);
                }
            }

            if (resolvedDevices.Count > 0)
            {
                available = new HashSet<string>(available.Where(d => resolvedDevices.Contains(d, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < resolvedDevices.Count; i++)
                {
                    string device = (resolvedDevices[i] ?? string.Empty).Trim();
                    if (device.Length > 0)
                        available.Add(device);
                }
            }

            if (connectedOnly && !profileOnly)
                available = new HashSet<string>(available.Where(d => connected.Contains(d)), StringComparer.OrdinalIgnoreCase);
            else if (profileOnly && !connectedOnly)
                available = new HashSet<string>(available.Where(d => profile.Contains(d)), StringComparer.OrdinalIgnoreCase);

            List<DcsFfbEntry> entries = available
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(device =>
                {
                    ForceFeedbackS setting = null;
                    bool hasSetting = InternalDataManagement.JoystickFFB != null &&
                                      InternalDataManagement.JoystickFFB.TryGetValue(device, out setting) &&
                                      setting != null;
                    setting = setting ?? new ForceFeedbackS();

                    DcsFfbEntry entry = new DcsFfbEntry();
                    entry.Device = device;
                    entry.HasSetting = hasSetting;
                    entry.InvertX = setting.invertX;
                    entry.InvertY = setting.invertY;
                    entry.SwapAxis = setting.swapAxis;
                    entry.Connected = connected.Contains(device);
                    entry.Profile = profile.Contains(device);
                    return entry;
                })
                .ToList();

            DcsFfbListResult result = new DcsFfbListResult();
            result.Devices = entries;
            result.Warnings = warnings;
            result.ConnectedOnly = connectedOnly;
            result.ProfileOnly = profileOnly;

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDcsFfbSet(
            List<string> args,
            string savedGamesPath,
            bool confirmSuggestions,
            string outPath,
            bool textOutput)
        {
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                deviceOptions.Add(args[0]);
                args.RemoveAt(0);
            }

            bool invertX = PopOptionBoolean(args, "--invert-x", false, out bool invertXProvided);
            if (!invertXProvided)
                invertX = PopOptionBoolean(args, "--invertx", false, out invertXProvided);
            bool invertY = PopOptionBoolean(args, "--invert-y", false, out bool invertYProvided);
            if (!invertYProvided)
                invertY = PopOptionBoolean(args, "--inverty", false, out invertYProvided);
            bool swapAxis = PopOptionBoolean(args, "--swap-axis", false, out bool swapAxisProvided);
            if (!swapAxisProvided)
                swapAxis = PopOptionBoolean(args, "--swap-axes", false, out swapAxisProvided);
            if (!swapAxisProvided)
                swapAxis = PopOptionBoolean(args, "--swapaxis", false, out swapAxisProvided);

            if (args.Count > 0)
                return Fail("Unknown dcs ffb set argument(s): " + string.Join(" ", args));

            if (!invertXProvided && !invertYProvided && !swapAxisProvided)
                return Fail("dcs ffb set requires at least one setting: --invert-x, --invert-y, or --swap-axis.");

            List<string> requested = ParseDelimitedOptionValues(deviceOptions);
            if (requested.Count == 0)
                return Fail("dcs ffb set requires a device name.");
            if (requested.Count > 1)
                return Fail("dcs ffb set accepts exactly one device target.");

            if (!TryResolveDcsFfbDevices(requested, savedGamesPath, confirmSuggestions, "DCS FFB set", out List<string> resolvedDevices, out List<string> warnings, out string resolveError))
                return Fail(resolveError);

            if (resolvedDevices.Count == 0)
                return Fail("No DCS device matched for dcs ffb set.");

            string targetDevice = resolvedDevices[0];
            if (InternalDataManagement.JoystickFFB == null)
                InternalDataManagement.JoystickFFB = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);

            ForceFeedbackS setting = InternalDataManagement.JoystickFFB.ContainsKey(targetDevice) &&
                                     InternalDataManagement.JoystickFFB[targetDevice] != null
                ? InternalDataManagement.JoystickFFB[targetDevice]
                : new ForceFeedbackS();

            if (invertXProvided)
                setting.invertX = invertX;
            if (invertYProvided)
                setting.invertY = invertY;
            if (swapAxisProvided)
                setting.swapAxis = swapAxis;

            InternalDataManagement.JoystickFFB[targetDevice] = setting;

            DcsFfbMutationResult result = new DcsFfbMutationResult();
            result.Action = "set";
            result.Devices.Add(targetDevice);
            result.Updated = 1;
            result.InvertXProvided = invertXProvided;
            result.InvertYProvided = invertYProvided;
            result.SwapAxisProvided = swapAxisProvided;
            result.InvertX = setting.invertX;
            result.InvertY = setting.invertY;
            result.SwapAxis = setting.swapAxis;
            result.Warnings = warnings;

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDcsFfbClear(
            List<string> args,
            string savedGamesPath,
            bool confirmSuggestions,
            string outPath,
            bool textOutput)
        {
            bool clearAll = PopFlag(args, "--all");
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                deviceOptions.Add(args[0]);
                args.RemoveAt(0);
            }

            if (args.Count > 0)
                return Fail("Unknown dcs ffb clear argument(s): " + string.Join(" ", args));

            List<string> requested = ParseDelimitedOptionValues(deviceOptions);
            if (!clearAll && requested.Count == 0)
                return Fail("dcs ffb clear requires a device name or --all.");

            if (InternalDataManagement.JoystickFFB == null)
                InternalDataManagement.JoystickFFB = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);

            List<string> warnings;
            List<string> targets;
            if (clearAll && requested.Count == 0)
            {
                warnings = new List<string>();
                targets = InternalDataManagement.JoystickFFB.Keys
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                if (!TryResolveDcsFfbDevices(requested, savedGamesPath, confirmSuggestions, "DCS FFB clear", out List<string> resolvedDevices, out warnings, out string resolveError))
                    return Fail(resolveError);
                targets = resolvedDevices;
            }

            List<string> removed = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                string target = (targets[i] ?? string.Empty).Trim();
                if (target.Length == 0)
                    continue;
                if (InternalDataManagement.JoystickFFB.Remove(target))
                    removed.Add(target);
            }

            if (requested.Count > 0 && removed.Count == 0)
                return Fail("No DCS FFB settings matched the requested device(s).");

            DcsFfbMutationResult result = new DcsFfbMutationResult();
            result.Action = "clear";
            result.All = clearAll;
            result.Devices = targets
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Removed = removed.Count;
            result.Updated = 0;
            result.Warnings = warnings;

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDcsDefaults(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? (command[1] ?? string.Empty).Trim().ToLowerInvariant() : "cut";
            if (string.IsNullOrWhiteSpace(action))
                action = "cut";

            List<string> args = command.Skip(2).ToList();
            string savedGamesPath = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                savedGamesPath = args[0];
                args.RemoveAt(0);
            }

            string installPath = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                installPath = args[0];
                args.RemoveAt(0);
            }

            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = pathOption;
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = PopOptionValue(args, "--saved-games");
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = PopOptionValue(args, "--saved-games-path");

            string installPathOption = PopOptionValue(args, "--install-path");
            if (string.IsNullOrWhiteSpace(installPathOption))
                installPathOption = PopOptionValue(args, "--install");
            if (string.IsNullOrWhiteSpace(installPath))
                installPath = installPathOption;

            string backupPath = PopOptionValue(args, "--backup-path");

            if (args.Count > 0)
                return Fail("Unknown dcs defaults argument(s): " + string.Join(" ", args));

            if (string.IsNullOrWhiteSpace(savedGamesPath))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredDcs = GamePathDiscovery.DiscoverDcs(discoveryWarnings);
                if (!string.IsNullOrWhiteSpace(discoveredDcs.PreferredConfigPath))
                {
                    savedGamesPath = discoveredDcs.PreferredConfigPath;
                    Console.Error.WriteLine("Auto-detected DCS Saved Games path: " + savedGamesPath);
                }
                else
                {
                    for (int i = 0; i < discoveryWarnings.Count; i++)
                        Console.Error.WriteLine(discoveryWarnings[i]);
                }
            }

            if (string.IsNullOrWhiteSpace(installPath))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredDcs = GamePathDiscovery.DiscoverDcs(discoveryWarnings);
                if (!string.IsNullOrWhiteSpace(discoveredDcs.PreferredInstallPath))
                {
                    installPath = discoveredDcs.PreferredInstallPath;
                    Console.Error.WriteLine("Auto-detected DCS install path: " + installPath);
                }
                else
                {
                    for (int i = 0; i < discoveryWarnings.Count; i++)
                        Console.Error.WriteLine(discoveryWarnings[i]);
                }
            }

            if (string.IsNullOrWhiteSpace(savedGamesPath))
            {
                return Fail(
                    "dcs defaults " + action + " requires a DCS Saved Games path. " +
                    "Try: engine games paths --game DCS --text");
            }
            if (string.IsNullOrWhiteSpace(installPath))
            {
                return Fail(
                    "dcs defaults " + action + " requires a DCS install path. " +
                    "Try: engine games paths --game DCS --text");
            }

            savedGamesPath = NormalizePath(savedGamesPath);
            installPath = NormalizePath(installPath);
            if (string.IsNullOrWhiteSpace(backupPath))
                backupPath = Path.Combine(savedGamesPath, "BACKUP_JOYSTICK_DEFAULTS");
            backupPath = NormalizePath(backupPath);

            DcsStickDefaultsResult result;
            switch (action)
            {
                case "cut":
                    result = CutDcsStickDefaults(savedGamesPath, installPath, backupPath);
                    break;
                case "restore":
                    result = RestoreDcsStickDefaults(savedGamesPath, installPath, backupPath);
                    break;
                default:
                    return Fail("Unknown dcs defaults action: " + action);
            }

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);

            if (result.Errors != null && result.Errors.Count > 0)
                return Fail("dcs defaults " + action + " failed: " + string.Join(" | ", result.Errors));

            return 0;
        }

        // engine dcs strip-defaults [apply|restore|status|auto] / engine dcs device-luas [list|remove|restore]:
        // the optional changes to the DCS installation (EngineDcsDefaultsStrip). The flags of the community script
        // work as well (--restore, --dry-run, --skip-ui-layer).
        private static int RunDcsInstallDefaults(string area, List<string> command, string outPath, bool textOutput)
        {
            bool deviceFiles = area != "strip-defaults";
            List<string> args = command.Skip(1).ToList();
            string action = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].Trim().ToLowerInvariant() : string.Empty;
            if (action.Length > 0)
                args.RemoveAt(0);
            bool dryRun = PopFlag(args, "--dry-run");
            if (PopFlag(args, "--restore"))
                action = "restore";
            if (PopFlag(args, "--remove"))
                action = "remove";
            if (PopFlag(args, "--status"))
                action = "status";
            bool skipUi = PopOptionBoolean(args, "--skip-ui-layer", false, out bool skipUiProvided);
            bool remember = PopOptionBoolean(args, "--remember", true, out _);
            string installPath = PopOptionValue(args, "--install-path") ?? PopOptionValue(args, "--install");
            string savedGames = PopOptionValue(args, "--saved-games") ?? PopOptionValue(args, "--path");
            string luae = PopOptionValue(args, "--luae");
            bool noLoadCheck = PopFlag(args, "--no-load-check");
            if (args.Count > 0)
                return Fail("Unknown dcs " + area + " argument(s): " + string.Join(" ", args));

            EngineDcsDefaultsStrip.StripOptions options = new EngineDcsDefaultsStrip.StripOptions
            {
                InstallPath = installPath,
                SavedGamesPath = savedGames,
                DryRun = dryRun,
                SkipUiLayer = skipUiProvided ? (bool?)skipUi : null,
                Remember = remember,
                LuaePath = luae,
                SkipLoadCheck = noLoadCheck
            };
            EngineDcsDefaultsStrip.DcsStripResult result;
            if (deviceFiles)
            {
                switch (action)
                {
                    case "":
                    case "list":
                        result = EngineDcsDefaultsStrip.ListDeviceLuas(options);
                        break;
                    case "remove":
                    case "cut":
                        result = EngineDcsDefaultsStrip.RemoveDeviceLuas(options);
                        break;
                    case "restore":
                        result = EngineDcsDefaultsStrip.RestoreDeviceLuas(options);
                        break;
                    default:
                        return Fail("Unknown dcs device-luas action: " + action + " (use list, remove or restore).");
                }
            }
            else
            {
                switch (action)
                {
                    case "":
                    case "apply":
                        result = EngineDcsDefaultsStrip.Strip(options);
                        break;
                    case "restore":
                        result = EngineDcsDefaultsStrip.Unstrip(options);
                        break;
                    case "status":
                        result = EngineDcsDefaultsStrip.Status(options);
                        break;
                    case "auto":
                        result = EngineDcsDefaultsStrip.Auto(options);
                        break;
                    default:
                        return Fail("Unknown dcs strip-defaults action: " + action + " (use apply, restore, status or auto).");
                }
            }
            EmitPayload(textOutput ? EngineDcsDefaultsStrip.ToText(result) : EngineSerialization.ToJson(result), outPath);
            if (result.Errors.Count > 0)
                return Fail("dcs " + area + " " + (action.Length == 0 ? "" : action) + " failed: " + string.Join(" | ", result.Errors));
            return 0;
        }

        // engine dcs verify-clean: which DCS default binds DCS itself still applies on the joysticks (EngineDcsRefresh.VerifyClean).
        private static int RunDcsVerifyClean(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string savedGamesPath = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
            if (savedGamesPath != null)
                args.RemoveAt(0);
            savedGamesPath = savedGamesPath ?? PopOptionValue(args, "--path") ?? PopOptionValue(args, "--saved-games");
            string installPath = PopOptionValue(args, "--install-path") ?? PopOptionValue(args, "--install");
            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            bool includeDisconnected = PopOptionBoolean(args, "--include-disconnected", false, out _);
            string maxRaw = PopOptionValue(args, "--max-listed");
            int maxListed = 200;
            if (!string.IsNullOrWhiteSpace(maxRaw) && !int.TryParse(maxRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxListed))
                return Fail("Invalid --max-listed value: '" + maxRaw + "'.");
            if (args.Count > 0)
                return Fail("Unknown dcs verify-clean argument(s): " + string.Join(" ", args));
            GamePathEntry discovered = GamePathDiscovery.DiscoverDcs(new List<string>());
            DcsRefreshOptions options = new DcsRefreshOptions();
            options.SavedGamesPath = NormalizePath(string.IsNullOrWhiteSpace(savedGamesPath) ? discovered.PreferredConfigPath : savedGamesPath);
            options.InstallPath = NormalizePath(string.IsNullOrWhiteSpace(installPath) ? discovered.PreferredInstallPath : installPath);
            options.UseHeadlessLuaRefresh = true;
            options.Confirm = false;
            options.PlaneFilter = NormalizeDcsPlaneFilterTokens(ParseDelimitedOptionValues(planeOptions));
            DcsVerifyCleanResult result = EngineDcsRefresh.VerifyClean(options, ParseDelimitedOptionValues(deviceOptions), includeDisconnected, maxListed);
            EmitPayload(textOutput ? EngineDcsRefresh.ToText(result) : EngineSerialization.ToJson(result), outPath);
            if (result.Errors.Count > 0)
                return Fail("dcs verify-clean failed: " + string.Join(" | ", result.Errors));
            return 0;
        }

        private static int RunDcsRefresh(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? (command[1] ?? string.Empty).Trim().ToLowerInvariant() : "preview-clicks";
            if (string.IsNullOrWhiteSpace(action))
                action = "preview-clicks";

            List<string> args = command.Skip(2).ToList();

            string savedGamesPath = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                savedGamesPath = args[0];
                args.RemoveAt(0);
            }

            string installPath = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                installPath = args[0];
                args.RemoveAt(0);
            }

            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = pathOption;
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = PopOptionValue(args, "--saved-games");
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                savedGamesPath = PopOptionValue(args, "--saved-games-path");

            string installPathOption = PopOptionValue(args, "--install-path");
            if (string.IsNullOrWhiteSpace(installPathOption))
                installPathOption = PopOptionValue(args, "--install");
            if (string.IsNullOrWhiteSpace(installPath))
                installPath = installPathOption;

            string clicksXmlPath = PopOptionValue(args, "--clicks-xml");
            string clicksDirPath = PopOptionValue(args, "--clicks-dir");
            string dcsExePath = PopOptionValue(args, "--dcs-exe");
            string defaultStick = PopOptionValue(args, "--default-stick");
            string previewImagePath = PopOptionValue(args, "--preview-image");
            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            string menuWaitSecondsRaw = PopOptionValue(args, "--menu-wait-seconds");
            if (string.IsNullOrWhiteSpace(menuWaitSecondsRaw))
                menuWaitSecondsRaw = PopOptionValue(args, "--wait-menu-seconds");
            string ocrMenuTimeoutSecondsRaw = PopOptionValue(args, "--ocr-menu-timeout-seconds");
            string settleSecondsRaw = PopOptionValue(args, "--settle-seconds");
            string modulesToScanRaw = PopOptionValue(args, "--modules-to-scan");
            string guiScaleRaw = PopOptionValue(args, "--gui-scale");
            string windowLeftRaw = PopOptionValue(args, "--window-left");
            string windowTopRaw = PopOptionValue(args, "--window-top");
            string windowWidthRaw = PopOptionValue(args, "--window-width");
            string windowHeightRaw = PopOptionValue(args, "--window-height");

            bool useOcr = PopOptionBoolean(args, "--ocr", false, out bool ocrProvided);
            if (!ocrProvided && PopFlag(args, "--use-ocr"))
                useOcr = true;
            bool confirm = PopOptionBoolean(args, "--confirm", true, out _);
            bool launch = PopOptionBoolean(args, "--launch", true, out _);
            bool keepRunning = PopOptionBoolean(args, "--keep-running", false, out _);
            bool dryRun = PopFlag(args, "--dry-run");
            bool previewClicks = PopFlag(args, "--preview-clicks");
            bool headlessLua = PopOptionBoolean(args, "--headless-lua", false, out bool headlessLuaProvided);
            if (!headlessLuaProvided && PopFlag(args, "--headless"))
                headlessLua = true;
            List<string> requestedPlaneFilters = NormalizeDcsPlaneFilterTokens(ParseDelimitedOptionValues(planeOptions));

            int menuWaitSeconds = 45;
            if (!string.IsNullOrWhiteSpace(menuWaitSecondsRaw) &&
                !int.TryParse(menuWaitSecondsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out menuWaitSeconds))
            {
                return Fail("Invalid --menu-wait-seconds value: '" + menuWaitSecondsRaw + "'.");
            }

            int ocrMenuTimeoutSeconds = 600;
            if (!string.IsNullOrWhiteSpace(ocrMenuTimeoutSecondsRaw) &&
                !int.TryParse(ocrMenuTimeoutSecondsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ocrMenuTimeoutSeconds))
            {
                return Fail("Invalid --ocr-menu-timeout-seconds value: '" + ocrMenuTimeoutSecondsRaw + "'.");
            }

            int settleSeconds = 60;
            if (!string.IsNullOrWhiteSpace(settleSecondsRaw) &&
                !int.TryParse(settleSecondsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out settleSeconds))
            {
                return Fail("Invalid --settle-seconds value: '" + settleSecondsRaw + "'.");
            }

            int modulesToScan = 0;
            if (!string.IsNullOrWhiteSpace(modulesToScanRaw) &&
                !int.TryParse(modulesToScanRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out modulesToScan))
            {
                return Fail("Invalid --modules-to-scan value: '" + modulesToScanRaw + "'.");
            }

            double? guiScaleOverride = null;
            if (!string.IsNullOrWhiteSpace(guiScaleRaw))
            {
                if (!double.TryParse(guiScaleRaw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsedGuiScale))
                    return Fail("Invalid --gui-scale value: '" + guiScaleRaw + "'.");
                guiScaleOverride = parsedGuiScale;
            }

            bool hasWindowOverridePart =
                !string.IsNullOrWhiteSpace(windowLeftRaw) ||
                !string.IsNullOrWhiteSpace(windowTopRaw) ||
                !string.IsNullOrWhiteSpace(windowWidthRaw) ||
                !string.IsNullOrWhiteSpace(windowHeightRaw);
            DcsWindowRect windowOverride = null;
            if (hasWindowOverridePart)
            {
                if (string.IsNullOrWhiteSpace(windowLeftRaw) ||
                    string.IsNullOrWhiteSpace(windowTopRaw) ||
                    string.IsNullOrWhiteSpace(windowWidthRaw) ||
                    string.IsNullOrWhiteSpace(windowHeightRaw))
                {
                    return Fail("Window override requires --window-left, --window-top, --window-width, and --window-height.");
                }

                if (!int.TryParse(windowLeftRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int windowLeft) ||
                    !int.TryParse(windowTopRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int windowTop) ||
                    !int.TryParse(windowWidthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int windowWidth) ||
                    !int.TryParse(windowHeightRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int windowHeight))
                {
                    return Fail("Invalid --window-* override values.");
                }

                if (windowWidth <= 0 || windowHeight <= 0)
                    return Fail("--window-width and --window-height must be greater than zero.");

                windowOverride = new DcsWindowRect();
                windowOverride.Left = windowLeft;
                windowOverride.Top = windowTop;
                windowOverride.Right = windowLeft + windowWidth;
                windowOverride.Bottom = windowTop + windowHeight;
            }

            if (args.Count > 0)
                return Fail("Unknown dcs refresh argument(s): " + string.Join(" ", args));

            if (headlessLua && action != "id" && action != "ids" && action != "clean")
                return Fail("--headless-lua is only supported for dcs refresh id/clean.");
            if (requestedPlaneFilters.Count > 0 && action != "id" && action != "ids" && action != "clean")
                return Fail("--plane/--planes are only supported for dcs refresh id/clean.");

            bool requiresPaths = action == "clean" || action == "id" || action == "ids";
            if (requiresPaths && (string.IsNullOrWhiteSpace(savedGamesPath) || string.IsNullOrWhiteSpace(installPath)))
            {
                List<string> discoveryWarnings = new List<string>();
                GamePathEntry discoveredDcs = GamePathDiscovery.DiscoverDcs(discoveryWarnings);
                if (string.IsNullOrWhiteSpace(savedGamesPath) && !string.IsNullOrWhiteSpace(discoveredDcs.PreferredConfigPath))
                {
                    savedGamesPath = discoveredDcs.PreferredConfigPath;
                    Console.Error.WriteLine("Auto-detected DCS Saved Games path: " + savedGamesPath);
                }
                if (string.IsNullOrWhiteSpace(installPath) && !string.IsNullOrWhiteSpace(discoveredDcs.PreferredInstallPath))
                {
                    installPath = discoveredDcs.PreferredInstallPath;
                    Console.Error.WriteLine("Auto-detected DCS install path: " + installPath);
                }
                for (int i = 0; i < discoveryWarnings.Count; i++)
                    Console.Error.WriteLine(discoveryWarnings[i]);
            }

            if (requiresPaths)
            {
                if (string.IsNullOrWhiteSpace(savedGamesPath))
                    return Fail("dcs refresh " + action + " requires a DCS Saved Games path.");
                if (string.IsNullOrWhiteSpace(installPath))
                    return Fail("dcs refresh " + action + " requires a DCS install path.");
            }

            DcsRefreshOptions options = new DcsRefreshOptions();
            options.SavedGamesPath = NormalizePath(savedGamesPath);
            options.InstallPath = NormalizePath(installPath);
            options.ClicksXmlPath = NormalizePath(clicksXmlPath);
            options.ClicksImageFolder = NormalizePath(clicksDirPath);
            options.DcsExecutablePath = NormalizePath(dcsExePath);
            options.DefaultStickReference = defaultStick ?? string.Empty;
            options.UseOcr = useOcr;
            options.Confirm = confirm;
            options.DryRun = dryRun;
            options.PreviewClicks = previewClicks;
            options.PreviewImagePath = NormalizePath(previewImagePath);
            options.LaunchDcs = launch;
            options.KeepDcsRunning = keepRunning;
            options.MenuWaitSeconds = menuWaitSeconds;
            options.OcrMenuTimeoutSeconds = ocrMenuTimeoutSeconds;
            options.SettleSeconds = settleSeconds;
            options.ModulesToScan = modulesToScan;
            options.UseHeadlessLuaRefresh = headlessLua;
            options.PlaneFilter = requestedPlaneFilters;
            options.GuiScaleOverride = guiScaleOverride;
            options.WindowOverride = windowOverride;
            options.StatusSink = BuildStatusSink("dcs refresh " + action);

            switch (action)
            {
                case "preview":
                case "preview-clicks":
                case "click-preview":
                case "show-clicks":
                    {
                        DcsClickPreviewResult preview = EngineDcsRefresh.PreviewClicks(options);
                        string payload = textOutput ? ToText(preview) : EngineSerialization.ToJson(preview);
                        EmitPayload(payload, outPath);
                        if (preview.Errors != null && preview.Errors.Count > 0)
                            return Fail("dcs refresh preview-clicks failed: " + string.Join(" | ", preview.Errors));
                        return 0;
                    }
                case "clean":
                    {
                        DcsRefreshResult refresh = EngineDcsRefresh.RefreshClean(options);
                        string payload = textOutput ? ToText(refresh) : EngineSerialization.ToJson(refresh);
                        EmitPayload(payload, outPath);
                        if (refresh.Errors != null && refresh.Errors.Count > 0)
                            return Fail("dcs refresh clean failed: " + string.Join(" | ", refresh.Errors));
                        return 0;
                    }
                case "id":
                case "ids":
                    {
                        DcsRefreshResult refresh = EngineDcsRefresh.RefreshId(options);
                        string payload = textOutput ? ToText(refresh) : EngineSerialization.ToJson(refresh);
                        EmitPayload(payload, outPath);
                        if (refresh.Errors != null && refresh.Errors.Count > 0)
                            return Fail("dcs refresh id failed: " + string.Join(" | ", refresh.Errors));
                        if (!options.DryRun)
                            DbUpdateCheck.RecordCurrent("DCS", null);
                        return 0;
                    }
                default:
                    return Fail("Unknown dcs refresh action: " + action);
            }
        }

        private static DcsStickDefaultsResult CutDcsStickDefaults(string savedGamesPath, string installPath, string backupPath)
        {
            DcsStickDefaultsResult result = new DcsStickDefaultsResult();
            result.Action = "cut";
            result.SavedGamesPath = savedGamesPath ?? string.Empty;
            result.InstallPath = installPath ?? string.Empty;
            result.BackupPath = backupPath ?? string.Empty;

            string aircraftRoot = ResolveDcsAircraftRoot(installPath);
            result.AircraftRoot = aircraftRoot;
            if (string.IsNullOrWhiteSpace(aircraftRoot) || !Directory.Exists(aircraftRoot))
            {
                result.Errors.Add("Couldn't find DCS install aircraft path under: " + installPath);
                return result;
            }

            try
            {
                Directory.CreateDirectory(backupPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed creating backup folder '" + backupPath + "': " + ex.Message);
                return result;
            }

            string[] aircraftDirs;
            try
            {
                aircraftDirs = Directory.GetDirectories(aircraftRoot);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed listing DCS aircraft modules at '" + aircraftRoot + "': " + ex.Message);
                return result;
            }

            for (int i = 0; i < aircraftDirs.Length; i++)
            {
                string aircraftDir = aircraftDirs[i];
                string aircraftName = Path.GetFileName(aircraftDir) ?? string.Empty;
                if (aircraftName.Length == 0)
                    continue;

                result.AircraftScanned++;
                string inputRoot = Path.Combine(aircraftDir, "Input");
                if (!Directory.Exists(inputRoot))
                    continue;

                string[] inputDirs;
                try
                {
                    inputDirs = Directory.GetDirectories(inputRoot);
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Failed listing input folders for '" + aircraftName + "': " + ex.Message);
                    continue;
                }

                for (int j = 0; j < inputDirs.Length; j++)
                {
                    string inputDir = inputDirs[j];
                    string inputName = Path.GetFileName(inputDir) ?? string.Empty;
                    if (inputName.Length == 0)
                        continue;

                    string joystickDir = Path.Combine(inputDir, "joystick");
                    if (!Directory.Exists(joystickDir))
                        continue;

                    result.JoystickFoldersScanned++;
                    string backupJoystickDir = Path.Combine(backupPath, aircraftName, "Input", inputName, "joystick");
                    try
                    {
                        Directory.CreateDirectory(backupJoystickDir);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(
                            "Failed creating backup joystick folder '" + backupJoystickDir + "': " + ex.Message);
                        continue;
                    }

                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(joystickDir);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add("Failed listing files in '" + joystickDir + "': " + ex.Message);
                        continue;
                    }

                    for (int f = 0; f < files.Length; f++)
                    {
                        string sourceFile = files[f];
                        string fileName = Path.GetFileName(sourceFile) ?? string.Empty;
                        if (fileName.Length == 0)
                            continue;

                        string targetFile = Path.Combine(backupJoystickDir, fileName);
                        GameBackups.Protect(targetFile, result.Warnings);
                        if (string.Equals(fileName, "default.lua", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                File.Copy(sourceFile, targetFile, true);
                                result.DefaultFilesCopied++;
                            }
                            catch (Exception ex)
                            {
                                result.Errors.Add("Failed copying default file '" + sourceFile + "': " + ex.Message);
                            }
                            continue;
                        }

                        try
                        {
                            GameBackups.Protect(sourceFile, result.Warnings);
                            if (File.Exists(targetFile))
                                File.Delete(targetFile);
                            File.Move(sourceFile, targetFile);
                            result.StickSpecificFilesMoved++;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add("Failed moving stick-specific file '" + sourceFile + "': " + ex.Message);
                        }
                    }
                }
            }

            return result;
        }

        private static DcsStickDefaultsResult RestoreDcsStickDefaults(string savedGamesPath, string installPath, string backupPath)
        {
            DcsStickDefaultsResult result = new DcsStickDefaultsResult();
            result.Action = "restore";
            result.SavedGamesPath = savedGamesPath ?? string.Empty;
            result.InstallPath = installPath ?? string.Empty;
            result.BackupPath = backupPath ?? string.Empty;

            string aircraftRoot = ResolveDcsAircraftRoot(installPath);
            result.AircraftRoot = aircraftRoot;
            if (string.IsNullOrWhiteSpace(aircraftRoot) || !Directory.Exists(aircraftRoot))
            {
                result.Errors.Add("Couldn't find DCS install aircraft path under: " + installPath);
                return result;
            }

            if (!Directory.Exists(backupPath))
            {
                result.Errors.Add("No backup folder found at: " + backupPath);
                return result;
            }

            string[] aircraftBackupDirs;
            try
            {
                aircraftBackupDirs = Directory.GetDirectories(backupPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Failed listing backup folders in '" + backupPath + "': " + ex.Message);
                return result;
            }

            for (int i = 0; i < aircraftBackupDirs.Length; i++)
            {
                string sourceAircraftDir = aircraftBackupDirs[i];
                string aircraftName = Path.GetFileName(sourceAircraftDir) ?? string.Empty;
                if (aircraftName.Length == 0)
                    continue;

                result.AircraftScanned++;
                string targetAircraftDir = Path.Combine(aircraftRoot, aircraftName);
                try
                {
                    CopyDirectoryContentsRecursive(sourceAircraftDir, targetAircraftDir, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add("Failed restoring backup for '" + aircraftName + "': " + ex.Message);
                }
            }

            return result;
        }

        private static string ResolveDcsAircraftRoot(string installPath)
        {
            string normalized = NormalizePath(installPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            string trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string modsAircraft = Path.Combine(trimmed, "Mods", "aircraft");
            if (Directory.Exists(modsAircraft))
                return modsAircraft;

            if (string.Equals(Path.GetFileName(trimmed), "mods", StringComparison.OrdinalIgnoreCase))
            {
                string aircraftUnderMods = Path.Combine(trimmed, "aircraft");
                if (Directory.Exists(aircraftUnderMods))
                    return aircraftUnderMods;
            }

            if (string.Equals(Path.GetFileName(trimmed), "aircraft", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(trimmed))
            {
                return trimmed;
            }

            return string.Empty;
        }

        private static void CopyDirectoryContentsRecursive(string sourceDir, string targetDir, DcsStickDefaultsResult result)
        {
            Directory.CreateDirectory(targetDir);
            result.DirectoriesTouched++;

            string[] files = Directory.GetFiles(sourceDir);
            for (int i = 0; i < files.Length; i++)
            {
                string sourceFile = files[i];
                string targetFile = Path.Combine(targetDir, Path.GetFileName(sourceFile) ?? string.Empty);
                GameBackups.Protect(targetFile, result.Warnings);
                File.Copy(sourceFile, targetFile, true);
                result.FilesRestored++;
            }

            string[] subDirectories = Directory.GetDirectories(sourceDir);
            for (int i = 0; i < subDirectories.Length; i++)
            {
                string sourceSubDirectory = subDirectories[i];
                string targetSubDirectory = Path.Combine(targetDir, Path.GetFileName(sourceSubDirectory) ?? string.Empty);
                CopyDirectoryContentsRecursive(sourceSubDirectory, targetSubDirectory, result);
            }
        }

        private static bool TryResolveDcsFfbDevices(
            List<string> requestedFilters,
            string savedGamesPath,
            bool confirmSuggestions,
            string scope,
            out List<string> resolvedFilters,
            out List<string> warnings,
            out string error)
        {
            warnings = new List<string>();
            List<string> requested = (requestedFilters ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            resolvedFilters = new List<string>();
            error = string.Empty;
            if (requested.Count == 0)
                return true;

            List<DeviceCandidate> candidates = BuildDcsFfbDeviceCandidates(savedGamesPath, warnings);

            foreach (string token in requested)
            {
                if (TryResolveRequestedDcsFfbDeviceToken(token, candidates, confirmSuggestions, scope, warnings, out string resolved))
                {
                    resolvedFilters.Add(resolved);
                    continue;
                }

                resolvedFilters.Add(token);
            }

            resolvedFilters = resolvedFilters
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        private static bool TryResolveRequestedDcsFfbDeviceToken(
            string token,
            List<DeviceCandidate> candidates,
            bool confirmSuggestions,
            string scope,
            List<string> warnings,
            out string resolved)
        {
            resolved = token ?? string.Empty;
            string request = (token ?? string.Empty).Trim();
            if (request.Length == 0)
                return false;

            DeviceCandidate fullMatch = candidates.FirstOrDefault(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.Name) &&
                string.Equals(c.Name, request, StringComparison.OrdinalIgnoreCase));
            if (fullMatch != null)
            {
                resolved = fullMatch.Name;
                return true;
            }

            string tokenBase = StripDeviceGuidSuffix(request);
            List<DeviceCandidate> baseMatches = candidates
                .Where(c =>
                    c != null &&
                    !string.IsNullOrWhiteSpace(c.Name) &&
                    string.Equals(StripDeviceGuidSuffix(c.Name), tokenBase, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (baseMatches.Count == 1)
            {
                resolved = baseMatches[0].Name;
                return true;
            }

            if (confirmSuggestions && baseMatches.Count > 1)
            {
                AddUnique(
                    warnings,
                    "Multiple device matches found for '" + request + "' in " + scope + ". Using requested token as-is.");
            }

            resolved = request;
            return false;
        }

        private static int SetBind(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 1)
                return Fail("binds set requires: <bind-or-relation> plus one or more options.");

            string query = args[0];
            args.RemoveAt(0);

            string deviceOption;
            string inputOption;
            string formatRaw;
            bool clearInput;
            bool clearModifiers;
            bool inverted;
            bool invertedProvided;
            bool slider;
            bool sliderProvided;
            bool doubleTap;
            bool doubleTapProvided;
            string deadzoneRaw;
            string satXRaw;
            string satYRaw;
            List<string> curvatureOptions;
            List<string> modifierOptions;
            try
            {
                deviceOption = PopOptionValue(args, "--device");
                inputOption = PopOptionValue(args, "--input");
                formatRaw = PopOptionValue(args, "--format");
                clearInput = PopFlag(args, "--clear-input");
                clearModifiers = PopFlag(args, "--clear-modifiers");
                inverted = PopOptionBoolean(args, "--inverted", false, out invertedProvided);
                slider = PopOptionBoolean(args, "--slider", false, out sliderProvided);
                doubleTap = PopOptionBoolean(args, "--double-tap", false, out doubleTapProvided);
                deadzoneRaw = PopOptionValue(args, "--deadzone");
                satXRaw = PopOptionValue(args, "--saturation-x");
                satYRaw = PopOptionValue(args, "--saturation-y");
                curvatureOptions = PopOptionValues(args, "--curvature");
                modifierOptions = PopOptionValues(args, "--modifier");
                modifierOptions.AddRange(PopOptionValues(args, "--mod"));
                modifierOptions.AddRange(PopOptionValues(args, "--reformer"));
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }

            if (args.Count > 0)
                return Fail("Unknown binds set argument(s): " + string.Join(" ", args));

            bool deadzoneProvided = !string.IsNullOrWhiteSpace(deadzoneRaw);
            bool satXProvided = !string.IsNullOrWhiteSpace(satXRaw);
            bool satYProvided = !string.IsNullOrWhiteSpace(satYRaw);
            bool curvatureProvided = curvatureOptions.Count > 0;
            bool modifiersProvided = modifierOptions.Count > 0;

            bool hasAnyChange =
                !string.IsNullOrWhiteSpace(deviceOption) ||
                !string.IsNullOrWhiteSpace(inputOption) ||
                clearInput ||
                clearModifiers ||
                modifiersProvided ||
                invertedProvided ||
                sliderProvided ||
                deadzoneProvided ||
                satXProvided ||
                satYProvided ||
                curvatureProvided ||
                doubleTapProvided;
            if (!hasAnyChange)
                return Fail("binds set requires at least one change option (e.g. --device, --input, --curvature, --inverted).");

            double deadzone = 0.0;
            if (deadzoneProvided && !TryParseInvariantDouble(deadzoneRaw, out deadzone))
                return Fail("Invalid --deadzone value '" + deadzoneRaw + "'. Use a number like 0.15.");

            double saturationX = 0.0;
            if (satXProvided && !TryParseInvariantDouble(satXRaw, out saturationX))
                return Fail("Invalid --saturation-x value '" + satXRaw + "'. Use a number like 0.9.");

            double saturationY = 0.0;
            if (satYProvided && !TryParseInvariantDouble(satYRaw, out saturationY))
                return Fail("Invalid --saturation-y value '" + satYRaw + "'. Use a number like 0.8.");

            List<double> curvature = null;
            if (curvatureProvided)
            {
                if (!TryParseDoubleList(curvatureOptions, out curvature, out string curvatureError))
                    return Fail(curvatureError);
            }

            string normalizedInput = null;
            if (!string.IsNullOrWhiteSpace(inputOption))
            {
                string format = string.IsNullOrWhiteSpace(formatRaw) ? "dcs" : formatRaw.Trim().ToLowerInvariant();
                normalizedInput = NormalizeInputForFormat(inputOption, format, out string normalizeError);
                if (string.IsNullOrWhiteSpace(normalizedInput))
                    return Fail(normalizeError);
            }
            else if (!string.IsNullOrWhiteSpace(formatRaw))
            {
                return Fail("--format requires --input.");
            }

            List<string> candidates;
            string relationName;
            if (!TryResolveRelationOrBindName(query, out relationName, out candidates))
                return FailResolution("relation/bind", query, candidates);
            if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                return Fail("Relation not found: " + relationName);

            Relation relation = InternalDataManagement.AllRelations[relationName];
            if (relation == null)
                return Fail("Relation payload is null: " + relationName);

            bool axisTuningRequested =
                invertedProvided ||
                sliderProvided ||
                deadzoneProvided ||
                satXProvided ||
                satYProvided ||
                curvatureProvided;
            if (axisTuningRequested && !relation.ISAXIS)
                return Fail("Axis tuning options (--inverted/--slider/--curvature/--deadzone/--saturation-x/--saturation-y) can only be set on axis relations.");

            Bind bind = null;
            if (InternalDataManagement.AllBinds.ContainsKey(relationName))
                bind = InternalDataManagement.AllBinds[relationName];
            if (bind == null)
                bind = new Bind(relation);
            if (bind.AllReformers == null)
                bind.AllReformers = new List<string>();

            if (!string.IsNullOrWhiteSpace(deviceOption))
            {
                bind.Joystick = deviceOption.Trim();
                bind.JoyBackup = bind.Joystick;
            }

            if (clearInput)
            {
                bind.JAxis = string.Empty;
                bind.JButton = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(normalizedInput))
            {
                if (relation.ISAXIS)
                {
                    bind.JAxis = normalizedInput;
                    bind.JButton = string.Empty;
                }
                else
                {
                    bind.JButton = normalizedInput;
                    bind.JAxis = string.Empty;
                }
            }

            if (modifiersProvided)
            {
                bind.AllReformers = NormalizeModifierInputs(modifierOptions);
            }
            else if (clearModifiers)
            {
                bind.AllReformers.Clear();
            }

            if (invertedProvided)
                bind.Inverted = inverted;
            if (sliderProvided)
                bind.Slider = slider;
            if (deadzoneProvided)
                bind.Deadzone = deadzone;
            if (satXProvided)
                bind.SaturationX = saturationX;
            if (satYProvided)
                bind.SaturationY = saturationY;
            if (curvatureProvided)
                bind.Curvature = curvature.Count == 0 ? new List<double> { 0.0 } : curvature;
            if (doubleTapProvided)
                bind.doubleTap = doubleTap;

            bind.Rl = relation;
            relation.bind = bind;
            InternalDataManagement.AllBinds[relationName] = bind;
            InternalDataManagement.AllRelations[relationName] = relation;

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return ShowBind(new List<string> { "show", relationName }, outPath, textOutput);
        }

        private static int ClearBind(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 1)
                return Fail("binds clear requires: <bind-or-relation> [--input-only].");

            string query = args[0];
            args.RemoveAt(0);
            bool inputOnly = PopFlag(args, "--input-only");
            if (args.Count > 0)
                return Fail("Unknown binds clear argument(s): " + string.Join(" ", args));

            List<string> candidates;
            string relationName;
            if (!TryResolveRelationOrBindName(query, out relationName, out candidates))
                return FailResolution("relation/bind", query, candidates);
            if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                return Fail("Relation not found: " + relationName);

            bool hadBind = InternalDataManagement.AllBinds.ContainsKey(relationName) &&
                           InternalDataManagement.AllBinds[relationName] != null;
            bool removedBind = false;
            bool clearedInput = false;

            if (hadBind)
            {
                Bind bind = InternalDataManagement.AllBinds[relationName];
                if (inputOnly)
                {
                    clearedInput = !string.IsNullOrWhiteSpace(bind.JAxis) || !string.IsNullOrWhiteSpace(bind.JButton);
                    bind.JAxis = string.Empty;
                    bind.JButton = string.Empty;
                    InternalDataManagement.AllBinds[relationName] = bind;
                    if (InternalDataManagement.AllRelations[relationName] != null)
                        InternalDataManagement.AllRelations[relationName].bind = bind;
                }
                else
                {
                    InternalDataManagement.AllBinds.Remove(relationName);
                    if (InternalDataManagement.AllRelations[relationName] != null)
                        InternalDataManagement.AllRelations[relationName].bind = null;
                    removedBind = true;
                }
            }

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            BindClearResult result = new BindClearResult();
            result.Relation = relationName;
            result.InputOnly = inputOnly;
            result.HadBind = hadBind;
            result.ClearedInput = clearedInput;
            result.RemovedBind = removedBind;

            string payload = textOutput
                ? ToText(result)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RenameRelationGroup(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count < 2)
                return Fail("relations group rename requires: <old-group> <new-group>.");
            if (args.Count > 2)
                return Fail("Unknown relations group rename argument(s): " + string.Join(" ", args.Skip(2)));

            string oldName = (args[0] ?? string.Empty).Trim();
            string newName = (args[1] ?? string.Empty).Trim();
            if (oldName.Length == 0 || newName.Length == 0)
                return Fail("relations group rename requires non-empty group names.");
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return Fail("relations group rename requires different old/new names.");

            List<string> changedRelations = new List<string>();
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.Groups == null || relation.Groups.Count == 0)
                    continue;

                bool hasOld = relation.Groups.Any(g => !string.IsNullOrWhiteSpace(g) && string.Equals(g.Trim(), oldName, StringComparison.OrdinalIgnoreCase));
                if (!hasOld)
                    continue;

                List<string> rebuilt = new List<string>();
                for (int i = 0; i < relation.Groups.Count; i++)
                {
                    string raw = relation.Groups[i];
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string value = raw.Trim();
                    if (string.Equals(value, oldName, StringComparison.OrdinalIgnoreCase))
                        AddUniqueGroup(rebuilt, newName);
                    else
                        AddUniqueGroup(rebuilt, value);
                }

                relation.Groups = rebuilt;
                changedRelations.Add(entry.Key);
            }

            if (changedRelations.Count == 0)
                return Fail("Group not found: " + oldName);

            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationGroupRenameResult result = new RelationGroupRenameResult();
            result.OldName = oldName;
            result.NewName = newName;
            result.UpdatedRelations = changedRelations.OrderBy(v => v).ToList();
            result.UpdatedCount = result.UpdatedRelations.Count;

            string payload = textOutput
                ? ToText(result)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int EditBindModifiers(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 1)
                return Fail("binds modifier requires: list [<bind-or-relation>] | add|remove <bind-or-relation> <modifier...> | rename <old-modifier> <new-modifier> [<bind-or-relation>].");

            string operation = args[0].ToLowerInvariant();
            if (operation == "list")
                return ListBindModifiers(args.Skip(1).ToList(), outPath, textOutput);
            if (operation == "rename")
                return RenameBindModifier(args.Skip(1).ToList(), outPath, textOutput);

            if (args.Count < 2)
                return Fail("binds modifier requires: add|remove <bind-or-relation> <modifier...>.");

            string bindQuery = args[1];
            List<string> modifierInputs = args.Skip(2).ToList();
            bool clearAll = PopFlag(modifierInputs, "--all");

            if (modifierInputs.Any(v => v.StartsWith("--", StringComparison.Ordinal)))
                return Fail("Unknown binds modifier option(s): " + string.Join(" ", modifierInputs.Where(v => v.StartsWith("--", StringComparison.Ordinal))));

            List<string> bindCandidates;
            string relationName;
            if (!TryResolveBindRelationName(bindQuery, out relationName, out bindCandidates))
                return FailResolution("bind", bindQuery, bindCandidates);
            if (!InternalDataManagement.AllBinds.ContainsKey(relationName))
                return Fail("Bind not found for relation: " + relationName);

            Bind bind = InternalDataManagement.AllBinds[relationName];
            if (bind == null)
                return Fail("Bind payload is null for relation: " + relationName);
            if (bind.AllReformers == null)
                bind.AllReformers = new List<string>();

            switch (operation)
            {
                case "add":
                    {
                        List<string> modifiersToAdd = NormalizeModifierInputs(modifierInputs);
                        if (modifiersToAdd.Count == 0)
                            return Fail("binds modifier add requires at least one modifier.");

                        for (int i = 0; i < modifiersToAdd.Count; i++)
                        {
                            string reformer = modifiersToAdd[i];
                            if (!bind.AllReformers.Any(existing => string.Equals(existing, reformer, StringComparison.OrdinalIgnoreCase)))
                                bind.AllReformers.Add(reformer);
                        }
                        break;
                    }
                case "remove":
                    {
                        if (clearAll)
                        {
                            bind.AllReformers.Clear();
                        }
                        else
                        {
                            List<string> modifiersToRemove = NormalizeModifierInputs(modifierInputs);
                            if (modifiersToRemove.Count == 0)
                                return Fail("binds modifier remove requires at least one modifier or --all.");

                            HashSet<string> removalNames = new HashSet<string>(
                                modifiersToRemove.Select(ModifierNameFromReformer),
                                StringComparer.OrdinalIgnoreCase);

                            for (int i = bind.AllReformers.Count - 1; i >= 0; i--)
                            {
                                string existing = bind.AllReformers[i];
                                string existingName = ModifierNameFromReformer(existing);
                                if (removalNames.Contains(existingName) || modifiersToRemove.Any(r => string.Equals(r, existing, StringComparison.OrdinalIgnoreCase)))
                                    bind.AllReformers.RemoveAt(i);
                            }
                        }
                        break;
                    }
                default:
                    return Fail("Unknown binds modifier action: " + operation);
            }

            InternalDataManagement.AllBinds[relationName] = bind;
            if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                InternalDataManagement.AllRelations[relationName].bind = bind;

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return ShowBind(new List<string> { "show", relationName }, outPath, textOutput);
        }

        private static int ListBindModifiers(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count > 1)
                return Fail("binds modifier list accepts at most one bind/relation selector.");

            if (args.Count == 1)
            {
                string bindQuery = args[0];
                List<string> bindCandidates;
                string relationName;
                if (!TryResolveBindRelationName(bindQuery, out relationName, out bindCandidates))
                    return FailResolution("bind", bindQuery, bindCandidates);
                if (!InternalDataManagement.AllBinds.ContainsKey(relationName))
                    return Fail("Bind not found for relation: " + relationName);

                Bind bind = InternalDataManagement.AllBinds[relationName];
                List<string> reformers = bind == null || bind.AllReformers == null
                    ? new List<string>()
                    : bind.AllReformers
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                List<string> modifiers = reformers
                    .Select(ModifierNameFromReformer)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v)
                    .ToList();

                BindModifierListResult relationResult = new BindModifierListResult();
                relationResult.Scope = "bind";
                relationResult.Relation = relationName;
                relationResult.Modifiers = modifiers;
                relationResult.Reformers = reformers.OrderBy(v => v).ToList();

                string relationPayload = textOutput
                    ? ToText(relationResult)
                    : EngineSerialization.ToJson(relationResult);
                EmitPayload(relationPayload, outPath);
                return 0;
            }

            Dictionary<string, HashSet<string>> map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in InternalDataManagement.AllBinds)
            {
                string relationName = entry.Key;
                Bind bind = entry.Value;
                if (bind == null || bind.AllReformers == null)
                    continue;

                for (int i = 0; i < bind.AllReformers.Count; i++)
                {
                    string reformer = bind.AllReformers[i];
                    string name = ModifierNameFromReformer(reformer);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    string modifierName = name.Trim();
                    if (!map.ContainsKey(modifierName))
                        map.Add(modifierName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    map[modifierName].Add(relationName);
                }
            }

            BindModifierListResult globalResult = new BindModifierListResult();
            globalResult.Scope = "global";
            globalResult.Modifiers = map.Keys.OrderBy(v => v).ToList();
            globalResult.Entries = map
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp =>
                {
                    BindModifierListEntry item = new BindModifierListEntry();
                    item.Name = kvp.Key;
                    item.Relations = kvp.Value.OrderBy(v => v).ToList();
                    item.BindCount = item.Relations.Count;
                    return item;
                })
                .ToList();

            string payload = textOutput
                ? ToText(globalResult)
                : EngineSerialization.ToJson(globalResult);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RenameBindModifier(List<string> args, string outPath, bool textOutput)
        {
            if (args.Count < 2)
                return Fail("binds modifier rename requires: <old-modifier> <new-modifier> [<bind-or-relation>].");

            string oldName = (args[0] ?? string.Empty).Trim();
            string newName = (args[1] ?? string.Empty).Trim();
            if (oldName.Length == 0 || newName.Length == 0)
                return Fail("binds modifier rename requires non-empty modifier names.");
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                return Fail("binds modifier rename requires different old/new names.");

            List<string> targetRelations;
            string scope;
            string targetRelation = null;

            if (args.Count > 2)
            {
                string query = string.Join(" ", args.Skip(2)).Trim();
                if (query.Length == 0)
                    return Fail("binds modifier rename received an empty bind/relation selector.");

                List<string> bindCandidates;
                if (!TryResolveBindRelationName(query, out targetRelation, out bindCandidates))
                    return FailResolution("bind", query, bindCandidates);
                if (!InternalDataManagement.AllBinds.ContainsKey(targetRelation))
                    return Fail("Bind not found for relation: " + targetRelation);

                targetRelations = new List<string> { targetRelation };
                scope = "bind";
            }
            else
            {
                targetRelations = InternalDataManagement.AllBinds.Keys.OrderBy(v => v).ToList();
                scope = "global";
            }

            List<string> changedRelations = new List<string>();
            for (int r = 0; r < targetRelations.Count; r++)
            {
                string relationName = targetRelations[r];
                if (!InternalDataManagement.AllBinds.ContainsKey(relationName))
                    continue;

                Bind bind = InternalDataManagement.AllBinds[relationName];
                if (bind == null || bind.AllReformers == null || bind.AllReformers.Count == 0)
                    continue;

                bool changed = false;
                List<string> rebuilt = new List<string>();
                for (int i = 0; i < bind.AllReformers.Count; i++)
                {
                    string reformer = bind.AllReformers[i];
                    string existingName = ModifierNameFromReformer(reformer);
                    string normalized = reformer;
                    if (string.Equals(existingName, oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = ReplaceModifierNameInReformer(reformer, newName);
                        changed = true;
                    }

                    if (!rebuilt.Any(v => string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase)))
                        rebuilt.Add(normalized);
                }

                if (!changed)
                    continue;

                bind.AllReformers = rebuilt;
                InternalDataManagement.AllBinds[relationName] = bind;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = bind;
                changedRelations.Add(relationName);
            }

            if (changedRelations.Count == 0)
                return Fail("Modifier not found: " + oldName);

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            BindModifierRenameResult result = new BindModifierRenameResult();
            result.Scope = scope;
            result.Relation = targetRelation ?? string.Empty;
            result.OldName = oldName;
            result.NewName = newName;
            result.UpdatedRelations = changedRelations.OrderBy(v => v).ToList();
            result.UpdatedCount = result.UpdatedRelations.Count;

            string payload = textOutput
                ? ToText(result)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int EditRelationItems(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 2)
                return Fail("relations item requires: add|remove|enable|disable <relation-name> [--node ... | --id ...].");

            string operation = args[0].ToLowerInvariant();
            string relationQuery = args[1];
            List<string> options = args.Skip(2).ToList();

            List<string> nodeSpecs = PopOptionValues(options, "--node");
            string idOption = PopOptionValue(options, "--id");
            string gameOption = PopOptionValue(options, "--game");
            string planesOption = PopOptionValue(options, "--planes");
            bool clearAll = PopFlag(options, "--all");

            if (options.Count > 0)
                return Fail("Unknown relations item argument(s): " + string.Join(" ", options));

            List<NodeSpec> targets = new List<NodeSpec>();
            for (int i = 0; i < nodeSpecs.Count; i++)
            {
                NodeSpec parsed;
                if (!TryParseNodeSpec(nodeSpecs[i], out parsed))
                    return Fail("Invalid --node value '" + nodeSpecs[i] + "'. Use --node <game:id[:plane1|plane2]>.");
                targets.Add(parsed);
            }

            if (!string.IsNullOrWhiteSpace(idOption))
            {
                NodeSpec single = new NodeSpec();
                single.Id = idOption.Trim();
                single.Game = string.IsNullOrWhiteSpace(gameOption) ? "DCS" : gameOption.Trim();
                single.Planes = ParsePlaneList(planesOption);
                targets.Add(single);
            }

            if (operation == "add")
                ApplyCatalogAircraft(targets);
            if ((operation == "add" || operation == "enable" || operation == "disable") && targets.Count == 0)
                return Fail("relations item " + operation + " requires at least one target via --node or --id.");
            if (operation == "remove" && !clearAll && targets.Count == 0)
                return Fail("relations item remove requires --all or at least one target via --node or --id.");

            List<string> relationCandidates;
            string relationName;
            if (!TryResolveRelationName(relationQuery, out relationName, out relationCandidates))
                return FailResolution("relation", relationQuery, relationCandidates);
            if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                return Fail("Relation not found: " + relationName);

            Relation relation = InternalDataManagement.AllRelations[relationName];
            if (relation.NODES == null)
                relation.NODES = new List<RelationItem>();

            int changed = 0;
            switch (operation)
            {
                case "add":
                    changed = ApplyRelationItemAdd(relation, targets);
                    break;
                case "remove":
                    changed = ApplyRelationItemRemove(relation, targets, clearAll);
                    break;
                case "enable":
                    changed = ApplyRelationItemEnableDisable(relation, targets, true);
                    break;
                case "disable":
                    changed = ApplyRelationItemEnableDisable(relation, targets, false);
                    break;
                default:
                    return Fail("Unknown relations item action: " + operation);
            }

            if (changed == 0)
                return Fail("No relation items were changed.");

            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return ShowRelation(new List<string> { "show", relationName }, outPath, textOutput);
        }

        private static int ApplyRelationItemAdd(Relation relation, List<NodeSpec> targets)
        {
            int changed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                NodeSpec target = targets[i];
                if (target == null || string.IsNullOrWhiteSpace(target.Id))
                    continue;

                List<string> planes = PlanesForNewNode(target);
                if (planes.Count == 0)
                {
                    if (relation.AddNode(target.Id, target.Game, relation.ISAXIS))
                        changed++;
                    continue;
                }

                for (int p = 0; p < planes.Count; p++)
                {
                    if (relation.AddNode(target.Id, target.Game, relation.ISAXIS, planes[p], false))
                        changed++;
                }
            }
            return changed;
        }

        // The planes a new node gets: the ones given, else for DCS the aircraft where the id has its most common meaning
        // (EngineIdMeanings: same or near title and category). Aircraft where the same id means something else are not
        // added (a DCS node without active aircraft is not exported); their count goes to the warnings.
        private static List<string> PlanesForNewNode(NodeSpec spec)
        {
            if (spec.Planes != null && spec.Planes.Count > 0)
                return spec.Planes;
            if (string.Equals(string.IsNullOrWhiteSpace(spec.Game) ? "DCS" : spec.Game.Trim(), "DCS", StringComparison.OrdinalIgnoreCase))
            {
                List<EngineIdMeanings.Cluster> clusters = EngineIdMeanings.Clusters("DCS", spec.Id);
                if (clusters.Count == 0)
                    return InternalDataManagement.DcsPlanesWithId(spec.Id);
                if (clusters.Count > 1)
                {
                    int left = clusters.Skip(1).Sum(c => c.Count);
                    Console.Error.WriteLine("Note: " + spec.Id + " means '" + clusters[0].Head.Title + "' in " + clusters[0].Count.ToString(CultureInfo.InvariantCulture) +
                                            " aircraft; the " + left.ToString(CultureInfo.InvariantCulture) + " aircraft where it means something else were left out.");
                }
                return clusters[0].Planes.ToList();
            }
            return new List<string>();
        }

        private static int ApplyRelationItemRemove(Relation relation, List<NodeSpec> targets, bool clearAll)
        {
            if (clearAll)
            {
                int count = relation.NODES == null ? 0 : relation.NODES.Count;
                relation.NODES = new List<RelationItem>();
                return count;
            }

            int changed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                NodeSpec target = targets[i];
                List<RelationItem> matches = FindMatchingRelationItems(relation, target);
                if (matches.Count == 0)
                    continue;

                if (target.Planes == null || target.Planes.Count == 0)
                {
                    for (int m = 0; m < matches.Count; m++)
                    {
                        relation.NODES.Remove(matches[m]);
                        changed++;
                    }
                    continue;
                }

                for (int m = 0; m < matches.Count; m++)
                {
                    RelationItem item = matches[m];
                    for (int p = 0; p < target.Planes.Count; p++)
                    {
                        string plane = target.Planes[p];
                        if (item.GetStateAircraft(plane) == PlaneState.NOT_EXISTENT)
                            continue;
                        item.DeleteAircraftFromActivity(plane);
                        changed++;
                    }
                    if (item.AIRCRAFT == null || item.AIRCRAFT.Count == 0)
                        relation.NODES.Remove(item);
                }
            }

            return changed;
        }

        private static int ApplyRelationItemEnableDisable(Relation relation, List<NodeSpec> targets, bool enabled)
        {
            int changed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                NodeSpec target = targets[i];
                List<RelationItem> matches = FindMatchingRelationItems(relation, target);
                if (matches.Count == 0)
                    continue;

                for (int m = 0; m < matches.Count; m++)
                {
                    RelationItem item = matches[m];
                    List<string> planes = target.Planes == null || target.Planes.Count == 0
                        ? item.AIRCRAFT.Keys.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                        : target.Planes;

                    for (int p = 0; p < planes.Count; p++)
                    {
                        string plane = planes[p];
                        PlaneState before = item.GetStateAircraft(plane);
                        item.SetAircraftActivity(plane, enabled);
                        PlaneState after = item.GetStateAircraft(plane);
                        if (before != after)
                            changed++;
                    }
                }
            }

            return changed;
        }

        private static List<RelationItem> FindMatchingRelationItems(Relation relation, NodeSpec target)
        {
            if (relation == null || relation.NODES == null || target == null || string.IsNullOrWhiteSpace(target.Id))
                return new List<RelationItem>();

            string expectedGame = string.IsNullOrWhiteSpace(target.Game) ? "DCS" : target.Game.Trim();
            // An old id of a game split into versions (MSFS, XPlane) stands for the nodes of every version.
            IReadOnlyList<string> expectedGames = Games.LegacyGameIds.Expand(expectedGame);
            return relation.NODES
                .Where(n => n != null &&
                            string.Equals(n.ID ?? string.Empty, target.Id.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game, expectedGame, StringComparison.OrdinalIgnoreCase) ||
                             expectedGames.Contains(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game.Trim(), StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        private static int EditRelationMass(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            if (args.Count < 1)
                return Fail("relations mass requires: delete|merge|split|duplicate|add-prefix|add-postfix|remove-part|export.");

            string operation = args[0].Trim().ToLowerInvariant();
            List<string> options = args.Skip(1).ToList();
            switch (operation)
            {
                case "delete":
                    return RunRelationMassDelete(options, outPath, textOutput);
                case "duplicate":
                    return RunRelationMassDuplicate(options, outPath, textOutput);
                case "merge":
                    return RunRelationMassMerge(options, outPath, textOutput);
                case "split":
                    return RunRelationMassSplit(options, outPath, textOutput);
                case "add-prefix":
                case "prefix":
                    return RunRelationMassRename(options, "add-prefix", outPath, textOutput);
                case "add-postfix":
                case "postfix":
                    return RunRelationMassRename(options, "add-postfix", outPath, textOutput);
                case "remove-part":
                case "remove-name-part":
                    return RunRelationMassRename(options, "remove-part", outPath, textOutput);
                case "export":
                    return RunRelationMassExport(options, outPath, textOutput);
                default:
                    return Fail("Unknown relations mass action: " + operation);
            }
        }

        private static int RunRelationMassDelete(List<string> args, string outPath, bool textOutput)
        {
            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass delete argument(s): " + string.Join(" ", args));

            List<string> deleted = new List<string>();
            for (int i = 0; i < selectedNames.Count; i++)
            {
                string name = selectedNames[i];
                if (RemoveRelationByName(name))
                    deleted.Add(name);
            }

            if (deleted.Count == 0)
                return Fail("No relations were deleted.");

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationMassResult result = new RelationMassResult();
            result.Action = "delete";
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.DeletedRelations = deleted.OrderBy(v => v).ToList();
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int RunRelationMassDuplicate(List<string> args, string outPath, bool textOutput)
        {
            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass duplicate argument(s): " + string.Join(" ", args));

            List<string> created = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                Relation source = selected[i];
                if (source == null || string.IsNullOrWhiteSpace(source.NAME))
                    continue;

                Relation duplicate = source.Copy();
                duplicate.NAME = BuildDuplicateRelationName(source.NAME);
                if (duplicate.bind != null)
                    duplicate.bind.Rl = duplicate;

                InternalDataManagement.AllRelations[duplicate.NAME] = duplicate;
                if (duplicate.bind != null)
                    InternalDataManagement.AllBinds[duplicate.NAME] = duplicate.bind;
                created.Add(duplicate.NAME);
            }

            if (created.Count == 0)
                return Fail("No relations were duplicated.");

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationMassResult result = new RelationMassResult();
            result.Action = "duplicate";
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.CreatedRelations = created.OrderBy(v => v).ToList();
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int RunRelationMassMerge(List<string> args, string outPath, bool textOutput)
        {
            string mergeName = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                mergeName = args[0];
                args.RemoveAt(0);
            }
            string mergeNameOption = PopOptionValue(args, "--name");
            if (string.IsNullOrWhiteSpace(mergeName))
                mergeName = mergeNameOption;
            if (string.IsNullOrWhiteSpace(mergeName))
                return Fail("relations mass merge requires a target name via positional <name> or --name.");

            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass merge argument(s): " + string.Join(" ", args));

            bool isAxis = selected[0].ISAXIS;
            for (int i = 1; i < selected.Count; i++)
            {
                if (selected[i] != null && selected[i].ISAXIS != isAxis)
                    return Fail("relations mass merge requires selected relations to be either all axis or all button.");
            }

            List<string> deleted = new List<string>();
            for (int i = 0; i < selectedNames.Count; i++)
            {
                string name = selectedNames[i];
                if (RemoveRelationByName(name))
                    deleted.Add(name);
            }

            Relation merged = new Relation();
            merged.NAME = EnsureUniqueRelationName(mergeName.Trim());
            merged.ISAXIS = isAxis;
            for (int i = 0; i < selected.Count; i++)
            {
                Relation source = selected[i];
                if (source == null || source.NODES == null)
                    continue;

                for (int n = 0; n < source.NODES.Count; n++)
                {
                    RelationItem node = source.NODES[n];
                    if (node == null)
                        continue;
                    merged.IncludeRelationItem(node.Copy());
                }
            }
            merged.RecalculateElementCount();
            InternalDataManagement.AllRelations[merged.NAME] = merged;

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationMassResult result = new RelationMassResult();
            result.Action = "merge";
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.DeletedRelations = deleted.OrderBy(v => v).ToList();
            result.CreatedRelations = new List<string> { merged.NAME };
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int RunRelationMassSplit(List<string> args, string outPath, bool textOutput)
        {
            string postfix = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                postfix = args[0];
                args.RemoveAt(0);
            }
            string postfixOption = PopOptionValue(args, "--postfix");
            if (string.IsNullOrWhiteSpace(postfix))
                postfix = postfixOption;
            if (string.IsNullOrWhiteSpace(postfix))
                return Fail("relations mass split requires a postfix via positional <postfix> or --postfix.");

            List<string> planeOptions = PopOptionValues(args, "--plane");
            planeOptions.AddRange(PopOptionValues(args, "--planes"));
            List<string> planesToSplit = ParseMassSplitPlanes(planeOptions);
            if (planesToSplit.Count == 0)
                return Fail("relations mass split requires at least one plane via --plane or --planes.");

            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass split argument(s): " + string.Join(" ", args));

            bool isAxis = selected[0].ISAXIS;
            for (int i = 1; i < selected.Count; i++)
            {
                if (selected[i] != null && selected[i].ISAXIS != isAxis)
                    return Fail("relations mass split requires selected relations to be either all axis or all button.");
            }

            List<string> created = new List<string>();
            for (int i = 0; i < selected.Count; i++)
            {
                Relation source = selected[i];
                if (source == null)
                    continue;

                Relation split = new Relation();
                split.NAME = EnsureUniqueRelationName((source.NAME ?? "Relation") + "_" + postfix.Trim());
                split.ISAXIS = source.ISAXIS;
                split.Groups = source.Groups == null ? new List<string>() : new List<string>(source.Groups);

                List<RelationItem> nodes = source.NODES == null
                    ? new List<RelationItem>()
                    : source.NODES.Where(n => n != null).ToList();
                for (int n = 0; n < nodes.Count; n++)
                {
                    RelationItem sourceNode = nodes[n];
                    RelationItem copiedNode = sourceNode.Copy();
                    List<string> activeAircraft = copiedNode.GetActiveAircraftList();

                    for (int a = 0; a < activeAircraft.Count; a++)
                        copiedNode.SetAircraftActivity(activeAircraft[a], false);

                    for (int p = 0; p < planesToSplit.Count; p++)
                    {
                        string plane = planesToSplit[p];
                        bool hasPlane = activeAircraft.Any(v => string.Equals(v, plane, StringComparison.OrdinalIgnoreCase));
                        if (!hasPlane)
                            continue;

                        copiedNode.SetAircraftActivity(plane, true);
                        sourceNode.SetAircraftActivity(plane, false);
                    }

                    split.AddNode(copiedNode);
                }

                split.RecalculateElementCount();
                InternalDataManagement.AllRelations[split.NAME] = split;
                created.Add(split.NAME);
            }

            if (created.Count == 0)
                return Fail("No split relations were created.");

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationMassResult result = new RelationMassResult();
            result.Action = "split";
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.CreatedRelations = created.OrderBy(v => v).ToList();
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int RunRelationMassRename(List<string> args, string mode, string outPath, bool textOutput)
        {
            string value = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[0];
                args.RemoveAt(0);
            }
            string valueOption = PopOptionValue(args, "--value");
            if (string.IsNullOrWhiteSpace(value))
                value = valueOption;
            if (string.IsNullOrWhiteSpace(value))
                return Fail("relations mass " + mode + " requires a value via positional <value> or --value.");

            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass " + mode + " argument(s): " + string.Join(" ", args));

            List<RelationMassRenameEntry> renamed = new List<RelationMassRenameEntry>();
            for (int i = 0; i < selectedNames.Count; i++)
            {
                string originalName = selectedNames[i];
                if (!InternalDataManagement.AllRelations.ContainsKey(originalName))
                    continue;

                string requested;
                switch (mode)
                {
                    case "add-prefix":
                        requested = value.Trim() + originalName;
                        break;
                    case "add-postfix":
                        requested = originalName + value.Trim();
                        break;
                    case "remove-part":
                        requested = originalName.Replace(value, string.Empty);
                        if (string.IsNullOrWhiteSpace(requested))
                            requested = originalName;
                        break;
                    default:
                        return Fail("Unknown mass rename mode: " + mode);
                }

                string finalName = RenameRelationEntry(originalName, requested);
                if (string.IsNullOrWhiteSpace(finalName))
                    continue;

                RelationMassRenameEntry entry = new RelationMassRenameEntry();
                entry.OldName = originalName;
                entry.NewName = finalName;
                renamed.Add(entry);
            }

            if (renamed.Count == 0)
                return Fail("No relation names were changed.");

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            RelationMassResult result = new RelationMassResult();
            result.Action = mode;
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.RenamedRelations = renamed
                .OrderBy(v => v.OldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int RunRelationMassExport(List<string> args, string outPath, bool textOutput)
        {
            string filePath = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                filePath = args[0];
                args.RemoveAt(0);
            }
            string pathOption = PopOptionValue(args, "--path");
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = pathOption;
            if (string.IsNullOrWhiteSpace(filePath))
                return Fail("relations mass export requires a target path (.rl or .json).");

            filePath = NormalizePath(filePath);
            string formatOption = PopOptionValue(args, "--format");
            string format = ResolveMassExportFormat(filePath, formatOption, out string formatError);
            if (!string.IsNullOrWhiteSpace(formatError))
                return Fail(formatError);

            if (!TryResolveMassRelationSelection(args, out List<Relation> selected, out List<string> selectedNames, out string error))
                return Fail(error);
            if (args.Count > 0)
                return Fail("Unknown relations mass export argument(s): " + string.Join(" ", args));

            try
            {
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    EngineRelationsDocument source = EngineSerialization.ExportRelations();
                    HashSet<string> selectedSet = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
                    EngineRelationsDocument exportDoc = new EngineRelationsDocument();
                    exportDoc.FormatVersion = source.FormatVersion;
                    exportDoc.GeneratedUtc = source.GeneratedUtc;
                    exportDoc.Relations = (source.Relations ?? new List<EngineRelationDto>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name) && selectedSet.Contains(r.Name))
                        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    File.WriteAllText(filePath, EngineSerialization.ToJson(exportDoc), Encoding.UTF8);
                }
                else
                {
                    Dictionary<string, Relation> exportMap = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < selected.Count; i++)
                    {
                        Relation relation = selected[i];
                        if (relation == null || string.IsNullOrWhiteSpace(relation.NAME))
                            continue;
                        exportMap[relation.NAME] = relation.Copy();
                    }

                    using (FileStream stream = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        BinaryFormatter formatter = new BinaryFormatter();
                        formatter.Binder = new LegacyJoyProSerializationBinder();
                        formatter.Serialize(stream, exportMap);
                    }
                }
            }
            catch (Exception ex)
            {
                return Fail("relations mass export failed: " + ex.Message);
            }

            RelationMassResult result = new RelationMassResult();
            result.Action = "export";
            result.SelectedRelations = selectedNames;
            result.SelectedCount = selectedNames.Count;
            result.OutputPath = filePath;
            result.ExportFormat = format;
            return EmitRelationMassResult(result, outPath, textOutput);
        }

        private static int EmitRelationMassResult(RelationMassResult result, string outPath, bool textOutput)
        {
            string payload = textOutput
                ? ToText(result)
                : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static bool TryResolveMassRelationSelection(
            List<string> args,
            out List<Relation> selectedRelations,
            out List<string> selectedNames,
            out string error)
        {
            selectedRelations = new List<Relation>();
            selectedNames = new List<string>();
            error = string.Empty;

            List<string> relationOptions = PopOptionValues(args, "--relation");
            relationOptions.AddRange(PopOptionValues(args, "--relations"));
            string query = PopOptionValue(args, "--query");
            bool selectAll = PopFlag(args, "--all");

            List<string> explicitTokens = ParseDelimitedOptionValues(relationOptions);
            HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < explicitTokens.Count; i++)
            {
                string token = explicitTokens[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (!TryResolveRelationName(token, out string resolvedName, out List<string> candidates))
                {
                    error = BuildResolutionErrorMessage("relation", token, candidates);
                    return false;
                }

                selected.Add(resolvedName);
            }

            if (selectAll)
            {
                foreach (string relationName in InternalDataManagement.AllRelations.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(relationName))
                        selected.Add(relationName);
                }
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                EngineRelationsDocument filtered = BuildFilteredRelationsDocument(query);
                if (filtered.Relations != null)
                {
                    for (int i = 0; i < filtered.Relations.Count; i++)
                    {
                        EngineRelationDto relation = filtered.Relations[i];
                        if (relation == null || string.IsNullOrWhiteSpace(relation.Name))
                            continue;
                        selected.Add(relation.Name);
                    }
                }
            }

            if (selected.Count == 0)
            {
                error = "relations mass requires at least one selected relation via --relation/--relations, --query, or --all.";
                return false;
            }

            selectedNames = selected
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < selectedNames.Count; i++)
            {
                string name = selectedNames[i];
                if (InternalDataManagement.AllRelations.ContainsKey(name))
                    selectedRelations.Add(InternalDataManagement.AllRelations[name]);
            }

            if (selectedRelations.Count == 0)
            {
                error = "No selected relations are available in the current profile.";
                return false;
            }

            return true;
        }

        private static bool RemoveRelationByName(string relationName)
        {
            if (string.IsNullOrWhiteSpace(relationName))
                return false;

            bool removed = false;
            if (InternalDataManagement.AllRelations.ContainsKey(relationName))
            {
                InternalDataManagement.AllRelations.Remove(relationName);
                removed = true;
            }

            if (InternalDataManagement.AllBinds.ContainsKey(relationName))
            {
                InternalDataManagement.AllBinds.Remove(relationName);
                removed = true;
            }

            return removed;
        }

        private static string RenameRelationEntry(string oldName, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(oldName))
                return string.Empty;
            if (!InternalDataManagement.AllRelations.ContainsKey(oldName))
                return string.Empty;

            Relation relation = InternalDataManagement.AllRelations[oldName];
            Bind bind = InternalDataManagement.AllBinds.ContainsKey(oldName)
                ? InternalDataManagement.AllBinds[oldName]
                : null;

            InternalDataManagement.AllRelations.Remove(oldName);
            if (bind != null)
                InternalDataManagement.AllBinds.Remove(oldName);

            string requested = string.IsNullOrWhiteSpace(requestedName) ? oldName : requestedName.Trim();
            string finalName = EnsureUniqueRelationName(requested);
            relation.NAME = finalName;
            relation.bind = bind;

            if (bind != null)
            {
                bind.Rl = relation;
                InternalDataManagement.AllBinds[finalName] = bind;
            }

            InternalDataManagement.AllRelations[finalName] = relation;
            return finalName;
        }

        private static string BuildDuplicateRelationName(string sourceName)
        {
            // "<name> (copy)", then "<name> (copy 2)", ... - readable and never an existing name.
            string baseName = string.IsNullOrWhiteSpace(sourceName) ? "Relation" : sourceName.Trim();
            string candidate = baseName + " (copy)";
            for (int n = 2; InternalDataManagement.AllRelations.ContainsKey(candidate); n++)
                candidate = baseName + " (copy " + n + ")";
            return candidate;
        }

        private static List<string> ParseMassSplitPlanes(List<string> options)
        {
            List<string> parsed = ParseDelimitedOptionValues(options);
            List<string> planes = new List<string>();
            for (int i = 0; i < parsed.Count; i++)
            {
                string token = parsed[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                string value = token.Trim();
                int split = value.IndexOf(':');
                if (split >= 0 && split < value.Length - 1)
                    value = value.Substring(split + 1).Trim();
                if (value.Length == 0)
                    continue;
                if (!planes.Contains(value, StringComparer.OrdinalIgnoreCase))
                    planes.Add(value);
            }
            return planes;
        }

        private static string ResolveMassExportFormat(string filePath, string formatOption, out string error)
        {
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(formatOption))
            {
                string option = formatOption.Trim().ToLowerInvariant();
                if (option == "rl" || option == "binary")
                    return "rl";
                if (option == "json")
                    return "json";
                error = "Invalid --format value: " + formatOption + ". Use rl or json.";
                return string.Empty;
            }

            string extension = Path.GetExtension(filePath ?? string.Empty);
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                return "json";
            return "rl";
        }

        private static List<string> NormalizeModifierInputs(IEnumerable<string> rawInputs)
        {
            List<string> normalized = new List<string>();
            if (rawInputs == null)
                return normalized;

            List<string> splitInputs = new List<string>();
            foreach (string raw in rawInputs)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string[] pieces = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < pieces.Length; i++)
                {
                    string token = pieces[i].Trim();
                    if (token.Length > 0)
                        splitInputs.Add(token);
                }
            }

            for (int i = 0; i < splitInputs.Count; i++)
            {
                string token = splitInputs[i];
                Modifier parsed = Modifier.ReformerToMod(token);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.name))
                {
                    normalized.Add(parsed.toReformerString());
                    continue;
                }

                if (InternalDataManagement.AllModifiers.ContainsKey(token) && InternalDataManagement.AllModifiers[token] != null)
                {
                    normalized.Add(InternalDataManagement.AllModifiers[token].toReformerString());
                    continue;
                }

                normalized.Add(BuildFallbackReformerString(token));
            }

            return normalized
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildFallbackReformerString(string name)
        {
            string safe = string.IsNullOrWhiteSpace(name) ? "Modifier" : name.Trim();
            return safe + "\u00A7Keyboard\u00A7" + safe + "\u00A7False";
        }

        private static string ModifierNameFromReformer(string reformer)
        {
            if (string.IsNullOrWhiteSpace(reformer))
                return string.Empty;

            Modifier parsed = Modifier.ReformerToMod(reformer);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.name))
                return parsed.name.Trim();

            string trimmed = reformer.Trim();
            int splitIndex = trimmed.IndexOf('\u00A7');
            if (splitIndex > 0)
                return trimmed.Substring(0, splitIndex).Trim();
            return trimmed;
        }

        private static string ReplaceModifierNameInReformer(string reformer, string newName)
        {
            string safeName = string.IsNullOrWhiteSpace(newName) ? "Modifier" : newName.Trim();
            if (string.IsNullOrWhiteSpace(reformer))
                return BuildFallbackReformerString(safeName);

            Modifier parsed = Modifier.ReformerToMod(reformer);
            if (parsed != null)
            {
                parsed.name = safeName;
                return parsed.toReformerString();
            }

            string normalized = reformer.Trim();
            int splitIndex = normalized.IndexOf('\u00A7');
            if (splitIndex >= 0)
                return safeName + normalized.Substring(splitIndex);

            return BuildFallbackReformerString(safeName);
        }

        private static int ImportRelations(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                return Fail("File not found: " + sourcePath);

            string json = File.ReadAllText(sourcePath);
            EngineRelationsDocument doc = EngineSerialization.FromJson<EngineRelationsDocument>(json);
            EngineSerialization.ImportRelations(doc);
            Console.WriteLine("Imported relations from: " + sourcePath);
            Console.WriteLine(Summary());
            return 0;
        }

        private static int ImportBinds(string sourcePath)
        {
            if (!File.Exists(sourcePath))
                return Fail("File not found: " + sourcePath);

            string json = File.ReadAllText(sourcePath);
            EngineBindsDocument doc = EngineSerialization.FromJson<EngineBindsDocument>(json);
            EngineSerialization.ImportBinds(doc);
            Console.WriteLine("Imported binds from: " + sourcePath);
            Console.WriteLine(Summary());
            return 0;
        }

        private static int EditRelations(string editorCommand)
        {
            EngineRelationsDocument doc = EngineSerialization.ExportRelations();
            string path = CreateTempPath("relations");
            File.WriteAllText(path, EngineSerialization.ToJson(doc));
            LaunchEditor(editorCommand, path);
            return ImportRelations(path);
        }

        private static int EditBinds(string editorCommand)
        {
            EngineBindsDocument doc = EngineSerialization.ExportBinds();
            string path = CreateTempPath("binds");
            File.WriteAllText(path, EngineSerialization.ToJson(doc));
            LaunchEditor(editorCommand, path);
            return ImportBinds(path);
        }

        private static string CreateTempPath(string label)
        {
            string fileName = "joypro-engine-" + label + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".json";
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        private static void LaunchEditor(string editorCommand, string filePath)
        {
            if (string.IsNullOrWhiteSpace(editorCommand))
            {
                Process defaultEditor = Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = Quote(filePath),
                    UseShellExecute = true
                });
                if (defaultEditor != null)
                    defaultEditor.WaitForExit();
                return;
            }

            string commandText = editorCommand;
            if (commandText.Contains("{file}"))
            {
                commandText = commandText.Replace("{file}", Quote(filePath));
            }
            else
            {
                commandText = commandText + " " + Quote(filePath);
            }

            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + commandText,
                UseShellExecute = false
            });
            if (process != null)
                process.WaitForExit();
        }

        private static void EmitPayload(string payload, string outPath)
        {
            if (string.IsNullOrWhiteSpace(outPath))
            {
                Console.WriteLine(payload);
                return;
            }

            File.WriteAllText(outPath, payload);
            Console.WriteLine("Wrote file: " + outPath);
        }

        private static string JoinQuery(IEnumerable<string> queryTokens)
        {
            if (queryTokens == null)
                return null;
            string query = string.Join(" ", queryTokens.Where(t => t != null));
            return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        }

        private static EngineRelationsDocument BuildFilteredRelationsDocument(string query)
        {
            EngineRelationsDocument source = EngineSerialization.ExportRelations();
            EngineRelationsDocument result = new EngineRelationsDocument();
            result.FormatVersion = source.FormatVersion;
            result.GeneratedUtc = source.GeneratedUtc;

            IEnumerable<EngineRelationDto> relations = (source.Relations ?? new List<EngineRelationDto>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name));

            if (!string.IsNullOrWhiteSpace(query))
            {
                relations = relations.Where(r => RelationMatchesQuery(r, query));
            }

            result.Relations = relations
                .OrderBy(r => r.Name)
                .ToList();

            return result;
        }

        private static EngineBindsDocument BuildFilteredBindsDocument(string query)
        {
            EngineBindsDocument source = EngineSerialization.ExportBinds();
            EngineBindsDocument result = new EngineBindsDocument();
            result.FormatVersion = source.FormatVersion;
            result.GeneratedUtc = source.GeneratedUtc;

            IEnumerable<EngineBindDto> binds = (source.Binds ?? new List<EngineBindDto>())
                .Where(b => b != null && !string.IsNullOrWhiteSpace(b.RelationName));

            if (!string.IsNullOrWhiteSpace(query))
            {
                binds = binds.Where(b => BindMatchesQuery(b, query));
            }

            result.Binds = binds
                .OrderBy(b => b.RelationName)
                .ToList();
            return result;
        }

        private static bool RelationMatchesQuery(EngineRelationDto relation, string query)
        {
            string q = query.Trim();
            if (ContainsIgnoreCase(relation.Name, q))
                return true;

            if (relation.Groups != null && relation.Groups.Any(g => ContainsIgnoreCase(g, q)))
                return true;

            if (relation.Bind != null && BindMatchesQuery(relation.Bind, q))
                return true;

            if (relation.Nodes != null)
            {
                for (int i = 0; i < relation.Nodes.Count; i++)
                {
                    EngineRelationItemDto node = relation.Nodes[i];
                    if (node == null)
                        continue;

                    if (ContainsIgnoreCase(node.Game, q) || ContainsIgnoreCase(node.Id, q))
                        return true;

                    if (node.Aircraft != null)
                    {
                        if (node.Aircraft.Keys.Any(plane => ContainsIgnoreCase(plane, q)))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool BindMatchesQuery(EngineBindDto bind, string query)
        {
            string q = query.Trim();
            if (ContainsIgnoreCase(bind.RelationName, q))
                return true;
            if (ContainsIgnoreCase(bind.Joystick, q))
                return true;
            if (ContainsIgnoreCase(bind.JButton, q))
                return true;
            if (ContainsIgnoreCase(bind.JAxis, q))
                return true;
            if (ContainsIgnoreCase(bind.AliasJoystick, q))
                return true;
            if (ContainsIgnoreCase(bind.AdditionalImportInfo, q))
                return true;

            string combo = (bind.Joystick ?? string.Empty) + "::" + BindInput(bind);
            if (ContainsIgnoreCase(combo, q))
                return true;

            if (bind.AllReformers != null && bind.AllReformers.Any(m => ContainsIgnoreCase(m, q)))
                return true;

            return false;
        }

        private static string BindInput(EngineBindDto bind)
        {
            if (!string.IsNullOrWhiteSpace(bind.JButton))
                return bind.JButton;
            if (!string.IsNullOrWhiteSpace(bind.JAxis))
                return bind.JAxis;
            return string.Empty;
        }

        private static bool ContainsIgnoreCase(string text, string query)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
                return false;
            return text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool GameMatchesFilter(string gameName, string filter)
        {
            string game = NormalizeGameKey(gameName);
            string query = NormalizeGameKey(filter);
            if (query.Length == 0)
                return true;

            if (query == "dcs")
                return game == "dcs";
            if (query == "il2" || query == "il2game" || query == "il2sturmovik")
                return game == "il2game";
            if (query == "sc" || query == "starcitizen")
                return game == "starcitizen";

            return game.Contains(query) || query.Contains(game);
        }

        private static string NormalizeGameKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder sb = new StringBuilder();
            string raw = value.Trim().ToLowerInvariant();
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Summary()
        {
            return "relations=" + InternalDataManagement.AllRelations.Count +
                   ", binds=" + InternalDataManagement.AllBinds.Count +
                   ", modifiers=" + InternalDataManagement.AllModifiers.Count;
        }

        private static void ResetDiscoveryCaches()
        {
            lock (ConnectedJoystickCacheLock)
            {
                ConnectedJoystickCache = null;
                ConnectedJoystickCacheWarnings = null;
            }

            lock (DcsInputDeviceCacheLock)
            {
                DcsInputDeviceCache.Clear();
            }

            lock (StarCitizenP4kIndexCacheLock)
            {
                StarCitizenP4kIndexCache.Clear();
            }
        }

        private static bool IsHelp(string value)
        {
            string v = value.ToLowerInvariant();
            return v == "help" || v == "--help" || v == "-h";
        }

        private static string PopOptionValue(List<string> tokens, string option)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (i + 1 >= tokens.Count)
                    throw new ArgumentException("Missing value for option " + option);
                string value = tokens[i + 1];
                tokens.RemoveAt(i + 1);
                tokens.RemoveAt(i);
                return value;
            }
            return null;
        }

        private static List<string> PopOptionValues(List<string> tokens, string option)
        {
            List<string> values = new List<string>();
            while (true)
            {
                bool found = false;
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (!tokens[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (i + 1 >= tokens.Count)
                        throw new ArgumentException("Missing value for option " + option);
                    values.Add(tokens[i + 1]);
                    tokens.RemoveAt(i + 1);
                    tokens.RemoveAt(i);
                    found = true;
                    break;
                }

                if (!found)
                    break;
            }
            return values;
        }

        private static bool PopFlag(List<string> tokens, string option)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                    continue;
                tokens.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static bool PopOptionBoolean(List<string> tokens, string option, bool defaultValue, out bool provided)
        {
            provided = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool parsed = true;
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    string raw = tokens[i + 1];
                    if (!TryParseOptionBoolean(raw, out parsed))
                        throw new ArgumentException("Invalid value for option " + option + ": '" + raw + "'. Use true/false.");
                    tokens.RemoveAt(i + 1);
                }

                tokens.RemoveAt(i);
                provided = true;
                return parsed;
            }

            return defaultValue;
        }

        private static bool TryParseOptionBoolean(string raw, out bool value)
        {
            string normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "true":
                case "t":
                case "yes":
                case "y":
                case "1":
                case "on":
                    value = true;
                    return true;
                case "false":
                case "f":
                case "no":
                case "n":
                case "0":
                case "off":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static bool TryParseLayoutTemplatePayload(
            string markersRawJson,
            string tablesRawJson,
            string connectorsRawJson,
            out List<LayoutTemplateMarkerDto> markers,
            out List<LayoutTemplateTableDto> tables,
            out List<LayoutTemplateConnectorDto> connectors,
            out string error)
        {
            markers = new List<LayoutTemplateMarkerDto>();
            tables = new List<LayoutTemplateTableDto>();
            connectors = new List<LayoutTemplateConnectorDto>();
            error = string.Empty;

            if (!TryParseLayoutTemplateMarkers(
                markersRawJson,
                out markers,
                out List<LayoutTemplateTableDto> embeddedTables,
                out List<LayoutTemplateConnectorDto> embeddedConnectors,
                out error))
                return false;

            if (string.IsNullOrWhiteSpace(tablesRawJson))
            {
                tables = embeddedTables;
            }
            else if (!TryParseLayoutTemplateTables(tablesRawJson, out tables, out string tableError))
            {
                error = tableError;
                return false;
            }

            if (string.IsNullOrWhiteSpace(connectorsRawJson))
            {
                connectors = embeddedConnectors;
                return true;
            }

            if (!TryParseLayoutTemplateConnectors(connectorsRawJson, out connectors, out string connectorError))
            {
                error = connectorError;
                return false;
            }

            return true;
        }

        private static bool TryParseLayoutTemplateMarkers(
            string rawJson,
            out List<LayoutTemplateMarkerDto> markers,
            out List<LayoutTemplateTableDto> tables,
            out List<LayoutTemplateConnectorDto> connectors,
            out string error)
        {
            markers = new List<LayoutTemplateMarkerDto>();
            tables = new List<LayoutTemplateTableDto>();
            connectors = new List<LayoutTemplateConnectorDto>();
            error = string.Empty;

            string payload = (rawJson ?? string.Empty).Trim();
            if (payload.Length == 0)
                return true;

            try
            {
                List<LayoutTemplateMarkerDto> parsedList = EngineSerialization.FromJson<List<LayoutTemplateMarkerDto>>(payload);
                if (parsedList != null)
                {
                    markers = parsedList
                        .Where(m => m != null)
                        .Select(NormalizeTemplateMarker)
                        .ToList();
                    return true;
                }
            }
            catch
            {
                // Try wrapped payload below.
            }

            try
            {
                LayoutTemplateMarkerPayload parsedPayload = EngineSerialization.FromJson<LayoutTemplateMarkerPayload>(payload);
                if (parsedPayload != null)
                {
                    if (parsedPayload.Markers != null)
                    {
                        markers = parsedPayload.Markers
                            .Where(m => m != null)
                            .Select(NormalizeTemplateMarker)
                            .ToList();
                    }

                    if (parsedPayload.Tables != null)
                    {
                        tables = parsedPayload.Tables
                            .Where(t => t != null)
                            .Select(NormalizeTemplateTable)
                            .ToList();
                    }

                    if (parsedPayload.Connectors != null)
                    {
                        connectors = parsedPayload.Connectors
                            .Where(c => c != null)
                            .Select(NormalizeTemplateConnector)
                            .Where(c => c.Points.Count >= 2)
                            .ToList();
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            error = "Expected a JSON array of markers or object with 'Markers' array.";
            return false;
        }

        private static bool TryParseLayoutTemplateTables(
            string rawJson,
            out List<LayoutTemplateTableDto> tables,
            out string error)
        {
            tables = new List<LayoutTemplateTableDto>();
            error = string.Empty;

            string payload = (rawJson ?? string.Empty).Trim();
            if (payload.Length == 0)
                return true;

            try
            {
                List<LayoutTemplateTableDto> parsedList = EngineSerialization.FromJson<List<LayoutTemplateTableDto>>(payload);
                if (parsedList != null)
                {
                    tables = parsedList
                        .Where(t => t != null)
                        .Select(NormalizeTemplateTable)
                        .ToList();
                    return true;
                }
            }
            catch
            {
                // Try wrapped payload below.
            }

            try
            {
                LayoutTemplateMarkerPayload parsedPayload = EngineSerialization.FromJson<LayoutTemplateMarkerPayload>(payload);
                if (parsedPayload != null && parsedPayload.Tables != null)
                {
                    tables = parsedPayload.Tables
                        .Where(t => t != null)
                        .Select(NormalizeTemplateTable)
                        .ToList();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            error = "Expected a JSON array of tables or object with 'Tables' array.";
            return false;
        }

        private static bool TryParseLayoutTemplateConnectors(
            string rawJson,
            out List<LayoutTemplateConnectorDto> connectors,
            out string error)
        {
            connectors = new List<LayoutTemplateConnectorDto>();
            error = string.Empty;

            string payload = (rawJson ?? string.Empty).Trim();
            if (payload.Length == 0)
                return true;

            try
            {
                List<LayoutTemplateConnectorDto> parsedList = EngineSerialization.FromJson<List<LayoutTemplateConnectorDto>>(payload);
                if (parsedList != null)
                {
                    connectors = parsedList
                        .Where(c => c != null)
                        .Select(NormalizeTemplateConnector)
                        .Where(c => c.Points.Count >= 2)
                        .ToList();
                    return true;
                }
            }
            catch
            {
                // Try wrapped payload below.
            }

            try
            {
                LayoutTemplateMarkerPayload parsedPayload = EngineSerialization.FromJson<LayoutTemplateMarkerPayload>(payload);
                if (parsedPayload != null && parsedPayload.Connectors != null)
                {
                    connectors = parsedPayload.Connectors
                        .Where(c => c != null)
                        .Select(NormalizeTemplateConnector)
                        .Where(c => c.Points.Count >= 2)
                        .ToList();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            error = "Expected a JSON array of connectors or object with 'Connectors' array.";
            return false;
        }

        private static LayoutTemplateMarkerDto NormalizeTemplateMarker(LayoutTemplateMarkerDto marker)
        {
            LayoutTemplateMarkerDto normalized = marker ?? new LayoutTemplateMarkerDto();
            if (normalized.Token == null)
                normalized.Token = string.Empty;
            if (normalized.Type == null)
                normalized.Type = string.Empty;
            if (normalized.Modifier == null)
                normalized.Modifier = string.Empty;
            return normalized;
        }

        private static LayoutTemplateTableDto NormalizeTemplateTable(LayoutTemplateTableDto table)
        {
            LayoutTemplateTableDto normalized = table ?? new LayoutTemplateTableDto();
            if (normalized.Name == null)
                normalized.Name = string.Empty;
            normalized.Name = normalized.Name.Trim();
            if (normalized.Name.Length == 0)
                normalized.Name = "Table";
            normalized.Mode = NormalizeTemplateTableMode(normalized.Mode);
            normalized.X = Math.Max(0, normalized.X);
            normalized.Y = Math.Max(0, normalized.Y);
            normalized.Width = Math.Max(120, normalized.Width);
            normalized.Height = Math.Max(70, normalized.Height);
            normalized.Columns = Math.Max(1, normalized.Columns);
            if (normalized.BackgroundColor == null)
                normalized.BackgroundColor = "#F6F8FB";
            normalized.BackgroundColor = NormalizeTemplateColor(normalized.BackgroundColor, "#F6F8FB");
            if (float.IsNaN(normalized.BackgroundOpacity) || float.IsInfinity(normalized.BackgroundOpacity))
                normalized.BackgroundOpacity = 0.93f;
            if (normalized.BackgroundOpacity < 0f)
                normalized.BackgroundOpacity = 0f;
            else if (normalized.BackgroundOpacity > 1f)
                normalized.BackgroundOpacity = 1f;
            return normalized;
        }

        private static LayoutTemplateConnectorDto NormalizeTemplateConnector(LayoutTemplateConnectorDto connector)
        {
            LayoutTemplateConnectorDto normalized = connector ?? new LayoutTemplateConnectorDto();
            if (normalized.Name == null)
                normalized.Name = string.Empty;
            normalized.Name = normalized.Name.Trim();
            if (normalized.Name.Length == 0)
                normalized.Name = "Connector";
            if (normalized.Color == null)
                normalized.Color = "#E2E8F0";
            normalized.Color = normalized.Color.Trim();
            if (normalized.Color.Length == 0)
                normalized.Color = "#E2E8F0";
            if (normalized.Width < 1f)
                normalized.Width = 1f;
            else if (normalized.Width > 12f)
                normalized.Width = 12f;
            if (normalized.Route == null)
                normalized.Route = "orthogonal";
            normalized.Route = NormalizeTemplateConnectorRoute(normalized.Route);
            if (normalized.Steps < 0)
                normalized.Steps = 0;
            else if (normalized.Steps > 8)
                normalized.Steps = 8;
            if (normalized.TargetToken == null)
                normalized.TargetToken = string.Empty;
            normalized.TargetToken = normalized.TargetToken.Trim();
            normalized.Points = NormalizeTemplateConnectorPoints(normalized.Points);
            return normalized;
        }

        private static string NormalizeTemplateColor(string value, string fallback)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                raw = fallback;
            if (!raw.StartsWith("#", StringComparison.Ordinal))
                raw = "#" + raw;
            if (raw.Length != 7 && raw.Length != 9)
                return fallback;
            return raw.ToUpperInvariant();
        }

        private static string NormalizeTemplateConnectorRoute(string route)
        {
            string normalized = (route ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "diagonal")
                return "diagonal";
            if (normalized == "manual")
                return "manual";
            return "orthogonal";
        }

        private static List<LayoutTemplateConnectorPointDto> NormalizeTemplateConnectorPoints(IEnumerable<LayoutTemplateConnectorPointDto> points)
        {
            List<LayoutTemplateConnectorPointDto> normalized = new List<LayoutTemplateConnectorPointDto>();
            foreach (LayoutTemplateConnectorPointDto point in points ?? Enumerable.Empty<LayoutTemplateConnectorPointDto>())
            {
                if (point == null)
                    continue;
                normalized.Add(new LayoutTemplateConnectorPointDto
                {
                    X = Math.Max(0, point.X),
                    Y = Math.Max(0, point.Y)
                });
            }

            return normalized;
        }

        private static string NormalizeTemplateTableMode(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "list" ? "list" : "panel";
        }

        private sealed class LayoutTemplateMarkerPayload
        {
            public List<LayoutTemplateMarkerDto> Markers { get; set; }
            public List<LayoutTemplateTableDto> Tables { get; set; }
            public List<LayoutTemplateConnectorDto> Connectors { get; set; }

            public LayoutTemplateMarkerPayload()
            {
                Markers = new List<LayoutTemplateMarkerDto>();
                Tables = new List<LayoutTemplateTableDto>();
                Connectors = new List<LayoutTemplateConnectorDto>();
            }
        }

        private static int PopOptionInt(List<string> tokens, string option, int defaultValue)
        {
            string raw = PopOptionValue(tokens, option);
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw new ArgumentException("Invalid value for option " + option + ": '" + raw + "'. Use integer values.");
            return parsed;
        }

        private static float PopOptionFloat(List<string> tokens, string option, float defaultValue)
        {
            string raw = PopOptionValue(tokens, option);
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                throw new ArgumentException("Invalid value for option " + option + ": '" + raw + "'. Use numeric values.");
            return parsed;
        }

        private static bool TryParseRelationType(string value, out bool isAxis)
        {
            isAxis = false;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim().ToLowerInvariant();
            if (normalized == "axis" || normalized == "a")
            {
                isAxis = true;
                return true;
            }
            if (normalized == "button" || normalized == "btn" || normalized == "b")
            {
                isAxis = false;
                return true;
            }
            return false;
        }

        private static bool TryInferRelationTypeFromNodes(List<NodeSpec> nodes, out bool isAxis, out string error)
        {
            isAxis = false;
            error = string.Empty;
            if (nodes == null || nodes.Count == 0)
                return false;

            bool resolved = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                NodeSpec spec = nodes[i];
                if (spec == null || string.IsNullOrWhiteSpace(spec.Id))
                    continue;

                bool? nodeAxis = TryInferNodeAxis(spec.Game, spec.Id.Trim());
                if (!nodeAxis.HasValue)
                    continue;

                if (!resolved)
                {
                    isAxis = nodeAxis.Value;
                    resolved = true;
                    continue;
                }

                if (isAxis != nodeAxis.Value)
                {
                    error = "relations add cannot infer a single type: provided nodes include both axis and button items.";
                    return false;
                }
            }

            return resolved;
        }

        internal static bool? TryInferNodeAxis(string gameRaw, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string gameKey = NormalizeGameKey(gameRaw);
            if (string.IsNullOrWhiteSpace(gameKey) || gameKey == "dcs")
            {
                bool? dcs = TryInferDcsAxis(id);
                if (dcs.HasValue)
                    return dcs;
                return null;
            }

            if (gameKey == "il2" || gameKey == "il2game" || gameKey == "il2sturmovik")
                return TryInferIl2Axis(id);
            if (gameKey == "sc" || gameKey == "starcitizen")
                return TryInferStarCitizenAxis(id);

            // Registry games: the catalog says what the action is (DCS-style prefixes mean nothing there).
            Games.IGameIntegration registryGame;
            if (Games.GameRegistry.TryResolve(gameRaw, out registryGame))
            {
                Games.GameAction action = Games.GameCatalogStore.Load(registryGame, null).Find(id);
                return action == null ? (bool?)null : action.IsAxis;
            }

            return TryInferDcsAxis(id);
        }

        private static bool? TryInferDcsAxis(string id)
        {
            string normalized = (id ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return null;

            char lead = char.ToLowerInvariant(normalized[0]);
            if (lead == 'a')
                return true;
            if (lead == 'd')
                return false;
            return null;
        }

        private static bool? TryInferIl2Axis(string id)
        {
            string normalized = (id ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return null;
            if (normalized.EndsWith("+", StringComparison.Ordinal) ||
                normalized.EndsWith("-", StringComparison.Ordinal))
                return false;

            string actionsPath = GameDataPaths.DbFile("IL2", "IL2.actions");
            if (!File.Exists(actionsPath))
                return null;

            string[] lines;
            try
            {
                lines = File.ReadAllLines(actionsPath);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? string.Empty;
                int comma = raw.IndexOf(',');
                if (comma < 0)
                    continue;

                string lineId = raw.Substring(0, comma).Trim();
                if (!lineId.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    continue;

                int comment = raw.IndexOf("//", comma, StringComparison.OrdinalIgnoreCase);
                if (comment < 0)
                    continue;
                string marker = raw.Substring(comment).Trim();
                if (marker.StartsWith("//a", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (marker.StartsWith("//b", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return null;
        }

        private static bool? TryInferStarCitizenAxis(string id)
        {
            string normalized = (id ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return null;

            string scRoot = GameDataPaths.DbFolder("StarCitizen");
            string filePath = Path.Combine(scRoot, "AllBinds.xml");
            if (!File.Exists(filePath))
                filePath = Path.Combine(scRoot, "actionmaps.xml");
            if (!File.Exists(filePath))
                return null;

            string lastCategory = string.Empty;
            const string categoryToken = "<actionmap name=\"";
            const string idToken = "<action name=\"";
            const string commentStart = "<!--";
            const string commentEnd = "-->";

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch
            {
                return null;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? string.Empty;

                int categoryIndex = raw.IndexOf(categoryToken, StringComparison.OrdinalIgnoreCase);
                if (categoryIndex >= 0)
                {
                    string tail = raw.Substring(categoryIndex + categoryToken.Length);
                    int end = tail.IndexOf('\"');
                    if (end > 0)
                        lastCategory = tail.Substring(0, end).Trim();
                }

                int idIndex = raw.IndexOf(idToken, StringComparison.OrdinalIgnoreCase);
                if (idIndex < 0)
                    continue;

                string actionId = string.Empty;
                {
                    string tail = raw.Substring(idIndex + idToken.Length);
                    int end = tail.IndexOf('\"');
                    if (end > 0)
                        actionId = tail.Substring(0, end).Trim();
                }
                if (string.IsNullOrWhiteSpace(actionId))
                    continue;

                string dictKey = string.IsNullOrWhiteSpace(lastCategory)
                    ? actionId
                    : lastCategory + "___" + actionId;
                if (!normalized.Equals(dictKey, StringComparison.OrdinalIgnoreCase) &&
                    !normalized.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool axis = false;
                int cStart = raw.IndexOf(commentStart, StringComparison.OrdinalIgnoreCase);
                int cEnd = raw.IndexOf(commentEnd, StringComparison.OrdinalIgnoreCase);
                if (cStart >= 0 && cEnd > cStart)
                {
                    string comment = raw.Substring(cStart + commentStart.Length, cEnd - (cStart + commentStart.Length));
                    string[] parts = comment.Split(new[] { "//" }, StringSplitOptions.None);
                    if (parts.Length > 1)
                        axis = string.Equals(parts[1].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                }

                return axis;
            }

            return null;
        }

        private static List<string> ParseGroupOptions(List<string> options)
        {
            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < options.Count; i++)
            {
                string option = options[i];
                if (string.IsNullOrWhiteSpace(option))
                    continue;
                string[] parts = option.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int j = 0; j < parts.Length; j++)
                {
                    string group = parts[j].Trim();
                    if (group.Length > 0)
                        groups.Add(group);
                }
            }
            return groups.OrderBy(g => g).ToList();
        }

        private static List<string> ParsePlaneList(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();

            string[] parts = value.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return parts
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ParseDelimitedOptionValues(List<string> rawValues)
        {
            List<string> output = new List<string>();
            if (rawValues == null)
                return output;

            for (int i = 0; i < rawValues.Count; i++)
            {
                string raw = rawValues[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string[] parts = raw.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < parts.Length; p++)
                {
                    string value = parts[p].Trim();
                    if (value.Length == 0)
                        continue;
                    if (!output.Contains(value, StringComparer.OrdinalIgnoreCase))
                        output.Add(value);
                }
            }

            return output;
        }

        private static bool TryParseInvariantDouble(string raw, out double value)
        {
            return double.TryParse(
                (raw ?? string.Empty).Trim(),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool TryParseDoubleList(IEnumerable<string> rawValues, out List<double> values, out string error)
        {
            values = new List<double>();
            error = string.Empty;
            if (rawValues == null)
            {
                error = "No values provided.";
                return false;
            }

            foreach (string raw in rawValues)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string[] tokens = raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i].Trim();
                    if (token.Length == 0)
                        continue;

                    if (!TryParseInvariantDouble(token, out double parsed))
                    {
                        error = "Invalid numeric value '" + token + "' in list option.";
                        return false;
                    }

                    values.Add(parsed);
                }
            }

            if (values.Count == 0)
            {
                error = "At least one numeric value is required.";
                return false;
            }

            return true;
        }

        private static List<string> NormalizeDcsPlaneFilterTokens(List<string> tokens)
        {
            List<string> output = new List<string>();
            if (tokens == null)
                return output;

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                string value = token.Trim();
                int split = value.IndexOf(':');
                if (split > 0 && split < value.Length - 1)
                {
                    string prefix = value.Substring(0, split).Trim();
                    string plane = value.Substring(split + 1).Trim();
                    if (prefix.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                        value = plane;
                }

                if (value.Length == 0)
                    continue;
                if (!output.Contains(value, StringComparer.OrdinalIgnoreCase))
                    output.Add(value);
            }

            return output;
        }

        private static bool TryParseNodeSpec(string raw, out NodeSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            string[] parts = raw.Split(':');
            if (parts.Length < 2)
                return false;

            string game = parts[0].Trim();
            string id = parts[1].Trim();
            string planesRaw = parts.Length > 2
                ? string.Join(":", parts.Skip(2))
                : string.Empty;

            if (string.IsNullOrWhiteSpace(id))
                return false;

            spec = new NodeSpec();
            spec.Game = string.IsNullOrWhiteSpace(game) ? "DCS" : game;
            spec.Id = id;
            spec.Planes = ParsePlaneList(planesRaw);
            // Games without aircraft keep node state under the game name (see MigratePlanelessNodeAircraft).
            string specGame = spec.Game;
            if (!string.Equals(specGame, "DCS", StringComparison.OrdinalIgnoreCase))
            {
                spec.Planes = spec.Planes
                    .Select(p => string.Equals(p, "Default", StringComparison.OrdinalIgnoreCase) ? specGame : p)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return true;
        }

        // MSFS 2024: an action the catalog has only for some aircraft (an add-on aircraft's own, actions.tsv aircraft
        // column) comes in ticked for them alone unless the node names aircraft: the one case JoyPro limits a relation
        // on its own, since the action exists only there.
        private static void ApplyCatalogAircraft(List<NodeSpec> specs)
        {
            Dictionary<string, Games.GameCatalog> catalogs = new Dictionary<string, Games.GameCatalog>(StringComparer.OrdinalIgnoreCase);
            foreach (NodeSpec spec in specs)
            {
                Games.IGameIntegration game;
                if (spec == null || !Games.GameRegistry.TryResolve(spec.Game, out game))
                    continue;
                Games.IGameAircraftScope scope = game as Games.IGameAircraftScope;
                if (scope == null || !scope.WritesLimitedPerAircraft)
                    continue;
                List<string> planes = spec.Planes ?? new List<string>();
                if (planes.Any(p => !string.Equals(p, game.GameId, StringComparison.OrdinalIgnoreCase) && !string.Equals(p, spec.Game, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Games.GameCatalog catalog;
                if (!catalogs.TryGetValue(game.GameId, out catalog))
                    catalogs[game.GameId] = catalog = Games.GameCatalogStore.Load(game, null);
                Games.GameAction action = catalog.Find(spec.Id);
                if (action != null && action.Aircraft != null && action.Aircraft.Count > 0)
                    spec.Planes = new List<string>(action.Aircraft);
            }
        }

        private static string EnsureUniqueRelationName(string requested)
        {
            string baseName = string.IsNullOrWhiteSpace(requested) ? "Relation" : requested.Trim();
            string candidate = baseName;
            int counter = 1;

            while (InternalDataManagement.AllRelations.Keys.Any(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseName + "__" + counter;
                counter++;
            }

            return candidate;
        }

        private static void AddUniqueGroup(List<string> groups, string group)
        {
            if (groups == null || string.IsNullOrWhiteSpace(group))
                return;

            if (!groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add(group);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            if (Path.IsPathRooted(path))
                return path;
            return Path.GetFullPath(Path.Combine(InvocationDirectory, path));
        }

        private static bool TryParseProfileMergeMode(string raw, out ProfileMergeMode mode, out string error)
        {
            mode = ProfileMergeMode.Right;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            string normalized = raw.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "left":
                case "l":
                case "left-overwrite":
                case "keep-left":
                    mode = ProfileMergeMode.Left;
                    return true;
                case "right":
                case "r":
                case "right-overwrite":
                case "overwrite":
                case "keep-right":
                    mode = ProfileMergeMode.Right;
                    return true;
                case "ask":
                case "a":
                case "prompt":
                case "interactive":
                    mode = ProfileMergeMode.Ask;
                    return true;
                default:
                    error = "Invalid value for profile merge mode '" + raw + "'. Use left|right|ask.";
                    return false;
            }
        }

        private static Pr0file LoadProfileDocument(string filePath)
        {
            string normalizedPath = NormalizePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
                throw new ArgumentException("profile path is empty.");
            if (!File.Exists(normalizedPath))
                throw new FileNotFoundException("profile does not exist.", normalizedPath);

            return LooksLikeJsonProfilePayload(normalizedPath)
                ? LoadJsonProfileDocument(normalizedPath)
                : LoadBinaryProfileDocument(normalizedPath);
        }

        private static bool LooksLikeJsonProfilePayload(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte current = bytes[i];
                if (current == 9 || current == 10 || current == 13 || current == 32)
                    continue;
                return current == '{' || current == '[';
            }
            return false;
        }

        private static Pr0file LoadJsonProfileDocument(string filePath)
        {
            string json = File.ReadAllText(filePath);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer.Deserialize<Pr0file>(json);
        }

        private static Pr0file LoadBinaryProfileDocument(string filePath)
        {
            using (FileStream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BinaryFormatter formatter = new BinaryFormatter();
                formatter.Binder = new LegacyJoyProSerializationBinder();
                object payload = formatter.Deserialize(stream);
                Pr0file profile = payload as Pr0file;
                if (profile == null)
                    throw new InvalidDataException("Profile payload is not JoyPro.Pr0file.");
                return profile;
            }
        }

        private static ProfileMergeData NormalizeProfileForMerge(Pr0file profile)
        {
            ProfileMergeData data = new ProfileMergeData();

            Dictionary<string, Relation> rawRelations =
                profile != null && profile.Relations != null
                    ? profile.Relations
                    : new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Relation> entry in rawRelations)
            {
                Relation relation = entry.Value ?? new Relation();
                string key = !string.IsNullOrWhiteSpace(relation.NAME)
                    ? relation.NAME
                    : (entry.Key ?? string.Empty);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                key = EnsureUniqueProfileRelationKey(data.Relations, key.Trim());
                data.Relations[key] = NormalizeRelationForMergeCopy(relation, key);
            }

            Dictionary<string, Bind> rawBinds =
                profile != null && profile.Binds != null
                    ? profile.Binds
                    : new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> entry in rawBinds)
            {
                Bind bind = entry.Value ?? new Bind();
                string key = string.IsNullOrWhiteSpace(entry.Key) ? string.Empty : entry.Key.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = bind.Rl != null && !string.IsNullOrWhiteSpace(bind.Rl.NAME)
                        ? bind.Rl.NAME.Trim()
                        : string.Empty;
                }
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!data.Relations.ContainsKey(key))
                {
                    Relation placeholder = new Relation();
                    placeholder.NAME = key;
                    placeholder.ISAXIS = !string.IsNullOrWhiteSpace(bind.JAxis);
                    data.Relations[key] = NormalizeRelationForMergeCopy(placeholder, key);
                }

                Bind copy = NormalizeBindForMergeCopy(bind, data.Relations[key]);
                data.Binds[key] = copy;
                data.Relations[key].bind = copy;
            }

            data.JoystickAliases = CopyStringDictionary(profile != null ? profile.JoystickAliases : null);
            data.JoystickFileImages = CopyStringDictionary(profile != null ? profile.JoystickFileImages : null);
            data.JoystickLayoutExport = profile == null ? string.Empty : (profile.JoystickLayoutExport ?? string.Empty);
            data.PlaneAliases = CopyNestedStringDictionary(profile != null ? profile.PlaneAliases : null);
            data.JoysticksPGuids = CopyStringDictionary(profile != null ? profile.JoysticksPGuids : null);
            data.ModifierNameChanges = ToModifierRenameMap(profile == null ? null : profile.modifierNameChanges);
            data.JoystickFfb = CopyFfbDictionary(profile != null ? profile.JoystickFFB : null);
            data.FfbOverrides = CopyStringDictionary(profile != null ? profile.FfbOverrides : null);
            data.LastSelectedDcsInstance = profile == null ? string.Empty : (profile.LastSelectedDCSInstance ?? string.Empty);

            return data;
        }

        private static string EnsureUniqueProfileRelationKey(Dictionary<string, Relation> map, string baseKey)
        {
            string key = string.IsNullOrWhiteSpace(baseKey) ? "Relation" : baseKey;
            int count = 1;
            while (map.ContainsKey(key))
            {
                key = baseKey + "__" + count;
                count++;
            }
            return key;
        }

        private static Relation NormalizeRelationForMergeCopy(Relation source, string key)
        {
            Relation relation = source == null ? new Relation() : source.Copy();
            relation.NAME = key;
            relation.bind = null;
            relation.Groups = relation.Groups == null
                ? new List<string>()
                : relation.Groups
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (relation.NODES == null)
                relation.NODES = new List<RelationItem>();

            for (int i = relation.NODES.Count - 1; i >= 0; i--)
            {
                RelationItem node = relation.NODES[i];
                if (node == null || string.IsNullOrWhiteSpace(node.ID))
                {
                    relation.NODES.RemoveAt(i);
                    continue;
                }

                node.ID = node.ID.Trim();
                node.Game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
                node.AIRCRAFT = NormalizeNodeAircraft(node.AIRCRAFT);
                node.AllInputs = node.AllInputs == null
                    ? new DCSInput[0]
                    : node.AllInputs.Where(v => v != null).Select(v => v.Copy()).ToArray();
                node.OtherInputs = node.OtherInputs == null
                    ? new OtherGameInput[0]
                    : node.OtherInputs.Where(v => v != null).Select(v => v.Copy()).ToArray();
            }

            relation.RecalculateElementCount();
            return relation;
        }

        private static Dictionary<string, bool> NormalizeNodeAircraft(Dictionary<string, bool> source)
        {
            Dictionary<string, bool> aircraft = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, bool> entry in source ?? new Dictionary<string, bool>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;
                aircraft[entry.Key.Trim()] = entry.Value;
            }
            return aircraft;
        }

        private static Bind NormalizeBindForMergeCopy(Bind source, Relation relation)
        {
            Bind bind = source == null ? new Bind(relation) : source.Copy(relation);
            bind.Joystick = (bind.Joystick ?? string.Empty).Trim();
            bind.JAxis = bind.JAxis ?? string.Empty;
            bind.JButton = bind.JButton ?? string.Empty;
            bind.Reformer_depr = bind.Reformer_depr ?? string.Empty;
            bind.Curvature = bind.Curvature == null || bind.Curvature.Count == 0
                ? new List<double> { 0.0 }
                : new List<double>(bind.Curvature);
            bind.AllReformers = bind.AllReformers == null
                ? new List<string>()
                : bind.AllReformers.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            bind.AdditionalImportInfo = bind.AdditionalImportInfo ?? string.Empty;
            bind.AliasJoystick = bind.AliasJoystick ?? string.Empty;
            bind.Rl = relation;

            string sourceBackup = source == null ? null : source.JoyBackup;
            bind.JoyBackup = string.IsNullOrWhiteSpace(sourceBackup)
                ? (string.IsNullOrWhiteSpace(bind.JoyBackup) ? bind.Joystick : bind.JoyBackup)
                : sourceBackup;
            relation.bind = bind;
            return bind;
        }

        private static void MergeProfileData(
            ProfileMergeData target,
            ProfileMergeData incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string sourceLabel)
        {
            if (target == null || incoming == null)
                return;

            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(target.Relations.Keys);
            keys.UnionWith(target.Binds.Keys);
            keys.UnionWith(incoming.Relations.Keys);
            keys.UnionWith(incoming.Binds.Keys);

            foreach (string key in keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            {
                target.Relations.TryGetValue(key, out Relation leftRelation);
                target.Binds.TryGetValue(key, out Bind leftBind);
                incoming.Relations.TryGetValue(key, out Relation rightRelation);
                incoming.Binds.TryGetValue(key, out Bind rightBind);

                bool hasLeft = leftRelation != null || leftBind != null;
                bool hasRight = rightRelation != null || rightBind != null;
                if (!hasLeft || !hasRight)
                {
                    if (!hasLeft && hasRight)
                        AssignMergedEntity(target, key, rightRelation, rightBind);
                    continue;
                }

                if (AreRelationBindEntitiesEqual(leftRelation, leftBind, rightRelation, rightBind))
                    continue;

                bool takeRight = ResolveProfileMergeConflict(
                    mode,
                    result,
                    "relation '" + key + "'",
                    DescribeRelationBindEntity(leftRelation, leftBind),
                    DescribeRelationBindEntity(rightRelation, rightBind),
                    sourceLabel);
                if (takeRight)
                    AssignMergedEntity(target, key, rightRelation, rightBind);
            }

            MergeStringDictionary(
                target.JoystickAliases,
                incoming.JoystickAliases,
                mode,
                result,
                "joystick alias",
                sourceLabel);
            MergeStringDictionary(
                target.JoystickFileImages,
                incoming.JoystickFileImages,
                mode,
                result,
                "joystick image",
                sourceLabel);
            MergeStringDictionary(
                target.JoysticksPGuids,
                incoming.JoysticksPGuids,
                mode,
                result,
                "joystick pguid",
                sourceLabel);
            MergeNestedStringDictionary(
                target.PlaneAliases,
                incoming.PlaneAliases,
                mode,
                result,
                "plane alias",
                sourceLabel);
            MergeModifierRenameMaps(
                target.ModifierNameChanges,
                incoming.ModifierNameChanges,
                mode,
                result,
                sourceLabel);
            MergeFfbDictionary(
                target.JoystickFfb,
                incoming.JoystickFfb,
                mode,
                result,
                sourceLabel);
            MergeStringDictionary(
                target.FfbOverrides,
                incoming.FfbOverrides,
                mode,
                result,
                "force feedback setting",
                sourceLabel);
            string layoutExport = target.JoystickLayoutExport;
            MergeScalarString(
                ref layoutExport,
                incoming.JoystickLayoutExport,
                mode,
                result,
                "layout export",
                sourceLabel);
            target.JoystickLayoutExport = layoutExport;
            string lastSelectedDcs = target.LastSelectedDcsInstance;
            MergeScalarString(
                ref lastSelectedDcs,
                incoming.LastSelectedDcsInstance,
                mode,
                result,
                "last selected dcs instance",
                sourceLabel);
            target.LastSelectedDcsInstance = lastSelectedDcs;
        }

        private static void AssignMergedEntity(ProfileMergeData target, string key, Relation relation, Bind bind)
        {
            Relation nextRelation = relation == null
                ? null
                : NormalizeRelationForMergeCopy(relation, key);
            if (nextRelation == null && bind != null)
            {
                nextRelation = new Relation();
                nextRelation.NAME = key;
                nextRelation.ISAXIS = !string.IsNullOrWhiteSpace(bind.JAxis);
                nextRelation.NODES = new List<RelationItem>();
                nextRelation.Groups = new List<string>();
            }

            if (nextRelation == null)
            {
                target.Relations.Remove(key);
                target.Binds.Remove(key);
                return;
            }

            target.Relations[key] = nextRelation;
            if (bind == null)
            {
                target.Binds.Remove(key);
                nextRelation.bind = null;
                return;
            }

            Bind nextBind = NormalizeBindForMergeCopy(bind, nextRelation);
            target.Binds[key] = nextBind;
            nextRelation.bind = nextBind;
        }

        private static void MergeStringDictionary(
            Dictionary<string, string> target,
            Dictionary<string, string> incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string kind,
            string sourceLabel)
        {
            foreach (KeyValuePair<string, string> entry in incoming ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                string key = entry.Key.Trim();
                string right = entry.Value ?? string.Empty;
                if (!target.ContainsKey(key))
                {
                    target[key] = right;
                    continue;
                }

                string left = target[key] ?? string.Empty;
                if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool takeRight = ResolveProfileMergeConflict(
                    mode,
                    result,
                    kind + " '" + key + "'",
                    ClipPreview(left),
                    ClipPreview(right),
                    sourceLabel);
                if (takeRight)
                    target[key] = right;
            }
        }

        private static void MergeNestedStringDictionary(
            Dictionary<string, Dictionary<string, string>> target,
            Dictionary<string, Dictionary<string, string>> incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string kind,
            string sourceLabel)
        {
            foreach (KeyValuePair<string, Dictionary<string, string>> outer in
                incoming ?? new Dictionary<string, Dictionary<string, string>>())
            {
                if (string.IsNullOrWhiteSpace(outer.Key))
                    continue;

                string outerKey = outer.Key.Trim();
                if (!target.ContainsKey(outerKey))
                    target[outerKey] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                Dictionary<string, string> targetInner = target[outerKey];
                Dictionary<string, string> incomingInner =
                    outer.Value ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, string> inner in incomingInner)
                {
                    if (string.IsNullOrWhiteSpace(inner.Key))
                        continue;

                    string innerKey = inner.Key.Trim();
                    string right = inner.Value ?? string.Empty;
                    if (!targetInner.ContainsKey(innerKey))
                    {
                        targetInner[innerKey] = right;
                        continue;
                    }

                    string left = targetInner[innerKey] ?? string.Empty;
                    if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool takeRight = ResolveProfileMergeConflict(
                        mode,
                        result,
                        kind + " '" + outerKey + ":" + innerKey + "'",
                        ClipPreview(left),
                        ClipPreview(right),
                        sourceLabel);
                    if (takeRight)
                        targetInner[innerKey] = right;
                }
            }
        }

        private static void MergeModifierRenameMaps(
            Dictionary<string, string> target,
            Dictionary<string, string> incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string sourceLabel)
        {
            foreach (KeyValuePair<string, string> entry in incoming ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                string key = entry.Key.Trim();
                string right = entry.Value ?? string.Empty;
                if (!target.ContainsKey(key))
                {
                    target[key] = right;
                    continue;
                }

                string left = target[key] ?? string.Empty;
                if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool takeRight = ResolveProfileMergeConflict(
                    mode,
                    result,
                    "modifier rename '" + key + "'",
                    ClipPreview(left),
                    ClipPreview(right),
                    sourceLabel);
                if (takeRight)
                    target[key] = right;
            }
        }

        private static void MergeFfbDictionary(
            Dictionary<string, ForceFeedbackS> target,
            Dictionary<string, ForceFeedbackS> incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string sourceLabel)
        {
            foreach (KeyValuePair<string, ForceFeedbackS> entry in incoming ?? new Dictionary<string, ForceFeedbackS>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                string key = entry.Key.Trim();
                ForceFeedbackS right = CopyFfb(entry.Value);
                if (!target.ContainsKey(key))
                {
                    target[key] = right;
                    continue;
                }

                ForceFeedbackS left = target[key];
                if (AreFfbEquivalent(left, right))
                    continue;

                bool takeRight = ResolveProfileMergeConflict(
                    mode,
                    result,
                    "ffb '" + key + "'",
                    DescribeFfb(left),
                    DescribeFfb(right),
                    sourceLabel);
                if (takeRight)
                    target[key] = right;
            }
        }

        private static void MergeScalarString(
            ref string target,
            string incoming,
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string kind,
            string sourceLabel)
        {
            string right = incoming ?? string.Empty;
            if (string.IsNullOrWhiteSpace(right))
                return;

            string left = target ?? string.Empty;
            if (string.IsNullOrWhiteSpace(left))
            {
                target = right;
                return;
            }

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return;

            bool takeRight = ResolveProfileMergeConflict(
                mode,
                result,
                kind,
                ClipPreview(left),
                ClipPreview(right),
                sourceLabel);
            if (takeRight)
                target = right;
        }

        private static bool ResolveProfileMergeConflict(
            ProfileMergeMode mode,
            ProfileMergeResult result,
            string label,
            string leftValue,
            string rightValue,
            string sourceLabel)
        {
            result.Conflicts++;
            switch (mode)
            {
                case ProfileMergeMode.Left:
                    result.ResolvedLeft++;
                    return false;
                case ProfileMergeMode.Right:
                    result.ResolvedRight++;
                    return true;
                default:
                    bool chooseRight = PromptProfileMergeConflictChoice(label, leftValue, rightValue, sourceLabel);
                    if (chooseRight)
                        result.ResolvedRight++;
                    else
                        result.ResolvedLeft++;
                    return chooseRight;
            }
        }

        private static bool PromptProfileMergeConflictChoice(
            string label,
            string leftValue,
            string rightValue,
            string sourceLabel)
        {
            while (true)
            {
                Console.Error.WriteLine("Profile merge conflict: " + label);
                if (!string.IsNullOrWhiteSpace(sourceLabel))
                    Console.Error.WriteLine("Incoming source: " + sourceLabel);
                Console.Error.WriteLine("  [L] " + (string.IsNullOrWhiteSpace(leftValue) ? "<empty>" : leftValue));
                Console.Error.WriteLine("  [R] " + (string.IsNullOrWhiteSpace(rightValue) ? "<empty>" : rightValue));
                Console.Error.Write("Choose left or right [L/R]: ");

                string rawAnswer = Console.ReadLine();
                if (rawAnswer == null)
                    throw new InvalidOperationException("Profile merge conflict prompt needs console input, but stdin is closed. Use --mode keep-left or keep-right.");

                string answer = rawAnswer.Trim().ToLowerInvariant();
                if (answer == "l" || answer == "left")
                    return false;
                if (answer == "r" || answer == "right")
                    return true;
            }
        }

        private static bool AreRelationBindEntitiesEqual(
            Relation leftRelation,
            Bind leftBind,
            Relation rightRelation,
            Bind rightBind)
        {
            return AreRelationsEquivalent(leftRelation, rightRelation) &&
                   AreBindsEquivalent(leftBind, rightBind);
        }

        private static bool AreRelationsEquivalent(Relation left, Relation right)
        {
            string leftFingerprint = BuildRelationFingerprint(left);
            string rightFingerprint = BuildRelationFingerprint(right);
            return string.Equals(leftFingerprint, rightFingerprint, StringComparison.Ordinal);
        }

        private static bool AreBindsEquivalent(Bind left, Bind right)
        {
            string leftFingerprint = BuildBindFingerprint(left);
            string rightFingerprint = BuildBindFingerprint(right);
            return string.Equals(leftFingerprint, rightFingerprint, StringComparison.Ordinal);
        }

        private static bool AreFfbEquivalent(ForceFeedbackS left, ForceFeedbackS right)
        {
            if (left == null && right == null)
                return true;
            if (left == null || right == null)
                return false;
            return left.invertX == right.invertX &&
                   left.invertY == right.invertY &&
                   left.swapAxis == right.swapAxis;
        }

        private static string BuildRelationFingerprint(Relation relation)
        {
            if (relation == null)
                return "<null>";

            StringBuilder sb = new StringBuilder();
            sb.Append(relation.ISAXIS ? "axis" : "button");
            sb.Append("|groups:");

            List<string> groups = (relation.Groups ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i > 0)
                    sb.Append(",");
                sb.Append(groups[i].ToLowerInvariant());
            }

            List<RelationItem> nodes = (relation.NODES ?? new List<RelationItem>())
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.ID))
                .OrderBy(v => (v.Game ?? "DCS"), StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.ID, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < nodes.Count; i++)
            {
                RelationItem node = nodes[i];
                string game = string.IsNullOrWhiteSpace(node.Game) ? "DCS" : node.Game.Trim();
                string id = node.ID.Trim();

                sb.Append("|node:");
                sb.Append(game.ToLowerInvariant());
                sb.Append(":");
                sb.Append(id.ToLowerInvariant());

                Dictionary<string, bool> aircraft = NormalizeNodeAircraft(node.AIRCRAFT);
                foreach (KeyValuePair<string, bool> plane in aircraft.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append(";p:");
                    sb.Append(plane.Key.ToLowerInvariant());
                    sb.Append("=");
                    sb.Append(plane.Value ? "1" : "0");
                }

                DCSInput[] dcsInputs = node.AllInputs ?? new DCSInput[0];
                for (int d = 0; d < dcsInputs.Length; d++)
                {
                    DCSInput input = dcsInputs[d];
                    if (input == null)
                        continue;
                    sb.Append(";d:");
                    sb.Append((input.ID ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Plane ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Title ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Cat ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append(input.IsAxis ? "1" : "0");
                }

                OtherGameInput[] otherInputs = node.OtherInputs ?? new OtherGameInput[0];
                for (int o = 0; o < otherInputs.Length; o++)
                {
                    OtherGameInput input = otherInputs[o];
                    if (input == null)
                        continue;
                    sb.Append(";o:");
                    sb.Append((input.Game ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.ID ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Plane ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Title ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append((input.Category ?? string.Empty).Trim().ToLowerInvariant());
                    sb.Append(":");
                    sb.Append(input.IsAxis ? "1" : "0");
                }
            }

            return sb.ToString();
        }

        private static string BuildBindFingerprint(Bind bind)
        {
            if (bind == null)
                return "<null>";

            StringBuilder sb = new StringBuilder();
            sb.Append((bind.Joystick ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append((bind.JAxis ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append((bind.JButton ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append((bind.Reformer_depr ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append(bind.Inverted.HasValue ? (bind.Inverted.Value ? "1" : "0") : "n");
            sb.Append("|");
            sb.Append(bind.Slider.HasValue ? (bind.Slider.Value ? "1" : "0") : "n");
            sb.Append("|");
            sb.Append(bind.Deadzone.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("|");
            sb.Append(bind.SaturationX.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("|");
            sb.Append(bind.SaturationY.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("|");
            sb.Append((bind.AdditionalImportInfo ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append((bind.AliasJoystick ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append((bind.JoyBackup ?? string.Empty).Trim().ToLowerInvariant());
            sb.Append("|");
            sb.Append(bind.doubleTap ? "1" : "0");

            List<double> curve = bind.Curvature == null
                ? new List<double>()
                : new List<double>(bind.Curvature);
            sb.Append("|curve:");
            for (int i = 0; i < curve.Count; i++)
            {
                if (i > 0)
                    sb.Append(",");
                sb.Append(curve[i].ToString("R", CultureInfo.InvariantCulture));
            }

            List<string> modifiers = bind.AllReformers == null
                ? new List<string>()
                : bind.AllReformers
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            sb.Append("|mods:");
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (i > 0)
                    sb.Append(",");
                sb.Append(modifiers[i].ToLowerInvariant());
            }

            return sb.ToString();
        }

        private static string DescribeRelationBindEntity(Relation relation, Bind bind)
        {
            string kind = relation == null
                ? "unknown"
                : (relation.ISAXIS ? "axis" : "button");
            int nodes = relation == null || relation.NODES == null ? 0 : relation.NODES.Count;
            int groups = relation == null || relation.Groups == null ? 0 : relation.Groups.Count;
            string input = bind == null
                ? "<none>"
                : (!string.IsNullOrWhiteSpace(bind.JButton) ? bind.JButton : bind.JAxis);
            string device = bind == null ? "<none>" : (bind.Joystick ?? string.Empty);
            return ClipPreview(
                "type=" + kind +
                ", nodes=" + nodes.ToString(CultureInfo.InvariantCulture) +
                ", groups=" + groups.ToString(CultureInfo.InvariantCulture) +
                ", bind=" + device + ":" + input);
        }

        private static string DescribeFfb(ForceFeedbackS value)
        {
            if (value == null)
                return "<none>";
            return "invertX=" + value.invertX.ToString().ToLowerInvariant() +
                   ", invertY=" + value.invertY.ToString().ToLowerInvariant() +
                   ", swapAxis=" + value.swapAxis.ToString().ToLowerInvariant();
        }

        private static string ClipPreview(string value, int max = 180)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length <= max)
                return trimmed;
            return trimmed.Substring(0, max) + "...";
        }

        private static void ApplyMergedProfileData(ProfileMergeData merged)
        {
            InternalDataManagement.NewFile(true);

            InternalDataManagement.AllRelations =
                merged == null
                    ? new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Relation>(merged.Relations, StringComparer.OrdinalIgnoreCase);
            InternalDataManagement.AllBinds =
                merged == null
                    ? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Bind>(merged.Binds, StringComparer.OrdinalIgnoreCase);
            InternalDataManagement.JoystickAliases =
                merged == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(merged.JoystickAliases, StringComparer.OrdinalIgnoreCase);
            InternalDataManagement.JoystickFileImages =
                merged == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(merged.JoystickFileImages, StringComparer.OrdinalIgnoreCase);
            InternalDataManagement.JoystickLayoutExport =
                merged == null ? string.Empty : (merged.JoystickLayoutExport ?? string.Empty);
            InternalDataManagement.PlaneAliases =
                merged == null
                    ? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                    : CopyNestedStringDictionary(merged.PlaneAliases);
            InternalDataManagement.LocalJoystickPGUID =
                merged == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(merged.JoysticksPGuids, StringComparer.OrdinalIgnoreCase);
            InternalDataManagement.ModifierNameChanges =
                merged == null
                    ? new List<KeyValuePair<string, string>>()
                    : merged.ModifierNameChanges
                        .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(v => new KeyValuePair<string, string>(v.Key, v.Value ?? string.Empty))
                        .ToList();
            InternalDataManagement.JoystickFFB =
                merged == null
                    ? new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase)
                    : CopyFfbDictionary(merged.JoystickFfb);
            InternalDataManagement.FfbOverrides =
                merged == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : CopyStringDictionary(merged.FfbOverrides);

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static Dictionary<string, string> CopyStringDictionary(Dictionary<string, string> source)
        {
            return source == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, Dictionary<string, string>> CopyNestedStringDictionary(
            Dictionary<string, Dictionary<string, string>> source)
        {
            Dictionary<string, Dictionary<string, string>> output =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, string>> outer in
                source ?? new Dictionary<string, Dictionary<string, string>>())
            {
                if (string.IsNullOrWhiteSpace(outer.Key))
                    continue;
                string key = outer.Key.Trim();
                output[key] = outer.Value == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(outer.Value, StringComparer.OrdinalIgnoreCase);
            }
            return output;
        }

        private static Dictionary<string, string> ToModifierRenameMap(List<KeyValuePair<string, string>> source)
        {
            Dictionary<string, string> output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in source ?? new List<KeyValuePair<string, string>>())
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                    continue;
                output[pair.Key.Trim()] = pair.Value ?? string.Empty;
            }
            return output;
        }

        private static Dictionary<string, ForceFeedbackS> CopyFfbDictionary(Dictionary<string, ForceFeedbackS> source)
        {
            Dictionary<string, ForceFeedbackS> output = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ForceFeedbackS> entry in source ?? new Dictionary<string, ForceFeedbackS>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;
                output[entry.Key.Trim()] = CopyFfb(entry.Value);
            }
            return output;
        }

        private static ForceFeedbackS CopyFfb(ForceFeedbackS source)
        {
            if (source == null)
                return new ForceFeedbackS();
            return new ForceFeedbackS
            {
                invertX = source.invertX,
                invertY = source.invertY,
                swapAxis = source.swapAxis
            };
        }

        private static bool TryResolveRelationName(string query, out string relationName, out List<string> candidates)
        {
            candidates = new List<string>();
            relationName = null;

            if (string.IsNullOrWhiteSpace(query))
                return false;

            string trimmed = query.Trim();
            List<string> allNames = InternalDataManagement.AllRelations.Keys.ToList();

            candidates = allNames
                .Where(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name)
                .ToList();
            if (candidates.Count == 1)
            {
                relationName = candidates[0];
                return true;
            }
            if (candidates.Count > 1)
            {
                return false;
            }

            candidates = allNames
                .Where(name => name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(name => name)
                .ToList();
            if (candidates.Count == 1)
            {
                relationName = candidates[0];
                return true;
            }
            return false;
        }

        private static bool TryResolveBindRelationName(string query, out string relationName, out List<string> candidates)
        {
            candidates = new List<string>();
            relationName = null;

            if (string.IsNullOrWhiteSpace(query))
                return false;

            string trimmed = query.Trim();
            List<KeyValuePair<string, Bind>> allBinds = InternalDataManagement.AllBinds.ToList();

            candidates = allBinds
                .Where(kvp => string.Equals(kvp.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .Distinct()
                .OrderBy(name => name)
                .ToList();
            if (candidates.Count == 1)
            {
                relationName = candidates[0];
                return true;
            }
            if (candidates.Count > 1)
            {
                return false;
            }

            candidates = allBinds
                .Where(kvp =>
                {
                    Bind bind = kvp.Value;
                    if (bind == null)
                        return false;

                    string axis = bind.JAxis == null ? string.Empty : bind.JAxis;
                    string button = bind.JButton == null ? string.Empty : bind.JButton;
                    string joystick = bind.Joystick == null ? string.Empty : bind.Joystick;
                    string input = !string.IsNullOrWhiteSpace(button) ? button : axis;
                    string combo = joystick + "::" + input;

                    return string.Equals(axis, trimmed, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(button, trimmed, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(combo, trimmed, StringComparison.OrdinalIgnoreCase);
                })
                .Select(kvp => kvp.Key)
                .Distinct()
                .OrderBy(name => name)
                .ToList();
            if (candidates.Count == 1)
            {
                relationName = candidates[0];
                return true;
            }
            if (candidates.Count > 1)
            {
                return false;
            }

            candidates = allBinds
                .Where(kvp => kvp.Key.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(kvp => kvp.Key)
                .Distinct()
                .OrderBy(name => name)
                .ToList();
            if (candidates.Count == 1)
            {
                relationName = candidates[0];
                return true;
            }
            return false;
        }

        private static bool TryResolveRelationOrBindName(string query, out string relationName, out List<string> candidates)
        {
            relationName = null;
            candidates = new List<string>();

            List<string> relationCandidates;
            if (TryResolveRelationName(query, out relationName, out relationCandidates))
                return true;

            List<string> bindCandidates;
            if (TryResolveBindRelationName(query, out relationName, out bindCandidates))
                return true;

            candidates = (relationCandidates ?? new List<string>())
                .Concat(bindCandidates ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
            return false;
        }

        private static int RunDevices(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "list" : command[0].ToLowerInvariant();
            switch (action)
            {
                case "list":
                    return RunDevicesList(command.Skip(1).ToList(), outPath, textOutput);
                case "inputs":
                case "catalog":
                case "input-catalog":
                    return RunDevicesInputs(command.Skip(1).ToList(), outPath, textOutput);
                case "exchange":
                case "swap":
                    return RunDevicesExchange(command.Skip(1).ToList(), outPath, textOutput);
                case "replace":
                    return RunDevicesReplace(command.Skip(1).ToList(), outPath, textOutput);
                case "auto-remap":
                case "remap":
                    return RunDevicesAutoRemap(command.Skip(1).ToList(), outPath, textOutput);
                case "remove":
                case "purge":
                case "clear":
                    return RunDevicesRemove(command.Skip(1).ToList(), outPath, textOutput);
                case "kb":
                    return RunDevicesKb(command.Skip(1).ToList(), outPath, textOutput);
                case "reference":
                case "ref":
                    return RunDevicesReference(command.Skip(1).ToList(), outPath, textOutput);
                case "alias":
                case "aliases":
                    return RunDevicesAlias(command.Skip(1).ToList(), outPath, textOutput);
                default:
                    return Fail("Unknown devices action: " + action);
            }
        }

        private static int RunDevicesList(List<string> args, string outPath, bool textOutput)
        {
            bool connectedOnly = PopFlag(args, "--connected");
            bool profileOnly =
                PopFlag(args, "--profile-devices") ||
                PopFlag(args, "--profile-only") ||
                PopFlag(args, "--in-profile");

            if (args.Count > 0)
                return Fail("Unknown devices list argument(s): " + string.Join(" ", args));

            bool includeConnected = connectedOnly || !profileOnly;
            bool includeProfile = profileOnly || !connectedOnly;

            DeviceListResult result = new DeviceListResult();
            if (includeConnected)
                result.Connected = BuildConnectedJoystickList(result.Warnings);
            if (includeProfile)
                result.Profile = BuildProfileJoystickList();

            result.Connected = result.Connected
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
            result.Profile = result.Profile
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();

            HashSet<string> connectedSet = new HashSet<string>(result.Connected, StringComparer.OrdinalIgnoreCase);
            HashSet<string> profileSet = new HashSet<string>(result.Profile, StringComparer.OrdinalIgnoreCase);
            result.OnlyConnected = connectedSet.Where(v => !profileSet.Contains(v)).OrderBy(v => v).ToList();
            result.OnlyProfile = profileSet.Where(v => !connectedSet.Contains(v)).OrderBy(v => v).ToList();

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDevicesInputs(List<string> args, string outPath, bool textOutput)
        {
            bool includeOff = PopFlag(args, "--include-off");
            if (args.Count > 0)
                return Fail("Unknown devices inputs argument(s): " + string.Join(" ", args));

            DeviceInputCatalogResult result = BuildDeviceInputCatalog(includeOff);
            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDevicesExchange(List<string> args, string outPath, bool textOutput)
        {
            string oldToken = null;
            string newToken = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                oldToken = args[0];
                args.RemoveAt(0);
            }
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                newToken = args[0];
                args.RemoveAt(0);
            }

            string oldOption = PopOptionValue(args, "--old");
            if (string.IsNullOrWhiteSpace(oldOption))
                oldOption = PopOptionValue(args, "--from");
            if (string.IsNullOrWhiteSpace(oldOption))
                oldOption = PopOptionValue(args, "--device");

            string newOption = PopOptionValue(args, "--new");
            if (string.IsNullOrWhiteSpace(newOption))
                newOption = PopOptionValue(args, "--to");
            if (string.IsNullOrWhiteSpace(newOption))
                newOption = PopOptionValue(args, "--target");

            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out _);

            if (string.IsNullOrWhiteSpace(oldToken))
                oldToken = oldOption;
            if (string.IsNullOrWhiteSpace(newToken))
                newToken = newOption;

            if (string.IsNullOrWhiteSpace(oldToken) || string.IsNullOrWhiteSpace(newToken))
                return Fail("devices exchange requires <old-device> <new-device> (or --old/--new).");
            if (args.Count > 0)
                return Fail("Unknown devices exchange argument(s): " + string.Join(" ", args));

            List<DeviceCandidate> profileCandidates = BuildProfileDeviceCandidates();
            if (profileCandidates.Count == 0)
                return Fail("No profile-referenced devices are available to exchange.");

            if (!TryResolveDeviceToken(oldToken, profileCandidates, confirmSuggestions, "devices exchange", out string oldResolved, out string error))
                return Fail(error);

            List<string> warnings = new List<string>();
            List<DeviceCandidate> allCandidates = BuildDeviceMutationCandidates(profileCandidates, warnings);
            string newResolved;
            string requestedNew = CollapseSpaces(newToken);
            // A target given with its instance GUID is taken as is (e.g. a device that is not connected right now);
            // fuzzy matching would pick the old instance of the same model.
            bool exactTarget = !string.Equals(StripDeviceGuidSuffix(requestedNew), requestedNew, StringComparison.Ordinal);
            DeviceCandidate exactCandidate = allCandidates.FirstOrDefault(c => c != null && SameDeviceInstance(c.Name, requestedNew));
            if (exactCandidate != null)
                newResolved = exactCandidate.Name;
            else if (exactTarget)
                newResolved = requestedNew;
            else if (!TryResolveDeviceToken(newToken, allCandidates, confirmSuggestions, "devices exchange", out newResolved, out error))
                return Fail(error);

            if (SameDeviceInstance(oldResolved, newResolved))
                return Fail("devices exchange source and target resolve to the same device: '" + oldResolved + "'.");

            DeviceExchangeResult result = ExchangeDeviceReferences(oldResolved, newResolved);
            result.RequestedOldDevice = (oldToken ?? string.Empty).Trim();
            result.RequestedNewDevice = (newToken ?? string.Empty).Trim();
            result.ConfirmSuggestions = confirmSuggestions;
            if (warnings.Count > 0)
                result.Warnings.AddRange(warnings);

            if (result.TotalChanges == 0)
                return Fail("No profile references were updated for device '" + oldResolved + "'.");

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int RunDevicesRemove(List<string> args, string outPath, bool textOutput)
        {
            string positional = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                positional = args[0];
                args.RemoveAt(0);
            }

            List<string> deviceOptions = PopOptionValues(args, "--device");
            deviceOptions.AddRange(PopOptionValues(args, "--devices"));
            bool confirmSuggestions = PopOptionBoolean(args, "--confirm", true, out _);

            List<string> requested = ParseDelimitedOptionValues(deviceOptions);
            if (!string.IsNullOrWhiteSpace(positional))
                requested.Insert(0, positional.Trim());
            requested = requested
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (requested.Count == 0)
                return Fail("devices remove requires <device-name> or --device <name>.");
            if (requested.Count > 1)
                return Fail("devices remove accepts one target device at a time.");
            if (args.Count > 0)
                return Fail("Unknown devices remove argument(s): " + string.Join(" ", args));

            List<DeviceCandidate> profileCandidates = BuildProfileDeviceCandidates();
            if (profileCandidates.Count == 0)
                return Fail("No profile-referenced devices are available to remove.");

            string requestedDevice = requested[0];
            if (!TryResolveDeviceToken(requestedDevice, profileCandidates, confirmSuggestions, "devices remove", out string resolvedDevice, out string error))
                return Fail(error);

            DeviceRemoveResult result = RemoveDeviceReferences(resolvedDevice);
            result.RequestedDevice = requestedDevice;
            result.ConfirmSuggestions = confirmSuggestions;

            if (result.TotalChanges == 0)
                return Fail("No profile references were removed for device '" + resolvedDevice + "'.");

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static int UnbindBindInputs(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.Skip(1).ToList();
            string positional = null;
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                positional = args[0];
                args.RemoveAt(0);
            }

            string deviceFilter = PopOptionValue(args, "--device");
            string inputValue = PopOptionValue(args, "--input");
            string gameFilter = PopOptionValue(args, "--game");
            string formatRaw = PopOptionValue(args, "--format");
            bool allMatches = PopFlag(args, "--all");

            if (args.Count > 0)
                return Fail("Unknown binds unbind argument(s): " + string.Join(" ", args));

            if (!string.IsNullOrWhiteSpace(positional))
            {
                int split = positional.IndexOf("::", StringComparison.Ordinal);
                if (split > 0 && string.IsNullOrWhiteSpace(deviceFilter) && string.IsNullOrWhiteSpace(inputValue))
                {
                    deviceFilter = positional.Substring(0, split).Trim();
                    inputValue = positional.Substring(split + 2).Trim();
                }
                else if (string.IsNullOrWhiteSpace(inputValue))
                {
                    inputValue = positional.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(inputValue))
                return Fail("binds unbind requires --input <value> or positional <device::input|input>.");

            string format = string.IsNullOrWhiteSpace(formatRaw) ? "dcs" : formatRaw.Trim().ToLowerInvariant();
            string normalizeError;
            string normalizedInput = NormalizeInputForFormat(inputValue, format, out normalizeError);
            if (string.IsNullOrWhiteSpace(normalizedInput))
                return Fail(normalizeError);

            List<string> matches = new List<string>();
            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds)
            {
                string relationName = kvp.Key;
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(deviceFilter) &&
                    !string.Equals((bind.Joystick ?? string.Empty).Trim(), deviceFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(gameFilter))
                {
                    if (!InternalDataManagement.AllRelations.ContainsKey(relationName))
                        continue;

                    Relation relation = InternalDataManagement.AllRelations[relationName];
                    if (relation == null || relation.NODES == null || !relation.NODES.Any(n =>
                        string.Equals(string.IsNullOrWhiteSpace(n.Game) ? "DCS" : n.Game,
                                      gameFilter,
                                      StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                }

                if (BindMatchesInput(bind, normalizedInput))
                    matches.Add(relationName);
            }

            if (matches.Count == 0)
                return Fail("No bind matched input '" + normalizedInput + "'.");

            if (!allMatches && matches.Count > 1)
            {
                string sample = string.Join(", ", matches.Take(10));
                if (matches.Count > 10)
                    sample = sample + ", ...";
                return Fail("Ambiguous bind match for input '" + normalizedInput + "'. Matches: " + sample + ". Use --all or narrow with --device/--game.");
            }

            List<string> updated = new List<string>();
            IEnumerable<string> targets = allMatches ? matches : matches.Take(1);
            foreach (string relationName in targets)
            {
                if (!InternalDataManagement.AllBinds.ContainsKey(relationName))
                    continue;

                Bind bind = InternalDataManagement.AllBinds[relationName];
                if (bind == null)
                    continue;

                bool changed = false;
                if (string.Equals((bind.JButton ?? string.Empty).Trim(), normalizedInput, StringComparison.OrdinalIgnoreCase))
                {
                    bind.JButton = string.Empty;
                    changed = true;
                }
                if (string.Equals((bind.JAxis ?? string.Empty).Trim(), normalizedInput, StringComparison.OrdinalIgnoreCase))
                {
                    bind.JAxis = string.Empty;
                    changed = true;
                }

                if (!changed)
                    continue;

                InternalDataManagement.AllBinds[relationName] = bind;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = bind;
                updated.Add(relationName);
            }

            if (updated.Count == 0)
                return Fail("No bind matched input '" + normalizedInput + "'.");

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();

            BindUnbindResult result = new BindUnbindResult();
            result.Input = inputValue;
            result.NormalizedInput = normalizedInput;
            result.Format = format;
            result.DeviceFilter = deviceFilter ?? string.Empty;
            result.GameFilter = gameFilter ?? string.Empty;
            result.UpdatedRelations = updated.OrderBy(v => v).ToList();

            string payload = textOutput ? ToText(result) : EngineSerialization.ToJson(result);
            EmitPayload(payload, outPath);
            return 0;
        }

        private static ProfileValidationResult ValidateCurrentProfile()
        {
            ProfileValidationResult result = new ProfileValidationResult();

            List<Relation> relations = InternalDataManagement.AllRelations == null
                ? new List<Relation>()
                : InternalDataManagement.AllRelations.Values
                    .Where(v => v != null)
                    .ToList();
            List<Modifier> modifiers = InternalDataManagement.AllModifiers == null
                ? new List<Modifier>()
                : InternalDataManagement.AllModifiers.Values
                    .Where(v => v != null)
                    .ToList();

            Dictionary<string, List<string>> relationItemKeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < relations.Count; i++)
            {
                Relation relation = relations[i];
                string relationName = string.IsNullOrWhiteSpace(relation.NAME)
                    ? "<unnamed relation>"
                    : relation.NAME;

                List<RelationItem> nodes = relation.NODES ?? new List<RelationItem>();
                for (int j = 0; j < nodes.Count; j++)
                {
                    RelationItem node = nodes[j];
                    if (node == null || string.IsNullOrWhiteSpace(node.ID) || node.AIRCRAFT == null)
                        continue;

                    foreach (KeyValuePair<string, bool> aircraftEntry in node.AIRCRAFT)
                    {
                        if (!aircraftEntry.Value || string.IsNullOrWhiteSpace(aircraftEntry.Key))
                            continue;

                        string key = node.ID + "__" + aircraftEntry.Key.Trim();
                        if (!relationItemKeys.TryGetValue(key, out List<string> relationNames))
                        {
                            relationNames = new List<string>();
                            relationItemKeys[key] = relationNames;
                        }

                        if (!relationNames.Any(v => string.Equals(v, relationName, StringComparison.OrdinalIgnoreCase)))
                            relationNames.Add(relationName);
                    }
                }
            }

            foreach (KeyValuePair<string, List<string>> kvp in relationItemKeys)
            {
                List<string> relationNames = kvp.Value ?? new List<string>();
                if (relationNames.Count < 2)
                    continue;

                StringBuilder warning = new StringBuilder();
                warning.Append("Relation Item key found in multiple Relation: ");
                warning.Append(kvp.Key);
                warning.Append(" The Relations it was found in: ");
                warning.Append(relationNames[0]);
                for (int i = 1; i < relationNames.Count; i++)
                    warning.Append(", ").Append(relationNames[i]);

                AddUnique(result.RelationWarnings, warning.ToString());
                AddUnique(result.Warnings, warning.ToString());
            }

            Dictionary<string, List<string>> bindKeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                string relationName = kvp.Key ?? string.Empty;
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;

                Relation relation = bind.Rl;
                if (relation == null && InternalDataManagement.AllRelations != null && InternalDataManagement.AllRelations.ContainsKey(relationName))
                    relation = InternalDataManagement.AllRelations[relationName];
                if (relation == null)
                    continue;

                string resolvedRelationName = string.IsNullOrWhiteSpace(relation.NAME)
                    ? (string.IsNullOrWhiteSpace(relationName) ? "<unnamed relation>" : relationName)
                    : relation.NAME;
                string joystick = bind.Joystick ?? string.Empty;
                string input = relation.ISAXIS ? (bind.JAxis ?? string.Empty) : (bind.JButton ?? string.Empty);

                List<RelationItem> nodes = relation.NODES ?? new List<RelationItem>();
                for (int j = 0; j < nodes.Count; j++)
                {
                    RelationItem node = nodes[j];
                    if (node == null || node.AIRCRAFT == null)
                        continue;

                    string game = NormalizeValidationGame(node.Game);
                    if (string.Equals(game, "StarCitizen", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (KeyValuePair<string, bool> aircraftEntry in node.AIRCRAFT)
                    {
                        if (!aircraftEntry.Value || string.IsNullOrWhiteSpace(aircraftEntry.Key))
                            continue;

                        StringBuilder keyBuilder = new StringBuilder();
                        keyBuilder.Append(game);
                        keyBuilder.Append(":");
                        keyBuilder.Append(aircraftEntry.Key.Trim());
                        keyBuilder.Append("§");
                        keyBuilder.Append(joystick);
                        keyBuilder.Append("§");
                        keyBuilder.Append(input);

                        List<string> reformers = bind.AllReformers ?? new List<string>();
                        for (int r = 0; r < reformers.Count; r++)
                        {
                            keyBuilder.Append("§");
                            keyBuilder.Append(reformers[r] ?? string.Empty);
                        }

                        string bindKey = keyBuilder.ToString();
                        if (!bindKeys.TryGetValue(bindKey, out List<string> relationNames))
                        {
                            relationNames = new List<string>();
                            bindKeys[bindKey] = relationNames;
                        }

                        if (!relationNames.Any(v => string.Equals(v, resolvedRelationName, StringComparison.OrdinalIgnoreCase)))
                            relationNames.Add(resolvedRelationName);
                    }
                }
            }

            foreach (KeyValuePair<string, List<string>> kvp in bindKeys)
            {
                List<string> relationNames = kvp.Value ?? new List<string>();
                if (relationNames.Count < 2)
                    continue;

                StringBuilder error = new StringBuilder();
                error.Append("Button Item key found in multiple Relation: ");
                error.Append(kvp.Key);
                error.Append(" The Relations it was found in: ");
                error.Append(relationNames[0]);
                for (int i = 1; i < relationNames.Count; i++)
                    error.Append(", ").Append(relationNames[i]);

                AddUnique(result.BindErrors, error.ToString());
                AddUnique(result.Errors, error.ToString());
            }

            Dictionary<string, Dictionary<string, bool>> modifierUsedOnCraft =
                new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                string relationName = kvp.Key ?? string.Empty;
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;

                Relation relation = bind.Rl;
                if (relation == null && InternalDataManagement.AllRelations != null && InternalDataManagement.AllRelations.ContainsKey(relationName))
                    relation = InternalDataManagement.AllRelations[relationName];
                if (relation == null || relation.NODES == null)
                    continue;

                List<string> reformers = bind.AllReformers ?? new List<string>();
                for (int m = 0; m < modifiers.Count; m++)
                {
                    Modifier modifier = modifiers[m];
                    string modifierReformer = modifier.toReformerString();
                    if (!reformers.Any(v => string.Equals(v, modifierReformer, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string modifierSignature = BuildModifierSignature(modifier);
                    if (!modifierUsedOnCraft.TryGetValue(modifierSignature, out Dictionary<string, bool> usedPlanes))
                    {
                        usedPlanes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        modifierUsedOnCraft[modifierSignature] = usedPlanes;
                    }

                    for (int n = 0; n < relation.NODES.Count; n++)
                    {
                        RelationItem node = relation.NODES[n];
                        if (node == null || node.AIRCRAFT == null)
                            continue;

                        foreach (KeyValuePair<string, bool> aircraftEntry in node.AIRCRAFT)
                        {
                            if (!aircraftEntry.Value || string.IsNullOrWhiteSpace(aircraftEntry.Key))
                                continue;
                            usedPlanes[aircraftEntry.Key.Trim()] = true;
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds ?? new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase))
            {
                string relationName = kvp.Key ?? string.Empty;
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;

                Relation relation = bind.Rl;
                if (relation == null && InternalDataManagement.AllRelations != null && InternalDataManagement.AllRelations.ContainsKey(relationName))
                    relation = InternalDataManagement.AllRelations[relationName];
                if (relation == null || relation.ISAXIS || relation.NODES == null)
                    continue;

                for (int m = 0; m < modifiers.Count; m++)
                {
                    Modifier modifier = modifiers[m];
                    if (!string.Equals(modifier.device ?? string.Empty, bind.Joystick ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(modifier.key ?? string.Empty, bind.JButton ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string modifierSignature = BuildModifierSignature(modifier);
                    if (!modifierUsedOnCraft.TryGetValue(modifierSignature, out Dictionary<string, bool> usedPlanes))
                        continue;

                    for (int n = 0; n < relation.NODES.Count; n++)
                    {
                        RelationItem node = relation.NODES[n];
                        if (node == null || node.AIRCRAFT == null)
                            continue;

                        foreach (KeyValuePair<string, bool> aircraftEntry in node.AIRCRAFT)
                        {
                            if (!aircraftEntry.Value || string.IsNullOrWhiteSpace(aircraftEntry.Key))
                                continue;

                            string aircraft = aircraftEntry.Key.Trim();
                            if (!usedPlanes.ContainsKey(aircraft))
                                continue;

                            string resolvedRelationName = string.IsNullOrWhiteSpace(relation.NAME)
                                ? (string.IsNullOrWhiteSpace(relationName) ? "<unnamed relation>" : relationName)
                                : relation.NAME;
                            string error =
                                "Modifier error, Modifier used on craft, which also uses modifier as a regular button. Relation: " +
                                resolvedRelationName +
                                " with Joystick: " + (bind.Joystick ?? string.Empty) +
                                " with Button: " + (bind.JButton ?? string.Empty) +
                                " and Modifier: " + (modifier.name ?? string.Empty);
                            AddUnique(result.ModifierErrors, error);
                            AddUnique(result.Errors, error);
                        }
                    }
                }
            }

            for (int i = 0; i < relations.Count; i++)
            {
                Relation relation = relations[i];
                string relationName = string.IsNullOrWhiteSpace(relation.NAME)
                    ? "<unnamed relation>"
                    : relation.NAME;
                Dictionary<string, int> planeState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                List<RelationItem> nodes = relation.NODES ?? new List<RelationItem>();
                for (int n = 0; n < nodes.Count; n++)
                {
                    RelationItem node = nodes[n];
                    if (node == null || node.AIRCRAFT == null)
                        continue;

                    string game = NormalizeValidationGame(node.Game);
                    foreach (KeyValuePair<string, bool> aircraftEntry in node.AIRCRAFT)
                    {
                        if (!aircraftEntry.Value || string.IsNullOrWhiteSpace(aircraftEntry.Key))
                            continue;

                        string key = game + "§" + aircraftEntry.Key.Trim();
                        if (!planeState.ContainsKey(key))
                            planeState[key] = 0;
                        planeState[key]++;
                    }
                }

                foreach (KeyValuePair<string, int> planeEntry in planeState)
                {
                    if (planeEntry.Value <= 1)
                        continue;

                    string[] parts = planeEntry.Key.Split(new[] { '§' }, 2);
                    string game = parts.Length > 0 ? parts[0] : "DCS";
                    string plane = parts.Length > 1 ? parts[1] : string.Empty;
                    string error =
                        "The game " + game +
                        " has multiple active items for the plane " + plane +
                        " on the Relation " + relationName;
                    AddUnique(result.DuplicateActiveErrors, error);
                    AddUnique(result.Errors, error);
                }
            }

            return result;
        }

        private static string NormalizeValidationGame(string game)
        {
            return string.IsNullOrWhiteSpace(game) ? "DCS" : game.Trim();
        }

        private static string BuildModifierSignature(Modifier modifier)
        {
            if (modifier == null)
                return string.Empty;
            return (modifier.name ?? string.Empty).Trim() + "§" +
                   (modifier.device ?? string.Empty).Trim() + "§" +
                   (modifier.key ?? string.Empty).Trim();
        }

        private static void AddUnique(List<string> target, string message)
        {
            if (target == null || string.IsNullOrWhiteSpace(message))
                return;
            if (!target.Any(v => string.Equals(v, message, StringComparison.Ordinal)))
                target.Add(message);
        }

        private static bool BindMatchesInput(Bind bind, string normalizedInput)
        {
            if (bind == null || string.IsNullOrWhiteSpace(normalizedInput))
                return false;

            string btn = (bind.JButton ?? string.Empty).Trim();
            string axis = (bind.JAxis ?? string.Empty).Trim();
            return string.Equals(btn, normalizedInput, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(axis, normalizedInput, StringComparison.OrdinalIgnoreCase);
        }

        private const string KeyboardConversionSeparator = "\u00A7";

        private static DeviceInputCatalogResult BuildDeviceInputCatalog(bool includeOff)
        {
            DeviceInputCatalogResult result = new DeviceInputCatalogResult();
            result.JoystickAxes = BuildPossibleJoystickAxes();
            result.JoystickButtons = BuildPossibleJoystickButtons();
            result.JoystickPovButtons = BuildPossibleJoystickPovButtons();

            if (includeOff)
            {
                result.JoystickOffVariants = BuildOffVariants(
                    result.JoystickAxes
                        .Concat(result.JoystickButtons)
                        .Concat(result.JoystickPovButtons));
            }

            Dictionary<string, string> dcsToDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> dxToDcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadKeyboardConversion("DCS2DX.keyboardconversion", dcsToDx, dxToDcs, result.Warnings);

            Dictionary<string, string> il2ToDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> dxToIl2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadKeyboardConversion("IL2DX.keyboardconversion", il2ToDx, dxToIl2, result.Warnings);

            Dictionary<string, string> scToDx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> dxToSc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadKeyboardConversion("SC2DX.keyboardconversion", scToDx, dxToSc, result.Warnings);
            _ = dxToSc;

            HashSet<string> directInputTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTokens(directInputTokens, BuildBaseDirectInputKeyboardTokens());
            AddTokens(directInputTokens, dcsToDx.Values);
            AddTokens(directInputTokens, il2ToDx.Values);
            AddTokens(directInputTokens, scToDx.Values);

            HashSet<string> dcsTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> il2Tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dxTokenRaw in directInputTokens)
            {
                string dxToken = (dxTokenRaw ?? string.Empty).Trim();
                if (dxToken.Length == 0)
                    continue;

                if (!dxToDcs.TryGetValue(dxToken, out string dcsToken))
                    dcsToken = dxToken;
                if (!string.IsNullOrWhiteSpace(dcsToken))
                    dcsTokens.Add(dcsToken.Trim());

                if (!dxToIl2.TryGetValue(dxToken, out string il2Raw))
                    il2Raw = dxToken.ToLowerInvariant();
                il2Raw = (il2Raw ?? string.Empty).Trim();
                if (il2Raw.Length > 0)
                    il2Tokens.Add("key_" + il2Raw.ToLowerInvariant());
            }

            foreach (KeyValuePair<string, string> kvp in dcsToDx)
            {
                string dcsToken = (kvp.Key ?? string.Empty).Trim();
                if (dcsToken.Length > 0)
                    dcsTokens.Add(dcsToken);
            }

            foreach (KeyValuePair<string, string> kvp in il2ToDx)
            {
                string il2Raw = (kvp.Key ?? string.Empty).Trim().ToLowerInvariant();
                if (il2Raw.Length > 0)
                    il2Tokens.Add("key_" + il2Raw);
            }

            result.KeyboardDirectInput = directInputTokens
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.KeyboardDcs = dcsTokens
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.KeyboardIl2 = il2Tokens
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return result;
        }

        private static List<string> BuildPossibleJoystickAxes()
        {
            return new List<string>
            {
                "JOY_X",
                "JOY_Y",
                "JOY_Z",
                "JOY_RX",
                "JOY_RY",
                "JOY_RZ",
                "JOY_SLIDER1",
                "JOY_SLIDER2"
            };
        }

        private static List<string> BuildPossibleJoystickButtons()
        {
            List<string> result = new List<string>();
            for (int i = 1; i <= 128; i++)
                result.Add("JOY_BTN" + i.ToString(CultureInfo.InvariantCulture));
            return result;
        }

        private static List<string> BuildPossibleJoystickPovButtons()
        {
            string[] directions = new[] { "U", "UR", "R", "DR", "D", "DL", "L", "UL" };
            List<string> result = new List<string>();
            for (int pov = 0; pov <= 8; pov++)
            {
                for (int i = 0; i < directions.Length; i++)
                    result.Add("JOY_BTN_POV" + pov.ToString(CultureInfo.InvariantCulture) + "_" + directions[i]);
            }
            return result;
        }

        private static List<string> BuildOffVariants(IEnumerable<string> tokens)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string tokenRaw in tokens ?? Enumerable.Empty<string>())
            {
                string token = (tokenRaw ?? string.Empty).Trim();
                if (token.Length == 0)
                    continue;

                string variant = token + "_OFF";
                if (seen.Add(variant))
                    result.Add(variant);
            }
            return result;
        }

        private static List<string> BuildBaseDirectInputKeyboardTokens()
        {
            HashSet<string> tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (char c = 'A'; c <= 'Z'; c++)
                tokens.Add(c.ToString());
            for (int i = 0; i <= 9; i++)
                tokens.Add("D" + i.ToString(CultureInfo.InvariantCulture));
            for (int i = 1; i <= 24; i++)
                tokens.Add("F" + i.ToString(CultureInfo.InvariantCulture));

            string[] fixedTokens = new[]
            {
                "Escape", "Tab", "CapsLock",
                "LeftShift", "RightShift",
                "LeftControl", "RightControl",
                "LeftAlt", "RightAlt",
                "LeftWindowsKey", "RightWindowsKey",
                "Space", "Return", "Backspace",
                "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
                "UpArrow", "DownArrow", "LeftArrow", "RightArrow",
                "Pause", "ScrollLock", "NumberLock", "PrintScreen",
                "Grave", "Minus", "Equals", "Backslash", "Semicolon", "Apostrophe",
                "LeftBracket", "RightBracket", "Comma", "Period", "Slash", "Oem102",
                "NumberPadPlus", "NumberPadMinus", "NumberPadStar", "NumberPadSlash", "NumberPadPeriod", "NumberPadEnter"
            };
            AddTokens(tokens, fixedTokens);
            for (int i = 0; i <= 9; i++)
                tokens.Add("NumberPad" + i.ToString(CultureInfo.InvariantCulture));

            return tokens
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddTokens(HashSet<string> map, IEnumerable<string> values)
        {
            if (map == null || values == null)
                return;

            foreach (string raw in values)
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length > 0)
                    map.Add(value);
            }
        }

        private static void LoadKeyboardConversion(
            string fileName,
            Dictionary<string, string> leftToRight,
            Dictionary<string, string> rightToLeft,
            List<string> warnings)
        {
            if (leftToRight == null || rightToLeft == null)
                return;

            string basePath = MainStructure.PROGPATH ?? string.Empty;
            if (string.IsNullOrWhiteSpace(basePath))
                basePath = Environment.CurrentDirectory;
            string path = Path.Combine(basePath, "TOOLS", "Conversions", fileName ?? string.Empty);
            if (!File.Exists(path))
            {
                warnings?.Add("Missing keyboard conversion file: " + path);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read keyboard conversion file '" + path + "': " + ex.Message);
                return;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!TryParseKeyboardConversionLine(lines[i], out string left, out string right))
                    continue;

                if (!leftToRight.ContainsKey(left))
                    leftToRight[left] = right;
                if (!rightToLeft.ContainsKey(right))
                    rightToLeft[right] = left;
            }
        }

        private static bool TryParseKeyboardConversionLine(string line, out string left, out string right)
        {
            left = string.Empty;
            right = string.Empty;

            string normalized = (line ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return false;

            normalized = normalized.Replace("Ã‚Â§", KeyboardConversionSeparator);
            normalized = normalized.Replace("Â§", KeyboardConversionSeparator);
            int split = normalized.IndexOf(KeyboardConversionSeparator, StringComparison.Ordinal);
            if (split <= 0 || split >= normalized.Length - KeyboardConversionSeparator.Length)
                return false;

            left = normalized.Substring(0, split).Trim();
            right = normalized.Substring(split + KeyboardConversionSeparator.Length).Trim();
            return left.Length > 0 && right.Length > 0;
        }

        private static List<string> BuildProfileJoystickList()
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (InternalDataManagement.LocalJoysticks != null)
            {
                for (int i = 0; i < InternalDataManagement.LocalJoysticks.Length; i++)
                {
                    string joy = InternalDataManagement.LocalJoysticks[i];
                    if (!string.IsNullOrWhiteSpace(joy) && !string.Equals(joy.Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy.Trim());
                }
            }

            foreach (KeyValuePair<string, Bind> kvp in InternalDataManagement.AllBinds)
            {
                Bind bind = kvp.Value;
                if (bind == null)
                    continue;

                string joy = (bind.Joystick ?? string.Empty).Trim();
                if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    output.Add(joy);
            }

            foreach (KeyValuePair<string, Modifier> kvp in InternalDataManagement.AllModifiers)
            {
                Modifier modifier = kvp.Value;
                if (modifier == null)
                    continue;

                string joy = (modifier.device ?? string.Empty).Trim();
                if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                    output.Add(joy);
            }

            if (InternalDataManagement.LocalJoystickPGUID != null)
            {
                foreach (KeyValuePair<string, string> kvp in InternalDataManagement.LocalJoystickPGUID)
                {
                    string joy = (kvp.Key ?? string.Empty).Trim();
                    if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy);
                }
            }

            if (InternalDataManagement.JoystickAliases != null)
            {
                foreach (KeyValuePair<string, string> kvp in InternalDataManagement.JoystickAliases)
                {
                    string joy = (kvp.Key ?? string.Empty).Trim();
                    if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy);
                }
            }

            if (InternalDataManagement.JoystickFileImages != null)
            {
                foreach (KeyValuePair<string, string> kvp in InternalDataManagement.JoystickFileImages)
                {
                    string joy = (kvp.Key ?? string.Empty).Trim();
                    if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy);
                }
            }

            if (InternalDataManagement.JoystickActivity != null)
            {
                foreach (KeyValuePair<string, bool> kvp in InternalDataManagement.JoystickActivity)
                {
                    string joy = (kvp.Key ?? string.Empty).Trim();
                    if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy);
                }
            }

            if (InternalDataManagement.JoystickFFB != null)
            {
                foreach (KeyValuePair<string, ForceFeedbackS> kvp in InternalDataManagement.JoystickFFB)
                {
                    string joy = (kvp.Key ?? string.Empty).Trim();
                    if (joy.Length > 0 && !string.Equals(joy, "Keyboard", StringComparison.OrdinalIgnoreCase))
                        output.Add(joy);
                }
            }

            return output.OrderBy(v => v).ToList();
        }

        private static List<string> BuildConnectedJoystickList(List<string> warnings)
        {
            if (TryGetConnectedJoystickCache(warnings, out List<string> cached))
                return cached;

            List<string> localWarnings = new List<string>();
            List<string> discovered = BuildConnectedJoystickListLegacy(localWarnings);
            CacheConnectedJoystickList(discovered, localWarnings);
            if (warnings != null)
            {
                for (int i = 0; i < localWarnings.Count; i++)
                    AddUnique(warnings, localWarnings[i]);
            }
            return discovered;
        }

        private static List<string> BuildConnectedJoystickListBestEffort(List<string> warnings)
        {
            return BuildConnectedJoystickListLegacy(warnings);
        }

        private static bool TryGetConnectedJoystickCache(List<string> warnings, out List<string> cached)
        {
            lock (ConnectedJoystickCacheLock)
            {
                if (ConnectedJoystickCache == null)
                {
                    cached = null;
                    return false;
                }

                cached = new List<string>(ConnectedJoystickCache);
                if (warnings != null && ConnectedJoystickCacheWarnings != null)
                {
                    for (int i = 0; i < ConnectedJoystickCacheWarnings.Count; i++)
                        AddUnique(warnings, ConnectedJoystickCacheWarnings[i]);
                }
                return true;
            }
        }

        private static void CacheConnectedJoystickList(List<string> devices, List<string> warnings)
        {
            lock (ConnectedJoystickCacheLock)
            {
                ConnectedJoystickCache = (devices ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                ConnectedJoystickCacheWarnings = (warnings ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
        }

        private static List<string> BuildConnectedJoystickListLegacy(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDirectInputConnectedJoysticks(output, warnings);
            AddJoyCplConnectedJoysticks(output, warnings);
            AddWmiConnectedJoysticks(output, warnings);
            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool AddDirectInputConnectedJoysticks(HashSet<string> output, List<string> warnings)
        {
            if (output == null)
                return false;

            List<string> localWarnings = new List<string>();
            List<string> devices = DirectInputDeviceDiscovery.EnumerateConnectedDevices(localWarnings);
            for (int i = 0; i < devices.Count; i++)
            {
                string value = devices[i];
                if (!string.IsNullOrWhiteSpace(value))
                    output.Add(value.Trim());
            }

            if (warnings != null)
            {
                for (int i = 0; i < localWarnings.Count; i++)
                    AddUnique(warnings, localWarnings[i]);
            }
            return localWarnings.Count == 0;
        }

        private static bool AddJoyCplConnectedJoysticks(HashSet<string> output, List<string> warnings)
        {
            if (output == null)
                return false;

            try
            {
                uint deviceSlots = joyGetNumDevs();
                uint capsSize = (uint)Marshal.SizeOf(typeof(JoyCaps));
                for (uint id = 0; id < deviceSlots; id++)
                {
                    JoyCaps caps = new JoyCaps();
                    int result = joyGetDevCaps(id, ref caps, capsSize);
                    if (result != JoyErrNoError)
                        continue;

                    string name = (caps.ProductName ?? string.Empty).Trim().Trim('\0');
                    if (name.Length > 0)
                        output.Add(name);
                }
                return true;
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "joy.cpl device query failed: " + ex.Message);
                return false;
            }
        }

        private static bool AddWmiConnectedJoysticks(HashSet<string> output, List<string> warnings)
        {
            if (output == null)
                return false;

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass, Status FROM Win32_PnPEntity WHERE Status='OK'"))
                using (ManagementObjectCollection collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        string name = Convert.ToString(obj["Name"]) ?? string.Empty;
                        string pnpClass = Convert.ToString(obj["PNPClass"]) ?? string.Empty;
                        if (!IsLikelyJoystickName(name, pnpClass))
                            continue;

                        output.Add(name.Trim());
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "Connected joystick query failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsLikelyJoystickName(string name, string pnpClass)
        {
            string value = (name ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;

            string upper = value.ToUpperInvariant();
            string cls = (pnpClass ?? string.Empty).ToUpperInvariant();
            if (upper.Contains("HOST CONTROLLER") ||
                upper.Contains("SYSTEM CONTROLLER") ||
                upper.Contains("I2C CONTROLLER") ||
                upper.Contains("GPIO CONTROLLER") ||
                upper.Contains("AUDIO CONTROLLER") ||
                upper.Contains("NVM EXPRESS CONTROLLER") ||
                upper.Contains("AHCI CONTROLLER") ||
                upper.Contains("INTERRUPT CONTROLLER") ||
                upper.Contains("MEMORY ACCESS CONTROLLER") ||
                upper.Contains("STORAGE SPACES CONTROLLER"))
            {
                return false;
            }

            bool inputLikeClass = cls.Length == 0 || cls.Contains("HID") || cls.Contains("USB") || cls.Contains("MEDIA");
            if (!inputLikeClass)
                return false;

            return upper.Contains("JOYSTICK") ||
                   upper.Contains("GAMEPAD") ||
                   upper.Contains("GAME CONTROLLER") ||
                   upper.Contains("HOTAS") ||
                   upper.Contains("THROTTLE") ||
                   upper.Contains("RUDDER") ||
                   upper.Contains("PEDAL") ||
                   upper.Contains("FLIGHT STICK") ||
                   upper.Contains("FLIGHTSTICK") ||
                   upper.Contains("XBOX") ||
                   upper.Contains("VJOY") ||
                   upper.Contains("VKB") ||
                   upper.Contains("WINWING") ||
                   upper.Contains("VIRPIL");
        }

        private const int JoyErrNoError = 0;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern uint joyGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int joyGetDevCaps(uint uJoyID, ref JoyCaps pjc, uint cbjc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JoyCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ProductName;
            public uint XMin;
            public uint XMax;
            public uint YMin;
            public uint YMax;
            public uint ZMin;
            public uint ZMax;
            public uint NumButtons;
            public uint PeriodMin;
            public uint PeriodMax;
            public uint RMin;
            public uint RMax;
            public uint UMin;
            public uint UMax;
            public uint VMin;
            public uint VMax;
            public uint Caps;
            public uint MaxAxes;
            public uint NumAxes;
            public uint MaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string RegistryKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string OemVxd;
        }

        private static List<DeviceCandidate> BuildDcsFfbDeviceCandidates(
            string savedGamesPath,
            List<string> warnings)
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, BuildProfileJoystickList(), "profile", 0);
            AddDeviceCandidates(
                map,
                InternalDataManagement.JoystickFFB == null
                    ? new List<string>()
                    : InternalDataManagement.JoystickFFB.Keys.Where(k => !string.IsNullOrWhiteSpace(k) && !string.Equals(k.Trim(), "Keyboard", StringComparison.OrdinalIgnoreCase)),
                "profile-ffb",
                0);
            AddDeviceCandidates(
                map,
                BuildConnectedJoystickListBestEffort(warnings),
                "connected",
                1);
            AddDeviceCandidates(map, DiscoverDcsInputDevices(savedGamesPath, warnings), "dcs-input", 2);

            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DeviceCandidate> BuildDcsDeviceCandidates(string savedGamesPath, List<string> warnings)
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, BuildProfileJoystickList(), "profile", 0);
            AddDeviceCandidates(map, BuildConnectedJoystickListBestEffort(warnings), "connected", 1);
            AddDeviceCandidates(map, DiscoverDcsInputDevices(savedGamesPath, warnings), "dcs-input", 2);

            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DeviceCandidate> BuildIl2DeviceCandidates(string il2RootPath, List<string> warnings)
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, BuildProfileJoystickList(), "profile", 0);
            AddDeviceCandidates(map, BuildConnectedJoystickListBestEffort(warnings), "connected", 1);
            AddDeviceCandidates(map, DiscoverIl2InputDevices(il2RootPath, warnings), "il2-input", 2);

            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DeviceCandidate> BuildStarCitizenDeviceCandidates(
            string rootOrPath,
            string sourcePath,
            List<string> warnings)
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, BuildProfileJoystickList(), "profile", 0);
            AddDeviceCandidates(map, BuildConnectedJoystickListBestEffort(warnings), "connected", 1);

            List<string> localWarnings = new List<string>();
            List<string> discovered = EngineStarCitizen.DiscoverInputDevices(rootOrPath, sourcePath, localWarnings);
            for (int i = 0; i < localWarnings.Count; i++)
                AddUnique(warnings, localWarnings[i]);
            AddDeviceCandidates(map, discovered, "sc-input", 2);

            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddDeviceCandidates(
            Dictionary<string, DeviceCandidate> map,
            IEnumerable<string> names,
            string source,
            int priority)
        {
            if (map == null || names == null)
                return;

            foreach (string raw in names)
            {
                string name = (raw ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;

                if (map.TryGetValue(name, out DeviceCandidate existing))
                {
                    if (priority < existing.Priority)
                    {
                        existing.Priority = priority;
                        existing.Source = source;
                    }
                    continue;
                }

                DeviceCandidate candidate = new DeviceCandidate();
                candidate.Name = name;
                candidate.Source = source;
                candidate.Priority = priority;
                map[name] = candidate;
            }
        }

        private static List<DeviceCandidate> BuildProfileDeviceCandidates()
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, BuildProfileJoystickList(), "profile", 0);
            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DeviceCandidate> BuildDeviceMutationCandidates(
            List<DeviceCandidate> profileCandidates,
            List<string> warnings)
        {
            Dictionary<string, DeviceCandidate> map = new Dictionary<string, DeviceCandidate>(StringComparer.OrdinalIgnoreCase);
            AddDeviceCandidates(map, (profileCandidates ?? new List<DeviceCandidate>()).Select(c => c?.Name), "profile", 0);
            AddDeviceCandidates(map, BuildConnectedJoystickListBestEffort(warnings), "connected", 1);
            return map.Values
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryResolveDeviceToken(
            string requestedToken,
            List<DeviceCandidate> candidates,
            bool confirmSuggestions,
            string scope,
            out string resolved,
            out string error)
        {
            resolved = string.Empty;
            error = string.Empty;

            string token = (requestedToken ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                error = "Device value is required for " + scope + ".";
                return false;
            }

            if (candidates == null || candidates.Count == 0)
            {
                resolved = token;
                return true;
            }

            DeviceCandidate fullMatch = candidates.FirstOrDefault(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.Name) &&
                string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
            if (fullMatch != null)
            {
                resolved = fullMatch.Name;
                return true;
            }

            string tokenBase = StripDeviceGuidSuffix(token);
            List<DeviceCandidate> baseMatches = candidates
                .Where(c => c != null &&
                            !string.IsNullOrWhiteSpace(c.Name) &&
                            string.Equals(StripDeviceGuidSuffix(c.Name), tokenBase, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (baseMatches.Count == 1)
            {
                resolved = baseMatches[0].Name;
                return true;
            }

            DeviceCandidate best = ChooseBestDeviceCandidate(token, baseMatches.Count > 1 ? baseMatches : candidates);
            if (best == null)
            {
                error = "No device matched '" + token + "' for " + scope + ".";
                return false;
            }

            if (confirmSuggestions)
            {
                string promptPrefix = baseMatches.Count > 1
                    ? "Multiple devices matched '" + token + "'"
                    : "Device '" + token + "' was not found";
                if (!ConfirmDeviceSuggestion(scope, promptPrefix, best))
                {
                    error = "Canceled device resolution for '" + token + "'.";
                    return false;
                }
            }
            else
            {
                Console.Error.WriteLine("Auto-selected '" + best.Name + "' for '" + token + "' (" + best.Source + ", --confirm false).");
            }

            resolved = best.Name;
            return true;
        }

        private static DeviceExchangeResult ExchangeDeviceReferences(string oldDevice, string newDevice)
        {
            DeviceExchangeResult result = new DeviceExchangeResult();
            result.OldDevice = (oldDevice ?? string.Empty).Trim();
            result.NewDevice = (newDevice ?? string.Empty).Trim();

            foreach (string relationName in InternalDataManagement.AllBinds.Keys.ToList())
            {
                if (!InternalDataManagement.AllBinds.TryGetValue(relationName, out Bind bind) || bind == null)
                    continue;

                bool relationChanged = false;
                if (SameDeviceInstance(bind.Joystick, result.OldDevice))
                {
                    bind.Joystick = result.NewDevice;
                    result.UpdatedBindDevices++;
                    relationChanged = true;
                }
                if (SameDeviceInstance(bind.JoyBackup, result.OldDevice))
                {
                    bind.JoyBackup = result.NewDevice;
                    result.UpdatedBindBackups++;
                    relationChanged = true;
                }
                if (SameDeviceInstance(bind.AliasJoystick, result.OldDevice))
                {
                    bind.AliasJoystick = result.NewDevice;
                    result.UpdatedBindAliases++;
                    relationChanged = true;
                }

                if (bind.AllReformers != null)
                {
                    for (int i = 0; i < bind.AllReformers.Count; i++)
                    {
                        string reformer = bind.AllReformers[i];
                        Modifier parsed = Modifier.ReformerToMod(reformer);
                        if (parsed == null || !SameDeviceInstance(parsed.device, result.OldDevice))
                            continue;

                        parsed.device = result.NewDevice;
                        bind.AllReformers[i] = parsed.toReformerString();
                        result.UpdatedBindReformers++;
                        relationChanged = true;
                    }
                }

                if (!relationChanged)
                    continue;

                InternalDataManagement.AllBinds[relationName] = bind;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = bind;
                if (!result.UpdatedRelations.Contains(relationName, StringComparer.OrdinalIgnoreCase))
                    result.UpdatedRelations.Add(relationName);
            }

            foreach (string modifierName in InternalDataManagement.AllModifiers.Keys.ToList())
            {
                if (!InternalDataManagement.AllModifiers.TryGetValue(modifierName, out Modifier modifier) || modifier == null)
                    continue;
                if (!SameDeviceInstance(modifier.device, result.OldDevice))
                    continue;

                modifier.device = result.NewDevice;
                InternalDataManagement.AllModifiers[modifierName] = modifier;
                result.UpdatedModifiers++;
            }

            result.UpdatedJoystickAliases = MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickAliases, result.OldDevice, result.NewDevice);
            result.UpdatedJoystickImages = MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickFileImages, result.OldDevice, result.NewDevice);
            result.UpdatedJoystickPGuids = MoveDeviceInstanceKeyEntries(InternalDataManagement.LocalJoystickPGUID, result.OldDevice, result.NewDevice);
            result.UpdatedJoystickFfb = MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickFFB, result.OldDevice, result.NewDevice);
            result.UpdatedJoystickFfb += MoveFfbOverrideDevice(result.OldDevice, result.NewDevice);
            result.UpdatedJoystickActivity = MoveDeviceInstanceKeyEntries(InternalDataManagement.JoystickActivity, result.OldDevice, result.NewDevice);
            result.UpdatedLocalJoysticks = ReplaceLocalJoystickInstance(result.OldDevice, result.NewDevice);

            result.UpdatedRelations = result.UpdatedRelations
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return result;
        }

        private static DeviceRemoveResult RemoveDeviceReferences(string deviceName)
        {
            DeviceRemoveResult result = new DeviceRemoveResult();
            result.Device = (deviceName ?? string.Empty).Trim();

            foreach (string relationName in InternalDataManagement.AllBinds.Keys.ToList())
            {
                if (!InternalDataManagement.AllBinds.TryGetValue(relationName, out Bind bind) || bind == null)
                    continue;

                if (DeviceNameMatches(bind.Joystick, result.Device))
                {
                    InternalDataManagement.AllBinds.Remove(relationName);
                    if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                        InternalDataManagement.AllRelations[relationName].bind = null;
                    result.RemovedBinds++;
                    if (!result.UpdatedRelations.Contains(relationName, StringComparer.OrdinalIgnoreCase))
                        result.UpdatedRelations.Add(relationName);
                    continue;
                }

                bool relationChanged = false;
                if (DeviceNameMatches(bind.JoyBackup, result.Device))
                {
                    bind.JoyBackup = string.Empty;
                    result.ClearedBindBackups++;
                    relationChanged = true;
                }
                if (DeviceNameMatches(bind.AliasJoystick, result.Device))
                {
                    bind.AliasJoystick = string.Empty;
                    result.ClearedBindAliases++;
                    relationChanged = true;
                }

                if (bind.AllReformers != null)
                {
                    for (int i = bind.AllReformers.Count - 1; i >= 0; i--)
                    {
                        string reformer = bind.AllReformers[i];
                        Modifier parsed = Modifier.ReformerToMod(reformer);
                        if (parsed == null || !DeviceNameMatches(parsed.device, result.Device))
                            continue;

                        bind.AllReformers.RemoveAt(i);
                        result.RemovedBindReformers++;
                        relationChanged = true;
                    }
                }

                if (!relationChanged)
                    continue;

                InternalDataManagement.AllBinds[relationName] = bind;
                if (InternalDataManagement.AllRelations.ContainsKey(relationName) && InternalDataManagement.AllRelations[relationName] != null)
                    InternalDataManagement.AllRelations[relationName].bind = bind;
                if (!result.UpdatedRelations.Contains(relationName, StringComparer.OrdinalIgnoreCase))
                    result.UpdatedRelations.Add(relationName);
            }

            foreach (string modifierName in InternalDataManagement.AllModifiers.Keys.ToList())
            {
                if (!InternalDataManagement.AllModifiers.TryGetValue(modifierName, out Modifier modifier) || modifier == null)
                    continue;
                if (!DeviceNameMatches(modifier.device, result.Device))
                    continue;

                InternalDataManagement.AllModifiers.Remove(modifierName);
                result.RemovedModifiers++;
            }

            result.RemovedJoystickAliases = RemoveDeviceKeyEntries(InternalDataManagement.JoystickAliases, result.Device);
            result.RemovedJoystickImages = RemoveDeviceKeyEntries(InternalDataManagement.JoystickFileImages, result.Device);
            result.RemovedJoystickPGuids = RemoveDeviceKeyEntries(InternalDataManagement.LocalJoystickPGUID, result.Device);
            result.RemovedJoystickFfb = RemoveDeviceKeyEntries(InternalDataManagement.JoystickFFB, result.Device);
            result.RemovedJoystickFfb += MoveFfbOverrideDevice(result.Device, null);
            result.RemovedJoystickActivity = RemoveDeviceKeyEntries(InternalDataManagement.JoystickActivity, result.Device);
            result.RemovedLocalJoysticks = RemoveLocalJoystickEntries(result.Device);

            result.UpdatedRelations = result.UpdatedRelations
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
            return result;
        }

        private static int MoveDeviceKeyEntries<T>(
            IDictionary<string, T> map,
            string oldDevice,
            string newDevice)
        {
            if (map == null)
                return 0;

            List<string> keys = map.Keys
                .Where(key => DeviceNameMatches(key, oldDevice))
                .ToList();
            if (keys.Count == 0)
                return 0;

            int moved = 0;
            bool newExists = map.Keys.Any(key => string.Equals(key, newDevice, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (DeviceNameMatches(key, newDevice))
                    continue;

                T value = map[key];
                map.Remove(key);
                moved++;

                if (!newExists)
                {
                    map[newDevice] = value;
                    newExists = true;
                }
            }

            return moved;
        }

        // Device exchange works on one device instance: "Name {GUID}" must match including the GUID, so other devices
        // of the same model (and the new instance itself) keep their entries. Names without a GUID match by name.
        private static bool SameDeviceInstance(string left, string right)
        {
            string a = CollapseSpaces(left);
            string b = CollapseSpaces(right);
            if (a.Length == 0 || b.Length == 0)
                return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            bool aHasGuid = !string.Equals(StripDeviceGuidSuffix(a), a, StringComparison.Ordinal);
            bool bHasGuid = !string.Equals(StripDeviceGuidSuffix(b), b, StringComparison.Ordinal);
            return !(aHasGuid && bHasGuid) && DeviceNameMatches(a, b);
        }

        private static string CollapseSpaces(string value)
        {
            return System.Text.RegularExpressions.Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private static int MoveDeviceInstanceKeyEntries<T>(IDictionary<string, T> map, string oldDevice, string newDevice)
        {
            if (map == null)
                return 0;
            List<string> keys = map.Keys.Where(key => SameDeviceInstance(key, oldDevice) && !SameDeviceInstance(key, newDevice)).ToList();
            bool newExists = map.Keys.Any(key => SameDeviceInstance(key, newDevice));
            foreach (string key in keys)
            {
                T value = map[key];
                map.Remove(key);
                if (!newExists)
                {
                    map[newDevice] = value;
                    newExists = true;
                }
            }
            return keys.Count;
        }

        private static int ReplaceLocalJoystickInstance(string oldDevice, string newDevice)
        {
            string[] source = InternalDataManagement.LocalJoysticks ?? new string[0];
            int updated = source.Count(item => SameDeviceInstance(item, oldDevice));
            if (updated == 0)
                return 0;
            InternalDataManagement.LocalJoysticks = source
                .Select(item => SameDeviceInstance(item, oldDevice) ? (newDevice ?? string.Empty).Trim() : (item ?? string.Empty).Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return updated;
        }

        // FFB settings are keyed "<device>|<scope>": moves (or with newDevice null removes) one device's entries.
        private static int MoveFfbOverrideDevice(string oldDevice, string newDevice)
        {
            if (InternalDataManagement.FfbOverrides == null || string.IsNullOrWhiteSpace(oldDevice))
                return 0;
            int count = 0;
            foreach (string key in InternalDataManagement.FfbOverrides.Keys.ToList())
            {
                int bar = key.IndexOf('|');
                if (bar <= 0 || !string.Equals(key.Substring(0, bar), oldDevice.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = InternalDataManagement.FfbOverrides[key];
                InternalDataManagement.FfbOverrides.Remove(key);
                if (!string.IsNullOrWhiteSpace(newDevice))
                    InternalDataManagement.FfbOverrides[newDevice.Trim() + key.Substring(bar)] = value;
                count++;
            }
            return count;
        }

        private static int RemoveDeviceKeyEntries<T>(IDictionary<string, T> map, string deviceName)
        {
            if (map == null)
                return 0;

            List<string> keys = map.Keys
                .Where(key => DeviceNameMatches(key, deviceName))
                .ToList();
            for (int i = 0; i < keys.Count; i++)
                map.Remove(keys[i]);
            return keys.Count;
        }

        private static int ReplaceLocalJoystickEntries(string oldDevice, string newDevice)
        {
            string[] source = InternalDataManagement.LocalJoysticks ?? new string[0];
            if (source.Length == 0)
                return 0;

            int updated = 0;
            List<string> values = new List<string>();
            for (int i = 0; i < source.Length; i++)
            {
                string item = (source[i] ?? string.Empty).Trim();
                if (item.Length == 0)
                    continue;

                if (DeviceNameMatches(item, oldDevice))
                {
                    values.Add((newDevice ?? string.Empty).Trim());
                    updated++;
                }
                else
                {
                    values.Add(item);
                }
            }

            InternalDataManagement.LocalJoysticks = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return updated;
        }

        private static int RemoveLocalJoystickEntries(string deviceName)
        {
            string[] source = InternalDataManagement.LocalJoysticks ?? new string[0];
            if (source.Length == 0)
                return 0;

            int removed = 0;
            List<string> values = new List<string>();
            for (int i = 0; i < source.Length; i++)
            {
                string item = (source[i] ?? string.Empty).Trim();
                if (item.Length == 0)
                    continue;
                if (DeviceNameMatches(item, deviceName))
                {
                    removed++;
                    continue;
                }
                values.Add(item);
            }

            InternalDataManagement.LocalJoysticks = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return removed;
        }

        private static bool DeviceNameMatches(string left, string right)
        {
            string a = (left ?? string.Empty).Trim();
            string b = (right ?? string.Empty).Trim();
            if (a.Length == 0 || b.Length == 0)
                return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            string aBase = StripDeviceGuidSuffix(a);
            string bBase = StripDeviceGuidSuffix(b);
            if (aBase.Length == 0 || bBase.Length == 0)
                return false;
            return string.Equals(aBase, bBase, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> DiscoverDcsInputDevices(
            string savedGamesPath,
            List<string> warnings)
        {
            string normalizedRoot = NormalizePath(savedGamesPath);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
                return new List<string>();

            string cacheKey = normalizedRoot;
            if (TryGetDcsInputDeviceCache(cacheKey, out List<string> cached))
                return cached;

            List<string> localWarnings = new List<string>();
            List<string> discovered = DiscoverDcsInputDevicesLegacy(normalizedRoot, localWarnings);
            CacheDcsInputDevices(cacheKey, discovered);

            if (warnings != null)
            {
                for (int i = 0; i < localWarnings.Count; i++)
                    AddUnique(warnings, localWarnings[i]);
            }
            return discovered;
        }

        private static bool TryGetDcsInputDeviceCache(string key, out List<string> devices)
        {
            lock (DcsInputDeviceCacheLock)
            {
                if (string.IsNullOrWhiteSpace(key) || !DcsInputDeviceCache.TryGetValue(key, out List<string> cached))
                {
                    devices = null;
                    return false;
                }

                devices = new List<string>(cached);
                return true;
            }
        }

        private static void CacheDcsInputDevices(string key, List<string> devices)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            List<string> payload = (devices ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (DcsInputDeviceCacheLock)
            {
                DcsInputDeviceCache[key] = payload;
            }
        }

        private static List<string> DiscoverDcsInputDevicesLegacy(string savedGamesPath, List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(savedGamesPath))
                return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            string inputRoot = Path.Combine(savedGamesPath, "Config", "Input");
            if (!Directory.Exists(inputRoot))
            {
                AddUnique(warnings, "DCS device lookup: input folder not found at '" + inputRoot + "'.");
                return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(inputRoot, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                AddUnique(warnings, "DCS device lookup failed: " + ex.Message);
                return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            }

            for (int i = 0; i < files.Length; i++)
                AddDcsInputDeviceFromFilePath(output, files[i]);

            return output.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddDcsInputDeviceFromFilePath(HashSet<string> output, string file)
        {
            if (output == null || string.IsNullOrWhiteSpace(file))
                return;

            string lower = file.ToLowerInvariant();
            if (!lower.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase) &&
                !lower.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string folder = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
            if (!string.Equals(folder, "joystick", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(folder, "keyboard", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string fileName = Path.GetFileName(file) ?? string.Empty;
            string device = string.Empty;
            if (fileName.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                device = fileName.Substring(0, fileName.Length - ".diff.lua".Length);
            else if (fileName.EndsWith(".jp", StringComparison.OrdinalIgnoreCase))
                device = fileName.Substring(0, fileName.Length - ".jp".Length);

            device = device.Trim();
            if (device.Length > 0)
                output.Add(device);
        }

        private static List<string> DiscoverIl2InputDevices(string il2RootPath, List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(il2RootPath))
                return output.OrderBy(v => v).ToList();

            string devicesFile = Path.Combine(il2RootPath, "data", "input", "devices.txt");
            if (!File.Exists(devicesFile))
            {
                warnings?.Add("IL2 device lookup: devices file not found at '" + devicesFile + "'.");
                return output.OrderBy(v => v).ToList();
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(devicesFile);
            }
            catch (Exception ex)
            {
                warnings?.Add("IL2 device lookup failed: " + ex.Message);
                return output.OrderBy(v => v).ToList();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = (lines[i] ?? string.Empty).Trim().TrimEnd('|').Trim();
                if (line.Length == 0 || line.StartsWith("configId", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length < 3)
                    continue;

                string guid = parts[1].Trim().Replace("%22", string.Empty).Replace("\"", string.Empty);
                string model = parts[2].Trim();
                string converted = ConvertIl2DeviceToDcsName(guid, model);
                if (!string.IsNullOrWhiteSpace(converted))
                    output.Add(converted);
            }

            return output.OrderBy(v => v).ToList();
        }

        private static string ConvertIl2DeviceToDcsName(string guid, string model)
        {
            string cleanGuid = (guid ?? string.Empty).Trim().Replace("%22", string.Empty).Replace("\"", string.Empty);
            string cleanModel = Uri.UnescapeDataString((model ?? string.Empty).Trim()).Trim();
            if (cleanModel.Length == 0)
                return string.Empty;

            string[] parts = cleanGuid.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return cleanModel;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(cleanModel);
            sb.Append(" {");
            sb.Append(parts[0].ToUpperInvariant());
            for (int i = 1; i < parts.Length; i++)
            {
                sb.Append("-");
                sb.Append(i == 2 ? parts[i].ToLowerInvariant() : parts[i].ToUpperInvariant());
            }
            sb.Append("}");
            return sb.ToString();
        }

        private static bool TryResolveDeviceFilters(
            List<string> requestedFilters,
            List<DeviceCandidate> candidates,
            bool confirmSuggestions,
            string scope,
            out List<string> resolvedFilters,
            out string error)
        {
            resolvedFilters = new List<string>();
            error = string.Empty;

            List<string> requested = (requestedFilters ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (requested.Count == 0 || candidates == null || candidates.Count == 0)
            {
                resolvedFilters = requested;
                return true;
            }

            foreach (string token in requested)
            {
                DeviceCandidate fullMatch = candidates.FirstOrDefault(c =>
                    string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
                if (fullMatch != null)
                {
                    resolvedFilters.Add(fullMatch.Name);
                    continue;
                }

                string tokenBase = StripDeviceGuidSuffix(token);
                List<DeviceCandidate> baseMatches = candidates
                    .Where(c => string.Equals(StripDeviceGuidSuffix(c.Name), tokenBase, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (baseMatches.Count == 1)
                {
                    resolvedFilters.Add(baseMatches[0].Name);
                    continue;
                }

                DeviceCandidate best = ChooseBestDeviceCandidate(token, baseMatches.Count > 1 ? baseMatches : candidates);
                if (best == null)
                {
                    error = "No device matched '" + token + "' for " + scope + " import.";
                    return false;
                }

                if (confirmSuggestions)
                {
                    string promptPrefix = baseMatches.Count > 1
                        ? "Multiple devices matched '" + token + "'"
                        : "Device '" + token + "' was not found";
                    if (!ConfirmDeviceSuggestion(scope, promptPrefix, best))
                    {
                        error = "Canceled device resolution for '" + token + "'.";
                        return false;
                    }
                }
                else
                {
                    Console.Error.WriteLine("Auto-selected '" + best.Name + "' for '" + token + "' (" + best.Source + ", --confirm false).");
                }

                resolvedFilters.Add(best.Name);
            }

            resolvedFilters = resolvedFilters
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        private static bool ConfirmDeviceSuggestion(string scope, string prefix, DeviceCandidate candidate)
        {
            if (!Environment.UserInteractive || Console.IsInputRedirected)
            {
                // Same outcome as --confirm false: callers without a console (the GUI) cannot answer prompts.
                Console.Error.WriteLine(prefix + " for " + scope + ". Auto-selected '" + candidate.Name + "' (" + candidate.Source + ", non-interactive).");
                return true;
            }

            Console.Error.Write(prefix + " for " + scope + ". Use '" + candidate.Name + "' (" + candidate.Source + ")? [y/N]: ");
            string answer = Console.ReadLine();
            return IsAffirmativeAnswer(answer);
        }

        private static bool ConfirmLoadDeviceRemap(string missingDevice, string replacementDevice, string source)
        {
            string missing = (missingDevice ?? string.Empty).Trim();
            string replacement = (replacementDevice ?? string.Empty).Trim();
            string origin = string.IsNullOrWhiteSpace(source) ? "candidate" : source.Trim();
            if (missing.Length == 0 || replacement.Length == 0)
                return false;
            if (!Environment.UserInteractive || Console.IsInputRedirected)
                return false;

            Console.Error.Write("Load correction: replace missing device '" + missing + "' with '" + replacement + "' (" + origin + ")? [y/N]: ");
            string answer = Console.ReadLine();
            return IsAffirmativeAnswer(answer);
        }

        private static bool ConfirmLoadItemCorrection(InternalDataManagement.LoadCorrectionSuggestion suggestion)
        {
            if (suggestion == null)
                return false;
            if (!Environment.UserInteractive || Console.IsInputRedirected)
                return false;

            string relation = (suggestion.RelationName ?? string.Empty).Trim();
            if (relation.Length == 0)
                relation = "<unnamed>";
            string game = (suggestion.Game ?? string.Empty).Trim();
            if (game.Length == 0)
                game = "DCS";

            Console.Error.WriteLine(
                "Load correction suggestion [" + game + "] relation '" + relation + "': '" +
                (suggestion.OldTitle ?? string.Empty) + "' (" + (suggestion.OldPlane ?? string.Empty) +
                ") -> '" + (suggestion.NewTitle ?? string.Empty) + "' (" + (suggestion.NewPlane ?? string.Empty) +
                "), score=" + suggestion.Score + ", confidence=" + (suggestion.Confidence ?? string.Empty) + ".");
            if (!string.IsNullOrWhiteSpace(suggestion.Reason))
                Console.Error.WriteLine("Reason: " + suggestion.Reason);
            Console.Error.Write("Apply this correction? [y/N]: ");
            string answer = Console.ReadLine();
            return IsAffirmativeAnswer(answer);
        }

        private static bool TryParseLoadCorrectionMode(
            string raw,
            out InternalDataManagement.LoadItemCorrectionMode mode,
            out string error)
        {
            mode = InternalDataManagement.LoadItemCorrectionMode.Balanced;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            string value = raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "strict":
                    mode = InternalDataManagement.LoadItemCorrectionMode.Strict;
                    return true;
                case "balanced":
                    mode = InternalDataManagement.LoadItemCorrectionMode.Balanced;
                    return true;
                case "aggressive":
                    mode = InternalDataManagement.LoadItemCorrectionMode.Aggressive;
                    return true;
                default:
                    error = "Invalid value for --correction-mode: '" + raw + "'. Use strict|balanced|aggressive.";
                    return false;
            }
        }

        private static bool TryParseCorrectionAutoApplyThreshold(
            string raw,
            out int? threshold,
            out string error)
        {
            threshold = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return true;

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                error = "Invalid value for --correction-auto-threshold: '" + raw + "'. Use integer 0..200.";
                return false;
            }

            if (parsed < 0 || parsed > 200)
            {
                error = "Invalid value for --correction-auto-threshold: '" + raw + "'. Use integer 0..200.";
                return false;
            }

            threshold = parsed;
            return true;
        }

        private static void WriteLoadCorrectionReportIfRequested()
        {
            if (string.IsNullOrWhiteSpace(LoadCorrectionReportPath))
                return;

            InternalDataManagement.LoadCorrectionReport report = InternalDataManagement.GetLastLoadCorrectionReport();
            string fullPath = NormalizePath(LoadCorrectionReportPath);
            if (string.IsNullOrWhiteSpace(fullPath))
                return;

            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string extension = Path.GetExtension(fullPath) ?? string.Empty;
            bool text = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".log", StringComparison.OrdinalIgnoreCase);
            string payload = text
                ? FormatLoadCorrectionReportText(report)
                : EngineSerialization.ToJson(report);
            File.WriteAllText(fullPath, payload);
        }

        private static string FormatLoadCorrectionReportText(InternalDataManagement.LoadCorrectionReport report)
        {
            InternalDataManagement.LoadCorrectionReport payload = report ?? new InternalDataManagement.LoadCorrectionReport();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Load Correction Report");
            sb.AppendLine("Run UTC: " + payload.RunUtc.ToString("o", CultureInfo.InvariantCulture));
            sb.AppendLine("Corrections Enabled: " + payload.CorrectionsEnabled);
            sb.AppendLine("Correction Mode: " + (payload.ItemCorrectionMode ?? string.Empty));
            sb.AppendLine("Auto Apply Score Threshold: " + (payload.ItemCorrectionAutoApplyScoreThreshold.HasValue
                ? payload.ItemCorrectionAutoApplyScoreThreshold.Value.ToString(CultureInfo.InvariantCulture)
                : "<mode-default>"));
            sb.AppendLine("Confirm Item Corrections: " + payload.ConfirmItemCorrections);
            sb.AppendLine("Auto Add DB Items: " + payload.AutoAddDbItems);
            sb.AppendLine("Remap Missing Devices: " + payload.RemapMissingDevices);
            sb.AppendLine("Confirm Device Remap: " + payload.ConfirmDeviceRemap);
            sb.AppendLine("Entries: " + payload.TotalEntries);
            sb.AppendLine("Applied: " + payload.AppliedEntries);
            sb.AppendLine("Skipped: " + payload.SkippedEntries);
            sb.AppendLine();

            List<InternalDataManagement.LoadCorrectionReportEntry> entries =
                payload.Entries ?? new List<InternalDataManagement.LoadCorrectionReportEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                InternalDataManagement.LoadCorrectionReportEntry entry = entries[i];
                if (entry == null)
                    continue;
                sb.AppendLine(
                    "[" + i + "] " +
                    (entry.Kind ?? string.Empty) +
                    " | action=" + (entry.Action ?? string.Empty) +
                    " | game=" + (entry.Game ?? string.Empty) +
                    " | relation=" + (entry.RelationName ?? string.Empty) +
                    " | confidence=" + (entry.Confidence ?? string.Empty) +
                    " | score=" + entry.Score);
                sb.AppendLine(
                    "      old: id=" + (entry.OldId ?? string.Empty) +
                    ", title=" + (entry.OldTitle ?? string.Empty) +
                    ", plane=" + (entry.OldPlane ?? string.Empty));
                sb.AppendLine(
                    "      new: id=" + (entry.NewId ?? string.Empty) +
                    ", title=" + (entry.NewTitle ?? string.Empty) +
                    ", plane=" + (entry.NewPlane ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(entry.Reason))
                    sb.AppendLine("      reason: " + entry.Reason);
                if (!string.IsNullOrWhiteSpace(entry.Details))
                    sb.AppendLine("      details: " + entry.Details);
            }

            return sb.ToString();
        }

        private static bool IsAffirmativeAnswer(string answer)
        {
            string normalized = (answer ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "y" ||
                   normalized == "yes" ||
                   normalized == "true" ||
                   normalized == "1";
        }

        private static DeviceCandidate ChooseBestDeviceCandidate(string requested, IEnumerable<DeviceCandidate> candidates)
        {
            if (candidates == null)
                return null;

            return candidates
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                .OrderByDescending(c => DeviceSimilarityScore(requested, c.Name))
                .ThenBy(c => c.Priority)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int DeviceSimilarityScore(string requested, string candidate)
        {
            string reqFull = NormalizeDeviceTokenForSimilarity(requested);
            string reqBase = NormalizeDeviceTokenForSimilarity(StripDeviceGuidSuffix(requested));
            string candFull = NormalizeDeviceTokenForSimilarity(candidate);
            string candBase = NormalizeDeviceTokenForSimilarity(StripDeviceGuidSuffix(candidate));
            if (reqFull.Length == 0 || reqBase.Length == 0 || candFull.Length == 0 || candBase.Length == 0)
                return int.MinValue;

            int score = 0;
            if (string.Equals(reqFull, candFull, StringComparison.OrdinalIgnoreCase))
                score += 100000;
            if (string.Equals(reqBase, candBase, StringComparison.OrdinalIgnoreCase))
                score += 50000;

            if (candBase.Contains(reqBase) || reqBase.Contains(candBase))
                score += 12000 - Math.Abs(candBase.Length - reqBase.Length);

            int distBase = LevenshteinDistance(reqBase, candBase);
            int distFull = LevenshteinDistance(reqFull, candFull);
            score += Math.Max(0, 10000 - (distBase * 250));
            score += Math.Max(0, 2500 - (distFull * 50));
            return score;
        }

        private static string NormalizeDeviceTokenForSimilarity(string value)
        {
            string raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
                return string.Empty;

            char[] filtered = raw
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            if (filtered.Length == 0)
                return raw.ToUpperInvariant();
            return new string(filtered);
        }

        private static string StripDeviceGuidSuffix(string value)
        {
            string token = (value ?? string.Empty).Trim();
            int split = token.LastIndexOf(" {", StringComparison.Ordinal);
            if (split > 0 && token.EndsWith("}", StringComparison.Ordinal))
                return token.Substring(0, split).Trim();
            return token;
        }

        private static int LevenshteinDistance(string left, string right)
        {
            string a = left ?? string.Empty;
            string b = right ?? string.Empty;
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int deletion = previous[j] + 1;
                    int insertion = current[j - 1] + 1;
                    int substitution = previous[j - 1] + cost;
                    current[j] = Math.Min(Math.Min(deletion, insertion), substitution);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        private static string NormalizeInputForFormat(string input, string format, out string error)
        {
            error = string.Empty;
            string value = (input ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                error = "Input value is empty.";
                return null;
            }

            if (string.Equals(format, "dcs", StringComparison.OrdinalIgnoreCase))
                return value;

            if (string.Equals(format, "il2", StringComparison.OrdinalIgnoreCase))
            {
                if (TryConvertIl2TokenToDcsInput(value, out string converted, out string convertError))
                    return converted;

                error = "Could not convert IL2 input '" + value + "' to DCS format" +
                        (string.IsNullOrWhiteSpace(convertError) ? "." : ": " + convertError);
                return null;
            }

            error = "Unsupported format '" + format + "'. Use dcs or il2.";
            return null;
        }

        private static bool TryConvertIl2TokenToDcsInput(string raw, out string input, out string error)
        {
            input = string.Empty;
            error = string.Empty;

            string token = (raw ?? string.Empty).Trim();
            if (token.Length == 0)
            {
                error = "empty token";
                return false;
            }

            if (token.StartsWith("joy", StringComparison.OrdinalIgnoreCase))
            {
                int split = token.IndexOf('_');
                if (split <= 3)
                {
                    error = "invalid joystick token";
                    return false;
                }
                token = token.Substring(split + 1);
            }

            if (token.StartsWith("key_", StringComparison.OrdinalIgnoreCase))
            {
                string key = token.Substring(4).Trim();
                if (key.Length == 0)
                {
                    error = "empty key token";
                    return false;
                }

                input = key.Length == 1
                    ? key.ToUpperInvariant()
                    : char.ToUpperInvariant(key[0]) + key.Substring(1);
                return true;
            }

            if (token.StartsWith("axis_", StringComparison.OrdinalIgnoreCase))
            {
                switch (token.Substring(5).Trim().ToLowerInvariant())
                {
                    case "x": input = "JOY_X"; return true;
                    case "y": input = "JOY_Y"; return true;
                    case "z": input = "JOY_Z"; return true;
                    case "w": input = "JOY_RX"; return true;
                    case "s": input = "JOY_RY"; return true;
                    case "t": input = "JOY_RZ"; return true;
                    case "p": input = "JOY_SLIDER1"; return true;
                    case "q": input = "JOY_SLIDER2"; return true;
                    default:
                        error = "unsupported axis token";
                        return false;
                }
            }

            if (string.Equals(token, "b63_povnu", StringComparison.OrdinalIgnoreCase))
            {
                input = "JOY_BTN64";
                return true;
            }

            if (token.StartsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                string digits = new string(token.Skip(1).TakeWhile(char.IsDigit).ToArray());
                if (digits.Length == 0)
                {
                    error = "invalid button token";
                    return false;
                }

                if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int button))
                {
                    error = "invalid button number";
                    return false;
                }

                input = "JOY_BTN" + (button + 1).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (token.StartsWith("pov0_", StringComparison.OrdinalIgnoreCase))
            {
                string angle = token.Substring("pov0_".Length).Trim();
                string dir = PovAngleToDirection(angle);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    error = "unsupported POV angle";
                    return false;
                }

                input = "JOY_BTN_POV0_" + dir;
                return true;
            }

            if (token.StartsWith("pov", StringComparison.OrdinalIgnoreCase))
            {
                int split = token.IndexOf('_');
                if (split < 0)
                {
                    error = "invalid POV token";
                    return false;
                }

                string headRaw = token.Substring(3, split - 3);
                string angleRaw = token.Substring(split + 1);
                if (!int.TryParse(headRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int povHead))
                {
                    error = "invalid POV head";
                    return false;
                }

                int add = PovAngleToAddition(angleRaw);
                if (add < 0)
                {
                    error = "unsupported POV angle";
                    return false;
                }

                int finalButton = (povHead - 1) * 8 + 80 + add;
                input = "JOY_BTN" + finalButton.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            error = "unsupported token";
            return false;
        }

        private static string PovAngleToDirection(string angleRaw)
        {
            switch ((angleRaw ?? string.Empty).Trim())
            {
                case "0": return "U";
                case "45": return "UR";
                case "90": return "R";
                case "135": return "DR";
                case "180": return "D";
                case "225": return "DL";
                case "270": return "L";
                case "315": return "UL";
                default: return null;
            }
        }

        private static int PovAngleToAddition(string angleRaw)
        {
            switch ((angleRaw ?? string.Empty).Trim())
            {
                case "0": return 0;
                case "45": return 1;
                case "90": return 2;
                case "135": return 3;
                case "180": return 4;
                case "225": return 5;
                case "270": return 6;
                case "315": return 7;
                default: return -1;
            }
        }

        private static string ToText(DcsFfbListResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Force Feedback");
            sb.AppendLine("Devices: " + result.Devices.Count);
            sb.AppendLine("Connected Only: " + result.ConnectedOnly);
            sb.AppendLine("Profile Only: " + result.ProfileOnly);
            for (int i = 0; i < result.Devices.Count; i++)
            {
                DcsFfbEntry entry = result.Devices[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Device))
                    continue;
                sb.AppendLine(
                    "  - " + entry.Device +
                    " | invertX=" + entry.InvertX.ToString().ToLowerInvariant() +
                    ", invertY=" + entry.InvertY.ToString().ToLowerInvariant() +
                    ", swapAxis=" + entry.SwapAxis.ToString().ToLowerInvariant() +
                    ", hasSetting=" + entry.HasSetting.ToString().ToLowerInvariant() +
                    ", connected=" + entry.Connected.ToString().ToLowerInvariant() +
                    ", profile=" + entry.Profile.ToString().ToLowerInvariant());
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DcsFfbMutationResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Force Feedback Update");
            sb.AppendLine("Action: " + result.Action);
            sb.AppendLine("All: " + result.All);
            sb.AppendLine("Devices: " + result.Devices.Count);
            for (int i = 0; i < result.Devices.Count; i++)
                sb.AppendLine("  - " + result.Devices[i]);
            sb.AppendLine("Updated: " + result.Updated);
            sb.AppendLine("Removed: " + result.Removed);

            if (result.InvertXProvided)
                sb.AppendLine("Invert X: " + result.InvertX.ToString().ToLowerInvariant());
            if (result.InvertYProvided)
                sb.AppendLine("Invert Y: " + result.InvertY.ToString().ToLowerInvariant());
            if (result.SwapAxisProvided)
                sb.AppendLine("Swap Axis: " + result.SwapAxis.ToString().ToLowerInvariant());

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DcsStickDefaultsResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Stick Defaults");
            sb.AppendLine("Action: " + result.Action);
            sb.AppendLine("Saved Games Path: " + (string.IsNullOrWhiteSpace(result.SavedGamesPath) ? "<none>" : result.SavedGamesPath));
            sb.AppendLine("Install Path: " + (string.IsNullOrWhiteSpace(result.InstallPath) ? "<none>" : result.InstallPath));
            sb.AppendLine("Aircraft Root: " + (string.IsNullOrWhiteSpace(result.AircraftRoot) ? "<none>" : result.AircraftRoot));
            sb.AppendLine("Backup Path: " + (string.IsNullOrWhiteSpace(result.BackupPath) ? "<none>" : result.BackupPath));
            sb.AppendLine("Aircraft Scanned: " + result.AircraftScanned);
            sb.AppendLine("Joystick Folders Scanned: " + result.JoystickFoldersScanned);
            sb.AppendLine("Default Files Copied: " + result.DefaultFilesCopied);
            sb.AppendLine("Stick-Specific Files Moved: " + result.StickSpecificFilesMoved);
            sb.AppendLine("Files Restored: " + result.FilesRestored);
            sb.AppendLine("Directories Touched: " + result.DirectoriesTouched);

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            if (result.Errors != null && result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DcsClickPreviewResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Refresh Click Preview");
            sb.AppendLine("Action: " + result.Action);
            sb.AppendLine("Use OCR: " + result.UseOcr.ToString().ToLowerInvariant());
            sb.AppendLine("Saved Games Path: " + (string.IsNullOrWhiteSpace(result.SavedGamesPath) ? "<none>" : result.SavedGamesPath));
            sb.AppendLine("Install Path: " + (string.IsNullOrWhiteSpace(result.InstallPath) ? "<none>" : result.InstallPath));
            sb.AppendLine("Clicks XML Path: " + (string.IsNullOrWhiteSpace(result.ClicksXmlPath) ? "<none>" : result.ClicksXmlPath));
            sb.AppendLine("Clicks Image Folder: " + (string.IsNullOrWhiteSpace(result.ClicksImageFolder) ? "<none>" : result.ClicksImageFolder));
            sb.AppendLine("GUI Scale: " + result.GuiScale.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("Used Window Override: " + result.UsedWindowOverride.ToString().ToLowerInvariant());
            sb.AppendLine("Used Running DCS Window: " + result.UsedRunningDcsProcess.ToString().ToLowerInvariant());
            if (result.Window != null)
            {
                sb.AppendLine(
                    "Window: left=" + result.Window.Left +
                    ", top=" + result.Window.Top +
                    ", width=" + result.Window.Width +
                    ", height=" + result.Window.Height);
            }
            else
            {
                sb.AppendLine("Window: <none>");
            }

            sb.AppendLine("Clicks: " + result.Clicks.Count);
            for (int i = 0; i < result.Clicks.Count; i++)
            {
                DcsResolvedClick click = result.Clicks[i];
                if (click == null)
                    continue;

                string line =
                    "  - " + click.Name +
                    " | method=" + (string.IsNullOrWhiteSpace(click.Method) ? "<none>" : click.Method) +
                    ", found=" + click.Found.ToString().ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(click.Anchor))
                    line += ", anchor=" + click.Anchor;
                if (click.Method == "xml")
                    line += ", offset=(" + click.OffsetX + "," + click.OffsetY + ")";
                if (!string.IsNullOrWhiteSpace(click.Template))
                    line += ", template=" + click.Template;
                if (click.Found)
                    line += ", xy=(" + click.X + "," + click.Y + ")";
                if (click.MatchWidth > 0 && click.MatchHeight > 0)
                {
                    line += ", match=(" +
                        click.MatchLeft + "," +
                        click.MatchTop + "," +
                        click.MatchWidth + "x" +
                        click.MatchHeight + ")";
                }

                sb.AppendLine(line);
            }

            sb.AppendLine("Preview Image: " + (string.IsNullOrWhiteSpace(result.PreviewImagePath) ? "<none>" : result.PreviewImagePath));

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            if (result.Errors != null && result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DcsRefreshResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Refresh");
            sb.AppendLine("Action: " + result.Action);
            sb.AppendLine("Use OCR: " + result.UseOcr.ToString().ToLowerInvariant());
            sb.AppendLine("Dry Run: " + result.DryRun.ToString().ToLowerInvariant());
            sb.AppendLine("Launch DCS: " + result.LaunchDcs.ToString().ToLowerInvariant());
            sb.AppendLine("Keep DCS Running: " + result.KeepDcsRunning.ToString().ToLowerInvariant());
            sb.AppendLine("Headless Lua: " + result.UsedHeadlessLuaRefresh.ToString().ToLowerInvariant());
            sb.AppendLine("Confirm: " + result.Confirm.ToString().ToLowerInvariant());
            sb.AppendLine("Preview Clicks: " + result.PreviewClicks.ToString().ToLowerInvariant());
            sb.AppendLine("Saved Games Path: " + (string.IsNullOrWhiteSpace(result.SavedGamesPath) ? "<none>" : result.SavedGamesPath));
            sb.AppendLine("Install Path: " + (string.IsNullOrWhiteSpace(result.InstallPath) ? "<none>" : result.InstallPath));
            sb.AppendLine("DCS Executable: " + (string.IsNullOrWhiteSpace(result.DcsExecutablePath) ? "<none>" : result.DcsExecutablePath));
            sb.AppendLine("Clicks XML Path: " + (string.IsNullOrWhiteSpace(result.ClicksXmlPath) ? "<none>" : result.ClicksXmlPath));
            sb.AppendLine("Clicks Image Folder: " + (string.IsNullOrWhiteSpace(result.ClicksImageFolder) ? "<none>" : result.ClicksImageFolder));
            sb.AppendLine("Input Path: " + (string.IsNullOrWhiteSpace(result.InputPath) ? "<none>" : result.InputPath));
            sb.AppendLine("Input Backup Path: " + (string.IsNullOrWhiteSpace(result.InputBackupPath) ? "<none>" : result.InputBackupPath));
            sb.AppendLine("Input Layouts Path: " + (string.IsNullOrWhiteSpace(result.InputLayoutsPath) ? "<none>" : result.InputLayoutsPath));
            sb.AppendLine("Default Stick Reference: " + (string.IsNullOrWhiteSpace(result.DefaultStickReference) ? "<none>" : result.DefaultStickReference));
            sb.AppendLine("GUI Scale: " + result.GuiScale.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("Used Window Override: " + result.UsedWindowOverride.ToString().ToLowerInvariant());
            sb.AppendLine("Used Running DCS Window: " + result.UsedRunningDcsProcess.ToString().ToLowerInvariant());
            sb.AppendLine("Started DCS: " + result.StartedDcs.ToString().ToLowerInvariant());
            if (result.Window != null)
            {
                sb.AppendLine(
                    "Window: left=" + result.Window.Left +
                    ", top=" + result.Window.Top +
                    ", width=" + result.Window.Width +
                    ", height=" + result.Window.Height);
            }
            else
            {
                sb.AppendLine("Window: <none>");
            }

            sb.AppendLine("Menu Wait Seconds: " + result.MenuWaitSeconds);
            sb.AppendLine("OCR Menu Timeout Seconds: " + result.OcrMenuTimeoutSeconds);
            sb.AppendLine("Settle Seconds: " + result.SettleSeconds);
            sb.AppendLine("Modules To Scan Requested: " + result.ModulesToScanRequested);
            sb.AppendLine("Modules To Scan Resolved: " + result.ModulesToScanResolved);
            sb.AppendLine("Requested Planes: " + (result.RequestedPlanes == null || result.RequestedPlanes.Count == 0
                ? "<all>"
                : string.Join(", ", result.RequestedPlanes)));
            sb.AppendLine("Matched Planes: " + (result.MatchedPlanes == null || result.MatchedPlanes.Count == 0
                ? "<none>"
                : string.Join(", ", result.MatchedPlanes)));
            sb.AppendLine("Clicks Planned: " + result.ClicksPlanned);
            sb.AppendLine("Clicks Executed: " + result.ClicksExecuted);
            sb.AppendLine("Planes Scanned: " + result.PlanesScanned);
            sb.AppendLine("HTML Files Copied: " + result.HtmlFilesCopied);
            sb.AppendLine("Clean Files Written: " + result.CleanFilesWritten);
            sb.AppendLine("Keyboard Clean Files Written: " + result.KeyboardCleanFilesWritten);
            sb.AppendLine("Device Clean Files Written: " + result.DeviceCleanFilesWritten);
            sb.AppendLine("Input Files Backed Up: " + result.InputFilesBackedUp);
            sb.AppendLine("Input Files Restored: " + result.InputFilesRestored);
            sb.AppendLine("Preview Image: " + (string.IsNullOrWhiteSpace(result.PreviewImagePath) ? "<none>" : result.PreviewImagePath));

            if (result.Clicks != null && result.Clicks.Count > 0)
            {
                sb.AppendLine("Clicks:");
                for (int i = 0; i < result.Clicks.Count; i++)
                {
                    DcsResolvedClick click = result.Clicks[i];
                    if (click == null)
                        continue;

                    string line =
                        "  - " + click.Name +
                        " | method=" + (string.IsNullOrWhiteSpace(click.Method) ? "<none>" : click.Method) +
                        ", found=" + click.Found.ToString().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(click.Anchor))
                        line += ", anchor=" + click.Anchor;
                    if (click.Method == "xml")
                        line += ", offset=(" + click.OffsetX + "," + click.OffsetY + ")";
                    if (!string.IsNullOrWhiteSpace(click.Template))
                        line += ", template=" + click.Template;
                    if (click.Found)
                        line += ", xy=(" + click.X + "," + click.Y + ")";
                    sb.AppendLine(line);
                }
            }

            if (result.Steps != null && result.Steps.Count > 0)
            {
                sb.AppendLine("Steps:");
                for (int i = 0; i < result.Steps.Count; i++)
                    sb.AppendLine("  - " + result.Steps[i]);
            }

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            if (result.Errors != null && result.Errors.Count > 0)
            {
                sb.AppendLine("Errors:");
                for (int i = 0; i < result.Errors.Count; i++)
                    sb.AppendLine("  - " + result.Errors[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DeviceInputCatalogResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Input Catalog");
            sb.AppendLine("Joystick Axes: " + result.JoystickAxes.Count);
            for (int i = 0; i < result.JoystickAxes.Count; i++)
                sb.AppendLine("  - " + result.JoystickAxes[i]);

            sb.AppendLine("Joystick Buttons: " + result.JoystickButtons.Count);
            for (int i = 0; i < result.JoystickButtons.Count; i++)
                sb.AppendLine("  - " + result.JoystickButtons[i]);

            sb.AppendLine("Joystick POV Buttons: " + result.JoystickPovButtons.Count);
            for (int i = 0; i < result.JoystickPovButtons.Count; i++)
                sb.AppendLine("  - " + result.JoystickPovButtons[i]);

            if (result.JoystickOffVariants != null && result.JoystickOffVariants.Count > 0)
            {
                sb.AppendLine("Joystick _OFF Variants: " + result.JoystickOffVariants.Count);
                for (int i = 0; i < result.JoystickOffVariants.Count; i++)
                    sb.AppendLine("  - " + result.JoystickOffVariants[i]);
            }

            sb.AppendLine("Keyboard Keys (DirectInput): " + result.KeyboardDirectInput.Count);
            for (int i = 0; i < result.KeyboardDirectInput.Count; i++)
                sb.AppendLine("  - " + result.KeyboardDirectInput[i]);

            sb.AppendLine("Keyboard Keys (DCS): " + result.KeyboardDcs.Count);
            for (int i = 0; i < result.KeyboardDcs.Count; i++)
                sb.AppendLine("  - " + result.KeyboardDcs[i]);

            sb.AppendLine("Keyboard Keys (IL2 format, use with --format il2): " + result.KeyboardIl2.Count);
            for (int i = 0; i < result.KeyboardIl2.Count; i++)
                sb.AppendLine("  - " + result.KeyboardIl2[i]);

            if (result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DeviceListResult result)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Devices");
            sb.AppendLine("Connected: " + result.Connected.Count);
            for (int i = 0; i < result.Connected.Count; i++)
                sb.AppendLine("  - " + result.Connected[i]);
            sb.AppendLine("Profile: " + result.Profile.Count);
            for (int i = 0; i < result.Profile.Count; i++)
                sb.AppendLine("  - " + result.Profile[i]);

            if (result.OnlyConnected.Count > 0)
            {
                sb.AppendLine("Only Connected:");
                for (int i = 0; i < result.OnlyConnected.Count; i++)
                    sb.AppendLine("  - " + result.OnlyConnected[i]);
            }

            if (result.OnlyProfile.Count > 0)
            {
                sb.AppendLine("Only In Profile:");
                for (int i = 0; i < result.OnlyProfile.Count; i++)
                    sb.AppendLine("  - " + result.OnlyProfile[i]);
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(StarCitizenDeviceListResult result)
        {
            StarCitizenDeviceListResult data = result ?? new StarCitizenDeviceListResult();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Star Citizen Devices");
            sb.AppendLine("Root Path: " + (string.IsNullOrWhiteSpace(data.RootPath) ? "<auto>" : data.RootPath));
            sb.AppendLine("Source Path: " + (string.IsNullOrWhiteSpace(data.SourcePath) ? "<none>" : data.SourcePath));
            sb.AppendLine("Devices: " + data.Devices.Count);
            for (int i = 0; i < data.Devices.Count; i++)
                sb.AppendLine("  - " + data.Devices[i]);

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < data.Warnings.Count; i++)
                    sb.AppendLine("  - " + data.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DeviceExchangeResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Exchange");
            sb.AppendLine("Requested Old: " + (string.IsNullOrWhiteSpace(result.RequestedOldDevice) ? "<none>" : result.RequestedOldDevice));
            sb.AppendLine("Resolved Old: " + (string.IsNullOrWhiteSpace(result.OldDevice) ? "<none>" : result.OldDevice));
            sb.AppendLine("Requested New: " + (string.IsNullOrWhiteSpace(result.RequestedNewDevice) ? "<none>" : result.RequestedNewDevice));
            sb.AppendLine("Resolved New: " + (string.IsNullOrWhiteSpace(result.NewDevice) ? "<none>" : result.NewDevice));
            sb.AppendLine("Confirm Suggestions: " + result.ConfirmSuggestions.ToString().ToLowerInvariant());
            sb.AppendLine("Updated Binds (Device): " + result.UpdatedBindDevices);
            sb.AppendLine("Updated Binds (Backup): " + result.UpdatedBindBackups);
            sb.AppendLine("Updated Binds (Alias): " + result.UpdatedBindAliases);
            sb.AppendLine("Updated Binds (Reformers): " + result.UpdatedBindReformers);
            sb.AppendLine("Updated Modifiers: " + result.UpdatedModifiers);
            sb.AppendLine("Updated Joystick Aliases: " + result.UpdatedJoystickAliases);
            sb.AppendLine("Updated Joystick Images: " + result.UpdatedJoystickImages);
            sb.AppendLine("Updated Joystick PGUIDs: " + result.UpdatedJoystickPGuids);
            sb.AppendLine("Updated Joystick FFB: " + result.UpdatedJoystickFfb);
            sb.AppendLine("Updated Joystick Activity: " + result.UpdatedJoystickActivity);
            sb.AppendLine("Updated Local Joysticks: " + result.UpdatedLocalJoysticks);
            sb.AppendLine("Updated Relations: " + result.UpdatedRelations.Count);
            for (int i = 0; i < result.UpdatedRelations.Count; i++)
                sb.AppendLine("  - " + result.UpdatedRelations[i]);

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(DeviceRemoveResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Device Remove");
            sb.AppendLine("Requested Device: " + (string.IsNullOrWhiteSpace(result.RequestedDevice) ? "<none>" : result.RequestedDevice));
            sb.AppendLine("Resolved Device: " + (string.IsNullOrWhiteSpace(result.Device) ? "<none>" : result.Device));
            sb.AppendLine("Confirm Suggestions: " + result.ConfirmSuggestions.ToString().ToLowerInvariant());
            sb.AppendLine("Removed Binds: " + result.RemovedBinds);
            sb.AppendLine("Cleared Binds (Backup): " + result.ClearedBindBackups);
            sb.AppendLine("Cleared Binds (Alias): " + result.ClearedBindAliases);
            sb.AppendLine("Removed Binds (Reformers): " + result.RemovedBindReformers);
            sb.AppendLine("Removed Modifiers: " + result.RemovedModifiers);
            sb.AppendLine("Removed Joystick Aliases: " + result.RemovedJoystickAliases);
            sb.AppendLine("Removed Joystick Images: " + result.RemovedJoystickImages);
            sb.AppendLine("Removed Joystick PGUIDs: " + result.RemovedJoystickPGuids);
            sb.AppendLine("Removed Joystick FFB: " + result.RemovedJoystickFfb);
            sb.AppendLine("Removed Joystick Activity: " + result.RemovedJoystickActivity);
            sb.AppendLine("Removed Local Joysticks: " + result.RemovedLocalJoysticks);
            sb.AppendLine("Updated Relations: " + result.UpdatedRelations.Count);
            for (int i = 0; i < result.UpdatedRelations.Count; i++)
                sb.AppendLine("  - " + result.UpdatedRelations[i]);

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(StarCitizenDbRefreshResult result)
        {
            StarCitizenDbRefreshResult data = result ?? new StarCitizenDbRefreshResult();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Star Citizen DB Refresh");
            sb.AppendLine("Success: " + data.Success.ToString().ToLowerInvariant());
            sb.AppendLine("Dry Run: " + data.DryRun.ToString().ToLowerInvariant());
            sb.AppendLine("Include User Profiles: " + data.IncludeUserProfiles.ToString().ToLowerInvariant());
            sb.AppendLine("Requested Source: " + (string.IsNullOrWhiteSpace(data.RequestedSourcePath) ? "<none>" : data.RequestedSourcePath));
            sb.AppendLine("Requested Install: " + (string.IsNullOrWhiteSpace(data.RequestedInstallPath) ? "<none>" : data.RequestedInstallPath));
            sb.AppendLine("Requested Branch: " + (string.IsNullOrWhiteSpace(data.RequestedBranch) ? "<none>" : data.RequestedBranch));
            sb.AppendLine("Selected Source: " + (string.IsNullOrWhiteSpace(data.SourceFile) ? "<none>" : data.SourceFile));
            sb.AppendLine("Resolved Install: " + (string.IsNullOrWhiteSpace(data.InstallPath) ? "<none>" : data.InstallPath));
            sb.AppendLine("Resolved Branch: " + (string.IsNullOrWhiteSpace(data.Branch) ? "<none>" : data.Branch));
            sb.AppendLine("DB Path: " + (string.IsNullOrWhiteSpace(data.DbPath) ? "<none>" : data.DbPath));
            sb.AppendLine("ActionMaps Output: " + (string.IsNullOrWhiteSpace(data.ActionmapsOutputFile) ? "<none>" : data.ActionmapsOutputFile));
            sb.AppendLine("AllBinds Output: " + (string.IsNullOrWhiteSpace(data.AllBindsOutputFile) ? "<none>" : data.AllBindsOutputFile));
            sb.AppendLine("Action Maps: " + data.ActionMapCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Actions: " + data.ActionCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Axis: " + data.AxisCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Actions With Option Group: " + data.OptionGroupActions.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Joystick Option Groups: " + data.OptionTreeGroups.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Buttons: " + data.ButtonCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Duplicates Skipped: " + data.DuplicateActionsSkipped.ToString(CultureInfo.InvariantCulture));

            if (data.SourceCandidates != null && data.SourceCandidates.Count > 0)
            {
                sb.AppendLine("Source Candidates: " + data.SourceCandidates.Count.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < data.SourceCandidates.Count; i++)
                    sb.AppendLine("  - " + data.SourceCandidates[i]);
            }

            if (data.Warnings != null && data.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < data.Warnings.Count; i++)
                    sb.AppendLine("  - " + data.Warnings[i]);
            }

            if (!string.IsNullOrWhiteSpace(data.Error))
                sb.AppendLine("Error: " + data.Error);

            return sb.ToString();
        }

        private static string ToText(GamePathCatalog result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Game Paths");

            List<GamePathEntry> games = (result?.Games ?? new List<GamePathEntry>())
                .Where(g => g != null)
                .OrderBy(g => g.Game, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < games.Count; i++)
            {
                GamePathEntry game = games[i];
                if (i > 0)
                    sb.AppendLine();
                sb.AppendLine(game.Game ?? string.Empty);
                sb.AppendLine("  Preferred Install: " + (string.IsNullOrWhiteSpace(game.PreferredInstallPath) ? "<none>" : game.PreferredInstallPath));
                sb.AppendLine("  Preferred Config: " + (string.IsNullOrWhiteSpace(game.PreferredConfigPath) ? "<none>" : game.PreferredConfigPath));
                List<string> installPaths = game.InstallPaths ?? new List<string>();
                List<string> configPaths = game.ConfigPaths ?? new List<string>();
                sb.AppendLine("  Install Paths: " + installPaths.Count);
                for (int p = 0; p < installPaths.Count; p++)
                    sb.AppendLine("    - " + installPaths[p]);
                sb.AppendLine("  Config Paths: " + configPaths.Count);
                for (int p = 0; p < configPaths.Count; p++)
                    sb.AppendLine("    - " + configPaths[p]);
            }

            if (result != null && result.Warnings != null && result.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }

        private static string ToText(ProfileValidationResult result)
        {
            ProfileValidationResult data = result ?? new ProfileValidationResult();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Profile Validation");
            sb.AppendLine("Passed: " + data.Passed);
            sb.AppendLine("Fail On Warnings: " + data.FailOnWarnings);
            sb.AppendLine("Warnings: " + data.WarningCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Errors: " + data.ErrorCount.ToString(CultureInfo.InvariantCulture));

            sb.AppendLine("Relation Warnings: " + data.RelationWarnings.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < data.RelationWarnings.Count; i++)
                sb.AppendLine("  - " + data.RelationWarnings[i]);

            sb.AppendLine("Bind Errors: " + data.BindErrors.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < data.BindErrors.Count; i++)
                sb.AppendLine("  - " + data.BindErrors[i]);

            sb.AppendLine("Modifier Errors: " + data.ModifierErrors.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < data.ModifierErrors.Count; i++)
                sb.AppendLine("  - " + data.ModifierErrors[i]);

            sb.AppendLine("Duplicate Active Errors: " + data.DuplicateActiveErrors.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < data.DuplicateActiveErrors.Count; i++)
                sb.AppendLine("  - " + data.DuplicateActiveErrors[i]);

            return sb.ToString();
        }

        private static string ToText(BindUnbindResult result)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Binds Unbind");
            sb.AppendLine("Input: " + result.Input);
            sb.AppendLine("Normalized Input: " + result.NormalizedInput);
            sb.AppendLine("Format: " + result.Format);
            sb.AppendLine("Device Filter: " + (string.IsNullOrWhiteSpace(result.DeviceFilter) ? "<none>" : result.DeviceFilter));
            sb.AppendLine("Game Filter: " + (string.IsNullOrWhiteSpace(result.GameFilter) ? "<none>" : result.GameFilter));
            sb.AppendLine("Updated Relations: " + result.UpdatedRelations.Count);
            for (int i = 0; i < result.UpdatedRelations.Count; i++)
                sb.AppendLine("  - " + result.UpdatedRelations[i]);
            return sb.ToString();
        }

        private static string ToText(BindClearResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Bind Clear");
            sb.AppendLine("Relation: " + result.Relation);
            sb.AppendLine("Input Only: " + result.InputOnly);
            sb.AppendLine("Had Bind: " + result.HadBind);
            sb.AppendLine("Cleared Input: " + result.ClearedInput);
            sb.AppendLine("Removed Bind: " + result.RemovedBind);
            return sb.ToString();
        }

        private static string ToText(RelationGroupListResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (string.Equals(result.Scope, "relation", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("Relation Groups");
                sb.AppendLine("Relation: " + (string.IsNullOrWhiteSpace(result.Relation) ? "<none>" : result.Relation));
                sb.AppendLine("Groups: " + result.Groups.Count);
                for (int i = 0; i < result.Groups.Count; i++)
                    sb.AppendLine("  - " + result.Groups[i]);
                return sb.ToString();
            }

            sb.AppendLine("Groups");
            sb.AppendLine("Groups: " + result.Groups.Count);
            for (int i = 0; i < result.Entries.Count; i++)
            {
                RelationGroupListEntry entry = result.Entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                string relations = entry.Relations.Count == 0
                    ? "<none>"
                    : string.Join(", ", entry.Relations);
                sb.AppendLine("  - " + entry.Name + " (" + entry.RelationCount + "): " + relations);
            }
            return sb.ToString();
        }

        private static string ToText(RelationGroupRenameResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Group Rename");
            sb.AppendLine("Old Name: " + result.OldName);
            sb.AppendLine("New Name: " + result.NewName);
            sb.AppendLine("Updated Relations: " + result.UpdatedCount);
            for (int i = 0; i < result.UpdatedRelations.Count; i++)
                sb.AppendLine("  - " + result.UpdatedRelations[i]);
            return sb.ToString();
        }

        private static string ToText(BindModifierListResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (string.Equals(result.Scope, "bind", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("Bind Modifiers");
                sb.AppendLine("Relation: " + (string.IsNullOrWhiteSpace(result.Relation) ? "<none>" : result.Relation));
                sb.AppendLine("Modifiers: " + result.Modifiers.Count);
                for (int i = 0; i < result.Modifiers.Count; i++)
                    sb.AppendLine("  - " + result.Modifiers[i]);
                sb.AppendLine("Reformers: " + result.Reformers.Count);
                for (int i = 0; i < result.Reformers.Count; i++)
                    sb.AppendLine("  - " + result.Reformers[i]);
                return sb.ToString();
            }

            sb.AppendLine("Modifiers");
            sb.AppendLine("Modifiers: " + result.Modifiers.Count);
            for (int i = 0; i < result.Entries.Count; i++)
            {
                BindModifierListEntry entry = result.Entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                string relations = entry.Relations.Count == 0
                    ? "<none>"
                    : string.Join(", ", entry.Relations);
                sb.AppendLine("  - " + entry.Name + " (" + entry.BindCount + "): " + relations);
            }
            return sb.ToString();
        }

        private static string ToText(BindModifierRenameResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Modifier Rename");
            sb.AppendLine("Scope: " + result.Scope);
            if (!string.IsNullOrWhiteSpace(result.Relation))
                sb.AppendLine("Relation: " + result.Relation);
            sb.AppendLine("Old Name: " + result.OldName);
            sb.AppendLine("New Name: " + result.NewName);
            sb.AppendLine("Updated Relations: " + result.UpdatedCount);
            for (int i = 0; i < result.UpdatedRelations.Count; i++)
                sb.AppendLine("  - " + result.UpdatedRelations[i]);
            return sb.ToString();
        }

        private static string ToText(RelationMassResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Relation Mass Operation");
            sb.AppendLine("Action: " + result.Action);
            sb.AppendLine("Selected Relations: " + result.SelectedCount);
            for (int i = 0; i < result.SelectedRelations.Count; i++)
                sb.AppendLine("  - " + result.SelectedRelations[i]);

            if (result.CreatedRelations.Count > 0)
            {
                sb.AppendLine("Created Relations: " + result.CreatedRelations.Count);
                for (int i = 0; i < result.CreatedRelations.Count; i++)
                    sb.AppendLine("  - " + result.CreatedRelations[i]);
            }

            if (result.DeletedRelations.Count > 0)
            {
                sb.AppendLine("Deleted Relations: " + result.DeletedRelations.Count);
                for (int i = 0; i < result.DeletedRelations.Count; i++)
                    sb.AppendLine("  - " + result.DeletedRelations[i]);
            }

            if (result.RenamedRelations.Count > 0)
            {
                sb.AppendLine("Renamed Relations: " + result.RenamedRelations.Count);
                for (int i = 0; i < result.RenamedRelations.Count; i++)
                {
                    RelationMassRenameEntry rename = result.RenamedRelations[i];
                    if (rename == null)
                        continue;
                    sb.AppendLine("  - " + rename.OldName + " => " + rename.NewName);
                }
            }

            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                sb.AppendLine("Output Path: " + result.OutputPath);
                sb.AppendLine("Export Format: " + (string.IsNullOrWhiteSpace(result.ExportFormat) ? "<none>" : result.ExportFormat));
            }

            if (result.Warnings.Count > 0)
            {
                sb.AppendLine("Warnings:");
                for (int i = 0; i < result.Warnings.Count; i++)
                    sb.AppendLine("  - " + result.Warnings[i]);
            }

            return sb.ToString();
        }
        private static int FailResolution(string kind, string query, List<string> candidates)
        {
            return Fail(BuildResolutionErrorMessage(kind, query, candidates));
        }

        private static string BuildResolutionErrorMessage(string kind, string query, List<string> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return "No " + kind + " matched query: " + query;

            string sample = string.Join(", ", candidates.Take(10));
            if (candidates.Count > 10)
                sample = sample + ", ...";
            return "Ambiguous " + kind + " query '" + query + "'. Matches: " + sample;
        }

        private static bool ShouldAutoSaveProfile(string profilePath, string area, List<string> command)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                return false;

            return IsMutatingCommand(area, command);
        }

        // The game whose files a command area writes; the versioned game backups are filed under it.
        private static string BackupGame(string area)
        {
            switch ((area ?? string.Empty).ToLowerInvariant())
            {
                case "dcs": return "DCS";
                case "il2": return "IL2Game";
                case "sc":
                case "starcitizen": return "StarCitizen";
            }
            Games.IGameIntegration game;
            return Games.GameRegistry.TryResolve(area, out game) ? game.GameId : string.Empty;
        }

        // engine hud devices [--grip <device>=<catalog id>]...: pictures, inputs and button positions of the connected devices.
        private static int RunHud(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "devices" : command[0].ToLowerInvariant();
            if (action != "devices")
                return Fail("engine hud devices [--grip <device>=<catalog id>]... [--no-device-photos]");
            List<string> args = command.Skip(1).ToList();
            // The user said no to the makers' photos (GUI: Settings > Privacy "Never"): their addresses are not even named.
            // The engine never downloads a device photo itself; the GUI does, per device, after the user's yes.
            bool noDevicePhotos = PopFlag(args, "--no-device-photos");
            Dictionary<string, string> grips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string grip in PopOptionValues(args, "--grip"))
            {
                int split = grip.LastIndexOf('=');
                if (split <= 0 || split == grip.Length - 1)
                    return Fail("--grip needs <device>=<catalog id>.");
                grips[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
            }
            if (args.Count > 0)
                return Fail("Unknown hud devices argument(s): " + string.Join(" ", args));
            EngineHudResult result = EngineHud.Devices(grips, new List<string>());
            if (noDevicePhotos)
            {
                foreach (EngineHudDevice device in result.Devices)
                {
                    device.Image = string.Empty;
                    foreach (EngineHudChoice choice in device.GripChoices)
                        choice.Image = string.Empty;
                }
            }
            EmitPayload(textOutput ? EngineHud.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]
        // engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]
        private static int RunOverlay(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "map" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            List<string> warnings = new List<string>();
            if (action == "map")
            {
                string game = PopOptionValue(args, "--game");
                string plane = PopOptionValue(args, "--plane");
                string unit = PopOptionValue(args, "--unit");
                if (args.Count > 0)
                    return Fail("Unknown overlay map argument(s): " + string.Join(" ", args));
                EngineOverlayMap map = EngineOverlay.Map(game, plane, unit, warnings);
                map.Warnings = warnings;
                EmitPayload(textOutput ? EngineOverlay.ToText(map) : EngineSerialization.ToJson(map), outPath);
                return 0;
            }
            if (action == "dcs-hook")
            {
                string hookAction = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "status";
                if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                    args.RemoveAt(0);
                string path = PopOptionValue(args, "--path");
                bool dryRun = PopFlag(args, "--dry-run");
                if (args.Count > 0)
                    return Fail("Unknown overlay dcs-hook argument(s): " + string.Join(" ", args));
                if (string.IsNullOrWhiteSpace(path))
                    path = GamePathDiscovery.DiscoverDcs(warnings).PreferredConfigPath;
                if (string.IsNullOrWhiteSpace(path))
                    return Fail("No DCS Saved Games folder found; use --path <Saved Games\\DCS>.");
                if (hookAction != "status" && !dryRun && Games.GameProcessGuard.FindRunning(new[] { "DCS" }).Count > 0)
                    return Fail("Close DCS first: it reads Export.lua when a mission starts.");
                GameBackups.Begin("DCS", "engine overlay dcs-hook " + hookAction, dryRun);
                EngineOverlayHookResult result = EngineOverlay.Hook(hookAction, NormalizePath(path), dryRun);
                result.Warnings.InsertRange(0, warnings);
                EmitPayload(textOutput ? EngineOverlay.ToText(result) : EngineSerialization.ToJson(result), outPath);
                return result.Errors.Count > 0 ? Fail(string.Join(" | ", result.Errors)) : 0;
            }
            return Fail("engine overlay map|dcs-hook");
        }

        // engine media [games|aircraft|sources] [--no-internet-pictures]: official pictures for the GUI (game covers,
        // DCS aircraft pictures); sources lists the web source of each game's cover.
        private static int RunMedia(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.ToList();
            bool noInternet = PopFlag(args, "--no-internet-pictures");
            string kind = args.Count == 0 ? "all" : args[0].ToLowerInvariant();
            if (args.Count > 1 || (kind != "all" && kind != "games" && kind != "aircraft" && kind != "sources"))
                return Fail("engine media [games|aircraft|sources] [--no-internet-pictures]");
            if (kind == "sources")
            {
                List<string> ids = EngineMedia.KnownGames().Select(g => g.GameId).ToList();
                Dictionary<string, List<EngineCoverSource>> catalog = EngineGameCovers.Catalog();
                EmitPayload(textOutput
                    ? EngineGameCovers.SourcesText(ids)
                    : EngineSerialization.ToJson(new { Games = ids.Select(id => new { GameId = id, Sources = catalog.ContainsKey(id) ? catalog[id] : new List<EngineCoverSource>() }).ToList() }), outPath);
                return 0;
            }
            List<string> warnings = new List<string>();
            List<EngineMediaGame> games = kind == "aircraft" ? null : EngineMedia.Games(warnings, noInternet);
            List<EngineMediaAircraft> aircraft = kind == "games" ? null : EngineMedia.Aircraft(warnings);
            EmitPayload(textOutput ? EngineMedia.ToText(games, aircraft) : EngineSerialization.ToJson(new { Games = games ?? new List<EngineMediaGame>(), Aircraft = aircraft ?? new List<EngineMediaAircraft>(), Warnings = warnings }), outPath);
            return 0;
        }

        // engine backups list [--game <id>] | show <id> | restore <id|latest> [--game <id>] [--dry-run]
        private static int RunBackups(List<string> command, string outPath, bool textOutput)
        {
            List<string> args = command.ToList();
            string action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
            if (args.Count > 0)
                args.RemoveAt(0);
            string game = PopOptionValue(args, "--game");
            if (game != null)
            {
                string resolved = BackupGame(game);
                game = resolved.Length > 0 ? resolved : game;
            }
            bool dryRun = PopFlag(args, "--dry-run");
            if (action == "list")
            {
                List<GameBackupManifest> sets = GameBackups.List(game);
                EmitPayload(textOutput ? GameBackups.ToText(sets) : EngineSerialization.ToJson(sets), outPath);
                return 0;
            }
            if (args.Count == 0)
                return Fail("engine backups " + action + " needs a backup id (or latest).");
            GameBackupManifest set = GameBackups.Find(args[0], game);
            if (set == null)
                return Fail("No backup '" + args[0] + "'" + (game != null ? " for " + game : string.Empty) + ". See engine backups list.");
            if (action == "show")
            {
                EmitPayload(textOutput ? GameBackups.ToText(set) : EngineSerialization.ToJson(set), outPath);
                return 0;
            }
            if (action != "restore")
                return Fail("Unknown backups action: " + action + " (use list, show or restore).");
            string refusal = RefuseGameAction(set.Game, "restore");
            if (refusal != null)
                return Fail(refusal);
            GameBackupRestoreResult result = GameBackups.Restore(set, dryRun, BackupProcessNames(set.Game));
            EmitPayload(textOutput ? GameBackups.ToText(result) : EngineSerialization.ToJson(result), outPath);
            return result.Errors.Count > 0 ? 1 : 0;
        }

        // DCS, IL-2 and Star Citizen rewrite their control files themselves (DCS on Options > OK, IL-2 and SC on exit):
        // an export while the game runs is lost or mixed, so it is refused like for the registry games.
        private static string RefuseWhileRunning(string gameName, IEnumerable<string> processNames)
        {
            List<string> running = Games.GameProcessGuard.FindRunning(processNames);
            return running.Count == 0 ? null : gameName + " is running (" + string.Join(", ", running) + "). Close the game first; it rewrites its control files.";
        }

        // Import, export, scan, DB refresh, stick defaults and backup restore need the game installed (found, or the
        // user's folder set); paths, catalogs, lists and profile-only commands do not.
        private static string RefuseGameAction(string gameId, string action)
        {
            string verb;
            switch (action)
            {
                case "scan": verb = "scan"; break;
                case "import": verb = "import from"; break;
                case "export": verb = "export to"; break;
                case "refresh":
                case "refresh-db": verb = "refresh the DB of"; break;
                case "defaults":
                case "stick-defaults":
                case "stickdefaults": verb = "change the default binds of"; break;
                case "restore": verb = "restore a backup of"; break;
                default: return null;
            }
            return GameInstalls.RefuseIfMissing(gameId, verb, new List<string>());
        }

        // Processes that rewrite a game's config on exit; a restore is refused while one runs.
        private static IEnumerable<string> BackupProcessNames(string game)
        {
            switch (game)
            {
                case "DCS": return new[] { "DCS" };
                case "IL2Game": return new[] { "Il-2" };
                case "StarCitizen": return new[] { "StarCitizen" };
            }
            // A backup made for the single MSFS / X-Plane game: every version's process blocks the restore.
            if (Games.LegacyGameIds.IsLegacy(game))
                return Games.LegacyGameIds.Expand(game).SelectMany(BackupProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Games.IGameIntegration integration;
            if (!Games.GameRegistry.TryResolve(game, out integration))
                return Enumerable.Empty<string>();
            // A generic process name (Cliffs of Dover's "Launcher"): only the game's own running exe blocks the restore.
            return integration is Games.IGameProcessFilter ? Games.GameProcessGuard.FindRunning(integration) : integration.ProcessNames;
        }

        private static bool ShouldRunLoadCorrections(string area, List<string> command)
        {
            string commandArea = (area ?? string.Empty).Trim().ToLowerInvariant();
            string action = command != null && command.Count > 0
                ? (command[0] ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;

            // profile load is explicitly a load operation and should retain correction behavior.
            if (commandArea == "profile")
            {
                if (string.IsNullOrWhiteSpace(action))
                    action = "summary";
                return action == "load";
            }

            if (commandArea == "dcs" &&
                (action == "ffb" || action == "force-feedback" || action == "forcefeedback"))
            {
                return false;
            }
            if (commandArea == "devices")
            {
                return false;
            }
            // The auto-group preview must see the same corrected profile the real run changes.
            if (commandArea == "relations" && (action == "auto-group" || action == "auto-assign" || action == "suggest" || action == "from-text"))
                return true;
            // The Setup wizard's plan must see the same corrected profile its apply changes.
            if (commandArea == "setup" && (action == "plan" || action == "apply"))
                return true;
            // sync diff must see the same corrected profile sync apply changes (the change ids depend on it).
            if (commandArea == "sync" && (action == "diff" || action == "apply"))
                return true;

            return IsMutatingCommand(commandArea, command);
        }

        private static bool IsMutatingCommand(string area, List<string> command)
        {
            string commandArea = (area ?? string.Empty).Trim().ToLowerInvariant();
            string action = command != null && command.Count > 0
                ? (command[0] ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;

            Games.IGameIntegration registryGame;
            if (Games.GameRegistry.TryResolve(commandArea, out registryGame))
                return Games.GameCommands.IsMutating(command);

            switch (commandArea)
            {
                case "setup":
                    return action == "apply" && !command.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
                case "sync":
                    return EngineSync.IsMutating(command);
                case "vjoy":
                    return EngineVJoy.IsMutating(command);
                case "ffb":
                    return action == "set" || action == "clear";
                case "relations":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "list";
                    if (action == "group")
                    {
                        string groupAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : string.Empty;
                        return groupAction == "add" ||
                               groupAction == "remove" ||
                               groupAction == "rename";
                    }
                    if (action == "mass")
                    {
                        string massAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : string.Empty;
                        return massAction == "delete" ||
                               massAction == "merge" ||
                               massAction == "split" ||
                               massAction == "duplicate" ||
                               massAction == "add-prefix" ||
                               massAction == "prefix" ||
                               massAction == "add-postfix" ||
                               massAction == "postfix" ||
                               massAction == "remove-part" ||
                               massAction == "remove-name-part";
                    }
                    if (action == "auto-group" || action == "auto-assign")
                        return !command.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
                    if (action == "from-text")
                        return !command.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--check", StringComparison.OrdinalIgnoreCase));
                    if (action == "suggest")
                        return command.Count > 1 && string.Equals(command[1], "apply", StringComparison.OrdinalIgnoreCase);
                    if (action == "chat")
                        return command.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));
                    if (action == "assistant")
                        return !command.Any(a => string.Equals(a, "--show-prompt", StringComparison.OrdinalIgnoreCase));
                    if (action == "plan")
                        return command.Count > 1 && string.Equals(command[1], "apply", StringComparison.OrdinalIgnoreCase) && !command.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
                    if (action == "aircraft")
                        return command.Count > 1 && string.Equals(command[1], "set", StringComparison.OrdinalIgnoreCase);
                    if (action == "check-aircraft")
                        return command.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));
                    if (action == "function")
                        return command.Any(a => string.Equals(a, "--set", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--none", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(a, "--auto", StringComparison.OrdinalIgnoreCase));
                    return action == "add" ||
                           action == "rename" ||
                           action == "item" ||
                           action == "import" ||
                           action == "edit";
                case "binds":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "list";
                    if (action == "modifier")
                    {
                        string modifierAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : string.Empty;
                        return modifierAction == "add" ||
                               modifierAction == "remove" ||
                               modifierAction == "rename";
                    }
                    if (action == "tuning")
                    {
                        string tuningAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : string.Empty;
                        return tuningAction == "set" || tuningAction == "clear";
                    }
                    if (action == "detent")
                    {
                        string detentAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : string.Empty;
                        if (detentAction == "merge")
                            return command.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));
                        return detentAction == "set" || detentAction == "calibrate" || detentAction == "uncalibrate";
                    }
                    return action == "set" ||
                           action == "clear" ||
                           action == "unbind" ||
                           action == "import" ||
                           action == "edit";
                case "dcs":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "scan";
                    if (action == "ffb" || action == "force-feedback" || action == "forcefeedback")
                    {
                        string ffbAction = command != null && command.Count > 1
                            ? (command[1] ?? string.Empty).Trim().ToLowerInvariant()
                            : "list";
                        return ffbAction == "set" || ffbAction == "clear";
                    }
                    return action == "import";
                case "il2":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "scan";
                    return action == "import";
                case "starcitizen":
                case "sc":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "scan";
                    return action == "import";
                case "devices":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "list";
                    return action == "exchange" ||
                           action == "replace" ||
                           action == "auto-remap" ||
                           action == "remap" ||
                           action == "swap" ||
                           action == "remove" ||
                           action == "purge" ||
                           action == "clear" ||
                           (action == "alias" || action == "aliases") && command.Count > 1 &&
                           !string.Equals((command[1] ?? string.Empty).Trim(), "list", StringComparison.OrdinalIgnoreCase);
                case "profile":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "summary";
                    return action == "merge";
                case "layout":
                    if (string.IsNullOrWhiteSpace(action))
                        action = "render";
                    if (action == "image")
                        return command.Any(a => string.Equals(a, "--set", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--clear", StringComparison.OrdinalIgnoreCase));
                    if (action != "suggest")
                        return false;
                    if (command == null)
                        return false;
                    for (int i = 1; i < command.Count; i++)
                    {
                        if (!string.Equals(command[i], "--apply", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (i + 1 >= command.Count || command[i + 1].StartsWith("--", StringComparison.Ordinal))
                            return true;
                        if (TryParseOptionBoolean(command[i + 1], out bool apply))
                            return apply;
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        private static string BuildCommandMetricName(string area, List<string> command)
        {
            string resolvedArea = string.IsNullOrWhiteSpace(area) ? "unknown" : area.Trim().ToLowerInvariant();
            string action = command != null && command.Count > 0
                ? (command[0] ?? string.Empty).Trim().ToLowerInvariant()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(action))
                action = resolvedArea == "summary" ? "summary" : "default";
            return resolvedArea + "." + action;
        }

        // Always a sink: the progress messages go to the background log even without --status.
        private static Action<string> BuildStatusSink(string scope)
        {
            string trimmedScope = (scope ?? string.Empty).Trim();
            return message =>
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;
                if (trimmedScope.Length == 0)
                    Status(message);
                else
                    Status(trimmedScope + ": " + message);
            };
        }

        private static void Status(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            JoyPro.Logging.JoyProLog.Debug("Status", message);
            if (!StatusUpdatesEnabled)
                return;
            Console.Error.WriteLine("[engine-status] " + message);
        }

        private static void AddBenchmark(string key, long elapsedMs)
        {
            if (!BenchmarkEnabled || string.IsNullOrWhiteSpace(key))
                return;
            if (BenchmarksMs.ContainsKey(key))
                BenchmarksMs[key] += elapsedMs;
            else
                BenchmarksMs[key] = elapsedMs;
        }

        private static T Measure<T>(string key, Func<T> action)
        {
            if (action == null)
                return default(T);

            Stopwatch sw = Stopwatch.StartNew();
            Status("Starting " + key + ".");
            try
            {
                return action();
            }
            finally
            {
                sw.Stop();
                AddBenchmark(key, sw.ElapsedMilliseconds);
                Status(
                    "Finished " +
                    key +
                    " in " +
                    sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " ms.");
            }
        }

        private static void EmitBenchmarkSummary(string commandName, int exitCode)
        {
            if (!BenchmarkEnabled)
                return;

            Console.Error.WriteLine(
                "[engine-benchmark] command=" +
                (commandName ?? string.Empty) +
                " exitCode=" +
                exitCode.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, long> entry in BenchmarksMs.OrderByDescending(kvp => kvp.Value))
            {
                Console.Error.WriteLine(
                    "[engine-benchmark] " +
                    entry.Key +
                    "=" +
                    entry.Value.ToString(CultureInfo.InvariantCulture) +
                    " ms");
            }
        }

        private static int Fail(string message)
        {
            JoyPro.Logging.JoyProLog.Warn("Engine", "Command failed: " + message);
            Console.Error.WriteLine(message);
            return 1;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("engine - JoyPro CLI engine");
            Console.WriteLine();
            Console.WriteLine("Global options:");
            Console.WriteLine("  --profile <path>     Load/create this .pr0file and auto-save after mutating commands");
            Console.WriteLine("  --out <path>         Write command output to file");
            Console.WriteLine("  --editor <command>   Editor command for relations edit / binds edit (use {file} placeholder)");
            Console.WriteLine("  --text               Text output instead of JSON for list/export/show/add");
            Console.WriteLine("  --status true|false  Emit live status/progress updates to stderr (default: false)");
            Console.WriteLine("  --progress true|false  Emit only the machine-readable progress lines to stderr: [engine-progress] <current>/<total> <phase>: <item> (default: false; --status true includes them)");
            Console.WriteLine("  --benchmark true|false  Emit per-command timing summary to stderr (default: false)");
            Console.WriteLine("  --load-corrections true|false   Apply load-time correction mechanics (default: true)");
            Console.WriteLine("  --auto-add-db-items true|false  Auto-add corrected DB nodes during profile load (default: true)");
            Console.WriteLine("  --remap-missing-devices true|false  Remap missing profile device names during load (default: true)");
            Console.WriteLine("  --confirm-device-remap true|false   Ask before each load-time device remap (default: true)");
            Console.WriteLine("  --correction-mode strict|balanced|aggressive  Confidence model for relation-item remaps (default: balanced)");
            Console.WriteLine("  --correction-auto-threshold <0..200>  Minimum score to auto-apply relation-item remaps (default: mode-based)");
            Console.WriteLine("  --confirm-corrections true|false   Ask for confirmation on medium-confidence item remaps (default: true)");
            Console.WriteLine("  --correction-report <path>         Write load correction report (json, or text with .txt/.log)");
            Console.WriteLine("  --history-note <text>  Name of this change in the profile history (default: the command's name)");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  engine summary");
            Console.WriteLine("  engine profile summary");
            Console.WriteLine("  engine profile load <path.pr0file>");
            Console.WriteLine("  engine profile save <path.pr0file>");
            Console.WriteLine("  engine profile new <path.pr0file>");
            Console.WriteLine("  engine profile save-last");
            Console.WriteLine("  engine profile validate [--fail-on-warnings true|false]");
            Console.WriteLine("  engine profile games   (games and DCS aircraft the profile has relations for, with how many are bound: what each export writes)");
            Console.WriteLine("  engine profile merge <left.pr0file> <right.pr0file> [<more.pr0file> ...] [--mode left|right|ask] [--save <path.pr0file>]");
            Console.WriteLine("  engine profile history [list]   (the versions of --profile, newest first: every save is one, kept in <data>\\ProfileHistory; undo/redo state)");
            Console.WriteLine("  engine profile history show <id> | diff <id> [<id>|current]   (what a version changed / the changes between two versions)");
            Console.WriteLine("  engine profile history restore <id> | undo | redo   (writes a version back into the profile; that is a version too, so it can be undone)");
            Console.WriteLine("  engine profile history keep <id> [on|off] | checkpoint [--label <text>]   (a kept version is never cleaned up; a checkpoint keeps the profile as it is now)");
            Console.WriteLine("  engine profile history prune [--all] [--dry-run] | usage [--all]   (clean-up: all of 24 h, hourly 7 days, daily 60 days, weekly after; 200 MB per profile, 1 GB in total)");
            Console.WriteLine("  engine dcs scan [<saved-games-path>] [--path <saved-games-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]");
            Console.WriteLine("  engine dcs import [<saved-games-path>] [--path <saved-games-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--ignore-easy-planes true|false] [--include-defaults] [--import-inverted] [--import-slider] [--import-curvature] [--import-deadzone] [--import-saturation-x] [--import-saturation-y] [--confirm true|false]");
            Console.WriteLine("  engine dcs export [<saved-games-path>] [--path <saved-games-path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--nuke-untouched-devices true|false] [--clean-all-aircraft true|false] [--plane <name>] [--planes <csv>]   (only these aircraft; writes .diff.lua + .jp backup + modifiers.lua named as DCS names the device; clean: the written devices also lose binds and DCS defaults in every other aircraft unless --clean-all-aircraft false or a plane filter; nuke: devices without binds in an exported plane lose all binds there, DCS defaults included)");
            Console.WriteLine("  engine dcs defaults cut [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (moves stick-specific defaults from game folder into BACKUP_JOYSTICK_DEFAULTS)");
            Console.WriteLine("  engine dcs defaults restore [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--backup-path <path>]   (restores joystick defaults from BACKUP_JOYSTICK_DEFAULTS)");
            Console.WriteLine("  engine dcs strip-defaults [apply|restore|status|auto] [--dry-run] [--restore] [--skip-ui-layer true|false] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>] [--luae <luae.exe>] [--no-load-check]   (switches DCS's default joystick binds off in the DCS install: wraps every joystick profile lua, combos = nil, backup <file>.nocombo.bak; restore puts the originals back; auto re-applies after a DCS update; patched files fail server integrity checks)");
            Console.WriteLine("  engine dcs device-luas [list|remove|restore] [--dry-run] [--remember true|false] [--install-path <dcs-install-path>] [--saved-games <saved-games-path>]   (moves the device-specific default files of the DCS install (<device type>.lua, template diffs) into JoyPro's backup folder so default.lua applies; restore puts them back)");
            Console.WriteLine("  engine dcs verify-clean [<saved-games-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--plane <name>] [--planes <csv>] [--device <name>] [--devices <csv>] [--include-disconnected true|false] [--max-listed <n>]   (DCS's own Lua lists the default binds still active on the joysticks with the files of that Saved Games folder; read only)");
            Console.WriteLine("  engine dcs refresh clean [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--settle-seconds <n>] [--default-stick <name>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]");
            Console.WriteLine("  engine dcs refresh id [<saved-games-path>] [<dcs-install-path>] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--ocr true|false] [--launch true|false] [--keep-running true|false] [--headless-lua true|false] [--plane <name>] [--planes <csv>] [--dry-run] [--menu-wait-seconds <n>] [--ocr-menu-timeout-seconds <n>] [--modules-to-scan <n>] [--clicks-xml <path>] [--clicks-dir <path>] [--dcs-exe <path>] [--preview-clicks] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]");
            Console.WriteLine("  engine dcs refresh preview-clicks [--ocr true|false] [--path <saved-games-path>] [--install-path <dcs-install-path>] [--clicks-xml <path>] [--clicks-dir <path>] [--preview-image <path>] [--gui-scale <value>] [--window-left <n> --window-top <n> --window-width <n> --window-height <n>]");
            Console.WriteLine("  engine dcs ffb list [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--confirm true|false]");
            Console.WriteLine("  engine dcs ffb set <device-name> [--path <saved-games-path>] [--invert-x true|false] [--invert-y true|false] [--swap-axis true|false] [--confirm true|false]");
            Console.WriteLine("  engine dcs ffb clear [<device-name>] [--path <saved-games-path>] [--device <name>] [--devices <csv>] [--all] [--confirm true|false]");
            Console.WriteLine("  engine il2 scan [<il2-root-path>] [--path <il2-root-path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]");
            Console.WriteLine("  engine il2 import [<il2-root-path>] [--path <il2-root-path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--import-inverted] [--import-curvature] [--import-deadzone] [--confirm true|false]");
            Console.WriteLine("  engine il2 export [<il2-root-path>] [--path <il2-root-path>] [--mode clean|add|merge|merge-overwrite] [--nuke-untouched-devices true|false]   (conflict = same action on the same device; clean releases the other binds of the written devices, nuke the joysticks the profile does not use; keyboard lines stay)");
            Console.WriteLine("  engine starcitizen scan [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]");
            Console.WriteLine("  engine starcitizen import [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--merge] [--save-profile <path.pr0file>] [--save-last] [--device <name>] [--devices <csv>] [--ignore-device <name>] [--ignore-devices <csv>] [--include-keyboard true|false] [--confirm true|false]");
            Console.WriteLine("  engine starcitizen export [<sc-root-or-profile-path>] [--path <path>] [--source <path>] [--mode clean|add|merge|merge-overwrite] [--keep-keyboard-defaults true|false] [--prefer-local-order true|false] [--include-keyboard true|false] [--nuke-untouched-devices true|false]   (clean releases the ship binds of the written devices, nuke those of the devices the profile does not use; both switch off the game's default joystick binds on js1 when js1 is written (clean) or unused (nuke))");
            Console.WriteLine("  engine starcitizen list-devices [<sc-root-or-profile-path>] [--path <path>] [--source <path>]   (aliases: devices, list)");
            Console.WriteLine("  engine db summary [--keyboard] [--no-keyboard] [--strict-names]");
            Console.WriteLine("  engine db list [filter] [--missing] [--keyboard] [--no-keyboard] [--strict-names]");
            Console.WriteLine("  engine db check [--keyboard] [--no-keyboard] [--strict-names]");
            Console.WriteLine("  engine db games [filter]");
            Console.WriteLine("  engine db check-updates [--accept <game>] [--accept-all] [--no-record]   (games updated since their DB was built)");
            Console.WriteLine("  engine db planes [filter] [--game <name>]");
            Console.WriteLine("  engine db fingerprint");
            Console.WriteLine("  engine db paths   (where the lists are read from: the game-data folder for everything built from your games, the program folder for JoyPro's own data)");
            Console.WriteLine("  engine db refresh-il2 [<install, data\\input folder or .actions file>] [--path <path>] [--db-path <folder>] [--dry-run]   (adds new actions of the game's key mapping files to DB\\IL2\\IL2.actions; also: engine il2 refresh-db)");
            Console.WriteLine("  engine db refresh-starcitizen [<install-or-source-path>] [--install-path <path>] [--path <path>] [--source <path-to-source>] [--branch <name>] [--db-path <path>] [--include-user-profiles true|false] [--dry-run true|false]");
            Console.WriteLine("  engine games installs [--game <id>]   (which supported games are installed and where; import/export/scan/refresh/restore are refused for the others)");
            Console.WriteLine("  engine games install set <game> <folder> | clear <game>   (your installation folder of a game, e.g. a portable install; it then counts as installed and detection never replaces it; unset = clear)");
            Console.WriteLine("  engine games install disable <game> [<game> ...] | enable <game> [<game> ...]   (games you do not have: they count as not installed and the GUI hides them; enable undoes it)");
            Console.WriteLine("  engine games config set <game> <file or folder> | clear <game>   (your config location of a game, e.g. Saved Games\\DCS.openbeta; it comes first wherever JoyPro looks for the game's controls; not for games that keep them in the installation)");
            Console.WriteLine("  engine games paths [--game <name>] [--games <csv>]");
            Console.WriteLine("  engine version");
            Console.WriteLine("  engine games integrations");
            Console.WriteLine("  engine games support [--markdown | --html | --json] [--notes <file>]   (how far JoyPro supports each game: the feature matrix from the code plus the verification notes, docs/games/support.md; exit 1 when the notes miss a game or claim more than the code)");
            Console.WriteLine("  engine hud devices [--grip <device>=<catalog id>]... [--no-device-photos]   (connected devices for the HUD: product picture address, axes with their role, buttons, hats, layout picture with button positions; --no-device-photos leaves the makers' photo addresses out (the user said never); the engine downloads no device photo, the GUI does per device after the user's yes)");
            Console.WriteLine("  engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]   (bound buttons with modifiers and titles, for the overlay)");
            Console.WriteLine("  engine overlay dcs-hook status|install|remove [--path <Saved Games\\DCS>] [--dry-run]   (Export.lua addition that tells JoyPro the aircraft; backed up)");
            Console.WriteLine("  engine media [games|aircraft|sources] [--no-internet-pictures]   (official pictures: game covers from Steam on this PC, else downloaded once from Steam's store or the publisher's site, else the game's executable icon; DCS aircraft pictures from the modules; sources = the web source per game)");
            Console.WriteLine("  engine backups list [--game <id>] | show <id> | restore <id|latest> [--game <id>] [--dry-run]");
            Console.WriteLine("      every export first copies the game files it changes into a backup set (newest 30 per game); restore puts them back");
            Console.WriteLine("      (files the export created are removed) and backs up the current files first, so it can be undone.");
            Console.WriteLine("  engine games devices   (connected devices with VID:PID, HID name and DirectInput axes/buttons/hats)");
            Console.WriteLine("  engine sync status [--game <id>] [--path <config>]   (per game the config files / registry values JoyPro manages, their fingerprint and whether the game changed them since JoyPro's last export, accept or apply)");
            Console.WriteLine("  engine sync accept --game <id> [--path <config>]   (the current config counts as known; nothing is imported)");
            Console.WriteLine("  engine sync diff --game <id> [--path <config>]   (what the game changed that JoyPro manages, compared with the profile: added, removed, changed-tuning, changed-modifiers, with candidate relations)");
            Console.WriteLine("  engine sync apply --game <id> --choices <file.json> [--path <config>]   (per change id: Target = relation name, \"new\" (+ Name) or \"ignore\"; then the config counts as known)");
            Console.WriteLine("  engine support bundle [--game <id>] [--days <n>] [--list] --out <file.zip>   (the support file: logs of the last days, the games' environment and devices, JoyPro's copies of their small config files, MSFS profiles read; user folder replaced, nothing uploaded; --list only shows the contents)");
            Console.WriteLine("  engine vjoy status   (virtual joystick mode: vJoy and HidHide installed, vJoy devices, HidHide state and JoyPro's session, games native/virtual, the mapping table, what vJoy must offer; read-only)");
            Console.WriteLine("  engine vjoy mode <game> native|virtual|xbox [--dry-run]   (per game: physical devices, the virtual joystick (vJoy, every used input gets a fixed vJoy slot) or the virtual Xbox 360 pad (ViGEmBus, for XInput-only or one-device games: the game's gamepad layout first, then sticks, triggers, buttons, D-pad))");
            Console.WriteLine("  engine vjoy plan [--reset] [--prune]   (the mapping table and the vJoy configuration it needs; nothing is saved)");
            Console.WriteLine("  engine vjoy map [--reset] [--prune] [--dry-run]   (saves the mapping table in the profile; slots never move unless --reset; --prune drops unused slots)");
            Console.WriteLine("  engine vjoy settings [--first-device N] [--last-device N] [--auto-start on|off] [--hide-devices on|off] [--dry-run]   (which vJoy devices JoyPro may use, GUI auto start, HidHide on/off)");
            Console.WriteLine("  engine vjoy exe <game> [--add <path>] [--remove <path>]   (the game executables HidHide blocks in virtual mode: found in the installation + your own)");
            Console.WriteLine("  engine vjoy forwarder-config [--out <file>] [--xbox-game <game>] [--game <game>]   (the mapping table for JoyPro.VJoy.exe, the forwarding process; one Xbox game's pad table; with --game only that game's virtual functions)");
            Console.WriteLine("  engine vjoy functions types   (the virtual function types: axis bands, button cycle, toggle, switch pulses, short/long/double press, shift layer, merge, split, curve, axis ends, axes to hat, buttons to axis, encoder, turbo, macro, hat to buttons, buttons to hat)");
            Console.WriteLine("  engine vjoy functions list [--game <game>]   (the virtual functions per game with inputs, outputs (bind device and input), problems, limit warnings and the physical devices for inputs)");
            Console.WriteLine("  engine vjoy functions add --game <game> --type <type> [--name <name>] [--input role=<device>|<input>]... [--output role=<target>[@<vJoy device>][:hold|enter|leave|both]]... [--param key=value]... [--edges 0.1,0.9] [--count n] [--tuning <axis tuning>] [--keyboard on|off] [--spec <json>|@<file>] [--no-auto-outputs] [--dry-run]   (adds a function; outputs left empty get free vJoy buttons/axes/hats (or Xbox pad controls), reserved in the table)");
            Console.WriteLine("  engine vjoy functions set --game <game> --id <id> [same options as add] [--enabled on|off] [--auto-outputs] [--dry-run]   (changes a function; --spec replaces it)");
            Console.WriteLine("  engine vjoy functions remove --game <game> --id <id> [--dry-run]   (removes a function; its outputs are free again)");
            Console.WriteLine("  engine vjoy functions test (--game <game> --id <id> | --spec <json>|@<file>) [--stream \"<ms>:<role>=<value>; ...\"] [--rate Hz] [--duration ms]   (simulates the function with the forwarder's evaluation and lists every output change; nothing goes to vJoy, the pad or the keyboard)");
            Console.WriteLine("  engine vjoy limits [--game <game>]   (what each game reads of one joystick: buttons, axes, hats, with confidence and source; vJoy offers 128 buttons, 8 axes, 4 hats)");
            Console.WriteLine("  engine vjoy configure-vjoy [--max [--device N]] [--dry-run]   (runs vJoyConfig for the vJoy devices that lack axes, buttons or hats, or with --max sets 8 axes, 128 buttons, 4 hats; one administrator prompt; never shrinks a device; Windows must restart afterwards)");
            Console.WriteLine("  engine vjoy apply-hidhide [--dry-run] [--owner-pid N] [--exe <game.exe>]... [--trusted <program.exe>]...   (hides the mapped physical devices from the virtual-mode games only; one administrator prompt; journal first)");
            Console.WriteLine("  engine vjoy restore-hidhide [--dry-run]   (undoes exactly JoyPro's HidHide changes and restores the previous HidHide state; one administrator prompt)");
            Console.WriteLine("  engine vjoy recover [--dry-run] [--force]   (restores a session whose owner (the GUI) is gone; --force: also while it runs)");
            foreach (Games.IGameIntegration registryGame in Games.GameRegistry.All)
            {
                string gameArea = registryGame.Aliases[0];
                Console.WriteLine("  engine " + gameArea + " paths");
                Console.WriteLine("  engine " + gameArea + " catalog");
                string profileOption = registryGame is Games.IGameProfileSelection ? " [--game-profile <name>]" : string.Empty;
                string disconnectedOption = (registryGame is Games.IGameDisconnectedDevices ? " [--include-disconnected]" : string.Empty) +
                                            (registryGame is Games.IGameSimDefaults ? " [--include-sim-defaults]" : string.Empty);
                Console.WriteLine("  engine " + gameArea + " scan [<config-path>] [--path <config-path>]" + profileOption + disconnectedOption);
                Console.WriteLine("  engine " + gameArea + " import [<config-path>] [--path <config-path>] [--replace] [--include-keyboard]" + profileOption + disconnectedOption +
                                  (registryGame is Games.IGameAircraftScope ? " [--limit-to-profile-aircraft]" : string.Empty) +
                                  (registryGame is Games.IGameDisconnectedDevices ? "   (devices that are not connected only with --include-disconnected)" : string.Empty));
                string aircraftOption = registryGame is Games.IGameAircraftScope ? " [--aircraft <aircraft-key>]" : string.Empty;
                Console.WriteLine("  engine " + gameArea + " export [<config-path>] [--path <config-path>] [--mode clean|add|merge|merge-overwrite] [--dry-run] [--include-keyboard] [--nuke-untouched-devices]" + profileOption + aircraftOption +
                    (registryGame.ExclusiveFlightSim
                        ? "   (flight only: clean takes over the devices it writes, nuke clears unused devices)"
                        : "   (only aircraft/helicopter actions: clean and nuke release those on the written / unused devices)"));
                if (registryGame.SupportsCatalogRefresh)
                    Console.WriteLine("  engine " + gameArea + " refresh-db [<source-path>] [--dry-run]" +
                                      (registryGame is Games.WarThunderIntegration ? " [--no-game-archives]" : string.Empty) +
                                      (registryGame is Games.HllVietnamIntegration ? " [--oodle-dll <path>]" : string.Empty) +
                                      "   (builds the catalog from the installed game, or adds its new actions; writes it only when it changes)" +
                                      (registryGame is Games.WarThunderIntegration ? "; War Thunder: the full list from gui.vromfs.bin, with --no-game-archives only JoyPro's own list" : string.Empty));
                if (registryGame is Games.HllVietnamIntegration)
                    Console.WriteLine("  engine " + gameArea + " oodle [--path <game folder>]   (the game's own Oodle library, or one another installed game ships; refresh-db --oodle-dll <path> uses that one only with the user's consent, from where it is, never copied; without it JoyPro's own list of the pad keys)");
                if (registryGame is Games.VtolVrIntegration)
                    Console.WriteLine("  engine " + gameArea + " mod status|prepare [--accept-license]   (the BYOJoystick mod on this PC: Mod Loader, Workshop item, BYOJoystick.dll with SHA-256 and the targets JoyPro reads; prepare backs up the BYOJ_<seat>.xml files and returns the Steam pages to open (Mod Loader, Workshop item 3276352374); Steam downloads and installs the mod, JoyPro ships none of it; refresh-db then reads the targets from the installed mod)");
                if (registryGame is Games.MsfsIntegration && ((Games.MsfsIntegration)registryGame).Sim == 2024)
                    Console.WriteLine("      export also writes your own profiles the sim uses (Steam remote folder, Microsoft Store save; backup first, never while the sim runs) [--user-profiles true|false] [--import-files]   (--import-files: import files for every device and category, else only where your profile could not be written)");
                if (registryGame is Games.IGameAircraftScope)
                    Console.WriteLine("      refresh-db also lists the installed aircraft in DB\\" + registryGame.DbFolder + "\\aircraft.tsv (engine db planes --game " + registryGame.GameId + "; --node " + registryGame.GameId + ":<action>:<aircraft key> limits a relation to one);" +
                                      " the game's presets apply to every aircraft, so export writes the relations for all aircraft plus, with --aircraft <aircraft-key>, those limited to that aircraft");
            }
            Console.WriteLine("  engine layout render [--path <export-folder>] [--device <name>] [--devices <csv>] [--layout-map <joystick>=<path.layout>] [--game <name>] [--games <csv>] [--plane <name>] [--planes <csv>] [--include-overview true|false] [--include-planes true|false] [--skip-missing-layouts true|false] [--draw-markers true|false] [--draw-legend-table true|false] [--marker-size <n>] [--legend-width <n>] [--legend-max-rows <n>] [--legend-font-size <n>] [--modifier-colors <modifier=#RRGGBB,...>] [--button-symbol circle|square|triangle|diamond|bar] [--axis-symbol circle|square|triangle|diamond|bar] [--pov-symbol circle|square|triangle|diamond|bar] [--slider-symbol circle|square|triangle|diamond|bar] [--font <family>] [--font-size <n>] [--text-color <#RRGGBB|#AARRGGBB>] [--outline-color <color|none>] [--label-background <color|none>] [--style dark|light] [--print-width <px>] [--combine true|false] [--drawing-style lines|cel] [--drawing-preset joypro|manual|blueprint|print|custom] [--drawing-ink <#RRGGBB>] [--drawing-paper <#RRGGBB>] [--drawing-accent <#RRGGBB>]   (a --layout-map to a device layout .json with tables and lines draws JoyPro 1 style sheets; the --drawing-* options paint JoyPro's own drawings, default: the GUI's settings)");
            Console.WriteLine("  engine layout callouts (--model <catalog id> | --layout <device-layout.json>) [--picture <file>] [--schematic] [--drawing-style lines|cel] [--drawing-preset <p>] [--drawing-ink|--drawing-paper|--drawing-accent <#RRGGBB>] [--arrange] [--device <name>] [--game <name> --plane <name>] [--sample] [--png <file>] [--light] [--print-width <px>] [--save <file.json>]   (tables around the device picture with orthogonal lines; --check-all [query] checks every model)");
            Console.WriteLine("  engine layout titles --device <name>   (the game's own menu titles of the device's binds, per game and aircraft)");
            Console.WriteLine("  engine layout image <device> [--set <layout file>|--clear]   (the layout file the profile uses for a device; --clear: JoyPro's picture again, --set puts one back)");
            Console.WriteLine("  engine layout multiview <file.json> [--views <folder>] [--png <file>] [--device <name>] [--game <g> --plane <p>] [--light] [--print-width <px>]   (a page with several views of a device: each view as view-<n>.png, the page as one picture; layout render draws it when --layout-map points to it)");
            Console.WriteLine("  engine layout suggest [--device <name>] [--devices <csv>] [--connected] [--profile-devices] [--legacy-layout-dir <path>] [--legacy-output-dir <path>] [--catalog <path.json>] [--apply true|false] [--apply-min-score <n>] [--backdrop <picture-file-or-image-url>]");
            Console.WriteLine("  engine layout identify [--device <name>] [--devices <csv>] [--connected]   (vendor/model from VID:PID and HID name, picture search link)");
            Console.WriteLine("  engine layout template-inspect --source <path.layout-or-image> [--max-markers <n>]");
            Console.WriteLine("  engine layout template-save --source <path.layout-or-image> --out <path.layout> [--markers-json <file.json> | --markers <json-array-or-object>] [--tables-json <file.json> | --tables <json-array-or-object>] [--connectors-json <file.json> | --connectors <json-array-or-object>] [--replace-all true|false]");
            Console.WriteLine("  engine devices list [--connected] [--profile-devices]");
            Console.WriteLine("  engine devices inputs [--include-off]   (aliases: catalog, input-catalog)");
            Console.WriteLine("  engine devices exchange <old-device> <new-device> [--old <name>] [--new <name>] [--confirm true|false]   (alias: swap; renames the device, inputs stay)");
            Console.WriteLine("  engine devices replace <old-device> <new-device> [--mode auto|same|layout] [--map OLD=NEW]... [--merge add|keep|replace] [--grip <device>=<grip id>]... [--dry-run]   (moves binds and modifiers input by input: same ids, or by layout from the device catalog; --dry-run previews)");
            Console.WriteLine("  engine devices auto-remap [--dry-run]   (alias: remap; a profile device that is not connected gets the connected device of the same product with a new GUID)");
            Console.WriteLine("  engine devices remove <device-name> [--device <name>] [--confirm true|false]   (aliases: purge, clear)");
            Console.WriteLine("  engine devices alias list | set <device> <alias> | clear <device>   (a name of your own for a device; display only, exports use the real device)");
            Console.WriteLine("  engine relations list [filter]");
            Console.WriteLine("  engine relations show <relation-name>");
            Console.WriteLine("  engine relations add <name> [--axis|--button|--type axis|button] [--node game:id[:plane1|plane2]] [--from relation] [--group g] [--device d --input i --modifier m]");
            Console.WriteLine("  engine relations group list [relation-name]");
            Console.WriteLine("  engine relations group add <relation-name> <group1> [group2] [...]");
            Console.WriteLine("  engine relations group remove <relation-name> <group1> [group2] [...] [--all]");
            Console.WriteLine("  engine relations group rename <old-group> <new-group>");
            Console.WriteLine("  engine relations item add <relation-name> --node game:id[:plane1|plane2] [--node ...]  (or --id/--game/--planes)");
            Console.WriteLine("  engine relations item remove <relation-name> --node game:id[:plane1|plane2] [--node ...] [--all]");
            Console.WriteLine("  engine relations item enable <relation-name> --node game:id[:plane1|plane2] [--node ...]");
            Console.WriteLine("  engine relations item disable <relation-name> --node game:id[:plane1|plane2] [--node ...]");
            Console.WriteLine("  engine relations mass delete [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass duplicate [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass merge <name> [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass split <postfix> [--plane <game:plane|plane>] [--planes <csv>] [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass add-prefix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass add-postfix <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass remove-part <value> [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations mass export <path.rl|path.json> [--format rl|json] [--relation <name>] [--relations <csv>] [--query <text>] [--all]");
            Console.WriteLine("  engine relations export <path.json>");
            Console.WriteLine("  engine relations import <path.json>");
            Console.WriteLine("  engine relations edit   (no args; exports temp relations JSON, opens editor, re-imports on close; uses --editor or notepad)");
            Console.WriteLine("  engine relations suggest <relation>|--relation <relation> [--min high|medium|low] [--limit N] [--game g1|g2] | --all [--missing [--per-relation N]] [--min ...]");
            Console.WriteLine("      proposes what a relation is still missing, each with the reason (\"Same function as F-16C_50 'CMS Aft'\"): the");
            Console.WriteLine("      functions of its commands (function catalog, weighted by how many agree), your style (what you put there when an");
            Console.WriteLine("      aircraft lacks the function), the function map (\"Same function: Throttle axis\", also where the catalog has no");
            Console.WriteLine("      entry), the same DCS command id in more aircraft, similar titles in any game. Also lists what");
            Console.WriteLine("      you dismissed. Each suggestion says what the relation lacks (Missing: game / aircraft, NewPlanes). --all lists");
            Console.WriteLine("      every relation with suggestions; --all --missing per relation the free actions of games and aircraft it lacks.");
            Console.WriteLine("  engine relations suggest apply <relation> [--add game:id:plane1|plane2]... [--never game:id:plane1|plane2]... [--remove game:id:planes]... [--move game:id:planes=>relation]... [--take game:id:planes]...");
            Console.WriteLine("      adds the accepted commands; --never dismisses them (added switched off, never proposed again; --remove restores);");
            Console.WriteLine("      --take moves a command another relation already uses into this one; --remove / --move fix entries that look wrong.");
            Console.WriteLine("  engine relations search \"<words>\" [--game g1|g2] [--plane p1|p2] [--category <name>] [--axis|--button] [--limit N] [--per-game N] [--batch <file>]");
            Console.WriteLine("      finds actions of every game and aircraft through the function map (DB/Functions/function-map.json): names,");
            Console.WriteLine("      synonyms and game terms (\"throttle\" = thrust = collective = strafe longitudinal), what a query describes (\"make");
            Console.WriteLine("      the plane go faster\"), a direction (\"gear up\"), typos (\"did you mean\") and plain title words. Ranked exact title,");
            Console.WriteLine("      function, synonym, described, words; grouped by game and function; every game keeps its best rows within --limit.");
            Console.WriteLine("      --category alone lists a whole category of a game's list; --batch runs one query per line (JSON array).");
            Console.WriteLine("  engine relations browse [--game <game> [--plane p1|p2]] [--axis|--button]   (alias: relations catalog)");
            Console.WriteLine("      what exists: every game with its aircraft; with a game (DCS: and aircraft) every action of its list by the");
            Console.WriteLine("      game's own categories (games without: JoyPro's function categories), with the relations that use each.");
            Console.WriteLine("  engine relations resolve \"<query>\" [--game g1|g2] [--plane p1|p2] [--axis|--button] [--per-game N]");
            Console.WriteLine("      reads a query as function ids (with direction) and lists the matching actions of every game, grouped by game.");
            Console.WriteLine("  engine relations functions [--category <id>] [--query \"<words>\"] [--coverage] [--dump <file.tsv>]");
            Console.WriteLine("      lists the function map: ids, names, synonyms, game terms, slots and how many actions of each game are each");
            Console.WriteLine("      function; --coverage per game, --dump every action with its functions.");
            Console.WriteLine("  engine relations chat <relation>|--relation <relation> \"<message>\" [--llm off|auto|on] [--show-prompt] [--apply]");
            Console.WriteLine("      changes one relation from a sentence (\"add flare release for all jets\", \"remove the helicopters\", \"also War Thunder\"):");
            Console.WriteLine("      the offline rules and the relation's suggestions give numbered candidates, the optional local model picks ids from");
            Console.WriteLine("      them, JoyPro checks every id and prints the proposed adds and removes (invalid ids rejected). --apply applies them.");
            Console.WriteLine("  engine relations assistant \"<message>\" [--relation <name>] [--llm auto|on|off] [--max-steps N] [--show-prompt]");
            Console.WriteLine("      the JoyPro assistant: creates, fills, renames and trims relations from a sentence and applies it at once. With the");
            Console.WriteLine("      local AI model it works through tools (resolve, search, suggest, create, add, remove, rename; every id checked);");
            Console.WriteLine("      without it simple commands work: \"Throttle ALL: all throttle axes\", \"add the collectives to Throttle\",");
            Console.WriteLine("      \"remove War Thunder from Throttle\", \"rename Throttle to Throttle ALL\". --show-prompt prints the prompt and grammar only.");
            Console.WriteLine("  engine relations rename <relation> <new name>");
            Console.WriteLine("  engine relations function <relation> [--set <function>|--none|--auto]");
            Console.WriteLine("      the function the relation stands for: set (picked), named, covered (at least 80% of its known actions), mixed");
            Console.WriteLine("      (a button position: each aircraft its own function, no function-based suggestions) or unknown. --set picks a");
            Console.WriteLine("      catalog function (id or name), --none says it has none, --auto goes back to automatic.");
            Console.WriteLine("  engine relations function --list [--axis|--button] [--query text]   the catalog functions to pick from");
            Console.WriteLine("  engine relations aircraft <relation> [--node game:id]");
            Console.WriteLine("      every action of the relation with every aircraft that has its id: on / off / not in it, each aircraft's own title");
            Console.WriteLine("      and category of the id and same / near / differs to the action's meaning (\"82 of 84 aircraft\").");
            Console.WriteLine("  engine relations aircraft set <relation> --node game:id [--on p1|p2] [--off p3|p4] [--only p1|p2]");
            Console.WriteLine("      one action's aircraft selection in one step (one undo step); --on also adds aircraft to it.");
            Console.WriteLine("  engine relations check-aircraft [--relation <name>] [--apply] [--include-same-function]");
            Console.WriteLine("      actions with switched-on aircraft where the same id means something else than in the others (\"Pitch\" in jets,");
            Console.WriteLine("      \"Collective\" in a helicopter); --apply switches those aircraft off (they stay listed, one tick brings one back).");
            Console.WriteLine("  engine relations meanings [--game g] [--top N]");
            Console.WriteLine("      per game: ids shared by several aircraft lists and how many of them mean something else in some aircraft.");
            Console.WriteLine("  engine relations action-info --coverage [--game g]");
            Console.WriteLine("      per game: actions with a readable name, with an explanation (cockpit guide / the game's own / general) and functions with a recommended action.");
            Console.WriteLine("  engine relations auto-group [--preset all|wing|era|purist] [--no-substitutes] [--planes p1|p2|Game] [--replace] [--dry-run] [--no-map]");
            Console.WriteLine("      groups the same function of all DCS aircraft and the other games into relations from DB/Functions (manual-verified catalog);");
            Console.WriteLine("      all = one relation per function, wing = split fixed/rotary wing, era = split by wing type and era,");
            Console.WriteLine("      purist = only basic flight controls shared. Existing relations of the same name get the missing aircraft,");
            Console.WriteLine("      commands already used in another relation stay there; --replace first removes relations tagged AutoGroup.");
            Console.WriteLine("      The function map adds the actions, aircraft and games the catalog does not list (--no-map: catalog only).");
            Console.WriteLine("  engine relations from-text \"<request>\" [--games g1|g2] [--planes p1|p2] [--llm off|auto|on] [--substitutes] [--select <relation>]... [--skip-node game:id:plane1|plane2]... [--name <name>] [--min-confidence 0..1] [--dry-run]");
            Console.WriteLine("      group by description: \"speed brake in and out on all jets\", \"gear up/down of every DCS warbird\", \"flaps for all MSFS airliners and");
            Console.WriteLine("      IL-2 bombers\". Offline rules (DB/Functions/language.json) pick the functions and aircraft, the function catalog gives");
            Console.WriteLine("      the commands; each node has a confidence and an explanation. --llm on|auto lets the optional local model re-pick");
            Console.WriteLine("      from the offline candidates (default auto when enabled); --select / --skip-node create only what you ticked.");
            Console.WriteLine("      Commands already in another relation stay there.");
            Console.WriteLine("  engine relations from-text --check   (tests the vocabulary in DB/Functions/language.json against the catalog)");
            Console.WriteLine("  engine relations from-text \"<request>\" --show-prompt [--prompt-model <model>|local]");
            Console.WriteLine("      shows the prompt and grammar the local model would get (candidate functions with what they do, aircraft); no model runs.");
            Console.WriteLine("  engine llm status|recommend [--ram-gb N] [--vram-gb N] [--gpu <name>] [--gpu-vendor nvidia|amd|intel] [--cores N] [--avx2 true|false]");
            Console.WriteLine("      hardware, the model JoyPro recommends for it (size, quantization, GPU or CPU), downloaded models and runtime;");
            Console.WriteLine("      the options replace detected values (what-if).");
            Console.WriteLine("  engine llm download [<model>] --accept-license [--runtime auto|cpu|vulkan|cuda] [--no-runtime]");
            Console.WriteLine("      downloads the model (Hugging Face, pinned revision, SHA-256) and llama.cpp's llama-server (release SHA-256) into");
            Console.WriteLine("      <data>/Llm and switches the model on; resumable; without --accept-license it only shows size and licenses.");
            Console.WriteLine("      auto = Vulkan build with a graphics card, else CPU; cuda = NVIDIA build plus CUDA runtime. engine llm cancel stops it.");
            Console.WriteLine("  engine llm remove [<model>] [--all]   (--all also removes the runtime and switches the model off)");
            Console.WriteLine("  engine llm enable [<model>|local] | engine llm disable   (use the local model for relations from-text)");
            Console.WriteLine("  engine llm cancel   (stops a running download; the next download resumes it)");
            Console.WriteLine("  engine llm find-local [--root <folder>]...");
            Console.WriteLine("      looks for a llama-server.exe and GGUF models already on this PC (PATH, llama.cpp/AI/models folders, Hugging Face");
            Console.WriteLine("      cache, LM Studio, Downloads; read-only) and names the pair that suits the hardware.");
            Console.WriteLine("  engine llm use-local [--server <llama-server.exe>] [--model <file.gguf>] [--gpu-layers auto|all|N] [--context N] [--template server|chatml] | --clear");
            Console.WriteLine("      uses your own llama.cpp and GGUF instead of a download (without paths: what find-local recommends) and switches");
            Console.WriteLine("      the model on; the server applies the GGUF's chat template. --clear forgets them.");
            Console.WriteLine("  engine llm start   (loads the chosen model now; started from JoyPro it stays loaded until the idle stop)");
            Console.WriteLine("  engine llm server | engine llm stop   (what runs now / end it and free its memory)");
            Console.WriteLine("  engine llm fake-server [--port N] [--port-file <path>] [--response <file>] [--serve <folder>] [--log <file>] [--lifetime <s>]");
            Console.WriteLine("      test stub that answers like llama-server and serves files with HTTP Range (no model involved).");
            Console.WriteLine("  engine setup devices [--grip <device>=<grip id>]... [--role <device|VID:PID>=<role>]... [--add-device <catalog id>]...");
            Console.WriteLine("      Setup wizard (docs/setup-wizard.md): the connected devices with the proposed role (main-stick, heli-stick, left-stick,");
            Console.WriteLine("      throttle, collective, pedals, panel, keyboard-joystick, virtual, unused) and the rules the plan follows");
            Console.WriteLine("  engine setup aircraft --games <g1,g2>   (the aircraft / games for the priority list)");
            Console.WriteLine("  engine setup plan --games <g1,g2> [--priorities \"A>B|C>D\"] [--priority \"B|C\"]... [--modifiers 0..8] [--prefer-fewer|--use-all-modifiers]");
            Console.WriteLine("      [--realism 0..1] [--seed N] [--replace-existing] [--role ...] [--grip ...] [--add-device ...] [--cockpit-file <json>]... [--out-plan <plan.json>]");
            Console.WriteLine("      relations and binds for the important functions of the games' aircraft: priorities ('>' next, '|' equal), modifier budget");
            Console.WriteLine("      (more only when the inputs are too few), realism 0 = group by function, 1 = like the real HOTAS (cockpit database or");
            Console.WriteLine("      knowledge map); deterministic. Writes nothing to the profile");
            Console.WriteLine("  engine setup apply --plan <plan.json> [--dry-run] [--force]   (writes the plan's relations, binds and modifiers in one step)");
            Console.WriteLine("  engine setup render --plan <plan.json> --out-dir <folder> [--aircraft <name>] [--light] [--width <px>]   (a callout sheet per device)");
            Console.WriteLine("  engine relations auto-assign [--grip <device|VID:PID>=<grip id>]... [--add-device <catalog id>]... [--ignore-device <device>]... [--relation <name>]... [--max-modifiers 0|1|2] [--overwrite [--include-missing]] [--no-style] [--explain <relation>]... [--dry-run]");
            Console.WriteLine("      binds the relations to the best physical controls of your devices (device catalog DB/Devices, function positions");
            Console.WriteLine("      DB/Functions/slot_controls.tsv); existing binds stay unless --overwrite (binds on the keyboard, vJoy and devices");
            Console.WriteLine("      not connected stay even then; --include-missing moves the latter); at most 2 modifier layers. One device");
            Console.WriteLine("      per hand role (a second right-hand stick is left out); with --overwrite the controls used before win unless --no-style;");
            Console.WriteLine("      --explain lists the best candidates of a relation and what occupies them.");
            Console.WriteLine("  engine devices kb [list [query] | show <id> | identify [--grip <device>=<grip id>]... [--add-device <id>]...]   (device catalog for Auto Assign)");
            Console.WriteLine("  engine devices reference [list [query] | identify --vidpid <VID:PID> --name <HID name> [--instance <name>] [--buttons <n>] [--hats <n>] [--axes X,Y,RZ] | show <id> [--base <id>] [--svg <file>] [--light] [--no-badges] | check [query] | photos [query] [--coverage] | photo-add --model <id> --url <https photo on the maker's host> --view <front|side|back|top|base|three-quarter|sheet> [--page <url>] [--confidence <c>] [--shows a,b] [--sha256 <hex>] | layout-check | drawings [query] [--check] [--model <id> [--view <v>] --svg <file> | --png <file>] [--light] [--drawing-style lines|cel] [--drawing-preset <p>] | completeness [query] [--missing] [--list <file>] | mesh-render --aircraft <plane> --grip stick|throttle [--view <yaw>,<pitch>] [--size <px>] [--skip <regex>] --folder <dir>]   (device reference pictures: catalog, identification and the question to ask, JoyPro's schematic with markers, photo references, curated marker positions, JoyPro's own drawings of the devices, whether every input has a position, a game's grip rendered for a drawing)");
            Console.WriteLine("  engine cockpits [list [query] [--game G] [--era E] [--role R] [--nation N] [--category fixed-wing|helicopter|space|fiction|real] | search <text> [--limit n] | get <aircraft> [<variant>] [--json] | check | render <aircraft> [<variant>] --png <file> [--device <d>] [--style dark|light] [--print-width <px>] [--drawing-only] | compare <aircraft> [<variant>] [--game G] | photos [query] [--coverage] | coverage | drawings | pictures build [--game DCS|XPlane12|MSFS2020|IL2Game|NuclearOption|all] [--aircraft <name>,...] [--force] [--out <folder>] | pictures status | pictures check [--game G] [--aircraft <name>,...] [--write] [--drop]]");
            Console.WriteLine("      the cockpit database (DB/Cockpits, docs/cockpits.md): stick and throttle of real and fictional aircraft per variant with");
            Console.WriteLine("      their functions and JoyPro function ids, JoyPro's drawings, panels, sources and photo links; get = the layout the setup");
            Console.WriteLine("      wizard takes over; compare (with --profile) = which functions the profile has relations for.");
            Console.WriteLine("  engine binds list [filter]");
            Console.WriteLine("  engine binds show <bind-or-relation>");
            Console.WriteLine("  engine binds set <bind-or-relation> [--device <name>] [--input <value>] [--format dcs|il2] [--modifier <name>] [--clear-modifiers] [--clear-input] [--inverted true|false] [--slider true|false] [--curvature <csv>] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--double-tap true|false]");
            Console.WriteLine("  engine binds clear <bind-or-relation> [--input-only]");
            Console.WriteLine("  engine binds modifier list [bind-or-relation]");
            Console.WriteLine("  engine binds modifier add <bind-or-relation> <mod1> [mod2] [...]");
            Console.WriteLine("  engine binds modifier remove <bind-or-relation> <mod1> [mod2] [...] [--all]");
            Console.WriteLine("  engine binds modifier rename <old-modifier> <new-modifier> [bind-or-relation]");
            Console.WriteLine("  engine ffb models");
            Console.WriteLine("  engine ffb show [--device <device>] [--game <game>] [--plane <plane>]");
            Console.WriteLine("  engine ffb set --device <device>|* --game <game> [--plane <plane>] --param <name>=<value>...");
            Console.WriteLine("  engine ffb clear --device <device>|* --game <game> [--plane <plane>] [--param <name>]...");
            Console.WriteLine("  engine ffb telemetry status   (JoyPro's telemetry force feedback: settings, game setup, process, last status)");
            Console.WriteLine("  engine ffb telemetry effects");
            Console.WriteLine("  engine ffb telemetry config show");
            Console.WriteLine("  engine ffb telemetry config set [--game DCS|IL2Game|IL2Korea|WarThunder] [--plane <aircraft>] [--effect <name>] | [--device {guid}] --param <key>=<value>...");
            Console.WriteLine("  engine ffb telemetry config clear [--game <game>] [--plane <aircraft>] [--effect <name>] | [--device {guid}]");
            Console.WriteLine("  engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path <Saved Games\\DCS | IL-2 folder | startup.cfg>]   (DCS Hooks script / IL-2 startup.cfg telemetry target; backed up)");
            Console.WriteLine("  engine telemetry games   (telemetry recording, docs/telemetry-recording.md: the games, what JoyPro listens to, ACMI possible)");
            Console.WriteLine("  engine telemetry config show");
            Console.WriteLine("  engine telemetry config set [--game <game>] --param enabled|folder|max-total-mb|keep-days|sample-hz|keep-raw|session-timeout=<value>...");
            Console.WriteLine("  engine telemetry sessions [--game <game>] [--folder <folder>]   (recorded sessions, newest first)");
            Console.WriteLine("  engine telemetry show <session> [--folder <folder>]   (<session> = path, file name, latest or a number of the list)");
            Console.WriteLine("  engine telemetry export <session> --format acmi|acmi-zip|csv [--to <file or folder>] [--max-hz <n>] [--no-per-channel]   (Tacview ACMI 2.2 / CSV)");
            Console.WriteLine("  engine telemetry preview <session> [--channel <key>]... [--points <n>]");
            Console.WriteLine("  engine telemetry delete <session>...");
            Console.WriteLine("  engine telemetry recover | prune   (finish sessions cut off by a crash | apply the retention settings)");
            Console.WriteLine("  engine telemetry record --game <game> [--seconds <n>] [--folder <folder>] [--sample-hz <n>] [--no-raw] [--no-game-check]   (records now; Ctrl+C ends it)");
            Console.WriteLine("  engine binds tuning models");
            Console.WriteLine("  engine binds tuning show <bind-or-relation> [--game <game>] [--plane <plane>]");
            Console.WriteLine("  engine binds tuning set <bind-or-relation> --scope base|game:<game>|plane:<game>:<plane>|family:<family> [--param <name>=<value>]... [--inverted true|false] [--slider true|false] [--deadzone <value>] [--saturation-x <value>] [--saturation-y <value>] [--curvature <csv>] [--curve-points <x:y,...>|none]");
            Console.WriteLine("  engine binds tuning clear <bind-or-relation> --scope game:<game>|plane:<game>:<plane>|family:<family>");
            Console.WriteLine("  engine binds tuning preview <bind-or-relation> --game <game> [--plane <plane>] [--points <n>]");
            Console.WriteLine("  engine binds detent show <bind-or-relation>   (detent mode: settings, the device axis calibration, the aircraft grouped by afterburner start, per game the curve it gets and how exactly)");
            Console.WriteLine("  engine binds detent set <bind-or-relation> [--on|--off] [--no-ab full|linear] [--unknown full|linear] [--margin <0..0.1>] [--target <game>:<plane>=ab:<0..1>|full|linear|default]...");
            Console.WriteLine("  engine binds detent calibrate <bind-or-relation> --idle <0..1> --detent <0..1> [--full <0..1>|none] [--cutoff <0..1>|none] [--source wizard|manual]   (raw axis positions of the bound device axis)");
            Console.WriteLine("  engine binds detent uncalibrate <bind-or-relation>");
            Console.WriteLine("  engine binds detent gates [--game <game>] [--plane <plane>]   (the shipped throttle gate data: afterburner start per aircraft, source, confidence)");
            Console.WriteLine("  engine binds detent merge [--relations <csv>] [--into <relation>] [--ab-source profile|game] [--apply]   (per-aircraft throttle relations of one device axis into one relation in detent mode; without --apply only the plan)");
            Console.WriteLine("  engine binds unbind <device::input|input> [--device <name>] [--input <value>] [--format dcs|il2] [--game DCS|IL2Game] [--all]");
            Console.WriteLine("  engine binds export <path.json>");
            Console.WriteLine("  engine binds import <path.json>");
            Console.WriteLine("  engine binds edit   (no args; exports temp binds JSON, opens editor, re-imports on close; uses --editor or notepad)");
        }

        private enum ProfileMergeMode
        {
            Left,
            Right,
            Ask
        }

        private sealed class ProfileMergeData
        {
            public Dictionary<string, Relation> Relations { get; set; }
            public Dictionary<string, Bind> Binds { get; set; }
            public Dictionary<string, string> JoystickAliases { get; set; }
            public Dictionary<string, string> JoystickFileImages { get; set; }
            public string JoystickLayoutExport { get; set; }
            public Dictionary<string, Dictionary<string, string>> PlaneAliases { get; set; }
            public Dictionary<string, string> JoysticksPGuids { get; set; }
            public Dictionary<string, string> ModifierNameChanges { get; set; }
            public Dictionary<string, ForceFeedbackS> JoystickFfb { get; set; }
            public Dictionary<string, string> FfbOverrides { get; set; }
            public string LastSelectedDcsInstance { get; set; }

            public ProfileMergeData()
            {
                Relations = new Dictionary<string, Relation>(StringComparer.OrdinalIgnoreCase);
                Binds = new Dictionary<string, Bind>(StringComparer.OrdinalIgnoreCase);
                JoystickAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                JoystickFileImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                JoystickLayoutExport = string.Empty;
                PlaneAliases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                JoysticksPGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ModifierNameChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                JoystickFfb = new Dictionary<string, ForceFeedbackS>(StringComparer.OrdinalIgnoreCase);
                FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                LastSelectedDcsInstance = string.Empty;
            }
        }

        private sealed class ProfileMergeResult
        {
            public string Mode { get; set; }
            public List<string> Sources { get; set; }
            public int Conflicts { get; set; }
            public int ResolvedLeft { get; set; }
            public int ResolvedRight { get; set; }

            public ProfileMergeResult()
            {
                Mode = "right";
                Sources = new List<string>();
                Conflicts = 0;
                ResolvedLeft = 0;
                ResolvedRight = 0;
            }
        }

        private sealed class ProfileValidationResult
        {
            public bool FailOnWarnings { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> Errors { get; set; }
            public List<string> RelationWarnings { get; set; }
            public List<string> BindErrors { get; set; }
            public List<string> ModifierErrors { get; set; }
            public List<string> DuplicateActiveErrors { get; set; }

            public int WarningCount
            {
                get
                {
                    return Warnings == null ? 0 : Warnings.Count;
                }
            }

            public int ErrorCount
            {
                get
                {
                    return Errors == null ? 0 : Errors.Count;
                }
            }

            public bool Passed
            {
                get
                {
                    if (ErrorCount > 0)
                        return false;
                    if (FailOnWarnings && WarningCount > 0)
                        return false;
                    return true;
                }
            }

            public ProfileValidationResult()
            {
                FailOnWarnings = false;
                Warnings = new List<string>();
                Errors = new List<string>();
                RelationWarnings = new List<string>();
                BindErrors = new List<string>();
                ModifierErrors = new List<string>();
                DuplicateActiveErrors = new List<string>();
            }
        }

        private sealed class DcsFfbEntry
        {
            public string Device { get; set; }
            public bool HasSetting { get; set; }
            public bool InvertX { get; set; }
            public bool InvertY { get; set; }
            public bool SwapAxis { get; set; }
            public bool Connected { get; set; }
            public bool Profile { get; set; }

            public DcsFfbEntry()
            {
                Device = string.Empty;
                HasSetting = false;
                InvertX = false;
                InvertY = false;
                SwapAxis = false;
                Connected = false;
                Profile = false;
            }
        }

        private sealed class DcsFfbListResult
        {
            public List<DcsFfbEntry> Devices { get; set; }
            public bool ConnectedOnly { get; set; }
            public bool ProfileOnly { get; set; }
            public List<string> Warnings { get; set; }

            public DcsFfbListResult()
            {
                Devices = new List<DcsFfbEntry>();
                ConnectedOnly = false;
                ProfileOnly = false;
                Warnings = new List<string>();
            }
        }

        private sealed class DcsFfbMutationResult
        {
            public string Action { get; set; }
            public bool All { get; set; }
            public List<string> Devices { get; set; }
            public int Updated { get; set; }
            public int Removed { get; set; }
            public bool InvertXProvided { get; set; }
            public bool InvertYProvided { get; set; }
            public bool SwapAxisProvided { get; set; }
            public bool InvertX { get; set; }
            public bool InvertY { get; set; }
            public bool SwapAxis { get; set; }
            public List<string> Warnings { get; set; }

            public DcsFfbMutationResult()
            {
                Action = string.Empty;
                All = false;
                Devices = new List<string>();
                Updated = 0;
                Removed = 0;
                InvertXProvided = false;
                InvertYProvided = false;
                SwapAxisProvided = false;
                InvertX = false;
                InvertY = false;
                SwapAxis = false;
                Warnings = new List<string>();
            }
        }

        private sealed class DcsStickDefaultsResult
        {
            public string Action { get; set; }
            public string SavedGamesPath { get; set; }
            public string InstallPath { get; set; }
            public string AircraftRoot { get; set; }
            public string BackupPath { get; set; }
            public int AircraftScanned { get; set; }
            public int JoystickFoldersScanned { get; set; }
            public int DefaultFilesCopied { get; set; }
            public int StickSpecificFilesMoved { get; set; }
            public int FilesRestored { get; set; }
            public int DirectoriesTouched { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> Errors { get; set; }

            public DcsStickDefaultsResult()
            {
                Action = string.Empty;
                SavedGamesPath = string.Empty;
                InstallPath = string.Empty;
                AircraftRoot = string.Empty;
                BackupPath = string.Empty;
                AircraftScanned = 0;
                JoystickFoldersScanned = 0;
                DefaultFilesCopied = 0;
                StickSpecificFilesMoved = 0;
                FilesRestored = 0;
                DirectoriesTouched = 0;
                Warnings = new List<string>();
                Errors = new List<string>();
            }
        }

        private sealed class DeviceInputCatalogResult
        {
            public List<string> JoystickAxes { get; set; }
            public List<string> JoystickButtons { get; set; }
            public List<string> JoystickPovButtons { get; set; }
            public List<string> JoystickOffVariants { get; set; }
            public List<string> KeyboardDirectInput { get; set; }
            public List<string> KeyboardDcs { get; set; }
            public List<string> KeyboardIl2 { get; set; }
            public List<string> Warnings { get; set; }

            public DeviceInputCatalogResult()
            {
                JoystickAxes = new List<string>();
                JoystickButtons = new List<string>();
                JoystickPovButtons = new List<string>();
                JoystickOffVariants = new List<string>();
                KeyboardDirectInput = new List<string>();
                KeyboardDcs = new List<string>();
                KeyboardIl2 = new List<string>();
                Warnings = new List<string>();
            }
        }

        private sealed class DeviceListResult
        {
            public List<string> Connected { get; set; }
            public List<string> Profile { get; set; }
            public List<string> OnlyConnected { get; set; }
            public List<string> OnlyProfile { get; set; }
            public List<string> Warnings { get; set; }

            public DeviceListResult()
            {
                Connected = new List<string>();
                Profile = new List<string>();
                OnlyConnected = new List<string>();
                OnlyProfile = new List<string>();
                Warnings = new List<string>();
            }
        }

        private sealed class StarCitizenDeviceListResult
        {
            public string RootPath { get; set; }
            public string SourcePath { get; set; }
            public List<string> Devices { get; set; }
            public List<string> Warnings { get; set; }

            public StarCitizenDeviceListResult()
            {
                RootPath = string.Empty;
                SourcePath = string.Empty;
                Devices = new List<string>();
                Warnings = new List<string>();
            }
        }

        private sealed class DeviceExchangeResult
        {
            public string RequestedOldDevice { get; set; }
            public string RequestedNewDevice { get; set; }
            public string OldDevice { get; set; }
            public string NewDevice { get; set; }
            public bool ConfirmSuggestions { get; set; }
            public int UpdatedBindDevices { get; set; }
            public int UpdatedBindBackups { get; set; }
            public int UpdatedBindAliases { get; set; }
            public int UpdatedBindReformers { get; set; }
            public int UpdatedModifiers { get; set; }
            public int UpdatedJoystickAliases { get; set; }
            public int UpdatedJoystickImages { get; set; }
            public int UpdatedJoystickPGuids { get; set; }
            public int UpdatedJoystickFfb { get; set; }
            public int UpdatedJoystickActivity { get; set; }
            public int UpdatedLocalJoysticks { get; set; }
            public List<string> UpdatedRelations { get; set; }
            public List<string> Warnings { get; set; }

            public int TotalChanges
            {
                get
                {
                    return UpdatedBindDevices +
                           UpdatedBindBackups +
                           UpdatedBindAliases +
                           UpdatedBindReformers +
                           UpdatedModifiers +
                           UpdatedJoystickAliases +
                           UpdatedJoystickImages +
                           UpdatedJoystickPGuids +
                           UpdatedJoystickFfb +
                           UpdatedJoystickActivity +
                           UpdatedLocalJoysticks;
                }
            }

            public DeviceExchangeResult()
            {
                RequestedOldDevice = string.Empty;
                RequestedNewDevice = string.Empty;
                OldDevice = string.Empty;
                NewDevice = string.Empty;
                ConfirmSuggestions = true;
                UpdatedBindDevices = 0;
                UpdatedBindBackups = 0;
                UpdatedBindAliases = 0;
                UpdatedBindReformers = 0;
                UpdatedModifiers = 0;
                UpdatedJoystickAliases = 0;
                UpdatedJoystickImages = 0;
                UpdatedJoystickPGuids = 0;
                UpdatedJoystickFfb = 0;
                UpdatedJoystickActivity = 0;
                UpdatedLocalJoysticks = 0;
                UpdatedRelations = new List<string>();
                Warnings = new List<string>();
            }
        }

        private sealed class DeviceRemoveResult
        {
            public string RequestedDevice { get; set; }
            public string Device { get; set; }
            public bool ConfirmSuggestions { get; set; }
            public int RemovedBinds { get; set; }
            public int ClearedBindBackups { get; set; }
            public int ClearedBindAliases { get; set; }
            public int RemovedBindReformers { get; set; }
            public int RemovedModifiers { get; set; }
            public int RemovedJoystickAliases { get; set; }
            public int RemovedJoystickImages { get; set; }
            public int RemovedJoystickPGuids { get; set; }
            public int RemovedJoystickFfb { get; set; }
            public int RemovedJoystickActivity { get; set; }
            public int RemovedLocalJoysticks { get; set; }
            public List<string> UpdatedRelations { get; set; }
            public List<string> Warnings { get; set; }

            public int TotalChanges
            {
                get
                {
                    return RemovedBinds +
                           ClearedBindBackups +
                           ClearedBindAliases +
                           RemovedBindReformers +
                           RemovedModifiers +
                           RemovedJoystickAliases +
                           RemovedJoystickImages +
                           RemovedJoystickPGuids +
                           RemovedJoystickFfb +
                           RemovedJoystickActivity +
                           RemovedLocalJoysticks;
                }
            }

            public DeviceRemoveResult()
            {
                RequestedDevice = string.Empty;
                Device = string.Empty;
                ConfirmSuggestions = true;
                RemovedBinds = 0;
                ClearedBindBackups = 0;
                ClearedBindAliases = 0;
                RemovedBindReformers = 0;
                RemovedModifiers = 0;
                RemovedJoystickAliases = 0;
                RemovedJoystickImages = 0;
                RemovedJoystickPGuids = 0;
                RemovedJoystickFfb = 0;
                RemovedJoystickActivity = 0;
                RemovedLocalJoysticks = 0;
                UpdatedRelations = new List<string>();
                Warnings = new List<string>();
            }
        }

        private sealed class BindUnbindResult
        {
            public string Input { get; set; }
            public string NormalizedInput { get; set; }
            public string Format { get; set; }
            public string DeviceFilter { get; set; }
            public string GameFilter { get; set; }
            public List<string> UpdatedRelations { get; set; }

            public BindUnbindResult()
            {
                Input = string.Empty;
                NormalizedInput = string.Empty;
                Format = string.Empty;
                DeviceFilter = string.Empty;
                GameFilter = string.Empty;
                UpdatedRelations = new List<string>();
            }
        }

        private sealed class BindClearResult
        {
            public string Relation { get; set; }
            public bool InputOnly { get; set; }
            public bool HadBind { get; set; }
            public bool ClearedInput { get; set; }
            public bool RemovedBind { get; set; }

            public BindClearResult()
            {
                Relation = string.Empty;
                InputOnly = false;
                HadBind = false;
                ClearedInput = false;
                RemovedBind = false;
            }
        }

        private sealed class RelationGroupListResult
        {
            public string Scope { get; set; }
            public string Relation { get; set; }
            public List<string> Groups { get; set; }
            public List<RelationGroupListEntry> Entries { get; set; }

            public RelationGroupListResult()
            {
                Scope = "global";
                Relation = string.Empty;
                Groups = new List<string>();
                Entries = new List<RelationGroupListEntry>();
            }
        }

        private sealed class RelationGroupListEntry
        {
            public string Name { get; set; }
            public int RelationCount { get; set; }
            public List<string> Relations { get; set; }

            public RelationGroupListEntry()
            {
                Name = string.Empty;
                RelationCount = 0;
                Relations = new List<string>();
            }
        }

        private sealed class RelationGroupRenameResult
        {
            public string OldName { get; set; }
            public string NewName { get; set; }
            public int UpdatedCount { get; set; }
            public List<string> UpdatedRelations { get; set; }

            public RelationGroupRenameResult()
            {
                OldName = string.Empty;
                NewName = string.Empty;
                UpdatedCount = 0;
                UpdatedRelations = new List<string>();
            }
        }

        private sealed class BindModifierListResult
        {
            public string Scope { get; set; }
            public string Relation { get; set; }
            public List<string> Modifiers { get; set; }
            public List<string> Reformers { get; set; }
            public List<BindModifierListEntry> Entries { get; set; }

            public BindModifierListResult()
            {
                Scope = "global";
                Relation = string.Empty;
                Modifiers = new List<string>();
                Reformers = new List<string>();
                Entries = new List<BindModifierListEntry>();
            }
        }

        private sealed class BindModifierListEntry
        {
            public string Name { get; set; }
            public int BindCount { get; set; }
            public List<string> Relations { get; set; }

            public BindModifierListEntry()
            {
                Name = string.Empty;
                BindCount = 0;
                Relations = new List<string>();
            }
        }

        private sealed class BindModifierRenameResult
        {
            public string Scope { get; set; }
            public string Relation { get; set; }
            public string OldName { get; set; }
            public string NewName { get; set; }
            public int UpdatedCount { get; set; }
            public List<string> UpdatedRelations { get; set; }

            public BindModifierRenameResult()
            {
                Scope = "global";
                Relation = string.Empty;
                OldName = string.Empty;
                NewName = string.Empty;
                UpdatedCount = 0;
                UpdatedRelations = new List<string>();
            }
        }

        private sealed class RelationMassResult
        {
            public string Action { get; set; }
            public int SelectedCount { get; set; }
            public List<string> SelectedRelations { get; set; }
            public List<string> CreatedRelations { get; set; }
            public List<string> DeletedRelations { get; set; }
            public List<RelationMassRenameEntry> RenamedRelations { get; set; }
            public string OutputPath { get; set; }
            public string ExportFormat { get; set; }
            public List<string> Warnings { get; set; }

            public RelationMassResult()
            {
                Action = string.Empty;
                SelectedCount = 0;
                SelectedRelations = new List<string>();
                CreatedRelations = new List<string>();
                DeletedRelations = new List<string>();
                RenamedRelations = new List<RelationMassRenameEntry>();
                OutputPath = string.Empty;
                ExportFormat = string.Empty;
                Warnings = new List<string>();
            }
        }

        private sealed class RelationMassRenameEntry
        {
            public string OldName { get; set; }
            public string NewName { get; set; }

            public RelationMassRenameEntry()
            {
                OldName = string.Empty;
                NewName = string.Empty;
            }
        }

        private sealed class StarCitizenDbRefreshResult
        {
            public bool Success { get; set; }
            public bool DryRun { get; set; }
            public bool IncludeUserProfiles { get; set; }
            public string RequestedSourcePath { get; set; }
            public string RequestedInstallPath { get; set; }
            public string RequestedBranch { get; set; }
            public string SourceFile { get; set; }
            public string InstallPath { get; set; }
            public string Branch { get; set; }
            public string DbPath { get; set; }
            public string ActionmapsOutputFile { get; set; }
            public string AllBindsOutputFile { get; set; }
            public int ActionMapCount { get; set; }
            public int ActionCount { get; set; }
            public int AxisCount { get; set; }
            public int ButtonCount { get; set; }
            public int DuplicateActionsSkipped { get; set; }
            public int OptionGroupActions { get; set; }
            public int OptionTreeGroups { get; set; }
            public List<string> SourceCandidates { get; set; }
            public List<string> Warnings { get; set; }
            public string Error { get; set; }

            public StarCitizenDbRefreshResult()
            {
                Success = false;
                DryRun = false;
                IncludeUserProfiles = true;
                RequestedSourcePath = string.Empty;
                RequestedInstallPath = string.Empty;
                RequestedBranch = string.Empty;
                SourceFile = string.Empty;
                InstallPath = string.Empty;
                Branch = string.Empty;
                DbPath = string.Empty;
                ActionmapsOutputFile = string.Empty;
                AllBindsOutputFile = string.Empty;
                ActionMapCount = 0;
                ActionCount = 0;
                AxisCount = 0;
                ButtonCount = 0;
                DuplicateActionsSkipped = 0;
                SourceCandidates = new List<string>();
                Warnings = new List<string>();
                Error = string.Empty;
            }
        }

        private sealed class StarCitizenSourceCandidate
        {
            public string FilePath { get; set; }
            public string ArchivePath { get; set; }
            public string ArchiveEntryPath { get; set; }
            public string InstallPath { get; set; }
            public string Branch { get; set; }
            public int Priority { get; set; }
            public string Reason { get; set; }

            public StarCitizenSourceCandidate()
            {
                FilePath = string.Empty;
                ArchivePath = string.Empty;
                ArchiveEntryPath = string.Empty;
                InstallPath = string.Empty;
                Branch = string.Empty;
                Priority = 0;
                Reason = string.Empty;
            }
        }

        private sealed class StarCitizenParsedCatalog
        {
            public string SourceFile { get; set; }
            public string InstallPath { get; set; }
            public string Branch { get; set; }
            public int CandidatePriority { get; set; }
            public string CandidateReason { get; set; }
            public int ActionMapCount { get; set; }
            public int ActionCount { get; set; }
            public int AxisCount { get; set; }
            public int ButtonCount { get; set; }
            public int DuplicateActionsSkipped { get; set; }
            public XElement OptionTree { get; set; }
            public List<StarCitizenActionEntry> Actions { get; set; }

            public StarCitizenParsedCatalog()
            {
                SourceFile = string.Empty;
                InstallPath = string.Empty;
                Branch = string.Empty;
                CandidatePriority = 0;
                CandidateReason = string.Empty;
                ActionMapCount = 0;
                ActionCount = 0;
                AxisCount = 0;
                ButtonCount = 0;
                DuplicateActionsSkipped = 0;
                Actions = new List<StarCitizenActionEntry>();
            }
        }

        private sealed class StarCitizenActionEntry
        {
            public string Category { get; set; }
            public string Id { get; set; }
            public string Title { get; set; }
            public bool IsAxis { get; set; }
            public string OptionGroup { get; set; }
            // The game's default joystick input (defaultProfile.xml joystick="y"); JoyPro's clean mode unbinds it.
            public string DefaultJoystick { get; set; }

            public StarCitizenActionEntry()
            {
                Category = string.Empty;
                Id = string.Empty;
                Title = string.Empty;
                IsAxis = false;
                OptionGroup = string.Empty;
            }
        }

        private sealed class StarCitizenP4kEntryDescriptor
        {
            public string EntryPath { get; set; }
            public ushort CompressionMethod { get; set; }
            public long CompressedSize { get; set; }
            public long UncompressedSize { get; set; }
            public long LocalHeaderOffset { get; set; }

            public StarCitizenP4kEntryDescriptor()
            {
                EntryPath = string.Empty;
                CompressionMethod = 0;
                CompressedSize = 0;
                UncompressedSize = 0;
                LocalHeaderOffset = 0;
            }
        }

        private sealed class CryXmlBinaryNode
        {
            public int NodeNameOffset { get; set; }
            public int ItemType { get; set; }
            public short AttributeCount { get; set; }
            public short ChildCount { get; set; }
            public int ParentNodeId { get; set; }
            public int FirstAttributeIndex { get; set; }
            public int FirstChildIndex { get; set; }
            public int Reserved { get; set; }

            public CryXmlBinaryNode()
            {
                NodeNameOffset = 0;
                ItemType = 0;
                AttributeCount = 0;
                ChildCount = 0;
                ParentNodeId = -1;
                FirstAttributeIndex = 0;
                FirstChildIndex = 0;
                Reserved = 0;
            }
        }

        private sealed class CryXmlBinaryReference
        {
            public int NameOffset { get; set; }
            public int ValueOffset { get; set; }

            public CryXmlBinaryReference()
            {
                NameOffset = 0;
                ValueOffset = 0;
            }
        }

        private sealed class DeviceCandidate
        {
            public string Name { get; set; }
            public string Source { get; set; }
            public int Priority { get; set; }

            public DeviceCandidate()
            {
                Name = string.Empty;
                Source = string.Empty;
                Priority = 0;
            }
        }

        private sealed class NodeSpec
        {
            public string Game { get; set; }
            public string Id { get; set; }
            public List<string> Planes { get; set; }

            public NodeSpec()
            {
                Game = "DCS";
                Id = string.Empty;
                Planes = new List<string>();
            }
        }
    }
}







