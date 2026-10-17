#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

// Axis response tuning: one set of axis parameters, the game response models that turn a stick position into what the
// game sees, and the conversion between models. Plain math without engine dependencies: the GUI links this file for
// its live curve preview (keep it C# 7.3). See docs/axis-curves.md.
namespace JoyPro.Engine.Axis
{
    // The parameters of one axis response. Deadzone, saturations and curvature are DCS's (the base model JoyPro has
    // always stored); a game model with its own parameters keeps them in Native (e.g. Korea "sensitivity").
    public sealed class AxisTuning
    {
        public bool Inverted;
        public bool Slider;
        public double Deadzone;
        public double SaturationX = 1.0;
        public double SaturationY = 1.0;
        // One value: curvature; 11 values: user curve (outputs at 0, 0.1, .. 1).
        public List<double> Curvature = new List<double> { 0.0 };
        // Model-specific values by name.
        public Dictionary<string, double> Native = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public AxisTuning Clone()
        {
            AxisTuning copy = (AxisTuning)MemberwiseClone();
            copy.Curvature = new List<double>(Curvature ?? new List<double> { 0.0 });
            copy.Native = new Dictionary<string, double>(Native ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
            return copy;
        }

        public bool IsUserCurve
        {
            get { return Curvature != null && Curvature.Count == AxisModels.UserCurvePoints; }
        }

        public double NativeOr(string key, double fallback)
        {
            double value;
            return Native != null && Native.TryGetValue(key, out value) ? value : fallback;
        }

        // A game curve with any number of control points (X-Plane's per-axis response curve), kept in Native as
        // cpN (count), cpx<i>, cpy<i>: x = stick travel 0..1 (deflection magnitude on centred axes), y = output 0..1.
        public const string ControlPointCount = "cpN";

        public bool HasControlPoints
        {
            get { return NativeOr(ControlPointCount, 0) >= 2; }
        }

        public List<KeyValuePair<double, double>> ControlPoints()
        {
            List<KeyValuePair<double, double>> points = new List<KeyValuePair<double, double>>();
            int count = (int)Math.Round(NativeOr(ControlPointCount, 0));
            for (int i = 0; i < count; i++)
            {
                string index = i.ToString(CultureInfo.InvariantCulture);
                double x;
                double y;
                if (Native.TryGetValue("cpx" + index, out x) && Native.TryGetValue("cpy" + index, out y))
                    points.Add(new KeyValuePair<double, double>(x, y));
            }
            return points;
        }

        public void SetControlPoints(IList<KeyValuePair<double, double>> points)
        {
            ClearControlPoints();
            if (points == null || points.Count < 2)
                return;
            if (Native == null)
                Native = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Native[ControlPointCount] = points.Count;
            for (int i = 0; i < points.Count; i++)
            {
                string index = i.ToString(CultureInfo.InvariantCulture);
                Native["cpx" + index] = Math.Round(points[i].Key, 6);
                Native["cpy" + index] = Math.Round(points[i].Value, 6);
            }
        }

        public void ClearControlPoints()
        {
            if (Native == null)
                return;
            foreach (string key in Native.Keys.Where(k => string.Equals(k, ControlPointCount, StringComparison.OrdinalIgnoreCase)
                || ((k.StartsWith("cpx", StringComparison.OrdinalIgnoreCase) || k.StartsWith("cpy", StringComparison.OrdinalIgnoreCase)) && k.Length > 3 && k.Substring(3).All(char.IsDigit))).ToList())
                Native.Remove(key);
        }

        // "inv=1;sl=0;dz=0.05;sx=1;sy=1;cv=0.2;n.sensitivity=22" (invariant culture). Profiles store overrides as these
        // strings so a .pr0file keeps only framework types (JoyPro 1 can still open it).
        public string Format()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("inv=").Append(Inverted ? "1" : "0");
            sb.Append(";sl=").Append(Slider ? "1" : "0");
            sb.Append(";dz=").Append(Num(Deadzone));
            sb.Append(";sx=").Append(Num(SaturationX));
            sb.Append(";sy=").Append(Num(SaturationY));
            sb.Append(";cv=").Append(string.Join("|", (Curvature ?? new List<double> { 0.0 }).Select(Num)));
            foreach (KeyValuePair<string, double> native in (Native ?? new Dictionary<string, double>()).OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(";n.").Append(native.Key).Append('=').Append(Num(native.Value));
            return sb.ToString();
        }

        public static AxisTuning Parse(string text)
        {
            AxisTuning tuning = new AxisTuning();
            foreach (string part in (text ?? string.Empty).Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();
                double number;
                switch (key)
                {
                    case "inv": tuning.Inverted = value == "1"; break;
                    case "sl": tuning.Slider = value == "1"; break;
                    case "dz": if (TryNum(value, out number)) tuning.Deadzone = number; break;
                    case "sx": if (TryNum(value, out number)) tuning.SaturationX = number; break;
                    case "sy": if (TryNum(value, out number)) tuning.SaturationY = number; break;
                    case "cv":
                        List<double> curve = new List<double>();
                        foreach (string c in value.Split('|'))
                        {
                            if (TryNum(c, out number))
                                curve.Add(number);
                        }
                        tuning.Curvature = curve.Count == 0 ? new List<double> { 0.0 } : curve;
                        break;
                    default:
                        if (key.StartsWith("n.", StringComparison.Ordinal) && TryNum(value, out number))
                            tuning.Native[key.Substring(2)] = number;
                        break;
                }
            }
            return tuning;
        }

        internal static string Num(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static bool TryNum(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    public enum AxisParameterKind
    {
        Number,
        Toggle,
        UserCurve
    }

    // One adjustable parameter of a game's response model, in the game's own units.
    public sealed class AxisParameter
    {
        public string Key;
        public string Label;
        public AxisParameterKind Kind;
        public double Min;
        public double Max;
        public double Step;
        public double Default;
        public string Help;
    }

    // Marks a static method in engine\AxisModels\*.cs that returns a game's response model; the ids are the games
    // (GameIntegration ids, e.g. "WarThunder") that use it.
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class GameAxisModelAttribute : Attribute
    {
        public readonly string[] Games;

        public GameAxisModelAttribute(params string[] games)
        {
            Games = games ?? new string[0];
        }
    }

    public sealed class AxisModel
    {
        // "dcs", "il2korea", "generic", ...
        public string Id;
        public string Title;
        public string Description;
        public List<AxisParameter> Parameters = new List<AxisParameter>();
        // Stick position -1..1 (sliders: -1 = start) -> what the game uses, -1..1.
        public Func<AxisTuning, double, double> Evaluate;
        // Optional: the model's own parameters fitted to another model's curve (inheritance); null = copy as is.
        public Func<AxisTuning, Func<double, double>, AxisTuning> Fit;
        // Optional: parameters the game keeps once per physical device axis (MSFS's Sensitivity page), not per action:
        // relations on the same device axis should agree on them. A tuning with PerActionKey >= 0.5 has its own values.
        public List<string> SharedPerAxisKeys = new List<string>();
        public string PerActionKey;
        // Optional: the game curve takes free control points (binds tuning set --curve-points, AxisTuning.ControlPoints);
        // returns the stops the game gets for a tuning (its own control points, else those of dead zone, saturation and
        // curvature). null = the model has no free curve points.
        public Func<AxisTuning, List<KeyValuePair<double, double>>> CurveStops;
    }

    public static class AxisModels
    {
        public const int UserCurvePoints = 11;
        public const string Dcs = "dcs";
        public const string Il2Korea = "il2korea";
        public const string Il2 = "il2";
        public const string Generic = "generic";

        // Game id -> response model. Games without a researched model use "generic" (invert, dead zone, saturation).
        // Declared before Models: Build adds the models of engine\AxisModels\*.cs ([GameAxisModel]) to it.
        private static readonly Dictionary<string, string> GameModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DCS", Dcs },
            { "IL2Game", Il2 },
            { "IL2Korea", Il2Korea }
        };

        private static readonly Dictionary<string, AxisModel> Models = Build();

        // A game's own model lives in engine\AxisModels\<Game>.cs: a static method marked [GameAxisModel("<game id>")]
        // that returns the model (the GUI links the folder for its live preview).
        private static void AddGameModels(Dictionary<string, AxisModel> models)
        {
            foreach (Type type in typeof(AxisModels).Assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object[] marks = method.GetCustomAttributes(typeof(GameAxisModelAttribute), false);
                    if (marks.Length == 0 || method.GetParameters().Length != 0 || !typeof(AxisModel).IsAssignableFrom(method.ReturnType))
                        continue;
                    AxisModel model = (AxisModel)method.Invoke(null, null);
                    models[model.Id] = model;
                    foreach (string game in ((GameAxisModelAttribute)marks[0]).Games)
                        GameModels[game] = model.Id;
                }
            }
        }

        public static IEnumerable<AxisModel> All
        {
            get { return Models.Values; }
        }

        public static AxisModel Get(string id)
        {
            AxisModel model;
            return Models.TryGetValue(id ?? string.Empty, out model) ? model : Models[Generic];
        }

        public static AxisModel ForGame(string gameId)
        {
            string id;
            return Get(GameModels.TryGetValue(gameId ?? string.Empty, out id) ? id : Generic);
        }

        // The base tuning (DCS parameters) as a game's model sees it: models with their own parameters get them fitted
        // to the base curve; invert, slider, dead zone and saturation carry over.
        public static AxisTuning Inherit(AxisTuning baseTuning, AxisModel target)
        {
            AxisTuning tuning = baseTuning.Clone();
            if (target.Fit == null)
                return tuning;
            AxisModel dcs = Get(Dcs);
            AxisTuning shape = baseTuning.Clone();
            shape.Inverted = false;
            shape.Slider = false;
            return target.Fit(tuning, x => dcs.Evaluate(shape, x));
        }

        // Samples for a preview: n points from -1 to 1.
        public static List<KeyValuePair<double, double>> Sample(AxisModel model, AxisTuning tuning, int points)
        {
            List<KeyValuePair<double, double>> samples = new List<KeyValuePair<double, double>>();
            for (int i = 0; i < points; i++)
            {
                double x = -1.0 + 2.0 * i / (points - 1);
                samples.Add(new KeyValuePair<double, double>(x, model.Evaluate(tuning, x)));
            }
            return samples;
        }

        private static Dictionary<string, AxisModel> Build()
        {
            Dictionary<string, AxisModel> models = new Dictionary<string, AxisModel>(StringComparer.OrdinalIgnoreCase);
            models[Dcs] = new AxisModel
            {
                Id = Dcs,
                Title = "DCS World",
                Description = "Per aircraft, device and axis: dead zone, saturation X/Y, curvature (exponential) or an 11-point user curve, slider, invert (lua-gui_axes_tune.dll).",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "saturationX", Label = "Saturation X", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "saturationY", Label = "Saturation Y", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 1 },
                    new AxisParameter { Key = "curvature", Label = "Curvature", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "userCurve", Label = "User curve", Kind = AxisParameterKind.UserCurve, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "slider", Label = "Slider", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateDcs
            };
            models[Il2Korea] = new AxisModel
            {
                Id = Il2Korea,
                Title = "IL-2 Series: Korea",
                Description = "Per action (pitch: per pitch profile): exponent, dead zones (centre/edges; low/high on one-sided axes such as throttles, which Slider marks), centre offset (pitch only), built-in curve and control surface loading (pitch, roll, yaw), relative control; file units.",
                Parameters =
                {
                    new AxisParameter { Key = "sensitivity", Label = "Exponent", Kind = AxisParameterKind.Number, Min = -100, Max = 100, Step = 1, Default = 0 },
                    new AxisParameter { Key = "deadZone0", Label = "Dead zone: centre", Kind = AxisParameterKind.Number, Min = 0, Max = 50, Step = 1, Default = 0 },
                    new AxisParameter { Key = "deadZone1", Label = "Dead zone: edges", Kind = AxisParameterKind.Number, Min = 0, Max = 50, Step = 1, Default = 0 },
                    new AxisParameter { Key = "offset", Label = "Centre offset (pitch)", Kind = AxisParameterKind.Number, Min = -100, Max = 100, Step = 1, Default = 0 },
                    new AxisParameter { Key = "harmonization", Label = "Built-in response curve (pitch/roll/yaw)", Kind = AxisParameterKind.Number, Min = 0, Max = 2, Step = 1, Default = 1,
                        Help = "0 Exponent only, 1 Built-in static curve, 2 Built-in adaptive curve (speed dependent, applied in flight on top of this curve)." },
                    new AxisParameter { Key = "normalized", Label = "Control surface loading (pitch/roll/yaw)", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 1,
                        Help = "0 Proportional stick travel (caps the deflection at high speed), 1 Full stick travel." },
                    new AxisParameter { Key = "relative", Label = "Relative control", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 1, Default = 0,
                        Help = "1: the axis sets the speed of change instead of the position (Key mapping, the axis's diamond button)." },
                    new AxisParameter { Key = "relativeDeadZone", Label = "Relative control: dead zone %", Kind = AxisParameterKind.Number, Min = 0, Max = 80, Step = 1, Default = 10,
                        Help = "Dead zone of a relative axis in % (general.actions \"additive\", stored /100)." },
                    new AxisParameter { Key = "relativeSensitivity", Label = "Relative control: sensitivity", Kind = AxisParameterKind.Number, Min = 1, Max = 20, Step = 1, Default = 5,
                        Help = "Speed of a relative axis, 1..20 as in the game (general.actions \"additive\", stored /100)." },
                    new AxisParameter { Key = "slider", Label = "One-sided axis (throttle, brakes, ...)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateKorea,
                Fit = FitKorea
            };
            models[Il2] = new AxisModel
            {
                Id = Il2,
                Title = "IL-2 Great Battles",
                Description = "Per action, all aircraft: centred axes (current.responses type1) sensitivity balance, centre and side dead zone, pitch also the centre shift (global.pitch); one-sided axes such as throttles (Slider, type2) low and high dead zone; file units (0.5 balance and 0.25 dead zone are 100 % in the game).",
                Parameters =
                {
                    new AxisParameter { Key = "sensitivityBallance", Label = "Sensitivity balance", Kind = AxisParameterKind.Number, Min = 0, Max = 0.5, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "centerDeadZone", Label = "Dead zone: centre", Kind = AxisParameterKind.Number, Min = 0, Max = 0.25, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "sideDeadZone", Label = "Dead zone: edges", Kind = AxisParameterKind.Number, Min = 0, Max = 0.25, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "pitchShift", Label = "Centre shift (pitch)", Kind = AxisParameterKind.Number, Min = -1, Max = 1, Step = 0.01, Default = 0,
                        Help = "Output at the stick centre; pitch only (data\\input\\global.pitch)." },
                    new AxisParameter { Key = "lowValuesDeadZone", Label = "Dead zone: low end (one-sided)", Kind = AxisParameterKind.Number, Min = 0, Max = 0.5, Step = 0.01, Default = 0,
                        Help = "Share of the whole travel at the low end that stays at minimum (one-sided axes, type2)." },
                    new AxisParameter { Key = "highValuesDeadZone", Label = "Dead zone: high end (one-sided)", Kind = AxisParameterKind.Number, Min = 0, Max = 0.5, Step = 0.01, Default = 0,
                        Help = "Share of the whole travel at the high end that stays at maximum (one-sided axes, type2)." },
                    new AxisParameter { Key = "slider", Label = "One-sided axis (throttle, brakes, ...)", Kind = AxisParameterKind.Toggle },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateIl2,
                Fit = FitIl2
            };
            models[Generic] = new AxisModel
            {
                Id = Generic,
                Title = "Invert, dead zone, saturation",
                Description = "Games whose response JoyPro does not model: invert, a linear dead zone and saturation as far as the game supports them.",
                Parameters =
                {
                    new AxisParameter { Key = "deadzone", Label = "Dead zone", Kind = AxisParameterKind.Number, Min = 0, Max = 1, Step = 0.01, Default = 0 },
                    new AxisParameter { Key = "invert", Label = "Invert", Kind = AxisParameterKind.Toggle }
                },
                Evaluate = EvaluateGeneric
            };
            AddGameModels(models);
            return models;
        }

        // ---- DCS: Scripts\Input filter, evaluated as lua-gui_axes_tune.dll does ------------------------------------

        public static double EvaluateDcs(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            double y;
            if (t.Slider)
            {
                y = 2.0 * DcsShape(t, (x + 1.0) / 2.0) - 1.0;
                return t.Inverted ? -y : y;
            }
            double sign = x < 0 ? -1.0 : 1.0;
            if (t.Inverted)
                sign = -sign;
            return sign * DcsShape(t, Math.Abs(x));
        }

        private static double DcsShape(AxisTuning t, double u)
        {
            double dz = t.Deadzone;
            double sx = t.SaturationX <= 0 ? 1.0 : t.SaturationX;
            double sy = t.SaturationY;
            if (u <= dz)
                return 0.0;
            if (u >= sx)
                return sy;
            double v = (u - dz) / (sx - dz);
            if (t.IsUserCurve)
                return NaturalSpline(t.Curvature, v);
            double k = t.Curvature == null || t.Curvature.Count == 0 ? 0.0 : t.Curvature[0];
            if (k != 0.0)
                v = ExpM1(10.0 * k * v) / ExpM1(10.0 * k);
            return sy * v;
        }

        private static double ExpM1(double x)
        {
            return Math.Abs(x) < 1e-5 ? x + x * x / 2.0 + x * x * x / 6.0 : Math.Exp(x) - 1.0;
        }

        // Natural cubic spline through equally spaced points on 0..1 (DCS user curve).
        internal static double NaturalSpline(List<double> ys, double v)
        {
            int n = ys.Count;
            double h = 1.0 / (n - 1);
            double[] m = new double[n];
            double[] c = new double[n];
            double[] d = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double rhs = 6.0 * (ys[i + 1] - 2.0 * ys[i] + ys[i - 1]) / (h * h);
                double pivot = 4.0 - (i > 1 ? c[i - 1] : 0.0);
                c[i] = 1.0 / pivot;
                d[i] = (rhs - (i > 1 ? d[i - 1] : 0.0)) / pivot;
            }
            for (int i = n - 2; i >= 1; i--)
                m[i] = d[i] - c[i] * (i < n - 2 ? m[i + 1] : 0.0);
            int lo = Math.Max(0, Math.Min(n - 2, (int)Math.Floor(v / h)));
            int hi = lo + 1;
            double a = (hi * h - v) / h;
            double b = (v - lo * h) / h;
            return a * ys[lo] + b * ys[hi] + ((a * a * a - a) * m[lo] + (b * b * b - b) * m[hi]) * h * h / 6.0;
        }

        // ---- IL-2 Korea: general.pitch / shared.responses (docs/games/il2-korea.md) --------------------------------

        // Flat side of a centre offset: measured share of the control point distance (no closed form found).
        private static readonly double[] KoreaFlatHeights = { 0.0, 0.4, 0.61, 0.8, 0.9, 1.0 };
        private static readonly double[] KoreaFlatFactors = { 0.0, 0.300, 0.545, 0.767, 0.883, 1.0 };

        // Korea and Great Battles share the response curve, in different units: a tuning made for the IL-2 family
        // ("family:il2") may carry either game's keys. These read a value in one game's units, converted from the other
        // game's key when the tuning only has that.
        public static double KoreaValue(AxisTuning t, string key)
        {
            double value;
            if (t.Native != null && t.Native.TryGetValue(key, out value))
                return value;
            switch (key)
            {
                case "sensitivity": return t.NativeOr("sensitivityBallance", 0) * 200.0;
                case "deadZone0": return t.Slider && t.Native != null && t.Native.ContainsKey("lowValuesDeadZone") ? t.Native["lowValuesDeadZone"] * 100.0 : t.NativeOr("centerDeadZone", 0) * 100.0;
                case "deadZone1": return t.Slider && t.Native != null && t.Native.ContainsKey("highValuesDeadZone") ? t.Native["highValuesDeadZone"] * 100.0 : t.NativeOr("sideDeadZone", 0) * 100.0;
                case "offset": return t.NativeOr("pitchShift", 0) * 100.0;
                default: return 0.0;
            }
        }

        public static double Il2Value(AxisTuning t, string key)
        {
            double value;
            if (t.Native != null && t.Native.TryGetValue(key, out value))
                return value;
            switch (key)
            {
                case "sensitivityBallance": return t.NativeOr("sensitivity", 0) / 200.0;
                case "centerDeadZone": return t.NativeOr("deadZone0", 0) / 100.0;
                case "sideDeadZone": return t.NativeOr("deadZone1", 0) / 100.0;
                // Korea's one-sided axes keep Low/High in deadZone0/deadZone1 (share of the whole travel, in %).
                case "lowValuesDeadZone": return t.NativeOr("deadZone0", 0) / 100.0;
                case "highValuesDeadZone": return t.NativeOr("deadZone1", 0) / 100.0;
                case "pitchShift": return t.NativeOr("offset", 0) / 100.0;
                default: return 0.0;
            }
        }

        public static double EvaluateKorea(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            // One-sided axes (throttle, brakes, ...): the positive half of the curve, without offset, over the whole
            // travel (fitted to the game's tables, error 6e-7).
            if (t.Slider)
                return 2.0 * KoreaCurve(KoreaValue(t, "sensitivity") / 100.0, KoreaValue(t, "deadZone0") / 100.0, KoreaValue(t, "deadZone1") / 100.0, 0.0, (x + 1.0) / 2.0) - 1.0;
            return KoreaCurve(KoreaValue(t, "sensitivity") / 100.0, KoreaValue(t, "deadZone0") / 100.0, KoreaValue(t, "deadZone1") / 100.0,
                t.NativeOr("offset", 0) / 100.0, x);
        }

        // s: exponent 0..1, d/e: dead zone at the centre / edges as a share of the half travel, o: centre offset.
        private static double KoreaCurve(double s, double d, double e, double o, double x)
        {
            double side = x < 0 ? -1.0 : 1.0;
            double u = Math.Abs(x);
            double x0 = d;
            double x1 = 1.0 - e;
            if (u <= x0)
                return o;
            if (u >= x1)
                return side;
            double y0 = o;
            double y1 = side;
            double w = x1 - x0;
            double h = Math.Abs(y1 - y0);
            double factor = h >= 1.0 ? w : w * Interpolate(KoreaFlatHeights, KoreaFlatFactors, h);
            double dist = s / 2.0 * factor;
            double len = Math.Sqrt(w * w + (y1 - y0) * (y1 - y0));
            // Perpendicular to the chord, towards the x axis (flatter around the centre).
            double nx = Math.Abs(y1 - y0) / len;
            double ny = -side * w / len;
            double cx = (x0 + x1) / 2.0 + dist * nx;
            double cy = (y0 + y1) / 2.0 + dist * ny;
            return QuadraticBezierY(x0, y0, cx, cy, x1, y1, u);
        }

        private static double QuadraticBezierY(double x0, double y0, double cx, double cy, double x1, double y1, double xv)
        {
            double lo = 0.0;
            double hi = 1.0;
            for (int i = 0; i < 50; i++)
            {
                double mid = (lo + hi) / 2.0;
                double bx = (1 - mid) * (1 - mid) * x0 + 2 * (1 - mid) * mid * cx + mid * mid * x1;
                if (bx < xv)
                    lo = mid;
                else
                    hi = mid;
            }
            double t = (lo + hi) / 2.0;
            return (1 - t) * (1 - t) * y0 + 2 * (1 - t) * t * cy + t * t * y1;
        }

        private static double Interpolate(double[] xs, double[] ys, double x)
        {
            if (x <= xs[0])
                return ys[0];
            for (int i = 1; i < xs.Length; i++)
            {
                if (x <= xs[i])
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            }
            return ys[ys.Length - 1];
        }

        // Korea's exponent and dead zones that come closest to a base curve (sampled on 0..1, the positive side).
        private static AxisTuning FitKorea(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            result.Native["deadZone0"] = Math.Round(Math.Max(0, Math.Min(50, tuning.Deadzone * 100)));
            result.Native["deadZone1"] = Math.Round(Math.Max(0, Math.Min(50, (1 - tuning.SaturationX) * 100)));
            double best = double.MaxValue;
            double bestS = 0;
            for (int s = 0; s <= 100; s++)
            {
                result.Native["sensitivity"] = s;
                result.Native.Remove("sensitivityBallance");
                double err = 0;
                for (int i = 1; i <= 20; i++)
                {
                    double x = i / 20.0;
                    double diff = EvaluateKorea(result, x) - target(x);
                    err += diff * diff;
                }
                if (err < best)
                {
                    best = err;
                    bestS = s;
                }
            }
            result.Native["sensitivity"] = bestS;
            return result;
        }

        // ---- IL-2 Great Battles: current.responses (docs/axis-curves.md) ------------------------------------------

        // Korea's curve with sensitivity = 200 * sensitivityBallance. The dead zones snap to the game's 0.02 grid, rounded
        // towards the centre on the positive side and away from it on the negative side (fitted to the tables IL-2 compiles
        // into current.map, error below 1e-4).
        public static double EvaluateIl2(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            // One-sided axes (type2/type4 rows: high and low dead zone, no curve): linear over the whole travel between
            // the two dead zones (IL-2 compiles exactly this, tools\e2e\research\Il2GbAxesSurvey.ps1).
            if (t.Slider)
            {
                double low = Math.Max(0, Math.Round(Il2Value(t, "lowValuesDeadZone"), 2));
                double high = Math.Max(0, Math.Round(Il2Value(t, "highValuesDeadZone"), 2));
                double span = 1.0 - low - high;
                double u = (x + 1.0) / 2.0;
                double v = span <= 1e-9 ? (u < low ? 0.0 : 1.0) : Math.Max(0.0, Math.Min(1.0, (u - low) / span));
                return 2.0 * v - 1.0;
            }
            // current.responses holds two decimals (the export rounds the same way).
            double s = Math.Round(Il2Value(t, "sensitivityBallance"), 2) * 2.0;
            double center = Math.Round(Il2Value(t, "centerDeadZone"), 2) * 50.0;
            double side = Math.Round(Il2Value(t, "sideDeadZone"), 2) * 50.0;
            double d = x >= 0 ? Math.Floor(center + 1e-9) / 50.0 : Math.Ceiling(center - 1e-9) / 50.0;
            double e = x >= 0 ? Math.Ceiling(side - 1e-9) / 50.0 : Math.Floor(side + 1e-9) / 50.0;
            return KoreaCurve(s, d, e, Math.Round(Il2Value(t, "pitchShift"), 2), x);
        }

        // What JoyPro has always written for a bind without IL-2 settings: the curvature as the sensitivity balance
        // (0..1 -> 0..0.5), the dead zone (0..1 -> 0..0.25) at the centre. A slider is a one-sided row: the base dead
        // zone at the low end and the saturation's remainder at the high end (shares of the whole travel, as DCS).
        private static AxisTuning FitIl2(AxisTuning tuning, Func<double, double> target)
        {
            AxisTuning result = tuning.Clone();
            if (tuning.Slider)
            {
                double sx = tuning.SaturationX <= 0 ? 1.0 : tuning.SaturationX;
                result.Native["lowValuesDeadZone"] = Math.Round(Math.Max(0, Math.Min(0.5, tuning.Deadzone)), 2);
                result.Native["highValuesDeadZone"] = Math.Round(Math.Max(0, Math.Min(0.5, 1.0 - sx)), 2);
                return result;
            }
            double curvature = tuning.Curvature == null || tuning.Curvature.Count == 0 ? 0.0 : tuning.Curvature[0];
            result.Native["sensitivityBallance"] = Math.Round(curvature * 0.5, 2);
            result.Native["centerDeadZone"] = Math.Round(tuning.Deadzone * 0.25, 2);
            result.Native["sideDeadZone"] = 0.0;
            return result;
        }

        // ---- Generic ----------------------------------------------------------------------------------------------

        public static double EvaluateGeneric(AxisTuning t, double x)
        {
            x = Math.Max(-1.0, Math.Min(1.0, x));
            if (t.Inverted)
                x = -x;
            double u = Math.Abs(x);
            if (u <= t.Deadzone)
                return 0.0;
            return Math.Sign(x) * Math.Min(1.0, (u - t.Deadzone) / (1.0 - t.Deadzone));
        }
    }
}
