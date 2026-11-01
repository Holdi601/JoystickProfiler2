using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Services;

// The HUD as the stream page sees it (docs/hud.md "Streamer mode"): the layout (the widgets ticked for the stream with
// their places, styles as CSS values, pictures) and the live state (stick positions, bars, levers, held buttons, hats,
// chips, the binds ticker, the header), as Server-Sent Events for HudStreamServer. Runs on the UI thread. Without a
// connected page nothing is built. The state is written into one reused buffer (no allocation per update; the texts
// of chips and lines are the app's own strings).
public sealed class HudStreamHub : IHudStreamSource, IDisposable
{
    private static readonly byte[] StatePrefix = Encoding.ASCII.GetBytes("event: state\ndata: ");
    private static readonly byte[] EventEnd = Encoding.ASCII.GetBytes("\n\n");

    private readonly IReadOnlyList<HudWidget> widgets;
    private readonly HudContext context;
    private readonly Func<HudAircraft?> aircraft;
    private readonly ArrayBufferWriter<byte> buffer = new(16384);
    private readonly Utf8JsonWriter json;
    private readonly ConcurrentDictionary<string, byte[]> pictures = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, string> pictureIds = new();
    private readonly object snapshotGate = new();
    private string lastLayout = "{}";
    private string lastState = "{}";
    private volatile bool layoutDirty = true;
    private int pictureCounter;

