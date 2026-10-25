using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests.QualityGate;

// I10 of the quality gate (QualityGate.cs): layout. Every main page, and the relation editor with an empty, a normal and
// a large relation, laid out offscreen (HiddenWindow) at the window's minimum size, 1280x720 and 1920x1080, at 100 % and
// 150 % scaling, in Light and Dark. 150 % means the window has the physical size / 1.5 in layout units (clamped to the
// window's MinWidth/MinHeight, as Windows does).
//   - No two visible interactive controls (buttons, toggles and chips, check boxes, text and combo boxes) intersect,
//     unless one holds the other; popups and flyouts are their own layer and not part of the page.
//   - No text is cut: a one-line text without trimming needs no more width than it got.
//   - A relation opened in the editor never pre-fills the search with words that find nothing ("Relation 024").
// A page with findings is saved as layout-<page>-<size>-<theme>.png with the overlaps outlined in red.
[SupportedOSPlatform("windows")]
public sealed class QualityGateLayoutTests
{
    private sealed record Variant(string Name, UiStage Stage, string Relation = "", bool AddPanel = false, bool Check = false, bool OwnLayouts = false);

    // The text size and font runs: JoyPro's own, then Settings > Appearance's text size and two theme fonts.
    private static readonly (string Label, double TextScale, string Font)[] Typography =
    [
        ("", 1.0, ""),
        ("text 150%", 1.5, ""),
        ("text 200%", 2.0, ""),
        ("serif font, text 150%", 1.5, "Georgia"),
        ("wide font, text 150%", 1.5, "Verdana"),
    ];

    private static readonly (string Label, double Width, double Height, double Scale)[] Screens =
    [
        ("MinWidth@100%", 0, 0, 1.0),
        ("MinWidth@150%", 0, 0, 1.5),
        ("1280x720@100%", 1280, 720, 1.0),
        ("1280x720@150%", 1280, 720, 1.5),
        ("1920x1080@100%", 1920, 1080, 1.0),
        ("1920x1080@150%", 1920, 1080, 1.5),
    ];

