using System;
using System.Collections.Generic;
using System.Globalization;

namespace JoyPro.Gui.Services;

// Decides which button or hat of one device Record takes. Only a change of state counts, never an input that was
// already pressed when recording started: many devices have buttons that rest pressed (the middle of a 3-way switch,
// a mode switch's position), and taking one of them is a false positive.
//
// A device that sends reports only on change shows DirectInput's empty defaults (nothing pressed) until its first
// report. That first report shows the resting buttons pressed together with whatever made the device report, so a
// button that appears pressed in it is uncertain: it counts once it is released again (a press the user made), while
// a resting button stays pressed and never counts. Every later report is a real change and counts at once.
internal sealed class RecordButtonTracker
{
    private bool[] previousButtons;
    private int[] previousPovs;
    private bool reported;
    private readonly HashSet<int> heldAtStart = [];
    private readonly HashSet<int> uncertain = [];

    // initialButtons / initialPovs: the state when the device was opened (POVs as DirectInput angles, -1 = centred).
    // allowReleaseOfHeld: a button that was pressed at the start and is released counts (off by default).
    public RecordButtonTracker(IReadOnlyList<bool> initialButtons, IReadOnlyList<int> initialPovs, bool allowReleaseOfHeld = false)
    {
        previousButtons = Copy(initialButtons);
        previousPovs = Copy(initialPovs);
        AllowReleaseOfHeld = allowReleaseOfHeld;
        // Anything pressed in the opening state is a real report (the defaults have nothing pressed).
        reported = IsReport(previousButtons, previousPovs);
        for (int i = 0; i < previousButtons.Length; i++)
        {
            if (previousButtons[i])
                heldAtStart.Add(i);
        }
    }

    public bool AllowReleaseOfHeld { get; }

    // True once the device sent a report (its state is real, not DirectInput's defaults).
    public bool Reported => reported;

    // The buttons (0-based) pressed when recording started and still held.
    public IReadOnlyCollection<int> HeldAtStart => heldAtStart;

    // Feeds the next state. armed = false during the warmup: changes only update what is held. Returns the input
    // that counts ("JOY_BTN5", "JOY_BTN_POV1_UR"), or null.
    public string? Feed(IReadOnlyList<bool> buttons, IReadOnlyList<int> povs, bool armed)
    {
        bool[] current = Copy(buttons);
        int[] currentPovs = Copy(povs);
        try
        {
            if (!reported)
            {
                if (SameState(previousButtons, current, previousPovs, currentPovs))
                    return null;
                reported = true;
                for (int i = 0; i < current.Length; i++)
                {
                    if (!current[i])
                        continue;
                    if (armed)
                        uncertain.Add(i);
                    else
                        heldAtStart.Add(i);
                }
                // A hat does not rest off centre: a direction in the first report is the user's.
                return armed ? PovEdge(previousPovs, currentPovs) : null;
            }

            if (!armed)
            {
                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i])
                        heldAtStart.Add(i);
                    else
                        heldAtStart.Remove(i);
                }
                return null;
            }

            int count = Math.Min(previousButtons.Length, current.Length);
            for (int i = 0; i < count; i++)
            {
                if (current[i] && !previousButtons[i])
                {
                    uncertain.Remove(i);
                    return ButtonName(i);
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (current[i] || !previousButtons[i])
                    continue;
                if (uncertain.Remove(i))
                    return ButtonName(i);
                if (heldAtStart.Remove(i) && AllowReleaseOfHeld)
                    return ButtonName(i);
            }

            return PovEdge(previousPovs, currentPovs);
        }
        finally
        {
            previousButtons = current;
            previousPovs = currentPovs;
        }
    }

    internal static string ButtonName(int index) => "JOY_BTN" + (index + 1).ToString(CultureInfo.InvariantCulture);

    // A hat that moved to a new direction (not to the centre).
    private static string? PovEdge(int[] previous, int[] current)
    {
        int count = Math.Min(previous.Length, current.Length);
        for (int i = 0; i < count; i++)
        {
            string? before = PovDirection(previous[i]);
            string? after = PovDirection(current[i]);
            if (after != null && after != before)
                return "JOY_BTN_POV" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_" + after;
        }
        return null;
    }

    // "U", "UR", ... for a DirectInput hat angle (hundredths of a degree), null when centred.
    internal static string? PovDirection(int angle)
    {
        if (angle < 0 || angle > 35999)
            return null;
        int snapped = ((angle + 2250) / 4500) * 4500 % 36000;
        return snapped switch
        {
            0 => "U",
            4500 => "UR",
            9000 => "R",
            13500 => "DR",
            18000 => "D",
            22500 => "DL",
            27000 => "L",
            31500 => "UL",
            _ => null
        };
    }

    private static bool IsReport(bool[] buttons, int[] povs)
    {
        foreach (bool button in buttons)
        {
            if (button)
                return true;
        }
        foreach (int pov in povs)
        {
            if (PovDirection(pov) != null)
                return true;
        }
        return false;
    }

    private static bool SameState(bool[] a, bool[] b, int[] povA, int[] povB)
    {
        if (a.Length != b.Length || povA.Length != povB.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        for (int i = 0; i < povA.Length; i++)
        {
            if (PovDirection(povA[i]) != PovDirection(povB[i]))
                return false;
        }
        return true;
    }

    private static bool[] Copy(IReadOnlyList<bool>? values)
    {
        if (values == null)
            return [];
        bool[] copy = new bool[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i];
        return copy;
    }

    private static int[] Copy(IReadOnlyList<int>? values)
    {
        if (values == null)
            return [];
        int[] copy = new int[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i];
        return copy;
    }
}
