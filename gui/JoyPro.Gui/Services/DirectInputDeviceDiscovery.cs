using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace JoyPro.Gui.Services;

[SupportedOSPlatform("windows")]
internal static class DirectInputDeviceDiscovery
{
    private const uint DirectInputVersion = 0x0800;
    private const uint Di8DevclassGameCtrl = 4;
    private const uint DiedflAttachedOnly = 0x00000001;
    private static readonly Guid IidIDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    private static readonly EnumDevicesCallback DeviceEnumCallback = EnumDevicesCallbackImpl;
    private static readonly IntPtr DeviceEnumCallbackPtr = Marshal.GetFunctionPointerForDelegate(DeviceEnumCallback);

    public static List<string> EnumerateConnectedDevices(List<string>? warnings)
    {
        HashSet<string> output = new(StringComparer.OrdinalIgnoreCase);
        IDirectInput8W? directInput = null;
        GCHandle stateHandle = default;

        try
        {
            IntPtr hinst = GetModuleHandle(null);
            Guid iid = IidIDirectInput8W;
            int hr = DirectInput8Create(hinst, DirectInputVersion, ref iid, out directInput, IntPtr.Zero);
            if (hr != 0 || directInput == null)
            {
                warnings?.Add(T("DirectInput create failed: 0x{0}", hr.ToString("X8")));
                return output.OrderBy(v => v).ToList();
            }

            EnumerationState state = new()
            {
                Output = output
            };
            stateHandle = GCHandle.Alloc(state);

            int enumHr = directInput.EnumDevices(
                Di8DevclassGameCtrl,
                DeviceEnumCallbackPtr,
                GCHandle.ToIntPtr(stateHandle),
                DiedflAttachedOnly);
            if (enumHr != 0)
                warnings?.Add(T("DirectInput enum failed: 0x{0}", enumHr.ToString("X8")));
        }
        catch (Exception ex)
        {
            warnings?.Add(T("DirectInput query failed: {0}", ex.Message));
        }
        finally
        {
            if (stateHandle.IsAllocated)
                stateHandle.Free();
            if (directInput != null && Marshal.IsComObject(directInput))
                Marshal.ReleaseComObject(directInput);
        }

        return output.OrderBy(v => v).ToList();
    }

    private static int EnumDevicesCallbackImpl(IntPtr lpddi, IntPtr pvRef)
    {
        if (lpddi == IntPtr.Zero || pvRef == IntPtr.Zero)
            return 1;

        try
        {
            GCHandle handle = GCHandle.FromIntPtr(pvRef);
            EnumerationState? state = handle.Target as EnumerationState;
            if (state == null || state.Output == null)
                return 1;

            DideviceInstanceW instance = Marshal.PtrToStructure<DideviceInstanceW>(lpddi);
            string formatted = ToDirectInputInstanceString(instance.tszInstanceName, instance.guidInstance);
            if (!string.IsNullOrWhiteSpace(formatted))
                state.Output.Add(formatted);
        }
        catch
        {
            // Ignore and continue enumeration.
        }

        return 1;
    }

    private static string ToDirectInputInstanceString(string instanceName, Guid instanceGuid)
    {
        string safeName = (instanceName ?? string.Empty)
            .Replace("/", "_")
            .Replace("\"", "_")
            .Trim();
        if (safeName.Length == 0)
            return string.Empty;

        string id = instanceGuid.ToString();
        string[] idParts = id.Split('-');
        if (idParts.Length > 2)
        {
            id = idParts[0].ToUpperInvariant() + "-" +
                 idParts[1].ToUpperInvariant() + "-" +
                 idParts[2].ToLowerInvariant();
            for (int i = 3; i < idParts.Length; i++)
                id += "-" + idParts[i].ToUpperInvariant();
        }

        return safeName + " {" + id + "}";
    }

    private sealed class EnumerationState
    {
        public HashSet<string>? Output { get; set; }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumDevicesCallback(IntPtr lpddi, IntPtr pvRef);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("dinput8.dll", CharSet = CharSet.Unicode, EntryPoint = "DirectInput8Create", PreserveSig = true)]
    private static extern int DirectInput8Create(
        IntPtr hinst,
        uint dwVersion,
        ref Guid riidltf,
        [MarshalAs(UnmanagedType.Interface)] out IDirectInput8W ppvOut,
        IntPtr punkOuter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DideviceInstanceW
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
    private interface IDirectInput8W
    {
        [PreserveSig]
        int CreateDevice(ref Guid rguid, out IntPtr lplpDirectInputDevice, IntPtr pUnkOuter);

        [PreserveSig]
        int EnumDevices(uint dwDevType, IntPtr lpCallback, IntPtr pvRef, uint dwFlags);

        [PreserveSig]
        int GetDeviceStatus(ref Guid rguidInstance);

        [PreserveSig]
        int RunControlPanel(IntPtr hwndOwner, uint dwFlags);

        [PreserveSig]
        int Initialize(IntPtr hinst, uint dwVersion);

        [PreserveSig]
        int FindDevice(ref Guid rguidClass, [MarshalAs(UnmanagedType.LPWStr)] string ptszName, out Guid pguidInstance);

        [PreserveSig]
        int EnumDevicesBySemantics(
            [MarshalAs(UnmanagedType.LPWStr)] string ptszUserName,
            IntPtr lpdiActionFormat,
            IntPtr lpCallback,
            IntPtr pvRef,
            uint dwFlags);

        [PreserveSig]
        int ConfigureDevices(
            IntPtr lpdiCallback,
            IntPtr lpdiCDParams,
            uint dwFlags,
            IntPtr pvRefData);
    }
}
