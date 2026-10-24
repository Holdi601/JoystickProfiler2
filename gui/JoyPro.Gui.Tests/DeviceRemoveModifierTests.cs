using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Removing an old device from the profile (a tester removed old "Joystick (Razer Tartarus V2)" entries and the modifier
// of his connected keypad, the same model under another GUID, vanished with all its binds): this view model and the
// real engine on a profile with two instances of one model. Removing the old instance takes only its binds and the
// modifiers on it; the kept instance keeps its binds and its modifier, and a keyboard modifier that only the removed
// device's binds used stays in the Modifier Library as a temporary modifier. The Modifiers page is rendered before and
// after into JOYPRO_GUI_PARITY_PREVIEW (hidden root, never shown).
[SupportedOSPlatform("windows")]
public sealed class DeviceRemoveModifierTests
{
    private const string Sep = "§";
    private const string OldPad = "Joystick (Remove Keypad) {DDDDDDDD-0000-0000-0000-000000000001}";
    private const string KeptPad = "Joystick (Remove Keypad) {DDDDDDDD-0000-0000-0000-000000000002}";
    private const string Stick = "Remove Other Stick {DDDDDDDD-0000-0000-0000-000000000003}";

    [Fact]
    public void RemoveOldInstance_KeepsTheOtherInstancesBindsAndModifiers_AndModifiersOfOtherDevices()
    {
        string engine = EnginePath();
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "device-remove-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "remove.pr0file");
        try
        {
            BuildProfile(engine, folder, profile);
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new IsolatedEngineClient(folder), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = engine,
                    ConfirmHandler = _ => Task.FromResult(true)
                };
                vm.CurrentStage = UiStage.Start;
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1600, 1000));
                vm.ProfilePath = profile;
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.LoadProfileCommand.ExecuteAsync(null)));
                // The Modifiers page refreshes itself when it opens; the removal waits for that (as a user's click would).
                vm.CurrentStage = UiStage.Modifiers;
                WaitIdle(vm, main);
                Assert.Equal(["KbAlt", "KeptShift", "OldShift"], vm.ModifierItems.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                main.Snap("device-remove-modifiers-before.png");

                vm.DeviceRemoveName = OldPad;
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.RemoveDeviceFromProfileCommand.ExecuteAsync(null)));
                WaitIdle(vm, main);
                main.Snap("device-remove-modifiers-after.png");

                ModifierListItem kept = Assert.Single(vm.ModifierItems, m => m.Name == "KeptShift");
                Assert.False(kept.IsPending);
                Assert.Equal(2, kept.UsageCount);
                ModifierListItem keyboard = Assert.Single(vm.ModifierItems, m => m.Name == "KbAlt");
                Assert.True(keyboard.IsPending);
                Assert.Equal("Keyboard", keyboard.PrimaryDevice);
                Assert.DoesNotContain(vm.ModifierItems, m => m.Name == "OldShift");
                Assert.Contains("KbAlt", vm.StatusText);

                // The binds of the kept instance and of the other device are still there.
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.RefreshBindsCommand.ExecuteAsync(null)));
                WaitIdle(vm, main);
                Assert.True(vm.Binds.Single(b => b.RelationName == "Kept Fire").HasBind);
                Assert.True(vm.Binds.Single(b => b.RelationName == "Kept Hat").HasBind);
                Assert.True(vm.Binds.Single(b => b.RelationName == "Stick Fire").HasBind);
                Assert.False(vm.Binds.Single(b => b.RelationName == "Old Fire").HasBind);

                // The kept keyboard modifier goes with the profile (autosave drafts) and comes back when it reopens.
                AvaloniaRenderThread.Wait(vm.FlushAutosaveAsync());

                Assert.Contains("KbAlt", JoyPro.History.ProfileHistory.ReadDrafts(profile));
                MainWindowViewModel reopened = new(new IsolatedEngineClient(folder), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
                reopened.ProfilePath = profile;
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(reopened.LoadProfileCommand.ExecuteAsync(null)));
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(reopened.RefreshModifiersCommand.ExecuteAsync(null)));
                Assert.Contains(reopened.ModifierItems, m => m.Name == "KbAlt" && m.IsPending);
                Assert.Contains(reopened.ModifierItems, m => m.Name == "KeptShift" && !m.IsPending);
            });
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Deleting the only relation that used a keyboard modifier: the relation and its bind go, the modifier stays in the
    // library as a temporary modifier (it is not the user's choice to delete it).
    [Fact]
    public void DeleteRelation_KeepsAModifierThatLostItsLastBind()
    {
        string engine = EnginePath();
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "relation-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "remove.pr0file");
        try
        {
            BuildProfile(engine, folder, profile);
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new IsolatedEngineClient(folder), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
                {
                    EnginePath = engine,
                    ConfirmHandler = _ => Task.FromResult(true)
                };
                vm.CurrentStage = UiStage.Start;
                using HiddenWindow main = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                vm.ProfilePath = profile;
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.LoadProfileCommand.ExecuteAsync(null)));
                WaitIdle(vm, main);
                vm.SelectedRelation = vm.Relations.Single(r => r.Name == "Old Kb");
                AvaloniaRenderThread.Wait(GuiParityRenderTests.Done(vm.DeleteSelectedRelationCommand.ExecuteAsync(null)));
                WaitIdle(vm, main);
                Assert.DoesNotContain(vm.Relations, r => r.Name == "Old Kb");
                Assert.Contains(vm.ModifierItems, m => m.Name == "KbAlt" && m.IsPending && m.PrimaryDevice == "Keyboard");
                Assert.Contains(vm.ModifierItems, m => m.Name == "KeptShift" && !m.IsPending);
                Assert.Contains(vm.ModifierItems, m => m.Name == "OldShift" && !m.IsPending);
            });
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Two instances of one model (an old GUID and the one in use), another device, a keyboard modifier only on a bind
    // of the old instance and a modifier of the old instance on the other device's bind.
    private static void BuildProfile(string engine, string folder, string profile)
    {
        int node = 0;
        foreach ((string name, string device, string input) in new[]
                 {
                     ("Old Fire", OldPad, "JOY_BTN1"), ("Old Kb", OldPad, "JOY_BTN3"),
                     ("Kept Fire", KeptPad, "JOY_BTN1"), ("Kept Hat", KeptPad, "JOY_BTN2"), ("Stick Fire", Stick, "JOY_BTN1")
                 })
        {
            node++;
            Run(engine, folder, "--profile", profile, "--remap-missing-devices", "false", "relations", "add", name, "--button",
                "--node", "DCS:rm_" + node + ":F-16C_50", "--device", device, "--input", input);
        }
        foreach ((string relation, string modifier) in new[]
                 {
                     ("Kept Fire", "KeptShift" + Sep + KeptPad + Sep + "JOY_BTN20" + Sep + "False"),
                     ("Kept Hat", "KeptShift" + Sep + KeptPad + Sep + "JOY_BTN20" + Sep + "False"),
                     ("Old Kb", "KbAlt" + Sep + "Keyboard" + Sep + "LeftAlt" + Sep + "True"),
                     ("Stick Fire", "OldShift" + Sep + OldPad + Sep + "JOY_BTN5" + Sep + "False")
                 })
            Run(engine, folder, "--profile", profile, "--remap-missing-devices", "false", "binds", "set", relation, "--modifier", modifier);
    }

    // Pumps the UI thread until no engine command has run for a moment (the page's own refreshes included).
    private static void WaitIdle(MainWindowViewModel vm, HiddenWindow main)
    {
        int quiet = 0;
        for (int i = 0; i < 600 && quiet < 5; i++)
        {
            main.Settle();
            quiet = vm.IsBusy ? 0 : quiet + 1;
            Thread.Sleep(20);
        }
        Assert.False(vm.IsBusy, "the view model is still busy");
    }

    internal static string EnginePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JoyPro.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string engine = Path.Combine(dir!.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        Assert.True(File.Exists(engine), "Build the engine first: " + engine);
        return engine;
    }

    private static void Run(string engine, string folder, params string[] args)
    {
        ProcessStartInfo info = new(engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(engine)!
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        foreach (KeyValuePair<string, string> pair in IsolatedEngineClient.Environment(folder))
            info.Environment[pair.Key] = pair.Value;
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120000), "engine " + string.Join(" ", args) + " did not finish.");
        Assert.True(process.ExitCode == 0, "engine " + string.Join(" ", args) + ": " + stdout + stderr.Result);
    }

    // The real engine with its data, logs and devices inside the test folder (never the user's).
    private sealed class IsolatedEngineClient(string folder) : IEngineCliClient
    {
        private readonly EngineCliClient inner = new();

        public static Dictionary<string, string> Environment(string folder)
        {
            string devices = Path.Combine(folder, "devices.tsv");
            if (!File.Exists(devices))
                File.WriteAllText(devices, string.Empty);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["JOYPRO_DATA_DIR"] = Path.Combine(folder, "data"),
                ["JOYPRO_LOG_DIR"] = Path.Combine(folder, "logs"),
                ["JOYPRO_INSTALL_DETECTION"] = "off",
                ["JOYPRO_NO_INTERNET_PICTURES"] = "1",
                ["JOYPRO_FAKE_DEVICES"] = devices
            };
        }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            RunStreamingAsync(engineExePath, arguments, new EngineRunOptions(), cancellationToken);

        public Task<EngineCommandResult> RunStreamingAsync(string engineExePath, IReadOnlyList<string> arguments, EngineRunOptions options, CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
            if (options?.Environment != null)
                foreach (KeyValuePair<string, string> pair in options.Environment)
                    environment[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, string> pair in Environment(folder))
                environment[pair.Key] = pair.Value;
            return inner.RunStreamingAsync(engineExePath, arguments, new EngineRunOptions { StandardErrorLine = options?.StandardErrorLine, Environment = environment }, cancellationToken);
        }
    }
}
