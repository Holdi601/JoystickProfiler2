using System;
using System.Collections.Generic;

namespace JoyPro.Engine.Games
{
    // JoyPro's own list of the FSX / Prepar3D control events it manages (docs/games/fsx-p3d.md). The control file
    // (Standard.XML, SimControls) names every action by its event id (e.g. GEAR_TOGGLE), so the ids are the game's
    // public names (FSX/P3D SDK "Event IDs"); titles and categories are JoyPro's. No SDK text is copied.
    // Row: "<event id>|a|b|<category>|<title>" (a = axis event, b = button/key event).
    internal static class FsxEvents
    {
        public const string Flight = "Flight controls";
        public const string Trim = "Trim";
        public const string Engines = "Engines";
        public const string GearBrakes = "Gear and brakes";
        public const string FlapsSpoilers = "Flaps and spoilers";
        public const string Lights = "Lights";
        public const string Autopilot = "Autopilot";
        public const string Views = "Views";
        public const string Helicopter = "Helicopter";
        public const string Simulation = "Simulation";
        public const string Electrical = "Electrical and systems";

        private static readonly string[] Rows =
        {
            // Primary axes.
            "AXIS_AILERONS_SET|a|" + Flight + "|Ailerons (roll) axis",
            "AXIS_ELEVATOR_SET|a|" + Flight + "|Elevator (pitch) axis",
            "AXIS_RUDDER_SET|a|" + Flight + "|Rudder (yaw) axis",
            "AXIS_THROTTLE_SET|a|" + Engines + "|Throttle axis (all engines)",
            "AXIS_THROTTLE1_SET|a|" + Engines + "|Throttle 1 axis",
            "AXIS_THROTTLE2_SET|a|" + Engines + "|Throttle 2 axis",
            "AXIS_THROTTLE3_SET|a|" + Engines + "|Throttle 3 axis",
            "AXIS_THROTTLE4_SET|a|" + Engines + "|Throttle 4 axis",
            "AXIS_PROPELLER_SET|a|" + Engines + "|Propeller axis (all engines)",
            "AXIS_PROPELLER1_SET|a|" + Engines + "|Propeller 1 axis",
            "AXIS_PROPELLER2_SET|a|" + Engines + "|Propeller 2 axis",
            "AXIS_PROPELLER3_SET|a|" + Engines + "|Propeller 3 axis",
            "AXIS_PROPELLER4_SET|a|" + Engines + "|Propeller 4 axis",
            "AXIS_MIXTURE_SET|a|" + Engines + "|Mixture axis (all engines)",
            "AXIS_MIXTURE1_SET|a|" + Engines + "|Mixture 1 axis",
            "AXIS_MIXTURE2_SET|a|" + Engines + "|Mixture 2 axis",
            "AXIS_MIXTURE3_SET|a|" + Engines + "|Mixture 3 axis",
            "AXIS_MIXTURE4_SET|a|" + Engines + "|Mixture 4 axis",
            "AXIS_LEFT_BRAKE_SET|a|" + GearBrakes + "|Left toe brake axis",
            "AXIS_RIGHT_BRAKE_SET|a|" + GearBrakes + "|Right toe brake axis",
            "AXIS_SPOILER_SET|a|" + FlapsSpoilers + "|Spoilers axis",
            "AXIS_FLAPS_SET|a|" + FlapsSpoilers + "|Flaps axis",
            "AXIS_STEERING_SET|a|" + GearBrakes + "|Nose wheel steering axis (Prepar3D v5+)",
            "AXIS_ELEV_TRIM_SET|a|" + Trim + "|Elevator trim axis",
            "AXIS_PAN_PITCH|a|" + Views + "|View pan pitch axis",
            "AXIS_PAN_HEADING|a|" + Views + "|View pan heading axis",
            "AXIS_PAN_TILT|a|" + Views + "|View pan tilt axis",

            // Flight controls as buttons.
            "ELEV_UP|b|" + Flight + "|Elevator up (nose down)",
            "ELEV_DOWN|b|" + Flight + "|Elevator down (nose up)",
            "AILERONS_LEFT|b|" + Flight + "|Ailerons left",
            "AILERONS_RIGHT|b|" + Flight + "|Ailerons right",
            "RUDDER_LEFT|b|" + Flight + "|Rudder left",
            "RUDDER_RIGHT|b|" + Flight + "|Rudder right",
            "RUDDER_CENTER|b|" + Flight + "|Rudder centre",
            "CENTER_AILER_RUDDER|b|" + Flight + "|Centre ailerons and rudder",

            // Trim.
            "ELEV_TRIM_UP|b|" + Trim + "|Elevator trim up (nose down)",
            "ELEV_TRIM_DN|b|" + Trim + "|Elevator trim down (nose up)",
            "AILERON_TRIM_LEFT|b|" + Trim + "|Aileron trim left",
            "AILERON_TRIM_RIGHT|b|" + Trim + "|Aileron trim right",
            "RUDDER_TRIM_LEFT|b|" + Trim + "|Rudder trim left",
            "RUDDER_TRIM_RIGHT|b|" + Trim + "|Rudder trim right",

            // Flaps and spoilers.
            "FLAPS_UP|b|" + FlapsSpoilers + "|Flaps fully up",
            "FLAPS_DOWN|b|" + FlapsSpoilers + "|Flaps fully down",
            "FLAPS_INCR|b|" + FlapsSpoilers + "|Flaps one notch down",
            "FLAPS_DECR|b|" + FlapsSpoilers + "|Flaps one notch up",
            "FLAPS_1|b|" + FlapsSpoilers + "|Flaps position 1",
            "FLAPS_2|b|" + FlapsSpoilers + "|Flaps position 2",
            "FLAPS_3|b|" + FlapsSpoilers + "|Flaps position 3",
            "SPOILERS_TOGGLE|b|" + FlapsSpoilers + "|Spoilers toggle",
            "SPOILERS_ON|b|" + FlapsSpoilers + "|Spoilers extend",
            "SPOILERS_OFF|b|" + FlapsSpoilers + "|Spoilers retract",
            "SPOILERS_ARM_TOGGLE|b|" + FlapsSpoilers + "|Spoilers arm toggle",

            // Gear and brakes.
            "GEAR_TOGGLE|b|" + GearBrakes + "|Gear toggle",
            "GEAR_UP|b|" + GearBrakes + "|Gear up",
            "GEAR_DOWN|b|" + GearBrakes + "|Gear down",
            "GEAR_PUMP|b|" + GearBrakes + "|Gear hand pump",
            "BRAKES|b|" + GearBrakes + "|Brakes (both)",
            "BRAKES_LEFT|b|" + GearBrakes + "|Left brake",
            "BRAKES_RIGHT|b|" + GearBrakes + "|Right brake",
            "PARKING_BRAKES|b|" + GearBrakes + "|Parking brake",
            "ANTISKID_BRAKES_TOGGLE|b|" + GearBrakes + "|Anti-skid toggle",
            "TOGGLE_TAIL_HOOK_HANDLE|b|" + GearBrakes + "|Tail hook toggle",
            "TOGGLE_WATER_RUDDER|b|" + GearBrakes + "|Water rudder toggle",

            // Engines.
            "THROTTLE_FULL|b|" + Engines + "|Throttle full",
            "THROTTLE_CUT|b|" + Engines + "|Throttle cut",
            "THROTTLE_INCR|b|" + Engines + "|Throttle increase",
            "THROTTLE_INCR_SMALL|b|" + Engines + "|Throttle increase (small)",
            "THROTTLE_DECR|b|" + Engines + "|Throttle decrease",
            "THROTTLE_DECR_SMALL|b|" + Engines + "|Throttle decrease (small)",
            "PROP_PITCH_INCR|b|" + Engines + "|Propeller pitch increase",
            "PROP_PITCH_DECR|b|" + Engines + "|Propeller pitch decrease",
            "PROP_PITCH_HI|b|" + Engines + "|Propeller pitch high RPM",
            "PROP_PITCH_LO|b|" + Engines + "|Propeller pitch low RPM",
            "MIXTURE_INCR|b|" + Engines + "|Mixture richer",
            "MIXTURE_DECR|b|" + Engines + "|Mixture leaner",
            "MIXTURE_RICH|b|" + Engines + "|Mixture full rich",
            "MIXTURE_LEAN|b|" + Engines + "|Mixture idle cut-off",
            "ENGINE_AUTO_START|b|" + Engines + "|Engine auto start",
            "ENGINE_AUTO_SHUTDOWN|b|" + Engines + "|Engine auto shutdown",
            "MAGNETO_INCR|b|" + Engines + "|Magnetos next position",
            "MAGNETO_DECR|b|" + Engines + "|Magnetos previous position",
            "TOGGLE_STARTER1|b|" + Engines + "|Starter 1",
            "TOGGLE_STARTER2|b|" + Engines + "|Starter 2",
            "ANTI_ICE_TOGGLE|b|" + Engines + "|Engine anti-ice toggle",
            "TOGGLE_PROPELLER_DEICE|b|" + Engines + "|Propeller de-ice toggle",
            "PITOT_HEAT_TOGGLE|b|" + Electrical + "|Pitot heat toggle",

            // Electrical and systems.
            "TOGGLE_MASTER_BATTERY|b|" + Electrical + "|Master battery toggle",
            "TOGGLE_MASTER_ALTERNATOR|b|" + Electrical + "|Master alternator toggle",
            "TOGGLE_MASTER_BATTERY_ALTERNATOR|b|" + Electrical + "|Master battery and alternator toggle",
            "TOGGLE_AVIONICS_MASTER|b|" + Electrical + "|Avionics master toggle",
            "TOGGLE_FUEL_VALVE_ALL|b|" + Electrical + "|Fuel valves toggle",
            "SMOKE_TOGGLE|b|" + Electrical + "|Smoke system toggle",
            "TOW_PLANE_RELEASE|b|" + Electrical + "|Tow rope release",

            // Lights.
            "ALL_LIGHTS_TOGGLE|b|" + Lights + "|All lights toggle",
            "LANDING_LIGHTS_TOGGLE|b|" + Lights + "|Landing lights toggle",
            "TOGGLE_TAXI_LIGHTS|b|" + Lights + "|Taxi lights toggle",
            "TOGGLE_NAV_LIGHTS|b|" + Lights + "|Navigation lights toggle",
            "TOGGLE_BEACON_LIGHTS|b|" + Lights + "|Beacon toggle",
            "STROBES_TOGGLE|b|" + Lights + "|Strobe lights toggle",
            "PANEL_LIGHTS_TOGGLE|b|" + Lights + "|Panel lights toggle",
            "TOGGLE_LOGO_LIGHTS|b|" + Lights + "|Logo lights toggle",
            "TOGGLE_WING_LIGHTS|b|" + Lights + "|Wing lights toggle",
            "TOGGLE_CABIN_LIGHTS|b|" + Lights + "|Cabin lights toggle",

            // Autopilot.
            "AP_MASTER|b|" + Autopilot + "|Autopilot master toggle",
            "AUTOPILOT_ON|b|" + Autopilot + "|Autopilot on",
            "AUTOPILOT_OFF|b|" + Autopilot + "|Autopilot off",
            "YAW_DAMPER_TOGGLE|b|" + Autopilot + "|Yaw damper toggle",
            "AP_WING_LEVELER|b|" + Autopilot + "|Wing leveler toggle",
            "AP_HDG_HOLD|b|" + Autopilot + "|Heading hold toggle",
            "AP_ALT_HOLD|b|" + Autopilot + "|Altitude hold toggle",
            "AP_ATT_HOLD|b|" + Autopilot + "|Attitude hold toggle",
            "AP_NAV1_HOLD|b|" + Autopilot + "|NAV hold toggle",
            "AP_APR_HOLD|b|" + Autopilot + "|Approach hold toggle",
            "AP_LOC_HOLD|b|" + Autopilot + "|Localizer hold toggle",
            "AP_BC_HOLD|b|" + Autopilot + "|Back course hold toggle",
            "AP_AIRSPEED_HOLD|b|" + Autopilot + "|Airspeed hold toggle",
            "AP_MACH_HOLD|b|" + Autopilot + "|Mach hold toggle",
            "AUTO_THROTTLE_ARM|b|" + Autopilot + "|Autothrottle arm",
            "AUTO_THROTTLE_TO_GA|b|" + Autopilot + "|Autothrottle TO/GA",
            "HEADING_BUG_INC|b|" + Autopilot + "|Heading bug right",
            "HEADING_BUG_DEC|b|" + Autopilot + "|Heading bug left",
            "AP_ALT_VAR_INC|b|" + Autopilot + "|Selected altitude up",
            "AP_ALT_VAR_DEC|b|" + Autopilot + "|Selected altitude down",
            "AP_VS_VAR_INC|b|" + Autopilot + "|Selected vertical speed up",
            "AP_VS_VAR_DEC|b|" + Autopilot + "|Selected vertical speed down",
            "AP_SPD_VAR_INC|b|" + Autopilot + "|Selected airspeed up",
            "AP_SPD_VAR_DEC|b|" + Autopilot + "|Selected airspeed down",

            // Helicopter.
            "ROTOR_BRAKE|b|" + Helicopter + "|Rotor brake",
            "ROTOR_CLUTCH_SWITCH_TOGGLE|b|" + Helicopter + "|Rotor clutch toggle",
            "ROTOR_GOV_SWITCH_TOGGLE|b|" + Helicopter + "|Rotor governor toggle",

            // Views.
            "PAN_VIEW|b|" + Views + "|View pan (hat switch)",
            "VIEW_MODE|b|" + Views + "|Next view mode",
            "VIEW_MODE_REV|b|" + Views + "|Previous view mode",
            "VIEW_RESET|b|" + Views + "|Reset view",
            "PAN_RESET_COCKPIT|b|" + Views + "|Reset cockpit view",
            "PAN_UP|b|" + Views + "|Pan up",
            "PAN_DOWN|b|" + Views + "|Pan down",
            "PAN_LEFT|b|" + Views + "|Pan left",
            "PAN_RIGHT|b|" + Views + "|Pan right",
            "ZOOM_IN|b|" + Views + "|Zoom in",
            "ZOOM_OUT|b|" + Views + "|Zoom out",
            "VIEW_VIRTUAL_COCKPIT_FORWARD|b|" + Views + "|Virtual cockpit view",
            "VIEW_COCKPIT_FORWARD|b|" + Views + "|2D cockpit view",
            "NEXT_VIEW|b|" + Views + "|Next view",
            "PREV_VIEW|b|" + Views + "|Previous view",

            // Simulation.
            "PAUSE_TOGGLE|b|" + Simulation + "|Pause toggle",
            "SIM_RATE_INCR|b|" + Simulation + "|Simulation rate up",
            "SIM_RATE_DECR|b|" + Simulation + "|Simulation rate down",
            "SIM_RATE|b|" + Simulation + "|Simulation rate select"
        };

        public static IEnumerable<GameAction> All()
        {
            foreach (string row in Rows)
            {
                string[] parts = row.Split(new[] { '|' }, 4);
                yield return new GameAction
                {
                    Id = parts[0],
                    IsAxis = parts[1] == "a",
                    Category = parts[2],
                    Title = parts[3]
                };
            }
        }

        // An event id JoyPro does not list (found in the user's file): its title from the id, axis when it is an AXIS_ event.
        public static GameAction FromId(string id)
        {
            string value = (id ?? string.Empty).Trim().ToUpperInvariant();
            bool axis = value.StartsWith("AXIS_", StringComparison.Ordinal);
            string title = value.Replace('_', ' ').ToLowerInvariant();
            if (title.Length > 0)
                title = char.ToUpperInvariant(title[0]) + title.Substring(1);
            return new GameAction { Id = value, IsAxis = axis, Category = axis ? Flight : "Other", Title = title };
        }
    }
}
