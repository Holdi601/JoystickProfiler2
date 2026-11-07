using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.Engine.Axis;

namespace JoyPro.Gui.Models;

// The aircraft of one game that share a curve today ("AH-64D CPG, AH-64D PLT: same curve", "Ka-50 III: uses DCS"); a click
// ticks them. Scopes: what is ticked ("game:DCS" = the aircraft that use the game's curve, else their plane scopes).
public sealed class AxisTuningGroupItem : ObservableObject
{
    public AxisTuningGroupItem(string text, string tip, System.Collections.Generic.IReadOnlyList<string> scopes)
    {
        Text = text;
        Tip = tip;
        Scopes = scopes;
    }

    public string Text { get; }

    public string Tip { get; }

    public System.Collections.Generic.IReadOnlyList<string> Scopes { get; }

    // These are the ticked rows now.
    public bool IsCurrent
    {
        get => isCurrent;
        set => SetProperty(ref isCurrent, value);
    }

    private bool isCurrent;
}

// One row of the axis curve manager's "Applies to" list: where a tuning is stored ("base", "game:DCS",
// "plane:DCS:F-16C_50", "family:il2"), which game's response model edits and previews it, whether it is ticked and what
// it uses today ("own curve", "same as ...", "uses DCS").
public sealed class AxisTuningScopeItem : ObservableObject
{
    public AxisTuningScopeItem(string scope, string label, string game, string plane, bool isShown = true)
    {
        Scope = scope;
        Label = label;
        Game = game;
        Plane = plane;
        this.isShown = isShown;
    }

    // Ticked: the curve shown is saved for this row (several aircraft of one game can be ticked together).
    public bool IsChecked
    {
        get => isChecked;
        set => SetProperty(ref isChecked, value);
    }

    private bool isChecked;

    // A family row shows only where it applies (two of its games in the relation, or a curve stored for it).
    public bool IsShown
    {
        get => isShown;
        set => SetProperty(ref isShown, value);
    }

    private bool isShown;

    // "own curve", "same as AH-64D CPG", "uses DCS".
    public string UsesText
    {
        get => usesText;
        set => SetProperty(ref usesText, value);
    }

    private string usesText = string.Empty;

    public string UsesTip
    {
        get => usesTip;
        set => SetProperty(ref usesTip, value);
    }

    private string usesTip = string.Empty;

    // The row has a curve of its own (the base always has).
    public bool IsOwn
    {
        get => isOwn;
        set => SetProperty(ref isOwn, value);
    }

    private bool isOwn;

    public bool IsAircraft => Scope.StartsWith("plane:", StringComparison.Ordinal);

    // Aircraft rows sit under their game.
    public double IndentWidth => IsAircraft ? 26 : 0;

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

// What a number box tells the value it edits (Views/NumberBox.cs): a typed number outside the range was put back to its
// nearest end, which the page says next to the box (never silently).
public interface INumberBoxOwner
{
    void OnPutBackInRange(double typed, double kept);
}

// The numbers of the axis response as the box shows them (AxisParameter.DisplayScale): DCS's dialog shows value * 100 and
// reads text / 100; the value itself, stored, exported and sent to the engine, stays in the file's unit and is never
// parsed back from what is shown, so a loaded value keeps all its decimals until the user edits it.
internal static class AxisShownNumber
{
    // Four decimals of a shown 0..100 are the six the profile's tuning strings keep.
    public static string Show(double stored, double scale, int decimals = 6) =>
        AxisParameterEditor.Format(scale == 1 ? (decimals >= 6 ? stored : Math.Round(stored, decimals)) : Math.Round(stored * scale, 4));

    // Rounded so that a typed 12.34 gives the 0.1234 the file should hold, not 0.12340000000000001.
    public static double Stored(double shown, double scale) => scale == 1 ? shown : Math.Round(shown / scale, 10);

    public static string RangeText(double min, double max, double scale) => Show(min, scale) + " .. " + Show(max, scale);

    // 0.08 typed into a box of 0 .. 100: the old habit (2.6.3 and before took 0..1); valid, but it may not be what was meant.
    public static string OldUnitNote(string label, double typed, double scale)
    {
        if (scale != 100 || typed == 0 || Math.Abs(typed) >= 1)
            return string.Empty;
        return T("{0}: {1} here means {1} of 100. Did you mean {2}?", label, Show(typed, 1), Show(typed, scale));
    }

    public static string PutBackNote(string label, double typed, double kept, string range) =>
        T("{0}: {1} is outside {2}, so it is now {3}.", label, Show(typed, 1), range, Show(kept, 1));
}

// One parameter editor of the curve manager, generated from the scope's model (AxisParameter).
public sealed partial class AxisParameterEditor : ObservableObject, INumberBoxOwner
{
    private bool syncing;

