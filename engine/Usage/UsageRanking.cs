#if NET
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JoyPro.Usage
{
    // The most used aircraft, ships and games (docs/usage-statistics.md#ranking). Order: the user's pinned units, then
    // units with logged time (hours, then sessions, then last use), then units the profile only has binds for (a weak
    // fallback, by bind count). Excluded units stay in the list (so they can be included again) but rank last and are
    // never a style reference. Games rank by hours (logged or Steam, whichever is higher).
    public sealed class UsageRanking
    {
        public List<UsageUnitStat> Units = new List<UsageUnitStat>();
        public List<UsageGameStat> Games = new List<UsageGameStat>();

        public static UsageRanking Build(UsageAggregate aggregate, UsageOverrides overrides, IDictionary<string, int> bindCounts = null, Func<string, string, string> classOf = null)
        {
            overrides = overrides ?? new UsageOverrides();
            UsageRanking ranking = new UsageRanking();
            List<UsageUnitStat> units = aggregate.Units.Select(u => u).ToList();
            // Units the profile binds that never showed up in a log: the weak fallback.
            if (bindCounts != null)
            {
                foreach (KeyValuePair<string, int> pair in bindCounts)
                {
                    int bar = pair.Key.IndexOf('|');
                    if (bar <= 0 || pair.Value <= 0)
                        continue;
                    string game = pair.Key.Substring(0, bar), unit = pair.Key.Substring(bar + 1);
                    UsageUnitStat known = units.FirstOrDefault(u => string.Equals(u.Game, game, StringComparison.OrdinalIgnoreCase) && UsageAggregate.SameUnit(u.Unit, unit));
                    if (known != null)
                    {
                        known.Binds = Math.Max(known.Binds, pair.Value);
                        continue;
                    }
                    units.Add(new UsageUnitStat { Game = game, Unit = unit, Display = unit, Evidence = "binds", Binds = pair.Value, Class = classOf == null ? string.Empty : classOf(game, unit) ?? string.Empty });
                }
            }
            foreach (UsageUnitStat u in units)
            {
                u.Pinned = overrides.Pinned.Contains(u.Id);
                u.Excluded = overrides.Excluded.Contains(u.Id);
                if (string.IsNullOrEmpty(u.Class) && classOf != null)
                    u.Class = classOf(u.Game, u.Unit) ?? string.Empty;
            }
            ranking.Units = units
                .OrderBy(u => u.Excluded ? 3 : u.Pinned ? 0 : u.Evidence == "binds" ? 2 : 1)
                .ThenByDescending(u => u.Hours)
                .ThenByDescending(u => u.Sessions)
                .ThenByDescending(u => u.Binds)
                .ThenByDescending(u => u.Last)
                .ThenBy(u => u.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (UsageGameStat g in aggregate.Games.Values)
            {
                g.Pinned = overrides.Pinned.Contains(UsageOverrides.Id(g.Game, string.Empty));
                g.Excluded = overrides.Excluded.Contains(UsageOverrides.Id(g.Game, string.Empty));
            }
            ranking.Games = aggregate.Games.Values
                .OrderBy(g => g.Excluded ? 2 : g.Pinned ? 0 : 1)
                .ThenByDescending(g => g.Hours)
                .ThenByDescending(g => g.Sessions)
                .ThenByDescending(g => g.Last)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ranking;
        }

        // The style reference per game and class: the first ranked unit with real use (logs or a game's total, or
        // pinned), never an excluded one. A unit known only from binds is no reference.
        public Dictionary<string, UsageUnitStat> References()
        {
            Dictionary<string, UsageUnitStat> refs = new Dictionary<string, UsageUnitStat>(StringComparer.OrdinalIgnoreCase);
            foreach (UsageUnitStat u in Units)
            {
                if (u.Excluded || (!u.Pinned && (u.Evidence == "binds" || u.Hours <= 0)))
                    continue;
                string key = ReferenceKey(u.Game, u.Class);
                if (!refs.ContainsKey(key))
                    refs[key] = u;
            }
            return refs;
        }

        public static string ReferenceKey(string game, string cls)
        {
            return (game ?? string.Empty) + "|" + (cls ?? string.Empty);
        }

        // A unit counts as unused (new to the user) below half an hour of logged or kept time.
        public bool IsUnused(string game, string unit)
        {
            UsageUnitStat u = Units.FirstOrDefault(x => string.Equals(x.Game, game, StringComparison.OrdinalIgnoreCase) && UsageAggregate.SameUnit(x.Unit, unit) && x.Evidence != "binds");
            return u == null || (u.Hours < 0.5 && !u.Pinned);
        }

        public static Dictionary<string, object> UnitObject(UsageUnitStat u, int rank, Func<string, string> gameName)
        {
            Dictionary<string, object> o = new Dictionary<string, object>
            {
                { "Rank", rank }, { "Game", u.Game }, { "GameName", gameName == null ? u.Game : gameName(u.Game) ?? u.Game }, { "Unit", u.Unit },
                { "Name", string.IsNullOrEmpty(u.Display) ? u.Unit : u.Display }, { "Class", u.Class }, { "Hours", Math.Round(u.Hours, 2) },
                { "Sessions", u.Sessions }, { "First", UsageText.Iso(u.First) }, { "Last", UsageText.Iso(u.Last) }, { "Evidence", u.Evidence },
                { "Binds", u.Binds }, { "Pinned", u.Pinned }, { "Excluded", u.Excluded }, { "Id", u.Id }
            };
            o["Counters"] = u.Counters.Where(c => c.Value > 0).OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)Math.Round(c.Value, 2));
            return o;
        }

        public static Dictionary<string, object> GameObject(UsageGameStat g, int rank)
        {
            return new Dictionary<string, object>
            {
                { "Rank", rank }, { "Game", g.Game }, { "Name", g.Name }, { "Hours", Math.Round(g.Hours, 2) }, { "LogHours", Math.Round(g.LogHours, 2) },
                { "SteamHours", Math.Round(g.SteamHours, 2) }, { "TotalHours", Math.Round(g.TotalHours, 2) }, { "Sessions", g.Sessions },
                { "First", UsageText.Iso(g.First) }, { "Last", UsageText.Iso(g.Last) }, { "Pinned", g.Pinned }, { "Excluded", g.Excluded },
                { "Id", UsageOverrides.Id(g.Game, string.Empty) },
                { "Sources", g.Sources.Cast<object>().ToList() },
                { "Counters", g.Counters.Where(c => c.Value > 0).OrderBy(c => c.Key, StringComparer.Ordinal).ToDictionary(c => c.Key, c => (object)Math.Round(c.Value, 2)) }
            };
        }

        public string ToText(Func<string, string> gameName, int top = 25)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("Most used games");
            int i = 0;
            foreach (UsageGameStat g in Games.Where(g => !g.Excluded).Take(top))
                sb.AppendLine("  " + (++i).ToString(CultureInfo.InvariantCulture) + ". " + g.Name + ": " + UsageText.Hours(g.Hours) + ", " + g.Sessions.ToString(CultureInfo.InvariantCulture) + " session(s)" +
                              (g.Last == DateTime.MinValue ? string.Empty : ", last " + g.Last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + (g.Pinned ? " [pinned]" : string.Empty));
            if (i == 0)
                sb.AppendLine("  (nothing recorded yet)");
            sb.AppendLine("Most used aircraft, ships and vehicles");
            i = 0;
            foreach (UsageUnitStat u in Units.Where(u => !u.Excluded).Take(top))
                sb.AppendLine("  " + (++i).ToString(CultureInfo.InvariantCulture) + ". " + (string.IsNullOrEmpty(u.Display) ? u.Unit : u.Display) + " (" + (gameName == null ? u.Game : gameName(u.Game)) + "): " +
                              (u.Evidence == "binds" ? u.Binds.ToString(CultureInfo.InvariantCulture) + " bind(s), not flown yet" : UsageText.Hours(u.Hours) + ", " + u.Sessions.ToString(CultureInfo.InvariantCulture) + " session(s)") +
                              (u.Pinned ? " [pinned]" : string.Empty));
            if (i == 0)
                sb.AppendLine("  (nothing recorded yet)");
            List<UsageUnitStat> excluded = Units.Where(u => u.Excluded).ToList();
            if (excluded.Count > 0)
                sb.AppendLine("Excluded: " + string.Join(", ", excluded.Select(u => u.Display)));
            return sb.ToString().TrimEnd();
        }
    }
}
