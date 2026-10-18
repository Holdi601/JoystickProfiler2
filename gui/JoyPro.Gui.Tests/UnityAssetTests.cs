using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.CockpitPictures;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Unity asset reader behind the Nuclear Option cockpit pictures (engine/CockpitPictures/UnityAssets.cs): the
// serialized file's header, type trees and objects, JoyPro's own class layouts, the mesh channels, the textures, and the
// Nuclear Option builder's frame conversion, names and action list.
public sealed class UnityAssetTests
{
    // ------------------------------------------------------------------ a tiny serialized file

    private sealed class Node
    {
        public string Type = "", Name = "";
        public int Level, Size, Flags;
        public bool IsArray;
    }

    private static void Align(BinaryWriter w)
    {
        while (w.BaseStream.Position % 4 != 0)
            w.Write((byte)0);
    }

    private static void CString(BinaryWriter w, string s)
    {
        w.Write(Encoding.UTF8.GetBytes(s));
        w.Write((byte)0);
    }

    private static void BigEndian(BinaryWriter w, uint v)
    {
        w.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }

    private static void BigEndian(BinaryWriter w, long v)
    {
        BigEndian(w, (uint)(v >> 32));
        BigEndian(w, (uint)v);
    }

    // A format 22 file (Unity 2022) with one type and one object; the type tree only when nodes are given.
    private static byte[] SerializedFile(int classId, List<Node>? nodes, long pathId, byte[] data)
    {
        MemoryStream meta = new MemoryStream();
        BinaryWriter m = new BinaryWriter(meta);
        CString(m, "2022.3.62f2");
        m.Write(19);
        m.Write((byte)(nodes != null ? 1 : 0));
        m.Write(1);
        m.Write(classId);
        m.Write((byte)0);
        m.Write((short)-1);
        m.Write(new byte[16]);
        if (nodes != null)
        {
            // Strings: Unity's shared ones by offset (top bit), the rest in the type's own buffer.
            MemoryStream strings = new MemoryStream();
            Func<string, uint> offset = s =>
            {
                uint common = UnityTypeNode.CommonOffset(s);
                if (common != uint.MaxValue)
                    return common;
                uint at = (uint)strings.Length;
                byte[] b = Encoding.UTF8.GetBytes(s + "\0");
                strings.Write(b, 0, b.Length);
                return at;
            };
            List<byte[]> rows = new List<byte[]>();
            foreach (Node n in nodes)
            {
                MemoryStream row = new MemoryStream();
                BinaryWriter r = new BinaryWriter(row);
                r.Write((ushort)1);
                r.Write((byte)n.Level);
                r.Write((byte)(n.IsArray ? 1 : 0));
                r.Write(offset(n.Type));
                r.Write(offset(n.Name));
                r.Write(n.Size);
                r.Write(0);
                r.Write(n.Flags);
                r.Write(0UL);
                rows.Add(row.ToArray());
            }
            m.Write(nodes.Count);
            m.Write((int)strings.Length);
            foreach (byte[] row in rows)
                m.Write(row);
            m.Write(strings.ToArray());
            m.Write(0);
        }
        m.Write(1);
        // The header is 48 bytes; the metadata follows it, so alignment is relative to the file start.
        while ((48 + meta.Position) % 4 != 0)
            m.Write((byte)0);
        m.Write(pathId);
        m.Write(0L);
        m.Write((uint)data.Length);
        m.Write(0);
        m.Write(0);
        m.Write(0);
        m.Write(0);
        CString(m, "");
        byte[] metadata = meta.ToArray();
        long dataOffset = (48 + metadata.Length + 15) / 16 * 16;
        MemoryStream file = new MemoryStream();
        BinaryWriter f = new BinaryWriter(file);
        BigEndian(f, 0u);
        BigEndian(f, 0u);
        BigEndian(f, 22u);
        BigEndian(f, 0u);
        f.Write(new byte[4]);
        BigEndian(f, (uint)metadata.Length);
        BigEndian(f, dataOffset + data.Length);
        BigEndian(f, dataOffset);
        BigEndian(f, 0L);
        f.Write(metadata);
        while (file.Position < dataOffset)
            f.Write((byte)0);
        f.Write(data);
        return file.ToArray();
    }

