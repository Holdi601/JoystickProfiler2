using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.VisualTree;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using Xunit;

namespace JoyPro.Gui.Tests;

// Settings > Report a problem > Create Report (MainWindowViewModel.AppServices.cs, docs/logging.md "Support
// file"): the box "Include my open profile and its history" is on by default and survives a restart
// (ui-settings.json supportIncludeProfile), and the engine call passes it (--include-profile, --profile only when the box
// is on and a profile is open). The confirmation is answered "no": nothing is written and no Explorer opens. Uses the
// test run's own settings folder in the "TrayMode" collection that owns it.
[SupportedOSPlatform("windows")]
[Collection("TrayMode")]
public sealed class SupportFileTests : IDisposable
{
    private readonly string uiSettings = Path.Combine(AppInfo.SettingsFolder, "ui-settings.json");

    public SupportFileTests()
    {
        Directory.CreateDirectory(AppInfo.SettingsFolder);
        File.Delete(uiSettings);
    }

    public void Dispose()
    {
        File.Delete(uiSettings);
    }

    private static MainWindowViewModel Vm(NoviceGuiTests.NoviceFakeEngine engine, bool persist) =>
        new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: persist) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };

    [Fact]
    public void IncludeProfileBox_IsOnByDefault_AndComesBackAfterARestart()
    {
        MainWindowViewModel first = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.True(first.SupportIncludeProfile);
        first.SupportIncludeProfile = false;
        Assert.Contains("\"supportIncludeProfile\": false", File.ReadAllText(uiSettings), StringComparison.OrdinalIgnoreCase);

        MainWindowViewModel second = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.False(second.SupportIncludeProfile);
        second.SupportIncludeProfile = true;

        MainWindowViewModel third = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: true);
        Assert.True(third.SupportIncludeProfile);
    }

    [Fact]
    public async Task CreateSupportFile_PassesTheBox_AndTheOpenProfileOnlyWhenTicked()
    {
        NoviceGuiTests.NoviceFakeEngine engine = new();
        MainWindowViewModel vm = Vm(engine, persist: false);
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string profile = Path.Combine(folder, "pilot.pr0file");
        File.WriteAllText(profile, "{}");
        vm.ProfilePath = profile;
        List<string> asked = [];
        vm.ConfirmHandler = request =>
        {
            asked.Add(request.Message);
            return Task.FromResult(false);
        };

        // Ticked (the default): --profile <open profile> --include-profile true.
        await vm.CreateProblemReportCommand.ExecuteAsync(null);
        IReadOnlyList<string> ticked = engine.Calls.Last(c => c.Contains("support"));
        Assert.True(Sequence(ticked, "support", "bundle", "--list"), string.Join(" ", ticked));
        Assert.True(Sequence(ticked, "--include-profile", "true"), string.Join(" ", ticked));
        Assert.True(Sequence(ticked, "--profile", profile), string.Join(" ", ticked));
        Assert.Single(asked);

        // Unticked: --include-profile false and no profile.
        vm.SupportIncludeProfile = false;
        await vm.CreateProblemReportCommand.ExecuteAsync(null);
        IReadOnlyList<string> unticked = engine.Calls.Last(c => c.Contains("support"));
        Assert.True(Sequence(unticked, "--include-profile", "false"), string.Join(" ", unticked));
        Assert.DoesNotContain("--profile", unticked);

        // The save call is the same with --out.
        vm.SupportIncludeProfile = true;
        List<string> save = vm.SupportBundleArguments(false, @"C:\Reports\JoyPro2-support.zip");
        Assert.Equal(["--out", @"C:\Reports\JoyPro2-support.zip", "support", "bundle", "--include-profile", "true"], save);
        Assert.DoesNotContain(engine.Calls, c => c.Contains("--out"));
    }

    [Fact]
    public void SupportFileEntries_ShowsTheParts_AndWhatWasTrimmed()
    {
        string output = "Support file contents (not written)\nEntries: 3, 10 KB before compression\nProfile: pilot.pr0file (9 KB), history: 3 version(s), 3 in this file\nParts:\n" +
                        "  + logs | JoyPro's logs | 15 file(s) | 19.7 MB\n  + profile | the open profile and its history | 8 file(s) | 14.1 KB\n" +
                        "Trimmed or left out:\n  ! logs: 2 older log file(s) left out\n  - logs/engine.log | JoyPro log | 1234 bytes\n";
        Assert.Equal(["- JoyPro's logs: 15 file(s), 19.7 MB", "- the open profile and its history: 8 file(s), 14.1 KB", "  logs: 2 older log file(s) left out"],
                     MainWindowViewModel.SupportFileEntries(output));
        Assert.Equal(["logs/engine.log (JoyPro log)"], MainWindowViewModel.SupportFileEntries("  - logs/engine.log | JoyPro log | 1234 bytes\n"));
    }

    // The looks of Settings > Report a problem with the support file's contents and the box. Only with
    // JOYPRO_UI_DESIGN_OUT set: PNGs support-file-<width>-<light|dark>.png. In a hidden real window (HiddenWindow: Avalonia's
    // own layout passes, never on the desktop), with the search typed before the page is shown: the plain offscreen render
    // (UiDesignRenderTests.RenderWindow) measures the page by hand and kept the section at the height it had before the
    // section texts were read (0, it did not match yet), which showed as an empty page.
    [Fact]
    public void ReportAProblemSection_Renders()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_UI_DESIGN_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        MainWindowViewModel vm = Vm(new NoviceGuiTests.NoviceFakeEngine(), persist: false);
        vm.ProblemReportStatus = "Done. The report is saved as JoyPro2-report-20261026-170635.zip in Documents\\JoyPro2 Problem Reports (Explorer shows it). Nothing was sent.";
        AvaloniaRenderThread.Run(() =>
        {
            vm.CurrentStage = JoyPro.Gui.Models.UiStage.Settings;
            vm.CollapseAllSettingsCommand.Execute(null);
            vm.OpenSettingsSectionCommand.Execute("report");
            vm.SettingsSearchText = "Include my open profile";
            AvaloniaRenderThread.Pump();
            foreach ((Avalonia.Styling.ThemeVariant theme, string name) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = theme;
                foreach ((int w, int h) in new[] { (2267, 1250), (1280, 800) })
                {
                    using HiddenWindow host = HiddenWindow.Create(new JoyPro.Gui.Views.MainWindow { DataContext = vm }, new Avalonia.Size(w, h));
                    host.Settle();
                    host.Settle();
                    Avalonia.Controls.ScrollViewer scroller = host.Page.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(s => s.Name == "SettingsScrollViewer");
                    Avalonia.Controls.Control section = ((JoyPro.Gui.Views.Controls.SettingsSectionsPanel)scroller.Content!).OrderedChildren().First(c => c.IsVisible);
                    double gap = section.TranslatePoint(new Avalonia.Point(0, 0), scroller)!.Value.Y;
                    Assert.True(gap >= 0 && gap <= 16 && section.Bounds.Height > 100, w + " " + name + ": the section is at " + gap.ToString("0") + ", " + section.Bounds.Height.ToString("0") + " px high");
                    using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = host.Render();
                    bitmap.Save(Path.Combine(outDir, "support-file-" + w + "-" + name + ".png"));
                }
            }
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        });
    }

    // The one flow: Create Report saves the engine's support file and adds report.txt (the text and the diagnostics)
    // and this session's engine output to it; Show Report / Open GitHub Issue Page then work on that zip. The engine is
    // a fake that writes a small zip; Explorer is not started.
    [Fact]
    public async Task CreateReport_IsTheSupportFileWithTheText()
    {
        BundleFakeEngine engine = new();
        MainWindowViewModel vm = new(engine, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        vm.ProblemDescription = "Export to DCS lost my binds";
        vm.ConfirmHandler = _ => Task.FromResult(true);
        List<string> started = [];
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> original = ExternalProcess.Start;
        ExternalProcess.Start = info => { started.Add(info.FileName); return null; };
        try
        {
            await vm.CreateProblemReportCommand.ExecuteAsync(null);
        }
        finally
        {
            ExternalProcess.Start = original;
        }
        Assert.True(vm.HasProblemReport, vm.ProblemReportStatus);
        Assert.StartsWith("JoyPro2-report-", Path.GetFileName(vm.LastProblemReportPath), StringComparison.Ordinal);
        Assert.Contains(engine.Calls, c => c.Contains("--out") && c.Contains("bundle") && c.Contains("--include-profile"));
        using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(vm.LastProblemReportPath))
        {
            Assert.NotNull(zip.GetEntry("summary.txt"));
            using StreamReader reader = new(zip.GetEntry("report.txt")!.Open());
            string report = reader.ReadToEnd();
            Assert.Contains("Export to DCS lost my binds", report, StringComparison.Ordinal);
            Assert.Contains("- JoyPro's logs: 2 file(s), 1.0 KB", report, StringComparison.Ordinal);
        }
        Assert.Equal(["explorer.exe"], started);
        File.Delete(vm.LastProblemReportPath);
    }

    // Answers support bundle --list with two parts and writes a zip with summary.txt for --out.
    private sealed class BundleFakeEngine : IEngineCliClient
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, System.Threading.CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(arguments.ToArray());
            string output = string.Empty;
            int bundle = arguments.ToList().IndexOf("bundle");
            if (bundle > 0 && arguments.Contains("--list"))
                output = "Support file contents (not written)\nParts:\n  + logs | JoyPro's logs | 2 file(s) | 1.0 KB\n  + summary | summary.txt | 1 file(s) | 0 B\n";
            else if (bundle > 0)
            {
                string zip = arguments[arguments.ToList().IndexOf("--out") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
                using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
                using (StreamWriter writer = new(archive.CreateEntry("summary.txt").Open()))
                    writer.Write("JoyPro2 support file");
                output = "{}";
            }
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, StandardError = string.Empty, Arguments = arguments.ToArray() });
        }
    }

    private static bool Sequence(IReadOnlyList<string> args, params string[] sequence)
    {
        for (int i = 0; i + sequence.Length <= args.Count; i++)
        {
            if (sequence.Select((s, j) => string.Equals(args[i + j], s, StringComparison.Ordinal)).All(b => b))
                return true;
        }
        return false;
    }
}
