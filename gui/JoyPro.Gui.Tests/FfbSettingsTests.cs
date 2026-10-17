using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;
using static JoyPro.Gui.Tests.AxisTuningTests;

namespace JoyPro.Gui.Tests;

// The game force feedback panel (Settings page) against a fake engine that keeps the profile's ffb entries the way
// engine/EngineFfbCli.cs does: IL-2 Korea game-wide (device *), DCS per device and aircraft.
[SupportedOSPlatform("windows")]
public sealed class FfbSettingsTests
{
    private const string StickA = "Stick A {00000000-0000-0000-0000-0000000000A1}";
    private const string StickB = "Stick B {00000000-0000-0000-0000-0000000000B2}";

    [Fact]
    public async Task Expanding_LoadsTheModelsAndTheStoredValues()
    {
        FfbFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);

        Assert.Equal(["DCS", "IL2Game", "IL2Korea"], vm.FfbGames.Select(g => g.Game).ToArray());
        Assert.Contains(fake.Calls, c => Seq(c, "ffb", "models"));
        Assert.Contains(fake.Calls, c => Seq(c, "ffb", "show") && c.Contains("--profile"));

        SelectGame(vm, "IL2Korea");
        Assert.False(vm.FfbNeedsDevice);
        Assert.False(vm.FfbHasPlanes);
        Assert.True(vm.FfbLoaded);
        Assert.Equal(
            ["ffEnabled", "ffAmplitude", "ffForce", "ffForceFactorMin", "ffForceFactorMax", "ffPositionFactor", "ffContactFactor", "joyFilter"],
            vm.FfbEditors.Select(e => e.Key).ToArray());

        FfbParameterEditor force = Editor(vm, "ffForce");
        Assert.True(force.IsStored);
        Assert.Equal(80, force.Value);
        Assert.Equal("80", force.Text);
        Assert.Equal("Stored", force.SourceText);

        FfbParameterEditor amplitude = Editor(vm, "ffAmplitude");
        Assert.False(amplitude.IsStored);
        Assert.Equal(100, amplitude.Value);
        Assert.Equal("Not stored (game default 100)", amplitude.SourceText);

