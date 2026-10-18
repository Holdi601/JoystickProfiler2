using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JoyPro.Ffb.Core.Telemetry;
using JoyPro.Ffb.Core.Telemetry.XPlane;

namespace JoyPro.Ffb.Core.Tests;

public sealed class XPlaneTelemetryTests
{
    private static int IndexOf(string key) => XPlaneProtocol.Datarefs.Single(d => d.Key == key).Index;

    private static XPlaneTelemetrySource NewSource() => new(new ManualClock(5)) { MinFrameIntervalSeconds = 0 };

    // ------------------------------------------------------------------ packets

    [Fact]
    public void Request_is_413_bytes_RREF_null_freq_index_and_padded_path()
    {
        var packet = XPlaneProtocol.BuildRequest(20, 7, "sim/flightmodel/position/latitude");

        Assert.Equal(413, packet.Length);
        Assert.Equal("RREF\0"u8.ToArray(), packet[..5]);
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(9)));
        var path = "sim/flightmodel/position/latitude"u8.ToArray();
        Assert.Equal(path, packet[13..(13 + path.Length)]);
        Assert.All(packet[(13 + path.Length)..], b => Assert.Equal(0, b));

        Assert.True(XPlaneProtocol.TryParseRequest(packet, out var freq, out var index, out var parsed));
        Assert.Equal((20, 7, "sim/flightmodel/position/latitude"), (freq, index, parsed));

        var stop = XPlaneProtocol.BuildRequest(0, 7, "sim/flightmodel/position/latitude");
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(stop.AsSpan(5)));
        Assert.Throws<ArgumentException>(() => XPlaneProtocol.BuildRequest(1, 1, new string('a', 400)));
        Assert.Equal(413, XPlaneProtocol.BuildRequest(1, 1, new string('a', 399)).Length);
    }

    [Fact]
    public void Answer_parsing_reads_pairs_ignores_a_partial_tail_and_rejects_other_datagrams()
    {
        var answer = XPlaneProtocol.BuildAnswer([(3, 1.5f), (9, -2.25f), (1001, 67f)]);
        Assert.Equal(5 + 3 * 8, answer.Length);
        var pairs = new List<(int, float)>();
        Assert.Equal(3, XPlaneProtocol.ParseAnswer(answer, (i, v) => pairs.Add((i, v))));
        Assert.Equal([(3, 1.5f), (9, -2.25f), (1001, 67f)], pairs);

        // A truncated datagram: the complete pairs count, the 3 trailing bytes do not.
        pairs.Clear();
        Assert.Equal(2, XPlaneProtocol.ParseAnswer(answer.AsSpan(0, answer.Length - 5), (i, v) => pairs.Add((i, v))));
        Assert.Equal(2, pairs.Count);

        Assert.Equal(0, XPlaneProtocol.ParseAnswer("RREF,"u8, (_, _) => { }));
        Assert.Equal(-1, XPlaneProtocol.ParseAnswer("RRE"u8, (_, _) => { }));
        Assert.Equal(-1, XPlaneProtocol.ParseAnswer("DATA*\0\0\0\0"u8, (_, _) => { }));
    }

    [Fact]
    public void Catalogue_has_unique_indices_keys_and_valid_paths()
    {
        var datarefs = XPlaneProtocol.Datarefs;
        Assert.InRange(datarefs.Count, 150, 400);
        Assert.Equal(Enumerable.Range(0, datarefs.Count), datarefs.Select(d => d.Index));
        Assert.True(datarefs.Count < XPlaneProtocol.TextIndexBase);
        Assert.Equal(datarefs.Count, datarefs.Select(d => d.Key).Distinct().Count());
        Assert.All(datarefs, d => Assert.StartsWith("xp.", d.Key));
        Assert.All(datarefs, d => Assert.False(string.IsNullOrWhiteSpace(d.Category)));

        var subs = XPlaneProtocol.Subscriptions(20);
        Assert.Equal(subs.Count, subs.Select(s => s.Index).Distinct().Count());
        Assert.Equal(subs.Count, subs.Select(s => s.Path).Distinct().Count());
        Assert.All(subs, s => Assert.True(s.Path.Length < XPlaneProtocol.PathLength && s.Path.StartsWith("sim/", StringComparison.Ordinal), s.Path));
        Assert.All(subs, s => Assert.InRange(s.Frequency, 1, 20));
        Assert.Contains(subs, s => s.Path == "sim/flightmodel/position/latitude" && s.Frequency == 20);
        Assert.Contains(subs, s => s.Path == "sim/cockpit2/engine/indicators/N1_percent[3]");
        Assert.Contains(subs, s => s.Path == "sim/aircraft/view/acf_ICAO[0]" && s.Frequency == 1);
        Assert.Contains(subs, s => s.Path == "sim/cockpit2/tcas/targets/position/lat[1]" && s.Frequency == 2);
        Assert.DoesNotContain(subs, s => s.Path == "sim/cockpit2/tcas/targets/position/lat[0]");  // slot 0 = the user's aircraft

        // Everything X-Plane answers fits its packet limit in a few datagrams per update.
        var fast = datarefs.Count(d => d.Rate == XPlaneRate.Fast);
        Assert.InRange(fast, 100, 3 * XPlaneProtocol.MaxPairsPerDatagram);
    }

    [Fact]
    public void Nav_mappings_point_at_catalogue_keys()
    {
        var source = NewSource();
        var keys = source.Descriptors.Select(d => d.Key).ToHashSet();
        Assert.All(source.NavMappings, m => Assert.Contains(m.Key, keys));
        foreach (var target in new[] { TelemetryNav.Latitude, TelemetryNav.Longitude, TelemetryNav.Altitude, TelemetryNav.Roll, TelemetryNav.Pitch, TelemetryNav.Yaw,
                     TelemetryNav.Ias, TelemetryNav.Tas, TelemetryNav.Mach, TelemetryNav.Aoa, TelemetryNav.Aos, TelemetryNav.Agl, TelemetryNav.VerticalG,
                     TelemetryNav.Throttle, TelemetryNav.Gear, TelemetryNav.Flaps, TelemetryNav.Airbrakes, TelemetryNav.FuelKg, TelemetryNav.VerticalSpeed })
            Assert.Single(source.NavMappings, m => m.Target == target);
        Assert.Equal(TelemetryNav.KnotsToMetresPerSecond, source.NavMappings.Single(m => m.Target == TelemetryNav.Ias).Scale);
    }

    // ------------------------------------------------------------------ the source

    [Fact]
    public void Values_map_to_keys_and_a_set_split_over_183_pair_datagrams_is_one_frame()
    {
        var source = NewSource();
        var frames = new List<TelemetryFrame>();
        source.FrameReceived += frames.Add;

        var pairs = XPlaneProtocol.Datarefs.Where(d => d.Rate == XPlaneRate.Fast).Select(d => (d.Index, 0f)).ToList();
        Set(pairs, "xp.time", 1234.5f);
        Set(pairs, "xp.lat", 47.25f);
        Set(pairs, "xp.lon", 8.5f);
        Set(pairs, "xp.elevation", 1500f);
        Set(pairs, "xp.ias", 100f);
        Set(pairs, "xp.true_psi", 270f);
        Set(pairs, "xp.n1_2", 88f);
        pairs.Add((999, 1f));  // unknown index: ignored

        var first = XPlaneProtocol.BuildAnswer(pairs.Take(183).ToList());
        var rest = XPlaneProtocol.BuildAnswer(pairs.Skip(183).ToList());
        source.Ingest(first);
        Assert.Empty(frames);  // 183 pairs: more follow
        source.Ingest(rest);
        var frame = Assert.Single(frames);

        Assert.Equal(1234.5, frame.Time, 3);
        Assert.Equal(47.25, frame.Values["xp.lat"], 4);
        Assert.Equal(8.5, frame.Values["xp.lon"], 4);
        Assert.Equal(88, frame.Values["xp.n1_2"], 4);
        Assert.Equal(1, source.UnknownIndices);

        // The nav mapping turns knots into m/s.
        var ias = source.NavMappings.Single(m => m.Target == TelemetryNav.Ias);
        Assert.Equal(51.4444, frame.Values[ias.Key] * ias.Scale + ias.Offset, 3);
        var yaw = source.NavMappings.Single(m => m.Target == TelemetryNav.Yaw);
        Assert.Equal(270, frame.Values[yaw.Key]);

        // A small update (fewer than 183 pairs) is a frame of its own; values not in it keep their last value.
        source.Ingest(XPlaneProtocol.BuildAnswer([(IndexOf("xp.time"), 1234.55f), (IndexOf("xp.lat"), 47.26f)]));
        Assert.Equal(2, frames.Count);
        Assert.Equal(47.26, frames[1].Values["xp.lat"], 4);
        Assert.Equal(1500, frames[1].Values["xp.elevation"], 4);

        // Not RREF: rejected, no frame.
        source.Ingest("HELLO"u8);
        Assert.Equal(1, source.DatagramsRejected);
        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void Slow_only_datagrams_publish_no_frame_and_raw_input_is_copied_per_datagram()
    {
        var source = NewSource();
        var frames = 0;
        var raw = new List<TelemetryRawPacket>();
        source.FrameReceived += _ => frames++;
        source.RawReceived += raw.Add;

        source.Ingest(XPlaneProtocol.BuildAnswer([(IndexOf("xp.light_landing"), 1f)]));
        Assert.Equal(0, frames);
        source.Ingest(XPlaneProtocol.BuildAnswer([(IndexOf("xp.time"), 10f), (IndexOf("xp.agl"), 2f)]));
        Assert.Equal(1, frames);
        Assert.Equal(1, source.Latest!.Values["xp.light_landing"]);

        Assert.Equal(2, raw.Count);
        Assert.All(raw, r => Assert.Equal("udp", r.Stream));
        Assert.Equal(5 + 8, raw[0].Data.Length);
    }

    [Fact]
    public void Aircraft_name_icao_and_version_become_session_facts_once_stable()
    {
        var source = NewSource();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;

        var name = Text("name", "Cessna 172 SP");
        var icao = Text("icao", "C172");
        var tail = Text("tailnum", "N172SP");
        source.Ingest(XPlaneProtocol.BuildAnswer([.. name, .. icao, .. tail, (XPlaneProtocol.VersionIndex, 121001f)]));
        Assert.Null(source.Aircraft);  // one read is not trusted yet (bytes may straddle datagrams)
        source.Ingest(XPlaneProtocol.BuildAnswer([.. name, .. icao, .. tail]));
        Assert.Equal("Cessna 172 SP", source.Aircraft);
        Assert.Equal("C172", source.SessionInfo[XPlaneTelemetrySource.IcaoInfoKey]);
        Assert.Equal("N172SP", source.SessionInfo[XPlaneTelemetrySource.TailNumberInfoKey]);
        Assert.Equal("X-Plane build 121001", source.SessionInfo[TelemetryInfoKeys.GameVersion]);
        Assert.Single(events, e => e.Kind == TelemetryEventKinds.Aircraft && e.Text == "Cessna 172 SP");

        // Same name again: no new event. A new aircraft: one event once stable.
        source.Ingest(XPlaneProtocol.BuildAnswer(name));
        var other = Text("name", "F-14 Tomcat");
        source.Ingest(XPlaneProtocol.BuildAnswer(other));
        source.Ingest(XPlaneProtocol.BuildAnswer(other));
        Assert.Equal("F-14 Tomcat", source.Aircraft);
        Assert.Equal(2, events.Count(e => e.Kind == TelemetryEventKinds.Aircraft));

        // No UI name (older aircraft): the description is used.
        var old = NewSource();
        var descrip = Text("descrip", "Old Glider");
        old.Ingest(XPlaneProtocol.BuildAnswer(descrip));
        old.Ingest(XPlaneProtocol.BuildAnswer(descrip));
        Assert.Equal("Old Glider", old.Aircraft);
    }

    [Fact]
    public void Session_starts_with_the_first_frame_and_a_flight_reset_ends_it_pause_does_not()
    {
        var source = NewSource();
        var events = new List<TelemetryEvent>();
        source.EventRaised += events.Add;

        void Step(float time, float flightTime, float paused = 0, float crashed = 0) => source.Ingest(XPlaneProtocol.BuildAnswer(
            [(IndexOf("xp.time"), time), (IndexOf("xp.flight_time"), flightTime), (IndexOf("xp.paused"), paused), (IndexOf("xp.crashed"), crashed)]));

        Step(100, 50);
        Assert.Single(events, e => e.Kind == TelemetryEventKinds.SessionStart);
        Step(101, 51, paused: 1);
        Step(102, 51, paused: 1);
        Step(103, 52, crashed: 1);
        Step(104, 53, crashed: 1);
        Assert.DoesNotContain(events, e => e.Kind == TelemetryEventKinds.SessionEnd);
        Assert.Single(events, e => e.Kind == XPlaneTelemetrySource.CrashEvent);

        Step(110, 0.5f);  // a new flight was loaded
        Assert.Single(events, e => e.Kind == TelemetryEventKinds.SessionEnd && e.Text == "flight reset");
        Assert.Equal(2, events.Count(e => e.Kind == TelemetryEventKinds.SessionStart));
        Assert.Equal(110, source.Latest!.Time);
    }

    [Fact]
    public void Traffic_becomes_an_objects_snapshot_and_an_empty_one_when_it_is_gone()
    {
        var source = NewSource();
        var objects = new List<string>();
        source.EventRaised += e =>
        {
            if (e.Kind == TelemetryEventKinds.Objects)
                objects.Add(e.Text);
        };

        // tcas_num_acf = 3: the user + targets 1 and 2. Target 3 holds stale numbers and is ignored.
        var pairs = new List<(int, float)> { (XPlaneProtocol.TrafficIndexBase, 3f) };
        void Target(int t, float lat, float lon, float ele, float psi, float modeS)
        {
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 0), lat));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 1), lon));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 2), ele));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 3), psi));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 4), 2f));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 5), -10f));
            pairs.Add((XPlaneProtocol.TrafficIndex(t, 6), modeS));
        }

        Target(1, 47.5f, 8.25f, 900f, 45f, 0xABCDEF);
        Target(2, 47.75f, 8.5f, 1200f, 180f, 0);
        Target(3, 10f, 10f, 10f, 0f, 5);
        source.Ingest(XPlaneProtocol.BuildAnswer(pairs));

        var lines = Assert.Single(objects).Split('\n');
        Assert.Equal(2, lines.Length);
        var first = lines[0].Split('|');
        Assert.Equal("ABCDEF", first[0]);
        Assert.Equal("Air+FixedWing", first[1]);
        Assert.Equal(47.5, double.Parse(first[4], CultureInfo.InvariantCulture), 4);
        Assert.Equal(8.25, double.Parse(first[5], CultureInfo.InvariantCulture), 4);
        Assert.Equal(900, double.Parse(first[6], CultureInfo.InvariantCulture), 2);
        Assert.Equal(-10, double.Parse(first[7], CultureInfo.InvariantCulture), 2);  // roll
        Assert.Equal(2, double.Parse(first[8], CultureInfo.InvariantCulture), 2);    // pitch
        Assert.Equal(45, double.Parse(first[9], CultureInfo.InvariantCulture), 2);   // yaw
        Assert.StartsWith("tcas2|", lines[1]);

        source.Ingest(XPlaneProtocol.BuildAnswer([(XPlaneProtocol.TrafficIndexBase, 1f)]));
        Assert.Equal(2, objects.Count);
        Assert.Equal("", objects[1]);
        source.Ingest(XPlaneProtocol.BuildAnswer([(XPlaneProtocol.TrafficIndexBase, 1f)]));
        Assert.Equal(2, objects.Count);  // still nobody: no further empty snapshots
    }

    // ------------------------------------------------------------------ the feed against a fake X-Plane

    [Fact]
    public async Task Feed_subscribes_records_a_scripted_flight_and_unsubscribes_on_stop()
    {
        using var sim = new FakeXPlane();
        var source = new XPlaneTelemetrySource(new StopwatchClock(), "X-Plane 12") { MinFrameIntervalSeconds = 0 };
        var frames = new ConcurrentQueue<TelemetryFrame>();
        source.FrameReceived += frames.Enqueue;
        var feed = new XPlaneTelemetryFeed(source, sim.Endpoint, 0, 20) { BatchDelay = TimeSpan.FromMilliseconds(5), ResubscribeAfter = TimeSpan.FromSeconds(10) };
        Assert.Equal("UDP 127.0.0.1:" + sim.Endpoint.Port + " (RREF)", feed.Endpoint);

        var expected = XPlaneProtocol.Subscriptions(20);
        feed.Start();
        try
        {
            await WaitUntil(() => sim.Subscribed.Count >= expected.Count);
            Assert.Equal(expected.Count, sim.Subscribed.Count);
            var client = sim.Subscribed.Values.First().Sender;
            Assert.Equal(feed.LocalPort, client.Port);
            Assert.Equal(20, sim.Subscribed[IndexOf("xp.lat")].Frequency);
            Assert.Equal("sim/flightmodel/position/latitude", sim.Subscribed[IndexOf("xp.lat")].Path);

            // The scripted flight: 10 updates 20 ms apart, every subscribed fast value, split like X-Plane does.
            var fast = sim.Subscribed.Values.Where(s => s.Index < XPlaneProtocol.TextIndexBase && s.Frequency == 20).Select(s => s.Index).OrderBy(i => i).ToList();
            for (var step = 0; step < 10; step++)
            {
                var pairs = fast.Select(i => (i, 0f)).ToList();
                Set(pairs, "xp.time", 500 + step * 0.05f);
                Set(pairs, "xp.lat", 47f + step * 0.001f);
                Set(pairs, "xp.lon", 8.5f);
                Set(pairs, "xp.elevation", 1000f + step);
                Set(pairs, "xp.true_psi", 90f);
                Set(pairs, "xp.ias", 120f);
                Set(pairs, "xp.gear_deploy_1", 1f);
                if (step == 0)
                    pairs.AddRange(Text("name", "Cessna 172 SP"));
                sim.Answer(client, pairs);
                await Task.Delay(20);
            }

            await WaitUntil(() => frames.Count >= 10);
            Assert.True(feed.Answering);
            Assert.Null(feed.Problem);
            var last = frames.Last();
            Assert.Equal(47.009, last.Values["xp.lat"], 4);
            Assert.Equal(1009, last.Values["xp.elevation"], 2);
            Assert.Equal(500.45, last.Time, 3);
            Assert.True(frames.Select(f => f.Time).SequenceEqual(frames.Select(f => f.Time).Order()));
        }
        finally
        {
            feed.Stop();
        }

        // Unsubscribe: freq 0 for every index, from the same socket.
        await WaitUntil(() => sim.Unsubscribed.Count >= expected.Count);
        Assert.Equal(expected.Select(e => e.Index).Order(), sim.Unsubscribed.Keys.Order());
        Assert.All(sim.Unsubscribed.Values, u => Assert.Equal(feed.LocalPort, u.Sender.Port));
        feed.Stop();  // idempotent
        feed.Dispose();
    }

    [Fact]
    public async Task Feed_resubscribes_while_the_sim_is_silent_and_reports_a_problem()
    {
        using var sim = new FakeXPlane();
        var source = new XPlaneTelemetrySource(new StopwatchClock()) { MinFrameIntervalSeconds = 0 };
        using var feed = new XPlaneTelemetryFeed(source, sim.Endpoint, 0, 20) { BatchDelay = TimeSpan.FromMilliseconds(5), ResubscribeAfter = TimeSpan.FromMilliseconds(200) };
        var perRound = XPlaneProtocol.Subscriptions(20).Count;

        feed.Start();
        await WaitUntil(() => sim.Requests >= 2 * perRound);  // X-Plane "started later": the second round arrives
        Assert.True(feed.SubscribeRounds >= 2);
        Assert.False(feed.Answering);
        Assert.Contains("Accept incoming connections", feed.Problem);

        var client = sim.Subscribed.Values.First().Sender;
        sim.Answer(client, [(IndexOf("xp.time"), 1f), (IndexOf("xp.lat"), 50f)]);
        await WaitUntil(() => source.FramesPublished >= 1);
        Assert.Null(feed.Problem);

        // While answers flow there is no further round.
        var rounds = feed.SubscribeRounds;
        for (var i = 0; i < 8; i++)
        {
            sim.Answer(client, [(IndexOf("xp.time"), 2f + i)]);
            await Task.Delay(50);
        }

        Assert.Equal(rounds, feed.SubscribeRounds);
    }

    [Fact]
    public void Feeds_create_the_xplane_feed_for_both_versions()
    {
        foreach (var id in new[] { "XPlane12", "XPlane11", "xp11" })
        {
            using var feed = TelemetryFeeds.Create(id, new ManualClock(), new TelemetryFeedOptions());
            var xp = Assert.IsType<XPlaneTelemetryFeed>(feed);
            Assert.Equal("UDP 127.0.0.1:49000 (RREF)", xp.Endpoint);
            Assert.IsType<XPlaneTelemetrySource>(xp.Source);
            Assert.Null(xp.Problem);  // not started
        }

        var remote = new IPEndPoint(IPAddress.Parse("192.168.1.20"), 49001);
        using var custom = (XPlaneTelemetryFeed)TelemetryFeeds.Create("XPlane12", new ManualClock(), new TelemetryFeedOptions { XPlaneEndpoint = remote });
        Assert.Equal("UDP 192.168.1.20:49001 (RREF)", custom.Endpoint);
        Assert.Equal("X-Plane 12", custom.Source.Name);
    }

    // ------------------------------------------------------------------ helpers

    private static void Set(List<(int Index, float Value)> pairs, string key, float value)
    {
        var index = IndexOf(key);
        var at = pairs.FindIndex(p => p.Index == index);
        if (at < 0)
            pairs.Add((index, value));
        else
            pairs[at] = (index, value);
    }

    private static List<(int Index, float Value)> Text(string name, string value)
    {
        var text = XPlaneProtocol.Texts.Single(t => t.Name == name);
        var bytes = Encoding.ASCII.GetBytes(value);
        return Enumerable.Range(0, text.Length).Select(i => (text.FirstIndex + i, i < bytes.Length ? (float)bytes[i] : 0f)).ToList();
    }

    private static async Task WaitUntil(Func<bool> condition, double seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>A stand-in for X-Plane's UDP port: records RREF requests and answers like the sim (183 pairs max).</summary>
    private sealed class FakeXPlane : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0)) { Client = { ReceiveBufferSize = 1 << 20 } };
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public FakeXPlane()
        {
            Endpoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
            _loop = Task.Run(Loop);
        }

        public IPEndPoint Endpoint { get; }
        public ConcurrentDictionary<int, (int Index, int Frequency, string Path, IPEndPoint Sender)> Subscribed { get; } = new();
        public ConcurrentDictionary<int, (int Index, int Frequency, string Path, IPEndPoint Sender)> Unsubscribed { get; } = new();
        public int Requests => Volatile.Read(ref _requests);

        public void Answer(IPEndPoint client, IReadOnlyList<(int Index, float Value)> pairs)
        {
            for (var at = 0; at < pairs.Count; at += XPlaneProtocol.MaxPairsPerDatagram)
            {
                var chunk = pairs.Skip(at).Take(XPlaneProtocol.MaxPairsPerDatagram).ToList();
                var packet = XPlaneProtocol.BuildAnswer(chunk);
                _udp.Send(packet, packet.Length, client);
            }
        }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (result.Buffer.Length != XPlaneProtocol.RequestLength ||
                    !XPlaneProtocol.TryParseRequest(result.Buffer, out var freq, out var index, out var path))
                    continue;
                Interlocked.Increment(ref _requests);
                var entry = (index, freq, path, result.RemoteEndPoint);
                if (freq > 0)
                    Subscribed[index] = entry;
                else
                    Unsubscribed[index] = entry;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }
}
