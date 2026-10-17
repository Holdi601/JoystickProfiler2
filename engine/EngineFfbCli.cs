using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.Engine.Axis;

namespace JoyPro.Engine
{
    // engine ffb ...: force feedback settings of the games per device and scope (docs/ffb.md).
    //   ffb models                                                   games with settings and their parameters
    //   ffb show [--device D] [--game G] [--plane P]                 stored settings; with --game, what the device gets
    //   ffb set --device D|* --game G [--plane P] --param k=v ...    a device's settings for a game (or aircraft)
    //   ffb clear --device D|* --game G [--plane P] [--param k]...   drops the whole scope, or single values
    internal static partial class Program
    {
        private static int RunFfb(List<string> command, string outPath, bool textOutput)
        {
            if (command.Count > 0 && string.Equals(command[0], "telemetry", StringComparison.OrdinalIgnoreCase))
                return RunFfbTelemetry(command.Skip(1).ToList(), outPath, textOutput);
            string action = command.Count == 0 ? "models" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            string device;
            string game;
            string plane;
            List<string> paramOptions;
            try
            {
                device = PopOptionValue(args, "--device");
                game = PopOptionValue(args, "--game");
                plane = PopOptionValue(args, "--plane");
                paramOptions = PopOptionValues(args, "--param");
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
            if (args.Count > 0)
                return Fail("Unknown ffb argument(s): " + string.Join(" ", args));
            if (InternalDataManagement.FfbOverrides == null)
                InternalDataManagement.FfbOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            switch (action)
            {
                case "models":
                    return EmitFfbModels(outPath, textOutput);
                case "show":
                case "list":
                    return EmitFfbShow(device, game, plane, outPath, textOutput);
                case "set":
                case "clear":
                    break;
                default:
                    return Fail("ffb: unknown action '" + action + "' (models, show, set, clear).");
            }

            if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(game))
                return Fail("ffb " + action + " requires --device <device or *> and --game <game>.");
            string gameId = GameInstalls.Resolve(game);
            if (gameId.Length == 0)
                gameId = game.Trim();
            FfbModel model = FfbSettings.ForGame(gameId);
            if (model == null)
                return Fail("JoyPro manages no force feedback settings for '" + game + "'. See: engine ffb models.");
            string scope = string.IsNullOrWhiteSpace(plane) ? BindTuning.GameScope(gameId) : BindTuning.PlaneScope(gameId, plane);
            string key = FfbSettings.Key(device, scope);
            Dictionary<string, double> values;
            string stored;
            values = InternalDataManagement.FfbOverrides.TryGetValue(key, out stored) ? FfbSettings.Parse(stored) : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (action == "clear")
            {
                if (paramOptions.Count == 0)
                    values.Clear();
                foreach (string name in paramOptions)
                    values.Remove(name.Trim());
            }
            else
            {
                if (paramOptions.Count == 0)
                    return Fail("ffb set needs at least one --param <name>=<value>.");
                foreach (string option in paramOptions)
                {
                    int eq = option.IndexOf('=');
                    string raw = eq > 0 ? option.Substring(eq + 1).Trim() : string.Empty;
                    string name = eq > 0 ? option.Substring(0, eq).Trim() : option.Trim();
                    FfbParameter parameter = model.Parameters.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                    if (parameter == null)
                        return Fail("Unknown " + model.Title + " force feedback setting '" + name + "' (" + string.Join(", ", model.Parameters.Select(p => p.Key)) + ").");
                    if (parameter.GameWide != (device.Trim() == FfbSettings.AnyDevice))
                        return Fail(parameter.Key + (parameter.GameWide ? " is a game-wide setting: use --device *." : " is set per device: --device takes the device name."));
                    double number;
                    if (parameter.Toggle && (raw == "true" || raw == "false"))
                        number = raw == "true" ? 1 : 0;
                    else if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        return Fail("--param " + name + " needs a value (" + (parameter.Toggle ? "true/false" : AxisTuning.Num(parameter.Min) + ".." + AxisTuning.Num(parameter.Max)) + ").");
                    if (!parameter.Toggle && (number < parameter.Min || number > parameter.Max))
                        return Fail(parameter.Key + " must be " + AxisTuning.Num(parameter.Min) + ".." + AxisTuning.Num(parameter.Max) + ".");
                    values[parameter.Key] = parameter.Toggle ? (number != 0 ? 1 : 0) : number;
                }
            }

            if (values.Count == 0)
                InternalDataManagement.FfbOverrides.Remove(key);
            else
                InternalDataManagement.FfbOverrides[key] = FfbSettings.Format(values);
            string message = (values.Count == 0 ? "Cleared " : "Set ") + key + (values.Count == 0 ? "." : ": " + FfbSettings.Format(values));
            EmitPayload(textOutput ? "Force feedback: " + message : EngineSerialization.ToJson(new { Key = key, Values = values, Message = message }), outPath);
            return 0;
        }

