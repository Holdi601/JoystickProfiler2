using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JoyPro.Engine
{
    // What "binds extra ..." prints.
    public sealed class EngineExtraBindsResult
    {
        public string Relation { get; set; }
        public bool IsAxis { get; set; }
        // Is the experimental setting on for this run (--multi-bind on)?
        public bool Enabled { get; set; }
        public EngineBindDto Main { get; set; }
        public List<EngineBindDto> Extras { get; set; }
        public string Message { get; set; }
        public List<string> Warnings { get; set; }

        public EngineExtraBindsResult()
        {
            Relation = string.Empty;
            Extras = new List<EngineBindDto>();
            Message = string.Empty;
            Warnings = new List<string>();
        }
    }

    // engine binds extra ...: the extra binds of a relation (experimental multi-bind, docs/multi-bind.md).
    //   binds extra list <relation>
    //   binds extra add <relation> --device <d> --input <i> [--format dcs|il2] [--modifier <m>]... [--copy-tuning true|false] [tuning options]
    //   binds extra set <relation> <n> [--device <d>] [--input <i>] [--modifier <m>]... [--clear-modifiers] [tuning options]
    //   binds extra remove <relation> <n>|--all
    //   binds extra move <relation> <n> --to <m>             (0 = main bind: the two swap)
    //   binds extra move <relation> <n> --to-relation <name>  (the input becomes the other relation's bind, or another of its inputs)
    // Inputs are numbered as the pages show them: 1 = the main bind, 2.. = the extra binds (<n> of an extra bind is 2 or more).
    // Changing commands need the setting on (--multi-bind on); list and remove work always, so extra binds of a
    // profile can be seen and taken out with the setting off.
    internal static partial class Program
    {
        private static int RunBindsExtra(List<string> command, string outPath, bool textOutput)
        {
            string action = command.Count > 1 ? command[1].ToLowerInvariant() : "list";
            List<string> args = command.Skip(2).ToList();
            if (args.Count < 1)
                return Fail("binds extra " + action + " requires a relation name.");
            string query = args[0];
            args.RemoveAt(0);

            List<string> candidates;
            string relationName;
            if (!TryResolveRelationOrBindName(query, out relationName, out candidates))
                return FailResolution("relation/bind", query, candidates);
            Relation relation;
            if (!InternalDataManagement.AllRelations.TryGetValue(relationName, out relation) || relation == null)
                return Fail("Relation not found: " + relationName);

            if ((action == "add" || action == "set" || action == "move") && !MultiBind.Enabled)
                return Fail("Several inputs per relation is an experimental setting and it is off: switch it on in Settings > Experimental, or pass " + MultiBind.Option + " on.");

            EngineExtraBindsResult result;
            string error;
            switch (action)
            {
                case "list":
                case "show":
                    if (args.Count > 0)
                        return Fail("Unknown binds extra list argument(s): " + string.Join(" ", args));
                    result = ExtraBindsResult(relationName, relation, string.Empty);
                    CommandChangedNothing = true;
                    break;
                case "add":
                    if (!ExtraBindAdd(relationName, relation, args, out result, out error))
                        return Fail(error);
                    break;
                case "set":
                    if (!ExtraBindSet(relationName, relation, args, out result, out error))
                        return Fail(error);
                    break;
                case "remove":
                case "delete":
                    if (!ExtraBindRemove(relationName, relation, args, out result, out error))
                        return Fail(error);
                    break;
                case "move":
                    if (!ExtraBindMove(relationName, relation, args, out result, out error))
                        return Fail(error);
                    break;
                default:
                    return Fail("binds extra: unknown action '" + action + "' (list, add, set, remove, move).");
            }
            EmitPayload(textOutput ? ExtraBindsText(result) : EngineSerialization.ToJson(result), outPath);
            return 0;
        }

        // engine devices copy-to <A> <B> [--mode auto|same|layout] [--map OLD=NEW]... [--grip <device>=<grip id>]...
        //     [--merge skip|add] [--dry-run]
        // Every bind of device A (main and extra binds) gets the matching input of device B as one more input of the same
        // relation: the same functions on both devices (the same grip on two bases). The inputs are matched as "devices
        // replace" matches them (same ids for the same product, else by layout, --map for your own choices); A keeps its
        // binds. An input of B another relation uses is left out unless --merge add. One profile version (one undo).
        private static int RunDevicesCopyTo(List<string> args, string outPath, bool textOutput)
        {
            bool dryRun = PopFlag(args, "--dry-run");
            string mode = (PopOptionValue(args, "--mode") ?? "auto").Trim().ToLowerInvariant();
            string merge = (PopOptionValue(args, "--merge") ?? "skip").Trim().ToLowerInvariant();
            Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string map in PopOptionValues(args, "--map"))
            {
                int split = map.IndexOf('=');
                if (split <= 0)
                    return Fail("--map expects OLD=NEW, e.g. JOY_BTN3=JOY_BTN5 (NEW = none leaves the input out).");
                overrides[map.Substring(0, split).Trim()] = map.Substring(split + 1).Trim();
            }
            Dictionary<string, string> grips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string grip in PopOptionValues(args, "--grip"))
            {
                int split = grip.LastIndexOf('=');
                if (split <= 0)
                    return Fail("--grip expects \"<device or VID:PID>=<grip id>\".");
                grips[grip.Substring(0, split).Trim()] = grip.Substring(split + 1).Trim();
            }
            List<string> positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (positional.Count != args.Count)
                return Fail("Unknown devices copy-to argument(s): " + string.Join(" ", args.Where(a => a.StartsWith("--", StringComparison.Ordinal))));
            if (positional.Count != 2)
                return Fail("devices copy-to requires <device A> <device B> [--mode auto|same|layout] [--map OLD=NEW] [--merge skip|add] [--dry-run].");
            if (mode != "auto" && mode != "same" && mode != "layout")
                return Fail("--mode must be auto, same or layout.");
            if (merge != "skip" && merge != "add")
                return Fail("--merge must be skip or add.");
            if (!dryRun && !MultiBind.Enabled)
                return Fail("Several inputs per relation is an experimental setting and it is off: switch it on in Settings > Experimental, or pass " + MultiBind.Option + " on.");

            List<string> warnings = new List<string>();
            Games.GameDeviceDirectory directory = Games.GameDeviceDirectory.Discover(warnings);
            List<DeviceUse> uses = ProfileDeviceUse();
            string oldToken = positional[0];
            string newToken = positional[1];
            string oldDevice = uses.Select(u => u.Device).FirstOrDefault(d => ReplaceSameInstance(d, oldToken))
                               ?? uses.Select(u => u.Device).FirstOrDefault(d => string.Equals(ReplaceNameOf(d), ReplaceNameOf(oldToken), StringComparison.OrdinalIgnoreCase) && uses.Count(u => ReplaceNameKey(u.Device) == ReplaceNameKey(oldToken)) == 1);
            if (oldDevice == null)
                return Fail("The profile has no binds on '" + oldToken + "'. Devices with binds: " + string.Join(", ", uses.Select(u => u.Device)));
            string newDevice = directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName).FirstOrDefault(d => ReplaceSameInstance(d, newToken))
                               ?? uses.Select(u => u.Device).FirstOrDefault(d => ReplaceSameInstance(d, newToken))
                               ?? directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName).FirstOrDefault(d => string.Equals(ReplaceNameOf(d), ReplaceNameOf(newToken), StringComparison.OrdinalIgnoreCase));
            Guid newGuid;
            if (newDevice == null && TryDeviceGuid(newToken, out newGuid))
                newDevice = CollapseSpaces(newToken);
            if (newDevice == null)
                return Fail("'" + newToken + "' is neither connected nor in the profile. Connected: " + string.Join(", ", directory.Devices.Where(d => d.Connected).Select(d => d.JoyProName)));
            if (ReplaceSameInstance(oldDevice, newDevice))
                return Fail("Device A and device B are the same device: " + oldDevice + ".");

            ReplacePlan plan = PlanDeviceReplace(oldDevice, newDevice, mode, overrides, grips, directory, warnings);
            plan.DryRun = dryRun;
            plan.Merge = merge;
            CopyToDevice(plan, merge, !dryRun);
            if (!plan.Applied)
                CommandChangedNothing = true;
            EmitPayload(textOutput ? ReplaceText(plan).Replace("Device Replace", "Copy to another device") : EngineSerialization.ToJson(plan), outPath);
            return 0;
        }

        // The copies (apply) or what they would be (preview): plan.MovedBinds = inputs added, plan.KeptOnOld = inputs of A
        // without a match on B, plan.RemovedTargetBinds stays 0 (nothing of B is changed).
        private static void CopyToDevice(ReplacePlan plan, string merge, bool apply)
        {
            Dictionary<string, ReplaceMapping> byInput = plan.Mappings.Where(m => m.Mapped).ToDictionary(m => m.OldInput, StringComparer.OrdinalIgnoreCase);
            int copied = 0, unmatched = 0, already = 0, taken = 0;
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).ToList())
            {
                Relation relation = entry.Value;
                Bind main;
                if (relation == null || !InternalDataManagement.AllBinds.TryGetValue(entry.Key, out main) || main == null)
                    continue;
                List<Bind> sources = new List<Bind> { main };
                sources.AddRange(MultiBind.Extras(relation));
                HashSet<string> have = new HashSet<string>(sources.Where(b => MultiBind.IsBound(relation, b)).Select(b => MultiBind.Signature(relation, b)), StringComparer.OrdinalIgnoreCase);
                foreach (Bind source in sources)
                {
                    if (!ReplaceSameInstance(source.Joystick, plan.OldDevice) || BindInput(source).Length == 0)
                        continue;
                    ReplaceMapping m;
                    if (!byInput.TryGetValue(BindInput(source), out m))
                    {
                        unmatched++;
                        continue;
                    }
                    Bind copy = source.CopyAsExtra(relation);
                    copy.Joystick = plan.NewDevice;
                    copy.JoyBackup = plan.NewDevice;
                    string alias;
                    copy.AliasJoystick = InternalDataManagement.JoystickAliases != null && InternalDataManagement.JoystickAliases.TryGetValue(plan.NewDevice, out alias) ? alias ?? string.Empty : string.Empty;
                    if (relation.ISAXIS)
                        copy.JAxis = m.NewInput;
                    else
                        copy.JButton = m.NewInput;
                    for (int i = 0; copy.AllReformers != null && i < copy.AllReformers.Count; i++)
                    {
                        Modifier modifier = Modifier.ReformerToMod(copy.AllReformers[i]);
                        ReplaceMapping mm;
                        if (modifier == null || !ReplaceSameInstance(modifier.device, plan.OldDevice) || !byInput.TryGetValue((modifier.key ?? string.Empty).Trim(), out mm))
                            continue;
                        modifier.device = plan.NewDevice;
                        modifier.key = mm.NewInput;
                        copy.AllReformers[i] = modifier.toReformerString();
                    }
                    if (!have.Add(MultiBind.Signature(relation, copy)))
                    {
                        already++;
                        continue;
                    }
                    List<string> others = m.TargetRelations.Where(r => !string.Equals(r, entry.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (others.Count > 0 && merge != "add")
                    {
                        taken++;
                        plan.Warnings.Add(plan.NewDevice + " " + m.NewInput + " is used by " + string.Join(", ", others.Select(o => "'" + o + "'")) + ": '" + entry.Key + "' was not copied there (--merge add copies it anyway).");
                        continue;
                    }
                    if (apply)
                        MultiBind.Add(relation, copy);
                    copied++;
                }
            }
            plan.MovedBinds = copied;
            plan.KeptOnOld = unmatched;
            if (apply && copied > 0)
            {
                InternalDataManagement.ResyncBindsToMods();
                InternalDataManagement.ResyncRelations();
                InternalDataManagement.RecalcFigures();
                plan.Applied = true;
            }
            plan.Steps.Add((apply ? "Copied " : "Would copy ") + copied + " input(s) of " + plan.OldDevice + " to " + plan.NewDevice + " as more inputs of the same relations" +
                           (unmatched > 0 ? "; " + unmatched + " without a matching input on " + plan.NewDevice : string.Empty) +
                           (already > 0 ? "; " + already + " already there" : string.Empty) +
                           (taken > 0 ? "; " + taken + " left out (input used by another relation)" : string.Empty) + ".");
        }

        private static EngineExtraBindsResult ExtraBindsResult(string relationName, Relation relation, string message)
        {
            EngineExtraBindsResult result = new EngineExtraBindsResult();
            result.Relation = relationName;
            result.IsAxis = relation.ISAXIS;
            result.Enabled = MultiBind.Enabled;
            Bind main;
            InternalDataManagement.AllBinds.TryGetValue(relationName, out main);
            result.Main = main == null ? null : EngineSerialization.ToBindDto(relationName, relation.ISAXIS, main);
            result.Extras = MultiBind.Extras(relation).Select(b => EngineSerialization.ToBindDto(relationName, relation.ISAXIS, b)).ToList();
            result.Message = message ?? string.Empty;
            if (!MultiBind.Enabled && result.Extras.Count > 0)
                result.Warnings.Add("The experimental setting \"Several inputs per relation\" is off: exports, checks and pages use only the main bind.");
            return result;
        }

        private static string ExtraBindsText(EngineExtraBindsResult result)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Message))
                sb.AppendLine(result.Message);
            sb.AppendLine("Relation: " + result.Relation + " (" + (result.IsAxis ? "axis" : "button") + ")");
            sb.AppendLine("  1 (main): " + ExtraBindLine(result.Main, result.IsAxis));
            for (int i = 0; i < result.Extras.Count; i++)
                sb.AppendLine("  " + (i + 2).ToString(CultureInfo.InvariantCulture) + ": " + ExtraBindLine(result.Extras[i], result.IsAxis));
            foreach (string warning in result.Warnings)
                sb.AppendLine("Warning: " + warning);
            return sb.ToString();
        }

        private static string ExtraBindLine(EngineBindDto bind, bool axis)
        {
            if (bind == null)
                return "<no bind>";
            string input = axis ? bind.JAxis : bind.JButton;
            string mods = bind.AllReformers == null || bind.AllReformers.Count == 0 ? "<none>" : string.Join(", ", bind.AllReformers.Select(ModifierNameFromReformer));
            string line = (string.IsNullOrWhiteSpace(bind.Joystick) ? "<no device>" : bind.Joystick) + "::" + (string.IsNullOrWhiteSpace(input) ? "<no input>" : input) + " | modifiers=" + mods;
            if (axis)
                line += " | tuning=" + (bind.Inverted == true ? "inverted, " : string.Empty) + "deadzone " + bind.Deadzone.ToString(CultureInfo.InvariantCulture) +
                        ", saturation " + bind.SaturationX.ToString(CultureInfo.InvariantCulture) + "/" + bind.SaturationY.ToString(CultureInfo.InvariantCulture) +
                        ", curvature " + string.Join(" ", (bind.Curvature ?? new List<double>()).Select(c => c.ToString(CultureInfo.InvariantCulture))) +
                        (bind.TuningOverrides == null || bind.TuningOverrides.Count == 0 ? string.Empty : ", " + bind.TuningOverrides.Count + " scope(s) of its own");
            return line;
        }

        // The extra bind <n> of the relation (n as the pages count: 2 = the first extra bind).
        private static bool TryExtraIndex(Relation relation, string raw, out int index, out string error)
        {
            index = -1;
            error = null;
            int number;
            List<Bind> extras = MultiBind.Extras(relation);
            if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                error = "Name the input by its number (2 = the first extra input; binds extra list shows them).";
                return false;
            }
            if (number < 2 || number > extras.Count + 1)
            {
                error = extras.Count == 0
                    ? "Relation '" + relation.NAME + "' has no extra inputs."
                    : "Input " + number + " does not exist: the extra inputs are 2.." + (extras.Count + 1) + ".";
                return false;
            }
            index = number - 2;
            return true;
        }

        private sealed class ExtraBindOptions
        {
            public string Device;
            public string Input;
            public bool ClearModifiers;
            public List<string> Modifiers = new List<string>();
            public bool? Inverted;
            public bool? Slider;
            public bool? DoubleTap;
            public double? Deadzone;
            public double? SaturationX;
            public double? SaturationY;
            public List<double> Curvature;
            public bool CopyTuning = true;
        }

        private static bool ParseExtraBindOptions(List<string> args, Relation relation, out ExtraBindOptions options, out string error)
        {
            options = new ExtraBindOptions();
            error = null;
            string formatRaw, inputRaw, deadzoneRaw, satXRaw, satYRaw;
            List<string> curvature;
            bool inverted, invertedProvided, slider, sliderProvided, doubleTap, doubleTapProvided, copyTuning, copyTuningProvided;
            try
            {
                options.Device = PopOptionValue(args, "--device");
                inputRaw = PopOptionValue(args, "--input");
                formatRaw = PopOptionValue(args, "--format");
                options.ClearModifiers = PopFlag(args, "--clear-modifiers");
                options.Modifiers.AddRange(PopOptionValues(args, "--modifier"));
                options.Modifiers.AddRange(PopOptionValues(args, "--mod"));
                inverted = PopOptionBoolean(args, "--inverted", false, out invertedProvided);
                slider = PopOptionBoolean(args, "--slider", false, out sliderProvided);
                doubleTap = PopOptionBoolean(args, "--double-tap", false, out doubleTapProvided);
                copyTuning = PopOptionBoolean(args, "--copy-tuning", true, out copyTuningProvided);
                deadzoneRaw = PopOptionValue(args, "--deadzone");
                satXRaw = PopOptionValue(args, "--saturation-x");
                satYRaw = PopOptionValue(args, "--saturation-y");
                curvature = PopOptionValues(args, "--curvature");
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
            if (args.Count > 0)
            {
                error = "Unknown argument(s): " + string.Join(" ", args);
                return false;
            }
            if (!string.IsNullOrWhiteSpace(inputRaw))
            {
                string format = string.IsNullOrWhiteSpace(formatRaw) ? "dcs" : formatRaw.Trim().ToLowerInvariant();
                string normalizeError;
                options.Input = NormalizeInputForFormat(inputRaw, format, out normalizeError);
                if (string.IsNullOrWhiteSpace(options.Input))
                {
                    error = normalizeError;
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(formatRaw))
            {
                error = "--format requires --input.";
                return false;
            }
            options.CopyTuning = copyTuning;
            bool tuning = invertedProvided || sliderProvided || deadzoneRaw != null || satXRaw != null || satYRaw != null || curvature.Count > 0;
            if (tuning && !relation.ISAXIS)
            {
                error = "Axis tuning options (--inverted/--slider/--curvature/--deadzone/--saturation-x/--saturation-y) can only be set on axis relations.";
                return false;
            }
            if (invertedProvided)
                options.Inverted = inverted;
            if (sliderProvided)
                options.Slider = slider;
            if (doubleTapProvided)
                options.DoubleTap = doubleTap;
            double number;
            if (deadzoneRaw != null)
            {
                if (!TryParseInvariantDouble(deadzoneRaw, out number))
                {
                    error = "Invalid --deadzone value '" + deadzoneRaw + "'. Use a number like 0.15.";
                    return false;
                }
                options.Deadzone = number;
            }
            if (satXRaw != null)
            {
                if (!TryParseInvariantDouble(satXRaw, out number))
                {
                    error = "Invalid --saturation-x value '" + satXRaw + "'.";
                    return false;
                }
                options.SaturationX = number;
            }
            if (satYRaw != null)
            {
                if (!TryParseInvariantDouble(satYRaw, out number))
                {
                    error = "Invalid --saturation-y value '" + satYRaw + "'.";
                    return false;
                }
                options.SaturationY = number;
            }
            if (curvature.Count > 0)
            {
                List<double> values;
                if (!TryParseDoubleList(curvature, out values, out error))
                    return false;
                options.Curvature = values.Count == 0 ? new List<double> { 0.0 } : values;
            }
            return true;
        }

        private static void ApplyExtraBindOptions(Relation relation, Bind bind, ExtraBindOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.Device))
            {
                bind.Joystick = options.Device.Trim();
                bind.JoyBackup = bind.Joystick;
                string alias;
                if (InternalDataManagement.JoystickAliases != null && InternalDataManagement.JoystickAliases.TryGetValue(bind.Joystick, out alias))
                    bind.AliasJoystick = alias ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(options.Input))
            {
                if (relation.ISAXIS)
                {
                    bind.JAxis = options.Input;
                    bind.JButton = string.Empty;
                }
                else
                {
                    bind.JButton = options.Input;
                    bind.JAxis = string.Empty;
                }
            }
            if (bind.AllReformers == null)
                bind.AllReformers = new List<string>();
            if (options.Modifiers.Count > 0)
                bind.AllReformers = NormalizeModifierInputs(options.Modifiers);
            else if (options.ClearModifiers)
                bind.AllReformers.Clear();
            if (options.Inverted.HasValue)
                bind.Inverted = options.Inverted.Value;
            if (options.Slider.HasValue)
                bind.Slider = options.Slider.Value;
            if (options.DoubleTap.HasValue)
                bind.doubleTap = options.DoubleTap.Value;
            if (options.Deadzone.HasValue)
                bind.Deadzone = options.Deadzone.Value;
            if (options.SaturationX.HasValue)
                bind.SaturationX = options.SaturationX.Value;
            if (options.SaturationY.HasValue)
                bind.SaturationY = options.SaturationY.Value;
            if (options.Curvature != null)
                bind.Curvature = options.Curvature;
        }

        // Another bind of the profile on the same physical input (device, input, modifier keys).
        private static List<string> ExtraBindCollisions(Relation relation, Bind bind)
        {
            List<string> warnings = new List<string>();
            if (!MultiBind.IsBound(relation, bind))
                return warnings;
            string signature = MultiBind.Signature(relation, bind);
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation other = entry.Value;
                if (other == null || other.ISAXIS != relation.ISAXIS)
                    continue;
                Bind otherMain;
                InternalDataManagement.AllBinds.TryGetValue(entry.Key, out otherMain);
                foreach (Bind candidate in MultiBind.ActiveBinds(other, otherMain))
                {
                    if (ReferenceEquals(candidate, bind) || !MultiBind.IsBound(other, candidate))
                        continue;
                    if (!string.Equals(MultiBind.Signature(other, candidate), signature, StringComparison.OrdinalIgnoreCase))
                        continue;
                    warnings.Add(ReferenceEquals(other, relation)
                        ? "This input is already an input of '" + entry.Key + "'."
                        : "Relation '" + entry.Key + "' uses the same input (" + bind.Joystick + "::" + MultiBind.InputOf(relation, bind) + ").");
                    break;
                }
            }
            return warnings;
        }

        private static void AfterExtraBindChange()
        {
            InternalDataManagement.ResyncBindsToMods();
            InternalDataManagement.ResyncRelations();
            InternalDataManagement.RecalcFigures();
        }

        private static bool ExtraBindAdd(string relationName, Relation relation, List<string> args, out EngineExtraBindsResult result, out string error)
        {
            result = null;
            ExtraBindOptions options;
            if (!ParseExtraBindOptions(args, relation, out options, out error))
                return false;
            if (string.IsNullOrWhiteSpace(options.Device) || string.IsNullOrWhiteSpace(options.Input))
            {
                error = "binds extra add needs --device and --input.";
                return false;
            }
            Bind main;
            InternalDataManagement.AllBinds.TryGetValue(relationName, out main);
            Bind bind = MultiBind.NewExtra(relation);
            // An axis starts with the main bind's tuning (every scope): the same function on another device usually
            // wants the same response; --copy-tuning false starts neutral.
            if (relation.ISAXIS && main != null && options.CopyTuning)
            {
                Bind copy = main.CopyAsExtra(relation);
                bind.Inverted = copy.Inverted;
                bind.Slider = copy.Slider;
                bind.Deadzone = copy.Deadzone;
                bind.SaturationX = copy.SaturationX;
                bind.SaturationY = copy.SaturationY;
                bind.Curvature = copy.Curvature;
                bind.TuningOverrides = copy.TuningOverrides;
            }
            ApplyExtraBindOptions(relation, bind, options);
            if (main == null || !MultiBind.IsBound(relation, main))
            {
                // No main bind yet: this input becomes the main bind (a relation has extra binds only next to one).
                bind.Rl = relation;
                relation.bind = bind;
                InternalDataManagement.AllBinds[relationName] = bind;
                AfterExtraBindChange();
                result = ExtraBindsResult(relationName, relation, "Relation '" + relationName + "' had no input: this is its main input now.");
                result.Warnings.AddRange(ExtraBindCollisions(relation, bind));
                return true;
            }
            string signature = MultiBind.Signature(relation, bind);
            if (new[] { main }.Concat(MultiBind.Extras(relation)).Any(b => string.Equals(MultiBind.Signature(relation, b), signature, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Relation '" + relationName + "' already has this input (" + bind.Joystick + "::" + MultiBind.InputOf(relation, bind) + ").";
                return false;
            }
            MultiBind.Add(relation, bind);
            AfterExtraBindChange();
            result = ExtraBindsResult(relationName, relation, "Added input " + (MultiBind.Extras(relation).Count + 1) + " to '" + relationName + "': " + bind.Joystick + "::" + MultiBind.InputOf(relation, bind) + ".");
            result.Warnings.AddRange(ExtraBindCollisions(relation, bind));
            return true;
        }

        private static bool ExtraBindSet(string relationName, Relation relation, List<string> args, out EngineExtraBindsResult result, out string error)
        {
            result = null;
            if (args.Count < 1)
            {
                error = "binds extra set <relation> <n> [options]";
                return false;
            }
            int index;
            if (!TryExtraIndex(relation, args[0], out index, out error))
                return false;
            args.RemoveAt(0);
            ExtraBindOptions options;
            if (!ParseExtraBindOptions(args, relation, out options, out error))
                return false;
            Bind bind = MultiBind.Extras(relation)[index];
            ApplyExtraBindOptions(relation, bind, options);
            AfterExtraBindChange();
            result = ExtraBindsResult(relationName, relation, "Changed input " + (index + 2) + " of '" + relationName + "'.");
            result.Warnings.AddRange(ExtraBindCollisions(relation, bind));
            return true;
        }

        private static bool ExtraBindRemove(string relationName, Relation relation, List<string> args, out EngineExtraBindsResult result, out string error)
        {
            result = null;
            error = null;
            bool all = PopFlag(args, "--all");
            if (all)
            {
                if (args.Count > 0)
                {
                    error = "Unknown binds extra remove argument(s): " + string.Join(" ", args);
                    return false;
                }
                int count = MultiBind.Extras(relation).Count;
                relation.ExtraBinds = null;
                if (count == 0)
                    CommandChangedNothing = true;
                AfterExtraBindChange();
                result = ExtraBindsResult(relationName, relation, count == 0 ? "Relation '" + relationName + "' has no extra inputs." : "Removed " + count + " extra input(s) of '" + relationName + "'.");
                return true;
            }
            if (args.Count != 1)
            {
                error = "binds extra remove <relation> <n>|--all";
                return false;
            }
            int index;
            if (!TryExtraIndex(relation, args[0], out index, out error))
                return false;
            Bind removed = MultiBind.Extras(relation)[index];
            relation.ExtraBinds.Remove(removed);
            AfterExtraBindChange();
            result = ExtraBindsResult(relationName, relation, "Removed input " + (index + 2) + " of '" + relationName + "' (" + removed.Joystick + "::" + MultiBind.InputOf(relation, removed) + ").");
            return true;
        }

        private static bool ExtraBindMove(string relationName, Relation relation, List<string> args, out EngineExtraBindsResult result, out string error)
        {
            result = null;
            string toRaw = PopOptionValue(args, "--to");
            string toRelationRaw = PopOptionValue(args, "--to-relation");
            if (args.Count != 1 || (toRaw == null) == (toRelationRaw == null))
            {
                error = "binds extra move <relation> <n> --to <m> | --to-relation <name>";
                return false;
            }
            int index;
            if (!TryExtraIndex(relation, args[0], out index, out error))
                return false;
            Bind moving = MultiBind.Extras(relation)[index];
            if (toRelationRaw != null)
            {
                List<string> candidates;
                string targetName;
                if (!TryResolveRelationName(toRelationRaw, out targetName, out candidates))
                {
                    error = "Relation not found: " + toRelationRaw + (candidates.Count > 0 ? " (did you mean: " + string.Join(", ", candidates.Take(5)) + ")" : string.Empty);
                    return false;
                }
                Relation target = InternalDataManagement.AllRelations[targetName];
                if (ReferenceEquals(target, relation))
                {
                    error = "Use --to <m> to move an input within its relation.";
                    return false;
                }
                if (target.ISAXIS != relation.ISAXIS)
                {
                    error = "'" + targetName + "' is " + (target.ISAXIS ? "an axis" : "a button") + " relation; the input is " + (relation.ISAXIS ? "an axis" : "a button") + ".";
                    return false;
                }
                relation.ExtraBinds.Remove(moving);
                Bind targetMain;
                InternalDataManagement.AllBinds.TryGetValue(targetName, out targetMain);
                string message;
                if (targetMain == null || !MultiBind.IsBound(target, targetMain))
                {
                    Bind main = moving.CopyAsExtra(target);
                    target.bind = main;
                    InternalDataManagement.AllBinds[targetName] = main;
                    message = "Moved input " + (index + 2) + " of '" + relationName + "' to '" + targetName + "' as its input.";
                }
                else
                {
                    MultiBind.Add(target, moving);
                    message = "Moved input " + (index + 2) + " of '" + relationName + "' to '" + targetName + "' as input " + (MultiBind.Extras(target).Count + 1) + ".";
                }
                AfterExtraBindChange();
                result = ExtraBindsResult(relationName, relation, message);
                return true;
            }

            int to;
            if (!int.TryParse(toRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out to) || to < 1 || to > MultiBind.Extras(relation).Count + 1)
            {
                error = "--to takes 1 (the main input) to " + (MultiBind.Extras(relation).Count + 1) + ".";
                return false;
            }
            if (to == index + 2)
            {
                CommandChangedNothing = true;
                result = ExtraBindsResult(relationName, relation, "Input " + to + " is already there.");
                return true;
            }
            if (to == 1)
            {
                // The extra input becomes the main bind; the main bind takes its place among the extra inputs.
                Bind main;
                InternalDataManagement.AllBinds.TryGetValue(relationName, out main);
                Bind newMain = moving.CopyAsExtra(relation);
                relation.ExtraBinds[relation.ExtraBinds.IndexOf(moving)] = main == null ? null : main.CopyAsExtra(relation);
                relation.bind = newMain;
                newMain.Rl = relation;
                InternalDataManagement.AllBinds[relationName] = newMain;
            }
            else
            {
                relation.ExtraBinds.Remove(moving);
                relation.ExtraBinds.Insert(to - 2, moving);
            }
            AfterExtraBindChange();
            result = ExtraBindsResult(relationName, relation, "Moved input " + (index + 2) + " of '" + relationName + "' to place " + to + ".");
            return true;
        }
    }
}
