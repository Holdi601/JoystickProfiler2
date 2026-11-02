using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JoyPro.Engine
{
    // Commands that DCS mods add to the controls list (docs/games/dcs.md "Commands from mods").
    //
    // - Quaggles' "DCS Input Command Injector" (github.com/Quaggles/dcs-input-command-injector, often used with community
    //   packs such as munkwolf's dcs-community-keybinds): its hook (<install>\Scripts\Hooks\QuagglesInputCommandInjector.lua,
    //   1.1.0) or, before 1.1.0, a patched <install>\Scripts\Input\Data.lua wraps DCS's loader of the input files. Every
    //   file DCS loads under ./Mods/aircraft/<x> or (./)Config/Input/<x> gets the file <Saved Games>\<variant>\InputCommands\<x>
    //   merged in, run in the same environment: its keyCommands and axisCommands (no axes for the keyboard) are joined to
    //   the module's. The headless refresh does the same merge itself (EngineDcsRefresh.AppendInputCommandsInjection),
    //   so the command list matches what DCS shows with the mod, whether or not DCS's own Data.lua is patched.
    // - Modules in <Saved Games>\<variant>\Mods\aircraft: DCS loads them like installed modules (the refresh lists them).
    //
    // Both live in the Saved Games folder the installed DCS uses: Saved Games\DCS, or DCS.<variant> for a variant install
    // (dcs_variant.txt "openbeta" -> DCS.openbeta). SavedGamesFor picks that folder.
    internal static class DcsModInputs
    {
        public const string InputCommandsFolderName = "InputCommands";

        // The injector's own files in InputCommands (its settings and its update-check state, rewritten by DCS starts).
        private static readonly HashSet<string> InjectorOwnFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "settings.lua", "state.lua" };

        // The Saved Games folder name an installed DCS uses: "DCS.<variant>" with the install's dcs_variant.txt
        // ("openbeta"), "DCS.openbeta" for an install folder named OpenBeta, else "DCS"; empty without an install folder.
        // (The GUI's MainWindowViewModel.DcsSavedGamesFolderName does the same.)
        public static string SavedGamesFolderName(string installPath)
        {
            string install = (installPath ?? string.Empty).Trim();
            if (install.Length == 0 || !Directory.Exists(install))
                return string.Empty;
            try
            {
                string variantFile = Path.Combine(install, "dcs_variant.txt");
                string variant = File.Exists(variantFile) ? File.ReadAllText(variantFile).Trim() : string.Empty;
                if (variant.Length > 0 && variant.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    return "DCS." + variant;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            string name = Path.GetFileName(install.TrimEnd('\\', '/')) ?? string.Empty;
            return name.IndexOf("openbeta", StringComparison.OrdinalIgnoreCase) >= 0 ? "DCS.openbeta" : "DCS";
        }

        // The Saved Games folder the installed DCS really reads, for a refresh given savedGamesPath: the given folder,
        // unless it is another DCS folder (Saved Games\DCS) next to the one this install uses (Saved Games\DCS.openbeta)
        // and the user did not set it as DCS's config location. note says what was switched (empty when nothing).
        public static string ForInstall(string savedGamesPath, string installPath, out string note)
        {
            note = string.Empty;
            string given = (savedGamesPath ?? string.Empty).TrimEnd('\\', '/');
            string own = SavedGamesFolderName(installPath);
            if (given.Length == 0 || own.Length == 0)
                return savedGamesPath;
            string name = Path.GetFileName(given) ?? string.Empty;
            if (string.Equals(name, own, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("DCS", StringComparison.OrdinalIgnoreCase))
                return savedGamesPath;
            string configOverride = GameInstalls.ConfigOverride("DCS");
            if (configOverride != null && string.Equals(configOverride.TrimEnd('\\', '/'), given, StringComparison.OrdinalIgnoreCase))
                return savedGamesPath;
            string parent = Path.GetDirectoryName(given);
            if (string.IsNullOrEmpty(parent))
                return savedGamesPath;
            string sibling = Path.Combine(parent, own);
            if (!Directory.Exists(sibling))
                return savedGamesPath;
            note = "This DCS installation uses Saved Games\\" + own + ", not Saved Games\\" + name + ": its mods, InputCommands and binds are read from there.";
            return sibling;
        }

        // The Saved Games folder of the installed DCS for the update check: the user's config location (Settings >
        // Games), else the discovered folder named for the install's variant, else the first discovered one.
        public static string SavedGamesFor(string installPath, List<string> warnings)
        {
            string configOverride = GameInstalls.ConfigOverride("DCS");
            if (configOverride != null && Directory.Exists(configOverride))
                return configOverride;
            List<string> found = GamePathDiscovery.DiscoverDcsSavedGamesPaths(warnings).Where(Directory.Exists).ToList();
            string own = SavedGamesFolderName(installPath);
            string match = found.FirstOrDefault(p => string.Equals(Path.GetFileName(p.TrimEnd('\\', '/')), own, StringComparison.OrdinalIgnoreCase));
            return match ?? found.FirstOrDefault();
        }

        // How the Input Command Injector is installed in this DCS: "hook" (Scripts\Hooks, 1.1.0 and later), "data.lua"
        // (a patched Scripts\Input\Data.lua, older versions) or empty (not installed: DCS does not merge InputCommands).
        public static string InjectorInstall(string installPath)
        {
            string install = (installPath ?? string.Empty).Trim();
            if (install.Length == 0 || !Directory.Exists(install))
                return string.Empty;
            try
            {
                string hooks = Path.Combine(install, "Scripts", "Hooks");
                if (Directory.Exists(hooks))
                {
                    foreach (string file in Directory.GetFiles(hooks, "*.lua"))
                    {
                        FileInfo info = new FileInfo(file);
                        if (info.Length > 2 * 1024 * 1024)
                            continue;
                        if (File.ReadAllText(file).IndexOf("InputCommandInjector", StringComparison.Ordinal) >= 0)
                            return "hook";
                    }
                }
                if (DataLuaPatched(install))
                    return "data.lua";
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return string.Empty;
        }

        // An older injector (before 1.1.0) replaced DCS's Scripts\Input\Data.lua with a copy that merges InputCommands.
        public static bool DataLuaPatched(string installPath)
        {
            try
            {
                string dataLua = Path.Combine(installPath ?? string.Empty, "Scripts", "Input", "Data.lua");
                return File.Exists(dataLua) && File.ReadAllText(dataLua).IndexOf("QuagglesInputCommandInjector", StringComparison.Ordinal) >= 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The command files of the InputCommands folder (its .lua files without the injector's settings and state).
        public static List<FileInfo> InputCommandFiles(string savedGamesPath)
        {
            string root = Path.Combine(savedGamesPath ?? string.Empty, InputCommandsFolderName);
            List<FileInfo> files = new List<FileInfo>();
            if (string.IsNullOrWhiteSpace(savedGamesPath) || !Directory.Exists(root))
                return files;
            try
            {
                foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*.lua", SearchOption.AllDirectories))
                {
                    if (string.Equals(file.DirectoryName?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && InjectorOwnFiles.Contains(file.Name))
                        continue;
                    files.Add(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return files;
        }

        // The input files of the modules in <Saved Games>\Mods\aircraft (each module's Input folder and its entry.lua).
        public static List<FileInfo> SavedGamesModInputFiles(string savedGamesPath)
        {
            List<FileInfo> files = new List<FileInfo>();
            string aircraft = Path.Combine(savedGamesPath ?? string.Empty, "Mods", "aircraft");
            if (string.IsNullOrWhiteSpace(savedGamesPath) || !Directory.Exists(aircraft))
                return files;
            try
            {
                foreach (DirectoryInfo module in new DirectoryInfo(aircraft).EnumerateDirectories())
                {
                    FileInfo entry = new FileInfo(Path.Combine(module.FullName, "entry.lua"));
                    if (entry.Exists)
                        files.Add(entry);
                    DirectoryInfo input = new DirectoryInfo(Path.Combine(module.FullName, "Input"));
                    if (input.Exists)
                        files.AddRange(input.EnumerateFiles("*.lua", SearchOption.AllDirectories));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return files;
        }

        // The update check's fingerprint of the mod input sources (names, sizes and dates only: cheap at startup), as
        // "<hash>;commands=<n>;mods=<n>;injector=<hook|data.lua|none>"; empty when there are none (no InputCommands
        // files, no Saved Games modules), so a DCS without mods never counts as changed.
        public static string Fingerprint(string installPath, string savedGamesPath)
        {
            List<FileInfo> commands = InputCommandFiles(savedGamesPath);
            List<FileInfo> mods = SavedGamesModInputFiles(savedGamesPath);
            if (commands.Count == 0 && mods.Count == 0)
                return string.Empty;
            string injector = InjectorInstall(installPath);
            string root = (savedGamesPath ?? string.Empty).TrimEnd('\\', '/') + "\\";
            StringBuilder lines = new StringBuilder();
            lines.Append("injector=").Append(injector).Append('\n');
            foreach (FileInfo file in commands.Concat(mods).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                string relative = file.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.FullName.Substring(root.Length) : file.FullName;
                lines.Append(relative.ToLowerInvariant()).Append('|').Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                     .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(lines.ToString()))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            return hash + ";commands=" + commands.Count.ToString(CultureInfo.InvariantCulture) + ";mods=" + mods.Count.ToString(CultureInfo.InvariantCulture) +
                   ";injector=" + (injector.Length > 0 ? injector : "none");
        }

        // "252 InputCommands files (Input Command Injector: hook), 0 Saved Games module files" for a fingerprint value.
        public static string DescribeFingerprint(string value)
        {
            Dictionary<string, string> parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (value ?? string.Empty).Split(';').Skip(1))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                    parts[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            string commands, mods, injector;
            parts.TryGetValue("commands", out commands);
            parts.TryGetValue("mods", out mods);
            parts.TryGetValue("injector", out injector);
            return (commands ?? "0") + " InputCommands file(s) (Input Command Injector: " + (string.IsNullOrEmpty(injector) || injector == "none" ? "not installed" : injector) + "), " +
                   (mods ?? "0") + " Saved Games module input file(s)";
        }
    }
}
