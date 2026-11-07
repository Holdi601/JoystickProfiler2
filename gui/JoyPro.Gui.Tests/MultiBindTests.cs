using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Experimental: several inputs per relation (docs/multi-bind.md, MainWindowViewModel.MultiBind.cs).
//   - off (the default): the engine gets no --multi-bind, no card shows another input, the More inputs panel is hidden,
//     also when the binds list has extra binds;
//   - on: --multi-bind on on every engine call (also the overlay map), the card's "+1", the relation's other inputs, the
//     panel's rows; record/pick, make main, remove and copy to another device reach the engine with the right arguments
//     (copy: a preview first, then one call with its history note);
//   - the setting survives a restart.
// Renders (opt-in): JOYPRO_MULTIBIND_RENDER_OUT=<folder> the Binds page with a two-input relation, dark and light.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class MultiBindTests
{
    internal const string Moza = "MOZA AB9 Base {00000000-0000-0000-0000-0000000000b9}";

    [Fact]
    public async Task Off_ByDefault_NothingShows_AndTheEngineGetsNoFlag()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);
        Assert.False(vm.MultiBindEnabled);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--multi-bind"));
        BindListItem release = vm.Binds.Single(b => b.RelationName == "Weapon Release");
        Assert.Single(release.ExtraBinds);
        Assert.False(release.HasExtraBinds);
        Assert.Equal(string.Empty, release.ExtraBindsBadge);
        vm.SelectedBind = release;
        Assert.False(vm.ShowMultiBindPanel);
        Assert.Empty(vm.SelectedExtraBinds);
        Assert.False(vm.Relations.Single(r => r.Name == "Weapon Release").HasMoreInputs);
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public async Task On_ShowsTheOtherInputs_AndEveryCommandReachesTheEngine()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);
        vm.MultiBindEnabled = true;
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        IReadOnlyList<string> list = fake.Calls.Last(c => AxisTuningTests.Seq(c, "binds", "list"));
        Assert.Equal("on", ValueAfter(list, "--multi-bind"));

        BindListItem release = vm.Binds.Single(b => b.RelationName == "Weapon Release");
        Assert.True(release.HasExtraBinds);
        Assert.Equal("+1", release.ExtraBindsBadge);
        Assert.Contains("MOZA AB9 Base", release.ExtraBindsTooltip);
        RelationListItem relation = vm.Relations.Single(r => r.Name == "Weapon Release");
        Assert.True(relation.HasMoreInputs);
        Assert.Equal("+1", relation.MoreInputsBadge);

        vm.SelectedBind = release;
        Assert.True(vm.ShowMultiBindPanel);
        ExtraBindRow row = Assert.Single(vm.SelectedExtraBinds);
        Assert.Equal(2, row.Number);
        Assert.Equal("Input 2", row.NumberText);
        Assert.Equal("JOY_BTN2", row.Input);
        Assert.Contains("Shift", row.Label);

        // Pick by hand -> another input.
        vm.BindDeviceInput = Moza;
        vm.BindInputValue = "JOY_BTN5";
        await vm.AddPickedExtraInputCommand.ExecuteAsync(null);
        IReadOnlyList<string> add = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "add"));
        Assert.Equal("Weapon Release", add[add.ToList().IndexOf("add") + 1]);
        Assert.Equal(Moza, ValueAfter(add, "--device"));
        Assert.Equal("JOY_BTN5", ValueAfter(add, "--input"));
        Assert.Equal("on", ValueAfter(add, "--multi-bind"));
        Assert.Equal("Added another input to 'Weapon Release'.", vm.StatusText);

        // Make main and remove: input 2 by its number.
        await vm.MakeExtraBindMainCommand.ExecuteAsync(row);
        IReadOnlyList<string> move = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "move"));
        Assert.Equal(["binds", "extra", "move", "Weapon Release", "2", "--to", "1"], move.SkipWhile(a => a != "binds").Take(7));
        await vm.RemoveExtraBindCommand.ExecuteAsync(row);
        IReadOnlyList<string> remove = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "remove"));
        Assert.Equal(["binds", "extra", "remove", "Weapon Release", "2"], remove.SkipWhile(a => a != "binds").Take(5));
        Assert.Equal("Removed input 2 of 'Weapon Release'.", vm.StatusText);

        // Copy to another device: a preview (dry run) first; Copy is one call with its history note.
        vm.RefreshCopyDevicesCommand.Execute(null);
        vm.CopyFromDevice = vm.CopyDeviceChoices.Single(c => c.Device == AxisTuningTests.Stick);
        vm.CopyToDevice = vm.CopyDeviceChoices.Single(c => c.Device == Moza);
        Assert.False(vm.CanApplyCopyDevice);
        await vm.PreviewCopyToDeviceCommand.ExecuteAsync(null);
        IReadOnlyList<string> preview = fake.Calls.Single(c => AxisTuningTests.Seq(c, "devices", "copy-to"));
        Assert.Contains("--dry-run", preview);
        Assert.True(vm.HasCopyDevicePreview);
        Assert.True(vm.CanApplyCopyDevice);
        await vm.ApplyCopyToDeviceCommand.ExecuteAsync(null);
        IReadOnlyList<string> copy = fake.Calls.Last(c => AxisTuningTests.Seq(c, "devices", "copy-to"));
        Assert.DoesNotContain("--dry-run", copy);
        Assert.Equal("Copy to another device", ValueAfter(copy, "--history-note"));
        Assert.StartsWith("Copied the binds of ", vm.StatusText);
        Assert.False(vm.HasCopyDevicePreview);

        // Off again: the panel and the badges go, the engine gets no flag.
        vm.MultiBindEnabled = false;
        Assert.False(vm.ShowMultiBindPanel);
        Assert.False(vm.Binds.Single(b => b.RelationName == "Weapon Release").HasExtraBinds);
        Assert.False(relation.HasMoreInputs);
        int before = fake.Calls.Count;
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls.Skip(before), c => c.Contains("--multi-bind"));
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public void ServedCommands_KnowTheOption()
    {
        Assert.Equal(("binds", "list", false), EngineServerCommands.Command(["--profile", "p.pr0file", "--multi-bind", "on", "binds", "list"]));
        Assert.True(EngineServerCommands.IsServed(["--profile", "p.pr0file", "--multi-bind", "on", "binds", "list"]));
    }

    [Fact]
    public void Setting_SurvivesARestart()
    {
        string settingsFile = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.Delete(settingsFile);
            MainWindowViewModel first = new(new MultiBindFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.False(first.MultiBindEnabled);
            first.MultiBindEnabled = true;
            MainWindowViewModel second = new(new MultiBindFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.True(second.MultiBindEnabled);
            second.MultiBindEnabled = false;
            MainWindowViewModel third = new(new MultiBindFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.False(third.MultiBindEnabled);
        }
        finally
        {
            if (File.Exists(settingsFile))
                File.Delete(settingsFile);
        }
    }

    [Fact]
    public void RenderBindsPageWithTwoInputs()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_MULTIBIND_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            MultiBindFakeEngine fake = new();
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(LoadAsync(fake));
            try
            {
                vm.MultiBindEnabled = true;
                AvaloniaRenderThread.Wait(vm.RefreshBindsCommand.ExecuteAsync(null));
                vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Weapon Release");
                vm.RefreshCopyDevicesCommand.Execute(null);
                vm.CopyFromDevice = vm.CopyDeviceChoices.Single(c => c.Device == AxisTuningTests.Stick);
                vm.CopyToDevice = vm.CopyDeviceChoices.Single(c => c.Device == Moza);
                AvaloniaRenderThread.Wait(vm.PreviewCopyToDeviceCommand.ExecuteAsync(null));
                vm.IsCopyDeviceOpen = true;
                Assert.True(vm.Binds.Count > 10);
                Assert.Single(vm.SelectedExtraBinds);
                vm.StatusText = "Ready.";
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        vm.CurrentStage = UiStage.Binds;
                        AvaloniaRenderThread.Pump();
                        // The first render of a page realizes its lists; the second shows them.
                        UiDesignRenderTests.RenderWindow(vm, width, 1900, Path.Combine(outDir, "warm-up.png"));
                        UiDesignRenderTests.RenderWindow(vm, width, 1900, Path.Combine(outDir, "binds-two-inputs-" + width + "-" + theme + ".png"));
                        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Weapon Release");
                        vm.CurrentStage = UiStage.Relations;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 1100, Path.Combine(outDir, "relations-two-inputs-" + width + "-" + theme + ".png"));
                        vm.CurrentStage = UiStage.Settings;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 900, Path.Combine(outDir, "settings-" + width + "-" + theme + ".png"));
                    }
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                File.Delete(Path.Combine(outDir, "warm-up.png"));
            }
        });
    }

    private static async Task<MainWindowViewModel> LoadAsync(MultiBindFakeEngine fake)
    {
        return await UiDesignRenderTests.CreateLoadedVmAsync(fake.Design, fake);
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        int at = args.ToList().IndexOf(option);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : string.Empty;
    }

    // UiDesignRenderTests' profile, with Weapon Release also on the MOZA base (BTN 2 with Shift), and answers to the
    // multi-bind commands.
    internal sealed class MultiBindFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public UiDesignRenderTests.DesignFakeEngine Design { get; } = new();

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            if (AxisTuningTests.Seq(arguments, "binds", "extra") || AxisTuningTests.Seq(arguments, "devices", "copy-to"))
            {
                string text = AxisTuningTests.Seq(arguments, "devices", "copy-to")
                    ? "Copy to another device (preview)\nMode: same (the same product: every input keeps its id)\n  JOY_BTN1 -> JOY_BTN1 | same input | 1 relation(s)\n  JOY_BTN2 -> JOY_BTN2 | same input | 1 relation(s)\nWould copy 2 input(s)."
                    : "Relation: Weapon Release (button)";
                return new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = text, StandardError = string.Empty, Arguments = arguments.ToArray() };
            }
            EngineCommandResult result = await Design.RunAsync(engineExePath, arguments, cancellationToken);
            if (AxisTuningTests.Seq(arguments, "binds", "list") && result.StandardOutput.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                JsonNode? doc = JsonNode.Parse(result.StandardOutput);
                foreach (JsonNode? bind in doc?["Binds"]?.AsArray() ?? [])
                {
                    if ((string?)bind?["RelationName"] == "Weapon Release")
                        bind!["ExtraBinds"] = JsonNode.Parse("[{\"RelationName\":\"Weapon Release\",\"IsAxis\":false,\"Joystick\":\"" + Moza + "\",\"JAxis\":\"\",\"JButton\":\"JOY_BTN2\"," +
                                                             "\"AllReformers\":[\"Shift\\u00A7" + Moza + "\\u00A7JOY_BTN4\\u00A7False\"]}]");
                }
                return new EngineCommandResult { ExitCode = result.ExitCode, Duration = result.Duration, StandardOutput = doc!.ToJsonString(), StandardError = result.StandardError, Arguments = result.Arguments };
            }
            return result;
        }
    }
}