    [Fact]
    public void I10_NoOverlapsNoClippedTextAtEverySize()
    {
        if (!Gate.Enabled)
            return;
        string dir = Path.Combine(Gate.OutDir, "layout");
        Directory.CreateDirectory(dir);
        string profile = Gate.CopyProfile("layout") ?? Gate.EmptyProfile("layout");
        Dictionary<string, (HashSet<string> Screens, HashSet<string> Themes, string Detail)> overlaps = new(StringComparer.Ordinal);
        Dictionary<string, (HashSet<string> Screens, HashSet<string> Themes, string Detail)> clipped = new(StringComparer.Ordinal);
        Dictionary<string, (HashSet<string> Screens, HashSet<string> Themes, string Detail)> spilled = new(StringComparer.Ordinal);
        int windows = 0;
        GateSession.Run(profile, async s =>
        {
            MainWindowViewModel vm = s.Vm;
            await PrepareRelationsAsync(s, profile);
            if (File.Exists(profile))
                await vm.LoadProfileCommand.ExecuteAsync(null);
            await vm.RefreshGameInstallsCommand.ExecuteAsync(null);
            await vm.RefreshRegistryGamesCommand.ExecuteAsync(null);
            List<RelationListItem> relations = vm.Relations.ToList();
            // The opt-in Statistics page with every section (the fixtures' document).
            vm.ShowStatisticsPage = true;
            vm.Statistics.Apply(UsageRecordingTests.SampleStats);
            string large = await LargestRelationAsync(s, relations);
            string normal = relations.FirstOrDefault(r => r.Name.StartsWith("Pitch", StringComparison.OrdinalIgnoreCase))?.Name
                            ?? relations.Where(r => !r.Name.StartsWith("QG ", StringComparison.Ordinal) && r.Name != "Relation 024").Select(r => r.Name).Skip(relations.Count / 2).FirstOrDefault() ?? string.Empty;
            List<Variant> variants =
            [
                new("Start", UiStage.Start), new("Relations (none open)", UiStage.Relations), new("Binds", UiStage.Binds), new("Modifiers", UiStage.Modifiers),
                new("Layout", UiStage.Layout),
                // The device list as the user's profile shows it: his own layouts of many devices with long names
                // ("Your layout · 135 markers" chips next to Model... and the swap button).
                new("Layout: your layouts, long device names", UiStage.Layout, OwnLayouts: true),
                new("Export", UiStage.Export), new("Settings", UiStage.Settings), new("Advanced", UiStage.Advanced),
                new("HUD", UiStage.Hud), new("Force feedback", UiStage.FfbTelemetry), new("Virtual joystick", UiStage.VirtualJoystick), new("Cockpits", UiStage.Cockpits),
                new("Statistics", UiStage.Statistics),
                new("Relation editor: empty relation", UiStage.Relations, "QG empty relation"),
                new("Relation editor: normal relation", UiStage.Relations, normal),
                new("Relation editor: normal relation, Add actions", UiStage.Relations, normal, AddPanel: true),
                new("Relation editor: large relation", UiStage.Relations, large),
                new("Relation editor: placeholder name", UiStage.Relations, "Relation 024", AddPanel: true),
                new("Export: problems found by the check", UiStage.Export, Check: true),
            ];
            try
            {
                foreach (Variant variant in variants)
                {
                    if (variant.Relation.Length == 0 && variant.Name.StartsWith("Relation editor", StringComparison.Ordinal))
                        continue;
                    await ShowAsync(s, variant);
                    // JoyPro's text size in Light and Dark at every screen; then the text size of Settings > Appearance
                    // (150 % and 200 %) and a serif and a wide font at 150 % text, with 150 % Windows scaling, in Dark.
                    foreach ((string typo, double textScale, string font) in Typography)
                    foreach ((string theme, ThemeVariant themeVariant) in typo.Length == 0 ? new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) } : new[] { ("dark", ThemeVariant.Dark) })
                    {
                        if (font.Length > 0 && !JoyPro.Gui.Services.ThemeService.IsInstalled(font))
                            continue;
                        JoyPro.Gui.Services.ThemeService.ApplyTypography(textScale, new JoyPro.Gui.Services.ThemeFonts { Ui = font });
                        Application.Current!.RequestedThemeVariant = themeVariant;
                        // One layout per logical size; every screen that maps to it is named in the findings.
                        foreach (IGrouping<(double, double), (string Label, double Width, double Height, double Scale)> logical in Screens.Where(sc => typo.Length == 0 || sc.Scale > 1.4).GroupBy(sc => LogicalSize(sc)))
                        {
                            (double width, double height) = logical.Key;
                            string screens = string.Join(", ", logical.Select(l => l.Label + (typo.Length > 0 ? " " + typo : string.Empty)));
                            HiddenWindow window = GateRender.NewWindow(vm, width, height);
                            windows++;
                            if (variant.Check)
                            {
                                // The check's problems panel scrolled into view (cards, fixes, a preview when there are duplicates).
                                Control? panel = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window.Page).OfType<Control>().FirstOrDefault(c => c.Name == "CheckProblemsBorder");
                                panel?.BringIntoView();
                                window.Settle();
                            }
                            try
                            {
                                Control root = GateRender.Root(window);
                                List<Rect> boxes = [];
                                foreach ((Control a, Rect ra, Control b, Rect rb) in LayoutChecks.Overlaps(root, width, height))
                                {
                                    string key = variant.Name + "|" + LayoutChecks.Describe(a) + "|" + LayoutChecks.Describe(b);
                                    Add(overlaps, key, screens, theme, "at " + Round(ra) + " and " + Round(rb));
                                    boxes.Add(ra);
                                    boxes.Add(rb);
                                }
                                foreach ((Control item, Rect ir, Border frame, Rect fr) in LayoutChecks.Spills(root, width, height))
                                {
                                    string key = variant.Name + "|" + LayoutChecks.Describe(item) + "|" + GateRender.Describe(frame);
                                    Add(spilled, key, screens, theme, "at " + Round(ir) + ", frame " + Round(fr));
                                    boxes.Add(ir);
                                }
                                foreach ((TextBlock text, double need, double got) in LayoutChecks.ClippedTexts(root, width, height))
                                {
                                    string owner = Owner(text);
                                    string key = variant.Name + "|'" + Gate.Short(text.Text ?? string.Empty, 50) + "'" + (owner.Length > 0 ? " in " + owner : string.Empty);
                                    Add(clipped, key, screens, theme, "needs " + need.ToString("0", CultureInfo.InvariantCulture) + " px, has " + got.ToString("0", CultureInfo.InvariantCulture));
                                    Rect? r = GateRender.BoundsInWindow(text, root);
                                    if (r != null)
                                        boxes.Add(r.Value);
                                }
                                if (boxes.Count > 0)
                                    GateRender.Save(window, width, height, logical.Max(l => l.Scale), Path.Combine(dir, "layout-" + FileName(variant.Name) + "-" + (int)width + "x" + (int)height + "-" + theme + (typo.Length > 0 ? "-" + FileName(typo) : string.Empty) + ".png"), boxes);
                            }
                            finally
                            {
                                window.Dispose();
                                AvaloniaRenderThread.Pump();
                            }
                        }
                    }
                }
            }
            finally
            {
                JoyPro.Gui.Services.ThemeService.ApplyTypography(1.0, null);
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
        foreach ((string key, var e) in overlaps)
        {
            string[] parts = key.Split('|');
            Gate.Fail("I10", "interactive controls overlap", string.Empty, parts[0] + ": " + parts[1] + " overlaps " + parts[2],
                "screens: " + string.Join(", ", e.Screens) + "; themes: " + string.Join("/", e.Themes) + "; " + e.Detail);
        }
        foreach ((string key, var e) in spilled)
        {
            string[] parts = key.Split('|');
            Gate.Fail("I10", "content outside its frame", string.Empty, parts[0] + ": " + parts[1] + " sticks out of " + parts[2],
                "screens: " + string.Join(", ", e.Screens) + "; themes: " + string.Join("/", e.Themes) + "; " + e.Detail);
        }
        foreach ((string key, var e) in clipped)
        {
            string[] parts = key.Split('|');
            Gate.Fail("I10", "text cut off (no trimming)", string.Empty, parts[0] + ": " + parts[1], "screens: " + string.Join(", ", e.Screens) + "; themes: " + string.Join("/", e.Themes) + "; " + e.Detail);
        }
        Gate.Note("I10: " + windows + " layouts checked (" + Screens.Length + " screens, Light and Dark); pictures with findings in " + dir);
        Gate.Finish("I10");
    }

