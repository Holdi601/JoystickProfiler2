namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>The normalised aircraft model: the same keys ("ac.*", SI units) for every game, so one graph works in DCS,
/// IL-2 and War Thunder. Each game source fills what its telemetry has; the rest stays absent (a Telemetry node then
/// reads 0 and "valid" 0).</summary>
/// <remarks>
/// Values come from a per-game <see cref="AircraftTelemetryDeriver"/> mapping plus derived values computed over time:
/// gear/flaps/speed brake moving (a change within the last <see cref="AircraftTelemetryDeriver.MovingHoldSeconds"/>),
/// guns firing (rounds fired within <see cref="AircraftTelemetryDeriver.FiringHoldSeconds"/>; DCS counts the cannon
/// shells going down, IL-2 counts GUN_FIRE events), a hit pulse and the ground roll speed (ground speed while on the
/// ground).
/// </remarks>
public static class AircraftTelemetry
{
    public const string Prefix = "ac.";

    public const string Ias = "ac.ias";
    public const string Tas = "ac.tas";
    public const string Mach = "ac.mach";
    public const string Aoa = "ac.aoa";
    public const string Aos = "ac.aos";
    public const string G = "ac.g";
    public const string VerticalSpeed = "ac.vs";
    public const string Agl = "ac.agl";
    public const string Altitude = "ac.alt";
    public const string Gear = "ac.gear";
    public const string GearMoving = "ac.gear_moving";
    public const string Flaps = "ac.flaps";
    public const string FlapsMoving = "ac.flaps_moving";
    public const string SpeedBrake = "ac.speedbrake";
    public const string SpeedBrakeMoving = "ac.speedbrake_moving";
    public const string OnGround = "ac.on_ground";
    public const string GroundSpeed = "ac.ground_speed";
    public const string GroundRoll = "ac.ground_roll";
    public const string Ammo = "ac.ammo";
    public const string RoundsFired = "ac.rounds_fired";
    public const string Firing = "ac.firing";
    public const string StallWarning = "ac.stall_warning";
    public const string Hits = "ac.hits";
    public const string Hit = "ac.hit";
    public const string Damage = "ac.damage";
    public const string TrimPitch = "ac.trim_pitch";
    public const string TrimRoll = "ac.trim_roll";
    public const string TrimYaw = "ac.trim_yaw";
    public const string Rpm = "ac.rpm";
    public const string ShakeAmplitude = "ac.shake_amp";
    public const string ShakeFrequency = "ac.shake_freq";

