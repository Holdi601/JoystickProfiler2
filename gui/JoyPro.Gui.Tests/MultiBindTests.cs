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

// Several inputs per relation (docs/multi-bind.md, MainWindowViewModel.MultiBind.cs): a normal feature since 2.6.6.
//   - no setting any more: the engine gets no --multi-bind (on by default there), the cards' "+1", the relation's other
//     inputs and the More inputs panel show for every profile with extra inputs; Settings has no Experimental section;
//   - record/pick, make main, remove and copy to another device reach the engine with the right arguments;
//   - the pages find a relation by any of its inputs: follow input, the Layout page's pictures and tables, the device
//     filters of the Relations and Binds pages, the device lists; the axis-response section tunes the input picked.
// Renders (opt-in): JOYPRO_MULTIBIND_RENDER_OUT=<folder> the Binds page, the Relations page, the Layout page's tables
// and the picture with extra inputs, dark and light, at 1280 and 2267 wide.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class MultiBindTests
{
    internal const string Moza = "MOZA AB9 Base {00000000-0000-0000-0000-0000000000b9}";

    [Fact]
    public async Task ExtraInputs_ShowWithoutASetting_AndTheEngineGetsNoFlag()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--multi-bind"));
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
        // A relation without other inputs shows no badge.
        Assert.False(vm.Binds.Single(b => b.RelationName == "Gun Trigger").HasExtraBinds);
        // Settings: the Experimental section with its switch is gone.
        Assert.DoesNotContain(vm.SettingsSections, s => s.Id == "experimental");
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public async Task EveryCommandReachesTheEngine()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Weapon Release");
        ExtraBindRow row = Assert.Single(vm.SelectedExtraBinds);

        // Pick by hand -> another input.
        vm.BindDeviceInput = Moza;
        vm.BindInputValue = "JOY_BTN5";
        await vm.AddPickedExtraInputCommand.ExecuteAsync(null);
        IReadOnlyList<string> add = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "add"));
        Assert.Equal("Weapon Release", add[add.ToList().IndexOf("add") + 1]);
        Assert.Equal(Moza, ValueAfter(add, "--device"));
        Assert.Equal("JOY_BTN5", ValueAfter(add, "--input"));
        Assert.DoesNotContain("--multi-bind", add);
        Assert.Equal("Added another input to 'Weapon Release'.", vm.StatusText);

        // Make main and remove: input 2 by its number.
        await vm.MakeExtraBindMainCommand.ExecuteAsync(row);
        IReadOnlyList<string> move = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "move"));
        Assert.Equal(["binds", "extra", "move", "Weapon Release", "2", "--to", "1"], move.SkipWhile(a => a != "binds").Take(7));
        await vm.RemoveExtraBindCommand.ExecuteAsync(row);
        IReadOnlyList<string> remove = fake.Calls.Single(c => AxisTuningTests.Seq(c, "binds", "extra", "remove"));
        Assert.Equal(["binds", "extra", "remove", "Weapon Release", "2"], remove.SkipWhile(a => a != "binds").Take(5));
        Assert.Equal("Removed input 2 of 'Weapon Release'.", vm.StatusText);

        // Copy to another device: opening it fills the device lists; a preview (dry run) first; Copy is one call.
        vm.IsCopyDeviceOpen = true;
        Assert.Contains(vm.CopyDeviceChoices, c => c.Device == Moza);
        vm.CopyFromDevice = vm.CopyDeviceChoices.Single(c => c.Device == AxisTuningTests.Stick);
        vm.CopyToDevice = vm.CopyDeviceChoices.Single(c => c.Device == Moza);
        Assert.False(vm.CanApplyCopyDevice);
        await vm.PreviewCopyToDeviceCommand.ExecuteAsync(null);
        Assert.Contains("--dry-run", fake.Calls.Single(c => AxisTuningTests.Seq(c, "devices", "copy-to")));
        Assert.True(vm.CanApplyCopyDevice);
        await vm.ApplyCopyToDeviceCommand.ExecuteAsync(null);
        IReadOnlyList<string> copy = fake.Calls.Last(c => AxisTuningTests.Seq(c, "devices", "copy-to"));
        Assert.DoesNotContain("--dry-run", copy);
        Assert.Equal("Copy to another device", ValueAfter(copy, "--history-note"));
        Assert.StartsWith("Copied the binds of ", vm.StatusText);
        vm.StopGameConfigWatcher();
    }

    // 2.6.5 notes: "some pages still only show the main input". Each lookup below found only the main bind before.
    [Fact]
    public async Task ThePagesFindARelationByItsOtherInputs()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);

        // Follow input: a press on the MOZA's BTN 2 is Weapon Release (its input 2).
        Assert.Contains(vm.RelationsOnInput(Moza, "JOY_BTN2"), r => r.Relation == "Weapon Release");
        Assert.Contains(vm.RelationsOnInput(Moza, "JOY_Y"), r => r.Relation == "Pitch");

        // The Layout page's picture and tables: what each input of the MOZA does.
        Dictionary<string, string> moza = vm.DeviceReferenceBindings(Moza);
        Assert.Contains("Weapon Release", moza["JOY_BTN2"]);
        Assert.Contains("Pitch", moza["JOY_Y"]);
        // The main device still shows the main input.
        Assert.Contains("Weapon Release", vm.DeviceReferenceBindings(AxisTuningTests.Stick)["JOY_BTN2"]);

        // The device filters: the MOZA is a device of the profile; ticked, it shows the relations with an input on it.
        Assert.Contains(vm.BindFilter.Devices, d => d.Key == Moza);
        vm.BindFilter.SelectDevices(Moza);
        Assert.Equal(["Pitch", "Weapon Release"], vm.Binds.Select(b => b.RelationName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(vm.RelationFilter.Devices, d => d.Key == Moza);
        vm.RelationFilter.SelectDevices(Moza);
        Assert.Equal(["Pitch", "Weapon Release"], vm.Relations.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
        vm.StopGameConfigWatcher();
    }

    // The axis-response section edited the main bind only; now it picks the input (engine binds tuning --bind n).
    [Fact]
    public async Task AxisResponse_TunesTheInputPicked()
    {
        MultiBindFakeEngine fake = new();
        MainWindowViewModel vm = await LoadAsync(fake);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Roll");
        Assert.False(vm.HasAxisTuningInputChoices);
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        Assert.True(vm.HasAxisTuningInputChoices);
        Assert.Equal([1, 2], vm.AxisTuningInputChoices.Select(c => c.Number));
        Assert.StartsWith("Input 1 (main)", vm.AxisTuningInputChoices[0].Label);
        Assert.Contains("MOZA AB9 Base", vm.AxisTuningInputChoices[1].Label);
        Assert.Equal(1, vm.SelectedAxisTuningInput?.Number);

        int before = fake.Calls.Count;
        vm.SelectedAxisTuningInput = vm.AxisTuningInputChoices[1];
        for (int i = 0; i < 200 && !fake.Calls.Skip(before).Any(c => AxisTuningTests.Seq(c, "binds", "tuning", "show")); i++)
            await Task.Delay(10);
        IReadOnlyList<string> show = fake.Calls.Skip(before).First(c => AxisTuningTests.Seq(c, "binds", "tuning", "show"));
        Assert.Equal("2", ValueAfter(show, "--bind"));
        // Input 1 again: the commands as before (no --bind).
        before = fake.Calls.Count;
        vm.SelectedAxisTuningInput = vm.AxisTuningInputChoices[0];
        for (int i = 0; i < 200 && !fake.Calls.Skip(before).Any(c => AxisTuningTests.Seq(c, "binds", "tuning", "show")); i++)
            await Task.Delay(10);
        Assert.DoesNotContain(fake.Calls.Skip(before).Where(c => AxisTuningTests.Seq(c, "binds", "tuning")), c => c.Contains("--bind"));
        vm.StopGameConfigWatcher();
    }

    [Fact]
    public void ServedCommands_KnowTheOption()
    {
        Assert.Equal(("binds", "list", false), EngineServerCommands.Command(["--profile", "p.pr0file", "--multi-bind", "on", "binds", "list"]));
        Assert.True(EngineServerCommands.IsServed(["--profile", "p.pr0file", "--multi-bind", "on", "binds", "list"]));
    }

    [Fact]
    public void RenderPagesWithExtraInputs()
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
                vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
                vm.IsAxisTuningExpanded = true;
                vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Weapon Release");
                vm.IsCopyDeviceOpen = true;
                vm.CopyFromDevice = vm.CopyDeviceChoices.Single(c => c.Device == AxisTuningTests.Stick);
                vm.CopyToDevice = vm.CopyDeviceChoices.Single(c => c.Device == Moza);
                AvaloniaRenderThread.Wait(vm.PreviewCopyToDeviceCommand.ExecuteAsync(null));
                Assert.Single(vm.SelectedExtraBinds);
                vm.StatusText = "Ready.";
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Weapon Release");
                        vm.CurrentStage = UiStage.Binds;
                        AvaloniaRenderThread.Pump();
                        // The first render of a page realizes its lists; the second shows them.
                        UiDesignRenderTests.RenderWindow(vm, width, 1900, Path.Combine(outDir, "warm-up.png"));
                        UiDesignRenderTests.RenderWindow(vm, width, 1900, Path.Combine(outDir, "binds-two-inputs-" + width + "-" + theme + ".png"));
                        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 2400, Path.Combine(outDir, "warm-up.png"));
                        UiDesignRenderTests.RenderWindow(vm, width, 2400, Path.Combine(outDir, "binds-axis-two-inputs-" + width + "-" + theme + ".png"));
                        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Weapon Release");
                        vm.CurrentStage = UiStage.Relations;
                        AvaloniaRenderThread.Pump();
                        UiDesignRenderTests.RenderWindow(vm, width, 1100, Path.Combine(outDir, "relations-two-inputs-" + width + "-" + theme + ".png"));
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

    // The Layout page (picture, tables, bind on the picture) with extra inputs on the stick: the VKB layout of
    // LayoutRedesignRenderTests, Weapon Release also on BTN 6 and Gun Trigger also on BTN 8 with Shift.
    [Fact]
    public void RenderLayoutPagesWithExtraInputs()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_MULTIBIND_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ui-layout-multibind");
        Directory.CreateDirectory(work);
        AvaloniaRenderThread.Run(() =>
        {
            string picture = LayoutRedesignRenderTests.ResolvePicture(work);
            ExtraInputsEngine fake = new(new LayoutRedesignRenderTests.LayoutFakeEngine(picture, work), new()
            {
                ["Weapon Release"] = "[" + ExtraJson("Weapon Release", false, AxisTuningTests.Stick, "JOY_BTN6", []) + "]",
                ["Gun Trigger"] = "[" + ExtraJson("Gun Trigger", false, AxisTuningTests.Stick, "JOY_BTN8", ["Shift\\u00A7" + AxisTuningTests.Stick + "\\u00A7JOY_BTN4\\u00A7False"]) + "]"
            });
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLayoutVmAsync(fake, work));
            try
            {
                Dictionary<string, string> stick = vm.DeviceReferenceBindings(AxisTuningTests.Stick);
                Assert.Contains("Weapon Release", stick["JOY_BTN6"]);
                Assert.Contains("Gun Trigger", stick["JOY_BTN8"]);
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1920, 1080), (1280, 800) })
                    {
                        foreach (string page in new[] { "view", "tables", "bind" })
                        {
                            AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.LayoutPages.ShowAsync(vm, page));
                            AvaloniaRenderThread.Pump();
                            UiDesignRenderTests.RenderWindow(vm, width, height, Path.Combine(outDir, "warm-up.png"));
                            UiDesignRenderTests.RenderWindow(vm, width, height, Path.Combine(outDir, "layout-" + page + "-two-inputs-" + width + "-" + theme + ".png"));
                        }
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

    private static async Task<MainWindowViewModel> CreateLayoutVmAsync(IEngineCliClient fake, string work)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string profile = Path.Combine(work, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        vm.LayoutExportPath = Path.Combine(work, "Cheat sheets");
        vm.StatusText = "Ready.";
        return vm;
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

    internal static string ExtraJson(string relation, bool axis, string device, string input, string[] reformers) =>
        "{\"RelationName\":\"" + relation + "\",\"IsAxis\":" + (axis ? "true" : "false") + ",\"Joystick\":\"" + device + "\",\"JAxis\":\"" + (axis ? input : string.Empty) +
        "\",\"JButton\":\"" + (axis ? string.Empty : input) + "\",\"AllReformers\":[" + string.Join(",", reformers.Select(r => "\"" + r + "\"")) + "]}";

    // Any fake engine, with "ExtraBinds" added to the relations named in its binds list.
    internal sealed class ExtraInputsEngine(IEngineCliClient inner, Dictionary<string, string> extras) : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            EngineCommandResult result = await inner.RunAsync(engineExePath, arguments, cancellationToken);
            if (!AxisTuningTests.Seq(arguments, "binds", "list") || !result.StandardOutput.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return result;
            JsonNode? doc = JsonNode.Parse(result.StandardOutput);
            foreach (JsonNode? bind in doc?["Binds"]?.AsArray() ?? [])
            {
                if ((string?)bind?["RelationName"] is { } name && extras.TryGetValue(name, out string? json))
                    bind!["ExtraBinds"] = JsonNode.Parse(json);
            }
            return new EngineCommandResult { ExitCode = result.ExitCode, Duration = result.Duration, StandardOutput = doc!.ToJsonString(), StandardError = result.StandardError, Arguments = result.Arguments };
        }
    }

    // UiDesignRenderTests' profile, with Weapon Release also on the MOZA base (BTN 2 with Shift), Pitch also on the MOZA's
    // Y axis, and answers to the multi-bind commands.
    internal sealed class MultiBindFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public UiDesignRenderTests.DesignFakeEngine Design { get; } = new();

        private readonly ExtraInputsEngine extras;

        public MultiBindFakeEngine()
        {
            extras = new ExtraInputsEngine(Design, new Dictionary<string, string>
            {
                ["Weapon Release"] = "[" + ExtraJson("Weapon Release", false, Moza, "JOY_BTN2", ["Shift\\u00A7" + Moza + "\\u00A7JOY_BTN4\\u00A7False"]) + "]",
                ["Pitch"] = "[" + ExtraJson("Pitch", true, Moza, "JOY_Y", []) + "]"
            });
        }

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            if (AxisTuningTests.Seq(arguments, "binds", "extra") || AxisTuningTests.Seq(arguments, "devices", "copy-to"))
            {
                string text = AxisTuningTests.Seq(arguments, "devices", "copy-to")
                    ? "Copy to another device (preview)\nMode: same (the same product: every input keeps its id)\n  JOY_BTN1 -> JOY_BTN1 | same input | 1 relation(s)\n  JOY_BTN2 -> JOY_BTN2 | same input | 1 relation(s)\nWould copy 2 input(s)."
                    : "Relation: Weapon Release (button)";
                return new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = text, StandardError = string.Empty, Arguments = arguments.ToArray() };
            }
            return await extras.RunAsync(engineExePath, arguments, cancellationToken);
        }
    }
}
