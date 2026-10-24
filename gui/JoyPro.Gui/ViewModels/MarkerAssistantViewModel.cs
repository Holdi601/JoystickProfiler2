using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// The marker assistant of the device reference (docs/device-reference.md): "press a button on your device" -> JoyPro
// sees which input it is -> "click where it is on the picture" -> next. Axes by moving them. Known parts without a
// number get the pressed input; inputs the model does not know become new parts. Finish saves the marker positions of
// the picture and, for a device the catalog did not know (or one that got new inputs), the user's own definition.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class MarkerAssistantViewModel : ObservableObject
{
    private readonly DeviceReferenceViewModel reference;
    private readonly IJoystickCaptureService capture;
    private readonly Stack<(ReferenceMarker Marker, double X, double Y, bool WasPlaced, bool WasShown)> undo = new();
    private CancellationTokenSource? cancel;
    private bool definitionChanged;
    // Parts the user skipped in "Now place" (asked again after the others).
    private readonly List<string> skipped = [];

    public MarkerAssistantViewModel(DeviceReferenceViewModel reference, IJoystickCaptureService capture)
    {
        this.reference = reference;
        this.capture = capture;
        reference.Assistant = this;
        reference.PictureChanged += OnPictureChanged;
        RefreshUnplaced();
    }

    // The assistant closes: it no longer follows the picture.
    public void Detach() => reference.PictureChanged -= OnPictureChanged;

    private void OnPictureChanged(object? sender, EventArgs e) => RefreshUnplaced();

    // ------------------------------------------------------------------ drag to place, "Now place: X"

    // The inputs without a place on the shown picture, as chips to drag onto the picture (the next one to place first).
    public System.Collections.ObjectModel.ObservableCollection<ReferenceMarker> UnplacedParts { get; } = [];

    // The part a click on the picture places now (the first unplaced one, the skipped ones last).
    [ObservableProperty] private ReferenceMarker? nextToPlace;

    // The rarely used tools (Axis, Stop, Add control, Remove from picture) behind "More".
    [ObservableProperty] private bool showMoreTools;

    // Before every placement (the page's undo remembers the parts as they were).
    public Action<string>? BeforePlacement { get; set; }

    public bool HasUnplacedParts => UnplacedParts.Count > 0;

    public bool HasNextToPlace => NowPlacing != null;

    // The part asked for: the pressed input's part while it waits for its click, else the next unplaced one.
    public ReferenceMarker? NowPlacing => Step == "Place" && Current != null ? Current : NextToPlace;

    public string NowPlaceText => NowPlacing is { } part ? T("Now place: {0}", PartLabel(part)) : T("Every input has its place on this picture.");

    public string UnplacedSummary => Plural("{0} input without a place: drag it onto the picture", "{0} inputs without a place: drag one onto the picture", UnplacedParts.Count);

    private static string PartLabel(ReferenceMarker part)
    {
        string inputs = string.Join(", ", part.Inputs.Where(i => i.Input.Length > 0).Select(i => DeviceMarkerText.ShortLabel(i.Input)).Distinct().Take(3));
        return inputs.Length > 0 ? part.Name + " (" + inputs + ")" : part.Name;
    }

    public void RefreshUnplaced()
    {
        List<ReferenceMarker> parts = reference.Markers.Where(m => !m.IsShown && m.Inputs.Any(i => i.Input.Length > 0)).ToList();
        List<ReferenceMarker> ordered = parts.Where(p => !skipped.Contains(p.Id)).Concat(skipped.Select(id => parts.FirstOrDefault(p => p.Id == id)).OfType<ReferenceMarker>()).ToList();
        if (!ordered.SequenceEqual(UnplacedParts))
        {
            UnplacedParts.Clear();
            foreach (ReferenceMarker part in ordered)
                UnplacedParts.Add(part);
        }
        NextToPlace = UnplacedParts.FirstOrDefault();
        NotifyNowPlace();
    }

    private void NotifyNowPlace()
    {
        OnPropertyChanged(nameof(HasUnplacedParts));
        OnPropertyChanged(nameof(NowPlacing));
        OnPropertyChanged(nameof(HasNextToPlace));
        OnPropertyChanged(nameof(NowPlaceText));
        OnPropertyChanged(nameof(UnplacedSummary));
    }

    partial void OnNextToPlaceChanged(ReferenceMarker? value) => NotifyNowPlace();

    partial void OnCurrentChanged(ReferenceMarker? value) => NotifyNowPlace();

    // "Skip" in Now place: this part is asked again after the others.
    [RelayCommand]
    private void SkipNext()
    {
        if (NextToPlace == null)
            return;
        skipped.Remove(NextToPlace.Id);
        skipped.Add(NextToPlace.Id);
        RefreshUnplaced();
    }

    // A click on the picture while no press waits for its click: the part of "Now place" goes there (0..1).
    public bool PlaceNextAt(double x, double y)
    {
        ReferenceMarker? part = NextToPlace;
        if (part == null)
            return false;
        PlacePart(part, x, y);
        Prompt = T("Placed \"{0}\". Click the next one, drag a chip onto the picture, or press a button.", part.Name);
        return true;
    }

    // A chip dropped on the picture (0..1): its part goes there.
    public bool DropPart(string partId, double x, double y)
    {
        ReferenceMarker? part = reference.Markers.FirstOrDefault(m => string.Equals(m.Id, partId, StringComparison.OrdinalIgnoreCase));
        if (part == null)
            return false;
        PlacePart(part, x, y);
        Prompt = T("Placed \"{0}\".", part.Name);
        return true;
    }

    // Every placement (press and click, Now place, a dropped chip): undoable here and on the page.
    private void PlacePart(ReferenceMarker part, double x, double y)
    {
        BeforePlacement?.Invoke(T("place {0}", part.Name));
        undo.Push((part, part.X, part.Y, part.IsPlaced, part.IsShown));
        reference.MoveMarker(part, x, y);
        reference.SelectedMarker = part;
        skipped.Remove(part.Id);
        PlacedCount++;
        RefreshUnplaced();
    }

    // Idle, Input (waiting for a button / hat / axis), Place (waiting for the click on the picture), Done.
    [ObservableProperty] private string step = "Idle";
    [ObservableProperty] private string prompt = T("Click the picture where the asked input is, drag a chip onto it, or press Start and then a button of the device.");
    [ObservableProperty] private ReferenceMarker? current;
    [ObservableProperty] private string currentInput = string.Empty;
    [ObservableProperty] private int placedCount;
    [ObservableProperty] private bool isRunning;
    // How long to wait for a press (ms).
    public int TimeoutMs { get; set; } = 30000;

    public bool IsWaitingForClick => Step == "Place";
    public bool IsWaitingForInput => Step == "Input";

    partial void OnStepChanged(string value)
    {
        OnPropertyChanged(nameof(IsWaitingForClick));
        OnPropertyChanged(nameof(IsWaitingForInput));
        NotifyNowPlace();
    }

    [RelayCommand]
    private Task StartAsync() => NextAsync(axis: false);

    [RelayCommand]
    private Task CaptureAxisAsync() => NextAsync(axis: true);

    // Waits for the next input of the device and asks where it is.
    private async Task NextAsync(bool axis)
    {
        if (reference.Model == null)
        {
            Prompt = T("Pick or connect a device first.");
            return;
        }
        cancel?.Cancel();
        CancellationTokenSource source = new();
        cancel = source;
        IsRunning = true;
        Step = "Input";
        SetCurrent(null);
        Prompt = axis ? T("Move one axis of {0} all the way.", DeviceLabel()) : T("Press a button or a hat direction on {0} (or move an axis with \"Axis\").", DeviceLabel());
        JoystickCaptureService.CaptureResult? result;
        try
        {
            result = await capture.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
            {
                AxisMode = axis,
                IncludeKeyboard = false,
                PreferredDevice = reference.DeviceName,
                TimeoutMs = TimeoutMs
            }, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (source.IsCancellationRequested || cancel != source)
            return;
        if (result == null || string.IsNullOrWhiteSpace(result.Input))
        {
            Step = "Idle";
            IsRunning = false;
            Prompt = T("Nothing was pressed. Press Start to go on, or Finish.");
            return;
        }
        if (reference.DeviceName.Length > 0 && result.Device.Length > 0 && !SameDevice(result.Device, reference.DeviceName))
        {
            Prompt = T("That was {0}, not {1}. Press a button on {2}.", DeviceProbe.RemoveGuid(result.Device), DeviceLabel(), DeviceLabel());
            _ = NextAsync(axis);
            return;
        }
        Found(result.Input.Trim().ToUpperInvariant());
    }

    private static bool SameDevice(string a, string b)
    {
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(DeviceProbe.RemoveGuid(a), DeviceProbe.RemoveGuid(b), StringComparison.OrdinalIgnoreCase) && !a.Contains('{') ||
               a.Contains('{') && b.Contains('{') && string.Equals(a[a.LastIndexOf('{')..], b[b.LastIndexOf('{')..], StringComparison.OrdinalIgnoreCase);
    }

    private string DeviceLabel() => reference.Model?.Name ?? T("the device");

    // An input arrived: its part (known, a known part without a number = the selected one, or a new part).
    public void Found(string input)
    {
        CurrentInput = input;
        ReferenceMarker? marker = reference.MarkerOf(input);
        if (marker == null && reference.SelectedMarker != null && reference.Model != null)
        {
            // The user picked a part in the legend that has no number yet: this input is it.
            string direction = DeviceVocabulary.InputKind(input) == "hat" ? DeviceVocabulary.HatDirection(input) : string.Empty;
            string id = reference.SelectedMarker.Id;
            if (reference.AssignInput(reference.SelectedMarker, input, direction))
            {
                definitionChanged = true;
                marker = reference.Markers.FirstOrDefault(m => m.Id == id);
            }
        }
        if (marker == null)
        {
            marker = reference.AddControl(NewControl(input));
            definitionChanged = true;
        }
        if (marker == null)
        {
            Prompt = T("{0} could not be added.", input);
            Step = "Idle";
            IsRunning = false;
            return;
        }
        reference.SelectedMarker = marker;
        SetCurrent(marker);
        Step = "Place";
        Prompt = T("Click where \"{0}\" ({1}) is on the picture. Skip if it is already right.", marker.Name, input);
    }

    // The control for an input the model does not know, in the vocabulary of its kind.
    private RefControl NewControl(string input)
    {
        string kind = reference.Model?.Kind ?? "panel";
        switch (DeviceVocabulary.InputKind(input))
        {
            case "hat":
            {
                int hat = DeviceVocabulary.HatNumber(input);
                // A free hat number of the kind ("stick.hat.3").
                string control = DeviceGeneric.HatControl(kind, hat);
                while (reference.Model != null && reference.Model.AllControls.Any(c => c.Control.StartsWith(control + ".", StringComparison.OrdinalIgnoreCase) && !c.Input.StartsWith("JOY_BTN_POV" + hat, StringComparison.OrdinalIgnoreCase)))
                    control = DeviceGeneric.HatControl(kind, ++hat + 10);
                return new RefControl { Input = input, Control = control + "." + DeviceVocabulary.HatDirection(input), Label = T("Hat {0}", DeviceVocabulary.HatNumber(input)) };
            }
            case "axis":
            {
                string control = DeviceGeneric.AxisControl(kind, input);
                if (reference.Model != null && reference.Model.AllControls.Any(c => string.Equals(c.Control, control, StringComparison.OrdinalIgnoreCase)))
                    control = DeviceGeneric.Family(kind) + ".axis.rotary." + input.Replace("JOY_", string.Empty).ToLowerInvariant();
                return new RefControl { Input = input, Control = control, Type = "analog", Label = T("Axis {0}", input.Replace("JOY_", string.Empty)) };
            }
            default:
            {
                int number = int.TryParse(input.Replace("JOY_BTN", string.Empty), out int n) ? n : 0;
                string control = DeviceGeneric.Family(kind) + ".button." + (100 + number);
                return new RefControl { Input = input, Control = control, Label = T("Button {0}", number) };
            }
        }
    }

    private void SetCurrent(ReferenceMarker? marker)
    {
        if (Current != null)
            Current.IsHighlighted = false;
        Current = marker;
        if (marker != null)
            marker.IsHighlighted = true;
    }

    // The click on the picture (0..1 of the shown picture). True when it placed the current part.
    public bool PlaceAt(double x, double y)
    {
        if (Step != "Place" || Current == null)
            return false;
        PlacePart(Current, x, y);
        if (addingControl)
        {
            // A control added by its kind: done; the next one is added the same way or by pressing.
            addingControl = false;
            SetCurrent(null);
            Step = "Idle";
            IsRunning = false;
            Prompt = T("Placed. Add the next control, press Start for the others, or Finish to save.");
            return true;
        }
        _ = NextAsync(axis: false);
        return true;
    }

    // ------------------------------------------------------------------ "Add control": a part by its kind

    // The kinds of control the user can add (a hat whose directions the device reports as buttons, a switch ...): each
    // with the inputs to press or move in turn.
    public static readonly string[] ControlKinds =
    [
        N("Button"), N("2-way switch"), N("3-way switch"), N("Hat 4-way"), N("Hat 4-way + press"), N("Hat 8-way"),
        N("Mini-stick + press"), N("Encoder"), N("Axis / slider"), N("Wheel (up / down)")
    ];

    public IReadOnlyList<string> ControlKindOptions => ControlKinds;

    [ObservableProperty] private string controlKind = ControlKinds[4];
    [ObservableProperty] private string controlName = string.Empty;
    private bool addingControl;

    // The inputs of a kind, in the order they are asked for ("axis:" = move an axis).
    public static IReadOnlyList<string> SlotsOf(string kind) => kind switch
    {
        "2-way switch" => ["up", "down"],
        "3-way switch" => ["up", "center", "down"],
        "Hat 4-way" => ["up", "right", "down", "left"],
        "Hat 4-way + press" => ["up", "right", "down", "left", "press"],
        "Hat 8-way" => ["up", "upright", "right", "downright", "down", "downleft", "left", "upleft"],
        "Mini-stick + press" => ["axis:x", "axis:y", "press"],
        "Encoder" => ["cw", "ccw", "press"],
        "Axis / slider" => ["axis:"],
        "Wheel (up / down)" => ["up", "down"],
        _ => [""]
    };

    // The part id of a new control of a kind on the shown model: a free number of the kind's family.
    public string NewPartId(string kind)
    {
        string family = DeviceGeneric.Family(reference.Model?.Kind ?? "panel");
        HashSet<string> used = new(reference.Markers.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (kind == "Mini-stick + press")
            return used.Contains(family + ".ministick") ? family + ".ministick2" : family + ".ministick";
        string stem = kind switch
        {
            "2-way switch" => family == "panel" ? "panel.toggle." : family + ".base.toggle.",
            "3-way switch" => family == "panel" ? "panel.toggle3." : family + ".base.toggle.",
            "Hat 4-way" or "Hat 4-way + press" or "Hat 8-way" => family + ".hat.",
            "Encoder" => family == "panel" ? "panel.encoder." : family + ".base.encoder.",
            "Wheel (up / down)" => family + ".rocker.",
            "Axis / slider" => family + ".axis.slider.",
            _ => family + ".button."
        };
        int n = kind == "Button" ? 101 : 1;
        while (used.Contains(stem + n))
            n++;
        return stem + n;
    }

    // The control id of one input of a new part (a hat's direction, a mini-stick's axis).
    public static string ControlOf(string part, string kind, string slot)
    {
        bool axis = slot.StartsWith("axis:", StringComparison.Ordinal);
        string direction = axis ? slot[5..] : slot;
        if (kind == "Mini-stick + press")
        {
            int dot = part.IndexOf('.');
            return axis ? part[..dot] + ".axis." + part[(dot + 1)..] + "." + direction : part + "." + direction;
        }
        return direction.Length == 0 ? part : part + "." + direction;
    }

    // Adds one control of the chosen kind: press (or move) each of its inputs in turn, then click its spot.
    [RelayCommand]
    private async Task AddControlAsync()
    {
        if (reference.Model == null)
        {
            Prompt = T("Pick or connect a device first.");
            return;
        }
        cancel?.Cancel();
        CancellationTokenSource source = new();
        cancel = source;
        IsRunning = true;
        Step = "Input";
        SetCurrent(null);
        string kind = ControlKind;
        string part = NewPartId(kind);
        string name = ControlName.Trim().Length > 0 ? ControlName.Trim() : T(kind);
        List<RefControl> controls = [];
        IReadOnlyList<string> slots = SlotsOf(kind);
        for (int k = 0; k < slots.Count; k++)
        {
            string slot = slots[k];
            bool axis = slot.StartsWith("axis:", StringComparison.Ordinal);
            string direction = axis ? slot[5..] : slot;
            Prompt = axis ? T("\"{0}\": move its axis {1} all the way.", name, direction)
                : direction.Length == 0 ? T("Press \"{0}\" on {1}.", name, DeviceLabel()) : T("\"{0}\": press {1}.", name, T(direction));
            JoystickCaptureService.CaptureResult? result;
            try
            {
                result = await capture.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
                {
                    AxisMode = axis,
                    IncludeKeyboard = false,
                    PreferredDevice = reference.DeviceName,
                    TimeoutMs = TimeoutMs
                }, source.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (source.IsCancellationRequested || cancel != source)
                return;
            if (result == null || string.IsNullOrWhiteSpace(result.Input))
            {
                Step = "Idle";
                IsRunning = false;
                Prompt = T("Nothing was pressed. \"{0}\" is not added.", name);
                return;
            }
            string input = result.Input.Trim().ToUpperInvariant();
            if (controls.Any(c => c.Input == input))
            {
                // The same input twice (the hat was let go too late): ask for this direction again.
                k--;
                continue;
            }
            controls.Add(new RefControl { Input = input, Control = ControlOf(part, kind, slot), Label = name, Type = axis ? "analog" : kind.Contains("switch") ? "latched" : "momentary" });
        }
        foreach (RefControl c in controls)
        {
            // An input the model has already moves to the new control (the user knows his device).
            RefControl? old = reference.Model.AllControls.FirstOrDefault(x => string.Equals(x.Input, c.Input, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                old.Control = c.Control;
                old.Label = c.Label;
            }
            else if (c.Type == "analog")
            {
                reference.Model.Axes.Add(c);
            }
            else
            {
                reference.Model.Controls.Add(c);
            }
        }
        reference.RedrawControls();
        ReferenceMarker? marker = reference.Markers.FirstOrDefault(m => string.Equals(m.Id, part, StringComparison.OrdinalIgnoreCase)) ?? reference.MarkerOf(controls[0].Input);
        definitionChanged = true;
        if (marker == null)
        {
            Prompt = T("{0} could not be added.", name);
            Step = "Idle";
            IsRunning = false;
            return;
        }
        addingControl = true;
        reference.SelectedMarker = marker;
        SetCurrent(marker);
        Step = "Place";
        Prompt = T("Click where \"{0}\" is on the picture.", name);
    }

    // Takes the selected part off the picture (JoyPro placed it wrong and the user does not know where it is).
    [RelayCommand]
    private void RemoveSelected()
    {
        ReferenceMarker? marker = reference.SelectedMarker;
        if (marker == null)
        {
            Prompt = T("Pick a part in the legend or on the picture first.");
            return;
        }
        BeforePlacement?.Invoke(T("remove {0}", marker.Name));
        undo.Push((marker, marker.X, marker.Y, marker.IsPlaced, marker.IsShown));
        reference.RemoveFromPicture(marker);
        RefreshUnplaced();
        Prompt = T("\"{0}\" is off the picture now; place it again with Start or a click after picking it.", marker.Name);
    }

    [RelayCommand]
    private void Skip()
    {
        if (Step == "Place")
            _ = NextAsync(axis: false);
    }

    [RelayCommand]
    private void Undo()
    {
        if (undo.Count == 0)
            return;
        (ReferenceMarker marker, double x, double y, bool wasPlaced, bool wasShown) = undo.Pop();
        marker.X = x;
        marker.Y = y;
        marker.IsPlaced = wasPlaced;
        PlacedCount = Math.Max(0, PlacedCount - 1);
        reference.SelectedMarker = marker;
        reference.MoveMarker(marker, x, y);
        marker.IsPlaced = wasPlaced;
        // A part that had no place before has none again (back among the chips).
        marker.IsShown = wasShown;
        RefreshUnplaced();
    }

    [RelayCommand]
    private void Stop()
    {
        cancel?.Cancel();
        cancel = null;
        SetCurrent(null);
        Step = "Idle";
        IsRunning = false;
        Prompt = T("Stopped. Start goes on; Finish saves.");
    }

    // Saves what the assistant did: the marker positions of the shown picture, and the user's definition when the
    // device was unknown or got new inputs.
    [RelayCommand]
    private void Finish()
    {
        cancel?.Cancel();
        cancel = null;
        SetCurrent(null);
        IsRunning = false;
        Step = "Done";
        if (reference.Model == null)
            return;
        reference.SaveMarkersCommand.Execute(null);
        string text = T("{0} marker(s) saved.", PlacedCount);
        if (definitionChanged || reference.IsGeneric)
        {
            RefDevice? definition = reference.CurrentDefinition();
            if (definition != null)
            {
                string file = DeviceReferenceService.SaveUserDefinition(definition);
                // Positions saved under the definition's id too, so the device shows them next time.
                reference.SavePlacedMarkersFor(definition.Id);
                text += T(" Saved as your device definition ({0}); Export shares it.", System.IO.Path.GetFileName(file));
            }
        }
        Prompt = text;
        reference.Status = text;
    }
}
