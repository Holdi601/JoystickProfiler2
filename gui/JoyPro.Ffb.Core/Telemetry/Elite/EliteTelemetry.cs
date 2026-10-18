using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace JoyPro.Ffb.Core.Telemetry.Elite;

/// <summary>Elite Dangerous (Horizons / Odyssey): the player journal folder (Saved Games\Frontier Developments\Elite
/// Dangerous) with Status.json, Journal.*.log and the companion files.</summary>
/// <remarks>
/// Status.json is rewritten by the game whenever something on the HUD changes. Every numeric field becomes a key with
/// the prefix "ed." ("PlanetRadius" -> "ed.planet_radius", Latitude/Longitude/Altitude/Heading -> ed.lat/lon/alt/heading,
/// Fuel.FuelMain -> ed.fuel_main, Pips -> ed.pips.sys/eng/wep in half pips 0..8, Destination.System/Body ->
/// ed.dest.system/body); Flags and Flags2 stay numbers (ed.flags, ed.flags2) and every documented bit is its own 0/1
/// key (ed.flag.*, ed.flag2.*). Text fields (BodyName, GuiFocus, LegalState, SelectedWeapon, Destination name) become
/// session facts. Elite's latitude / longitude are on other planets, so the source has no <see cref="FrameTelemetrySource.NavMappings"/>.
/// </remarks>
public static class EliteTelemetryProtocol
{
    public const string Prefix = "ed.";
    public const string StatusFile = "Status.json";
    public const string JournalPattern = "Journal.*.log";

    /// <summary>Journal event texts and companion file texts are cut to this many characters.</summary>
    public const int MaxEventText = 4096;

    /// <summary>The companion files the game rewrites now and then (recorded as raw input and an "ed.file.&lt;name&gt;" event).</summary>
    public static readonly string[] CompanionFiles =
    [
        "NavRoute.json", "Cargo.json", "ModulesInfo.json", "Market.json", "Outfitting.json", "Shipyard.json",
        "Backpack.json", "ShipLocker.json", "FCMaterials.json"
    ];

    /// <summary>Status.json "Flags", bit 0..31 (Frontier's journal manual).</summary>
    public static readonly string[] FlagNames =
    [
        "docked", "landed", "gear_down", "shields_up", "supercruise", "fa_off", "hardpoints", "in_wing",
        "lights", "cargo_scoop", "silent_running", "scooping", "srv_handbrake", "srv_turret_view", "srv_turret_retracted", "srv_drive_assist",
        "mass_locked", "fsd_charging", "fsd_cooldown", "low_fuel", "overheating", "has_lat_long", "in_danger", "being_interdicted",
        "in_main_ship", "in_fighter", "in_srv", "analysis_mode", "night_vision", "alt_from_avg_radius", "fsd_jump", "srv_high_beam"
    ];

    /// <summary>Status.json "Flags2" (Odyssey), bit 0..19.</summary>
    public static readonly string[] Flag2Names =
    [
        "on_foot", "in_taxi", "in_multicrew", "on_foot_in_station", "on_foot_on_planet", "aim_down_sight", "low_oxygen", "low_health",
        "cold", "hot", "very_cold", "very_hot", "glide_mode", "on_foot_in_hangar", "on_foot_social_space", "on_foot_exterior",
        "breathable_atmosphere", "telepresence_multicrew", "physical_multicrew", "fsd_hyperdrive_charging"
    ];

    /// <summary>Status.json "GuiFocus" 0..11.</summary>
    public static readonly string[] GuiFocusNames =
    [
        "none", "internal_panel", "external_panel", "comms_panel", "role_panel", "station_services",
        "galaxy_map", "system_map", "orrery", "fss", "saa", "codex"
    ];

    /// <summary>Status.json "LegalState"; ed.legal_state is the index (-1 = a state not in this list).</summary>
    public static readonly string[] LegalStates = ["Clean", "IllegalCargo", "Speeding", "Wanted", "Hostile", "PassengerWanted", "Warrant"];

