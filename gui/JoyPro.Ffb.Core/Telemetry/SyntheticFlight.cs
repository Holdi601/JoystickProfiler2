using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry;

/// <summary>One moment of the synthetic test flight.</summary>
public sealed record FlightState(
    double Time,
    string Phase,
    double IasMs,
    double AoaDeg,
    double G,
    double Gear,
    double Flaps,
    bool OnGround,
    double GroundSpeedMs,
    int RoundsFired,
    int Hits,
    double Agl);

/// <summary>A scripted flight for tests without the games: the same story as DCS lines, IL-2 packets and War Thunder
/// JSON, so every source and the built-in graph can be checked end to end (JoyPro.Ffb simulate, the smoke test and the
/// unit tests use it).</summary>
/// <remarks>
/// A 12 second loop:
/// <code>
///  0 -  2 s  cruise        150 m/s, AoA 3 deg, 1 G, gear up
///  2 -  5 s  pull          AoA 3 -> 22 deg, 1 -> 6 G (buffet from 10 deg)
///  5 -  6 s  recover       AoA 3 deg, 1 G
///  6 -  7 s  gear down     gear 0 -> 1, flaps 0 -> 0.5 (moving)
///  7 -  8 s  guns          20 rounds a second (IL-2: GUN_FIRE events; DCS: shells counting down), one hit at 7.5 s
///  8 - 10 s  landing roll  on the ground, 60 -> 10 m/s
/// 10 - 12 s  stopped       on the ground, 0 m/s
/// </code>
/// </remarks>
public static class SyntheticFlight
{
    public const double LoopSeconds = 12;
    public const int StartShells = 1000;

    public static FlightState At(double time)
    {
        var loop = Math.Floor(time / LoopSeconds);
        var t = time - loop * LoopSeconds;
        var roundsBefore = (int)loop * 20;
        var hitsBefore = (int)loop;
        if (t < 2)
            return new(time, "cruise", 150, 3, 1, 0, 0, false, 150, roundsBefore, hitsBefore, 1500);
        if (t < 5)
        {
            var k = (t - 2) / 3;
            return new(time, "pull", 150 - 30 * k, 3 + 19 * k, 1 + 5 * k, 0, 0, false, 150 - 30 * k, roundsBefore, hitsBefore, 1500);
        }

        if (t < 6)
            return new(time, "recover", 120, 3, 1, 0, 0, false, 120, roundsBefore, hitsBefore, 1500);
        if (t < 7)
        {
            var k = t - 6;
            return new(time, "gear", 90, 4, 1, k, 0.5 * k, false, 90, roundsBefore, hitsBefore, 300);
        }

        if (t < 8)
        {
            var rounds = (int)Math.Floor((t - 7) * 20);
            return new(time, "guns", 90, 4, 1, 1, 0.5, false, 90, roundsBefore + rounds, hitsBefore + (t >= 7.5 ? 1 : 0), 300);
        }

        if (t < 10)
        {
            var speed = 60 - 25 * (t - 8);
            return new(time, "landing roll", speed, 0, 1, 1, 0.5, true, speed, roundsBefore + 20, hitsBefore + 1, 0);
        }

        return new(time, "stopped", 0, 0, 1, 1, 0.5, true, 0, roundsBefore + 20, hitsBefore + 1, 0);
    }

    /// <summary>The DCS hook's line for this moment.</summary>
    public static string DcsLine(FlightState s)
    {
        var ci = CultureInfo.InvariantCulture;
        var rod = s.OnGround ? 0.3 : 0;
        return string.Create(ci,
            $"t={s.Time:0.###};ias={s.IasMs:0.###};tas={s.IasMs * 1.05:0.###};mach={s.IasMs / 340:0.####};aoa={s.AoaDeg * Math.PI / 180:0.#####};aos=0;" +
            $"g={s.G:0.###};vv=0;agl={s.Agl:0.#};alt={s.Agl + 100:0.#};gs={s.GroundSpeedMs:0.###};gear={s.Gear:0.###};flaps={s.Flaps:0.###};speedbrake=0;" +
            $"rod_l={rod};rod_r={rod};rod_n={rod};shells={StartShells - s.RoundsFired};stall={(s.AoaDeg > 18 ? 1 : 0)};fail=0;rpm_l=90;rpm_r=90");
    }

