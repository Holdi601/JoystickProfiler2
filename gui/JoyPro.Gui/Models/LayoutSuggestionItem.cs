using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace JoyPro.Gui.Models;

public sealed partial class LayoutSuggestionItem : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string device = string.Empty;

    [ObservableProperty]
    private string templateName = string.Empty;

    [ObservableProperty]
    private string layoutPath = string.Empty;

    [ObservableProperty]
    private string backdropPath = string.Empty;

    [ObservableProperty]
    private int score;

    [ObservableProperty]
    private string matchReason = string.Empty;

    [ObservableProperty]
    private string layoutMapSpec = string.Empty;

    [ObservableProperty]
    private int markerCount;

    [ObservableProperty]
    private string source = string.Empty;

    // Vendor and model from VID:PID / HID name, and an image search link for it.
    [ObservableProperty]
    private string model = string.Empty;

    [ObservableProperty]
    private string searchUrl = string.Empty;

    public List<LayoutEditorTableItem> SuggestedTables { get; } = [];

    // The device without its GUID ("Throttle - HOTAS Warthog").
    public string DisplayName => DisplayText.ShortDevice(Device);

    // The full device name ("Left panel - alias of ...").
    public string DeviceTip => DisplayText.DeviceTip(Device);

    public void RefreshName()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DeviceTip));
    }

    public string ModelLine => !string.IsNullOrWhiteSpace(Model) ? Model : !string.IsNullOrWhiteSpace(TemplateName) ? TemplateName : T("Unknown model");

    public bool HasLayout => !string.IsNullOrWhiteSpace(LayoutPath);

    public string MarkerSummary => MarkerCount == 1 ? T("Your layout · 1 marker") : T("Your layout · {0} markers", MarkerCount);

    // A layout file of the user's own (his layout, a JoyPro 1 layout, a picture he chose) rather than a shipped
    // template, JoyPro's drawing or a placeholder the engine picked: the Layout page shows it instead of the reference.
    public bool IsOwnLayout =>
        HasLayout && (Source ?? string.Empty).Trim().ToLowerInvariant() is not ("catalog" or "reference-schematic" or "fallback" or "fallback-placeholder" or "fallback-local-picture");

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(IsOwnLayout));

    partial void OnDeviceChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(ModelLine));

    partial void OnTemplateNameChanged(string value) => OnPropertyChanged(nameof(ModelLine));

    partial void OnLayoutPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasLayout));
        OnPropertyChanged(nameof(IsOwnLayout));
    }

    partial void OnMarkerCountChanged(int value) => OnPropertyChanged(nameof(MarkerSummary));
}
