using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Watch;

namespace JoyPro.Gui.ViewModels;

// Usage recording, ranking and the Statistics page (docs/usage-statistics.md). Recording always runs: at start the
// engine reads what the games logged since the last time (engine usage harvest, its own process, never blocking the
// user's commands), then the live tail (JoyPro.Watch UsageLogTailer) follows the games' logs and journals while a game
// runs and a harvest follows when it closed. Everything stays on this PC. Settings > Usage statistics shows the ranking
// (pin, exclude), switches the style reference of Auto Assign / Auto Group / Setup wizard / Lay out with AI and the
// opt-in Statistics page.
public sealed partial class MainWindowViewModel
{
    public const string UsageSettingsSectionId = "usage";

    // Settings: the Statistics page in the bottom bar (off by default; kept in ui-settings.json "showStatisticsPage").
    [ObservableProperty]
    private bool showStatisticsPage;

    // Settings: new or unused aircraft follow the user's most used aircraft of their class (on by default;
    // ui-settings.json "usageStyle"). Off: the engine gets --usage-style off.
    [ObservableProperty]
    private bool useUsageStyle = true;

    [ObservableProperty]
    private string usageStatus = string.Empty;

    [ObservableProperty]
    private bool usageBusy;

    public ObservableCollection<UsageRankItem> UsageRankingUnits { get; } = [];

    public ObservableCollection<UsageRankItem> UsageRankingGames { get; } = [];

    public bool HasUsageRanking => UsageRankingUnits.Count > 0 || UsageRankingGames.Count > 0;

    // Tests: no tail, or a fake one.
    internal Func<string, IUsageTailer> UsageTailerFactory { get; set; } = folder => new UsageLogTailer(folder);

    // JOYPRO_USAGE_RECORDING=off stops the recording (support cases); there is no setting for it.
    internal static bool UsageRecordingEnabled => !string.Equals(Environment.GetEnvironmentVariable("JOYPRO_USAGE_RECORDING"), "off", StringComparison.OrdinalIgnoreCase);

    private IUsageTailer? usageTailer;
    private bool usageRecordingStarted;
    private StatisticsViewModel? statistics;

    partial void OnShowStatisticsPageChanged(bool value)
    {
        if (FindNavPage(StatisticsNavPageId) is { } page)
            page.IsAvailable = value;
        if (!value && CurrentStage == UiStage.Statistics)
            CurrentStage = UiStage.Start;
        SaveUiSettings();
    }

    partial void OnUseUsageStyleChanged(bool value) => SaveUiSettings();

    public StatisticsViewModel Statistics => statistics ??= new StatisticsViewModel
    {
        StatsRunner = RunUsageStatsAsync,
        HarvestRunner = () => HarvestUsageAsync(null)
    };

    public bool IsStatisticsStage => CurrentStage == UiStage.Statistics;

    [RelayCommand]
    private void GoStatistics() => CurrentStage = UiStage.Statistics;

    // Called when the stage changes (OnCurrentStageChanged).
    private void OnStatisticsStageChanged(bool shown)
    {
        OnPropertyChanged(nameof(IsStatisticsStage));
        if (shown)
            Statistics.EnsureLoaded();
    }

