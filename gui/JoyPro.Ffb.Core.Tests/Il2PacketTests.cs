using System.Numerics;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.Il2;
using static JoyPro.Ffb.Core.Tests.Il2PacketBuilder;

namespace JoyPro.Ffb.Core.Tests;

public class Il2PacketTests
{
    [Fact]
    public void FfbPacket_ParsesHeaderAndPackedRecords()
    {
        var data = BuildFfb(
            1234,
            (1, 0, 0, -0.25f, 0.5f, 0.75f, 0f),    // Y spring on joy0
            (0, 1, 1, 0.125f, -0.6f, 1f, 0f),      // X constant force on joy1
            (1, 0, 2, 0f, 0.3f, 0.9f, 12.5f));     // Y damper on joy0

        Assert.Equal(12 + 3 * 19, data.Length);
        Assert.True(Il2FfbPacket.TryParse(data, out var packet, out var error), error);
        Assert.Equal(1234u, packet!.Tick);
        Assert.Equal(1234 / 50.0, packet.TimeSeconds, 9);
        Assert.Equal(3, packet.Records.Count);

        var spring = packet.Records[0];
        Assert.Equal((byte)1, spring.Axis);
        Assert.Equal((byte)0, spring.Device);
        Assert.Equal(Il2FfbForceType.Spring, spring.Type);
        Assert.Equal(-0.25f, spring.Position);
        Assert.Equal(0.5f, spring.Force);
        Assert.Equal(0.75f, spring.Amplification);
        Assert.Equal(0f, spring.Frequency);

        Assert.Equal(Il2FfbForceType.Constant, packet.Records[1].Type);
        Assert.Equal((byte)1, packet.Records[1].Device);
        Assert.Equal(-0.6f, packet.Records[1].Force);
        Assert.Equal(Il2FfbForceType.Damper, packet.Records[2].Type);
        Assert.Equal(12.5f, packet.Records[2].Frequency);
    }

    [Fact]
    public void FfbPacket_RecordFieldsSitAtTheDocumentedOddOffsets()
    {
        var data = BuildFfb(7, (2, 3, 1, 1.5f, 2.5f, 3.5f, 4.5f));
        // pos at record offset 3, force at 7, amp at 11, freq at 15 (header is 12 bytes).
        Assert.Equal(1.5f, BitConverter.ToSingle(data, 12 + 3));
        Assert.Equal(2.5f, BitConverter.ToSingle(data, 12 + 7));
        Assert.Equal(3.5f, BitConverter.ToSingle(data, 12 + 11));
        Assert.Equal(4.5f, BitConverter.ToSingle(data, 12 + 15));
        Assert.True(Il2FfbPacket.TryParse(data, out var packet, out _));
        Assert.Equal(new Il2FfbRecord(2, 3, 1, 1.5f, 2.5f, 3.5f, 4.5f), packet!.Records[0]);
    }

    [Fact]
    public void FfbPacket_EmptyIsValid_TruncatedOrForeignIsRejected()
    {
        Assert.True(Il2FfbPacket.TryParse(BuildFfb(1), out var empty, out _));
        Assert.Empty(empty!.Records);

        var full = BuildFfb(1, (0, 0, 0, 0, 0, 0, 0), (1, 0, 0, 0, 0, 0, 0));
        Assert.False(Il2FfbPacket.TryParse(full.AsSpan(0, full.Length - 1), out _, out var error));
        Assert.Contains("announces 2 records", error);

        Assert.False(Il2FfbPacket.TryParse(BuildMotion(1, 0, 0, 0, 0, 0, 0, 0, 0, 0), out _, out _));
        Assert.False(Il2FfbPacket.TryParse(new byte[8], out _, out _));
    }

    [Fact]
    public void MotionPacket_ParsesAllFields()
    {
        var data = BuildMotion(50, 0.1f, -0.2f, 0.3f, 1f, 2f, 3f, -9.81f, 4f, 5f);
        Assert.Equal(44, data.Length);
        Assert.True(Il2MotionPacket.TryParse(data, out var packet, out var error), error);
        Assert.Equal(50u, packet!.Tick);
        Assert.Equal(1.0, packet.TimeSeconds, 9);
        Assert.Equal(0.1f, packet.Yaw);
        Assert.Equal(-0.2f, packet.Pitch);
        Assert.Equal(0.3f, packet.Roll);
        Assert.Equal(new Vector3(1, 2, 3), packet.Spin);
        Assert.Equal(new Vector3(-9.81f, 4, 5), packet.Acceleration);
        Assert.False(Il2MotionPacket.TryParse(data.AsSpan(0, 43), out _, out _));
    }

