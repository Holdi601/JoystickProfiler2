using System;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// One line of the overlay: what the pressed button does, fading out after a few seconds.
public sealed partial class OverlayLine : ObservableObject
{
    public OverlayLine(string text, string detail, double fontSize, HorizontalAlignment align)
    {
        Text = text;
        Detail = detail;
        FontSize = fontSize;
        DetailFontSize = Math.Max(10, fontSize * 0.6);
        Align = align;
        Shown = DateTime.UtcNow;
    }

    public string Text { get; }

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    public double FontSize { get; }

    public double DetailFontSize { get; }

    public HorizontalAlignment Align { get; }

    public DateTime Shown { get; set; }

    [ObservableProperty]
    private double opacity = 1;
}
