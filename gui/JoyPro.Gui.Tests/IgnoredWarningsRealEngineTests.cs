using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// The check's "Ignore" and its list "Ignored warnings" with the real engine on the tester's small profile
// (DuplicateFixture): Ignore hides a card (saved in the profile, one undo step), the list shows it in the card's words
// with "Show again", one more button with the command brings the card back marked "Shown again: now also on ..." and
// new, a button that does two things is ignored the same way, "Show all again" brings every one back, and the check's
// search finds the list by what people call it. Offscreen pictures of the panel (1280 wide, Light and Dark) when
// JOYPRO_IGNORED_RENDER_OUT names a folder.
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class IgnoredWarningsRealEngineTests
{
    [Theory]
    [InlineData("ignore list")]
    [InlineData("ignored")]
    [InlineData("hidden warnings")]
    [InlineData("dismissed")]
    [InlineData("muted")]
    [InlineData("keep both")]
    public void TheCheckSearch_FindsTheList_ByWhatPeopleCallIt(string search) =>
        Assert.True(MainWindowViewModel.IsIgnoredWarningsSearch(search.Split(' ')), search);

    [Theory]
    [InlineData("master caution")]
    [InlineData("warnings")]
    [InlineData("trim list")]
    public void OtherSearches_DoNotOpenTheList(string search) =>
        Assert.False(MainWindowViewModel.IsIgnoredWarningsSearch(search.Split(' ')), search);

    [Fact]
    public void Ignore_ShownAgain_AndTheIgnoredList_OnTheTestersProfile()
    {
        string? engine = DuplicateFixture.EnginePath();
        if (engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "ignored-real-" + Guid.NewGuid().ToString("N"));
        string? render = Environment.GetEnvironmentVariable("JOYPRO_IGNORED_RENDER_OUT");
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
                List<CheckIssueCard> Cards() => vm.CheckSections.SingleOrDefault(s => s.Kind == "duplicates")?.AllCards.ToList() ?? [];
                CheckIssueCard Caution() => Cards().Single(c => c.Sentence.Contains("Master Caution", StringComparison.Ordinal));
                Assert.Equal(4, Cards().Count);
                Assert.False(vm.HasIgnoredWarnings);

                // Ignore: one button, no "Keep both" next to it; the card goes, the list has it in the card's words.
                CheckIssueCard caution = Caution();
                Assert.Single(caution.Fixes, f => f.Text == "Ignore");
                Assert.DoesNotContain(caution.Fixes.Concat(caution.MoreFixes), f => f.Text.StartsWith("Keep both", StringComparison.Ordinal));
                Assert.Contains("comes back when the command lands on one more button", caution.Fixes.Single(f => f.Text == "Ignore").Tip);
                int versions = JoyPro.History.ProfileHistory.Read(profile).Versions.Count;
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)caution.IgnoreCommand!).ExecuteAsync(null));
                Assert.Equal(versions + 1, JoyPro.History.ProfileHistory.Read(profile).Versions.Count);
                Assert.Equal(3, Cards().Count);
                Assert.Equal(1, vm.IgnoredWarningCount);
                Assert.Equal("Ignored warnings (1)", vm.IgnoredWarningsText);
                CheckIssueCard ignored = Assert.Single(vm.IgnoredWarnings);
                Assert.Equal(caution.Title, ignored.Title);
                Assert.Equal(caution.ButtonsLine, ignored.ButtonsLine);
                Assert.Equal(caution.Sentence, ignored.Sentence);
                Assert.Equal("Show again", ignored.ShowAgain!.Text);
                Assert.Empty(CheckTextGate.Problems([ignored]));

                // The check's search opens the list.
                Assert.False(vm.IsIgnoredWarningsOpen);
                vm.CheckSearchText = "hidden warnings";
                Assert.True(vm.IsIgnoredWarningsOpen);
                Assert.Single(vm.IgnoredWarnings);
                vm.CheckSearchText = string.Empty;

                // Undo brings the card back (one step), redo by ignoring it again.
                AvaloniaRenderThread.Wait(vm.UndoLastChangeCommand.ExecuteAsync(null));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                Assert.Equal(4, Cards().Count);
                Assert.Equal(0, vm.IgnoredWarningCount);
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)Caution().IgnoreCommand!).ExecuteAsync(null));
                Assert.Equal(1, vm.IgnoredWarningCount);

                // One more button with the command: the card is back, marked and new; the list is empty again.
                AvaloniaRenderThread.Wait(DuplicateFixture.RunAsync(engine, root, profile, "Adding an action", ["relations", "item", "add", "Mode Button 04", "--node", "DCS:" + DuplicateFixture.CautionId + ":F-16C_50"]));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                CheckIssueCard again = Cards().Single(c => c.Sentence.Contains("Master Caution", StringComparison.Ordinal) && c.HasShownAgain);
                Assert.Equal("Shown again: now also on Total Controls MFBB · BTN 36 ('Mode Button 04')", again.ShownAgainText);
                Assert.True(again.IsNew);
                Assert.Equal("Same command on 3 buttons", again.Title);
                Assert.Equal(0, vm.IgnoredWarningCount);
                Assert.Empty(CheckTextGate.Problems([again]));

                // A button that does two things: "Mode Button 04" on the button of "Mode Button 07"; ignored the same way.
                AvaloniaRenderThread.Wait(DuplicateFixture.RunAsync(engine, root, profile, "Binding", ["binds", "set", "Mode Button 04", "--input", "JOY_BTN39"]));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                CheckIssueCard twoThings = vm.CheckSections.Single(s => s.Kind == "input-conflicts").AllCards.First();
                CheckFix ignoreConflict = Assert.Single(twoThings.Fixes, f => f.Text == "Ignore");
                Assert.DoesNotContain(twoThings.Fixes, f => f.Text.StartsWith("Keep both", StringComparison.Ordinal));
                int inputCards = vm.CheckSections.Single(s => s.Kind == "input-conflicts").AllCards.Count;
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)ignoreConflict.Command!).ExecuteAsync(null));
                Assert.Equal(inputCards - 1, vm.CheckSections.SingleOrDefault(s => s.Kind == "input-conflicts")?.AllCards.Count ?? 0);
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)Cards().Single(c => c.HasShownAgain).IgnoreCommand!).ExecuteAsync(null));
                Assert.Equal(2, vm.IgnoredWarningCount);
                Assert.Contains(vm.IgnoredWarnings, c => c.IsError && c.Title.StartsWith("One button does", StringComparison.Ordinal));
                Assert.Contains(vm.IgnoredWarnings, c => c.IsWarning && c.Title == "Same command on 3 buttons");
                // Errors first, like the sections.
                Assert.True(vm.IgnoredWarnings[0].IsError);

                if (!string.IsNullOrWhiteSpace(render))
                {
                    vm.IsIgnoredWarningsOpen = true;
                    RenderPanel(vm, render, "list");
                    vm.IsIgnoredWarningsOpen = false;
                }

                // Show again, one: the card comes back (one step); then the ignored command joins one more button
                // again for the picture of a card that shows again.
                CheckIssueCard listed = vm.IgnoredWarnings.Single(c => c.Title == "Same command on 3 buttons");
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)listed.ShowAgain!.Command!).ExecuteAsync(null));
                Assert.Equal(1, vm.IgnoredWarningCount);
                Assert.Contains(Cards(), c => c.Title == "Same command on 3 buttons" && !c.HasShownAgain);
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)Cards().Single(c => c.Title == "Same command on 3 buttons").IgnoreCommand!).ExecuteAsync(null));
                AvaloniaRenderThread.Wait(DuplicateFixture.RunAsync(engine, root, profile, "Adding an action", ["relations", "item", "add", "Trim Nose Down", "--node", "DCS:" + DuplicateFixture.CautionId + ":F-16C_50"]));
                AvaloniaRenderThread.Wait(vm.ValidateProfileCommand.ExecuteAsync(null));
                CheckIssueCard four = Cards().Single(c => c.HasShownAgain);
                Assert.Equal("Shown again: now also on MOZA AB9 FFB Base · BTN 3 ('Trim Nose Down')", four.ShownAgainText);
                if (!string.IsNullOrWhiteSpace(render))
                {
                    vm.CheckSearchText = "Master Caution";
                    RenderPanel(vm, render, "shown-again");
                    vm.CheckSearchText = string.Empty;
                }

                // Show all again: every ignored one is back (one step), the list button goes.
                Assert.Equal(1, vm.IgnoredWarningCount);
                AvaloniaRenderThread.Wait(vm.ShowAllIgnoredAgainCommand.ExecuteAsync(null));
                Assert.Equal(0, vm.IgnoredWarningCount);
                Assert.False(vm.HasIgnoredWarnings);
                Assert.Equal(inputCards, vm.CheckSections.Single(s => s.Kind == "input-conflicts").AllCards.Count);

                // A fresh view model of the saved profile reads the same.
                CheckFix again2 = vm.CheckSections.Single(s => s.Kind == "input-conflicts").AllCards.First().Fixes.Single(f => f.Text == "Ignore");
                AvaloniaRenderThread.Wait(((IAsyncRelayCommand)again2.Command!).ExecuteAsync(null));
                Assert.Equal(1, vm.IgnoredWarningCount);
                MainWindowViewModel reopened = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                AvaloniaRenderThread.Wait(reopened.ValidateProfileCommand.ExecuteAsync(null));
                Assert.Equal(1, reopened.IgnoredWarningCount);
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

    // The panel alone as tall as it is, 1280 and 900 wide, Light and Dark.
    private static void RenderPanel(MainWindowViewModel vm, string folder, string step)
    {
        Directory.CreateDirectory(folder);
        foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
        {
            Application.Current!.RequestedThemeVariant = variant;
            foreach (int width in new[] { 1280, 900 })
            {
                Views.Controls.CheckProblemsPanel measure = new() { DataContext = vm };
                using (HiddenWindow sized = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = measure } }, new Size(width, 4000)))
                {
                    double tall = Math.Min(4000, Math.Ceiling(measure.DesiredSize.Height + 24));
                    using HiddenWindow fitted = HiddenWindow.Create(new Window { DataContext = vm, Content = new Border { Padding = new Thickness(12), Child = new Views.Controls.CheckProblemsPanel { DataContext = vm } } },
                        new Size(width, Math.Max(400, tall)));
                    GateRender.Save(fitted, width, Math.Max(400, tall), 1, Path.Combine(folder, "ignored-" + step + "-" + width + "-" + theme + ".png"));
                }
            }
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }
}
