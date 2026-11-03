using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// 2.6.2: the repair of profiles an earlier load correction filled up (engine profile auto-added: the notice with the
// list to untick, Take them out as one engine run, Keep them), the check banner's words (the button, the aircraft by
// name and what the command does in each; the buttons say the outcome) and its drop rule (the engine's Drop only, never
// an imported aircraft), and a node's title when its aircraft call the id differently.
[SupportedOSPlatform("windows")]
public sealed class AutoAddCorrectionTests
{
    private const string Helo = "THROTTLE - SLIDER -HELO POWER BOTH";

    private static readonly string[] HeloAdded =
        ["a-10a", "A-10C II", "A-10C", "AV8BNA", "Default", "F-15C", "F-4E-45MC", "F-5E-3", "FA-18C_hornet", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33"];

    [Fact]
    public async Task Repair_NoticeOffersExactlyTheAddedAircraftAndTakesThemOutInOneRun()
    {
        AutoAddFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.CheckAutoAddRepairAsync();

        Assert.True(vm.HasAutoAddRepairNotice);
        string day = DateTime.Parse("2026-11-03T13:26:39.0794492Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        Assert.Equal("JoyPro added 19 aircraft to 2 relations on " + day + " after the command lists changed.", vm.AutoAddRepairTitle);
        Assert.StartsWith("These were not bound by you", vm.AutoAddRepairText);
        Assert.Contains("'" + Helo + "' goes back to", vm.AutoAddRepairText);
        Assert.Equal(19, vm.AutoAddRepairItems.Count);
        Assert.Equal(17, vm.AutoAddRepairItems.Count(i => i.Text.StartsWith("'" + Helo + "'", StringComparison.Ordinal)));
        Assert.All(vm.AutoAddRepairItems, i => Assert.True(i.IsTicked));
        Assert.Equal("Show the list (19 aircraft in 2 relations)", vm.AutoAddRepairListToggleText);

        // Untick one: it is skipped; everything else goes in one engine run.
        DuplicateChangeItem keep = vm.AutoAddRepairItems.Single(i => i.Key.EndsWith("\tpitcha10c", StringComparison.Ordinal));
        keep.IsTicked = false;
        fake.Calls.Clear();
        await vm.TakeOutAutoAddedCommand.ExecuteAsync(null);
        IReadOnlyList<string> apply = Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "auto-added", "--apply"));
        Assert.True(AxisTuningTests.Seq(apply, "--skip", "pitcha10c"));
        Assert.False(vm.HasAutoAddRepairNotice);
        Assert.Equal("Took out 18 aircraft JoyPro had added. Ctrl+Z undoes it.", vm.StatusText);
    }

    [Fact]
    public async Task Repair_KeepThemStopsAskingAndNothingIsTakenOut()
    {
        AutoAddFakeEngine fake = new();
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await vm.CheckAutoAddRepairAsync();
        Assert.True(vm.HasAutoAddRepairNotice);
        fake.Calls.Clear();
        await vm.KeepAutoAddedCommand.ExecuteAsync(null);
        Assert.Single(fake.Calls, c => AxisTuningTests.Seq(c, "profile", "auto-added", "--keep"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--apply"));
        Assert.False(vm.HasAutoAddRepairNotice);

        // Nothing found: no notice.
        vm.ApplyAutoAddRepair("{\"Found\":false,\"Entries\":[]}");
        Assert.False(vm.HasAutoAddRepairNotice);
    }

    // "BTN 5 does different things in these aircraft: ... Where should BTN 5 work?" with the outcome on the buttons.
    [Fact]
    public async Task Banner_NamesTheButtonTheAircraftAndTheOutcome()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "two-meanings" };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await vm.RefreshAircraftCheckAsync();
        Assert.True(vm.HasAircraftCheckNotice);
        string hornet = vm.AircraftWords("DCS", ["FA-18C_hornet"]);
        string viper = vm.AircraftWords("DCS", ["F-16C_50"]);
        Assert.Equal("BTN 5 does different things in these aircraft: " + hornet + ": RADAR Switch - EMERG · " + viper + ": KY-58 MODE Knob - RV. Where should BTN 5 work?",
            vm.RelationCheckText);
        Assert.Equal("Only in the " + hornet + " (take it off the " + viper + ")", vm.RelationCheckOnlyLabel);
        Assert.Equal("Keep it in both (" + viper + " keeps KY-58 MODE Knob - RV)", vm.RelationCheckKeepLabel);

        // "Keep it in both" changes nothing in the profile (no engine run), it only stops asking on this PC.
        fake.Calls.Clear();
        vm.KeepRelationCheckForAllCommand.Execute(null);
        Assert.Empty(fake.Calls);
        Assert.Contains("Flare|DCS|d3014pnil|F-16C_50", vm.ListPrefs.KeptAircraft);
        Assert.Contains("does not ask about it again on this PC", vm.StatusText);
    }

