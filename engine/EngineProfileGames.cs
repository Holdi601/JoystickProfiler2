using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JoyPro.Engine.Games;

namespace JoyPro.Engine
{
    // engine profile games: the games (and for DCS the aircraft) the loaded profile has relations for, with how many of
    // them are bound, i.e. what an export to each game writes. The GUI shows this as the list of export tiles.
    internal static class EngineProfileGames
    {
        public static EngineProfileGamesResult Build()
        {
            Dictionary<string, EngineProfileGame> games = new Dictionary<string, EngineProfileGame>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, EngineProfilePlane>> planes = new Dictionary<string, Dictionary<string, EngineProfilePlane>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, Relation> entry in InternalDataManagement.AllRelations)
            {
                Relation relation = entry.Value;
                if (relation == null || relation.NODES == null || relation.NODES.Count == 0)
                    continue;
                bool bound = IsBound(entry.Key, relation);
                HashSet<string> countedGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> countedPlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RelationItem node in relation.NODES)
                {
                    if (node == null || string.IsNullOrWhiteSpace(node.ID))
                        continue;
                    string gameId = GameIdOf(node.Game);
                    if (!games.TryGetValue(gameId, out EngineProfileGame game))
                    {
                        game = new EngineProfileGame { GameId = gameId, Name = DisplayNameOf(gameId), Exporter = ExporterOf(gameId) };
                        games[gameId] = game;
                        planes[gameId] = new Dictionary<string, EngineProfilePlane>(StringComparer.OrdinalIgnoreCase);
                    }
                    if (countedGames.Add(gameId))
                    {
                        game.Relations++;
                        if (bound)
                            game.Bound++;
                    }
                    if (!gameId.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                        continue;
                    List<string> active = node.GetActiveAircraftList();
                    if (active.Count == 0)
                        active.Add("Default");
                    foreach (string plane in active)
                    {
                        if (!countedPlanes.Add(plane))
                            continue;
                        if (!planes[gameId].TryGetValue(plane, out EngineProfilePlane item))
                        {
                            item = new EngineProfilePlane { Plane = plane };
                            planes[gameId][plane] = item;
                        }
                        item.Relations++;
                        if (bound)
                            item.Bound++;
                    }
                }
            }

            EngineProfileGamesResult result = new EngineProfileGamesResult();
            foreach (EngineProfileGame game in games.Values.OrderBy(g => g.GameId.Equals("DCS", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                game.Planes = planes[game.GameId].Values.OrderBy(p => p.Plane, StringComparer.OrdinalIgnoreCase).ToList();
                GameInstall install = GameInstalls.Find(game.GameId, new List<string>());
                game.Installed = install.Installed || install.Name.Length == 0 || !GameInstalls.GameIds.Contains(install.GameId);
                game.InstallPath = install.InstallPath;
                game.Disabled = install.Disabled;
                result.Games.Add(game);
            }
            return result;
        }

        // The same test the exporters use: a bind with a button or an axis.
        private static bool IsBound(string key, Relation relation)
        {
            Bind bind = relation.bind;
            if (bind == null && InternalDataManagement.AllBinds.ContainsKey(key))
                bind = InternalDataManagement.AllBinds[key];
            if (bind == null)
                return false;
            return !string.IsNullOrWhiteSpace(relation.ISAXIS ? bind.JAxis : bind.JButton);
        }

        private static string GameIdOf(string nodeGame)
        {
            if (string.IsNullOrWhiteSpace(nodeGame))
                return "DCS";
            string game = nodeGame.Trim();
            if (game.Equals("DCS", StringComparison.OrdinalIgnoreCase) || game.Equals("IL2Game", StringComparison.OrdinalIgnoreCase) || game.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                return game;
            IGameIntegration integration;
            return GameRegistry.TryResolve(game, out integration) ? integration.GameId : game;
        }

        private static string DisplayNameOf(string gameId)
        {
            if (gameId.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                return "DCS World";
            if (gameId.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
                return "IL-2 Sturmovik: Great Battles";
            if (gameId.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                return "Star Citizen";
            IGameIntegration integration;
            return GameRegistry.TryResolve(gameId, out integration) ? integration.DisplayName : gameId;
        }

        // Which export writes the game: dcs, il2, starcitizen, registry (engine <alias> export), or none (no exporter).
        private static string ExporterOf(string gameId)
        {
            if (gameId.Equals("DCS", StringComparison.OrdinalIgnoreCase))
                return "dcs";
            if (gameId.Equals("IL2Game", StringComparison.OrdinalIgnoreCase))
                return "il2";
            if (gameId.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                return "starcitizen";
            IGameIntegration integration;
            return GameRegistry.TryResolve(gameId, out integration) ? "registry" : "none";
        }

        public static string ToText(EngineProfileGamesResult result)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Profile Games");
            foreach (EngineProfileGame game in result.Games)
            {
                sb.AppendLine("  " + game.Name + " [" + game.GameId + ", export: " + game.Exporter + (game.Disabled ? ", you do not have it" : game.Installed ? string.Empty : ", not installed") + "]: " +
                              game.Bound.ToString(CultureInfo.InvariantCulture) + " of " + game.Relations.ToString(CultureInfo.InvariantCulture) + " relation(s) bound");
                foreach (EngineProfilePlane plane in game.Planes)
                    sb.AppendLine("    - " + plane.Plane + ": " + plane.Bound.ToString(CultureInfo.InvariantCulture) + " of " + plane.Relations.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString().TrimEnd();
        }
    }

    public sealed class EngineProfileGamesResult
    {
        public List<EngineProfileGame> Games { get; set; }

        public EngineProfileGamesResult()
        {
            Games = new List<EngineProfileGame>();
        }
    }

    public sealed class EngineProfileGame
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public string Exporter { get; set; }
        public int Relations { get; set; }
        public int Bound { get; set; }
        // Found on this PC or set by the user (engine games installs); exports to other games are refused.
        public bool Installed { get; set; }
        public string InstallPath { get; set; }
        // The user said they do not have the game (Settings > Games): the GUI leaves it out without a warning.
        public bool Disabled { get; set; }
        public List<EngineProfilePlane> Planes { get; set; }

        public EngineProfileGame()
        {
            GameId = Name = Exporter = InstallPath = string.Empty;
            Planes = new List<EngineProfilePlane>();
        }
    }

    public sealed class EngineProfilePlane
    {
        public string Plane { get; set; }
        public int Relations { get; set; }
        public int Bound { get; set; }

        public EngineProfilePlane()
        {
            Plane = string.Empty;
        }
    }
}
