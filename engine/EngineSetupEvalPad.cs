using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.DeviceReference;
using JoyPro.Engine.Games;
using JoyPro.Setup;

namespace JoyPro.Engine
{
    // The pad persona (docs/auto-setup-eval.md "ACE COMBAT 8 player"): someone who flies only ACE COMBAT 8 with a HOTAS
    // and takes the pad path at the first start. The Setup wizard alone (its defaults, the game alone, no Auto Group, no
    // Fill all) must put every flight control on the pad: pitch and roll on the left stick, the throttle split on both
    // triggers, yaw on LB / RB from the twist or the pedals, gun, missile and target switch on the grip, the flare chord,
    // the High-G hold and the wingman D-pad. Scored with the pad gates (PadScore: engine vjoy pad-check on the profile).
    internal static partial class EngineSetupEval
    {
        internal static readonly string[] PadPersonaGames = { "AceCombat8" };

        internal static List<EvalSetup> PadPersonaSetups()
        {
            List<EvalSetup> list = new List<EvalSetup>
            {
                S("ac8-tflight", "Thrustmaster T.Flight HOTAS 4 (stick and throttle in one, twist on the stick)", "thrustmaster-tflight-hotas-4"),
                S("ac8-vkb-virpil", "VKB Gunfighter MCG Pro + Virpil MongoosT-50CM throttle + MFG Crosswind pedals", "vkb-gunfighter-mcg-pro", "virpil-mongoost-50cm-throttle", "mfg-crosswind-v2")
            };
            foreach (EvalSetup setup in list)
                setup.Kind = "pad-persona";
            return list;
        }

        // One profile per setup and pad game, made by the wizard alone (fresh profile, setup apply), then scored.
        internal static EvalResult RunPad(List<EvalSetup> setups, List<string> games, string workFolder, Action<string> progress)
        {
            EvalResult result = new EvalResult { Setups = setups, Date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            result.Targets = games.Select(g => new EvalTarget { Id = g, Title = g + " (virtual Xbox pad, wizard alone)", Game = g, Aircraft = g, Pad = true }).ToList();
            System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
            string previousFake = Environment.GetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable);
            Directory.CreateDirectory(workFolder);
            try
            {
                foreach (EvalSetup setup in setups)
                {
                    List<string> warnings = new List<string>();
                    EngineSetupWizard.DeviceChoices choices;
                    string fake = WriteFakeDevices(setup, workFolder, warnings, out choices);
                    Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, fake);
                    List<SetupInput> inputs;
                    List<SetupDevice> devices = EngineSetupWizard.Devices(choices, warnings, out inputs);
                    result.Messages[setup.Id] = SetupRoles.Rules(devices);
                    result.Roles[setup.Id] = devices.Select(d => SetupRoles.Short(d) + " = " + d.Role + (d.Generic ? " (not recognised: generic layout)" : string.Empty) +
                                                                 " [" + inputs.Count(i => i.Device == d.Device).ToString(CultureInfo.InvariantCulture) + " inputs]").ToList();
                    result.Warnings.AddRange(warnings.Distinct().Select(w => setup.Id + ": " + w));
                    progress?.Invoke("Pad persona " + setup.Id + ": " + string.Join(", ", result.Roles[setup.Id]));
                    foreach (EvalTarget target in result.Targets)
                    {
                        EvalRow row = new EvalRow { Setup = setup.Id, SetupKind = setup.Kind, Target = target.Id, Stage = "setup", Class = SetupClasses.Fixed };
                        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            InternalDataManagement.NewFile(true);
                            List<SetupDevice> copies = devices.Select(Copy).ToList();
                            // The wizard's defaults (the pad tour's "Set my devices automatically").
                            SetupOptions options = new SetupOptions { Games = new List<string> { target.Game }, Modifiers = 0, PreferFewer = true, Realism = RealismValue, HeadTracking = HeadTrackingValue, PhaseLayers = "auto", Seed = 1 };
                            SetupRequest request = EngineSetupWizard.Request(copies, inputs, options, null, new List<string>());
                            SetupPlan plan = SetupPlanner.Plan(request);
                            EngineSetupWizard.FlightAxesGuard(plan, request);
                            SetupQuality quality = plan.Quality.FirstOrDefault(q => string.Equals(q.Aircraft, target.Aircraft, StringComparison.OrdinalIgnoreCase));
                            if (quality != null)
                            {
                                row.Quality = quality.Score;
                                row.Placed = quality.Placed;
                                row.Total = quality.Total;
                            }
                            EngineSetupWizard.Apply(plan, false, true);
                            Resync();
                            PadScore(row, target, plan);
                        }
                        catch (Exception ex)
                        {
                            row.Error = ex.GetType().Name + ": " + ex.Message;
                        }
                        row.Millis = watch.ElapsedMilliseconds;
                        result.Rows.Add(row);
                    }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(GameDeviceDirectory.FakeDevicesEnvironmentVariable, previousFake);
                InternalDataManagement.NewFile(true);
            }
            result.Millis = total.ElapsedMilliseconds;
            return result;
        }

