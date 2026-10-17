using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The game setup window (first start, or Settings > Games > Run setup again). Browse picks folders and files here;
// MainWindowViewModel.ApplyGameSetupAsync writes the choices on Done.
[SupportedOSPlatform("windows")]
public partial class GameSetupWindow : Window
{
    private bool finished;

    public GameSetupWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            // Closed without Done: the first-start setup is not shown again.
            if (!finished && DataContext is MainWindowViewModel vm)
                vm.SkipGameSetup();
        };
    }

    private async void BrowseInstallClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameSetupItem game)
            return;
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Installation folder of {0} (its folder, launcher folder or bin folder)", game.Name),
            AllowMultiple = false
        });
        string? path = folders.Count == 0 ? null : LocalPath(folders[0]);
        if (!string.IsNullOrWhiteSpace(path))
            game.LinkedFolder = GameInstallFolder.Resolve(game.Install.Markers, path);
    }

    // The game's (or its launcher's) .exe instead of a folder: its folder, or the game folder next to it, is linked.
    private async void BrowseInstallExeClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameSetupItem game)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("The .exe of {0} (the game or its launcher)", game.Name),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Programs (*.exe)")) { Patterns = ["*.exe"] }]
        });
        string? path = files.Count == 0 ? null : LocalPath(files[0]);
        if (!string.IsNullOrWhiteSpace(path))
            game.LinkedFolder = GameInstallFolder.Resolve(game.Install.Markers, path);
    }

    private async void BrowseConfigClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GameSetupItem game)
            return;
        string? path;
        if (game.ConfigIsFolder)
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("Where {0} keeps its controls", game.Name),
                AllowMultiple = false
            });
            path = folders.Count == 0 ? null : LocalPath(folders[0]);
        }
        else
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("The file where {0} keeps its controls", game.Name),
                AllowMultiple = false
            });
            path = files.Count == 0 ? null : LocalPath(files[0]);
        }
        if (!string.IsNullOrWhiteSpace(path))
            game.ConfigLink = path;
    }

    private async void DoneClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (await vm.ApplyGameSetupAsync())
        {
            finished = true;
            Close();
        }
    }

    private void LaterClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string? LocalPath(IStorageItem item)
    {
        return item.TryGetLocalPath() ?? (item.Path.IsAbsoluteUri ? item.Path.LocalPath : null);
    }
}
