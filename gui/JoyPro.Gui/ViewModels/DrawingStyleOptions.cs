using System;
using CommunityToolkit.Mvvm.ComponentModel;
using JoyPro.DeviceReference;

namespace JoyPro.Gui.ViewModels;

// The drawing style editor (Settings > Drawings, the export options, the Layout page's toggle): lines or cel-shaded,
// a colour preset and the user's own line, page and leader colours. Each change is stored at once (DeviceReferenceService)
// and reported with Changed, so every picture of JoyPro's drawings repaints.
public sealed partial class DrawingStyleOptions : ObservableObject
{
    public static readonly string[] Modes = ["Lines", "Cel-shaded"];

    public static readonly string[] PresetTitles = ["JoyPro (theme)", "Manual (white on black)", "Blueprint", "Print (black on white)", "Custom"];

    private readonly Func<DrawingStyle> read;
    private readonly Action<DrawingStyle> write;
    private bool loading;

    public DrawingStyleOptions(Func<DrawingStyle> read, Action<DrawingStyle> write)
    {
        this.read = read;
        this.write = write;
        Load();
    }

    public event EventHandler? Changed;

    public string[] ModeChoices => Modes;

    public string[] PresetChoices => PresetTitles;

    [ObservableProperty] private string mode = Modes[1];
    [ObservableProperty] private string preset = PresetTitles[0];
    // #RRGGBB; empty = the preset's colour.
    [ObservableProperty] private string ink = string.Empty;
    [ObservableProperty] private string paper = string.Empty;
    [ObservableProperty] private string accent = string.Empty;

    public bool IsCel
    {
        get => Mode == Modes[1];
        set => Mode = value ? Modes[1] : Modes[0];
    }

    // The colours the style paints with now (light theme), for the swatches next to the boxes.
    public string InkShown => Current.Resolve(false).Ink;

    public string PaperShown => Current.Resolve(false).Paper;

    public string AccentShown => Current.Resolve(false).Accent;

    public DrawingStyle Current => new()
    {
        Mode = Mode == Modes[0] ? DrawingStyle.Lines : DrawingStyle.Cel,
        Preset = DrawingStyle.Presets[Math.Max(0, Array.IndexOf(PresetTitles, Preset))],
        Ink = DrawingStyle.NormalizeColor(Ink),
        Paper = DrawingStyle.NormalizeColor(Paper),
        Accent = DrawingStyle.NormalizeColor(Accent)
    };

    public void Load()
    {
        loading = true;
        try
        {
            DrawingStyle style = read();
            Mode = style.IsCel ? Modes[1] : Modes[0];
            int index = Array.IndexOf(DrawingStyle.Presets, style.Preset);
            Preset = PresetTitles[index < 0 ? 0 : index];
            Ink = style.Ink;
            Paper = style.Paper;
            Accent = style.Accent;
        }
        finally
        {
            loading = false;
        }
        NotifyShown();
    }

    partial void OnModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCel));
        Store();
    }

    partial void OnPresetChanged(string value) => Store();

    partial void OnInkChanged(string value) => StoreColour(value);

    partial void OnPaperChanged(string value) => StoreColour(value);

    partial void OnAccentChanged(string value) => StoreColour(value);

    // A colour box stores only a complete colour (or an empty box = the preset's).
    private void StoreColour(string value)
    {
        if (value.Trim().Length == 0 || DrawingStyle.NormalizeColor(value).Length > 0)
            Store();
    }

    private void Store()
    {
        if (loading)
            return;
        write(Current);
        NotifyShown();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyShown()
    {
        OnPropertyChanged(nameof(InkShown));
        OnPropertyChanged(nameof(PaperShown));
        OnPropertyChanged(nameof(AccentShown));
    }
}
