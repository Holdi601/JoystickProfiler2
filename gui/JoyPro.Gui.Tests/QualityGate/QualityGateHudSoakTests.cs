using System;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I12 of the quality gate, opt-in on top of the gate (it takes minutes): the HUD and the stream page stay light while a
// fake device stream runs (HudSoak: CPU of one core, allocations, heap and working set growth, updates and page events
// per second, one software frame). Runs only with JOYPRO_QUALITY_GATE_HUD_SOAK=<minutes> (tools\quality_gate.ps1
// -HudSoakMinutes <n>); otherwise a note, so the normal gate does not get longer. Report: <Out>\hud-soak.md.
[SupportedOSPlatform("windows")]
public sealed class QualityGateHudSoakTests
{
    [Fact]
    public void I12_HudAndStreamPageStayLight()
    {
        if (!Gate.Enabled)
            return;
        if (!double.TryParse(Environment.GetEnvironmentVariable("JOYPRO_QUALITY_GATE_HUD_SOAK"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
        {
            Gate.Note("I12: HUD soak skipped (opt-in: JOYPRO_QUALITY_GATE_HUD_SOAK=<minutes>, or -HudSoakMinutes)");
            Gate.Finish("I12");
            return;
        }
        HudSoak.Result result = HudSoak.Run(TimeSpan.FromMinutes(minutes));
        File.WriteAllText(Path.Combine(Gate.OutDir, "hud-soak.md"), result.Report());
        foreach (string failure in result.Failures)
            Gate.Fail("I12", "HUD background cost over budget", string.Empty, "HUD soak", failure);
        Gate.Note("I12: HUD soak " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " min, " + (result.Failures.Count == 0 ? "within budgets" : result.Failures.Count + " over budget") + " (hud-soak.md)");
        Gate.Finish("I12");
    }
}
