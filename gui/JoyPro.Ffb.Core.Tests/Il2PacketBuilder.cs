using System.Buffers.Binary;
using System.Text;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>Builds IL-2 datagrams byte by byte from the documented tables, independently of the parsers.</summary>
internal static class Il2PacketBuilder
{
    // IL2_ffbOutput.doc: header PacketID(0,4) Tick(4,4) nRecords(8,4); record axis(0,1) devNo(1,1) nForceType(2,1)
    // pos(3,4) force(7,4) amp(11,4) freq(15,4) -> 19 bytes.
    public static byte[] BuildFfb(uint tick, params (byte Axis, byte Dev, byte Type, float Pos, float Force, float Amp, float Freq)[] records)
    {
        var data = new byte[12 + 19 * records.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0x494D0100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), tick);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            var r = data.AsSpan(12 + 19 * i, 19);
            r[0] = records[i].Axis;
            r[1] = records[i].Dev;
            r[2] = records[i].Type;
            BinaryPrimitives.WriteSingleLittleEndian(r[3..], records[i].Pos);
            BinaryPrimitives.WriteSingleLittleEndian(r[7..], records[i].Force);
            BinaryPrimitives.WriteSingleLittleEndian(r[11..], records[i].Amp);
            BinaryPrimitives.WriteSingleLittleEndian(r[15..], records[i].Freq);
        }

        return data;
    }

    // Il2_MotionOutput: Packet_ID(0) Tick(4) Yaw(8) Pitch(12) Roll(16) Spin_x/y/z(20/24/28) Acc_x/y/z(32/36/40).
    public static byte[] BuildMotion(uint tick, float yaw, float pitch, float roll, float sx, float sy, float sz, float ax, float ay, float az)
    {
        var data = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0x494C0100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), tick);
        var floats = new[] { yaw, pitch, roll, sx, sy, sz, ax, ay, az };
        for (var i = 0; i < floats.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8 + 4 * i), floats[i]);
        return data;
    }

    // Il2_TeleOutput: Packet_ID(0,4) Size(4,2) Tick(6,4) Indicators_Count(10,1) SIndicator[] Events_Count(1) SEvent[].
    // SIndicator: WORD id, BYTE count, 32-bit values. SEvent: WORD id, BYTE size, data.
    public static byte[] BuildTelemetry(uint tick, (ushort Id, float[] Values)[] indicators, (ushort Id, byte[] Data)[] events)
    {
        var body = new List<byte>();
        body.Add((byte)indicators.Length);
        foreach (var (id, values) in indicators)
        {
            body.AddRange(U16(id));
            body.Add((byte)values.Length);
            foreach (var v in values)
                body.AddRange(F32(v));
        }

        body.Add((byte)events.Length);
        foreach (var (id, data) in events)
        {
            body.AddRange(U16(id));
            body.Add((byte)data.Length);
            body.AddRange(data);
        }

        var packet = new byte[10 + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), 0x54000101);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), tick);
        body.CopyTo(packet, 10);
        return packet;
    }

    public static byte[] U16(ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return b;
    }

    public static byte[] I16(short value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, value);
        return b;
    }

    public static byte[] I32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    public static byte[] F32(float value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        return b;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Floats(params float[] values) => values.SelectMany(F32).ToArray();

    /// <summary>STRING: length byte + ASCII.</summary>
    public static byte[] Str(string text) => Concat([(byte)text.Length], Encoding.ASCII.GetBytes(text));
}
