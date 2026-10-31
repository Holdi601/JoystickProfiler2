using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The 2.5.0 tester batch (reports 261028_0001 and _0002): the HUD stick element of a grip without X/Y on its base, the
// picture picked on the Layout page kept at once, the marker assistant's place, the engine output's height, hidden
// games without game chips, the problem reports' new folder and the first-export question's primary button.
[Collection("ProcessEnvironment")]
[SupportedOSPlatform("windows")]
public sealed class TesterBatch250Tests
{
    internal const string Grip = "VKB-Sim Gunfighter Modern Combat Pro Twist {5e7b0c10-0000-0000-0000-000000000001}";
    internal const string Base = "VPforce Rhino FFB Joystick {5e7b0c10-0000-0000-0000-000000000002}";
    internal const string Throttle = "LEFT VPC Throttle MT-50 CM {5e7b0c10-0000-0000-0000-000000000004}";

    // ---- HUD: a grip without X/Y on its base -------------------------------------------------------------------------

    // "hud devices" of the user's set: the VKB grip reports no X/Y (its X/Y sensors are in the Rhino base), the Rhino
    // reports X/Y and a few buttons, a left throttle with a thumb stick.
    internal sealed class GripOnBaseEngine : IEngineCliClient
    {
        public Task<EngineCommandResult> RunAsync(string engineExePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string output = arguments.Contains("hud") && arguments.Contains("devices")
                ? "{\"Devices\":[" +
                  "{\"Device\":\"" + Grip + "\",\"Name\":\"VKB-Sim Gunfighter Modern Combat Pro Twist\",\"Model\":\"VKB Gunfighter MCG Pro\",\"ModelId\":\"vkb-mcg-pro\",\"Kind\":\"stick\",\"Hand\":\"right\",\"Image\":\"\",\"Buttons\":64,\"Hats\":2," +
                  "\"Axes\":[{\"Input\":\"JOY_RZ\",\"Control\":\"stick.axis.twist\",\"Label\":\"Twist\"},{\"Input\":\"JOY_RX\",\"Control\":\"stick.axis.ministick.x\",\"Label\":\"Thumb X\"}]," +
                  "\"Labels\":{\"JOY_BTN1\":\"Trigger\"},\"GripChoices\":[],\"Layout\":null}," +
                  "{\"Device\":\"" + Base + "\",\"Name\":\"VPforce Rhino FFB Joystick\",\"Model\":\"VPforce Rhino\",\"ModelId\":\"vpforce-rhino\",\"Kind\":\"base\",\"Hand\":\"\",\"Image\":\"\",\"Buttons\":5,\"Hats\":0," +
                  "\"Axes\":[{\"Input\":\"JOY_X\",\"Control\":\"base.axis.roll\",\"Label\":\"Roll\"},{\"Input\":\"JOY_Y\",\"Control\":\"base.axis.pitch\",\"Label\":\"Pitch\"}],\"Labels\":{},\"GripChoices\":[],\"Layout\":null}," +
                  "{\"Device\":\"" + Throttle + "\",\"Name\":\"LEFT VPC Throttle MT-50 CM\",\"Model\":\"MT-50 CM\",\"ModelId\":\"mt50cm\",\"Kind\":\"throttle\",\"Hand\":\"left\",\"Image\":\"\",\"Buttons\":64,\"Hats\":0," +
                  "\"Axes\":[{\"Input\":\"JOY_Z\",\"Control\":\"throttle.axis.left\",\"Label\":\"Left throttle\"},{\"Input\":\"JOY_X\",\"Control\":\"throttle.axis.ministick.x\",\"Label\":\"Thumb X\"}],\"Labels\":{},\"GripChoices\":[],\"Layout\":null}" +
                  "],\"Warnings\":[]}"
                : "{}";
            return Task.FromResult(new EngineCommandResult { ExitCode = 0, Duration = TimeSpan.FromMilliseconds(1), StandardOutput = output, Arguments = arguments.ToArray() });
        }
    }

    internal static async Task<MainWindowViewModel> HudVmAsync(bool persisted = false)
    {
        MainWindowViewModel vm = new(new GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: persisted) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        await vm.LoadHudDevicesCommand.ExecuteAsync(null);
        return vm;
    }

    private static double[] Axes(double x = 0.5, double y = 0.5, double z = 0.5, double rx = 0.5, double ry = 0.5, double rz = 0.5) => [x, y, z, rx, ry, rz, 0.5, 0.5];

