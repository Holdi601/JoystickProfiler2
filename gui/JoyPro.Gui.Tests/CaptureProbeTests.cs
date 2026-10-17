using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// JOYPRO_CAPTURE_PROBE=<file> writes, for every connected device, the buttons pressed when it is opened and a moment
// later (real devices; reads only).
[SupportedOSPlatform("windows")]
public sealed class CaptureProbeTests
{
    [Fact]
    public void Probe_PressedButtonsOfConnectedDevices()
    {
        string? output = Environment.GetEnvironmentVariable("JOYPRO_CAPTURE_PROBE");
        if (string.IsNullOrWhiteSpace(output))
            return;
        var lines = JoystickCaptureService.ProbePressedButtons(3000)
            .Select(d => d.Device + " | open: " + string.Join(",", d.AtOpen) + " | 3 s: " + string.Join(",", d.Later) + " | first change after " + d.FirstChangeMs + " ms");
        File.WriteAllLines(output, lines);
    }
}