    [Fact]
    public void TelemetryPacket_ParsesIndicatorsAndEvents()
    {
        var hit = Floats(1, 2, 3, 300, 400, 0);
        var data = BuildTelemetry(
            100,
            [
                (0, [2400f, 2380f]),           // ENG_RPM, two engines
                (6, [120.5f]),                 // EAS
                (8, [0f, 29.42f, 0f]),         // ACCELERATION
                (9, [17f, 0.25f])              // COCKPIT_SHAKE
            ],
            [
                (0, Str("Spitfire Mk.IXe")),   // SET_FOCUS
                (9, [1]),                      // GUN_FIRE gun 1
                (9, [0]),                      // GUN_FIRE gun 0
                (6, hit)                       // HIT
            ]);

        Assert.True(Il2TelemetryPacket.TryParse(data, out var packet, out var error), error);
        Assert.Equal((ushort)data.Length, packet!.Size);
        Assert.Equal(100u, packet.Tick);
        Assert.Equal(4, packet.Indicators.Count);
        Assert.Equal(Il2IndicatorId.EngineRpm, packet.Indicators[0].Kind);
        Assert.Equal([2400f, 2380f], packet.Indicators[0].Values);
        Assert.Equal(120.5f, packet.Indicators[1].Values[0]);
        Assert.Equal(4, packet.Events.Count);
        Assert.Equal("Spitfire Mk.IXe", Il2EventData.String(packet.Events[0]));
        Assert.True(Il2EventData.TryGunFire(packet.Events[1], out var gun));
        Assert.Equal((byte)1, gun);
        Assert.True(Il2EventData.TryHit(packet.Events[3], out var h));
        Assert.Equal(new Vector3(1, 2, 3), h.Position);
        Assert.Equal(500f, h.Force.Length(), 3);
    }

    [Fact]
    public void TelemetryPacket_HeaderOffsetsMatchTheDocument()
    {
        var data = BuildTelemetry(0x01020304, [(6, [1f])], []);
        Assert.Equal(0x54000101u, BitConverter.ToUInt32(data, 0));
        Assert.Equal((ushort)data.Length, BitConverter.ToUInt16(data, 4));
        Assert.Equal(0x01020304u, BitConverter.ToUInt32(data, 6));
        Assert.Equal(1, data[10]);                         // Indicators_Count
        Assert.Equal((ushort)6, BitConverter.ToUInt16(data, 11)); // first SIndicator id
    }

    [Fact]
    public void TelemetryPacket_SizeFieldAndTruncationAreChecked()
    {
        var data = BuildTelemetry(1, [(0, [1f, 2f, 3f])], [(9, [0])]);

        // Size larger than the datagram: rejected.
        Assert.False(Il2TelemetryPacket.TryParse(data.AsSpan(0, data.Length - 1), out _, out _));

        // Extra bytes after Size are ignored.
        var padded = data.Concat(new byte[] { 0xFF, 0xFF }).ToArray();
        Assert.True(Il2TelemetryPacket.TryParse(padded, out var packet, out _));
        Assert.Single(packet!.Events);

        // A Size that cuts an indicator in half.
        var cut = (byte[])data.Clone();
        BitConverter.GetBytes((ushort)16).CopyTo(cut, 4);
        Assert.False(Il2TelemetryPacket.TryParse(cut, out _, out var error));
        Assert.Contains("indicator", error);
    }