        FfbParameterEditor factorMax = Editor(vm, "ffForceFactorMax");
        Assert.Equal(200, factorMax.Maximum);
        Assert.Equal(1, factorMax.Step);
        Assert.True(Editor(vm, "ffEnabled").IsToggle);
        Assert.True(Editor(vm, "ffEnabled").IsOn);
        Assert.Contains("noisy axis", Editor(vm, "joyFilter").ToolTipText);
        Assert.Contains("1 of 8", vm.FfbStatus);
        Assert.True(vm.CanClearFfb);
        Assert.False(vm.CanApplyFfb);
    }

    [Fact]
    public async Task KoreaApply_SendsOnlyTheChangedValues_ForTheWholeGame()
    {
        FfbFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        SelectGame(vm, "IL2Korea");

        Editor(vm, "ffForceFactorMax").Text = "150";
        Editor(vm, "ffEnabled").IsOn = false;
        Assert.True(vm.FfbHasEdits);
        Assert.True(vm.CanApplyFfb);

        fake.Calls.Clear();
        await vm.ApplyFfbSettingsCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => Seq(c, "ffb", "set"));
        Assert.True(Seq(set, "ffb", "set", "--device", "*", "--game", "IL2Korea"));
        Assert.True(Seq(set, "--param", "ffForceFactorMax=150"));
        Assert.True(Seq(set, "--param", "ffEnabled=false"));
        Assert.Equal(2, set.Count(a => a == "--param"));
        Assert.DoesNotContain("--plane", set);
        Assert.Contains("--profile", set);

        // Reloaded from the profile afterwards.
        Assert.Contains(fake.Calls, c => Seq(c, "ffb", "show"));
        Assert.Equal("ffEnabled=0;ffForce=80;ffForceFactorMax=150", fake.Entries["*|game:IL2Korea"]);
        Assert.True(Editor(vm, "ffForceFactorMax").IsStored);
        Assert.Equal(150, Editor(vm, "ffForceFactorMax").Value);
        Assert.False(Editor(vm, "ffEnabled").IsOn);
        Assert.False(vm.FfbHasEdits);
    }

    [Fact]
    public async Task InvalidValues_AreNotSent_AndRevertRestores()
    {
        FfbFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        SelectGame(vm, "IL2Korea");

        FfbParameterEditor factorMin = Editor(vm, "ffForceFactorMin");
        factorMin.Text = "250";
        Assert.Contains("0 .. 200", vm.FfbValidation);
        Assert.False(vm.CanApplyFfb);
        fake.Calls.Clear();
        await vm.ApplyFfbSettingsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(fake.Calls, c => Seq(c, "ffb", "set"));

        factorMin.Text = "120";
        Assert.Equal(string.Empty, vm.FfbValidation);
        Assert.True(vm.CanApplyFfb);
        vm.RevertFfbEditsCommand.Execute(null);
        Assert.Equal(100, factorMin.Value);
        Assert.False(vm.FfbHasEdits);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Clear_DropsTheScope_AndUnsetDropsOneValue()
    {
        FfbFakeEngine fake = new();
        fake.Entries["*|game:IL2Korea"] = "ffForce=80;joyFilter=10";
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        SelectGame(vm, "IL2Korea");

        fake.Calls.Clear();
        await vm.UnsetFfbParameterCommand.ExecuteAsync(Editor(vm, "joyFilter"));
        IReadOnlyList<string> unset = Assert.Single(fake.Calls, c => Seq(c, "ffb", "clear"));
        Assert.True(Seq(unset, "ffb", "clear", "--device", "*", "--game", "IL2Korea", "--param", "joyFilter"));
        Assert.False(Editor(vm, "joyFilter").IsStored);
        Assert.True(Editor(vm, "ffForce").IsStored);

        fake.Calls.Clear();
        await vm.ClearFfbSettingsCommand.ExecuteAsync(null);
        IReadOnlyList<string> clear = Assert.Single(fake.Calls, c => Seq(c, "ffb", "clear"));
        Assert.True(Seq(clear, "ffb", "clear", "--device", "*", "--game", "IL2Korea"));
        Assert.DoesNotContain("--param", clear);
        Assert.Contains("--profile", clear);
        Assert.False(fake.Entries.ContainsKey("*|game:IL2Korea"));
        Assert.False(vm.CanClearFfb);
        Assert.All(vm.FfbEditors, e => Assert.False(e.IsStored));
    }

    [Fact]
    public async Task Dcs_IsSetPerDeviceAndAircraft()
    {
        FfbFakeEngine fake = new();
        fake.Entries[StickA + "|game:DCS"] = "trimmer=0.7";
        fake.Entries[StickA + "|plane:DCS:F-16C_50"] = "shake=0.3";
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        vm.ConnectedDevices.Add(StickB);
        await ExpandAsync(vm);

        SelectGame(vm, "DCS");
        Assert.True(vm.FfbNeedsDevice);
        Assert.True(vm.FfbHasPlanes);
        Assert.Contains(vm.FfbDeviceOptions, o => o.Value == StickA && o.Label.Contains("stored"));
        Assert.Contains(vm.FfbDeviceOptions, o => o.Value == StickB && o.Label.Contains("connected"));
        Assert.Equal(StickA, vm.SelectedFfbDevice?.Value);
        Assert.Equal(string.Empty, vm.SelectedFfbPlane?.Value);
        Assert.Contains(vm.FfbPlaneOptions, o => o.Value == "F-16C_50");
        Assert.Equal(0.7, Editor(vm, "trimmer").Value);
        Assert.Equal(0.01, Editor(vm, "trimmer").Step);

        vm.SelectedFfbPlane = vm.FfbPlaneOptions.Single(o => o.Value == "F-16C_50");
        Assert.Equal("Stored", Editor(vm, "shake").SourceText);
        Assert.Equal(0.3, Editor(vm, "shake").Value);
        Assert.Equal("From all aircraft", Editor(vm, "trimmer").SourceText);
        Assert.Equal(0.7, Editor(vm, "trimmer").Value);

        Editor(vm, "trimmer").Value = 0.5;
        Editor(vm, "invertY").IsOn = true;
        fake.Calls.Clear();
        await vm.ApplyFfbSettingsCommand.ExecuteAsync(null);
        IReadOnlyList<string> set = Assert.Single(fake.Calls, c => Seq(c, "ffb", "set"));
        Assert.True(Seq(set, "ffb", "set", "--device", StickA, "--game", "DCS", "--plane", "F-16C_50"));
        Assert.True(Seq(set, "--param", "trimmer=0.5"));
        Assert.True(Seq(set, "--param", "invertY=true"));
        Assert.Equal(2, set.Count(a => a == "--param"));
        Assert.Equal("Stored", Editor(vm, "trimmer").SourceText);
        Assert.Equal("F-16C_50", vm.SelectedFfbPlane?.Value);

        // The other stick has nothing stored; clearing a device's aircraft leaves its other scopes.
        vm.SelectedFfbDevice = vm.FfbDeviceOptions.Single(o => o.Value == StickB);
        Assert.All(vm.FfbEditors, e => Assert.False(e.IsStored));
        Assert.False(vm.CanClearFfb);
        vm.SelectedFfbDevice = vm.FfbDeviceOptions.Single(o => o.Value == StickA);
        fake.Calls.Clear();
        await vm.ClearFfbSettingsCommand.ExecuteAsync(null);
        IReadOnlyList<string> clear = Assert.Single(fake.Calls, c => Seq(c, "ffb", "clear"));
        Assert.True(Seq(clear, "ffb", "clear", "--device", StickA, "--game", "DCS", "--plane", "F-16C_50"));
        Assert.True(fake.Entries.ContainsKey(StickA + "|game:DCS"));
        Assert.Equal("From all aircraft", Editor(vm, "trimmer").SourceText);
    }

    [Fact]
    public async Task Dcs_WithoutDevice_AsksForOne()
    {
        FfbFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        // DCS also offers the devices of this machine's Saved Games folder.
        vm.DcsKnownDevices.Clear();
        vm.ConnectedDevices.Clear();
        vm.ProfileDevices.Clear();
        SelectGame(vm, "IL2Korea");
        SelectGame(vm, "DCS");
        Assert.Empty(vm.FfbDeviceOptions);
        Assert.False(vm.FfbLoaded);
        Assert.Empty(vm.FfbEditors);
        Assert.StartsWith("Pick a device", vm.FfbStatus);
        Assert.False(vm.CanApplyFfb);
    }

    [Fact]
    public async Task AnotherProfile_DropsTheShownValues_UntilReloaded()
    {
        FfbFakeEngine fake = new();
        MainWindowViewModel vm = await CreateLoadedVmAsync(fake);
        SelectGame(vm, "IL2Korea");
        Assert.True(vm.FfbLoaded);

        fake.Calls.Clear();
        vm.ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ffb-settings-other.pr0file");
        Assert.False(vm.FfbLoaded);
        Assert.Empty(vm.FfbEditors);
        Assert.Contains("Reload", vm.FfbStatus);
        Assert.Empty(fake.Calls);

        fake.Entries.Clear();
        await vm.RefreshFfbSettingsCommand.ExecuteAsync(null);
        Assert.Equal("IL2Korea", vm.SelectedFfbGame?.Game);
        Assert.True(vm.FfbLoaded);
        Assert.All(vm.FfbEditors, e => Assert.False(e.IsStored));
        Assert.Contains(fake.Calls, c => Seq(c, "ffb", "show") && c.Contains(vm.ProfilePath));
    }

    [Fact]
    public void ParseFfbModels_ReadsEveryParameter()
    {
        List<FfbGameItem> games = MainWindowViewModel.ParseFfbModels(FfbFakeEngine.ModelsJson + Environment.NewLine + "Auto-saved profile: x");
        FfbGameItem korea = games.Single(g => g.Game == "IL2Korea");
        Assert.Equal("IL-2 Series: Korea", korea.Title);
        Assert.False(korea.PerDevice);
        FfbParameterInfo filter = korea.Parameters.Single(p => p.Key == "joyFilter");
        Assert.Equal(new FfbParameterInfo("joyFilter", "Axis noise filter (%, all joysticks)", false, 0, 100, 0, true, "Smooths noisy axis sensors (older or cheap sticks)."), filter);
        Assert.True(games.Single(g => g.Game == "DCS").PerDevice);
    }

    // The panel as the Settings page shows it, into %TEMP%\JoyPro2.Gui.Tests\axis-curves (needs the whole application:
    // set JOYPRO_FFB_PAGE_PREVIEW=1 & dotnet test gui/JoyPro.Gui.Tests --filter RenderFfbPanel).
    [Fact]
    public void RenderFfbPanel()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOYPRO_FFB_PAGE_PREVIEW")))
            return;
        string outDir = AxisCurveRenderTests.OutputFolder;
        Directory.CreateDirectory(outDir);
        AvaloniaRenderThread.Run(() =>
        {
            RenderPanel(Path.Combine(outDir, "ffb-panel-il2korea.png"), vm =>
            {
                SelectGame(vm, "IL2Korea");
                Editor(vm, "ffForceFactorMax").Text = "150";
                Editor(vm, "ffContactFactor").Text = "abc";
            });
            RenderPanel(Path.Combine(outDir, "ffb-panel-dcs.png"), vm =>
            {
                SelectGame(vm, "DCS");
                vm.SelectedFfbPlane = vm.FfbPlaneOptions.Single(o => o.Value == "F-16C_50");
            }, fake =>
            {
                fake.Entries[StickA + "|game:DCS"] = "trimmer=0.7";
                fake.Entries[StickA + "|plane:DCS:F-16C_50"] = "shake=0.3;invertY=1";
            });
        });
    }

    private static void RenderPanel(string file, Action<MainWindowViewModel> edit, Action<FfbFakeEngine>? prepare = null)
    {
        FfbFakeEngine fake = new();
        prepare?.Invoke(fake);
        MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLoadedVmAsync(fake));
        vm.CurrentStage = UiStage.Settings;
        AvaloniaRenderThread.Pump();
        edit(vm);

        Views.MainWindow main = new() { DataContext = vm, Width = 1500, Height = 3200 };
        Avalonia.Size pageSize = new(1500, 3200);
        main.Measure(pageSize);
        main.Arrange(new Avalonia.Rect(pageSize));
        AvaloniaRenderThread.Pump();
        Avalonia.Controls.Expander? panel = main.GetVisualDescendantsOfType<Avalonia.Controls.Expander>()
            .FirstOrDefault(e => Equals(e.Header, "Game Force Feedback"));
        Assert.NotNull(panel);
        panel!.Measure(new Avalonia.Size(1100, double.PositiveInfinity));
        panel.Arrange(new Avalonia.Rect(new Avalonia.Point(0, 0), new Avalonia.Size(1100, panel.DesiredSize.Height)));
        AvaloniaRenderThread.Pump();
        Avalonia.PixelSize size = new(1100, (int)Math.Ceiling(panel.DesiredSize.Height));
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(size);
        using (Avalonia.Media.DrawingContext context = bitmap.CreateDrawingContext())
            context.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F8FAFC")), new Avalonia.Rect(0, 0, size.Width, size.Height));
        bitmap.Render(panel);
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    private static async Task<MainWindowViewModel> CreateLoadedVmAsync(FfbFakeEngine fake)
    {
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        await ExpandAsync(vm);
        return vm;
    }

    private static async Task ExpandAsync(MainWindowViewModel vm)
    {
        vm.EnginePath = typeof(MainWindowViewModel).Assembly.Location;
        string tempRoot = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests");
        Directory.CreateDirectory(tempRoot);
        vm.ProfilePath = Path.Combine(tempRoot, "ffb-settings.pr0file");
        vm.IsFfbSettingsExpanded = true;
        for (int i = 0; i < 200 && vm.SelectedFfbGame == null; i++)
            await Task.Delay(10);
        Assert.NotNull(vm.SelectedFfbGame);
        for (int i = 0; i < 200 && vm.IsBusy; i++)
            await Task.Delay(10);
    }

    private static void SelectGame(MainWindowViewModel vm, string game) =>
        vm.SelectedFfbGame = vm.FfbGames.Single(g => g.Game == game);

    private static FfbParameterEditor Editor(MainWindowViewModel vm, string key) => vm.FfbEditors.Single(e => e.Key == key);
}

