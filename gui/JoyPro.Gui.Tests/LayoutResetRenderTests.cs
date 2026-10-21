using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The looks of "Reset to default" (the Layout page's Edit panel "Picture and markers", with Undo after a reset) and of
// Settings > Drawings ("Reset all my layout changes"). Only with JOYPRO_UI_DESIGN_OUT set: PNGs layout-reset-*.png.
[SupportedOSPlatform("windows")]
public sealed class LayoutResetRenderTests
{
    [Fact]
    public async Task ResetToDefault_EditPanelAndSettings_Render()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "layout-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(Path.Combine(work, "no-picture.png"), work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.SuggestLayoutsCommand.ExecuteAsync(null);
        vm.SelectedLayoutSuggestion = vm.LayoutSuggestions.Single(s => s.Device == AxisTuningTests.Stick);
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.HasReferenceModel);
        vm.CurrentStage = UiStage.Layout;
        vm.LayoutShowsLayoutFile = false;
        vm.SetLayoutCanvasModeCommand.Execute("edit");
        // The Edit panel "Picture and markers" (shown while the sheet's tables are off).
        vm.LayoutCalloutsEnabled = false;
        await vm.ResetDeviceLayoutCommand.ExecuteAsync(vm.SelectedLayoutSuggestion);
        string data = Path.Combine(JoyPro.Gui.Services.DeviceReferenceService.DataFolder(), "Legend");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, JoyPro.DeviceReference.DeviceVocabulary.Slug(vm.DeviceReference.Model!.Id) + ".json"), "{}");
        await vm.ResetDeviceLayoutCommand.ExecuteAsync(vm.SelectedLayoutSuggestion);
        AvaloniaRenderThread.Run(() =>
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "layout-reset-edit-" + w + ".png"));
            vm.CollapseAllSettingsCommand.Execute(null);
            vm.OpenSettingsSectionCommand.Execute("drawings");
            vm.SettingsSearchText = "reset layout";
            AvaloniaRenderThread.Pump();
            foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                UiDesignRenderTests.RenderWindow(vm, w, h, Path.Combine(outDir, "layout-reset-settings-" + w + ".png"));
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        });
        await vm.UndoLayoutResetCommand.ExecuteAsync(null);
    }
}
