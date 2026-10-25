using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The relation cards of the Relations page (tester feedback on 2.1.0): the bind shows device, input and every modifier
// ("VPC VMAX Prime Throttle · BTN 1 · IG..." cut the modifiers off), the "N aircraft" chip names its aircraft in a
// tooltip. Rendered at 1280x800 and 2267x1250 (JOYPRO_RELCARD_OUT=<folder> saves the PNGs).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class RelationCardTests
{
    private const string LongThrottle = "VPC VMAX Prime Throttle {00000000-0000-0000-0000-0000000000B1}";
    private const string LongerGrip = "VKB-Sim Gunfighter Modern Combat Grip Pro Twist Left Hand Edition {00000000-0000-0000-0000-0000000000B2}";

    [Fact]
    public void Card_BindParts_AndModifierChips()
    {
        RelationListItem card = new() { Name = "View briefing on/off", Type = "button", Bind = LongThrottle + "::JOY_BTN1", Modifiers = ["IGNITION Shift", "Shift B", "<none>"] };
        Assert.Equal("VPC VMAX Prime Throttle", card.BindDevice);
        Assert.Equal(" · BTN 1", card.BindInputPart);
        Assert.Equal(["IGNITION Shift", "Shift B"], card.ModifierChips);
        Assert.True(card.HasModifierChips);
        Assert.Contains("IGNITION Shift + Shift B", card.BindTooltip);
        Assert.Contains(LongThrottle, card.BindTooltip);

        RelationListItem unbound = new() { Name = "Gear", Bind = "unbound", Modifiers = ["Shift"] };
        Assert.False(unbound.HasModifierChips);
        Assert.Equal(string.Empty, unbound.BindInputPart);
    }

    [Fact]
    public void AircraftTooltip_ReadableNames_CappedBeyondForty()
    {
        IReadOnlyDictionary<string, string> before = RelationListItem.AircraftNames;
        try
        {
            RelationListItem.AircraftNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["FA-18C_hornet"] = "F/A-18C Hornet", ["A-10C_2"] = "A-10C II Tank Killer" };
            RelationListItem card = new() { Name = "Gear", Planes = "3", PlaneIds = ["FA-18C_hornet", "A-10C_2", "F-16C_50"] };
            Assert.Equal("3 aircraft:\nA-10C II Tank Killer, F-16C_50, F/A-18C Hornet", card.PlanesTooltip);

            List<string> many = Enumerable.Range(1, 45).Select(i => "Plane" + i.ToString("D2")).ToList();
            RelationListItem big = new() { Name = "Trim", Planes = "45", PlaneIds = many };
            Assert.EndsWith("Plane40 +5 more", big.PlanesTooltip);
            Assert.DoesNotContain("Plane41", big.PlanesTooltip);

            List<string> forty = many.Take(40).ToList();
            RelationListItem exact = new() { Name = "Trim", Planes = "40", PlaneIds = forty };
            Assert.DoesNotContain("more", exact.PlanesTooltip);

            // A game without aircraft (its id as the one "aircraft") by its name.
            RelationListItem mixed = new() { Name = "Gear", Games = "DCS, IL2Game, WarThunder", Planes = "3", PlaneIds = ["A-10C_2", "IL2Game", "WarThunder"] };
            Assert.Equal("3 aircraft:\nA-10C II Tank Killer, " + string.Join(", ", new[] { DisplayText.Game("IL2Game"), DisplayText.Game("WarThunder") }.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), mixed.PlanesTooltip);
            Assert.DoesNotContain("IL2Game", mixed.PlanesTooltip);

            // Without the list (text answer of an old engine): the count.
            Assert.Equal("2 aircraft", new RelationListItem { Name = "X", Planes = "2" }.PlanesTooltip);
        }
        finally
        {
            RelationListItem.AircraftNames = before;
        }
    }

    [Fact]
    public void RelationsPage_CardsShowInputAndEveryModifier_AtBothWidths()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_RELCARD_OUT");
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new CardEngine(), new FollowInputTests.FollowCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "cards.pr0file")
            };
            AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
            vm.CurrentStage = UiStage.Relations;
            Assert.Contains(vm.Relations, r => r.HasModifierChips);
            RelationListItem many = vm.Relations.Single(r => r.Name == "View Down slow");
            Assert.Equal(45, many.PlaneIds.Count);
            Assert.EndsWith("+5 more", many.PlanesTooltip);

            foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
            {
                // A root that is never shown, with Avalonia's own layout passes (the list realizes what is in view).
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                window.Settle();
                Control page = window.Page;
                ListBox list = page.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "RelationsListBox");
                List<ListBoxItem> items = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsVisible && i.Bounds.Height > 0).ToList();
                Assert.NotEmpty(items);
                int checkedCards = 0;
                foreach (ListBoxItem item in items)
                {
                    if (item.DataContext is not RelationListItem card || !card.IsBound)
                        continue;
                    checkedCards++;
                    double right = item.Bounds.Width;
                    // Every modifier chip and the input part are whole and inside the card.
                    List<TextBlock> texts = item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
                    foreach (string modifier in card.ModifierChips)
                    {
                        TextBlock chip = texts.FirstOrDefault(t => t.Text == modifier) ?? throw new InvalidOperationException(width + ": no chip '" + modifier + "' on " + card.Name);
                        AssertInside(chip, item, right, width + " " + card.Name + " modifier " + modifier);
                    }
                    TextBlock input = texts.First(t => t.Text == card.BindInputPart);
                    AssertInside(input, item, right, width + " " + card.Name + " input");
                    Assert.True(input.Bounds.Width + 0.5 >= input.DesiredSize.Width - input.Margin.Left - input.Margin.Right, width + " " + card.Name + ": the input part is cut");
                    // The device name may shorten, never past the card.
                    TextBlock device = texts.First(t => t.Text == card.BindDevice);
                    AssertInside(device, item, right, width + " " + card.Name + " device");
                    // The aircraft chip's tooltip is the list of names.
                    Border? aircraft = item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("aircraft") && b.IsEffectivelyVisible);
                    if (card.HasPlanes)
                    {
                        Assert.NotNull(aircraft);
                        AssertInside(aircraft!, item, right, width + " " + card.Name + " aircraft chip");
                        Assert.Equal(card.PlanesTooltip, ToolTip.GetTip(aircraft!));
                    }
                }
                Assert.True(checkedCards >= 2, width + ": only " + checkedCards + " bound cards on screen");
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    using RenderTargetBitmap bitmap = window.Render();
                    bitmap.Save(Path.Combine(outDir, "relation-cards-" + width + ".png"));
                    // The aircraft tooltip text as the tooltip wraps it (the theme's tooltips are 320 wide).
                    Window host = new() { Width = 400, Height = 300, Content = new Border { Background = new SolidColorBrush(Color.Parse("#0F172A")), Padding = new Thickness(12), Child = new TextBlock { Text = many.PlanesTooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Foreground = Brushes.White } } };
                    Control tip = (Control)host.Content!;
                    for (int pass = 0; pass < 3; pass++)
                    {
                        host.Measure(new Size(400, 600));
                        host.Arrange(new Rect(new Size(400, 600)));
                        tip.Measure(new Size(400, 600));
                        tip.Arrange(new Rect(tip.DesiredSize));
                        AvaloniaRenderThread.Pump();
                    }
                    using RenderTargetBitmap tipBitmap = new(new PixelSize(Math.Max(1, (int)Math.Ceiling(tip.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(tip.Bounds.Height))));
                    tipBitmap.Render(tip);
                    tipBitmap.Save(Path.Combine(outDir, "aircraft-tooltip.png"));
                }
            }
        });
    }

    private static void AssertInside(Control control, Control card, double right, string what)
    {
        Point? topLeft = control.TranslatePoint(new Point(0, 0), card);
        Assert.NotNull(topLeft);
        Assert.True(topLeft!.Value.X >= -0.5 && topLeft.Value.X + control.Bounds.Width <= right + 0.5,
            what + " is outside the card: x " + topLeft.Value.X.ToString("F1") + " w " + control.Bounds.Width.ToString("F1") + " card " + right.ToString("F1"));
        Assert.True(control.Bounds.Width > 1, what + " has no width");
    }

    // "relations list" (JSON) with long device names, several modifiers and up to 45 aircraft.
    private sealed class CardEngine : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = string.Empty;
            List<string> args = arguments.ToList();
            int i = args.IndexOf("relations");
            if (i >= 0 && i + 1 < args.Count && args[i + 1] == "list")
                output = RelationsJson();
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = output,
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static string RelationsJson()
        {
            StringBuilder sb = new("{\"Relations\":[");
            (string Name, string Device, string Input, string[] Mods, int Planes)[] rows =
            [
                ("UFC Enter Button", "Total Controls MFBB {00000000-0000-0000-0000-0000000000B3}", "JOY_BTN12", [], 8),
                ("View briefing on/off", LongThrottle, "JOY_BTN1", ["IGNITION Shift"], 18),
                ("View Down slow", LongThrottle, "JOY_BTN12", ["IGNITION Shift", "Shift B"], 45),
                ("View Left slow", LongerGrip, "JOY_BTN13", ["Very Long Modifier Name A", "Mod B", "Mod C"], 19),
                ("Wheel Brake Left", "T-Pendular-Rudder {00000000-0000-0000-0000-0000000000B4}", "JOY_Y", [], 5),
            ];
            sb.Append(string.Join(",", rows.Select(r =>
                "{\"Name\":\"" + r.Name + "\",\"IsAxis\":" + (r.Input == "JOY_Y" ? "true" : "false") + ",\"Groups\":[\"View\"]," +
                "\"Nodes\":[{\"Game\":\"DCS\",\"Aircraft\":{" + string.Join(",", Enumerable.Range(1, r.Planes).Select(p => "\"Plane" + p.ToString("D2") + "\":true")) + "}}" +
                // Many games on one card: the aircraft chip still shows (the chips wrap).
                (r.Planes == 19 ? string.Concat(new[] { "IL2Game", "MSFS2024", "WarThunder", "XPlane12", "FalconBMS" }.Select(g => ",{\"Game\":\"" + g + "\"}")) : string.Empty) + "]," +
                "\"Bind\":{\"Joystick\":\"" + r.Device + "\",\"" + (r.Input == "JOY_Y" ? "JAxis" : "JButton") + "\":\"" + r.Input + "\",\"AllReformers\":[" + string.Join(",", r.Mods.Select(m => "\"" + m + "\"")) + "]}}")));
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