    // The engine never proposes an imported aircraft (Ka-50 III's throttle against the Apache's power levers): no banner.
    [Fact]
    public async Task Banner_NeverProposesAnImportedAircraft()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "imported" };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await vm.RefreshAircraftCheckAsync();
        Assert.False(vm.HasAircraftCheckNotice);
        Assert.Empty(vm.AircraftCheckItems);
    }

    // The main meaning is the title; another function and the other names of the same function are lines under it.
    [Fact]
    public async Task NodeTitle_ShowsTheMainMeaningAndTheOthersOnLinesOfTheirOwn()
    {
        AutoAddFakeEngine fake = new() { CheckMode = "none", MixedNode = true };
        MainWindowViewModel vm = await RelationBuilderTests.CreateVmAsync(fake);
        await RelationBuilderTests.OpenAsync(vm, "Flare");
        await RelationBuilderTests.WaitForAsync(() => vm.RelationNodes.Any(n => n.HasAircraft));
        RelationNodeItem node = vm.RelationNodes.Single(n => n.Id == "d3014pnil");
        Assert.Equal("Countermeasures Management Switch - Fwd", node.TitlesSummary);
        string hornet = vm.AircraftWords("DCS", ["FA-18C_hornet"]);
        Assert.Equal(hornet + ": RADAR Switch - EMERG (different function)" + Environment.NewLine + "also called: CMS Forward (2)", node.MeaningNote);
        Assert.True(node.HasMeaningNote);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AutoAddFakeEngine : IEngineCliClient
{
    private readonly BuilderFakeEngine inner = new();

    public List<IReadOnlyList<string>> Calls { get; } = [];

    // check-aircraft: "two-meanings" (Flare's id is the F/A-18C's radar switch and the F-16C's KY-58 knob), "imported"
    // (the other meaning came from the user's files: no Drop), "none".
    public string CheckMode { get; set; } = "none";

    // relations aircraft: Flare's node with four ticked aircraft and three titles.
    public bool MixedNode { get; set; }

    private bool repaired;

    public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        lock (Calls)
            Calls.Add(arguments.ToArray());
        string? output = Output(arguments);
        if (output == null)
            return inner.RunAsync(engineExePath, arguments, cancellationToken);
        return Task.FromResult(new EngineCommandResult
        {
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(1),
            StandardOutput = output,
            StandardError = string.Empty,
            Arguments = arguments.ToArray()
        });
    }

    private static string Entry(string key, string relation, string id, string plane, string title) =>
        "{\"Key\":\"" + key + "\",\"Version\":117,\"Relation\":\"" + relation + "\",\"Game\":\"DCS\",\"Id\":\"" + id + "\",\"Plane\":\"" + plane + "\",\"Title\":\"" + title + "\",\"Skipped\":false}";

    private string? Output(IReadOnlyList<string> args)
    {
        if (AxisTuningTests.Seq(args, "profile", "auto-added"))
        {
            if (args.Contains("--apply"))
            {
                repaired = true;
                int skipped = args.Contains("--skip") ? args[args.ToList().IndexOf("--skip") + 1].Split(';').Length : 0;
                return "{\"Found\":true,\"Applied\":true,\"Removed\":" + (19 - skipped) + ",\"Message\":\"\"}";
            }
            if (args.Contains("--keep"))
            {
                repaired = true;
                return "{\"Found\":true,\"Kept\":true,\"Removed\":0,\"Message\":\"\"}";
            }
            if (repaired)
                return "{\"Found\":false,\"Entries\":[]}";
            List<string> entries = [];
            int n = 0;
            foreach (string plane in new[] { "a-10a", "A-10C II", "A-10C", "AV8BNA", "Default", "F-15C", "F-4E-45MC", "F-5E-3", "FA-18C_hornet", "J-11A", "mig-29", "mig-29c", "mig-29g", "su-25", "su-25T", "su-27", "su-33" })
                entries.Add(Entry("helo" + (n++).ToString(CultureInfo.InvariantCulture), "THROTTLE - SLIDER -HELO POWER BOTH", "a2004cdnil", plane, plane.StartsWith("A-10C", StringComparison.Ordinal) ? "Throttle Both" : "Thrust"));
            entries.Add(Entry("pitchfa18", "Pitch", "a2001cdnil", "FA-18C_hornet", "Pitch"));
            entries.Add(Entry("pitcha10c", "Pitch", "a2001cdnil", "A-10C", "Pitch"));
            return "{\"Found\":true,\"Aircraft\":19,\"RelationCount\":2,\"Applied\":false,\"Kept\":false,\"Removed\":0," +
                   "\"Steps\":[{\"Version\":117,\"Utc\":\"2026-11-03T13:26:39.0794492Z\",\"Local\":\"2026-11-03 14:26:39\",\"Label\":\"Keeping the action in one relation\",\"Aircraft\":19,\"Relations\":2}]," +
                   "\"Relations\":[{\"Relation\":\"Pitch\",\"Remove\":[\"FA-18C_hornet\",\"A-10C\"],\"Keeps\":[\"F-16C_50\"]}," +
                   "{\"Relation\":\"THROTTLE - SLIDER -HELO POWER BOTH\",\"Remove\":[" + string.Join(",", entries.Take(17).Select((_, i) => "\"p" + i + "\"")) + "],\"Keeps\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\",\"Ka-50 III\",\"MosquitoFBMkVI\"]}]," +
                   "\"Entries\":[" + string.Join(",", entries) + "],\"Warnings\":[]}";
        }
        if (AxisTuningTests.Seq(args, "relations", "check-aircraft"))
        {
            string found = CheckMode switch
            {
                "two-meanings" => "{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"RADAR Switch - EMERG\",\"Category\":\"\",\"Active\":2,\"Available\":2," +
                                  "\"Meanings\":[{\"Title\":\"KY-58 MODE Knob - RV\",\"Category\":\"\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"F-16C_50\"]}]," +
                                  "\"Drop\":[\"F-16C_50\"],\"SameFunction\":[],\"Imported\":[],\"Planes\":[\"FA-18C_hornet\"]}",
                "imported" => "{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"Power Levers (Both)\",\"Category\":\"\",\"Active\":3,\"Available\":3," +
                              "\"Meanings\":[{\"Title\":\"Throttle\",\"Category\":\"\",\"Match\":\"differs\",\"Note\":\"\",\"Included\":true,\"Planes\":[\"Ka-50 III\"]}]," +
                              "\"Drop\":[],\"SameFunction\":[],\"Imported\":[\"Ka-50 III\"],\"Planes\":[\"AH-64D_BLK_II_CPG\",\"AH-64D_BLK_II_PLT\"]}",
                _ => string.Empty
            };
            return "{\"Relations\":1,\"Nodes\":1,\"Found\":[" + found + "]}";
        }
        if (MixedNode && AxisTuningTests.Seq(args, "relations", "aircraft") && !args.Contains("set"))
        {
            return "{\"Relation\":\"Flare\",\"Nodes\":[{\"Relation\":\"Flare\",\"Game\":\"DCS\",\"Id\":\"d3014pnil\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\"," +
                   "\"Active\":4,\"Available\":4,\"Differs\":3,\"DiffersSameFunction\":2,\"Excluded\":0,\"Mixed\":true,\"Badge\":\"4 of 4 aircraft\",\"Aircraft\":[" +
                   "{\"Plane\":\"F-16C_50\",\"Title\":\"Countermeasures Management Switch - Fwd\",\"Category\":\"Stick\",\"State\":\"on\",\"Match\":\"same\",\"Note\":\"\"}," +
                   "{\"Plane\":\"FA-18C_hornet\",\"Title\":\"RADAR Switch - EMERG\",\"Category\":\"Sensor\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\"}," +
                   "{\"Plane\":\"A-10C\",\"Title\":\"CMS Forward\",\"Category\":\"HOTAS\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\",\"SameFunction\":true}," +
                   "{\"Plane\":\"AV8BNA\",\"Title\":\"CMS Forward\",\"Category\":\"HOTAS\",\"State\":\"on\",\"Match\":\"differs\",\"Note\":\"\",\"SameFunction\":true}]}]}";
        }
        return null;
    }
}
