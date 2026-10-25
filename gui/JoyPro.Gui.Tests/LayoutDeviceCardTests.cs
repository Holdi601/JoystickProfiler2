using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Layout page's device list with cards as the user's profile shows them (his own layouts of many devices, long names,
// "Your layout · 135 markers"): the chip, "Model..." and the swap button never overlap, at 1280 and 2267 px, at 100 %
// and 150 % Windows scaling; the model menu opens beside the card (over the picture), never over the list; the header
// line names the picture in words with a match of at most 100 %.
// JOYPRO_LAYOUT_CARDS_OUT=<folder> keeps the renders.
[SupportedOSPlatform("windows")]
public sealed class LayoutDeviceCardTests
{
    [Theory]
    [InlineData("VPC_MT50CM+mod2", "VPC MT50CM + mod2")]
    [InlineData("C:/Layouts/WinWing_Orion2_F15EX.layout", "WinWing Orion2 F15EX")]
    [InlineData("JoyPro drawing of VKB Gladiator NXT EVO", "JoyPro drawing of VKB Gladiator NXT EVO")]
    [InlineData("", "")]
    public void PictureName_IsWords(string template, string expected)
    {
        Assert.Equal(expected, LayoutSuggestionItem.ReadablePictureName(template));
    }

    [Theory]
    [InlineData(190, 100)]
    [InlineData(200, 100)]
    [InlineData(100, 100)]
    [InlineData(70, 70)]
    [InlineData(-5, 0)]
    public void MatchPercent_IsAtMost100(int score, int expected)
    {
        Assert.Equal(expected, LayoutSuggestionItem.MatchPercentOf(score));
        Assert.Equal(expected, new LayoutSuggestionItem { Score = score }.MatchPercent);
    }

    [Fact]
    public void DeviceCards_NeverOverlap_AndTheModelMenuOpensBesideTheList()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_LAYOUT_CARDS_OUT");
        if (!string.IsNullOrWhiteSpace(outDir))
            Directory.CreateDirectory(outDir);
        List<string> problems = [];
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                vm.CurrentStage = UiStage.Layout;
                for (int i = 0; i < 100 && vm.IsBusy; i++)
                {
                    AvaloniaRenderThread.Pump();
                    Thread.Sleep(20);
                }
                QualityGateLayoutTests.OwnLayoutCards(vm);
                string status = vm.LayoutSuggestStatus ?? string.Empty;
                Assert.Contains("VPC MT50CM + mod2", status);
                Assert.Contains("100%", status);
                Assert.DoesNotContain("190", status);

                foreach ((string theme, ThemeVariant variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((double width, double height, string label) in new[] { (1280.0, 800.0, "1280"), (2267.0, 1250.0, "2267"), (1200.0, 760.0, "1280@150%"), (1511.0, 833.0, "2267@150%") })
                        CheckCards(vm, width, height, theme + " " + label, problems, outDir);
                }
            }
            finally
            {
                vm.StopGameConfigWatcher();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    internal static void CheckCards(MainWindowViewModel vm, double width, double height, string label, List<string> problems, string? outDir)
    {
        using HiddenWindow window = GateRender.NewWindow(vm, width, height);
        window.Settle();
        Control root = GateRender.Root(window);
        ListBox? list = root.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => ReferenceEquals(l.ItemsSource, vm.LayoutSuggestions));
        if (list == null)
        {
            problems.Add(label + ": the device list is not on the Layout page");
            return;
        }
        List<ListBoxItem> cards = list.GetVisualDescendants().OfType<ListBoxItem>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0).ToList();
        if (cards.Count < 2)
            problems.Add(label + ": only " + cards.Count + " device cards laid out");
        foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height, list))
            problems.Add(label + ": " + LayoutChecks.Describe(a) + " overlaps " + LayoutChecks.Describe(b) + " at " + ra + " / " + rb);
        // Nothing on the page sticks out of its frame (the "Which device is this?" question over the picture's status row).
        foreach ((Control item, Rect _, Border frame, Rect _) in LayoutChecks.Spills(root, width, height))
            problems.Add(label + ": " + LayoutChecks.Describe(item) + " sticks out of " + GateRender.Describe(frame));
        foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height, list))
            problems.Add(label + ": '" + text.Text + "' cut off (needs " + need.ToString("0") + ", has " + got.ToString("0") + ")");
        // The model menu: beside the card, past the device list's surface (over the picture).
        Border? surface = FindSurface(list);
        foreach (ListBoxItem card in cards.Take(3))
        {
            Button? model = card.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Classes.Contains("layoutModelMenu"));
            if (model == null || FlyoutBase.GetAttachedFlyout(model) is not PopupFlyoutBase flyout)
            {
                problems.Add(label + ": a card has no Model... menu");
                continue;
            }
            if (flyout.Placement != PlacementMode.RightEdgeAlignedTop)
                problems.Add(label + ": the model menu opens " + flyout.Placement + " (over the device list)");
            Rect? cardRect = GateRender.BoundsInWindow(card, root);
            Rect? surfaceRect = surface == null ? null : GateRender.BoundsInWindow(surface, root);
            if (cardRect != null && surfaceRect != null && cardRect.Value.Right + flyout.HorizontalOffset < surfaceRect.Value.Right)
                problems.Add(label + ": the model menu starts at " + (cardRect.Value.Right + flyout.HorizontalOffset).ToString("0") + ", inside the device list (ends at " + surfaceRect.Value.Right.ToString("0") + ")");
        }
        if (!string.IsNullOrWhiteSpace(outDir))
            GateRender.Save(window, width, height, 1, Path.Combine(outDir, "layout-cards-" + label.Replace(' ', '-').Replace('%', 'p').Replace('@', '-') + ".png"));
    }

    private static Border? FindSurface(Control list)
    {
        for (Visual? v = list.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            if (v is Border b && b.Classes.Contains("surface"))
                return b;
        }
        return null;
    }
}
