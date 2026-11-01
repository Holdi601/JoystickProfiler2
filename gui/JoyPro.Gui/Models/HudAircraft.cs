using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// The aircraft widget (docs/hud.md "Aircraft stick and throttle"): the stick and the throttle of the aircraft the user
// flies, as the game draws them (the grip pictures of the cockpit picture sets, docs/cockpits.md; else JoyPro's line
// drawing of the grip), moving with the user's own stick and throttle and lighting the control that the pressed
// button is (by the control vocabulary: a Warthog's "stick.hat.tms.up" lights the A-10C's TMS).
// The live part allocates nothing: the sources' Moved marks the widget dirty, Flush (once per HUD update, capped by
// the HUD's frame rate) moves it; properties change only when their value does.
public sealed partial class HudAircraft : ObservableObject
{
    public const double PictureHeight = 240;
    public const double MaxRoll = 16, MaxPitch = 12, MaxYaw = 12;
    public const double ThrottleTravel = 46;

    // A control the aircraft has: its name and its places on the pictures.
    public sealed class Control(string id, string name, string side)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Side { get; } = side;
        public List<HudAircraftMarker> Markers { get; } = [];
        public int Held { get; set; }
    }

    private readonly List<Control> controls;
    // Per device: input -> the aircraft's controls it is.
    private readonly Dictionary<HudDevice, Dictionary<string, List<Control>>> byInput = [];
    private HudDevice? stickSource, throttleSource;
    private readonly Action<HudDevice> onMoved;
    private bool dirty = true;

    public HudAircraft(string key, string title, string credit, Bitmap? stick, Bitmap? throttle, IEnumerable<Control> aircraftControls)
    {
        Key = key;
        Title = title;
        Credit = credit;
        StickPicture = stick;
        ThrottlePicture = throttle;
        StickWidth = Width(stick);
        ThrottleWidth = Width(throttle);
        controls = aircraftControls.ToList();
        foreach (Control control in controls)
        {
            foreach (HudAircraftMarker marker in control.Markers)
            {
                marker.Place(control.Side == "throttle" ? ThrottleWidth : StickWidth, PictureHeight);
                (control.Side == "throttle" ? ThrottleMarkers : StickMarkers).Add(marker);
            }
        }
        onMoved = _ => dirty = true;
    }

    // "<game>/<set>" of the pictures, or "drawing:<variant>" for JoyPro's drawings.
    public string Key { get; }

    public string Title { get; }

    // Where the pictures come from ("Rendered from DCS World's model on this PC").
    public string Credit { get; }

    public Bitmap? StickPicture { get; }

    public Bitmap? ThrottlePicture { get; }

    // PNG bytes of the pictures for the stream page (made with the pictures).
    public byte[]? StickPng { get; init; }

    public byte[]? ThrottlePng { get; init; }

    public double StickWidth { get; }

    public double ThrottleWidth { get; }

    public bool HasStick => StickPicture != null;

    public bool HasThrottle => ThrottlePicture != null;

    public ObservableCollection<HudAircraftMarker> StickMarkers { get; } = [];

    public ObservableCollection<HudAircraftMarker> ThrottleMarkers { get; } = [];

    // The aircraft's controls that are held, by name ("TMS (Target Management Switch)").
    public ObservableCollection<string> PressedChips { get; } = [];

    public IReadOnlyList<Control> Controls => controls;

    public HudDevice? StickSource => stickSource;

    public HudDevice? ThrottleSource => throttleSource;

    // Tilt of the stick picture in degrees (Rotate3DTransform), travel of the throttle picture in pixels.
    [ObservableProperty]
    private double stickRoll;

    [ObservableProperty]
    private double stickPitch;

    [ObservableProperty]
    private double stickYaw;

    [ObservableProperty]
    private double throttleOffset;

    [ObservableProperty]
    private double throttleTilt;

    // 0..1 for the travel rail next to the throttle.
    [ObservableProperty]
    private double throttleValue;

    // Where the stick and throttle move from (the designer's choice or Automatic).
    public void SetSources(HudDevice? stick, HudDevice? throttle)
    {
        if (stickSource != null)
            stickSource.Moved -= onMoved;
        if (throttleSource != null && !ReferenceEquals(throttleSource, stickSource))
            throttleSource.Moved -= onMoved;
        stickSource = stick;
        throttleSource = throttle;
        if (stickSource != null)
            stickSource.Moved += onMoved;
        if (throttleSource != null && !ReferenceEquals(throttleSource, stickSource))
            throttleSource.Moved += onMoved;
        dirty = true;
        Flush();
    }

    // Which input of which device is which control of the aircraft: the device's control vocabulary id is the
    // aircraft's control id or starts with it ("stick.hat.tms.up" -> "stick.hat.tms"; the longest one wins).
    public void BindDevices(IEnumerable<HudDevice> devices)
    {
        byInput.Clear();
        foreach (HudDevice device in devices)
        {
            Dictionary<string, List<Control>> map = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string input, string vocabulary) in device.Controls)
            {
                Control? best = null;
                foreach (Control control in controls)
                {
                    if (control.Markers.Count == 0 && control.Name.Length == 0)
                        continue;
                    bool same = string.Equals(vocabulary, control.Id, StringComparison.OrdinalIgnoreCase)
                                || vocabulary.StartsWith(control.Id + ".", StringComparison.OrdinalIgnoreCase);
                    if (same && (best == null || control.Id.Length > best.Id.Length))
                        best = control;
                }
                if (best != null)
                    map[input] = [best];
            }
            if (map.Count > 0)
                byInput[device] = map;
        }
    }

    public bool Knows(HudDevice device, string input) => byInput.TryGetValue(device, out var map) && map.ContainsKey(input);

    // A button of a device went down or up: its aircraft control lights up (a hat direction lights the hat).
    public void OnInput(HudDevice device, string input, bool down)
    {
        if (!byInput.TryGetValue(device, out Dictionary<string, List<Control>>? map) || !map.TryGetValue(input, out List<Control>? hit))
            return;
        foreach (Control control in hit)
        {
            control.Held = Math.Max(0, control.Held + (down ? 1 : -1));
            bool lit = control.Held > 0;
            foreach (HudAircraftMarker marker in control.Markers)
                marker.IsPressed = lit;
            string name = ShortName(control.Name);
            if (lit && name.Length > 0 && !PressedChips.Contains(name))
                PressedChips.Add(name);
            else if (!lit)
                PressedChips.Remove(name);
            while (PressedChips.Count > 4)
                PressedChips.RemoveAt(0);
        }
    }

    public void ReleaseAll()
    {
        foreach (Control control in controls)
        {
            control.Held = 0;
            foreach (HudAircraftMarker marker in control.Markers)
                marker.IsPressed = false;
        }
        PressedChips.Clear();
    }

    // Moves the pictures to where the sources are (called once per HUD update; nothing happens when nothing moved).
    public void Flush()
    {
        if (!dirty)
            return;
        dirty = false;
        double x = stickSource?.StickX ?? 0, y = stickSource?.StickY ?? 0, twist = stickSource?.StickTwist ?? 0;
        StickRoll = Math.Round(x * MaxRoll, 1);
        StickPitch = Math.Round(-y * MaxPitch, 1);
        StickYaw = Math.Round(twist * MaxYaw, 1);
        double travel = throttleSource == null || throttleSource.Levers.Count == 0 ? 0.5 : throttleSource.LeverValue(0);
        ThrottleValue = Math.Round(travel, 3);
        ThrottleOffset = Math.Round((0.5 - travel) * ThrottleTravel, 1);
        ThrottleTilt = Math.Round((travel - 0.5) * -14, 1);
    }


    public void Detach() => SetSources(null, null);

    private static double Width(Bitmap? picture) =>
        picture == null || picture.Size.Height <= 0 ? 0 : Math.Round(PictureHeight * picture.Size.Width / picture.Size.Height);

    // "TMS (Target Management Switch)" stays; "Trigger, first detent (gun camera)" -> "Trigger, first detent".
    internal static string ShortName(string name)
    {
        string text = (name ?? string.Empty).Trim();
        if (text.Length > 34)
            text = Regex.Replace(text, @"\s*\(.*?\)", string.Empty).Trim();
        return text.Length > 34 ? text[..33] + "…" : text;
    }
}

// A control's place on an aircraft's grip picture (0..1 of the picture), placed in widget pixels.
public sealed partial class HudAircraftMarker : ObservableObject
{
    public const double Size = 18;

    public HudAircraftMarker(string controlId, string name, double x, double y)
    {
        ControlId = controlId;
        Name = name;
        RelativeX = x;
        RelativeY = y;
    }

    public string ControlId { get; }

    public string Name { get; }

    public double RelativeX { get; }

    public double RelativeY { get; }

    [ObservableProperty]
    private double left;

    [ObservableProperty]
    private double top;

    [ObservableProperty]
    private bool isPressed;

    public void Place(double width, double height)
    {
        Left = RelativeX * width - Size / 2;
        Top = RelativeY * height - Size / 2;
    }
}
