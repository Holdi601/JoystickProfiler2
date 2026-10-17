using System;
using System.Collections.Generic;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Arma 2 (arma2.exe 1.11, <Name>.ArmA2Profile) and Arma 2: Operation Arrowhead (ArmA2OA.exe 1.62+, also Combined
    // Operations, BAF, PMC, ACR, DayZ mod; <Name>.ArmA2OAProfile). Both keep their profiles in Documents\ArmA 2 and
    // Documents\ArmA 2 Other Profiles\<Name>\ (see docs/games/arma2.md). Same key lines and codes as Arma 3, with:
    // helicopters and planes share HeliForward/Back, HeliCyclicLeft/Right and the pedals; "Thrust (Analogue)"
    // (HeliThrottlePos) is the plane throttle and the absolute helicopter collective; devices read 32 buttons; a device
    // is off in mode "Disable" or "Scheme"; no joystick combos. OA maps a full axis to one action from both halves.
    internal abstract class Arma2FamilyIntegration : ArmaProfileIntegration
    {
        protected static Dictionary<string, Composite> SharedComposites(bool fullAxis)
        {
            Dictionary<string, Composite> composites = new Dictionary<string, Composite>(StringComparer.OrdinalIgnoreCase)
            {
                { "Axis.Pitch", new Composite("HeliForward", "HeliBack") },
                { "Axis.Roll", new Composite("HeliCyclicLeft", "HeliCyclicRight") },
                { "Axis.Rudder", new Composite("HeliRudderLeft", "HeliRudderRight") },
                { "Axis.Turn", new Composite("HeliLeft", "HeliRight") },
                { "Axis.ThrustRate", new Composite("HeliUp", "HeliDown") },
                { "Axis.Throttle", new Composite("HeliThrottlePos", "HeliThrottlePos", fullAxis) },
                { "Axis.Airbrake", new Composite("HeliThrottleNeg", "HeliThrottleNeg", fullAxis) },
                { "Axis.LookHorizontal", new Composite("LookLeftCont", "LookRightCont") },
                { "Axis.LookVertical", new Composite("LookUpCont", "LookDownCont") },
                { "Axis.Zoom", new Composite("ZoomContIn", "ZoomContOut") }
            };
            return composites;
        }

        public override string DbFolder { get { return GameId; } }
        protected override string DefaultProfileFolder { get { return "ArmA 2"; } }
        protected override string OtherProfilesFolder { get { return "ArmA 2 Other Profiles"; } }
        protected override int MaxButtons { get { return 32; } }

        protected override bool IsDisabledMode(string mode)
        {
            return !string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        }

        protected override string DisabledModeName { get { return "Disable"; } }

        // sensitivity[16] (only after Customize > OK): the slider p as s = 10^p in [a] and [a+8]; the game reads [a] for
        // both halves (axis model "arma2"). joystickSensitivity[] (top level, legacy) is never touched.
        protected override string[] AxisTuningKeys
        {
            get { return new[] { "sensitivity" }; }
        }

        protected override void ReadAxisTuning(ArmaProfile profile, ArmaJoystick joystick, int axis, AxisTuning tuning)
        {
            List<string> tokens = ArrayTokens(profile, joystick.Guid, "sensitivity");
            if (tokens == null || axis >= tokens.Count)
                return;
            tuning.Native["sensitivity"] = Math.Round(Arma2AxisModel.SliderValue(TokenValue(tokens, axis, 1)), 4);
        }

        protected override void WriteAxisTuning(ArmaProfile profile, Guid device, int axis, AxisTuning tuning, string label, GameIoResult result)
        {
            double p;
            if (!tuning.Native.TryGetValue("sensitivity", out p))
                return;
            double s = Arma2AxisModel.FileValue(p);
            List<string> tokens = ArrayTokens(profile, device, "sensitivity") ?? new List<string>();
            while (tokens.Count < 16)
                tokens.Add("1");
            bool changed = SetToken(tokens, axis, s);
            changed |= SetToken(tokens, axis + 8, s);
            if (changed)
                profile.SetJoystickValue(device, "sensitivity", RenderArray(tokens), new[] { "isXInput", "offset", "name", "guid" },
                    label + " sensitivity " + tokens[axis] + " (slider " + AxisTuning.Num(Math.Round(p, 4)) + ")");
        }

        protected override string[] FlightPrefixes
        {
            get { return new[] { "Heli", "Flaps", "LandGear", "AutoHover", "LaunchCM", "NextCM" }; }
        }

        protected override string DefaultKeysClass { get { return "CfgDefaultKeysMapping"; } }

        // Arma 2 flies with the same weapons, view and radio actions as on foot: JoyPro manages the ones a HOTAS carries.
        protected override string[] ExtraManagedActions
        {
            get
            {
                return new[]
                {
                    "Eject", "GetOut", "Fire", "SwitchWeapon", "PersonView", "Optics", "OpticsMode", "ZoomInToggle", "ZoomOutToggle",
                    "LookAround", "LookAroundToggle", "LookCenter", "LookUp", "LookRightUp", "LookRight", "LookRightDown", "LookDown",
                    "LookLeftDown", "LookLeft", "LookLeftUp", "PushToTalkVehicle", "PushToTalkGroup", "PushToTalkSide",
                    "PushToTalkCommand", "PushToTalkDirect"
                };
            }
        }
    }

    internal sealed class Arma2OaIntegration : Arma2FamilyIntegration
    {
        private static readonly Dictionary<string, Composite> Composites = SharedComposites(true);

        public override string GameId { get { return "Arma2OA"; } }
        public override string DisplayName { get { return "Arma 2: Operation Arrowhead"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "arma2oa", "a2oa", "arrowhead", "arma2co" }; } }
        public override string Notes { get { return "Experimental (built from the installed game files). Also Combined Operations, BAF, PMC, ACR and DayZ mod. Close the game before exporting; start it once with every device connected (Controls > Controllers); JoyPro enables devices that get binds. The sensitivity slider is per device axis for every vehicle. Thrust (Analogue) is the plane throttle and the helicopter collective; 32 buttons per device; joystick only."; } }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "arma2oa", "arma2oa_be" }; } }

        protected override IReadOnlyDictionary<string, Composite> CompositeAxes { get { return Composites; } }
        protected override string ProfileExtension { get { return ".ArmA2OAProfile"; } }
        protected override bool ReadsMergedAxes { get { return true; } }
        protected override string[] ConfigPbos { get { return new[] { @"Expansion\dta\bin.pbo" }; } }
        protected override string[] LanguagePbos { get { return new[] { @"Expansion\dta\languagecore.pbo" }; } }
    }

    internal sealed class Arma2Integration : Arma2FamilyIntegration
    {
        private static readonly Dictionary<string, Composite> Composites = Arma2Composites();

        private static Dictionary<string, Composite> Arma2Composites()
        {
            // Arma 2 1.11 cannot map a full axis to one action: throttle and airbrake use one half each, and one lever
            // can split into thrust (forward half) and brake (rear half).
            Dictionary<string, Composite> composites = SharedComposites(false);
            composites["Axis.ThrottleBrake"] = new Composite("HeliThrottlePos", "HeliThrottleNeg");
            return composites;
        }

        public override string GameId { get { return "Arma2"; } }
        public override string DisplayName { get { return "Arma 2"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "arma2", "a2" }; } }
        public override string Notes { get { return "Experimental (built from the installed game files). Arma 2 standalone (arma2.exe); Operation Arrowhead and Combined Operations are the separate game 'arma2oa'. Close the game before exporting; start it once with every device connected (Controls > Controllers); JoyPro enables devices that get binds. The sensitivity slider is per device axis for every vehicle. Throttle and airbrake use half a lever (no full-axis mapping in Arma 2); 32 buttons per device; joystick only."; } }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "arma2" }; } }

        protected override IReadOnlyDictionary<string, Composite> CompositeAxes { get { return Composites; } }
        protected override string ProfileExtension { get { return ".ArmA2Profile"; } }
        protected override string[] ConfigPbos { get { return new[] { @"Dta\bin.pbo" }; } }
        protected override string[] LanguagePbos { get { return new[] { @"Dta\languagecore.pbo" }; } }
    }
}
