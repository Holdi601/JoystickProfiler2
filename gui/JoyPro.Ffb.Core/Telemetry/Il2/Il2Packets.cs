using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace JoyPro.Ffb.Core.Telemetry.Il2;

// IL-2 Great Battles / IL-2 Series: Korea UDP outputs, as documented in <install>\Doc\UDP_Output\ (read from the
// IL2Series install; the documents name "IL2 Great Battles" and both games use the same startup.cfg keys):
//   IL2_ffbOutput.doc        [KEY = ffbdevice]       packet id 0x494D0100  (force feedback the game would play)
//   Il2_MotionOutput.odt/pdf [KEY = motiondevice]    packet id 0x494C0100  (rev. 1.0, game v3.012)
//   Il2_TeleOutput.odt/pdf   [KEY = telemetrydevice] packet id 0x54000101  (rev. 1.2, game v4.003)
// All integers and floats are little endian ("LSB"); floats are IEEE 754 single precision. Records are packed (no
// padding): the ffb record is 19 bytes with floats at odd offsets, exactly as the offset table in the .doc says.

public static class Il2PacketIds
{
    public const uint Ffb = 0x494D0100;
    public const uint Motion = 0x494C0100;
    public const uint Telemetry = 0x54000101;

    public static uint? Peek(ReadOnlySpan<byte> packet) =>
        packet.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(packet) : null;
}

// ---------------------------------------------------------------- ffbdevice ---------------------------------------

/// <summary>"currently supported force types" (IL2_ffbOutput.doc, footnote ***).</summary>
public enum Il2FfbForceType : byte
{
    Spring = 0,
    Constant = 1,
    Damper = 2
}

/// <param name="Axis">Axis id in the user's current control mapping, by default 0 = X, 1 = Y, 2 = Z ...</param>
/// <param name="Device">DirectInput ordinal as mapped in the game: 0 = joy0, 1 = joy1 ...</param>
/// <param name="ForceType">Raw force type byte; see <see cref="Il2FfbForceType"/> (unknown values are kept).</param>
/// <param name="Position">"position/direction".</param>
/// <param name="Force">"force value".</param>
/// <param name="Amplification">"amplification".</param>
/// <param name="Frequency">"frequency of periodic force".</param>
public readonly record struct Il2FfbRecord(
    byte Axis,
    byte Device,
    byte ForceType,
    float Position,
    float Force,
    float Amplification,
    float Frequency)
{
    public bool IsKnownType => ForceType <= (byte)Il2FfbForceType.Damper;
    public Il2FfbForceType Type => (Il2FfbForceType)ForceType;
}

/// <summary>An ffbdevice packet: header (12 bytes) + nRecords packed 19-byte records.</summary>
/// <remarks>
/// IL2_ffbOutput.doc, "1. UDP message format":
/// <code>
/// Header      offset size type
///   PacketID  0      4    DWORD  0x494D0100
///   Tick      4      4    DWORD  simulation frame tick, 1 unit = 1/50 s
///   nRecords  8      4    DWORD  number of FFB records in the message
/// Record (immediately following, packed)
///   axis      0      1    BYTE   axis id
///   devNo     1      1    BYTE   device id
///   nForceType 2     1    BYTE   SPRING = 0, CONST = 1, DAMPER = 2
///   pos       3      4    float  position/direction
///   force     7      4    float  force value
///   amp       11     4    float  amplification
///   freq      15     4    float  frequency of periodic force
/// </code>
/// </remarks>
public sealed record Il2FfbPacket(uint Tick, IReadOnlyList<Il2FfbRecord> Records)
{
    public const int HeaderSize = 12;
    public const int RecordSize = 19;

    /// <summary>Guard against a corrupt count (a UDP datagram holds at most ~3400 records anyway).</summary>
    public const int MaxRecords = 4096;

    public double TimeSeconds => Tick / 50.0;