        // Every gate of every row must pass (the pad persona has no thresholds: a missing flight control is a bug).
        internal static List<string> PadPersonaFailures(EvalResult result)
        {
            List<string> failures = new List<string>();
            foreach (EvalRow row in result.Rows)
            {
                if (row.Error.Length > 0)
                    failures.Add(row.Setup + " x " + row.Target + ": " + row.Error);
                foreach (EvalGate gate in row.Gates.Where(g => g.Status == "fail"))
                    failures.Add(row.Setup + " x " + row.Target + ": " + gate.Gate + " (" + gate.Detail + ")");
            }
            return failures;
        }

        internal static string PadPersonaMarkdown(EvalResult result)
        {
            StringBuilder sb = new StringBuilder();
            List<string> failures = PadPersonaFailures(result);
            sb.AppendLine("# Pad persona: ACE COMBAT 8 with the Setup wizard alone");
            sb.AppendLine();
            sb.AppendLine("Date " + result.Date + ", " + (failures.Count == 0 ? "passed" : "failed: " + string.Join("; ", failures)) + ".");
            sb.AppendLine();
            foreach (EvalSetup setup in result.Setups)
            {
                sb.AppendLine("## " + setup.Name + " (`" + setup.Id + "`)");
                sb.AppendLine();
                sb.AppendLine("Roles: " + string.Join("; ", result.Roles.ContainsKey(setup.Id) ? result.Roles[setup.Id] : new List<string>()));
                sb.AppendLine();
                foreach (EvalRow row in result.Rows.Where(r => r.Setup == setup.Id))
                {
                    sb.AppendLine("### " + row.Target + ": " + row.Done.ToString("0", CultureInfo.InvariantCulture) + "/" + row.Required.ToString(CultureInfo.InvariantCulture) + " gates, wizard quality " +
                                  row.Quality.ToString("0.0", CultureInfo.InvariantCulture) + " % (" + row.Placed.ToString(CultureInfo.InvariantCulture) + "/" + row.Total.ToString(CultureInfo.InvariantCulture) + " placed)" +
                                  (row.Error.Length > 0 ? ", error: " + row.Error : string.Empty));
                    sb.AppendLine();
                    sb.AppendLine("| Gate | Status | Where |");
                    sb.AppendLine("|---|---|---|");
                    foreach (EvalGate gate in row.Gates)
                        sb.AppendLine("| " + gate.Gate + " | " + gate.Status + " | " + gate.Detail.Replace("|", "/") + " |");
                    sb.AppendLine();
                    sb.AppendLine("Pad actions bound:");
                    foreach (string binding in row.Bindings.OrderBy(b => b, StringComparer.Ordinal))
                        sb.AppendLine("- " + binding);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }
    }

    internal static partial class Program
    {
        // engine setup eval-pad [--out-dir <folder>]: the pad persona (EngineSetupEval.RunPad); exit 1 when a gate fails.
        private static int RunSetupEvalPad(List<string> args, string outPath, bool textOutput)
        {
            string outDir = PopOptionValue(args, "--out-dir");
            if (args.Count > 0)
                return Fail("Unknown setup eval-pad argument(s): " + string.Join(" ", args));
            string folder = NormalizePath(string.IsNullOrWhiteSpace(outDir) ? Path.Combine(Path.GetTempPath(), "joypro-pad-persona-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)) : outDir);
            Directory.CreateDirectory(folder);
            EngineSetupEval.EvalResult result = EngineSetupEval.RunPad(EngineSetupEval.PadPersonaSetups(), EngineSetupEval.PadPersonaGames.ToList(), folder, line => { if (textOutput) Console.Error.WriteLine(line); });
            List<string> failures = EngineSetupEval.PadPersonaFailures(result);
            File.WriteAllText(Path.Combine(folder, "pad-persona.md"), EngineSetupEval.PadPersonaMarkdown(result), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "pad-persona.json"), MiniJson.Write(EngineSetupEval.ToJson(result), true), new UTF8Encoding(false));
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Pad persona: " + result.Setups.Count.ToString(CultureInfo.InvariantCulture) + " setups x " + result.Targets.Count.ToString(CultureInfo.InvariantCulture) + " pad games, wizard alone");
                foreach (EngineSetupEval.EvalRow row in result.Rows)
                {
                    sb.AppendLine("  " + row.Setup + " x " + row.Target + ": " + row.Done.ToString("0", CultureInfo.InvariantCulture) + "/" + row.Required.ToString(CultureInfo.InvariantCulture) + " gates" +
                                  (row.Error.Length > 0 ? ", error: " + row.Error : string.Empty));
                    foreach (EngineSetupEval.EvalGate gate in row.Gates)
                        sb.AppendLine("    " + (gate.Status == "ok" ? "ok  " : "MISS") + " " + gate.Gate + ": " + gate.Detail);
                }
                sb.AppendLine("Report: " + Path.Combine(folder, "pad-persona.md"));
                foreach (string failure in failures)
                    sb.AppendLine("Failed: " + failure);
                if (failures.Count == 0)
                    sb.AppendLine("Pad persona: passed");
                EmitPayload(sb.ToString(), outPath);
            }
            else
            {
                EmitPayload(MiniJson.Write(new Dictionary<string, object>
                {
                    { "Report", Path.Combine(folder, "pad-persona.md") }, { "Rows", result.Rows.Count }, { "Failures", failures.Cast<object>().ToList() }
                }, false), outPath);
            }
            return failures.Count == 0 ? 0 : 1;
        }
    }
}
