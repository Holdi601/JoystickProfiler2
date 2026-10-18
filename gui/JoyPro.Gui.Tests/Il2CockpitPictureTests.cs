using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.CockpitPictures;
using Xunit;

namespace JoyPro.Gui.Tests;

// IL-2 Great Battles cockpit pictures (docs/cockpits.md "Pictures from your games"): the game's archives (.gtp), the
// cockpit models (.mgm) and how the cockpit's control arguments are named.
public sealed class Il2CockpitPictureTests
{
    // ------------------------------------------------------------------ archives

    private static byte[] Record(string tag, int kind, uint offset, uint size, string name)
    {
        byte[] n = Encoding.ASCII.GetBytes(name + "\0");
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes(tag));
        w.Write(kind);
        w.Write(offset);
        w.Write(size);
        w.Write(0x12345678u);
        w.Write(0u);
        w.Write((ulong)n.Length);
        w.Write(n);
        w.Flush();
        return m.ToArray();
    }

    private static byte[] Archive(Dictionary<string, byte[]> files)
    {
        // Directory first (its size decides where the files go), then the files behind their STRMFILE headers.
        List<byte[]> records = new List<byte[]> { Record("DIR!", 2, 3, 0, ""), Record("DIR!", 2, 4, 0, "/graphics") };
        int dirSize = records.Sum(r => r.Length) + files.Sum(f => Record("FILE", 1, 0, 0, f.Key).Length);
        uint offset = (uint)(0x40 + dirSize);
        List<byte[]> payload = new List<byte[]>();
        foreach (KeyValuePair<string, byte[]> f in files)
        {
            records.Add(Record("FILE", 1, offset, (uint)f.Value.Length, f.Key));
            byte[] head = new byte[32];
            Encoding.ASCII.GetBytes("STRMFILE").CopyTo(head, 0);
            payload.Add(head);
            payload.Add(f.Value);
            offset += (uint)(32 + f.Value.Length);
        }
        byte[] dir = records.SelectMany(r => r).ToArray();
        byte[] header = new byte[0x40];
        Encoding.ASCII.GetBytes("S16R!").CopyTo(header, 0);
        BitConverter.GetBytes(offset).CopyTo(header, 8);
        BitConverter.GetBytes((uint)(records.Count)).CopyTo(header, 12);
        Encoding.ASCII.GetBytes("STRMFAT?").CopyTo(header, 0x20);
        BitConverter.GetBytes((uint)dir.Length).CopyTo(header, 0x28);
        return header.Concat(dir).Concat(payload.SelectMany(p => p)).ToArray();
    }

    [Fact]
    public void Gtp_ReadsTheDirectoryAndTheStoredFiles_AndLeavesOtherArchivesAlone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "il2gtp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] mgm = { 1, 2, 3, 4, 5 };
            byte[] text = Encoding.ASCII.GetBytes("object_name = \"Bf 109 F-4\"");
            File.WriteAllBytes(Path.Combine(dir, "Planes.gtp"), Archive(new Dictionary<string, byte[]>
            {
                { "/graphics/planes/Bf109F4/Bf109F4-C.mgm", mgm },
                { "/luascripts/worldobjects/planes/bf109f4.txt", text }
            }));
            File.WriteAllBytes(Path.Combine(dir, "Other.gtp"), Encoding.ASCII.GetBytes("not an archive of this layout at all......................................"));
            using Il2Gtp gtp = Il2Gtp.Open(dir);
            Assert.Equal(2, gtp.Count);
            Assert.Single(gtp.Archives);
            Assert.True(gtp.Has("graphics\\planes\\bf109f4\\bf109f4-c.mgm"));
            Assert.Equal(mgm, gtp.Read("/graphics/planes/bf109f4/bf109f4-c.mgm"));
            Assert.Equal("object_name = \"Bf 109 F-4\"", gtp.ReadText("/luascripts/worldobjects/planes/bf109f4.txt"));
            Assert.Null(gtp.Read("/graphics/planes/none.mgm"));
            Assert.Equal(new[] { "/luascripts/worldobjects/planes/bf109f4.txt" }, gtp.Paths("/luascripts/").ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ models

    private sealed class MgmWriter
    {
        public readonly MemoryStream M = new MemoryStream();
        public readonly BinaryWriter W;

        public MgmWriter()
        {
            W = new BinaryWriter(M);
        }

        public void Str(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            W.Write(b.Length);
            W.Write(b);
        }

        public void Ptr(string type, string id)
        {
            Str(type);
            W.Write(0);
            Str(id);
        }

        public void Null()
        {
            W.Write(0);
            W.Write(0);
            W.Write(0);
        }

        public void Matrix(double tx, double ty, double tz)
        {
            float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (float)tx, (float)ty, (float)tz, 1 };
            foreach (float f in m)
                W.Write(f);
        }

        public void Key(float time, bool visible)
        {
            byte[] k = new byte[Il2Mgm.KeySize];
            BitConverter.GetBytes(time).CopyTo(k, 0);
            k[6] = (byte)(visible ? 1 : 0);
            BitConverter.GetBytes((short)32767).CopyTo(k, 26);
            for (int i = 0; i < 3; i++)
                BitConverter.GetBytes((ushort)1000).CopyTo(k, 28 + 2 * i);
            W.Write(k);
        }
    }

    // A root node, a stick node with one triangle in a chunk, and an animation of argument 406 that hides the stick
    // from 0.5 on.
    private static byte[] TinyModel()
    {
        MgmWriter w = new MgmWriter();
        w.Ptr("class model::Model", "0");
        // root
        w.Ptr("struct model::Node", "1");
        w.W.Write(0);
        w.Str("Scene Root");
        for (int i = 0; i < 6; i++)
            w.W.Write(0f);
        w.Null();
        w.W.Write(1);
        w.Matrix(0, 0, 0);
        w.W.Write(0);
        w.Matrix(0, 0, 0);
        for (int i = 0; i < 12; i++)
            w.W.Write(0);
        w.W.Write(36);
        w.Str("Specular");
        w.W.Write(1);
        // the stick (a child of the root)
        w.Ptr("struct model::Node", "2");
        w.W.Write(1);
        w.Str("Z_Stick");
        for (int i = 0; i < 6; i++)
            w.W.Write(0f);
        w.Ptr("struct model::Node", "1");
        w.W.Write(0);
        w.Matrix(1, 0, 0);
        w.W.Write(1);
        w.Matrix(0, 2, 0);
        int[] words = { 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 0, 0 };
        foreach (int x in words)
            w.W.Write(x);
        w.W.Write(36);
        w.Str("Specular");
        w.W.Write(0);
        w.W.Write((ushort)1);
        w.W.Write(0);
        // its chunk
        w.Ptr("struct model::Chunk", "3");
        w.Str("Specular");
        w.W.Write(new byte[] { 80, 90, 100, 26, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        w.W.Write(1);
        w.Str("Stick_C.tga");
        w.W.Write(3);
        w.W.Write(3);
        long offsets = w.M.Position;
        w.W.Write(0);
        w.W.Write(0);
        w.W.Write((byte)0);
        w.W.Write(36);
        w.W.Write(1);
        w.Ptr("struct model::Node", "2");
        w.W.Write(0);
        w.W.Write(0);
        // end of the chunk; the stick's slot
        w.W.Write(0);
        // back in the root: no geometry of its own
        w.W.Write((ushort)0);
        w.W.Write(0);
        w.Null();
        w.W.Write(0);
        // the animations
        w.W.Write(1);
        w.W.Write(406);
        w.Ptr("struct model::Animation", "4");
        w.W.Write(1);
        w.W.Write((ushort)1);
        w.W.Write((ushort)256);
        w.W.Write(2 * Il2Mgm.KeySize);
        w.Key(0, true);
        w.Key(0.5f, false);
        w.W.Write(1);
        w.W.Write(1);
        w.W.Write(200);
        // the vertices and the triangle
        long vertices = w.M.Position;
        float[][] v = { new[] { 0f, 0, 0 }, new[] { 1f, 0, 0 }, new[] { 0f, 1, 0 } };
        foreach (float[] p in v)
        {
            w.W.Write(p[0]);
            w.W.Write(p[1]);
            w.W.Write(p[2]);
            w.W.Write(0f);
            w.W.Write(0f);
            w.W.Write(1f);
            w.W.Write(p[0]);
            w.W.Write(p[1]);
            w.W.Write(0);
        }
        long indices = w.M.Position;
        w.W.Write((ushort)0);
        w.W.Write((ushort)1);
        w.W.Write((ushort)2);
        w.W.Flush();
        byte[] data = w.M.ToArray();
        BitConverter.GetBytes((uint)vertices).CopyTo(data, offsets);
        BitConverter.GetBytes((uint)indices).CopyTo(data, offsets + 4);
        return data;
    }

    [Fact]
    public void Mgm_ReadsTheNodesTheChunkTheGeometryAndTheAnimation()
    {
        Il2Mgm m = Il2Mgm.Read(TinyModel());
        Assert.Equal("Scene Root", m.Root.Name);
        Assert.Equal(2, m.Nodes.Count);
        Il2Mgm.Node stick = m.Nodes.Single(n => n.Name == "Z_Stick");
        Assert.Same(m.Root, stick.Parent);
        Assert.Same(stick, m.Root.Children.Single());
        Assert.NotNull(stick.Chunk);
        Assert.Equal("Stick_C.tga", stick.Chunk.Textures.Single());
        Assert.Same(stick, stick.Chunk.Nodes.Single());
        Assert.Equal(new[] { 0, 1, 2 }, m.Indices(stick));
        float x, y, z, nx, ny, nz, u, w;
        Assert.True(m.Vertex(stick.Chunk, 1, out x, out y, out z, out nx, out ny, out nz, out u, out w));
        Assert.Equal(1f, x);
        Assert.Equal(1f, nz);
        // The geometry: offset (0, 2, 0) in the node, the node at (1, 0, 0) in the root.
        Vec3 p = Mat4.Point(Il2Mgm.World(stick), 1, 0, 0);
        Assert.Equal(2, p.X, 5);
        Assert.Equal(2, p.Y, 5);
        Assert.Equal(1, Mat4.Point(Il2Mgm.Pivot(stick), 0, 0, 0).X, 5);
        // The animation: argument 406 moves the stick; it shows it below 0.5 and hides it from there.
        Il2Mgm.Animation a = m.Animations.Single();
        Assert.Equal(406, a.Arg);
        Assert.Same(stick, m.NodesOf(406).Single());
        Assert.Empty(m.Hidden(_ => 0.2));
        Assert.Contains(stick, m.Hidden(_ => 0.7));
        // An identity key leaves the pose as it is.
        double[] k = Il2Mgm.KeyAt(a.Tracks[0], 0.2)!;
        Assert.Equal(0, Mat4.Point(k, 0, 0, 0).Length, 6);
    }

    [Fact]
    public void Mgm_RejectsWhatIsNoModel()
    {
        Assert.ThrowsAny<Exception>(() => Il2Mgm.Read(Encoding.ASCII.GetBytes("\u0004\0\0\0junk")));
    }

    // ------------------------------------------------------------------ names

    private static Il2Input Input()
    {
        string actions = "&planecontrols=action,axisType|\r\ngear|\r\ngear_up|\r\ngear_dn|\r\nflaps_up|\r\nflaps_dn|\r\n" +
                         "rpc_all_engines_throttles,2,lm|\r\nrpc_engine_1_throttle,2|\r\nrpc_engine_2_throttle,2|\r\nlaunch_rockets_command|\r\n";
        string locale = "﻿\r\n&inpgear=Gear up/down\r\n&inpgear_up=Gear up\r\n&inpgear_dn=Gear down\r\n&inpflaps_dn=Flaps down\r\n&inpflaps_up=Flaps up\r\n" +
                        "&inprpc_all_engines_throttles=Engines throttle control\r\n&inprpc_engine_1_throttle=Engine 1 throttle control\r\n&inplaunch_rockets_command=Launch rockets\r\n";
        return Il2Input.Parse(actions, locale);
    }

    [Fact]
    public void Labels_UseTheGamesActionNames_WhereTheArgumentIsPlainlyThatControl()
    {
        Il2Input input = Input();
        Il2CockpitPictures.CalloutText gear = Il2CockpitPictures.Label(423, "Control landing gears", null, input, 1)!;
        Assert.Equal("Gear up/down", gear.Label);
        Assert.Equal(new[] { "gear", "gear_up", "gear_dn" }, gear.Commands.Select(c => c.Id).ToArray());
        Assert.Equal("Gear up", gear.Commands[1].Name);
        // Flaps: the two directions name it together.
        Assert.Equal("Flaps down / Flaps up", Il2CockpitPictures.Label(430, "Control flaps", null, input, 1)!.Label);
        // One engine: the all-engines action first; two engines: the engine's own.
        Assert.Equal("Engines throttle control", Il2CockpitPictures.Label(1100, null, null, input, 1)!.Label);
        Assert.Equal("Engine 1 throttle control", Il2CockpitPictures.Label(1100, "Control throttle E1", null, input, 2)!.Label);
        // No action for it: the argument's own name.
        Assert.Equal("Magneto", Il2CockpitPictures.Label(1152, "Control magneto", null, input, 1)!.Label);
        // Gauges and lamps are no callouts; a number used for something else in this cockpit is not that control.
        Assert.Null(Il2CockpitPictures.Label(8201, "Air speed", null, input, 1));
        Assert.Null(Il2CockpitPictures.Label(9003, "MG151-20 Shoot Ind", null, input, 1));
        Assert.Null(Il2CockpitPictures.Label(9003, null, null, input, 1));
        Assert.Equal("Launch rockets", Il2CockpitPictures.Label(9003, "Control rocket trigger", null, input, 1)!.Label);
        // A cockpit without names uses the name all cockpits give the number.
        Dictionary<int, string> common = new Dictionary<int, string> { { 1110, "Control supercharger" } };
        Assert.Equal("Supercharger", Il2CockpitPictures.Label(1110, null, common, input, 1)!.Label);
        Assert.Null(Il2CockpitPictures.Label(4999, null, common, input, 1));
    }

    [Fact]
    public void CommonNames_KeepOnlyNumbersAllCockpitsUseForTheSameControl()
    {
        Dictionary<int, Dictionary<string, int>> all = new Dictionary<int, Dictionary<string, int>>
        {
            { 423, new Dictionary<string, int> { { "Control landing gears", 5 }, { "Control landing gear", 2 } } },
            { 9003, new Dictionary<string, int> { { "Control rocket trigger", 5 }, { "MG151-20 Shoot Ind", 3 } } }
        };
        Dictionary<int, string> common = Il2CockpitPictures.CommonNames(all);
        Assert.Equal("Control landing gears", common[423]);
        Assert.False(common.ContainsKey(9003));
        Assert.Equal("Landing gears", Il2CockpitPictures.ArgLabel("Control landing gears"));
        Assert.Equal("Throttle E2", Il2CockpitPictures.ArgLabel("2nd Control throttle E2"));
    }

    [Fact]
    public void Plane_NamesEyeAndDefaults_AreReadFromTheGamesScripts()
    {
        Il2CockpitPictures.Plane p = new Il2CockpitPictures.Plane { Key = "yak1s69", Name = "Yak-1 ser.69", Cockpit = "/graphics/planes/yak1s69/yak1s69-c.mgm" };
        List<string> names = Il2CockpitPictures.Names(p);
        Assert.Contains("yak1s69", names);
        Assert.Contains("Yak-1 ser.69", names);
        Assert.Contains("Yak-1 series 69", names);
        Assert.Contains("P-51B", Il2CockpitPictures.Names(new Il2CockpitPictures.Plane { Key = "p51b5", Name = "P-51B-5", Cockpit = "/graphics/planes/p51b5/p51b5-c.mgm" }));
        Assert.Contains("Mk IXc", Il2CockpitPictures.Names(new Il2CockpitPictures.Plane { Key = "spitfiremkixc", Name = "Spitfire Mk.IXc", Cockpit = "/graphics/planes/spitfiremkixc/spitfiremkixc-c.mgm" }));
        string plane = "[attach=0]\r\n\tposition= -0.9, -0.03, 0.025\r\n\tobject = \"LuaScripts/WorldObjects/Bots/BotPilot_Bf109_GER41.txt\"\r\n[end]\r\n" +
                       "CockpitArg = -2, 1000, 0, 00406, 0.5, \"smooth_clamp\", 0.0, \"\" // stick\r\n";
        Vec3 eye = Il2CockpitPictures.Eye(plane, "CloseSphere=-0.8, 0.6, 0.0, 0.7")!.Value;
        Assert.Equal(-0.9, eye.X, 6);
        Assert.Equal(0.69, eye.Y, 6);
        Assert.Equal(-0.8, Il2CockpitPictures.Eye(string.Empty, "CloseSphere=-0.8, 0.6, 0.0, 0.7")!.Value.X, 6);
        Dictionary<int, double> d = Il2CockpitPictures.Defaults("DefArg=2519,1.0\r\nDefArg=-1,0.5\r\n", plane);
        Assert.Equal(0.5, d[406]);
        Assert.Equal(1.0, d[2519]);
        Assert.False(d.ContainsKey(-1));
    }
}
