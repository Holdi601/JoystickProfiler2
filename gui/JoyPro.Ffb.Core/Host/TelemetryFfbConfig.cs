using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JoyPro.Ffb.Core.Graph;

namespace JoyPro.Ffb.Core.Host;

/// <summary>Where telemetry force feedback keeps its files: &lt;data&gt;\Ffb\Telemetry.</summary>
/// <remarks>&lt;data&gt; is JOYPRO_DATA_DIR, else %LOCALAPPDATA%\JoyPro2 (the engine's data folder, see
/// engine/GameBackups.cs). The engine CLI (engine ffb telemetry ...) edits the same config file.</remarks>
public static class TelemetryFfbPaths
{
    public const string DataFolderVariable = "JOYPRO_DATA_DIR";
    public const string ConfigFileName = "telemetry-ffb.json";
    public const string StatusFileName = "status.json";
    public const string GraphExtension = ".jpffb.json";

    public static string DataRoot()
    {
        return JoyPro.Logging.JoyProDataFolder.Root();
    }

    public static string Folder() => Path.Combine(DataRoot(), "Ffb", "Telemetry");
    public static string ConfigFile() => Path.Combine(Folder(), ConfigFileName);
    public static string StatusFile() => Path.Combine(Folder(), StatusFileName);
    public static string GraphFolder() => Path.Combine(Folder(), "Graphs");

    /// <summary>A graph reference of the config: a file name in <see cref="GraphFolder"/> or a full path.</summary>
    public static string ResolveGraph(string reference) =>
        Path.IsPathRooted(reference) ? reference : Path.Combine(GraphFolder(), reference);
}

/// <summary>The games telemetry force feedback supports, with their process names and default ports.</summary>
public sealed record TelemetryGame(string Id, string Title, IReadOnlyList<string> ProcessNames, int DefaultPort, string Transport)
{
    public const string Dcs = "DCS";
    public const string Il2 = "IL2Game";
    public const string Il2Korea = "IL2Korea";
    public const string WarThunder = "WarThunder";

    /// <summary>JoyPro's IL-2 telemetry port (the game sends to it through an extra addrN line in startup.cfg).</summary>
    public const int Il2Port = 41994;

    public static IReadOnlyList<TelemetryGame> All { get; } =
    [
        new(Dcs, "DCS World", ["DCS", "DCS_mt"], Telemetry.Dcs.DcsTelemetryProtocol.Port, "UDP"),
        new(Il2, "IL-2 Great Battles", ["Il-2"], Il2Port, "UDP"),
        new(Il2Korea, "IL-2 Series: Korea", ["IL2Series"], Il2Port, "UDP"),
        new(WarThunder, "War Thunder", ["aces", "aces_be"], 8111, "HTTP")
    ];