        private static int EmitFfbModels(string outPath, bool textOutput)
        {
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new
                {
                    Models = FfbSettings.All.Select(m => new { m.Game, m.Title, m.Description, Parameters = m.Parameters.Select(p => new { p.Key, p.Label, p.Toggle, p.Min, p.Max, p.Default, p.GameWide, p.Help }).ToList() }).ToList()
                }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            foreach (FfbModel model in FfbSettings.All)
            {
                sb.AppendLine(model.Game + " - " + model.Title);
                sb.AppendLine("  " + model.Description);
                foreach (FfbParameter p in model.Parameters)
                    sb.AppendLine("  - " + p.Key + " (" + p.Label + ", " + (p.Toggle ? "true/false" : AxisTuning.Num(p.Min) + ".." + AxisTuning.Num(p.Max)) + ", default " + AxisTuning.Num(p.Default) + (p.GameWide ? ", game-wide: --device *" : string.Empty) + ")");
            }
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        private static int EmitFfbShow(string device, string game, string plane, string outPath, bool textOutput)
        {
            Dictionary<string, string> store = InternalDataManagement.FfbOverrides;
            List<KeyValuePair<string, string>> entries = store
                .Where(e => string.IsNullOrWhiteSpace(device) || e.Key.StartsWith(device.Trim() + "|", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string gameId = string.IsNullOrWhiteSpace(game) ? string.Empty : (GameInstalls.Resolve(game).Length > 0 ? GameInstalls.Resolve(game) : game.Trim());
            Dictionary<string, double> resolved = gameId.Length > 0 && !string.IsNullOrWhiteSpace(device) ? FfbSettings.Resolve(store, device, gameId, plane) : null;
            if (!textOutput)
            {
                EmitPayload(EngineSerialization.ToJson(new { Entries = entries.ToDictionary(e => e.Key, e => e.Value), Game = gameId, Plane = plane, Resolved = resolved }), outPath);
                return 0;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Force feedback settings: " + entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, string> entry in entries)
                sb.AppendLine("  " + entry.Key + ": " + entry.Value);
            if (resolved != null)
                sb.AppendLine(device + " in " + gameId + (string.IsNullOrWhiteSpace(plane) ? string.Empty : " / " + plane) + ": " + (resolved.Count == 0 ? "(game file values)" : FfbSettings.Format(resolved)));
            EmitPayload(sb.ToString(), outPath);
            return 0;
        }

        // engine ffb telemetry ...: JoyPro's own telemetry force feedback (docs/ffb-telemetry.md, EngineFfbTelemetry.cs).
        private static int RunFfbTelemetry(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count == 0 ? "status" : command[0].ToLowerInvariant();
            List<string> args = command.Skip(1).ToList();
            List<string> warnings = new List<string>();
            try
            {
                switch (action)
                {
                    case "status":
                    {
                        if (args.Count > 0)
                            return Fail("Unknown ffb telemetry status argument(s): " + string.Join(" ", args));
                        EngineFfbTelemetryStatus status = EngineFfbTelemetry.Status(warnings);
                        EmitPayload(textOutput ? EngineFfbTelemetry.ToText(status) : EngineSerialization.ToJson(status), outPath);
                        return 0;
                    }
                    case "effects":
                    {
                        if (textOutput)
                            EmitPayload(string.Join(Environment.NewLine, EngineFfbTelemetry.Effects.Select(e => e[0] + " - " + e[1])), outPath);
                        else
                            EmitPayload(EngineSerialization.ToJson(new { Effects = EngineFfbTelemetry.Effects.Select(e => new { Name = e[0], Title = e[1] }).ToList() }), outPath);
                        return 0;
                    }
                    case "config":
                        return RunFfbTelemetryConfig(args, outPath, textOutput, warnings);
                    case "setup":
                    {
                        string game = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
                        if (game.Length > 0)
                            args.RemoveAt(0);
                        bool remove = PopFlag(args, "--remove");
                        bool dryRun = PopFlag(args, "--dry-run");
                        string path = PopOptionValue(args, "--path");
                        if (args.Count > 0)
                            return Fail("Unknown ffb telemetry setup argument(s): " + string.Join(" ", args));
                        EngineFfbTelemetry.GameSpec spec = EngineFfbTelemetry.FindGame(game);
                        if (spec == null)
                            return Fail("engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path <folder>]");
                        if (!dryRun && spec.Id != "WarThunder" && Games.GameProcessGuard.FindRunning(spec.ProcessNames).Count > 0)
                            return Fail("Close " + spec.Title + " first: it reads " + (spec.Id == "DCS" ? "its Hooks" : "startup.cfg") + " when it starts.");
                        GameBackups.Begin(spec.Id, "engine ffb telemetry setup " + spec.Id + (remove ? " --remove" : string.Empty), dryRun);
                        EngineFfbTelemetrySetupResult result = EngineFfbTelemetry.Setup(spec.Id, remove, false, string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path), warnings, dryRun);
                        result.Warnings.InsertRange(0, warnings);
                        EmitPayload(textOutput ? EngineFfbTelemetry.ToText(result) : EngineSerialization.ToJson(result), outPath);
                        return result.Errors.Count > 0 ? Fail(string.Join(" | ", result.Errors)) : 0;
                    }
                    default:
                        return Fail("engine ffb telemetry status|config show|config set|config clear|setup|effects");
                }
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
        }

        private static int RunFfbTelemetryConfig(List<string> args, string outPath, bool textOutput, List<string> warnings)
        {
            string action = args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "show";
            if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                args.RemoveAt(0);
            string game = PopOptionValue(args, "--game");
            string plane = PopOptionValue(args, "--plane");
            string effect = PopOptionValue(args, "--effect");
            string device = PopOptionValue(args, "--device");
            List<string> parameters = PopOptionValues(args, "--param");
            if (args.Count > 0)
                return Fail("Unknown ffb telemetry config argument(s): " + string.Join(" ", args));
            Dictionary<string, object> config = EngineFfbTelemetry.LoadConfig(warnings);
            List<string> changes = new List<string>();
            string error;
            switch (action)
            {
                case "show":
                    if (textOutput)
                        EmitPayload(EngineFfbTelemetry.IndentJson(EngineSerialization.ToJson(config)), outPath);
                    else
                        EmitPayload(EngineSerialization.ToJson(new { ConfigPath = EngineFfbTelemetry.ConfigPath(), Config = config, Warnings = warnings }), outPath);
                    return 0;
                case "set":
                    error = EngineFfbTelemetry.Set(config, game, plane, effect, device, parameters, changes, warnings);
                    break;
                case "clear":
                case "reset":
                    if (parameters.Count > 0)
                        return Fail("config clear takes no --param; it resets the whole scope.");
                    error = string.IsNullOrWhiteSpace(device)
                        ? EngineFfbTelemetry.Clear(config, game, plane, effect, changes)
                        : EngineFfbTelemetry.ClearDevice(config, device, changes);
                    break;
                default:
                    return Fail("engine ffb telemetry config show|set|clear");
            }
            if (error != null)
                return Fail(error);
            EngineFfbTelemetry.SaveConfig(config);
            if (textOutput)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Telemetry force feedback settings: " + EngineFfbTelemetry.ConfigPath());
                foreach (string change in changes)
                    sb.AppendLine("  * " + change);
                if (changes.Count == 0)
                    sb.AppendLine("  (nothing changed)");
                foreach (string warning in warnings)
                    sb.AppendLine("  warning: " + warning);
                EmitPayload(sb.ToString().TrimEnd(), outPath);
            }
            else
            {
                EmitPayload(EngineSerialization.ToJson(new { ConfigPath = EngineFfbTelemetry.ConfigPath(), Changes = changes, Warnings = warnings, Config = config }), outPath);
            }
            return 0;
        }
    }
}
