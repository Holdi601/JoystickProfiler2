using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.Services;

// The plain function names the quick review shows (SetupReviewRow.PlainName), in the language shown: a fixed list, so a
// name the list does not know (a DCS slot such as "AH64 CPG IAT", an aircraft's own command) stays as the game calls it.
// The list: the actions of the games on the pretend controller (ACE COMBAT 7 and 8, Hell Let Loose: Vietnam; the
// engine's PadFunctionNames joins "Yaw left" + "Yaw right" to "Yaw"), the plain slot names IL-2 and the other flight
// games get from the setup catalog (DB/Functions/slots.json), and the face buttons of PlayStation-style sticks
// (T.Flight HOTAS 4: Square, Cross, Circle, Triangle).
public static class FunctionNames
{
    private static readonly HashSet<string> Known = new(
    [
        // the pretend controller's games (engine/Games/AceCombat7Integration.cs, AceCombat8Integration.cs,
        // HllVietnamIntegration.cs), as the quick review shows them
        N("Function name: Pitch"), N("Function name: Roll"), N("Function name: Yaw"), N("Function name: Yaw left"), N("Function name: Yaw right"), N("Function name: Rudder"), N("Function name: Throttle"), N("Function name: Throttle up"), N("Function name: Throttle down"),
        N("Function name: Look left/right"), N("Function name: Look up/down"), N("Function name: Look"), N("Function name: Free look"), N("Function name: Change view"), N("Function name: Zoom"),
        N("Function name: Wingman order"), N("Function name: Wingman order up"), N("Function name: Wingman order down"), N("Function name: Wingman order left"), N("Function name: Wingman order right"),
        N("Function name: Fire gun"), N("Function name: Fire missile"), N("Function name: Fire missile / special weapon"), N("Function name: Change weapon"), N("Function name: Switch target"), N("Function name: Radar map"),
        N("Function name: Flares"), N("Function name: High-G turn"), N("Function name: Autopilot"), N("Function name: Pause"), N("Function name: Hat up"), N("Function name: Hat down"), N("Function name: Hat left"), N("Function name: Hat right"),
        N("Function name: Collective"), N("Function name: Collective up"), N("Function name: Collective down"), N("Function name: Cyclic pitch"), N("Function name: Cyclic roll"), N("Function name: Stability"), N("Function name: Auto-level"),
        N("Function name: Supply drop"), N("Function name: Fire recon flare"), N("Function name: Medic"), N("Function name: Medic radial"), N("Function name: Next seat"),
        N("Function name: Pitch up"), N("Function name: Pitch down"), N("Function name: Roll left"), N("Function name: Roll right"), N("Function name: Cargo drop / sling"),

        // plain slot names of the flight games (IL-2 Great Battles and others)
        N("Function name: Afterburner"), N("Function name: Airbrake toggle"), N("Function name: Airbrake retract"), N("Function name: Airbrake extend"), N("Function name: Autopilot - On"), N("Function name: Autopilot - Off"),
        N("Function name: Autopilot - Attitude hold"), N("Function name: Autopilot - Altitude Hold"), N("Function name: Autopilot - Heading Hold"), N("Function name: Autopilot - Pitch on"),
        N("Function name: Autopilot - Roll on"), N("Function name: Autopilot - Yaw on"), N("Function name: Autopilot - Pitch off"), N("Function name: Autopilot - Roll off"), N("Function name: Autopilot - Yaw off"),
        N("Function name: Bomb Bay Doors"), N("Function name: Canopy open"), N("Function name: Canopy close"), N("Function name: Canopy toggle"), N("Function name: Eject"), N("Function name: Flaps up"), N("Function name: Flaps down"),
        N("Function name: Flaps toggle"), N("Function name: Gear in"), N("Function name: Gear out"), N("Function name: Gear toggle"), N("Function name: Jettison Fuel"), N("Function name: Jettison Weapons"), N("Function name: Kneeboard toggle"),
        N("Function name: Launch weapon"), N("Function name: Left engine start"), N("Function name: Right engine start"), N("Function name: Left engine stop"), N("Function name: Right engine stop"),
        N("Function name: Lights - EXT - Landing-Taxi-On"), N("Function name: Lights - EXT - Landing-Taxi-Off"), N("Function name: Lights - EXT - Navigation On"),
        N("Function name: Lights - EXT - Navigation Off"), N("Function name: Lights - EXT- Formation+"), N("Function name: Lights - EXT- Formation-"), N("Function name: Lights - IN - Instrument+"),
        N("Function name: Lights - IN - Instrument-"), N("Function name: Master ARM toggle"), N("Function name: Parking brake"), N("Function name: RPM Control"), N("Function name: RPM Increase"), N("Function name: RPM Decrease"),
        N("Function name: Radiator Axis"), N("Function name: Radiator close"), N("Function name: Radiator open"), N("Function name: Reload"), N("Function name: Tailwheel lock"), N("Function name: Throttle left"), N("Function name: Throttle right"),
        N("Function name: Trim Axis - Aileron"), N("Function name: Trim Axis - Elevator"), N("Function name: Trim Axis - Rudder"), N("Function name: Trim left"), N("Function name: Trim right"), N("Function name: Trim nose up"),
        N("Function name: Trim nose down"), N("Function name: Trim reset"), N("Function name: Trim rudder left"), N("Function name: Trim rudder right"), N("Function name: View Center"), N("Function name: View Cockpit"),
        N("Function name: View External"), N("Function name: View Padlock"), N("Function name: View: look back"), N("Function name: View: look forward"), N("Function name: View: look left"), N("Function name: View: look right"),
        N("Function name: View: look left / right axis"), N("Function name: View: look up / down axis"), N("Function name: Wheel brakes"), N("Function name: Wheel brake left analog"),
        N("Function name: Wheel brake right analog"), N("Function name: Weapon selector1"), N("Function name: Fire Extinguisher"), N("Function name: Engine Primer"), N("Function name: Fuel Pump"), N("Function name: Pitot Heat"),

        // IL-2 Great Battles' own action names (DB/Functions/ActionTexts/IL2Game.tsv) as its quick review and the Binds
        // list show them, and the names the setup gives IL-2's relations ("Fire", "Map mode")
        N("Function name: AI autopilot on/off"), N("Function name: AI level flight on/off"), N("Function name: Aileron trim"), N("Function name: Aileron trim: left"), N("Function name: Aileron trim: right"),
        N("Function name: Air brakes: in"), N("Function name: Air brakes: out/in"), N("Function name: Autopilot: heading channel on/off"), N("Function name: Autopilot: pitch channel on/off"), N("Function name: Autopilot: roll channel on/off"),
        N("Function name: Bail out"), N("Function name: Bomb bay doors open/close"), N("Function name: Bomb safety on/off"), N("Function name: Boost / WEP on/off"), N("Function name: Camera: cockpit"),
        N("Function name: Cockpit light on/off"), N("Function name: Cockpit view: zoom"), N("Function name: Cockpit view: zoom in"), N("Function name: Cockpit view: zoom out"), N("Function name: Cockpit view: zoom reset"),
        N("Function name: Command menu"), N("Function name: Drop bombs"), N("Function name: Drop tank: jettison"), N("Function name: Elevator trim"), N("Function name: Elevator trim: nose down"),
        N("Function name: Elevator trim: nose up"), N("Function name: Engine 1 start/stop"), N("Function name: Engine 1 throttle"), N("Function name: Engine 2 start/stop"), N("Function name: Engine 2 throttle"),
        N("Function name: External camera: your aircraft"), N("Function name: External camera: zoom"), N("Function name: Fire"), N("Function name: Fire all guns"), N("Function name: Fire gun group 1"),
        N("Function name: Fire gun group 2"), N("Function name: Fire rockets"), N("Function name: Formation lights on/off"), N("Function name: Jettison stores"), N("Function name: Landing gear down"),
        N("Function name: Landing gear up"), N("Function name: Landing gear up/down"), N("Function name: Landing lights on/off"), N("Function name: Map mode"), N("Function name: Mixture"),
        N("Function name: Navigation lights on/off"), N("Function name: Padlock: enemy aircraft"), N("Function name: Parking brake on"), N("Function name: Parking brake on/off"), N("Function name: Propeller RPM"),
        N("Function name: Propeller: coarser pitch"), N("Function name: Propeller: finer pitch"), N("Function name: Reload guns"), N("Function name: Rudder trim"), N("Function name: Rudder trim: left"),
        N("Function name: Rudder trim: right"), N("Function name: Snap view: back"), N("Function name: Snap view: centre"), N("Function name: Snap view: forward"), N("Function name: Snap view: left"),
        N("Function name: Snap view: right"), N("Function name: Tail wheel lock on/off"), N("Function name: Trim: reset all"), N("Function name: Water radiator"), N("Function name: Water radiator: close"),
        N("Function name: Water radiator: open"), N("Function name: Wheel brake: left"), N("Function name: Wheel brake: right"),

        // face buttons printed as shapes on PlayStation-style sticks
        N("Function name: Square"), N("Function name: Cross"), N("Function name: Circle"), N("Function name: Triangle"),

        // JoyPro's plain names of the catalog's slots (DB/Functions/slots.json) that the quick review shows instead of a
        // slot id ("GearIn", "Trim_NoseDown", "LaunchWeapon"): DCS, Falcon BMS and the other games set up by slots (ById)
        N("Function name: Trigger: first click"), N("Function name: Cursor (TDC) left/right"), N("Function name: Cursor (TDC) up/down"),
        N("Function name: Cursor (TDC) up"), N("Function name: Cursor (TDC) down"), N("Function name: Cursor (TDC) left"), N("Function name: Cursor (TDC) right"),
        N("Function name: Cursor (TDC) press"), N("Function name: China hat forward"), N("Function name: China hat back"),
        N("Function name: Radio 1: talk"), N("Function name: Radio 2: talk"), N("Function name: Intercom: talk"), N("Function name: IFF check"),
        N("Function name: Flaps middle"), N("Function name: Radar range +"), N("Function name: Radar range -"),
        N("Function name: Flares and chaff program (CMS forward)"), N("Function name: Chaff (CMS back)"), N("Function name: CMS left"), N("Function name: CMS right"),
        N("Function name: Jammer (CMS press)"), N("Function name: Nosewheel steering"), N("Function name: Radar lock"), N("Function name: Radar unlock"),
        N("Function name: Night vision goggles"), N("Function name: Select gun"), N("Function name: Select heat-seeking missile"), N("Function name: Select radar missile"),
        N("Function name: Master arm on"), N("Function name: Master arm off"), N("Function name: Autopilot - Route"), N("Function name: Autopilot - Hover"),
        N("Function name: Uncage missile seeker"), N("Function name: Gun safety on/off"), N("Function name: Gun safe"), N("Function name: Gun armed"),
        N("Function name: Weapon select {0}"), N("Function name: Autopilot disconnect (paddle)"),
        N("Function name: Mode: air to air"), N("Function name: Mode: dogfight"), N("Function name: Mode: air to ground"), N("Function name: Mode: navigation"),
        N("Function name: Mode: landing"), N("Function name: Mode: take-off"), N("Function name: Parking brake off"),
        N("Function name: Gunsight target size +"), N("Function name: Gunsight target size -"), N("Function name: Radar on"), N("Function name: Radar silent"),
        N("Function name: Sensor select forward"), N("Function name: Sensor select back"), N("Function name: Sensor select left"), N("Function name: Sensor select right"),
        N("Function name: Sensor select press"), N("Function name: Dogfight switch: middle"), N("Function name: Dogfight switch: dogfight"),
        N("Function name: Dogfight switch: missile override"), N("Function name: Target switch (TMS) up"), N("Function name: Target switch (TMS) down"),
        N("Function name: Target switch (TMS) left"), N("Function name: Target switch (TMS) right"), N("Function name: Target switch (TMS) press"),
        N("Function name: Anti-skid on"), N("Function name: Anti-skid off"), N("Function name: Trim (helicopter trimmer)"), N("Function name: Tail hook down"),
        N("Function name: Tail hook up"), N("Function name: Tail hook up/down"), N("Function name: Kneeboard: next page"), N("Function name: Kneeboard: previous page"),
        N("Function name: Next waypoint"), N("Function name: Previous waypoint"), N("Function name: Display switch (DMS) up"), N("Function name: Display switch (DMS) down"),
        N("Function name: Display switch (DMS) left"), N("Function name: Display switch (DMS) right"), N("Function name: Radar up"), N("Function name: Radar down")
    ], StringComparer.Ordinal);

