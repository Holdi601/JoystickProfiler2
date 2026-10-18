using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// Settings > Telemetry recording: a small timeline of a recorded session, one strip per channel (altitude, speed, G ...)
// over the whole session, each scaled to its own min..max (TelemetryPreview keeps the peaks of every time bucket).
public sealed class TelemetryTimelineView : Control
{
    public static readonly StyledProperty<IReadOnlyList<TelemetryPreviewTrack>?> TracksProperty =
        AvaloniaProperty.Register<TelemetryTimelineView, IReadOnlyList<TelemetryPreviewTrack>?>(nameof(Tracks));

    private const double StripHeight = 54;
    private const double PadLeft = 8;
    private const double PadRight = 8;
    private const double LabelHeight = 15;
    private const double AxisHeight = 16;

    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#0F172A"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#1E293B")), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IPen[] TrackPens =
    [
        new Pen(new SolidColorBrush(Color.Parse("#38BDF8")), 1.5, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#F59E0B")), 1.5, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#4ADE80")), 1.5, lineJoin: PenLineJoin.Round),
        new Pen(new SolidColorBrush(Color.Parse("#F472B6")), 1.5, lineJoin: PenLineJoin.Round),
    ];
    private static readonly Typeface Font = new(FontFamily.Default);

    static TelemetryTimelineView()
    {
        AffectsRender<TelemetryTimelineView>(TracksProperty);
        AffectsMeasure<TelemetryTimelineView>(TracksProperty);
    }

    public IReadOnlyList<TelemetryPreviewTrack>? Tracks
    {
        get => GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Math.Max(1, Tracks?.Count ?? 1);
        double w = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(w, count * (StripHeight + LabelHeight) + AxisHeight + 6);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(PanelBrush, FramePen, bounds, 6, 6);
        IReadOnlyList<TelemetryPreviewTrack>? tracks = Tracks;
        if (tracks == null || tracks.Count == 0)
            return;
        double width = Math.Max(20, bounds.Width - PadLeft - PadRight);
        double duration = 0;
        foreach (TelemetryPreviewTrack track in tracks)
            duration = Math.Max(duration, track.Duration);
        duration = Math.Max(duration, 0.001);
        double y = 4;
        for (int i = 0; i < tracks.Count; i++)
        {
            TelemetryPreviewTrack track = tracks[i];
            IPen pen = TrackPens[i % TrackPens.Length];
            Text(context, track.Label, new Point(PadLeft, y), TitleBrush, 0, 11);
            Text(context, Number(track.Max) + " / " + Number(track.Min), new Point(bounds.Width - PadRight, y), LabelBrush, 1, 10);
            Rect strip = new(PadLeft, y + LabelHeight, width, StripHeight - 4);
            context.DrawRectangle(null, GridPen, strip);
            double range = track.Max - track.Min;
            if (track.Times.Count > 1)
            {
                using (context.PushClip(strip.Inflate(1)))
                {
                    StreamGeometry geometry = new();
                    using (StreamGeometryContext g = geometry.Open())
                    {
                        for (int p = 0; p < track.Times.Count; p++)
                        {
                            double x = strip.Left + track.Times[p] / duration * strip.Width;
                            double norm = range > 1e-12 ? (track.Values[p] - track.Min) / range : 0.5;
                            Point point = new(x, strip.Bottom - 2 - norm * (strip.Height - 4));
                            if (p == 0)
                                g.BeginFigure(point, false);
                            else
                                g.LineTo(point);
                        }
                        g.EndFigure(false);
                    }
                    context.DrawGeometry(null, pen, geometry);
                }
            }
            y += StripHeight + LabelHeight;
        }

        // Time axis: start, middle, end of the session.
        Text(context, "0:00", new Point(PadLeft, y), LabelBrush, 0, 10);
        Text(context, Clock(duration / 2), new Point(PadLeft + width / 2, y), LabelBrush, 0.5, 10);
        Text(context, Clock(duration), new Point(PadLeft + width, y), LabelBrush, 1, 10);
    }

    private static string Number(double value)
    {
        if (double.IsNaN(value))
            return "-";
        double abs = Math.Abs(value);
        string format = abs >= 1000 ? "0" : abs >= 10 ? "0.#" : "0.###";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    private static string Clock(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void Text(DrawingContext context, string text, Point at, IBrush brush, double alignX, double size)
    {
        FormattedText formatted = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, size, brush);
        context.DrawText(formatted, new Point(at.X - formatted.Width * alignX, at.Y));
    }
}
