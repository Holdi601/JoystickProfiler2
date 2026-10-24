using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Services;

namespace JoyPro.Gui.ViewModels;

// "Use my old JoyPro layouts..." on the Layout page (docs/device-reference.md): JoyPro 1 layout folders found on this PC
// are taken over once (copied into JoyPro's data, the old folder is only read); the button picks another folder. The
// layouts then match the connected devices by the device name stored in them, and the Layout page shows them first.
public sealed partial class MainWindowViewModel
{
    private bool oldLayoutsChecked;

    // Found and taken over ("20 layouts from C:\...\JoyPro\LayoutImages"), or a hint.
    [ObservableProperty] private string oldLayoutsStatus = string.Empty;

    // The folder the picker starts in: a JoyPro 1 folder found on this PC.
    [ObservableProperty] private string oldLayoutsSuggestedFolder = string.Empty;

    // Off in tests: no folder of the PC is searched or taken over unless a test asks for it.
    public bool AutoImportOldLayouts { get; set; }

    public bool HasOldLayoutsStatus => OldLayoutsStatus.Length > 0;

    partial void OnOldLayoutsStatusChanged(string value) => OnPropertyChanged(nameof(HasOldLayoutsStatus));

    // Once per session when the Layout page opens: JoyPro 1 layout folders not taken over yet are taken over.
    private async Task CheckOldJoyProLayoutsAsync()
    {
        if (oldLayoutsChecked || !AutoImportOldLayouts)
            return;
        oldLayoutsChecked = true;
        List<string> found = await Task.Run(() => OldJoyProLayouts.Detect());
        OldLayoutsSuggestedFolder = found.FirstOrDefault() ?? string.Empty;
        // Their pictures also give the 2D layouts JoyPro does not ship (button maps of the makers).
        foreach (string folder in found)
            RememberVariantPictureFolder(folder);
        List<string> done = OldJoyProLayouts.ImportedSources();
        List<string> fresh = found.Where(f => !done.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0)
        {
            int have = OldJoyProLayouts.CountLayouts(OldJoyProLayouts.TargetFolder());
            OldLayoutsStatus = have > 0 ? T("Your JoyPro 1 layouts are in use ({0}).", have.ToString(CultureInfo.InvariantCulture)) : string.Empty;
            return;
        }
        int copied = 0;
        foreach (string folder in fresh)
            copied += await Task.Run(() => OldJoyProLayouts.Import(folder));
        OldLayoutsStatus = T("Took over {0} JoyPro 1 layout(s) from {1}.", copied.ToString(CultureInfo.InvariantCulture), string.Join(", ", fresh.Select(ShortFolder)));
    }

    // The folder picked with "Use my old JoyPro layouts..." (a JoyPro 1 folder or its LayoutImages folder).
    public async Task ImportOldJoyProLayoutsAsync(string folder)
    {
        string path = (folder ?? string.Empty).Trim();
        if (path.Length == 0)
            return;
        string withLayouts = OldJoyProLayouts.HasLayouts(path) ? path
            : OldJoyProLayouts.HasLayouts(Path.Combine(path, "LayoutImages")) ? Path.Combine(path, "LayoutImages")
            : string.Empty;
        if (withLayouts.Length == 0)
        {
            OldLayoutsStatus = T("No JoyPro 1 layouts (.layout files) in {0}. Pick the JoyPro 1 folder or its LayoutImages folder.", ShortFolder(path));
            return;
        }
        int copied = await Task.Run(() => OldJoyProLayouts.Import(withLayouts));
        RememberVariantPictureFolder(withLayouts);
        int all = OldJoyProLayouts.CountLayouts(withLayouts);
        OldLayoutsStatus = copied > 0
            ? T("Took over {0} layout(s) from {1}.", copied.ToString(CultureInfo.InvariantCulture), ShortFolder(withLayouts))
            : T("The {0} layout(s) of {1} are already in use.", all.ToString(CultureInfo.InvariantCulture), ShortFolder(withLayouts));
        await SuggestLayoutsAsync();
        if (SelectedLayoutSuggestion?.Device is { Length: > 0 } device)
            await ShowWorkspaceDeviceAsync(device);
    }

    private static string ShortFolder(string folder)
    {
        string[] parts = folder.TrimEnd('\\', '/').Split('\\', '/');
        return parts.Length <= 3 ? folder : "...\\" + string.Join("\\", parts[^3..]);
    }
}
