#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // The aircraft system a clickable control belongs to, from the names the game's files give it: X-Plane's command
    // paths (sim/autopilot/..., laminar/B738/electric/...), MSFS's node and event names (ELECTRICAL_Switch_Battery,
    // AUTOPILOT_Knob_Heading), the tooltip. Games that name no panels (X-Plane, MSFS) group their controls by system and
    // place with it, so a picture is "Autopilot" or "Fuel" and not "Overhead panel (4)".
    internal static class ControlSystems
    {
        // Order matters: the first rule whose words occur wins (the CDU before the radios, the audio panel before the
        // radios, the fire handles before the engine).
        private static readonly KeyValuePair<string, Regex>[] Rules =
        {
            Rule("CDU (flight management)", @"\b(fms2?|cdu|mcdu|fmc|fmgc)\b"),
            Rule("GPS", @"\b(gps|gns430|gns530|gtn)\b"),
            Rule("Autopilot", @"\b(autopilot|ap|fcu|mcp|autothrottle|autothrust|athr|flight director|fdir|fd)\b"),
            Rule("EFIS control", @"\b(efis|nd mode|nd range|baro)\b"),
            Rule("EICAS / ECAM", @"\b(eicas|ecam)\b"),
            Rule("Audio panel", @"\b(audio panel|audio|intercom|ics)\b"),
            Rule("Transponder", @"\b(transponder|xpdr|squawk|tcas)\b"),
            Rule("Radios", @"\b(radios?|com ?[12]|nav ?[12]|adf|vor|vhf|uhf|hf|obs|dme|rtp|rtp [lr]|comm)\b"),
            Rule("Fire protection", @"\b(fire|extinguish\w*|squib)\b"),
            Rule("Anti-ice", @"\b(ice|anti ice|deice|de ice|pitot|probe heat|window heat|wiper)\b"),
            Rule("Air conditioning and pressurisation", @"\b(bleed\w*|pack|packs|air ?cond\w*|pressuri[sz]\w*|cabin press\w*|cabin alt\w*|cabin temp\w*|ecs|outflow)\b"),
            Rule("Fuel", @"\b(fuel|crossfeed|x feed|tank)\b"),
            Rule("Hydraulics", @"\b(hyd\w*)\b"),
            Rule("Electrical", @"\b(electric\w*|elec|battery|batt|generators?|alternators?|bus|inverter|apu gen|ext pwr|ground power|avionics|drive disconnect\w*|idg|ac power|dc power|standby power)\b"),
            Rule("Lights", @"\b(lights?|lighting|beacon|strobe|landing light\w*|taxi light\w*|nav lights?|logo|dome|flood|panel bright\w*|rheos?)\b"),
            Rule("Flight instruments", @"\b(altimeter|airspeed|attitude|adi|hsi|vsi|clock|chrono\w*|timer|elapsed\w*|standby|instruments?)\b"),
            Rule("Engine and start", @"\b(engine\w*|eng|starters?|start|ignition|ign|magnetos?|mixture|primer|prop|propeller|throttle|condition lever|cowl|apu)\b"),
            Rule("Landing gear and brakes", @"\b(gear|brakes?|autobrake|anti skid|antiskid|parking brake|nosewheel|steering)\b"),
            Rule("Flight controls", @"\b(flaps?|slats?|spoilers?|speed ?brake|trim\w*|yaw damper|flight controls?)\b"),
            Rule("Warnings", @"\b(master caution|master warning|annunciator\w*|caution|warning|gpws|stall)\b"),
            Rule("Oxygen", @"\b(oxygen|o2)\b"),
            Rule("Doors and windows", @"\b(door|canopy|window|hatch)\b"),
            Rule("Weather radar", @"\b(wxr|weather radar|radar)\b"),
        };

        private static KeyValuePair<string, Regex> Rule(string title, string words)
        {
            return new KeyValuePair<string, Regex>(title, new Regex(words, RegexOptions.IgnoreCase | RegexOptions.Compiled));
        }

        // Words of a name: path separators, underscores and camel case become spaces ("laminar/B738/autopilot/cmd_a" ->
        // "laminar b738 autopilot cmd a", "ELECTRICAL_Switch_Battery" -> "electrical switch battery").
        public static string Words(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            string t = Regex.Replace(s, "([a-z])([A-Z])", "$1 $2");
            t = Regex.Replace(t, @"[^A-Za-z0-9]+", " ");
            return t.Trim().ToLowerInvariant();
        }

        // The system's title, or empty when the names say nothing clear.
        public static string Of(CockpitCallout c)
        {
            if (c == null)
                return string.Empty;
            // The commands first (X-Plane paths name the system), then the control's own name, then its tooltip.
            List<string> names = new List<string>();
            foreach (CalloutCommand cmd in c.Commands)
            {
                // A vendor prefix and a generic folder say nothing ("laminar/B738/push_button/...": the button's name does).
                string id = Regex.Replace(cmd.Id ?? string.Empty, @"\[\d+\]$", string.Empty);
                string[] parts = id.Split('/');
                names.Add(Words(string.Join(" ", parts.Skip(parts.Length > 2 ? 1 : 0))));
            }
            names.Add(Words(c.Id));
            names.Add(Words(c.Label));
            foreach (string n in names.Where(x => x.Length > 0))
            {
                foreach (KeyValuePair<string, Regex> r in Rules)
                {
                    if (r.Value.IsMatch(n))
                        return r.Key;
                }
            }
            return string.Empty;
        }

        // Panels by system and place: the callout's panel becomes "<area>|<system>" with the system's title, where the
        // names say which system; else it keeps the place (the area and its title).
        // A system with fewer than MinControls controls at a place stays with the place (no page for one trim wheel).
        public const int MinControls = 3;

        public static void Assign(IEnumerable<CockpitCallout> callouts, Vec3 head, Func<string, string> areaTitle)
        {
            List<Tuple<CockpitCallout, string, string>> all = callouts.Select(c => Tuple.Create(c, CockpitViews.AreaOf(c.Position, head), Of(c))).ToList();
            Dictionary<string, int> count = all.Where(t => t.Item3.Length > 0).GroupBy(t => t.Item3).ToDictionary(g => g.Key, g => g.Count());
            foreach (Tuple<CockpitCallout, string, string> t in all)
            {
                CockpitCallout c = t.Item1;
                string area = t.Item2, system = t.Item3;
                int n;
                if (system.Length > 0 && (!count.TryGetValue(system, out n) || n < MinControls))
                    system = string.Empty;
                // A system's controls are one panel wherever they are (a CDU across the pedestal's edge); the planner splits
                // them by place (the captain's and the first officer's CDU, the overhead's and the panel's lights).
                c.Panel = system.Length > 0 ? "system:" + CockpitViews.Key(system) : area;
                c.PanelTitle = system.Length > 0 ? system : areaTitle(area);
            }
        }
    }
}
