using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Engine.Axis;

namespace JoyPro.Gui.Models;

// One entry of the axis curve manager's scope list: where a tuning is stored ("base", "game:DCS", "plane:DCS:F-16C_50",
// "family:il2") and which game's response model edits and previews it.
public sealed class AxisTuningScopeItem : ObservableObject
{
    public AxisTuningScopeItem(string scope, string label, string game, string plane)
    {
        Scope = scope;
        Label = label;
        Game = game;
        Plane = plane;
    }

    public string Scope { get; }

    // Set again when the language changes (the scope list keeps its items and the selection).
    public string Label
    {
        get => label;
        set => SetProperty(ref label, value);
    }

    private string label = string.Empty;

    // The game whose model this scope uses; "DCS" for the base (the base parameters are DCS's).
    public string Game { get; }

    public string Plane { get; }

    public bool IsBase => Scope == "base";

    public bool IsFamily => Scope.StartsWith("family:", StringComparison.Ordinal);

    public AxisModel Model => AxisModels.ForGame(Game);

    public override string ToString() => Label;
}

// One parameter editor of the curve manager, generated from the scope's model (AxisParameter).
public sealed partial class AxisParameterEditor : ObservableObject
{
    private bool syncing;

    public AxisParameterEditor(AxisParameter parameter)
    {
        Parameter = parameter;
        value = parameter.Default;
        text = Format(parameter.Default);
        if (parameter.Kind == AxisParameterKind.UserCurve)
        {
            for (int i = 0; i < AxisModels.UserCurvePoints; i++)
            {
                AxisCurvePointEditor point = new(i / (double)(AxisModels.UserCurvePoints - 1));
                point.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is not (nameof(AxisCurvePointEditor.Value) or nameof(AxisCurvePointEditor.ValidationMessage)))
                        return;
                    // A point that is no number 0..1 makes the curve invalid (the message says which point).
                    ValidationMessage = Points.FirstOrDefault(p => !p.IsValid)?.ValidationMessage ?? string.Empty;
                    Changed?.Invoke(this, EventArgs.Empty);
                };
                Points.Add(point);
            }
        }
    }

    public AxisParameter Parameter { get; }

    public string Key => Parameter.Key;

    public string Label => Parameter.Kind == AxisParameterKind.UserCurve ? T("User curve (11 points)") : Parameter.Label;

    public bool IsNumber => Parameter.Kind == AxisParameterKind.Number;

    public bool IsToggle => Parameter.Kind == AxisParameterKind.Toggle;

    public bool IsUserCurve => Parameter.Kind == AxisParameterKind.UserCurve;

    public double Minimum => Parameter.Min;

    public double Maximum => Parameter.Max;

    public double Step => Parameter.Step > 0 ? Parameter.Step : 0.01;

    public string RangeText => Format(Parameter.Min) + " .. " + Format(Parameter.Max);

    // User curve: outputs at 0, 0.1, .. 1.
    public ObservableCollection<AxisCurvePointEditor> Points { get; } = [];

    // Any edit (value, toggle, curve point).
    public event EventHandler? Changed;

    [ObservableProperty]
    private double value;

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private bool isOn;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    // False while another setting replaces this one (curvature while the user curve is on).
    [ObservableProperty]
    private bool isActive = true;

    // The user curve was switched on (the view model starts it from the current curvature).
    public event EventHandler? SwitchedOn;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool IsValid => ValidationMessage.Length == 0;

    // Sets the value without treating it as an edit of the text box.
    public void Load(double number)
    {
        syncing = true;
        Value = number;
        Text = Format(number);
        ValidationMessage = string.Empty;
        syncing = false;
    }

    public void LoadCurve(System.Collections.Generic.IReadOnlyList<double> points)
    {
        for (int i = 0; i < Points.Count && i < points.Count; i++)
            Points[i].Load(points[i]);
    }

    partial void OnValueChanged(double value)
    {
        if (syncing)
            return;
        syncing = true;
        Text = Format(value);
        ValidationMessage = string.Empty;
        syncing = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        if (!TryParseNumber(value, out double number))
        {
            ValidationMessage = T("{0}: enter a number ({1}).", Parameter.Label, RangeText);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (number < Parameter.Min || number > Parameter.Max)
        {
            ValidationMessage = T("{0} must be {1}.", Parameter.Label, RangeText);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        ValidationMessage = string.Empty;
        syncing = true;
        Value = number;
        syncing = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (value && IsUserCurve)
            SwitchedOn?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(IsValid));
    }

    internal static string Format(double number) => number.ToString("0.######", CultureInfo.InvariantCulture);

    // A typed number: "0.35", "0,35" (a decimal comma), no NaN or infinity.
    internal static bool TryParseNumber(string? text, out double number) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        !double.IsNaN(number) && !double.IsInfinity(number);
}

