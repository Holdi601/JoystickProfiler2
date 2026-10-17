using System.Runtime.InteropServices;
using JoyPro.Ffb.Core.Input;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb;

/// <summary>Buttons, hats and stick positions for the graph's input and trim nodes, read through DirectInput.</summary>
/// <remarks>
/// Every device is opened non-exclusively in the background (DISCL_NONEXCLUSIVE | DISCL_BACKGROUND): reading never
/// takes a device away from the game, works while the game has the focus, and does not depend on JoyPro's own
/// exclusive acquisition of the force feedback stick (a second, non-exclusive instance of the same device in this
/// process reads its buttons and position, also while the forces are off). Devices are opened on first use (when a
/// node asks for them) and polled once per tick on the force loop thread (<see cref="Poll"/>); a device that is missing
/// is looked for again every few seconds. Axes are read with a range of -10000..10000, so -1..1 after scaling:
/// right and pulled back are positive, as DirectInput reports them.
/// </remarks>
public sealed class DirectInputInputs : IFfbInputs, IDisposable
{
    private const uint DisclNonExclusive = 0x2;
    private const uint DisclBackground = 0x8;
    private const int StateSize = 152; // X, Y, 4 POVs, 128 buttons
    private const int RangeMax = 10000;
    private const double RetrySeconds = 3;

    private readonly Guid _ffbDevice;
    private readonly Dictionary<string, Reader?> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private DirectInputFfb.IDirectInput8W? _directInput;
    private HiddenWindow? _window;
    private IntPtr _format;
    private bool _disposed;

    /// <param name="ffbDevice">The force feedback device ("" in a node means this one).</param>
    public DirectInputInputs(Guid ffbDevice) => _ffbDevice = ffbDevice;

    /// <summary>Messages for the log (a device that was not found, a read that failed); drained by the caller.</summary>
    public List<string> Warnings { get; } = [];

    public void Poll()
    {
        if (_disposed)
            return;
        foreach (var reader in _readers.Values)
            reader?.Read();
    }

    public bool TryGetButton(string device, FfbInputRef input, out bool pressed)
    {
        pressed = false;
        if (input.IsNone || Get(device) is not { Valid: true } reader)
            return false;
        if (input.Kind == FfbInputKind.Button)
        {
            pressed = input.Index < 128 && Marshal.ReadByte(reader.State, 24 + input.Index) >= 0x80;
            return true;
        }

        var pov = input.Index < 4 ? unchecked((uint)Marshal.ReadInt32(reader.State, 8 + 4 * input.Index)) : 0xFFFFFFFF;
        pressed = input.HatMatches(pov > 35999 ? -1 : (int)pov);
        return true;
    }

    public bool TryGetStick(string device, FfbAxis axis, out double position)
    {
        position = 0;
        if (Get(device) is not { Valid: true } reader)
            return false;
        position = Math.Clamp(Marshal.ReadInt32(reader.State, axis == FfbAxis.X ? 0 : 4) / (double)RangeMax, -1, 1);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var reader in _readers.Values)
            reader?.Dispose();
        _readers.Clear();
        if (_directInput is not null)
        {
            Marshal.ReleaseComObject(_directInput);
            _directInput = null;
        }

