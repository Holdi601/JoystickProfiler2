using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.Services.Components;

namespace JoyPro.Gui.Tests;

// Before any test: nothing a command does may reach the PC outside the test folders. Commands that open a browser,
// Explorer, an installer (UAC prompt) or the update script start nothing, and problem reports go to a temp folder
// instead of the user's Documents (AllViewModelCommands_ExecuteWithoutThrowing runs every command).
// Telemetry force feedback: JoyPro.Ffb is never started (a real force feedback stick is connected), no game process
// is looked up, and graphs and settings live in a temp folder.
internal static class TestEnvironment
{
    public static string FfbFolder { get; } = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ffb-telemetry");

    // The Run key every AutostartService of the tests gets (in memory).
    public static InMemoryRunKeyStore SharedRunKey { get; } = new();

    [ModuleInitializer]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void Initialize()
    {
        ExternalProcess.Start = _ => null;
        // No picture is downloaded from the internet (manufacturers' product pictures, docs/device-reference.md).
        DeviceImageCache.InternetPicturesEnabled = false;
        DeviceImageCache.InternetPicturesBlocked = true;
        // Game covers: an engine a test starts never downloads one (engine media --no-internet-pictures, also by
        // this variable), docs/export.md#pictures.
        System.Environment.SetEnvironmentVariable("JOYPRO_NO_INTERNET_PICTURES", "1");
        // The update check (at start and "Check for updates") never asks GitHub: a feed file that does not exist reads
        // as "no release". The update tests point it at their own fake feeds (UpdateFeedTests).
        System.Environment.SetEnvironmentVariable(UpdateService.FeedVariable, Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "no-update-feed", "latest.json"));
        // The cockpit picture packs (Cockpits page) are never looked up on GitHub either: an empty folder has no
        // manifest. The pack tests use their own local feeds (CockpitPicturePackTests).
        System.Environment.SetEnvironmentVariable(CockpitPicturePacks.FeedVariable, Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "no-picture-feed"));
        ProblemReporter.FolderOverride = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "problem-reports");
        // The engine the tests start keeps its data (game folders, hidden games, sync records, last profile) in
        // JOYPRO_DATA_DIR: a fresh folder per run, never the user's %LOCALAPPDATA%\JoyPro or Documents\JoyPro.
        // Tests that need their own folder set and restore it themselves.
        string data = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "data-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        System.Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", data);
        // The move of an old %LOCALAPPDATA%\JoyPro into JoyPro2 (JoyProDataFolder) never runs in a test, also not in an
        // engine a test starts or while a test has JOYPRO_DATA_DIR unset: the migration tests call it on temp folders.
        System.Environment.SetEnvironmentVariable(JoyPro.Logging.JoyProDataFolder.NoMigrationVariable, "1");
        JoyPro.Logging.JoyProDataFolder.LocalAppDataOverride = Path.Combine(data, "localappdata");
        // New profiles ("New profile", the first import) get their automatic name here, not in Documents\JoyPro2\profiles;
        // the profile history (<data>\ProfileHistory) is in the data folder above.
        System.Environment.SetEnvironmentVariable("JOYPRO_PROFILES_DIR", Path.Combine(data, "profiles"));
        // The usage recording (docs/usage-statistics.md) of an engine a test starts reads an empty folder instead of this
        // PC's games, Saved Games, Documents and Steam (tests that need sources point it at a fixture copy).
        string usageRoot = Path.Combine(data, "usage-root");
        Directory.CreateDirectory(usageRoot);
        System.Environment.SetEnvironmentVariable("JOYPRO_USAGE_ROOT", usageRoot);
        // "Start with Windows": never the real HKCU\...\CurrentVersion\Run key, also not from a view model with
        // persisted settings.
        AutostartService.StoreFactory = () => SharedRunKey;
        FfbTelemetryServices.Launcher = new NoFfbHostLauncher();
        FfbTelemetryServices.FindGameProcess = _ => null;
        FfbTelemetryServices.GraphFolder = () => Path.Combine(FfbFolder, "Graphs");
        FfbTelemetryServices.ConfigFile = () => Path.Combine(FfbFolder, "telemetry-ffb.json");
        FfbTelemetryServices.ExePath = () => Path.Combine(FfbFolder, "never-started", "JoyPro.Ffb.exe");
        // Virtual joystick: JoyPro.VJoy is never started (it would write to the user's vJoy device 1, which the e2e kit
        // uses), no game is looked up, and an engine a test might start sees a fake vJoy and HidHide (JOYPRO_VJOY_FAKE):
        // the real HidHide configuration and vJoyConfig are never reached.
        VirtualJoystickServices.Launcher = new NoFfbHostLauncher();
        VirtualJoystickServices.FindGameProcess = _ => null;
        VirtualJoystickServices.ProcessPath = _ => null;
        VirtualJoystickServices.ExePath = () => Path.Combine(FfbFolder, "never-started", "JoyPro.VJoy.exe");
        VirtualJoystickServices.DataFolder = () => Path.Combine(data, "VirtualJoystick");
        // Components (vJoy, HidHide, ViGEmBus, .NET Framework): nothing is detected on the real PC, downloaded, checked
        // against a real file or installed. Tests that need specific behaviour set their own fakes and restore these.
        ComponentServices.Probe = ComponentFakes.NothingInstalledProbe;
        ComponentServices.Downloader = ComponentFakes.NoDownloader;
        ComponentServices.SignatureVerifier = ComponentFakes.NoSignatures;
        ComponentServices.Runner = ComponentFakes.NoRunner;
        ComponentServices.DownloadFolder = () => Path.Combine(data, "Components");
        string vjoyFake = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "vjoy-fake-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vjoyFake);
        System.Environment.SetEnvironmentVariable("JOYPRO_VJOY_FAKE", vjoyFake);
    }

    private sealed class NoFfbHostLauncher : IFfbHostLauncher
    {
        public IFfbHostProcess Start(string exePath, IReadOnlyList<string> arguments) =>
            throw new InvalidOperationException("JoyPro.Ffb is never started by the GUI tests.");

        public Task<FfbToolResult> RunToolAsync(string exePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfbToolResult(1, string.Empty, "JoyPro.Ffb is never started by the GUI tests."));
    }
}
