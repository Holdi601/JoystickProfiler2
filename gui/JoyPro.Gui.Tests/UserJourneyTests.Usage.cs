using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JoyPro.Gui.Models;
using JoyPro.Gui.ViewModels;

namespace JoyPro.Gui.Tests;

public sealed partial class UserJourneyTests
{
    private sealed partial class Journey
    {
        // Usage statistics (docs/usage-statistics.md): the synthetic fixtures stand for the PC's games
        // (JOYPRO_USAGE_ROOT); the start reads them, Settings > Usage statistics shows the ranking, the Statistics page is
        // switched on and looked at, an aircraft is pinned.
        private void RunUsageStatistics()
        {
            string root = Path.Combine(folder, "usage-root");
            UsageStoreTests.CopyFixtures(root);
            string? oldRoot = Environment.GetEnvironmentVariable("JOYPRO_USAGE_ROOT");
            Environment.SetEnvironmentVariable("JOYPRO_USAGE_ROOT", root);
            try
            {
                FirstStart(answerSetup: "later");
                Step("usage: the start read the games' logs", async () =>
                {
                    await vm!.StartUsageRecordingAsync();
                    await vm.HarvestUsageAsync(null);
                    Note("usage status: " + vm.UsageStatus);
                    if (!File.Exists(Path.Combine(data, "Usage", "sessions.jsonl")))
                        Failures.Add("usage: the harvest wrote no store");
                });
                Step("usage: Settings > Usage statistics", async () =>
                {
                    vm!.CurrentStage = UiStage.Settings;
                    vm.CollapseAllSettingsCommand.Execute(null);
                    vm.MoveSettingsSectionToTopCommand.Execute(vm.FindSettingsSection(MainWindowViewModel.UsageSettingsSectionId));
                    vm.FindSettingsSection(MainWindowViewModel.UsageSettingsSectionId)!.IsExpanded = true;
                    await vm.RefreshUsageRankingAsync();
                    Note("ranking: " + string.Join(" | ", vm.UsageRankingUnits.Take(6).Select(u => u.RankText + " " + u.Title + " - " + u.Detail)));
                    Note("games: " + string.Join(" | ", vm.UsageRankingGames.Take(5).Select(g => g.RankText + " " + g.Title + " - " + g.Detail)));
                    if (!vm.HasUsageRanking)
                        Failures.Add("usage: no ranking in Settings");
                    if (vm.FindNavPage(MainWindowViewModel.StatisticsNavPageId)!.IsShownInBar)
                        Failures.Add("usage: the Statistics page shows before it was switched on");
                }, extraRender: slug => RenderTall(slug, 2000));
                Step("usage: Statistics page switched on", async () =>
                {
                    vm!.ShowStatisticsPage = true;
                    vm.CurrentStage = UiStage.Statistics;
                    await vm.Statistics.ReloadAsync();
                    Note("tiles: " + string.Join(" | ", vm.Statistics.Tiles.Select(t => t.Value + " " + t.Label)));
                    Note("facts: " + string.Join(" | ", vm.Statistics.Facts));
                    if (!vm.Statistics.HasData || vm.Statistics.GameBars.Count == 0 || vm.Statistics.UnitBars.Count == 0)
                        Failures.Add("usage: the Statistics page has no data");
                }, extraRender: slug => RenderTall(slug, 2600));
                Step("usage: pin the Hornet", async () =>
                {
                    vm!.CurrentStage = UiStage.Settings;
                    UsageRankItem? hornet = vm.UsageRankingUnits.FirstOrDefault(u => u.Unit == "FA-18C_hornet");
                    if (hornet == null)
                    {
                        Failures.Add("usage: the Hornet is not in the ranking");
                        return;
                    }
                    await vm.ToggleUsagePinCommand.ExecuteAsync(hornet);
                    Note("first now: " + vm.UsageRankingUnits.FirstOrDefault()?.Title + " (" + vm.UsageRankingUnits.FirstOrDefault()?.PinText + ")");
                    if (vm.UsageRankingUnits.FirstOrDefault()?.Unit != "FA-18C_hornet")
                        Failures.Add("usage: pinning did not move the Hornet first");
                }, extraRender: slug => RenderTall(slug, 2000));
                vm!.StopUsageRecording();
            }
            finally
            {
                Environment.SetEnvironmentVariable("JOYPRO_USAGE_ROOT", oldRoot);
            }
        }
    }
}