    // At start (RunStartupChecksAsync): catch up with what the games logged, then follow their logs live.
    public async Task StartUsageRecordingAsync()
    {
        if (usageRecordingStarted || !UsageRecordingEnabled)
            return;
        usageRecordingStarted = true;
        try
        {
            await HarvestUsageAsync(null);
            EngineCommandResult? sources = await RunUsageEngineAsync(["usage", "sources"]);
            if (sources == null || sources.ExitCode != 0)
                return;
            (string LiveFolder, List<UsageTailTarget> Targets)? parsed = UsageLogTailer.ParseSources(sources.StandardOutput);
            if (parsed == null)
                return;
            usageTailer ??= UsageTailerFactory(parsed.Value.LiveFolder);
            usageTailer.GameSessionEnded -= OnUsageSessionEnded;
            usageTailer.GameSessionEnded += OnUsageSessionEnded;
            usageTailer.Update(parsed.Value.Targets);
            JoyPro.Logging.JoyProLog.Info("Usage", "Recording: following " + parsed.Value.Targets.Count + " game log folder(s)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            JoyPro.Logging.JoyProLog.Warn("Usage", "The usage recording did not start", ex);
        }
    }

    public void StopUsageRecording()
    {
        if (usageTailer == null)
            return;
        usageTailer.GameSessionEnded -= OnUsageSessionEnded;
        usageTailer.Dispose();
        usageTailer = null;
    }

    // From the tail's thread: a game closed; its live copies are finished.
    private void OnUsageSessionEnded(string game) => GameSyncPost(() => _ = OnUsageSessionEndedAsync(game));

    private async Task OnUsageSessionEndedAsync(string game)
    {
        if (await HarvestUsageAsync(game))
        {
            if (statistics != null && IsStatisticsStage)
                await statistics.ReloadAsync();
            if (FindSettingsSection(UsageSettingsSectionId) is { IsContentVisible: true })
                await RefreshUsageRankingAsync();
        }
    }

    // engine usage harvest (its own process, beside the user's commands: the first one reads a year of logs).
    internal async Task<bool> HarvestUsageAsync(string? game)
    {
        List<string> args = ["usage", "harvest"];
        if (!string.IsNullOrWhiteSpace(game))
        {
            args.Add("--game");
            args.Add(game);
        }
        UsageBusy = true;
        try
        {
            EngineCommandResult? result = await RunUsageEngineAsync(args);
            if (result == null || result.ExitCode != 0)
            {
                UsageStatus = T("The games' logs could not be read (see Output).");
                return false;
            }
            UsageStatus = HarvestSummary(result.StandardOutput);
            return true;
        }
        finally
        {
            UsageBusy = false;
        }
    }

    private static string HarvestSummary(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int read = root.TryGetProperty("FilesRead", out JsonElement r) ? r.GetInt32() : 0;
            int fresh = root.TryGetProperty("SessionsNew", out JsonElement n) ? n.GetInt32() : 0;
            string time = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
            return fresh == 0
                ? T("Read the games' logs at {0}: nothing new.", time)
                : Plural("Read the games' logs at {1}: {0} new session.", "Read the games' logs at {1}: {0} new sessions.", fresh, fresh, time);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    // A usage command in a process of its own (harvest) or through the server (the rest), without the user's
    // command queue: the user never waits for the recording.
    private async Task<EngineCommandResult?> RunUsageEngineAsync(IReadOnlyList<string> commandArgs)
    {
        string engine = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engine) || !System.IO.File.Exists(engine))
            return null;
        List<string> args = ["--status", "false", "--benchmark", "false", "--load-corrections", "false", .. commandArgs];
        try
        {
            EngineCommandResult result = await cliClient.RunAsync(engine, args);
            if (persistUiFiles)
                AppendSessionLog(BuildOutputText(result, string.Empty));
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            JoyPro.Logging.JoyProLog.Warn("Usage", "engine " + string.Join(" ", commandArgs) + " failed", ex);
            return null;
        }
    }

    // The Statistics page's document (engine usage stats, with the open profile for its devices and growth).
    private async Task<string?> RunUsageStatsAsync()
    {
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(ProfilePath) && System.IO.File.Exists(ProfilePath))
        {
            args.Add("--profile");
            args.Add(ProfilePath);
        }
        args.AddRange(["usage", "stats"]);
        EngineCommandResult? result = await RunUsageEngineAsync(args);
        return result == null || result.ExitCode != 0 ? null : result.StandardOutput;
    }

    // Settings > Usage statistics: the ranking (top 15 aircraft / ships / vehicles, top 10 games).
    internal async Task RefreshUsageRankingAsync()
    {
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(ProfilePath) && System.IO.File.Exists(ProfilePath))
        {
            args.Add("--profile");
            args.Add(ProfilePath);
        }
        args.AddRange(["usage", "ranking", "--top", "15"]);
        EngineCommandResult? result = await RunUsageEngineAsync(args);
        if (result == null || result.ExitCode != 0)
            return;
        ApplyUsageRanking(result.StandardOutput);
    }

    internal void ApplyUsageRanking(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            UsageRankingUnits.Clear();
            UsageRankingGames.Clear();
            if (doc.RootElement.TryGetProperty("Units", out JsonElement units))
                foreach (JsonElement u in units.EnumerateArray())
                    UsageRankingUnits.Add(UsageRankItem.FromJson(u, unit: true));
            if (doc.RootElement.TryGetProperty("Games", out JsonElement games))
                foreach (JsonElement g in games.EnumerateArray().Take(10))
                    UsageRankingGames.Add(UsageRankItem.FromJson(g, unit: false));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            JoyPro.Logging.JoyProLog.Warn("Usage", "The ranking could not be read", ex);
        }
        OnPropertyChanged(nameof(HasUsageRanking));
    }

    [RelayCommand]
    private async Task ReadUsageNowAsync()
    {
        if (await HarvestUsageAsync(null))
        {
            await RefreshUsageRankingAsync();
            if (statistics != null)
                await statistics.ReloadAsync();
        }
    }

    [RelayCommand]
    private Task ToggleUsagePinAsync(UsageRankItem? item) => item == null ? Task.CompletedTask : SetUsageOverrideAsync(item, item.Pinned ? "unpin" : "pin");

    [RelayCommand]
    private Task ToggleUsageExcludeAsync(UsageRankItem? item) => item == null ? Task.CompletedTask : SetUsageOverrideAsync(item, item.Excluded ? "include" : "exclude");

    private async Task SetUsageOverrideAsync(UsageRankItem item, string action)
    {
        List<string> args = ["usage", action, item.Game];
        if (item.Unit.Length > 0)
            args.Add(item.Unit);
        EngineCommandResult? result = await RunUsageEngineAsync(args);
        if (result == null || result.ExitCode != 0)
        {
            StatusText = T("The ranking could not be changed (see Output).");
            return;
        }
        await RefreshUsageRankingAsync();
        if (statistics != null && statistics.IsLoaded)
            await statistics.ReloadAsync();
    }

    // The commands that use the ranking as their style reference (they get --usage-style off when the setting is off).
    internal static bool UsesUsageStyle(IReadOnlyList<string> command)
    {
        (string area, string action, _) = EngineServerCommands.Command(command);
        return (string.Equals(area, "relations", StringComparison.OrdinalIgnoreCase) && (action is "auto-assign" or "auto-group")) ||
               string.Equals(area, "setup", StringComparison.OrdinalIgnoreCase) || string.Equals(area, "arrange", StringComparison.OrdinalIgnoreCase);
    }
}

