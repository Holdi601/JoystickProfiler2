using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using JoyPro.Gui.Views.Controls;
using Xunit;

namespace JoyPro.Gui.Tests;

// Every collapsible header opens and closes from anywhere on it, the chevron too (Styles/DesignStyles.axaml for every
// Expander, Styles/SettingsSection.axaml for the Settings sections).
[SupportedOSPlatform("windows")]
public sealed class ExpanderHeaderTests
{
    private static void Lay(Window window)
    {
        Size size = new(600, 300);
        for (int i = 0; i < 4; i++)
        {
            window.Measure(size);
            window.Arrange(new Rect(size));
            AvaloniaRenderThread.Pump();
        }
    }

    [Fact]
    public void ExpanderHeader_TheChevronPassesItsClicksToTheHeadersToggle()
    {
        AvaloniaRenderThread.Run(() =>
        {
            Expander expander = new() { Header = "Recording settings", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Content = new TextBlock { Text = "x" } };
            Window window = new() { Content = new StackPanel { Children = { expander } } };
            Lay(window);
            ToggleButton header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ExpanderHeader");
            Border chevron = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ExpandCollapseChevronBorder");
            Assert.False(chevron.IsHitTestVisible);
            // The header spans the expander: the empty part right of the title is the toggle too.
            Assert.Equal(expander.Bounds.Width, header.Bounds.Width, 1);
            window.Content = null;
        });
    }

    [Fact]
    public void SettingsSectionHeader_ATapOnTheRowToggles_ButNotOnItsButtons()
    {
        AvaloniaRenderThread.Run(() =>
        {
            Button own = new() { Content = "Detect again" };
            SettingsSection section = new() { Title = "Games", HeaderContent = own, Content = new TextBlock { Text = "x" } };
            Window window = new() { Content = section };
            Lay(window);
            Control header = section.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Header");
            Control grip = section.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Grip");
            Assert.True(SettingsSection.HeaderTapToggles(header, header));
            Assert.False(SettingsSection.HeaderTapToggles(own, header));
            Assert.False(SettingsSection.HeaderTapToggles(grip, header));
            // The chevron, the icon and the title are the toggle (one pointer handler on the row, HeaderPointerTests clicks them).
            PathIcon arrow = section.GetVisualDescendants().OfType<PathIcon>().First(p => p.Name == "PART_Closed");
            Assert.True(SettingsSection.HeaderTapToggles(arrow, header));
            Control toggle = section.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Toggle");
            Assert.True(SettingsSection.HeaderTapToggles(toggle, header));
            Assert.False(SettingsSection.HeaderTapToggles(section.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Menu"), header));
            window.Content = null;
        });
    }
}
