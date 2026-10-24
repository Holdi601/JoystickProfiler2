using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace JoyPro.Engine.Games
{
    // ACE COMBAT 8: Wings of Theve (Unreal Engine 5, Easy Anti-Cheat): a virtual-only game (docs/games/ace-combat.md,
    // docs/games/one-device-games.md). Its exe reads Xbox pads (XInput 1.4) and the licensed flight sticks through
    // Thrustmaster's SDK only; generic DirectInput sticks are not read. JoyPro flies it with the virtual Xbox 360 pad
    // (engine vjoy mode AceCombat8 xbox) and writes nothing:
    //  - the settings (System.sav, GVAS) end with a checksum; JoyPro only
    //    reads the gamepad keys the player set (<Action>Key_Controller = an Unreal FKey name) from it;
    //  - the catalog (engine ac8 refresh-db) holds one "Pad_*" action per gamepad control with that key in the 5th
    //    column, so the virtual-pad planner pins the HOTAS input to the control the game uses. The sticks (pitch, roll,
    //    camera) are fixed in the game and come from JoyPro's own table.
    internal sealed class AceCombat8Integration : IGameIntegration, IGameDeviceLimit, IGameBuiltInCatalog
    {
        public string GameId { get { return "AceCombat8"; } }
        public string DisplayName { get { return "ACE COMBAT 8: Wings of Theve"; } }
        public IReadOnlyList<string> Aliases { get { return new[] { "ac8", "acecombat8", "acecombat" }; } }
        public string DbFolder { get { return "AceCombat8"; } }
        public bool Experimental { get { return true; } }
        public bool ExclusiveFlightSim { get { return false; } }
        public string Notes
        {
            get
            {
                return "Reads Xbox pads (and a few licensed sticks) only: fly it with the virtual Xbox pad (engine vjoy mode AceCombat8 xbox; needs ViGEmBus). " +
                       "JoyPro writes nothing for this game (checksummed settings): it puts your HOTAS on the pad controls the game uses. " +
                       "In the game: Options > Controls, controller, and keep Steam Input off for the game.";
            }
        }
        public IReadOnlyList<string> ProcessNames { get { return new[] { "AceCombat8", "AceCombat8-Win64-Shipping", "start_protected_game" }; } }
        public bool SupportsCatalogRefresh { get { return true; } }
        // Nothing is read or written (the binds reach the game through the virtual pad).
        public GameFeature KeyboardBinds { get { return GameFeature.No; } }
        public GameFeature ModifierBinds { get { return GameFeature.No; } }

        public int MaxJoysticks { get { return 0; } }
        public string SuggestedMode { get { return "xbox"; } }
        public string DeviceLimitNote { get { return "Reads Xbox controllers (XInput) and a few licensed sticks, no generic joystick: fly it with the virtual Xbox pad."; } }
        public IReadOnlyList<string> AvoidVJoyAxes { get { return new string[0]; } }

        public const string PadPrefix = "Pad_";
        private const string ControllerSuffix = "Key_Controller";

        // The sticks the game does not let the player move: JoyPro's own table (pitch/roll on the left stick, the
        // camera on the right stick, in both control types).
        private static readonly string[][] FixedPadActions =
        {
            new[] { "Pad_Pitch", "Pitch (left stick up/down)", "axis", "Gamepad_LeftY" },
            new[] { "Pad_Roll", "Roll (left stick left/right)", "axis", "Gamepad_LeftX" },
            new[] { "Pad_CameraYaw", "Look left/right (right stick)", "axis", "Gamepad_RightX" },
            new[] { "Pad_CameraPitch", "Look up/down (right stick)", "axis", "Gamepad_RightY" }
        };

        // The player-mappable gamepad keys (System.sav <Name>Key_Controller) with their defaults (build of 2026-10).
        private static readonly string[][] MappableDefaults =
        {
            new[] { "YawLeft", "Yaw left", "button", "Gamepad_LeftShoulder" },
            new[] { "YawRight", "Yaw right", "button", "Gamepad_RightShoulder" },
            new[] { "ThrottleUp", "Throttle up (accelerate)", "axis", "Gamepad_RightTriggerAxis" },
            new[] { "ThrottleDown", "Throttle down (brake)", "axis", "Gamepad_LeftTriggerAxis" },
            new[] { "FireMachineGun", "Fire gun", "button", "Gamepad_FaceButton_Bottom" },
            new[] { "FireMissile", "Fire missile", "button", "Gamepad_FaceButton_Right" },
            new[] { "ChangeWeapon", "Change weapon", "button", "Gamepad_FaceButton_Left" },
            new[] { "SwitchTarget", "Switch target", "button", "Gamepad_FaceButton_Top" },
            new[] { "ToggleRadarMapDisplay", "Radar map", "button", "Gamepad_Special_Left" }
        };

        public static string SavePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BANDAI NAMCO Entertainment", "ACE COMBAT 8", "Saved", "SaveGames", "System.sav");
        }

        public List<string> DiscoverConfigPaths(List<string> warnings)
        {
            string path = SavePath();
            return File.Exists(path) ? new List<string> { path } : new List<string>();
        }

        // ------------------------------------------------------------------ catalog

        public GameCatalog RefreshCatalog(string sourcePath, GameCatalog current, GameIoResult result)
        {
            string path = string.IsNullOrWhiteSpace(sourcePath) || Directory.Exists(sourcePath) ? SavePath() : sourcePath;
            Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);
            string controlType = string.Empty;
            if (File.Exists(path))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    keys = ReadControllerKeys(data);
                    controlType = ReadEnum(data, "EOptionsControlTypes::");
                    result.Steps.Add("Read " + keys.Count.ToString(CultureInfo.InvariantCulture) + " gamepad key(s) from " + path + (controlType.Length > 0 ? " (control type " + controlType + ")" : string.Empty) + ".");
                }
                catch (IOException ex)
                {
                    result.Warnings.Add(path + " could not be read: " + ex.Message);
                }
            }
            if (keys.Count == 0)
                result.Steps.Add("No gamepad keys read from the game's settings (start the game once); JoyPro's table of the default gamepad layout is used.");
            return CatalogOf(keys);
        }

        // Before the first refresh: JoyPro's table of the default gamepad layout (what the refresh writes without a save).
        public GameCatalog BuiltInCatalog()
        {
            return CatalogOf(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        private GameCatalog CatalogOf(Dictionary<string, string> keys)
        {
            GameCatalog catalog = new GameCatalog { GameId = GameId };
            foreach (string[] row in FixedPadActions)
                catalog.Add(new GameAction { Id = row[0], Title = row[1], IsAxis = row[2] == "axis", Category = "Gamepad", NativeId = row[3] });
            foreach (string[] row in MappableDefaults)
            {
                string key;
                if (!keys.TryGetValue(row[0], out key) || !key.StartsWith("Gamepad_", StringComparison.Ordinal))
                    key = row[3];
                catalog.Add(new GameAction { Id = PadPrefix + row[0], Title = row[1], IsAxis = key.EndsWith("Axis", StringComparison.Ordinal) || key.EndsWith("X", StringComparison.Ordinal) || key.EndsWith("Y", StringComparison.Ordinal), Category = "Gamepad", NativeId = key });
            }
            // Gamepad keys of the save JoyPro has no row for: listed under their own name.
            foreach (KeyValuePair<string, string> extra in keys.Where(k => MappableDefaults.All(r => r[0] != k.Key) && k.Value.StartsWith("Gamepad_", StringComparison.Ordinal)))
                catalog.Add(new GameAction { Id = PadPrefix + extra.Key, Title = Split(extra.Key), IsAxis = extra.Value.EndsWith("Axis", StringComparison.Ordinal), Category = "Gamepad", NativeId = extra.Value });
            return catalog;
        }

        private static string Split(string name)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        // GVAS FStrings: int32 length (with the terminating zero) + ASCII + 0. "<Name>Key_Controller" is a property name;
        // its value is the next FString starting with "Gamepad_" (the FKey's KeyName) within the property.
        internal static Dictionary<string, string> ReadControllerKeys(byte[] data)
        {
            Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);
            int pos = 0;
            while (true)
            {
                int at = IndexOf(data, Encoding.ASCII.GetBytes(ControllerSuffix + "\0"), pos);
                if (at < 0)
                    break;
                pos = at + ControllerSuffix.Length + 1;
                int start = at;
                while (start > 0 && IsNameChar(data[start - 1]))
                    start--;
                if (start < 4)
                    continue;
                int length = BitConverter.ToInt32(data, start - 4);
                if (length != at + ControllerSuffix.Length + 1 - start)
                    continue;
                string name = Encoding.ASCII.GetString(data, start, at - start);
                // The value lies within this property: before the next property's name.
                int limit = Math.Min(data.Length, pos + 300);
                int next = IndexOf(data, Encoding.ASCII.GetBytes(ControllerSuffix + "\0"), pos);
                if (next > 0)
                    limit = Math.Min(limit, next - ControllerSuffix.Length - 4);
                string value = NextGamepadKey(data, pos, limit);
                if (name.Length > 0 && value.Length > 0 && !keys.ContainsKey(name))
                    keys[name] = value;
            }
            return keys;
        }

        private static string NextGamepadKey(byte[] data, int from, int to)
        {
            byte[] prefix = Encoding.ASCII.GetBytes("Gamepad_");
            int at = IndexOf(data, prefix, from);
            if (at < 4 || at >= to)
                return string.Empty;
            int length = BitConverter.ToInt32(data, at - 4);
            if (length < 2 || at + length > data.Length || data[at + length - 1] != 0)
                return string.Empty;
            return Encoding.ASCII.GetString(data, at, length - 1);
        }

        private static string ReadEnum(byte[] data, string prefix)
        {
            int at = IndexOf(data, Encoding.ASCII.GetBytes(prefix), 0);
            if (at < 0)
                return string.Empty;
            int end = at + prefix.Length;
            while (end < data.Length && IsNameChar(data[end]))
                end++;
            return Encoding.ASCII.GetString(data, at + prefix.Length, end - at - prefix.Length);
        }

        private static bool IsNameChar(byte b)
        {
            return (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_';
        }

        private static int IndexOf(byte[] data, byte[] pattern, int from)
        {
            for (int i = Math.Max(0, from); i <= data.Length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j])
                    j++;
                if (j == pattern.Length)
                    return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ read / write

        // The binds live on the virtual pad; the game file holds no joystick binds JoyPro could read.
        public GameScanResult Read(string configPath, GameCatalog catalog, GameDeviceDirectory devices)
        {
            GameScanResult result = new GameScanResult();
            result.Steps.Add(DisplayName + " stores no joystick binds; JoyPro's binds reach it through the virtual Xbox pad (engine vjoy mode " + GameId + " xbox).");
            return result;
        }

        public GameIoResult Write(GameExportRequest request)
        {
            GameIoResult result = new GameIoResult();
            int pad = request.Bindings.Count(b => b.ActionId.StartsWith(PadPrefix, StringComparison.Ordinal));
            result.Steps.Add(pad.ToString(CultureInfo.InvariantCulture) + " gamepad action bind(s): nothing is written (the game's settings are checksummed); " +
                             "they reach the game through the virtual Xbox pad (engine vjoy mode " + GameId + " xbox), on the controls the game uses. " +
                             "After changing a gamepad key in the game, run engine ac8 refresh-db and engine vjoy map.");
            foreach (GameInputBinding binding in request.Bindings.Where(b => !b.ActionId.StartsWith(PadPrefix, StringComparison.Ordinal)))
            {
                result.BindingsSkipped++;
                result.Warnings.Add(binding.Describe() + ": not a gamepad action of the catalog.");
            }
            return result;
        }
    }
}