    // Every device card of the Layout page as one of the user's own layouts with many markers, the device names as long
    // as real ones get ("LEFT VPC MongoosT-50CM3 Throttle - alias ..."), at least 12 cards; the first one picked with a
    // layout file's raw name and the engine's top score (190 of 200), whose header line must say a readable name and
    // at most 100 %.
    internal static void OwnLayoutCards(MainWindowViewModel vm)
    {
        string[] longNames =
        [
            "LEFT VPC MongoosT-50CM3 Throttle", "RIGHT VPC Constellation ALPHA Prime", "VKBsim Gladiator EVO OT Left Premium",
            "Thrustmaster T.Flight Rudder Pedals", "WINWING Orion2 Throttle Base + F15EX Handle", "Virpil Control Panel #2 (CP2)",
            "Logitech G Saitek PRO Flight Switch Panel", "VPC SharKa-50 Control Panel", "MFG Crosswind V3 Rudder Pedals",
            "Thrustmaster HOTAS Warthog Joystick", "WINWING MFD C Left", "Razer Tartarus V2 Keypad"
        ];
        int index = 0;
        foreach (LayoutSuggestionItem item in vm.LayoutSuggestions)
            Own(item, index++);
        for (; index < longNames.Length; index++)
        {
            LayoutSuggestionItem extra = new() { Device = longNames[index] + " {" + Guid.NewGuid().ToString().ToUpperInvariant() + "}" };
            Own(extra, index);
            vm.LayoutSuggestions.Add(extra);
        }
        LayoutSuggestionItem first = vm.LayoutSuggestions[0];
        first.TemplateName = "VPC_MT50CM+mod2";
        first.Score = 190;
        vm.SelectedLayoutSuggestion = null;
        vm.SelectedLayoutSuggestion = first;
        string status = vm.LayoutSuggestStatus ?? string.Empty;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(status, @"(\d+)\s*%"))
        {
            if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 100)
                Gate.Fail("I10", "match above 100 %", string.Empty, "Layout: " + Gate.Short(status, 120), "the picture's match is a percentage (0..100)");
        }
        if (status.Contains('_') || status.Contains("+mod", StringComparison.Ordinal))
            Gate.Fail("I10", "raw picture file name", string.Empty, "Layout: " + Gate.Short(status, 120), "the header names the picture in words");

        static void Own(LayoutSuggestionItem item, int i)
        {
            item.Source = "legacy-layout-dir";
            if (string.IsNullOrWhiteSpace(item.LayoutPath))
                item.LayoutPath = "C:/Layouts/device" + i + ".layout";
            item.MarkerCount = 100 + 35 * (i % 3);
            if (string.IsNullOrWhiteSpace(item.Model))
                item.Model = "Virpil Controls VPC MongoosT-50CM3 Throttle (VID 3344, PID 0194)";
        }
    }

    private static (double, double) LogicalSize((string Label, double Width, double Height, double Scale) screen)
    {
        const double minWidth = 1200, minHeight = 760; // MainWindow.axaml MinWidth / MinHeight
        double width = screen.Width <= 0 ? minWidth : Math.Max(minWidth, Math.Round(screen.Width / screen.Scale));
        double height = screen.Height <= 0 ? minHeight : Math.Max(minHeight, Math.Round(screen.Height / screen.Scale));
        return (width, height);
    }

    private static void Add(Dictionary<string, (HashSet<string> Screens, HashSet<string> Themes, string Detail)> map, string key, string screens, string theme, string detail)
    {
        if (!map.TryGetValue(key, out var e))
            map[key] = e = (new HashSet<string>(), new HashSet<string>(), detail);
        foreach (string screen in screens.Split(", "))
            e.Screens.Add(screen);
        e.Themes.Add(theme);
    }

    private static string Round(Rect r) => "(" + (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height + ")";

    private static string FileName(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').Replace("--", "-");

    // The empty relation and the placeholder-named one, added to the profile copy before it is opened.
    private static async Task PrepareRelationsAsync(GateSession s, string profile)
    {
        foreach (string[] args in new[]
                 {
                     new[] { "relations", "add", "QG empty relation", "--button", "--node", "WarThunder:ID_GEAR:WarThunder" },
                     new[] { "relations", "suggest", "apply", "QG empty relation", "--remove", "WarThunder:ID_GEAR:WarThunder" },
                     new[] { "relations", "add", "Relation 024", "--button", "--node", "WarThunder:ID_GEAR:WarThunder" },
                 })
        {
            JoyPro.Gui.Models.EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, ["--profile", profile, "--status", "false", .. args]);
            if (result.ExitCode != 0)
                Gate.Note("I10: could not prepare '" + string.Join(" ", args) + "': " + Gate.Short(result.StandardError, 100));
        }
    }

    private static async Task<string> LargestRelationAsync(GateSession s, List<RelationListItem> relations)
    {
        if (!s.HasProfile)
            return string.Empty;
        JoyPro.Gui.Models.EngineCommandResult result = await s.Engine.RunAsync(Gate.EnginePath, ["--profile", s.Profile, "--status", "false", "relations", "list"]);
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(MainWindowViewModel.ExtractJsonPayload(result.StandardOutput));
            return doc.RootElement.GetProperty("Relations").EnumerateArray()
                .OrderByDescending(r => r.TryGetProperty("Nodes", out System.Text.Json.JsonElement n) ? n.GetArrayLength() : 0)
                .Select(r => r.GetProperty("Name").GetString() ?? string.Empty).FirstOrDefault() ?? string.Empty;
        }
        catch (Exception)
        {
            return relations.FirstOrDefault()?.Name ?? string.Empty;
        }
    }

    private static async Task ShowAsync(GateSession s, Variant variant)
    {
        MainWindowViewModel vm = s.Vm;
        vm.CurrentStage = variant.Stage;
        await Task.Delay(100);
        await s.SettleAsync(() => true, 30000);
        if (variant.Check)
        {
            await vm.ValidateProfileCommand.ExecuteAsync(null);
            if (vm.HasSureDuplicates)
                await vm.PreviewSureDuplicatesCommand.ExecuteAsync(null);
            await s.SettleAsync(() => true, 30000);
        }
        if (variant.OwnLayouts)
        {
            await s.SettleAsync(() => !vm.IsBusy, 60000);
            OwnLayoutCards(vm);
            await Task.Delay(200);
            await s.SettleAsync(() => true, 30000);
        }
        if (variant.Stage != UiStage.Relations)
            return;
        if (variant.Relation.Length == 0)
        {
            vm.SelectedRelation = null;
            await s.SettleAsync(() => true, 30000);
            return;
        }
        RelationListItem? relation = vm.Relations.FirstOrDefault(r => r.Name == variant.Relation);
        if (relation == null)
        {
            Gate.Note("I10: relation '" + variant.Relation + "' not in the list");
            return;
        }
        vm.SelectedRelation = relation;
        await Task.Delay(200);
        await s.SettleAsync(() => vm.SelectedRelation?.Name == relation.Name, 60000);
        if (variant.AddPanel)
        {
            vm.ShowBuilderFindCommand.Execute(null);
            await Task.Delay(400);
            await s.SettleAsync(() => true, 30000);
        }
        // The search of Add actions: pre-filled words must find something.
        string query = (vm.BuilderSearchText ?? string.Empty).Trim();
        if (query.Length > 0 && vm.BuilderSearchResults.Count == 0 && !vm.IsBuilderSearching)
            Gate.Fail("I10", "search pre-filled with words that find nothing", string.Empty, variant.Name + ": '" + query + "'", Gate.Short(vm.BuilderSearchStatus, 120));
    }

    private static string Owner(TextBlock text)
    {
        for (Visual? v = text.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            if (v is Button or TextBox or ComboBox or TabItem or MenuItem or Expander)
                return GateRender.Describe((Control)v);
        }
        return text.Classes.Count > 0 ? "TextBlock." + string.Join(".", text.Classes) : string.Empty;
    }
}