    private const string Flight = "Aircraft";
    private const string Mech = "Aircraft mechanics";
    private const string Weapons = "Aircraft weapons";

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } =
    [
        new(Ias, "Indicated airspeed", "m/s", 0, 250, Flight, "DCS ias, IL-2 EAS, War Thunder IAS"),
        new(Tas, "True airspeed", "m/s", 0, 400, Flight, "DCS tas, War Thunder TAS"),
        new(Mach, "Mach number", "", 0, 2, Flight, "DCS, War Thunder"),
        new(Aoa, "Angle of attack", "deg", -10, 30, Flight, "all"),
        new(Aos, "Angle of sideslip", "deg", -20, 20, Flight, "all"),
        new(G, "G load (normal)", "G", -3, 9, Flight, "DCS Ny, IL-2 |acceleration|, War Thunder Ny"),
        new(VerticalSpeed, "Vertical speed", "m/s", -100, 100, Flight, "all"),
        new(Agl, "Height above ground", "m", 0, 3000, Flight, "DCS, IL-2"),
        new(Altitude, "Altitude above sea level", "m", 0, 12000, Flight, "DCS, War Thunder"),
        new(Gear, "Gear extended", "0..1", 0, 1, Mech, "all"),
        new(GearMoving, "Gear moving", "0/1", 0, 1, Mech, "derived"),
        new(Flaps, "Flaps extended", "0..1", 0, 1, Mech, "all"),
        new(FlapsMoving, "Flaps moving", "0/1", 0, 1, Mech, "derived"),
        new(SpeedBrake, "Speed brake extended", "0..1", 0, 1, Mech, "all"),
        new(SpeedBrakeMoving, "Speed brake moving", "0/1", 0, 1, Mech, "derived"),
        new(OnGround, "On the ground", "0/1", 0, 1, Mech, "DCS gear struts, IL-2 gear pressure, War Thunder radio altimeter"),
        new(GroundSpeed, "Ground speed", "m/s", 0, 120, Mech, "DCS velocity, IL-2 EAS, War Thunder IAS"),
        new(GroundRoll, "Ground roll speed (0 in the air)", "m/s", 0, 120, Mech, "derived"),
        new(Ammo, "Cannon rounds left", "rounds", 0, 2000, Weapons, "DCS shells, War Thunder ammo counters (some cockpits)"),
        new(RoundsFired, "Rounds fired (count)", "rounds", 0, 2000, Weapons, "derived; IL-2 GUN_FIRE"),
        new(Firing, "Guns firing", "0/1", 0, 1, Weapons, "derived"),
        new(StallWarning, "Stall warning", "0/1", 0, 1, Flight, "DCS StallSignalization, War Thunder"),
        new(Hits, "Hits taken (count)", "", 0, 100, Weapons, "IL-2 HIT/DAMAGE"),
        new(Hit, "Hit (pulse)", "0/1", 0, 1, Weapons, "derived"),
        new(Damage, "Failed systems", "", 0, 10, Weapons, "DCS LoGetMCPState failures"),
        new(TrimPitch, "Pitch trim", "-1..1", -1, 1, Mech, "War Thunder trimmer gauge (some cockpits)"),
        new(TrimRoll, "Roll trim", "-1..1", -1, 1, Mech, "none of the phase 1 games (MSFS, X-Plane later)"),
        new(TrimYaw, "Yaw trim", "-1..1", -1, 1, Mech, "none of the phase 1 games (MSFS, X-Plane later)"),
        new(Rpm, "Engine RPM (highest engine)", "0..1", 0, 1.1, Flight, "DCS"),
        new(ShakeAmplitude, "Cockpit shake amplitude", "0..1", 0, 1, Flight, "IL-2 COCKPIT_SHAKE"),
        new(ShakeFrequency, "Cockpit shake frequency", "Hz", 0, 100, Flight, "IL-2 COCKPIT_SHAKE"),
    ];

    public static TelemetryDescriptor? Find(string key) => Descriptors.FirstOrDefault(d => d.Key == key);
}

/// <summary>Fills the "ac.*" keys from a game's raw keys and derives the values that need time.</summary>
public sealed class AircraftTelemetryDeriver : ITelemetryEnricher
{
    public const double MovingHoldSeconds = 0.3;
    public const double FiringHoldSeconds = 0.15;
    public const double HitHoldSeconds = 0.15;

    /// <summary>A larger jump of the rounds-left counter is a rearm or a new aircraft, not fire.</summary>
    public const double MaxRoundsPerFrame = 150;

    private readonly Action<IReadOnlyDictionary<string, double>, IDictionary<string, double>> _map;
    private readonly object _gate = new();
    private Mover _gear;
    private Mover _flaps;
    private Mover _speedBrake;
    private double? _lastTime;
    private double? _lastAmmo;
    private double _roundsFromAmmo;
    private double? _lastRounds;
    private double _firingUntil = double.NegativeInfinity;
    private double? _lastHits;
    private double _hitUntil = double.NegativeInfinity;

    public AircraftTelemetryDeriver(string game, Action<IReadOnlyDictionary<string, double>, IDictionary<string, double>> map)
    {
        Game = game;
        _map = map;
    }

    public string Game { get; }

    public IReadOnlyList<TelemetryDescriptor> Descriptors => AircraftTelemetry.Descriptors;

