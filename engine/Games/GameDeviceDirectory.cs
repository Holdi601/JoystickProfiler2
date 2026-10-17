using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JoyPro.Engine.Games
{
    // A game controller as the different games see it: JoyPro's DirectInput string, the instance and
    // product GUIDs, USB VID/PID and the HID product string (which some games use instead of the
    // DirectInput name, e.g. WARDOGS).
    internal sealed class GameDevice
    {
        public string JoyProName { get; set; }
        public string InstanceName { get; set; }
        // DirectInput's product name as the device reports it (leading/trailing blanks kept); empty when unknown.
        public string ProductName { get; set; }
        public Guid InstanceGuid { get; set; }
        public Guid ProductGuid { get; set; }
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string HidProductString { get; set; }
        public int EnumerationIndex { get; set; }
        public bool Connected { get; set; }

        public GameDevice()
        {
            JoyProName = string.Empty;
            InstanceName = string.Empty;
            ProductName = string.Empty;
            HidProductString = string.Empty;
            HidPath = string.Empty;
            AxisSlots = new List<int>();
            VendorId = -1;
            ProductId = -1;
            EnumerationIndex = -1;
        }

        public bool HasVidPid
        {
            get { return VendorId >= 0 && ProductId >= 0; }
        }

        // Filled by GameDeviceDirectory.EnsureCapabilities: present axes as DIJOYSTATE2 slots (0 X .. 5 Rz,
        // 6/7 sliders) in DirectInput enumeration order, button and hat counts, and the HID interface path
        // (\\?\hid#vid_...), which X-Plane stores to tell identical devices apart.
        public bool CapabilitiesKnown { get; set; }
        public string HidPath { get; set; }
        public List<int> AxisSlots { get; set; }
        public int ButtonCount { get; set; }
        public int PovCount { get; set; }
    }

    internal sealed class GameDeviceDirectory
    {
        // Test hook: a TSV file with lines "<JoyPro device string>\t<VID hex>\t<PID hex>\t<HID product string>"
        // [\t<axes>\t<buttons>\t<hats>[\t<HID path>]] replaces real device discovery, so exports can be tested
        // without hardware.
        public const string FakeDevicesEnvironmentVariable = "JOYPRO_FAKE_DEVICES";

        private static readonly Regex JoyProDeviceRegex = new Regex(@"^(?<name>.*?)\s*\{(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}\s*$", RegexOptions.Compiled);
        private static readonly byte[] PidVidSuffix = { 0x00, 0x00, 0x50, 0x49, 0x44, 0x56, 0x49, 0x44 };

        public List<GameDevice> Devices { get; private set; }

        public GameDeviceDirectory()
        {
            Devices = new List<GameDevice>();
        }

        public static GameDeviceDirectory Discover(List<string> warnings)
        {
            string fakePath = Environment.GetEnvironmentVariable(FakeDevicesEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fakePath))
                return LoadFake(fakePath, warnings);

            GameDeviceDirectory directory = new GameDeviceDirectory();
            Dictionary<string, string> hidNames = HidProductNames.ByVidPid(warnings);
            List<DirectInputDeviceDiscovery.DirectInputDeviceInfo> infos = DirectInputDeviceDiscovery.EnumerateConnectedDeviceInfos(warnings);
            for (int i = 0; i < infos.Count; i++)
            {
                DirectInputDeviceDiscovery.DirectInputDeviceInfo info = infos[i];
                if (info == null || string.IsNullOrWhiteSpace(info.InstanceToken))
                    continue;

                GameDevice device = new GameDevice
                {
                    JoyProName = info.InstanceToken,
                    InstanceName = info.InstanceName ?? string.Empty,
                    ProductName = info.ProductName ?? string.Empty,
                    InstanceGuid = info.InstanceGuid,
                    ProductGuid = info.ProductGuid,
                    EnumerationIndex = i,
                    Connected = true
                };

                int vid;
                int pid;
                if (TryGetVidPid(info.ProductGuid, out vid, out pid))
                {
                    device.VendorId = vid;
                    device.ProductId = pid;
                    string hidName;
                    if (hidNames.TryGetValue(VidPidKey(vid, pid), out hidName))
                        device.HidProductString = hidName;
                }

                directory.Devices.Add(device);
            }

            // Once per engine run: the connected devices with VID:PID and HID name.
            if (JoyPro.Logging.JoyProLog.ShouldLog("devices", TimeSpan.FromHours(1)))
                JoyPro.Logging.JoyProLog.Debug("Devices", "Connected game devices: " + directory.Devices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                                          string.Concat(directory.Devices.Select(d => Environment.NewLine + "  " + d.JoyProName +
                                                                                                      " | vid:pid=" + (d.HasVidPid ? VidPidKey(d.VendorId, d.ProductId) : "?") +
                                                                                                      " | hid=\"" + d.HidProductString + "\"")));
            return directory;
        }

        private static GameDeviceDirectory LoadFake(string path, List<string> warnings)
        {
            GameDeviceDirectory directory = new GameDeviceDirectory();
            if (!File.Exists(path))
            {
                warnings?.Add("Fake device file not found: " + path);
                return directory;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                string[] parts = line.Split('\t');
                GameDevice device = new GameDevice
                {
                    JoyProName = parts[0].Trim(),
                    EnumerationIndex = directory.Devices.Count,
                    Connected = true
                };

                string name;
                Guid guid;
                if (TryParseJoyProName(device.JoyProName, out name, out guid))
                {
                    device.InstanceName = name;
                    device.InstanceGuid = guid;
                }

                int vid;
                int pid;
                if (parts.Length >= 3 &&
                    int.TryParse(parts[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid) &&
                    int.TryParse(parts[2].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid))
                {
                    device.VendorId = vid;
                    device.ProductId = pid;
                    device.ProductGuid = BuildPidVidGuid(vid, pid);
                }

                // The HID string is kept verbatim: some products have leading/trailing spaces.
                if (parts.Length >= 4)
                    device.HidProductString = parts[3];
                // DirectInput's product name is the HID product string for HID devices.
                device.ProductName = parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : device.InstanceName;

                // Optional capabilities: axes (e.g. "X,Y,RZ,SLIDER1"), button count, hat count, HID interface path.
                int buttons;
                int povs;
                if (parts.Length >= 7 &&
                    int.TryParse(parts[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out buttons) &&
                    int.TryParse(parts[6].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out povs))
                {
                    device.AxisSlots = parts[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(a =>
                        {
                            JoyInput axis;
                            return GameInputTokens.TryParse("JOY_" + a.Trim(), out axis) && axis.Kind == JoyInputKind.Axis ? axis.Index : -1;
                        })
                        .Where(slot => slot >= 0)
                        .ToList();
                    device.ButtonCount = buttons;
                    device.PovCount = povs;
                    device.CapabilitiesKnown = true;
                    if (parts.Length >= 8)
                        device.HidPath = parts[7].Trim();
                }

                directory.Devices.Add(device);
            }

            return directory;
        }

        public GameDevice FindByJoyProName(string joyProName)
        {
            string value = (joyProName ?? string.Empty).Trim();
            if (value.Length == 0)
                return null;

            GameDevice exact = Devices.FirstOrDefault(d => string.Equals(d.JoyProName, value, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            string name;
            Guid guid;
            if (TryParseJoyProName(value, out name, out guid))
            {
                GameDevice byGuid = Devices.FirstOrDefault(d => d.InstanceGuid == guid);
                if (byGuid != null)
                    return byGuid;
            }

            return null;
        }

        public bool EnsureCapabilities(GameDevice device, List<string> warnings)
        {
            if (device == null)
                return false;
            if (device.CapabilitiesKnown)
                return true;
            if (device.InstanceGuid == Guid.Empty)
                return false;

            List<int> axes;
            int buttons;
            int povs;
            string path;
            if (!DirectInputObjects.TryEnumerate(device.InstanceGuid, out axes, out buttons, out povs, out path, warnings))
                return false;
            device.AxisSlots = axes;
            device.ButtonCount = buttons;
            device.PovCount = povs;
            device.HidPath = path;
            device.CapabilitiesKnown = true;
            return true;
        }

        public GameDevice FindByInstanceGuid(Guid guid)
        {
            return Devices.FirstOrDefault(d => d.InstanceGuid == guid);
        }

        public List<GameDevice> FindByVidPid(int vid, int pid)
        {
            return Devices.Where(d => d.VendorId == vid && d.ProductId == pid).ToList();
        }

        public static bool TryParseJoyProName(string joyProName, out string name, out Guid guid)
        {
            name = string.Empty;
            guid = Guid.Empty;
            Match match = JoyProDeviceRegex.Match(joyProName ?? string.Empty);
            if (!match.Success)
                return false;

            name = match.Groups["name"].Value.Trim();
            return Guid.TryParse(match.Groups["guid"].Value, out guid);
        }

        public static string BuildJoyProName(string name, Guid instanceGuid)
        {
            return (name ?? string.Empty).Trim() + " {" + DirectInputDeviceDiscovery.FormatGuidToken(instanceGuid) + "}";
        }

        // DirectInput product GUIDs of HID devices are "PIDVID" GUIDs: Data1 = PID << 16 | VID.
        public static bool TryGetVidPid(Guid productGuid, out int vid, out int pid)
        {
            vid = -1;
            pid = -1;
            byte[] bytes = productGuid.ToByteArray();
            for (int i = 0; i < PidVidSuffix.Length; i++)
            {
                if (bytes[8 + i] != PidVidSuffix[i])
                    return false;
            }

            uint data1 = BitConverter.ToUInt32(bytes, 0);
            vid = (int)(data1 & 0xFFFF);
            pid = (int)(data1 >> 16);
            return true;
        }

        public static Guid BuildPidVidGuid(int vid, int pid)
        {
            byte[] bytes = new byte[16];
            BitConverter.GetBytes(((uint)pid << 16) | (uint)vid).CopyTo(bytes, 0);
            PidVidSuffix.CopyTo(bytes, 8);
            return new Guid(bytes);
        }

        public static string VidPidKey(int vid, int pid)
        {
            return vid.ToString("X4", CultureInfo.InvariantCulture) + ":" + pid.ToString("X4", CultureInfo.InvariantCulture);
        }
    }

    // Reads HID product strings of present devices through SetupAPI, keyed by "VID:PID".
    internal static class HidProductNames
    {
        private const int DigcfPresent = 0x2;
        private const int DigcfDeviceInterface = 0x10;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        public static Dictionary<string, string> ByVidPid(List<string> warnings)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr deviceInfoSet = InvalidHandleValue;
            try
            {
                Guid hidGuid;
                HidD_GetHidGuid(out hidGuid);
                deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
                if (deviceInfoSet == InvalidHandleValue)
                    return result;

                for (uint index = 0; ; index++)
                {
                    SpDeviceInterfaceData interfaceData = new SpDeviceInterfaceData();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                        break;

                    string path = GetInterfacePath(deviceInfoSet, ref interfaceData);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    AddProductString(path, result);
                }
            }
            catch (Exception ex)
            {
                warnings?.Add("HID product names could not be read: " + ex.Message);
            }
            finally
            {
                if (deviceInfoSet != InvalidHandleValue)
                    SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        private static string GetInterfacePath(IntPtr deviceInfoSet, ref SpDeviceInterfaceData interfaceData)
        {
            int requiredSize;
            SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
            if (requiredSize <= 0)
                return string.Empty;

            IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, requiredSize, out requiredSize, IntPtr.Zero))
                    return string.Empty;
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void AddProductString(string path, Dictionary<string, string> result)
        {
            // Access 0 is enough for attributes and strings and works for devices opened exclusively.
            IntPtr handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle == InvalidHandleValue)
                return;

            try
            {
                HiddAttributes attributes = new HiddAttributes();
                attributes.Size = Marshal.SizeOf(typeof(HiddAttributes));
                if (!HidD_GetAttributes(handle, ref attributes))
                    return;

                string key = GameDeviceDirectory.VidPidKey(attributes.VendorID, attributes.ProductID);
                if (result.ContainsKey(key))
                    return;

                StringBuilder name = new StringBuilder(256);
                if (HidD_GetProductString(handle, name, name.Capacity * 2))
                    result[key] = name.ToString();
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    // Enumerates a device's axes, buttons and hats through IDirectInput8/IDirectInputDevice8 vtables.
    internal static class DirectInputObjects
    {
        private static readonly Guid IidDirectInput8W = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
        private static readonly Dictionary<Guid, int> AxisSlotsByType = new Dictionary<Guid, int>
        {
            { new Guid("A36D02E0-C9F3-11CF-BFC7-444553540000"), 0 },
            { new Guid("A36D02E1-C9F3-11CF-BFC7-444553540000"), 1 },
            { new Guid("A36D02E2-C9F3-11CF-BFC7-444553540000"), 2 },
            { new Guid("A36D02F4-C9F3-11CF-BFC7-444553540000"), 3 },
            { new Guid("A36D02F5-C9F3-11CF-BFC7-444553540000"), 4 },
            { new Guid("A36D02E3-C9F3-11CF-BFC7-444553540000"), 5 }
        };
        private static readonly Guid SliderType = new Guid("A36D02E4-C9F3-11CF-BFC7-444553540000");
        private const uint DidftAxis = 0x03;
        private const uint DidftButton = 0x0C;
        private const uint DidftPov = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DiDeviceObjectInstanceW
        {
            public int dwSize;
            public Guid guidType;
            public int dwOfs;
            public int dwType;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string tszName;
            public int dwFFMaxForce;
            public int dwFFForceResolution;
            public ushort wCollectionNumber;
            public ushort wDesignatorIndex;
            public ushort wUsagePage;
            public ushort wUsage;
            public int dwDimension;
            public ushort wExponent;
            public ushort wReportId;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumObjectsCallback(IntPtr objectInstance, IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDeviceFn(IntPtr self, ref Guid instance, out IntPtr device, IntPtr outer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumObjectsFn(IntPtr self, EnumObjectsCallback callback, IntPtr context, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPropertyFn(IntPtr self, IntPtr property, IntPtr header);
        private const int DipropGuidAndPath = 12;

        [DllImport("dinput8.dll")]
        private static extern int DirectInput8Create(IntPtr instance, uint version, ref Guid iid, out IntPtr directInput, IntPtr outer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        public static bool TryEnumerate(Guid instanceGuid, out List<int> axisSlots, out int buttons, out int povs, out string hidPath, List<string> warnings)
        {
            List<int> axes = new List<int>();
            int buttonCount = 0;
            int povCount = 0;
            axisSlots = axes;
            buttons = 0;
            povs = 0;
            hidPath = string.Empty;

            IntPtr directInput = IntPtr.Zero;
            IntPtr device = IntPtr.Zero;
            try
            {
                Guid iid = IidDirectInput8W;
                if (DirectInput8Create(GetModuleHandle(null), 0x0800, ref iid, out directInput, IntPtr.Zero) != 0 || directInput == IntPtr.Zero)
                    return false;
                Guid instance = instanceGuid;
                if (VTable<CreateDeviceFn>(directInput, 3)(directInput, ref instance, out device, IntPtr.Zero) != 0 || device == IntPtr.Zero)
                    return false;

                int sliders = 0;
                EnumObjectsCallback axisCallback = (objectInstance, context) =>
                {
                    DiDeviceObjectInstanceW info = (DiDeviceObjectInstanceW)Marshal.PtrToStructure(objectInstance, typeof(DiDeviceObjectInstanceW));
                    int slot;
                    if (AxisSlotsByType.TryGetValue(info.guidType, out slot))
                        axes.Add(slot);
                    else if (info.guidType == SliderType && sliders < 2)
                        axes.Add(6 + sliders++);
                    return 1;
                };
                EnumObjectsCallback buttonCallback = (objectInstance, context) => { buttonCount++; return 1; };
                EnumObjectsCallback povCallback = (objectInstance, context) => { povCount++; return 1; };

                EnumObjectsFn enumObjects = VTable<EnumObjectsFn>(device, 4);
                enumObjects(device, axisCallback, IntPtr.Zero, DidftAxis);
                enumObjects(device, buttonCallback, IntPtr.Zero, DidftButton);
                enumObjects(device, povCallback, IntPtr.Zero, DidftPov);
                GC.KeepAlive(axisCallback);
                GC.KeepAlive(buttonCallback);
                GC.KeepAlive(povCallback);

                buttons = buttonCount;
                povs = povCount;
                hidPath = ReadHidPath(device);
                return true;
            }
            catch (Exception ex)
            {
                warnings?.Add("Could not read the axes/buttons of device " + instanceGuid + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (device != IntPtr.Zero)
                    VTable<ReleaseFn>(device, 2)(device);
                if (directInput != IntPtr.Zero)
                    VTable<ReleaseFn>(directInput, 2)(directInput);
            }
        }

        // IDirectInputDevice8::GetProperty(DIPROP_GUIDANDPATH): DIPROPHEADER (16 bytes), GUID guidClass, WCHAR wszPath[260].
        private static string ReadHidPath(IntPtr device)
        {
            const int size = 16 + 16 + 260 * 2;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++)
                    Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, size);
                Marshal.WriteInt32(buffer, 4, 16);
                if (VTable<GetPropertyFn>(device, 5)(device, new IntPtr(DipropGuidAndPath), buffer) != 0)
                    return string.Empty;
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, 32)) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static T VTable<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(comObject);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
        }
    }
}
