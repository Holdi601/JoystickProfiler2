using System.Collections.Generic;

namespace JoyPro.Engine.Axis
{
    // WARDOGS has no force feedback; its game-wide HOTAS switches live in GameUserSettings.ini
    // [/Script/WDGame.WDUserSettings] and are managed here (device "*"). See docs/games/wardogs.md.
    internal static class WardogsGameSettings
    {
        public const string Game = "Wardogs";

        // Parameter key -> ini key in [/Script/WDGame.WDUserSettings] (True/False).
        public static readonly Dictionary<string, string> IniKeys = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "enableHotas", "bUseHOTASHelicopters" },
            { "selfCenteringCollective", "bCollectiveSelfCentering" }
        };

        [GameFfbModel]
        private static FfbModel Build()
        {
            return new FfbModel
            {
                Game = Game,
                Title = "WARDOGS (game input settings, no force feedback)",
                Description = "Game-wide, Settings > GAMEPAD > HOTAS: GameUserSettings.ini [/Script/WDGame.WDUserSettings] (device *). WARDOGS has no force feedback.",
                Parameters =
                {
                    new FfbParameter { Key = "enableHotas", Label = "Enable HOTAS", Toggle = true, Default = 1, GameWide = true,
                        Help = "The game: \"Fly helicopters with a flight stick (HOTAS) instead of mouse and keyboard. Disables mouse/keyboard flight controls while active.\" (bUseHOTASHelicopters). Without this setting an export with binds turns it on." },
                    new FfbParameter { Key = "selfCenteringCollective", Label = "Self-Centering Collective", Toggle = true, Default = 0, GameWide = true,
                        Help = "The game: \"For dual-stick (HOSAS) setups: the collective returns to the hover position when the stick is released, instead of holding its last value. Leave off for throttle levers, which keep their physical position.\" (bCollectiveSelfCentering; key matched by name, confirm with tools\\e2e\\games\\WardogsSettings.ps1)." }
                }
            };
        }
    }
}
