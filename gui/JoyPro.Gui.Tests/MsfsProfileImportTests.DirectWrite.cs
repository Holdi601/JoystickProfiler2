using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// MSFS 2024: the export writes JoyPro's binds into the profile the sim uses for each device and category (docs/games/
// msfs.md "Writing your own MSFS 2024 profiles"): the Microsoft Store save written like the save tools and the game do
// (new blob, container.<n+1>, containers.index last), the Steam remote file in place, the sim's records (profile_00)
// naming the active profile. Tester (2026-10-20, MSFS 2024 Store): importing JoyPro's file made a second profile; he
// wants his own default profile to have the binds.
public sealed partial class MsfsProfileImportTests
{
    private const string VmaxGuid = "0000CCCC-0000-4000-8000-000000000301";
    private const string Vmax = "VPC VMAX Prime Throttle {" + VmaxGuid + "}";
    private static readonly Guid VmaxId = new(VmaxGuid);

    // A General profile as the sim saves it (no category, every action of its contexts listed): active pause bound to
    // button 26 (INGAME_UI), the ATC choices unbound, camera zoom on slider Y.
    private static string GeneralProfile(string name, bool atcBound = false) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version Num=\"-1\"/>\n<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">" + name + "</FriendlyName>\n" +
        "<Device DeviceName=\"VPC VMAX Prime Throttle\" GUID=\"{" + VmaxGuid + "}\" ProductID=\"413\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\n" +
        "\t<Context ContextName=\"ATC\">\n" +
        (atcBound
            ? "\t\t<Action ActionName=\"KEY_ATC_MENU_0\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Button 2\">1</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n"
            : "\t\t<Action ActionName=\"KEY_ATC_MENU_0\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n") +
        "\t\t<Action ActionName=\"KEY_ATC_MENU_1\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n" +
        "\t</Context>\n" +
        "\t<Context ContextName=\"COCKPIT_GLOBAL_CAMERA\">\n" +
        "\t\t<Action ActionName=\"KEY_COCKPIT_CAMERA_ZOOM_AXIS\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"4\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Slider Y \">530</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n" +
        "\t</Context>\n" +
        "\t<Context ContextName=\"INGAME_UI\">\n" +
        "\t\t<Action ActionName=\"KEY_ACTIVE_PAUSE_TOGGLE\" ValueEvent=\"0.000000\" Delay=\"2.000000\" Flag=\"8194\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick Button 26\">25</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n" +
        "\t\t<Action ActionName=\"KEY_PAUSE_TOGGLE\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n" +
        "\t</Context>\n" +
        "</Device>\n";

    private static string AirplaneProfile(string name, string extraBound = "") =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version Num=\"-1\"/>\n<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">" + name + "</FriendlyName>\n" +
        "<Device DeviceName=\"VPC VMAX Prime Throttle\" GUID=\"{" + VmaxGuid + "}\" ProductID=\"413\" CompositeID=\"0\" HWVer=\"1.0.0.0\">\n" +
        "\t<AircraftInfo CategoryName=\"AIRPLANE\"/>\n" +
        "\t<Context ContextName=\"AIRCRAFT\">\n" +
        "\t\t<Action ActionName=\"KEY_AXIS_FLAPS_SET\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"4\">\n\t\t\t<Primary>\n\t\t\t\t<KEY Information=\"Joystick R-Axis Z \">802</KEY>\n\t\t\t</Primary>\n\t\t</Action>\n" +
        "\t\t<Action ActionName=\"KEY_GEAR_TOGGLE\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n" +
        "\t\t<Action ActionName=\"KEY_BEACON_LIGHTS_OFF\" ValueEvent=\"0.000000\" Delay=\"0.000000\" Flag=\"2\"/>\n" +
        extraBound +
        "\t</Context>\n" +
        "</Device>\n";

