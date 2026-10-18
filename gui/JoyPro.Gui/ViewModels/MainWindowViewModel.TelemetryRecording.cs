using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Ffb.Core.Host;
using JoyPro.Ffb.Core.Telemetry.Recording;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Logging;
using JoyPro.TelemetrySessions;

namespace JoyPro.Gui.ViewModels;

// Settings > Telemetry recording (docs/telemetry-recording.md): opt-in recording of every game's telemetry, like
// Tacview for DCS, for all games JoyPro reads telemetry of. Off by default. The settings are telemetry-recording.json
// (TelemetryRecordingConfig, the same file `engine telemetry config` edits); a running JoyPro.Ffb is told to "reload".
//
// Who records: one process owns a game's telemetry port. While telemetry force feedback runs for a game, its JoyPro.Ffb
// (run) records too. For every other game the GUI starts `JoyPro.Ffb record --game G --game-pid P --parent-pid GUI
// --protocol` when the game runs (looked up every 5 s, only while recording is on; nothing at all while it is off).
// The process ends with the game; its session is finished then. Everything stays on this PC.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<TelemetryRecordingGameItem> TelemetryRecordingGames { get; } = [];

    public ObservableCollection<TelemetrySessionItem> TelemetrySessions { get; } = [];

    public IReadOnlyList<TelemetryChoice> TelemetrySampleRates { get; } =
        TelemetryRecordingConfig.SampleRates.Select(hz => new TelemetryChoice(hz, hz <= 0 ? T("Every frame the game sends") : T("{0} per second", hz.ToString(CultureInfo.CurrentCulture)))).ToList();

    public IReadOnlyList<TelemetryChoice> TelemetrySizeCaps { get; } =
        new double[] { 1024, 2048, 4096, 10240, 20480, 51200, 0 }.Select(mb => new TelemetryChoice(mb, mb <= 0 ? T("No limit") : T("{0} GB", (mb / 1024).ToString("0.#", CultureInfo.CurrentCulture)))).ToList();

    public IReadOnlyList<TelemetryChoice> TelemetryKeepDaysChoices { get; } =
        new double[] { 0, 7, 30, 90, 365 }.Select(d => new TelemetryChoice(d, d <= 0 ? T("Keep them") : Plural("{0} day", "{0} days", (int)d))).ToList();

    [ObservableProperty]
    private bool telemetryRecordingEnabled;

    // "" = the default folder (<data>\Telemetry\Recordings).
    [ObservableProperty]
    private string telemetryRecordingFolder = string.Empty;

    [ObservableProperty]
    private TelemetryChoice? selectedTelemetrySampleRate;

    [ObservableProperty]
    private TelemetryChoice? selectedTelemetrySizeCap;

    [ObservableProperty]
    private TelemetryChoice? selectedTelemetryKeepDays;

    [ObservableProperty]
    private bool telemetryRecordingKeepRaw = true;

    [ObservableProperty]
    private TelemetrySessionItem? selectedTelemetrySession;

    [ObservableProperty]
    private IReadOnlyList<TelemetryPreviewTrack>? telemetryPreviewTracks;

    [ObservableProperty]
    private string telemetryPreviewText = string.Empty;

    [ObservableProperty]
    private string telemetrySessionsSummary = string.Empty;

    [ObservableProperty]
    private string telemetryRecordingMessage = string.Empty;

    [ObservableProperty]
    private string telemetryRecordingNow = string.Empty;

    [ObservableProperty]
    private bool telemetrySessionsLoading;

    public string TelemetryRecordingFolderText => TelemetrySessionStore.ResolveFolder(TelemetryRecordingFolder);

    public bool TelemetryRecordingUsesDefaultFolder => string.IsNullOrWhiteSpace(TelemetryRecordingFolder);

    public bool HasTelemetrySessions => TelemetrySessions.Count > 0;

    public bool HasSelectedTelemetrySession => SelectedTelemetrySession != null;

    public bool CanExportSelectedTelemetryAcmi => SelectedTelemetrySession?.CanExportAcmi ?? false;

    public bool CanChangeSelectedTelemetrySession => SelectedTelemetrySession?.CanChange ?? false;

    public string TelemetryRecordingStateText => !TelemetryRecordingEnabled
        ? T("Off: nothing is recorded.")
        : TelemetryRecordingNow.Length > 0 ? TelemetryRecordingNow : T("On: a game's telemetry is recorded while you play it.");

    // Test seams (the tests point the config at their data folder and replace the process launcher).
    internal Func<string> TelemetryRecordingConfigFile { get; set; } = () => TelemetryRecordingConfig.DefaultPath();

    internal TimeSpan TelemetryRecordingPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal bool TelemetryRecordingPolling => telemetryRecordingPollCts != null;

    internal string TelemetryRecordHostGame => telemetryRecordHostGame;

    private TelemetryRecordingConfig telemetryRecordingConfig = new();
    private bool telemetryRecordingLoading;
    private CancellationTokenSource? telemetryRecordingPollCts;
    private IFfbHostProcess? telemetryRecordHost;
    private string telemetryRecordHostGame = string.Empty;
    private int? telemetryRecordBlockedPid;
    private int telemetryPreviewVersion;
    private bool telemetrySessionsLoaded;
    private string telemetryLastRecordingState = string.Empty;

    private void InitializeTelemetryRecording(bool readConfigFile)
    {
        foreach (TelemetryRecordingGame game in JoyPro.TelemetrySessions.TelemetryRecordingGames.All)
        {
            TelemetryRecordingGameItem item = new(game);
            item.Changed += OnTelemetryRecordingGameChanged;
            TelemetryRecordingGames.Add(item);
        }
        TelemetrySessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTelemetrySessions));
        TelemetryRecordingConfig config = new();
        if (readConfigFile)
        {
            try
            {
                config = TelemetryRecordingConfig.Load(TelemetryRecordingConfigFile());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TelemetryRecordingMessage = T("Cannot read the telemetry recording settings: {0}", ex.Message);
            }
        }
        ApplyTelemetryRecordingConfig(config);
    }

    internal void ApplyTelemetryRecordingConfig(TelemetryRecordingConfig config)
    {
        telemetryRecordingLoading = true;
        try
        {
            telemetryRecordingConfig = config.Clone();
            TelemetryRecordingEnabled = config.Enabled;
            TelemetryRecordingFolder = config.Folder;
            TelemetryRecordingKeepRaw = config.KeepRaw;
            SelectedTelemetrySampleRate = Nearest(TelemetrySampleRates, config.SampleHz);
            SelectedTelemetrySizeCap = Nearest(TelemetrySizeCaps, config.MaxTotalMb);
            SelectedTelemetryKeepDays = Nearest(TelemetryKeepDaysChoices, config.KeepDays);
            foreach (TelemetryRecordingGameItem game in TelemetryRecordingGames)
            {
                game.Loading = true;
                game.Enabled = config.GameEnabled(game.Id);
                game.Loading = false;
            }
        }
        finally
        {
            telemetryRecordingLoading = false;
        }
        UpdateTelemetryRecordingPolling();
    }

    private static TelemetryChoice? Nearest(IReadOnlyList<TelemetryChoice> choices, double value)
    {
        TelemetryChoice? exact = choices.FirstOrDefault(c => Math.Abs(c.Value - value) < 1e-9);
        if (exact != null)
            return exact;
        // A value set on the command line that the list does not have: keep it as an extra choice.
        return choices.Where(c => c.Value > 0).OrderBy(c => Math.Abs(c.Value - value)).FirstOrDefault();
    }

    // ---- settings -> file ----

    partial void OnTelemetryRecordingEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
        if (telemetryRecordingLoading)
            return;
        telemetryRecordingConfig.Enabled = value;
        telemetryRecordBlockedPid = null;
        SaveTelemetryRecordingConfig();
        UpdateTelemetryRecordingPolling();
    }

    partial void OnTelemetryRecordingFolderChanged(string value)
    {
        OnPropertyChanged(nameof(TelemetryRecordingFolderText));
        OnPropertyChanged(nameof(TelemetryRecordingUsesDefaultFolder));
        if (telemetryRecordingLoading)
            return;
        telemetryRecordingConfig.Folder = value ?? string.Empty;
        SaveTelemetryRecordingConfig();
        _ = RefreshTelemetrySessionsAsync();
    }

    partial void OnSelectedTelemetrySampleRateChanged(TelemetryChoice? value)
    {
        if (telemetryRecordingLoading || value == null)
            return;
        telemetryRecordingConfig.SampleHz = value.Value;
        SaveTelemetryRecordingConfig();
    }

    partial void OnSelectedTelemetrySizeCapChanged(TelemetryChoice? value)
    {
        if (telemetryRecordingLoading || value == null)
            return;
        telemetryRecordingConfig.MaxTotalMb = (int)value.Value;
        SaveTelemetryRecordingConfig();
    }

    partial void OnSelectedTelemetryKeepDaysChanged(TelemetryChoice? value)
    {
        if (telemetryRecordingLoading || value == null)
            return;
        telemetryRecordingConfig.KeepDays = (int)value.Value;
        SaveTelemetryRecordingConfig();
    }

    partial void OnTelemetryRecordingKeepRawChanged(bool value)
    {
        if (telemetryRecordingLoading)
            return;
        telemetryRecordingConfig.KeepRaw = value;
        SaveTelemetryRecordingConfig();
    }

    private void OnTelemetryRecordingGameChanged(TelemetryRecordingGameItem game)
    {
        if (telemetryRecordingLoading)
            return;
        telemetryRecordingConfig.Games[game.Id] = game.Enabled;
        SaveTelemetryRecordingConfig();
        if (!game.Enabled && telemetryRecordHostGame == game.Id)
            StopTelemetryRecordHost(waitForExit: false);
    }

    private void SaveTelemetryRecordingConfig()
    {
        try
        {
            telemetryRecordingConfig.Save(TelemetryRecordingConfigFile());
            TelemetryRecordingMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TelemetryRecordingMessage = T("Cannot save the telemetry recording settings: {0}", ex.Message);
            JoyProLog.Warn("Recording", "Saving the recording settings failed", ex);
            return;
        }
        JoyProLog.Info("Recording", "Settings saved: enabled " + telemetryRecordingConfig.Enabled + ", folder '" + telemetryRecordingConfig.Folder + "', sample " +
                                    telemetryRecordingConfig.SampleHz.ToString(CultureInfo.InvariantCulture) + " Hz");
        // The running processes read the file again.
        SendFfbHostCommand(HostProtocol.Reload);
        telemetryRecordHost?.Send(HostProtocol.Reload);
    }

    // Settings > Telemetry recording > Choose... (the window's folder picker calls it).
    public void SetTelemetryRecordingFolder(string path)
    {
        string full = Path.GetFullPath(path);
        TelemetryRecordingFolder = string.Equals(full.TrimEnd('\\'), TelemetrySessionStore.DefaultRecordingsFolder().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? string.Empty : full;
    }

    [RelayCommand]
    private void UseDefaultTelemetryFolder() => TelemetryRecordingFolder = string.Empty;

    [RelayCommand]
    private void OpenTelemetryFolder()
    {
        string folder = TelemetryRecordingFolderText;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TelemetryRecordingMessage = T("Cannot open {0}: {1}", folder, ex.Message);
            return;
        }
        if (SelectedTelemetrySession is { } session && File.Exists(session.Path))
            OpenExternal("explorer.exe", "/select,\"" + session.Path + "\"");
        else
            OpenExternal("explorer.exe", "\"" + folder + "\"");
    }

    // ---- sessions list ----

    // The list loads when the section is open on the Settings page (and after a session ends while it is open).
    private void SyncTelemetryRecordingSection()
    {
        if (CurrentStage == UiStage.Settings && FindSettingsSection("telemetry-recording") is { IsContentVisible: true } && !telemetrySessionsLoaded)
            _ = RefreshTelemetrySessionsAsync();
    }

    [RelayCommand]
    internal async Task RefreshTelemetrySessionsAsync()
    {
        telemetrySessionsLoaded = true;
        string folder = TelemetryRecordingFolderText;
        string? keep = SelectedTelemetrySession?.Path;
        TelemetrySessionsLoading = true;
        List<string> problems = [];
        List<TelemetrySessionInfo> sessions;
        try
        {
            sessions = await Task.Run(() => TelemetrySessionStore.List(folder, problems));
        }
        finally
        {
            TelemetrySessionsLoading = false;
        }
        TelemetrySessions.Clear();
        foreach (TelemetrySessionInfo info in sessions)
            TelemetrySessions.Add(new TelemetrySessionItem(info));
        long total = sessions.Sum(s => s.SizeBytes);
        TelemetrySessionsSummary = sessions.Count == 0
            ? T("No recordings yet in {0}.", folder)
            : Plural("{0} recording", "{0} recordings", sessions.Count) + ", " + TelemetrySessionStore.FormatSize(total) + " · " + folder;
        if (problems.Count > 0)
            TelemetryRecordingMessage = T("Some files could not be read: {0}", string.Join("; ", problems.Take(3)));
        SelectedTelemetrySession = TelemetrySessions.FirstOrDefault(s => s.Path == keep) ?? TelemetrySessions.FirstOrDefault();
    }

    partial void OnSelectedTelemetrySessionChanged(TelemetrySessionItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedTelemetrySession));
        OnPropertyChanged(nameof(CanExportSelectedTelemetryAcmi));
        OnPropertyChanged(nameof(CanChangeSelectedTelemetrySession));
        _ = LoadTelemetryPreviewAsync(value);
    }

    internal async Task LoadTelemetryPreviewAsync(TelemetrySessionItem? session)
    {
        int version = Interlocked.Increment(ref telemetryPreviewVersion);
        if (session == null)
        {
            TelemetryPreviewTracks = null;
            TelemetryPreviewText = string.Empty;
            return;
        }
        TelemetryPreviewText = T("Reading {0}...", session.FileName);
        List<TelemetryPreviewSeries> series;
        try
        {
            series = await Task.Run(() => TelemetryPreview.Read(session.Path, null, 400, 3));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (version == telemetryPreviewVersion)
            {
                TelemetryPreviewTracks = null;
                TelemetryPreviewText = T("Cannot read {0}: {1}", session.FileName, ex.Message);
            }
            return;
        }
        if (version != telemetryPreviewVersion)
            return;
        double duration = Math.Max(session.Info.Duration, series.SelectMany(s => s.Times).DefaultIfEmpty(0).Max());
        TelemetryPreviewTracks = series.Select(s => new TelemetryPreviewTrack(
            s.Title + (s.Unit.Length > 0 ? " [" + s.Unit + "]" : string.Empty) + "  (" + s.Key + ")",
            s.Times, s.Values, s.Min, s.Max, duration)).ToList();
        TelemetryPreviewText = series.Count == 0
            ? T("No values in this recording.")
            : T("{0} · {1} · {2}", session.Game, session.Aircraft, session.Duration);
    }

    [RelayCommand]
    private Task ExportTelemetryAcmiAsync() => ExportSelectedTelemetryAsync("acmi-zip");

    [RelayCommand]
    private Task ExportTelemetryCsvAsync() => ExportSelectedTelemetryAsync("csv");

    internal async Task<string?> ExportSelectedTelemetryAsync(string format)
    {
        TelemetrySessionItem? session = SelectedTelemetrySession;
        if (session == null)
            return null;
        string target = TelemetrySessionStore.ExportPath(session.Path, format);
        TelemetryRecordingMessage = format == "csv" ? T("Exporting {0} as CSV...", session.FileName) : T("Exporting {0} for Tacview...", session.FileName);
        try
        {
            TelemetryExportResult result = await Task.Run(() => format == "csv"
                ? TelemetryCsvExport.Export(session.Path, target)
                : TelemetryAcmiExport.Export(session.Path, target, zip: true));
            TelemetryRecordingMessage = format == "csv"
                ? T("Exported: {0} ({1} rows, {2} files)", target, result.Rows.ToString("N0", CultureInfo.CurrentCulture), result.Files.Count)
                : T("Exported for Tacview: {0}", target);
            JoyProLog.Info("Recording", "Exported " + session.Path + " as " + format + " to " + target);
            if (format == "csv")
                OpenExternal("explorer.exe", "\"" + target + "\"");
            else
                OpenExternal("explorer.exe", "/select,\"" + target + "\"");
            return target;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            TelemetryRecordingMessage = T("Export failed: {0}", Loc.Translate(ex.Message));
            JoyProLog.Warn("Recording", "Export failed: " + session.Path, ex);
            return null;
        }
    }

    [RelayCommand]
    internal async Task DeleteTelemetrySessionAsync()
    {
        TelemetrySessionItem? session = SelectedTelemetrySession;
        if (session == null)
            return;
        bool ok = await ConfirmAsync(T("Delete recording"),
            T("Delete the recording of {0} ({1}, {2}, {3})? The file is deleted, not moved to the recycle bin.", session.Game, session.Date, session.Duration, session.Size),
            "Delete", isDanger: true);
        if (!ok)
            return;
        List<string> problems = [];
        if (TelemetrySessionStore.Delete(session.Path, problems))
        {
            TelemetryRecordingMessage = T("Deleted {0}.", session.FileName);
            JoyProLog.Info("Recording", "Deleted " + session.Path);
        }
        else
        {
            TelemetryRecordingMessage = T("Not deleted: {0}", string.Join("; ", problems));
        }
        await RefreshTelemetrySessionsAsync();
    }

    // ---- process manager ----

    private void UpdateTelemetryRecordingPolling()
    {
        if (TelemetryRecordingEnabled)
        {
            if (telemetryRecordingPollCts == null)
            {
                telemetryRecordingPollCts = new CancellationTokenSource();
                _ = RunTelemetryRecordingPollAsync(telemetryRecordingPollCts.Token);
            }
        }
        else
        {
            telemetryRecordingPollCts?.Cancel();
            telemetryRecordingPollCts?.Dispose();
            telemetryRecordingPollCts = null;
            StopTelemetryRecordHost(waitForExit: false);
            TelemetryRecordingNow = string.Empty;
        }
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
    }

    private async Task RunTelemetryRecordingPollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            PollTelemetryRecordingGamesOnce();
            try
            {
                await Task.Delay(TelemetryRecordingPollInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // True when the telemetry force feedback process runs or will run for this game (it records then). Not when force
    // feedback failed for this game process (no device, the device held by the game): then it is not restarted, and
    // the record process records it.
    internal bool FfbCoversRecordingGame(string gameId, int? gamePid = null)
    {
        if (ffbHost != null)
            return string.Equals(ffbHostGame, gameId, StringComparison.OrdinalIgnoreCase);
        if (!FfbTelemetryEnabled || !FfbTelemetryGames.Any(g => g.Enabled && string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase)))
            return false;
        return gamePid == null || gamePid != ffbBlockedGamePid;
    }

    // One look for a running game that recording is on for; starts JoyPro.Ffb record for the first one force feedback
    // does not cover.
    internal void PollTelemetryRecordingGamesOnce()
    {
        if (!TelemetryRecordingEnabled)
            return;
        if (telemetryRecordHost != null)
        {
            if (FfbCoversRecordingGame(telemetryRecordHostGame))
                StopTelemetryRecordHost(waitForExit: true);
            return;
        }
        foreach (TelemetryRecordingGameItem game in TelemetryRecordingGames)
        {
            if (!game.Enabled)
                continue;
            int? pid = FfbFindGameProcess(game.ProcessNames);
            if (pid is int found && found != telemetryRecordBlockedPid && !FfbCoversRecordingGame(game.Id, found))
            {
                StartTelemetryRecordHost(game, found);
                return;
            }
        }
    }

    private void StartTelemetryRecordHost(TelemetryRecordingGameItem game, int gamePid)
    {
        List<string> args =
        [
            "record", "--game", game.Id,
            "--game-pid", gamePid.ToString(CultureInfo.InvariantCulture),
            "--parent-pid", FfbTelemetryGuiPid.ToString(CultureInfo.InvariantCulture),
            "--protocol"
        ];
        IFfbHostProcess process;
        try
        {
            process = FfbHostLauncher.Start(FfbTelemetryExePath, args);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            telemetryRecordBlockedPid = gamePid;
            TelemetryRecordingNow = T("Not recording {0}: JoyPro.Ffb could not start ({1})", game.Title, ex.Message);
            OnPropertyChanged(nameof(TelemetryRecordingStateText));
            return;
        }
        telemetryRecordHost = process;
        telemetryRecordHostGame = game.Id;
        TelemetryRecordingNow = T("{0} runs: waiting for its telemetry.", game.Title);
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
        process.LineReceived += line => FfbTelemetryDispatch(() => OnTelemetryRecordHostLine(process, line));
        process.Exited += () => FfbTelemetryDispatch(() => OnTelemetryRecordHostExited(process, gamePid));
        JoyProLog.Info("Recording", "JoyPro.Ffb record started for " + game.Id + " (game pid " + gamePid.ToString(CultureInfo.InvariantCulture) + ")");
        if (process.HasExited)
            OnTelemetryRecordHostExited(process, gamePid);
    }

    // Before telemetry force feedback starts for a game, a record process for it gives the port back.
    internal void StopTelemetryRecordHostFor(string gameId)
    {
        if (telemetryRecordHost != null && string.Equals(telemetryRecordHostGame, gameId, StringComparison.OrdinalIgnoreCase))
            StopTelemetryRecordHost(waitForExit: true);
    }

    private void StopTelemetryRecordHost(bool waitForExit)
    {
        IFfbHostProcess? process = telemetryRecordHost;
        if (process == null)
            return;
        telemetryRecordHost = null;
        telemetryRecordHostGame = string.Empty;
        process.Send(HostProtocol.Stop);
        if (waitForExit)
            EndFfbHost(process);
        else
            _ = Task.Run(() => EndFfbHost(process));
        TelemetryRecordingNow = string.Empty;
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
    }

    public void StopTelemetryRecordingForShutdown()
    {
        telemetryRecordingPollCts?.Cancel();
        telemetryRecordingPollCts = null;
        StopTelemetryRecordHost(waitForExit: true);
    }

    private void OnTelemetryRecordHostExited(IFfbHostProcess process, int gamePid)
    {
        if (!ReferenceEquals(process, telemetryRecordHost))
            return;
        telemetryRecordHost = null;
        telemetryRecordHostGame = string.Empty;
        process.Dispose();
        // Not again for the same game process (no restart loop); the next game start records again.
        telemetryRecordBlockedPid = gamePid;
        TelemetryRecordingNow = string.Empty;
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
        if (telemetrySessionsLoaded)
            _ = RefreshTelemetrySessionsAsync();
    }

    private void OnTelemetryRecordHostLine(IFfbHostProcess process, string line)
    {
        if (!ReferenceEquals(process, telemetryRecordHost))
            return;
        JsonObject? json = HostProtocol.Parse(line);
        if (json != null && FfbNodeString(json, "type") == "recording")
            OnTelemetryRecordingLine(json);
    }

    // A "recording" line of either process (JoyPro.Ffb record, or run while force feedback runs).
    internal void OnTelemetryRecordingLine(JsonObject json)
    {
        string state = FfbNodeString(json, "state");
        string game = FfbNodeString(json, "game");
        string title = JoyPro.TelemetrySessions.TelemetryRecordingGames.Find(game)?.Title ?? game;
        string aircraft = FfbNodeString(json, "aircraft");
        string session = FfbNodeString(json, "session");
        long frames = json["frames"] is JsonValue f && f.TryGetValue<long>(out long n) ? n : 0;
        string error = FfbNodeString(json, "error");
        bool wasRecording = telemetryLastRecordingState == "recording";
        telemetryLastRecordingState = state;
        TelemetryRecordingNow = state switch
        {
            "recording" => T("Recording {0}{1}: {2} ({3} frames)", title, aircraft.Length > 0 ? " - " + aircraft : string.Empty, Path.GetFileName(session), frames.ToString("N0", CultureInfo.CurrentCulture)),
            "listening" => T("{0} runs: waiting for its telemetry.", title) + (error.Length > 0 ? " " + error : string.Empty),
            "waiting" => string.Empty,
            _ => string.Empty
        };
        OnPropertyChanged(nameof(TelemetryRecordingStateText));
        // A session ended (recording -> listening): the list shows it.
        if (wasRecording && state != "recording" && telemetrySessionsLoaded)
            _ = RefreshTelemetrySessionsAsync();
    }
}
