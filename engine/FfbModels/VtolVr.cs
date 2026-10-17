namespace JoyPro.Engine.Axis
{
    // VTOL VR has no DirectInput force feedback; its control settings (Settings > Controls) live in
    // %APPDATA%\Boundless Dynamics, LLC\VTOLVR\SaveData\gameSettings.cfg, root GAMESETTINGS, one "\tKEY = value" line per
    // setting (GameSettings.SaveGameSettings, floats "G" en-US; on/off settings 1 / -1, on = value > 0; floats clamped to
    // their range on load). Device "*"; written by the vtolvr-native export (that file sits next to inputbindings.cfg).
    internal static class VtolVrGameSettings
    {
        public const string Game = "VTOLVR";

        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "VTOL VR (control settings, VR haptics)",
                Description = "Game-wide, Settings > Controls: gameSettings.cfg next to inputbindings.cfg (device *), in the game's units (% for dead zones and haptics). Written by the vtolvr-native export. VTOL VR has no joystick force feedback; haptics are the VR controllers'.",
                Parameters =
                {
                    new FfbParameter { Key = "HARDWARE_RUDDER", Label = "Hardware Control Rudder", Toggle = true, Default = 0, GameWide = true,
                        Help = "The game: \"Use hardware (rudder pedals) for rudder. Overrides other settings.\" Needed for the native rudder and toe brake binds; read when a vehicle spawns." },
                    new FfbParameter { Key = "THUMB_RUDDER", Label = "Thumb Control Rudder", Toggle = true, Default = 0, GameWide = true,
                        Help = "The game: \"Use throttle thumb control for rudder instead of joystick-twist.\" Hardware Control Rudder overrides it." },
                    new FfbParameter { Key = "TWIST_RUDDER_DEADZONE", Label = "Twist Rudder Deadzone (%)", Min = 0, Max = 90, Default = 3, GameWide = true,
                        Help = "Dead zone of the VR stick twist rudder in % (rescaled), not of hardware pedals." },
                    new FfbParameter { Key = "THUMBSTICK_DEADZONE", Label = "Thumbstick Deadzone (%)", Min = 0, Max = 90, Default = 3, GameWide = true,
                        Help = "Dead zone of the VR controller thumbsticks in % (radial, rescaled)." },
                    new FfbParameter { Key = "THUMBSTICK_MODE", Label = "Thumbstick Mode", Toggle = true, Default = 0, GameWide = true,
                        Help = "The game: \"Use controller thumbstick instead of touch-pad.\" Shown only for touch-pad or unknown controllers (default depends on the controller)." },
                    new FfbParameter { Key = "TAP_TOGGLE_GRIP", Label = "Toggle Grip", Toggle = true, Default = 1, GameWide = true,
                        Help = "The game: \"Allow toggling throttle/joystick grip by tapping grip button.\" Not shown for Index controllers." },
                    new FfbParameter { Key = "CONTROL_HAPTICS", Label = "Flight Control Haptics (%)", Min = 0, Max = 100, Default = 100, GameWide = true,
                        Help = "The game: \"Set the intensity of joystick/throttle haptic feedback.\" VR controller vibration on the virtual stick and throttle (default 75 on Quest)." },
                    new FfbParameter { Key = "OVERALL_HAPTICS", Label = "Overall Haptics (%)", Min = 0, Max = 200, Default = 100, GameWide = true,
                        Help = "The game: \"Set the intensity of all haptic feedback.\"" }
                }
            };
        }
    }
}
