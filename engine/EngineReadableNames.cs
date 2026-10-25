using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine
{
    // Readable category and action names outside DCS (docs/relation-builder.md "Readable names"). The game lists keep
    // the game's own ids (MSFS contexts such as "AIRCRAFT", Elite's "ship_flight", Star Citizen's action maps): the game
    // integrations use them. The catalog, search, suggestions and relation editor show these names instead:
    //   - a table per game where the game's menu has its own group names (MSFS, Elite, Star Citizen, X-Plane, VTOL VR);
    //   - otherwise a readable form of the id: "flight_controls" -> "Flight controls", "2.06 AUX COMM PANEL" ->
    //     "2.06 Aux Comm Panel", "FlightAssist" -> "Flight assist" (known abbreviations stay in capitals).
    // DCS is left alone (its own titles and categories).
    internal static class EngineReadableNames
    {
        private static readonly Dictionary<string, string> MsfsCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AIRCRAFT", "Aircraft" },
            { "AIRCRAFT_PC", "Aircraft (PC)" },
            { "AIRCRAFT_ACTIVE_INTERACTION", "Aircraft: cockpit interaction" },
            { "AIRPLANE", "Airplane" },
            { "BASIC_CONTROL", "Basic controls" },
            { "BASIC_CONTROL_HELICOPTER", "Basic controls: helicopter" },
            { "BASIC_CONTROL_LIGHTER_THAN_AIR", "Basic controls: airship and balloon" },
            { "EFB", "Electronic flight bag (EFB)" },
            { "EFB_CORE", "Electronic flight bag (EFB): text fields and back" },
            { "PLAYER_CHARACTER_FPV", "On foot: first person view" },
            { "RTC", "Flight intro cinematic (RTC)" },
            { "GLIDER", "Glider" },
            { "HELICOPTER", "Helicopter" },
            { "HELICOPTER_ACTIVE_INTERACTION", "Helicopter: cockpit interaction" },
            { "INSTRUMENTS_CONTROL", "Instruments" },
            { "LIGHTER_THAN_AIR", "Airship and balloon" },
            { "PLANE", "Plane" },
            { "RUDDER_AXIS", "Rudder axis" },
            { "SURFACES", "Control surfaces" },
            { "SURFACES_GYRO", "Control surfaces: gyroscope" },
            { "SURFACES_HELICOPTER", "Control surfaces: helicopter" },
            { "SURFACES_LIGHTER_THAN_AIR", "Control surfaces: airship and balloon" },
            { "THROTTLE", "Throttle" },
            { "TRANSVERSAL", "General (all aircraft)" },
        };

        private static readonly Dictionary<string, string> EliteCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ship_flight", "Ship: flight" },
            { "ship_flight_landing", "Ship: landing overrides" },
            { "ship_drive", "Ship: drives" },
            { "ship_modes", "Ship: mode switches" },
            { "ship_targeting", "Ship: targeting" },
            { "ship_weapons", "Ship: weapons" },
            { "ship_defence", "Ship: defence" },
            { "ship_power", "Ship: power distribution" },
            { "ship_systems", "Ship: systems" },
            { "ship_headlook", "Ship: head look" },
            { "ship_multicrew", "Ship: multicrew" },
            { "ship_fighter_orders", "Ship: fighter orders" },
            { "ship_fss", "Ship: Full Spectrum System scanner" },
            { "ship_dss", "Ship: Detailed Surface Scanner" },
            { "galaxy_map", "Galaxy map" },
            { "system_map", "System map" },
            { "srv_driving", "SRV: driving" },
            { "srv_turret", "SRV: turret" },
            { "on_foot", "On foot" },
            { "camera", "Camera" },
        };

        private static readonly Dictionary<string, string> StarCitizenCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "spaceship_movement", "Flight: movement" },
            { "spaceship_general", "Flight: general" },
            { "spaceship_view", "Flight: view" },
            { "spaceship_quantum", "Flight: quantum travel" },
            { "spaceship_docking", "Flight: docking" },
            { "spaceship_targeting", "Flight: targeting" },
            { "spaceship_targeting_advanced", "Flight: advanced targeting" },
            { "spaceship_target_hailing", "Flight: hailing" },
            { "spaceship_weapons", "Flight: weapons" },
            { "spaceship_missiles", "Flight: missiles" },
            { "spaceship_auto_weapons", "Flight: automated weapons" },
            { "spaceship_defensive", "Flight: countermeasures and shields" },
            { "spaceship_power", "Flight: power" },
            { "spaceship_radar", "Flight: radar" },
            { "spaceship_scanning", "Flight: scanning" },
            { "spaceship_mining", "Flight: mining" },
            { "spaceship_salvage", "Flight: salvage" },
            { "spaceship_hud", "Flight: HUD" },
            { "IFCS_controls", "Flight: IFCS (flight control system)" },
            { "seat_general", "Seat: general" },
            { "turret_movement", "Turret: movement" },
            { "turret_advanced", "Turret: advanced" },
            { "vehicle_driver", "Ground vehicle: driver" },
            { "vehicle_general", "Ground vehicle: general" },
            { "vehicle_mfd", "Vehicle: MFD screens" },
            { "vehicle_mobiglas", "Vehicle: mobiGlas" },
            { "lights_controller", "Lights" },
            { "tractor_beam", "Tractor beam" },
            { "mining", "On foot: mining" },
            { "player", "On foot: general" },
            { "player_choice", "On foot: interaction choices" },
            { "player_emotes", "On foot: emotes" },
            { "player_input_optical_tracking", "Head tracking" },
            { "prone", "On foot: prone" },
            { "incapacitated", "On foot: incapacitated" },
            { "zero_gravity_eva", "EVA: zero gravity" },
            { "zero_gravity_traversal", "EVA: zero-g traversal" },
            { "hacking", "Hacking" },
            { "mapui", "Map" },
            { "ui_notification", "UI: notifications" },
            { "ui_textfield", "UI: text fields" },
            { "character_customizer", "Character customizer" },
            { "flycam", "Free camera" },
            { "spectator", "Spectator" },
            { "stopwatch", "Stopwatch" },
            { "view_director_mode", "Director camera" },
            { "debug", "Debug" },
            { "default", "General" },
            { "server_renderer", "Server renderer" },
            { "RemoteRigidEntityController", "Remote controlled vehicle" },
        };

        private static readonly Dictionary<string, string> XPlaneCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "FMS2", "FMS 2" },
            { "audio_panel_copilot", "Audio panel (copilot)" },
            { "altair", "Altair" },
            { "Axis", "Axes" },
        };

        private static readonly Dictionary<string, string> VtolCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "FlightAssist", "Flight assist" },
            { "NumPad", "Number pad" },
        };

        // Abbreviations kept in capitals.
        private static readonly HashSet<string> Acronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AA", "AG", "ADF", "ADI", "AP", "APU", "ATC", "AUTO", "AVTR", "CDI", "CDU", "CMDS", "COM", "DED", "DME", "DSS", "ECM", "EFIS", "EGT", "ELT",
            "EPU", "ESP", "EVA", "FADEC", "FCR", "FD", "FLIR", "FMS", "FSD", "FSS", "GNS", "GPS", "HMCS", "HOTAS", "HSI", "HUD", "IAS", "ICP", "IFCS", "IFF", "ILS",
            "INS", "JFS", "MFD", "MCDU", "MFDS", "NAV", "NVG", "OBS", "PC", "PFD", "PFL", "PWR", "RPM", "RWR", "SNSR", "SRV", "TACAN", "TCAS", "TGP", "TV",
            "UFC", "UHF", "UI", "VHF", "VNAV", "VOR", "VR", "VTOL", "XPDR"
        };

        // The category as people read it.
        public static string Category(string game, string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            if (text.Length == 0 || string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
                return text;
            string named;
            Dictionary<string, string> table = TableOf(game);
            if (table != null && table.TryGetValue(text, out named))
                return named;
            // Only what looks like an id changes: "flight_controls", "galaxy", "FlightAssist", "2.06 AUX COMM PANEL";
            // "Helicopter and plane" stays as the game writes it.
            if (text.IndexOf('_') >= 0 || IsLowerToken(text))
                return Readable(text, sentence: true);
            if (!text.Any(char.IsLower) && Regex.IsMatch(text, "[A-Z]{4,}"))
                return Readable(text, sentence: false);
            return text;
        }

        // DCS: a command the module's lua gives no category (its axes and some view commands) is listed under DCS's own
        // "Axis Commands" filter, the others under "Other Commands"; never an empty category.
        public static string DcsCategory(string category, bool isAxis)
        {
            string c = (category ?? string.Empty).Trim();
            return c.Length > 0 ? c : isAxis ? "Axis Commands" : "Other Commands";
        }

        // DCS: a title written as a program name ("Canopy_Handle" in the F-15E) reads as words (a lowercase key such as
        // "flaps_1" stays: the key search matches it); everything else as the
        // module names it (localization keys are resolved elsewhere, DcsCommandNames).
        public static string DcsTitle(string title)
        {
            string t = (title ?? string.Empty).Trim();
            return Regex.IsMatch(t, "^[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$") ? t.Replace('_', ' ') : t;
        }

        // IL-2 Great Battles: an action the game's file gives no text for keeps its text key ("inprb_pitch"): JoyPro's own
        // name of it (DB\Functions\ActionTexts\IL2Game.tsv, "Mouse turret aim: up/down"); without one the key in words.
        public static string Il2Title(string id, string title)
        {
            string t = (title ?? string.Empty).Trim();
            string own = EngineActionTexts.Title("IL2Game", id, t);
            if (!string.Equals(own, t, StringComparison.Ordinal))
                return own;
            if (t.Length > 0 && t.IndexOf('_') < 0 && !t.StartsWith("inp", StringComparison.Ordinal))
                return Title("IL2Game", t);
            string key = t.StartsWith("inp", StringComparison.Ordinal) && t.Length > 3 ? t.Substring(3) : (t.Length > 0 ? t : id ?? string.Empty);
            key = Regex.Replace(key, "^cam_", "camera_");
            key = Regex.Replace(key, "^ext_action([0-9]+)$", "extra_action_$1");
            key = key.Replace("gopro", "GoPro");
            string[] words = key.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length <= 2 && words[i].All(char.IsLetter))
                    words[i] = words[i].ToUpperInvariant();
                else if (i == 0)
                    words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            }
            return words.Length == 0 ? t : string.Join(" ", words);
        }

        private static Dictionary<string, string> TableOf(string game)
        {
            string g = game ?? string.Empty;
            if (g.StartsWith("MSFS", StringComparison.OrdinalIgnoreCase))
                return MsfsCategories;
            if (g.StartsWith("XPlane", StringComparison.OrdinalIgnoreCase))
                return XPlaneCategories;
            if (g.StartsWith("VTOLVR", StringComparison.OrdinalIgnoreCase))
                return VtolCategories;
            switch (g)
            {
                case "EliteDangerous":
                    return EliteCategories;
                case "StarCitizen":
                    return StarCitizenCategories;
            }
            return null;
        }

        // An action title as people read it: underscores, run-together words and titles written in capitals made
        // readable ("FMS: key_0" -> "FMS: key 0", "HeliBrakes" -> "Heli Brakes", "TOGGLE PUSHBACK" -> "Toggle Pushback").
        public static string Title(string game, string title)
        {
            string text = (title ?? string.Empty).Trim();
            if (text.Length == 0 || string.Equals(game, "DCS", StringComparison.OrdinalIgnoreCase))
                return text;
            if (text.IndexOf('_') >= 0)
                text = Regex.Replace(text.Replace('_', ' '), @"\s+", " ").Trim();
            if (Regex.IsMatch(text, "^[A-Z][a-z]+(?:[A-Z][a-z]+)+$"))
                return Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ");
            if (!text.Any(char.IsLower) && Regex.Matches(text, "[A-Z]{5,}").Count > 0)
                return Readable(text, sentence: false);
            return text;
        }

        // A Star Citizen action id without a game text: "v_strafe_up" -> "Strafe up", "v_ifcs_toggle_vector_decoupling"
        // -> "IFCS toggle vector decoupling".
        public static string FromStarCitizenId(string id)
        {
            string text = id ?? string.Empty;
            foreach (string prefix in new[] { "v_", "pc_", "foip_" })
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length)
                {
                    text = text.Substring(prefix.Length);
                    break;
                }
            }
            return Readable(Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", "_"), sentence: true);
        }

        // A Star Citizen text key in CamelCase without the game's text: "@ui_CIEmoteForward" -> "Emote forward",
        // "@ui_COGroundVehicleViewY" -> "Ground vehicle view y" (the two-letter context prefix CI/CO dropped). Null for
        // keys that are ids ("@ui_v_weapon_preset_qid").
        public static string FromStarCitizenKey(string key)
        {
            string k = (key ?? string.Empty).Trim().TrimStart('@');
            if (!k.StartsWith("ui_", StringComparison.Ordinal))
                return null;
            k = k.Substring(3);
            if (!Regex.IsMatch(k, "^(?:C[A-Z])?[A-Z][a-z]+(?:[A-Z][a-z0-9]*)+$"))
                return null;
            k = Regex.Replace(k, "^C[A-Z](?=[A-Z][a-z])", string.Empty);
            return Readable(Regex.Replace(k, "(?<=[a-z0-9])(?=[A-Z])", "_"), sentence: true);
        }

        private static bool IsLowerToken(string text)
        {
            return text.IndexOf(' ') < 0 && text.All(c => !char.IsLetter(c) || char.IsLower(c)) ||
                   Regex.IsMatch(text, "^[A-Z][a-z]+(?:[A-Z][a-z]+)+$");
        }

        // sentence: "Flight controls" (first word capitalized); else Title Case ("Aux Comm Panel"). Abbreviations and
        // numbers stay as they are.
        private static string Readable(string text, bool sentence)
        {
            string spaced = Regex.Replace(text.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
            string[] words = spaced.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                string letters = new string(w.Where(char.IsLetter).ToArray());
                string written;
                if (letters.Length == 0)
                    written = w;
                else if (Acronyms.Contains(w) || Acronyms.Contains(letters) && w.All(c => char.IsLetter(c) || char.IsDigit(c)))
                    written = w.ToUpperInvariant();
                else if (Regex.IsMatch(w, "^[A-Za-z]+[0-9]+$") && Acronyms.Contains(Regex.Replace(w, "[0-9]+$", string.Empty)))
                    written = w.ToUpperInvariant();
                else
                {
                    string lower = w.ToLowerInvariant();
                    written = (i == 0 || !sentence) ? char.ToUpperInvariant(lower[0]) + lower.Substring(1) : lower;
                }
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(written);
            }
            return sb.Length == 0 ? text : sb.ToString();
        }
    }
}
