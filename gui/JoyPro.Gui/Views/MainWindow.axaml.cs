using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace JoyPro.Gui.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private MainWindowViewModel? observedViewModel;
    private readonly WindowNotificationManager notificationManager;
    private string relationNodeSortKey = string.Empty;
    private ListSortDirection relationNodeSortDirection = ListSortDirection.Ascending;
    private LayoutEditorTableItem? layoutEditorDragTable;
    private Point layoutEditorTableDragOffset;
    private LayoutEditorTableItem? layoutEditorResizeTable;
    private string layoutEditorResizeHandle = string.Empty;
    private Point layoutEditorResizeStartPointer;
    private double layoutEditorResizeStartX;
    private double layoutEditorResizeStartY;
    private double layoutEditorResizeStartWidth;
    private double layoutEditorResizeStartHeight;
    private LayoutEditorConnectorItem? layoutEditorDragConnector;
    private int layoutEditorDragConnectorPointIndex = -1;
    private Point layoutEditorConnectorPointDragOffset;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string LayoutFilePath => Path.Combine(JoyPro.Gui.Services.AppInfo.SettingsFolder, "ui-layout.json");

    public MainWindow()
    {
        InitializeComponent();
        VirtualListGrowth.Attach(RelationsListBox);
        VirtualListGrowth.Attach(RelationNodeList, followSelected: false);
        RelationNodeList.AddHandler(DragDrop.DragOverEvent, RelationNodeListDragOver);
        RelationNodeList.AddHandler(DragDrop.DragLeaveEvent, RelationNodeListDragLeave);
        RelationNodeList.AddHandler(DragDrop.DropEvent, RelationNodeListDrop);
        // Before the list's own arrow keys (which would move the selection instead).
        RelationNodeList.AddHandler(KeyDownEvent, RelationNodeListKeyDown, RoutingStrategies.Tunnel);
        AttachNavPagesDrag();
        notificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 4
        };
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        DataContextChanged += OnDataContextChanged;
        // Follow input must not jump the bind list while the user types in a box.
        AddHandler(GotFocusEvent, (_, _) => UpdateTextInputFocus(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(UpdateTextInputFocus), RoutingStrategies.Bubble, handledEventsToo: true);
        tooltipHold = new TooltipHold(this);
        // Follow input acts only while JoyPro is the active window (typing in another program must not jump the list).
        Activated += (_, _) => SetViewModelActive(true);
        Deactivated += (_, _) => SetViewModelActive(false);
    }

    private void SetViewModelActive(bool active)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsMainWindowActive = active;
    }

    private bool TextBoxHasFocus() => FocusManager?.GetFocusedElement() is TextBox;

    // Follow input moved the list under a resting mouse (or the window was in the background): no tooltip pops up
    // until the mouse really moves.
    private readonly TooltipHold tooltipHold;

    internal TooltipHold TooltipHold => tooltipHold;

    private void OnFollowInputActivity(object? sender, EventArgs e) => tooltipHold.Hold();

    private void UpdateTextInputFocus()
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsTextInputFocused = TextBoxHasFocus();
    }

    private async void OpenProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Open JoyPro Profile"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("JoyPro Profile"))
                {
                    Patterns = ["*.pr0file"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        string? path = GetLocalPath(files[0]);
        if (string.IsNullOrWhiteSpace(path))
            return;

        // The open profile's pending edits are saved before another one opens.
        await vm.FlushAutosaveAsync();
        vm.ProfilePath = path;
        await vm.LoadProfileCommand.ExecuteAsync(null);
    }

    private static readonly FilePickerFileType ProfileFileType = new("JoyPro Profile") { Patterns = ["*.pr0file"] };

    // More > Save as...: a copy under another name, JoyPro works on the copy from then on.
    private async void SaveProfileAsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasProfile)
            return;
        string? path = await PickProfileTargetAsync(T("Save a copy of {0}", vm.ProfileDisplayName), vm.ProfileDisplayName + " copy.pr0file", vm.ProfilePath);
        if (!string.IsNullOrWhiteSpace(path))
            await vm.SaveProfileAsAsync(path);
    }

    // More > Rename...: the profile file gets another name; its history goes along.
    private async void RenameProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasProfile)
            return;
        string? path = await PickProfileTargetAsync(T("Rename {0}", vm.ProfileDisplayName), vm.ProfileDisplayName + ".pr0file", vm.ProfilePath);
        if (!string.IsNullOrWhiteSpace(path))
            await vm.RenameProfileAsync(path);
    }

    private async Task<string?> PickProfileTargetAsync(string title, string suggestedName, string currentPath)
    {
        IStorageFolder? folder = null;
        string? directory = Path.GetDirectoryName(currentPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            folder = await StorageProvider.TryGetFolderFromPathAsync(directory);
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = folder,
            DefaultExtension = "pr0file",
            FileTypeChoices = [ProfileFileType]
        });
        return file == null ? null : GetLocalPath(file);
    }

    // "Add another profile...": pick a .pr0file and merge it into the open profile (the view model asks first and
    // keeps a copy of the profile).
    private async void MergeProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (!vm.HasProfile)
        {
            vm.StatusText = T("Open or create your profile first; then add another one to it.");
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Profile to add to {0}", vm.ProfileDisplayName),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("JoyPro Profile")) { Patterns = ["*.pr0file"] }]
        });
        string? path = files.Count == 0 ? null : GetLocalPath(files[0]);
        if (string.IsNullOrWhiteSpace(path))
            return;

        vm.MergeRightProfilePath = path;
        await vm.MergeProfileCommand.ExecuteAsync(null);
    }

    // Group by description > Local AI model: the user's own llama-server.exe and GGUF file.
    private async void BrowseLlmServerClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        string? path = await PickSingleFileAsync("Choose llama-server.exe", "llama.cpp server", "llama-server.exe");
        if (!string.IsNullOrWhiteSpace(path))
            vm.LlmLocalServerPath = path;
    }

    private async void BrowseLlmModelClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        string? path = await PickSingleFileAsync(T("Choose a GGUF model"), T("GGUF model"), "*.gguf");
        if (!string.IsNullOrWhiteSpace(path))
            vm.LlmLocalModelPath = path;
    }

    private async System.Threading.Tasks.Task<string?> PickSingleFileAsync(string title, string typeName, string pattern)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(typeName)
                {
                    Patterns = [pattern]
                }
            ]
        });
        return files.Count == 0 ? null : GetLocalPath(files[0]);
    }

    // "New profile": no dialog, the profile gets a name in Documents\JoyPro2\profiles (More > Rename... gives it yours).
    private async void NewProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        await vm.NewProfileAutoCommand.ExecuteAsync(null);
    }

    // Settings > Games: the user's installation folder of a game.
    private async void ChooseGameInstallFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || (sender as Control)?.DataContext is not GameInstallItem game)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Installation folder of {0}", game.Name),
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        string? path = GetLocalPath(folders[0]);
        if (!string.IsNullOrWhiteSpace(path))
            await vm.SetGameInstallFolderAsync(game, path);
    }

    // Settings > Games: the game's (or its launcher's) .exe; the engine links the game's folder for it.
    private async void ChooseGameInstallExeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || (sender as Control)?.DataContext is not GameInstallItem game)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("The .exe of {0} (the game or its launcher)", game.Name),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Programs (*.exe)")) { Patterns = ["*.exe"] }]
        });
        string? path = files.Count == 0 ? null : GetLocalPath(files[0]);
        if (!string.IsNullOrWhiteSpace(path))
            await vm.SetGameInstallFolderAsync(game, path);
    }

    private async void BrowseLayoutExportFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Select Layout Export Folder"),
            AllowMultiple = false
        });
        if (folders.Count == 0)
            return;

        string? path = GetLocalPath(folders[0]);
        if (string.IsNullOrWhiteSpace(path))
            return;

        vm.LayoutExportPath = path;
    }

    private static readonly FilePickerFileType JsonFileType = new("JSON") { Patterns = ["*.json"] };

    private async Task<string?> PickJsonToOpenAsync(string title)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType]
        });
        return files.Count == 0 ? null : GetLocalPath(files[0]);
    }

    private async Task<string?> PickJsonToSaveAsync(string title, string suggestedName)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType]
        });
        return file == null ? null : GetLocalPath(file);
    }

    private async void ImportRelationsJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || await PickJsonToOpenAsync(T("Import Relations JSON")) is not { Length: > 0 } path)
            return;
        vm.RelationImportPath = path;
        await vm.ImportRelationsCommand.ExecuteAsync(null);
    }

    private async void ExportRelationsJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || await PickJsonToSaveAsync(T("Export Relations JSON"), "relations.json") is not { Length: > 0 } path)
            return;
        vm.RelationExportPath = path;
        await vm.ExportRelationsCommand.ExecuteAsync(null);
    }

    private async void ImportBindsJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || await PickJsonToOpenAsync(T("Import Binds JSON")) is not { Length: > 0 } path)
            return;
        vm.BindsImportPath = path;
        await vm.ImportBindsCommand.ExecuteAsync(null);
    }

    private async void ExportBindsJsonClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || await PickJsonToSaveAsync(T("Export Binds JSON"), "binds.json") is not { Length: > 0 } path)
            return;
        vm.BindsExportPath = path;
        await vm.ExportBindsCommand.ExecuteAsync(null);
    }

    private async void BrowseLayoutPictureClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Picture of the device"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Pictures")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"]
                }
            ]
        });
        if (files.Count == 0)
            return;

        string? path = GetLocalPath(files[0]);
        if (string.IsNullOrWhiteSpace(path))
            return;

        vm.LayoutPictureSource = path;
        await vm.UseLayoutPictureCommand.ExecuteAsync(null);
    }

    private static string? GetLocalPath(IStorageFile file)
    {
        string? localPath = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
            return localPath;

        if (file.Path.IsAbsoluteUri)
            return file.Path.LocalPath;

        return null;
    }

    private static string? GetLocalPath(IStorageFolder folder)
    {
        string? localPath = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
            return localPath;

        if (folder.Path.IsAbsoluteUri)
            return folder.Path.LocalPath;

        return null;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        SetViewModelActive(IsActive);
        if (DataContext is MainWindowViewModel startupVm)
        {
            _ = startupVm.RunStartupChecksAsync();
            // The last profile opens once the window has drawn itself (the busy area shows it), then the history's
            // background clean-up starts.
            Dispatcher.UIThread.Post(() => _ = startupVm.AutoOpenLastProfileOnStartupAsync(), DispatcherPriority.Background);
            startupVm.StartHistoryMaintenance();
        }

        if (ShouldUseDefaultLayout())
        {
            UpdateEngineOutputCompactLayout();
            FitIntoScreen();
            return;
        }

        UiLayoutState? state = LoadLayoutState();
        if (state == null)
        {
            FitIntoScreen();
            return;
        }

        if (state.WindowWidth >= 1000 && state.WindowHeight >= 700)
        {
            Width = state.WindowWidth;
            Height = state.WindowHeight;
        }

        if (state.WindowX.HasValue && state.WindowY.HasValue)
            Position = new PixelPoint((int)state.WindowX.Value, (int)state.WindowY.Value);

        if (state.EngineOutputExpanded.HasValue && EngineOutputExpander != null)
            EngineOutputExpander.IsExpanded = state.EngineOutputExpanded.Value;

        if (state.RelationsLeftPaneWidth >= 220 && state.RelationsLeftPaneWidth <= 900)
            RelationsPaneGrid.ColumnDefinitions[0].Width = new GridLength(state.RelationsLeftPaneWidth);

        if (state.BindsLeftPaneWidth >= 220 && state.BindsLeftPaneWidth <= 900)
            BindsPaneGrid.ColumnDefinitions[0].Width = new GridLength(state.BindsLeftPaneWidth);

        UpdateEngineOutputCompactLayout();
        ScrollEngineOutputToBottom();
        FitIntoScreen();
    }

    // The window (1500 x 950 by default, or its saved size and place) stays on the screen it opens on: on a smaller or
    // portrait screen (1440 x 2560) or after the monitor it was saved on went away it is shrunk and moved into the
    // working area instead of reaching past the screen's edge, where its dialogs' buttons would be cut off.
    private void FitIntoScreen()
    {
        Avalonia.Platform.Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
            return;
        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double maxWidth = area.Width / scale;
        double maxHeight = area.Height / scale;
        if (MinWidth > maxWidth)
            MinWidth = maxWidth;
        if (MinHeight > maxHeight)
            MinHeight = maxHeight;
        if (Width > maxWidth)
            Width = maxWidth;
        if (Height > maxHeight)
            Height = maxHeight;
        int width = (int)(Width * scale);
        int height = (int)(Height * scale);
        int x = Math.Min(Math.Max(Position.X, area.X), Math.Max(area.X, area.Right - width));
        int y = Math.Min(Math.Max(Position.Y, area.Y), Math.Max(area.Y, area.Bottom - height));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
    }

    private bool autosaveFlushedForClose;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Autosave: edits that wait for their 2 s are saved before JoyPro closes (the close is repeated afterwards).
        if (!autosaveFlushedForClose && DataContext is MainWindowViewModel closingVm && closingVm.NeedsAutosaveFlush)
        {
            e.Cancel = true;
            try
            {
                await closingVm.FlushAutosaveAsync();
            }
            catch (Exception)
            {
                // Closing goes on; the error was shown in the top bar.
            }
            autosaveFlushedForClose = true;
            Close();
            return;
        }

        // The overlay window would keep the app alive.
        (DataContext as MainWindowViewModel)?.StopOverlayForShutdown();
        (DataContext as MainWindowViewModel)?.StopGameConfigWatcher();
        (DataContext as MainWindowViewModel)?.StopUsageRecording();
        // JoyPro.Ffb follows the GUI's pid anyway; "stop" ends it at once (killed after 3 s).
        (DataContext as MainWindowViewModel)?.StopFfbTelemetryForShutdown();
        (DataContext as MainWindowViewModel)?.StopTelemetryRecordingForShutdown();
        // JoyPro.VJoy stops (vJoy neutral); a HidHide session is restored by an engine process (one admin prompt).
        (DataContext as MainWindowViewModel)?.StopVirtualJoystickForShutdown();
        if (ShouldUseDefaultLayout())
            return;

        UiLayoutState state = new()
        {
            WindowWidth = Bounds.Width,
            WindowHeight = Bounds.Height,
            WindowX = Position.X,
            WindowY = Position.Y,
            EngineOutputExpanded = EngineOutputExpander?.IsExpanded,
            RelationsLeftPaneWidth = RelationsPaneGrid.ColumnDefinitions[0].Width.Value,
            BindsLeftPaneWidth = BindsPaneGrid.ColumnDefinitions[0].Width.Value
        };
        SaveLayoutState(state);
    }

    // Live views (force feedback editor, inspector) pause while the window is minimised.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            (DataContext as MainWindowViewModel)?.SetMainWindowMinimized(WindowState == WindowState.Minimized);
        else if (change.Property == ClientSizeProperty && EngineOutputExpander?.IsExpanded == true)
            UpdateEngineOutputCompactLayout();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (observedViewModel != null)
        {
            observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            observedViewModel.WarningRequested -= OnWarningRequested;
            observedViewModel.GameChangesOffered -= OnGameChangesOffered;
            observedViewModel.ShutdownRequested -= OnShutdownRequested;
            observedViewModel.HudArrangeRequested -= OnHudArrangeRequested;
            observedViewModel.GameSetupRequested -= OnGameSetupRequested;
            observedViewModel.ReplaceDeviceDialogRequested -= OnReplaceDeviceDialogRequested;
            observedViewModel.SetupWizardRequested -= OnSetupWizardRequested;
            observedViewModel.NewRelationFormRequested -= OnNewRelationFormRequested;
            observedViewModel.AssistantFocusRequested -= OnAssistantFocusRequested;
            observedViewModel.SettingsSectionFocusRequested -= OnSettingsSectionFocusRequested;
            observedViewModel.AssistantMessages.CollectionChanged -= OnAssistantMessagesChanged;
            observedViewModel.FollowInputActivity -= OnFollowInputActivity;
            observedViewModel.FollowInputJumped -= OnFollowInputActivity;
            observedViewModel.MultiViewRedraw -= OnMultiViewRedraw;
            observedViewModel.NavBarChanged -= OnNavBarChanged;
            observedViewModel.TextInputFocusProbe = null;
            observedViewModel.ConfirmHandler = null;
        }

        observedViewModel = DataContext as MainWindowViewModel;
        if (observedViewModel != null)
        {
            observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            observedViewModel.MultiViewRedraw += OnMultiViewRedraw;
            observedViewModel.NavBarChanged += OnNavBarChanged;
            ApplyNavBar();
            observedViewModel.WarningRequested += OnWarningRequested;
            observedViewModel.GameChangesOffered += OnGameChangesOffered;
            observedViewModel.ShutdownRequested += OnShutdownRequested;
            observedViewModel.OverlayPresenter ??= new OverlayPresenter();
            observedViewModel.HudArrangeRequested += OnHudArrangeRequested;
            observedViewModel.GameSetupRequested += OnGameSetupRequested;
            observedViewModel.ReplaceDeviceDialogRequested += OnReplaceDeviceDialogRequested;
            observedViewModel.SetupWizardRequested += OnSetupWizardRequested;
            observedViewModel.NewRelationFormRequested += OnNewRelationFormRequested;
            observedViewModel.AssistantFocusRequested += OnAssistantFocusRequested;
            observedViewModel.SettingsSectionFocusRequested += OnSettingsSectionFocusRequested;
            observedViewModel.AssistantMessages.CollectionChanged += OnAssistantMessagesChanged;
            observedViewModel.FollowInputActivity += OnFollowInputActivity;
            // Every jump arms the tooltip hold (again), whatever input caused it.
            observedViewModel.FollowInputJumped += OnFollowInputActivity;
            observedViewModel.TextInputFocusProbe = TextBoxHasFocus;
            observedViewModel.ConfirmHandler = AskConfirmationAsync;
            // The output panel's place follows the view model (hidden when closed) from the first layout on.
            Dispatcher.UIThread.Post(UpdateEngineOutputCompactLayout);
        }
    }

    // The HUD designer's preview knows the widgets' real sizes (pictures loaded): measure after the next layout pass.
    private void OnHudArrangeRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm && this.FindControl<Views.Controls.HudSurface>("HudDesignerSurface") is { } surface)
            {
                Dictionary<HudWidget, Size> sizes = surface.MeasuredWidgets();
                if (sizes.Count > 0)
                    vm.ArrangeHudWidgets(sizes);
            }
        }, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.OutputText))
        {
            ScrollEngineOutputToBottom();
            return;
        }

        // The "Output" button of the bottom bar opens the panel while it is hidden.
        if (e.PropertyName == nameof(MainWindowViewModel.EngineOutputExpanded))
        {
            Dispatcher.UIThread.Post(UpdateEngineOutputCompactLayout);
            return;
        }

    }

    private void ScrollEngineOutputToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (EngineOutputTextBox == null)
                return;

            int length = EngineOutputTextBox.Text?.Length ?? 0;
            EngineOutputTextBox.CaretIndex = length;
        });
    }

    // The game setup (first start, or Settings > Games > Run setup again) as a dialog over the main window.
    private GameSetupWindow? gameSetupWindow;

    private async void OnGameSetupRequested()
    {
        if (gameSetupWindow != null || DataContext is not MainWindowViewModel vm)
            return;
        gameSetupWindow = new GameSetupWindow { DataContext = vm };
        try
        {
            await gameSetupWindow.ShowDialog(this);
        }
        finally
        {
            gameSetupWindow = null;
        }
    }

    private ReplaceDeviceWindow? replaceDeviceWindow;

    private async void OnReplaceDeviceDialogRequested()
    {
        if (replaceDeviceWindow != null || DataContext is not MainWindowViewModel vm)
            return;
        replaceDeviceWindow = new ReplaceDeviceWindow { DataContext = vm };
        try
        {
            await replaceDeviceWindow.ShowDialog(this);
        }
        finally
        {
            replaceDeviceWindow = null;
        }
    }

    private SetupWizardWindow? setupWizardWindow;

    // The Setup wizard (docs/setup-wizard.md): a window of its own; the main window stays usable behind it.
    private void OnSetupWizardRequested()
    {
        if (DataContext is not MainWindowViewModel vm || vm.SetupWizard == null)
            return;
        if (setupWizardWindow != null)
        {
            setupWizardWindow.Activate();
            return;
        }
        setupWizardWindow = new SetupWizardWindow { DataContext = vm.SetupWizard };
        setupWizardWindow.Closed += (_, _) => setupWizardWindow = null;
        setupWizardWindow.Show(this);
    }

    // Yes/no before an action that changes game files or the profile (MainWindowViewModel.ConfirmAsync).
    private async Task<bool> AskConfirmationAsync(ConfirmRequest request)
    {
        if (!IsVisible)
            return true;
        return await new ConfirmWindow(request).ShowDialog<bool>(this);
    }

    // A tab of the Settings page, a link to Settings > Local AI or the version in the corner: once the page is laid out,
    // the section's header goes to the top of the page (its settings below it in view).
    private void OnSettingsSectionFocusRequested(string id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<Views.Controls.SettingsSectionsPanel>("SettingsSectionsHost") is not { } host)
                return;
            Views.Controls.SettingsSection? section = host.Children.OfType<Views.Controls.SettingsSection>()
                .FirstOrDefault(s => string.Equals(s.SectionId, id, StringComparison.OrdinalIgnoreCase));
            if (section == null)
                return;
            if (this.FindControl<ScrollViewer>("SettingsScrollViewer") is { } scroller && section.TranslatePoint(new Point(0, 0), host) is { } top)
            {
                double max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(top.Y, 0, max));
                return;
            }
            section.BringIntoView(new Rect(0, 0, section.Bounds.Width, Math.Min(section.Bounds.Height, 200)));
        }, DispatcherPriority.Background);
    }

    private void OnShutdownRequested()
    {
        Dispatcher.UIThread.Post(Close);
    }

    private void OnWarningRequested(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        notificationManager.Show(new Notification(
            T("JoyPro2 Warning"),
            message,
            NotificationType.Warning,
            TimeSpan.FromSeconds(7)));
    }

    // The config watcher's dialog came up (docs/sync-watcher.md): an information notice (not a warning), and when the
    // window is not the active one, its taskbar button flashes until the user switches to it. The focus is never taken
    // (the user may be in another game already).
    private void OnGameChangesOffered(string title, string message)
    {
        notificationManager.Show(new Notification(title, message, NotificationType.Information, TimeSpan.FromSeconds(10)));
        if (IsActive || !OperatingSystem.IsWindows())
            return;
        IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
            Services.TaskbarFlash.Flash(handle);
    }

    private void RelationsListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not ListBox listBox)
            return;

        if (listBox.SelectedItems == null)
        {
            vm.SetRelationMassSelection(Array.Empty<RelationListItem>());
            return;
        }

        vm.SetRelationMassSelection(listBox.SelectedItems.OfType<RelationListItem>());
    }

    // A click on an action of the open relation (not on its remove button) opens or closes its aircraft.
    private void RelationNodeHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control { DataContext: RelationNodeItem node })
            return;
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, sender)).Any(v => v is Button))
            return;
        vm.ToggleNodeAircraftCommand.Execute(node);
    }

    // ---- The order of the open relation's actions: drag by the grip, drop above or below another card; Alt+Up/Down.

    private const string RelationNodeDragFormat = "joypro-relation-node:";
    private RelationNodeItem? relationNodeDragItem;
    private Point relationNodeDragStart;
    private ListBoxItem? relationNodeDropTarget;

    private void RelationNodeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        relationNodeDragItem = (sender as Control)?.DataContext as RelationNodeItem;
        relationNodeDragStart = e.GetPosition(this);
        // A press on the grip is no click on the card (that opens its aircraft).
        e.Handled = true;
    }

    private async void RelationNodeGripMoved(object? sender, PointerEventArgs e)
    {
        if (relationNodeDragItem == null || DataContext is not MainWindowViewModel vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - relationNodeDragStart.X) + Math.Abs(now.Y - relationNodeDragStart.Y) < 6)
            return;
        int index = vm.RelationNodes.IndexOf(relationNodeDragItem);
        relationNodeDragItem = null;
        if (index < 0)
            return;
