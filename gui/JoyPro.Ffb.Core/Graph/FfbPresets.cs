using JoyPro.Ffb.Core.Output;
using JoyPro.Ffb.Core.Telemetry;

namespace JoyPro.Ffb.Core.Graph;

/// <summary>The effect names of the built-in graph; the per-game switches and strengths refer to them.</summary>
/// <remarks>The engine CLI (engine/EngineFfbTelemetry.cs) keeps the same list for its validation.</remarks>
public static class FfbEffectNames
{
    public const string Buffet = "buffet";
    public const string GForce = "gforce";
    public const string Gear = "gear";
    public const string Flaps = "flaps";
    public const string SpeedBrake = "speedbrake";
    public const string Runway = "runway";
    public const string Guns = "guns";
    public const string Hits = "hits";
    public const string Shake = "shake";
    public const string Spring = "spring";

    public static IReadOnlyList<(string Name, string Title)> All { get; } =
    [
        (Buffet, "Stall / AoA buffet"),
        (GForce, "G-force stick weight"),
        (Gear, "Gear moving rumble"),
        (Flaps, "Flaps moving rumble"),
        (SpeedBrake, "Speed brake buffet"),
        (Runway, "Runway rumble"),
        (Guns, "Gun fire bursts"),
        (Hits, "Hits taken"),
        (Shake, "Cockpit shake (IL-2)"),
        (Spring, "Centring spring: airspeed stiffening and trim")
    ];
}

/// <summary>Built-in graphs ("presets"): what the stick does out of the box in every supported game.</summary>
/// <remarks>
/// One flight graph on the normalised keys (<see cref="AircraftTelemetry"/>), so it works in DCS, IL-2 and War
/// Thunder alike; values a game does not send read 0 and their effect stays silent. Layout: telemetry nodes left,
/// shaping in the middle, effects right. Slots: buffet 0, gear/flaps/speed brake rumble 1 (strongest wins), runway and
/// cockpit shake 2, guns 3; the G-force weight and the hit jolt are constant forces on different slots; one spring
/// per axis. Strengths are modest on purpose: the user's global strength and the force cap apply on top.
/// The design (telemetry -> group/curve -> output with values at 0 % and 100 %) follows the user's Forza haptics app.
/// </remarks>
public static class FfbPresets
{
    public const string FlightName = "Flight (built-in)";

