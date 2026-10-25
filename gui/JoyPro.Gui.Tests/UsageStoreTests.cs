using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using JoyPro.Usage;
using Xunit;

namespace JoyPro.Gui.Tests;

// The usage summary store (engine/Usage, docs/usage-statistics.md): every parser on the synthetic fixtures of
// engine/tests/fixtures/usage/root (no real user data), the harvest's dedupe and idempotence, the store's retention
// (history survives deleted and overwritten logs, totals never drop, compaction, daily backups) and the ranking.
public sealed class UsageStoreTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "usage-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string storeFolder;

    public UsageStoreTests()
    {
        root = Path.Combine(work, "root");
        storeFolder = Path.Combine(work, "data", "Usage");
        CopyFixtures(root);
    }

    public void Dispose()
    {
        UsageStore.UtcNow = () => DateTime.UtcNow;
        try
        {
            Directory.Delete(work, true);
        }
        catch (IOException)
        {
        }
    }

    // The fixture folder (engine/tests/fixtures/usage/root) into a temp copy, with the write times the parsers use
    // (git does not keep them).
    internal static void CopyFixtures(string target)
    {
        string source = Path.Combine(RepoRoot(), "engine", "tests", "fixtures", "usage", "root");
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, true);
        }
        File.SetLastWriteTimeUtc(Path.Combine(target, "Saved Games", "DCS", "Tracks", "harrier-practice.trk"), new DateTime(2026, 1, 12, 21, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(target, "Games", "WarThunder", "Replays", "#2026.01.16 18.00.00.wrpl"), new DateTime(2026, 1, 16, 18, 25, 0, DateTimeKind.Utc));
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "tests", "fixtures", "usage")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("engine/tests/fixtures/usage not found");
    }

    private UsageInput Input(string relative, string game, string kind) => new(Path.Combine(root, relative), game, kind);

    private UsageHarvestReport Harvest(UsageStore store, bool full = false) =>
        UsageHarvester.Run(store, new FixtureLocator(root), new UsageHarvestOptions { Full = full });

    // ---- parsers --------------------------------------------------------------------------------------------------

    [Fact]
    public void DcsLog_SessionAndAircraftSegments()
    {
        UsageParseResult r = UsageParsers.Parse(Input(@"Saved Games\DCS\Logs\dcs.log", "DCS", UsageSourceKinds.DcsLog));
        UsageSession s = Assert.Single(r.Sessions);
        Assert.Equal(new DateTime(2026, 1, 10, 18, 0, 0, DateTimeKind.Utc), s.Start);
        Assert.Equal(70.5, s.Seconds / 60, 1);
        Assert.Equal(["F-16C_50", "FA-18C_hornet"], s.Units.Select(u => u.Unit));
        Assert.Equal(40, s.Units[0].Seconds / 60, 1);
        Assert.Equal(20, s.Units[1].Seconds / 60, 1);
        Assert.Equal("DCS|dcs-log|2026-01-10T18:00:00", s.Key);
    }

    [Fact]
    public void DcsLogbook_TotalsOfThePilot()
    {
        UsageTotal t = Assert.Single(UsageParsers.Parse(Input(@"Saved Games\DCS\MissionEditor\logbook.lua", "DCS", UsageSourceKinds.DcsLogbook)).Totals);
        Assert.Equal(2.5, t.Hours);
        Assert.Equal(6, t.Counters[UsageCounters.Landings]);
        Assert.Equal(5, t.Counters[UsageCounters.Kills]);
        Assert.Equal(2, t.Counters[UsageCounters.Deaths]);
        Assert.Equal("DCS|dcs-logbook|Test Pilot", t.Key);
    }

    [Fact]
    public void DcsTrack_ThePlayersAircraft()
    {
        UsageSession s = Assert.Single(UsageParsers.Parse(Input(@"Saved Games\DCS\Tracks\harrier-practice.trk", "DCS", UsageSourceKinds.DcsTrack)).Sessions);
        Assert.Equal("AV8BNA", Assert.Single(s.Units).Unit);
        Assert.Equal(new DateTime(2026, 1, 12, 21, 0, 0, DateTimeKind.Utc), s.Start);
    }

    [Fact]
    public void Tacview_PlayerByAuthor_TakeOffAndLanding()
    {
        UsageSession s = Assert.Single(UsageParsers.Parse(Input(@"Documents\Tacview\Tacview-20260110-180610-DCS-training.txt.acmi", "", UsageSourceKinds.Tacview)).Sessions);
        Assert.Equal("DCS", s.Game);
        UsageSegment f16 = Assert.Single(s.Units);
        Assert.Equal("F-16C_50", f16.Unit);
        Assert.Equal(2400, f16.Seconds);
        Assert.Equal(1, f16.Counters[UsageCounters.Landings]);
        Assert.Equal(1, f16.Counters[UsageCounters.TakeOffs]);
    }

    [Fact]
    public void Tacview_GameFromDataSource()
    {
        Assert.Equal("DCS", TacviewUsageParser.GameOf("DCS 2.9.0"));
        Assert.Equal("FalconBMS", TacviewUsageParser.GameOf("Falcon 4.0 (BMS 4.37)"));
        Assert.Equal("IL2Game", TacviewUsageParser.GameOf("IL-2 Sturmovik"));
        Assert.Equal("XPlane12", TacviewUsageParser.GameOf("X-Plane 12"));
        Assert.Equal(string.Empty, TacviewUsageParser.GameOf("Unknown Sim"));
    }

    [Fact]
    public void Elite_ShipsJumpsDistanceLandingsKillsDeaths()
    {
        UsageSession s = Assert.Single(UsageParsers.Parse(Input(@"Saved Games\Frontier Developments\Elite Dangerous\Journal.2026-01-11T100000.01.log", "EliteDangerous", UsageSourceKinds.EliteJournal)).Sessions);
        Assert.Equal(["python", "anaconda"], s.Units.Select(u => u.Unit));
        Assert.Equal(["Python", "Anaconda"], s.Units.Select(u => u.Display));
        Assert.Equal(2, s.Counters[UsageCounters.Jumps]);
        Assert.Equal(32.75, s.Counters[UsageCounters.DistanceLy], 2);
        Assert.Equal(2, s.Counters[UsageCounters.Landings]);
        Assert.Equal(1, s.Counters[UsageCounters.Kills]);
        Assert.Equal(1, s.Counters[UsageCounters.Deaths]);
        Assert.Equal(2, s.Units[0].Counters[UsageCounters.Jumps]);
        Assert.Equal(100, s.Seconds / 60, 1);
    }

    [Fact]
    public void StarCitizen_ShipByControlToken_KillsDeathsQuantum_DefaultIgnored()
    {
        UsageSession old = Assert.Single(UsageParsers.Parse(Input(@"Games\StarCitizen\LIVE\logbackups\Game Build(100) 01 Jan 26 (10 00 00).log", "StarCitizen", UsageSourceKinds.StarCitizenLog)).Sessions);
        UsageSegment gladius = Assert.Single(old.Units);
        Assert.Equal("AEGS_Gladius", gladius.Unit);
        Assert.Equal("Aegis Gladius", gladius.Display);
        Assert.Equal(40, gladius.Seconds / 60, 1);
        Assert.Equal(1, old.Counters[UsageCounters.Kills]);
        Assert.Equal(1, old.Counters[UsageCounters.Deaths]);
        Assert.Equal(1, old.Counters[UsageCounters.Jumps]);
        Assert.Equal(90, old.Seconds / 60, 1);

        UsageSession now = Assert.Single(UsageParsers.Parse(Input(@"Games\StarCitizen\LIVE\Game.log", "StarCitizen", UsageSourceKinds.StarCitizenLog)).Sessions);
        Assert.Equal("RSI_Polaris", Assert.Single(now.Units).Unit);
        Assert.Equal("Crusader Starlifter A2", StarCitizenUsageParser.ShipName("CRUS_Starlifter_A2"));
    }

    [Fact]
    public void XPlane_LogbookLinesAndLogTxt()
    {
        List<UsageSession> lines = UsageParsers.Parse(Input(@"Games\XPlane12\Output\logbooks\X-Plane Pilot.txt", "XPlane12", UsageSourceKinds.XPlaneLogbook)).Sessions;
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.DateOnly));
        Assert.Equal("Cessna 172 SP", lines[0].Units[0].Unit);
        Assert.Equal(3600, lines[0].Seconds);
        Assert.Equal(2, lines[1].Counters[UsageCounters.Landings]);
        Assert.Equal(new DateTime(2026, 1, 5), lines[1].Start.Date);

        UsageSession log = Assert.Single(UsageParsers.Parse(Input(@"Games\XPlane12\Log.txt", "XPlane12", UsageSourceKinds.XPlaneLog)).Sessions);
        Assert.Equal("Cessna_172SP", Assert.Single(log.Units).Unit);
        Assert.Equal(UsageInput.LocalToUtc(new DateTime(2026, 1, 14, 15, 0, 0)), log.Start);
        Assert.Equal(60, log.Units[0].Seconds / 60, 1);
    }

    [Fact]
    public void Il2_MissionOverItsParts_PlaneKillsTakeOffLanding()
    {
        UsageSession s = Assert.Single(UsageParsers.Parse(Input(@"Games\IL2Game\data\missionReports\missionReport(2026-01-15_20-00-00)[0].txt", "IL2Game", UsageSourceKinds.Il2MissionReport)).Sessions);
        UsageSegment yak = Assert.Single(s.Units);
        Assert.Equal("Yak-1 ser.69", yak.Unit);
        Assert.Equal((90500 - 15) / 50.0, yak.Seconds, 1);
        Assert.Equal(1, s.Counters[UsageCounters.Kills]);
        Assert.Equal(1, s.Counters[UsageCounters.TakeOffs]);
        Assert.Equal(1, s.Counters[UsageCounters.Landings]);
        // The second part gives the same session (same key).
        UsageSession again = Assert.Single(UsageParsers.Parse(Input(@"Games\IL2Game\data\missionReports\missionReport(2026-01-15_20-00-00)[1].txt", "IL2Game", UsageSourceKinds.Il2MissionReport)).Sessions);
        Assert.Equal(s.Key, again.Key);
    }

    [Fact]
    public void Bms_LogbookRoundTrip_AndFlightHours()
    {
        UsageTotal t = Assert.Single(UsageParsers.Parse(Input(@"Games\FalconBMS\User\Config\Viper.lbk", "FalconBMS", UsageSourceKinds.BmsLogbook)).Totals);
        Assert.Equal(12.5, t.Hours);
        Assert.Equal("F-16", t.Unit);
        Assert.Equal(4, t.Counters[UsageCounters.Kills]);
        Assert.Equal(1, t.Counters[UsageCounters.Deaths]);
        Assert.Equal(7, t.Counters["missions"]);
        byte[] plain = BmsUsageParser.Decode(File.ReadAllBytes(Path.Combine(root, @"Games\FalconBMS\User\Config\Viper.lbk")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(root, @"Games\FalconBMS\User\Config\Viper.lbk")), BmsUsageParser.Encode(plain));
        // A logbook of another pilot under a wrong name is not taken for this one.
        string renamed = Path.Combine(root, @"Games\FalconBMS\User\Config\Other.lbk");
        File.Copy(Path.Combine(root, @"Games\FalconBMS\User\Config\Viper.lbk"), renamed);
        UsageParseResult other = UsageParsers.Parse(new UsageInput(renamed, "FalconBMS", UsageSourceKinds.BmsLogbook));
        Assert.Empty(other.Totals);
        Assert.NotEmpty(other.Notes);
    }

    [Fact]
    public void WarThunder_ReplayHeader_StartAndLength()
    {
        UsageSession s = Assert.Single(UsageParsers.Parse(Input(@"Games\WarThunder\Replays\#2026.01.16 18.00.00.wrpl", "WarThunder", UsageSourceKinds.WarThunderReplay)).Sessions);
        Assert.Equal(new DateTime(2026, 1, 16, 18, 0, 0, DateTimeKind.Utc), s.Start);
        Assert.Equal(25 * 60, s.Seconds, 1);
        Assert.Empty(s.Units);
    }

    [Fact]
    public void X4_SaveInfo_PlayTime()
    {
        UsageTotal t = Assert.Single(UsageParsers.Parse(Input(@"Documents\Egosoft\X4\1001\save\quicksave.xml.gz", "X4", UsageSourceKinds.X4Save)).Totals);
        Assert.Equal(10, t.Hours, 2);
        Assert.Equal("X4|x4-save|555111", t.Key);
    }

    [Fact]
    public void Steam_PlaytimeOfEveryAccountAddsUp()
    {
        List<string> notes = [];
        List<string> vdfs = UsageSources.SteamConfigs(new FixtureLocator(root));
        Assert.Equal(2, vdfs.Count);
        List<UsageTotal> totals = SteamUsageParser.Totals(vdfs, g => g == "WarThunder" ? [236390] : g == "DCS" ? [223750] : g == "EliteDangerous" ? [359320] : [], ["DCS", "WarThunder", "EliteDangerous"], notes);
        Assert.Equal(23, totals.Single(t => t.Game == "WarThunder").Hours, 2);
        Assert.Equal(10, totals.Single(t => t.Game == "DCS").Hours, 2);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1768150000), totals.Single(t => t.Game == "WarThunder").LastPlayed);
    }

    [Fact]
    public void BrokenFiles_GiveNotes_NeverThrow()
    {
        string broken = Path.Combine(work, "broken.trk");
        File.WriteAllText(broken, "not a zip");
        UsageParseResult r = UsageParsers.Parse(new UsageInput(broken, "DCS", UsageSourceKinds.DcsTrack));
        Assert.True(r.IsEmpty);
        Assert.NotEmpty(r.Notes);
        File.WriteAllBytes(broken, [1, 2, 3]);
        Assert.True(UsageParsers.Parse(new UsageInput(broken, "WarThunder", UsageSourceKinds.WarThunderReplay)).IsEmpty);
        Assert.True(UsageParsers.Parse(new UsageInput(broken, "FalconBMS", UsageSourceKinds.BmsLogbook)).IsEmpty);
    }

    // ---- harvest, dedupe, idempotence --------------------------------------------------------------------------------

    [Fact]
    public void Harvest_EverySource_ThenNothingTwice()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        UsageHarvestReport first = Harvest(store);
        Assert.True(first.SessionsNew >= 10, "sessions: " + first.SessionsNew);
        int sessions = store.Sessions.Count;
        int totals = store.Totals.Count;
        string before = File.ReadAllText(Path.Combine(storeFolder, "sessions.jsonl"));

        // Same files again: all known, nothing written.
        UsageHarvestReport second = Harvest(UsageStore.Open(storeFolder));
        Assert.Equal(0, second.FilesRead);
        Assert.Equal(0, second.SessionsNew);
        Assert.Equal(before, File.ReadAllText(Path.Combine(storeFolder, "sessions.jsonl")));

        // Everything read again (--full): sessions replace themselves, nothing is added.
        UsageStore again = UsageStore.Open(storeFolder);
        UsageHarvestReport full = Harvest(again, full: true);
        Assert.Equal(0, full.SessionsNew);
        Assert.Equal(sessions, again.Sessions.Count);
        Assert.Equal(totals, again.Totals.Count);
        Assert.Equal(sessions, UsageStore.Open(storeFolder).Sessions.Count);
    }

    [Fact]
    public void Harvest_ReportsWhatEachSourceYielded()
    {
        UsageHarvestReport r = Harvest(UsageStore.Open(storeFolder));
        UsageHarvestSource sc = r.Sources.Single(s => s.Game == "StarCitizen" && s.Kind == UsageSourceKinds.StarCitizenLog);
        Assert.Equal(2, sc.Files);
        Assert.Equal(2, sc.Sessions);
        Assert.True(sc.LiveTail);
        Assert.Equal(1, r.Sources.Single(s => s.Game == "DCS" && s.Kind == UsageSourceKinds.DcsLogbook).Totals);
        Assert.Equal(2, r.Sources.Single(s => s.Game == "DCS" && s.Kind == UsageSourceKinds.DcsLog).Sessions);
        Assert.True(r.Sources.Single(s => s.Game == "WarThunder" && s.Kind == UsageSourceKinds.SteamPlaytime).TotalHours > 20);
        Assert.Contains(UsageHarvester.LastSources(UsageStore.Open(storeFolder)), s => (string)s["Game"] == "EliteDangerous");
    }

    [Fact]
    public void MovedLog_IsRecognised_NoSecondSession()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        int sessions = store.Sessions.Count;
        // The game starts again: Game.log moves into logbackups under a new name (same bytes).
        string live = Path.Combine(root, @"Games\StarCitizen\LIVE\Game.log");
        File.Move(live, Path.Combine(root, @"Games\StarCitizen\LIVE\logbackups\Game Build(100) 13 Jan 26 (20 00 00).log"));
        UsageHarvestReport r = Harvest(UsageStore.Open(storeFolder));
        Assert.Equal(0, r.FilesRead);
        Assert.Equal(sessions, UsageStore.Open(storeFolder).Sessions.Count);
    }

    [Fact]
    public void GrownLog_ReplacesItsSession()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        string log = Path.Combine(root, @"Saved Games\DCS\Logs\dcs.log");
        string text = File.ReadAllText(log);
        // First read while the game still runs (no F/A-18 yet).
        File.WriteAllText(log, string.Join("\n", text.Split('\n').Take(7)) + "\n");
        Harvest(store);
        UsageSession partial = store.Sessions["DCS|dcs-log|2026-01-10T18:00:00"];
        Assert.Single(partial.Units);
        File.WriteAllText(log, text);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(1));
        UsageHarvestReport r = Harvest(UsageStore.Open(storeFolder));
        Assert.Equal(1, r.SessionsUpdated);
        UsageStore reread = UsageStore.Open(storeFolder);
        Assert.Equal(2, reread.Sessions["DCS|dcs-log|2026-01-10T18:00:00"].Units.Count);
        Assert.Equal(1, reread.Sessions.Keys.Count(k => k.StartsWith("DCS|dcs-log|2026-01-10", StringComparison.Ordinal)));
    }

    [Fact]
    public void LiveCopy_ReadLikeTheLog_FinishedCopyRemoved_LogOverwrittenLater()
    {
        // The watcher's copy of dcs.log, finished when the game closed.
        string liveFolder = Path.Combine(storeFolder, "Live", "DCS");
        Directory.CreateDirectory(liveFolder);
        string copy = Path.Combine(liveFolder, UsageHarvester.LiveName(UsageSourceKinds.DcsLog, "dcs.log", DateTime.UtcNow) + "x");
        string done = Path.ChangeExtension(copy, null) + UsageHarvester.DoneExtension;
        File.Copy(Path.Combine(root, @"Saved Games\DCS\Logs\dcs.log"), done);
        // The game overwrote its log at the next start before JoyPro read it: only the copy has that session.
        File.WriteAllText(Path.Combine(root, @"Saved Games\DCS\Logs\dcs.log"), "=== Log opened UTC 2026-01-20 10:00:00\n2026-01-20 10:00:05.000 INFO    EDCORE (Main): (dDispatcher)enterToState_:3\n");
        UsageStore store = UsageStore.Open(storeFolder);
        UsageHarvestReport r = Harvest(store);
        Assert.Equal(1, r.LiveCopiesRead);
        Assert.Equal(1, r.LiveCopiesRemoved);
        Assert.False(File.Exists(done));
        Assert.True(store.Sessions.ContainsKey("DCS|dcs-log|2026-01-10T18:00:00"));
        Assert.True(store.Sessions.ContainsKey("DCS|dcs-log|2026-01-20T10:00:00"));
        Assert.True(UsageHarvester.ParseLiveName(Path.GetFileName(done), out string kind, out string original));
        Assert.Equal(UsageSourceKinds.DcsLog, kind);
        Assert.Equal("dcs.log", original);
    }

    // ---- retention ---------------------------------------------------------------------------------------------------

    [Fact]
    public void History_SurvivesDeletedLogs_TotalsNeverDrop()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        int sessions = store.Sessions.Count;
        double steam = store.Totals["WarThunder|steam"].Hours;
        // The game deletes its logs, Steam shows less (another account removed).
        Directory.Delete(Path.Combine(root, @"Games\StarCitizen\LIVE\logbackups"), true);
        File.Delete(Path.Combine(root, @"Saved Games\DCS\Logs\dcs.log.old"));
        File.Delete(Path.Combine(root, @"Steam\userdata\2\config\localconfig.vdf"));
        Harvest(UsageStore.Open(storeFolder), full: true);
        UsageStore after = UsageStore.Open(storeFolder);
        Assert.Equal(sessions, after.Sessions.Count);
        Assert.Equal(steam, after.Totals["WarThunder|steam"].Hours);
    }

    [Fact]
    public void Store_Compacts_AndKeepsDailyBackups()
    {
        DateTime day = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        UsageStore.UtcNow = () => day;
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        // Many re-reads of one session (a live copy growing): the file is compacted to one line per session.
        UsageSession s = store.Sessions.Values.First(x => x.Seconds > 0).Clone();
        for (int i = 1; i <= 450; i++)
        {
            UsageSession longer = s.Clone();
            longer.Seconds = s.Seconds + i;
            store.Commit([longer], null, null);
        }
        UsageStore reread = UsageStore.Open(storeFolder);
        Assert.Equal(s.Seconds + 450, reread.Sessions[s.Key].Seconds);
        Assert.True(reread.SessionLineCount <= Math.Max(200, reread.Sessions.Count * 2), "lines: " + reread.SessionLineCount);
        // A backup per day before its first change; the newest 7 stay.
        for (int d = 1; d <= 9; d++)
        {
            DateTime now = day.AddDays(d);
            UsageStore.UtcNow = () => now;
            UsageSession next = s.Clone();
            next.Seconds = s.Seconds + 1000 + d;
            reread.Commit([next], null, null);
        }
        List<string> backups = reread.BackupDaysList();
        Assert.Equal(UsageStore.BackupDays, backups.Count);
        Assert.Equal("2026-02-10", backups[0]);
        Assert.True(reread.Restore(backups[^1]));
        Assert.True(reread.Sessions[s.Key].Seconds < s.Seconds + 1009);
    }

    [Fact]
    public void Overrides_Persist()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        store.Overrides.Pinned.Add("DCS|F-16C_50");
        store.Overrides.Excluded.Add("StarCitizen|ARGO_RAFT");
        store.SaveOverrides();
        UsageStore reread = UsageStore.Open(storeFolder);
        Assert.Contains("DCS|F-16C_50", reread.Overrides.Pinned);
        Assert.Contains("StarCitizen|ARGO_RAFT", reread.Overrides.Excluded);
    }

    [Fact]
    public void Exports_AreCounted()
    {
        UsageStore store = new(storeFolder);
        store.RecordExport("DCS", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        store.RecordExport("DCS", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        store.RecordExport("IL2Game", new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        UsageStore reread = UsageStore.Open(storeFolder);
        Assert.Equal(2, reread.Exports.Count(e => e.Key == "DCS"));
        Assert.Equal(3, reread.Exports.Count);
    }

    // ---- aggregation and ranking -----------------------------------------------------------------------------------

    private static string ClassOf(string game, string unit) => game switch
    {
        "StarCitizen" or "EliteDangerous" or "X4" => "spaceship",
        "WarThunder" => "",
        _ => unit.StartsWith("AH-", StringComparison.Ordinal) || unit.StartsWith("UH-", StringComparison.Ordinal) ? "rotary_wing" : "fixed_wing"
    };

    [Fact]
    public void Aggregate_SourcesDoNotCountTwice()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        UsageAggregate a = UsageAggregate.Build(store.Sessions.Values, store.Totals.Values, null, ClassOf);
        // The F-16: dcs.log 40 min + Tacview 40 min of the same flight + the earlier run 30 min = 70 min, not 110.
        UsageUnitStat f16 = a.Unit("DCS", "F-16C_50")!;
        Assert.Equal(70, f16.LogHours * 60, 0);
        Assert.Equal(1, f16.Counters[UsageCounters.Landings]);
        // DCS game time: union of the runs (70.5 + 31.2 min); Steam (10 h) is higher and wins for the game.
        UsageGameStat dcs = a.Games["DCS"];
        Assert.Equal(3, dcs.Sessions); // two runs plus the track (a flight without hours)
        Assert.Equal(10, dcs.SteamHours, 2);
        Assert.Equal(10, dcs.Hours, 2);
        // X-Plane: the logbook line of Jan 14 is the Log.txt flight; its landing goes there, its hour does not add.
        UsageUnitStat cessna = a.Unit("XPlane12", "Cessna_172SP")!;
        Assert.Equal(1, cessna.Counters[UsageCounters.Landings]);
        Assert.Equal(60, cessna.LogHours * 60, 0);
        Assert.Null(a.Unit("XPlane12", "Cessna 172 SP"));
        // The older logbook line (no Log.txt any more) counts on its own.
        Assert.Equal(0.5, a.Unit("XPlane12", "Cirrus SR22")!.LogHours, 2);
        // BMS: the logbook's hours for the F-16.
        Assert.Equal(12.5, a.Unit("FalconBMS", "F-16")!.Hours);
    }

    [Fact]
    public void Ranking_PinnedFirst_ExcludedLast_BindsOnlyAfterLogged_References()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        UsageAggregate a = UsageAggregate.Build(store.Sessions.Values, store.Totals.Values, null, ClassOf);
        Dictionary<string, int> binds = new() { ["DCS|F-14B"] = 120, ["DCS|F-16C_50"] = 40 };
        UsageRanking r = UsageRanking.Build(a, new UsageOverrides(), binds, ClassOf);
        // The BMS logbook's 12.5 h on the F-16 lead (a game's own total counts like logged time).
        Assert.Equal(("FalconBMS", "F-16", "total"), (r.Units[0].Game, r.Units[0].Unit, r.Units[0].Evidence));
        List<double> logged = r.Units.Where(u => u.Evidence != "binds").Select(u => u.Hours).ToList();
        Assert.Equal(logged.OrderByDescending(h => h), logged);
        UsageUnitStat f14 = r.Units.Single(u => u.Unit == "F-14B");
        Assert.Equal("binds", f14.Evidence);
        Assert.True(r.Units.IndexOf(f14) > r.Units.FindLastIndex(u => u.Evidence != "binds" && !u.Excluded));
        Assert.Equal(40, r.Units.Single(u => u.Unit == "F-16C_50").Binds);
        Dictionary<string, UsageUnitStat> refs = r.References();
        Assert.Equal("F-16C_50", refs[UsageRanking.ReferenceKey("DCS", "fixed_wing")].Unit);
        Assert.False(refs.ContainsKey(UsageRanking.ReferenceKey("DCS", "rotary_wing")));
        Assert.True(r.IsUnused("DCS", "F-14B"));
        Assert.False(r.IsUnused("DCS", "F-16C_50"));
        // War Thunder (Steam 23 h) heads the games.
        Assert.Equal("WarThunder", r.Games[0].Game);

        UsageOverrides o = new();
        o.Pinned.Add("DCS|FA-18C_hornet");
        o.Excluded.Add("StarCitizen|RSI_Polaris");
        o.Excluded.Add("WarThunder|");
        UsageRanking r2 = UsageRanking.Build(a, o, binds, ClassOf);
        Assert.Equal("FA-18C_hornet", r2.Units[0].Unit);
        Assert.True(r2.Units[0].Pinned);
        Assert.Equal("RSI_Polaris", r2.Units[^1].Unit);
        Assert.Equal("FA-18C_hornet", r2.References()[UsageRanking.ReferenceKey("DCS", "fixed_wing")].Unit);
        Assert.False(r2.References().Values.Any(u => u.Unit == "RSI_Polaris"));
        Assert.Equal("WarThunder", r2.Games[^1].Game);
        Assert.True(r2.Games[^1].Excluded);
    }

    [Fact]
    public void Stats_StreaksFactsAndCounters()
    {
        UsageStore store = UsageStore.Open(storeFolder);
        Harvest(store);
        store.RecordExport("DCS", DateTime.UtcNow);
        store.RecordExport("DCS", DateTime.UtcNow);
        store.RecordExport("IL2Game", DateTime.UtcNow);
        UsageAggregate a = UsageAggregate.Build(store.Sessions.Values, store.Totals.Values, g => g + " name", ClassOf);
        UsageRanking r = UsageRanking.Build(a, store.Overrides, null, ClassOf);
        Dictionary<string, object> stats = UsageStats.Build(store, a, r, null, g => g + " name", new DateTime(2026, 1, 16, 20, 0, 0, DateTimeKind.Utc));
        Dictionary<string, object> totals = (Dictionary<string, object>)stats["Totals"];
        Assert.True((bool)stats["HasData"]);
        // Jan 9 .. Jan 16 has play on every day except Jan 7/8: the streak runs from Jan 9 to Jan 16.
        Assert.True((int)totals["LongestStreakDays"] >= 6, "streak " + totals["LongestStreakDays"]);
        Assert.True((int)totals["CurrentStreakDays"] >= 1);
        List<object> counters = (List<object>)stats["Counters"];
        Assert.Contains(counters.Cast<Dictionary<string, object>>(), c => (string)c["Name"] == UsageCounters.Jumps);
        List<string> facts = ((List<object>)stats["Facts"]).Cast<Dictionary<string, object>>().Select(f => (string)f["Id"]).ToList();
        Assert.Contains("exports", facts);
        Assert.Contains("streak", facts);
        Assert.Equal(2, ((List<object>)stats["Exports"]).Cast<Dictionary<string, object>>().Single(e => (string)e["Game"] == "DCS")["Count"]);
        string text = UsageStats.ToText(stats);
        Assert.Contains("Fun facts", text);
        Assert.Contains("distanceLy: 32.8", text);
        // Every fact id has an English sentence.
        foreach (Dictionary<string, object> f in ((List<object>)stats["Facts"]).Cast<Dictionary<string, object>>())
            Assert.False(UsageStats.FactEnglish((string)f["Id"], ((List<object>)f["Values"]).Cast<string>().ToList()).StartsWith((string)f["Id"] + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void Facts_MoreThanAllHelicopters()
    {
        List<UsageSession> sessions = [];
        DateTime t = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        void Fly(string unit, double hours)
        {
            UsageSession s = new() { Key = "DCS|x|" + sessions.Count, Game = "DCS", Source = UsageSourceKinds.DcsLog, Start = t, Seconds = hours * 3600 };
            s.Units.Add(new UsageSegment { Unit = unit, Display = unit, Start = t, Seconds = hours * 3600 });
            sessions.Add(s);
            t = t.AddDays(1);
        }
        Fly("F-14B", 10);
        Fly("AH-64D_BLK_II", 3);
        Fly("UH-1H", 2);
        UsageAggregate a = UsageAggregate.Build(sessions, [], null, ClassOf);
        UsageRanking r = UsageRanking.Build(a, new UsageOverrides(), null, ClassOf);
        Dictionary<string, object> stats = UsageStats.Build(new UsageStore(storeFolder), a, r, null, g => g, t);
        Dictionary<string, object> fact = ((List<object>)stats["Facts"]).Cast<Dictionary<string, object>>().First();
        Assert.Equal("more-than-helicopters", fact["Id"]);
        Assert.Equal("F-14B", ((List<object>)fact["Values"])[0]);
        Assert.Equal("You flew the F-14B more than all helicopters together (10 h vs 5 h).", UsageStats.FactEnglish("more-than-helicopters", ["F-14B", "10", "5"]));
    }

    [Fact]
    public void Vdf_ParsesNestedKeysAndComments()
    {
        VdfNode node = Vdf.Parse("// comment\n\"a\" { \"apps\" { \"10\" { \"Playtime\" \"5\" } } }");
        Assert.Equal("5", node.FindAll("apps").Single().Children.Single().Value("Playtime"));
    }

    // The fixture folder as JOYPRO_USAGE_ROOT lays it out (as engine/EngineUsage.cs EngineUsageLocator in its test mode).
    internal sealed class FixtureLocator(string root) : IUsageLocator
    {
        private static readonly Dictionary<string, int[]> Apps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DCS"] = [223750], ["WarThunder"] = [236390], ["EliteDangerous"] = [359320], ["StarCitizen"] = [], ["XPlane12"] = [2014780], ["IL2Game"] = [307960],
            ["FalconBMS"] = [], ["X4"] = [392160]
        };

        public IEnumerable<string> Games => Apps.Keys;
        public string GameName(string game) => game;
        public string InstallFolder(string game) => Directory.Exists(Path.Combine(root, "Games", game)) ? Path.Combine(root, "Games", game) : string.Empty;
        public IEnumerable<string> DcsSavedGames() => Directory.Exists(SavedGamesRoot()) ? Directory.GetDirectories(SavedGamesRoot(), "DCS*") : [];
        public string SavedGamesRoot() => Path.Combine(root, "Saved Games");
        public string DocumentsRoot() => Path.Combine(root, "Documents");
        public IEnumerable<string> SteamRoots() => [Path.Combine(root, "Steam")];
        public int[] SteamApps(string game) => Apps.TryGetValue(game, out int[]? apps) ? apps : [];
        public IEnumerable<string> ProcessNames(string game) => [];
    }
}
