using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Setup wizard window (docs/setup-wizard.md).
// - WizardRunsEveryStepThroughTheEngine: the view model with a fake engine: what each step asks the engine (GUI-CLI
//   parity: setup devices / aircraft / plan / render / apply with the user's roles, priorities, modifiers and realism),
//   the review's edits and Apply.
// - RenderSetupWizard: every step rendered offscreen with the REAL engine on the fake device set of the fixture
//   engine\tests\fixtures\setupwizard\devices.tsv (VKB MCG Pro on Gunfighter, VPforce Rhino, Virpil WarBRD + Alpha-L,
//   VPC MT-50CM throttle, MFG Crosswind, WinWing takeoff panel, vJoy, Wooting). Only when JOYPRO_SETUP_WIZARD_PREVIEW
//   names an output folder, after the engine was built:
//     set JOYPRO_SETUP_WIZARD_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\setup-wizard & dotnet test gui/JoyPro.Gui.Tests --filter SetupWizardRenderTests
[SupportedOSPlatform("windows")]
public sealed class SetupWizardRenderTests
{
    [Fact]
    public void WizardRunsEveryStepThroughTheEngine()
    {
        string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "setup-wizard-vm-" + Guid.NewGuid().ToString("N"));
        FakeSetupEngine fake = new();
        SetupWizardViewModel vm = new(fake.RunAsync, [new SetupGameChoice("DCS", "DCS World", true), new SetupGameChoice("StarCitizen", "Star Citizen", true),
            new SetupGameChoice("WarThunder", "War Thunder", false)], workFolder: work);
        Assert.Equal(["DCS", "StarCitizen"], vm.ChosenGames);

        // 2 devices: the engine's proposal, then the user's role change.
        Wait(vm.NextCommand.ExecuteAsync(null));
        Assert.True(vm.IsDevicesStep);
        Assert.Equal(4, vm.Devices.Count);
        Assert.Equal(SetupRoles.LeftStick, vm.Devices.Single(d => d.Name.StartsWith("L-VPC", StringComparison.Ordinal)).SelectedRole!.Id);
        Assert.Contains(vm.Rules, r => r.StartsWith("Space sims: translation on the left stick", StringComparison.Ordinal));
        SetupDeviceItem left = vm.Devices.Single(d => d.Name.StartsWith("L-VPC", StringComparison.Ordinal));
        left.SelectedRole = vm.RoleOptions.Single(r => r.Id == SetupRoles.Unused);
        Assert.Contains(vm.Rules, r => r.StartsWith("Space sims: no left stick: translation on the throttle", StringComparison.Ordinal));
        left.SelectedRole = vm.RoleOptions.Single(r => r.Id == SetupRoles.LeftStick);

        // 3 priorities: F-16 first, F/A-18 and A-10C II equal, then Star Citizen; 1 modifier, realism 30 %.
        Wait(vm.NextCommand.ExecuteAsync(null));
        Assert.True(vm.IsPrioritiesStep);
        vm.AddPriority("F-16C_50");
        vm.AddPriority("FA-18C_hornet");
        vm.AddPriority("A-10C II", equalToLast: true);
        vm.AddPriority("StarCitizen");
        Assert.Equal("F-16C_50>FA-18C_hornet|A-10C II>StarCitizen", vm.PriorityArgument());
        Assert.Equal("Priority 2 (equal)", vm.Tiers[1].Title);
        vm.SelectedTier = vm.Tiers[2];
        vm.PriorityUpCommand.Execute(null);
        Assert.Equal("StarCitizen", vm.Tiers[1].Aircraft[0].Key);
        vm.PriorityDownCommand.Execute(null);
        vm.Modifiers = 1;
        vm.Realism = 30;
        Assert.Contains("Group by function", vm.RealismText);

        // 4 plan: the engine is asked with exactly these choices.
        Wait(vm.NextCommand.ExecuteAsync(null));
        Assert.True(vm.IsPlanStep);
        List<string> plan = fake.Calls.Last(c => c.Take(2).SequenceEqual(["setup", "plan"]));
        Assert.Equal("DCS,StarCitizen", After(plan, "--games"));
        Assert.Equal("F-16C_50>FA-18C_hornet|A-10C II>StarCitizen", After(plan, "--priorities"));
        Assert.Equal("1", After(plan, "--modifiers"));
        Assert.Contains("--prefer-fewer", plan);
        Assert.Equal("0.3", After(plan, "--realism"));
        Assert.True(vm.HasPlan);
        Assert.NotEmpty(vm.Quality);
        Assert.StartsWith("F-16C_50", vm.Quality[0].Aircraft, StringComparison.Ordinal);

