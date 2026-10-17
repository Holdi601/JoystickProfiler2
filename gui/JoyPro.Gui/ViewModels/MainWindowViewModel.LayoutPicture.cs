using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.ViewModels;

// A picture of the selected device chosen by the user (file or image address): the engine builds a layout on it with
// markers for the device's real buttons and axes (engine layout suggest --backdrop).
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    private string layoutPictureSource = string.Empty;

    [RelayCommand]
    private async Task UseLayoutPictureAsync()
    {
        string device = ResolveLayoutBackdropSearchDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            LayoutSuggestStatus = T("Select the device in the suggestion list first.");
            return;
        }

        string picture = (LayoutPictureSource ?? string.Empty).Trim().Trim('"');
        if (picture.Length == 0)
        {
            LayoutSuggestStatus = T("Enter a picture file or an image address (right-click the image in the browser > Copy image address).");
            return;
        }

        bool apply = !string.IsNullOrWhiteSpace(ProfilePath);
        List<string> args = ["layout", "suggest", "--device", device, "--backdrop", picture, "--apply", apply ? "true" : "false", "--apply-min-score", "55"];
        EngineCommandResult? result = await RunEngineAsync(args, includeProfile: apply, mutatesProfile: apply, busyLabel: N("Building the layout from the picture..."), forceText: false);
        if (result == null || result.ExitCode != 0)
            return;

        if (!TryParseLayoutSuggestions(result.StandardOutput, out List<LayoutSuggestionItem> parsed) || parsed.Count == 0)
        {
            LayoutSuggestStatus = T("The engine returned no layout for the picture.");
            return;
        }

        LayoutSuggestionItem item = parsed[0];
        if (string.IsNullOrWhiteSpace(item.LayoutPath))
        {
            LayoutSuggestStatus = item.MatchReason;
            return;
        }

        LayoutSuggestionItem? existing = LayoutSuggestions.FirstOrDefault(v => string.Equals(v.Device, item.Device, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            LayoutSuggestions[LayoutSuggestions.IndexOf(existing)] = item;
        else
            LayoutSuggestions.Add(item);
        item.IsSelected = true;
        SelectedLayoutSuggestion = item;
        LayoutPictureSource = string.Empty;
        LayoutSuggestStatus = T("Layout built from the picture{0} Drag the markers onto the real buttons and save.", (apply ? T(" and assigned to the device.") : T(". Load a profile to assign it.")));
        await OpenSelectedLayoutInEditorAsync();
    }
}
