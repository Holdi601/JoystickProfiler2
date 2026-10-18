using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JoyPro.TelemetrySessions;

namespace JoyPro.Ffb.Core.Telemetry.Recording;

/// <summary>The user's telemetry recording settings (&lt;data&gt;\Telemetry\telemetry-recording.json).</summary>
/// <remarks>
/// Off by default. Written by the GUI (Settings > Telemetry recording) and by <c>engine telemetry config set</c>; read by
/// JoyPro.Ffb when it starts and on "reload". Unknown fields are ignored, missing ones get the defaults:
/// <code>
/// { "format": "joypro-telemetry-recording-config", "version": 1, "enabled": false, "folder": "",
///   "maxTotalMb": 4096, "keepDays": 0, "sampleHz": 20, "keepRaw": true, "sessionTimeoutSeconds": 60,
///   "games": { "DCS": { "enabled": true } } }
/// </code>
/// A game without an entry is recorded when the global switch is on.
/// </remarks>
public sealed class TelemetryRecordingConfig
{
    public const string Format = "joypro-telemetry-recording-config";
    public const int CurrentVersion = 1;
    public const double DefaultSampleHz = 20;
    public const int DefaultMaxTotalMb = 4096;
    public const double DefaultSessionTimeoutSeconds = 60;

    /// <summary>The sample rates offered (0 = every frame the game sends).</summary>
    public static IReadOnlyList<double> SampleRates { get; } = [0, 100, 50, 20, 10, 5, 1];

    public bool Enabled { get; set; }

    /// <summary>Where sessions go ("" = &lt;data&gt;\Telemetry\Recordings).</summary>
    public string Folder { get; set; } = "";

    /// <summary>Size cap of the folder in MB (0 = none): the oldest sessions are deleted first.</summary>
    public int MaxTotalMb { get; set; } = DefaultMaxTotalMb;

    /// <summary>Sessions older than this many days are deleted (0 = keep them).</summary>
    public int KeepDays { get; set; }

    /// <summary>Frames per second written (0 = every frame the game sends).</summary>
    public double SampleHz { get; set; } = DefaultSampleHz;

    /// <summary>Keep the raw input too (datagrams, HTTP answers, shared memory snapshots, journal lines).</summary>
    public bool KeepRaw { get; set; } = true;

    /// <summary>A session ends after this long without telemetry (a pause longer than this starts a new session).</summary>
    public double SessionTimeoutSeconds { get; set; } = DefaultSessionTimeoutSeconds;

    /// <summary>Per game: recorded or not (missing = recorded).</summary>
    public Dictionary<string, bool> Games { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool GameEnabled(string gameId) => !Games.TryGetValue(gameId, out var on) || on;

    /// <summary>Recording is on for this game (global switch and the game's switch).</summary>
    public bool Records(string gameId) => Enabled && GameEnabled(gameId);

    public string ResolvedFolder() => TelemetrySessionStore.ResolveFolder(Folder);

    public long MaxTotalBytes => MaxTotalMb <= 0 ? 0 : MaxTotalMb * 1024L * 1024L;

    public static string DefaultPath() => TelemetrySessionStore.ConfigPath();

    public static TelemetryRecordingConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
            return new TelemetryRecordingConfig();
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new TelemetryRecordingConfig();
        }
    }

    public static TelemetryRecordingConfig Parse(string json)
    {
        var config = new TelemetryRecordingConfig();
        if (JsonNode.Parse(json) is not JsonObject root)
            return config;
        config.Enabled = Bool(root["enabled"], false);
        config.Folder = root["folder"] is JsonValue folder && folder.TryGetValue<string>(out var text) ? text : "";
        config.MaxTotalMb = (int)Math.Clamp(Num(root["maxTotalMb"], DefaultMaxTotalMb), 0, 10_000_000);
        config.KeepDays = (int)Math.Clamp(Num(root["keepDays"], 0), 0, 100_000);
        config.SampleHz = Math.Clamp(Num(root["sampleHz"], DefaultSampleHz), 0, 1000);
        config.KeepRaw = Bool(root["keepRaw"], true);
        config.SessionTimeoutSeconds = Math.Clamp(Num(root["sessionTimeoutSeconds"], DefaultSessionTimeoutSeconds), 5, 3600);
        if (root["games"] is JsonObject games)
        {
            foreach (var pair in games)
            {
                if (pair.Value is JsonObject game)
                    config.Games[pair.Key] = Bool(game["enabled"], true);
                else if (pair.Value is JsonValue value && value.TryGetValue<bool>(out var on))
                    config.Games[pair.Key] = on;
            }
        }

        return config;
    }

    public string ToJson()
    {
        var games = new JsonObject();
        foreach (var pair in Games.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            games[pair.Key] = new JsonObject { ["enabled"] = pair.Value };
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["enabled"] = Enabled,
            ["folder"] = Folder,
            ["maxTotalMb"] = MaxTotalMb,
            ["keepDays"] = KeepDays,
            ["sampleHz"] = SampleHz,
            ["keepRaw"] = KeepRaw,
            ["sessionTimeoutSeconds"] = SessionTimeoutSeconds,
            ["games"] = games
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Writes the settings atomically (temp file + replace).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson() + Environment.NewLine, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public TelemetryRecordingConfig Clone() => Parse(ToJson());

    private static bool Bool(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : fallback;

    private static double Num(JsonNode? node, double fallback)
    {
        if (node is not JsonValue value)
            return fallback;
        if (value.TryGetValue<double>(out var d) && double.IsFinite(d))
            return d;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return d;
        return fallback;
    }
}
