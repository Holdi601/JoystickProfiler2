using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The import options survive a restart (MainWindowViewModel.ImportSettings.cs, export-settings.json "import"): every
// option of the Start page import and the expert import is set, the view model is made again, and all come back. Uses
// the test run's own settings folder (JOYPRO_DATA_DIR\settings) in the "TrayMode" collection that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class ImportSettingsPersistTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
    private readonly string exportSettings = Path.Combine(AppInfo.SettingsFolder, "export-settings.json");

    public ImportSettingsPersistTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
        File.Delete(exportSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
        File.Delete(exportSettings);
    }

    private static MainWindowViewModel Vm(bool persist) =>
        new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void EveryImportOption_ComesBackAfterARestart_IncludeDefaultsStartsOff()
    {
        MainWindowViewModel first = Vm(persist: true);
        Assert.False(first.ImportIncludeDefaults);
        Assert.True(first.ImportKeyboardCommands);
        Assert.True(first.ImportWizardAddToProfile);

        first.ImportWizardAddToProfile = false;
        first.ImportMerge = true;
        first.ImportIncludeDefaults = true;
        first.ImportStarCitizenIncludeKeyboard = true;
        first.ImportIgnoreEasyPlanes = true;
        first.ImportInverted = true;
        first.ImportSlider = true;
        first.ImportCurvature = true;
        first.ImportDeadzone = true;
        first.ImportSaturationX = true;
        first.ImportSaturationY = true;
        first.ImportAllDcsPlanes = false;
        first.ImportUseSelectedDevices = true;
        first.ImportPlanesCsv = "A-10C_2";
        first.ImportDevicesCsv = "Smoke Stick";
        first.ImportIgnoreDevicesCsv = "Smoke Pedals";
        first.ImportDcsOnlyConnected = true;
        first.ImportDcsSameNameAsOne = false;
        first.ImportIncludeDisconnected = true;
        first.ImportIncludeSimDefaults = true;

        // The Start page's keyboard box: per game, right away (also without running the import).
        GameCardItem dcs = new("DCS", "DCS World", "dcs", "dcs", true, true);
        GameCardItem warThunder = new("WarThunder", "War Thunder", "registry", "registry", true, false);
        first.StartImportWizardCommand.Execute(dcs);
        Assert.True(first.ImportWizardKeyboard);
        first.ImportWizardKeyboard = false;
        first.ImportWizardBackCommand.Execute(null);
        first.StartImportWizardCommand.Execute(warThunder);
        Assert.False(first.ImportWizardKeyboard);
        first.ImportWizardKeyboard = true;
        first.RestartImportWizardCommand.Execute(null);

        // A ticked aircraft, device and ignored device of the lists.
        SelectableTagItem plane = new("F-16C_50");
        first.ImportPlaneOptions.Add(plane);
        plane.IsSelected = true;
        SelectableTagItem device = new("Smoke Throttle");
        first.ImportDeviceOptions.Add(device);
        device.IsSelected = true;
        SelectableTagItem ignored = new("Smoke Wheel");
        first.ImportIgnoreDeviceOptions.Add(ignored);
        ignored.IsSelected = true;

        MainWindowViewModel second = Vm(persist: true);

        Assert.False(second.ImportWizardAddToProfile);
        Assert.True(second.ImportMerge);
        Assert.True(second.ImportIncludeDefaults);
        Assert.False(second.ImportKeyboardCommands);
        Assert.True(second.ImportStarCitizenIncludeKeyboard);
        Assert.True(second.ImportIgnoreEasyPlanes);
        Assert.True(second.ImportInverted);
        Assert.True(second.ImportSlider);
        Assert.True(second.ImportCurvature);
        Assert.True(second.ImportDeadzone);
        Assert.True(second.ImportSaturationX);
        Assert.True(second.ImportSaturationY);
        Assert.False(second.ImportAllDcsPlanes);
        Assert.True(second.ImportUseSelectedDevices);
        Assert.Equal("A-10C_2", second.ImportPlanesCsv);
        Assert.Equal("Smoke Stick", second.ImportDevicesCsv);
        Assert.Equal("Smoke Pedals", second.ImportIgnoreDevicesCsv);
        Assert.True(second.ImportDcsOnlyConnected);
        Assert.False(second.ImportDcsSameNameAsOne);
        Assert.True(second.ImportIncludeDisconnected);
        Assert.True(second.ImportIncludeSimDefaults);

        second.StartImportWizardCommand.Execute(dcs);
        Assert.False(second.ImportWizardKeyboard);
        second.ImportWizardBackCommand.Execute(null);
        second.StartImportWizardCommand.Execute(warThunder);
        Assert.True(second.ImportWizardKeyboard);
        second.ImportWizardBackCommand.Execute(null);
        second.StartImportWizardCommand.Execute(new GameCardItem("X4", "X4", "registry", "registry", true, false));
        Assert.False(second.ImportWizardKeyboard);
        second.RestartImportWizardCommand.Execute(null);

        // The lists' ticks come back when the entries are listed again.
        SelectableTagItem planeAgain = new("F-16C_50");
        second.ImportPlaneOptions.Add(planeAgain);
        Assert.True(planeAgain.IsSelected);
        SelectableTagItem deviceAgain = new("Smoke Throttle");
        second.ImportDeviceOptions.Add(deviceAgain);
        Assert.True(deviceAgain.IsSelected);
        SelectableTagItem ignoredAgain = new("Smoke Wheel");
        second.ImportIgnoreDeviceOptions.Add(ignoredAgain);
        Assert.True(ignoredAgain.IsSelected);
        SelectableTagItem other = new("Smoke Other");
        second.ImportPlaneOptions.Add(other);
        Assert.False(other.IsSelected);

        // Unticked again: forgotten after the next restart.
        planeAgain.IsSelected = false;
        second.ImportIncludeDefaults = false;
        MainWindowViewModel third = Vm(persist: true);
        Assert.False(third.ImportIncludeDefaults);
        SelectableTagItem planeThird = new("F-16C_50");
        third.ImportPlaneOptions.Add(planeThird);
        Assert.False(planeThird.IsSelected);
    }

    [Fact]
    public void WithoutPersistedSettings_NothingIsWrittenOrRestored()
    {
        MainWindowViewModel vm = Vm(persist: false);
        vm.ImportIncludeDefaults = true;
        vm.ImportInverted = true;
        Assert.False(File.Exists(exportSettings));
        Assert.False(Vm(persist: false).ImportIncludeDefaults);
    }
}