    public static int LegalStateCode(string? state)
    {
        for (var i = 0; i < LegalStates.Length; i++)
        {
            if (string.Equals(LegalStates[i], state, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public static string GuiFocusName(int focus) => focus >= 0 && focus < GuiFocusNames.Length ? GuiFocusNames[focus] : "focus_" + focus.ToString(CultureInfo.InvariantCulture);

    public static readonly IReadOnlyList<TelemetryDescriptor> Descriptors = BuildDescriptors();

    private static IReadOnlyList<TelemetryDescriptor> BuildDescriptors()
    {
        var list = new List<TelemetryDescriptor>
        {
            new("ed.time_unix", "Status time", "s (Unix)", 0, 4e9, "Status", "Status.json timestamp"),
            new("ed.flags", "Flags", "", 0, uint.MaxValue, "Status", "Status.json Flags"),
            new("ed.flags2", "Flags2", "", 0, 1 << 20, "Status", "Status.json Flags2"),
            new("ed.pips.sys", "Pips SYS", "half pips", 0, 8, "Ship", "Status.json Pips[0]"),
            new("ed.pips.eng", "Pips ENG", "half pips", 0, 8, "Ship", "Status.json Pips[1]"),
            new("ed.pips.wep", "Pips WEP", "half pips", 0, 8, "Ship", "Status.json Pips[2]"),
            new("ed.fire_group", "Fire group", "", 0, 8, "Ship", "Status.json FireGroup"),
            new("ed.gui_focus", "GUI focus", "", 0, 11, "Ship", "Status.json GuiFocus"),
            new("ed.fuel_main", "Fuel main tank", "t", 0, 64, "Ship", "Status.json Fuel.FuelMain"),
            new("ed.fuel_reservoir", "Fuel reservoir", "t", 0, 1.5, "Ship", "Status.json Fuel.FuelReservoir"),
            new("ed.cargo", "Cargo", "t", 0, 800, "Ship", "Status.json Cargo"),
            new("ed.legal_state", "Legal state", "code", -1, 6, "Ship", "Status.json LegalState (0 Clean, 1 IllegalCargo, 2 Speeding, 3 Wanted, 4 Hostile, 5 PassengerWanted, 6 Warrant)"),
            new("ed.balance", "Credits", "Cr", 0, 1e10, "Commander", "Status.json Balance"),
            new("ed.lat", "Planet latitude", "deg", -90, 90, "Planet", "Status.json Latitude"),
            new("ed.lon", "Planet longitude", "deg", -180, 180, "Planet", "Status.json Longitude"),
            new("ed.alt", "Altitude", "m", 0, 100000, "Planet", "Status.json Altitude"),
            new("ed.heading", "Heading", "deg", 0, 360, "Planet", "Status.json Heading"),
            new("ed.planet_radius", "Planet radius", "m", 0, 1e8, "Planet", "Status.json PlanetRadius"),
            new("ed.dest.system", "Destination system address", "", 0, 1e17, "Navigation", "Status.json Destination.System"),
            new("ed.dest.body", "Destination body id", "", 0, 1000, "Navigation", "Status.json Destination.Body"),
            new("ed.oxygen", "Oxygen", "0..1", 0, 1, "On foot", "Status.json Oxygen"),
            new("ed.health", "Health", "0..1", 0, 1, "On foot", "Status.json Health"),
            new("ed.temperature", "Temperature", "K", 0, 1000, "On foot", "Status.json Temperature"),
            new("ed.gravity", "Gravity", "g", 0, 10, "On foot", "Status.json Gravity"),
            new("ed.hull_health", "Hull health", "0..1", 0, 1, "Journal", "Journal Loadout.HullHealth / HullDamage.Health"),
            new("ed.star_x", "Galaxy position X", "ly", -50000, 50000, "Journal", "Journal Location/FSDJump/CarrierJump StarPos[0]"),
            new("ed.star_y", "Galaxy position Y", "ly", -5000, 5000, "Journal", "Journal StarPos[1]"),
            new("ed.star_z", "Galaxy position Z", "ly", -50000, 80000, "Journal", "Journal StarPos[2]"),
            new("ed.jump_dist", "Last jump distance", "ly", 0, 100, "Journal", "Journal FSDJump.JumpDist"),
        };
        for (var i = 0; i < FlagNames.Length; i++)
            list.Add(new TelemetryDescriptor("ed.flag." + FlagNames[i], "Flag " + FlagNames[i].Replace('_', ' '), "0/1", 0, 1, "Flags", "Status.json Flags bit " + i.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < Flag2Names.Length; i++)
            list.Add(new TelemetryDescriptor("ed.flag2." + Flag2Names[i], "Flag " + Flag2Names[i].Replace('_', ' '), "0/1", 0, 1, "Flags", "Status.json Flags2 bit " + i.ToString(CultureInfo.InvariantCulture)));
        return list;
    }

    private static readonly Dictionary<string, string> SnakeCache = new(StringComparer.Ordinal);

    /// <summary>"PlanetRadius" -> "planet_radius", "FuelMain" -> "fuel_main", "SRVType" -> "srv_type".</summary>
    public static string Snake(string name)
    {
        lock (SnakeCache)
        {
            if (SnakeCache.TryGetValue(name, out var cached))
                return cached;
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    var prevLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    var nextLower = i > 0 && i + 1 < name.Length && char.IsUpper(name[i - 1]) && char.IsLower(name[i + 1]);
                    if ((prevLower || nextLower) && sb.Length > 0 && sb[^1] != '_')
                        sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }
            }

            var key = sb.ToString().Trim('_');
            if (SnakeCache.Count < 1024)
                SnakeCache[name] = key;
            return key;
        }
    }

    private static readonly JsonWriterOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The JSON without the game's cosmetic spaces, cut to <see cref="MaxEventText"/> characters ("..." marks a cut).</summary>
    public static string Compact(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, CompactOptions))
            element.WriteTo(writer);
        return Cap(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string Cap(string text) => text.Length <= MaxEventText ? text : text[..(MaxEventText - 3)] + "...";

    /// <summary>The "event" field of a journal line without parsing the rest; null when the line is no JSON object.</summary>
    public static string? PeekEvent(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return null;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                var isEvent = reader.ValueTextEquals("event"u8);
                if (!reader.Read())
                    return null;
                if (isEvent)
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>The journal folder: TelemetryFeedOptions.EliteFolder, else Saved Games\Frontier Developments\Elite Dangerous.</summary>
    public static string DefaultFolder()
    {
        var savedGames = SavedGamesFolder();
        return Path.Combine(savedGames, "Frontier Developments", "Elite Dangerous");
    }

    private static string SavedGamesFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var id = new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4"); // FOLDERID_SavedGames
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) == 0)
                {
                    try
                    {
                        var path = Marshal.PtrToStringUni(pointer);
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
            {
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>Turns Status.json, journal lines and companion files into frames, events, raw input and session facts.</summary>
/// <remarks>
/// Frames come from Status.json (the game writes it only on a change; <see cref="Heartbeat"/> repeats the last state
/// while a session runs so a recording has no gaps) plus a few journal values (ed.hull_health, ed.star_x/y/z,
/// ed.jump_dist). Every journal line raises "ed.&lt;event&gt;" with the compact JSON as text. Session: LoadGame
/// starts it, Shutdown or Music "MainMenu" (and a new journal part 1 of a restarted game) ends it. Not thread-safe:
/// one thread (the feed's poll) calls it.
/// </remarks>
public sealed class EliteTelemetrySource : FrameTelemetrySource
{
    public const string StatusRawStream = "file:Status.json";
    public const string JournalRawStream = "file:Journal";
    public const string FileEventPrefix = "ed.file.";

    /// <summary>Session facts besides <see cref="TelemetryInfoKeys"/>.</summary>
    public const string InfoBody = "body";
    public const string InfoStation = "station";
    public const string InfoGuiFocus = "gui_focus";
    public const string InfoLegalState = "legal_state";
    public const string InfoSelectedWeapon = "selected_weapon";
    public const string InfoDestination = "destination";
    public const string InfoVehicle = "vehicle";
    public const string InfoShipName = "ship_name";
    public const string InfoShipIdent = "ship_ident";

    /// <summary>Journal events read while catching up at start (they set the current ship, system, commander).</summary>
    private static readonly HashSet<string> ContextEvents = new(StringComparer.Ordinal)
    {
        "Fileheader", "Commander", "LoadGame", "Loadout", "ShipyardSwap", "ShipyardNew", "Location", "FSDJump",
        "CarrierJump", "Docked", "Undocked", "Shutdown", "Music", "HullDamage"
    };

    private readonly Dictionary<string, double> _sticky = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _pendingInfo = new(StringComparer.Ordinal);
    private Dictionary<string, double>? _status;
    private byte[]? _lastStatusBytes;
    private bool _catchingUp;
    private double _lastPublish = double.NegativeInfinity;

    public EliteTelemetrySource(IFfbClock clock) : base("Elite Dangerous", EliteTelemetryProtocol.Descriptors, clock)
    {
    }

    /// <summary>Seconds without a Status.json change after which the last state is published again (0 = never).</summary>
    public double HeartbeatSeconds { get; set; } = 1;

    /// <summary>True between LoadGame and Shutdown / the main menu.</summary>
    public bool InSession { get; private set; }

    public long JournalLines { get; private set; }

    // ------------------------------------------------------------------ catching up at start

    /// <summary>From now on journal lines only update the context (ship, system, commander, session): no events.</summary>
    public void BeginCatchUp()
    {
        _catchingUp = true;
        _pendingInfo.Clear();
    }

    /// <summary>Ends the catch-up. If the journal says a session runs, raises SessionStart ("in progress") and sets the facts.</summary>
    public void EndCatchUp()
    {
        if (!_catchingUp)
            return;
        _catchingUp = false;
        if (InSession)
        {
            RaiseEvent(TelemetryEventKinds.SessionStart, "in progress");
            foreach (var pair in _pendingInfo)
                SetInfo(pair.Key, pair.Value);
        }

        _pendingInfo.Clear();
    }

    // ------------------------------------------------------------------ Status.json

    /// <summary>Handles one read of Status.json.</summary>
    /// <param name="initial">The state found at start: published only when the journal says a session runs.</param>
    /// <returns>False when the bytes are no complete Status object (a half-written file: read again next tick).</returns>
    public bool IngestStatus(ReadOnlySpan<byte> json, bool initial = false)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!ParseStatus(json, values, texts))
            return false;
        if (_lastStatusBytes is not null && json.SequenceEqual(_lastStatusBytes))
            return true;
        _lastStatusBytes = json.ToArray();

        var flags = values.TryGetValue("ed.flags", out var f) ? (long)f : 0;
        var flags2 = values.TryGetValue("ed.flags2", out var f2) ? (long)f2 : 0;
        if (flags == 0 && flags2 == 0)
        {
            // Main menu or the game closed: the game writes a Status with only the timestamp and Flags 0.
            if (!initial)
                PublishRaw(StatusRawStream, json);
            _status = null;
            Clear();
            return true;
        }

        if (initial && !InSession)
        {
            _status = null;
            return true;
        }

        PublishRaw(StatusRawStream, json);
        _status = values;
        foreach (var pair in texts)
            SetInfo(pair.Key, pair.Value);
        SetInfo(InfoVehicle, Vehicle(flags, flags2));
        PublishMerged();
        return true;
    }

    /// <summary>Publishes the last state again when Status.json has not changed for <see cref="HeartbeatSeconds"/>.</summary>
    public bool Heartbeat()
    {
        if (HeartbeatSeconds <= 0 || !InSession || _status is null || Clock.Now - _lastPublish < HeartbeatSeconds)
            return false;
        PublishMerged();
        return true;
    }

    private void PublishMerged()
    {
        if (_status is null)
            return;
        var values = new Dictionary<string, double>(_status, StringComparer.Ordinal);
        foreach (var pair in _sticky)
            values[pair.Key] = pair.Value;
        _lastPublish = Clock.Now;
        Publish(new TelemetryFrame(Clock.Now, values));
    }

    private static string? Vehicle(long flags, long flags2)
    {
        if ((flags2 & 1) != 0)
            return "on_foot";
        if ((flags2 & 2) != 0)
            return "taxi";
        if ((flags & (1L << 26)) != 0)
            return "srv";
        if ((flags & (1L << 25)) != 0)
            return "fighter";
        if ((flags & (1L << 24)) != 0)
            return "ship";
        return null;
    }

    /// <summary>Status.json -> "ed.*" values and text facts. Public for tests.</summary>
    public static bool ParseStatus(ReadOnlySpan<byte> json, IDictionary<string, double> values, IDictionary<string, string?> texts)
    {
        JsonDocument document;
        try
        {
            if (json.IsEmpty)
                return false;
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() != "Status")
                return false;

            texts[InfoBody] = null;
            texts[InfoLegalState] = null;
            texts[InfoSelectedWeapon] = null;
            texts[InfoDestination] = null;
            texts[InfoGuiFocus] = null;
            string? weapon = null, weaponLocalised = null;
            foreach (var property in root.EnumerateObject())
            {
                var name = property.Name;
                var value = property.Value;
                switch (name)
                {
                    case "event":
                        break;
                    case "timestamp":
                        if (value.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp))
                            values["ed.time_unix"] = stamp.ToUnixTimeSeconds();
                        break;
                    case "Flags":
                        if (TryNumber(value, out var flags))
                            AddBits(values, "ed.flags", "ed.flag.", EliteTelemetryProtocol.FlagNames, flags);
                        break;
                    case "Flags2":
                        if (TryNumber(value, out var flags2))
                            AddBits(values, "ed.flags2", "ed.flag2.", EliteTelemetryProtocol.Flag2Names, flags2);
                        break;
                    case "Pips":
                        if (value.ValueKind == JsonValueKind.Array)
                        {
                            var i = 0;
                            foreach (var pip in value.EnumerateArray())
                            {
                                if (TryNumber(pip, out var p))
                                    values[i switch { 0 => "ed.pips.sys", 1 => "ed.pips.eng", 2 => "ed.pips.wep", _ => "ed.pips." + i.ToString(CultureInfo.InvariantCulture) }] = p;
                                i++;
                            }
                        }

                        break;
                    case "Fuel":
                        AddObject(values, value, "ed.fuel_", n => n switch { "FuelMain" => "ed.fuel_main", "FuelReservoir" => "ed.fuel_reservoir", _ => null });
                        break;
                    case "Destination":
                        if (value.ValueKind == JsonValueKind.Object)
                        {
                            AddObject(values, value, "ed.dest.", n => n switch { "System" => "ed.dest.system", "Body" => "ed.dest.body", _ => null });
                            texts[InfoDestination] = Text(value, "Name_Localised") ?? Text(value, "Name");
                        }

                        break;
                    case "LegalState":
                        var legal = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        values["ed.legal_state"] = EliteTelemetryProtocol.LegalStateCode(legal);
                        texts[InfoLegalState] = legal;
                        break;
                    case "GuiFocus":
                        if (TryNumber(value, out var focus))
                        {
                            values["ed.gui_focus"] = focus;
                            texts[InfoGuiFocus] = EliteTelemetryProtocol.GuiFocusName((int)focus);
                        }

                        break;
                    case "BodyName":
                        texts[InfoBody] = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        break;
                    case "SelectedWeapon":
                        weapon = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        break;
                    case "SelectedWeapon_Localised":
                        weaponLocalised = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        break;
                    case "Latitude":
                        AddNumber(values, "ed.lat", value);
                        break;
                    case "Longitude":
                        AddNumber(values, "ed.lon", value);
                        break;
                    case "Altitude":
                        AddNumber(values, "ed.alt", value);
                        break;
                    case "Heading":
                        AddNumber(values, "ed.heading", value);
                        break;
                    default:
                        AddGeneric(values, EliteTelemetryProtocol.Prefix + EliteTelemetryProtocol.Snake(name), value, 0);
                        break;
                }
            }

            texts[InfoSelectedWeapon] = weaponLocalised ?? weapon;
            if (!values.ContainsKey("ed.flags"))
                return root.TryGetProperty("timestamp", out _);
            return true;
        }
    }

    private static void AddBits(IDictionary<string, double> values, string key, string prefix, string[] names, double number)
    {
        var bits = (long)number;
        values[key] = bits;
        for (var i = 0; i < names.Length; i++)
            values[prefix + names[i]] = (bits >> i & 1) != 0 ? 1 : 0;
    }

    private static void AddObject(IDictionary<string, double> values, JsonElement value, string prefix, Func<string, string?> known)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;
        foreach (var child in value.EnumerateObject())
        {
            var key = known(child.Name) ?? prefix + EliteTelemetryProtocol.Snake(child.Name);
            AddNumber(values, key, child.Value);
        }
    }

    private static void AddGeneric(IDictionary<string, double> values, string key, JsonElement value, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AddNumber(values, key, value);
                break;
            case JsonValueKind.Object when depth < 2:
                foreach (var child in value.EnumerateObject())
                    AddGeneric(values, key + "." + EliteTelemetryProtocol.Snake(child.Name), child.Value, depth + 1);
                break;
            case JsonValueKind.Array when depth < 2:
                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (i >= 16)
                        break;
                    AddGeneric(values, key + "." + i.ToString(CultureInfo.InvariantCulture), item, depth + 1);
                    i++;
                }

                break;
        }
    }

    private static void AddNumber(IDictionary<string, double> values, string key, JsonElement value)
    {
        if (TryNumber(value, out var number))
            values[key] = number;
    }

    private static bool TryNumber(JsonElement value, out double number)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out number) && double.IsFinite(number):
                return true;
            case JsonValueKind.True:
                number = 1;
                return true;
            case JsonValueKind.False:
                number = 0;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } s ? s : null
            : null;

    // ------------------------------------------------------------------ journal

    /// <summary>Handles one complete journal line (without the line break).</summary>
    public void IngestJournalLine(ReadOnlySpan<byte> line)
    {
        line = Trim(line);
        if (line.IsEmpty)
            return;
        var name = EliteTelemetryProtocol.PeekEvent(line);
        if (name is null || (_catchingUp && !ContextEvents.Contains(name)))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            JournalLines++;
            if (name == "LoadGame")
            {
                EndSession("LoadGame");
                InSession = true;
                _sticky.Clear();
                Event(TelemetryEventKinds.SessionStart, string.Join(", ", new[] { Text(root, "Commander"), Ship(root, "Ship"), Text(root, "GameMode") }.Where(s => s is not null)));
            }

            if (!_catchingUp)
            {
                PublishRaw(JournalRawStream, line);
                RaiseEvent(EliteTelemetryProtocol.Prefix + name, EliteTelemetryProtocol.Compact(root));
            }

            Handle(name, root);
        }
    }

    private void Handle(string name, JsonElement root)
    {
        var stickyChanged = false;
        switch (name)
        {
            case "Fileheader":
                if (TryInt(root, "part") is 1)
                    EndSession("new journal");
                Info(TelemetryInfoKeys.GameVersion, GameVersion(root));
                break;
            case "Commander":
                Info(TelemetryInfoKeys.Pilot, Text(root, "Name"));
                break;
            case "LoadGame":
                Info(TelemetryInfoKeys.Pilot, Text(root, "Commander"));
                Info(TelemetryInfoKeys.Aircraft, Ship(root, "Ship"));
                Info(InfoShipName, Text(root, "ShipName"));
                Info(InfoShipIdent, Text(root, "ShipIdent"));
                var mode = Text(root, "GameMode");
                var group = Text(root, "Group");
                Info(TelemetryInfoKeys.Mission, group is null ? mode : mode + " (" + group + ")");
                if (GameVersion(root) is { } version)
                    Info(TelemetryInfoKeys.GameVersion, version);
                break;
            case "Loadout":
                Info(TelemetryInfoKeys.Aircraft, Ship(root, "Ship"));
                Info(InfoShipName, Text(root, "ShipName"));
                Info(InfoShipIdent, Text(root, "ShipIdent"));
                stickyChanged |= Sticky("ed.hull_health", root, "HullHealth");
                break;
            case "ShipyardSwap":
            case "ShipyardNew":
                Info(TelemetryInfoKeys.Aircraft, Ship(root, "ShipType"));
                break;
            case "Location":
            case "FSDJump":
            case "CarrierJump":
                Info(TelemetryInfoKeys.Map, Text(root, "StarSystem"));
                if (root.TryGetProperty("StarPos", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() == 3)
                {
                    stickyChanged |= Sticky("ed.star_x", pos[0]);
                    stickyChanged |= Sticky("ed.star_y", pos[1]);
                    stickyChanged |= Sticky("ed.star_z", pos[2]);
                }

                if (name == "FSDJump")
                    stickyChanged |= Sticky("ed.jump_dist", root, "JumpDist");
                var docked = root.TryGetProperty("Docked", out var d) && d.ValueKind == JsonValueKind.True;
                Info(InfoStation, docked ? Text(root, "StationName") : null);
                break;
            case "Docked":
                Info(InfoStation, Text(root, "StationName"));
                break;
            case "Undocked":
                Info(InfoStation, null);
                break;
            case "HullDamage":
                var player = !root.TryGetProperty("PlayerPilot", out var pp) || pp.ValueKind == JsonValueKind.True;
                var fighter = root.TryGetProperty("Fighter", out var fi) && fi.ValueKind == JsonValueKind.True;
                if (player && !fighter)
                    stickyChanged |= Sticky("ed.hull_health", root, "Health");
                break;
            case "Shutdown":
                EndSession("Shutdown");
                break;
            case "Music":
                if (Text(root, "MusicTrack") == "MainMenu")
                    EndSession("MainMenu");
                break;
            case "ReceiveText":
                var from = Text(root, "From_Localised") ?? Text(root, "From");
                var message = Text(root, "Message_Localised") ?? Text(root, "Message");
                if (message is not null)
                    Event(TelemetryEventKinds.Message, EliteTelemetryProtocol.Cap((from is null ? "" : from + ": ") + message));
                break;
            case "SendText":
                var sent = Text(root, "Message");
                if (sent is not null)
                    Event(TelemetryEventKinds.Message, EliteTelemetryProtocol.Cap("me" + (Text(root, "To") is { } to ? " to " + to : "") + ": " + sent));
                break;
        }

        if (stickyChanged && !_catchingUp && InSession && _status is not null)
            PublishMerged();
    }

    // ------------------------------------------------------------------ companion files

    /// <summary>A companion file (NavRoute.json, Cargo.json, ...) changed: raw input and an "ed.file.&lt;name&gt;" event.</summary>
    public void IngestCompanionFile(string fileName, ReadOnlySpan<byte> content)
    {
        PublishRaw("file:" + fileName, content);
        string text;
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            text = EliteTelemetryProtocol.Compact(document.RootElement);
        }
        catch (JsonException)
        {
            text = EliteTelemetryProtocol.Cap(Encoding.UTF8.GetString(content));
        }

        RaiseEvent(FileEventPrefix + Path.GetFileNameWithoutExtension(fileName), text);
    }

    // ------------------------------------------------------------------ helpers

    private void EndSession(string why)
    {
        if (!InSession)
            return;
        InSession = false;
        Event(TelemetryEventKinds.SessionEnd, why);
        if (!_catchingUp)
        {
            _status = null;
            Clear();
        }
    }

    private void Event(string kind, string text)
    {
        if (!_catchingUp)
            RaiseEvent(kind, text);
    }

    private void Info(string key, string? value)
    {
        if (_catchingUp)
            _pendingInfo[key] = value;
        else
            SetInfo(key, value);
    }

    private bool Sticky(string key, JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && Sticky(key, value);

    private bool Sticky(string key, JsonElement value)
    {
        if (!TryNumber(value, out var number))
            return false;
        if (_sticky.TryGetValue(key, out var old) && old.Equals(number))
            return false;
        _sticky[key] = number;
        return true;
    }

    /// <summary>The ship type in one spelling (LoadGame writes "Krait_MkII", Loadout "krait_mkii").</summary>
    private static string? Ship(JsonElement root, string property) => Text(root, property)?.ToLowerInvariant();

    private static string? GameVersion(JsonElement root)
    {
        var version = Text(root, "gameversion");
        var build = Text(root, "build")?.Trim();
        return version is null ? null : string.IsNullOrEmpty(build) ? version : version + " " + build;
    }

    private static int? TryInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line)
    {
        if (line.StartsWith("﻿"u8))
            line = line[3..];
        var start = 0;
        var end = line.Length;
        while (start < end && line[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        while (end > start && line[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0)
            end--;
        return line[start..end];
    }
}

/// <summary>Polls the journal folder between Start and Stop: Status.json, the newest Journal.*.log and the companion
/// files, by their write time and length (no FileSystemWatcher: it misses changes on some setups).</summary>
/// <remarks>
/// Each tick (<see cref="PollHz"/>, at most 10 per second from <see cref="TelemetryFeeds.Create"/>) costs a few file
/// stats; a file is opened only when it changed. Status.json is read with FileShare.ReadWrite | Delete; a half-written
/// file is read again next tick. The first poll reads the current journal from its start without raising its old
/// events (it only learns ship, system, commander and whether a session runs); later lines raise events. A newer
/// journal (the game restarted, or a "Continued" part) is followed after the rest of the old one is read. The folder
/// is looked for again every tick, so the feed may start before the game created it.
/// </remarks>
public sealed class EliteTelemetryFeed : ITelemetryFeed
{
    /// <summary>The directory listing (for a newer journal) is redone at least this often, and whenever the folder's write time changes.</summary>
    public const double JournalRescanSeconds = 5;

    /// <summary>Companion files, an opened size check of the journal (sizes of files in use can lag) and a re-read of
    /// Status.json every this many seconds.</summary>
    public const double SlowCheckSeconds = 1;

    private readonly EliteTelemetrySource _source;
    private readonly object _pollGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Ticks, long Length)> _companionStamps = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    private bool _primed;
    private string? _journal;
    private long _journalOffset;
    private (long Ticks, long Length) _statusStamp = (-1, -1);
    private long _folderTicks = -1;
    private double _lastScan = double.NegativeInfinity;
    private double _lastSlow = double.NegativeInfinity;

    public EliteTelemetryFeed(EliteTelemetrySource source, string? folder = null, double pollHz = 10)
    {
        _source = source;
        Folder = string.IsNullOrWhiteSpace(folder) ? EliteTelemetryProtocol.DefaultFolder() : folder;
        PollHz = Math.Clamp(double.IsFinite(pollHz) ? pollHz : 10, 0.5, 100);
    }

    public FrameTelemetrySource Source => _source;
    public EliteTelemetrySource EliteSource => _source;
    public string Folder { get; }
    public double PollHz { get; }
    public string Endpoint => Folder;
    public string? Problem { get; private set; }

    /// <summary>The journal followed now (null before one was found).</summary>
    public string? CurrentJournal => _journal;

    public long Polls { get; private set; }

    /// <summary>The last unexpected error of a poll (the loop keeps running).</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => Loop(token));
        }
    }

    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            _stop?.Cancel();
        }

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _stop?.Dispose();
            _stop = null;
        }
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / PollHz));
        do
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;              // keep polling: the next tick may read fine
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (!stop.IsCancellationRequested);
    }

    /// <summary>One poll (the loop calls it every tick; tests call it directly).</summary>
    public void Poll()
    {
        lock (_pollGate)
        {
            Polls++;
            if (!Directory.Exists(Folder))
            {
                Problem = "Elite Dangerous journal folder not found: " + Folder;
                return;
            }

            var now = _source.Clock.Now;
            var slow = now - _lastSlow >= SlowCheckSeconds || !_primed;
            if (slow)
                _lastSlow = now;

            // The journal first: LoadGame comes before the Status.json of the new session.
            var folderTicks = SafeDirectoryTicks();
            if (!_primed || folderTicks != _folderTicks || now - _lastScan >= JournalRescanSeconds)
            {
                _folderTicks = folderTicks;
                _lastScan = now;
                var newest = NewestJournal();
                if (newest is not null && !string.Equals(newest, _journal, StringComparison.OrdinalIgnoreCase))
                {
                    if (_journal is not null)
                        ReadJournal(force: true);
                    _journal = newest;
                    _journalOffset = 0;
                    if (!_primed)
                    {
                        _source.BeginCatchUp();
                        ReadJournal(force: true);
                        _source.EndCatchUp();
                    }
                }
            }

            ReadJournal(force: slow);
            var statusPresent = ReadStatus(initial: !_primed, force: slow);
            if (slow)
                CheckCompanionFiles(baseline: !_primed);
            _source.Heartbeat();
            _primed = true;
            Problem = statusPresent ? null : "Status.json missing in " + Folder + " (Elite Dangerous writes it while it runs)";
        }
    }

    /// <param name="force">Read even with an unchanged stamp: two rewrites of the same length within the file time's
    /// resolution look alike (the source drops an unchanged content).</param>
    private bool ReadStatus(bool initial, bool force)
    {
        var path = Path.Combine(Folder, EliteTelemetryProtocol.StatusFile);
        if (!TryStamp(path, out var stamp))
            return false;
        if (stamp == _statusStamp && !force)
            return true;
        var bytes = TryReadAll(path);
        if (bytes is null)
            return true;                                    // locked: next tick
        if (_source.IngestStatus(bytes, initial))
            _statusStamp = stamp;                           // a half-written file keeps the old stamp: read again next tick
        return true;
    }

    private void ReadJournal(bool force)
    {
        if (_journal is null)
            return;
        if (!force && (!TryStamp(_journal, out var stamp) || stamp.Length <= _journalOffset))
            return;
        try
        {
            using var stream = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var length = stream.Length;
            if (length < _journalOffset)
                _journalOffset = 0;                         // replaced by a shorter file
            if (length == _journalOffset)
                return;
            var count = (int)Math.Min(length - _journalOffset, 64L * 1024 * 1024);
            var buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                stream.Seek(_journalOffset, SeekOrigin.Begin);
                var read = 0;
                while (read < count)
                {
                    var n = stream.Read(buffer, read, count - read);
                    if (n <= 0)
                        break;
                    read += n;
                }

                var data = buffer.AsSpan(0, read);
                var consumed = 0;
                while (true)
                {
                    var newline = data[consumed..].IndexOf((byte)'\n');
                    if (newline < 0)
                        break;                              // a line still being written: next tick
                    _source.IngestJournalLine(data.Slice(consumed, newline));
                    consumed += newline + 1;
                }

                _journalOffset += consumed;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being created or locked: next tick.
        }
    }

    private void CheckCompanionFiles(bool baseline)
    {
        foreach (var name in EliteTelemetryProtocol.CompanionFiles)
        {
            var path = Path.Combine(Folder, name);
            if (!TryStamp(path, out var stamp))
            {
                _companionStamps.Remove(name);
                continue;
            }

            if (_companionStamps.TryGetValue(name, out var old) && old == stamp)
                continue;
            if (baseline)
            {
                _companionStamps[name] = stamp;
                continue;
            }

            var bytes = TryReadAll(path);
            if (bytes is null || bytes.Length == 0)
                continue;                                   // locked or being rewritten: next check
            _companionStamps[name] = stamp;
            _source.IngestCompanionFile(name, bytes);
        }
    }

    /// <summary>The newest Journal.*.log by the time in its name (both name styles), then the part number.</summary>
    private string? NewestJournal()
    {
        string? best = null;
        (DateTime Time, int Part) bestKey = (DateTime.MinValue, -1);
        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder, EliteTelemetryProtocol.JournalPattern))
            {
                var key = JournalKey(path);
                if (best is null || key.CompareTo(bestKey) > 0)
                {
                    best = path;
                    bestKey = key;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _journal;
        }

        return best;
    }

    /// <summary>"Journal.2026-10-11T101337.01.log" or the old "Journal.170403163310.01.log" -> (time, part).</summary>
    internal static (DateTime Time, int Part) JournalKey(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        if (parts.Length >= 4)
        {
            var part = int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
            if (DateTime.TryParseExact(parts[1], ["yyyy-MM-dd'T'HHmmss", "yyMMddHHmmss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                return (time, part);
        }

        try
        {
            return (File.GetLastWriteTimeUtc(path), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (DateTime.MinValue, 0);
        }
    }

    private long SafeDirectoryTicks()
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(Folder).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool TryStamp(string path, out (long Ticks, long Length) stamp)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        stamp = (-1, -1);
        return false;
    }

    private static byte[]? TryReadAll(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var length = stream.Length;
            if (length > 64L * 1024 * 1024)
                return null;
            var buffer = new byte[length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                    break;
                read += n;
            }

            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