// One free control point of a game curve (binds tuning set --curve-points x:y,...): stick travel X and output Y, 0..1.
public sealed partial class AxisControlPointEditor : ObservableObject
{
    private bool syncing;

    public AxisControlPointEditor(double x, double y)
    {
        Set(x, y);
    }

    public double X { get; private set; }

    public double Y { get; private set; }

    [ObservableProperty]
    private string xText = string.Empty;

    [ObservableProperty]
    private string yText = string.Empty;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    public bool IsValid => ValidationMessage.Length == 0;

    // Any edit of X or Y.
    public event EventHandler? Changed;

    // Sets both values (loading, dragging on the curve) without re-parsing the text.
    public void Set(double x, double y)
    {
        syncing = true;
        X = x;
        Y = y;
        XText = AxisParameterEditor.Format(x);
        YText = AxisParameterEditor.Format(y);
        ValidationMessage = string.Empty;
        syncing = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Spec => AxisParameterEditor.Format(X) + ":" + AxisParameterEditor.Format(Y);

    partial void OnXTextChanged(string value) => Parse();

    partial void OnYTextChanged(string value) => Parse();

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(IsValid));

    // The engine's rule: x and y in 0..1.
    private void Parse()
    {
        if (syncing)
            return;
        if (TryUnit(XText, out double x) && TryUnit(YText, out double y))
        {
            X = x;
            Y = y;
            ValidationMessage = string.Empty;
        }
        else
        {
            ValidationMessage = T("Curve points: x and y must be numbers 0..1.");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryUnit(string text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        value >= 0 && value <= 1;
}

// Another bound axis the axis response can be copied to ("Thrust Right · T.16000M · Z").
public sealed class AxisTuningCopyTarget
{
    public AxisTuningCopyTarget(string relation, string label)
    {
        Relation = relation;
        Label = label;
    }

    public string Relation { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

// A handle dragged on the curve preview (AxisCurveView.Handles): its index and the new position in curve coordinates
// (stick -1..1, output -1..1).
public sealed record AxisCurveHandleMove(int Index, double X, double Y);

// One point of a DCS user curve (the output at Position, 0..1): a number box under the curve (2.6.2: the 11 vertical
// sliders were hard to set exactly), and a white point on the preview to drag. Both edit Value; Text follows it.
public sealed partial class AxisCurvePointEditor : ObservableObject
{
    private bool syncing;

    public AxisCurvePointEditor(double position)
    {
        Position = position;
        value = position;
        text = AxisParameterEditor.Format(position);
    }

    public const double Minimum = 0.0;

    public const double Maximum = 1.0;

    public const double Step = 0.01;

    public double Position { get; }

    // "50%": how far the stick is at this point.
    public string PositionLabel => (Position * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public string Tooltip => T("Output at {0} of the stick travel: 0 .. 1 (arrow keys or the mouse wheel: 0.01, Shift: 0.1)", PositionLabel);

    [ObservableProperty]
    private double value;

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    public bool IsValid => ValidationMessage.Length == 0;

    partial void OnValueChanged(double value)
    {
        if (syncing)
            return;
        syncing = true;
        Text = AxisParameterEditor.Format(Math.Round(value, 4));
        ValidationMessage = string.Empty;
        syncing = false;
    }

    // A typed value: a number 0..1 sets the point; anything else is shown as a message (and not sent).
    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        if (!AxisParameterEditor.TryParseNumber(value, out double number))
        {
            ValidationMessage = T("User curve at {0}: enter a number (0 .. 1).", PositionLabel);
            return;
        }
        if (number < Minimum || number > Maximum)
        {
            ValidationMessage = T("User curve at {0} must be 0 .. 1.", PositionLabel);
            return;
        }
        syncing = true;
        ValidationMessage = string.Empty;
        Value = number;
        syncing = false;
    }

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(IsValid));

    // Sets the point from a loaded curve (also over a typed value that was no number).
    public void Load(double number)
    {
        syncing = true;
        Value = number;
        Text = AxisParameterEditor.Format(Math.Round(number, 4));
        ValidationMessage = string.Empty;
        syncing = false;
    }
}
