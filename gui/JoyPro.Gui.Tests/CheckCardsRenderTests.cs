using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The check's cards (2.5.0, docs/ui/check.md) on a copy of a real profile with the real engine, rendered offscreen at
// 1280 and 2267 wide in Light and Dark: the panel with every kind of card (one button does two things, one button runs
// two commands, same command on two buttons with "Caused by", unknown commands, not written to the game, copies), the
// relation opened from a card with its commands marked, and the texts checked for ids and internal words. Opt-in (a
// real profile, takes minutes): JOYPRO_CHECK_CARDS_PROFILE=<.pr0file> (copied, never changed), optional
// JOYPRO_CHECK_CARDS_HISTORY=<its ProfileHistory folder> (copied, so "Caused by" can be found) and
// JOYPRO_CHECK_CARDS_OUT=<folder> for the pictures; dotnet test --filter CheckCardsRenderTests
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class CheckCardsRenderTests
{
    [Fact]
    public void RenderCheckCards_OnARealProfileCopy()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_CHECK_CARDS_PROFILE");
        string? engine = DuplicateFixture.EnginePath();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || engine == null)
            return;
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_CHECK_CARDS_OUT");
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "check-cards-" + Guid.NewGuid().ToString("N"));
        Dictionary<string, string?> previous = new();
        try
        {
            Directory.CreateDirectory(root);
            string profile = Path.Combine(root, Path.GetFileName(source));
            File.Copy(source, profile);
            Dictionary<string, string> environment = new()
            {
                ["JOYPRO_DATA_DIR"] = Path.Combine(root, "data"),
                ["JOYPRO_DEVICE_KB"] = "off",
                ["JOYPRO_PROFILE_HISTORY"] = "on",
                ["JOYPRO_ENGINE_SERVER"] = "off"
            };
            foreach (KeyValuePair<string, string> variable in environment)
            {
                previous[variable.Key] = Environment.GetEnvironmentVariable(variable.Key);
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
            string? history = Environment.GetEnvironmentVariable("JOYPRO_CHECK_CARDS_HISTORY");
            if (!string.IsNullOrWhiteSpace(history) && Directory.Exists(history))
                CopyHistory(history, profile);

            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                // A "Not written to the game" card next to the real ones.
                CheckIssueCard? any = vm.CheckSections.SelectMany(s => s.Cards).FirstOrDefault(c => c.Kind == "duplicates");
                if (any != null)
                {
                    vm.ApplyExportMarks(
                    [
                        new ExportMarkItem
                        {
                            Game = "DCS", Aircraft = "F-16C_50", Relation = any.Relations[0], ActionId = "d3001pnilu3001cd2vd1vpnilvu0", Action = "Landing Gear Up",
                            Device = "Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}", Input = "JOY_BTN5", GameActionId = "d3002pnilu3002cd2vd1vpnilvu0",
                            GameAction = "Weapon Release", Date = "2026-10-31"
                        }
                    ]);
                }
                List<string> problems = CheckTextGate.Problems(vm.CheckSections.SelectMany(s => s.AllCards));
                Assert.True(problems.Count == 0, "check texts with ids or internal words:\n" + string.Join("\n", problems.Take(30)));
                // Every card a person can act on has a fix or an Open button; errors their own icon.
                foreach (CheckIssueCard card in vm.CheckSections.SelectMany(s => s.AllCards))
                {
                    Assert.True(card.HasAnyFix || card.HasOpenButtons || card.Kind == "notes", "a card without anything to click: " + card.Title + " / " + card.Sentence);
                    if (card.Kind is "input-conflicts" or "action-conflicts")
                        Assert.True(card.IsError);
                }
                if (string.IsNullOrWhiteSpace(outDir))
                    return;
                Directory.CreateDirectory(outDir);
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (int width in new[] { 1280, 2267 })
                    {
                        // The panel alone as tall as it is (the first page of cards of each section), then each kind alone.
                        RenderPanel(vm, width, Path.Combine(outDir, "check-panel-" + width + "-" + theme + ".png"));
                        foreach (CheckIssueSection section in vm.CheckSections.ToList())
                            RenderSection(vm, section, width, Path.Combine(outDir, "check-" + section.Kind + "-" + width + "-" + theme + ".png"));
                    }
                }
                // A card's details and its whole table open.
                CheckIssueCard? rich = vm.CheckSections.SelectMany(s => s.Cards).FirstOrDefault(c => c.HasHiddenRows && c.HasDetails);
                if (rich != null)
                {
                    rich.ShowAllRows = true;
                    rich.IsDetailsOpen = true;
                    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                    RenderSection(vm, vm.CheckSections.First(s => s.Cards.Contains(rich)), 1280, Path.Combine(outDir, "check-card-open-1280-light.png"));
                }
                // The relation opened from a card: the commands of the card marked, scrolled into view.
                CheckIssueCard? open = vm.CheckSections.SelectMany(s => s.Cards).FirstOrDefault(c => c.Kind == "duplicates" && c.OpenButtons.Count == 2);
                if (open != null)
                {
                    foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                    {
                        Application.Current!.RequestedThemeVariant = variant;
                        vm.CurrentStage = UiStage.Export;
                        AvaloniaRenderThread.Wait(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)open.OpenButtons[1].Command!).ExecuteAsync(null));
                        AvaloniaRenderThread.Wait(System.Threading.Tasks.Task.Delay(1500));
                        AvaloniaRenderThread.Pump();
                        vm.ApplyCheckHighlight();
                        Assert.Contains(vm.RelationNodes, n => n.IsSearchHit);
                        using HiddenWindow page = GateRender.NewWindow(vm, 1280, 800);
                        page.Window.DataContext = vm;
                        page.Settle();
                        GateRender.Save(page, 1280, 800, 1, Path.Combine(outDir, "check-opened-relation-1280-" + theme + ".png"));
                        vm.NavigateBackCommand.Execute(null);
                        Assert.Equal(UiStage.Export, vm.CurrentStage);
                    }
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            });
        }
        finally
        {
            foreach (KeyValuePair<string, string?> variable in previous)
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            EngineServerPool.StopAll();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    // The history of the original profile, as the history of the copy (its folder name and its profile path).
    private static void CopyHistory(string from, string profile)
    {
        string folder = JoyPro.History.ProfileHistory.FolderFor(profile);
        Directory.CreateDirectory(folder);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(folder, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        string log = Path.Combine(folder, "history.json");
        if (!File.Exists(log))
            return;
        System.Text.Json.Nodes.JsonNode? json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(log));
        if (json != null)
        {
            json["profilePath"] = profile;
            File.WriteAllText(log, json.ToJsonString());
        }
    }

    private static void RenderPanel(MainWindowViewModel vm, int width, string file)
    {
        Views.Controls.CheckProblemsPanel measure = new() { DataContext = vm };
        double tall;
        using (HiddenWindow sized = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = measure } }, new Size(width, 12000)))
            tall = Math.Min(12000, Math.Ceiling(measure.DesiredSize.Height + 24));
        using HiddenWindow fitted = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = new Views.Controls.CheckProblemsPanel { DataContext = vm } } },
            new Size(width, Math.Max(400, tall)));
        GateRender.Save(fitted, width, Math.Max(400, tall), 1, file);
    }

    // One section's cards on their own (the section's own template from the panel, its first page of cards).
    private static void RenderSection(MainWindowViewModel vm, CheckIssueSection section, int width, string file)
    {
        List<CheckIssueSection> all = vm.CheckSections.ToList();
        try
        {
            foreach (CheckIssueSection other in all.Where(s => !ReferenceEquals(s, section)))
                vm.CheckSections.Remove(other);
            RenderPanel(vm, width, file);
        }
        finally
        {
            vm.CheckSections.Clear();
            foreach (CheckIssueSection s in all)
                vm.CheckSections.Add(s);
        }
    }
}

