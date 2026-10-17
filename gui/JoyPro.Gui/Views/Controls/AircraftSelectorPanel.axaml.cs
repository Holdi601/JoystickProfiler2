using Avalonia.Controls;
using Avalonia.Input;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The aircraft of one id with a tick each (Models/RelationBuilderItems.cs AircraftSelector), in the Add actions rows and
// in the actions of the open relation. Shift+click ticks or unticks every aircraft between the last clicked one and this.
public partial class AircraftSelectorPanel : UserControl
{
    public AircraftSelectorPanel()
    {
        InitializeComponent();
    }

    private void AircraftChoiceTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: AircraftChoiceItem item } && DataContext is AircraftSelector selector)
            selector.Clicked(item, (e.KeyModifiers & KeyModifiers.Shift) != 0);
    }
}
