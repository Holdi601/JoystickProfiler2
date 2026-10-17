using System.Runtime.InteropServices;

namespace JoyPro.VJoy;

/// <summary>An invisible top-level window on its own message thread: DirectInput's SetCooperativeLevel needs one, and
/// it receives WM_DEVICECHANGE, so a device that comes back is opened again at once (no polling for missing devices).</summary>
/// <remarks>
/// A window whose thread does not pump messages would stall programs that broadcast messages (SendMessage to all
/// top-level windows), so the window gets a thread that waits in GetMessage: no CPU while nothing arrives.
/// </remarks>
internal sealed class HiddenWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WmQuit = 0x0012;
    private const uint WmDeviceChange = 0x0219;
    private const string ClassName = "JoyProVJoyWindow";

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Action? _devicesChanged;
    private readonly WndProc _wndProc;
    private uint _threadId;

    private HiddenWindow(Action? devicesChanged)
    {
        _devicesChanged = devicesChanged;
        _wndProc = OnMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "JoyPro vJoy window" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public IntPtr Handle { get; private set; }

    public static HiddenWindow Create(Action? devicesChanged = null)
    {
        var window = new HiddenWindow(devicesChanged);
        window._thread.Start();
        window._ready.Wait(TimeSpan.FromSeconds(5));
        if (window.Handle == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the window DirectInput needs.");
        return window;
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDeviceChange)
            _devicesChanged?.Invoke();
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var instance = Native.GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = ClassName
        };
        RegisterClassExW(ref windowClass); // fails harmlessly when the class exists already (a second forwarder)
        Handle = CreateWindowExW(0, ClassName, "JoyPro virtual joystick", WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _ready.Set();
        if (Handle == IntPtr.Zero)
            return;
        var message = Marshal.AllocHGlobal(64);
        try
        {
            while (GetMessageW(message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(message);
            DestroyWindow(Handle);
            GC.KeepAlive(_wndProc);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(IntPtr message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(IntPtr message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(IntPtr message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