[SupportedOSPlatform("windows")]
internal sealed class FfbFakeEngine : IEngineCliClient
{
    // `engine ffb models` as the engine prints it (engine/FfbSettings.cs).
    internal const string ModelsJson =
        "{\"Models\":[" +
        "{\"Game\":\"DCS\",\"Title\":\"DCS World\",\"Description\":\"Per device and aircraft.\",\"Parameters\":[" +
        "{\"Key\":\"trimmer\",\"Label\":\"Trimmer force\",\"Toggle\":false,\"Min\":0,\"Max\":1,\"Default\":1,\"GameWide\":false,\"Help\":null}," +
        "{\"Key\":\"shake\",\"Label\":\"Shake\",\"Toggle\":false,\"Min\":0,\"Max\":1,\"Default\":0.5,\"GameWide\":false,\"Help\":null}," +
        "{\"Key\":\"swapAxes\",\"Label\":\"Swap axes\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":0,\"GameWide\":false,\"Help\":null}," +
        "{\"Key\":\"invertX\",\"Label\":\"Invert X\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":0,\"GameWide\":false,\"Help\":null}," +
        "{\"Key\":\"invertY\",\"Label\":\"Invert Y\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":0,\"GameWide\":false,\"Help\":null}," +
        "{\"Key\":\"ignore\",\"Label\":\"Ignore (no DCS force feedback)\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":0,\"GameWide\":false,\"Help\":null}]}," +
        "{\"Game\":\"IL2Game\",\"Title\":\"IL-2 Great Battles\",\"Description\":\"Game-wide.\",\"Parameters\":[" +
        "{\"Key\":\"enabled\",\"Label\":\"Force feedback on\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":1,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"force\",\"Label\":\"Force\",\"Toggle\":false,\"Min\":0,\"Max\":1,\"Default\":1,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"amplitude\",\"Label\":\"Shake amplitude\",\"Toggle\":false,\"Min\":0,\"Max\":1,\"Default\":1,\"GameWide\":true,\"Help\":null}]}," +
        "{\"Game\":\"IL2Korea\",\"Title\":\"IL-2 Series: Korea\",\"Description\":\"Game-wide, in %.\",\"Parameters\":[" +
        "{\"Key\":\"ffEnabled\",\"Label\":\"Force feedback on\",\"Toggle\":true,\"Min\":0,\"Max\":0,\"Default\":1,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffAmplitude\",\"Label\":\"Shaking power (%)\",\"Toggle\":false,\"Min\":0,\"Max\":100,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffForce\",\"Label\":\"Feedback power (%)\",\"Toggle\":false,\"Min\":0,\"Max\":100,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffForceFactorMin\",\"Label\":\"Low-speed stick force (%)\",\"Toggle\":false,\"Min\":0,\"Max\":200,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffForceFactorMax\",\"Label\":\"High-speed stick force (%)\",\"Toggle\":false,\"Min\":0,\"Max\":200,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffPositionFactor\",\"Label\":\"Force-neutral rebalancing (%)\",\"Toggle\":false,\"Min\":0,\"Max\":100,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"ffContactFactor\",\"Label\":\"Terrain contact (%)\",\"Toggle\":false,\"Min\":0,\"Max\":100,\"Default\":100,\"GameWide\":true,\"Help\":null}," +
        "{\"Key\":\"joyFilter\",\"Label\":\"Axis noise filter (%, all joysticks)\",\"Toggle\":false,\"Min\":0,\"Max\":100,\"Default\":0,\"GameWide\":true,\"Help\":\"Smooths noisy axis sensors (older or cheap sticks).\"}]}]}";

