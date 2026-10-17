using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One force feedback setting of a game as `engine ffb models` describes it (engine/FfbSettings.cs).
public sealed record FfbParameterInfo(string Key, string Label, bool Toggle, double Min, double Max, double Default, bool GameWide, string Help);

// A game with force feedback settings (`engine ffb models`).
public sealed class FfbGameItem
{
    public FfbGameItem(string game, string title, string description, IReadOnlyList<FfbParameterInfo> parameters)
    {
        Game = game;
        Title = title;
        Description = description;
        Parameters = parameters;
    }

    public string Game { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<FfbParameterInfo> Parameters { get; }

    // Settings stored per device (DCS) rather than for the whole game (device "*").
    public bool PerDevice => Parameters.Any(p => !p.GameWide);

    // Only DCS keeps force feedback per aircraft (diff.lua ffDiffs).
    public bool HasPlanes => string.Equals(Game, "DCS", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Title;
}

// An entry of the device or aircraft picker: Value is what the engine gets ("" = all aircraft).
public sealed record FfbPickerOption(string Value, string Label)
{
    public override string ToString() => Label;
}

// One setting's editor in the force feedback panel: a toggle or a slider with a number box, plus where the value
// comes from (stored for this scope, inherited from all aircraft, or not set: the game file keeps its value).
public sealed partial class FfbParameterEditor : ObservableObject
{
    private bool syncing;
    private double loadedValue;
    private double? inheritedValue;
    private string inheritedFrom = string.Empty;

    public FfbParameterEditor(FfbParameterInfo parameter)
    {
        Parameter = parameter;
        value = parameter.Default;
        text = Format(parameter.Default);
    }

    public FfbParameterInfo Parameter { get; }

    public string Key => Parameter.Key;

    public string Label => Parameter.Label;

    public bool IsToggle => Parameter.Toggle;

    public bool IsNumber => !Parameter.Toggle;

    public double Minimum => Parameter.Min;

    public double Maximum => Parameter.Max;

    // Percent settings move in whole steps, 0..1 factors in hundredths.
    public double Step => Parameter.Max - Parameter.Min > 2 ? 1 : 0.01;

    public string RangeText => Format(Parameter.Min) + " .. " + Format(Parameter.Max);

    public string DefaultText => Parameter.Toggle ? (Parameter.Default != 0 ? "on" : "off") : Format(Parameter.Default);

    public string ToolTipText =>
        T("{0}{1}, game default {2}{3}{4}engine key: {5}", (string.IsNullOrWhiteSpace(Parameter.Help) ? string.Empty : Parameter.Help.Trim() + Environment.NewLine), (Parameter.Toggle ? T("On / off") : T("Range {0}", RangeText)), DefaultText, (Parameter.GameWide ? T(". Game-wide (all devices).") : T(". Per device.")), Environment.NewLine, Parameter.Key);

    public event EventHandler? Changed;

    [ObservableProperty]
    private double value;

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    // The value stored for the selected scope itself (null: not stored there).
    [ObservableProperty]
    private double? storedValue;

    [ObservableProperty]
    private string sourceText = string.Empty;

    [ObservableProperty]
    private bool hasEdit;

    public bool IsStored => StoredValue.HasValue;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool IsValid => ValidationMessage.Length == 0;

    // The editor's value as the engine takes it (toggles 1 / 0).
    public double CurrentValue => Parameter.Toggle ? (IsOn ? 1 : 0) : Value;

    // stored: this scope's value; inherited: the value of the broader scope (all aircraft) when this one has none.
    public void Load(double? stored, double? inherited, string inheritedFrom)
    {
        StoredValue = stored;
        inheritedValue = inherited;
        this.inheritedFrom = inheritedFrom;
        double shown = stored ?? inherited ?? Parameter.Default;
        loadedValue = Parameter.Toggle ? (shown != 0 ? 1 : 0) : shown;
        SourceText = stored.HasValue
            ? T("Stored")
            : inherited.HasValue
                ? T("From {0}", inheritedFrom)
                : T("Not stored (game default {0})", DefaultText);
        syncing = true;
        Value = loadedValue;
        Text = Format(loadedValue);
        IsOn = loadedValue != 0;
        ValidationMessage = string.Empty;
        syncing = false;
        HasEdit = false;
    }

    public void Revert() => Load(StoredValue, inheritedValue, inheritedFrom);

    public string FormatCurrent() => Parameter.Toggle ? (IsOn ? "true" : "false") : Format(Value);

    partial void OnStoredValueChanged(double? value) => OnPropertyChanged(nameof(IsStored));

    partial void OnValueChanged(double value)
    {
        if (syncing)
            return;
        syncing = true;
        Text = Format(value);
        ValidationMessage = string.Empty;
        syncing = false;
        RaiseChanged();
    }

    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        if (!double.TryParse((value ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
            double.IsNaN(number) || double.IsInfinity(number))
        {
            ValidationMessage = T("{0}: enter a number ({1}).", Parameter.Label, RangeText);
            RaiseChanged();
            return;
        }
        if (number < Parameter.Min || number > Parameter.Max)
        {
            ValidationMessage = Parameter.Label + " must be " + RangeText + ".";
            RaiseChanged();
            return;
        }
        ValidationMessage = string.Empty;
        syncing = true;
        Value = number;
        syncing = false;
        RaiseChanged();
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!syncing)
            RaiseChanged();
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(IsValid));
    }

    private void RaiseChanged()
    {
        HasEdit = IsValid && Math.Abs(CurrentValue - loadedValue) > 1e-9;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string Format(double number) => number.ToString("0.######", CultureInfo.InvariantCulture);
}
