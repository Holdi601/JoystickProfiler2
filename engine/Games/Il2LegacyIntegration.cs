using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // One input as an IL-2 1946 / Cliffs of Dover line key, decoded into JoyPro terms.
    internal sealed class Il2LegacyInput2
    {
        // JoyPro device string, "Keyboard", or empty when the line's device is not connected / unknown.
        public string Device = string.Empty;
        // JoyPro input token (JOY_X, JOY_BTN3, JOY_BTN_POV1_U) or key name.
        public string Input = string.Empty;
        public bool IsAxis;
        public List<string> Reformers = new List<string>();
        // The device as the file names it (JoystickDevice1, vJoy_Device-5A479A63); empty for the keyboard.
        public string FileDevice = string.Empty;
    }

    // Shared reader/writer of the two IL-2 classics: the binds are "[HotKey <section>]" lines "<trigger(s)>=<command>"
    // in one ini file; every other line stays as it was (docs/games/il2-1946.md, il2-cliffs-of-dover.md). The concrete
    // game decodes and encodes the line keys (device naming differs), the value (axis inversion), the axis tuning and
    // the catalog. Flight-only games: clean takes over the written devices, nuke clears the other connected devices.
    internal abstract class Il2LegacyIntegration : IGameIntegration, IGameOwnershipTracking
    {
        public abstract string GameId { get; }
        public abstract string DisplayName { get; }
        public abstract IReadOnlyList<string> Aliases { get; }
        public abstract string DbFolder { get; }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return true; } }
        public abstract string Notes { get; }
        public abstract IReadOnlyList<string> ProcessNames { get; }
        public bool SupportsCatalogRefresh { get { return true; } }
        public GameFeature KeyboardBinds { get { return GameFeature.Yes; } }
        public virtual GameFeature ModifierBinds { get { return GameFeature.Yes; } }

        // How many joysticks the game reads at once (docs; unified with the one-device games at merge).
        public abstract int MaxJoysticks { get; }

        // The "[HotKey <section>]" sections JoyPro reads and writes, and the ones whose commands are axes.
        protected abstract IReadOnlyList<string> Sections { get; }
        protected abstract IReadOnlyList<string> AxisSections { get; }
        // The word the game writes for the control key ("Ctrl" / "Control").
        protected abstract string CtrlWord { get; }
        // True when a button or key may stand on an axis command's line (Cliffs of Dover).
        protected virtual bool AxisCommandsTakeButtons { get { return false; } }

        public abstract List<string> DiscoverConfigPaths(List<string> warnings);
        // JoyPro's own command list (rows of the catalog a refresh starts from).
        protected abstract IEnumerable<GameAction> OwnActions();
        // Files of the installed game and the user whose bind lines name commands (for refresh-db).
        protected abstract IEnumerable<string> CatalogSources(string sourcePath, List<string> warnings);

        // A line key -> input; false (with a reason, or empty for inputs JoyPro does not handle such as the mouse).
        protected abstract bool TryDecodeKey(string key, GameDeviceDirectory devices, out Il2LegacyInput2 input, out string problem);
        // An export binding -> the line key; false with a reason.
        protected abstract bool TryEncodeKey(GameInputBinding binding, GameDeviceDirectory devices, LegacyIniFile file, out string key, out string fileDevice, out string problem);
        // The line value -> command (and inversion of an axis).
        protected abstract string DecodeValue(string section, string value, out bool inverted);
        protected abstract string EncodeValue(string section, string command, bool axis, bool inverted);
        // The most inputs one command keeps (the game's controls page drops the others); 0 = no limit.
        protected abstract int MaxInputsPerCommand(bool axis);

        // Axis tuning hooks: the game's values of an imported axis bind; writing the tunings of the exported axes.
        protected virtual void ReadTuning(Il2LegacyContext context, string section, string command, Il2LegacyInput2 input, GameInputBinding binding) { }
        protected virtual void WriteTunings(Il2LegacyContext context, List<Il2LegacyWanted> axes, GameExportRequest request, GameIoResult result) { }
        protected virtual void OpenExtraFiles(Il2LegacyContext context, List<string> warnings) { }

        public static string SectionHeader(string section)
        {
            return "HotKey " + section;
        }

        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            Il2LegacyContext context;
            if (!TryOpen(configPath, devices, result.Errors, result.Warnings, out context))
                return result;

            HashSet<string> warnedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string section in Sections)
            {
                foreach (LegacyIniFile.Entry entry in context.File.Entries(SectionHeader(section)))
                {
                    bool inverted;
                    string command = DecodeValue(section, entry.Value, out inverted);
                    if (command.Length == 0)
                        continue;
                    Il2LegacyInput2 input;
                    string problem;
                    if (!TryDecodeKey(entry.Key, devices, out input, out problem))
                    {
                        if (problem.Length > 0 && warnedDevices.Add(problem))
                            result.Warnings.Add(problem);
                        continue;
                    }
                    bool axisSection = AxisSections.Contains(section, StringComparer.OrdinalIgnoreCase);
                    string id = Il2LegacyInput.Id(section, command);
                    if (catalog.Actions.Count > 0 && !catalog.Contains(id))
                        result.Warnings.Add("'" + id + "' is not in the " + DisplayName + " catalog yet; run 'engine " + Aliases[0] + " refresh-db' to add it.");
                    GameInputBinding binding = new GameInputBinding
                    {
                        ActionId = id,
                        Device = input.Device,
                        Input = input.Input,
                        IsAxis = axisSection && input.IsAxis,
                        Inverted = axisSection && input.IsAxis && inverted,
                        Reformers = input.Reformers
                    };
                    if (binding.IsAxis)
                        ReadTuning(context, section, command, input, binding);
                    result.Bindings.Add(binding);
                }
            }
            return result;
        }

        protected bool TryOpen(string configPath, GameDeviceDirectory devices, List<string> errors, List<string> warnings, out Il2LegacyContext context)
        {
            context = null;
            LegacyIniFile file;
            if (!LegacyIniFile.TryLoad(configPath, errors, out file))
                return false;
            context = new Il2LegacyContext { ConfigPath = configPath, File = file, Devices = devices ?? new GameDeviceDirectory() };
            OpenExtraFiles(context, warnings);
            return true;
        }

        // A line key with each connected device under the name the game uses for it now (Cliffs of Dover Blitz: a line
        // written while the device was on another USB port names an older id); the key itself otherwise.
        protected virtual string CanonicalKey(string key, GameDeviceDirectory devices)
        {
            return key;
        }

        private sealed class Existing
        {
            public int Line;
            public string Key;
            public string Section;
            public string Command;
            public string Value;
            public string Normalized;
            public string FileDevice;
            public string JoyProDevice;
            public bool IsAxisCommand;
        }

        // The key with its triggers in a fixed order, so "AXE_U JoystickDevice0" and "JoystickDevice0 AXE_U" match.
        public static string NormalizeKey(string key)
        {
            return string.Join(" ", (key ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToUpperInvariant()).OrderBy(t => t, StringComparer.Ordinal));
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            Il2LegacyContext context;
            if (!TryOpen(request.ConfigPath, request.Devices, result.Errors, result.Warnings, out context))
                return result;
            LegacyIniFile file = context.File;

            // What the profile wants, one bind per input.
            List<Il2LegacyWanted> wanted = new List<Il2LegacyWanted>();
            Dictionary<string, Il2LegacyWanted> byKey = new Dictionary<string, Il2LegacyWanted>(StringComparer.Ordinal);
            foreach (GameInputBinding binding in request.Bindings)
            {
                string section;
                string command;
                GameAction action = request.Catalog.Find(binding.ActionId);
                string reason = string.Empty;
                if (action == null || !Il2LegacyInput.TrySplitId(action.Id, out section, out command) || !Sections.Contains(section, StringComparer.OrdinalIgnoreCase))
                {
                    section = command = string.Empty;
                    reason = "not a command of the " + DisplayName + " catalog.";
                }
                string key = string.Empty;
                string fileDevice = string.Empty;
                bool axisSection = section.Length > 0 && AxisSections.Contains(section, StringComparer.OrdinalIgnoreCase);
                // Cliffs of Dover also puts a button on an axis command ("Joystick2+Key4=1underc").
                if (reason.Length == 0 && axisSection != binding.IsAxis && !(axisSection && AxisCommandsTakeButtons))
                    reason = axisSection ? "'" + command + "' is an axis command; bind an axis." : "'" + command + "' needs a button or key.";
                if (reason.Length == 0 && !TryEncodeKey(binding, request.Devices, file, out key, out fileDevice, out reason))
                    reason = reason.Length == 0 ? "the input cannot be written." : reason;
                Il2LegacyWanted existing;
                if (reason.Length == 0 && byKey.TryGetValue(NormalizeKey(key), out existing))
                    reason = "the input is already used by relation '" + existing.Binding.RelationName + "'.";
                if (reason.Length > 0)
                {
                    result.BindingsSkipped++;
                    result.Warnings.Add("Relation '" + binding.RelationName + "' (" + binding.ActionId + "): " + reason);
                    continue;
                }
                Il2LegacyWanted want = new Il2LegacyWanted
                {
                    Binding = binding,
                    Section = action.Id.Substring(0, action.Id.IndexOf('.')),
                    Command = command,
                    Key = key,
                    Normalized = NormalizeKey(key),
                    FileDevice = fileDevice,
                    Value = EncodeValue(section, command, binding.IsAxis, binding.IsAxis && binding.Inverted)
                };
                wanted.Add(want);
                byKey[want.Normalized] = want;
            }

            // What the file has.
            List<Existing> lines = new List<Existing>();
            foreach (string section in Sections)
            {
                foreach (LegacyIniFile.Entry entry in file.Entries(SectionHeader(section)))
                {
                    bool inverted;
                    string command = DecodeValue(section, entry.Value, out inverted);
                    Il2LegacyInput2 input;
                    string problem;
                    bool decoded = TryDecodeKey(entry.Key, request.Devices, out input, out problem);
                    lines.Add(new Existing
                    {
                        Line = entry.Line,
                        Key = entry.Key,
                        Section = section,
                        Command = command,
                        Value = entry.Value,
                        Normalized = NormalizeKey(CanonicalKey(entry.Key, request.Devices)),
                        FileDevice = decoded ? input.FileDevice : DeviceTokenOf(entry.Key),
                        JoyProDevice = decoded ? input.Device : string.Empty,
                        IsAxisCommand = AxisSections.Contains(section, StringComparer.OrdinalIgnoreCase)
                    });
                }
            }

            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(request.ConfigPath, result.Warnings);
            bool overwrite = request.Mode == GameExportMode.Clean || request.Mode == GameExportMode.MergeOverwrite;
            HashSet<int> remove = new HashSet<int>();

            // Inputs JoyPro wrote before and no longer wants go (clean, merge-overwrite), unless changed in the game.
            if (overwrite)
            {
                foreach (Existing line in lines)
                {
                    // Keyboard binds are only managed by exports that include them.
                    if (line.FileDevice.Length == 0 && !request.IncludeKeyboard)
                        continue;
                    string id = Il2LegacyInput.Id(line.Section, line.Command);
                    Il2LegacyWanted want;
                    bool stillWanted = byKey.TryGetValue(line.Normalized, out want) && string.Equals(Il2LegacyInput.Id(want.Section, want.Command), id, StringComparison.OrdinalIgnoreCase);
                    if (!stillWanted && GameOwnershipStore.Contains(owned, id, line.Normalized))
                        remove.Add(line.Line);
                }
            }

            // Clean: the written devices keep only the profile's binds; nuke: the other connected devices lose theirs.
            HashSet<string> usedDevices = new HashSet<string>(wanted.Where(w => w.FileDevice.Length > 0).Select(w => w.FileDevice), StringComparer.OrdinalIgnoreCase);
            HashSet<string> connectedDevices = new HashSet<string>(lines.Where(l => l.FileDevice.Length > 0 && l.JoyProDevice.Length > 0).Select(l => l.FileDevice), StringComparer.OrdinalIgnoreCase);
            int released = 0;
            foreach (Existing line in lines)
            {
                if (line.FileDevice.Length == 0 || byKey.ContainsKey(line.Normalized))
                    continue;
                bool used = usedDevices.Contains(line.FileDevice);
                if ((used && request.Mode == GameExportMode.Clean) || (!used && request.NukeUnusedDevices && connectedDevices.Contains(line.FileDevice)))
                {
                    if (remove.Add(line.Line))
                        released++;
                }
            }
            if (released > 0)
                result.Steps.Add("Released " + released.ToString(CultureInfo.InvariantCulture) + " bind(s) of " + (request.Mode == GameExportMode.Clean ? "the written devices" : "unused devices") + ".");

            // The profile's binds.
            List<string> inserts = new List<string>();
            List<KeyValuePair<string, string>> appendLines = new List<KeyValuePair<string, string>>();
            List<Il2LegacyWanted> writtenAxes = new List<Il2LegacyWanted>();
            Dictionary<string, HashSet<string>> newOwned = overwrite
                ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                : owned.ToDictionary(e => e.Key, e => new HashSet<string>(e.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (overwrite && !request.IncludeKeyboard)
            {
                // Keyboard binds JoyPro owns stay owned when the export leaves the keyboard alone.
                foreach (Existing line in lines.Where(l => l.FileDevice.Length == 0))
                {
                    string id = Il2LegacyInput.Id(line.Section, line.Command);
                    if (GameOwnershipStore.Contains(owned, id, line.Normalized))
                        GameOwnershipStore.Add(newOwned, id, line.Normalized);
                }
            }
            foreach (Il2LegacyWanted want in wanted)
            {
                string id = Il2LegacyInput.Id(want.Section, want.Command);
                string label = "Relation '" + want.Binding.RelationName + "' (" + id + ")";
                List<Existing> sameInput = lines.Where(l => l.Normalized == want.Normalized && !remove.Contains(l.Line)).ToList();
                Existing exact = sameInput.FirstOrDefault(l => string.Equals(l.Section, want.Section, StringComparison.OrdinalIgnoreCase) && l.Value == want.Value);
                List<Existing> others = sameInput.Where(l => l != exact).ToList();
                if (others.Count > 0)
                {
                    if (!overwrite)
                    {
                        result.ConflictsKept++;
                        result.BindingsSkipped++;
                        result.Warnings.Add(label + ": the input already has '" + others[0].Value + "' in [" + SectionHeader(others[0].Section) + "]; " + ModeName(request.Mode) + " keeps the game's bind.");
                        continue;
                    }
                    foreach (Existing other in others)
                    {
                        remove.Add(other.Line);
                        result.Warnings.Add(label + " replaced '" + other.Value + "' on its input.");
                    }
                }

                // The game's controls page keeps one axis and two inputs per command and drops the rest.
                int max = MaxInputsPerCommand(want.Binding.IsAxis);
                if (max > 0 && exact == null)
                {
                    List<Existing> commandInputs = lines.Where(l => !remove.Contains(l.Line) && string.Equals(l.Section, want.Section, StringComparison.OrdinalIgnoreCase) &&
                                                                    string.Equals(l.Command, want.Command, StringComparison.OrdinalIgnoreCase) && l.Normalized != want.Normalized).ToList();
                    int alsoWanted = wanted.Count(w => w != want && wanted.IndexOf(w) < wanted.IndexOf(want) && Il2LegacyInput.Id(w.Section, w.Command) == id);
                    int room = max - alsoWanted;
                    if (commandInputs.Count + 1 > room)
                    {
                        if (!overwrite || room <= 0)
                        {
                            result.ConflictsKept++;
                            result.BindingsSkipped++;
                            result.Warnings.Add(label + ": " + DisplayName + " keeps " + max.ToString(CultureInfo.InvariantCulture) + " input(s) per command and '" + want.Command + "' has them; " +
                                                (overwrite ? "the profile binds it more often than that." : ModeName(request.Mode) + " keeps the game's."));
                            continue;
                        }
                        foreach (Existing drop in commandInputs.Take(commandInputs.Count + 1 - room))
                        {
                            remove.Add(drop.Line);
                            result.Warnings.Add(label + " replaced the game's input '" + drop.Normalized + "' of '" + want.Command + "' (" + DisplayName + " keeps " + max.ToString(CultureInfo.InvariantCulture) + " per command).");
                        }
                    }
                }

                if (exact == null)
                    appendLines.Add(new KeyValuePair<string, string>(SectionHeader(want.Section), want.Key + "=" + want.Value));
                else if (NormalizeKey(exact.Key) != want.Normalized)
                {
                    // The bind is there under the device's older name: written with the name the game uses now.
                    file.RewriteLine(exact.Line, want.Key + "=" + want.Value);
                    result.Warnings.Add(label + ": '" + exact.Key + "' renamed to '" + want.Key + "' (the device's current name).");
                }
                GameOwnershipStore.Add(newOwned, id, want.Normalized);
                result.BindingsWritten++;
                JoyInput wantedInput;
                // Only a real axis has a response (a button on an axis command has none).
                if (want.Binding.IsAxis && GameInputTokens.TryParse(want.Binding.Input, out wantedInput) && wantedInput.Kind == JoyInputKind.Axis)
                    writtenAxes.Add(want);
            }

            file.RemoveLines(remove);
            foreach (KeyValuePair<string, string> line in appendLines)
                file.Insert(line.Key, line.Value);
            WriteTunings(context, writtenAxes, request, result);

            if (request.DryRun)
            {
                result.Steps.Add("Dry run: " + file.ChangedLines.ToString(CultureInfo.InvariantCulture) + " line(s) of " + Path.GetFileName(request.ConfigPath) + " would change" +
                                 context.ExtraFiles.Where(f => f.Value.ChangedLines > 0).Select(f => ", " + f.Value.ChangedLines.ToString(CultureInfo.InvariantCulture) + " of " + Path.GetFileName(f.Key)).DefaultIfEmpty(string.Empty).Aggregate((a, b) => a + b) + ".");
                return result;
            }

            GameOwnershipStore.Save(request.ConfigPath, GameId, newOwned);
            SaveIfChanged(request.ConfigPath, file, result);
            foreach (KeyValuePair<string, LegacyIniFile> extra in context.ExtraFiles)
                SaveIfChanged(extra.Key, extra.Value, result);
            if (result.FilesWritten.Count == 0)
                result.Steps.Add("The " + DisplayName + " binds already match the profile.");
            return result;
        }

        private static void SaveIfChanged(string path, LegacyIniFile file, GameIoResult result)
        {
            if (file.ChangedLines == 0)
                return;
            GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
            file.Save(path);
            result.FilesWritten.Add(path);
            result.Steps.Add(file.ChangedLines.ToString(CultureInfo.InvariantCulture) + " line(s) of " + Path.GetFileName(path) + " changed.");
        }

        private static string ModeName(GameExportMode mode)
        {
            return mode == GameExportMode.Add ? "add" : "merge";
        }

        // The device token of a line key JoyPro could not decode (a device that is not connected), for nuke/clean: none.
        protected virtual string DeviceTokenOf(string key)
        {
            return string.Empty;
        }

        public void RecordImport(string configPath, List<GameInputBinding> applied, GameDeviceDirectory devices, GameIoResult result)
        {
            Dictionary<string, HashSet<string>> owned = GameOwnershipStore.Load(configPath, result.Warnings);
            LegacyIniFile file;
            List<string> errors = new List<string>();
            if (!LegacyIniFile.TryLoad(configPath, errors, out file))
                return;
            foreach (GameInputBinding binding in applied)
            {
                string key;
                string fileDevice;
                string problem;
                if (TryEncodeKey(binding, devices, file, out key, out fileDevice, out problem))
                    GameOwnershipStore.Add(owned, binding.ActionId, NormalizeKey(key));
            }
            GameOwnershipStore.Save(configPath, GameId, owned);
        }

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (GameAction action in current.Actions)
                catalog.Add(action);
            int own = 0;
            foreach (GameAction action in OwnActions())
            {
                if (!catalog.Contains(action.Id))
                {
                    catalog.Add(action);
                    own++;
                }
            }
            result.Steps.Add("JoyPro's own " + DisplayName + " command list: " + own.ToString(CultureInfo.InvariantCulture) + " new command(s).");

            foreach (string path in CatalogSources(sourcePath, result.Warnings).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                LegacyIniFile file;
                List<string> errors = new List<string>();
                if (!LegacyIniFile.TryLoad(path, errors, out file))
                {
                    result.Warnings.AddRange(errors);
                    continue;
                }
                int added = 0;
                foreach (string section in Sections)
                {
                    bool axis = AxisSections.Contains(section, StringComparer.OrdinalIgnoreCase);
                    foreach (LegacyIniFile.Entry entry in file.Entries(SectionHeader(section)))
                    {
                        bool inverted;
                        string command = DecodeValue(section, entry.Value, out inverted);
                        string id = Il2LegacyInput.Id(section, command);
                        if (command.Length == 0 || catalog.Contains(id) || command.IndexOfAny(new[] { ' ', '\t' }) >= 0)
                            continue;
                        catalog.Add(new GameAction { Id = id, Title = Il2LegacyInput.Humanize(command), IsAxis = axis, Category = CategoryOf(section) });
                        added++;
                    }
                }
                result.Steps.Add("Read " + path + ": " + added.ToString(CultureInfo.InvariantCulture) + " new command(s).");
            }
            return catalog;
        }

        protected abstract string CategoryOf(string section);

        // Rows "section|command|a or b|title" of an own list.
        protected IEnumerable<GameAction> ParseOwnRows(IEnumerable<string> rows)
        {
            foreach (string row in rows)
            {
                string[] parts = row.Split(new[] { '|' }, 4);
                if (parts.Length < 4)
                    continue;
                yield return new GameAction { Id = Il2LegacyInput.Id(parts[0], parts[1]), IsAxis = parts[2] == "a", Title = parts[3], Category = CategoryOf(parts[0]) };
            }
        }
    }

    internal sealed class Il2LegacyContext
    {
        public string ConfigPath;
        public LegacyIniFile File;
        public GameDeviceDirectory Devices;
        // Other files the export may change (IL-2 1946 conf.ini), by path.
        public Dictionary<string, LegacyIniFile> ExtraFiles = new Dictionary<string, LegacyIniFile>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Il2LegacyWanted
    {
        public GameInputBinding Binding;
        public string Section;
        public string Command;
        public string Key;
        public string Normalized;
        public string FileDevice;
        public string Value;
    }
}
