using System;
using System.Globalization;

namespace JoyPro.Gui.Models;

// One input of a device picture as the picture canvas sees it (contract of the device canvas, see
// docs/ui/layout-redesign.md). X and Y are the top-left corner of the input's label on the picture, in picture
// pixels. The canvas never writes them: a drag ends in a MarkerMoved event and the page moves the marker (with undo).
public interface IDeviceMarker
{
    // The DirectInput name of the input: JOY_BTN5, JOY_X, JOY_BTN_POV1_U, JOY_SLIDER1.
    string Input { get; }

    // button, axis, pov or slider.
    string Kind { get; }

    // The modifier the label is for ("" = none).
    string Modifier { get; }

    double X { get; }

    double Y { get; }

    // What the input does (the bound relation), "" when nothing is bound.
    string Command { get; }

    bool IsSelected { get; }
}

// A physical part of a device reference picture (docs/device-reference.md): X and Y are its centre (not a label
// corner), drawn as a ring of Radius with a numbered Badge; Label is the part's name ("TMS hat"); IsHighlighted lights
// it up (live input, the marker assistant asks for it).
public interface IDevicePartMarker : IDeviceMarker
{
    double Radius { get; }

    string Badge { get; }

    string Label { get; }

    bool IsHighlighted { get; }

    // The tooltip: name, inputs and the bound command.
    string Details { get; }
}

// What the canvas lets the user do: look (click selects), edit the markers (drag, place, nudge) or bind (click
// selects the input the side panel binds).
public enum DeviceCanvasMode
{
    View = 0,
    EditMarkers = 1,
    Bind = 2
}

// A finished drag (or arrow-key nudge) of a marker, in picture pixels.
public sealed record DeviceMarkerMove(IDeviceMarker Marker, double FromX, double FromY, double ToX, double ToY);

// A click on the picture outside the markers, in picture pixels.
public sealed record DeviceCanvasClick(double X, double Y, bool IsRightButton);

// A chip of an unplaced input (the marker assistant's list) dropped on the picture, in picture pixels.
public sealed record DevicePartDrop(string PartId, double X, double Y);

public static class DeviceMarkerText
{
    // "Throttle - HOTAS Warthog {GUID}" -> "Throttle - HOTAS Warthog".
    public static string DeviceName(string? device)
    {
        string value = (device ?? string.Empty).Trim();
        int brace = value.LastIndexOf('{');
        if (brace > 0 && value.EndsWith('}'))
            value = value[..brace].Trim();
        return value;
    }
    // A short label of an input for badges and key caps: "BTN 5", "POV1 Up", "Axis X", "Slider 1".
    public static string ShortLabel(string? input)
    {
        string token = (input ?? string.Empty).Trim();
        if (token.Length == 0)
            return "?";
        string upper = token.ToUpperInvariant();
        if (upper.StartsWith("JOY_BTN_POV", StringComparison.Ordinal))
        {
            string rest = upper["JOY_BTN_POV".Length..];
            int underscore = rest.IndexOf('_');
            if (underscore > 0)
            {
                string direction = rest[(underscore + 1)..] switch
                {
                    "U" => "Up",
                    "D" => "Down",
                    "L" => "Left",
                    "R" => "Right",
                    "UR" => "Up-Right",
                    "UL" => "Up-Left",
                    "DR" => "Down-Right",
                    "DL" => "Down-Left",
                    string other => other
                };
                return T("POV{0} {1}", rest[..underscore], direction);
            }
            return "POV" + rest;
        }
        if (upper.StartsWith("JOY_BTN", StringComparison.Ordinal))
            return T("BTN {0}", upper["JOY_BTN".Length..]);
        if (upper.StartsWith("JOY_SLIDER", StringComparison.Ordinal))
            return T("Slider {0}", upper["JOY_SLIDER".Length..]);
        if (upper.StartsWith("JOY_", StringComparison.Ordinal))
            return T("Axis {0}", upper["JOY_".Length..]);
        return token;
    }

    // Natural order of inputs: axes, buttons by number, hats, sliders.
    public static string SortKey(string? input, string? kind)
    {
        string token = (input ?? string.Empty).Trim().ToUpperInvariant();
        int group = (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "axis" => 0,
            "slider" => 1,
            "button" => 2,
            "pov" => 3,
            _ => 4
        };
        int digits = 0;
        int start = -1;
        int end = -1;
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsDigit(token[i]))
            {
                start = i;
                end = i;
                while (end < token.Length && char.IsDigit(token[end]) && end - start < 5)
                    end++;
                digits = int.Parse(token[start..end], CultureInfo.InvariantCulture);
                break;
            }
        }
        string prefix = start < 0 ? token : token[..start];
        string suffix = start < 0 ? string.Empty : token[end..];
        return group.ToString(CultureInfo.InvariantCulture) + "|" + prefix + "|" + digits.ToString("D5", CultureInfo.InvariantCulture) + "|" + suffix;
    }
}
