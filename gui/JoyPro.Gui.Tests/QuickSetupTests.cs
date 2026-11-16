using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Help;
using JoyPro.Gui.ViewModels;
using JoyPro.DeviceReference;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The welcome's quick answer ("Just get my buttons working, quickly", docs/ui/help-and-tours.md) leads into the one
// quick path of JoyPro (MainWindowViewModel.AutoSetupStart.cs, the same as the first start's "Done, and give me binds
// quickly" and the Start card): the setup runs by itself up to its plain-words review with "Use these binds" and "Write
// to my game". With a fake engine (IL-2 Great Battles and a WinWing URSA MINOR, or a stick JoyPro does not know); the
// real engine runs in QuickSetupPersonaTests. A stop before the review (no stick) is no dead end: Try again, or the
// usual steps.
[SupportedOSPlatform("windows")]
public sealed class QuickSetupTests
{
    private static readonly string[] JoyProWords = ["relation", "profile", "modifier", "engine", "catalog"];

    [Fact]
    public async Task WelcomeQuickAnswer_LeadsIntoTheQuickPath_UpToItsReview_ThenUseAndWrite()
    {
        QuickFakeEngine engine = new();
        MainWindowViewModel vm = await NewVmAsync(engine);
        vm.InstalledGamesForWelcome = () => vm.GameCards.Where(c => c.Installed).Select(c => c.GameId).ToList();
        vm.OpenWelcome();
        Assert.Equal(TourCatalog.Quick, vm.WelcomeOptions.Single(o => o.IsSelected).Id);
        int opened = 0;
        vm.SetupWizardRequested += () => opened++;
        await vm.StartWelcomeChoiceCommand.ExecuteAsync(null);

        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.Equal(1, opened);
        Assert.False(vm.IsWelcomeOpen);
        Assert.True(wizard.QuickMode);
        Assert.False(wizard.QuickStuck);
        Assert.Equal(["IL2Game"], wizard.ChosenGames);
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.Contains("IL-2", wizard.QuickSummary);
        AssertPlainWords(wizard.QuickSummary);
        Assert.Equal(T("Use these buttons"), wizard.ApplyText);
        Assert.Equal(T("Write to my game"), wizard.ExportText);
        // The engine's steps, in order: the devices, the aircraft of IL-2, the plan.
        List<string> steps = engine.Calls.Select(c => new[] { "setup devices", "setup aircraft", "setup plan" }.FirstOrDefault(k => AxisTuningTests.Seq(c, k.Split(' '))) ?? string.Empty).ToList();
        Assert.True(steps.IndexOf("setup devices") >= 0 && steps.IndexOf("setup devices") < steps.IndexOf("setup aircraft") && steps.IndexOf("setup aircraft") < steps.IndexOf("setup plan"),
            string.Join(" | ", steps));

        await wizard.ApplyCommand.ExecuteAsync(null);
        Assert.True(wizard.Applied, wizard.Status);
        Assert.Contains(engine.Calls, c => AxisTuningTests.Seq(c, "setup", "apply"));
    }

    [Fact]
    public async Task UnknownStick_IsSaidPlainly_AndStillSetUp()
    {
        QuickFakeEngine engine = new() { Generic = true };
        MainWindowViewModel vm = await NewVmAsync(engine);
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        Assert.False(wizard.QuickStuck);
    }

    [Fact]
    public async Task NoStick_IsNoDeadEnd_TryAgain_OrStepByStep()
    {
        QuickFakeEngine engine = new() { NoDevices = true };
        MainWindowViewModel vm = await NewVmAsync(engine);
        await vm.StartQuickSetupAsync();
        SetupWizardViewModel wizard = vm.SetupWizard!;
        Assert.True(wizard.QuickStuck);
        Assert.False(wizard.IsReviewStep);
        AssertPlainWords(wizard.QuickSummary);
        // Plugged in: Try again goes on to the review.
        engine.NoDevices = false;
        await wizard.QuickTryAgainCommand.ExecuteAsync(null);
        Assert.False(wizard.QuickStuck);
        Assert.True(wizard.IsReviewStep, wizard.Status + " / " + wizard.QuickSummary);
        // Or the usual steps, from the first one.
        wizard.QuickStepByStepCommand.Execute(null);
        Assert.False(wizard.QuickMode);
        Assert.True(wizard.IsGamesStep);
    }

    private static void AssertPlainWords(string text)
    {
        foreach (string word in JoyProWords)
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    private static string T(string text) => JoyPro.Gui.Localization.Loc.T(text);

    internal static async Task<MainWindowViewModel> NewVmAsync(QuickFakeEngine engine)
    {
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location
        };
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "quick-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "My buttons.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        await vm.RefreshGameCardsCommand.ExecuteAsync(null);
        return vm;
    }

    // The engine for the persona: IL-2 Great Battles installed, a WinWing URSA MINOR (or a stick JoyPro does not know).
    internal sealed class QuickFakeEngine : IEngineCliClient
    {
        private const string Ursa = "WINWING URSA MINOR Combat Joystick L {5}";
        private readonly NoviceGuiTests.NoviceFakeEngine basics = new();

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool Generic { get; init; }

        public bool NoDevices { get; set; }

        public bool FailExport { get; set; }

        // How long "setup devices" takes (a quick setup still running when it is asked for again).
        public int DevicesDelayMs { get; set; }

        public async Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (DevicesDelayMs > 0 && AxisTuningTests.Seq(arguments, "setup", "devices"))
                await Task.Delay(DevicesDelayMs, cancellationToken);
            return await Answer(engineExePath, arguments, cancellationToken);
        }

