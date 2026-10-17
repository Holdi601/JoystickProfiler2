namespace JoyPro.Engine.Axis
{
    // Arma Reforger game settings (docs/games/arma-reforger.md), GameUserSettings.conf in
    // profile\.save\app<id>_user<id>\settings: SCR_GameplaySettings.m_eHotasCollective (1.8 scripts) and
    // SCR_ControllerSettings.m_fForceFeedbackIntensity (1.9 experimental scripts). Only keys the file already has are
    // changed (format not seen on a real install).
    internal static class ArmaReforgerSettingsModel
    {
        public const string JoystickSlot = "joystickSlot";

        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = "ArmaReforger",
                Title = "Arma Reforger",
                Description = "Game-wide (device *): Settings > Gameplay > HOTAS collective mode and (1.9) Settings > Controllers > force feedback intensity, in GameUserSettings.conf. JoyPro only changes keys the file already has: change the setting in the game once.",
                Parameters =
                {
                    new FfbParameter { Key = "hotasCollective", Label = "HOTAS collective mode", Min = 0, Max = 3, Default = 3, GameWide = true,
                        Help = "m_eHotasCollective: 0 Default, 1 Manual, 2 Sticky, 3 Direct (the game's default for a HOTAS; an absolute collective lever)." },
                    new FfbParameter { Key = "forceFeedbackIntensity", Label = "Force feedback intensity (1.9)", Min = 0, Max = 3, Default = 1, GameWide = true,
                        Help = "m_fForceFeedbackIntensity, 0..3 (experimental branch 1.9 and later)." },
                    new FfbParameter { Key = JoystickSlot, Label = "Joystick slot (0-3, -1 automatic)", Min = -1, Max = 3, Default = -1,
                        Help = "Per device: which of the game's four joystick slots (Settings > Controllers > Devices) it gets, written as a device order rule. The game uses only four joysticks and only sorts the ones it detected." }
                }
            };
        }
    }
}
