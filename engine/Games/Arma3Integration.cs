using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine.Games
{
    // Arma 3 keeps binds as "key<Action>[]={code,...};" lines in <Name>.Arma3Profile (see docs/games/arma3.md and the
    // shared ArmaProfileIntegration). Per device and physical axis: sensitivity[16], deadZone[8], steepness[8] (axis
    // model "arma3"); per device: mode, analogClickHigh/Low; game-wide: RTD* (advanced helicopter flight model) and
    // vibrations (FfbModels\Arma3.cs).
    internal sealed class Arma3Integration : ArmaProfileIntegration
    {
        private static readonly Dictionary<string, Composite> Composites = new Dictionary<string, Composite>(StringComparer.OrdinalIgnoreCase)
        {
            { "Axis.HeliCyclicPitch", new Composite("HeliCyclicForward", "HeliCyclicBack") },
            { "Axis.HeliCyclicRoll", new Composite("HeliCyclicLeft", "HeliCyclicRight") },
            { "Axis.HeliCollective", new Composite("HeliCollectiveRaiseCont", "HeliCollectiveLowerCont") },
            { "Axis.Rudder", new Composite("HeliRudderLeft", "HeliRudderRight") },
            { "Axis.PlanePitch", new Composite("HeliForward", "HeliBack") },
            { "Axis.PlaneRoll", new Composite("AirBankLeft", "AirBankRight") },
            // "Thrust (Analog)" / "Speedbrake (Analog)": both halves on the one action (the game's tooltip: "Both + and -
            // axes need to be mapped"), negative half first = lever forward is maximum.
            { "Axis.PlaneThrottle", new Composite("HeliThrottlePos", "HeliThrottlePos", true) },
            { "Axis.PlaneAirbrake", new Composite("HeliThrottleNeg", "HeliThrottleNeg", true) }
        };

        private static readonly string[] GameOptionKeys = { "RTDEnabled", "RTDFullHUD", "RTDRoughLanding", "RTDWindDynamic", "RTDAutoTrim", "RTDStressDamage", "vibrations" };

        public override string GameId { get { return "Arma3"; } }
        public override string DisplayName { get { return "Arma 3"; } }
        public override IReadOnlyList<string> Aliases { get { return new[] { "arma3", "a3" }; } }
        public override string DbFolder { get { return "Arma3"; } }
        public override string Notes { get { return "Experimental (built from documentation and the game's files). Close Arma 3 before exporting; the device must have been seen by Arma once (Controls > Controllers). Response settings (sensitivity, dead zone, steepness) are per device axis for every vehicle."; } }
        public override IReadOnlyList<string> ProcessNames { get { return new[] { "arma3_x64", "arma3", "arma3battleye" }; } }

        protected override IReadOnlyDictionary<string, Composite> CompositeAxes { get { return Composites; } }
        protected override string DefaultProfileFolder { get { return "Arma 3"; } }
        protected override string OtherProfilesFolder { get { return "Arma 3 - Other Profiles"; } }
        protected override string ProfileExtension { get { return ".Arma3Profile"; } }
        protected override bool ReadsMergedAxes { get { return true; } }
        protected override string[] ConfigPbos { get { return new[] { @"Dta\bin.pbo" }; } }
        protected override string DefaultKeysClass { get { return "CfgDefaultKeysPresets/Default/Mappings"; } }
        protected override string[] LanguagePbos { get { return new[] { @"Dta\languagecore_f.pbo" }; } }

        protected override string[] AxisTuningKeys
        {
            get { return new[] { "sensitivityPos", "sensitivityNeg", "deadZone", "steepness" }; }
        }

        // sensitivity[] as the game loads it: fewer than 8 values are copied to both halves, missing ones are 0.
        private static List<string> Sensitivity(ArmaProfile profile, Guid device)
        {
            List<string> tokens = ArrayTokens(profile, device, "sensitivity");
            if (tokens == null)
                return null;
            List<string> full = Enumerable.Repeat("0", 16).ToList();
            for (int i = 0; i < Math.Min(tokens.Count, 16); i++)
            {
                full[i] = tokens[i];
                if (tokens.Count < 8)
                    full[i + 8] = tokens[i];
            }
            return full;
        }

        private static List<string> Padded(List<string> tokens, Func<int, string> fallback)
        {
            if (tokens == null)
                return null;
            List<string> full = Enumerable.Range(0, 8).Select(fallback).ToList();
            for (int i = 0; i < Math.Min(tokens.Count, 8); i++)
                full[i] = tokens[i];
            return full;
        }

        private static string DefaultDeadZone(int axis)
        {
            return ArmaProfile.FormatFloat(Arma3AxisModel.DefaultDeadZones[axis]);
        }

        protected override void ReadAxisTuning(ArmaProfile profile, ArmaJoystick joystick, int axis, AxisTuning tuning)
        {
            List<string> sensitivity = Sensitivity(profile, joystick.Guid);
            if (sensitivity != null)
            {
                tuning.Native["sensitivityPos"] = Math.Max(-1, Math.Min(1, TokenValue(sensitivity, axis, 0)));
                tuning.Native["sensitivityNeg"] = Math.Max(-1, Math.Min(1, TokenValue(sensitivity, axis + 8, 0)));
            }
            List<string> deadZone = Padded(ArrayTokens(profile, joystick.Guid, "deadZone"), DefaultDeadZone);
            if (deadZone != null)
                tuning.Native["deadZone"] = Math.Round(TokenValue(deadZone, axis, Arma3AxisModel.DefaultDeadZones[axis]), 6);
            List<string> steepness = Padded(ArrayTokens(profile, joystick.Guid, "steepness"), i => "1");
            if (steepness != null)
                tuning.Native["steepness"] = Math.Round(TokenValue(steepness, axis, 1), 6);
        }

        protected override void WriteAxisTuning(ArmaProfile profile, Guid device, int axis, AxisTuning tuning, string label, GameIoResult result)
        {
            double value;
            bool hasPos = tuning.Native.TryGetValue("sensitivityPos", out value);
            if (hasPos || tuning.Native.ContainsKey("sensitivityNeg"))
            {
                List<string> tokens = Sensitivity(profile, device) ?? Enumerable.Repeat("0", 16).ToList();
                bool changed = false;
                if (hasPos)
                    changed |= SetToken(tokens, axis, Math.Max(-1, Math.Min(1, value)));
                // The game's Customize dialog has one slider per axis and saves it to both halves on OK (checked in
                // game, 2.22): a missing - half follows the + half, and different halves get a warning.
                double neg;
                if (!tuning.Native.TryGetValue("sensitivityNeg", out neg))
                    neg = hasPos ? tuning.Native["sensitivityPos"] : 0;
                else if (hasPos && Math.Abs(neg - tuning.Native["sensitivityPos"]) > 1e-9)
                    result.Warnings.Add(label + ": different sensitivities per half are kept in the file, but OK in the game's Customize dialog sets both halves to the + half.");
                changed |= SetToken(tokens, axis + 8, Math.Max(-1, Math.Min(1, neg)));
                if (changed)
                    profile.SetJoystickValue(device, "sensitivity", RenderArray(tokens), new[] { "isXInput", "offset", "name", "guid" },
                        label + " sensitivity +" + tokens[axis] + " / -" + tokens[axis + 8]);
            }
            if (tuning.Native.TryGetValue("deadZone", out value))
            {
                double clamped = Math.Max(0, Math.Min(0.1, value));
                if (Math.Abs(clamped - value) > 1e-9)
                    result.Warnings.Add(label + ": dead zone " + AxisTuning.Num(value) + " is outside the game's 0..0.1; written as " + AxisTuning.Num(clamped) + ".");
                List<string> tokens = Padded(ArrayTokens(profile, device, "deadZone"), DefaultDeadZone) ?? Enumerable.Range(0, 8).Select(DefaultDeadZone).ToList();
                if (SetToken(tokens, axis, clamped))
                    profile.SetJoystickValue(device, "deadZone", RenderArray(tokens), new[] { "sensitivity", "isXInput", "offset", "name", "guid" },
                        label + " dead zone " + tokens[axis]);
            }
            if (tuning.Native.TryGetValue("steepness", out value))
            {
                double snapped = Arma3AxisModel.SnapSteepness(value);
                if (Math.Abs(snapped - value) > 1e-6)
                    result.Warnings.Add(label + ": steepness " + AxisTuning.Num(value) + " is not one of the game's choices (1.0, 1.2, 1.5, 1.9, 2.4, 3.0); written as " + AxisTuning.Num(snapped) + ".");
                List<string> tokens = Padded(ArrayTokens(profile, device, "steepness"), i => "1") ?? Enumerable.Repeat("1", 8).ToList();
                if (SetToken(tokens, axis, snapped))
                    profile.SetJoystickValue(device, "steepness", RenderArray(tokens), new[] { "deadZone", "sensitivity", "isXInput", "offset", "name", "guid" },
                        label + " steepness " + tokens[axis]);
            }
        }

        protected override void WriteDeviceSettings(ArmaProfile profile, Guid device, Dictionary<string, double> values, string label, GameIoResult result)
        {
            double high;
            double low;
            bool hasHigh = values.TryGetValue("analogClickHigh", out high);
            bool hasLow = values.TryGetValue("analogClickLow", out low);
            if (!hasHigh && !hasLow)
                return;
            ArmaJoystick joystick = profile.FindJoystick(device);
            string raw;
            double current;
            double effectiveHigh = hasHigh ? high : (joystick.Values.TryGetValue("analogClickHigh", out raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out current) ? current : 0.4);
            double effectiveLow = hasLow ? low : (joystick.Values.TryGetValue("analogClickLow", out raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out current) ? current : 0.2);
            if (effectiveLow > effectiveHigh)
            {
                result.Warnings.Add(label + ": analog click release threshold " + AxisTuning.Num(effectiveLow) + " is above the press threshold " + AxisTuning.Num(effectiveHigh) + "; not written.");
                return;
            }
            if (hasHigh)
                SetFloat(profile, device, "analogClickHigh", high, new[] { "mode", "steepness", "deadZone", "sensitivity", "isXInput" }, label + " analogClickHigh");
            if (hasLow)
                SetFloat(profile, device, "analogClickLow", low, new[] { "analogClickHigh", "mode", "steepness", "deadZone", "sensitivity", "isXInput" }, label + " analogClickLow");
        }

        private static void SetFloat(ArmaProfile profile, Guid device, string key, double value, string[] after, string label)
        {
            ArmaJoystick joystick = profile.FindJoystick(device);
            string raw;
            double current;
            if (joystick.Values.TryGetValue(key, out raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out current) && (float)current == (float)value)
                return;
            string text = ArmaProfile.FormatFloat(value);
            profile.SetJoystickValue(device, key, text, after, label + " = " + text);
        }

        protected override void WriteGameSettings(ArmaProfile profile, Dictionary<string, double> values, GameIoResult result)
        {
            foreach (string key in GameOptionKeys)
            {
                double value;
                if (!values.TryGetValue(key, out value))
                    continue;
                string text = value != 0 ? "1" : "0";
                if (profile.TopLevelValue(key) == text)
                    continue;
                profile.SetTopLevel(key, text, GameOptionKeys, key + " = " + text);
            }
        }

        // The game options as WriteGameSettings writes them: top-level "key=0/1;" lines, any other number counts as on.
        protected override void ReadGameSettings(ArmaProfile profile, Dictionary<string, double> values)
        {
            foreach (string key in GameOptionKeys)
            {
                double value;
                string text = profile.TopLevelValue(key);
                if (text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    values[key] = value != 0 ? 1 : 0;
            }
        }
    }
}
