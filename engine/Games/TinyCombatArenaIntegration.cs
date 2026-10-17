using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JoyPro.Engine.Games
{
    // Tiny Combat Arena (Why485 / MicroProse, Unity + Rewired). Like Nuclear Option the binds are Rewired JoystickMap XML
    // values in Unity PlayerPrefs, here HKCU\Software\TinyCombat\Arena (seen after the first start on 2026-10-11, game
    // 0.17.2.3; Rewired's UserDataStore_File / RewiredSaveData.json is in the game but not used). Differences to Nuclear
    // Option: the player is "Player"; the joystick maps are split over the Control Mapper's map categories (one value
    // per device and map category: 0 System, 3 Flight, 8 Weapons, 11 Camera, 4 View Select, 9 Map UI); the mapper writes
    // axisRange 1 on button elements; hat directions are elements 160 + 4h + (Up, Right, Down, Left) (Hat 0 Up = 160
    // seen). Unknown joysticks get no default binds (every saved map starts empty). The game's own control options
    // (stick and rudder curve, throttle speed) are in Settings\Controls.cfg / Steam Cloud and are not written.
    // DB\TinyCombatArena\actions.tsv comes from the game's Rewired InputManager (refresh-db, sharedassets*.assets).
    // Docs: docs/games/tiny-combat-arena.md.
    internal sealed class TinyCombatArenaIntegration : RewiredMapIntegration
    {
        public const string RegistryKey = @"HKCU\Software\TinyCombat\Arena";
        private const string SubKey = @"Software\TinyCombat\Arena";

        // Action categories (InputManager): id -> name.
        private static readonly Dictionary<int, string> Categories = new Dictionary<int, string>
        {
            { 0, "Default" }, { 1, "Flight Control" }, { 7, "Weapon Control" }, { 2, "View Control" }, { 10, "Camera Control" },
            { 8, "Map Control" }, { 9, "UI" }, { 11, "System" }
        };

        // Action category -> the Control Mapper's map category (its tabs System, Flight, Weapons, Camera, View Select,
        // Map UI). The map categories are the ids of the joystick maps the game saved for every device.
        private static readonly Dictionary<string, int> MapCategories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Default", 0 }, { "Flight Control", 3 }, { "Weapon Control", 8 }, { "Camera Control", 11 }, { "View Control", 4 }, { "Map Control", 9 }
        };

        public override string GameId { get { return "TinyCombatArena"; } }
        public override string DisplayName { get { return "Tiny Combat Arena"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "tinycombatarena", "tca", "tinycombat" }; } }
        public override string DbFolder { get { return "TinyCombatArena"; } }
        public override bool Experimental { get { return true; } }
        public override bool ExclusiveFlightSim { get { return true; } }
        public override string Notes
        {
            get
            {
                return "Experimental. Close Tiny Combat Arena before exporting (it saves its bindings when the bindings menu closes). Binds are " +
                       "global, one set for every aircraft; keyboard binds and hat diagonals are not written.";
            }
        }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "Arena" }; } }
        protected override string PlayerName { get { return "Player"; } }
        protected override bool HatsVerified { get { return true; } }
        protected override int ButtonAxisRange { get { return 1; } }

        public override List<string> DiscoverConfigPaths(List<string> warnings)
        {
            return UnityPrefsStore.RegistryKeyExists(SubKey) ? new List<string> { RegistryKey } : new List<string>();
        }

        protected override bool TryOpenStore(string configPath, List<string> errors, out RewiredStore store)
        {
            UnityPrefsStore prefs;
            bool ok = UnityPrefsStore.TryOpen(configPath, SubKey, DisplayName, PlayerName, errors, out prefs);
            store = prefs;
            return ok;
        }

        protected override IReadOnlyDictionary<int, string> DefaultCategories { get { return Categories; } }

        protected override int MapCategoryOf(string actionCategory)
        {
            int map;
            return MapCategories.TryGetValue(actionCategory ?? string.Empty, out map) ? map : -1;
        }

        // A flight-only game: clean and nuke release every assignable action.
        protected override bool IsCleanCategory(string category)
        {
            return MapCategories.ContainsKey(category ?? string.Empty);
        }

        protected override RewiredLayout CatalogLayout { get { return RewiredLayout.TinyCombatArena; } }

        // The InputManager sits in one of the data files (sharedassets1.assets in 0.17): the first that parses wins.
        protected override string FindCatalogAssets(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;
            string path = sourcePath.Trim().Trim('"');
            if (File.Exists(path))
                return path;
            string data = Directory.Exists(Path.Combine(path, "Arena_Data")) ? Path.Combine(path, "Arena_Data") : path;
            if (!Directory.Exists(data))
                return null;
            IEnumerable<string> candidates = Directory.GetFiles(data, "sharedassets*.assets").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Concat(new[] { Path.Combine(data, "resources.assets"), Path.Combine(data, "globalgamemanagers.assets") }).Where(File.Exists);
            foreach (string candidate in candidates)
            {
                List<RewiredActionRecord> records;
                Dictionary<int, string> categories;
                string error;
                if (RewiredInputManagerData.TryParse(File.ReadAllBytes(candidate), CatalogLayout, out records, out categories, out error))
                    return candidate;
            }
            return null;
        }
    }
}
