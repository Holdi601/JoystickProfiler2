using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // The folders of DCS's kneeboard (<Saved Games>\DCS\Kneeboard\<unit type>\): DCS shows the pictures of the folder
    // named after the unit type of the aircraft you fly (make_flyable in the module's entry.lua: "A-10C_2",
    // "F-15ESE", "OH58D"), not after its input profile ("A-10C II", "F-15E_WSO", "OH-58D Pilot"), which is the name
    // JoyPro's DCS list uses. OpenKneeboard's "DCS Aircraft" tab reads the same folder. Checked against the entry.lua
    // files of DCS 2.9 (November 2026); JoyPro 1 had a shorter table in Overlay\DCS\rename.plane.
    internal static class DcsKneeboardFolders
    {
        // Input profile (JoyPro's aircraft name) -> the unit types that use it. A seat or game-mode variant uses the
        // folder of its aircraft (the pilot and the RIO of the F-14 see the same kneeboard).
        private static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "A-10C", new[] { "A-10C" } },
            { "A-10C II", new[] { "A-10C_2" } },
            { "a-10a", new[] { "A-10A" } },
            { "A-4E-C", new[] { "A-4E-C" } },
            { "AH-64D_BLK_II_PLT", new[] { "AH-64D_BLK_II" } },
            { "AH-64D_BLK_II_CPG", new[] { "AH-64D_BLK_II" } },
            { "AJS37", new[] { "AJS37" } },
            { "AV8BNA", new[] { "AV8BNA" } },
            { "Bf-109K-4", new[] { "Bf-109K-4" } },
            { "C-101CC", new[] { "C-101CC" } },
            { "C-101EB", new[] { "C-101EB" } },
            { "C-130J", new[] { "C-130J-30" } },
            { "C-130J_LoadMaster", new[] { "C-130J-30" } },
            { "CH-47F", new[] { "CH-47Fbl1" } },
            { "CH-47F_GUN", new[] { "CH-47Fbl1" } },
            { "Christen Eagle II", new[] { "Christen Eagle II" } },
            { "F-100D", new[] { "F-100D" } },
            { "F-14", new[] { "F-14B", "F-14A-135-GR", "F-14A-135-GR-Early", "F-14A-95-GR" } },
            { "F-14_RIO", new[] { "F-14B", "F-14A-135-GR", "F-14A-135-GR-Early", "F-14A-95-GR" } },
            { "F-14BU", new[] { "F-14BU" } },
            { "F-14BU_RIO", new[] { "F-14BU" } },
            { "F-15C", new[] { "F-15C" } },
            { "F-15E", new[] { "F-15ESE" } },
            { "F-15E_WSO", new[] { "F-15ESE" } },
            { "F-16C_50", new[] { "F-16C_50" } },
            { "F-4E-45MC", new[] { "F-4E-45MC" } },
            { "F-4E-45MC_WSO", new[] { "F-4E-45MC" } },
            { "F-5E-3", new[] { "F-5E-3" } },
            { "F-86F", new[] { "F-86F Sabre" } },
            { "F-86F Sabre", new[] { "F-86F Sabre" } },
            { "F4U-1D", new[] { "F4U-1D", "F4U-1D_CW" } },
            { "FA-18C_hornet", new[] { "FA-18C_hornet" } },
            { "FW-190A8", new[] { "FW-190A8" } },
            { "FW-190D9", new[] { "FW-190D9" } },
            { "Hercules", new[] { "Hercules" } },
            { "I-16", new[] { "I-16" } },
            { "J-11A", new[] { "J-11A" } },
            { "JF-17", new[] { "JF-17" } },
            { "Ka-50 III", new[] { "Ka-50_3" } },
            { "ka-50", new[] { "Ka-50" } },
            { "L-39C", new[] { "L-39C" } },
            { "L-39ZA", new[] { "L-39ZA" } },
            { "La-7", new[] { "La-7" } },
            { "M-2000C", new[] { "M-2000C" } },
            { "MB-339A", new[] { "MB-339A" } },
            { "MB-339APAN", new[] { "MB-339APAN" } },
            { "Mi-24P_pilot", new[] { "Mi-24P" } },
            { "Mi-24P_operator", new[] { "Mi-24P" } },
            { "Mi-24P_AimingStation", new[] { "Mi-24P" } },
            { "Mi-24P_Gunner", new[] { "Mi-24P" } },
            { "Mi-24P_TrackIR_Gunner", new[] { "Mi-24P" } },
            { "mi-8mt", new[] { "Mi-8MT" } },
            { "mi-8mt_Gunner", new[] { "Mi-8MT" } },
            { "mi-8mt_TrackIR_Gunner", new[] { "Mi-8MT" } },
            { "MiG-15bis", new[] { "MiG-15bis" } },
            { "MiG-19P", new[] { "MiG-19P" } },
            { "MiG-21Bis", new[] { "MiG-21Bis" } },
            { "MiG-29-Fulcrum", new[] { "MiG-29 Fulcrum" } },
            { "mig-29", new[] { "MiG-29A" } },
            { "mig-29c", new[] { "MiG-29S" } },
            { "mig-29g", new[] { "MiG-29G" } },
            { "Mirage-F1BE", new[] { "Mirage-F1BE" } },
            { "Mirage-F1CE", new[] { "Mirage-F1CE" } },
            { "Mirage-F1EE", new[] { "Mirage-F1EE" } },
            { "Mirage-F1M-EE", new[] { "Mirage-F1M-EE" } },
            { "MosquitoFBMkVI", new[] { "MosquitoFBMkVI" } },
            { "OH-58D", new[] { "OH58D" } },
            { "OH-58D Pilot", new[] { "OH58D" } },
            { "OH-58D Copilot", new[] { "OH58D" } },
            { "OH-58D Copilot Personal Weapon", new[] { "OH58D" } },
            { "P-47D-30", new[] { "P-47D-30", "P-47D-30bl1", "P-47D-40" } },
            { "P-51D", new[] { "P-51D" } },
            { "P-51D-30-NA", new[] { "P-51D-30-NA" } },
            { "SA342", new[] { "SA342L", "SA342M", "SA342Mistral", "SA342Minigun" } },
            { "SA342 Pilot", new[] { "SA342L", "SA342M", "SA342Mistral", "SA342Minigun" } },
            { "SA342 Copilot", new[] { "SA342L", "SA342M", "SA342Mistral", "SA342Minigun" } },
            { "SA342 Gunner", new[] { "SA342L", "SA342M", "SA342Mistral", "SA342Minigun" } },
            { "SpitfireLFMkIX", new[] { "SpitfireLFMkIX" } },
            { "SpitfireLFMkIXCW", new[] { "SpitfireLFMkIXCW" } },
            { "su-25", new[] { "Su-25" } },
            { "su-25T", new[] { "Su-25T" } },
            { "su-27", new[] { "Su-27" } },
            { "su-33", new[] { "Su-33" } },
            { "TF-51D", new[] { "TF-51D" } },
            { "UH-1H", new[] { "UH-1H" } },
            { "UH-1H_Gunner", new[] { "UH-1H" } },
            { "UH-1H_TrackIR_Gunner", new[] { "UH-1H" } },
            { "Yak-52", new[] { "Yak-52" } },
        };

        // DCS input lists that are no aircraft (menus, cameras, radios, the ground units): they have no kneeboard.
        private static readonly HashSet<string> Layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Default", "NS430", "RadioVoIP", "VoiceChat", "UiLayer", "CameraFree", "CameraObject", "CameraRoom", "CommandMenu",
            "QuadMenu", "ground_unit", "C-130J_UI"
        };

        public static bool IsLayer(string plane)
        {
            string name = (plane ?? string.Empty).Trim();
            return name.Length == 0 || Layers.Contains(name) || Regex.IsMatch(name, @"_AI_?Menu$", RegexOptions.IgnoreCase);
        }

        // A game-mode or head-tracking variant ("F-16C_50_easy", "UH-1H_TrackIR_Gunner") and the aircraft it belongs to;
        // null when the name is no variant.
        public static string VariantBase(string plane)
        {
            string name = (plane ?? string.Empty).Trim();
            if (name.EndsWith("_easy", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - "_easy".Length);
            int trackIr = name.IndexOf("_TrackIR_", StringComparison.OrdinalIgnoreCase);
            if (trackIr > 0)
                return name.Substring(0, trackIr) + "_" + name.Substring(trackIr + "_TrackIR_".Length);
            return null;
        }

        // The kneeboard folders of an aircraft (JoyPro's DCS name). known = false: JoyPro does not know the aircraft and
        // the installed DCS has no module with it either; the aircraft's own name is used (right for most modules).
        public static List<string> Resolve(string plane, out bool known, Func<string, List<string>> installLookup = null)
        {
            string name = (plane ?? string.Empty).Trim();
            known = true;
            string[] folders;
            if (Known.TryGetValue(name, out folders))
                return folders.ToList();
            string basis = VariantBase(name);
            if (basis != null && Known.TryGetValue(basis, out folders))
                return folders.ToList();
            List<string> fromInstall = installLookup == null ? null : installLookup(basis ?? name);
            if (fromInstall != null && fromInstall.Count > 0)
                return fromInstall;
            known = false;
            return new List<string> { basis ?? name };
        }

        // The unit types the installed DCS gives an input profile: the make_flyable names of the module whose
        // InputProfiles (or Input folder) has it. Empty when DCS is not installed or no module has it.
        public static List<string> FromInstall(string plane, List<string> warnings)
        {
            List<string> result = new List<string>();
            string install;
            try
            {
                install = GamePathDiscovery.DiscoverDcsInstallPaths(warnings).FirstOrDefault(System.IO.Directory.Exists) ?? string.Empty;
            }
            catch (Exception)
            {
                return result;
            }
            if (install.Length == 0)
                return result;
            foreach (string root in new[] { Path.Combine(install, "Mods", "aircraft"), Path.Combine(install, "CoreMods", "aircraft") }.Where(System.IO.Directory.Exists))
            {
                foreach (string module in System.IO.Directory.GetDirectories(root))
                {
                    string entry = Path.Combine(module, "entry.lua");
                    if (!File.Exists(entry))
                        continue;
                    string text;
                    try
                    {
                        text = File.ReadAllText(entry);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    Match profiles = Regex.Match(text, @"InputProfiles\s*=\s*\{(.*?)\}", RegexOptions.Singleline);
                    bool hasProfile = profiles.Success && Regex.Matches(profiles.Groups[1].Value, @"\[\s*['""]([^'""]+)['""]\s*\]")
                        .Cast<Match>().Any(m => string.Equals(m.Groups[1].Value, plane, StringComparison.OrdinalIgnoreCase));
                    if (!hasProfile && !System.IO.Directory.Exists(Path.Combine(module, "Input", plane)))
                        continue;
                    result.AddRange(Regex.Matches(text, @"make_flyable\(\s*['""]([^'""]+)['""]").Cast<Match>().Select(m => m.Groups[1].Value));
                    if (result.Count > 0)
                        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            return result;
        }
    }
}
