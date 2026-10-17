using System.Runtime.InteropServices;

namespace JoyPro.VJoy;

/// <summary>DirectInput 8 (dinput.h), Win32 waits and timers, display settings. Own declarations, no third-party library.</summary>
internal static class Native
{
    public const uint DirectInputVersion = 0x0800;
    public const uint Di8DevClassGameCtrl = 4;
    public const uint DiedflAttachedOnly = 0x1;
    public const uint DisclNonExclusive = 0x2;
    public const uint DisclBackground = 0x8;
    public const int DiErrInputLost = unchecked((int)0x8007001E);
    public const int DiErrNotAcquired = unchecked((int)0x8007000C);
    public const int DiPropRange = 4;
    public const int DiPropGuidAndPath = 12;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x102;
    public const uint WaitFailed = 0xFFFFFFFF;
    public const uint Infinite = 0xFFFFFFFF;
    public const uint CreateWaitableTimerHighResolution = 0x2;
    public const uint TimerAllAccess = 0x1F0003;
    public const uint Synchronize = 0x00100000;

    public static readonly Guid IidIDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    public static readonly Guid GuidXAxis = new("A36D02E0-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidYAxis = new("A36D02E1-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidZAxis = new("A36D02E2-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidRxAxis = new("A36D02F4-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidRyAxis = new("A36D02F5-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidRzAxis = new("A36D02E3-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidSlider = new("A36D02E4-C9F3-11CF-BFC7-444553540000");
    public static readonly Guid GuidPov = new("A36D02F2-C9F3-11CF-BFC7-444553540000");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int EnumCallback(IntPtr item, IntPtr state);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("dinput8.dll", EntryPoint = "DirectInput8Create")]
    public static extern int DirectInput8Create(IntPtr hinst, uint version, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectInput8W directInput, IntPtr outer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe uint WaitForMultipleObjects(uint count, IntPtr* handles, bool waitAll, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int periodMs, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelWaitableTimer(IntPtr timer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint period);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct DideviceInstanceW
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

    [ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirectInput8W
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

    // Property calls take the predefined property id as a pointer value (MAKEDIPROP), hence IntPtr instead of ref Guid.
    [ComImport, Guid("54D41080-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirectInputDevice8W
    {
        [PreserveSig] int GetCapabilities(IntPtr caps);
        [PreserveSig] int EnumObjects(IntPtr callback, IntPtr state, uint flags);
        [PreserveSig] int GetProperty(IntPtr property, IntPtr header);
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

    // ------------------------------------------------------------------ displays

    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1;

    /// <summary>The highest refresh rate of the attached displays, at least 60 Hz.</summary>
    public static int HighestRefreshRateHz()
    {
        var best = 0;
        try
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
            {
                if ((device.StateFlags & AttachedToDesktop) != 0)
                {
                    var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
                    if (EnumDisplaySettingsW(device.DeviceName, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency > 1)
                        best = Math.Max(best, mode.dmDisplayFrequency);
                }

                device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return Math.Max(60, best);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DevMode devMode);
}

/// <summary>vJoyInterface.dll (vJoy SDK), loaded from the vJoy installation.</summary>
internal static class VJoyNative
{
    public enum VjdStat
    {
        Own,
        Free,
        Busy,
        Missing,
        Unknown
    }

    public const uint HidUsageX = 0x30;

    private static string? _dllPath;

    /// <summary>Where vJoyInterface.dll is loaded from (call before the first vJoy call).</summary>
    public static void Configure(string? dllPath)
    {
        _dllPath = dllPath;
        NativeLibrary.SetDllImportResolver(typeof(VJoyNative).Assembly, (name, _, _) =>
        {
            if (name != "vJoyInterface")
                return IntPtr.Zero;
            var path = _dllPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");
            return NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        });
    }

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool vJoyEnabled();

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    public static extern VjdStat GetVJDStatus(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AcquireVJD(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    public static extern void RelinquishVJD(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateVJD(uint rID, ref VJoyPosition data);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDButtonNumber(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDDiscPovNumber(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDContPovNumber(uint rID);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVJDAxisExist(uint rID, uint axis);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVJDAxisMax(uint rID, uint axis, ref long max);

    [DllImport("vJoyInterface", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVJDAxisMin(uint rID, uint axis, ref long min);
}
