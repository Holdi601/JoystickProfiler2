namespace JoyPro.Engine.Axis
{
    // Arma 2 / Operation Arrowhead control settings beside the binds (docs/games/arma2.md): only the device switch of
    // class JoysticksList (mode "Custom" / "Disable"). The games have no joystick force feedback or dead zone setting.
    internal static class Arma2SettingsModel
    {
        private static FfbModel Make(string game, string title)
        {
            return new FfbModel
            {
                Game = game,
                Title = title,
                Description = "Per device (Controls > Controllers): enabled (mode Custom / Disable). Written to the profile on export.",
                Parameters =
                {
                    new FfbParameter { Key = "enabled", Label = "Device enabled (Controls > Controllers)", Toggle = true, Default = 1,
                        Help = "mode=\"Custom\" or \"Disable\". A device in mode Disable or Scheme that gets binds in an export is switched to Custom unless this is set to off." }
                }
            };
        }

        [GameFfbModel]
        private static FfbModel BuildArma2()
        {
            return Make("Arma2", "Arma 2");
        }

        [GameFfbModel]
        private static FfbModel BuildArma2Oa()
        {
            return Make("Arma2OA", "Arma 2: Operation Arrowhead");
        }
    }
}
