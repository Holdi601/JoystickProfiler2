using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Which model is this?" again, any time (docs/device-reference.md): the Model menu on a device of the Layout page's
// list reopens the question over the picture, also after an answer, a wrong pick or a weak match ("Placeholder ...
// (match 30%)"). "This is not my device" says the shown model is wrong: the device is asked like an unknown one. The
// answer is kept per device (grips.json) as before; "Undo the last model choice" puts the earlier answer back.
public sealed partial class MainWindowViewModel
{
    // Per device: what the user had said it is before the last change (null = nothing, JoyPro identified it).
    private readonly Dictionary<string, string?> modelChoiceUndo = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool canUndoModelChoice;

    [RelayCommand]
    private Task ChooseAnotherModelAsync(LayoutSuggestionItem? item) => AskModelAgainAsync(item, notMine: false);

    [RelayCommand]
    private Task NotMyDeviceAsync(LayoutSuggestionItem? item) => AskModelAgainAsync(item, notMine: true);

    private async Task AskModelAgainAsync(LayoutSuggestionItem? item, bool notMine)
    {
        item ??= SelectedLayoutSuggestion;
        string device = (item?.Device ?? string.Empty).Trim();
        if (item == null || device.Length == 0)
            return;
        if (!ReferenceEquals(SelectedLayoutSuggestion, item))
            SelectedLayoutSuggestion = item;
        // The question is asked over the reference picture (a layout file of the user's own stays one click away).
        LayoutShowsLayoutFile = false;
        await ShowDeviceReferenceForAsync(device);
        if (!modelChoiceUndo.ContainsKey(device))
            modelChoiceUndo[device] = DeviceReferenceService.ChoiceOf(device);
        CanUndoModelChoice = true;
        if (notMine)
        {
            // The shown model is wrong: from now on the device is shown as its kind until the user says what it is.
            string guess = DeviceReference.DeviceClass.Length > 0 ? DeviceReference.DeviceClass : "panel";
            DeviceReferenceService.SetChoice(device, DeviceReferenceService.ClassChoicePrefix + guess);
            await ShowDeviceReferenceForAsync(device);
        }
        DeviceReference.AskAgain(notMine);
        OnPropertyChanged(nameof(ShowReferenceQuestion));
        StatusText = notMine
            ? T("Find your device in the catalog or pick what kind of device it is. 'Undo the last model choice' in its Model menu puts the old answer back.")
            : T("Pick the model of '{0}'. 'Undo the last model choice' in its Model menu puts the old answer back.", item.DisplayName);
    }

    [RelayCommand]
    private async Task UndoModelChoiceAsync(LayoutSuggestionItem? item)
    {
        item ??= SelectedLayoutSuggestion;
        string device = (item?.Device ?? string.Empty).Trim();
        if (device.Length == 0 || !modelChoiceUndo.TryGetValue(device, out string? before))
        {
            StatusText = T("No model choice to undo for this device.");
            return;
        }
        modelChoiceUndo.Remove(device);
        CanUndoModelChoice = modelChoiceUndo.Count > 0;
        if (string.IsNullOrEmpty(before))
            DeviceReferenceService.ClearChoice(device);
        else
            DeviceReferenceService.SetChoice(device, before);
        await ShowDeviceReferenceForAsync(device);
        OnPropertyChanged(nameof(ShowReferenceQuestion));
        if (lastHudDevices.Count > 0)
            _ = RebuildDeviceCardsAsync();
        StatusText = T("The earlier model choice of '{0}' is back.", item?.DisplayName ?? device);
    }
}