// The words of the check's cards: what a first-time PC user reads. No game id, device GUID, raw input token, profile key
// or internal word ("node", "relation item", "item key", "multiple active items") in a title, the buttons line, the
// sentence, the table, the cause, a fix or an Open button, nor in the details' reasons (the technical line that names
// where an unknown command came from, with its id, is the one exception).
internal static class CheckTextGate
{
    private static readonly System.Text.RegularExpressions.Regex Raw = new(
        @"\bd-?\d+p(nil|-?[\d.]+)u|\b[ad]\d{3,4}cd|cdnil|\{[0-9A-Fa-f]{8}-|\bJOY_[A-Z]|§|\bnode\b|relation item|item key|Button Item|multiple active items|\bKEY_[A-Z_]{3,}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static IEnumerable<string> Texts(CheckIssueCard card)
    {
        yield return card.Title;
        yield return card.ButtonsLine;
        yield return card.Sentence;
        yield return card.CausedText;
        foreach (CheckTableRow row in card.Rows)
        {
            yield return row.Left;
            yield return row.Right;
        }
        if (card.Recommended != null)
            yield return card.Recommended.Text;
        foreach (CheckFix fix in card.Fixes.Concat(card.MoreFixes).Concat(card.OpenButtons))
        {
            yield return fix.Text;
            yield return fix.Tip;
        }
        foreach (string line in card.DetailLines)
            yield return line;
    }

    // "<card title>: <text>" for every text with an id or an internal word.
    public static List<string> Problems(IEnumerable<CheckIssueCard> cards) =>
        cards.SelectMany(c => Texts(c).Where(t => !string.IsNullOrEmpty(t) && Raw.IsMatch(t)).Select(t => c.Title + ": " + t)).Distinct().ToList();
}
