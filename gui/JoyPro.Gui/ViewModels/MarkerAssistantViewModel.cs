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
    private readonly Stack<(ReferenceMarker Marker, double X, double Y, bool WasPlaced)> undo = new();
    private CancellationTokenSource? cancel;
    private bool definitionChanged;

    public MarkerAssistantViewModel(DeviceReferenceViewModel reference, IJoystickCaptureService capture)
    {
        this.reference = reference;
        this.capture = capture;
        reference.Assistant = this;
    }

    // Idle, Input (waiting for a button / hat / axis), Place (waiting for the click on the picture), Done.
    [ObservableProperty] private string step = "Idle";
    [ObservableProperty] private string prompt = "Start the assistant, then press each button of the device and click where it is on the picture.";
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
        undo.Push((Current, Current.X, Current.Y, Current.IsPlaced));
        reference.MoveMarker(Current, x, y);
        PlacedCount++;
        _ = NextAsync(axis: false);
        return true;
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
        (ReferenceMarker marker, double x, double y, bool wasPlaced) = undo.Pop();
        marker.X = x;
        marker.Y = y;
        marker.IsPlaced = wasPlaced;
        PlacedCount = Math.Max(0, PlacedCount - 1);
        reference.SelectedMarker = marker;
        reference.MoveMarker(marker, x, y);
        marker.IsPlaced = wasPlaced;
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
