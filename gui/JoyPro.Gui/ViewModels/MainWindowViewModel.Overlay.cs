using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The overlay (like JoyPro 1's): a small always-on-top window that shows what a pressed button does in the game that
// runs, e.g. "Gear Toggle (Landing Gear Lever - UP/DOWN)". The game is found by its process; DCS also tells the
// aircraft through a small Export.lua addition (engine overlay dcs-hook, installed only on request). Buttons come from
// the DirectInput monitor, the binds from engine overlay map.
public sealed partial class MainWindowViewModel
{
    public const string OverlayAutomaticPlane = "Automatic (from DCS)";

    public ObservableCollection<OverlayLine> OverlayLines { get; } = [];

    public ObservableCollection<string> OverlayPlaneOptions { get; } = [OverlayAutomaticPlane];

    public IReadOnlyList<string> OverlayCorners { get; } = ["Top left", "Top right", "Bottom left", "Bottom right"];

    // Set by the main window; null in tests and when no window can be shown.
    public IOverlayPresenter? OverlayPresenter { get; set; }

    [ObservableProperty]
    private bool isOverlayRunning;

    [ObservableProperty]
    private string overlayStatus = "The HUD is off.";

    [ObservableProperty]
    private string overlayHeader = string.Empty;

    [ObservableProperty]
    private string overlayCorner = "Top left";

    [ObservableProperty]
    private decimal overlayMaxLines = 6;

    [ObservableProperty]
    private decimal overlaySeconds = 4;

    [ObservableProperty]
    private decimal overlayFontSize = 20;

    [ObservableProperty]
    private bool overlayShowDevice;

    [ObservableProperty]
    private bool overlayShowHeader = true;

    [ObservableProperty]
    private bool overlayClickThrough = true;

    [ObservableProperty]
    private bool overlayOnlyInGame = true;

    [ObservableProperty]
    private string overlayPlaneChoice = OverlayAutomaticPlane;

    [ObservableProperty]
    private string dcsHookStatus = string.Empty;

    private CancellationTokenSource? overlayCts;
    private OverlayResolver? overlayResolver;
    private Dictionary<string, List<string>> overlayProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private string overlayGame = string.Empty;
    private string overlayUnit = string.Empty;
    private string overlayPlane = string.Empty;

    public string OverlayToggleLabel => IsOverlayRunning ? T("Stop HUD") : T("Start HUD");

    partial void OnIsOverlayRunningChanged(bool value) => OnPropertyChanged(nameof(OverlayToggleLabel));

    partial void OnOverlayPlaneChoiceChanged(string value)
    {
        SaveUiSettings();
        if (IsOverlayRunning)
            _ = ReloadOverlayMapAsync();
    }

    partial void OnOverlayCornerChanged(string value)
    {
        SaveUiSettings();
        if (IsOverlayRunning)
            OverlayPresenter?.Show(this);
    }
    partial void OnOverlayMaxLinesChanged(decimal value) => SaveUiSettings();
    partial void OnOverlaySecondsChanged(decimal value) => SaveUiSettings();
    partial void OnOverlayFontSizeChanged(decimal value) => SaveUiSettings();
    partial void OnOverlayShowDeviceChanged(bool value) => SaveUiSettings();
    partial void OnOverlayShowHeaderChanged(bool value) => SaveUiSettings();
    partial void OnOverlayClickThroughChanged(bool value) => SaveUiSettings();
    partial void OnOverlayOnlyInGameChanged(bool value) => SaveUiSettings();

    [RelayCommand]
    private async Task ToggleOverlayAsync()
    {
        if (IsOverlayRunning)
        {
            StopOverlay();
            return;
        }
        await StartOverlayAsync();
    }

    private async Task StartOverlayAsync()
    {
        overlayCts = new CancellationTokenSource();
        CancellationToken token = overlayCts.Token;
        IsOverlayRunning = true;
        OverlayLines.Clear();
        if (!hudLoaded)
            await LoadHudDevicesAsync();
        overlayGame = DetectRunningGame();
        await ReloadOverlayMapAsync();
        overlayGame = DetectRunningGame();
        if (overlayGame.Length > 0)
            await ReloadOverlayMapAsync();

        // Devices, axes and buttons need no profile; what the buttons do does.
        if (string.IsNullOrWhiteSpace(ProfilePath))
            OverlayStatus = T("The HUD is on: your devices. Load a profile to also see what each button does in the game.");
        StartInputMonitor();
        _ = Task.Run(() => ListenForDcsAsync(token), CancellationToken.None);
        _ = OverlayLoopAsync(token);
        OverlayPresenter?.Show(this);
    }

    public void StopOverlayForShutdown()
    {
        if (IsOverlayRunning)
            StopOverlay();
    }

    private void StopOverlay()
    {
        overlayCts?.Cancel();
        overlayCts = null;
        OverlayPresenter?.Hide();
        OverlayLines.Clear();
        IsOverlayRunning = false;
        StopInputMonitorIfIdle();
        OverlayStatus = T("The HUD is off.");
    }

    internal void OnOverlayInput(JoystickInputEvent input)
    {
        OverlayResolver.Entry? entry = overlayResolver?.Handle(input.Device, input.Input, input.Down);
        if (entry == null || (OverlayOnlyInGame && overlayGame.Length == 0))
            return;
        string detail = entry.Title;
        if (OverlayShowDevice)
        {
            string where = ShortDeviceName(input.Device) + " " + input.Input + string.Concat(entry.Modifiers.Select(m => " + " + m.Name));
            detail = detail.Length > 0 ? detail + " · " + where : where;
        }
        OverlayLine? same = OverlayLines.FirstOrDefault(l => l.Text == entry.Relation && l.Detail == detail);
        if (same != null)
            OverlayLines.Remove(same);
        HorizontalAlignment align = OverlayCorner.EndsWith("right", StringComparison.OrdinalIgnoreCase) ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        OverlayLine line = new(entry.Relation, detail, (double)OverlayFontSize, align);
        // Newest next to the corner: on top for the top corners, at the bottom for the bottom corners.
        if (OverlayCorner.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase))
            OverlayLines.Add(line);
        else
            OverlayLines.Insert(0, line);
        while (OverlayLines.Count > Math.Max(1, (int)OverlayMaxLines))
            OverlayLines.Remove(OverlayCorner.StartsWith("Bottom", StringComparison.OrdinalIgnoreCase) ? OverlayLines[0] : OverlayLines[^1]);
    }

    private static string ShortDeviceName(string device) => DisplayText.ShortDevice(device);

    // Fades and removes old lines; checks every 2 s which game runs.
    private async Task OverlayLoopAsync(CancellationToken token)
    {
        int tick = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            double life = Math.Max(1, (double)OverlaySeconds);
            foreach (OverlayLine line in OverlayLines.ToList())
            {
                double age = (DateTime.UtcNow - line.Shown).TotalSeconds;
                if (age >= life)
                    OverlayLines.Remove(line);
                else
                    line.Opacity = age > life - 1 ? Math.Max(0, life - age) : 1;
            }
            if (++tick % 10 != 0)
                continue;
            string game = DetectRunningGame();
            if (game != overlayGame)
            {
                overlayGame = game;
                if (game != "DCS")
                    overlayUnit = string.Empty;
                await ReloadOverlayMapAsync();
            }
        }
    }

    private string DetectRunningGame()
    {
        // DCS, IL-2 and Star Citizen first, then the registry games.
        IEnumerable<KeyValuePair<string, List<string>>> ordered = overlayProcessNames
            .OrderBy(p => p.Key == "DCS" ? 0 : p.Key == "IL2Game" ? 1 : p.Key == "StarCitizen" ? 2 : 3);
        foreach (KeyValuePair<string, List<string>> game in ordered)
        {
            // A hidden game (Settings > Games) is not looked for: the HUD header never names it.
            if (IsGameHidden(game.Key))
                continue;
            foreach (string name in game.Value)
            {
                if (GameProcessLookup.IsRunning(name))
                    return game.Key;
            }
        }
        return string.Empty;
    }

    // "plane <unit type>" and "exit" from the DCS hook (UDP 127.0.0.1:1992).
    private async Task ListenForDcsAsync(CancellationToken token)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 1992));
        }
        catch (SocketException ex)
        {
            Dispatcher.UIThread.Post(() => DcsHookStatus = T("Cannot listen for DCS on port 1992: {0}", ex.Message));
            return;
        }
        using (udp)
        {
            while (!token.IsCancellationRequested)
            {
                string message;
                try
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(token);
                    message = Encoding.UTF8.GetString(result.Buffer).Trim();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }
                Dispatcher.UIThread.Post(() => OnDcsMessage(message));
            }
        }
    }

    internal void OnDcsMessage(string message)
    {
        if (message.StartsWith("plane ", StringComparison.Ordinal))
        {
            string unit = message[6..].Trim();
            if (unit == overlayUnit)
                return;
            overlayUnit = unit;
            overlayGame = "DCS";
            _ = ReloadOverlayMapAsync();
        }
        else if (message == "exit")
        {
            overlayUnit = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ReloadOverlayMapAsync()
    {
        string engine = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine) || string.IsNullOrWhiteSpace(ProfilePath))
            return;
        List<string> args = ["--profile", ProfilePath, "--status", "false", "overlay", "map"];
        if (overlayGame.Length > 0)
            args.AddRange(["--game", overlayGame]);
        if (overlayGame == "DCS" || overlayGame.Length == 0)
        {
            if (OverlayPlaneChoice != OverlayAutomaticPlane && !string.IsNullOrWhiteSpace(OverlayPlaneChoice))
                args.AddRange(["--plane", OverlayPlaneChoice]);
            else if (overlayUnit.Length > 0)
                args.AddRange(["--unit", overlayUnit]);
        }
        EngineCommandResult result;
        try
        {
            // Quiet: started by the game, not by the user, and must not queue behind long commands.
            result = await cliClient.RunAsync(engine, args);
        }
        catch (Exception ex)
        {
            OverlayStatus = T("Could not read the binds: {0}", ex.Message);
            return;
        }
        if (result.ExitCode != 0)
        {
            OverlayStatus = T("Could not read the binds: {0}", BuildFailureStatus(result));
            return;
        }
        ApplyOverlayMap(result.StandardOutput);
    }

    private void ApplyOverlayMap(string output)
    {
        List<OverlayResolver.Entry> entries = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(output));
            JsonElement root = doc.RootElement;
            overlayPlane = ReadJsonString(root, "Plane");
            if (root.TryGetProperty("ProcessNames", out JsonElement processes) && processes.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, List<string>> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty game in processes.EnumerateObject())
                    names[game.Name] = game.Value.ValueKind == JsonValueKind.Array ? game.Value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).Where(v => v.Length > 0).ToList() : [];
                overlayProcessNames = names;
            }
            List<string> planes = ReadStringArray(root, "DcsPlanes");
            foreach (string plane in planes.Where(p => !OverlayPlaneOptions.Contains(p)))
                OverlayPlaneOptions.Add(plane);
            if (root.TryGetProperty("Entries", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    List<OverlayResolver.Modifier> modifiers = [];
                    if (item.TryGetProperty("Modifiers", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement mod in mods.EnumerateArray())
                        {
                            modifiers.Add(new OverlayResolver.Modifier(ReadJsonString(mod, "Name"), ReadJsonString(mod, "Device"), ReadJsonString(mod, "Input"),
                                mod.TryGetProperty("Switch", out JsonElement sw) && sw.ValueKind == JsonValueKind.True));
                        }
                    }
                    entries.Add(new OverlayResolver.Entry(ReadJsonString(item, "Relation"), ReadJsonString(item, "Title"), ReadJsonString(item, "Device"), ReadJsonString(item, "Input"), modifiers));
                }
            }
        }
        catch (JsonException)
        {
            OverlayStatus = T("Could not read the binds (unexpected engine output).");
            return;
        }
        overlayResolver = new OverlayResolver(entries);
        string where = overlayGame.Length == 0 ? T("no game running") : overlayGame + (overlayPlane.Length > 0 && overlayGame == "DCS" ? " · " + overlayPlane : string.Empty);
        OverlayHeader = T("JoyPro · {0}", where);
        _ = UpdateHudHeaderAsync();
        OverlayStatus = T("The HUD is on ({0}, {1} bound buttons).{2}", where, entries.Count, (overlayGame.Length == 0 && OverlayOnlyInGame ? T(" It shows presses once a game runs.") : string.Empty));
    }

    // The HUD header: the DCS aircraft (its module icon) or the game (its cover).
    private async Task UpdateHudHeaderAsync()
    {
        if (overlayGame.Length == 0)
        {
            HudContext.HeaderText = T("No game running");
            HudContext.HeaderPicture = null;
            return;
        }
        MediaIndex index = await GetMediaIndexAsync();
        string gameName = index.GameNames.TryGetValue(overlayGame, out string? name) && name.Length > 0 ? name : overlayGame;
        if (overlayGame == "DCS" && overlayPlane.Length > 0)
        {
            HudContext.HeaderText = T("{0} · DCS", (index.AircraftNames.TryGetValue(overlayPlane, out string? planeName) ? planeName : overlayPlane));
            HudContext.HeaderPicture = await LoadAircraftPictureAsync(overlayPlane) ?? await LoadGameCoverAsync("DCS", 80);
            return;
        }
        HudContext.HeaderText = gameName;
        HudContext.HeaderPicture = await LoadGameCoverAsync(overlayGame, 80);
    }

    [RelayCommand]
    private async Task DcsHookStatusAsync() => await RunDcsHookAsync("status");

    [RelayCommand]
    private async Task InstallDcsHookAsync() => await RunDcsHookAsync("install");

    [RelayCommand]
    private async Task RemoveDcsHookAsync() => await RunDcsHookAsync("remove");

    private async Task RunDcsHookAsync(string action)
    {
        List<string> args = ["overlay", "dcs-hook", action];
        if (!string.IsNullOrWhiteSpace(ExportDcsPath))
            args.AddRange(["--path", ExportDcsPath.Trim()]);
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: false, mutatesProfile: false,
            busyLabel: action == "install" ? "Installing the DCS overlay hook..." : action == "remove" ? "Removing the DCS overlay hook..." : "Checking the DCS overlay hook...", forceText: false);
        if (result == null)
            return;
        if (result.ExitCode != 0)
        {
            DcsHookStatus = BuildFailureStatus(result);
            return;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ExtractJsonPayload(result.StandardOutput));
            bool installed = doc.RootElement.TryGetProperty("Installed", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            DcsHookStatus = installed
                ? T("DCS hook installed: DCS tells JoyPro the aircraft you fly (from the next mission start). Export.lua was backed up first.")
                : T("DCS hook not installed: pick the aircraft above, or install the hook so DCS tells JoyPro.");
        }
        catch (JsonException)
        {
            DcsHookStatus = result.StandardOutput.Trim();
        }
    }
}
