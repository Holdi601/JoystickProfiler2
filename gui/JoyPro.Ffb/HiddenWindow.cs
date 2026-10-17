using System.Runtime.InteropServices;

namespace JoyPro.Ffb;

/// <summary>An invisible top-level window on its own message thread, for DirectInput's SetCooperativeLevel.</summary>
/// <remarks>
/// DirectInput wants a top-level window of the process even in background mode. A window whose thread does not pump
/// messages would stall programs that broadcast messages (SendMessage to all top-level windows), so the window gets a
/// thread that waits in GetMessage: no CPU while nothing arrives.
/// </remarks>
internal sealed class HiddenWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WmQuit = 0x0012;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    private HiddenWindow()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "JoyPro FFB window" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public IntPtr Handle { get; private set; }

    public static HiddenWindow Create()
    {
        var window = new HiddenWindow();
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

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        Handle = CreateWindowExW(0, "STATIC", "JoyPro force feedback", WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, DirectInputFfb.GetModuleHandle(null), IntPtr.Zero);
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
        }
    }

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