    private static List<Node> TextAssetTree()
    {
        return new List<Node>
        {
            new Node { Type = "TextAsset", Name = "Base", Level = 0, Size = -1 },
            new Node { Type = "string", Name = "m_Name", Level = 1, Size = -1 },
            new Node { Type = "Array", Name = "Array", Level = 2, Size = -1, IsArray = true, Flags = UnityTypeNode.AlignFlag },
            new Node { Type = "int", Name = "size", Level = 3, Size = 4 },
            new Node { Type = "char", Name = "data", Level = 3, Size = 1 },
            new Node { Type = "int", Name = "m_Custom", Level = 1, Size = 4 },
            new Node { Type = "vector", Name = "m_Points", Level = 1, Size = -1 },
            new Node { Type = "Array", Name = "Array", Level = 2, Size = -1, IsArray = true, Flags = UnityTypeNode.AlignFlag },
            new Node { Type = "int", Name = "size", Level = 3, Size = 4 },
            new Node { Type = "Vector2f", Name = "data", Level = 3, Size = 8 },
            new Node { Type = "float", Name = "x", Level = 4, Size = 4 },
            new Node { Type = "float", Name = "y", Level = 4, Size = 4 }
        };
    }

    [Fact]
    public void SerializedFile_ReadsTheHeaderTheTypeTreeAndAnObject()
    {
        MemoryStream d = new MemoryStream();
        BinaryWriter w = new BinaryWriter(d);
        w.Write(5);
        w.Write(Encoding.UTF8.GetBytes("Hello"));
        Align(w);
        w.Write(42);
        w.Write(2);
        w.Write(1.5f);
        w.Write(-2f);
        w.Write(3f);
        w.Write(4f);
        byte[] file = SerializedFile(49, TextAssetTree(), 7, d.ToArray());
        using UnityAssetFile f = UnityAssetFile.Parse(new MemoryStream(file));
        Assert.Equal(22, f.Format);
        Assert.Equal("2022.3.62f2", f.UnityVersion);
        Assert.True(f.HasTypeTrees);
        Assert.Single(f.Types);
        UnityTypeNode tree = f.Types[0].Tree!;
        Assert.Equal("TextAsset", tree.Type);
        Assert.Equal(new[] { "m_Name", "m_Custom", "m_Points" }, tree.Children.Select(c => c.Name).ToArray());
        Assert.True(tree.Children[0].Children[0].IsArray);
        UnityObjectInfo o = f.Objects[7];
        Assert.Equal(49, o.ClassId);
        Assert.Equal("Hello", f.NameOf(o));
        Dictionary<string, object>? v = f.Read(7);
        Assert.NotNull(v);
        Assert.Equal("Hello", UnityAssetFile.Str(v!, "m_Name"));
        Assert.Equal(42, UnityAssetFile.Num(v!, "m_Custom"));
        List<object> points = UnityAssetFile.List(v!, "m_Points");
        Assert.Equal(2, points.Count);
        Assert.Equal(-2, UnityAssetFile.Num((Dictionary<string, object>)points[0], "y"));
        Assert.False(v!.ContainsKey("_rest"));
    }

