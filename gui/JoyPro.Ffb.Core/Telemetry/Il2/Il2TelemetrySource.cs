using System.Globalization;
using System.Numerics;

namespace JoyPro.Ffb.Core.Telemetry.Il2;

public enum Il2PacketKind
{
    Invalid,
    Ffb,
    Motion,
    Telemetry
}

/// <summary>Live IL-2 telemetry: feed it the datagrams of the motion, telemetry and ffbdevice outputs.</summary>
/// <remarks>
/// The three outputs may share one port (the packet id tells them apart) or use three; values of all of them are
/// merged into one frame. Events become counters that only grow (shells fired, hits taken), so a
/// CounterDeltaToEvent node in "Increase" mode turns them back into one event per bullet.
/// The ffbdevice values are what the game itself would play on the stick; with the game's own FFB muted, a graph
/// can rebuild (and reshape) it from these keys.
/// Ingest is thread-safe (one UDP thread per port is fine); readers never block.
/// </remarks>
public sealed class Il2TelemetrySource : FrameTelemetrySource
{
    private const double Gravity = 9.80665;

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private double _time;

    public Il2TelemetrySource(IFfbClock clock, string name = "IL-2")
        : base(name, Catalogue, clock)
    {
    }

    public static IReadOnlyList<TelemetryDescriptor> Catalogue { get; } = BuildCatalogue();

    /// <summary>The object the player controls (SET_FOCUS), e.g. the aircraft name.</summary>
    public string? FocusObject { get; private set; }

    public string? ServerAddress { get; private set; }
    public string? ServerTitle { get; private set; }
    public string? LastError { get; private set; }
    public long PacketsAccepted { get; private set; }
    public long PacketsRejected { get; private set; }

    public Il2PacketKind Ingest(ReadOnlySpan<byte> packet)
    {
        lock (_gate)
        {
            var kind = IngestLocked(packet);
            if (kind == Il2PacketKind.Invalid)
            {
                PacketsRejected++;
                return kind;
            }

            PacketsAccepted++;
            Publish(new TelemetryFrame(_time, _values));
            return kind;
        }
    }

    private Il2PacketKind IngestLocked(ReadOnlySpan<byte> packet)
    {
        string? error;
        var id = Il2PacketIds.Peek(packet);
        if (id == Il2PacketIds.Ffb)
        {
            if (Il2FfbPacket.TryParse(packet, out var ffb, out error))
            {
                Apply(ffb!);
                return Il2PacketKind.Ffb;
            }
        }
        else if (id == Il2PacketIds.Motion)
        {
            if (Il2MotionPacket.TryParse(packet, out var motion, out error))
            {
                Apply(motion!);
                return Il2PacketKind.Motion;
            }
        }
        else if (id == Il2PacketIds.Telemetry)
        {
            if (Il2TelemetryPacket.TryParse(packet, out var telemetry, out error))
            {
                Apply(telemetry!);
                return Il2PacketKind.Telemetry;
            }
        }
        else
        {
            error = id is null ? "Datagram too short." : $"Unknown packet id 0x{id:X8}.";
        }

        LastError = error;
        return Il2PacketKind.Invalid;
    }

    private void Apply(Il2FfbPacket packet)
    {
        _time = packet.TimeSeconds;
        RemovePrefix("il2.ffb.");
        _values["il2.ffb.tick"] = packet.Tick;
        _values["il2.ffb.records"] = packet.Records.Count;
        foreach (var r in packet.Records)
        {
            var type = r.ForceType switch
            {
                (byte)Il2FfbForceType.Spring => "spring",
                (byte)Il2FfbForceType.Constant => "const",
                (byte)Il2FfbForceType.Damper => "damper",
                var other => "type" + other.ToString(CultureInfo.InvariantCulture)
            };
            var prefix = FfbPrefix(r.Device, r.Axis, type);
            _values[prefix + "pos"] = r.Position;
            _values[prefix + "force"] = r.Force;
            _values[prefix + "amp"] = r.Amplification;
            _values[prefix + "freq"] = r.Frequency;
        }
    }

