using System;
using System.Collections.Generic;
using System.Linq;

namespace JoyPro.Watch;

// The user's "Watch for changes" ticks on the Start page's game cards (docs/sync-watcher.md#which-games-are-watched),
// stored in ui-settings.json as "watchedGames": { "<game id>": false, ... } next to "watchGameConfigs" (the master
// switch). Only explicit choices are stored: a game without one is watched once it is installed, so a game installed
// later is ticked by itself, and a game the user unticked stays unticked (also after an uninstall and a new install).
// This is not "I do not have this game" (engine games-disabled.json): an unwatched game still imports and exports.
// Read by the GUI's watcher and the tray host alike; immutable, compared by value.
public sealed class GameWatchChoices : IEquatable<GameWatchChoices>
{
    public const string SettingsKey = "watchedGames";

    public static GameWatchChoices Empty { get; } = new(null);

    private readonly Dictionary<string, bool> choices = new(StringComparer.OrdinalIgnoreCase);

    public GameWatchChoices(IEnumerable<KeyValuePair<string, bool>>? choices)
    {
        foreach (KeyValuePair<string, bool> choice in choices ?? [])
        {
            if (!string.IsNullOrWhiteSpace(choice.Key))
                this.choices[choice.Key.Trim()] = choice.Value;
        }
    }

    // The explicit choices (game id -> watched), sorted by game id.
    public IReadOnlyDictionary<string, bool> Choices =>
        choices.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    public int Count => choices.Count;

    // The user's tick for the game, or null when they never chose.
    public bool? Choice(string gameId) =>
        !string.IsNullOrWhiteSpace(gameId) && choices.TryGetValue(gameId.Trim(), out bool value) ? value : null;

    // A game is watched when it is installed and the user did not untick it.
    public bool IsWatched(string gameId, bool installed) => installed && Choice(gameId) != false;

    public GameWatchChoices With(string gameId, bool watched)
    {
        if (string.IsNullOrWhiteSpace(gameId) || Choice(gameId) == watched)
            return this;
        Dictionary<string, bool> next = new(choices, StringComparer.OrdinalIgnoreCase) { [gameId.Trim()] = watched };
        return new GameWatchChoices(next);
    }

    // The configs of engine sync status that are watched (ParseStatus already left out games that are not installed).
    public List<SyncStatusEntry> Filter(IEnumerable<SyncStatusEntry> entries) =>
        (entries ?? []).Where(e => IsWatched(e.GameId, installed: true)).ToList();

    public bool Equals(GameWatchChoices? other) =>
        other != null && other.choices.Count == choices.Count &&
        choices.All(c => other.choices.TryGetValue(c.Key, out bool value) && value == c.Value);

    public override bool Equals(object? obj) => Equals(obj as GameWatchChoices);

    public override int GetHashCode()
    {
        int hash = 0;
        foreach (KeyValuePair<string, bool> choice in choices)
            hash ^= HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(choice.Key), choice.Value);
        return hash;
    }

    public override string ToString() =>
        choices.Count == 0 ? "(no choices)" : string.Join(", ", Choices.Select(c => c.Key + "=" + (c.Value ? "on" : "off")));
}
