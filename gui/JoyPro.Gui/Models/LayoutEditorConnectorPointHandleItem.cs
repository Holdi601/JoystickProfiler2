using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

public sealed partial class LayoutEditorConnectorPointHandleItem : ObservableObject
{
    [ObservableProperty]
    private int index;

    [ObservableProperty]
    private double x;

    [ObservableProperty]
    private double y;
}
