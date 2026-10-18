using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace JoyPro.Gui.Services;

// "Follow input" on the Binds page: decides from the live input monitor's events when the user used an input to find
// its relation. A button or hat direction counts when it goes down (a bouncing contact only once); an axis when it
// moved a large part of its travel (AxisStepTravel) from where it was, and again for every further large move, so
// moving it to and fro keeps stepping until the user stops.
//
// The device's first report after it was opened shows its resting buttons "going down" (DirectInput showed nothing
// pressed until then); such a press only counts when the button is released again soon (a real, short press), a
// resting switch position never does. The first report's axis values are where the axes really are, not a move.
internal sealed class FollowInputTracker
{
    public const double AxisStepTravel = 0.4;
    public const long AxisStepGapMs = 350;
    public const long ButtonDebounceMs = 120;
    public const long UncertainReleaseMs = 1500;

    // DIJOYSTATE2 order, as JoystickCaptureService names them.
    private static readonly string[] AxisNames = ["JOY_X", "JOY_Y", "JOY_Z", "JOY_RX", "JOY_RY", "JOY_RZ", "JOY_SLIDER1", "JOY_SLIDER2"];

    private readonly Func<long> clock;
    private readonly Dictionary<string, long> lastPress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> uncertain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double[]> anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> lastAxisStep = new(StringComparer.OrdinalIgnoreCase);

    // The last OnAxes call saw an axis a large part of its travel away from its anchor, also when it did not step
    // (too soon after the last step): the user is moving it, so the window keeps tooltips back.
    public bool AxisMovedFar { get; private set; }

    public FollowInputTracker(Func<long>? clock = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        this.clock = clock ?? (() => watch.ElapsedMilliseconds);
    }

    // The input to follow ("JOY_BTN8", "JOY_BTN_POV1_U"), or null.
    public string? OnButton(string device, string input, bool down, bool firstReport)
    {
        if (string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(input) ||
            string.Equals(device, "Keyboard", StringComparison.OrdinalIgnoreCase))
            return null;
        string key = device.Trim() + "|" + input.Trim();
        long now = clock();
        if (down)
        {
            if (firstReport)
            {
                uncertain[key] = now;
                return null;
            }
            if (lastPress.TryGetValue(key, out long last) && now - last < ButtonDebounceMs)
                return null;
            lastPress[key] = now;
            return input.Trim();
        }
        if (uncertain.Remove(key, out long since) && now - since <= UncertainReleaseMs)
        {
            lastPress[key] = now;
            return input.Trim();
        }
        return null;
    }

    // The axis to follow ("JOY_X"), or null. axes: the device's 8 axes, 0..1.
    public string? OnAxes(string device, IReadOnlyList<double> axes, bool firstReport)
    {
        AxisMovedFar = false;
        if (string.IsNullOrWhiteSpace(device) || axes == null)
            return null;
        string name = device.Trim();
        if (!anchors.TryGetValue(name, out double[]? anchor) || firstReport || anchor.Length != axes.Count)
        {
            anchors[name] = Copy(axes);
            return null;
        }

        long now = clock();
        int best = -1;
        double bestTravel = 0;
        for (int i = 0; i < Math.Min(axes.Count, AxisNames.Length); i++)
        {
            double travel = Math.Abs(axes[i] - anchor[i]);
            if (travel >= AxisStepTravel && travel > bestTravel)
            {
                best = i;
                bestTravel = travel;
            }
        }
        if (best < 0)
            return null;
        AxisMovedFar = true;
        string key = name + "|" + AxisNames[best];
        if (lastAxisStep.TryGetValue(key, out long last) && now - last < AxisStepGapMs)
            return null;
        lastAxisStep[key] = now;
        anchor[best] = axes[best];
        return AxisNames[best];
    }

    public void Reset()
    {
        lastPress.Clear();
        uncertain.Clear();
        anchors.Clear();
        lastAxisStep.Clear();
    }

    private static double[] Copy(IReadOnlyList<double> values)
    {
        double[] copy = new double[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i];
        return copy;
    }
}
