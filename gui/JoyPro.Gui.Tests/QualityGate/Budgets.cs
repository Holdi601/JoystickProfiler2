using System.Collections.Generic;

namespace JoyPro.Gui.Tests.QualityGate;

// The quality gate's time budgets in one table (milliseconds, measured through the view model with the real engine on
// the real profile and game lists). An operation over its budget is a finding of I7; every measurement is in
// timings.csv. The goal (user, 2026-10-24): nothing a person waits for takes more than a few seconds, interactive
// actions at most 2 s. The budgets sit a little above what the engine server made of them (measured on the user's
// profile copy, docs/testing.md "Speed"), so a regression fails the gate.
internal static class Budgets
{
    public static readonly IReadOnlyDictionary<string, double> Table = new Dictionary<string, double>
    {
        // Open a profile: profile load, relation list, binds, modifiers, device remap check, device names. (~2.4 s)
        ["profile load"] = 3000,
        // Open one relation in the editor (its actions with titles). (~0.4 s)
        ["relation open"] = 1000,
        // A new relation with one action from the search ("Create relation with 1 action"), and one more action into it.
        // (~1.1 s: add, list, show, aircraft, suggestions)
        ["relation create"] = 2000,
        ["relation add"] = 2000,
        // A new relation with 200 actions at once (catalog "Create relation with 200 actions"). (~1.5 s alone, 2.7 s
        // measured while other heavy work ran on the PC; a bulk action, so a few seconds are fine)
        ["relation add 200 nodes"] = 3500,
        // 200 more actions into an existing relation ("Add selected (200)"). (~1.3 s)
        ["relation add 200 nodes to existing"] = 2000,
        // Save (Ctrl+S) of the profile. (~0.7 s)
        ["save"] = 1000,
        // The suggestions of the open relation. (~0.3 s)
        ["suggestions"] = 1000,
        // The catalog's game list ("Open catalog" the first time). (~0.5 s)
        ["catalog overview"] = 1000,
        // The catalog of one game or one DCS aircraft. (~0.3 s, the biggest ~1 s)
        ["catalog open"] = 2000,
        // One search over every game (Add actions). (~0.1 s)
        ["search"] = 1000,
        // One search with a game chip ticked. (~0.1 s)
        ["search game chip"] = 1000,
        // An export of one game as a dry run (registry games) or into a copy of its config folder (DCS, IL-2, Star Citizen).
        // (registry games ~1 s; DCS ~4 s: 2000 files written, then the whole folder read back for the sync record)
        ["export dry-run"] = 5000,
        // The Start page's games (games installs + integrations). (~0.7 s)
        ["games list"] = 1500,
        // The engine server starting and reading the game lists (once, in the background while the app starts). (~4 s)
        ["engine server start"] = 8000,
    };

    public static double For(string operation) => Table.TryGetValue(operation, out double ms) ? ms : 0;
}
