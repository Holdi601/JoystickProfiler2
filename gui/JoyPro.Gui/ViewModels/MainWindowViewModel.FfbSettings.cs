using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// The game force feedback panel (Settings page): every setting `engine ffb models` lists, per game, device and (DCS)
// aircraft. The engine stores them in the profile (ffb set / clear auto-save); the export writes them into the game.
// Game-wide settings (IL-2) use the device "*"; values not stored are left as the game file has them.
public sealed partial class MainWindowViewModel
{
    public const string FfbAnyDevice = "*";

    public ObservableCollection<FfbGameItem> FfbGames { get; } = [];

    public ObservableCollection<FfbPickerOption> FfbDeviceOptions { get; } = [];

    public ObservableCollection<FfbPickerOption> FfbPlaneOptions { get; } = [];

    public ObservableCollection<FfbParameterEditor> FfbEditors { get; } = [];

    [ObservableProperty]
    private bool isFfbSettingsExpanded;

    [ObservableProperty]
    private FfbGameItem? selectedFfbGame;

    [ObservableProperty]
    private FfbPickerOption? selectedFfbDevice;

    [ObservableProperty]
    private FfbPickerOption? selectedFfbPlane;

    [ObservableProperty]
    private string ffbStatus = "Expand to load the games' force feedback settings.";

    [ObservableProperty]
    private string ffbValidation = string.Empty;

    [ObservableProperty]
    private bool ffbLoaded;

    [ObservableProperty]
    private bool ffbHasEdits;

    [ObservableProperty]
    private bool ffbHasStored;

    public bool FfbNeedsDevice => SelectedFfbGame?.PerDevice ?? false;

    public bool FfbHasPlanes => SelectedFfbGame?.HasPlanes ?? false;

    public string FfbGameDescription => SelectedFfbGame?.Description ?? string.Empty;

    public bool CanApplyFfb => FfbLoaded && FfbHasEdits && FfbValidation.Length == 0;

    public bool CanClearFfb => FfbLoaded && FfbHasStored;

    // Every stored entry of the profile ("<device>|game:<game>" / "<device>|plane:<game>:<plane>" -> "k=v;...").
    private Dictionary<string, string> ffbEntries = new(StringComparer.OrdinalIgnoreCase);
    private bool ffbModelsLoaded;
    private bool ffbStoredLoaded;
    private bool ffbRebuildingPickers;
    private int ffbLoadVersion;

    partial void OnIsFfbSettingsExpandedChanged(bool value)
    {
        if (value && !ffbModelsLoaded)
            _ = RefreshFfbSettingsAsync();
        else if (value && !ffbStoredLoaded)
            _ = ReloadFfbStoredAsync();
    }

    partial void OnSelectedFfbGameChanged(FfbGameItem? value)
    {
        OnPropertyChanged(nameof(FfbNeedsDevice));
        OnPropertyChanged(nameof(FfbHasPlanes));
        OnPropertyChanged(nameof(FfbGameDescription));
        RebuildFfbPickers();
        LoadFfbEditors();
    }

    partial void OnSelectedFfbDeviceChanged(FfbPickerOption? value)
    {
        if (!ffbRebuildingPickers)
            LoadFfbEditors();
    }

    partial void OnSelectedFfbPlaneChanged(FfbPickerOption? value)
    {
        if (!ffbRebuildingPickers)
            LoadFfbEditors();
    }

    partial void OnFfbValidationChanged(string value) => OnPropertyChanged(nameof(CanApplyFfb));

