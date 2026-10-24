using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using JoyPro.DeviceReference;
using JoyPro.Gui.Models;
using JoyPro.Gui.Services;
using JoyPro.Gui.ViewModels;
using JoyPro.Gui.Views;
using JoyPro.Gui.Views.Controls;
using SkiaSharp;
using Xunit;

namespace JoyPro.Gui.Tests;

// The Layout page's placing by drag and "Now place: X" (marker assistant), "Share layout..." with the page of several
// views (round trip), and the 2D layouts (DB\DeviceLayouts2D: JoyPro 1's sheets, maogowzeg's community sheets with his
// credit, button maps from the user's JoyPro 1 folder). Everything the user would keep goes to the tests' own
// JOYPRO_DATA_DIR. JOYPRO_V230_OUT names a folder for the renders (1280x800 and 2267x1250, light and dark; the 2D layout
// renders use the real engine of TestEngine.Configuration).
[SupportedOSPlatform("windows")]
public sealed class LayoutPlacementShareVariantTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "JoyPro2.Gui.Tests", "v230-" + Guid.NewGuid().ToString("N"));
    private readonly string? previousData = Environment.GetEnvironmentVariable("JOYPRO_DATA_DIR");

    public LayoutPlacementShareVariantTests()
    {
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", Path.Combine(temp, "data"));
        DeviceReferenceService.CatalogFolderOverride = () => DeviceReferenceTests.ShippedCatalog;
        DeviceReferenceService.Reload();
        DeviceImageCache.InternetPicturesEnabled = false;
        LayoutVariantStore.ExtraPictureFolders.Clear();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JOYPRO_DATA_DIR", previousData);
        DeviceReferenceService.CatalogFolderOverride = () => null;
        DeviceReferenceService.Reload();
        LayoutVariantStore.ExtraPictureFolders.Clear();
        try
        {
            Directory.Delete(temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private const string Box = "Home Made Box {11111111-0000-0000-0000-000000000001}";

    private static DeviceReferenceViewModel UnknownBox(out MarkerAssistantViewModel assistant)
    {
        DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false, CaptureService = new QuietCapture() };
        AvaloniaRenderThread.Wait(Done(vm.ShowDeviceAsync(new DeviceProbe { ProductName = "Home Made Box", InstanceName = Box, Buttons = 4, Hats = 1, Axes = ["JOY_X"] }, Box)));
        vm.OpenAssistantCommand.Execute(null);
        assistant = vm.Assistant!;
        return vm;
    }

    // ------------------------------------------------------------------ drag to place, "Now place: X"

    [Fact]
    public void Assistant_NowPlace_Chips_ClickAndDropPlace_LaterAsksAgain_UndoTakesEveryPlacementBack()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = UnknownBox(out MarkerAssistantViewModel assistant);
            List<string> undoNotes = [];
            assistant.BeforePlacement = undoNotes.Add;
            // A picture without positions: every input is a chip, the first is asked for.
            int withInputs = vm.Markers.Count(m => m.Inputs.Any(i => i.Input.Length > 0));
            Assert.True(withInputs >= 3);
            Assert.Equal(withInputs, assistant.UnplacedParts.Count);
            Assert.True(assistant.HasNextToPlace);
            ReferenceMarker first = assistant.NextToPlace!;
            Assert.Equal(assistant.UnplacedParts[0], first);
            Assert.StartsWith("Now place: " + first.Name, assistant.NowPlaceText);

            // Later: asked again after the others.
            assistant.SkipNextCommand.Execute(null);
            Assert.NotEqual(first, assistant.NextToPlace);
            Assert.Equal(first, assistant.UnplacedParts[^1]);

            // A click on the picture places the asked one.
            ReferenceMarker second = assistant.NextToPlace!;
            Assert.True(vm.PlaceAssistantPart(vm.PictureWidth * 0.25, vm.PictureHeight * 0.5));
            Assert.True(second.IsShown);
            Assert.Equal(0.25, second.X, 3);
            Assert.DoesNotContain(second, assistant.UnplacedParts);
            Assert.Equal(1, assistant.PlacedCount);

            // A chip dropped on the picture (picture pixels): its part goes there.
            Assert.True(vm.DropAssistantPart(first.Id, vm.PictureWidth * 0.75, vm.PictureHeight * 0.2));
            Assert.True(first.IsShown);
            Assert.Equal(0.75, first.X, 3);
            Assert.Equal(0.2, first.Y, 3);
            Assert.Equal(withInputs - 2, assistant.UnplacedParts.Count);
            Assert.Equal(2, undoNotes.Count);

            // Undo: the dropped part has no place again (back among the chips), then the clicked one.
            assistant.UndoCommand.Execute(null);
            Assert.False(first.IsShown);
            Assert.Contains(first, assistant.UnplacedParts);
            assistant.UndoCommand.Execute(null);
            Assert.False(second.IsShown);
            Assert.Equal(withInputs, assistant.UnplacedParts.Count);
            Assert.Equal(0, assistant.PlacedCount);

            // The rarely used tools sit behind More.
            Assert.False(assistant.ShowMoreTools);
            assistant.ShowMoreTools = true;
            Assert.True(assistant.ShowMoreTools);
        });
    }

    // The pressed input waits for its click: the click places it, not the "Now place" part; the prompt names it.
    [Fact]
    public void Assistant_PressedInput_TakesTheClick_AndIsNamedInNowPlace()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = UnknownBox(out MarkerAssistantViewModel assistant);
            ReferenceMarker next = assistant.NextToPlace!;
            assistant.Found("JOY_BTN3");
            ReferenceMarker pressed = vm.MarkerOf("JOY_BTN3")!;
            Assert.Equal("Place", assistant.Step);
            Assert.Equal(pressed, assistant.NowPlacing);
            Assert.Contains(pressed.Name, assistant.NowPlaceText);
            Assert.True(vm.PlaceAssistantPart(vm.PictureWidth * 0.5, vm.PictureHeight * 0.5));
            Assert.True(pressed.IsShown);
            if (!ReferenceEquals(next, pressed))
                Assert.False(next.IsShown);
            assistant.StopCommand.Execute(null);
        });
    }

    // The canvas turns a dropped chip into the picture point of the drop.
    [Fact]
    public void Canvas_DropOfAPartChip_RunsTheCommandWithThePicturePoint()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DevicePartDrop? dropped = null;
            DeviceMarkerCanvas canvas = new()
            {
                SourceWidth = 1000,
                SourceHeight = 500,
                PartDroppedCommand = new RelayCommand<DevicePartDrop>(d => dropped = d),
                Width = 1000,
                Height = 500
            };
            canvas.Measure(new Size(1000, 500));
            canvas.Arrange(new Rect(0, 0, 1000, 500));
            Point view = canvas.ToViewPoint(new Point(400, 200));
            Assert.True(canvas.DropPartAt(view, "panel.button.3"));
            Assert.NotNull(dropped);
            Assert.Equal("panel.button.3", dropped!.PartId);
            Assert.Equal(400, dropped.X, 0);
            Assert.Equal(200, dropped.Y, 0);
            // Outside the picture nothing happens.
            dropped = null;
            Assert.False(canvas.DropPartAt(canvas.ToViewPoint(new Point(1500, 200)), "panel.button.3"));
            Assert.Null(dropped);
        });
    }

    // On the Layout page every placement of the assistant is undone with the page's Undo too.
    [Fact]
    public void LayoutPage_DroppedChip_IsUndoneWithThePagesUndo()
    {
        string work = Path.Combine(temp, "layout");
        Directory.CreateDirectory(work);
        AvaloniaRenderThread.Run(() =>
        {
            MainWindowViewModel vm = AvaloniaRenderThread.Wait(AssistantPageAsync(work));
            MarkerAssistantViewModel assistant = vm.DeviceReference.Assistant!;
            ReferenceMarker part = assistant.UnplacedParts[1];
            vm.LayoutPartDroppedCommand.Execute(new DevicePartDrop(part.Id, vm.DeviceReference.PictureWidth / 3, vm.DeviceReference.PictureHeight / 3));
            Assert.True(part.IsShown);
            Assert.True(vm.CanUndoLayoutEdit);
            vm.UndoLayoutEditCommand.Execute(null);
            Assert.False(part.IsShown);
            Assert.Contains(part, assistant.UnplacedParts);
            vm.RedoLayoutEditCommand.Execute(null);
            Assert.True(part.IsShown);
            vm.StopGameConfigWatcher();
        });
    }

    private static async Task<MainWindowViewModel> AssistantPageAsync(string work)
    {
        LayoutRedesignRenderTests.LayoutFakeEngine fake = new(StandInPicture(work), work);
        MainWindowViewModel vm = new(fake, new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
        vm.CurrentStage = UiStage.Layout;
        vm.IsCheatSheetsSection = false;
        await vm.ShowWorkspaceDeviceAsync("Home Cockpit Box {11111111-0000-0000-0000-000000000001}");
        await LayoutRedesignRenderTests.WaitForAsync(() => vm.HasReferenceModel);
        vm.OpenReferenceAssistantCommand.Execute(null);
        return vm;
    }

    private static string StandInPicture(string work)
    {
        string file = Path.Combine(work, "stand-in.png");
        using RenderTargetBitmap bitmap = new(new PixelSize(400, 300));
        bitmap.Save(file);
        return file;
    }

    // ------------------------------------------------------------------ Share layout with the page of several views

    [Fact]
    public void ShareLayout_WithThePageOfSeveralViews_RoundTrip()
    {
        AvaloniaRenderThread.Run(() =>
        {
            const string model = "winwing-f18-takeoff-panel";
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync(model)));
            Assert.Equal(model, vm.Model?.Id);

            // A page: the drawing's sheet (positions, a table and a line) and an image of the user's.
            CalloutSheet callouts = CalloutSheet.FromJsonObject((Dictionary<string, object>)MiniJson.Parse(
                "{\"version\":1,\"width\":2056,\"height\":1656,\"picture\":[514,203,1000,1300],\"rowHeight\":31,\"mainWidth\":264,\"modWidth\":171,\"modifier\":\"\",\"otherY\":-1," +
                "\"tables\":[{\"id\":\"panel.toggle.3\",\"title\":\"Launch bar\",\"parts\":[\"panel.toggle.3\"],\"side\":\"right\",\"x\":1524,\"y\":545,\"width\":465,\"modifier\":false," +
                "\"spot\":[0.585,0.282],\"rows\":[{\"input\":\"JOY_BTN3\",\"label\":\"Up\",\"icon\":\"button\"}]}]," +
                "\"lines\":[{\"table\":\"panel.toggle.3\",\"points\":[[1099,568],[1505,568],[1505,580],[1524,580]]}]}"));
            DeviceLayoutSet drawing = new() { Model = model, PhotoKind = "drawing", Markers = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase) { ["panel.toggle.3"] = [0.353, 0.358] }, Callouts = callouts };
            string sheet = DeviceReferenceService.SaveCallouts(drawing, "schematic");
            string imageFile = Path.Combine(temp, "front.png");
            using (RenderTargetBitmap bitmap = new(new PixelSize(64, 48)))
                bitmap.Save(imageFile);
            string kept = MultiViewStore.KeepImage(model, imageFile);
            MultiViewLayout page = new() { Model = model, Page = "16x9" };
            page.Items.Add(new MultiViewItem { Sheet = sheet, PictureKey = "schematic", Title = "Drawing", X = 100, Y = 120, Scale = 0.75, Width = 2056, Height = 1656 });
            page.Items.Add(new MultiViewItem { Image = kept, Title = "Front", X = 2000, Y = 300, Scale = 4, Width = 64, Height = 48 });
            MultiViewStore.Save(page);

            string shared = Path.Combine(temp, "shared.layout.json");
            vm.ExportLayout(shared);
            Assert.Contains("several views", vm.Status);
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(shared)))
            {
                JsonElement multiview = json.RootElement.GetProperty("multiview");
                Assert.Equal("16x9", multiview.GetProperty("page").GetString());
                Assert.Equal(2, multiview.GetProperty("views").GetArrayLength());
                Assert.True(multiview.GetProperty("views")[0].TryGetProperty("layout", out JsonElement layout));
                Assert.True(layout.TryGetProperty("callouts", out _));
                Assert.False(layout.GetProperty("photo").TryGetProperty("local", out _), "photo.local is never shared");
                Assert.True(multiview.GetProperty("views")[1].TryGetProperty("image", out _));
            }

            // Another PC: nothing of the page there; the import brings the page, the sheet and the image back.
            Directory.Delete(MultiViewStore.Folder(), recursive: true);
            Directory.Delete(DeviceReferenceService.CalloutFolder(), recursive: true);
            Assert.Null(MultiViewStore.Load(model));
            Assert.True(AvaloniaRenderThread.Wait(vm.ImportLayoutAsync(shared)));
            Assert.Contains("2 views", vm.Status);
            MultiViewLayout? back = MultiViewStore.Load(model);
            Assert.NotNull(back);
            Assert.Equal("16x9", back!.Page);
            Assert.Equal(2, back.Items.Count);
            MultiViewItem view = back.Items[0];
            Assert.Equal(100, view.X);
            Assert.Equal(120, view.Y);
            Assert.Equal(0.75, view.Scale);
            Assert.True(File.Exists(view.Sheet));
            DeviceLayoutSet sheetBack = DeviceLayoutSet.Parse(File.ReadAllText(view.Sheet), view.Sheet);
            Assert.NotNull(sheetBack.Callouts);
            Assert.Equal("Launch bar", sheetBack.Callouts!.Tables.Single().Title);
            MultiViewItem image = back.Items[1];
            Assert.True(File.Exists(image.Image));
            Assert.Equal(File.ReadAllBytes(imageFile), File.ReadAllBytes(image.Image));
            Assert.Equal(4, image.Scale);
        });
    }

    // A layout without a page is shared as before (no "multiview").
    [Fact]
    public void ShareLayout_WithoutAPage_HasNoMultiview()
    {
        AvaloniaRenderThread.Run(() =>
        {
            DeviceReferenceViewModel vm = new() { VendorPhotosEnabled = false };
            AvaloniaRenderThread.Wait(Done(vm.ShowModelAsync("winwing-f18-takeoff-panel")));
            string shared = Path.Combine(temp, "plain.layout.json");
            vm.ExportLayout(shared);
            Assert.DoesNotContain("\"multiview\"", File.ReadAllText(shared));
            Assert.Equal(0, LayoutShare.ImportMultiView(File.ReadAllText(shared), "winwing-f18-takeoff-panel"));
        });
    }

    // ------------------------------------------------------------------ 2D layouts

    private static string VariantFolder => LayoutVariantDb.FolderOf(DeviceReferenceTests.ShippedCatalog);

    // A plain picture of a size (stands in for a sheet; the tests never need the real ones).
    private static string SyntheticPicture(string folder, string name, int width, int height, byte shade = 200)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, name);
        using SKBitmap bitmap = new(width, height);
        bitmap.Erase(new SKColor(shade, shade, shade));
        using SKData data = bitmap.Encode(name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(file, data.ToArray());
        return file;
    }

    private static LayoutVariant SyntheticVariant(string id, string model, int width = 200, int height = 100, string credit = "")
    {
        LayoutVariant variant = new() { Id = id, Title = "Sheet " + id, Models = [model], Width = width, Height = height, Font = "Arial", FontSize = 9, Credit = credit };
        variant.Positions["JOY_BTN1"] = [10, 10];
        variant.Positions["MOD+JOY_BTN1"] = [110, 10];
        return variant;
    }

    // The shipped 2D layouts: text positions only (no picture ships: the sheets hold the makers' photos and renders),
    // every model known, every position on the picture, maogowzeg's credit, every device of the list covered.
    [Fact]
    public void ShippedVariants_AreValid_FitTheirModels_AndCoverTheDevices()
    {
        List<LayoutVariant> variants = LayoutVariantDb.Load(VariantFolder);
        Assert.Equal(Directory.GetFiles(VariantFolder, "*.json").Length, variants.Count);
        Assert.Equal(variants.Count, variants.Select(v => v.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        DeviceCatalog catalog = DeviceReferenceService.Catalog;
        foreach (LayoutVariant variant in variants)
        {
            Assert.NotEmpty(variant.Models);
            foreach (string model in variant.Models)
                Assert.True(catalog.Find(model) != null, variant.Id + ": unknown model " + model);
            Assert.True(variant.Width > 0 && variant.Height > 0, variant.Id);
            Assert.NotEmpty(variant.Positions);
            Assert.All(variant.Positions, p => Assert.True(p.Value[0] >= 0 && p.Value[1] >= 0 && p.Value[0] < variant.Width && p.Value[1] < variant.Height, variant.Id + " " + p.Key));
            // Found on the user's PC by name and content only.
            Assert.False(variant.IsShipped, variant.Id + " names a shipped picture");
            Assert.True(variant.LocalPictureName.Length > 0 && variant.LocalPictureSha256.Length == 64, variant.Id);
            Assert.All(variant.LocalPictureAlternatives, a => Assert.True(a.Key.Length > 0 && a.Value.Length == 64, variant.Id + " alternative " + a.Key));
        }

        // The community sheets: maogowzeg's credit, every position an input of the device.
        List<LayoutVariant> community = variants.Where(v => v.Id.StartsWith("maogowzeg-", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, community.Count);
        foreach (LayoutVariant variant in community)
        {
            Assert.Equal("maogowzeg (Discord)", variant.Credit);
            foreach (string model in variant.Models)
            {
                RefDevice device = catalog.Find(model)!;
                HashSet<string> inputs = new(device.AllControls.Concat(catalog.BaseOf(device)?.AllControls ?? []).Select(c => c.Input), StringComparer.OrdinalIgnoreCase);
                inputs.Add("Game");
                inputs.Add("Plane");
                if (variant.Models.Count == 1)
                    Assert.All(variant.Positions.Keys, k => Assert.True(inputs.Contains(k), variant.Id + ": " + k + " is no input of " + model));
            }
        }

        // The devices of the task: each has a 2D layout.
        string[] covered =
        [
            "vkb-gunfighter-mcg-pro", "vkb-gladiator-nxt", "vkb-t-rudder-mk4", "thrustmaster-mfd-cougar-left", "thrustmaster-mfd-cougar-right", "mfg-crosswind-v2",
            "thrustmaster-twcs-throttle", "virpil-alpha-l", "virpil-alpha-r", "virpil-mongoost-50cm-throttle", "virpil-mongoost-50cm3-throttle",
            "virpil-control-panel-1", "virpil-control-panel-2", "thrustmaster-fa18c-hornet-grip", "thrustmaster-hotas-warthog-throttle",
            "winwing-viperace-ex-joystick-grip", "winwing-strikeace-ex-throttle-grip-r", "winwing-strikeace-throttle-grip-l", "winwing-f18-takeoff-panel-2"
        ];
        Assert.All(covered, model => Assert.True(LayoutVariantDb.For(variants, model).Count > 0, model + " has no 2D layout"));

        // The credit is in the notices.
        DirectoryInfo? root = new(DeviceReferenceTests.ShippedCatalog);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "THIRD_PARTY_NOTICES.md")))
            root = root.Parent;
        Assert.Contains("maogowzeg", File.ReadAllText(Path.Combine(root!.FullName, "THIRD_PARTY_NOTICES.md")));
    }

    // No picture ships in DB\DeviceLayouts2D, and the release does not let one through.
    [Fact]
    public void ShippedVariants_NoPictureFileShips()
    {
        string[] pictures = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".layout", ".xcf"];
        Assert.Empty(Directory.GetFiles(VariantFolder, "*", SearchOption.AllDirectories).Where(f => pictures.Contains(Path.GetExtension(f).ToLowerInvariant())));
        DirectoryInfo? root = new(DeviceReferenceTests.ShippedCatalog);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "tools", "ReleaseContent.psm1")))
            root = root.Parent;
        string release = File.ReadAllText(Path.Combine(root!.FullName, "tools", "ReleaseContent.psm1"));
        string pictureFolders = release.Split('\n').Single(l => l.TrimStart().StartsWith("$script:PictureDbFolders", StringComparison.Ordinal));
        Assert.DoesNotContain("DeviceLayouts2D", pictureFolders);
    }

    [Fact]
    public void Variants_OfferedOnlyWithTheirPicture_CommunityFolder_TakeOverFillsTheModifierColumn_ThePickIsKept()
    {
        string folder = Path.Combine(temp, "variants");
        Directory.CreateDirectory(folder);
        // A sheet JoyPro would own: its picture next to the JSON.
        LayoutVariant owned = SyntheticVariant("test-owned", "winwing-viperace-ex-joystick-grip");
        owned.Picture = Path.GetFileName(SyntheticPicture(folder, "test-owned.png", 200, 100));
        File.WriteAllText(Path.Combine(folder, "test-owned.json"), owned.ToJson());
        // A JoyPro 1 button map: its picture in the user's JoyPro 1 folder (found by name and content).
        string jp1 = Path.Combine(temp, "JoyPro", "LayoutImages");
        string jp1Picture = SyntheticPicture(jp1, "Box_Template.png", 200, 100);
        LayoutVariant local = SyntheticVariant("test-box", "vkb-gunfighter-mcg-pro");
        local.LocalPictureName = "Box_Template.png";
        local.LocalPictureSha256 = DeviceLayoutSet.HashFile(jp1Picture);
        File.WriteAllText(Path.Combine(folder, "test-box.json"), local.ToJson());
        // A community sheet: its picture in <data>\Layouts\Community once the user received it (as it was sent, too).
        string sheets = Path.Combine(temp, "received");
        string received = SyntheticPicture(sheets, "community-sheet.jpg", 300, 150, 120);
        string original = SyntheticPicture(sheets, "community-sheet-original.png", 300, 150, 90);
        LayoutVariant shared = SyntheticVariant("test-community", "winwing-f18-takeoff-panel-2", 300, 150, "someone (Discord)");
        shared.LocalPictureName = "community-sheet.jpg";
        shared.LocalPictureSha256 = DeviceLayoutSet.HashFile(received);
        shared.LocalPictureAlternatives.Add(new KeyValuePair<string, string>("community-sheet-original.png", DeviceLayoutSet.HashFile(original)));
        File.WriteAllText(Path.Combine(folder, "test-community.json"), shared.ToJson());
        Assert.Contains("localPictureAlternatives", File.ReadAllText(Path.Combine(folder, "test-community.json")));

        Func<List<LayoutVariant>> previous = LayoutVariantStore.Shipped;
        try
        {
            LayoutVariantStore.Shipped = () => LayoutVariantDb.Load(folder);
            Assert.Equal("2D layout from JoyPro 1 (modifier column)", LayoutVariantStore.TitleOf(Assert.Single(LayoutVariantStore.For("winwing-viperace-ex-joystick-grip"))));
            // Without their pictures nothing is offered.
            Assert.Empty(LayoutVariantStore.For("vkb-gunfighter-mcg-pro"));
            Assert.Empty(LayoutVariantStore.For("winwing-f18-takeoff-panel-2"));

            // The received sheet put into the Community folder: offered at once (also as the original file).
            string community = DeviceCatalog.CommunityLayoutFolder();
            Assert.StartsWith(Path.Combine(temp, "data"), community, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(community);
            File.Copy(original, Path.Combine(community, "community-sheet-original.png"));
            LayoutVariant offered = Assert.Single(LayoutVariantStore.For("winwing-f18-takeoff-panel-2"));
            Assert.Equal("2D layout by someone (Discord) (modifier column)", LayoutVariantStore.TitleOf(offered));
            Assert.EndsWith("community-sheet-original.png", offered.ResolvePicture(LayoutVariantStore.LocalPictureFolders()));
            File.Copy(received, Path.Combine(community, "community-sheet.jpg"));
            Assert.EndsWith("community-sheet.jpg", offered.ResolvePicture(LayoutVariantStore.LocalPictureFolders()));
            // Another file of the same name is not the sheet.
            File.Delete(Path.Combine(community, "community-sheet.jpg"));
            File.Delete(Path.Combine(community, "community-sheet-original.png"));
            SyntheticPicture(community, "community-sheet.jpg", 300, 150, 10);
            Assert.Empty(LayoutVariantStore.For("winwing-f18-takeoff-panel-2"));

            LayoutVariantStore.ExtraPictureFolders.Add(jp1);
            LayoutVariant found = Assert.Single(LayoutVariantStore.For("vkb-gunfighter-mcg-pro"));
            Assert.Equal("2D layout from your JoyPro 1 folder (modifier column)", LayoutVariantStore.TitleOf(found));

            // Taken over: the picture copied into the user's data, the modifier column for his modifier.
            string file = LayoutVariantStore.TakeOver(found, "Lpinky");
            Assert.True(LayoutVariantStore.IsVariantCopy(file));
            LayoutVariant copy = LayoutVariant.Parse(File.ReadAllText(file), file);
            Assert.True(Path.IsPathRooted(copy.Picture) && File.Exists(copy.Picture));
            Assert.StartsWith(LayoutVariantStore.Folder(), copy.Picture, StringComparison.OrdinalIgnoreCase);
            Assert.True(copy.Positions.ContainsKey("Lpinky+JOY_BTN1"));
            Assert.False(copy.Positions.ContainsKey("MOD+JOY_BTN1"));
            // Without a modifier the column stays empty.
            LayoutVariant plain = LayoutVariant.Parse(File.ReadAllText(LayoutVariantStore.TakeOver(found, string.Empty)), file);
            Assert.Equal(["JOY_BTN1"], plain.Positions.Keys);

            // The pick per device (without its GUID) is kept, and forgotten again.
            const string device = "VKB-Sim Gunfighter Modern Combat Pro Twist  {B052B4D0-35D1-11ec-8011-444553540000}";
            LayoutVariantStore.SetChoice(device, new LayoutVariantStore.Choice { Variant = found.Id, Layout = file, Previous = "C:\\old.layout" });
            Assert.Equal("test-box", LayoutVariantStore.ChoiceOf("VKB-Sim Gunfighter Modern Combat Pro Twist {00000000-0000-0000-0000-000000000000}")?.Variant);
            LayoutVariantStore.UpdateLayout(device, file);
            Assert.Equal("C:\\old.layout", LayoutVariantStore.ClearChoice(device)?.Previous);
            Assert.Null(LayoutVariantStore.ChoiceOf(device));
        }
        finally
        {
            LayoutVariantStore.Shipped = previous;
        }
    }

    // The engine reads a 2D layout like a layout file: its picture (found in <data>\Layouts\Community), text style,
    // positions and the maker's credit; without the picture it says so.
    [Fact]
    public void Engine_ReadsA2DLayoutAsLayoutFile()
    {
        string? engine = EngineExe();
        if (engine == null)
            return;
        string engineData = Path.Combine(temp, "engine-data");
        string folder = Path.Combine(temp, "engine-variants");
        Directory.CreateDirectory(folder);
        string picture = SyntheticPicture(Path.Combine(temp, "received-engine"), "sheet.png", 640, 480);
        LayoutVariant variant = SyntheticVariant("engine-sheet", "winwing-viperace-ex-joystick-grip", 640, 480, "someone (Discord)");
        variant.LocalPictureName = "sheet.png";
        variant.LocalPictureSha256 = DeviceLayoutSet.HashFile(picture);
        variant.Positions["JOY_BTN2"] = [10, 40];
        string json = Path.Combine(folder, "engine-sheet.json");
        File.WriteAllText(json, variant.ToJson());

        Assert.Contains("is not on this PC", RunEngine(engine, ["layout", "template-inspect", "--source", json]));

        string community = Path.Combine(engineData, "Layouts", "Community");
        Directory.CreateDirectory(community);
        File.Copy(picture, Path.Combine(community, "sheet.png"));
        using JsonDocument found = JsonDocument.Parse(RunEngine(engine, ["layout", "template-inspect", "--source", json]));
        Assert.Empty(found.RootElement.GetProperty("Errors").EnumerateArray());
        Assert.Equal(640, found.RootElement.GetProperty("Width").GetInt32());
        Assert.Equal("someone (Discord)", found.RootElement.GetProperty("Credit").GetString());
        Assert.Equal("Arial", found.RootElement.GetProperty("FontName").GetString());
        Assert.Equal(3, found.RootElement.GetProperty("MarkerCount").GetInt32());
    }

    internal static string? EngineExe()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "bin")))
            dir = dir.Parent;
        string? engine = dir == null ? null : Path.Combine(dir.FullName, "engine", "bin", "x64", TestEngine.Configuration, "engine.exe");
        return engine != null && File.Exists(engine) ? engine : null;
    }

    private string RunEngine(string engine, string[] args)
    {
        System.Diagnostics.ProcessStartInfo start = new(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["JOYPRO_DATA_DIR"] = Path.Combine(temp, "engine-data");
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        System.Threading.Tasks.Task<string> errors = process.StandardError.ReadToEndAsync();
        // A failed command says why on stderr (no JSON then).
        string text = process.StandardOutput.ReadToEnd() + errors.Result;
        process.WaitForExit();
        int brace = text.IndexOf('{');
        return brace >= 0 ? text[brace..] : text;
    }

    // The Layout page's picture strip lists the model's 2D layouts whose picture is here; the Community folder hint.
    [Fact]
    public void LayoutPage_PictureStrip_ListsTheModels2DLayouts()
    {
        string folder = Path.Combine(temp, "strip-variants");
        Directory.CreateDirectory(folder);
        LayoutVariant owned = SyntheticVariant("strip-owned", "vkb-gunfighter-mcg-pro");
        owned.Picture = Path.GetFileName(SyntheticPicture(folder, "strip-owned.png", 200, 100));
        File.WriteAllText(Path.Combine(folder, "strip-owned.json"), owned.ToJson());
        string sheet = SyntheticPicture(DeviceCatalog.CommunityLayoutFolder(), "strip-community.png", 200, 100);
        LayoutVariant shared = SyntheticVariant("strip-community", "winwing-viperace-ex-joystick-grip", credit: "maogowzeg (Discord)");
        shared.LocalPictureName = "strip-community.png";
        shared.LocalPictureSha256 = DeviceLayoutSet.HashFile(sheet);
        File.WriteAllText(Path.Combine(folder, "strip-community.json"), shared.ToJson());
        LayoutVariant absent = SyntheticVariant("strip-absent", "winwing-f18-takeoff-panel-2");
        absent.LocalPictureName = "not-here.png";
        absent.LocalPictureSha256 = new string('0', 64);
        File.WriteAllText(Path.Combine(folder, "strip-absent.json"), absent.ToJson());
        Func<List<LayoutVariant>> previous = LayoutVariantStore.Shipped;
        try
        {
            LayoutVariantStore.Shipped = () => LayoutVariantDb.Load(folder);
            AvaloniaRenderThread.Run(() =>
            {
                MainWindowViewModel vm = new(new NoviceGuiTests.NoviceFakeEngine(), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = typeof(MainWindowViewModel).Assembly.Location };
                Assert.Contains(DeviceCatalog.CommunityLayoutFolder(), vm.CommunityLayoutHint);
                AvaloniaRenderThread.Wait(Done(vm.DeviceReference.ShowModelAsync("vkb-gunfighter-mcg-pro")));
                AvaloniaRenderThread.Pump();
                WorkspacePictureChoice jp1 = Assert.Single(vm.WorkspacePictures, p => p.Variant != null);
                Assert.Equal("2D layout from JoyPro 1 (modifier column)", jp1.Title);
                Assert.Contains("written into its boxes", jp1.Tip);
                AvaloniaRenderThread.Wait(Done(vm.DeviceReference.ShowModelAsync("winwing-viperace-ex-joystick-grip")));
                AvaloniaRenderThread.Pump();
                WorkspacePictureChoice community = Assert.Single(vm.WorkspacePictures, p => p.Variant != null);
                Assert.Equal("2D layout by maogowzeg (Discord) (modifier column)", community.Title);
                Assert.Contains("maogowzeg", community.Tip);
                // A 2D layout whose picture is not here is not offered.
                AvaloniaRenderThread.Wait(Done(vm.DeviceReference.ShowModelAsync("winwing-f18-takeoff-panel-2")));
                AvaloniaRenderThread.Pump();
                Assert.DoesNotContain(vm.WorkspacePictures, p => p.Variant != null);
            });
        }
        finally
        {
            LayoutVariantStore.Shipped = previous;
        }
    }

    // ------------------------------------------------------------------ renders (looked at by hand)

    // JOYPRO_V230_OUT: the assistant with its chips and More; with JOYPRO_V230_COMMUNITY (a folder with the received
    // community sheets) also the 2D layouts found there, shown with the real engine.
    [Fact]
    public void Render_AssistantChipsAnd2DLayouts()
    {
        string? outDir = Environment.GetEnvironmentVariable("JOYPRO_V230_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(temp, "render");
        Directory.CreateDirectory(work);
        string? engine = EngineExe();
        string? sheets = Environment.GetEnvironmentVariable("JOYPRO_V230_COMMUNITY");
        if (!string.IsNullOrWhiteSpace(sheets) && Directory.Exists(sheets))
        {
            Directory.CreateDirectory(DeviceCatalog.CommunityLayoutFolder());
            foreach (string sheet in Directory.GetFiles(sheets))
                File.Copy(sheet, Path.Combine(DeviceCatalog.CommunityLayoutFolder(), Path.GetFileName(sheet)), overwrite: true);
        }
        AvaloniaRenderThread.Run(() =>
        {
            try
            {
                foreach ((string theme, ThemeVariant variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach ((int width, int height) in new[] { (1280, 800), (2267, 1250) })
                    {
                        // The marker assistant: Now place, the chips, More open in the second picture.
                        MainWindowViewModel vm = AvaloniaRenderThread.Wait(AssistantPageAsync(work));
                        using (HiddenWindow main = NavPagesTests.Show(vm, variant, width, height))
                        {
                            main.Settle();
                            NavPagesTests.Save(main, Path.Combine(outDir, "assistant-chips-" + width + "-" + theme + ".png"));
                            vm.DeviceReference.Assistant!.ShowMoreTools = true;
                            main.Settle();
                            NavPagesTests.Save(main, Path.Combine(outDir, "assistant-more-" + width + "-" + theme + ".png"));
                        }
                        vm.StopGameConfigWatcher();

                        if (engine == null)
                            continue;
                        // A 2D layout with the real engine when its sheet is in the Community folder (maogowzeg's ViperAce EX).
                        const string device = "WINWING Orion Joystick Base 2 + JGRIP-F16 {22222222-0000-0000-0000-000000000002}";
                        DeviceReferenceService.SetChoice(device, "winwing-viperace-ex-joystick-grip");
                        MainWindowViewModel layout = new(new DataDirEngineClient(Path.Combine(temp, "data")), new AxisTuningFakeCapture(), loadPersistedUiSettings: false) { EnginePath = engine };
                        layout.CurrentStage = UiStage.Layout;
                        layout.IsCheatSheetsSection = false;
                        AvaloniaRenderThread.Wait(layout.ShowWorkspaceDeviceAsync(device));
                        AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => layout.HasReferenceModel && !layout.IsBusy));
                        if (layout.WorkspacePictures.FirstOrDefault(p => p.Variant != null) is { } choice)
                        {
                            AvaloniaRenderThread.Wait(layout.UseLayoutVariantAsync(choice.Variant!));
                            AvaloniaRenderThread.Wait(LayoutRedesignRenderTests.WaitForAsync(() => !layout.IsBusy && layout.LayoutEditorPreviewImage != null));
                        }
                        using (HiddenWindow main = NavPagesTests.Show(layout, variant, width, height))
                        {
                            main.Settle();
                            NavPagesTests.Save(main, Path.Combine(outDir, "variant-community-" + width + "-" + theme + ".png"));
                            // "Use your own picture" open: the Community folder hint.
                            if (Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(main.Window, "CommunityLayoutHint") is { } hint &&
                                Avalonia.LogicalTree.LogicalExtensions.FindLogicalAncestorOfType<Avalonia.Controls.Expander>(hint) is { } expander)
                            {
                                expander.IsExpanded = true;
                                main.Settle();
                                NavPagesTests.Save(main, Path.Combine(outDir, "community-hint-" + width + "-" + theme + ".png"));
                            }
                        }
                        layout.StopGameConfigWatcher();
                    }
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            }
        });
    }

    private sealed class QuietCapture : IJoystickCaptureService
    {
        public string LastDiagnostics => string.Empty;

        public Task<JoystickCaptureService.CaptureResult?> CaptureNextAsync(JoystickCaptureService.CaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<JoystickCaptureService.CaptureResult?>(null);

        public Task MonitorAsync(Action<JoystickInputEvent> onEvent, Action<JoystickAxisSnapshot>? onAxes, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
