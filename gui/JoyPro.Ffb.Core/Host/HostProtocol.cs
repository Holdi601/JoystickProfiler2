using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoyPro.Ffb.Core.Host;

/// <summary>The line protocol between the GUI and the JoyPro.Ffb process (run --protocol).</summary>
/// <remarks>
/// The process writes one JSON object per line to stdout:
/// <code>
/// {"type":"status","state":"active","reason":"active - F-16C_50","game":"DCS","aircraft":"F-16C_50",
///  "graph":"Flight (built-in)","frames":1234,"endpoint":"UDP 41992","device":"VPforce Rhino FFB Joystick"}
/// {"type":"telemetry","time":12.3,"values":{"ac.ias":154.2,...}}      5 times a second while watching
/// {"type":"nodes","values":{"n1a2b3c4d5e6":0.42,...}}                  5 times a second while watching
/// {"type":"log","message":"..."}
/// {"type":"exit","reason":"game exited"}
/// </code>
/// and reads commands, one per line, from stdin: "watch on", "watch off", "reload" (config and graphs changed),
/// "stop". End of stdin (the GUI went away) stops the process as well.
/// </remarks>
public static class HostProtocol
{
    public const string WatchOn = "watch on";
    public const string WatchOff = "watch off";
    public const string Reload = "reload";
    public const string Stop = "stop";

    public static string StatusLine(TelemetryFfbStatus status, string device) => new JsonObject
    {
        ["type"] = "status",
        ["state"] = status.StateName,
        ["reason"] = status.Reason,
        ["game"] = status.Game,
        ["aircraft"] = status.Aircraft,
        ["graph"] = status.Graph,
        ["frames"] = status.Frames,
        ["endpoint"] = status.Endpoint,
        ["device"] = device
    }.ToJsonString();

    public static string ValuesLine(string type, IReadOnlyDictionary<string, double> values, double? time = null)
    {
        var json = new JsonObject { ["type"] = type };
        if (time is { } t)
            json["time"] = Math.Round(t, 3);
        var map = new JsonObject();
        foreach (var pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (double.IsFinite(pair.Value))
                map[pair.Key] = Math.Round(pair.Value, 5);
        }

        json["values"] = map;
        return json.ToJsonString();
    }

    /// <summary>Telemetry recording (run and record): state "off" | "waiting" | "listening" | "recording", the file
    /// being written, its frames, and the last problem.</summary>
    public static string RecordingLine(string state, string reason, string game, string? session, long frames, int sessions, string? error, string? aircraft = null) => new JsonObject
    {
        ["type"] = "recording",
        ["state"] = state,
        ["reason"] = reason,
        ["game"] = game,
        ["aircraft"] = aircraft,
        ["session"] = session,
        ["frames"] = frames,
        ["sessions"] = sessions,
        ["error"] = error
    }.ToJsonString();

    public static string RecordingLine(TelemetryRecordStatus status) =>
        RecordingLine(status.State, status.Reason, status.Game, status.Session, status.Frames, status.Sessions, status.Error, status.Aircraft);

    public static string LogLine(string message) => new JsonObject { ["type"] = "log", ["message"] = message }.ToJsonString();

    public static string ExitLine(string reason) => new JsonObject { ["type"] = "exit", ["reason"] = reason }.ToJsonString();

    /// <summary>Parses one stdout line; null when it is not a JSON object with a "type".</summary>
    public static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject json && json["type"] is JsonValue ? json : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
