using System;
using System.Runtime.InteropServices;

namespace JoyPro.Gui.Services;

// Flashes a window's taskbar button until the window comes to the front (FlashWindowEx, FLASHW_TRAY |
// FLASHW_TIMERNOFG): how Windows lets a background program ask for attention without taking the focus.
public static class TaskbarFlash
{
    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    public static void Flash(IntPtr window)
    {
        if (window == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;
        FlashInfo info = new() { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashTray | FlashUntilForeground };
        FlashWindowEx(ref info);
    }
}