    public static TelemetryGame? Find(string id)
    {
        var key = (id ?? "").Trim();
        if (key.Equals("il2", StringComparison.OrdinalIgnoreCase) || key.Equals("il-2", StringComparison.OrdinalIgnoreCase))
            key = Il2;
        else if (key.Equals("wt", StringComparison.OrdinalIgnoreCase))
            key = WarThunder;
        return All.FirstOrDefault(g => g.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class EffectSetting
{
    public bool Enabled { get; set; } = true;
    public double Strength { get; set; } = 1;
}

/// <summary>Settings of one force feedback device (the direction test writes them).</summary>
public sealed class DeviceFfbSettings
{
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
}

public sealed class AircraftFfbSettings
{
    /// <summary>A graph file for this aircraft ("" = the game's graph).</summary>
    public string Graph { get; set; } = "";
    public double Strength { get; set; } = 1;
    public Dictionary<string, EffectSetting> Effects { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GameFfbSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>UDP port JoyPro listens on (DCS, IL-2); 0 = the default.</summary>
    public int Port { get; set; }

    /// <summary>The graph file ("" = the built-in flight graph).</summary>
    public string Graph { get; set; } = "";
    public double Strength { get; set; } = 1;

    /// <summary>War Thunder's web server.</summary>
    public string Url { get; set; } = "http://127.0.0.1:8111/";

    /// <summary>War Thunder polls per second (1..60).</summary>
    public double PollHz { get; set; } = 20;

    public Dictionary<string, EffectSetting> Effects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AircraftFfbSettings> Aircraft { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The user's telemetry force feedback settings (telemetry-ffb.json).</summary>
/// <remarks>
/// Format (version 1), written by the engine CLI and read here; unknown fields are ignored, missing ones get the
/// defaults below:
/// <code>
/// { "format": "joypro-ffb-telemetry-config", "version": 1, "enabled": false,
///   "device": "{instance guid}", "deviceName": "VPforce Rhino FFB Joystick",
///   "strength": 1.0, "maxForce": 0.6, "invertX": false, "invertY": false, "tickRateHz": 50, "timeoutSeconds": 0.5,
///   "centringSpring": true, "centringStiffness": 0.3,
///   "devices": { "{instance guid}": { "invertX": false, "invertY": true } },
///   "games": { "DCS": { "enabled": true, "port": 41992, "graph": "", "strength": 1.0,
///                       "effects": { "buffet": { "enabled": true, "strength": 0.8 } },
///                       "aircraft": { "F-16C_50": { "graph": "viper.jpffb.json", "strength": 1.0, "effects": {} } } } } }
/// </code>
/// </remarks>
public sealed class TelemetryFfbConfig
{
    public const string Format = "joypro-ffb-telemetry-config";
    public const int CurrentVersion = 1;
    public const double DefaultMaxForce = 0.6;

    public bool Enabled { get; set; }

    /// <summary>DirectInput instance GUID of the force feedback device ("" = the first one found).</summary>
    public string Device { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public double Strength { get; set; } = 1;

    /// <summary>The hard force cap per axis, 0..1 (default 60 %).</summary>
    public double MaxForce { get; set; } = DefaultMaxForce;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }

    /// <summary>JoyPro provides the centring spring while it owns the device (graph springs, a plain spring on axes
    /// without one, springs held during telemetry pauses). Off: JoyPro sends no springs; the device's own centring acts.</summary>
    public bool CentringSpring { get; set; } = true;

    /// <summary>Stiffness of JoyPro's plain centring spring, 0..1.</summary>
    public double CentringStiffness { get; set; } = DefaultCentringStiffness;

    public const double DefaultCentringStiffness = 0.3;

    /// <summary>Per force feedback device (instance guid "{...}"): the direction found by the direction test. A device
    /// without an entry uses <see cref="InvertX"/>/<see cref="InvertY"/>.</summary>
    public Dictionary<string, DeviceFfbSettings> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The force inverts for a device (its own entry, else the global ones).</summary>
    public (bool X, bool Y) InvertFor(string? device)
    {
        var key = NormaliseDeviceKey(device);
        return key.Length > 0 && Devices.TryGetValue(key, out var settings) ? (settings.InvertX, settings.InvertY) : (InvertX, InvertY);
    }

    /// <summary>"{GUID}" in upper case (the form JoyPro.Ffb devices prints), or "" when it is not a guid.</summary>
    public static string NormaliseDeviceKey(string? device) =>
        Guid.TryParse((device ?? "").Trim().Trim('{', '}'), out var guid) ? "{" + guid.ToString().ToUpperInvariant() + "}" : "";

    /// <summary>The runtime's centring for these settings.</summary>
    public JoyPro.Ffb.Core.Runtime.FfbCentring Centring() => CentringSpring
        ? new JoyPro.Ffb.Core.Runtime.FfbCentring(JoyPro.Ffb.Core.Runtime.FfbCentringMode.Ensure, Clamp01(CentringStiffness), 0.5)
        : new JoyPro.Ffb.Core.Runtime.FfbCentring(JoyPro.Ffb.Core.Runtime.FfbCentringMode.Off);
    public double TickRateHz { get; set; } = 50;
    public double TimeoutSeconds { get; set; } = 0.5;
    public Dictionary<string, GameFfbSettings> Games { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GameFfbSettings Game(string id) => Games.TryGetValue(id, out var game) ? game : new GameFfbSettings();

    public int PortFor(TelemetryGame game)
    {
        var port = Game(game.Id).Port;
        return port is > 0 and < 65536 ? port : game.DefaultPort;
    }

    /// <summary>Strength per effect name for a game and aircraft (0 = off); effects not mentioned play at 1.</summary>
    public Dictionary<string, double> EffectScales(string game, string? aircraft)
    {
        var scales = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var settings = Game(game);
        foreach (var pair in settings.Effects)
            scales[pair.Key] = pair.Value.Enabled ? Math.Max(0, pair.Value.Strength) : 0;
        if (aircraft is not null && settings.Aircraft.TryGetValue(aircraft, out var plane))
        {
            foreach (var pair in plane.Effects)
                scales[pair.Key] = pair.Value.Enabled ? Math.Max(0, pair.Value.Strength) : 0;
        }

        return scales;
    }

    /// <summary>The overall gain: global strength x game strength x aircraft strength.</summary>
    public double GainFor(string game, string? aircraft)
    {
        var settings = Game(game);
        var gain = Clamp01(Strength) * Math.Max(0, settings.Strength);
        if (aircraft is not null && settings.Aircraft.TryGetValue(aircraft, out var plane))
            gain *= Math.Max(0, plane.Strength);
        return Math.Clamp(gain, 0, 2);
    }

    /// <summary>The graph reference for a game and aircraft ("" = built-in).</summary>
    public string GraphFor(string game, string? aircraft)
    {
        var settings = Game(game);
        if (aircraft is not null && settings.Aircraft.TryGetValue(aircraft, out var plane) && plane.Graph.Length > 0)
            return plane.Graph;
        return settings.Graph;
    }

    public static TelemetryFfbConfig Load(string path)
    {
        if (!File.Exists(path))
            return new TelemetryFfbConfig();
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Reads the config; a broken file gives the defaults (force feedback off).</summary>
    public static TelemetryFfbConfig Parse(string json)
    {
        var config = new TelemetryFfbConfig();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (JsonException)
        {
            return config;
        }

        if (root is null)
            return config;
        config.Enabled = Bool(root, "enabled", false);
        config.Device = Str(root, "device", "");
        config.DeviceName = Str(root, "deviceName", "");
        config.Strength = Clamp01(Num(root, "strength", 1));
        config.MaxForce = Clamp01(Num(root, "maxForce", DefaultMaxForce));
        config.InvertX = Bool(root, "invertX", false);
        config.InvertY = Bool(root, "invertY", false);
        config.CentringSpring = Bool(root, "centringSpring", true);
        config.CentringStiffness = Clamp01(Num(root, "centringStiffness", DefaultCentringStiffness));
        if (Get(root, "devices") is JsonObject devices)
        {
            foreach (var pair in devices)
            {
                var key = NormaliseDeviceKey(pair.Key);
                if (key.Length > 0 && pair.Value is JsonObject d)
                    config.Devices[key] = new DeviceFfbSettings { InvertX = Bool(d, "invertX", false), InvertY = Bool(d, "invertY", false) };
            }
        }
        config.TickRateHz = Math.Clamp(Num(root, "tickRateHz", 50), 10, 200);
        config.TimeoutSeconds = Math.Clamp(Num(root, "timeoutSeconds", 0.5), 0.1, 5);
        if (Get(root, "games") is JsonObject games)
        {
            foreach (var pair in games)
            {
                if (pair.Value is not JsonObject g)
                    continue;
                var game = new GameFfbSettings
                {
                    Enabled = Bool(g, "enabled", true),
                    Port = (int)Math.Clamp(Num(g, "port", 0), 0, 65535),
                    Graph = Str(g, "graph", ""),
                    Strength = Math.Clamp(Num(g, "strength", 1), 0, 2),
                    Url = Str(g, "url", "http://127.0.0.1:8111/"),
                    PollHz = Math.Clamp(Num(g, "pollHz", 20), 1, 60)
                };
                ReadEffects(Get(g, "effects") as JsonObject, game.Effects);
                if (Get(g, "aircraft") is JsonObject aircraft)
                {
                    foreach (var plane in aircraft)
                    {
                        if (plane.Value is not JsonObject p)
                            continue;
                        var settings = new AircraftFfbSettings { Graph = Str(p, "graph", ""), Strength = Math.Clamp(Num(p, "strength", 1), 0, 2) };
                        ReadEffects(Get(p, "effects") as JsonObject, settings.Effects);
                        game.Aircraft[plane.Key] = settings;
                    }
                }

                config.Games[pair.Key] = game;
            }
        }

        return config;
    }

    public string ToJson()
    {
        var games = new JsonObject();
        foreach (var pair in Games.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var g = pair.Value;
            var aircraft = new JsonObject();
            foreach (var plane in g.Aircraft.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                aircraft[plane.Key] = new JsonObject { ["graph"] = plane.Value.Graph, ["strength"] = plane.Value.Strength, ["effects"] = WriteEffects(plane.Value.Effects) };
            games[pair.Key] = new JsonObject
            {
                ["enabled"] = g.Enabled,
                ["port"] = g.Port,
                ["graph"] = g.Graph,
                ["strength"] = g.Strength,
                ["url"] = g.Url,
                ["pollHz"] = g.PollHz,
                ["effects"] = WriteEffects(g.Effects),
                ["aircraft"] = aircraft
            };
        }

        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = CurrentVersion,
            ["enabled"] = Enabled,
            ["device"] = Device,
            ["deviceName"] = DeviceName,
            ["strength"] = Strength,
            ["maxForce"] = MaxForce,
            ["invertX"] = InvertX,
            ["invertY"] = InvertY,
            ["centringSpring"] = CentringSpring,
            ["centringStiffness"] = CentringStiffness,
            ["devices"] = WriteDevices(),
            ["tickRateHz"] = TickRateHz,
            ["timeoutSeconds"] = TimeoutSeconds,
            ["games"] = games
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }

    private JsonObject WriteDevices()
    {
        var json = new JsonObject();
        foreach (var pair in Devices.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            json[pair.Key] = new JsonObject { ["invertX"] = pair.Value.InvertX, ["invertY"] = pair.Value.InvertY };
        return json;
    }

    private static JsonObject WriteEffects(Dictionary<string, EffectSetting> effects)
    {
        var json = new JsonObject();
        foreach (var pair in effects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            json[pair.Key] = new JsonObject { ["enabled"] = pair.Value.Enabled, ["strength"] = pair.Value.Strength };
        return json;
    }

    private static void ReadEffects(JsonObject? json, Dictionary<string, EffectSetting> into)
    {
        if (json is null)
            return;
        foreach (var pair in json)
        {
            if (pair.Value is JsonObject e)
                into[pair.Key] = new EffectSetting { Enabled = Bool(e, "enabled", true), Strength = Math.Clamp(Num(e, "strength", 1), 0, 2) };
        }
    }

    // Case-insensitive field access: the engine (JavaScriptSerializer) may write PascalCase.
    private static JsonNode? Get(JsonObject json, string name)
    {
        if (json.TryGetPropertyValue(name, out var exact))
            return exact;
        foreach (var pair in json)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static double Num(JsonObject json, string name, double fallback)
    {
        var node = Get(json, name) as JsonValue;
        if (node is null)
            return fallback;
        if (node.TryGetValue<double>(out var number) && double.IsFinite(number))
            return number;
        if (node.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
            return number;
        return fallback;
    }

    private static bool Bool(JsonObject json, string name, bool fallback)
    {
        var node = Get(json, name) as JsonValue;
        if (node is null)
            return fallback;
        if (node.TryGetValue<bool>(out var flag))
            return flag;
        if (node.TryGetValue<string>(out var text) && bool.TryParse(text, out flag))
            return flag;
        return fallback;
    }

    private static string Str(JsonObject json, string name, string fallback) =>
        Get(json, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

    private static double Clamp01(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
}
