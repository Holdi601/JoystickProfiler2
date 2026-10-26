using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// Yes/no before an action that changes game files or the profile. A dangerous action shows a warning symbol and its
// button is not the default (Enter does not confirm it); Escape or closing the window cancels.
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    private readonly ConfirmRequest? request;

    public ConfirmWindow(ConfirmRequest request) : this()
    {
        this.request = request;
        DontAskAgainBox.IsVisible = request.DontAskKey.Length > 0;
        Title = T("JoyPro2 - {0}", request.Title);
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        ConfirmButton.Content = request.ConfirmLabel;
        ConfirmButton.IsDefault = !request.IsDanger;
        if (request.SecondaryLabel.Length > 0)
        {
            // The action stays the accent (and default) button; the other way is a plain one.
            SecondaryButton.Content = request.SecondaryLabel;
            SecondaryButton.IsVisible = true;
            SecondaryButton.Classes.Remove("accent");
            Grid.SetRow(ConfirmButtons, 1);
            Grid.SetColumn(ConfirmButtons, 0);
            Grid.SetColumnSpan(ConfirmButtons, 2);
            Width = 600;
        }
        if (request.IsDanger && this.TryFindResource("Icon.Warning", out object? warning) && warning is Geometry geometry)
        {
            QuestionIcon.Data = geometry;
            QuestionIcon.Classes.Remove("info");
            QuestionIcon.Classes.Add("warn");
        }
    }

    private void ConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (request != null)
            request.DontAskAgain = DontAskAgainBox.IsChecked == true;
        Close(true);
    }

    private void SecondaryClicked(object? sender, RoutedEventArgs e)
    {
        if (request != null)
            request.SecondaryChosen = true;
        Close(true);
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
