using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The check's "actions in several relations" with the real engine on a small copy of a tester's profile and its
// history (DuplicateFixture): four cards in words with 96% suggestions from the history, "Fix the sure ones" as one
// profile version that Undo takes back, "Keep both" saved in the profile, an instruction in words. Offscreen pictures
// of the panel (1280 and 2267 wide, Light and Dark) when JOYPRO_DUP_RENDER_OUT names a folder.
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class DuplicateRealEngineTests
{
    private static readonly Regex RawId = new(@"\bd-?\d+p(nil|-?[\d.]+)u|\b[ad]\d{3,4}cd|\{[0-9A-Fa-f]{8}-|JOY_[A-Z]", RegexOptions.CultureInvariant);

    [Fact]
    public void ChecksFixesAndUndo_OnTheTestersProfile()
    {
        string? engine = DuplicateFixture.EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "dup-real-" + Guid.NewGuid().ToString("N"));
        string? render = Environment.GetEnvironmentVariable("JOYPRO_DUP_RENDER_OUT");
        Dictionary<string, string?> previous = new();
        try
        {
            string profile = DuplicateFixture.PrepareAsync(engine, root).GetAwaiter().GetResult();
            foreach (KeyValuePair<string, string> variable in DuplicateFixture.Environment(root))
            {
                previous[variable.Key] = Environment.GetEnvironmentVariable(variable.Key);
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                CheckIssueSection section = Assert.Single(vm.CheckSections, s => s.Kind == "duplicates");
                Assert.Equal(4, section.Cards.Count);
                CheckIssueCard trim = section.Cards.Single(c => c.Headline.Contains("AL/Right", StringComparison.Ordinal));
                Assert.Equal("AH-64D Block II (CPG and Pilot): 'Force Trim/Hold Mode Switch - AL/Right'", trim.Headline);
                Assert.Equal("96% sure: keep in 'Trim Right'", trim.SuggestionText);
                Assert.Contains("It was only in 'Trim Right' before", trim.ReasonText);
                Assert.Contains(section.Cards, c => c.Headline == "AJS 37 Viggen and Mirage 2000C: 'Thrust'" && c.SuggestionText == "96% sure: keep in 'Throttle Right Inverted DZ'");
                foreach (CheckIssueCard card in section.Cards)
                {
                    foreach (string text in new[] { card.Headline, card.Details, card.SuggestionText, card.ReasonText }.Concat(card.ActionLines).Concat(card.Choices.Select(c => c.KeepText)))
                        Assert.False(RawId.IsMatch(text), "a raw id in the panel: " + text);
                }
                Assert.Equal("Fix the sure ones (7 of 7 at 90% or more)", vm.FixSureOnesText);
                if (!string.IsNullOrWhiteSpace(render))
                    RenderPanel(vm, render, "cards");

                // Fix the sure ones: a preview of 7 changes, applied as one version; Undo brings all of them back.
                AvaloniaRenderThread.Wait(vm.PreviewSureDuplicatesCommand.ExecuteAsync(null));
                Assert.Equal(7, vm.DuplicatePreview.Count);
                if (!string.IsNullOrWhiteSpace(render))
                    RenderPanel(vm, render, "preview");
                int versions = JoyPro.History.ProfileHistory.Read(profile).Versions.Count;
                AvaloniaRenderThread.Wait(vm.ApplyDuplicatePreviewCommand.ExecuteAsync(null));
                Assert.DoesNotContain(vm.CheckSections, s => s.Kind == "duplicates");
                Assert.Equal(versions + 1, JoyPro.History.ProfileHistory.Read(profile).Versions.Count);
                AvaloniaRenderThread.Wait(vm.UndoLastChangeCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                Assert.Equal(4, vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Count);

                // Keep both: saved in the profile, the card stays away after a reload.
                CheckIssueCard caution = vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Single(c => c.Headline.Contains("Master Caution", StringComparison.Ordinal));
                AvaloniaRenderThread.Wait(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)caution.KeepBothCommand!).ExecuteAsync(null));
                Assert.Equal(3, vm.CheckSections.Single(s => s.Kind == "duplicates").Cards.Count);
                MainWindowViewModel again = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                AvaloniaRenderThread.Wait(again.ValidateProfileCommand.ExecuteAsync(null));
                Assert.DoesNotContain(again.CheckSections.Single(s => s.Kind == "duplicates").Cards, c => c.Headline.Contains("Master Caution", StringComparison.Ordinal));

                // Keep in one relation, one card.
                CheckIssueCard thrust = again.CheckSections.Single(s => s.Kind == "duplicates").Cards.Single(c => c.Headline.Contains("Thrust", StringComparison.Ordinal));
                AvaloniaRenderThread.Wait(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)thrust.Choices.Single(c => c.Relation == "Throttle Right Inverted DZ").KeepCommand!).ExecuteAsync(null));
                Assert.DoesNotContain(again.CheckSections.Single(s => s.Kind == "duplicates").Cards, c => c.Headline.Contains("Thrust", StringComparison.Ordinal));

                // An instruction in words, read by the phrase rules alone.
                again.DuplicateInstruction = "keep trim functions in the Trim relations, everything else in the Mode Button relations";
                AvaloniaRenderThread.Wait(again.PreviewDuplicateInstructionCommand.ExecuteAsync(null));
                Assert.Equal(4, again.DuplicatePreview.Count);
                Assert.All(again.DuplicatePreview, c => Assert.Contains("keep in 'Trim", c.Text, StringComparison.Ordinal));
                Assert.Contains("Understood: everything else in the Mode Button relations", again.DuplicatePreviewNotes);
                if (!string.IsNullOrWhiteSpace(render))
                    RenderPanel(again, render, "instruction");
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

    // The Export page scrolled to the panel, and the panel alone at its full height; 1280 and 2267 wide, Light and Dark.
    private static void RenderPanel(MainWindowViewModel vm, string folder, string step)
    {
        Directory.CreateDirectory(folder);
        vm.CurrentStage = UiStage.Export;
        foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
        {
            Application.Current!.RequestedThemeVariant = variant;
            foreach ((int width, int height) in new[] { (2267, 1250), (1280, 800) })
            {
                using HiddenWindow page = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(width, height));
                Control? panel = page.Page.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "CheckProblemsBorder");
                panel?.BringIntoView();
                page.Settle();
                GateRender.Save(page, width, height, 1, Path.Combine(folder, "check-" + step + "-page-" + width + "-" + theme + ".png"));

                // The panel alone, as tall as it is.
                Views.Controls.CheckProblemsPanel alone = new() { DataContext = vm };
                Window host = new() { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = alone } };
                using HiddenWindow sized = HiddenWindow.Create(host, new Size(width, 4000));
                double tall = Math.Min(4000, Math.Ceiling(alone.DesiredSize.Height + 24));
                using HiddenWindow fitted = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = new Views.Controls.CheckProblemsPanel { DataContext = vm } } }, new Size(width, Math.Max(400, tall)));
                GateRender.Save(fitted, width, Math.Max(400, tall), 1, Path.Combine(folder, "check-" + step + "-panel-" + width + "-" + theme + ".png"));
            }
        }
    }
}
