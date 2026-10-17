using System.Buffers.Binary;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb.Core.Tests;

/// <summary>The bytes JoyPro hands to DirectInput for an effect: axis offsets, condition order per axis, signs.</summary>
/// <remarks>Background: on the VPforce Rhino a Y spring centre moved X (2026-10-11). These checks pin down that every
/// block names the axis it is meant for; which layout the device obeys is a hardware question (docs/ffb-telemetry.md).</remarks>
public class DirectInputLayoutTests
{
    private const long Base = 0x1000_0000;

    private static FfbEffectCommand Spring(FfbAxis axis, double centre, double coefficient = 0.8, double saturation = 0.25) =>
        new("d", axis, FfbEffectType.Spring, Centre: centre, Coefficient: coefficient, Saturation: saturation);

    [Fact]
    public void AxisOffsets_MatchTheDataFormat_XAt0_YAt4()
    {
        Assert.Equal(0, DirectInputLayout.AxisOffset(FfbAxis.X));
        Assert.Equal(4, DirectInputLayout.AxisOffset(FfbAxis.Y));
    }

    [Fact]
    public void CombinedSpring_HasXThenY_EachWithItsOwnCentre()
    {
        var buffer = new byte[DirectInputLayout.BufferSize];
        DiCondition[] conditions =
        [
            DirectInputLayout.Condition(Spring(FfbAxis.X, 0), 1),
            DirectInputLayout.Condition(Spring(FfbAxis.Y, -0.35), 1)
        ];
        DirectInputLayout.WriteCondition(buffer, Base, [FfbAxis.X, FfbAxis.Y], conditions);

        var (axes, read, duration) = DirectInputLayout.ReadCondition(buffer);
        Assert.Equal([0, 4], axes);
        Assert.Equal(2, read.Length);
        Assert.Equal(0, read[0].Offset);                              // X: centred
        Assert.Equal(-3500, read[1].Offset);                          // Y: 35 % forward -- on Y, not on X
        Assert.Equal(8000, read[1].PositiveCoefficient);
        Assert.Equal(8000, read[1].NegativeCoefficient);
        Assert.Equal(2500, read[1].PositiveSaturation);
        Assert.Equal(DirectInputLayout.Infinite, duration);

        // DIEFFECT header: size, flags, 2 axes, pointers into the same buffer, direction (0, 0), 48 bytes of conditions.
        Assert.Equal(80, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldSize)));
        Assert.Equal(0x12, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldFlags)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldAxisCount)));
        Assert.Equal(Base + 80, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(DirectInputLayout.FieldAxes)));
        Assert.Equal(Base + 88, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(DirectInputLayout.FieldDirection)));
        Assert.Equal(Base + 96, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(DirectInputLayout.FieldParams)));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(DirectInputLayout.FieldEnvelope)));
        Assert.Equal(48, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldParamsSize)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.DirectionOffset)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.DirectionOffset + 4)));
    }

    [Fact]
    public void SingleAxisSpring_OnY_NamesYOnly()
    {
        var buffer = new byte[DirectInputLayout.BufferSize];
        DirectInputLayout.WriteCondition(buffer, Base, [FfbAxis.Y], [DirectInputLayout.Condition(Spring(FfbAxis.Y, -0.35), 1)]);
        var (axes, read, _) = DirectInputLayout.ReadCondition(buffer);
        Assert.Equal([4], axes);
        Assert.Equal(-3500, Assert.Single(read).Offset);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.DirectionOffset)));
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldParamsSize)));
    }

    [Fact]
    public void Invert_FlipsTheCentreOfItsAxisOnly()
    {
        var signX = DirectInputLayout.Sign(FfbAxis.X, invertX: false, invertY: true);
        var signY = DirectInputLayout.Sign(FfbAxis.Y, invertX: false, invertY: true);
        Assert.Equal(1, signX);
        Assert.Equal(-1, signY);
        Assert.Equal(2000, DirectInputLayout.Condition(Spring(FfbAxis.X, 0.2), signX).Offset);
        Assert.Equal(3500, DirectInputLayout.Condition(Spring(FfbAxis.Y, -0.35), signY).Offset);
    }

    [Fact]
    public void MissingAxis_GetsAZeroCondition_DamperHasNoOffsetAndFullSaturation()
    {
        Assert.Equal(DiCondition.None, DirectInputLayout.Condition(null, 1));
        var damper = DirectInputLayout.Condition(new FfbEffectCommand("d", FfbAxis.X, FfbEffectType.Damper, Centre: 0.5, Coefficient: 0.4), 1);
        Assert.Equal(new DiCondition(0, 4000, 4000, 10000, 10000, 0), damper);
    }

    [Fact]
    public void ConstantForce_SingleAxis_SignedMagnitude()
    {
        var buffer = new byte[DirectInputLayout.BufferSize];
        DirectInputLayout.Write(buffer, Base, 300_000, [DirectInputLayout.AxisOffset(FfbAxis.Y)], [1], [DirectInputLayout.Nominal(-0.2)]);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.AxesOffset)));
        Assert.Equal(-2000, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.ParamsOffset)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldParamsSize)));
        Assert.Equal(300_000, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldDuration)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(DirectInputLayout.FieldAxisCount)));
    }

    [Fact]
    public void PairConditions_PutsEachAxisCommandOnItsOwnSide()
    {
        var pairs = new Dictionary<(FfbEffectType Type, int Slot), (FfbEffectCommand? X, FfbEffectCommand? Y)>();
        DirectInputLayout.PairConditions(
        [
            Spring(FfbAxis.Y, -0.35),
            Spring(FfbAxis.X, 0.1),
            new FfbEffectCommand("d", FfbAxis.Y, FfbEffectType.ConstantForce, Magnitude: 0.2),
            new FfbEffectCommand("d", FfbAxis.Y, FfbEffectType.Damper, Coefficient: 0.3, Slot: 1)
        ], pairs);
        Assert.Equal(2, pairs.Count);
        var spring = pairs[(FfbEffectType.Spring, 0)];
        Assert.Equal(0.1, spring.X!.Value.Centre);
        Assert.Equal(-0.35, spring.Y!.Value.Centre);
        var damper = pairs[(FfbEffectType.Damper, 1)];
        Assert.Null(damper.X);
        Assert.Equal(0.3, damper.Y!.Value.Coefficient);
    }

    [Fact]
    public void Write_RefusesMoreThanTwoAxesOrConditions()
    {
        var buffer = new byte[DirectInputLayout.BufferSize];
        Assert.Throws<ArgumentException>(() => DirectInputLayout.Write(buffer, Base, 0, [0, 4, 8], [0, 0, 0], []));
        Assert.Throws<ArgumentException>(() => DirectInputLayout.Write(buffer, Base, 0, [0], [1], new int[13]));
        Assert.Throws<ArgumentException>(() => DirectInputLayout.Write(new byte[10], Base, 0, [0], [1], []));
    }
}