        _window?.Dispose();
        _window = null;
        if (_format != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_format);
            _format = IntPtr.Zero;
        }
    }

    // The reader for a device setting; opened on first use, looked for again every few seconds while missing.
    private Reader? Get(string device)
    {
        var key = (device ?? "").Trim();
        if (_readers.TryGetValue(key, out var reader) && reader is not null)
            return reader;
        if (_disposed)
            return null;
        var now = _clock.Elapsed.TotalSeconds;
        if (_lastAttempt.TryGetValue(key, out var last) && now - last < RetrySeconds)
            return null;
        _lastAttempt[key] = now;
        reader = Open(key);
        _readers[key] = reader;
        reader?.Read();
        return reader;
    }

    private Reader? Open(string setting)
    {
        try
        {
            _directInput ??= DirectInputFfb.Create();
            // A fresh device list when a device was missing (it may have been plugged in since).
            var instances = DirectInputFfb.Instances(_directInput);
            Guid? guid = setting.Length == 0 ? _ffbDevice : null;
            foreach (var instance in instances)
            {
                if (guid is null && FfbDeviceMatch.Matches(setting, instance.guidInstance, instance.tszInstanceName, instance.tszProductName))
                    guid = instance.guidInstance;
            }
            if (guid is null || guid == Guid.Empty)
            {
                Warn("Input device not found: " + (setting.Length == 0 ? "the force feedback stick" : setting));
                return null;
            }

            var id = guid.Value;
            if (_directInput.CreateDevice(ref id, out var device, IntPtr.Zero) < 0 || device is null)
            {
                Warn("Input device could not be opened: " + setting);
                return null;
            }

            _format = _format == IntPtr.Zero ? BuildFormat() : _format;
            _window ??= HiddenWindow.Create();
            if (device.SetDataFormat(_format) < 0 || device.SetCooperativeLevel(_window.Handle, DisclNonExclusive | DisclBackground) < 0)
            {
                Marshal.ReleaseComObject(device);
                Warn("Input device could not be set up: " + setting);
                return null;
            }

            SetRange(device);
            device.Acquire();
            return new Reader(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            Warn("Input device " + setting + ": " + ex.Message);
            return null;
        }
    }

    private void Warn(string message)
    {
        if (Warnings.Count < 50)
            Warnings.Add(message);
    }

    // DIPROP_RANGE (4) for every axis of the device (DIPH_DEVICE): -10000..10000.
    private static void SetRange(DirectInputFfb.IDirectInputDevice8W device)
    {
        var header = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(header, 0, 24);   // dwSize (DIPROPRANGE)
            Marshal.WriteInt32(header, 4, 16);   // dwHeaderSize
            Marshal.WriteInt32(header, 8, 0);    // dwObj
            Marshal.WriteInt32(header, 12, 0);   // dwHow = DIPH_DEVICE
            Marshal.WriteInt32(header, 16, -RangeMax);
            Marshal.WriteInt32(header, 20, RangeMax);
            device.SetProperty(new IntPtr(4), header);
        }
        catch (Exception)
        {
        }
        finally
        {
            Marshal.FreeHGlobal(header);
        }
    }

    /// <summary>X and Y (by axis guid), 4 POVs and 128 buttons (by instance, as JoyPro's capture numbers them).</summary>
    private static IntPtr BuildFormat()
    {
        const int objectSize = 24;     // DIOBJECTDATAFORMAT on x64
        const int formatSize = 32;     // DIDATAFORMAT on x64
        const uint didftAxis = 0x3;
        const uint didftPov = 0x10;
        const uint didftButton = 0xC;
        const uint didftAnyInstance = 0x00FFFF00;
        const uint didftOptional = 0x80000000;
        const uint didfAbsAxis = 0x1;
        const int count = 2 + 4 + 128;
        var memory = Marshal.AllocHGlobal(formatSize + count * objectSize + 2 * 16);
        var objects = memory + formatSize;
        var guids = objects + count * objectSize;
        Marshal.StructureToPtr(DirectInputFfb.GuidXAxis, guids, false);
        Marshal.StructureToPtr(DirectInputFfb.GuidYAxis, guids + 16, false);
        void Write(int index, IntPtr guid, int offset, uint type)
        {
            var o = objects + index * objectSize;
            Marshal.WriteIntPtr(o, 0, guid);
            Marshal.WriteInt32(o, 8, offset);
            Marshal.WriteInt32(o, 12, unchecked((int)type));
            Marshal.WriteInt32(o, 16, 0);
            Marshal.WriteInt32(o, 20, 0);
        }

        Write(0, guids, 0, didftAxis | didftAnyInstance | didftOptional);
        Write(1, guids + 16, 4, didftAxis | didftAnyInstance | didftOptional);
        for (var i = 0; i < 4; i++)
            Write(2 + i, IntPtr.Zero, 8 + 4 * i, didftPov | ((uint)i << 8) | didftOptional);
        for (var i = 0; i < 128; i++)
            Write(6 + i, IntPtr.Zero, 24 + i, didftButton | ((uint)i << 8) | didftOptional);

        Marshal.WriteInt32(memory, 0, formatSize);
        Marshal.WriteInt32(memory, 4, objectSize);
        Marshal.WriteInt32(memory, 8, (int)didfAbsAxis);
        Marshal.WriteInt32(memory, 12, StateSize);
        Marshal.WriteInt32(memory, 16, count);
        Marshal.WriteInt32(memory, 20, 0);
        Marshal.WriteIntPtr(memory, 24, objects);
        return memory;
    }

    private sealed class Reader(DirectInputFfb.IDirectInputDevice8W device) : IDisposable
    {
        public IntPtr State { get; } = Marshal.AllocHGlobal(StateSize);
        public bool Valid { get; private set; }

        public void Read()
        {
            try
            {
                device.Poll();
                var hr = device.GetDeviceState(StateSize, State);
                if (hr < 0 && device.Acquire() >= 0)
                {
                    device.Poll();
                    hr = device.GetDeviceState(StateSize, State);
                }

                Valid = hr >= 0;
            }
            catch (COMException)
            {
                Valid = false;
            }
        }

        public void Dispose()
        {
            try
            {
                device.Unacquire();
            }
            catch (COMException)
            {
            }

            Marshal.ReleaseComObject(device);
            Marshal.FreeHGlobal(State);
        }
    }
}
