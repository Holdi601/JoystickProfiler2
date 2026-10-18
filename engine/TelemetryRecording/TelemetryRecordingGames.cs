#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.TelemetrySessions
{
    // The games whose telemetry JoyPro can record (docs/telemetry-recording.md). Plain C# 7.3: compiled into the engine
    // and linked into JoyPro.Ffb.Core (the recorder) and so into the GUI, so the three agree on ids, names and processes.
    public sealed class TelemetryRecordingGame
    {
        public string Id;              // the engine's game id (GameInstalls)
        public string Title;
        public string[] ProcessNames;  // without ".exe"
        public string Transport;       // what JoyPro listens to, for people: "UDP 41992 (JoyPro's DCS hook)"
        public bool HasPosition;       // the telemetry has a position: ACMI export possible
        public string Setup;           // what has to be set up once ("" = nothing): DCS = engine ffb telemetry setup dcs,
                                       // IL-2 = engine ffb telemetry setup il2 | il2korea
    }

    public static class TelemetryRecordingGames
    {
        public static readonly TelemetryRecordingGame[] All =
        {
            new TelemetryRecordingGame { Id = "DCS", Title = "DCS World", ProcessNames = new[] { "DCS", "DCS_mt" }, Transport = "UDP 41992 (JoyPro's DCS hook)", HasPosition = true,
                Setup = "JoyPro's DCS hook (Force Feedback page: Set up)" },
            new TelemetryRecordingGame { Id = "IL2Game", Title = "IL-2 Great Battles", ProcessNames = new[] { "Il-2" }, Transport = "UDP 41994 (telemetry and motion output)", HasPosition = false,
                Setup = "JoyPro's address in the game's startup.cfg (Force Feedback page: Set up)" },
            new TelemetryRecordingGame { Id = "IL2Korea", Title = "IL-2 Series: Korea", ProcessNames = new[] { "IL2Series" }, Transport = "UDP 41994 (telemetry and motion output)", HasPosition = false,
                Setup = "JoyPro's address in the game's startup.cfg (Force Feedback page: Set up)" },
            new TelemetryRecordingGame { Id = "WarThunder", Title = "War Thunder", ProcessNames = new[] { "aces", "aces_be" }, Transport = "HTTP 127.0.0.1:8111 (the game's local web server)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "XPlane12", Title = "X-Plane 12", ProcessNames = new[] { "X-Plane" }, Transport = "UDP 49000 (RREF subscriptions)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "XPlane11", Title = "X-Plane 11", ProcessNames = new[] { "X-Plane" }, Transport = "UDP 49000 (RREF subscriptions)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "MSFS2024", Title = "Microsoft Flight Simulator 2024", ProcessNames = new[] { "FlightSimulator2024" }, Transport = "SimConnect (the sim's own SimConnect.dll)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "MSFS2020", Title = "Microsoft Flight Simulator 2020", ProcessNames = new[] { "FlightSimulator" }, Transport = "SimConnect (the sim's own SimConnect.dll)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "FalconBMS", Title = "Falcon BMS", ProcessNames = new[] { "Falcon BMS" }, Transport = "shared memory (FalconSharedMemoryArea, read only)", HasPosition = true, Setup = "" },
            new TelemetryRecordingGame { Id = "EliteDangerous", Title = "Elite Dangerous", ProcessNames = new[] { "EliteDangerous64" }, Transport = "Status.json and the journal (Saved Games\\Frontier Developments)", HasPosition = false, Setup = "" },
        };

        public static TelemetryRecordingGame Find(string id)
        {
            string key = (id ?? string.Empty).Trim();
            switch (key.ToLowerInvariant())
            {
                case "il2": case "il-2": case "il2gb": key = "IL2Game"; break;
                case "korea": key = "IL2Korea"; break;
                case "wt": case "war thunder": key = "WarThunder"; break;
                case "xplane": case "x-plane": case "x-plane 12": case "xp12": key = "XPlane12"; break;
                case "x-plane 11": case "xp11": key = "XPlane11"; break;
                case "msfs": case "msfs 2024": key = "MSFS2024"; break;
                case "msfs 2020": key = "MSFS2020"; break;
                case "bms": case "falcon": case "falcon bms": key = "FalconBMS"; break;
                case "elite": case "ed": case "elite dangerous": key = "EliteDangerous"; break;
            }
            return All.FirstOrDefault(g => string.Equals(g.Id, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
