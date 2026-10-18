#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace JoyPro.TelemetrySessions
{
    public sealed class TelemetryExportResult
    {
        public List<string> Files = new List<string>();
        public long Frames;
        public long Rows;
        public int Objects;
        public List<string> Warnings = new List<string>();
    }

    // Tacview ACMI 2.2 text export (the openly documented format, https://www.tacview.net/documentation/acmi/):
    //   FileType=text/acmi/tacview / FileVersion=2.2 header, global object 0 (ReferenceTime, ReferenceLongitude/Latitude,
    //   DataSource, DataRecorder, Title, RecordingTime), "#<seconds>" time frames relative to ReferenceTime, objects with
    //   hexadecimal ids ("<id>,T=lon|lat|alt|roll|pitch|yaw,Name=...,Type=..."), "-<id>" when an object is gone,
    //   "0,Event=Message|<id>|text" / "Bookmark" events. Longitudes and latitudes are written relative to the reference
    //   (integer degrees), as the format recommends. Flat-world games (only u/v metres) are placed around their
    //   reference point (ref_lat/ref_lon, else 0/0) and also get U/V: T=lon|lat|alt|roll|pitch|yaw|u|v|heading.
    public static class TelemetryAcmiExport
    {
        public const string PlayerId = "A0001";
        public const double DefaultMaxHz = 20;
        private const double MetresPerDegree = 111319.49079327357;

        public static TelemetryExportResult Export(string sessionPath, string outPath, bool zip, double maxHz = DefaultMaxHz)
        {
            TelemetrySessionInfo info = TelemetrySessionStore.ReadInfo(sessionPath);
            if (!info.HasPosition)
                throw new InvalidOperationException("This recording has no position (" + info.GameTitle + " does not send one): ACMI needs one. Export CSV instead.");
            TelemetryExportResult result = new TelemetryExportResult();
            string directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            Directory.CreateDirectory(directory);
            string temp = outPath + ".tmp";
            try
            {
            using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (zip)
                {
                    using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create, true))
                    {
                        string inner = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(outPath)) + ".txt.acmi";
                        ZipArchiveEntry entry = archive.CreateEntry(inner, CompressionLevel.Optimal);
                        using (Stream stream = entry.Open())
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(true)))
                            Write(info, writer, maxHz, result);
                    }
                }
                else
                {
                    using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(true), 64 * 1024, true))
                        Write(info, writer, maxHz, result);
                }
            }
            }
            catch
            {
                if (File.Exists(temp))
                    File.Delete(temp);
                throw;
            }
            if (File.Exists(outPath))
                File.Delete(outPath);
            File.Move(temp, outPath);
            result.Files.Add(outPath);
            return result;
        }

        // The ACMI text of a session (tests and the export).
        public static void Write(TelemetrySessionInfo info, TextWriter w, double maxHz, TelemetryExportResult result)
        {
            w.NewLine = "\n";
            Dictionary<string, TelemetrySessionNav> nav = info.Nav.GroupBy(n => n.Target).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            bool geo = nav.ContainsKey("lat") && nav.ContainsKey("lon");
            double refLat = 0, refLon = 0;
            double parsed;
            if (TelemetrySessionFormat.TryParseNumber(info.Info("ref_lat"), out parsed))
                refLat = parsed;
            if (TelemetrySessionFormat.TryParseNumber(info.Info("ref_lon"), out parsed))
                refLon = parsed;
            double minInterval = maxHz > 0 ? 1.0 / maxHz : 0;

            using (TelemetrySessionReader reader = new TelemetrySessionReader(info.Path))
            {
                // The reference point: the first position, rounded down to whole degrees.
                double firstLat = double.NaN, firstLon = double.NaN;
                if (geo)
                {
                    using (TelemetrySessionReader probe = new TelemetrySessionReader(info.Path))
                    {
                        while (probe.Next())
                        {
                            if (probe.Kind != TelemetrySessionReader.RecordKind.Frame)
                                continue;
                            double la = Nav(probe, nav, "lat"), lo = Nav(probe, nav, "lon");
                            if (!double.IsNaN(la) && !double.IsNaN(lo) && (la != 0 || lo != 0))
                            {
                                firstLat = la;
                                firstLon = lo;
                                break;
                            }
                        }
                    }
                }
                double refLonOut = geo ? (double.IsNaN(firstLon) ? 0 : Math.Floor(firstLon)) : Math.Floor(refLon);
                double refLatOut = geo ? (double.IsNaN(firstLat) ? 0 : Math.Floor(firstLat)) : Math.Floor(refLat);

                string aircraft = info.Info("aircraft");
                if (aircraft.Length == 0 && info.Aircraft.Count > 0)
                    aircraft = info.Aircraft[0];
                string title = info.GameTitle + (aircraft.Length > 0 ? " - " + aircraft : string.Empty);
                w.WriteLine("FileType=text/acmi/tacview");
                w.WriteLine("FileVersion=2.2");
                // ReferenceTime in whole seconds (as the format's examples); the session's start fraction moves the frames.
                DateTime created = info.CreatedUtc == DateTime.MinValue ? DateTime.UtcNow : info.CreatedUtc;
                DateTime reference = created.AddTicks(-(created.Ticks % TimeSpan.TicksPerSecond));
                double shift = (created - reference).TotalSeconds;
                w.WriteLine("0,ReferenceTime=" + Iso(reference));
                w.WriteLine("0,RecordingTime=" + Iso(DateTime.UtcNow));
                w.WriteLine("0,DataSource=" + Text(info.GameTitle));
                w.WriteLine("0,DataRecorder=JoyPro telemetry recording");
                w.WriteLine("0,Title=" + Text(title));
                if (info.Info("map").Length > 0)
                    w.WriteLine("0,Comments=" + Text("Map: " + info.Info("map")));
                w.WriteLine("0,ReferenceLongitude=" + Num(refLonOut, 0));
                w.WriteLine("0,ReferenceLatitude=" + Num(refLatOut, 0));

                Dictionary<string, string> objectIds = new Dictionary<string, string>(StringComparer.Ordinal);
                HashSet<string> alive = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, string> lastProps = new Dictionary<string, string>(StringComparer.Ordinal);
                int nextObject = 0x1001;
                double lastFrameTime = double.NegativeInfinity;
                string currentTimeLine = null;
                bool playerShown = false;
                string lastT = null;
                string pendingName = aircraft;
                string pilot = info.Info("pilot");

                Action<double> time = t =>
                {
                    string line = "#" + Num(t + shift, 2);
                    if (line != currentTimeLine)
                    {
                        w.WriteLine(line);
                        currentTimeLine = line;
                    }
                };

                while (reader.Next())
                {
                    if (reader.Kind == TelemetrySessionReader.RecordKind.Frame)
                    {
                        result.Frames++;
                        if (reader.Time - lastFrameTime < minInterval - 1e-9)
                            continue;
                        double lat, lon, u = double.NaN, v = double.NaN;
                        if (geo)
                        {
                            lat = Nav(reader, nav, "lat");
                            lon = Nav(reader, nav, "lon");
                            if (double.IsNaN(lat) || double.IsNaN(lon) || (lat == 0 && lon == 0))
                                continue;
                        }
                        else
                        {
                            u = Nav(reader, nav, "u");
                            v = Nav(reader, nav, "v");
                            if (double.IsNaN(u) || double.IsNaN(v))
                                continue;
                            lat = refLat + v / MetresPerDegree;
                            lon = refLon + u / (MetresPerDegree * Math.Max(0.01, Math.Cos(refLat * Math.PI / 180)));
                        }
                        double alt = Nav(reader, nav, "alt");
                        double roll = Nav(reader, nav, "roll"), pitch = Nav(reader, nav, "pitch"), yaw = Nav(reader, nav, "yaw");
                        double heading = Nav(reader, nav, "heading");
                        bool attitude = !double.IsNaN(roll) || !double.IsNaN(pitch) || !double.IsNaN(yaw);
                        StringBuilder t = new StringBuilder("T=");
                        t.Append(Num(lon - refLonOut, 7)).Append('|').Append(Num(lat - refLatOut, 7)).Append('|').Append(double.IsNaN(alt) ? "0" : Num(alt, 2));
                        if (!geo)
                        {
                            t.Append('|').Append(Opt(roll, 1)).Append('|').Append(Opt(pitch, 1)).Append('|').Append(Opt(Norm360(yaw), 1))
                             .Append('|').Append(Num(u, 1)).Append('|').Append(Num(v, 1)).Append('|').Append(Opt(Norm360(double.IsNaN(heading) ? yaw : heading), 1));
                        }
                        else if (attitude)
                        {
                            t.Append('|').Append(Opt(roll, 1)).Append('|').Append(Opt(pitch, 1)).Append('|').Append(Opt(Norm360(yaw), 1));
                        }
                        List<string> parts = new List<string>();
                        string tText = t.ToString();
                        if (tText != lastT)
                            parts.Add(tText);
                        Prop(parts, lastProps, "IAS", Nav(reader, nav, "ias"), 1);
                        Prop(parts, lastProps, "TAS", Nav(reader, nav, "tas"), 1);
                        Prop(parts, lastProps, "Mach", Nav(reader, nav, "mach"), 3);
                        Prop(parts, lastProps, "AOA", Nav(reader, nav, "aoa"), 1);
                        Prop(parts, lastProps, "AOS", Nav(reader, nav, "aos"), 1);
                        Prop(parts, lastProps, "AGL", Nav(reader, nav, "agl"), 1);
                        Prop(parts, lastProps, "HDG", Norm360(double.IsNaN(heading) ? yaw : heading), 1);
                        Prop(parts, lastProps, "Throttle", Nav(reader, nav, "throttle"), 2);
                        Prop(parts, lastProps, "LandingGear", Nav(reader, nav, "gear"), 2);
                        Prop(parts, lastProps, "Flaps", Nav(reader, nav, "flaps"), 2);
                        Prop(parts, lastProps, "AirBrakes", Nav(reader, nav, "airbrakes"), 2);
                        Prop(parts, lastProps, "FuelWeight", Nav(reader, nav, "fuel_kg"), 0);
                        Prop(parts, lastProps, "VerticalGForce", Nav(reader, nav, "g"), 2);
                        Prop(parts, lastProps, "LongitudinalGForce", Nav(reader, nav, "gx"), 2);
                        Prop(parts, lastProps, "LateralGForce", Nav(reader, nav, "gz"), 2);
                        if (!playerShown)
                        {
                            parts.Add("Type=Air+FixedWing");
                            parts.Add("Color=Blue");
                            if (pilot.Length > 0)
                                parts.Add("Pilot=" + Text(pilot));
                            playerShown = true;
                            pendingName = string.IsNullOrEmpty(pendingName) ? info.GameTitle : pendingName;
                        }
                        if (pendingName != null)
                        {
                            parts.Add("Name=" + Text(pendingName));
                            pendingName = null;
                        }
                        if (parts.Count == 0)
                            continue;
                        time(reader.Time);
                        w.WriteLine(PlayerId + "," + string.Join(",", parts));
                        lastT = tText;
                        lastFrameTime = reader.Time;
                        result.Rows++;
                    }
                    else if (reader.Kind == TelemetrySessionReader.RecordKind.Event)
                    {
                        string kind = reader.EventKind;
                        if (kind == "aircraft" && reader.EventText.Length > 0)
                        {
                            if (playerShown)
                            {
                                time(reader.Time);
                                w.WriteLine(PlayerId + ",Name=" + Text(reader.EventText));
                                w.WriteLine("0,Event=Bookmark|" + PlayerId + "|" + Text("Aircraft: " + reader.EventText));
                            }
                            else
                            {
                                pendingName = reader.EventText;
                            }
                        }
                        else if (kind == "map" && reader.EventText.Length > 0)
                        {
                            time(reader.Time);
                            w.WriteLine("0,Event=Bookmark|" + Text("Map: " + reader.EventText));
                        }
                        else if (kind == "message" && reader.EventText.Length > 0)
                        {
                            time(reader.Time);
                            w.WriteLine("0,Event=Message|" + PlayerId + "|" + Text(reader.EventText));
                        }
                        else if (kind == "session_end")
                        {
                            time(reader.Time);
                            w.WriteLine("0,Event=Bookmark|" + Text("End: " + reader.EventText));
                        }
                        else if (kind == "objects")
                        {
                            WriteObjects(w, reader, time, objectIds, alive, ref nextObject, geo, refLat, refLon, refLatOut, refLonOut, result);
                        }
                    }
                }
                if (!playerShown)
                    throw new InvalidOperationException("This recording has no frame with a position (the game sent none): ACMI needs one. Export CSV instead.");
            }
        }

        private static void WriteObjects(TextWriter w, TelemetrySessionReader reader, Action<double> time, Dictionary<string, string> ids,
            HashSet<string> alive, ref int next, bool geo, double refLat, double refLon, double refLatOut, double refLonOut, TelemetryExportResult result)
        {
            HashSet<string> now = new HashSet<string>(StringComparer.Ordinal);
            List<string> lines = new List<string>();
            foreach (string raw in reader.EventText.Split('\n'))
            {
                string[] f = raw.Split('|');
                if (f.Length < 7 || f[0].Length == 0)
                    continue;
                double lat = ParseOr(f, 4), lon = ParseOr(f, 5), alt = ParseOr(f, 6);
                double roll = ParseOr(f, 7), pitch = ParseOr(f, 8), yaw = ParseOr(f, 9), u = ParseOr(f, 10), v = ParseOr(f, 11);
                if (double.IsNaN(lat) || double.IsNaN(lon))
                {
                    if (double.IsNaN(u) || double.IsNaN(v))
                        continue;
                    lat = refLat + v / MetresPerDegree;
                    lon = refLon + u / (MetresPerDegree * Math.Max(0.01, Math.Cos(refLat * Math.PI / 180)));
                }
                string id;
                bool fresh = false;
                if (!ids.TryGetValue(f[0], out id))
                {
                    id = (next++).ToString("X", CultureInfo.InvariantCulture);
                    ids[f[0]] = id;
                    result.Objects++;
                }
                if (!alive.Contains(id))
                    fresh = true;
                now.Add(id);
                StringBuilder sb = new StringBuilder(id).Append(",T=");
                sb.Append(Num(lon - refLonOut, 7)).Append('|').Append(Num(lat - refLatOut, 7)).Append('|').Append(double.IsNaN(alt) ? "0" : Num(alt, 1));
                bool flat = !double.IsNaN(u) && !double.IsNaN(v) && !geo;
                if (flat)
                    sb.Append('|').Append(Opt(roll, 1)).Append('|').Append(Opt(pitch, 1)).Append('|').Append(Opt(Norm360(yaw), 1)).Append('|').Append(Num(u, 1)).Append('|').Append(Num(v, 1)).Append('|');
                else if (!double.IsNaN(roll) || !double.IsNaN(pitch) || !double.IsNaN(yaw))
                    sb.Append('|').Append(Opt(roll, 1)).Append('|').Append(Opt(pitch, 1)).Append('|').Append(Opt(Norm360(yaw), 1));
                if (fresh)
                {
                    if (f[1].Length > 0)
                        sb.Append(",Type=").Append(Text(f[1]));
                    if (f.Length > 2 && f[2].Length > 0)
                        sb.Append(",Name=").Append(Text(f[2]));
                    if (f.Length > 3 && f[3].Length > 0)
                        sb.Append(",Coalition=").Append(Text(f[3])).Append(",Color=").Append(ColorFor(f[3]));
                }
                lines.Add(sb.ToString());
            }
            List<string> gone = alive.Where(a => !now.Contains(a)).ToList();
            if (lines.Count == 0 && gone.Count == 0)
                return;
            time(reader.Time);
            foreach (string line in lines)
                w.WriteLine(line);
            foreach (string id in gone)
            {
                w.WriteLine("-" + id);
                alive.Remove(id);
            }
            foreach (string id in now)
                alive.Add(id);
        }

        public static string ColorFor(string coalition)
        {
            string c = (coalition ?? string.Empty).ToLowerInvariant();
            if (c.Contains("red") || c.Contains("enem") || c.Contains("hostile") || c.Contains("axis"))
                return "Red";
            if (c.Contains("blue") || c.Contains("all") || c.Contains("friend"))
                return "Blue";
            if (c.Contains("green"))
                return "Green";
            return "Grey";
        }

        private static double ParseOr(string[] f, int i)
        {
            double value;
            return i < f.Length && f[i].Length > 0 && TelemetrySessionFormat.TryParseNumber(f[i], out value) ? value : double.NaN;
        }

        private static double Nav(TelemetrySessionReader reader, Dictionary<string, TelemetrySessionNav> nav, string target)
        {
            TelemetrySessionNav map;
            if (!nav.TryGetValue(target, out map))
                return double.NaN;
            double raw = reader.Value(reader.ChannelIndex(map.Key));
            return double.IsNaN(raw) ? double.NaN : raw * map.Scale + map.Offset;
        }

        private static void Prop(List<string> parts, Dictionary<string, string> last, string name, double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;
            string text = Num(value, decimals);
            string previous;
            if (last.TryGetValue(name, out previous) && previous == text)
                return;
            last[name] = text;
            parts.Add(name + "=" + text);
        }

        private static double Norm360(double degrees)
        {
            if (double.IsNaN(degrees))
                return double.NaN;
            double d = degrees % 360;
            return d < 0 ? d + 360 : d;
        }

        private static string Opt(double value, int decimals)
        {
            return double.IsNaN(value) ? string.Empty : Num(value, decimals);
        }

        public static string Num(double value, int decimals)
        {
            string format = decimals <= 0 ? "0" : "0." + new string('#', decimals);
            string text = Math.Round(value, Math.Max(0, decimals)).ToString(format, CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        private static string Iso(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        // ACMI property values: commas are escaped with a backslash, line breaks become spaces.
        public static string Text(string value)
        {
            return (value ?? string.Empty).Replace("\\", "/").Replace(",", "\\,").Replace("\r", " ").Replace("\n", " ");
        }
    }

    // CSV export: one table with every channel as a column (one row per frame), the channel list, the events, and one
    // small file per channel (time and value at every change). Comma separated, '.' decimals, UTF-8 (RFC 4180).
    public static class TelemetryCsvExport
    {
        public static TelemetryExportResult Export(string sessionPath, string outFolder, bool perChannel = true, double maxHz = 0)
        {
            TelemetryExportResult result = new TelemetryExportResult();
            Directory.CreateDirectory(outFolder);
            string stem = Path.GetFileNameWithoutExtension(sessionPath);
            // Pass 1: the channels (a channel may first appear late in the session).
            List<TelemetrySessionChannel> channels;
            using (TelemetrySessionReader probe = new TelemetrySessionReader(sessionPath))
            {
                while (probe.Next())
                {
                }
                channels = probe.Channels.Where(c => c != null).ToList();
            }
            UTF8Encoding utf8 = new UTF8Encoding(false);
            string channelsPath = Path.Combine(outFolder, stem + "-channels.csv");
            using (StreamWriter writer = new StreamWriter(channelsPath, false, utf8))
            {
                writer.NewLine = "\r\n";
                writer.WriteLine("index,key,title,unit,category,min,max");
                foreach (TelemetrySessionChannel c in channels)
                    writer.WriteLine(string.Join(",", c.Index.ToString(CultureInfo.InvariantCulture), Csv(c.Key), Csv(c.Title), Csv(c.Unit), Csv(c.Category),
                        TelemetrySessionFormat.FormatNumber(c.Min), TelemetrySessionFormat.FormatNumber(c.Max)));
            }
            result.Files.Add(channelsPath);

            string tablePath = Path.Combine(outFolder, stem + ".csv");
            string eventsPath = Path.Combine(outFolder, stem + "-events.csv");
            string channelFolder = Path.Combine(outFolder, stem + "-channels");
            Dictionary<int, StreamWriter> perChannelWriters = new Dictionary<int, StreamWriter>();
            Dictionary<int, double> lastWritten = new Dictionary<int, double>();
            double minInterval = maxHz > 0 ? 1.0 / maxHz : 0;
            double lastRow = double.NegativeInfinity;
            try
            {
                using (StreamWriter table = new StreamWriter(tablePath, false, utf8, 64 * 1024))
                using (StreamWriter events = new StreamWriter(eventsPath, false, utf8))
                using (TelemetrySessionReader reader = new TelemetrySessionReader(sessionPath))
                {
                    table.NewLine = "\r\n";
                    events.NewLine = "\r\n";
                    table.WriteLine("time_s," + string.Join(",", channels.Select(c => Csv(c.Key + (c.Unit.Length > 0 ? " [" + c.Unit + "]" : string.Empty)))));
                    events.WriteLine("time_s,kind,text");
                    if (perChannel)
                        Directory.CreateDirectory(channelFolder);
                    StringBuilder row = new StringBuilder(4096);
                    while (reader.Next())
                    {
                        string t = reader.Time.ToString("0.###", CultureInfo.InvariantCulture);
                        if (reader.Kind == TelemetrySessionReader.RecordKind.Event)
                        {
                            events.WriteLine(t + "," + Csv(reader.EventKind) + "," + Csv(reader.EventText));
                            continue;
                        }
                        if (reader.Kind != TelemetrySessionReader.RecordKind.Frame)
                            continue;
                        result.Frames++;
                        if (perChannel)
                        {
                            foreach (int index in reader.Changed)
                            {
                                StreamWriter cw;
                                if (!perChannelWriters.TryGetValue(index, out cw))
                                {
                                    TelemetrySessionChannel c = index < reader.Channels.Count ? reader.Channels[index] : null;
                                    if (c == null)
                                        continue;
                                    string file = Path.Combine(channelFolder, TelemetrySessionStore.SafeName(c.Key) + ".csv");
                                    cw = new StreamWriter(file, false, utf8, 4096) { NewLine = "\r\n" };
                                    cw.WriteLine("time_s," + Csv(c.Key + (c.Unit.Length > 0 ? " [" + c.Unit + "]" : string.Empty)));
                                    perChannelWriters[index] = cw;
                                    result.Files.Add(file);
                                }
                                double value = reader.Value(index);
                                double written;
                                // Key frames repeat every value: only real changes go into the channel's file.
                                if (lastWritten.TryGetValue(index, out written) && (written == value || double.IsNaN(written) && double.IsNaN(value)))
                                    continue;
                                lastWritten[index] = value;
                                cw.WriteLine(t + "," + (double.IsNaN(value) ? string.Empty : TelemetrySessionFormat.FormatNumber(value)));
                            }
                        }
                        if (reader.Time - lastRow < minInterval - 1e-9)
                            continue;
                        lastRow = reader.Time;
                        row.Clear();
                        row.Append(t);
                        foreach (TelemetrySessionChannel c in channels)
                        {
                            row.Append(',');
                            double value = reader.Value(c.Index);
                            if (!double.IsNaN(value))
                                row.Append(TelemetrySessionFormat.FormatNumber(value));
                        }
                        table.WriteLine(row.ToString());
                        result.Rows++;
                    }
                }
            }
            finally
            {
                foreach (StreamWriter cw in perChannelWriters.Values)
                    cw.Dispose();
            }
            result.Files.Insert(0, tablePath);
            result.Files.Insert(1, eventsPath);
            return result;
        }

        public static string Csv(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }

    public sealed class TelemetryPreviewSeries
    {
        public string Key;
        public string Title;
        public string Unit;
        public List<double> Times = new List<double>();
        public List<double> Values = new List<double>();
        public double Min = double.NaN;
        public double Max = double.NaN;
    }

    // A few channels of a session, reduced to about `points` samples each (min and max per bucket, so peaks stay).
    public static class TelemetryPreview
    {
        // Channels a preview shows when none are asked for: altitude, speed, G, attitude, throttle; else the first ones.
        public static readonly string[] PreferredTargets = { "alt", "ias", "g", "pitch", "roll", "throttle", "aoa" };

        public static List<string> DefaultKeys(TelemetrySessionInfo info, IReadOnlyList<TelemetrySessionChannel> channels, int count)
        {
            List<string> keys = new List<string>();
            foreach (string target in PreferredTargets)
            {
                TelemetrySessionNav nav = info.Nav.FirstOrDefault(n => n.Target == target);
                if (nav != null && !keys.Contains(nav.Key) && channels.Any(c => c != null && c.Key == nav.Key))
                    keys.Add(nav.Key);
                if (keys.Count >= count)
                    return keys;
            }
            foreach (TelemetrySessionChannel c in channels)
            {
                if (keys.Count >= count)
                    break;
                if (c != null && !keys.Contains(c.Key) && !c.Key.EndsWith(".t", StringComparison.Ordinal) && c.Key != "dcs.t")
                    keys.Add(c.Key);
            }
            return keys;
        }

        public static List<TelemetryPreviewSeries> Read(string sessionPath, IList<string> keys, int points, int defaultCount = 3)
        {
            TelemetrySessionInfo info = TelemetrySessionStore.ReadInfo(sessionPath);
            double duration = Math.Max(info.Duration, 0.001);
            List<TelemetrySessionChannel> channels;
            using (TelemetrySessionReader probe = new TelemetrySessionReader(sessionPath))
            {
                while (probe.Next())
                {
                    if (probe.Kind == TelemetrySessionReader.RecordKind.Frame || probe.Kind == TelemetrySessionReader.RecordKind.Event)
                        duration = Math.Max(duration, probe.Time);
                }
                channels = probe.Channels.ToList();
            }
            bool chosen = keys != null && keys.Count > 0;
            // Without a choice: more candidates than shown, so a channel without values (no position in this game)
            // is replaced by the next one.
            List<string> wanted = chosen ? keys.ToList() : DefaultKeys(info, channels, Math.Max(defaultCount * 4, 12));
            List<TelemetryPreviewSeries> series = new List<TelemetryPreviewSeries>();
            int buckets = Math.Max(2, points / 2);
            List<double[]> lo = new List<double[]>(), hi = new List<double[]>();
            List<int> indexes = new List<int>();
            foreach (string key in wanted)
            {
                TelemetrySessionChannel c = channels.FirstOrDefault(x => x != null && x.Key == key);
                if (c == null)
                    continue;
                series.Add(new TelemetryPreviewSeries { Key = c.Key, Title = c.Title.Length > 0 ? c.Title : c.Key, Unit = c.Unit });
                indexes.Add(c.Index);
                lo.Add(Enumerable.Repeat(double.NaN, buckets).ToArray());
                hi.Add(Enumerable.Repeat(double.NaN, buckets).ToArray());
            }
            if (series.Count == 0)
                return series;
            using (TelemetrySessionReader reader = new TelemetrySessionReader(sessionPath))
            {
                while (reader.Next())
                {
                    if (reader.Kind != TelemetrySessionReader.RecordKind.Frame)
                        continue;
                    int b = Math.Min(buckets - 1, Math.Max(0, (int)(reader.Time / duration * buckets)));
                    for (int s = 0; s < series.Count; s++)
                    {
                        double value = reader.Value(indexes[s]);
                        if (double.IsNaN(value))
                            continue;
                        if (double.IsNaN(lo[s][b]) || value < lo[s][b])
                            lo[s][b] = value;
                        if (double.IsNaN(hi[s][b]) || value > hi[s][b])
                            hi[s][b] = value;
                    }
                }
            }
            for (int s = 0; s < series.Count; s++)
            {
                for (int b = 0; b < buckets; b++)
                {
                    if (double.IsNaN(lo[s][b]))
                        continue;
                    double t0 = duration * b / buckets, t1 = duration * (b + 0.5) / buckets;
                    series[s].Times.Add(t0);
                    series[s].Values.Add(lo[s][b]);
                    series[s].Times.Add(t1);
                    series[s].Values.Add(hi[s][b]);
                    series[s].Min = double.IsNaN(series[s].Min) ? lo[s][b] : Math.Min(series[s].Min, lo[s][b]);
                    series[s].Max = double.IsNaN(series[s].Max) ? hi[s][b] : Math.Max(series[s].Max, hi[s][b]);
                }
            }
            if (!chosen)
                series = series.Where(s => s.Values.Count > 0).Take(defaultCount).ToList();
            return series;
        }
    }
}