    public static FfbGraph Flight()
    {
        var graph = new FfbGraph { Name = FlightName };
        double row = 0;
        double Next() => 40 + 120 * row++;

        // Airborne gate: 0 below 20 m/s, 1 above 35 m/s (AoA means nothing on a parked aircraft).
        var y = Next();
        var airborne = graph.Add(Tele("Airborne (IAS)", AircraftTelemetry.Ias, 20, 35, 40, y));

        // Stall / AoA buffet: rises from 10 deg AoA to 20 deg, plus the stall warning.
        y = Next();
        var aoa = graph.Add(Tele("AoA beyond 10 deg", AircraftTelemetry.Aoa, 10, 20, 40, y));
        var aoaCurve = graph.Add(Curve("Buffet onset", 300, y, 0.45, 0.05, 0.85, 0.6));
        var stall = graph.Add(Tele("Stall warning", AircraftTelemetry.StallWarning, 0, 1, 40, Next()));
        var buffetIn = graph.Add(new GroupNode { Title = "Buffet sources", Mode = CombineMode.Maximum, X = 560, Y = y });
        var buffetGate = graph.Add(new MultiplyNode { Title = "Only in the air", X = 800, Y = y });
        var buffet = graph.Add(new PeriodicNode
        {
            Title = "Stall buffet", Effect = FfbEffectNames.Buffet, Slot = 0, X = 1060, Y = y,
            Waveform = FfbWaveform.Sine, Modulation = FfbModulation.StrengthAndFrequency,
            MinStrength = 0.05, MaxStrength = 0.45, MinFrequencyHz = 9, MaxFrequencyHz = 14
        });
        graph.Connect(aoa, "value", aoaCurve, "in");
        graph.Connect(aoaCurve, "out", buffetIn, "in");
        graph.Connect(stall, "value", buffetIn, "in");
        graph.Connect(buffetIn, "out", buffetGate, "a");
        graph.Connect(airborne, "value", buffetGate, "b");
        graph.Connect(buffetGate, "out", buffet, "in");

        // G-force: the stick gets heavier (pushes forward) from 1.5 G to 9 G.
        y = Next();
        var g = graph.Add(Tele("G load 1.5..9", AircraftTelemetry.G, 1.5, 9, 40, y));
        var gSmooth = graph.Add(new LowPassNode { Title = "Smooth G", TimeConstant = 0.15, X = 300, Y = y });
        var weight = graph.Add(new ConstantForceNode
        {
            Title = "G stick weight", Effect = FfbEffectNames.GForce, Slot = 0, X = 1060, Y = y,
            Axis = FfbAxis.Y, Negative = true, MinStrength = 0, MaxStrength = 0.35
        });
        graph.Connect(g, "value", gSmooth, "in");
        graph.Connect(gSmooth, "out", weight, "in");

        // Gear / flaps / speed brake: a rumble while they move (speed brake: buffet while out, by airspeed).
        y = Next();
        var gearMoving = graph.Add(Tele("Gear moving", AircraftTelemetry.GearMoving, 0, 1, 40, y));
        var gearRumble = graph.Add(new PeriodicNode
        {
            Title = "Gear rumble", Effect = FfbEffectNames.Gear, Slot = 1, X = 1060, Y = y,
            Waveform = FfbWaveform.Triangle, MinStrength = 0.15, MaxStrength = 0.15, MinFrequencyHz = 16, MaxFrequencyHz = 16
        });
        graph.Connect(gearMoving, "value", gearRumble, "in");

        y = Next();
        var flapsMoving = graph.Add(Tele("Flaps moving", AircraftTelemetry.FlapsMoving, 0, 1, 40, y));
        var flapsRumble = graph.Add(new PeriodicNode
        {
            Title = "Flaps rumble", Effect = FfbEffectNames.Flaps, Slot = 1, X = 1060, Y = y,
            Waveform = FfbWaveform.Sine, MinStrength = 0.1, MaxStrength = 0.1, MinFrequencyHz = 22, MaxFrequencyHz = 22
        });
        graph.Connect(flapsMoving, "value", flapsRumble, "in");

        y = Next();
        var brake = graph.Add(Tele("Speed brake out", AircraftTelemetry.SpeedBrake, 0, 1, 40, y));
        var brakeAirspeed = graph.Add(Tele("Airspeed 50..250 m/s", AircraftTelemetry.Ias, 50, 250, 40, Next()));
        var brakeLevel = graph.Add(new MultiplyNode { Title = "Brake x airspeed", X = 560, Y = y });
        var brakeBuffet = graph.Add(new PeriodicNode
        {
            Title = "Speed brake buffet", Effect = FfbEffectNames.SpeedBrake, Slot = 1, X = 1060, Y = y,
            Waveform = FfbWaveform.Sine, MinStrength = 0.03, MaxStrength = 0.2, MinFrequencyHz = 12, MaxFrequencyHz = 12
        });
        graph.Connect(brake, "value", brakeLevel, "a");
        graph.Connect(brakeAirspeed, "value", brakeLevel, "b");
        graph.Connect(brakeLevel, "out", brakeBuffet, "in");

        // Runway: rumble on the ground, faster and stronger with speed.
        y = Next();
        var roll = graph.Add(Tele("Ground roll 1..60 m/s", AircraftTelemetry.GroundRoll, 1, 60, 40, y));
        var runway = graph.Add(new PeriodicNode
        {
            Title = "Runway rumble", Effect = FfbEffectNames.Runway, Slot = 2, X = 1060, Y = y,
            Waveform = FfbWaveform.Triangle, Modulation = FfbModulation.StrengthAndFrequency,
            MinStrength = 0.04, MaxStrength = 0.22, MinFrequencyHz = 8, MaxFrequencyHz = 24
        });
        graph.Connect(roll, "value", runway, "in");

        // IL-2 cockpit shake: the game's own shake amplitude and frequency.
        y = Next();
        var shakeAmp = graph.Add(Tele("Cockpit shake", AircraftTelemetry.ShakeAmplitude, 0, 1, 40, y));
        var shakeFreq = graph.Add(new TelemetryNode
        {
            Title = "Shake frequency (Hz)", Key = AircraftTelemetry.ShakeFrequency, Mode = TelemetryNormalisation.Raw, X = 40, Y = Next()
        });
        var shake = graph.Add(new PeriodicNode
        {
            Title = "Cockpit shake", Effect = FfbEffectNames.Shake, Slot = 2, X = 1060, Y = y,
            Waveform = FfbWaveform.Sine, MinStrength = 0, MaxStrength = 0.3, MinFrequencyHz = 15, MaxFrequencyHz = 15
        });
        graph.Connect(shakeAmp, "value", shake, "in");
        graph.Connect(shakeFreq, "value", shake, "frequency");

        // Guns: pulsed square bursts while firing.
        y = Next();
        var firing = graph.Add(Tele("Guns firing", AircraftTelemetry.Firing, 0, 1, 40, y));
        var guns = graph.Add(new PeriodicNode
        {
            Title = "Gun bursts", Effect = FfbEffectNames.Guns, Slot = 3, X = 1060, Y = y, Axes = FfbAxisSelection.Y,
            Waveform = FfbWaveform.Square, MinStrength = 0.35, MaxStrength = 0.35, MinFrequencyHz = 25, MaxFrequencyHz = 25,
            Pulsed = true, PulseRateHz = 12, DutyCycle = 0.45
        });
        graph.Connect(firing, "value", guns, "in");

        // Hits: a short sideways jolt.
        y = Next();
        var hit = graph.Add(Tele("Hit taken", AircraftTelemetry.Hit, 0, 1, 40, y));
        var jolt = graph.Add(new ConstantForceNode
        {
            Title = "Hit jolt", Effect = FfbEffectNames.Hits, Slot = 1, X = 1060, Y = y,
            Axis = FfbAxis.X, MinStrength = 0.3, MaxStrength = 0.3
        });
        graph.Connect(hit, "value", jolt, "in");

        // Centring spring: a base stiffness plus airspeed stiffening; the centre follows the trim (War Thunder).
        y = Next();
        var baseStiffness = graph.Add(new ConstantNode { Title = "Base stiffness", Value = 0.2, X = 40, Y = y });
        var speed = graph.Add(Tele("Airspeed 30..250 m/s", AircraftTelemetry.Ias, 30, 250, 40, Next()));
        var aero = graph.Add(new ScaleOffsetNode { Title = "Aero stiffening", Scale = 0.5, X = 300, Y = y + 120 });
        var stiffness = graph.Add(new GroupNode { Title = "Stiffness", Mode = CombineMode.SumClamped, X = 560, Y = y });
        var trimPitch = graph.Add(new TelemetryNode
        {
            Title = "Pitch trim", Key = AircraftTelemetry.TrimPitch, Mode = TelemetryNormalisation.Signed, Minimum = -1, Maximum = 1, X = 40, Y = Next()
        });
        var trimRoll = graph.Add(new TelemetryNode
        {
            Title = "Roll trim", Key = AircraftTelemetry.TrimRoll, Mode = TelemetryNormalisation.Signed, Minimum = -1, Maximum = 1, X = 40, Y = Next()
        });
        var springY = graph.Add(new SpringNode
        {
            Title = "Pitch spring", Effect = FfbEffectNames.Spring, X = 1060, Y = y, Axes = FfbAxisSelection.Y,
            MinStiffness = 0, MaxStiffness = 0.5, Saturation = 0.5, CentreScale = 1
        });
        var springX = graph.Add(new SpringNode
        {
            Title = "Roll spring", Effect = FfbEffectNames.Spring, X = 1060, Y = y + 120, Axes = FfbAxisSelection.X,
            MinStiffness = 0, MaxStiffness = 0.5, Saturation = 0.5, CentreScale = 1
        });
        graph.Connect(speed, "value", aero, "in");
        graph.Connect(baseStiffness, "value", stiffness, "in");
        graph.Connect(aero, "out", stiffness, "in");
        graph.Connect(stiffness, "out", springY, "in");
        graph.Connect(stiffness, "out", springX, "in");

        // Spring centre = the game's trim (War Thunder sends it; half travel at full trim) + JoyPro's stick trim.
        var gamePitch = graph.Add(new ScaleOffsetNode { Title = "Game pitch trim x0.5", Scale = 0.5, X = 300, Y = trimPitch.Y });
        var gameRoll = graph.Add(new ScaleOffsetNode { Title = "Game roll trim x0.5", Scale = 0.5, X = 300, Y = trimRoll.Y });
        graph.Connect(trimPitch, "value", gamePitch, "in");
        graph.Connect(trimRoll, "value", gameRoll, "in");
        AddStickTrim(graph, gamePitch, gameRoll, springY, springX, 40 + 120 * row);
        return graph;
    }

