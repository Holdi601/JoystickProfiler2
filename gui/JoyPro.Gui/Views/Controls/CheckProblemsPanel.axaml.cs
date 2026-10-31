using Avalonia.Controls;
using Avalonia.Interactivity;
using JoyPro.Gui.Models;

namespace JoyPro.Gui.Views.Controls;

// The check's problems on the Export page: sections, cards and fixes (MainWindowViewModel.Duplicates.cs,
// MainWindowViewModel.CheckCards.cs).
public partial class CheckProblemsPanel : UserControl
{
    public CheckProblemsPanel()
    {
        InitializeComponent();
    }

    // "Open a relation" of a light card with more than two relations: a menu of its Open buttons.
    private void OpenMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is CheckIssueCard card)
            ShowMenu(button, card.OpenButtons);
    }

    // "More" of a card: a menu of its other fixes, made only when it is opened (a page of cards stays light).
    private void MoreFixesClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is CheckIssueCard card)
            ShowMenu(button, card.MoreFixes);
    }

    private static void ShowMenu(Button button, System.Collections.Generic.IReadOnlyList<CheckFix> fixes)
    {
        if (fixes.Count == 0)
            return;
        MenuFlyout menu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (CheckFix fix in fixes)
        {
            MenuItem item = new() { Header = fix.Text, Command = fix.Command };
            if (fix.Tip.Length > 0)
                ToolTip.SetTip(item, fix.Tip);
            menu.Items.Add(item);
        }
        menu.ShowAt(button);
    }
}