        private Task<EngineCommandResult> Answer(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string? output = null;
            int exit = 0;
            List<string> args = arguments.ToList();
            if (Seq(args, "games", "installs") || Seq(args, "games", "install"))
                output = "{\"Games\":[{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Installed\":true,\"InstallPath\":\"D:\\\\IL-2 Sturmovik Battle of Stalingrad\",\"Source\":\"test\",\"ConfigPaths\":[]}," +
                         "{\"GameId\":\"DCS\",\"Name\":\"DCS World\",\"Installed\":false,\"InstallPath\":\"\",\"Source\":\"\",\"ConfigPaths\":[]}]}";
            else if (Seq(args, "games", "integrations"))
                output = "{\"Integrations\":[]}";
            else if (Seq(args, "setup", "games"))
                output = "{\"Games\":[{\"Game\":\"IL2Game\",\"Wizard\":true,\"Status\":\"empty\",\"GameBinds\":0,\"ProfileBinds\":0}]}";
            else if (Seq(args, "setup", "devices"))
                output = MiniJson.Write(new Dictionary<string, object> { { "Devices", Devices().Select(d => (object)SetupPlanJson.DeviceObject(d)).ToList() } }, false);
            else if (Seq(args, "setup", "aircraft"))
                output = "{\"Aircraft\":[{\"Key\":\"Bf 109 G-6\",\"Game\":\"IL2Game\",\"ClassTitle\":\"Fixed wing\"},{\"Key\":\"P-51D-15\",\"Game\":\"IL2Game\",\"ClassTitle\":\"Fixed wing\"}]}";
            else if (Seq(args, "setup", "plan"))
            {
                SetupPlan plan = Plan(args);
                File.WriteAllText(args[args.IndexOf("--out-plan") + 1], SetupPlanJson.Write(plan, true), new UTF8Encoding(false));
                output = SetupPlanJson.Write(plan, false);
            }
            else if (Seq(args, "setup", "apply"))
                output = "{\"Relations\":3,\"Binds\":3}";
            else if (Seq(args, "setup", "render") || Seq(args, "cockpits", "get"))
                output = "{}";
            else if (Seq(args, "profile", "games"))
                output = "{\"Games\":[{\"GameId\":\"IL2Game\",\"Name\":\"IL-2 Sturmovik: Great Battles\",\"Exporter\":\"il2\",\"Relations\":3,\"Bound\":3,\"Installed\":true}]}";
            else if (Seq(args, "il2", "export"))
            {
                exit = FailExport ? 1 : 0;
                output = FailExport ? string.Empty : "Exported 3 binds.";
            }
            else if (Seq(args, "profile", "game-state"))
                output = "{}";
            if (output == null)
                return basics.RunAsync(engineExePath, arguments, cancellationToken);
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = exit, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output,
                StandardError = exit == 0 ? string.Empty : "The game's control folder was not found.", Arguments = arguments.ToArray()
            });
        }

        private List<SetupDevice> Devices()
        {
            if (NoDevices)
                return [new SetupDevice { Device = "Keyboard", Model = "Keyboard", Kind = "keyboard", Buttons = 100 }];
            List<SetupDevice> devices =
            [
                Generic
                    ? new SetupDevice { Device = "Generic USB Joystick {7}", Model = "Generic USB Joystick", Kind = "stick", Generic = true, Hand = "right", Buttons = 12, Hats = 1, Axes = 4 }
                    : new SetupDevice { Device = Ursa, Model = "WINWING URSA MINOR Combat Joystick", ModelId = "winwing-ursa-minor", Kind = "hotas", Hand = "right", Buttons = 24, Hats = 2, Axes = 5 }
            ];
            SetupRoles.Propose(devices);
            return devices;
        }

        private SetupPlan Plan(List<string> args)
        {
            SetupDevice device = Devices()[0];
            SetupRequest request = new() { Devices = [device] };
            request.Inputs.Add(new SetupInput { Device = device.Device, Input = "JOY_X", Control = "stick.axis.roll", IsAxis = true });
            request.Inputs.Add(new SetupInput { Device = device.Device, Input = "JOY_Y", Control = "stick.axis.pitch", IsAxis = true });
            request.Inputs.Add(new SetupInput { Device = device.Device, Input = "JOY_BTN1", Control = "stick.trigger.stage1" });
            request.Functions["pitch"] = new SetupFunction { Id = "pitch", Name = "Pitch", IsAxis = true, Side = "stick", Prefs = ["stick.axis.pitch"], Frequency = "constant" };
            request.Functions["fire"] = new SetupFunction { Id = "fire", Name = "Fire guns", Side = "stick", Prefs = ["stick.trigger.stage1"], Frequency = "frequent" };
            foreach (string plane in new[] { "Bf 109 G-6", "P-51D-15" })
            {
                request.Aircraft[plane] = new SetupAircraft { Key = plane, Game = "IL2Game", Type = "fixed_wing" };
                foreach (string function in request.Functions.Keys)
                    request.Commands.Add(new SetupCommand { Function = function, Game = "IL2Game", Aircraft = plane, Action = function + "_" + plane, Status = "confirmed" });
            }
            request.Options = new SetupOptions
            {
                Games = args[args.IndexOf("--games") + 1].Split(',').ToList(),
                Modifiers = int.Parse(args[args.IndexOf("--modifiers") + 1]),
                Realism = double.Parse(args[args.IndexOf("--realism") + 1], System.Globalization.CultureInfo.InvariantCulture)
            };
            return SetupPlanner.Plan(request);
        }

        private static bool Seq(IReadOnlyList<string> args, string a, string b) => AxisTuningTests.Seq(args, a, b);
    }
}
