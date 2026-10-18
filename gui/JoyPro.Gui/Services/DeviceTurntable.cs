using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace JoyPro.Gui.Services;

// The rotatable 3D view of a device (docs/device-reference.md, "3D view"): pre-rendered frames around the vertical at a
// few pitches (<data>\DeviceReference\Turntables\<model>\frames, a downloaded pack, made by tools/device_drawings.py
// turntable from a 3D model of the device) and where each part of the device lands on every frame and whether it faces the viewer.
public sealed class DeviceTurntable
{
    public sealed class Frame
    {
        public double Pitch { get; init; }
        public double Yaw { get; init; }
        public string File { get; init; } = string.Empty;
        // Part id -> (x, y 0..1 of the frame, seen).
        public Dictionary<string, (double X, double Y, bool Seen)> Points { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public string Model { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int YawSteps { get; init; }
    public List<double> Pitches { get; init; } = [];
    public List<Frame> Frames { get; init; } = [];

    public Frame? FrameAt(int pitchIndex, int yawIndex)
    {
        if (YawSteps <= 0 || Pitches.Count == 0)
            return null;
        int p = Math.Clamp(pitchIndex, 0, Pitches.Count - 1);
        int y = ((yawIndex % YawSteps) + YawSteps) % YawSteps;
        double pitch = Pitches[p], yaw = y * 360.0 / YawSteps;
        return Frames.FirstOrDefault(f => Math.Abs(f.Pitch - pitch) < 0.01 && Math.Abs(f.Yaw - yaw) < 0.01);
    }

    public string PathOf(Frame frame) => Path.Combine(Folder, frame.File.Replace('/', Path.DirectorySeparatorChar));

    private static readonly ConcurrentDictionary<string, DeviceTurntable?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The turntable of a model downloaded to this PC (DeviceTurntablePacks: <data>\DeviceReference\Turntables), or null
    // (read once; an install clears the cache). The 3D views are not shipped with JoyPro.
    public static DeviceTurntable? Of(string modelId) => Of(DeviceTurntablePacks.InstalledRoot(), modelId);

    public static DeviceTurntable? Of(string root, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(root) || modelId.Contains("..", StringComparison.Ordinal) ||
            modelId.IndexOfAny(['\\', '/', ':']) >= 0)
            return null;
        string folder = Path.Combine(root, modelId);
        if (Cache.TryGetValue(folder, out DeviceTurntable? known) && known != null)
            return known;
        // Not there (yet): looked for again next time, so a view downloaded meanwhile shows at once.
        DeviceTurntable? loaded = Load(folder, modelId);
        if (loaded != null)
            Cache[folder] = loaded;
        return loaded;
    }

    public static void ClearCache() => Cache.Clear();

    public static DeviceTurntable? Load(string folder, string modelId)
    {
        string file = Path.Combine(folder, "turntable.json");
        if (!System.IO.File.Exists(file))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            JsonElement root = doc.RootElement;
            List<Frame> frames = [];
            foreach (JsonElement f in root.GetProperty("frames").EnumerateArray())
            {
                Dictionary<string, (double, double, bool)> points = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty p in f.GetProperty("points").EnumerateObject())
                {
                    JsonElement[] v = p.Value.EnumerateArray().ToArray();
                    points[p.Name] = (v[0].GetDouble(), v[1].GetDouble(), v.Length > 2 && v[2].ValueKind == JsonValueKind.True);
                }
                frames.Add(new Frame { Pitch = f.GetProperty("pitch").GetDouble(), Yaw = f.GetProperty("yaw").GetDouble(), File = f.GetProperty("file").GetString() ?? string.Empty, Points = points });
            }
            return new DeviceTurntable
            {
                Model = modelId,
                Folder = folder,
                Source = root.TryGetProperty("source", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty,
                YawSteps = root.GetProperty("yawSteps").GetInt32(),
                Pitches = root.GetProperty("pitches").EnumerateArray().Select(e => e.GetDouble()).ToList(),
                Frames = frames
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
