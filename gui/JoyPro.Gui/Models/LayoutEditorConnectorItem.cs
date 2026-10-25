using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class LayoutEditorConnectorItem : ObservableObject
{
    [ObservableProperty]
    private string name = "Connector";

    [ObservableProperty]
    private string color = "#E2E8F0";

    [ObservableProperty]
    private double strokeWidth = 2;

    [ObservableProperty]
    private string route = "orthogonal";

    [ObservableProperty]
    private int steps = 2;

    [ObservableProperty]
    private string targetToken = string.Empty;

    [ObservableProperty]
    private string pointsCsv = string.Empty;

    [ObservableProperty]
    private AvaloniaList<Point> points = [];

    [ObservableProperty]
    private AvaloniaList<LayoutEditorConnectorPointHandleItem> pointHandles = [];

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isFlashing;

    // Its input is pressed (or the axis moved) right now: the line lights up.
    [ObservableProperty]
    private bool isLive;

    public string DisplayStroke =>
        IsFlashing
            ? "#FDE047"
            : IsLive
            ? "#F59E0B"
            : IsSelected
            ? "#7DD3FC"
            : IsDefaultColor && SheetLineColor.Length > 0
            ? SheetLineColor
            : (string.IsNullOrWhiteSpace(Color) ? "#E2E8F0" : Color);

    // A line left at the default colour takes the cheat sheet style's line colour (as the engine prints it).
    private string sheetLineColor = string.Empty;

    public string SheetLineColor
    {
        get => sheetLineColor;
        set
        {
            if (SetProperty(ref sheetLineColor, value ?? string.Empty))
                OnPropertyChanged(nameof(DisplayStroke));
        }
    }

    private bool IsDefaultColor => string.IsNullOrWhiteSpace(Color) || string.Equals(Color.Trim(), "#E2E8F0", StringComparison.OrdinalIgnoreCase);

    public double DisplayStrokeThickness => IsFlashing || IsLive ? Math.Max(4, StrokeWidth + 2) : (IsSelected ? Math.Max(3, StrokeWidth + 1) : StrokeWidth);

    partial void OnIsLiveChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayStroke));
        OnPropertyChanged(nameof(DisplayStrokeThickness));
        OnPropertyChanged(nameof(HitStrokeThickness));
    }
    public double HitStrokeThickness => Math.Max(14, DisplayStrokeThickness + 10);

    public void SetPoints(IEnumerable<Point> newPoints)
    {
        Points = new AvaloniaList<Point>(newPoints ?? Array.Empty<Point>());
        RebuildPointHandles();
    }

    public bool SetPointAt(int index, Point point)
    {
        if (index < 0 || index >= Points.Count)
            return false;

        AvaloniaList<Point> updated = new AvaloniaList<Point>(Points);
        updated[index] = point;
        Points = updated;
        if (index < PointHandles.Count)
        {
            PointHandles[index].X = point.X;
            PointHandles[index].Y = point.Y;
        }

        return true;
    }

    public bool TryParsePointsCsv(out List<Point> parsed, out string error)
    {
        parsed = new List<Point>();
        error = string.Empty;
        string raw = (PointsCsv ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = T("Connector points are empty.");
            return false;
        }

        string[] segments = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // Preferred format: x,y;x,y (also supports x|y and x y)
        bool allPairSegments = true;
        foreach (string segment in segments)
        {
            string part = (segment ?? string.Empty).Trim();
            if (part.Length == 0)
                continue;

            if (!TryParsePointPair(part, out Point point))
            {
                allPairSegments = false;
                break;
            }
            parsed.Add(point);
        }

        if (!allPairSegments)
        {
            // Fallback for locale-heavy manual edits: "x;y;x;y;..." where each token is a single number
            // and decimals may use "," (e.g. de-DE style).
            parsed.Clear();
            List<double> values = new List<double>();
            foreach (string segment in segments)
            {
                string part = (segment ?? string.Empty).Trim();
                if (part.Length == 0)
                    continue;

                if (!TryParseFlexibleDouble(part, out double value))
                {
                    error = T("Invalid point '{0}'. Use x,y;x,y (or x|y;x|y).", part);
                    return false;
                }

                values.Add(value);
            }

            if (values.Count < 4 || values.Count % 2 != 0)
            {
                error = T("Connector points invalid. Use x,y;x,y (at least 2 points).");
                return false;
            }

            for (int i = 0; i < values.Count; i += 2)
                parsed.Add(new Point(values[i], values[i + 1]));
        }

        if (parsed.Count < 2)
        {
            error = T("Connector needs at least 2 points.");
            return false;
        }

        return true;
    }

    public static string ToPointsCsv(IEnumerable<Point> points)
    {
        List<string> parts = new List<string>();
        foreach (Point point in points ?? Array.Empty<Point>())
        {
            parts.Add(
                point.X.ToString("0.##", CultureInfo.InvariantCulture) +
                "," +
                point.Y.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return string.Join(";", parts);
    }

    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(DisplayStroke));

    partial void OnStrokeWidthChanged(double value)
    {
        if (value < 1)
        {
            StrokeWidth = 1;
            return;
        }

        if (value > 12)
        {
            StrokeWidth = 12;
            return;
        }

        OnPropertyChanged(nameof(DisplayStrokeThickness));
        OnPropertyChanged(nameof(HitStrokeThickness));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayStroke));
        OnPropertyChanged(nameof(DisplayStrokeThickness));
        OnPropertyChanged(nameof(HitStrokeThickness));
    }

    partial void OnIsFlashingChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayStroke));
        OnPropertyChanged(nameof(DisplayStrokeThickness));
        OnPropertyChanged(nameof(HitStrokeThickness));
    }

    partial void OnRouteChanged(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized != "diagonal" && normalized != "manual")
            normalized = "orthogonal";
        if (!string.Equals(Route, normalized, StringComparison.Ordinal))
            Route = normalized;
    }

    partial void OnStepsChanged(int value)
    {
        int clamped = value;
        if (clamped < 0)
            clamped = 0;
        else if (clamped > 8)
            clamped = 8;

        if (clamped != value)
            Steps = clamped;
    }

    private static bool TryParsePointPair(string part, out Point point)
    {
        point = default;
        string text = (part ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        string[] pair;
        if (text.Contains('|'))
            pair = text.Split('|');
        else
            pair = text.Split(',');

        if (pair.Length == 2 &&
            TryParseFlexibleDouble(pair[0], out double x) &&
            TryParseFlexibleDouble(pair[1], out double y))
        {
            point = new Point(x, y);
            return true;
        }

        string[] whitespacePair = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (whitespacePair.Length == 2 &&
            TryParseFlexibleDouble(whitespacePair[0], out x) &&
            TryParseFlexibleDouble(whitespacePair[1], out y))
        {
            point = new Point(x, y);
            return true;
        }

        return false;
    }

    private static bool TryParseFlexibleDouble(string raw, out double value)
    {
        string text = (raw ?? string.Empty).Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return true;

        // Handle decimal comma explicitly if user copies numbers from locale-formatted UI.
        string commaToDot = text.Replace(',', '.');
        return double.TryParse(commaToDot, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void RebuildPointHandles()
    {
        PointHandles.Clear();
        for (int i = 0; i < Points.Count; i++)
        {
            Point point = Points[i];
            PointHandles.Add(new LayoutEditorConnectorPointHandleItem
            {
                Index = i,
                X = point.X,
                Y = point.Y
            });
        }
        OnPropertyChanged(nameof(PointHandles));
    }
}
