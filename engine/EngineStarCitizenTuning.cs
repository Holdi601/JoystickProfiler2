using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using JoyPro.Engine.Axis;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Star Citizen's axis settings in actionmaps.xml (docs/games/starcitizen.md) and its game-wide control settings in
    // attributes.xml:
    //   <options type="joystick" instance="N" Product="..."><flight_move_pitch invert="1" exponent="1.5" sensitivity="1"/>
    //     or <flight_move_roll><nonlinearity_curve><point in="0.1" out="0.05"/>...</nonlinearity_curve></flight_move_roll>
    //   <deviceoptions name="<Product>"><option input="x" deadzone="0.05" saturation="0.95"/></deviceoptions>
    // The option group of an action comes from the catalog (defaultProfile.xml, engine db refresh-starcitizen); its
    // defaults and parents from the joystick option tree stored there. A leaf without its own value takes its parent's
    // (the user's value first, then the game's default at each level).
    internal static partial class EngineStarCitizen
    {
        private const string TuningGame = "StarCitizen";

        // ---- the joystick option tree (catalog) ---------------------------------------------------------------------

        private sealed class ScOptionGroupInfo
        {
            public string Name;
            public string Parent;
            public bool? Invert;
            public double? Exponent;
            public double? Sensitivity;
            public List<double> Curve;
            public bool CurveReset;
            public bool ShowCurve = true;
        }

        private static Dictionary<string, ScOptionGroupInfo> ParseOptionTree(XElement tree)
        {
            Dictionary<string, ScOptionGroupInfo> output = new Dictionary<string, ScOptionGroupInfo>(StringComparer.OrdinalIgnoreCase);
            if (tree == null)
                return output;
            foreach (XElement group in tree.Descendants().Where(e => e.Name.LocalName.Equals("optiongroup", StringComparison.OrdinalIgnoreCase)))
            {
                string name = ((string)group.Attribute("name") ?? string.Empty).Trim();
                if (name.Length == 0 || output.ContainsKey(name))
                    continue;
                ScOptionGroupInfo info = new ScOptionGroupInfo { Name = name };
                XElement parent = group.Parent;
                if (parent != null && parent.Name.LocalName.Equals("optiongroup", StringComparison.OrdinalIgnoreCase))
                    info.Parent = ((string)parent.Attribute("name") ?? string.Empty).Trim();
                string invert = (string)group.Attribute("invert");
                if (invert != null)
                    info.Invert = ParseFlag(invert);
                info.Exponent = ParseNumber((string)group.Attribute("exponent"));
                info.Sensitivity = ParseNumber((string)group.Attribute("sensitivity"));
                XElement curve = group.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("nonlinearity_curve", StringComparison.OrdinalIgnoreCase));
                if (curve != null)
                {
                    info.Curve = CurveToGrid(curve);
                    info.CurveReset = ParseFlag((string)curve.Attribute("reset"));
                }
                // Groups without UIShowCurve (zoom, turret limiter) only offer Invert in the game.
                string showCurve = (string)group.Attribute("UIShowCurve");
                info.ShowCurve = showCurve != null && showCurve.Trim() != "0";
                output[name] = info;
            }
            return output;
        }

        // Used when the catalog has no option tree (a catalog from an older JoyPro): the defaults that are not neutral
        // in SC 4.10's defaultProfile.xml.
        private static Dictionary<string, ScOptionGroupInfo> BuiltInOptionTree()
        {
            Dictionary<string, ScOptionGroupInfo> tree = new Dictionary<string, ScOptionGroupInfo>(StringComparer.OrdinalIgnoreCase);
            tree["flight_view"] = new ScOptionGroupInfo { Name = "flight_view", Exponent = 2.5, CurveReset = true };
            tree["flight_view_pitch"] = new ScOptionGroupInfo { Name = "flight_view_pitch", Parent = "flight_view" };
            tree["flight_view_yaw"] = new ScOptionGroupInfo { Name = "flight_view_yaw", Parent = "flight_view" };
            tree["mgv_move"] = new ScOptionGroupInfo { Name = "mgv_move", Invert = true };
            tree["mining_throttle"] = new ScOptionGroupInfo { Name = "mining_throttle", Invert = true };
            foreach (string invertOnly in new[] { "flight_zoom", "flight_zoom_abs", "turret_limiter_relative", "turret_limiter_absolute", "mgv_zoom", "mgv_zoom_abs" })
                tree[invertOnly] = new ScOptionGroupInfo { Name = invertOnly, ShowCurve = false };
            return tree;
        }

        private static List<string> GroupChain(Dictionary<string, ScOptionGroupInfo> tree, string group)
        {
            List<string> chain = new List<string>();
            string current = group;
            while (!string.IsNullOrWhiteSpace(current) && !chain.Contains(current, StringComparer.OrdinalIgnoreCase) && chain.Count < 32)
            {
                chain.Add(current);
                ScOptionGroupInfo info;
                current = tree.TryGetValue(current, out info) ? info.Parent : null;
            }
            return chain;
        }

        private static bool GroupShowsCurve(Dictionary<string, ScOptionGroupInfo> tree, string group)
        {
            ScOptionGroupInfo info;
            return !tree.TryGetValue(group ?? string.Empty, out info) || info.ShowCurve;
        }

        // ---- values -------------------------------------------------------------------------------------------------

        // What the game uses for one option group (and device axis), in the form JoyPro compares and writes.
        private sealed class ScAxisValues
        {
            public bool Invert;
            public double Exponent = 1.0;
            public List<double> Curve;
            public double Sensitivity = 1.0;
            public double Deadzone;
            public double Saturation = 1.0;

            public static ScAxisValues From(AxisTuning t)
            {
                return new ScAxisValues
                {
                    Invert = t.Inverted,
                    Exponent = Math.Round(StarCitizenAxisModel.Exponent(t), 4),
                    Curve = StarCitizenAxisModel.GameCurve(t) == null ? null : StarCitizenAxisModel.GameCurve(t).Select(v => Math.Round(v, 4)).ToList(),
                    Sensitivity = Math.Round(StarCitizenAxisModel.Sensitivity(t), 4),
                    Deadzone = Math.Round(Math.Max(0, Math.Min(1, t.Deadzone)), 4),
                    Saturation = Math.Round(t.SaturationX <= 0 ? 1.0 : Math.Min(1, t.SaturationX), 4)
                };
            }

            public AxisTuning ToTuning()
            {
                AxisTuning t = new AxisTuning();
                t.Inverted = Invert;
                t.Deadzone = Deadzone;
                t.SaturationX = Saturation;
                if (Curve != null)
                {
                    List<double> points = new List<double> { 0.0 };
                    points.AddRange(Curve);
                    points.Add(1.0);
                    t.Curvature = points;
                }
                else
                    t.Native[StarCitizenAxisModel.ExponentKey] = Exponent;
                if (Math.Abs(Sensitivity - 1.0) > 1e-9)
                    t.Native[StarCitizenAxisModel.SensitivityKey] = Sensitivity;
                return t;
            }

            public string ShapeToken()
            {
                return Curve != null ? "curve=" + string.Join("|", Curve.Select(Fmt)) : "exp=" + Fmt(Exponent);
            }

            public string GroupToken()
            {
                return "inv=" + (Invert ? "1" : "0") + ";" + ShapeToken() + ";sens=" + Fmt(Sensitivity);
            }

            public string DeviceToken()
            {
                return "dz=" + Fmt(Deadzone) + ";sat=" + Fmt(Saturation);
            }

            public string Describe(bool showCurve)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Invert ? "inverted" : "not inverted");
                if (showCurve)
                {
                    sb.Append(Curve != null ? ", curve " + string.Join(" ", Curve.Select(Fmt)) : ", exponent " + Fmt(Exponent));
                    if (Math.Abs(Sensitivity - 1.0) > 1e-9)
                        sb.Append(", sensitivity " + Fmt(Sensitivity));
                }
                return sb.ToString();
            }
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static bool ParseFlag(string raw)
        {
            string value = (raw ?? string.Empty).Trim();
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static double? ParseNumber(string raw)
        {
            double value;
            return raw != null && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? (double?)value : null;
        }

        // A <nonlinearity_curve> as the outputs at in = 0.1 .. 0.9 (straight lines through (0,0), the points and (1,1));
        // null without points.
        private static List<double> CurveToGrid(XElement curve)
        {
            List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
            foreach (XElement point in curve.Elements().Where(e => e.Name.LocalName.Equals("point", StringComparison.OrdinalIgnoreCase)))
            {
                double? x = ParseNumber((string)point.Attribute("in"));
                double? y = ParseNumber((string)point.Attribute("out"));
                if (x.HasValue && y.HasValue && x.Value > 0 && x.Value < 1)
                    points.Add(new KeyValuePair<double, double>(x.Value, Math.Max(0, Math.Min(1, y.Value))));
            }
            if (points.Count == 0)
                return null;
            points = points.OrderBy(p => p.Key).ToList();
            points.Insert(0, new KeyValuePair<double, double>(0, 0));
            points.Add(new KeyValuePair<double, double>(1, 1));
            List<double> grid = new List<double>();
            for (int i = 1; i <= StarCitizenAxisModel.GamePoints; i++)
            {
                double x = i / 10.0;
                int hi = points.FindIndex(p => p.Key >= x - 1e-12);
                if (hi <= 0)
                {
                    grid.Add(Math.Round(points[Math.Max(0, hi)].Value, 4));
                    continue;
                }
                KeyValuePair<double, double> a = points[hi - 1];
                KeyValuePair<double, double> b = points[hi];
                double y = b.Key - a.Key < 1e-12 ? b.Value : a.Value + (b.Value - a.Value) * (x - a.Key) / (b.Key - a.Key);
                grid.Add(Math.Round(y, 4));
            }
            return grid;
        }

        private static XElement UserGroupElement(XElement options, string group)
        {
            return options == null ? null : options.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(group, StringComparison.OrdinalIgnoreCase));
        }

        private static XElement CurveElement(XElement groupElement)
        {
            return groupElement == null ? null : groupElement.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("nonlinearity_curve", StringComparison.OrdinalIgnoreCase));
        }

        // The effective invert/exponent/curve/sensitivity of a group on one joystick instance: at each level of the
        // group's chain (leaf, parent, ...) the user's element first, then the game's default; skipLeaf leaves out the
        // user's own leaf element (= what the leaf inherits).
        private static ScAxisValues EffectiveGroupValues(XElement options, Dictionary<string, ScOptionGroupInfo> tree, string group, bool skipLeaf)
        {
            ScAxisValues values = new ScAxisValues();
            List<string> chain = GroupChain(tree, group);
            bool invertFound = false;
            bool shapeFound = false;
            bool sensFound = false;
            for (int level = 0; level < chain.Count; level++)
            {
                XElement user = level == 0 && skipLeaf ? null : UserGroupElement(options, chain[level]);
                ScOptionGroupInfo info;
                tree.TryGetValue(chain[level], out info);
                if (!invertFound)
                {
                    string raw = user == null ? null : (string)user.Attribute("invert");
                    if (raw != null) { values.Invert = ParseFlag(raw); invertFound = true; }
                    else if (info != null && info.Invert.HasValue) { values.Invert = info.Invert.Value; invertFound = true; }
                }
                if (!shapeFound)
                {
                    XElement curve = CurveElement(user);
                    List<double> grid = curve == null ? null : CurveToGrid(curve);
                    double? exponent = user == null ? null : ParseNumber((string)user.Attribute("exponent"));
                    if (grid != null) { values.Curve = grid; shapeFound = true; }
                    else if (exponent.HasValue) { values.Exponent = Math.Round(exponent.Value, 4); shapeFound = true; }
                    else if (info != null && info.Curve != null) { values.Curve = info.Curve; shapeFound = true; }
                    else if (info != null && info.Exponent.HasValue) { values.Exponent = Math.Round(info.Exponent.Value, 4); shapeFound = true; }
                }
                if (!sensFound)
                {
                    double? sens = user == null ? null : ParseNumber((string)user.Attribute("sensitivity"));
                    if (sens.HasValue) { values.Sensitivity = Math.Round(sens.Value, 4); sensFound = true; }
                    else if (info != null && info.Sensitivity.HasValue) { values.Sensitivity = Math.Round(info.Sensitivity.Value, 4); sensFound = true; }
                }
            }
            return values;
        }

        private static bool UserGroupHasValues(XElement element)
        {
            return element != null && (element.Attribute("invert") != null || element.Attribute("exponent") != null ||
                                        element.Attribute("sensitivity") != null || CurveToGridOrNull(element) != null);
        }

        private static List<double> CurveToGridOrNull(XElement groupElement)
        {
            XElement curve = CurveElement(groupElement);
            return curve == null ? null : CurveToGrid(curve);
        }

        private static IEnumerable<XElement> DeviceOptionsFor(IEnumerable<XElement> deviceOptions, string product)
        {
            string normalized = NormalizeScProductToken(product);
            return deviceOptions.Where(d => normalized.Length > 0 && NormalizeScProductToken((string)d.Attribute("name")) == normalized);
        }

        private static XElement DeviceOptionElement(IEnumerable<XElement> deviceOptions, string product, string axisToken)
        {
            return DeviceOptionsFor(deviceOptions, product)
                .SelectMany(d => d.Elements().Where(e => e.Name.LocalName.Equals("option", StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(o => string.Equals(((string)o.Attribute("input") ?? string.Empty).Trim(), axisToken, StringComparison.OrdinalIgnoreCase));
        }

        private static void ReadDeviceValues(ScAxisValues values, XElement option)
        {
            if (option == null)
                return;
            double? dz = ParseNumber((string)option.Attribute("deadzone"));
            double? sat = ParseNumber((string)option.Attribute("saturation"));
            if (dz.HasValue)
                values.Deadzone = Math.Round(Math.Max(0, Math.Min(1, dz.Value)), 4);
            if (sat.HasValue)
                values.Saturation = Math.Round(sat.Value <= 0 ? 1.0 : Math.Min(1, sat.Value), 4);
        }

        private static bool IsScAxisToken(string token)
        {
            switch ((token ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "x": case "y": case "z": case "rotx": case "roty": case "rotz": case "slider1": case "slider2":
                    return true;
                default:
                    return false;
            }
        }

        // A relation without own axis settings (nothing for Star Citizen, a neutral base) leaves the game's values.
        private static bool HasAxisSettings(Bind bind, ResolvedTuning resolved)
        {
            if (resolved.Source != BindTuning.BaseScope)
                return true;
            AxisTuning b = BindTuning.Base(bind);
            bool linear = b.IsUserCurve
                ? b.Curvature.Select((v, i) => Math.Abs(v - i / 10.0) < 1e-6).All(x => x)
                : (b.Curvature == null || b.Curvature.Count == 0 || Math.Abs(b.Curvature[0]) < 1e-9);
            return b.Inverted || Math.Abs(b.Deadzone) > 1e-9 || Math.Abs(b.SaturationX - 1) > 1e-9 || Math.Abs(b.SaturationY - 1) > 1e-9 || !linear;
        }

        // ---- import ---------------------------------------------------------------------------------------------------

        // The values the game uses for an imported axis rebind (option group of the action on its instance, dead zone
        // and saturation of the device axis); null when it is not a joystick axis.
        private static ScAxisValues ReadImportValues(ScActionFileData data, Dictionary<string, ScOptionGroupInfo> tree, int instance, string optionGroup, string axisToken)
        {
            if (!IsScAxisToken(axisToken))
                return null;
            XElement options;
            data.JoystickOptionElements.TryGetValue(instance, out options);
            ScAxisValues values = string.IsNullOrWhiteSpace(optionGroup)
                ? new ScAxisValues()
                : EffectiveGroupValues(options, tree, optionGroup, false);
            string product = options == null ? string.Empty : (string)options.Attribute("Product") ?? string.Empty;
            ReadDeviceValues(values, DeviceOptionElement(data.DeviceOptions, product, axisToken));
            return values;
        }

        // The file's values become the relation's game:StarCitizen tuning unless what the relation already gives Star
        // Citizen is the same (then nothing changes, e.g. a neutral group on a neutral bind).
        // One relation on several actions (merged by device and input) holds one Star Citizen tuning: the first action
        // in the file sets it, a later one with other values only warns.
        private static bool ApplyImportedAxisValues(Bind bind, ScAxisValues values, string relationName, string nodeKey,
            Dictionary<Bind, KeyValuePair<string, ScAxisValues>> appliedThisImport, List<string> warnings)
        {
            if (bind == null || values == null)
                return false;
            KeyValuePair<string, ScAxisValues> earlier;
            if (appliedThisImport.TryGetValue(bind, out earlier))
            {
                if (earlier.Value.GroupToken() != values.GroupToken() || earlier.Value.DeviceToken() != values.DeviceToken())
                    AddUnique(warnings, "Star Citizen import: relation '" + relationName + "' is bound to " + earlier.Key + " and " + nodeKey + " with different axis settings; it keeps those of " + earlier.Key + ".");
                return false;
            }
            appliedThisImport[bind] = new KeyValuePair<string, ScAxisValues>(nodeKey, values);
            ScAxisValues current = ScAxisValues.From(BindTuning.Resolve(bind, TuningGame, null).Tuning);
            if (current.GroupToken() == values.GroupToken() && current.DeviceToken() == values.DeviceToken())
                return false;
            AxisTuning tuning = values.ToTuning();
            tuning.Slider = bind.Slider == true;
            BindTuning.Set(bind, BindTuning.GameScope(TuningGame), tuning);
            return true;
        }

        // ---- export ---------------------------------------------------------------------------------------------------

        private sealed class ScAxisSource
        {
            public ScWantedRebind Want;
            public int Instance;
            public string AxisToken;
            public string Group;
            public ScAxisValues Values;
        }

        // Writes the option groups and device options of the axis rebinds JoyPro wrote; other groups, devices and
        // attributes stay. Several relations on one group (or physical axis) with different values: the first relation
        // by name wins, with a warning. Merge and add keep values the user changed in the game.
        private static void ApplyAxisSettings(
            ScActionmapsDocument document,
            List<ScWantedRebind> written,
            Dictionary<string, ScOptionGroupInfo> tree,
            StarCitizenExportMode mode,
            Dictionary<string, HashSet<string>> ownedBefore,
            Dictionary<string, HashSet<string>> owned,
            EngineStarCitizenExportResult result)
        {
            Dictionary<string, ScAxisSource> groups = new Dictionary<string, ScAxisSource>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ScAxisSource> axes = new Dictionary<string, ScAxisSource>(StringComparer.OrdinalIgnoreCase);
            foreach (ScWantedRebind want in written.OrderBy(w => w.RelationName, StringComparer.OrdinalIgnoreCase))
            {
                if (!want.IsAxis || want.Bind == null)
                    continue;
                int split = want.Input.IndexOf('_');
                int instance;
                if (!want.Input.StartsWith("js", StringComparison.OrdinalIgnoreCase) || split <= 2 ||
                    !int.TryParse(want.Input.Substring(2, split - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out instance))
                    continue;
                string token = want.Input.Substring(split + 1).Trim().ToLowerInvariant();
                if (!IsScAxisToken(token))
                    continue;
                ResolvedTuning resolved = BindTuning.Resolve(want.Bind, TuningGame, null);
                if (!HasAxisSettings(want.Bind, resolved))
                    continue;
                ScAxisSource source = new ScAxisSource { Want = want, Instance = instance, AxisToken = token, Group = want.State.OptionGroup ?? string.Empty, Values = ScAxisValues.From(resolved.Tuning) };

                if (source.Group.Length == 0)
                {
                    ScAxisValues neutral = new ScAxisValues();
                    if (source.Values.GroupToken() != neutral.GroupToken())
                        AddUnique(result.Warnings, "Star Citizen: no option group known for '" + want.State.Category + "|" + want.State.Id + "' (relation '" + want.RelationName + "'): invert/exponent/curve not written. Refresh the DB (engine db refresh-starcitizen).");
                }
                else
                {
                    string groupKey = instance.ToString(CultureInfo.InvariantCulture) + "|" + source.Group;
                    ScAxisSource first;
                    if (!groups.TryGetValue(groupKey, out first))
                        groups[groupKey] = source;
                    else if (first.Values.GroupToken() != source.Values.GroupToken())
                        AddUnique(result.Warnings, "Star Citizen: relations '" + first.Want.RelationName + "' and '" + want.RelationName + "' share option group " + source.Group + " on js" + instance.ToString(CultureInfo.InvariantCulture) + " with different settings; '" + first.Want.RelationName + "' wins (" + first.Values.Describe(true) + ").");
                }

                string axisKey = instance.ToString(CultureInfo.InvariantCulture) + "|" + token;
                ScAxisSource firstAxis;
                if (!axes.TryGetValue(axisKey, out firstAxis))
                    axes[axisKey] = source;
                else if (firstAxis.Values.DeviceToken() != source.Values.DeviceToken())
                    AddUnique(result.Warnings, "Star Citizen: relations '" + firstAxis.Want.RelationName + "' and '" + want.RelationName + "' use axis " + token + " of js" + instance.ToString(CultureInfo.InvariantCulture) + " with a different dead zone/saturation; '" + firstAxis.Want.RelationName + "' wins (" + firstAxis.Values.DeviceToken() + ").");
            }

            bool keepUserValues = mode == StarCitizenExportMode.Merge || mode == StarCitizenExportMode.Add;
            foreach (ScAxisSource source in groups.Values.OrderBy(s => s.Instance).ThenBy(s => s.Group, StringComparer.OrdinalIgnoreCase))
            {
                XElement options = document.JoystickOptions(source.Instance);
                if (options == null)
                    continue;
                string product = NormalizeScProductToken((string)options.Attribute("Product"));
                string ownerKey = "options:" + product + "|" + source.Group;
                bool showCurve = GroupShowsCurve(tree, source.Group);
                ScAxisValues want = source.Values;
                ScAxisValues existing = EffectiveGroupValues(options, tree, source.Group, false);
                if (!showCurve)
                {
                    // Invert-only groups (zoom, turret limiter): the shape stays the game's.
                    want = new ScAxisValues { Invert = want.Invert, Exponent = existing.Exponent, Curve = existing.Curve, Sensitivity = existing.Sensitivity };
                }
                string wantToken = want.GroupToken();
                string existingToken = existing.GroupToken();
                if (existingToken == wantToken)
                {
                    GameOwnershipStore.Add(owned, ownerKey, wantToken);
                    continue;
                }
                XElement leaf = UserGroupElement(options, source.Group);
                if (keepUserValues && UserGroupHasValues(leaf) && !GameOwnershipStore.Contains(ownedBefore, ownerKey, existingToken))
                {
                    AddUnique(result.Warnings, "Star Citizen: " + source.Group + " on js" + source.Instance.ToString(CultureInfo.InvariantCulture) + " has settings made in the game; " + ExportModeToString(mode) + " keeps them.");
                    continue;
                }
                WriteGroupValues(document, options, tree, source.Group, want, showCurve);
                GameOwnershipStore.Add(owned, ownerKey, wantToken);
                result.OptionGroupsWritten++;
                AddUnique(result.AxisSettings, "js" + source.Instance.ToString(CultureInfo.InvariantCulture) + " " + source.Group + ": " + want.Describe(showCurve) + " (" + source.Want.RelationName + ")");
            }

            foreach (ScAxisSource source in axes.Values.OrderBy(s => s.Instance).ThenBy(s => s.AxisToken, StringComparer.Ordinal))
            {
                XElement options = document.JoystickOptions(source.Instance);
                if (options == null)
                    continue;
                string rawProduct = (string)options.Attribute("Product") ?? string.Empty;
                string product = NormalizeScProductToken(rawProduct);
                if (product.Length == 0)
                    continue;
                string ownerKey = "deviceoptions:" + product + "|" + source.AxisToken;
                ScAxisValues existing = new ScAxisValues();
                XElement option = DeviceOptionElement(document.DeviceOptions(), product, source.AxisToken);
                ReadDeviceValues(existing, option);
                string wantToken = source.Values.DeviceToken();
                string existingToken = existing.DeviceToken();
                if (existingToken == wantToken)
                {
                    GameOwnershipStore.Add(owned, ownerKey, wantToken);
                    continue;
                }
                bool userSet = option != null && (option.Attribute("deadzone") != null || option.Attribute("saturation") != null);
                if (keepUserValues && userSet && !GameOwnershipStore.Contains(ownedBefore, ownerKey, existingToken))
                {
                    AddUnique(result.Warnings, "Star Citizen: axis " + source.AxisToken + " of '" + product + "' has a dead zone/saturation made in the game; " + ExportModeToString(mode) + " keeps it.");
                    continue;
                }
                if (option == null)
                    option = document.AddDeviceOption(rawProduct, source.AxisToken);
                SetNumberAttribute(option, "deadzone", source.Values.Deadzone, 0.0);
                SetNumberAttribute(option, "saturation", source.Values.Saturation, 1.0);
                GameOwnershipStore.Add(owned, ownerKey, wantToken);
                result.DeviceOptionsWritten++;
                AddUnique(result.AxisSettings, "js" + source.Instance.ToString(CultureInfo.InvariantCulture) + " axis " + source.AxisToken + ": dead zone " + Fmt(source.Values.Deadzone) + ", saturation " + Fmt(source.Values.Saturation) + " (" + source.Want.RelationName + ")");
            }
        }

        // An attribute the element already has gets the wanted value; a missing one is added only when the wanted value
        // is not the default.
        private static void SetNumberAttribute(XElement element, string name, double value, double defaultValue)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute != null)
            {
                double? current = ParseNumber(attribute.Value);
                if (!current.HasValue || Math.Abs(current.Value - value) > 1e-6)
                    attribute.Value = Fmt(value);
                return;
            }
            if (Math.Abs(value - defaultValue) > 1e-6)
                element.SetAttributeValue(name, Fmt(value));
        }

        // Makes the group's effective values the wanted ones with as few changes as possible: a value the leaf already
        // has is set; a missing one is added only when the inherited value differs. Exponent and curve exclude each
        // other (as in the game's menu).
        private static void WriteGroupValues(ScActionmapsDocument document, XElement options, Dictionary<string, ScOptionGroupInfo> tree, string group, ScAxisValues want, bool showCurve)
        {
            ScAxisValues inherited = EffectiveGroupValues(options, tree, group, true);
            XElement leaf = UserGroupElement(options, group);
            Func<XElement> ensureLeaf = () =>
            {
                if (leaf == null)
                {
                    leaf = new XElement(group);
                    document.AddElement(options, leaf, 3);
                }
                return leaf;
            };

            XAttribute invert = leaf == null ? null : leaf.Attribute("invert");
            if (invert != null)
            {
                if (ParseFlag(invert.Value) != want.Invert)
                    invert.Value = want.Invert ? "1" : "0";
            }
            else if (want.Invert != inherited.Invert)
                ensureLeaf().SetAttributeValue("invert", want.Invert ? "1" : "0");

            if (!showCurve)
                return;

            XElement curve = CurveElement(leaf);
            if (want.Curve != null)
            {
                List<double> current = curve == null ? null : CurveToGrid(curve);
                bool sameCurve = current != null && current.Count == want.Curve.Count && current.Zip(want.Curve, (a, b) => Math.Abs(a - b) < 1e-4).All(x => x);
                bool leafHasShape = current != null || (leaf != null && leaf.Attribute("exponent") != null);
                if (!sameCurve && (leafHasShape || inherited.ShapeToken() != want.ShapeToken()))
                {
                    XElement target = ensureLeaf();
                    if (target.Attribute("exponent") != null)
                        target.Attribute("exponent").Remove();
                    if (curve != null)
                        ScActionmapsDocument.RemoveWithIndent(curve);
                    XElement newCurve = new XElement("nonlinearity_curve");
                    document.AddElement(target, newCurve, 4);
                    for (int i = 0; i < want.Curve.Count; i++)
                        document.AddElement(newCurve, new XElement("point", new XAttribute("in", Fmt((i + 1) / 10.0)), new XAttribute("out", Fmt(want.Curve[i]))), 5);
                }
                else if (sameCurve && leaf.Attribute("exponent") != null)
                    leaf.Attribute("exponent").Remove();
            }
            else
            {
                bool hadCurve = curve != null && CurveToGrid(curve) != null;
                if (curve != null && !ParseFlag((string)curve.Attribute("reset")))
                {
                    ScActionmapsDocument.RemoveWithIndent(curve);
                    // Without children the group is written as the game does: <flight_move_roll exponent="2"/>.
                    if (leaf.Nodes().All(n => n is XText && string.IsNullOrWhiteSpace(((XText)n).Value)))
                        leaf.RemoveNodes();
                }
                XAttribute exponent = leaf == null ? null : leaf.Attribute("exponent");
                if (exponent != null)
                {
                    double? current = ParseNumber(exponent.Value);
                    if (!current.HasValue || Math.Abs(current.Value - want.Exponent) > 1e-6)
                        exponent.Value = Fmt(want.Exponent);
                }
                else if (hadCurve || inherited.ShapeToken() != want.ShapeToken())
                    ensureLeaf().SetAttributeValue("exponent", Fmt(want.Exponent));
            }

            XAttribute sensitivity = leaf == null ? null : leaf.Attribute("sensitivity");
            if (sensitivity != null)
            {
                double? current = ParseNumber(sensitivity.Value);
                if (!current.HasValue || Math.Abs(current.Value - want.Sensitivity) > 1e-6)
                    sensitivity.Value = Fmt(want.Sensitivity);
            }
            else if (Math.Abs(inherited.Sensitivity - want.Sensitivity) > 1e-6)
                ensureLeaf().SetAttributeValue("sensitivity", Fmt(want.Sensitivity));
        }

        // Game-wide settings (engine ffb set --device * --game StarCitizen): attributes.xml next to actionmaps.xml,
        // set in place. The game creates the file; JoyPro does not create one (its Version is the game's).
        private static void ApplyGameSettings(string profileDirectory, EngineStarCitizenExportResult result)
        {
            Dictionary<string, double> settings = FfbSettings.Resolve(InternalDataManagement.FfbOverrides, FfbSettings.AnyDevice, TuningGame, null);
            if (settings.Count == 0 || string.IsNullOrWhiteSpace(profileDirectory))
                return;
            string path = Path.Combine(profileDirectory, "attributes.xml");
            if (!File.Exists(path))
            {
                AddUnique(result.Warnings, "Star Citizen game settings not written: '" + path + "' does not exist yet (start the game once).");
                return;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                List<string> changes = new List<string>();
                if (!StarCitizenGameSettings.Apply(ref text, settings, changes))
                    return;
                GameExportSafety.BackupBeforeOverwrite(path, result.Warnings);
                File.WriteAllText(path, text, new UTF8Encoding(bom));
                result.FilesWritten++;
                foreach (string change in changes)
                    AddUnique(result.GameSettings, change);
            }
            catch (Exception ex)
            {
                result.Errors.Add("Could not write Star Citizen game settings '" + path + "': " + ex.Message);
            }
        }
    }
}
