using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// MSFS 2024 per-aircraft binds (docs/games/msfs.md "Per-aircraft binds"): the sim keeps a profile choice per device and
// aircraft (profile_00 records). The export writes every own profile the records name, each with the relations valid
// for all of its aircraft; the import ticks the aircraft of the profiles a bind is in.
public sealed partial class MsfsProfileImportTests
{
    private const string C172 = "asobo-aircraft-c172sp-as1000";
    private const string Bonanza = "asobo-aircraft-bonanza-g36";

    // Steam: the C172 and the Bonanza share the General profile, each has its own Airplanes profile.
    private (string Home, string Remote, string Community, string Profile) SteamPerAircraftSetup()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000101"), AirplaneProfile("C172 Planes"));
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000102"), GeneralProfile("VMAX General"));
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000104"), AirplaneProfile("Bonanza Planes"));
        File.WriteAllBytes(Path.Combine(remote, "profile_00"), SimProfile00.Build(new SimProfile00.Device(VmaxId, 413, null, null,
        [
            new SimProfile00.Rec(C172, "inputprofile_0000000102", "inputprofile_0000000101", 3),
            new SimProfile00.Rec(Bonanza, "inputprofile_0000000102", "inputprofile_0000000104", 3)
        ])));
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        string community = Path.Combine(roaming, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        Engine(home, "games", "install", "set", "MSFS2024", roaming);
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, Vmax + "\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        string profile = Path.Combine(root, "p.pr0file");
        (int code, string import) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");
        Assert.True(code == 0, import);
        // JoyPro's aircraft list (DB\MSFS2024\aircraft.tsv): key, title, category, sims.
        string db = Path.Combine(root, "data", "GameData", "DB", "MSFS2024");
        Directory.CreateDirectory(db);
        File.WriteAllText(Path.Combine(db, "aircraft.tsv"), C172 + "\tC172 Test\tairplane\t2024\r\n" + Bonanza + "\tBonanza Test\tairplane\t2024\r\n");
        return (home, remote, community, profile);
    }

