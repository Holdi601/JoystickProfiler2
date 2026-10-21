using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// A hat's press row that has no input yet ("not mapped - press the hat or drop an input here", docs/device-reference.md
// "Unmapped and bound inputs"): a click on it opens the mapping in the side panel: press the hat on the device, or pick
// one of the bound inputs that have no place on the picture. Saved as the user's definition of the device.
public sealed partial class MainWindowViewModel
{
    // The hat part whose press is being mapped (empty: none).
    [ObservableProperty]
    private string pendingPressPart = string.Empty;

    [ObservableProperty]
    private string pendingPressPrompt = string.Empty;

    private CancellationTokenSource? pressCapture;

    public ObservableCollection<string> PendingPressCandidates { get; } = [];

    public bool HasPendingPress => PendingPressPart.Length > 0;

    public bool HasPendingPressCandidates => PendingPressCandidates.Count > 0;

    partial void OnPendingPressPartChanged(string value) => OnPropertyChanged(nameof(HasPendingPress));

    // A row of the sheet without an input: a hat's unmapped press opens its mapping.
    private void OpenPressMapping(string part)
    {
        if (deviceReference == null || !deviceReference.HasUnmappedPress(part))
            return;
        PendingPressPart = part;
        PendingPressCandidates.Clear();
        foreach (string input in deviceReference.BoundStandIns)
            PendingPressCandidates.Add(input);
        OnPropertyChanged(nameof(HasPendingPressCandidates));
        PendingPressPrompt = T("This hat's press is not mapped yet. Press the hat on your device now, or pick the input it sends:");
        _ = CapturePressAsync(part);
    }

    private async Task CapturePressAsync(string part)
    {
        pressCapture?.Cancel();
        CancellationTokenSource source = new();
        pressCapture = source;
        JoystickCaptureService.CaptureResult? result;
        try
        {
            result = await joystickCaptureService.CaptureNextAsync(new JoystickCaptureService.CaptureOptions
            {
                AxisMode = false,
                IncludeKeyboard = false,
                PreferredDevice = deviceReference?.DeviceName ?? string.Empty,
                TimeoutMs = 30000
            }, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            return;
        }
        if (source.IsCancellationRequested || pressCapture != source || PendingPressPart != part)
            return;
        if (result == null || string.IsNullOrWhiteSpace(result.Input) || DeviceVocabulary.InputKind(result.Input) != "button")
        {
            PendingPressPrompt = T("No button was pressed. Pick the input below, or click the row again to press the hat.");
            return;
        }
        MapPendingPress(result.Input);
    }

    [RelayCommand]
    private void MapPendingPress(string? input)
    {
        if (deviceReference == null || string.IsNullOrWhiteSpace(input) || PendingPressPart.Length == 0)
            return;
        string part = PendingPressPart;
        if (!deviceReference.MapPress(part, input))
        {
            PendingPressPrompt = deviceReference.Status;
            return;
        }
        CancelPendingPress();
        RebuildReferenceLegend();
        RefreshLayoutCallouts();
        LayoutEditorStatus = deviceReference.Status;
    }

    [RelayCommand]
    private void CancelPendingPress()
    {
        pressCapture?.Cancel();
        pressCapture = null;
        PendingPressPart = string.Empty;
        PendingPressCandidates.Clear();
        OnPropertyChanged(nameof(HasPendingPressCandidates));
    }
}
