using Avalonia.Controls;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The Cockpits page; its data context is the cockpit reference's view model (CockpitsViewModel, docs/cockpits.md).
// The manual page view's links (an overview area to its panel) and tooltips (the JoyPro function of a cell) go to it.
public partial class CockpitsView : UserControl
{
    public CockpitsView()
    {
        InitializeComponent();
        PageView.LinkClicked += (_, link) => (DataContext as CockpitsViewModel)?.OpenPage(link);
        PageView.TagText = tag => (DataContext as CockpitsViewModel)?.TagText(tag);
        PageView.Dark = CockpitsViewModel.ThemeIsDark;
        ActualThemeVariantChanged += (_, _) =>
        {
            PageView.Dark = CockpitsViewModel.ThemeIsDark;
            PageView.InvalidateVisual();
        };
    }
}