    public AxisParameterEditor(AxisParameter parameter)
    {
        Parameter = parameter;
        value = parameter.Default;
        text = AxisShownNumber.Show(parameter.Default, Scale);
        if (parameter.Kind == AxisParameterKind.UserCurve)
        {
            for (int i = 0; i < AxisModels.UserCurvePoints; i++)
            {
                AxisCurvePointEditor point = new(i / (double)(AxisModels.UserCurvePoints - 1), Scale);
                point.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AxisCurvePointEditor.InputNote))
                        InputNote = Points.FirstOrDefault(p => p.InputNote.Length > 0)?.InputNote ?? string.Empty;
                    if (e.PropertyName is not (nameof(AxisCurvePointEditor.Value) or nameof(AxisCurvePointEditor.ValidationMessage)))
                        return;
                    // A point that is no number of the range makes the curve invalid (the message says which point).
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

    // The slider moves the stored value.
    public double Minimum => Parameter.Min;

    public double Maximum => Parameter.Max;

    public double Step => Parameter.Step > 0 ? Parameter.Step : 0.01;

    // What the number box shows and takes, in the unit of the game's own dialog (DCS: 0 .. 100 for a stored 0..1).
    private double Scale => Parameter.DisplayScale > 0 ? Parameter.DisplayScale : 1;

    public double BoxMinimum => Parameter.Min * Scale;

    public double BoxMaximum => Parameter.Max * Scale;

    public double BoxStep => Math.Round(Step * Scale, 10);

    public string RangeText => AxisShownNumber.RangeText(Parameter.Min, Parameter.Max, Scale);

    // Above the 11 boxes of a user curve.
    public string CurveHint => T("The output ({0}) at each point of the stick travel: type it, use Up / Down or the mouse wheel, or drag the white points on the curve.", RangeText);

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

    // Under the box, no error (nothing is refused): a number put back into the range, or 0.08 typed where 8 was meant.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputNote))]
    private string inputNote = string.Empty;

    public bool HasInputNote => InputNote.Length > 0;

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
        Text = AxisShownNumber.Show(number, Scale);
        ValidationMessage = string.Empty;
        InputNote = string.Empty;
        syncing = false;
    }

    public void OnPutBackInRange(double typed, double kept) => InputNote = AxisShownNumber.PutBackNote(Parameter.Label, typed, kept, RangeText);

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
        Text = AxisShownNumber.Show(value, Scale);
        ValidationMessage = string.Empty;
        InputNote = string.Empty;
        syncing = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        InputNote = string.Empty;
        if (!TryParseNumber(value, out double number))
        {
            ValidationMessage = T("{0}: enter a number ({1}).", Parameter.Label, RangeText);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (number < BoxMinimum || number > BoxMaximum)
        {
            ValidationMessage = T("{0} must be {1}.", Parameter.Label, RangeText);
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        ValidationMessage = string.Empty;
        InputNote = AxisShownNumber.OldUnitNote(Parameter.Label, number, Scale);
        syncing = true;
        Value = AxisShownNumber.Stored(number, Scale);
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

// One point of a user curve (the output at Position, stored 0..1; shown in the model's unit, DCS 0 .. 100): a number box
// under the curve (2.6.2: the 11 vertical sliders were hard to set exactly), and a white point on the preview to drag.
// Both edit Value; Text follows it.
public sealed partial class AxisCurvePointEditor : ObservableObject, INumberBoxOwner
{
    private const double StoredStep = 0.01;

    private bool syncing;
    private readonly double scale;

    public AxisCurvePointEditor(double position, double scale = 1)
    {
        Position = position;
        this.scale = scale;
        value = position;
        text = AxisShownNumber.Show(position, scale, 4);
    }

    public double BoxMinimum => 0.0;

    public double BoxMaximum => scale;

    public double BoxStep => Math.Round(StoredStep * scale, 10);

    private string RangeText => AxisShownNumber.RangeText(0, 1, scale);

    public double Position { get; }

    // "50%": how far the stick is at this point.
    public string PositionLabel => (Position * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public string Tooltip => T("Output at {0} of the stick travel: {1} (arrow keys or the mouse wheel: {2}, Shift: {3})", PositionLabel, RangeText,
        AxisShownNumber.Show(BoxStep, 1), AxisShownNumber.Show(BoxStep * 10, 1));

    [ObservableProperty]
    private double value;

    [ObservableProperty]
    private string text;

    [ObservableProperty]
    private string validationMessage = string.Empty;

    // The note under the 11 boxes (AxisParameterEditor.InputNote).
    [ObservableProperty]
    private string inputNote = string.Empty;

    public bool IsValid => ValidationMessage.Length == 0;

    private string NoteLabel => T("User curve at {0}", PositionLabel);

    public void OnPutBackInRange(double typed, double kept) => InputNote = AxisShownNumber.PutBackNote(NoteLabel, typed, kept, RangeText);

    partial void OnValueChanged(double value)
    {
        if (syncing)
            return;
        syncing = true;
        Text = AxisShownNumber.Show(value, scale, 4);
        ValidationMessage = string.Empty;
        InputNote = string.Empty;
        syncing = false;
    }

    // A typed value: a number of the range sets the point; anything else is shown as a message (and not sent).
    partial void OnTextChanged(string value)
    {
        if (syncing)
            return;
        InputNote = string.Empty;
        if (!AxisParameterEditor.TryParseNumber(value, out double number))
        {
            ValidationMessage = T("User curve at {0}: enter a number ({1}).", PositionLabel, RangeText);
            return;
        }
        if (number < BoxMinimum || number > BoxMaximum)
        {
            ValidationMessage = T("User curve at {0} must be {1}.", PositionLabel, RangeText);
            return;
        }
        syncing = true;
        ValidationMessage = string.Empty;
        InputNote = AxisShownNumber.OldUnitNote(NoteLabel, number, scale);
        Value = AxisShownNumber.Stored(number, scale);
        syncing = false;
    }

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(IsValid));

    // Sets the point from a loaded curve (also over a typed value that was no number).
    public void Load(double number)
    {
        syncing = true;
        Value = number;
        Text = AxisShownNumber.Show(number, scale, 4);
        ValidationMessage = string.Empty;
        InputNote = string.Empty;
        syncing = false;
    }
}