    // A Store install with the tester's layout: the VMAX's General and Airplanes profiles the sim's records name, an
    // older Airplanes profile it does not use, profile_00 and a logbook.
    private (string Home, string User, string Community, string Profile) StoreDirectSetup(bool records = true)
    {
        string home = Path.Combine(root, "h");
        string localCache = Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache");
        string community = Path.Combine(localCache, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(localCache, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(localCache, "Packages") + "\"\r\n");
        List<WgsContainer> containers =
        [
            new("inputprofile_0000000101", [new("data", Encoding.UTF8.GetBytes(AirplaneProfile("VMAX Planes")), DateTime.Now.AddDays(-2))]),
            new("inputprofile_0000000102", [new("data", Encoding.UTF8.GetBytes(GeneralProfile("VMAX General")), DateTime.Now.AddDays(-2))]),
            new("inputprofile_0000000103", [new("data", Encoding.UTF8.GetBytes(AirplaneProfile("VMAX Old Planes")), DateTime.Now)]),
            new("sr_logbook", [new("data", Encoding.UTF8.GetBytes("logbook"), null)])
        ];
        if (records)
        {
            SimProfile00.Rec[] recs =
            [
                new("asobo-aircraft-c172sp-as1000", "inputprofile_0000000102", "inputprofile_0000000101", 3, "VMAX General", "VMAX Planes"),
                new("asobo-aircraft-bonanza-g36", "inputprofile_0000000102", "inputprofile_0000000101", 3, "VMAX General", "VMAX Planes")
            ];
            containers.Add(new("profile_00", [new("data", SimProfile00.Build(new SimProfile00.Device(VmaxId, 413, null, null, recs)), null)]));
        }
        string user = WgsWriter.Write(StoreWgs(home, 2024), "Microsoft.Limitless_8wekyb3d8bbwe", containers);
        Engine(home, "games", "install", "set", "MSFS2024", Path.Combine(home, "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe"));
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, Vmax + "\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        string profile = Path.Combine(root, "p.pr0file");
        (int code, string import) = Engine(home, "--profile", profile, "msfs2024", "import", "--text");
        Assert.True(code == 0, import);
        return (home, user, community, profile);
    }

    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    // The Store save as the reader sees it: per container the entry and its blob text.
    private static Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> ReadSave(string user)
    {
        WgsIndexFile index = WgsIndexFile.Parse(File.ReadAllBytes(Path.Combine(user, "containers.index")));
        Dictionary<string, (WgsIndexEntry, string, string, Guid, Guid)> save = new(StringComparer.OrdinalIgnoreCase);
        foreach (WgsIndexEntry entry in index.Entries)
        {
            string folder = Path.Combine(user, entry.Folder.ToString("N").ToUpperInvariant());
            string containerFile = Path.Combine(folder, "container." + entry.Number);
            byte[] container = File.ReadAllBytes(containerFile);
            Assert.Equal(168, container.Length);
            Assert.Equal(4u, BitConverter.ToUInt32(container, 0));
            Assert.Equal(1u, BitConverter.ToUInt32(container, 4));
            Guid cloud = new(container.AsSpan(8 + 128, 16));
            Guid disk = new(container.AsSpan(8 + 144, 16));
            string blob = Path.Combine(folder, disk.ToString("N").ToUpperInvariant());
            Assert.True(File.Exists(blob), "blob of " + entry.Name);
            Assert.Equal(entry.Size, new FileInfo(blob).Length);
            // The game keeps one container file and one blob per folder.
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            save[entry.Name] = (entry, File.ReadAllText(blob), containerFile, cloud, disk);
        }
        return save;
    }

    // The sim saves a changed profile: a new blob (new GUID name), container.<n+1>, the index rewritten, the old files
    // removed.
    internal static void SimSave(string user, string name, byte[] bytes)
    {
        string indexPath = Path.Combine(user, "containers.index");
        WgsIndexFile index = WgsIndexFile.Parse(File.ReadAllBytes(indexPath));
        WgsIndexEntry entry = index.Entries.Single(e => e.Name == name);
        string folder = Path.Combine(user, entry.Folder.ToString("N").ToUpperInvariant());
        string oldContainer = Path.Combine(folder, "container." + entry.Number);
        byte[] container = File.ReadAllBytes(oldContainer);
        string oldBlob = Path.Combine(folder, new Guid(container.AsSpan(8 + 144, 16)).ToString("N").ToUpperInvariant());
        Guid id = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(folder, id.ToString("N").ToUpperInvariant()), bytes);
        id.ToByteArray().CopyTo(container, 8 + 128);
        id.ToByteArray().CopyTo(container, 8 + 144);
        entry.Number++;
        entry.Size = bytes.Length;
        entry.FileTime = DateTime.UtcNow.ToFileTimeUtc() / 10000 * 10000;
        File.WriteAllBytes(Path.Combine(folder, "container." + entry.Number), container);
        index.FileTime = DateTime.UtcNow.ToFileTimeUtc();
        File.WriteAllBytes(indexPath + ".tmp", index.ToBytes());
        File.Move(indexPath + ".tmp", indexPath, true);
        File.Delete(oldContainer);
        File.Delete(oldBlob);
    }

