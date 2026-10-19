using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JoyPro.Engine.Games
{
    // The sims' own input data, for a catalog refresh without exported profiles:
    //   - the device presets Asobo ships (MSFS 2024: packages fs-input-profiles and asobo-input-profiles-pc,
    //     InputProfiles\**\*.xml; MSFS 2020: the install's Input folder), <DefaulftInput> files with every action
    //     a preset binds, per context;
    //   - the menu names of the actions: fs-base\en-US.locPak (JSON, "INPUT.KEY_..." -> "THROTTLE AXIS").
    // Steam installs keep these packages in <install>\Packages, Microsoft Store installs under the
    // InstalledPackagesPath of UserCfg.opt (Official\OneStore, Official2024\...). Read only.
    internal static class MsfsGameData
    {
        private static readonly string[] PresetPackages = { "fs-input-profiles", "asobo-input-profiles-pc" };
        private static readonly int[] SteamApps = { 2537590, 1250410 };
        private static readonly Regex InstalledPackagesPath = new Regex("^\\s*InstalledPackagesPath\\s+\"(?<path>[^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled);

        // Folders of one sim (2020 or 2024) that hold package folders (fs-base, fs-input-profiles, ...) or the 2020
        // Input folder.
        public static List<string> Roots(int sim, List<string> warnings)
        {
            List<string> roots = new List<string>();
            GameInstall found = GameInstalls.Find(sim == 2020 ? "MSFS2020" : "MSFS2024", warnings);
            if (found != null && found.Installed && !string.IsNullOrEmpty(found.InstallPath))
                roots.Add(found.InstallPath);
            // JOYPRO_INSTALL_DETECTION=off (tests): only the folder set with "engine games install set".
            if (Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) == "off")
                return roots.Where(Directory.Exists).ToList();
            foreach (string install in GameInstalls.SteamFolders(new[] { sim == 2020 ? SteamApps[1] : SteamApps[0] }, warnings))
                roots.Add(install);
            roots.AddRange(StoreContentFolders(sim));
            string roaming = MsfsUserProfiles.RoamingAppData();
            string local = MsfsUserProfiles.LocalAppData();
            string[] cfgs = sim == 2020
                ? new[]
                {
                    Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt"),
                    Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt")
                }
                : new[]
                {
                    Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt"),
                    Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt")
                };
            foreach (string cfg in cfgs)
            {
                try
                {
                    if (!File.Exists(cfg))
                        continue;
                    Match m = InstalledPackagesPath.Match(File.ReadAllText(cfg));
                    if (!m.Success || !Directory.Exists(m.Groups["path"].Value))
                        continue;
                    // Official\OneStore, Official2020\Steam, Official2024\Steam, ...
                    foreach (string official in Directory.GetDirectories(m.Groups["path"].Value, "Official*"))
                        roots.AddRange(Directory.GetDirectories(official));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return roots.Where(Directory.Exists).Select(r => Path.GetFullPath(r).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The user's own profiles may join a catalog refresh without --path, unless install detection is off for a
        // test that has no stand-in user folders (JOYPRO_TEST_MSFS_HOME): a test must not read the real profiles.
        public static bool UserProfilesAllowed()
        {
            return MsfsUserProfiles.UsesTestHome || Environment.GetEnvironmentVariable(GameInstalls.DetectionVariable) != "off";
        }

        // Microsoft Store / Xbox app installs: <drive>\XboxGames\<game>\Content (the folder the Xbox app installs to; it
        // also notes its install folders in <drive>\.GamingRoot), with the same Packages folder as the Steam install.
        public static List<string> StoreContentFolders(int sim)
        {
            string game = sim == 2020 ? "Microsoft Flight Simulator" : "Microsoft Flight Simulator 2024";
            List<string> folders = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                {
                    List<string> gamingRoots = new List<string> { Path.Combine(drive.RootDirectory.FullName, "XboxGames") };
                    gamingRoots.AddRange(ReadGamingRoot(Path.Combine(drive.RootDirectory.FullName, ".GamingRoot")).Select(r => Path.Combine(drive.RootDirectory.FullName, r.TrimStart('\\'))));
                    foreach (string root in gamingRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string content = Path.Combine(root, game, "Content");
                        if (Directory.Exists(content))
                            folders.Add(content);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return folders;
        }

        // .GamingRoot: "RGBX", uint32 count, then NUL-terminated UTF-16 folder paths relative to the drive.
        internal static List<string> ReadGamingRoot(string path)
        {
            List<string> roots = new List<string>();
            try
            {
                if (!File.Exists(path))
                    return roots;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 8 || bytes[0] != (byte)'R' || bytes[1] != (byte)'G' || bytes[2] != (byte)'B' || bytes[3] != (byte)'X')
                    return roots;
                string text = Encoding.Unicode.GetString(bytes, 8, (bytes.Length - 8) / 2 * 2);
                roots.AddRange(text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).Where(r => r.Length > 0 && r.IndexOfAny(Path.GetInvalidPathChars()) < 0));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return roots;
        }

        // The preset XML files of all roots (the same package from two roots counts once, by relative path).
        public static List<string> PresetFiles(List<string> roots)
        {
            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                List<string> folders = new List<string>();
                foreach (string package in PresetPackages)
                {
                    folders.Add(Path.Combine(root, "Packages", package, "InputProfiles"));
                    folders.Add(Path.Combine(root, package, "InputProfiles"));
                }
                folders.Add(Path.Combine(root, "Input"));
                foreach (string folder in folders.Where(Directory.Exists))
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        {
                            string key = Path.GetFileName(Path.GetDirectoryName(folder)) + "|" + file.Substring(folder.Length);
                            if (seen.Add(key))
                                files.Add(file);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return files;
        }

        // The locPak names every input event of the sim (2736 in MSFS 2024's build of 2026-10), flight and non-flight
        // alike, without its context; the presets bind only about a quarter of them. JoyPro's own rules decide which
        // named events are flight controls and which context a new one goes into (docs/games/msfs.md "Catalog"):
        //   - not flight: cameras, views, menus, UI, developer mode, debug, photo mode, xCloud, slew, EFB, toolbar, ATC,
        //     cursor, VR, replay, multiplayer, on-foot character, failures and blockages, recording;
        //   - context: lighter-than-air, helicopter, glider and the PLANE-only systems by name, everything else AIRCRAFT
        //     (the context of the 2024 Controls menu's aircraft systems).
        // Measured against the development list built from real exports (2026-10-12): 984 of the 996 flight events
        // the presets do not bind are kept; the context matches for 91 % of them (the rest are mostly MSFS 2020
        // INSTRUMENTS_CONTROL rows).
        private static readonly Regex NotFlight = new Regex(
            "^KEY_(" +
            "MENU|DEVMODE|DEBUG|VIEW|PHOTO|PC_|XCLOUD|CHASE|UI_|SLEW|EFB|CAMERA|TOOLBAR|PAN_|ATC|ZOOM|DEZOOM|CURSOR|VR_|" +
            "FREEZE|EYEPOINT|SR_|DRONE|REPLAY|DEMO|INGAMEPANEL|MAP_|INPUTS|SIM_|SOUND|AUDIO|HEADPHONE|GUI|MOUSE|SMART|" +
            "PAUSE|SEE_|3RD_PARTY|CLOSE_|TOOLTIP|KNEEBOARD|LOD|SCRIPT|MACRO|SITUATION|READOUTS|NEXT_|PREV_|SHOW_|SKIP|NEW_|" +
            "WINDOW|FLY_|FIXED_|HEAD_|VIRTUAL|INTERACTION|POINT_|DEFAULT|CHVPP|FORCE_|STOP_|INSTRUMENT_|" +
            "PANEL_(?!LIGHTS)|TRUE_COCKPIT|COCKPIT_(?!FLASHLIGHT)|FIRE_|CYCLE_|SWITCH_|TAKEOFF_|SELECT_|MISSION|" +
            "CHECKLIST|NOTEPAD|CHAT|TELEPORT|TIME_|SIMRATE|SIM_RATE|CAPTURE|ABORT|LIVE|RECORD|" +
            "MULTIPLAYER_(?!DISPLAY_PLAYERS)|" +
            "RESET_(?!G_FORCE|MAX_RPM)|" +
            "AXIS_(PAN|CAMERA|SLEW|DRONE|EYEPOINT|VIEW|ZOOM|MOUSE|CURSOR|SMART|CHASE|COCKPIT|PHOTO|EDITOR|PC_|XCLOUD|IND)|" +
            "TOGGLE_(MENU|UI|VR|DEBUG|FULLSCREEN|PAUSE|SOUND|CAMERA|VIEW|COCKPIT_TOOLTIP|COCKPIT_CAMERA|INSTRUMENT|PANEL|" +
            "TRACK|DRONE|AIRCRAFT_LABELS|MINIMAL|SMART|EXTERNAL_VIEW|ATC|TOOLTIP|MAP|FLIGHT_|FIXED_CAMERA|AIRPORT_NAME|" +
            "RACERESULTS|DAMAGE_TEXT|ENEMY|\\w*_FAILURE|PITOT_BLOCKAGE|STATIC_PORT_BLOCKAGE)|" +
            "ACTION_PC|ADVENTURE|ANALYSIS|BACK_ON_TRACK|EXIT$|EYE_TRACKING|FLIGHT_MAP|FULL_WINDOW|INVOKE_HELP|" +
            "JOYSTICK_CALIBRATE|KEYBOARD_OVERLAY|LABEL_COLOR|LETTERBOX|OVERLAYMENU|REFRESH_SCENERY|RELOAD_PANELS|" +
            "SCREEN_NARRATOR|SET_USER_POI|UNSET_USER_POI|SIMUI|SNAP_VIEW|SP_MULTIPLAYER|TEXT_SCROLL|USERINTERRUPT|" +
            "VIDEO_RECORD|YAXIS_INVERT|CONTROL_RELOAD|OBJECT_INTERACTION|GROUND_SERVICE|TOOLS_|FLIGHT_PHASE|LANDING_VIEW|" +
            "HUD_VIEW|PLANE_CONTROL|UNLOCK_TARGET)", RegexOptions.Compiled);

        public static bool IsFlightEvent(string id)
        {
            return !string.IsNullOrEmpty(id) && id.StartsWith("KEY_", StringComparison.Ordinal) && !NotFlight.IsMatch(id);
        }

        // The context a flight event the presets do not bind goes into (JoyPro's rule, see above).
        public static string GuessContext(string id)
        {
            string name = id ?? string.Empty;
            if (Regex.IsMatch(name, "AIRSHIP|BALLOON|BURNER"))
                return "LIGHTER_THAN_AIR";
            if (Regex.IsMatch(name, "HELI|ROTOR|COLLECTIVE|CYCLIC|AUTO_HOVER"))
                return "HELICOPTER";
            if (Regex.IsMatch(name, "WATER_BALLAST|TOW_PLANE"))
                return "GLIDER";
            if (Regex.IsMatch(name, "LIQUID_DROPPING|GRAPPLE_HOOK|LEAD_POLE|HYDRAULIC_ACTUATOR|SKYDIVE|G_LIMITER|FIREFIGHTING|TAIL_HOOK_HANDLE|WATER_RUDDER|GPWS_SWITCH|ANTISKID|PUSHBACK"))
                return "PLANE";
            return "AIRCRAFT";
        }

        // The context of a general event (camera, view, UI, ...) that only the locPak names: the context of the known
        // general actions whose names share the longest prefix with it ("KEY_INSTRUMENT_LOOK_UP" -> the context of
        // the other "KEY_INSTRUMENT_LOOK_..." actions; at least two words, "KEY_INSTRUMENT_..." alone is too wide), when
        // at least two thirds of them agree; null when no known action shares such a prefix or they disagree (the event
        // then stays out). Checked against a full General listing of a user's MSFS 2024 profile (2026-10-19): of the 720
        // general events only the locPak names, 490 sit in the sim's debug contexts, 170 in managed ones; this rule
        // places 63 right, 14 wrong (mostly debug ones) and leaves the rest out. known: action id -> its general context.
        public static string GuessGeneralContext(string id, IEnumerable<KeyValuePair<string, string>> known)
        {
            string name = StripKey(id);
            string[] words = name.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            List<KeyValuePair<string, string>> pairs = known.Where(k => !string.IsNullOrEmpty(k.Value) && !string.Equals(k.Key, id, StringComparison.Ordinal))
                .Select(k => new KeyValuePair<string, string>(StripKey(k.Key), k.Value)).ToList();
            for (int length = words.Length - 1; length >= 2; length--)
            {
                string prefix = string.Join("_", words.Take(length)) + "_";
                if (prefix.Length < 4)
                    continue;
                List<string> contexts = pairs.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.Value).ToList();
                if (contexts.Count == 0)
                    continue;
                IGrouping<string, string> top = contexts.GroupBy(c => c, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
                return top.Count() * 3 >= contexts.Count * 2 ? top.Key : null;
            }
            return null;
        }

        private static string StripKey(string id)
        {
            string name = id ?? string.Empty;
            return name.StartsWith("KEY_", StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        // ---- Readable titles ----
        // The locPak writes the menu names in capitals ("ELEVATOR AXIS", "THROTTLE 1 AXIS (-100 to 100%)"); the catalog
        // keeps them readable: "Elevator Axis", abbreviations the sim's panels use stay in capitals ("AP Pitch Ref").
        private static readonly HashSet<string> Acronyms = new HashSet<string>(StringComparer.Ordinal)
        {
            "AP", "ADF", "ADI", "AGL", "AI", "ALT", "AOA", "APU", "APR", "APP", "ATC", "ATIS", "ATS", "AUX", "BC", "CDI", "COM", "CRS", "CWS",
            "DH", "DME", "ECAM", "ECU", "EFB", "EFIS", "EGT", "EICAS", "ELT", "FADEC", "FCU", "FD", "FLC", "FMC", "FMS", "GA", "GPS", "GPWS",
            "GS", "HDG", "HSI", "HUD", "IAS", "IFF", "ILS", "ITT", "LNAV", "LOC", "LVL", "MCP", "MFD", "MSL", "ND", "NDB", "NAV", "OBS", "OAT",
            "PFD", "PTT", "QNH", "RPM", "SPD", "TACAN", "TCAS", "TOGA", "UHF", "VHF", "VNAV", "VOR", "VS", "VSI", "XPNDR", "YD", "AFCS", "SAS",
            "ID", "IR", "MP", "PC"
        };

        private static readonly HashSet<string> SmallWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "and", "or", "of", "the", "to", "for", "by", "with", "from"
        };

        // Words of an event name ("KEY_AP_PITCH_REF_INC_UP") written out for the fallback title.
        private static readonly Dictionary<string, string> EventWords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "INC", "INCREASE" }, { "INCR", "INCREASE" }, { "DEC", "DECREASE" }, { "DECR", "DECREASE" }, { "DN", "DOWN" },
            { "ELEV", "ELEVATOR" }, { "AILER", "AILERON" }, { "REF", "REFERENCE" }
        };

        // A name in capitals ("SET BURNER PITCH AXIS", "THROTTLE 1 AXIS (-100 to 100%)") in title case; a name written
        // in small letters stays as it is.
        public static string Readable(string name)
        {
            string text = (name ?? string.Empty).Trim();
            int capitals = text.Count(char.IsUpper);
            int smalls = text.Count(char.IsLower);
            if (text.Length == 0 || capitals == 0 || smalls * 4 > capitals)
                return text;
            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                string letters = new string(word.Where(char.IsLetter).ToArray());
                // Acronyms with a number ("NAV1", "COM2") and "VOR/ILS" keep their capitals.
                bool acronym = letters.Length > 0 && word.Split('/').All(part =>
                {
                    string l = new string(part.Where(char.IsLetter).ToArray());
                    return l.Length > 0 && Acronyms.Contains(l);
                });
                if (acronym || letters.Length == 0)
                    continue;
                string lower = word.ToLowerInvariant();
                if (i > 0 && SmallWords.Contains(lower))
                {
                    words[i] = lower;
                    continue;
                }
                StringBuilder sb = new StringBuilder(lower.Length);
                bool start = true;
                foreach (char c in lower)
                {
                    sb.Append(start && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                    if (char.IsLetter(c))
                        start = false;
                    else if (c == '/' || c == '-' || c == '(')
                        start = true;
                }
                words[i] = sb.ToString();
            }
            return string.Join(" ", words);
        }

        // The title an event had before it was named: its event name without "KEY_", underscores as spaces ("AXIS
        // ELEVATOR SET"). Only titles like that are renamed by a refresh.
        public static string RawTitle(string id)
        {
            string name = id ?? string.Empty;
            return (name.StartsWith("KEY_", StringComparison.Ordinal) ? name.Substring(4) : name).Replace('_', ' ');
        }

        // A readable title from the event name when the sim names none (no locPak found, or an event it does not
        // list): "KEY_AXIS_ELEVATOR_SET" -> "Elevator Axis", "KEY_THROTTLE1_AXIS_SET_EX1" -> "Throttle 1 Axis",
        // "KEY_AP_PITCH_REF_INC_UP" -> "AP Pitch Reference Increase Up".
        public static string TitleFromEventName(string id)
        {
            List<string> words = new List<string>();
            foreach (string part in RawTitle(id).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // "THROTTLE1" -> "THROTTLE 1"; "EX1" (the event's version) goes.
                if (Regex.IsMatch(part, "^EX[0-9]+$"))
                    continue;
                Match m = Regex.Match(part, "^([A-Z]{3,})([0-9]+)$");
                if (m.Success && !Acronyms.Contains(m.Groups[1].Value))
                {
                    words.Add(m.Groups[1].Value);
                    words.Add(m.Groups[2].Value);
                    continue;
                }
                string written;
                words.Add(EventWords.TryGetValue(part, out written) ? written : part);
            }
            bool axis = words.Contains("AXIS");
            if (axis)
            {
                // "AXIS ELEVATOR SET" / "THROTTLE AXIS SET": the axis named last, without "SET".
                words.RemoveAll(w => w == "AXIS" || w == "SET");
                words.Add("AXIS");
            }
            return words.Count == 0 ? RawTitle(id) : Readable(string.Join(" ", words));
        }

        // Action id -> menu name (en-US, readable), from the first fs-base\en-US.locPak found; empty when there is none.
        public static Dictionary<string, string> Titles(List<string> roots, List<string> warnings)
        {
            return LocStrings(roots, warnings, false);
        }

        // Action id -> the sim's own description ("INPUT.KEY_AXIS_ELEVATOR_SET_DESC": "Control the elevator axis."), the
        // blank ones left out; empty when there is no locPak.
        public static Dictionary<string, string> Descriptions(List<string> roots, List<string> warnings)
        {
            return LocStrings(roots, warnings, true);
        }

        private static Dictionary<string, string> LocStrings(List<string> roots, List<string> warnings, bool descriptions)
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string root in roots)
            {
                foreach (string file in new[] { Path.Combine(root, "Packages", "fs-base", "en-US.locPak"), Path.Combine(root, "fs-base", "en-US.locPak") })
                {
                    if (!File.Exists(file))
                        continue;
                    try
                    {
                        JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        Dictionary<string, object> doc = json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                        object package, strings;
                        if (doc == null || !doc.TryGetValue("LocalisationPackage", out package) || !(package is Dictionary<string, object>) ||
                            !((Dictionary<string, object>)package).TryGetValue("Strings", out strings) || !(strings is Dictionary<string, object>))
                            continue;
                        foreach (KeyValuePair<string, object> entry in (Dictionary<string, object>)strings)
                        {
                            if (!entry.Key.StartsWith("INPUT.KEY_", StringComparison.Ordinal) || entry.Key.EndsWith("_DESC", StringComparison.Ordinal) != descriptions ||
                                !(entry.Value is string) || ((string)entry.Value).Trim().Length == 0)
                                continue;
                            string key = entry.Key.Substring("INPUT.".Length);
                            if (descriptions)
                                titles[key.Substring(0, key.Length - "_DESC".Length)] = ((string)entry.Value).Trim();
                            else
                                titles[key] = Readable((string)entry.Value);
                        }
                        return titles;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
                    {
                        warnings.Add("Could not read the MSFS action names from " + file + ": " + ex.Message);
                    }
                }
            }
            return titles;
        }
    }
}