        // 5 review: rows of the first device for the top aircraft; move one function, then Apply.
        Wait(vm.NextCommand.ExecuteAsync(null));
        Assert.True(vm.IsReviewStep);
        Assert.Equal("F-16C_50", vm.SelectedReviewAircraft!.Id);
        vm.SelectedReviewDevice = vm.ReviewDevices.Single(d => d.Id.StartsWith("VKB", StringComparison.Ordinal));
        Assert.NotEmpty(vm.ReviewRows);
        SetupReviewRow row = vm.ReviewRows.First(r => !r.IsKept && !r.Relation.IsAxis);
        vm.SelectedRow = row;
        Assert.NotEmpty(vm.MoveTargets);
        SetupTargetItem target = vm.MoveTargets.First();
        vm.SelectedMoveTarget = target;
        vm.MoveSelectedCommand.Execute(null);
        Assert.Equal(target.Input, row.Relation.Input);
        Assert.Equal("moved by you", row.Relation.Reason);

        Wait(vm.ApplyCommand.ExecuteAsync(null));
        Assert.True(vm.Applied);
        List<string> apply = fake.Calls.Last();
        Assert.Equal(["setup", "apply", "--plan"], apply.Take(3));
        SetupPlan written = SetupPlanJson.Read(File.ReadAllText(apply[3], Encoding.UTF8));
        Assert.Equal(target.Input, written.Relations.Single(r => r.Name == row.Relation.Name).Input);
        Assert.True(fake.ApplyMutates);
    }

    [Fact]
    public void RenderSetupWizard()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_SETUP_WIZARD_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        string engine = FindRepoFile(Path.Combine("engine", "bin", "x64", "Debug", "engine.exe")) ?? FindRepoFile(Path.Combine("engine", "bin", "x64", "Release", "engine.exe"))
                        ?? throw new InvalidOperationException("Build the engine first (engine\\bin\\x64\\Debug\\engine.exe).");
        string devices = FindRepoFile(Path.Combine("engine", "tests", "fixtures", "setupwizard", "devices.tsv")) ?? throw new InvalidOperationException("Fixture missing.");
        Directory.CreateDirectory(outDir);
        RealEngine real = new(engine, devices);

        AvaloniaRenderThread.Run(() =>
        {
            SetupWizardViewModel vm = new(real.RunAsync, [new SetupGameChoice("DCS", "DCS World", true), new SetupGameChoice("StarCitizen", "Star Citizen", true),
                new SetupGameChoice("WarThunder", "War Thunder", false), new SetupGameChoice("IL2Game", "IL-2 Sturmovik: Great Battles", false)], workFolder: Path.Combine(outDir, "work"));
            Render(vm, Path.Combine(outDir, "setup-wizard-1-games.png"));

            Wait(vm.NextCommand.ExecuteAsync(null));
            Assert.True(vm.IsDevicesStep);
            Assert.Contains(vm.Devices, d => d.SelectedRole?.Id == SetupRoles.KeyboardJoystick);
            Render(vm, Path.Combine(outDir, "setup-wizard-2-devices.png"));

            Wait(vm.NextCommand.ExecuteAsync(null));
            Assert.True(vm.IsPrioritiesStep);
            vm.AddPriority("F-16C_50");
            vm.AddPriority("FA-18C_hornet");
            vm.AddPriority("A-10C II", equalToLast: true);
            vm.AddPriority("StarCitizen");
            vm.AddPriority("UH-1H");
            vm.Modifiers = 1;
            vm.Realism = 30;
            vm.AircraftFilter = "F-";
            Render(vm, Path.Combine(outDir, "setup-wizard-3-priorities.png"));

            Wait(vm.NextCommand.ExecuteAsync(null));
            Assert.True(vm.HasPlan, vm.Status);
            Render(vm, Path.Combine(outDir, "setup-wizard-4-plan.png"));

            Wait(vm.NextCommand.ExecuteAsync(null));
            Assert.True(vm.IsReviewStep);
            vm.SelectedReviewDevice = vm.ReviewDevices.Single(d => d.Id.StartsWith("VKB", StringComparison.Ordinal));
            Wait(vm.RefreshSheetAsync());
            Assert.True(vm.HasSheet, vm.SheetNote);
            vm.SelectedRow = vm.ReviewRows.FirstOrDefault(r => !r.IsKept && !r.Relation.IsAxis);
            Render(vm, Path.Combine(outDir, "setup-wizard-5-review.png"));

            vm.SelectedReviewDevice = vm.ReviewDevices.Single(d => d.Id.StartsWith("L-VPC", StringComparison.Ordinal));
            vm.SelectedReviewAircraft = vm.ReviewAircraft.Single(a => a.Id == "StarCitizen");
            Wait(vm.RefreshSheetAsync());
            Render(vm, Path.Combine(outDir, "setup-wizard-5-review-left-stick-star-citizen.png"));
            File.Copy(vm.PlanFile, Path.Combine(outDir, "setup-plan-user-like.json"), true);
        });
    }

    // ---- helpers ----

    private static void Wait(Task task)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            AvaloniaRenderThread.Wait(task);
        else
            task.GetAwaiter().GetResult();
    }

    private static string After(List<string> args, string option)
    {
        int index = args.IndexOf(option);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : string.Empty;
    }

    private static void Render(SetupWizardViewModel vm, string file)
    {
        SetupWizardWindow window = new() { DataContext = vm, Width = 1400, Height = 900 };
        Size size = new(1400, 900);
        Control content = (Control)window.Content!;
        // Offscreen, the virtualizing lists do not learn their viewport: they show their rows in a plain panel.
        foreach (ListBox list in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<ListBox>())
        {
            list.AutoScrollToSelectedItem = false;
            list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
        }
        for (int pass = 0; pass < 6; pass++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            content.Measure(size);
            content.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
        using RenderTargetBitmap page = new(new PixelSize((int)size.Width, (int)size.Height));
        page.Render(content);
        using RenderTargetBitmap bitmap = new(new PixelSize((int)size.Width, (int)size.Height));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#202020")), new Rect(size));
            context.DrawImage(page, new Rect(size), new Rect(size));
        }
        bitmap.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    private static string? FindRepoFile(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The engine as the GUI runs it, with the fixture's fake devices.
    private sealed class RealEngine(string exe, string devices)
    {
        public async Task<EngineCommandResult?> RunAsync(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string label, bool quiet)
        {
            ProcessStartInfo info = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--status");
            info.ArgumentList.Add("false");
            foreach (string arg in args)
                info.ArgumentList.Add(arg);
            info.Environment["JOYPRO_FAKE_DEVICES"] = devices;
            info.Environment["JOYPRO_NO_INTERNET_PICTURES"] = "1";
            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new EngineCommandResult { ExitCode = process.ExitCode, StandardOutput = await output, StandardError = await error, Arguments = args.ToArray() };
        }
    }

    // Answers like the engine: devices and aircraft of a small setup, a plan made by the planner itself.
    private sealed class FakeSetupEngine
    {
        public List<List<string>> Calls { get; } = [];

        public bool ApplyMutates { get; private set; }

        private const string Vkb = "VKB-Sim Gunfighter Modern Combat Pro Twist {1}";
        private const string Throttle = "LEFT VPC Throttle MT-50 CM {2}";
        private const string LeftStick = "L-VPC Stick WarBRD {3}";
        private const string Panel = "WINWING F18 TAKEOFF PANEL {4}";

        private static List<SetupDevice> Devices()
        {
            List<SetupDevice> devices =
            [
                new() { Device = Vkb, Model = "VKB Gunfighter MCG Pro", Kind = "stick", Hand = "right", Buttons = 30 },
                new() { Device = Throttle, Model = "VPC MongoosT-50CM2 Throttle", Kind = "throttle", Hand = "left", Buttons = 40 },
                new() { Device = LeftStick, Model = "L-VPC Stick WarBRD", Kind = "stick", Hand = "left", Buttons = 30 },
                new() { Device = Panel, Model = "WinCtrl F/A-18 Takeoff Panel", Kind = "panel", Buttons = 32 }
            ];
            SetupRoles.Propose(devices);
            return devices;
        }

        private static List<SetupInput> Inputs()
        {
            List<SetupInput> inputs =
            [
                new() { Device = Vkb, Input = "JOY_X", Control = "stick.axis.roll", IsAxis = true },
                new() { Device = Vkb, Input = "JOY_Y", Control = "stick.axis.pitch", IsAxis = true },
                new() { Device = Vkb, Input = "JOY_BTN1", Control = "stick.trigger.stage1" },
                new() { Device = Vkb, Input = "JOY_BTN2", Control = "stick.hat.cms.up" },
                new() { Device = Throttle, Input = "JOY_RX", Control = "throttle.axis.left", IsAxis = true },
                new() { Device = LeftStick, Input = "JOY_X", Control = "stick.axis.roll", IsAxis = true },
                new() { Device = Panel, Input = "JOY_BTN1", Control = "panel.lever.gear.up", Label = "LDG GEAR UP", Type = "latched" },
                new() { Device = Panel, Input = "JOY_BTN2", Control = "panel.lever.gear.down", Label = "LDG GEAR DOWN", Type = "latched" }
            ];
            for (int i = 1; i <= 8; i++)
                inputs.Add(new SetupInput { Device = Vkb, Input = "JOY_BTN" + (10 + i), Control = "stick.button.thumb." + i });
            return inputs;
        }

        public Task<EngineCommandResult?> RunAsync(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string label, bool quiet)
        {
            Calls.Add(args.ToList());
            string output = "{}";
            int exit = 0;
            string action = args.Count > 1 && args[0] == "setup" ? args[1] : args.FirstOrDefault() ?? string.Empty;
            switch (action)
            {
                case "devices":
                    output = MiniJson.Write(new Dictionary<string, object>
                    {
                        { "Devices", Devices().Select(d => (object)SetupPlanJson.DeviceObject(d)).ToList() }
                    }, false);
                    break;
                case "aircraft":
                    output = "{\"Aircraft\":[{\"Key\":\"A-10C II\",\"Game\":\"DCS\",\"ClassTitle\":\"Fixed wing\"},{\"Key\":\"F-16C_50\",\"Game\":\"DCS\",\"ClassTitle\":\"Fixed wing\"}," +
                             "{\"Key\":\"FA-18C_hornet\",\"Game\":\"DCS\",\"ClassTitle\":\"Fixed wing\"},{\"Key\":\"StarCitizen\",\"Game\":\"StarCitizen\",\"ClassTitle\":\"Spaceships\"}]}";
                    break;
                case "plan":
                    SetupPlan plan = MakePlan(args);
                    string file = args[args.ToList().IndexOf("--out-plan") + 1];
                    File.WriteAllText(file, SetupPlanJson.Write(plan, true), new UTF8Encoding(false));
                    output = SetupPlanJson.Write(plan, false);
                    break;
                case "render":
                    output = "{\"Sheets\":[]}";
                    break;
                case "apply":
                    ApplyMutates = mutatesProfile;
                    break;
                case "cockpits":
                    exit = 1;
                    output = string.Empty;
                    break;
            }
            return Task.FromResult<EngineCommandResult?>(new EngineCommandResult
            {
                ExitCode = exit, StandardOutput = output, StandardError = exit == 0 ? string.Empty : "Unknown command area: cockpits", Arguments = args.ToArray()
            });
        }

        private static SetupPlan MakePlan(IReadOnlyList<string> args)
        {
            List<string> list = args.ToList();
            SetupRequest request = new() { Devices = Devices(), Inputs = Inputs() };
            request.Functions["pitch"] = new SetupFunction { Id = "pitch", Name = "Pitch", IsAxis = true, Side = "stick", Prefs = ["stick.axis.pitch"], Frequency = "constant" };
            request.Functions["cms_fwd"] = new SetupFunction { Id = "cms_fwd", Name = "CMS FWD", Side = "stick", Prefs = ["stick.hat.cms.up", "stick.button.*"], Frequency = "frequent" };
            request.Functions["radar_lock"] = new SetupFunction { Id = "radar_lock", Name = "Radar Lock", Side = "stick", Prefs = ["stick.button.thumb.*"], Frequency = "frequent" };
            request.Functions["gearin"] = new SetupFunction { Id = "gearin", Name = "GearIn", Side = "panel", Prefs = ["panel.lever.gear.up"], PairGroup = "gear", PairRole = "in", Labels = ["GEAR"] };
            request.Functions["gearout"] = new SetupFunction { Id = "gearout", Name = "GearOut", Side = "panel", Prefs = ["panel.lever.gear.down"], PairGroup = "gear", PairRole = "out", Labels = ["GEAR"] };
            foreach (string plane in new[] { "F-16C_50", "FA-18C_hornet", "A-10C II" })
                request.Aircraft[plane] = new SetupAircraft { Key = plane, Game = "DCS", Type = "fixed_wing" };
            request.Aircraft["StarCitizen"] = new SetupAircraft { Key = "StarCitizen", Game = "StarCitizen", Type = "spaceship" };
            foreach (string function in request.Functions.Keys)
            {
                foreach (SetupAircraft plane in request.Aircraft.Values)
                    request.Commands.Add(new SetupCommand { Function = function, Game = plane.Game, Aircraft = plane.Key, Action = function + "_" + plane.Key, Status = "confirmed" });
            }
            request.Options = new SetupOptions
            {
                Games = list[list.IndexOf("--games") + 1].Split(',').ToList(),
                Priorities = list.Contains("--priorities") ? list[list.IndexOf("--priorities") + 1].Split('>').Select(t => t.Split('|').ToList()).ToList() : [],
                Modifiers = int.Parse(list[list.IndexOf("--modifiers") + 1]),
                PreferFewer = list.Contains("--prefer-fewer"),
                Realism = double.Parse(list[list.IndexOf("--realism") + 1], System.Globalization.CultureInfo.InvariantCulture)
            };
            return SetupPlanner.Plan(request);
        }
    }
}
