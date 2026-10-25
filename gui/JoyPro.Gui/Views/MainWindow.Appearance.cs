using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// Settings > Appearance in the window: the UI scale on the whole window (UiScaleHost), Ctrl+Plus / Ctrl+Minus / Ctrl+0
// for the text size, theme files picked for import and export.
public partial class MainWindow
{
    private static readonly FilePickerFileType ThemeFileType = new("JoyPro theme") { Patterns = ["*.json"] };

    private void ObserveAppearance(MainWindowViewModel? old, MainWindowViewModel? vm)
    {
        if (old != null)
            old.PropertyChanged -= OnAppearancePropertyChanged;
        if (vm != null)
            vm.PropertyChanged += OnAppearancePropertyChanged;
        ApplyUiScale();
    }

    private void OnAppearancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.UiScale))
            ApplyUiScale();
    }

    // The UI scale: the whole page laid out in fewer (or more) units and drawn bigger (or smaller), on top of Windows'
    // display scaling. The page reflows (a 200 % UI scale lays out like a window of half the size).
    internal void ApplyUiScale()
    {
        double scale = DataContext is MainWindowViewModel vm ? vm.UiScale : 1.0;
        if (this.FindControl<LayoutTransformControl>("UiScaleHost") is LayoutTransformControl host)
            host.LayoutTransform = System.Math.Abs(scale - 1.0) < 1e-9 ? null : new ScaleTransform(scale, scale);
    }

    // The profile header: its actions get the width the header has minus some room for the name, so at a big text size
    // or UI scale they wrap to a second line instead of running out of the window.
    private void FitProfileHeader()
    {
        ProfileHeaderGrid.SizeChanged += (_, e) =>
        {
            double max = Math.Max(280, e.NewSize.Width - 240);
            if (Math.Abs(ProfileHeaderButtons.MaxWidth - max) > 0.5)
                ProfileHeaderButtons.MaxWidth = max;
        };
    }

    // A row picked in a list scrolls into view inside its list only: at a big text size the page scrolls as a whole
    // (PageScroller), and picking a relation must not scroll the page so that the pane's header goes out of view.
    private void KeepListScrollingInLists()
    {
        PagesHost.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            if (e.TargetObject is Visual target && (target is ListBoxItem || target.FindAncestorOfType<ListBoxItem>() != null))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    // Ctrl+Plus / Ctrl+Minus / Ctrl+0: the text size (Settings > Appearance), as in a browser.
    private static bool HandleTextSizeKey(MainWindowViewModel vm, KeyEventArgs e, bool ctrl)
    {
        if (!ctrl)
            return false;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                vm.IncreaseTextScaleCommand.Execute(null);
                return true;
            case Key.OemMinus or Key.Subtract:
                vm.DecreaseTextScaleCommand.Execute(null);
                return true;
            case Key.D0 or Key.NumPad0:
                vm.ResetTextScaleCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private async void ImportThemeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import a theme"),
            AllowMultiple = false,
            FileTypeFilter = [ThemeFileType]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            vm.ImportTheme(path);
    }

    private async void ExportThemeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasEditingTheme)
            return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export the theme"),
            SuggestedFileName = vm.SuggestedThemeFileName,
            DefaultExtension = "json",
            FileTypeChoices = [ThemeFileType]
        });
        string? path = file?.TryGetLocalPath();
        if (path != null)
            vm.ExportEditingTheme(path);
    }
}