#pragma warning disable CS0618 // DataObject / DoDragDrop: the Avalonia 11 drag data API
        DataObject data = new();
        data.Set(DataFormats.Text, RelationNodeDragFormat + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
        ShowRelationNodeDropTarget(null, false);
    }

    // The dragged action's index, or -1 when the drag is something else.
    private static int DraggedRelationNode(DragEventArgs e)
    {
#pragma warning disable CS0618
        string? text = e.Data.Contains(DataFormats.Text) ? e.Data.GetText() : null;
#pragma warning restore CS0618
        return text != null && text.StartsWith(RelationNodeDragFormat, StringComparison.Ordinal) &&
               int.TryParse(text[RelationNodeDragFormat.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index
            : -1;
    }

    // The card under the pointer and whether the drop goes below it (lower half).
    private (ListBoxItem? Item, bool Below) RelationNodeDropPlace(DragEventArgs e)
    {
        ListBoxItem? item = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item == null)
            return (null, false);
        return (item, e.GetPosition(item).Y > item.Bounds.Height / 2);
    }

    private void ShowRelationNodeDropTarget(ListBoxItem? item, bool below)
    {
        if (relationNodeDropTarget != null && !ReferenceEquals(relationNodeDropTarget, item))
        {
            relationNodeDropTarget.Classes.Remove("dropAbove");
            relationNodeDropTarget.Classes.Remove("dropBelow");
        }
        relationNodeDropTarget = item;
        if (item == null)
            return;
        item.Classes.Set("dropAbove", !below);
        item.Classes.Set("dropBelow", below);
    }

    private void RelationNodeListDragOver(object? sender, DragEventArgs e)
    {
        if (DraggedRelationNode(e) < 0)
            return;
        (ListBoxItem? item, bool below) = RelationNodeDropPlace(e);
        ShowRelationNodeDropTarget(item, below);
        e.DragEffects = item == null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void RelationNodeListDragLeave(object? sender, DragEventArgs e) => ShowRelationNodeDropTarget(null, false);

    private async void RelationNodeListDrop(object? sender, DragEventArgs e)
    {
        int from = DraggedRelationNode(e);
        (ListBoxItem? item, bool below) = RelationNodeDropPlace(e);
        ShowRelationNodeDropTarget(null, false);
        if (from < 0 || item?.DataContext is not RelationNodeItem over || DataContext is not MainWindowViewModel vm || from >= vm.RelationNodes.Count)
            return;
        e.Handled = true;
        int target = RelationNodeDropIndex(from, vm.RelationNodes.IndexOf(over), below);
        await vm.MoveRelationNodeAsync(vm.RelationNodes[from], target);
    }

    // Where the dragged action lands (its index after the move) when dropped above or below the card at `over`.
    internal static int RelationNodeDropIndex(int from, int over, bool below)
    {
        if (over < 0)
            return from;
        int place = below ? over + 1 : over;
        return place > from ? place - 1 : place;
    }

    // Alt+Up / Alt+Down: the selected action one place up or down.
    private async void RelationNodeListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !e.KeyModifiers.HasFlag(KeyModifiers.Alt) || vm.SelectedRelationNode == null || e.Source is TextBox)
            return;
        if (e.Key == Key.Up)
        {
            e.Handled = true;
            await vm.MoveRelationNodeUpCommand.ExecuteAsync(vm.SelectedRelationNode);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            await vm.MoveRelationNodeDownCommand.ExecuteAsync(vm.SelectedRelationNode);
        }
    }

    private void ClearRelationSelectionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        RelationsListBox.SelectedItems?.Clear();
        vm.ClearRelationSelectionCommand.Execute(null);
    }

    // ---- Tables and lines (callout sheet): a row picks its input; relations are dragged from the side panel onto rows.

    private void LayoutCalloutRowClicked(object? sender, (string Input, bool Modifier) row)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.SelectCalloutInput(row.Input);
    }

    private void LayoutCalloutRowChangeRequested(object? sender, (string Input, bool Modifier) row)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        vm.SelectCalloutInput(row.Input);
        vm.SetLayoutCanvasModeCommand.Execute("bind");
    }

    private Point calloutDragStart;
    private string? calloutDragRelation;

    private void CalloutRelationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        calloutDragRelation = (sender as Control)?.DataContext as string;
        calloutDragStart = e.GetPosition(this);
    }

    // A row of the bind list: its relation can be dragged onto an input of the picture (Bind on the picture) or onto a
    // row of the Layout page's tables.
    private void BindRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        calloutDragRelation = ((sender as Control)?.DataContext as BindListItem)?.RelationName;
        calloutDragStart = e.GetPosition(this);
    }

    private async void CalloutRelationPointerMoved(object? sender, PointerEventArgs e)
    {
        if (calloutDragRelation == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - calloutDragStart.X) + Math.Abs(now.Y - calloutDragStart.Y) < 6)
            return;
        string relation = calloutDragRelation;
        calloutDragRelation = null;
