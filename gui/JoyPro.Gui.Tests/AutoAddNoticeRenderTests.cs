using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Styling;
using JoyPro.Gui.Localization;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.Tests.QualityGate;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// The notice about aircraft JoyPro added, on a copy of a real profile and its history with the real engine, rendered
// offscreen in Light and Dark with the list closed and open (and at its end, where the input layers are), and in German.
// Opt-in (a real profile): JOYPRO_AUTOADD_NOTICE_PROFILE=<.pr0file> (copied, never changed),
// JOYPRO_AUTOADD_NOTICE_HISTORY=<its history folder> (copied) and JOYPRO_AUTOADD_NOTICE_OUT=<folder> for the pictures;
// dotnet test --filter AutoAddNoticeRenderTests
[SupportedOSPlatform("windows")]
[Collection("EngineServer")]
public sealed class AutoAddNoticeRenderTests
{
    [Fact]
    public void RenderNotice_OnARealProfileCopy()
    {
        string? source = Environment.GetEnvironmentVariable("JOYPRO_AUTOADD_NOTICE_PROFILE");
        string? history = Environment.GetEnvironmentVariable("JOYPRO_AUTOADD_NOTICE_HISTORY");
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_AUTOADD_NOTICE_OUT");
        string? engine = DuplicateFixture.EnginePath();
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || string.IsNullOrWhiteSpace(history) || !Directory.Exists(history) || string.IsNullOrWhiteSpace(outDir) || engine == null)
            return;
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "autoadd-notice-" + Guid.NewGuid().ToString("N"));
        Dictionary<string, string?> previous = new();
        string language = Loc.Current.Code;
        try
        {
            Directory.CreateDirectory(root);
            string profile = Path.Combine(root, Path.GetFileName(source));
            File.Copy(source, profile);
            Dictionary<string, string> environment = new()
            {
                ["JOYPRO_DATA_DIR"] = Path.Combine(root, "data"),
                ["JOYPRO_TEST_SANDBOX"] = root,
                ["JOYPRO_TEST_IGNORE_RUNNING_GAMES"] = "1",
                ["JOYPRO_DEVICE_KB"] = "off",
                ["JOYPRO_PROFILE_HISTORY"] = "on",
                ["JOYPRO_ENGINE_SERVER"] = "off"
            };
            foreach (KeyValuePair<string, string> variable in environment)
            {
                previous[variable.Key] = Environment.GetEnvironmentVariable(variable.Key);
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
            CopyHistory(history, profile);
            Directory.CreateDirectory(outDir);

            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new EngineCliClient(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine, ProfilePath = profile };
                using HiddenWindow window = HiddenWindow.Create(new MainWindow { DataContext = vm }, new Size(1280, 800));
                AvaloniaRenderThread.Wait(vm.RefreshRelationsCommand.ExecuteAsync(null));
                vm.CurrentStage = UiStage.Relations;
                AvaloniaRenderThread.Wait(vm.CheckAutoAddRepairAsync());
                Assert.True(vm.HasAutoAddRepairNotice, "no notice on this profile");
                Assert.DoesNotContain(vm.AutoAddRepairItems, i => i.Text.Contains("('Default", StringComparison.Ordinal));
                Assert.DoesNotContain(vm.AutoAddRepairItems, i => i.Text.Contains("ground_unit", StringComparison.Ordinal));

                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    vm.IsAutoAddRepairListOpen = false;
                    window.Settle();
                    GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "notice-closed-" + theme + ".png"));
                    vm.IsAutoAddRepairListOpen = true;
                    window.Settle();
                    GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "notice-list-" + theme + ".png"));
                }

                // The end of the list: only its last rows, so the input layers show without scrolling.
                List<DuplicateChangeItem> all = vm.AutoAddRepairItems.ToList();
                foreach (DuplicateChangeItem item in all.Take(all.Count - 12))
                    vm.AutoAddRepairItems.Remove(item);
                window.Settle();
                GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "notice-list-end-dark.png"));
                Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
                window.Settle();
                GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "notice-list-end-light.png"));

                // The same words in German.
                Loc.SetLanguage("de");
                AvaloniaRenderThread.Wait(vm.CheckAutoAddRepairAsync());
                vm.IsAutoAddRepairListOpen = false;
                window.Settle();
                GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "notice-closed-light-de.png"));

                // The page behind it without any notice.
                Loc.SetLanguage("en");
                vm.DismissAutoAddRepairCommand.Execute(null);
                window.Settle();
                GateRender.Save(window, 1280, 800, 1, Path.Combine(outDir, "no-notice-light.png"));
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            });
        }
        finally
        {
            Loc.SetLanguage(language);
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
        System.Text.Json.Nodes.JsonNode? json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(log));
        if (json != null)
        {
            json["profilePath"] = profile;
            File.WriteAllText(log, json.ToJsonString());
        }
    }
}