    public HudStreamHub(IReadOnlyList<HudWidget> widgets, HudContext context, Func<HudAircraft?> aircraft)
    {
        this.widgets = widgets;
        this.context = context;
        this.aircraft = aircraft;
        json = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });
    }

    public HudStreamServer? Server { get; set; }

    // How many states were sent (tests, the soak measurement).
    public long Published { get; private set; }

    // The widgets, a style or a picture changed: the pages get a new layout with the next update.
    public void LayoutChanged() => layoutDirty = true;

    // After every HUD update (HudInputPump): the new state for the connected pages; nothing without one.
    public void Publish()
    {
        HudStreamServer? server = Server;
        if (server == null || server.ClientCount == 0)
            return;
        if (layoutDirty)
        {
            layoutDirty = false;
            string layout = BuildLayout();
            lock (snapshotGate)
                lastLayout = layout;
            server.Broadcast(Encoding.UTF8.GetBytes("event: layout\ndata: " + layout + "\n\n"), replaceable: false);
        }
        WriteState();
        server.Broadcast(buffer.WrittenSpan, replaceable: true);
        Published++;
    }

    // Asks for an update on the UI thread (the view model kicks the HUD's pump).
    public Action? UpdateRequested { get; set; }

    // From the server's thread: the new page gets the whole layout with the next update, which comes at once.
    public void ClientJoined()
    {
        layoutDirty = true;
        UpdateRequested?.Invoke();
    }

    // From the server's thread: the layout and the state as they are now (made on the UI thread; the last ones when the
    // UI thread does not answer within a second). The page is served with it, so it shows the HUD at once.
    public string Snapshot()
    {
        try
        {
            Avalonia.Threading.DispatcherOperation operation = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshSnapshot);
            operation.GetTask().Wait(1000);
        }
        catch (Exception)
        {
        }
        lock (snapshotGate)
            return "{\"layout\":" + lastLayout + ",\"state\":" + lastState + "}";
    }

    private void RefreshSnapshot()
    {
        string layout = BuildLayout();
        WriteState();
        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        string state = Encoding.UTF8.GetString(written.Slice(StatePrefix.Length, written.Length - StatePrefix.Length - EventEnd.Length));
        lock (snapshotGate)
        {
            lastLayout = layout;
            lastState = state;
        }
    }

    public byte[]? Picture(string id) => pictures.TryGetValue(id, out byte[]? png) ? png : null;

    // The layout as JSON (also for the tests); on the UI thread.
    public string BuildLayout()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("width", HudLayout.ReferenceWidth);
            w.WriteNumber("height", HudLayout.ReferenceHeight);
            w.WriteStartArray("widgets");
            for (int i = 0; i < widgets.Count; i++)
            {
                HudWidget widget = widgets[i];
                if (!widget.StreamVisible)
                    continue;
                w.WriteStartObject();
                w.WriteString("i", widget.Config.Id);
                w.WriteString("k", widget.Kind);
                w.WriteNumber("x", widget.X);
                w.WriteNumber("y", widget.Y);
                w.WriteNumber("s", widget.Scale);
                w.WriteStartObject("css");
                foreach (KeyValuePair<string, string> pair in widget.Look.CssVariables())
                    w.WriteString(pair.Key, pair.Value);
                w.WriteEndObject();
                w.WriteBoolean("name", widget.ShowName);
                w.WriteBoolean("kind", widget.ShowKind);
                w.WriteBoolean("btn", widget.ShowButtons);
                w.WriteBoolean("chips", widget.ChipsVisible);
                w.WriteBoolean("values", widget.ShowValues);
                w.WriteBoolean("labels", widget.ShowLabels);
                w.WriteBoolean("gimbal", widget.ShowGimbal);
                w.WriteBoolean("axes", widget.ShowAxes);
                w.WriteBoolean("hpic", widget.ShowHeaderPicture);
                if (widget.IsDevice && widget.Device is { } device)
                {
                    w.WriteString("title", device.ShortTitle);
                    w.WriteString("dkind", device.DeviceKind);
                    w.WriteString("pic", widget.PictureVisible ? PictureId(device.Picture) : string.Empty);
                    w.WriteNumber("pw", device.PictureWidth);
                    w.WriteNumber("ph", HudDevice.PictureHeight);
                    w.WriteBoolean("tilt", widget.Tilt && device.CanTilt);
                    w.WriteBoolean("mv", widget.MarkersVisible);
                    w.WriteStartArray("markers");
                    for (int m = 0; m < device.Markers.Count; m++)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(Math.Round(device.Markers[m].RelativeX, 4));
                        w.WriteNumberValue(Math.Round(device.Markers[m].RelativeY, 4));
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteNumber("grid", widget.ButtonGridVisible ? device.ButtonCells.Count : 0);
                    w.WriteBoolean("gim", widget.GimbalVisible);
                    w.WriteBoolean("tw", device.HasTwist);
                    w.WriteStartArray("bars");
                    if (widget.AxesVisible)
                    {
                        for (int b = 0; b < device.Bars.Count; b++)
                        {
                            w.WriteStartArray();
                            w.WriteStringValue(device.Bars[b].Label);
                            w.WriteBooleanValue(device.Bars[b].Bipolar);
                            w.WriteEndArray();
                        }
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("levers");
                    if (widget.LeversVisible)
                    {
                        for (int b = 0; b < device.Levers.Count; b++)
                            w.WriteStringValue(device.Levers[b].Label);
                    }
                    w.WriteEndArray();
                    w.WriteNumber("hats", widget.HatsVisible ? device.Hats.Count : 0);
                }
                else if (widget.IsAircraft)
                {
                    HudAircraft? a = aircraft();
                    w.WriteString("title", a?.Title ?? string.Empty);
                    w.WriteString("sp", a != null && widget.AircraftStickVisible ? PictureId(a.StickPng) : string.Empty);
                    w.WriteNumber("sw", a?.StickWidth ?? 0);
                    w.WriteString("tp", a != null && widget.AircraftThrottleVisible ? PictureId(a.ThrottlePng) : string.Empty);
                    w.WriteNumber("tw", a?.ThrottleWidth ?? 0);
                    w.WriteNumber("ph", HudAircraft.PictureHeight);
                    w.WriteNumber("travel", HudAircraft.ThrottleTravel);
                    WriteMarkers(w, "sm", a?.StickMarkers);
                    WriteMarkers(w, "tm", a?.ThrottleMarkers);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMarkers(Utf8JsonWriter w, string name, IReadOnlyList<HudAircraftMarker>? markers)
    {
        w.WriteStartArray(name);
        if (markers != null)
        {
            for (int m = 0; m < markers.Count; m++)
            {
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(markers[m].RelativeX, 4));
                w.WriteNumberValue(Math.Round(markers[m].RelativeY, 4));
                w.WriteEndArray();
            }
        }
        w.WriteEndArray();
    }

    // The live state as one SSE "state" event in the reused buffer.
    private void WriteState()
    {
        buffer.ResetWrittenCount();
        buffer.Write(StatePrefix);
        json.Reset(buffer);
        json.WriteStartObject();
        json.WriteStartArray("w");
        for (int i = 0; i < widgets.Count; i++)
        {
            HudWidget widget = widgets[i];
            if (!widget.StreamVisible)
                continue;
            if (widget.IsDevice && widget.Device is { } d)
            {
                json.WriteStartObject();
                json.WriteString("i", widget.Config.Id);
                json.WriteStartArray("g");
                json.WriteNumberValue(d.StickX);
                json.WriteNumberValue(d.StickY);
                json.WriteNumberValue(d.StickTwist);
                json.WriteEndArray();
                json.WriteStartArray("r");
                json.WriteNumberValue(d.Roll);
                json.WriteNumberValue(d.Pitch);
                json.WriteNumberValue(d.Twist);
                json.WriteEndArray();
                json.WriteStartArray("b");
                for (int b = 0; b < d.Bars.Count; b++)
                    json.WriteNumberValue(Math.Round(d.Bars[b].Value, 3));
                json.WriteEndArray();
                json.WriteStartArray("v");
                for (int b = 0; b < d.Levers.Count; b++)
                    json.WriteNumberValue(Math.Round(d.Levers[b].Value, 3));
                json.WriteEndArray();
                json.WriteStartArray("p");
                for (int b = 0; b < d.ButtonCells.Count; b++)
                {
                    if (d.ButtonCells[b].IsPressed)
                        json.WriteNumberValue(b);
                }
                json.WriteEndArray();
                json.WriteStartArray("m");
                for (int m = 0; m < d.Markers.Count; m++)
                {
                    if (d.Markers[m].IsPressed)
                        json.WriteNumberValue(m);
                }
                json.WriteEndArray();
                json.WriteStartArray("h");
                for (int h = 0; h < d.Hats.Count; h++)
                {
                    int dir = -1;
                    for (int k = 0; k < d.Hats[h].Dots.Count; k++)
                    {
                        if (d.Hats[h].Dots[k].IsActive)
                            dir = k;
                    }
                    json.WriteNumberValue(dir);
                }
                json.WriteEndArray();
                json.WriteStartArray("c");
                for (int c = 0; c < d.PressedChips.Count; c++)
                    json.WriteStringValue(d.PressedChips[c]);
                json.WriteEndArray();
                json.WriteEndObject();
            }
            else if (widget.IsAircraft && aircraft() is { } a)
            {
                json.WriteStartObject();
                json.WriteString("i", widget.Config.Id);
                json.WriteStartArray("s");
                json.WriteNumberValue(a.StickRoll);
                json.WriteNumberValue(a.StickPitch);
                json.WriteNumberValue(a.StickYaw);
                json.WriteEndArray();
                json.WriteStartArray("t");
                json.WriteNumberValue(a.ThrottleOffset);
                json.WriteNumberValue(a.ThrottleTilt);
                json.WriteNumberValue(a.ThrottleValue);
                json.WriteEndArray();
                json.WriteStartArray("g");
                json.WriteNumberValue(a.StickSource?.StickX ?? 0);
                json.WriteNumberValue(a.StickSource?.StickY ?? 0);
                json.WriteNumberValue(a.StickSource?.StickTwist ?? 0);
                json.WriteEndArray();
                WritePressed("m", a.StickMarkers);
                WritePressed("n", a.ThrottleMarkers);
                json.WriteStartArray("c");
                for (int c = 0; c < a.PressedChips.Count; c++)
                    json.WriteStringValue(a.PressedChips[c]);
                json.WriteEndArray();
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
        json.WriteStartArray("l");
        for (int i = 0; i < context.Lines.Count; i++)
        {
            OverlayLine line = context.Lines[i];
            json.WriteStartArray();
            json.WriteStringValue(line.Text);
            json.WriteStringValue(line.Detail);
            json.WriteNumberValue(Math.Round(line.Opacity, 2));
            json.WriteNumberValue(line.FontSize);
            json.WriteBooleanValue(line.Align == Avalonia.Layout.HorizontalAlignment.Right);
            json.WriteEndArray();
        }
        json.WriteEndArray();
        json.WriteString("hd", context.HeaderText);
        json.WriteString("hp", PictureId(context.HeaderPicture));
        json.WriteEndObject();
        json.Flush();
        buffer.Write(EventEnd);
        if (Published % 30 == 0)
        {
            // The /hud/state snapshot is refreshed about once a second (a string per second, not per update).
            ReadOnlySpan<byte> written = buffer.WrittenSpan;
            string state = Encoding.UTF8.GetString(written.Slice(StatePrefix.Length, written.Length - StatePrefix.Length - EventEnd.Length));
            lock (snapshotGate)
                lastState = state;
        }
    }

    private void WritePressed(string name, IReadOnlyList<HudAircraftMarker> markers)
    {
        json.WriteStartArray(name);
        for (int m = 0; m < markers.Count; m++)
        {
            if (markers[m].IsPressed)
                json.WriteNumberValue(m);
        }
        json.WriteEndArray();
    }

    // The id of a picture for /hud/img/<id> (its PNG made once; the same bitmap keeps its id).
    private string PictureId(Bitmap? bitmap)
    {
        if (bitmap == null)
            return string.Empty;
        if (pictureIds.TryGetValue(bitmap, out string? known))
            return known;
        try
        {
            using MemoryStream stream = new();
            bitmap.Save(stream);
            string id = "p" + (++pictureCounter) + ".png";
            pictures[id] = stream.ToArray();
            pictureIds.AddOrUpdate(bitmap, id);
            return id;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // The aircraft's grip pictures come with their PNG already made.
    private string PictureId(byte[]? png)
    {
        if (png == null)
            return string.Empty;
        if (pictureIds.TryGetValue(png, out string? known))
            return known;
        string id = "a" + (++pictureCounter) + ".png";
        pictures[id] = png;
        pictureIds.AddOrUpdate(png, id);
        return id;
    }

    public void Dispose()
    {
        json.Dispose();
        pictures.Clear();
    }
}
