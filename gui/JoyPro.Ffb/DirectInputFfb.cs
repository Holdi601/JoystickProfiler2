using System.Globalization;
using System.Runtime.InteropServices;
using JoyPro.Ffb.Core.Output;

namespace JoyPro.Ffb;

/// <summary>A force feedback device as DirectInput lists it (read-only enumeration).</summary>
public sealed record FfbDeviceInfo(
    Guid InstanceGuid,
    Guid ProductGuid,
    string Name,
    string ProductName,
    int VendorId,
    int ProductId,
    IReadOnlyList<string> ForceAxes,
    IReadOnlyList<string> Effects)
{
    public string InstanceToken => "{" + InstanceGuid.ToString().ToUpperInvariant() + "}";
}

/// <summary>DirectInput 8 force feedback: read-only device listing and the output backend.</summary>
/// <remarks>
/// Own P/Invoke/COM declarations after the DirectX SDK headers (dinput.h); no third-party library. The listing only
/// creates the device object to read its capabilities, effects and force axes: it never sets a cooperative level,
/// never acquires and never sends anything, so it is safe while a game uses the stick.
/// </remarks>
public static class DirectInputFfb
{
    internal const uint DirectInputVersion = 0x0800;
    internal const uint Di8DevClassGameCtrl = 4;
    internal const uint DiedflAttachedOnly = 0x00000001;
    internal const uint DiedflForceFeedback = 0x00000100;
    internal const uint DidcForceFeedback = 0x00000100;
    internal const uint DidftAxis = 0x00000003;
    internal const uint DidftFfActuator = 0x01000000;
    internal const uint DieftAll = 0;

