using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Engine.VJoy
{
    // How many buttons, axes and hats of ONE joystick a game reads (docs/virtual-joystick.md "What a game reads").
    // JoyPro sets vJoy up for up to 128 buttons, 8 axes and 4 hats per device; a game that reads less (DirectInput's
    // DIJOYSTATE: 32 buttons; DIJOYSTATE2: 128) misses what lies above. The planner and the virtual functions warn when
    // a slot or an output of a virtual-mode game lies above what the game reads. -1 = not known (no warning).
    //
    // Sources: what JoyPro's own game code enforces when it writes the game's files (verified in code) and what the
    // game docs (docs/games/*.md) record from the game files; nothing here is a guess. Games not listed: not known.
    internal sealed class GameInputLimit
    {
        public string GameId { get; set; }
        public int Buttons { get; set; }
        public int Axes { get; set; }
        public int Hats { get; set; }
        // Hats with four directions only (no diagonals).
        public bool FourWayHats { get; set; }
        public string Interface { get; set; }
        // verified | documented | reported (GameInputLimits); empty = not known.
        public string Confidence { get; set; }
        public string Note { get; set; }
        public string Source { get; set; }

        public GameInputLimit()
        {
            Buttons = Axes = Hats = -1;
            GameId = Interface = Note = Source = Confidence = string.Empty;
        }

        public bool Known
        {
            get { return !string.IsNullOrEmpty(Confidence); }
        }

        // A game that reads no joystick at all (XInput only): the virtual Xbox pad, not vJoy.
        public bool XInputOnly
        {
            get { return Known && Buttons == 0 && Axes == 0 && Hats == 0; }
        }

        // "reads 32 buttons, 8 axes, 1 hat per joystick"
        public string Describe()
        {
            if (!Known)
                return "what it reads of a joystick is not known";
            if (XInputOnly)
                return "reads no joystick, only Xbox pads";
            List<string> parts = new List<string>();
            if (Buttons >= 0)
                parts.Add(Buttons.ToString(CultureInfo.InvariantCulture) + " buttons");
            if (Axes >= 0)
                parts.Add(Axes.ToString(CultureInfo.InvariantCulture) + " axes");
            if (Hats >= 0)
                parts.Add(Hats == 0 ? "no hats" : Hats.ToString(CultureInfo.InvariantCulture) + (Hats == 1 ? " hat" : " hats") + (FourWayHats ? " (4 directions)" : string.Empty));
            return "reads " + string.Join(", ", parts) + " per joystick (" + Confidence + ")";
        }
    }

    internal static class GameInputLimits
    {
        private static readonly Dictionary<string, GameInputLimit> Table = Build();

        // Confidence: verified (JoyPro's game code enforces it / read from the game's files or binary), documented (the
        // game's own docs or help), reported (players, forums). Every known limit warns; "unknown" never does.
        public const string Verified = "verified";
        public const string Documented = "documented";
        public const string Reported = "reported";

        private static void Add(Dictionary<string, GameInputLimit> table, string ids, int buttons, int axes, int hats, bool fourWay, string iface, string confidence, string note, string source)
        {
            foreach (string id in ids.Split(','))
                table[id.Trim()] = new GameInputLimit { GameId = id.Trim(), Buttons = buttons, Axes = axes, Hats = hats, FourWayHats = fourWay, Interface = iface, Confidence = confidence, Note = note, Source = source };
        }

        private static Dictionary<string, GameInputLimit> Build()
        {
            Dictionary<string, GameInputLimit> table = new Dictionary<string, GameInputLimit>(StringComparer.OrdinalIgnoreCase);
            Add(table, "DCS", 128, 8, 4, false, "DirectInput (DIJOYSTATE2)", Documented,
                "DCS reads 128 buttons and 8 axes per joystick.", "research 2026-10-12 (ED forum / DCS input docs); tokens JOY_BTN1..128 in engine/Games/GameInputTokens.cs");
            Add(table, "IL2Game", 128, 8, 4, false, "DirectInput", Documented,
                "IL-2 Great Battles reads 128 buttons since 5.204.", "research 2026-10-12 (IL-2 GB update 5.204 notes)");
            Add(table, "IL2CoD,IL2CoDBlitz", 128, 8, 1, false, "DirectInput", Documented,
                "Cliffs of Dover reads 128 buttons since 5.025 and names one hat per joystick.", "engine/Games/Il2CodIntegration.cs, docs/games/il2-cliffs-of-dover.md; research 2026-10-12");
            Add(table, "IL21946", 32, 8, 4, false, "DirectInput", Reported,
                "IL-2 1946 reads 32 buttons per joystick and up to 4 hats shared by all devices (JoyPro's writer would allow 128; players report 32).", "research 2026-10-12 (community reports); engine/Games/Il21946Integration.cs");
            Add(table, "FalconBMS", 128, 8, 2, false, "DirectInput", Documented,
                "Falcon BMS reads 128 buttons when g_nButtonsPerDevice is 128 (the 4.37 launcher sets it; older setups: 32) and only two 8-way hats in all.", "engine/Games/FalconBmsIntegration.cs, docs/games/falcon-bms.md; research 2026-10-12");
            Add(table, "FSX,FSXSE,P3D4,P3D5,P3D6", 32, 8, -1, false, "DirectInput", Reported,
                "FSX and Prepar3D read 32 buttons per joystick.", "research 2026-10-12 (community reports)");
            Add(table, "MSFS2020,MSFS2024", -1, 8, 1, true, "DirectInput", Documented,
                "MSFS reads more than 32 buttons, but add-ons through SimConnect see only buttons 1-32; JoyPro writes one hat (four directions) per joystick.", "engine/Games/MsfsIntegration.cs, docs/games/msfs.md; research 2026-10-12");
            Add(table, "XPlane11,XPlane12", 160, 25, 1, false, "DirectInput", Verified,
                "X-Plane counts one hat per joystick as a hat; further hats become buttons.", "engine/Games/XPlaneIntegration.cs, docs/games/xplane.md; research 2026-10-12");
            Add(table, "AceCombat7", 32, 8, 4, true, "DirectInput + XInput", Documented,
                "ACE COMBAT 7 reads only the first 32 buttons of a joystick (its Input.ini help); hats in four directions.", "engine/Games/AceCombat7Integration.cs, docs/games/ace-combat.md; the game's Input.ini");
            Add(table, "Arma2,Arma2OA", 32, 8, 1, false, "DirectInput (DIJOYSTATE)", Verified,
                "Arma 2 reads only the first 32 buttons and one hat of a joystick.", "engine/Games/ArmaProfileIntegration.cs, docs/games/arma2.md (reverse engineering)");
            Add(table, "X4", 64, 8, 1, false, "DirectInput", Verified,
                "X4 reads 64 buttons and one hat per joystick.", "engine/Games/X4Integration.cs, docs/games/x4.md (from the game binary)");
            Add(table, "GRebels", 96, 24, 0, false, "Raw Input (UE EnhancedRawInput)", Documented,
                "G-Rebels (Unreal's Raw Input plugin) reads 96 buttons and 24 axes and no hats.", "docs/games/g-rebels.md; research 2026-10-12 (UE 5.1+ RawInput plugin)");
            Add(table, "VTOLVR", 128, 8, 4, true, "DirectInput (DIJOYSTATE2, BYOJoystick)", Verified,
                "", "engine/Games/VtolVrIntegration.cs, docs/games/vtol-vr.md; research 2026-10-12");
            Add(table, "AceCombat8,HLLVietnam", 0, 0, 0, false, "XInput only (fixed Xbox pad layout)", Documented,
                "Reads no joystick, only Xbox pads: use the virtual Xbox pad.", "engine/Games/AceCombat8Integration.cs, engine/Games/HllVietnamIntegration.cs");
            // Not known (no warning): Star Citizen (sources conflict), War Thunder, Arma 3, Arma Reforger, Battlefield,
            // Elite Dangerous (players report 32, but Joy_41 worked on the development PC), IL-2 Korea, WARDOGS,
            // Rewired games (Nuclear Option, Tiny Combat Arena).
            return table;
        }

        public static GameInputLimit For(string gameId)
        {
            GameInputLimit limit;
            return Table.TryGetValue((gameId ?? string.Empty).Trim(), out limit) ? limit : new GameInputLimit { GameId = gameId ?? string.Empty };
        }

        public static IEnumerable<GameInputLimit> All
        {
            get { return Table.Values.OrderBy(l => l.GameId, StringComparer.OrdinalIgnoreCase); }
        }

        // The vJoy targets ("JOY_BTN40", "POV2", "JOY_SLIDER2") that lie above what the game reads, as one warning per
        // kind: "Arma 2 reads only the first 32 buttons of a joystick: buttons 33, 40 on vJoy 1 will not work there."
        public static List<string> Check(string gameId, string gameName, IEnumerable<KeyValuePair<int, string>> targets, string what)
        {
            List<string> warnings = new List<string>();
            GameInputLimit limit = For(gameId);
            if (!limit.Known || limit.XInputOnly)
                return warnings;
            List<KeyValuePair<int, int>> buttons = new List<KeyValuePair<int, int>>();
            List<KeyValuePair<int, int>> hats = new List<KeyValuePair<int, int>>();
            List<KeyValuePair<int, int>> axes = new List<KeyValuePair<int, int>>();
            foreach (KeyValuePair<int, string> target in targets)
            {
                VJoyInput input;
                if (!VJoyInput.TryParseSlotToken(target.Value, out input))
                    continue;
                if (input.Kind == VJoyInputKind.Button && limit.Buttons >= 0 && input.Index >= limit.Buttons)
                    buttons.Add(new KeyValuePair<int, int>(target.Key, input.Index + 1));
                else if (input.Kind == VJoyInputKind.Pov && limit.Hats >= 0 && input.Index >= limit.Hats)
                    hats.Add(new KeyValuePair<int, int>(target.Key, input.Index + 1));
                else if (input.Kind == VJoyInputKind.Axis && limit.Axes >= 0 && input.Index >= limit.Axes)
                    axes.Add(new KeyValuePair<int, int>(target.Key, input.Index + 1));
            }
            if (buttons.Count > 0)
                warnings.Add(gameName + " reads only the first " + limit.Buttons.ToString(CultureInfo.InvariantCulture) + " buttons of a joystick: " + what + " " + List(buttons, "button") + " will not work there.");
            if (hats.Count > 0)
                warnings.Add(gameName + (limit.Hats == 0 ? " reads no hats" : " reads only " + (limit.Hats == 1 ? "one hat" : limit.Hats.ToString(CultureInfo.InvariantCulture) + " hats")) + " of a joystick: " + what + " " + List(hats, "hat") + " will not work there.");
            if (axes.Count > 0)
                warnings.Add(gameName + " reads only " + limit.Axes.ToString(CultureInfo.InvariantCulture) + " axes of a joystick: " + what + " " + List(axes, "axis") + " will not work there.");
            return warnings;
        }

        private static string List(List<KeyValuePair<int, int>> items, string word)
        {
            return string.Join("; ", items.GroupBy(i => i.Key).OrderBy(g => g.Key).Select(g =>
                (g.Count() == 1 ? word : word + "s") + " " + string.Join(", ", g.Select(i => i.Value).Distinct().OrderBy(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture))) +
                " on vJoy " + g.Key.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
