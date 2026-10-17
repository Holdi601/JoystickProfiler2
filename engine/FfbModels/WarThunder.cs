namespace JoyPro.Engine.Axis
{
    // War Thunder's game-wide control options on the Controls pages (aircraft and helicopter), in the units the game
    // shows. machine.blk keeps them in content{settings{}} (reals: % / 100, force gain / 50),
    // content{gui_options{OPTIONS_MODE_GAMEPLAY{}}} (USEROPT_JOYFX, USEROPT_AUTOTRIM) and content{controls{params{}}}
    // (holdThrottleForWEP). Sources: optionsExt.nut (get/set of each USEROPT), shortcutsGroupAircraft/Helicopter.nut;
    // keys as seen in real machine.blk files. Written by WarThunderIntegration (WarThunderSettingsBlk).
    internal static class WarThunderFfbModel
    {
        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = "WarThunder",
                Title = "War Thunder",
                Description = "Game-wide, Controls (aircraft/helicopter): force feedback, roll/pitch/yaw sensitivity, invert Y and throttle, hold throttle for WEP, autotrim; machine.blk settings{}, gui_options{} and controls{params{}} (device *), in the game's units.",
                Parameters =
                {
                    new FfbParameter { Key = "joyFX", Label = "Force feedback on", Toggle = true, Default = 1, GameWide = true,
                        Help = "The game needs a restart for this one (gui_options USEROPT_JOYFX)." },
                    new FfbParameter { Key = "forceGain", Label = "Force feedback multiplier", Min = 0, Max = 100, Default = 50, GameWide = true,
                        Help = "0..100 as the game shows it; 50 is the normal force (settings forceGain = value / 50)." },
                    new FfbParameter { Key = "aileronsMultiplier", Label = "Roll sensitivity, aircraft (%)", Min = 0, Max = 100, Default = 79, GameWide = true },
                    new FfbParameter { Key = "elevatorMultiplier", Label = "Pitch sensitivity, aircraft (%)", Min = 0, Max = 100, Default = 64, GameWide = true },
                    new FfbParameter { Key = "rudderMultiplier", Label = "Yaw sensitivity, aircraft (%)", Min = 0, Max = 100, Default = 43, GameWide = true },
                    new FfbParameter { Key = "helicopterCyclicRollMultiplier", Label = "Roll sensitivity, helicopter (%)", Min = 0, Max = 100, Default = 79, GameWide = true },
                    new FfbParameter { Key = "helicopterCyclicPitchMultiplier", Label = "Pitch sensitivity, helicopter (%)", Min = 0, Max = 100, Default = 64, GameWide = true },
                    new FfbParameter { Key = "helicopterPedalsMultiplier", Label = "Yaw sensitivity, helicopter (%)", Min = 0, Max = 100, Default = 43, GameWide = true },
                    new FfbParameter { Key = "invertY", Label = "Invert Y axis (aircraft)", Toggle = true, Default = 0, GameWide = true,
                        Help = "Adds to each axis's own Invert: set one or the other." },
                    new FfbParameter { Key = "invertYHelicopter", Label = "Invert Y axis (helicopter)", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "invertT", Label = "Invert throttle axis", Toggle = true, Default = 0, GameWide = true },
                    new FfbParameter { Key = "holdThrottleForWEP", Label = "Hold throttle for WEP", Toggle = true, Default = 0, GameWide = true,
                        Help = "War Emergency Power only while the throttle is held at maximum (controls params)." },
                    new FfbParameter { Key = "autotrim", Label = "Autotrim", Toggle = true, Default = 1, GameWide = true,
                        Help = "Tied to the control mode by the game: off = Full-real controls (JoyPro sets that mode), on = a full-real mode becomes Realistic; listed in Realistic and Full-real only (gui_options USEROPT_AUTOTRIM, USEROPT_HELPERS_MODE)." }
                }
            };
        }
    }
}
