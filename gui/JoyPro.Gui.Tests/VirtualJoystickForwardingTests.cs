using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using JoyPro.VJoy;
using Xunit;

namespace JoyPro.Gui.Tests;

// The forwarding table of JoyPro.VJoy (gui/JoyPro.VJoy/ForwardingTable.cs, linked here): physical device states in,
// vJoy positions out. No DirectInput and no vJoy involved.
public sealed class VirtualJoystickForwardingTests
{
    // As engine vjoy forwarder-config writes it.
    private const string Config = """
        {"version":1,"vjoyDevices":[1,2],"sources":[
         {"device":"VKB Stick {1A2B3C4D-0A3F-11F0-8011-444553540000}","name":"VKB Stick","instanceGuid":"1a2b3c4d-0a3f-11f0-8011-444553540000"},
         {"device":"Throttle {42CEC570-0A3F-11F0-800B-444553540000}","name":"Throttle","instanceGuid":"42cec570-0a3f-11f0-800b-444553540000"}],
        "slots":[
         {"source":0,"kind":"axis","index":0,"device":1,"target":0},
         {"source":0,"kind":"axis","index":5,"device":1,"target":5},
         {"source":1,"kind":"axis","index":0,"device":1,"target":2},
         {"source":1,"kind":"axis","index":7,"device":2,"target":7},
         {"source":0,"kind":"button","index":0,"device":1,"target":0},
         {"source":1,"kind":"button","index":127,"device":1,"target":95},
         {"source":1,"kind":"button","index":3,"device":2,"target":127},
         {"source":0,"kind":"hat","index":0,"device":1,"target":0},
         {"source":1,"kind":"hat","index":1,"device":2,"target":3},
         {"source":5,"kind":"button","index":0,"device":1,"target":1},
         {"source":0,"kind":"button","index":0,"device":9,"target":1},
         {"source":0,"kind":"axis","index":8,"device":1,"target":1}],
        "virtualGames":["DCS"]}
        """;

    [Fact]
    public void Parse_ReadsTheEngineTable()
    {
        ForwarderConfig config = ForwarderConfig.Parse(Config);

        Assert.Equal([1, 2], config.VJoyDevices);
        Assert.Equal(2, config.Sources.Count);
        Assert.Equal(Guid.Parse("1a2b3c4d-0a3f-11f0-8011-444553540000"), config.Sources[0].InstanceGuid);
        Assert.Equal(12, config.Slots.Count);
        Assert.Equal(["DCS"], config.VirtualGames);
    }

