using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Telemetry.WarThunder;

/// <summary>/map_info.json: the map's extent in metres and its grid.</summary>
/// <remarks>Example: <c>{"grid_steps":[8192,8192],"grid_zero":[-32768,32768],"map_generation":5,"hud_type":0,
/// "map_max":[32768,32768],"map_min":[-32768,-32768],"valid":true}</c>. grid_zero is the top left corner (grid cell
/// A1): x grows to the right (east), y grows upwards on the map picture (north). The game's own page draws the map
/// picture with map_min/map_max as its edges.</remarks>
public sealed record WarThunderMapInfo(
    double MinX, double MinY, double MaxX, double MaxY,
    double GridStepX, double GridStepY, double GridZeroX, double GridZeroY,
    int Generation, int HudType)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    /// <summary>The game names no map here: a stable id from the extent and grid ("wt_65536x65536_1a2b3c4d").</summary>
    public string Id
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            var canonical = string.Join(";", new[] { MinX, MinY, MaxX, MaxY, GridStepX, GridStepY, GridZeroX, GridZeroY }
                .Select(d => Math.Round(d, 1).ToString("0.#", ci)));
            uint hash = 2166136261;
            foreach (var c in canonical)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return string.Create(ci, $"wt_{Width:0}x{Height:0}_{hash:x8}");
        }
    }

    /// <summary>Map picture fractions (x right, y down, 0..1) to metres: u = east from the map's x origin,
    /// v = north (the top edge of the picture is map_max y).</summary>
    public (double U, double V) ToMetres(double x01, double y01) => (MinX + x01 * Width, MaxY - y01 * Height);

    /// <summary>Null when the text is not a map info or says "valid": false (hangar, menus).</summary>
    public static WarThunderMapInfo? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            if (JsonNode.Parse(json.ToArray()) is not JsonObject o)
                return null;
            if (o["valid"] is JsonValue valid && valid.TryGetValue<bool>(out var isValid) && !isValid)
                return null;
            if (!TryPair(o["map_min"], out var minX, out var minY) || !TryPair(o["map_max"], out var maxX, out var maxY)
                || maxX <= minX || maxY <= minY)
                return null;
            TryPair(o["grid_steps"], out var stepX, out var stepY);
            if (!TryPair(o["grid_zero"], out var zeroX, out var zeroY))
            {
                zeroX = minX;
                zeroY = maxY;
            }

            return new WarThunderMapInfo(minX, minY, maxX, maxY, stepX, stepY, zeroX, zeroY,
                (int)Number(o["map_generation"], -1), (int)Number(o["hud_type"], 0));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryPair(JsonNode? node, out double a, out double b)
    {
        a = b = 0;
        if (node is not JsonArray { Count: >= 2 } array)
            return false;
        a = Number(array[0], double.NaN);
        b = Number(array[1], double.NaN);
        return double.IsFinite(a) && double.IsFinite(b);
    }

    internal static double Number(JsonNode? node, double fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d) && double.IsFinite(d))
                return d;
            if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d))
                return d;
        }

        return fallback;
    }
}

/// <summary>One entry of /map_obj.json. Positions are map picture fractions (x right, y down, 0..1).</summary>
/// <remarks>Fields of the game: "type" (aircraft, ground_model, airfield, capture_zone, bombing_point,
/// defending_point, respawn_base_tank/fighter/bomber, waypoint), "icon" (Player, Fighter, Assault, Bomber, Heavytank,
/// Mediumtank, Lighttank, TankDestroyer, SPAA, Airdefence, Structure, capture_zone, ...), "color" ("#rrggbb", the
/// team colour as the player sees it), "color[]", "blink", "x"/"y", "dx"/"dy" (direction, picture axes), and for an
/// airfield "sx"/"sy"/"ex"/"ey" (runway ends; here X/Y = its middle, Dx/Dy = the runway direction).</remarks>
public sealed record WarThunderMapObject(string Type, string Icon, string Color, double X, double Y, double? Dx, double? Dy)
{
    public bool IsPlayer => Icon == "Player";

    /// <summary>Heading in degrees clockwise from north (the picture's up), null without a direction.</summary>
    public double? Heading => Dx is { } dx && Dy is { } dy && (dx != 0 || dy != 0) ? WarThunderMap.HeadingDeg(dx, dy) : null;
}

public static class WarThunderMap
{
    /// <summary>Direction on the map picture (x right, y down) to degrees clockwise from north (as the game's page
    /// turns its icons: atan2(dx, -dy)).</summary>
    public static double HeadingDeg(double dx, double dy)
    {
        var h = Math.Atan2(dx, -dy) * TelemetryNav.RadToDeg;
        return h < 0 ? h + 360 : h;
    }

