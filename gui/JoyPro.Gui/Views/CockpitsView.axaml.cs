using System;
using Avalonia;
using Avalonia.Controls;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The Cockpits page; its data context is the cockpit reference's view model (CockpitsViewModel, docs/cockpits.md).
// The manual page view's links (an overview area to its panel), tooltips (the JoyPro function of a cell) and callout
// clicks (the cockpit guide's control) go to it. The guide sits next to the page on a wide window and above it on a
// narrow one.
public partial class CockpitsView : UserControl
{
    // Below this width of the page column the guide goes above the page.
    public const double StackBelow = 1150;

    public CockpitsView()
    {
        InitializeComponent();
        PageView.LinkClicked += (_, link) => (DataContext as CockpitsViewModel)?.OpenPage(link);
        PageView.TagClicked += (_, tag) => (DataContext as CockpitsViewModel)?.SelectTag(tag);
        PageView.TagText = tag => (DataContext as CockpitsViewModel)?.TagText(tag);
        PageView.Dark = CockpitsViewModel.ThemeIsDark;
        ActualThemeVariantChanged += (_, _) =>
        {
            PageView.Dark = CockpitsViewModel.ThemeIsDark;
            PageView.InvalidateVisual();
        };
        GuideList.LayoutUpdated += (_, _) =>
        {
            // A row to show once the list has its size (a callout was clicked, or the page opened with a row picked).
            if (pendingReveal != null && GuideList.Bounds.Height > 0)
            {
                CockpitGuideRow row = pendingReveal;
                pendingReveal = null;
                GuideList.ScrollIntoView(row);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CockpitsViewModel vm)
            {
                vm.GuideRowRevealRequested -= Reveal;
                vm.GuideRowRevealRequested += Reveal;
                Reveal(vm, vm.SelectedGuideRow);
            }
        };
    }

    private CockpitGuideRow? pendingReveal;

    private void Reveal(object? sender, CockpitGuideRow? row)
    {
        if (row == null)
            return;
        pendingReveal = row;
        GuideList.InvalidateArrange();
    }

    // The guide next to or above the page by the width the page column gets (the picker takes about 345 px).
    protected override Size ArrangeOverride(Size finalSize)
    {
        Arrange(finalSize.Width - 345);
        Size size = base.ArrangeOverride(finalSize);
        if (pendingReveal != null && GuideList.Bounds.Height > 0)
        {
            CockpitGuideRow row = pendingReveal;
            pendingReveal = null;
            GuideList.ScrollIntoView(row);
        }
        return size;
    }

    // Side by side (guide 430 px, the page beside it) or stacked (the guide above the page, shorter).
    private bool stacked;

    private void Arrange(double width)
    {
        bool stack = width > 0 && width < StackBelow;
        if (stack == stacked && GuideSplit.ColumnDefinitions.Count == 2)
            return;
        stacked = stack;
        GuideSplit.ColumnDefinitions = stack ? new ColumnDefinitions("*") : new ColumnDefinitions("430,*");
        Grid.SetColumn(PageView, stack ? 0 : 1);
        Grid.SetRow(PageView, stack ? 1 : 0);
        Grid.SetColumn(GuidePane, 0);
        Grid.SetRow(GuidePane, 0);
        GuideList.Height = stack ? 380 : 720;
    }
}
