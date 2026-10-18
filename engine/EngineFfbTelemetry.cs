using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // Telemetry force feedback (docs/ffb-telemetry.md): JoyPro.Ffb reads a game's telemetry and drives the force
    // feedback stick through a node graph. The engine manages its settings file and sets the games up:
    //   engine ffb telemetry status                                     settings, game setup, process, last status
    //   engine ffb telemetry config show
    //   engine ffb telemetry config set [--game G] [--plane P] [--effect E] | [--device {guid}] --param key=value ...
    //       global keys incl. centring-spring, centring-stiffness; --device: invert-x, invert-y (per force feedback device)
    //   engine ffb telemetry config clear [--game G] [--plane P] [--effect E] | [--device {guid}]
    //   engine ffb telemetry setup dcs|il2|il2korea|warthunder [--remove] [--dry-run] [--path P]
    //   engine ffb telemetry effects                                    the effect names of the built-in graph
    // Settings: <data>\Ffb\Telemetry\telemetry-ffb.json (data = JOYPRO_DATA_DIR or %LOCALAPPDATA%\JoyPro); the same
    // format gui/JoyPro.Ffb.Core/Host/TelemetryFfbConfig.cs reads.
    internal static class EngineFfbTelemetry
    {
        public const string ConfigFormat = "joypro-ffb-telemetry-config";
        public const double DefaultMaxForce = 0.6;
        public const double DefaultCentringStiffness = 0.3;
        public const int DcsPort = 41992;
        public const int Il2Port = 41994;
        public const string DcsHookFileName = "JoyProFfb.lua";
        public const string DcsHookMarker = "-- JoyPro2 telemetry force feedback";

        // Keep in step with FfbEffectNames in gui/JoyPro.Ffb.Core/Graph/FfbPresets.cs.
        public static readonly string[][] Effects =
        {
            new[] { "buffet", "Stall / AoA buffet" },
            new[] { "gforce", "G-force stick weight" },
            new[] { "gear", "Gear moving rumble" },
            new[] { "flaps", "Flaps moving rumble" },
            new[] { "speedbrake", "Speed brake buffet" },
            new[] { "runway", "Runway rumble" },
            new[] { "guns", "Gun fire bursts" },
            new[] { "hits", "Hits taken" },
            new[] { "shake", "Cockpit shake (IL-2)" },
            new[] { "spring", "Centring spring: airspeed stiffening and trim" }
        };

        public sealed class GameSpec
        {
            public string Id;
            public string Title;
            public string[] ProcessNames;
            public int DefaultPort;
            public string Transport;
        }

        public static readonly GameSpec[] Games =
        {
            new GameSpec { Id = "DCS", Title = "DCS World", ProcessNames = new[] { "DCS", "DCS_mt" }, DefaultPort = DcsPort, Transport = "UDP" },
            new GameSpec { Id = "IL2Game", Title = "IL-2 Great Battles", ProcessNames = new[] { "Il-2" }, DefaultPort = Il2Port, Transport = "UDP" },
            new GameSpec { Id = "IL2Korea", Title = "IL-2 Series: Korea", ProcessNames = new[] { "IL2Series" }, DefaultPort = Il2Port, Transport = "UDP" },
            new GameSpec { Id = "WarThunder", Title = "War Thunder", ProcessNames = new[] { "aces", "aces_be" }, DefaultPort = 8111, Transport = "HTTP" }
        };

        public static GameSpec FindGame(string game)
        {
            string key = (game ?? string.Empty).Trim();
            switch (key.ToLowerInvariant())
            {
                case "dcs": key = "DCS"; break;
                case "il2": case "il-2": case "il2game": case "il2gb": key = "IL2Game"; break;
                case "il2korea": case "korea": key = "IL2Korea"; break;
                case "wt": case "warthunder": case "war thunder": key = "WarThunder"; break;
            }
            return Games.FirstOrDefault(g => string.Equals(g.Id, key, StringComparison.OrdinalIgnoreCase));
        }

        public static string Folder()
        {
            return Path.Combine(GameInstalls.DataRoot(), "Ffb", "Telemetry");
        }

        public static string ConfigPath()
        {
            return Path.Combine(Folder(), "telemetry-ffb.json");
        }

        public static string StatusPath()
        {
            return Path.Combine(Folder(), "status.json");
        }

        private static string Il2StatePath()
        {
            return Path.Combine(Folder(), "il2-setup.json");
        }

        // ------------------------------------------------------------------ settings file

        // The settings as JSON objects (Dictionary<string, object>), with every default filled in.
        public static Dictionary<string, object> LoadConfig(List<string> warnings)
        {
            Dictionary<string, object> raw = null;
            string path = ConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    raw = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    warnings.Add("The telemetry force feedback settings could not be read (" + ex.Message + "); the defaults apply.");
                }
            }
            return Normalize(raw ?? new Dictionary<string, object>());
        }

        public static void SaveConfig(Dictionary<string, object> config)
        {
            string path = ConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, IndentJson(EngineSerialization.ToJson(config)) + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private static Dictionary<string, object> Normalize(Dictionary<string, object> raw)
        {
            Dictionary<string, object> config = new Dictionary<string, object>(StringComparer.Ordinal);
            config["format"] = ConfigFormat;
            config["version"] = 1;
            config["enabled"] = Bool(raw, "enabled", false);
            config["device"] = Str(raw, "device", string.Empty);
            config["deviceName"] = Str(raw, "deviceName", string.Empty);
            config["strength"] = Clamp(Num(raw, "strength", 1), 0, 1);
            config["maxForce"] = Clamp(Num(raw, "maxForce", DefaultMaxForce), 0, 1);
            config["invertX"] = Bool(raw, "invertX", false);
            config["invertY"] = Bool(raw, "invertY", false);
            config["tickRateHz"] = Clamp(Num(raw, "tickRateHz", 50), 10, 200);
            config["timeoutSeconds"] = Clamp(Num(raw, "timeoutSeconds", 0.5), 0.1, 5);
            config["centringSpring"] = Bool(raw, "centringSpring", true);
            config["centringStiffness"] = Clamp(Num(raw, "centringStiffness", DefaultCentringStiffness), 0, 1);
            Dictionary<string, object> devices = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> rawDevices = Obj(raw, "devices");
            if (rawDevices != null)
            {
                foreach (KeyValuePair<string, object> pair in rawDevices)
                {
                    Dictionary<string, object> d = pair.Value as Dictionary<string, object>;
                    string key = DeviceKey(pair.Key);
                    if (d == null || key == null)
                        continue;
                    devices[key] = NormalizeDevice(d);
                }
            }
            config["devices"] = devices;
            Dictionary<string, object> games = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> rawGames = Obj(raw, "games");
            if (rawGames != null)
            {
                foreach (KeyValuePair<string, object> pair in rawGames)
                {
                    Dictionary<string, object> g = pair.Value as Dictionary<string, object>;
                    if (g == null)
                        continue;
                    GameSpec spec = FindGame(pair.Key);
                    games[spec != null ? spec.Id : pair.Key] = NormalizeGame(g);
                }
            }
            config["games"] = games;
            return config;
        }

        // Per force feedback device (instance guid): the direction found by the direction test.
        private static Dictionary<string, object> NormalizeDevice(Dictionary<string, object> d)
        {
            Dictionary<string, object> device = new Dictionary<string, object>(StringComparer.Ordinal);
            device["invertX"] = Bool(d, "invertX", false);
            device["invertY"] = Bool(d, "invertY", false);
            return device;
        }

        // "{GUID}" in upper case, or null when the text is not a guid.
        public static string DeviceKey(string device)
        {
            Guid guid;
            return Guid.TryParse((device ?? string.Empty).Trim().Trim('{', '}'), out guid) ? "{" + guid.ToString().ToUpperInvariant() + "}" : null;
        }

        private static Dictionary<string, object> NormalizeGame(Dictionary<string, object> g)
        {
            Dictionary<string, object> game = new Dictionary<string, object>(StringComparer.Ordinal);
            game["enabled"] = Bool(g, "enabled", true);
            game["port"] = (int)Clamp(Num(g, "port", 0), 0, 65535);
            game["graph"] = Str(g, "graph", string.Empty);
            game["strength"] = Clamp(Num(g, "strength", 1), 0, 2);
            game["url"] = Str(g, "url", "http://127.0.0.1:8111/");
            game["pollHz"] = Clamp(Num(g, "pollHz", 20), 1, 60);
            game["effects"] = NormalizeEffects(Obj(g, "effects"));
            Dictionary<string, object> aircraft = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> rawAircraft = Obj(g, "aircraft");
            if (rawAircraft != null)
            {
                foreach (KeyValuePair<string, object> plane in rawAircraft)
                {
                    Dictionary<string, object> p = plane.Value as Dictionary<string, object>;
                    if (p == null)
                        continue;
                    Dictionary<string, object> settings = new Dictionary<string, object>(StringComparer.Ordinal);
                    settings["graph"] = Str(p, "graph", string.Empty);
                    settings["strength"] = Clamp(Num(p, "strength", 1), 0, 2);
                    settings["effects"] = NormalizeEffects(Obj(p, "effects"));
                    aircraft[plane.Key] = settings;
                }
            }
            game["aircraft"] = aircraft;
            return game;
        }

        private static Dictionary<string, object> NormalizeEffects(Dictionary<string, object> raw)
        {
            Dictionary<string, object> effects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (raw == null)
                return effects;
            foreach (KeyValuePair<string, object> pair in raw)
            {
                Dictionary<string, object> e = pair.Value as Dictionary<string, object>;
                if (e == null)
                    continue;
                effects[pair.Key] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "enabled", Bool(e, "enabled", true) },
                    { "strength", Clamp(Num(e, "strength", 1), 0, 2) }
                };
            }
            return effects;
        }

        // Sets values in one scope: global, --game, --game --plane, --effect (game or plane), --device. Returns an error or null.
        public static string Set(Dictionary<string, object> config, string game, string plane, string effect, List<string> parameters, List<string> changes, List<string> warnings)
        {
            return Set(config, game, plane, effect, null, parameters, changes, warnings);
        }

        public static string Set(Dictionary<string, object> config, string game, string plane, string effect, string device, List<string> parameters, List<string> changes, List<string> warnings)
        {
            Dictionary<string, object> scope;
            string scopeName;
            string scopeKind;
            string error;
            if (!string.IsNullOrWhiteSpace(device))
            {
                if (!string.IsNullOrWhiteSpace(game) || !string.IsNullOrWhiteSpace(plane) || !string.IsNullOrWhiteSpace(effect))
                    return "--device cannot be combined with --game, --plane or --effect.";
                string key = DeviceKey(device);
                if (key == null)
                    return "--device needs the device's instance guid ({...}), as 'JoyPro.Ffb devices' lists it: " + device;
                Dictionary<string, object> devices = (Dictionary<string, object>)config["devices"];
                if (!devices.ContainsKey(key))
                    devices[key] = NormalizeDevice(new Dictionary<string, object>());
                scope = (Dictionary<string, object>)devices[key];
                scopeName = "device " + key;
                scopeKind = "device";
            }
            else
            {
                error = ResolveScope(config, game, plane, effect, true, out scope, out scopeName, out scopeKind);
                if (error != null)
                    return error;
            }
            if (parameters.Count == 0)
                return "config set needs at least one --param <key>=<value>.";
            foreach (string parameter in parameters)
            {
                int eq = parameter.IndexOf('=');
                if (eq <= 0)
                    return "--param needs <key>=<value>: " + parameter;
                string key = parameter.Substring(0, eq).Trim().ToLowerInvariant();
                string value = parameter.Substring(eq + 1).Trim();
                string field;
                object parsed;
                error = ParseValue(scopeKind, key, value, out field, out parsed);
                if (error != null)
                    return error;
                if (field == "url")
                {
                    Uri uri;
                    if (!Uri.TryCreate((string)parsed, UriKind.Absolute, out uri) || !uri.IsLoopback || (uri.Scheme != "http" && uri.Scheme != "https"))
                        return "url must be an http address on this PC (e.g. http://127.0.0.1:8111/).";
                }
                if (field == "graph" && ((string)parsed).Length > 0 && !((string)parsed).EndsWith(".jpffb.json", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("Graph files are named *.jpffb.json; '" + parsed + "' is used as given.");
                scope[field] = parsed;
                changes.Add(scopeName + ": " + field + " = " + Format(parsed));
            }
            if (scopeKind == "effect" && !Effects.Any(e => string.Equals(e[0], effect, StringComparison.OrdinalIgnoreCase)))
                warnings.Add("'" + effect + "' is not an effect of the built-in graph (" + string.Join(", ", Effects.Select(e => e[0])) + "); it only acts on graphs that name an effect so.");
            return null;
        }

        // Forgets a device's own directions (it uses the global invert-x / invert-y again).
        public static string ClearDevice(Dictionary<string, object> config, string device, List<string> changes)
        {
            string key = DeviceKey(device);
            if (key == null)
                return "--device needs the device's instance guid ({...}): " + device;
            if (((Dictionary<string, object>)config["devices"]).Remove(key))
                changes.Add("device " + key + ": removed (the global directions apply).");
            return null;
        }

        // Removes a scope (back to its defaults): --game, --game --plane, --effect; without options everything.
        public static string Clear(Dictionary<string, object> config, string game, string plane, string effect, List<string> changes)
        {
            if (string.IsNullOrWhiteSpace(game) && string.IsNullOrWhiteSpace(plane) && string.IsNullOrWhiteSpace(effect))
            {
                Dictionary<string, object> defaults = Normalize(new Dictionary<string, object>());
                config.Clear();
                foreach (KeyValuePair<string, object> pair in defaults)
                    config[pair.Key] = pair.Value;
                changes.Add("All telemetry force feedback settings back to the defaults.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(game))
                return "config clear --plane/--effect needs --game.";
            GameSpec spec = FindGame(game);
            if (spec == null)
                return UnknownGame(game);
            Dictionary<string, object> games = (Dictionary<string, object>)config["games"];
            if (!games.ContainsKey(spec.Id))
                return null;
            Dictionary<string, object> g = (Dictionary<string, object>)games[spec.Id];
            if (string.IsNullOrWhiteSpace(plane) && string.IsNullOrWhiteSpace(effect))
            {
                games.Remove(spec.Id);
                changes.Add(spec.Id + ": back to the defaults.");
                return null;
            }
            Dictionary<string, object> holder = g;
            string name = spec.Id;
            if (!string.IsNullOrWhiteSpace(plane))
            {
                Dictionary<string, object> aircraft = (Dictionary<string, object>)g["aircraft"];
                if (!aircraft.ContainsKey(plane.Trim()))
                    return null;
                if (string.IsNullOrWhiteSpace(effect))
                {
                    aircraft.Remove(plane.Trim());
                    changes.Add(spec.Id + " / " + plane.Trim() + ": removed.");
                    return null;
                }
                holder = (Dictionary<string, object>)aircraft[plane.Trim()];
                name += " / " + plane.Trim();
            }
            Dictionary<string, object> effects = (Dictionary<string, object>)holder["effects"];
            if (effects.Remove(effect.Trim()))
                changes.Add(name + ": effect " + effect.Trim() + " back to on, strength 1.");
            return null;
        }

        private static string ResolveScope(Dictionary<string, object> config, string game, string plane, string effect, bool create,
            out Dictionary<string, object> scope, out string scopeName, out string scopeKind)
        {
            scope = config;
            scopeName = "global";
            scopeKind = "global";
            bool hasGame = !string.IsNullOrWhiteSpace(game);
            bool hasPlane = !string.IsNullOrWhiteSpace(plane);
            bool hasEffect = !string.IsNullOrWhiteSpace(effect);
            if (!hasGame)
                return hasPlane || hasEffect ? "--plane and --effect need --game." : null;
            GameSpec spec = FindGame(game);
            if (spec == null)
                return UnknownGame(game);
            Dictionary<string, object> games = (Dictionary<string, object>)config["games"];
            if (!games.ContainsKey(spec.Id))
                games[spec.Id] = NormalizeGame(new Dictionary<string, object>());
            scope = (Dictionary<string, object>)games[spec.Id];
            scopeName = spec.Id;
            scopeKind = "game";
            if (hasPlane)
            {
                Dictionary<string, object> aircraft = (Dictionary<string, object>)scope["aircraft"];
                string key = plane.Trim();
                if (!aircraft.ContainsKey(key))
                {
                    Dictionary<string, object> fresh = new Dictionary<string, object>(StringComparer.Ordinal);
                    fresh["graph"] = string.Empty;
                    fresh["strength"] = 1.0;
                    fresh["effects"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    aircraft[key] = fresh;
                }
                scope = (Dictionary<string, object>)aircraft[key];
                scopeName += " / " + key;
                scopeKind = "plane";
            }
            if (hasEffect)
            {
                string name = effect.Trim().ToLowerInvariant();
                if (!Regex.IsMatch(name, "^[a-z0-9_-]{1,40}$"))
                    return "Effect names are letters, digits, '-' and '_': " + effect;
                Dictionary<string, object> effects = (Dictionary<string, object>)scope["effects"];
                if (!effects.ContainsKey(name))
                    effects[name] = new Dictionary<string, object>(StringComparer.Ordinal) { { "enabled", true }, { "strength", 1.0 } };
                scope = (Dictionary<string, object>)effects[name];
                scopeName += " effect " + name;
                scopeKind = "effect";
            }
            return null;
        }

        private static string ParseValue(string scopeKind, string key, string value, out string field, out object parsed)
        {
            field = null;
            parsed = null;
            // key -> (field, type, min, max) per scope
            string type;
            double min = 0;
            double max = 1;
            switch (scopeKind + ":" + key)
            {
                case "global:enabled": field = "enabled"; type = "bool"; break;
                case "global:device": field = "device"; type = "string"; break;
                case "global:device-name": field = "deviceName"; type = "string"; break;
                case "global:strength": field = "strength"; type = "number"; break;
                case "global:max-force": field = "maxForce"; type = "number"; break;
                case "global:invert-x": field = "invertX"; type = "bool"; break;
                case "global:invert-y": field = "invertY"; type = "bool"; break;
                case "global:tick-rate": field = "tickRateHz"; type = "number"; min = 10; max = 200; break;
                case "global:timeout": field = "timeoutSeconds"; type = "number"; min = 0.1; max = 5; break;
                case "global:centring-spring": field = "centringSpring"; type = "bool"; break;
                case "global:centring-stiffness": field = "centringStiffness"; type = "number"; break;
                case "device:invert-x": field = "invertX"; type = "bool"; break;
                case "device:invert-y": field = "invertY"; type = "bool"; break;
                case "game:enabled": field = "enabled"; type = "bool"; break;
                case "game:port": field = "port"; type = "int"; min = 0; max = 65535; break;
                case "game:graph": field = "graph"; type = "string"; break;
                case "game:strength": field = "strength"; type = "number"; max = 2; break;
                case "game:url": field = "url"; type = "string"; break;
                case "game:poll-hz": field = "pollHz"; type = "number"; min = 1; max = 60; break;
                case "plane:graph": field = "graph"; type = "string"; break;
                case "plane:strength": field = "strength"; type = "number"; max = 2; break;
                case "effect:enabled": field = "enabled"; type = "bool"; break;
                case "effect:strength": field = "strength"; type = "number"; max = 2; break;
                default:
                    return "Unknown " + scopeKind + " setting '" + key + "' (" + KeysOf(scopeKind) + ").";
            }
            if (type == "string")
            {
                parsed = value;
                return null;
            }
            if (type == "bool")
            {
                string lower = value.ToLowerInvariant();
                if (lower == "true" || lower == "1" || lower == "on" || lower == "yes")
                    parsed = true;
                else if (lower == "false" || lower == "0" || lower == "off" || lower == "no")
                    parsed = false;
                else
                    return key + " needs true or false.";
                return null;
            }
            double number;
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                return key + " needs a number (" + Num(min) + ".." + Num(max) + ").";
            if (number < min || number > max)
                return key + " must be " + Num(min) + ".." + Num(max) + ".";
            parsed = type == "int" ? (object)(int)Math.Round(number) : number;
            return null;
        }

        private static string KeysOf(string scopeKind)
        {
            switch (scopeKind)
            {
                case "global": return "enabled, device, device-name, strength, max-force, invert-x, invert-y, tick-rate, timeout, centring-spring, centring-stiffness";
                case "device": return "invert-x, invert-y";
                case "game": return "enabled, port, graph, strength, url, poll-hz";
                case "plane": return "graph, strength";
                default: return "enabled, strength";
            }
        }

        private static string UnknownGame(string game)
        {
            return "Telemetry force feedback supports " + string.Join(", ", Games.Select(g => g.Id)) + ", not '" + game + "'.";
        }

        // ------------------------------------------------------------------ status

        public static EngineFfbTelemetryStatus Status(List<string> warnings)
        {
            EngineFfbTelemetryStatus status = new EngineFfbTelemetryStatus();
            status.ConfigPath = ConfigPath();
            status.Config = LoadConfig(warnings);
            Dictionary<string, object> games = (Dictionary<string, object>)status.Config["games"];
            foreach (GameSpec spec in Games)
            {
                EngineFfbTelemetryGame game = new EngineFfbTelemetryGame { Game = spec.Id, Title = spec.Title, ProcessNames = spec.ProcessNames.ToList() };
                game.Running = GameProcessGuard.FindRunning(spec.ProcessNames).Count > 0;
                Dictionary<string, object> settings = games.ContainsKey(spec.Id) ? (Dictionary<string, object>)games[spec.Id] : NormalizeGame(new Dictionary<string, object>());
                int port = PortOf(settings, spec);
                game.Endpoint = spec.Transport == "HTTP" ? Str(settings, "url", "http://127.0.0.1:8111/") : "UDP " + port.ToString(CultureInfo.InvariantCulture);
                EngineFfbTelemetrySetupResult setup = Setup(spec.Id, false, true, null, warnings);
                game.SetupNeeded = spec.Id != "WarThunder";
                game.SetupInstalled = setup.Installed;
                game.SetupDetail = setup.Detail;
                game.SetupFiles = setup.Files;
                status.Games.Add(game);
            }
            foreach (string[] effect in Effects)
                status.Effects.Add(new EngineFfbTelemetryEffect { Name = effect[0], Title = effect[1] });
            Process[] processes = Process.GetProcessesByName("JoyPro.Ffb");
            status.Process.Running = processes.Length > 0;
            status.Process.Pid = processes.Length > 0 ? processes[0].Id : 0;
            foreach (Process process in processes)
                process.Dispose();
            if (File.Exists(StatusPath()))
            {
                try
                {
                    status.LastStatus = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(StatusPath()));
                }
                catch (Exception ex)
                {
                    warnings.Add("Last status not readable: " + ex.Message);
                }
            }
            status.Warnings = warnings;
            return status;
        }

        private static int PortOf(Dictionary<string, object> settings, GameSpec spec)
        {
            int port = (int)Num(settings, "port", 0);
            return port > 0 && port < 65536 ? port : spec.DefaultPort;
        }

        public static int PortFor(string gameId)
        {
            GameSpec spec = FindGame(gameId);
            Dictionary<string, object> config = LoadConfig(new List<string>());
            Dictionary<string, object> games = (Dictionary<string, object>)config["games"];
            return games.ContainsKey(spec.Id) ? PortOf((Dictionary<string, object>)games[spec.Id], spec) : spec.DefaultPort;
        }

        // ------------------------------------------------------------------ game setup

        // DCS: a Hooks script in every Saved Games\DCS* folder (or --path). Hooks are loaded by DCS itself, each in its own
        // environment, so nothing in Export.lua changes and other tools (SRS, Tacview, TelemFFB, DCS-BIOS) are not touched.
        // IL-2: one addrN line in [KEY = telemetrydevice] of data\startup.cfg (the section is created when missing).
        // War Thunder: nothing (its web server on port 8111 is on by default).
        public static EngineFfbTelemetrySetupResult Setup(string gameId, bool remove, bool statusOnly, string path, List<string> warnings, bool dryRun = false)
        {
            GameSpec spec = FindGame(gameId);
            EngineFfbTelemetrySetupResult result = new EngineFfbTelemetrySetupResult
            {
                Game = spec == null ? gameId : spec.Id,
                Action = statusOnly ? "status" : remove ? "remove" : "install",
                DryRun = dryRun
            };
            if (spec == null)
            {
                result.Errors.Add(UnknownGame(gameId));
                return result;
            }
            if (spec.Id == "WarThunder")
            {
                result.Installed = true;
                result.Detail = "Nothing to set up: War Thunder's web server (http://127.0.0.1:8111) is on by default; JoyPro reads /state and /indicators while the game runs.";
                return result;
            }
            if (spec.Id == "DCS")
                return SetupDcs(result, remove, statusOnly, path, dryRun);
            return SetupIl2(result, spec, remove, statusOnly, path, dryRun);
        }

        private static EngineFfbTelemetrySetupResult SetupDcs(EngineFfbTelemetrySetupResult result, bool remove, bool statusOnly, string path, bool dryRun)
        {
            List<string> folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(path))
                folders.Add(path.Trim());
            else
                folders.AddRange(GamePathDiscovery.DiscoverDcsSavedGamesPaths(result.Warnings));
            if (folders.Count == 0)
            {
                result.Detail = "No DCS Saved Games folder found (use --path <Saved Games\\DCS>).";
                if (!statusOnly)
                    result.Errors.Add(result.Detail);
                return result;
            }
            string script = DcsHookScript(PortFor("DCS"));
            List<string> installed = new List<string>();
            List<string> outdated = new List<string>();
            foreach (string folder in folders)
            {
                string hook = Path.Combine(folder, "Scripts", "Hooks", DcsHookFileName);
                result.Files.Add(hook);
                bool exists = File.Exists(hook);
                string current = exists ? File.ReadAllText(hook) : string.Empty;
                if (exists && Normalized(current) == Normalized(script))
                    installed.Add(folder);
                else if (exists)
                    outdated.Add(folder);
                if (statusOnly)
                    continue;
                if (!Directory.Exists(folder))
                {
                    result.Errors.Add("DCS Saved Games folder not found: " + folder);
                    continue;
                }
                if (remove)
                {
                    if (!exists)
                        continue;
                    if (!current.Contains(DcsHookMarker))
                    {
                        result.Warnings.Add(hook + " was not written by JoyPro; left as it is.");
                        continue;
                    }
                    result.Changes.Add("Delete " + hook);
                    if (dryRun)
                        continue;
                    GameBackups.Protect("DCS", hook, result.Warnings);
                    File.Delete(hook);
                    continue;
                }
                if (exists && Normalized(current) == Normalized(script))
                    continue;
                if (exists && !current.Contains(DcsHookMarker))
                {
                    result.Errors.Add(hook + " exists and was not written by JoyPro; not replaced.");
                    continue;
                }
                result.Changes.Add((exists ? "Update " : "Create ") + hook);
                if (dryRun)
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(hook));
                // Also when the file is new: restoring the backup removes it again.
                GameBackups.Protect("DCS", hook, result.Warnings);
                File.WriteAllText(hook, script, new UTF8Encoding(false));
            }
            if (!statusOnly && !dryRun)
            {
                installed.Clear();
                outdated.Clear();
                foreach (string folder in folders)
                {
                    string hook = Path.Combine(folder, "Scripts", "Hooks", DcsHookFileName);
                    if (File.Exists(hook))
                    {
                        if (Normalized(File.ReadAllText(hook)) == Normalized(script))
                            installed.Add(folder);
                        else
                            outdated.Add(folder);
                    }
                }
            }
            result.Installed = installed.Count > 0;
            result.Detail = installed.Count > 0
                ? "Hook installed in " + string.Join(", ", installed) + (outdated.Count > 0 ? "; outdated in " + string.Join(", ", outdated) : string.Empty) + ". DCS loads it at start."
                : outdated.Count > 0 ? "Hook outdated in " + string.Join(", ", outdated) + " (install again)." : "Hook not installed (" + string.Join(", ", folders) + ").";
            return result;
        }

        private static string Normalized(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        }

        // The DCS hook (Saved Games\DCS\Scripts\Hooks\JoyProFfb.lua) is engine\DcsHooks\JoyProFfb.lua, embedded. It runs
        // in DCS's GUI Lua state; the export functions are there as Export.Lo* (API\Sim_ControlAPI.md). It sends at most
        // 100 lines per second of model time plus slower values, session facts and (when the server allows it) the other
        // objects once a second; nothing while paused (model time stands still), "noexport" when a multiplayer server
        // forbids ownship export, "exit" when the mission ends. UDP to 127.0.0.1 only; nothing waits for JoyPro and
        // nothing fails when it is not running. The line protocol: gui/JoyPro.Ffb.Core/Telemetry/Dcs/DcsTelemetry.cs.
        public const string DcsHookResource = "JoyPro.Engine.DcsHooks.JoyProFfb.lua";

        public static string DcsHookScript(int port)
        {
            string template;
            using (Stream stream = typeof(EngineFfbTelemetry).Assembly.GetManifestResourceStream(DcsHookResource))
            {
                if (stream == null)
                    throw new InvalidOperationException("The DCS hook script is missing from the engine (" + DcsHookResource + ").");
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                    template = reader.ReadToEnd();
            }
            return DcsHookScriptFromTemplate(template, port);
        }

        // The template sends to the default port; setup writes the configured one. LF line ends, one final line end.
        public static string DcsHookScriptFromTemplate(string template, int port)
        {
            string p = port.ToString(CultureInfo.InvariantCulture);
            string d = DcsPort.ToString(CultureInfo.InvariantCulture);
            string text = (template ?? string.Empty).Replace("\r\n", "\n")
                .Replace("127.0.0.1:" + d, "127.0.0.1:" + p)
                .Replace("local PORT = " + d, "local PORT = " + p);
            return text.TrimEnd('\n') + "\n";
        }

        private static EngineFfbTelemetrySetupResult SetupIl2(EngineFfbTelemetrySetupResult result, GameSpec spec, bool remove, bool statusOnly, string path, bool dryRun)
        {
            string startup = Il2StartupPath(spec.Id, path, result.Warnings);
            if (string.IsNullOrEmpty(startup))
            {
                result.Detail = spec.Title + " not found (set its folder in Settings > Games, or use --path <game folder or startup.cfg>).";
                if (!statusOnly)
                    result.Errors.Add(result.Detail);
                return result;
            }
            result.Files.Add(startup);
            if (!File.Exists(startup))
            {
                result.Detail = startup + " does not exist (start the game once).";
                if (!statusOnly)
                    result.Errors.Add(result.Detail);
                return result;
            }
            int port = PortFor(spec.Id);
            string text = File.ReadAllText(startup);
            Il2TelemetrySection state = Il2TelemetrySection.Read(text, port);
            if (statusOnly)
            {
                result.Installed = state.HasOurTarget && state.Enabled;
                result.Detail = !state.Exists ? "No [KEY = telemetrydevice] section in " + startup + "."
                    : state.HasOurTarget ? (state.Enabled ? "Telemetry goes to 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + " (decimation " + state.Decimation + ")." : "JoyPro's target is set but the telemetry output is disabled.")
                    : "The telemetry output does not go to JoyPro (port " + port.ToString(CultureInfo.InvariantCulture) + ").";
                return result;
            }

            Dictionary<string, Dictionary<string, object>> memory = LoadIl2State();
            string key = Path.GetFullPath(startup).ToLowerInvariant();
            string changed = text;
            List<string> changes = new List<string>();
            if (remove)
            {
                Dictionary<string, object> record = memory.ContainsKey(key) ? memory[key] : null;
                changed = Il2TelemetrySection.Remove(text, port, record, changes);
                memory.Remove(key);
            }
            else
            {
                Dictionary<string, object> record = memory.ContainsKey(key) ? memory[key] : new Dictionary<string, object>();
                changed = Il2TelemetrySection.Install(text, port, record, changes);
                memory[key] = record;
            }
            result.Changes.AddRange(changes.Select(c => c + " (" + startup + ")"));
            if (!dryRun && changed != text)
            {
                GameBackups.Protect(spec.Id, startup, result.Warnings);
                File.WriteAllText(startup, changed, new UTF8Encoding(false));
            }
            if (!dryRun)
                SaveIl2State(memory);
            Il2TelemetrySection after = Il2TelemetrySection.Read(dryRun ? text : changed, port);
            result.Installed = after.HasOurTarget && after.Enabled;
            result.Detail = result.Installed ? "IL-2 sends telemetry to 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "; restart the game if it runs." : "JoyPro's telemetry target is not set.";
            if (!remove && after.Decimation > 2)
                result.Warnings.Add("decimation = " + after.Decimation + " in [KEY = telemetrydevice]: only " + (50 / after.Decimation) + " updates per second; 1 or 2 is better for force feedback.");
            return result;
        }

        private static string Il2StartupPath(string gameId, string path, List<string> warnings)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string trimmed = path.Trim();
                if (trimmed.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
                return Path.Combine(trimmed, "data", "startup.cfg");
            }
            GameInstall install = GameInstalls.Find(gameId, warnings);
            if (install == null || string.IsNullOrWhiteSpace(install.InstallPath) || !install.Installed)
                return string.Empty;
            return Path.Combine(install.InstallPath, "data", "startup.cfg");
        }

        private static Dictionary<string, Dictionary<string, object>> LoadIl2State()
        {
            Dictionary<string, Dictionary<string, object>> state = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(Il2StatePath()))
                {
                    Dictionary<string, object> raw = EngineSerialization.FromJson<Dictionary<string, object>>(File.ReadAllText(Il2StatePath()));
                    foreach (KeyValuePair<string, object> pair in raw)
                    {
                        Dictionary<string, object> record = pair.Value as Dictionary<string, object>;
                        if (record != null)
                            state[pair.Key] = record;
                    }
                }
            }
            catch (Exception)
            {
            }
            return state;
        }

        private static void SaveIl2State(Dictionary<string, Dictionary<string, object>> state)
        {
            Directory.CreateDirectory(Folder());
            File.WriteAllText(Il2StatePath(), IndentJson(EngineSerialization.ToJson(state)) + Environment.NewLine, new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ text output

        public static string ToText(EngineFfbTelemetryStatus status)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<string, object> c = status.Config;
            sb.AppendLine("Telemetry force feedback: " + ((bool)c["enabled"] ? "on" : "off"));
            sb.AppendLine("Settings: " + status.ConfigPath);
            sb.AppendLine("Device: " + (Str(c, "device", string.Empty).Length == 0 ? "<first force feedback stick>" : Str(c, "deviceName", string.Empty) + " " + Str(c, "device", string.Empty)));
            sb.AppendLine("Strength: " + Pct((double)c["strength"]) + ", force cap: " + Pct((double)c["maxForce"]) + (Bool(c, "invertX", false) ? ", X inverted" : string.Empty) + (Bool(c, "invertY", false) ? ", Y inverted" : string.Empty));
            Dictionary<string, object> games = (Dictionary<string, object>)c["games"];
            foreach (EngineFfbTelemetryGame game in status.Games)
            {
                Dictionary<string, object> g = games.ContainsKey(game.Game) ? (Dictionary<string, object>)games[game.Game] : null;
                sb.AppendLine("  " + game.Title + ": " + (g == null || Bool(g, "enabled", true) ? "on" : "off") + ", " + game.Endpoint + (game.Running ? ", running" : string.Empty));
                sb.AppendLine("    setup: " + game.SetupDetail);
                if (g == null)
                    continue;
                if (Str(g, "graph", string.Empty).Length > 0)
                    sb.AppendLine("    graph: " + Str(g, "graph", string.Empty));
                if (Num(g, "strength", 1) != 1)
                    sb.AppendLine("    strength: " + Pct(Num(g, "strength", 1)));
                foreach (KeyValuePair<string, object> e in (Dictionary<string, object>)g["effects"])
                    sb.AppendLine("    effect " + e.Key + ": " + EffectText((Dictionary<string, object>)e.Value));
                foreach (KeyValuePair<string, object> plane in (Dictionary<string, object>)g["aircraft"])
                {
                    Dictionary<string, object> p = (Dictionary<string, object>)plane.Value;
                    sb.AppendLine("    aircraft " + plane.Key + ": " + (Str(p, "graph", string.Empty).Length > 0 ? "graph " + Str(p, "graph", string.Empty) + ", " : string.Empty) + "strength " + Pct(Num(p, "strength", 1)));
                    foreach (KeyValuePair<string, object> e in (Dictionary<string, object>)p["effects"])
                        sb.AppendLine("      effect " + e.Key + ": " + EffectText((Dictionary<string, object>)e.Value));
                }
            }
            sb.AppendLine("Process: " + (status.Process.Running ? "JoyPro.Ffb runs (pid " + status.Process.Pid.ToString(CultureInfo.InvariantCulture) + ")" : "not running"));
            if (status.LastStatus != null)
                sb.AppendLine("Last status: " + Str(status.LastStatus, "game", string.Empty) + " " + Str(status.LastStatus, "state", string.Empty) + " - " + Str(status.LastStatus, "reason", string.Empty));
            foreach (string warning in status.Warnings)
                sb.AppendLine("  warning: " + warning);
            return sb.ToString().TrimEnd();
        }

        public static string ToText(EngineFfbTelemetrySetupResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Telemetry force feedback setup: " + result.Game + " (" + result.Action + (result.DryRun ? ", dry run" : string.Empty) + ")");
            sb.AppendLine("Installed: " + (result.Installed ? "yes" : "no"));
            if (!string.IsNullOrEmpty(result.Detail))
                sb.AppendLine(result.Detail);
            foreach (string change in result.Changes)
                sb.AppendLine("  * " + change);
            if (result.Changes.Count == 0 && result.Action != "status")
                sb.AppendLine("  (nothing to change)");
            foreach (string warning in result.Warnings)
                sb.AppendLine("  warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("  error: " + error);
            return sb.ToString().TrimEnd();
        }

        private static string EffectText(Dictionary<string, object> e)
        {
            return (Bool(e, "enabled", true) ? "on" : "off") + ", strength " + Pct(Num(e, "strength", 1));
        }

        private static string Pct(double value)
        {
            return Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + " %";
        }

        // ------------------------------------------------------------------ small JSON helpers

        private static Dictionary<string, object> Obj(Dictionary<string, object> json, string name)
        {
            object value = Get(json, name);
            return value as Dictionary<string, object>;
        }

        private static object Get(Dictionary<string, object> json, string name)
        {
            if (json == null)
                return null;
            object value;
            if (json.TryGetValue(name, out value))
                return value;
            foreach (KeyValuePair<string, object> pair in json)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static double Num(Dictionary<string, object> json, string name, double fallback)
        {
            object value = Get(json, name);
            if (value == null || value is bool)
                return fallback;
            double number;
            if (value is string)
                return double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : fallback;
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsNaN(number) || double.IsInfinity(number) ? fallback : number;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool Bool(Dictionary<string, object> json, string name, bool fallback)
        {
            object value = Get(json, name);
            if (value is bool)
                return (bool)value;
            bool parsed;
            return value is string && bool.TryParse((string)value, out parsed) ? parsed : fallback;
        }

        private static string Str(Dictionary<string, object> json, string name, string fallback)
        {
            object value = Get(json, name);
            return value as string ?? fallback;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Format(object value)
        {
            if (value is double)
                return Num((double)value);
            if (value is bool)
                return (bool)value ? "true" : "false";
            if (value is string && ((string)value).Length == 0)
                return "\"\"";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // Pretty-prints compact JSON (two spaces per level).
        public static string IndentJson(string json)
        {
            StringBuilder sb = new StringBuilder(json.Length * 2);
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                        sb.Append(json[++i]);
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        char close = c == '{' ? '}' : ']';
                        if (i + 1 < json.Length && json[i + 1] == close)
                        {
                            sb.Append(c).Append(close);
                            i++;
                            break;
                        }
                        depth++;
                        sb.Append(c).Append('\n').Append(' ', depth * 2);
                        break;
                    case '}':
                    case ']':
                        depth--;
                        sb.Append('\n').Append(' ', depth * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', depth * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString().Replace("\n", Environment.NewLine);
        }
    }

    // [KEY = telemetrydevice] ... [END] in an IL-2 startup.cfg (Doc\UDP_Output\Il2_TeleOutput, rev. 1.2): addr = "IP[:port]",
    // further targets as addr1, addr2 ...; decimation (50 Hz / n); enable; port. JoyPro adds its own addrN line and never
    // changes the addresses of other tools. What it changed is remembered in <data>\Ffb\Telemetry\il2-setup.json so the
    // removal restores it (created section, added line, enable switched on).
    internal sealed class Il2TelemetrySection
    {
        public const string Header = "[KEY = telemetrydevice]";

        public bool Exists;
        public bool Enabled;
        public bool HasOurTarget;
        public int Decimation = 2;

        public static Il2TelemetrySection Read(string text, int port)
        {
            Il2TelemetrySection section = new Il2TelemetrySection();
            List<string> lines = Lines(text);
            int start, end;
            if (!Find(lines, out start, out end))
                return section;
            section.Exists = true;
            for (int i = start + 1; i < end; i++)
            {
                string k, v;
                if (!KeyValue(lines[i], out k, out v))
                    continue;
                if (k == "enable")
                    section.Enabled = v == "true" || v == "1";
                else if (k == "decimation")
                {
                    int d;
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out d) && d > 0)
                        section.Decimation = d;
                }
                else if (k.StartsWith("addr", StringComparison.Ordinal) && IsOurTarget(v, port))
                    section.HasOurTarget = true;
            }
            return section;
        }

        public static string Install(string text, int port, Dictionary<string, object> record, List<string> changes)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = Lines(text);
            string target = "\"127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\"";
            int start, end;
            if (!Find(lines, out start, out end))
            {
                int insert = lines.Count;
                while (insert > 0 && lines[insert - 1].Trim().Length == 0)
                    insert--;
                List<string> block = new List<string>
                {
                    Header,
                    "\taddr = " + target,
                    "\tdecimation = 1",
                    "\tenable = true",
                    "\tport = " + port.ToString(CultureInfo.InvariantCulture),
                    "[END]"
                };
                if (insert > 0)
                    block.Insert(0, string.Empty);
                lines.InsertRange(insert, block);
                record["createdSection"] = true;
                changes.Add("Add [KEY = telemetrydevice] sending to 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + " at 50 Hz");
                return Join(lines, nl, text);
            }

            bool ours = false;
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            int lastAddr = start;
            int enableLine = -1;
            string enableValue = null;
            string indent = "\t";
            for (int i = start + 1; i < end; i++)
            {
                string k, v;
                if (!KeyValue(lines[i], out k, out v))
                    continue;
                indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
                if (k.StartsWith("addr", StringComparison.Ordinal))
                {
                    used.Add(k);
                    lastAddr = i;
                    if (IsOurTarget(v, port))
                        ours = true;
                }
                else if (k == "enable")
                {
                    enableLine = i;
                    enableValue = v;
                }
            }
            if (!ours)
            {
                int n = 1;
                while (used.Contains("addr" + n.ToString(CultureInfo.InvariantCulture)))
                    n++;
                string key = "addr" + n.ToString(CultureInfo.InvariantCulture);
                lines.Insert(lastAddr + 1, indent + key + " = " + target);
                end++;
                if (enableLine > lastAddr)
                    enableLine++;
                record["addedKey"] = key;
                changes.Add("Add " + key + " = " + target + " to [KEY = telemetrydevice] (the other targets stay)");
            }
            if (enableLine < 0)
            {
                lines.Insert(end, indent + "enable = true");
                record["addedEnable"] = true;
                changes.Add("Add enable = true to [KEY = telemetrydevice]");
            }
            else if (enableValue != "true" && enableValue != "1")
            {
                if (!record.ContainsKey("enableWas"))
                    record["enableWas"] = enableValue;
                lines[enableLine] = lines[enableLine].Substring(0, lines[enableLine].Length - lines[enableLine].TrimStart().Length) + "enable = true";
                changes.Add("Switch the telemetry output on (enable = true, was " + enableValue + ")");
            }
            return Join(lines, nl, text);
        }

        public static string Remove(string text, int port, Dictionary<string, object> record, List<string> changes)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = Lines(text);
            int start, end;
            if (!Find(lines, out start, out end))
                return text;
            bool created = record != null && record.ContainsKey("createdSection") && record["createdSection"] is bool && (bool)record["createdSection"];
            List<int> ourLines = new List<int>();
            int otherTargets = 0;
            for (int i = start + 1; i < end; i++)
            {
                string k, v;
                if (!KeyValue(lines[i], out k, out v) || !k.StartsWith("addr", StringComparison.Ordinal))
                    continue;
                if (IsOurTarget(v, port))
                    ourLines.Add(i);
                else
                    otherTargets++;
            }
            if (created && otherTargets == 0)
            {
                int from = start;
                if (from > 0 && lines[from - 1].Trim().Length == 0)
                    from--;
                lines.RemoveRange(from, end - from + 1);
                changes.Add("Remove [KEY = telemetrydevice] (JoyPro had added it)");
                return Join(lines, nl, text);
            }
            for (int i = ourLines.Count - 1; i >= 0; i--)
            {
                changes.Add("Remove " + lines[ourLines[i]].Trim() + " from [KEY = telemetrydevice]");
                lines.RemoveAt(ourLines[i]);
                end--;
            }
            object enableWas;
            if (record != null && record.TryGetValue("enableWas", out enableWas) && enableWas is string)
            {
                for (int i = start + 1; i < end; i++)
                {
                    string k, v;
                    if (KeyValue(lines[i], out k, out v) && k == "enable")
                    {
                        lines[i] = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length) + "enable = " + (string)enableWas;
                        changes.Add("Switch the telemetry output back to enable = " + (string)enableWas);
                        break;
                    }
                }
            }
            return Join(lines, nl, text);
        }

        private static bool IsOurTarget(string value, int port)
        {
            string v = value.Trim().Trim('"').Trim();
            return v == "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) || v == "localhost:" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static bool Find(List<string> lines, out int start, out int end)
        {
            start = lines.FindIndex(l => l.Trim().Equals(Header, StringComparison.OrdinalIgnoreCase));
            end = -1;
            if (start < 0)
                return false;
            int s = start;
            end = lines.FindIndex(s + 1, l => l.Trim().Equals("[END]", StringComparison.OrdinalIgnoreCase));
            return end > start;
        }

        private static bool KeyValue(string line, out string key, out string value)
        {
            key = value = null;
            string trimmed = line.Trim();
            int comment = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && trimmed.IndexOf('"') < 0)
                trimmed = trimmed.Substring(0, comment).Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
                return false;
            key = trimmed.Substring(0, eq).Trim().ToLowerInvariant();
            value = trimmed.Substring(eq + 1).Trim();
            int slash = value.IndexOf("//", StringComparison.Ordinal);
            if (slash >= 0 && (value.IndexOf('"') < 0 || slash > value.LastIndexOf('"')))
                value = value.Substring(0, slash).Trim();
            return true;
        }

        private static List<string> Lines(string text)
        {
            return text.Replace("\r\n", "\n").Split('\n').ToList();
        }

        private static string Join(List<string> lines, string nl, string original)
        {
            string joined = string.Join(nl, lines);
            return joined;
        }
    }

    public sealed class EngineFfbTelemetryStatus
    {
        public string ConfigPath { get; set; }
        public Dictionary<string, object> Config { get; set; }
        public List<EngineFfbTelemetryGame> Games { get; set; }
        public List<EngineFfbTelemetryEffect> Effects { get; set; }
        public EngineFfbTelemetryProcess Process { get; set; }
        public Dictionary<string, object> LastStatus { get; set; }
        public List<string> Warnings { get; set; }

        public EngineFfbTelemetryStatus()
        {
            ConfigPath = string.Empty;
            Games = new List<EngineFfbTelemetryGame>();
            Effects = new List<EngineFfbTelemetryEffect>();
            Process = new EngineFfbTelemetryProcess();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineFfbTelemetryGame
    {
        public string Game { get; set; }
        public string Title { get; set; }
        public List<string> ProcessNames { get; set; }
        public bool Running { get; set; }
        public bool SetupNeeded { get; set; }
        public bool SetupInstalled { get; set; }
        public string SetupDetail { get; set; }
        public List<string> SetupFiles { get; set; }
        public string Endpoint { get; set; }
    }

    public sealed class EngineFfbTelemetryEffect
    {
        public string Name { get; set; }
        public string Title { get; set; }
    }

    public sealed class EngineFfbTelemetryProcess
    {
        public bool Running { get; set; }
        public int Pid { get; set; }
    }

    public sealed class EngineFfbTelemetrySetupResult
    {
        public string Game { get; set; }
        public string Action { get; set; }
        public bool DryRun { get; set; }
        public bool Installed { get; set; }
        public string Detail { get; set; }
        public List<string> Files { get; set; }
        public List<string> Changes { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineFfbTelemetrySetupResult()
        {
            Game = Action = Detail = string.Empty;
            Files = new List<string>();
            Changes = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