    [Fact]
    public void EventPayloads_DecodeWithTwoBytePacking()
    {
        var engine = new Il2Event(1, Concat(I16(1), I16(7), Floats(1, 2, 3, 2700)));
        Assert.True(Il2EventData.TryEngineSetup(engine, out var e));
        Assert.Equal(new Il2EngineSetup(1, 7, new Vector3(1, 2, 3), 2700), e);

        var gun = new Il2Event(2, Concat(I16(3), Floats(4, 5, 6, 0.1f, 880)));
        Assert.Equal(22, gun.Data.Length);
        Assert.True(Il2EventData.TryGunSetup(gun, out var g));
        Assert.Equal(new Il2GunSetup(3, new Vector3(4, 5, 6), 0.1f, 880), g);

        var gear = new Il2Event(3, Concat(I16(0), I16(2), Floats(7, 8, 9)));
        Assert.True(Il2EventData.TryLandingGearSetup(gear, out var lg));
        Assert.Equal(new Il2LandingGearSetup(0, 2, new Vector3(7, 8, 9)), lg);

        var bomb = new Il2Event(4, Concat(Floats(1, 1, 1, 250), U16(2)));
        Assert.Equal(18, bomb.Data.Length);
        Assert.True(Il2EventData.TryDrop(bomb, out var b));
        Assert.Equal(250f, b.Mass);
        Assert.Equal((ushort)2, b.Flags);

        var explosion = new Il2Event(8, Floats(0, 0, 0, 12));
        Assert.True(Il2EventData.TryExplosion(explosion, out var x));
        Assert.Equal(12f, x.Radius);

        var name = new byte[32];
        "Pilot"u8.CopyTo(name);
        var client = new Il2Event(13, Concat(I32(42), I32(-1), name));
        Assert.True(Il2EventData.TryClientData(client, out var c));
        Assert.Equal(new Il2ClientData(42, -1, "Pilot"), c);

        var controlled = new Il2Event(14, Concat(I32(-1), I16(2)));
        Assert.True(Il2EventData.TryControlledData(controlled, out var cd));
        Assert.Equal(new Il2ControlledData(-1, 2), cd);

        Assert.False(Il2EventData.TryHit(new Il2Event(6, new byte[23]), out _));
    }

    [Fact]
    public void Source_MergesAllThreeOutputsIntoOneFrame()
    {
        var clock = new ManualClock(5);
        var source = new Il2TelemetrySource(clock);

        Assert.Equal(Il2PacketKind.Telemetry, source.Ingest(BuildTelemetry(
            50,
            [(6, [100f]), (8, [0f, 3 * 9.80665f, 0f]), (0, [2000f])],
            [(0, Str("Bf 109 G-6")), (9, [0]), (9, [0]), (9, [1])])));
        Assert.Equal(Il2PacketKind.Motion, source.Ingest(BuildMotion(51, 0, 0.1f, 0, 0, 0, 0, 0, 0, 0)));
        Assert.Equal(Il2PacketKind.Ffb, source.Ingest(BuildFfb(52, (1, 0, 0, 0.2f, 0.7f, 1f, 0f))));

        Assert.Equal("Bf 109 G-6", source.FocusObject);
        Assert.Equal(5.0, source.LastReceiveTime);
        Assert.Equal(52 / 50.0, source.Latest!.Time, 9);
        Assert.True(source.TryGet("il2.eas", out var eas));
        Assert.Equal(100, eas, 3);
        Assert.True(source.TryGet("il2.acc.g", out var g));
        Assert.Equal(3, g, 3);
        Assert.True(source.TryGet("il2.eng.rpm.0", out var rpm));
        Assert.Equal(2000, rpm);
        Assert.True(source.TryGet("il2.ev.gun_fire", out var fired));
        Assert.Equal(3, fired);
        Assert.True(source.TryGet("il2.ev.gun_fire.g0", out var gun0));
        Assert.Equal(2, gun0);
        Assert.True(source.TryGet("il2.motion.pitch", out var pitch));
        Assert.Equal(0.1, pitch, 5);
        Assert.True(source.TryGet("il2.ffb.d0.a1.spring.force", out var springForce));
        Assert.Equal(0.7, springForce, 5);

        // Next ffb packet without the spring: the old record is gone.
        source.Ingest(BuildFfb(53, (0, 0, 1, 0, -0.4f, 1f, 0f)));
        Assert.False(source.TryGet("il2.ffb.d0.a1.spring.force", out _));
        Assert.True(source.TryGet("il2.ffb.d0.a0.const.force", out var constant));
        Assert.Equal(-0.4, constant, 5);

        // Garbage is counted and does not publish.
        Assert.Equal(Il2PacketKind.Invalid, source.Ingest(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Equal(1, source.PacketsRejected);
        Assert.Contains(source.Descriptors, d => d.Key == "il2.ffb.d0.a1.spring.force");
    }

    [Fact]
    public async Task UdpReceiver_DeliversLoopbackDatagramsToTheSource()
    {
        var source = new Il2TelemetrySource(new StopwatchClock());
        using var receiver = UdpTelemetryReceiver.ForIl2(0, source);
        using (var sender = new System.Net.Sockets.UdpClient())
        {
            var packet = BuildMotion(10, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0);
            await sender.SendAsync(packet, packet.Length, "127.0.0.1", receiver.Port);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (source.Latest is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(source.TryGet("il2.motion.roll", out var roll));
        Assert.Equal(0.5, roll, 5);
    }
}