    [Fact]
    public void Table_IgnoresSlotsOutOfRange()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config));

        // Unknown source 5, unknown vJoy device 9, axis index 8: dropped.
        Assert.Equal(9, table.SlotCount);
    }

    [Fact]
    public void Reset_IsNeutral()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config));

        ref VJoyPosition p = ref table.Position(0);
        Assert.Equal(1, p.bDevice);
        Assert.Equal(16384, p.wAxisX);
        Assert.Equal(16384, p.wDial);
        Assert.Equal(0, p.lButtons);
        Assert.Equal(uint.MaxValue, p.bHats);
        Assert.Equal(uint.MaxValue, p.bHatsEx3);
        Assert.Equal(2, table.Position(1).bDevice);
    }

    [Fact]
    public void Apply_CopiesAxesButtonsAndHats()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config));
        byte[] stick = State();
        SetAxis(stick, 0, 1234);
        SetAxis(stick, 5, 40000); // beyond vJoy's range: clamped
        stick[ForwardingTable.ButtonOffset + 0] = 0x80;
        SetHat(stick, 0, 27000);
        byte[] throttle = State();
        SetAxis(throttle, 0, 32767);
        SetAxis(throttle, 7, 7);
        throttle[ForwardingTable.ButtonOffset + 127] = 0x80;
        throttle[ForwardingTable.ButtonOffset + 3] = 0x80;
        SetHat(throttle, 1, 4500);

        table.Apply(0, stick);
        table.Apply(1, throttle);

        ref VJoyPosition one = ref table.Position(0);
        Assert.Equal(1234, one.wAxisX);
        Assert.Equal(32767, one.wAxisZRot);
        Assert.Equal(32767, one.wAxisZ);
        Assert.True(one.GetButton(0));
        Assert.True(one.GetButton(95));
        Assert.Equal(unchecked((int)0x80000000), one.lButtonsEx2);
        Assert.Equal(27000u, one.bHats);
        ref VJoyPosition two = ref table.Position(1);
        Assert.Equal(7, two.wDial);
        Assert.True(two.GetButton(127));
        Assert.Equal(unchecked((int)0x80000000), two.lButtonsEx3);
        Assert.Equal(4500u, two.bHatsEx3);

        // Button up, hat centred.
        stick[ForwardingTable.ButtonOffset + 0] = 0;
        SetHat(stick, 0, uint.MaxValue);
        table.Apply(0, stick);
        Assert.False(table.Position(0).GetButton(0));
        Assert.Equal(uint.MaxValue, table.Position(0).bHats);
    }

    [Fact]
    public void Release_LiftsButtonsAndCentresHatsButKeepsAxes()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config));
        byte[] stick = State();
        SetAxis(stick, 0, 2000);
        stick[ForwardingTable.ButtonOffset] = 0x80;
        SetHat(stick, 0, 9000);
        table.Apply(0, stick);

        table.Release(0);

        Assert.Equal(2000, table.Position(0).wAxisX);
        Assert.False(table.Position(0).GetButton(0));
        Assert.Equal(uint.MaxValue, table.Position(0).bHats);
    }

    [Fact]
    public void DiscreteHats_RoundToTheNearestDirection()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config), discreteHats: new System.Collections.Generic.Dictionary<int, bool> { [1] = true });
        byte[] stick = State();
        Assert.Equal(0xFu, table.Position(0).GetDiscreteHat(0));

        foreach ((uint angle, uint direction) in new[] { (0u, 0u), (4500u, 1u), (9000u, 1u), (13499u, 1u), (18000u, 2u), (27000u, 3u), (31500u, 0u) })
        {
            SetHat(stick, 0, angle);
            table.Apply(0, stick);
            Assert.Equal(direction, table.Position(0).GetDiscreteHat(0));
        }

        SetHat(stick, 0, uint.MaxValue);
        table.Apply(0, stick);
        Assert.Equal(0xFu, table.Position(0).GetDiscreteHat(0));
    }

    [Fact]
    public void Apply_AllocatesNothing()
    {
        ForwardingTable table = new(ForwarderConfig.Parse(Config));
        byte[] stick = State();
        byte[] throttle = State();
        table.Apply(0, stick);
        table.Apply(1, throttle);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++)
        {
            SetAxis(stick, 0, i & 0x7FFF);
            stick[ForwardingTable.ButtonOffset] = (byte)((i & 1) * 0x80);
            table.Apply(0, stick);
            table.Apply(1, throttle);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Position_HasVJoysLayout()
    {
        // JOYSTICK_POSITION_V2: BYTE + 3 padding, 18 LONG axes, LONG buttons, 4 DWORD hats, 3 LONG buttons.
        Assert.Equal(108, Marshal.SizeOf<VJoyPosition>());
        Assert.Equal(4, (int)Marshal.OffsetOf<VJoyPosition>(nameof(VJoyPosition.wThrottle)));
        Assert.Equal(76, (int)Marshal.OffsetOf<VJoyPosition>(nameof(VJoyPosition.lButtons)));
        Assert.Equal(80, (int)Marshal.OffsetOf<VJoyPosition>(nameof(VJoyPosition.bHats)));
        Assert.Equal(96, (int)Marshal.OffsetOf<VJoyPosition>(nameof(VJoyPosition.lButtonsEx1)));
    }

    // The virtual Xbox pad: one source with stick axes, buttons, a hat; a second one with a throttle.
    private const string XboxConfig = """
        {"version":1,"vjoyDevices":[],"sources":[
         {"device":"Stick {42CF1390-0A3F-11F0-8011-444553540000}","name":"Stick","instanceGuid":"42cf1390-0a3f-11f0-8011-444553540000"},
         {"device":"Throttle {42CEC570-0A3F-11F0-800B-444553540000}","name":"Throttle","instanceGuid":"42cec570-0a3f-11f0-800b-444553540000"}],
        "slots":[],"xboxGame":"HLLVietnam",
        "xbox":[
         {"source":0,"kind":"axis","index":0,"target":"XB_LX"},
         {"source":0,"kind":"axis","index":1,"target":"XB_LY"},
         {"source":1,"kind":"axis","index":2,"target":"XB_RT"},
         {"source":0,"kind":"button","index":0,"target":"XB_A"},
         {"source":1,"kind":"button","index":0,"target":"XB_A"},
         {"source":0,"kind":"button","index":1,"target":"XB_LX+"},
         {"source":0,"kind":"button","index":2,"target":"XB_LT"},
         {"source":0,"kind":"hat","index":0,"target":"XB_DPAD"},
         {"source":0,"kind":"axis","index":0,"target":"XB_A"},
         {"source":0,"kind":"button","index":3,"target":"XB_NOPE"}]}
        """;

    [Fact]
    public void Xbox_ParseKeepsOnlyValidSlots()
    {
        ForwarderConfig config = ForwarderConfig.Parse(XboxConfig);
        XboxTable table = new(config);

        Assert.Equal("HLLVietnam", table.Game);
        Assert.Equal(10, config.Xbox.Count);
        Assert.Equal(8, table.SlotCount); // axis on a button and an unknown control are dropped
        Assert.Equal(0, table.Report.LeftX);
        Assert.Equal(0, table.Report.LeftTrigger);
    }

    [Fact]
    public void Xbox_AxesButtonsHalvesTriggersAndDPad()
    {
        XboxTable table = new(ForwarderConfig.Parse(XboxConfig));
        byte[] stick = State();
        SetAxis(stick, 0, 0);          // full left
        SetAxis(stick, 1, 0);          // stick forward (DirectInput small) -> XInput up (positive)
        SetHat(stick, 0, 4500);        // up-right
        byte[] throttle = State();
        SetAxis(throttle, 2, 32767);

        table.Apply(0, stick);
        table.Apply(1, throttle);

        ref XboxReport r = ref table.Report;
        Assert.Equal(-32767, r.LeftX);
        Assert.Equal(32767, r.LeftY);
        Assert.Equal(255, r.RightTrigger);
        Assert.Equal(XboxReport.DPadUp | XboxReport.DPadRight, r.Buttons);

        // Two buttons on A combine; a button on the stick half wins over the axis; a button on LT pulls it fully.
        throttle[ForwardingTable.ButtonOffset] = 0x80;
        stick[ForwardingTable.ButtonOffset + 1] = 0x80;
        stick[ForwardingTable.ButtonOffset + 2] = 0x80;
        table.Apply(1, throttle);
        table.Apply(0, stick);
        Assert.Equal(32767, table.Report.LeftX);
        Assert.Equal(255, table.Report.LeftTrigger);
        Assert.True((table.Report.Buttons & XboxReport.A) != 0);
        throttle[ForwardingTable.ButtonOffset] = 0;
        table.Apply(1, throttle);
        Assert.True((table.Report.Buttons & XboxReport.A) == 0);

        // A lost device: buttons up, hat centred, axes keep.
        table.Release(0);
        Assert.Equal(XboxReport.DPadUp & 0, table.Report.Buttons & (XboxReport.DPadUp | XboxReport.DPadRight));
        Assert.Equal(0, table.Report.LeftTrigger);
        Assert.Equal(-32767, table.Report.LeftX);
    }

    // HLLV's collective: one axis drives Ascend (RT) above the centre and Descend (LT) below; "~" reverses an axis.
    [Fact]
    public void Xbox_TriggerSplitAndInvertedAxis()
    {
        const string config = """
            {"version":1,"vjoyDevices":[],"sources":[
             {"device":"Throttle {42CEC570-0A3F-11F0-800B-444553540000}","name":"Throttle","instanceGuid":"42cec570-0a3f-11f0-800b-444553540000"}],
            "slots":[],"xboxGame":"HLLVietnam",
            "xbox":[
             {"source":0,"kind":"axis","index":2,"target":"XB_TRIGGERS"},
             {"source":0,"kind":"axis","index":5,"target":"XB_RX~"},
             {"source":0,"kind":"axis","index":1,"target":"XB_TRIGGERS~"}]}
            """;
        XboxTable table = new(ForwarderConfig.Parse(config));
        Assert.Equal(3, table.SlotCount);
        byte[] throttle = State();
        SetAxis(throttle, 1, 16384);   // the reversed split axis rests at its centre
        SetAxis(throttle, 2, 16384);   // centre: both triggers up
        SetAxis(throttle, 5, 0);       // reversed: full left becomes full right
        table.Apply(0, throttle);
        Assert.Equal(0, table.Report.LeftTrigger);
        Assert.Equal(0, table.Report.RightTrigger);
        Assert.Equal(32767, table.Report.RightX);

        SetAxis(throttle, 2, 32767);   // forward: Ascend
        table.Apply(0, throttle);
        Assert.Equal(255, table.Report.RightTrigger);
        Assert.Equal(0, table.Report.LeftTrigger);

        SetAxis(throttle, 2, 0);       // back: Descend
        table.Apply(0, throttle);
        Assert.Equal(0, table.Report.RightTrigger);
        Assert.Equal(255, table.Report.LeftTrigger);

        SetAxis(throttle, 2, 16384);
        SetAxis(throttle, 1, 32767);   // the reversed split axis pushed forward gives Descend
        table.Apply(0, throttle);
        Assert.Equal(255, table.Report.LeftTrigger);
        Assert.Equal(0, table.Report.RightTrigger);
    }

    [Fact]
    public void Xbox_ApplyAllocatesNothing()
    {
        XboxTable table = new(ForwarderConfig.Parse(XboxConfig));
        byte[] stick = State();
        table.Apply(0, stick);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++)
        {
            SetAxis(stick, 0, i & 0x7FFF);
            stick[ForwardingTable.ButtonOffset] = (byte)((i & 1) * 0x80);
            table.Apply(0, stick);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    private static byte[] State()
    {
        byte[] state = new byte[ForwardingTable.StateSize];
        for (int hat = 0; hat < 4; hat++)
            SetHat(state, hat, uint.MaxValue);
        return state;
    }

    private static void SetAxis(byte[] state, int axis, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(ForwardingTable.AxisOffset + axis * 4, 4), value);

    private static void SetHat(byte[] state, int hat, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(ForwardingTable.HatOffset + hat * 4, 4), value);
}