    [Fact]
    public async Task Hud_GripWithoutXY_TakesThemFromItsBase_AndIsCentredMeanwhile()
    {
        MainWindowViewModel vm = await HudVmAsync();
        HudWidget gripWidget = vm.HudWidgets.Single(w => w.Device?.Device == Grip);
        HudDevice grip = gripWidget.Device!;
        HudDevice rhino = vm.HudWidgets.Single(w => w.Device?.Device == Base).Device!;
        Assert.Same(rhino, grip.PairedBase);
        Assert.True(grip.HasGimbal);
        Assert.True(gripWidget.GimbalVisible);
        // Nothing reported yet: the middle, never a corner.
        Assert.Equal(0, grip.GimbalX);
        Assert.Equal(0, grip.GimbalY);

        // The grip reports zeros where it has no axes: still the middle.
        vm.OnHudAxes(new JoystickAxisSnapshot(Grip, [0, 0, 0, 0.5, 0.5, 0.5, 0, 0]));
        Assert.Equal(0, grip.GimbalX);
        Assert.Equal(0, grip.GimbalY);

        // The base moves: the grip's stick element follows it (axes from the base, buttons from the grip).
        vm.OnHudAxes(new JoystickAxisSnapshot(Base, Axes()));
        vm.OnHudAxes(new JoystickAxisSnapshot(Base, Axes(x: 1.0, y: 0.25)));
        Assert.Equal(HudDevice.GimbalRadius, grip.GimbalX);
        Assert.Equal(-HudDevice.GimbalRadius / 2, grip.GimbalY);
        Assert.Equal(18, grip.Roll);
        vm.OnHudInput(new JoystickInputEvent(Grip, "JOY_BTN1", true));
        Assert.Equal("Trigger", grip.PressedText);

        // Twist is the grip's own.
        vm.OnHudAxes(new JoystickAxisSnapshot(Grip, [0, 0, 0, 0.5, 0.5, 1.0, 0, 0]));
        Assert.Equal(90, grip.TwistTick);
        Assert.Equal(HudDevice.GimbalRadius, grip.GimbalX);
        vm.SelectedHudWidget = gripWidget;
        Assert.Contains("VPforce Rhino", vm.HudAxisAutomaticText);
    }