    /// <returns>Null when the answer is not a JSON array (a broken answer must not read as "everything is gone").</returns>
    public static List<WarThunderMapObject>? ParseObjects(ReadOnlySpan<byte> json)
    {
        var list = new List<WarThunderMapObject>();
        try
        {
            if (JsonNode.Parse(json.ToArray()) is not JsonArray array)
                return null;
            foreach (var node in array)
            {
                if (node is not JsonObject o)
                    continue;
                var type = Text(o["type"]);
                var icon = Text(o["icon"]);
                var color = Text(o["color"]);
                if (type == "airfield" || (o.ContainsKey("sx") && !o.ContainsKey("x")))
                {
                    var sx = WarThunderMapInfo.Number(o["sx"], double.NaN);
                    var sy = WarThunderMapInfo.Number(o["sy"], double.NaN);
                    var ex = WarThunderMapInfo.Number(o["ex"], double.NaN);
                    var ey = WarThunderMapInfo.Number(o["ey"], double.NaN);
                    if (!double.IsFinite(sx + sy + ex + ey))
                        continue;
                    list.Add(new WarThunderMapObject(type, icon, color, (sx + ex) / 2, (sy + ey) / 2, ex - sx, ey - sy));
                    continue;
                }

                var x = WarThunderMapInfo.Number(o["x"], double.NaN);
                var y = WarThunderMapInfo.Number(o["y"], double.NaN);
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    continue;
                var dx = WarThunderMapInfo.Number(o["dx"], double.NaN);
                var dy = WarThunderMapInfo.Number(o["dy"], double.NaN);
                list.Add(new WarThunderMapObject(type, icon, color, x, y,
                    double.IsFinite(dx) ? dx : null, double.IsFinite(dy) ? dy : null));
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return list;
    }

    /// <summary>The map object's kind in Tacview words.</summary>
    public static string TacviewType(string type, string icon)
    {
        var i = icon.ToLowerInvariant();
        switch (type)
        {
            case "aircraft":
                return i.Contains("heli", StringComparison.Ordinal) ? "Air+Rotorcraft" : "Air+FixedWing";
            case "ground_model":
                if (i.Contains("spaa", StringComparison.Ordinal) || i.Contains("airdefence", StringComparison.Ordinal))
                    return "Ground+AntiAircraft";
                if (i.Contains("tank", StringComparison.Ordinal))
                    return "Ground+Heavy+Armor+Vehicle+Tank";
                if (i.Contains("ship", StringComparison.Ordinal) || i.Contains("boat", StringComparison.Ordinal)
                    || i.Contains("destroyer", StringComparison.Ordinal) || i.Contains("cruiser", StringComparison.Ordinal))
                    return "Sea+Watercraft";
                if (i.Contains("structure", StringComparison.Ordinal))
                    return "Ground+Static+Building";
                return "Ground+Vehicle";
            case "airfield":
                return "Ground+Static+Aerodrome";
            case "waypoint":
                return "Navaid+Static+Waypoint";
            default:
                // capture_zone, bombing_point, defending_point, respawn_base_*: places on the map
                return "Navaid+Static";
        }
    }

    /// <summary>The coalition from the map colour: the player's own colour or a blue/green one = "Allies", a red one =
    /// "Enemies", grey/white (neutral zones) = "Neutral". The defaults of the game: player yellow #faC81E, squad green,
    /// allies blue #174DFF, enemies red #f40C00 (colour-blind palettes change them; unknown colours = "Neutral").</summary>
    public static string Coalition(string? color, string? playerColor)
    {
        if (!string.IsNullOrEmpty(color) && string.Equals(color, playerColor, StringComparison.OrdinalIgnoreCase))
            return "Allies";
        if (!TryRgb(color, out var r, out var g, out var b))
            return "Neutral";
        if (r - Math.Max(g, b) > 80)
            return "Enemies";
        if (Math.Max(g, b) - r > 40)
            return "Allies";
        return "Neutral";
    }

    private static bool TryRgb(string? color, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (color is not { Length: 7 } || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        r = (rgb >> 16) & 0xFF;
        g = (rgb >> 8) & 0xFF;
        b = rgb & 0xFF;
        return true;
    }

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>Gives map objects (which have no ids in the game) stable synthetic ids between polls.</summary>
/// <remarks>
/// Heuristic: objects are matched to the tracks of the previous polls with the same "type|icon" (the colour may change:
/// a captured zone), by the smallest distance to the track's predicted position (last position + last velocity × time
/// since), greedily nearest first, within a gate of the kind's top speed × elapsed time + a margin (aircraft 700 m/s +
/// 150 m, ground and sea 40 m/s + 60 m, places 0 + 60 m; the elapsed time counts at least 0.5 s). Unmatched objects get
/// a new id (hex digits, from 10001); a track that is not seen for <see cref="GraceSeconds"/> is forgotten, so an
/// object that comes back later (a ground unit spotted again) may get a new id. Two aircraft of one type that cross
/// within a few hundred metres can swap ids.
/// </remarks>
public sealed class WarThunderObjectTracker
{
    public const double GraceSeconds = 3;

    private sealed class Track
    {
        public required string Id;
        public required string Kind;
        public double U, V, Vu, Vv, Time;
        public bool HasVelocity;
    }

    private readonly List<Track> _tracks = [];
    private long _next = 0x10001;

    public int Count => _tracks.Count;

    public void Reset() => _tracks.Clear();

    /// <summary>Matches one poll's objects (with positions in metres) and returns each object's id in input order.</summary>
    public string[] Update(double time, IReadOnlyList<(WarThunderMapObject Obj, double U, double V)> objects)
    {
        var ids = new string?[objects.Count];
        var pairs = new List<(double Distance, int Object, int Track)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            var track = _tracks[t];
            var dt = Math.Max(0, time - track.Time);
            var pu = track.U + (track.HasVelocity ? track.Vu * dt : 0);
            var pv = track.V + (track.HasVelocity ? track.Vv * dt : 0);
            var (speed, margin) = Gate(track.Kind);
            var gate = speed * Math.Max(dt, 0.5) + margin;
            for (var o = 0; o < objects.Count; o++)
            {
                if (Kind(objects[o].Obj) != track.Kind)
                    continue;
                var d = Math.Sqrt(Square(objects[o].U - pu) + Square(objects[o].V - pv));
                if (d <= gate)
                    pairs.Add((d, o, t));
            }
        }

        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var usedTracks = new bool[_tracks.Count];
        foreach (var (_, o, t) in pairs)
        {
            if (ids[o] is not null || usedTracks[t])
                continue;
            usedTracks[t] = true;
            var track = _tracks[t];
            var dt = time - track.Time;
            if (dt > 0.05)
            {
                track.Vu = (objects[o].U - track.U) / dt;
                track.Vv = (objects[o].V - track.V) / dt;
                track.HasVelocity = true;
            }

            track.U = objects[o].U;
            track.V = objects[o].V;
            track.Time = time;
            ids[o] = track.Id;
        }

        for (var t = _tracks.Count - 1; t >= 0; t--)
        {
            if (!usedTracks[t] && time - _tracks[t].Time > GraceSeconds)
                _tracks.RemoveAt(t);
        }

        for (var o = 0; o < objects.Count; o++)
        {
            if (ids[o] is not null)
                continue;
            var id = (_next++).ToString("x", CultureInfo.InvariantCulture);
            _tracks.Add(new Track { Id = id, Kind = Kind(objects[o].Obj), U = objects[o].U, V = objects[o].V, Time = time });
            ids[o] = id;
        }

        return ids!;
    }

    private static string Kind(WarThunderMapObject obj) => obj.Type + "|" + obj.Icon;

    private static (double Speed, double Margin) Gate(string kind)
    {
        if (kind.StartsWith("aircraft|", StringComparison.Ordinal))
            return (700, 150);
        if (kind.StartsWith("ground_model|", StringComparison.Ordinal))
            return (40, 60);
        return (0, 60);
    }

    private static double Square(double x) => x * x;
}

/// <summary>/hudmsg answers: {"events":[...],"damage":[...]}, each entry {"id","msg","sender","enemy","mode","time"}.</summary>
public static class WarThunderHudMessages
{
    public sealed record Entry(long Id, string Text, double? Time);

    public static bool TryParse(ReadOnlySpan<byte> json, out List<Entry> events, out List<Entry> damage)
    {
        events = [];
        damage = [];
        try
        {
            if (JsonNode.Parse(json.ToArray()) is not JsonObject o)
                return false;
            Read(o["events"], events);
            Read(o["damage"], damage);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Read(JsonNode? node, List<Entry> into)
    {
        if (node is not JsonArray array)
            return;
        foreach (var item in array.OfType<JsonObject>())
        {
            var id = WarThunderMapInfo.Number(item["id"], double.NaN);
            var text = item["msg"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : "";
            if (!double.IsFinite(id) || string.IsNullOrWhiteSpace(text))
                continue;
            var time = WarThunderMapInfo.Number(item["time"], double.NaN);
            into.Add(new Entry((long)id, text.Trim(), double.IsFinite(time) ? time : null));
        }

        into.Sort((a, b) => a.Id.CompareTo(b.Id));
    }
}

/// <summary>Decides the sign of a cockpit gauge (artificial horizon) from the flight physics: the gauges' sign
/// convention is not documented, so roll is compared with the turn direction and pitch with flight path + AoA.</summary>
/// <remarks>Each clear sample votes "agrees" (gauge sign = physical sign) or not; from 10 votes on, a 3:1 majority
/// decides; until then <see cref="DefaultSign"/> holds. Votes stop at 200 (the decision is kept).</remarks>
public sealed class WarThunderSignVote(double defaultSign)
{
    private int _agree;
    private int _disagree;

    public double DefaultSign { get; } = defaultSign;

    public int Votes => _agree + _disagree;

    public double Sign
    {
        get
        {
            if (Votes >= 10)
            {
                if (_agree >= 3 * _disagree)
                    return 1;
                if (_disagree >= 3 * _agree)
                    return -1;
            }

            return DefaultSign;
        }
    }

    public void Add(bool agrees)
    {
        if (Votes >= 200)
            return;
        if (agrees)
            _agree++;
        else
            _disagree++;
    }
}

internal static class WarThunderJson
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The answer as compact JSON (whitespace removed), null when it is not JSON.</summary>
    public static string? Compact(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonNode.Parse(json.ToArray())?.ToJsonString(CompactOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