// One row of the ranking in Settings > Usage statistics.
public sealed class UsageRankItem
{
    public string Id { get; init; } = string.Empty;
    public string Game { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string GameName { get; init; } = string.Empty;
    public int Rank { get; init; }
    public double Hours { get; init; }
    public int Sessions { get; init; }
    public string Evidence { get; init; } = string.Empty;
    public int Binds { get; init; }
    public DateTime Last { get; init; }
    public bool Pinned { get; init; }
    public bool Excluded { get; init; }

    public string RankText => Excluded ? "-" : Rank.ToString(JoyPro.Gui.Localization.Loc.Culture) + ".";

    public string Title => Unit.Length > 0 && GameName.Length > 0 ? T("{0} ({1})", Name, GameName) : Name;

    // "12.5 h, 8 sessions, last 2026-10-11" / "120 binds, not flown yet".
    public string Detail
    {
        get
        {
            if (Evidence == "total" && Sessions == 0)
                return T("{0} in the game's own logbook", StatisticsViewModel.HoursText(Hours));
            if (Evidence == "binds")
                return Plural("{0} bind in your profile, not used in a recorded session yet", "{0} binds in your profile, not used in a recorded session yet", Binds);
            string text = StatisticsViewModel.HoursText(Hours);
            if (Sessions > 0)
                text = T("{0}, {1}", text, Plural("{0} session", "{0} sessions", Sessions));
            if (Last != DateTime.MinValue)
                text = T("{0}, last {1}", text, Last.ToLocalTime().ToString("d", JoyPro.Gui.Localization.Loc.Culture));
            return text;
        }
    }

    public string PinText => Pinned ? T("Unpin") : T("Pin");

    public string ExcludeText => Excluded ? T("Include") : T("Exclude");

    public string PinTip => T("Pinned entries rank first; a pinned aircraft is the model for new aircraft of its class");

    public string ExcludeTip => T("Excluded entries do not count in the ranking and are never the model for new aircraft");

    internal static UsageRankItem FromJson(JsonElement e, bool unit)
    {
        string Str(string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        double Num(string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        bool Bool(string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
        DateTime last = DateTime.TryParse(Str("Last"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime l) ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : DateTime.MinValue;
        return new UsageRankItem
        {
            Id = Str("Id"),
            Game = Str("Game"),
            Unit = unit ? Str("Unit") : string.Empty,
            Name = Str("Name"),
            GameName = unit ? Str("GameName") : string.Empty,
            Rank = (int)Num("Rank"),
            Hours = Num("Hours"),
            Sessions = (int)Num("Sessions"),
            Evidence = Str("Evidence"),
            Binds = (int)Num("Binds"),
            Last = last,
            Pinned = Bool("Pinned"),
            Excluded = Bool("Excluded")
        };
    }
}
