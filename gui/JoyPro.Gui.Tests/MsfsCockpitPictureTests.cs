using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.CockpitPictures;
using Xunit;

namespace JoyPro.Gui.Tests;

// MSFS 2020 cockpit pictures from the open glTF interiors (engine/CockpitPictures/MsfsCockpitPictures.cs): a tiny
// package in MSFS's layout with Asobo's optimised vertex format (interleaved vertices, half-float texture
// coordinates, primitives sharing one index buffer with StartIndex / BaseVertexIndex).
public sealed class MsfsCockpitPictureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "jp-msfs-pictures-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception)
        {
        }
    }

    // A box of 8 corners around a centre, as 12 triangles.
    private static (float[] Pos, int[] Tris) Box(double cx, double cy, double cz, double h)
    {
        List<float> p = new List<float>();
        for (int i = 0; i < 8; i++)
        {
            p.Add((float)(cx + ((i & 1) == 0 ? -h : h)));
            p.Add((float)(cy + ((i & 2) == 0 ? -h : h)));
            p.Add((float)(cz + ((i & 4) == 0 ? -h : h)));
        }
        int[] t = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        return (p.ToArray(), t);
    }

    private static ushort HalfOf(float f)
    {
        // Enough for 0..1 texture coordinates.
        if (f == 0)
            return 0;
        int bits = BitConverter.SingleToInt32Bits(f);
        int exp = ((bits >> 23) & 0xFF) - 127 + 15;
        int mant = (bits >> 13) & 0x3FF;
        return (ushort)((exp << 10) | mant);
    }

    // The package: one aircraft with an interior glTF of two parts (a yoke and a battery switch) in one mesh whose two
    // primitives share the vertex and index buffers; plus an encrypted package (.fsarchive) that must not be read.
    private string MakePackages()
    {
        string pkg = Path.Combine(root, "Official", "Steam", "test-aircraft-trainer");
        string folder = Path.Combine(pkg, "SimObjects", "Airplanes", "Test_Trainer");
        string model = Path.Combine(folder, "model");
        Directory.CreateDirectory(model);
        File.WriteAllText(Path.Combine(folder, "aircraft.cfg"), "[FLTSIM.0]\ntitle = \"Test Trainer Asobo\"\nui_type = \"Trainer T1\"\n");
        File.WriteAllText(Path.Combine(folder, "cameras.cfg"), "[VIEWS]\neyepoint = -1.0, -1.0, 2.0 ; feet\n\n[CAMERADEFINITION.0]\nTitle = \"Pilot\"\nCategory = \"Cockpit\"\nSubCategoryItem = \"DefaultPilot\"\nInitialXyz = 0.0, 0.1, 0.0\n");
        File.WriteAllText(Path.Combine(folder, "flight_model.cfg"), "[WEIGHT_AND_BALANCE]\nreference_datum_position = 1.0, 0, 0\n");
        File.WriteAllText(Path.Combine(model, "model.cfg"), "[models]\nexterior=Trainer.xml\ninterior=Trainer_interior.xml\n");
        File.WriteAllText(Path.Combine(model, "Trainer_interior.xml"),
            "<ModelInfo><LODS><LOD minSize=\"100\" ModelFile=\"Trainer_interior_LOD00.gltf\"/></LODS><Behaviors>" +
            "<Component ID=\"ELECTRICAL\"><UseTemplate Name=\"ASOBO_ELECTRICAL_Switch_Battery_Master_Template\"><EVENT_ID>TOGGLE_MASTER_BATTERY</EVENT_ID></UseTemplate></Component>" +
            "<Component ID=\"HANDLING\"><UseTemplate Name=\"ASOBO_HANDLING_Yoke_Template\"><NODE_ID>HANDLING_Yoke_1</NODE_ID></UseTemplate></Component>" +
            "</Behaviors></ModelInfo>");
        // glTF: +Z forward, +Y up, +X left. The yoke 0.6 m ahead of the eye, the switch to its right.
        (float[] yp, int[] yt) = Box(0.25, 0.55, 0.6, 0.08);
        (float[] sp, int[] st) = Box(-0.15, 0.5, 0.75, 0.02);
        float[] pos = yp.Concat(sp).ToArray();
        int vc = pos.Length / 3;
        // Interleaved: position (12) + texcoord half2 (4) = 16 bytes.
        MemoryStream bin = new MemoryStream();
        BinaryWriter w = new BinaryWriter(bin);
        for (int v = 0; v < vc; v++)
        {
            w.Write(pos[3 * v]);
            w.Write(pos[3 * v + 1]);
            w.Write(pos[3 * v + 2]);
            w.Write(HalfOf((v % 2) * 0.5f));
            w.Write(HalfOf(0.25f));
        }
        int vertexBytes = (int)bin.Length;
        // One index buffer for both primitives; the switch's indices count from its own first vertex (BaseVertexIndex 8).
        foreach (int i in yt.Concat(st))
            w.Write((ushort)i);
        int indexBytes = (int)bin.Length - vertexBytes;
        w.Flush();
        File.WriteAllBytes(Path.Combine(model, "Trainer_interior_LOD00.bin"), bin.ToArray());
        string gltf = @"{
  ""asset"": { ""version"": ""2.0"", ""extensions"": { ""ASOBO_asset_optimized"": {} } },
  ""scenes"": [ { ""nodes"": [0] } ],
  ""nodes"": [
    { ""name"": ""Cockpit"", ""children"": [1, 2] },
    { ""name"": ""HANDLING_Yoke_1"", ""mesh"": 0 },
    { ""name"": ""ELECTRICAL_Switch_Battery_Master"", ""mesh"": 1, ""translation"": [0, 0, 0] }
  ],
  ""meshes"": [
    { ""name"": ""x0_HANDLING_Yoke_1"", ""primitives"": [ { ""attributes"": { ""POSITION"": 0, ""TEXCOORD_0"": 1 }, ""indices"": 2, ""material"": 0, ""extras"": { ""ASOBO_primitive"": { ""PrimitiveCount"": 12, ""VertexType"": ""VTX"" } } } ] },
    { ""name"": ""x0_ELECTRICAL_Switch_Battery_Master"", ""primitives"": [ { ""attributes"": { ""POSITION"": 0, ""TEXCOORD_0"": 1 }, ""indices"": 2, ""material"": 0, ""extras"": { ""ASOBO_primitive"": { ""BaseVertexIndex"": 8, ""StartIndex"": 36, ""PrimitiveCount"": 12, ""VertexType"": ""VTX"" } } } ] }
  ],
  ""materials"": [ { ""name"": ""panel"", ""pbrMetallicRoughness"": { ""baseColorFactor"": [0.5, 0.5, 0.5, 1.0] } } ],
  ""accessors"": [
    { ""bufferView"": 0, ""componentType"": 5126, ""count"": " + vc + @", ""type"": ""VEC3"" },
    { ""bufferView"": 0, ""byteOffset"": 12, ""componentType"": 5122, ""count"": " + vc + @", ""type"": ""VEC2"" },
    { ""bufferView"": 1, ""componentType"": 5123, ""count"": 72, ""type"": ""SCALAR"" }
  ],
  ""bufferViews"": [
    { ""buffer"": 0, ""byteLength"": " + vertexBytes + @", ""byteStride"": 16 },
    { ""buffer"": 0, ""byteOffset"": " + vertexBytes + @", ""byteLength"": " + indexBytes + @" }
  ],
  ""buffers"": [ { ""uri"": ""Trainer_interior_LOD00.bin"", ""byteLength"": " + (vertexBytes + indexBytes) + @" } ]
}";
        File.WriteAllText(Path.Combine(model, "Trainer_interior_LOD00.gltf"), gltf);
        // Encrypted: the package holds an .fsarchive (its models are never read).
        string enc = Path.Combine(root, "Official", "Steam", "test-aircraft-locked");
        Directory.CreateDirectory(Path.Combine(enc, "SimObjects", "Airplanes", "Locked", "model"));
        File.WriteAllBytes(Path.Combine(enc, "abcd.fsarchive"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(enc, "SimObjects", "Airplanes", "Locked", "model", "model.cfg"), "[models]\ninterior=Locked_interior.xml\n");
        // A livery package points at the aircraft's model and is not a cockpit of its own.
        Directory.CreateDirectory(Path.Combine(root, "Official", "Steam", "test-aircraft-trainer-livery-red", "SimObjects", "Airplanes", "Test_Trainer_Red"));
        return folder;
    }

    [Fact]
    public void Find_ReadsTheOpenInterior_AndSkipsEncryptedPackagesAndLiveries()
    {
        MakePackages();
        List<string> encrypted = new List<string>();
        List<MsfsCockpitPictures.Aircraft> found = MsfsCockpitPictures.Find(new[] { root }, encrypted);
        MsfsCockpitPictures.Aircraft a = Assert.Single(found);
        Assert.EndsWith("Trainer_interior_LOD00.gltf", a.Interior);
        Assert.EndsWith("Trainer_interior.xml", a.Behaviours);
        Assert.Contains("Test Trainer Asobo", a.Names);
        Assert.Contains("Test_Trainer", a.Names);
        Assert.Contains("test-aircraft-locked", encrypted);
    }

    [Fact]
    public void Build_DrawsTheCockpit_WithTheYokeAsAGrip_AndTheSwitchAsACallout()
    {
        MakePackages();
        string outRoot = Path.Combine(root, "out", "MSFS2020");
        MsfsCockpitPictures.Job job = new MsfsCockpitPictures.Job { OutRoot = outRoot, Width = 320, Force = true };
        job.PackageRoots.Add(root);
        List<MsfsCockpitPictures.SetResult> results = MsfsCockpitPictures.Build(job);
        MsfsCockpitPictures.SetResult r = Assert.Single(results);
        Assert.True(string.IsNullOrEmpty(r.Error), r.Error);
        Assert.Equal(1, r.Callouts);
        string json = File.ReadAllText(Path.Combine(r.Folder, "pictures.json"));
        Assert.Contains("\"game\":\"MSFS2020\"", json);
        Assert.Contains("Battery Master switch (electrical)", json);
        Assert.Contains("\"id\":\"device:stick\"", json);
        // The behaviour's template and event go with the callout.
        Assert.Contains("ASOBO_ELECTRICAL_Switch_Battery_Master_Template", json);
        Assert.Contains("TOGGLE_MASTER_BATTERY", json);
        Assert.True(File.Exists(Path.Combine(outRoot, "index.json")));
        Assert.Contains("Test Trainer Asobo", File.ReadAllText(Path.Combine(outRoot, "index.json")));
        // The switch callout sits on the panel picture where its box is (inside the picture).
        Match x = Regex.Match(json, "\"label\":\"Battery Master switch \\(electrical\\)\",\"x\":([0-9.]+),\"y\":([0-9.]+)");
        Assert.True(x.Success, json);
        double cx = double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), cy = double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.InRange(cx, 0.05, 0.95);
        Assert.InRange(cy, 0.05, 0.95);
    }

    [Fact]
    public void Accessor_ReadsInterleavedFloatsAndHalfFloatTextureCoordinates()
    {
        byte[] buf = new byte[32];
        BitConverter.GetBytes(1.5f).CopyTo(buf, 0);
        BitConverter.GetBytes(-2f).CopyTo(buf, 4);
        BitConverter.GetBytes(3f).CopyTo(buf, 8);
        BitConverter.GetBytes(HalfOf(0.5f)).CopyTo(buf, 12);
        BitConverter.GetBytes(HalfOf(0.25f)).CopyTo(buf, 14);
        BitConverter.GetBytes(4f).CopyTo(buf, 16);
        Dictionary<string, object> view = new Dictionary<string, object> { { "byteStride", 16.0 } };
        Dictionary<string, object> pos = new Dictionary<string, object> { { "count", 2.0 }, { "componentType", 5126.0 }, { "type", "VEC3" } };
        Dictionary<string, object> uv = new Dictionary<string, object> { { "count", 1.0 }, { "componentType", 5122.0 }, { "type", "VEC2" }, { "byteOffset", 12.0 } };
        float[] p = MsfsCockpitPictures.ReadAccessor(pos, view, buf, false);
        Assert.Equal(new[] { 1.5f, -2f, 3f, 4f, 0f, 0f }, p);
        float[] t = MsfsCockpitPictures.ReadAccessor(uv, view, buf, true);
        Assert.Equal(new[] { 0.5f, 0.25f }, t);
        // An accessor past the buffer's end is not read.
        Dictionary<string, object> tooLong = new Dictionary<string, object> { { "count", 9.0 }, { "componentType", 5126.0 }, { "type", "VEC3" } };
        Assert.Null(MsfsCockpitPictures.ReadAccessor(tooLong, view, buf, false));
    }

    [Fact]
    public void Behaviours_NameTheNodesByNodeIdOrByTheTemplate_AndTheirEvents()
    {
        string xml = Path.Combine(root, "b.xml");
        Directory.CreateDirectory(root);
        File.WriteAllText(xml, "<ModelInfo><Behaviors><!-- <UseTemplate Name=\"ASOBO_ENGINE_Lever_Throttle_Template\"/> -->" +
                               "<UseTemplate Name=\"ASOBO_LIGHTING_Switch_Light_Taxi_Template\"></UseTemplate>" +
                               "<UseTemplate Name=\"ASOBO_FUEL_Lever_Mixture_Template\"><NODE_ID>FUEL_Lever_Mixture_1</NODE_ID><CODE>1 (&gt;K:MIXTURE_SET)</CODE></UseTemplate>" +
                               "<UseTemplate Name=\"ASOBO_INSTRUMENT_Needle_Airspeed_Template\"/></Behaviors></ModelInfo>");
        MsfsCockpitPictures.Behaviours b = MsfsCockpitPictures.Behaviours.Read(xml);
        Assert.Equal("ASOBO_LIGHTING_Switch_Light_Taxi_Template", b.Nodes["LIGHTING_Switch_Light_Taxi"]);
        Assert.True(b.Nodes.ContainsKey("FUEL_Lever_Mixture_1"));
        Assert.Contains("K:MIXTURE_SET", b.Events["FUEL_Lever_Mixture_1"]);
        Assert.False(b.Nodes.ContainsKey("ENGINE_Lever_Throttle"));
        Assert.False(b.Nodes.Keys.Any(k => k.Contains("Needle")));
    }

    [Fact]
    public void EyePoint_IsTheDatumPlusTheEyepointPlusTheCameraOffset()
    {
        string folder = MakePackages();
        Assert.True(MsfsCockpitPictures.EyePoint(folder, out Vec3 eye));
        // Longitudinal (1 - 1) ft + 0 m, vertical 2 ft + 0.1 m, lateral -1 ft (left).
        Assert.Equal(0, eye.X, 3);
        Assert.Equal(2 * 0.3048 + 0.1, eye.Y, 3);
        Assert.Equal(-0.3048, eye.Z, 3);
    }

    [Fact]
    public void Labels_AreTheModelsOwnPartNames()
    {
        Match m = MsfsCockpitPictures.Interactive.Match("LIGHTING_Switch_Light_Beacon_1");
        Assert.True(m.Success);
        Assert.Equal("Light Beacon switch 1 (lighting)", MsfsCockpitPictures.Label(m));
        Assert.Equal("Deicesystem switch airframe", MsfsCockpitPictures.LooseLabel("DEICESYSTEM_SWITCH_AIRFRAME"));
        Assert.True(MsfsCockpitPictures.Loose.IsMatch("LEVER_THROTTLE_HANDLE"));
        // glTF: +Z forward, +X left -> the scene's x forward, z right.
        Vec3 s = MsfsCockpitPictures.ToScene(new Vec3(1, 2, 3));
        Assert.Equal(3, s.X);
        Assert.Equal(2, s.Y);
        Assert.Equal(-1, s.Z);
    }
}
