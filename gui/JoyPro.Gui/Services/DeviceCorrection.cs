using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.Services;

// A user's correction of JoyPro's device data (docs/device-reference.md, "Correcting JoyPro"): the parts the user placed
// or moved on one picture of a model and the controls he added or renamed, as a small file ("joypro-device-correction")
// that the maintainer merges with tools/device_corrections.py. No personal data: the model, its USB id, the picture's
// kind and content key, positions (0..1), input ids, control ids and names.
public static class DeviceCorrection
{
    public const string FormatName = "joypro-device-correction";
    public const string IssueBase = "https://github.com/Holdi601/JoystickProfiler2/issues/new";
    // GitHub takes about 8000 characters in an address; a bigger correction goes as an attached file.
    public const int MaxUrlLength = 7500;

    public sealed class Part
    {
        public string Id { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
    }

    // positions: the shown picture's parts with a position the user placed; shipped: JoyPro's positions on that picture
    // (a part placed where JoyPro has it already is no correction).
    public static JsonObject Build(RefDevice shippedModel, RefDevice current, string vidPid, string pictureKind, string pictureKey, string view,
                                   IEnumerable<Part> placed, IReadOnlyDictionary<string, double[]> shipped, string note = "")
    {
        JsonObject positions = new();
        JsonArray removed = [];
        foreach (Part part in placed.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (part.X < 0 || part.Y < 0)
            {
                // Taken off the picture by the user: JoyPro's position of it is wrong.
                removed.Add(part.Id);
                continue;
            }
            if (shipped.TryGetValue(part.Id, out double[]? xy) && Math.Abs(xy[0] - part.X) < 0.004 && Math.Abs(xy[1] - part.Y) < 0.004)
                continue;
            positions[part.Id] = new JsonArray(Math.Round(part.X, 4), Math.Round(part.Y, 4));
        }
        JsonArray controls = [];
        Dictionary<string, RefControl> before = shippedModel.AllControls.Where(c => c.Input.Length > 0)
            .GroupBy(c => c.Input, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (RefControl c in current.AllControls.Where(c => c.Input.Length > 0))
        {
            if (before.TryGetValue(c.Input, out RefControl? old) && string.Equals(old.Control, c.Control, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(old.Label, c.Label, StringComparison.Ordinal))
                continue;
            JsonObject item = new() { ["input"] = c.Input, ["control"] = c.Control };
            if (c.Label.Length > 0)
                item["label"] = c.Label;
            if (c.Type.Length > 0)
                item["type"] = c.Type;
            item["change"] = old == null ? "added" : !string.Equals(old.Control, c.Control, StringComparison.OrdinalIgnoreCase) ? "control" : "label";
            controls.Add(item);
        }
        JsonObject root = new()
        {
            ["format"] = FormatName,
            ["version"] = 1,
            ["model"] = shippedModel.Id,
            ["modelName"] = shippedModel.Name,
            ["vidPid"] = vidPid ?? string.Empty,
            ["picture"] = new JsonObject { ["kind"] = pictureKind, ["key"] = pictureKey, ["view"] = view },
            ["positions"] = positions,
            ["removed"] = removed,
            ["controls"] = controls,
            ["app"] = "JoyPro " + typeof(DeviceCorrection).Assembly.GetName().Version?.ToString(3)
        };
        if (!string.IsNullOrWhiteSpace(note))
            root["note"] = note.Trim();
        return root;
    }

    public static bool IsEmpty(JsonObject correction) =>
        (correction["positions"] as JsonObject)?.Count == 0 && (correction["controls"] as JsonArray)?.Count == 0 && ((correction["removed"] as JsonArray)?.Count ?? 0) == 0;

    public static string ToJson(JsonObject correction) => correction.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    // <data>\DeviceReference\Corrections\<model>-<yyyyMMdd-HHmmss>.json
    public static string Save(JsonObject correction, string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, DeviceVocabulary.Slug((string?)correction["model"] ?? "device") + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(file, ToJson(correction), new UTF8Encoding(false));
        return file;
    }

    public static JsonObject Read(string file)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new FormatException("not a JSON object");
        if (!string.Equals((string?)root["format"], FormatName, StringComparison.Ordinal))
            throw new FormatException("not a JoyPro device correction");
        return root;
    }

    // The prefilled issue form ("Device layout correction"): the title and the correction itself when it fits into the
    // address, else a note to attach the saved file. The user submits it (or not) in the browser.
    public static string IssueUrl(JsonObject correction, out bool attached)
    {
        string title = "Device layout correction: " + ((string?)correction["modelName"] is { Length: > 0 } n ? n : (string?)correction["model"]);
        // The form renders the field as JSON (render: json): the plain JSON goes in.
        string body = correction.ToJsonString();
        string url = IssueBase + "?template=device-layout-correction.yml&labels=device-correction&title=" + Uri.EscapeDataString(title) +
                     "&model=" + Uri.EscapeDataString((string?)correction["model"] ?? string.Empty) + "&correction=" + Uri.EscapeDataString(body);
        attached = url.Length > MaxUrlLength;
        if (attached)
            url = IssueBase + "?template=device-layout-correction.yml&labels=device-correction&title=" + Uri.EscapeDataString(title) +
                  "&model=" + Uri.EscapeDataString((string?)correction["model"] ?? string.Empty) +
                  "&correction=" + Uri.EscapeDataString("(the correction file is attached)");
        return url;
    }
}