    // The catalog's slot (function id) -> its plain name in the list above, for names written as slots ("GearIn",
    // "Trim_NoseDown", "CMS FWD (Start Program or just Flare)"): the most used functions of the popular DCS modules and
    // the other games set up by slots. A slot not listed gets PlainName (words split, no ids, no aircraft list).
    private static readonly Dictionary<string, string> ById = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pitch"] = "Pitch", ["roll"] = "Roll", ["rudder"] = "Rudder", ["zoom"] = "Zoom", ["collective"] = "Collective", // nonloc
        ["view_snap_forward"] = "Snap view: forward", ["view_snap_back"] = "Snap view: back", ["view_snap_left"] = "Snap view: left", ["view_snap_right"] = "Snap view: right", // nonloc
        ["view_center"] = "View Center", ["view_cockpit"] = "Camera: cockpit", ["view_external"] = "External camera: your aircraft", ["view_padlock"] = "Padlock: enemy aircraft", // nonloc
        ["view_look_horizontal"] = "Look left/right", ["view_look_vertical"] = "Look up/down", // nonloc
        ["tdc_horizontal"] = "Cursor (TDC) left/right", ["tdc_vertical"] = "Cursor (TDC) up/down", ["tdc_vertical_inv"] = "Cursor (TDC) up/down", // nonloc
        ["tdc_slew_up"] = "Cursor (TDC) up", ["tdc_slew_down"] = "Cursor (TDC) down", ["tdc_slew_left"] = "Cursor (TDC) left", ["tdc_slew_right"] = "Cursor (TDC) right", // nonloc
        ["tdc_depress"] = "Cursor (TDC) press", ["fov_toggle_china_hat_fwd"] = "China hat forward", ["reset_curser_fov_out_lss_china_aft"] = "China hat back", // nonloc
        ["wheelbrakes_button"] = "Wheel brakes", ["wheelbrakeleftanalog"] = "Wheel brake: left", ["wheelbrakerightanalog"] = "Wheel brake: right", // nonloc
        ["wheelbrakeleftanaloginv"] = "Wheel brake: left", ["wheelbrakerightanaloginv"] = "Wheel brake: right", // nonloc
        ["trimnoseup"] = "Trim nose up", ["trim_nosedown"] = "Trim nose down", ["trimleft"] = "Trim left", ["trimright"] = "Trim right", ["trimreset"] = "Trim reset", // nonloc
        ["trimrudderleft"] = "Trim rudder left", ["trimrudderright"] = "Trim rudder right", ["trimmerset"] = "Trim (helicopter trimmer)", // nonloc
        ["trim_axis_aileron"] = "Aileron trim", ["trim_axis_elevator"] = "Elevator trim", ["trim_axis_rudder"] = "Rudder trim", // nonloc
        ["gear_toggle"] = "Landing gear up/down", ["gearin"] = "Landing gear up", ["gearout"] = "Landing gear down", // nonloc
        ["flapsdown"] = "Flaps down", ["flapsup"] = "Flaps up", ["flaps_toggle"] = "Flaps toggle", ["flapsneutral"] = "Flaps middle", // nonloc
        ["trigger1ststage"] = "Trigger: first click", ["trigger2ndstage"] = "Fire gun", ["launchweapon"] = "Launch weapon", // nonloc
        ["comm1_up"] = "Radio 1: talk", ["comm2_down"] = "Radio 2: talk", ["comm3_aft_ics"] = "Intercom: talk", ["comm4_fwd_iff"] = "IFF check", ["easycomms"] = "Command menu", // nonloc
        ["radar_gunsight_rng_2"] = "Radar range +", ["radar_gunsight_rng"] = "Radar range -", // nonloc
        ["cms_fwd_start_program_or_just_flare"] = "Flares and chaff program (CMS forward)", ["cms_aft_panic_or_just_chaff"] = "Chaff (CMS back)", // nonloc
        ["cms_left"] = "CMS left", ["cms_right"] = "CMS right", ["cms_depress_ecm"] = "Jammer (CMS press)", // nonloc
        ["nosewheel_steering_lasing_2nd_detent"] = "Nosewheel steering", ["radar_lock"] = "Radar lock", ["radar_unlock"] = "Radar unlock", ["nvg"] = "Night vision goggles", // nonloc
        ["airbrakeretract"] = "Airbrake retract", ["airbakeextend"] = "Airbrake extend", ["airbrake_toggle"] = "Airbrake toggle", ["afterburner"] = "Afterburner", // nonloc
        ["select_gun"] = "Select gun", ["select_heater"] = "Select heat-seeking missile", ["select_mrm"] = "Select radar missile", ["select_mrm_2"] = "Select radar missile", // nonloc
        ["master_arm_on"] = "Master arm on", ["master_arm_off"] = "Master arm off", ["master_arm_toggle"] = "Master ARM toggle", // nonloc
        ["autopilot_off"] = "Autopilot - Off", ["autopilot_on"] = "Autopilot - On", ["autopilot_attitude_hold"] = "Autopilot - Attitude hold", // nonloc
        ["autopilot_altitude_hold"] = "Autopilot - Altitude Hold", ["autopilot_route"] = "Autopilot - Route", ["autopilot_auto_hover"] = "Autopilot - Hover", // nonloc
        ["autopilot_roll_on"] = "Autopilot - Roll on", ["autopilot_roll_off"] = "Autopilot - Roll off", ["autopilot_pitch_on"] = "Autopilot - Pitch on", // nonloc
        ["autopilot_pitch_off"] = "Autopilot - Pitch off", ["autopilot_yaw_on"] = "Autopilot - Yaw on", ["autopilot_yaw_off"] = "Autopilot - Yaw off", // nonloc
        ["uncage"] = "Uncage missile seeker", ["gun_safety_lever_toggle"] = "Gun safety on/off", ["gunsafe"] = "Gun safe", ["gunarm"] = "Gun armed", // nonloc
        ["fbw_disconnect"] = "Autopilot disconnect (paddle)", // nonloc
        ["mode_a2a_bvr"] = "Mode: air to air", ["mode_a2a_cas"] = "Mode: dogfight", ["mode_a2g"] = "Mode: air to ground", ["mode_nav"] = "Mode: navigation", // nonloc
        ["mode_ldg"] = "Mode: landing", ["mode_to"] = "Mode: take-off", // nonloc
        ["parkbreakpull"] = "Parking brake on", ["parkbreakstow"] = "Parking brake off", ["parkbreakcw"] = "Parking brake", ["parkbreakccw"] = "Parking brake", // nonloc
        ["wingspan_incr_target_size_increase"] = "Gunsight target size +", ["wingspan_decr_target_size_decrease"] = "Gunsight target size -", // nonloc
        ["radar_on"] = "Radar on", ["radar_silent"] = "Radar silent", ["radar_elevation_up"] = "Radar up", ["radar_elevation_down"] = "Radar down", // nonloc
        ["sensor_select_fwd_ins_mis_uncage"] = "Sensor select forward", ["sensor_select_aft"] = "Sensor select back", ["sensor_select_left"] = "Sensor select left", // nonloc
        ["sensor_select_right"] = "Sensor select right", ["sensor_select_depress_fov_zoom_out"] = "Sensor select press", // nonloc
        ["f_16_df_center_jf_nav_m2k_cmd_centerdepress"] = "Dogfight switch: middle", ["f_16_df_df_m2k_cqb_nav_mode_next"] = "Dogfight switch: dogfight", // nonloc
        ["f_16_jf_df_m_m2k_cqa_mode_prev"] = "Dogfight switch: missile override", // nonloc
        ["target_management_system_up"] = "Target switch (TMS) up", ["target_management_system_down"] = "Target switch (TMS) down", // nonloc
        ["target_management_system_left"] = "Target switch (TMS) left", ["target_management_system_right"] = "Target switch (TMS) right", // nonloc
        ["target_management_system_press"] = "Target switch (TMS) press", // nonloc
        ["a10_dms_up"] = "Display switch (DMS) up", ["a10_dms_down"] = "Display switch (DMS) down", ["a10_dms_left"] = "Display switch (DMS) left", ["a10_dms_right"] = "Display switch (DMS) right", // nonloc
        ["antiskidon"] = "Anti-skid on", ["antiskidoff"] = "Anti-skid off", // nonloc
        ["tailhook_down"] = "Tail hook down", ["tailhook_up"] = "Tail hook up", ["tailhook_toggle"] = "Tail hook up/down", // nonloc
        ["kneeboard_next_pg"] = "Kneeboard: next page", ["kneeboard_previous_pg"] = "Kneeboard: previous page", ["kneeboard_toggle"] = "Kneeboard toggle", // nonloc
        ["wp_increment"] = "Next waypoint", ["wp_decrease"] = "Previous waypoint", // nonloc
        ["rpm_control"] = "Propeller RPM", ["rpm_increase"] = "RPM Increase", ["rpm_decrease"] = "RPM Decrease", ["nozzle_angle_mixture_control"] = "Mixture", // nonloc
        ["radiator_open"] = "Radiator open", ["radiator_close"] = "Radiator close", ["tailwheel_lock"] = "Tailwheel lock", ["reload"] = "Reload", // nonloc
        ["left_engine_start"] = "Left engine start", ["left_engine_stop"] = "Left engine stop", ["right_engine_start"] = "Right engine start", ["right_engine_stop"] = "Right engine stop", // nonloc
        ["jettison_weapons"] = "Jettison Weapons", ["jettison_fuel"] = "Jettison Fuel", ["eject"] = "Eject", ["canopy_open"] = "Canopy open", ["canopy_close"] = "Canopy close" // nonloc
    };

    // Some catalog names come out of PlainName oddly written: the list's way of writing them.
    private static readonly Dictionary<string, string> Spelling = new(StringComparer.Ordinal)
    {
        ["Trim_Nose down"] = "Trim nose down", // nonloc
        ["Cargo Drop / Sling"] = "Cargo drop / sling", // nonloc
        ["Autopilot - Pitch ON"] = "Autopilot - Pitch on", // nonloc
        ["Autopilot - Roll ON"] = "Autopilot - Roll on", // nonloc
        ["Autopilot - Yaw ON"] = "Autopilot - Yaw on", // nonloc
        // IL-2's one throttle lever for all engines (slot ThrottleLeftNonAB): the throttle
        ["Throttle left non AB"] = "Throttle", // nonloc
        ["Throttle right non AB"] = "Throttle" // nonloc
    };

    // The quick review's name of a function the plan places: the game's own name when the list knows it ("Fire",
    // "Landing gear up"), JoyPro's plain name of the catalog slot ("GearIn" -> "Landing gear up", "Trim_NoseDown" ->
    // "Trim nose down"), else the name made readable (PlainName: words split, no "[A-10C, AJS37 +52]" aircraft list).
    // The throttle levers of one engine side ("ThrottleLeft29", "ThrottleRightJ11") are throttle levers.
    public static string Plain(string name, string function)
    {
        string bare = JoyPro.Gui.Models.SetupReviewRow.WithoutAircraft(name);
        if (IsKnown(bare))
            return Translate(bare);
        string id = (function ?? string.Empty).Trim();
        if (ById.TryGetValue(id, out string? english))
            return Translate(english);
        if (System.Text.RegularExpressions.Regex.Match(id, @"^weaponselector(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } select)
            return T("Function name: Weapon select {0}", select.Groups[1].Value);
        string plain = JoyPro.Gui.Models.SetupReviewRow.PlainName(bare);
        if (IsKnown(plain))
            return Translate(plain);
        if (IsThrottleLever(id))
            return Translate(id.Contains("left", StringComparison.OrdinalIgnoreCase) ? "Throttle left" : id.Contains("right", StringComparison.OrdinalIgnoreCase) ? "Throttle right" : "Throttle"); // nonloc
        return plain;
    }

    // A throttle lever (ThrottleLeft, ThrottleRightNonAB, ThrottleF1, ThrottleLeftJ11 ...), not a throttle button.
    public static bool IsThrottleLever(string function)
    {
        string id = (function ?? string.Empty).Trim().ToLowerInvariant();
        return id.StartsWith("throttle", StringComparison.Ordinal) && !id.Contains("button") && !id.Contains("detent") && !id.Contains("up") && !id.Contains("down");
    }

    private static bool IsKnown(string name) =>
        !string.IsNullOrEmpty(name) && Known.Contains(Prefix + (Spelling.TryGetValue(name, out string? spelled) ? spelled : name));

    // The name in the language shown, when the list knows it; else as it is.
    public static string Translate(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name ?? string.Empty;
        string plain = Spelling.TryGetValue(name, out string? spelled) ? spelled : name;
        string key = Prefix + plain;
        if (!Known.Contains(key))
            return name;
        string text = Loc.T(key);
        return text == key ? plain : text;
    }

    // A relation's name in a list (the Binds page): its translation when it is a function name of the list, written as
    // the setup wrote it ("Landing gear up") or as a slot id ("LaunchWeapon"); a name the user gave stays as it is.
    // English shows the name unchanged.
    public static string TranslateRelation(string name)
    {
        if (string.IsNullOrEmpty(name) || Loc.Current == Languages.English)
            return name ?? string.Empty;
        string direct = Translate(name);
        if (!string.Equals(direct, name, StringComparison.Ordinal))
            return direct;
        string plain = JoyPro.Gui.Models.SetupReviewRow.PlainName(name);
        if (!Known.Contains(Prefix + (Spelling.TryGetValue(plain, out string? spelled) ? spelled : plain)))
            return name;
        return Translate(plain);
    }

    // A printed label of a device: the shapes of PlayStation-style face buttons and the parts of an Xbox controller
    // (DB/Devices/microsoft.json: "Right stick left/right", "LB (left bumper)") are words to translate.
    public static string TranslateLabel(string label)
    {
        if (label is "Square" or "Cross" or "Circle" or "Triangle") // nonloc
            return Translate(label);
        if (PadLabels.Contains(PadPrefix + label))
        {
            string text = Loc.T(PadPrefix + label);
            return text == PadPrefix + label ? label : text;
        }
        return label;
    }

    // The printed parts of an Xbox controller (keys "Pad control: ...", so "Menu / Start" never takes another place's word).
    private static readonly HashSet<string> PadLabels = new(
    [
        N("Pad control: Left stick left/right"), N("Pad control: Left stick up/down"), N("Pad control: Right stick left/right"), N("Pad control: Right stick up/down"),
        N("Pad control: Triggers LT/RT (one axis)"), N("Pad control: LB (left bumper)"), N("Pad control: RB (right bumper)"), N("Pad control: Left stick press"),
        N("Pad control: Right stick press"), N("Pad control: View / Back"), N("Pad control: Menu / Start"), N("Pad control: D-pad up"), N("Pad control: D-pad down"),
        N("Pad control: D-pad left"), N("Pad control: D-pad right")
    ], StringComparer.Ordinal);

    private const string PadPrefix = "Pad control: "; // nonloc

    // The keys of the translations are "Function name: Look": a plain "Look" is already the HUD's "Aussehen" in German
    // and "Square" a corner style, so a game function never takes the word of another place of the app.
    private const string Prefix = "Function name: "; // nonloc

    // The English names of the list.
    internal static IReadOnlyCollection<string> All => Known.Select(k => k[Prefix.Length..]).ToList();

    internal static string KeyOf(string name) => Prefix + name;
}
