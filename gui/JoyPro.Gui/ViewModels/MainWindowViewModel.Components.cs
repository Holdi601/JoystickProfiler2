using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services.Components;

namespace JoyPro.Gui.ViewModels;

// Optional third-party components (docs/components.md): vJoy, HidHide and ViGEmBus, each installable with one click
// (consent tick -> pinned download -> SHA-256 + signature -> official installer, one UAC prompt -> detect again).
// Shown on the Settings "Components" card and inline on the Virtual Joystick page (the same items). The local AI model
// has its own consent and download flow in Settings > Local AI; its card here says whether it is ready and links there.
// Detection is read-only and runs when Settings or the Virtual Joystick page opens, or on "Check again".
public sealed partial class MainWindowViewModel
{
    private readonly ComponentInstallGate componentGate = new();
    private ObservableCollection<ComponentItem>? components;
    private bool componentsChecked;

    public ObservableCollection<ComponentItem> Components => components ??= new ObservableCollection<ComponentItem>(
        ComponentCatalog.All.Select(spec => new ComponentItem(spec, componentGate, OnComponentInstalled)));

    // The ones the Virtual Joystick page needs (today all of them).
    public IReadOnlyList<ComponentItem> VirtualJoystickComponents => Components
        .Where(c => c.Id is ComponentCatalog.VJoyId or ComponentCatalog.HidHideId or ComponentCatalog.ViGEmBusId).ToList();

    public ComponentItem? FindComponent(string id) => Components.FirstOrDefault(c => c.Id == id);

    public string ComponentsSummary
    {
        get
        {
            if (!componentsChecked)
                return T("Not checked yet.");
            List<ComponentItem> missing = Components.Where(c => !c.IsOk).ToList();
            return missing.Count == 0
                ? T("All optional components are installed.")
                : T("Not installed or not running: {0}. You only need them for the features named on each card.", string.Join(", ", missing.Select(c => c.Name)));
        }
    }

    private void OnComponentsStageChanged(UiStage stage)
    {
        if ((stage == UiStage.Settings || stage == UiStage.VirtualJoystick) && !componentsChecked)
            RefreshComponents();
        // The Local AI section and the Components card say whether the AI is set up and on.
        if (stage == UiStage.Settings)
            _ = EnsureLlmStatusAsync();
    }

    [RelayCommand]
    private void RefreshComponents()
    {
        foreach (ComponentItem item in Components.Where(c => !c.IsBusy))
            item.Refresh();
        componentsChecked = true;
        OnPropertyChanged(nameof(ComponentsSummary));
    }

    private void OnComponentInstalled(ComponentItem item, ComponentInstallResult result)
    {
        componentsChecked = true;
        OnPropertyChanged(nameof(ComponentsSummary));
        // The Virtual Joystick page reads vJoy/HidHide/ViGEmBus through the engine: read again after a change.
        if (result.Ok && VirtualJoystickGames.Count > 0 && !IsBusy)
            _ = RefreshVirtualJoystickAsync();
    }

    // The local AI model's own setup (license, size, SHA-256, own llama.cpp) is Settings > Local AI.
    [RelayCommand]
    private Task OpenLlmSetupAsync() => OpenLocalAiSettingsAsync();
}
