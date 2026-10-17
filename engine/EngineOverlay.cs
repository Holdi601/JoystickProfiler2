using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // The overlay (the GUI shows what a pressed button does in the game that runs):
    //   engine overlay map [--game <id>] [--plane <name>] [--unit <DCS unit type>]
    //       every bound button of the profile for that game / aircraft, with its modifiers and the command title;
    //   engine overlay dcs-hook status|install|remove [--path <Saved Games\DCS>] [--dry-run]
    //       a small Export.lua addition that sends the aircraft DCS flies to JoyPro (UDP 127.0.0.1:1992).
    internal static class EngineOverlay
    {
        public const int DcsPort = 1992;
        private const string HookFileName = "JoyProOverlay.lua";
        private const string HookMarker = "-- JoyPro2 overlay";

        public static EngineOverlayMap Map(string game, string plane, string unit, List<string> warnings)
        {
            EngineOverlayMap map = new EngineOverlayMap();
            map.Game = string.IsNullOrWhiteSpace(game) ? string.Empty : GameKey(game);
            map.DcsPlanes = DcsPlanesOfProfile();
            map.Plane = (plane ?? string.Empty).Trim();
            if (map.Plane.Length == 0 && !string.IsNullOrWhiteSpace(unit))
                map.Plane = ResolveDcsPlane(unit.Trim(), map.DcsPlanes, warnings);
            if (map.Plane.Length > 0 && map.Game.Length == 0)
                map.Game = "DCS";
            map.ProcessNames = ProcessNames();

            Dictionary<string, string> titles = Titles(map.Game, map.Plane, warnings);
            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
            {
                Relation relation = entry.Value;
                if (relation == null || relation.ISAXIS || relation.NODES == null)
                    continue;
                Bind bind = relation.bind;
                if (bind == null && InternalDataManagement.AllBinds.ContainsKey(entry.Key))
                    bind = InternalDataManagement.AllBinds[entry.Key];
                if (bind == null || string.IsNullOrWhiteSpace(bind.Joystick) || string.IsNullOrWhiteSpace(bind.JButton))
                    continue;

                RelationItem node = null;
                if (map.Game.Length > 0)
                {
                    node = relation.NODES.FirstOrDefault(n => n != null && GameKey(n.Game) == map.Game &&
                                                              (map.Game != "DCS" || map.Plane.Length == 0 || n.GetActiveAircraftList().Contains(map.Plane, StringComparer.OrdinalIgnoreCase)));
                    if (node == null)
                        continue;
                }

                string title;
                titles.TryGetValue(node == null ? string.Empty : node.ID ?? string.Empty, out title);
                string relationName = string.IsNullOrWhiteSpace(relation.NAME) ? entry.Key : relation.NAME;
                map.Entries.Add(new EngineOverlayEntry
                {
                    Relation = relationName,
                    Title = string.IsNullOrWhiteSpace(title) || string.Equals(title, relationName, StringComparison.OrdinalIgnoreCase) ? string.Empty : title,
                    Device = bind.Joystick.Trim(),
                    Input = bind.JButton.Trim(),
                    Modifiers = Modifiers(bind)
                });
            }
            return map;
        }

        // The DCS unit type (LoGetSelfData().Name, e.g. "A-10C_2") as JoyPro's aircraft name ("A-10C II"): the same name
        // in the profile, else an aircraft of the same module (entry.lua: make_flyable and InputProfiles), else a prefix.
        public static string ResolveDcsPlane(string unit, List<string> profilePlanes, List<string> warnings)
        {
            string exact = profilePlanes.FirstOrDefault(p => string.Equals(p, unit, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
            try
            {
                List<EngineMediaAircraft> aircraft = EngineMedia.Aircraft(warnings);
                EngineMediaAircraft self = aircraft.FirstOrDefault(a => string.Equals(a.Plane, unit, StringComparison.OrdinalIgnoreCase));
                if (self != null && self.Module.Length > 0)
                {
                    string sibling = aircraft
                        .Where(a => string.Equals(a.Module, self.Module, StringComparison.OrdinalIgnoreCase))
                        .Select(a => profilePlanes.FirstOrDefault(p => string.Equals(p, a.Plane, StringComparison.OrdinalIgnoreCase)))
                        .Where(p => p != null)
                        .OrderBy(SeatRank)
                        .FirstOrDefault();
                    if (sibling != null)
                        return sibling;
                }
            }
            catch (Exception ex)
            {
                warnings.Add("DCS aircraft names: " + ex.Message);
            }
            string prefix = profilePlanes
                .Where(p => p.StartsWith(unit, StringComparison.OrdinalIgnoreCase) || unit.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                .OrderBy(SeatRank)
                .ThenByDescending(p => unit.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? p.Length : 0)
                .ThenBy(p => p.Length)
                .FirstOrDefault();
            return prefix ?? unit;
        }

        // The front seat first: LoGetSelfData() names the unit, not the seat (the overlay lets the user pick another one).
        private static int SeatRank(string plane)
        {
            if (Regex.IsMatch(plane, @"(_easy$|AI_?Menu|Menu$|TrackIR)", RegexOptions.IgnoreCase))
                return 3;
            if (Regex.IsMatch(plane, @"(CPG|RIO|WSO|Gunner|GNR|Operator|Copilot|Rear|NAV|_OP|Aiming|Station)", RegexOptions.IgnoreCase))
                return 2;
            if (Regex.IsMatch(plane, @"(Pilot|PLT)", RegexOptions.IgnoreCase))
                return 0;
            return 1;
        }

        private static List<string> DcsPlanesOfProfile()
        {
            return InternalDataManagement.AllRelations.Values
                .Where(r => r != null && r.NODES != null)
                .SelectMany(r => r.NODES)
                .Where(n => n != null && GameKey(n.Game) == "DCS")
                .SelectMany(n => n.GetActiveAircraftList())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Command titles of the game (DCS: of the aircraft), by id.
        private static Dictionary<string, string> Titles(string game, string plane, List<string> warnings)
        {
            Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (game == "DCS" && plane.Length > 0)
                {
                    string file = GameDataPaths.DbFile("DCS", plane + ".html");
                    if (File.Exists(file))
                    {
                        foreach (DCSInput input in InternalDataManagement.ParseDcsHtmlInputs(file, plane))
                        {
                            if (!string.IsNullOrWhiteSpace(input.ID) && !titles.ContainsKey(input.ID))
                                titles[input.ID] = input.Title ?? string.Empty;
                        }
                    }
                }
                else if (game.Length > 0 && game != "DCS")
                {
                    IGameIntegration integration;
                    if (GameRegistry.TryResolve(game, out integration))
                    {
                        foreach (GameAction action in GameCatalogStore.Load(integration, warnings).Actions)
                            titles[action.Id] = action.Title;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Command titles: " + ex.Message);
            }
            return titles;
        }

        // "name§device§key§switch" (device and key may be empty: then the profile's modifier list knows them).
        private static List<EngineOverlayModifier> Modifiers(Bind bind)
        {
            List<EngineOverlayModifier> modifiers = new List<EngineOverlayModifier>();
            foreach (string raw in bind.AllReformers ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string[] parts = raw.Split('§');
                string name = parts[0].Trim();
                if (name.Length == 0)
                    continue;
                Modifier known = null;
                if (InternalDataManagement.AllModifiers != null && InternalDataManagement.AllModifiers.ContainsKey(name))
                    known = InternalDataManagement.AllModifiers[name];
                string device = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : known?.device ?? string.Empty;
                string key = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : known?.key ?? string.Empty;
                bool latched = parts.Length > 3 ? string.Equals(parts[3].Trim(), "true", StringComparison.OrdinalIgnoreCase) : known != null && known.sw;
                modifiers.Add(new EngineOverlayModifier { Name = name, Device = device, Input = key, Switch = latched });
            }
            return modifiers;
        }

        private static Dictionary<string, List<string>> ProcessNames()
        {
            Dictionary<string, List<string>> names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "DCS", new List<string> { "DCS" } },
                { "IL2Game", new List<string> { "Il-2" } },
                { "StarCitizen", new List<string> { "StarCitizen" } }
            };
            foreach (IGameIntegration game in GameRegistry.All)
                names[game.GameId] = game.ProcessNames.ToList();
            return names;
        }

        private static string GameKey(string game)
        {
            if (string.IsNullOrWhiteSpace(game))
                return "DCS";
            string trimmed = game.Trim();
            if (trimmed.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                return "DCS";
            if (trimmed.Equals("IL2Game", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("il2", StringComparison.OrdinalIgnoreCase))
                return "IL2Game";
            if (trimmed.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("sc", StringComparison.OrdinalIgnoreCase))
                return "StarCitizen";
            IGameIntegration integration;
            return GameRegistry.TryResolve(trimmed, out integration) ? integration.GameId : trimmed;
        }

        public static EngineOverlayHookResult Hook(string action, string savedGames, bool dryRun)
        {
            EngineOverlayHookResult result = new EngineOverlayHookResult { Action = action, DryRun = dryRun };
            string scripts = Path.Combine(savedGames, "Scripts");
            string export = Path.Combine(scripts, "Export.lua");
            string hook = Path.Combine(scripts, HookFileName);
            result.ExportLua = export;
            result.HookFile = hook;
            string exportText = File.Exists(export) ? File.ReadAllText(export) : string.Empty;
            bool lineThere = exportText.Split('\n').Any(l => l.Contains(HookMarker));
            result.Installed = lineThere && File.Exists(hook);

            if (action == "status")
                return result;

            if (action == "install")
            {
                if (!Directory.Exists(savedGames))
                {
                    result.Errors.Add("DCS Saved Games folder not found: " + savedGames);
                    return result;
                }
                result.Changes.Add((File.Exists(hook) ? "Update " : "Create ") + hook);
                if (!lineThere)
                    result.Changes.Add((File.Exists(export) ? "Add one line to " : "Create ") + export);
                if (dryRun)
                    return result;
                Directory.CreateDirectory(scripts);
                // Also when the files are new: restoring the backup then removes them again.
                GameBackups.Protect("DCS", hook, result.Warnings);
                File.WriteAllText(hook, HookScript(), new UTF8Encoding(false));
                if (!lineThere)
                {
                    GameBackups.Protect("DCS", export, result.Warnings);
                    string separator = exportText.Length == 0 || exportText.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : Environment.NewLine;
                    File.AppendAllText(export, separator + ExportLine() + Environment.NewLine, new UTF8Encoding(false));
                }
                result.Installed = true;
                return result;
            }

            if (action == "remove")
            {
                if (lineThere)
                    result.Changes.Add("Remove the JoyPro line from " + export);
                if (File.Exists(hook))
                    result.Changes.Add("Delete " + hook);
                if (dryRun)
                    return result;
                if (lineThere)
                {
                    GameBackups.Protect("DCS", export, result.Warnings);
                    string newLine = exportText.Contains("\r\n") ? "\r\n" : "\n";
                    List<string> kept = exportText.Replace("\r\n", "\n").Split('\n').Where(l => !l.Contains(HookMarker)).ToList();
                    File.WriteAllText(export, string.Join(newLine, kept), new UTF8Encoding(false));
                }
                if (File.Exists(hook))
                {
                    GameBackups.Protect("DCS", hook, result.Warnings);
                    File.Delete(hook);
                }
                result.Installed = false;
                return result;
            }

            result.Errors.Add("engine overlay dcs-hook status|install|remove");
            return result;
        }

        // pcall: a missing or broken hook never stops the other Export.lua scripts (SRS, Tacview, ...).
        private static string ExportLine()
        {
            return "pcall(function() local lfs = require('lfs'); dofile(lfs.writedir() .. [[Scripts\\" + HookFileName + "]]) end) " + HookMarker;
        }

        // Sends "plane <unit type>" when the aircraft changes (and every 5 s, for a JoyPro started later) and "exit" when
        // the mission ends. UDP: nothing waits for JoyPro, nothing fails when it is not running. The previous export
        // callbacks are kept and called.
        private static string HookScript()
        {
            return string.Join("\n", new[]
            {
                HookMarker + ": tells JoyPro which aircraft you fly, for its overlay (UDP 127.0.0.1:" + DcsPort.ToString(CultureInfo.InvariantCulture) + ").",
                "-- Remove it in JoyPro (Overlay, Remove DCS hook) or delete this file and the JoyPro line in Export.lua.",
                "pcall(function()",
                "    package.path = package.path .. \";.\\\\LuaSocket\\\\?.lua\"",
                "    package.cpath = package.cpath .. \";.\\\\LuaSocket\\\\?.dll\"",
                "    local socket = require(\"socket\")",
                "    local udp = socket.udp()",
                "    udp:settimeout(0)",
                "    local last, lastSent = nil, -100",
                "    local function send(text)",
                "        pcall(function() udp:sendto(text, \"127.0.0.1\", " + DcsPort.ToString(CultureInfo.InvariantCulture) + ") end)",
                "    end",
                "    local previousAfter = LuaExportAfterNextFrame",
                "    local previousStop = LuaExportStop",
                "    LuaExportAfterNextFrame = function()",
                "        pcall(function()",
                "            local data = LoGetSelfData()",
                "            local name = data and data.Name or nil",
                "            local now = LoGetModelTime and LoGetModelTime() or 0",
                "            if name and (name ~= last or now - lastSent > 5) then",
                "                send(\"plane \" .. name)",
                "                last = name",
                "                lastSent = now",
                "            end",
                "        end)",
                "        if previousAfter then previousAfter() end",
                "    end",
                "    LuaExportStop = function()",
                "        send(\"exit\")",
                "        if previousStop then previousStop() end",
                "    end",
                "end)",
                string.Empty
            });
        }

        public static string ToText(EngineOverlayMap map)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Overlay Map");
            sb.AppendLine("Game: " + (map.Game.Length == 0 ? "<all>" : map.Game) + (map.Plane.Length > 0 ? ", aircraft: " + map.Plane : string.Empty));
            sb.AppendLine("Buttons: " + map.Entries.Count.ToString(CultureInfo.InvariantCulture));
            foreach (EngineOverlayEntry entry in map.Entries)
            {
                string modifiers = entry.Modifiers.Count == 0 ? string.Empty : " + " + string.Join(" + ", entry.Modifiers.Select(m => m.Name));
                sb.AppendLine("  - " + entry.Device + " " + entry.Input + modifiers + ": " + entry.Relation + (entry.Title.Length > 0 ? " (" + entry.Title + ")" : string.Empty));
            }
            return sb.ToString().TrimEnd();
        }

        public static string ToText(EngineOverlayHookResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DCS Overlay Hook" + (result.DryRun ? " (dry run)" : string.Empty));
            sb.AppendLine("Installed: " + (result.Installed ? "yes" : "no"));
            sb.AppendLine("Export.lua: " + result.ExportLua);
            sb.AppendLine("Hook: " + result.HookFile);
            foreach (string change in result.Changes)
                sb.AppendLine("  * " + change);
            foreach (string warning in result.Warnings)
                sb.AppendLine("  warning: " + warning);
            foreach (string error in result.Errors)
                sb.AppendLine("  error: " + error);
            return sb.ToString().TrimEnd();
        }
    }

    public sealed class EngineOverlayMap
    {
        public string Game { get; set; }
        public string Plane { get; set; }
        public List<string> DcsPlanes { get; set; }
        public Dictionary<string, List<string>> ProcessNames { get; set; }
        public List<EngineOverlayEntry> Entries { get; set; }
        public List<string> Warnings { get; set; }

        public EngineOverlayMap()
        {
            Game = Plane = string.Empty;
            DcsPlanes = new List<string>();
            ProcessNames = new Dictionary<string, List<string>>();
            Entries = new List<EngineOverlayEntry>();
            Warnings = new List<string>();
        }
    }

    public sealed class EngineOverlayEntry
    {
        public string Relation { get; set; }
        public string Title { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public List<EngineOverlayModifier> Modifiers { get; set; }

        public EngineOverlayEntry()
        {
            Relation = Title = Device = Input = string.Empty;
            Modifiers = new List<EngineOverlayModifier>();
        }
    }

    public sealed class EngineOverlayModifier
    {
        public string Name { get; set; }
        public string Device { get; set; }
        public string Input { get; set; }
        public bool Switch { get; set; }

        public EngineOverlayModifier()
        {
            Name = Device = Input = string.Empty;
        }
    }

    public sealed class EngineOverlayHookResult
    {
        public string Action { get; set; }
        public bool DryRun { get; set; }
        public bool Installed { get; set; }
        public string ExportLua { get; set; }
        public string HookFile { get; set; }
        public List<string> Changes { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }

        public EngineOverlayHookResult()
        {
            Action = ExportLua = HookFile = string.Empty;
            Changes = new List<string>();
            Warnings = new List<string>();
            Errors = new List<string>();
        }
    }
}
