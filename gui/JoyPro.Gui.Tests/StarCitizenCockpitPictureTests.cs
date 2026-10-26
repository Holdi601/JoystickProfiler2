using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JoyPro.CockpitPictures;
using Xunit;

namespace JoyPro.Gui.Tests;

// Star Citizen cockpit pictures (docs/cockpits.md "Pictures from your games"): the read-only Data.p4k reader (ZIP64 with
// CIG's extra fields, zstd entries, encrypted entries refused, the index cache), object container ZIPs, CryXmlB, the
// DataForge database, the #ivo models, split DDS textures, the materials and the names of grips and callouts, on small
// hand-made data.
public sealed class StarCitizenCockpitPictureTests
{
    // ------------------------------------------------------------------ zstd (raw blocks only, for the tests)

    // A zstd frame holding the bytes in one raw block (single segment, 1-byte content size).
    private static byte[] ZstdRaw(byte[] data)
    {
        Assert.True(data.Length < 256);
        List<byte> f = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0x20, (byte)data.Length };
        int header = 1 | (data.Length << 3);
        f.Add((byte)header);
        f.Add((byte)(header >> 8));
        f.Add((byte)(header >> 16));
        f.AddRange(data);
        return f.ToArray();
    }

    private static int zstdCalls;

    private static byte[] UnZstdRaw(byte[] d, int offset, int length)
    {
        zstdCalls++;
        Assert.Equal(0xFD2FB528u, BitConverter.ToUInt32(d, offset));
        int size = d[offset + 5];
        int header = d[offset + 6] | (d[offset + 7] << 8) | (d[offset + 8] << 16);
        Assert.Equal(1, header & 1);          // last block
        Assert.Equal(0, (header >> 1) & 3);   // raw
        Assert.Equal(size, header >> 3);
        return d.Skip(offset + 9).Take(size).ToArray();
    }

    // ------------------------------------------------------------------ a tiny Data.p4k

    private sealed class P4kEntry
    {
        public string Name = "";
        public byte[] Data = Array.Empty<byte>();
        public int Method;
        public int Size;
        public bool Encrypted;
        public long Offset;
    }

    // ZIP64 like CIG's: every central entry has 0x0001 (size, packed size, offset; the fixed fields 0xFFFFFFFF), 0x5000
    // (128 bytes), 0x5002 (2 bytes: 1 = encrypted), 0x5003 (32 bytes); the extra lengths count their own 4-byte header.
    private static byte[] P4k(List<P4kEntry> entries)
    {
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        foreach (P4kEntry e in entries)
        {
            e.Offset = m.Position;
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            w.Write(0x04034b50u);
            w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)e.Method);
            w.Write(0); w.Write(0);
            w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu);
            w.Write((ushort)name.Length); w.Write((ushort)6);
            w.Write(name);
            w.Write(new byte[6]);
            w.Write(e.Data);
        }
        long cd = m.Position;
        foreach (P4kEntry e in entries)
        {
            byte[] name = Encoding.UTF8.GetBytes(e.Name);
            using MemoryStream x = new MemoryStream();
            using BinaryWriter xw = new BinaryWriter(x);
            xw.Write((ushort)0x0001); xw.Write((ushort)(4 + 24)); xw.Write((long)e.Size); xw.Write((long)e.Data.Length); xw.Write(e.Offset);
            xw.Write((ushort)0x5000); xw.Write((ushort)(4 + 128)); xw.Write(new byte[128]);
            xw.Write((ushort)0x5002); xw.Write((ushort)(4 + 2)); xw.Write((ushort)(e.Encrypted ? 1 : 0));
            xw.Write((ushort)0x5003); xw.Write((ushort)(4 + 32)); xw.Write(new byte[32]);
            xw.Flush();
            byte[] extra = x.ToArray();
            w.Write(0x02014b50u);
            w.Write((ushort)45); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)e.Method);
            w.Write(0); w.Write(0);
            w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu);
            w.Write((ushort)name.Length); w.Write((ushort)extra.Length); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(0);
            w.Write(0xFFFFFFFFu);
            w.Write(name);
            w.Write(extra);
        }
        long cdEnd = m.Position;
        long z64 = m.Position;
        w.Write(0x06064b50u); w.Write(44L); w.Write((ushort)45); w.Write((ushort)45); w.Write(0); w.Write(0);
        w.Write((long)entries.Count); w.Write((long)entries.Count); w.Write(cdEnd - cd); w.Write(cd);
        w.Write(0x07064b50u); w.Write(0); w.Write(z64); w.Write(1);
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);
        w.Flush();
        return m.ToArray();
    }

    private static List<P4kEntry> SampleEntries()
    {
        byte[] cga = Encoding.ASCII.GetBytes("#ivo-model");
        byte[] mtl = Encoding.ASCII.GetBytes("<Material Name=\"x\"/>");
        return new List<P4kEntry>
        {
            new P4kEntry { Name = "Data\\Objects\\Spaceships\\Ships\\TEST\\test.cga", Data = cga, Size = cga.Length },
            new P4kEntry { Name = "Data\\Objects\\Spaceships\\Ships\\TEST\\test.mtl", Data = ZstdRaw(mtl), Method = 100, Size = mtl.Length },
            new P4kEntry { Name = "Data\\Scripts\\Entities\\Vehicles\\Implementations\\Xml\\TEST_Ship.xml", Data = new byte[] { 1, 2, 3, 4 }, Size = 4, Encrypted = true },
            new P4kEntry { Name = "Data\\Sounds\\test.wem", Data = new byte[] { 9 }, Size = 1 },
            new P4kEntry { Name = "Data\\Objects\\Spaceships\\Ships\\TEST\\textures\\a.dds.3", Data = new byte[] { 7 }, Size = 1 }
        };
    }

    [Fact]
    public void P4kReadsStoredAndZstdEntriesAndRefusesEncryptedOnes()
    {
        zstdCalls = 0;
        using StarCitizenP4k p = StarCitizenP4k.OpenStream(new MemoryStream(P4k(SampleEntries())), UnZstdRaw);
        // The sound is not a kind of file the pictures use: not kept.
        Assert.Equal(4, p.Count);
        Assert.False(p.Exists("Data\\Sounds\\test.wem"));
        Assert.Equal("#ivo-model", Encoding.ASCII.GetString(p.Read("objects/spaceships/ships/test/TEST.cga")!));
        Assert.True(p.Exists("Data/Objects/Spaceships/Ships/TEST/test.cga"));
        Assert.True(p.Exists("Objects\\Spaceships\\Ships\\TEST\\textures\\a.dds.3"));
        Assert.Equal("<Material Name=\"x\"/>", Encoding.ASCII.GetString(p.Read("Data\\Objects\\Spaceships\\Ships\\TEST\\test.mtl")!));
        Assert.Equal(1, zstdCalls);
        string enc = "Scripts\\Entities\\Vehicles\\Implementations\\Xml\\TEST_Ship.xml";
        Assert.True(p.IsEncrypted(enc));
        StarCitizenP4k.EncryptedEntryException ex = Assert.Throws<StarCitizenP4k.EncryptedEntryException>(() => p.Read(enc));
        Assert.EndsWith("TEST_Ship.xml", ex.Entry);
        Assert.Contains(p.EncryptedAsked, n => n.EndsWith("TEST_Ship.xml", StringComparison.Ordinal));
        Assert.Null(p.Read("Data\\nothing.cga"));
        Assert.Equal(3, p.List("Data\\Objects\\Spaceships\\Ships\\TEST").Count);
        Assert.Equal(4, p.SizeOf("Data\\Scripts\\Entities\\Vehicles\\Implementations\\Xml\\TEST_Ship.xml"));
    }

    [Fact]
    public void P4kIndexIsCachedPerArchiveSizeAndTime()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jp-sc-p4k-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string archive = Path.Combine(dir, "Data.p4k");
            File.WriteAllBytes(archive, P4k(SampleEntries()));
            string cache = Path.Combine(dir, "cache");
            using (StarCitizenP4k p = StarCitizenP4k.Open(archive, cache, UnZstdRaw))
                Assert.Equal(4, p.Count);
            string[] files = Directory.GetFiles(cache, "p4k-*.index");
            Assert.Single(files);
            // The second opening uses the cache (a damaged central directory does not matter then).
            using (StarCitizenP4k p = StarCitizenP4k.Open(archive, cache, UnZstdRaw))
            {
                Assert.Equal(4, p.Count);
                Assert.True(p.IsEncrypted("Data\\Scripts\\Entities\\Vehicles\\Implementations\\Xml\\TEST_Ship.xml"));
                Assert.Equal("#ivo-model", Encoding.ASCII.GetString(p.Read("Data\\Objects\\Spaceships\\Ships\\TEST\\test.cga")!));
            }
            // Another archive (other size) gets a new cache; the old one goes.
            File.WriteAllBytes(archive, P4k(SampleEntries().Take(2).ToList()));
            using (StarCitizenP4k p = StarCitizenP4k.Open(archive, cache, UnZstdRaw))
                Assert.Equal(2, p.Count);
            Assert.Single(Directory.GetFiles(cache, "p4k-*.index"));
            Assert.NotEqual(files[0], Directory.GetFiles(cache, "p4k-*.index")[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KeptKindsAndNameForms()
    {
        Assert.True(StarCitizenP4k.Keep("Data\\x\\a.dds.7"));
        Assert.True(StarCitizenP4k.Keep("Data\\x\\a.dds.7a"));
        Assert.True(StarCitizenP4k.Keep("Data\\Game2.dcb"));
        Assert.True(StarCitizenP4k.Keep("Data\\x\\a.socpak"));
        Assert.False(StarCitizenP4k.Keep("Data\\Animations\\a.caf"));
        // (names keep their case; lookups ignore it)
        Assert.Equal("Data\\objects\\a.cga", StarCitizenP4k.Normalize("objects/a.cga"));
        Assert.Equal("Data\\Objects\\a.cga", StarCitizenP4k.Normalize("Data\\Objects\\a.cga"));
        Assert.Equal("Data\\Objects\\a.cga", StarCitizenP4k.Normalize("\\Objects\\a.cga"));
    }

    [Fact]
    public void ObjectContainerZipIsRead()
    {
        using MemoryStream m = new MemoryStream();
        using (ZipArchive z = new ZipArchive(m, ZipArchiveMode.Create, true))
        {
            using (Stream s = z.CreateEntry("base\\room.soc", CompressionLevel.Optimal).Open())
                s.Write(Encoding.ASCII.GetBytes("soc soc soc soc soc"));
            using (Stream s = z.CreateEntry("base.xml", CompressionLevel.NoCompression).Open())
                s.Write(Encoding.ASCII.GetBytes("<ObjectContainer/>"));
        }
        Dictionary<string, byte[]> files = StarCitizenP4k.ReadZip(m.ToArray(), null);
        Assert.Equal("soc soc soc soc soc", Encoding.ASCII.GetString(files["base/room.soc"]));
        Assert.Equal("<ObjectContainer/>", Encoding.ASCII.GetString(files["BASE.xml"]));
        Assert.Throws<InvalidDataException>(() => StarCitizenP4k.ReadZip(new byte[40], null));
    }

    // ------------------------------------------------------------------ CryXmlB

    private sealed class XNode
    {
        public string Tag = "";
        public string Content = "";
        public List<(string, string)> Attrs = new();
        public List<XNode> Children = new();
    }

    private static byte[] CryXmlB(XNode root)
    {
        List<XNode> nodes = new();
        List<int> parents = new();
        void Collect(XNode n, int parent)
        {
            int me = nodes.Count;
            nodes.Add(n);
            parents.Add(parent);
            foreach (XNode c in n.Children)
                Collect(c, me);
        }
        Collect(root, -1);
        MemoryStream strings = new MemoryStream();
        Dictionary<string, int> offsets = new();
        int Str(string s)
        {
            if (offsets.TryGetValue(s, out int o))
                return o;
            o = (int)strings.Length;
            byte[] b = Encoding.UTF8.GetBytes(s + "\0");
            strings.Write(b);
            offsets[s] = o;
            return o;
        }
        List<(int, int)> attrs = new();
        List<int> children = new();
        int[] firstAttr = new int[nodes.Count], firstChild = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            firstAttr[i] = attrs.Count;
            foreach ((string k, string v) in nodes[i].Attrs)
                attrs.Add((Str(k), Str(v)));
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            firstChild[i] = children.Count;
            foreach (XNode c in nodes[i].Children)
                children.Add(nodes.IndexOf(c));
        }
        int nodeOff = 44, attrOff = nodeOff + 28 * nodes.Count, childOff = attrOff + 8 * attrs.Count, strOff = childOff + 4 * children.Count;
        int[] tags = nodes.Select(n => Str(n.Tag)).ToArray();
        int[] contents = nodes.Select(n => Str(n.Content)).ToArray();
        byte[] sb = strings.ToArray();
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("CryXmlB\0"));
        w.Write(strOff + sb.Length);
        w.Write(nodeOff); w.Write(nodes.Count);
        w.Write(attrOff); w.Write(attrs.Count);
        w.Write(childOff); w.Write(children.Count);
        w.Write(strOff); w.Write(sb.Length);
        for (int i = 0; i < nodes.Count; i++)
        {
            w.Write(tags[i]); w.Write(contents[i]);
            w.Write((ushort)nodes[i].Attrs.Count); w.Write((ushort)nodes[i].Children.Count);
            w.Write(parents[i]); w.Write(firstAttr[i]); w.Write(firstChild[i]); w.Write(0);
        }
        foreach ((int k, int v) in attrs)
        {
            w.Write(k); w.Write(v);
        }
        foreach (int c in children)
            w.Write(c);
        w.Write(sb);
        w.Flush();
        return m.ToArray();
    }

    private static XNode SampleMtl()
    {
        XNode Mat(string name, string shader, params (string, string)[] more)
        {
            XNode n = new XNode { Tag = "Material" };
            n.Attrs.Add(("Name", name));
            n.Attrs.Add(("Shader", shader));
            n.Attrs.AddRange(more);
            return n;
        }
        XNode illum = Mat("seat", "Illum", ("Diffuse", "0.5,0.25,1"));
        XNode tex = new XNode { Tag = "Textures" };
        XNode t1 = new XNode { Tag = "Texture" };
        t1.Attrs.Add(("Map", "TexSlot1"));
        t1.Attrs.Add(("File", "Objects/Ships/x/seat_diff.tif"));
        tex.Children.Add(t1);
        illum.Children.Add(tex);
        XNode hs = Mat("metal", "HardSurface");
        XNode layers = new XNode { Tag = "MatLayers" };
        XNode layer = new XNode { Tag = "Layer" };
        layer.Attrs.Add(("Path", "Materials/layers/paint.mtl"));
        layer.Attrs.Add(("TintColor", "0.2,0.1,0.05"));
        layers.Children.Add(layer);
        hs.Children.Add(layers);
        XNode subs = new XNode { Tag = "SubMaterials" };
        subs.Children.Add(Mat("Proxy", "NoDraw"));
        subs.Children.Add(Mat("canopy_glass", "GlassPBR"));
        subs.Children.Add(illum);
        subs.Children.Add(hs);
        subs.Children.Add(Mat("UI_Graphics", "DisplayScreen"));
        subs.Children.Add(Mat("detail_decals", "MeshDecal"));
        XNode root = new XNode { Tag = "Material" };
        root.Attrs.Add(("MtlFlags", "256"));
        root.Children.Add(subs);
        return root;
    }

    [Fact]
    public void CryXmlBinaryAndTextAreRead()
    {
        CryXmlNode n = CryXml.Parse(CryXmlB(SampleMtl()))!;
        Assert.Equal("Material", n.Tag);
        Assert.Equal("256", n.Get("mtlflags"));
        CryXmlNode subs = n.Child("SubMaterials")!;
        Assert.Equal(6, subs.Children.Count);
        Assert.Equal("seat", subs.Children[2].Get("Name"));
        Assert.Equal("Objects/Ships/x/seat_diff.tif", subs.Children[2].Child("Textures")!.Children[0].Get("File"));
        Assert.Equal(6, n.Descendants("Material").Count());
        CryXmlNode text = CryXml.Parse(Encoding.UTF8.GetBytes("﻿<Material Name=\"a\"><SubMaterials><Material Name=\"b\"/></SubMaterials></Material>"))!;
        Assert.Equal("b", text.Child("SubMaterials")!.Children[0].Get("Name"));
        Assert.Equal(new[] { 1.0, 0.5, -2 }, CryXml.Numbers("1, 0.5,-2"));
    }

    [Fact]
    public void MaterialsAreDrawnSkippedOrScreens()
    {
        List<StarCitizenMaterial> mats = StarCitizenMaterial.Parse(CryXml.Parse(CryXmlB(SampleMtl())));
        Assert.Equal(new[] { "skip", "skip", "draw", "draw", "screen", "skip" }, mats.Select(m => m.Kind).ToArray());
        Assert.Equal("Objects/Ships/x/seat_diff.tif", mats[2].Texture);
        Assert.Equal(0.25, mats[2].Diffuse[1], 6);
        Assert.Equal("Materials/layers/paint.mtl", mats[3].LayerPath);
        Assert.Equal(0.1, mats[3].LayerTint![1], 6);
        Assert.Equal(186, StarCitizenMaterial.Srgb(0.5));
        Assert.Equal("Data\\Objects\\Ships\\x\\seat_diff.dds", StarCitizenTextures.DdsName("Objects/Ships/x/seat_diff.tif"));
    }

    [Fact]
    public void SocEntitiesAreFoundInTheChunkFile()
    {
        XNode ents = new XNode { Tag = "Entities" };
        XNode e = new XNode { Tag = "Entity" };
        e.Attrs.Add(("Name", "Int-001"));
        e.Attrs.Add(("Pos", "1,2,3"));
        e.Attrs.Add(("EntityClass", "GeomEntity"));
        XNode g = new XNode { Tag = "Geometry" };
        g.Attrs.Add(("path", "objects/spaceships/ships/test/test_int.cga"));
        e.Children.Add(g);
        ents.Children.Add(e);
        byte[] xml = CryXmlB(ents);
        byte[] soc = Encoding.ASCII.GetBytes("CrCh").Concat(new byte[60]).Concat(xml).Concat(new byte[8]).ToArray();
        List<CryXmlNode> list = StarCitizenCockpitPictures.SocEntities(soc);
        Assert.Single(list);
        Assert.Equal("objects/spaceships/ships/test/test_int.cga", list[0].Children[0].Descendants("Geometry").First().Get("path"));
    }

    // A .soc with a brush chunk (0x0010 version 1): two models, one material, one tint palette, two brushes.
    [Fact]
    public void SocBrushesArePlacedFromTheBrushChunk()
    {
        byte[] Name(string s)
        {
            byte[] b = new byte[256];
            Encoding.UTF8.GetBytes(s).CopyTo(b, 0);
            return b;
        }
        byte[] Brush(int model, int material, double[] rows, double x0, double x1)
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w = new BinaryWriter(ms);
            w.Write(1);
            foreach (double v in new[] { x0, 0.0, 0.0, x1, 1.0, 1.0 })
                w.Write(v);
            w.Write(0);
            w.Write(0x20b83008u);
            w.Write((ushort)model);
            w.Write((ushort)0x6464);
            foreach (double v in rows)
                w.Write(v);
            w.Write(new byte[8]);
            w.Write((ushort)8);
            w.Write((ushort)material);
            w.Write((ushort)0);
            w.Write((byte)1);
            w.Write((byte)0xff);
            w.Write(new byte[32]);
            return ms.ToArray();
        }
        double[] turned = { 0, -1, 0, 1.5, 1, 0, 0, 2.5, 0, 0, 1, -0.5 };
        double[] plain = { 1, 0, 0, 0, 0, 1, 0, 4, 0, 0, 1, 0 };
        byte[] records = Brush(1, 0, turned, -1, 1).Concat(Brush(0, 0xffff, plain, 0, 2)).ToArray();
        Assert.Equal(StarCitizenCockpitPictures.SocBrushRecord, records.Length / 2);
        byte[] chunk;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(0);
            w.Write(2);
            w.Write(Name("objects/ships/test/wall.cgf"));
            w.Write(Name("objects/ships/test/console.cga"));
            w.Write((ushort)1);
            w.Write((ushort)1);
            w.Write(Name("objects/ships/test/test_int"));
            w.Write(Name("Libs/Foundry/Records/TintPalettes/default"));
            w.Write(15);
            w.Write(new byte[24]);
            w.Write(records.Length);
            w.Write(records);
            chunk = ms.ToArray();
        }
        byte[] soc;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(Encoding.ASCII.GetBytes("CrCh"));
            w.Write(0x746);
            w.Write(1);
            w.Write(16);
            w.Write((ushort)0x0010);
            w.Write((ushort)1);
            w.Write(1);
            w.Write(chunk.Length);
            w.Write(32);
            w.Write(chunk);
            soc = ms.ToArray();
        }
        List<StarCitizenCockpitPictures.SocBrush> list = StarCitizenCockpitPictures.SocBrushes(soc);
        Assert.Equal(2, list.Count);
        Assert.Equal("objects/ships/test/console.cga", list[0].Model);
        Assert.Equal("objects/ships/test/test_int", list[0].Material);
        // (rows: x' = -y + 1.5, y' = x + 2.5, z' = z - 0.5)
        Vec3 p = Mat4.Point(list[0].Matrix, 1, 0, 0);
        Assert.Equal(1.5, p.X, 6);
        Assert.Equal(3.5, p.Y, 6);
        Assert.Equal(-0.5, p.Z, 6);
        Assert.Equal(Math.Sqrt(4 + 1 + 1), list[0].Size, 6);
        Assert.Equal("objects/ships/test/wall.cgf", list[1].Model);
        Assert.Equal(string.Empty, list[1].Material);
        Assert.Equal(4, Mat4.Point(list[1].Matrix, 0, 0, 0).Y, 6);
    }

    // ------------------------------------------------------------------ DataForge

    // A version 8 database: struct Ship { string Name; int32 Crew; Child Box (inline); string[] Tags } and Child
    // { float X }, one record of Ship.
    private static byte[] DataForgeFile()
    {
        MemoryStream text = new MemoryStream(), blob = new MemoryStream();
        int T(MemoryStream s, string v) { int o = (int)s.Length; s.Write(Encoding.UTF8.GetBytes(v + "\0")); return o; }
        int nShip = T(blob, "Ship"), nChild = T(blob, "Child"), pName = T(blob, "Name"), pCrew = T(blob, "Crew"), pBox = T(blob, "Box"), pTags = T(blob, "Tags"), pX = T(blob, "X");
        int rName = T(blob, "EntityClassDefinition.TEST_Ship");
        int rFile = T(text, "libs/foundry/records/entities/spaceships/test_ship.xml");
        int sName = T(text, "Test Ship"), sTag1 = T(text, "fighter"), sTag2 = T(text, "small");
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        w.Write(0); w.Write(8); w.Write(0L);
        // counts: structs, props, enums, mappings, records, bool, i8, i16, i32, i64, u8, u16, u32, u64, f32, f64, guid,
        // string, locale, enum, strong, weak, reference, enum options, text, blob
        int[] counts = { 2, 5, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, (int)text.Length, (int)blob.Length };
        foreach (int c in counts)
            w.Write(c);
        // structs: name, parent, property count, first property, node type
        w.Write(nShip); w.Write(-1); w.Write((ushort)4); w.Write((ushort)0); w.Write(0);
        w.Write(nChild); w.Write(-1); w.Write((ushort)1); w.Write((ushort)4); w.Write(0);
        // properties: name, struct, type, conversion, padding
        void Prop(int name, int st, int type, int conv) { w.Write(name); w.Write((ushort)st); w.Write((ushort)type); w.Write((ushort)conv); w.Write((ushort)0); }
        Prop(pName, 0, 10, 0);
        Prop(pCrew, 0, 4, 0);
        Prop(pBox, 1, 0x10, 0);
        Prop(pTags, 0, 10, 1);
        Prop(pX, 0, 11, 0);
        // data mappings: count, struct
        w.Write(1); w.Write(0);
        w.Write(1); w.Write(1);
        // record (36 bytes): name, file, tag, struct, guid, instance, size
        w.Write(rName); w.Write(rFile); w.Write(0); w.Write(0);
        w.Write(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        w.Write((ushort)0); w.Write((ushort)24);
        // string values array
        w.Write(sTag1); w.Write(sTag2);
        w.Write(text.ToArray());
        w.Write(blob.ToArray());
        // Ship instance: Name, Crew, Box (X), Tags (count, first)
        w.Write(sName); w.Write(3); w.Write(2.5f); w.Write(2); w.Write(0);
        // Child instance
        w.Write(7.5f);
        w.Flush();
        return m.ToArray();
    }

    [Fact]
    public void DataForgeRecordsAreRead()
    {
        DataForge df = DataForge.Parse(DataForgeFile());
        Assert.Equal(8, df.Version);
        Assert.Equal(1, df.RecordCount);
        int r = df.Find("EntityClassDefinition.TEST_Ship");
        Assert.Equal(0, r);
        Assert.Equal(-1, df.Find("nothing"));
        Assert.Equal(new[] { 0 }, df.RecordsUnder("libs/foundry/records/entities/spaceships/").ToArray());
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", df.RecordGuid(0));
        Dictionary<string, object> o = df.Read(r, 4);
        Assert.Equal("Ship", DataForge.TypeOf(o));
        Assert.Equal("Test Ship", DataForge.Str(o, "Name"));
        Assert.Equal(3, DataForge.Num(o, "Crew"));
        Assert.Equal(2.5, DataForge.Num(DataForge.Obj(o, "Box"), "X"));
        Assert.Equal(new object[] { "fighter", "small" }, DataForge.List(o, "Tags").ToArray());
        // A file whose data does not end at its end is refused.
        byte[] bad = DataForgeFile().Concat(new byte[4]).ToArray();
        Assert.Throws<InvalidDataException>(() => DataForge.Parse(bad));
    }

    [Fact]
    public void TextsComeFromGlobalIni()
    {
        StarCitizenTexts t = StarCitizenTexts.Parse(Encoding.UTF8.GetBytes("﻿vehicle_NameTEST=Test Ship\r\nui_flight_ready,P=Flight Ready\r\nbad=<= UNINITIALIZED =>\n"));
        Assert.Equal("Test Ship", t.Text("@vehicle_NameTEST"));
        Assert.Equal("Test Ship", t.Text("LOC:@vehicle_nametest"));
        Assert.Equal("Flight Ready", t.Text("@ui_flight_ready"));
        Assert.Equal("", t.Text("@bad"));
        Assert.Equal("", t.Text("@missing"));
    }

    // ------------------------------------------------------------------ #ivo models

    private static void Ivo(BinaryWriter w, List<(uint Type, byte[] Body)> chunks)
    {
        w.Write(Encoding.ASCII.GetBytes("#ivo"));
        w.Write(0x900u);
        w.Write(chunks.Count);
        w.Write(16);
        long at = 16 + 16 * chunks.Count;
        foreach ((uint type, byte[] body) in chunks)
        {
            w.Write(type); w.Write(0x900u); w.Write(at);
            at += body.Length;
        }
        foreach ((uint _, byte[] body) in chunks)
            w.Write(body);
    }

    private static byte[] Body(Action<BinaryWriter> write)
    {
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        write(w);
        w.Flush();
        return m.ToArray();
    }

    // A model with two nodes (the root and "Control_Joystick" at (0, 1, 0)) and two mesh objects: object 1 placed at
    // (0, 1, 0); a mesh of one triangle per object.
    private static (byte[] Model, byte[] Mesh) SampleModel(int extraVertices = 0)
    {
        byte[] bones = Body(w =>
        {
            byte[] names = Encoding.ASCII.GetBytes("Root\0Control_Joystick\0");
            w.Write(2); w.Write(names.Length); w.Write(new byte[40]);
            // hash, first child, child count, parent, -, -, mesh object
            w.Write(0u); w.Write((ushort)1); w.Write((ushort)1); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0);
            w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)1);
            w.Write(names);
            for (int pass = 0; pass < 2; pass++)
            {
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
                w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0f); w.Write(1f); w.Write(0f);
            }
        });
        byte[] nodeMesh = Body(w =>
        {
            w.Write(32); w.Write(2); w.Write(2); w.Write(1); w.Write(2); w.Write(0); w.Write(2); w.Write(0);
            w.Write(new byte[32]);
            for (int i = 0; i < 2; i++)
            {
                float ty = i;
                foreach (float f in new[] { 1f, 0, 0, 0, 0, 1, 0, ty, 0, 0, 1, 0 })
                    w.Write(f);
                w.Write(new byte[208 - 48]);
            }
        });
        byte[] material = Body(w => { w.Write(Encoding.ASCII.GetBytes("Objects/Test/test_mat\0")); w.Write(new byte[10]); });
        int nv = 6 + extraVertices;
        byte[] mesh = Body(w =>
        {
            w.Write(0u);                                            // flags
            w.Write(4); w.Write(nv); w.Write(6); w.Write(2); w.Write(0);
            foreach (float f in new[] { -1f, -1, -1, 1, 2, 1 }) w.Write(f);            // placed box
            foreach (float f in new[] { -0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f }) w.Write(f);   // position box
            w.Write(new byte[168 - 72]);
            // sub-meshes: material, object, first index, index count, first vertex, -, vertex count, ...
            int fv2 = 3 + extraVertices;
            w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(3); w.Write(0); w.Write(0); w.Write(3); w.Write(new byte[24]);
            w.Write((ushort)1); w.Write((ushort)1); w.Write(3); w.Write(3); w.Write(fv2); w.Write(0); w.Write(3); w.Write(new byte[24]);
            // triangles (16-bit); the second counts from its base once the mesh is beyond 16 bits
            w.Write(IvoModel.IndexStream); w.Write(2);
            int b = extraVertices > 0 ? 0 : fv2;
            foreach (int i in new[] { 0, 1, 2, b, b + 1, b + 2 }) w.Write((ushort)i);
            w.Write(new byte[4]);                                   // pad to 8
            w.Write(IvoModel.VertexStream); w.Write(16);
            for (int v = 0; v < nv; v++)
            {
                short x = (short)(v % 3 == 1 ? 32767 : 0), y = (short)(v % 3 == 2 ? 32767 : 0);
                w.Write(x); w.Write(y); w.Write((short)-32767); w.Write((short)32767);
                w.Write(0xFFFFFFFFu);
                w.Write((ushort)0x3800); w.Write((ushort)0x3C00);  // u 0.5, v 1
            }
        });
        byte[] model = Body(w => Ivo(w, new List<(uint, byte[])> { (IvoModel.BonesChunk, bones), (IvoModel.MaterialChunk, material), (IvoModel.NodeMeshChunk, nodeMesh) }));
        byte[] meshFile = Body(w => Ivo(w, new List<(uint, byte[])> { (IvoModel.MeshChunk, mesh) }));
        return (model, meshFile);
    }

    [Fact]
    public void IvoModelGivesNodesMeshAndPlacement()
    {
        (byte[] model, byte[] mesh) = SampleModel();
        IvoModel m = IvoModel.Read(model, mesh);
        Assert.Equal("", m.Problem);
        Assert.Equal("Objects/Test/test_mat", m.Material);
        Assert.Equal(2, m.Nodes.Count);
        Assert.Equal("Control_Joystick", m.Nodes[1].Name);
        Assert.Equal(0, m.Nodes[1].Parent);
        Assert.Equal(1, m.Nodes[1].ObjectIndex);
        Assert.Equal(1.0, Mat4.Point(m.Nodes[1].World, 0, 0, 0).Y, 6);
        Assert.Equal(6, m.VertexCount);
        // 16-bit positions in the position box (half size below 1: scale 1): x = 32767/32767 * 1 + 0
        Assert.Equal(1.0, m.Positions![3], 4);
        Assert.Equal(-1.0, m.Positions[2], 4);
        Assert.Equal(0.5f, m.Uvs![0]);
        Assert.Equal(1f, m.Uvs[1]);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, m.Indices);
        Assert.Equal(2, m.Subsets.Count);
        Assert.Equal(1, m.Subsets[1].Object);
        Assert.Equal(1.0, Mat4.Point(m.ObjectMatrix(1), 0, 0, 0).Y, 6);
        Assert.Same(m.Nodes[1], m.NodeOfObject(1));
        Assert.True(m.IsUnder(m.Nodes[1], m.Nodes[0]));
        Assert.Single(StarCitizenCockpitPictures.GripNodes(m));
        Assert.Equal("not an #ivo model", IvoModel.Read(new byte[40], null).Problem);
    }

    [Fact]
    public void LargeMeshTrianglesCountFromTheirBase()
    {
        (byte[] model, byte[] mesh) = SampleModel(70000);
        IvoModel m = IvoModel.Read(model, mesh);
        Assert.Equal("", m.Problem);
        Assert.Equal(new[] { 0, 1, 2, 70003, 70004, 70005 }, m.Indices);
    }

    [Fact]
    public void SmoothNormalsAreUnitLength()
    {
        float[] p = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        float[] n = StarCitizenCockpitPictures.SmoothNormals(p, new[] { 0, 1, 2 });
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, n);
        Vec3 s = StarCitizenCockpitPictures.ToScene(new Vec3(1, 2, 3));
        Assert.Equal(new Vec3(2, 3, 1), s);
    }

    // ------------------------------------------------------------------ split DDS

    private static byte[] DdsHead(int w, int h, int mips, byte[] tail)
    {
        byte[] d = new byte[128 + tail.Length];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(d, 0);
        BitConverter.GetBytes(124).CopyTo(d, 4);
        BitConverter.GetBytes(h).CopyTo(d, 12);
        BitConverter.GetBytes(w).CopyTo(d, 16);
        BitConverter.GetBytes(mips).CopyTo(d, 28);
        BitConverter.GetBytes(32).CopyTo(d, 76);
        BitConverter.GetBytes(4).CopyTo(d, 80);
        Encoding.ASCII.GetBytes("DXT1").CopyTo(d, 84);
        tail.CopyTo(d, 128);
        return d;
    }

    [Fact]
    public void SplitDdsIsPutTogetherFromTheWantedLevel()
    {
        // 16x16 DXT1, 5 levels: 16 (128 bytes) in .dds.2, 8 (32) in .dds.1, 4, 2, 1 (8 each) in the head file.
        byte[] tail = Enumerable.Repeat((byte)3, 24).ToArray();
        byte[] head = DdsHead(16, 16, 5, tail);
        Dictionary<int, byte[]> mips = new() { [2] = Enumerable.Repeat((byte)1, 128).ToArray(), [1] = Enumerable.Repeat((byte)2, 32).ToArray() };
        byte[] full = StarCitizenTextures.Assemble(head, k => mips[k], k => mips.ContainsKey(k), 2048)!;
        Assert.Equal(128 + 128 + 32 + 24, full.Length);
        Assert.Equal(16, BitConverter.ToInt32(full, 16));
        Assert.Equal(1, full[128]);
        byte[] half = StarCitizenTextures.Assemble(head, k => mips[k], k => mips.ContainsKey(k), 8)!;
        Assert.Equal(128 + 32 + 24, half.Length);
        Assert.Equal(8, BitConverter.ToInt32(half, 16));
        Assert.Equal(8, BitConverter.ToInt32(half, 12));
        Assert.Equal(4, BitConverter.ToInt32(half, 28));
        Assert.Equal(2, half[128]);
        TextureImage img = TextureImage.Decode(half, "x.dds", 8)!;
        Assert.Equal(8, img.Width);
        // No split files: the head file is the texture.
        Assert.Same(head, StarCitizenTextures.Assemble(head, k => null!, k => false, 8));
    }

    [Fact]
    public void GripsListTheGamesFlightMovementActions()
    {
        string xml = "<ActionMaps><actionmap name=\"spaceship_general\"><action name=\"v_eject\"/></actionmap>" +
                     "<actionmap name=\"spaceship_movement\" UILabel=\"@ui_CGSpaceFlightMovement\">" +
                     "<action name=\"v_pitch\" joystick=\"y\" UILabel=\"@ui_CIPitch\"/><action name=\"v_pitch_mouse\" mouse=\"maxis_y\" UILabel=\"@ui_CIPitch\"/>" +
                     "<action name=\"v_strafe_up\" keyboard=\"space\"/></actionmap></ActionMaps>";
        StarCitizenTexts t = StarCitizenTexts.Parse(Encoding.UTF8.GetBytes("ui_CIPitch=Pitch\nui_CGSpaceFlightMovement=Flight - Movement\n"));
        List<CalloutCommand> a = StarCitizenCockpitPictures.FlightActions(CryXml.Parse(Encoding.UTF8.GetBytes(xml)), t);
        Assert.Equal(new[] { "v_pitch", "v_strafe_up" }, a.Select(x => x.Id).ToArray());
        Assert.Equal("Pitch", a[0].Name);
        Assert.Equal("Strafe up", a[1].Name);
        Assert.Equal("Flight - Movement", a[0].Category);
    }

    [Fact]
    public void EyeStaysBehindAndAboveTheStick()
    {
        CockpitGrip stick = new CockpitGrip { Device = "stick", Pivot = new Vec3(1.1, 0.14, 0.3) };
        Vec3 h = StarCitizenCockpitPictures.EyeBehindGrips(new Vec3(0.9, 0.2, 0), new List<CockpitGrip> { stick });
        Assert.Equal(1.1 - 0.3, h.X, 6);
        Assert.Equal(0.14 + 0.28, h.Y, 6);
        h = StarCitizenCockpitPictures.EyeBehindGrips(new Vec3(-1, 1.5, 0), new List<CockpitGrip> { stick });
        // (a stick beside the pilot, on the seat's arm rest: the eye right behind it)
        Assert.Equal(1.1 - 0.4, h.X, 6);
        CockpitGrip centre = new CockpitGrip { Device = "stick", Pivot = new Vec3(1.1, 0.14, 0) };
        Assert.Equal(1.1 - 0.65, StarCitizenCockpitPictures.EyeBehindGrips(new Vec3(-1, 1.5, 0), new List<CockpitGrip> { centre }).X, 6);
        Assert.Equal(0.14 + 0.7, h.Y, 6);
        Assert.Equal(new Vec3(1, 2, 3), StarCitizenCockpitPictures.EyeBehindGrips(new Vec3(1, 2, 3), new List<CockpitGrip>()));
    }

    // ------------------------------------------------------------------ names

    [Fact]
    public void PartAndScreenNamesAreReadable()
    {
        Assert.Equal("Button 2", StarCitizenCockpitPictures.PartLabel("Control_Joystick_Button_02", "Control_Joystick"));
        Assert.Equal("Trigger", StarCitizenCockpitPictures.PartLabel("Control_Joystick_trigger", "Control_Joystick"));
        Assert.Equal("Button 1", StarCitizenCockpitPictures.PartLabel("throttle_button_1", "Control_Throttle"));
        Assert.Equal("Quantum Travel", StarCitizenCockpitPictures.Humanize("$IP_QuantumTravel"));
        Assert.Equal("Flight Ready", StarCitizenCockpitPictures.Humanize("IP_Flight_Ready"));
    }
}
