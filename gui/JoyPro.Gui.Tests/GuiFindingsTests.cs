using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Setup;
using Xunit;

namespace JoyPro.Gui.Tests;

// The GUI findings of the wiki pass of 2026-11-01: the cheat sheets and the setup wizard work in another language
// (state and JSON names, never translated texts), "Keep the UI layer's default binds" has its handler, "Skip devices
// without a picture" also skips a ticked device, "Use this layout" reaches the setup wizard, "Manage" opens Settings >
// Devices, Replace device's Move binds asks first and keeps a copy of the profile.
[SupportedOSPlatform("windows")]
public sealed class GuiFindingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "gui-findings-" + Guid.NewGuid().ToString("N"));

    public GuiFindingsTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
        }
    }

    // ---- cheat sheets ----------------------------------------------------------------------------------------------

    // In German the PNG export stopped after saving the layout editor's template: it compared the status line with the
    // English "Layout template saved.".
    [Fact]
    public async Task CheatSheets_InGerman_GoOnAfterTheTemplateWasSaved()
    {
        ScriptedEngine engine = new();
        MainWindowViewModel vm = CreateVm(engine);
        string source = Path.Combine(folder, "stick.layout");
        File.WriteAllText(source, "{}");
        vm.LayoutEditorSourcePath = source;
        vm.LayoutEditorOutputPath = Path.Combine(folder, "stick-edited.layout");
        try
        {
            Loc.SetLanguage("de");
            await vm.RenderLayoutCommand.ExecuteAsync(null);
        }
        finally
        {
            Loc.SetLanguage("en");
        }

        Assert.Contains(engine.Calls, c => Has(c, "layout", "template-save"));
        Assert.Contains(engine.Calls, c => Has(c, "layout", "render"));
    }

    // "Skip devices without a picture": a ticked device without one is left out and the others are made (before, the
    // ticked device stopped the whole export with a warning).
    [Fact]
    public async Task CheatSheets_SkipDevicesWithoutAPicture_AlsoSkipsATickedDevice()
    {
        ScriptedEngine engine = new();
        MainWindowViewModel vm = CreateVm(engine);
        string layout = Path.Combine(folder, "stick.layout");
        File.WriteAllText(layout, "{}");
        vm.LayoutSuggestions.Add(new LayoutSuggestionItem { Device = "Smoke Stick", LayoutPath = layout });
        vm.LayoutDevicesCsv = "Smoke Stick,Smoke Pedals";
        string? warning = null;
        vm.WarningRequested += w => warning = w;

        vm.LayoutSkipMissing = true;
        await vm.RenderLayoutCommand.ExecuteAsync(null);
        string[] render = engine.Calls.Single(c => Has(c, "layout", "render"));
        Assert.True(Has(render, "--device", "Smoke Stick"));
        Assert.False(Has(render, "--device", "Smoke Pedals"));
        Assert.Null(warning);

        // Off: the device without a picture stops the export, as before.
        engine.Calls.Clear();
        vm.LayoutSkipMissing = false;
        await vm.RenderLayoutCommand.ExecuteAsync(null);
        Assert.DoesNotContain(engine.Calls, c => Has(c, "layout", "render"));
        Assert.NotNull(warning);
    }

    // ---- setup wizard ----------------------------------------------------------------------------------------------

    // In German the wizard read the engine's JSON with translated field names ("Gerät" for "Device"): every device and
    // aircraft came out empty.
    [Fact]
    public async Task SetupWizard_InGerman_ReadsTheEnginesDevicesAndAircraft()
    {
        WizardEngine engine = new();
        SetupWizardViewModel wizard = new(engine.RunAsync, [new SetupGameChoice("DCS", "DCS World", true)], workFolder: Path.Combine(folder, "wizard"));
        try
        {
            Loc.SetLanguage("de");
            await wizard.NextCommand.ExecuteAsync(null);
            SetupDeviceItem stick = Assert.Single(wizard.Devices);
            Assert.Equal(WizardEngine.Stick, stick.Device.Device);
            Assert.Equal("Gladiator NXT", stick.Device.Model);
            Assert.Equal(SetupRoles.MainStick, stick.SelectedRole!.Id);

            await wizard.NextCommand.ExecuteAsync(null);
            Assert.True(wizard.IsPrioritiesStep);
            Assert.Contains(wizard.Aircraft, a => a.Key == "F-16C_50" && a.Game == "DCS");
        }
        finally
        {
            Loc.SetLanguage("en");
        }
    }

    // "Use this layout" on the Cockpits page opens the setup wizard with that variant: its aircraft comes first and the
    // plan reads that variant's layout (engine cockpits get <aircraft> <variant> --json) for it.
    [Fact]
    public async Task UseThisLayout_GoesToTheSetupWizard()
    {
        CockpitLayoutRequest request = new("f-16", "f-16c-block-50", "F-16C", "Block 50", ["DCS:F-16C_50"], ["cockpits", "get", "f-16", "f-16c-block-50", "--json"]);

        // The main window: a new wizard takes the pick when it is made.
        MainWindowViewModel vm = CreateVm(new ScriptedEngine());
        int opened = 0;
        vm.SetupWizardRequested += () => opened++;
        vm.UseCockpitLayout(request);
        Assert.Equal(1, opened);
        Assert.NotNull(vm.SetupWizard);
        Assert.Same(request, vm.SetupWizard!.CockpitLayout);
        Assert.Null(vm.PendingCockpitLayout);

        // The wizard: the aircraft first, the picked variant read for the plan.
        WizardEngine engine = new();
        SetupWizardViewModel wizard = new(engine.RunAsync, [new SetupGameChoice("DCS", "DCS World", true)], workFolder: Path.Combine(folder, "wizard"));
        wizard.UseCockpitLayout(request);
        Assert.True(wizard.HasCockpitLayout);
        Assert.Contains("Block 50", wizard.CockpitLayoutText);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal("F-16C_50", wizard.Tiers[0].Aircraft.Single().Key);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Contains(engine.Calls, c => c.SequenceEqual(["cockpits", "get", "f-16", "f-16c-block-50", "--json"]));
        Assert.DoesNotContain(engine.Calls, c => c.SequenceEqual(["cockpits", "get", "F-16C_50", "--json"]));
    }

    // ---- Settings --------------------------------------------------------------------------------------------------

    // "Keep the UI layer's default binds" had no handler: a change while stripping was on did nothing. Now the input
    // files are made again with the new choice (restore, then strip with --skip-ui-layer).
    [Fact]
    public async Task DcsDefaults_KeepTheUiLayer_WhileStrippingIsOn_StripsAgainWithTheNewChoice()
    {
        DcsEngine engine = new();
        MainWindowViewModel vm = CreateVm(engine);
        vm.DcsStripDefaults = true;
        await WaitUntilAsync(() => engine.Calls.Any(c => Has(c, "strip-defaults", "apply")) && !vm.IsBusy);
        Assert.True(vm.DcsStripDefaults);
        Assert.False(vm.DcsStripSkipUiLayer);
        engine.Calls.Clear();

        vm.DcsStripSkipUiLayer = true;
        await WaitUntilAsync(() => engine.Calls.Any(c => Has(c, "strip-defaults", "apply")) && !vm.IsBusy);
        int restore = engine.Calls.FindIndex(c => Has(c, "strip-defaults", "restore"));
        int apply = engine.Calls.FindIndex(c => Has(c, "strip-defaults", "apply"));
        Assert.True(restore >= 0 && restore < apply);
        Assert.True(Has(engine.Calls[apply], "--skip-ui-layer", "true"));
        Assert.True(vm.DcsStripDefaults);
        Assert.True(vm.DcsStripSkipUiLayer);

        // Stripping off: the choice stays for the next strip, nothing runs.
        vm.DcsStripDefaults = false;
        await WaitUntilAsync(() => !vm.DcsStripDefaults && !vm.IsBusy);
        engine.Calls.Clear();
        vm.DcsStripSkipUiLayer = false;
        Assert.Empty(engine.Calls);
        Assert.False(vm.DcsStripSkipUiLayer);
    }

    // "Manage" on the Start page's device card opens Settings with the Devices section, not just Settings.
    [Fact]
    public void Manage_OpensSettingsDevices()
    {
        string axaml = File.ReadAllText(Path.Combine(LocalizationTests.GuiFolder(), "Views", "MainWindow.axaml"));
        string manage = axaml.Split('\n').Single(l => l.Contains("Content=\"{l:T 'Manage'}\"", StringComparison.Ordinal));
        Assert.Contains("Command=\"{Binding OpenSettingsSectionCommand}\"", manage);
        Assert.Contains("CommandParameter=\"devices\"", manage);

        MainWindowViewModel vm = CreateVm(new ScriptedEngine());
        SettingsSectionItem devices = vm.SettingsSections.Single(s => s.Id == "devices");
        devices.IsExpanded = false;
        vm.OpenSettingsSectionCommand.Execute("devices");
        Assert.Equal(UiStage.Settings, vm.CurrentStage);
        Assert.True(devices.IsExpanded);
    }

    // ---- Replace device --------------------------------------------------------------------------------------------

    // Move binds asks first and keeps a copy of the profile next to it, as Exchange Device and Remove do.
    [Fact]
    public async Task ReplaceDevice_MoveBinds_AsksFirst_AndKeepsACopyOfTheProfile()
    {
        ScriptedEngine engine = new()
        {
            Answer = args => Has(args, "devices", "replace")
                ? (0, Has(args, "--dry-run") ? ReplacePlan : "{\"MovedBinds\":1,\"KeptOnOld\":0,\"RemovedTargetBinds\":0}")
                : (0, "{}")
        };
        MainWindowViewModel vm = CreateVm(engine);
        File.WriteAllText(vm.ProfilePath, "profile");
        vm.ReplaceOldDevice = "Old Stick {AAAA}";
        vm.ReplaceNewDevice = "New Stick {BBBB}";
        await vm.PreviewReplaceDeviceCommand.ExecuteAsync(null);
        Assert.True(vm.CanApplyReplace);

        List<ConfirmRequest> asked = [];
        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(false); };
        Assert.False(await vm.ApplyReplaceDeviceAsync());
        Assert.Single(asked);
        Assert.Contains("copy of your profile", asked[0].Message);
        Assert.DoesNotContain(engine.Calls, c => Has(c, "devices", "replace") && !Has(c, "--dry-run"));
        Assert.Empty(ProfileCopies(vm.ProfilePath));

        vm.ConfirmHandler = r => { asked.Add(r); return Task.FromResult(true); };
        Assert.True(await vm.ApplyReplaceDeviceAsync());
        Assert.Contains(engine.Calls, c => Has(c, "devices", "replace") && !Has(c, "--dry-run"));
        Assert.Single(ProfileCopies(vm.ProfilePath));
    }

    private const string ReplacePlan =
        "{\"Mode\":\"same\",\"ModeReason\":\"same product\",\"OldDevice\":\"Old Stick {AAAA}\",\"NewDevice\":\"New Stick {BBBB}\",\"OldConnected\":false," +
        "\"OldModel\":\"Stick\",\"NewModel\":\"Stick\",\"NewInputs\":[\"JOY_BTN1\"],\"Mappings\":[{\"OldInput\":\"JOY_BTN1\",\"NewInput\":\"JOY_BTN1\",\"Axis\":false," +
        "\"Method\":\"same\",\"Relations\":[\"Fire\"]}]}";

    private static string[] ProfileCopies(string profile) =>
        Directory.GetFiles(Path.GetDirectoryName(profile)!, Path.GetFileNameWithoutExtension(profile) + ".before-device-replace-*");

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private MainWindowViewModel CreateVm(IEngineCliClient engine) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(folder, "findings.pr0file")
        };

    internal static bool Has(IReadOnlyList<string> args, params string[] sequence) => VirtualJoystickTests.Seq(args, sequence);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "Timed out.");
    }

    // An engine that answers every command with "{}" unless Answer says otherwise.
    private class ScriptedEngine : IEngineCliClient
    {
        public List<string[]> Calls { get; } = [];

        public Func<string[], (int Code, string Output)>? Answer { get; init; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string[] args = arguments.ToArray();
            lock (Calls)
                Calls.Add(args);
            (int code, string output) = Respond(args);
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = code,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = code == 0 ? string.Empty : "failed",
                Arguments = args
            });
        }

        protected virtual (int Code, string Output) Respond(string[] args) => Answer?.Invoke(args) ?? (0, "{}");
    }

    // dcs strip-defaults apply / restore as the engine answers them (the state it keeps).
    private sealed class DcsEngine : ScriptedEngine
    {
        private bool strip;
        private bool skipUi;

        protected override (int Code, string Output) Respond(string[] args)
        {
            if (Has(args, "strip-defaults", "apply"))
            {
                strip = true;
                int at = Array.IndexOf(args, "--skip-ui-layer");
                skipUi = at >= 0 && args[at + 1] == "true";
            }
            else if (Has(args, "strip-defaults", "restore"))
                strip = false;
            else
                return (0, "{}");
            return (0, "{\"ProfileFiles\":40,\"AlreadyPatched\":" + (strip ? 40 : 0) + ",\"StripEnabled\":" + (strip ? "true" : "false") +
                       ",\"SkipUiLayer\":" + (skipUi ? "true" : "false") + "}");
        }
    }

    // engine setup devices / aircraft as the engine answers them (one stick, two DCS aircraft); the plan fails.
    private sealed class WizardEngine
    {
        public const string Stick = "VKB Gladiator NXT {11111111-0000-0000-0000-000000000001}";

        public List<string[]> Calls { get; } = [];

        public Task<EngineCommandResult?> RunAsync(IReadOnlyList<string> args, bool includeProfile, bool mutatesProfile, string busyLabel, bool quiet)
        {
            string[] a = args.ToArray();
            Calls.Add(a);
            string output = "{}";
            int code = 0;
            if (Has(a, "setup", "devices"))
                output = "{\"Devices\":[{\"Device\":\"" + Stick + "\",\"Model\":\"Gladiator NXT\",\"Kind\":\"stick\",\"Hand\":\"right\",\"Connected\":true," +
                         "\"Buttons\":20,\"Hats\":2,\"Axes\":3,\"Inputs\":25,\"Role\":\"" + SetupRoles.MainStick + "\",\"ProposedRole\":\"" + SetupRoles.MainStick + "\"}]}";
            else if (Has(a, "setup", "aircraft"))
                output = "{\"Aircraft\":[{\"Key\":\"A-10C II\",\"Game\":\"DCS\",\"ClassTitle\":\"Fixed wing\"},{\"Key\":\"F-16C_50\",\"Game\":\"DCS\",\"ClassTitle\":\"Fixed wing\"}]}";
            else if (Has(a, "setup", "plan"))
                code = 1;
            return Task.FromResult<EngineCommandResult?>(new EngineCommandResult
            {
                ExitCode = code,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = code == 0 ? string.Empty : "no plan in this test",
                Arguments = a
            });
        }
    }
}
