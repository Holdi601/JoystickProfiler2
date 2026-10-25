using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

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

    public string ModelLine => !string.IsNullOrWhiteSpace(Model) ? Model : !string.IsNullOrWhiteSpace(TemplateName) ? PictureName : T("Unknown model");

    // The picture in words: a layout file's name ("VPC_MT50CM+mod2") written out ("VPC MT50CM + mod2"); the engine's
    // own names ("JoyPro drawing of ...") stay.
    public string PictureName => ReadablePictureName(TemplateName);

    // How well the picture fits, 0..100 %. The engine's score is a sum of evidence (this device's GUID 140, the
    // product's VID:PID 150, a known name 120, model words up to 60, the vendor 10; at most 200), not a percentage:
    // 100 and more is a sure match (VID:PID or the device itself), so it is shown as 100 %.
    public int MatchPercent => MatchPercentOf(Score);

    public static int MatchPercentOf(int score) => Math.Clamp(score, 0, 100);

    public static string ReadablePictureName(string? name)
    {
        string text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;
        // A path or a file name with its extension: the name only.
        if (text.IndexOfAny(['\\', '/']) >= 0 || Regex.IsMatch(text, @"\.(layout|png|jpe?g|webp|bmp|json)$", RegexOptions.IgnoreCase))
            text = Path.GetFileNameWithoutExtension(text);
        // A name with spaces is already words ("JoyPro drawing of the VKB Gladiator NXT").
        if (text.Contains(' '))
            return text;
        text = Regex.Replace(text, @"[_]+", " ");
        text = Regex.Replace(text, @"\s*\+\s*", " + ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text;
    }

    public bool HasLayout => !string.IsNullOrWhiteSpace(LayoutPath);

    public string MarkerSummary => MarkerCount == 1 ? T("Your layout · 1 marker") : T("Your layout · {0} markers", MarkerCount);

    // The chip's tooltip: what it is, with its whole text (the chip itself may be cut with "...").
    public string OwnLayoutTip => MarkerSummary + Environment.NewLine + T("Your own layout file of this device (markers, tables and lines of the cheat sheets)");

    // A layout file of the user's own (his layout, a JoyPro 1 layout, a picture he chose) rather than a shipped
    // template, JoyPro's drawing or a placeholder the engine picked: the Layout page shows it instead of the reference.
    public bool IsOwnLayout =>
        HasLayout && (Source ?? string.Empty).Trim().ToLowerInvariant() is not ("catalog" or "reference-schematic" or "fallback" or "fallback-placeholder" or "fallback-local-picture");

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(IsOwnLayout));

    partial void OnDeviceChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(ModelLine));

    partial void OnTemplateNameChanged(string value)
    {
        OnPropertyChanged(nameof(ModelLine));
        OnPropertyChanged(nameof(PictureName));
    }

    partial void OnScoreChanged(int value) => OnPropertyChanged(nameof(MatchPercent));

    partial void OnLayoutPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasLayout));
        OnPropertyChanged(nameof(IsOwnLayout));
    }

    partial void OnMarkerCountChanged(int value)
    {
        OnPropertyChanged(nameof(MarkerSummary));
        OnPropertyChanged(nameof(OwnLayoutTip));
    }
}
