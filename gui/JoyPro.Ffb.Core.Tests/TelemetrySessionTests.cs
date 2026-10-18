using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.TelemetrySessions;

namespace JoyPro.Ffb.Core.Tests;

// The session file format, the store (list, recover, retention) and the exports (ACMI 2.2, CSV).
public class TelemetrySessionTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestEnvironment.DataFolder, "sessions-" + Guid.NewGuid().ToString("N"));

    public TelemetrySessionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    internal static Dictionary<string, string> Header(string game = "DCS", string aircraft = "F-16C_50", bool geo = true, DateTime? created = null)
    {
        var header = new Dictionary<string, string>
        {
            ["game"] = game,
            ["gameTitle"] = TelemetryRecordingGames.Find(game)?.Title ?? game,
            ["source"] = "test",
            ["created"] = TelemetrySessionFormat.FormatTime(created ?? new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)),
            ["info.aircraft"] = aircraft,
            ["info.map"] = "Caucasus"
        };
        if (geo)
        {
            header["nav.lat"] = "t.lat*1+0";
            header["nav.lon"] = "t.lon*1+0";
        }
        else
        {
            header["nav.u"] = "t.x*1+0";
            header["nav.v"] = "t.y*1+0";
        }

        header["nav.alt"] = "t.alt_ft*0.3048+0";
        header["nav.roll"] = "t.bank*57.29577951+0";
        header["nav.pitch"] = "t.pitch*57.29577951+0";
        header["nav.yaw"] = "t.hdg*57.29577951+0";
        header["nav.ias"] = "t.ias*1+0";
        return header;
    }

    // A two-minute flight at 10 Hz going north-east from 41.5 N / 41.9 E.
    internal static string WriteFlight(string folder, string name = "flight.jptel", bool geo = true, int blockBytes = 2048, bool finish = true, DateTime? created = null)
    {
        var path = Path.Combine(folder, name);
        using var writer = new TelemetrySessionWriter(path, Header(geo: geo, created: created)) { BlockBytes = blockBytes };
        writer.Channel("t.lat", "deg", "Latitude", "Position", -90, 90);
        writer.Channel("t.alt_ft", "ft", "Altitude", "Position", 0, 50000);
        for (var i = 0; i <= 1200; i++)
        {
            var t = i / 10.0;
            var values = new Dictionary<string, double>
            {
                ["t.alt_ft"] = 5000 + i,
                ["t.bank"] = Math.Sin(t / 10) * 0.5,
                ["t.pitch"] = 0.05,
                ["t.hdg"] = Math.PI / 4,
                ["t.ias"] = 150,
                ["t.gear"] = i < 600 ? 1 : 0
            };
            if (geo)
            {
                values["t.lat"] = 41.5 + i * 1e-5;
                values["t.lon"] = 41.9 + i * 1e-5;
            }
            else
            {
                values["t.x"] = i * 10.0;
                values["t.y"] = i * 5.0;
            }

            if (i >= 1000)
                values.Remove("t.gear");    // a value that goes away
            writer.Frame(t, values);
            if (i == 300)
                writer.Event(t, "message", "Bandit, bandit, 12 o'clock\nhigh, 5 miles");
            if (i == 500)
                writer.Event(t, "aircraft", "F-16C_50 block 50");
            if (i % 50 == 0)
            {
                var raw = Encoding.ASCII.GetBytes("t=" + t.ToString(CultureInfo.InvariantCulture));
                writer.Raw(t, "udp", raw, 0, raw.Length);
            }
            if (i == 600)
            {
                var objects = string.Join("\n",
                    TelemetryEventKinds.ObjectLine("dcs-17", "Air+FixedWing", "MiG-29A", "Enemies", 41.6, 42.0, 3000, 0, 0, 180),
                    TelemetryEventKinds.ObjectLine("dcs-18", "Ground+Vehicle", "T-72B", "Enemies", 41.55, 41.95, 20));
                writer.Event(t, TelemetryEventKinds.Objects, objects);
            }

            if (i == 700)
                writer.Event(t, TelemetryEventKinds.Objects, TelemetryEventKinds.ObjectLine("dcs-17", "Air+FixedWing", "MiG-29A", "Enemies", 41.61, 42.01, 3100));
        }

        if (finish)
            writer.Finish("mission ended", new DateTime(2026, 10, 18, 12, 2, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public void Writer_And_Reader_RoundTrip_AcrossManyBlocks()
    {
        var path = WriteFlight(_folder);
        using var reader = new TelemetrySessionReader(path, includeRaw: true);
        var frames = 0;
        var events = new List<(double T, string Kind, string Text)>();
        var raws = 0;
        double lastAlt = 0;
        var gearGoneAt = -1.0;
        while (reader.Next())
        {
            switch (reader.Kind)
            {
                case TelemetrySessionReader.RecordKind.Frame:
                    frames++;
                    var alt = reader.Value(reader.ChannelIndex("t.alt_ft"));
                    Assert.Equal(5000 + Math.Round(reader.Time * 10), alt, 6);
                    lastAlt = alt;
                    var gear = reader.Value(reader.ChannelIndex("t.gear"));
                    Assert.Equal(reader.Time < 60 ? 1 : reader.Time < 100 ? 0 : double.NaN, gear);
                    if (double.IsNaN(gear) && gearGoneAt < 0)
                        gearGoneAt = reader.Time;
                    Assert.Equal(41.5 + Math.Round(reader.Time * 10) * 1e-5, reader.Value(reader.ChannelIndex("t.lat")), 9);
                    break;
                case TelemetrySessionReader.RecordKind.Event:
                    events.Add((reader.Time, reader.EventKind, reader.EventText));
                    break;
                case TelemetrySessionReader.RecordKind.Raw:
                    raws++;
                    Assert.Equal("udp", reader.RawStream);
                    Assert.StartsWith("t=", Encoding.ASCII.GetString(reader.RawData));
                    break;
            }
        }

        Assert.Equal(1201, frames);
        Assert.Equal(6200, lastAlt);
        Assert.Equal(100, gearGoneAt, 3);
        Assert.Equal(25, raws);
        Assert.Contains(events, e => e.Kind == "message" && e.Text == "Bandit, bandit, 12 o'clock\nhigh, 5 miles" && Math.Abs(e.T - 30) < 1e-9);
        Assert.Equal(3, events.Count(e => e.Kind is "aircraft" or "objects"));
        Assert.False(reader.Truncated);
        Assert.NotNull(reader.Summary);
        Assert.Equal("mission ended", reader.Summary!["endReason"]);
        Assert.Equal("Latitude", reader.Channels[reader.ChannelIndex("t.lat")].Title);
        Assert.Equal("deg", reader.Channels[reader.ChannelIndex("t.lat")].Unit);
    }

    [Fact]
    public void Writer_OnlyWritesChanges_AndCompresses()
    {
        var path = Path.Combine(_folder, "flat.jptel");
        using (var writer = new TelemetrySessionWriter(path, Header()))
        {
            for (var i = 0; i < 20000; i++)
                writer.Frame(i / 100.0, new Dictionary<string, double> { ["a"] = 1, ["b"] = 2, ["c"] = i / 1000 });
            writer.Finish("done", DateTime.UtcNow);
        }

        // 20000 frames of three values that hardly change: well under 100 KB (the text alone is about 200 KB).
        Assert.InRange(new FileInfo(path).Length, 1, 100 * 1024);
        var info = TelemetrySessionStore.ReadInfo(path);
        Assert.Equal(20000, info.Frames);
        Assert.Equal(199.99, info.Duration, 3);
        Assert.Equal(3, info.Channels);
    }

    [Fact]
    public void ReadInfo_UsesHeaderAndSummary()
    {
        var path = WriteFlight(_folder);
        var info = TelemetrySessionStore.ReadInfo(path);
        Assert.True(info.Complete);
        Assert.False(info.InUse);
        Assert.Equal("DCS", info.Game);
        Assert.Equal("DCS World", info.GameTitle);
        Assert.Equal(120, info.Duration, 3);
        Assert.Equal(1201, info.Frames);
        Assert.Equal(["F-16C_50", "F-16C_50 block 50"], info.Aircraft);
        Assert.Equal(["Caucasus"], info.Maps);
        Assert.Equal(new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), info.CreatedUtc);
        Assert.True(info.HasPosition);
        Assert.Contains(info.Nav, n => n.Target == "alt" && n.Key == "t.alt_ft" && Math.Abs(n.Scale - 0.3048) < 1e-12);
    }

    [Fact]
    public void CutOffSession_IsReadToTheLastWholeBlock_AndRecovered()
    {
        var path = WriteFlight(_folder, finish: false);
        // A crash in the middle of a block: the last bytes are missing.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length - 37)]);
        var info = TelemetrySessionStore.ReadInfo(path);
        Assert.False(info.Complete);
        Assert.Equal("unfinished", info.EndReason);
        Assert.InRange(info.Duration, 100, 120);

        int frames;
        using (var reader = new TelemetrySessionReader(path))
        {
            frames = 0;
            while (reader.Next())
            {
                if (reader.Kind == TelemetrySessionReader.RecordKind.Frame)
                    frames++;
            }

            Assert.True(reader.Truncated);
            Assert.InRange(frames, 1000, 1201);
        }

        var recovered = TelemetrySessionStore.Recover(_folder);
        Assert.Equal([path], recovered);
        info = TelemetrySessionStore.ReadInfo(path);
        Assert.True(info.Complete);
        Assert.Equal(frames, info.Frames);
        Assert.StartsWith("recovered", info.EndReason);
        Assert.Contains("F-16C_50 block 50", info.Aircraft);
        Assert.Empty(TelemetrySessionStore.Recover(_folder));     // only once
        using (var reader = new TelemetrySessionReader(path))
        {
            while (reader.Next())
            {
            }

            Assert.False(reader.Truncated);                       // the damaged tail is gone
        }
    }

    [Fact]
    public void Store_ListsResolvesAndProtectsTheSessionBeingWritten()
    {
        var older = WriteFlight(_folder, "a.jptel", created: new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
        var newer = WriteFlight(_folder, "b.jptel", created: new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        var live = Path.Combine(_folder, "c.jptel");
        using var writer = new TelemetrySessionWriter(live, Header(created: new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc)));
        writer.Frame(0, new Dictionary<string, double> { ["x"] = 1 });
        writer.Flush();

        var list = TelemetrySessionStore.List(_folder);
        Assert.Equal([live, newer, older], list.Select(s => s.Path));
        Assert.True(list[0].InUse);
        Assert.Equal("recording", list[0].EndReason);
        Assert.Equal(newer, TelemetrySessionStore.Resolve(_folder, "2"));
        Assert.Equal(live, TelemetrySessionStore.Resolve(_folder, "latest"));
        Assert.Equal(older, TelemetrySessionStore.Resolve(_folder, "a"));
        Assert.Null(TelemetrySessionStore.Resolve(_folder, "9"));

        var problems = new List<string>();
        Assert.False(TelemetrySessionStore.Delete(live, problems));
        Assert.Single(problems);
        Assert.Empty(TelemetrySessionStore.Recover(_folder));       // the live one is not "recovered"

        // Retention: older than 30 days (relative to 2026-10-18) -> a.jptel; then the size cap -> b.jptel; never c.
        var deleted = TelemetrySessionStore.Prune(_folder, 0, 30, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal([older], deleted);
        deleted = TelemetrySessionStore.Prune(_folder, 1, 0, DateTime.UtcNow);
        Assert.Equal([newer], deleted);
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void NewSessionPath_IsUniqueAndSafe()
    {
        var start = new DateTime(2026, 10, 18, 14, 3, 22);
        var first = TelemetrySessionStore.NewSessionPath(_folder, "DCS", start);
        Assert.Equal("2026-10-18_14-03-22_DCS.jptel", Path.GetFileName(first));
        File.WriteAllText(first, "");
        Assert.Equal("2026-10-18_14-03-22_DCS_2.jptel", Path.GetFileName(TelemetrySessionStore.NewSessionPath(_folder, "DCS", start)));
        Assert.Equal("a_b_c", TelemetrySessionStore.SafeName("a/b:c"));
    }

    // ---------------------------------------------------------------- ACMI 2.2

    internal static void AssertValidAcmi(string text, out List<string> objectLines)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        // Header: the two mandatory lines first, in this order.
        Assert.Equal("FileType=text/acmi/tacview", lines[0]);
        Assert.Equal("FileVersion=2.2", lines[1]);
        Assert.Matches(new Regex(@"^0,ReferenceTime=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$"), lines.Single(l => l.StartsWith("0,ReferenceTime=", StringComparison.Ordinal)));
        Assert.Matches(new Regex(@"^0,ReferenceLongitude=-?\d+$"), lines.Single(l => l.StartsWith("0,ReferenceLongitude=", StringComparison.Ordinal)));
        Assert.Matches(new Regex(@"^0,ReferenceLatitude=-?\d+$"), lines.Single(l => l.StartsWith("0,ReferenceLatitude=", StringComparison.Ordinal)));
        var lastTime = double.NegativeInfinity;
        var seenFrame = false;
        objectLines = [];
        var removed = new HashSet<string>();
        foreach (var line in lines.Skip(2))
        {
            if (line.StartsWith('#'))
            {
                var t = double.Parse(line[1..], CultureInfo.InvariantCulture);
                Assert.True(t >= lastTime, "time frames must not go back: " + line);
                lastTime = t;
                seenFrame = true;
                continue;
            }

            if (line.StartsWith("0,", StringComparison.Ordinal))
                continue;
            Assert.True(seenFrame, "an object line before the first time frame: " + line);
            if (line.StartsWith('-'))
            {
                Assert.Matches(new Regex("^-[0-9A-F]+$"), line);
                removed.Add(line[1..]);
                continue;
            }

            // <hex id>,prop=value,... (commas inside values escaped)
            Assert.Matches(new Regex(@"^[0-9A-F]+,"), line);
            var id = line[..line.IndexOf(',')];
            Assert.NotEqual("0", id);
            foreach (var prop in Regex.Split(line[(id.Length + 1)..], @"(?<!\\),"))
            {
                Assert.Contains('=', prop);
                if (prop.StartsWith("T=", StringComparison.Ordinal))
                {
                    var count = prop[2..].Split('|').Length;
                    Assert.Contains(count, new[] { 3, 5, 6, 9 });
                }
            }

            objectLines.Add(line);
        }

        Assert.True(seenFrame);
    }

    [Fact]
    public void Acmi_Export_FollowsTheFormat()
    {
        var path = WriteFlight(_folder);
        var acmi = Path.Combine(_folder, "out.txt.acmi");
        var result = TelemetryAcmiExport.Export(path, acmi, zip: false, maxHz: 5);
        var bytes = File.ReadAllBytes(acmi);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);     // UTF-8 with BOM, as Tacview writes it
        var text = Encoding.UTF8.GetString(bytes[3..]);
        AssertValidAcmi(text, out var objects);
        Assert.Contains("0,ReferenceTime=2026-10-18T12:00:00Z", text);
        Assert.Contains("0,ReferenceLongitude=41", text);
        Assert.Contains("0,ReferenceLatitude=41", text);
        Assert.Contains("0,DataSource=DCS World", text);
        Assert.Contains("0,Title=DCS World - F-16C_50", text);

        var player = objects.Where(l => l.StartsWith(TelemetryAcmiExport.PlayerId + ",", StringComparison.Ordinal)).ToList();
        Assert.Contains("Type=Air+FixedWing", player[0]);
        Assert.Contains("Name=F-16C_50", player[0]);
        // T = lon|lat|alt|roll|pitch|yaw, longitude and latitude relative to the reference.
        var first = Regex.Match(player[0], @"T=([^,]*)").Groups[1].Value.Split('|');
        Assert.Equal(6, first.Length);
        Assert.Equal(0.9, double.Parse(first[0], CultureInfo.InvariantCulture), 6);
        Assert.Equal(0.5, double.Parse(first[1], CultureInfo.InvariantCulture), 6);
        Assert.Equal(5000 * 0.3048, double.Parse(first[2], CultureInfo.InvariantCulture), 1);
        Assert.Equal(45, double.Parse(first[5], CultureInfo.InvariantCulture), 1);
        Assert.Contains("IAS=150", player[0]);
        // At most 5 time frames a second (+ events), 120 s.
        Assert.InRange(result.Rows, 500, 602);
        Assert.InRange(text.Split('\n').Count(l => l.StartsWith('#')), 500, 610);

        // The other objects: own ids, a type and coalition when they appear, removed when they are gone.
        var mig = objects.First(l => l.Contains("Name=MiG-29A", StringComparison.Ordinal));
        Assert.Contains("Type=Air+FixedWing", mig);
        Assert.Contains("Color=Red", mig);
        var tank = objects.First(l => l.Contains("Name=T-72B", StringComparison.Ordinal));
        var tankId = tank[..tank.IndexOf(',')];
        Assert.Contains("\n-" + tankId + "\n", text.Replace("\r\n", "\n"));
        Assert.Equal(2, result.Objects);
        // Events: a message (commas escaped, the line break gone) and a bookmark for the aircraft change.
        Assert.Contains(@"0,Event=Message|A0001|Bandit\, bandit\, 12 o'clock high\, 5 miles", text);
        Assert.Contains("0,Event=Bookmark|A0001|Aircraft: F-16C_50 block 50", text);
    }

    [Fact]
    public void Acmi_Zip_HoldsOneTextAcmi()
    {
        var path = WriteFlight(_folder);
        var zip = Path.Combine(_folder, "flight.zip.acmi");
        TelemetryAcmiExport.Export(path, zip, zip: true);
        using var archive = ZipFile.OpenRead(zip);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("flight.txt.acmi", entry.Name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        AssertValidAcmi(reader.ReadToEnd(), out var objects);
        Assert.NotEmpty(objects);
    }

    [Fact]
    public void Acmi_FlatWorld_UsesUvAndTheReferencePoint()
    {
        var path = WriteFlight(_folder, geo: false);
        var acmi = Path.Combine(_folder, "flat.txt.acmi");
        TelemetryAcmiExport.Export(path, acmi, zip: false);
        var text = File.ReadAllText(acmi);
        AssertValidAcmi(text, out var objects);
        var last = objects.Last(l => l.StartsWith(TelemetryAcmiExport.PlayerId + ",", StringComparison.Ordinal) && l.Contains("T=", StringComparison.Ordinal));
        var t = Regex.Match(last, @"T=([^,]*)").Groups[1].Value.Split('|');
        Assert.Equal(9, t.Length);
        Assert.Equal(12000, double.Parse(t[6], CultureInfo.InvariantCulture), 1);    // u = x
        Assert.Equal(6000, double.Parse(t[7], CultureInfo.InvariantCulture), 1);     // v = y
        // 6000 m north of 0/0 is about 0.0539 degrees.
        Assert.Equal(6000 / 111319.49, double.Parse(t[1], CultureInfo.InvariantCulture), 4);
    }

    [Fact]
    public void Acmi_RefusesARecordingWithoutPosition()
    {
        var path = Path.Combine(_folder, "nopos.jptel");
        using (var writer = new TelemetrySessionWriter(path, new Dictionary<string, string> { ["game"] = "IL2Game" }))
        {
            writer.Frame(0, new Dictionary<string, double> { ["il2.eas"] = 80 });
            writer.Finish("x", DateTime.UtcNow);
        }

        Assert.Throws<InvalidOperationException>(() => TelemetryAcmiExport.Export(path, Path.Combine(_folder, "x.acmi"), false));
    }

    [Fact]
    public void Acmi_MappedButNeverSent_IsNotOffered()
    {
        // DCS maps lat/lon, but an old hook (or a server forbidding export) sends none: no ACMI, no empty file.
        var path = Path.Combine(_folder, "nolatlon.jptel");
        using (var writer = new TelemetrySessionWriter(path, Header()))
        {
            for (var i = 0; i < 50; i++)
                writer.Frame(i * 0.1, new Dictionary<string, double> { ["t.alt_ft"] = 1000, ["t.ias"] = 100 });
            writer.Finish("x", DateTime.UtcNow);
        }

        var info = TelemetrySessionStore.ReadInfo(path);
        Assert.Equal("0", info.Summary["positionFrames"]);
        Assert.False(info.HasPosition);
        Assert.True(TelemetrySessionStore.ReadInfo(WriteFlight(_folder)).HasPosition);
        var target = Path.Combine(_folder, "x.zip.acmi");
        Assert.Throws<InvalidOperationException>(() => TelemetryAcmiExport.Export(path, target, true));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"));
        // The preview skips the mapped channels without values.
        Assert.Equal(["t.alt_ft", "t.ias"], TelemetryPreview.Read(path, null, 50).Select(s => s.Key));
    }

    // ---------------------------------------------------------------- CSV and preview

    [Fact]
    public void Csv_Export_WritesTableEventsChannelsAndOneFilePerChannel()
    {
        var path = WriteFlight(_folder);
        var outFolder = Path.Combine(_folder, "csv");
        var result = TelemetryCsvExport.Export(path, outFolder);
        var table = File.ReadAllLines(Path.Combine(outFolder, "flight.csv"));
        Assert.StartsWith("time_s,t.lat [deg],t.alt_ft [ft],", table[0]);
        Assert.Equal(1202, table.Length);
        Assert.Equal(1201, result.Rows);
        var header = table[0].Split(',');
        var row = table[601].Split(',');
        Assert.Equal("60", row[0]);
        Assert.Equal("5600", row[Array.IndexOf(header, "t.alt_ft [ft]")]);
        Assert.Equal("0", row[Array.IndexOf(header, "t.gear")]);
        Assert.Equal("", table[1101].Split(',')[Array.IndexOf(header, "t.gear")]);     // gone after 100 s
        var events = File.ReadAllText(Path.Combine(outFolder, "flight-events.csv"));
        Assert.Contains("30,message,\"Bandit, bandit, 12 o'clock\nhigh, 5 miles\"", events.Replace("\r\n", "\n"));
        var channels = File.ReadAllLines(Path.Combine(outFolder, "flight-channels.csv"));
        Assert.Equal("index,key,title,unit,category,min,max", channels[0]);
        Assert.Contains("0,t.lat,Latitude,deg,Position,-90,90", channels);
        var gear = File.ReadAllLines(Path.Combine(outFolder, "flight-channels", "t.gear.csv"));
        Assert.Equal(["time_s,t.gear", "0,1", "60,0", "100,"], gear);
        Assert.True(File.Exists(Path.Combine(outFolder, "flight-channels", "t.ias.csv")));
    }

    [Fact]
    public void Preview_ReducesChannelsKeepingPeaks()
    {
        var path = WriteFlight(_folder);
        var series = TelemetryPreview.Read(path, null, 100);
        Assert.Equal(["t.alt_ft", "t.ias", "t.pitch"], series.Select(s => s.Key));
        Assert.All(series, s => Assert.InRange(s.Times.Count, 2, 100));
        var alt = series[0];
        Assert.Equal(5000, alt.Min);
        Assert.Equal(6200, alt.Max);
        var bank = TelemetryPreview.Read(path, ["t.bank"], 60).Single();
        Assert.Equal(0.5 * Math.Sin(12) , bank.Values.Last(), 1);
        Assert.InRange(bank.Max, 0.49, 0.5);
    }
}