    public void Reset()
    {
        lock (_gate)
        {
            _gear = default;
            _flaps = default;
            _speedBrake = default;
            _lastTime = null;
            _lastAmmo = null;
            _roundsFromAmmo = 0;
            _lastRounds = null;
            _firingUntil = double.NegativeInfinity;
            _lastHits = null;
            _hitUntil = double.NegativeInfinity;
        }
    }

    public TelemetryFrame Enrich(TelemetryFrame frame)
    {
        var values = new Dictionary<string, double>(frame.Values, StringComparer.Ordinal);
        lock (_gate)
        {
            if (_lastTime is { } last && frame.Time < last - 1e-6)
                ResetLocked();
            _lastTime = frame.Time;
            _map(frame.Values, values);
            Derive(frame.Time, values);
        }

        return new TelemetryFrame(frame.Time, values);
    }

    private void ResetLocked()
    {
        _gear = default;
        _flaps = default;
        _speedBrake = default;
        _lastAmmo = null;
        _roundsFromAmmo = 0;
        _lastRounds = null;
        _firingUntil = double.NegativeInfinity;
        _lastHits = null;
        _hitUntil = double.NegativeInfinity;
    }

    private void Derive(double t, Dictionary<string, double> v)
    {
        Moving(ref _gear, t, v, AircraftTelemetry.Gear, AircraftTelemetry.GearMoving);
        Moving(ref _flaps, t, v, AircraftTelemetry.Flaps, AircraftTelemetry.FlapsMoving);
        Moving(ref _speedBrake, t, v, AircraftTelemetry.SpeedBrake, AircraftTelemetry.SpeedBrakeMoving);

        // Rounds fired: a counter of the game (IL-2) or the rounds left going down (DCS).
        if (!v.ContainsKey(AircraftTelemetry.RoundsFired) && v.TryGetValue(AircraftTelemetry.Ammo, out var ammo))
        {
            if (_lastAmmo is { } previous)
            {
                var spent = previous - ammo;
                if (spent > 0 && spent <= MaxRoundsPerFrame)
                    _roundsFromAmmo += spent;
            }

            _lastAmmo = ammo;
            v[AircraftTelemetry.RoundsFired] = _roundsFromAmmo;
        }

        if (v.TryGetValue(AircraftTelemetry.RoundsFired, out var rounds))
        {
            if (_lastRounds is { } before && rounds > before && rounds - before <= MaxRoundsPerFrame)
                _firingUntil = t + FiringHoldSeconds;
            _lastRounds = rounds;
            v[AircraftTelemetry.Firing] = t <= _firingUntil ? 1 : 0;
        }

        if (v.TryGetValue(AircraftTelemetry.Hits, out var hits))
        {
            if (_lastHits is { } before && hits > before)
                _hitUntil = t + HitHoldSeconds;
            _lastHits = hits;
            v[AircraftTelemetry.Hit] = t <= _hitUntil ? 1 : 0;
        }

        if (v.TryGetValue(AircraftTelemetry.OnGround, out var onGround) && v.TryGetValue(AircraftTelemetry.GroundSpeed, out var groundSpeed))
            v[AircraftTelemetry.GroundRoll] = onGround > 0.5 ? Math.Abs(groundSpeed) : 0;
    }

    private static void Moving(ref Mover mover, double t, Dictionary<string, double> v, string key, string movingKey)
    {
        if (!v.TryGetValue(key, out var position))
            return;
        // A jump of more than half the travel within one frame is a new aircraft or mission, not movement.
        var step = Math.Abs(position - mover.Last);
        if (mover.Known && step > 1e-4 && step <= 0.5)
            mover.Until = t + MovingHoldSeconds;
        mover.Known = true;
        mover.Last = position;
        v[movingKey] = mover.Until > 0 && t <= mover.Until ? 1 : 0;
    }

    private struct Mover
    {
        public bool Known;
        public double Last;
        public double Until;
    }

    // ------------------------------------------------------------------ per-game mappings

