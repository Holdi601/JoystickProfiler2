using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// 2D layouts on the Layout page (docs/device-reference.md, "2D layouts"): the picture strip offers the shown model's 2D
// cheat sheets (JoyPro 1's sheets, community sheets with their maker's credit, button maps from the user's JoyPro 1
// folder). Picking one takes it over for the device: it is shown as the device's layout file (the bound commands written
// into its boxes), the cheat sheets print it, and the pick survives a restart. Picking another picture of the strip
// gives the device its earlier layout back.
public sealed partial class MainWindowViewModel
{
    // "Layout sheet: maogowzeg (Discord)" under the picture while a credited 2D layout is shown.
    [ObservableProperty] private string layoutSourceCredit = string.Empty;

    public string LayoutVariantCreditText => LayoutShowsLayoutFile && LayoutSourceCredit.Length > 0 ? T("Layout sheet: {0}", LayoutSourceCredit) : string.Empty;

    public bool HasLayoutVariantCredit => LayoutVariantCreditText.Length > 0;

    partial void OnLayoutSourceCreditChanged(string value) => NotifyLayoutVariantCredit();

    private void NotifyLayoutVariantCredit()
    {
        OnPropertyChanged(nameof(LayoutVariantCreditText));
        OnPropertyChanged(nameof(HasLayoutVariantCredit));
    }

    // The 2D layouts of the shown model, for the picture strip.
    private IEnumerable<WorkspacePictureChoice> VariantPictureChoices()
    {
        string? model = deviceReference?.Model?.Id;
        if (string.IsNullOrWhiteSpace(model) || deviceReference?.IsGeneric == true)
            yield break;
        foreach (LayoutVariant variant in LayoutVariantStore.For(model))
            yield return new WorkspacePictureChoice { Title = LayoutVariantStore.TitleOf(variant), Variant = variant };
    }

    // The device's pick when its layout file is shown (the strip marks that 2D layout).
    private string ShownVariantId()
    {
        string device = ResolveLayoutEditorContextDevice();
        if (!LayoutShowsLayoutFile || device.Length == 0)
            return string.Empty;
        LayoutVariantStore.Choice? choice = LayoutVariantStore.ChoiceOf(device);
        return choice != null && string.Equals(NormalizeUiPath(choice.Layout), NormalizeUiPath(LayoutEditorSourcePath), StringComparison.OrdinalIgnoreCase) ? choice.Variant : string.Empty;
    }

    // The layout file shown is a 2D layout the user picked (not his own layout).
    private bool ShowsVariantLayout() => ShownVariantId().Length > 0;

    // Picks a 2D layout for the shown device.
    public async Task UseLayoutVariantAsync(LayoutVariant variant)
    {
        string device = ResolveLayoutEditorContextDevice();
        if (device.Length == 0)
        {
            LayoutEditorStatus = T("Select the device in the suggestion list first.");
            return;
        }
        string file;
        try
        {
            file = LayoutVariantStore.TakeOver(variant, ModifierItems.FirstOrDefault()?.Name ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LayoutEditorStatus = ex.Message;
            return;
        }
        LayoutVariantStore.Choice? earlier = LayoutVariantStore.ChoiceOf(device);
        string previous = earlier?.Previous ?? string.Empty;
        if (earlier == null && !string.IsNullOrWhiteSpace(ProfilePath))
            previous = await LayoutImageOfAsync(device, null, busyLabel: N("Loading the device layout..."));
        LayoutVariantStore.SetChoice(device, new LayoutVariantStore.Choice { Variant = variant.Id, Layout = file, Previous = previous });
        if (!string.IsNullOrWhiteSpace(ProfilePath))
            await LayoutImageOfAsync(device, "--set", file, N("Loading the device layout..."));
        await ShowLayoutFileOfDeviceAsync(device, file);
        LayoutEditorStatus = T("{0} is the layout of {1} now; the cheat sheets print it. Another picture of the strip gives the device its earlier layout back.",
            LayoutVariantStore.TitleOf(variant), DeviceDisplayName(device));
        StatusText = LayoutEditorStatus;
    }

    // Shows a layout file of the device (a 2D layout's copy or the user's saved one) on the canvas.
    private async Task ShowLayoutFileOfDeviceAsync(string device, string file)
    {
        savedLayoutDevices.Add(device);
        layoutTemplateByDevice[device] = file;
        LayoutEditorOutputPath = string.Empty;
        LayoutEditorSourcePath = file;
        LayoutShowsLayoutFile = true;
        await LoadLayoutEditorSourceAsync();
        LayoutShowsLayoutFile = true;
        RebuildWorkspacePictures();
    }

    // Another picture of the strip was picked while a 2D layout was the device's layout: the earlier one comes back.
    private async Task ForgetLayoutVariantAsync(string device)
    {
        LayoutVariantStore.Choice? old = LayoutVariantStore.ClearChoice(device);
        if (old == null)
            return;
        savedLayoutDevices.Remove(device);
        layoutTemplateByDevice.Remove(device);
        if (!string.IsNullOrWhiteSpace(ProfilePath))
        {
            if (old.Previous.Length > 0)
                await LayoutImageOfAsync(device, "--set", old.Previous, N("Loading the device layout..."));
            else
                await LayoutImageOfAsync(device, "--clear", busyLabel: N("Loading the device layout..."));
        }
        LayoutSourceCredit = string.Empty;
        RebuildWorkspacePictures();
    }

    // The device's 2D layout pick (its layout file), or null.
    private static string? VariantLayoutOf(string device) => LayoutVariantStore.ChoiceOf(device)?.Layout;

    // The user saved his edits of a 2D layout: the saved file is the device's layout from now on.
    private void OnLayoutFileSaved(string device, string output)
    {
        if (device.Length == 0 || LayoutVariantStore.ChoiceOf(device) == null)
            return;
        LayoutVariantStore.UpdateLayout(device, output);
        if (!string.IsNullOrWhiteSpace(ProfilePath))
            _ = LayoutImageOfAsync(device, "--set", output, N("Loading the device layout..."));
    }

    // Where the user puts community cheat sheets he received (their pictures do not ship): <data>\Layouts\Community.
    public string CommunityLayoutFolder => DeviceCatalog.CommunityLayoutFolder();

    public string CommunityLayoutHint => T("Community cheat sheets you received (for example maogowzeg's WinWing sheets): put the picture files into {0}. JoyPro then offers them as 2D layouts of their devices.", CommunityLayoutFolder);

    [RelayCommand]
    private void OpenCommunityLayoutFolder()
    {
        try
        {
            Directory.CreateDirectory(CommunityLayoutFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LayoutEditorStatus = ex.Message;
            return;
        }
        OpenExternal("explorer.exe", "\"" + CommunityLayoutFolder + "\"");
        // Sheets put there now are offered when the strip is built again (the next device, or this one again).
        RebuildWorkspacePictures();
    }

    // Folders found on this PC with JoyPro 1 pictures (for 2D layouts whose picture JoyPro does not ship).
    private void RememberVariantPictureFolder(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !LayoutVariantStore.ExtraPictureFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            LayoutVariantStore.ExtraPictureFolders.Add(folder);
    }
}
