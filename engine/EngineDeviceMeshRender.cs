using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JoyPro.CockpitPictures;
using JoyPro.DeviceReference;

namespace JoyPro.Engine
{
    // engine devices reference mesh-render --game DCS --aircraft <plane> --grip stick|throttle [--view <yaw>,<pitch>]
    //     [--size <px>] --folder <folder>
    // The grip of an aircraft in a game's own 3D cockpit (read the same way as the cockpit pictures: plain files only),
    // rendered without textures for JoyPro's drawing of a replica device (tools/device_drawings.py mesh): an orthographic
    // view of the grip's meshes only, as buffers a drawing is made from, and where each named part of the grip lands.
    // Writes into the folder: part.bin (int32 per pixel: the mesh part, -1 = background), light.bin (float32: diffuse
    // light 0..1), albedo.png (the surface colour without light), meta.json (size, camera, the grip's named parts with
    // their picture position and whether they are seen). Nothing is written into the game folder.
    internal static partial class Program
    {
        private static int RunDeviceMeshRender(List<string> args, string outPath)
        {
            string game = PopOptionValue(args, "--game") ?? "DCS";
            string aircraft = PopOptionValue(args, "--aircraft") ?? string.Empty;
            string gripKind = (PopOptionValue(args, "--grip") ?? "stick").ToLowerInvariant();
            string view = PopOptionValue(args, "--view") ?? "0,10";
            int size = PopOptionInt(args, "--size", 2400);
            string folder = PopOptionValue(args, "--folder") ?? string.Empty;
            int gripIndex = PopOptionInt(args, "--index", 0);
            // Meshes of the grip that the device does not have (a lever of the real cockpit), by node name (regex).
            string skip = PopOptionValue(args, "--skip") ?? string.Empty;
            if (aircraft.Length == 0 || folder.Length == 0)
                return Fail("devices reference mesh-render needs --aircraft <plane> and --folder <folder>.");
            if (!string.Equals(GameInstalls.Resolve(game), "DCS", StringComparison.OrdinalIgnoreCase))
                return Fail("devices reference mesh-render reads DCS World cockpits so far.");
            List<string> warnings = new List<string>();
            GameInstall install = GameInstalls.Find("DCS", warnings);
            if (!install.Installed || !Directory.Exists(install.InstallPath))
                return Fail("DCS World is not installed.");
            string[] yp = view.Split(',');
            double yaw = double.Parse(yp[0], CultureInfo.InvariantCulture), pitch = yp.Length > 1 ? double.Parse(yp[1], CultureInfo.InvariantCulture) : 0;
            string work = Path.Combine(Path.GetTempPath(), "JoyPro-mesh-render-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            try
            {
                List<string> notes = new List<string>();
                DcsCockpitPictures.Cockpit cockpit = DcsCockpitPictures.Cockpits(install.InstallPath, work, notes, new List<string> { aircraft }).FirstOrDefault();
                if (cockpit == null)
                    return Fail("No cockpit of '" + aircraft + "' found in DCS World. " + string.Join("; ", notes.Take(3)));
                EdmFile edm = EdmFile.Read(cockpit.Edm);
                string moduleDir = Path.Combine(install.InstallPath, "Mods", "aircraft", cockpit.Module);
                using (TextureLibrary lib = new TextureLibrary())
                {
                    foreach (string dir in Directory.GetDirectories(moduleDir))
                        lib.AddFolder(dir, true);
                    lib.AddFolder(Path.Combine(install.InstallPath, "Bazar", "World", "textures"), false);
                    EdmScene.Options options = new EdmScene.Options { Texture = lib.Get };
                    CockpitScene scene = EdmScene.Build(edm, options);
                    List<CockpitGrip> all = DcsCockpitPictures.Grips(edm, scene, cockpit.Gauges, options);
                    DcsCockpitPictures.ShowHiddenGrips(edm, ref scene, ref options, ref all, cockpit.Gauges);
                    List<CockpitGrip> grips = all.Where(g => g.Device == gripKind).ToList();
                    if (grips.Count <= gripIndex)
                        return Fail("The " + aircraft + " cockpit has no " + gripKind + " grip the reader finds (" + grips.Count + ").");
                    CockpitGrip grip = grips[gripIndex];
                    if (skip.Length > 0)
                    {
                        System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex(skip, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        grip.Meshes = grip.Meshes.Where(m => !(scene.Meshes[m].Node >= 0 && scene.Meshes[m].Node < scene.NodeNames.Count && rx.IsMatch(scene.NodeNames[scene.Meshes[m].Node]))).ToList();
                        grip.Parts = grip.Parts.Where(pp => !rx.IsMatch(pp.Id)).ToList();
                    }
                    int turntable = PopOptionInt(args, "--turntable", 0);
                    if (turntable > 0)
                        return RenderTurntable(scene, grip, args, folder, size, turntable, aircraft, gripKind, outPath);
                    // The camera: yaw around the vertical (0 = from behind, the pilot's view; 90 = from the left),
                    // pitch down from the horizontal.
                    double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
                    Vec3 forward = new Vec3(Math.Cos(p) * Math.Cos(y), -Math.Sin(p), Math.Cos(p) * Math.Sin(y));
                    RenderCamera cam = new RenderCamera { Ortho = true, Width = size, Height = size };
                    cam.Aim(forward);
                    Vec3 mn, mx;
                    scene.Bounds(grip.Meshes, out mn, out mx);
                    Vec3 centre = (mn + mx) * 0.5;
                    // Fit the projected bounds (the eight corners) with a margin.
                    double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                    foreach (Vec3 c in Corners(mn, mx))
                    {
                        Vec3 d = c - centre;
                        double vx = Vec3.Dot(d, cam.Right), vy = Vec3.Dot(d, cam.Up);
                        minX = Math.Min(minX, vx); maxX = Math.Max(maxX, vx); minY = Math.Min(minY, vy); maxY = Math.Max(maxY, vy);
                    }
                    double half = Math.Max(maxX - minX, maxY - minY) / 2 * 1.06;
                    Vec3 mid = centre + cam.Right * ((minX + maxX) / 2) + cam.Up * ((minY + maxY) / 2);
                    cam.HalfHeight = half;
                    cam.Eye = mid - cam.Forward * Math.Max(1.0, (mx - mn).Length * 3);
                    RenderBuffers b = SoftwareRenderer.Render(scene, cam, grip.Meshes, new SoftwareRenderer.Light { Ambient = 0, Diffuse = 1, Headlight = 0 });
                    Directory.CreateDirectory(folder);
                    int n = b.Width * b.Height;
                    using (BinaryWriter w = new BinaryWriter(File.Create(Path.Combine(folder, "part.bin"))))
                    {
                        for (int i = 0; i < n; i++)
                            w.Write(b.Covered[i] ? b.Part[i] : -1);
                    }
                    Vec3 light = new Vec3(0.3, 1, -0.2).Normalized;
                    using (BinaryWriter w = new BinaryWriter(File.Create(Path.Combine(folder, "light.bin"))))
                    {
                        for (int i = 0; i < n; i++)
                        {
                            Vec3 nn = new Vec3(b.Normal[3 * i], b.Normal[3 * i + 1], b.Normal[3 * i + 2]);
                            // Two-sided: the side facing the camera.
                            if (Vec3.Dot(nn, cam.Forward) > 0)
                                nn = nn * -1;
                            w.Write(b.Covered[i] ? (float)Math.Max(0, Vec3.Dot(nn, light) * 0.75 + Math.Max(0, -Vec3.Dot(nn, cam.Forward)) * 0.25) : 0f);
                        }
                    }
                    using (BinaryWriter w = new BinaryWriter(File.Create(Path.Combine(folder, "depth.bin"))))
                    {
                        for (int i = 0; i < n; i++)
                            w.Write(b.Covered[i] ? b.Depth[i] : float.MaxValue);
                    }
                    PngWriter.Write(Path.Combine(folder, "albedo.png"), b.Albedo, b.Width, b.Height, 3);
                    List<object> parts = new List<object>();
                    foreach (CockpitCallout part in grip.Parts)
                    {
                        double sx, sy, depth;
                        cam.Project(part.Position, out sx, out sy, out depth);
                        int px = (int)Math.Round(sx), py = (int)Math.Round(sy);
                        bool seen = false;
                        if (px >= 0 && py >= 0 && px < b.Width && py < b.Height)
                        {
                            // Seen when the surface at its spot is no farther than the part's front.
                            for (int dy = -6; dy <= 6 && !seen; dy++)
                                for (int dx = -6; dx <= 6 && !seen; dx++)
                                {
                                    int qx = px + dx, qy = py + dy;
                                    if (qx < 0 || qy < 0 || qx >= b.Width || qy >= b.Height)
                                        continue;
                                    int i = qy * b.Width + qx;
                                    seen = b.Covered[i] && b.Depth[i] <= depth + 0.006;
                                }
                        }
                        parts.Add(new { part.Id, part.Label, X = Math.Round(sx, 1), Y = Math.Round(sy, 1), Depth = Math.Round(depth, 4), Seen = seen });
                    }
                    var meta = new
                    {
                        Format = "joypro-mesh-render",
                        Game = "DCS",
                        Aircraft = aircraft,
                        Module = cockpit.Module,
                        Model = Path.GetFileName(cockpit.Edm),
                        Grip = gripKind,
                        Skip = skip,
                        Width = b.Width,
                        Height = b.Height,
                        Yaw = yaw,
                        Pitch = pitch,
                        MetresPerPixel = half * 2 / b.Height,
                        Meshes = grip.Meshes.Select(m => new { Index = m, scene.Meshes[m].Name, scene.Meshes[m].Part, Material = scene.Materials[scene.Meshes[m].Material].Name, Node = scene.Meshes[m].Node >= 0 && scene.Meshes[m].Node < scene.NodeNames.Count ? scene.NodeNames[scene.Meshes[m].Node] : string.Empty }).ToList(),
                        Parts = parts
                    };
                    File.WriteAllText(Path.Combine(folder, "meta.json"), EngineSerialization.ToJson(meta), new UTF8Encoding(false));
                    EmitPayload(aircraft + " " + gripKind + ": " + grip.Meshes.Count + " meshes, " + parts.Count + " named parts -> " + folder + Environment.NewLine, outPath);
                    return 0;
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                }
                catch (Exception)
                {
                }
            }
        }

        // The camera of a single view as the normal mode frames it (the grip's projected bounds with a margin).
        private static RenderCamera ViewCamera(double yaw, double pitch, Vec3 mn, Vec3 mx, int size)
        {
            RenderCamera cam = new RenderCamera { Ortho = true, Width = size, Height = size };
            cam.Aim(Direction(yaw, pitch));
            Vec3 centre = (mn + mx) * 0.5;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (Vec3 c in Corners(mn, mx))
            {
                Vec3 d = c - centre;
                double vx = Vec3.Dot(d, cam.Right), vy = Vec3.Dot(d, cam.Up);
                minX = Math.Min(minX, vx); maxX = Math.Max(maxX, vx); minY = Math.Min(minY, vy); maxY = Math.Max(maxY, vy);
            }
            cam.HalfHeight = Math.Max(maxX - minX, maxY - minY) / 2 * 1.06;
            Vec3 mid = centre + cam.Right * ((minX + maxX) / 2) + cam.Up * ((minY + maxY) / 2);
            cam.Eye = mid - cam.Forward * Math.Max(1.0, (mx - mn).Length * 3);
            return cam;
        }

        private static Vec3 Direction(double yaw, double pitch)
        {
            double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
            return new Vec3(Math.Cos(p) * Math.Cos(y), -Math.Sin(p), Math.Cos(p) * Math.Sin(y));
        }

        // A turntable of the grip for the rotatable view (docs/device-reference.md, "3D view"): frames around the
        // vertical at a few pitches, all framed alike (the bounding sphere of the grip's upper keep-top part), each a
        // cel-shaded picture (flat colours in two tones, outlines between the model's parts) with a transparent
        // background, and where the anchors (the model's named parts, and points picked on a single view and placed in
        // 3D through that view's depth) land on every frame and whether they face the camera.
        //   --turntable <yaw steps> [--pitches 5,25,45] [--keep-top <0..1>] [--anchors <file>;<file>]
        //   anchors file: { "view": [yaw, pitch], "size": <px>, "points": { "<key>": [x, y] } } (pixels of that view)
        private static int RenderTurntable(CockpitScene scene, CockpitGrip grip, List<string> args, string folder, int size, int steps, string aircraft, string gripKind, string outPath)
        {
            double[] pitches = (PopOptionValue(args, "--pitches") ?? "5,25,45").Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            double keepTop = double.Parse(PopOptionValue(args, "--keep-top") ?? "1", CultureInfo.InvariantCulture);
            string anchorFiles = PopOptionValue(args, "--anchors") ?? string.Empty;
            Vec3 mn, mx;
            scene.Bounds(grip.Meshes, out mn, out mx);
            // Anchors in 3D: the named parts, then the points of the anchor files through their view's depth.
            Dictionary<string, Vec3> anchors = new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);
            foreach (CockpitCallout part in grip.Parts)
                anchors[part.Id] = part.Position;
            foreach (string file in anchorFiles.Split(';').Select(f => f.Trim()).Where(f => f.Length > 0))
            {
                Dictionary<string, object> root = MiniJson.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                List<object> v = root["view"] as List<object>;
                int asize = Convert.ToInt32(root["size"], CultureInfo.InvariantCulture);
                RenderCamera cam = ViewCamera(Convert.ToDouble(v[0], CultureInfo.InvariantCulture), Convert.ToDouble(v[1], CultureInfo.InvariantCulture), mn, mx, asize);
                RenderBuffers b = SoftwareRenderer.Render(scene, cam, grip.Meshes, new SoftwareRenderer.Light());
                double s = cam.Height / 2.0 / cam.HalfHeight;
                foreach (KeyValuePair<string, object> pair in (Dictionary<string, object>)root["points"])
                {
                    List<object> xy = (List<object>)pair.Value;
                    double px = Convert.ToDouble(xy[0], CultureInfo.InvariantCulture), py = Convert.ToDouble(xy[1], CultureInfo.InvariantCulture);
                    // The nearest covered pixel: the surface under the point.
                    int best = -1;
                    double bestD = double.MaxValue;
                    for (int dy = -10; dy <= 10; dy++)
                        for (int dx = -10; dx <= 10; dx++)
                        {
                            int qx = (int)Math.Round(px) + dx, qy = (int)Math.Round(py) + dy;
                            if (qx < 0 || qy < 0 || qx >= b.Width || qy >= b.Height || !b.Covered[qy * b.Width + qx])
                                continue;
                            double d = dx * dx + dy * dy;
                            if (d < bestD)
                            {
                                bestD = d;
                                best = qy * b.Width + qx;
                            }
                        }
                    if (best < 0)
                        continue;
                    double vx = (px - cam.Width / 2.0) / s, vy = -(py - cam.Height / 2.0) / s;
                    anchors[pair.Key] = cam.Eye + cam.Right * vx + cam.Up * vy + cam.Forward * b.Depth[best];
                }
            }
            // One framing for every frame: the sphere around the kept upper part of the grip.
            double top = mx.Y, bottom = mx.Y - (mx.Y - mn.Y) * Math.Max(0.05, Math.Min(1, keepTop));
            Vec3 lo = new Vec3(mn.X, bottom, mn.Z), hi = new Vec3(mx.X, top, mx.Z);
            Vec3 centre = (lo + hi) * 0.5;
            double radius = (hi - lo).Length / 2 * 1.02;
            Directory.CreateDirectory(Path.Combine(folder, "frames"));
            List<object> frames = new List<object>();
            int ss = 2;
            for (int pi = 0; pi < pitches.Length; pi++)
            {
                for (int yi = 0; yi < steps; yi++)
                {
                    double yaw = yi * 360.0 / steps;
                    RenderCamera cam = new RenderCamera { Ortho = true, Width = size * ss, Height = size * ss, HalfHeight = radius };
                    cam.Aim(Direction(yaw, pitches[pi]));
                    cam.Eye = centre - cam.Forward * Math.Max(1.0, radius * 6);
                    RenderBuffers b = SoftwareRenderer.Render(scene, cam, grip.Meshes, new SoftwareRenderer.Light());
                    byte[] rgba = CelFrame(b, cam, ss, size);
                    string name = "p" + pi + "_y" + yi.ToString("D2", CultureInfo.InvariantCulture) + ".png";
                    PngWriter.Write(Path.Combine(folder, "frames", name), rgba, size, size, 4);
                    Dictionary<string, object> points = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, Vec3> a in anchors)
                    {
                        double sx, sy, depth;
                        cam.Project(a.Value, out sx, out sy, out depth);
                        bool seen = false;
                        int px = (int)Math.Round(sx), py = (int)Math.Round(sy);
                        for (int dy = -5; dy <= 5 && !seen; dy++)
                            for (int dx = -5; dx <= 5 && !seen; dx++)
                            {
                                int qx = px + dx, qy = py + dy;
                                if (qx < 0 || qy < 0 || qx >= b.Width || qy >= b.Height)
                                    continue;
                                int i = qy * b.Width + qx;
                                seen = b.Covered[i] && b.Depth[i] >= depth - radius * 0.03;
                            }
                        points[a.Key] = new List<object> { Math.Round(sx / ss / size, 4), Math.Round(sy / ss / size, 4), seen };
                    }
                    frames.Add(new Dictionary<string, object> { ["pitch"] = pitches[pi], ["yaw"] = yaw, ["file"] = "frames/" + name, ["points"] = points });
                }
            }
            Dictionary<string, object> meta = new Dictionary<string, object>
            {
                ["format"] = "joypro-mesh-turntable",
                ["aircraft"] = aircraft,
                ["grip"] = gripKind,
                ["size"] = size,
                ["yawSteps"] = steps,
                ["pitches"] = pitches.Select(p => (object)p).ToList(),
                ["anchors"] = anchors.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (object)k).ToList(),
                ["frames"] = frames
            };
            File.WriteAllText(Path.Combine(folder, "turntable.json"), EngineSerialization.ToJson(meta), new UTF8Encoding(false));
            EmitPayload(aircraft + " " + gripKind + ": " + frames.Count + " frames, " + anchors.Count + " anchors -> " + folder + Environment.NewLine, outPath);
            return 0;
        }