    public static bool TryParse(ReadOnlySpan<byte> packet, out Il2FfbPacket? result, out string? error)
    {
        result = null;
        if (packet.Length < HeaderSize)
        {
            error = $"ffbdevice packet too short ({packet.Length} bytes, header needs {HeaderSize}).";
            return false;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        if (id != Il2PacketIds.Ffb)
        {
            error = $"Not an ffbdevice packet (id 0x{id:X8}).";
            return false;
        }

        var tick = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        if (count > MaxRecords || HeaderSize + (long)count * RecordSize > packet.Length)
        {
            error = $"ffbdevice packet announces {count} records but has {(packet.Length - HeaderSize) / RecordSize}.";
            return false;
        }

        var records = new Il2FfbRecord[count];
        for (var i = 0; i < count; i++)
        {
            var r = packet.Slice(HeaderSize + i * RecordSize, RecordSize);
            records[i] = new Il2FfbRecord(
                r[0],
                r[1],
                r[2],
                BinaryPrimitives.ReadSingleLittleEndian(r[3..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[7..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[11..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[15..]));
        }

        result = new Il2FfbPacket(tick, records);
        error = null;
        return true;
    }
}

// ---------------------------------------------------------------- motiondevice ------------------------------------

/// <summary>A motiondevice packet (44 bytes).</summary>
/// <remarks>
/// Il2_MotionOutput.odt, "3. UDP message":
/// <code>
/// Packet_ID 0  4 DWORD 0x494C0100
/// Tick      4  4 DWORD simulation frame tick, 1 unit = 1/50 s
/// Yaw       8  4 float rad     (Euler angles, world space, order Roll-Pitch-Yaw)
/// Pitch     12 4 float rad
/// Roll      16 4 float rad
/// Spin_x    20 4 float rad/s   (body frame)
/// Spin_y    24 4 float rad/s
/// Spin_z    28 4 float rad/s
/// Acc_x     32 4 float m/s^2   (body frame)
/// Acc_y     36 4 float m/s^2
/// Acc_z     40 4 float m/s^2
/// </code>
/// On pause the game sends a zero state with the previous tick. With decimation > 1 the values are averages.
/// </remarks>
public sealed record Il2MotionPacket(
    uint Tick,
    float Yaw,
    float Pitch,
    float Roll,
    Vector3 Spin,
    Vector3 Acceleration)
{
    public const int Size = 44;

    public double TimeSeconds => Tick / 50.0;

    public static bool TryParse(ReadOnlySpan<byte> packet, out Il2MotionPacket? result, out string? error)
    {
        result = null;
        if (packet.Length < Size)
        {
            error = $"motiondevice packet too short ({packet.Length} bytes, needs {Size}).";
            return false;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        if (id != Il2PacketIds.Motion)
        {
            error = $"Not a motiondevice packet (id 0x{id:X8}).";
            return false;
        }

        result = new Il2MotionPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]),
            F(packet, 8),
            F(packet, 12),
            F(packet, 16),
            new Vector3(F(packet, 20), F(packet, 24), F(packet, 28)),
            new Vector3(F(packet, 32), F(packet, 36), F(packet, 40)));
        error = null;
        return true;
    }

    private static float F(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(packet[offset..]);
}

// ---------------------------------------------------------------- telemetrydevice ---------------------------------

/// <summary>Indicator ids (Il2_TeleOutput, "3.2. Indicators list").</summary>
public enum Il2IndicatorId : ushort
{
    EngineRpm = 0,
    EngineManifoldPressure = 1,
    EngineShakeFrequency = 2,
    EngineShakeAmplitude = 3,
    LandingGearState = 4,
    LandingGearPressure = 5,
    Eas = 6,
    Aoa = 7,
    Acceleration = 8,
    CockpitShake = 9,
    Agl = 10,
    Flaps = 11,
    AirBrakes = 12,
    Aos = 13,
    VerticalSpeed = 14
}

/// <summary>Event ids (Il2_TeleOutput, "3.4. Events list").</summary>
public enum Il2EventId : ushort
{
    SetFocus = 0,
    SetupEngine = 1,
    SetupGun = 2,
    SetupLandingGear = 3,
    DropBomb = 4,
    RocketLaunch = 5,
    Hit = 6,
    Damage = 7,
    Explosion = 8,
    GunFire = 9,
    ServerAddress = 10,
    ServerTitle = 11,
    SrsAddress = 12,
    ClientData = 13,
    ControlledData = 14
}

/// <summary>SIndicator: WORD id, BYTE values count, then count x 32-bit values (floats for every documented id).</summary>
public sealed record Il2Indicator(ushort Id, float[] Values)
{
    public Il2IndicatorId Kind => (Il2IndicatorId)Id;
}

/// <summary>SEvent: WORD id, BYTE data size, then the data.</summary>
public sealed record Il2Event(ushort Id, byte[] Data)
{
    public Il2EventId Kind => (Il2EventId)Id;
}

/// <summary>A telemetrydevice packet.</summary>
/// <remarks>
/// Il2_TeleOutput.odt rev. 1.2, "3. UDP message":
/// <code>
/// Packet_ID        0  4 DWORD 0x54000101
/// Size             4  2 WORD  current message size, bytes
/// Tick             6  4 DWORD simulation frame tick, 1 unit = 1/50 s
/// Indicators_Count 10 1 BYTE  IC
/// Indicator[0..IC-1]    SIndicator { WORD Indicator_ID; BYTE Values_count; 32-bit value[VC] }
/// Events_Count        1 BYTE  EC
/// Event[0..EC-1]        SEvent { WORD Event_ID; BYTE Event_Size; BYTE data[Event_Size] }
/// </code>
/// Parsing stops at the Size field when it is smaller than the datagram (and fails when it is larger).
/// </remarks>
public sealed record Il2TelemetryPacket(
    ushort Size,
    uint Tick,
    IReadOnlyList<Il2Indicator> Indicators,
    IReadOnlyList<Il2Event> Events)
{
    public const int HeaderSize = 11;

    public double TimeSeconds => Tick / 50.0;

    public static bool TryParse(ReadOnlySpan<byte> packet, out Il2TelemetryPacket? result, out string? error)
    {
        result = null;
        if (packet.Length < HeaderSize)
        {
            error = $"telemetrydevice packet too short ({packet.Length} bytes).";
            return false;
        }

        var id = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        if (id != Il2PacketIds.Telemetry)
        {
            error = $"Not a telemetrydevice packet (id 0x{id:X8}).";
            return false;
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
        if (size < HeaderSize || size > packet.Length)
        {
            error = $"telemetrydevice packet size field {size} does not fit the datagram ({packet.Length} bytes).";
            return false;
        }

        packet = packet[..size];
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]);
        var indicatorCount = packet[10];
        var offset = HeaderSize;
        var indicators = new List<Il2Indicator>(indicatorCount);
        for (var i = 0; i < indicatorCount; i++)
        {
            if (offset + 3 > packet.Length)
                return Truncated(out error, "indicator", i);
            var indicatorId = BinaryPrimitives.ReadUInt16LittleEndian(packet[offset..]);
            var valueCount = packet[offset + 2];
            offset += 3;
            if (offset + valueCount * 4 > packet.Length)
                return Truncated(out error, "indicator values", i);
            var values = new float[valueCount];
            for (var v = 0; v < valueCount; v++)
                values[v] = BinaryPrimitives.ReadSingleLittleEndian(packet[(offset + v * 4)..]);
            offset += valueCount * 4;
            indicators.Add(new Il2Indicator(indicatorId, values));
        }

        var events = new List<Il2Event>();
        if (offset < packet.Length)
        {
            var eventCount = packet[offset++];
            for (var e = 0; e < eventCount; e++)
            {
                if (offset + 3 > packet.Length)
                    return Truncated(out error, "event", e);
                var eventId = BinaryPrimitives.ReadUInt16LittleEndian(packet[offset..]);
                var dataSize = packet[offset + 2];
                offset += 3;
                if (offset + dataSize > packet.Length)
                    return Truncated(out error, "event data", e);
                events.Add(new Il2Event(eventId, packet.Slice(offset, dataSize).ToArray()));
                offset += dataSize;
            }
        }

        result = new Il2TelemetryPacket(size, tick, indicators, events);
        error = null;
        return true;
    }

    private static bool Truncated(out string error, string what, int index)
    {
        error = $"telemetrydevice packet ends inside {what} #{index}.";
        return false;
    }
}

// Event payloads ("Most of events are represented as data structures packet with 2-bytes packing alignment"). With
// 2-byte packing and these member types no padding occurs, so the sizes below are the plain sums.

/// <summary>SETUP_ENG: struct STEEngineSetup { short nIndex; short nID; float afPos[3]; float fMaxRPM; } (20 bytes).</summary>
public readonly record struct Il2EngineSetup(short Index, short Id, Vector3 Position, float MaxRpm);

/// <summary>SETUP_GUN: struct STEGunSetup { short nIndex; float afPos[3]; float fProjectileMass; float fShootVelocity; } (22 bytes).</summary>
public readonly record struct Il2GunSetup(short Index, Vector3 Position, float ProjectileMass, float ShootVelocity);

/// <summary>SETUP_LGEAR: struct STELandingGearSetup { short nIndex; short nID; float afPos[3]; } (16 bytes).</summary>
public readonly record struct Il2LandingGearSetup(short Index, short Id, Vector3 Position);

/// <summary>DROP_BOMB / ROCKET_LAUNCH: struct STEDropData { float afPos[3]; float fMass; unsigned short uFlags; } (18 bytes).</summary>
/// <remarks>The doc says "uFlags: Bit 1 = Rocket" without saying whether bit 1 is 0x1 or 0x2; the raw flags are kept.</remarks>
public readonly record struct Il2DropData(Vector3 Position, float Mass, ushort Flags);

/// <summary>HIT / DAMAGE: struct STEHit { float afPos[3]; float afHitF[3]; } (24 bytes, force in N).</summary>
public readonly record struct Il2Hit(Vector3 Position, Vector3 Force);

/// <summary>EXPLOSION: struct STEExplosion { float afPos[3]; float fExpRad; } (16 bytes).</summary>
public readonly record struct Il2Explosion(Vector3 Position, float Radius);

/// <summary>CLIENT_DAT: struct STClientData { long nClientID; long nServerClientID; char sPlayerName[32]; } (40 bytes, long = 32 bit).</summary>
public readonly record struct Il2ClientData(int ClientId, int ServerClientId, string PlayerName);

/// <summary>CTRL_DAT: struct STControlledData { long nParentClientID; short nCoalitionID; } (6 bytes).</summary>
public readonly record struct Il2ControlledData(int ParentClientId, short CoalitionId);

public static class Il2EventData
{
    public static bool TryEngineSetup(Il2Event e, out Il2EngineSetup value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 20)
            return false;
        value = new Il2EngineSetup(I16(d, 0), I16(d, 2), V3(d, 4), F(d, 16));
        return true;
    }

    public static bool TryGunSetup(Il2Event e, out Il2GunSetup value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 22)
            return false;
        value = new Il2GunSetup(I16(d, 0), V3(d, 2), F(d, 14), F(d, 18));
        return true;
    }

