using System;
using System.Collections.Generic;

namespace JoyPro.Engine.Axis
{
    // FreeSpace Open's game-wide joystick options (no force feedback settings of its own): Options > Controls dead zone
    // and sensitivity, the same for every axis of every joystick. FSO keeps them in the pilot (sections.settings
    // joy_dead_zone_size / joy_sensitivity, which win when the pilot loads) and in the mod's settings
    // (data\<mod>_settings.ini [Input] JoystickDeadZone / JoystickSensitivity); the export writes both. See
    // docs/games/fate-of-the-galaxy.md "Axis settings".
    public static class FsoGameSettings
    {
        public const string DeadZone = "deadZone";
        public const string Sensitivity = "sensitivity";

        public static readonly Dictionary<string, string> PilotKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { DeadZone, "joy_dead_zone_size" },
            { Sensitivity, "joy_sensitivity" }
        };

        public static readonly Dictionary<string, string> IniKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { DeadZone, "JoystickDeadZone" },
            { Sensitivity, "JoystickSensitivity" }
        };

        // The option's range in the game (DeadZoneOption 0..45, SensitivityOption 0..9).
        public static int Clamp(string key, double value)
        {
            int max = string.Equals(key, Sensitivity, StringComparison.OrdinalIgnoreCase) ? 9 : 45;
            return (int)Math.Max(0, Math.Min(max, Math.Round(value)));
        }

        [GameFfbModel]
        private static FfbModel Model()
        {
            return new FfbModel
            {
                Game = "FateOfTheGalaxy",
                Title = "Fate of the Galaxy (FreeSpace Open)",
                Description = "Game-wide joystick options (device *), written by the export into the pilot and the mod's settings. FreeSpace Open has no force feedback settings of its own.",
                Parameters =
                {
                    new FfbParameter { Key = DeadZone, Label = "Joystick dead zone", Min = 0, Max = 45, Default = 10, GameWide = true,
                        Help = "Options > Controls dead zone, one for every stick axis (not the absolute throttle). FSO cuts dead zone x 2 % of the half travel around the centre: 10 = 20 %." },
                    new FfbParameter { Key = Sensitivity, Label = "Joystick sensitivity", Min = 0, Max = 9, Default = 9, GameWide = true,
                        Help = "Options > Controls sensitivity: 9 is linear, lower values blend in a fifth-power curve (gentler around the centre, full at the end)." }
                }
            };
        }
    }
}
