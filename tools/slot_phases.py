"""Adds the flight-phase and time-criticality columns to DB/Functions/slot_controls.tsv (docs/auto-assign.md#flight-phases).

    python tools/slot_phases.py [--check]

Column 9 `phase`: the flight phases a function belongs to, '|'-separated:
    pilot    flying the aircraft (axes, trim, airbrake, afterburner)
    tol      take-off and landing (gear, flaps, wheel brakes, hook, launch bar)
    nav      navigation (autopilot, waypoints, UFC, NS430, quantum travel)
    a2a      air to air (weapon select, radar lock, dogfight switch; space combat)
    a2g      air to ground (DMS, laser, targeting pod; space mining / salvage)
    systems  aircraft systems (engines, lights, canopy, seats)
    comms    radios and the AI menu
    view     views, kneeboard, NVG
Column 10 `crit`: how fast the pilot must reach it: instant (a split second: trigger, CMS, TMS, flight axes),
seconds (gear, flaps, trim, sensor select), minutes (lights, autopilot modes, radar modes), idle (start-up, rare).

The rules are ordered; the first that matches a slot id wins, else the slot's category in function-map.json decides.
It also adds the printed labels of keypad keys (LABELS) and keeps the MFD rows in OSB order: every MFD key prefers its own MFD and else the same key of any MFD.
Re-run it after the catalog generator rewrote slot_controls.tsv; --check only reports rows that would change.
"""
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "EngineOnly_Runtime", "DB", "Functions")
TSV = os.path.join(ROOT, "slot_controls.tsv")