    private const double RadToDeg = 180 / Math.PI;

    private static void Copy(IReadOnlyDictionary<string, double> raw, IDictionary<string, double> ac, string from, string to, double scale = 1)
    {
        if (raw.TryGetValue(from, out var value) && double.IsFinite(value))
            ac[to] = value * scale;
    }

    /// <summary>DCS: the keys of the JoyPro Export.lua hook (<see cref="Dcs.DcsTelemetryProtocol.Channels"/>).</summary>
    public static AircraftTelemetryDeriver ForDcs() => new("DCS", (raw, ac) =>
    {
        Copy(raw, ac, "dcs.ias", AircraftTelemetry.Ias);
        Copy(raw, ac, "dcs.tas", AircraftTelemetry.Tas);
        Copy(raw, ac, "dcs.mach", AircraftTelemetry.Mach);
        Copy(raw, ac, "dcs.aoa", AircraftTelemetry.Aoa, RadToDeg);
        Copy(raw, ac, "dcs.aos", AircraftTelemetry.Aos, RadToDeg);
        Copy(raw, ac, "dcs.g", AircraftTelemetry.G);
        Copy(raw, ac, "dcs.vv", AircraftTelemetry.VerticalSpeed);
        Copy(raw, ac, "dcs.agl", AircraftTelemetry.Agl);
        Copy(raw, ac, "dcs.alt", AircraftTelemetry.Altitude);
        Copy(raw, ac, "dcs.gear", AircraftTelemetry.Gear);
        Copy(raw, ac, "dcs.flaps", AircraftTelemetry.Flaps);
        Copy(raw, ac, "dcs.speedbrake", AircraftTelemetry.SpeedBrake);
        Copy(raw, ac, "dcs.gs", AircraftTelemetry.GroundSpeed);
        Copy(raw, ac, "dcs.shells", AircraftTelemetry.Ammo);
        Copy(raw, ac, "dcs.stall", AircraftTelemetry.StallWarning);
        Copy(raw, ac, "dcs.fail", AircraftTelemetry.Damage);
        var rods = new[] { "dcs.rod_l", "dcs.rod_r", "dcs.rod_n" };
        var anyRod = false;
        var compressed = false;
        foreach (var rod in rods)
        {
            if (!raw.TryGetValue(rod, out var value))
                continue;
            anyRod = true;
            compressed |= value > 0.01;
        }

        if (anyRod)
            ac[AircraftTelemetry.OnGround] = compressed ? 1 : 0;
        if (raw.TryGetValue("dcs.rpm_l", out var left) | raw.TryGetValue("dcs.rpm_r", out var right))
            ac[AircraftTelemetry.Rpm] = Math.Max(left, right) / 100;
    });

    /// <summary>IL-2 Great Battles / Korea: the telemetrydevice output (<see cref="Il2.Il2TelemetrySource"/>).</summary>
    public static AircraftTelemetryDeriver ForIl2() => new("IL-2", (raw, ac) =>
    {
        Copy(raw, ac, "il2.eas", AircraftTelemetry.Ias);
        Copy(raw, ac, "il2.aoa", AircraftTelemetry.Aoa, RadToDeg);
        Copy(raw, ac, "il2.aos", AircraftTelemetry.Aos, RadToDeg);
        Copy(raw, ac, "il2.acc.g", AircraftTelemetry.G);
        Copy(raw, ac, "il2.vs", AircraftTelemetry.VerticalSpeed);
        Copy(raw, ac, "il2.agl", AircraftTelemetry.Agl);
        Copy(raw, ac, "il2.flaps", AircraftTelemetry.Flaps);
        Copy(raw, ac, "il2.airbrakes", AircraftTelemetry.SpeedBrake);
        // Event counters appear with the first event: before it they are 0 (so the first burst and hit count too).
        ac[AircraftTelemetry.RoundsFired] = raw.GetValueOrDefault("il2.ev.gun_fire");
        Copy(raw, ac, "il2.cockpit_shake.amp", AircraftTelemetry.ShakeAmplitude);
        Copy(raw, ac, "il2.cockpit_shake.freq", AircraftTelemetry.ShakeFrequency);
        double gearSum = 0;
        var gears = 0;
        var pressed = false;
        var anyPressure = false;
        foreach (var pair in raw)
        {
            if (pair.Key.StartsWith("il2.gear.state.", StringComparison.Ordinal))
            {
                gearSum += pair.Value;
                gears++;
            }
            else if (pair.Key.StartsWith("il2.gear.press.", StringComparison.Ordinal))
            {
                anyPressure = true;
                pressed |= pair.Value > 0.01;
            }
        }

        if (gears > 0)
            ac[AircraftTelemetry.Gear] = gearSum / gears;
        if (anyPressure)
            ac[AircraftTelemetry.OnGround] = pressed ? 1 : 0;
        if (raw.TryGetValue("il2.eas", out var eas))
            ac[AircraftTelemetry.GroundSpeed] = eas;
        ac[AircraftTelemetry.Hits] = raw.GetValueOrDefault("il2.ev.hit") + raw.GetValueOrDefault("il2.ev.damage");
    });

