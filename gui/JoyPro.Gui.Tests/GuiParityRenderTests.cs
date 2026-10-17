using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using Xunit;

namespace JoyPro.Gui.Tests;

// Renders the GUI paths of former CLI-only features into PNGs without showing a window, with a fake engine: the Start
// page's DB update notice with per-game "Refresh DB" / "Keep DB", and the Auto Assign panel with a device layout
// (devices kb show), "Only the selected relations" and an explanation. Needs the whole application, so it renders only
// when JOYPRO_GUI_PARITY_PREVIEW names an output folder, and then must run alone:
//   set JOYPRO_GUI_PARITY_PREVIEW=%TEMP%\JoyPro2.Gui.Tests\gui-parity & dotnet test gui/JoyPro.Gui.Tests --filter RenderGuiParityPanels
[SupportedOSPlatform("windows")]
public sealed class GuiParityRenderTests
{
    [Fact]
    public void RenderGuiParityPanels()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_GUI_PARITY_PREVIEW");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);

        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = new(new ParityFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false)
            {
                EnginePath = typeof(MainWindowViewModel).Assembly.Location,
                ProfilePath = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "parity.pr0file")
            };

            // Start page: the DB notice.
            AvaloniaRenderThread.Wait(Done(vm.CheckDbUpdatesCommand.ExecuteAsync(null)));
            Assert.Equal(3, vm.DbUpdateItems.Count);
            vm.CurrentStage = UiStage.Start;
            Render(vm, Path.Combine(outDir, "start-db-updates.png"), "Game updates detected", 420, page =>
            {
                List<Button> buttons = page.GetVisualDescendantsOfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
                Assert.Equal(2, buttons.Count(b => (b.Content as string) == "Refresh DB"));
                Assert.Equal(3, buttons.Count(b => (b.Content as string) == "Keep DB"));
            });

            // Binds page ("Put relations on my buttons automatically"): Auto Assign with a device layout and an explanation.
            AvaloniaRenderThread.Wait(Done(vm.RefreshRelationsCommand.ExecuteAsync(null)));
            vm.CurrentStage = UiStage.Relations;
            AvaloniaRenderThread.Wait(Done(vm.ToggleAutoAssignPanelCommand.ExecuteAsync(null)));
            vm.SetRelationMassSelection(vm.Relations.Where(r => r.Name == "Weapon Release"));
            vm.AutoAssignOnlySelected = true;
            AvaloniaRenderThread.Wait(Done(vm.ShowAutoAssignDeviceLayoutCommand.ExecuteAsync(vm.AutoAssignDevices.Single(d => d.ModelId.Length > 0))));
            AvaloniaRenderThread.Wait(Done(vm.ExplainAutoAssignCommand.ExecuteAsync(null)));
            Assert.True(vm.HasAutoAssignLayout);
            Render(vm, Path.Combine(outDir, "relations-auto-assign.png"), "Auto Assign", 1100, page =>
            {
                List<string> texts = page.GetVisualDescendantsOfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
                Assert.Contains("Thrustmaster HOTAS Warthog Joystick (thrustmaster-warthog-stick)", texts);
                Assert.Contains("stick.hat.tms.up", texts);
                Assert.Contains(page.GetVisualDescendantsOfType<CheckBox>(), c => (c.Content as string) == "Only the selected relations" && c.IsChecked == true);
                Assert.Contains(page.GetVisualDescendantsOfType<Button>(), b => (b.Content as string) == "Explain Selected");
            });
        });
    }

    // The main window on the current stage, cut from the first TextBlock with the given text (or "text: ...") down by height pixels.
    internal static void Render(MainWindowViewModel vm, string file, string anchorText, int height, Action<Control> check)
    {
        MainWindow main = new() { DataContext = vm, Width = 1500, Height = 2600 };
        Size pageSize = new(1500, 2600);
        main.Measure(pageSize);
        main.Arrange(new Rect(pageSize));
        Control page = (Control)main.Content!;
        page.Measure(pageSize);
        page.Arrange(new Rect(pageSize));
        AvaloniaRenderThread.Pump();
        page.Measure(pageSize);
        page.Arrange(new Rect(pageSize));
        check(page);

        TextBlock? anchor = page.GetVisualDescendantsOfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && (t.Text == anchorText || (t.Text ?? string.Empty).StartsWith(anchorText + ":", StringComparison.Ordinal)));
        Assert.NotNull(anchor);
        Point? top = anchor!.TranslatePoint(new Point(0, 0), page);
        Assert.NotNull(top);
        double y = Math.Max(0, top!.Value.Y - 30);
        double x = Math.Max(0, top.Value.X - 30);
        PixelSize size = new((int)Math.Min(1500 - x, 1440), (int)Math.Min(2600 - y, height));
        using RenderTargetBitmap whole = new(new PixelSize(1500, 2600));
        using (DrawingContext context = whole.CreateDrawingContext())
            context.FillRectangle(new SolidColorBrush(Color.Parse("#202020")), new Rect(pageSize));
        whole.Render(page);
        using RenderTargetBitmap cut = new(size);
        using (DrawingContext context = cut.CreateDrawingContext())
            context.DrawImage(whole, new Rect(x, y, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
        cut.Save(file);
        Assert.True(new FileInfo(file).Length > 1000);
    }

    internal static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private sealed class ParityFakeEngine : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EngineCommandResult
            {
                ExitCode = 0,
                Duration = TimeSpan.FromMilliseconds(1),
                StandardOutput = Output(arguments),
                StandardError = string.Empty,
                Arguments = arguments.ToArray()
            });
        }

        private static string Output(IReadOnlyList<string> args)
        {
            if (AxisTuningTests.Seq(args, "db", "check-updates"))
            {
                return "{\"Games\":[" +
                       "{\"Game\":\"DCS\",\"DisplayName\":\"DCS World\",\"State\":\"game-updated\",\"Message\":\"DCS World was updated since its DB was built (2.9.20 -> 2.9.21).\",\"RefreshCommand\":\"engine dcs refresh id\",\"Details\":[\"new module(s): F-4E\"]}," +
                       "{\"Game\":\"WarThunder\",\"DisplayName\":\"War Thunder\",\"State\":\"game-updated\",\"Message\":\"War Thunder was updated since its DB was built.\",\"RefreshCommand\":\"engine warthunder refresh-db\",\"Details\":[]}," +
                       "{\"Game\":\"NoRefreshGame\",\"DisplayName\":\"Game Without Refresh\",\"State\":\"game-updated\",\"Message\":\"Game Without Refresh was updated since its DB was built.\",\"RefreshCommand\":\"update JoyPro\",\"Details\":[]}" +
                       "],\"Outdated\":3,\"Warnings\":[]}";
            }
            if (AxisTuningTests.Seq(args, "relations", "list"))
            {
                return "{\"Relations\":[" +
                       "{\"Name\":\"Weapon Release\",\"IsAxis\":false,\"Groups\":[\"Weapons\"],\"Nodes\":[{\"Id\":\"d3001\",\"Game\":\"DCS\",\"Aircraft\":{\"F-16C_50\":true}}],\"Bind\":null}," +
                       "{\"Name\":\"TMS Up\",\"IsAxis\":false,\"Groups\":[\"Sensors\"],\"Nodes\":[{\"Id\":\"d3002\",\"Game\":\"DCS\",\"Aircraft\":{\"F-16C_50\":true}}],\"Bind\":null}]}";
            }
            if (AxisTuningTests.Seq(args, "devices", "kb", "identify"))
            {
                return "[{\"Device\":\"Joystick - HOTAS Warthog {00000000-0000-0000-0000-0000000000a1}\",\"Connected\":true,\"Model\":\"Thrustmaster HOTAS Warthog Joystick\",\"ModelId\":\"thrustmaster-warthog-stick\",\"Kind\":\"stick\",\"Generic\":false,\"Note\":\"\",\"AlternativeTo\":\"\",\"GripChoices\":[]}," +
                       "{\"Device\":\"Throttle - HOTAS Warthog {00000000-0000-0000-0000-0000000000a2}\",\"Connected\":true,\"Model\":\"\",\"ModelId\":\"\",\"Kind\":\"other\",\"Generic\":true,\"Note\":\"\",\"AlternativeTo\":\"\",\"GripChoices\":[]}]";
            }
            if (AxisTuningTests.Seq(args, "devices", "kb", "show"))
            {
                return "{\"Id\":\"thrustmaster-warthog-stick\",\"Name\":\"Thrustmaster HOTAS Warthog Joystick\",\"Vendor\":\"Thrustmaster\",\"Kind\":\"stick\",\"Hand\":\"right\",\"Base\":\"\",\"Image\":\"\",\"Confidence\":\"high\",\"VidPids\":[\"044F:0402\"]," +
                       "\"Axes\":[{\"Input\":\"JOY_X\",\"Control\":\"stick.axis.roll\",\"Label\":\"\",\"Type\":\"momentary\",\"Finger\":\"\"},{\"Input\":\"JOY_Y\",\"Control\":\"stick.axis.pitch\",\"Label\":\"\",\"Type\":\"momentary\",\"Finger\":\"\"}]," +
                       "\"Controls\":[{\"Input\":\"JOY_BTN1\",\"Control\":\"stick.trigger.stage1\",\"Label\":\"Trigger\",\"Type\":\"momentary\",\"Finger\":\"index\"}," +
                       "{\"Input\":\"JOY_BTN2\",\"Control\":\"stick.weapon_release\",\"Label\":\"Weapon release\",\"Type\":\"momentary\",\"Finger\":\"thumb\"}," +
                       "{\"Input\":\"JOY_BTN7\",\"Control\":\"stick.hat.tms.up\",\"Label\":\"TMS up\",\"Type\":\"momentary\",\"Finger\":\"thumb\"}]}";
            }
            if (AxisTuningTests.Seq(args, "relations", "auto-assign"))
            {
                return string.Join(Environment.NewLine,
                    "Auto Assign (dry run)",
                    "Assigned: 1, kept: 0, unassigned: 0",
                    "Assignments:",
                    "  - Weapon Release => Joystick - HOTAS Warthog {...a1} JOY_BTN2 (stick.weapon_release \"Weapon release\")",
                    "Explain:",
                    "  Weapon Release: slot weapon_release; best candidates:",
                    "    JOY_BTN2 stick.weapon_release score 100 (free)",
                    "    JOY_BTN1 stick.trigger.stage1 score 62 (free)");
            }
            return "ok";
        }
    }
}