    [Fact]
    public void Hud_DeadAxisStuckAtAnEnd_IsCentred_TheBaseTakesOver()
    {
        // A grip that reports X/Y (declared, without sensors: 0 for ever) and a base with live X/Y.
        HudDevice grip = new(Grip, "Grip", "Grip", "stick", 64, 0, [("JOY_X", "stick.axis.roll", "Roll"), ("JOY_Y", "stick.axis.pitch", "Pitch")], new Dictionary<string, string>(), []);
        HudDevice rhino = new(Base, "Rhino", "Rhino", "base", 5, 0, [("JOY_X", "base.axis.roll", "Roll"), ("JOY_Y", "base.axis.pitch", "Pitch")], new Dictionary<string, string>(), []);
        Assert.Same(rhino, MainWindowViewModel.PairedHudBase(grip, [grip, rhino]));
        Assert.Null(MainWindowViewModel.PairedHudBase(rhino, [grip, rhino]));
        grip.SetAxisSources(rhino, (null, -1), (null, -1), (null, -1));
        rhino.AddAxisDependent(grip);
        grip.ApplyAxes([0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(0, grip.GimbalX);
        Assert.Equal(0, grip.GimbalY);
        rhino.ApplyAxes(Axes());
        rhino.ApplyAxes(Axes(x: 0.0, y: 1.0));
        Assert.Equal(-HudDevice.GimbalRadius, grip.GimbalX);
        Assert.Equal(HudDevice.GimbalRadius, grip.GimbalY);
        // Without a base: still the middle.
        grip.SetAxisSources(null, (null, -1), (null, -1), (null, -1));
        Assert.Equal(0, grip.GimbalX);
        Assert.False(grip.AxisLive(0));
    }

    [Fact]
    public async Task Hud_PickedAxisSource_DrivesTheElement_AndSurvivesARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "hud-axes-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            MainWindowViewModel first = await HudVmAsync(persisted: true);
            HudWidget gripWidget = first.HudWidgets.Single(w => w.Device?.Device == Grip);
            first.SelectedHudWidget = gripWidget;
            Assert.Equal(MainWindowViewModel.HudAxisAutomatic, first.SelectedHudAxisX!.Id);
            HudChoice thumb = first.HudAxisChoices.Single(c => c.Id == Throttle + "|JOY_X");
            first.SelectedHudAxisX = thumb;
            Assert.Equal(Throttle + "|JOY_X", gripWidget.Config.AxisX);
            HudDevice grip = gripWidget.Device!;
            first.OnHudAxes(new JoystickAxisSnapshot(Throttle, Axes()));
            first.OnHudAxes(new JoystickAxisSnapshot(Throttle, Axes(x: 0.0)));
            Assert.Equal(-HudDevice.GimbalRadius, grip.GimbalX);
            // Y stays Automatic (the base).
            first.OnHudAxes(new JoystickAxisSnapshot(Base, Axes()));
            first.OnHudAxes(new JoystickAxisSnapshot(Base, Axes(x: 1.0, y: 1.0)));
            Assert.Equal(-HudDevice.GimbalRadius, grip.GimbalX);
            Assert.Equal(HudDevice.GimbalRadius, grip.GimbalY);
            await Task.Delay(900);

            MainWindowViewModel second = await HudVmAsync(persisted: true);
            HudWidget again = second.HudWidgets.Single(w => w.Device?.Device == Grip);
            Assert.Equal(Throttle + "|JOY_X", again.AxisX);
            second.SelectedHudWidget = again;
            Assert.Equal(thumb.Id, second.SelectedHudAxisX!.Id);
            Assert.Equal(MainWindowViewModel.HudAxisAutomatic, second.SelectedHudAxisY!.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // The performance rule (HUD, overlay, watcher run while the user plays): an axis report allocates nothing.
    [Fact]
    public async Task Hud_AxisReports_AllocateNothing()
    {
        MainWindowViewModel vm = await HudVmAsync();
        HudDevice rhino = vm.HudWidgets.Single(w => w.Device?.Device == Base).Device!;
        HudDevice grip = vm.HudWidgets.Single(w => w.Device?.Device == Grip).Device!;
        double[] a = Axes(x: 0.2, y: 0.7), b = Axes(x: 0.8, y: 0.3);
        for (int i = 0; i < 100; i++)
            rhino.ApplyAxes(i % 2 == 0 ? a : b);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
            rhino.ApplyAxes(i % 2 == 0 ? a : b);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4096, "10 000 axis reports allocated " + allocated + " bytes");
        Assert.NotEqual(0, grip.GimbalX);
    }

    // ---- Layout: the picked picture is kept at once -------------------------------------------------------------------

    [Fact]
    public void LayoutPicture_PickedPhoto_IsKeptAtOnce_AndComesBack()
    {
        string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "picture-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        try
        {
            const string model = "virpil-mongoost-50cm3-throttle";
            const string device = "VPC MongoosT-50CM3 {072C2740-0D8D-11f1-8021-444553540000}";
            DevicePhotoConsent.Answer(model, "yes");
            Dictionary<string, string> photos = new();
            Task<string?> Fetch(string url)
            {
                if (!photos.TryGetValue(url, out string? file))
                    photos[url] = file = WritePhoto(Path.Combine(temp, "photo-" + photos.Count + ".png"), photos.Count);
                return Task.FromResult<string?>(file);
            }
            AvaloniaRenderThread.Run(() =>
            {
                DeviceReferenceViewModel first = new() { FetchVendorPhoto = Fetch };
                AvaloniaRenderThread.Wait(first.ShowModelAsync(model, device));
                List<ReferencePictureOption> vendor = first.Pictures.Where(p => p.Kind == "vendor").ToList();
                Assert.True(vendor.Count >= 2, "the MongoosT-50CM3 has several photos: " + string.Join(", ", first.Pictures.Select(p => p.Title)));
                ReferencePictureOption pick = vendor[^1];
                Assert.NotSame(pick, first.SelectedPicture);
                first.SelectedPicture = pick;
                AvaloniaRenderThread.Wait(first.LastPictureChoice);
                Assert.Same(pick, first.SelectedPicture);
                Assert.Equal(DeviceReferenceViewModel.PictureId(pick), DeviceReferenceService.PictureChoiceOf(device, model));
                Assert.StartsWith(pick.Title + " is kept for this device.", first.Status);
                Assert.False(first.HasUnsavedChanges);

                // Shown again (a refresh, the next start): the picked photo, not JoyPro's line drawing.
                DeviceReferenceViewModel second = new() { FetchVendorPhoto = Fetch };
                AvaloniaRenderThread.Wait(second.ShowModelAsync(model, device));
                Assert.Equal("vendor", second.PictureKind);
                Assert.Equal(pick.Title, second.SelectedPicture?.Title);
                // The same device under a new GUID still has it; another model shown for it does not take it.
                Assert.Equal(DeviceReferenceViewModel.PictureId(pick), DeviceReferenceService.PictureChoiceOf("VPC MongoosT-50CM3 {00000000-0000-0000-0000-000000000099}", model));
                Assert.Null(DeviceReferenceService.PictureChoiceOf(device, "virpil-mongoost-50cm2-throttle"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            DeviceReferenceService.CatalogFolderOverride = () => null;
            DeviceReferenceService.Reload();
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static string WritePhoto(string file, int variant)
    {
        using SKBitmap bitmap = new(400, 300);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.White);
            using SKPaint paint = new() { Color = new SKColor((byte)(30 + variant * 40), 40, 60) };
            canvas.DrawRect(new SKRect(80 + variant * 10, 30, 320, 270), paint);
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(file, data.ToArray());
        return file;
    }

    // ---- Marker assistant, engine output -----------------------------------------------------------------------------

    [Fact]
    public void MarkerAssistant_StaysInsideThePicture()
    {
        Size host = new(900, 600), panel = new(400, 260);
        Assert.Equal((0d, 0d), MainWindow.ClampMarkerAssistant(-50, -10, host, panel));
        Assert.Equal((900 - 400 - 24d, 600 - 260 - 24d), MainWindow.ClampMarkerAssistant(5000, 5000, host, panel));
        Assert.Equal((120d, 80d), MainWindow.ClampMarkerAssistant(120, 80, host, panel));
    }

    [Fact]
    public void MarkerAssistantPlace_IsKeptOverARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "assistant-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", folder);
        try
        {
            MainWindowViewModel first = new(new GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(0, first.MarkerAssistantOffsetX);
            first.SetMarkerAssistantOffset(310.4, 95.6, keep: true);
            MainWindowViewModel second = new(new GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: true);
            Assert.Equal(310, second.MarkerAssistantOffsetX);
            Assert.Equal(96, second.MarkerAssistantOffsetY);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previous);
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData(800, 0, 176)]
    [InlineData(1300, 0, 260)]
    [InlineData(500, 0, 150)]
    [InlineData(900, 30, MainWindow.EngineOutputMinHeight)]
    [InlineData(900, 2000, 540)]
    [InlineData(900, 300, 300)]
    public void EngineOutput_HeightIsNeverSqueezed(double window, double wanted, double expected) =>
        Assert.Equal(expected, MainWindow.EngineOutputRowHeight(window, wanted), 3);

    [Fact]
    public void EngineOutput_FillsItsRow_AndFollowsTheDraggedHeight()
    {
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(UiDesignRenderTests.CreateLoadedVmAsync());
            try
            {
                MainWindow window = new() { DataContext = vm };
                using HiddenWindow main = HiddenWindow.Create(window, new Size(1280, 800));
                vm.EngineOutputExpanded = true;
                window.SetEngineOutputHeight(0);
                main.Settle();
                TextBox output = window.FindControl<TextBox>("EngineOutputTextBox")!;
                Border border = window.FindControl<Border>("EngineOutputBorder")!;
                Assert.True(border.Bounds.Height >= MainWindow.EngineOutputMinHeight - 1, "output panel " + border.Bounds.Height);
                Assert.True(output.Bounds.Height >= 60, "output text " + output.Bounds.Height);
                double small = output.Bounds.Height;
                window.SetEngineOutputHeight(380);
                main.Settle();
                Assert.InRange(border.Bounds.Height, 370, 390);
                Assert.True(output.Bounds.Height > small + 100, "the text grows with the panel: " + small + " -> " + output.Bounds.Height);
                main.Snap("engine-output-380.png");
            }
            finally
            {
                vm.StopGameConfigWatcher();
            }
        });
    }

    // ---- Hidden games, problem reports, the first-export question ----------------------------------------------------

    [Fact]
    public void HiddenGames_HaveNoGameChip_UntilShownAgain()
    {
        HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase) { "WarThunder" };
        ListFilterViewModel filter = new("relations") { IsGameHidden = hidden.Contains };
        filter.SetSubjects(
        [
            new FilterSubject("Gear", ["<none>"], ["DCS|F-16C_50", "WarThunder|WarThunder"], string.Empty, false, false, "gear"),
            new FilterSubject("Flaps", ["<none>"], ["WarThunder|WarThunder"], string.Empty, false, false, "flaps")
        ]);
        Assert.Equal(["DCS"], filter.Games.Select(g => g.Game));
        hidden.Clear();
        filter.RefreshHiddenGames();
        Assert.Equal(["DCS", "WarThunder"], filter.Games.Select(g => g.Game).OrderBy(g => g));
    }

    [Fact]
    public void ProblemReports_MoveIntoTheJoyPro2Folder_KeepingWhatIsThereAlready()
    {
        string root = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "reports-" + Guid.NewGuid().ToString("N"));
        string old = Path.Combine(root, "JoyPro2 Problem Reports");
        string target = Path.Combine(root, "JoyPro2", "Problem Reports");
        Directory.CreateDirectory(Path.Combine(old, "sub"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(old, "JoyPro2-report-1.zip"), "one");
        File.WriteAllText(Path.Combine(old, "JoyPro2-report-2.zip"), "old two");
        File.WriteAllText(Path.Combine(old, "sub", "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(target, "JoyPro2-report-2.zip"), "new two");
        try
        {
            (int moved, int kept) = ProblemReporter.MigrateFolder(old, target);
            Assert.Equal(2, moved);
            Assert.Equal(1, kept);
            Assert.Equal("one", File.ReadAllText(Path.Combine(target, "JoyPro2-report-1.zip")));
            Assert.Equal("notes", File.ReadAllText(Path.Combine(target, "sub", "notes.txt")));
            Assert.Equal("new two", File.ReadAllText(Path.Combine(target, "JoyPro2-report-2.zip")));
            Assert.Equal("old two", File.ReadAllText(Path.Combine(old, "JoyPro2-report-2.zip")));
            Assert.False(Directory.Exists(Path.Combine(old, "sub")));

            // Everything moved: the old folder goes.
            File.Delete(Path.Combine(target, "JoyPro2-report-2.zip"));
            Assert.Equal((1, 0), ProblemReporter.MigrateFolder(old, target));
            Assert.False(Directory.Exists(old));
            Assert.Equal((0, 0), ProblemReporter.MigrateFolder(old, target));
            Assert.EndsWith(Path.Combine("JoyPro2", "Problem Reports"), ProblemReporter.DefaultFolder);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void FirstExportQuestion_ImportFirstIsThePrimaryButton()
    {
        AvaloniaRenderThread.Run(() =>
        {
            ConfirmRequest request = new("First export to DCS World", "Import first?", "Export anyway", false)
            {
                SecondaryLabel = "Import first", DontAskKey = MainWindowViewModel.ConfirmFirstExportKey, SecondaryIsPrimary = true
            };
            ConfirmWindow window = new(request);
            Button confirm = window.FindControl<Button>("ConfirmButton")!;
            Button secondary = window.FindControl<Button>("SecondaryButton")!;
            WrapPanel buttons = window.FindControl<WrapPanel>("ConfirmButtons")!;
            Assert.True(secondary.IsDefault);
            Assert.Contains("accent", secondary.Classes);
            Assert.False(confirm.IsDefault);
            Assert.DoesNotContain("accent", confirm.Classes);
            Assert.Equal(0, buttons.Children.IndexOf(secondary));

            // Other three-answer questions keep the action as the primary button.
            ConfirmWindow plain = new(new ConfirmRequest("Restore", "Export first?", "Restore", false) { SecondaryLabel = "Export first" });
            Assert.True(plain.FindControl<Button>("ConfirmButton")!.IsDefault);
            Assert.DoesNotContain("accent", plain.FindControl<Button>("SecondaryButton")!.Classes);
        });
    }

    [Fact]
    public async Task FirstExportQuestion_AsksWithImportFirstAsPrimary()
    {
        // The question as the view model asks it (ExportDefaultsTests covers when it is asked and what each answer does).
        MainWindowViewModel vm = new(new GripOnBaseEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false);
        ConfirmRequest? asked = null;
        vm.ConfirmHandler = r =>
        {
            asked = r;
            return Task.FromResult(false);
        };
        ConfirmChoice answer = await vm.ConfirmChoiceAsync("First export", "Import first?", "Export anyway", "Import first", MainWindowViewModel.ConfirmFirstExportKey, secondaryIsPrimary: true);
        Assert.Equal(ConfirmChoice.Cancel, answer);
        Assert.True(asked?.SecondaryIsPrimary);
    }
}