    internal static readonly Guid IidIDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    internal static readonly Guid IidIDirectInputEffect = new("E7E1F7C0-88D2-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidXAxis = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
    internal static readonly Guid GuidYAxis = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
    internal static readonly Guid GuidConstantForce = new("13541C20-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidSquare = new("13541C22-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidSine = new("13541C23-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidTriangle = new("13541C24-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidSawtoothUp = new("13541C25-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidSawtoothDown = new("13541C26-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidSpring = new("13541C27-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidDamper = new("13541C28-8E33-11D0-9AD0-00A0C9A06E35");
    internal static readonly Guid GuidFriction = new("13541C2A-8E33-11D0-9AD0-00A0C9A06E35");

    private static readonly Dictionary<Guid, string> EffectNames = new()
    {
        [GuidConstantForce] = "ConstantForce",
        [new Guid("13541C21-8E33-11D0-9AD0-00A0C9A06E35")] = "Ramp",
        [GuidSquare] = "Square",
        [GuidSine] = "Sine",
        [GuidTriangle] = "Triangle",
        [GuidSawtoothUp] = "SawtoothUp",
        [GuidSawtoothDown] = "SawtoothDown",
        [GuidSpring] = "Spring",
        [GuidDamper] = "Damper",
        [new Guid("13541C29-8E33-11D0-9AD0-00A0C9A06E35")] = "Inertia",
        [GuidFriction] = "Friction",
        [new Guid("13541C2B-8E33-11D0-9AD0-00A0C9A06E35")] = "CustomForce"
    };

    private static readonly EnumCallback DeviceCallback = OnDevice;
    private static readonly EnumCallback EffectCallback = OnEffect;
    private static readonly EnumCallback ObjectCallback = OnObject;

    /// <summary>Attached force feedback game controllers with their effects and force axes. Never throws.</summary>
    public static List<FfbDeviceInfo> Enumerate(List<string>? warnings = null)
    {
        var result = new List<FfbDeviceInfo>();
        IDirectInput8W? directInput = null;
        try
        {
            directInput = Create();
            var instances = new List<DideviceInstanceW>();
            var handle = GCHandle.Alloc(instances);
            try
            {
                var hr = directInput.EnumDevices(Di8DevClassGameCtrl, Marshal.GetFunctionPointerForDelegate(DeviceCallback), GCHandle.ToIntPtr(handle), DiedflAttachedOnly | DiedflForceFeedback);
                if (hr < 0)
                    warnings?.Add("DirectInput EnumDevices failed: 0x" + hr.ToString("X8"));
            }
            finally
            {
                handle.Free();
            }

            foreach (var instance in instances)
            {
                var effects = new List<string>();
                var axes = new List<string>();
                var guid = instance.guidInstance;
                if (directInput.CreateDevice(ref guid, out var device, IntPtr.Zero) >= 0 && device is not null)
                {
                    try
                    {
                        var effectsHandle = GCHandle.Alloc(effects);
                        var axesHandle = GCHandle.Alloc(axes);
                        try
                        {
                            device.EnumEffects(Marshal.GetFunctionPointerForDelegate(EffectCallback), GCHandle.ToIntPtr(effectsHandle), DieftAll);
                            device.EnumObjects(Marshal.GetFunctionPointerForDelegate(ObjectCallback), GCHandle.ToIntPtr(axesHandle), DidftAxis | DidftFfActuator);
                        }
                        finally
                        {
                            effectsHandle.Free();
                            axesHandle.Free();
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                var product = instance.guidProduct.ToByteArray();
                var vid = product[0] | (product[1] << 8);
                var pid = product[2] | (product[3] << 8);
                result.Add(new FfbDeviceInfo(instance.guidInstance, instance.guidProduct, instance.tszInstanceName, instance.tszProductName, vid, pid, axes, effects));
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            warnings?.Add("DirectInput: " + ex.Message);
        }
        finally
        {
            if (directInput is not null)
                Marshal.ReleaseComObject(directInput);
        }

        return result;
    }

    /// <summary>The configured device: by instance GUID, else (empty setting) the first device with force axes.</summary>
    /// <remarks>Virtual devices such as vJoy list every effect but no force axis, so a real stick wins.</remarks>
    public static FfbDeviceInfo? Find(string setting, List<string>? warnings = null)
    {
        var devices = Enumerate(warnings);
        var wanted = (setting ?? "").Trim();
        if (wanted.Length == 0)
            return devices.FirstOrDefault(d => d.ForceAxes.Count > 0) ?? devices.FirstOrDefault();
        if (Guid.TryParse(wanted.Trim('{', '}'), out var guid))
            return devices.FirstOrDefault(d => d.InstanceGuid == guid);
        return devices.FirstOrDefault(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The effects a JoyPro command needs, by the names <see cref="Enumerate"/> reports.</summary>
    public static IReadOnlyCollection<FfbEffectType> Supported(IReadOnlyList<string> effects)
    {
        var set = new HashSet<FfbEffectType>();
        if (effects.Contains("ConstantForce"))
            set.Add(FfbEffectType.ConstantForce);
        if (effects.Contains("Spring"))
            set.Add(FfbEffectType.Spring);
        if (effects.Contains("Damper"))
            set.Add(FfbEffectType.Damper);
        if (effects.Contains("Friction"))
            set.Add(FfbEffectType.Friction);
        if (effects.Contains("Sine") || effects.Contains("Square") || effects.Contains("Triangle"))
            set.Add(FfbEffectType.Periodic);
        return set;
    }

    /// <summary>Attached game controllers (all of them, not only force feedback ones): instance guid and names.</summary>
    internal static List<DideviceInstanceW> Instances(IDirectInput8W directInput)
    {
        var instances = new List<DideviceInstanceW>();
        var handle = GCHandle.Alloc(instances);
        try
        {
            directInput.EnumDevices(Di8DevClassGameCtrl, Marshal.GetFunctionPointerForDelegate(DeviceCallback), GCHandle.ToIntPtr(handle), DiedflAttachedOnly);
        }
        finally
        {
            handle.Free();
        }

        return instances;
    }

    internal static IDirectInput8W Create()
    {
        var iid = IidIDirectInput8W;
        var hr = DirectInput8Create(GetModuleHandle(null), DirectInputVersion, ref iid, out var directInput, IntPtr.Zero);
        if (hr < 0 || directInput is null)
            throw new COMException("DirectInput8Create failed", hr);
        return directInput;
    }

    private static int OnDevice(IntPtr instance, IntPtr state)
    {
        try
        {
            if (GCHandle.FromIntPtr(state).Target is List<DideviceInstanceW> list)
                list.Add(Marshal.PtrToStructure<DideviceInstanceW>(instance));
        }
        catch (ArgumentException)
        {
        }

        return 1; // DIENUM_CONTINUE
    }

    private static int OnEffect(IntPtr info, IntPtr state)
    {
        try
        {
            if (GCHandle.FromIntPtr(state).Target is List<string> list)
            {
                var effect = Marshal.PtrToStructure<DieffectInfoW>(info);
                list.Add(EffectNames.TryGetValue(effect.guid, out var name) ? name : effect.tszName);
            }
        }
        catch (ArgumentException)
        {
        }

        return 1;
    }

    private static int OnObject(IntPtr info, IntPtr state)
    {
        try
        {
            if (GCHandle.FromIntPtr(state).Target is List<string> list)
            {
                var guidType = Marshal.PtrToStructure<Guid>(info + 4);
                var name = Marshal.PtrToStringUni(info + 32) ?? "";
                list.Add(guidType == GuidXAxis ? "X" : guidType == GuidYAxis ? "Y" : name);
            }
        }
        catch (ArgumentException)
        {
        }

        return 1;
    }

    // ------------------------------------------------------------------ interop (dinput.h)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int EnumCallback(IntPtr item, IntPtr state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("dinput8.dll", EntryPoint = "DirectInput8Create")]
    private static extern int DirectInput8Create(IntPtr hinst, uint version, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectInput8W directInput, IntPtr outer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct DideviceInstanceW
    {
        public int dwSize;
        public Guid guidInstance;
        public Guid guidProduct;
        public int dwDevType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string tszInstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string tszProductName;
        public Guid guidFFDriver;
        public ushort wUsagePage;
        public ushort wUsage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct DieffectInfoW
    {
        public int dwSize;
        public Guid guid;
        public uint dwEffType;
        public uint dwStaticParams;
        public uint dwDynamicParams;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string tszName;
    }

    [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectInput8W
    {
        [PreserveSig] int CreateDevice(ref Guid rguid, [MarshalAs(UnmanagedType.Interface)] out IDirectInputDevice8W device, IntPtr outer);
        [PreserveSig] int EnumDevices(uint devType, IntPtr callback, IntPtr state, uint flags);
        [PreserveSig] int GetDeviceStatus(ref Guid rguidInstance);
        [PreserveSig] int RunControlPanel(IntPtr hwndOwner, uint flags);
        [PreserveSig] int Initialize(IntPtr hinst, uint version);
        [PreserveSig] int FindDevice(ref Guid rguidClass, [MarshalAs(UnmanagedType.LPWStr)] string name, out Guid instance);
        [PreserveSig] int EnumDevicesBySemantics([MarshalAs(UnmanagedType.LPWStr)] string userName, IntPtr actionFormat, IntPtr callback, IntPtr state, uint flags);
        [PreserveSig] int ConfigureDevices(IntPtr callback, IntPtr parameters, uint flags, IntPtr state);
    }

    [ComImport, Guid("54D41080-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectInputDevice8W
    {
        [PreserveSig] int GetCapabilities(IntPtr caps);
        [PreserveSig] int EnumObjects(IntPtr callback, IntPtr state, uint flags);
        [PreserveSig] int GetProperty(ref Guid property, IntPtr header);
        // REFGUID: the predefined properties (DIPROP_AUTOCENTER = 9) are passed as the pointer value itself.
        [PreserveSig] int SetProperty(IntPtr property, IntPtr header);
        [PreserveSig] int Acquire();
        [PreserveSig] int Unacquire();
        [PreserveSig] int GetDeviceState(int size, IntPtr data);
        [PreserveSig] int GetDeviceData(int objectSize, IntPtr data, ref int inOut, uint flags);
        [PreserveSig] int SetDataFormat(IntPtr format);
        [PreserveSig] int SetEventNotification(IntPtr handle);
        [PreserveSig] int SetCooperativeLevel(IntPtr hwnd, uint flags);
        [PreserveSig] int GetObjectInfo(IntPtr info, uint obj, uint how);
        [PreserveSig] int GetDeviceInfo(IntPtr info);
        [PreserveSig] int RunControlPanel(IntPtr hwndOwner, uint flags);
        [PreserveSig] int Initialize(IntPtr hinst, uint version, ref Guid rguid);
        [PreserveSig] int CreateEffect(ref Guid rguid, IntPtr effect, out IntPtr directInputEffect, IntPtr outer);
        [PreserveSig] int EnumEffects(IntPtr callback, IntPtr state, uint effectType);
        [PreserveSig] int GetEffectInfo(IntPtr info, ref Guid rguid);
        [PreserveSig] int GetForceFeedbackState(out uint state);
        [PreserveSig] int SendForceFeedbackCommand(uint flags);
        [PreserveSig] int EnumCreatedEffectObjects(IntPtr callback, IntPtr state, uint flags);
        [PreserveSig] int Escape(IntPtr escape);
        [PreserveSig] int Poll();
    }

    [ComImport, Guid("E7E1F7C0-88D2-11D0-9AD0-00A0C9A06E35"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirectInputEffect
    {
        [PreserveSig] int Initialize(IntPtr hinst, uint version, ref Guid rguid);
        [PreserveSig] int GetEffectGuid(out Guid guid);
        [PreserveSig] int GetParameters(IntPtr effect, uint flags);
        [PreserveSig] int SetParameters(IntPtr effect, uint flags);
        [PreserveSig] int Start(uint iterations, uint flags);
        [PreserveSig] int Stop();
        [PreserveSig] int GetEffectStatus(out uint status);
        [PreserveSig] int Download();
        [PreserveSig] int Unload();
        [PreserveSig] int Escape(IntPtr escape);
    }
}

/// <summary>Sends JoyPro's effect commands to one DirectInput force feedback device.</summary>
/// <remarks>
/// - The device is acquired exclusively (background) on the first <see cref="Apply"/> and released by
///   <see cref="StopAll"/>: the runtime calls that when the telemetry stops (unless JoyPro holds the centring spring,
///   see FfbCentringMode.Ensure) and when the process ends, so outside a flight the device is not held.
/// - Acquire: DIPROP_AUTOCENTER off before Acquire (the property cannot change while acquired). DirectInput itself
///   resets the device on an exclusive Acquire and, with autocenter off, stops all effects (USB captures, see Wine's
///   dinput); JoyPro then only sends STOP ALL and ACTUATORS ON. No explicit RESET after Acquire: on PID devices a
///   reset is the device's startup state, which brings the firmware's own centring back.
/// - Release (<see cref="StopAll"/>, never throws): STOP ALL, unload JoyPro's effects, RESET while still acquired
///   (the device's startup state, with its default centring if it has one), Unacquire (DirectInput resets an
///   exclusively acquired device again) and DIPROP_AUTOCENTER on (the default for the next program).
///   Measured with the VPforce Rhino: a firmware spring switched on in the VPforce Configurator overrides every
///   DirectInput spring and is not controlled by DIPROP_AUTOCENTER; switched off there, the stick is slack whenever
///   no program drives it. JoyPro cannot change that firmware setting; it keeps its own centring spring active while
///   it owns the device instead (docs/ffb-telemetry.md).
/// - One DirectInput effect per (axis, effect type, slot, waveform), single axis, created on first use and reused.
///   Constant and periodic effects have the command's finite duration and are restarted (DIEP_START) once half of it
///   has passed: without refresh (a hung or killed process) the device ends them on its own. Condition effects
///   (spring, damper, friction) are passive -- they only resist the user's hand -- so they run with an infinite
///   duration, are started once and only get their changed parameters (DIEP_TYPESPECIFICPARAMS, no restart): a
///   restarted spring drops its force for a moment and jitters. A killed process loses its exclusive acquisition, and
///   DirectInput stops its effects then. Between restarts only changed parameters are sent, so the USB traffic stays
///   far below the device's report rate (a USB HID device takes about 1000 reports a second; each restart costs two,
///   each change one). Periodic effects restart at the phase they reached. Effects missing from an Apply are stopped.
/// - Forces in JoyPro's convention: a positive magnitude pushes towards the positive end of the axis (right, and
///   towards the pilot on Y). Measured on the VPforce Rhino: X matches, Y is reversed (a negative Y force pushed back
///   towards the pilot); <see cref="InvertX"/>/<see cref="InvertY"/> (the settings, found by the direction test) flip
///   forces and the spring centre (the Rhino's spring offset follows the force direction).
/// All calls come from the force loop thread (and Dispose); the window for SetCooperativeLevel lives on its own
/// message thread (<see cref="HiddenWindow"/>).
/// </remarks>
public sealed class DirectInputFfbBackend : IFfbBackend
{
    private const uint DisclExclusive = 0x1;
    private const uint DisclBackground = 0x8;
    private const uint DisffcReset = 0x1;
    private const uint DisffcStopAll = 0x2;
    private const uint DisffcSetActuatorsOn = 0x10;
    private const uint DiepDuration = 0x1;
    private const uint DiepTypeSpecificParams = 0x100;
    private const uint DiepStart = 0x20000000;
    private const string DeviceId = "directinput";


    private readonly FfbDeviceInfo _info;
    private readonly Dictionary<(FfbAxis, FfbEffectType, int, FfbWaveform), Effect> _effects = new();
    private readonly HashSet<(FfbAxis, FfbEffectType, int, FfbWaveform)> _seen = new();
    private DirectInputFfb.IDirectInput8W? _directInput;
    private DirectInputFfb.IDirectInputDevice8W? _device;
    private HiddenWindow? _window;
    private IntPtr _dataFormat;
    private IntPtr _effectBuffer;
    private bool _acquired;

    public DirectInputFfbBackend(FfbDeviceInfo info, bool invertX, bool invertY)
    {
        _info = info;
        InvertX = invertX;
        InvertY = invertY;
        var axes = new List<FfbAxis>();
        if (info.ForceAxes.Contains("X") || info.ForceAxes.Count == 0)
            axes.Add(FfbAxis.X);
        if (info.ForceAxes.Contains("Y") || info.ForceAxes.Count == 0)
            axes.Add(FfbAxis.Y);
        Devices = [new FfbDeviceCapabilities(DeviceId, info.Name, axes, DirectInputFfb.Supported(info.Effects), 500)];
    }

    public string Name => "DirectInput: " + _info.Name;
    public FfbDeviceInfo Info => _info;

    /// <summary>Flip forces (and the spring centre) on X / Y. Changing it takes effect with the next parameters sent
    /// (the direction test sets it between pushes, with every effect stopped).</summary>
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public IReadOnlyList<FfbDeviceCapabilities> Devices { get; }
    public bool Acquired => _acquired;
    public long Updates { get; private set; }

    /// <summary>SetParameters/Start calls sent to the device (diagnostics).</summary>
    public long DeviceCalls { get; private set; }

    /// <summary>How spring, damper and friction go to the device (default: one 2-axis effect per type and slot).</summary>
    /// <remarks>Measured on the VPforce Rhino (2026-10-11): with one single-axis spring per axis, a Y centre offset
    /// moved X and Y stayed. Combined (one effect, X and Y with a DICONDITION each, like a game's 2-axis spring) is the
    /// default; the other layouts stay for a hardware A/B test (JoyPro.Ffb test: "conditions ...").</remarks>
    public FfbConditionLayout ConditionLayout { get; set; } = FfbConditionLayout.Combined;

    /// <summary>Every device call with its values and HRESULT (the test process logs all, the run process rate-limits).</summary>
    public Action<string>? Log { get; set; }

    private const FfbAxis CombinedAxes = (FfbAxis)(-1);
    private readonly Dictionary<(FfbEffectType, int), (FfbEffectCommand? X, FfbEffectCommand? Y)> _conditions = new();

    private bool CombineConditions => ConditionLayout == FfbConditionLayout.Combined && Devices[0].Axes.Count == 2;

    public void Apply(double time, IReadOnlyList<FfbEffectCommand> commands)
    {
        EnsureAcquired();
        _seen.Clear();
        _conditions.Clear();
        var combine = CombineConditions;
        if (combine)
            DirectInputLayout.PairConditions(commands, _conditions);
        foreach (var command in commands)
        {
            if (combine && DirectInputLayout.IsCondition(command.Type))
                continue;
            var key = (command.Axis, command.Type, command.Slot, command.Type == FfbEffectType.Periodic ? command.Waveform : FfbWaveform.Sine);
            _seen.Add(key);
            Update(key, command, time);
        }

        foreach (var pair in _conditions)
        {
            var key = (CombinedAxes, pair.Key.Item1, pair.Key.Item2, FfbWaveform.Sine);
            _seen.Add(key);
            UpdateCombined(key, pair.Key.Item1, pair.Value.X, pair.Value.Y, time);
        }

        foreach (var pair in _effects)
        {
            if (!_seen.Contains(pair.Key) && pair.Value.Playing)
            {
                var hr = pair.Value.Com.Stop();
                Trace("Stop " + Describe(pair.Key), hr);
                pair.Value.Playing = false;
            }
        }

        Updates++;
    }

    /// <summary>Release (never throws): STOP ALL, stop and unload every effect, RESET, Unacquire, autocenter on.</summary>
    public void StopAll()
    {
        var wasAcquired = _device is not null && _acquired;
        if (!wasAcquired)
        {
            _effects.Clear();
            return;
        }

        Try(() => Trace("DISFFC_STOPALL", _device!.SendForceFeedbackCommand(DisffcStopAll)));
        foreach (var effect in _effects.Values)
            effect.Release();
        Log?.Invoke("Released " + _effects.Count.ToString(CultureInfo.InvariantCulture) + " effect(s)");
        _effects.Clear();

        // Hand the device back in its startup state: RESET while still ours (with it the device's default centring,
        // if it has one), then Unacquire (DirectInput resets an exclusively acquired device as well) and autocenter
        // on again, the default for the next program.
        Try(() => Trace("DISFFC_RESET", _device!.SendForceFeedbackCommand(DisffcReset)));
        Try(() => Trace("Unacquire", _device!.Unacquire()));
        _acquired = false;
        Trace("DIPROP_AUTOCENTER on", SetAutoCenter(true));
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }

    // DIPROP_AUTOCENTER with a DIPROPDWORD for the whole device (DIPH_DEVICE). Best effort: a device without the
    // property simply keeps its behaviour.
    private int SetAutoCenter(bool on)
    {
        if (_device is null)
            return -1;
        var header = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.WriteInt32(header, 0, 20);      // dwSize (DIPROPDWORD)
            Marshal.WriteInt32(header, 4, 16);      // dwHeaderSize (DIPROPHEADER)
            Marshal.WriteInt32(header, 8, 0);       // dwObj
            Marshal.WriteInt32(header, 12, 0);      // dwHow = DIPH_DEVICE
            Marshal.WriteInt32(header, 16, on ? 1 : 0); // DIPROPAUTOCENTER_ON / _OFF
            return _device.SetProperty(new IntPtr(9), header);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
        finally
        {
            Marshal.FreeHGlobal(header);
        }
    }

    public void Dispose()
    {
        StopAll();
        if (_device is not null)
        {
            Marshal.ReleaseComObject(_device);
            _device = null;
        }

        if (_directInput is not null)
        {
            Marshal.ReleaseComObject(_directInput);
            _directInput = null;
        }

        _window?.Dispose();
        _window = null;
        if (_dataFormat != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_dataFormat);
            _dataFormat = IntPtr.Zero;
        }

        if (_effectBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_effectBuffer);
            _effectBuffer = IntPtr.Zero;
        }
    }

    private void EnsureAcquired()
    {
        if (_acquired)
            return;
        if (_device is null)
        {
            _directInput ??= DirectInputFfb.Create();
            var guid = _info.InstanceGuid;
            Check(_directInput.CreateDevice(ref guid, out var device, IntPtr.Zero), "CreateDevice");
            _device = device;
            _dataFormat = BuildDataFormat();
            Check(_device.SetDataFormat(_dataFormat), "SetDataFormat");
            _window ??= HiddenWindow.Create();
            Check(_device.SetCooperativeLevel(_window.Handle, DisclExclusive | DisclBackground), "SetCooperativeLevel");
        }

        // DirectInput's autocenter (the device's default spring after a reset) would fight JoyPro's spring and its trim
        // offset: off while JoyPro drives the stick (standard practice for force feedback programs), on again when it
        // lets go. Set before Acquire: the property cannot change while acquired. Some firmwares (the VPforce
        // Configurator's "Spring") keep their own spring regardless; see the remarks above.
        Trace("DIPROP_AUTOCENTER off", SetAutoCenter(false));
        Call(_device.Acquire(), "Acquire (another program may hold the device exclusively, e.g. the game's own force feedback)");
        _acquired = true;
        Trace("DISFFC_STOPALL", _device.SendForceFeedbackCommand(DisffcStopAll));
        Trace("DISFFC_SETACTUATORSON (inverts X " + InvertX + ", Y " + InvertY + ", conditions " + ConditionLayout + ")", _device.SendForceFeedbackCommand(DisffcSetActuatorsOn));
    }

    private void Update((FfbAxis, FfbEffectType, int, FfbWaveform) key, FfbEffectCommand command, double time)
    {
        var buffer = EffectBuffer();
        if (!_effects.TryGetValue(key, out var effect))
        {
            var phase = PhaseAt(command, time);
            WriteEffect(buffer, command, phase, out var guid);
            effect = Create(guid, buffer, Describe(key) + " " + Values(command));
            _effects[key] = effect;
            Call(effect.Com.Start(1, 0), "Start " + Describe(key));
            effect.Restarted(command, time, phase);
            DeviceCalls += 2;
            return;
        }

        if (DirectInputLayout.IsCondition(command.Type) && ConditionLayout != FfbConditionLayout.SeparateRestart)
        {
            // Infinite and passive: started once, then only changed parameters (no restart, no jitter).
            if (!effect.Playing)
            {
                WriteEffect(buffer, command, 0, out _);
                Call(effect.Com.SetParameters(buffer, DiepTypeSpecificParams | DiepStart), "SetParameters+Start " + Describe(key) + " " + Values(command));
                effect.Restarted(command, time, 0);
                DeviceCalls++;
            }
            else if (Changed(effect.Sent, command))
            {
                WriteEffect(buffer, command, 0, out _);
                Call(effect.Com.SetParameters(buffer, DiepTypeSpecificParams), "SetParameters " + Describe(key) + " " + Values(command));
                effect.Sent = command;
                DeviceCalls++;
            }

            return;
        }

        if (!effect.Playing || time - effect.StartTime >= command.DurationSeconds * 0.5)
        {
            var phase = PhaseAt(command, time);
            WriteEffect(buffer, command, phase, out _);
            Call(effect.Com.SetParameters(buffer, DiepDuration | DiepTypeSpecificParams | DiepStart), "Restart " + Describe(key) + " " + Values(command));
            effect.Restarted(command, time, phase);
            DeviceCalls++;
        }
        else if (Changed(effect.Sent, command))
        {
            WriteEffect(buffer, command, effect.Phase, out _);
            Call(effect.Com.SetParameters(buffer, DiepTypeSpecificParams), "SetParameters " + Describe(key) + " " + Values(command));
            effect.Sent = command;
            DeviceCalls++;
        }
    }

    // One condition effect for both axes: a DICONDITION per axis (X first), infinite, never restarted. An axis without
    // this effect gets an all-zero condition (no force).
    private void UpdateCombined((FfbAxis, FfbEffectType, int, FfbWaveform) key, FfbEffectType type, FfbEffectCommand? x, FfbEffectCommand? y, double time)
    {
        var buffer = EffectBuffer();
        var conditions = WriteCombined(buffer, x, y);
        var detail = Describe(key) + " X: " + conditions[0] + "; Y: " + conditions[1];
        if (!_effects.TryGetValue(key, out var effect))
        {
            effect = Create(GuidFor(type), buffer, detail);
            _effects[key] = effect;
            Call(effect.Com.Start(1, 0), "Start " + Describe(key));
            effect.Restarted(x ?? y!.Value, time, 0);
            (effect.SentX, effect.SentY) = (x, y);
            DeviceCalls += 2;
            return;
        }

        if (!effect.Playing)
        {
            Call(effect.Com.SetParameters(buffer, DiepTypeSpecificParams | DiepStart), "SetParameters+Start " + detail);
            effect.Restarted(x ?? y!.Value, time, 0);
            (effect.SentX, effect.SentY) = (x, y);
            DeviceCalls++;
        }
        else if (Changed(effect.SentX, x) || Changed(effect.SentY, y))
        {
            Call(effect.Com.SetParameters(buffer, DiepTypeSpecificParams), "SetParameters " + detail);
            (effect.SentX, effect.SentY) = (x, y);
            DeviceCalls++;
        }
    }

    private DiCondition[] WriteCombined(IntPtr buffer, FfbEffectCommand? x, FfbEffectCommand? y)
    {
        DiCondition[] conditions =
        [
            DirectInputLayout.Condition(x, DirectInputLayout.Sign(FfbAxis.X, InvertX, InvertY)),
            DirectInputLayout.Condition(y, DirectInputLayout.Sign(FfbAxis.Y, InvertX, InvertY))
        ];
        DirectInputLayout.WriteCondition(_bytes, buffer.ToInt64(), [FfbAxis.X, FfbAxis.Y], conditions);
        Marshal.Copy(_bytes, 0, buffer, _bytes.Length);
        return conditions;
    }

    private Effect Create(Guid guid, IntPtr buffer, string detail)
    {
        Call(_device!.CreateEffect(ref guid, buffer, out var raw, IntPtr.Zero), "CreateEffect " + detail);
        try
        {
            return new Effect((DirectInputFfb.IDirectInputEffect)Marshal.GetObjectForIUnknown(raw));
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    private static Guid GuidFor(FfbEffectType type) => type switch
    {
        FfbEffectType.Spring => DirectInputFfb.GuidSpring,
        FfbEffectType.Damper => DirectInputFfb.GuidDamper,
        FfbEffectType.Friction => DirectInputFfb.GuidFriction,
        _ => DirectInputFfb.GuidConstantForce
    };

    /// <summary>Spring, damper and friction: passive effects that run with an infinite duration.</summary>
    public static bool IsCondition(FfbEffectType type) => DirectInputLayout.IsCondition(type);

    /// <summary>The DirectInput duration (microseconds) an effect is downloaded with: infinite for condition effects
    /// (unless the restart layout is chosen), the command's own (10 ms .. 5 s) for forces.</summary>
    private uint DurationFor(FfbEffectCommand c) =>
        IsCondition(c.Type) && ConditionLayout != FfbConditionLayout.SeparateRestart
            ? DirectInputLayout.Infinite
            : (uint)Math.Clamp(c.DurationSeconds * 1_000_000, 10_000, 5_000_000);

    // Below about half a percent (of the device's full force or of 1 Hz) a change is not worth a USB report.
    private static bool Changed(FfbEffectCommand a, FfbEffectCommand b) =>
        Math.Abs(a.Magnitude - b.Magnitude) > 0.005 || Math.Abs(a.Coefficient - b.Coefficient) > 0.005 ||
        Math.Abs(a.Centre - b.Centre) > 0.005 || Math.Abs(a.Saturation - b.Saturation) > 0.005 ||
        Math.Abs(a.FrequencyHz - b.FrequencyHz) > 0.05;

    private static bool Changed(FfbEffectCommand? a, FfbEffectCommand? b) =>
        a is { } first ? b is not { } second || Changed(first, second) : b is not null;

    // The phase the waveform has at this moment (hundredths of a degree), so a restart continues it.
    private static int PhaseAt(FfbEffectCommand c, double time)
    {
        if (c.Type != FfbEffectType.Periodic)
            return 0;
        var cycles = time * Math.Clamp(c.FrequencyHz, 0.5, 500);
        return (int)((cycles - Math.Floor(cycles)) * 36000) % 36000;
    }

    private readonly byte[] _bytes = new byte[DirectInputLayout.BufferSize];

    private IntPtr EffectBuffer()
    {
        if (_effectBuffer == IntPtr.Zero)
            _effectBuffer = Marshal.AllocHGlobal(DirectInputLayout.BufferSize);
        return _effectBuffer;
    }

    // A single-axis effect (DirectInputLayout: DIEFFECT, the axis' offset, direction 1, the type-specific block).
    private void WriteEffect(IntPtr buffer, FfbEffectCommand c, int phase, out Guid guid)
    {
        var sign = DirectInputLayout.Sign(c.Axis, InvertX, InvertY);
        int[] parameters;
        switch (c.Type)
        {
            case FfbEffectType.ConstantForce:
                guid = DirectInputFfb.GuidConstantForce;
                parameters = [DirectInputLayout.Nominal(c.Magnitude * sign)];
                break;
            case FfbEffectType.Periodic:
                guid = c.Waveform switch
                {
                    FfbWaveform.Square => DirectInputFfb.GuidSquare,
                    FfbWaveform.Triangle => DirectInputFfb.GuidTriangle,
                    FfbWaveform.SawUp => DirectInputFfb.GuidSawtoothUp,
                    FfbWaveform.SawDown => DirectInputFfb.GuidSawtoothDown,
                    _ => DirectInputFfb.GuidSine
                };
                var frequency = Math.Clamp(c.FrequencyHz, 0.5, 500);
                // DIPERIODIC: dwMagnitude, lOffset, dwPhase, dwPeriod (us)
                parameters = [Math.Abs(DirectInputLayout.Nominal(c.Magnitude)), 0, phase, (int)Math.Round(1_000_000 / frequency)];
                break;
            default:
                guid = GuidFor(c.Type);
                parameters = DirectInputLayout.ConditionParams([DirectInputLayout.Condition(c, sign)]);
                break;
        }

        DirectInputLayout.Write(_bytes, buffer.ToInt64(), DurationFor(c), [DirectInputLayout.AxisOffset(c.Axis)], [1], parameters);
        Marshal.Copy(_bytes, 0, buffer, _bytes.Length);
    }

    // ------------------------------------------------------------------ log

    private void Trace(string what, int hr) => Log?.Invoke(what + " -> " + (hr >= 0 ? "ok" : "0x" + hr.ToString("X8", CultureInfo.InvariantCulture)));

    private void Call(int hr, string what)
    {
        Trace(what, hr);
        Check(hr, what);
    }

    private static string Describe((FfbAxis Axis, FfbEffectType Type, int Slot, FfbWaveform Waveform) key) =>
        key.Type + (key.Type == FfbEffectType.Periodic ? "/" + key.Waveform : "") + " " + (key.Axis == CombinedAxes ? "X+Y" : key.Axis.ToString()) +
        (key.Slot > 0 ? " slot " + key.Slot.ToString(CultureInfo.InvariantCulture) : "");

    // The command's values and what the device gets (after the invert).
    private string Values(FfbEffectCommand c)
    {
        var sign = DirectInputLayout.Sign(c.Axis, InvertX, InvertY);
        return c.Type switch
        {
            FfbEffectType.ConstantForce => string.Create(CultureInfo.InvariantCulture, $"magnitude {c.Magnitude:0.###} (device {DirectInputLayout.Nominal(c.Magnitude * sign)})"),
            FfbEffectType.Periodic => string.Create(CultureInfo.InvariantCulture, $"magnitude {c.Magnitude:0.###}, {c.FrequencyHz:0.#} Hz"),
            _ => string.Create(CultureInfo.InvariantCulture, $"centre {c.Centre:0.###}, coefficient {c.Coefficient:0.###}, saturation {c.Saturation:0.###} (device {DirectInputLayout.Condition(c, sign)})")
        };
    }

    // A data format with just X and Y (offsets 0 and 4): enough to acquire, and the offsets the effects name.
    private static IntPtr BuildDataFormat()
    {
        const int objectSize = 24;     // DIOBJECTDATAFORMAT on x64
        const int formatSize = 32;     // DIDATAFORMAT on x64
        const uint didftAxis = 0x3;
        const uint didftAnyInstance = 0x00FFFF00;
        const uint didftOptional = 0x80000000;
        const uint didoiAspectPosition = 0x100;
        const uint didfAbsAxis = 0x1;
        var memory = Marshal.AllocHGlobal(formatSize + 2 * objectSize + 2 * 16);
        var objects = memory + formatSize;
        var guids = objects + 2 * objectSize;
        Marshal.StructureToPtr(DirectInputFfb.GuidXAxis, guids, false);
        Marshal.StructureToPtr(DirectInputFfb.GuidYAxis, guids + 16, false);
        for (var i = 0; i < 2; i++)
        {
            var o = objects + i * objectSize;
            Marshal.WriteIntPtr(o, 0, guids + i * 16);
            Marshal.WriteInt32(o, 8, i * 4);
            Marshal.WriteInt32(o, 12, unchecked((int)(didftAxis | didftAnyInstance | didftOptional)));
            Marshal.WriteInt32(o, 16, (int)didoiAspectPosition);
            Marshal.WriteInt32(o, 20, 0);
        }

        Marshal.WriteInt32(memory, 0, formatSize);  // dwSize
        Marshal.WriteInt32(memory, 4, objectSize);  // dwObjSize
        Marshal.WriteInt32(memory, 8, (int)didfAbsAxis);
        Marshal.WriteInt32(memory, 12, 8);          // dwDataSize
        Marshal.WriteInt32(memory, 16, 2);          // dwNumObjs
        Marshal.WriteInt32(memory, 20, 0);
        Marshal.WriteIntPtr(memory, 24, objects);   // rgodf
        return memory;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException("DirectInput " + what + " failed: 0x" + hr.ToString("X8"), hr);
    }

    private sealed class Effect(DirectInputFfb.IDirectInputEffect com)
    {
        public DirectInputFfb.IDirectInputEffect Com { get; } = com;
        public bool Playing { get; set; }
        public double StartTime { get; private set; }
        public int Phase { get; private set; }
        public FfbEffectCommand Sent { get; set; }
        public FfbEffectCommand? SentX { get; set; }
        public FfbEffectCommand? SentY { get; set; }

        public void Restarted(FfbEffectCommand command, double time, int phase)
        {
            Playing = true;
            StartTime = time;
            Phase = phase;
            Sent = command;
        }

        public void Release()
        {
            try
            {
                Com.Stop();
                Com.Unload();
            }
            catch (Exception)
            {
            }

            try
            {
                Marshal.ReleaseComObject(Com);
            }
            catch (Exception)
            {
            }
        }
    }
}