RULES = [
    # flight controls
    (r"^(pitch|roll|rudder|space_yaw)$", "pilot", "instant"),
    (r"^throttle", "pilot", "instant"),
    (r"^rpm_control$", "pilot", "instant"),
    (r"^wheelbrake", "tol", "instant"),
    (r"^(trim|trimmerset)", "pilot", "seconds"),
    (r"^(airbrake|airbake)", "pilot|tol", "seconds"),
    (r"^afterburner$", "pilot", "seconds"),
    (r"^reverse_thrust$", "tol", "seconds"),
    (r"^nozzle", "pilot|tol", "seconds"),
    (r"^rpm_(increase|decrease)$", "pilot", "seconds"),
    (r"^f14_wing_sweep", "pilot", "seconds"),
    (r"^f14_dlc", "tol", "seconds"),
    (r"^(fbw_disconnect|autopilot_off)$", "pilot", "instant"),
    (r"^(autopilot_|atc_engage)", "nav", "minutes"),
    # take-off and landing
    (r"^(gear|flaps)", "tol", "seconds"),
    (r"^nosewheel_steering", "tol|a2g", "seconds"),
    (r"^(tailhook|dragchute)", "tol", "seconds"),
    (r"^(antiskid|parkbreak|tailwheel_lock|launchbar)", "tol", "minutes"),
    (r"^mode_(to|ldg)$", "tol", "seconds"),
    (r"^mode_nav$", "nav", "seconds"),
    (r"^mode_a2a", "a2a", "seconds"),
    (r"^mode_a2g$", "a2g", "seconds"),
    (r"^mode_hmd$", "a2a|a2g", "minutes"),
    # weapons, countermeasures, sensors
    (r"^cms_(left|right|depress)", "a2a|a2g", "seconds"),
    (r"^cms_", "a2a|a2g", "instant"),
    (r"^(trigger|launchweapon)", "a2a|a2g", "instant"),
    (r"^gun_safety", "a2a|a2g", "seconds"),
    (r"^laser_", "a2g", "instant"),
    (r"^target_management_system", "a2a|a2g", "instant"),
    (r"^a10_dms", "a2g", "seconds"),
    (r"^sensor_select", "a2a|a2g", "seconds"),
    (r"^select_off$", "a2a", "seconds"),
    (r"^select_", "a2a", "instant"),
    (r"^f_16_(df|jf)", "a2a", "instant"),
    (r"^flir_", "a2g", "seconds"),
    (r"^(fov_toggle|reset_curser)", "a2g|view", "seconds"),
    (r"^uncage$", "a2a", "instant"),
    (r"^radar_(lock|unlock)$", "a2a", "instant"),
    (r"^radar_(elevation_(up|down)|azimuth|cone|bars|gunsight)", "a2a", "seconds"),
    (r"^tws_man$", "a2a", "seconds"),
    (r"^(radar_|mig_21_radar)", "a2a", "minutes"),
    (r"^(master_arm|gunarm|gunsafe)", "a2a|a2g", "minutes"),
    (r"^weaponselector", "a2a|a2g", "seconds"),
    (r"^jettis", "a2a|a2g", "seconds"),
    (r"^wingspan_", "a2a", "seconds"),
    (r"^(tdc_|hcu_|ah64_track_controller)", "a2a|a2g", "instant"),
    (r"^f14_lantirn_(pitch|yaw)$", "a2g", "instant"),
    (r"^f14_lantirn", "a2g", "seconds"),
    (r"^(ah64_cpg|ajs_|mi24gointoaiming)", "a2g", "seconds"),
    (r"^tgpcameraon$", "a2g", "minutes"),
    (r"^clearradiation$", "a2a", "seconds"),
    (r"^hmd_axis$", "a2a|a2g", "minutes"),
    # navigation, systems, comms, views
    (r"^wp_", "nav", "minutes"),
    (r"^(canopy|wingfold|refuel_probe)", "systems", "minutes"),
    (r"^(left_engine|right_engine|apu_start)", "systems", "idle"),
    (r"^(radiator|jettison_fuel)", "systems", "minutes"),
    (r"^lights_ext_landing", "tol", "minutes"),
    (r"(^lights_|lightsaxis|lightaxis$)", "systems", "minutes"),
    (r"^nvg", "view", "minutes"),
    (r"^kneeboard", "view", "minutes"),
    (r"^zoom$", "view", "seconds"),
    (r"^hideaimingarrow$", "view", "minutes"),
    (r"^(comm\d|easycomms|ai_|toggle_crew_ai|salute)", "comms", "seconds"),
    (r"^set\w*seat$", "systems", "minutes"),
    (r"^(toggle_units|dummyunbindrelation)$", "systems", "idle"),
    # spaceships (Star Citizen, Elite, X4): combat = a2a, mining / salvage / scanning = a2g
    (r"^space_strafe", "pilot", "instant"),
    (r"^space_boost$", "pilot|a2a", "instant"),
    (r"^(space_speed_limiter|space_decouple)$", "pilot", "seconds"),
    (r"^space_cruise_control$", "nav", "minutes"),
    (r"^space_quantum_engage$", "nav", "seconds"),
    (r"^space_quantum", "nav", "minutes"),
    (r"^space_(fire_mining|mining|salvage|scanning)", "a2g", "seconds"),
    (r"^space_weapons_o", "a2a", "minutes"),
    (r"^space_missile_(cycle|mode)$", "a2a", "seconds"),
    (r"^space_missile", "a2a", "seconds"),
    (r"^space_lock_under_reticle$", "a2a", "instant"),
    (r"^space_(target|pin|unpin|activate_radar|cycle_gimbal|lead_lag)", "a2a", "seconds"),
    (r"^space_(power_triangle|shield_|shields_reset)", "a2a", "seconds"),
    (r"^space_shields_o", "systems", "minutes"),
    (r"^space_(flight_ready|power_|thrusters|lock_doors|unlock_doors|light)", "systems", "minutes"),
    (r"^space_(docking|invoke_docking|vtol|g_safety|gear)", "tol", "seconds"),
    (r"^space_(cycle_camera|look_ahead)", "view", "minutes"),
    # display keys
    (r"^ufc_aa$", "a2a", "seconds"),
    (r"^ufc_ag$", "a2g", "seconds"),
    (r"^(ufc|ns430)", "nav", "minutes"),
    (r"^mfd_", "nav|a2a|a2g", "minutes"),
]

BY_CATEGORY = {
    "flight": ("pilot", "seconds"),
    "engine": ("pilot", "seconds"),
    "weapons": ("a2a|a2g", "seconds"),
    "countermeasures": ("a2a|a2g", "instant"),
    "sensors": ("a2a|a2g", "seconds"),
    "hotas": ("a2a|a2g", "seconds"),
    "nav": ("nav", "minutes"),
    "lights": ("systems", "minutes"),
    "systems": ("systems", "minutes"),
    "comms": ("comms", "seconds"),
    "view": ("view", "minutes"),
    "sim": ("view", "minutes"),
    "space": ("pilot", "seconds"),
}

