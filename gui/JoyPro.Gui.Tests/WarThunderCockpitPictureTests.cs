using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.CockpitPictures;
using JoyPro.WtOodle;
using Xunit;

namespace JoyPro.Gui.Tests;

// War Thunder cockpit pictures (docs/cockpits.md "Pictures from your games"): the GRP2 resource index, the DxP2/DDSx
// texture headers, the binary BLK of dynModelDesc.bin, meshoptimizer's packed index buffers, the DynModel vertex
// channels and the Oodle helper's decoder (tools/wt-oodle) on small hand-made streams.
public sealed class WarThunderCockpitPictureTests
{
    // ------------------------------------------------------------------ GRP2

    private static byte[] Grp(out long fileLength)
    {
        // header (16), three tables (16 each), name offsets, names, resource table
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("GRP2"));
        w.Write(0);
        w.Write(112);   // full size: everything after the header
        w.Write(0);
        w.Write(64); w.Write(2); w.Write(0L);          // names: offset 64, 2
        w.Write(104); w.Write(2); w.Write(0L);         // resources: offset 104, 2
        w.Write(0); w.Write(0); w.Write(0L);           // resource data (not read)
        w.Write(72); w.Write(82);                      // name offsets (file offsets)
        w.Write(Encoding.ASCII.GetBytes("x_cockpit\0"));
        w.Write(Encoding.ASCII.GetBytes("x_cockpit_skeleton\0"));
        w.Write(new byte[3]);                          // to 104
        w.Write(WarThunderGrp.DynModelClass); w.Write(200u); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(WarThunderGrp.SkeletonClass); w.Write(150u); w.Write((ushort)1); w.Write((ushort)0);
        w.Flush();
        fileLength = 300;
        return m.ToArray();
    }

    [Fact]
    public void Grp2IndexGivesNamesClassesOffsetsAndLengths()
    {
        long length;
        byte[] d = Grp(out length);
        Assert.Equal(128, d.Length);
        WarThunderGrp g = WarThunderGrp.Parse(d, length);
        Assert.Equal(2, g.Entries.Count);
        WarThunderGrp.Entry model = g.Find("x_cockpit", WarThunderGrp.DynModelClass)!;
        WarThunderGrp.Entry skel = g.Find("x_cockpit_skeleton", WarThunderGrp.SkeletonClass)!;
        Assert.Equal(200, model.Offset);
        Assert.Equal(100, model.Length);   // to the end of the file
        Assert.Equal(150, skel.Offset);
        Assert.Equal(50, skel.Length);     // to the next resource
        Assert.Null(g.Find("x_cockpit", WarThunderGrp.SkeletonClass));
        Assert.Throws<InvalidDataException>(() => WarThunderGrp.Parse(new byte[80], 80));
    }

    // ------------------------------------------------------------------ DxP2 / DDSx

    private static byte[] Ddsx(string fourcc, uint flags, int w, int h, int levels, int dxtShift, int mem, int packed)
    {
        using MemoryStream m = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(m);
        b.Write(Encoding.ASCII.GetBytes("DDSx"));
        b.Write(Encoding.ASCII.GetBytes(fourcc));
        b.Write(flags);
        b.Write((ushort)w);
        b.Write((ushort)h);
        b.Write((byte)levels);
        b.Write((byte)0);
        b.Write((ushort)1);
        b.Write((ushort)4);
        b.Write((byte)0);
        b.Write((byte)dxtShift);
        b.Write(mem);
        b.Write(packed);
        b.Flush();
        return m.ToArray();
    }

    [Fact]
    public void DdsxHeaderAndDxp2PackAreRead()
    {
        byte[] hdr = Ddsx("DXT1", DdsxHeader.Oodle | DdsxHeader.RevMipOrder | 0x11, 256, 128, 3, 3, 21504, 777);
        DdsxHeader h = DdsxHeader.Parse(hdr, 0)!;
        Assert.Equal("DXT1", h.FourCC);
        Assert.Equal(DdsxHeader.Oodle, h.Compression);
        Assert.Equal(256, h.Width);
        Assert.Equal(128, h.Height);
        Assert.Equal(3, h.Levels);
        Assert.Equal(16384, h.SurfaceSize(0));   // 64 x 32 blocks of 8 bytes
        Assert.Equal(4096, h.SurfaceSize(1));
        Assert.Equal(1024, h.SurfaceSize(2));
        Assert.Null(DdsxHeader.Parse(new byte[32], 0));

        // a pack: names at 48, the name at 56, headers at 72, records (24 bytes) at 104; offsets relative to byte 16
        using MemoryStream m = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(m);
        w.Write(Encoding.ASCII.GetBytes("DxP2")); w.Write(2); w.Write(1); w.Write(128);
        w.Write(48); w.Write(1); w.Write(0L);
        w.Write(72); w.Write(1); w.Write(0L);
        w.Write(104); w.Write(1); w.Write(0L);
        w.Write(56); w.Write(0);
        w.Write(Encoding.ASCII.GetBytes("abc_c$hq*\0"));
        w.Write(new byte[6]);
        w.Write(hdr);
        w.Write(0L); w.Write(-1); w.Write(4096); w.Write(777); w.Write(0);
        w.Flush();
        WarThunderDxp p = WarThunderDxp.Parse(m.ToArray());
        WarThunderDxp.Texture t = Assert.Single(p.Textures);
        Assert.Equal("abc_c$hq*", t.Name);
        Assert.Equal(4096, t.Offset);
        Assert.Equal(777, t.Size);
        Assert.Equal(256, t.Header.Width);

        int q;
        Assert.Equal("abc_c", WarThunderDxp.BaseName("abc_c$hq*", out q));
        Assert.Equal(2, q);
        Assert.Equal("mig_21_f13_cockpit_c", WarThunderDxp.BaseName("mig_21_f13_cockpit_c*?na16", out q));
        Assert.Equal(1, q);
        Assert.Equal("x_n", WarThunderDxp.BaseName("x_n$tq*", out q));
        Assert.Equal(0, q);
    }

    [Fact]
    public void TextureLevelZeroIsTakenFromTheEndWhenMipsAreStoredSmallestFirst()
    {
        DdsxHeader h = DdsxHeader.Parse(Ddsx("DXT1", DdsxHeader.RevMipOrder, 256, 128, 3, 3, 21504, 0), 0)!;
        byte[] raw = new byte[1024 + 4096 + 16384];
        // level 2 and 1 black, level 0 solid red blocks (both colours 0xF800, all indices 0)
        for (int o = 1024 + 4096; o < raw.Length; o += 8)
        {
            raw[o] = 0x00;
            raw[o + 1] = 0xF8;
            raw[o + 2] = 0x00;
            raw[o + 3] = 0xF8;
        }
        TextureImage img = WarThunderCockpitPictures.ToImage(h, raw, 512)!;
        Assert.Equal(256, img.Width);
        Assert.Equal(128, img.Height);
        Assert.True(img.Rgba[0] > 240 && img.Rgba[1] < 10 && img.Rgba[2] < 10);
        // BC7 goes into a DX10 header; an unknown D3D format gives no image
        DdsxHeader bc7 = DdsxHeader.Parse(Ddsx("BC7 ", 0, 4, 4, 1, 4, 16, 0), 0)!;
        byte[] dds = WarThunderCockpitPictures.Dds(bc7, new byte[16], 0, 16)!;
        Assert.Equal("DX10", Encoding.ASCII.GetString(dds, 84, 4));
        Assert.Equal(98, BitConverter.ToInt32(dds, 128));
        DdsxHeader odd = DdsxHeader.Parse(Ddsx("q\0\0\0", 0, 4, 4, 1, 0, 128, 0), 0)!;
        Assert.Null(WarThunderCockpitPictures.Dds(odd, new byte[128], 0, 128));
    }

    // ------------------------------------------------------------------ binary BLK

    private static void Var(List<byte> o, uint v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            o.Add(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
    }

    [Fact]
    public void BinaryBlkGivesBlocksAndParams()
    {
        string[] names = { "mig", "tex", "cls", "t0", "matR", "mat" };
        byte[] complex = Encoding.ASCII.GetBytes("a_c*\0b_n*\0dyn\0");
        List<byte> d = new List<byte> { 1 };
        Var(d, (uint)names.Length);
        byte[] pool = Encoding.ASCII.GetBytes(string.Join("\0", names) + "\0");
        Var(d, (uint)pool.Length);
        d.AddRange(pool);
        Var(d, 5);  // blocks
        Var(d, 4);  // params
        Var(d, (uint)complex.Length);
        d.AddRange(complex);
        void Param(int name, int type, uint v)
        {
            d.AddRange(BitConverter.GetBytes((uint)name | ((uint)type << 24)));
            d.AddRange(BitConverter.GetBytes(v));
        }
        Param(1, 1, 0);   // tex = "a_c*"
        Param(1, 1, 5);   // tex = "b_n*"
        Param(2, 1, 10);  // cls = "dyn"
        Param(3, 2, 1);   // t0 = 1
        void Block(uint nameIdPlus1, uint pc, uint bc, uint first)
        {
            Var(d, nameIdPlus1);
            Var(d, pc);
            Var(d, bc);
            if (bc > 0)
                Var(d, first);
        }
        Block(0, 0, 1, 1);  // root
        Block(1, 0, 2, 2);  // mig
        Block(2, 2, 0, 0);  // tex
        Block(5, 0, 1, 4);  // matR
        Block(6, 2, 0, 0);  // mat
        WarThunderBlk root = WarThunderBlk.Parse(d.ToArray(), 0);
        WarThunderBlk mig = Assert.Single(root.Blocks);
        Assert.Equal("mig", mig.Name);
        Assert.Equal(new[] { "a_c*", "b_n*" }, mig.Block("tex")!.Params.Select(p => (string)p.Value).ToArray());
        WarThunderBlk mat = Assert.Single(mig.Block("matR")!.Blocks);
        Assert.Equal("dyn", mat.GetString("cls"));
        Assert.Equal(1, mat.GetInt("t0", -1));
        Assert.Equal(-1, mat.GetInt("t1", -1));
        Assert.Throws<InvalidDataException>(() => WarThunderBlk.Parse(new byte[] { 2, 0 }, 0));
    }

    // ------------------------------------------------------------------ packed index buffers

    private static byte[] EncodeSequence(int[] indices)
    {
        // meshopt_encodeIndexSequence with one baseline: zigzag delta to the last index, low bit = baseline 0
        List<byte> o = new List<byte> { 0xD1 };
        uint last = 0;
        foreach (int i in indices)
        {
            int d = (int)((uint)i - last);
            uint v = (uint)((d << 1) ^ (d >> 31));
            v <<= 1;
            do
            {
                byte b = (byte)(v & 127);
                v >>= 7;
                o.Add(v != 0 ? (byte)(b | 128) : b);
            } while (v != 0);
            last = (uint)i;
        }
        o.AddRange(new byte[4]);
        return o.ToArray();
    }

    [Fact]
    public void MeshoptIndexSequenceDecodes()
    {
        int[] indices = { 0, 1, 2, 2, 1, 3, 300, 299, 70000 };
        byte[] packed = EncodeSequence(indices);
        Assert.Equal(indices, MeshoptIndexSequence.Decode(packed, 0, packed.Length, indices.Length));
        Assert.Throws<InvalidDataException>(() => MeshoptIndexSequence.Decode(packed, 0, packed.Length, indices.Length + 3));
        byte[] bad = (byte[])packed.Clone();
        bad[0] = 0xA1;
        Assert.Throws<InvalidDataException>(() => MeshoptIndexSequence.Decode(bad, 0, bad.Length, indices.Length));
    }

    // ------------------------------------------------------------------ vertex channels

    [Fact]
    public void VertexChannelsDecodeAndGoIntoTheSceneFrame()
    {
        float[] o = new float[4];
        byte[] vb = new byte[16];
        BitConverter.GetBytes((short)32767).CopyTo(vb, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(vb, 2);
        BitConverter.GetBytes((short)0).CopyTo(vb, 4);
        BitConverter.GetBytes((short)32767).CopyTo(vb, 6);
        vb[8] = 128; vb[9] = 255; vb[10] = 128; vb[11] = 0;   // E3DCOLOR b, g, r, a: normal (0, 1, 0)
        BitConverter.GetBytes((short)2048).CopyTo(vb, 12);   // texture coordinates in 1/4096
        BitConverter.GetBytes((short)4096).CopyTo(vb, 14);
        WarThunderDynModel.ReadChannel(vb, 0, 10, 0, o);
        Assert.Equal(1f, o[0], 3);
        Assert.Equal(-0.5f, o[1], 3);
        WarThunderDynModel.ReadChannel(vb, 12, 6, 3, o);
        Assert.Equal(0.5f, o[0], 4);
        Assert.Equal(1f, o[1], 4);
        WarThunderDynModel.ReadChannel(new byte[] { 0x00, 0x3C, 0x00, 0xC0 }, 0, 15, 3, o);   // HALF2 (1, -2)
        Assert.Equal(1f, o[0]);
        Assert.Equal(-2f, o[1]);
        Assert.Equal(16, WarThunderDynModel.TypeSize(10) + WarThunderDynModel.TypeSize(4) + WarThunderDynModel.TypeSize(6));

        // A triangle: SHORT4N positions unpacked with the model's scale and offset, E3DCOLOR normals, SHORT2 uvs;
        // DagorEngine's z (left) becomes the scene's -z (right).
        WarThunderDynModel dm = new WarThunderDynModel { BoundPack = true, PosScale = new[] { 2f, 2f, 2f }, PosOffset = new[] { 1f, 0f, 0f } };
        byte[] three = new byte[48];
        for (int i = 0; i < 3; i++)
            Array.Copy(vb, 0, three, 16 * i, 16);
        BitConverter.GetBytes((short)16384).CopyTo(three, 16 + 4);   // vertex 1: z = 0.5 * 2
        WarThunderDynModel.VertexData vd = new WarThunderDynModel.VertexData { Count = 3, Stride = 16, Vertices = three, Indices = new[] { 0, 1, 2 } };
        vd.Channels.Add(new WarThunderDynModel.Channel { Type = 10, Usage = 0 });
        vd.Channels.Add(new WarThunderDynModel.Channel { Type = 4, Usage = 1 });
        vd.Channels.Add(new WarThunderDynModel.Channel { Type = 6, Usage = 3 });
        dm.Vdata.Add(vd);
        SceneMesh mesh = WarThunderCockpitPictures.Mesh(dm, new WarThunderDynModel.Element { VertexData = 0, StartIndex = 0, Faces = 1 }, Mat4.Identity(), Mat4.Identity(), 0, 1, "n")!;
        Assert.Equal(3, mesh.Positions[0], 3);     // 1 * 2 + 1
        Assert.Equal(-1, mesh.Positions[1], 3);    // -0.5 * 2
        Assert.Equal(-1, mesh.Positions[3 + 2], 3);  // z 1 -> -1
        Assert.Equal(1, mesh.Normals![1], 2);
        Assert.Equal(0.5f, mesh.Uvs![0], 4);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.Null(WarThunderCockpitPictures.Mesh(dm, new WarThunderDynModel.Element { VertexData = 0, StartIndex = 0, Faces = 2 }, Mat4.Identity(), Mat4.Identity(), 0, 1, "n"));
    }

    // ------------------------------------------------------------------ names and callouts

    [Fact]
    public void ModelNamesAreShortenedOnlyForTheShortestModel()
    {
        Assert.Equal(new[] { "f_16a_block_10_iaf", "f-16a-block-10-iaf", "f_16a_block_10", "f-16a-block-10", "f_16a_block", "f-16a-block", "f_16a", "f-16a" },
            WarThunderCockpitPictures.Names("f_16a_block_10_iaf").ToArray());
        Assert.Equal(new[] { "spitfire_mk1", "spitfire-mk1" }, WarThunderCockpitPictures.Names("spitfire_mk1").ToArray());
        Dictionary<string, List<string>> own = WarThunderCockpitPictures.OwnNames(new[] { "p_51d_20_na_israel", "p_51d_5", "p_51d_20_na" });
        Assert.Contains("p_51d", own["p_51d_5"]);
        Assert.DoesNotContain("p_51d", own["p_51d_20_na"]);
        Assert.Contains("p_51d_20", own["p_51d_20_na"]);
        Assert.DoesNotContain("p_51d_20_na", own["p_51d_20_na_israel"]);   // that is another model's own name
        Assert.Contains("p_51d_20_na_israel", own["p_51d_20_na_israel"]);
    }

    [Fact]
    public void ModelNamesFindTheCatalogsWarThunderVariants()
    {
        // Cockpit model names as the game's packs have them; index.json gets each model's own names.
        string[] keys =
        {
            "mig_21_bis", "mig_21_bis_lazur", "mig_21_pfm", "mig_21_f13", "f_16a_block_10", "f_16a_block_10_iaf", "f_16a_block_15_adf",
            "f_16a_block_15_adf_italy", "bf_109f_4", "bf_109f_4_trop", "p_51d_5", "p_51d_20_na", "p_51d_20_na_israel", "ah_64a", "ah_64d_usa"
        };
        Dictionary<string, List<string>> own = WarThunderCockpitPictures.OwnNames(keys);
        Dictionary<string, string> index = JoyPro.DeviceReference.CockpitPictureSet.IndexOf(own.SelectMany(kv => kv.Value.Select(n => new KeyValuePair<string, string>(n, kv.Key))));
        JoyPro.DeviceReference.CockpitCatalog catalog = JoyPro.DeviceReference.CockpitCatalog.Load(CockpitDatabaseTests.ShippedCockpits);
        Dictionary<string, JoyPro.DeviceReference.CockpitVariant> variants = catalog.Aircraft.SelectMany(a => a.Variants).Select(catalog.Resolve)
            .Where(v => v.Games.Any(g => g.Game == "WarThunder")).ToDictionary(v => v.Id);
        Assert.Contains("WarThunder", JoyPro.DeviceReference.CockpitPictureSet.Games);
        string? SetOf(string id) => JoyPro.DeviceReference.CockpitPictureSet.Locate(variants[id], g => g == "WarThunder" ? index : new Dictionary<string, string>(), (g, f) => true)?.Item2;
        Assert.Equal("mig_21_bis", SetOf("mig-21bis"));
        Assert.Equal("mig_21_pfm", SetOf("mig-21pfm"));
        Assert.Equal("bf_109f_4", SetOf("bf-109f-4"));
        Assert.Equal("p_51d_5", SetOf("p-51d"));
        Assert.Equal("ah_64a", SetOf("ah-64a"));
    }

    [Fact]
    public void ControlAndInstrumentNodesGetLabels()
    {
        Assert.Equal("Landing gear lever", WarThunderCockpitPictures.Label("gears")!.Label);
        Assert.Equal("lever", WarThunderCockpitPictures.Label("flaps")!.Kind);
        Assert.Equal("Altimeter (altitude_hour)", WarThunderCockpitPictures.Label("altitude_hour")!.Label);
        Assert.Equal("Engine temperature gauge (water_temperature)", WarThunderCockpitPictures.Label("water_temperature")!.Label);
        Assert.Null(WarThunderCockpitPictures.Label("tumb12"));
        Assert.Null(WarThunderCockpitPictures.Label("gear_lamp"));
        Assert.Null(WarThunderCockpitPictures.Label("flaps_l"));
    }

    // ------------------------------------------------------------------ the Oodle helper's decoder

    [Fact]
    public void OodleMemsetQuantum()
    {
        // block header (Leviathan), quantum header "memset" (0x7FFFF), the byte
        byte[] src = { 0x8C, 0x0C, 0x07, 0xFF, 0xFF, 0x41 };
        Assert.Equal(Encoding.ASCII.GetBytes("AAAAAAAAAA"), OodleLz.Decompress(src, 0, src.Length, 10));
    }

    [Fact]
    public void OodleStoredQuantum()
    {
        // a quantum as long as its output is stored
        byte[] src = { 0x8C, 0x0C, 0x00, 0x00, 0x04, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), OodleLz.Decompress(src, 0, src.Length, 5));
    }

    [Fact]
    public void OodleEntropyOnlyChunkWithRle()
    {
        // quantum of 11 bytes: an entropy block (long header: RLE, 6 bytes in, 17 out) without LZ: commands from the
        // end: 0x01 sets the run byte ('Z'), 0xFD copies two bytes ("ab") and runs 15
        byte[] src = { 0x8C, 0x0C, 0x00, 0x00, 0x0A, 0x30, 0x00, 0x40, 0x00, 0x06, 0x00, (byte)'Z', (byte)'a', (byte)'b', 0xFD, 0x01 };
        Assert.Equal(Encoding.ASCII.GetBytes("ab" + new string('Z', 15)), OodleLz.Decompress(src, 0, src.Length, 17));
    }

    [Fact]
    public void OodleCorruptDataThrows()
    {
        Assert.Throws<InvalidOperationException>(() => OodleLz.Decompress(new byte[] { 0x8C, 0x0C, 0x00, 0x00, 0x30, 1, 2 }, 0, 7, 100));
        Assert.Throws<InvalidOperationException>(() => OodleLz.Decompress(new byte[] { 0x12, 0x34, 0, 0 }, 0, 4, 10));
        byte[] rle = { 0x8C, 0x0C, 0x00, 0x00, 0x0A, 0x30, 0x00, 0x40, 0x00, 0x06, 0x00, (byte)'Z', (byte)'a', (byte)'b', 0xFE, 0x01 };
        Assert.Throws<InvalidOperationException>(() => OodleLz.Decompress(rle, 0, rle.Length, 17));
    }
}
