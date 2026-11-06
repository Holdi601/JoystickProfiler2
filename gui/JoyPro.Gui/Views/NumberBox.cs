using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views;

// A text box for an exact number (the axis response's dead zone, saturations, curvature and user-curve points; 2.6.2
// tester report: the sliders were hard to set exactly and to repeat on another axis). Typing sets the number (the view
// model checks it and shows a message while it is no number of the range); Up / Down and the mouse wheel over the
// focused box change it by Step (Shift or Page Up / Page Down: ten steps); Enter or leaving the box puts a number outside
// the range back to its nearest end and tells the value it edits (INumberBoxOwner), so the page says so next to the box.
// Minimum, Maximum and Step are in the unit the box shows. The text is set through the box, so the binding carries it to
// the view model.
//   <TextBox views:NumberBox.Step="1" views:NumberBox.Minimum="0" views:NumberBox.Maximum="100" Text="{Binding Text}" />
public static class NumberBox
{
    public static readonly AttachedProperty<double> StepProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Step", typeof(NumberBox), double.NaN);

    public static readonly AttachedProperty<double> MinimumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Minimum", typeof(NumberBox), double.NegativeInfinity);

    public static readonly AttachedProperty<double> MaximumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Maximum", typeof(NumberBox), double.PositiveInfinity);

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Hooked", typeof(NumberBox));

    static NumberBox()
    {
        StepProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
    }

    public static double GetStep(TextBox box) => box.GetValue(StepProperty);

    public static void SetStep(TextBox box, double value) => box.SetValue(StepProperty, value);

    public static double GetMinimum(TextBox box) => box.GetValue(MinimumProperty);

    public static void SetMinimum(TextBox box, double value) => box.SetValue(MinimumProperty, value);

    public static double GetMaximum(TextBox box) => box.GetValue(MaximumProperty);

    public static void SetMaximum(TextBox box, double value) => box.SetValue(MaximumProperty, value);

    private static void Hook(TextBox box)
    {
        if (box.GetValue(HookedProperty))
            return;
        box.SetValue(HookedProperty, true);
        // Before the text box's own keys (Up / Down would move the caret).
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => Commit(box);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        bool large = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        int direction = e.Key switch
        {
            Key.Up => 1,
            Key.Down => -1,
            Key.PageUp => 10,
            Key.PageDown => -10,
            _ => 0
        };
        if (direction != 0)
        {
            e.Handled = StepBy(box, large ? direction * 10 : direction);
            return;
        }
        if (e.Key == Key.Enter)
            Commit(box);
    }

    // The wheel changes the number only while the box has the focus; otherwise the page scrolls as always.
    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not TextBox box || !box.IsKeyboardFocusWithin || e.Delta.Y == 0)
            return;
        int direction = e.Delta.Y > 0 ? 1 : -1;
        e.Handled = StepBy(box, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? direction * 10 : direction);
    }

    // The number `steps` steps further, kept in the range. False: the text is no number (nothing changes).
    internal static bool StepBy(TextBox box, int steps)
    {
        double step = GetStep(box);
        if (double.IsNaN(step) || step <= 0 || !TryParse(box.Text, out double number))
            return false;
        double next = Clamp(Math.Round((number + steps * step) / step) * step, GetMinimum(box), GetMaximum(box));
        SetText(box, next, step);
        return true;
    }

    // A number outside the range goes back to its nearest end, and the page says so; text that is no number stays (its
    // message shows).
    internal static void Commit(TextBox box)
    {
        if (!TryParse(box.Text, out double number))
            return;
        double kept = Clamp(number, GetMinimum(box), GetMaximum(box));
        if (kept == number)
            return;
        SetText(box, kept, GetStep(box));
        (box.DataContext as INumberBoxOwner)?.OnPutBackInRange(number, kept);
    }

    internal static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));

    private static void SetText(TextBox box, double value, double step)
    {
        // As many decimals as the step has, at least the four the curve keeps.
        int decimals = double.IsNaN(step) || step <= 0 ? 4 : Math.Max(4, (int)Math.Ceiling(-Math.Log10(step)));
        string text = Math.Round(value, Math.Min(decimals, 10)).ToString("0.##########", CultureInfo.InvariantCulture);
        box.Text = text;
        box.CaretIndex = text.Length;
    }

    internal static bool TryParse(string? text, out double number) =>
        double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
        !double.IsNaN(number) && !double.IsInfinity(number);
}