# Printed labels of keypads (UFC, ICP, CDU): "^1" = the label starts with the word 1 ("1", "1 T-ILS", "0 -"),
# "=A" = the label is exactly A. Merged with the labels the catalog generator wrote.
LABELS = {
    "ufc_clear": "CLR;CLEAR", "ufc_enter": "ENT;ENTR;ENTER", "ufc_iff": "IFF", "ufc_tcn": "TCN", "ufc_ils": "ILS",
    "ufc_datalink": "D/L;DATALINK", "ufc_onoff": "ON/OFF", "ufc_aa": "A/A;A-A", "ufc_ag": "A/G;A-G",
    "ufc_comm1": "COM1;COMM1;COMM 1", "ufc_comm2": "COM2;COMM2;COMM 2", "ufc_mastercaution": "MASTER CAUTION",
    "ufc_func": "FUNC", "ufc_edit": "EDIT", "ufc_dot": "=.;DOT", "ufc_space": "SPACE;SPC", "ufc_divide": "=/",
    "ufc_multiply": "=*", "ufc_modeup": "DCS UP", "ufc_modedown": "DCS DN;DCS DOWN", "ufc_modeleft": "DCS RTN",
    "ufc_moderight": "DCS SEQ",
}
for digit in "0123456789":
    LABELS["ufc_" + digit] = "^" + digit
LABELS["ufc_2_2"] = "^2"
for letter in "abcdefghijklmnopqrstuvwxyz":
    LABELS["ufc_" + letter] = "=" + letter.upper()
for number in range(1, 7):
    LABELS["ufc_dp%d" % number] = "OPTION SELECT %d" % number

# Label keywords that replace the generator's (too generic words take printed labels from other functions: LDG is
# landing gear more often than the landing light, START and ARM are on many panels, a bare MODE or NAV names modes).
LABELS_REPLACE = {
    "lights_ext_landing_taxi_off": "LDG/TAXI;TAXI;LANDING LIGHT;LDG LIGHT;LDG/TAXI LIGHT",
    "lights_ext_landing_taxi_off1": "LDG/TAXI;TAXI;LANDING LIGHT;LDG LIGHT;LDG/TAXI LIGHT",
    "lights_ext_landing_taxi_on": "LDG/TAXI;TAXI;LANDING LIGHT;LDG LIGHT;LDG/TAXI LIGHT",
    "lights_ext_landing_taxi_on1": "LDG/TAXI;TAXI;LANDING LIGHT;LDG LIGHT;LDG/TAXI LIGHT",
    "lights_ext_navigation_off": "NAV LT;NAV LIGHT;NAV LIGHTS;POSITION;POS LT;POSITION LIGHTS",
    "lights_ext_navigation_off1": "NAV LT;NAV LIGHT;NAV LIGHTS;POSITION;POS LT;POSITION LIGHTS",
    "lights_ext_navigation_on": "NAV LT;NAV LIGHT;NAV LIGHTS;POSITION;POS LT;POSITION LIGHTS",
    "lights_ext_navigation_on1": "NAV LT;NAV LIGHT;NAV LIGHTS;POSITION;POS LT;POSITION LIGHTS",
    "lights_ext_collision_off": "ANTI COLL;STROBE;BEACON",
    "lights_ext_collision_on": "ANTI COLL;STROBE;BEACON",
    "lights_ext_formation": "FORM;FORMATION",
    "lights_ext_formation_2": "FORM;FORMATION",
    "lights_ext_aar": "AAR LT;AAR LIGHT",
    "lights_ext_aar_2": "AAR LT;AAR LIGHT",
    "left_engine_start": "ENG L;LEFT ENG;L ENG;ENG CRANK L;L ENG START",
    "left_engine_stop": "ENG L;LEFT ENG;L ENG;ENG CRANK L",
    "right_engine_start": "ENG R;RIGHT ENG;R ENG;ENG CRANK R;R ENG START",
    "right_engine_stop": "ENG R;RIGHT ENG;R ENG;ENG CRANK R",
    "master_arm_on": "MASTER ARM", "master_arm_off": "MASTER ARM", "master_arm_toggle": "MASTER ARM",
    "gunarm": "GUN;GUN/PAC;GUN ARM", "gunsafe": "GUN;GUN/PAC;GUN SAFE",
    "mode_a2a_bvr": "MASTER MODE A/A;A/A MODE", "mode_a2a_cas": "MASTER MODE A/A", "mode_a2g": "MASTER MODE A/G;A/G MODE",
    "mode_hmd": "HMD", "mode_ldg": "MASTER MODE LDG;LDG MODE", "mode_nav": "MASTER MODE NAV;NAV MODE", "mode_to": "MASTER MODE TO;TO MODE",
    "nvg_mode_1": "NVG", "nvg_mode_2": "NVG",
    "f14_lantirn_mode_toggle": "LANTIRN", "f_16_df_df_m2k_cqb_nav_mode_next": "DOG FIGHT;DOGFIGHT", "f_16_jf_df_m_m2k_cqa_mode_prev": "MSL OVRD",
    "radar_next_mode_f1": "RADAR MODE", "radar_prev_mode_f1": "RADAR MODE",
    "jettison_weapons": "JETT;JETTISON;EMER JETT", "jettison_cover_weapons": "JETT COVER", "jettison_fuel": "FUEL DUMP;DUMP;JETT FUEL",
    "jettisonsafe": "JETT SAFE;SELECT JETT SAFE", "jettisonlfus": "L FUS", "jettisonrfus": "R FUS", "jettisonrack": "RACK",
    "jettisonstores": "JETT STORES;STORES", "jettiston_selective": "SEL JETT;SELECTIVE JETT",
    "wingfoldcenter": "WING FOLD", "wingfoldfold": "WING FOLD", "wingfoldspread": "WING FOLD", "wingfoldstow": "WING FOLD",
}

