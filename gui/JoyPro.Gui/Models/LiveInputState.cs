using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace JoyPro.Gui.Models;

// What the device shown on a picture is doing right now: the buttons and hat directions held and where its axes are
// (Visual Assign and the Layout page light the input up while it is used). Fed from the live input monitor on the UI
// thread; the canvases redraw on Changed.
public sealed class LiveInputState
{
    // An axis counts as "in use" this long after it last moved.
    public const long AxisActiveMs = 700;

    // Less than this (0..1 of the travel) is sensor noise, not a move.
    public const double AxisMoveThreshold = 0.015;

    private static readonly string[] AxisNames =
        ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    private readonly HashSet<string> pressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly double[] axes = Enumerable.Repeat(double.NaN, AxisNames.Length).ToArray();
    // Where each axis was when it was last counted as moved, and when (clock ms; -1 = never).
    private readonly double[] axisAnchor = Enumerable.Repeat(double.NaN, AxisNames.Length).ToArray();
    private readonly long[] axisMovedAt = Enumerable.Repeat(-1L, AxisNames.Length).ToArray();
    private readonly Func<long> realClock;

    public LiveInputState(Func<long>? clock = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        realClock = clock ?? (() => watch.ElapsedMilliseconds);
    }

    // Render tests stop the clock so a moved axis stays "in use" until the picture is taken.
    internal long? FrozenAt { get; set; }

    private long clock() => FrozenAt ?? realClock();

    public event EventHandler? Changed;

    public IReadOnlyCollection<string> Pressed => pressed;

    // Any button held or any axis moved within AxisActiveMs (the canvases keep pulsing while true).
    public bool AnyActive => pressed.Count > 0 || ActiveAxes().Any();

    public long Now => clock();

    // A button or hat direction went down or up; true when that changed anything.
    public bool SetButton(string input, bool down)
    {
        string token = (input ?? string.Empty).Trim();
        if (token.Length == 0)
            return false;
        bool changed = down ? pressed.Add(token) : pressed.Remove(token);
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    // The device's 8 axes (0..1). firstReport: the values jumped from DirectInput's defaults, nobody moved them.
    // Returns the axes that started or stopped counting as moved (the lists light their rows up for them).
    public IReadOnlyList<string> SetAxes(IReadOnlyList<double> values, bool firstReport = false)
    {
        List<string> toggled = [];
        long now = clock();
        for (int i = 0; i < Math.Min(values.Count, axes.Length); i++)
        {
            bool wasActive = IsAxisActive(i, now);
            axes[i] = values[i];
            if (double.IsNaN(axisAnchor[i]) || firstReport)
            {
                axisAnchor[i] = values[i];
                continue;
            }
            if (Math.Abs(values[i] - axisAnchor[i]) >= AxisMoveThreshold)
            {
                axisAnchor[i] = values[i];
                axisMovedAt[i] = now;
            }
            if (IsAxisActive(i, now) != wasActive)
                toggled.Add(AxisNames[i]);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return toggled;
    }

    // The axes whose activity ran out since the last call (the pulse timer asks).
    public IReadOnlyList<string> ExpiredAxes(ISet<string> shownActive)
    {
        long now = clock();
        return shownActive.Where(a => AxisIndex(a) is int i and >= 0 && !IsAxisActive(i, now)).ToList();
    }

    public void Clear()
    {
        bool any = pressed.Count > 0 || axes.Any(v => !double.IsNaN(v));
        pressed.Clear();
        Array.Fill(axes, double.NaN);
        Array.Fill(axisAnchor, double.NaN);
        Array.Fill(axisMovedAt, -1L);
        if (any)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsPressed(string? input) => input != null && pressed.Contains(input.Trim());

    // A held button or hat direction, or an axis in use.
    public bool IsActive(string? input)
    {
        if (IsPressed(input))
            return true;
        int axis = AxisIndex(input);
        return axis >= 0 && IsAxisActive(axis, clock());
    }

    public bool IsActive(IEnumerable<string> inputs) => inputs.Any(IsActive);

    // The axis value 0..1, NaN when unknown or the input is no axis.
    public double AxisValue(string? input)
    {
        int axis = AxisIndex(input);
        return axis < 0 ? double.NaN : axes[axis];
    }

    public IEnumerable<string> ActiveAxes()
    {
        long now = clock();
        for (int i = 0; i < AxisNames.Length; i++)
        {
            if (IsAxisActive(i, now))
                yield return AxisNames[i];
        }
    }

    public static int AxisIndex(string? input) =>
        Array.FindIndex(AxisNames, n => string.Equals(n, (input ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    private bool IsAxisActive(int index, long now) => axisMovedAt[index] >= 0 && now - axisMovedAt[index] <= AxisActiveMs;
}