    /// <summary>Titles of the trim button nodes of the built-in graph (the GUI's "assign trim buttons" looks them up).</summary>
    public static class TrimButtons
    {
        public const string Here = "Trim: to stick position";
        public const string Up = "Trim: nose down (hat up)";
        public const string Down = "Trim: nose up (hat down)";
        public const string Left = "Trim: left";
        public const string Right = "Trim: right";
        public const string Reset = "Trim: reset";

        public static IReadOnlyList<(string Title, string Port)> All { get; } =
            [(Here, "here"), (Up, "up"), (Down, "down"), (Left, "left"), (Right, "right"), (Reset, "reset")];
    }

    // The stick trim: six unassigned button nodes (assign them with "press the button now" in the editor) into a Trim
    // node, whose centre is added to the game's trim and goes into the springs' "centre".
    private static void AddStickTrim(FfbGraph graph, FfbNode gamePitch, FfbNode gameRoll, SpringNode springY, SpringNode springX, double top)
    {
        var trim = graph.Add(new TrimNode { Title = "Stick trim", X = 560, Y = top });
        var y = top;
        foreach (var (title, port) in TrimButtons.All)
        {
            var button = graph.Add(new DeviceButtonNode { Title = title, X = 300, Y = y });
            graph.Connect(button, "held", trim, port);
            y += 90;
        }

        var pitchCentre = graph.Add(new AddNode { Title = "Pitch centre (game + stick trim)", X = 800, Y = springY.Y });
        var rollCentre = graph.Add(new AddNode { Title = "Roll centre (game + stick trim)", X = 800, Y = springX.Y });
        graph.Connect(gamePitch, "out", pitchCentre, "a");
        graph.Connect(trim, "y", pitchCentre, "b");
        graph.Connect(gameRoll, "out", rollCentre, "a");
        graph.Connect(trim, "x", rollCentre, "b");
        graph.Connect(pitchCentre, "out", springY, "centre");
        graph.Connect(rollCentre, "out", springX, "centre");
    }

    private static TelemetryNode Tele(string title, string key, double min, double max, double x, double y) => new()
    {
        Title = title,
        Key = key,
        Minimum = min,
        Maximum = max,
        Mode = TelemetryNormalisation.Unsigned,
        X = x,
        Y = y
    };

    /// <summary>A curve through (0,0) and (1,1) with the two inner handles given (ease-in for onsets).</summary>
    private static CurveNode Curve(string title, double x, double y, double outX, double outY, double inX, double inY)
    {
        var node = new CurveNode { Title = title, Symmetric = false, X = x, Y = y };
        node.Curve.Nodes.Clear();
        node.Curve.Nodes.Add(new BezierNode(new CurvePoint(0, 0), new CurvePoint(0, 0), new CurvePoint(outX, outY)));
        node.Curve.Nodes.Add(new BezierNode(new CurvePoint(1, 1), new CurvePoint(inX, inY), new CurvePoint(1, 1)));
        return node;
    }
}
