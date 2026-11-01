using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JoyPro.Gui.Models;

// The HUD page's design options (Look): every option of HudLookSettings as a property the controls bind to. A change
// raises Changed once (not while a whole style is loaded).
public sealed partial class HudLookEditor : ObservableObject
{
    private bool loading;

    public event EventHandler? Changed;

    [ObservableProperty] private string accent = "#38BDF8";
    [ObservableProperty] private string pressed = string.Empty;
    [ObservableProperty] private double opacity = 1;
    [ObservableProperty] private string font = "Default";
    [ObservableProperty] private double textScale = 1;
    [ObservableProperty] private double letterSpacing = 0.6;
    [ObservableProperty] private bool boldTitles = true;
    [ObservableProperty] private string text = "#F1F5F9";
    [ObservableProperty] private string textDim = "#94A3B8";
    [ObservableProperty] private string cardBackground = "#B00B1220";
    [ObservableProperty] private string cardBorder = string.Empty;
    [ObservableProperty] private double cardBorderAlpha = 0.33;
    [ObservableProperty] private double cardBorderThickness = 1;
    [ObservableProperty] private double cardRadius = 16;
    [ObservableProperty] private string cardShadow = "Soft";
    [ObservableProperty] private double glow = 1;
    [ObservableProperty] private string track = "#26FFFFFF";
    [ObservableProperty] private string gimbalShape = "Ring";
    [ObservableProperty] private double gimbalSize = 84;
    [ObservableProperty] private string barShape = "Rounded";
    [ObservableProperty] private double barHeight = 6;
    [ObservableProperty] private string leverShape = "Rounded";
    [ObservableProperty] private double leverWidth = 20;
    [ObservableProperty] private string buttonShape = "Rounded";
    [ObservableProperty] private string chipShape = "Pill";

    // The colour fields take #AARRGGBB; the pressed colour and the card border may be empty (= the accent).
    public string PressedOrAccent
    {
        get => Pressed.Length > 0 ? Pressed : Accent;
        set => Pressed = SameColor(value, Accent) ? string.Empty : value ?? string.Empty;
    }

    public string CardBorderOrAccent
    {
        get => CardBorder.Length > 0 ? CardBorder : Accent;
        set => CardBorder = SameColor(value, Accent) ? string.Empty : value ?? string.Empty;
    }

    private static bool SameColor(string? a, string? b) =>
        Avalonia.Media.Color.TryParse(a ?? string.Empty, out Avalonia.Media.Color x) && Avalonia.Media.Color.TryParse(b ?? string.Empty, out Avalonia.Media.Color y) && x == y;

    public void Load(HudLookSettings settings)
    {
        HudLookSettings s = settings.Normalized();
        loading = true;
        try
        {
            Accent = s.Accent;
            Pressed = s.Pressed;
            Opacity = s.Opacity;
            Font = s.Font;
            TextScale = s.TextScale;
            LetterSpacing = s.LetterSpacing;
            BoldTitles = s.BoldTitles;
            Text = s.Text;
            TextDim = s.TextDim;
            CardBackground = s.CardBackground;
            CardBorder = s.CardBorder;
            CardBorderAlpha = s.CardBorderAlpha;
            CardBorderThickness = s.CardBorderThickness;
            CardRadius = s.CardRadius;
            CardShadow = s.CardShadow;
            Glow = s.Glow;
            Track = s.Track;
            GimbalShape = s.GimbalShape;
            GimbalSize = s.GimbalSize;
            BarShape = s.BarShape;
            BarHeight = s.BarHeight;
            LeverShape = s.LeverShape;
            LeverWidth = s.LeverWidth;
            ButtonShape = s.ButtonShape;
            ChipShape = s.ChipShape;
        }
        finally
        {
            loading = false;
        }
        OnPropertyChanged(nameof(PressedOrAccent));
        OnPropertyChanged(nameof(CardBorderOrAccent));
    }

    public HudLookSettings ToSettings(string name) => new HudLookSettings
    {
        Name = name,
        Accent = Accent,
        Pressed = Pressed,
        Opacity = Opacity,
        Font = Font,
        TextScale = TextScale,
        LetterSpacing = LetterSpacing,
        BoldTitles = BoldTitles,
        Text = Text,
        TextDim = TextDim,
        CardBackground = CardBackground,
        CardBorder = CardBorder,
        CardBorderAlpha = CardBorderAlpha,
        CardBorderThickness = CardBorderThickness,
        CardRadius = CardRadius,
        CardShadow = CardShadow,
        Glow = Glow,
        Track = Track,
        GimbalShape = GimbalShape,
        GimbalSize = GimbalSize,
        BarShape = BarShape,
        BarHeight = BarHeight,
        LeverShape = LeverShape,
        LeverWidth = LeverWidth,
        ButtonShape = ButtonShape,
        ChipShape = ChipShape
    }.Normalized();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Accent) or nameof(Pressed))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(PressedOrAccent)));
        if (e.PropertyName is nameof(Accent) or nameof(CardBorder))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(CardBorderOrAccent)));
        if (!loading && e.PropertyName is not (nameof(PressedOrAccent) or nameof(CardBorderOrAccent)))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
