using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Localization;

namespace JoyPro.Gui.ViewModels;

// The Statistics page (opt-in, docs/usage-statistics.md#statistics-page): everything engine usage stats knows, as
// tiles, bar and column charts, fun facts and the profile's numbers. Its own view model; the main window gives it the
// engine calls.
public sealed partial class StatisticsViewModel : ViewModelBase
{
    // The fun facts' sentences by the engine's fact id ({n} = the fact's values; dates and numbers formatted here).
    internal static readonly Dictionary<string, string> FactTexts = new(StringComparer.Ordinal)
    {
        ["more-than-helicopters"] = N("You flew the {0} more than all helicopters together ({1} h against {2} h)."),
        ["more-than-planes"] = N("You spent more time in the {0} than in all airplanes together ({1} h against {2} h)."),
        ["more-than-spaceships"] = N("You flew the {0} more than all spaceships together ({1} h against {2} h)."),
        ["total-days"] = N("{0} hours in total: that is {1} full days."),
        ["top-game"] = N("Most played: {0} with {1} hours ({2} % of your time)."),
        ["longest-session"] = N("Your longest session: {0} hours of {1} on {2}."),
        ["streak"] = N("Your longest streak: {0} days in a row ({1} to {2})."),
        ["favourite-ship"] = N("Your favourite ship: the {0} ({1} h)."),
        ["distance"] = N("You jumped {0} light years: {1} times across the Milky Way."),
        ["distance-short"] = N("You jumped {0} light years in Elite Dangerous."),
        ["most-kills"] = N("Most kills in one flight: {0} in the {1} ({2})."),
        ["busiest-weekday"] = N("Your busiest day of the week: {0}."),
        ["night-owl"] = N("{0} sessions started after midnight. Night owl!"),
        ["first-flight"] = N("Your first recorded flight: {0} in the {1} ({2})."),
        ["exports"] = N("JoyPro wrote your binds into your games {0} times."),
        ["steam-only"] = N("{0}: {1} hours on Steam, most of them from before JoyPro started counting."),
    };

    internal static readonly Dictionary<string, string> CounterTitles = new(StringComparer.Ordinal)
    {
        ["landings"] = N("Landings"),
        ["takeoffs"] = N("Take-offs"),
        ["kills"] = N("Kills"),
        ["deaths"] = N("Deaths"),
        ["jumps"] = N("Jumps"),
        ["distanceLy"] = N("Light years"),
    };

    // The source titles the engine reports (engine/Usage/UsageModel.cs UsageSourceKinds.Title), for the translation.
    internal static readonly string[] SourceTitles =
    [
        N("DCS log (dcs.log)"), N("DCS logbook (logbook.lua)"), N("DCS tracks (.trk)"), N("Tacview recordings (.acmi)"), N("Falcon BMS pilot logbook (.lbk)"),
        N("IL-2 mission text logs"), N("X-Plane logbook (X-Plane Pilot.txt)"), N("X-Plane log (Log.txt)"), N("War Thunder replays (.wrpl)"),
        N("Elite Dangerous journals"), N("Star Citizen game logs (Game.log)"), N("X4 savegames"), N("Steam playtime (localconfig.vdf)")
    ];

    // Notes the engine adds to a source (shown through Loc.Translate).
    internal static readonly string[] SourceNotes =
    [
        N("IL-2 writes no mission text logs (startup.cfg: mission_text_log = 0)"),
        N("The flight logbook is kept online; only Steam's playtime is on this PC")
    ];

    private bool loadRequested;

    public Func<Task<string?>>? StatsRunner { get; init; }

    public Func<Task<bool>>? HarvestRunner { get; init; }

    [ObservableProperty]
    private bool isLoaded;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasData;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string oldestBindText = string.Empty;

    [ObservableProperty]
    private string growthText = string.Empty;

    [ObservableProperty]
    private string growthStart = string.Empty;

    [ObservableProperty]
    private string growthEnd = string.Empty;

