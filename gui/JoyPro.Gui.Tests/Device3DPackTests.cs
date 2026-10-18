using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// A turntable as tools/device_drawings.py turntable writes it, made up: the parts sit on a circle around the vertical
// axis (part k at k * 360 / n degrees from the back), a part faces the viewer while it is on the near half. Frames are
// small grey PNGs.
internal static class TurntableFixture
{
    public static string Write(string folder, string model, IReadOnlyList<string> parts, int steps = 8, double[]? pitches = null, string source = "Rendered from a test model by JoyPro")
    {
        pitches ??= [5, 25, 45];
        string dir = Path.Combine(folder, model);
        Directory.CreateDirectory(Path.Combine(dir, "frames"));
        List<object> frames = [];
        using SKBitmap bitmap = new(64, 64);
        bitmap.Erase(new SKColor(90, 96, 104));
        byte[] png = bitmap.Encode(SKEncodedImageFormat.Png, 90).ToArray();
        foreach (double pitch in pitches)
        {
            for (int y = 0; y < steps; y++)
            {
                double yaw = y * 360.0 / steps;
                string file = string.Format(CultureInfo.InvariantCulture, "frames/p{0:00}-y{1:000}.png", pitch, yaw);
                File.WriteAllBytes(Path.Combine(dir, file), png);
                Dictionary<string, object> points = new();
                for (int k = 0; k < parts.Count; k++)
                {
                    double a = (k * 360.0 / Math.Max(1, parts.Count) + yaw) * Math.PI / 180;
                    points[parts[k]] = new object[] { Math.Round(0.5 + 0.35 * Math.Sin(a), 4), Math.Round(0.3 + 0.4 * k / Math.Max(1, parts.Count), 4), Math.Cos(a) > 0.05 };
                }
                frames.Add(new { pitch, yaw, file, points });
            }
        }
        File.WriteAllText(Path.Combine(dir, "turntable.json"), JsonSerializer.Serialize(new { model, source, yawSteps = steps, pitches, frames }));
        return dir;
    }

    // A release folder as tools/build_device_3d_packs.ps1 makes it (without running it): one zip per model + manifest.
    public static string Feed(string feed, string source, IEnumerable<(string Model, int Version)> models, bool badHash = false)
    {
        Directory.CreateDirectory(feed);
        List<object> packs = [];
        foreach ((string model, int version) in models)
        {
            string zip = Path.Combine(feed, model + ".zip");
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(Path.Combine(source, model), zip);
            string sha = CockpitPicturePacks.Sha256Of(zip);
            if (badHash)
                sha = new string('0', 64);
            packs.Add(new { model, asset = model + ".zip", size = new FileInfo(zip).Length, sha256 = sha, version, built = "2026-10-18T12:00:00Z", source = "test" });
        }
        File.WriteAllText(Path.Combine(feed, "manifest.json"), JsonSerializer.Serialize(new { format = DeviceTurntablePacks.FormatName, packVersion = 1, tag = DeviceTurntablePacks.ReleaseTag, packs }));
        return feed;
    }
}