    partial void OnFfbLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApplyFfb));
        OnPropertyChanged(nameof(CanClearFfb));
    }

    partial void OnFfbHasEditsChanged(bool value) => OnPropertyChanged(nameof(CanApplyFfb));

    partial void OnFfbHasStoredChanged(bool value) => OnPropertyChanged(nameof(CanClearFfb));

    // Another profile: its settings are loaded when the panel is opened again or reloaded (not on every keystroke of a
    // typed profile path).
    private void OnFfbProfileChanged()
    {
        ffbEntries.Clear();
        ffbStoredLoaded = false;
        LoadFfbEditors();
        if (ffbModelsLoaded)
            FfbStatus = T("Another profile: press Reload to load its force feedback settings.");
    }

    // ffb models (the games and their settings), then ffb show (what the profile stores).
    [RelayCommand]
    private async Task RefreshFfbSettingsAsync()
    {
        int version = ++ffbLoadVersion;
        FfbLoaded = false;
        FfbStatus = T("Loading...");
        EngineCommandResult? result = await RunEngineAsync(["ffb", "models"], includeProfile: false, mutatesProfile: false,
            busyLabel: N("Loading force feedback settings..."), forceText: false);
        if (version != ffbLoadVersion)
            return;
        if (result == null || result.ExitCode != 0)
        {
            FfbStatus = T("Could not load the force feedback models (see output).");
            return;
        }
        List<FfbGameItem> games;
        try
        {
            games = ParseFfbModels(result.StandardOutput);
        }
        catch (JsonException)
        {
            FfbStatus = T("The engine's force feedback models could not be read.");
            return;
        }

        string previous = SelectedFfbGame?.Game ?? string.Empty;
        ffbModelsLoaded = true;
        allFfbGames.Clear();
        allFfbGames.AddRange(games);
        FfbGames.Clear();
        foreach (FfbGameItem game in games.Where(g => !IsGameHidden(g.Game)))
            FfbGames.Add(game);
        await ReloadFfbStoredAsync();
        if (version != ffbLoadVersion)
            return;
        SelectedFfbGame = FfbGames.FirstOrDefault(g => string.Equals(g.Game, previous, StringComparison.OrdinalIgnoreCase)) ?? FfbGames.FirstOrDefault();
        if (FfbGames.Count == 0)
            FfbStatus = T("The engine manages no force feedback settings.");
    }

    // Every game with force feedback settings; FfbGames leaves out the games the user does not have (Settings > Games).
    private readonly List<FfbGameItem> allFfbGames = [];

    private void RebuildVisibleFfbGames()
    {
        if (!ffbModelsLoaded)
            return;
        List<FfbGameItem> visible = allFfbGames.Where(g => !IsGameHidden(g.Game)).ToList();
        if (visible.SequenceEqual(FfbGames))
            return;
        FfbGameItem? previous = SelectedFfbGame;
        FfbGames.Clear();
        foreach (FfbGameItem game in visible)
            FfbGames.Add(game);
        SelectedFfbGame = previous != null && FfbGames.Contains(previous) ? previous : FfbGames.FirstOrDefault();
    }

    internal static List<FfbGameItem> ParseFfbModels(string output)
    {
        List<FfbGameItem> games = [];
        using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
        if (!doc.RootElement.TryGetProperty("Models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            return games;
        foreach (JsonElement model in models.EnumerateArray())
        {
            List<FfbParameterInfo> parameters = [];
            if (model.TryGetProperty("Parameters", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in list.EnumerateArray())
                {
                    string key = GetJsonString(p, "Key");
                    if (key.Length == 0)
                        continue;
                    parameters.Add(new FfbParameterInfo(
                        key,
                        GetJsonString(p, "Label") is { Length: > 0 } label ? label : key,
                        GetJsonNullableBool(p, "Toggle") ?? false,
                        GetJsonNullableDouble(p, "Min") ?? 0,
                        GetJsonNullableDouble(p, "Max") ?? 1,
                        GetJsonNullableDouble(p, "Default") ?? 0,
                        GetJsonNullableBool(p, "GameWide") ?? false,
                        GetJsonString(p, "Help")));
                }
            }
            string game = GetJsonString(model, "Game");
            if (game.Length > 0 && parameters.Count > 0)
                games.Add(new FfbGameItem(game, DisplayText.GameName(game, GetJsonString(model, "Title") is { Length: > 0 } title ? title : game), GetJsonString(model, "Description"), parameters));
        }
        return games;
    }

    // ffb show without a filter: every stored entry, which also names the devices and aircraft that have settings.
    private async Task ReloadFfbStoredAsync()
    {
        int version = ffbLoadVersion;
        ffbStoredLoaded = false;
        LoadFfbEditors();
        if (string.IsNullOrWhiteSpace(ProfilePath))
        {
            FfbStatus = T("Load or create a profile first: the force feedback settings are stored in it.");
            return;
        }
        EngineCommandResult? result = await RunEngineAsync(["ffb", "show"], includeProfile: true, mutatesProfile: false,
            busyLabel: N("Loading stored force feedback settings..."), forceText: false);
        if (version != ffbLoadVersion)
            return;
        if (result == null || result.ExitCode != 0)
        {
            FfbStatus = T("Could not load the stored force feedback settings (see output).");
            return;
        }
        Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            if (doc.RootElement.TryGetProperty("Entries", out JsonElement list) && list.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in list.EnumerateObject())
                    entries[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() ?? string.Empty : string.Empty;
            }
        }
        catch (JsonException)
        {
            FfbStatus = T("The engine's stored force feedback settings could not be read.");
            return;
        }
        ffbEntries = entries;
        ffbStoredLoaded = true;
        RebuildFfbPickers();
        LoadFfbEditors();
    }

    // Devices: those with stored settings for the game, connected ones and the profile's (DCS: also its config folders).
    private void RebuildFfbPickers()
    {
        FfbGameItem? game = SelectedFfbGame;
        string device = SelectedFfbDevice?.Value ?? string.Empty;
        string plane = SelectedFfbPlane?.Value ?? string.Empty;
        ffbRebuildingPickers = true;
        try
        {
            FfbDeviceOptions.Clear();
            FfbPlaneOptions.Clear();
            if (game == null)
            {
                SelectedFfbDevice = null;
                SelectedFfbPlane = null;
                return;
            }

            if (game.PerDevice)
            {
                HashSet<string> stored = new(StoredFfbKeys(game.Game).Select(k => k.Device).Where(d => d != FfbAnyDevice), StringComparer.OrdinalIgnoreCase);
                HashSet<string> connected = new(ConnectedDevices.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()), StringComparer.OrdinalIgnoreCase);
                HashSet<string> profile = new(ProfileDevices.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()), StringComparer.OrdinalIgnoreCase);
                HashSet<string> all = new(stored.Concat(connected).Concat(profile), StringComparer.OrdinalIgnoreCase);
                if (game.HasPlanes)
                    all.UnionWith(DcsKnownDevices.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()));
                foreach (string name in all.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    List<string> marks = [];
                    if (stored.Contains(name))
                        marks.Add("stored");
                    if (connected.Contains(name))
                        marks.Add("connected");
                    if (profile.Contains(name))
                        marks.Add(T("in profile"));
                    FfbDeviceOptions.Add(new FfbPickerOption(name, marks.Count == 0 ? name : name + "  (" + string.Join(", ", marks) + ")"));
                }
            }

            if (game.HasPlanes)
            {
                FfbPlaneOptions.Add(new FfbPickerOption(string.Empty, T("All aircraft")));
                HashSet<string> storedPlanes = new(StoredFfbKeys(game.Game).Where(k => k.Plane.Length > 0).Select(k => k.Plane), StringComparer.OrdinalIgnoreCase);
                foreach (string name in storedPlanes.Concat(BuildDcsRefreshPlaneCandidates())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    FfbPlaneOptions.Add(new FfbPickerOption(name, storedPlanes.Contains(name) ? T("{0}  (stored)", name) : name));
                }
            }

            SelectedFfbDevice = FfbDeviceOptions.FirstOrDefault(o => string.Equals(o.Value, device, StringComparison.OrdinalIgnoreCase))
                                ?? FfbDeviceOptions.FirstOrDefault(o => o.Label.Contains("(stored", StringComparison.Ordinal))
                                ?? FfbDeviceOptions.FirstOrDefault();
            SelectedFfbPlane = FfbPlaneOptions.FirstOrDefault(o => string.Equals(o.Value, plane, StringComparison.OrdinalIgnoreCase))
                               ?? FfbPlaneOptions.FirstOrDefault();
        }
        finally
        {
            ffbRebuildingPickers = false;
        }
    }

    // The stored keys of a game, split into device and aircraft ("" = all aircraft).
    private IEnumerable<(string Device, string Plane)> StoredFfbKeys(string game)
    {
        foreach (string key in ffbEntries.Keys)
        {
            int bar = key.IndexOf('|');
            if (bar <= 0)
                continue;
            string scope = key.Substring(bar + 1);
            if (string.Equals(scope, "game:" + game, StringComparison.OrdinalIgnoreCase))
                yield return (key.Substring(0, bar), string.Empty);
            else if (scope.StartsWith("plane:" + game + ":", StringComparison.OrdinalIgnoreCase))
                yield return (key.Substring(0, bar), scope.Substring(("plane:" + game + ":").Length));
        }
    }

    private static string FfbKey(string device, string game, string plane) =>
        device + "|" + (plane.Length == 0 ? "game:" + game : "plane:" + game + ":" + plane);

    private Dictionary<string, double> StoredFfbValues(string device, string game, string plane)
    {
        Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
        if (!ffbEntries.TryGetValue(FfbKey(device, game, plane), out string? text))
            return values;
        foreach (string part in text.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && double.TryParse(part.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                values[part.Substring(0, eq).Trim()] = number;
        }
        return values;
    }

    // The device a setting is stored under: "*" for game-wide settings, else the picked device ("" = none picked).
    private string FfbDeviceFor(FfbParameterInfo parameter) =>
        parameter.GameWide ? FfbAnyDevice : (SelectedFfbDevice?.Value ?? string.Empty).Trim();

    // Game-wide settings ignore the aircraft: they are stored for the whole game.
    private string FfbPlaneFor(FfbParameterInfo parameter) =>
        parameter.GameWide || SelectedFfbGame?.HasPlanes != true ? string.Empty : (SelectedFfbPlane?.Value ?? string.Empty).Trim();

    // Fills the editors from the stored entries: the scope's own value, else (aircraft) the game's, else the game default.
    private void LoadFfbEditors()
    {
        foreach (FfbParameterEditor old in FfbEditors)
            old.Changed -= OnFfbEditorChanged;
        FfbEditors.Clear();
        FfbValidation = string.Empty;
        FfbHasEdits = false;
        FfbHasStored = false;
        FfbGameItem? game = SelectedFfbGame;
        if (game == null || !ffbModelsLoaded || !ffbStoredLoaded)
        {
            FfbLoaded = false;
            return;
        }
        if (game.PerDevice && string.IsNullOrWhiteSpace(SelectedFfbDevice?.Value))
        {
            FfbLoaded = false;
            FfbStatus = T("Pick a device: {0} keeps force feedback settings per device.", game.Title);
            return;
        }

        foreach (FfbParameterInfo parameter in game.Parameters)
        {
            string device = FfbDeviceFor(parameter);
            string plane = FfbPlaneFor(parameter);
            Dictionary<string, double> own = StoredFfbValues(device, game.Game, plane);
            Dictionary<string, double> inherited = plane.Length > 0 ? StoredFfbValues(device, game.Game, string.Empty) : new(StringComparer.OrdinalIgnoreCase);
            FfbParameterEditor editor = new(parameter);
            editor.Load(
                own.TryGetValue(parameter.Key, out double stored) ? stored : null,
                inherited.TryGetValue(parameter.Key, out double fromGame) ? fromGame : null,
                "all aircraft");
            editor.Changed += OnFfbEditorChanged;
            FfbEditors.Add(editor);
        }
        FfbHasStored = FfbEditors.Any(e => e.IsStored);
        FfbStatus = FfbScopeLabel() + ": " + (FfbHasStored
            ? T("{0} of {1} settings stored.", FfbEditors.Count(e => e.IsStored).ToString(CultureInfo.InvariantCulture), FfbEditors.Count.ToString(CultureInfo.InvariantCulture))
            : T("nothing stored; the game keeps its own values."));
        FfbLoaded = true;
    }

    private string FfbScopeLabel()
    {
        FfbGameItem? game = SelectedFfbGame;
        if (game == null)
            return string.Empty;
        string label = game.Title;
        if (game.PerDevice)
            label += " / " + (SelectedFfbDevice?.Value ?? string.Empty);
        else
            label += T(" (all devices)");
        if (game.HasPlanes)
            label += " / " + (string.IsNullOrWhiteSpace(SelectedFfbPlane?.Value) ? "all aircraft" : SelectedFfbPlane!.Value);
        return label;
    }

    private void OnFfbEditorChanged(object? sender, EventArgs e)
    {
        FfbValidation = string.Join(" ", FfbEditors.Where(x => !x.IsValid).Select(x => x.ValidationMessage));
        FfbHasEdits = FfbEditors.Any(x => x.HasEdit);
    }

    // ffb set with the changed settings only, one call per device / aircraft scope they are stored under.
    internal List<List<string>> BuildFfbSetArguments(out string error)
    {
        error = string.Empty;
        FfbGameItem? game = SelectedFfbGame;
        if (game == null || !FfbLoaded)
        {
            error = T("Pick a game (and device) first.");
            return [];
        }
        if (FfbValidation.Length > 0)
        {
            error = FfbValidation;
            return [];
        }
        List<List<string>> calls = [];
        foreach (IGrouping<(string Device, string Plane), FfbParameterEditor> scope in FfbEditors
                     .Where(e => e.HasEdit)
                     .GroupBy(e => (FfbDeviceFor(e.Parameter), FfbPlaneFor(e.Parameter))))
        {
            List<string> args = ["ffb", "set", "--device", scope.Key.Device, "--game", game.Game];
            if (scope.Key.Plane.Length > 0)
                args.AddRange(["--plane", scope.Key.Plane]);
            foreach (FfbParameterEditor editor in scope)
                args.AddRange(["--param", editor.Key + "=" + editor.FormatCurrent()]);
            calls.Add(args);
        }
        if (calls.Count == 0)
            error = T("Nothing changed.");
        return calls;
    }

    [RelayCommand]
    private async Task ApplyFfbSettingsAsync()
    {
        List<List<string>> calls = BuildFfbSetArguments(out string error);
        if (calls.Count == 0)
        {
            StatusText = error;
            return;
        }
        string label = FfbScopeLabel();
        foreach (List<string> args in calls)
        {
            EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
                busyLabel: N("Saving force feedback settings..."), forceText: false);
            if (result?.ExitCode != 0)
                return;
        }
        await ReloadFfbStoredAsync();
        StatusText = T("Force feedback saved for {0} (written into the game on export).", label);
    }

    // ffb clear for the whole scope (every device / aircraft scope the shown settings are stored under).
    [RelayCommand]
    private async Task ClearFfbSettingsAsync()
    {
        FfbGameItem? game = SelectedFfbGame;
        if (game == null || !FfbLoaded)
            return;
        string label = FfbScopeLabel();
        foreach ((string device, string plane) in FfbEditors.Where(e => e.IsStored).Select(e => (FfbDeviceFor(e.Parameter), FfbPlaneFor(e.Parameter))).Distinct().ToList())
        {
            if (!await RunFfbClearAsync(game.Game, device, plane, null))
                return;
        }
        await ReloadFfbStoredAsync();
        StatusText = T("Force feedback settings cleared for {0}.", label);
    }

    // Drops one stored setting; the game then keeps its own value (or the aircraft takes the game's).
    [RelayCommand]
    private async Task UnsetFfbParameterAsync(FfbParameterEditor? editor)
    {
        FfbGameItem? game = SelectedFfbGame;
        if (game == null || editor == null || !editor.IsStored)
            return;
        if (!await RunFfbClearAsync(game.Game, FfbDeviceFor(editor.Parameter), FfbPlaneFor(editor.Parameter), editor.Key))
            return;
        await ReloadFfbStoredAsync();
        StatusText = T("{0} is no longer stored for {1}.", editor.Label, FfbScopeLabel());
    }

    private async Task<bool> RunFfbClearAsync(string game, string device, string plane, string? parameter)
    {
        List<string> args = ["ffb", "clear", "--device", device, "--game", game];
        if (plane.Length > 0)
            args.AddRange(["--plane", plane]);
        if (parameter != null)
            args.AddRange(["--param", parameter]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: true, mutatesProfile: true,
            busyLabel: N("Clearing force feedback settings..."), forceText: false);
        return result?.ExitCode == 0;
    }

    [RelayCommand]
    private void RevertFfbEdits()
    {
        foreach (FfbParameterEditor editor in FfbEditors)
            editor.Revert();
        FfbValidation = string.Empty;
        FfbHasEdits = false;
    }
}
