using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class DcsFfbDeviceSettingItem : ObservableObject
{
    public DcsFfbDeviceSettingItem(string device)
    {
        Device = (device ?? string.Empty).Trim();
    }

    public string Device { get; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool invertX;

    [ObservableProperty]
    private bool invertY;

    [ObservableProperty]
    private bool swapAxis;

    [ObservableProperty]
    private bool hasSetting;

    [ObservableProperty]
    private bool connected;

    [ObservableProperty]
    private bool profile;
}