public sealed class Device3DPackTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro-3d-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        DeviceTurntable.ClearCache();
        try
        {
            Directory.Delete(temp, true);
        }
        catch (Exception)
        {
        }
    }

    private string Temp(string name)
    {
        string dir = Path.Combine(temp, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly string[] WarthogParts = ["stick.hat.tms", "stick.hat.dms", "stick.trigger"];

    [Fact]
    public void Manifest_LeavesOutUnsafePacks_AndRefusesANewerFormat()
    {
        string sha = new('a', 64);
        DeviceTurntablePackManifest m = DeviceTurntablePacks.ParseManifest(
            "{\"format\":\"joypro-device-3d-packs\",\"packVersion\":1,\"tag\":\"device-3d-v1\",\"packs\":[" +
            "{\"model\":\"thrustmaster-warthog-stick\",\"asset\":\"thrustmaster-warthog-stick.zip\",\"size\":10,\"sha256\":\"" + sha + "\",\"version\":2}," +
            "{\"model\":\"../evil\",\"asset\":\"a.zip\",\"sha256\":\"" + sha + "\"}," +
            "{\"model\":\"ok-model\",\"asset\":\"../a.zip\",\"sha256\":\"" + sha + "\"}," +
            "{\"model\":\"no-hash\",\"asset\":\"b.zip\",\"sha256\":\"xyz\"}]}");
        DeviceTurntablePack pack = Assert.Single(m.Packs);
        Assert.Equal(2, pack.Version);
        Assert.Equal("device-3d-v1", m.Tag);
        Assert.NotNull(m.Find("THRUSTMASTER-WARTHOG-STICK"));
        Assert.Throws<DeviceTurntablePackException>(() => DeviceTurntablePacks.ParseManifest("{\"format\":\"joypro-device-3d-packs\",\"packVersion\":2,\"packs\":[]}"));
        Assert.Throws<DeviceTurntablePackException>(() => DeviceTurntablePacks.ParseManifest("{\"format\":\"joypro-cockpit-packs\",\"packs\":[]}"));
    }

    // The build tool: one zip per model with turntable.json, the frames and CREDITS.txt, a manifest with checksum and
    // version; an unchanged model keeps its zip and version, a changed one gets the next version; JoyPro installs it.
    [Fact]
    public async Task BuildTool_WritesPacksAndManifest_ThatJoyProInstalls()
    {
        string source = Temp("src"), out1 = Path.Combine(temp, "release", "device-3d");
        TurntableFixture.Write(source, "thrustmaster-warthog-stick", WarthogParts);
        string script = Path.Combine(RepoRoot(), "tools", "build_device_3d_packs.ps1");
        RunTool(script, source, out1);
        string manifestText = File.ReadAllText(Path.Combine(out1, "manifest.json"));
        DeviceTurntablePackManifest manifest = DeviceTurntablePacks.ParseManifest(manifestText);
        DeviceTurntablePack pack = Assert.Single(manifest.Packs);
        Assert.Equal("thrustmaster-warthog-stick", pack.Model);
        Assert.Equal(1, pack.Version);
        Assert.Equal(new FileInfo(Path.Combine(out1, pack.Asset)).Length, pack.Size);
        Assert.Equal(CockpitPicturePacks.Sha256Of(Path.Combine(out1, pack.Asset)), pack.Sha256);
        using (ZipArchive zip = ZipFile.OpenRead(Path.Combine(out1, pack.Asset)))
        {
            List<string> names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("turntable.json", names);
            Assert.Contains("CREDITS.txt", names);
            Assert.Equal(24, names.Count(n => n.StartsWith("frames/", StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.Ordinal)));
        }
        // Unchanged: same version and checksum.
        RunTool(script, source, out1);
        Assert.Equal(pack.Sha256, Assert.Single(DeviceTurntablePacks.ParseManifest(File.ReadAllText(Path.Combine(out1, "manifest.json"))).Packs).Sha256);
        // Installed from the folder as a local feed.
        string root = Temp("installed");
        DeviceTurntablePacks packs = new(out1, root);
        Assert.Equal(DeviceTurntablePackState.Missing, packs.StateOf(pack));
        Assert.True(await packs.DownloadAsync(pack));
        Assert.Equal(DeviceTurntablePackState.Installed, packs.StateOf(pack));
        Assert.NotNull(DeviceTurntable.Of(root, "thrustmaster-warthog-stick"));
        // Changed source: next version, offered as an update.
        File.AppendAllText(Path.Combine(source, "thrustmaster-warthog-stick", "turntable.json"), " ");
        RunTool(script, source, out1);
        DeviceTurntablePack v2 = Assert.Single(DeviceTurntablePacks.ParseManifest(File.ReadAllText(Path.Combine(out1, "manifest.json"))).Packs);
        Assert.Equal(2, v2.Version);
        Assert.Equal(DeviceTurntablePackState.UpdateAvailable, packs.StateOf(v2));
        // A model that is not in the catalog is refused.
        TurntableFixture.Write(source, "no-such-device-model", WarthogParts);
        Assert.NotEqual(0, RunTool(script, source, out1, check: false));
    }

    [Fact]
    public async Task Install_RefusesBadChecksumAndForeignFiles()
    {
        string source = Temp("src");
        TurntableFixture.Write(source, "thrustmaster-warthog-stick", WarthogParts);
        string root = Temp("installed");
        DeviceTurntablePack bad = Assert.Single(DeviceTurntablePacks.ParseManifest(File.ReadAllText(Path.Combine(TurntableFixture.Feed(Temp("bad"), source, [("thrustmaster-warthog-stick", 1)], badHash: true), "manifest.json"))).Packs);
        DeviceTurntablePacks packs = new(Path.Combine(temp, "bad"), root);
        await Assert.ThrowsAsync<DeviceTurntablePackException>(() => packs.DownloadAsync(bad));
        Assert.False(Directory.Exists(Path.Combine(root, "thrustmaster-warthog-stick")));
        // A zip with a file outside frames/.
        File.WriteAllText(Path.Combine(source, "thrustmaster-warthog-stick", "run.exe"), "x");
        string feed = TurntableFixture.Feed(Temp("evil"), source, [("thrustmaster-warthog-stick", 1)]);
        DeviceTurntablePack evil = Assert.Single(DeviceTurntablePacks.ParseManifest(File.ReadAllText(Path.Combine(feed, "manifest.json"))).Packs);
        await Assert.ThrowsAsync<DeviceTurntablePackException>(() => new DeviceTurntablePacks(feed, root).DownloadAsync(evil));
        Assert.False(Directory.Exists(Path.Combine(root, "thrustmaster-warthog-stick")));
        Assert.Null(DeviceTurntable.Of(root, "..\\thrustmaster-warthog-stick"));
    }

    // A connected device with a pack: asked (Download / Not now), downloaded without asking (always), nothing (never);
    // offline: nothing, the flat drawing stays.
    [Fact]
    public async Task ConnectedDevice_OffersAndDownloadsIts3DView_ByTheSetting()
    {
        string source = Temp("src");
        TurntableFixture.Write(source, "thrustmaster-warthog-stick", WarthogParts);
        TurntableFixture.Write(source, "thrustmaster-hotas-warthog-throttle", ["throttle.hat.coolie"]);
        string feed = TurntableFixture.Feed(Temp("feed"), source, [("thrustmaster-warthog-stick", 1), ("thrustmaster-hotas-warthog-throttle", 1)]);
        string[] connected = ["thrustmaster-warthog-stick", "vkb-gunfighter-mcg-pro", ""];

        MainWindowViewModel ask = Vm(feed, Temp("ask"), CockpitPicturePacks.Ask);
        await ask.CheckDevice3DAsync(connected);
        Assert.True(ask.HasDevice3DOffer);
        Assert.Contains("MB", ask.Device3DOfferText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp, "ask", "thrustmaster-warthog-stick", "turntable.json")));
        await ask.AcceptDevice3DCommand.ExecuteAsync(null);
        Assert.False(ask.HasDevice3DOffer);
        Assert.True(File.Exists(Path.Combine(temp, "ask", "thrustmaster-warthog-stick", "pack.json")));
        Assert.False(Directory.Exists(Path.Combine(temp, "ask", "thrustmaster-hotas-warthog-throttle")));
        // Installed: not offered again.
        await ask.CheckDevice3DAsync(connected);
        Assert.False(ask.HasDevice3DOffer);

        MainWindowViewModel later = Vm(feed, Temp("later"), CockpitPicturePacks.Ask);
        await later.CheckDevice3DAsync(connected);
        later.DismissDevice3DCommand.Execute(null);
        await later.CheckDevice3DAsync(connected);
        Assert.False(later.HasDevice3DOffer);
        Assert.False(Directory.Exists(Path.Combine(temp, "later", "thrustmaster-warthog-stick")));

        MainWindowViewModel always = Vm(feed, Temp("always"), CockpitPicturePacks.Always);
        await always.CheckDevice3DAsync(connected);
        Assert.False(always.HasDevice3DOffer);
        Assert.True(File.Exists(Path.Combine(temp, "always", "thrustmaster-warthog-stick", "pack.json")), always.Device3DStatus);

        MainWindowViewModel never = Vm(feed, Temp("never"), CockpitPicturePacks.Never);
        await never.CheckDevice3DAsync(connected);
        Assert.False(never.HasDevice3DOffer);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temp, "never")));

        MainWindowViewModel offline = Vm(Path.Combine(temp, "no-feed-here"), Temp("offline"), CockpitPicturePacks.Always);
        await offline.CheckDevice3DAsync(connected);
        Assert.False(offline.HasDevice3DOffer);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temp, "offline")));
    }

    private static MainWindowViewModel Vm(string feed, string root, string mode) =>
        new(new AxisTuningFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
        {
            Device3DPacksFactory = () => new DeviceTurntablePacks(feed, root),
            Device3DMode = () => mode
        };

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "tools", "build_device_3d_packs.ps1")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository not found");
    }

    private static int RunTool(string script, string source, string output, bool check = true)
    {
        ProcessStartInfo psi = new("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Source", source, "-Out", output })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (check)
            Assert.True(p.ExitCode == 0, stdout + stderr);
        return p.ExitCode;
    }
}
