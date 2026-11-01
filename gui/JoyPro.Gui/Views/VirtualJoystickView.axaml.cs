using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views;

// The virtual joystick page; its data context is the main window's view model (MainWindowViewModel.VirtualJoystick.cs).
public partial class VirtualJoystickView : UserControl
{
    private static readonly FilePickerFileType GremlinProfiles = new("Joystick Gremlin profile") { Patterns = ["*.xml"] };

    public VirtualJoystickView()
    {
        InitializeComponent();
    }

    // "Import a Gremlin profile...": the file, then the engine's dry run and its report (MainWindowViewModel.VirtualModes.cs).
    private async void ImportGremlinClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Joystick Gremlin profile"),
            AllowMultiple = false,
            FileTypeFilter = [GremlinProfiles]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;
        await vm.PreviewGremlinImportCommand.ExecuteAsync(path);
    }
}
