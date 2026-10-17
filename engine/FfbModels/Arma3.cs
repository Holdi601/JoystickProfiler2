namespace JoyPro.Engine.Axis
{
    // Arma 3 control settings beside the binds (docs/games/arma3.md), written to <Name>.Arma3Profile on export:
    // per device the JoysticksList entry (mode, analogClickHigh/Low), game-wide the top-level helicopter flight model
    // options (RTD*) and the gamepad vibration switch. Arma 3 has no joystick force feedback setting.
    internal static class Arma3SettingsModel
    {
        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = "Arma3",
                Title = "Arma 3",
                Description = "Per device (Controls > Controllers): enabled (mode Custom / Disabled) and the analog click thresholds (axis used as a button). Game-wide (device *, Game options): helicopter advanced flight model and its options, gamepad vibrations. Written to the .Arma3Profile on export.",
                Parameters =
                {
                    new FfbParameter { Key = "enabled", Label = "Device enabled (Controls > Controllers)", Toggle = true, Default = 1,
                        Help = "mode=\"Custom\" or \"Disabled\". A disabled device that gets binds in an export is enabled anyway unless this is set to off." },
                    new FfbParameter { Key = "analogClickHigh", Label = "Analog click: press threshold", Min = 0, Max = 1, Default = 0.4,
                        Help = "An axis bound to a button action counts as pressed above this value (analogClickHigh)." },
                    new FfbParameter { Key = "analogClickLow", Label = "Analog click: release threshold", Min = 0, Max = 1, Default = 0.2,
                        Help = "... and as released below this value (analogClickLow); keep it below the press threshold." },
                    new FfbParameter { Key = "RTDEnabled", Label = "Helicopter flight model: advanced (Simulation)", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "RTDAutoTrim", Label = "Advanced flight model: auto-trim", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "RTDFullHUD", Label = "Advanced flight model: show gauges", Toggle = true, Default = 1, GameWide = true },
                    new FfbParameter { Key = "RTDRoughLanding", Label = "Advanced flight model: rough landing", Toggle = true, Default = 1, GameWide = true },
                    new FfbParameter { Key = "RTDWindDynamic", Label = "Advanced flight model: wind effects", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "RTDStressDamage", Label = "Advanced flight model: stress damage", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "vibrations", Label = "Gamepad vibrations", Toggle = true, Default = 1, GameWide = true }
                }
            };
        }
    }
}
