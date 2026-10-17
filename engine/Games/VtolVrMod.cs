using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // VTOL VR's BYOJoystick mod on the user's PC (docs/games/vtol-vr.md "BYOJoystick mod"). The mod has no license
    // (all rights reserved by its author, sicsix); it is free on the Steam Workshop (item 3276352374) for the
    // "VTOL VR Mod Loader" (free Steam app 3018410), its only official binary source: GitHub has the source code but no
    // release files. JoyPro never ships or copies it: the user subscribes in Steam (Steam downloads, checks and installs
    // the item into <library>\steamapps\workshop\content\3018410\3276352374, where the Mod Loader loads it), and JoyPro
    // reads the target list from the installed BYOJoystick.dll, read only.
    internal static class VtolVrMod
    {
        public const string WorkshopItem = "3276352374";
        public const int ModLoaderApp = 3018410;
        public const int GameApp = 667970;
        public const string WorkshopPage = "https://steamcommunity.com/sharedfiles/filedetails/?id=3276352374";
        public const string SourcePage = "https://github.com/sicsix/BYOJoystick";
        public const string ModLoaderPage = "https://store.steampowered.com/app/3018410/";
        // Size Steam lists for the item (Workshop page, 2026-10-12, version 1.3.2 of 2024-09-08).
        public const long ListedSize = 1435000;
        public const string LicenseText =
            "BYOJoystick by sicsix has no license: the author publishes the source code on GitHub without a license file " +
            "(all rights reserved) and distributes the mod free of charge only on the Steam Workshop. JoyPro does not copy, " +
            "change or ship it. Steam downloads it to your PC under the Steam Subscriber Agreement when you subscribe; JoyPro " +
            "only reads which commands it offers.";
        // Test hook: a folder that stands for the installed Workshop item (or one holding BYOJoystick.dll).
        public const string ModFolderVariable = "JOYPRO_VTOLVR_MOD_DIR";

        public sealed class Status
        {
            public bool ModLoaderInstalled;
            public string ModLoaderPath = string.Empty;
            public bool ModInstalled;
            public string ModFolder = string.Empty;
            public string DllPath = string.Empty;
            public string DllSha256 = string.Empty;
            public long DllSize;
            public string DllVersion = string.Empty;
            public int Targets;
            public List<string> Notes = new List<string>();
        }

        public static Status Find(List<string> warnings)
        {
            Status status = new Status();
            string forced = Environment.GetEnvironmentVariable(ModFolderVariable);
            // JOYPRO_INSTALL_DETECTION=off (tests): no Steam library of the PC is looked at.
            bool detection = Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) != "off";
            List<string> libraries = string.IsNullOrWhiteSpace(forced) && detection ? GamePathDiscovery.GetSteamLibraryRoots(warnings ?? new List<string>()) : new List<string>();
            foreach (string library in libraries)
            {
                string manifest = Path.Combine(library, "steamapps", "appmanifest_" + ModLoaderApp.ToString(CultureInfo.InvariantCulture) + ".acf");
                if (File.Exists(manifest) && !status.ModLoaderInstalled)
                {
                    status.ModLoaderInstalled = true;
                    status.ModLoaderPath = manifest;
                }
            }
            List<string> folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(forced))
            {
                folders.Add(forced.Trim().Trim('"'));
                status.ModLoaderInstalled = true;
                status.ModLoaderPath = "(" + ModFolderVariable + ")";
            }
            foreach (string library in libraries)
            {
                folders.Add(Path.Combine(library, "steamapps", "workshop", "content", ModLoaderApp.ToString(CultureInfo.InvariantCulture), WorkshopItem));
                folders.Add(Path.Combine(library, "steamapps", "workshop", "content", GameApp.ToString(CultureInfo.InvariantCulture), WorkshopItem));
            }
            foreach (string folder in folders.Where(f => Directory.Exists(f) || File.Exists(f)))
            {
                string dll = FindDll(folder);
                status.ModInstalled = true;
                status.ModFolder = folder;
                if (dll == null)
                {
                    status.Notes.Add("The Workshop item is there, but BYOJoystick.dll is not (Steam may still be downloading it).");
                    break;
                }
                status.DllPath = dll;
                try
                {
                    FileInfo info = new FileInfo(dll);
                    status.DllSize = info.Length;
                    using (FileStream stream = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (SHA256 sha = SHA256.Create())
                        status.DllSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
                    System.Diagnostics.FileVersionInfo version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
                    status.DllVersion = version.FileVersion ?? string.Empty;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings?.Add("Could not read " + dll + ": " + ex.Message);
                }
                break;
            }
            return status;
        }

        // engine vtolvr mod status|prepare --accept-license
        //   status: the Mod Loader and the mod on this PC (Workshop folder, BYOJoystick.dll with SHA-256, size and file
        //     version, the targets JoyPro can read from it), the official source, the size Steam lists and the license.
        //   prepare: needs --accept-license (the user ticked the consent); backs up the user's BYOJ_<seat>.xml files into
        //     the backup store (engine backups) and returns the Steam addresses to open: the Mod Loader's install page
        //     when it is missing, then the Workshop item's page, where the user subscribes. Steam downloads, verifies and
        //     installs the item; JoyPro starts nothing itself (the GUI opens the addresses) and writes no mod file.
        public static int RunCommand(List<string> args, bool textOutput, out string payload)
        {
            string action = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
            List<string> warnings = new List<string>();
            Status status = Find(warnings);
            if (status.DllPath.Length > 0)
            {
                List<string> seats;
                List<Target> targets = ReadTargets(status.DllPath, warnings, out seats);
                status.Targets = targets == null ? 0 : targets.Count;
            }
            if (action == "status")
            {
                payload = Render(status, new List<string>(), new List<string>(), warnings, textOutput);
                return 0;
            }
            if (action != "prepare")
            {
                payload = EngineSerialization.ToJson(new { Errors = new[] { "Unknown vtolvr mod action: " + action + ". Use status or prepare --accept-license." } });
                return 1;
            }
            if (!args.Skip(1).Any(a => string.Equals(a, "--accept-license", StringComparison.OrdinalIgnoreCase)))
            {
                payload = EngineSerialization.ToJson(new { Errors = new[] { "BYOJoystick is not JoyPro's: read its license (" + LicenseText + ") and pass --accept-license to go on." } });
                return 1;
            }
            List<string> backedUp = new List<string>();
            string saveData = SaveDataFolder(warnings);
            if (saveData != null)
            {
                foreach (string file in Directory.GetFiles(saveData, "BYOJ_*.xml"))
                {
                    GameBackups.Protect("VTOLVR", file, warnings);
                    backedUp.Add(file);
                }
            }
            List<string> open = new List<string>();
            if (!status.ModLoaderInstalled)
                open.Add("steam://install/" + ModLoaderApp.ToString(CultureInfo.InvariantCulture));
            if (!status.ModInstalled)
                open.Add("steam://url/CommunityFilePage/" + WorkshopItem);
            payload = Render(status, open, backedUp, warnings, textOutput);
            return 0;
        }

        private static string SaveDataFolder(List<string> warnings)
        {
            GameInstall install = GameInstalls.Find("VTOLVR", warnings);
            string folder = install != null && install.Installed ? Path.Combine(install.InstallPath, "SaveData") : null;
            return folder != null && Directory.Exists(folder) ? folder : null;
        }

        private static string Render(Status status, List<string> open, List<string> backedUp, List<string> warnings, bool textOutput)
        {
            if (!textOutput)
            {
                return EngineSerialization.ToJson(new
                {
                    status.ModLoaderInstalled,
                    status.ModInstalled,
                    status.ModFolder,
                    status.DllPath,
                    status.DllSha256,
                    status.DllSize,
                    status.DllVersion,
                    status.Targets,
                    WorkshopItem,
                    WorkshopPage,
                    SourcePage,
                    ModLoaderPage,
                    ListedSize,
                    License = LicenseText,
                    Verification = "Steam downloads the item and checks it against the Workshop's content manifest; JoyPro shows the SHA-256 of the installed BYOJoystick.dll. The author publishes no checksum or signature.",
                    OpenUrls = open,
                    BackedUp = backedUp,
                    status.Notes,
                    Warnings = warnings
                });
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("BYOJoystick (VTOL VR mod)");
            sb.AppendLine("Mod Loader: " + (status.ModLoaderInstalled ? "installed" : "not installed (" + ModLoaderPage + ")"));
            sb.AppendLine("Mod: " + (status.ModInstalled ? status.ModFolder : "not installed (" + WorkshopPage + ")"));
            if (status.DllPath.Length > 0)
            {
                sb.AppendLine("DLL: " + status.DllPath + " (" + status.DllSize.ToString(CultureInfo.InvariantCulture) + " bytes, version " + status.DllVersion + ")");
                sb.AppendLine("SHA-256: " + status.DllSha256);
                sb.AppendLine("Targets JoyPro reads: " + status.Targets.ToString(CultureInfo.InvariantCulture));
            }
            sb.AppendLine("License: " + LicenseText);
            foreach (string url in open)
                sb.AppendLine("Open: " + url);
            foreach (string file in backedUp)
                sb.AppendLine("Backed up: " + file);
            foreach (string note in status.Notes.Concat(warnings))
                sb.AppendLine("Note: " + note);
            return sb.ToString();
        }

        private static string FindDll(string folder)
        {
            try
            {
                if (File.Exists(folder) && string.Equals(Path.GetFileName(folder), "BYOJoystick.dll", StringComparison.OrdinalIgnoreCase))
                    return folder;
                return Directory.GetFiles(folder, "BYOJoystick.dll", SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A target of the mod: its name (the <Target> text), the seats (vehicle ShortName) that have it, its input type
        // (axis, axis_centered, button) and its category (the mod's ActionCategory).
        public sealed class Target
        {
            public string Name;
            public string Kind;
            public string Category;
            public List<string> Seats = new List<string>();
        }

        // The mod adds its targets in the vehicle managers with helper calls such as FlightAxisC("Joystick Pitch", ...)
        // or LightsButton("Nav Lights Toggle", ...): <Category><Axis|AxisC|Button>, the target name being the first
        // string argument; the base manager adds "Reset VR Position" (StaticButton) and the "Modifier" button to every
        // vehicle. A manager's seat is the string its ShortName property returns. Read from the DLL's metadata and IL;
        // the DLL is not loaded.
        private static readonly Regex Helper = new Regex("^(Flight|Assist|Nav|Systems|HUD|NumPad|Display|Radio|Music|Lights|Misc)(Axis|AxisC|Button)$|^StaticButton$", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> Categories = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Flight", "Flight" }, { "Assist", "FlightAssist" }, { "Nav", "Navigation" }, { "Systems", "Systems" }, { "HUD", "HUD" },
            { "NumPad", "NumPad" }, { "Display", "Displays" }, { "Radio", "Radio" }, { "Music", "Music" }, { "Lights", "Lights" }, { "Misc", "Misc" }
        };

        public static List<Target> ReadTargets(string dllPath, List<string> warnings, out List<string> seats)
        {
            seats = new List<string>();
            ClrMetadata metadata = ClrMetadata.Load(dllPath, warnings);
            if (metadata == null)
                return null;
            List<Target> targets = new List<Target>();
            Dictionary<string, Target> byName = new Dictionary<string, Target>(StringComparer.Ordinal);
            List<string> common = new List<string>();
            Dictionary<string, string> commonKinds = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (ClrMetadata.TypeInfo type in metadata.Types)
            {
                ClrMetadata.MethodInfo shortName = type.Methods.FirstOrDefault(m => m.Name == "get_ShortName");
                string seat = shortName == null ? null : metadata.FirstString(shortName);
                bool isBase = type.Name == "Manager";
                if (string.IsNullOrWhiteSpace(seat) && !isBase)
                    continue;
                if (!isBase && !seats.Contains(seat))
                    seats.Add(seat);
                foreach (ClrMetadata.MethodInfo method in type.Methods)
                {
                    foreach (ClrMetadata.CallSite call in metadata.Calls(method, name => Helper.IsMatch(name ?? string.Empty)))
                    {
                        if (call.Strings.Count == 0)
                            continue;
                        string name = call.Strings[0];
                        Match helper = Helper.Match(call.Method);
                        string kind = call.Method == "StaticButton" || helper.Groups[2].Value == "Button" ? "button" : helper.Groups[2].Value == "AxisC" ? "axis_centered" : "axis";
                        string category = call.Method == "StaticButton" ? "Misc" : Categories[helper.Groups[1].Value];
                        if (isBase)
                        {
                            if (!common.Contains(name))
                            {
                                common.Add(name);
                                commonKinds[name] = kind + "\t" + category;
                            }
                            continue;
                        }
                        Target target;
                        if (!byName.TryGetValue(name, out target))
                        {
                            target = new Target { Name = name, Kind = kind, Category = category };
                            byName[name] = target;
                            targets.Add(target);
                        }
                        if (!target.Seats.Contains(seat))
                            target.Seats.Add(seat);
                    }
                }
            }
            if (seats.Count == 0)
                return targets;
            // The base manager's targets and the modifier exist in every vehicle seat.
            foreach (string name in new[] { "Modifier" }.Concat(common))
            {
                if (byName.ContainsKey(name))
                    continue;
                string[] kindCategory = name == "Modifier" ? new[] { "button", "Modifier" } : commonKinds[name].Split('\t');
                Target target = new Target { Name = name, Kind = kindCategory[0], Category = kindCategory[1], Seats = new List<string>(seats) };
                byName[name] = target;
                targets.Insert(name == "Modifier" ? 0 : targets.Count, target);
            }
            return targets;
        }
    }
}