    [Fact]
    public void SerializedFile_WithoutTypeTreesReadsATransformWithJoyProsLayout()
    {
        MemoryStream d = new MemoryStream();
        BinaryWriter w = new BinaryWriter(d);
        w.Write(0);
        w.Write(11L);                      // game object
        w.Write(0f); w.Write(0.7071068f); w.Write(0f); w.Write(0.7071068f); // rotation x y z w
        w.Write(1f); w.Write(2f); w.Write(3f);                               // position
        w.Write(1f); w.Write(1f); w.Write(1f);                               // scale
        w.Write(1);
        w.Write(0); w.Write(12L);          // one child
        w.Write(0); w.Write(0L);           // no father
        byte[] file = SerializedFile(4, null, 5, d.ToArray());
        using UnityAssetFile f = UnityAssetFile.Parse(new MemoryStream(file));
        Assert.False(f.HasTypeTrees);
        Dictionary<string, object>? t = f.Read(5);
        Assert.NotNull(t);
        Assert.Equal(11, UnityAssetFile.PathIdOf(UnityAssetFile.Field(t!, "m_GameObject")));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, UnityAssetFile.Vector(t!, "m_LocalPosition", "x", "y", "z"));
        Assert.Equal(12, UnityAssetFile.PathIdOf(UnityAssetFile.List(t!, "m_Children")[0]));
        Assert.Equal(0, UnityAssetFile.PathIdOf(UnityAssetFile.Field(t!, "m_Father")));
    }

    [Fact]
    public void SerializedFile_ALayoutThatDoesNotFitReadsAsNothing()
    {
        // A Transform with four bytes too many: JoyPro's layout does not fit, so no values (rather than wrong ones).
        byte[] data = new byte[12 + 16 + 12 + 12 + 4 + 12 + 4];
        byte[] file = SerializedFile(4, null, 5, data);
        using UnityAssetFile f = UnityAssetFile.Parse(new MemoryStream(file));
        Assert.Null(f.Read(5));
        Assert.Null(f.Read(99));
    }

    [Fact]
    public void SerializedFile_RejectsWhatIsNoSerializedFile()
    {
        Assert.ThrowsAny<Exception>(() => UnityAssetFile.Parse(new MemoryStream(Encoding.ASCII.GetBytes("UnityFS\0 5.x.x 2022.3.62f2 and some more bytes here"))));
    }

    [Fact]
    public void Layouts_ExistForTheCockpitClassesOfUnity2022Only()
    {
        foreach (int id in new[] { 1, 4, 21, 23, 28, 33, 43, 49, 114 })
            Assert.NotNull(UnityLayouts.For(id, "2022.3.62f2"));
        Assert.Null(UnityLayouts.For(43, "2021.3.1f1"));
        Assert.Null(UnityLayouts.For(43, "6000.0.1f1"));
        Assert.Null(UnityLayouts.For(108, "2022.3.62f2"));
        UnityTypeNode tex = UnityLayouts.For(28, "2022.3.62f2")!;
        Assert.Contains(tex.Children, c => c.Name == "image data" && c.Type == "TypelessData");
        UnityTypeNode mesh = UnityLayouts.For(43, "2022.3.62f2")!;
        Assert.Equal("m_StreamData", mesh.Children.Last().Name);
    }

    // ------------------------------------------------------------------ meshes

    [Fact]
    public void Mesh_DecodesChannelsInTwoStreamsAndTheSubMeshes()
    {
        // Three vertices: stream 0 position (float x3) and normal (half x3 + pad: dimension 4), stream 1 uv0 (unorm16 x2).
        List<int[]> channels = new List<int[]>
        {
            new[] { 0, 0, 0, 3 },   // position
            new[] { 0, 12, 1, 4 },  // normal, float16
            new[] { 0, 0, 0, 0 },   // tangent: none
            new[] { 0, 0, 0, 0 },   // colour: none
            new[] { 1, 0, 4, 2 }    // uv0, unorm16
        };
        MemoryStream d = new MemoryStream();
        BinaryWriter w = new BinaryWriter(d);
        for (int v = 0; v < 3; v++)
        {
            w.Write((float)v);
            w.Write(10f + v);
            w.Write(-1f);
            w.Write((ushort)0x3C00); // 1.0
            w.Write((ushort)0x0000);
            w.Write((ushort)0xBC00); // -1.0
            w.Write((ushort)0);
        }
        // stride 20 * 3 = 60 bytes: stream 1 starts at 64.
        while (d.Length < 64)
            w.Write((byte)0xEE);
        for (int v = 0; v < 3; v++)
        {
            w.Write((ushort)(v == 1 ? 65535 : 0));
            w.Write((ushort)(v == 2 ? 65535 : 0));
        }
        UnityMesh mesh = new UnityMesh();
        mesh.Decode(d.ToArray(), 3, channels);
        Assert.Equal(string.Empty, mesh.Problem);
        Assert.Equal(new float[] { 0, 10, -1, 1, 11, -1, 2, 12, -1 }, mesh.Positions);
        Assert.Equal(new float[] { 1, 0, -1 }, mesh.Normals!.Take(3).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1 }, mesh.Uvs);
        // 16-bit indices: a triangle list with a base vertex, and a quad (two triangles).
        byte[] index = new ushort[] { 0, 1, 1, 0, 1, 2, 0 }.SelectMany(BitConverter.GetBytes).ToArray();
        mesh.Indices(index, 0, new List<Dictionary<string, object>>
        {
            new Dictionary<string, object> { { "firstByte", 0u }, { "indexCount", 3u }, { "topology", 0 }, { "baseVertex", 1u } },
            new Dictionary<string, object> { { "firstByte", 6u }, { "indexCount", 4u }, { "topology", 2 }, { "baseVertex", 0u } }
        });
        Assert.Equal(new[] { 1, 2, 2 }, mesh.SubMeshes[0]);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 0 }, mesh.SubMeshes[1]);
    }

    [Fact]
    public void Mesh_HalfFloatsAndNormalisedFormats()
    {
        Assert.Equal(1f, UnityMesh.Half(0x3C00));
        Assert.Equal(-2f, UnityMesh.Half(0xC000));
        Assert.Equal(0.5f, UnityMesh.Half(0x3800));
        Assert.Equal(-1f, UnityMesh.Component(new byte[] { 0x81 }, 0, 3, false));
        Assert.Equal(1f, UnityMesh.Component(new byte[] { 255 }, 0, 2, false));
        Assert.Equal(4, UnityMesh.FormatSize(0, false));
        Assert.Equal(2, UnityMesh.FormatSize(1, false));
        Assert.Equal(1, UnityMesh.FormatSize(2, false));
    }

    // ------------------------------------------------------------------ textures

    [Fact]
    public void Texture_Dxt1DataBecomesAPicture()
    {
        byte[] block = { 0x00, 0xF8, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x40 };
        byte[] dds = UnityTextures.Dds(block, 4, 4, 1, 10)!;
        TextureImage? img = TextureImage.Decode(dds, "x.dds", 1024);
        Assert.NotNull(img);
        Assert.Equal(255, img!.Rgba[0]);
        Assert.Equal(255, img.Rgba[4 * 15 + 2]);
        // RGBA32 (4) as it is; an unknown format (crunched DXT1, 28) is left out.
        byte[] rgba = Enumerable.Range(0, 16).SelectMany(i => new byte[] { 10, 20, 30, 40 }).ToArray();
        TextureImage? raw = TextureImage.Decode(UnityTextures.Dds(rgba, 4, 4, 1, 4)!, "x.dds", 1024);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, raw!.Rgba.Take(4).ToArray());
        Assert.Null(UnityTextures.Dds(block, 4, 4, 1, 28));
    }

    // ------------------------------------------------------------------ Nuclear Option

    [Fact]
    public void NuclearOption_UnityFrameBecomesTheScenesFrame()
    {
        // Unity: x right, y up, z forward; the scene: x forward, y up, z right.
        Vec3 s = NuclearOptionCockpitPictures.ToScene(new Vec3(1, 2, 3));
        Assert.Equal(3, s.X);
        Assert.Equal(2, s.Y);
        Assert.Equal(1, s.Z);
        // A quarter turn about up (Unity's quaternion y = w = 0.7071) turns forward to the right in both frames.
        double[] m = NuclearOptionCockpitPictures.LocalMatrix(new double[] { 0, 0, 0 }, new[] { 0, 0.7071068, 0, 0.7071068 }, new double[] { 1, 1, 1 });
        Vec3 turned = NuclearOptionCockpitPictures.ToScene(Mat4.Point(m, 0, 0, 1));
        Assert.Equal(0, turned.X, 4);
        Assert.Equal(1, turned.Z, 4);
        // Translation after rotation and scale (T * R * S).
        double[] t = NuclearOptionCockpitPictures.LocalMatrix(new double[] { 1, 2, 3 }, new double[] { 0, 0, 0, 1 }, new double[] { 2, 2, 2 });
        Vec3 p = Mat4.Point(t, 1, 1, 1);
        Assert.Equal(3, p.X, 6);
        Assert.Equal(4, p.Y, 6);
        Assert.Equal(5, p.Z, 6);
    }

    [Fact]
    public void NuclearOption_TheGamesAircraftNamesFindTheCatalogsVariants()
    {
        // The names the builder writes into index.json (unit name, code, prefab name), as Nuclear Option names them.
        string[][] sets =
        {
            new[] { "CI-22 Cricket", "COIN" }, new[] { "T/A-30 Compass", "trainer" }, new[] { "A-19 Brawler", "A-19", "CAS1" },
            new[] { "FS-12 Revoker", "Fighter1" }, new[] { "KR-67 Ifrit", "KR-67", "Multirole1" }, new[] { "SFB-81 Darkreach", "Darkreach" },
            new[] { "Alkyon AB-4", "AB-4", "FastBomber1" }, new[] { "VT-7 Vagrant", "VT-7", "VTOLTrainer1" }, new[] { "FS-20 Vortex", "FS-20", "SmallFighter1" },
            new[] { "EW-25 Medusa", "EW-25", "EW1" }, new[] { "VL-49 Tarantula", "VL-49", "QuadVTOL1" }, new[] { "SAH-46 Chicane", "SAH-46", "AttackHelo1" },
            new[] { "UH-90 Ibis", "UH-90", "UtilityHelo1" }
        };
        Dictionary<string, string> index = JoyPro.DeviceReference.CockpitPictureSet.IndexOf(sets.SelectMany(s => s.Select(n => new KeyValuePair<string, string>(n, s.Last()))));
        JoyPro.DeviceReference.CockpitCatalog catalog = JoyPro.DeviceReference.CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        List<JoyPro.DeviceReference.CockpitVariant> variants = catalog.Aircraft.SelectMany(a => a.Variants).Select(catalog.Resolve)
            .Where(v => v.Games.Any(g => g.Game == "NuclearOption") && v.Id.StartsWith("no-", StringComparison.Ordinal)).ToList();
        Assert.True(variants.Count >= 13);
        foreach (JoyPro.DeviceReference.CockpitVariant v in variants)
        {
            Tuple<string, string>? hit = JoyPro.DeviceReference.CockpitPictureSet.Locate(v, game => game == "NuclearOption" ? index : new Dictionary<string, string>(), (g, f) => true);
            Assert.True(hit != null, v.Id + " finds no set");
        }
        Assert.Equal("CAS1", JoyPro.DeviceReference.CockpitPictureSet.Locate(variants.First(v => v.Id == "no-a-19-brawler"), g => index, (g, f) => true)!.Item2);
    }

    private static void Str(BinaryWriter w, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
        Align(w);
    }

    [Fact]
    public void NuclearOption_ReadsTheUnitNameAndCodeOfAnAircraftDefinition()
    {
        MemoryStream d = new MemoryStream();
        BinaryWriter w = new BinaryWriter(d);
        w.Write(1f);
        w.Write(0);
        Str(w, "CAS1");
        Str(w, "A-19 Brawler");
        Str(w, "");
        Str(w, "The Brawler is a reliable and rugged platform.\nSecond line.");
        Str(w, "A-19");
        w.Write(5);
        Assert.Equal(new[] { "A-19 Brawler", "A-19" }, NuclearOptionCockpitPictures.DefinitionNames(d.ToArray(), "CAS1"));
        Assert.Empty(NuclearOptionCockpitPictures.DefinitionNames(d.ToArray(), "COIN"));
    }

    [Fact]
    public void NuclearOption_ReadsTheGamesActionListWithItsCategories()
    {
        MemoryStream d = new MemoryStream();
        BinaryWriter w = new BinaryWriter(d);
        Str(w, "Player0");
        w.Write(7);
        w.Write(3);
        foreach (var a in new[] { (0, "Pitch", 0, "Pitch Axis", "Pitch Up", "Pitch Down", 1), (5, "Fire", 1, "Fire", "", "", 1), (17, "Center", 1, "Center View", "", "", 2) })
        {
            w.Write(a.Item1);
            Str(w, a.Item2);
            w.Write(a.Item3);
            Str(w, a.Item4);
            Str(w, a.Item5);
            Str(w, a.Item6);
            Str(w, "");
            Str(w, "");
            Str(w, "");
            w.Write(0);
            w.Write(1);
            w.Write(a.Item7);
        }
        w.Write(3);
        foreach (var c in new[] { (0, "Default", "Default"), (1, "Flight", "Flight Controls"), (2, "Camera", "Camera Controls") })
        {
            Str(w, c.Item2);
            Str(w, c.Item3);
            Str(w, "");
            Str(w, "");
            w.Write(c.Item1);
            w.Write(1);
        }
        List<NuclearOptionCockpitPictures.GameAction> list = NuclearOptionCockpitPictures.ParseActions(d.ToArray());
        Assert.Equal(3, list.Count);
        Assert.Equal("Pitch", list[0].Name);
        Assert.Equal("Pitch Axis", list[0].Title);
        Assert.True(list[0].Axis);
        Assert.Equal("Pitch Down", list[0].Negative);
        Assert.Equal("Flight Controls", list[0].Category);
        Assert.Equal("Camera Controls", list[2].Category);
        Assert.False(list[2].Axis);
        Assert.Empty(NuclearOptionCockpitPictures.ParseActions(new byte[64]));
    }
}
