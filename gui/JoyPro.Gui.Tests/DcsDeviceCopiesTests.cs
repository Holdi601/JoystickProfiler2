using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// A DCS import of a device Windows gave new IDs over time made one relation copy per aircraft ("<name>__1", ...):
//   - the DCS import options "Treat devices with the same name as one device" (on) and "Only import connected devices"
//     (off) reach the engine's scan and import (--merge-same-name, --only-connected) and survive a restart;
//   - the check lists such copies as cards (engine relations merge-copies --dry-run) and merges them in one call each
//     (one undo step, history note "Merging copied relations").
// The persistence test writes the test run's ui-settings.json, so the class runs in the TrayMode collection (alone).
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class DcsDeviceCopiesTests
{
    private static MainWindowViewModel NewVm(IEngineCliClient engine, bool persisted = false) => new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: persisted)
    {
        EnginePath = typeof(MainWindowViewModel).Assembly.Location,
        ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dcs-device-copies.pr0file")
    };

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        int at = args.ToList().IndexOf(option);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : string.Empty;
    }

    [Fact]
    public async Task DcsImportOptions_ReachScanAndImport_AndSurviveARestart()
    {
        string settingsFile = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            File.Delete(settingsFile);
            CopiesFakeEngine fake = new();
            MainWindowViewModel vm = NewVm(fake, persisted: true);
            Assert.True(vm.ImportDcsSameNameAsOne);
            Assert.False(vm.ImportDcsOnlyConnected);

            vm.ImportAllDcsPlanes = true;
            await vm.ScanDcsCommand.ExecuteAsync(null);
            IReadOnlyList<string> scan = fake.Calls.Last(c => AxisTuningTests.Seq(c, "dcs", "scan"));
            Assert.Equal("true", ValueAfter(scan, "--merge-same-name"));
            Assert.Equal("false", ValueAfter(scan, "--only-connected"));

            vm.ImportDcsSameNameAsOne = false;
            vm.ImportDcsOnlyConnected = true;
            await vm.ImportDcsCommand.ExecuteAsync(null);
            IReadOnlyList<string> import = fake.Calls.Last(c => AxisTuningTests.Seq(c, "dcs", "import"));
            Assert.Equal("false", ValueAfter(import, "--merge-same-name"));
            Assert.Equal("true", ValueAfter(import, "--only-connected"));

            // A restart keeps both choices.
            MainWindowViewModel second = NewVm(new CopiesFakeEngine(), persisted: true);
            Assert.False(second.ImportDcsSameNameAsOne);
            Assert.True(second.ImportDcsOnlyConnected);
            second.ImportDcsSameNameAsOne = true;
            second.ImportDcsOnlyConnected = false;
            MainWindowViewModel third = NewVm(new CopiesFakeEngine(), persisted: true);
            Assert.True(third.ImportDcsSameNameAsOne);
            Assert.False(third.ImportDcsOnlyConnected);
        }
        finally
        {
            if (File.Exists(settingsFile))
                File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task Check_ListsCopiedRelations_AndMergesThemInOneStep()
    {
        CopiesFakeEngine fake = new();
        MainWindowViewModel vm = NewVm(fake);

        await vm.ValidateProfileCommand.ExecuteAsync(null);

        Assert.Contains(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "merge-copies", "--dry-run"));
        CheckIssueSection copies = Assert.Single(vm.CheckSections, s => s.Kind == "copies");
        Assert.Equal("Copied relations (2)", copies.CountText);
        CheckIssueCard card = copies.Cards.Single(c => c.RelationsSort == "Razer BTN12 - PREV KNEEBOARD PAGE");
        Assert.Equal("3 relations are copies of 'Razer BTN12 - PREV KNEEBOARD PAGE' with the same input", card.Headline);
        Assert.Contains("keeps every aircraft (4)", card.Details);
        Assert.DoesNotContain("{40509BC0", card.Details);
        Assert.Equal(["'Razer BTN12 - PREV KNEEBOARD PAGE__1'", "'Razer BTN12 - PREV KNEEBOARD PAGE__2'", "'Razer BTN12 - PREV KNEEBOARD PAGE__3'"], card.ActionLines);
        Assert.True(card.HasMergeCopies);
        Assert.Equal("Merge into one", card.MergeCopiesText);
        Assert.True(card.HasMergeAllCopies);
        Assert.Equal("Merge all copied relations (4)", card.MergeAllCopiesText);
        Assert.Contains("4 copied relations", vm.CheckSummaryText);
        // Nothing changed by looking.
        Assert.DoesNotContain(fake.Calls, c => AxisTuningTests.Seq(c, "relations", "merge-copies") && !c.Contains("--dry-run"));

        // Merge one family: one engine call (one profile version) with its history note; checked again, its card is gone.
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.MergeCopiesCommand!).ExecuteAsync(null);
        IReadOnlyList<string> merge = fake.Calls.Single(c => AxisTuningTests.Seq(c, "relations", "merge-copies", "--name", "Razer BTN12 - PREV KNEEBOARD PAGE"));
        Assert.DoesNotContain("--dry-run", merge);
        Assert.Equal("Merging copied relations", ValueAfter(merge, "--history-note"));
        Assert.Equal("Merged 3 copies into 'Razer BTN12 - PREV KNEEBOARD PAGE'. Ctrl+Z undoes it.", vm.StatusText);
        copies = Assert.Single(vm.CheckSections, s => s.Kind == "copies");
        CheckIssueCard left = Assert.Single(copies.Cards);
        Assert.False(left.HasMergeAllCopies);

        // Search finds a card by a copy's name; the kind filter "errors" hides the section.
        vm.CheckSearchText = "EJECT__1";
        Assert.Single(vm.CheckSections, s => s.Kind == "copies");
        vm.CheckSearchText = string.Empty;
        vm.SelectedCheckKind = vm.CheckKindChoices.Single(c => c.Rule == "errors");
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "copies");
        vm.SelectedCheckKind = vm.CheckKindChoices[0];

        // The last family.
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)left.MergeCopiesCommand!).ExecuteAsync(null);
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "copies");
    }

    [Fact]
    public async Task MergeAll_IsOneCall_WithoutAName()
    {
        CopiesFakeEngine fake = new();
        MainWindowViewModel vm = NewVm(fake);
        await vm.ValidateProfileCommand.ExecuteAsync(null);
        CheckIssueCard card = vm.CheckSections.Single(s => s.Kind == "copies").Cards.First();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)card.MergeAllCopiesCommand!).ExecuteAsync(null);
        IReadOnlyList<string> merge = fake.Calls.Single(c => AxisTuningTests.Seq(c, "relations", "merge-copies") && !c.Contains("--dry-run"));
        Assert.DoesNotContain("--name", merge);
        Assert.Equal("Merging copied relations", ValueAfter(merge, "--history-note"));
        Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "copies");
    }

    // profile validate, relations merge-copies (dry run: two families until merged), the DCS scan and import.
    private sealed class CopiesFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        private readonly HashSet<string> merged = new(StringComparer.OrdinalIgnoreCase);

        private const string Device = "Joystick (Razer Tartarus V2) {40509BC0-8DCE-11f1-8015-444553540000}";

        private static string Family(string relation, int copies, string input, string aircraft) =>
            "{\"Relation\":\"" + relation + "\",\"Copies\":[" + string.Join(",", Enumerable.Range(1, copies).Select(n => "\"" + relation + "__" + n + "\"")) + "]," +
            "\"Device\":\"" + Device + "\",\"Input\":\"" + input + "\",\"IsAxis\":false,\"Modifiers\":[],\"Aircraft\":[" + aircraft + "],\"NewAircraft\":1}";

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output = string.Empty;
            if (AxisTuningTests.Seq(arguments, "profile", "validate"))
            {
                output = "Profile Validation\nPassed: True\nFail On Warnings: False\nWarnings: 0\nErrors: 0\n";
            }
            else if (AxisTuningTests.Seq(arguments, "relations", "merge-copies"))
            {
                bool dry = arguments.Contains("--dry-run");
                if (!dry)
                {
                    int at = arguments.ToList().IndexOf("--name");
                    if (at >= 0)
                        merged.Add(arguments[at + 1]);
                    else
                        merged.UnionWith(["Razer BTN12 - PREV KNEEBOARD PAGE", "Razer BTN07 - EJECT"]);
                }
                List<string> families = [];
                if (!merged.Contains("Razer BTN12 - PREV KNEEBOARD PAGE"))
                    families.Add(Family("Razer BTN12 - PREV KNEEBOARD PAGE", 3, "JOY_BTN12", "\"DCS|F-16C_50\",\"DCS|FA-18C_hornet\",\"DCS|A-10C_2\",\"DCS|AH-64D_BLK_II_PLT\""));
                if (!merged.Contains("Razer BTN07 - EJECT"))
                    families.Add(Family("Razer BTN07 - EJECT", 1, "JOY_BTN7", "\"DCS|F-16C_50\",\"DCS|F-14\""));
                output = "{\"DryRun\":" + (dry ? "true" : "false") + ",\"Count\":" + families.Count + ",\"Families\":[" + (dry ? string.Join(",", families) : string.Empty) + "]}";
            }
            else if (AxisTuningTests.Seq(arguments, "relations", "list"))
            {
                output = "{\"Relations\":[]}";
            }
            else if (AxisTuningTests.Seq(arguments, "binds", "list"))
            {
                output = "{\"Binds\":[]}";
            }
            else if (AxisTuningTests.Seq(arguments, "relations", "duplicates"))
            {
                output = "{\"Count\":0,\"Duplicates\":[],\"Groups\":[]}";
            }
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, Arguments = arguments.ToArray() });
        }
    }
}
