using Avalonia.Media;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using Xunit;

namespace JoyPro.Gui.Tests;

// The "{0} column(s)" hint on the layout editor's tables reads in every sheet style preset, on a panel table (its own
// light background, also in the Dark sheet style) and on a list table (the style's table background): 4.5:1.
public sealed class SheetHintContrastTests
{
    [Theory]
    [InlineData("joypro")]
    [InlineData("print")]
    [InlineData("high-contrast")]
    [InlineData("dark-sheet")]
    public void ColumnHint_ReadsOnEveryTable_InEveryPreset(string preset)
    {
        SheetStyle style = SheetStyle.Preset(preset);
        foreach (string mode in new[] { "panel", "list" })
        {
            LayoutEditorTableItem table = new() { Mode = mode, BackgroundColor = "#F6F8FB", BackgroundOpacity = 0.93 };
            table.ApplySheetStyle(style.TableBorder, style.HeaderBackground, style.TableBackground, style.TableText);
            Color bg = ThemeService.Over(Color.Parse(table.DisplayBackground), Colors.White);
            Color fg = ThemeService.Over(Color.Parse(table.DisplayHintText), bg);
            double ratio = ThemeService.Contrast(fg, bg);
            Assert.True(ratio >= 4.5, preset + " " + mode + ": hint " + table.DisplayHintText + " on " + table.DisplayBackground + " = " + ratio.ToString("0.00"));
        }
    }

    [Fact]
    public void ColumnHint_KeepsTheStylesTableText_WhenItReads()
    {
        Assert.Equal("#FF334155", LayoutEditorTableItem.ReadableOn("#FFF6F8FB", "#FF334155"));
        // Light text on a light panel table (the Dark sheet's table text): dark text instead.
        Assert.Equal("#0F172A", LayoutEditorTableItem.ReadableOn("#EDF6F8FB", "#FFE5E7EB"));
        Assert.Equal("#F8FAFC", LayoutEditorTableItem.ReadableOn("#FF0F172A", "#FF1E293B"));
    }
}