    /// <summary>War Thunder: /state and /indicators of the game's local web server (<see cref="WarThunder.WarThunderTelemetrySource"/>).</summary>
    public static AircraftTelemetryDeriver ForWarThunder() => new("War Thunder", (raw, ac) =>
    {
        Copy(raw, ac, "wt.ias_km_h", AircraftTelemetry.Ias, 1 / 3.6);
        Copy(raw, ac, "wt.tas_km_h", AircraftTelemetry.Tas, 1 / 3.6);
        Copy(raw, ac, "wt.m", AircraftTelemetry.Mach);
        Copy(raw, ac, "wt.aoa_deg", AircraftTelemetry.Aoa);
        Copy(raw, ac, "wt.aos_deg", AircraftTelemetry.Aos);
        Copy(raw, ac, "wt.ny", AircraftTelemetry.G);
        Copy(raw, ac, "wt.vy_m_s", AircraftTelemetry.VerticalSpeed);
        Copy(raw, ac, "wt.h_m", AircraftTelemetry.Altitude);
        Copy(raw, ac, "wt.gear_pct", AircraftTelemetry.Gear, 0.01);
        Copy(raw, ac, "wt.flaps_pct", AircraftTelemetry.Flaps, 0.01);
        Copy(raw, ac, "wt.airbrake_pct", AircraftTelemetry.SpeedBrake, 0.01);
        // Cockpit gauges: present only where the aircraft's cockpit has them ("trimmer" = trim indicator, range and
        // sign to be verified in the game; "ammo_counterN" = round counters).
        Copy(raw, ac, "wt.ind.trimmer", AircraftTelemetry.TrimPitch);
        double ammo = 0;
        var counters = 0;
        foreach (var pair in raw)
        {
            if (pair.Key.StartsWith("wt.ind.ammo_counter", StringComparison.Ordinal) && !pair.Key.EndsWith("_lamp", StringComparison.Ordinal))
            {
                ammo += pair.Value;
                counters++;
            }
        }

        if (counters > 0)
            ac[AircraftTelemetry.Ammo] = ammo;

        // No weight-on-wheels field: on the ground = radio altimeter under 3 m, gear down and no vertical speed.
        if (raw.TryGetValue("wt.ind.radio_altitude", out var radio) && raw.TryGetValue("wt.gear_pct", out var gear))
        {
            var level = !raw.TryGetValue("wt.vy_m_s", out var vy) || Math.Abs(vy) < 1;
            ac[AircraftTelemetry.OnGround] = radio < 3 && gear >= 99 && level ? 1 : 0;
        }

        if (raw.TryGetValue("wt.ias_km_h", out var ias))
            ac[AircraftTelemetry.GroundSpeed] = ias / 3.6;
    });
}