MFD = re.compile(r"^mfd_(left|right|center_right|center)_([tblr])(\d+)$")


def categories():
    path = os.path.join(ROOT, "function-map.json")
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    result = {}
    for function in data.get("functions", []):
        for slot in (function.get("slots") or {}):
            result.setdefault(slot, function.get("category", ""))
    return result


def tag(slot, category):
    for pattern, phase, crit in RULES:
        if re.search(pattern, slot):
            return phase, crit
    return BY_CATEGORY.get(category, ("systems", "minutes"))


def main():
    check = "--check" in sys.argv
    cats = categories()
    with open(TSV, encoding="utf-8", newline="") as handle:
        raw = handle.read()
    newline = "\r\n" if "\r\n" in raw else "\n"
    lines = raw.splitlines()
    out = []
    changed = 0
    for line in lines:
        if line.startswith("#"):
            if line.startswith("# slot\t"):
                line = "# slot\tside\tprefs\tpair\tlabels\tfrequency\tkind\tgroup\tphase\tcrit"
            elif line.startswith("# group ="):
                out.append(line)
                if not any(l.startswith("# phase =") for l in lines):
                    out.append("# phase = flight phases (pilot, tol, nav, a2a, a2g, systems, comms, view), crit = instant, seconds, minutes, idle (tools/slot_phases.py)")
                continue
            out.append(line)
            continue
        if not line.strip():
            out.append(line)
            continue
        parts = line.split("\t")
        while len(parts) < 10:
            parts.append("")
        slot = parts[0]
        m = MFD.match(slot)
        if m:
            side, edge, number = m.group(1), m.group(2).upper(), m.group(3)
            parts[2] = "mfd.%s.osb.%s%s,mfd.*.osb.%s%s" % (side, edge, number, edge, number)
        if slot in LABELS_REPLACE:
            parts[4] = LABELS_REPLACE[slot]
        if slot in LABELS:
            words = [w for w in parts[4].split(";") if w]
            for word in LABELS[slot].split(";"):
                if word not in words:
                    words.append(word)
            parts[4] = ";".join(words)
        phase, crit = tag(slot, cats.get(slot, ""))
        parts[8], parts[9] = phase, crit
        new = "\t".join(parts[:10])
        if new != line:
            changed += 1
        out.append(new)
    if check:
        print("%d rows would change" % changed)
        return
    with open(TSV, "w", encoding="utf-8", newline="") as handle:
        handle.write(newline.join(out) + newline)
    print("%d rows changed" % changed)


if __name__ == "__main__":
    main()
