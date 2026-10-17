#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.Setup
{
    // How important a function is: its category (flight controls > weapons, countermeasures and sensors > systems >
    // views and comms > display keys), how often it is used and the priority of its aircraft.
    public static class SetupImportance
    {
        public const string Flight = "flight";
        public const string Weapons = "weapons";
        public const string Systems = "systems";
        public const string Views = "views";
        public const string Displays = "displays";

        private static readonly Regex FlightIds = new Regex(
            @"^(pitch|roll|rudder|throttle|rpm_|space_strafe|space_yaw|space_speed_limiter|wheelbrake|nozzle|trim|airbrake|airbakeextend|airbrakeretract|" +
            @"afterburner|space_boost|space_decouple|space_cruise_control|f14_wing_sweep|f14_dlc|reverse_thrust|fbw_disconnect|autopilot_off)", RegexOptions.Compiled);
        private static readonly Regex WeaponIds = new Regex(
            @"^(trigger|launchweapon|weaponselector|select_|master_arm|gun|cms|target_management|a10_dms|sensor_select|tdc|radar|mode_a2|uncage|laser|flir|" +
            @"f_16_|tws_man|hcu_|ah64_track|ah64_cpg|f14_lantirn|jettison|mi24|clearradiation|ajs_|nosewheel_steering_lasing|wingspan|reset_curser|fov_toggle|" +
            @"space_lock|space_target|space_missile|space_fire|space_weapons|space_activate_radar|space_pin|space_unpin|space_power_triangle|space_shield|" +
            @"space_g_safety|space_mining|space_scanning|space_salvage|tgpcameraon|wp_)", RegexOptions.Compiled);
        private static readonly Regex ViewIds = new Regex(
            @"^(zoom|comm\d|easycomms|ai_|kneeboard|space_cycle_camera|space_look_ahead|space_docking_camera|ns430|salute|set\w*seat|toggle_crew_ai|" +
            @"hideaimingarrow|toggle_units|mic)", RegexOptions.Compiled);

        public static string Category(SetupFunction function)
        {
            if (function == null || function.Id.Length == 0)
                return Systems;
            if (function.Kind == "mfd-osb" || function.Kind == "ufc-key")
                return Displays;
            if (function.Kind == "ns430")
                return Views;
            // The knowledge map's category first (relations functions), the slot's own words for the rest.
            switch (function.MapCategory)
            {
                case "flight":
                    return Flight;
                case "engine":
                    return function.IsAxis ? Flight : Systems;
                case "weapons":
                case "countermeasures":
                case "hotas":
                case "sensors":
                    return Weapons;
                case "nav":
                case "lights":
                case "systems":
                    return Systems;
                case "comms":
                case "view":
                case "sim":
                    return Views;
            }
            string id = function.Id.ToLowerInvariant();
            if (FlightIds.IsMatch(id))
                return Flight;
            if (WeaponIds.IsMatch(id))
                return Weapons;
            if (ViewIds.IsMatch(id))
                return Views;
            return Systems;
        }

        public static double CategoryWeight(string category)
        {
            switch (category)
            {
                case Flight: return 8;
                case Weapons: return 4;
                case Systems: return 2;
                case Views: return 1;
                default: return 0.5;
            }
        }

        public static string CategoryTitle(string category)
        {
            switch (category)
            {
                case Flight: return "Flight controls";
                case Weapons: return "Weapons, countermeasures, sensors";
                case Systems: return "Systems";
                case Views: return "Views and comms";
                default: return "Display keys";
            }
        }

        public static double FrequencyWeight(string frequency)
        {
            switch (frequency)
            {
                case "constant": return 5;
                case "frequent": return 3;
                case "occasional": return 2;
                default: return 1;
            }
        }

        public static int FrequencyRank(string frequency)
        {
            switch (frequency)
            {
                case "constant": return 4;
                case "frequent": return 3;
                case "occasional": return 2;
                case "rare": return 1;
                default: return 0;
            }
        }

        // Flight controls, weapons, countermeasures and sensors used constantly or often: what must be on the grips.
        public static bool Essential(string category, string frequency)
        {
            return (category == Flight || category == Weapons) && FrequencyRank(frequency) >= 3;
        }

        // Priority weight per aircraft: tier 0 = 1, each tier below 0.6 of the one above; unlisted aircraft after all
        // tiers. Without a priority list every aircraft weighs 1.
        public static Dictionary<string, double> Weights(List<List<string>> tiers, IEnumerable<SetupAircraft> aircraft, out Dictionary<string, int> tierOf)
        {
            tierOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            List<List<string>> list = (tiers ?? new List<List<string>>()).Where(t => t != null && t.Any(a => !string.IsNullOrWhiteSpace(a))).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                foreach (string plane in list[i].Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    if (!tierOf.ContainsKey(plane.Trim()))
                        tierOf[plane.Trim()] = i;
                }
            }
            double unlisted = list.Count == 0 ? 1.0 : Math.Pow(0.6, list.Count) * 0.5;
            List<SetupAircraft> all = aircraft.ToList();
            foreach (SetupAircraft plane in all)
            {
                int tier;
                string key = tierOf.ContainsKey(plane.Key) ? plane.Key : plane.VariantOf.Length > 0 && tierOf.ContainsKey(plane.VariantOf) ? plane.VariantOf : plane.Game;
                if (tierOf.TryGetValue(key, out tier))
                {
                    weights[plane.Key] = Math.Pow(0.6, tier);
                    if (!tierOf.ContainsKey(plane.Key))
                        tierOf[plane.Key] = tier;
                }
                else
                {
                    weights[plane.Key] = unlisted;
                }
            }
            // Input layers (DCS Default, NS430, voice) count like the average aircraft of their game.
            foreach (SetupAircraft layer in all.Where(a => a.SharedLayer))
            {
                List<double> game = all.Where(a => !a.SharedLayer && string.Equals(a.Game, layer.Game, StringComparison.OrdinalIgnoreCase)).Select(a => weights[a.Key]).ToList();
                weights[layer.Key] = game.Count == 0 ? unlisted : game.Average();
            }
            return weights;
        }
    }
}
