using System.Buffers.Binary;
using System.Globalization;

namespace JoyPro.Ffb.Core.Output;

/// <summary>One DICONDITION (dinput.h), in device units (-10000..10000).</summary>
public readonly record struct DiCondition(int Offset, int PositiveCoefficient, int NegativeCoefficient, int PositiveSaturation, int NegativeSaturation, int DeadBand)
{
    public static readonly DiCondition None = default;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"offset {Offset}, coefficient {PositiveCoefficient}/{NegativeCoefficient}, saturation {PositiveSaturation}/{NegativeSaturation}");
}

/// <summary>How condition effects (spring, damper, friction) are laid out on the device.</summary>
public enum FfbConditionLayout
{
    /// <summary>One effect for X and Y with one DICONDITION per axis, X first (the usual 2-axis spring of games).</summary>
    Combined,

    /// <summary>One single-axis effect per axis, updated without a restart.</summary>
    Separate,

    /// <summary>One single-axis effect per axis, restarted with every change (the first implementation).</summary>
    SeparateRestart
}

/// <summary>The DIEFFECT block JoyPro hands to DirectInput, written byte for byte (x64 layout), so it can be checked
/// without a device.</summary>
/// <remarks>
/// Buffer layout: DIEFFECT (80 bytes) | rgdwAxes (2 DWORDs) | rglDirection (2 LONGs) | type specific block (up to two
/// DICONDITIONs of 24 bytes). Axes are named by their offset in JoyPro's data format (DIEFF_OBJECTOFFSETS): X = 0,
/// Y = 4 -- the device's data format must put X and Y there. Condition blocks follow the axis order of rgdwAxes, so
/// with X and Y the first DICONDITION is X's and the second Y's; a combined condition has direction (0, 0) (each axis
/// has its own block, the direction is not used).
/// </remarks>
public static class DirectInputLayout
{
    public const int NominalMax = 10000;
    public const int AxisOffsetX = 0;
    public const int AxisOffsetY = 4;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint DieffObjectOffsets = 0x02;
    public const uint DieffCartesian = 0x10;
    public const uint DiebNoTrigger = 0xFFFFFFFF;

    public const int EffectSize = 80;
    public const int AxesOffset = 80;
    public const int DirectionOffset = 88;
    public const int ParamsOffset = 96;
    public const int ConditionSize = 24;
    public const int BufferSize = ParamsOffset + 2 * ConditionSize;

    // DIEFFECT field offsets (x64).
    public const int FieldSize = 0;
    public const int FieldFlags = 4;
    public const int FieldDuration = 8;
    public const int FieldGain = 16;
    public const int FieldTrigger = 20;
    public const int FieldAxisCount = 28;
    public const int FieldAxes = 32;
    public const int FieldDirection = 40;
    public const int FieldEnvelope = 48;
    public const int FieldParamsSize = 56;
    public const int FieldParams = 64;
    public const int FieldStartDelay = 72;

