using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace JoyPro
{
    internal static class DirectInputDeviceDiscovery
    {
        private const uint DirectInputVersion = 0x0800;
        private const uint Di8DevclassGameCtrl = 4;
        private const uint DiedflAttachedOnly = 0x00000001;
        private static readonly Guid IidIDirectInput8W = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
        private static readonly EnumDevicesCallback DeviceEnumCallback = EnumDevicesCallbackImpl;
        private static readonly IntPtr DeviceEnumCallbackPtr = Marshal.GetFunctionPointerForDelegate(DeviceEnumCallback);

        public static List<string> EnumerateConnectedDevices(List<string> warnings)
        {
            HashSet<string> output = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> ordered = EnumerateConnectedDevicesInOrder(warnings);
            for (int i = 0; i < ordered.Count; i++)
            {
                string value = ordered[i];
                if (!string.IsNullOrWhiteSpace(value))
                    output.Add(value);
            }

            return output.OrderBy(v => v).ToList();
        }

        public static List<string> EnumerateConnectedDevicesInOrder(List<string> warnings)
        {
            string orderOverride = Environment.GetEnvironmentVariable("JOYPRO_DI_ORDER_OVERRIDE");
            if (!string.IsNullOrWhiteSpace(orderOverride))
            {
                List<string> overridden = ParseOrderOverride(orderOverride);
                if (overridden.Count > 0)
                    return overridden;
            }

            List<string> output = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<DirectInputDeviceInfo> details = EnumerateConnectedDeviceDetails(warnings);
            for (int i = 0; i < details.Count; i++)
            {
                string value = details[i].InstanceToken;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (seen.Contains(value))
                    continue;

                seen.Add(value);
                output.Add(value);
            }

            return output;
        }

        private static List<string> ParseOrderOverride(string raw)
        {
            List<string> output = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
                return output;

            string[] tokens = value.Contains(";")
                ? value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = (tokens[i] ?? string.Empty).Trim();
                if (token.Length == 0 || seen.Contains(token))
                    continue;

                seen.Add(token);
                output.Add(token);
            }

            return output;
        }

        public static List<string> EnumerateConnectedProductsInOrder(List<string> warnings)
        {
            List<string> output = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<DirectInputDeviceInfo> details = EnumerateConnectedDeviceDetails(warnings);
            for (int i = 0; i < details.Count; i++)
            {
                string value = details[i].ProductToken;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (seen.Contains(value))
                    continue;

                seen.Add(value);
                output.Add(value);
            }

            return output;
        }

        private static int EnumDevicesCallbackImpl(IntPtr lpddi, IntPtr pvRef)
        {
            if (lpddi == IntPtr.Zero || pvRef == IntPtr.Zero)
                return 1;

            try
            {
                GCHandle handle = GCHandle.FromIntPtr(pvRef);
                EnumerationState state = handle.Target as EnumerationState;
                if (state == null || state.Output == null)
                    return 1;

                DideviceInstanceW instance = (DideviceInstanceW)Marshal.PtrToStructure(lpddi, typeof(DideviceInstanceW));
                DirectInputDeviceInfo info = new DirectInputDeviceInfo();
                info.InstanceName = instance.tszInstanceName;
                info.ProductName = instance.tszProductName;
                info.InstanceGuid = instance.guidInstance;
                info.ProductGuid = instance.guidProduct;
                info.InstanceToken = ToDirectInputInstanceString(instance.tszInstanceName, instance.guidInstance);
                info.ProductToken = ToDirectInputProductString(instance.tszInstanceName, instance.guidProduct);
                state.Output.Add(info);
            }
            catch
            {
                // Continue enumeration even if one instance fails to marshal.
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

            string id = FormatGuidToken(instanceGuid);
            if (id.Length == 0)
                return string.Empty;
            return safeName + " {" + id + "}";
        }

        private static string ToDirectInputProductString(string instanceName, Guid productGuid)
        {
            string safeName = (instanceName ?? string.Empty)
                .Replace("/", "_")
                .Replace("\"", "_")
                .Trim();
            if (safeName.Length == 0)
                return string.Empty;

            string id = FormatGuidToken(productGuid);
            if (id.Length == 0)
                return string.Empty;
            return safeName + " {" + id + "}";
        }

        internal static string FormatGuidToken(Guid guid)
        {
            string id = guid.ToString();
            string[] idParts = id.Split('-');
            if (idParts.Length > 2)
            {
                id = idParts[0].ToUpperInvariant() + "-" +
                     idParts[1].ToUpperInvariant() + "-" +
                     idParts[2].ToLowerInvariant();
                for (int i = 3; i < idParts.Length; i++)
                    id += "-" + idParts[i].ToUpperInvariant();
            }

            return id;
        }

        private static List<DirectInputDeviceInfo> EnumerateConnectedDeviceDetails(List<string> warnings)
        {
            List<DirectInputDeviceInfo> output = new List<DirectInputDeviceInfo>();
            IDirectInput8W directInput = null;
            GCHandle stateHandle = default(GCHandle);

            try
            {
                IntPtr hinst = GetModuleHandle(null);
                Guid iid = IidIDirectInput8W;
                int hr = DirectInput8Create(hinst, DirectInputVersion, ref iid, out directInput, IntPtr.Zero);
                if (hr != 0 || directInput == null)
                {
                    warnings?.Add("DirectInput create failed: 0x" + hr.ToString("X8"));
                    return output;
                }

                EnumerationState state = new EnumerationState();
                state.Output = output;
                stateHandle = GCHandle.Alloc(state);

                int enumHr = directInput.EnumDevices(
                    Di8DevclassGameCtrl,
                    DeviceEnumCallbackPtr,
                    GCHandle.ToIntPtr(stateHandle),
                    DiedflAttachedOnly);
                if (enumHr != 0)
                    warnings?.Add("DirectInput enum failed: 0x" + enumHr.ToString("X8"));
            }
            catch (Exception ex)
            {
                warnings?.Add("DirectInput query failed: " + ex.Message);
            }
            finally
            {
                if (stateHandle.IsAllocated)
                    stateHandle.Free();
                if (directInput != null && Marshal.IsComObject(directInput))
                    Marshal.ReleaseComObject(directInput);
            }

            return output;
        }

        // Connected game controllers in DirectInput enumeration order (the order games assign slots in).
        public static List<DirectInputDeviceInfo> EnumerateConnectedDeviceInfos(List<string> warnings)
        {
            return EnumerateConnectedDeviceDetails(warnings);
        }

        internal sealed class DirectInputDeviceInfo
        {
            public string InstanceName;
            // DIDEVICEINSTANCE.tszProductName, untrimmed (Cliffs of Dover Blitz names devices by it).
            public string ProductName;
            public Guid InstanceGuid;
            public Guid ProductGuid;
            public string InstanceToken;
            public string ProductToken;
        }

        private sealed class EnumerationState
        {
            public List<DirectInputDeviceInfo> Output;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumDevicesCallback(IntPtr lpddi, IntPtr pvRef);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

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
}