    private void Apply(Il2MotionPacket packet)
    {
        _time = packet.TimeSeconds;
        _values["il2.motion.tick"] = packet.Tick;
        _values["il2.motion.yaw"] = packet.Yaw;
        _values["il2.motion.pitch"] = packet.Pitch;
        _values["il2.motion.roll"] = packet.Roll;
        SetVector("il2.motion.spin", packet.Spin);
        SetVector("il2.motion.acc", packet.Acceleration);
    }

    private void Apply(Il2TelemetryPacket packet)
    {
        _time = packet.TimeSeconds;
        _values["il2.tick"] = packet.Tick;
        foreach (var indicator in packet.Indicators)
            Apply(indicator);
        foreach (var e in packet.Events)
            Apply(e);
    }

    private void Apply(Il2Indicator indicator)
    {
        var v = indicator.Values;
        float At(int i) => i < v.Length ? v[i] : 0f;
        switch (indicator.Kind)
        {
            case Il2IndicatorId.EngineRpm: SetIndexed("il2.eng.rpm.", v); _values["il2.eng.count"] = v.Length; break;
            case Il2IndicatorId.EngineManifoldPressure: SetIndexed("il2.eng.mp.", v); break;
            case Il2IndicatorId.EngineShakeFrequency: SetIndexed("il2.eng.shake_freq.", v); break;
            case Il2IndicatorId.EngineShakeAmplitude: SetIndexed("il2.eng.shake_amp.", v); break;
            case Il2IndicatorId.LandingGearState: SetIndexed("il2.gear.state.", v); break;
            case Il2IndicatorId.LandingGearPressure: SetIndexed("il2.gear.press.", v); break;
            case Il2IndicatorId.Eas: _values["il2.eas"] = At(0); break;
            case Il2IndicatorId.Aoa: _values["il2.aoa"] = At(0); break;
            case Il2IndicatorId.Acceleration:
                var acc = new Vector3(At(0), At(1), At(2));
                SetVector("il2.acc", acc);
                _values["il2.acc.g"] = acc.Length() / Gravity;
                break;
            case Il2IndicatorId.CockpitShake:
                _values["il2.cockpit_shake.freq"] = At(0);
                _values["il2.cockpit_shake.amp"] = At(1);
                break;
            case Il2IndicatorId.Agl: _values["il2.agl"] = At(0); break;
            case Il2IndicatorId.Flaps: _values["il2.flaps"] = At(0); break;
            case Il2IndicatorId.AirBrakes: _values["il2.airbrakes"] = At(0); break;
            case Il2IndicatorId.Aos: _values["il2.aos"] = At(0); break;
            case Il2IndicatorId.VerticalSpeed: _values["il2.vs"] = At(0); break;
            default:
                SetIndexed("il2.ind." + indicator.Id.ToString(CultureInfo.InvariantCulture) + ".", v);
                break;
        }
    }