    public static int Nominal(double value) => (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, -1, 1) * NominalMax);

    public static int AxisOffset(FfbAxis axis) => axis == FfbAxis.X ? AxisOffsetX : AxisOffsetY;

    /// <summary>The sign a command's direction (force and spring centre) gets on the device.</summary>
    public static int Sign(FfbAxis axis, bool invertX, bool invertY) => axis == FfbAxis.X ? (invertX ? -1 : 1) : (invertY ? -1 : 1);

    public static bool IsCondition(FfbEffectType type) => type is FfbEffectType.Spring or FfbEffectType.Damper or FfbEffectType.Friction;

    /// <summary>The DICONDITION for a command (null: this axis has no such effect -> no force).</summary>
    public static DiCondition Condition(FfbEffectCommand? command, int sign)
    {
        if (command is not { } c)
            return DiCondition.None;
        var coefficient = Math.Abs(Nominal(c.Coefficient));
        var saturation = c.Type == FfbEffectType.Spring ? Math.Abs(Nominal(c.Saturation)) : NominalMax;
        var offset = c.Type == FfbEffectType.Spring ? Nominal(c.Centre * sign) : 0;
        return new DiCondition(offset, coefficient, coefficient, saturation, saturation, 0);
    }

    /// <summary>The condition commands of one update, paired per (type, slot) into one 2-axis effect: X's command and
    /// Y's command (null where an axis has none). Forces are not included.</summary>
    public static void PairConditions(IEnumerable<FfbEffectCommand> commands, Dictionary<(FfbEffectType Type, int Slot), (FfbEffectCommand? X, FfbEffectCommand? Y)> into)
    {
        into.Clear();
        foreach (var command in commands)
        {
            if (!IsCondition(command.Type))
                continue;
            var key = (command.Type, command.Slot);
            var pair = into.GetValueOrDefault(key);
            into[key] = command.Axis == FfbAxis.X ? (command, pair.Y) : (pair.X, command);
        }
    }

    /// <summary>The type-specific block of conditions, one per axis, in axis order.</summary>
    public static int[] ConditionParams(ReadOnlySpan<DiCondition> conditions)
    {
        var values = new int[conditions.Length * 6];
        for (var i = 0; i < conditions.Length; i++)
        {
            var c = conditions[i];
            values[i * 6] = c.Offset;
            values[i * 6 + 1] = c.PositiveCoefficient;
            values[i * 6 + 2] = c.NegativeCoefficient;
            values[i * 6 + 3] = c.PositiveSaturation;
            values[i * 6 + 4] = c.NegativeSaturation;
            values[i * 6 + 5] = c.DeadBand;
        }

        return values;
    }

    /// <summary>Writes the DIEFFECT with its axes, direction and parameters into <paramref name="buffer"/>, whose first
    /// byte lives at <paramref name="baseAddress"/> in memory (the pointers inside point into the same buffer).</summary>
    public static void Write(Span<byte> buffer, long baseAddress, uint duration, ReadOnlySpan<int> axisOffsets, ReadOnlySpan<int> direction, ReadOnlySpan<int> parameters)
    {
        if (buffer.Length < BufferSize)
            throw new ArgumentException("The effect buffer is too small.", nameof(buffer));
        if (axisOffsets.Length is < 1 or > 2 || direction.Length != axisOffsets.Length || parameters.Length * 4 > BufferSize - ParamsOffset)
            throw new ArgumentException("One or two axes, a direction per axis and at most two conditions.");
        buffer[..BufferSize].Clear();
        for (var i = 0; i < axisOffsets.Length; i++)
        {
            Int(buffer, AxesOffset + 4 * i, axisOffsets[i]);
            Int(buffer, DirectionOffset + 4 * i, direction[i]);
        }

        for (var i = 0; i < parameters.Length; i++)
            Int(buffer, ParamsOffset + 4 * i, parameters[i]);

        Int(buffer, FieldSize, EffectSize);
        Int(buffer, FieldFlags, unchecked((int)(DieffObjectOffsets | DieffCartesian)));
        Int(buffer, FieldDuration, unchecked((int)duration));
        Int(buffer, FieldGain, NominalMax);
        Int(buffer, FieldTrigger, unchecked((int)DiebNoTrigger));
        Int(buffer, FieldAxisCount, axisOffsets.Length);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[FieldAxes..], baseAddress + AxesOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[FieldDirection..], baseAddress + DirectionOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[FieldEnvelope..], 0);
        Int(buffer, FieldParamsSize, parameters.Length * 4);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[FieldParams..], baseAddress + ParamsOffset);
        Int(buffer, FieldStartDelay, 0);
    }

    /// <summary>A condition effect on the given axes: one block per axis (combined: X then Y, direction 0/0; a single
    /// axis: direction 1).</summary>
    public static void WriteCondition(Span<byte> buffer, long baseAddress, ReadOnlySpan<FfbAxis> axes, ReadOnlySpan<DiCondition> conditions)
    {
        if (axes.Length != conditions.Length)
            throw new ArgumentException("One condition per axis.");
        Span<int> offsets = stackalloc int[axes.Length];
        Span<int> direction = stackalloc int[axes.Length];
        for (var i = 0; i < axes.Length; i++)
        {
            offsets[i] = AxisOffset(axes[i]);
            direction[i] = axes.Length == 1 ? 1 : 0;
        }

        Write(buffer, baseAddress, Infinite, offsets, direction, ConditionParams(conditions));
    }

    /// <summary>Reads the blocks back (tests, logs): axis offsets and conditions.</summary>
    public static (int[] Axes, DiCondition[] Conditions, uint Duration) ReadCondition(ReadOnlySpan<byte> buffer)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer[FieldAxisCount..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(buffer[FieldParamsSize..]);
        var axes = new int[count];
        for (var i = 0; i < count; i++)
            axes[i] = BinaryPrimitives.ReadInt32LittleEndian(buffer[(AxesOffset + 4 * i)..]);
        var conditions = new DiCondition[size / ConditionSize];
        for (var i = 0; i < conditions.Length; i++)
        {
            var block = buffer.Slice(ParamsOffset + i * ConditionSize, ConditionSize);
            conditions[i] = new DiCondition(
                BinaryPrimitives.ReadInt32LittleEndian(block),
                BinaryPrimitives.ReadInt32LittleEndian(block[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(block[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(block[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(block[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(block[20..]));
        }

        return (axes, conditions, unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(buffer[FieldDuration..])));
    }

    private static void Int(Span<byte> buffer, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], value);
}
