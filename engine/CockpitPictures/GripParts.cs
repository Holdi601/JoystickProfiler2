#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JoyPro.CockpitPictures
{
    // The named parts of a game's grips, checked against the aircraft's manual (docs/cockpits.md "Grip parts"): a model
    // whose part names do not say what the part is (a modelling tool's name, a numbered switch), or say it wrong (the DCS
    // A-10C's stick names its countermeasures switch "TMS"), gets the manual's name here; a part that is no control of
    // the grip (a panel switch under the throttle node) is left out. No callout is better than a wrong one.
    internal static class GripParts
    {
        internal sealed class Fix
        {
            public string Game;
            public string[] Models;
            public string Node;
            // The part's id and name as the manual names it; null: the part is left out.
            public string Id;
            public string Label;
            public string Source;
        }

        public static readonly List<Fix> Fixes = new List<Fix>
        {
            // DCS A-10C Flight Manual, Figure 69 "Control Stick": the ribbed hat in the middle of the thumb side is the
            // TMS (3), the mushroom below it the CMS (5). The model names the CMS "STICK-BTN-TMS" and the TMS
            // "STICK-BTN-TARG".
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-TMS", Id = "STICK-BTN-CMS", Label = "CMS (Countermeasures Switch)", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-TARG", Id = "STICK-BTN-TMS", Label = "TMS (Target Management Switch)", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-MMC", Id = "STICK-BTN-MMCB", Label = "MMCB (Master Mode Control Button)", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-NWS", Id = "STICK-BTN-NWS", Label = "NWS (Nosewheel Steering) button", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-WEAPON", Id = "STICK-BTN-WEAPON", Label = "Weapon release button", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-TRIM", Id = "STICK-BTN-TRIM", Label = "Trim switch", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-DMS", Id = "STICK-BTN-DMS", Label = "DMS (Data Management Switch)", Source = "DCS A-10C Flight Manual, Figure 69" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "STICK-BTN-TRIGGER", Id = "STICK-BTN-TRIGGER", Label = "Trigger", Source = "DCS A-10C Flight Manual, Figure 69" },
            // The paddle switch (emergency disconnect) has no part of its own in the model (the grip's mesh): no callout;
            // the page lists it under "Not shown on the drawing".
            // Figure 70 "Throttles": the left throttle button (6) is the red button on the left throttle.
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-BTN-RED-L", Id = "TRTL-BTN-LEFT-THROTTLE", Label = "Left throttle button", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-TMB-BRAKES", Id = "TRTL-R-SPEEDBRAKE", Label = "Speed brake", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-TMB-BOAT", Id = "TRTL-R-BOAT", Label = "Boat switch", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-TMB-CHINA", Id = "TRTL-R-CHINA-HAT", Label = "China hat", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-HAT-MIC", Id = "TRTL-R-MIC", Label = "Mic switch", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-HAT-SLEW", Id = "TRTL-R-SLEW", Label = "Slew control", Source = "DCS A-10C Flight Manual, Figure 70" },
            new Fix { Game = "DCS", Models = new[] { "A-10C", "A-10C_2" }, Node = "TRTL-R-HAT-COOLIE", Id = "TRTL-R-COOLIE", Label = "Coolie hat", Source = "DCS A-10C Flight Manual, Figure 70" },
        };

        // Parts that are no control of the grip: a panel's switch under the throttle node (DCS: TUMB-PANTRTL-..., the F-5E's
        // LVR-PANUHF-VOL: the UHF panel's volume), the
        // modelling tool's own names (Helix004, NGon058, Details036, RUS08: they say nothing of the part).
        private static readonly Regex NotAPart = new Regex("^(TUMB|PNT|PTR)[-_]|PAN[A-Z]*TRTL|^[A-Z]+[-_]PAN[A-Z]+[-_]|^(helix|ngon|details?|rus|obj|object|box|cylinder|chamfer|line|spline|polygon|poly|mesh|shape|group|geo|sphere|torus|tube|cone|plane|rectangle|circle|text|new ?stick)[-_ .]*\\d*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The grip's parts after the fixes (in place): renamed, left out, and the parts that say nothing removed.
        public static void Apply(string game, string model, CockpitGrip grip)
        {
            List<CockpitCallout> keep = new List<CockpitCallout>();
            foreach (CockpitCallout p in grip.Parts)
            {
                Fix fix = Fixes.FirstOrDefault(f => string.Equals(f.Game, game, StringComparison.OrdinalIgnoreCase) && f.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)) &&
                                                    string.Equals(f.Node, p.Id, StringComparison.OrdinalIgnoreCase));
                if (fix != null)
                {
                    if (fix.Id == null)
                        continue;
                    if (p.Animation == null)
                        p.Animation = new ControlAnimation { Source = "model node", Name = p.Id };
                    p.Id = fix.Id;
                    p.Label = fix.Label;
                    keep.Add(p);
                    continue;
                }
                if (NotAPart.IsMatch(p.Id ?? string.Empty))
                    continue;
                keep.Add(p);
            }
            grip.Parts = keep;
        }
    }
}
