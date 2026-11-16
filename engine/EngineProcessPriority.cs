using System;
using System.Runtime.InteropServices;

namespace JoyPro.Engine
{
    // JOYPRO_LOW_PRIORITY=1 (the app's background looks, e.g. the Start page's "setup games"): the run goes into Windows'
    // background mode, low CPU, disk and memory priority, so it never competes with a game or the app's own calls.
    internal static class EngineProcessPriority
    {
        public const string Variable = "JOYPRO_LOW_PRIORITY";

        private const int ProcessModeBackgroundBegin = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPriorityClass(IntPtr process, int priorityClass);

        public static void LowerIfAsked()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal))
                return;
            try
            {
                if (!SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin))
                    JoyPro.Logging.JoyProLog.Warn("Engine", "Background mode refused (error " + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                JoyPro.Logging.JoyProLog.Warn("Engine", "Background mode not available", ex);
            }
        }
    }
}
