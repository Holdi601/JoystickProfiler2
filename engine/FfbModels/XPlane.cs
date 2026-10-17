using System.Collections.Generic;

namespace JoyPro.Engine.Axis
{
    // X-Plane 11 and X-Plane 12 control settings kept in the joystick preferences ("... Joysticks.prf", the file JoyPro
    // exports to), one model per version (games XPlane11 / XPlane12). X-Plane has no DirectInput force feedback of its
    // own; the only FFB-related setting is X-Plane 12's control-loaded axis flag. See docs/games/xplane.md.
    internal static class XPlaneFfbModel
    {
        // Game-wide key -> prf key (_joy_prat / _joy_damp are indexed by axis use code: 1 pitch, 2 roll, 3 yaw,
        // 68/69/70 copilot pitch/roll/yaw).
        public static readonly Dictionary<string, string> GameWideKeys = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "pitchResponse", "_joy_prat1" },
            { "rollResponse", "_joy_prat2" },
            { "yawResponse", "_joy_prat3" },
            { "copilotPitchResponse", "_joy_prat68" },
            { "copilotRollResponse", "_joy_prat69" },
            { "copilotYawResponse", "_joy_prat70" },
            { "pitchStability", "_joy_damp1" },
            { "rollStability", "_joy_damp2" },
            { "yawStability", "_joy_damp3" },
            { "discretizeFlaps", "_discretize_flap_axis" }
        };

        public const string ControlLoaded = "controlLoaded";

        private const string Response = "Settings > Joystick > Control Sensitivity, Control Response slider (0 = linear, 1 = full right: 50 % stick gives ~10-15 % deflection). Applies to pitch/roll/yaw axes without a response curve of their own. ";

        private static void AddResponse(FfbModel model)
        {
            model.Parameters.Add(new FfbParameter { Key = "pitchResponse", Label = "Control response: pitch", Min = 0, Max = 1, Default = 0, GameWide = true, Help = Response + "_joy_prat1." });
            model.Parameters.Add(new FfbParameter { Key = "rollResponse", Label = "Control response: roll", Min = 0, Max = 1, Default = 0, GameWide = true, Help = Response + "_joy_prat2." });
            model.Parameters.Add(new FfbParameter { Key = "yawResponse", Label = "Control response: yaw", Min = 0, Max = 1, Default = 0, GameWide = true, Help = Response + "_joy_prat3." });
        }

        private static FfbParameter DiscretizeFlaps()
        {
            return new FfbParameter { Key = "discretizeFlaps", Label = "Flap axis snaps to detents", Toggle = true, Default = 0, GameWide = true,
                Help = "_discretize_flap_axis (UI label not yet seen in the game)." };
        }

        [GameFfbModel]
        private static FfbModel Build12()
        {
            FfbModel model = new FfbModel
            {
                Game = "XPlane12",
                Title = "X-Plane 12",
                Description = "Control settings in the exported Joysticks.prf: control response per axis (pilot and copilot), the flap axis detent option (device *), " +
                              "and per device whether its pitch/roll/yaw axes are control-loaded by force feedback software. X-Plane has no force feedback of its own."
            };
            AddResponse(model);
            model.Parameters.Add(new FfbParameter { Key = "copilotPitchResponse", Label = "Control response: copilot pitch", Min = 0, Max = 1, Default = 0, GameWide = true,
                Help = Response + "_joy_prat68 (use code 68 from the X-Plane 12 use-code table; written only when the file has that line)." });
            model.Parameters.Add(new FfbParameter { Key = "copilotRollResponse", Label = "Control response: copilot roll", Min = 0, Max = 1, Default = 0, GameWide = true,
                Help = Response + "_joy_prat69 (written only when the file has that line)." });
            model.Parameters.Add(new FfbParameter { Key = "copilotYawResponse", Label = "Control response: copilot yaw", Min = 0, Max = 1, Default = 0, GameWide = true,
                Help = Response + "_joy_prat70 (written only when the file has that line)." });
            model.Parameters.Add(DiscretizeFlaps());
            model.Parameters.Add(new FfbParameter { Key = ControlLoaded, Label = "Control-loaded axes (FFB stick/yoke/pedals)", Toggle = true, Default = 0,
                Help = "Marks the device's pitch, roll and yaw axes (pilot and copilot) as centred by force feedback software, so X-Plane does not add trim on top (_joy_ffb_axis, the .joy tag 'ffb'). Written only on axes JoyPro exports and only when the file has _joy_ffb_axis lines." });
            return model;
        }

        [GameFfbModel]
        private static FfbModel Build11()
        {
            const string stability = "Settings > Joystick > Stability Augmentation (X-Plane 11; X-Plane 12 removed it). ";
            FfbModel model = new FfbModel
            {
                Game = "XPlane11",
                Title = "X-Plane 11",
                Description = "Control settings in the exported Joysticks.prf: control response and stability augmentation per axis, and the flap axis detent option (device *). " +
                              "X-Plane has no force feedback of its own (X-Plane 11 has no control-loaded axis flag)."
            };
            AddResponse(model);
            model.Parameters.Add(new FfbParameter { Key = "pitchStability", Label = "Stability augmentation: pitch", Min = 0, Max = 1, Default = 0, GameWide = true, Help = stability + "_joy_damp1." });
            model.Parameters.Add(new FfbParameter { Key = "rollStability", Label = "Stability augmentation: roll", Min = 0, Max = 1, Default = 0, GameWide = true, Help = stability + "_joy_damp2." });
            model.Parameters.Add(new FfbParameter { Key = "yawStability", Label = "Stability augmentation: yaw", Min = 0, Max = 1, Default = 0, GameWide = true, Help = stability + "_joy_damp3." });
            model.Parameters.Add(DiscretizeFlaps());
            return model;
        }
    }
}