    internal sealed class WgsIndexEntry
    {
        public string Name = "";
        public string Name2 = "";
        public string ETag = "";
        public byte Number;
        public uint State;
        public Guid Folder;
        public long FileTime;
        public byte[] Reserved = new byte[8];
        public long Size;
    }

    // containers.index (engine\Games\WgsContainers.cs).
    internal sealed class WgsIndexFile
    {
        public uint Version;
        public uint Reserved;
        public string Package = "";
        public long FileTime;
        public uint Flags;
        public string Root = "";
        public byte[] Trailing = new byte[8];
        public List<WgsIndexEntry> Entries = [];

        public static WgsIndexFile Parse(byte[] data)
        {
            using BinaryReader r = new(new MemoryStream(data));
            WgsIndexFile index = new() { Version = r.ReadUInt32() };
            uint count = r.ReadUInt32();
            index.Reserved = r.ReadUInt32();
            index.Package = S(r);
            index.FileTime = r.ReadInt64();
            index.Flags = r.ReadUInt32();
            index.Root = S(r);
            index.Trailing = r.ReadBytes(8);
            for (int i = 0; i < count; i++)
                index.Entries.Add(new WgsIndexEntry
                {
                    Name = S(r), Name2 = S(r), ETag = S(r), Number = r.ReadByte(), State = r.ReadUInt32(), Folder = new Guid(r.ReadBytes(16)),
                    FileTime = r.ReadInt64(), Reserved = r.ReadBytes(8), Size = r.ReadInt64()
                });
            Assert.Equal(data.Length, (int)r.BaseStream.Position);
            return index;
        }

        public byte[] ToBytes()
        {
            using MemoryStream m = new();
            using BinaryWriter w = new(m);
            w.Write(Version);
            w.Write((uint)Entries.Count);
            w.Write(Reserved);
            W(w, Package);
            w.Write(FileTime);
            w.Write(Flags);
            W(w, Root);
            w.Write(Trailing);
            foreach (WgsIndexEntry e in Entries)
            {
                W(w, e.Name);
                W(w, e.Name2);
                W(w, e.ETag);
                w.Write(e.Number);
                w.Write(e.State);
                w.Write(e.Folder.ToByteArray());
                w.Write(e.FileTime);
                w.Write(e.Reserved);
                w.Write(e.Size);
            }
            w.Flush();
            return m.ToArray();
        }

        private static string S(BinaryReader r) => Encoding.Unicode.GetString(r.ReadBytes(r.ReadInt32() * 2));

        private static void W(BinaryWriter w, string s)
        {
            w.Write(s.Length);
            w.Write(Encoding.Unicode.GetBytes(s));
        }
    }

