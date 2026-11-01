using System;
using System.IO;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// A tester's PC (2.5.0) had Saved Games\DCS (no binds) and Saved Games\DCS.openbeta (the binds of his install
// "DCS World Openbeta"). JoyPro kept Saved Games\DCS for the DCS folder because it exists: the imports found no binds
// until he set the import folder by hand, and the export folder stayed Saved Games\DCS, where his DCS does not read.
public sealed class DcsSavedGamesAdoptionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dcs-saved-games-" + Guid.NewGuid().ToString("N"));

    public DcsSavedGamesAdoptionTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name, bool binds)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "Config", "Input"));
        if (binds)
        {
            string joystick = Path.Combine(folder, "Config", "Input", "F-4E-45MC", "joystick");
            Directory.CreateDirectory(joystick);
            File.WriteAllText(Path.Combine(joystick, "WINCTRL F18 COMBAT READY PANEL {071E92B0-0D8D-11f1-8003-444553540000}.diff.lua"), "local diff = {} return diff");
        }
        return folder;
    }

    private string Install(string name, string? variant)
    {
        string install = Path.Combine(root, "Eagle Dynamics", name);
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        if (variant != null)
            File.WriteAllText(Path.Combine(install, "dcs_variant.txt"), variant);
        return install;
    }

    [Fact]
    public void OpenBetaInstall_SavedGamesDcsWithoutBinds_TakesDcsOpenbeta()
    {
        string stable = Folder("DCS", binds: false);
        string openBeta = Folder("DCS.openbeta", binds: true);
        string install = Install("DCS World Openbeta", "openbeta");

        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, currentIsHandPath: false, configOverridden: false, lastAdopted: string.Empty, install));
        // Also by the install folder's name when it has no dcs_variant.txt.
        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, false, false, string.Empty, Install("DCS World OpenBeta 2", null)));
    }

    [Fact]
    public void FolderSetByHand_OrWithBinds_OrOfAnotherInstall_Stays()
    {
        string stable = Folder("DCS", binds: false);
        string openBeta = Folder("DCS.openbeta", binds: true);
        string openBetaInstall = Install("DCS World Openbeta", "openbeta");

        // The user's own folder stays while it exists.
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, currentIsHandPath: true, configOverridden: false, lastAdopted: string.Empty, openBetaInstall));
        // A stable install reads Saved Games\DCS: an old DCS.openbeta with binds does not take its place.
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, false, false, string.Empty, Install("DCS World", null)));
        // Without a known install nothing is guessed.
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, false, false, string.Empty, string.Empty));
        // Binds in both: the folder stays.
        string stableWithBinds = Folder("DCS2", binds: true);
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(openBeta, stableWithBinds, false, false, string.Empty, openBetaInstall));
    }

    [Fact]
    public void EarlierRules_Unchanged()
    {
        string openBeta = Folder("DCS.openbeta", binds: true);
        string stable = Folder("DCS", binds: true);
        // The GUI's folder does not exist.
        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, Path.Combine(root, "missing"), false, false, string.Empty));
        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, Path.Combine(root, "missing"), true, false, string.Empty));
        // The user linked a config location (Settings > Games).
        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, false, true, string.Empty));
        // Taken from the engine before.
        Assert.True(MainWindowViewModel.AdoptsDcsConfig(openBeta, stable, false, false, stable));
        // The same folder, or none from the engine.
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(openBeta, openBeta, false, false, string.Empty));
        Assert.False(MainWindowViewModel.AdoptsDcsConfig(string.Empty, stable, false, false, string.Empty));
    }
}