#pragma warning disable CS0618 // DataObject / DoDragDrop: the Avalonia 11 drag data API
        DataObject data = new();
        data.Set(DataFormats.Text, Controls.CalloutSheetCanvas.RelationDragFormat + ":" + relation);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Link | DragDropEffects.Copy);
#pragma warning restore CS0618
    }

    // A pointer position on the layout picture, in picture pixels (the tables and lines of the canvas overlay).
    private Point LayoutPicturePoint(PointerEventArgs e) =>
        LayoutCanvas == null ? e.GetPosition(this) : LayoutCanvas.ToCanvasPoint(e.GetPosition(LayoutCanvas));

    private void LayoutEditorTablesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
    }

    private void LayoutEditorConnectorsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
    }

    private async void RelationNodeSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        // The relation editor's "Find actions" box: Enter searches at once (typing searches after a short pause).
        e.Handled = true;
        await vm.SearchBuilderActionsCommand.ExecuteAsync(null);
    }

    // The JoyPro assistant's input: Enter sends (Shift+Enter: a new line).
    private async void AssistantInputKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key != Key.Enter && e.Key != Key.Return) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not MainWindowViewModel vm)
            return;
        e.Handled = true;
        await vm.AskAssistantCommand.ExecuteAsync(null);
    }

    // A pick in the function picker closes it (the command does the change).
    private void RelationFunctionPicked(object? sender, RoutedEventArgs e) => this.FindControl<Button>("RelationFunctionChip")?.Flyout?.Hide();

    // The open relation's name, edited in place: Enter renames, Esc puts the old name back, leaving the box renames.
    private async void RelationNameEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelBuilderRenameCommand.Execute(null);
            return;
        }
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;
        e.Handled = true;
        await vm.CommitBuilderRenameCommand.ExecuteAsync(null);
    }

    private async void RelationNameEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.CommitBuilderRenameCommand.ExecuteAsync(null);
    }

    private void RelationNodeSearchResultsSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Column == null)
            return;

        string key = (e.Column.SortMemberPath ?? string.Empty).Trim();
        if (key.Length == 0)
            return;

        ListSortDirection direction;
        if (string.Equals(relationNodeSortKey, key, StringComparison.OrdinalIgnoreCase))
        {
            direction = relationNodeSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            direction = ListSortDirection.Ascending;
        }

        relationNodeSortKey = key;
        relationNodeSortDirection = direction;
        vm.SortRelationNodeSearchResults(key, direction);
    }

    // Del in the relation list: delete the selected relation(s); the view model asks first.
    private async void RelationsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainWindowViewModel vm || e.Source is TextBox)
            return;
        e.Handled = true;
        await vm.DeleteSelectedRelationsCommand.ExecuteAsync(null);
    }

    // Shortcuts of the main pages (TextBoxes keep their own Ctrl+Z / Ctrl+F): Ctrl+F search, Ctrl+Shift+F database
    // search, Ctrl+N new relation, Ctrl+S save, Ctrl+Z undo the last change, F5 refresh, Ctrl+1..4 the main pages.
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm)
            return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (vm.IsLayoutStage && vm.IsDevicePicturesSection && await HandleLayoutKeyAsync(vm, e, ctrl, shift))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.F when ctrl:
                e.Handled = true;
                if (vm.IsRelationsStage && shift)
                {
                    // The relation editor's "Find actions" tab.
                    vm.ShowBuilderFindCommand.Execute(null);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusAndSelect(RelationNodeSearchTextBox), Avalonia.Threading.DispatcherPriority.Loaded);
                }
                else if (vm.IsRelationsStage)
                    FocusAndSelect(RelationSearchBox);
                else if (vm.IsBindsStage)
                    FocusAndSelect(BindSearchBox);
                break;
            case Key.N when ctrl:
                e.Handled = true;
                vm.StartNewRelationCommand.Execute(null);
                break;
            case Key.S when ctrl:
                e.Handled = true;
                await vm.SaveProfileCommand.ExecuteAsync(null);
                break;
            case Key.Z when ctrl && !shift:
                e.Handled = true;
                await vm.UndoLastChangeCommand.ExecuteAsync(null);
                break;
            case Key.Y when ctrl:
            case Key.Z when ctrl && shift:
                e.Handled = true;
                await vm.RedoLastChangeCommand.ExecuteAsync(null);
                break;
            case Key.F5:
                e.Handled = true;
                if (vm.IsRelationsStage)
                    await vm.RefreshRelationsCommand.ExecuteAsync(null);
                else if (vm.IsBindsStage)
                    await vm.RefreshBindsCommand.ExecuteAsync(null);
                else if (vm.IsModifiersStage)
                    await vm.RefreshModifiersCommand.ExecuteAsync(null);
                else if (vm.IsStartStage)
                    await vm.RefreshDashboardCommand.ExecuteAsync(null);
                break;
            case Key.D1 when ctrl:
                e.Handled = true;
                vm.GoStartCommand.Execute(null);
                break;
            case Key.D2 when ctrl:
                e.Handled = true;
                vm.GoRelationsCommand.Execute(null);
                break;
            case Key.D3 when ctrl:
                e.Handled = true;
                vm.GoBindsCommand.Execute(null);
                break;
            case Key.D4 when ctrl:
                e.Handled = true;
                vm.GoModifiersCommand.Execute(null);
                break;
        }
    }

    // Layout page (Device pictures): V / E / B the modes, N "place marker", Del removes the selected marker, Ctrl+Z /
    // Ctrl+Y (Ctrl+Shift+Z) undo and redo marker edits, Ctrl+S saves the layout while it has unsaved changes. Typing in a
    // text box keeps its keys.
    private async Task<bool> HandleLayoutKeyAsync(MainWindowViewModel vm, KeyEventArgs e, bool ctrl, bool shift)
    {
        if (FocusManager?.GetFocusedElement() is TextBox)
            return false;
        bool plain = !ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.V when plain:
                vm.SetLayoutCanvasModeCommand.Execute("view");
                return true;
            case Key.E when plain:
                vm.SetLayoutCanvasModeCommand.Execute("edit");
                return true;
            case Key.B when plain:
                vm.SetLayoutCanvasModeCommand.Execute("bind");
                return true;
            case Key.N when plain && vm.IsLayoutFileEditMode:
                vm.LayoutPlaceMarkerMode = !vm.LayoutPlaceMarkerMode;
                return true;
            case Key.Delete when vm.IsLayoutFileEditMode && vm.SelectedLayoutEditorMarker != null:
                vm.RemoveSelectedLayoutEditorMarkerCommand.Execute(null);
                return true;
            case Key.Z when ctrl && !shift && vm.CanUndoLayoutEdit:
                vm.UndoLayoutEditCommand.Execute(null);
                return true;
            case Key.Y when ctrl && vm.CanRedoLayoutEdit:
            case Key.Z when ctrl && shift && vm.CanRedoLayoutEdit:
                vm.RedoLayoutEditCommand.Execute(null);
                return true;
            case Key.S when ctrl && vm.WorkspaceHasUnsavedChanges && vm.CanSaveWorkspace:
                vm.SaveWorkspaceCommand.Execute(null);
                await Task.CompletedTask;
                return true;
            default:
                return false;
        }
    }

    private static readonly FilePickerFileType ReferencePictures = new("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] };
    private static readonly FilePickerFileType ReferenceDefinitions = new("JoyPro device definition") { Patterns = ["*.json"] };

    // The device reference of the Layout page: your photo of the device, export and import of device definitions.
    private async void ReferencePhotoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.DeviceReference.Model == null)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Your photo of {0}", vm.DeviceReference.Model.Name),
            AllowMultiple = false,
            FileTypeFilter = [ReferencePictures]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            await vm.DropWorkspacePhotoCommand.ExecuteAsync(path);
    }

    // Several views on one page: an own image as a view, a view moved or scaled, the views drawn again.
    private async void MultiViewImageClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("An image of your own for the page"),
            AllowMultiple = false,
            FileTypeFilter = [ReferencePictures]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            await vm.AddMultiViewImageAsync(path);
    }

    private void MultiViewItemChanged(object? sender, MultiViewItemModel item)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.MultiViewItemChanged(item);
    }

    private void OnMultiViewRedraw(object? sender, EventArgs e) => this.FindControl<Controls.MultiViewCanvas>("MultiViewCanvas")?.InvalidateVisual();

    // A picture file of the user's own as the shown device's icon.
    private async void DeviceIconFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("A picture for the device's icon"),
            AllowMultiple = false,
            FileTypeFilter = [ReferencePictures]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            await vm.ChooseDeviceIconFileAsync(path);
    }

    private async void ReferenceExportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.DeviceReference.Model == null)
            return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export the device definition"),
            SuggestedFileName = vm.DeviceReference.Model.Id + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [ReferenceDefinitions]
        });
        string? path = file?.TryGetLocalPath();
        if (path != null)
            vm.DeviceReference.ExportDefinition(path);
    }

    private async void ReferenceImportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import device definitions"),
            AllowMultiple = false,
            FileTypeFilter = [ReferenceDefinitions]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null)
            return;
        try
        {
            await vm.DeviceReference.ImportDefinitionsAsync(path);
        }
        catch (Exception ex)
        {
            vm.DeviceReference.Status = T("The file is not a device definition: {0}", ex.Message);
        }
    }

    // The marker positions of the shown picture as a shareable layout (joypro-device-layout).
    private async void ReferenceLayoutExportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.DeviceReference.Model == null)
            return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Share the marker positions of this picture"),
            SuggestedFileName = vm.DeviceReference.Model.Id + ".layout.json",
            DefaultExtension = "json",
            FileTypeChoices = [ReferenceDefinitions]
        });
        string? path = file?.TryGetLocalPath();
        if (path != null)
            vm.DeviceReference.ExportLayout(path);
    }

    private async void ReferenceLayoutImportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import marker positions"),
            AllowMultiple = false,
            FileTypeFilter = [ReferenceDefinitions]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            await vm.DeviceReference.ImportLayoutAsync(path);
    }

    // "Use my old JoyPro layouts...": a JoyPro 1 folder (or its LayoutImages folder); starts in one found on this PC.
    private async void OldLayoutsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        FolderPickerOpenOptions options = new() { Title = T("Your JoyPro 1 folder (or its LayoutImages folder)"), AllowMultiple = false };
        string start = vm.OldLayoutsSuggestedFolder;
        if (start.Length == 0)
            start = (await System.Threading.Tasks.Task.Run(() => JoyPro.Gui.Services.OldJoyProLayouts.Detect())).FirstOrDefault() ?? string.Empty;
        if (start.Length > 0 && System.IO.Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count == 0)
            return;
        string? path = GetLocalPath(folders[0]);
        if (!string.IsNullOrWhiteSpace(path))
            await vm.ImportOldJoyProLayoutsAsync(path);
    }

    private static void FocusAndSelect(TextBox? box)
    {
        if (box == null)
            return;
        box.Focus();
        box.SelectAll();
    }

    // "Add relation" opened the form: the name box gets the focus.
    private void OnNewRelationFormRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => FocusAndSelect(NewRelationNameBox), DispatcherPriority.Loaded);
    }

    // "Expand with AI" or an example: the assistant's input box gets the focus (the cursor at the end).
    private void OnAssistantFocusRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AssistantInputBox.Focus();
            AssistantInputBox.CaretIndex = AssistantInputBox.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    // A new message: the conversation scrolls to it.
    private void OnAssistantMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => AssistantScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void LayoutEditorConnectorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            control.DataContext is not LayoutEditorConnectorItem connector)
            return;

        vm.SelectLayoutEditorConnector(connector);
        e.Handled = true;
    }

    private void LayoutEditorConnectorPointPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            control.DataContext is not LayoutEditorConnectorPointHandleItem handle)
            return;

        LayoutEditorConnectorItem? connector = ResolveLayoutEditorConnectorForHandle(vm, handle);
        if (connector == null)
            return;

        vm.SelectLayoutEditorConnector(connector);
        Point pointer = LayoutPicturePoint(e);
        layoutEditorDragConnector = connector;
        layoutEditorDragConnectorPointIndex = handle.Index;
        layoutEditorConnectorPointDragOffset = new Point(pointer.X - handle.X, pointer.Y - handle.Y);
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void LayoutEditorConnectorPointPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            layoutEditorDragConnector == null ||
            layoutEditorDragConnectorPointIndex < 0)
            return;

        Point pointer = LayoutPicturePoint(e);
        vm.MoveLayoutEditorConnectorPoint(
            layoutEditorDragConnector,
            layoutEditorDragConnectorPointIndex,
            pointer.X - layoutEditorConnectorPointDragOffset.X,
            pointer.Y - layoutEditorConnectorPointDragOffset.Y);
        e.Handled = true;
    }

    private void LayoutEditorConnectorPointPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        layoutEditorDragConnector = null;
        layoutEditorDragConnectorPointIndex = -1;
        e.Handled = true;
    }

    private static LayoutEditorConnectorItem? ResolveLayoutEditorConnectorForHandle(
        MainWindowViewModel vm,
        LayoutEditorConnectorPointHandleItem handle)
    {
        if (vm.SelectedLayoutEditorConnector != null &&
            vm.SelectedLayoutEditorConnector.PointHandles.Contains(handle))
            return vm.SelectedLayoutEditorConnector;

        foreach (LayoutEditorConnectorItem connector in vm.LayoutEditorConnectors)
        {
            if (connector.PointHandles.Contains(handle))
                return connector;
        }

        return null;
    }

    private void LayoutEditorTablePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            control.DataContext is not LayoutEditorTableItem table)
            return;

        vm.SelectLayoutEditorTable(table);
        Point pointer = LayoutPicturePoint(e);
        layoutEditorDragTable = table;
        layoutEditorTableDragOffset = new Point(pointer.X - table.X, pointer.Y - table.Y);
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void LayoutEditorTablePointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            layoutEditorDragTable == null)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        Point pointer = LayoutPicturePoint(e);
        vm.MoveLayoutEditorTable(
            layoutEditorDragTable,
            pointer.X - layoutEditorTableDragOffset.X,
            pointer.Y - layoutEditorTableDragOffset.Y);
        e.Handled = true;
    }

    private void LayoutEditorTablePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        layoutEditorDragTable = null;
        e.Handled = true;
    }

    private void LayoutEditorTableResizePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            control.DataContext is not LayoutEditorTableItem table)
            return;

        string handle = (control.Tag as string ?? string.Empty).Trim().ToLowerInvariant();
        if (handle.Length == 0)
            return;

        vm.SelectLayoutEditorTable(table);
        layoutEditorResizeTable = table;
        layoutEditorResizeHandle = handle;
        layoutEditorResizeStartPointer = LayoutPicturePoint(e);
        layoutEditorResizeStartX = table.X;
        layoutEditorResizeStartY = table.Y;
        layoutEditorResizeStartWidth = table.Width;
        layoutEditorResizeStartHeight = table.Height;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void LayoutEditorTableResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not Control control ||
            layoutEditorResizeTable == null)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        Point pointer = LayoutPicturePoint(e);
        double dx = pointer.X - layoutEditorResizeStartPointer.X;
        double dy = pointer.Y - layoutEditorResizeStartPointer.Y;

        double nextX = layoutEditorResizeStartX;
        double nextY = layoutEditorResizeStartY;
        double nextWidth = layoutEditorResizeStartWidth;
        double nextHeight = layoutEditorResizeStartHeight;

        switch (layoutEditorResizeHandle)
        {
            case "tl":
                nextX = layoutEditorResizeStartX + dx;
                nextY = layoutEditorResizeStartY + dy;
                nextWidth = layoutEditorResizeStartWidth - dx;
                nextHeight = layoutEditorResizeStartHeight - dy;
                break;
            case "tr":
                nextY = layoutEditorResizeStartY + dy;
                nextWidth = layoutEditorResizeStartWidth + dx;
                nextHeight = layoutEditorResizeStartHeight - dy;
                break;
            case "bl":
                nextX = layoutEditorResizeStartX + dx;
                nextWidth = layoutEditorResizeStartWidth - dx;
                nextHeight = layoutEditorResizeStartHeight + dy;
                break;
            default:
                nextWidth = layoutEditorResizeStartWidth + dx;
                nextHeight = layoutEditorResizeStartHeight + dy;
                break;
        }

        vm.ResizeLayoutEditorTable(layoutEditorResizeTable, nextX, nextY, nextWidth, nextHeight);
        e.Handled = true;
    }

    private void LayoutEditorTableResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        layoutEditorResizeTable = null;
        layoutEditorResizeHandle = string.Empty;
        e.Handled = true;
    }

    private void EngineOutputExpanderExpanded(object? sender, RoutedEventArgs e)
    {
        UpdateEngineOutputCompactLayout();
        ScrollEngineOutputToBottom();
    }

    private void EngineOutputExpanderCollapsed(object? sender, RoutedEventArgs e)
    {
        UpdateEngineOutputCompactLayout();
    }

    private void UpdateEngineOutputCompactLayout()
    {
        if (EngineOutputHostGrid == null || EngineOutputExpander == null || EngineOutputBorder == null || RootLayoutGrid == null)
            return;

        if (EngineOutputExpander.IsExpanded)
        {
            RootLayoutGrid.RowDefinitions[2].Height = new GridLength(6, GridUnitType.Pixel);
            RootLayoutGrid.RowDefinitions[3].Height = GridLength.Auto;
            Grid.SetRow(EngineOutputBorder, 3);
            EngineOutputBorder.IsVisible = true;
            // On a small window the panel takes less height, so the bottom bar stays on screen.
            double windowHeight = double.IsNaN(Height) || Height <= 0 ? Bounds.Height : Height;
            if (EngineOutputTextBox != null && windowHeight > 0)
                EngineOutputTextBox.Height = Math.Clamp(windowHeight * 0.2, 90, 180);
            EngineOutputBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            EngineOutputBorder.VerticalAlignment = VerticalAlignment.Stretch;
            EngineOutputBorder.Width = double.NaN;
            EngineOutputExpander.HorizontalAlignment = HorizontalAlignment.Stretch;
            EngineOutputExpander.Width = double.NaN;
            if (EngineOutputQuickToggle != null)
                EngineOutputQuickToggle.IsVisible = false;
            return;
        }

        // Collapsed: the panel is not drawn at all (in a 0 px row its header used to paint over the status line of the
        // bottom bar); the bottom bar's "Output" button opens it again.
        RootLayoutGrid.RowDefinitions[2].Height = new GridLength(0, GridUnitType.Pixel);
        RootLayoutGrid.RowDefinitions[3].Height = new GridLength(0, GridUnitType.Pixel);
        Grid.SetRow(EngineOutputBorder, 3);
        EngineOutputBorder.IsVisible = false;
        if (EngineOutputQuickToggle != null)
            EngineOutputQuickToggle.IsVisible = true;
    }

    private static bool ShouldUseDefaultLayout()
    {
        string[] args = Environment.GetCommandLineArgs();
        return args.Any(a =>
            string.Equals(a, "--default-layout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--layout-default", StringComparison.OrdinalIgnoreCase));
    }

    private static UiLayoutState? LoadLayoutState()
    {
        try
        {
            if (!File.Exists(LayoutFilePath))
                return null;
            string json = File.ReadAllText(LayoutFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<UiLayoutState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLayoutState(UiLayoutState state)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LayoutFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(LayoutFilePath, json);
        }
        catch
        {
            // Ignore persistence errors.
        }
    }

    private sealed class UiLayoutState
    {
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public int? WindowX { get; set; }
        public int? WindowY { get; set; }
        public bool? EngineOutputExpanded { get; set; }
        public double RelationsLeftPaneWidth { get; set; }
        public double RelationsGroupPaneHeight { get; set; }
        public double BindsLeftPaneWidth { get; set; }
        public double BindsGroupPaneHeight { get; set; }
        public double RelationNodeBottomPaneHeight { get; set; }
        public bool? RelationDbResultsExpanded { get; set; }
    }
}