        // A cel-shaded frame: the surface colour snapped to a few tones, a darker tone where the light falls off, dark
        // outlines where the model's part or the background changes; downsampled by ss (smooth edges); RGBA.
        private static byte[] CelFrame(RenderBuffers b, RenderCamera cam, int ss, int size)
        {
            int w = b.Width, h = b.Height;
            Vec3 light = new Vec3(0.3, 1, -0.2).Normalized;
            byte[] full = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!b.Covered[i])
                        continue;
                    int part = b.Part[i];
                    bool edge = false;
                    for (int k = 1; k <= ss && !edge; k++)
                    {
                        foreach (int j in new[] { i - k, i + k, i - k * w, i + k * w })
                        {
                            if (j < 0 || j >= w * h || !b.Covered[j] || b.Part[j] != part ||
                                Math.Abs(b.Depth[j] - b.Depth[i]) > 0.01)
                            {
                                edge = true;
                                break;
                            }
                        }
                    }
                    byte r, g, bl;
                    if (edge)
                    {
                        r = g = bl = 236;
                    }
                    else
                    {
                        Vec3 n = new Vec3(b.Normal[3 * i], b.Normal[3 * i + 1], b.Normal[3 * i + 2]);
                        if (Vec3.Dot(n, cam.Forward) > 0)
                            n = n * -1;
                        double lit = Math.Max(0, Vec3.Dot(n, light)) * 0.7 + Math.Max(0, -Vec3.Dot(n, cam.Forward)) * 0.3;
                        double tone = lit >= 0.42 ? 1.0 : 0.72;
                        // Snap the surface colour to a few steps so the faces read flat.
                        int ar = b.Albedo[3 * i], ag = b.Albedo[3 * i + 1], ab = b.Albedo[3 * i + 2];
                        ar = Math.Max(34, (ar / 40) * 40 + 20);
                        ag = Math.Max(34, (ag / 40) * 40 + 20);
                        ab = Math.Max(34, (ab / 40) * 40 + 20);
                        r = (byte)Math.Min(255, ar * tone);
                        g = (byte)Math.Min(255, ag * tone);
                        bl = (byte)Math.Min(255, ab * tone);
                    }
                    full[4 * i] = r;
                    full[4 * i + 1] = g;
                    full[4 * i + 2] = bl;
                    full[4 * i + 3] = 255;
                }
            }
            // The silhouette: a dark rim one pixel outside the grip so it reads on any background.
            byte[] outp = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double r = 0, g = 0, bl = 0, a = 0;
                    for (int dy = 0; dy < ss; dy++)
                        for (int dx = 0; dx < ss; dx++)
                        {
                            int i = ((y * ss + dy) * w + (x * ss + dx)) * 4;
                            double al = full[i + 3] / 255.0;
                            r += full[i] * al; g += full[i + 1] * al; bl += full[i + 2] * al; a += al;
                        }
                    int o = (y * size + x) * 4;
                    if (a > 0)
                    {
                        outp[o] = (byte)(r / a);
                        outp[o + 1] = (byte)(g / a);
                        outp[o + 2] = (byte)(bl / a);
                    }
                    outp[o + 3] = (byte)Math.Round(a / (ss * ss) * 255);
                }
            }
            return outp;
        }

        private static IEnumerable<Vec3> Corners(Vec3 a, Vec3 b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vec3((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
        }
    }
}
