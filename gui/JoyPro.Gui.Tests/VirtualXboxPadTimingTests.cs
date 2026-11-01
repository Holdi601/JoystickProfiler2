using System;
using System.IO;
using System.Runtime.Versioning;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Virtual Joystick page tells when to start the virtual Xbox pad of Hell Let Loose: Vietnam and ACE COMBAT 8 (after
// their title screen shows, docs/virtual-joystick.md "When to start the pad"): under step 3 of the Overview and in the
// tooltips of Start and of the auto start. Named for the game set to "Xbox pad", a general note naming both while the
// games are not loaded, nothing for other games. Every language has the texts.
[SupportedOSPlatform("windows")]
public sealed class VirtualXboxPadTimingTests
{
    private const string Both = "Exception, the virtual Xbox pad of Hell Let Loose: Vietnam and ACE COMBAT 8: these games only see a pad created after their title screen shows. Press Start there, or press Stop and Start once the title screen shows.";
    private const string One = "Exception, the virtual Xbox pad of {0}: the game only sees a pad created after its title screen shows. Press Start there, or press Stop and Start once the title screen shows.";

    private static VirtualJoystickGameItem Game(string id, string name, string mode) =>
        new(id, name, 3, mode, ["game.exe"], null);

    [Fact]
    public void Note_NamesTheXboxGamesOnlyWhenTheyAreSetToXboxPad()
    {
        MainWindowViewModel vm = new(new VfFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);

        // Games not loaded yet: the general note naming both, also in both tooltips.
        Assert.Equal(Both, vm.VirtualXboxPadTimingNote);
        Assert.True(vm.HasVirtualXboxPadTimingNote);
        Assert.EndsWith(" " + Both, vm.VirtualJoystickStartTip);
        Assert.StartsWith("Hides your physical devices", vm.VirtualJoystickStartTip);
        Assert.EndsWith(" " + Both, vm.VirtualJoystickAutoStartTip);

        // Other games only (DCS virtual, Hell Let Loose: Vietnam native): nothing.
        vm.VirtualJoystickGames.Add(Game("DCS", "DCS World", VirtualJoystickGameItem.ModeVJoy));
        vm.VirtualJoystickGames.Add(Game("HLLVietnam", "Hell Let Loose: Vietnam", VirtualJoystickGameItem.ModeNative));
        Assert.Equal(string.Empty, vm.VirtualXboxPadTimingNote);
        Assert.False(vm.HasVirtualXboxPadTimingNote);
        Assert.DoesNotContain("title screen", vm.VirtualJoystickStartTip);
        Assert.DoesNotContain("title screen", vm.VirtualJoystickAutoStartTip);

        // ACE COMBAT 8 on the Xbox pad: named.
        vm.VirtualJoystickGames.Add(Game("AceCombat8", "ACE COMBAT 8: Wings of Theve", VirtualJoystickGameItem.ModeXbox));
        string one = string.Format(One, "ACE COMBAT 8: Wings of Theve");
        Assert.Equal(one, vm.VirtualXboxPadTimingNote);
        Assert.EndsWith(" " + one, vm.VirtualJoystickStartTip);
        Assert.EndsWith(" " + one, vm.VirtualJoystickAutoStartTip);

        // Both on the Xbox pad: the note naming both.
        vm.VirtualJoystickGames.Clear();
        vm.VirtualJoystickGames.Add(Game("HLLVietnam", "Hell Let Loose: Vietnam", VirtualJoystickGameItem.ModeXbox));
        vm.VirtualJoystickGames.Add(Game("AceCombat8", "ACE COMBAT 8: Wings of Theve", VirtualJoystickGameItem.ModeXbox));
        Assert.Equal(Both, vm.VirtualXboxPadTimingNote);
    }

    [Fact]
    public void Page_ShowsTheNoteUnderStep3AndInBothTooltips()
    {
        string axaml = File.ReadAllText(Path.Combine(RepoRoot(), "gui", "JoyPro.Gui", "Views", "VirtualJoystickView.axaml"));
        int step3 = axaml.IndexOf("3. Press Start before you start the game.", StringComparison.Ordinal);
        int step4 = axaml.IndexOf("4. Press Stop when you are done.", StringComparison.Ordinal);
        int note = axaml.IndexOf("Text=\"{Binding VirtualXboxPadTimingNote}\"", StringComparison.Ordinal);
        Assert.True(step3 > 0 && note > step3 && note < step4, "the note belongs under step 3 of the Overview");
        Assert.Contains("IsVisible=\"{Binding HasVirtualXboxPadTimingNote}\"", axaml);
        Assert.Contains("ToolTip.Tip=\"{Binding VirtualJoystickStartTip}\"", axaml);
        Assert.Contains("ToolTip.Tip=\"{Binding VirtualJoystickAutoStartTip}\"", axaml);
    }

    [Fact]
    public void Note_IsTranslatedInEveryLanguage()
    {
        try
        {
            foreach (LanguageInfo language in Languages.All)
            {
                if (language.Code == "en")
                    continue;
                Loc.SetLanguage(language.Code);
                Assert.NotEqual(Both, Loc.T(Both));
                string one = Loc.T(One, "ACE COMBAT 8");
                Assert.NotEqual(string.Format(One, "ACE COMBAT 8"), one);
                Assert.Contains("ACE COMBAT 8", one);
            }
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "gui", "JoyPro.Gui", "Views", "VirtualJoystickView.axaml")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