    [ObservableProperty]
    private string growthRelationsMax = string.Empty;

    [ObservableProperty]
    private string growthBindsMax = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<double> growthRelations = [];

    [ObservableProperty]
    private IReadOnlyList<double> growthBinds = [];

    [ObservableProperty]
    private IReadOnlyList<double> weekdayValues = [];

    [ObservableProperty]
    private IReadOnlyList<double> hourValues = [];

    [ObservableProperty]
    private IReadOnlyList<double> monthValues = [];

    [ObservableProperty]
    private string weekdayTip = string.Empty;

    [ObservableProperty]
    private string hourTip = string.Empty;

    [ObservableProperty]
    private string monthTip = string.Empty;

    [ObservableProperty]
    private string monthStart = string.Empty;

    [ObservableProperty]
    private string monthEnd = string.Empty;

    public ObservableCollection<StatTile> Tiles { get; } = [];
    public ObservableCollection<StatTile> CounterTiles { get; } = [];
    public ObservableCollection<StatBar> GameBars { get; } = [];
    public ObservableCollection<StatBar> UnitBars { get; } = [];
    public ObservableCollection<StatBar> DeviceBars { get; } = [];
    public ObservableCollection<StatBar> ExportBars { get; } = [];
    public ObservableCollection<string> WeekdayLabels { get; } = [];
    public ObservableCollection<string> Facts { get; } = [];
    public ObservableCollection<StatSource> Sources { get; } = [];

    public bool HasCounters => CounterTiles.Count > 0;
    public bool HasUnits => UnitBars.Count > 0;
    public bool HasFacts => Facts.Count > 0;
    public bool HasDevices => DeviceBars.Count > 0;
    public bool HasExports => ExportBars.Count > 0;
    public bool HasGrowth => GrowthBinds.Count >= 2 && GrowthBinds.Max() > 0;
    public bool HasMonths => MonthValues.Count >= 2;
    public bool HasOldestBind => OldestBindText.Length > 0;
    public bool HasSources => Sources.Count > 0;
    public bool ShowEmpty => IsLoaded && !HasData;