    // A tester's relations "AAA56" (VMAX button 2, ATC Panel Choice 0, General) and a new gear bind (Airplanes): they go
    // into his active profiles "VMAX General" / "VMAX Planes" in the Store save; active pause (catalog: PLANE from an
    // older list) stays in the General profile; the profile the sim does not use, the logbook and profile_00 stay
    // untouched; the save reads back consistently; a second export changes nothing.
    [Fact]
    public void Store2024_Export_WritesTheActiveProfilesInTheSave()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        // The shared command list may have active pause in a flight context (MSFS 2020); the sim lists it in INGAME_UI.
        string actions = Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv");
        File.WriteAllText(actions, Regex.Replace(File.ReadAllText(actions), @"(?m)^(KEY_ACTIVE_PAUSE_TOGGLE\t[^\t]+\tbutton\t)\w+", "$1PLANE"));
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAA56", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", Vmax, "--input", "JOY_BTN5", "--text").Code);
        Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> before = ReadSave(user);
        WgsIndexFile indexBefore = WgsIndexFile.Parse(File.ReadAllBytes(Path.Combine(user, "containers.index")));

        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");

        Assert.True(code == 0, export);
        Assert.Contains("MSFS 2024 'VPC VMAX Prime Throttle' (General): your profile 'VMAX General' (active in 2 of 2 aircraft record(s)): 3 bind(s)", export);
        Assert.Contains("MSFS 2024 'VPC VMAX Prime Throttle' (Airplanes): your profile 'VMAX Planes' (active in 2 of 2 aircraft record(s)): 2 bind(s)", export);
        Assert.Contains("VPC VMAX Prime Throttle (General): updated 'VMAX General', 3 bind(s)", export);
        Assert.Contains("Microsoft Store save written (2 profile(s)); backup of the whole save: set ", export);
        Assert.DoesNotContain("not written", export);
        // Covered by the direct write: no import file needed.
        Assert.DoesNotContain("MSFS 2024 import files written", export);

        Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> after = ReadSave(user);
        WgsIndexFile index = WgsIndexFile.Parse(File.ReadAllBytes(Path.Combine(user, "containers.index")));
        Assert.Equal(indexBefore.Entries.Count, index.Entries.Count);
        Assert.Equal(14u, index.Version);
        Assert.Equal(2u, index.Flags);
        Assert.True(index.FileTime > indexBefore.FileTime);
        Assert.Equal(indexBefore.Trailing, index.Trailing);
        foreach (string name in new[] { "inputprofile_0000000101", "inputprofile_0000000102" })
        {
            Assert.Equal(before[name].Entry.Number + 1, after[name].Entry.Number);
            Assert.Equal(2u, after[name].Entry.State);
            Assert.Equal(before[name].Entry.ETag, after[name].Entry.ETag);
            Assert.Equal(before[name].Entry.Folder, after[name].Entry.Folder);
            // The cloud's blob id stays, the new file is the on-disk id; the old generation is gone.
            Assert.Equal(before[name].Disk, after[name].Cloud);
            Assert.NotEqual(before[name].Disk, after[name].Disk);
            Assert.False(File.Exists(before[name].ContainerFile));
        }
        foreach (string name in new[] { "inputprofile_0000000103", "sr_logbook", "profile_00" })
        {
            Assert.Equal(before[name].Text, after[name].Text);
            Assert.Equal(before[name].Entry.Number, after[name].Entry.Number);
            Assert.Equal(1u, after[name].Entry.State);
        }
        string general = after["inputprofile_0000000102"].Text;
        Assert.Matches("(?s)<Action ActionName=\"KEY_ATC_MENU_0\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 2\">1</KEY>", general);
        Assert.Matches("(?s)<Action ActionName=\"KEY_ACTIVE_PAUSE_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 26\">25</KEY>", general);
        Assert.Contains("<FriendlyName PlatformAvailability=\"1\" Locked=\"false\">VMAX General</FriendlyName>", general);
        string planes = after["inputprofile_0000000101"].Text;
        Assert.Matches("(?s)<Action ActionName=\"KEY_GEAR_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 5\">4</KEY>", planes);
        Assert.DoesNotContain("ACTIVE_PAUSE", planes);
        Assert.Matches("(?s)KEY_AXIS_FLAPS_SET.*?802</KEY>", planes);

        // Read back like the import does: the binds are in the sim's active profiles.
        (int scanCode, string scan) = Engine(home, "msfs2024", "scan", "--text");
        Assert.True(scanCode == 0, scan);
        Assert.Contains("KEY_ATC_MENU_0 <- " + Vmax + " JOY_BTN2", scan);
        Assert.Contains("KEY_GEAR_TOGGLE <- " + Vmax + " JOY_BTN5", scan);
        Assert.Contains("KEY_ACTIVE_PAUSE_TOGGLE <- " + Vmax + " JOY_BTN26", scan);

        // Again: nothing to write.
        Dictionary<string, string> hashes = Hashes(user);
        (int againCode, string again) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(againCode == 0, again);
        Assert.Contains("VPC VMAX Prime Throttle (General): unchanged 'VMAX General', 3 bind(s)", again);
        Assert.Equal(hashes, Hashes(user));
        // The GUI's result lines, from the text and from the JSON output.
        Assert.Contains("VPC VMAX Prime Throttle (General): your profile 'VMAX General' (active) already has the 3 bind(s)", MainWindowViewModel.MsfsProfileUpdateLines(again));
        (int jsonCode, string json) = Engine(home, "--profile", profile, "msfs2024", "export", community);
        Assert.True(jsonCode == 0, json);
        List<MainWindowViewModel.MsfsProfileUpdate> updates = MainWindowViewModel.ParseMsfsProfileUpdates(json);
        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Device == "VPC VMAX Prime Throttle" && u.Category == "Airplanes" && u.Profile == "VMAX Planes" && u.Status == "unchanged" && u.Binds == 2);
    }

    [Fact]
    public void MsfsProfileUpdateLines_NameTheFallbackAndTheMissingBinds_SettingsRemembered()
    {
        string text = "Profiles:\r\n  - VPC VMAX Prime Throttle (General): import-file 'VMAX General', 2 bind(s) - MSFS 2024 is running (gamelaunchhelper); close it and export again\r\n" +
                      "Steps:\r\n  - x\r\nWarnings:\r\n  - MSFS 2024 'VPC VMAX Prime Throttle': not written: JP Heli (Helicopter Only Test) (Airplanes) - the profile does not list KEY_HELICOPTER_ONLY_TEST.\r\n";
        List<string> lines = MainWindowViewModel.MsfsProfileUpdateLines(text);
        Assert.Equal("VPC VMAX Prime Throttle (General): not written into your profile, the import file has the binds: MSFS 2024 is running (gamelaunchhelper); close it and export again", lines[0]);
        Assert.Equal("Not written: VPC VMAX Prime Throttle: JP Heli (Helicopter Only Test) (Airplanes) - the profile does not list KEY_HELICOPTER_ONLY_TEST", lines[1]);
        // "MSFS2024" in a node's aircraft means every aircraft (an imported relation showed "1 aircraft").
        Assert.True(MainWindowViewModel.IsAllAircraftScope("MSFS2024", "MSFS2024"));
        Assert.True(MainWindowViewModel.IsAllAircraftScope("MSFS2024", "MSFS"));
        Assert.False(MainWindowViewModel.IsAllAircraftScope("MSFS2024", "asobo-aircraft-c172sp-as1000"));
        // The export option is on by default and survives a restart.
        string settings = Path.Combine(root, "export-settings.json");
        Assert.True(JoyPro.Gui.Services.ExportSettingsStore.Load(settings).MsfsWriteUserProfiles);
        JoyPro.Gui.Services.ExportSettings s = JoyPro.Gui.Services.ExportSettingsStore.Load(settings);
        s.MsfsWriteUserProfiles = false;
        s.MsfsAlwaysImportFiles = true;
        JoyPro.Gui.Services.ExportSettingsStore.Save(settings, s);
        Assert.False(JoyPro.Gui.Services.ExportSettingsStore.Load(settings).MsfsWriteUserProfiles);
        Assert.True(JoyPro.Gui.Services.ExportSettingsStore.Load(settings).MsfsAlwaysImportFiles);
    }

    // A relation deleted in JoyPro takes its bind out of the profile again (merge-overwrite); a dry run writes nothing.
    [Fact]
    public void Store2024_Export_RelationDeletedInJoyPro_LeavesTheProfile_DryRunWritesNothing()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        // The JoyPro profile as it was before the gear relation.
        string other = Path.Combine(root, "p2.pr0file");
        File.Copy(profile, other);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", Vmax, "--input", "JOY_BTN5", "--text").Code);
        Dictionary<string, string> hashes = Hashes(user);
        (int dryCode, string dry) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--dry-run", "--text");
        Assert.True(dryCode == 0, dry);
        Assert.Contains("dry run: your profile 'VMAX Planes'", dry);
        Assert.Equal(hashes, Hashes(user));
        Assert.Equal(0, Engine(home, "--profile", profile, "msfs2024", "export", community, "--text").Code);
        Assert.Contains("Joystick Button 5", ReadSave(user)["inputprofile_0000000101"].Text);

        // The relation deleted (the profile without it): JoyPro's gear bind leaves the sim's profile, the flaps stay.
        (int code, string export) = Engine(home, "--profile", other, "msfs2024", "export", community, "--text");
        Assert.True(code == 0, export);
        string planes = ReadSave(user)["inputprofile_0000000101"].Text;
        Assert.DoesNotContain("Joystick Button 5", planes);
        Assert.Contains("802</KEY>", planes);
    }

    // Safety: while the sim's launch helper runs nothing is written (the import file has the binds); the sim itself
    // refuses the whole export; an index JoyPro does not know writes nothing.
    [Fact]
    public void Store2024_Export_SimRunningOrUnknownIndex_WritesNothing_ImportFilesInstead()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAA56", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);
        Dictionary<string, string> hashes = Hashes(user);
        string folder = Path.Combine(root, "data", "MSFS 2024 import files");

        // The Store build's launch helper (a stand-in exe in a "Microsoft Flight Simulator 2024" folder).
        using (Process helper = StartFakeProcess(Path.Combine(root, "XboxGames", "Microsoft Flight Simulator 2024", "Content"), "gamelaunchhelper"))
        {
            (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
            Assert.True(code == 0, export);
            Assert.Contains("MSFS 2024 is running (gamelaunchhelper", export);
            Assert.Contains("JoyPro did not write into your MSFS profiles", export);
            Assert.Contains("VPC VMAX Prime Throttle (General): import-file 'VMAX General'", export);
            Assert.Equal(hashes, Hashes(user));
            // The import file is named like his profile: importing it overwrites that one.
            string file = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*_General.xml")));
            Assert.Contains(">VMAX General</FriendlyName>", file);
            Assert.Contains("Import it and confirm 'overwrite': your profile 'VMAX General' then has JoyPro's binds.", export);
            Assert.Matches("(?s)KEY_ATC_MENU_0\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 2\">1</KEY>", file);
            helper.Kill();
            helper.WaitForExit();
        }
        // The sim itself: the whole export is refused.
        using (Process sim = StartFakeProcess(Path.Combine(root, "simexe"), "FlightSimulator2024"))
        {
            (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
            Assert.NotEqual(0, code);
            Assert.Contains("is running (FlightSimulator2024)", export);
            Assert.Equal(hashes, Hashes(user));
            sim.Kill();
            sim.WaitForExit();
        }
        // Another index version: nothing written, the reason said.
        string indexPath = Path.Combine(user, "containers.index");
        byte[] original = File.ReadAllBytes(indexPath);
        byte[] changed = (byte[])original.Clone();
        changed[0] = 15;
        File.WriteAllBytes(indexPath, changed);
        hashes = Hashes(user);
        (int unknownCode, string unknown) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(unknownCode == 0, unknown);
        Assert.Contains("JoyPro found something unexpected in the Microsoft Store save and wrote nothing: the save index has version 15", unknown);
        Assert.Equal(hashes, Hashes(user));
        Assert.Contains("MSFS 2024 import files written", unknown);
        File.WriteAllBytes(indexPath, original);
    }

    // A failure after the index was written (validation) puts the save back; the backup set restores the state before
    // an export (engine backups restore), and the restored containers count as changed on this PC.
    [Fact]
    public void Store2024_Export_FailureRollsBack_BackupRestoreRoundTrip()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAA56", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);
        Dictionary<string, string> hashes = Hashes(user);

        environment["JOYPRO_TEST_FAULT_WGS"] = "index";
        (int faultCode, string fault) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        environment.Remove("JOYPRO_TEST_FAULT_WGS");
        Assert.True(faultCode == 0, fault);
        Assert.Contains("JoyPro put the save back as it was", fault);
        Assert.Equal(hashes, Hashes(user));
        Assert.Contains("MSFS 2024 import files written", fault);

        Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> original = ReadSave(user);
        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(code == 0, export);
        string set = Regex.Match(export, "backup of the whole save: set (\\S+) ").Groups[1].Value;
        Assert.NotEmpty(set);
        Assert.Contains("Joystick Button 2", ReadSave(user)["inputprofile_0000000102"].Text);
        (int listCode, string list) = Engine(home, "backups", "list", "--game", "MSFS2024", "--text");
        Assert.True(listCode == 0, list);
        Assert.Contains(set, list);

        (int restoreCode, string restore) = Engine(home, "backups", "restore", set, "--text");
        Assert.True(restoreCode == 0, restore);
        Assert.Contains("restored save container(s) marked for upload to the Xbox cloud", restore);
        Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> restored = ReadSave(user);
        foreach (KeyValuePair<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> container in original)
        {
            Assert.Equal(container.Value.Text, restored[container.Key].Text);
            Assert.Equal(container.Value.Entry.Number, restored[container.Key].Entry.Number);
            Assert.Equal(container.Value.Disk, restored[container.Key].Disk);
        }
        Assert.Equal(2u, restored["inputprofile_0000000102"].Entry.State);
        Assert.Equal(1u, restored["sr_logbook"].Entry.State);
        // No file of JoyPro's write is left.
        Assert.Equal(original.Count * 2 + 1, Directory.GetFiles(user, "*", SearchOption.AllDirectories).Length);
        (_, string scan) = Engine(home, "msfs2024", "scan", "--text");
        Assert.DoesNotContain("KEY_ATC_MENU_0 <- ", scan);
    }

    // The tester: he deleted 'ATC Panel Choice 0' in the sim, JoyPro did not see it; and an import into a clean JoyPro
    // profile brought back relations made in JoyPro that the sim shows nowhere (they were only in JoyPro's package
    // preset). His own active profile is the truth for the device and category: the package preset adds nothing there,
    // the watcher's diff reports the removal, and an import no longer has the bind.
    [Fact]
    public void Store2024_BindDeletedInTheSim_IsRemoved_PackagePresetDoesNotBringItBack()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAA56", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAAAA57", "--node", "MSFS2024:KEY_BEACON_LIGHTS_OFF", "--device", Vmax, "--input", "JOY_BTN9", "--text").Code);
        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");
        Assert.True(code == 0, export);
        // JoyPro's package preset has the binds too (for aircraft the sim has not set up yet).
        string presets = string.Join("\n", Directory.GetFiles(Path.Combine(community, "joypro-input-profiles"), "*.xml", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("Joystick Button 2", presets);
        (_, string status) = Engine(home, "sync", "status", "--game", "MSFS2024");
        Assert.Contains("\"State\":\"unchanged\"", status);

        // In the sim: Controls > VMAX > General, ATC Panel Choice 0 cleared, saved.
        string general = ReadSave(user)["inputprofile_0000000102"].Text;
        string cleared = Regex.Replace(general, "(?s)<Action ActionName=\"KEY_ATC_MENU_0\"([^>]*)>.*?</Action>", "<Action ActionName=\"KEY_ATC_MENU_0\"$1/>");
        Assert.NotEqual(general, cleared);
        SimSave(user, "inputprofile_0000000102", Encoding.UTF8.GetBytes(cleared));

        (_, string changed) = Engine(home, "sync", "status", "--game", "MSFS2024");
        Assert.Contains("\"State\":\"changed\"", changed);
        (int diffCode, string diff) = Engine(home, "--profile", profile, "sync", "diff", "--game", "MSFS2024");
        Assert.True(diffCode == 0, diff);
        Assert.True(Regex.IsMatch(diff, "\"Kind\":\"removed\"[^}]*KEY_ATC_MENU_0"), diff);

        // An import into a clean profile: the sim's binds, not JoyPro's preset.
        string clean = Path.Combine(root, "clean.pr0file");
        (int importCode, string import) = Engine(home, "--profile", clean, "msfs2024", "import", "--text");
        Assert.True(importCode == 0, import);
        Assert.DoesNotContain("KEY_ATC_MENU_0 <- ", import);
        Assert.Contains("KEY_BEACON_LIGHTS_OFF <- " + Vmax + " JOY_BTN9", import);
        Assert.Contains("KEY_ACTIVE_PAUSE_TOGGLE <- " + Vmax + " JOY_BTN26", import);
        Assert.Contains("JoyPro preset(s) of the Community2024 package not read: your own MSFS profile of that device and category is what the sim uses", import);

        // Relations made in JoyPro that never reached his profiles (written only to the package before): an import into
        // a clean profile does not bring them back.
        string beacon = ReadSave(user)["inputprofile_0000000101"].Text;
        SimSave(user, "inputprofile_0000000101", Encoding.UTF8.GetBytes(Regex.Replace(beacon, "(?s)<Action ActionName=\"KEY_BEACON_LIGHTS_OFF\"([^>]*)>.*?</Action>", "<Action ActionName=\"KEY_BEACON_LIGHTS_OFF\"$1/>")));
        string clean2 = Path.Combine(root, "clean2.pr0file");
        (_, string import2) = Engine(home, "--profile", clean2, "msfs2024", "import", "--text");
        Assert.DoesNotContain("KEY_BEACON_LIGHTS_OFF <- ", import2);
        Assert.DoesNotContain("KEY_ATC_MENU_0 <- ", import2);
    }

    // Every bind of a device lands in the right category profile, and one that cannot is named in the result: an action
    // the sim's profiles do not list, both input slots taken.
    [Fact]
    public void Store2024_Export_MixedCategories_EveryBindLandsOrIsNamed()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Pause Too", "--node", "MSFS2024:KEY_ACTIVE_PAUSE_TOGGLE", "--device", Vmax, "--input", "JOY_BTN27", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Pause Three", "--node", "MSFS2024:KEY_ACTIVE_PAUSE_TOGGLE", "--device", Vmax, "--input", "JOY_BTN28", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", Vmax, "--input", "JOY_BTN5", "--text").Code);
        // An airplane action the user's Airplanes profile does not list (another category of the sim).
        string actions = Path.Combine(root, "data", "GameData", "DB", "MSFS2024", "actions.tsv");
        File.AppendAllText(actions, "KEY_HELICOPTER_ONLY_TEST\tHelicopter Only Test\tbutton\tHELICOPTER\r\n");
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Heli", "--node", "MSFS2024:KEY_HELICOPTER_ONLY_TEST", "--device", Vmax, "--input", "JOY_BTN6", "--text").Code);

        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");

        Assert.True(code == 0, export);
        Dictionary<string, (WgsIndexEntry Entry, string Text, string ContainerFile, Guid Cloud, Guid Disk)> save = ReadSave(user);
        Assert.Contains("Joystick Button 5", save["inputprofile_0000000101"].Text);
        Assert.Matches("(?s)KEY_ACTIVE_PAUSE_TOGGLE\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 26\">25</KEY>\\s*</Primary>\\s*<Secondary>\\s*<KEY Information=\"Joystick Button 2[78]\">", save["inputprofile_0000000102"].Text);
        Assert.Matches("not written: JP Pause (Too|Three) \\([^)]*\\) \\(General\\) - [^\\r\\n]*both MSFS input slots", export);
        Assert.Contains("not written: JP Heli (Helicopter Only Test) (Airplanes) - the profile does not list KEY_HELICOPTER_ONLY_TEST", export);
    }

    // MSFS 2024 Steam: the remote folder's inputprofile_* the records name is edited in place (backup first).
    [Fact]
    public void Steam2024_Export_WritesTheActiveRemoteProfile()
    {
        string home = Path.Combine(root, "h");
        string remote = Path.Combine(home, "Steam", "userdata", "42", "2537590", "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000101"), AirplaneProfile("VMAX Planes"));
        File.WriteAllText(Path.Combine(remote, "inputprofile_0000000102"), GeneralProfile("VMAX General"));
        File.WriteAllBytes(Path.Combine(remote, "profile_00"), SimProfile00.Build(new SimProfile00.Device(VmaxId, 413, null, null,
            [new SimProfile00.Rec("asobo-aircraft-c172sp-as1000", "inputprofile_0000000102", "inputprofile_0000000101", 3)])));
        byte[] records = File.ReadAllBytes(Path.Combine(remote, "profile_00"));
        string roaming = Path.Combine(home, "Roaming", "Microsoft Flight Simulator 2024");
        string community = Path.Combine(roaming, "Packages", "Community2024");
        Directory.CreateDirectory(community);
        File.WriteAllText(Path.Combine(roaming, "UserCfg.opt"), "InstalledPackagesPath \"" + Path.Combine(roaming, "Packages") + "\"\r\n");
        Engine(home, "games", "install", "set", "MSFS2024", roaming);
        connectedDevices = Path.Combine(root, "devices.tsv");
        File.WriteAllText(connectedDevices, Vmax + "\t3344\t019D\tVPC VMAX Prime Throttle\r\n");
        string profile = Path.Combine(root, "p.pr0file");
        Assert.Equal(0, Engine(home, "--profile", profile, "msfs2024", "import", "--text").Code);
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "AAA56", "--node", "MSFS2024:KEY_ATC_MENU_0", "--device", Vmax, "--input", "JOY_BTN2", "--text").Code);

        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--text");

        Assert.True(code == 0, export);
        Assert.Contains("your profile 'VMAX General' (active in 1 of 1 aircraft record(s)): 3 bind(s)", export);
        Assert.Contains("(Steam, inputprofile_0000000102)", export);
        Assert.Matches("(?s)KEY_ATC_MENU_0\"[^>]*>\\s*<Primary>\\s*<KEY Information=\"Joystick Button 2\">1</KEY>", File.ReadAllText(Path.Combine(remote, "inputprofile_0000000102")));
        // profile_00 is never written; the old file is in the backups.
        Assert.Equal(records, File.ReadAllBytes(Path.Combine(remote, "profile_00")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data", "Backups", "MSFS"), "inputprofile_0000000102.*.jpbak"));
        Assert.Equal(["inputprofile_0000000101", "inputprofile_0000000102", "profile_00"], Directory.GetFiles(remote).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // --user-profiles false: the import files carry the binds, named like the active profile (Import overwrites it).
    [Fact]
    public void Store2024_Export_UserProfilesOff_ImportFileNamedLikeTheActiveProfile()
    {
        (string home, string user, string community, string profile) = StoreDirectSetup();
        Assert.Equal(0, Engine(home, "--profile", profile, "relations", "add", "JP Gear", "--node", "MSFS2024:KEY_GEAR_TOGGLE", "--device", Vmax, "--input", "JOY_BTN5", "--text").Code);
        Dictionary<string, string> hashes = Hashes(user);
        (int code, string export) = Engine(home, "--profile", profile, "msfs2024", "export", community, "--user-profiles", "false", "--text");
        Assert.True(code == 0, export);
        Assert.Contains("Writing into your own MSFS 2024 profiles is off", export);
        Assert.Equal(hashes, Hashes(user));
        string folder = Path.Combine(root, "data", "MSFS 2024 import files");
        string planes = File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "*_Airplanes.xml")));
        Assert.Contains(">VMAX Planes</FriendlyName>", planes);
        Assert.Contains("Import it and confirm 'overwrite': your profile 'VMAX Planes' then has JoyPro's binds.", export);
        // Without records (no profile_00) the file is a new "JoyPro ..." profile.
    }

    private Process StartFakeProcess(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, true);
        Process process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        System.Threading.Thread.Sleep(300);
        return process;
    }
}