    public static bool TryLandingGearSetup(Il2Event e, out Il2LandingGearSetup value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 16)
            return false;
        value = new Il2LandingGearSetup(I16(d, 0), I16(d, 2), V3(d, 4));
        return true;
    }

    public static bool TryDrop(Il2Event e, out Il2DropData value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 18)
            return false;
        value = new Il2DropData(V3(d, 0), F(d, 12), BinaryPrimitives.ReadUInt16LittleEndian(d[16..]));
        return true;
    }

    public static bool TryHit(Il2Event e, out Il2Hit value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 24)
            return false;
        value = new Il2Hit(V3(d, 0), V3(d, 12));
        return true;
    }

    public static bool TryExplosion(Il2Event e, out Il2Explosion value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 16)
            return false;
        value = new Il2Explosion(V3(d, 0), F(d, 12));
        return true;
    }

    /// <summary>GUN_FIRE: one byte, the index of the gun that fired one bullet.</summary>
    public static bool TryGunFire(Il2Event e, out byte gunIndex)
    {
        gunIndex = e.Data.Length >= 1 ? e.Data[0] : (byte)0;
        return e.Data.Length >= 1;
    }

    public static bool TryClientData(Il2Event e, out Il2ClientData value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 40)
            return false;
        value = new Il2ClientData(
            BinaryPrimitives.ReadInt32LittleEndian(d),
            BinaryPrimitives.ReadInt32LittleEndian(d[4..]),
            CString(d.Slice(8, 32)));
        return true;
    }

    public static bool TryControlledData(Il2Event e, out Il2ControlledData value)
    {
        value = default;
        var d = e.Data.AsSpan();
        if (d.Length < 6)
            return false;
        value = new Il2ControlledData(BinaryPrimitives.ReadInt32LittleEndian(d), I16(d, 4));
        return true;
    }

    /// <summary>SET_FOCUS, SRV_ADDR, SRV_TITLE, SRS_ADDR: a STRING (length byte + ASCII).</summary>
    /// <remarks>When the first byte is not a matching length the whole payload is read as text (defensive; the doc
    /// does not say whether the event size already covers the length byte).</remarks>
    public static string String(Il2Event e)
    {
        var d = e.Data.AsSpan();
        if (d.Length >= 1 && d[0] <= d.Length - 1)
            return CString(d.Slice(1, d[0]));
        return CString(d);
    }

    private static short I16(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadInt16LittleEndian(d[offset..]);
    private static float F(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadSingleLittleEndian(d[offset..]);
    private static Vector3 V3(ReadOnlySpan<byte> d, int offset) => new(F(d, offset), F(d, offset + 4), F(d, offset + 8));

    private static string CString(ReadOnlySpan<byte> d)
    {
        var end = d.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? d[..end] : d);
    }
}
