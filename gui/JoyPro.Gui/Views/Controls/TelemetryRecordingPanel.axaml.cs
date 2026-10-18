using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// Settings > Telemetry recording: the folder picker; everything else is bound to the view model.
public partial class TelemetryRecordingPanel : UserControl
{
    public TelemetryRecordingPanel()
    {
        InitializeComponent();
    }

    private async void ChooseFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Folder for the telemetry recordings"),
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;
        string? path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) && folders[0].Path.IsAbsoluteUri && folders[0].Path.IsFile)
            path = folders[0].Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
            vm.SetTelemetryRecordingFolder(path);
    }
}