    public List<IReadOnlyList<string>> Calls { get; } = [];

    // The profile's FfbOverrides: "<device>|game:<game>" / "<device>|plane:<game>:<plane>" -> "k=v;...".
    public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["*|game:IL2Korea"] = "ffForce=80"
    };

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments.ToArray());
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = BuildOutput(arguments),
            Arguments = arguments.ToArray()
        });
    }

    private string BuildOutput(IReadOnlyList<string> args)
    {
        if (Seq(args, "ffb", "models"))
            return ModelsJson;
        if (Seq(args, "ffb", "show"))
            return "{\"Entries\":{" + string.Join(",", Entries.Select(e => "\"" + e.Key + "\":\"" + e.Value + "\"")) + "},\"Game\":\"\",\"Plane\":null,\"Resolved\":null}";
        if (Seq(args, "ffb", "set") || Seq(args, "ffb", "clear"))
        {
            string plane = Option(args, "--plane");
            string key = Option(args, "--device") + "|" + (plane.Length == 0 ? "game:" + Option(args, "--game") : "plane:" + Option(args, "--game") + ":" + plane);
            SortedDictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            if (Entries.TryGetValue(key, out string? stored))
            {
                foreach (string part in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    values[part.Split('=')[0]] = part.Split('=')[1];
            }
            List<string> parameters = Options(args, "--param");
            if (Seq(args, "ffb", "clear"))
            {
                if (parameters.Count == 0)
                    values.Clear();
                foreach (string name in parameters)
                    values.Remove(name);
            }
            else
            {
                foreach (string option in parameters)
                {
                    string[] kv = option.Split('=');
                    values[kv[0]] = kv[1] == "true" ? "1" : kv[1] == "false" ? "0" : double.Parse(kv[1], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                }
            }
            if (values.Count == 0)
                Entries.Remove(key);
            else
                Entries[key] = string.Join(";", values.Select(v => v.Key + "=" + v.Value));
            return "{\"Key\":\"" + key + "\",\"Message\":\"ok\"}" + Environment.NewLine + "Auto-saved profile: x";
        }
        return "ok";
    }

    private static string Option(IReadOnlyList<string> args, string name) => Options(args, name).FirstOrDefault() ?? string.Empty;

    private static List<string> Options(IReadOnlyList<string> args, string name)
    {
        List<string> values = [];
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[i + 1]);
        }
        return values;
    }
}