    private void Apply(Il2Event e)
    {
        switch (e.Kind)
        {
            case Il2EventId.SetFocus:
                FocusObject = Il2EventData.String(e);
                Count("il2.ev.focus");
                break;
            case Il2EventId.GunFire:
                Count("il2.ev.gun_fire");
                if (Il2EventData.TryGunFire(e, out var gun))
                {
                    _values["il2.ev.gun_fire.last_gun"] = gun;
                    Count("il2.ev.gun_fire.g" + gun.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case Il2EventId.Hit:
                Count("il2.ev.hit");
                if (Il2EventData.TryHit(e, out var hit))
                    _values["il2.ev.hit.force"] = hit.Force.Length();
                break;
            case Il2EventId.Damage:
                Count("il2.ev.damage");
                if (Il2EventData.TryHit(e, out var damage))
                    _values["il2.ev.damage.force"] = damage.Force.Length();
                break;
            case Il2EventId.Explosion:
                Count("il2.ev.explosion");
                if (Il2EventData.TryExplosion(e, out var explosion))
                    _values["il2.ev.explosion.radius"] = explosion.Radius;
                break;
            case Il2EventId.DropBomb:
                Count("il2.ev.bomb");
                if (Il2EventData.TryDrop(e, out var bomb))
                    _values["il2.ev.bomb.mass"] = bomb.Mass;
                break;
            case Il2EventId.RocketLaunch:
                Count("il2.ev.rocket");
                if (Il2EventData.TryDrop(e, out var rocket))
                    _values["il2.ev.rocket.mass"] = rocket.Mass;
                break;
            case Il2EventId.ServerAddress:
                ServerAddress = Il2EventData.String(e);
                break;
            case Il2EventId.ServerTitle:
                ServerTitle = Il2EventData.String(e);
                break;
        }
    }

    private void Count(string key) => _values[key] = _values.GetValueOrDefault(key) + 1;

    private void SetVector(string prefix, Vector3 v)
    {
        _values[prefix + ".x"] = v.X;
        _values[prefix + ".y"] = v.Y;
        _values[prefix + ".z"] = v.Z;
    }

    private void SetIndexed(string prefix, float[] values)
    {
        RemovePrefix(prefix);
        for (var i = 0; i < values.Length; i++)
            _values[prefix + i.ToString(CultureInfo.InvariantCulture)] = values[i];
    }

    private void RemovePrefix(string prefix)
    {
        foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _values.Remove(key);
    }

    public static string FfbPrefix(int device, int axis, string type) =>
        string.Create(CultureInfo.InvariantCulture, $"il2.ffb.d{device}.a{axis}.{type}.");

    private static IReadOnlyList<TelemetryDescriptor> BuildCatalogue()
    {
        const string flight = "Flight";
        const string engine = "Engine";
        const string motion = "Motion";
        const string events = "Events";
        const string ffb = "Game FFB";
        const string tele = "telemetrydevice";
        var list = new List<TelemetryDescriptor>
        {
            new("il2.eas", "Equivalent airspeed", "m/s", 0, 250, flight, tele + " EAS (6)"),
            new("il2.aoa", "Angle of attack", "rad", -0.5, 0.5, flight, tele + " AOA (7)"),
            new("il2.aos", "Angle of sideslip", "rad", -0.5, 0.5, flight, tele + " AOS (13)"),
            new("il2.acc.x", "Acceleration X (incl. gravity)", "m/s2", -100, 100, flight, tele + " ACCELERATION (8)"),
            new("il2.acc.y", "Acceleration Y (incl. gravity)", "m/s2", -100, 100, flight, tele + " ACCELERATION (8)"),
            new("il2.acc.z", "Acceleration Z (incl. gravity)", "m/s2", -100, 100, flight, tele + " ACCELERATION (8)"),
            new("il2.acc.g", "G load (|acceleration| / g)", "G", 0, 9, flight, "derived from ACCELERATION (8)"),
            new("il2.agl", "Altitude above ground", "m", 0, 10000, flight, tele + " AGL (10)"),
            new("il2.vs", "Vertical speed", "m/s", -100, 100, flight, tele + " VERTICAL_SPEED (14)"),
            new("il2.flaps", "Flaps", "0..1", 0, 1, flight, tele + " FLAPS (11)"),
            new("il2.airbrakes", "Air brakes", "0..1", 0, 1, flight, tele + " AIR_BRAKES (12)"),
            new("il2.cockpit_shake.freq", "Cockpit shake frequency", "Hz", 0, 100, flight, tele + " COCKPIT_SHAKE (9)"),
            new("il2.cockpit_shake.amp", "Cockpit shake amplitude", "0..1", 0, 1, flight, tele + " COCKPIT_SHAKE (9)"),
            new("il2.eng.count", "Engines", "", 0, 4, engine, tele + " ENG_RPM (0) values count"),
        };

        for (var i = 0; i < 4; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new($"il2.eng.rpm.{i}", "Engine " + n + " RPM", "rpm", 0, 4000, engine, tele + " ENG_RPM (0)"));
            list.Add(new($"il2.eng.mp.{i}", "Engine " + n + " manifold pressure", "", 0, 2, engine, tele + " ENG_MP (1)"));
            list.Add(new($"il2.eng.shake_freq.{i}", "Engine " + n + " shake frequency", "Hz", 0, 100, engine, tele + " ENG_SHAKE_FRQ (2)"));
            list.Add(new($"il2.eng.shake_amp.{i}", "Engine " + n + " shake amplitude", "0..1", 0, 1, engine, tele + " ENG_SHAKE_AMP (3)"));
        }

        for (var i = 0; i < 3; i++)
        {
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            list.Add(new($"il2.gear.state.{i}", "Gear " + n + " extension", "0..1", 0, 1, flight, tele + " LGEARS_STATE (4)"));
            list.Add(new($"il2.gear.press.{i}", "Gear " + n + " pressure", "0..1", 0, 1, flight, tele + " LGEARS_PRESS (5)"));
        }

        list.AddRange(
        [
            new("il2.ev.gun_fire", "Rounds fired (count)", "rounds", 0, 1000, events, tele + " GUN_FIRE (9), one per bullet"),
            new("il2.ev.gun_fire.last_gun", "Last gun fired (index)", "", 0, 8, events, tele + " GUN_FIRE (9)"),
            new("il2.ev.hit", "Hits (count)", "", 0, 1000, events, tele + " HIT (6)"),
            new("il2.ev.hit.force", "Last hit force", "N", 0, 100000, events, tele + " HIT (6) |afHitF|"),
            new("il2.ev.damage", "Damage events (count)", "", 0, 1000, events, tele + " DAMAGE (7)"),
            new("il2.ev.damage.force", "Last damage force", "N", 0, 100000, events, tele + " DAMAGE (7) |afHitF|"),
            new("il2.ev.explosion", "Explosions (count)", "", 0, 1000, events, tele + " EXPLOSION (8)"),
            new("il2.ev.explosion.radius", "Last explosion radius", "m", 0, 100, events, tele + " EXPLOSION (8)"),
            new("il2.ev.bomb", "Bombs dropped (count)", "", 0, 100, events, tele + " DROP_BOMB (4)"),
            new("il2.ev.bomb.mass", "Last bomb mass", "kg", 0, 2000, events, tele + " DROP_BOMB (4)"),
            new("il2.ev.rocket", "Rockets launched (count)", "", 0, 100, events, tele + " ROCKET_LAUNCH (5)"),
            new("il2.ev.rocket.mass", "Last rocket mass", "kg", 0, 500, events, tele + " ROCKET_LAUNCH (5)"),
            new("il2.ev.focus", "Focus changes (count)", "", 0, 100, events, tele + " SET_FOCUS (0)"),
            new("il2.motion.yaw", "Yaw", "rad", -Math.PI, Math.PI, motion, "motiondevice Yaw"),
            new("il2.motion.pitch", "Pitch", "rad", -Math.PI / 2, Math.PI / 2, motion, "motiondevice Pitch"),
            new("il2.motion.roll", "Roll", "rad", -Math.PI, Math.PI, motion, "motiondevice Roll"),
            new("il2.motion.spin.x", "Spin X (body)", "rad/s", -5, 5, motion, "motiondevice Spin_x"),
            new("il2.motion.spin.y", "Spin Y (body)", "rad/s", -5, 5, motion, "motiondevice Spin_y"),
            new("il2.motion.spin.z", "Spin Z (body)", "rad/s", -5, 5, motion, "motiondevice Spin_z"),
            new("il2.motion.acc.x", "Acceleration X (body)", "m/s2", -100, 100, motion, "motiondevice Acc_x"),
            new("il2.motion.acc.y", "Acceleration Y (body)", "m/s2", -100, 100, motion, "motiondevice Acc_y"),
            new("il2.motion.acc.z", "Acceleration Z (body)", "m/s2", -100, 100, motion, "motiondevice Acc_z"),
        ]);

        for (var device = 0; device < 2; device++)
        {
            for (var axis = 0; axis < 2; axis++)
            {
                var where = $"joy{device} axis {(axis == 0 ? "X" : "Y")}";
                foreach (var (type, title) in new[] { ("spring", "spring"), ("const", "constant force"), ("damper", "damper") })
                {
                    var p = FfbPrefix(device, axis, type);
                    list.Add(new(p + "force", $"Game {title} force, {where}", "", -1, 1, ffb, "ffbdevice force"));
                    list.Add(new(p + "pos", $"Game {title} position/direction, {where}", "", -1, 1, ffb, "ffbdevice pos"));
                    list.Add(new(p + "amp", $"Game {title} amplification, {where}", "", 0, 1, ffb, "ffbdevice amp"));
                    list.Add(new(p + "freq", $"Game {title} frequency, {where}", "Hz", 0, 100, ffb, "ffbdevice freq"));
                }
            }
        }

        return list;
    }
}
