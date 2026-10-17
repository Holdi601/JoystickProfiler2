using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// Where a telemetry force feedback setting lives: global (all null), a game, an aircraft of a game, or an effect of a
// game or aircraft. The engine call is `ffb telemetry config set [--game G] [--plane P] [--effect E] --param k=v`.
public readonly record struct FfbTelemetryScope(string? Game = null, string? Plane = null, string? Effect = null, string? Device = null)
{
    public static FfbTelemetryScope Global => default;

    // One force feedback device's own settings (its directions): `config set --device {guid}`.
    public static FfbTelemetryScope ForDevice(string device) => new(Device: device);

    public IEnumerable<string> ToArguments()
    {
        if (!string.IsNullOrEmpty(Device))
        {
            yield return "--device";
            yield return Device;
        }
        if (!string.IsNullOrEmpty(Game))
        {
            yield return "--game";
            yield return Game;
        }
        if (!string.IsNullOrEmpty(Plane))
        {
            yield return "--plane";
            yield return Plane;
        }
        if (!string.IsNullOrEmpty(Effect))
        {
            yield return "--effect";
            yield return Effect;
        }
    }
}

// Raised by the items when the user changes something (not while the page loads them).
public delegate void FfbTelemetrySettingChanged(FfbTelemetryScope scope, string key, string value);

// A graph a game or aircraft can use: "" = the built-in flight graph, else a *.jpffb.json file in the Graphs folder.
public sealed record FfbGraphOption(string Value, string Label)
{
    public const string BuiltInLabel = "Built-in flight graph";

    public static FfbGraphOption BuiltIn { get; } = new(string.Empty, BuiltInLabel);

    public bool IsBuiltIn => Value.Length == 0;

    public override string ToString() => Label;
}

// A force feedback device of `JoyPro.Ffb devices --json`.
public sealed record FfbTelemetryDeviceOption(string InstanceGuid, string Name, string Detail, bool HasForceAxes, string Vid = "", string Pid = "")
{
    public string Label => Detail.Length == 0 ? Name : Name + "  (" + Detail + ")";

    // VPforce Rhino / TMS (VID FFFF, PID 2055, or the name): their Configurator's own spring overrides JoyPro's spring.
    public bool IsVpforce => IsVpforceDevice(Vid, Pid, Name);

    public static bool IsVpforceDevice(string vid, string pid, string name) =>
        (string.Equals(vid, "FFFF", StringComparison.OrdinalIgnoreCase) && string.Equals(pid, "2055", StringComparison.OrdinalIgnoreCase)) ||
        (name ?? string.Empty).Contains("VPforce", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Label;
}

// Base of the editable items: knows its scope, reports changes unless it is being loaded.
public abstract class FfbTelemetrySettingItem : ObservableObject
{
    internal bool Loading { get; set; }

    internal FfbTelemetrySettingChanged? Changed { get; set; }

    protected abstract FfbTelemetryScope Scope { get; }

    protected void Report(string key, string value)
    {
        if (!Loading)
            Changed?.Invoke(Scope, key, value);
    }

    internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Format(bool value) => value ? "true" : "false";
}

// One effect of the built-in graph for a game: on/off and a strength in % (0..200).
public sealed partial class FfbTelemetryEffectItem : FfbTelemetrySettingItem
{
    private readonly string game;

    public FfbTelemetryEffectItem(string game, string name, string title)
    {
        this.game = game;
        Name = name;
        Title = title;
    }

    public string Name { get; }

    public string Title { get; }

    [ObservableProperty]
    private bool enabled = true;

    [ObservableProperty]
    private double strength = 100;

    public string StrengthText => Strength.ToString("0", CultureInfo.InvariantCulture) + " %";

    protected override FfbTelemetryScope Scope => new(game, null, Name);

    partial void OnEnabledChanged(bool value) => Report("enabled", Format(value));

    partial void OnStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(StrengthText));
        Report("strength", Format(Math.Round(value) / 100.0));
    }
}

// An aircraft with its own graph and/or strength (overrides the game's).
public sealed partial class FfbTelemetryAircraftItem : FfbTelemetrySettingItem
{
    private readonly string game;

