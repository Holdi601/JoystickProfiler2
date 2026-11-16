using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Kneeboard (Layout page, MainWindowViewModel.Kneeboard.cs, engine layout kneeboard): the arguments the GUI hands the
// engine (DCS and DCS.openbeta next to the Export page's DCS folder, a folder of choice, the ticked aircraft, name
// ending, page height, one page with all devices), the checks before the engine runs, and with the real engine: the
// pages land in both stand-in Saved Games folders under the DCS folder names, a second export replaces only JoyPro's
// pages, pictures of others stay byte for byte, and an unknown aircraft gets its own name. Everything happens in the
// test sandbox (TestEnvironment: JOYPRO_TEST_SANDBOX, JOYPRO_DATA_DIR; RealFolderGuard checks the real folders).
[SupportedOSPlatform("windows")]
public sealed class KneeboardExportTests : IDisposable
{
    private readonly string folder = Path.Combine(TestEnvironment.SandboxRoot, "kneeboard-" + Guid.NewGuid().ToString("N"));

    public KneeboardExportTests()
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

    private MainWindowViewModel CreateVm(IEngineCliClient engine, string? enginePath = null) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            EnginePath = enginePath ?? typeof(MainWindowViewModel).Assembly.Location,
            ProfilePath = Path.Combine(folder, "kneeboard.pr0file")
        };

    private static bool Has(IReadOnlyList<string> args, params string[] sequence) => VirtualJoystickTests.Seq(args, sequence);

    private (string Dcs, string Beta) SavedGames()
    {
        string dcs = Path.Combine(folder, "Saved Games", "DCS");
        string beta = Path.Combine(folder, "Saved Games", "DCS.openbeta");
        Directory.CreateDirectory(Path.Combine(dcs, "Config", "Input"));
        Directory.CreateDirectory(Path.Combine(beta, "Config", "Input"));
        return (dcs, beta);
    }

    [Fact]
    public async Task DcsKneeboard_HandsTheEngineBothSavedGamesFolders_TheOptions_AndShowsTheResult()
    {
        (string dcs, string beta) = SavedGames();
        string page = Path.Combine(dcs, "Kneeboard", "F-16C_50", "JoyPro_F-16C_50_Stick.png");
        ScriptedEngine engine = new()
        {
            Answer = args => Has(args, "layout", "kneeboard")
                ? (0, "Kneeboard Export\nExport Path: " + Path.Combine(dcs, "Kneeboard") + "\nAircraft: 1\n  - F-16C_50 -> F-16C_50\nPictures Written: 1\nWritten Files: 1\n  - " + page + "\n")
                : (0, "{}")
        };
        MainWindowViewModel vm = CreateVm(engine);
        vm.ExportDcsPath = dcs;
        vm.KneeboardPostfix = "_VR";
        vm.KneeboardSize = "1024";
        vm.KneeboardOnePage = true;

        await vm.ExportKneeboardCommand.ExecuteAsync(null);

        string[] call = engine.Calls.Single(c => Has(c, "layout", "kneeboard"));
        Assert.True(Has(call, "--target", "dcs"));
        Assert.True(Has(call, "--saved-games", dcs));
        Assert.True(Has(call, "--saved-games", beta));
        Assert.True(Has(call, "--postfix", "_VR"));
        Assert.True(Has(call, "--size", "1024"));
        Assert.True(Has(call, "--combine", "true"));
        // The list page with the device picture is the default.
        Assert.True(Has(call, "--page", "list"));
        Assert.True(Has(call, "--picture", "top"));
        Assert.DoesNotContain("--plane", call);
        Assert.Equal("Kneeboard pages written: 1 for 1 aircraft. Folder: " + Path.Combine(dcs, "Kneeboard"), vm.CheatSheetStatus);
    }

    [Fact]
    public async Task OnlyTheTickedAircraft_AndAFolderOfChoice_GoToTheEngine()
    {
        ScriptedEngine engine = new();
        MainWindowViewModel vm = CreateVm(engine);
        vm.LayoutPlaneCsv = "FA-18C_hornet";
        vm.KneeboardAllAircraft = false;
        vm.KneeboardToFolder = true;
        Assert.False(vm.KneeboardToDcs);
        string mine = Path.Combine(folder, "My kneeboard");
        vm.KneeboardFolder = mine;

        await vm.ExportKneeboardCommand.ExecuteAsync(null);

        string[] call = engine.Calls.Single(c => Has(c, "layout", "kneeboard"));
        Assert.True(Has(call, "--target", "folder", "--folder", mine));
        Assert.True(Has(call, "--plane", "FA-18C_hornet"));
        Assert.True(Has(call, "--size", "2048"));
        Assert.DoesNotContain("--saved-games", call);

        // Without the device picture; then the cheat sheet picture instead of the list (its own shape in a folder,
        // the 3:4 page for DCS).
        engine.Calls.Clear();
        vm.KneeboardPicture = false;
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.True(Has(engine.Calls.Single(c => Has(c, "layout", "kneeboard")), "--picture", "none"));
        engine.Calls.Clear();
        vm.KneeboardListPage = false;
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        string[] sheet = engine.Calls.Single(c => Has(c, "layout", "kneeboard"));
        Assert.True(Has(sheet, "--page", "original"));
        Assert.DoesNotContain("--picture", sheet);
        engine.Calls.Clear();
        vm.KneeboardToFolder = false;
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.True(Has(engine.Calls.Single(c => Has(c, "layout", "kneeboard")), "--page", "portrait"));
    }

    [Fact]
    public async Task NoFolder_OrNoTickedAircraft_StopsBeforeTheEngine_WithAPlainNote()
    {
        ScriptedEngine engine = new();
        MainWindowViewModel vm = CreateVm(engine);
        vm.KneeboardToFolder = true;
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.Equal("Pick a folder for the kneeboard pages first.", vm.CheatSheetStatus);

        vm.KneeboardToFolder = false;
        vm.KneeboardAllAircraft = false;
        vm.LayoutPlaneCsv = string.Empty;
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.Equal("Tick the aircraft above, or tick All aircraft.", vm.CheatSheetStatus);
        Assert.DoesNotContain(engine.Calls, c => Has(c, "layout", "kneeboard"));

        // A failed engine run says so.
        ScriptedEngine failing = new() { Answer = args => Has(args, "layout", "kneeboard") ? (1, string.Empty) : (0, "{}") };
        MainWindowViewModel other = CreateVm(failing);
        await other.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.Equal("No kneeboard pages were written; see Engine Output for why.", other.CheatSheetStatus);
    }

    [Fact]
    public void DcsFolders_AreTheChosenOne_AndTheDcsFoldersNextToIt()
    {
        (string dcs, string beta) = SavedGames();
        Directory.CreateDirectory(Path.Combine(folder, "Saved Games", "DCS.backup", "Config"));
        Assert.Equal([dcs, beta], MainWindowViewModel.KneeboardDcsFolders(dcs));
        Assert.Equal([beta, dcs], MainWindowViewModel.KneeboardDcsFolders(beta + Path.DirectorySeparatorChar));
        Assert.Empty(MainWindowViewModel.KneeboardDcsFolders(string.Empty));
        Assert.Equal(3, MainWindowViewModel.ParseKneeboardAircraftCount("Kneeboard Export\r\nAircraft: 3\r\n  - a"));
    }

    // With the real engine (the build of this configuration): the pages, the folder names, a second export, the
    // pictures of others, and an unknown aircraft.
    [Fact]
    public async Task RealEngine_WritesOnlyJoyProsPages_IntoBothSavedGamesFolders()
    {
        string? engine = TestEnvironment.EnginePath();
        if (engine == null)
            return;
        (string dcs, string beta) = SavedGames();
        const string Device = "KB Grip {5B5B5B5B-0000-1111-2222-333333333333}";
        string profile = Path.Combine(folder, "kneeboard.pr0file");
        await RunEngineAsync(engine, "--profile", profile, "relations", "add", "Gear", "--button", "--node", "DCS:d3001pnilu3001cd1vd1vpnilvu0:F-16C_50", "--device", Device, "--input", "JOY_BTN1");
        await RunEngineAsync(engine, "--profile", profile, "relations", "add", "Mystery", "--button", "--node", "DCS:d9:KB Mystery Jet", "--device", Device, "--input", "JOY_BTN2");
        await RunEngineAsync(engine, "--profile", profile, "relations", "add", "Pilot", "--button", "--node", "DCS:d8:F-14", "--device", Device, "--input", "JOY_BTN3");
        string layout = Path.Combine(folder, "grip.callouts.json");
        await RunEngineAsync(engine, "layout", "callouts", "--model", "vkb-gunfighter-mcg-pro", "--device", Device, "--save", layout);

        string viper = Path.Combine(dcs, "Kneeboard", "F-16C_50");
        Directory.CreateDirectory(viper);
        string foreign = Path.Combine(viper, "00_startup.png");
        File.WriteAllBytes(foreign, [1, 2, 3, 4, 5]);
        string foreignHash = Hash(foreign);

        MainWindowViewModel vm = CreateVm(new EngineCliClient(), engine);
        vm.ExportDcsPath = dcs;
        vm.LayoutSuggestions.Add(new LayoutSuggestionItem { Device = Device, LayoutPath = layout });
        vm.LayoutDevicesCsv = Device;
        vm.KneeboardSize = "1024";
        await vm.ExportKneeboardCommand.ExecuteAsync(null);

        Assert.StartsWith("Kneeboard pages written: 12 for 3 aircraft.", vm.CheatSheetStatus);
        foreach (string root in new[] { dcs, beta })
        {
            Assert.True(File.Exists(Path.Combine(root, "Kneeboard", "F-16C_50", "JoyPro_F-16C_50_KB Grip.png")), root);
            Assert.True(File.Exists(Path.Combine(root, "Kneeboard", "F-14B", "JoyPro_F-14_KB Grip.png")), root);
            Assert.True(File.Exists(Path.Combine(root, "Kneeboard", "F-14A-95-GR", "JoyPro_F-14_KB Grip.png")), root);
            // Unknown to JoyPro and DCS: the aircraft's own name.
            Assert.True(File.Exists(Path.Combine(root, "Kneeboard", "KB Mystery Jet", "JoyPro_KB Mystery Jet_KB Grip.png")), root);
        }
        Assert.Equal(foreignHash, Hash(foreign));

        // Again: JoyPro's pages are replaced, the picture of someone else stays.
        string ownPage = Path.Combine(viper, "JoyPro_F-16C_50_KB Grip.png");
        string firstHash = Hash(ownPage);
        vm.KneeboardSize = "2048";
        await vm.ExportKneeboardCommand.ExecuteAsync(null);
        Assert.NotEqual(firstHash, Hash(ownPage));
        Assert.Equal(foreignHash, Hash(foreign));
        Assert.Equal(["00_startup.png", "JoyPro_F-16C_50_KB Grip.png"], Directory.GetFiles(viper).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static async Task RunEngineAsync(string engine, params string[] args)
    {
        ProcessStartInfo start = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, string.Join(" ", args) + ": " + output + error);
    }

    private sealed class ScriptedEngine : IEngineCliClient
    {
        public List<string[]> Calls { get; } = [];

        public Func<string[], (int Code, string Output)>? Answer { get; init; }

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string[] args = arguments.ToArray();
            lock (Calls)
                Calls.Add(args);
            (int code, string output) = Answer?.Invoke(args) ?? (0, "{}");
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = code,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = code == 0 ? string.Empty : "failed",
                Arguments = args
            });
        }
    }
}