    private static JsonElement NodeOf(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json[json.IndexOf('{')..]);
        return doc.RootElement.GetProperty("Nodes")[0].Clone();
    }

    [Fact]
    public void Steam2024_PerAircraft_EveryProfileGetsTheRelationsOfAllItsAircraft_ImportTicksThem()
    {
        (string home, string remote, string community, string profile) = SteamPerAircraftSetup();
        // The import of two Airplanes profiles: the flaps axis is in both, so it is for every aircraft.
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", Vmax, "--input", "JOY_BTN5", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Beacon", "--node", "MSFS2024:KEY_BEACON_LIGHTS_OFF", "--device", Vmax, "--input", "JOY_BTN9", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP ATC", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);

        // The aircraft checkmarks of an MSFS action: every aircraft ticked = all aircraft; one off limits it.
        (int listCode, string list) = Engine(home, "--profile", profile, "relations", "aircraft", "JP Beacon");
        Assert.True(listCode == 0, list);
        JsonElement node = NodeOf(list);
        Assert.Equal("all aircraft", node.GetProperty("Badge").GetString());
        Assert.Equal(2, node.GetProperty("Aircraft").GetArrayLength());
        Assert.True(MainWindowViewModel.HasOwnAircraftList("MSFS2024", node));
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "aircraft", "set", "JP Beacon", "--node", "MSFS2024:KEY_BEACON_LIGHTS_OFF", "--off", Bonanza).Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "aircraft", "set", "JP ATC", "--node", "MSFS2024:KEY_ATC_MENU_0", "--off", Bonanza).Code);
        node = NodeOf(Engine(home, "--profile", profile, "relations", "aircraft", "JP Beacon").Output);
        Assert.Equal("1 of 2 aircraft", node.GetProperty("Badge").GetString());
        Assert.Contains(node.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == C172 && a.GetProperty("State").GetString() == "on" && a.GetProperty("Title").GetString() == "C172 Test");
        Assert.Contains(node.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == Bonanza && a.GetProperty("State").GetString() == "off");
        // Ticking it again gives "all aircraft" back.
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "aircraft", "set", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--off", Bonanza).Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "aircraft", "set", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--on", Bonanza).Code);
        Assert.Equal("all aircraft", NodeOf(Engine(home, "--profile", profile, "relations", "aircraft", "JP Gear").Output).GetProperty("Badge").GetString());

        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");

        Assert.True(code == 0, export);
        string c172 = File.ReadAllText(Path.Combine(remote, "inputprofile_0000000101"));
        string bonanza = File.ReadAllText(Path.Combine(remote, "inputprofile_0000000104"));
        string general = File.ReadAllText(Path.Combine(remote, "inputprofile_0000000102"));
        // The general relation in both Airplanes profiles, the C172 one only in the C172's.
        Assert.Matches("(?s)KEY_GEAR_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 5\">4</KEY>", c172);
        Assert.Matches("(?s)KEY_GEAR_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 5\">4</KEY>", bonanza);
        Assert.Matches("(?s)KEY_BEACON_LIGHTS_OFF\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 9\">8</KEY>", c172);
        Assert.Matches("KEY_BEACON_LIGHTS_OFF\"[^>]*/>", bonanza);
        // The General profile is shared: the C172-only bind is not written there, the user is told how to split it.
        Assert.Matches("KEY_ATC_MENU_0\"[^>]*/>", general);
        Assert.Contains("is only for C172 Test, but your profile 'VMAX General' is also used by Bonanza Test: not written there. Give the C172 Test its own profile in the sim " +
                        "(Controls, with the C172 Test loaded: duplicate the profile), then export again.", export);
        // One result line per profile, with its aircraft (the imported flaps axis counts too).
        Assert.Contains("MSFS 2024 'VPC VMAX Prime Throttle' (Airplanes): your profile 'C172 Planes' (C172 Test): 3 bind(s)", export);
        Assert.Contains("MSFS 2024 'VPC VMAX Prime Throttle' (Airplanes): your profile 'Bonanza Planes' (Bonanza Test): 2 bind(s)", export);
        Assert.Contains("VPC VMAX Prime Throttle (Airplanes): updated 'C172 Planes' (C172 Test), 3 bind(s)", export);
        Assert.Contains("VPC VMAX Prime Throttle (Airplanes): your profile 'C172 Planes' (C172 Test) updated: 3 bind(s)", MainWindowViewModel.MsfsProfileUpdateLines(export));
        // profile_00 is never written, no new profile is made; the preset package gets no aircraft-limited bind.
        Assert.Equal(["inputprofile_0000000101", "inputprofile_0000000102", "inputprofile_0000000104", "profile_00"],
            Directory.GetFiles(remote).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        foreach (string preset in Directory.GetFiles(community, "*.xml", SearchOption.AllDirectories))
            Assert.DoesNotMatch("(?s)KEY_BEACON_LIGHTS_OFF\"[^>]*>\\s*<Primary>", File.ReadAllText(preset));

        // Import round trip: the bind only in the C172's profile comes back for the C172, the ones in both for all.
        string clean = Path.Combine(root, "clean.pr0file");
        (int importCode, string import) = Engine(home, "--profile", clean, "msfs2024", "import", "--text");
        Assert.True(importCode == 0, import);
        Assert.Contains("for different aircraft; imported all of them", import);
        JsonElement beacon = NodeOf(Engine(home, "--profile", clean, "relations", "aircraft", "JP Beacon").Output);
        Assert.Equal("1 of 2 aircraft", beacon.GetProperty("Badge").GetString());
        Assert.Contains(beacon.GetProperty("Aircraft").EnumerateArray(), a => a.GetProperty("Plane").GetString() == C172 && a.GetProperty("State").GetString() == "on");
        Assert.Equal("all aircraft", NodeOf(Engine(home, "--profile", clean, "relations", "aircraft", "JP Gear").Output).GetProperty("Badge").GetString());
        // Exported again unchanged from the imported profile: nothing moves.
        (int againCode, string again) = Engine(home, "--profile", clean, "msfs2024", "export", community, "--text");
        Assert.True(againCode == 0, again);
        Assert.Equal(c172, File.ReadAllText(Path.Combine(remote, "inputprofile_0000000101")));
        Assert.Equal(bonanza, File.ReadAllText(Path.Combine(remote, "inputprofile_0000000104")));
    }
}
