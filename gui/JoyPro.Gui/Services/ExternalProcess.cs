using System;
using System.Diagnostics;

namespace JoyPro.Gui.Services;

// Every program the GUI opens outside itself (browser, Explorer, installers, the update script) starts here, so the
// tests can replace it: a test that runs every command must not open pages, folders or UAC prompts on the PC.
public static class ExternalProcess
{
    public static Func<ProcessStartInfo, Process?> Start { get; set; } = info => Process.Start(info);
}