    // Called when the page opens: loads once (Reload refreshes).
    public void EnsureLoaded()
    {
        if (loadRequested)
            return;
        loadRequested = true;
        _ = ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (StatsRunner == null)
            return;
        IsLoading = true;
        try
        {
            string? json = await StatsRunner();
            try
            {
                if (json != null)
                {
                    Apply(json);
                    return;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                JoyPro.Logging.JoyProLog.Warn("Usage", "The statistics document could not be read", ex);
            }
            StatusText = T("The statistics could not be read (see Output).");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ReadNowAsync()
    {
        if (HarvestRunner != null)
            await HarvestRunner();
        await ReloadAsync();
    }

    // The engine's document onto the page (also the render tests: a fixed document).
    public void Apply(string json)
    {
        loadRequested = true;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        HasData = Bool(root, "HasData");
        Tiles.Clear();
        CounterTiles.Clear();
        GameBars.Clear();
        UnitBars.Clear();
        DeviceBars.Clear();
        ExportBars.Clear();
        Facts.Clear();
        Sources.Clear();
        WeekdayLabels.Clear();

        JsonElement totals = Obj(root, "Totals");
        Tiles.Add(new StatTile(HoursText(Num(totals, "Hours")), T("Played in total"), T("Logged in your games: {0}", HoursText(Num(totals, "LogHours")))));
        Tiles.Add(new StatTile(Count(Num(totals, "Sessions")), T("Sessions"), T("Game starts and logged flights")));
        Tiles.Add(new StatTile(Count(Num(totals, "Games")), T("Games"), T("{0} aircraft, ships and vehicles", Count(Num(totals, "Units")))));
        Tiles.Add(new StatTile(Count(Num(totals, "ActiveDays")), T("Days you played"), DateRange(Str(totals, "First"), Str(totals, "Last"))));
        Tiles.Add(new StatTile(Count(Num(totals, "LongestStreakDays")), T("Longest streak (days)"),
            Str(totals, "LongestStreakStart").Length > 0 ? DateRange(Str(totals, "LongestStreakStart"), Str(totals, "LongestStreakEnd")) : T("Days in a row")));
        Tiles.Add(new StatTile(Count(Num(totals, "CurrentStreakDays")), T("Current streak (days)"), T("Up to today or yesterday")));

        List<JsonElement> games = Arr(root, "Games").ToList();
        double maxGame = games.Count == 0 ? 0 : games.Max(g => Num(g, "Hours"));
        foreach (JsonElement g in games.Take(12))
        {
            double hours = Num(g, "Hours");
            string sub = Num(g, "SteamHours") > Num(g, "LogHours") + 0.01
                ? T("Steam {0}, logged {1}", HoursText(Num(g, "SteamHours")), HoursText(Num(g, "LogHours")))
                : Num(g, "Sessions") == 0
                    ? T("Kept by the game itself (logbook, savegame)")
                    : Plural("{0} session", "{0} sessions", (long)Num(g, "Sessions"));
            GameBars.Add(new StatBar(Str(g, "Name"), sub, Fraction(hours, maxGame), HoursText(hours)));
        }
        List<JsonElement> units = Arr(root, "Units").ToList();
        double maxUnit = units.Count == 0 ? 0 : units.Max(u => Num(u, "Hours"));
        foreach (JsonElement u in units.Take(10))
        {
            double hours = Num(u, "Hours");
            string sessions = Num(u, "Sessions") == 0 ? T("from its logbook") : Plural("{0} session", "{0} sessions", (long)Num(u, "Sessions"));
            UnitBars.Add(new StatBar(Str(u, "Name"), T("{0}, {1}", Str(u, "GameName"), sessions), Fraction(hours, maxUnit), HoursText(hours)));
        }
        foreach (JsonElement c in Arr(root, "Counters"))
        {
            string name = Str(c, "Name");
            string title = CounterTitles.TryGetValue(name, out string? t) ? T(t) : name;
            JsonElement perGame = Obj(c, "PerGame");
            string where = perGame.ValueKind == JsonValueKind.Object ? string.Join(", ", perGame.EnumerateObject().Select(p => GameName(root, p.Name))) : string.Empty;
            CounterTiles.Add(new StatTile(Count(Num(c, "Total")), title, where));
        }

        // When: Monday first, 24 hours, the months.
        List<double> weekdays = Arr(root, "Weekdays").Select(v => v.GetDouble()).ToList();
        WeekdayValues = weekdays;
        DateTimeFormatInfo format = Loc.Culture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
            WeekdayLabels.Add(format.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7)));
        WeekdayTip = weekdays.Count == 7 ? string.Join(Environment.NewLine, Enumerable.Range(0, 7).Select(i => T("{0}: {1}", format.GetDayName((DayOfWeek)((i + 1) % 7)), HoursText(weekdays[i])))) : string.Empty;
        List<double> hours24 = Arr(root, "HoursOfDay").Select(v => v.GetDouble()).ToList();
        HourValues = hours24;
        int peak = hours24.Count == 24 ? hours24.IndexOf(hours24.Max()) : -1;
        HourTip = peak >= 0 && hours24[peak] > 0 ? T("Most sessions start at {0}:00 ({1})", peak.ToString("00", CultureInfo.InvariantCulture), Count(hours24[peak])) : string.Empty;
        List<JsonElement> months = Arr(root, "Months").ToList().TakeLast(24).ToList();
        MonthValues = months.Select(m => Num(m, "Hours")).ToList();
        MonthStart = months.Count > 0 ? MonthName(Str(months[0], "Month")) : string.Empty;
        MonthEnd = months.Count > 0 ? MonthName(Str(months[^1], "Month")) : string.Empty;
        MonthTip = string.Join(Environment.NewLine, months.Select(m => T("{0}: {1}", MonthName(Str(m, "Month")), HoursText(Num(m, "Hours")))));

        foreach (JsonElement f in Arr(root, "Facts"))
        {
            string text = FactText(Str(f, "Id"), Arr(f, "Values").Select(v => v.GetString() ?? string.Empty).ToList());
            if (text.Length > 0)
                Facts.Add(text);
        }

        List<JsonElement> devices = Arr(root, "Devices").ToList();
        double maxDevice = devices.Count == 0 ? 0 : devices.Max(d => Num(d, "Binds"));
        foreach (JsonElement d in devices.Take(10))
            DeviceBars.Add(new StatBar(Str(d, "Device"), T("{0} axes, {1} buttons", Count(Num(d, "Axes")), Count(Num(d, "Buttons"))), Fraction(Num(d, "Binds"), maxDevice), Count(Num(d, "Binds"))));
        JsonElement oldest = Obj(root, "OldestUnusedBind");
        OldestBindText = oldest.ValueKind == JsonValueKind.Object
            ? T("'{0}' on {1} {2}, bound since {3} (or longer); none of its aircraft or games shows up in a recorded session.", Str(oldest, "Relation"), Str(oldest, "Device"), Str(oldest, "Input"), DateText(Str(oldest, "Since")))
            : string.Empty;
        List<JsonElement> growth = Arr(root, "Growth").ToList();
        GrowthRelations = growth.Select(g => Num(g, "Relations")).ToList();
        GrowthBinds = growth.Select(g => Num(g, "Binds")).ToList();
        GrowthStart = growth.Count > 0 ? DateText(Str(growth[0], "Utc")) : string.Empty;
        GrowthEnd = growth.Count > 0 ? DateText(Str(growth[^1], "Utc")) : string.Empty;
        GrowthRelationsMax = growth.Count > 0 ? Count(GrowthRelations.Max()) : string.Empty;
        GrowthBindsMax = growth.Count > 0 ? Count(GrowthBinds.Max()) : string.Empty;
        GrowthText = growth.Count >= 2
            ? T("From {0} to {1}: relations {2} to {3}, binds {4} to {5}.", GrowthStart, GrowthEnd, Count(GrowthRelations[0]), Count(GrowthRelations[^1]), Count(GrowthBinds[0]), Count(GrowthBinds[^1]))
            : string.Empty;

        List<JsonElement> exports = Arr(root, "Exports").ToList();
        double maxExport = exports.Count == 0 ? 0 : exports.Max(e => Num(e, "Count"));
        foreach (JsonElement e in exports.Take(10))
            ExportBars.Add(new StatBar(Str(e, "Name"), T("last {0}", DateText(Str(e, "Last"))), Fraction(Num(e, "Count"), maxExport), Count(Num(e, "Count"))));

        // Steam's playtime in one row (it covers every game with a Steam app id).
        List<JsonElement> steamRows = Arr(root, "Sources").Where(s => Str(s, "Kind") == "steam" && Num(s, "TotalHours") > 0).ToList();
        if (steamRows.Count > 0)
            Sources.Add(new StatSource(Plural("{0} game", "{0} games", steamRows.Count), T("Steam playtime (localconfig.vdf)"),
                T("{0} on Steam", HoursText(steamRows.Sum(s => Num(s, "TotalHours")))), string.Empty, false));
        foreach (JsonElement s in Arr(root, "Sources").Where(s => Str(s, "Kind") != "steam"))
        {
            string kind = Str(s, "Kind");
            bool steam = false;
            string yielded = steam
                ? (Num(s, "TotalHours") > 0 ? T("{0} on Steam", HoursText(Num(s, "TotalHours"))) : T("nothing on Steam"))
                : !Bool(s, "Exists") && Num(s, "Files") == 0
                    ? T("not on this PC")
                    : T("{0}: {1}, {2}", Plural("{0} file", "{0} files", (long)Num(s, "Files")), Plural("{0} session", "{0} sessions", (long)Num(s, "Sessions")),
                        HoursText(Math.Max(Num(s, "Hours"), Num(s, "TotalHours"))));
            string notes = string.Join(" ", Arr(s, "Notes").Select(n => Loc.Translate(n.GetString() ?? string.Empty)).Take(2));
            Sources.Add(new StatSource(Str(s, "Game").Length == 0 ? T("Any game") : GameName(root, Str(s, "Game")), T(Str(s, "Title")), yielded, notes, Bool(s, "LiveTail")));
        }
        IsLoaded = true;
        StatusText = HasData ? T("Updated {0}", DateTime.Now.ToString("t", Loc.Culture)) : string.Empty;
        foreach (string name in new[] { nameof(HasCounters), nameof(HasUnits), nameof(HasFacts), nameof(HasDevices), nameof(HasExports), nameof(HasGrowth), nameof(HasMonths), nameof(HasOldestBind), nameof(HasSources), nameof(ShowEmpty) })
            OnPropertyChanged(name);
    }

    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnHasDataChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    internal static string FactText(string id, IReadOnlyList<string> values)
    {
        if (!FactTexts.TryGetValue(id, out string? template))
            return string.Empty;
        object[] args = values.Select((v, i) => (object)FormatValue(id, i, v)).ToArray();
        return T(template, args);
    }

    // Numbers in the user's language, dates as the user's short date, the weekday by name.
    private static string FormatValue(string id, int index, string value)
    {
        if (id == "busiest-weekday" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int day))
            return Loc.Culture.DateTimeFormat.GetDayName((DayOfWeek)((day + 1) % 7));
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            return date.ToString("d", Loc.Culture);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !(id is "more-than-helicopters" or "more-than-planes" or "more-than-spaceships" or "favourite-ship" or "most-kills" or "first-flight" or "steam-only" or "top-game" && index == 0))
            return number.ToString(number == Math.Floor(number) ? "#,0" : "#,0.###", Loc.Culture);
        return value;
    }

    public static string HoursText(double hours)
    {
        if (hours <= 0)
            return T("0 h");
        if (hours < 1)
            return T("{0} min", Math.Round(hours * 60).ToString("0", Loc.Culture));
        return T("{0} h", hours.ToString(hours < 10 ? "0.#" : "#,0", Loc.Culture));
    }

    private static string Count(double value) => value.ToString(value == Math.Floor(value) ? "#,0" : "#,0.#", Loc.Culture);

    private static double Fraction(double value, double max) => max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);

    private static string DateText(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d)
            ? (iso.Length <= 10 ? d : d.ToLocalTime()).ToString("d", Loc.Culture)
            : string.Empty;

    private static string DateRange(string first, string last)
    {
        string a = DateText(first), b = DateText(last);
        return a.Length == 0 ? string.Empty : a == b || b.Length == 0 ? a : T("{0} to {1}", a, b);
    }

    private static string MonthName(string month) =>
        DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime m) ? m.ToString("MMM yyyy", Loc.Culture) : month;

    private static string GameName(JsonElement root, string game)
    {
        foreach (JsonElement g in Arr(root, "Games"))
        {
            if (string.Equals(Str(g, "Game"), game, StringComparison.OrdinalIgnoreCase))
                return Str(g, "Name");
        }
        return Models.DisplayText.Game(game);
    }

    private static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}

public sealed record StatTile(string Value, string Label, string Caption);

// A bar: label, a line under it, the bar's length (0..1) and the value at its end.
public sealed record StatBar(string Label, string Sub, double Fraction, string ValueText)
{
    public string Tip => T("{0}: {1}", Label, ValueText);
}

public sealed record StatSource(string Game, string Title, string Yielded, string Notes, bool LiveTail)
{
    public bool HasNotes => Notes.Length > 0;

    public string LiveText => LiveTail ? T("followed live while the game runs") : string.Empty;
}
