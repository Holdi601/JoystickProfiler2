using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Views.Controls;

// Where the legend of a DeviceReferenceView goes.
public enum ReferenceLegendPlacement
{
    Right,
    Bottom,
    None
}

// The reusable device reference: picture + markers + legend (docs/device-reference.md). Put it on a page with a
// DeviceReferenceViewModel as DataContext.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class DeviceReferenceView : UserControl
{
    public static readonly StyledProperty<ReferenceLegendPlacement> LegendPlacementProperty =
        AvaloniaProperty.Register<DeviceReferenceView, ReferenceLegendPlacement>(nameof(LegendPlacement), ReferenceLegendPlacement.Right);

    public static readonly StyledProperty<bool> ShowToolbarProperty =
        AvaloniaProperty.Register<DeviceReferenceView, bool>(nameof(ShowToolbar), true);

    public static readonly StyledProperty<bool> ShowAssistantProperty =
        AvaloniaProperty.Register<DeviceReferenceView, bool>(nameof(ShowAssistant), true);

    public static readonly StyledProperty<bool> ShowBadgesProperty =
        AvaloniaProperty.Register<DeviceReferenceView, bool>(nameof(ShowBadges), true);

    public static readonly StyledProperty<bool> IsLegendEditableProperty =
        AvaloniaProperty.Register<DeviceReferenceView, bool>(nameof(IsLegendEditable), true);

    // The canvas' zoom bar and hint (off for small views and exported pictures).
    public static readonly StyledProperty<bool> ShowCanvasChromeProperty =
        AvaloniaProperty.Register<DeviceReferenceView, bool>(nameof(ShowCanvasChrome), true);

    public static readonly StyledProperty<double> LegendWidthProperty =
        AvaloniaProperty.Register<DeviceReferenceView, double>(nameof(LegendWidth), 400);

    public DeviceReferenceView()
    {
        InitializeComponent();
        ApplyLegendPlacement();
    }

    public ReferenceLegendPlacement LegendPlacement
    {
        get => GetValue(LegendPlacementProperty);
        set => SetValue(LegendPlacementProperty, value);
    }

    public bool ShowToolbar
    {
        get => GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    public bool ShowAssistant
    {
        get => GetValue(ShowAssistantProperty);
        set => SetValue(ShowAssistantProperty, value);
    }

    public bool ShowBadges
    {
        get => GetValue(ShowBadgesProperty);
        set => SetValue(ShowBadgesProperty, value);
    }

    public bool IsLegendEditable
    {
        get => GetValue(IsLegendEditableProperty);
        set => SetValue(IsLegendEditableProperty, value);
    }

    public bool ShowCanvasChrome
    {
        get => GetValue(ShowCanvasChromeProperty);
        set => SetValue(ShowCanvasChromeProperty, value);
    }

    public double LegendWidth
    {
        get => GetValue(LegendWidthProperty);
        set => SetValue(LegendWidthProperty, value);
    }

    // The picture part, for hosts that want PictureClicked or the picture rectangle.
    public DeviceMarkerCanvas PictureCanvas => Canvas;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LegendPlacementProperty || change.Property == LegendWidthProperty)
            ApplyLegendPlacement();
    }

    private void ApplyLegendPlacement()
    {
        if (LegendHost == null)
            return;
        switch (LegendPlacement)
        {
            case ReferenceLegendPlacement.Bottom:
                LegendHost.IsVisible = true;
                Grid.SetRow(LegendHost, 3);
                Grid.SetColumn(LegendHost, 0);
                Grid.SetColumnSpan(LegendHost, 2);
                LegendHost.Width = double.NaN;
                LegendHost.MaxHeight = 320;
                Grid.SetColumnSpan(PictureHost, 2);
                break;
            case ReferenceLegendPlacement.None:
                LegendHost.IsVisible = false;
                Grid.SetColumnSpan(PictureHost, 2);
                break;
            default:
                LegendHost.IsVisible = true;
                Grid.SetRow(LegendHost, 1);
                Grid.SetColumn(LegendHost, 1);
                Grid.SetColumnSpan(LegendHost, 1);
                LegendHost.Width = LegendWidth;
                LegendHost.MaxHeight = double.PositiveInfinity;
                Grid.SetColumnSpan(PictureHost, 1);
                break;
        }
    }

    private DeviceReferenceViewModel? Reference => DataContext as DeviceReferenceViewModel;

    private static readonly FilePickerFileType Pictures = new("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] };
    private static readonly FilePickerFileType Definitions = new("JoyPro device definition") { Patterns = ["*.json"] };

    private async void UserPhotoClicked(object? sender, RoutedEventArgs e)
    {
        if (Reference?.Model == null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Your photo of {0}", Reference.Model.Name),
            AllowMultiple = false,
            FileTypeFilter = [Pictures]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
            await Reference.SetUserPhotoAsync(path);
    }

    private async void ExportClicked(object? sender, RoutedEventArgs e)
    {
        if (Reference?.Model == null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export the device definition"),
            SuggestedFileName = Reference.Model.Id + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [Definitions]
        });
        string? path = file?.TryGetLocalPath();
        if (path != null)
            Reference.ExportDefinition(path);
    }

    private async void ImportClicked(object? sender, RoutedEventArgs e)
    {
        if (Reference == null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Import device definitions"),
            AllowMultiple = false,
            FileTypeFilter = [Definitions]
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null)
            return;
        try
        {
            await Reference.ImportDefinitionsAsync(path);
        }
        catch (Exception ex)
        {
            Reference.Status = T("The file is not a device definition: {0}", ex.Message);
        }
    }

    private void CloseAssistantClicked(object? sender, RoutedEventArgs e)
    {
        Reference?.CloseAssistantCommand.Execute(null);
    }
}
