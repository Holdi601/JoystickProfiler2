using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The main pages of the design pass (Start dashboard, Relation Studio, Bind Studio, Modifiers) rendered offscreen as the
// whole window, at 1920x1080 and 1280x800, in the dark and the light theme, with a fake engine that holds a realistic
// profile (relations of several games, binds with modifiers, an axis with a curve, devices, sync records). Needs the
// whole application, so it renders only when JOYPRO_UI_DESIGN_OUT names an output folder, and must then run alone:
//   set JOYPRO_UI_DESIGN_OUT=<repo>\docs\ui & set JOYPRO_UI_DESIGN_TAG=after & dotnet test gui/JoyPro.Gui.Tests --filter UiDesignRenderTests
// Files: <page>-<tag>-<width>-<theme>.png (tag defaults to "after"), plus relations-empty-<tag>.png (no profile).
[SupportedOSPlatform("windows")]
public sealed class UiDesignRenderTests
{
    [Fact]
    public void RenderDesignPages()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        string tag = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_TAG") is { Length: > 0 } t ? t : "after";
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(CreateLoadedVmAsync());
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1920, 1080), (1280, 800) })
                    {
                        foreach ((string page, UiStage stage) in new[] { ("start", UiStage.Start), ("relations", UiStage.Relations), ("binds", UiStage.Binds), ("modifiers", UiStage.Modifiers) })
                        {
                            vm.CurrentStage = stage;
                            AvaloniaRenderThread.Pump();
                            RenderWindow(vm, width, height, Path.Combine(outDir, page + "-" + tag + "-" + width + "-" + theme + ".png"));
                        }
                    }
                }

                // The whole Bind Studio (record, modifiers, the axis curve) and a Relation Studio tool, dark.
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                vm.CurrentStage = UiStage.Binds;
                AvaloniaRenderThread.Pump();
                RenderWindow(vm, 1920, 2300, Path.Combine(outDir, "binds-full-" + tag + ".png"));
                vm.CurrentStage = UiStage.Relations;
                vm.StartAutoGroupCommand.Execute(null);
                AvaloniaRenderThread.Pump();
                RenderWindow(vm, 1920, 1080, Path.Combine(outDir, "relations-autogroup-" + tag + ".png"));
                vm.CloseRelationToolsCommand.Execute(null);

                // No profile open: the empty states.
                MainWindowViewModel empty = new(new DesignFakeEngine { Empty = true }, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = typeof(MainWindowViewModel).Assembly.Location
                };
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                empty.CurrentStage = UiStage.Relations;
                AvaloniaRenderThread.Pump();
                RenderWindow(empty, 1280, 800, Path.Combine(outDir, "relations-empty-" + tag + ".png"));
                empty.CurrentStage = UiStage.Start;
                AvaloniaRenderThread.Pump();
                RenderWindow(empty, 1280, 800, Path.Combine(outDir, "start-empty-" + tag + ".png"));
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    internal static async Task<MainWindowViewModel> CreateLoadedVmAsync()
    {
        DesignFakeEngine fake = new();
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ui-design");
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "My Sticks.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        await vm.LoadDeviceCardsCommand.ExecuteAsync(null);
        await vm.RefreshRelationsCommand.ExecuteAsync(null);
        await vm.RefreshBindsCommand.ExecuteAsync(null);
        await vm.RefreshModifiersCommand.ExecuteAsync(null);
        await vm.StartGameConfigWatcherAsync();
        vm.GameCards.Single(c => c.GameId == "WarThunder").Result = "Imported 14:02";

        // Relations: one relation open with its commands.
        vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Gear Up/Down");
        await WaitForAsync(() => vm.RelationNodes.Count > 0);

        // Binds: the axes first (sorted by type), the axis Pitch with its curve open.
        vm.SortBindsByColumnCommand.Execute("TypeDisplay");
        vm.SelectedBind = vm.Binds.Single(b => b.RelationName == "Pitch");
        vm.IsAxisTuningExpanded = true;
        await WaitForAsync(() => vm.AxisTuningLoaded);

        // Modifiers: the first one selected.
        vm.SelectedModifierItem = vm.ModifierItems.FirstOrDefault();
        vm.StatusText = "Ready.";
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }

    internal static void RenderWindow(MainWindowViewModel vm, int width, int height, string file)
    {
        MainWindow main = new() { DataContext = vm, Width = width, Height = height };
        Size size = new(width, height);
        Control page = (Control)main.Content!;
        // Offscreen, the virtualizing lists do not learn their viewport (no shown window): they show all their rows in a
        // plain panel, from the first one (as after a click on a visible row).
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(main).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        for (int pass = 0; pass < 6; pass++)
        {
            main.Measure(size);
            main.Arrange(new Rect(size));
            page.Measure(size);
            page.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        // The window's own background (Fluent's region color, or JoyPro's page background) in the theme of the render.
        IBrush background = main.TryFindResource("JP.PageBackgroundBrush", main.ActualThemeVariant, out object? page2) && page2 is IBrush own
            ? own
            : new SolidColorBrush(main.ActualThemeVariant == ThemeVariant.Light ? Color.Parse("#F3F3F3") : Color.Parse("#202020"));
        // Render clears the target: the page goes onto the background afterwards.
        using RenderTargetBitmap content = new(new PixelSize(width, height));
        content.Render(page);
        using RenderTargetBitmap bitmap = new(new PixelSize(width, height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(background, new Rect(size));
            context.DrawImage(content, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    // A profile with relations of DCS, IL-2 Korea and War Thunder, binds on a Warthog stick and throttle with two
    // modifiers, the axis "Pitch" with a curve (the axis tuning answers of AxisTuningFakeEngine), devices and sync records.
    internal sealed class DesignFakeEngine : IEngineCliClient
    {
        private const string Stick = AxisTuningTests.Stick;
        private const string Throttle = "Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}";
        private readonly AxisTuningFakeEngine tuning = new();

        public bool Empty { get; init; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (AxisTuningTests.Seq(arguments, "binds", "tuning"))
                return tuning.RunAsync(engineExePath, arguments, cancellationToken);
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static readonly (string Name, bool Axis, string Group, string Device, string Input, string[] Mods, (string Game, string Id, string Planes)[] Nodes)[] Profile =
        [
            ("Pitch", true, "Flight", Stick, "JOY_Y", [], [("DCS", "a2001cdnil", "F-16C_50,A-10C,FA-18C_hornet"), ("IL2Korea", "pitch", "IL2Korea")]),
            ("Roll", true, "Flight", Stick, "JOY_X", [], [("DCS", "a2002cdnil", "F-16C_50,A-10C,FA-18C_hornet"), ("IL2Korea", "roll", "IL2Korea")]),
            ("Throttle", true, "Flight", Throttle, "JOY_Z", [], [("DCS", "a2004cdnil", "F-16C_50,FA-18C_hornet"), ("WarThunder", "throttle", "WarThunder")]),
            ("Weapon Release", false, "Weapons", Stick, "JOY_BTN2", [], [("DCS", "d3001pnilu3001cd2vd1vpnilvunil", "F-16C_50,FA-18C_hornet,A-10C"), ("WarThunder", "ID_BOMBS", "WarThunder"), ("IL2Korea", "bombs", "IL2Korea")]),
            ("Gun Trigger", false, "Weapons", Stick, "JOY_BTN1", [], [("DCS", "d3002pnilu3002cd2vd1vpnilvunil", "F-16C_50,FA-18C_hornet"), ("WarThunder", "ID_FIRE_MGUNS", "WarThunder")]),
            ("Master Arm", false, "Weapons", Throttle, "JOY_BTN12", ["Shift"], [("DCS", "d3003pnilu3003cd2vd1vpnilvunil", "F-16C_50")]),
            ("TMS Up", false, "Sensors", Stick, "JOY_BTN7", [], [("DCS", "d3004pnilu3004cd16vd1vpnilvunil", "F-16C_50,A-10C")]),
            ("TMS Down", false, "Sensors", Stick, "JOY_BTN9", [], [("DCS", "d3005pnilu3005cd16vd1vpnilvunil", "F-16C_50,A-10C")]),
            ("Countermeasures Program", false, "Defensive", Throttle, "JOY_BTN5", ["Shift"], [("DCS", "d3006pnilu3006cd34vd1vpnilvunil", "F-16C_50")]),
            ("Gear Up/Down", false, "Systems", Throttle, "JOY_BTN22", ["Shift", "Pinky"], [("DCS", "d68pnilunilcdnilvdnilvpnilvunil", "F-16C_50,FA-18C_hornet,A-10C"), ("IL2Korea", "gear", "IL2Korea"), ("WarThunder", "ID_GEAR", "WarThunder")]),
            ("Flaps Down", false, "Systems", "", "", [], [("DCS", "d145pnilunilcdnilvdnilvpnilvunil", "FA-18C_hornet,A-10C"), ("WarThunder", "ID_FLAPS_DOWN", "WarThunder")]),
            ("Speed Brake Out", false, "Systems", "", "", [], [("DCS", "d147pnilunilcdnilvdnilvpnilvunil", "F-16C_50,FA-18C_hornet,A-10C")]),
            ("Trim Nose Down", false, "Flight", Stick, "JOY_BTN10", ["Pinky"], [("DCS", "d3010pnilu3010cd2vd1vpnilvunil", "F-16C_50")]),
            ("Canopy", false, "Systems", "", "", [], [("DCS", "d71pnilunilcdnilvdnilvpnilvunil", "F-16C_50,A-10C")]),
        ];

        private string Output(IReadOnlyList<string> args)
        {
            if (AxisTuningTests.Seq(args, "relations", "list"))
            {
                if (Empty)
                    return "{\"Relations\":[]}";
                IEnumerable<string> relations = Profile.Select(r =>
                    "{\"Name\":\"" + r.Name + "\",\"IsAxis\":" + (r.Axis ? "true" : "false") + ",\"Groups\":[\"" + r.Group + "\"],\"Nodes\":[" +
                    string.Join(",", r.Nodes.Select(n => "{\"Id\":\"" + n.Id + "\",\"Game\":\"" + n.Game + "\",\"Aircraft\":{" +
                                                          string.Join(",", n.Planes.Split(',').Select(p => "\"" + p + "\":true")) + "}}")) + "]," +
                    "\"Bind\":" + (r.Device.Length == 0 ? "null" : "{\"Joystick\":\"" + r.Device + "\",\"" + (r.Axis ? "JAxis" : "JButton") + "\":\"" + r.Input + "\"}") + "}");
                return "{\"Relations\":[" + string.Join(",", relations) + "]}";
            }
            int show = IndexOf(args, "relations", "show");
            if (show >= 0 && show + 2 < args.Count)
            {
                string name = args[show + 2];
                var relation = Profile.FirstOrDefault(r => r.Name == name);
                if (relation.Name == null)
                    return "Relation: " + name;
                return "Relation: " + relation.Name + Environment.NewLine + "Nodes:" + Environment.NewLine +
                       string.Join(Environment.NewLine, relation.Nodes.Select((n, i) => "  [" + (i + 1) + "] game=" + n.Game + ", id=" + n.Id + ", aircraft=" + n.Planes));
            }
            if (AxisTuningTests.Seq(args, "binds", "modifier", "list"))
            {
                if (Empty)
                    return "Modifiers:";
                return string.Join(Environment.NewLine, "Modifiers:",
                    "  - Shift (3): Master Arm, Countermeasures Program, Gear Up/Down",
                    "  - Pinky (2): Gear Up/Down, Trim Nose Down");
            }
            if (AxisTuningTests.Seq(args, "binds", "list"))
            {
                if (Empty)
                    return "{\"Binds\":[]}";
                IEnumerable<string> binds = Profile.Where(r => r.Device.Length > 0).Select(r =>
                    "{\"RelationName\":\"" + r.Name + "\",\"IsAxis\":" + (r.Axis ? "true" : "false") + ",\"Joystick\":\"" + r.Device + "\"," +
                    "\"JAxis\":\"" + (r.Axis ? r.Input : string.Empty) + "\",\"JButton\":\"" + (r.Axis ? string.Empty : r.Input) + "\"," +
                    "\"Inverted\":false,\"Slider\":false,\"Deadzone\":" + (r.Axis ? "0.05" : "0") + ",\"Curvature\":[" + (r.Axis ? "0.3" : string.Empty) + "],\"SaturationX\":1,\"SaturationY\":1," +
                    "\"AllReformers\":[" + string.Join(",", r.Mods.Select(m => "\"" + m + "\\u00A7" + (m == "Shift" ? Stick : Throttle) + "\\u00A7" + (m == "Shift" ? "JOY_BTN4" : "JOY_BTN3") + "\\u00A7False\"")) + "],\"DoubleTap\":false}");
                return "{\"Binds\":[" + string.Join(",", binds) + "]}";
            }
            if (AxisTuningTests.Seq(args, "games", "installs") || AxisTuningTests.Seq(args, "games", "install"))
            {
                return "{\"Games\":[" +
                       "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":true,\"InstallPath\":\"D:\\\\DCS World\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"IL2Korea\",\"Name\":\"IL-2 Sturmovik: Korea\",\"Installed\":true,\"InstallPath\":\"D:\\\\IL-2 Korea\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"WarThunder\",\"Name\":\"War Thunder\",\"Installed\":true,\"InstallPath\":\"D:\\\\War Thunder\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                       "{\"GameId\":\"StarCitizen\",\"Name\":\"Star Citizen\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}" +
                       "]}";
            }
            if (AxisTuningTests.Seq(args, "games", "integrations"))
            {
                return "{\"Integrations\":[" +
                       "{\"GameId\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Aliases\":[\"warthunder\"],\"Notes\":\"\",\"SupportsCatalogRefresh\":true}" +
                       "]}";
            }
            if (AxisTuningTests.Seq(args, "db", "check-updates"))
                return "{\"Games\":[],\"Outdated\":0}";
            if (AxisTuningTests.Seq(args, "devices", "list"))
            {
                return string.Join(Environment.NewLine, "Connected:", "  - " + Stick, "  - " + Throttle, "  - Pedals - MFG Crosswind {00000000-0000-0000-0000-0000000000a3}",
                    "  - VPforce Rhino FFB Joystick {00000000-0000-0000-0000-0000000000a4}", "  - WINWING F18 TAKEOFF PANEL {00000000-0000-0000-0000-0000000000a5}",
                    "  - vJoy Device {00000000-0000-0000-0000-0000000000a6}", "  - vJoy Driver", "  - Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b1}",
                    "  - Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b2}", "  - Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b3}",
                    "Profile:", "  - " + Stick, "  - " + Throttle);
            }
            if (AxisTuningTests.Seq(args, "hud", "devices"))
            {
                // A PC like the user's: the Warthog (in the profile), MFG pedals, a Rhino FFB base without a grip set, a
                // WinWing panel, vJoy, and a Wooting keyboard with three controller interfaces.
                return "{\"Devices\":[" +
                       "{\"Device\":\"" + Stick + "\",\"Name\":\"Joystick - HOTAS Warthog\",\"VidPid\":\"044F:0402\",\"Model\":\"Thrustmaster HOTAS Warthog Joystick\",\"ModelId\":\"thrustmaster-warthog-stick\",\"Kind\":\"stick\",\"Buttons\":19,\"Hats\":4,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"" + Throttle + "\",\"Name\":\"Throttle - HOTAS Warthog\",\"VidPid\":\"044F:0404\",\"Model\":\"Thrustmaster HOTAS Warthog Throttle\",\"ModelId\":\"thrustmaster-hotas-warthog-throttle\",\"Kind\":\"throttle\",\"Buttons\":32,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_Z\"},{\"Input\":\"JOY_RZ\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"Pedals - MFG Crosswind {00000000-0000-0000-0000-0000000000a3}\",\"Name\":\"MFG Crosswind V2\",\"VidPid\":\"16D0:0A38\",\"Model\":\"MFG Crosswind V2 Rudder Pedals\",\"ModelId\":\"mfg-crosswind-v2\",\"Kind\":\"pedals\",\"Buttons\":2,\"Hats\":0,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"},{\"Input\":\"JOY_RZ\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"VPforce Rhino FFB Joystick {00000000-0000-0000-0000-0000000000a4}\",\"Name\":\"Rhino FFB Joystick\",\"VidPid\":\"FFFF:2055\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"stick\",\"Buttons\":64,\"Hats\":4,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"},{\"Input\":\"JOY_Z\"},{\"Input\":\"JOY_RX\"},{\"Input\":\"JOY_RY\"},{\"Input\":\"JOY_RZ\"},{\"Input\":\"JOY_SLIDER1\"}],\"Image\":\"\",\"GripChoices\":[{\"Id\":\"vpforce-rhino-tm-warthog-grip\",\"Name\":\"Thrustmaster Warthog Grip on Rhino\",\"Image\":\"\"}]}," +
                       "{\"Device\":\"WINWING F18 TAKEOFF PANEL {00000000-0000-0000-0000-0000000000a5}\",\"Name\":\"WINWING F18 TAKEOFF PANEL\",\"VidPid\":\"4098:BE04\",\"Model\":\"WinCtrl F/A-18 Takeoff Panel (PTO)\",\"ModelId\":\"winwing-f18-takeoff-panel\",\"Kind\":\"panel\",\"Buttons\":38,\"Hats\":0,\"Axes\":[{\"Input\":\"JOY_X\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"vJoy Device {00000000-0000-0000-0000-0000000000a6}\",\"Name\":\"vJoy - Virtual Joystick\",\"VidPid\":\"1234:BEAD\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"stick\",\"Buttons\":32,\"Hats\":4,\"Axes\":[{\"Input\":\"JOY_X\"},{\"Input\":\"JOY_Y\"},{\"Input\":\"JOY_Z\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b1}\",\"Name\":\"Wooting Two HE (ARM)\",\"VidPid\":\"31E3:1231\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"other\",\"Buttons\":12,\"Hats\":1,\"Axes\":[{\"Input\":\"JOY_X\"}],\"Image\":\"\"}," +
                       "{\"Device\":\"Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b2}\",\"Name\":\"Wooting Two HE (ARM)\",\"VidPid\":\"31E3:1231\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"other\",\"Buttons\":0,\"Hats\":0,\"Axes\":[],\"Image\":\"\"}," +
                       "{\"Device\":\"Wooting Two HE (ARM) {00000000-0000-0000-0000-0000000000b3}\",\"Name\":\"Wooting Two HE (ARM)\",\"VidPid\":\"31E3:1231\",\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"other\",\"Buttons\":3,\"Hats\":0,\"Axes\":[],\"Image\":\"\"}" +
                       "]}";
            }
            if (AxisTuningTests.Seq(args, "sync", "status"))
            {
                if (Empty)
                    return "{\"Games\":[]}";
                return "{\"Games\":[" +
                       "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"Installed\":true,\"Disabled\":false,\"ConfigPath\":\"\",\"State\":\"unchanged\",\"Tracked\":true,\"RecordedUtc\":\"2026-10-11T12:05:00Z\",\"Reason\":\"export\",\"Watch\":[]}," +
                       "{\"Game\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"Installed\":true,\"Disabled\":false,\"ConfigPath\":\"\",\"State\":\"unchanged\",\"Tracked\":true,\"RecordedUtc\":\"2026-10-10T18:40:00Z\",\"Reason\":\"import\",\"Watch\":[]}" +
                       "]}";
            }
            if (AxisTuningTests.Seq(args, "backups", "list"))
                return "[{\"Id\":\"20261011-140500-DCS\",\"Game\":\"DCS\",\"Created\":\"2026-10-11T14:05:00\",\"Files\":[\"a\"],\"Command\":\"dcs export\"}]";
            return "ok";
        }

        private static int IndexOf(IReadOnlyList<string> args, string first, string second)
        {
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] == first && args[i + 1] == second)
                    return i;
            }
            return -1;
        }
    }
}