    /// <summary>An IL-2 telemetrydevice packet (indicators EAS, AOA, ACCELERATION, LGEARS_STATE/PRESS, FLAPS, AGL;
    /// one GUN_FIRE event per round since <paramref name="previous"/> and one HIT event per new hit).</summary>
    public static byte[] Il2Packet(FlightState s, FlightState? previous)
    {
        var indicators = new List<(ushort Id, float[] Values)>
        {
            (6, [(float)s.IasMs]),
            (7, [(float)(s.AoaDeg * Math.PI / 180)]),
            (8, [0f, (float)(s.G * 9.80665), 0f]),
            (4, [(float)s.Gear, (float)s.Gear, (float)s.Gear]),
            (5, s.OnGround ? [0.5f, 0.5f, 0.4f] : [0f, 0f, 0f]),
            (11, [(float)s.Flaps]),
            (10, [(float)s.Agl])
        };
        var events = new List<(ushort Id, byte[] Data)>();
        var fired = previous is null ? 0 : Math.Clamp(s.RoundsFired - previous.RoundsFired, 0, 60);
        for (var i = 0; i < fired; i++)
            events.Add((9, [(byte)(i % 2)]));
        var hits = previous is null ? 0 : Math.Clamp(s.Hits - previous.Hits, 0, 10);
        for (var i = 0; i < hits; i++)
            events.Add((6, new byte[24]));

        var size = 11 + indicators.Sum(x => 3 + 4 * x.Values.Length) + 1 + events.Sum(e => 3 + e.Data.Length);
        var packet = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 0x54000101);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)size);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), (uint)Math.Max(0, Math.Round(s.Time * 50)));
        packet[10] = (byte)indicators.Count;
        var offset = 11;
        foreach (var (id, values) in indicators)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset), id);
            packet[offset + 2] = (byte)values.Length;
            offset += 3;
            foreach (var value in values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset), value);
                offset += 4;
            }
        }

        packet[offset++] = (byte)events.Count;
        foreach (var (id, data) in events)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(offset), id);
            packet[offset + 2] = (byte)data.Length;
            data.CopyTo(packet.AsSpan(offset + 3));
            offset += 3 + data.Length;
        }

        return packet;
    }

    /// <summary>War Thunder's /state for this moment ("valid": false while <paramref name="inFlight"/> is false).</summary>
    public static string WarThunderState(FlightState s, bool inFlight = true)
    {
        if (!inFlight)
            return "{\"valid\": false}";
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("{\"valid\": true, ");
        sb.Append(ci, $"\"H, m\": {Math.Round(s.Agl + 100)}, \"TAS, km/h\": {Math.Round(s.IasMs * 3.6 * 1.05)}, \"IAS, km/h\": {Math.Round(s.IasMs * 3.6)}, ");
        sb.Append(ci, $"\"M\": {s.IasMs / 340:0.00}, \"AoA, deg\": {s.AoaDeg:0.0}, \"AoS, deg\": 0.0, \"Ny\": {s.G:0.00}, \"Vy, m/s\": 0.0, \"Wx, deg/s\": 0, ");
        sb.Append(ci, $"\"aileron, %\": 0, \"elevator, %\": -10, \"rudder, %\": 0, \"flaps, %\": {Math.Round(s.Flaps * 100)}, \"gear, %\": {Math.Round(s.Gear * 100)}, \"airbrake, %\": 0, ");
        sb.Append("\"throttle 1, %\": 90, \"RPM 1\": 2600}");
        return sb.ToString();
    }

    /// <summary>War Thunder's /indicators for this moment (a cockpit with a radio altimeter, trim indicator and round counter).</summary>
    public static string WarThunderIndicators(FlightState s, bool inFlight = true)
    {
        if (!inFlight)
            return "{\"valid\": false}";
        var ci = CultureInfo.InvariantCulture;
        return string.Create(ci,
            $"{{\"valid\": true, \"type\": \"synthetic_fighter\", \"speed\": {s.IasMs:0.0}, \"radio_altitude\": {(s.OnGround ? 1.6 : s.Agl):0.0}, " +
            $"\"g_meter\": {s.G:0.000}, \"gears\": {s.Gear:0.00}, \"flaps\": {s.Flaps:0.00}, \"trimmer\": 0.10, \"ammo_counter1\": {StartShells - s.RoundsFired}}}");
    }
}
