using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // How JoyPro writes a source for a catalog action Arma Reforger has no hand-made rule for: the action's 5th catalog
    // column "<Reforger action>|<FilterPreset>|<Filter class>|<Key value;Key value>" (empty fields allowed; without the
    // column the action is the id). The refresh takes it from the game's own default source of that action, so a
    // joystick button behaves like the game's key (e.g. InputFilterRepeat with Multiplier -1 for "sight range down").
    internal sealed class ReforgerActionSpec
    {
        public string Action = string.Empty;
        public string Preset = string.Empty;
        public string FilterClass = string.Empty;
        public string Parameters = string.Empty;

        public static ReforgerActionSpec Of(GameAction action)
        {
            string[] parts = (action.NativeId ?? string.Empty).Split('|');
            ReforgerActionSpec spec = new ReforgerActionSpec { Action = parts[0].Trim().Length > 0 ? parts[0].Trim() : action.Id };
            if (parts.Length > 1)
                spec.Preset = parts[1].Trim();
            if (parts.Length > 2)
                spec.FilterClass = parts[2].Trim();
            if (parts.Length > 3)
                spec.Parameters = parts[3].Trim();
            return spec;
        }

        public string Format()
        {
            return Action + "|" + Preset + "|" + FilterClass + "|" + Parameters;
        }
    }

    // Arma Reforger's action list from the game's own files (read only):
    //   Configs/System/chimeraInputCommon.conf  ActionManager { Actions { Action <name> {...} } Contexts { ActionContext
    //                                           <name>Context { Actions { Action ... } ActionRefs { "<name>" } } } }
    //   Configs/System/keyBindingMenu.conf      SCR_KeyBindingEntry { m_sActionName m_sDisplayName "#AR-..." m_sPreset }
    //   Language/localization.en_us.conf        StringTableRuntime { Ids { "AR-..." } Texts { "..." } } (same order)
    // inside the game's PAK archives (addons\**\*.pak), or in a folder of extracted files. Only the flight contexts count
    // (Helicopter..., Airplane...); the category is the context name without "Context".
    internal static class ReforgerCatalogSource
    {
        public const string InputConfig = "Configs/System/chimeraInputCommon.conf";
        public const string KeyBindingMenu = "Configs/System/keyBindingMenu.conf";
        public const string Localization = "Language/localization.en_us.conf";

        private static readonly string[] FlightPrefixes = { "Helicopter", "Airplane", "Aircraft", "Plane" };
        private static readonly string[] AnalogTypes = { "Analog", "AnalogRelative", "Motion" };

        private sealed class MenuEntry
        {
            public string Preset;
            public string TitleKey;
        }

        private sealed class Source
        {
            public string Preset;
            public string Input;
            public string FilterClass;
            public string Parameters;
        }

        // Adds the flight actions the catalog does not have yet (known ids, titles and categories stay); null with an
        // error when the game's input config could not be read.
        public static GameCatalog Refresh(string gameId, string source, GameCatalog current, List<string> userFiles, GameIoResult result)
        {
            Dictionary<string, string> files = LoadGameFiles(source, result);
            if (files == null)
                return null;
            string inputText;
            EnfusionNode manager = files.TryGetValue(InputConfig, out inputText) ? EnfusionNode.Parse(inputText).Block("ActionManager") : null;
            if (manager == null)
            {
                result.Errors.Add("No " + InputConfig + " with an ActionManager was found in " + source + ".");
                return null;
            }
            Dictionary<string, List<MenuEntry>> menu = ReadMenu(files);
            Dictionary<string, string> texts = ReadLocalization(files, result.Warnings);
            if (texts.Count == 0)
                result.Warnings.Add(Localization + " was not found; new actions are titled by their id.");

            GameCatalog catalog = new GameCatalog { GameId = gameId, SourcePath = source };
            if (current != null)
            {
                foreach (GameAction action in current.Actions)
                    catalog.Add(action);
            }

            Dictionary<string, EnfusionNode> topLevel = new Dictionary<string, EnfusionNode>(StringComparer.Ordinal);
            foreach (EnfusionNode action in Blocks(manager.Block("Actions"), "Action").Where(a => a.Head.Count > 1))
            {
                if (!topLevel.ContainsKey(action.Head[1]))
                    topLevel[action.Head[1]] = action;
            }

            int found = 0;
            int added = 0;
            List<string> contexts = new List<string>();
            foreach (EnfusionNode context in Blocks(manager.Block("Contexts"), "ActionContext").Where(c => c.Head.Count > 1))
            {
                string category = FlightCategory(context.Head[1], true);
                if (category == null)
                    continue;
                contexts.Add(context.Head[1]);
                List<EnfusionNode> actions = Blocks(context.Block("Actions"), "Action").Where(a => a.Head.Count > 1).ToList();
                EnfusionNode refs = context.Block("ActionRefs");
                foreach (EnfusionNode line in (refs == null ? new List<EnfusionNode>() : refs.Children).Where(l => l.Children == null))
                {
                    EnfusionNode referenced;
                    if (topLevel.TryGetValue(line.Name, out referenced))
                        actions.Add(referenced);
                }
                foreach (EnfusionNode action in actions)
                {
                    found++;
                    added += AddAction(catalog, action, category, menu, texts);
                }
            }
            result.Steps.Add("Read " + InputConfig + ": " + found.ToString(CultureInfo.InvariantCulture) + " flight action(s) in " +
                             (contexts.Count == 0 ? "no flight context" : string.Join(", ", contexts)) + "; " + added.ToString(CultureInfo.InvariantCulture) + " new.");

            // The user's own input configs (joystick presets, InputUserSettings.conf) may name flight actions the
            // game's files do not have in a flight context (mods).
            int userAdded = 0;
            foreach (string file in userFiles ?? new List<string>())
            {
                string text;
                try
                {
                    text = ReadText(File.ReadAllBytes(file));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Warnings.Add("Could not read " + file + ": " + ex.Message);
                    continue;
                }
                foreach (EnfusionNode action in EnfusionNode.Parse(text).Descendants().Where(n => n.Children != null && n.Name == "Action" && n.Head.Count > 1))
                {
                    string category = FlightCategory(action.Head[1], false);
                    if (category != null)
                        userAdded += AddAction(catalog, action, category, menu, texts);
                }
            }
            if (userFiles != null && userFiles.Count > 0)
                result.Steps.Add("Read " + userFiles.Count.ToString(CultureInfo.InvariantCulture) + " input config(s) of the user: " + userAdded.ToString(CultureInfo.InvariantCulture) + " new flight action(s).");
            return catalog;
        }

        // Path (as in the archives) -> text of the wanted game files: from the archives under <game>\addons (or the .pak
        // files of a folder, or one .pak), else from a folder of extracted files; a single .conf is the input config.
        private static Dictionary<string, string> LoadGameFiles(string source, GameIoResult result)
        {
            string[] wanted = { InputConfig, KeyBindingMenu, Localization };
            Dictionary<string, byte[]> raw = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(source))
            {
                string addons = Path.Combine(source, "addons");
                List<string> paks = (Directory.Exists(addons)
                        ? Directory.GetFiles(addons, "*.pak", SearchOption.AllDirectories)
                        : Directory.GetFiles(source, "*.pak", SearchOption.TopDirectoryOnly))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (paks.Count > 0)
                {
                    raw = EnfusionPak.ReadFiles(paks, wanted, result.Warnings);
                    result.Steps.Add("Read " + raw.Count.ToString(CultureInfo.InvariantCulture) + " of " + wanted.Length.ToString(CultureInfo.InvariantCulture) + " input file(s) from " + paks.Count.ToString(CultureInfo.InvariantCulture) + " PAK archive(s) in " + source + ".");
                }
                else
                {
                    foreach (string path in wanted)
                    {
                        string file = Path.Combine(source, path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(file))
                            raw[path] = File.ReadAllBytes(file);
                    }
                }
            }
            else if (File.Exists(source) && string.Equals(Path.GetExtension(source), ".pak", StringComparison.OrdinalIgnoreCase))
                raw = EnfusionPak.ReadFiles(new[] { source }, wanted, result.Warnings);
            else if (File.Exists(source))
                raw[InputConfig] = File.ReadAllBytes(source);
            else
            {
                result.Errors.Add("Arma Reforger was not found: " + source + ". Pass the game folder (addons\\data\\*.pak), a .pak file, a folder with the extracted Configs\\System\\chimeraInputCommon.conf, or that file.");
                return null;
            }
            return raw.ToDictionary(f => f.Key, f => ReadText(f.Value), StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadText(byte[] bytes)
        {
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        private static IEnumerable<EnfusionNode> Blocks(EnfusionNode parent, string name)
        {
            return parent == null ? Enumerable.Empty<EnfusionNode>() : parent.Blocks(name);
        }

        // "HelicopterContext" -> "Helicopter" (contexts), "HelicopterFoo" -> "Helicopter" (action names); null otherwise.
        private static string FlightCategory(string name, bool context)
        {
            string prefix = FlightPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
            if (prefix == null)
                return null;
            if (!context)
                return prefix;
            return name.EndsWith("Context", StringComparison.Ordinal) && name.Length > "Context".Length ? name.Substring(0, name.Length - "Context".Length) : name;
        }

        // Adds the catalog rows of one Reforger action; returns how many were new.
        private static int AddAction(GameCatalog catalog, EnfusionNode node, string category, Dictionary<string, List<MenuEntry>> menu, Dictionary<string, string> texts)
        {
            string action = node.Head[1];
            // The half axes of JoyPro's composite axes (cyclic, collective, pedals) are one axis action in the catalog.
            string composite = ArmaReforgerIntegration.CompositeOf(action);
            if (composite != null)
            {
                if (catalog.Contains(composite))
                    return 0;
                catalog.Add(new GameAction { Id = composite, Title = TitleRule(composite.Substring("Axis.".Length), category, string.Empty), IsAxis = true, Category = category });
                return 1;
            }
            if (catalog.Contains(action))
                return 0;

            List<Source> sources = new List<Source>();
            CollectSources(node, sources);
            List<MenuEntry> entries;
            if (!menu.TryGetValue(action, out entries))
                entries = new List<MenuEntry>();
            bool analog = AnalogTypes.Contains(node.Value("Type"));

            if (analog && sources.Any(s => IsFullAxis(s.Input)))
            {
                catalog.Add(new GameAction { Id = action, Title = Title(action, string.Empty, category, entries, texts), IsAxis = true, Category = category });
                return 1;
            }

            // A stepping action with two directions (sight range up / down): one button row per direction.
            List<string> presets = entries.Select(e => e.Preset).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (presets.Count < 2)
                presets = sources.Select(s => NormalizePreset(s.Preset)).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (analog && presets.Count >= 2)
            {
                int added = 0;
                foreach (string preset in presets)
                {
                    string id = action + "." + char.ToUpperInvariant(preset[0]) + preset.Substring(1);
                    if (catalog.Contains(id))
                        continue;
                    catalog.Add(new GameAction { Id = id, Title = Title(action, preset, category, entries, texts), IsAxis = false, Category = category, NativeId = Spec(action, preset, sources) });
                    added++;
                }
                return added;
            }

            string single = entries.Select(e => e.Preset).FirstOrDefault(p => p.Length > 0) ?? string.Empty;
            if (single.Length == 0)
            {
                Source template = Template(sources, string.Empty);
                single = template == null ? string.Empty : NormalizePreset(template.Preset);
            }
            catalog.Add(new GameAction { Id = action, Title = Title(action, string.Empty, category, entries, texts), IsAxis = false, Category = category, NativeId = Spec(action, single, sources) });
            return 1;
        }

        // The InputSourceValue entries of an action (not the parts of key combinations).
        private static void CollectSources(EnfusionNode node, List<Source> sources)
        {
            foreach (EnfusionNode child in (node.Children ?? new List<EnfusionNode>()).Where(c => c.Children != null))
            {
                if (child.Name == "InputSourceCombo")
                    continue;
                if (child.Name == "InputSourceValue")
                {
                    EnfusionNode filter = child.Block("Filter");
                    sources.Add(new Source
                    {
                        Preset = child.Value("FilterPreset"),
                        Input = child.Value("Input"),
                        FilterClass = filter != null && filter.Head.Count > 1 ? filter.Head[1] : string.Empty,
                        Parameters = filter == null ? string.Empty : string.Join(";", filter.Children.Where(c => c.Children == null && c.Head.Count > 1).Select(c => c.Head[0] + " " + c.Head[1]))
                    });
                    continue;
                }
                CollectSources(child, sources);
            }
        }

        // The game's default source a joystick bind copies: the one with that preset, keyboard first.
        private static Source Template(List<Source> sources, string preset)
        {
            List<Source> matching = preset.Length == 0 ? sources : sources.Where(s => string.Equals(NormalizePreset(s.Preset), preset, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
                matching = sources;
            return matching.OrderBy(s => DeviceRank(s.Input)).FirstOrDefault();
        }

        private static string Spec(string action, string preset, List<Source> sources)
        {
            Source template = Template(sources, preset);
            return new ReforgerActionSpec
            {
                Action = action,
                Preset = preset,
                FilterClass = template == null ? string.Empty : template.FilterClass,
                Parameters = template == null ? string.Empty : template.Parameters
            }.Format();
        }

        private static int DeviceRank(string input)
        {
            string device = (input ?? string.Empty).Split(':')[0];
            if (device == "keyboard")
                return 0;
            if (device == "mouse")
                return 1;
            if (device.StartsWith("joystick", StringComparison.Ordinal))
                return 2;
            return device.StartsWith("gamepad", StringComparison.Ordinal) ? 4 : 3;
        }

        // "gamepad:up" -> "up" (the gamepad sources of an entry carry the same preset with a prefix).
        private static string NormalizePreset(string preset)
        {
            string value = preset ?? string.Empty;
            int colon = value.IndexOf(':');
            return colon >= 0 ? value.Substring(colon + 1) : value;
        }

        // A whole axis (not a half axis "+"/"-", not a key): joystickN:axisA, gamepad thumb sticks and triggers, mouse x_abs.
        private static bool IsFullAxis(string input)
        {
            string value = input ?? string.Empty;
            int colon = value.IndexOf(':');
            string name = colon >= 0 ? value.Substring(colon + 1) : value;
            if (name.EndsWith("+", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal))
                return false;
            return Regex.IsMatch(name, @"^(axis\d+|.*_(horizontal|vertical|trigger|abs))$");
        }

        private static string Title(string action, string preset, string category, List<MenuEntry> entries, Dictionary<string, string> texts)
        {
            MenuEntry entry = preset.Length == 0
                ? entries.FirstOrDefault()
                : entries.FirstOrDefault(e => string.Equals(e.Preset, preset, StringComparison.OrdinalIgnoreCase));
            string text;
            if (entry != null && texts.TryGetValue(entry.TitleKey.TrimStart('#'), out text) && text.Trim().Length > 0)
                return Regex.Replace(text, @"\s+", " ").Trim();
            return TitleRule(action, category, preset);
        }

        // "HelicopterSightZeroing" in Helicopter, "up" -> "Sight zeroing (up)".
        private static string TitleRule(string action, string category, string preset)
        {
            string name = action.StartsWith(category, StringComparison.Ordinal) && action.Length > category.Length ? action.Substring(category.Length) : action;
            string[] words = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string title = string.Join(" ", words.Select((w, i) => i == 0 || w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
            return preset.Length == 0 ? title : title + " (" + preset + ")";
        }

        private static Dictionary<string, List<MenuEntry>> ReadMenu(Dictionary<string, string> files)
        {
            Dictionary<string, List<MenuEntry>> menu = new Dictionary<string, List<MenuEntry>>(StringComparer.Ordinal);
            string text;
            if (!files.TryGetValue(KeyBindingMenu, out text))
                return menu;
            foreach (EnfusionNode entry in EnfusionNode.Parse(text).Descendants().Where(n => n.Children != null && n.Name == "SCR_KeyBindingEntry"))
            {
                string action = entry.Value("m_sActionName");
                if (action.Length == 0 || action == "separator")
                    continue;
                List<MenuEntry> list;
                if (!menu.TryGetValue(action, out list))
                {
                    list = new List<MenuEntry>();
                    menu[action] = list;
                }
                list.Add(new MenuEntry { Preset = entry.Value("m_sPreset"), TitleKey = entry.Value("m_sDisplayName") });
            }
            return menu;
        }

        private static Dictionary<string, string> ReadLocalization(Dictionary<string, string> files, List<string> warnings)
        {
            Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text;
            if (!files.TryGetValue(Localization, out text))
                return texts;
            EnfusionNode table = EnfusionNode.Parse(text).Block("StringTableRuntime");
            EnfusionNode ids = table == null ? null : table.Block("Ids");
            EnfusionNode values = table == null ? null : table.Block("Texts");
            if (ids == null || values == null)
            {
                warnings.Add(Localization + " has no Ids / Texts lists; titles are taken from the action names.");
                return texts;
            }
            List<string> keys = ids.Children.Where(c => c.Children == null).Select(c => c.Head[0]).ToList();
            List<string> strings = values.Children.Where(c => c.Children == null).Select(c => c.Head[0]).ToList();
            if (keys.Count != strings.Count)
            {
                warnings.Add(Localization + " lists " + keys.Count.ToString(CultureInfo.InvariantCulture) + " ids but " + strings.Count.ToString(CultureInfo.InvariantCulture) + " texts; titles are taken from the action names.");
                return texts;
            }
            for (int i = 0; i < keys.Count; i++)
                texts[keys[i]] = strings[i];
            return texts;
        }
    }
}