    public FfbTelemetryAircraftItem(string game, string name, ObservableCollection<FfbGraphOption> graphOptions)
    {
        this.game = game;
        Name = name;
        GraphOptions = graphOptions;
    }

    public string Name { get; }

    public ObservableCollection<FfbGraphOption> GraphOptions { get; }

    [ObservableProperty]
    private FfbGraphOption? graph;

    [ObservableProperty]
    private double strength = 100;

    public string StrengthText => Strength.ToString("0", CultureInfo.InvariantCulture) + " %";

    protected override FfbTelemetryScope Scope => new(game, Name, null);

    // A combo box clears its selection while its list is rebuilt: null is never sent.
    partial void OnGraphChanged(FfbGraphOption? value)
    {
        if (value != null)
            Report("graph", value.Value);
    }

    partial void OnStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(StrengthText));
        Report("strength", Format(Math.Round(value) / 100.0));
    }
}

// A supported game: its switch, strength, graph, effects, aircraft overrides and the setup state (`ffb telemetry status`).
public sealed partial class FfbTelemetryGameItem : FfbTelemetrySettingItem
{
    public FfbTelemetryGameItem(string id, string title, IReadOnlyList<string> processNames, ObservableCollection<FfbGraphOption> graphOptions)
    {
        Id = id;
        Title = title;
        ProcessNames = processNames;
        GraphOptions = graphOptions;
    }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<string> ProcessNames { get; }

    public string ProcessText => string.Join(" / ", ProcessNames.Select(n => n + ".exe"));

    public ObservableCollection<FfbGraphOption> GraphOptions { get; }

    public ObservableCollection<FfbTelemetryEffectItem> Effects { get; } = [];

    public ObservableCollection<FfbTelemetryAircraftItem> Aircraft { get; } = [];

    [ObservableProperty]
    private bool enabled = true;

    [ObservableProperty]
    private double strength = 100;

    [ObservableProperty]
    private FfbGraphOption? graph;

    [ObservableProperty]
    private bool running;

    [ObservableProperty]
    private bool setupNeeded = true;

    [ObservableProperty]
    private bool setupInstalled;

    [ObservableProperty]
    private string setupDetail = string.Empty;

    [ObservableProperty]
    private string endpoint = string.Empty;

    public string StrengthText => Strength.ToString("0", CultureInfo.InvariantCulture) + " %";

    public string Label => Title + (Running ? "  (running)" : string.Empty);

    public string SetupStateText => !SetupNeeded
        ? T("Nothing to set up: {0} always offers its telemetry.", Title)
        : (SetupInstalled ? T("Telemetry export installed.") : T("Telemetry export not installed.")) +
          (SetupDetail.Length > 0 ? " " + SetupDetail : string.Empty);

    protected override FfbTelemetryScope Scope => new(Id, null, null);

    partial void OnEnabledChanged(bool value) => Report("enabled", Format(value));

    partial void OnStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(StrengthText));
        Report("strength", Format(Math.Round(value) / 100.0));
    }

    partial void OnGraphChanged(FfbGraphOption? value)
    {
        if (value != null)
            Report("graph", value.Value);
    }

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(Label));

    partial void OnSetupNeededChanged(bool value) => OnPropertyChanged(nameof(SetupStateText));

    partial void OnSetupInstalledChanged(bool value) => OnPropertyChanged(nameof(SetupStateText));

    partial void OnSetupDetailChanged(string value) => OnPropertyChanged(nameof(SetupStateText));

    public override string ToString() => Label;
}

// One field of the telemetry inspector: the latest value, with title and unit when the key is described.
public sealed partial class FfbTelemetryFieldItem : ObservableObject
{
    public FfbTelemetryFieldItem(string key, string title, string unit)
    {
        Key = key;
        Title = title;
        Unit = unit;
    }

    public string Key { get; }

    public string Title { get; }

    public string Unit { get; }

    [ObservableProperty]
    private double value;

    public string ValueText => Value.ToString("0.#####", CultureInfo.InvariantCulture);

    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(ValueText));
}
